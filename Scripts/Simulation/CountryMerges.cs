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
/// line in the diplomacy list, a relation with every state on earth, and, where the map gave them
/// a province, a patch of ground with no army and nobody to talk to on it.
///
/// `common/country_merges/` says which entries these are and who holds their ground on the first
/// day (or that nobody does). Two things follow, and both are the same rule for every entry:
///
/// - the political map gives the entry's provinces to that owner as it is laid on the ground
///   (ProvinceOwnership.Apply), so Siachen is India's and the base areas are Britain's without a
///   province line written for each;
/// - an entry that holds no ground is not shown as a country (IsArtefact). It is kept in the
///   nation list - ids are list positions, and a save, a fingerprint or a mod naming the country
///   after it would all shift if it were dropped - and the weekly review ends it the way it ends
///   any country found with no ground that never had any (MovementFall).
///
/// Nothing is special-cased by tag in code: the table is data, a mod adds or removes a line, and
/// an entry that somehow comes to hold ground again (a mod handing it a province, a secession
/// reviving the tag) is a country again because it then has ground.
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
            if (entry.Value.ValueKind != JsonValueKind.Object ||
                !entry.Value.TryGetProperty("into", out var into) || into.ValueKind != JsonValueKind.String) continue;
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
    /// Whether this nation is one of the table's entries and holds no ground: something the map
    /// source called a country that is not one, and so is not listed as one. A movement is never
    /// an artefact - a government in exile holds no ground either, and it is still a country.
    /// </summary>
    public static bool IsArtefact(Campaign campaign, Nation nation) =>
        campaign is not null && nation is { Movement: null } && IsMerged(nation.Tag) && !campaign.HasOwnedLand(nation);
}
