// PeakSigns.cs - KOMPLETTE DATEI
// Schild: prozedural + (optional) Asset-Variante mit Prefabs aus Bundle: PeakSign01 + PeakPole01 (+ optional PeakSignLabel01)
// Farben: Panel = ownerColor, Pole = dunkler
// Placement: Boden + Wand
// MP Spawn/Delete + RoomCache
// Progress UI Clone (UseItem radial)
//
// ✅ FIX 1: HOLD-LOCK (Delete vs Place) -> entscheidet beim MouseDown einmalig, verhindert Place-Blockade
// ✅ FIX 2: VORLADEN + PREWARM -> AssetBundle/Prefabs laden + unsichtbar instantiieren + Shader vereinfachen
//            => verhindert “Asset kommt erst nach ~6 Sekunden” (typisch Shader/Material warmup)
//
// Controls:
// - MiddleMouse (config) HOLD: Place / Delete
//
// WICHTIG: Im AssetBundle müssen die Prefab-Namen exakt so heißen:
//  - PeakSign01
//  - PeakPole01
// Optional:
//  - PeakSignLabel01 (WorldSpace UI/TMP/Text)

using System;
using System.IO;
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

    // AssetSign Config
    private ConfigEntry<bool> _useAssetSign;
    private ConfigEntry<string> _assetBundleName;

    // Farben (UI Progress)
    private readonly Color _deleteColor = new Color(1f, 0.35f, 0.10f, 1f);
    private readonly Color _placeColor  = new Color(0.10f, 1.0f, 0.20f, 1f);

    // -------- Hold-State --------
    private bool _isHolding;
    private float _holdStart;
    private bool _placedThisHold;

    private GameObject _deleteTarget;
    private bool _holdIsDelete; // ✅ FIX 1

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

    // =========================
    // Asset-Sign (Prefabs in Bundle)
    // =========================
    private const string SIGN_BUNDLE_NAME_DEFAULT = "peaksignsassets";
    private const string ASSET_PANEL_PREFAB = "PeakSign01";
    private const string ASSET_POLE_PREFAB  = "PeakPole01";
    private const string ASSET_LABEL_PREFAB = "PeakSignLabel01"; // optional

    private AssetBundle _signBundle;
    private GameObject _assetPanelPrefab;
    private GameObject _assetPolePrefab;
    private GameObject _assetLabelPrefab;

    // Prewarm flags
    private bool _assetPrewarmed;

    private void Awake()
    {
        _mouseButton = Config.Bind("Input", "MouseButton", 2, "2 = Middle Mouse (Mausrad-Klick)");
        _maxDistance = Config.Bind("Placement", "MaxDistance", 60f, "Maximale Distanz.");

        _deleteHoldSeconds = Config.Bind("Delete", "HoldSeconds", 0.5f, "Hold-Zeit zum Löschen.");
        _placeHoldSeconds  = Config.Bind("Placement", "HoldSeconds", 0.0125f, "Hold-Zeit zum Platzieren.");

        _useAssetSign = Config.Bind("AssetSign", "UseAssetSign", true, "Wenn true: versucht Panel/Pole aus AssetBundle zu laden (PeakSign01 + PeakPole01).");
        _assetBundleName = Config.Bind("AssetSign", "BundleName", SIGN_BUNDLE_NAME_DEFAULT, "AssetBundle Dateiname (ohne Endung).");

        _nextRecolorAt = Time.unscaledTime + RECOLOR_INTERVAL;

        Logger.LogInfo("Peak Signs 1.0.0 geladen (Prozedural + AssetSign optional + AssetLabel optional).");
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

        // ✅ FIX 2: VORLADEN + PREWARM -> verhindert 6s "unsichtbar bis Shader warm"
        if (_useAssetSign.Value)
        {
            TryLoadAssetSignPrefabs();
            PrewarmAssetSign(); // warmup instantiation + shader simplify
        }
    }

    private void Update()
    {
        if (!_progressReady)
            TrySetupUseItemProgress();

        // alle 10s Farben nachfärben
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

            // ✅ FIX 1: Einmalig entscheiden ob Delete oder Place
            _deleteTarget = GetSignUnderCrosshair();
            _holdIsDelete = (_deleteTarget != null);
        }

        if (_isHolding && Input.GetMouseButton(btn))
        {
            float held = Time.unscaledTime - _holdStart;

            // FALL A: Löschen (nur wenn beim Hold-Start ein Schild anvisiert war)
            if (_holdIsDelete && _deleteTarget != null)
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
            _holdIsDelete = false;
            ShowProgress(false, _deleteColor);
            ShowProgress(false, _placeColor);
        }
    }

    // =========================
    // Multiplayer Requests
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

        placePos = hit.point + surfaceNormal * 0.06f;

        if (!isWall)
        {
            Vector3 fwdFlat = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (fwdFlat.sqrMagnitude < 0.0001f) fwdFlat = Vector3.forward;
            rot = Quaternion.LookRotation(fwdFlat.normalized, Vector3.up);
        }
        else
        {
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
    // Create Sign (Asset first, else procedural)
    // =========================
    private GameObject CreateSign(Vector3 placePos, Quaternion rot, Vector3 surfaceNormal, bool isWall, int signId, int ownerActor, Color ownerColor)
    {
        if (_useAssetSign.Value)
        {
            GameObject asset = CreateSignFromAssets(placePos, rot, surfaceNormal, isWall, signId, ownerActor, ownerColor);
            if (asset != null)
                return asset;
        }

        return CreateSignProcedural(placePos, rot, surfaceNormal, isWall, signId, ownerActor, ownerColor);
    }

    // =========================
    // AssetSign Loader
    // =========================
    private void TryLoadAssetSignPrefabs()
    {
        try
        {
            if (_signBundle != null && _assetPanelPrefab != null && _assetPolePrefab != null)
                return;

            string pluginDir = Paths.PluginPath;
            string dllDir = Path.GetDirectoryName(Info.Location) ?? pluginDir;

            string bundleFile = _assetBundleName.Value?.Trim();
            if (string.IsNullOrWhiteSpace(bundleFile))
                bundleFile = SIGN_BUNDLE_NAME_DEFAULT;

            string[] candidates =
            {
                Path.Combine(pluginDir, bundleFile),
                Path.Combine(pluginDir, bundleFile + ".bundle"),
                Path.Combine(pluginDir, bundleFile + ".assetbundle"),

                Path.Combine(dllDir, bundleFile),
                Path.Combine(dllDir, bundleFile + ".bundle"),
                Path.Combine(dllDir, bundleFile + ".assetbundle"),
            };

            string found = null;
            foreach (var p in candidates)
                if (File.Exists(p)) { found = p; break; }

            if (found == null)
            {
                Logger.LogWarning($"[AssetSign] Bundle '{bundleFile}' nicht gefunden (z.B. in {pluginDir}).");
                return;
            }

            if (_signBundle == null)
            {
                _signBundle = AssetBundle.LoadFromFile(found);
                if (_signBundle == null)
                {
                    Logger.LogWarning($"[AssetSign] Bundle konnte nicht geladen werden: {found}");
                    return;
                }

                Logger.LogInfo($"[AssetSign] Bundle OK: {found}");
            }

            if (_assetPanelPrefab == null)
                _assetPanelPrefab = _signBundle.LoadAsset<GameObject>(ASSET_PANEL_PREFAB);
            if (_assetPolePrefab == null)
                _assetPolePrefab = _signBundle.LoadAsset<GameObject>(ASSET_POLE_PREFAB);
            if (_assetLabelPrefab == null)
                _assetLabelPrefab = _signBundle.LoadAsset<GameObject>(ASSET_LABEL_PREFAB);

            if (_assetPanelPrefab == null)
                Logger.LogWarning($"[AssetSign] Prefab '{ASSET_PANEL_PREFAB}' nicht im Bundle gefunden.");
            if (_assetPolePrefab == null)
                Logger.LogWarning($"[AssetSign] Prefab '{ASSET_POLE_PREFAB}' nicht im Bundle gefunden.");

            if (_assetLabelPrefab == null)
                Logger.LogWarning($"[AssetSign] Label-Prefab '{ASSET_LABEL_PREFAB}' nicht im Bundle gefunden (optional).");
            else
                Logger.LogInfo($"[AssetSign] Label-Prefab OK: {ASSET_LABEL_PREFAB}");

            if (_assetPanelPrefab != null && _assetPolePrefab != null)
                Logger.LogInfo($"[AssetSign] Prefabs OK: {ASSET_PANEL_PREFAB} + {ASSET_POLE_PREFAB}");
        }
        catch (Exception e)
        {
            Logger.LogWarning($"[AssetSign] TryLoadAssetSignPrefabs Exception: {e}");
        }
    }

    // =========================
    // ✅ PREWARM: Instanziere einmal unsichtbar + ersetze Shader auf Unlit/Standard
    // -> verhindert, dass das erste echte Asset erst nach Sekunden gerendert wird.
    // =========================
    private void PrewarmAssetSign()
    {
        if (_assetPrewarmed) return;

        try
        {
            if (_assetPanelPrefab == null || _assetPolePrefab == null)
            {
                Logger.LogWarning("[AssetSign] Prewarm skipped (prefabs missing).");
                return;
            }

            GameObject tmpRoot = new GameObject("PeakSign_Prewarm");
            tmpRoot.SetActive(false);

            GameObject tmpPanel = Instantiate(_assetPanelPrefab, tmpRoot.transform, false);
            GameObject tmpPole  = Instantiate(_assetPolePrefab,  tmpRoot.transform, false);

            ForceSimpleShaders(tmpPanel);
            ForceSimpleShaders(tmpPole);

            // Renderer einmal anfassen (initialisiert intern häufig einiges)
            _ = tmpPanel.GetComponentsInChildren<Renderer>(true);
            _ = tmpPole.GetComponentsInChildren<Renderer>(true);

            Destroy(tmpRoot);

            _assetPrewarmed = true;
            Logger.LogInfo("[AssetSign] Prewarm done.");
        }
        catch (Exception e)
        {
            Logger.LogWarning($"[AssetSign] Prewarm failed: {e}");
        }
    }

    private static void ForceSimpleShaders(GameObject root)
    {
        if (root == null) return;

        Shader unlit = Shader.Find("Unlit/Color");
        Shader standard = Shader.Find("Standard");

        var renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            if (r == null) continue;

            var mats = r.materials;
            if (mats == null) continue;

            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;

                if (unlit != null) m.shader = unlit;
                else if (standard != null) m.shader = standard;

                // wenn möglich eine Farbe setzen, damit Unlit/Color sicher etwas zeigt
                if (m.HasProperty("_Color")) m.color = Color.white;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            }
        }
    }

    // =========================
    // AssetSign Create
    // =========================
    private GameObject CreateSignFromAssets(Vector3 placePos, Quaternion rot, Vector3 surfaceNormal, bool isWall, int signId, int ownerActor, Color ownerColor)
    {
        TryLoadAssetSignPrefabs();

        if (_assetPanelPrefab == null || _assetPolePrefab == null)
        {
            Logger.LogWarning("[AssetSign] Fallback -> prozedurales Schild (Assets nicht geladen).");
            return null;
        }

        // Safety: wenn Start() aus irgendeinem Grund nicht lief (Hot reload), prewarm hier einmal
        if (!_assetPrewarmed)
            PrewarmAssetSign();

        GameObject root = new GameObject("PeakSign_Asset");
        var marker = root.AddComponent<PeakSignMarker>();
        marker.SignId = signId;
        marker.OwnerActor = ownerActor;

        GameObject panel = Instantiate(_assetPanelPrefab);
        panel.name = "Panel";
        panel.transform.SetParent(root.transform, false);

        GameObject pole = Instantiate(_assetPolePrefab);
        pole.name = "Pole";
        pole.transform.SetParent(root.transform, false);

        // ✅ auch beim echten Spawn Shader vereinfachen (damit garantiert sofort sichtbar)
        ForceSimpleShaders(panel);
        ForceSimpleShaders(pole);

        SetLayerRecursive(panel, 0);
        SetLayerRecursive(pole, 0);

        MakeAllCollidersTrigger(panel);
        MakeAllCollidersTrigger(pole);

        float wallGap = 0.03f;

        if (!isWall)
        {
            panel.transform.position = placePos + Vector3.up * 0.85f;
            panel.transform.rotation = rot;

            pole.transform.position = placePos + Vector3.up * 0.45f;
            pole.transform.rotation = rot;
        }
        else
        {
            Vector3 n = surfaceNormal.normalized;

            Quaternion poleRot = Quaternion.FromToRotation(Vector3.up, n);
            pole.transform.rotation = poleRot;

            float poleLen = 1.0f;
            var poleR = pole.GetComponentInChildren<Renderer>(true);
            if (poleR != null) poleLen = Mathf.Max(0.3f, poleR.bounds.size.magnitude);

            Vector3 poleCenter = placePos + n * (poleLen * 0.5f + wallGap);
            pole.transform.position = poleCenter;

            Vector3 forward = -n;

            Camera cam = Camera.main;
            Vector3 dirOnPlane = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, n) : Vector3.zero;
            if (dirOnPlane.sqrMagnitude < 0.0001f)
                dirOnPlane = Vector3.Cross(Vector3.up, n);
            if (dirOnPlane.sqrMagnitude < 0.0001f)
                dirOnPlane = Vector3.Cross(Vector3.forward, n);
            dirOnPlane.Normalize();

            Vector3 up = Vector3.Cross(forward, dirOnPlane).normalized;
            if (up.sqrMagnitude < 0.0001f)
                up = Vector3.ProjectOnPlane(Vector3.up, forward).normalized;

            panel.transform.rotation = Quaternion.LookRotation(forward, up);
            panel.transform.position = placePos + n * 0.06f;
        }

        // Colors (do NOT replace materials; only set color props)
        ApplyColorToAllRenderers(panel, ownerColor);
        ApplyColorToAllRenderers(pole, Color.Lerp(ownerColor, Color.black, 0.45f));

        // Label (AssetLabel -> sonst TextMesh)
        try
        {
            string playerName = GetOwnerPlayerName(ownerActor);

            if (_assetLabelPrefab != null)
            {
                var label = Instantiate(_assetLabelPrefab);
                label.name = "NameLabel";
                label.transform.SetParent(panel.transform, false);

                var anchor = panel.transform.Find("LabelAnchor");
                if (anchor != null)
                {
                    label.transform.position = anchor.position;
                    label.transform.rotation = anchor.rotation;
                }

                SetAnyTextOnLabel(label, playerName);
                MakeLabelNonInteractive(label);
            }
            else
            {
                CreateNameLabelOnAssetPanel(panel.transform, playerName);
            }
        }
        catch (Exception e)
        {
            Logger.LogWarning($"[AssetSign] NameLabel failed: {e}");
        }

        return root;
    }

    // =========================
    // Procedural Create
    // =========================
    private GameObject CreateSignProcedural(Vector3 placePos, Quaternion rot, Vector3 surfaceNormal, bool isWall, int signId, int ownerActor, Color ownerColor)
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
            panel.transform.position = placePos + Vector3.up * 0.85f;
            panel.transform.rotation = rot * Quaternion.Euler(0f, -90f, 0f);

            pole.transform.position = placePos + Vector3.up * 0.45f;
            pole.transform.rotation = rot;
            pole.transform.localScale = new Vector3(poleRadius, poleHalfHeight, poleRadius);
        }
        else
        {
            Vector3 n = surfaceNormal.normalized;

            Quaternion poleRot = Quaternion.FromToRotation(Vector3.up, n);

            Vector3 poleCenter = placePos + n * (poleLength * 0.5f + wallGap);
            pole.transform.position = poleCenter;
            pole.transform.rotation = poleRot;
            pole.transform.localScale = new Vector3(poleRadius, poleHalfHeight, poleRadius);

            Vector3 pivot = placePos + n * (poleLength + wallGap);
            panel.transform.position = pivot + n * (panelThickness * 0.5f + wallGap);

            Vector3 forward = -n;

            Camera cam = Camera.main;
            Vector3 dirOnPlane = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, n) : Vector3.zero;
            if (dirOnPlane.sqrMagnitude < 0.0001f)
                dirOnPlane = Vector3.Cross(Vector3.up, n);
            if (dirOnPlane.sqrMagnitude < 0.0001f)
                dirOnPlane = Vector3.Cross(Vector3.forward, n);
            dirOnPlane.Normalize();

            Vector3 up = Vector3.Cross(forward, dirOnPlane).normalized;
            if (up.sqrMagnitude < 0.0001f)
                up = Vector3.ProjectOnPlane(Vector3.up, forward).normalized;

            panel.transform.rotation = Quaternion.LookRotation(forward, up);
        }

        MakeNoHitbox(panel);
        MakeNoHitbox(pole);

        ApplyUnlit(panel, ownerColor);
        ApplyUnlit(pole, Color.Lerp(ownerColor, Color.black, 0.45f));

        // Front-Text
        try
        {
            string playerName = GetOwnerPlayerName(ownerActor);
            CreateNameLabelTextMesh_FrontOnly(panel.transform, playerName, panelWidth, panelHeight, tipLen, panelThickness);
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
        go.layer = 0;
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

    // =========================
    // Asset Helpers
    // =========================
    private static void ApplyColorToAllRenderers(GameObject root, Color c)
    {
        if (root == null) return;

        var renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null) continue;

            var mats = r.materials;
            if (mats == null) continue;

            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (mat == null) continue;

                if (mat.HasProperty("_Color")) mat.color = c;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            }

            r.enabled = true;
        }
    }

    private static void MakeAllCollidersTrigger(GameObject root)
    {
        if (root == null) return;
        var cols = root.GetComponentsInChildren<Collider>(true);
        foreach (var c in cols)
            if (c != null) c.isTrigger = true;
    }

    private static void SetLayerRecursive(GameObject go, int layer)
    {
        if (go == null) return;
        go.layer = layer;
        foreach (Transform t in go.transform)
            if (t != null) SetLayerRecursive(t.gameObject, layer);
    }

    // =========================
    // Label Helpers (Asset UI/Text/TMP)
    // =========================
    private static void SetAnyTextOnLabel(GameObject labelRoot, string text)
    {
        if (labelRoot == null) return;
        if (string.IsNullOrWhiteSpace(text)) text = "Player";

        // TMP (Reflection)
        try
        {
            var comps = labelRoot.GetComponentsInChildren<Component>(true);
            foreach (var c in comps)
            {
                if (c == null) continue;
                var t = c.GetType();
                if (t == null) continue;

                if (t.FullName != null && t.FullName.Contains("TMPro"))
                {
                    var p = t.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                    if (p != null && p.CanWrite)
                        p.SetValue(c, text, null);
                }
            }
        }
        catch { }

        // Unity UI Text
        try
        {
            var uiTexts = labelRoot.GetComponentsInChildren<UnityEngine.UI.Text>(true);
            foreach (var t in uiTexts)
                if (t != null) t.text = text;
        }
        catch { }

        // 3D TextMesh
        try
        {
            var tms = labelRoot.GetComponentsInChildren<TextMesh>(true);
            foreach (var tm in tms)
                if (tm != null) tm.text = text;
        }
        catch { }
    }

    private static void MakeLabelNonInteractive(GameObject labelRoot)
    {
        if (labelRoot == null) return;

        var raycasters = labelRoot.GetComponentsInChildren<GraphicRaycaster>(true);
        foreach (var r in raycasters)
            if (r != null) r.enabled = false;

        var graphics = labelRoot.GetComponentsInChildren<Graphic>(true);
        foreach (var g in graphics)
            if (g != null) g.raycastTarget = false;

        SetLayerRecursive(labelRoot, 0);
    }

    // =========================
    // Asset NameLabel (Fallback TextMesh)
    // =========================
    private static void CreateNameLabelOnAssetPanel(Transform panel, string text)
    {
        GameObject textGo = new GameObject("NameLabel");
        textGo.transform.SetParent(panel, false);

        TextMesh tm = textGo.AddComponent<TextMesh>();
        tm.text = string.IsNullOrWhiteSpace(text) ? "Player" : text;

        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = Color.black;

        tm.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        tm.fontStyle = FontStyle.Bold;

        tm.fontSize = 56;
        tm.characterSize = 0.075f;
        tm.richText = false;

        textGo.transform.localPosition = new Vector3(0f, 0f, 0.01f);
        textGo.transform.localRotation = Quaternion.identity;

        MeshRenderer mr = textGo.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            Material mat = new Material(tm.font.material);
            mat.renderQueue = 3100;

            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_ZTest")) mat.SetFloat("_ZTest", (float)CompareFunction.LessEqual);

            mr.material = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.sortingOrder = 5000;
            mr.enabled = true;
        }
    }

    // =========================
    // Text (Procedural)
    // =========================
    private static Font GetBuiltinArialFont()
    {
        return Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    private static Material CreateTextMaterialFromFont(Font font)
    {
        if (font == null || font.material == null) return null;

        Material m = new Material(font.material);
        m.renderQueue = 3100;

        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
        if (m.HasProperty("_ZTest")) m.SetFloat("_ZTest", (float)CompareFunction.LessEqual);

        return m;
    }

    private static void CreateNameLabelTextMesh_FrontOnly(
        Transform panel,
        string text,
        float panelWidth,
        float panelHeight,
        float tipLen,
        float panelThickness)
    {
        GameObject textGo = new GameObject("NameLabel");
        textGo.transform.SetParent(panel, false);

        TextMesh tm = textGo.AddComponent<TextMesh>();
        tm.text = string.IsNullOrWhiteSpace(text) ? "Player" : text;

        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = Color.black;

        tm.font = GetBuiltinArialFont();
        tm.fontStyle = FontStyle.Bold;

        tm.fontSize = 56;
        tm.characterSize = 0.075f;
        tm.richText = false;

        float bodyWidth = Mathf.Max(0.1f, panelWidth - tipLen);
        float xCenter = -(tipLen * 0.25f);

        float zOut = (panelThickness * 0.5f) + 0.010f;

        textGo.transform.localPosition = new Vector3(xCenter, 0f, zOut);
        textGo.transform.localRotation = Quaternion.identity;

        MeshRenderer mr = textGo.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            Material mat = CreateTextMaterialFromFont(tm.font);
            if (mat != null) mr.material = mat;

            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.sortingOrder = 5000;
            mr.enabled = true;
        }

        float maxWidth  = bodyWidth * 0.85f;
        float maxHeight = panelHeight * 0.70f;

        FitTextMeshToPanelBounds_ShrinkOnly(tm, maxWidth, maxHeight, baseCharacterSize: 0.075f, minCharacterSize: 0.04f);
    }

    private static void FitTextMeshToPanelBounds_ShrinkOnly(TextMesh textMesh, float maxWidth, float maxHeight, float baseCharacterSize, float minCharacterSize)
    {
        if (textMesh == null) return;

        MeshRenderer mr = textMesh.GetComponent<MeshRenderer>();
        if (mr == null) return;

        mr.enabled = true;

        string original = textMesh.text ?? string.Empty;
        if (original.Length == 0)
        {
            textMesh.characterSize = baseCharacterSize;
            return;
        }

        textMesh.characterSize = baseCharacterSize;

        for (int i = 0; i < 30; i++)
        {
            Bounds b = mr.bounds;
            float w = b.size.x;
            float h = b.size.y;

            if (w <= 0.0001f || h <= 0.0001f)
                break;

            if (w <= maxWidth && h <= maxHeight)
                return;

            float scaleW = maxWidth / w;
            float scaleH = maxHeight / h;
            float scale = Mathf.Min(scaleW, scaleH);

            scale = Mathf.Clamp(scale, 0.70f, 0.97f);
            float next = textMesh.characterSize * scale;

            if (next >= textMesh.characterSize - 0.00001f)
                next = textMesh.characterSize - 0.003f;

            textMesh.characterSize = Mathf.Max(minCharacterSize, next);

            if (textMesh.characterSize <= minCharacterSize + 0.00001f)
                return;
        }
    }

    // =========================
    // Arrow Mesh (Procedural)
    // =========================
    private static Mesh CreateArrowMesh(float width, float height, float thickness, float tipLength)
    {
        float w = width;
        float h = height;
        float t = thickness;
        float tip = Mathf.Clamp(tipLength, 0.05f, w * 0.49f);

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

        // front
        tris.AddRange(new[] { 0, 1, 2,  0, 2, 3,  0, 3, 4 });
        // back
        tris.AddRange(new[] { n + 0, n + 2, n + 1,  n + 0, n + 3, n + 2,  n + 0, n + 4, n + 3 });

        // sides
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
    // Owner Name
    // =========================
    private static string GetOwnerPlayerName(int ownerActor)
    {
        try
        {
            if (PhotonNetwork.IsConnected && PhotonNetwork.LocalPlayer != null &&
                ownerActor == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                var lc = Character.localCharacter;
                if (lc != null && !string.IsNullOrWhiteSpace(lc.characterName))
                    return lc.characterName;

                if (!string.IsNullOrWhiteSpace(PhotonNetwork.LocalPlayer.NickName))
                    return PhotonNetwork.LocalPlayer.NickName;
            }
        }
        catch { }

        try
        {
            if (PhotonNetwork.IsConnected && PhotonNetwork.CurrentRoom != null)
            {
                var p = PhotonNetwork.CurrentRoom.GetPlayer(ownerActor);
                if (p != null && !string.IsNullOrWhiteSpace(p.NickName))
                    return p.NickName;
            }
        }
        catch { }

        return "Player";
    }

    // =========================
    // Recolor (works for Asset + Procedural)
    // =========================
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
            {
                MeshRenderer pmr = panelT.GetComponent<MeshRenderer>();
                if (pmr != null)
                    SetRendererColor(pmr, ownerColor);
                else
                    ApplyColorToAllRenderers(panelT.gameObject, ownerColor);
            }

            Transform poleT = root.transform.Find("Pole");
            if (poleT != null)
            {
                MeshRenderer mr = poleT.GetComponent<MeshRenderer>();
                if (mr != null)
                    SetRendererColor(mr, Color.Lerp(ownerColor, Color.black, 0.45f));
                else
                    ApplyColorToAllRenderers(poleT.gameObject, Color.Lerp(ownerColor, Color.black, 0.45f));
            }
        }
    }

    // =========================
    // Progress UI (UseItem Clone)
    // =========================
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

    // =========================
    // SetRendererColor helper
    // =========================
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
    // Customization Reflection (Player Color)
    // =========================
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
}

public class PeakSignMarker : MonoBehaviour
{
    public int SignId;
    public int OwnerActor;
}
