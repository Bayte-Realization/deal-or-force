using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>Monthly budget and investment decisions, separate from war settlement and diplomacy.</summary>
public static partial class AiStatecraft
{
    /// <summary>
    /// Tax, the army's share, what is spent on people, and what is built with the rest.
    ///
    /// Split out of <see cref="Economy"/> so a player can hand this portfolio to their own
    /// civil service and get exactly what every other country in the world gets - see
    /// <see cref="Delegation"/>. Deliberately NOT including the cabinet: a decision is a
    /// thing a country does once and cannot undo, and delegating the budget is not consent
    /// to have your government reformed while you were looking at the map.
    /// </summary>
    public static void GovernTheBudget(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return;
        var defines = campaign.Defines;
        var atWar = AiFiscalPolicy.AtWar(campaign, nation);
        var fiscal = AiFiscalPolicy.Assess(campaign, nation, defines.AiTreasuryFloor);
        if (fiscal.Reason == AiFiscalReason.InvalidAccounts) { Count("budget_invalid_accounts"); return; }
        var reserves = nation.Gdp <= 0f ? 0f : nation.Treasury / nation.Gdp;

        // Tax. Broke countries tax more; comfortable ones with an unhappy population tax
        // less, because the alternative to a rebate is a riot.
        var taxTarget = nation.TaxRate;
        if (!fiscal.CanSpend) taxTarget += defines.AiTaxStep;
        else if (reserves > defines.AiTreasuryComfort && nation.Stability < 0.6f && fiscal.WeeklyBalance >= 0)
            taxTarget -= defines.AiTaxStep;
        nation.TaxRate = M.Clamp(taxTarget, defines.AiMinimumTaxRate, defines.AiMaximumTaxRate);

        // The army costs what the situation is worth.
        bool threatened;
        using (SimProfiler.Measure("    eco:threat"))
            threatened = !atWar && (nation.WarPlanTargetId >= 0 || Threatened(campaign, nation));
        var militaryTarget = atWar
            ? defines.AiWartimeMilitarySpending
            : threatened ? defines.AiTenseMilitarySpending : defines.AiPeacetimeMilitarySpending;
        nation.MilitarySpending = M.Lerp(nation.MilitarySpending, militaryTarget, 0.35f);

        // And the war industry follows the budget it is part of. A country at war runs
        // its plants harder; a country at peace spins them mostly back down. The AI sets
        // the same capacity pool the player uses, by the same rule it sets the military budget
        // by - so the world's economies behave like the player's rather than around it.
        var focusTarget = atWar
            ? defines.AiWartimeArmsFocus
            : threatened ? defines.AiWartimeArmsFocus * 0.5f : defines.AiPeacetimeArmsFocus;
        nation.ArmsFocus = M.Lerp(nation.ArmsFocus, focusTarget, 0.20f);
        // AI policy is quantised by the same capacity allocator as player orders.
        nation.ConvertedCivilianUnits = -1;

        // An unhappy country buys quiet, when it can afford it.
        fiscal = AiFiscalPolicy.Assess(campaign, nation, defines.AiTreasuryFloor);
        if ((nation.Stability < defines.AiStabilityConcern || RadicalismCore.NeedsAttention(campaign, nation)) && fiscal.CanSpend)
        {
            var welfareBefore = nation.WelfareSpending;
            var educationBefore = nation.EducationSpending;
            nation.WelfareSpending = M.Clamp(nation.WelfareSpending + defines.AiSocialStep, 0f, 0.18f);
            nation.EducationSpending = M.Clamp(nation.EducationSpending + defines.AiSocialStep * 0.6f, 0f, 0.15f);
            // A monthly promise is recurring spending, not a cheque paid once. Keep
            // the relief only if the resulting budget still has its operating buffer.
            if (!AiFiscalPolicy.Assess(campaign, nation, defines.AiTreasuryFloor).CanSpend)
            {
                nation.WelfareSpending = welfareBefore;
                nation.EducationSpending = educationBefore;
                Count("social_expansion_deferred");
            }
        }
        else if (!fiscal.CanSpend)
        {
            nation.WelfareSpending = M.Clamp(nation.WelfareSpending - defines.AiSocialStep, 0.01f, 0.20f);
        }

        if (!nation.IsHuman || Delegation.Holds(nation, Delegation.Construction))
            using (SimProfiler.Measure("    eco:build")) Build(campaign, nation, atWar);
    }

