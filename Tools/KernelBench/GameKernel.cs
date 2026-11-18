using System;
using System.Collections.Generic;
using DealOrForce.Simulation;

namespace DealOrForce.Tools.KernelBench;

/// <summary>
/// (a) The game's own code: the first half of HouseholdEconomy.Review, copied expression for
/// expression (Scripts/Simulation/HouseholdEconomy.cs, commit 79cadc59), on the game's own
/// objects - a List of PopulationGroup classes per region, the profession looked up by string.
///
/// The copy stops where the group walk stops (the region's living standard). What follows in
/// Review - vital rates, births and deaths, beliefs - reads few groups or none and is not the
/// numeric loop under test; the oracle in Program runs the whole real Review regardless.
/// The one line left out is "s.LastHouseholdReviewDay = c.ElapsedDays": it would make every
/// sample after the first return early.
/// </summary>
internal static class GameKernel
{
    /// <summary>HouseholdEconomy.Supply (private there).</summary>
    public static float Supply(Nation n, Commodity good) =>
        n.Market.TryGetValue(good, out var q) ? Math.Clamp(q.Satisfaction, 0, 1) : 1;

    public static void ReviewGroups(Campaign c, Nation n, ScenarioState s, float annualConditions)
    {
        if (!c.Defines.HouseholdDynamicsEnabled || s.LastHouseholdReviewDay == c.ElapsedDays || s.Unclaimed || s.Population <= 0) return;
        var interval = Math.Clamp(c.Defines.EconomyTickDays, 1, 365) / 365f;
        var labour = HouseholdEconomy.Labour(s);
        var buildingJobs = MarketSystem.JobsDemanded(s);
        if (s.NonBuildingJobs < 0) s.NonBuildingJobs = Math.Max(0, labour * HouseholdEconomy.Employment(s) - buildingJobs);
        var damage = Math.Clamp(s.WarDamage, 0, 1);
        s.NonBuildingJobs = Math.Max(0, s.NonBuildingJobs * (1 + Math.Clamp(annualConditions, -.25f, .15f) * interval));
        s.EmploymentTarget = labour <= 0 ? 0 : Math.Clamp((s.NonBuildingJobs + buildingJobs) * (1 - damage * .5f) / labour, 0, 1);
        var food = Supply(n, Commodity.Food);
        var energy = Supply(n, Commodity.Energy);
        var medicine = Supply(n, Commodity.Pharmaceuticals);
        s.BasicNeedsCoverage = food * .6f + energy * .25f + medicine * .15f;
        var scarcity = Math.Max(0, 1 - s.BasicNeedsCoverage - c.Defines.ScarcityTolerance);
        var unemployment = Math.Max(0, .9f - s.EmploymentTarget);
        s.LivelihoodStress = Math.Clamp(scarcity * .65f + unemployment * .25f + damage * .25f, 0, 1);
        var employmentRate = Math.Clamp(c.Defines.HouseholdEmploymentApproach, 0, 1);
        var wealthRate = Math.Clamp(c.Defines.HouseholdWealthApproach, 0, 1);
        double living = 0;
        foreach (var g in s.PopulationGroups)
        {
            g.Employment = M.Lerp(g.Employment, s.EmploymentTarget, employmentRate);
            var anchor = DemographyCatalog.Profession(g.Profession)?.Wealth ?? (g.Class == SocialClass.Upper ? .9f : g.Class == SocialClass.Middle ? .6f : .35f);
            var targetWealth = Math.Clamp(anchor + (g.Employment - .9f) * .22f - scarcity * .18f - damage * .12f, 0, 1);
            g.Wealth = M.Lerp(g.Wealth, targetWealth, wealthRate);
            var targetLiving = Math.Clamp(n.StandardOfLiving + (g.Wealth - .5f) * .12f - s.LivelihoodStress * .25f, 0, 1);
            g.StandardOfLiving = M.Lerp(g.StandardOfLiving, targetLiving, .04f);
            g.Radicalism = Math.Clamp(M.Lerp(g.Radicalism, s.Radicalism + s.LivelihoodStress * .2f, .025f), 0, 1);
            living += g.Size * (double)g.StandardOfLiving;
        }
        if (s.PopulationGroups.Count > 0) s.StandardOfLiving = M.Lerp(s.StandardOfLiving, (float)(living / s.Population), .08f);
    }

    /// <summary>Serially, region after region, in the economy's order.</summary>
    public static void RunSerial(Campaign c, List<ReviewItem> items)
    {
        for (var i = 0; i < items.Count; i++) ReviewGroups(c, items[i].Nation, items[i].State, items[i].Annual);
    }

    /// <summary>
    /// As the game schedules it (EconomySystem, eco:states): country by country, and a country's
    /// regions side by side through DeterministicParallel when it has at least sixteen.
    /// </summary>
    public static void RunAsGame(Campaign c, List<(Nation Nation, float Annual, ScenarioState[] States)> byNation)
    {
        foreach (var (n, annual, states) in byNation)
            DeterministicParallel.For(c, 0, states.Length, index => ReviewGroups(c, n, states[index], annual));
    }
}
