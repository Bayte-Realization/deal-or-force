using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public float RegionalRadicalismRecoveryPerWeek { get; set; } = .004f;
    public float RegionalRadicalismLivingRecovery { get; set; } = .006f;
    public float RegionalRadicalismWarRecovery { get; set; } = .65f;
    public float RegionalRadicalismOccupation { get; set; } = .002f;
    public float AiRadicalismConcern { get; set; } = .25f;
}

/// <summary>Weekly grievances minus gradual recovery. No country exemptions and no permanent 100% trap.</summary>
public static class RadicalismCore
{
    public static float InflationPressure(Nation nation)
    {
        var w = InflationCore.Weights;
        var excess = MathF.Max(0, nation.Inflation - w.Comfort);
        return (w.Bite > 0 ? MathF.Min(excess, w.Bite) : excess) * w.Radicalism;
    }

    public static float WeeklyChange(Campaign c, Nation nation, ScenarioState state,
        bool atWar, float budgetPressure, float policyPressure, Breakdown drivers = null)
    {
        var d = c.Defines;
        var living = (.46f - state.StandardOfLiving) * .002f;
        // A people that has come to accept its holder nurses its grievance less, and one that runs
        // itself less again (ConquestAcceptance, RegionalAutonomy). Both factors are exactly one on
        // ground nobody has conquered or granted anything, so the 2024 world reads as it always did.
        var minority = EconomySystem.ForeignShare(state, nation.Tag) * d.MinorityRadicalismPerWeek *
                       ConquestAcceptance.MinorityFactor(d, state);
        var occupation = StabilityCore.OccupiedShare(c, state, nation) * MathF.Max(0, d.RegionalRadicalismOccupation);
        var inflation = InflationPressure(nation);
        var overstretch = (nation is not null && nation.ImperialOverstretch > 0.001f &&
                           TerritorialClaims.TitleOf(c, nation, state) != TerritorialTitle.Homeland &&
                           EconomySystem.ForeignShare(state, nation.Tag) >= ConquestAcceptance.UnentitledForeignShare)
            ? nation.ImperialOverstretch * d.ImperialOverstretchRadicalismWeekly * (1f - M.Clamp01(state.Acceptance))
            : 0f;
        if (RegionalAutonomy.Autonomous(state))
        {
            var inflow = M.Clamp01(d.AutonomyRadicalismInflow);
            minority *= inflow;
            overstretch *= inflow;
        }
        // Beaten rebels are years of quiet: what feeds anger arrives a third as fast (RisingAftermath).
        var exhausted = RisingAftermath.Relief(c, state, minority + overstretch + occupation);
        // Recovery scales with the existing angry population, not a free stability bonus.
        // Good living conditions make it faster; war slows it, but never turns recovery into growth.
        var rate = MathF.Max(0, d.RegionalRadicalismRecoveryPerWeek) +
                   M.Clamp01(state.StandardOfLiving) * MathF.Max(0, d.RegionalRadicalismLivingRecovery);
        if (atWar) rate *= M.Clamp01(d.RegionalRadicalismWarRecovery);
        // A time of troubles exhausts itself: the longer a country has been at the bottom, the
        // faster people tire of the radicals (Nation.TroublesDays). Without it a collapsed state
        // stayed at the floor of its stability for twenty-five years, its regions at radicalism one.
        if (nation is not null && nation.TroublesDays > 0)
            rate *= 1f + M.Clamp(nation.TroublesDays / MathF.Max(1f, d.TroublesExhaustionDays), 0f, d.TroublesExhaustionCap);
        var recovery = -M.Clamp01(state.Radicalism) * M.Clamp01(rate);
        var raw = living + minority + occupation + inflation + overstretch + exhausted + budgetPressure + policyPressure + recovery;
        var actual = M.Clamp01(state.Radicalism + raw) - state.Radicalism;
        if (drivers is not null)
        {
            drivers.Begin("Radicalism: weekly forecast", "share");
            drivers.Add("Living conditions", living);
            drivers.Add("Budget policy", budgetPressure);
            drivers.Add("Government and ideology", policyPressure);
            drivers.Add("Minority grievances", minority);
            drivers.Add("Occupation", occupation);
            drivers.Add("Inflation", inflation);
            if (overstretch > 0f) drivers.Add(Loc.Get("Imperial overstretch"), overstretch);
            if (exhausted < 0f) drivers.Add("Exhausted after a crushed rising", exhausted);
            drivers.Add("Gradual recovery", recovery);
            drivers.Settle(actual);
        }
        return actual;
    }

    public static Breakdown Forecast(Campaign c, Nation nation, ScenarioState state)
    {
        var result = new Breakdown();
        if (c is null || nation is null || state is null) return result;
        var atWar = c.Wars.Exists(w => !w.Ended && w.Involves(nation.Id));
        var budget = BudgetCore.Evaluate(nation, nation.Gdp / 52f, atWar);
        var policy = GovernmentFocusCatalog.Of(nation.GovernmentFocus).Radicalism +
                     PoliticsCore.GovernmentOf(nation).Radicalism + PoliticsCore.IdeologyOf(nation).Radicalism;
        var weekly = WeeklyChange(c, nation, state, atWar, budget.Radicalism, policy, result);
        // What laws, spirits and a proclaimed union add to every region a week is paid outside this
        // pass - a seventh a day by NationalModifiers.Tick - and a forecast that left it out showed a
        // falling radicalism in a Yugoslavia whose every region was climbing two points a week.
        if (MechanicFeatures.SystemEnabled(c, nameof(NationalModifiers)) &&
            NationalModifiers.Of(nation, "radicalism_weekly") is var modifiers && modifiers != 0f)
        {
            result.Add("Laws, spirits and the union", modifiers);
            result.Settle(M.Clamp01(state.Radicalism + weekly + modifiers) - state.Radicalism);
        }
        return result;
    }

    public static bool NeedsAttention(Campaign c, Nation nation)
    {
        double angry = 0, people = 0;
        foreach (var state in c.StatesOf(nation))
        {
            angry += state.Population * (double)state.Radicalism;
            people += state.Population;
        }
        return people > 0 && angry / people >= c.Defines.AiRadicalismConcern;
    }
}
