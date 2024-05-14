using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace VaM_PerformancePlugin.VaM;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class DAZMorphBankPatch
{

    [HarmonyPatch(typeof(DAZMorphBank), "BuildMorphSubBanksByRegion")]
    [HarmonyPrefix]
    public static bool BuildMorphSubBanksByRegion()
    {
        return true;
    }

    [HarmonyPatch(typeof(DAZMorphBank), "UnloadDemandActivatedMorphs")]
    [HarmonyPrefix]
    public static bool UnloadDemandActivatedMorphs()
    {
        return true;
    }
}