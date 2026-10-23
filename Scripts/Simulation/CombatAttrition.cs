using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

// Frontage, damage and casualty arithmetic; no orders, captures or random draws.
public static partial class Warfare
{
    // Only values, never Army/Campaign references. Cleared before each damage pass.
    // Pre-damage exposure is necessary even for duplicate ids in a legacy battle.
    private static readonly List<(float Personnel, float Casualties)> DamageExposure = new();

    /// <summary>Combined power of one country's divisions in a list of what is standing there.</summary>
    private static float Power(List<Army> present, int nationId)
    {
        var total = 0f;
        foreach (var army in present) if (army.NationId == nationId) total += army.Power;
        return total;
    }

    /// <summary>Frontage a side would take if the province had unlimited room.</summary>
    private static float WantedWidth(IReadOnlyCollection<Army> side) =>
        side.Sum(army => army.Divisions * UnitTypeCatalog.Get(army.TemplateId).CombatWidth);

    /// <summary>
    /// How much of a side is actually in the fight. Divisions past the frontage wait their
    /// turn, and piling far more in than fits starts to cost the ones who are fighting -
    /// men in the way of their own guns.
    /// </summary>
    private static float Engagement(float committed, float wanted, float available, Defines defines)
    {
        var share = M.Clamp(committed / M.Max(0.01f, wanted), 0.05f, 1f);
        var over = M.Max(0f, wanted - available) / M.Max(0.01f, available);
        var crowding = M.Clamp(1f - over * defines.OverWidthPenalty, defines.MinCrowdingFactor, 1f);
        return share * crowding;
    }

    /// <summary>One plus what a formation's country adds to this: research, spirits, government (NationalModifiers).</summary>
    private static float Researched(Campaign campaign, Army army, string effect) =>
        NationalModifiers.Factor(campaign.NationById(army.NationId), effect);

    /// <summary>One division's soft/hard fire against a target of the given hardness.</summary>
    private static float Offence(Army army, float targetHardness)
    {
        var template = UnitTypeCatalog.Get(army.TemplateId);
        return template.Attack * M.Lerp(template.SoftAttack, template.HardAttack, M.Clamp01(targetHardness));
    }

    /// <summary>
    /// Armour against piercing. Enough piercing and the shot counts in full; not enough and
    /// half of it bounces. The floor is a define so a mod can make armour decisive or
    /// irrelevant without touching code.
    /// </summary>
    private static float ArmourFactor(float piercing, float armour, Defines defines)
    {
        if (armour <= 0.01f || piercing >= armour) return 1f;
        return M.Lerp(defines.UnpiercedDamageFloor, 1f, M.Clamp01(piercing / armour));
    }

    /// <summary>A division-weighted average of some per-division number.</summary>
    private static float Weighted(IReadOnlyCollection<Army> side, Func<Army, float> select)
    {
        var divisions = side.Sum(army => M.Max(0.01f, army.Divisions));
        return divisions <= 0f ? 0f : side.Sum(army => select(army) * M.Max(0.01f, army.Divisions)) / divisions;
    }

