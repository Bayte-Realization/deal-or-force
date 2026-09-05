using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public enum SocialClass { Workers, Farmers, MiddleClass, Owners }
public enum BuildingKind
{
    Agriculture,
    CivilianIndustry,
    Infrastructure,
    University,
    Hospital,
    Barracks,
    ExtractionIndustry,
    PowerPlant,
    ArmsIndustry
}

/// <summary>
/// A people, a faith, a trade, and how many of them there are in one region.
///
/// The four social classes the market prices are still here as `Class`; what is new is
/// that a group also knows who it is. A region is no longer four numbers but a table of
/// them, and the two things anyone actually wants to know about a place - who lives there
/// and what they do for a living - are facts about the simulation rather than captions on
/// a screen.
/// </summary>
public sealed class PopulationGroup
{
    public SocialClass Class;

    /// <summary>The people, by the id in common/cultures.</summary>
    public string Culture = "";

    /// <summary>What they believe, by the id in common/religions.</summary>
    public string Religion = "";

    /// <summary>What they do, by the id in common/professions.</summary>
    public string Profession = "";

    public long Size;
    public float Employment = 0.9f;
    public float Wealth = 0.5f;
    public float StandardOfLiving = 0.5f;
    public float Radicalism;
}

public sealed class ConstructionProject
{
    public int Id;
    public int NationId;
    public int StateMapIndex;
    public BuildingKind Building;
    public float Cost;
    public float Paid;
    public int DurationDays;
    public int ProgressDays;
    public float Progress => M.Clamp01(Math.Min(
        DurationDays <= 0 ? 1f : ProgressDays / (float)DurationDays,
        Cost <= 0f ? 1f : Paid / Cost));
}

public readonly record struct BuildingDefinition(
    string Name, float Cost, int Days, float GdpGain, float CapacityGain, string Description);

public static class BuildingCatalog
{
    private static readonly Dictionary<BuildingKind, BuildingDefinition> Defaults = new()
    {
        [BuildingKind.Agriculture] = new("Agricultural complex", 0.45f, 120, 0.22f, 0.05f,
            "Food output and rural employment."),
        [BuildingKind.CivilianIndustry] = new("Civilian industry", 2.40f, 240, 1.65f, 0.25f,
            "Manufacturing output, tax base and construction capacity."),
        [BuildingKind.Infrastructure] = new("Infrastructure", 1.35f, 180, 0.45f, 0.45f,
            "Movement, market access and supply throughput."),
        [BuildingKind.University] = new("University", 1.80f, 300, 0.30f, 0.05f,
            "Literacy and long-term productivity."),
        [BuildingKind.Hospital] = new("Hospital", 1.55f, 260, 0.18f, 0.05f,
            "Population growth and standard of living."),
        [BuildingKind.Barracks] = new("Barracks", 1.10f, 150, 0.12f, 0.05f,
            "Military readiness and reinforcement capacity."),
        [BuildingKind.ExtractionIndustry] = new("Extraction industry", 1.65f, 210, 0.85f, 0.12f,
            "Mines, wells and processing sites that exploit regional deposits."),
        [BuildingKind.PowerPlant] = new("Power plant", 2.05f, 230, 1.10f, 0.16f,
            "Reliable energy for households, mines and manufacturing."),
        [BuildingKind.ArmsIndustry] = new("Arms industry", 2.85f, 280, 1.45f, 0.10f,
            "Dedicated weapons and ammunition production for field forces.")
    };
    private static readonly Dictionary<BuildingKind, BuildingDefinition> Definitions = new();

    public static BuildingDefinition Get(BuildingKind kind) => Definitions[kind];
    public static IEnumerable<BuildingKind> All => Definitions.Keys;

    static BuildingCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Definitions.Clear();
        foreach (var entry in Defaults) Definitions[entry.Key] = entry.Value;
        foreach (var document in GameDataLoader.DataDocuments("common", "buildings"))
            using (document)
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (!Enum.TryParse<BuildingKind>(entry.Name, true, out var kind) || entry.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var value = entry.Value;
                Text(value, "name", out var name);
                Text(value, "description", out var description);
                Number(value, "cost", out var cost);
                Integer(value, "days", out var days);
                Number(value, "gdp_gain", out var gdp);
                Number(value, "capacity_gain", out var capacity);
                Override(kind, name, cost, days, gdp, capacity, description);
            }
    }

    public static bool Override(BuildingKind kind, string name, float? cost, int? days,
        float? gdpGain, float? capacityGain, string description)
    {
        if (!Definitions.TryGetValue(kind, out var current) || cost is <= 0f || days is <= 0 ||
            gdpGain is < 0f || capacityGain is < 0f) return false;
        Definitions[kind] = current with
        {
            Name = string.IsNullOrWhiteSpace(name) ? current.Name : name,
            Cost = cost ?? current.Cost,
            Days = days ?? current.Days,
            GdpGain = gdpGain ?? current.GdpGain,
            CapacityGain = capacityGain ?? current.CapacityGain,
            Description = string.IsNullOrWhiteSpace(description) ? current.Description : description
        };
        return true;
    }

    private static bool Text(JsonElement value, string key, out string result)
    {
        result = value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() : null;
        return result is not null;
    }

    private static bool Number(JsonElement value, string key, out float? result)
    {
        result = value.TryGetProperty(key, out var element) && element.TryGetSingle(out var number) ? number : null;
        return result is not null;
    }

    private static bool Integer(JsonElement value, string key, out int? result)
    {
        result = value.TryGetProperty(key, out var element) && element.TryGetInt32(out var number) ? number : null;
        return result is not null;
    }
}

