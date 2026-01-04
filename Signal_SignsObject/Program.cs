using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

[BepInPlugin("com.mitschpielen.peaksigns", "Peak Signs", "1.0.0")]
public class PeakSigns : BaseUnityPlugin
{
    // -------- Config --------
    private ConfigEntry<int> _mouseButton;
    private ConfigEntry<float> _maxDistance;

    private ConfigEntry<float> _deleteHoldSeconds;
    private ConfigEntry<float> _placeHoldSeconds;

    // Farben (UI Progress)
    private readonly Color _deleteColor = new Color(1f, 0.35f, 0.10f, 1f);
    private readonly Color _placeColor  = new Color(0.10f, 1.0f, 0.20f, 1f);

    // -------- Hold-State --------
    private bool _isHolding;
    private float _holdStart;
    private bool _placedThisHold;

    // Target fürs Löschen
    private GameObject _deleteTarget;

    // -------- Signs (lokal + MP) --------
    private readonly Dictionary<int, GameObject> _signsById = new Dictionary<int, GameObject>();

    // -------- Progress UI (UseItem Clone) --------
    private GameObject _progressRoot;
    private Image _progressFill;
    private bool _progressReady;

    // -------- Photon Events --------
    private const byte EVT_SPAWN_SIGN  = 101;
    private const byte EVT_DELETE_SIGN = 202;

    // -------- Recolor Timer --------
    private float _nextRecolorAt;
    private const float RECOLOR_INTERVAL = 10f;

    // -------- Stable ID --------
    private int _localCounter = 0;

    // -------- Cached Reflection Handles --------
    private static object _cachedCustomization;          // Singleton<Customization>.Instance
    private static Type _ccType;                         // CharacterCustomization type
    private static MethodInfo _ccGetDataMethod;          // customization-data method
    private static bool _ccSearched;

    private void Awake()
    {
        _mouseButton = Config.Bind("Input", "MouseButton", 2, "2 = Middle Mouse (Mausrad-Klick)");
        _maxDistance = Config.Bind("Placement", "MaxDistance", 60f, "Maximale Distanz.");

        // Zeiten halbiert (schneller)
        _deleteHoldSeconds = Config.Bind("Delete", "HoldSeconds", 0.5f, "Hold-Zeit zum Löschen (halbiert).");
        _placeHoldSeconds  = Config.Bind("Placement", "HoldSeconds", 0.0125f, "Hold-Zeit zum Platzieren (halbiert).");

        _nextRecolorAt = Time.unscaledTime + RECOLOR_INTERVAL;

        Logger.LogInfo("Peak Signs 1.0.0 geladen. MP Sync + Owner-Farbe + Wand parallel zur Oberfläche + Recolor alle 10s.");
    }

    private void OnEnable()
    {
        if (PhotonNetwork.NetworkingClient != null)
            PhotonNetwork.NetworkingClient.EventReceived += OnPhotonEvent;
    }

    private void OnDisable()
    {
        if (PhotonNetwork.NetworkingClient != null)
            PhotonNetwork.NetworkingClient.EventReceived -= OnPhotonEvent;
    }

    private void Start()
    {
        TrySetupUseItemProgress();
    }

    private void Update()
    {
        if (!_progressReady)
            TrySetupUseItemProgress();

        // ---- Alle 10 Sekunden Farben nachfärben ----
        if (Time.unscaledTime >= _nextRecolorAt)
        {
            _nextRecolorAt = Time.unscaledTime + RECOLOR_INTERVAL;
            RefreshAllSignsColors();
        }

        int btn = _mouseButton.Value;

        if (Input.GetMouseButtonDown(btn))
        {
            _isHolding = true;
            _holdStart = Time.unscaledTime;
            _placedThisHold = false;
            _deleteTarget = GetSignUnderCrosshair();
        }

        if (_isHolding && Input.GetMouseButton(btn))
        {
            float held = Time.unscaledTime - _holdStart;

            if (!_placedThisHold)
                _deleteTarget = GetSignUnderCrosshair();

            // FALL A: Auf Schild -> Löschen-Progress
            if (_deleteTarget != null)
            {
                float tDelete = Mathf.Clamp01(held / Mathf.Max(0.01f, _deleteHoldSeconds.Value));
                ShowProgress(true, _deleteColor);
                SetProgress(tDelete);

                if (held >= _deleteHoldSeconds.Value)
                {
                    int id = FindIdForSignObject(_deleteTarget);
                    if (id != 0)
                        RequestDelete(id);

                    _deleteTarget = null;
                    _isHolding = false;
                    ShowProgress(false, _deleteColor);
                }
                return;
            }

            // FALL B: Platzieren
            float tPlace = Mathf.Clamp01(held / Mathf.Max(0.01f, _placeHoldSeconds.Value));
            ShowProgress(true, _placeColor);
            SetProgress(tPlace);

            if (!_placedThisHold && held >= _placeHoldSeconds.Value)
            {
                if (TryGetPlacement(out Vector3 pos, out Quaternion rot, out Vector3 normal, out bool isWall))
                {
                    int id = GenerateSignIdStable();
                    RequestSpawn(id, pos, rot, normal, isWall);
                    _placedThisHold = true;
                }
                ShowProgress(false, _placeColor);
            }
        }

        if (_isHolding && Input.GetMouseButtonUp(btn))
        {
            _isHolding = false;
            _deleteTarget = null;
            ShowProgress(false, _deleteColor);
            ShowProgress(false, _placeColor);
        }
    }

