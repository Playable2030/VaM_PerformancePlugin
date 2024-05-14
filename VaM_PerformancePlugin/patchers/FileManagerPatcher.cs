using System.Collections.Generic;
using Mono.Cecil;

namespace VaM_PerformancePlugin.patchers;

// WIP Preloader patch
// Not used
public static class FileManagerPatcher
{
    // List of assemblies to patch
    public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

    // Patches the assemblies
    public static void Patch(AssemblyDefinition assembly)
    {
        // assembly.MainModule.Types.GetType()
        // Patcher code here
    }

    // Called before patching occurs
    // public static void Initialize();

    // Called after preloader has patched all assemblies and loaded them in
    // At this point it is fine to reference patched assemblies
    // public static void Finish();
}