public sealed class QueueConstructionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; }
    public BuildingKind Building { get; init; }

    public void Apply(Campaign campaign)
    {
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;

        // Three full scans with three closures, for an order the AI gives eight times a
        // day: the state by map index over seventeen hundred, the country's projects over
        // six hundred, and the highest id over the same six hundred again.
        ScenarioState state = null;
        foreach (var candidate in campaign.States)
            if (candidate.MapIndex == StateMapIndex) { state = candidate; break; }
        if (state is null || state.NationMapIndex != nation.MapIndex) return;

        var running = 0;
        var highest = 0;
        foreach (var project in campaign.Construction)
        {
            if (project.Id > highest) highest = project.Id;
            if (project.NationId == nation.Id && ++running >= 12) return;
        }

        var definition = BuildingCatalog.Get(Building);
        if (nation.Treasury < Math.Min(0.05f, definition.Cost)) return;
        campaign.Construction.Add(new ConstructionProject
        {
            Id = highest + 1,
            NationId = nation.Id,
            StateMapIndex = state.MapIndex,
            Building = Building,
            Cost = definition.Cost,
            DurationDays = definition.Days
        });
        campaign.MarkStrategicChange();
    }
}

public static class EconomySystem
{
    public static void Seed(Campaign campaign)
    {
        foreach (var nation in campaign.Nations)
        {
            nation.Literacy = M.Clamp(0.42f + MathF.Log10(Math.Max(1f, nation.Gdp)) * 0.10f, 0.32f, 0.98f);
            nation.StandardOfLiving = M.Clamp(0.34f + MathF.Log10(Math.Max(0.1f,
                nation.Gdp * 1_000_000_000f / Math.Max(1, nation.Population))) * 0.07f, 0.28f, 0.90f);
            nation.Productivity = 0.72f + nation.Literacy * 0.50f;
            nation.Stability = 0.58f + nation.StandardOfLiving * 0.25f;

            var states = campaign.StatesOf(nation);
            if (states.Length == 0) continue;

            // A region correction that states its own population keeps it, and the rest of
            // the country is spread over what is left. Spreading the whole national figure
            // over every region discarded those numbers without saying so - which is how
            // the Andaman Islands ended up with fifty million people and Nakhchivan with
            // one and a half.
            var authored = new long[states.Length];
            long claimed = 0;
            for (var index = 0; index < states.Length; index++)
                if (TerritorialTopology.AuthoredPopulation(states[index].MapIndex, out var stated))
                {
                    authored[index] = stated;
                    claimed += stated;
                }

            // If the authored regions between them claim more than the country has, they
            // are scaled to fit rather than allowed to invent people.
            var remaining = nation.Population - claimed;
            var scale = 1.0;
            if (remaining < 0 && claimed > 0)
            {
                scale = nation.Population / (double)claimed;
                remaining = 0;
            }

            // Nobody lives on a lake. Water regions are owned and drawn like any other, so
            // without this they take a full share of the country's population - which put two
            // million Ukrainians in the Kakhovka reservoir and forty-seven million Chinese in
            // Qinghai Lake, and took those people away from the land they actually live on.
            var free = Enumerable.Range(0, states.Length)
                .Where(index => authored[index] == 0 && !LakeRegions.IsWaterState(states[index])).ToArray();
            // Unless there is nowhere else: a country of nothing but water still has to put
            // its people somewhere, and losing them silently is worse than drowning them.
            if (free.Length == 0)
                free = Enumerable.Range(0, states.Length).Where(index => authored[index] == 0).ToArray();
            var weights = free.Select(index => 0.75 + StableFraction(states[index].MapIndex) * 0.50).ToArray();
            var totalWeight = weights.Sum();

            // Every region's head-count first, and only then who those people are. It cannot
            // be done a region at a time: a people who named the regions they live in have a
            // national number to place, and placing it means knowing how big those regions
            // are relative to one another.
            var counts = new long[states.Length];
            long assigned = 0;
            for (var slot = 0; slot < free.Length; slot++)
            {
                var index = free[slot];
                counts[index] = Math.Max(0, slot == free.Length - 1
                    ? remaining - assigned
                    : (long)(remaining * weights[slot] / totalWeight));
                assigned += counts[index];
            }
            for (var index = 0; index < states.Length; index++)
                if (authored[index] > 0) counts[index] = Math.Max(0, (long)(authored[index] * scale));

            var composition = DemographyCatalog.AllocateCultures(nation.Tag, states, counts);
            var farming = StatedAgricultureScale(nation, states, counts);
            for (var index = 0; index < states.Length; index++)
                SeedState(states[index], nation, counts[index], composition[index], farming);
        }
    }