    // =========================
    // Multiplayer Requests (RoomCache)
    // =========================

    private void RequestSpawn(int id, Vector3 pos, Quaternion rot, Vector3 normal, bool isWall)
    {
        int ownerActor = (PhotonNetwork.IsConnected ? PhotonNetwork.LocalPlayer.ActorNumber : 0);

        SpawnLocal(id, ownerActor, pos, rot, normal, isWall);

        if (IsInMultiplayerRoom())
        {
            object[] data = new object[]
            {
                id,
                pos.x, pos.y, pos.z,
                rot.x, rot.y, rot.z, rot.w,
                normal.x, normal.y, normal.z,
                isWall ? 1 : 0
            };

            var opts = new RaiseEventOptions
            {
                Receivers = ReceiverGroup.Others,
                CachingOption = EventCaching.AddToRoomCache
            };

            PhotonNetwork.RaiseEvent(EVT_SPAWN_SIGN, data, opts, SendOptions.SendReliable);
        }
    }

    private void RequestDelete(int id)
    {
        DeleteLocal(id);

        if (IsInMultiplayerRoom())
        {
            object[] data = new object[] { id };

            var opts = new RaiseEventOptions
            {
                Receivers = ReceiverGroup.Others,
                // Hinweis: AddToRoomCache lässt "Delete" für Late-Joiner mitlaufen,
                // aber löscht nicht automatisch alte Spawn-Events aus dem Cache.
                // Für viele Mods reicht das trotzdem, weil Delete danach verarbeitet wird.
                CachingOption = EventCaching.AddToRoomCache
            };

            PhotonNetwork.RaiseEvent(EVT_DELETE_SIGN, data, opts, SendOptions.SendReliable);
        }
    }

    private void OnPhotonEvent(EventData photonEvent)
    {
        if (photonEvent == null) return;

        if (photonEvent.Code == EVT_SPAWN_SIGN)
        {
            object[] data = photonEvent.CustomData as object[];
            if (data == null || data.Length < 12) return;

            int id = (int)data[0];

            Vector3 pos = new Vector3(
                Convert.ToSingle(data[1]),
                Convert.ToSingle(data[2]),
                Convert.ToSingle(data[3])
            );

            Quaternion rot = new Quaternion(
                Convert.ToSingle(data[4]),
                Convert.ToSingle(data[5]),
                Convert.ToSingle(data[6]),
                Convert.ToSingle(data[7])
            );

            Vector3 normal = new Vector3(
                Convert.ToSingle(data[8]),
                Convert.ToSingle(data[9]),
                Convert.ToSingle(data[10])
            );

            bool isWall = Convert.ToInt32(data[11]) == 1;

            int ownerActor = photonEvent.Sender;

            SpawnLocal(id, ownerActor, pos, rot, normal, isWall);
        }
        else if (photonEvent.Code == EVT_DELETE_SIGN)
        {
            object[] data = photonEvent.CustomData as object[];
            if (data == null || data.Length < 1) return;

            int id = (int)data[0];
            DeleteLocal(id);
        }
    }

    private bool IsInMultiplayerRoom()
    {
        return PhotonNetwork.IsConnected && PhotonNetwork.InRoom;
    }

    // =========================
    // Spawn/Delete Local
    // =========================

    private void SpawnLocal(int id, int ownerActor, Vector3 pos, Quaternion rot, Vector3 normal, bool isWall)
    {
        if (_signsById.ContainsKey(id))
            return;

        Color ownerColor = GetOwnerPlayerColor(ownerActor);

        GameObject sign = CreateSign(pos, rot, normal, isWall, id, ownerActor, ownerColor);
        _signsById[id] = sign;

        Logger.LogInfo($"Schild gespawnt id={id} ownerActor={ownerActor} isWall={isWall} pos={pos}");
    }