    public static void PlanConstruction(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || !campaign.FeatureEnabled("construction")) return;
        using (SimProfiler.Measure("    eco:build")) Build(campaign, nation, AiFiscalPolicy.AtWar(campaign, nation));
    }

    /// <summary>
    /// A month's building: one project for an economy of the reference size, more for a larger one
    /// (ConstructionCost.Size) - China does not build what Italy builds - each still only if the
    /// treasury allows it.
    /// </summary>
    private static void Build(Campaign campaign, Nation nation, bool atWar)
    {
        var starts = Math.Max(1, (int)MathF.Round(ConstructionCost.Size(campaign, nation) / 2f));
        for (var i = 0; i < starts; i++)
            if (!BuildOne(campaign, nation, atWar, i)) return;
    }

    /// <summary>
    /// One project a month, in the region that most needs it. What a country builds says
    /// what it thinks is wrong with itself: an army that cannot be fed builds roads, a
    /// country at war builds guns, and a country at peace builds the thing that makes it
    /// richer next year.
    /// </summary>
    private static bool BuildOne(Campaign campaign, Nation nation, bool atWar, int slot)
    {
        var defines = campaign.Defines;
        var fiscal = AiFiscalPolicy.Assess(campaign, nation, defines.AiBuildTreasuryFloor);
        if (!fiscal.CanSpend) { Count("construction_fiscal_" + fiscal.Reason); return false; }
        var running = 0;
        foreach (var project in campaign.Construction)
            if (project.NationId == nation.Id && ++running >= ConstructionCost.Limit(campaign, nation, defines.AiMaxConcurrentProjects))
            { Count("construction_sites_full"); return false; }

        var states = campaign.StatesOf(nation);
        if (states.Length == 0) return false;

        var congested = false;
        foreach (var army in campaign.Armies)
            if (army.NationId == nation.Id && army.SupplyCongestion < 0.75f) { congested = true; break; }
        // A poor country's needs are schools and fields, but not only: one project in three goes
        // to them and the rest to what makes it richer. Answering every month with a university
        // while literacy stood under 0.72 kept India, Nigeria and Brazil building nothing that
        // produces anything for ten years, while the rich world built factories.
        var needsTurn = (campaign.ElapsedDays / 30 + slot) % 3 == 0;
        var mature = nation.Literacy >= .72f && nation.StandardOfLiving >= .55f;
        // A country at war builds guns while it is short of them, and otherwise what it would
        // build at peace.
        var armsTurn = atWar && AiBuilder.ShortOfArms(campaign, nation);
        var wanted =
            congested ? BuildingKind.Infrastructure :
            armsTurn ? BuildingKind.ArmsIndustry :
            !mature && needsTurn && nation.Literacy < 0.72f ? BuildingKind.University :
            !mature && needsTurn && nation.StandardOfLiving < 0.55f ? BuildingKind.Agriculture :
            BuildingKind.CivilianIndustry;

        ScenarioState target = null;
        // The production-chain planner already understands recipes, deposits and
        // water access. Let a peaceful economy actually use it.
        if (!congested && !armsTurn && (mature || !needsTurn))
            AiBuilder.Choose(campaign, nation, states, out wanted, out target);

        // Put a generic priority where there is most to gain. A specialized market
        // investment keeps the region selected by the production/deposit planner.
        var bestScore = float.MinValue;
        if (target is null) foreach (var state in states)
        {
            var already = state.Buildings.GetValueOrDefault(wanted);
            var score = state.Population / 1_000_000f - already * defines.AiBuildSaturation;
            if (score <= bestScore) continue;
            bestScore = score;
            target = state;
        }
        if (target is null) return false;
        if (!AiFiscalPolicy.CanStartProject(campaign, nation, wanted, fiscal))
        { Count("construction_operating_buffer"); return false; }

        new QueueConstructionCommand
        {
            IssuerId = nation.Id,
            StateMapIndex = target.MapIndex,
            Building = wanted
        }.Apply(campaign);
        Count("construction_started_" + wanted);
        return true;
    }
}
