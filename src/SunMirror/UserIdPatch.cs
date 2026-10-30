using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;

namespace SunMirror;

/// <summary>
/// Отдельный идентификатор Photon, чтобы два клиента на одной машине могли
/// сидеть в одной комнате.
///
/// Зачем. PEAK берёт идентификатор из Steam, а Steam-аккаунт у обоих запусков
/// один. Photon отбивает второй вход ошибкой «UserId already joined» (32746).
/// Для проверки «что видит игрок БЕЗ мода» второй клиент должен быть чистым,
/// поэтому чинить идентификатор приходится тому, у кого мод есть.
///
/// Почему именно сеттер PhotonNetwork.AuthValues. Прошлая версия патчила
/// NetworkingUtilities.LoadUserID — и молчала: этот метод зовут только из
/// JoinDummyRoom, то есть в офлайне, а сетевой путь идёт мимо него. Настоящее
/// место нашлось в SteamPhotonAuthManager.Authenticate, он собирает AuthValues
/// сам. Но вставать на него плохо: это корутина, и платформенных менеджеров
/// пять штук. Сеттер свойства — узкое горлышко, через которое проходят все.
///
/// ВНИМАНИЕ: своей цели патч НЕ достигает, и это проверено 30.10. Он оставлен
/// как единственное рабочее место для правки идентификатора и как запись о том,
/// почему затея невозможна — чтобы её не начинали заново.
///
/// Попытка первая: выдать гостю аутентификацию без Steam
/// (CustomAuthenticationType.None) — ту же, что игра использует в LoadUserID.
/// Сервер отказал прямо на name-сервере:
///
///     OperationResponse 230: ReturnCode: 32755
///     (Authentication type 'None' not supported)
///
/// Приложение Photon у PEAK обязано иметь внешнюю аутентификацию.
///
/// Попытка вторая (её код ниже): оставить билет и тип Steam, поменять только
/// UserId. Клиент подключается без ошибки, но идентификатор не держится:
///
///     [ID] UserId -> '76561198050820244-sm2'
///     [F7] AuthValues='76561198050820244', LocalPlayer.UserId='76561198050820244'
///
/// Сервер выдаёт UserId по Steam-билету и клиентский игнорирует. Значит два
/// запуска с одного Steam-аккаунта всегда приходят под одним идентификатором,
/// и проверка «что видит игрок без мода» требует второго аккаунта или живого
/// второго человека — см. TESTING-2P.md.
///
/// По умолчанию приписка пустая — на обычной игре патч не делает ничего.
/// </summary>
[HarmonyPatch(typeof(PhotonNetwork), nameof(PhotonNetwork.AuthValues), MethodType.Setter)]
internal static class UserIdSuffixPatch
{
    /// <summary>Выставляется из Plugin при чтении конфига. Пусто — патч бездействует.</summary>
    internal static string Suffix = "";

    /// <summary>
    /// Приписка была поставлена хотя бы раз. Нужно потому, что читать её обратно
    /// из PhotonNetwork.AuthValues нельзя: после аутентификации там снова лежит
    /// голый SteamID — сервер возвращает свой идентификатор и затирает наш.
    /// </summary>
    internal static bool Applied;

    [HarmonyPrefix]
    private static void Prefix(ref AuthenticationValues value)
    {
        if (string.IsNullOrEmpty(Suffix) || value == null) return;

        // Повторный вызов: приписка уже стоит. Без этой проверки она наросла бы
        // в «...-sm2-sm2-sm2» — AuthValues выставляют несколько раз за сессию.
        if (string.IsNullOrEmpty(value.UserId) || value.UserId.EndsWith(Suffix)) return;

        // Объект не подменяем: SteamPhotonAuthManager сразу после присваивания
        // добавляет в него билет через AddAuthParameter. Меняем одно поле.
        value.UserId += Suffix;
        Applied = true;
        Plugin.Log?.LogWarning($"[ID] UserId -> '{value.UserId}' (тип аутентификации '{value.AuthType}' оставлен игре).");
    }
}
