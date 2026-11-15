using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// An era overlay: a different look for the same interface, chosen by a scenario or a mod with
/// data alone. The base skin is deliberately era-neutral; what makes a campaign read as antiquity
/// or as the far future is a folder laid out like a mod -
/// <c>skins/&lt;id&gt;/{interface,gui,gfx}</c> with a <c>skin.json</c> naming it - that swaps colour
/// tokens, materials, icon roles and pictures, and nothing else.
///
/// Layer order: the base game's <c>interface</c>/<c>gui</c>/<c>gfx</c>, then every mod's, then
/// <c>game/skins/&lt;id&gt;</c>, then <c>&lt;mod&gt;/skins/&lt;id&gt;</c> in mod order - so a mod
/// can both add its own overlay and correct someone else's. A skin.json may name a parent with
/// <c>inherits</c>; the parent is layered first.
///
/// Which overlay is active is decided by <see cref="Choose"/>: the scenario's <c>skin</c>, unless the
/// player's <c>skin_overlay</c> setting says <c>none</c> (always the base look - an accessibility
/// promise) or names an overlay of their own.
/// </summary>
public static class SkinOverlays
{
    public const string SettingKey = "skin_overlay";

    /// <summary>The overlay layered on top of base and mods right now; empty for the base look.</summary>
    public static string Active { get; private set; } = "";

    /// <summary>Called whenever the base skin is reloaded: whatever was layered on top is gone.</summary>
    internal static void MarkBase() => Active = "";

    /// <summary>
    /// The overlay to use: <paramref name="setting"/> <c>none</c> means the base look, <c>scenario</c>
    /// (or nothing) means what the scenario asked for, and any other value is an overlay id the
    /// player chose themselves.
    /// </summary>
    public static string Choose(string scenarioSkin, string setting)
    {
        setting = (setting ?? "").Trim();
        if (setting.Equals("none", StringComparison.OrdinalIgnoreCase)) return "";
        if (setting.Length == 0 || setting.Equals("scenario", StringComparison.OrdinalIgnoreCase))
            return (scenarioSkin ?? "").Trim();
        return setting;
    }

    /// <summary>Every overlay id the base game and the given packages ship, sorted.</summary>
    public static IReadOnlyList<string> Discover(string baseSkinsRoot, IEnumerable<string> packageRoots)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { baseSkinsRoot }.Concat((packageRoots ?? Array.Empty<string>())
                     .Select(p => p is null ? null : Path.Combine(p, "skins"))))
        {
            if (root is null || !Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
                found.Add(Path.GetFileName(directory));
        }
        return found.ToArray();
    }

    /// <summary>
    /// Layer overlay <paramref name="id"/> over what is loaded now (base plus mods). An empty id
    /// does nothing. An id nobody ships is reported - in <paramref name="errors"/> and in the error
    /// log - and the base look stays: a scenario naming a missing overlay still plays.
    /// Returns the id applied, or empty.
    /// </summary>
    public static string Apply(string id, string baseSkinsRoot,
        IEnumerable<(string Physical, string Virtual)> packages, List<string> errors)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0) return "";
        var packageList = (packages ?? Array.Empty<(string, string)>()).ToList();
        var layers = new List<(string Directory, string Virtual)>();
        Collect(id, baseSkinsRoot, packageList, layers, new HashSet<string>(StringComparer.OrdinalIgnoreCase), errors);
        if (layers.Count == 0)
        {
            var message = $"skin '{id}' is not shipped by the game or any active mod; the base interface is used.";
            errors?.Add(message);
            DiagnosticLogs.Write("errors", "SKIN_UNKNOWN " + message);
            return "";
        }

        foreach (var (directory, virtualRoot) in layers)
        {
            InterfaceSkin.LoadDirectory(Path.Combine(directory, "interface"), errors);
            InterfaceSkin.LoadDirectory(Path.Combine(directory, "gui"), errors);
            SpriteCatalog.LoadTree(Path.Combine(directory, "gfx"), virtualRoot + "/gfx", errors);
        }
        Active = id;
        return id;
    }

    /// <summary>
    /// Reload base and mods, then layer <paramref name="id"/>. Used when the overlay changes
    /// between campaigns: an overlay is only ever added on top, so taking one away is a rebuild.
    /// </summary>
    public static string Rebuild(string id, string baseSkinsRoot,
        IEnumerable<(string Physical, string Virtual)> packages, List<string> errors)
    {
        var packageList = (packages ?? Array.Empty<(string, string)>()).ToList();
        InterfaceSkin.ResetCatalog(errors);
        SpriteCatalog.Load();
        foreach (var (physical, virtualRoot) in packageList)
        {
            InterfaceSkin.LoadDirectory(Path.Combine(physical, "interface"), errors);
            InterfaceSkin.LoadDirectory(Path.Combine(physical, "gui"), errors);
            SpriteCatalog.LoadTree(Path.Combine(physical, "gfx"), virtualRoot + "/gfx", errors);
        }
        return Apply(id, baseSkinsRoot, packageList, errors);
    }

    private static void Collect(string id, string baseSkinsRoot, List<(string Physical, string Virtual)> packages,
        List<(string, string)> layers, HashSet<string> visited, List<string> errors)
    {
        if (!visited.Add(id) || visited.Count > 8) return;
        var own = new List<(string Directory, string Virtual)>();
        if (baseSkinsRoot is not null && Directory.Exists(Path.Combine(baseSkinsRoot, id)))
            own.Add((Path.Combine(baseSkinsRoot, id), "res://game/skins/" + id));
        foreach (var (physical, virtualRoot) in packages)
        {
            var directory = Path.Combine(physical ?? "", "skins", id);
            if (Directory.Exists(directory)) own.Add((directory, $"{virtualRoot}/skins/{id}"));
        }
        if (own.Count == 0) return;

        // The parent goes underneath, so this overlay only has to say what it changes.
        foreach (var (directory, _) in own)
        {
            var parent = Inherits(Path.Combine(directory, "skin.json"), errors);
            if (parent.Length > 0) Collect(parent, baseSkinsRoot, packages, layers, visited, errors);
        }
        layers.AddRange(own);
    }

    private static string Inherits(string manifest, List<string> errors)
    {
        if (!File.Exists(manifest)) return "";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest), GameDataLoader.ForgivingJson);
            return document.RootElement.TryGetProperty("inherits", out var parent) && parent.ValueKind == JsonValueKind.String
                ? parent.GetString()?.Trim() ?? ""
                : "";
        }
        catch (Exception exception)
        {
            errors?.Add($"{manifest}: {exception.Message}");
            return "";
        }
    }
}
