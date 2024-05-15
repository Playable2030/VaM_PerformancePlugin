using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using MVR.FileManagement;
using MVR.FileManagementSecure;
using UnityEngine;

namespace VaM_PerformancePlugin.VaM.FileManagement;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class FileManagerStatics
{
    internal static readonly Traverse fileManagerTraverse = Traverse.Create<FileManager>();

    internal static readonly HashSet<VarPackage> enabledPackages = new();
    internal static readonly HashSet<VarFileEntry> allVarFileEntries = new();
    internal static readonly HashSet<VarDirectoryEntry> allVarDirectoryEntries = new();
    internal static readonly Dictionary<string, VarPackage> packagesByPath = new();
    internal static readonly Dictionary<string, VarPackage> packagesByUid = new();
    internal static readonly Dictionary<string, VarPackageGroup> packageGroups = new();
    internal static readonly Dictionary<string, VarFileEntry> uidToVarFileEntry = new();
    internal static readonly Dictionary<string, VarFileEntry> pathToVarFileEntry = new();
    internal static readonly Dictionary<string, VarDirectoryEntry> uidToVarDirectoryEntry = new();
    internal static readonly Dictionary<string, VarDirectoryEntry> pathToVarDirectoryEntry = new();
    internal static readonly Dictionary<string, VarDirectoryEntry> varPackagePathToRootVarDirectory = new();

    // TODO why do these fail with nulls in patched methods? this is a hack that may not be good long term...
    internal static string packageFolder =>
        fileManagerTraverse.Property<string>("packageFolder").Value ?? "AddonPackages";
    internal static string userPrefsFolder =>
        fileManagerTraverse.Property<string>("userPrefsFolder").Value ?? "AddonPackagesUserPrefs";
    internal static OnRefresh onRefreshHandlers => fileManagerTraverse.Property<OnRefresh>("onRefreshHandlers").Value;

    private static readonly Traverse RegisterPackageMethod =
        fileManagerTraverse.Method("RegisterPackage", [typeof(string)]);
    internal static void RegisterPackage(string vpath) => RegisterPackageMethod.GetValue([vpath]);

    internal static DateTime lastPackageRefreshTime
    {
        get => FileManager.lastPackageRefreshTime;
        set => fileManagerTraverse.Property<DateTime>("lastPackageRefreshTime").Value = value;
    }

    internal static readonly bool packagesEnabled = new();
    internal static readonly HashSet<string> restrictedReadPaths = new();
    internal static readonly HashSet<string> secureReadPaths = new();
    internal static readonly HashSet<string> secureInternalWritePaths = new();
    internal static readonly HashSet<string> securePluginWritePaths = new();
    internal static readonly HashSet<string> pluginWritePathsThatDoNotNeedConfirm = new();
    internal static readonly Dictionary<string, string> pluginHashToPluginPath = new();
    internal static readonly HashSet<string> userConfirmedPlugins = new();
    internal static readonly HashSet<string> userDeniedPlugins = new();
    internal static readonly LinkedList<string> loadDirStack = new();

