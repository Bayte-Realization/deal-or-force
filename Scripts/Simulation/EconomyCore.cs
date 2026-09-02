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

public sealed class PopulationGroup
{
    public SocialClass Class;
    public string Nationality = "";
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
        var file = GameDataLoader.FindDataFile("common", "buildings", "buildings.json");
        if (file is null) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
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
        catch { /* Compiled defaults deliberately remain bootable. */ }
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
        var state = campaign.States.FirstOrDefault(candidate => candidate.MapIndex == StateMapIndex);
        if (nation is null || state is null || state.NationMapIndex != nation.MapIndex) return;
        if (campaign.Construction.Count(project => project.NationId == nation.Id) >= 12) return;
        var definition = BuildingCatalog.Get(Building);
        if (nation.Treasury < Math.Min(0.05f, definition.Cost)) return;
        campaign.Construction.Add(new ConstructionProject
        {
            Id = campaign.Construction.Count == 0 ? 1 : campaign.Construction.Max(project => project.Id) + 1,
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

            var free = Enumerable.Range(0, states.Length).Where(index => authored[index] == 0).ToArray();
            var weights = free.Select(index => 0.75 + StableFraction(states[index].MapIndex) * 0.50).ToArray();
            var totalWeight = weights.Sum();
            long assigned = 0;
            for (var slot = 0; slot < free.Length; slot++)
            {
                var index = free[slot];
                var population = slot == free.Length - 1
                    ? remaining - assigned
                    : (long)(remaining * weights[slot] / totalWeight);
                assigned += population;
                SeedState(states[index], nation, Math.Max(0, population));
            }
            for (var index = 0; index < states.Length; index++)
                if (authored[index] > 0)
                    SeedState(states[index], nation, Math.Max(0, (long)(authored[index] * scale)));
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
            nation.WeeklyTaxIncome = weeklyOutput * nation.TaxRate * 0.24f * (0.72f + nation.Stability * 0.22f);
            nation.WeeklyExpenses = weeklyOutput * (0.030f + nation.MilitarySpending * 0.22f +
                                                    nation.WelfareSpending * 0.18f +
                                                    nation.EducationSpending * 0.14f +
                                                    (1f - nation.Stability) * 0.025f);
            nation.WeeklyBalance = nation.WeeklyTaxIncome + nation.WeeklyTariffIncome -
                                   nation.WeeklyExpenses + nation.WeeklyTradeBalance;
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
            nation.Stability = M.Clamp(nation.Stability + (nation.StandardOfLiving - 0.45f) * 0.001f +
                securityStability, 0.10f, 0.98f);
            if (nation.GovernmentFocus == GovernmentFocus.NationalSecurity)
                nation.WarSupport = M.Clamp01(nation.WarSupport + 0.00010f);
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
            if (!string.Equals(group.Nationality, ownerTag, StringComparison.OrdinalIgnoreCase))
                foreign += group.Size;
        }
        return total <= 0 ? 0f : foreign / (float)total;
    }

    /// <summary>Every nationality living in a region, largest first.</summary>
    public static IReadOnlyList<(string Nationality, long Size)> Nationalities(ScenarioState state)
    {
        if (state is null) return Array.Empty<(string, long)>();
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in state.PopulationGroups)
        {
            if (group.Size <= 0) continue;
            var key = string.IsNullOrEmpty(group.Nationality) ? "?" : group.Nationality;
            totals[key] = totals.GetValueOrDefault(key) + group.Size;
        }
        return totals.OrderByDescending(entry => entry.Value)
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
            if (string.Equals(group.Nationality, nation.Tag, StringComparison.OrdinalIgnoreCase)) continue;
            var share = (long)MathF.Round(group.Size * rate);
            if (share <= 0) continue;
            group.Size -= share;
            moved += share;
        }
        if (moved <= 0) return;

        var host = state.PopulationGroups.Find(group =>
            string.Equals(group.Nationality, nation.Tag, StringComparison.OrdinalIgnoreCase));
        if (host is null)
        {
            host = new PopulationGroup
            {
                Class = SocialClass.Workers,
                Nationality = nation.Tag,
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

    internal static void SeedState(ScenarioState state, Nation nation, long population)
    {
        state.Population = population;
        state.Literacy = M.Clamp01(nation.Literacy + (StableFraction(state.MapIndex + 17) - 0.5f) * 0.12f);
        state.StandardOfLiving = M.Clamp01(nation.StandardOfLiving + (StableFraction(state.MapIndex + 31) - 0.5f) * 0.10f);
        state.Radicalism = M.Clamp01((0.52f - state.StandardOfLiving) * 0.40f);
        state.Urbanization = M.Clamp(0.18f + nation.Gdp / Math.Max(1f, nation.Population / 1_000_000f) * 0.002f, 0.08f, 0.88f);
        AddGroup(state, nation.Tag, SocialClass.Farmers, 0.34f);
        AddGroup(state, nation.Tag, SocialClass.Workers, 0.38f);
        AddGroup(state, nation.Tag, SocialClass.MiddleClass, 0.22f);
        var used = state.PopulationGroups.Sum(group => group.Size);
        state.PopulationGroups.Add(new PopulationGroup
        {
            Class = SocialClass.Owners, Nationality = nation.Tag, Size = Math.Max(0, population - used),
            Employment = 0.96f, Wealth = 0.86f, StandardOfLiving = M.Clamp01(state.StandardOfLiving + 0.24f)
        });
        state.Buildings[BuildingKind.Agriculture] = Math.Max(1, (int)(population / 4_000_000));
        state.Buildings[BuildingKind.CivilianIndustry] = Math.Max(0, (int)(population * state.Urbanization / 5_000_000));
        state.Buildings[BuildingKind.Infrastructure] = Math.Max(1, (int)(state.Urbanization * 4));
        SeedResources(state, nation);
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

    private static void AddGroup(ScenarioState state, string nationality, SocialClass socialClass, float share)
    {
        state.PopulationGroups.Add(new PopulationGroup
        {
            Class = socialClass, Nationality = nationality, Size = (long)(state.Population * share),
            Employment = socialClass == SocialClass.Farmers ? 0.82f : 0.90f,
            Wealth = socialClass == SocialClass.MiddleClass ? 0.62f : 0.38f,
            StandardOfLiving = state.StandardOfLiving, Radicalism = state.Radicalism
        });
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
        foreach (var nation in campaign.Nations)
        {
            var project = campaign.Construction.Where(item => item.NationId == nation.Id)
                .OrderBy(item => item.Id).FirstOrDefault();
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
        var state = campaign.States.FirstOrDefault(candidate => candidate.MapIndex == project.StateMapIndex);
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
