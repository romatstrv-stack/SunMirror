using System.Collections.Generic;
﻿using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Peak.Afflictions;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Zorro.Core;

namespace SunMirror;

/// <summary>
/// Sun Mirror — «солнечный зайчик».
///   G — взять/убрать зеркало.  F (держать) — прожектор-пятно, водишь мышкой.
///   Навёл на игрока → он прикрывает глаза (эмоут) → через задержку падает.
///   H — DEBUG: эмоут + падение на себе.
/// Зайчик работает только когда солнце реально падает на игрока (день и не в тени).
/// </summary>
[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;
    internal static Plugin Instance = null!;

    private ConfigEntry<Key> _equipKey = null!, _useKey = null!, _selfTestKey = null!, _thirdPersonKey = null!, _giveKey = null!;
    private bool _itemsLogged;
    private bool _lmbLogged;
    private bool _holdingMirror;
    private int _debugLayer;   // 0 всё, 1 без звезды, 2 без блика, 3 без прожектора, 4 всё выкл

    // рескин бинокля → зеркало
    // Отдельный слой для модели зеркала: камера рисует все слои, а спот-лайт зайчика
    // этот слой исключает — иначе зайчик подсвечивает заднюю крышку прямо перед лицом.
    private const int MirrorLayer = 31;
    private ConfigEntry<bool> _reskinEnabled = null!;
    private ConfigEntry<float> _reskinScale = null!, _rOffX = null!, _rOffY = null!, _rOffZ = null!, _rYaw = null!, _rPitch = null!, _rRoll = null!;
    private ConfigEntry<bool> _glintOn = null!;
    private ConfigEntry<float> _glintSize = null!;
    private ConfigEntry<float> _glintFlashMul = null!;
    private Transform? _glintTf;
    private Vector3 _glintBaseScale = Vector3.one;
    private ConfigEntry<float> _viewLowerY = null!, _viewFwd = null!;
    private ConfigEntry<float> _discTilt = null!;
    private ConfigEntry<bool> _realMirrorOn = null!;
    private ConfigEntry<int> _realMirrorRes = null!, _realMirrorEvery = null!;
    private ConfigEntry<bool> _reflProbeOn = null!;
    private ConfigEntry<float> _reflRefresh = null!;
    private ConfigEntry<int> _reflRes = null!;
    private ReflectionProbe? _probe;
    private float _probeTimer;
    private Transform? _discMesh;            // сам диск (для наклона)
    private GameObject? _cloneGo;
    private readonly System.Collections.Generic.List<(Item item, GameObject model)> _reskins = new();
    private ConfigEntry<float> _tpDistance = null!;
    private ConfigEntry<float> _range = null!, _spotSize = null!, _spread = null!, _spotGlow = null!, _heightFactor = null!, _smooth = null!;
    private ConfigEntry<bool> _showSprite = null!, _requireSun = null!;
    private ConfigEntry<float> _fallSeconds = null!, _fallShake = null!;
    private ConfigEntry<bool> _applyStatuses = null!;
    private ConfigEntry<float> _sporesAmount = null!, _drowsyAmount = null!;
    private ConfigEntry<float> _glareStrength = null!;
    private float _glareAlpha, _lastLocalHitTime = -999f;
    // Откуда светят — мировая точка зеркала. Вспышка должна оставаться НА ЗЕРКАЛЕ,
    // а не ехать за взглядом: раньше она рисовалась всегда по центру экрана.
    private Vector3 _glareSourcePos;
    private bool _glareSourceValid;

    // Насколько зеркало попадает в поле зрения: 1 — смотришь прямо на него, 0 — оно вне обзора.
    // Слепить должно ТОЛЬКО то, что ты видишь: раньше засвет держался даже когда отвернулся.
    private float ViewFactor()
    {
        var cam = Camera.main;
        if (!_glareSourceValid || cam == null) return 1f;
        Vector3 dir = _glareSourcePos - cam.transform.position;
        if (dir.sqrMagnitude < 1e-6f) return 1f;
        float d = Vector3.Dot(cam.transform.forward, dir.normalized);
        // Узкий конус: слепит, только когда зеркало реально в центре взгляда.
        // При широком (0.35) эффект включался ещё до того, как ты на него посмотрел.
        return Mathf.Clamp01(Mathf.InverseLerp(0.80f, 0.96f, d));
    }
    private ConfigEntry<bool> _useEmote = null!;
    private ConfigEntry<string> _emoteName = null!;
    private ConfigEntry<float> _stunDelay = null!;
    private ConfigEntry<bool> _blindOn = null!;
    private ConfigEntry<bool> _selfTarget = null!;
    private ConfigEntry<float> _rigAimTime = null!;
    private ConfigEntry<float> _blindAfflictionSeconds = null!;
    private ConfigEntry<bool> _nativeBlindOnSelf = null!;
    private ConfigEntry<float> _spriteBright = null!;
    private ConfigEntry<string> _blindMouth = null!;
    private ConfigEntry<float> _poseFreezeAt = null!;
    private ConfigEntry<float> _netSendEvery = null!;
    private ConfigEntry<bool> _flinch = null!;
    private ConfigEntry<bool> _debugKeys = null!;
    private ConfigEntry<float> _signalGrow = null!;
    private ConfigEntry<float> _signalMax = null!;
    private ConfigEntry<float> _beamLinger = null!;
    private ConfigEntry<float> _maxBlind = null!;
    private ConfigEntry<float> _cooldown = null!;
    private ConfigEntry<float> _flinchForce = null!;
    private string _emoteState = "";
    private ConfigEntry<bool> _showModel = null!;
    private ConfigEntry<float> _targetSize = null!, _heldScale = null!;
    private ConfigEntry<float> _offX = null!, _offY = null!, _offZ = null!;
    private ConfigEntry<float> _yaw = null!, _pitch = null!, _roll = null!;

    private GameObject? _mirrorPrototype, _held, _spotQuad, _spotLightGo, _signal;
    private Harmony? _harmony;
    private Light? _spotLight, _signalLight;
    private Material[]? _heldMats;
    private Camera? _tpCam;
    private bool _tpActive;
    private float _selfDemoUntil;
    private bool _bonesLogged;
    private bool _triedLoad, _emotesLogged;

    private Vector3 _spotPos;
    private bool _spotHasPos;
    private float _lastSunLog;
    private Character? _victim;

    private void Awake()
    {
        Log = Logger;
        Instance = this;

        _equipKey      = Config.Bind("Controls", "EquipKey", Key.G, "Взять/убрать зеркало.");
        _useKey        = Config.Bind("Controls", "UseKey", Key.F, "Держать — светить зайчиком.");
        _selfTestKey   = Config.Bind("Controls", "SelfTestKey", Key.H, "DEBUG: эмоут+падение на себе.");
        _thirdPersonKey= Config.Bind("Controls", "ThirdPersonKey", Key.C, "DEBUG: вид со стороны (посмотреть на себя).");
        _giveKey       = Config.Bind("Controls", "GiveBinocularsKey", Key.B, "DEBUG: выдать бинокль в руку (тест рескина).");
        _tpDistance    = Config.Bind("Controls", "ThirdPersonDistance", 2.8f, "Дистанция камеры со стороны.");
        _range         = Config.Bind("Spot", "Range", 150f, "Дальность (метры).");
        _spotSize      = Config.Bind("Spot", "Size", 0.5f, "Базовый диаметр пятна (вблизи).");
        _spread        = Config.Bind("Spot", "SpreadPerMeter", 0.04f, "Прибавка к диаметру на метр дистанции (рассеяние).");
        _spotGlow      = Config.Bind("Spot", "Glow", 14f, "Яркость прожектора.");
        _heightFactor  = Config.Bind("Spot", "HeightFactor", 2f, "Высота источника = размер×это (больше = мягче край).");
        _smooth        = Config.Bind("Spot", "SmoothSpeed", 22f, "Сглаживание хода пятна (больше = резче/быстрее).");
        _showSprite    = Config.Bind("Spot", "ShowSprite", true, "Яркий спрайт в центре пятна.");
        _spriteBright  = Config.Bind("Spot", "SpriteBrightness", 2.4f, "Яркость зайчика. Больше 1 — уходит в пересвет и ловит блум; на ярком песке меньше 1.5 не читается.");
        _requireSun    = Config.Bind("Spot", "RequireSunlight", true, "Светить только когда солнце падает на игрока (день и не в тени).");
        _fallSeconds   = Config.Bind("Effect", "FallSeconds", 1.6f, "Секунд рэгдолла.");
        _fallShake     = Config.Bind("Effect", "FallShake", 0.6f, "Тряска экрана.");
        _applyStatuses = Config.Bind("Effect", "ApplyStatuses", false, "Spores/Drowsy (на незнакомцах может не сработать).");
        _sporesAmount  = Config.Bind("Effect", "SporesAmount", 0.5f, "Кол-во Spores.");
        _drowsyAmount  = Config.Bind("Effect", "DrowsyAmount", 0.4f, "Кол-во Drowsy.");
        _glareStrength = Config.Bind("Effect", "GlareStrength", 0.75f, "Сила засвета экрана, когда светят в тебя (0..1).");
        _useEmote      = Config.Bind("Emote", "UseEmote", true, "Эмоут-реакция перед станом.");
        _emoteName     = Config.Bind("Emote", "EmoteName", "ну хватит", "Имя эмоута — русское или английское.");
        _stunDelay     = Config.Bind("Emote", "StunDelaySeconds", 5f, "Секунд светить в глаза до падения (эмоут+звёздочки всё это время).");
        _blindHoldSeconds = Config.Bind("Emote", "BlindHoldSeconds", 6f, "Сколько держится ослепление после попадания зайчика в глаза.");
        _poseFreezeDelay = Config.Bind("Emote", "PoseFreezeDelay", 0.55f, "Через сколько секунд после начала эмоута заморозить позу (руки у глаз).");
        _rigAimTime    = Config.Bind("Debug", "RigAimSeconds", 1.0f, "Сколько зеркало-стенд наводит зайчика до ослепления.");
        _debugKeys = Config.Bind("Debug", "DebugKeys", false, "Отладочные клавиши (B K L J C H U N P M O F8-F11 и подстройка наклона). По умолчанию выключены: обычному игроку они не нужны.");
        _selfTarget    = Config.Bind("Debug", "SelfTarget", false, "Луч всегда бьёт в тебя — тест эффекта без второго игрока (клавиша J).");
        _blindOn       = Config.Bind("Emote", "BlindStars", true, "Ослепление со звёздочками над головой (Affliction_Blind).");
        _blindAfflictionSeconds = Config.Bind("Emote", "BlindAfflictionSeconds", 2.5f, "Мин. длительность нативного Affliction_Blind за одно обновление (сетевое, видно всем).");
        _netSendEvery = Config.Bind("Emote", "NetSendEverySeconds", 0.25f, "Как часто слать ослепление по сети, пока луч на цели (каждый кадр — флуд).");
        _flinch = Config.Bind("Emote", "VanillaFlinch", true, "Отдёргивание головы при попадании. Единственное, что физически чувствует игрок БЕЗ мода: сетевой метод самой игры.");
        _signalGrow = Config.Bind("Spot", "SignalGrowPerMeter", 0.02f, "Насколько вспышка на зеркале растёт с каждым метром — чтобы её было видно издалека.");
        _signalMax = Config.Bind("Spot", "SignalMaxSize", 3.5f, "Потолок размера вспышки. Без него на дальней дистанции зеркало заслоняет пол-экрана.");
        _maxBlind = Config.Bind("Emote", "MaxBlindSeconds", 5f, "Потолок непрерывного ослепления. Дольше держать нельзя: иначе зеркалом можно просто выключить человека из игры.");
        _beamLinger = Config.Bind("Emote", "BeamLingerSeconds", 3f, "Сколько ослепление держится после того, как луч ушёл с глаз. Он же задаёт, скольких можно держать разом, проводя лучом по группе.");
        _cooldown = Config.Bind("Emote", "CooldownSeconds", 4f, "Пауза, в течение которой ту же цель ослепить нельзя. Защита от удержания в цикле.");
        _flinchForce = Config.Bind("Emote", "VanillaFlinchForce", 14f, "Сила отдёргивания головы. Больше 30 валит персонажа — нам нужно вздрагивание, а не нокдаун.");
        // Нативное ослепление игры рисует СВОЮ пелену на весь экран (IllegalScreenEffect, статус "BLIND").
        // Чужому игроку это и нужно — он без мода, иначе не почувствует ничего. А себе — нет:
        // именно эта пелена и была «туманом», который не снимался ни слоями [O], ни клавишей U.
        _nativeBlindOnSelf = Config.Bind("Emote", "NativeBlindOnSelf", false, "Вешать нативное ослепление игры (с экранной пеленой) на СЕБЯ. Для тестов обычно false — слепит только звезда.");
        _blindMouth = Config.Bind("Emote", "BlindMouthName", "", "Рот ослеплённого: имя варианта из Customization.mouths (список печатает клавиша N). Пусто — рот не менять.");
        _poseFreezeAt = Config.Bind("Emote", "PoseFreezeAt", 0.35f, "Кадр клипа эмоута (0..1), на котором держим позу. Подбор в игре: F9 раньше, F10 позже.");
        _showModel     = Config.Bind("Model", "ShowModel", true, "Показывать модель зеркала в руке.");
        _targetSize    = Config.Bind("Model", "BaseSize", 0.3f, "Габарит модели (метры).");
        _heldScale     = Config.Bind("Model", "HeldScale", 1f, "Множитель масштаба.");
        _offX          = Config.Bind("Model", "HeldOffsetX", 0.18f, "Смещение: вправо.");
        _offY          = Config.Bind("Model", "HeldOffsetY", -0.12f, "Смещение: вниз.");
        _offZ          = Config.Bind("Model", "HeldOffsetZ", 0.45f, "Смещение: вперёд.");
        _yaw           = Config.Bind("Model", "HeldYaw", 0f, "Поворот Y.");
        _pitch         = Config.Bind("Model", "HeldPitch", 0f, "Поворот X.");
        _roll          = Config.Bind("Model", "HeldRoll", 0f, "Поворот Z.");

        _reskinEnabled = Config.Bind("Reskin", "Enabled", true, "Превращать бинокль в зеркало (рескин).");
        _reskinScale   = Config.Bind("Reskin", "Scale", 0.22f, "Габарит модели зеркала на бинокле (метры).");
        _rOffX         = Config.Bind("Reskin", "OffsetX", 0f, "Смещение модели: вправо.");
        _rOffY         = Config.Bind("Reskin", "OffsetY", 0f, "Смещение модели: вверх.");
        _rOffZ         = Config.Bind("Reskin", "OffsetZ", 0f, "Смещение модели: вперёд.");
        _rYaw          = Config.Bind("Reskin", "Yaw", 0f, "Поворот Y.");
        _rPitch        = Config.Bind("Reskin", "Pitch", 0f, "Поворот X.");
        _rRoll         = Config.Bind("Reskin", "Roll", 0f, "Поворот Z.");
        _glintOn       = Config.Bind("Reskin", "Glint", true, "Солнечный блик-вспышка на зеркале.");
        _glintSize     = Config.Bind("Reskin", "GlintSize", 0.5f, "Размер блика в покое (метры).");
        _glintFlashMul = Config.Bind("Reskin", "GlintFlashMultiplier", 5f, "Во сколько раз блик раздувается при наводке на тиммейта (вспышка ослепления).");
        _viewLowerY    = Config.Bind("Reskin", "ViewLowerY", 0.45f, "Опустить хват (руки+зеркало) в 1-м лице вниз, метры.");
        _viewFwd       = Config.Bind("Reskin", "ViewForward", 0.1f, "Сдвинуть хват вперёд в 1-м лице, метры.");
        // 65° подобрано в игре и признано рабочим — вопрос закрыт, не трогать без запроса.
        _discTilt      = Config.Bind("Reskin", "DiscTilt", 65f, "Наклон тарелочки, чтобы зеркальная сторона смотрела вперёд (градусы).");
        // ПО УМОЛЧАНИЮ ВЫКЛЮЧЕНО: съёмка кубической карты (6 граней) в открытой локации PEAK
        // оказалась слишком дорогой даже раз в 1.5 с — давала тяжёлые лаги.
        _realMirrorOn    = Config.Bind("Reflection", "RealMirror", true, "Настоящее зеркало шейдером игры GD/Mirror (камера в текстуру). Дороже всего.");
        _realMirrorRes   = Config.Bind("Reflection", "RealMirrorResolution", 256, "Разрешение текстуры отражения (64..512). Меньше = дешевле.");
        _realMirrorEvery = Config.Bind("Reflection", "RealMirrorEveryNthFrame", 2, "Обновлять отражение раз в N кадров. Больше = дешевле.");
        _reflProbeOn   = Config.Bind("Reflection", "Enabled", false, "Отражение окружения через кубическую карту. ДОРОГО, может лагать.");
        _reflRefresh   = Config.Bind("Reflection", "RefreshSeconds", 1.5f, "Как часто пересниматькубическую карту. Больше = дешевле.");
        _reflRes       = Config.Bind("Reflection", "Resolution", 128, "Разрешение кубической карты (64/128/256). Меньше = дешевле.");

        // значение могло убежать за разумные пределы при подстройке клавишами — возвращаем в диапазон
        _viewLowerY.Value = Mathf.Clamp(_viewLowerY.Value, 0f, 0.8f);
        _viewFwd.Value    = Mathf.Clamp(_viewFwd.Value, -0.5f, 0.5f);

        Camera.onPreCull += OnCamPreCull;   // сдвиг хвата — позже анимации

        try { _harmony = new Harmony(Id); _harmony.PatchAll(); Log.LogInfo("Harmony patched (reskin + suppress zoom)."); }
        catch (System.Exception e) { Log.LogWarning($"Harmony patch failed: {e.GetType().Name}: {e.Message}"); }

        Log.LogInfo($"Plugin {Name} loaded. Equip={_equipKey.Value}, Use(hold)={_useKey.Value}, SelfTest={_selfTestKey.Value}");
    }

    // Требуется для hot-reload (AutoReload/ScriptEngine): полностью убираем за собой,
    // иначе после перезагрузки плагина останутся висеть патчи, объекты и залипшее движение.
    private void OnDestroy()
    {
        try
        {
            Camera.onPreCull -= OnCamPreCull;   // иначе после hot-reload останется висеть подписка
            RestoreAllBlinds();                // вернуть движение и глаза всем жертвам
            EmergencyUnstick();                // страховка: движение точно не осталось нулевым

            // Удаляем ВСЕ наши объекты в сцене по имени. Раньше чистились только объекты
            // от бинокля, а прослойки/блики на тарелочке оставались — за 13 перезагрузок
            // плагина мусор накапливался и давал лаги, не зависящие от предмета в руках.
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (t == null || !t.name.StartsWith("SunMirror")) continue;
                if (t.name == "SunMirrorTilt")
                {
                    // прослойку убираем аккуратно: возвращаем диск на его место в иерархии
                    for (int i = t.childCount - 1; i >= 0; i--) t.GetChild(i).SetParent(t.parent, true);
                }
                Object.Destroy(t.gameObject);
            }
            // Наши компоненты висят на ЧУЖИХ объектах — на персонажах и ботах, —
            // поэтому уборка по имени объекта их не ловит. Ищем ПО ИМЕНИ ТИПА:
            // после горячей перезагрузки старый компонент относится к другой сборке,
            // и обычный GetComponent<T>() его уже не находит.
            DestroyOurComponents();

            _reskins.Clear();
            _mirroredCovers.Clear();

            // Созданные нами материалы и текстуры Unity сам не соберёт — удаляем явно.
            if (_mirrorMat != null) { Object.Destroy(_mirrorMat); _mirrorMat = null; }
            if (_flareTex != null)  { Object.Destroy(_flareTex);  _flareTex = null; }
            if (_reflTex != null)   { Object.Destroy(_reflTex);   _reflTex = null; }
            _donorMat = null; _donorSearched = false;
            _discMesh = null; _tiltPivot = null; _glintTf = null;

            // наши объекты (часть с DontDestroyOnLoad — сами не исчезнут)
            foreach (var go in new[] { _mirrorPrototype, _held, _spotQuad, _spotLightGo, _signal })
                if (go != null) Object.Destroy(go);
            if (_tpCam != null) Object.Destroy(_tpCam.gameObject);
            if (_probe != null) { Object.Destroy(_probe.gameObject); _probe = null; }
            if (_mirrorCam != null) { Object.Destroy(_mirrorCam.gameObject); _mirrorCam = null; }
            if (_mirrorRT != null) { _mirrorRT.Release(); Object.Destroy(_mirrorRT); _mirrorRT = null; }
            if (_testRig != null) { Object.Destroy(_testRig); _testRig = null; }
            _mirrorPrototype = _held = _spotQuad = _spotLightGo = _signal = null;
            _tpCam = null; _heldMats = null; _cloneGo = null;

            _harmony?.UnpatchSelf();           // снять Harmony-патчи (иначе зум останется заглушен)
            _harmony = null;

            Log.LogInfo("SunMirror unloaded cleanly.");
        }
        catch (System.Exception e) { Log.LogWarning($"OnDestroy: {e.GetType().Name}: {e.Message}"); }
    }

    private float _fpsAccum; private int _fpsFrames; private float _selfMs;

    private void Update()
    {
        // Замер: средний кадр и сколько в нём занимает НАША работа.
        // Лаги были сквозной темой проекта, а числа до сих пор ни одного.
        bool measure = _debugKeys.Value;
        long t0 = 0;
        if (measure) { _fpsAccum += Time.unscaledDeltaTime; _fpsFrames++; t0 = System.Diagnostics.Stopwatch.GetTimestamp(); }

        var kb = Keyboard.current;
        if (kb == null) return;
        // Вся отладка — за одним выключателем. Перед релизом Debug/DebugKeys = false,
        // и мод остаётся только с игровым поведением: подобрал тарелочку, жмёшь ЛКМ.
        if (_debugKeys.Value) HandleDebugKeys(kb);
        UpdateTestRig();
        UpdateReflectionProbe();
        UpdateRealMirror();
        ApplyDiscTilt();   // применяем и здесь тоже: прослойка наша, перезаписывать её нечему
        EnsureNetReceivers();
        ReskinAllBinoculars();
        SyncReskins();

        // Засвет: вспыхивает быстро, гаснет быстро. Зависимость от взгляда учитываем ЗДЕСЬ,
        // а не только при отрисовке — иначе яркость жила своей жизнью и тянулась
        // ещё пару секунд после того, как ты отвернулся.
        float viewNow = ViewFactor();
        float target = (Time.time - _lastLocalHitTime < 0.15f)
            ? Mathf.Clamp01(_glareStrength.Value) * viewNow
            : 0f;
        _glareAlpha = Mathf.MoveTowards(_glareAlpha, target, Time.deltaTime * (target > _glareAlpha ? 9f : 7f));

        if (measure)
            _selfMs += (float)(System.Diagnostics.Stopwatch.GetTimestamp() - t0)
                     * 1000f / System.Diagnostics.Stopwatch.Frequency;
    }


    // Фейковое «отражение»: небо сверху, светлый горизонт, земля снизу + солнечное пятно.
    // Настоящих отражений шейдер игры не считает, а это стоит почти ноль и читается как зеркало.
    private Texture2D? _reflTex;
    internal Texture2D GetFakeReflectionTex()
    {
        if (_reflTex != null) return _reflTex;
        int w = 128, h = 128;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        // ПОЛИРОВАННОЕ СЕРЕБРО. Металлик без отражений рисовать нечем — шейдер выдаёт плоскую
        // заливку, и получается белый пластик. Поэтому металл рисуем сами: холодный градиент
        // от тёмной стали снизу через яркую полосу к светло-голубому верху. Никакой земли —
        // коричневый низ прошлой версии и давал «неполированную бронзу».
        // Зеркало отражает ЧЁТКУЮ картинку, а не рябь: прошлая версия с синусоидой читалась
        // как рифлёный пластик. Рисуем отражённый мир — резкий горизонт, небо, земля, солнце.
        var skyHi  = new Color(0.55f, 0.74f, 0.96f);   // зенит
        var skyLo  = new Color(0.97f, 0.99f, 1.00f);   // почти белое небо у горизонта
        var grndHi = new Color(0.52f, 0.53f, 0.58f);   // земля у горизонта (светлее — дальний план)
        var grndLo = new Color(0.24f, 0.25f, 0.30f);   // земля вблизи
        const float horizon = 0.52f;
        const float blend   = 0.07f;                   // мягкость линии горизонта

        for (int y = 0; y < h; y++)
        {
            float v = y / (float)(h - 1);          // 0 низ .. 1 верх
            Color sky   = Color.Lerp(skyLo, skyHi, Mathf.InverseLerp(horizon, 1f, v));
            Color grnd  = Color.Lerp(grndLo, grndHi, Mathf.InverseLerp(0f, horizon, v));
            // мягкий переход вместо резкой границы — она читалась как заливка в два цвета
            float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(horizon - blend, horizon + blend, v));
            Color c = Color.Lerp(grnd, sky, t);

            for (int x = 0; x < w; x++)
            {
                float u = x / (float)(w - 1);
                // солнце в небе + его отблеск-дорожка вниз от горизонта, как в воде
                float dx = (u - 0.34f), dy = (v - 0.72f);
                float sun  = Mathf.Exp(-(dx * dx * 26f + dy * dy * 90f));
                float path = (v < horizon) ? Mathf.Exp(-dx * dx * 90f) * Mathf.Exp(-(horizon - v) * 7f) * 0.45f : 0f;
                tex.SetPixel(x, y, Color.Lerp(c, Color.white, Mathf.Clamp01(sun + path)));
            }
        }
        tex.Apply();
        _reflTex = tex; return tex;
    }

    private Texture2D? _flareTex;
    internal Texture2D GetFlareTex()
    {
        if (_flareTex != null) return _flareTex;
        int s = 256;
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false, false);
        float rmax = s / 2f;
        var px = new Color[s * s];
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float dx = (x - rmax) / rmax, dy = (y - rmax) / rmax;
            float d = Mathf.Sqrt(dx * dx + dy * dy);           // 0..~1.41
            float core = Mathf.Exp(-d * d * 140f);             // компактное яркое ядро
            // Гало нет: именно оно заливало промежутки между лучами молочной мутью.
            float ang = Mathf.Atan2(dy, dx);
            float spikes = Mathf.Pow(Mathf.Abs(Mathf.Cos(ang * 2f)), 120f)
                         + Mathf.Pow(Mathf.Abs(Mathf.Cos((ang - 0.7854f) * 2f)), 160f) * 0.65f;
            spikes *= Mathf.Exp(-d * 1.6f);                    // медленное затухание = длинные лучи
            float a = Mathf.Clamp01(core + spikes);
            // Жёсткая обрезка по кругу: без неё слабые края квада заливали весь экран.
            // ВНИМАНИЕ: Mathf.SmoothStep(from,to,t) в Unity — это ИНТЕРПОЛЯЦИЯ между from и to,
            // а НЕ пороговая smoothstep из шейдеров. Запись SmoothStep(0.92f,1f,d) возвращала
            // 0.92 в центре → вся звезда умножалась на 0.08 и была почти невидимой.
            // Правильно: сначала InverseLerp даёт 0..1 по краю, потом сглаживаем.
            a *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.92f, 1.0f, d));
            px[y * s + x] = new Color(1f, 1f, 1f, a);
        }
        tex.SetPixels(px);
        tex.Apply(false, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        _flareTex = tex; return tex;
    }

    private void HandleDebugKeys(Keyboard kb)
    {
        if (kb[_selfTestKey.Value].wasPressedThisFrame) SelfRagdollTest();
        if (kb[_thirdPersonKey.Value].wasPressedThisFrame) ToggleThirdPerson();
        if (kb[_giveKey.Value].wasPressedThisFrame) GiveBinoculars();
        if (kb[Key.N].wasPressedThisFrame) { ScanShinyMaterials(); DumpMouths(); _emotesLogged = false; LogEmotesOnce(); ProbeNetwork(); }
        // (LogGuidebookOnce убран: он сканировал сцену КАЖДЫЙ кадр и никогда не находил
        //  руководство после перехода на тарелочку — это и были лаги.)
        if (kb[Key.U].wasPressedThisFrame) EmergencyUnstick();   // застрял (старый баг) — отлипнуть
        if (kb[Key.P].wasPressedThisFrame) DebugBoardFlight();  // сразу вылететь на остров (отладка)
        if (kb[Key.M].wasPressedThisFrame) DebugNoon();
        if (kb[Key.F11].wasPressedThisFrame) DebugSpawnBot();  // бот рядом — цель для теста сети
        if (kb[Key.F12].wasPressedThisFrame) DebugKillVictim();  // убить ослеплённого — проверка уборки         // перевести время на полдень (отладка)
        // Живая подстройка кадра позы: F9 раньше, F10 позже.
        // Config.Save() обязателен — иначе значение потеряется при следующей пересборке.
        bool poseMinus = kb[Key.F9].wasPressedThisFrame;
        bool posePlus  = kb[Key.F10].wasPressedThisFrame;
        if (poseMinus || posePlus)
        {
            _poseFreezeAt.Value = Mathf.Clamp01(_poseFreezeAt.Value + (posePlus ? 0.05f : -0.05f));
            Config.Save();
            Log.LogWarning($"PoseFreezeAt = {_poseFreezeAt.Value:0.00} (кадр клипа эмоута)");
        }
        // O — поочерёдно отключаем слои, чтобы НАЙТИ источник засветки, а не гадать.
        if (kb[Key.O].wasPressedThisFrame || kb[Key.F8].wasPressedThisFrame || kb[Key.Numpad0].wasPressedThisFrame || kb[Key.Digit0].wasPressedThisFrame)
        {
            _debugLayer = (_debugLayer + 1) % 5;
            string[] names = { "все слои включены", "БЕЗ экранной звезды", "БЕЗ блика на зеркале",
                               "БЕЗ прожектора", "ВСЕ мои эффекты выключены" };
            Log.LogWarning($"[O] Режим {_debugLayer}: {names[_debugLayer]}");
            if (_debugLayer >= 3) HideSpot();
        }

        if (kb[Key.K].wasPressedThisFrame) ToggleTestRig();   // K — поставить/убрать зеркало
        if (kb[Key.L].wasPressedThisFrame)                    // L — зеркало начинает ловить зайчика
        {
            _rigAiming = !_rigAiming; _rigAim = 0f;
            if (!_rigAiming) RestoreAllBlinds();
            Log.LogInfo($"Стенд наводит зайчика: {_rigAiming}");
        }
        if (kb[Key.J].wasPressedThisFrame)
        {
            _selfTarget.Value = !_selfTarget.Value; Config.Save();
            Log.LogInfo($"SelfTarget = {_selfTarget.Value} (луч бьёт в себя — тест без второго игрока)");
        }

        // живая подстройка посадки хвата (без пересборки): PageUp/PageDown — выше/ниже, Home/End — ближе/дальше
        // Тарелочку держат низко, регулировка высоты не нужна — клавиши отданы под НАКЛОН диска.
        // Config.Save() обязателен: каждая пересборка перезагружает плагин, и он перечитывает
        // конфиг с диска — без сохранения подстройка терялась при следующем билде.
        // Клавиш-дублей несколько: у тебя не срабатывали и PageUp/PageDown, и скобки.
        bool tiltMinus = kb[Key.LeftBracket].wasPressedThisFrame  || kb[Key.Minus].wasPressedThisFrame  || kb[Key.Numpad4].wasPressedThisFrame;
        bool tiltPlus  = kb[Key.RightBracket].wasPressedThisFrame || kb[Key.Equals].wasPressedThisFrame || kb[Key.Numpad6].wasPressedThisFrame;
        if (tiltMinus || tiltPlus)
        {
            _discTilt.Value += tiltPlus ? 5f : -5f;
            Config.Save();
            Log.LogInfo($"DiscTilt = {_discTilt.Value:0}");
        }
    }

    private void OnGUI()
    {
        if (_tpActive) return;            // в виде сбоку не засвечиваем (смотрим как тиммейт)
        if (_debugLayer == 1 || _debugLayer == 4) return;   // [O] диагностика: звезда выключена
        if (_glareAlpha <= 0.01f) return;
        var prev = GUI.color;
        int w = Screen.width, h = Screen.height;

        // Дымки НЕТ: ослепляет только звезда. Заливка экрана делала картинку мутной,
        // а не слепящей — по ощущению это был туман, а не солнце в глаза.

        // Звезда рисуется В ТОЧКЕ ЗЕРКАЛА, а не по центру экрана. Раньше она была
        //    привязана к центру и ехала за взглядом — отвернулся, а вспышка следом.
        float cx = w * 0.5f, cy = h * 0.5f;
        var cam = Camera.main;
        if (_glareSourceValid && cam != null)
        {
            Vector3 sp = cam.WorldToScreenPoint(_glareSourcePos);
            if (sp.z <= 0f) { GUI.color = prev; return; }   // зеркало позади — не слепит вообще
            cx = sp.x;
            cy = h - sp.y;                                  // экранные координаты GUI идут сверху
        }

        // Компактная звезда в один проход. Раньше было 1.5 экрана и ДВА прохода —
        // лучи сливались в сплошную засветку, и это читалось как туман.
        // Звезда снова ЯРКАЯ и крупная — слепить должна именно она.
        float size = Mathf.Max(w, h) * 1.6f;
        var rect = new Rect(cx - size / 2f, cy - size / 2f, size, size);
        GUI.color = new Color(1f, 1f, 0.96f, Mathf.Clamp01(_glareAlpha));
        GUI.DrawTexture(rect, GetFlareTex(), ScaleMode.StretchToFill, true);
        GUI.DrawTexture(rect, GetFlareTex(), ScaleMode.StretchToFill, true);   // второй проход — ядро добела

        GUI.color = prev;
    }

    // Пятно двигаем в LateUpdate — после того как камера обновилась (иначе дёргается).
    private void LateUpdate()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        // основной триггер — держим зеркало (есть viewmodel-клон) и жмём ЛКМ; F — debug-дубль (старый проп)
        bool lmb = mouse != null && mouse.leftButton.isPressed;
        bool fFallback = _held != null && kb != null && kb[_useKey.Value].isPressed;
        bool active = (_holdingMirror && lmb) || fFallback;

        if (lmb && !_lmbLogged) { _lmbLogged = true;
            Log.LogInfo($"LMB press: holdingMirror={_holdingMirror} active={active}"); }
        if (!lmb) _lmbLogged = false;   // сбрасываем — каждое нажатие логируется

        if (active) UpdateSpot();
        else { HideSpot(); ClearTarget(); _spotHasPos = false; }

        // сигнальный блик — ТОЛЬКО когда наводишь на тиммейта (он замечает тебя издалека)
        var me = Character.localCharacter;
        bool demo = Time.time < _selfDemoUntil && me != null;   // H-демо: вся визуалка на себе
        // Сигнальное зеркало: вспышка горит ВСЁ время, пока пускаешь зайчик, а не только
        // когда луч уже зацепил игрока. Иначе тиммейт издалека не поймёт, что его зовут:
        // чтобы вспышка появилась, надо было сначала попасть ему в глаза.
        bool flash = (active && me != null && _spotHasPos) || demo;
        if (flash && me != null) { ShowSignal(me); if (demo) _lastLocalHitTime = Time.time; }
        else HideSignal();
        SetMirrorFlash(flash);   // само зеркало вспыхивает свечением (вспышка на поверхности)

        UpdateThirdPerson(me);
        LowerViewmodel();
        TickBlind();
    }

    // Опускаем весь хват (руки+зеркало) в 1-м лице, чтобы не перекрывал центр экрана.
    private void LowerViewmodel()
    {
        // Работа перенесена в OnCamPreCull: LateUpdate слишком рано — анимация рук
        // пересчитывает кости после него и затирает сдвиг.
    }

    // Вызывается непосредственно перед отрисовкой кадра — позже любой анимации.
    private int _lastShiftFrame = -1;
    private void OnCamPreCull(Camera cam)
    {
        if (cam == null || cam != Camera.main) return;         // только основная камера
        if (_lastShiftFrame == Time.frameCount) return;        // один раз за кадр
        _lastShiftFrame = Time.frameCount;

        // Фиксация взгляда нужна и когда зеркала в руках нет (тест со стендом),
        // поэтому делаем это ДО проверки на предмет в руках.
        if (_lockLook) cam.transform.rotation = _lockedRot;

        if (_cloneGo == null) return;

        ApplyDiscTilt();   // ДО раннего выхода: иначе при нулевом сдвиге наклон не применялся

        var ct = cam.transform;
        Vector3 delta = (-ct.up * _viewLowerY.Value) + (ct.forward * _viewFwd.Value);
        if (delta.sqrMagnitude < 1e-8f) return;

        _cloneGo.transform.position += delta;                 // зеркало и всё статичное

        // Руки берём из кэша: GetComponentsInChildren тут выделял массив КАЖДЫЙ кадр
        // по всей иерархии зеркала (27 мешей) — это и были лаги.
        if (_handL != null) _handL.position += delta;
        if (_handR != null) _handR.position += delta;
    }
    private Transform? _handL, _handR;

    // Камера, с которой реально смотрим (для билборда блика): debug-вид сбоку или основная.
    internal bool DebugLayerHidesGlint => _debugLayer == 2 || _debugLayer == 4;

    internal Camera? ActiveCamera() => (_tpActive && _tpCam != null) ? _tpCam : Camera.main;

    // ── ТЕСТОВЫЙ СТЕНД: зеркало висит в воздухе и светит в тебя ──
    // Воспроизводит ситуацию «тиммейт светит тебе в глаза»: ты видишь и вспышку
    // на поверхности зеркала со стороны, и ослепление своими глазами одновременно.
    private GameObject? _testRig;
    private bool _rigAiming;      // L — зеркало начало ловить зайчика
    private float _rigAim;        // 0..1 — насколько пятно дошло до глаз

    private void ToggleTestRig()
    {
        if (_testRig != null)
        {
            Object.Destroy(_testRig);
            _testRig = null;
            RestoreAllBlinds();
            Log.LogInfo("Тестовый стенд убран.");
            return;
        }

        var me = Character.localCharacter;
        var cam = Camera.main;
        if (me == null || cam == null) { Log.LogWarning("Стенд: нет игрока или камеры."); return; }

        // плоский диск перед игроком, на уровне глаз
        _testRig = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        _testRig.name = "SunMirrorTestRig";
        var col = _testRig.GetComponent<Collider>(); if (col != null) Object.Destroy(col);
        _testRig.transform.position = cam.transform.position + cam.transform.forward * 4f;
        _testRig.transform.localScale = new Vector3(0.6f, 0.02f, 0.6f);   // тонкий блин

        // Стенд намеренно МАТОВЫЙ и без блика: зеркальный материал с текстурой отражения
        // и яркий блик раздувались свечением игры в сплошную засветку. Стенд — инструмент
        // для проверки РЕАКЦИИ персонажа, красивая поверхность ему не нужна.
        var rr = _testRig.GetComponent<Renderer>();
        var plain = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (plain != null) rr.material = new Material(plain) { color = new Color(0.55f, 0.58f, 0.62f) };

        Object.DontDestroyOnLoad(_testRig);
        Log.LogInfo("Тестовый стенд поставлен: отойди и посмотри на него — он светит тебе в глаза.");
    }

    private void UpdateTestRig()
    {
        if (_testRig == null) return;
        var me = Character.localCharacter;
        if (me == null) return;

        // Диск разворачиваем плоскостью к ГЛАЗАМ игрока. У цилиндра плоские грани
        // перпендикулярны оси Y, поэтому ось Y должна смотреть прямо на камеру.
        var cam = Camera.main;
        Vector3 eye = cam != null ? cam.transform.position : me.transform.position + Vector3.up * 1.4f;
        Vector3 toEye = eye - _testRig.transform.position;
        if (toEye.sqrMagnitude > 1e-6f)
            _testRig.transform.rotation = Quaternion.FromToRotation(Vector3.up, toEye.normalized);

        _glareSourcePos = _testRig.transform.position;   // вспышка привязана к этому зеркалу
        _glareSourceValid = true;

        // Зеркало «ловит зайчика» не мгновенно: после нажатия L оно наводится на игрока
        // примерно секунду, и только когда пятно дошло до глаз — начинается ослепление.
        if (!_rigAiming) { _rigAim = 0f; HideSpot(); return; }
        _rigAim = Mathf.MoveTowards(_rigAim, 1f, Time.deltaTime / Mathf.Max(0.1f, _rigAimTime.Value));

        // Сначала по персонажу ползёт СВЕТОВОЕ ПЯТНО — от ног к глазам,
        // и только когда доползло, начинается ослепление.
        Vector3 feet = me.transform.position;
        Vector3 eyes = feet + Vector3.up * 1.55f;
        Vector3 spot = Vector3.Lerp(feet, eyes, _rigAim);
        Vector3 toRig = (_testRig.transform.position - spot).normalized;

        if (_rigAim < 1f)
        {
            // Пятно ползёт по телу — показываем прожектор.
            if (_debugLayer < 3) ShowSpot(spot + toRig * 0.25f, toRig, 0.7f); else HideSpot();
            return;                                     // ещё не дошло до глаз — не слепит
        }

        // Дошло до глаз — прожектор ГАСИМ. Он стоит в 25 см от лица с яркостью 14
        // и засвечивал весь кадр через свечение игры: это и был «туман»,
        // который не убирался правками экранной вспышки.
        HideSpot();

        // Ослепление (поза, глаза, обездвиживание) держится, ПОКА зеркало наведено,
        // и не зависит от того, куда ты смотришь: луч бьёт в лицо физически.
        // От взгляда зависит только засвет экрана — его считает ViewFactor в Update.
        // Держим эффект ФИКСИРОВАННОЕ время, а не «пока обновляется каждый кадр».
        // Раньше любой сбой обновления (в т.ч. случайное переключение L) мгновенно снимал позу.
        // Через тот же сетевой путь, что и боевой луч, — чтобы стенд проверял
        // настоящий код, а не укороченный локальный дублёр.
        SendBlind(me, _blindHoldSeconds.Value);
        _lastLocalHitTime = Time.time;
    }

    // === Тест: выдать реальный предмет (бинокль) в руку — для рескина в зеркало ===
    private void GiveBinoculars()
    {
        var me = Character.localCharacter;
        if (me == null) { Log.LogWarning("GiveBinoculars: localCharacter == null"); return; }

        LogItemsOnce();

        // Тарелочка (Frisbee, id 99) — круглая, держится двумя руками ВНИЗУ, обзор свободен,
        // и у неё нет действия на ЛКМ (только бросок на Q) — глушить ничего не нужно.
        string[] tryNames = { "Frisbee" };
        var items = me.refs != null ? me.refs.items : null;
        if (items == null) { Log.LogWarning("GiveBinoculars: refs.items == null"); return; }

        foreach (var nm in tryNames)
        {
            try
            {
                items.SpawnItemInHand(nm);
                Log.LogInfo($"GiveBinoculars: SpawnItemInHand(\"{nm}\") вызван без ошибки.");
                return;
            }
            catch (System.Exception e)
            {
                Log.LogWarning($"GiveBinoculars: '{nm}' -> {e.GetType().Name}: {e.Message}");
            }
        }
        Log.LogWarning("GiveBinoculars: ни одно имя не подошло — смотри список предметов в логе выше.");
    }

    // Разовый дамп структуры руководства: какие меши/материалы, чтобы понять, что делать зеркальным.
    private void LogItemsOnce()
    {
        if (_itemsLogged) return;
        _itemsLogged = true;
        try
        {
            var all = Resources.FindObjectsOfTypeAll<Item>();
            Log.LogInfo($"=== SunMirror: найдено предметов (Item) = {all.Length} ===");
            foreach (var it in all)
            {
                if (it == null) continue;
                Log.LogInfo($"  item name='{it.name}' id={it.itemID}");
            }
            Log.LogInfo("=== конец списка предметов ===");
        }
        catch (System.Exception e)
        {
            Log.LogWarning($"LogItemsOnce: {e.GetType().Name}: {e.Message}");
        }
    }

    // Периодически рескиним ВСЕ бинокли в сцене: 1-е лицо (Binoculars(Clone)), тело в 3-м лице, мировые.
    private float _cloneTimer;
    private void ReskinAllBinoculars()
    {
        _cloneTimer -= Time.deltaTime;
        if (_cloneTimer > 0f) return;
        _cloneTimer = 0.5f;
        if (!_reskinEnabled.Value) { _holdingMirror = false; _cloneGo = null; return; }

        // ПРОИЗВОДИТЕЛЬНОСТЬ: раньше здесь был Resources.FindObjectsOfTypeAll<MeshRenderer>() 5 раз в секунду —
        // перебор ВСЕХ загруженных объектов движка вместе с ассетами, главный источник лагов.
        // Теперь перебираем только Item в активной сцене (на порядки дешевле).
        // GameObject.Find не годится: клонов в сцене может быть несколько, и он вернёт не тот.
        // Клон viewmodel — КОРНЕВОЙ объект сцены, а перебор корневых объектов очень дёшев
        // и не зависит от того, включён ли на нём компонент Item (по нему найти не удавалось).
        var cam = Camera.main;
        GameObject? held = null;
        float bestD = float.MaxValue;
        int found = 0;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null || !root.activeInHierarchy) continue;
                if (!root.name.StartsWith("Frisbee")) continue;   // тарелочка = наше зеркало
                found++;
                if (cam == null) { held = root; continue; }
                float d = Vector3.Distance(root.transform.position, cam.transform.position);
                if (d < bestD) { bestD = d; held = root; }    // ближайший к камере = тот, что в руках
            }
        }

        if (held != _cloneGo)   // сменился предмет в руках — обновляем кэш рук
        {
            _handL = _handR = null;
            _discMesh = null; _tiltPivot = null;   // перезахватим на новом предмете
            if (held != null)
            {
                // ищем рекурсивно: руки могут лежать не прямым потомком
                foreach (var t in held.GetComponentsInChildren<Transform>(true))
                {
                    string n = t.name.ToLowerInvariant();
                    if (_handL == null && n == "hand_l") _handL = t;
                    if (_handR == null && n == "hand_r") _handR = t;
                }
            }
        }
        _cloneGo = held;
        _holdingMirror = held != null;
        if (held != null) MakeCoverMirrored(held.transform);

        if (Time.time - _lastHoldLog > 3f)
        {
            _lastHoldLog = Time.time;
            Log.LogInfo($"held: {(held != null ? $"'{held.name}' d={bestD:0.00}" : "нет")} (кандидатов: {found}) " +
                        $"handL={(_handL != null ? _handL.name : "NULL")} handR={(_handR != null ? _handR.name : "NULL")}");
        }
    }
    private float _lastHoldLog;

    // === Руководство → зеркало: обложке ставим зеркальный материал, форму блокнота сохраняем ===
    // Модель mirror.glb здесь не нужна: используем родной меш игры, поэтому нет ни лишних
    // 27 мешей, ни возни с позиционированием — блокнот уже правильно лежит в руках.
    private readonly System.Collections.Generic.HashSet<int> _mirroredCovers = new();

    // Берём ГОТОВЫЙ материал игры (хром/стекло) вместо подбора своих чисел.
    // Рецепт из скана: m_chrome = metallic 1, smooth 1, tint 0.53 серый, baseTex пустая,
    // tex1 есть + триплanar. Свои нарисованные градиенты этому и близко не соответствовали.
    // === НАСТОЯЩЕЕ ЗЕРКАЛО через шейдер игры GD/Mirror ===
    // Скан показал: единственное настоящее зеркало в игре (в аэропорту) сделано камерой,
    // которая рендерит сцену в RenderTexture 3000x1500, и шейдером GD/Mirror.
    // Шейдеры в Unity глобальные, поэтому берём тот же шейдер, но с крошечной текстурой
    // и редким обновлением — иначе получим ровно те лаги, что мы уже дважды выгребали.
    private Camera? _mirrorCam;
    private RenderTexture? _mirrorRT;
    private Material? _mirrorMat;
    private int _mirrorFrame;

    private bool TrySetupRealMirror(Renderer r)
    {
        if (!_realMirrorOn.Value) return false;
        var sh = Shader.Find("GD/Mirror");
        if (sh == null) { Log.LogWarning("Шейдер GD/Mirror не найден."); return false; }

        _mirrorMat = new Material(sh);
        // Свойство берём ЯВНО. Раньше цикл брал последнее найденное, а им оказалось
        // служебное unity_ShadowMasks — отражение уезжало в слот теней, отсюда белый круг.
        // Низкое разрешение = мягкое, размытое отражение (и дешевле рендер).
        // Чёткая картинка в зеркальце размером с ладонь выглядит неестественно.
        int res = Mathf.Clamp(_realMirrorRes.Value, 32, 512);
        _mirrorRT = new RenderTexture(res, res, 16)
        {
            name = "SunMirrorRT",
            filterMode = FilterMode.Bilinear,   // сглаживание при растягивании — это и даёт мягкость
            useMipMap = false,
            antiAliasing = 1
        };
        if (_mirrorMat.HasProperty("_MainTex"))         _mirrorMat.SetTexture("_MainTex", _mirrorRT);
        if (_mirrorMat.HasProperty("_TextureSample0"))  _mirrorMat.SetTexture("_TextureSample0", _mirrorRT);

        var go = new GameObject("SunMirrorCam");
        Object.DontDestroyOnLoad(go);
        _mirrorCam = go.AddComponent<Camera>();
        _mirrorCam.targetTexture = _mirrorRT;
        _mirrorCam.enabled = false;                      // рендерим вручную, не каждый кадр
        _mirrorCam.cullingMask = ~(1 << MirrorLayer);    // себя не снимаем
        _mirrorCam.fieldOfView = 90f;
        _mirrorCam.nearClipPlane = 0.05f;
        // Дальность режем: в отражении на диске 3 см дальний план всё равно не разглядеть,
        // а каждый лишний метр — это лишняя геометрия в полном рендере сцены.
        _mirrorCam.farClipPlane = 45f;
        _mirrorCam.allowHDR = false;                     // отражению HDR не нужен
        _mirrorCam.allowMSAA = false;                    // и сглаживание тоже
        _mirrorCam.useOcclusionCulling = true;

        // Можно ли сделать зеркальной только лицевую сторону? Это возможно, лишь если у меша
        // несколько подмешей (материальных слотов) — тогда задней можно дать обычный материал.
        int slots = r.sharedMaterials.Length;
        if (slots > 1)
        {
            var mats = r.materials;                    // копии, чтобы не портить материалы игры
            mats[0] = _mirrorMat;                      // лицевая — зеркало
            r.materials = mats;                        // остальные слоты остаются родными
            Log.LogInfo($"Зеркало только на первом слоте из {slots}.");
        }
        else
        {
            r.material = _mirrorMat;
        }
        Log.LogInfo($"Настоящее зеркало включено: GD/Mirror, RT {res}x{res}, слотов материала={slots}.");
        return true;
    }

    private void UpdateRealMirror()
    {
        if (_mirrorCam == null || _discMesh == null || !_holdingMirror) return;
        _mirrorFrame++;
        if (_mirrorFrame % Mathf.Max(1, _realMirrorEvery.Value) != 0) return;   // не каждый кадр

        // камера смотрит из зеркала наружу, вдоль его нормали
        var t = _discMesh;
        Vector3 n = t.forward;
        var me = Character.localCharacter;
        if (me != null && Vector3.Dot(n, t.position - me.transform.position) < 0f) n = -n;
        _mirrorCam.transform.position = t.position + n * 0.05f;
        _mirrorCam.transform.rotation = Quaternion.LookRotation(n, Vector3.up);
        _mirrorCam.Render();
    }

    private Material? _donorMat;
    private bool _donorSearched;
    private Material? FindDonorMaterial()
    {
        if (_donorMat != null || _donorSearched) return _donorMat;
        string[] wanted = { "m_chrome", "Plane Glass Solid", "Plane Glass" };
        foreach (var name in wanted)
        {
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.name != name) continue;
                _donorMat = m;
                Log.LogInfo($"Донор материала найден: '{m.name}' (shader {m.shader.name}).");
                return _donorMat;
            }
        }
        // Кэшируем и НЕНАХОД: без этого Resources.FindObjectsOfTypeAll<Material>() —
        // перебор всех загруженных материалов — повторялся снова и снова.
        _donorSearched = true;
        Log.LogWarning("Донор материала не найден — применяю числовой рецепт хрома.");
        return null;
    }

    private void MakeCoverMirrored(Transform root)
    {
        // Тарелочка — один-два меша; руки не трогаем, дым не трогаем.
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            if (r is ParticleSystemRenderer) continue;
            if (r.name.ToLowerInvariant().Contains("hand")) continue;
            // КРИТИЧНО: пропускаем собственные объекты. Без этого блик попадал в перебор,
            // считался зеркальной поверхностью и получал ещё один блик внутрь себя —
            // и так каждые 0.5 с, бесконечной матрёшкой. Это и давало лаги.
            if (r.name.StartsWith("SunMirror")) continue;

            int id = r.GetInstanceID();
            if (_mirroredCovers.Contains(id)) continue;   // уже сделали
            _mirroredCovers.Add(id);

            // Сначала — настоящее зеркало шейдером игры. Если получилось, остальное не нужно.
            if (_discMesh == null && TrySetupRealMirror(r))
            {
                _discMesh = r.transform;
                _tiltPivot = EnsureTiltPivot(r.transform);
                AddGlint(r.transform);
                continue;
            }

            // Иначе пробуем ПОДМЕНИТЬ материал на готовый хром игры — так поверхность
            // будет буквально такая же, как у хромированных предметов, без подбора чисел.
            var donor = FindDonorMaterial();
            if (donor != null) r.material = new Material(donor);

            // material (не sharedMaterial) — копия, чтобы не испортить материал игры глобально
            var m = r.material;
            Log.LogInfo($"MIRROR: '{r.name}' mat='{m.name}' shader='{m.shader.name}'");

            // Свойства шейдера игры W/Peak_Standard (имена взяты из дампа, а не угаданы).
            // Зеркало НЕ белое: белый читается как бумага. Настоящее зеркало — тёмная
            // холодная база с очень сильным бликом, тогда оно выглядит металлическим.
            // Белый цвет базы «съедал» градиент и давал пластик — берём нейтральный,
            // чтобы рисунок металла читался, а не выбеливался.
            var silver = new Color(1f, 1f, 1f, 1f);
            // ПОЛИРОВАННОЕ СЕРЕБРО. Раньше выходила «неполированная бронза»: нижняя половина
            // моей нарисованной подложки коричневая (земля), и она пятнами лезла наружу.
            // Теперь отражение даёт кубическая карта, поэтому подложку убираем совсем,
            // а все цветовые слои шейдера глушим в нейтраль — иначе они подмешивают тёплый оттенок.
            // Как у m_chrome: базовая текстура ПУСТАЯ, цвет задаётся через _Tint.
            if (m.HasProperty("_BaseTexture"))   m.SetTexture("_BaseTexture", null);
            if (m.HasProperty("_BaseTexAmount")) m.SetFloat("_BaseTexAmount", 1f);
            if (m.HasProperty("_Texture1"))      m.SetTexture("_Texture1", null);
            if (m.HasProperty("_Texture2"))      m.SetTexture("_Texture2", null);
            if (m.HasProperty("_Texture3"))      m.SetTexture("_Texture3", null);

            // Точные значения хрома игры (из скана m_chrome):
            if (m.HasProperty("_BaseColor"))     m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Tint"))          m.SetColor("_Tint", new Color(0.62f, 0.64f, 0.68f, 1f)); // чуть светлее хрома
            if (m.HasProperty("_Color1"))        m.SetColor("_Color1", new Color(0.55f, 0.55f, 0.55f, 0.57f));
            if (m.HasProperty("_Smooth1"))       m.SetFloat("_Smooth1", 1f);
            if (m.HasProperty("_Triplanar1On"))  m.SetFloat("_Triplanar1On", 1f);

            // слои-подмешивания и сдвиг оттенка — в ноль, это источник «бронзовых» пятен
            if (m.HasProperty("_HueStr1"))       m.SetFloat("_HueStr1", 0f);
            if (m.HasProperty("_HueStr2"))       m.SetFloat("_HueStr2", 0f);
            if (m.HasProperty("_HueShifts1"))    m.SetVector("_HueShifts1", Vector4.zero);
            if (m.HasProperty("_Mask1"))         m.SetFloat("_Mask1", 0f);
            if (m.HasProperty("_Triplanar2On"))  m.SetFloat("_Triplanar2On", 0f);
            if (m.HasProperty("_Triplanar3On"))  m.SetFloat("_Triplanar3On", 0f);
            if (m.HasProperty("_TopDown2On"))    m.SetFloat("_TopDown2On", 0f);

            // Слой 1 — «отражение»: та же картинка, но спроецированная ПО МИРУ (triplanar).
            // При повороте диска она сдвигается по поверхности, и это читается как отражение,
            // а не как наклейка. Настоящих отражений шейдер не считает, это их замена.
            if (m.HasProperty("_Texture1"))      m.SetTexture("_Texture1", GetFakeReflectionTex());
            if (m.HasProperty("_Triplanar1On"))  m.SetFloat("_Triplanar1On", 1f);
            if (m.HasProperty("_TopDown1On"))    m.SetFloat("_TopDown1On", 1f);
            if (m.HasProperty("_UV1"))           m.SetFloat("_UV1", 0f);
            if (m.HasProperty("_ViewDirBlend"))  m.SetFloat("_ViewDirBlend", 1f);

            // зеркальная полировка
            if (m.HasProperty("_BaseMetallic"))  m.SetFloat("_BaseMetallic", 1f);
            if (m.HasProperty("_BaseSmooth"))    m.SetFloat("_BaseSmooth", 1f);
            if (m.HasProperty("_Smooth1"))       m.SetFloat("_Smooth1", 1f);
            if (m.HasProperty("_Smooth2"))       m.SetFloat("_Smooth2", 1f);
            if (m.HasProperty("_Smooth3"))       m.SetFloat("_Smooth3", 1f);
            // У хрома игры ЭТИ ПАРАМЕТРЫ ПО НУЛЯМ — мои «усиления» и портили картинку.
            if (m.HasProperty("_AddSpecular"))   m.SetFloat("_AddSpecular", 0f);
            if (m.HasProperty("_SpecularPower")) m.SetFloat("_SpecularPower", 1f);
            if (m.HasProperty("_NoShadowSpec"))  m.SetFloat("_NoShadowSpec", 0f);
            if (m.HasProperty("_ViewDirBlend"))  m.SetFloat("_ViewDirBlend", 0f);

            if (_discMesh == null)   // первый настоящий меш = диск
            {
                _discMesh = r.transform;
                _tiltPivot = EnsureTiltPivot(r.transform);
                AddGlint(r.transform);   // блик вешаем ТОЛЬКО на диск, и только один раз
            }
        }
    }

    // === Отражение окружения кубической картой ===
    // Дешёвый путь из статьи: сцена снимается в кубическую карту РЕДКО (а не каждый кадр,
    // как при планарных отражениях), материал с металликом её отражает.
    // Движущиеся объекты в таком отражении отстают — это нормальная плата за дешевизну.
    private void UpdateReflectionProbe()
    {
        if (!_reflProbeOn.Value)
        {
            if (_probe != null) { Object.Destroy(_probe.gameObject); _probe = null; }   // выключили — убираем совсем
            return;
        }
        if (!_holdingMirror) return;                 // без зеркала в руках не тратим ничего

        var me = Character.localCharacter;
        if (me == null) return;

        if (_probe == null)
        {
            var go = new GameObject("SunMirrorReflectionProbe");
            Object.DontDestroyOnLoad(go);
            _probe = go.AddComponent<ReflectionProbe>();
            _probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
            _probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
            _probe.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.IndividualFaces;
            _probe.resolution = Mathf.Clamp(_reflRes.Value, 32, 256);
            _probe.cullingMask = ~(1 << MirrorLayer);   // само зеркало в отражении не нужно
            _probe.clearFlags = UnityEngine.Rendering.ReflectionProbeClearFlags.Skybox;
            _probe.size = Vector3.one * 60f;            // зона влияния вокруг игрока
            _probe.boxProjection = false;
            Log.LogInfo($"Reflection probe created (res={_probe.resolution}).");
        }

        _probe.transform.position = me.transform.position + Vector3.up * 1.2f;

        _probeTimer -= Time.deltaTime;
        if (_probeTimer <= 0f)
        {
            _probeTimer = Mathf.Max(0.25f, _reflRefresh.Value);
            _probe.RenderProbe();                       // пересъёмка, размазана по кадрам
        }
    }

    // Наклон диска: тарелочку держат плашмя, и зеркальная сторона смотрит в небо.
    // Доворачиваем её вперёд, чтобы блик летел в сторону взгляда.
    // Крутим НЕ сам диск, а вставленную нами прослойку-родителя.
    // Анимация предмета каждый кадр переписывает поворот самого диска — из-за этого
    // наклон то применялся, то нет. Про прослойку она не знает и трогать её не может.
    private void ApplyDiscTilt()
    {
        if (_tiltPivot == null) return;
        _tiltPivot.localRotation = Quaternion.Euler(_discTilt.Value, 0f, 0f);
    }

    private Transform? _tiltPivot;

    private Transform EnsureTiltPivot(Transform mesh)
    {
        // уже вставляли? (переживает перезагрузку плагина)
        if (mesh.parent != null && mesh.parent.name == "SunMirrorTilt") return mesh.parent;

        var pivot = new GameObject("SunMirrorTilt").transform;
        pivot.SetParent(mesh.parent, false);
        pivot.localPosition = mesh.localPosition;
        pivot.localRotation = Quaternion.identity;
        pivot.localScale = Vector3.one;
        mesh.SetParent(pivot, true);      // диск переезжает внутрь, сохраняя вид
        Log.LogInfo($"Tilt pivot вставлен над '{mesh.name}'.");
        return pivot;
    }

    // Блик на поверхности зеркала: квад с текстурой lens-flare по центру диска, лицом к камере.
    private void AddGlint(Transform modelRoot)
    {
        if (!_glintOn.Value || modelRoot.Find("SunMirrorGlint") != null) return;
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = "SunMirrorGlint";
        var col = q.GetComponent<Collider>(); if (col != null) Object.Destroy(col);
        var mr = q.GetComponent<MeshRenderer>();
        var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent");
        var mat = new Material(sh) { mainTexture = GetFlareTex(), color = new Color(1f, 1f, 1f, 1f) };
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        var qt = q.transform;
        qt.SetParent(modelRoot, false);
        qt.localPosition = Vector3.zero;                     // центр диска
        Vector3 pls = modelRoot.lossyScale;
        float w = _glintSize.Value;
        qt.localScale = new Vector3(w / Mathf.Max(1e-4f, Mathf.Abs(pls.x)), w / Mathf.Max(1e-4f, Mathf.Abs(pls.y)), w / Mathf.Max(1e-4f, Mathf.Abs(pls.z)));
        q.AddComponent<Billboard>();
        _glintTf = qt;                       // чтобы раздувать блик в момент ослепления
        _glintBaseScale = qt.localScale;
        Log.LogInfo($"Glint added on '{modelRoot.parent?.name}' size={w}");
    }

    // Скан «здесь и сейчас»: активные видимые рендереры бинокля + все камеры (найти viewmodel)
    // Ищем в сцене самые «металлические» материалы игры и выписываем их рецепт целиком.
    // Свои значения подбирать вслепую бессмысленно — у игры уже есть рабочие настройки
    // под её собственный шейдер, надо просто взять их и усилить.
    private void ScanShinyMaterials()
    {
        try
        {
            var found = new System.Collections.Generic.Dictionary<string, (float score, Material m, string owner)>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r == null || r.name.StartsWith("SunMirror")) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    if (!m.HasProperty("_BaseMetallic") || !m.HasProperty("_BaseSmooth")) continue;
                    float met = m.GetFloat("_BaseMetallic");
                    float smo = m.GetFloat("_BaseSmooth");
                    float score = met * smo;
                    if (score <= 0.05f) continue;
                    if (!found.ContainsKey(m.name) || found[m.name].score < score)
                        found[m.name] = (score, m, r.name);
                }
            }

            var top = new System.Collections.Generic.List<(float score, Material m, string owner)>(found.Values);
            top.Sort((a, b) => b.score.CompareTo(a.score));

            Log.LogInfo($"=== БЛЕСТЯЩИЕ МАТЕРИАЛЫ ИГРЫ (найдено {top.Count}) ===");
            for (int i = 0; i < Mathf.Min(10, top.Count); i++)
            {
                var (score, m, owner) = top[i];
                string F(string p) => m.HasProperty(p) ? m.GetFloat(p).ToString("0.00") : "-";
                string C(string p) => m.HasProperty(p) ? m.GetColor(p).ToString("F2") : "-";
                string T(string p) => m.HasProperty(p) ? (m.GetTexture(p) != null ? "есть" : "нет") : "-";
                Log.LogInfo($"  [{i}] '{m.name}' на '{owner}' score={score:0.00}");
                Log.LogInfo($"      metallic={F("_BaseMetallic")} smooth={F("_BaseSmooth")} " +
                            $"addSpec={F("_AddSpecular")} specPow={F("_SpecularPower")} noShadowSpec={F("_NoShadowSpec")} viewDir={F("_ViewDirBlend")}");
                Log.LogInfo($"      baseColor={C("_BaseColor")} tint={C("_Tint")} texAmount={F("_BaseTexAmount")} baseTex={T("_BaseTexture")} " +
                            $"color1={C("_Color1")} smooth1={F("_Smooth1")} tex1={T("_Texture1")} tri1={F("_Triplanar1On")}");
            }
            Log.LogInfo("=== конец списка блестящих ===");

            // Как игра делает зеркала: ищем объекты со словами mirror/photo/booth и смотрим,
            // чем они отрисованы — камерой в текстуру, кубической картой или просто материалом.
            Log.LogInfo("=== ЗЕРКАЛА ИГРЫ ===");
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (t == null) continue;
                string n = t.name.ToLowerInvariant();
                if (!(n.Contains("mirror") || n.Contains("photo") || n.Contains("booth"))) continue;

                var cam = t.GetComponent<Camera>();
                var rend = t.GetComponent<Renderer>();
                string info = $"  '{t.name}' path={GetPath(t)}";
                if (cam != null)
                    info += $" | КАМЕРА: target={(cam.targetTexture != null ? $"RenderTexture {cam.targetTexture.width}x{cam.targetTexture.height}" : "нет")} enabled={cam.enabled}";
                if (rend != null && rend.sharedMaterial != null)
                {
                    var mm = rend.sharedMaterial;
                    string mainTex = "нет";
                    if (mm.HasProperty("_BaseTexture") && mm.GetTexture("_BaseTexture") != null) mainTex = mm.GetTexture("_BaseTexture").GetType().Name;
                    else if (mm.HasProperty("_MainTex") && mm.GetTexture("_MainTex") != null) mainTex = mm.GetTexture("_MainTex").GetType().Name;
                    info += $" | МАТЕРИАЛ '{mm.name}' shader='{mm.shader.name}' tex={mainTex}";
                }
                Log.LogInfo(info);
            }
            Log.LogInfo("=== конец зеркал ===");
        }
        catch (System.Exception e) { Log.LogWarning($"ScanShiny: {e.GetType().Name}: {e.Message}"); }
    }

    private static string GetPath(Transform t)
    {
        var sb = new System.Text.StringBuilder(t.name);
        var p = t.parent;
        int guard = 0;
        while (p != null && guard++ < 12) { sb.Insert(0, p.name + "/"); p = p.parent; }
        return sb.ToString();
    }

    // чистим список от уничтоженных предметов (холстер: модель — дитя предмета, гаснет вместе с ним)
    private void SyncReskins()
    {
        for (int i = _reskins.Count - 1; i >= 0; i--)
        {
            var (item, model) = _reskins[i];
            if (item == null || model == null) { _reskins.RemoveAt(i); continue; }
        }
    }

    private void ToggleThirdPerson()
    {
        _tpActive = !_tpActive;
        EnsureTpCam();
        if (_tpCam != null) _tpCam.gameObject.SetActive(_tpActive);
        Log.LogInfo($"ThirdPerson cam: {_tpActive}");
    }

    private void EnsureTpCam()
    {
        if (_tpCam != null) return;
        var go = new GameObject("SunMirrorTPCam");
        _tpCam = go.AddComponent<Camera>();
        var main = Camera.main;
        if (main != null)
        {
            _tpCam.fieldOfView = main.fieldOfView;
            _tpCam.depth = main.depth + 1;       // рисуется поверх основной
            _tpCam.nearClipPlane = main.nearClipPlane;
            _tpCam.farClipPlane = main.farClipPlane;
        }
        _tpCam.cullingMask = ~0;                 // всё, включая своего персонажа
        _tpCam.clearFlags = CameraClearFlags.Skybox;
        go.SetActive(false);
        DontDestroyOnLoad(go);
    }

    private void UpdateThirdPerson(Character? me)
    {
        if (!_tpActive || _tpCam == null || me == null) return;
        var main = Camera.main;
        Vector3 head = me.transform.position + Vector3.up * 1.5f;
        Vector3 fwd = (main != null) ? main.transform.forward
            : (me.data.lookDirection.sqrMagnitude > 0.01f ? me.data.lookDirection.normalized : me.transform.forward);
        // ставим камеру ПЕРЕД тобой и смотрим назад — видно лицо/зеркало/эмоут
        _tpCam.transform.position = head + fwd * _tpDistance.Value + Vector3.up * 0.4f;
        _tpCam.transform.LookAt(head);
    }

    // Зеркало вспыхивает ярким свечением, когда светишь в игрока.
    private void SetMirrorFlash(bool on)
    {
        // Главное здесь — блик на зеркале. В спокойном состоянии он маленький,
        // а при наводке на тиммейта раздувается: игровой bloom превращает его
        // в ту самую огромную звезду-вспышку, которую видит ослеплённый.
        // Держать её постоянно нельзя — это половина экрана полупрозрачной заливки, отсюда лаги.
        // Блик показываем ТОЛЬКО в момент ослепления. Постоянно горящая вспышка
        // закрывала белым пятном весь диск — из-за неё не видно ни поверхности, ни наклона.
        if (_glintTf != null)
        {
            var bb = _glintTf.GetComponent<Billboard>();
            if (bb != null) bb.allowed = on;
            if (on)
            {
                float pulseG = 1f + 0.25f * Mathf.Sin(Time.time * 14f);
                _glintTf.localScale = _glintBaseScale * (_glintFlashMul.Value * pulseG);
            }
        }

        if (_heldMats == null) return;
        float pulse = 0.7f + 0.3f * Mathf.Sin(Time.time * 12f);
        Color e = on ? new Color(1f, 0.95f, 0.8f) * (3.5f * pulse) : Color.black;
        foreach (var m in _heldMats)
        {
            if (m == null || !m.HasProperty("_EmissionColor")) continue;
            if (on) m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", e);
        }
    }

    // Яркий пульсирующий блик над игроком — «отражение солнца» / сигнал «я здесь» издалека.
    private void ShowSignal(Character me)
    {
        EnsureSignal();
        var cam = Camera.main;
        // Вспышка висит на САМОМ диске. Раньше она привязывалась к старому пропу в руке;
        // после его удаления якоря не стало, и вспышка уехала персонажу на голову.
        // Якорь — НАСТОЯЩАЯ рука персонажа, а не viewmodel-клон. Клон живёт у камеры
        // от первого лица: по замерам вспышка оказывалась метрах в шести от персонажа,
        // в воздухе. Свой экран этого не выдавал, а тиммейт увидел бы светяшку рядом
        // с игроком, а не на его зеркале. Кость руки рисуют все клиенты одинаково.
        Vector3 pos = HandAnchor(me);
        _signal!.transform.position = pos;
        // Разворачиваем вспышку на ТОГО, КТО СМОТРИТ, а не на цель луча.
        // У каждого клиента это его собственная камера — так зеркало «мигает» каждому,
        // кто на тебя смотрит, как настоящее сигнальное. Раньше тут стояла позиция жертвы:
        // при включённом SelfTarget вспышка разворачивалась на собственные ноги игрока
        // и с десятка метров была видна с ребра, то есть почти никак.
        var viewer = ActiveCamera();
        Vector3 lookFrom = viewer != null ? viewer.transform.position
            : (cam != null ? cam.transform.position : pos - Vector3.forward);
        _signal.transform.rotation = Quaternion.LookRotation(pos - lookFrom);
        float pulse = 0.7f + 0.3f * Mathf.Sin(Time.time * 12f);
        // Растём с расстоянием до смотрящего: зеркало должно быть заметно и за сто метров,
        // иначе на таком удалении вспышка превращается в один пиксель.
        float dist = Vector3.Distance(pos, lookFrom);
        float size = Mathf.Clamp(0.6f + dist * _signalGrow.Value, 0.6f, _signalMax.Value);
        _signal.transform.localScale = Vector3.one * (size * pulse);
        if (_signalLight != null) _signalLight.intensity = 12f * pulse;
        if (!_signal.activeSelf) _signal.SetActive(true);


    }

    /// <summary>
    /// Где у персонажа зеркало с точки зрения ЛЮБОГО наблюдателя: кость правой руки.
    /// Запасной вариант — грудь, если кость почему-то не отдали.
    /// </summary>
    private static Vector3 HandAnchor(Character c)
    {
        try
        {
            var bp = c.GetBodypart(BodypartType.Hand_R);
            if (bp != null && bp.transform != null) return bp.transform.position;
        }
        catch (System.Exception) { /* у бота или трупа кости может не быть */ }
        return c.Center;
    }

    private void HideSignal() { if (_signal != null && _signal.activeSelf) _signal.SetActive(false); }

    private void EnsureSignal()
    {
        if (_signal != null) return;
        _signal = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _signal.name = "SunSignal";
        var col = _signal.GetComponent<Collider>();
        if (col != null) Destroy(col);
        var mr = _signal.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
        var mat = new Material(sh) { name = "SunSignalMat", mainTexture = CircleTex() };
        mat.color = new Color(1f, 0.98f, 0.85f, 1f);
        mr.material = mat;
        var lgo = new GameObject("SunSignalLight");
        lgo.transform.SetParent(_signal.transform, false);
        _signalLight = lgo.AddComponent<Light>();
        _signalLight.type = LightType.Point;
        _signalLight.color = new Color(1f, 0.97f, 0.8f);
        _signalLight.range = 8f;
        _signalLight.shadows = LightShadows.None;
        _signal.SetActive(false);
        DontDestroyOnLoad(_signal);
    }

    // Ближайшее попадание, игнорируя предмет в руках и собственного персонажа.
    private bool RaycastIgnoringHeld(Vector3 origin, Vector3 dir, float range, out RaycastHit best)
    {
        best = default;
        var me = Character.localCharacter;
        var hits = Physics.RaycastAll(origin, dir, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float bestD = float.MaxValue;
        foreach (var h in hits)
        {
            if (h.collider == null) continue;
            var t = h.collider.transform;
            if (_cloneGo != null && t.IsChildOf(_cloneGo.transform)) continue;   // наш зеркало-viewmodel
            if (me != null && t.IsChildOf(me.transform)) continue;               // собственное тело
            if (h.distance < bestD) { bestD = h.distance; best = h; }
        }
        return bestD < float.MaxValue;
    }

    private float _timeOnVictim;
    private bool _emotePlayed, _stunned;
    private void ClearTarget() { _victim = null; _timeOnVictim = 0f; _emotePlayed = false; _stunned = false; }

    private void UpdateSpot()
    {
        var me = Character.localCharacter;
        var cam = Camera.main;
        if (me == null || cam == null) { HideSpot(); return; }

        Vector3 origin = cam.transform.position;
        Vector3 aim = cam.transform.forward;

        // Луч не должен цепляться за наш же предмет в руках: у бинокля остаётся MeshCollider
        // (меши мы прячем, коллайдер нет), и он ловит луч в паре см от камеры.
        if (!RaycastIgnoringHeld(origin, aim, _range.Value, out RaycastHit hit))
        { HideSpot(); ClearTarget(); _spotHasPos = false; return; }

        // Попал в зеркало? Тогда цель = ты сам (наводишься на своё отражение),
        // и солнце можно не проверять (ты ловишь через зеркало, в помещении).
        bool isMirror = IsMirrorSurface(hit.collider);

        if (!isMirror && _requireSun.Value && !SunReaches(origin, out string why))
        {
            if (Time.time - _lastSunLog > 1f) { Log.LogInfo($"Spot off: no sunlight ({why})."); _lastSunLog = Time.time; }
            HideSpot(); ClearTarget(); _spotHasPos = false; return;
        }

        // рассеяние: дальше → больше диаметр
        float size = _spotSize.Value + hit.distance * _spread.Value;

        // сглаживание позиции (frame-rate independent)
        if (!_spotHasPos) { _spotPos = hit.point; _spotHasPos = true; }
        else _spotPos = Vector3.Lerp(_spotPos, hit.point, 1f - Mathf.Exp(-_smooth.Value * Time.deltaTime));

        ShowSpot(_spotPos, hit.normal, size);

        // цель: у зеркала — отражаем луч и бьём в того, чьё отражение поймал (в т.ч. себя);
        // иначе — прямое попадание, себя игнорим.
        Character? victim;
        if (isMirror)
        {
            Vector3 reflDir = Vector3.Reflect(aim, hit.normal).normalized;
            victim = Physics.Raycast(hit.point + reflDir * 0.05f, reflDir, out RaycastHit rhit, _range.Value)
                ? rhit.collider.GetComponentInParent<Character>() : null;
        }
        else
        {
            victim = hit.collider.GetComponentInParent<Character>();
            if (victim == me) victim = null;
        }

        // DEBUG-режим тестирования без второго игрока: куда бы ни бил луч, целью становишься ты.
        // Это НЕ имитация — выполняется ровно тот же код, что отработает на тиммейте,
        // потому что вся логика цели принимает любого Character, а не только чужого.
        if (_selfTarget.Value && me != null) victim = me;

        if (victim == null) { ClearTarget(); return; }
        if (victim == Character.localCharacter)
        {
            _lastLocalHitTime = Time.time;                 // светят в тебя → засвет экрана
            if (_discMesh != null)                         // вспышка висит на зеркале, а не на экране
            { _glareSourcePos = _discMesh.position; _glareSourceValid = true; }
        }

        if (victim != _victim) { _victim = victim; _timeOnVictim = 0f; _emotePlayed = false; _stunned = false; }

        if (!_emotePlayed)
        {
            if (_useEmote.Value && !string.IsNullOrEmpty(_emoteName.Value))
                PlayVictimEmote(victim, _emoteName.Value);
            _emotePlayed = true;
        }

        // пока светим в глаза — ослепление (глаза, рот, поза, обездвиживание), без падения.
        // Шлём по сети: эффект должен примениться на клиенте жертвы и у всех зрителей.
        SendBlind(victim, _beamLinger.Value);
    }

    // Солнце реально попадает: над горизонтом + луч к солнцу не перекрыт реальным объектом
    // (границы уровня EdgeWall / небо-купол игнорируем).
    private bool SunReaches(Vector3 fromPos, out string reason)
    {
        reason = "";
        var dnm = DayNightManager.instance;
        if (dnm == null || dnm.sun == null) return true;    // не знаем — светим
        Vector3 toSun = -dnm.sun.transform.forward;
        if (toSun.y <= 0.1f) { reason = $"sun below horizon (toSun.y={toSun.y:0.00})"; return false; } // ночь

        Vector3 start = fromPos + Vector3.up * 0.4f;
        var hits = Physics.RaycastAll(start, toSun, 60f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        RaycastHit real = default; float best = float.MaxValue;
        foreach (var hh in hits)
        {
            string n = hh.collider.name.ToLowerInvariant();
            if (n.Contains("edge") || n.Contains("bound") || n.Contains("wall") || n.Contains("sky")
                || n.Contains("dome") || n.Contains("blocker") || n.Contains("limit")) continue; // границы уровня
            if (hh.distance < best) { best = hh.distance; real = hh; }
        }
        if (best < float.MaxValue) { reason = $"shadowed by {real.collider.name}"; return false; }
        return true;
    }


    // Поверхность — зеркало? (аэропортовое зеркало / фотобудка / любой Mirror-объект)
    private static bool IsMirrorSurface(Collider col)
    {
        if (col == null) return false;
        if (col.GetComponentInParent<Mirror>() != null) return true;
        if (col.GetComponentInParent<MirrorReflection>() != null) return true;
        if (col.GetComponentInParent<AirportPhotoboothKiosk>() != null) return true;
        string n = col.name.ToLowerInvariant();
        return n.Contains("mirror") || n.Contains("photo") || n.Contains("reflect");
    }

    private void ShowSpot(Vector3 point, Vector3 normal, float size)
    {
        EnsureSpot();

        float h = Mathf.Max(0.15f, size * _heightFactor.Value);
        _spotLightGo!.transform.position = point + normal * h;
        _spotLightGo.transform.rotation = Quaternion.LookRotation(-normal);
        _spotLight!.spotAngle = 2f * Mathf.Atan2(size * 0.5f, h) * Mathf.Rad2Deg;
        _spotLight.range = h + size + 0.5f;
        _spotLight.intensity = _spotGlow.Value * (1f + size * 0.25f); // дальше/крупнее → ярче
        if (!_spotLightGo.activeSelf) _spotLightGo.SetActive(true);

        if (_showSprite.Value)
        {
            _spotQuad!.transform.position = point + normal * 0.06f;
            _spotQuad.transform.rotation = Quaternion.FromToRotation(Vector3.forward, normal);
            _spotQuad.transform.localScale = new Vector3(size, size, 1f);
            var mr = _spotQuad.GetComponent<MeshRenderer>();
            if (!_spotQuad.activeSelf) _spotQuad.SetActive(true);
            if (Time.time - _spotDbgAt > 2f)
            {
                _spotDbgAt = Time.time;
                var scam = Camera.main;
                float dist = scam != null ? Vector3.Distance(scam.transform.position, point) : -1f;
                Log.LogInfo($"[SPOT] размер={size:0.00}м дист={dist:0.0}м виден={mr.isVisible} " +
                            $"шейдер={mr.material.shader.name} queue={mr.material.renderQueue}");
            }
        }
        else if (_spotQuad != null && _spotQuad.activeSelf) _spotQuad.SetActive(false);
    }

    private float _spotDbgAt;

    private void HideSpot()
    {
        if (_spotLightGo != null && _spotLightGo.activeSelf) _spotLightGo.SetActive(false);
        if (_spotQuad != null && _spotQuad.activeSelf) _spotQuad.SetActive(false);
    }

    private void EnsureSpot()
    {
        if (_spotLightGo == null)
        {
            _spotLightGo = new GameObject("SunSpotLight");
            _spotLight = _spotLightGo.AddComponent<Light>();
            _spotLight.type = LightType.Spot;
            _spotLight.color = new Color(1f, 0.95f, 0.7f);
            _spotLight.shadows = LightShadows.None;
            // Зайчик не должен подсвечивать наше же зеркало перед лицом — исключаем его слой.
            _spotLight.cullingMask = ~(1 << MirrorLayer);
            _spotLightGo.SetActive(false);
            DontDestroyOnLoad(_spotLightGo);
        }
        if (_spotQuad == null)
        {
            _spotQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _spotQuad.name = "SunSpotSprite";
            var col = _spotQuad.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var mr = _spotQuad.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            // Зайчик должен быть ЯРЧЕ белого, иначе на кремовом песке он не читается:
            // альфа-смешивание упирается в белый, а песок и так почти белый. Нужно
            // аддитивное смешивание и цвет выше 1 — тогда его подхватывает блум игры.
            var mat = MakeAdditiveSpotMaterial();
            mr.material = mat;
            _spotQuad.SetActive(false);
            DontDestroyOnLoad(_spotQuad);
        }
    }

    private static Texture2D? _circleTex;
    // Зайчик на ЯРКОМ песке мягким пятном не читается: белое по белому.
    // Нужен жёсткий диск почти без градиента + яркий ободок, который очерчивает край.
    private static Texture2D? _spotTex;
    private static Texture2D SpotTex()
    {
        if (_spotTex != null) return _spotTex;
        int s = 128;
        var t = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[s * s];
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float dx = (x - s / 2f) / (s / 2f), dy = (y - s / 2f) / (s / 2f);
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            // Внимание: Mathf.SmoothStep(from,to,t) — интерполяция, а НЕ порог из шейдеров.
            // Порог делаем сами: сначала InverseLerp, потом сглаживание.
            // Сплошной диск с резкой границей. Ореол убран намеренно: при аддитивном
            // смешивании он размазывает край и на светлом песке съедает весь контраст.
            // Спад с 0.86 до 0.94 — только сглаживание ступенек, не «мягкость».
            float a = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.86f, 0.94f, d));
            px[y * s + x] = new Color(1f, 1f, 1f, a);
        }
        t.SetPixels(px); t.Apply(false, false);
        t.filterMode = FilterMode.Bilinear;
        _spotTex = t; return t;
    }

    /// <summary>
    /// Материал зайчика. Пробуем URP/Unlit с принудительно аддитивным смешиванием
    /// (у него _SrcBlend/_DstBlend — настоящие свойства материала, в отличие от Sprites/Default,
    /// где режим смешивания зашит в шейдер). Если шейдера нет — откатываемся на Sprites/Default.
    /// </summary>
    private Material MakeAdditiveSpotMaterial()
    {
        var urp = Shader.Find("Universal Render Pipeline/Unlit");
        if (urp != null)
        {
            var m = new Material(urp) { name = "SunSpotMat" };
            m.SetFloat("_Surface", 1f);                 // Transparent
            m.SetFloat("_Blend", 2f);                   // Additive
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_AlphaClip", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = 3100;
            m.SetTexture("_BaseMap", SpotTex());
            // Выше единицы — чтобы пятно ушло в пересвет и поймало блум.
            float b = Mathf.Max(0.2f, _spriteBright.Value);
            m.SetColor("_BaseColor", new Color(b, b * 0.95f, b * 0.8f, 1f));
            Log.LogInfo($"Зайчик: URP/Unlit, аддитивное смешивание, яркость {b:0.00}.");
            return m;
        }

        var sh = Shader.Find("Sprites/Default");
        var fb = new Material(sh) { name = "SunSpotMat", mainTexture = SpotTex() };
        fb.color = new Color(1f, 0.99f, 0.93f, 1f);
        fb.renderQueue = 3100;
        Log.LogWarning($"Зайчик: URP/Unlit не найден, откат на '{sh?.name ?? "НЕТ ШЕЙДЕРА"}' (будет бледнее).");
        return fb;
    }

    private static Texture2D CircleTex()
    {
        if (_circleTex != null) return _circleTex;
        int s = 128;
        var t = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float dx = (x - s / 2f) / (s / 2f), dy = (y - s / 2f) / (s / 2f);
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(1f - d); a *= a;
                t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        t.Apply();
        _circleTex = t;
        return t;
    }

    // ── эффекты на жертву ──
    private void PlayVictimEmote(Character victim, string wantedName)
    {
        var anims = victim.refs?.animations;
        if (anims == null) return;
        var data = FindEmote(wantedName);
        string anim = data != null && !string.IsNullOrEmpty(data.anim) ? data.anim : wantedName;

        // ВАЖНО: играть эмоут нужно ТОЛЬКО через PlayEmote и ТОЛЬКО именем состояния аниматора.
        // RPCA_PlayRemove внутри делает две вещи: animator.SetBool("Emote", true) и Play(state).
        // PlaySpecificAnimation ставит только Play, без флага — состояние эмоута мгновенно
        // вытесняется локомоцией, и поза не появляется (так и было).
        // Бонус: PlayEmote идёт RpcTarget.All, то есть позу видят и игроки БЕЗ мода.
        try { anims.PlayEmote(anim); }
        catch (System.Exception e) { Log.LogWarning($"PlayEmote failed: {e.Message}"); }
        _emoteState = anim;

        Log.LogInfo($"Emote: состояние аниматора '{anim}' (через PlayEmote, сетевой).");
    }

    private static EmoteWheelData? FindEmote(string wanted)
    {
        if (string.IsNullOrEmpty(wanted)) return null;
        try
        {
            foreach (var e in Resources.FindObjectsOfTypeAll<EmoteWheelData>())
            {
                if (string.Equals(e.emoteName, wanted, System.StringComparison.OrdinalIgnoreCase)) return e;
                string loc = SafeLocalize(e.emoteName);
                if (!string.IsNullOrEmpty(loc) && string.Equals(loc, wanted, System.StringComparison.OrdinalIgnoreCase)) return e;
            }
        }
        catch { }
        return null;
    }

    private static string SafeLocalize(string key)
    {
        try { return LocalizedText.GetText(key, false); }
        catch { return ""; }
    }

    // ── Ослепление: стоит на месте (rooted) БЕЗ падения. Спиральные глаза/zzz — best-effort. ──
    /// <summary>
    /// Состояние ослепления ОДНОЙ жертвы. Раньше всё это были одиночные поля, то есть
    /// мод физически не мог держать двоих: при смене цели эффект с предыдущей снимался.
    /// В соло это незаметно, а если провести лучом по группе — слетает с каждого.
    /// </summary>
    private sealed class BlindState
    {
        public float until;              // до какого момента держится
        public float beganAt;            // начало непрерывного ослепления (для потолка)
        public float emoteStarted;       // когда проиграли эмоут (для задержки позы)
        public bool posePinned;
        public bool forcePrev;
        public string emoteState = "";
        public CharacterMovement? mv;    // только если жертва — локальный игрок
        public float moveModPrev = 1f;
        // Бэкап текстур лица СВОЙ у каждой жертвы: общий список возвращал чужие текстуры.
        public readonly System.Collections.Generic.List<(Material mat, string prop, Texture? tex)> faceBackup = new();
    }

    private readonly Dictionary<Character, BlindState> _blinds = new();
    private bool _lockLook;
    private Quaternion _lockedRot;
    private Character? _lastEmoteVictim;
    private float _lastEmoteTime = -99f;
    // Оригинальные текстуры глаз, чтобы вернуть после ослепления

    // Ставим Eye_Spin ПРЯМО на рендереры глаз. Через CurrentEyeTexture не работает:
    // игра сама пересчитывает её каждый кадр и возвращает обычные глаза.
    private void ApplyEyeTexture(CharacterCustomization cc, Texture? tex, System.Collections.Generic.List<(Material, string, Texture?)>? backup)
    {
        if (cc == null || cc.refs == null || cc.refs.EyeRenderers == null || tex == null) return;
        string[] props = { "_BaseTexture", "_BaseMap", "_MainTex", "_Texture1" };
        foreach (var r in cc.refs.EyeRenderers)
        {
            if (r == null) continue;
            var m = r.material;
            foreach (var p in props)
            {
                if (!m.HasProperty(p)) continue;
                backup?.Add((m, p, m.GetTexture(p)));
                m.SetTexture(p, tex);
            }
        }
    }

    /// <summary>
    /// Рот ослеплённого. Игра ставит рот так же, как глаза — текстурой на материал
    /// mouthRenderer (CharacterCustomization строка с refs.mouthRenderer.material.SetTexture).
    /// Варианты лежат в Customization.mouths; ищем по имени из конфига.
    /// Бэкап кладём в тот же список, что и для глаз, — он восстанавливает всё лицо разом.
    /// </summary>
    private void ApplyMouthTexture(CharacterCustomization cc, System.Collections.Generic.List<(Material, string, Texture?)>? backup)
    {
        if (string.IsNullOrWhiteSpace(_blindMouth.Value)) return;
        var r = cc.refs?.mouthRenderer;
        if (r == null) return;
        var tex = FindMouthTexture(_blindMouth.Value);
        if (tex == null) return;
        string[] props = { "_MainTex", "_BaseMap", "_BaseTexture", "_Texture1" };
        var m = r.material;
        foreach (var prop in props)
        {
            if (!m.HasProperty(prop)) continue;
            backup?.Add((m, prop, m.GetTexture(prop)));
            m.SetTexture(prop, tex);
        }
    }

    private static Texture? FindMouthTexture(string wanted)
    {
        var list = Singleton<Customization>.Instance?.mouths;
        if (list == null) return null;
        foreach (var o in list)
        {
            if (o == null) continue;
            if (string.Equals(o.name, wanted, System.StringComparison.OrdinalIgnoreCase)) return o.texture;
        }
        foreach (var o in list)   // запасной вариант — поиск по части имени
        {
            if (o == null) continue;
            if (o.name.ToLowerInvariant().Contains(wanted.ToLowerInvariant())) return o.texture;
        }
        Plugin.Log.LogWarning($"Рот '{wanted}' не найден. Нажми N — выведу список доступных.");
        return null;
    }

    private static void RestoreFaceTextures(System.Collections.Generic.List<(Material mat, string prop, Texture? tex)> backup)
    {
        foreach (var (mat, prop, tex) in backup)
            if (mat != null) mat.SetTexture(prop, tex);
        backup.Clear();
    }
    private ConfigEntry<float> _poseFreezeDelay = null!;
    private ConfigEntry<float> _blindHoldSeconds = null!;
    private readonly Dictionary<int, float> _cooldownUntil = new();  // ключ — ViewID цели

    /// <summary>
    /// Коротко отдёргивает голову жертвы назад — как от света в глаза.
    /// Character.AddForceToBodyPart сам рассылает RPCA_AddForceToBodyPart всем клиентам,
    /// поэтому это работает и на игроке БЕЗ мода — в отличие от глаз, рта и обездвиживания.
    /// Толкаем ТОЛЬКО голову (wholeBodyForce = ноль), иначе персонаж улетит.
    /// </summary>
    private void FlinchHead(Character victim)
    {
        if (!_flinch.Value || victim == null) return;
        try
        {
            var rig = victim.GetBodypartRig(BodypartType.Head);
            if (rig == null) return;
            // Направление — от того, кто светит, к жертве, плюс немного вверх:
            // голова запрокидывается, а не вдавливается в плечи.
            var me = Character.localCharacter;
            Vector3 dir = me != null ? (victim.Center - me.Center) : Vector3.forward;
            dir.y = 0f;
            dir = (dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward) + Vector3.up * 0.6f;
            victim.AddForceToBodyPart(rig, dir.normalized * _flinchForce.Value, Vector3.zero);
        }
        catch (System.Exception e) { Log.LogWarning($"FlinchHead: {e.Message}"); }
    }

    // ───────────────────────── сетевой слой ─────────────────────────

    private float _netTick, _netSendAt;
    private Character? _lastNetLogged;

    /// <summary>
    /// Вешает приёмник RPC на каждого персонажа в сцене. Нужен на ВСЕХ, а не только
    /// на нас: Photon вызывает метод на объекте жертвы, и объект этот есть у каждого клиента.
    /// Character.AllCharacters — статический список самой игры, перебор дешёвый.
    /// </summary>
    private static readonly string[] OurComponentTypes = { "SunMirrorNet", "SunMirrorDummy" };

    /// <summary>
    /// Снимает наши компоненты с чужих объектов. Сравниваем ИМЯ типа, а не сам тип:
    /// после hot-reload компонент от прошлой сборки — формально другой тип, и
    /// GetComponent&lt;T&gt;() его не видит. Из-за этого они копились на персонажах,
    /// а RPC вызывался столько раз, сколько накопилось обработчиков.
    /// </summary>
    private static void DestroyOurComponents()
    {
        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (mb == null) continue;
            string n = mb.GetType().Name;
            for (int i = 0; i < OurComponentTypes.Length; i++)
                if (n == OurComponentTypes[i]) { Object.Destroy(mb); break; }
        }
    }

    /// <summary>Есть ли уже наш компонент с таким именем типа (в т.ч. от прошлой сборки).</summary>
    private static bool HasOurComponent(GameObject go, string typeName)
    {
        foreach (var mb in go.GetComponents<MonoBehaviour>())
            if (mb != null && mb.GetType().Name == typeName) return true;
        return false;
    }

    private void EnsureNetReceivers()
    {
        _netTick -= Time.deltaTime;
        if (_netTick > 0f) return;
        _netTick = 1f;

        foreach (var c in Character.AllCharacters)
        {
            if (c == null || HasOurComponent(c.gameObject, "SunMirrorNet")) continue;
            c.gameObject.AddComponent<SunMirrorNet>();
            // Без этого Photon ищет метод в списке компонентов, собранном ДО нас.
            try { c.photonView?.RefreshRpcMonoBehaviourCache(); }
            catch (System.Exception e) { Log.LogWarning($"RefreshRpcMonoBehaviourCache: {e.Message}"); }
            Log.LogInfo($"Сетевой приёмник повешен на '{c.name}' (viewID={c.photonView?.ViewID}).");
        }
    }

    /// <summary>
    /// Шлём ослепление на клиент жертвы. Не каждый кадр: RPC каждый кадр — это флуд,
    /// поэтому шлём несколько раз в секунду, а длительность берём с запасом.
    /// </summary>
    private void SendBlind(Character victim, float seconds)
    {
        if (victim == null) return;
        if (Time.time - _netSendAt < _netSendEvery.Value)
        {
            // между посылками эффект всё равно держим локально, чтобы не мигал
            StartOrRefreshBlind(victim, seconds);
            return;
        }
        _netSendAt = Time.time;

        var me = Character.localCharacter;
        int shooter = me != null && me.photonView != null ? me.photonView.ViewID : -1;
        var view = victim.photonView;
        if (view == null)
        {
            Log.LogWarning($"[SEND] у '{victim.name}' нет PhotonView — применяю локально.");
            StartOrRefreshBlind(victim, seconds); return;
        }
        if (view.ViewID == 0)
        {
            Log.LogWarning($"[SEND] у '{victim.name}' ViewID=0 (объект не зарегистрирован в сети) — локально.");
            StartOrRefreshBlind(victim, seconds); return;
        }
        if (victim.GetComponent<SunMirrorNet>() == null)
            Log.LogWarning($"[SEND] на '{victim.name}' нет приёмника SunMirrorNet — RPC не найдёт метод.");

        if (victim != _lastNetLogged)
        {
            _lastNetLogged = victim;
            Log.LogInfo($"[SEND] RPC -> '{victim.name}' viewID={view.ViewID} от {shooter}");
        }
        try { view.RPC(SunMirrorNet.RpcName, RpcTarget.All, seconds, shooter); }
        catch (System.Exception e)
        {
            Log.LogWarning($"RPC ослепления не ушёл ({e.Message}) — применяю локально.");
            StartOrRefreshBlind(victim, seconds);
        }
    }

    /// <summary>
    /// Выполняется на КАЖДОМ клиенте. Визуал (глаза, рот, поза) ставим всем —
    /// модель жертвы рисует каждый сам. Управление (обездвиживание, фиксация взгляда,
    /// засвет экрана) — только на клиенте самой жертвы, этим занимается StartOrRefreshBlind.
    /// </summary>
    internal void ApplyNetworkBlind(Character victim, float seconds, int shooterViewID)
    {
        StartOrRefreshBlind(victim, seconds);

        if (victim != Character.localCharacter) return;

        // Светят в НАС: засвет экрана и привязка вспышки к зеркалу обидчика.
        _lastLocalHitTime = Time.time;
        Character.GetCharacterWithPhotonID(shooterViewID, out var shooter);
        if (shooter != null && shooter != victim)
        {
            _glareSourcePos = shooter.Center;
            _glareSourceValid = true;
        }
        else if (_discMesh != null)      // сами себе (тест) — вспышка на своём зеркале
        {
            _glareSourcePos = _discMesh.position;
            _glareSourceValid = true;
        }
    }

    // Пока светим в глаза — держим/обновляем ослепление (linger = сколько ещё висит после ухода луча).
    private void StartOrRefreshBlind(Character victim, float linger)
    {
        if (!_blindOn.Value || victim == null) return;

        // ЗАЩИТА ОТ УДЕРЖАНИЯ. Проверяем здесь, а не у стрелка: этот код выполняется
        // и на клиенте самой жертвы, поэтому её нельзя держать ослеплённой, что бы
        // ей ни присылали. Иначе зеркалом можно просто выключить человека из игры.
        int key = VictimKey(victim);
        if (_cooldownUntil.TryGetValue(key, out float until) && Time.time < until) return;

        if (_blinds.TryGetValue(victim, out var st))
        {
            if (_maxBlind.Value > 0f && Time.time - st.beganAt >= _maxBlind.Value)
            {
                _cooldownUntil[key] = Time.time + Mathf.Max(0f, _cooldown.Value);
                Log.LogInfo($"Ослепление '{victim.name}' прервано по потолку {_maxBlind.Value:0.0} с; " +
                            $"повтор не раньше чем через {_cooldown.Value:0.0} с.");
                RestoreBlind(victim);
                return;
            }
        }
        else
        {
            st = BeginBlind(victim);
        }
        st.until = Time.time + linger;

        // Настоящее сетевое ослепление (видно всем, не только держателю зеркала).
        // Affliction сама умеет освежать себя повторным AddAffliction (Stack/OnStacked) —
        // безопасно звать каждый кадр, пока луч на цели.
        bool self = victim == Character.localCharacter;
        if (victim.refs?.afflictions != null && (!self || _nativeBlindOnSelf.Value))
        {
            var blind = new Affliction_Blind { totalTime = Mathf.Max(linger, _blindAfflictionSeconds.Value) };
            try { victim.refs.afflictions.AddAffliction(blind, false); }
            catch (System.Exception e) { Log.LogWarning($"AddAffliction(Blind) failed: {e.Message}"); }
        }
        else if (self) ClearNativeBlind(victim);   // страховка: вдруг осталось с прошлого раза
    }

    /// <summary>Ключ цели для кулдауна: ViewID, если персонаж сетевой, иначе id объекта.</summary>
    private static int VictimKey(Character c)
    {
        var v = c.photonView;
        return v != null && v.ViewID != 0 ? v.ViewID : c.GetInstanceID();
    }

    private BlindState BeginBlind(Character victim)
    {
        var st = new BlindState { beganAt = Time.time };
        _blinds[victim] = st;

        var cc = victim.GetComponentInChildren<CharacterCustomization>();
        if (cc != null && cc.passedOutEyes != null)
        {
            st.forcePrev = cc.forcePresetEyes;
            cc.forcePresetEyes = true;
            cc.CurrentEyeTexture = cc.passedOutEyes;
            ApplyEyeTexture(cc, cc.passedOutEyes, st.faceBackup);   // Eye_Spin прямо на рендереры
            ApplyMouthTexture(cc, st.faceBackup);                   // и рот ослеплённого
        }

        // Эмоут НЕ переигрываем, если совсем недавно уже играли на этом же персонаже.
        // Иначе при перезапуске эффекта руки опускаются и поднимаются заново.
        bool replayEmote = victim != _lastEmoteVictim || Time.time - _lastEmoteTime > 4f;
        if (replayEmote) { _lastEmoteVictim = victim; _lastEmoteTime = Time.time; }

        if (replayEmote && _useEmote.Value && !string.IsNullOrEmpty(_emoteName.Value))
        {
            PlayVictimEmote(victim, _emoteName.Value);
            st.emoteStarted = Time.time;
            st.emoteState = _emoteState;
        }

        // Фиксируем направление взгляда — только своей камере.
        if (victim == Character.localCharacter)
        {
            var mcam = Camera.main;
            _lockLook = mcam != null;
            if (_lockLook) _lockedRot = mcam!.transform.rotation;

            // rooted: стоит на месте. Запоминаем ИСХОДНОЕ значение движения,
            // чтобы гарантированно вернуть (иначе персонаж застревает навсегда).
            st.mv = victim.GetComponentInChildren<CharacterMovement>();
            if (st.mv != null) st.moveModPrev = st.mv.movementModifier;
        }

        FlinchHead(victim);
        Log.LogInfo($"Blind begin on {victim.name} (всего целей: {_blinds.Count})");
        return st;
    }

    private void TickBlind()
    {
        if (_blinds.Count == 0) return;

        // Список ключей копируем: внутри цикла снимаем эффект и меняем словарь.
        _blindScratch.Clear();
        foreach (var kv in _blinds) _blindScratch.Add(kv.Key);

        foreach (var victim in _blindScratch)
        {
            if (victim == null) { _blinds.Remove(victim!); continue; }
            if (!_blinds.TryGetValue(victim, out var st)) continue;

            if (Time.time >= st.until) { RestoreBlind(victim); continue; }

            // Жертва умерла, отключилась или её вырубило игрой — эффект снимаем,
            // иначе он останется висеть на трупе или утечёт вместе с персонажем.
            var d = victim.data;
            if (d == null || d.dead || d.fullyPassedOut)
            {
                Log.LogInfo($"Blind снят с '{victim.name}': персонаж умер или потерял сознание.");
                RestoreBlind(victim);
                continue;
            }

            var cc = victim.GetComponentInChildren<CharacterCustomization>();
            if (cc != null)
            {
                if (cc.passedOutEyes != null)
                {
                    cc.forcePresetEyes = true;
                    cc.CurrentEyeTexture = cc.passedOutEyes;
                    ApplyEyeTexture(cc, cc.passedOutEyes, null);   // держим спирали каждый кадр
                    ApplyMouthTexture(cc, null);                   // и рот — игра его тоже перезаписывает
                }

                // УДЕРЖАНИЕ ПОЗЫ. animator.speed = 0 не работает: персонаж в PEAK —
                // физическая кукла, аниматор лишь задаёт ей цели, и при остановленном
                // аниматоре конечности расслабляются обратно в стойку.
                // Поэтому КАЖДЫЙ кадр заново ставим то же состояние на тот же кадр клипа.
                // Плюс держим флаг "Emote": штатный Update игры гасит его через 2 секунды.
                var an = cc.PlayerAnimator;
                if (an != null && !string.IsNullOrEmpty(st.emoteState) &&
                    Time.time - st.emoteStarted >= _poseFreezeDelay.Value)
                {
                    an.speed = 1f;
                    an.SetBool("Emote", true);
                    an.Play(st.emoteState, 0, Mathf.Clamp01(_poseFreezeAt.Value));
                    if (!st.posePinned)
                    {
                        st.posePinned = true;
                        Log.LogInfo($"Поза '{victim.name}' удерживается: кадр {_poseFreezeAt.Value:0.00}");
                    }
                }
            }

            if (st.mv != null) st.mv.movementModifier = 0f;

            // Звёздочки на глазах и экранная пелена — не наши: их включает нативная
            // Affliction_Blind. Если её не вешаем, держим погашенными принудительно.
            if (victim == Character.localCharacter && !_nativeBlindOnSelf.Value)
                ClearNativeBlind(victim);
        }
    }

    private readonly List<Character> _blindScratch = new();

    /// <summary>
    /// Снимает ВСЁ нативное ослепление игры: аффликцию, экранную пелену и звёздочки на модели.
    /// Пелену рисует IllegalScreenEffect — отдельный квад у камеры с приватным таймером;
    /// снаружи его гасят только тем же статусом с нулевой длительностью.
    /// Звёздочки игра включает через gameObject.SetActive, а не Renderer.enabled —
    /// поэтому прежний EmergencyUnstick их и не снимал.
    /// </summary>
    private void ClearNativeBlind(Character? c)
    {
        if (c == null) return;
        try { c.refs?.afflictions?.RemoveAffliction(Affliction.AfflictionType.Blind); }
        catch (System.Exception e) { Log.LogWarning($"RemoveAffliction(Blind): {e.Message}"); }
        try { c.illegalStatusAction?.Invoke("BLIND", 0f); }
        catch (System.Exception e) { Log.LogWarning($"clear BLIND screen: {e.Message}"); }
        var br = c.refs?.customization?.refs?.blindRenderer;
        if (br != null)
        {
            if (br.gameObject.activeSelf) br.gameObject.SetActive(false);
            if (br.enabled) br.enabled = false;
        }
    }

    /// <summary>Снимает ослепление с ОДНОЙ жертвы. Остальные продолжают стоять ослеплёнными.</summary>
    private void RestoreBlind(Character? victim)
    {
        if (victim == null || !_blinds.TryGetValue(victim, out var st)) return;
        _blinds.Remove(victim);

        var cc = victim.GetComponentInChildren<CharacterCustomization>();
        if (cc != null) cc.forcePresetEyes = st.forcePrev;      // вернуть обычные глаза
        RestoreFaceTextures(st.faceBackup);
        ClearNativeBlind(victim);
        if (cc != null && cc.PlayerAnimator != null)
        {
            cc.PlayerAnimator.speed = 1f;                 // вернуть нормальный ход
            cc.PlayerAnimator.SetBool("Emote", false);    // отпустить позу
        }
        if (st.mv != null) st.mv.movementModifier = st.moveModPrev;   // ОБЯЗАТЕЛЬНО вернуть движение
        if (victim == Character.localCharacter) _lockLook = false;    // вернуть управление взглядом

        // Небольшая пауза после КАЖДОГО снятия: без неё луч, сошедший с глаз на долю
        // секунды, тут же начинает новое ослепление, и получается то же удержание.
        if (_cooldown.Value > 0f)
        {
            // ВАЖНО: не затирать длинную паузу, поставленную при обрыве по потолку.
            int ck = VictimKey(victim);
            float shortUntil = Time.time + _cooldown.Value * 0.5f;
            if (!_cooldownUntil.TryGetValue(ck, out float cur) || cur < shortUntil)
                _cooldownUntil[ck] = shortUntil;
        }
        Log.LogInfo($"Blind RESTORE '{victim.name}' (осталось целей: {_blinds.Count})");
    }

    /// <summary>Снимает ослепление со всех разом — выгрузка мода, уборка стенда, аварийный сброс.</summary>
    private void RestoreAllBlinds()
    {
        if (_blinds.Count == 0) return;
        _blindScratch.Clear();
        foreach (var kv in _blinds) _blindScratch.Add(kv.Key);
        foreach (var v in _blindScratch) RestoreBlind(v);
    }

    // На случай если персонаж уже застрял из-за предыдущей сборки — сразу возвращаем движение.
    private void EmergencyUnstick()
    {
        var me = Character.localCharacter;
        if (me == null) return;
        var mv = me.GetComponentInChildren<CharacterMovement>();
        if (mv != null && mv.movementModifier <= 0f)
        {
            mv.movementModifier = 1f;
            Log.LogInfo("EmergencyUnstick: movementModifier forced back to 1.");
        }

        // Снимаем всё, что могло залипнуть после горячих перезагрузок плагина.
        var cc = me.GetComponentInChildren<CharacterCustomization>();
        if (cc != null)
        {
            if (cc.PlayerAnimator != null && cc.PlayerAnimator.speed == 0f)
            { cc.PlayerAnimator.speed = 1f; Log.LogInfo("EmergencyUnstick: анимация разморожена."); }
            cc.forcePresetEyes = false;
        }
        // Снимаем эффект со ВСЕХ, а не только с себя: аварийная клавиша должна
        // расхлопнуть всё, что мод навесил, включая чужих персонажей.
        RestoreAllBlinds();
        ClearNativeBlind(me);
        _lockLook = false;
        Log.LogWarning("[U] EmergencyUnstick: нативное ослепление снято (аффликция + пелена + звёздочки), " +
                       "движение и анимация возвращены.");
    }

    /// <summary>
    /// Отладка: вылет из аэропорта на остров без беготни до киоска.
    /// AirportCheckInKiosk.StartGame(ascent) — публичный метод самой игры,
    /// он шлёт тот же RPC, что и обычная регистрация на рейс.
    /// </summary>
    private void DebugBoardFlight()
    {
        var kiosk = Object.FindFirstObjectByType<AirportCheckInKiosk>();
        if (kiosk == null) { Log.LogWarning("[P] Киоск регистрации не найден — мы не в аэропорту."); return; }
        Log.LogWarning("[P] Вылет на остров: AirportCheckInKiosk.StartGame(0).");
        kiosk.StartGame(0);
    }

    /// <summary>Отладка: перевести игровое время на полдень — чтобы тестировать зайчик при солнце.</summary>
    private void DebugNoon()
    {
        var dn = DayNightManager.instance;
        if (dn == null) { Log.LogWarning("[M] DayNightManager не найден."); return; }
        dn.timeOfDay = 12f;
        // Заодно лечим: при долгой отладке персонаж успевает умереть от голода,
        // и забег приходится начинать заново.
        var me = Character.localCharacter;
        var aff = me?.refs?.afflictions;
        if (aff != null)
        {
            try { aff.ClearAllStatus(excludeCurse: false, excludePetrify: false); }
            catch (System.Exception e) { Log.LogWarning($"[M] ClearAllStatus: {e.Message}"); }
        }
        Log.LogWarning($"[M] Полдень (isDay={dn.isDay}) + статусы сняты.");
    }

    /// <summary>
    /// Отладка: убить ослеплённую жертву (кроме себя), чтобы проверить, что эффект
    /// снимается, а не остаётся висеть на трупе. RPCA_SetDead — метод самой игры,
    /// он и выставляет data.dead, по которому мы жертву отпускаем.
    /// </summary>
    private void DebugKillVictim()
    {
        Character? target = null;
        foreach (var kv in _blinds)
            if (kv.Key != null && kv.Key != Character.localCharacter) { target = kv.Key; break; }

        if (target == null) { Log.LogWarning("[F12] Нет ослеплённой жертвы, кроме тебя самого."); return; }
        Log.LogWarning($"[F12] Убиваю ослеплённого '{target.name}' — смотрим, снимется ли эффект.");
        try { target.RPCA_SetDead(); }
        catch (System.Exception e) { Log.LogWarning($"[F12] RPCA_SetDead: {e.Message}"); }
    }

    /// <summary>
    /// Отладка: поставить бота рядом. Бот — полноценный Character с IsLocal=false,
    /// то есть ровно тот случай, на котором ломается применение эффектов,
    /// и его можно воспроизвести без второго игрока.
    /// </summary>
    private void DebugSpawnBot()
    {
        var sp = Object.FindFirstObjectByType<BotSpawner>();
        var me = Character.localCharacter;
        if (sp == null || me == null) { Log.LogWarning("[F11] BotSpawner или персонаж не найдены."); return; }
        // Ставим ПО ВЗГЛЯДУ, а не по направлению тела: в PEAK они расходятся,
        // и бот оказывался сбоку от экрана.
        var cam = Camera.main;
        Vector3 fwd = cam != null ? cam.transform.forward : me.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = me.transform.forward;
        Vector3 where = me.Center + fwd.normalized * 4f;
        sp.SpawnBot(where);
        StartCoroutine(FaceNewBot(where, me));
        Log.LogWarning($"[F11] Бот заспавнен у {where}.");
    }

    /// <summary>
    /// Разворачивает только что созданного бота лицом к игроку: иначе он стоит спиной
    /// и реакцию (глаза, рот, позу) просто не видно. Ищем по близости к точке спавна —
    /// в Character.AllCharacters новичок попадает не сразу, поэтому ждём и пробуем несколько раз.
    /// </summary>
    private IEnumerator FaceNewBot(Vector3 where, Character me)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            yield return new WaitForSeconds(0.2f);
            Character? found = null;
            float best = 6f;
            // Ищем по сцене, а не по Character.AllCharacters: локально созданный бот
            // в этот список не попадает (он не регистрируется как сетевой персонаж).
            foreach (var c in Object.FindObjectsByType<Character>(FindObjectsSortMode.None))
            {
                if (c == null || !c.isBot) continue;
                float d = Vector3.Distance(c.Center, where);
                if (d < best) { best = d; found = c; }
            }
            if (found == null) continue;

            Vector3 dir = me.Center - found.Center; dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) yield break;
            found.transform.rotation = Quaternion.LookRotation(dir.normalized);
            // Одного разворота мало: ИИ бота вернёт его обратно через кадр.
            // Вешаем компонент, который держит его неподвижным и лицом к игроку.
            if (!HasOurComponent(found.gameObject, "SunMirrorDummy"))
                found.gameObject.AddComponent<SunMirrorDummy>();
            Log.LogWarning($"[F11] Болванчик '{found.name}' поставлен лицом к игроку (попытка {attempt + 1}).");
            yield break;
        }
        Log.LogWarning("[F11] Бот рядом с точкой спавна не нашёлся — развернуть не смог.");
    }

    /// <summary>ВРЕМЕННО: что из сетевого/ботов вообще доступно в текущей сцене.</summary>
    private void ProbeNetwork()
    {
        var spawners = Object.FindObjectsByType<BotSpawner>(FindObjectsSortMode.None);
        var chars = Object.FindObjectsByType<Character>(FindObjectsSortMode.None);
        Log.LogWarning($"[NET] BotSpawner в сцене: {spawners.Length}; " +
                       $"botPrefab задан: {(spawners.Length > 0 && spawners[0].botPrefab != null)}");
        Log.LogWarning($"[NET] Character в сцене: {chars.Length}");
        foreach (var c in chars)
            Log.LogWarning($"[NET]   '{c.name}' isBot={c.isBot} IsLocal={c.IsLocal} " +
                           $"viewID={(c.photonView != null ? c.photonView.ViewID : -1)}");
        Log.LogWarning($"[NET] PhotonNetwork: InRoom={PhotonNetwork.InRoom} " +
                       $"IsMaster={PhotonNetwork.IsMasterClient} Offline={PhotonNetwork.OfflineMode} " +
                       $"игроков={(PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 0)}");

        // Сколько наших компонентов реально висит в сцене: после hot-reload старые
        // не снимались и копились, а RPC вызывался столько раз, сколько их накопилось.
        int net = 0, dummy = 0;
        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (mb == null) continue;
            string n = mb.GetType().Name;
            if (n == "SunMirrorNet") net++;
            else if (n == "SunMirrorDummy") dummy++;
        }
        Log.LogWarning($"[NET] наших компонентов в сцене: SunMirrorNet={net}, SunMirrorDummy={dummy} " +
                       $"(персонажей {Character.AllCharacters.Count} — больше одного на персонажа это мусор)");

        if (_fpsFrames > 0)
        {
            float avgFrame = _fpsAccum / _fpsFrames * 1000f;
            float avgSelf = _selfMs / _fpsFrames;
            Log.LogWarning($"[PERF] кадр {avgFrame:0.00} мс ({1000f / avgFrame:0} fps), " +
                           $"из них наш Update {avgSelf:0.000} мс ({avgSelf / avgFrame * 100f:0.0}%), " +
                           $"замер по {_fpsFrames} кадрам; ослеплено {_blinds.Count}, зеркало в руках {_holdingMirror}");
            _fpsAccum = 0f; _fpsFrames = 0; _selfMs = 0f;
        }
    }

    /// <summary>Печатает список доступных ртов — чтобы вписать нужный в конфиг BlindMouthName.</summary>
    private void DumpMouths()
    {
        var list = Singleton<Customization>.Instance?.mouths;
        if (list == null) { Log.LogWarning("[N] Customization.mouths недоступен."); return; }
        Log.LogWarning($"[N] === рты ({list.Length} шт.), имя вписывать в Emote/BlindMouthName ===");
        for (int i = 0; i < list.Length; i++)
        {
            var o = list[i];
            if (o == null) continue;
            Log.LogWarning($"[N]   {i,2}: '{o.name}' текстура='{(o.texture != null ? o.texture.name : "НЕТ")}'" +
                           $"{(o.isBlank ? " (пустой)" : "")}");
        }
    }

    private void SelfRagdollTest()
    {
        var me = Character.localCharacter;
        if (me == null) { Log.LogInfo("SelfTest: no local character."); return; }
        LogEmotesOnce();   // чтобы в логе был список с русскими именами
        _lastLocalHitTime = Time.time;   // засвет экрана на себе
        _selfDemoUntil = Time.time + _stunDelay.Value;   // показать всю визуалку на себе (для вида сбоку)
        if (_useEmote.Value && !string.IsNullOrEmpty(_emoteName.Value))
            PlayVictimEmote(me, _emoteName.Value);
        StartOrRefreshBlind(me, _stunDelay.Value);       // спиральные глаза + zzz + rooted (без падения)
        Log.LogInfo("SelfTest: blind on self (no fall).");
    }

    private void LogEmotesOnce()
    {
        if (_emotesLogged) return;
        _emotesLogged = true;
        try
        {
            var all = Resources.FindObjectsOfTypeAll<EmoteWheelData>();
            Log.LogInfo($"=== Available emotes ({all.Length}) ===");
            foreach (var e in all) Log.LogInfo($"  emoteName='{e.emoteName}'  localized='{SafeLocalize(e.emoteName)}'  anim='{e.anim}'");
        }
        catch (System.Exception ex) { Log.LogWarning($"Emote listing failed: {ex.Message}"); }
    }

    // Реальная кость кисти (Hand_R) — всегда в руке, движется с персонажем.
    private static Transform GetHandAnchor(Character me)
    {
        var bone = FindHandBone(me);
        if (bone != null) return bone;
        var r = me.refs;
        if (r != null && r.animationItemTransform != null) return r.animationItemTransform;
        if (Camera.main != null) return Camera.main.transform;
        return me.transform;
    }

    private static Transform? FindHandBone(Character me)
    {
        Transform? right = null, left = null, any = null;
        foreach (var t in me.GetComponentsInChildren<Transform>())
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("ik") || n.Contains("target") || n.Contains("upper") || n.Contains("shirt")
                || n.Contains("finger") || n.Contains("thumb")) continue;
            if (!(n.Contains("hand") || n.Contains("wrist") || n.Contains("palm"))) continue;
            any ??= t;
            if (n.Contains("_r") || n.Contains(".r") || n.Contains("right")) right ??= t;
            else if (n.Contains("_l") || n.Contains(".l") || n.Contains("left")) left ??= t;
        }
        return right ?? left ?? any;
    }

    private static void RecenterAndScale(GameObject root, float targetSize)
    {
        var rends = root.GetComponentsInChildren<Renderer>();
        if (rends.Length == 0) { Log.LogWarning("Model has no renderers!"); return; }
        var b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
        Vector3 localCenter = root.transform.InverseTransformPoint(b.center);
        foreach (Transform child in root.transform) child.localPosition -= localCenter;
        float max = Mathf.Max(b.size.x, b.size.y, b.size.z);
        if (max > 1e-4f) root.transform.localScale *= targetSize / max;
        Log.LogInfo($"Model bounds size={b.size}, renderers={rends.Length}, scaled to ~{targetSize}m.");
    }

    private static void TuneMirrorMaterial(GameObject root)
    {
        foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
        {
            var m = mr.sharedMaterial;
            if (m == null) continue;
            if (m.name.ToUpperInvariant().Contains("MIRROR"))
            {
                // светлое серебро — читается как зеркало, меньше синевы неба
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", new Color(0.82f, 0.84f, 0.88f, 1f));
                if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(0.82f, 0.84f, 0.88f, 1f));
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.85f);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.9f);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.9f);
                if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 1f);
                if (m.HasProperty("_EmissionColor"))
                {
                    m.EnableKeyword("_EMISSION");
                    m.SetColor("_EmissionColor", new Color(0.85f, 0.87f, 0.9f) * 0.25f);
                }
            }
        }
    }
}
