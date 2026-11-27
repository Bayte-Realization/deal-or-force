using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
#if NET9_0_OR_GREATER
using System.Numerics.Tensors;
#endif
using System.Runtime.InteropServices;
using System.Text.Json;
using ZeroAlloc.Collections;

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
    /// <summary>
    /// Days of work done, not days elapsed: a site works ConstructionCost.Pace days each calendar
    /// day, so a country with spare construction capacity finishes before DurationDays have passed
    /// and one short of builders after. A whole number in every save written before
    /// 1 November 2026, which reads back as the same number of days.
    /// </summary>
    public float ProgressDays;
    /// <summary>What the site did on the last construction day, and why not (ConstructionQueue codes; "" before its first).</summary>
    public string Waiting = "";
    /// <summary>The site a waiting one is queued behind, or -1.</summary>
    public int WaitingBehind = -1;
    /// <summary>The day the treasury first failed to pay it, while it still cannot be paid; -1 otherwise.</summary>
    public int StalledSinceDay = -1;
    public float Progress => M.Clamp01(Math.Min(
        DurationDays <= 0 ? 1f : ProgressDays / DurationDays,
        Cost <= 0f ? 1f : Paid / Cost));
}

/// <summary>
/// What a building is, as data.
///
/// <paramref name="NeedsWater"/> is the one that is not a number: a port or a yard is
/// useless away from water, and which buildings those are used to be a line of C# naming
/// two of them. A mod that adds a fishing harbour can now say so, and a mod that decides
/// its rail terminal is a river terminal can say that.
///
/// <paramref name="Jobs"/> is the labour one of them asks for, in millions of workers - the
/// number MarketSystem.JobsDemanded sums to decide how much of a country's plant it can
/// staff. It was a line of C# per building in the market pass, which meant the build list
/// could not say how many people a refinery employs without keeping a second copy of the
/// table, and a mod's new works employed nobody at all.
///
/// <paramref name="CapacityGain"/> is what one finished building adds to the country's
/// construction capacity, which is the speed its sites work at (ConstructionCost.Pace), with
/// diminishing returns above one. There is no GDP field: there was a `gdp_gain`, a step GDP took
/// when the building was finished, and the weekly pull of GDP towards the market value of what
/// the country sells took it back out within two months. A building's GDP is its output.
/// </summary>
public readonly record struct BuildingDefinition(
    string Name, float Cost, int Days, float CapacityGain, string Description,
    bool NeedsWater = false, float Jobs = 0f);

public static class BuildingCatalog
{
    private static readonly ImmutableArray<(BuildingKind Key, BuildingDefinition Value)> Defaults = ImmutableArray.Create(
        (BuildingKind.Agriculture, new BuildingDefinition("Agricultural complex", 0.45f, 120, 0.05f,
            "Farms. Grow food and some fertiliser (and rubber where it grows) and employ country people. Without fertiliser part of the harvest is lost.",
            Jobs: 0.42f)),
        (BuildingKind.CivilianIndustry, new BuildingDefinition("Civilian industry", 2.40f, 240, 0.25f,
            "General factories. Make a little of almost every manufactured good out of coal, ore, energy and rare earths. The largest employer.",
            Jobs: 0.85f)),
        (BuildingKind.Infrastructure, new BuildingDefinition("Infrastructure", 1.35f, 180, 0.45f,
            "Roads, power lines and depots. More army supply and faster marching here, better mining, some energy and materials, wider trade reach.",
            Jobs: 0.30f)),
        (BuildingKind.University, new BuildingDefinition("University", 1.80f, 300, 0.05f,
            "Raises literacy here and productivity nationwide, speeds research and keeps the educated from emigrating. Makes no goods.")),
        (BuildingKind.Hospital, new BuildingDefinition("Hospital", 1.55f, 260, 0.05f,
            "Lowers the death rate here while there is medicine to give, and lifts local living standards for a while. Uses pharmaceuticals.")),
        (BuildingKind.Barracks, new BuildingDefinition("Barracks", 1.10f, 150, 0.05f,
            "Army supply and reinforcements, larger recruitment orders, room for support formations, and a few arms from local workshops.")),
        (BuildingKind.ExtractionIndustry, new BuildingDefinition("Extraction industry", 1.65f, 210, 0.12f,
            "Mines and wells. Dig up whatever lies under this region: the richer the deposits, the more each one yields. Uses machinery and iron ore.",
            Jobs: 0.55f)),
        (BuildingKind.PowerPlant, new BuildingDefinition("Power plant", 2.05f, 230, 0.16f,
            "Makes energy, which households and every factory need. Burns coal, natural gas and a little uranium.",
            Jobs: 0.22f)),
        (BuildingKind.ArmsIndustry, new BuildingDefinition("Arms industry", 2.85f, 280, 0.10f,
            "Makes arms, ammunition, tanks, drones, aircraft, missiles and artillery. Needs steel, chemicals, electronics and components.",
            Jobs: 0.60f)),
        (BuildingKind.Refinery, new BuildingDefinition("Refinery", 2.20f, 250, 0.08f,
            "Turns crude oil, with some natural gas, into energy, chemicals and sulphur. Slows when crude runs short.",
            Jobs: 0.35f)),
        (BuildingKind.ChemicalPlant, new BuildingDefinition("Chemical plant", 2.55f, 265, 0.09f,
            "Makes chemicals, fertiliser and some medicine out of natural gas, sulphur, phosphates and energy. Slows when energy runs short.",
            Jobs: 0.45f)),
        (BuildingKind.Smelter, new BuildingDefinition("Smelter", 2.10f, 235, 0.11f,
            "Makes steel, aluminium and industrial materials out of bauxite, iron ore, coal, nickel and a great deal of energy. Slows when energy runs short.",
            Jobs: 0.50f)),
        (BuildingKind.ElectronicsPlant, new BuildingDefinition("Electronics plant", 3.15f, 300, 0.08f,
            "Makes electronics, chips, batteries and precision components out of silica, metals, chemicals, aluminium, rare earths and energy. Slows when rare earths or energy run short.",
            Jobs: 0.60f)),
        (BuildingKind.Shipyard, new BuildingDefinition("Shipyard", 2.75f, 290, 0.10f, NeedsWater: true,
            Description:
            "Slipways for warships and merchant hulls, and some machinery. Uses steel, aluminium and components.",
            Jobs: 0.55f)),
        (BuildingKind.Port, new BuildingDefinition("Port", 1.70f, 200, 0.18f, NeedsWater: true,
            Description:
            "Widens how far across the sea this country trades, and adds a few slipways and depot space. Makes no goods.",
            Jobs: 0.20f)),
        (BuildingKind.RailNetwork, new BuildingDefinition("Rail network", 1.95f, 215, 0.30f,
            "Widens how far overland this country trades and carries army supply to the hubs here, a little over half what infrastructure does.",
            Jobs: 0.25f)),
        (BuildingKind.ResearchLab, new BuildingDefinition("Research laboratory", 2.30f, 320, 0.06f,
            "Speeds research, makes medicine and keeps scientists from emigrating. Uses electronics, chips and components.",
            Jobs: 0.15f)));
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

    /// <summary>
    /// What a finished university adds at once: literacy in its own region, a little across
    /// the country, and the country's productivity. One-offs - taken back when it is pulled
    /// down - and read by the build list, so the tooltip and the completion cannot disagree.
    /// </summary>
    public const float UniversityRegionalLiteracy = 0.025f;
    public const float UniversityNationalLiteracy = 0.003f;
    public const float UniversityProductivity = 0.006f;

    /// <summary>
    /// What a finished hospital adds at once to its region's standard of living. A one-off,
    /// and a short one: a region's living standard drifts back towards the country's by
    /// EconomySystem's weekly pull, so the lasting part of a hospital is the lives it saves
    /// (HouseholdEconomy.HospitalCover), not this.
    /// </summary>
    public const float HospitalLiving = 0.018f;

    private static readonly BuildingDefinition Unknown =
        new("Unknown building", 1f, 180, 0f, "Declared by content that is no longer loaded.");

    private static volatile (BuildingDefinition Def, bool Present)[] _slots = Array.Empty<(BuildingDefinition, bool)>();
    internal static volatile int Version;
    internal static bool HasExtraJobBuildings { get; private set; }

    private static void RebuildIndex()
    {
        var maxIdx = -1;
        var hasExtra = false;
        foreach (var kv in Definitions)
        {
            var idx = (int)kv.Key;
            if (idx > maxIdx) maxIdx = idx;
            if (idx > (int)BuildingKind.ResearchLab && kv.Value.Jobs > 0f) hasExtra = true;
        }
        var slots = maxIdx >= 0 ? new (BuildingDefinition Def, bool Present)[maxIdx + 1] : Array.Empty<(BuildingDefinition, bool)>();
        foreach (var kv in Definitions)
            if ((uint)kv.Key < (uint)slots.Length) slots[(int)kv.Key] = (kv.Value, true);
        _slots = slots;
        HasExtraJobBuildings = hasExtra;
        unchecked { Version++; }
    }

    public static BuildingDefinition Get(BuildingKind kind)
    {
        var slots = _slots;
        var idx = (int)kind;
        if ((uint)idx < (uint)slots.Length && slots[idx].Present) return slots[idx].Def;
        return Definitions.TryGetValue(kind, out var definition) ? definition : Unknown;
    }