    /// <summary>
    /// Spend a day of fighting on one side. Organisation goes first and men go slowly:
    /// a division that still has organisation is losing its willingness to press, not its
    /// people. Once organisation is gone the same fire starts taking the formation apart,
    /// which is why a broken unit that keeps standing bleeds so much faster than one that
    /// withdraws in order.
    ///
    /// The side doing the assaulting pays more for the same day. That asymmetry is what
    /// makes an attrition war grind down the attacker rather than whoever has fewer men:
    /// crossing open ground into a prepared position has never cost what holding one does.
    /// </summary>
    private static float ApplyDamage(Campaign campaign, IReadOnlyCollection<Army> side,
        float organisationLoss, float casualtyScale, out float killed)
    {
        var defines = campaign.Defines;
        var totalPower = side.Sum(army => M.Max(0.01f, army.Power));
        // Calculate the denominator once, in the original summation order. Every
        // exposure is still sampled before any formation loses organisation.
        DamageExposure.Clear();
        foreach (var army in side)
            DamageExposure.Add(Casualties(campaign, army, side.Count, totalPower, organisationLoss, casualtyScale));
        var lost = 0f;
        killed = 0f;
        var woundedShare = M.Clamp01(defines.WoundedShareOfCasualties);
        var index = 0;
        foreach (var army in side)
        {
            var template = UnitTypeCatalog.Get(army.TemplateId);
            var share = M.Max(0.01f, army.Power) / totalPower * side.Count;
            var loss = organisationLoss * share / M.Max(0.2f, template.Organisation);
            var (personnel, casualties) = DamageExposure[index++];

            army.Organisation = M.Clamp01(army.Organisation - loss);

            // A division that holds the same ground goes on digging even while it is being
            // shelled - that is what a prepared line is. Shelling costs it a little more
            // than a day of work is worth only when the fighting is heavy; otherwise the
            // position slowly deepens. Advancing throws all of it away, which is the price
            // of an offensive and the reason a static front is so expensive to move.
            army.Entrenchment = M.Clamp01(army.Entrenchment
                - defines.EntrenchmentLostPerCombatDay
                + defines.EntrenchmentPerDay * defines.EntrenchmentUnderFireShare);

            if (personnel > 1f) army.Strength = M.Clamp01(army.Strength - casualties / personnel);
            army.Personnel = Math.Max(0, army.Personnel - (int)MathF.Round(casualties));
            army.Recovering += casualties * defines.WoundedReturningShare;
            army.Experience = M.Clamp01(army.Experience + loss * 0.004f);
            army.Cohesion = M.Clamp01(army.Cohesion - loss * 0.08f);
            lost += casualties;
            killed += casualties * (1f - woundedShare);
        }
        return lost;
    }

    /// <summary>One formation's men and the casualties a day of this battle costs it.</summary>
    private static (float Personnel, float Casualties) Casualties(Campaign campaign, Army army,
        int formationCount, float totalPower, float organisationLoss, float casualtyScale)
    {
        var defines = campaign.Defines;
        var template = UnitTypeCatalog.Get(army.TemplateId);
        var share = M.Max(0.01f, army.Power) / totalPower * formationCount;
        var loss = organisationLoss * share / M.Max(0.2f, template.Organisation);
        var brokenExposure = M.Lerp(defines.BrokenCasualtyMultiplier, 1f,
            M.Clamp01(army.Organisation / M.Max(0.05f, defines.BattleBreakOrganisation * 4f)));
        var personnel = army.Divisions * MilitaryBootstrap.DivisionSize * army.Strength;
        var casualties = personnel * loss * defines.CasualtiesPerOrganisationPoint *
                         brokenExposure * casualtyScale *
                         MilitaryDoctrine.CasualtyExposure(army.Stance) *
                         (army.CutOff ? defines.EncircledCasualtyMultiplier : 1f);
        return (personnel, casualties);
    }

    /// <summary>A side's men, and the casualties a day of this battle would cost it.</summary>
    private static (float Men, float Dead) CasualtyTotals(Campaign campaign, IReadOnlyCollection<Army> side,
        float organisationLoss, float casualtyScale)
    {
        var totalPower = side.Sum(army => M.Max(0.01f, army.Power));
        float men = 0f, dead = 0f;
        foreach (var army in side)
        {
            var (personnel, casualties) = Casualties(campaign, army, side.Count, totalPower, organisationLoss, casualtyScale);
            men += personnel;
            dead += casualties;
        }
        return (men, dead);
    }

    private static float AverageOrganisation(IReadOnlyCollection<Army> side)
    {
        var divisions = side.Sum(army => M.Max(0.01f, army.Divisions));
        return divisions <= 0f ? 0f : side.Sum(army => army.Organisation * M.Max(0.01f, army.Divisions)) / divisions;
    }

    private static float WeightedPlanning(IReadOnlyCollection<Army> side)
    {
        var divisions = side.Sum(army => M.Max(0.01f, army.Divisions));
        return divisions <= 0f ? 0f : side.Sum(army => army.Planning * M.Max(0.01f, army.Divisions)) / divisions;
    }

}

public sealed partial class Defines
{
    /// <summary>The most one side of a battle can lose per formation per day against the other.</summary>
    public float MaxOrganisationLossRatio { get; set; } = 3.5f;

    /// <summary>The most of its men one side of a battle can lose in a day, as a multiple of the other's share.</summary>
    public float MaxCasualtyRateRatio { get; set; } = 2.5f;

    /// <summary>The most men one side of a battle can lose in a day for each it kills.</summary>
    public float MaxCasualtyExchange { get; set; } = 3f;
}
