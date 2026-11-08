using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Gives a campaign saved before the world's resources existed the fields, mines and fabs it
/// would have had.
///
/// Deposits are part of a save: every region carries what lies under its ground. A campaign
/// saved before history/states/2024/zz_world_resources.json was written therefore has no
/// grain plains, no fab clusters and no potash anywhere - loaded as it is, the world would
/// open in a famine of bread and a drought of chips that nothing it does could end, because
/// a region gains deposits only from the file that seeded the first day.
///
/// So on load, and only for such a save, the file is read again and every region gets what
/// the file gives it: by its own id where it still has one; a region cut out of another in
/// play (StatePartition, which keeps ParentMapIndex) shares its original's amounts with the
/// rest of that family in proportion to the people living in each piece, as the cut itself
/// shared them. A save is recognised as old when not one region anywhere holds any of the
/// goods the file names - a campaign that was ever seeded from it always has grain somewhere,
/// however much war and partition have moved it about. Only the 2024 bookmark is touched;
/// an authored scenario's regions answer to its own data, not to 2024's.
///
/// A fresh campaign never comes here, so nothing about a new game or its fingerprint moves.
/// </summary>
public static class WorldResourceMigration
{
    /// <summary>The overlay file whose goods a save may predate.</summary>
    public const string OverlayFile = "zz_world_resources.json";

    /// <summary>Region id to its deposits, as the overlay writes them; empty when the file is missing.</summary>
    public static Dictionary<string, Dictionary<Commodity, float>> ReadOverlay()
    {
        var table = new Dictionary<string, Dictionary<Commodity, float>>(StringComparer.OrdinalIgnoreCase);
        var path = GameDataLoader.DataFiles("history", "states", "2024")
            .FirstOrDefault(file => string.Equals(Path.GetFileName(file), OverlayFile, StringComparison.OrdinalIgnoreCase));
        if (path is null) return table;
        try
        {
            using var document = JsonDocument.Parse(StructuredData.ReadJson(path), GameDataLoader.ForgivingJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return table;
            foreach (var region in document.RootElement.EnumerateObject())
            {
                if (region.Value.ValueKind != JsonValueKind.Object ||
                    !region.Value.TryGetProperty("resources", out var resources) ||
                    resources.ValueKind != JsonValueKind.Object) continue;
                var goods = new Dictionary<Commodity, float>();
                foreach (var resource in resources.EnumerateObject())
                    if (CommodityCatalog.TryParse(resource.Name, out var good) &&
                        resource.Value.ValueKind == JsonValueKind.Number &&
                        resource.Value.TryGetSingle(out var amount) && float.IsFinite(amount) && amount > 0f)
                        goods[good] = amount;
                if (goods.Count > 0) table[region.Name] = goods;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // A broken overlay seeds nothing; HistoryDataLoader reports the same file on a new game.
        }
        return table;
    }

    /// <summary>
    /// Seeds the overlay's deposits into a campaign that has none of them. Returns the number of
    /// regions that received any; zero when the campaign did not need it.
    /// </summary>
    public static int Apply(Campaign campaign) => Apply(campaign, ReadOverlay());

    public static int Apply(Campaign campaign, Dictionary<string, Dictionary<Commodity, float>> overlay)
    {
        if (campaign is null || overlay is null || overlay.Count == 0) return 0;
        if (!string.Equals(campaign.ScenarioId, "2024", StringComparison.Ordinal)) return 0;
        var goods = new HashSet<Commodity>(overlay.Values.SelectMany(table => table.Keys));
        if (campaign.States.Any(state => state.ResourceDeposits.Any(pair => goods.Contains(pair.Key) && pair.Value > 0f)))
            return 0;

        // Which 2024 region each region of the save descends from: itself, or the original a
        // partition cut it from - found by map index while the original still stands, and by
        // the id the cut was named after ("{original}_{mapIndex}") once it is gone.
        var byMapIndex = campaign.States.ToDictionary(state => state.MapIndex);
        // By id; by ISO code only where no region carries the overlay's key as its id, because
        // several regions can share one ISO code (Madagascar's three Toamasinas) and the file
        // means the one whose id it names, as HistoryDataLoader reads it.
        var ids = new HashSet<string>(campaign.States.Select(state => state.Id), StringComparer.OrdinalIgnoreCase);
        string Key(ScenarioState state)
        {
            if (overlay.ContainsKey(state.Id)) return state.Id;
            var iso = (state.Iso ?? "").Replace('-', '_');
            return iso.Length > 0 && overlay.ContainsKey(iso) && !ids.Contains(iso) ? iso : null;
        }
        string RootKey(ScenarioState state)
        {
            if (state.ParentMapIndex == 0) return Key(state);
            if (byMapIndex.TryGetValue(state.ParentMapIndex, out var original) && Key(original) is { } found) return found;
            var id = state.Id;
            for (var cut = id.LastIndexOf('_'); cut > 0; cut = id.LastIndexOf('_'))
            {
                id = id[..cut];
                if (overlay.ContainsKey(id)) return id;
            }
            return Key(state);
        }

        var families = new Dictionary<string, List<ScenarioState>>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in campaign.States)
        {
            if (RootKey(state) is not { } key) continue;
            if (!families.TryGetValue(key, out var members)) families[key] = members = new List<ScenarioState>();
            members.Add(state);
        }

        var seeded = 0;
        foreach (var (key, members) in families)
        {
            var people = members.Sum(member => (double)Math.Max(0L, member.Population));
            foreach (var member in members)
            {
                var share = people > 0 ? Math.Max(0L, member.Population) / people : 1.0 / members.Count;
                if (share <= 0) continue;
                foreach (var (good, amount) in overlay[key])
                    member.ResourceDeposits[good] = (float)(amount * share);
                seeded++;
            }
        }
        return seeded;
    }
}
