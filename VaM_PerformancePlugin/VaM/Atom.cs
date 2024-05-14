using System;
using HarmonyLib;

namespace VaM_PerformancePlugin.VaM;

public class AtomPatch
{
    
    [HarmonyPatch(typeof(Atom), nameof(Atom.uidWithoutSubScenePath), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool uidWithoutSubScenePath(ref string __result, ref string ____uid, ref string ____subScenePath)
    {
        if (!String.IsNullOrEmpty(____subScenePath))
        {
            __result = string.Copy(____uid).Replace(____subScenePath, string.Empty);
        }
        else
        {
            __result = ____uid;
        }

        return false;
    }
}