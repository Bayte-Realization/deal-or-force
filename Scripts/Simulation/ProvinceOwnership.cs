using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Who owns each province, as data.
///
/// The province *geometry* has been data for a long time - `provinces.png` and
/// `definition.csv` between them say where every one of the 37 807 provinces is. Who owns
/// them was not: ownership came out of a control raster compiled into the executable, so a
/// mod could redraw the shape of the world and not one border on it.
///
/// This is the other half, and it is the shape Paradox titles use: a `history/provinces`
/// folder saying who owns and who holds each province. The base game ships the full map;
/// a mod ships only the provinces it changes, layered over it in the ordinary load order.
/// Everything else - the economy, the fronts, the peace conference, the AI - already reads
/// ownership from one place, so moving one line in this file moves all of them.
///
/// Region membership deliberately stays geometric. A province is grown inside one region
/// and never crosses its border, so which region it belongs to is a fact about its shape
/// rather than about its politics; letting a file claim otherwise would produce regions
/// made of ground that does not touch.
/// </summary>
public static class ProvinceOwnership
{
    /// <summary>One province's political standing.</summary>
    public readonly record struct Entry(int Province, string Owner, string Controller);

    private static readonly Dictionary<int, Entry> Entries = new();

    // Load on first use, the way every other catalogue does. Waiting for the mod loader
    // meant the headless bench - which has no mod loader - ran on a political map that had
    // never been read, and reported that the file was missing when it was sitting there.
    static ProvinceOwnership() => ResetCatalog();

    /// <summary>Whether any file has been loaded at all.</summary>
    public static bool HasData => Entries.Count > 0;

    public static int Count => Entries.Count;

    /// <summary>
    /// Forget the political map entirely.
    ///
    /// The map compiler needs this. It builds the province map from the vector sources and
    /// then exports the political map from the result - but attaching a topology applies
    /// whatever ownership file is already on disk, which was written against the *previous*
    /// numbering. Recompiling after any change to the province count therefore laid the old
    /// map over the new provinces, scrambled every border, and wrote the scrambling back
    /// out. Three recompiles in, half the world's provinces belonged to the wrong country:
    /// Kentucky was Chinese, Ashgabat was American, and the interface faithfully reported
    /// it. Existing outputs are outputs, not inputs.
    /// </summary>
    public static void Clear() => Entries.Clear();

    /// <summary>Reload the base-game political map. Mods layer on top afterwards.</summary>
    public static void ResetCatalog()
    {
        Entries.Clear();
        var directory = FindDirectory();
        if (directory is not null) LoadDirectory(directory, null);
    }

    /// <summary>Layer every `*.csv` in one `history/provinces` directory over what is loaded.</summary>
    public static void LoadDirectory(string directory, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.csv", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try { Merge(File.ReadAllText(file), $"history/provinces/{Path.GetFileName(file)}", errors); }
            catch (Exception exception) { errors?.Add($"history/provinces/{Path.GetFileName(file)}: {exception.Message}"); }
        }
    }

    /// <summary>
    /// Merge one file. The format is `province;owner;controller`, semicolon-separated the
    /// way `definition.csv` already is, with `#` comments. A missing controller means the
    /// owner holds it, which is the ordinary case and not worth writing out 37 000 times.
    /// </summary>
    public static void Merge(string text, string context, List<string> errors)
    {
        if (string.IsNullOrEmpty(text)) return;
        var line = 0;
        foreach (var raw in text.Split('\n'))
        {
            line++;
            var row = raw.Trim();
            if (row.Length == 0 || row[0] == '#') continue;

            var parts = row.Split(';');
            if (parts.Length < 2)
            {
                errors?.Add($"{context}:{line}: expected province;owner[;controller]");
                continue;
            }
            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var province) ||
                province <= 0)
            {
                // The first line is usually a header; only complain about the rest.
                if (line > 1) errors?.Add($"{context}:{line}: '{parts[0]}' is not a province id");
                continue;
            }

            var owner = parts[1].Trim();
            if (owner.Length == 0)
            {
                errors?.Add($"{context}:{line}: province {province} has no owner");
                continue;
            }
            var controller = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : owner;
            Entries[province] = new Entry(province, owner, controller);
        }
    }

    /// <summary>
    /// Write the political map back out. The compiler uses this to ship the base game's
    /// own file, so what a modder edits is exactly what the game runs on rather than a
    /// hand-made approximation of it.
    /// </summary>
    public static string Export(Campaign campaign, ProvinceTopology topology, out int owned)
    {
        owned = 0;
        var tags = campaign.Nations.ToDictionary(nation => nation.MapIndex, nation => nation.Tag);
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("# Deal or Force - the political map, one line per province.");
        builder.AppendLine("#");
        builder.AppendLine("# province;owner;controller   (controller may be omitted when the owner holds it)");
        builder.AppendLine("#");
        builder.AppendLine("# A mod lists only the provinces it changes and drops the file into its own");
        builder.AppendLine("# history/provinces folder; it is layered over this one in load order.");
        builder.AppendLine("province;owner;controller");

        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (!tags.TryGetValue(topology.ProvinceToNation[province], out var owner)) continue;
            var controllerIndex = campaign.ProvinceController is null
                ? topology.ProvinceToNation[province]
                : campaign.ProvinceController[province];
            var controller = tags.TryGetValue(controllerIndex, out var held) ? held : owner;
            owned++;
            builder.Append(province).Append(';').Append(owner);
            if (!string.Equals(controller, owner, StringComparison.Ordinal))
                builder.Append(';').Append(controller);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    /// <summary>
    /// Apply the loaded political map to a freshly built topology. Provinces the file does
    /// not mention keep whatever the raster gave them, so a mod that moves one border does
    /// not have to restate the other thirty-seven thousand.
    /// </summary>
    public static int Apply(Campaign campaign, ProvinceTopology topology, ushort[] controllers)
    {
        if (Entries.Count == 0 || topology is null) return 0;
        var byTag = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        foreach (var nation in campaign.Nations) byTag[nation.Tag] = (ushort)nation.MapIndex;

        var applied = 0;
        foreach (var entry in Entries.Values)
        {
            if (entry.Province <= 0 || entry.Province > topology.ProvinceCount) continue;
            if (!byTag.TryGetValue(entry.Owner, out var owner)) continue;
            topology.ProvinceToNation[entry.Province] = owner;
            if (controllers is not null && entry.Province < controllers.Length)
                controllers[entry.Province] = byTag.TryGetValue(entry.Controller, out var held) ? held : owner;
            applied++;
        }
        return applied;
    }

    private static string FindDirectory()
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "game", "history", "provinces");
            if (Directory.Exists(directory)) return directory;
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 7 && directory is not null; depth++, directory = directory.Parent)
            yield return directory.FullName;
    }
}
