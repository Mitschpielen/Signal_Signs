using System;
using System.Collections.Generic;
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

    // Farben
    private readonly Color _deleteColor = new Color(1f, 0.35f, 0.10f, 1f); // orange/rot
    private readonly Color _placeColor  = new Color(0.20f, 0.80f, 1.00f, 1f); // blau

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
    // Frei wählbare Event-Codes (nur nicht mit Spiel kollidieren; hohe Nummern sind meist ok)
    private const byte EVT_SPAWN_SIGN  = 201;
    private const byte EVT_DELETE_SIGN = 202;

    private void Awake()
    {
        _mouseButton = Config.Bind("Input", "MouseButton", 2, "2 = Middle Mouse (Mausrad-Klick)");
        _maxDistance = Config.Bind("Placement", "MaxDistance", 60f, "Maximale Distanz.");

        _deleteHoldSeconds = Config.Bind("Delete", "HoldSeconds", 1.25f, "Hold-Zeit zum Löschen.");
        _placeHoldSeconds  = Config.Bind("Placement", "HoldSeconds", 1.00f, "Hold-Zeit zum Platzieren.");

        Logger.LogInfo("Peak Signs 1.0.0 geladen. Place+Delete mit Progress + Multiplayer Sync (Photon).");
    }

    private void OnEnable()
    {
        // Photon Event Hook
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

        int btn = _mouseButton.Value;

        if (Input.GetMouseButtonDown(btn))
        {
            _isHolding = true;
            _holdStart = Time.unscaledTime;
            _placedThisHold = false;

            // initial check
            _deleteTarget = GetSignUnderCrosshair();
        }

        if (_isHolding && Input.GetMouseButton(btn))
        {
            float held = Time.unscaledTime - _holdStart;

            // Während Hold: wenn wir noch nicht platziert haben, darf sich Target ändern
            // (z.B. du fängst frei an zu halten, zielst dann auf Schild -> soll löschen)
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
                    // Schild-ID finden (Root hat Marker, wir speichern IDs in Dictionary -> suchen)
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
                // Spawn an visiertem Punkt
                if (TryGetPlacement(out Vector3 pos, out Quaternion rot))
                {
                    int id = GenerateSignId();
                    RequestSpawn(id, pos, rot);
                    _placedThisHold = true;
                }
                // Nach Platzierung: Progress aus (oder lass ihn kurz stehen, wenn du willst)
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
        // Lokal spawnen
        SpawnLocal(id, pos, rot);

        // Multiplayer: an andere schicken
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
        // Lokal löschen
        DeleteLocal(id);

        // Multiplayer: an andere schicken
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

            SpawnLocal(id, new Vector3(px, py, pz), new Quaternion(qx, qy, qz, qw));
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
        // "InRoom" deckt i.d.R. Multiplayer ab
        return PhotonNetwork.IsConnected && PhotonNetwork.InRoom;
    }

    // =========================
    // Spawn/Delete Local
    // =========================

    private void SpawnLocal(int id, Vector3 groundPos, Quaternion rot)
    {
        if (_signsById.ContainsKey(id))
            return; // schon da

        GameObject sign = CreateSign(groundPos, rot);
        _signsById[id] = sign;

        Logger.LogInfo($"Schild gespawnt id={id} pos={groundPos}");
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
        // signObj ist unser Root (PeakSign), oder Child -> wir gehen auf Marker-Root
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
        // einfache ID; robust genug für kleine Mods
        int id;
        do
        {
            id = UnityEngine.Random.Range(100000, int.MaxValue);
        } while (_signsById.ContainsKey(id));
        return id;
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

            // bevorzugt "GAME" root, falls vorhanden
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

        // Position: links von Mitte
        RectTransform rt = _progressRoot.GetComponent<RectTransform>();
        if (rt != null)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(-120f, 0f);
            rt.localScale = Vector3.one;
        }
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

    private GameObject CreateSign(Vector3 groundPos, Quaternion rot)
    {
        GameObject root = new GameObject("PeakSign");
        root.AddComponent<PeakSignMarker>();

        GameObject panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panel.name = "Panel";
        panel.transform.SetParent(root.transform, false);
        panel.transform.position = groundPos + Vector3.up * 0.9f;
        panel.transform.rotation = rot;
        panel.transform.localScale = new Vector3(1.2f, 0.8f, 0.08f);

        GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "Pole";
        pole.transform.SetParent(root.transform, false);
        pole.transform.position = groundPos + Vector3.up * 0.45f;
        pole.transform.rotation = rot;
        pole.transform.localScale = new Vector3(0.08f, 0.45f, 0.08f);

        MakeNoHitbox(panel);
        MakeNoHitbox(pole);

        ApplyUnlit(panel, new Color(1f, 0.95f, 0.2f, 1f));
        ApplyUnlit(pole, new Color(1f, 0.2f, 1f, 1f));

        return root;
    }

    private static void MakeNoHitbox(GameObject go)
    {
        Collider c = go.GetComponent<Collider>();
        if (c != null) c.isTrigger = true;  // blockt nicht
        go.layer = 2; // Ignore Raycast (optional)
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

public class PeakSignMarker : MonoBehaviour { }
