using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Which rung of the ladder a household stands on, and nothing else.
///
/// This used to be four names on three different axes: `Workers` and `MiddleClass` said how
/// much you earned, `Farmers` said where you worked, `Owners` said whether you owned
/// anything. Mixing them meant the one question a class is FOR - who in this region is poor
/// - could not be asked, because half the poor were filed under farming. A peasant at 0.16
/// wealth and a labourer at 0.30 stand on the same rung; what differs is the work, and the
/// work is now <see cref="Sector"/>.
///
/// Three rungs and not four because that is what the trades already were. Sorted by wealth
/// they fall into groups with real gaps between them - 0.16 to 0.42, then 0.55 to 0.70, then
/// 0.88 to 0.92 - and those gaps are where the lines belong.
/// </summary>
public enum SocialClass { Lower, Middle, Upper }

/// <summary>
/// What a trade lives off: the other axis, and the one that decides who feeds the country.
///
/// Kept apart from the class on purpose. A landowner and a peasant both live off the land
/// and could not be further apart in standing; a machinist and a clerk stand at much the
/// same height and have nothing else in common. Asking one enum both questions is what made
/// the farming weights treat landowners as townsfolk.
///
/// `Ownership` is the honest answer for people who live off owning rather than working - it
/// is neither a field, a factory floor nor a counter. It is also not called `Capital`, which
/// is already the word this game uses for the city a government sits in.
/// </summary>
public enum Sector { Agriculture, Industry, Services, Ownership }
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
/// <remarks>Saved as a row of values: see <see cref="PopulationGroupConverter"/>.</remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(PopulationGroupConverter))]
public sealed class PopulationGroup
{
    /// <summary>How well off they are. See <see cref="SocialClass"/>.</summary>
    public SocialClass Class;

    /// <summary>What they live off. See <see cref="Sector"/>.</summary>
    public Sector Sector;

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

/// <summary>
/// What a building is, as data.
///
/// <paramref name="NeedsWater"/> is the one that is not a number: a port or a yard is
/// useless away from water, and which buildings those are used to be a line of C# naming
/// two of them. A mod that adds a fishing harbour can now say so, and a mod that decides
/// its rail terminal is a river terminal can say that.
/// </summary>
public readonly record struct BuildingDefinition(
    string Name, float Cost, int Days, float GdpGain, float CapacityGain, string Description,
    bool NeedsWater = false);

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
        [BuildingKind.Shipyard] = new("Shipyard", 2.75f, 290, 0.90f, 0.10f, NeedsWater: true,
            Description:
            "Hull capacity for the navy and the merchant marine that carries its trade."),
        [BuildingKind.Port] = new("Port", 1.70f, 200, 0.55f, 0.18f, NeedsWater: true,
            Description:
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
                Flag(value, "needs_water", out var needsWater);
                Declare(entry.Name, name, cost, days, gdp, capacity, description, needsWater);
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
        float? gdpGain, float? capacityGain, string description, bool? needsWater = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (TryParse(id, out var existing))
            return Override(existing, name, cost, days, gdpGain, capacityGain, description, needsWater);
        if (string.IsNullOrWhiteSpace(name) || cost is not > 0f || days is not > 0) return false;
        var kind = (BuildingKind)_nextId;
        Register(kind, id);
        Definitions[kind] = new BuildingDefinition(name, cost.Value, days.Value,
            gdpGain ?? 0f, capacityGain ?? 0f, description ?? "", needsWater ?? false);
        return true;
    }

    public static bool Override(BuildingKind kind, string name, float? cost, int? days,
        float? gdpGain, float? capacityGain, string description, bool? needsWater = null)
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
            Description = string.IsNullOrWhiteSpace(description) ? current.Description : description,
            NeedsWater = needsWater ?? current.NeedsWater
        };
        return true;
    }

    /// <summary>A yes-or-no property, absent meaning "leave it as it was".</summary>
    private static bool Flag(JsonElement value, string key, out bool? result)
    {
        result = value.TryGetProperty(key, out var element) &&
                 element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean() : null;
        return result is not null;
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
        if (!MechanicFeatures.Allows(campaign, this)) return;
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
            if (project.NationId == nation.Id && ++running >= ConstructionCost.Limit(campaign, nation, campaign.Defines.MaxConstructionProjects)) return;
        }

        // A wharf needs water. The AI never orders one and the player's button now says
        // so before it is pressed, but a command is the boundary of the simulation and an
        // order arriving over the network or out of a save has not been through either.
        if (Waterfront.NeedsWater(Building) &&
            Waterfront.OfRegion(campaign, state) == WaterAccess.None) return;

        var definition = BuildingCatalog.Get(Building);
        var cost = ConstructionCost.Of(campaign, nation, Building);
        if (nation.Treasury < Math.Min(0.05f, cost)) return;
        campaign.Construction.Add(new ConstructionProject
        {
            Id = highest + 1,
            NationId = nation.Id,
            StateMapIndex = state.MapIndex,
            Building = Building,
            // What building costs in this country (ConstructionCost), and what its modifiers make
            // of it: cheaper, and quicker, while a construction drive or a war economy lasts.
            Cost = cost,
            DurationDays = Math.Max(1, (int)MathF.Round(definition.Days / NationalModifiers.Factor(nation, "construction_speed")))
        });
        campaign.MarkStrategicChange();
    }
}


