using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using MVR.FileManagement;
using MVR.FileManagementSecure;
using UnityEngine;

namespace VaM_PerformancePlugin.VaM.FileManagement;

/// <summary>
/// Replaces functions on existing <see cref="FileManager"/> implementation to improve performance:
/// <list type="bullet">
/// <item>Removes uses of Regex when possible with more performant string manipulation</item>
/// <item>TODO</item>
/// </list>
/// </summary>
[SuppressMessage("ReSharper", "InconsistentNaming")]
public class FileManagerPatch
{
    // quick and dirty re-implementation of newer dotnet stdlibs
    //

    // see https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.enumeratefiles?view=net-8.0
    public static IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.GetFiles(path, searchPattern, searchOption);
    }

    // see https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.enumeratedirectories?view=net-8.0
    public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.GetDirectories(path, searchPattern, searchOption);
    }

    //
    // end re-implementation

    private const string FilePrefix = "file:///";

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetFullPath))]
    [HarmonyPrefix]
    public static bool GetFullPath(ref string __result, string path)
    {
        __result = path;
        if (path.StartsWith(FilePrefix))
        {
            __result = __result.Substring(FilePrefix.Length - 1, __result.Length);
        }

        __result = Path.GetFullPath(__result);
        return false;
    }

    private static readonly Regex GetSuffixRegex = new(".*/", RegexOptions.Compiled);

    [HarmonyPatch(typeof(FileManager),
        // nameof(FileManager.packagePathToUid)
        "packagePathToUid"
    )]
    [HarmonyPrefix]
    public static bool packagePathToUid(ref string __result, string vpath)
    {
        __result = vpath.Replace('\\', '/');
        if (__result.EndsWith(".var") || __result.EndsWith(".zip"))
        {
            // 4 = # of chars for all ext
            __result = __result.Substring(0, __result.Length - 4);
        }

        // find last `/` char and trim it and everything before it
        __result = GetSuffixRegex.Replace(__result, string.Empty);

        // TODO why does this not work?
        // int charPos = -1;
        // const char targetChar = '/';
        // for (var i = __result.Length - 1; i >= 0; i--)
        // {
        //     if (targetChar == __result[i])
        //     {
        //         charPos = i;
        //         break;
        //     }
        // }
        //
        // if (charPos != -1)
        // {
        //     // +1 to exclude `targetChar` as well
        //     __result = __result.Substring(charPos + 1, __result.Length);
        // }

        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetVarFileEntry))]
    [HarmonyPrefix]
    public static bool GetVarFileEntry(ref Dictionary<string, VarFileEntry> ___uidToVarFileEntry,
        ref Dictionary<string, VarFileEntry> ___pathToVarFileEntry, ref VarFileEntry __result,
        string path)
    {
        __result = null;
        if (path == null)
        {
            return false;
        }

        string key = FileManager.CleanFilePath(path);
        if (key == null)
        {
            return false;
        }

        if (___uidToVarFileEntry == null)
        {
            return false;
        }

        VarFileEntry varFileEntry;
        if (___uidToVarFileEntry.TryGetValue(key, out varFileEntry))
        {
            __result = varFileEntry;
            return false;
        }

        if (___pathToVarFileEntry == null)
        {
            return false;
        }

        if (___pathToVarFileEntry.TryGetValue(key, out varFileEntry))
        {
            __result = varFileEntry;
            return false;
        }

        // do not run original method
        return false;
    }

    private static readonly MethodInfo RegisterPackageMethodInfo =
        typeof(FileManager).GetMethod("RegisterPackage", BindingFlags.NonPublic)!;


    // TODO why doesn't this patch seem to apply?
    [HarmonyPatch(typeof(FileManager), nameof(FileManager.Refresh))]
    [HarmonyPrefix]
    public static bool Refresh(ref FileManager __instance,
        ref HashSet<VarPackage> ___enabledPackages,
        ref HashSet<VarFileEntry> ___allVarFileEntries,
        ref HashSet<VarDirectoryEntry> ___allVarDirectoryEntries,
        ref Dictionary<string, VarPackage> ___packagesByPath,
        ref Dictionary<string, VarPackage> ___packagesByUid,
        ref Dictionary<string, VarPackageGroup> ___packageGroups,
        ref Dictionary<string, VarFileEntry> ___uidToVarFileEntry,
        ref Dictionary<string, VarFileEntry> ___pathToVarFileEntry,
        ref Dictionary<string, VarDirectoryEntry> ___uidToVarDirectoryEntry,
        ref Dictionary<string, VarDirectoryEntry> ___pathToVarDirectoryEntry,
        ref Dictionary<string, VarDirectoryEntry> ___varPackagePathToRootVarDirectory,
        ref string ___packageFolder,
        ref string ___userPrefsFolder,
        ref OnRefresh ___onRefreshHandlers,
        ref bool ___packagesEnabled,
        ref HashSet<string> ___restrictedReadPaths,
        ref HashSet<string> ___secureReadPaths,
        ref HashSet<string> ___secureInternalWritePaths,
        ref HashSet<string> ___securePluginWritePaths,
        ref HashSet<string> ___pluginWritePathsThatDoNotNeedConfirm,
        ref Transform ___userConfirmContainer,
        ref Transform ___userConfirmPrefab,
        ref Transform ___userConfirmPluginActionPref,
        ref Dictionary<string, string> ___pluginHashToPluginPath,
        ref AsyncFlag ___userConfirmFlag,
        ref HashSet<UserConfirmPanel> ___activeUserConfirmPanels,
        ref HashSet<string> ___userConfirmedPlugins,
        ref HashSet<string> ___userDeniedPlugins,
        ref LinkedList<string> ___loadDirStack,
        ref DateTime ___lastPackageRefreshTime
    )
    {
        Debug.Log("[VaMPerformancePatch] Patched FileManager.Refresh() running...");
        if (FileManager.debug)
        {
            Debug.Log("FileManager Refresh()");
        }

        // TODO move init style/ensure style code out of here and into a constructor/init
        ___packagesByUid ??= new();
        ___packagesByPath ??= new();
        ___packageGroups ??= new();
        ___enabledPackages ??= new();
        ___allVarFileEntries ??= new();
        ___allVarDirectoryEntries ??= new();
        ___uidToVarFileEntry ??= new();
        ___pathToVarFileEntry ??= new();
        ___uidToVarDirectoryEntry ??= new();
        ___pathToVarDirectoryEntry ??= new();
        ___varPackagePathToRootVarDirectory ??= new();

        bool packagesChanged = false;
        float startMillis = 0.0f;

        if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
        {
            startMillis = GlobalStopwatch.GetElapsedMilliseconds();
        }

        try
        {
            if (!Directory.Exists(___packageFolder))
            {
                FileManager.CreateDirectory(___packageFolder);
            }

            if (!Directory.Exists(___userPrefsFolder))
            {
                FileManager.CreateDirectory(___userPrefsFolder);
            }

            if (Directory.Exists(___packageFolder))
            {
                IEnumerable<string> directories = null;
                IEnumerable<string> files = null;
                if (___packagesEnabled)
                {
                    // TODO why both?
                    directories = EnumerateDirectories(___packageFolder, "*.var", SearchOption.AllDirectories);
                    files = EnumerateFiles(___packageFolder, "*.var", SearchOption.AllDirectories);
                }
                else if (FileManager.demoPackagePrefixes != null)
                {
                    IEnumerable<string> result = new List<string>();
                    foreach (string demoPackagePrefix in FileManager.demoPackagePrefixes)
                    {
                        IEnumerable<string> enumerateFiles = EnumerateFiles(___packageFolder,
                            demoPackagePrefix + "*.var", SearchOption.AllDirectories);
                        result = result.Concat(enumerateFiles);
                    }

                    files = result;
                }

                HashSet<string> packagesToRegister = new();
                HashSet<string> packagesToUnregister = new();

                foreach (string directory in directories)
                {
                    packagesToRegister.Add(directory);
                    VarPackage vp;
                    if (___packagesByPath.TryGetValue(directory, out vp))
                    {
                        bool previouslyEnabled = ___enabledPackages.Contains(vp);
                        bool enabled = vp.Enabled;
                        if (!previouslyEnabled && enabled || previouslyEnabled && !enabled || !vp.IsSimulated)
                        {
                            FileManager.UnregisterPackage(vp);
                            packagesToUnregister.Add(directory);
                        }
                    }
                    else
                    {
                        packagesToUnregister.Add(directory);
                    }
                }

                if (files != null)
                {
                    foreach (string file in files)
                    {
                        packagesToRegister.Add(file);
                        VarPackage vp;
                        if (___packagesByPath.TryGetValue(file, out vp))
                        {
                            bool flag3 = ___enabledPackages.Contains(vp);
                            bool enabled = vp.Enabled;
                            if (!flag3 && enabled || flag3 && !enabled || vp.IsSimulated)
                            {
                                FileManager.UnregisterPackage(vp);
                                packagesToUnregister.Add(file);
                            }
                        }
                        else
                        {
                            packagesToUnregister.Add(file);
                        }
                    }
                }

                HashSet<VarPackage> varPackageSet = new();
                foreach (VarPackage varPackage in ___packagesByUid.Values)
                {
                    if (!packagesToRegister.Contains(varPackage.Path))
                        varPackageSet.Add(varPackage);
                }

                foreach (VarPackage vp in varPackageSet)
                {
                    FileManager.UnregisterPackage(vp);
                    packagesChanged = true;
                }

                foreach (string vpath in packagesToUnregister)
                {
                    RegisterPackageMethodInfo.Invoke(null, [vpath]);
                    packagesChanged = true;
                }

                if (packagesChanged)
                {
                    foreach (VarPackage varPackage in ___packagesByUid.Values)
                    {
                        varPackage.LoadMetaData();
                    }

                    foreach (VarPackageGroup varPackageGroup in ___packageGroups.Values)
                    {
                        varPackageGroup.Init();
                    }
                }

                if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
                {
                    float elapsedMilliseconds = GlobalStopwatch.GetElapsedMilliseconds();
                    float packageScanningDurationMillis = elapsedMilliseconds - startMillis;
                    Debug.Log(new StringBuilder().Append("Scanned ")
                        .Append(___packagesByUid.Count)
                        .Append(" packages in ")
                        .Append(packageScanningDurationMillis.ToString("F1"))
                        .Append(" ms")
                        .ToString());
                    startMillis = elapsedMilliseconds;
                }

                foreach (VarPackage varPackage in ___packagesByUid.Values)
                {
                    if (varPackage.forceRefresh)
                    {
                        Debug.Log("Force refresh of package " + varPackage.Uid);
                        packagesChanged = true;
                        varPackage.forceRefresh = false;
                    }
                }

                if (packagesChanged)
                {
                    Debug.Log("Package changes detected");
                    ___onRefreshHandlers?.Invoke();
                }
                else
                {
                    Debug.Log("No package changes detected");
                }

                if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
                {
                    float elapsedMilliseconds2 = GlobalStopwatch.GetElapsedMilliseconds();
                    Debug.Log(new StringBuilder().Append("Refresh Handlers took ")
                        .Append((elapsedMilliseconds2 - startMillis).ToString("F1"))
                        .Append(" ms")
                        .ToString());
                    startMillis = elapsedMilliseconds2;
                }
            }
        }
        catch (Exception ex)
        {
            SuperController.LogError(new StringBuilder().Append("Exception during package refresh ")
                .Append(ex)
                .ToString());
        }

        if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
        {
            Debug.Log(new StringBuilder().Append("Refresh package handlers took ")
                .Append((GlobalStopwatch.GetElapsedMilliseconds() - startMillis).ToString("F1"))
                .Append(" ms")
                .ToString());
        }

        ___lastPackageRefreshTime = DateTime.Now;
        return false;
    }
}