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
    public static float Labour(ScenarioState s)
    {
        var share = DemographicCycle.WorkingShare(s);
        var groups = s.PopulationGroups;
        if (groups.Count == 0) return s.Population * share * 1e-6f;
        var sum = 0.0;
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            sum += g.Size * share * 1e-6 * (g.Class == SocialClass.Upper ? .15 : 1);
        }
        return (float)sum;
    }

    public static float Employment(ScenarioState s)
    {
        var labour = Labour(s);
        if (labour <= 0) return 0;
        var share = DemographicCycle.WorkingShare(s);
        var groups = s.PopulationGroups;
        var sum = 0.0;
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            sum += g.Size * share * 1e-6 * (g.Class == SocialClass.Upper ? .15 : 1) * g.Employment;
        }
        return (float)sum / labour;
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
        var (birth, death, _) = VitalRates(c, n, s);
        s.ReviewBirths = (long)Math.Round(s.Population * (double)birth * interval);
        s.ReviewDeaths = Math.Min(s.Population + s.ReviewBirths, (long)Math.Round(s.Population * (double)death * interval));
        EconomySystem.AdjustStatePopulation(s, s.ReviewBirths - s.ReviewDeaths);
        s.Radicalism = Math.Clamp(s.Radicalism + s.LivelihoodStress * .001f, 0, 1);
        BeliefDynamics.Review(c, n, s, c.Defines.EconomyTickDays);
    }

    /// <summary>
    /// Multi-factor annual birth rate, death rate, and old-age burden (0..1) for a region,
    /// combining socioeconomic conditions with <see cref="DemographicCycle"/> age structure.
    /// When <paramref name="netBreakdown"/> is supplied, records each signed contribution.
    /// </summary>
    public static (float BirthAnnual, float DeathAnnual, float OldAgeBurden) VitalRates(
        Campaign c, Nation n, ScenarioState s, Breakdown netBreakdown = null)
    {
        if (c is null || n is null || s is null) return (0f, 0f, 0f);
        var damage = Math.Clamp(s.WarDamage, 0f, 1f);
        var food = Supply(n, Commodity.Food);
        var energy = Supply(n, Commodity.Energy);
        var medicine = Supply(n, Commodity.Pharmaceuticals);
        var coverage = food * .6f + energy * .25f + medicine * .15f;
        var scarcity = Math.Max(0f, 1f - coverage - c.Defines.ScarcityTolerance);
        var healthCapacity = s.Buildings.GetValueOrDefault(BuildingKind.Hospital) / Math.Max(.1f, s.Population / 1e6f);
        var health = healthCapacity / (1f + healthCapacity) * medicine;

        var baseBirth = c.Defines.DemographyBirthBaseAnnual;
        var lowLitBirth = (1f - s.Literacy) * c.Defines.DemographyLowLiteracyBirthAnnual;
        var urbanSqueeze = s.Urbanization > 0.55f ? -(s.Urbanization - 0.55f) * 0.010f : 0f;
        var postIndustrial = s.Literacy > 0.82f && s.Urbanization > 0.60f
            ? -(s.Literacy - 0.82f) * (s.Urbanization - 0.60f) * 0.032f
            : 0f;
        var familyWelfare = s.Urbanization > 0.01f && n.WelfareSpending > 0.06f
            ? (n.WelfareSpending - 0.06f) * 0.042f
            : 0f;
        var traditionBonus = s.Urbanization > 0.01f && n.Ideology is "traditionalism" or "theocratic_rule" or "agrarianism"
            ? 0.0035f
            : 0f;
        var secularPenalty = s.Urbanization > 0.01f && (StateIdentity.IsSecular(n.DeclaredReligion) || StateIdentity.IsAtheist(n.DeclaredReligion))
            ? -0.0020f
            : 0f;
        var pronatalist = ModifierSystem.Has(n, "pronatalist_drive") ? 0.0065f
            : ModifierSystem.Has(n, "demographic_boom") ? 0.0085f : 0f;
        var inflationDrag = s.Urbanization > 0.01f && n.Inflation > 0.08f
            ? -Math.Clamp(n.Inflation - 0.08f, 0f, 0.30f) * 0.014f
            : 0f;

        var atWar = c.Wars.Any(w => !w.Ended && w.Involves(n.Id));
        var exhaustion = M.Clamp01(1f - n.WarSupport);
        var mobilizationShock = exhaustion > 0.08f ? -Math.Min(0.0075f, (exhaustion - 0.08f) * 0.02f) : 0f;
        if (atWar) mobilizationShock -= 0.0020f;
        var warNestingDrag = damage > 0.04f ? -Math.Min(0.006f, (damage - 0.04f) * 0.012f) : 0f;
        var peaceBabyBoom = !atWar && n.Stability > 0.72f && damage < 0.02f
            ? (n.Stability - 0.72f) * 0.008f
            : 0f;

        var preAgeBirth = Math.Clamp(baseBirth + lowLitBirth + urbanSqueeze + postIndustrial +
            familyWelfare + traditionBonus + secularPenalty + pronatalist + inflationDrag +
            mobilizationShock + warNestingDrag + peaceBabyBoom, 0f, .08f);
        var fertilityFactor = c.Defines.DemographyAgeStructureEnabled ? DemographicCycle.FertilityFactor(s) : 1f;
        var ageBirthEffect = preAgeBirth * (fertilityFactor - 1f);
        var birth = Math.Clamp(preAgeBirth + ageBirthEffect, 0f, .08f);

        var baseDeath = c.Defines.DemographyDeathBaseAnnual;
        var povertyDeath = (1f - s.StandardOfLiving) * .004f;
        var shortageDeath = scarcity * c.Defines.DemographyShortageDeathAnnual;
        var warDeath = damage * .012f;
        var famineSpike = food < 0.40f ? (0.40f - food) * 0.045f : 0f;
        var winterExposureDeath = MathF.Abs(n.CapitalLatitude) > 35f && energy < 0.45f ? (0.45f - energy) * 0.015f : 0f;
        var epidemicDeath = medicine < 0.35f && (damage > 0.10f || scarcity > 0.15f) ? (0.35f - medicine) * 0.020f : 0f;
        var violenceDeath = s.Radicalism > 0.65f && s.Stability < 0.25f ? (s.Radicalism - 0.65f) * (0.25f - s.Stability) * 0.035f : 0f;

        var preHealthDeath = Math.Clamp(baseDeath + povertyDeath + shortageDeath + warDeath +
            famineSpike + winterExposureDeath + epidemicDeath + violenceDeath, 0f, .08f);
        var hospitalRelief = preHealthDeath * health * .25f;
        var postHealthDeath = preHealthDeath - hospitalRelief;
        var mortalityFactor = c.Defines.DemographyAgeStructureEnabled ? DemographicCycle.MortalityFactor(s, c.Defines) : 1f;
        var oldAgeDeathEffect = postHealthDeath * (mortalityFactor - 1f);
        var death = Math.Clamp(postHealthDeath + oldAgeDeathEffect, 0f, .08f);

        var oldAgeBurden = M.Clamp01(
            Math.Max(0f, s.OldShare - c.Defines.DemographyReferenceOldShare) * 4.5f +
            (death > birth ? (death - birth) * 35f : 0f));

        if (netBreakdown is not null)
        {
            netBreakdown.Begin(Loc.Get("Annual natural population change"), "share");
            netBreakdown.Add(Loc.Get("Base birth rate"), baseBirth);
            if (lowLitBirth > 0.0001f) netBreakdown.Add(Loc.Get("Rural & large-family fertility"), lowLitBirth);
            if (urbanSqueeze < -0.0001f) netBreakdown.Add(Loc.Get("Urban housing & density squeeze"), urbanSqueeze);
            if (postIndustrial < -0.0001f) netBreakdown.Add(Loc.Get("Post-industrial demographic transition"), postIndustrial);
            if (familyWelfare > 0.0001f) netBreakdown.Add(Loc.Get("Family welfare & social support"), familyWelfare);
            if (traditionBonus > 0.0001f) netBreakdown.Add(Loc.Get("Traditional & religious family norms"), traditionBonus);
            if (secularPenalty < -0.0001f) netBreakdown.Add(Loc.Get("Secular state norms"), secularPenalty);
            if (pronatalist > 0.0001f) netBreakdown.Add(Loc.Get("Pronatalist family programme"), pronatalist);
            if (inflationDrag < -0.0001f) netBreakdown.Add(Loc.Get("Inflation squeeze on households"), inflationDrag);
            if (mobilizationShock < -0.0001f) netBreakdown.Add(Loc.Get("Wartime mobilization & family separation"), mobilizationShock);
            if (warNestingDrag < -0.0001f) netBreakdown.Add(Loc.Get("Housing devastation & displacement"), warNestingDrag);
            if (peaceBabyBoom > 0.0001f) netBreakdown.Add(Loc.Get("Post-war recovery baby boom"), peaceBabyBoom);
            if (MathF.Abs(ageBirthEffect) > 0.0001f) netBreakdown.Add(Loc.Get("Young cohort share (mothers)"), ageBirthEffect);
            netBreakdown.Add(Loc.Get("Base mortality"), -baseDeath);
            if (oldAgeDeathEffect > 0.0001f) netBreakdown.Add(Loc.Get("Old-age mortality (aging pyramid)"), -oldAgeDeathEffect);
            if (povertyDeath > 0.0001f) netBreakdown.Add(Loc.Get("Low living standard mortality"), -povertyDeath);
            if (shortageDeath > 0.0001f) netBreakdown.Add(Loc.Get("Food, energy & medicine shortages"), -shortageDeath);
            if (warDeath > 0.0001f) netBreakdown.Add(Loc.Get("War devastation & bombardment"), -warDeath);
            if (famineSpike > 0.0001f) netBreakdown.Add(Loc.Get("Severe famine & starvation"), -famineSpike);
            if (winterExposureDeath > 0.0001f) netBreakdown.Add(Loc.Get("Winter exposure & heating deficit"), -winterExposureDeath);
            if (epidemicDeath > 0.0001f) netBreakdown.Add(Loc.Get("Epidemic outbreak & medical collapse"), -epidemicDeath);
            if (violenceDeath > 0.0001f) netBreakdown.Add(Loc.Get("Civil violence & public breakdown"), -violenceDeath);
            if (hospitalRelief > 0.0001f) netBreakdown.Add(Loc.Get("Hospitals & medical supply relief"), hospitalRelief);
            netBreakdown.Settle(birth - death, Loc.Get("Clamped vital bounds"));
        }

        return (birth, death, oldAgeBurden);
    }

    /// <summary>
    /// National demographic summary &amp; weekly people breakdown for top-bar and country dossier tooltips.
    /// Purely read-only: never mutates campaign state.
    /// </summary>
    public static Breakdown ExplainNationPopulation(Campaign c, Nation n)
    {
        var b = new Breakdown();
        if (c is null || n is null) return b;
        b.Begin(Loc.Get("Weekly demographic balance"), "people");
        long births = 0, deaths = 0;
        var interval = Math.Clamp(c.Defines.EconomyTickDays, 1, 365) / 365f;
        foreach (var s in c.StatesOf(n))
        {
            if (s is null || s.Population <= 0) continue;
            var (br, dr, _) = VitalRates(c, n, s);
            births += s.LastHouseholdReviewDay >= 0 ? s.ReviewBirths : (long)Math.Round(s.Population * (double)br * interval);
            deaths += s.LastHouseholdReviewDay >= 0 ? s.ReviewDeaths : (long)Math.Round(s.Population * (double)dr * interval);
        }
        b.Add(Loc.Get("Births (natural fertility)"), births);
        b.Add(Loc.Get("Deaths (mortality & old age)"), -deaths);
        if (n.WeeklyNetMigration != 0)
            b.Add(Loc.Get("Cross-border migration"), n.WeeklyNetMigration);
        b.Settle(births - deaths + n.WeeklyNetMigration, Loc.Get("Net weekly change"));
        return b;
    }

    /// <summary>
    /// National weighted annual vital rates breakdown for Country Dossier tooltips.
    /// </summary>
    public static (float BirthAnnual, float DeathAnnual, float OldAgeBurden, string StatusKey) NationalVitalSummary(
        Campaign c, Nation n, Breakdown explain = null)
    {
        if (c is null || n is null) return (0f, 0f, 0f, "Balanced growth");
        var states = c.StatesOf(n);
        if (states.Length == 0) return (0f, 0f, 0f, "Balanced growth");
        ScenarioState representative = states[0];
        double popSum = 0, bSum = 0, dSum = 0, aSum = 0;
        foreach (var s in states)
        {
            if (s is null || s.Population <= 0) continue;
            if (s.Population > representative.Population) representative = s;
            var (br, dr, ag) = VitalRates(c, n, s);
            popSum += s.Population;
            bSum += br * s.Population;
            dSum += dr * s.Population;
            aSum += ag * s.Population;
        }
        if (popSum <= 0) return (0f, 0f, 0f, "Balanced growth");
        if (explain is not null) VitalRates(c, n, representative, explain);
        var birth = (float)(bSum / popSum);
        var death = (float)(dSum / popSum);
        var aging = (float)(aSum / popSum);
        var status = death > birth && (n.OldShare >= c.Defines.DemographyReferenceOldShare + 0.03f || aging >= 0.25f)
            ? "Demographic winter (dying of old age)"
            : death > birth ? "Depopulation (mortality exceeds births)"
            : birth - death >= 0.012f ? "Demographic boom"
            : "Balanced growth";
        return (birth, death, aging, status);
    }
}
