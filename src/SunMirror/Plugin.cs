using System.Collections.Generic;
﻿using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Peak.Afflictions;
using Peak.Network;
using Photon.Pun;
using Photon.Realtime;
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
    private ConfigEntry<bool> _fallAtMax = null!;
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
    private ConfigEntry<float> _ringStrength = null!;
    private ConfigEntry<string> _blindMouth = null!;
    private ConfigEntry<float> _poseFreezeAt = null!;
    private ConfigEntry<float> _poseSwing = null!, _poseSwingSpeed = null!;
    private ConfigEntry<float> _netSendEvery = null!;
    private ConfigEntry<bool> _flinch = null!;
    private ConfigEntry<bool> _debugKeys = null!;
    private ConfigEntry<bool> _autoJoinRoom = null!;
    private ConfigEntry<bool> _freezeNoon = null!;
    private string? _guestUserId;   // свой Photon UserId для второго клиента на той же машине
    private ConfigEntry<float> _signalGrow = null!;
    private ConfigEntry<float> _signalMax = null!;
    private ConfigEntry<float> _signalLightBright = null!;
    // Сглаженная яркость прожектора: сырое значение дёргается каждый кадр.
    private float _spotLitSmooth;
    // Отладка мерцания: 0 — всё включено, 1 — без точечных светов, 2 — без свечения,
    // 3 — ни того, ни другого. Поле, а не конфиг: не должно пережить перезапуск.
    private int _glowStage;
    private ConfigEntry<float> _beamLinger = null!;
    private ConfigEntry<float> _maxBlind = null!;
    private ConfigEntry<float> _cooldown = null!;
    private ConfigEntry<bool> _eyesOnly = null!;
    private ConfigEntry<bool> _poseLoop = null!;
    private ConfigEntry<float> _reactionEvery = null!;
    private ConfigEntry<float> _chargeDrain = null!;
    private ConfigEntry<bool> _rootVictim = null!, _spiralEyesNet = null!;
    private ConfigEntry<float> _lightReach = null!;
    private ConfigEntry<bool> _useLight = null!;
    private ConfigEntry<bool> _backPlate = null!, _ownIcon = null!;
    private ConfigEntry<string> _prefabFilter = null!;
    private ConfigEntry<float> _prefabDumpAtStart = null!;
    private ConfigEntry<string> _userIdSuffix = null!;
    private ConfigEntry<bool> _autoBlind = null!;
    private ConfigEntry<float> _autoBlindRange = null!;
    private float _prefabDumpAt = -1f;
    private ConfigEntry<float> _backPlateGap = null!, _backPlateScale = null!;
    private static Texture2D? _idleTex, _backTex, _iconTex;
    private Transform? _backPlateTf;
    private bool _iconReplaced;
    private ConfigEntry<float> _selfBlindDelay = null!;
    private float _selfBlindAt = -1f;
    private ConfigEntry<float> _flashEmission = null!, _flashPulse = null!, _flashPulseSpeed = null!;
    private ConfigEntry<float> _synthVolume = null!, _synthPitch = null!;
    private ConfigEntry<float> _emoteFirstHold = null!;
    // Накопленное ослепление по цели: сколько «фитиля» догорело.
    private readonly Dictionary<int, (float charge, float at)> _blindCharge = new();
    private readonly Dictionary<int, float> _reactionAt = new();
    private ConfigEntry<float> _poseLoopSpeed = null!, _poseLoopSpan = null!;
    private ConfigEntry<string> _mirrorItemName = null!, _sfxFilter = null!;
    private ConfigEntry<float> _eyesRadius = null!;
    private ConfigEntry<float> _flinchForce = null!;
    private ConfigEntry<float> _wobbleForce = null!, _wobbleEvery = null!, _shakeOffForce = null!, _staggerForce = null!;
    private ConfigEntry<string> _emoteB = null!, _blindSfx = null!;
    private ConfigEntry<float> _emoteSwapEvery = null!;
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
    private bool _emotesLogged;

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
        _spread        = Config.Bind("Spot", "SpreadPerMeter", 0.009f, "Прибавка к диаметру на метр (рассеяние). 0.009 — настоящая расходимость солнечного зайчика: угловой размер Солнца 0,53 градуса даёт ~9 мм на метр. На 60 м пятно около метра.");
        _spotGlow      = Config.Bind("Spot", "Glow", 14f, "Яркость прожектора.");
        // Отдельный выключатель прожектора: это настоящий источник света, и в URP
        // лишний свет заставляет соседние объекты терять и возвращать освещение
        // по кадрам. Без выключателя проверить его причастность к мерцанию
        // можно было только пересборкой.
        // Проверка «что будет с тем, кто лезет по скале»: светить себе и одновременно
        // карабкаться нельзя — руки заняты. Поэтому ослепление приходит с задержкой:
        // нажал, успел залезть, и тебя накрывает уже на стене.
        _selfBlindDelay = Config.Bind("Debug", "SelfBlindDelaySeconds", 8f,
            "Через сколько секунд после F2 ослепить тебя самого. Нужно, чтобы успеть залезть на скалу и посмотреть, что ослепление и падение делают с тем, кто висит на стене.");
        _useLight      = Config.Bind("Spot", "UseLight", true,
            "Подсвечивать место попадания настоящим источником света. Выключить, если на соседних поверхностях заметно мерцание.");
        _heightFactor  = Config.Bind("Spot", "HeightFactor", 2f, "Высота источника = размер×это (больше = мягче край).");
        // Насколько прожектор добивает ЗА поверхность, куда попал зайчик.
        // Раньше запас был «размер + 0.5 м»: на песке он уходил под землю и был незаметен,
        // а стоило навести луч на лицо — за поверхностью оказывалась стена, и на ней
        // загоралось второе, паразитное пятно. Теперь запас маленький и не зависит от размера.
        _lightReach    = Config.Bind("Spot", "LightReachBeyond", 0.3f,
            "На сколько метров прожектор светит за поверхность попадания. Больше — мягче край пятна, но начинает подсвечивать то, что стоит за целью.");
        _smooth        = Config.Bind("Spot", "SmoothSpeed", 22f, "Сглаживание хода пятна (больше = резче/быстрее).");
        _showSprite    = Config.Bind("Spot", "ShowSprite", true, "Яркий спрайт в центре пятна.");
        _spriteBright  = Config.Bind("Spot", "SpriteBrightness", 2.4f, "Яркость зайчика. Больше 1 — уходит в пересвет и ловит блум; на ярком песке меньше 1.5 не читается.");
        _ringStrength  = Config.Bind("Spot", "ContrastRing", 0f,
            "ОПЫТНОЕ, по умолчанию выключено. Тёмный ободок вокруг зайчика: аддитивное пятно на ярком песке не читается, ободок затемняет фон по краю и возвращает контраст. Включать значением 0.3-0.6.");
        _requireSun    = Config.Bind("Spot", "RequireSunlight", true, "Светить только когда солнце падает на игрока (день и не в тени).");
        _fallSeconds   = Config.Bind("Effect", "FallSeconds", 1.6f, "Секунд рэгдолла.");
        _fallShake     = Config.Bind("Effect", "FallShake", 0.6f, "Тряска экрана.");
        _fallAtMax     = Config.Bind("Emote", "FallAtMaxBlind", true,
            "Ронять цель, когда накопится MaxBlindSeconds ослепления. Падение летит штатным RPC игры, поэтому валит и тех, у кого мода нет. Выключить, если в лобби это лишнее.");
        _applyStatuses = Config.Bind("Effect", "ApplyStatuses", false, "Spores/Drowsy (на незнакомцах может не сработать).");
        _sporesAmount  = Config.Bind("Effect", "SporesAmount", 0.5f, "Кол-во Spores.");
        _drowsyAmount  = Config.Bind("Effect", "DrowsyAmount", 0.4f, "Кол-во Drowsy.");
        _glareStrength = Config.Bind("Effect", "GlareStrength", 0.75f, "Сила засвета экрана, когда светят в тебя (0..1).");
        _useEmote      = Config.Bind("Emote", "UseEmote", true, "Эмоут-реакция перед станом.");
        _emoteName     = Config.Bind("Emote", "EmoteName", "Defeat",
            "Имя эмоута ослепления. Берите английский ключ из списка по клавише N (Defeat, Nono, Wave...), а НЕ локализованную подпись: локализованная зависит от языка игры и у английского игрока не сопоставится.");
        _stunDelay     = Config.Bind("Emote", "StunDelaySeconds", 5f, "Секунд светить в глаза до падения (эмоут+звёздочки всё это время).");
        _blindHoldSeconds = Config.Bind("Emote", "BlindHoldSeconds", 6f, "Сколько держится ослепление после попадания зайчика в глаза.");
        _poseFreezeDelay = Config.Bind("Emote", "PoseFreezeDelay", 0.55f, "Через сколько секунд после начала эмоута заморозить позу (руки у глаз).");
        _rigAimTime    = Config.Bind("Debug", "RigAimSeconds", 1.0f, "Сколько зеркало-стенд наводит зайчика до ослепления.");
        _debugKeys = Config.Bind("Debug", "DebugKeys", false, "Отладочные клавиши (B K L J C H U N P M O F5-F12 и подстройка наклона). По умолчанию выключены: обычному игроку они не нужны.");
        _freezeNoon    = Config.Bind("Debug", "FreezeNoon", false,
            "Держать игровое время на полудне. Для отладки и съёмки: иначе солнце садится посреди работы и зайчик перестаёт работать.");
        _autoJoinRoom = Config.Bind("Debug", "AutoJoinHandshakeRoom", false,
            "Отладка на одной машине: при запуске сам войти в комнату, имя которой хост положил в sunmirror_room.txt (клавиша F7). Ставится только на втором клиенте.");
        if (_autoJoinRoom.Value) StartCoroutine(AutoJoinLoop());
        _selfTarget    = Config.Bind("Debug", "SelfTarget", false,
            "Луч всегда бьёт в тебя — тест эффекта без второго игрока (клавиша J). Сбрасывается при каждом запуске: это тумблер на один заход, а не настройка.");
        // Гасим на старте. Тумблер сохранялся в файл и переживал перезапуски:
        // одно случайное нажатие J — и мод молча начинал слепить хозяина вместо
        // цели. В логе при этом всё выглядело исправным (попадание находилось
        // верно, а жертвой подставлялся ты), поэтому причину было не видно.
        if (_selfTarget.Value)
        {
            _selfTarget.Value = false;
            Config.Save();
            Log.LogWarning("Debug/SelfTarget был включён в конфиге — выключил. " +
                           "Иначе зайчик слепил бы тебя самого, а не цель. Включается клавишей J на один заход.");
        }
        _blindOn       = Config.Bind("Emote", "BlindStars", true, "Ослепление со звёздочками над головой (Affliction_Blind).");
        // Было 2.5 с: у игрока БЕЗ мода пелена висела ещё две с половиной секунды
        // после ухода луча. Для «без луча глаза открыты» это и есть главный виновник,
        // потому что именно эту пелену видит ванильный игрок.
        _blindAfflictionSeconds = Config.Bind("Emote", "BlindAfflictionSeconds", 0.6f,
            "Мин. длительность нативного Affliction_Blind за одно обновление (сетевое, видно всем). Это же задаёт, насколько эффект переживает уход луча у игроков без мода.");
        // Единственное обездвиживание, которое доходит до игрока БЕЗ мода.
        // Наше movementModifier = 0 работает только на своём клиенте, а RPCA_Stick —
        // родной метод игры: его исполняет клиент жертвы своим кодом.
        // Безопасно: игра сама снимает сочленение через 4 секунды, и командует этим
        // клиент самой жертвы, так что зависнуть прибитым нельзя, даже если стрелок
        // вылетит из игры.
        // Спиральные глаза для тех, у кого мода нет.
        // CharacterCustomization.CharacterPassedOut — сетевой метод САМОЙ игры, и он
        // чисто косметический: меняет текстуру глаз и включает вращение (Spin = 1).
        // Настоящая отключка живёт отдельно, в Character.RPCA_PassOut, и мы её не трогаем.
        // Проверено по коду игры: поле isPassedOut у этого компонента не читает никто,
        // кроме него самого, так что на игру это не влияет. Возврат — парным OnRevive_RPC.
        _spiralEyesNet = Config.Bind("Emote", "SpiralEyesForEveryone", true,
            "Показывать спиральные глаза и игрокам БЕЗ мода, родным методом игры. Рот так передать нельзя — у него сетевого пути нет, он остаётся только у владельцев мода.");
        _rootVictim = Config.Bind("Emote", "RootVictim", true,
            "Прибивать ослеплённого на месте штатным механизмом игры (RPCA_Stick). Это единственное обездвиживание, которое видит игрок без мода. Игра сама отпускает через 4 секунды. На карабкающихся не применяется.");
        _chargeDrain = Config.Bind("Emote", "BlindChargeDrain", 0.5f,
            "Как быстро стекает накопленное ослепление, когда луч ушёл с глаз (доля от реального времени). 1.0 — стекает ровно столько же, сколько копилось; 0 — не стекает вовсе, цель запоминает всё.");
        // Свой звук синтезируем в рантайме: ни один из 885 звуков игры не подошёл,
        // а тащить в пакет мода аудиофайл — лишний вес и лишняя загрузка.
        _synthVolume = Config.Bind("Emote", "SynthVolume", 0.55f,
            "Громкость своего (синтезированного) звука.");
        _synthPitch = Config.Bind("Emote", "SynthPitch", 1f,
            "Высота своего звука. 0.8 — ниже и глуше, 1.3 — выше и звонче.");
        _reactionEvery = Config.Bind("Emote", "ReactionEverySeconds", 1.2f,
            "Как часто повторять удар головой и звук при непрерывном ослеплении. Защита от «пулемёта» при поводке луча по лицу.");
        _netSendEvery = Config.Bind("Emote", "NetSendEverySeconds", 0.25f, "Как часто слать ослепление по сети, пока луч на цели (каждый кадр — флуд).");
        _flinch = Config.Bind("Emote", "VanillaFlinch", true, "Отдёргивание головы при попадании. Единственное, что физически чувствует игрок БЕЗ мода: сетевой метод самой игры.");
        // Свечение зеркала раскачивало автоэкспозицию игры. Зеркало занимает низ
        // экрана и светится ярче белого, а его яркость пульсировала синусоидой с
        // периодом 0.52 с. Экспозиция шла за этой пульсацией — и вместе с зеркалом
        // «дышала» вся картинка. Со стороны это читалось как мерцание поверхности,
        // хотя поверхность была ни при чём. Теперь пульсация почти выключена.
        _flashEmission   = Config.Bind("Model", "FlashEmission", 2.2f,
            "Яркость свечения зеркала во время свечения зайчиком. Выше 3 начинает раскачивать автоэкспозицию игры, и вся сцена заметно пульсирует.");
        _flashPulse      = Config.Bind("Model", "FlashPulseAmount", 0.06f,
            "Насколько свечение зеркала пульсирует (доля от яркости). Было 0.3 — именно это и давало мерцание всей сцены. 0 — ровное свечение.");
        _flashPulseSpeed = Config.Bind("Model", "FlashPulseSpeed", 12f,
            "Скорость пульсации свечения. 12 даёт период около полусекунды.");
        _signalGrow = Config.Bind("Spot", "SignalGrowPerMeter", 0.02f, "Насколько вспышка на зеркале растёт с каждым метром — чтобы её было видно издалека.");
        _signalMax = Config.Bind("Spot", "SignalMaxSize", 3.5f, "Потолок размера вспышки. Без него на дальней дистанции зеркало заслоняет пол-экрана.");
        _signalLightBright = Config.Bind("Spot", "SignalLightBrightness", 12f,
            "Яркость точечного света у вспышки на зеркале. Он тоже попадает в замер автоэкспозиции: если при наводке сцена заметно темнеет — убавить.");
        // Четыре, а не пять, и это не круглое число ради красоты: ровно через 4 секунды
        // игра сама снимает удержание (HandleStickUpdate -> RPCA_ClearJoint). При пяти
        // получался разрыв — на 4-й секунде жертва освобождалась и секунду стояла
        // свободной, а падала на 5-й. Теперь отпускание и падение совпадают:
        // держало — отпустило и тут же уронило. Если менять здесь, помнить про те 4 с
        // в коде игры: они не наши и правке не поддаются.
        _maxBlind = Config.Bind("Emote", "MaxBlindSeconds", 4f,
            "Потолок накопленного ослепления: после него цель роняет и наступает пауза. Совпадает с 4 секундами, через которые игра сама снимает удержание, — иначе между освобождением и падением образуется разрыв.");
        // Было 3 с: глаза оставались спиральными ещё долго после ухода луча.
        // Теперь ровно столько, чтобы перекрыть паузу между сетевыми посылками
        // (NetSendEverySeconds) — иначе эффект мигал бы на чужих клиентах.
        // Меньше 0.3 ставить нельзя по той же причине.
        _beamLinger = Config.Bind("Emote", "BeamLingerSeconds", 0.4f,
            "Сколько ослепление держится после ухода луча с глаз. Держит эффект между сетевыми посылками, поэтому должно быть больше NetSendEverySeconds (0.25).");
        _cooldown = Config.Bind("Emote", "CooldownSeconds", 4f, "Пауза, в течение которой ту же цель ослепить нельзя. Защита от удержания в цикле.");
        _eyesOnly = Config.Bind("Emote", "EyesOnly", true,
            "Слепить только при попадании в голову. Выключить — слепит попадание в любую часть тела, как было в 0.1.3.");
        _eyesRadius = Config.Bind("Emote", "EyesRadius", 0.45f,
            "Радиус вокруг головы (в метрах), в котором попадание считается попаданием в глаза. Прямое попадание в коллайдер головы засчитывается всегда, независимо от радиуса. Меньше 0.25 — попасть почти невозможно, больше 0.6 — ловит плечи и грудь.");
        _flinchForce = Config.Bind("Emote", "VanillaFlinchForce", 14f, "Сила отдёргивания головы. Больше 30 валит персонажа — нам нужно вздрагивание, а не нокдаун.");
        // Три такта реакции. Замершая поза со стороны выглядит как зависший персонаж,
        // а PEAK — игра про потерю равновесия, в ней смешно именно шатание.
        _staggerForce  = Config.Bind("Emote", "StaggerForce", 9f,
            "Толчок всего тела в момент попадания — жертву качает назад. 0 — выключить. Больше 20 валит с ног.");
        _wobbleForce   = Config.Bind("Emote", "WobbleForce", 7f,
            "Сила покачивания, пока держится ослепление. Жертва топчется и шатается, прикрывая лицо. 0 — стоять столбом.");
        _wobbleEvery   = Config.Bind("Emote", "WobbleEverySeconds", 0.35f,
            "Как часто толкать при покачивании. Меньше — суетливее.");
        _shakeOffForce = Config.Bind("Emote", "ShakeOffForce", 11f,
            "Встряс в момент, когда луч ушёл — чтобы конец эффекта тоже читался. 0 — выключить.");
        _emoteB        = Config.Bind("Emote", "BlindEmoteB", "A_Scout_Emote_Nono",
            "Второй эмоут, с которым чередуется основной: жертва то закрывается руками, то отмахивается. Пусто — держать одну позу, как раньше. Список имён печатает клавиша N.");
        _emoteSwapEvery = Config.Bind("Emote", "EmoteSwapSeconds", 1.1f,
            "Как часто чередовать позу со вторым эмоутом. 0 — не чередовать, держать только первую позу.");
        // Порядок важен: сначала человек закрывается руками, и только потом
        // начинает отмахиваться. Без этой задержки чередование перекидывало на
        // второй эмоут на ПЕРВОМ же кадре (nextEmoteAt был нулём), и жертва
        // начинала с «no no no» — то есть отмахивалась от того, чего ещё не было.
        _emoteFirstHold = Config.Bind("Emote", "EmoteFirstHoldSeconds", 1.5f,
            "Сколько держать первую позу (руки к глазам), прежде чем начать чередование со вторым эмоутом. Это и задаёт порядок: сначала закрылся, потом отмахивается.");
        _blindSfx      = Config.Bind("Emote", "BlindSfxName", "synth:bell",
            "Звук при попадании в глаза. Пусто — без звука. Свои варианты: synth:bell, synth:shing, synth:sparkle, synth:wobble, synth:zap, synth:hum. Можно подставить и звук игры — список печатает клавиша N.");
        // Нативное ослепление игры рисует СВОЮ пелену на весь экран (IllegalScreenEffect, статус "BLIND").
        // Чужому игроку это и нужно — он без мода, иначе не почувствует ничего. А себе — нет:
        // именно эта пелена и была «туманом», который не снимался ни слоями [O], ни клавишей U.
        _nativeBlindOnSelf = Config.Bind("Emote", "NativeBlindOnSelf", false, "Вешать нативное ослепление игры (с экранной пеленой) на СЕБЯ. Для тестов обычно false — слепит только звезда.");
        _blindMouth = Config.Bind("Emote", "BlindMouthName", "", "Рот ослеплённого: имя варианта из Customization.mouths (список печатает клавиша N). Пусто — рот не менять.");
        _poseFreezeAt = Config.Bind("Emote", "PoseFreezeAt", 0.35f, "Кадр клипа эмоута (0..1), на котором держим позу. Подбор в игре: F9 раньше, F10 позже.");
        _poseSwing     = Config.Bind("Emote", "PoseSwing", 0.07f,
            "Насколько водить кадр позы туда-обратно вокруг PoseFreezeAt. 0 — застывшая поза, как было. Это и даёт движение рук у лица.");
        _poseSwingSpeed = Config.Bind("Emote", "PoseSwingSpeed", 4.5f,
            "Скорость этого покачивания позы.");
        // Замершая поза со стороны читается как зависший персонаж — это была главная
        // претензия к виду снаружи. Поэтому по умолчанию клип не держим на кадре,
        // а гоняем по кругу: жертва жмурится всё время, пока луч в глазах.
        _poseLoop = Config.Bind("Emote", "PoseLoop", true,
            "Проигрывать эмоут по кругу всё время ослепления. Выключить — держать один кадр (PoseFreezeAt), как было в 0.1.3.");
        _poseLoopSpeed = Config.Bind("Emote", "PoseLoopSpeed", 0.6f,
            "Скорость прокрутки эмоута по кругу. Больше — быстрее жмурится.");
        _poseLoopSpan = Config.Bind("Emote", "PoseLoopSpan", 0.3f,
            "Какую долю клипа гонять по кругу, начиная с PoseFreezeAt. Весь клип (1.0) опускает руки в конце — поэтому берём только участок, где руки у лица.");
        _mirrorItemName = Config.Bind("Model", "MirrorItemName", "Frisbee",
            "Английское имя предмета-зеркала (Item.UIData.itemName). Зайчик работает только когда этот предмет реально в руках.");
        // Набор отобран по каталогу из 885 звуков игры (клавиша N), в порядке
        // убывания пригодности: тематические -> звонкие -> «застыл» -> шуточные -> голос.
        _sfxFilter = Config.Bind("Emote", "SfxAuditionFilter",
            "sunspray|badgepopup|amulet|jingle|ping|petrify|curse|morale|banana|airhorn|uhhh|scoutshonor",
            "Подбор звука: клавиша . проигрывает следующего кандидата, , повторяет текущего. Несколько подстрок через '|', кандидаты идут в этом порядке. Понравившееся имя вписать в BlindSfxName.");
        _showModel     = Config.Bind("Model", "ShowModel", true, "Показывать модель зеркала в руке.");
        // Задняя крышка. У меша тарелочки ОДИН материальный слот, поэтому обе стороны
        // делят материал и отделить изнанку штатно нельзя. Добавляем за зеркальной
        // плоскостью отдельный непрозрачный диск — он же несёт надпись. Это не выдумка:
        // у настоящих сигнальных зеркал инструкция напечатана именно на обороте.
        _backPlate     = Config.Bind("Model", "BackPlate", true,
            "Непрозрачная задняя крышка с надписью SIGNAL MIRROR. Без неё изнанка зеркала тоже отражает, потому что у меша один материальный слот.");
        _backPlateGap  = Config.Bind("Model", "BackPlateGap", 0.006f,
            "Зазор между зеркальной плоскостью и задней крышкой (метры). Мало — крышка проступает сквозь зеркало, много — виден торец.");
        _backPlateScale = Config.Bind("Model", "BackPlateScale", 0.92f,
            "Размер крышки относительно диска. Меньше 1, чтобы по краю оставался ободок самого зеркала.");
        // Автодамп: клавишу нажать может быть некому (например, игру запустил не человек).
        // Больше нуля — через столько секунд после старта список выведется сам, один раз.
        // Приписка к идентификатору Photon. Нужна, чтобы два запуска игры под одним
        // аккаунтом Steam могли сидеть в одной комнате: иначе Photon отбивает второй
        // вход («UserId already joined»). Ставится ТОМУ клиенту, у которого есть мод,
        // потому что второй в проверке «что видит игрок без мода» обязан быть чистым.
        // Клиент-«болванчик»: сам светит в ближайшего, за ним никто не сидит.
        // Нужен, чтобы проверить вид со стороны игрока БЕЗ мода: садишься за
        // ванильный клиент, подходишь — и тебя накрывает. Бот для этого не годится,
        // наш код исполняет только персонаж игрока.
        _autoBlind = Config.Bind("Debug", "AutoBlindNearest", false,
            "Непрерывно ослеплять ближайшего, как будто держишь луч. Для клиента, за которым никто не сидит: он работает мишенью-обидчиком, пока ты играешь на другом.");
        _autoBlindRange = Config.Bind("Debug", "AutoBlindRange", 30f,
            "Дальность автоослепления в метрах.");
        _userIdSuffix = Config.Bind("Debug", "PhotonUserIdSuffix", "",
            "Приписка к идентификатору Photon, например -sm2. Нужна только когда на одной машине запущены два клиента под одним аккаунтом Steam. Пусто — обычная игра, ничего не меняется.");
        UserIdSuffixPatch.Suffix = _userIdSuffix.Value ?? "";
        if (UserIdSuffixPatch.Suffix.Length > 0)
            Log.LogWarning($"Идентификатор Photon получит приписку '{UserIdSuffixPatch.Suffix}' — " +
                           "это режим двух клиентов на одной машине.");
        _prefabDumpAtStart = Config.Bind("Debug", "PrefabDumpSecondsAfterStart", 0f,
            "Через сколько секунд после запуска вывести список префабов самому, без клавиши G. 0 — не выводить.");
        // Взводим ЗДЕСЬ, а не выше: настройка существует только после Config.Bind,
        // и обращение к .Value раньше роняло Awake исключением — мод при этом
        // молча оставался полумёртвым, а все настройки ниже не привязывались.
        if (_prefabDumpAtStart.Value > 0f)
            _prefabDumpAt = Time.realtimeSinceStartup + _prefabDumpAtStart.Value;
        _prefabFilter  = Config.Bind("Debug", "PrefabDumpFilter", "vfx",
            "Подстрока для списка префабов по клавише G. Пусто — вывести все (их тысячи). Несколько подстрок через '|'.");
        _ownIcon       = Config.Bind("Model", "ReplaceInventoryIcon", true,
            "Подменять иконку предмета в инвентаре: иначе зеркало показывается в слоте как обычная тарелочка.");
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

        try { _harmony = new Harmony(Id); _harmony.PatchAll(); Log.LogInfo("Harmony: патч идентификатора Photon установлен."); }
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
        MarkSelfModded();
        // Автодамп префабов — ДО всех проверок. Раньше он стоял в обработчике клавиш,
        // то есть за «if (kb == null) return» и за выключателем отладки: в главном
        // меню клавиатура может быть ещё не готова, и дамп не наступал никогда.
        if (_prefabDumpAt > 0f && Time.realtimeSinceStartup >= _prefabDumpAt)
        {
            _prefabDumpAt = -1f;
            Log.LogWarning("[AUTO] Вывожу список префабов (Debug/PrefabDumpSecondsAfterStart).");
            try { DumpPrefabs(); }
            catch (System.Exception e) { Log.LogWarning($"[AUTO] Дамп упал: {e.GetType().Name}: {e.Message}"); }
        }

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
        if (kb[Key.N].wasPressedThisFrame) { ScanShinyMaterials(); DumpMouths(); DumpSfx(); _emotesLogged = false; LogEmotesOnce(); ProbeNetwork(); }
        // (LogGuidebookOnce убран: он сканировал сцену КАЖДЫЙ кадр и никогда не находил
        //  руководство после перехода на тарелочку — это и были лаги.)
        if (kb[Key.U].wasPressedThisFrame) EmergencyUnstick();   // застрял (старый баг) — отлипнуть
        if (kb[Key.P].wasPressedThisFrame) DebugBoardFlight();  // сразу вылететь на остров (отладка)
        if (kb[Key.M].wasPressedThisFrame) DebugNoon();
        if (kb[Key.G].wasPressedThisFrame) DumpPrefabs();
        if (kb[Key.V].wasPressedThisFrame) DebugCycleGlow();
        if (kb[Key.F2].wasPressedThisFrame)
        {
            _selfBlindAt = Time.time + Mathf.Max(0f, _selfBlindDelay.Value);
            Log.LogWarning($"[F2] Ослеплю тебя через {_selfBlindDelay.Value:0.0} с — лезь на скалу.");
        }
        if (kb[Key.Period].wasPressedThisFrame) AuditionSfx(true);    // следующий звук-кандидат
        if (kb[Key.Comma].wasPressedThisFrame)  AuditionSfx(false);   // повторить текущий
        if (kb[Key.F11].wasPressedThisFrame) DebugSpawnBot();  // бот рядом — цель для теста сети
        if (kb[Key.F12].wasPressedThisFrame) DebugKillVictim();  // убить ослеплённого — проверка уборки
        if (kb[Key.F6].wasPressedThisFrame) DebugJoinTestRoom(); // свести два клиента в одну комнату Photon
        if (kb[Key.F5].wasPressedThisFrame) DebugLobbyLink();   // достать ссылку-приглашение в лобби
        if (kb[Key.F7].wasPressedThisFrame) DebugRoomHandshake(); // рукопожатие двух клиентов на одной машине
        if (kb[Key.F4].wasPressedThisFrame) SetupTestBench();   // собрать стенд одной клавишей
        if (kb[Key.F3].wasPressedThisFrame) DebugBlindNearest(); // ослепить ближайшего без прицеливания
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
        // Заморозка полудня. Нужна не для игры, а для работы: пока настраиваешь
        // сцену или пишешь ролик, игровое время уходит, солнце садится, и зайчик
        // перестаёт работать на середине дубля. Это съело у меня несколько записей.
        if (_freezeNoon.Value)
        {
            var dnf = DayNightManager.instance;
            if (dnf != null) dnf.timeOfDay = 12f;
        }

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

        TickSelfBlindTimer(me);
        TickAutoBlind(me);
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
        if (_discMesh != null) EnsureBackPlate(_discMesh);
        ReplaceInventoryIcon();
        // БАГ 0.1.3: _holdingMirror = (найден объект "Frisbee" в сцене). Тарелочка,
        // лежащая на земле или в руках у соседа, — тоже такой объект, поэтому зайчик
        // светил с пустыми руками. Спрашиваем у игры, что держит ИМЕННО наш персонаж.
        _holdingMirror = IsMirrorInHands();
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
        if (_mirrorCam == null || _discMesh == null) return;

        // БРОШЕННОЕ ЗЕРКАЛО. Камера снимает только пока предмет в руках, поэтому у
        // лежащей на песке тарелочки в зеркальный материал никто не рисует — и диск
        // выглядел чёрной дырой. Подменяем текстуру на статичный металл: материал
        // у всех дисков общий, так что это чинит и те, что лежат рядом.
        if (!_holdingMirror)
        {
            if (_mirrorMat != null && _mirrorMat.HasProperty("_MainTex")
                && _mirrorMat.GetTexture("_MainTex") != IdleTex())
            {
                _mirrorMat.SetTexture("_MainTex", IdleTex());
                if (_mirrorMat.HasProperty("_TextureSample0")) _mirrorMat.SetTexture("_TextureSample0", IdleTex());
                Log.LogInfo("Зеркало не в руках — показываем статичный металл вместо пустой текстуры отражения.");
            }
            return;
        }
        if (_mirrorMat != null && _mirrorRT != null && _mirrorMat.HasProperty("_MainTex")
            && _mirrorMat.GetTexture("_MainTex") != _mirrorRT)
        {
            _mirrorMat.SetTexture("_MainTex", _mirrorRT);
            if (_mirrorMat.HasProperty("_TextureSample0")) _mirrorMat.SetTexture("_TextureSample0", _mirrorRT);
        }
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

    /// <summary>Статичный «металл» для брошенного зеркала: мягкий вертикальный градиент.</summary>
    private static Texture2D IdleTex()
    {
        if (_idleTex != null) return _idleTex;
        const int N = 64;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = "SunMirrorIdle", wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < N; y++)
        {
            // сверху светлее, снизу темнее — так читается наклонная полированная поверхность
            float k = 0.55f + 0.45f * (y / (float)(N - 1));
            var c = new Color(k * 0.93f, k * 0.96f, k, 1f);
            for (int x = 0; x < N; x++) t.SetPixel(x, y, c);
        }
        t.Apply();
        _idleTex = t;
        return t;
    }

    /// <summary>
    /// Текстура задней крышки: тёмный диск с надписью SIGNAL MIRROR.
    /// Буквы рисуем точками по сетке 5x7 — шрифта в рантайме у нас нет, а тащить
    /// файл в пакет ради одной надписи не хочется.
    /// </summary>
    private static Texture2D BackTex()
    {
        if (_backTex != null) return _backTex;
        const int W = 256, H = 256;
        var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = "SunMirrorBack", wrapMode = TextureWrapMode.Clamp };
        var body = new Color(0.16f, 0.17f, 0.19f, 1f);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                t.SetPixel(x, y, body);

        DrawText(t, "SIGNAL", 2, 150, new Color(0.88f, 0.88f, 0.9f, 1f));
        DrawText(t, "MIRROR", 2, 110, new Color(0.88f, 0.88f, 0.9f, 1f));
        t.Apply();
        _backTex = t;
        return t;
    }

    // Простейший точечный шрифт 5x7 на заглавные буквы, которые нам нужны.
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['S'] = new[] { "01111", "10000", "10000", "01110", "00001", "00001", "11110" },
        ['I'] = new[] { "11111", "00100", "00100", "00100", "00100", "00100", "11111" },
        ['G'] = new[] { "01110", "10001", "10000", "10111", "10001", "10001", "01110" },
        ['N'] = new[] { "10001", "11001", "11001", "10101", "10011", "10011", "10001" },
        ['A'] = new[] { "01110", "10001", "10001", "11111", "10001", "10001", "10001" },
        ['L'] = new[] { "10000", "10000", "10000", "10000", "10000", "10000", "11111" },
        ['M'] = new[] { "10001", "11011", "10101", "10101", "10001", "10001", "10001" },
        ['R'] = new[] { "11110", "10001", "10001", "11110", "10100", "10010", "10001" },
        ['O'] = new[] { "01110", "10001", "10001", "10001", "10001", "10001", "01110" },
        [' '] = new[] { "00000", "00000", "00000", "00000", "00000", "00000", "00000" },
    };

    private static void DrawText(Texture2D t, string text, int scale, int baseY, Color col)
    {
        int totalW = text.Length * 6 * scale * 5;
        int x0 = (t.width - totalW) / 2;
        int px = x0;
        foreach (char ch in text)
        {
            if (!Glyphs.TryGetValue(ch, out var g)) { px += 6 * scale * 5; continue; }
            for (int row = 0; row < 7; row++)
                for (int col2 = 0; col2 < 5; col2++)
                {
                    if (g[row][col2] != '1') continue;
                    // ряд 0 — верх глифа, поэтому по Y идём вниз от baseY
                    for (int dy = 0; dy < scale * 5; dy++)
                        for (int dx = 0; dx < scale * 5; dx++)
                        {
                            int X = px + col2 * scale * 5 + dx;
                            int Y = baseY + (6 - row) * scale * 5 + dy;
                            if (X >= 0 && X < t.width && Y >= 0 && Y < t.height) t.SetPixel(X, Y, col);
                        }
                }
            px += 6 * scale * 5;
        }
    }

    /// <summary>
    /// Задняя крышка зеркала. Нужна потому, что у меша тарелочки один материальный
    /// слот: зеркальный материал ложится на обе стороны сразу, и изнанка тоже
    /// отражает. Кладём вплотную за зеркальной плоскостью отдельный непрозрачный
    /// диск; он же несёт надпись. Слой MirrorLayer — чтобы крышка не попадала
    /// в собственное отражение.
    /// </summary>
    private void EnsureBackPlate(Transform disc)
    {
        if (!_backPlate.Value)
        {
            if (_backPlateTf != null) { Destroy(_backPlateTf.gameObject); _backPlateTf = null; }
            return;
        }
        if (_backPlateTf != null && _backPlateTf.parent == disc) return;
        if (_backPlateTf != null) Destroy(_backPlateTf.gameObject);

        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "SunMirrorBackPlate";
        var col = go.GetComponent<Collider>(); if (col != null) Destroy(col);
        go.layer = MirrorLayer;

        var mr = go.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
        var m = new Material(sh) { name = "SunMirrorBackMat" };
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", BackTex());
        m.mainTexture = BackTex();
        mr.material = m;

        go.transform.SetParent(disc, false);

        // Размер берём от РЕАЛЬНЫХ габаритов диска, а не множителем от родителя.
        // Примитив Quad в Unity — это метр на метр; множитель 0.92 от единичного
        // трансформа дал бы крышку почти метровой при диске в треть метра.
        float diaWorld = 0.3f;
        var dr = disc.GetComponent<Renderer>() ?? disc.GetComponentInChildren<Renderer>();
        if (dr != null) { var b = dr.bounds.size; diaWorld = Mathf.Max(b.x, Mathf.Max(b.y, b.z)); }

        Vector3 ls = disc.lossyScale;
        float sx = Mathf.Abs(ls.x) > 1e-5f ? Mathf.Abs(ls.x) : 1f;
        float sy = Mathf.Abs(ls.y) > 1e-5f ? Mathf.Abs(ls.y) : 1f;
        float k = Mathf.Max(0.1f, _backPlateScale.Value);
        go.transform.localScale = new Vector3(diaWorld * k / sx, diaWorld * k / sy, 1f);

        // Крышка смотрит в обратную сторону и стоит чуть позади зеркальной плоскости.
        go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        float gapLocal = Mathf.Abs(_backPlateGap.Value) / (Mathf.Abs(ls.z) > 1e-5f ? Mathf.Abs(ls.z) : 1f);
        go.transform.localPosition = new Vector3(0f, 0f, -gapLocal);
        _backPlateTf = go.transform;

        Log.LogInfo($"Задняя крышка: диск '{disc.name}' габарит {diaWorld:0.000} м, " +
                    $"lossyScale ({ls.x:0.000}, {ls.y:0.000}, {ls.z:0.000}), " +
                    $"локальный масштаб крышки ({go.transform.localScale.x:0.000}, {go.transform.localScale.y:0.000}), " +
                    $"слой {go.layer}, шейдер '{m.shader.name}'.");
    }

    /// <summary>
    /// Иконка предмета в инвентаре. Без подмены зеркало лежит в слоте как обычная
    /// тарелочка — предмет один и тот же, иконку игра берёт из Item.UIData.icon.
    /// Подменяем один раз за запуск и только у тарелочки.
    /// </summary>
    private void ReplaceInventoryIcon()
    {
        if (!_ownIcon.Value || _iconReplaced) return;
        var me = Character.localCharacter;
        var item = me?.data?.currentItem;
        if (item == null || item.UIData == null) return;

        string have = item.UIData.itemName ?? item.name;
        if (have.IndexOf(_mirrorItemName.Value, System.StringComparison.OrdinalIgnoreCase) < 0) return;

        item.UIData.icon = IconTex();
        item.UIData.hasAltIcon = false;
        _iconReplaced = true;
        Log.LogWarning($"Иконка предмета '{have}' подменена на зеркальную. " +
                       "Правка живёт до выхода из игры: файлы игры не трогаем.");
    }

    /// <summary>Иконка: диск с бликом по диагонали. Рисуем кодом, файлов в пакете нет.</summary>
    private static Texture2D IconTex()
    {
        if (_iconTex != null) return _iconTex;
        const int N = 128;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = "SunMirrorIcon", wrapMode = TextureWrapMode.Clamp };
        float c0 = (N - 1) * 0.5f, rad = N * 0.46f;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = x - c0, dy = y - c0;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d > rad) { t.SetPixel(x, y, new Color(0, 0, 0, 0)); continue; }

                // корпус: тёмный ободок и светлая зеркальная середина
                float edge = Mathf.SmoothStep(1f, 0f, (d - rad * 0.82f) / (rad * 0.18f));
                float k = 0.55f + 0.35f * (y / (float)(N - 1));
                var c = Color.Lerp(new Color(0.18f, 0.19f, 0.21f), new Color(k * 0.95f, k * 0.97f, k), edge);

                // блик: светлая полоса по диагонали, из-за неё диск читается как зеркало
                float band = Mathf.Abs((dx + dy) * 0.7071f);
                if (band < rad * 0.16f && edge > 0.5f)
                    c = Color.Lerp(c, Color.white, 0.85f * (1f - band / (rad * 0.16f)));

                t.SetPixel(x, y, c);
            }
        t.Apply();
        _iconTex = t;
        return t;
    }

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

    /// <summary>
    /// Единственная пульсация яркости во всём моде. Раньше таких синусоид было три,
    /// с одной и той же частотой 12 рад/с (период 0.52 с) и разной амплитудой, и жили
    /// они в разных местах: свечение материала, масштаб блика и свет у вспышки. Убавили
    /// первую — мерцание осталось, потому что две других продолжали раскачивать
    /// автоэкспозицию. Теперь формула одна, и амплитуда у неё одна — FlashPulseAmount.
    /// При 0 функция возвращает ровную единицу, то есть пульсации нет вообще нигде.
    /// </summary>
    private float FlashPulse(float speedBonus = 0f)
    {
        float amp = Mathf.Clamp01(_flashPulse.Value);
        if (amp <= 0f) return 1f;
        return (1f - amp) + amp * Mathf.Sin(Time.time * (_flashPulseSpeed.Value + speedBonus));
    }

    /// <summary>Свечение материала и блик — включены на этой стадии отладки?</summary>
    private bool GlowEmissionOn => _glowStage == 0 || _glowStage == 1;
    /// <summary>Точечные светильники (прожектор зайчика, свет вспышки) — включены?</summary>
    private bool GlowLightsOn => _glowStage == 0 || _glowStage == 2;

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
            if (bb != null) bb.allowed = on && GlowEmissionOn;
            if (on)
            {
                // Блик пульсирует вместе со свечением и той же малой амплитудой:
                // раньше его собственные 25% добавляли к раскачке экспозиции.
                float pulseG = FlashPulse(2f);
                _glintTf.localScale = _glintBaseScale * (_glintFlashMul.Value * pulseG);
            }
        }

        if (_heldMats == null) return;
        float pulse = FlashPulse();
        bool glow = on && GlowEmissionOn;
        Color e = glow ? new Color(1f, 0.95f, 0.8f) * (_flashEmission.Value * pulse) : Color.black;
        foreach (var m in _heldMats)
        {
            if (m == null || !m.HasProperty("_EmissionColor")) continue;
            if (glow) m.EnableKeyword("_EMISSION");
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
        // Здесь и сидел остаток мерцания. Эта синусоида — 0.7..1.0 на частоте 12 рад/с,
        // то есть размах 30% с периодом ровно 0.52 с, замеренным на записи. Она качала
        // и размер вспышки, и силу света под ней, прямо в полуметре от камеры.
        float pulse = FlashPulse();
        // Растём с расстоянием до смотрящего: зеркало должно быть заметно и за сто метров,
        // иначе на таком удалении вспышка превращается в один пиксель.
        float dist = Vector3.Distance(pos, lookFrom);
        float size = Mathf.Clamp(0.6f + dist * _signalGrow.Value, 0.6f, _signalMax.Value);
        _signal.transform.localScale = Vector3.one * (size * pulse);
        if (_signalLight != null)
        {
            // Яркость света держим ровной. Пульсировать ей нельзя: автоэкспозиция
            // замеряет светильники сильнее всего, а этот висит в полуметре от камеры.
            // Размер самой картинки по-прежнему дышит — это видно, но не замеряется.
            _signalLight.enabled = GlowLightsOn;
            _signalLight.intensity = _signalLightBright.Value;
        }
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

    /// <summary>
    /// Измерение мерцания, а не лечение: по очереди гасит источники яркости, чтобы
    /// увидеть, который из них раскачивает автоэкспозицию. Четыре нажатия — круг.
    /// Если картинка перестаёт «дышать» на какой-то стадии, виноват выключенный на ней.
    /// Стадию нарочно держим в поле, а не в конфиге: тумблер, переживший перезапуск,
    /// уже однажды стоил нам дня отладки.
    /// </summary>
    private void DebugCycleGlow()
    {
        _glowStage = (_glowStage + 1) % 4;
        string[] names = { "всё включено", "без точечных светов", "без свечения и блика", "ни света, ни свечения" };
        Log.LogWarning($"[МЕРЦАНИЕ] стадия {_glowStage}: {names[_glowStage]}");
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

    private bool _emotePlayed;
    private void ClearTarget() { _victim = null; _emotePlayed = false; }

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
                ? VictimFromHit(rhit, null)          // в отражении себя слепить можно
                : null;
        }
        else
        {
            victim = VictimFromHit(hit, me);
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

        if (victim != _victim) { _victim = victim; _emotePlayed = false; }

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

        if (!_useLight.Value || !GlowLightsOn)
        {
            if (_spotLightGo != null && _spotLightGo.activeSelf) _spotLightGo.SetActive(false);
            _spotLitSmooth = 0f;
        }
        else
        {
        float h = Mathf.Max(0.15f, size * _heightFactor.Value);
        _spotLightGo!.transform.position = point + normal * h;
        _spotLightGo.transform.rotation = Quaternion.LookRotation(-normal);
        _spotLight!.spotAngle = 2f * Mathf.Atan2(size * 0.5f, h) * Mathf.Rad2Deg;
        _spotLight.range = h + Mathf.Max(0.05f, _lightReach.Value);
        // Яркость догоняем плавно. size пересчитывается каждый кадр от дистанции и дрожи
        // прицела, так что сырое значение дёргается — а за ним дёргалась и экспозиция.
        float wantI = _spotGlow.Value * (1f + size * 0.25f);   // дальше/крупнее → ярче
        _spotLitSmooth = _spotLitSmooth > 0f
            ? Mathf.Lerp(_spotLitSmooth, wantI, 1f - Mathf.Exp(-8f * Time.deltaTime))
            : wantI;
        _spotLight.intensity = _spotLitSmooth;
        if (!_spotLightGo.activeSelf) _spotLightGo.SetActive(true);
        }

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
        _spotLitSmooth = 0f;
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

            // Контрастный ободок. Аддитивное пятно на залитом солнцем песке не читается:
            // складывать уже некуда, песок и так почти белый. Яркость тут не помогает —
            // она лишь глубже уводит в пересвет. Поэтому рядом с ядром кладём слой,
            // который не складывает, а УМНОЖАЕТ, то есть затемняет фон кольцом вокруг
            // пятна. Тёмный ободок даёт краю контраст, и зайчик виден на любой поверхности.
            if (_ringStrength.Value > 0.01f)
            {
                var ring = GameObject.CreatePrimitive(PrimitiveType.Quad);
                ring.name = "SunSpotRing";
                var rc = ring.GetComponent<Collider>(); if (rc != null) Destroy(rc);
                var rmr = ring.GetComponent<MeshRenderer>();
                rmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                rmr.receiveShadows = false;
                rmr.material = MakeMultiplyRingMaterial();
                ring.transform.SetParent(_spotQuad.transform, false);
                ring.transform.localScale = new Vector3(1.7f, 1.7f, 1f);
                // чуть ближе к наблюдателю, иначе слои дерутся за один пиксель
                ring.transform.localPosition = new Vector3(0f, 0f, -0.001f);
            }

            _spotQuad.SetActive(false);
            DontDestroyOnLoad(_spotQuad);
        }
    }

    /// <summary>Кольцо: белое (не меняет фон) везде, кроме тёмной полосы вокруг ядра.</summary>
    private static Texture2D? _ringTex;
    private Texture2D RingTex()
    {
        if (_ringTex != null) return _ringTex;
        int s = 128;
        var t = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[s * s];
        float k = Mathf.Clamp01(_ringStrength.Value);
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float dx = (x - s / 2f) / (s / 2f), dy = (y - s / 2f) / (s / 2f);
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            // Полоса затемнения живёт между ядром (оно занимает ~0.55 этого квада)
            // и краем. Колокол: 0 на границах полосы, максимум посередине.
            float u = Mathf.InverseLerp(0.55f, 0.92f, d);
            float bell = (u <= 0f || u >= 1f) ? 0f : Mathf.Sin(u * Mathf.PI);
            float v = 1f - k * bell;            // 1 = фон не трогаем, меньше = темнее
            px[y * s + x] = new Color(v, v, v, 1f);
        }
        t.SetPixels(px); t.Apply(false, false);
        t.filterMode = FilterMode.Bilinear;
        _ringTex = t; return t;
    }

    private Material MakeMultiplyRingMaterial()
    {
        var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        var m = new Material(sh) { name = "SunSpotRingMat" };
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_ZWrite", 0f);
        // Умножение: результат = цвет_кадра * цвет_текстуры.
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.DstColor);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
        m.SetFloat("_AlphaClip", 0f);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = 3099;               // строго раньше ядра (3100)
        m.SetTexture("_BaseMap", RingTex());
        m.mainTexture = RingTex();
        m.SetColor("_BaseColor", Color.white);
        return m;
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

        // Молчаливый откат на само введённое имя — ловушка: если в настройке стоит
        // локализованная подпись (а она зависит от языка игры), сопоставление не
        // срабатывает, в аниматор уходит текст вроде «сколько можно», состояния с
        // таким именем нет, и поза просто не проигрывается. Со стороны выглядит
        // как «мод не работает», а в логе до этого не было ни слова.
        if (data == null && !anim.StartsWith("A_Scout_Emote_"))
            Log.LogWarning($"Эмоут '{wantedName}' не найден. В Emote/EmoteName нужен английский ключ " +
                           $"(Defeat, Nono, Wave — список печатает клавиша N), а не локализованная подпись. " +
                           $"Позы не будет.");

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
        public float nextWobbleAt;       // когда следующий толчок качания
        public float wobbleSide = 1f;    // в какую сторону толкаем — чередуется
        public float nextEmoteAt;        // когда менять эмоут
        public bool emoteOnB;            // сейчас проигрывается второй эмоут
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
    /// <summary>
    /// Качание ослеплённого. Главное про комедию в PEAK: смешное здесь — это потеря
    /// равновесия, а не поза. Замершая намертво фигура со стороны читается как
    /// зависший персонаж, а не как человек, которому бьют в глаза. Поэтому пока
    /// держим ослепление, раз в несколько десятых секунды толкаем корпус то в одну
    /// сторону, то в другую — жертва топчется и шатается, прикрывая лицо.
    ///
    /// Толчки идут через AddForceToBodyPart, а он рассылает RPC всем. Значит, качание
    /// видят и те, у кого мода нет: для них это единственная заметная часть эффекта.
    /// Сила маленькая нарочно — задача пошатнуть, а не повалить.
    /// </summary>
    private void Wobble(Character victim, BlindState st)
    {
        if (_wobbleForce.Value <= 0.01f || victim == null) return;
        if (Time.time < st.nextWobbleAt) return;
        st.nextWobbleAt = Time.time + Mathf.Max(0.1f, _wobbleEvery.Value);
        st.wobbleSide = -st.wobbleSide;

        try
        {
            var rig = victim.GetBodypartRig(BodypartType.Torso);
            if (rig == null) rig = victim.GetBodypartRig(BodypartType.Head);
            if (rig == null) return;

            // Вбок относительно того, кто светит: так шатание читается как реакция
            // на источник света, а не как случайный тычок.
            var me = Character.localCharacter;
            Vector3 toMe = me != null ? (me.Center - victim.Center) : Vector3.forward;
            toMe.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, toMe.sqrMagnitude > 1e-4f ? toMe.normalized : Vector3.forward);
            Vector3 push = (side * st.wobbleSide + Vector3.up * 0.25f).normalized * _wobbleForce.Value;

            victim.AddForceToBodyPart(rig, push, Vector3.zero);
        }
        catch (System.Exception e) { Log.LogWarning($"Качание: {e.Message}"); }
    }

    /// <summary>
    /// «Отпустило»: короткий встряс в момент, когда луч ушёл. Нужен, чтобы конец
    /// эффекта тоже читался — иначе жертва просто мгновенно возвращается в строй,
    /// и со стороны непонятно, что вообще закончилось.
    /// </summary>
    private void ShakeOff(Character victim)
    {
        if (_shakeOffForce.Value <= 0.01f || victim == null) return;
        try
        {
            var rig = victim.GetBodypartRig(BodypartType.Head);
            if (rig == null) return;
            Vector3 dir = (Vector3.up * 0.7f + UnityEngine.Random.insideUnitSphere * 0.5f).normalized;
            victim.AddForceToBodyPart(rig, dir * _shakeOffForce.Value, Vector3.zero);
        }
        catch (System.Exception e) { Log.LogWarning($"Встряс: {e.Message}"); }
    }

    private float _lastEyesLog;

    /// <summary>
    /// Кого считать ослеплённым по этому попаданию. Зайчик слепит только тогда,
    /// когда луч попал в глаза: пятно по телу всё равно ползёт и его видно со стороны,
    /// но ослепление начинается, лишь когда центр луча подошёл к голове.
    /// Проверяем геометрией от головного рига, а не типом коллайдера: пока персонаж
    /// жив, тело в PEAK — одна капсула, типа части тела у попадания просто нет.
    /// Дистанцию всегда пишем в лог: радиус подкручивается по этим цифрам, а не на ощупь.
    /// </summary>
    private Character? VictimFromHit(RaycastHit hit, Character? exclude)
    {
        var c = hit.collider.GetComponentInParent<Character>();
        if (c == null || (exclude != null && c == exclude)) return null;
        if (!_eyesOnly.Value) return c;

        Rigidbody? head = null;
        Vector3 eyes;
        try
        {
            head = c.GetBodypartRig(BodypartType.Head);
            // Точка глаз у игры своя: кость головы смещена, камера игрока сидит
            // в GetCameraPos(0). Мерим до неё, иначе порог поедет на пол-головы.
            eyes = c.GetCameraPos(0f);
        }
        catch (System.Exception e)
        {
            // Головы не достать — молча отбирать эффект нельзя, иначе мод «просто не работает».
            Log.LogWarning($"Голова '{c.name}' недоступна ({e.Message}), слепим без проверки глаз.");
            return c;
        }

        // Попали прямо в коллайдер головного рига — это глаза, геометрия не нужна.
        if (head != null && hit.collider.attachedRigidbody == head)
        {
            if (Time.time - _lastEyesLog > 1f)
            {
                _lastEyesLog = Time.time;
                Log.LogInfo($"Попадание по '{c.name}': коллайдер головы → в глаза, слепим.");
            }
            return c;
        }

        float d = Vector3.Distance(hit.point, eyes);
        bool inEyes = d <= _eyesRadius.Value;
        if (Time.time - _lastEyesLog > 1f)
        {
            _lastEyesLog = Time.time;
            Log.LogInfo($"Попадание по '{c.name}': {d:0.00} м от глаз, порог {_eyesRadius.Value:0.00} → " +
                        (inEyes ? "в глаза, слепим." : "в тело, не слепим."));
        }
        return inEyes ? c : null;
    }

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
            // Голову дёргаем заметно, всё тело — слабо: со стороны это читается как
            // «отшатнулся», а не «улетел». Оба импульса уходят одним RPC на всех,
            // поэтому отшатывание видит и игрок без мода.
            victim.AddForceToBodyPart(rig,
                                      dir.normalized * _flinchForce.Value,
                                      dir.normalized * _staggerForce.Value);
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
        try
        {
            // Свой RPC — только тем, у кого мод есть: у остальных метода нет, и Photon
            // писал бы им в лог ошибку на каждую посылку. Игроки без мода от этого
            // ничего не теряют: всё, что им видно, рассылается родными RPC игры из
            // BeginBlind. Себя не исключаем никогда — именно этот вызов у стрелка
            // и запускает BeginBlind, а с ним и родные RPC для всех.
            int sent = 0;
            foreach (var p in PhotonNetwork.PlayerList)
            {
                if (!p.IsLocal && !p.CustomProperties.ContainsKey(ModPlayerProp)) continue;
                view.RPC(SunMirrorNet.RpcName, p, seconds, shooter);
                sent++;
            }
            if (sent == 0) StartOrRefreshBlind(victim, seconds);   // офлайн/без комнаты
        }
        catch (System.Exception e)
        {
            Log.LogWarning($"RPC ослепления не ушёл ({e.Message}) — применяю локально.");
            StartOrRefreshBlind(victim, seconds);
        }
    }

    /// <summary>Свойство игрока Photon, по которому другие клиенты узнают, что мод у нас есть.</summary>
    private const string ModPlayerProp = "SunMirror";
    private string? _markedRoom;

    /// <summary>
    /// Отмечаемся в каждой комнате, куда входим: стрелки с модом шлют свой RPC
    /// только отмеченным. Раз на комнату — SetCustomProperties это сетевая операция.
    /// </summary>
    private void MarkSelfModded()
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode) { _markedRoom = null; return; }
        string room = PhotonNetwork.CurrentRoom.Name;
        if (_markedRoom == room) return;
        _markedRoom = room;
        PhotonNetwork.LocalPlayer.SetCustomProperties(
            new ExitGames.Client.Photon.Hashtable { { ModPlayerProp, 1 } });
        Log.LogInfo($"[NET] Отметился в комнате '{room}' как игрок с модом.");
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
            if (TryFireCap(victim, key)) return;
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

    /// <summary>
    /// Сколько ослепления накоплено по цели. Пока луч в глазах — растёт, после ухода
    /// луча стекает со скоростью BlindChargeDrain. Это и есть «фитиль» до падения.
    /// </summary>
    private float ChargeOf(int key)
    {
        if (!_blindCharge.TryGetValue(key, out var c)) return 0f;
        float idle = Mathf.Max(0f, Time.time - c.at);
        return Mathf.Max(0f, c.charge - idle * _chargeDrain.Value);
    }

    private void AddCharge(int key, float dt)
    {
        float cur = 0f;
        if (_blindCharge.TryGetValue(key, out var c))
        {
            // Сливаем только то время, что цель была БЕЗ луча: текущий кадр не в счёт.
            // Раньше здесь звался ChargeOf, который вычитает слив за время с прошлого
            // обновления, а прошлое обновление — это предыдущий кадр. Выходило:
            // прибавили dt и тут же отняли dt*BlindChargeDrain. Фитиль горел вдвое
            // медленнее реального времени, и потолок в 5 с набирался за 10 с.
            float idle = Mathf.Max(0f, Time.time - c.at - dt);
            cur = Mathf.Max(0f, c.charge - idle * _chargeDrain.Value);
        }
        _blindCharge[key] = (cur + dt, Time.time);
    }

    /// <summary>
    /// Ронять жертву. Character.Fall шлёт RPCA_Fall через PhotonView, а у персонажа
    /// без регистрации в сети (бот со стенда, ViewID = 0) RPC уходит в пустоту:
    /// исключения нет, в лог писалось «Падение...», а персонаж стоял. Поэтому
    /// для таких целей вызываем тот же RPCA_Fall напрямую — он всего лишь
    /// выставляет data.fallSeconds, остальное делает штатный FixedUpdate игры.
    /// </summary>
    /// <summary>
    /// Отложенное ослепление самого себя (F2). Держим дольше потолка, чтобы «фитиль»
    /// успел догореть и сработало падение — иначе главное в этой проверке
    /// (что делает падение с тем, кто висит на стене) просто не наступит.
    /// </summary>
    private void TickSelfBlindTimer(Character? me)
    {
        if (_selfBlindAt < 0f || Time.time < _selfBlindAt) return;
        _selfBlindAt = -1f;
        if (me == null) { Log.LogWarning("[F2] Персонажа нет — ослеплять некого."); return; }

        bool climbing = me.data != null && me.data.isClimbing;
        Log.LogWarning($"[F2] Ослепляю тебя сейчас. Ты {(climbing ? "НА СТЕНЕ" : "на земле")}.");
        if (_useEmote.Value && !string.IsNullOrEmpty(_emoteName.Value))
            PlayVictimEmote(me, _emoteName.Value);
        SendBlind(me, Mathf.Max(_blindHoldSeconds.Value, _maxBlind.Value + 1.5f));
    }

    /// <summary>
    /// Прибить жертву на месте родным механизмом игры.
    ///
    /// Наше обездвиживание (movementModifier = 0) живёт только на клиенте жертвы
    /// и до игрока без мода не доходит. RPCA_Stick — метод самой игры, поэтому
    /// его исполнит любой клиент своим кодом, мод ему не нужен.
    ///
    /// Величина статуса — ноль: внутри стоит проверка statusAmount > 0, так что
    /// никакого урона не наносится, только физическое удержание.
    ///
    /// Карабкающихся не трогаем: ослепление на стене мы проверяли и оно безопасно,
    /// а вот как поведёт себя прибитый на скале — не проверено, и выяснять это
    /// на живых игроках неправильно.
    /// </summary>
    /// <summary>
    /// Спиральные глаза у всех, включая игроков без мода.
    ///
    /// Наш способ (подмена текстуры на рендерерах) работает только на своём клиенте.
    /// А CharacterCustomization.CharacterPassedOut — метод самой игры, помеченный
    /// PunRPC, и он меняет ТОЛЬКО внешность: ставит passedOutEyes и Spin = 1.
    /// Персонаж при этом не отключается — за это отвечает другой метод.
    ///
    /// Шлём обычным All, а не AllBuffered как игра: буферизованный RPC остался бы
    /// в комнате и догнал бы тех, кто подключится позже, — для временного эффекта
    /// это мусор.
    ///
    /// Если жертва по-настоящему отключится или умрёт, пока висит наш визуал,
    /// Update игры сам поставит правильные глаза: там стоят проверки
    /// data.passedOut && !isPassedOut и data.dead && !isDead.
    /// </summary>
    private void SpiralEyesForEveryone(Character victim, bool on)
    {
        if (!_spiralEyesNet.Value || victim == null) return;
        var view = victim.photonView;
        if (view == null || view.ViewID == 0) return;

        // На по-настоящему отключённых и мёртвых не лезем: у них свои глаза
        // от игры, и наш возврат сбил бы их.
        var d = victim.data;
        if (d != null && (d.dead || d.passedOut || d.fullyPassedOut)) return;

        try { view.RPC(on ? "CharacterPassedOut" : "OnRevive_RPC", RpcTarget.All); }
        catch (System.Exception e)
        {
            Log.LogWarning($"Спиральные глаза по сети ({(on ? "вкл" : "выкл")}): " +
                           $"{e.GetType().Name}: {e.Message}");
        }
    }

    private void RootVictim(Character victim)
    {
        if (!_rootVictim.Value || victim == null) return;
        if (victim.data != null && victim.data.isClimbing) return;

        var view = victim.photonView;
        if (view == null || view.ViewID == 0) return;

        try
        {
            var rig = victim.GetBodypartRig(BodypartType.Hip);
            if (rig == null) return;
            Vector3 pos = rig.position;
            // Ставим туда же, где часть тела и так находится: никакого рывка.
            view.RPC("RPCA_Stick", RpcTarget.All, BodypartType.Hip, pos, pos,
                     CharacterAfflictions.STATUSTYPE.Injury, 0f);
            Log.LogInfo($"Прибил '{victim.name}' на месте (RPCA_Stick, урона нет). " +
                        "Игра отпустит сама через 4 с.");
        }
        catch (System.Exception e) { Log.LogWarning($"RPCA_Stick не прошёл: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>
    /// Снять наше удержание. Чистим ТОЛЬКО свою часть тела (RPCA_ClearJoint),
    /// а не всё разом: RPCA_Unstick освободил бы человека и от ловушек игры,
    /// в которые он попал сам.
    /// </summary>
    private void UnrootVictim(Character? victim)
    {
        if (!_rootVictim.Value || victim == null) return;
        var view = victim.photonView;
        if (view == null || view.ViewID == 0) return;
        try { view.RPC("RPCA_ClearJoint", RpcTarget.All, BodypartType.Hip); }
        catch (System.Exception e) { Log.LogWarning($"RPCA_ClearJoint не прошёл: {e.Message}"); }
    }

    private void DropVictim(Character victim)
    {
        float sec = _fallSeconds.Value, shake = _fallShake.Value;
        // Главный вопрос по лезущим: отпускает ли упавший стену. Пишем состояние
        // в момент падения, чтобы судить по записи, а не по впечатлению.
        bool climbing = victim.data != null && victim.data.isClimbing;
        try
        {
            var view = victim.photonView;
            if (view != null && view.ViewID != 0)
            {
                victim.Fall(sec, shake);
                Log.LogInfo($"Падение '{victim.name}' на {sec:0.0} с (тряска {shake:0.0}), по сети. " +
                            $"Цель {(climbing ? "ВИСЕЛА НА СТЕНЕ" : "стояла на земле")}.");
            }
            else
            {
                victim.RPCA_Fall(sec, shake);
                Log.LogInfo($"Падение '{victim.name}' на {sec:0.0} с — локально: " +
                            "персонаж не зарегистрирован в сети (ViewID=0), RPC ему не доставить. " +
                            $"Цель {(climbing ? "ВИСЕЛА НА СТЕНЕ" : "стояла на земле")}.");
            }
        }
        catch (System.Exception e) { Log.LogWarning($"Падение не вышло: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>
    /// Потолок ослепления: догорел «фитиль» — снимаем эффект, ставим паузу и,
    /// если разрешено, роняем цель. Возвращает true, если сработал.
    ///
    /// Зовётся и каждый кадр из TickBlind, и при обновлении эффекта. Раньше жил
    /// только в обновлении, из-за чего падение зависело от того, как часто стрелок
    /// шлёт эффект по сети: при удержании луча обновление шло каждые 0.25 с и всё
    /// работало, а при единственном попадании потолок не проверялся вообще и
    /// падение не наступало никогда. Это была случайность реализации, а не замысел.
    ///
    /// Считаем НАКОПЛЕННОЕ ослепление, а не непрерывное: непрерывное обнулялось
    /// от каждого разрыва луча в 0.4 с, и пять секунд подряд были недостижимы.
    /// </summary>
    private bool TryFireCap(Character victim, int key)
    {
        if (_maxBlind.Value <= 0f || ChargeOf(key) < _maxBlind.Value) return false;

        _cooldownUntil[key] = Time.time + Mathf.Max(0f, _cooldown.Value);
        _blindCharge.Remove(key);     // фитиль догорел — начинаем заново
        Log.LogInfo($"Ослепление '{victim.name}' прервано по потолку {_maxBlind.Value:0.0} с; " +
                    $"повтор не раньше чем через {_cooldown.Value:0.0} с.");

        // Падение в конце ослепления. Валит в том числе тех, у кого мода нет:
        // Character.Fall шлёт RPCA_Fall на всех. Выключается FallAtMaxBlind.
        if (_fallAtMax.Value) DropVictim(victim);

        RestoreBlind(victim);
        return true;
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
        }
        // Первое переключение — не раньше, чем через EmoteFirstHoldSeconds.
        // Иначе TickBlind на первом же кадре видит nextEmoteAt == 0 и сразу
        // перекидывает на второй эмоут, ломая весь порядок реакции.
        st.nextEmoteAt = Time.time + Mathf.Max(0f, _emoteFirstHold.Value);
        st.emoteOnB = false;

        // Состояние позы переносим ВСЕГДА, даже когда эмоут не переигрывали.
        // Иначе после короткого перерыва луча (а он теперь гаснет за 0.4 с) поза
        // пропадала бы совсем на все 4 секунды защиты от переигрывания:
        // спиральные глаза есть, а руки опущены.
        st.emoteState = _emoteState;

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

        // Удар головой и звук — только на НАЧАЛО реакции, а не на каждый перезапуск
        // эффекта. Луч теперь гаснет за 0.4 с, и при поводке по лицу BeginBlind
        // срабатывал бы несколько раз в секунду: голова дёргалась бы как пулемёт,
        // а звук превращался в треск.
        int rkey = VictimKey(victim);
        bool freshReaction = !_reactionAt.TryGetValue(rkey, out float ra)
                             || Time.time - ra >= _reactionEvery.Value;
        if (freshReaction)
        {
            _reactionAt[rkey] = Time.time;
            FlinchHead(victim);
            PlayBlindSfx(victim);
            RootVictim(victim);
            SpiralEyesForEveryone(victim, true);
        }
        Log.LogInfo($"Blind begin on {victim.name} (всего целей: {_blinds.Count}" +
                    (freshReaction ? "" : ", удар и звук пропущены — реакция уже шла") + ")");
        return st;
    }

    /// <summary>
    /// Звук попадания — берём готовый из самой игры, по имени из настройки.
    /// Своих звуков в пакете нет нарочно: чужая озвучка выбивается из игры, а у PEAK
    /// все SFX лежат ScriptableObject'ами и доступны по имени. Имя подбирается на глаз
    /// по списку, который печатает клавиша N.
    ///
    /// Звук локальный: его слышат только те, у кого стоит мод. Сетевого способа
    /// проиграть произвольный звук у игры нет.
    /// </summary>
    /// <summary>
    /// Держит ли НАШ персонаж зеркало прямо сейчас. Спрашиваем у игры
    /// (Character.data.currentItem), а не ищем объект по имени в сцене:
    /// тарелочка на земле или в руках у соседа — тоже объект "Frisbee",
    /// из-за чего зайчик светил с пустыми руками.
    /// Имя берём из Item.UIData.itemName — это английский ключ, а не
    /// локализованная подпись, так что сравнение не поедет на другом языке.
    /// </summary>
    private bool IsMirrorInHands()
    {
        var me = Character.localCharacter;
        if (me == null || me.data == null) return false;
        var item = me.data.currentItem;
        if (item == null) return false;

        string want = _mirrorItemName.Value;
        if (string.IsNullOrWhiteSpace(want)) return false;

        string have = item.UIData != null ? (item.UIData.itemName ?? "") : "";
        // UIData может быть не заполнен на отдельных предметах — тогда имя объекта.
        if (have.Length == 0) have = item.name;
        bool ok = have.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) >= 0;

        if (have != _lastItemLogged)
        {
            _lastItemLogged = have;
            Log.LogInfo($"В руках: '{have}' → зеркало {(ok ? "да" : "нет")} (ищем '{want}').");
        }
        return ok;
    }

    private string _lastItemLogged = "";

    // ---- подбор звука ослепления ----
    private readonly List<string> _sfxCandidates = new();
    private int _sfxPick = -1;
    private string _sfxFilterUsed = " ";

    /// <summary>
    /// Прослушка звуков: клавиша . берёт следующий звук игры, чьё имя содержит
    /// SfxAuditionFilter, и проигрывает его. Имена не выдумываем — список строим
    /// из того, что реально загружено в игре, поэтому опечатка в имени невозможна.
    /// </summary>
    private void AuditionSfx(bool next)
    {
        string filter = _sfxFilter.Value ?? "";
        // Фильтр правится в конфиге на ходу — список пересобираем, когда он сменился,
        // иначе клавиша продолжала бы крутить кандидатов от прошлого фильтра.
        if (_sfxCandidates.Count == 0 || filter != _sfxFilterUsed || (!next && _sfxPick < 0))
        {
            _sfxFilterUsed = filter;
            _sfxCandidates.Clear();

            // Фильтр — несколько подстрок через '|'. Кандидаты идут В ПОРЯДКЕ этих
            // подстрок, а не по алфавиту: так отобранный набор слушается осмысленно,
            // от самого подходящего к самому дальнему, а не вперемешку.
            // Фильтр "synth" — крутим не звуки игры, а свои синтезированные.
            bool synthOnly = (filter ?? "").Trim().Equals("synth", System.StringComparison.OrdinalIgnoreCase);
            if (synthOnly)
                foreach (var np in SynthPresets) _sfxCandidates.Add("synth:" + np);

            var terms = (filter ?? "").Split('|');
            var all = synthOnly
                ? System.Array.Empty<SFX_Instance>()
                : Resources.FindObjectsOfTypeAll<SFX_Instance>();
            foreach (var raw in terms)
            {
                string t = raw.Trim();
                var group = new List<string>();
                foreach (var sfx in all)
                {
                    if (sfx == null || sfx.name == null) continue;
                    if (t.Length > 0 && sfx.name.IndexOf(t, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!group.Contains(sfx.name) && !_sfxCandidates.Contains(sfx.name)) group.Add(sfx.name);
                }
                group.Sort();
                _sfxCandidates.AddRange(group);
            }
            _sfxPick = -1;
            Log.LogWarning($"[звук] по фильтру '{filter}' найдено {_sfxCandidates.Count}: " +
                           string.Join(", ", _sfxCandidates));
            if (_sfxCandidates.Count == 0)
            {
                Log.LogWarning("[звук] ничего не подошло. Полный список звуков печатает клавиша N; " +
                               "фильтр менять в Emote/SfxAuditionFilter.");
                return;
            }
        }

        if (next || _sfxPick < 0) _sfxPick = (_sfxPick + 1) % _sfxCandidates.Count;
        string name = _sfxCandidates[_sfxPick];
        var inst = ResolveSfx(name);
        var me = Character.localCharacter;
        if (inst == null) { Log.LogWarning($"[звук] '{name}' пропал из памяти."); return; }
        try
        {
            inst.Play(me != null ? me.Center : Vector3.zero);
            Log.LogWarning($"[звук {_sfxPick + 1}/{_sfxCandidates.Count}] '{name}'  " +
                           "(. — следующий, , — повторить; понравившийся номер скажи мне)");
        }
        catch (System.Exception e) { Log.LogWarning($"[звук] '{name}' не проигрался: {e.Message}"); }
    }

    // ======== свой звук: синтез в рантайме ========
    // Ни один из 885 звуков игры не подошёл: все они про удар, падение или интерфейс.
    // Поэтому собираем клип сами и оборачиваем его в SFX_Instance — тот же тип,
    // которым пользуется игра. Благодаря этому звук идёт через штатный SFX_Player:
    // громкость из настроек игрока, 3D-затухание по дистанции и кулдаун — даром.
    private static readonly string[] SynthPresets =
        { "shing", "sparkle", "bell", "wobble", "zap", "hum" };

    private readonly Dictionary<string, SFX_Instance> _synthCache = new();

    private SFX_Instance? SynthSfx(string preset)
    {
        preset = preset.Trim().ToLowerInvariant();
        if (_synthCache.TryGetValue(preset, out var cached) && cached != null)
        {
            // Экземпляр кэширован, а громкость с высотой правятся в конфиге на ходу —
            // перечитываем их здесь. Иначе правка SynthVolume молча ничего не делала бы.
            cached.settings.volume = Mathf.Clamp01(_synthVolume.Value);
            cached.settings.pitch = _synthPitch.Value;
            return cached;
        }

        var clip = BuildSynthClip(preset);
        if (clip == null)
        {
            Log.LogWarning($"[звук] свой вариант '{preset}' неизвестен. Есть: {string.Join(", ", SynthPresets)}");
            return null;
        }

        var inst = ScriptableObject.CreateInstance<SFX_Instance>();
        inst.name = "synth:" + preset;
        inst.clips = new[] { clip };
        inst.settings = new SFX_Settings
        {
            volume = Mathf.Clamp01(_synthVolume.Value),
            volume_Variation = 0.1f,
            pitch = _synthPitch.Value,
            pitch_Variation = 0.08f,   // чтобы повторы не звучали как заезженная пластинка
            spatialBlend = 1f,         // 3D: слышно с той стороны, откуда светят
            dopplerLevel = 0f,
            range = 150f,
            cooldown = 0.02f
        };
        _synthCache[preset] = inst;
        return inst;
    }

    private AudioClip? BuildSynthClip(string preset)
    {
        float len = preset switch
        {
            "bell" => 0.55f,
            "wobble" => 0.45f,
            "hum" => 0.60f,
            "sparkle" => 0.40f,
            "zap" => 0.18f,
            "shing" => 0.22f,
            _ => -1f
        };
        if (len < 0f) return null;

        const int rate = 44100;
        int n = (int)(rate * len);
        var buf = new float[n];
        var rnd = new System.Random(20261023);   // фиксированное зерно: звук одинаков каждый запуск

        // искры: четыре коротких пика в разное время и на разной высоте
        float[] spOff = { 0f, 0.07f, 0.15f, 0.24f };
        float[] spHz = { 2600f, 3300f, 2000f, 3900f };

        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float u = t / len;
            float v = 0f;

            switch (preset)
            {
                case "shing":   // блик по глазам: быстрый свип вверх
                {
                    float f = 900f * Mathf.Pow(3800f / 900f, u);
                    float env = Mathf.Exp(-u * 9f);
                    v = (Mathf.Sin(2f * Mathf.PI * f * t) + 0.3f * Mathf.Sin(4f * Mathf.PI * f * t)) * env;
                    break;
                }
                case "sparkle":
                {
                    for (int k = 0; k < spOff.Length; k++)
                    {
                        float dt = t - spOff[k];
                        if (dt < 0f) continue;
                        v += Mathf.Sin(2f * Mathf.PI * spHz[k] * dt) * Mathf.Exp(-dt * 40f);
                    }
                    v *= 0.7f;
                    break;
                }
                case "bell":    // колокольчик: негармоничные призвуки, как у настоящего
                {
                    const float f0 = 1760f;
                    float env = Mathf.Exp(-u * 5f);
                    v = (Mathf.Sin(2f * Mathf.PI * f0 * t)
                         + 0.5f * Mathf.Sin(2f * Mathf.PI * f0 * 2.76f * t)
                         + 0.25f * Mathf.Sin(2f * Mathf.PI * f0 * 5.40f * t)) * env * 0.6f;
                    break;
                }
                case "wobble":  // «поплыло в глазах»: тон с вибрато
                {
                    float f = 620f * (1f + 0.25f * Mathf.Sin(2f * Mathf.PI * 11f * t));
                    float env = Mathf.Sin(Mathf.PI * u);
                    v = Mathf.Sin(2f * Mathf.PI * f * t) * env * 0.8f;
                    break;
                }
                case "zap":     // короткий электрический щёлк
                {
                    float noise = (float)(rnd.NextDouble() * 2.0 - 1.0);
                    float f = 2200f * Mathf.Pow(300f / 2200f, u);
                    float env = Mathf.Exp(-u * 25f);
                    v = (noise * 0.5f + Mathf.Sin(2f * Mathf.PI * f * t)) * env;
                    break;
                }
                case "hum":     // низкий гул с нарастанием
                {
                    float env = Mathf.Sin(Mathf.PI * u);
                    v = (Mathf.Sin(2f * Mathf.PI * 170f * t)
                         + 0.4f * Mathf.Sin(2f * Mathf.PI * 340f * t)) * env * 0.7f;
                    break;
                }
            }

            // Срез на концах: без него щелчок на старте и обрыве слышен сильнее самого звука.
            float fadeIn = Mathf.Clamp01(t / 0.003f);
            float fadeOut = Mathf.Clamp01((len - t) / 0.005f);
            buf[i] = Mathf.Clamp(v * fadeIn * fadeOut, -1f, 1f);
        }

        var clip = AudioClip.Create("sm_" + preset, n, 1, rate, false);
        clip.SetData(buf, 0);
        return clip;
    }

    /// <summary>Звук по имени: "synth:shing" — наш синтезированный, иначе ищем среди звуков игры.</summary>
    private SFX_Instance? ResolveSfx(string name)
    {
        if (name.StartsWith("synth:", System.StringComparison.OrdinalIgnoreCase))
            return SynthSfx(name.Substring(6));
        return FindSfx(name);
    }

    private void PlayBlindSfx(Character victim)
    {
        string want = _blindSfx.Value;
        if (string.IsNullOrWhiteSpace(want) || victim == null) return;
        try
        {
            var sfx = ResolveSfx(want);
            if (sfx == null)
            {
                Log.LogWarning($"Звук '{want}' не найден. Список звуков игры печатает клавиша N; " +
                               $"свои варианты: synth:{string.Join(", synth:", SynthPresets)}");
                return;
            }
            sfx.Play(victim.Center);
        }
        catch (System.Exception e) { Log.LogWarning($"Звук попадания: {e.GetType().Name}: {e.Message}"); }
    }

    private static SFX_Instance? _sfxCache;
    private static string _sfxCacheName = "";
    private static SFX_Instance? FindSfx(string name)
    {
        if (_sfxCache != null && _sfxCacheName == name) return _sfxCache;
        foreach (var s in Resources.FindObjectsOfTypeAll<SFX_Instance>())
        {
            if (s == null || s.name != name) continue;
            _sfxCache = s; _sfxCacheName = name; return s;
        }
        return null;
    }

    /// <summary>Отладка: печатает имена всех звуков игры — чтобы выбрать для BlindSfxName.</summary>
    /// <summary>
    /// Отладка: что из ресурсов игры можно создать по сети.
    ///
    /// Игра умеет GameUtils.SpawnResourceAtPositionNetworked(путь, позиция, цель) —
    /// это RPC_SpawnResourceAtPosition, который у КАЖДОГО клиента делает
    /// Resources.Load(путь) и создаёт объект у себя. Мод получателю не нужен,
    /// поэтому это единственный способ показать что-то игроку без мода.
    ///
    /// Но Resources.Load берёт ПУТЬ, а у объекта в памяти есть только имя: для
    /// префаба во вложенной папке имени мало. Поэтому список фильтруем проверкой
    /// Resources.Load(name) != null — то есть печатаем лишь то, что действительно
    /// получится создать. Иначе список был бы длинным и наполовину нерабочим.
    /// </summary>
    private void DumpPrefabs()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        GameObject[] all;
        try { all = Resources.LoadAll<GameObject>(""); }
        catch (System.Exception e) { Log.LogWarning($"[G] Не удалось перечислить ресурсы: {e.Message}"); return; }

        var terms = (_prefabFilter.Value ?? "").Split('|');
        var usable = new List<string>();
        var seenNames = new HashSet<string>();
        int matched = 0;

        foreach (var g in all)
        {
            if (g == null || !seenNames.Add(g.name)) continue;

            bool pass = terms.Length == 0;
            foreach (var raw in terms)
            {
                string t = raw.Trim();
                if (t.Length == 0) { pass = true; break; }
                if (g.name.IndexOf(t, System.StringComparison.OrdinalIgnoreCase) >= 0) { pass = true; break; }
            }
            if (!pass) continue;
            matched++;

            // создать по сети получится только то, что грузится по голому имени
            if (Resources.Load<GameObject>(g.name) != null) usable.Add(g.name);
        }

        usable.Sort();
        sw.Stop();
        Log.LogWarning($"[G] === префабы: всего в ресурсах {seenNames.Count}, " +
                       $"под фильтр '{_prefabFilter.Value}' подошло {matched}, " +
                       $"из них создаются по имени {usable.Count} (за {sw.ElapsedMilliseconds} мс) ===");
        foreach (var n in usable) Log.LogInfo($"  prefab='{n}'");
        if (usable.Count == 0)
            Log.LogWarning("[G] Ни одного пригодного. Поменяй Debug/PrefabDumpFilter — например на explo, spore, light, flash, star.");
    }

    private void DumpSfx()
    {
        var all = Resources.FindObjectsOfTypeAll<SFX_Instance>();
        var names = new List<string>();
        foreach (var s in all) if (s != null) names.Add(s.name);
        names.Sort();
        Log.LogWarning($"[N] === звуки игры ({names.Count} шт.), имя вписывать в Emote/BlindSfxName ===");
        foreach (var n in names) Log.LogInfo($"  sfx='{n}'");
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

            int vkey = VictimKey(victim);
            AddCharge(vkey, Time.deltaTime);
            if (TryFireCap(victim, vkey)) continue;
            Wobble(victim, st);

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

                // Чередование эмоутов. Замершая поза со стороны читается как зависший
                // персонаж. Если задан второй эмоут, вместо удержания одного кадра
                // проигрываем по очереди «руки к лицу» и «нет-нет» — жертва закрывается
                // и отмахивается от света. PlayEmote идёт RpcTarget.All, поэтому это
                // единственная часть зрелища, которую видят и игроки БЕЗ мода.
                // Чередуем не ВЫЗОВЫ эмоута, а то, какое состояние удерживаем.
                // Повторный PlayEmote на чужом персонаже не срабатывает — проверено
                // на боте: поза пропадала совсем, руки опускались.
                bool swapping = _emoteSwapEvery.Value > 0.05f && !string.IsNullOrEmpty(_emoteB.Value);
                if (swapping && Time.time >= st.nextEmoteAt)
                {
                    st.nextEmoteAt = Time.time + _emoteSwapEvery.Value;
                    st.emoteOnB = !st.emoteOnB;
                }
                string pinState = (swapping && st.emoteOnB) ? _emoteB.Value : st.emoteState;

                if (an != null && !string.IsNullOrEmpty(pinState) &&
                    Time.time - st.emoteStarted >= _poseFreezeDelay.Value)
                {
                    an.speed = 1f;
                    an.SetBool("Emote", true);

                    // Кадр клипа не фиксируем намертво. По умолчанию гоняем участок
                    // клипа по кругу (PingPong — вперёд-назад, без рывка на стыке):
                    // жертва жмурится непрерывно, пока луч в глазах. Старый режим
                    // (покачивание вокруг одного кадра) остаётся на PoseLoop=false.
                    float frac = _poseLoop.Value
                        ? Mathf.Clamp01(_poseFreezeAt.Value
                                        + _poseLoopSpan.Value * Mathf.PingPong(Time.time * _poseLoopSpeed.Value, 1f))
                        : Mathf.Clamp01(_poseFreezeAt.Value
                                        + _poseSwing.Value * Mathf.Sin(Time.time * _poseSwingSpeed.Value));
                    an.Play(pinState, 0, frac);

                    if (!st.posePinned)
                    {
                        st.posePinned = true;
                        Log.LogInfo($"Поза '{victim.name}': " +
                                    (_poseLoop.Value
                                        ? $"клип по кругу {_poseFreezeAt.Value:0.00}..{_poseFreezeAt.Value + _poseLoopSpan.Value:0.00}, скорость {_poseLoopSpeed.Value:0.0}"
                                        : $"кадр {_poseFreezeAt.Value:0.00} ± {_poseSwing.Value:0.00}") +
                                    (swapping ? $", чередование с '{_emoteB.Value}' каждые {_emoteSwapEvery.Value:0.0} с" : ""));
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

        UnrootVictim(victim);                    // отпускаем раньше, чем игра сделает это сама
        SpiralEyesForEveryone(victim, false);    // и возвращаем обычные глаза всем
        ShakeOff(victim);       // третий такт: видно, что отпустило

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
    /// Отладка: достаёт ссылку-приглашение в текущее лобби Steam.
    /// Нужна, чтобы позвать людей, которых нет в друзьях: оверлей Steam для этого
    /// открывать не обязательно, id лобби игра и так держит в SteamLobbyHandler.
    /// </summary>
    private void DebugLobbyLink()
    {
        try
        {
            var h = GameHandler.GetService<SteamLobbyHandler>();
            if (h == null) { Log.LogWarning("[F5] SteamLobbyHandler недоступен."); return; }

            string lobby = h.CurrentLobbyID;
            bool inLobby = h.InSteamLobby();
            Log.LogWarning($"[F5] лобби='{lobby}' вЛобби={inLobby}");

            if (string.IsNullOrEmpty(lobby) || lobby == "0")
            {
                Log.LogWarning("[F5] Лобби ещё не создано.");
                return;
            }

            ulong owner = 0;
            try { owner = Steamworks.SteamMatchmaking.GetLobbyOwner(h.LobbySteamId).m_SteamID; }
            catch (System.Exception e) { Log.LogWarning($"[F5] владелец лобби: {e.Message}"); }

            Log.LogWarning($"[F5] ССЫЛКА: steam://joinlobby/3527290/{lobby}/{owner}");
        }
        catch (System.Exception e) { Log.LogWarning($"[F5] не вышло: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>
    /// Отладка: загнать клиент в комнату Photon с фиксированным именем.
    /// Нужно, чтобы свести два экземпляра игры на одной машине: PEAK собирает лобби
    /// через Steam, а оба клиента будут под одним аккаунтом. Photon про Steam не знает,
    /// комнаты у него свои — значит клиенты могут встретиться напрямую.
    /// Это штатный метод PUN, а не обход чего-либо.
    /// </summary>
    private void DebugJoinTestRoom()
    {
        Log.LogWarning($"[F6] Photon до: состояние={PhotonNetwork.NetworkClientState} " +
                       $"подключён={PhotonNetwork.IsConnected} вКомнате={PhotonNetwork.InRoom} " +
                       $"офлайн={PhotonNetwork.OfflineMode} " +
                       $"комната='{(PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.Name : "нет")}' " +
                       $"игроков={(PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 0)}");

        if (PhotonNetwork.OfflineMode)
        {
            Log.LogWarning("[F6] Клиент в офлайне — выключаю офлайн и подключаюсь.");
            PhotonNetwork.OfflineMode = false;
        }

        try
        {
            if (!PhotonNetwork.IsConnected)
            {
                Log.LogWarning("[F6] Нет соединения — ConnectUsingSettings(), жми F6 ещё раз через пару секунд.");
                PhotonNetwork.ConnectUsingSettings();
                return;
            }

            var opts = new Photon.Realtime.RoomOptions { MaxPlayers = 4, IsVisible = false, IsOpen = true };
            bool ok = PhotonNetwork.JoinOrCreateRoom("SunMirrorTest", opts, Photon.Realtime.TypedLobby.Default);
            Log.LogWarning($"[F6] JoinOrCreateRoom('SunMirrorTest') вернул {ok}.");
        }
        catch (System.Exception e) { Log.LogWarning($"[F6] не вышло: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>
    /// Отладка: свести два клиента на одной машине в одну комнату, мимо Steam-лобби.
    ///
    /// Steam не даёт запустить игру дважды с одного аккаунта, но сетевая часть PEAK
    /// живёт в Photon, а не в Steam. Поэтому хост, уже начавший забег, по F7 кладёт
    /// имя своей комнаты в файл во временной папке, а второй клиент по F7 это имя
    /// читает и входит в ту же комнату. Дальше игра сама спавнит персонажа вошедшего —
    /// и становится видно, доходят ли наши RPC до ЧУЖОГО клиента. Ради одной этой
    /// проверки и затевалось: на одном клиенте её не сделать никак.
    /// </summary>
    private static string RoomHandshakeFile =>
        Path.Combine(Path.GetTempPath(), "sunmirror_room.txt");

    /// <summary>
    /// Второй клиент сам входит в комнату хоста. Нужно потому, что окно второго
    /// клиента может быть недоступно для управления: нажать там клавишу нечем,
    /// а войти надо. Пробуем раз в пять секунд, пока не окажемся в комнате.
    /// </summary>
    private void OnPhotonOpResponse(ExitGames.Client.Photon.OperationResponse r)
    {
        if (r.ReturnCode != 0)
            Log.LogWarning($"[PHOTON] операция {r.OperationCode}: отказ, код={r.ReturnCode}, сообщение='{r.DebugMessage}'");
    }

    private IEnumerator AutoJoinLoop()
    {
        yield return new WaitForSecondsRealtime(20f);   // дать меню и сервисам подняться
        // Фотон не говорит причину отказа через PhotonNetwork — она приходит
        // отдельным ответом на операцию. Без неё «не вошёл» остаётся загадкой.
        if (PhotonNetwork.NetworkingClient != null)
            PhotonNetwork.NetworkingClient.OpResponseReceived += OnPhotonOpResponse;

        // Цикл не кончается нарочно: при смене сцены (перелёт на остров) гостя
        // выбрасывает из комнаты, и вернуться он должен сам — нажать клавишу
        // в его окне может быть некому. Заодно это единственная телеметрия
        // со второго клиента: его экран нам не виден.
        int tick = 0;
        while (true)
        {
            if (!PhotonNetwork.InRoom)
            {
                // Дёргать вход, пока предыдущая попытка ещё в пути, бессмысленно и вредно:
                // клиент застревает в Joining. Трогаем только «устоявшиеся» состояния.
                var st = PhotonNetwork.NetworkClientState;
                bool settled = st == ClientState.PeerCreated
                            || st == ClientState.Disconnected
                            || st == ClientState.ConnectedToMasterServer;
                if (settled)
                {
                    Log.LogWarning($"[AUTOJOIN] не в комнате, состояние={st}");
                    DebugRoomHandshake();
                }
                yield return new WaitForSecondsRealtime(5f);
                continue;
            }

            if (tick++ % 2 == 0)
            {
                var me = Character.localCharacter;
                Log.LogWarning($"[AUTOJOIN] в комнате '{PhotonNetwork.CurrentRoom.Name}', " +
                               $"игроков {PhotonNetwork.CurrentRoom.PlayerCount}, " +
                               $"свой персонаж={(me != null ? "есть" : "нет")}, " +
                               $"сцена '{SceneManager.GetActiveScene().name}'");
            }
            yield return new WaitForSecondsRealtime(5f);
        }
    }

    private void DebugRoomHandshake()
    {
        try
        {
            // Писать имя комнаты — дело хоста. Гость (он же автовход) этого не делает,
            // иначе затрёт файл своим же значением и запутает следующий запуск.
            if (PhotonNetwork.InRoom && !_autoJoinRoom.Value)
            {
                string mine = PhotonNetwork.CurrentRoom.Name;
                File.WriteAllText(RoomHandshakeFile, mine);
                Log.LogWarning($"[F7] Я в комнате '{mine}' (игроков {PhotonNetwork.CurrentRoom.PlayerCount}), " +
                               $"регион '{PhotonNetwork.CloudRegion}'. Имя записано в {RoomHandshakeFile}");
                return;
            }

            if (!File.Exists(RoomHandshakeFile))
            {
                Log.LogWarning($"[F7] Нет файла {RoomHandshakeFile} — сначала нажми F7 на хосте, уже начавшем забег.");
                return;
            }

            string name = File.ReadAllText(RoomHandshakeFile).Trim();
            if (string.IsNullOrEmpty(name)) { Log.LogWarning("[F7] В файле пусто."); return; }

            // Просто PhotonNetwork.JoinRoom мало: игра ведёт вход через свою машину
            // состояний (ConnectionService). Если её не выставить, OnJoinedRoom уйдёт
            // не туда, сцена не загрузится и персонаж не появится. Поэтому выставляем
            // ровно то состояние, которое игра использует для «войти в конкретную комнату».
            var svc = GameHandler.GetService<ConnectionService>();
            if (svc == null) { Log.LogWarning("[F7] ConnectionService недоступен."); return; }

            svc.StateMachine.SwitchState<JoinSpecificRoomState>();
            if (svc.StateMachine.CurrentState is JoinSpecificRoomState js)
            {
                js.RoomName = name;
                // Тот же регион, что и сейчас — тогда игра идёт прямо в JoinRoom,
                // без переподключения к другому облаку.
                js.RegionToJoin = PhotonNetwork.CloudRegion;
                Log.LogWarning($"[F7] Состояние -> JoinSpecificRoomState, комната '{name}', регион '{js.RegionToJoin}'.");
            }
            else
            {
                Log.LogWarning("[F7] Не удалось переключить состояние подключения.");
                return;
            }

            if (PhotonNetwork.OfflineMode)
            {
                Log.LogWarning("[F7] Клиент в офлайне — выключаю офлайн.");
                PhotonNetwork.OfflineMode = false;
            }

            if (!PhotonNetwork.IsConnected)
            {
                if (_guestUserId == null)
                {
                    // Важно: именно метод игры, а не голый ConnectUsingSettings.
                    // Игра перед подключением выставляет GameVersion и AppVersion из версии
                    // сборки — без этого клиент попадает в другую «версию приложения»
                    // и комнаты хоста просто не видит: Photon отвечает «Game does not exist».
                    Log.LogWarning("[F7] Нет соединения — подключаюсь методом игры NetworkingUtilities.ConnectToNetwork().");
                    NetworkingUtilities.ConnectToNetwork();
                }
                else
                {
                    // Версию приложения игра уже выставила при первом подключении —
                    // это статические поля, они сохранились. Теперь заходим под своим UserId.
                    PhotonNetwork.AuthValues = new Photon.Realtime.AuthenticationValues(_guestUserId);
                    PhotonNetwork.NickName = "SunMirror-гость";
                    Log.LogWarning($"[F7] Переподключаюсь со своим UserId '{_guestUserId}'.");
                    PhotonNetwork.ConnectUsingSettings();
                }
                return;
            }

            if (_guestUserId == null)
            {
                // Оба клиента на одной машине берут Photon UserId из одного SteamID,
                // и второму Photon отказывает: «UserId already joined the specified game».
                // Поэтому гостю выдаём собственный идентификатор и перезаходим с ним.
                string baseId = PhotonNetwork.AuthValues != null ? PhotonNetwork.AuthValues.UserId : "guest";
                _guestUserId = baseId + "-sm2";
                Log.LogWarning($"[F7] UserId совпадает с хостом ('{baseId}'). Отключаюсь и зайду как '{_guestUserId}'.");
                PhotonNetwork.Disconnect();
                return;
            }

            Log.LogWarning($"[F7] GameVersion='{PhotonNetwork.GameVersion}' " +
                           $"AppVersion='{PhotonNetwork.PhotonServerSettings.AppSettings.AppVersion}' " +
                           $"регион='{PhotonNetwork.CloudRegion}' UserId='{PhotonNetwork.AuthValues?.UserId}'");

            bool ok = PhotonNetwork.JoinRoom(name);
            Log.LogWarning($"[F7] JoinRoom('{name}') вернул {ok}. Состояние={PhotonNetwork.NetworkClientState}");
        }
        catch (System.Exception e) { Log.LogWarning($"[F7] не вышло: {e.GetType().Name}: {e.Message}"); }
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

    /// <summary>
    /// F4 — собрать испытательный стенд одной клавишей.
    ///
    /// Зачем: раньше проверка эффекта стоила пяти отдельных действий (зеркало,
    /// полдень, бот, развернуть, прицелиться), и на каждое уходило время, а
    /// прицеливание мышью по скриншотам промахивалось чаще, чем попадало.
    /// Здесь всё это делается разом и без прицеливания.
    /// </summary>
    private void SetupTestBench()
    {
        Log.LogWarning("[F4] Собираю стенд: зеркало, полдень, бот.");
        GiveBinoculars();
        DebugNoon();
        DebugSpawnBot();
        Log.LogWarning("[F4] Готово. F3 — ослепить ближайшего, прицеливаться не нужно.");
    }

    /// <summary>
    /// F3 — ослепить ближайшего персонажа, минуя луч и прицеливание.
    ///
    /// Нужно потому, что проверять ВИД эффекта и проверять ПОПАДАНИЕ луча —
    /// две разные задачи, а раньше они были склеены: чтобы посмотреть на позу,
    /// приходилось сначала попасть зайчиком в глаза. Здесь эффект применяется
    /// тем же путём, что и при настоящем попадании (эмоут + SendBlind), только
    /// цель выбирается по расстоянию.
    /// </summary>
    /// <summary>
    /// Автоослепление ближайшего: клиент работает «обидчиком» без человека за ним.
    ///
    /// Шлём каждый кадр, как при удержании луча, а не одиночным импульсом: иначе
    /// «фитиль» не накопится и падения не будет. Сетевой поток при этом не растёт —
    /// SendBlind сам ограничивает частоту посылок.
    /// </summary>
    private void TickAutoBlind(Character? me)
    {
        if (!_autoBlind.Value || me == null) return;

        Character? best = null;
        float bestD = _autoBlindRange.Value;
        foreach (var c in Object.FindObjectsByType<Character>(FindObjectsSortMode.None))
        {
            if (c == null || c == me) continue;
            var d = c.data;
            if (d == null || d.dead || d.fullyPassedOut) continue;
            float dist = Vector3.Distance(c.Center, me.Center);
            if (dist < bestD) { bestD = dist; best = c; }
        }
        if (best == null) return;

        // Разворачиваем обидчика к цели. Без этого он стоит спиной и ни на что
        // не наводит, а жертву при этом накрывает — проверять «читается ли шутка»
        // на такой картинке бессмысленно: в настоящей игре видно, что на тебя
        // направили тарелочку. Поворот тела синхронизируется игрой, поэтому его
        // видят все, включая клиентов без мода.
        Vector3 look = best.Center - me.Center;
        look.y = 0f;
        if (look.sqrMagnitude > 1e-4f)
        {
            look.Normalize();
            me.transform.rotation = Quaternion.LookRotation(look);
            if (me.data != null)
            {
                me.data.lookDirection = look;
                me.data.lookDirection_Flat = look;
            }
        }

        if (best != _autoBlindLast)
        {
            _autoBlindLast = best;
            Log.LogWarning($"[AUTO] Держу луч на '{best.name}' ({bestD:0.0} м). " +
                           $"Зеркало в руках: {(_holdingMirror ? "да" : "НЕТ — нажми F4 на этом клиенте")}.");
            if (_useEmote.Value && !string.IsNullOrEmpty(_emoteName.Value))
                PlayVictimEmote(best, _emoteName.Value);
        }
        SendBlind(best, _beamLinger.Value);
    }

    private Character? _autoBlindLast;

    private void DebugBlindNearest()
    {
        var me = Character.localCharacter;
        if (me == null) { Log.LogWarning("[F3] Нет своего персонажа."); return; }

        Character? best = null;
        float bestD = float.MaxValue;
        foreach (var c in Object.FindObjectsByType<Character>(FindObjectsSortMode.None))
        {
            if (c == null || c == me) continue;
            var d = c.data;
            if (d == null || d.dead || d.fullyPassedOut) continue;
            float dist = Vector3.Distance(c.Center, me.Center);
            if (dist < bestD) { bestD = dist; best = c; }
        }

        if (best == null)
        {
            Log.LogWarning("[F3] Рядом никого нет — поставь бота клавишей F11 или F4.");
            return;
        }

        if (_useEmote.Value && !string.IsNullOrEmpty(_emoteName.Value))
            PlayVictimEmote(best, _emoteName.Value);
        SendBlind(best, _beamLinger.Value);
        Log.LogWarning($"[F3] Ослеплён '{best.name}' в {bestD:0.0} м (без луча, для проверки вида).");
    }

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
            CopyLookToBot(found);
            Log.LogWarning($"[F11] Болванчик '{found.name}' поставлен лицом к игроку (попытка {attempt + 1}).");
            yield break;
        }
        Log.LogWarning("[F11] Бот рядом с точкой спавна не нашёлся — развернуть не смог.");
    }

    /// <summary>
    /// Одеть бота так же, как выглядит игрок.
    ///
    /// Болванчик заменяет живого тиммейта — и в отладке, и на съёмке. Стандартный бот
    /// выглядит иначе, и кадр получается «не про игроков». Внешность в PEAK лежит не на
    /// персонаже, а в сервисе PersistentPlayerDataService (по Photon-игроку), а применяет
    /// её CharacterCustomization.OnPlayerDataChange — ему и скармливаем свои данные.
    /// </summary>
    private void CopyLookToBot(Character bot)
    {
        try
        {
            var cc = bot.refs?.customization;
            if (cc == null) { Log.LogWarning("[F11] У бота нет CharacterCustomization."); return; }

            var svc = GameHandler.GetService<PersistentPlayerDataService>();
            var mine = svc?.GetPlayerData(PhotonNetwork.LocalPlayer);
            if (mine == null) { Log.LogWarning("[F11] Свои настройки внешности не нашлись."); return; }

            // Без этого применение молча выйдет на первой же строке OnPlayerDataChange:
            // боты по умолчанию косметику игроков игнорируют.
            cc.ignorePlayerCosmetics = false;
            cc.useDebugColor = false;
            cc.OnPlayerDataChange(mine);

            var d = mine.customizationData;
            Log.LogWarning($"[F11] Бот одет как игрок: кожа={d.currentSkin} наряд={d.currentOutfit} " +
                           $"шляпа={d.currentHat} глаза={d.currentEyes} рот={d.currentMouth}.");
        }
        catch (System.Exception e) { Log.LogWarning($"[F11] Внешность бота: {e.GetType().Name}: {e.Message}"); }
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
