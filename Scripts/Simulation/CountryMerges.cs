using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Entries in the country table that are not countries, and whose ground they really are.
///
/// The country table is Natural Earth's, and Natural Earth draws everything with a status of its
/// own as a country of its own: Scarborough Reef, the Siachen Glacier, the UN buffer zone on
/// Cyprus, the British bases beside it, Bir Tawil, a dozen uninhabited islands, two leased bases.
/// None of them has a government. In the game they were countries all the same - each with a
/// line in the diplomacy list, a relation with every state on earth, a ruler, a ranking, and,
/// where the map gave them a province, a patch of ground with no army and nobody to talk to on it.
///
/// `common/country_merges/` says which entries these are and who holds their ground on the first
/// day (or that nobody does). The table is applied once, where the world is built, and the rule
/// is the same for every entry:
///
/// - the entry is never made a country. ScenarioCatalog reads the country table past it, so it
///   is not in the nation list at all - and so not in the country picker, the diplomacy list,
///   the rankings, the map's labels, anybody's relations, the AI's considerations or a save;
/// - its regions are its holder's from the first line of the world (GroundByMapIndex): the
///   Siachen region is one of India's regions before the economy is dealt out, not a country's
///   only region handed over afterwards. An entry merged into nobody leaves unclaimed land,
///   settled like any other (ColonisationCore);
/// - its provinces follow on the map: the raster still paints them in the entry's own colour,
///   and the political map repaints that colour as the holder's wherever it is laid
///   (ProvinceOwnership.Apply), so no province line has to be written for each.
///
/// Who else wants the ground is not this table's business. A dispute is a core or a claim on the
/// region, in history/states with every other one (LostLands).
///
/// Nothing is special-cased by tag in code: the table is data, a mod adds or removes a line, and
/// an entry a mod takes off the list is an ordinary row of the country table again.
///
/// The one thing that still knows these as countries is a save written before the table was
/// applied at the world's building: its nation list is its own, ids are positions in it, and
/// nothing can be taken out of the middle of it. IsLegacyEntry names what such a save still
/// carries so the lists that show every nation can leave it out.
/// </summary>
public static class CountryMerges
{
    /// <summary>The owner to write instead of a merged entry's tag when it means nobody.</summary>
    public const string Nobody = "none";

    private static readonly Dictionary<string, string> Owners = new(StringComparer.OrdinalIgnoreCase);

    static CountryMerges() => ResetCatalog();

    /// <summary>Every merged entry and the owner its ground goes to ("none" for nobody).</summary>
    public static IReadOnlyDictionary<string, string> All => Owners;

    /// <summary>Reload the table from `common/country_merges/`, base game and mods in load order.</summary>
    public static void ResetCatalog()
    {
        Owners.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "country_merges"))
            using (document)
                Merge(document.RootElement);
    }

    /// <summary>
    /// Layer one document over the table. `"into": ""` takes an entry off the list, so a mod
    /// that wants one of these to be a country after all writes one line rather than copying
    /// the file.
    /// </summary>
    public static void Merge(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (GameDataLoader.IsRemoved(entry.Value, $"common/country_merges:{entry.Name}"))
            {
                Owners.Remove(entry.Name);
                continue;
            }
            if (!entry.Value.TryGetProperty("into", out var into) || into.ValueKind != JsonValueKind.String) continue;
            var owner = into.GetString()?.Trim() ?? "";
            if (owner.Length == 0) Owners.Remove(entry.Name);
            else Owners[entry.Name] = owner;
        }
    }

    /// <summary>Whether the table names this tag as an entry that is not a country.</summary>
    public static bool IsMerged(string tag) => !string.IsNullOrEmpty(tag) && Owners.ContainsKey(tag);

    /// <summary>
    /// The tag whose ground a merged entry is, following a chain if one entry is merged into
    /// another (a mod merging Akrotiri into a merged "sovereign bases" entry), and stopping at
    /// the first tag that is not merged itself. "none" for nobody; the tag itself when it is not
    /// in the table.
    /// </summary>
    public static string OwnerOf(string tag)
    {
        var current = tag;
        for (var step = 0; step < 8 && current is not null && Owners.TryGetValue(current, out var next); step++)
            current = next;
        return current;
    }

    /// <summary>
    /// The table by the numbers the map is drawn in: the country-table index of every merged
    /// entry, and the index of the country that holds its ground - zero for nobody.
    ///
    /// The world is built from this rather than from tags because the map knows a country only
    /// by its index: a region row names its country by index, and the raster paints a province in
    /// it. An entry whose holder is not a row of the country table - a misspelt tag, a chain that
    /// never leaves the table - holds nobody's ground: the entry is not a country either way, and
    /// land with no owner is at least land somebody can settle, where land owned by a country
    /// that does not exist is land nothing can ever reach.
    ///
    /// Worked out when asked rather than kept: it is read once as a world is built and once as
    /// its map is laid, and both tables it is made of can be replaced by a mod between two worlds.
    /// </summary>
    public static Dictionary<int, int> GroundByMapIndex()
    {
        var ground = new Dictionary<int, int>();
        if (Owners.Count == 0) return ground;
        var indexByTag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<(int MapIndex, string Tag)>();
        foreach (var row in WorldMapTables.CountryRows)
        {
            var parts = row.Split('|');
            if (parts.Length < 10 || !int.TryParse(parts[0], out var mapIndex) || mapIndex <= 0) continue;
            var tag = ScenarioCatalog.CountryTag(parts);
            indexByTag.TryAdd(tag, mapIndex);
            rows.Add((mapIndex, tag));
        }
        foreach (var (mapIndex, tag) in rows)
        {
            if (!Owners.ContainsKey(tag)) continue;
            var holder = OwnerOf(tag);
            ground[mapIndex] = holder is not null && !Owners.ContainsKey(holder) &&
                               indexByTag.TryGetValue(holder, out var held)
                ? held
                : 0;
        }
        return ground;
    }

    /// <summary>
    /// The country-table index of a tag, whether or not the row became a country; zero when no
    /// row answers to it. For whatever has to speak of an entry that is not in the nation list -
    /// a check that its provinces went where the table says, a tool reading the raster.
    /// </summary>
    public static int MapIndexOf(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return 0;
        foreach (var row in WorldMapTables.CountryRows)
        {
            var parts = row.Split('|');
            if (parts.Length >= 10 && int.TryParse(parts[0], out var mapIndex) &&
                string.Equals(ScenarioCatalog.CountryTag(parts), tag, StringComparison.OrdinalIgnoreCase))
                return mapIndex;
        }
        return 0;
    }

    /// <summary>
    /// Whether this nation is one of the table's entries carried by a save from before the table
    /// was applied as the world is built: a reef or a glacier that was a "country" there, holds
    /// no ground and never will. A new campaign has none. A movement is never one - a government
    /// in exile holds no ground either, and it is still a country - and neither is an entry that
    /// holds ground, which only a mod or an old campaign's own history could have given it.
    /// </summary>
    public static bool IsLegacyEntry(Campaign campaign, Nation nation) =>
        campaign is not null && nation is { Movement: null } && IsMerged(nation.Tag) && !campaign.HasOwnedLand(nation);
}
