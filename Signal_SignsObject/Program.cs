using BepInEx;
using UnityEngine;
using UnityEngine.Rendering;

[BepInPlugin("com.mitsc.signalsignsobject", "Signal Signs Object", "1.0.3")]
public class SignalSignsObjectPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        Logger.LogInfo("### SIGNAL SIGNS MOD 1.0.3 GELADEN ###");
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F))
        {
            Logger.LogInfo("### F GEDRÜCKT -> Spawn() ###");
            Spawn();
        }
    }

    private void Spawn()
    {
        Logger.LogInfo("### Spawn() START ###");

        Camera cam = Camera.main;
        if (cam == null)
        {
            Logger.LogError("Camera.main nicht gefunden.");
            return;
        }

        Logger.LogInfo($"### Camera cullingMask: {cam.cullingMask} ###");

        // 1) Spawn garantiert im Blickfeld: direkt vor der Kamera
        Vector3 pos = cam.transform.position + cam.transform.forward * 3.0f;

        // Ein kleines Stück nach unten raycasten, aber NICHT zwingend auf Boden setzen
        // (damit es nicht im Boden verschwindet)
        if (Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 10f, ~0, QueryTriggerInteraction.Ignore))
            pos = hit.point + Vector3.up * 0.3f;

        // 2) Debug-Marker: Kugel (solltest du IMMER sehen)
        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "SignalSign_Marker";
        marker.transform.position = pos;
        marker.transform.localScale = Vector3.one * 0.5f;

        var markerMr = marker.GetComponent<MeshRenderer>();
        markerMr.enabled = true;
        markerMr.shadowCastingMode = ShadowCastingMode.Off;
        markerMr.receiveShadows = false;
        markerMr.material = MakeUnlitColor(Color.magenta);

        // 3) Das eigentliche "Schild" (absichtlich groß)
        var sign = GameObject.CreatePrimitive(PrimitiveType.Cube);
        sign.name = "SignalSign";
        sign.transform.position = pos + Vector3.up * 0.8f; // über dem Marker
        sign.transform.rotation = Quaternion.LookRotation(
            Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized,
            Vector3.up
        );
        sign.transform.localScale = new Vector3(2.5f, 1.5f, 0.2f); // groß + dick

        // Rigidbody weg
        var rb = sign.GetComponent<Rigidbody>();
        if (rb != null) Destroy(rb);

        // 4) Renderer hart erzwingen
        var mr = sign.GetComponent<MeshRenderer>();
        mr.enabled = true;
        mr.shadowCastingMode = ShadowCastingMode.On;
        mr.receiveShadows = true;

        // 5) UNLIT Material erzwingen (damit es niemals "transparent" wird)
        mr.material = MakeUnlitColor(Color.yellow);

        Logger.LogInfo($"### Spawned at {pos} ###");
        Logger.LogInfo($"### Sign renderer enabled={mr.enabled}, shadowCastingMode={mr.shadowCastingMode} ###");
    }

    private static Material MakeUnlitColor(Color c)
    {
        // Erst versuchen: Built-in Unlit/Color
        Shader s = Shader.Find("Unlit/Color");

        // Falls Spiel eigene Unlit Shader hat, bleibt das oft null.
        // Dann fallback auf Standard, aber RenderQueue hochziehen.
        if (s == null)
            s = Shader.Find("Standard");

        var m = new Material(s);

        // Viele Shader nutzen _Color
        if (m.HasProperty("_Color"))
            m.color = c;

        // Sicherstellen, dass es nicht "hinter" irgendwas gerendert wird
        m.renderQueue = 3000; // transparent queue-ish, oft sichtbar

        return m;
    }
}
