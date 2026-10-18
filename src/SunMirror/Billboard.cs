using UnityEngine;

namespace SunMirror;

/// <summary>
/// Блик-вспышка: держится у центра зеркала, но чуть выдвинут К КАМЕРЕ (чтобы диск его не перекрывал),
/// и всегда повёрнут лицом к камере. Работает с любого ракурса (1-е и 3-е лицо).
/// </summary>
internal sealed class Billboard : MonoBehaviour
{
    public float frontOffset = 0.05f;   // чуть вперёд, чтобы диск не перекрывал (не раздувать в 1-м лице)
    public bool allowed;                // включает Plugin — только на время ослепления

    private Renderer? _r;
    private Renderer? _mirrorRend;   // рендерер зеркала — берём центр его геометрии
    private void Awake()
    {
        _r = GetComponent<Renderer>();
        if (transform.parent != null) _mirrorRend = transform.parent.GetComponent<Renderer>();
    }

    private void LateUpdate()
    {
        var cam = Plugin.Instance != null ? Plugin.Instance.ActiveCamera() : Camera.main;
        if (cam == null || transform.parent == null) return;
        Transform mirror = transform.parent;
        // Центр ВИДИМОЙ геометрии, а не точка отсчёта меша: у тарелочки pivot смещён,
        // и блик из-за этого висел в воздухе рядом с диском, а не на нём.
        Vector3 anchor = _mirrorRend != null ? _mirrorRend.bounds.center : mirror.position;

        // Лицевая сторона зеркала — та, что смотрит ОТ игрока (в неё он целится).
        // Считаем от персонажа, а не от камеры: в виде от третьего лица камера сзади.
        var me = Character.localCharacter;
        Vector3 holder = me != null ? me.transform.position : cam.transform.position;
        Vector3 n = mirror.forward;
        if (Vector3.Dot(n, anchor - holder) < 0f) n = -n;

        // Блик виден только зрителю с лицевой стороны (тиммейту), но при этом РАЗВЁРНУТ К КАМЕРЕ —
        // иначе с косых ракурсов он виден с ребра и выглядит как яркая полоска-артефакт.
        Vector3 toCam = cam.transform.position - anchor;
        bool layerOff = Plugin.Instance != null && Plugin.Instance.DebugLayerHidesGlint;
        bool visible = allowed && !layerOff && Vector3.Dot(toCam, n) > 0f;
        if (_r != null && _r.enabled != visible) _r.enabled = visible;
        if (!visible) return;

        transform.position = anchor + n * frontOffset;
        transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, cam.transform.up);
    }
}

/// <summary>Только поворот лицом к камере (без смещения) — для звёздочек.</summary>
internal sealed class FaceCamera : MonoBehaviour
{
    private void LateUpdate()
    {
        var cam = Plugin.Instance != null ? Plugin.Instance.ActiveCamera() : Camera.main;
        if (cam == null) return;
        transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, cam.transform.up);
    }
}

/// <summary>Крутит кольцо звёздочек над головой и сам уничтожается через lifetime.</summary>
internal sealed class StarsSpinner : MonoBehaviour
{
    public float speed = 180f;      // град/сек
    public float lifetime = 5f;
    private float _t;

    private void Update()
    {
        transform.Rotate(Vector3.up, speed * Time.deltaTime, Space.World);
        _t += Time.deltaTime;
        if (_t >= lifetime) Destroy(gameObject);
    }
}
