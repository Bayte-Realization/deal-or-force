using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Modding;

/// <summary>
/// The player's list of which mods and content packs are on, and the mod order.
/// Shared by the game and the standalone launcher, with no Godot type in this file,
/// so the launcher can show the same order the loader will use.
/// Schema version 1. A missing file means every package keeps its manifest default.
/// This file does not store campaign state and is not part of the save or the fingerprint.
/// </summary>
public sealed class ContentSelection
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, bool> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> Dlc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ModOrder { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip, WriteIndented = true
    };

    public static ContentSelection Load(string path)
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<ContentSelection>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Empty content selection.");
        if (value.SchemaVersion != 1 || value.Mods is null || value.Dlc is null || value.ModOrder is null)
            throw new InvalidDataException("Unsupported or invalid content selection.");
        value.Mods = new(value.Mods, StringComparer.OrdinalIgnoreCase);
        value.Dlc = new(value.Dlc, StringComparer.OrdinalIgnoreCase);
        return value;
    }

    // Writes this profile only. Never rewrites a mod manifest, a dlc.json, or entitlements.
    // Atomic replacement keeps the previous file as a .bak if the replace fails halfway.
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, this, JsonOptions);
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Launcher choice when the profile names this id; otherwise the manifest's own enabled flag.</summary>
    public bool IsEnabled(string id, bool dlc, bool defaultValue = true) =>
        (dlc ? Dlc : Mods).TryGetValue(id, out var enabled) ? enabled : defaultValue;

    /// <summary>Index in <see cref="ModOrder"/>, or the manifest load_order when the player has not arranged this id.</summary>
    public int Order(string id, int fallback)
    {
        var index = ModOrder.FindIndex(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? fallback : index;
    }
}

public sealed record ContentRequirement(string Id, bool Enabled, IReadOnlyList<string> Dependencies);

/// <summary>
/// One dependency walk for the launcher preview and the runtime loader.
/// A cycle, a missing id, or a dependency the player turned off drops the package and records why.
/// The returned list is the order later packages overlay earlier ones. Do not sort it again by file name.
/// </summary>
public static class ContentDependencies
{
    // DFS rejects cycles and propagates missing/disabled dependencies through every level.
    // The resulting order is shared by the launcher preview and the runtime loader.
    public static List<string> Resolve(IEnumerable<ContentRequirement> entries, out Dictionary<string, string> errors)
    {
        var ordered = entries.ToList();
        var byId = ordered.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        bool Visit(string id)
        {
            if (!byId.TryGetValue(id, out var item) || !item.Enabled) return false;
            if (state.TryGetValue(id, out var status))
            {
                if (status == 1) failures[id] = "Dependency cycle / цикл зависимостей";
                return status == 2 && !failures.ContainsKey(id);
            }
            state[id] = 1;
            foreach (var dependency in item.Dependencies)
                if (!Visit(dependency)) failures[id] = "Unavailable dependency / недоступна зависимость: " + dependency;
            state[id] = 2;
            if (failures.ContainsKey(id)) return false;
            result.Add(id);
            return true;
        }
        foreach (var item in ordered) Visit(item.Id);
        errors = failures;
        return result;
    }
}
