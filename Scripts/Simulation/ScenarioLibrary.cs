using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed record ScenarioEntry(
    string Id,
    string Name,
    string Description,
    string WorldFile,
    string ManifestFile = "",
    string Skin = "",
    ScenarioManifest Manifest = null);

public static class ScenarioLibrary
{
    public static IReadOnlyList<ScenarioEntry> Discover(IEnumerable<string> packageRoots, string localRoot)
    {
        var found = new Dictionary<string, ScenarioEntry>(StringComparer.OrdinalIgnoreCase);
        var baseRoot = GameDataLoader.DataFolder("scenarios");
        foreach (var root in new[] { baseRoot }.Concat(packageRoots.Select(p => Path.Combine(p, "scenarios"))).Append(localRoot))
        {
            if (root is null || !Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "scenario.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!ScenarioManifestLoader.TryParseFile(file, out var manifest, out var parseErrors) && manifest is null)
                    {
                        DiagnosticLogs.Write("errors", $"SCENARIO_REJECTED {file}: {string.Join("; ", parseErrors)}");
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name)) continue;
                    var path = !string.IsNullOrWhiteSpace(manifest.World)
                        ? ResolveWorldPath(Path.GetDirectoryName(file), manifest.World)
                        : "";
                    var usesBaseWorld = manifest.Id == "2024" || (manifest.BaseWorld && path.Length == 0);
                    if (!usesBaseWorld && !File.Exists(path)) continue;
                    found[manifest.Id] = new(
                        manifest.Id,
                        manifest.Name,
                        manifest.Description ?? "",
                        path,
                        file,
                        manifest.EffectiveSkin ?? "",
                        manifest);
                }
                catch (Exception e) { DiagnosticLogs.Write("errors", $"SCENARIO_REJECTED {file}: {e.Message}"); }
            }
        }
        return found.Values.OrderBy(e => e.Id == "2024" ? "" : e.Name).ToArray();
    }

    /// <summary>
    /// The era overlay scenario <paramref name="id"/> asks for, read from the manifest the picker
    /// would use: the base game's, unless a package ships one with the same id. Empty when the
    /// scenario names none or is not found.
    /// </summary>
    public static string SkinOf(string id, IEnumerable<string> packageRoots, string localRoot = null) =>
        Discover(packageRoots ?? Array.Empty<string>(), localRoot)
            .FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))?.Skin ?? "";

    public static Campaign Create(ScenarioEntry entry, int seed, int expectedProvinceCount = 0)
    {
        var manifest = entry.Manifest;
        if (manifest is null && !string.IsNullOrWhiteSpace(entry.ManifestFile) && File.Exists(entry.ManifestFile))
            ScenarioManifestLoader.TryParseFile(entry.ManifestFile, out manifest, out _);

        var buildFromBase = entry.WorldFile.Length == 0 &&
            (entry.Id == "2024" || (manifest?.BaseWorld ?? false));

        var campaign = buildFromBase
            ? ScenarioCatalog.CreateBaseCampaign(seed, entry.ManifestFile)
            : SaveSystem.ImportScenarioJson(File.ReadAllText(entry.WorldFile), entry.Id, expectedProvinceCount, seed);

        // The look the scenario asks for travels with the campaign and its saves (SkinOverlays).
        campaign.SkinOverlay = entry.Skin ?? "";

        // Apply declarative scenario rules (start date, custom calendar, era features, starting wars/alliances/truces)
        // when the manifest declares any (leaving the shipped 2024 scenario completely untouched).
        if (manifest is not null && (manifest.HasDeclarativeRules || !string.Equals(entry.Id, "2024", StringComparison.OrdinalIgnoreCase)))
        {
            var warnings = ScenarioRulesApplier.Apply(campaign, manifest);
            foreach (var warning in warnings)
                DiagnosticLogs.Write("errors", $"SCENARIO_RULE {entry.Id}: {warning}");
        }

        return campaign;
    }

    public static string ResolveWorldPath(string root, string relative)
    {
        var path = SafePath(root, relative);
        if (File.Exists(path) || string.IsNullOrWhiteSpace(relative)) return path;

        string matchedExt = null;
        string[] known = { SaveSystem.Extension, SaveSystem.PreviousExtension, SaveSystem.LegacyExtension };
        foreach (var ext in known)
        {
            if (relative.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                matchedExt = ext;
                break;
            }
        }
        if (matchedExt is null) return path;

        var stem = relative[..^matchedExt.Length];
        foreach (var ext in known)
        {
            if (string.Equals(ext, matchedExt, StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = SafePath(root, stem + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return path;
    }

    public static string SafePath(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative ?? ""));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path leaves its package.");
        return path;
    }
}