/// <summary>
/// Pull a building down.
///
/// The exact inverse of a project completing, which is the only way it can be right: the
/// same gain is taken back, scaled by the same geography - an inland port that only ever
/// delivered a fraction only ever takes that fraction away again - and a quarter of the
/// original price comes back as scrap.
///
/// Nothing stops a country demolishing its last farm. That is a decision with consequences
/// rather than an illegal move, and a game that refuses it is a game arguing with the
/// player about their own country.
/// </summary>
public sealed class DemolishBuildingCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; }
    public BuildingKind Building { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;

        ScenarioState state = null;
        foreach (var candidate in campaign.States)
            if (candidate.MapIndex == StateMapIndex) { state = candidate; break; }
        if (state is null || state.NationMapIndex != nation.MapIndex) return;

        var standing = state.Buildings.GetValueOrDefault(Building);
        if (standing <= 0) return;

        var definition = BuildingCatalog.Get(Building);
        var yield = Waterfront.Yield(campaign, state.MapIndex, Building);
        if (standing == 1) state.Buildings.Remove(Building);
        else state.Buildings[Building] = standing - 1;

        nation.Gdp = MathF.Max(0.01f, nation.Gdp - definition.GdpGain * yield);
        nation.ConstructionCapacity = MathF.Max(0f, nation.ConstructionCapacity - definition.CapacityGain * yield);
        nation.Treasury += ConstructionCost.Of(campaign, nation, Building) * campaign.Defines.DemolitionRefund;

        if (Building == BuildingKind.University)
        {
            state.Literacy = M.Clamp01(state.Literacy - 0.025f);
            nation.Literacy = M.Clamp01(nation.Literacy - 0.003f);
            nation.Productivity = MathF.Max(0.1f, nation.Productivity - 0.006f);
        }
        if (Building == BuildingKind.Hospital)
            state.StandardOfLiving = M.Clamp01(state.StandardOfLiving - 0.018f);

        campaign.MarkStrategicChange();
    }
}

/// <summary>
/// Call a site off before it is finished.
///
/// What has been spent is spent - the money went into the ground - and what has not is
/// still in the treasury, so cancelling costs exactly the progress made. The twelve-project
/// limit is what makes this worth having: a queue nobody can clear is a queue that has to
/// be got right first time.
/// </summary>
public sealed class CancelConstructionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ProjectId { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        var project = campaign.Construction.Find(candidate =>
            candidate.Id == ProjectId && candidate.NationId == nation.Id);
        if (project is null) return;
        campaign.Construction.Remove(project);
        campaign.MarkStrategicChange();
    }
}

public static class EconomySystem
{
    /// <summary>The living standard an economy of this size per head carries, 0.28 to 0.90.</summary>
    public static float EconomicLiving(Nation nation) =>
        M.Clamp(0.34f + MathF.Log10(Math.Max(0.1f, nation.Gdp * 1_000_000_000f / Math.Max(1, nation.Population))) * 0.07f, 0.28f, 0.90f);

