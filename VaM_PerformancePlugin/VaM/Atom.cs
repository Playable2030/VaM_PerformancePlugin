using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace VaM_PerformancePlugin.VaM;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class AtomPatch
{
    
    [HarmonyPatch(typeof(Atom), nameof(Atom.uidWithoutSubScenePath), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool uidWithoutSubScenePath(ref string __result, ref string ____uid, ref string ____subScenePath)
    {
        __result = !string.IsNullOrEmpty(____subScenePath) ? string.Copy(____uid).Replace(____subScenePath, string.Empty) : ____uid;

        return false;
    }
}