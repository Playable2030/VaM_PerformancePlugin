using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using VaM_PerformancePlugin.Inlined;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace VaM_PerformancePlugin.VaM.FileManagement;

// Rewrite of FileManager to be "lazier":
// - Avoids using a singleton in favor of static variables
// - Avoid a full refresh when possible
// - Use lazy File I/O and watchers that run on separate threads (if possible)
// - Serialize caches on shutdown and load them on startup
// Note: This is avoiding using the Harmony annotations/code here, and keeping that separate in an "ugly" "glue" class
public static class LazyFileManager
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

    // TODO is there a way to keep this in sync with normal VaM code?
    public const string packageFolder = "AddonPackages";
    public const string userPrefsFolder = "AddonPackagesUserPrefs";

    public static Dictionary<string, VarPackage> packagesByUid = new();
    public static HashSet<VarPackage> enabledPackages = [];
    public static Dictionary<string, VarPackage> packagesByPath = new();
    public static Dictionary<string, VarPackageGroup> packageGroups = new();
    public static HashSet<VarFileEntry> allVarFileEntries = [];
    public static HashSet<VarDirectoryEntry> allVarDirectoryEntries = [];
    public static Dictionary<string, VarFileEntry> uidToVarFileEntry = new();
    public static Dictionary<string, VarFileEntry> pathToVarFileEntry = new();
    public static Dictionary<string, VarDirectoryEntry> uidToVarDirectoryEntry = new();
    public static Dictionary<string, VarDirectoryEntry> pathToVarDirectoryEntry = new();
    public static Dictionary<string, VarDirectoryEntry> varPackagePathToRootVarDirectory = new();

    public static OnRefresh onRefreshHandlers;
    public static bool packagesEnabled = true;

    public static string[] demoPackagePrefixes = [];

    // protected static bool packagesEnabled = true;
    public static HashSet<string> restrictedReadPaths = [];
    public static HashSet<string> secureReadPaths = [];
    public static HashSet<string> secureInternalWritePaths = [];
    public static HashSet<string> securePluginWritePaths = [];
    public static HashSet<string> pluginWritePathsThatDoNotNeedConfirm = [];
    public static Transform userConfirmContainer;
    public static Transform userConfirmPrefab;
    public static Transform userConfirmPluginActionPrefab;
    public static Dictionary<string, string> pluginHashToPluginPath = new();
    public static AsyncFlag userConfirmFlag;
    public static HashSet<UserConfirmPanel> activeUserConfirmPanels = [];
    public static HashSet<string> userConfirmedPlugins = [];
    public static HashSet<string> userDeniedPlugins = [];
    public static LinkedList<string> loadDirStack = [];

    public static string PackagePathToUid(string vpath)
    {
        var result = vpath;
        // Remove ".var" or ".zip" if it exists at the end of the string
        result = result.Replace('\\', '/');
        if (result.EndsWith(".var") || result.EndsWith(".zip"))
        {
            // 4 = # of chars for all ext
            result = result.Remove(result.Length - 4);
        }

        // Get the last part of the path by finding the last '/' and getting the substring after it
        var lastSlashIndex = result.LastIndexOf('/');
        if (lastSlashIndex != -1)
        {
            result = result.Substring(lastSlashIndex + 1);
        }

        return result;
    }

    public static VarPackage RegisterPackage(string vpath)
    {
        // if (LazyFileManager.debug)
        // {
        //     Debug.Log("RegisterPackage " + vpath);
        // }

        var uid = PackagePathToUid(vpath);
        var strArray = uid.Split('.');
        if (strArray.Length == 3)
        {
            var creator = strArray[0];
            var name = strArray[1];
            var str = creator + "." + name;
            var s = strArray[2];
            try
            {
                var version = int.Parse(s);
                if (packagesByUid.ContainsKey(uid))
                {
                    SuperController.LogError("Duplicate package uid " + uid + ". Cannot register");
                }
                else
                {
                    if (!packageGroups.TryGetValue(str, out var group))
                    {
                        group = new VarPackageGroup(str);
                        packageGroups.Add(str, group);
                    }

                    var vp = new VarPackage(uid, vpath, group, creator, name, version);
                    packagesByUid.Add(uid, vp);
                    packagesByPath.Add(vp.Path, vp);
                    packagesByPath.Add(vp.SlashPath, vp);
                    packagesByPath.Add(vp.FullPath, vp);
                    packagesByPath.Add(vp.FullSlashPath, vp);
                    group.AddPackage(vp);
                    if (vp.Enabled)
                    {
                        enabledPackages.Add(vp);
                        foreach (var fileEntry in vp.FileEntries)
                        {
                            allVarFileEntries.Add(fileEntry);
                            uidToVarFileEntry.Add(fileEntry.Uid, fileEntry);
                            // if (LazyFileManager.debug)
                            // {
                            //     Debug.Log("Add var file with UID " + fileEntry.Uid);
                            // }

                            pathToVarFileEntry.Add(fileEntry.Path, fileEntry);
                            pathToVarFileEntry.Add(fileEntry.SlashPath, fileEntry);
                            pathToVarFileEntry.Add(fileEntry.FullPath, fileEntry);
                            pathToVarFileEntry.Add(fileEntry.FullSlashPath, fileEntry);
                        }

                        foreach (var directoryEntry in vp.DirectoryEntries)
                        {
                            allVarDirectoryEntries.Add(directoryEntry);
                            // if (LazyFileManager.debug)
                            // {
                            //     Debug.Log("Add var directory with UID " + directoryEntry.Uid);
                            // }

                            uidToVarDirectoryEntry.Add(directoryEntry.Uid, directoryEntry);
                            pathToVarDirectoryEntry.Add(directoryEntry.Path, directoryEntry);
                            pathToVarDirectoryEntry.Add(directoryEntry.SlashPath, directoryEntry);
                            pathToVarDirectoryEntry.Add(directoryEntry.FullPath, directoryEntry);
                            pathToVarDirectoryEntry.Add(directoryEntry.FullSlashPath, directoryEntry);
                        }

                        varPackagePathToRootVarDirectory.Add(vp.Path, vp.RootDirectory);
                        varPackagePathToRootVarDirectory.Add(vp.FullPath, vp.RootDirectory);
                    }

                    return vp;
                }
            }
            catch (FormatException)
            {
                SuperController.LogError("VAR file " + vpath +
                                         " does not use integer version field in name <creator>.<name>.<version>");
            }
        }
        else
        {
            SuperController.LogError("VAR file " + vpath +
                                     " is not named with convention <creator>.<name>.<version>");
        }

        return null;
    }

    public static void UnregisterPackage(VarPackage vp)
    {
        if (vp == null)
            return;
        vp.Group?.RemovePackage(vp);
        packagesByUid.Remove(vp.Uid);
        packagesByPath.Remove(vp.Path);
        packagesByPath.Remove(vp.SlashPath);
        packagesByPath.Remove(vp.FullPath);
        packagesByPath.Remove(vp.FullSlashPath);
        enabledPackages.Remove(vp);
        foreach (var fileEntry in vp.FileEntries)
        {
            allVarFileEntries.Remove(fileEntry);
            uidToVarFileEntry.Remove(fileEntry.Uid);
            pathToVarFileEntry.Remove(fileEntry.Path);
            pathToVarFileEntry.Remove(fileEntry.SlashPath);
            pathToVarFileEntry.Remove(fileEntry.FullPath);
            pathToVarFileEntry.Remove(fileEntry.FullSlashPath);
        }

        foreach (var directoryEntry in vp.DirectoryEntries)
        {
            allVarDirectoryEntries.Remove(directoryEntry);
            uidToVarDirectoryEntry.Remove(directoryEntry.Uid);
            pathToVarDirectoryEntry.Remove(directoryEntry.Path);
            pathToVarDirectoryEntry.Remove(directoryEntry.SlashPath);
            pathToVarDirectoryEntry.Remove(directoryEntry.FullPath);
            pathToVarDirectoryEntry.Remove(directoryEntry.FullSlashPath);
        }

        varPackagePathToRootVarDirectory.Remove(vp.Path);
        varPackagePathToRootVarDirectory.Remove(vp.FullPath);
        vp.Dispose();
    }

    public static void SyncJSONCache()
    {
        foreach (var package in GetPackages())
        {
            package.SyncJSONCache();
        }
    }

    public static void RegisterRefreshHandler(OnRefresh refreshHandler)
    {
        onRefreshHandlers += refreshHandler;
    }

    public static void UnregisterRefreshHandler(OnRefresh refreshHandler)
    {
        onRefreshHandlers -= refreshHandler;
    }

    public static DateTime LastPackageRefreshTime = DateTime.MinValue;

    public static void ClearAll()
    {
        foreach (var varPackage in packagesByUid.Values)
        {
            varPackage.Dispose();
        }

        packagesByUid?.Clear();
        packagesByPath?.Clear();
        packageGroups?.Clear();
        enabledPackages?.Clear();
        allVarFileEntries?.Clear();
        allVarDirectoryEntries?.Clear();
        uidToVarFileEntry?.Clear();
        pathToVarFileEntry?.Clear();
        uidToVarDirectoryEntry?.Clear();
        pathToVarDirectoryEntry?.Clear();
        varPackagePathToRootVarDirectory?.Clear();
    }

    public static void Refresh()
    {
        // VaMPerformancePlugin.PluginLogger.LogDebug("Patched LazyFileManager.Refresh() running...");

        var packagesChanged = false;
        var startMillis = 0.0f;

        if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
        {
            startMillis = GlobalStopwatch.GetElapsedMilliseconds();
        }

        try
        {
            if (!Directory.Exists(packageFolder))
            {
                CreateDirectory(packageFolder);
            }

            if (!Directory.Exists(userPrefsFolder))
            {
                CreateDirectory(userPrefsFolder);
            }

            if (Directory.Exists(packageFolder))
            {
                IEnumerable<string> directories = [];
                IEnumerable<string> files = [];
                if (packagesEnabled)
                {
                    // TODO why both?
                    directories = Directory.GetDirectories(packageFolder, "*.var",
                        SearchOption.AllDirectories);
                    files = Directory.GetFiles(packageFolder, "*.var", SearchOption.AllDirectories);
                    // directories = EnumerateDirectories(packageFolder, "*.var", SearchOption.AllDirectories);
                    // files = EnumerateFiles(packageFolder, "*.var", SearchOption.AllDirectories);
                }
                else if (demoPackagePrefixes != null)
                {
                    List<string> result = [];
                    // IEnumerable<string> result = new List<string>();
                    foreach (var demoPackagePrefix in demoPackagePrefixes)
                    {
                        foreach (var file in Directory.GetFiles(packageFolder,
                                     demoPackagePrefix + "*.var", SearchOption.AllDirectories))
                        {
                            result.Add(file);
                        }
                        // IEnumerable<string> enumerateFiles = EnumerateFiles(packageFolder,
                        //     demoPackagePrefix + "*.var", SearchOption.AllDirectories);
                        // result = result.Concat(enumerateFiles);
                    }

                    files = result.ToArray();
                }

                HashSet<string> registeredPacakges = [];
                HashSet<string> unregisteredPackages = [];

                foreach (var directory in directories)
                {
                    registeredPacakges.Add(directory);
                    if (packagesByPath.TryGetValue(directory, out var vp))
                    {
                        var previouslyEnabled = enabledPackages.Contains(vp);
                        var enabled = vp.Enabled;
                        if ((previouslyEnabled || !enabled) && (!previouslyEnabled || enabled) && vp.IsSimulated)
                            continue;
                        UnregisterPackage(vp);
                        unregisteredPackages.Add(directory);
                    }
                    else
                    {
                        unregisteredPackages.Add(directory);
                    }
                }

                if (files != null)
                {
                    foreach (var file in files)
                    {
                        registeredPacakges.Add(file);
                        if (packagesByPath.TryGetValue(file, out var vp))
                        {
                            var inEnabledPackages = enabledPackages.Contains(vp);
                            var enabled = vp.Enabled;
                            if ((inEnabledPackages || !enabled) && (!inEnabledPackages || enabled) && !vp.IsSimulated)
                                continue;
                            UnregisterPackage(vp);
                            unregisteredPackages.Add(file);
                        }
                        else
                        {
                            unregisteredPackages.Add(file);
                        }
                    }
                }

                HashSet<VarPackage> packagesToRemove = [];
                foreach (var varPackage in packagesByUid.Values)
                {
                    if (!registeredPacakges.Contains(varPackage.Path))
                    {
                        packagesToRemove.Add(varPackage);
                    }
                }

                foreach (var vp in packagesToRemove)
                {
                    // VaMPerformancePlugin.PluginLogger.LogDebug($"Unregistering package: {vp}");
                    UnregisterPackage(vp);
                    packagesChanged = true;
                }

                foreach (var vpath in unregisteredPackages)
                {
                    // VaMPerformancePlugin.PluginLogger.LogDebug($"Registering package: {vpath}");
                    RegisterPackage(vpath);
                    packagesChanged = true;
                }

                if (packagesChanged)
                {
                    foreach (var varPackage in packagesByUid.Values)
                    {
                        varPackage.LoadMetaData();
                    }

                    foreach (var varPackageGroup in packageGroups.Values)
                    {
                        varPackageGroup.Init();
                    }
                }

                if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
                {
                    var elapsedMilliseconds = GlobalStopwatch.GetElapsedMilliseconds();
                    var packageScanningDurationMillis = elapsedMilliseconds - startMillis;
                    Debug.Log(new StringBuilder().Append("Scanned ")
                        .Append(packagesByUid.Count)
                        .Append(" packages in ")
                        .Append(packageScanningDurationMillis.ToString("F1"))
                        .Append(" ms")
                        .ToString());
                    startMillis = elapsedMilliseconds;
                }

                foreach (var varPackage in packagesByUid.Values)
                {
                    if (!varPackage.forceRefresh) continue;
                    Debug.Log("Force refresh of package " + varPackage.Uid);
                    packagesChanged = true;
                    varPackage.forceRefresh = false;
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
                    var elapsedMilliseconds2 = GlobalStopwatch.GetElapsedMilliseconds();
                    Debug.Log(new StringBuilder().Append("Refresh Handlers took ")
                        .Append((elapsedMilliseconds2 - startMillis).ToString("F1"))
                        .Append(" ms")
                        .ToString());
                    startMillis = elapsedMilliseconds2;
                }
            }
        }
        catch (TargetInvocationException ex)
        {
            VaMPerformancePlugin.PluginLogger.LogError(ex);
        }
        catch (Exception ex)
        {
            SuperController.LogError(new StringBuilder().AppendLine("Exception during package refresh ")
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

        LastPackageRefreshTime = DateTime.Now;
    }

    public static void RegisterRestrictedReadPath(string path)
    {
        restrictedReadPaths.Add(Path.GetFullPath(path));
    }

    public static void RegisterSecureReadPath(string path)
    {
        secureReadPaths.Add(Path.GetFullPath(path));
    }

    public static void ClearSecureReadPaths()
    {
        secureReadPaths.Clear();
    }

    public static bool IsSecureReadPath(string path)
    {
        var fullPath = GetFullPath(path);
        var flag1 = false;
        foreach (var restrictedReadPath in restrictedReadPaths)
        {
            if (fullPath.StartsWith(restrictedReadPath))
            {
                flag1 = true;
                break;
            }
        }

        var flag2 = false;
        if (flag1) return false;
        foreach (var secureReadPath in secureReadPaths)
        {
            if (!fullPath.StartsWith(secureReadPath)) continue;
            flag2 = true;
            break;
        }

        return flag2;
    }

    public static void ClearSecureWritePaths()
    {
        secureInternalWritePaths.Clear();
        securePluginWritePaths.Clear();
        pluginWritePathsThatDoNotNeedConfirm.Clear();
    }

    public static void RegisterInternalSecureWritePath(string path)
    {
        secureInternalWritePaths.Add(Path.GetFullPath(path));
    }

    public static void RegisterPluginSecureWritePath(string path, bool doesNotNeedConfirm)
    {
        var fullPath = Path.GetFullPath(path);
        securePluginWritePaths.Add(fullPath);
        if (!doesNotNeedConfirm)
            return;
        pluginWritePathsThatDoNotNeedConfirm.Add(fullPath);
    }

    public static bool IsSecureWritePath(string path)
    {
        var fullPath = GetFullPath(path);
        var flag = false;
        foreach (var internalWritePath in secureInternalWritePaths)
        {
            if (fullPath.StartsWith(internalWritePath))
            {
                flag = true;
                break;
            }
        }

        return flag;
    }

    public static bool IsSecurePluginWritePath(string path)
    {
        var fullPath = GetFullPath(path);
        var flag = false;
        foreach (var securePluginWritePath in securePluginWritePaths)
        {
            if (!fullPath.StartsWith(securePluginWritePath)) continue;
            flag = true;
            break;
        }

        return flag;
    }

    public static bool IsPluginWritePathThatNeedsConfirm(string path)
    {
        var fullPath = GetFullPath(path);
        var flag = true;
        foreach (var str in pluginWritePathsThatDoNotNeedConfirm)
        {
            if (!fullPath.StartsWith(str)) continue;
            flag = false;
            break;
        }

        return flag;
    }

    public static void RegisterPluginHashToPluginPath(string hash, string path)
    {
        pluginHashToPluginPath.Remove(hash);
        pluginHashToPluginPath.Add(hash, path);
    }

    public static string GetPluginHash()
    {
        var stackTrace = new StackTrace();
        string pluginHash = null;
        for (var index = 0; index < stackTrace.FrameCount; ++index)
        {
            var name = stackTrace.GetFrame(index).GetMethod().DeclaringType.Assembly.GetName().Name;
            if (name.StartsWith("MVRPlugin_"))
            {
                pluginHash = Regex.Replace(name, "_[0-9]+$", string.Empty);
                break;
            }
        }

        return pluginHash;
    }

    public static void AssertNotCalledFromPlugin()
    {
        var pluginHash = GetPluginHash();
        if (pluginHash != null)
            throw new Exception("Plugin with signature " + pluginHash + " tried to execute forbidden operation");
    }

    public static void DestroyUserConfirmPanel(UserConfirmPanel ucp)
    {
        Object.Destroy(ucp.gameObject);
        activeUserConfirmPanels.Remove(ucp);
        if (activeUserConfirmPanels.Count != 0 || userConfirmFlag == null)
            return;
        userConfirmFlag.Raise();
        userConfirmFlag = null;
    }

    public static void CreateUserConfirmFlag()
    {
        if (userConfirmFlag != null ||
            !(SuperController.singleton != null))
            return;
        userConfirmFlag = new AsyncFlag("UserConfirm");
        SuperController.singleton.SetLoadingIconFlag(userConfirmFlag);
        SuperController.singleton.PauseAutoSimulation(userConfirmFlag);
    }

    public static void DestroyAllUserConfirmPanels()
    {
        foreach (var ucp in new List<UserConfirmPanel>(
                     activeUserConfirmPanels))
            DestroyUserConfirmPanel(ucp);
    }

    public static void UserConfirm(
        string prompt,
        UserActionCallback confirmCallback,
        UserActionCallback autoConfirmCallback,
        UserActionCallback confirmStickyCallback,
        UserActionCallback denyCallback,
        UserActionCallback autoDenyCallback,
        UserActionCallback denyStickyCallback)
    {
        if (userConfirmContainer != null &&
            userConfirmPrefab != null)
        {
            if (activeUserConfirmPanels == null)
                activeUserConfirmPanels = [];
            CreateUserConfirmFlag();
            var transform = Object.Instantiate(userConfirmPrefab, userConfirmContainer, false);
            transform.SetAsFirstSibling();
            var ucp = transform.GetComponent<UserConfirmPanel>();
            if (ucp != null)
            {
                ucp.signature = prompt;
                ucp.SetPrompt(prompt);
                activeUserConfirmPanels.Add(ucp);
                ucp.SetConfirmCallback(() =>
                {
                    DestroyUserConfirmPanel(ucp);
                    if (confirmCallback == null)
                        return;
                    confirmCallback();
                });
                ucp.SetAutoConfirmCallback(() =>
                {
                    DestroyUserConfirmPanel(ucp);
                    if (autoConfirmCallback == null)
                        return;
                    autoConfirmCallback();
                });
                ucp.SetConfirmStickyCallback(() =>
                {
                    DestroyUserConfirmPanel(ucp);
                    if (confirmStickyCallback == null)
                        return;
                    confirmStickyCallback();
                });
                ucp.SetDenyCallback(() =>
                {
                    DestroyUserConfirmPanel(ucp);
                    if (denyCallback == null)
                        return;
                    denyCallback();
                });
                ucp.SetAutoDenyCallback(() =>
                {
                    DestroyUserConfirmPanel(ucp);
                    if (autoDenyCallback == null)
                        return;
                    autoDenyCallback();
                });
                ucp.SetDenyStickyCallback(() =>
                {
                    DestroyUserConfirmPanel(ucp);
                    if (denyStickyCallback == null)
                        return;
                    denyStickyCallback();
                });
            }
            else
            {
                Object.Destroy(transform.gameObject);
                if (denyCallback == null)
                    return;
                denyCallback();
            }
        }
        else
        {
            if (denyCallback == null)
                return;
            denyCallback();
        }
    }

    public static void ConfirmWithUser(
        string prompt,
        UserActionCallback confirmCallback,
        UserActionCallback autoConfirmCallback,
        UserActionCallback confirmStickyCallback,
        UserActionCallback denyCallback,
        UserActionCallback autoDenyCallback,
        UserActionCallback denyStickyCallback)
    {
        UserConfirm(prompt, confirmCallback, autoConfirmCallback, confirmStickyCallback,
            denyCallback, autoDenyCallback, denyStickyCallback);
    }

    public static void AutoConfirmAllPanelsWithSignature(string signature)
    {
        List<UserConfirmPanel> userConfirmPanelList = [];
        foreach (var userConfirmPanel in activeUserConfirmPanels)
        {
            if (userConfirmPanel.signature == signature)
                userConfirmPanelList.Add(userConfirmPanel);
        }

        foreach (var userConfirmPanel in userConfirmPanelList)
            userConfirmPanel.AutoConfirm();
    }

    public static void ConfirmAllPanelsWithSignature(string signature, bool isPlugin)
    {
        List<UserConfirmPanel> userConfirmPanelList = [];
        foreach (var userConfirmPanel in activeUserConfirmPanels)
        {
            if (userConfirmPanel.signature == signature)
                userConfirmPanelList.Add(userConfirmPanel);
        }

        foreach (var userConfirmPanel in userConfirmPanelList)
            userConfirmPanel.Confirm();
        if (!isPlugin)
            return;
        userConfirmedPlugins.Add(signature);
    }

    public static void AutoConfirmAllWithSignature(string signature)
    {
        AutoConfirmAllPanelsWithSignature(signature);
    }

    public static void AutoDenyAllPanelsWithSignature(string signature)
    {
        List<UserConfirmPanel> userConfirmPanelList = [];
        foreach (var userConfirmPanel in activeUserConfirmPanels)
        {
            if (userConfirmPanel.signature == signature)
                userConfirmPanelList.Add(userConfirmPanel);
        }

        foreach (var userConfirmPanel in userConfirmPanelList)
            userConfirmPanel.AutoDeny();
    }

    public static void DenyAllPanelsWithSignature(string signature, bool isPlugin)
    {
        List<UserConfirmPanel> userConfirmPanelList = [];
        foreach (var userConfirmPanel in activeUserConfirmPanels)
        {
            if (userConfirmPanel.signature == signature)
                userConfirmPanelList.Add(userConfirmPanel);
        }

        foreach (var userConfirmPanel in userConfirmPanelList)
            userConfirmPanel.Deny();
        if (!isPlugin)
            return;
        userDeniedPlugins.Add(signature);
    }

    public static void AutoDenyAllWithSignature(string signature)
    {
        AutoDenyAllPanelsWithSignature(signature);
    }

    public static void UserConfirmPluginAction(
        string prompt,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback)
    {
        if (userConfirmContainer != null &&
            userConfirmPluginActionPrefab != null)
        {
            var pluginHash = GetPluginHash();
            if (pluginHash == null)
                Debug.LogError("Plugin did not have signature!");
            if (pluginHash != null)
            {
                if (userDeniedPlugins.Contains(pluginHash))
                {
                    denyCallback?.Invoke();
                    return;
                }

                if (userConfirmedPlugins.Contains(pluginHash))
                {
                    confirmCallback?.Invoke();
                    return;
                }
            }

            var transform = Object.Instantiate(userConfirmPluginActionPrefab, userConfirmContainer, false);
            transform.SetAsFirstSibling();
            var ucp = transform.GetComponent<UserConfirmPanel>();
            if (ucp != null && pluginHash != null)
            {
                if (pluginHashToPluginPath == null ||
                    !pluginHashToPluginPath.TryGetValue(pluginHash, out var str))
                    str = pluginHash;
                ucp.signature = pluginHash;
                ucp.SetPrompt("Plugin " + str + "\nwants to " + prompt);
                activeUserConfirmPanels.Add(ucp);
                ucp.SetConfirmCallback(() =>
                {
                    DestroyUserConfirmPanel(ucp);
                    if (confirmCallback == null)
                        return;
                    confirmCallback();
                });
                ucp.SetConfirmStickyCallback(() =>
                    ConfirmAllPanelsWithSignature(pluginHash, true));
                ucp.SetDenyCallback(() =>
                {
                    DestroyUserConfirmPanel(ucp);
                    if (denyCallback == null)
                        return;
                    denyCallback();
                });
                ucp.SetDenyStickyCallback(
                    () => DenyAllPanelsWithSignature(pluginHash, true));
            }
            else
            {
                Object.Destroy(transform.gameObject);
                if (denyCallback == null)
                    return;
                denyCallback();
            }
        }
        else
        {
            if (denyCallback == null)
                return;
            denyCallback();
        }
    }

    public static void ConfirmPluginActionWithUser(
        string prompt,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback)
    {
        UserConfirmPluginAction(prompt, confirmCallback, denyCallback);
    }

    private const string FilePrefix = "file:///";

    public static string GetFullPath(string path)
    {
        if (path.StartsWith(FilePrefix))
        {
            path = path.Substring(FilePrefix.Length - 1, path.Length);
        }

        return Path.GetFullPath(path);
    }

    private static readonly Regex PackagePathRegex = new(":/.*"); 

    public static bool IsPackagePath(string path)
    {
        return GetPackage(PackagePathRegex.Replace(path.Replace('\\', '/'), string.Empty)) != null;
    }

    public static bool IsSimulatedPackagePath(string path)
    {
        var package = GetPackage(PackagePathRegex.Replace(path.Replace('\\', '/'), string.Empty));
        return package != null && package.IsSimulated;
    }

    public static string ConvertSimulatedPackagePathToNormalPath(string path)
    {
        var input = path.Replace('\\', '/');
        if (input.Contains(":/"))
        {
            var package = GetPackage(PackagePathRegex.Replace(input, string.Empty));
            if (package != null && package.IsSimulated)
            {
                var str = Regex.Replace(input, ".*:/", string.Empty);
                path = package.SlashPath + "/" + str;
            }
        }

        return path;
    }

    public static string RemovePackageFromPath(string path)
    {
        return Regex.Replace(Regex.Replace(path, ".*:/", string.Empty), @".*:\\", string.Empty);
    }

    public static string NormalizePath(string path)
    {
        var str1 = path;
        var varFileEntry = GetVarFileEntry(path);
        string str2;
        if (varFileEntry == null)
        {
            var fullPath = GetFullPath(path);
            var oldValue = Path.GetFullPath(".") + "\\";
            var str3 = fullPath.Replace(oldValue, string.Empty);
            if (str3 != fullPath)
                str1 = str3;
            str2 = str1.Replace('\\', '/');
        }
        else
            str2 = varFileEntry.Uid;

        return str2;
    }

    public static string GetDirectoryName(string path, bool returnSlashPath = false)
    {
        return Path.GetDirectoryName(
            uidToVarFileEntry == null || !uidToVarFileEntry.TryGetValue(path, out var varFileEntry)
                ? (!returnSlashPath ? path.Replace('/', '\\') : path.Replace('\\', '/'))
                : (!returnSlashPath ? varFileEntry.Path : varFileEntry.SlashPath));
    }

    public static string GetSuggestedBrowserDirectoryFromDirectoryPath(
        string suggestedDir,
        string currentDir,
        bool allowPackagePath = true)
    {
        if (string.IsNullOrEmpty(currentDir))
            return suggestedDir;
        var oldValue = Regex.Replace(suggestedDir.Replace('\\', '/'), "/$", string.Empty);
        var path = currentDir.Replace('\\', '/');
        var varDirectoryEntry = GetVarDirectoryEntry(path);
        if (varDirectoryEntry != null)
        {
            if (!allowPackagePath)
                return null;
            var str1 = varDirectoryEntry.InternalSlashPath.Replace(oldValue, string.Empty);
            if (varDirectoryEntry.InternalSlashPath != str1)
            {
                var str2 = str1.Replace('/', '\\');
                return varDirectoryEntry.Package.SlashPath + ":/" + oldValue + str2;
            }
        }
        else
        {
            var str3 = path.Replace(oldValue, string.Empty);
            if (path != str3)
            {
                var str4 = str3.Replace('/', '\\');
                return suggestedDir + str4;
            }
        }

        return null;
    }

    public static string CurrentLoadDir
    {
        get
        {
            return loadDirStack != null && loadDirStack.Count > 0
                ? loadDirStack.Last.Value
                : null;
        }
    }

    public static string CurrentPackageUid
    {
        get
        {
            var currentLoadDir = CurrentLoadDir;
            if (currentLoadDir != null)
            {
                var varDirectoryEntry = GetVarDirectoryEntry(currentLoadDir);
                if (varDirectoryEntry != null)
                    return varDirectoryEntry.Package.Uid;
            }

            return null;
        }
    }

    public static string TopLoadDir
    {
        get
        {
            return loadDirStack != null && loadDirStack.Count > 0
                ? loadDirStack.First.Value
                : null;
        }
    }

    public static string TopPackageUid
    {
        get
        {
            var topLoadDir = TopLoadDir;
            if (topLoadDir != null)
            {
                var varDirectoryEntry = GetVarDirectoryEntry(topLoadDir);
                if (varDirectoryEntry != null)
                    return varDirectoryEntry.Package.Uid;
            }

            return null;
        }
    }

    public static void SetLoadDir(string dir, bool restrictPath)
    {
        loadDirStack?.Clear();
        PushLoadDir(dir, restrictPath);
    }

    public static void PushLoadDir(string dir, bool restrictPath = false)
    {
        var str = dir.Replace('\\', '/');
        if (str != "/")
            str = Regex.Replace(str, "/$", string.Empty);
        if (restrictPath && !IsSecureReadPath(str))
            throw new Exception("Attempted to push load dir for non-secure dir " + str);
        loadDirStack ??= [];
        loadDirStack.AddLast(str);
    }

    public static string PopLoadDir()
    {
        if (loadDirStack is not { Count: > 0 }) return null;
        var str = loadDirStack.Last.Value;
        loadDirStack.RemoveLast();

        return str;
    }

    public static void SetLoadDirFromFilePath(string path, bool restrictPath = false)
    {
        loadDirStack?.Clear();
        PushLoadDirFromFilePath(path, restrictPath);
    }

    public static void PushLoadDirFromFilePath(string path, bool restrictPath = false)
    {
        var fileEntry = !restrictPath || IsSecureReadPath(path)
            ? GetFileEntry(path)
            : throw new Exception("Attempted to set load dir from non-secure path " + path);
        PushLoadDir(
            fileEntry == null
                ? Path.GetDirectoryName(GetFullPath(path))
                    .Replace(Path.GetFullPath(".") + "\\", string.Empty)
                : (!(fileEntry is VarFileEntry)
                    ? Path.GetDirectoryName(fileEntry.FullPath).Replace(Path.GetFullPath(".") + "\\", string.Empty)
                    : Path.GetDirectoryName(fileEntry.Uid)), restrictPath);
    }

    public static string PackageIDToPackageGroupID(string packageId)
    {
        return Regex.Replace(
            Regex.Replace(Regex.Replace(packageId, "\\.[0-9]+$", string.Empty), "\\.latest$", string.Empty),
            "\\.min[0-9]+$", string.Empty);
    }

    public static string PackageIDToPackageVersion(string packageId)
    {
        var match = Regex.Match(packageId, "[0-9]+$");
        return match.Success ? match.Value : null;
    }

    public static string NormalizeID(string id)
    {
        var path = id;
        string str;
        if (path.StartsWith("SELF:"))
        {
            var currentPackageUid = CurrentPackageUid;
            str = currentPackageUid == null
                ? path.Replace("SELF:", string.Empty)
                : path.Replace("SELF:", currentPackageUid + ":");
        }
        else
            str = NormalizeCommon(path);

        return str;
    }

    public static string NormalizeCommon(string path)
    {
        var input = path;
        Match match1;
        if ((match1 = Regex.Match(input, @"^(([^\.]+\.[^\.]+)\.latest):")).Success)
        {
            var oldValue = match1.Groups[1].Value;
            var packageGroup = GetPackageGroup(match1.Groups[2].Value);
            var newestEnabledPackage = packageGroup?.NewestEnabledPackage;
            if (newestEnabledPackage != null)
                input = input.Replace(oldValue, newestEnabledPackage.Uid);
        }
        else
        {
            Match match2;
            if ((match2 = Regex.Match(input, @"^(([^\.]+\.[^\.]+)\.min([0-9]+)):")).Success)
            {
                var oldValue = match2.Groups[1].Value;
                var packageGroupUid = match2.Groups[2].Value;
                var requestVersion = int.Parse(match2.Groups[3].Value);
                var packageGroup = GetPackageGroup(packageGroupUid);
                var matchingPackageVersion =
                    packageGroup?.GetClosestMatchingPackageVersion(requestVersion, returnLatestOnMissing: false);
                if (matchingPackageVersion != null)
                    input = input.Replace(oldValue, matchingPackageVersion.Uid);
            }
            else
            {
                Match match3;
                if (!(match3 = Regex.Match(input, @"^([^\.]+\.[^\.]+\.[0-9]+):")).Success) return input;
                var str = match3.Groups[1].Value;
                var package = GetPackage(str);
                if (package is { Enabled: true }) return input;
                var packageGroup =
                    GetPackageGroup(PackageIDToPackageGroupID(str));
                var newestEnabledPackage = packageGroup?.NewestEnabledPackage;
                if (newestEnabledPackage != null)
                    input = input.Replace(str, newestEnabledPackage.Uid);
            }
        }

        return input;
    }

    public static string NormalizeLoadPath(string path)
    {
        var str1 = path;
        if (string.IsNullOrEmpty(path) || path == "/" || path == "NULL") return str1;
        var str2 = path.Replace('\\', '/');
        var currentLoadDir = CurrentLoadDir;
        if (!string.IsNullOrEmpty(currentLoadDir))
        {
            if (!str2.Contains("/"))
                str2 = currentLoadDir + "/" + str2;
            else if (Regex.IsMatch(str2, "^\\./"))
                str2 = Regex.Replace(str2, "^\\./", currentLoadDir + "/");
        }

        if (str2.StartsWith("SELF:/"))
        {
            var currentPackageUid = CurrentPackageUid;
            str1 = currentPackageUid == null
                ? str2.Replace("SELF:/", string.Empty)
                : str2.Replace("SELF:/", currentPackageUid + ":/");
        }
        else
            str1 = NormalizeCommon(str2);

        return str1;
    }

    public static string CurrentSaveDir { get; set; }

    public static void SetSaveDir(string path, bool restrictPath = true)
    {
        if (string.IsNullOrEmpty(path))
        {
            CurrentSaveDir = string.Empty;
        }
        else
        {
            path = ConvertSimulatedPackagePathToNormalPath(path);
            if (IsPackagePath(path))
                return;
            CurrentSaveDir = !restrictPath || IsSecureWritePath(path)
                ? GetFullPath(path).Replace(Path.GetFullPath(".") + "\\", string.Empty)
                    .Replace('\\', '/')
                : throw new Exception("Attempted to set save dir from non-secure path " + path);
        }
    }

    public static void SetSaveDirFromFilePath(string path, bool restrictPath = true)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (IsPackagePath(path))
            return;
        CurrentSaveDir = !restrictPath || IsSecureWritePath(path)
            ? Path.GetDirectoryName(GetFullPath(path)).Replace(Path.GetFullPath(".") + "\\", string.Empty)
                .Replace('\\', '/')
            : throw new Exception("Attempted to set save dir from non-secure path " + path);
    }

    public static void SetNullSaveDir() => CurrentSaveDir = null;

    public static string NormalizeSavePath(string path)
    {
        var str = path;
        if (string.IsNullOrEmpty(path) || path == "/" || path == "NULL") return str;

        var fullPath = GetFullPath(path);
        var oldValue = Path.GetFullPath(".") + "\\";
        var path1 = fullPath.Replace(oldValue, string.Empty);
        if (path1 != fullPath)
            str = path1;
        str = str.Replace('\\', '/');
        var fileName = Path.GetFileName(path1);
        var input = Path.GetDirectoryName(path1);
        input = input?.Replace('\\', '/');
        if (CurrentSaveDir == input)
        {
            str = fileName;
        }
        else if (input != null && CurrentSaveDir != null && CurrentSaveDir != string.Empty &&
                 Regex.IsMatch(input, "^" + CurrentSaveDir + "/"))
        {
            str = input.Replace(CurrentSaveDir, ".") + "/" + fileName;
        }

        return str;
    }

    public static List<VarPackage> GetPackages()
    {
        return packagesByUid == null
            ? []
            : packagesByUid.Values.ToList();
    }

    public static List<string> GetPackageUids()
    {
        var packageUids = packagesByUid.Keys.ToList();
        packageUids.Sort();

        return packageUids;
    }

    public static bool IsPackage(string packageUidOrPath)
    {
        return packagesByUid != null && packagesByUid.ContainsKey(packageUidOrPath) ||
               packagesByPath != null && packagesByPath.ContainsKey(packageUidOrPath);
    }

    public static VarPackage GetPackage(string packageUidOrPath)
    {
        VarPackage package = null;
        Match match1;
        if ((match1 = Regex.Match(packageUidOrPath, @"^([^\.]+\.[^\.]+)\.latest$")).Success)
        {
            var packageGroup = GetPackageGroup(match1.Groups[1].Value);
            if (packageGroup != null)
                package = packageGroup.NewestPackage;
        }
        else
        {
            Match match2;
            if ((match2 = Regex.Match(packageUidOrPath, @"^([^\.]+\.[^\.]+)\.min([0-9]+)$")).Success)
            {
                var packageGroupUid = match2.Groups[1].Value;
                var requestVersion = int.Parse(match2.Groups[2].Value);
                var packageGroup = GetPackageGroup(packageGroupUid);
                if (packageGroup != null)
                    package = packageGroup.GetClosestMatchingPackageVersion(requestVersion, false, false);
            }
            else if (packagesByUid != null && packagesByUid.ContainsKey(packageUidOrPath))
                packagesByUid.TryGetValue(packageUidOrPath, out package);
            else if (packagesByPath != null && packagesByPath.ContainsKey(packageUidOrPath))
                packagesByPath.TryGetValue(packageUidOrPath, out package);
        }

        return package;
    }

    public static List<VarPackageGroup> GetPackageGroups()
    {
        return packageGroups == null
            ? []
            : packageGroups.Values.ToList();
    }

    public static VarPackageGroup GetPackageGroup(string packageGroupUid)
    {
        VarPackageGroup packageGroup = null;
        packageGroups?.TryGetValue(packageGroupUid, out packageGroup);
        return packageGroup;
    }

    public static void SyncAutoAlwaysAllowPlugins(
        HashSet<FileManager.PackageUIDAndPublisher> packageUids)
    {
        foreach (var packageUid in packageUids)
        {
            var vp = GetPackage(packageUid.uid);
            if (vp == null || !vp.HasMatchingDirectories("Custom/Scripts")) continue;
            foreach (var package in vp.Group.Packages)
            {
                if (package == vp || !package.PluginsAlwaysEnabled) continue;
                vp.PluginsAlwaysEnabled = true;
                SuperController.AlertUser(
                    vp.Uid + "\nuploaded by Hub user: " + packageUid.publisher +
                    "\n\nwas just downloaded and contains plugins. This package has been automatically set to always enable plugins due to previous version of this same package having that preference set.\n\nClick OK if you accept.\n\nClick Cancel if you wish to reject this automatic setting.",
                    null, () => vp.PluginsAlwaysEnabled = false);
                break;
            }

            if (!UserPreferences.singleton.alwaysAllowPluginsDownloadedFromHub || vp.PluginsAlwaysEnabled) continue;
            vp.PluginsAlwaysEnabled = true;
            SuperController.AlertUser(
                vp.Uid + "\nuploaded by Hub user: " + packageUid.publisher +
                "\n\nwas just downloaded and contains plugins. This package has been automatically set to always enable plugins due to your user preference setting.\n\nClick OK if you accept.\n\nClick Cancel if you wish to reject this automatic setting for this package.",
                null, () => vp.PluginsAlwaysEnabled = false);
        }

        packageUids.Clear();
    }

    public static string CleanFilePath(string path) => path?.Replace('\\', '/');

    public static void FindAllFiles(
        string dir,
        string pattern,
        List<FileEntry> foundFiles,
        bool restrictPath = false)
    {
        FindRegularFiles(dir, pattern, foundFiles, restrictPath);
        FindVarFiles(dir, pattern, foundFiles);
    }

    public static void FindAllFilesRegex(
        string dir,
        string regex,
        List<FileEntry> foundFiles,
        bool restrictPath = false)
    {
        FindRegularFilesRegex(dir, regex, foundFiles, restrictPath);
        FindVarFilesRegex(dir, regex, foundFiles);
    }

    public static void FindRegularFiles(
        string dir,
        string pattern,
        List<FileEntry> foundFiles,
        bool restrictPath = false)
    {
        if (!Directory.Exists(dir))
            return;
        if (restrictPath && !IsSecureReadPath(dir))
            throw new Exception("Attempted to find files for non-secure path " + dir);
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        FindRegularFilesRegex(dir, regex, foundFiles, restrictPath);
    }

    public static bool CheckIfDirectoryChanged(
        string dir,
        DateTime previousCheckTime,
        bool recurse = true)
    {
        if (!Directory.Exists(dir)) return false;
        if (Directory.GetLastWriteTime(dir) > previousCheckTime)
            return true;
        if (!recurse) return false;
        foreach (var directory in Directory.GetDirectories(dir))
        {
            if (CheckIfDirectoryChanged(directory, previousCheckTime))
                return true;
        }

        return false;
    }

    public static void FindRegularFilesRegex(
        string dir,
        string regex,
        List<FileEntry> foundFiles,
        bool restrictPath = false)
    {
        dir = CleanDirectoryPath(dir);
        if (!Directory.Exists(dir))
            return;
        if (restrictPath && !IsSecureReadPath(dir))
            throw new Exception("Attempted to find files for non-secure path " + dir);
        foreach (var file in Directory.GetFiles(dir))
        {
            if (!Regex.IsMatch(file, regex, RegexOptions.IgnoreCase)) continue;
            var systemFileEntry = new SystemFileEntry(file);
            if (systemFileEntry.Exists)
                foundFiles.Add(systemFileEntry);
            else
                Debug.LogError("Error in lookup SystemFileEntry for " + file);
        }

        foreach (var directory in Directory.GetDirectories(dir))
            FindRegularFilesRegex(directory, regex, foundFiles);
    }

    public static void FindVarFiles(string dir, string pattern, List<FileEntry> foundFiles)
    {
        if (allVarFileEntries == null)
            return;
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        FindVarFilesRegex(dir, regex, foundFiles);
    }

    public static void FindVarFilesRegex(string dir, string regex, List<FileEntry> foundFiles)
    {
        dir = CleanDirectoryPath(dir);
        if (allVarFileEntries == null)
            return;
        foreach (var allVarFileEntry in allVarFileEntries)
        {
            if (allVarFileEntry.InternalSlashPath.StartsWith(dir) &&
                Regex.IsMatch(allVarFileEntry.Name, regex, RegexOptions.IgnoreCase))
                foundFiles.Add(allVarFileEntry);
        }
    }

    public static bool FileExists(string path, bool onlySystemFiles = false, bool restrictPath = false)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!onlySystemFiles)
        {
            var key = CleanFilePath(path);
            if (uidToVarFileEntry != null && uidToVarFileEntry.ContainsKey(path) ||
                pathToVarFileEntry != null && pathToVarFileEntry.ContainsKey(key))
                return true;
        }

        if (!File.Exists(path)) return false;
        if (restrictPath && !IsSecureReadPath(path))
            throw new Exception("Attempted to check file existence for non-secure path " + path);
        return true;

    }

    public static DateTime FileLastWriteTime(string path, bool onlySystemFiles = false, bool restrictPath = false)
    {
        if (string.IsNullOrEmpty(path)) return DateTime.MinValue;
        if (!onlySystemFiles)
        {
            var key = CleanFilePath(path);
            if (uidToVarFileEntry != null &&
                uidToVarFileEntry.TryGetValue(path, out var varFileEntry))
                return varFileEntry.LastWriteTime;
            if (pathToVarFileEntry != null &&
                pathToVarFileEntry.TryGetValue(key, out varFileEntry))
                return varFileEntry.LastWriteTime;
        }

        if (File.Exists(path))
            return !restrictPath || IsSecureReadPath(path)
                ? new FileInfo(path).LastWriteTime
                : throw new Exception("Attempted to check file existence for non-secure path " + path);

        return DateTime.MinValue;
    }

    public static DateTime FileCreationTime(string path, bool onlySystemFiles = false, bool restrictPath = false)
    {
        if (string.IsNullOrEmpty(path)) return DateTime.MinValue;
        if (!onlySystemFiles)
        {
            var key = CleanFilePath(path);
            if (uidToVarFileEntry != null &&
                uidToVarFileEntry.TryGetValue(path, out var varFileEntry))
                return varFileEntry.LastWriteTime;
            if (pathToVarFileEntry != null &&
                pathToVarFileEntry.TryGetValue(key, out varFileEntry))
                return varFileEntry.LastWriteTime;
        }

        if (File.Exists(path))
            return !restrictPath || IsSecureReadPath(path)
                ? new FileInfo(path).CreationTime
                : throw new Exception("Attempted to check file existence for non-secure path " + path);

        return DateTime.MinValue;
    }

    public static bool IsFileInPackage(string path)
    {
        var key = CleanFilePath(path);
        return uidToVarFileEntry != null && uidToVarFileEntry.ContainsKey(key) ||
               pathToVarFileEntry != null && pathToVarFileEntry.ContainsKey(key);
    }

    public static bool IsFavorite(string path, bool restrictPath = false)
    {
        var fileEntry = (FileEntry)GetVarFileEntry(path) ??
                        GetSystemFileEntry(path, restrictPath);
        return fileEntry != null && fileEntry.IsFavorite();
    }

    public static void SetFavorite(string path, bool fav, bool restrictPath = false)
    {
        ((FileEntry)GetVarFileEntry(path) ??
         GetSystemFileEntry(path, restrictPath))?.SetFavorite(fav);
    }

    public static bool IsHidden(string path, bool restrictPath = false)
    {
        var fileEntry = (FileEntry)GetVarFileEntry(path) ??
                        GetSystemFileEntry(path, restrictPath);
        return fileEntry != null && fileEntry.IsHidden();
    }

    public static void SetHidden(string path, bool hide, bool restrictPath = false)
    {
        ((FileEntry)GetVarFileEntry(path) ??
         GetSystemFileEntry(path, restrictPath))?.SetHidden(hide);
    }

    public static FileEntry GetFileEntry(string path, bool restrictPath = false)
    {
        return (FileEntry)GetVarFileEntry(path) ??
               GetSystemFileEntry(path, restrictPath);
    }

    public static SystemFileEntry GetSystemFileEntry(string path, bool restrictPath = false)
    {
        SystemFileEntry systemFileEntry = null;
        if (File.Exists(path))
            systemFileEntry = !restrictPath || IsSecureReadPath(path)
                ? new SystemFileEntry(path)
                : throw new Exception("Attempted to get file entry for non-secure path " + path);
        return systemFileEntry;
    }


    public static VarFileEntry GetVarFileEntry(string path)
    {
        if (path == null)
        {
            return null;
        }

        var key = CleanFilePath(path);
        if (key == null)
        {
            return null;
        }

        if (uidToVarFileEntry == null)
        {
            return null;
        }

        if (uidToVarFileEntry.TryGetValue(key, out var varFileEntry))
        {
            return varFileEntry;
        }

        if (pathToVarDirectoryEntry == null)
        {
            return null;
        }

        if (pathToVarFileEntry.TryGetValue(key, out varFileEntry))
        {
            return varFileEntry;
        }

        return null;
    }

    public static void SortFileEntriesByLastWriteTime(List<FileEntry> fileEntries)
    {
        fileEntries.Sort((e1, e2) => e1.LastWriteTime.CompareTo(e2.LastWriteTime));
    }

    public static string CleanDirectoryPath(string path)
    {
        if (path == null)
        {
            return null;
        }

        path = path.Replace('\\', '/');
        if (path.EndsWith("/"))
        {
            path = path.Substring(0, path.Length - 1);
        }

        return path;
    }

    public static int FolderContentsCount(string path)
    {
        var length = Directory.GetFiles(path).Length;
        foreach (var directory in Directory.GetDirectories(path))
            length += FolderContentsCount(directory);
        return length;
    }

    public static List<VarDirectoryEntry> FindVarDirectories(string dir, bool exactMatch = true)
    {
        dir = CleanDirectoryPath(dir);
        List<VarDirectoryEntry> varDirectories = [];
        if (allVarDirectoryEntries != null)
        {
            foreach (var varDirectoryEntry in allVarDirectoryEntries)
            {
                if (exactMatch)
                {
                    if (varDirectoryEntry.InternalSlashPath == dir)
                        varDirectories.Add(varDirectoryEntry);
                }
                else if (varDirectoryEntry.InternalSlashPath.StartsWith(dir))
                    varDirectories.Add(varDirectoryEntry);
            }
        }

        return varDirectories;
    }

    public static List<ShortCut> GetShortCutsForDirectory(
        string dir,
        bool allowNavigationAboveRegularDirectories = false,
        bool useFullPaths = false,
        bool generateAllFlattenedShortcut = false,
        bool includeRegularDirsInFlattenedShortcut = false)
    {
        dir = Regex.Replace(dir, @".*:\\", string.Empty);
        var str = dir.TrimEnd('/', '\\').Replace('\\', '/');
        var varDirectories = FindVarDirectories(str);
        List<ShortCut> cutsForDirectory = [];
        if (DirectoryExists(str))
        {
            var shortCut = new ShortCut();
            shortCut.package = string.Empty;
            if (allowNavigationAboveRegularDirectories)
            {
                str = str.Replace('/', '\\');
                shortCut.path = !useFullPaths ? str : Path.GetFullPath(str);
            }
            else
                shortCut.path = str;

            shortCut.displayName = str;
            cutsForDirectory.Add(shortCut);
        }

        if (varDirectories.Count > 0)
        {
            if (generateAllFlattenedShortcut)
            {
                if (includeRegularDirsInFlattenedShortcut)
                    cutsForDirectory.Add(new ShortCut
                    {
                        path = str,
                        displayName = "From: " + str,
                        flatten = true,
                        package = "All Flattened",
                        includeRegularDirsInFlatten = true
                    });
                cutsForDirectory.Add(new ShortCut
                {
                    path = str,
                    displayName = "From: " + str,
                    flatten = true,
                    package = "AddonPackages Flattened"
                });
            }

            cutsForDirectory.Add(new ShortCut
            {
                package = "AddonPackages Filtered",
                path = "AddonPackages",
                displayName = "Filter: " + str,
                packageFilter = str
            });
        }

        foreach (var varDirectoryEntry in varDirectories)
            cutsForDirectory.Add(new ShortCut
            {
                directoryEntry = varDirectoryEntry,
                isLatest = varDirectoryEntry.Package.isNewestEnabledVersion,
                package = varDirectoryEntry.Package.Uid,
                creator = varDirectoryEntry.Package.Creator,
                displayName = varDirectoryEntry.InternalSlashPath,
                path = varDirectoryEntry.SlashPath
            });
        return cutsForDirectory;
    }

    public static bool DirectoryExists(string path, bool onlySystemDirectories = false, bool restrictPath = false)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!onlySystemDirectories)
        {
            var key = CleanDirectoryPath(path);
            if (uidToVarDirectoryEntry != null &&
                uidToVarDirectoryEntry.ContainsKey(key) ||
                pathToVarDirectoryEntry != null &&
                pathToVarDirectoryEntry.ContainsKey(key))
                return true;
        }

        if (Directory.Exists(path))
        {
            if (restrictPath && !IsSecureReadPath(path))
                throw new Exception("Attempted to check file existence for non-secure path " + path);
            return true;
        }

        return false;
    }

    public static DateTime DirectoryLastWriteTime(
        string path,
        bool onlySystemDirectories = false,
        bool restrictPath = false)
    {
        if (string.IsNullOrEmpty(path)) return DateTime.MinValue;
        if (!onlySystemDirectories)
        {
            var key = CleanFilePath(path);
            if (uidToVarDirectoryEntry != null &&
                uidToVarDirectoryEntry.TryGetValue(path, out var varDirectoryEntry))
                return varDirectoryEntry.LastWriteTime;
            if (pathToVarDirectoryEntry != null &&
                pathToVarDirectoryEntry.TryGetValue(key, out varDirectoryEntry))
                return varDirectoryEntry.LastWriteTime;
        }

        if (Directory.Exists(path))
            return !restrictPath || IsSecureReadPath(path)
                ? new DirectoryInfo(path).LastWriteTime
                : throw new Exception("Attempted to check directory last write time for non-secure path " + path);

        return DateTime.MinValue;
    }

    public static DateTime DirectoryCreationTime(
        string path,
        bool onlySystemDirectories = false,
        bool restrictPath = false)
    {
        if (string.IsNullOrEmpty(path)) return DateTime.MinValue;
        if (!onlySystemDirectories)
        {
            var key = CleanFilePath(path);
            if (uidToVarDirectoryEntry != null &&
                uidToVarDirectoryEntry.TryGetValue(path, out var varDirectoryEntry))
                return varDirectoryEntry.LastWriteTime;
            if (pathToVarDirectoryEntry != null &&
                pathToVarDirectoryEntry.TryGetValue(key, out varDirectoryEntry))
                return varDirectoryEntry.LastWriteTime;
        }

        if (Directory.Exists(path))
            return !restrictPath || IsSecureReadPath(path)
                ? new DirectoryInfo(path).CreationTime
                : throw new Exception("Attempted to check directory creation time for non-secure path " + path);

        return DateTime.MinValue;
    }

    public static bool IsDirectoryInPackage(string path)
    {
        var key = CleanDirectoryPath(path);
        return uidToVarDirectoryEntry != null &&
               uidToVarDirectoryEntry.ContainsKey(key) ||
               pathToVarDirectoryEntry != null &&
               pathToVarDirectoryEntry.ContainsKey(key);
    }

    public static DirectoryEntry GetDirectoryEntry(string path, bool restrictPath = false)
    {
        var path1 = Regex.Replace(path, @"(/|\\)$", string.Empty);
        return (DirectoryEntry)GetVarDirectoryEntry(path1) ??
               GetSystemDirectoryEntry(path1, restrictPath);
    }

    public static SystemDirectoryEntry GetSystemDirectoryEntry(string path, bool restrictPath = false)
    {
        SystemDirectoryEntry systemDirectoryEntry = null;
        if (Directory.Exists(path))
            systemDirectoryEntry = !restrictPath || IsSecureReadPath(path)
                ? new SystemDirectoryEntry(path)
                : throw new Exception("Attempted to get directory entry for non-secure path " + path);
        return systemDirectoryEntry;
    }

    public static VarDirectoryEntry GetVarDirectoryEntry(string path)
    {
        VarDirectoryEntry varDirectoryEntry = null;
        var key = CleanDirectoryPath(path);
        if (uidToVarDirectoryEntry != null &&
            uidToVarDirectoryEntry.TryGetValue(key, out varDirectoryEntry) ||
            pathToVarDirectoryEntry == null ||
            !pathToVarDirectoryEntry.TryGetValue(key, out varDirectoryEntry))
            ;
        return varDirectoryEntry;
    }

    public static VarDirectoryEntry GetVarRootDirectoryEntryFromPath(string path)
    {
        VarDirectoryEntry directoryEntryFromPath = null;
        varPackagePathToRootVarDirectory?.TryGetValue(path, out directoryEntryFromPath);
        return directoryEntryFromPath;
    }

    public static string[] GetDirectories(string dir, string pattern = null, bool restrictPath = false)
    {
        if (restrictPath && !IsSecureReadPath(dir))
            throw new Exception("Attempted to get directories at non-secure path " + dir);
        List<string> stringList = [];
        var directoryEntry = GetDirectoryEntry(dir, restrictPath);
        if (directoryEntry == null)
            throw new Exception("Attempted to get directories at non-existent path " + dir);
        string pattern1 = null;
        if (pattern != null)
            pattern1 = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        foreach (var subDirectory in directoryEntry.SubDirectories)
        {
            if (pattern1 == null || Regex.IsMatch(subDirectory.Name, pattern1))
                stringList.Add(dir + "\\" + subDirectory.Name);
        }

        return stringList.ToArray();
    }

    public static string[] GetFiles(string dir, string pattern = null, bool restrictPath = false)
    {
        if (restrictPath && !IsSecureReadPath(dir))
            throw new Exception("Attempted to get files at non-secure path " + dir);
        List<string> stringList = [];
        var directoryEntry = GetDirectoryEntry(dir, restrictPath);
        if (directoryEntry == null)
            throw new Exception("Attempted to get files at non-existent path " + dir);
        foreach (var file in directoryEntry.GetFiles(pattern))
            stringList.Add(dir + "\\" + file.Name);
        return stringList.ToArray();
    }

    public static void CreateDirectory(string path)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (DirectoryExists(path))
            return;
        if (!IsSecureWritePath(path))
            throw new Exception("Attempted to create directory at non-secure path " + path);
        Directory.CreateDirectory(path);
    }

    public static void CreateDirectoryFromPlugin(
        string path,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (DirectoryExists(path))
            return;
        if (!IsSecurePluginWritePath(path))
        {
            var e = new Exception("Plugin attempted to create directory at non-secure path " + path);
            if (exceptionCallback == null)
                throw e;
            exceptionCallback(e);
        }
        else
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                if (exceptionCallback == null)
                    throw;
                exceptionCallback(ex);
                return;
            }

            if (confirmCallback == null)
                return;
            confirmCallback();
        }
    }

    public static void DeleteDirectory(string path, bool recursive = false)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (!DirectoryExists(path))
            return;
        if (!IsSecureWritePath(path))
            throw new Exception("Attempted to delete file at non-secure path " + path);
        Directory.Delete(path, recursive);
    }

    public static void DeleteDirectoryFromPlugin(
        string path,
        bool recursive,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (!DirectoryExists(path))
            return;
        if (!IsSecurePluginWritePath(path))
        {
            var e = new Exception("Plugin attempted to delete directory at non-secure path " + path);
            if (exceptionCallback == null)
                throw e;
            exceptionCallback(e);
        }
        else if (!IsPluginWritePathThatNeedsConfirm(path))
        {
            try
            {
                Directory.Delete(path, recursive);
            }
            catch (Exception ex)
            {
                if (exceptionCallback == null)
                    throw;
                exceptionCallback(ex);
                return;
            }

            if (confirmCallback == null)
                return;
            confirmCallback();
        }
        else
            ConfirmPluginActionWithUser("delete directory at " + path, () =>
            {
                try
                {
                    Directory.Delete(path, recursive);
                }
                catch (Exception ex)
                {
                    exceptionCallback?.Invoke(ex);
                    return;
                }

                if (confirmCallback == null)
                    return;
                confirmCallback();
            }, denyCallback);
    }

    public static void MoveDirectory(string oldPath, string newPath)
    {
        oldPath = ConvertSimulatedPackagePathToNormalPath(oldPath);
        if (!IsSecureWritePath(oldPath))
            throw new Exception("Attempted to move directory from non-secure path " + oldPath);
        newPath = ConvertSimulatedPackagePathToNormalPath(newPath);
        if (!IsSecureWritePath(newPath))
            throw new Exception("Attempted to move directory to non-secure path " + newPath);
        Directory.Move(oldPath, newPath);
    }

    public static void MoveDirectoryFromPlugin(
        string oldPath,
        string newPath,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        oldPath = ConvertSimulatedPackagePathToNormalPath(oldPath);
        if (!IsSecurePluginWritePath(oldPath))
        {
            var e = new Exception("Plugin attempted to move directory from non-secure path " + oldPath);
            if (exceptionCallback == null)
                throw e;
            exceptionCallback(e);
        }
        else
        {
            newPath = ConvertSimulatedPackagePathToNormalPath(newPath);
            if (!IsSecurePluginWritePath(newPath))
            {
                var e = new Exception("Plugin attempted to move directory to non-secure path " + newPath);
                if (exceptionCallback == null)
                    throw e;
                exceptionCallback(e);
            }
            else
            {
                if (!IsPluginWritePathThatNeedsConfirm(oldPath))
                {
                    if (!IsPluginWritePathThatNeedsConfirm(newPath))
                    {
                        try
                        {
                            Directory.Move(oldPath, newPath);
                        }
                        catch (Exception ex)
                        {
                            if (exceptionCallback == null)
                                throw;
                            exceptionCallback(ex);
                            return;
                        }

                        confirmCallback?.Invoke();
                        return;
                    }
                }

                ConfirmPluginActionWithUser("move directory from " + oldPath + " to " + newPath,
                    () =>
                    {
                        try
                        {
                            Directory.Move(oldPath, newPath);
                        }
                        catch (Exception ex)
                        {
                            exceptionCallback?.Invoke(ex);
                            return;
                        }

                        if (confirmCallback == null)
                            return;
                        confirmCallback();
                    }, denyCallback);
            }
        }
    }

    public static FileEntryStream OpenStream(FileEntry fe)
    {
        switch (fe)
        {
            case null:
                throw new Exception("Null FileEntry passed to OpenStreamReader");
            case VarFileEntry _:
                return new VarFileEntryStream(fe as VarFileEntry);
            case SystemFileEntry _:
                return new SystemFileEntryStream(fe as SystemFileEntry);
            default:
                throw new Exception("Unknown FileEntry class passed to OpenStreamReader");
        }
    }

    public static FileEntryStream OpenStream(string path, bool restrictPath = false)
    {
        return OpenStream(GetFileEntry(path, restrictPath) ??
                          throw new Exception("Path " + path + " not found"));
    }

    public static FileEntryStreamReader OpenStreamReader(FileEntry fe)
    {
        switch (fe)
        {
            case null:
                throw new Exception("Null FileEntry passed to OpenStreamReader");
            case VarFileEntry _:
                return new VarFileEntryStreamReader(fe as VarFileEntry);
            case SystemFileEntry _:
                return new SystemFileEntryStreamReader(fe as SystemFileEntry);
            default:
                throw new Exception("Unknown FileEntry class passed to OpenStreamReader");
        }
    }

    public static FileEntryStreamReader OpenStreamReader(string path, bool restrictPath = false)
    {
        return OpenStreamReader(GetFileEntry(path, restrictPath) ??
                                throw new Exception("Path " + path + " not found"));
    }

    public static byte[] ReadAllBytes(string path, bool restrictPath = false)
    {
        return ReadAllBytes(GetFileEntry(path, restrictPath) ??
                            throw new Exception("Path " + path + " not found"));
    }

    public static byte[] ReadAllBytes(FileEntry fe)
    {
        if (!(fe is VarFileEntry))
            return File.ReadAllBytes(fe.FullPath);
        var numArray = new byte[32768];
        using (var fileEntryStream = OpenStream(fe))
        {
            var buffer = new byte[fe.Size];
            using (var memoryStream = new MemoryStream(buffer))
                StreamUtils.Copy(fileEntryStream.Stream, memoryStream, numArray);
            return buffer;
        }
    }

    public static string ReadAllText(string path, bool restrictPath = false)
    {
        return ReadAllText(GetFileEntry(path, restrictPath) ??
                           throw new Exception("Path " + path + " not found"));
    }

    public static string ReadAllText(FileEntry fe)
    {
        using (var entryStreamReader = OpenStreamReader(fe))
            return entryStreamReader.ReadToEnd();
    }

    public static FileStream OpenStreamForCreate(string path)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        return IsSecureWritePath(path)
            ? File.Open(path, FileMode.Create)
            : throw new Exception("Attempted to open stream for create at non-secure path " + path);
    }

    public static StreamWriter OpenStreamWriter(string path)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        return IsSecureWritePath(path)
            ? new StreamWriter(path)
            : throw new Exception("Attempted to open stream writer at non-secure path " + path);
    }

    public static void WriteAllText(string path, string text)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (!IsSecureWritePath(path))
            throw new Exception("Attempted to write all text at non-secure path " + path);
        File.WriteAllText(path, text);
    }

    public static void WriteAllTextFromPlugin(
        string path,
        string text,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (!IsSecurePluginWritePath(path))
        {
            var e = new Exception("Plugin attempted to write all text at non-secure path " + path);
            if (exceptionCallback == null)
                throw e;
            exceptionCallback(e);
        }
        else if (File.Exists(path))
        {
            if (!IsPluginWritePathThatNeedsConfirm(path))
            {
                try
                {
                    File.WriteAllText(path, text);
                }
                catch (Exception ex)
                {
                    if (exceptionCallback == null)
                        throw;
                    exceptionCallback(ex);
                    return;
                }

                if (confirmCallback == null)
                    return;
                confirmCallback();
            }
            else
                ConfirmPluginActionWithUser("overwrite file " + path, () =>
                {
                    try
                    {
                        File.WriteAllText(path, text);
                    }
                    catch (Exception ex)
                    {
                        exceptionCallback?.Invoke(ex);
                        return;
                    }

                    if (confirmCallback == null)
                        return;
                    confirmCallback();
                }, denyCallback);
        }
        else
        {
            try
            {
                File.WriteAllText(path, text);
            }
            catch (Exception ex)
            {
                if (exceptionCallback == null)
                    throw;
                exceptionCallback(ex);
                return;
            }

            if (confirmCallback == null)
                return;
            confirmCallback();
        }
    }

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (!IsSecureWritePath(path))
            throw new Exception("Attempted to write all bytes at non-secure path " + path);
        File.WriteAllBytes(path, bytes);
    }

    public static void WriteAllBytesFromPlugin(
        string path,
        byte[] bytes,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (!IsSecurePluginWritePath(path))
        {
            var e = new Exception("Plugin attempted to write all bytes at non-secure path " + path);
            if (exceptionCallback == null)
                throw e;
            exceptionCallback(e);
        }
        else if (File.Exists(path))
        {
            if (!IsPluginWritePathThatNeedsConfirm(path))
            {
                try
                {
                    File.WriteAllBytes(path, bytes);
                }
                catch (Exception ex)
                {
                    if (exceptionCallback == null)
                        throw;
                    exceptionCallback(ex);
                    return;
                }

                if (confirmCallback == null)
                    return;
                confirmCallback();
            }
            else
                ConfirmPluginActionWithUser("overwrite file " + path, () =>
                {
                    try
                    {
                        File.WriteAllBytes(path, bytes);
                    }
                    catch (Exception ex)
                    {
                        exceptionCallback?.Invoke(ex);
                        return;
                    }

                    if (confirmCallback == null)
                        return;
                    confirmCallback();
                }, denyCallback);
        }
        else
        {
            try
            {
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex)
            {
                if (exceptionCallback == null)
                    throw;
                exceptionCallback(ex);
                return;
            }

            if (confirmCallback == null)
                return;
            confirmCallback();
        }
    }

    public static void SetFileAttributes(string path, FileAttributes attrs)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (!IsSecureWritePath(path))
            throw new Exception("Attempted to set file attributes at non-secure path " + path);
        File.SetAttributes(path, attrs);
    }

    public static void DeleteFile(string path)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (!File.Exists(path))
            return;
        if (!IsSecureWritePath(path))
            throw new Exception("Attempted to delete file at non-secure path " + path);
        File.Delete(path);
    }

    public static void DeleteFileFromPlugin(
        string path,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        path = ConvertSimulatedPackagePathToNormalPath(path);
        if (!File.Exists(path))
            return;
        if (!IsSecurePluginWritePath(path))
        {
            var e = new Exception("Plugin attempted to delete file at non-secure path " + path);
            if (exceptionCallback == null)
                throw e;
            exceptionCallback(e);
        }
        else if (!IsPluginWritePathThatNeedsConfirm(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                if (exceptionCallback == null)
                    throw;
                exceptionCallback(ex);
                return;
            }

            if (confirmCallback == null)
                return;
            confirmCallback();
        }
        else
            ConfirmPluginActionWithUser("delete file " + path, () =>
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    exceptionCallback?.Invoke(ex);
                    return;
                }

                if (confirmCallback == null)
                    return;
                confirmCallback();
            }, denyCallback);
    }

    public static void DoFileCopy(string oldPath, string newPath)
    {
        var fileEntry = GetFileEntry(oldPath);
        if (fileEntry != null && fileEntry is VarFileEntry)
        {
            var numArray = new byte[4096];
            using (var fileEntryStream = OpenStream(fileEntry))
            {
                using (var fileStream = OpenStreamForCreate(newPath))
                    StreamUtils.Copy(fileEntryStream.Stream, fileStream, numArray);
            }
        }
        else
            File.Copy(oldPath, newPath);
    }

    public static void CopyFile(string oldPath, string newPath, bool restrictPath = false)
    {
        oldPath = ConvertSimulatedPackagePathToNormalPath(oldPath);
        if (restrictPath && !IsSecureReadPath(oldPath))
            throw new Exception("Attempted to copy file from non-secure path " + oldPath);
        newPath = ConvertSimulatedPackagePathToNormalPath(newPath);
        if (!IsSecureWritePath(newPath))
            throw new Exception("Attempted to copy file to non-secure path " + newPath);
        DoFileCopy(oldPath, newPath);
    }

    public static void CopyFileFromPlugin(
        string oldPath,
        string newPath,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        oldPath = ConvertSimulatedPackagePathToNormalPath(oldPath);
        if (!IsSecureReadPath(oldPath))
        {
            var e = new Exception("Attempted to copy file from non-secure path " + oldPath);
            if (exceptionCallback == null)
                throw e;
            exceptionCallback(e);
        }
        else
        {
            newPath = ConvertSimulatedPackagePathToNormalPath(newPath);
            if (!IsSecurePluginWritePath(newPath))
            {
                var e = new Exception("Plugin attempted to copy file to non-secure path " + newPath);
                if (exceptionCallback == null)
                    throw e;
                exceptionCallback(e);
            }
            else if (File.Exists(newPath))
            {
                if (!IsPluginWritePathThatNeedsConfirm(newPath))
                {
                    try
                    {
                        DoFileCopy(oldPath, newPath);
                    }
                    catch (Exception ex)
                    {
                        if (exceptionCallback == null)
                            throw;
                        exceptionCallback(ex);
                        return;
                    }

                    if (confirmCallback == null)
                        return;
                    confirmCallback();
                }
                else
                    ConfirmPluginActionWithUser(
                        "copy file from " + oldPath + " to existing file " + newPath, () =>
                        {
                            try
                            {
                                DoFileCopy(oldPath, newPath);
                            }
                            catch (Exception ex)
                            {
                                exceptionCallback?.Invoke(ex);
                                return;
                            }

                            if (confirmCallback == null)
                                return;
                            confirmCallback();
                        }, denyCallback);
            }
            else
            {
                try
                {
                    DoFileCopy(oldPath, newPath);
                }
                catch (Exception ex)
                {
                    if (exceptionCallback == null)
                        throw;
                    exceptionCallback(ex);
                    return;
                }

                if (confirmCallback == null)
                    return;
                confirmCallback();
            }
        }
    }

    public static void DoFileMove(string oldPath, string newPath, bool overwrite = true)
    {
        if (File.Exists(newPath))
        {
            if (!overwrite)
                throw new Exception("File " + newPath + " exists. Cannot move into");
            File.Delete(newPath);
        }

        File.Move(oldPath, newPath);
    }

    public static void MoveFile(string oldPath, string newPath, bool overwrite = true)
    {
        oldPath = ConvertSimulatedPackagePathToNormalPath(oldPath);
        if (!IsSecureWritePath(oldPath))
            throw new Exception("Attempted to move file from non-secure path " + oldPath);
        newPath = ConvertSimulatedPackagePathToNormalPath(newPath);
        if (!IsSecureWritePath(newPath))
            throw new Exception("Attempted to move file to non-secure path " + newPath);
        DoFileMove(oldPath, newPath, overwrite);
    }

    public static void MoveFileFromPlugin(
        string oldPath,
        string newPath,
        bool overwrite,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        oldPath = ConvertSimulatedPackagePathToNormalPath(oldPath);
        if (!IsSecurePluginWritePath(oldPath))
        {
            var e = new Exception("Plugin attempted to move file from non-secure path " + oldPath);
            if (exceptionCallback == null)
                throw e;
            exceptionCallback(e);
        }
        else
        {
            newPath = ConvertSimulatedPackagePathToNormalPath(newPath);
            if (!IsSecurePluginWritePath(newPath))
            {
                var e = new Exception("Plugin attempted to move file to non-secure path " + newPath);
                if (exceptionCallback == null)
                    throw e;
                exceptionCallback(e);
            }
            else
            {
                if (!IsPluginWritePathThatNeedsConfirm(oldPath))
                {
                    if (!IsPluginWritePathThatNeedsConfirm(newPath))
                    {
                        try
                        {
                            DoFileMove(oldPath, newPath, overwrite);
                        }
                        catch (Exception ex)
                        {
                            if (exceptionCallback == null)
                                throw;
                            exceptionCallback(ex);
                            return;
                        }

                        confirmCallback?.Invoke();
                        return;
                    }
                }

                ConfirmPluginActionWithUser("move file from " + oldPath + " to " + newPath,
                    () =>
                    {
                        try
                        {
                            DoFileMove(oldPath, newPath, overwrite);
                        }
                        catch (Exception ex)
                        {
                            exceptionCallback?.Invoke(ex);
                            return;
                        }

                        if (confirmCallback == null)
                            return;
                        confirmCallback();
                    }, denyCallback);
            }
        }
    }

    // private void Awake() => LazyLazyFileManager.singleton = this;

    public static void OnDestroy() => ClearAll();

    // public class PackageUIDAndPublisher
    // {
    //     public string uid;
    //     public string publisher;
    // }
}

