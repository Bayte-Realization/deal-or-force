using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Whether the AI may turn a weak neighbour into a war of annexation.</summary>
    public bool AiAnnexesWeakNeighbours { get; set; } = true;
    /// <summary>How much stronger a country must be before swallowing a neighbour is plausible.</summary>
    public float AiAnnexationPowerRatio { get; set; } = 2.8f;
    /// <summary>The largest country, by regions, that one war may try to swallow.</summary>
    public int AiAnnexationMaxRegions { get; set; } = 12;
    /// <summary>The largest population share of the attacker that a target may have.</summary>
    public float AiAnnexationMaxPopulationShare { get; set; } = .35f;
    /// <summary>Above this war restraint a government does not annex without a national claim.</summary>
    public float AiAnnexationRestraintMax { get; set; } = .55f;
    /// <summary>How much a plausible annexation adds to the ordinary war judgement.</summary>
    public float AiAnnexationTargetBonus { get; set; } = .35f;
    /// <summary>War score at which a small conquest may be signed as a whole country.</summary>
    public float AiAnnexationDecisiveScore { get; set; } = 62f;
    public float AiAnnexationOccupationShare { get; set; } = .50f;
    /// <summary>A people split across a border needs less score to be put back together.</summary>
    public float AiReunificationDecisiveScore { get; set; } = 55f;
    public float AiReunificationOccupationShare { get; set; } = .45f;
    /// <summary>Either side of a civil war may end it by taking the whole state.</summary>
    public float AiCivilAnnexationScore { get; set; } = 55f;
    public float AiCivilAnnexationOccupationShare { get; set; } = .45f;
    /// <summary>A country this notorious has other problems than one more conquest.</summary>
    public float AiHegemonyNotorietyMax { get; set; } = 70f;
}

/// <summary>What an AI intends to take when it starts a war of absorption.</summary>
public readonly record struct AnnexationAim(string Cause, int ClaimedState, bool Reunification);

/// <summary>
/// When a country can, and wants to, swallow another one.
///
/// This is deliberately separate from the ordinary war judgement. That judgement asks
/// whether a border correction is worth the price; this asks the older question a foreign
/// ministry asks when it looks at a small neighbour: can we simply take it? A shared people,
/// a much weaker army and a government with little restraint are the three things that turn
/// a border war into a war of union. The peace writer uses the same vocabulary
/// (<c>hegemony</c>, <c>reunification</c>) so the declaration and the treaty agree.
/// </summary>
public static class AnnexationAmbition
{
    /// <summary>A cheap appetite read for the target loop: no casus belli, no map walks.</summary>
    public static float Appetite(Campaign campaign, Nation us, Nation them)
    {
        if (campaign?.ProvinceMap is null || us is null || them is null || us.Id == them.Id) return 0f;
        if (us.Population <= 0 || them.Population <= 0) return 0f;
        var defines = campaign.Defines;
        if (!defines.AiAnnexesWeakNeighbours) return 0f;
        if (them.IsSubject || them.Movement is not null || them.MapIndex == us.MapIndex) return 0f;
        var relation = CampaignDiplomacy.Peek(campaign, us.Id, them.Id);
        if (relation is { Stance: DiplomaticStance.Allied }) return 0f;
        if (relation is not null && (relation.GuaranteeByA || relation.GuaranteeByB)) return 0f;
        if (!CampaignDiplomacy.SharesLandBorder(campaign, us.MapIndex, them.MapIndex)) return 0f;

        var regions = campaign.StatesOf(them).Length;
        if (regions <= 0 || regions > Math.Max(1, defines.AiAnnexationMaxRegions)) return 0f;
        var populationShare = them.Population / (float)Math.Max(1L, us.Population);
        if (populationShare > Math.Clamp(defines.AiAnnexationMaxPopulationShare, .01f, 1f)) return 0f;

        var ours = Math.Max(1f, AiStatecraft.CachedStrength(campaign, us));
        var theirs = Math.Max(1f, AiStatecraft.CachedStrength(campaign, them));
        var ratio = ours / theirs;
        // Appetite is for swallowing, not for an ordinary border quarrel. A country that is
        // not stronger than its neighbour has no business planning to absorb it.
        if (ratio < 1.15f) return 0f;
        var power = Math.Clamp((ratio - 1f) / Math.Max(.1f, defines.AiAnnexationPowerRatio - 1f), 0f, 1f);
        var small = Math.Clamp(1f - regions / (float)Math.Max(1, defines.AiAnnexationMaxRegions + 1), .15f, 1f);
        var kin = PeacefulFormation.CulturalAffinity(us, them);
        var restraint = AiStatecraft.Restraint(campaign, us);
        var hunger = Math.Clamp(1f - restraint / Math.Max(.05f, defines.AiAnnexationRestraintMax), 0f, 1f);
        var score = power * .45f + small * .20f + kin * .25f + hunger * .10f;
        if (us.Notoriety >= defines.AiHegemonyNotorietyMax) score *= .5f;
        return Math.Clamp(score, 0f, 1f);
    }

