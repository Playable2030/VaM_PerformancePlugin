using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace VaM_PerformancePlugin.VaM;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class GlobalStopwatchPatch
{
    [HarmonyPatch(typeof(GlobalStopwatch), nameof(GlobalStopwatch.GetElapsedMilliseconds))]
    [HarmonyPrefix]
    public static bool GetElapsedMilliseconds(ref float __result)
    {
        __result = 0.0f;
        return false;
    }
    
    [HarmonyPatch(typeof(GlobalStopwatch), "Awake")]
    [HarmonyPrefix]
    public static bool Awake()
    {
        return false;
    }

    [HarmonyPatch(typeof(GlobalStopwatch), "OnDestroy")]
    [HarmonyPrefix]
    public static bool OnDestroy()
    {
        return false;
    }

}