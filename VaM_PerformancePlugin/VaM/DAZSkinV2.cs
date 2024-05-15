using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using MeshVR;

namespace VaM_PerformancePlugin.VaM;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class DAZSkinV2Patch
{
    [HarmonyPatch(typeof(DAZSkinV2), "FindNodeByUrl")]
    [HarmonyPrefix]
    public static bool FindNodeByUrl(ref DAZSkinV2Node __result, string url, ref DAZSkinV2Node __instance,
        ref List<DAZSkinV2Node> ___importNodes, ref string ___skinUrl)
    {
        string str = url;
        if (str.StartsWith("#"))
        {
            str = DAZImport.DAZurlToPathKey(___skinUrl) + str;
        }

        for (int i = 0; i < ___importNodes.Count; i++)
        {
            if (___importNodes[i].url == str)
            {
                __result = ___importNodes[i];
                return false;
            }
        }
        
        UnityEngine.Debug.LogError( ("[VaM_PerformancePlugin] Could not find node by url " + str));
        __result = null;
        return false;
    }
}