    /// <summary>
    /// Ties the emergent economy to the scenario's historical figures. Run once, after
    /// states, buildings and pops exist: it prices one market pass and records, per
    /// nation, the factor that turns weekly gross output into the GDP the scenario was
    /// authored with. Day zero therefore still reads exactly 2024, and every week after
    /// that GDP moves because production moved.
    ///
    /// Without this the switch to emergent output would silently rewrite every country's
    /// GDP to whatever the building set happened to produce - accurate to the model and
    /// wrong against the world.
    /// </summary>
    public static void CalibrateOutput(Campaign campaign)
    {
        MarketSystem.Prime(campaign);

        foreach (var nation in campaign.Nations)
        {
            var annualised = nation.GrossOutput * 52f;
            nation.OutputScale = annualised > 0.0001f && nation.Gdp > 0f
                ? nation.Gdp / annualised
                : 1f;
        }

        LastCalibrationReport = BuildCalibrationReport(campaign);
    }

    /// <summary>
    /// Written by the last calibration pass, for the developer console. Verifying that
    /// day-zero emergent output still reproduces the authored 2024 figures is the whole
    /// point of the calibration, so the numbers that prove it stay readable in-game.
    /// </summary>
    public static string LastCalibrationReport { get; private set; } = "No calibration recorded.";