    public static bool Contains(BuildingKind kind)
    {
        var slots = _slots;
        var idx = (int)kind;
        if ((uint)idx < (uint)slots.Length) return slots[idx].Present;
        return Definitions.ContainsKey(kind);
    }

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
                // `gdp_gain` is no longer read: a building's GDP is the market value of what it
                // sells, week by week (ConstructionSystem.Complete). A file that still names it loads.
                Number(value, "capacity_gain", out var capacity);
                Flag(value, "needs_water", out var needsWater);
                Number(value, "jobs", out var jobs);
                Declare(entry.Name, name, cost, days, capacity, description, needsWater, jobs);
            }
        RebuildIndex();
        BuildingRules.ResetCatalog();
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
        float? capacityGain, string description, bool? needsWater = null, float? jobs = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (TryParse(id, out var existing))
            return Override(existing, name, cost, days, capacityGain, description, needsWater, jobs);
        if (string.IsNullOrWhiteSpace(name) || cost is not > 0f || days is not > 0) return false;
        var kind = (BuildingKind)_nextId;
        Register(kind, id);
        Definitions[kind] = new BuildingDefinition(name, cost.Value, days.Value,
            capacityGain ?? 0f, description ?? "", needsWater ?? false,
            jobs is >= 0f and < float.PositiveInfinity ? jobs.Value : 0f);
        RebuildIndex();
        return true;
    }

    /// <summary>Remove a registered building kind by id so mods can disable entries cleanly via "remove": true.</summary>
    public static bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (TryParse(id, out var kind))
        {
            Definitions.Remove(kind);
            Ids.Remove(kind);
            var keysToRemove = new List<string>();
            foreach (var kv in Named)
                if (kv.Value == kind) keysToRemove.Add(kv.Key);
            for (var i = 0; i < keysToRemove.Count; i++)
                Named.Remove(keysToRemove[i]);
            RebuildIndex();
        }
        return true;
    }

    public static bool Override(BuildingKind kind, string name, float? cost, int? days,
        float? capacityGain, string description, bool? needsWater = null, float? jobs = null)
    {
        if (!Definitions.TryGetValue(kind, out var current) || cost is <= 0f || days is <= 0 ||
            capacityGain is < 0f || jobs is < 0f || (jobs is { } j && !float.IsFinite(j))) return false;
        Definitions[kind] = current with
        {
            Name = string.IsNullOrWhiteSpace(name) ? current.Name : name,
            Cost = cost ?? current.Cost,
            Days = days ?? current.Days,
            CapacityGain = capacityGain ?? current.CapacityGain,
            Description = string.IsNullOrWhiteSpace(description) ? current.Description : description,
            NeedsWater = needsWater ?? current.NeedsWater,
            Jobs = jobs ?? current.Jobs
        };
        RebuildIndex();
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
        // Its own regions, and those it administers although another owns them (HeldRegions):
        // held ground is built on as one's own.
        if (ConstructionAdmission.Refusal(campaign, nation, state, Building).Length > 0) return;

        var highest = 0;
        foreach (var project in campaign.Construction)
        {
            if (project.Id > highest) highest = project.Id;
        }

        var cost = ConstructionCost.Of(campaign, nation, Building);
        campaign.Construction.Add(new ConstructionProject
        {
            Id = highest + 1,
            NationId = nation.Id,
            StateMapIndex = state.MapIndex,
            Building = Building,
            // What building costs in this country (ConstructionCost), and what its modifiers make
            // of it: cheaper, and quicker, while a construction drive or a war economy lasts.
            Cost = cost,
            DurationDays = ConstructionCost.WorkDays(campaign, nation, Building)
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

        // GDP falls on its own as the building's output leaves the market; nothing to take back.
        nation.ConstructionCapacity = MathF.Max(0f, nation.ConstructionCapacity - definition.CapacityGain * yield);
        { var ledgerBefore = nation.Treasury; nation.Treasury += ConstructionCost.Of(campaign, nation, Building) * campaign.Defines.DemolitionRefund; MoneyLedger.Book(nation, MoneyFlow.Construction, nation.Treasury - ledgerBefore); }

        if (Building == BuildingKind.University)
        {
            state.Literacy = M.Clamp01(state.Literacy - BuildingCatalog.UniversityRegionalLiteracy);
            nation.Literacy = M.Clamp01(nation.Literacy - BuildingCatalog.UniversityNationalLiteracy);
            nation.Productivity = MathF.Max(0.1f, nation.Productivity - BuildingCatalog.UniversityProductivity);
        }
        if (Building == BuildingKind.Hospital)
            state.StandardOfLiving = M.Clamp01(state.StandardOfLiving - BuildingCatalog.HospitalLiving);

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

/// <summary>
/// The economy as a weekly pass: what each region makes, what the market clears it for,
/// what the government collects and spends, and what all of that does to the people.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// FOR MODDERS — the five things you probably came here to change:
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// 1. WHAT A REGION MAKES → `game/common/terrain/*.json` (what each terrain produces)
///    and `game/common/production/*.json` (what each building adds). A mod that wants
///    deserts to be rich in lithium adds a `"resource": "lithium"` there.
///
/// 2. WHAT THE GOVERNMENT CAN DO → `game/common/budget/00_budget.json`: every line
///    of the national budget, what it is charged against, what a slider may do,
///    and what moving it does to the country. A mod that wants a wealth tax adds
///    a line with `"player": true`.
///
/// 3. HOW MUCH THINGS COST → `game/common/defines/*.json`: `BaselineMilitarySpending`,
///    `ResearchBasePerWeek`, `SupplyPerInfrastructure`, and a hundred other tunables.
///    These are code defaults a mod may override; the game's own numbers are in the
///    partial classes in this file and its neighbours.
///
/// 4. WHO TRADED WHAT WITH WHOM → `game/common/trade/*.json` (what each country
///    wants to buy and sell). The clearing itself is MarketSystem (MarketCore.cs):
///    countries first satisfy demand from local production, then trade the rest.
///
/// 5. HOW THE PEOPLE LIVE → the two axes below: `SocialClass` (Lower / Middle /
///    Upper, by wealth) and `Sector` (what a trade lives off). These drive everything
///    from migration to revolution. A mod that wants a fourth class — say, a
///    precariat below Lower — adds it here and in `common/professions/*.json`.
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class EconomySystem
{
    /// <summary>The living standard an economy of this size per head carries, 0.28 to 0.90.</summary>
    public static float EconomicLiving(Nation nation) =>
        M.Clamp(0.34f + MathF.Log10(Math.Max(0.1f, nation.Gdp * 1_000_000_000f / Math.Max(1, nation.Population))) * 0.07f, 0.28f, 0.90f);

    public static void Seed(Campaign campaign)
    {
        var restingInflation = InflationCore.Weights.Base;
        var written = HistoryDataLoader.StatedRegions(campaign);

        foreach (var nation in campaign.Nations)
        {
            SeedNationBaseline(nation, restingInflation);
            var states = campaign.StatesOf(nation);
            if (states.Length == 0) continue;

            var counts = ComputeNationStatePopulations(nation, states, written, out var anyFixed);
            if (anyFixed) nation.Population = SumLong(counts);

            var urban = new float[states.Length];
            var peoples = new IReadOnlyList<(string Culture, float Share)>[states.Length];
            for (var index = 0; index < states.Length; index++)
            {
                var mapIdx = states[index].MapIndex;
                urban[index] = written.TryGetValue(mapIdx, out var figures) && figures.Urbanisation is { } share
                    ? share
                    : StateUrbanisation(nation, mapIdx);
                peoples[index] = written.TryGetValue(mapIdx, out figures) ? figures.Cultures : null;
            }

            var composition = DemographyCatalog.AllocateCultures(nation.Tag, states, counts, peoples);
            var farming = StatedAgricultureScale(nation, states, counts, urban);
            var buildings = SeedBuildings(campaign, nation, states, counts, urban);
            for (var index = 0; index < states.Length; index++)
            {
                var state = states[index];
                SeedState(campaign, state, nation, counts[index], composition[index], farming, buildings[index], urban[index]);
                if (written.TryGetValue(state.MapIndex, out var figures))
                    ApplyStatedRegionFigures(state, figures);
            }
        }
    }

    private static void SeedNationBaseline(Nation nation, float restingInflation)
    {
        nation.Inflation = restingInflation;
        var developed = M.Clamp(0.42f + MathF.Log10(Math.Max(1f, nation.Gdp)) * 0.10f, 0.32f, 0.98f);
        if (!nation.StatedFigures.Contains("literacy")) nation.Literacy = developed;
        nation.LiteracyOffset = developed - nation.Literacy;
        if (!nation.StatedFigures.Contains("standard_of_living"))
            nation.StandardOfLiving = EconomicLiving(nation);
        nation.LivingOffset = nation.StandardOfLiving - EconomicLiving(nation);
        nation.Productivity = 0.72f + nation.HumanCapital * 0.50f;
        if (!nation.StatedFigures.Contains("stability"))
            nation.Stability = 0.58f + nation.StandardOfLiving * 0.25f;
    }

    private static long[] ComputeNationStatePopulations(
        Nation nation,
        ScenarioState[] states,
        IReadOnlyDictionary<int, HistoryDataLoader.RegionFigures> written,
        out bool anyFixed)
    {
        var stateCount = states.Length;
        var authored = ArrayPool<long>.Shared.Rent(stateCount);
        var fixedAt = ArrayPool<long>.Shared.Rent(stateCount);
        var isFixed = ArrayPool<bool>.Shared.Rent(stateCount);
        var free = ArrayPool<int>.Shared.Rent(stateCount);
        var weights = ArrayPool<double>.Shared.Rent(stateCount);
        Array.Clear(authored, 0, stateCount);
        Array.Clear(fixedAt, 0, stateCount);
        Array.Clear(isFixed, 0, stateCount);

        try
        {
            long claimed = 0, settled = 0;
            anyFixed = false;
            for (var index = 0; index < stateCount; index++)
            {
                if (written.TryGetValue(states[index].MapIndex, out var figures) && figures.Population is { } people)
                {
                    isFixed[index] = true;
                    anyFixed = true;
                    fixedAt[index] = people;
                    settled += people;
                }
                else if (TerritorialTopology.AuthoredPopulation(states[index].MapIndex, out var stated))
                {
                    authored[index] = stated;
                    claimed += stated;
                }
            }

            var remaining = nation.Population - settled - claimed;
            var scale = 1.0;
            if (remaining < 0 && claimed > 0)
            {
                scale = Math.Max(0, nation.Population - settled) / (double)claimed;
                remaining = 0;
            }
            remaining = Math.Max(0, remaining);

            var freeCount = CollectFreePopulationIndices(states, authored, isFixed, free);
            double totalWeight = 0;
            for (var slot = 0; slot < freeCount; slot++)
            {
                var weight = 0.75 + StableFraction(states[free[slot]].MapIndex) * 0.50;
                weights[slot] = weight;
                totalWeight += weight;
            }

            var counts = new long[stateCount];
            long assigned = 0;
            for (var slot = 0; slot < freeCount; slot++)
            {
                var index = free[slot];
                counts[index] = Math.Max(0, slot == freeCount - 1
                    ? remaining - assigned
                    : (long)(remaining * weights[slot] / totalWeight));
                assigned += counts[index];
            }
            for (var index = 0; index < stateCount; index++)
                if (isFixed[index]) counts[index] = Math.Max(0, fixedAt[index]);
                else if (authored[index] > 0) counts[index] = Math.Max(0, (long)(authored[index] * scale));
            return counts;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(authored);
            ArrayPool<long>.Shared.Return(fixedAt);
            ArrayPool<bool>.Shared.Return(isFixed);
            ArrayPool<int>.Shared.Return(free);
            ArrayPool<double>.Shared.Return(weights);
        }
    }

    private static int CollectFreePopulationIndices(ScenarioState[] states, long[] authored, bool[] isFixed, int[] free)
    {
        var freeCount = 0;
        for (var index = 0; index < states.Length; index++)
            if (authored[index] == 0 && !isFixed[index] && !LakeRegions.IsWaterState(states[index]))
                free[freeCount++] = index;
        if (freeCount > 0) return freeCount;
        for (var index = 0; index < states.Length; index++)
            if (authored[index] == 0 && !isFixed[index])
                free[freeCount++] = index;
        return freeCount;
    }

    private static void ApplyStatedRegionFigures(ScenarioState state, HistoryDataLoader.RegionFigures figures)
    {
        state.DealtCultures = figures.Cultures is { Count: > 0 } ? HistoryDataLoader.MixSignature(figures.Cultures) : null;
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
        var count = states.Count;
        var result = new (int Farms, int Industry)[count];
        if (count == 0) return result;

        var defines = campaign?.Defines ?? new Defines();
        var rural = ArrayPool<double>.Shared.Rent(count);
        var industry = ArrayPool<double>.Shared.Rent(count);
        var farms = ArrayPool<double>.Shared.Rent(count);
        var farmShares = ArrayPool<int>.Shared.Rent(count);
        var industryShares = ArrayPool<int>.Shared.Rent(count);
        try
        {
            double everyone = 0;
            double countryside = 0;
            double industrySum = 0;
            for (var index = 0; index < count; index++)
            {
                var people = Math.Max(0, counts[index]);
                everyone += people;
                var share = urban is null ? StateUrbanisation(nation, states[index].MapIndex) : urban[index];
                var ruralVal = people * (1.0 - share);
                var industryVal = people * (double)share / MathF.Max(1f, defines.SeedUrbanPeoplePerIndustry);
                rural[index] = ruralVal;
                industry[index] = industryVal;
                countryside += ruralVal;
                industrySum += industryVal;
            }

            // The old floor of one farm, kept for the country rather than for each of its regions:
            // a country with anybody in it farms somewhere, however small it is.
            var farmTotal = (int)Math.Round(everyone / MathF.Max(1f, defines.SeedPeoplePerFarm));
            if (farmTotal == 0 && everyone > 0) farmTotal = 1;
            for (var index = 0; index < count; index++)
                farms[index] = countryside > 0
                    ? farmTotal * rural[index] / countryside
                    : everyone > 0 ? farmTotal * Math.Max(0, counts[index]) / everyone : 0;

            FillLargestRemainder(farms.AsSpan(0, count), farmTotal, counts, farmShares.AsSpan(0, count));
            FillLargestRemainder(industry.AsSpan(0, count), (int)Math.Round(industrySum), counts, industryShares.AsSpan(0, count));

            for (var index = 0; index < count; index++) result[index] = (farmShares[index], industryShares[index]);
            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rural);
            ArrayPool<double>.Shared.Return(industry);
            ArrayPool<double>.Shared.Return(farms);
            ArrayPool<int>.Shared.Return(farmShares);
            ArrayPool<int>.Shared.Return(industryShares);
        }
    }

    private readonly struct RemainderSlot
    {
        public readonly double Fraction;
        public readonly long People;
        public readonly int Index;

        public RemainderSlot(double fraction, long people, int index)
        {
            Fraction = fraction;
            People = people;
            Index = index;
        }
    }

    private static readonly Comparison<RemainderSlot> CompareRemainderSlot = static (a, b) =>
    {
        var cmp = b.Fraction.CompareTo(a.Fraction);
        if (cmp != 0) return cmp;
        cmp = b.People.CompareTo(a.People);
        return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
    };

    /// <summary>
    /// Whole shares of a whole total, in proportion to exact ones: everybody gets the whole
    /// part of theirs, and what is left goes one at a time to the largest fractions. Ties go to
    /// the more populous region and then to the earlier one, so the answer is the same on every
    /// machine.
    /// </summary>
    internal static int[] LargestRemainder(IReadOnlyList<double> exact, int total, IReadOnlyList<long> people)
    {
        var count = exact.Count;
        var shares = new int[count];
        if (count == 0) return shares;
        if (exact is double[] arr)
        {
            FillLargestRemainder(arr.AsSpan(0, count), total, people, shares.AsSpan());
            return shares;
        }
        var rented = ArrayPool<double>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++) rented[i] = exact[i];
            FillLargestRemainder(rented.AsSpan(0, count), total, people, shares.AsSpan());
            return shares;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    private static void FillLargestRemainder(ReadOnlySpan<double> exact, int total, IReadOnlyList<long> people, Span<int> shares)
    {
        var count = exact.Length;
        if (count == 0) return;
        var given = 0;
        for (var index = 0; index < count; index++)
        {
            var whole = (int)Math.Floor(Math.Max(0, exact[index]));
            shares[index] = whole;
            given += whole;
        }
        if (given >= total) return;
        var slots = ArrayPool<RemainderSlot>.Shared.Rent(count);
        try
        {
            for (var index = 0; index < count; index++)
                slots[index] = new RemainderSlot(Math.Max(0, exact[index]) - shares[index], people[index], index);
            var span = slots.AsSpan(0, count);
            span.Sort(CompareRemainderSlot);
            for (var step = 0; given < total; step++, given++)
                shares[span[step % count].Index]++;
        }
        finally
        {
            ArrayPool<RemainderSlot>.Shared.Return(slots);
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
        var top = new List<Nation>();
        var nations = campaign.Nations;
        for (var i = 0; i < nations.Count; i++)
            if (nations[i].Gdp > 0f) top.Add(nations[i]);
        top.Sort(static (a, b) => b.Gdp.CompareTo(a.Gdp));
        var limit = Math.Min(10, top.Count);
        for (var i = 0; i < limit; i++)
        {
            var nation = top[i];
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
            var liveWars = Warfare.LiveWars(campaign);
            for (var wi = 0; wi < liveWars.Count; wi++)
            {
                var war = liveWars[wi];
                if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
            }

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
        var wars = Warfare.LiveWars(campaign);
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
        if (campaign.ElapsedDays == 0) yield break;
        var every = Math.Max(1, campaign.Defines.EconomyTickDays);
        var marketDay = campaign.ElapsedDays % campaign.Defines.EconomyTickDays == 0;
        var staggered = campaign.Defines.StaggerWeeklyEconomy || campaign.Defines.EconomyWeekStaggered;
        if (!marketDay && !staggered) yield break;

        if (marketDay)
            using (var market = MarketSystem.TickStages(campaign).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("market")) more = market.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
            }

        var nationsList = staggered ? (IReadOnlyList<Nation>)campaign.Nations : campaign.ActiveNations;
        var atWarById = BuildAtWarLookup(campaign, nationsList, out var maxNationId, out var atWarOverflow);
        try
        {
            if (nationsList.Count > 0) ControlTally.World(campaign, 0, 0);
            using var prepList = new HeapPooledList<EconomyNationPrep>(64);
            using var workList = new HeapPooledList<(int PrepIndex, ScenarioState State)>(512);
            var batchYieldLimit = Campaign.IsUnboundedStep ? int.MaxValue : 32;
            var ranInBatch = 0;
            for (var index = 0; index < nationsList.Count; index++)
            {
                if (staggered && index % every != campaign.ElapsedDays % every) continue;
                var nation = nationsList[index];
                if (staggered && campaign.IsRetired(nation)) continue;
                if (ranInBatch >= batchYieldLimit && Campaign.StageOverdue())
                {
                    FlushEconomyBatch(campaign, prepList, workList);
                    prepList.Clear();
                    workList.Clear();
                    ranInBatch = 0;
                    yield return 0;
                }
                ranInBatch++;
                using var stage = SimProfiler.Measure("economy");
                ProcessNationEconomyWeek(campaign, nation, maxNationId, atWarById, atWarOverflow, prepList, workList);
            }
            if (prepList.Count > 0)
            {
                using var stage = SimProfiler.Measure("economy");
                FlushEconomyBatch(campaign, prepList, workList);
            }
        }
        finally
        {
            if (maxNationId >= 0) ArrayPool<bool>.Shared.Return(atWarById);
        }
    }

    private static bool[] BuildAtWarLookup(
        Campaign campaign,
        IReadOnlyList<Nation> nationsList,
        out int maxNationId,
        out HashSet<int> atWarOverflow)
    {
        maxNationId = -1;
        for (var i = 0; i < nationsList.Count; i++)
            if (nationsList[i].Id > maxNationId) maxNationId = nationsList[i].Id;
        var atWarById = maxNationId >= 0 ? ArrayPool<bool>.Shared.Rent(maxNationId + 1) : Array.Empty<bool>();
        if (maxNationId >= 0) Array.Clear(atWarById, 0, maxNationId + 1);
        atWarOverflow = null;

        var live = Warfare.LiveWars(campaign);
        for (var wi = 0; wi < live.Count; wi++)
        {
            var w = live[wi];
            MarkWarParticipant(w.AttackerId, maxNationId, atWarById, ref atWarOverflow);
            MarkWarParticipant(w.DefenderId, maxNationId, atWarById, ref atWarOverflow);
            foreach (var id in w.AttackerAllies) MarkWarParticipant(id, maxNationId, atWarById, ref atWarOverflow);
            foreach (var id in w.DefenderAllies) MarkWarParticipant(id, maxNationId, atWarById, ref atWarOverflow);
        }
        return atWarById;
    }

    private static void MarkWarParticipant(int id, int maxNationId, bool[] atWarById, ref HashSet<int> overflow)
    {
        if ((uint)id <= (uint)maxNationId) atWarById[id] = true;
        else (overflow ??= new()).Add(id);
    }

    private static void ProcessNationEconomyWeek(
        Campaign campaign,
        Nation nation,
        int maxNationId,
        bool[] atWarById,
        HashSet<int> atWarOverflow,
        HeapPooledList<EconomyNationPrep> prepList,
        HeapPooledList<(int PrepIndex, ScenarioState State)> workList)
    {
        MoneyLedger.Close(campaign, nation);
        IncomePenalties.Expire(campaign, nation);
        var states = campaign.StatesOf(nation);
        var fallen = campaign.Fallen.Contains(nation.Id) && states.Length == 0;
        if (nation.Movement is null && (states.Length == 0 || fallen))
        {
            nation.Population = fallen ? 0 : nation.Population;
            nation.Gdp = nation.GrossOutput = 0;
            nation.WeeklyTaxIncome = nation.WeeklyExpenses = nation.WeeklyBalance = 0;
            return;
        }

        var atWar = (uint)nation.Id <= (uint)maxNationId
            ? atWarById[nation.Id]
            : (atWarOverflow is not null && atWarOverflow.Contains(nation.Id));
        SovereignFinance.Prepare(campaign, nation);
        var weeklyOutput = nation.Gdp / 52f;
        var cabinet = GovernmentFocusCatalog.Of(nation.GovernmentFocus);
        var government = PoliticsCore.GovernmentOf(nation);
        var ideology = PoliticsCore.IdeologyOf(nation);
        var mismatch = PoliticsCore.IsMismatched(nation) ? ideology.MismatchStability : 0f;

        var budget = BudgetCore.Evaluate(nation, weeklyOutput, atWar, MoneyLedger.BudgetWeek(nation), out var interest);
        ApplyNationBudgetAndInflation(campaign, nation, weeklyOutput, atWar, budget, interest);

        var industrialSupply = nation.Market.GetValueOrDefault(Commodity.IndustrialMaterials)?.Satisfaction ?? 1f;
        var energySupply = nation.Market.GetValueOrDefault(Commodity.Energy)?.Satisfaction ?? 1f;
        var marketOutput = 0.65f + industrialSupply * 0.18f + energySupply * 0.17f;

        var macroWave = GeopoliticalCycle.MacroEconomicWave(campaign, nation);
        var sclerosis = GeopoliticalCycle.InstitutionalSclerosis(campaign, nation);
        var conditions = 0.006f + (nation.Stability - 0.5f) * 0.018f - (atWar ? 0.018f : 0f) -
                         nation.ShortageIndex * campaign.Defines.ShortageAnnualGrowthPenalty + budget.Growth +
                         GovernmentFocusRules.AnnualGrowth(nation.GovernmentFocus) +
                         government.Growth + ideology.Growth -
                         (1f - marketOutput) * 0.020f +
                         macroWave * 0.024f - sclerosis * 0.010f;
        nation.Productivity = M.Clamp(nation.Productivity * (1f + conditions / 52f), 0.15f, 4f);

        SecularCycle.Week(campaign, nation, atWar);
        UpdateNationGdpFromProduction(campaign, nation);

        var focusRadicalism = cabinet.Radicalism + government.Radicalism + ideology.Radicalism;
        var cycleRadicalism = (macroWave < -0.15f ? (-macroWave - 0.15f) * 0.0022f : 0f) + sclerosis * 0.0010f;
        var foodSupply = nation.Market.TryGetValue(Commodity.Food, out var fq) ? Math.Clamp(fq.Satisfaction, 0f, 1f) : 1f;
        var medSupply = nation.Market.TryGetValue(Commodity.Pharmaceuticals, out var mq) ? Math.Clamp(mq.Satisfaction, 0f, 1f) : 1f;
        var energySupplyClamped = Math.Clamp(energySupply, 0f, 1f);

        var prepIdx = prepList.Count;
        prepList.Add(new EconomyNationPrep(
            nation, states, conditions, atWar,
            budget.Radicalism, focusRadicalism, foodSupply, energySupplyClamped, medSupply,
            cabinet, government, ideology, budget.Living, budget.Stability, mismatch, cycleRadicalism));
        for (var s = 0; s < states.Length; s++)
            workList.Add((prepIdx, states[s]));
    }

    private static void ApplyNationBudgetAndInflation(
        Campaign campaign,
        Nation nation,
        float weeklyOutput,
        bool atWar,
        BudgetOutcome budget,
        float interest)
    {
        nation.WeeklyTaxIncome = budget.Taxation;
        nation.WeeklyExpenses = budget.Expenses;
        var macroWave = GeopoliticalCycle.MacroEconomicWave(campaign, nation);
        var fiscalBalance = budget.Balance;
        if (macroWave < -0.20f)
        {
            var contraction = (-macroWave - 0.20f) * MathF.Max(0f, budget.GrossRevenue) * 0.25f;
            fiscalBalance -= contraction;
        }
        nation.WeeklyBalance = fiscalBalance;
        nation.WeeklyGrossIncome = budget.GrossRevenue;
        nation.WeeklyIncomePenalty = budget.IncomePenalty;
        MoneyLedger.BookPenalty(nation, budget.IncomePenalty);

        if (nation.IsHuman)
        {
            BudgetCore.Itemise(nation, weeklyOutput, atWar);
            nation.Budget.Settle(fiscalBalance, Loc.Get("rounding"));
        }

        RegionalAutonomy.PayWeek(campaign, nation);
        SovereignFinance.Settle(campaign, nation, fiscalBalance, interest);
        if (!nation.IsHuman || Delegation.Holds(nation, Delegation.Budget))
            SovereignFinance.ManageLiquidity(campaign, nation);
        ProductionMethods.EvaluateAi(campaign, nation);
        NationalAmbitions.TickWeekly(campaign, nation);

        var scarcity = MathF.Max(0f, nation.ShortageIndex - campaign.Defines.ScarcityTolerance);
        InflationCore.Tick(campaign, nation, weeklyOutput, atWar, scarcity);
        if (macroWave > 0.45f)
            nation.Inflation = M.Clamp(nation.Inflation + (macroWave - 0.45f) * 0.0028f, InflationCore.Weights.Floor, InflationCore.Weights.Ceiling);
        else if (macroWave < -0.35f && nation.PublicDebt > nation.Gdp * 0.6f)
            nation.Inflation = M.Clamp(nation.Inflation + (-macroWave - 0.35f) * 0.0032f, InflationCore.Weights.Floor, InflationCore.Weights.Ceiling);
    }

    private static void UpdateNationGdpFromProduction(Campaign campaign, Nation nation)
    {
        if (campaign.ElapsedDays < campaign.Defines.OutputSpinUpDays && nation.GrossOutput > 0f && nation.Gdp > 0f)
        {
            var spun = nation.Gdp / (nation.GrossOutput * 52d);
            if (double.IsFinite(spun) && spun > 0d && spun < float.MaxValue) nation.OutputScale = (float)spun;
        }
        if (nation.GrossOutput <= 0f || nation.OutputScale <= 0f) return;

        var target = nation.GrossOutput * 52d * nation.OutputScale;
        var next = nation.Gdp + (target - nation.Gdp) * campaign.Defines.GdpConvergenceRate;
        if (!double.IsFinite(target) || !double.IsFinite(next) || next > float.MaxValue || next < -float.MaxValue)
            throw new CampaignIntegrityException("country " + nation.Tag + ".Gdp from production");
        nation.Gdp = (float)Math.Max(0.01, next);
    }

    private readonly record struct EconomyNationPrep(
        Nation Nation,
        ScenarioState[] States,
        float AnnualGrowth,
        bool AtWar,
        float WelfareRadicalism,
        float FocusRadicalism,
        float FoodSupply,
        float EnergySupply,
        float MedSupply,
        GovernmentFocusDefinition Cabinet,
        GovernmentDefinition Government,
        IdeologyDefinition Ideology,
        float BudgetLiving,
        float BudgetStability,
        float Mismatch,
        float CycleRadicalism);

    private sealed class EconomyFlushBatchScratch
    {
        public bool[] HouseholdDone = Array.Empty<bool>();
        public Nation[] VitalNations = Array.Empty<Nation>();
        public ScenarioState[] VitalStates = Array.Empty<ScenarioState>();
        public bool[] VitalAtWar = Array.Empty<bool>();
        public int[] VitalWorkIndex = Array.Empty<int>();
        public float[] OutBirth = Array.Empty<float>();
        public float[] OutDeath = Array.Empty<float>();
        public float[] OutOldBurden = Array.Empty<float>();
        public long[] OutRb = Array.Empty<long>();
        public long[] OutRd = Array.Empty<long>();

        public float[] NationSol = Array.Empty<float>();
        public float[] WeeklyRadDelta = Array.Empty<float>();
        public float[] CycleRad = Array.Empty<float>();
        public float[] StateSol = Array.Empty<float>();
        public float[] StateRad = Array.Empty<float>();

        public void EnsureCapacity(int count)
        {
            if (HouseholdDone.Length >= count) return;
            var cap = Math.Max(count + 64, 512);
            HouseholdDone = new bool[cap];
            VitalNations = new Nation[cap];
            VitalStates = new ScenarioState[cap];
            VitalAtWar = new bool[cap];
            VitalWorkIndex = new int[cap];
            OutBirth = new float[cap];
            OutDeath = new float[cap];
            OutOldBurden = new float[cap];
            OutRb = new long[cap];
            OutRd = new long[cap];
            NationSol = new float[cap];
            WeeklyRadDelta = new float[cap];
            CycleRad = new float[cap];
            StateSol = new float[cap];
            StateRad = new float[cap];
        }
    }

    [ThreadStatic] private static EconomyFlushBatchScratch _flushScratch;

    private static void FlushEconomyBatch(Campaign campaign, HeapPooledList<EconomyNationPrep> preps,
        HeapPooledList<(int PrepIndex, ScenarioState State)> work)
    {
        const int FlatStateParallelMin = 16;
        using (SimProfiler.Measure("eco:states"))
        {
            var workCount = work.Count;
            if (workCount >= 8 &&
                campaign.Defines.HouseholdDynamicsEnabled &&
                Kernels.NativeKernelsLoader.EnableEconomyDemoDefault &&
                Kernels.NativeKernelsLoader.HasCapability(Kernels.NativeKernelsLoader.CapEconomyDemo))
            {
                var scratch = _flushScratch ??= new EconomyFlushBatchScratch();
                scratch.EnsureCapacity(workCount);

                if (workCount >= FlatStateParallelMin && DeterministicParallel.Enabled(campaign))
                {
                    DeterministicParallel.ForRange(campaign, 0, workCount, (from, to) =>
                    {
                        for (var i = from; i < to; i++)
                        {
                            var (prepIdx, state) = work[i];
                            var p = preps[prepIdx];
                            scratch.HouseholdDone[i] = HouseholdEconomy.ReviewGroupsForBatch(
                                campaign, p.Nation, state, p.AnnualGrowth,
                                p.FoodSupply, p.EnergySupply, p.MedSupply, MarketSystem.JobsDemanded(state));
                        }
                    }, minItems: FlatStateParallelMin);
                }
                else
                {
                    for (var i = 0; i < workCount; i++)
                    {
                        var (prepIdx, state) = work[i];
                        var p = preps[prepIdx];
                        scratch.HouseholdDone[i] = HouseholdEconomy.ReviewGroupsForBatch(
                            campaign, p.Nation, state, p.AnnualGrowth,
                            p.FoodSupply, p.EnergySupply, p.MedSupply, MarketSystem.JobsDemanded(state));
                    }
                }

                var vitalCount = 0;
                for (var i = 0; i < workCount; i++)
                {
                    if (!scratch.HouseholdDone[i]) continue;
                    var (prepIdx, state) = work[i];
                    var p = preps[prepIdx];
                    scratch.VitalNations[vitalCount] = p.Nation;
                    scratch.VitalStates[vitalCount] = state;
                    scratch.VitalAtWar[vitalCount] = p.AtWar;
                    scratch.VitalWorkIndex[vitalCount] = i;
                    vitalCount++;
                }

                if (vitalCount > 0)
                {
                    var interval = Math.Clamp(campaign.Defines.EconomyTickDays, 1, 365) / 365f;
                    HouseholdEconomy.ComputeVitalRatesBatch(
                        campaign,
                        scratch.VitalNations.AsSpan(0, vitalCount),
                        scratch.VitalStates.AsSpan(0, vitalCount),
                        scratch.VitalAtWar.AsSpan(0, vitalCount),
                        interval,
                        scratch.OutBirth.AsSpan(0, vitalCount),
                        scratch.OutDeath.AsSpan(0, vitalCount),
                        scratch.OutOldBurden.AsSpan(0, vitalCount),
                        scratch.OutRb.AsSpan(0, vitalCount),
                        scratch.OutRd.AsSpan(0, vitalCount));
                    for (var vi = 0; vi < vitalCount; vi++)
                    {
                        HouseholdEconomy.FinishBatchedVitalStep(
                            campaign, scratch.VitalNations[vi], scratch.VitalStates[vi],
                            scratch.OutRb[vi], scratch.OutRd[vi]);
                        scratch.VitalNations[vi] = null;
                        scratch.VitalStates[vi] = null;
                    }
                }

                for (var i = 0; i < workCount; i++)
                {
                    var (prepIdx, state) = work[i];
                    var p = preps[prepIdx];
                    var oldSol = state.StandardOfLiving;
                    scratch.NationSol[i] = p.Nation.StandardOfLiving;
                    scratch.StateSol[i] = oldSol;
                    state.StandardOfLiving = M.Lerp(oldSol, p.Nation.StandardOfLiving, 0.04f);
                    scratch.WeeklyRadDelta[i] = RadicalismCore.WeeklyChange(
                        campaign, p.Nation, state, p.AtWar, p.WelfareRadicalism, p.FocusRadicalism);
                    scratch.CycleRad[i] = p.CycleRadicalism;
                    scratch.StateRad[i] = state.Radicalism;
                }

                Kernels.NativeCombatSupportKernels.EconStateWeekPost(
                    scratch.NationSol.AsSpan(0, workCount),
                    scratch.WeeklyRadDelta.AsSpan(0, workCount),
                    scratch.CycleRad.AsSpan(0, workCount),
                    scratch.StateSol.AsSpan(0, workCount),
                    scratch.StateRad.AsSpan(0, workCount));

                for (var i = 0; i < workCount; i++)
                {
                    var (prepIdx, state) = work[i];
                    var p = preps[prepIdx];
                    state.StandardOfLiving = scratch.StateSol[i];
                    state.Radicalism = scratch.StateRad[i];
                    MinorityPressure(campaign, p.Nation, state);
                    MovementRules.MarkPeopleStateDirty(campaign, state);
                }

                for (var pi = 0; pi < preps.Count; pi++)
                    SettleNationBatchItem(campaign, preps[pi]);
                return;
            }

            if (workCount >= FlatStateParallelMin && DeterministicParallel.Enabled(campaign))
            {
                DeterministicParallel.ForRange(campaign, 0, workCount, (from, to) =>
                {
                    for (var i = from; i < to; i++)
                    {
                        var (prepIdx, state) = work[i];
                        var p = preps[prepIdx];
                        WeekState(campaign, p.Nation, p.AnnualGrowth, p.AtWar,
                            p.WelfareRadicalism, p.FocusRadicalism, p.FoodSupply, p.EnergySupply, p.MedSupply, p.CycleRadicalism, state);
                    }
                }, minItems: FlatStateParallelMin);
            }
            else
            {
                for (var i = 0; i < workCount; i++)
                {
                    var (prepIdx, state) = work[i];
                    var p = preps[prepIdx];
                    WeekState(campaign, p.Nation, p.AnnualGrowth, p.AtWar,
                        p.WelfareRadicalism, p.FocusRadicalism, p.FoodSupply, p.EnergySupply, p.MedSupply, p.CycleRadicalism, state);
                }
            }

            for (var pi = 0; pi < preps.Count; pi++)
                SettleNationBatchItem(campaign, preps[pi]);
        }
    }

    private static void SettleNationBatchItem(Campaign campaign, EconomyNationPrep p)
    {
        var nation = p.Nation;
        var states = p.States;
        long totalPopulation = 0;
        for (var s = 0; s < states.Length; s++)
        {
            var state = states[s];
            totalPopulation += state.Population;
            ConquestAcceptance.Week(campaign, nation, state);
        }
        if (states.Length > 0) nation.Population = totalPopulation;

        var focusSol = p.Cabinet.Living + p.Government.Living + p.Ideology.Living;
        var cycleSol = GeopoliticalCycle.MacroEconomicWave(campaign, nation) * 0.0018f -
                       GeopoliticalCycle.InstitutionalSclerosis(campaign, nation) * 0.0005f;
        nation.StandardOfLiving = M.Clamp01(nation.StandardOfLiving + p.AnnualGrowth * 0.004f -
            MathF.Max(0f, nation.ShortageIndex - campaign.Defines.ScarcityTolerance) * campaign.Defines.ShortageSolWeeklyPenalty +
            p.BudgetLiving + focusSol + cycleSol);
        var anchor = M.Clamp01(EconomicLiving(nation) + nation.LivingOffset);
        nation.StandardOfLiving = M.Clamp01(nation.StandardOfLiving +
            (anchor - nation.StandardOfLiving) * campaign.Defines.LivingReversionPerWeek);

        var highTechModifier = 1f;
        if (nation.Market.TryGetValue(Commodity.Electronics, out var electQuote) && electQuote.Demand > 0.05f)
            highTechModifier *= (0.88f + 0.12f * Math.Clamp(electQuote.Satisfaction, 0f, 1f));
        if (nation.Market.TryGetValue(Commodity.RareEarths, out var reQuote) && reQuote.Demand > 0.05f)
            highTechModifier *= (0.90f + 0.10f * Math.Clamp(reQuote.Satisfaction, 0f, 1f));
        var productivityTarget = (0.72f + nation.HumanCapital * 0.50f + p.Cabinet.ProductivityTarget) * highTechModifier;
        nation.Productivity = M.Lerp(nation.Productivity, productivityTarget, 0.015f);

        var securityStability = p.Cabinet.Stability + p.Government.Stability + p.Ideology.Stability + p.Mismatch;
        var limit = StabilityCore.Weights.GovernanceLimit;
        var governance = nation.StabilityGovernance + securityStability + p.BudgetStability;
        governance -= governance * M.Clamp01(StabilityCore.Weights.GovernanceDecay);
        nation.StabilityGovernance = M.Clamp(governance, -limit, limit);

        if (p.AtWar || nation.Stability < 0.42f || nation.DebtCondition >= DebtCondition.Default)
            nation.StabilityBias *= 0.996f;
        else if (nation.StabilityBias > 0.12f)
            nation.StabilityBias = 0.12f + (nation.StabilityBias - 0.12f) * 0.999f;
        else if (nation.StabilityBias < -0.10f)
            nation.StabilityBias = -0.10f + (nation.StabilityBias + 0.10f) * 0.999f;
        var settledStability = StabilityCore.Target(campaign, nation, nation.IsHuman ? nation.StabilityDrivers : null);
        nation.Stability = M.Lerp(nation.Stability, settledStability, M.Clamp01(StabilityCore.Weights.Approach));

        var focusWarSupport = p.Cabinet.WarSupport + p.Government.WarSupport + p.Ideology.WarSupport;
        var settledWarSupport = M.Clamp01(nation.WarSupport + focusWarSupport);
        if (nation.IsHuman)
        {
            nation.WarSupportDrivers.Begin("War support, weekly", "share");
            nation.WarSupportDrivers.Add("Government focus on national security", focusWarSupport);
            nation.WarSupportDrivers.Settle(settledWarSupport - nation.WarSupport, "held at its limit");
        }
        nation.WarSupport = settledWarSupport;
    }

    private static void WeekState(Campaign campaign, Nation nation, float annualGrowth, bool atWar,
        float welfareRadicalism, float focusRadicalism, float foodSupply, float energySupply, float medicineSupply,
        float cycleRadicalism, ScenarioState state)
    {
        if (campaign.Defines.HouseholdDynamicsEnabled)
            HouseholdEconomy.ReviewFast(campaign, nation, state, annualGrowth, foodSupply, energySupply, medicineSupply, atWar);
        else
        {
            var hospital = state.Buildings.GetValueOrDefault(BuildingKind.Hospital);
            var weeklyGrowth = (0.0035f + (state.StandardOfLiving - .5f) * .006f + hospital * .0005f) / 52f;
            AdjustStatePopulation(state, (long)Math.Round(state.Population * weeklyGrowth));
        }
        state.StandardOfLiving = M.Lerp(state.StandardOfLiving, nation.StandardOfLiving, 0.04f);
        state.Radicalism = M.Clamp01(state.Radicalism +
            RadicalismCore.WeeklyChange(campaign, nation, state, atWar, welfareRadicalism, focusRadicalism) +
            cycleRadicalism);
        MinorityPressure(campaign, nation, state);
        MovementRules.MarkPeopleStateDirty(campaign, state);
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

    private static readonly ImmutableArray<SocialClass> AllSocialClasses =
        ImmutableArray.Create(SocialClass.Lower, SocialClass.Middle, SocialClass.Upper);

    private static string SocialClassName(SocialClass rung) => rung switch
    {
        SocialClass.Lower => nameof(SocialClass.Lower),
        SocialClass.Middle => nameof(SocialClass.Middle),
        SocialClass.Upper => nameof(SocialClass.Upper),
        _ => rung.ToString()
    };

    private static string SectorName(Sector sector) => sector switch
    {
        Sector.Agriculture => nameof(Sector.Agriculture),
        Sector.Industry => nameof(Sector.Industry),
        Sector.Services => nameof(Sector.Services),
        Sector.Ownership => nameof(Sector.Ownership),
        _ => sector.ToString()
    };

    /// <summary>Every people living in a region, largest first.</summary>
    public static IReadOnlyList<(string Culture, long Size)> Cultures(ScenarioState state) =>
        Tally(state, static group => group.Culture);

    /// <summary>Every faith held in a region, largest first.</summary>
    public static IReadOnlyList<(string Religion, long Size)> Religions(ScenarioState state) =>
        Tally(state, static group => group.Religion);

    /// <summary>Every trade worked in a region, largest first.</summary>
    public static IReadOnlyList<(string Profession, long Size)> Professions(ScenarioState state) =>
        Tally(state, static group => group.Profession);

    /// <summary>Every social class in a region, largest first.</summary>
    public static IReadOnlyList<(string Class, long Size)> Classes(ScenarioState state) =>
        Tally(state, static group => SocialClassName(group.Class));

    /// <summary>Every sector worked in a region, largest first.</summary>
    public static IReadOnlyList<(string Sector, long Size)> Sectors(ScenarioState state) =>
        Tally(state, static group => SectorName(group.Sector));

    private static void AccumulateByKey(ScenarioState state, Func<PopulationGroup, string> of, Dictionary<string, long> totals)
    {
        var groups = state.PopulationGroups;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group.Size <= 0) continue;
            var key = of(group);
            if (string.IsNullOrEmpty(key)) key = "?";
            totals[key] = totals.GetValueOrDefault(key) + group.Size;
        }
    }

    [ThreadStatic] private static Dictionary<string, long> _tallyTotalsScratch;

    private static IReadOnlyList<(string Key, long Size)> Tally(ScenarioState state, Func<PopulationGroup, string> of)
    {
        if (state is null || state.PopulationGroups.Count == 0) return Array.Empty<(string, long)>();
        var totals = _tallyTotalsScratch ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        totals.Clear();
        AccumulateByKey(state, of, totals);
        if (totals.Count == 0) return Array.Empty<(string, long)>();
        var result = new List<(string Key, long Size)>(totals.Count);
        foreach (var entry in totals)
            result.Add((entry.Key, entry.Value));
        result.Sort(static (a, b) =>
        {
            var cmp = b.Size.CompareTo(a.Size);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Key, b.Key);
        });
        return result;
    }

    /// <summary>Largest culture living in a region with exact tie-breaking of <see cref="Cultures"/>, without List/Sort allocation.</summary>
    internal static (string Culture, long Size) DominantCulture(ScenarioState state)
    {
        if (state is null || state.PopulationGroups.Count == 0) return default;
        var totals = _tallyTotalsScratch ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        totals.Clear();
        AccumulateByKey(state, static group => group.Culture, totals);
        string bestKey = null;
        long bestSize = 0;
        foreach (var entry in totals)
        {
            var cmp = entry.Value.CompareTo(bestSize);
            if (bestKey is null || cmp > 0 || (cmp == 0 && string.CompareOrdinal(entry.Key, bestKey) < 0))
            {
                bestKey = entry.Key;
                bestSize = entry.Value;
            }
        }
        return bestKey is null ? default : (bestKey, bestSize);
    }

    /// <summary>Total population of one culture in a region, allocating nothing.</summary>
    internal static long CultureSize(ScenarioState state, string culture)
    {
        if (state is null || string.IsNullOrEmpty(culture) || state.PopulationGroups.Count == 0) return 0;
        long total = 0;
        var groups = state.PopulationGroups;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group.Size > 0 && string.Equals(group.Culture, culture, StringComparison.OrdinalIgnoreCase))
                total += group.Size;
        }
        return total;
    }

    /// <summary>Total population belonging to any of the listed cultures in a region, allocating nothing.</summary>
    internal static long PeoplesSize(ScenarioState state, IReadOnlyList<string> peoples)
    {
        if (state is null || peoples is null || peoples.Count == 0 || state.PopulationGroups.Count == 0) return 0;
        long total = 0;
        var groups = state.PopulationGroups;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group.Size <= 0 || string.IsNullOrEmpty(group.Culture)) continue;
            for (var pi = 0; pi < peoples.Count; pi++)
            {
                if (string.Equals(peoples[pi], group.Culture, StringComparison.OrdinalIgnoreCase))
                {
                    total += group.Size;
                    break;
                }
            }
        }
        return total;
    }

    /// <summary>Adds each population group's culture size in <paramref name="state"/> into a caller-owned dictionary.</summary>
    internal static void AccumulateCultures(ScenarioState state, Dictionary<string, long> into)
    {
        if (state is null || state.PopulationGroups.Count == 0) return;
        AccumulateByKey(state, static group => group.Culture, into);
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
    private static void MinorityPressure(Campaign campaign, Nation nation, ScenarioState state) =>
        CompactEmptiedGroups(state);

    /// <summary>
    /// Removes emptied (size <= 0) population cohorts in place while preserving list order and avoiding allocations.
    /// </summary>
    internal static void CompactEmptiedGroups(ScenarioState state)
    {
        var groups = state.PopulationGroups;
        var count = groups.Count;
        var kept = 0;
        while (kept < count && groups[kept].Size > 0) kept++;
        if (kept == count) return;
        for (var index = kept + 1; index < count; index++)
        {
            var group = groups[index];
            if (group.Size <= 0) continue;
            groups[kept++] = group;
        }
        groups.RemoveRange(kept, count - kept);
    }

    /// <summary>
    /// Strips any population group emptied on a shared weekly pass (migration, brain drain, war damage)
    /// when staggered economies defer a region's own demographic review to another weekday.
    /// </summary>
    internal static void CompactWorldEmptiedGroups(Campaign campaign)
    {
        var states = campaign.States;
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (state is null || state.Unclaimed || state.PopulationGroups.Count == 0) continue;
            CompactEmptiedGroups(state);
        }
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

    private static readonly FrozenSet<string> CoalRichTags =
        FrozenSet.ToFrozenSet(new[] { "CHN", "USA", "IND", "RUS", "AUS", "DEU", "POL", "IDN", "ZAF", "UKR" }, StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> OilRichTags =
        FrozenSet.ToFrozenSet(new[] { "SAU", "RUS", "USA", "CAN", "IRN", "IRQ", "ARE", "VEN", "KWT", "QAT", "NOR", "KAZ", "AZE", "NGA", "BRA" }, StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> IronOreRichTags =
        FrozenSet.ToFrozenSet(new[] { "AUS", "BRA", "CHN", "IND", "RUS", "UKR", "CAN", "SWE", "ZAF", "KAZ" }, StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> RareEarthsRichTags =
        FrozenSet.ToFrozenSet(new[] { "CHN", "USA", "AUS", "MMR", "VNM", "BRA", "IND", "RUS" }, StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> NaturalGasRichTags =
        FrozenSet.ToFrozenSet(new[] { "RUS", "USA", "QAT", "IRN", "TKM", "SAU", "AUS", "NOR", "DZA", "CAN", "CHN", "NGA", "EGY" }, StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> BauxiteRichTags =
        FrozenSet.ToFrozenSet(new[] { "AUS", "GIN", "BRA", "CHN", "IND", "IDN", "JAM", "RUS", "KAZ", "VNM" }, StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> UraniumRichTags =
        FrozenSet.ToFrozenSet(new[] { "KAZ", "CAN", "AUS", "NAM", "NER", "RUS", "UZB", "USA", "ZAF", "UKR" }, StringComparer.OrdinalIgnoreCase);

    private static void SeedResources(ScenarioState state, Nation nation)
    {
        state.ResourceDeposits.Clear();
        var basePotential = 0.15f + StableFraction(state.MapIndex + 101) * 0.55f;
        state.ResourceDeposits[Commodity.Food] = 0.75f + StableFraction(state.MapIndex + 131) * 1.75f;
        state.ResourceDeposits[Commodity.Coal] = TaggedPotential(nation.Tag, CoalRichTags, basePotential, state.MapIndex + 151);
        state.ResourceDeposits[Commodity.Oil] = TaggedPotential(nation.Tag, OilRichTags, basePotential * 0.55f, state.MapIndex + 181) * 2.85f;
        state.ResourceDeposits[Commodity.IronOre] = TaggedPotential(nation.Tag, IronOreRichTags, basePotential * 0.75f, state.MapIndex + 211);
        state.ResourceDeposits[Commodity.RareEarths] = TaggedPotential(nation.Tag, RareEarthsRichTags, basePotential * 0.22f, state.MapIndex + 241);

        // The four the second tier of industry starts from. Gas and bauxite decide who can
        // run a chemical industry and a smelter at all; uranium decides who is watched.
        // Timber is deliberately not tagged to a short list - almost everywhere has some,
        // and a good that everybody has is a useful counterweight to four that they do not.
        state.ResourceDeposits[Commodity.NaturalGas] = TaggedPotential(nation.Tag, NaturalGasRichTags, basePotential * 0.60f, state.MapIndex + 271) * 2.70f;
        state.ResourceDeposits[Commodity.Bauxite] = TaggedPotential(nation.Tag, BauxiteRichTags, basePotential * 0.30f, state.MapIndex + 307);
        state.ResourceDeposits[Commodity.Uranium] = TaggedPotential(nation.Tag, UraniumRichTags, basePotential * 0.14f, state.MapIndex + 337) * 0.23f;
        state.ResourceDeposits[Commodity.Timber] = (0.35f + StableFraction(state.MapIndex + 367) * 1.45f) * 1.90f;
    }

    private static float TaggedPotential(string tag, FrozenSet<string> richTags, float fallback, int seed)
    {
        var rich = !string.IsNullOrEmpty(tag) && richTags.Contains(tag);
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

        var fallback = composition is null || composition.Count == 0;
        var fallbackCulture = fallback ? nation.Tag.ToLowerInvariant() : null;
        var compCount = fallback ? 1 : composition!.Count;

        var trades = DemographyCatalog.Professions;
        if (trades.Count == 0)
        {
            SeedUnprofessedRungs(state, population, composition, fallback, fallbackCulture, compCount);
            return;
        }

        float[] rentedWeights = null;
        Span<float> weights = trades.Count <= 128
            ? stackalloc float[128].Slice(0, trades.Count)
            : (rentedWeights = ArrayPool<float>.Shared.Rent(trades.Count)).AsSpan(0, trades.Count);
        try
        {
            var weightTotal = ComputeTradeWeights(state, trades, farming, weights);
            if (weightTotal <= 0f) return;

            var floor = Math.Max(1L, population / 1000);
            long placed = 0;
            PopulationGroup largest = null;
            for (var c = 0; c < compCount; c++)
            {
                var (culture, share) = fallback ? (fallbackCulture!, 1f) : composition![c];
                var faith = DemographyCatalog.FaithOf(culture, state.Id, nation?.Tag);
                var people = (long)(population * share);
                if (people <= 0) continue;
                PlaceCultureGroups(state, trades, weights, weightTotal, culture, faith, people, floor, ref placed, ref largest);
            }

            if (largest is null)
            {
                var firstCulture = fallback ? fallbackCulture! : composition![0].Culture;
                largest = Add(state, firstCulture, DemographyCatalog.FaithOf(firstCulture, state.Id, nation?.Tag)[0].Religion,
                    trades[0].Id, trades[0].Class, trades[0].Sector, 0,
                    trades[0].Employment, trades[0].Wealth);
            }
            largest.Size += population - placed;
            if (largest.Size < 0) largest.Size = 0;
        }
        finally
        {
            if (rentedWeights is not null) ArrayPool<float>.Shared.Return(rentedWeights);
        }
    }

    private static void SeedUnprofessedRungs(
        ScenarioState state,
        long population,
        IReadOnlyList<(string Culture, float Share)> composition,
        bool fallback,
        string fallbackCulture,
        int compCount)
    {
        for (var c = 0; c < compCount; c++)
        {
            var (culture, share) = fallback ? (fallbackCulture!, 1f) : composition![c];
            for (var r = 0; r < AllSocialClasses.Length; r++)
            {
                var rung = AllSocialClasses[r];
                Add(state, culture, "", SocialClassName(rung), rung, Sector.Services,
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
            }
        }
    }

    private static float ComputeTradeWeights(
        ScenarioState state,
        IReadOnlyList<ProfessionDefinition> trades,
        (float Fields, float Town) farming,
        Span<float> weights)
    {
        double weightSum = 0;
        for (var index = 0; index < trades.Count; index++)
        {
            var weight = Math.Max(0f, trades[index].ShareAt(state.Urbanization));
            weights[index] = weight;
            weightSum += weight;
        }
        var weightTotal = (float)weightSum;
        if (weightTotal <= 0f) return 0f;
        if (farming.Fields == 1f && farming.Town == 1f) return weightTotal;

        weightSum = 0;
        for (var index = 0; index < trades.Count; index++)
        {
            var weight = weights[index] * (trades[index].Sector == Sector.Agriculture ? farming.Fields : farming.Town);
            weights[index] = weight;
            weightSum += weight;
        }
        return (float)weightSum;
    }

    private static void PlaceCultureGroups(
        ScenarioState state,
        IReadOnlyList<ProfessionDefinition> trades,
        ReadOnlySpan<float> weights,
        float weightTotal,
        string culture,
        IReadOnlyList<(string Religion, float Share)> faith,
        long people,
        long floor,
        ref long placed,
        ref PopulationGroup largest)
    {
        for (var index = 0; index < trades.Count; index++)
        {
            var trade = trades[index];
            var ofTrade = (long)(people * (weights[index] / weightTotal));
            if (ofTrade < floor) continue;
            for (var f = 0; f < faith.Count; f++)
            {
                var (religion, faithShare) = faith[f];
                var size = (long)(ofTrade * faithShare);
                if (size < floor) continue;
                var group = Add(state, culture, religion, trade.Id, trade.Class,
                    trade.Sector, size, trade.Employment, trade.Wealth);
                placed += size;
                if (largest is null || group.Size > largest.Size) largest = group;
            }
        }
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
        if (nation.AgricultureShare <= 0f || trades.Count == 0 || states.Count == 0) return (1f, 1f);

        var people = ArrayPool<double>.Shared.Rent(states.Count);
        var fieldShare = ArrayPool<double>.Shared.Rent(states.Count);
        var denomScratch = ArrayPool<double>.Shared.Rent(states.Count);
        var workScratch = ArrayPool<double>.Shared.Rent(states.Count);
        try
        {
            var activeCount = 0;
            double everything = 0;
            var anyPositiveField = false;
            for (var index = 0; index < states.Count; index++)
            {
                var here = Math.Max(0, counts[index]);
                if (here <= 0) continue;
                double all = 0, fields = 0;
                for (var t = 0; t < trades.Count; t++)
                {
                    var trade = trades[t];
                    var share = Math.Max(0f, trade.ShareAt(urbanisation[index]));
                    all += share;
                    if (trade.Sector == Sector.Agriculture) fields += share;
                }
                if (all <= 0) continue;
                var fs = fields / all;
                if (fs > 0) anyPositiveField = true;
                people[activeCount] = here;
                fieldShare[activeCount] = fs;
                activeCount++;
                everything += here;
            }
            if (everything <= 0 || !anyPositiveField) return (1f, 1f);

            var stated = M.Clamp(nation.AgricultureShare, 0.001f, 0.95f);
            var pSpan = people.AsSpan(0, activeCount);
            var fSpan = fieldShare.AsSpan(0, activeCount);
            var dSpan = denomScratch.AsSpan(0, activeCount);
            var wSpan = workScratch.AsSpan(0, activeCount);

            // Bisection on log-ratio using TensorPrimitives vector kernels on .NET 9+ (scalar fallback on net8.0 tools).
            double low = -12, high = 12;
            for (var step = 0; step < 60; step++)
            {
                var middle = (low + high) / 2;
                var ratio = Math.Exp(middle);
                var farmers = EvaluateAgrarianFarmers(pSpan, fSpan, dSpan, wSpan, ratio);
                if (farmers / everything < stated) low = middle; else high = middle;
            }
            return ((float)Math.Exp((low + high) / 2), 1f);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(people);
            ArrayPool<double>.Shared.Return(fieldShare);
            ArrayPool<double>.Shared.Return(denomScratch);
            ArrayPool<double>.Shared.Return(workScratch);
        }
    }

    private static double EvaluateAgrarianFarmers(
        ReadOnlySpan<double> pSpan,
        ReadOnlySpan<double> fSpan,
        Span<double> dSpan,
        Span<double> wSpan,
        double ratio)
    {
#if NET9_0_OR_GREATER
        TensorPrimitives.Multiply<double>(fSpan, ratio - 1.0, dSpan);
        TensorPrimitives.Add<double>(dSpan, 1.0, dSpan);
        TensorPrimitives.Divide<double>(pSpan, dSpan, wSpan);
        TensorPrimitives.Multiply<double>(wSpan, fSpan, wSpan);
        return ratio * TensorPrimitives.Sum<double>(wSpan);
#else
        var sum = 0.0;
        var ratioMinusOne = ratio - 1.0;
        for (var i = 0; i < pSpan.Length; i++)
        {
            var f = fSpan[i];
            sum += pSpan[i] / (f * ratioMinusOne + 1.0) * f;
        }
        return ratio * sum;
#endif
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
        var groups = CollectionsMarshal.AsSpan(state.PopulationGroups);
        if (groups.Length == 0 || delta == 0) return;
        long total = 0;
        for (var i = 0; i < groups.Length; i++) total += groups[i].Size;
        long cumulative = 0, assigned = 0;
        var anyEmptied = false;
        for (var index = 0; index < groups.Length; index++)
        {
            cumulative += groups[index].Size;
            var through = index == groups.Length - 1 ? state.Population :
                (long)Math.Round(state.Population * (cumulative / (double)Math.Max(1, total)));
            var next = Math.Max(0, through - assigned);
            groups[index].Size = next;
            if (next == 0) anyEmptied = true;
            assigned = through;
        }
        if (anyEmptied) CompactEmptiedGroups(state);
    }

    internal static long SumLong(ReadOnlySpan<long> values)
    {
        if (values.IsEmpty) return 0L;
#if NET9_0_OR_GREATER
        return TensorPrimitives.Sum<long>(values);
#else
        long total = 0;
        for (var i = 0; i < values.Length; i++) total += values[i];
        return total;
#endif
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
        if (!campaign.StepOpensNewDay) return;

        CapitalDepreciation.Week(campaign);

        var construction = campaign.Construction;
        if (construction.Count == 0) return;

        var workingMap = _working ??= new Dictionary<int, List<ConstructionProject>>();
        foreach (var list in workingMap.Values) list.Clear();
        for (var i = 0; i < construction.Count; i++)
        {
            var item = construction[i];
            if (!workingMap.TryGetValue(item.NationId, out var list))
                workingMap[item.NationId] = list = new List<ConstructionProject>();
            list.Add(item);
        }

        var nations = campaign.Nations;
        for (var n = 0; n < nations.Count; n++)
        {
            var nation = nations[n];
            if (!workingMap.TryGetValue(nation.Id, out var sites) || sites.Count == 0) continue;
            TickNationSites(campaign, nation, sites);
        }
    }

    private static void TickNationSites(Campaign campaign, Nation nation, List<ConstructionProject> sites)
    {
        sites.Sort(CompareProjectById);
        var working = ConstructionCost.WorkingSites(campaign, nation);
        var pace = ConstructionCost.Pace(campaign, nation);

        if (nation.InstantConstruction)
        {
            for (var s = 0; s < sites.Count; s++)
            {
                var project = sites[s];
                project.Paid = project.Cost;
                project.ProgressDays = project.DurationDays;
                Complete(campaign, nation, project);
                campaign.Construction.Remove(project);
            }
            return;
        }

        ConstructionQueue.Reset(sites);
        var allowance = ConstructionQueue.WeeklyCeiling(campaign, nation) / 7f;
        for (var s = 0; s < sites.Count; s++)
        {
            var project = sites[s];
            if (!TryAdvanceSite(campaign, nation, project, pace, ref allowance, ref working)) break;
        }
        ConstructionQueue.Settle(campaign, sites);
    }

    private static bool TryAdvanceSite(
        Campaign campaign,
        Nation nation,
        ConstructionProject project,
        float pace,
        ref float allowance,
        ref int working)
    {
        if (!float.IsFinite(project.Cost) || project.Cost < 0 ||
            !float.IsFinite(project.Paid) || project.Paid < 0 ||
            !float.IsFinite(project.ProgressDays) ||
            !float.IsFinite(nation.Treasury) || !float.IsFinite(nation.ConstructionCapacity))
        {
            project.Waiting = ConstructionQueue.Invalid;
            return false;
        }

        var required = project.Cost / Math.Max(1, project.DurationDays);
        var outstanding = Math.Max(0f, project.Cost - project.Paid);
        var due = Math.Min(outstanding, required * pace);
        if (due > 0f && !(allowance > 1e-7f))
        {
            project.Waiting = ConstructionQueue.Budget;
            return --working > 0;
        }

        var paid = Math.Min(due, Math.Min(allowance, Math.Max(0f, nation.Treasury)));
        if (outstanding > 0f && paid <= 0f)
        {
            project.Waiting = ConstructionQueue.Treasury;
            return false;
        }

        var ledgerBefore = nation.Treasury;
        nation.Treasury -= paid;
        MoneyLedger.Book(nation, MoneyFlow.BuildingSites, nation.Treasury - ledgerBefore);
        allowance -= paid;
        project.Paid += paid;
        project.ProgressDays += due > 0f ? pace * MathF.Min(1f, paid / due) : pace;
        project.Waiting = ConstructionQueue.Working;
        if (project.Progress >= 0.999f)
        {
            Complete(campaign, nation, project);
            campaign.Construction.Remove(project);
        }
        return --working > 0;
    }

    private static readonly Comparison<ConstructionProject> CompareProjectById = static (a, b) => a.Id.CompareTo(b.Id);
    [ThreadStatic] private static Dictionary<int, List<ConstructionProject>> _working;

    private static void Complete(Campaign campaign, Nation nation, ConstructionProject project)
    {
        var state = campaign.StateByMapIndex(project.StateMapIndex);
        if (state is null) return;
        var definition = BuildingCatalog.Get(project.Building);
        // An inland yard cost what a coastal one cost and is worth a fraction of it. That
        // is the whole of the compromise: the building is allowed, the geography is priced.
        var yield = Waterfront.Yield(campaign, state.MapIndex, project.Building);
        state.Buildings[project.Building] = state.Buildings.GetValueOrDefault(project.Building) + 1;
        campaign.Chronicle.Record(campaign, "construction_completed", nation.Id, subject: definition.Name, place: state.Name, isPublic: false);
        // No step in GDP here. GDP is the market value of what the country sells (MarketSystem's
        // gross output), and the new building is in that from next week's market for as long as
        // it stands and sells; a one-off `gdp_gain` added on top was pulled back out by
        // GdpConvergenceRate within two months, so the number the build list showed was a lie.
        nation.ConstructionCapacity += definition.CapacityGain * yield;
        if (project.Building == BuildingKind.University)
        {
            state.Literacy = M.Clamp01(state.Literacy + BuildingCatalog.UniversityRegionalLiteracy);
            nation.Literacy = M.Clamp01(nation.Literacy + BuildingCatalog.UniversityNationalLiteracy);
            nation.Productivity += BuildingCatalog.UniversityProductivity;
        }
        if (project.Building == BuildingKind.Hospital)
            state.StandardOfLiving = M.Clamp01(state.StandardOfLiving + BuildingCatalog.HospitalLiving);
        if (string.Equals(BuildingCatalog.IdOf(project.Building), "SemiconductorFab", StringComparison.OrdinalIgnoreCase))
        {
            state.Literacy = M.Clamp01(state.Literacy + 0.008f);
            nation.Productivity = M.Clamp(nation.Productivity + 0.004f, 0.15f, 4f);
        }
    }
}

public sealed partial class Defines
{
    /// <summary>
    /// Off as shipped. On, each country closes its economic week (budget, growth, its regions'
    /// households) on a weekday of its own - its place in the list modulo the week - against the
    /// most recent market, instead of every country on the market's day. Every country still
    /// closes exactly one week a week. It takes most of the country half out of the week's heavy
    /// step, at the price of a different world: a country's budget then reads a market from up
    /// to six days earlier, and the daily passes in between read a world where some countries
    /// have closed their week and some have not. Measured for the owner in
    /// docs/PERFORMANCE_PASS_3_2026_11_18_RU.md; not part of the same-world contract.
    /// </summary>
    public bool EconomyWeekStaggered { get; set; }

    /// <summary>The weekly share of the gap between a country's living standard and what its
    /// economy carries that closes (EconomySystem.EconomicLiving): about a year and a third to halve it.</summary>
    public float LivingReversionPerWeek { get; set; } = 0.01f;
}

public sealed partial class Nation
{
    /// <summary>The living standard history wrote, less what the economy alone would give.</summary>
    public float LivingOffset;
}