    public static void Seed(Campaign campaign)
    {
        // Every country opens at the rate a settled economy rests at, not at zero and not
        // at whatever the field defaulted to. Reading the weight here is also what loads
        // `common/inflation` at a known moment rather than whenever something first asks -
        // a catalogue that loads lazily loads after the first thing that needed it.
        var restingInflation = InflationCore.Weights.Base;

        // What history/states says each region has: its people, how many of them live in towns,
        // who they are, what is built there and what lies under the ground. The map's own figure
        // is one number a country, and dealing that out almost evenly put as many people in
        // Magadan as in Moscow; the regions are written down in history/states/2024/<TAG>.json.
        var written = HistoryDataLoader.StatedRegions(campaign);

        foreach (var nation in campaign.Nations)
        {
            nation.Inflation = restingInflation;
            // What history/countries wrote stands, and the rest is worked out from the economy.
            // The formula used to run over everything, so the literacy written for 197 countries
            // never reached the game - Russia's 0.99 was 0.74. What it worked out is kept as the
            // country's human capital, which productivity, research and stability read.
            var developed = M.Clamp(0.42f + MathF.Log10(Math.Max(1f, nation.Gdp)) * 0.10f, 0.32f, 0.98f);
            if (!nation.StatedFigures.Contains("literacy")) nation.Literacy = developed;
            nation.LiteracyOffset = developed - nation.Literacy;
            if (!nation.StatedFigures.Contains("standard_of_living"))
                nation.StandardOfLiving = EconomicLiving(nation);
            // What history wrote, against what the economy alone would say: kept, so the
            // weekly pull towards the economy pulls towards this country's own level of it.
            nation.LivingOffset = nation.StandardOfLiving - EconomicLiving(nation);
            nation.Productivity = 0.72f + nation.HumanCapital * 0.50f;
            if (!nation.StatedFigures.Contains("stability"))
                nation.Stability = 0.58f + nation.StandardOfLiving * 0.25f;

            var states = campaign.StatesOf(nation);
            if (states.Length == 0) continue;

            // A region correction that states its own population keeps it, and the rest of
            // the country is spread over what is left. Spreading the whole national figure
            // over every region discarded those numbers without saying so - which is how
            // the Andaman Islands ended up with fifty million people and Nakhchivan with
            // one and a half.
            //
            // A region history/states writes a number for has exactly that number, ahead of any
            // compiled correction: the file is the figure a mod can change.
            var authored = new long[states.Length];
            var fixedAt = new long[states.Length];
            var isFixed = new bool[states.Length];
            long claimed = 0, settled = 0;
            for (var index = 0; index < states.Length; index++)
            {
                if (written.TryGetValue(states[index].MapIndex, out var figures) && figures.Population is { } people)
                {
                    isFixed[index] = true;
                    fixedAt[index] = people;
                    settled += people;
                }
                else if (TerritorialTopology.AuthoredPopulation(states[index].MapIndex, out var stated))
                {
                    authored[index] = stated;
                    claimed += stated;
                }
            }

            // If the authored regions between them claim more than the country has, they
            // are scaled to fit rather than allowed to invent people.
            var remaining = nation.Population - settled - claimed;
            var scale = 1.0;
            if (remaining < 0 && claimed > 0)
            {
                scale = Math.Max(0, nation.Population - settled) / (double)claimed;
                remaining = 0;
            }
            remaining = Math.Max(0, remaining);

            // Nobody lives on a lake. Water regions are owned and drawn like any other, so
            // without this they take a full share of the country's population - which put two
            // million Ukrainians in the Kakhovka reservoir and forty-seven million Chinese in
            // Qinghai Lake, and took those people away from the land they actually live on.
            var free = Enumerable.Range(0, states.Length)
                .Where(index => authored[index] == 0 && !isFixed[index] && !LakeRegions.IsWaterState(states[index]))
                .ToArray();
            // Unless there is nowhere else: a country of nothing but water still has to put
            // its people somewhere, and losing them silently is worse than drowning them.
            if (free.Length == 0)
                free = Enumerable.Range(0, states.Length).Where(index => authored[index] == 0 && !isFixed[index]).ToArray();
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
                if (isFixed[index]) counts[index] = Math.Max(0, fixedAt[index]);
                else if (authored[index] > 0) counts[index] = Math.Max(0, (long)(authored[index] * scale));

            // Where the file wrote regions down, the country is the sum of them: raising one
            // region in a mod raises the country, rather than being taken back from its
            // neighbours or quietly lost. A country nobody wrote a region for keeps its own
            // figure exactly as before.
            if (isFixed.Any(value => value)) nation.Population = counts.Sum();

            // How urban each region is: as history writes it where it does, and otherwise spread
            // around the national figure the way it always was.
            var urban = new float[states.Length];
            for (var index = 0; index < states.Length; index++)
                urban[index] = written.TryGetValue(states[index].MapIndex, out var figures) && figures.Urbanisation is { } share
                    ? share
                    : StateUrbanisation(nation, states[index].MapIndex);

            // Who lives there, as history writes it where it does: a region's own list is its
            // people, and only the rest are worked out from the country.
            var peoples = new IReadOnlyList<(string Culture, float Share)>[states.Length];
            for (var index = 0; index < states.Length; index++)
                peoples[index] = written.TryGetValue(states[index].MapIndex, out var figures) ? figures.Cultures : null;
            var composition = DemographyCatalog.AllocateCultures(nation.Tag, states, counts, peoples);
            var farming = StatedAgricultureScale(nation, states, counts, urban);
            var buildings = SeedBuildings(campaign, nation, states, counts, urban);
            for (var index = 0; index < states.Length; index++)
            {
                var state = states[index];
                SeedState(campaign, state, nation, counts[index], composition[index], farming, buildings[index],
                    urban[index]);
                if (!written.TryGetValue(state.MapIndex, out var figures)) continue;
                state.DealtCultures = figures.Cultures is { Count: > 0 } ? HistoryDataLoader.MixSignature(figures.Cultures) : null;
                // What is built and what lies under the ground, as written: the file is the
                // region, and a building or a deposit it leaves out is not there.
                if (figures.Buildings is not null)
                {
                    state.Buildings.Clear();
                    foreach (var (kind, level) in figures.Buildings)
                        if (level > 0) state.Buildings[kind] = level;
                }
                if (figures.Resources is not null)
                {
                    state.ResourceDeposits.Clear();
                    foreach (var (commodity, amount) in figures.Resources)
                        if (amount > 0f) state.ResourceDeposits[commodity] = amount;
                }
            }
        }
    }

    /// <summary>
    /// How many farms and factories each region opens with, decided for the country first.
    ///
    /// Each region used to work out its own and round down: a farm per four million people
    /// with at least one, and a factory per five million town-dwellers. That made the count
    /// depend on how finely the map happened to cut the country rather than on the country.
    /// Russia is seventy-nine regions of under two million, so it opened with seventy-nine
    /// farms - twice what its people call for - and not one factory; France, Britain, Turkey
    /// and Poland the same. China, in thirty-two regions of forty million, had a hundred and
    /// seventy-three. The total now comes from all of a country's people at once and is handed
    /// to its regions by largest remainder, so it is the same however the map is drawn and a
    /// region still gets the part of it its own people account for.
    ///
    /// Which people: the country's farms go where its countryside is and its factories where
    /// its towns are. The number of farms is still a farm per so many people - that is what
    /// the markets were tuned against - but a city region holds few of them.
    /// </summary>
    internal static (int Farms, int Industry)[] SeedBuildings(Campaign campaign, Nation nation,
        IReadOnlyList<ScenarioState> states, IReadOnlyList<long> counts, IReadOnlyList<float> urban = null)
    {
        var defines = campaign?.Defines ?? new Defines();
        var rural = new double[states.Count];
        var industry = new double[states.Count];
        double everyone = 0;
        for (var index = 0; index < states.Count; index++)
        {
            var people = Math.Max(0, counts[index]);
            everyone += people;
            var share = urban is null ? StateUrbanisation(nation, states[index].MapIndex) : urban[index];
            rural[index] = people * (1.0 - share);
            industry[index] = people * (double)share / MathF.Max(1f, defines.SeedUrbanPeoplePerIndustry);
        }

        // The old floor of one farm, kept for the country rather than for each of its regions:
        // a country with anybody in it farms somewhere, however small it is.
        var farmTotal = (int)Math.Round(everyone / MathF.Max(1f, defines.SeedPeoplePerFarm));
        if (farmTotal == 0 && everyone > 0) farmTotal = 1;
        var countryside = rural.Sum();
        var farms = new double[states.Count];
        for (var index = 0; index < states.Count; index++)
            farms[index] = countryside > 0
                ? farmTotal * rural[index] / countryside
                : everyone > 0 ? farmTotal * Math.Max(0, counts[index]) / everyone : 0;
        var farmShares = LargestRemainder(farms, farmTotal, counts);
        var industryShares = LargestRemainder(industry, (int)Math.Round(industry.Sum()), counts);

        var result = new (int Farms, int Industry)[states.Count];
        for (var index = 0; index < states.Count; index++) result[index] = (farmShares[index], industryShares[index]);
        return result;
    }

