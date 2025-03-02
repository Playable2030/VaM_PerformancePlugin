using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using VaM_PerformancePlugin.extra;

namespace VaM_PerformancePlugin.VaM;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class UnityThreadHelperPatch
{
    private static readonly MethodInfo EnsureHelperInstanceMethodInfo =
        typeof(UnityThreadHelper).GetMethod("EnsureHelperInstance", BindingFlags.NonPublic)!;


    // // sanity checks that prevent running patches if something is wrong 
    // private static bool IsValid()
    // {
    //     if (EnsureHelperInstanceMethodInfo is null)
    //     {
    //         return false;
    //     }
    //     // else
    //     // {
    //
    //     return true;
    // }

    [HarmonyPatch(typeof(UnityThreadHelper), nameof(UnityThreadHelper.EnsureHelper))]
    [HarmonyPrefix]
    public static bool EnsureHelper(ref object ___syncRoot, ref UnityThreadHelper ___instance)
    {
        // if (!IsValid())
        // {
        //     return true;
        // }

        // optimize perf by double-check locking
        // aka fail fast without locking
        if (___instance)
        {
            return false;
        }

        lock (___syncRoot)
        {
            // handle the case where the init happened while we were locking
            if (___instance)
            {
                return false;
            }

            // conscious decision not to re-impl `FindObjectOfType` here, it seems unnecessary and slow
            // UnityThreadHelper otherInstance = UnityEngine.Object.FindObjectOfType<UnityThreadHelper>();
            // if (otherInstance)
            // {
            //     __instance = otherInstance;
            //     return false;
            // }

            GameObject gameObject = new GameObject("[UnityThreadHelper]")
            {
                hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector | HideFlags.NotEditable
            };
            ___instance = gameObject.AddComponent<UnityThreadHelper>();

            EnsureHelperInstanceMethodInfo.Invoke(___instance, null);
        }

        return false;
    }
    
    [HarmonyPatch]
    [HarmonyFinalizer]
    public static Exception Finalizer(Exception __exception)
    {
        return new PluginException("UnityThreadHelper had an exception", __exception);
    }
}