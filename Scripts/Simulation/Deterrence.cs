using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// The reason the great powers stopped fighting each other directly in 1945, and the shape
/// everything since has taken instead: proxy wars, sanctions, coups, patience.
///
/// A country that holds a deterrent nobody can survive using is not attacked directly by
/// another holder - not because either government is restrained, but because the arithmetic
/// of such a war has no positive answer. This is that arithmetic, as a price: two holders
/// considering a direct war between themselves pay <see cref="Defines.AiMutualDeterrencePrice"/>
/// on top of everything else, which is what turns the Cold War from a script into a sum.
/// Competition between holders does not disappear - it moves to proxies, sanctions and
/// coups, which are priced elsewhere and stay cheap.
///
/// Who holds one is data, not code: the 2024 club is authored in `common/deterrence`, and
/// anybody else acquires one the way anything else is acquired - by researching it
/// (`effects: deterrence` on a technology), which is how proliferation arrives in play
/// rather than being declared. A mod for an era before the bomb ships an empty holders row
/// and no such technology, and the mechanic simply never speaks.
///
/// The tier is derived, never stored: it is the authored row or the researched effect,
/// whichever is higher, worked out when asked. There is therefore nothing to save, nothing
/// to fingerprint, and nothing a save written before this existed can get wrong.
/// </summary>
public static class Deterrence
{
    private static Dictionary<string, int> _holders;

    /// <summary>Tag to authored tier, from `common/deterrence`. Empty means an era without one.</summary>
    public static IReadOnlyDictionary<string, int> Holders { get { Ensure(); return _holders; } }

    public static void ResetCatalog() => _holders = null;

    private static void Ensure()
    {
        if (_holders is not null) return;
        var holders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in GameDataLoader.DataDocuments("common", "deterrence"))
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("holders", out var rows) ||
                rows.ValueKind != JsonValueKind.Object) continue;
            foreach (var row in rows.EnumerateObject())
                if (row.Value.ValueKind == JsonValueKind.Number && row.Value.TryGetInt32(out var tier) && tier > 0)
                    holders[row.Name] = tier;
        }
        _holders = holders;
    }

    /// <summary>
    /// What this country holds that makes attacking it directly unthinkable for another
    /// holder: the authored tier of the 2024 club, or what its research has earned it,
    /// whichever is higher. Zero is a country like any other. Off means off: with the
    /// define down, every holder is an ordinary neighbour again.
    /// </summary>
    public static int TierOf(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || !campaign.Defines.MutualDeterrenceEnabled) return 0;
        var tier = 0;
        if (!string.IsNullOrEmpty(nation.Tag) && Holders.TryGetValue(nation.Tag, out var authored)) tier = authored;
        return Math.Max(tier, (int)ResearchSystem.Bonus(nation, "deterrence"));
    }

    /// <summary>
    /// The price a direct war between two holders carries, and zero for every other pair.
    /// Read by the war judgement's price list, so what the bench reports, what the console
    /// explains and what the government decided are one arithmetic, as ever.
    /// </summary>
    public static float MutualPrice(Campaign campaign, Nation a, Nation b) =>
        TierOf(campaign, a) >= 1 && TierOf(campaign, b) >= 1 ? campaign.Defines.AiMutualDeterrencePrice : 0f;
}

public sealed partial class Defines
{
    /// <summary>Whether deterrent holders price each other's direct wars out of the arithmetic.</summary>
    public bool MutualDeterrenceEnabled { get; set; } = true;

    /// <summary>
    /// What a direct war between two deterrent holders costs on top of every other price.
    /// Deliberately of the same order as the whole rest of a war's price list added up:
    /// mutual assured destruction is not a caution, it is a wall with a door a mod can
    /// unlock by lowering it.
    /// </summary>
    public float AiMutualDeterrencePrice { get; set; } = 6f;
}
