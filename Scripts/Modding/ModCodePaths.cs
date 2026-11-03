using System;
using System.IO;
using System.Linq;

namespace DealOrForce.Modding;

/// <summary>
/// Where a compiled mod's assembly is allowed to live.
/// New mods set <c>corecode_assembly</c> to a path under <c>corecode/</c> inside the package.
/// An older mod that only set <c>csharp_assembly</c> is still loaded, from anywhere inside
/// the package except a rooted path or <c>..</c>. Both must name a <c>.dll</c>.
/// F# mods use the same field; <see cref="ManagedModLoadContext"/> then loads <c>FSharp.Core</c>
/// beside the game assembly so <see cref="IGlobalityMod"/> stays one type.
/// A path outside the package is refused. The loader does not search the game's own bin folder.
/// </summary>
public static class ModCodePaths
{
    /// <summary>
    /// Returns the relative dll path, or empty when the manifest names no assembly.
    /// Throws <see cref="InvalidDataException"/> when the path escapes the package or is not a dll.
    /// </summary>
    public static string AssemblyPath(string corecode, string legacy)
    {
        var modern = !string.IsNullOrWhiteSpace(corecode);
        var path = (modern ? corecode : legacy)?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (Path.IsPathRooted(path) || path.Contains(':') || path.Split('/').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("Code assembly must stay inside the mod directory.");
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Code assembly must be a compiled .dll.");
        if (modern && !path.StartsWith("corecode/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("corecode_assembly must point inside corecode/.");
        return path;
    }
}
