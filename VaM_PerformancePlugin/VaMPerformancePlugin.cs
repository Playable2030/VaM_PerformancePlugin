using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

using VaM_PerformancePlugin.VaM;
using VaM_PerformancePlugin.VaM.FileManagement;

namespace VaM_PerformancePlugin;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
[BepInProcess("VaM.exe")]
[BepInProcess("sottr.exe")]
public class VaMPerformancePlugin : BaseUnityPlugin
{
    private static readonly string HarmonyId = PluginInfo.PLUGIN_GUID;
    public static PluginOptions Options { get; private set; }
    public static ManualLogSource PluginLogger { get; private set; }
    private Harmony _harmony;

    private void Awake()
    {
        // Plugin startup logic
        Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} is loaded!");

        PluginLogger = Logger;
        Options = new PluginOptions(Config);

        Logger.LogDebug($"Initialization finished");

        Logger.LogDebug($"Beginning patching...");

        // FileManagerPatcher.Patch();
        // Harmony.CreateAndPatchAll(typeof(MVR.FileManagement.FileManager));
        // PatchAll

        if (!Options.Enabled.Value)
        {
            Logger.LogDebug($"Aborting patching due to {Options.Enabled.Definition} setting");
            return;
        }

        _harmony = new Harmony(HarmonyId);

        // alphabetical order
        _harmony.PatchAll(typeof(AtomPatch));
        _harmony.PatchAll(typeof(DAZSkinV2Patch));

        if (Options.EnabledFileManager.Value)
        {
            _harmony.PatchAll(typeof(FileManagerPatch));
        }
        else
        {
            Logger.LogDebug(
                $"Not patching {typeof(FileManagerPatch)}due to {Options.EnabledFileManager.Definition} setting");
        }

        _harmony.PatchAll(typeof(GenerateDAZMorphsControlUIPatch));

        if (Options.EnabledFileManager.Value)
        {
            _harmony.PatchAll(typeof(GlobalStopwatchPatch));
        }
        else
        {
            Logger.LogDebug(
                $"Not patching {typeof(GlobalStopwatchPatch)}due to {Options.EnabledGlobalStopwatch.Definition} setting");
        }

        // _harmony.PatchAll(typeof(ImageLoaderThreadedPatch));
        _harmony.PatchAll(typeof(LocalizatronPatch));
        _harmony.PatchAll(typeof(SuperControllerPatch));
        _harmony.PatchAll(typeof(UnityThreadHelperPatch));

        Logger.LogDebug($"End patching...");
    }
}