    private static string BuildCalibrationReport(Campaign campaign)
    {
        var lines = new List<string> { "Emergent output calibration (day zero):" };
        foreach (var nation in campaign.Nations
                     .Where(candidate => candidate.Gdp > 0f)
                     .OrderByDescending(candidate => candidate.Gdp)
                     .Take(10))
        {
            var emergent = nation.GrossOutput * 52f * nation.OutputScale;
            var drift = nation.Gdp <= 0f ? 0f : (emergent - nation.Gdp) / nation.Gdp * 100f;
            lines.Add($"  {nation.Tag} {nation.Name}: authored {nation.Gdp:N1}B, emergent {emergent:N1}B " +
                      $"({drift:+0.00;-0.00;0.00}%), scale {nation.OutputScale:0.####}, staffed {nation.EmployedShare:P0}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// What a rate of taxation is worth to this country in a week.
    ///
    /// The interface offers a slider and has to say what moving it would do, and the only
    /// honest way to answer is with the expression the weekly tick actually uses. Two
    /// copies of a formula are two formulas: the second one is right until somebody edits
    /// the first, and then the panel quietly lies about the country's finances.
    /// </summary>
    public static float WeeklyTaxAt(Nation nation, float rate) =>
        nation.Gdp / 52f * rate * 0.24f * (0.72f + nation.Stability * 0.22f);

    /// <summary>What a share of output spent on the armed forces costs in a week.</summary>
    public static float WeeklyMilitaryAt(Nation nation, float share) =>
        nation.Gdp / 52f * share * 0.22f;

    public static void Tick(Campaign campaign)
    {
        if (campaign.ElapsedDays == 0 || campaign.ElapsedDays % campaign.Defines.EconomyTickDays != 0) return;
        MarketSystem.Tick(campaign);
        foreach (var nation in campaign.Nations)
        {
            var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
            var weeklyOutput = nation.Gdp / 52f;
            // GDP is total economic activity, not the taxable budget base. Only a
            // calibrated share reaches the treasury; this keeps modern states near a
            // plausible balanced budget instead of banking 10-15% of GDP every year.
            nation.WeeklyTaxIncome = WeeklyTaxAt(nation, nation.TaxRate);

            // The budget is worked out in named pieces so the interface can show the
            // player what it is made of. The arithmetic is the same arithmetic; it is only
            // that the parts are kept instead of collapsed into one expression.
            var administration = weeklyOutput * 0.030f;
            var military = WeeklyMilitaryAt(nation, nation.MilitarySpending);
            var welfare = weeklyOutput * nation.WelfareSpending * 0.18f;
            var education = weeklyOutput * nation.EducationSpending * 0.14f;
            var unrest = weeklyOutput * (1f - nation.Stability) * 0.025f;
            nation.WeeklyExpenses = administration + military + welfare + education + unrest;
            nation.WeeklyBalance = nation.WeeklyTaxIncome + nation.WeeklyTariffIncome -
                                   nation.WeeklyExpenses + nation.WeeklyTradeBalance;

            nation.Budget.Begin("Weekly balance", "money");
            nation.Budget.Add($"Taxation at {nation.TaxRate:P0}", nation.WeeklyTaxIncome);
            nation.Budget.Add("Tariffs", nation.WeeklyTariffIncome);
            nation.Budget.Add("Trade", nation.WeeklyTradeBalance);
            nation.Budget.Add("Administration", -administration);
            nation.Budget.Add($"Armed forces at {nation.MilitarySpending:P0}", -military);
            nation.Budget.Add($"Welfare at {nation.WelfareSpending:P0}", -welfare);
            nation.Budget.Add($"Education at {nation.EducationSpending:P0}", -education);
            nation.Budget.Add("Policing unrest", -unrest);
            nation.Budget.Settle(nation.WeeklyBalance, "rounding");

            nation.Treasury += nation.WeeklyBalance;

            var industrialSupply = nation.Market.GetValueOrDefault(Commodity.IndustrialMaterials)?.Satisfaction ?? 1f;
            var energySupply = nation.Market.GetValueOrDefault(Commodity.Energy)?.Satisfaction ?? 1f;
            var marketOutput = 0.65f + industrialSupply * 0.18f + energySupply * 0.17f;

            // Everything that used to be applied straight to GDP now moves productivity
            // instead, and productivity is an input to production over in MarketCore. The
            // causal chain runs one way - policy and conditions change how much the
            // country can make, what it makes is priced by the market, and GDP is the
            // value of that - rather than GDP being a free-running number with the
            // economy simulated alongside it.
            //
            // Fiscal policy is still a real trade-off: rates below the neutral 22% leave
            // more private investment, high rates fund the state now and cost output later.
            var taxEffect = (0.22f - nation.TaxRate) * 0.030f;
            var conditions = 0.006f + (nation.Stability - 0.5f) * 0.018f - (atWar ? 0.018f : 0f) -
                             nation.ShortageIndex * campaign.Defines.ShortageAnnualGrowthPenalty + taxEffect +
                             GovernmentFocusRules.AnnualGrowth(nation.GovernmentFocus) -
                             (1f - marketOutput) * 0.020f;
            nation.Productivity = M.Clamp(nation.Productivity * (1f + conditions / 52f), 0.15f, 4f);

            // GDP is the annualised market value of what the country produced, converted
            // to the scale the scenario was authored in by a factor calibrated once at
            // seed. Smoothed, because an economy that reprices its entire output every
            // week reads as noise rather than as a trend the player can act on.
            if (nation.GrossOutput > 0f && nation.OutputScale > 0f)
            {
                var target = nation.GrossOutput * 52f * nation.OutputScale;
                nation.Gdp = MathF.Max(0.01f, M.Lerp(nation.Gdp, target, campaign.Defines.GdpConvergenceRate));
            }
            var annualGrowth = conditions;

            var states = campaign.StatesOf(nation);
            long totalPopulation = 0;
            foreach (var state in states)
            {
                var hospital = state.Buildings.GetValueOrDefault(BuildingKind.Hospital);
                var weeklyGrowth = (0.0035f + (state.StandardOfLiving - 0.5f) * 0.006f + hospital * 0.0005f) / 52f;
                var delta = (long)Math.Round(state.Population * weeklyGrowth);
                state.Population = Math.Max(0, state.Population + delta);
                GrowGroups(state, delta);
                state.StandardOfLiving = M.Lerp(state.StandardOfLiving, nation.StandardOfLiving, 0.04f);
                var welfareRadicalism = (0.055f - nation.WelfareSpending) * 0.0015f;
                var focusRadicalism = nation.GovernmentFocus switch
                {
                    GovernmentFocus.SocialCohesion => -0.00015f,
                    GovernmentFocus.NationalSecurity => 0.00005f,
                    _ => 0f
                };
                state.Radicalism = M.Clamp01(state.Radicalism +
                    (0.46f - state.StandardOfLiving) * 0.002f + welfareRadicalism + focusRadicalism);
                MinorityPressure(campaign, nation, state);
                totalPopulation += state.Population;
            }
            if (states.Length > 0) nation.Population = totalPopulation;
            var focusSol = nation.GovernmentFocus switch
            {
                GovernmentFocus.SocialCohesion => 0.00012f,
                GovernmentFocus.EconomicExpansion => -0.00004f,
                _ => 0f
            };
            nation.StandardOfLiving = M.Clamp01(nation.StandardOfLiving + annualGrowth * 0.004f -
                nation.ShortageIndex * campaign.Defines.ShortageSolWeeklyPenalty -
                MathF.Max(0f, nation.TaxRate - 0.32f) * 0.0006f +
                (nation.WelfareSpending - 0.055f) * 0.002f + focusSol);
            var focusLiteracy = nation.GovernmentFocus == GovernmentFocus.EducationAndScience ? 0.00008f : 0f;
            nation.Literacy = M.Clamp(nation.Literacy + nation.EducationSpending * 0.0012f + focusLiteracy,
                0.05f, 0.999f);
            var productivityTarget = 0.72f + nation.Literacy * 0.50f +
                                     (nation.GovernmentFocus == GovernmentFocus.EducationAndScience ? 0.04f : 0f);
            nation.Productivity = M.Lerp(nation.Productivity, productivityTarget, 0.015f);
            var securityStability = nation.GovernmentFocus == GovernmentFocus.NationalSecurity ? 0.00012f : 0f;
            var livingStandard = (nation.StandardOfLiving - 0.45f) * 0.001f;
            var settledStability = M.Clamp(nation.Stability + livingStandard + securityStability, 0.10f, 0.98f);

            // What is pushing stability this week, rather than only where it ended up. A
            // player who watches it fall for a year should be able to find out why in one
            // hover instead of by experiment.
            nation.StabilityDrivers.Begin("Stability, weekly", "share");
            nation.StabilityDrivers.Add(
                nation.StandardOfLiving >= 0.45f ? "Standard of living" : "Standard of living is poor",
                livingStandard);
            nation.StabilityDrivers.Add("Government focus on national security", securityStability);
            nation.StabilityDrivers.Settle(settledStability - nation.Stability,
                settledStability >= 0.98f ? "already as settled as a country gets"
                    : settledStability <= 0.10f ? "held up off the floor" : "rounding");
            nation.Stability = settledStability;

            var focusWarSupport = nation.GovernmentFocus == GovernmentFocus.NationalSecurity ? 0.00010f : 0f;
            var settledWarSupport = M.Clamp01(nation.WarSupport + focusWarSupport);
            nation.WarSupportDrivers.Begin("War support, weekly", "share");
            nation.WarSupportDrivers.Add("Government focus on national security", focusWarSupport);
            nation.WarSupportDrivers.Settle(settledWarSupport - nation.WarSupport, "held at its limit");
            nation.WarSupport = settledWarSupport;
        }
    }

    /// <summary>
    /// The share of a region's people who are not of the nation that governs them.
    ///
    /// At the scenario's start this is zero everywhere: every region is populated by its
    /// own country. It becomes non-zero the moment a border moves, because people do not
    /// change nationality when a treaty is signed - and that is the whole point. A region
    /// taken by force is full of people who did not choose it, and it stays restive until
    /// a generation has passed.
    /// </summary>
    public static float ForeignShare(ScenarioState state, string ownerTag)
    {
        if (state is null || string.IsNullOrEmpty(ownerTag) || state.PopulationGroups.Count == 0) return 0f;
        long total = 0;
        long foreign = 0;
        foreach (var group in state.PopulationGroups)
        {
            if (group.Size <= 0) continue;
            total += group.Size;
            // A minority is a people the state is not for. Which peoples those are is data:
            // Belgium has two of its own and Switzerland four, and a rule that assumed one
            // would make foreigners of half of both.
            if (!DemographyCatalog.IsPrimary(ownerTag, group.Culture)) foreign += group.Size;
        }
        return total <= 0 ? 0f : foreign / (float)total;
    }

    /// <summary>Every people living in a region, largest first.</summary>
    public static IReadOnlyList<(string Culture, long Size)> Cultures(ScenarioState state) =>
        Tally(state, group => group.Culture);

    /// <summary>Every faith held in a region, largest first.</summary>
    public static IReadOnlyList<(string Religion, long Size)> Religions(ScenarioState state) =>
        Tally(state, group => group.Religion);

    /// <summary>Every trade worked in a region, largest first.</summary>
    public static IReadOnlyList<(string Profession, long Size)> Professions(ScenarioState state) =>
        Tally(state, group => group.Profession);

    /// <summary>Every social class in a region, largest first.</summary>
    public static IReadOnlyList<(string Class, long Size)> Classes(ScenarioState state) =>
        Tally(state, group => group.Class.ToString());

    private static IReadOnlyList<(string Key, long Size)> Tally(ScenarioState state, Func<PopulationGroup, string> of)
    {
        if (state is null) return Array.Empty<(string, long)>();
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in state.PopulationGroups)
        {
            if (group.Size <= 0) continue;
            var key = of(group);
            if (string.IsNullOrEmpty(key)) key = "?";
            totals[key] = totals.GetValueOrDefault(key) + group.Size;
        }
        return totals.OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value))
            .ToList();
    }

