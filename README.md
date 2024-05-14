VaM_PerformancePlugin
===
This is a [BepInEx](https://github.com/BepInEx/BepInEx) 5.X plugin for [Virt-a-Mate](https://hub.virtamate.com/) (aka VaM) to improve performance when dealing with a large number of files.
This is achieved by unrolling code + more performant non-functional refactors.

Currently, it's only HarmonyX runtime patches, in the future I may look into patching via a preloader to improve load performance, as time permits.
For now, there should be plenty of benefits achieved alone with just HarmonyX.

Be warned, this is intentionally licensed under GNU GPLv3, so it cannot be included as part of a commercial offering.

# Requirements
- Windows
- DotNet 8.0+ - https://dotnet.microsoft.com/en-us/download
- IDE - I use [Rider](https://www.jetbrains.com/rider/), but VSCode/VS Community should work fine (but are untested)
- Virt-a-Mate v1.22.0.3

# Building
This assumes BepInEx 5 was installed.

Replace `~/VaM` with wherever VaM is installed
Replace `~/git` with wherever you cloned the repository
```shell
cd ~/git/VaM_PerformancePlugin/

# We need to copy over the DLL containing the high-level game code
# it's reference in the plugin via reflection, for build-time type safety checks
cp ~/VaM/VaM_Data/Managed/Assembly-CSharp.dll ~/git/VaM_PerformancePlugin/VaM_PerformancePlugin/lib/VaM_Data/Managed/Assembly-CSharp.dll

# Now we can build without errors
# initial build may take a bit while dependencies are downloaded from NuGet
dotnet build -c Release

# copy plugin to install it
mkdir -p ~/VaM/BepInEx/plugins/VaM_PerformancePlugin/
cp ~/git/VaM_PerformancePlugin/VaM_PerformancePlugin/bin/Release/net35/VaM_PerformancePlugin.dll ~/VaM/BepInEx/plugins/VaM_PerformancePlugin/
```

# Installing