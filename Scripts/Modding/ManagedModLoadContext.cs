using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace DealOrForce.Modding;

/// <summary>
/// Load context for one compiled mod, collectible so <see cref="ModLoader.StopCode"/> can drop it
/// between campaigns. The game assembly and the Godot assembly are handed back from the host,
/// which is what keeps <see cref="IGlobalityMod"/> the same type the mod was compiled against.
/// Shipping a private copy of the game dll inside the mod makes the interface a different type
/// and the mod then loads as if it implemented nothing.
///
/// Other dependencies, including <c>FSharp.Core</c>, resolve privately: first from the assembly's
/// deps file, then from a sibling dll in the same folder. A mod may ship those siblings.
/// This does not sandbox the code. A compiled mod is trusted.
/// </summary>
public sealed class ManagedModLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;

    public ManagedModLoadContext(string assemblyPath) : base(isCollectible: true)
    {
        _directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    /// <summary>Share the host contract. Load everything else from the package, not from a second copy of the game.</summary>
    protected override Assembly Load(AssemblyName name)
    {
        foreach (var shared in new[] { typeof(IGlobalityMod).Assembly, typeof(Godot.Node).Assembly })
            if (AssemblyName.ReferenceMatchesDefinition(name, shared.GetName())) return shared;
        var path = _resolver.ResolveAssemblyToPath(name);
        // Class-library mods may not have a deps.json; support explicitly shipped sibling DLLs.
        var sibling = Path.Combine(_directory, name.Name + ".dll");
        if (path is null && File.Exists(sibling)) path = sibling;
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <summary>Native dependencies stay inside the resolver's graph. A missing one fails the load; it is not searched on PATH.</summary>
    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
