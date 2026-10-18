using Photon.Pun;
using UnityEngine;

namespace SunMirror;

/// <summary>
/// Приёмник сетевого ослепления.
///
/// Зачем отдельный компонент: Photon ищет метод RPC среди MonoBehaviour,
/// висящих на том же объекте, что и PhotonView. Дописать метод в чужой
/// Character мы не можем, поэтому вешаем свой компонент рядом и дёргаем
/// PhotonView.RefreshRpcMonoBehaviourCache() — иначе Photon будет искать
/// в устаревшем списке компонентов и метод не найдёт.
///
/// Почему вообще через сеть: визуал ослепления — это подмена текстур на
/// модели персонажа, а модель каждый клиент рисует свою. Чтобы спиральные
/// глаза и рот увидели ВСЕ, эффект должен примениться на каждом клиенте.
/// Сама игра устроена так же: CharacterAfflictions.AddAffliction молча
/// выходит, если персонаж не локальный.
///
/// Ограничение, которое не обойти: у игрока БЕЗ мода этого компонента нет,
/// его Photon напишет в лог «нет такого метода» и ничего не произойдёт.
/// Для таких игроков работает только нативный эмоут (RPCA_PlayRemove),
/// который игра рассылает сама.
/// </summary>
internal sealed class SunMirrorNet : MonoBehaviour
{
    internal const string RpcName = nameof(RPCA_SunMirrorBlind);

    [PunRPC]
    public void RPCA_SunMirrorBlind(float seconds, int shooterViewID)
    {
        if (Plugin.Instance == null) return;
        var victim = GetComponent<Character>();
        if (victim == null) return;
        Plugin.Log.LogWarning($"[RPC] получено ослепление: цель='{victim.name}' " +
                              $"локальная={victim.IsLocal} бот={victim.isBot} " +
                              $"сек={seconds:0.0} от viewID={shooterViewID}");
        Plugin.Instance.ApplyNetworkBlind(victim, seconds, shooterViewID);
    }
}
