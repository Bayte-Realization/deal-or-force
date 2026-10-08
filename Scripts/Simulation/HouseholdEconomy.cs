using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class ScenarioState
{
    // Employment outside authored factories (services, self-employment, informal work).
    // -1 migrates old saves on their next economic review, never while reading a tooltip.
    public float NonBuildingJobs = -1;
    public int LastHouseholdReviewDay = -1;
    public long ReviewBirths, ReviewDeaths;
    public float BasicNeedsCoverage = 1, EmploymentTarget = .9f, LivelihoodStress;
}

public sealed partial class Defines
{
    public bool HouseholdDynamicsEnabled { get; set; } = true;
    public float HouseholdEmploymentApproach { get; set; } = .08f;
    public float HouseholdWealthApproach { get; set; } = .025f;
    public float DemographyBirthBaseAnnual { get; set; } = .010f;
    public float DemographyLowLiteracyBirthAnnual { get; set; } = .018f;
    public float DemographyDeathBaseAnnual { get; set; } = .006f;
    public float DemographyShortageDeathAnnual { get; set; } = .005f;
}

/// <summary>Bounded, deterministic household adjustment after the market clears.
/// Jobs change employment; actual basic-good satisfaction changes purchasing conditions;
/// medicine, living conditions and local destruction change vital rates. No money is created,
/// no migrants are conjured, and a national war flag does not kill every region equally.
/// These are deliberately aggregate game rates, not a real demographic forecast.</summary>
public static class HouseholdEconomy
{
    public static float Labour(ScenarioState s) => s.PopulationGroups.Count == 0 ? s.Population * .62e-6f :
        (float)s.PopulationGroups.Sum(g => g.Size * .62e-6 * (g.Class == SocialClass.Upper ? .15 : 1));

    public static float Employment(ScenarioState s)
    {
        var labour = Labour(s);
        return labour <= 0 ? 0 : (float)s.PopulationGroups.Sum(g => g.Size * .62e-6 *
            (g.Class == SocialClass.Upper ? .15 : 1) * g.Employment) / labour;
    }

    private static float Supply(Nation n, Commodity good) =>
        n.Market.TryGetValue(good, out var q) ? Math.Clamp(q.Satisfaction, 0, 1) : 1;

    public static void Review(Campaign c, Nation n, ScenarioState s, float annualConditions)
    {
        if (!c.Defines.HouseholdDynamicsEnabled || s.LastHouseholdReviewDay == c.ElapsedDays || s.Unclaimed || s.Population <= 0) return;
        s.LastHouseholdReviewDay = c.ElapsedDays;
        var interval = Math.Clamp(c.Defines.EconomyTickDays, 1, 365) / 365f;
        var labour = Labour(s);
        var buildingJobs = MarketSystem.JobsDemanded(s);
        if (s.NonBuildingJobs < 0) s.NonBuildingJobs = Math.Max(0, labour * Employment(s) - buildingJobs);
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
        var healthCapacity = s.Buildings.GetValueOrDefault(BuildingKind.Hospital) / Math.Max(.1f, s.Population / 1e6f);
        var health = healthCapacity / (1 + healthCapacity) * medicine;
        var birth = Math.Clamp(c.Defines.DemographyBirthBaseAnnual + (1 - s.Literacy) * c.Defines.DemographyLowLiteracyBirthAnnual, 0, .08f);
        var death = Math.Clamp(c.Defines.DemographyDeathBaseAnnual + (1 - s.StandardOfLiving) * .004f +
            scarcity * c.Defines.DemographyShortageDeathAnnual + damage * .012f, 0, .08f) * (1 - health * .25f);
        s.ReviewBirths = (long)Math.Round(s.Population * (double)birth * interval);
        s.ReviewDeaths = Math.Min(s.Population + s.ReviewBirths, (long)Math.Round(s.Population * (double)death * interval));
        EconomySystem.AdjustStatePopulation(s, s.ReviewBirths - s.ReviewDeaths);
        s.Radicalism = Math.Clamp(s.Radicalism + s.LivelihoodStress * .001f, 0, 1);
    }
}
