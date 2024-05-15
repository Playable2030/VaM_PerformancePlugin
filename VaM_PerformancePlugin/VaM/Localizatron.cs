using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace VaM_PerformancePlugin.VaM;

// TODO this seems to only be used in the patcher
[SuppressMessage("ReSharper", "InconsistentNaming")]
public class LocalizatronPatch
{
    private static bool Language_IsMatch(string language)
    {
        if (language.Length != 5 || language[2] != '_')
        {
            return false;
        }

        for (int i = 0; i < 2; i++)
        {
            char c = language[i];
            if (!('a' <= c && c <= 'z'))
                return false;
        }

        for (int i = 3; i < 5; i++)
        {
            char c = language[i];
            if (!('A' <= c && c <= 'Z'))
                return false;
        }

        return true;
    }

    [HarmonyPatch(typeof(Localizatron), nameof(Localizatron.SetLanguage))]
    [HarmonyPrefix]
    public static bool SetLanguage(ref Localizatron __instance, ref bool __result, string language,
        ref string ____currentLanguage,
        ref string ____languagePath, ref Dictionary<string, string> ___languageTable)
    {
        // unrolled regex
        // "^[a-z]{2}_[A-Z]{2}$"
        if (!Language_IsMatch(language))
        {
            __result = false;
            return false;
        }

        ____currentLanguage = language;
        ____languagePath = ____currentLanguage;
        ___languageTable = __instance.loadLanguageTable(____languagePath);
        Debug.Log(new StringBuilder().Append("[Localizatron] Locale loaded at: ")
            .Append(____languagePath)
            .ToString());

        return false;
    }
}