    /// <summary>
    /// A week of living under a flag that is not yours. Minorities are more restive than
    /// the people around them, and they slowly stop being minorities: schooling, a decent
    /// standard of living and time all assimilate, while anger slows it down. A conquest
    /// is therefore expensive for years and eventually is not - which is the difference
    /// between a border that has moved and one that is still moving.
    /// </summary>
    private static void MinorityPressure(Campaign campaign, Nation nation, ScenarioState state)
    {
        var defines = campaign.Defines;
        var foreign = ForeignShare(state, nation.Tag);
        if (foreign <= 0.0001f) return;

        state.Radicalism = M.Clamp01(state.Radicalism + foreign * defines.MinorityRadicalismPerWeek);

        // Assimilation. Fast where people are schooled and comfortable, slow where they
        // are angry, and never instant.
        var rate = defines.AssimilationPerWeek *
                   M.Clamp(state.Literacy + state.StandardOfLiving, 0.2f, 2f) *
                   M.Clamp(1f - state.Radicalism, 0.05f, 1f);
        if (rate <= 0f) return;

        long moved = 0;
        foreach (var group in state.PopulationGroups)
        {
            if (group.Size <= 0) continue;
            if (DemographyCatalog.IsPrimary(nation.Tag, group.Culture)) continue;
            var share = (long)MathF.Round(group.Size * rate);
            if (share <= 0) continue;
            group.Size -= share;
            moved += share;
        }
        if (moved <= 0) return;

        // They become the first of the peoples the state is for. Which one that is comes
        // from the data, so assimilating into Switzerland does not turn everybody German.
        var into = DemographyCatalog.PrimaryCultures(nation.Tag).FirstOrDefault()
                   ?? nation.Tag.ToLowerInvariant();
        var host = state.PopulationGroups.Find(group =>
            string.Equals(group.Culture, into, StringComparison.OrdinalIgnoreCase));
        if (host is null)
        {
            host = new PopulationGroup
            {
                Class = SocialClass.Workers,
                Culture = into,
                Religion = DemographyCatalog.FaithOf(into)[0].Religion,
                Profession = "labourers",
                Size = 0,
                Employment = 0.90f,
                Wealth = 0.38f,
                StandardOfLiving = state.StandardOfLiving,
                Radicalism = state.Radicalism
            };
            state.PopulationGroups.Add(host);
        }
        host.Size += moved;
        state.PopulationGroups.RemoveAll(group => group.Size <= 0 && group != host);
    }

