using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace DealOrForce.Modding;

/// <summary>Private dependencies (including FSharp.Core); shared host contract retains type identity.</summary>
public sealed class ManagedModLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;
    public ManagedModLoadContext(string assemblyPath) : base(isCollectible: true)
    {
        _directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }
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
    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
