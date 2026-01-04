using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;

[BepInPlugin("com.mitsc.peaksigns", "Peak Signs", "0.8.1")]
public class PeakSigns : BaseUnityPlugin
{
    private ConfigEntry<int> _mouseButton;
    private ConfigEntry<float> _maxDistance;
    private ConfigEntry<float> _deleteHoldSeconds;

    // Farbe für den Lösch-Progress (kannst du anpassen)
    // Beispiel: rot/orange
    private readonly Color _deleteColor = new Color(1f, 0.35f, 0.10f, 1f);

    // Hold-State (MMB)
    private bool _isHolding;
    private float _holdStart;
    private GameObject _deleteTarget;

    // Schilder
    private readonly List<GameObject> _signs = new List<GameObject>();

    // Progress UI (gezielt aus UseItem geklont)
    private GameObject _progressRoot;
    private Image _progressFill;
    private bool _progressReady;

    // Originalfarbe merken (damit wir optional wieder zurücksetzen können)
    private Color _originalProgressColor;

    private void Awake()
    {
        _mouseButton = Config.Bind("Input", "MouseButton", 2, "2 = Middle Mouse (Mausrad-Klick)");
        _maxDistance = Config.Bind("Placement", "MaxDistance", 60f, "Maximale Distanz.");
        _deleteHoldSeconds = Config.Bind("Delete", "HoldSeconds", 1.25f, "Hold-Zeit zum Löschen.");

        Logger.LogInfo("Peak Signs 0.8.1 geladen. Progress-Kreis aus 'UseItem' + Farbe angepasst.");
    }

    private void Start()
    {
        // Versuch beim Start – falls HUD später lädt, versuchen wir auch in Update nochmal.
        TrySetupUseItemProgress();
    }

    private void Update()
    {
        if (!_progressReady)
        {
            // HUD kann nachladen → gelegentlich nochmal versuchen
            TrySetupUseItemProgress();
        }

        int btn = _mouseButton.Value;

        if (Input.GetMouseButtonDown(btn))
        {
            _isHolding = true;
            _holdStart = Time.unscaledTime;
            _deleteTarget = GetSignUnderCrosshair();
        }

        if (_isHolding && Input.GetMouseButton(btn))
        {
            if (_deleteTarget != null)
            {
                float held = Time.unscaledTime - _holdStart;
                float t = Mathf.Clamp01(held / Mathf.Max(0.01f, _deleteHoldSeconds.Value));

                ShowProgress(true);
                SetProgress(t);

                if (held >= _deleteHoldSeconds.Value)
                {
                    DeleteSign(_deleteTarget);
                    _deleteTarget = null;
                    _isHolding = false;
                    ShowProgress(false);
                }
            }
            else
            {
                ShowProgress(false);
            }
        }

        if (_isHolding && Input.GetMouseButtonUp(btn))
        {
            float held = Time.unscaledTime - _holdStart;

            // Kurzer Klick: wenn nicht auf Schild gezielt -> Schild platzieren
            if (_deleteTarget == null && held < _deleteHoldSeconds.Value)
                TryPlaceSign();

            _deleteTarget = null;
            _isHolding = false;
            ShowProgress(false);
        }
    }

    // -------------------- Progress UI: UseItem -> Filled Radial Kreis --------------------