    private void DeleteLocal(int id)
    {
        if (!_signsById.TryGetValue(id, out GameObject sign) || sign == null)
            return;

        _signsById.Remove(id);
        Destroy(sign);

        Logger.LogInfo($"Schild gelöscht id={id}");
    }

    private int FindIdForSignObject(GameObject signObj)
    {
        PeakSignMarker marker = signObj.GetComponent<PeakSignMarker>();
        if (marker == null) marker = signObj.GetComponentInParent<PeakSignMarker>();
        GameObject root = marker != null ? marker.gameObject : signObj;

        foreach (var kv in _signsById)
            if (kv.Value == root) return kv.Key;

        return 0;
    }

    private int GenerateSignIdStable()
    {
        int actor = (PhotonNetwork.IsConnected ? PhotonNetwork.LocalPlayer.ActorNumber : 0);
        _localCounter++;
        int id = (actor << 20) ^ (_localCounter & 0xFFFFF);
        if (id == 0) id = 1;
        return id;
    }

    // =========================
    // Placement (Boden + Wand)
    // =========================

    private bool TryGetPlacement(out Vector3 placePos, out Quaternion rot, out Vector3 surfaceNormal, out bool isWall)
    {
        placePos = Vector3.zero;
        rot = Quaternion.identity;
        surfaceNormal = Vector3.up;
        isWall = false;

        Camera cam = Camera.main;
        if (cam == null) return false;

        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        if (!Physics.Raycast(ray, out RaycastHit hit, _maxDistance.Value, ~0, QueryTriggerInteraction.Ignore))
            return false;

        surfaceNormal = hit.normal.normalized;
        isWall = surfaceNormal.y < 0.6f;

        // raus aus Oberfläche
        placePos = hit.point + surfaceNormal * 0.06f;

        if (!isWall)
        {
            Vector3 fwdFlat = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (fwdFlat.sqrMagnitude < 0.0001f) fwdFlat = Vector3.forward;
            rot = Quaternion.LookRotation(fwdFlat.normalized, Vector3.up);
        }
        else
        {
            // rot wird für Wand nicht zwingend gebraucht (wir richten im CreateSign sauber aus),
            // aber lassen es drin, falls du später rot mitschicken willst.
            Vector3 fwd = surfaceNormal;
            Vector3 up = Vector3.up;
            if (Vector3.Cross(up, fwd).sqrMagnitude < 0.0001f)
                up = cam.transform.up;

            rot = Quaternion.LookRotation(fwd, up);
        }

        return true;
    }

    private GameObject GetSignUnderCrosshair()
    {
        Camera cam = Camera.main;
        if (cam == null) return null;

        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        if (Physics.Raycast(ray, out RaycastHit hit, _maxDistance.Value, ~0, QueryTriggerInteraction.Collide))
        {
            PeakSignMarker marker = hit.collider != null ? hit.collider.GetComponentInParent<PeakSignMarker>() : null;
            if (marker != null) return marker.gameObject;
        }
        return null;
    }

    // =========================
    // Customization Color (Reflection)
    // =========================

    private static object GetCustomizationInstanceReflect()
    {
        if (_cachedCustomization != null) return _cachedCustomization;

        try
        {
            Type singletonOpen = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                singletonOpen = asm.GetType("Zorro.Core.Singleton`1", false);
                if (singletonOpen != null) break;
            }
            if (singletonOpen == null) return null;

            Type singletonClosed = singletonOpen.MakeGenericType(typeof(Customization));
            var prop = singletonClosed.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (prop == null) return null;