//     public static VarPackage RegisterPackage(string vpath)
//     {
//         if (FileManager.debug)
//         {
//             Debug.Log("RegisterPackage " + vpath);
//         }
//
//         string uid = LazyFileManager.PackagePathToUid(vpath);
//         string[] strArray = uid.Split('.');
//         if (strArray.Length == 3)
//         {
//             string creator = strArray[0];
//             string name = strArray[1];
//             string str = creator + "." + name;
//             string s = strArray[2];
//             try
//             {
//                 int version = int.Parse(s);
//                 if (packagesByUid.ContainsKey(uid))
//                 {
//                     SuperController.LogError("Duplicate package uid " + uid + ". Cannot register");
//                 }
//                 else
//                 {
//                     VarPackageGroup group;
//                     if (!packageGroups.TryGetValue(str, out group))
//                     {
//                         group = new VarPackageGroup(str);
//                         packageGroups.Add(str, group);
//                     }
//
//                     VarPackage vp = new VarPackage(uid, vpath, group, creator, name, version);
//                     packagesByUid.Add(uid, vp);
//                     packagesByPath.Add(vp.Path, vp);
//                     packagesByPath.Add(vp.SlashPath, vp);
//                     packagesByPath.Add(vp.FullPath, vp);
//                     packagesByPath.Add(vp.FullSlashPath, vp);
//                     group.AddPackage(vp);
//                     if (vp.Enabled)
//                     {
//                         enabledPackages.Add(vp);
//                         foreach (VarFileEntry fileEntry in vp.FileEntries)
//                         {
//                             allVarFileEntries.Add(fileEntry);
//                             uidToVarFileEntry.Add(fileEntry.Uid, fileEntry);
//                             if (FileManager.debug)
//                             {
//                                 Debug.Log("Add var file with UID " + fileEntry.Uid);
//                             }
//
//                             pathToVarFileEntry.Add(fileEntry.Path, fileEntry);
//                             pathToVarFileEntry.Add(fileEntry.SlashPath, fileEntry);
//                             pathToVarFileEntry.Add(fileEntry.FullPath, fileEntry);
//                             pathToVarFileEntry.Add(fileEntry.FullSlashPath, fileEntry);
//                         }
//
//                         foreach (VarDirectoryEntry directoryEntry in vp.DirectoryEntries)
//                         {
//                             allVarDirectoryEntries.Add(directoryEntry);
//                             if (FileManager.debug)
//                             {
//                                 Debug.Log("Add var directory with UID " + directoryEntry.Uid);
//                             }
//
//                             uidToVarDirectoryEntry.Add(directoryEntry.Uid, directoryEntry);
//                             pathToVarDirectoryEntry.Add(directoryEntry.Path, directoryEntry);
//                             pathToVarDirectoryEntry.Add(directoryEntry.SlashPath, directoryEntry);
//                             pathToVarDirectoryEntry.Add(directoryEntry.FullPath, directoryEntry);
//                             pathToVarDirectoryEntry.Add(directoryEntry.FullSlashPath, directoryEntry);
//                         }
//
//                         varPackagePathToRootVarDirectory.Add(vp.Path, vp.RootDirectory);
//                         varPackagePathToRootVarDirectory.Add(vp.FullPath, vp.RootDirectory);
//                     }
//
//                     return vp;
//                 }
//             }
//             catch (FormatException)
//             {
//                 SuperController.LogError("VAR file " + vpath +
//                                          " does not use integer version field in name <creator>.<name>.<version>");
//             }
//         }
//         else
//         {
//             SuperController.LogError("VAR file " + vpath +
//                                      " is not named with convention <creator>.<name>.<version>");
//         }
//
//         return null;
//     }
// }

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
    // [HarmonyPatch(typeof(FileManager), MettMethodType.Constructor)]
    // [HarmonyPostfix]
    // private static void CTOR()
    // {
    //     FileManagerStatics.Init();
    // }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetFullPath))]
    [HarmonyPrefix]
    public static bool GetFullPath(ref string __result, string path)
    {
        __result = LazyFileManager.GetFullPath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager),
        "packagePathToUid"
    )]
    [HarmonyPrefix]
    public static bool packagePathToUid(ref string __result, string vpath)
    {
        __result = LazyFileManager.PackagePathToUid(vpath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "RegisterPackage")]
    [HarmonyPrefix]
    public static bool RegisterPackage(ref VarPackage __result, string vpath)
    {
        __result = LazyFileManager.RegisterPackage(vpath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.UnregisterPackage))]
    [HarmonyPrefix]
    public static bool UnregisterPackage(VarPackage vp)
    {
        LazyFileManager.UnregisterPackage(vp);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SyncJSONCache))]
    [HarmonyPrefix]
    public static bool SyncJSONCache()
    {
        LazyFileManager.SyncJSONCache();
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.RegisterRefreshHandler))]
    [HarmonyPrefix]
    public static bool RegisterRefreshHandler(OnRefresh refreshHandler)
    {
        LazyFileManager.RegisterRefreshHandler(refreshHandler);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.UnregisterRefreshHandler))]
    [HarmonyPrefix]
    public static bool UnregisterRefreshHandler(OnRefresh refreshHandler)
    {
        LazyFileManager.UnregisterRefreshHandler(refreshHandler);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "ClearAll")]
    [HarmonyPrefix]
    public static bool ClearAll()
    {
        LazyFileManager.ClearAll();
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.PackageFolder), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool PackageFolder(ref string __result)
    {
        __result = LazyFileManager.packageFolder;
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.UserPrefsFolder), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool UserPrefsFolder(ref string __result)
    {
        __result = LazyFileManager.userPrefsFolder;
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.lastPackageRefreshTime), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool lastPackageRefreshTime(ref DateTime __result)
    {
        __result = LazyFileManager.LastPackageRefreshTime;
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.RegisterRestrictedReadPath))]
    [HarmonyPrefix]
    public static bool RegisterRestrictedReadPath(string path)
    {
        LazyFileManager.RegisterRestrictedReadPath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.RegisterSecureReadPath))]
    [HarmonyPrefix]
    public static bool RegisterSecureReadPath(string path)
    {
        LazyFileManager.RegisterSecureReadPath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.ClearSecureReadPaths))]
    [HarmonyPrefix]
    public static bool ClearSecureReadPaths()
    {
        LazyFileManager.ClearSecureReadPaths();
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.IsSecureReadPath))]
    [HarmonyPrefix]
    public static bool IsSecureReadPath(ref bool __result, string path)
    {
        __result = LazyFileManager.IsSecureReadPath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.ClearSecureWritePaths))]
    [HarmonyPrefix]
    public static bool ClearSecureWritePaths()
    {
        LazyFileManager.ClearSecureWritePaths();
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.RegisterInternalSecureWritePath))]
    [HarmonyPrefix]
    public static bool RegisterInternalSecureWritePath(string path)
    {
        LazyFileManager.RegisterInternalSecureWritePath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.RegisterPluginSecureWritePath))]
    [HarmonyPrefix]
    public static bool RegisterPluginSecureWritePath(string path, bool doesNotNeedConfirm)
    {
        LazyFileManager.RegisterPluginSecureWritePath(path, doesNotNeedConfirm);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.IsSecureWritePath))]
    [HarmonyPrefix]
    public static bool IsSecureWritePath(ref bool __result, string path)
    {
        __result = LazyFileManager.IsSecureWritePath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.IsSecurePluginWritePath))]
    [HarmonyPrefix]
    public static bool IsSecurePluginWritePath(ref bool __result, string path)
    {
        __result = LazyFileManager.IsSecurePluginWritePath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.IsPluginWritePathThatNeedsConfirm))]
    [HarmonyPrefix]
    public static bool IsPluginWritePathThatNeedsConfirm(ref bool __result, string path)
    {
        __result = LazyFileManager.IsPluginWritePathThatNeedsConfirm(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.RegisterPluginHashToPluginPath))]
    [HarmonyPrefix]
    public static bool RegisterPluginHashToPluginPath(string hash, string path)
    {
        LazyFileManager.RegisterPluginHashToPluginPath(hash, path);
        return false;
    }

    // TODO should we touch this?
    // TODO can we just stub this out? manipulating stacktraces is expensive...
    // [HarmonyPatch(typeof(FileManager), "GetPluginHash")]
    // [HarmonyPrefix]
    // public static bool GetPluginHash(ref string __result)
    // {
    //     __result = LazyFileManager.GetPluginHash();
    //     return false;
    // }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.AssertNotCalledFromPlugin))]
    [HarmonyPrefix]
    public static bool AssertNotCalledFromPlugin()
    {
        LazyFileManager.AssertNotCalledFromPlugin();
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "DestroyUserConfirmPanel")]
    [HarmonyPrefix]
    public static bool DestroyUserConfirmPanel(UserConfirmPanel ucp)
    {
        LazyFileManager.DestroyUserConfirmPanel(ucp);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "CreateUserConfirmFlag")]
    [HarmonyPrefix]
    public static bool CreateUserConfirmFlag()
    {
        LazyFileManager.CreateUserConfirmFlag();
        return false;
    }


    [HarmonyPatch(typeof(FileManager), "DestroyAllUserConfirmPanels")]
    [HarmonyPrefix]
    public static bool DestroyAllUserConfirmPanels()
    {
        LazyFileManager.DestroyAllUserConfirmPanels();
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "UserConfirm")]
    [HarmonyPrefix]
    public static bool UserConfirm(
        string prompt,
        UserActionCallback confirmCallback,
        UserActionCallback autoConfirmCallback,
        UserActionCallback confirmStickyCallback,
        UserActionCallback denyCallback,
        UserActionCallback autoDenyCallback,
        UserActionCallback denyStickyCallback)
    {
        LazyFileManager.UserConfirm(prompt, confirmCallback, autoConfirmCallback, confirmStickyCallback, denyCallback,
            autoDenyCallback, denyStickyCallback);
        return false;
    }


    [HarmonyPatch(typeof(FileManager), nameof(FileManager.ConfirmWithUser))]
    [HarmonyPrefix]
    public static bool ConfirmWithUser(
        string prompt,
        UserActionCallback confirmCallback,
        UserActionCallback autoConfirmCallback,
        UserActionCallback confirmStickyCallback,
        UserActionCallback denyCallback,
        UserActionCallback autoDenyCallback,
        UserActionCallback denyStickyCallback)
    {
        LazyFileManager.ConfirmWithUser(prompt, confirmCallback, autoConfirmCallback, confirmStickyCallback,
            denyCallback,
            autoDenyCallback, denyStickyCallback);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "AutoConfirmAllPanelsWithSignature")]
    [HarmonyPrefix]
    public static bool AutoConfirmAllPanelsWithSignature(string signature)
    {
        LazyFileManager.AutoConfirmAllPanelsWithSignature(signature);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "ConfirmAllPanelsWithSignature")]
    [HarmonyPrefix]
    public static bool ConfirmAllPanelsWithSignature(string signature, bool isPlugin)
    {
        LazyFileManager.ConfirmAllPanelsWithSignature(signature, isPlugin);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.AutoConfirmAllWithSignature))]
    [HarmonyPrefix]
    public static bool AutoConfirmAllWithSignature(string signature)
    {
        LazyFileManager.AutoConfirmAllWithSignature(signature);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "DenyAllPanelsWithSignature")]
    [HarmonyPrefix]
    public static bool DenyAllPanelsWithSignature(string signature, bool isPlugin)
    {
        LazyFileManager.DenyAllPanelsWithSignature(signature, isPlugin);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "AutoDenyAllPanelsWithSignature")]
    [HarmonyPrefix]
    public static bool AutoDenyAllPanelsWithSignature(string signature)
    {
        LazyFileManager.AutoDenyAllPanelsWithSignature(signature);
        return false;
    }
    
    [HarmonyPatch(typeof(FileManager), "AutoDenyAllWithSignature")]
    [HarmonyPrefix]
    public static bool AutoDenyAllWithSignature(string signature)
    {
        LazyFileManager.AutoDenyAllWithSignature(signature);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "UserConfirmPluginAction")]
    [HarmonyPrefix]
    public static bool UserConfirmPluginAction(string prompt,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback)
    {
        LazyFileManager.UserConfirmPluginAction(prompt, confirmCallback, denyCallback);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.ConfirmPluginActionWithUser))]
    [HarmonyPrefix]
    public static bool ConfirmPluginActionWithUser(string prompt,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback)
    {
        LazyFileManager.ConfirmPluginActionWithUser(prompt, confirmCallback, denyCallback);
        return false;
    }


    [HarmonyPatch(typeof(FileManager), nameof(FileManager.IsPackagePath))]
    [HarmonyPrefix]
    public static bool IsPackagePath(ref bool __result, string path)
    {
        __result = LazyFileManager.IsPackagePath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.IsSimulatedPackagePath))]
    [HarmonyPrefix]
    public static bool IsSimulatedPackagePath(ref bool __result, string path)
    {
        __result = LazyFileManager.IsSimulatedPackagePath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.ConvertSimulatedPackagePathToNormalPath))]
    [HarmonyPrefix]
    public static bool ConvertSimulatedPackagePathToNormalPath(ref string __result, string path)
    {
        __result = LazyFileManager.ConvertSimulatedPackagePathToNormalPath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.RemovePackageFromPath))]
    [HarmonyPrefix]
    public static bool RemovePackageFromPath(ref string __result, string path)
    {
        __result = LazyFileManager.RemovePackageFromPath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.NormalizePath))]
    [HarmonyPrefix]
    public static bool NormalizePath(ref string __result, string path)
    {
        __result = LazyFileManager.NormalizePath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetDirectoryName))]
    [HarmonyPrefix]
    public static bool GetDirectoryName(ref string __result, string path, bool returnSlashPath)
    {
        __result = LazyFileManager.GetDirectoryName(path, returnSlashPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetSuggestedBrowserDirectoryFromDirectoryPath))]
    [HarmonyPrefix]
    public static bool GetSuggestedBrowserDirectoryFromDirectoryPath(ref string __result, string suggestedDir,
        string currentDir, bool allowPackagePath)
    {
        __result = LazyFileManager.GetSuggestedBrowserDirectoryFromDirectoryPath(suggestedDir, currentDir,
            allowPackagePath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.CurrentLoadDir), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool CurrentLoadDir(ref string __result)
    {
        __result = LazyFileManager.CurrentLoadDir;
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.CurrentPackageUid), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool CurrentPackageUid(ref string __result)
    {
        __result = LazyFileManager.CurrentPackageUid;
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.TopLoadDir), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool TopLoadDir(ref string __result)
    {
        __result = LazyFileManager.TopLoadDir;
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.TopPackageUid), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool TopPackageUid(ref string __result)
    {
        __result = LazyFileManager.TopPackageUid;
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.PushLoadDir))]
    [HarmonyPrefix]
    public static bool PushLoadDir(string dir, bool restrictPath)
    {
        LazyFileManager.PushLoadDir(dir, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.PopLoadDir))]
    [HarmonyPrefix]
    public static bool PopLoadDir(ref string __result)
    {
        __result = LazyFileManager.PopLoadDir();
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SetLoadDir))]
    [HarmonyPrefix]
    public static bool SetLoadDir(string dir, bool restrictPath)
    {
        LazyFileManager.SetLoadDir(dir, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SetLoadDirFromFilePath))]
    [HarmonyPrefix]
    public static bool SetLoadDirFromFilePath(string path, bool restrictPath)
    {
        LazyFileManager.SetLoadDirFromFilePath(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SetLoadDirFromFilePath))]
    [HarmonyPrefix]
    public static bool PushLoadDirFromFilePath(string path, bool restrictPath)
    {
        LazyFileManager.PushLoadDirFromFilePath(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.PackageIDToPackageGroupID))]
    [HarmonyPrefix]
    public static bool PackageIDToPackageGroupID(ref string __result, string packageId)
    {
        __result = LazyFileManager.PackageIDToPackageGroupID(packageId);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.PackageIDToPackageVersion))]
    [HarmonyPrefix]
    public static bool PackageIDToPackageVersion(ref string __result, string packageId)
    {
        __result = LazyFileManager.PackageIDToPackageVersion(packageId);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.NormalizeID))]
    [HarmonyPrefix]
    public static bool NormalizeID(ref string __result, string id)
    {
        __result = LazyFileManager.NormalizeID(id);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "NormalizeCommon")]
    [HarmonyPrefix]
    public static bool NormalizeCommon(ref string __result, string path)
    {
        __result = LazyFileManager.NormalizeCommon(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.NormalizeLoadPath))]
    [HarmonyPrefix]
    public static bool NormalizeLoadPath(ref string __result, string path)
    {
        __result = LazyFileManager.NormalizeLoadPath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.CurrentSaveDir), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool CurrentSaveDir(ref string __result)
    {
        __result = LazyFileManager.CurrentSaveDir;
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.CurrentSaveDir), MethodType.Setter)]
    [HarmonyPrefix]
    public static bool CurrentSaveDir(string value)
    {
        LazyFileManager.CurrentSaveDir = value;
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SetSaveDir))]
    [HarmonyPrefix]
    public static bool SetSaveDir(string path, bool restrictPath)
    {
        LazyFileManager.SetSaveDir(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SetSaveDirFromFilePath))]
    [HarmonyPrefix]
    public static bool SetSaveDirFromFilePath(string path, bool restrictPath)
    {
        LazyFileManager.SetSaveDirFromFilePath(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SetNullSaveDir))]
    [HarmonyPrefix]
    public static bool SetNullSaveDir()
    {
        LazyFileManager.SetNullSaveDir();
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.NormalizeSavePath))]
    [HarmonyPrefix]
    public static bool NormalizeSavePath(ref string __result, string path)
    {
        __result = LazyFileManager.NormalizeSavePath(path);
        return false;
    }


    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetPackages))]
    [HarmonyPrefix]
    public static bool GetPackages(ref List<VarPackage> __result)
    {
        __result = LazyFileManager.GetPackages();
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetPackageUids))]
    [HarmonyPrefix]
    public static bool GetPackageUids(ref List<string> __result)
    {
        __result = LazyFileManager.GetPackageUids();
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.IsPackage))]
    [HarmonyPrefix]
    public static bool IsPackage(ref bool __result, string packageUidOrPath)
    {
        __result = LazyFileManager.IsPackage(packageUidOrPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetPackage))]
    [HarmonyPrefix]
    public static bool GetPackage(ref VarPackage __result, string packageUidOrPath)
    {
        __result = LazyFileManager.GetPackage(packageUidOrPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetPackageGroups))]
    [HarmonyPrefix]
    public static bool GetPackageGroups(ref List<VarPackageGroup> __result)
    {
        __result = LazyFileManager.GetPackageGroups();
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetPackageGroup))]
    [HarmonyPrefix]
    public static bool GetPackageGroup(ref VarPackageGroup __result, string packageGroupUid)
    {
        __result = LazyFileManager.GetPackageGroup(packageGroupUid);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SyncAutoAlwaysAllowPlugins))]
    [HarmonyPrefix]
    public static bool SyncAutoAlwaysAllowPlugins(HashSet<FileManager.PackageUIDAndPublisher> packageUids)
    {
        LazyFileManager.SyncAutoAlwaysAllowPlugins(packageUids);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.CleanFilePath))]
    [HarmonyPrefix]
    public static bool CleanFilePath(ref string __result, string path)
    {
        __result = LazyFileManager.CleanFilePath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.FindAllFiles))]
    [HarmonyPrefix]
    public static bool FindAllFiles(string dir,
        string pattern,
        List<FileEntry> foundFiles,
        bool restrictPath
    )
    {
        LazyFileManager.FindAllFiles(dir, pattern, foundFiles, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.FindAllFilesRegex))]
    [HarmonyPrefix]
    public static bool FindAllFilesRegex(string dir,
        string regex,
        List<FileEntry> foundFiles,
        bool restrictPath
    )
    {
        LazyFileManager.FindAllFilesRegex(dir, regex, foundFiles, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.FindRegularFiles))]
    [HarmonyPrefix]
    public static bool FindRegularFiles(string dir,
        string pattern,
        List<FileEntry> foundFiles,
        bool restrictPath
    )
    {
        LazyFileManager.FindRegularFiles(dir, pattern, foundFiles, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.FindRegularFilesRegex))]
    [HarmonyPrefix]
    public static bool FindRegularFilesRegex(string dir,
        string regex,
        List<FileEntry> foundFiles,
        bool restrictPath
    )
    {
        LazyFileManager.FindRegularFilesRegex(dir, regex, foundFiles, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.CheckIfDirectoryChanged))]
    [HarmonyPrefix]
    public static bool CheckIfDirectoryChanged(ref bool __result,
        string dir,
        DateTime previousCheckTime,
        bool recurse
    )
    {
        __result = LazyFileManager.CheckIfDirectoryChanged(dir, previousCheckTime, recurse);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.FindVarFiles))]
    [HarmonyPrefix]
    public static bool FindVarFiles(
        string dir,
        string pattern,
        List<FileEntry> foundFiles
    )
    {
        LazyFileManager.FindVarFiles(dir, pattern, foundFiles);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.FindVarFilesRegex))]
    [HarmonyPrefix]
    public static bool FindVarFilesRegex(
        string dir,
        string regex,
        List<FileEntry> foundFiles
    )
    {
        LazyFileManager.FindVarFilesRegex(dir, regex, foundFiles);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.FileExists))]
    [HarmonyPrefix]
    public static bool FileExists(
        ref bool __result,
        string path, bool onlySystemFiles, bool restrictPath
    )
    {
        __result = LazyFileManager.FileExists(path, onlySystemFiles, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.FileLastWriteTime))]
    [HarmonyPrefix]
    public static bool FileLastWriteTime(
        ref DateTime __result,
        string path, bool onlySystemFiles, bool restrictPath
    )
    {
        __result = LazyFileManager.FileLastWriteTime(path, onlySystemFiles, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.FileCreationTime))]
    [HarmonyPrefix]
    public static bool FileCreationTime(
        ref DateTime __result,
        string path, bool onlySystemFiles, bool restrictPath
    )
    {
        __result = LazyFileManager.FileCreationTime(path, onlySystemFiles, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.IsFileInPackage))]
    [HarmonyPrefix]
    public static bool IsFileInPackage(
        ref bool __result,
        string path
    )
    {
        __result = LazyFileManager.IsFileInPackage(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.IsFavorite))]
    [HarmonyPrefix]
    public static bool IsFavorite(
        ref bool __result,
        string path, bool restrictPath
    )
    {
        __result = LazyFileManager.IsFavorite(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SetFavorite))]
    [HarmonyPrefix]
    public static bool SetFavorite(
        string path, bool fav, bool restrictPath)

    {
        LazyFileManager.SetFavorite(path, fav, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.IsHidden))]
    [HarmonyPrefix]
    public static bool IsHidden(
        ref bool __result,
        string path, bool restrictPath
    )
    {
        __result = LazyFileManager.IsHidden(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SetHidden))]
    [HarmonyPrefix]
    public static bool SetHidden(
        string path, bool hide, bool restrictPath)

    {
        LazyFileManager.SetHidden(path, hide, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetFileEntry))]
    [HarmonyPrefix]
    public static bool GetFileEntry(ref FileEntry __result, string path, bool restrictPath)
    {
        __result = LazyFileManager.GetFileEntry(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetSystemFileEntry))]
    [HarmonyPrefix]
    public static bool GetSystemFileEntry(ref FileEntry __result, string path, bool restrictPath)
    {
        __result = LazyFileManager.GetSystemFileEntry(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetVarFileEntry))]
    [HarmonyPrefix]
    public static bool GetVarFileEntry(ref VarFileEntry __result, string path)
    {
        __result = LazyFileManager.GetVarFileEntry(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SortFileEntriesByLastWriteTime))]
    [HarmonyPrefix]
    public static bool SortFileEntriesByLastWriteTime(List<FileEntry> fileEntries)
    {
        LazyFileManager.SortFileEntriesByLastWriteTime(fileEntries);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.CleanDirectoryPath))]
    [HarmonyPrefix]
    public static bool CleanDirectoryPath(ref string __result, string path)
    {
        __result = LazyFileManager.CleanDirectoryPath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.FolderContentsCount))]
    [HarmonyPrefix]
    public static bool FolderContentsCount(ref int __result, string path)
    {
        __result = LazyFileManager.FolderContentsCount(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.FindVarDirectories))]
    [HarmonyPrefix]
    public static bool FindVarDirectories(ref List<VarDirectoryEntry> __result, string dir, bool exactMatch)
    {
        __result = LazyFileManager.FindVarDirectories(dir, exactMatch);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetShortCutsForDirectory))]
    [HarmonyPrefix]
    public static bool GetShortCutsForDirectory(ref List<ShortCut> __result, string dir,
        bool allowNavigationAboveRegularDirectories,
        bool useFullPaths,
        bool generateAllFlattenedShortcut,
        bool includeRegularDirsInFlattenedShortcut)
    {
        __result = LazyFileManager.GetShortCutsForDirectory(dir, allowNavigationAboveRegularDirectories, useFullPaths,
            generateAllFlattenedShortcut, includeRegularDirsInFlattenedShortcut);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.DirectoryExists))]
    [HarmonyPrefix]
    public static bool DirectoryExists(ref bool __result, string path, bool onlySystemDirectories,
        bool restrictPath)
    {
        __result = LazyFileManager.DirectoryExists(path, onlySystemDirectories, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.DirectoryLastWriteTime))]
    [HarmonyPrefix]
    public static bool DirectoryLastWriteTime(ref DateTime __result, string path,
        bool onlySystemDirectories,
        bool restrictPath)
    {
        __result = LazyFileManager.DirectoryLastWriteTime(path, onlySystemDirectories, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.DirectoryCreationTime))]
    [HarmonyPrefix]
    public static bool DirectoryCreationTime(ref DateTime __result, string path,
        bool onlySystemDirectories,
        bool restrictPath)
    {
        __result = LazyFileManager.DirectoryCreationTime(path, onlySystemDirectories, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.IsDirectoryInPackage))]
    [HarmonyPrefix]
    public static bool IsDirectoryInPackage(ref bool __result, string path)
    {
        __result = LazyFileManager.IsDirectoryInPackage(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetDirectoryEntry))]
    [HarmonyPrefix]
    public static bool GetDirectoryEntry(ref DirectoryEntry __result, string path, bool restrictPath)
    {
        __result = LazyFileManager.GetDirectoryEntry(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetSystemDirectoryEntry))]
    [HarmonyPrefix]
    public static bool GetSystemDirectoryEntry(ref DirectoryEntry __result, string path, bool restrictPath)
    {
        __result = LazyFileManager.GetSystemDirectoryEntry(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetVarDirectoryEntry))]
    [HarmonyPrefix]
    public static bool GetVarDirectoryEntry(ref VarDirectoryEntry __result, string path)
    {
        __result = LazyFileManager.GetVarDirectoryEntry(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetVarRootDirectoryEntryFromPath))]
    [HarmonyPrefix]
    public static bool GetVarRootDirectoryEntryFromPath(ref VarDirectoryEntry __result, string path)
    {
        __result = LazyFileManager.GetVarRootDirectoryEntryFromPath(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetDirectories))]
    [HarmonyPrefix]
    public static bool GetDirectories(ref string[] __result, string dir, string pattern,
        bool restrictPath)
    {
        __result = LazyFileManager.GetDirectories(dir, pattern, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.GetFiles))]
    [HarmonyPrefix]
    public static bool GetFiles(ref string[] __result, string dir, string pattern, bool restrictPath)
    {
        __result = LazyFileManager.GetFiles(dir, pattern, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.CreateDirectory))]
    [HarmonyPrefix]
    public static bool CreateDirectory(string path)
    {
        LazyFileManager.CreateDirectory(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.CreateDirectoryFromPlugin))]
    [HarmonyPrefix]
    public static bool CreateDirectoryFromPlugin(
        string path,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        LazyFileManager.CreateDirectoryFromPlugin(path, confirmCallback, denyCallback, exceptionCallback);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.DeleteDirectory))]
    [HarmonyPrefix]
    public static bool DeleteDirectory(string path, bool recursive)
    {
        LazyFileManager.DeleteDirectory(path, recursive);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.DeleteDirectoryFromPlugin))]
    [HarmonyPrefix]
    public static bool DeleteDirectoryFromPlugin(
        string path,
        bool recursive,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        LazyFileManager.DeleteDirectoryFromPlugin(path, recursive, confirmCallback, denyCallback, exceptionCallback);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.MoveDirectory))]
    [HarmonyPrefix]
    public static bool MoveDirectory(string oldPath, string newPath)
    {
        LazyFileManager.MoveDirectory(oldPath, newPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.MoveDirectoryFromPlugin))]
    [HarmonyPrefix]
    public static bool MoveDirectoryFromPlugin(string oldPath, string newPath,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        LazyFileManager.MoveDirectoryFromPlugin(oldPath, newPath, confirmCallback, denyCallback, exceptionCallback);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.OpenStream), [typeof(FileEntry)])]
    [HarmonyPrefix]
    public static bool OpenStream(ref FileEntryStream __result, FileEntry fe)
    {
        __result = LazyFileManager.OpenStream(fe);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.OpenStream), typeof(string), typeof(bool))]
    [HarmonyPrefix]
    public static bool OpenStream(ref FileEntryStream __result, string path, bool restrictPath)
    {
        __result = LazyFileManager.OpenStream(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.OpenStreamReader), typeof(FileEntry))]
    [HarmonyPrefix]
    public static bool OpenStreamReader(ref FileEntryStreamReader __result, FileEntry fe)
    {
        __result = LazyFileManager.OpenStreamReader(fe);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.OpenStreamReader), typeof(string), typeof(bool))]
    [HarmonyPrefix]
    public static bool OpenStreamReader(ref FileEntryStreamReader __result, string path, bool restrictPath)
    {
        __result = LazyFileManager.OpenStreamReader(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.ReadAllBytes), typeof(FileEntry))]
    [HarmonyPrefix]
    public static bool ReadAllBytes(ref byte[] __result, FileEntry fe)
    {
        __result = LazyFileManager.ReadAllBytes(fe);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.ReadAllBytes), typeof(string), typeof(bool))]
    [HarmonyPrefix]
    public static bool ReadAllBytes(ref byte[] __result, string path, bool restrictPath)
    {
        __result = LazyFileManager.ReadAllBytes(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.ReadAllText), typeof(FileEntry))]
    [HarmonyPrefix]
    public static bool ReadAllText(ref string __result, FileEntry fe)
    {
        __result = LazyFileManager.ReadAllText(fe);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.ReadAllText), typeof(string), typeof(bool))]
    [HarmonyPrefix]
    public static bool ReadAllText(ref string __result, string path, bool restrictPath)
    {
        __result = LazyFileManager.ReadAllText(path, restrictPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.OpenStreamForCreate))]
    [HarmonyPrefix]
    public static bool OpenStreamForCreate(ref FileStream __result, string path)
    {
        __result = LazyFileManager.OpenStreamForCreate(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.OpenStreamWriter))]
    [HarmonyPrefix]
    public static bool OpenStreamWriter(ref StreamWriter __result, string path)
    {
        __result = LazyFileManager.OpenStreamWriter(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.WriteAllText))]
    [HarmonyPrefix]
    public static bool WriteAllText(string path, string text)
    {
        LazyFileManager.WriteAllText(path, text);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.WriteAllTextFromPlugin))]
    [HarmonyPrefix]
    public static bool WriteAllTextFromPlugin(string path, string text,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        LazyFileManager.WriteAllTextFromPlugin(path, text, confirmCallback, denyCallback, exceptionCallback);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.WriteAllBytes))]
    [HarmonyPrefix]
    public static bool WriteAllBytes(string path, byte[] bytes)
    {
        LazyFileManager.WriteAllBytes(path, bytes);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.WriteAllBytesFromPlugin))]
    [HarmonyPrefix]
    public static bool WriteAllBytesFromPlugin(string path, byte[] bytes,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        LazyFileManager.WriteAllBytesFromPlugin(path, bytes, confirmCallback, denyCallback, exceptionCallback);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.SetFileAttributes))]
    [HarmonyPrefix]
    public static bool SetFileAttributes(string path, FileAttributes attrs)
    {
        LazyFileManager.SetFileAttributes(path, attrs);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.DeleteFile))]
    [HarmonyPrefix]
    public static bool DeleteFile(string path)
    {
        LazyFileManager.DeleteFile(path);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.DeleteFileFromPlugin))]
    [HarmonyPrefix]
    public static bool DeleteFileFromPlugin(string path, UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        LazyFileManager.DeleteFileFromPlugin(path, confirmCallback, denyCallback, exceptionCallback);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "DoFileCopy")]
    [HarmonyPrefix]
    public static bool DoFileCopy(string oldPath, string newPath)
    {
        LazyFileManager.DoFileCopy(oldPath, newPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.CopyFile))]
    [HarmonyPrefix]
    public static bool CopyFile(string oldPath, string newPath)
    {
        LazyFileManager.CopyFile(oldPath, newPath);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.CopyFileFromPlugin))]
    [HarmonyPrefix]
    public static bool CopyFileFromPlugin(string oldPath,
        string newPath,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        LazyFileManager.CopyFileFromPlugin(oldPath, newPath, confirmCallback, denyCallback, exceptionCallback);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "DoFileMove")]
    [HarmonyPrefix]
    public static bool DoFileMove(string oldPath, string newPath, bool overwrite)
    {
        LazyFileManager.DoFileMove(oldPath, newPath, overwrite);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.MoveFile))]
    [HarmonyPrefix]
    public static bool MoveFile(string oldPath, string newPath, bool overwrite)
    {
        LazyFileManager.MoveFile(oldPath, newPath, overwrite);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.MoveFileFromPlugin))]
    [HarmonyPrefix]
    public static bool MoveFileFromPlugin(string oldPath,
        string newPath,
        bool overwrite,
        UserActionCallback confirmCallback,
        UserActionCallback denyCallback,
        ExceptionCallback exceptionCallback)
    {
        LazyFileManager.MoveFileFromPlugin(oldPath, newPath, overwrite, confirmCallback, denyCallback,
            exceptionCallback);
        return false;
    }

    [HarmonyPatch(typeof(FileManager), "Awake")]
    [HarmonyPrefix]
    public static bool Awake()
    {
        // LazyFileManager.Awake();
        return false;
    }


    [HarmonyPatch(typeof(FileManager), "OnDestroy")]
    [HarmonyPrefix]
    public static bool OnDestroy()
    {
        LazyFileManager.OnDestroy();
        return false;
    }

    // TODO why does this not work? Looks like it isn't working because of not registering all the addons on startup...
    [HarmonyPatch(typeof(FileManager), nameof(FileManager.Refresh))]
    [HarmonyPrefix]
    public static bool Refresh()
    {
        LazyFileManager.Refresh();
        return false;
        // VaMPerformancePlugin.PluginLogger.LogDebug("Patched FileManager.Refresh() running...");
        // if (FileManager.debug)
        // {
        //     Debug.Log("FileManager Refresh()");
        // }
        //
        // // TODO can we pull this out to avoid re-inits?
        // // FileManagerStatics.Init();
        //
        // // Pull out the static fields we need
        // var packagesByUid = FileManagerStatics.packagesByUid;
        // var packagesByPath = FileManagerStatics.packagesByPath;
        // var packageGroups = FileManagerStatics.packageGroups;
        // var enabledPackages = FileManagerStatics.enabledPackages;
        // VaMPerformancePlugin.PluginLogger.LogDebug(new StringBuilder("Variables: \n")
        //     .AppendLine($"packagesByUid: {packagesByUid}")
        //     .AppendLine($"packagesByPath: {packagesByPath}")
        //     .AppendLine($"packageGroups: {packageGroups}")
        //     .AppendLine($"enabledPackages: {enabledPackages}")
        //     .ToString());
        //
        // var packageFolder = FileManagerStatics.packageFolder;
        // var userPrefsFolder = FileManagerStatics.userPrefsFolder;
        // var packagesEnabled = FileManagerStatics.packagesEnabled;
        // var onRefreshHandlers = FileManagerStatics.onRefreshHandlers;
        // VaMPerformancePlugin.PluginLogger.LogDebug(new StringBuilder("Variables: \n")
        //     .AppendLine($"packageFolder: {packageFolder}")
        //     .AppendLine($"userPrefsFolder: {userPrefsFolder}")
        //     .AppendLine($"packagesEnabled: {packagesEnabled}")
        //     .AppendLine($"onRefreshHandlers: {onRefreshHandlers}")
        //     .ToString());
        //
        // bool packagesChanged = false;
        // float startMillis = 0.0f;
        //
        // if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
        // {
        //     startMillis = GlobalStopwatch.GetElapsedMilliseconds();
        // }
        //
        // try
        // {
        //     if (!Directory.Exists(packageFolder))
        //     {
        //         FileManager.CreateDirectory(packageFolder);
        //     }
        //
        //     if (!Directory.Exists(userPrefsFolder))
        //     {
        //         FileManager.CreateDirectory(userPrefsFolder);
        //     }
        //
        //     if (Directory.Exists(packageFolder))
        //     {
        //         IEnumerable<string> directories = [];
        //         IEnumerable<string> files = [];
        //         if (packagesEnabled)
        //         {
        //             // TODO why both?
        //             directories = Directory.GetDirectories(FileManagerStatics.packageFolder, "*.var",
        //                 SearchOption.AllDirectories);
        //             files = Directory.GetFiles(FileManagerStatics.packageFolder, "*.var", SearchOption.AllDirectories);
        //             // directories = EnumerateDirectories(packageFolder, "*.var", SearchOption.AllDirectories);
        //             // files = EnumerateFiles(packageFolder, "*.var", SearchOption.AllDirectories);
        //         }
        //         else if (FileManager.demoPackagePrefixes != null)
        //         {
        //             IEnumerable<string> result = new List<string>();
        //             foreach (string demoPackagePrefix in FileManager.demoPackagePrefixes)
        //             {
        //                 IEnumerable<string> enumerateFiles = EnumerateFiles(packageFolder,
        //                     demoPackagePrefix + "*.var", SearchOption.AllDirectories);
        //                 result = result.Concat(enumerateFiles);
        //             }
        //
        //             files = result;
        //         }
        //
        //         HashSet<string> registeredPacakges = new();
        //         HashSet<string> unregisteredPackages = new();
        //
        //         foreach (string directory in directories)
        //         {
        //             registeredPacakges.Add(directory);
        //             VarPackage vp;
        //             if (packagesByPath.TryGetValue(directory, out vp))
        //             {
        //                 bool previouslyEnabled = enabledPackages.Contains(vp);
        //                 bool enabled = vp.Enabled;
        //                 if (!previouslyEnabled && enabled || previouslyEnabled && !enabled || !vp.IsSimulated)
        //                 {
        //                     FileManager.UnregisterPackage(vp);
        //                     unregisteredPackages.Add(directory);
        //                 }
        //             }
        //             else
        //             {
        //                 unregisteredPackages.Add(directory);
        //             }
        //         }
        //
        //         if (files != null)
        //         {
        //             foreach (string file in files)
        //             {
        //                 registeredPacakges.Add(file);
        //                 VarPackage vp;
        //                 if (packagesByPath.TryGetValue(file, out vp))
        //                 {
        //                     bool inEnabledPackages = enabledPackages.Contains(vp);
        //                     bool enabled = vp.Enabled;
        //                     if (!inEnabledPackages && enabled || inEnabledPackages && !enabled || vp.IsSimulated)
        //                     {
        //                         FileManager.UnregisterPackage(vp);
        //                         unregisteredPackages.Add(file);
        //                     }
        //                 }
        //                 else
        //                 {
        //                     unregisteredPackages.Add(file);
        //                 }
        //             }
        //         }
        //
        //         HashSet<VarPackage> packagesToRemove = new();
        //         foreach (VarPackage varPackage in packagesByUid.Values)
        //         {
        //             if (!registeredPacakges.Contains(varPackage.Path))
        //             {
        //                 packagesToRemove.Add(varPackage);
        //             }
        //         }
        //
        //         foreach (VarPackage vp in packagesToRemove)
        //         {
        //             VaMPerformancePlugin.PluginLogger.LogDebug($"Unregistering package: {vp}");
        //             FileManager.UnregisterPackage(vp);
        //             packagesChanged = true;
        //         }
        //
        //         foreach (string vpath in unregisteredPackages)
        //         {
        //             VaMPerformancePlugin.PluginLogger.LogDebug($"Registering package: {vpath}");
        //             FileManagerStatics.RegisterPackage(vpath);
        //             packagesChanged = true;
        //         }
        //
        //         if (packagesChanged)
        //         {
        //             foreach (VarPackage varPackage in packagesByUid.Values)
        //             {
        //                 varPackage.LoadMetaData();
        //             }
        //
        //             foreach (VarPackageGroup varPackageGroup in packageGroups.Values)
        //             {
        //                 varPackageGroup.Init();
        //             }
        //         }
        //
        //         if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
        //         {
        //             float elapsedMilliseconds = GlobalStopwatch.GetElapsedMilliseconds();
        //             float packageScanningDurationMillis = elapsedMilliseconds - startMillis;
        //             Debug.Log(new StringBuilder().Append("Scanned ")
        //                 .Append(packagesByUid.Count)
        //                 .Append(" packages in ")
        //                 .Append(packageScanningDurationMillis.ToString("F1"))
        //                 .Append(" ms")
        //                 .ToString());
        //             startMillis = elapsedMilliseconds;
        //         }
        //
        //         foreach (VarPackage varPackage in packagesByUid.Values)
        //         {
        //             if (varPackage.forceRefresh)
        //             {
        //                 Debug.Log("Force refresh of package " + varPackage.Uid);
        //                 packagesChanged = true;
        //                 varPackage.forceRefresh = false;
        //             }
        //         }
        //
        //         if (packagesChanged)
        //         {
        //             Debug.Log("Package changes detected");
        //             onRefreshHandlers?.Invoke();
        //         }
        //         else
        //         {
        //             Debug.Log("No package changes detected");
        //         }
        //
        //         if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
        //         {
        //             float elapsedMilliseconds2 = GlobalStopwatch.GetElapsedMilliseconds();
        //             Debug.Log(new StringBuilder().Append("Refresh Handlers took ")
        //                 .Append((elapsedMilliseconds2 - startMillis).ToString("F1"))
        //                 .Append(" ms")
        //                 .ToString());
        //             startMillis = elapsedMilliseconds2;
        //         }
        //     }
        // }
        // catch (TargetInvocationException ex)
        // {
        //     VaMPerformancePlugin.PluginLogger.LogError(ex);
        // }
        // catch (Exception ex)
        // {
        //     SuperController.LogError(new StringBuilder().AppendLine("Exception during package refresh ")
        //         .Append(ex)
        //         .ToString());
        // }
        //
        // if (!VaMPerformancePlugin.Options.EnabledGlobalStopwatch.Value)
        // {
        //     Debug.Log(new StringBuilder().Append("Refresh package handlers took ")
        //         .Append((GlobalStopwatch.GetElapsedMilliseconds() - startMillis).ToString("F1"))
        //         .Append(" ms")
        //         .ToString());
        // }
        //
        // FileManagerStatics.lastPackageRefreshTime = DateTime.Now;
        // return false;
    }
}