    private void TrySetupUseItemProgress()
    {
        if (_progressReady) return;

        // 1) Finde GameObject namens "UseItem"
        GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
        GameObject useItem = null;

        for (int i = 0; i < all.Length; i++)
        {
            GameObject go = all[i];
            if (go == null) continue;
            if (!string.Equals(go.name, "UseItem", StringComparison.OrdinalIgnoreCase)) continue;

            // optional: bevorzugt aus "GAME" root (wie bei dir im Pfad)
            if (go.transform.root != null &&
                go.transform.root.name.IndexOf("GAME", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                useItem = go;
                break;
            }

            if (useItem == null) useItem = go;
        }

        if (useItem == null) return;

        // 2) In UseItem: Filled Image mit Radial fill suchen (dein Kreis)
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

        // 3) Parent-Container klonen (max 3 Ebenen hoch, aber nicht den ganzen HUD)
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
        _progressRoot.name = "PeakSigns_DeleteProgress(UseItemClone)";
        _progressRoot.SetActive(true);

        // 4) Animator/Animation entfernen, weil sie oft Sichtbarkeit steuern
        StripAnimators(_progressRoot);

        // 5) CanvasGroup erzwingen und von uns steuern
        EnsureCanvasGroups(_progressRoot);

        // 6) Passendes Filled Image im Clone finden (Name+FillMethod)
        _progressFill = FindFillInClone(_progressRoot, best.name, best.fillMethod);

        // Farbe setzen (Original merken)
        if (_progressFill != null)
        {
            _originalProgressColor = _progressFill.color;
            _progressFill.color = _deleteColor;
        }

        // Startzustand
        SetProgress(0f);
        ShowProgress(false);

        _progressReady = (_progressFill != null);

        Logger.LogInfo("✅ UseItem-Kreis bereit: root=" + root.name +
                       " | fillName=" + best.name +
                       " | fillMethod=" + best.fillMethod +
                       " | cloneFillFound=" + (_progressFill != null));
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

        // exakter Treffer
        for (int i = 0; i < imgs.Length; i++)
        {
            Image im = imgs[i];
            if (im == null) continue;
            if (im.type != Image.Type.Filled) continue;
            if (im.name == wantName && im.fillMethod == wantMethod)
                return im;
        }

        // fallback: erstes Filled
        for (int i = 0; i < imgs.Length; i++)
        {
            Image im = imgs[i];
            if (im != null && im.type == Image.Type.Filled)
                return im;
        }

        return null;
    }

    private void ShowProgress(bool visible)
    {
        if (_progressRoot == null) return;

        // Nach vorne
        _progressRoot.transform.SetAsLastSibling();

        // Optional: beim Einblenden sicherstellen, dass die Wunschfarbe gesetzt ist
        if (_progressFill != null)
            _progressFill.color = _deleteColor;

        // Sichtbarkeit über CanvasGroup Alpha erzwingen
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

        // Position: links von Mitte (wie beschrieben)
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

    // --------------------- Signs: place / pick / delete ---------------------

    private void TryPlaceSign()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        RaycastHit hit;

        if (!Physics.Raycast(ray, out hit, _maxDistance.Value, ~0, QueryTriggerInteraction.Ignore))
            return;

        Vector3 groundPos = hit.point + Vector3.up * 0.05f;

        Vector3 fwdFlat = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        if (fwdFlat.sqrMagnitude < 0.0001f) fwdFlat = Vector3.forward;

        GameObject sign = CreateSign(groundPos, Quaternion.LookRotation(fwdFlat.normalized, Vector3.up));
        _signs.Add(sign);

        Logger.LogInfo("Schild platziert (MMB kurz). Löschen: MMB halten auf Schild.");
    }

    private GameObject GetSignUnderCrosshair()
    {
        Camera cam = Camera.main;
        if (cam == null) return null;

        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        RaycastHit hit;

        // Trigger sollen getroffen werden (keine Hitbox, aber auswählbar)
        if (Physics.Raycast(ray, out hit, _maxDistance.Value, ~0, QueryTriggerInteraction.Collide))
        {
            PeakSignMarker marker = (hit.collider != null) ? hit.collider.GetComponentInParent<PeakSignMarker>() : null;
            if (marker != null) return marker.gameObject;
        }

        return null;
    }

    private void DeleteSign(GameObject sign)
    {
        if (sign == null) return;
        _signs.Remove(sign);
        Destroy(sign);
        Logger.LogInfo("Schild gelöscht.");
    }

    private GameObject CreateSign(Vector3 groundPos, Quaternion rot)
    {
        GameObject root = new GameObject("PeakSign");
        root.AddComponent<PeakSignMarker>();

        // Panel
        GameObject panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panel.name = "Panel";
        panel.transform.SetParent(root.transform, false);
        panel.transform.position = groundPos + Vector3.up * 0.9f;
        panel.transform.rotation = rot;
        panel.transform.localScale = new Vector3(1.2f, 0.8f, 0.08f);

        // Pfosten
        GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "Pole";
        pole.transform.SetParent(root.transform, false);
        pole.transform.position = groundPos + Vector3.up * 0.45f;
        pole.transform.rotation = rot;
        pole.transform.localScale = new Vector3(0.08f, 0.45f, 0.08f);

        // Keine Hitbox: blockt nicht (Trigger)
        MakeNoHitbox(panel);
        MakeNoHitbox(pole);

        // Sichtbar
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

// Marker damit wir Schilder in Raycasts erkennen
public class PeakSignMarker : MonoBehaviour { }
