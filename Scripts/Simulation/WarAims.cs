using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// The kind of war a belligerent is fighting, derived from its existing reasons and state.
/// It is not a second policy system: it gives the peace writer and the AI one vocabulary for
/// the difference between a border correction and a war of national survival.
/// </summary>
public enum WarAimKind
{
    Limited,
    Territorial,
    Irredentist,
    Reconquest,
    Secessionist,
    Revolutionary,
    Coalition,
    Total
}

public sealed partial class Defines
{
    /// <summary>
    /// A rising may claim neighbouring ground of its own people, not only the region it
    /// declared independence in. The claim must still be paid for in war score.
    /// </summary>
    public bool WarAimRebelsClaimBeyondHomeland { get; set; } = true;

    /// <summary>The share of a neighbouring region's people that makes it a national claim.</summary>
    public float WarAimRebelCultureShare { get; set; } = 0.30f;

    /// <summary>How strongly that share raises the value of the region in a rebel's treaty.</summary>
    public float WarAimRebelCultureWeight { get; set; } = 2.0f;

    /// <summary>The smallest war score at which a rising will press a written claim instead of fading into a truce.</summary>
    public float WarAimRebelPeaceMinimumBudget { get; set; } = 18f;
}

/// <summary>
/// War aims and the claims that follow from them.
///
/// This is deliberately derived from what a war already says about itself - its justification,
/// its size, whether it is a civil conflict, and whether a belligerent is a breakaway or a
/// faction - so a mod can add a new history without adding a new save field. A rising that has
/// taken ground next to its own people can press a claim there, but only within its war score;
/// an unrelated neighbour, or ground behind a third country, is not invented by a treaty.
/// </summary>
public static class WarAims
{
    public static bool IsRebel(Nation nation) =>
        nation is not null && (nation.SecededFromId >= 0 ||
            nation.Movement is not null ||
            !string.IsNullOrEmpty(nation.FactionId) ||
            nation.Name.StartsWith("rising:", StringComparison.Ordinal));

    public static WarAimKind Of(Campaign campaign, StrategicWar war, Nation nation)
    {
        if (war is null || nation is null) return WarAimKind.Territorial;
        // A war declared to absorb a country is not a border correction. The peace writer
        // reads the same three words and knows the whole of the enemy is the objective.
        if (!war.Limited &&
            war.Justification is "hegemony" or "annexation" or "reunification" or "conquest" or "imperialism")
            return WarAimKind.Total;
        if (war.CivilConflict || IsRebel(nation))
            return war.Justification is "revolution" or "revolutionary" or "social_revolution"
                ? WarAimKind.Revolutionary
                : WarAimKind.Secessionist;
        if (war.Limited) return WarAimKind.Limited;
        if (war.Intensity >= 2) return WarAimKind.Total;
        if (war.BelligerentCount >= 3) return WarAimKind.Coalition;
        if (string.Equals(war.Justification, "irredentism", StringComparison.Ordinal))
            return WarAimKind.Irredentist;
        if (string.Equals(war.Justification, "reconquest", StringComparison.Ordinal))
            return WarAimKind.Reconquest;
        return WarAimKind.Territorial;
    }

    /// <summary>
    /// The peoples a belligerent speaks for. The authored primary set is the first answer;
    /// a movement or faction that was declared in play carries its own list.
    /// </summary>
    public static HashSet<string> Cultures(Nation nation)
    {
        var peoples = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (nation is null) return peoples;
        foreach (var people in DemographyCatalog.PrimaryCultures(nation.Tag)) peoples.Add(people);
        foreach (var people in nation.FoundedCultures) peoples.Add(people);
        return peoples;
    }

    /// <summary>The share of a region's people that belong to any of these cultures.</summary>
    public static float CultureShare(ScenarioState region, IReadOnlySet<string> cultures)
    {
        if (region is null || cultures is null || cultures.Count == 0) return 0f;
        long total = 0;
        long ours = 0;
        foreach (var (culture, size) in EconomySystem.Cultures(region))
        {
            total += Math.Max(0L, size);
            if (cultures.Contains(culture)) ours += Math.Max(0L, size);
        }
        return total <= 0 ? 0f : ours / (float)total;
    }


    /// <summary>
    /// Whether a rising may claim this enemy region at a peace: its own title, or a
    /// substantial population of its peoples in ground touching ground it already holds.
    /// Adjacency is what keeps a plausible national claim from becoming a scatter of
    /// co-ethnic islands. The price of unheld ground still decides whether it can be signed.
    /// </summary>
    public static bool RebelClaim(Campaign campaign, StrategicWar war, Nation rebel, ScenarioState region)
    {
        if (campaign?.ProvinceMap is null || war is null || rebel is null || region is null) return false;
        if (!campaign.Defines.WarAimRebelsClaimBeyondHomeland || !IsRebel(rebel) || region.Unclaimed ||
            region.Population <= 0) return false;
        var owner = NavalOperations.NationAt(campaign, region.NationMapIndex);
        if (owner is null || !war.Opposes(rebel.Id, owner.Id)) return false;
        if (TerritorialClaims.TitleOf(campaign, rebel, region) != TerritorialTitle.None) return true;
        var cultures = Cultures(rebel);
        if (cultures.Count == 0 ||
            CultureShare(region, cultures) < Math.Clamp(campaign.Defines.WarAimRebelCultureShare, 0f, 1f))
            return false;
        return AdjacentToHeld(campaign, rebel, region.MapIndex);
    }

    /// <summary>The value a rising's national claim adds to a region in its treaty.</summary>
    public static float ClaimWeight(Campaign campaign, StrategicWar war, Nation rebel, ScenarioState region)
    {
        if (!RebelClaim(campaign, war, rebel, region)) return 0f;
        var share = CultureShare(region, Cultures(rebel));
        return 1.15f + MathF.Max(0f, campaign.Defines.WarAimRebelCultureWeight) * share;
    }

    /// <summary>
    /// A small, national war can be settled with less score than a great-power war. The floor
    /// is still a floor: below it there is no claim worth the ink, and the rising waits.
    /// </summary>
    public static float PeaceFloor(Campaign campaign, Nation nation) =>
        campaign is null || nation is null ? 35f :
        IsRebel(nation)
            ? MathF.Max(0f, campaign.Defines.WarAimRebelPeaceMinimumBudget)
            : campaign.Defines.AiPeaceMinimumBudget;

    private static bool AdjacentToHeld(Campaign campaign, Nation rebel, int region)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return false;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != (ushort)region) continue;
            foreach (var neighbour in topology.Neighbors[province])
                if (controllers[neighbour] == (ushort)rebel.MapIndex) return true;
        }
        return false;
    }
}