    /// <summary>
    /// Whole shares of a whole total, in proportion to exact ones: everybody gets the whole
    /// part of theirs, and what is left goes one at a time to the largest fractions. Ties go to
    /// the more populous region and then to the earlier one, so the answer is the same on every
    /// machine.
    /// </summary>
    internal static int[] LargestRemainder(IReadOnlyList<double> exact, int total, IReadOnlyList<long> people)
    {
        var shares = new int[exact.Count];
        var given = 0;
        for (var index = 0; index < exact.Count; index++)
        {
            shares[index] = (int)Math.Floor(Math.Max(0, exact[index]));
            given += shares[index];
        }
        if (given >= total) return shares;
        var order = Enumerable.Range(0, exact.Count)
            .OrderByDescending(index => Math.Max(0, exact[index]) - shares[index])
            .ThenByDescending(index => people[index])
            .ThenBy(index => index)
            .ToArray();
        for (var step = 0; given < total && order.Length > 0; step++, given++)
            shares[order[step % order.Length]]++;
        return shares;
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
        foreach (var n in campaign.Nations) ProductionCapacity.Initialize(n);
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


    /// <summary>
    /// Fill in the weekly figures and the explanations behind them, without moving money.
    ///
    /// The weekly tick does this as a side effect of running the week, and it does not run
    /// on day zero and does not survive a save - a breakdown keeps its parts in a private
    /// list and the serialiser writes public fields. Between them that is a budget screen
    /// reading zero for the first week of every campaign and for up to six days after every
    /// load, which is what "the economy sometimes zeroes out" turned out to be.
    ///
    /// Everything here is a pure read of the state: BudgetCore.Evaluate and Itemise compute,
    /// StabilityCore.Target reports a target it does not apply. Nothing is added to a
    /// treasury, so calling it twice in a day is not a week run twice.
    ///
    /// Only for countries somebody is looking at. Composing seventeen labels for two hundred
    /// and sixty cabinets nobody reads is the cost the weekly tick already refuses to pay.
    /// </summary>
    public static void Explain(Campaign campaign)
    {
        if (campaign is null) return;
        foreach (var nation in campaign.Nations)
        {
            if (!nation.IsHuman) continue;
            SovereignFinance.Prepare(campaign, nation, false);
            var atWar = false;
            foreach (var war in campaign.Wars)
                if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }

            var weeklyOutput = nation.Gdp / 52f;
            var budget = BudgetCore.Evaluate(nation, weeklyOutput, atWar);
            nation.WeeklyTaxIncome = budget.Taxation;
            nation.WeeklyExpenses = budget.Expenses;
            nation.WeeklyBalance = budget.Balance;
            BudgetCore.Itemise(nation, weeklyOutput, atWar);
            nation.Budget.Settle(nation.WeeklyBalance, Loc.Get("rounding"));

            StabilityCore.Target(campaign, nation, nation.StabilityDrivers);

            var scarcity = MathF.Max(0f, nation.ShortageIndex - campaign.Defines.ScarcityTolerance);
            InflationCore.Target(campaign, nation, weeklyOutput, atWar, scarcity, nation.InflationDrivers);

            var cabinet = GovernmentFocusCatalog.Of(nation.GovernmentFocus);
            var government = PoliticsCore.GovernmentOf(nation);
            var ideology = PoliticsCore.IdeologyOf(nation);
            var focusWarSupport = cabinet.WarSupport + government.WarSupport + ideology.WarSupport;
            nation.WarSupportDrivers.Begin("War support, weekly", "share");
            nation.WarSupportDrivers.Add("Government focus on national security", focusWarSupport);
            nation.WarSupportDrivers.Settle(0f, "held at its limit");
        }
    }

