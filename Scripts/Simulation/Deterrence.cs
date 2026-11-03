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
/// ═══════════════════════════════════════════════════════════════════════════════
/// FOR MODDERS — the three things you might want to change, and where:
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// 1. WHO holds one at the start → `game/common/deterrence/*.json`.
///    The file is a single object: `"holders": { "RUS": 1, "USA": 1, ... }`.
///    Delete the file (or empty the object) for a world without the bomb.
///    Higher tiers (2, 3) are for mods that want a second rung — a defence
///    that survives a first strike, say.
///
/// 2. HOW EXPENSIVE the wall is → `AiMutualDeterrencePrice` in
///    `game/common/defines/`. Default 6 — the same order as an entire war's
///    price list. Lower it and the Cold War thaws; raise it and not even a
///    border incident cracks the ice. Set `MutualDeterrenceEnabled` to false
///    and every holder is an ordinary neighbour again.
///
/// 3. WHO CAN GET ONE during play → add a technology with
///    `"effects": { "deterrence": 1 }` in `game/common/technologies/*.json`.
///    That's it. The country researches it, becomes tier 1, and the wall
///    goes up between it and every other holder. The AI reaches for it when
///    a rival already has one (TechnologyCore.Choose) — this is the
///    proliferation race, not a scripted event.
///
/// 4. СВЯЗЬ С ЛЕСТНИЦЕЙ БОЕГОТОВНОСТИ DEFCON:
///    DefconSystem опрашивает Deterrence.TierOf(campaign, nation) >= 1 для определения
///    участников ядерного клуба. Любая война или острый дипломатический кризис между
///    двумя державами с TierOf >= 1 переводит планету в DEFCON 2 или DEFCON 1,
///    включая мобилизационные издержки и сжатие мировой торговли.
///
/// Everything else is derived from these inputs. There is nothing to save,
/// nothing to fingerprint, and nothing a save written before this existed can get wrong.
/// ═══════════════════════════════════════════════════════════════════════════════
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
    /// whichever is higher. Zero is a country like any other.
    ///
    /// A mod adds a holder by adding a row to `common/deterrence` (authored at start)
    /// or a technology with `effects: deterrence` (acquired in play). Both paths
    /// arrive here and the higher wins.
    /// </summary>
    public static int TierOf(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || !campaign.Defines.MutualDeterrenceEnabled) return 0;
        var tier = 0;
        if (!string.IsNullOrEmpty(nation.Tag) && Holders.TryGetValue(nation.Tag, out var authored)) tier = authored;
        return Math.Max(tier, (int)ResearchSystem.Bonus(nation, "deterrence"));
    }

    /// <summary>
    /// The price a direct war between two deterrent holders carries, and zero for every other pair.
    /// Read by the war judgement's price list, so what the bench reports, what the console
    /// explains and what the government decided are one arithmetic, as ever.
    ///
    /// The default (6) is roughly the entire rest of a war's price list added up. A mod
    /// that wants the Cold War to crack at the seams lowers it; a mod that wants
    /// never-touch peace raises it. Zero and the wall is gone.
    /// </summary>
    public static float MutualPrice(Campaign campaign, Nation a, Nation b) =>
        TierOf(campaign, a) >= 1 && TierOf(campaign, b) >= 1 ? campaign.Defines.AiMutualDeterrencePrice : 0f;

    /// <summary>
    /// The shadow over a war both sides could end the world from, as a pressure 0..4: the
    /// product of the two tiers, zero for every ordinary war. Nobody plans a conquest
    /// through a war like that - not because either government is timid, but because the
    /// world (and each government's own cities) prices the map's redrafting out of it.
    ///
    /// Who reads this and what they do with it:
    ///   • AiPeaceOutcomes.WriteDecisiveSettlement — the Cuba rule: holders' wars end
    ///     in limited terms, and the decisive bar comes DOWN by this pressure (the
    ///     higher the shadow, the sooner the armistice).
    ///   • WarWeariness.Tick — the home front: both sides' populations bleed war
    ///     support and stability weekly under the `nuclear_shadow` spirit.
    ///   • The dossier card shows the tier to the player ("Mutual assured destruction").
    ///
    /// The value is the product of the two tiers (1×1=1, 2×1=2, 2×2=4), capped at 4.
    /// </summary>
    public static float Brinkmanship(Campaign campaign, StrategicWar war)
    {
        if (campaign is null || war is null || war.Ended || !campaign.Defines.MutualDeterrenceEnabled) return 0f;
        var attacker = campaign.NationById(war.AttackerId);
        var defender = campaign.NationById(war.DefenderId);
        if (attacker is null || defender is null) return 0f;
        var attackerTier = TierOf(campaign, attacker);
        var defenderTier = TierOf(campaign, defender);
        return attackerTier >= 1 && defenderTier >= 1 ? Math.Min(4, attackerTier * defenderTier) : 0f;
    }
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