    internal static void SeedState(ScenarioState state, Nation nation, long population) =>
        SeedState(state, nation, population, null, (1f, 1f));

    /// <summary>
    /// Fill one region with the people who live in it.
    ///
    /// Every people is crossed with every trade, so a region is a table rather than a total:
    /// Tatar peasants and Russian machinists are separate rows, they answer to the same
    /// market as before through the social class each trade belongs to, and the population
    /// screen has something true to draw. The trades are weighted by how urban the region
    /// is, which is why Bangladesh and Belgium come out differently without either being
    /// written down anywhere.
    ///
    /// Groups below a thousandth of the region are folded away. They are noise on any chart
    /// and they are a tenth of the simulation's population objects.
    /// </summary>
    internal static void SeedState(ScenarioState state, Nation nation, long population,
        IReadOnlyList<(string Culture, float Share)> composition, (float Fields, float Town) farming)
    {
        state.Population = population;
        state.Literacy = M.Clamp01(nation.Literacy + (StableFraction(state.MapIndex + 17) - 0.5f) * 0.12f);
        state.StandardOfLiving = M.Clamp01(nation.StandardOfLiving + (StableFraction(state.MapIndex + 31) - 0.5f) * 0.10f);
        state.Radicalism = M.Clamp01((0.52f - state.StandardOfLiving) * 0.40f);
        state.Urbanization = StateUrbanisation(nation, state.MapIndex);
        state.PopulationGroups.Clear();
        BuildGroups(state, nation, population, composition, farming);
        state.Buildings[BuildingKind.Agriculture] = Math.Max(1, (int)(population / 4_000_000));
        state.Buildings[BuildingKind.CivilianIndustry] = Math.Max(0, (int)(population * state.Urbanization / 5_000_000));
        state.Buildings[BuildingKind.Infrastructure] = Math.Max(1, (int)(state.Urbanization * 4));
        SeedResources(state, nation);
    }

    /// <summary>
    /// How urban one region is.
    ///
    /// A country is not one place: some of its regions are the city and some are the land
    /// behind it, which is the whole reason the trades differ from region to region. The
    /// spread is fixed to the region's own index, so it is the same on every machine and
    /// can be worked out ahead of time - which the country-wide arithmetic needs.
    /// </summary>
    internal static float StateUrbanisation(Nation nation, int mapIndex) =>
        M.Clamp(NationalUrbanisation(nation) + (StableFraction(mapIndex + 53) - 0.5f) * 0.26f, 0.06f, 0.95f);

    /// <summary>
    /// How urban a country is, stated or implied.
    ///
    /// Implied logarithmically from income per head, because the step from four hundred
    /// dollars a year to four thousand empties far more villages than the step from forty
    /// thousand to eighty. The line this replaced divided billions by millions and made
    /// Germany twenty-seven per cent urban, which put half of Bavaria behind a plough.
    /// </summary>
    internal static float NationalUrbanisation(Nation nation)
    {
        if (nation.Urbanisation > 0f) return M.Clamp01(nation.Urbanisation);
        var perHead = nation.Gdp * 1_000_000_000f / Math.Max(1f, nation.Population);
        return M.Clamp(0.12f + 0.27f * MathF.Log10(Math.Max(300f, perHead) / 300f), 0.10f, 0.90f);
    }

    private static void SeedResources(ScenarioState state, Nation nation)
    {
        state.ResourceDeposits.Clear();
        var basePotential = 0.15f + StableFraction(state.MapIndex + 101) * 0.55f;
        state.ResourceDeposits[Commodity.Food] = 0.75f + StableFraction(state.MapIndex + 131) * 1.75f;
        state.ResourceDeposits[Commodity.Coal] = TaggedPotential(nation.Tag,
            "CHN USA IND RUS AUS DEU POL IDN ZAF UKR", basePotential, state.MapIndex + 151);
        state.ResourceDeposits[Commodity.Oil] = TaggedPotential(nation.Tag,
            "SAU RUS USA CAN IRN IRQ ARE VEN KWT QAT NOR KAZ AZE NGA BRA", basePotential * 0.55f, state.MapIndex + 181);
        state.ResourceDeposits[Commodity.IronOre] = TaggedPotential(nation.Tag,
            "AUS BRA CHN IND RUS UKR CAN SWE ZAF KAZ", basePotential * 0.75f, state.MapIndex + 211);
        state.ResourceDeposits[Commodity.RareEarths] = TaggedPotential(nation.Tag,
            "CHN USA AUS MMR VNM BRA IND RUS", basePotential * 0.22f, state.MapIndex + 241);
    }

