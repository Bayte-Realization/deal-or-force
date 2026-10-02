using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed record ScenarioEntry(string Id, string Name, string Description, string WorldFile, string ManifestFile = "");

public static class ScenarioLibrary
{
    public static IReadOnlyList<ScenarioEntry> Discover(IEnumerable<string> packageRoots, string localRoot)
    {
        var found = new Dictionary<string, ScenarioEntry>(StringComparer.OrdinalIgnoreCase);
        var baseRoot = GameDataLoader.DataFolder("scenarios");
        foreach (var root in new[] { baseRoot }.Concat(packageRoots.Select(p => Path.Combine(p, "scenarios"))).Append(localRoot))
        {
            if (root is null || !Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "scenario.json", SearchOption.AllDirectories).OrderBy(p => p))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
                    var r = doc.RootElement;
                    var id = r.GetProperty("id").GetString();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var path = r.TryGetProperty("world", out var w) ? SafePath(Path.GetDirectoryName(file), w.GetString()) : "";
                    if (id != "2024" && !File.Exists(path)) continue;
                    found[id] = new(id, r.GetProperty("name").GetString(), r.TryGetProperty("description", out var d) ? d.GetString() : "", path, file);
                }
                catch (Exception e) { DiagnosticLogs.Write("errors", $"SCENARIO_REJECTED {file}: {e.Message}"); }
            }
        }
        return found.Values.OrderBy(e => e.Id == "2024" ? "" : e.Name).ToArray();
    }

    public static Campaign Create(ScenarioEntry entry, int seed, int expectedProvinceCount = 0) => entry.Id == "2024" && entry.WorldFile.Length == 0
        ? ScenarioCatalog.CreateBaseCampaign(seed) : SaveSystem.ImportScenarioJson(File.ReadAllText(entry.WorldFile), entry.Id, expectedProvinceCount, seed);

    public static string SafePath(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative ?? ""));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path leaves its package.");
        return path;
    }
}
