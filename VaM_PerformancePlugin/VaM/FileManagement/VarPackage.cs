using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using MVR.FileManagement;
using SimpleJSON;
using VaM_PerformancePlugin.extra;

namespace VaM_PerformancePlugin.VaM.FileManagement;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class VarPackagePatch
{
    static bool FindFilesMatch(VarFileEntry fileEntry)
    {
        return fileEntry.InternalSlashPath.StartsWith("Saves/scenes") && fileEntry.Name.EndsWith(".json");
    } 
    
    [HarmonyPatch(typeof(VarPackage), nameof(VarPackage.FindFiles))]
    public static bool FindFiles(ref VarPackage __instance, ref string dir, ref string pattern, ref List<FileEntry> foundFiles)
    {
        // common pattern we can optimize for
        if ("Saves/scenes".Equals(dir) && "*.json".Equals(pattern))
        {
            foreach (VarFileEntry fileEntry in __instance.FileEntries)
            {
                if (FindFilesMatch(fileEntry))
                {
                    foundFiles.Add(fileEntry);
                }
            }

            return false;
        }

        // default to existing behavior
        return true;
    }

    [HarmonyPatch(typeof(VarPackage), "AddDirToCache")]
    public static bool AddDirToCache(ref VarPackage __instance, ref VarDirectoryEntry de, string pattern,
        JSONClass cache)
    {
        HashSet<VarDirectoryEntry> dirsToCache = new HashSet<VarDirectoryEntry>();
        Queue<VarDirectoryEntry> dirsToTraverse = new Queue<VarDirectoryEntry>();
        dirsToTraverse.Enqueue(de);
        
        
        // un-roll directory "tree" to avoid recursion to improve perf
        while (dirsToTraverse.Count > 0)
        {
            var dir = dirsToTraverse.Dequeue();
            foreach (var subDir in dir.VarSubDirectories)
            {
                dirsToTraverse.Enqueue(subDir);
            }
            // the hashset will take care of de-duping for us
            dirsToCache.Add(dir);
        }
        
        // un-roll file list, mostly for de-duping
        HashSet<VarFileEntry> filesToCache = new HashSet<VarFileEntry>();
        foreach (VarDirectoryEntry directoryEntry in dirsToCache)
        {
            var files = directoryEntry.GetFiles(pattern);
            foreach (var file in files)
            {
                if (file is VarFileEntry fileEntry)
                {
                    filesToCache.Add(fileEntry);
                }
            }
        }


        foreach (VarFileEntry varFileEntry in filesToCache)
        {
            var jsonNode = JSON.Parse(FileManager.ReadAllText(varFileEntry));
            if (jsonNode != null)
            {
                cache[varFileEntry.InternalSlashPath] = jsonNode;
            }
        }
        
        return false;
    }
    
    [HarmonyPatch]
    [HarmonyFinalizer]
    public static Exception Finalizer(Exception __exception)
    {
        return new PluginException("VarPackagePatch had an exception", __exception);
    }
}