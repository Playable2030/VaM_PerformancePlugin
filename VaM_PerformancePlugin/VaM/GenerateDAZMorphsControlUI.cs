﻿using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using VaM_PerformancePlugin.extra;

namespace VaM_PerformancePlugin.VaM;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class GenerateDAZMorphsControlUIPatch
{
    // constructor patch to allow changing of default values via Plugin options
    // [HarmonyPatch(typeof(GenerateDAZMorphsControlUI), MethodType.Constructor)]
    // [HarmonyPostfix]
    // public static void CTOR(ref GenerateDAZMorphsControlUI __instance)
    // {
    //     var Options = VaMPerformancePlugin.Options;
    //     __instance.onlyShowActive = Options.Character_onlyShowActive.Value;
    //     __instance.onlyShowFavorites = Options.Character_onlyShowFavorites.Value;
    //     __instance.onlyShowLatest = Options.Character_onlyShowLatest.Value;
    //     VaMPerformancePlugin.PluginLogger.LogDebug("Successfully finished patched CTOR for GenerateDAZMorphsControlUI");
    // }
    
    [HarmonyPatch(typeof(GenerateDAZMorphsControlUI), "Awake")]
    [HarmonyPostfix]
    public static void Awake(ref GenerateDAZMorphsControlUI __instance)
    {
        var Options = VaMPerformancePlugin.Options;
        __instance.onlyShowActive = Options.Character_onlyShowActive.Value;
        __instance.onlyShowFavorites = Options.Character_onlyShowFavorites.Value;
        __instance.onlyShowLatest = Options.Character_onlyShowLatest.Value;
        VaMPerformancePlugin.PluginLogger.LogDebug("Successfully finished patched Awake() for GenerateDAZMorphsControlUI");
    }
    
    [HarmonyPatch]
    [HarmonyFinalizer]
    public static Exception Finalizer(Exception __exception)
    {
        return new PluginException("GenerateDAZMorphsControlUIPatch had an exception", __exception);
    }
}