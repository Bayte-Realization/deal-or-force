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
    ArmsIndustry,

    // The second tier. Four of these refine something into something else, which is what
    // gives the new goods their chains; two of them - the port and the rail network - are
    // not industry at all but reach, and decide how much of the world a country can
    // actually trade with. A landlocked country with no port is a country whose foreign
    // policy has to be about its neighbours, and that is a far more interesting country
    // to play than one for whom the world market was a constant.
    Refinery,
    ChemicalPlant,
    Smelter,
    ElectronicsPlant,
    Shipyard,
    Port,
    RailNetwork,
    ResearchLab
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
            "Dedicated weapons and ammunition production for field forces."),
        [BuildingKind.Refinery] = new("Refinery", 2.20f, 250, 1.05f, 0.08f,
            "Turns crude oil and gas into fuel, energy and chemical feedstock."),
        [BuildingKind.ChemicalPlant] = new("Chemical plant", 2.55f, 265, 1.20f, 0.09f,
            "Chemicals, fertiliser and medicine out of oil, gas and coal."),
        [BuildingKind.Smelter] = new("Smelter", 2.10f, 235, 0.95f, 0.11f,
            "Ore and bauxite into industrial materials and aluminium. Drinks electricity."),
        [BuildingKind.ElectronicsPlant] = new("Electronics plant", 3.15f, 300, 1.75f, 0.08f,
            "Chips and control systems from rare earths, chemicals and aluminium."),
        [BuildingKind.Shipyard] = new("Shipyard", 2.75f, 290, 0.90f, 0.10f,
            "Hull capacity for the navy and the merchant marine that carries its trade."),
        [BuildingKind.Port] = new("Port", 1.70f, 200, 0.55f, 0.18f,
            "How much of the world this country can trade with across water."),
        [BuildingKind.RailNetwork] = new("Rail network", 1.95f, 215, 0.60f, 0.30f,
            "Overland throughput: supply to the front, and goods to the neighbours."),
        [BuildingKind.ResearchLab] = new("Research laboratory", 2.30f, 320, 0.65f, 0.06f,
            "Productivity, literacy and the medicine a modern population expects.")
    };
    private static readonly Dictionary<BuildingKind, BuildingDefinition> Definitions = new();

    /// <summary>
    /// What each building is called in a file, and which building that is - see the same
    /// note on `CommodityCatalog`: the enum names the ones the game ships and does not stop
    /// a mod declaring an eighteenth.
    /// </summary>
    private static readonly Dictionary<string, BuildingKind> Named =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<BuildingKind, string> Ids = new();
    private static int _nextId;

    private static readonly BuildingDefinition Unknown =
        new("Unknown building", 1f, 180, 0f, 0f, "Declared by content that is no longer loaded.");

    public static BuildingDefinition Get(BuildingKind kind) =>
        Definitions.TryGetValue(kind, out var definition) ? definition : Unknown;

    public static IEnumerable<BuildingKind> All => Definitions.Keys;

    /// <summary>The name this building has in a file, which is what a save should be keyed by.</summary>
    public static string IdOf(BuildingKind kind) =>
        Ids.TryGetValue(kind, out var id) ? id : ((int)kind).ToString();

    private static string Squash(string id) => id is null
        ? ""
        : id.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    private static void Register(BuildingKind kind, string id)
    {
        Named[Squash(id)] = kind;
        Ids[kind] = id;
        if ((int)kind >= _nextId) _nextId = (int)kind + 1;
    }

    /// <summary>Which building a file means, if the catalog has one by that name.</summary>
    public static bool TryParse(string id, out BuildingKind kind) =>
        Named.TryGetValue(Squash(id), out kind);

    static BuildingCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Definitions.Clear();
        Named.Clear();
        Ids.Clear();
        _nextId = 0;
        foreach (var entry in Defaults)
        {
            Definitions[entry.Key] = entry.Value;
            Register(entry.Key, entry.Key.ToString());
        }
        foreach (var document in GameDataLoader.DataDocuments("common", "buildings"))
            using (document)
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var value = entry.Value;
                Text(value, "name", out var name);
                Text(value, "description", out var description);
                Number(value, "cost", out var cost);
                Integer(value, "days", out var days);
                Number(value, "gdp_gain", out var gdp);
                Number(value, "capacity_gain", out var capacity);
                Declare(entry.Name, name, cost, days, gdp, capacity, description);
            }
    }

    /// <summary>
    /// Change a building, or declare one that did not exist.
    ///
    /// A new building needs a name, a cost and a build time; without them it is a typo. What
    /// it does not get for free is a place in any production chain - a building nothing is
    /// made out of raises GDP and employs people and nothing else, which is a legitimate
    /// thing to want and also the first thing to check when a mod's new works does nothing.
    /// Give it output in `common/production`.
    /// </summary>
    public static bool Declare(string id, string name, float? cost, int? days,
        float? gdpGain, float? capacityGain, string description)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (TryParse(id, out var existing))
            return Override(existing, name, cost, days, gdpGain, capacityGain, description);
        if (string.IsNullOrWhiteSpace(name) || cost is not > 0f || days is not > 0) return false;
        var kind = (BuildingKind)_nextId;
        Register(kind, id);
        Definitions[kind] = new BuildingDefinition(name, cost.Value, days.Value,
            gdpGain ?? 0f, capacityGain ?? 0f, description ?? "");
        return true;
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
    public static float WeeklyTaxAt(Nation nation, float rate, bool atWar = false) =>
        BudgetCore.AmountAtRate(nation, BudgetCore.Find("income_tax"), nation.Gdp / 52f, atWar, rate);

    /// <summary>What a share of output spent on the armed forces costs in a week.</summary>
    public static float WeeklyMilitaryAt(Nation nation, float share, bool atWar = false) =>
        BudgetCore.AmountAtRate(nation, BudgetCore.Find("armed_forces"), nation.Gdp / 52f, atWar, share);

    public static void Tick(Campaign campaign)
    {
        if (campaign.ElapsedDays == 0 || campaign.ElapsedDays % campaign.Defines.EconomyTickDays != 0) return;
        using (SimProfiler.Measure("eco:market")) MarketSystem.Tick(campaign);
        foreach (var nation in campaign.Nations)
        {
            var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
            var weeklyOutput = nation.Gdp / 52f;
            // GDP is total economic activity, not the taxable budget base. Only a
            // calibrated share reaches the treasury; this keeps modern states near a
            // plausible balanced budget instead of banking 10-15% of GDP every year.
            //
            // What that share is, and what it is spent on, is now `common/budget` rather
            // than eight multiplications written out here. The eight lines this replaced
            // are the first eight rows of that file and produce the same figures; what it
            // buys is that a ninth is a row rather than an edit to the weekly tick.
            // Looked up once. Every one of the cabinet's seven effects used to be its own
            // switch on the enum, in five different places in this method.
            var cabinet = GovernmentFocusCatalog.Of(nation.GovernmentFocus);
            // How the country is arranged, and what it is trying to do. Two more sources of
            // the same six pressures the cabinet applies, looked up once for the same reason.
            var government = PoliticsCore.GovernmentOf(nation);
            var ideology = PoliticsCore.IdeologyOf(nation);
            // An ideology living in a form of government it has no business in. Not
            // forbidden - it is a real arrangement - but it costs every week it lasts.
            var mismatch = PoliticsCore.IsMismatched(nation) ? ideology.MismatchStability : 0f;
            var budget = BudgetCore.Evaluate(nation, weeklyOutput, atWar);
            nation.WeeklyTaxIncome = budget.Taxation;
            nation.WeeklyExpenses = budget.Expenses;
            nation.WeeklyBalance = budget.Balance;

            // Itemised for whoever is going to read it, which is a human. The labels that
            // carry a rate have to be composed fresh every week, and composing them for the
            // two hundred and sixty cabinets nobody is looking at was fifty thousand
            // sentences a simulated year written for an empty room.
            if (nation.IsHuman)
            {
                BudgetCore.Itemise(nation, weeklyOutput, atWar);
                nation.Budget.Settle(nation.WeeklyBalance, Loc.Get("rounding"));
            }

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
            // Fiscal policy is still a real trade-off, and now every line of it is: a tax
            // above its neutral rate funds the state now and costs output later, and the
            // three lines that buy growth - infrastructure, research, and the schools -
            // pay it back over years rather than weeks. Each line says which by how much
            // in `common/budget`; none of it is a constant here any more.
            var taxEffect = budget.Growth;
            var conditions = 0.006f + (nation.Stability - 0.5f) * 0.018f - (atWar ? 0.018f : 0f) -
                             nation.ShortageIndex * campaign.Defines.ShortageAnnualGrowthPenalty + taxEffect +
                             GovernmentFocusRules.AnnualGrowth(nation.GovernmentFocus) +
                             government.Growth + ideology.Growth -
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
            using var stateScope = SimProfiler.Measure("eco:states");
            foreach (var state in states)
            {
                var hospital = state.Buildings.GetValueOrDefault(BuildingKind.Hospital);
                var weeklyGrowth = (0.0035f + (state.StandardOfLiving - 0.5f) * 0.006f + hospital * 0.0005f) / 52f;
                var delta = (long)Math.Round(state.Population * weeklyGrowth);
                state.Population = Math.Max(0, state.Population + delta);
                GrowGroups(state, delta);
                state.StandardOfLiving = M.Lerp(state.StandardOfLiving, nation.StandardOfLiving, 0.04f);
                var welfareRadicalism = budget.Radicalism;
                var focusRadicalism = cabinet.Radicalism + government.Radicalism + ideology.Radicalism;
                state.Radicalism = M.Clamp01(state.Radicalism +
                    (0.46f - state.StandardOfLiving) * 0.002f + welfareRadicalism + focusRadicalism);
                MinorityPressure(campaign, nation, state);
                totalPopulation += state.Population;
            }
            if (states.Length > 0) nation.Population = totalPopulation;
            var focusSol = cabinet.Living + government.Living + ideology.Living;
            nation.StandardOfLiving = M.Clamp01(nation.StandardOfLiving + annualGrowth * 0.004f -
                nation.ShortageIndex * campaign.Defines.ShortageSolWeeklyPenalty +
                budget.Living + focusSol);
            var focusLiteracy = cabinet.Literacy + government.Literacy + ideology.Literacy;
            nation.Literacy = M.Clamp(nation.Literacy + budget.Literacy + focusLiteracy,
                0.05f, 0.999f);
            var productivityTarget = 0.72f + nation.Literacy * 0.50f + cabinet.ProductivityTarget;
            nation.Productivity = M.Lerp(nation.Productivity, productivityTarget, 0.015f);
            var securityStability = cabinet.Stability + government.Stability + ideology.Stability + mismatch;
            var livingStandard = (nation.StandardOfLiving - 0.45f) * 0.001f;
            var settledStability = M.Clamp(
                nation.Stability + livingStandard + securityStability + budget.Stability, 0.10f, 0.98f);

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

            var focusWarSupport = cabinet.WarSupport + government.WarSupport + ideology.WarSupport;
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
        var into = DemographyCatalog.PrimaryCulture(nation.Tag);
        // By hand rather than by List.Find: the predicate would capture `into`, and a
        // closure allocated once per region per week is a closure allocated a hundred
        // thousand times a simulated year.
        PopulationGroup host = null;
        for (var index = 0; index < state.PopulationGroups.Count; index++)
        {
            var candidate = state.PopulationGroups[index];
            if (!string.Equals(candidate.Culture, into, StringComparison.OrdinalIgnoreCase)) continue;
            host = candidate;
            break;
        }
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
            "SAU RUS USA CAN IRN IRQ ARE VEN KWT QAT NOR KAZ AZE NGA BRA", basePotential * 0.55f, state.MapIndex + 181) * 2.85f;
        state.ResourceDeposits[Commodity.IronOre] = TaggedPotential(nation.Tag,
            "AUS BRA CHN IND RUS UKR CAN SWE ZAF KAZ", basePotential * 0.75f, state.MapIndex + 211);
        state.ResourceDeposits[Commodity.RareEarths] = TaggedPotential(nation.Tag,
            "CHN USA AUS MMR VNM BRA IND RUS", basePotential * 0.22f, state.MapIndex + 241);

        // The four the second tier of industry starts from. Gas and bauxite decide who can
        // run a chemical industry and a smelter at all; uranium decides who is watched.
        // Timber is deliberately not tagged to a short list - almost everywhere has some,
        // and a good that everybody has is a useful counterweight to four that they do not.
        state.ResourceDeposits[Commodity.NaturalGas] = TaggedPotential(nation.Tag,
            "RUS USA QAT IRN TKM SAU AUS NOR DZA CAN CHN NGA EGY", basePotential * 0.60f, state.MapIndex + 271) * 2.70f;
        state.ResourceDeposits[Commodity.Bauxite] = TaggedPotential(nation.Tag,
            "AUS GIN BRA CHN IND IDN JAM RUS KAZ VNM", basePotential * 0.30f, state.MapIndex + 307);
        state.ResourceDeposits[Commodity.Uranium] = TaggedPotential(nation.Tag,
            "KAZ CAN AUS NAM NER RUS UZB USA ZAF UKR", basePotential * 0.14f, state.MapIndex + 337) * 0.23f;
        state.ResourceDeposits[Commodity.Timber] = (0.35f + StableFraction(state.MapIndex + 367) * 1.45f) * 1.90f;
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
