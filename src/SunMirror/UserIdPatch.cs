using HarmonyLib;
using Photon.Realtime;

namespace SunMirror;

/// <summary>
/// Приписка к идентификатору Photon, чтобы два клиента на одной машине могли
/// сидеть в одной комнате.
///
/// Зачем. PEAK берёт идентификатор из Steam, а Steam-аккаунт у обоих запусков
/// один. Photon отбивает второй вход ошибкой «UserId already joined» (32746).
/// Раньше мы обходили это внутри рукопожатия по F7, но то решение годилось
/// только когда мод стоял на обоих клиентах. Для проверки «что видит игрок
/// БЕЗ мода» второй клиент должен быть чистым, и чинить идентификатор
/// приходится тому, у кого мод есть.
///
/// Патчим LoadUserID, а не точку подключения: её зовут оба пути входа в сеть,
/// так что одна правка закрывает и создание игры, и присоединение.
///
/// По умолчанию приписка пустая — на обычной игре это ничего не меняет.
/// </summary>
[HarmonyPatch(typeof(Peak.Network.NetworkingUtilities),
              nameof(Peak.Network.NetworkingUtilities.LoadUserID))]
internal static class UserIdSuffixPatch
{
    /// <summary>Выставляется из Plugin при чтении конфига. Пусто — патч бездействует.</summary>
    internal static string Suffix = "";

    [HarmonyPostfix]
    private static void Postfix(AuthenticationValues __result)
    {
        if (string.IsNullOrEmpty(Suffix)) return;
        if (__result == null)
        {
            Plugin.Log?.LogWarning("[ID] LoadUserID вернул null — приписку ставить некуда.");
            return;
        }
        if (string.IsNullOrEmpty(__result.UserId))
        {
            Plugin.Log?.LogWarning("[ID] LoadUserID вернул пустой UserId — приписку ставить некуда.");
            return;
        }
        // Проверка на хвост: LoadUserID могут позвать несколько раз за сессию,
        // и без неё приписка наросла бы в «...-sm2-sm2-sm2».
        if (__result.UserId.EndsWith(Suffix)) return;
        __result.UserId += Suffix;
        Plugin.Log?.LogWarning($"[ID] LoadUserID: приписка поставлена, UserId='{__result.UserId}'.");
    }
}
