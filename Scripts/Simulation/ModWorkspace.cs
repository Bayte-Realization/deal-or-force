using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed record ModWorkspaceEntry(string Id, string Name, string Root);

/// <summary>Editor-owned data packages. Creating/selecting one never enables code or
/// changes the live campaign. Same manifest and game-folder layout as the real loader.</summary>
public static class ModWorkspace
{
    private static readonly string[] DeviceNames = { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" };
    public static bool ValidId(string id) => !string.IsNullOrEmpty(id) && id.Length <= 64 &&
        id[0] is >= 'a' and <= 'z' && !DeviceNames.Contains(id, StringComparer.OrdinalIgnoreCase) &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    public static ModWorkspaceEntry Create(string modsRoot, string id, string name)
    {
        if (!ValidId(id)) throw new InvalidDataException("Use a lowercase mod ID: letters, digits and underscores, starting with a letter.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new InvalidDataException("Enter a mod name (1–120 characters).");
        var target = ScenarioLibrary.SafePath(modsRoot, id);
        ContentWorkbench.RejectLinks(target);
        if (Directory.Exists(target) || File.Exists(target))
            throw new InvalidDataException("This mod folder already exists. Select it instead; nothing was overwritten.");
        Directory.CreateDirectory(modsRoot);
        // Assemble privately, then publish the complete folder with an atomic rename.
        // A collision must not overwrite another editor's manifest or files.
        var staging = ScenarioLibrary.SafePath(modsRoot, "." + id + "." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new
            {
                id, name = name.Trim(), version = "1.0.0", enabled = false, load_order = 10000,
                supported_game_version = GameBuildInfo.Version, description = "Created with the in-game workshop."
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var path in new[] { "common", "events", "gfx", "gui", "localization/english", "localization/russian" })
                Directory.CreateDirectory(Path.Combine(staging, path));
            Directory.Move(staging, target);
        }
        finally
        {
            // Only our never-published staging folder; never the destination/user package.
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        return new(id, name.Trim(), target);
    }

    public static IReadOnlyList<ModWorkspaceEntry> Discover(string modsRoot)
    {
        var result = new List<ModWorkspaceEntry>();
        if (!Directory.Exists(modsRoot)) return result;
        ContentWorkbench.RejectLinks(Path.Combine(modsRoot, "manifest.json"));
        foreach (var folder in Directory.EnumerateDirectories(modsRoot).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (Path.GetFileName(folder).StartsWith('_') || Path.GetFileName(folder).StartsWith('.')) continue;
            try
            {
                var manifest = Path.Combine(folder, "manifest.json");
                ContentWorkbench.RejectLinks(manifest);
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1024 * 1024) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(manifest), GameDataLoader.ForgivingJson);
                var root = document.RootElement;
                var id = root.GetProperty("id").GetString();
                var name = root.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name)) result.Add(new(id, name, folder));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
            { DiagnosticLogs.Write("errors", "WORKSPACE_SKIPPED " + Path.GetFileName(folder) + ": " + e.Message); }
        }
        return result;
    }
}
