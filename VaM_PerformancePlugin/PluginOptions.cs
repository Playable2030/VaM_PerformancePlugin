using BepInEx.Configuration;

namespace VaM_PerformancePlugin;

public class PluginOptions
{
    internal ConfigEntry<bool> Enabled { get; }
    internal ConfigEntry<bool> EnabledFileManager { get; }
    internal ConfigEntry<bool> EnabledGlobalStopwatch { get; }
    internal ConfigEntry<bool> EnabledImageLoaderPatch { get; }
    internal ConfigEntry<bool> Character_onlyShowActive { get; }
    internal ConfigEntry<bool> Character_onlyShowFavorites { get; }
    internal ConfigEntry<bool> Character_onlyShowLatest { get; }


    public PluginOptions(ConfigFile config)
    {
        // perf patch options
        Enabled = config.Bind("General", "Enabled", true, "Whether or not this plugin is enabled");
        EnabledFileManager = config.Bind("General.FileManager", "Enabled", true,
            "Whether or not the patch for FileManager aka Addon Files");
        EnabledGlobalStopwatch = config.Bind("General.GlobalStopwatch", "Enabled", true,
            "Whether the patch to disable the GlobalStopwatch is enabled. This is used for performance monitoring inside VaM");
        EnabledImageLoaderPatch = config.Bind("General.ImageLoader", "Enabled", true,
            "Whether the patch to disable the ImageLoader is enabled.");


        // defaults for in-game options
        const string charMorphsDesc = "Default value for in-game menu: Character -> Clothing/Hair/Morphs";
        Character_onlyShowActive =
            config.Bind("Character.All", "OnlyShowActive", true, charMorphsDesc);
        Character_onlyShowFavorites = config.Bind("Character.All", "OnlyShowFavorites", false,
            charMorphsDesc);
        Character_onlyShowLatest = config.Bind("Character.All", "OnlyShowLatest", true, charMorphsDesc);
        
    }
}