    /// <summary>
    /// The country-card line under the war-support percent. Peace says the figure does not
    /// apply. A war names the largest pieces already on <see cref="Nation.WarSupportDrivers"/>.
    /// Reading it does not refill the breakdown or move the number.
    /// </summary>
    public static string DescribeWarSupport(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return "";
        var atWar = false;
        var wars = campaign.Wars;
        for (var i = 0; i < wars.Count; i++)
        {
            var war = wars[i];
            if (war is not null && !war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
        }
        if (!atWar)
            return Loc.Get("War support does not apply while the country is at peace.");

        var drivers = nation.WarSupportDrivers;
        if (drivers is null || !drivers.HasEntries)
            return Loc.Get("At war. This week's reasons for war support have not been drawn yet.");

        var entries = drivers.Entries;
        var first = -1;
        var second = -1;
        var third = -1;
        var firstMag = -1f;
        var secondMag = -1f;
        var thirdMag = -1f;
        for (var i = 0; i < entries.Count; i++)
        {
            var magnitude = MathF.Abs(entries[i].Value);
            if (magnitude < 0.0000005f) continue;
            if (magnitude > firstMag)
            {
                third = second; thirdMag = secondMag;
                second = first; secondMag = firstMag;
                first = i; firstMag = magnitude;
            }
            else if (magnitude > secondMag)
            {
                third = second; thirdMag = secondMag;
                second = i; secondMag = magnitude;
            }
            else if (magnitude > thirdMag)
            {
                third = i; thirdMag = magnitude;
            }
        }

        var lines = new List<string>(4) { Loc.Get("War support, this week.") };
        void Line(int index)
        {
            if (index < 0) return;
            var entry = entries[index];
            lines.Add(Loc.Get(entry.Source) + " " + Fmt.PercentDelta(entry.Value));
        }
        Line(first);
        Line(second);
        Line(third);
        return string.Join("\n", lines);
    }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>
    /// The week, in stages: the market, then the countries a batch at a time. A smoothed frame
    /// loop (Campaign.StepFor) spreads them over frames; everything else runs them straight
    /// through. Either way they are the same work in the same order - nothing else touches the
    /// campaign between two stages, because orders wait for the next step.
    /// </summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(EconomySystem))) yield break;
        if (campaign.ElapsedDays == 0 || campaign.ElapsedDays % campaign.Defines.EconomyTickDays != 0) yield break;
        foreach (var _ in MarketSystem.TickStages(campaign)) yield return 0;
        for (var index = 0; index < campaign.Nations.Count; index++)
        {
            if (index > 0 && index % EconomyStageCountries == 0) yield return 0;
            using var stage = SimProfiler.Measure("economy");
            var nation = campaign.Nations[index];
            // Empty map entities (e.g. the unclaimed Antarctic mask) are not economies.
            // Their nominal placeholder GDP used to finance imaginary arms output forever.
            // Fallen countries (all ground lost, the fall flow has ruled) are not economies:
            // their books close the day the fall is decided, instead of a stateless remnant
            // financing a phantom army for months.
            if (nation.Movement is null && (!campaign.HasOwnedLand(nation) || campaign.Fallen.Contains(nation.Id)))
            {
                nation.Population = campaign.Fallen.Contains(nation.Id) ? 0 : nation.Population;
                nation.Gdp = nation.GrossOutput = 0;
                nation.WeeklyTaxIncome = nation.WeeklyExpenses = nation.WeeklyBalance = 0;
                continue;
            }
            var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
            SovereignFinance.Prepare(campaign, nation);
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

            var interest = 0f;
            foreach (var line in BudgetCore.All)
                if (line.Charge == BudgetBasis.Debt && line.Side == BudgetSide.Expense)
                    interest += BudgetCore.Amount(nation, line, weeklyOutput, atWar);
            SovereignFinance.Settle(campaign, nation, nation.WeeklyBalance, interest);

            // What the week did to prices, before what prices do to the week. Placed here
            // on purpose: the budget has been worked out and paid, so the deficit inflation
            // reads is the one that actually happened, and the standard of living and the
            // radicalism it moves are read further down by everything that cares.
            //
            // The market's shortfall is forgiven up to a tolerance - a market that is 98 %
            // supplied is a working market, not a shortage - so an ordinary world does not
            // spend its whole campaign being told it has a supply crisis.
            var scarcity = MathF.Max(0f, nation.ShortageIndex - campaign.Defines.ScarcityTolerance);
            InflationCore.Tick(campaign, nation, weeklyOutput, atWar, scarcity);

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
            // And the slow drift of what the country's work is worth: catch-up, the long wave,
            // the drags of war and empire (SecularCycle) - the only thing that lets the order of
            // 2024 change.
            SecularCycle.Week(campaign, nation, atWar);
            if (nation.GrossOutput > 0f && nation.OutputScale > 0f)
            {
                // Multiply in double: finite operands can overflow an intermediate float
                // even when smoothing would conceal the initial source of corruption.
                var target = nation.GrossOutput * 52d * nation.OutputScale;
                var next = nation.Gdp + (target - nation.Gdp) * campaign.Defines.GdpConvergenceRate;
                if (!double.IsFinite(target) || !double.IsFinite(next) || next > float.MaxValue || next < -float.MaxValue)
                    throw new CampaignIntegrityException("country " + nation.Tag + ".Gdp from production");
                nation.Gdp = (float)Math.Max(0.01, next);
            }
            var annualGrowth = conditions;

            var states = campaign.StatesOf(nation);
            long totalPopulation = 0;
            using var stateScope = SimProfiler.Measure("eco:states");
            foreach (var state in states)
            {
                if (campaign.Defines.HouseholdDynamicsEnabled) HouseholdEconomy.Review(campaign, nation, state, annualGrowth);
                else
                {
                    var hospital = state.Buildings.GetValueOrDefault(BuildingKind.Hospital);
                    var weeklyGrowth = (0.0035f + (state.StandardOfLiving - .5f) * .006f + hospital * .0005f) / 52f;
                    AdjustStatePopulation(state, (long)Math.Round(state.Population * weeklyGrowth));
                }
                state.StandardOfLiving = M.Lerp(state.StandardOfLiving, nation.StandardOfLiving, 0.04f);
                var welfareRadicalism = budget.Radicalism;
                var focusRadicalism = cabinet.Radicalism + government.Radicalism + ideology.Radicalism;
                state.Radicalism += RadicalismCore.WeeklyChange(campaign, nation, state,
                    atWar, welfareRadicalism, focusRadicalism);
                MinorityPressure(campaign, nation, state);
                totalPopulation += state.Population;
            }
            if (states.Length > 0) nation.Population = totalPopulation;
            var focusSol = cabinet.Living + government.Living + ideology.Living;
            // The same tolerance growth and prices already forgive: a market 94% supplied is a
            // working market, and charging every point of it to how people live took five
            // hundredths a year off the living standard of every poor country, whose markets run 80% supplied.
            nation.StandardOfLiving = M.Clamp01(nation.StandardOfLiving + annualGrowth * 0.004f -
                MathF.Max(0f, nation.ShortageIndex - campaign.Defines.ScarcityTolerance) * campaign.Defines.ShortageSolWeeklyPenalty +
                budget.Living + focusSol);
            // And back towards what the economy can carry. The terms above were added up for
            // ever and pulled towards nothing, so a century of small leanings ran every
            // country to an end of the scale: Japan, India and Bangladesh at nought, the
            // stability that reads it at a twentieth, and a civil war in every other country.
            // Policy still moves a country off its economy - by about a tenth, not to ruin.
            var anchor = M.Clamp01(EconomicLiving(nation) + nation.LivingOffset);
            nation.StandardOfLiving = M.Clamp01(nation.StandardOfLiving +
                (anchor - nation.StandardOfLiving) * campaign.Defines.LivingReversionPerWeek);
            var focusLiteracy = cabinet.Literacy + government.Literacy + ideology.Literacy;
            var highTechModifier = 1f;
            if (nation.Market.TryGetValue(Commodity.Electronics, out var electQuote) && electQuote.Demand > 0.05f)
                highTechModifier *= (0.88f + 0.12f * Math.Clamp(electQuote.Satisfaction, 0f, 1f));
            if (nation.Market.TryGetValue(Commodity.RareEarths, out var reQuote) && reQuote.Demand > 0.05f)
                highTechModifier *= (0.90f + 0.10f * Math.Clamp(reQuote.Satisfaction, 0f, 1f));
            var productivityTarget = (0.72f + nation.HumanCapital * 0.50f + cabinet.ProductivityTarget) * highTechModifier;
            nation.Productivity = M.Lerp(nation.Productivity, productivityTarget, 0.015f);
            // How a country is governed does not settle it this week; it builds up, slowly
            // and only so far. Government, ideology, cabinet and budget keep the accumulating
            // character they were written with, and accumulate into a bounded term that also
            // gives itself back - so a good cabinet steadies a country rather than making it
            // permanently unfallable, and a bad decade is something a country can come out of.
            var securityStability = cabinet.Stability + government.Stability + ideology.Stability + mismatch;
            var limit = StabilityCore.Weights.GovernanceLimit;
            var governance = nation.StabilityGovernance + securityStability + budget.Stability;
            governance -= governance * M.Clamp01(StabilityCore.Weights.GovernanceDecay);
            nation.StabilityGovernance = M.Clamp(governance, -limit, limit);

            // And then the country is pulled towards what its regions, its capital and its
            // wars actually add up to. A tenth of the gap a week: a shock arrives over a
            // season rather than in an afternoon.
            if (atWar || nation.Stability < 0.42f || nation.DebtCondition >= DebtCondition.Default)
                nation.StabilityBias *= 0.996f;
            else if (nation.StabilityBias > 0.12f)
                nation.StabilityBias = 0.12f + (nation.StabilityBias - 0.12f) * 0.999f;
            else if (nation.StabilityBias < -0.10f)
                nation.StabilityBias = -0.10f + (nation.StabilityBias + 0.10f) * 0.999f;
            var settledStability = StabilityCore.Target(campaign, nation, nation.StabilityDrivers);
            nation.Stability = M.Lerp(nation.Stability, settledStability,
                M.Clamp01(StabilityCore.Weights.Approach));

            var focusWarSupport = cabinet.WarSupport + government.WarSupport + ideology.WarSupport;
            var settledWarSupport = M.Clamp01(nation.WarSupport + focusWarSupport);
            nation.WarSupportDrivers.Begin("War support, weekly", "share");
            nation.WarSupportDrivers.Add("Government focus on national security", focusWarSupport);
            nation.WarSupportDrivers.Settle(settledWarSupport - nation.WarSupport, "held at its limit");
            nation.WarSupport = settledWarSupport;
        }
    }

