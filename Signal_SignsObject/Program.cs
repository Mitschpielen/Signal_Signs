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

[BepInPlugin("com.mitsc.peaksigns", "Peak Signs", "1.0.0")]
public class PeakSigns : BaseUnityPlugin
{
    // -------- Config --------
    private ConfigEntry<int> _mouseButton;
    private ConfigEntry<float> _maxDistance;

    private ConfigEntry<float> _deleteHoldSeconds;
    private ConfigEntry<float> _placeHoldSeconds;

    // Farben (UI Progress)
    private readonly Color _deleteColor = new Color(1f, 0.35f, 0.10f, 1f); // orange/rot
    private readonly Color _placeColor  = new Color(0.10f, 1.0f, 0.20f, 1f); // gruen

    // -------- Hold-State --------
    private bool _isHolding;
    private float _holdStart;
    private bool _placedThisHold;

    // Target fürs Löschen (kann während Hold wechseln)
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

    // -------- Cached Reflection Handles --------
    private static object _cachedCustomization;          // Customization instance (Singleton<Customization>.Instance)
    private static Type _ccType;                         // CharacterCustomization type
    private static MethodInfo _ccGetDataMethod;          // method that returns customization data
    private static bool _ccSearched;

    private void Awake()
    {
        _mouseButton = Config.Bind("Input", "MouseButton", 2, "2 = Middle Mouse (Mausrad-Klick)");
        _maxDistance = Config.Bind("Placement", "MaxDistance", 60f, "Maximale Distanz.");

        _deleteHoldSeconds = Config.Bind("Delete", "HoldSeconds", 1.0f, "Hold-Zeit zum Löschen.");
        _placeHoldSeconds  = Config.Bind("Placement", "HoldSeconds", 0.025f, "Hold-Zeit zum Platzieren.");

        _nextRecolorAt = Time.unscaledTime + RECOLOR_INTERVAL;

        Logger.LogInfo("Peak Signs 1.0.0 geladen. Place+Delete + MP Sync (Photon) + Owner-Farbe + Recolor alle 10s.");
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

            // FALL B: Nicht auf Schild -> Platzieren nach Hold-Zeit mit Progress
            float tPlace = Mathf.Clamp01(held / Mathf.Max(0.01f, _placeHoldSeconds.Value));
            ShowProgress(true, _placeColor);
            SetProgress(tPlace);

            if (!_placedThisHold && held >= _placeHoldSeconds.Value)
            {
                if (TryGetPlacement(out Vector3 pos, out Quaternion rot))
                {
                    int id = GenerateSignId();
                    RequestSpawn(id, pos, rot);
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
    // Multiplayer Requests
    // =========================

    private void RequestSpawn(int id, Vector3 pos, Quaternion rot)
    {
        int ownerActor = (PhotonNetwork.IsConnected ? PhotonNetwork.LocalPlayer.ActorNumber : 0);

        SpawnLocal(id, ownerActor, pos, rot);

        if (IsInMultiplayerRoom())
        {
            object[] data = new object[]
            {
                id,
                pos.x, pos.y, pos.z,
                rot.x, rot.y, rot.z, rot.w
            };

            var opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
            PhotonNetwork.RaiseEvent(EVT_SPAWN_SIGN, data, opts, SendOptions.SendReliable);
        }
    }

    private void RequestDelete(int id)
    {
        DeleteLocal(id);

        if (IsInMultiplayerRoom())
        {
            object[] data = new object[] { id };
            var opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
            PhotonNetwork.RaiseEvent(EVT_DELETE_SIGN, data, opts, SendOptions.SendReliable);
        }
    }

    private void OnPhotonEvent(EventData photonEvent)
    {
        if (photonEvent == null) return;

        if (photonEvent.Code == EVT_SPAWN_SIGN)
        {
            object[] data = photonEvent.CustomData as object[];
            if (data == null || data.Length < 8) return;

            int id = (int)data[0];

            float px = Convert.ToSingle(data[1]);
            float py = Convert.ToSingle(data[2]);
            float pz = Convert.ToSingle(data[3]);

            float qx = Convert.ToSingle(data[4]);
            float qy = Convert.ToSingle(data[5]);
            float qz = Convert.ToSingle(data[6]);
            float qw = Convert.ToSingle(data[7]);

            int ownerActor = photonEvent.Sender;

            SpawnLocal(id, ownerActor, new Vector3(px, py, pz), new Quaternion(qx, qy, qz, qw));
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

    private void SpawnLocal(int id, int ownerActor, Vector3 groundPos, Quaternion rot)
    {
        if (_signsById.ContainsKey(id))
            return;

        Color ownerColor = GetOwnerPlayerColor(ownerActor);

        GameObject sign = CreateSign(groundPos, rot, id, ownerActor, ownerColor);
        _signsById[id] = sign;

        Logger.LogInfo($"Schild gespawnt id={id} ownerActor={ownerActor} pos={groundPos}");
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
        {
            if (kv.Value == root)
                return kv.Key;
        }
        return 0;
    }

    private int GenerateSignId()
    {
        int id;
        do
        {
            id = UnityEngine.Random.Range(100000, int.MaxValue);
        } while (_signsById.ContainsKey(id));
        return id;
    }

    // =========================
    // Owner Color (Skin/Customization via Reflection)
    // =========================

    private static object GetCustomizationInstanceReflect()
    {
        if (_cachedCustomization != null)
            return _cachedCustomization;

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
        catch
        {
            return null;
        }
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
                if (t != null)
                {
                    _ccType = t;
                    break;
                }
            }

            if (_ccType == null)
                return;

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
            if (_ccType == null || _ccGetDataMethod == null)
                return -1;

            object instance = null;
            if (!_ccGetDataMethod.IsStatic)
            {
                // FIX für CS0618: statt FindObjectOfType(Type) -> FindAnyObjectByType(Type)
                if (typeof(UnityEngine.Object).IsAssignableFrom(_ccType))
                {
                    instance = UnityEngine.Object.FindAnyObjectByType(_ccType);
                    if (instance == null) return -1;
                }
                else
                {
                    return -1;
                }
            }

            object dataObj = _ccGetDataMethod.Invoke(instance, new object[] { ownerPlayer });
            if (dataObj == null) return -1;

            object curSkinObj = GetMemberValue(dataObj, "currentSkin");
            if (curSkinObj == null) return -1;

            return Convert.ToInt32(curSkinObj);
        }
        catch
        {
            return -1;
        }
    }

    private static Color GetOwnerPlayerColor(int ownerActor)
    {
        Color fallback = new Color(1f, 0.95f, 0.2f, 1f);

        try
        {
            if (!PhotonNetwork.IsConnected || PhotonNetwork.CurrentRoom == null)
                return fallback;

            Photon.Realtime.Player ownerPlayer = PhotonNetwork.CurrentRoom.GetPlayer(ownerActor);
            if (ownerPlayer == null)
                return fallback;

            object customizationObj = GetCustomizationInstanceReflect();
            if (customizationObj == null)
                return fallback;

            Array skinsArray = GetMemberValue(customizationObj, "skins") as Array;
            if (skinsArray == null || skinsArray.Length == 0)
                return fallback;

            int skinIndex = GetCurrentSkinIndexForPlayer(ownerPlayer);
            if (skinIndex < 0)
                return fallback;

            skinIndex = Mathf.Clamp(skinIndex, 0, skinsArray.Length - 1);
            object skinObj = skinsArray.GetValue(skinIndex);
            if (skinObj == null)
                return fallback;

            object colorObj = GetMemberValue(skinObj, "color");
            if (colorObj is Color c)
            {
                c.a = 1f;
                return c;
            }

            return fallback;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[PeakSigns] GetOwnerPlayerColor failed: " + e);
            return fallback;
        }
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
        if (GetCustomizationInstanceReflect() == null)
            return;

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
    // Placement Raycast (Ping-Style)
    // =========================

    private bool TryGetPlacement(out Vector3 groundPos, out Quaternion rot)
    {
        groundPos = Vector3.zero;
        rot = Quaternion.identity;

        Camera cam = Camera.main;
        if (cam == null) return false;

        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        if (!Physics.Raycast(ray, out RaycastHit hit, _maxDistance.Value, ~0, QueryTriggerInteraction.Ignore))
            return false;

        groundPos = hit.point + Vector3.up * 0.05f;

        Vector3 fwdFlat = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        if (fwdFlat.sqrMagnitude < 0.0001f) fwdFlat = Vector3.forward;

        rot = Quaternion.LookRotation(fwdFlat.normalized, Vector3.up);
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

        Logger.LogInfo("✅ Progress UI bereit (UseItem): root=" + root.name +
                       " | fillName=" + best.name +
                       " | fillMethod=" + best.fillMethod +
                       " | ok=" + _progressReady);
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
    private static Mesh CreateArrowMesh(float width, float height, float thickness, float tipLength)
    {
        float w = width;
        float h = height;
        float t = thickness;
        float tip = Mathf.Clamp(tipLength, 0.05f, w * 0.49f);

        // 2D Pfeil (rechts)
        // links: Rechteck, rechts: Spitze
        Vector2[] p =
        {
            new Vector2(-w * 0.5f, -h * 0.5f),         // 0 hinten-unten
            new Vector2( w * 0.5f - tip, -h * 0.5f),   // 1 vorne-unten (vor Spitze)
            new Vector2( w * 0.5f, 0f),                // 2 Spitze
            new Vector2( w * 0.5f - tip,  h * 0.5f),   // 3 vorne-oben (vor Spitze)
            new Vector2(-w * 0.5f,  h * 0.5f),         // 4 hinten-oben
        };

        // Extrusion entlang Z: vorne (z=+t/2) und hinten (z=-t/2)
        int n = p.Length;
        var verts = new Vector3[n * 2];

        float zF = +t * 0.5f;
        float zB = -t * 0.5f;

        for (int i = 0; i < n; i++)
        {
            verts[i] = new Vector3(p[i].x, p[i].y, zF);       // Front
            verts[i + n] = new Vector3(p[i].x, p[i].y, zB);   // Back
        }

        // Triangulation für Front (Polygon 0-1-2-3-4)
        // Front (CCW)
        var tris = new List<int>();

        // Front face fan um 0: (0,1,2) (0,2,3) (0,3,4)
        tris.AddRange(new[] { 0, 1, 2,  0, 2, 3,  0, 3, 4 });

        // Back face (reverse winding) mit Offset n
        tris.AddRange(new[] { n + 0, n + 2, n + 1,  n + 0, n + 3, n + 2,  n + 0, n + 4, n + 3 });

        // Seitenflächen (doppelseitig, damit du NICHT durchsehen kannst)
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;

            int f0 = i;
            int f1 = next;
            int b0 = i + n;
            int b1 = next + n;

            // Seite A
            tris.Add(f0); tris.Add(f1); tris.Add(b1);
            tris.Add(f0); tris.Add(b1); tris.Add(b0);

            // Seite A reversed (doppelseitig)
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

    private void SetProgress(float t)
    {
        if (_progressFill == null) return;
        _progressFill.type = Image.Type.Filled;
        _progressFill.fillAmount = Mathf.Clamp01(t);
    }

    // =========================
    // Sign Visual + No Hitbox
    // =========================

    private GameObject CreateSign(Vector3 groundPos, Quaternion rot, int signId, int ownerActor, Color ownerColor)
    {
        GameObject root = new GameObject("PeakSign");
        var marker = root.AddComponent<PeakSignMarker>();
        marker.SignId = signId;
        marker.OwnerActor = ownerActor;

        GameObject panel = new GameObject("Panel");
        panel.transform.SetParent(root.transform, false);
        panel.transform.position = groundPos + Vector3.up * 0.9f;
        panel.transform.rotation = rot * Quaternion.Euler(0f, -90f, 0f);

// Pfeil-Mesh (wie dein Bild, nach rechts zeigend)
        var mf = panel.AddComponent<MeshFilter>();
        var mr = panel.AddComponent<MeshRenderer>();

// Breite, Höhe, Dicke, Spitzenlänge
        mf.mesh = CreateArrowMesh(width: 1.2f, height: 0.35f, thickness: 0.08f, tipLength: 0.35f);

// Collider optional (Trigger, blockt nicht)
        var mc = panel.AddComponent<MeshCollider>();
        mc.sharedMesh = mf.mesh;
        mc.convex = true;
        mc.isTrigger = true;

        GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "Pole";
        pole.transform.SetParent(root.transform, false);
        pole.transform.position = groundPos + Vector3.up * 0.45f;
        pole.transform.rotation = rot;
        pole.transform.localScale = new Vector3(0.08f, 0.45f, 0.08f);

        MakeNoHitbox(panel);
        MakeNoHitbox(pole);

        ApplyUnlit(panel, ownerColor);
        ApplyUnlit(pole, Color.Lerp(ownerColor, Color.black, 0.45f));

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
}

public class PeakSignMarker : MonoBehaviour
{
    public int SignId;
    public int OwnerActor;
}
