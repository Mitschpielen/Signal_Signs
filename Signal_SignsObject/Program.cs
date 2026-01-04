using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

[BepInPlugin("com.mitsc.peaksigns", "Peak Signs", "0.2.0")]
public class PeakSigns : BaseUnityPlugin
{
    private ConfigEntry<int> _mouseButton;
    private ConfigEntry<float> _maxDistance;
    private ConfigEntry<float> _lifetimeSeconds;
    private ConfigEntry<int> _maxActiveSigns;

    private readonly List<GameObject> _activeSigns = new List<GameObject>();


    private void Awake()
    {
        _mouseButton     = Config.Bind("Input", "MouseButton", 2, "2 = Middle Mouse (Mausrad-Klick)");
        _maxDistance     = Config.Bind("Placement", "MaxDistance", 60f, "Maximale Platzierungsdistanz (wie Ping-Reichweite).");
        _lifetimeSeconds = Config.Bind("Placement", "LifetimeSeconds", 20f, "Wie lange das Schild bleibt (Sekunden).");
        _maxActiveSigns  = Config.Bind("Placement", "MaxActiveSigns", 3, "Maximale Anzahl gleichzeitiger Schilder.");

        Logger.LogInfo("PeakSigns 0.2.0 geladen.");
    }

    private void Update()
    {
        if (Input.GetMouseButtonDown(_mouseButton.Value))
        {
            TryPlaceSign();
        }
    }

    private void TryPlaceSign()
    {
        var cam = Camera.main;
        if (cam == null)
        {
            Logger.LogError("Camera.main nicht gefunden.");
            return;
        }

        // Genau wie Ping: Ray aus Bildschirmmitte
        Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

        if (!Physics.Raycast(ray, out RaycastHit hit, _maxDistance.Value, ~0, QueryTriggerInteraction.Ignore))
        {
            Logger.LogInfo("Kein Ziel getroffen (Raycast).");
            return;
        }

        Vector3 pos = hit.point + Vector3.up * 0.05f;

        // Schild schaut zur Kamera, aber ohne Neigung
        Vector3 fwdFlat = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
        if (fwdFlat.sqrMagnitude < 0.0001f) fwdFlat = Vector3.forward;
        Quaternion rot = Quaternion.LookRotation(fwdFlat, Vector3.up);

        // Limit: alte Schilder entfernen
        while (_activeSigns.Count >= _maxActiveSigns.Value)
        {
            if (_activeSigns[0] != null) Destroy(_activeSigns[0]);
            _activeSigns.RemoveAt(0);
        }

        GameObject sign = CreateSign(pos, rot);

        _activeSigns.Add(sign);
        Destroy(sign, _lifetimeSeconds.Value);

        Logger.LogInfo($"Schild platziert bei {pos} (entfernt sich in {_lifetimeSeconds.Value}s).");
    }

    private static GameObject CreateSign(Vector3 groundPos, Quaternion rot)
    {
        // Panel
        var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panel.name = "PeakSign";
        panel.transform.position = groundPos + Vector3.up * 0.9f;
        panel.transform.rotation = rot;
        panel.transform.localScale = new Vector3(1.2f, 0.8f, 0.08f);

        // Pfosten
        var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "PeakSign_Pole";
        pole.transform.SetParent(panel.transform, worldPositionStays: true);
        pole.transform.position = groundPos + Vector3.up * 0.45f;
        pole.transform.rotation = rot;
        pole.transform.localScale = new Vector3(0.08f, 0.45f, 0.08f);

        // Physik aus
        var rb1 = panel.GetComponent<Rigidbody>();
        if (rb1 != null) Object.Destroy(rb1);
        var rb2 = pole.GetComponent<Rigidbody>();
        if (rb2 != null) Object.Destroy(rb2);

        // Collider anlassen (damit es "da" ist) oder entfernen:
        // Object.Destroy(panel.GetComponent<Collider>());
        // Object.Destroy(pole.GetComponent<Collider>());

        ApplyUnlit(panel, new Color(1f, 0.95f, 0.2f, 1f)); // gelb
        ApplyUnlit(pole,  new Color(1f, 0.2f, 1f, 1f));   // magenta

        return panel;
    }

    private static void ApplyUnlit(GameObject go, Color c)
    {
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;

        mr.enabled = true;
        mr.shadowCastingMode = ShadowCastingMode.On;
        mr.receiveShadows = true;

        Shader s = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
        var m = new Material(s);
        if (m.HasProperty("_Color")) m.color = c;
        m.renderQueue = 3000;

        mr.material = m;
    }
}
