using HarmonyLib;

namespace SunMirror;

/// <summary>
/// Ловим показ предмета «Бинокль» (itemID 125) и превращаем его в зеркало.
/// OnEnable() срабатывает при каждой активации объекта — в т.ч. при переиспользовании из пула,
/// поэтому надёжнее, чем Start() (тот выполняется лишь раз за жизнь объекта).
/// </summary>
[HarmonyPatch(typeof(Item), "OnEnable")]
internal static class BinocularsReskinPatch
{
    private static void Postfix(Item __instance)
    {
        try
        {
            if (__instance == null || Plugin.Instance == null) return;
            if (__instance.itemID != 125) return;
            Plugin.Instance.ReskinBinoculars(__instance);
        }
        catch (System.Exception e)
        {
            Plugin.Log?.LogWarning($"ReskinPatch: {e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>Глушим родной зум-оверлей бинокля (два круга) — у нас зеркало.</summary>
[HarmonyPatch(typeof(Action_ShowBinocularOverlay), "RunAction")]
internal static class SuppressBinocularOverlay_RunAction
{
    private static bool Prefix() => false;   // не запускаем показ оверлея
}

[HarmonyPatch(typeof(Action_ShowBinocularOverlay), "Update")]
internal static class SuppressBinocularOverlay_Update
{
    private static bool Prefix() => false;   // и не поддерживаем его
}

/// <summary>Глушим приближение FOV бинокля.</summary>
[HarmonyPatch(typeof(CameraOverride_Binoculars), "Update")]
internal static class SuppressBinocularFov_Update
{
    private static bool Prefix() => false;
}

[HarmonyPatch(typeof(CameraOverride_Binoculars), "AdjustFOV")]
internal static class SuppressBinocularFov_Adjust
{
    private static bool Prefix() => false;
}

// ── Руководство (Wilderness Handbook) как зеркало: книга не должна открываться ──

/// <summary>ЛКМ по руководству больше не открывает книгу — это зеркало.</summary>
[HarmonyPatch(typeof(Action_Guidebook), "RunAction")]
internal static class SuppressGuidebook_RunAction
{
    private static bool Prefix() => false;
}

/// <summary>Само переключение открыто/закрыто — глушим и его (страховка от других вызовов).</summary>
[HarmonyPatch(typeof(Guidebook), "ToggleGuidebook")]
internal static class SuppressGuidebook_Toggle
{
    private static bool Prefix() => false;
}

/// <summary>Листание страниц колесом — не нужно.</summary>
[HarmonyPatch(typeof(Action_GuidebookScroll), "ScrollLeft")]
internal static class SuppressGuidebook_ScrollLeft
{
    private static bool Prefix() => false;
}

[HarmonyPatch(typeof(Action_GuidebookScroll), "ScrollRight")]
internal static class SuppressGuidebook_ScrollRight
{
    private static bool Prefix() => false;
}