    private static float TaggedPotential(string tag, string richTags, float fallback, int seed)
    {
        var rich = richTags.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(tag, StringComparer.OrdinalIgnoreCase);
        return rich
            ? 1.25f + StableFraction(seed) * 3.25f
            : fallback * (0.35f + StableFraction(seed) * 0.65f);
    }

    internal static void AdjustStatePopulation(ScenarioState state, long delta)
    {
        if (state is null || delta == 0) return;
        var bounded = delta < 0 ? Math.Max(delta, -state.Population) : delta;
        state.Population = Math.Max(0, state.Population + bounded);
        GrowGroups(state, bounded);
    }

    private static void BuildGroups(ScenarioState state, Nation nation, long population,
        IReadOnlyList<(string Culture, float Share)> composition, (float Fields, float Town) farming)
    {
        if (population <= 0) return;

        // No data for this country at all: it is one people, named after itself. Wrong in
        // detail, right in shape, and the population screen says so rather than inventing
        // a breakdown nobody wrote.
        if (composition is null || composition.Count == 0)
            composition = new[] { (nation.Tag.ToLowerInvariant(), 1f) };

        var trades = DemographyCatalog.Professions;
        if (trades.Count == 0)
        {
            // Without a trade catalogue there is still a population; it simply has no
            // occupations. The economy only needs the classes, so seed those directly.
            foreach (var (culture, share) in composition)
                foreach (var socialClass in Enum.GetValues<SocialClass>())
                    Add(state, culture, "", socialClass.ToString(), socialClass,
                        (long)(population * share * (socialClass == SocialClass.Owners ? 0.06f :
                            socialClass == SocialClass.MiddleClass ? 0.22f :
                            socialClass == SocialClass.Workers ? 0.38f : 0.34f)),
                        socialClass == SocialClass.Farmers ? 0.82f : 0.90f,
                        socialClass == SocialClass.Owners ? 0.86f : socialClass == SocialClass.MiddleClass ? 0.62f : 0.38f);
            return;
        }

        var weights = trades.Select(trade => Math.Max(0f, trade.ShareAt(state.Urbanization))).ToArray();
        var weightTotal = weights.Sum();
        if (weightTotal <= 0f) return;
        if (farming.Fields != 1f || farming.Town != 1f)
        {
            for (var index = 0; index < trades.Count; index++)
                weights[index] *= trades[index].Class == SocialClass.Farmers ? farming.Fields : farming.Town;
            weightTotal = weights.Sum();
            if (weightTotal <= 0f) return;
        }

        var floor = Math.Max(1L, population / 1000);
        long placed = 0;
        PopulationGroup largest = null;
        foreach (var (culture, share) in composition)
        {
            var faith = DemographyCatalog.FaithOf(culture);
            var people = (long)(population * share);
            if (people <= 0) continue;
            for (var index = 0; index < trades.Count; index++)
            {
                var trade = trades[index];
                var ofTrade = (long)(people * (weights[index] / weightTotal));
                if (ofTrade < floor) continue;
                foreach (var (religion, faithShare) in faith)
                {
                    var size = (long)(ofTrade * faithShare);
                    if (size < floor) continue;
                    var group = Add(state, culture, religion, trade.Id, trade.Class, size,
                        trade.Employment, trade.Wealth);
                    placed += size;
                    if (largest is null || group.Size > largest.Size) largest = group;
                }
            }
        }

        // Whatever the rounding and the floor left over goes to the largest group, so the
        // region's people add up to the region's population exactly.
        if (largest is null)
        {
            var (culture, _) = composition[0];
            largest = Add(state, culture, DemographyCatalog.FaithOf(culture)[0].Religion,
                trades[0].Id, trades[0].Class, 0, trades[0].Employment, trades[0].Wealth);
        }
        largest.Size += population - placed;
        if (largest.Size < 0) largest.Size = 0;
    }

