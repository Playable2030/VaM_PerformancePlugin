using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace VaM_PerformancePlugin.VaM;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class GenerateDAZMorphsControlUIPatch
{
    // constructor patch to allow changing of default values via Plugin options
    [HarmonyPatch(typeof(GenerateDAZMorphsControlUI), MethodType.Constructor)]
    [HarmonyPostfix]
    public static void CTOR(ref GenerateDAZMorphsControlUI __instance)
    {
        var Options = VaMPerformancePlugin.Options;
        __instance.onlyShowActive = Options.Character_onlyShowActive.Value;
        __instance.onlyShowFavorites = Options.Character_onlyShowFavorites.Value;
        __instance.onlyShowLatest = Options.Character_onlyShowLatest.Value;
        VaMPerformancePlugin.PluginLogger.LogDebug("Successfully finished patched CTOR for GenerateDAZMorphsControlUI");
    }
}