    /// <summary>How many countries one stage of the weekly pass settles: about four milliseconds.</summary>
    private const int EconomyStageCountries = 24;

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
        // A minority is a people the state is not for. Which peoples those are is data:
        // Belgium has two of its own and Switzerland four, and a rule that assumed one would
        // make foreigners of half of both. Looked up once for the region, not per group.
        var primary = DemographyCatalog.PrimarySet(ownerTag);
        long total = 0;
        long foreign = 0;
        foreach (var group in state.PopulationGroups)
        {
            if (group.Size <= 0) continue;
            total += group.Size;
            if (!IsOwn(primary, group)) foreign += group.Size;
        }
        return total <= 0 ? 0f : foreign / (float)total;
    }

    /// <summary>What <see cref="DemographyCatalog.IsPrimary"/> answers, against a set already looked up.</summary>
    private static bool IsOwn(IReadOnlySet<string> primary, PopulationGroup group) =>
        primary is not null && group.Culture is not null && primary.Contains(group.Culture);

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

    /// <summary>Every sector worked in a region, largest first.</summary>
    public static IReadOnlyList<(string Sector, long Size)> Sectors(ScenarioState state) =>
        Tally(state, group => group.Sector.ToString());

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
    /// <summary>
    /// The weekly tidy of a region's peoples: a group nobody belongs to any more is dropped.
    /// This used to run a second assimilation of its own as well, at about five per cent of a
    /// minority a year into the first group of the state's people it found; DemographicCycle's
    /// assimilation is now the only one.
    /// </summary>
    private static void MinorityPressure(Campaign campaign, Nation nation, ScenarioState state)
    {
        // Compacted by hand, in order: RemoveAll's predicate would allocate a closure per region
        // per week.
        var groups = state.PopulationGroups;
        var kept = 0;
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            if (group.Size <= 0) continue;
            groups[kept++] = group;
        }
        if (kept < groups.Count) groups.RemoveRange(kept, groups.Count - kept);
    }

    internal static void SeedState(Campaign campaign, ScenarioState state, Nation nation, long population) =>
        SeedState(campaign, state, nation, population, null, (1f, 1f), null);

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
    ///
    /// <paramref name="buildings"/> is the region's part of its country's farms and factories
    /// (see SeedBuildings). Without it - one region seeded on its own - the region works out
    /// its own, the old way. <paramref name="urbanisation"/> likewise: how urban history says
    /// the region is, or null for the spread around the national figure.
    /// </summary>
    internal static void SeedState(Campaign campaign, ScenarioState state, Nation nation, long population,
        IReadOnlyList<(string Culture, float Share)> composition, (float Fields, float Town) farming,
        (int Farms, int Industry)? buildings, float? urbanisation = null)
    {
        var defines = campaign?.Defines ?? new Defines();
        state.Population = population;
        state.Literacy = M.Clamp01(nation.Literacy + (StableFraction(state.MapIndex + 17) - 0.5f) * 0.12f);
        state.StandardOfLiving = M.Clamp01(nation.StandardOfLiving + (StableFraction(state.MapIndex + 31) - 0.5f) * 0.10f);
        state.Radicalism = M.Clamp01((0.52f - state.StandardOfLiving) * 0.40f);
        state.Urbanization = urbanisation ?? StateUrbanisation(nation, state.MapIndex);
        state.PopulationGroups.Clear();
        BuildGroups(state, nation, population, composition, farming);
        state.Buildings[BuildingKind.Agriculture] = buildings?.Farms ??
            Math.Max(1, (int)(population / MathF.Max(1f, defines.SeedPeoplePerFarm)));
        state.Buildings[BuildingKind.CivilianIndustry] = buildings?.Industry ?? Math.Max(0, (int)(population *
            state.Urbanization / MathF.Max(1f, defines.SeedUrbanPeoplePerIndustry)));
        state.Buildings[BuildingKind.Infrastructure] =
            Math.Max(1, (int)(state.Urbanization * defines.SeedInfrastructurePerUrbanisation));
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
            // occupations. The economy only needs the ladder, so seed the three rungs
            // directly - and with nobody working at anything there is no sector to report,
            // so they all carry the residual one.
            foreach (var (culture, share) in composition)
                foreach (var rung in Enum.GetValues<SocialClass>())
                    Add(state, culture, "", rung.ToString(), rung, Sector.Services,
                        (long)(population * share * rung switch
                        {
                            SocialClass.Upper => 0.06f,
                            SocialClass.Middle => 0.22f,
                            _ => 0.72f
                        }),
                        rung == SocialClass.Lower ? 0.86f : 0.90f,
                        rung switch
                        {
                            SocialClass.Upper => 0.86f,
                            SocialClass.Middle => 0.62f,
                            _ => 0.38f
                        });
            return;
        }

        var weights = trades.Select(trade => Math.Max(0f, trade.ShareAt(state.Urbanization))).ToArray();
        var weightTotal = weights.Sum();
        if (weightTotal <= 0f) return;
        if (farming.Fields != 1f || farming.Town != 1f)
        {
            for (var index = 0; index < trades.Count; index++)
                weights[index] *= trades[index].Sector == Sector.Agriculture
                    ? farming.Fields
                    : farming.Town;
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
                    var group = Add(state, culture, religion, trade.Id, trade.Class,
                        trade.Sector, size, trade.Employment, trade.Wealth);
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
                trades[0].Id, trades[0].Class, trades[0].Sector, 0,
                trades[0].Employment, trades[0].Wealth);
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
    ///
    /// And solved rather than divided. Each region is normalised on its own after the fields
    /// are scaled, so a region's share of farmers is k·f / (k·f + 1 − f) for its own f, not k·f:
    /// dividing the stated figure by the believed one is right only while every region sits
    /// near the same f. It did, while regions were spread a few points around the national
    /// urbanisation; once they carry their own - Moscow nearly all town, the steppe behind it
    /// nearly none - Guyana came out seven points more agricultural than it says it is. The
    /// ratio that actually delivers the stated figure is found by bisection instead.
    /// </summary>
    private static (float Fields, float Town) StatedAgricultureScale(Nation nation,
        IReadOnlyList<ScenarioState> states, IReadOnlyList<long> counts, IReadOnlyList<float> urbanisation)
    {
        var trades = DemographyCatalog.Professions;
        if (nation.AgricultureShare <= 0f || trades.Count == 0) return (1f, 1f);

        var people = new List<double>();
        var fieldShare = new List<double>();
        double everything = 0;
        for (var index = 0; index < states.Count; index++)
        {
            var here = Math.Max(0, counts[index]);
            if (here <= 0) continue;
            double all = 0, fields = 0;
            foreach (var trade in trades)
            {
                var share = Math.Max(0f, trade.ShareAt(urbanisation[index]));
                all += share;
                if (trade.Sector == Sector.Agriculture) fields += share;
            }
            if (all <= 0) continue;
            people.Add(here);
            fieldShare.Add(fields / all);
            everything += here;
        }
        if (everything <= 0 || fieldShare.All(share => share <= 0)) return (1f, 1f);

        var stated = M.Clamp(nation.AgricultureShare, 0.001f, 0.95f);
        double Farming(double ratio)
        {
            double farmers = 0;
            for (var index = 0; index < people.Count; index++)
            {
                var f = fieldShare[index];
                var denominator = ratio * f + (1 - f);
                if (denominator > 0) farmers += people[index] * ratio * f / denominator;
            }
            return farmers / everything;
        }

        // Farming rises with the ratio, from nobody at zero towards everybody who can farm at
        // all, so the answer is bracketed and bisection on its logarithm finds it.
        double low = -12, high = 12;
        for (var step = 0; step < 60; step++)
        {
            var middle = (low + high) / 2;
            if (Farming(Math.Exp(middle)) < stated) low = middle; else high = middle;
        }
        // The fields scaled and the towns left alone: only the ratio between them survives each
        // region's normalisation, so no trade is invented and none is quietly lost.
        return ((float)Math.Exp((low + high) / 2), 1f);
    }

    private static PopulationGroup Add(ScenarioState state, string culture, string religion,
        string profession, SocialClass socialClass, Sector sector, long size,
        float employment, float wealth)
    {
        var group = new PopulationGroup
        {
            Class = socialClass,
            Sector = sector,
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

    /// <summary>Bring a region's groups back to its population, in proportion, if they have drifted from it.</summary>
    internal static void MatchGroups(ScenarioState state)
    {
        if (state is null || state.PopulationGroups.Count == 0) return;
        long total = 0;
        foreach (var group in state.PopulationGroups) total += group.Size;
        if (total != state.Population) GrowGroups(state, 1);
    }

    private static void GrowGroups(ScenarioState state, long delta)
    {
        if (state.PopulationGroups.Count == 0 || delta == 0) return;
        var total = state.PopulationGroups.Sum(g => g.Size);
        long cumulative = 0, assigned = 0;
        for (var index = 0; index < state.PopulationGroups.Count; index++)
        {
            cumulative += state.PopulationGroups[index].Size;
            var through = index == state.PopulationGroups.Count - 1 ? state.Population :
                (long)Math.Round(state.Population * (cumulative / (double)Math.Max(1, total)));
            state.PopulationGroups[index].Size = Math.Max(0, through - assigned);
            assigned = through;
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
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(ConstructionSystem))) return;
        // A site works a day at a time, and this runs a step at a time.
        //
        // `ProgressDays++` and a day's payment, once per step, is correct exactly while a
        // step is a day. At the shipped hourly step it was twenty-four days of building and
        // twenty-four days of money every day: every country in the world finished
        // everything it had queued in a twenty-fourth of the time, and the world came out
        // the far end making five and a half times the industrial materials it wanted.
        //
        // Gated rather than scaled because the field is called ProgressDays and is counted
        // in them. A fractional day of progress would be a different model, and a truer one;
        // it is not the one this is.
        if (!campaign.StepOpensNewDay) return;

        // The oldest sites a country has open work today - as many as the size of its economy
        // runs (ConstructionCost.WorkingSites) - while the treasury pays for them. Only the oldest
        // single project used to move: a country with forty-eight sites finished one at a time, so
        // India and China completed what Malta did, a building or two a year, and their real
        // output stood still for a decade. ConstructionSitesInParallel false is the old queue.
        Working.Clear();
        foreach (var item in campaign.Construction)
        {
            if (!Working.TryGetValue(item.NationId, out var list)) Working[item.NationId] = list = new List<ConstructionProject>();
            list.Add(item);
        }
        foreach (var nation in campaign.Nations)
        {
            if (!Working.TryGetValue(nation.Id, out var sites) || sites.Count == 0) continue;
            sites.Sort((a, b) => a.Id.CompareTo(b.Id));
            var working = ConstructionCost.WorkingSites(campaign, nation);
            foreach (var project in sites)
            {
                if (!float.IsFinite(project.Cost) || project.Cost < 0 ||
                    !float.IsFinite(project.Paid) || project.Paid < 0 ||
                    !float.IsFinite(nation.Treasury) || !float.IsFinite(nation.ConstructionCapacity)) break;
                var required = project.Cost / Math.Max(1, project.DurationDays);
                var outstanding = Math.Max(0f, project.Cost - project.Paid);
                var paid = Math.Min(outstanding,
                    Math.Min(required * Math.Max(0.25f, nation.ConstructionCapacity), Math.Max(0f, nation.Treasury)));
                // Capacity can fund a site before its work time is complete. Continue that
                // work, but never charge beyond the contract or require fresh cash for a
                // fully funded site. Old overpayments are not refunded or rewritten here.
                // A site the treasury cannot pay today waits, and so does every newer one.
                if (outstanding > 0f && paid <= 0f) break;
                nation.Treasury -= paid;
                project.Paid += paid;
                project.ProgressDays++;
                if (project.Progress >= 0.999f)
                {
                    Complete(campaign, nation, project);
                    campaign.Construction.Remove(project);
                }
                if (--working <= 0) break;
            }
        }
    }

    private static readonly Dictionary<int, List<ConstructionProject>> Working = new();

    private static void Complete(Campaign campaign, Nation nation, ConstructionProject project)
    {
        ScenarioState state = null;
        foreach (var candidate in campaign.States)
            if (candidate.MapIndex == project.StateMapIndex) { state = candidate; break; }
        if (state is null) return;
        var definition = BuildingCatalog.Get(project.Building);
        // An inland yard cost what a coastal one cost and is worth a fraction of it. That
        // is the whole of the compromise: the building is allowed, the geography is priced.
        var yield = Waterfront.Yield(campaign, state.MapIndex, project.Building);
        state.Buildings[project.Building] = state.Buildings.GetValueOrDefault(project.Building) + 1;
        campaign.Chronicle.Record(campaign, "construction_completed", nation.Id, subject: definition.Name, place: state.Name, isPublic: false);
        nation.Gdp += definition.GdpGain * yield;
        nation.ConstructionCapacity += definition.CapacityGain * yield;
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

public sealed partial class Defines
{
    /// <summary>The weekly share of the gap between a country's living standard and what its
    /// economy carries that closes (EconomySystem.EconomicLiving): about a year and a third to halve it.</summary>
    public float LivingReversionPerWeek { get; set; } = 0.01f;
}

public sealed partial class Nation
{
    /// <summary>The living standard history wrote, less what the economy alone would give.</summary>
    public float LivingOffset;
}
