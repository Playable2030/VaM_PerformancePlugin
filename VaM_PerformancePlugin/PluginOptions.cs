using BepInEx.Configuration;

namespace VaM_PerformancePlugin;

public class PluginOptions
{
    internal ConfigEntry<bool> Enabled { get; }
    internal ConfigEntry<bool> EnabledFileManager { get; }
    internal ConfigEntry<bool> EnabledGlobalStopwatch { get; }

    public PluginOptions(ConfigFile config)
    {
        this.Enabled = config.Bind("General", "Enabled", true, "Whether or not this plugin is enabled");
        this.EnabledFileManager = config.Bind("General.FileManager", "Enabled", true,
            "Whether or not the patch for FileManager aka Addon Files");
        this.EnabledGlobalStopwatch = config.Bind("General.GlobalStopwatch", "Enabled", true,
            "Whether the patch to disable the GlobalStopwatch is enabled. This is used for performance monitoring inside VaM");
        
    }
}