    /// <summary>
    /// Make a country's stated share of farm work come true, without flattening its regions.
    ///
    /// The curve gets the shape of a country right and the level only roughly - it has one
    /// national number to work from, and the real figure spans one and a half per cent in
    /// Germany to two thirds in Burundi. Where a scenario states the number, the farming
    /// trades are scaled by however much the curve missed it, and every region keeps its own
    /// position around that average: the fields near the city still hold fewer farmers than
    /// the ones three hundred kilometres out.
    ///
    /// Measured across the country's actual regions rather than at its average urbanisation,
    /// because the curve bends. The average of a bent line is not the line at the average -
    /// aiming at the midpoint left Rwanda ten points too agricultural and Liberia ten points
    /// too little, in opposite directions, which is exactly the signature of that mistake.
    /// </summary>
    private static (float Fields, float Town) StatedAgricultureScale(Nation nation,
        IReadOnlyList<ScenarioState> states, IReadOnlyList<long> counts)
    {
        var trades = DemographyCatalog.Professions;
        if (nation.AgricultureShare <= 0f || trades.Count == 0) return (1f, 1f);

        double farmed = 0, everything = 0;
        for (var index = 0; index < states.Count; index++)
        {
            var people = Math.Max(0, counts[index]);
            if (people <= 0) continue;
            var urban = StateUrbanisation(nation, states[index].MapIndex);
            double here = 0, fields = 0;
            foreach (var trade in trades)
            {
                var share = Math.Max(0f, trade.ShareAt(urban));
                here += share;
                if (trade.Class == SocialClass.Farmers) fields += share;
            }
            if (here <= 0) continue;
            farmed += people * (fields / here);
            everything += people;
        }
        if (everything <= 0 || farmed <= 0) return (1f, 1f);

        var believed = farmed / everything;
        var stated = M.Clamp(nation.AgricultureShare, 0.001f, 0.95f);
        // Scale the fields by one factor and everything else by another, so the two together
        // still fill the country: no trade is invented and none is quietly lost.
        return ((float)(stated / believed), (float)((1 - stated) / Math.Max(0.0001, 1 - believed)));
    }

    private static PopulationGroup Add(ScenarioState state, string culture, string religion,
        string profession, SocialClass socialClass, long size, float employment, float wealth)
    {
        var group = new PopulationGroup
        {
            Class = socialClass,
            Culture = culture,
            Religion = religion,
            Profession = profession,
            Size = size,
            Employment = employment,
            Wealth = wealth,
            StandardOfLiving = M.Clamp01(state.StandardOfLiving + (wealth - 0.4f) * 0.5f),
            Radicalism = state.Radicalism
        };
        state.PopulationGroups.Add(group);
        return group;
    }

    private static void GrowGroups(ScenarioState state, long delta)
    {
        if (state.PopulationGroups.Count == 0 || delta == 0) return;
        var remaining = delta;
        for (var index = 0; index < state.PopulationGroups.Count; index++)
        {
            var addition = index == state.PopulationGroups.Count - 1
                ? remaining
                : (long)Math.Round(delta * state.PopulationGroups[index].Size / (double)Math.Max(1, state.Population));
            state.PopulationGroups[index].Size = Math.Max(0, state.PopulationGroups[index].Size + addition);
            remaining -= addition;
        }
    }

    private static float StableFraction(int value)
    {
        var x = (uint)value * 747796405u + 2891336453u;
        x = ((x >> ((int)(x >> 28) + 4)) ^ x) * 277803737u;
        x = (x >> 22) ^ x;
        return (x & 0xFFFFFF) / 16777216f;
    }
}

public static class ConstructionSystem
{
    public static void Tick(Campaign campaign)
    {
        // The oldest unfinished project each country has, found by hand. Where().OrderBy()
        // .FirstOrDefault() built a closure, an enumerator, a sorter and its key buffer for
        // every one of the two hundred and sixty-one countries every single day, to pick
        // one item out of a list that is usually a few hundred long.
        foreach (var nation in campaign.Nations)
        {
            ConstructionProject project = null;
            foreach (var item in campaign.Construction)
            {
                if (item.NationId != nation.Id) continue;
                if (project is null || item.Id < project.Id) project = item;
            }
            if (project is null) continue;
            var required = project.Cost / Math.Max(1, project.DurationDays);
            var paid = Math.Min(required * Math.Max(0.25f, nation.ConstructionCapacity), Math.Max(0f, nation.Treasury));
            if (paid <= 0f) continue;
            nation.Treasury -= paid;
            project.Paid += paid;
            project.ProgressDays++;
            if (project.Progress < 0.999f) continue;
            Complete(campaign, nation, project);
            campaign.Construction.Remove(project);
        }
    }

    private static void Complete(Campaign campaign, Nation nation, ConstructionProject project)
    {
        ScenarioState state = null;
        foreach (var candidate in campaign.States)
            if (candidate.MapIndex == project.StateMapIndex) { state = candidate; break; }
        if (state is null) return;
        var definition = BuildingCatalog.Get(project.Building);
        state.Buildings[project.Building] = state.Buildings.GetValueOrDefault(project.Building) + 1;
        nation.Gdp += definition.GdpGain;
        nation.ConstructionCapacity += definition.CapacityGain;
        if (project.Building == BuildingKind.University)
        {
            state.Literacy = M.Clamp01(state.Literacy + 0.025f);
            nation.Literacy = M.Clamp01(nation.Literacy + 0.003f);
            nation.Productivity += 0.006f;
        }
        if (project.Building == BuildingKind.Hospital)
            state.StandardOfLiving = M.Clamp01(state.StandardOfLiving + 0.018f);
    }
}