    /// <summary>The reason a strong, unrestrained government can give for taking a small neighbour.</summary>
    public static bool HegemonyCause(Campaign campaign, Nation us, Nation them)
    {
        if (campaign is null || us is null || them is null) return false;
        if (Appetite(campaign, us, them) < .55f) return false;
        if (AiStatecraft.Restraint(campaign, us) > campaign.Defines.AiAnnexationRestraintMax) return false;
        return us.Notoriety < campaign.Defines.AiHegemonyNotorietyMax;
    }

    /// <summary>The aim this pair would be fought over, or null for an ordinary border war.</summary>
    public static AnnexationAim? Aim(Campaign campaign, Nation us, Nation them)
    {
        if (campaign is null || us is null || them is null) return null;
        if (Appetite(campaign, us, them) < .5f) return null;
        // Wanting a union is not the same as being able to impose one. A country that is
        // not at least somewhat stronger keeps the claim for a better year.
        var ours = Math.Max(1f, AiStatecraft.CachedStrength(campaign, us));
        var theirs = Math.Max(1f, AiStatecraft.CachedStrength(campaign, them));
        if (ours < theirs * 1.15f) return null;
        var claimed = ClaimedState(campaign, them);
        if (claimed <= 0) return null;

        // A people with a state on one side of the border and a minority on the other is
        // the one conquest a restrained government will still argue for.
        if (IsReunification(campaign, us, them))
            return new AnnexationAim("reunification", claimed, true);

        var cause = Statecraft.CasusBelli(campaign, us, them);
        if (cause is "irredentism" or "reconquest" or "hegemony")
            return new AnnexationAim(cause, claimed, false);
        return null;
    }

    /// <summary>Whether the two countries are two halves of one people, not merely neighbours.</summary>
    public static bool IsReunification(Campaign campaign, Nation us, Nation them)
    {
        if (campaign?.ProvinceMap is null || us is null || them is null) return false;
        if (them.Movement is not null || them.IsSubject) return false;
        if (PeacefulFormation.CulturalAffinity(us, them) < .75f) return false;
        var ours = WarAims.Cultures(us);
        if (ours.Count == 0) return false;
        // A shared primary people is the claim: Austria and Germany, Albania and Kosovo,
        // Moldova and Transnistria. The share of the target's population is a question for
        // the peace table, not for whether the two states are one people.
        return ours.Overlaps(WarAims.Cultures(them));
    }

    /// <summary>
    /// Whether the two sides are a government and its rebels, or two factions of the same
    /// civil war. The parent is carried by <see cref="Nation.SecededFromId"/>.
    /// </summary>
    public static bool IsCivilWarBetween(Nation winner, Nation loser)
    {
        if (winner is null || loser is null || winner.Id == loser.Id) return false;
        var winnerParent = winner.SecededFromId;
        var loserParent = loser.SecededFromId;
        if (winnerParent < 0 && loserParent < 0) return false;
        return winnerParent == loserParent || winnerParent == loser.Id || loserParent == winner.Id;
    }

    /// <summary>The region the war is about: the capital, or the largest region if it has none.</summary>
    public static int ClaimedState(Campaign campaign, Nation them)
    {
        if (campaign is null || them is null) return -1;
        if (them.CapitalStateMapIndex > 0) return them.CapitalStateMapIndex;
        return campaign.StatesOf(them).OrderByDescending(state => state.Population)
            .Select(state => state.MapIndex).FirstOrDefault();
    }
}