            _cachedCustomization = prop.GetValue(null, null);
            return _cachedCustomization;
        }
        catch { return null; }
    }

    private static void EnsureCharacterCustomizationMethod()
    {
        if (_ccSearched) return;
        _ccSearched = true;

        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("CharacterCustomization", false);
                if (t != null) { _ccType = t; break; }
            }
            if (_ccType == null) return;

            MethodInfo best = null;
            var methods = _ccType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                if (ps == null || ps.Length != 1) continue;

                var p0 = ps[0].ParameterType;
                if (!p0.IsAssignableFrom(typeof(Photon.Realtime.Player)) && !typeof(Photon.Realtime.Player).IsAssignableFrom(p0))
                    continue;

                var rt = m.ReturnType;
                if (rt == typeof(void)) continue;

                bool hasCurrentSkin =
                    rt.GetField("currentSkin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null
                    || rt.GetProperty("currentSkin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null;

                if (hasCurrentSkin || rt.Name.IndexOf("Customization", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    best = m;
                    if (hasCurrentSkin) break;
                }
            }
            _ccGetDataMethod = best;
        }
        catch
        {
            _ccType = null;
            _ccGetDataMethod = null;
        }
    }

    private static int GetCurrentSkinIndexForPlayer(Photon.Realtime.Player ownerPlayer)
    {
        try
        {
            EnsureCharacterCustomizationMethod();
            if (_ccType == null || _ccGetDataMethod == null) return -1;

            object instance = null;
            if (!_ccGetDataMethod.IsStatic)
            {
                if (typeof(UnityEngine.Object).IsAssignableFrom(_ccType))
                {
                    instance = UnityEngine.Object.FindAnyObjectByType(_ccType);
                    if (instance == null) return -1;
                }
                else return -1;
            }

            object dataObj = _ccGetDataMethod.Invoke(instance, new object[] { ownerPlayer });
            if (dataObj == null) return -1;

            object curSkinObj = GetMemberValue(dataObj, "currentSkin");
            if (curSkinObj == null) return -1;

            return Convert.ToInt32(curSkinObj);
        }
        catch { return -1; }
    }

    private static Color GetOwnerPlayerColor(int ownerActor)
    {
        Color fallback = new Color(1f, 0.95f, 0.2f, 1f);

        try
        {
            if (!PhotonNetwork.IsConnected || PhotonNetwork.CurrentRoom == null)
                return fallback;

            Photon.Realtime.Player ownerPlayer = PhotonNetwork.CurrentRoom.GetPlayer(ownerActor);
            if (ownerPlayer == null) return fallback;

            object customizationObj = GetCustomizationInstanceReflect();
            if (customizationObj == null) return fallback;

            Array skinsArray = GetMemberValue(customizationObj, "skins") as Array;
            if (skinsArray == null || skinsArray.Length == 0) return fallback;

            int skinIndex = GetCurrentSkinIndexForPlayer(ownerPlayer);
            if (skinIndex < 0) return fallback;

            skinIndex = Mathf.Clamp(skinIndex, 0, skinsArray.Length - 1);
            object skinObj = skinsArray.GetValue(skinIndex);
            if (skinObj == null) return fallback;

            object colorObj = GetMemberValue(skinObj, "color");
            if (colorObj is Color c) { c.a = 1f; return c; }

            return fallback;
        }
        catch { return fallback; }
    }

    private static string GetOwnerPlayerName(int ownerActor)
    {
        try
        {
            if (!PhotonNetwork.IsConnected || PhotonNetwork.CurrentRoom == null)
                return "Player";

            Photon.Realtime.Player p = PhotonNetwork.CurrentRoom.GetPlayer(ownerActor);
            if (p == null || string.IsNullOrWhiteSpace(p.NickName))
                return "Player";

            return p.NickName;
        }
        catch
        {
            return "Player";
        }
    }

    private static void AdjustTextSizeForName(TextMesh textMesh, string name)
    {
        // Legacy heuristic kept for compatibility; CreateSign now uses FitTextMeshToPanelBounds.
        if (textMesh == null) return;

        if (string.IsNullOrEmpty(name))
        {
            textMesh.characterSize = 0.08f;
            return;
        }

        int len = name.Length;

        const int maxComfortableChars = 10;  // looks good at base size
        const int hardMaxChars = 25;         // very long names

        float baseSize = 0.08f;
        float minSize = 0.035f;

        if (len <= maxComfortableChars)
        {
            textMesh.characterSize = baseSize;
            return;
        }

        float t = Mathf.InverseLerp(maxComfortableChars, hardMaxChars, len);
        float scaled = Mathf.Lerp(baseSize, minSize, t);
        textMesh.characterSize = scaled;
    }

    private static void FitTextMeshToPanelBounds(TextMesh textMesh, float maxWidth, float maxHeight, float baseCharacterSize, float minCharacterSize)
    {
        if (textMesh == null) return;

        var mr = textMesh.GetComponent<MeshRenderer>();
        if (mr == null) return;

        // Ensure renderer is on so bounds are valid.
        mr.enabled = true;

        string original = textMesh.text ?? string.Empty;
        if (original.Length == 0)
        {
            textMesh.characterSize = baseCharacterSize;
            return;
        }

        // 1) Shrink to fit (fast, robust).
        textMesh.characterSize = baseCharacterSize;

        // Avoid infinite loops if bounds are weird.
        for (int i = 0; i < 20; i++)
        {
            Bounds b = mr.bounds;
            float w = b.size.x;
            float h = b.size.y;

            // If bounds are degenerate, bail.
            if (w <= 0.0001f || h <= 0.0001f)
                break;

            if (w <= maxWidth && h <= maxHeight)
                return;

            // Scale down proportionally by worst overflow.
            float scaleW = maxWidth / w;
            float scaleH = maxHeight / h;
            float scale = Mathf.Min(scaleW, scaleH);

            // In case we barely overflow, nudge a bit.
            scale = Mathf.Clamp(scale, 0.75f, 0.98f);
            float next = textMesh.characterSize * scale;

            if (next >= textMesh.characterSize - 0.00001f)
                next = textMesh.characterSize - 0.002f;

            textMesh.characterSize = Mathf.Max(minCharacterSize, next);

            if (textMesh.characterSize <= minCharacterSize + 0.00001f)
                break;
        }

        // 2) Still not fitting at min size -> ellipsis to fit width.
        for (int cut = original.Length; cut >= 1; cut--)
        {
            Bounds b = mr.bounds;
            if (b.size.x <= maxWidth && b.size.y <= maxHeight)
                return;

            // Keep at least 1 char + …
            int keep = Mathf.Max(1, cut - 1);
            textMesh.text = original.Substring(0, keep) + "…";
        }

        // If nothing fits (shouldn't happen), keep a single dot.
        textMesh.text = "…";
    }

    private static object GetMemberValue(object obj, string name)
    {
        if (obj == null) return null;
        Type t = obj.GetType();

        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null) return f.GetValue(obj);

        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null) return p.GetValue(obj, null);

        return null;
    }

    private void RefreshAllSignsColors()
    {
        if (GetCustomizationInstanceReflect() == null) return;

        foreach (var kv in _signsById)
        {
            GameObject root = kv.Value;
            if (root == null) continue;

            PeakSignMarker marker = root.GetComponent<PeakSignMarker>();
            if (marker == null) continue;

            Color ownerColor = GetOwnerPlayerColor(marker.OwnerActor);

            Transform panelT = root.transform.Find("Panel");
            if (panelT != null)
                SetRendererColor(panelT.GetComponent<MeshRenderer>(), ownerColor);

            Transform poleT = root.transform.Find("Pole");
            if (poleT != null)
                SetRendererColor(poleT.GetComponent<MeshRenderer>(), Color.Lerp(ownerColor, Color.black, 0.45f));
        }
    }

    private static void SetRendererColor(MeshRenderer mr, Color c)
    {
        if (mr == null) return;

        Material m = mr.material;
        if (m == null)
        {
            Shader s0 = Shader.Find("Unlit/Color");
            if (s0 == null) s0 = Shader.Find("Standard");
            m = new Material(s0);
            mr.material = m;
        }

        Shader s = Shader.Find("Unlit/Color");
        if (s != null && mr.material.shader != s)
            mr.material.shader = s;

        if (mr.material.HasProperty("_Color"))
            mr.material.color = c;

        mr.enabled = true;
    }

    // =========================
    // Progress UI (UseItem Clone)
    // =========================

    private void TrySetupUseItemProgress()
    {
        if (_progressReady) return;

        GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
        GameObject useItem = null;

        for (int i = 0; i < all.Length; i++)
        {
            GameObject go = all[i];
            if (go == null) continue;
            if (!string.Equals(go.name, "UseItem", StringComparison.OrdinalIgnoreCase)) continue;

            if (go.transform.root != null &&
                go.transform.root.name.IndexOf("GAME", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                useItem = go;
                break;
            }
            if (useItem == null) useItem = go;
        }

        if (useItem == null) return;

        Image[] imgs = useItem.GetComponentsInChildren<Image>(true);
        Image best = null;

        for (int i = 0; i < imgs.Length; i++)
        {
            Image img = imgs[i];
            if (img == null) continue;
            if (img.type != Image.Type.Filled) continue;

            bool radial =
                img.fillMethod == Image.FillMethod.Radial360 ||
                img.fillMethod == Image.FillMethod.Radial180 ||
                img.fillMethod == Image.FillMethod.Radial90;

            if (!radial) continue;

            best = img;
            break;
        }

        if (best == null) return;

        Transform root = best.transform;
        for (int up = 0; up < 3 && root.parent != null; up++)
        {
            if (root.parent.gameObject == useItem) break;
            root = root.parent;
        }

        if (_progressRoot != null)
        {
            Destroy(_progressRoot);
            _progressRoot = null;
            _progressFill = null;
        }

        _progressRoot = Instantiate(root.gameObject, root.parent);
        _progressRoot.name = "PeakSigns_Progress(Clone)";
        _progressRoot.SetActive(true);

        StripAnimators(_progressRoot);
        EnsureCanvasGroups(_progressRoot);

        _progressFill = FindFillInClone(_progressRoot, best.name, best.fillMethod);

        SetProgress(0f);
        ShowProgress(false, _deleteColor);

        _progressReady = (_progressFill != null);
    }

    private static void StripAnimators(GameObject root)
    {
        var animators = root.GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < animators.Length; i++)
            if (animators[i] != null) Destroy(animators[i]);

        var animations = root.GetComponentsInChildren<Animation>(true);
        for (int i = 0; i < animations.Length; i++)
            if (animations[i] != null) Destroy(animations[i]);
    }

    private static void EnsureCanvasGroups(GameObject root)
    {
        CanvasGroup[] groups = root.GetComponentsInChildren<CanvasGroup>(true);
        if (groups == null || groups.Length == 0)
            root.AddComponent<CanvasGroup>();
    }

    private static Image FindFillInClone(GameObject cloneRoot, string wantName, Image.FillMethod wantMethod)
    {
        Image[] imgs = cloneRoot.GetComponentsInChildren<Image>(true);

        for (int i = 0; i < imgs.Length; i++)
        {
            Image im = imgs[i];
            if (im == null) continue;
            if (im.type != Image.Type.Filled) continue;
            if (im.name == wantName && im.fillMethod == wantMethod)
                return im;
        }

        for (int i = 0; i < imgs.Length; i++)
        {
            Image im = imgs[i];
            if (im != null && im.type == Image.Type.Filled)
                return im;
        }

        return null;
    }

    private void ShowProgress(bool visible, Color color)
    {
        if (_progressRoot == null) return;

        _progressRoot.transform.SetAsLastSibling();

        if (_progressFill != null)
            _progressFill.color = color;

        CanvasGroup[] groups = _progressRoot.GetComponentsInChildren<CanvasGroup>(true);
        if (groups != null && groups.Length > 0)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                CanvasGroup g = groups[i];
                if (g == null) continue;
                g.alpha = visible ? 1f : 0f;
                g.interactable = false;
                g.blocksRaycasts = false;
            }
        }
        else
        {
            _progressRoot.SetActive(visible);
        }

        RectTransform rt = _progressRoot.GetComponent<RectTransform>();
        if (rt != null)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(180f, 0f);
            rt.localScale = new Vector3(0.7f, 0.7f, 0.7f);
        }
    }

    private void SetProgress(float t)
    {
        if (_progressFill == null) return;
        _progressFill.type = Image.Type.Filled;
        _progressFill.fillAmount = Mathf.Clamp01(t);
    }

    // =========================
    // Arrow Mesh
    // =========================

    private static Mesh CreateArrowMesh(float width, float height, float thickness, float tipLength)
    {
        float w = width;
        float h = height;
        float t = thickness;
        float tip = Mathf.Clamp(tipLength, 0.05f, w * 0.49f);

        // 2D Pfeil (rechts) im XY
        Vector2[] p =
        {
            new Vector2(-w * 0.5f, -h * 0.5f),
            new Vector2( w * 0.5f - tip, -h * 0.5f),
            new Vector2( w * 0.5f, 0f),
            new Vector2( w * 0.5f - tip,  h * 0.5f),
            new Vector2(-w * 0.5f,  h * 0.5f),
        };

        int n = p.Length;
        var verts = new Vector3[n * 2];

        float zF = +t * 0.5f;
        float zB = -t * 0.5f;

        for (int i = 0; i < n; i++)
        {
            verts[i] = new Vector3(p[i].x, p[i].y, zF);
            verts[i + n] = new Vector3(p[i].x, p[i].y, zB);
        }

        var tris = new List<int>(64);

        // Front
        tris.AddRange(new[] { 0, 1, 2,  0, 2, 3,  0, 3, 4 });

        // Back
        tris.AddRange(new[] { n + 0, n + 2, n + 1,  n + 0, n + 3, n + 2,  n + 0, n + 4, n + 3 });

        // Sides (doppelseitig)
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;

            int f0 = i;
            int f1 = next;
            int b0 = i + n;
            int b1 = next + n;

            tris.Add(f0); tris.Add(f1); tris.Add(b1);
            tris.Add(f0); tris.Add(b1); tris.Add(b0);

            tris.Add(b1); tris.Add(f1); tris.Add(f0);
            tris.Add(b0); tris.Add(b1); tris.Add(f0);
        }

        var mesh = new Mesh();
        mesh.name = "ArrowSignMesh";
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // =========================
    // Sign Visual (Boden + Wand)
    // =========================

    private GameObject CreateSign(Vector3 placePos, Quaternion rot, Vector3 surfaceNormal, bool isWall, int signId, int ownerActor, Color ownerColor)
    {
        GameObject root = new GameObject("PeakSign");
        var marker = root.AddComponent<PeakSignMarker>();
        marker.SignId = signId;
        marker.OwnerActor = ownerActor;

        // Panel
        GameObject panel = new GameObject("Panel");
        panel.transform.SetParent(root.transform, false);

        var mf = panel.AddComponent<MeshFilter>();
        panel.AddComponent<MeshRenderer>();

        float panelWidth = 1.2f;
        float panelHeight = 0.35f;
        float panelThickness = 0.08f;
        float tipLen = 0.35f;
        mf.mesh = CreateArrowMesh(panelWidth, panelHeight, panelThickness, tipLen);

        var mcPanel = panel.AddComponent<MeshCollider>();
        mcPanel.sharedMesh = mf.mesh;
        mcPanel.convex = true;
        mcPanel.isTrigger = true;

        // Pole
        GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "Pole";
        pole.transform.SetParent(root.transform, false);

        float poleRadius = 0.08f;
        float poleHalfHeight = 0.45f;
        float poleLength = poleHalfHeight * 2f;

        float wallGap = 0.03f;

        if (!isWall)
        {
            // Boden
            panel.transform.position = placePos + Vector3.up * 0.85f;
            panel.transform.rotation = rot * Quaternion.Euler(0f, -90f, 0f);

            pole.transform.position = placePos + Vector3.up * 0.45f;
            pole.transform.rotation = rot;
            pole.transform.localScale = new Vector3(poleRadius, poleHalfHeight, poleRadius);
        }
        else
        {
            // ===== WAND / SCHRÄGE FLÄCHE =====
            // Ziel: Panel soll PARALLEL zur Oberfläche sein.
            Vector3 n = surfaceNormal.normalized;

            // Pole: Y-Achse -> Normal
            Quaternion poleRot = Quaternion.FromToRotation(Vector3.up, n);

            // Pole raus aus Wand
            Vector3 poleCenter = placePos + n * (poleLength * 0.5f + wallGap);
            pole.transform.position = poleCenter;
            pole.transform.rotation = poleRot;
            pole.transform.localScale = new Vector3(poleRadius, poleHalfHeight, poleRadius);

            // Pivot (Ende vom Pole)
            Vector3 pivot = placePos + n * (poleLength + wallGap);

            // Panel an Pivot + halbe Dicke (damit es nicht in die Wand clippt)
            panel.transform.position = pivot + n * (panelThickness * 0.5f + wallGap);

            // --- Panel Rotation: parallel zur Oberfläche ---
            // Panel-Mesh liegt im lokalen XY, Dicke ist lokales Z.
            // => panel.forward (Z) muss auf +/- Normal zeigen.
            Vector3 forward = -n; // wenn Vorderseite "falsch" ist: forward = n;

            // Arrow-Richtung (lokal +X) soll sinnvoll liegen: nimm Blickrichtung projiziert auf die Fläche
            Camera cam = Camera.main;
            Vector3 dirOnPlane = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, n) : Vector3.zero;
            if (dirOnPlane.sqrMagnitude < 0.0001f)
                dirOnPlane = Vector3.Cross(Vector3.up, n);
            if (dirOnPlane.sqrMagnitude < 0.0001f)
                dirOnPlane = Vector3.Cross(Vector3.forward, n);
            dirOnPlane.Normalize();

            // Up so bauen, dass panel.right = dirOnPlane (damit Pfeil "in die Richtung" zeigt)
            Vector3 up = Vector3.Cross(forward, dirOnPlane).normalized;
            if (up.sqrMagnitude < 0.0001f)
                up = Vector3.ProjectOnPlane(Vector3.up, forward).normalized;

            panel.transform.rotation = Quaternion.LookRotation(forward, up);

            // Falls dein Pfeil auf der Fläche 90° verdreht ist, nutze genau eine Korrektur:
            // panel.transform.rotation *= Quaternion.AngleAxis(90f, forward);
            // oder:
            // panel.transform.rotation *= Quaternion.AngleAxis(-90f, forward);
        }

        MakeNoHitbox(panel);
        MakeNoHitbox(pole);

        ApplyUnlit(panel, ownerColor);
        ApplyUnlit(pole, Color.Lerp(ownerColor, Color.black, 0.45f));

        // --- Player name label (world-space UI, depth-tested, both sides) ---
        try
        {
            string playerName = GetOwnerPlayerName(ownerActor);
            Material uiMat = CreateDepthTestedUiMaterial();

            CreateNameLabelWorldUI(panel.transform, playerName, panelWidth, panelHeight, tipLen, panelThickness, isBackSide: false, uiMat: uiMat);
            CreateNameLabelWorldUI(panel.transform, playerName, panelWidth, panelHeight, tipLen, panelThickness, isBackSide: true, uiMat: uiMat);
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Failed to create sign label: {e}");
        }

        return root;
    }

    private static void MakeNoHitbox(GameObject go)
    {
        Collider c = go.GetComponent<Collider>();
        if (c != null) c.isTrigger = true;
        go.layer = 2;
    }

    private static void ApplyUnlit(GameObject go, Color c)
    {
        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;

        Shader s = Shader.Find("Unlit/Color");
        if (s == null) s = Shader.Find("Standard");

        Material m = new Material(s);
        if (m.HasProperty("_Color")) m.color = c;
        m.renderQueue = 3000;

        mr.material = m;
        mr.enabled = true;
        mr.shadowCastingMode = ShadowCastingMode.On;
        mr.receiveShadows = true;
    }

    private static Font GetBuiltinArialFont()
    {
        // Built-in resource name in Unity
        return Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    private static Material CreateDepthTestedUiMaterial()
    {
        // UI/Default is the standard for Unity UI Text and supports alpha.
        Shader s = Shader.Find("UI/Default");
        if (s == null)
            s = Shader.Find("Sprites/Default");
        if (s == null)
            return null;

        var m = new Material(s);

        // Force depth test/write when supported by the shader.
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f);
        if (m.HasProperty("_ZTest")) m.SetFloat("_ZTest", (float)CompareFunction.LessEqual);

        // Keep in transparent range but depth-tested.
        m.renderQueue = 3000;
        return m;
    }

    private static void CreateNameLabelWorldUI(
        Transform panel,
        string text,
        float panelWidth,
        float panelHeight,
        float tipLen,
        float panelThickness,
        bool isBackSide,
        Material uiMat)
    {
        // Build a small world-space canvas stuck to the panel face.
        var canvasGo = new GameObject(isBackSide ? "NameCanvasBack" : "NameCanvasFront");
        canvasGo.transform.SetParent(panel, false);

        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        // Make it deterministic and lightweight
        canvasGo.AddComponent<CanvasScaler>();
        canvasGo.AddComponent<GraphicRaycaster>();

        // Place it on the face.
        float z = (panelThickness * 0.5f) + 0.01f;
        if (isBackSide)
        {
            canvasGo.transform.localPosition = new Vector3(-tipLen * 0.25f, 0f, -z);
            // Rotate canvas to face outward from the back.
            canvasGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        }
        else
        {
            canvasGo.transform.localPosition = new Vector3(-tipLen * 0.25f, 0f, +z);
            canvasGo.transform.localRotation = Quaternion.identity;
        }

        // Rect size (in UI units). We'll map 100 units = 1 world unit for easy math.
        const float unitsPerWorld = 100f;

        float padX = 0.12f;
        float padY = 0.06f;
        float usableWidthWorld = Mathf.Max(0.05f, panelWidth - tipLen - padX);
        float usableHeightWorld = Mathf.Max(0.05f, panelHeight - padY);

        var rt = canvas.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(usableWidthWorld * unitsPerWorld, usableHeightWorld * unitsPerWorld);
        rt.localScale = Vector3.one / unitsPerWorld;

        // Child Text
        var textGo = new GameObject("Text");
        textGo.transform.SetParent(canvasGo.transform, false);

        var textRt = textGo.AddComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;

        var uiText = textGo.AddComponent<Text>();
        uiText.text = text;
        uiText.color = Color.black;
        uiText.alignment = TextAnchor.MiddleCenter;
        uiText.horizontalOverflow = HorizontalWrapMode.Overflow;
        uiText.verticalOverflow = VerticalWrapMode.Overflow;

        // Counteract mirroring that can happen with WorldSpace canvases on the back side.
        // Keep the canvas facing the back, but flip the text content so glyphs read normally.
        if (isBackSide)
            textGo.transform.localScale = new Vector3(-1f, 1f, 1f);

        uiText.font = GetBuiltinArialFont();
        uiText.fontStyle = FontStyle.Bold;

        // BestFit shrinks to keep within rect
        uiText.resizeTextForBestFit = true;
        uiText.resizeTextMinSize = 8;
        uiText.resizeTextMaxSize = 48;

        // Apply material if provided (depth-tested)
        if (uiMat != null)
            uiText.material = uiMat;

        // Disable raycast so it doesn't interfere
        uiText.raycastTarget = false;
    }
}

public class PeakSignMarker : MonoBehaviour
{
    public int SignId;
    public int OwnerActor;
}