    // internal static void Init()
    static FileManagerStatics()
    {
        // Link these instances in memory to the actual FileManager static fields
        fileManagerTraverse.Property<HashSet<VarPackage>>("enabledPackages").Value = enabledPackages;
        fileManagerTraverse.Property<HashSet<VarFileEntry>>("allVarFileEntries").Value = allVarFileEntries;
        fileManagerTraverse.Property<HashSet<VarDirectoryEntry>>("allVarDirectoryEntries").Value =
            allVarDirectoryEntries;
        fileManagerTraverse.Property<Dictionary<string, VarPackage>>("packagesByPath").Value = packagesByPath;
        fileManagerTraverse.Property<Dictionary<string, VarPackage>>("packagesByUid").Value = packagesByUid;
        fileManagerTraverse.Property<Dictionary<string, VarPackageGroup>>("packageGroups").Value = packageGroups;
        fileManagerTraverse.Property<Dictionary<string, VarFileEntry>>("uidToVarFileEntry").Value = uidToVarFileEntry;
        fileManagerTraverse.Property<Dictionary<string, VarFileEntry>>("pathToVarFileEntry").Value = pathToVarFileEntry;
        fileManagerTraverse.Property<Dictionary<string, VarDirectoryEntry>>("uidToVarDirectoryEntry").Value =
            uidToVarDirectoryEntry;
        fileManagerTraverse.Property<Dictionary<string, VarDirectoryEntry>>("pathToVarDirectoryEntry").Value =
            pathToVarDirectoryEntry;
        fileManagerTraverse.Property<Dictionary<string, VarDirectoryEntry>>("varPackagePathToRootVarDirectory").Value =
            varPackagePathToRootVarDirectory;

        // since these are initialized by default, get their references instead of giving them references
        // packageFolder = fileManagerTraverse.Field<string>("packageFolder").Value;
        // userPrefsFolder = fileManagerTraverse.Property<string>("userPrefsFolder").Value;
        // onRefreshHandlers = fileManagerTraverse.Property<OnRefresh>("onRefreshHandlers").Value;

        fileManagerTraverse.Property<bool>("packagesEnabled").Value = packagesEnabled;
        fileManagerTraverse.Property<HashSet<string>>("restrictedReadPaths").Value = restrictedReadPaths;
        fileManagerTraverse.Property<HashSet<string>>("secureReadPaths").Value = secureReadPaths;
        fileManagerTraverse.Property<HashSet<string>>("secureInternalWritePaths").Value = secureInternalWritePaths;
        fileManagerTraverse.Property<HashSet<string>>("securePluginWritePaths").Value = securePluginWritePaths;
        fileManagerTraverse.Property<HashSet<string>>("pluginWritePathsThatDoNotNeedConfirm").Value =
            pluginWritePathsThatDoNotNeedConfirm;
        fileManagerTraverse.Property<Dictionary<string, string>>("pluginHashToPluginPath").Value =
            pluginHashToPluginPath;
        fileManagerTraverse.Property<HashSet<string>>("userConfirmedPlugins").Value = userConfirmedPlugins;
        fileManagerTraverse.Property<HashSet<string>>("userDeniedPlugins").Value = userDeniedPlugins;
        fileManagerTraverse.Property<LinkedList<string>>("loadDirStack").Value = loadDirStack;
    }
}

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

    // [HarmonyPatch(typeof(FileManager), MettMethodType.Constructor)]
    // [HarmonyPostfix]
    // private static void CTOR()
    // {
    //     FileManagerStatics.Init();
    // }

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

    // private static readonly Regex GetSuffixRegex = new(".*/", RegexOptions.Compiled);

    [HarmonyPatch(typeof(FileManager),
        // nameof(FileManager.packagePathToUid)
        "packagePathToUid"
    )]
    [HarmonyPrefix]
    public static bool packagePathToUid(ref string __result, string vpath)
    {
        VaMPerformancePlugin.PluginLogger.LogDebug($"FileManager.packagePathToUid running for <{vpath}>");

        // Remove ".var" or ".zip" if it exists at the end of the string
        __result = vpath.Replace('\\', '/');
        if (__result.EndsWith(".var") || __result.EndsWith(".zip"))
        {
            // 4 = # of chars for all ext
            __result = __result.Remove(__result.Length - 4);
        }

        // Get the last part of the path by finding the last '/' and getting the substring after it
        int lastSlashIndex = __result.LastIndexOf('/');
        if (lastSlashIndex != -1)
        {
            __result = __result.Substring(lastSlashIndex + 1);
        }

        VaMPerformancePlugin.PluginLogger.LogDebug($"Actually returning <{__result}>");
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

        if (___uidToVarFileEntry.TryGetValue(key, out var varFileEntry))
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


    // TODO why does this not work? Looks like it isn't working because of the statics not being correctly inited...
    // [HarmonyPatch(typeof(FileManager), nameof(FileManager.Refresh))]
    // [HarmonyPrefix]
    public static bool Refresh()
    {
        VaMPerformancePlugin.PluginLogger.LogDebug("Patched FileManager.Refresh() running...");
        if (FileManager.debug)
        {
            Debug.Log("FileManager Refresh()");
        }
        
        // TODO can we pull this out to avoid re-inits?
        // FileManagerStatics.Init();
        
        // Pull out the static fields we need
        var packagesByUid = FileManagerStatics.packagesByUid;
        var packagesByPath = FileManagerStatics.packagesByPath;
        var packageGroups = FileManagerStatics.packageGroups;
        var enabledPackages = FileManagerStatics.enabledPackages;

        var packageFolder = FileManagerStatics.packageFolder;
        var userPrefsFolder = FileManagerStatics.userPrefsFolder;
        var packagesEnabled = FileManagerStatics.packagesEnabled;
        var onRefreshHandlers = FileManagerStatics.onRefreshHandlers;

        bool packagesChanged = false;
        float startMillis = 0.0f;

        if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
        {
            startMillis = GlobalStopwatch.GetElapsedMilliseconds();
        }

        try
        {
            if (!Directory.Exists(packageFolder))
            {
                FileManager.CreateDirectory(packageFolder);
            }

            if (!Directory.Exists(userPrefsFolder))
            {
                FileManager.CreateDirectory(userPrefsFolder);
            }

            if (Directory.Exists(packageFolder))
            {
                IEnumerable<string> directories = [];
                IEnumerable<string> files = [];
                if (packagesEnabled)
                {
                    // TODO why both?
                    directories = EnumerateDirectories(packageFolder, "*.var", SearchOption.AllDirectories);
                    files = EnumerateFiles(packageFolder, "*.var", SearchOption.AllDirectories);
                }
                else if (FileManager.demoPackagePrefixes != null)
                {
                    IEnumerable<string> result = new List<string>();
                    foreach (string demoPackagePrefix in FileManager.demoPackagePrefixes)
                    {
                        IEnumerable<string> enumerateFiles = EnumerateFiles(packageFolder,
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
                    if (packagesByPath.TryGetValue(directory, out vp))
                    {
                        bool previouslyEnabled = enabledPackages.Contains(vp);
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
                        if (packagesByPath.TryGetValue(file, out vp))
                        {
                            bool flag3 = enabledPackages.Contains(vp);
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
                foreach (VarPackage varPackage in packagesByUid.Values)
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
                    FileManagerStatics.RegisterPackage(vpath);
                    // FileManagerStatics.RegisterPackageMethodInfo.Invoke(null, [vpath]);
                    packagesChanged = true;
                }

                if (packagesChanged)
                {
                    foreach (VarPackage varPackage in packagesByUid.Values)
                    {
                        varPackage.LoadMetaData();
                    }

                    foreach (VarPackageGroup varPackageGroup in packageGroups.Values)
                    {
                        varPackageGroup.Init();
                    }
                }

                if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
                {
                    float elapsedMilliseconds = GlobalStopwatch.GetElapsedMilliseconds();
                    float packageScanningDurationMillis = elapsedMilliseconds - startMillis;
                    Debug.Log(new StringBuilder().Append("Scanned ")
                        .Append(packagesByUid.Count)
                        .Append(" packages in ")
                        .Append(packageScanningDurationMillis.ToString("F1"))
                        .Append(" ms")
                        .ToString());
                    startMillis = elapsedMilliseconds;
                }

                foreach (VarPackage varPackage in packagesByUid.Values)
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
                    onRefreshHandlers?.Invoke();
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

        FileManagerStatics.lastPackageRefreshTime = DateTime.Now;
        return false;
    }
}