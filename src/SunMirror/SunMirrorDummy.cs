using UnityEngine;

namespace SunMirror;

/// <summary>
/// Делает из игрового бота неподвижного болванчика, стоящего лицом к игроку.
///
/// Разворота один раз не хватает: персонаж в PEAK — физическая кукла, её поворот
/// задаёт не transform, а связка Bot → BotToCharacterTranslator, которая каждый кадр
/// переливает решения ИИ в character.input и character.data.lookValues.
/// Поэтому подменяем решения самого Bot, а не результат.
///
/// Нужен только для отладки: на болванчике видно реакцию (руки к лицу, глаза, рот)
/// так, как её увидит тиммейт, и при этом он не убегает.
/// </summary>
internal sealed class SunMirrorDummy : MonoBehaviour
{
    private Bot? _bot;

    private void Awake() => _bot = GetComponent<Bot>() ?? GetComponentInChildren<Bot>();

    private void LateUpdate()
    {
        if (_bot == null) return;
        var me = Character.localCharacter;
        if (me == null) return;

        // Стоим на месте и никого не преследуем.
        _bot.MovementInput = Vector2.zero;
        _bot.IsSprinting = false;
        _bot.TargetCharacter = null;
        _bot.targetPos_Set = _bot.Center;

        // И смотрим на игрока.
        Vector3 dir = me.Center - _bot.Center;
        dir.y = 0f;
        if (dir.sqrMagnitude > 1e-4f) _bot.LookDirection = dir.normalized;
    }
}
