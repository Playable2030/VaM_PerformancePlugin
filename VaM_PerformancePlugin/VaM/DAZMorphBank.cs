using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace VaM_PerformancePlugin.VaM;

// Not current used, needs heavy rewrite to avoid poor performance due to iterating over each morph
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