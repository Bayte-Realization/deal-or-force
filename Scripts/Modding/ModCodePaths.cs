using System;
using System.IO;
using System.Linq;

namespace DealOrForce.Modding;

/// <summary>New compiled-code mods live in corecode. Old explicit assembly paths remain readable.</summary>
public static class ModCodePaths
{
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
