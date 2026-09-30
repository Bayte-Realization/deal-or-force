using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// What a long-range formation is made of, the way common/units states a division.
///
/// These formations do not stand on the map. An artillery brigade, a drone group, a
/// fighter wing - all of them sit at a base in one of the country's regions and reach
/// out over a radius instead. What they do when they get there is a mission, and the
/// missions are the ones Hearts of Iron taught the genre: fire in support of the
/// battles in reach, contest the sky over them, interdict the ground behind them, or
/// stand by and recover.
///
///   range_km          how far from its base the formation projects, in real kilometres
///   support_fire      contribution to friendly offence in battles inside the radius
///   air_superiority   contest score: suppresses the enemy's long-range fire over them
///   interdiction      strips enemy protection in battles inside the radius
///   air_defence       the same suppression, but from the ground: a SAM brigade that
///                     covers its own ground whether or not it was told anything
///   recon             what the formation can see - shown to the player, priced in
///   recruitment_cost   fraction of a year's GDP at peacetime spending, like a division
///   build_days        days from the order to the formation existing
///   personnel          people the formation takes out of the manpower reserve
///   missions           the missions this kind of formation can be given
///
/// A mod may add a kind of formation by adding a file to common/support: nothing about
/// the catalogue is compiled in, and nothing about the effects is either - a formation
/// with no support_fire is a pure air-defence unit, one with no air stats is artillery.
/// </summary>
public enum SupportKind
{
    /// <summary>Towed guns and rocket launchers: short reach, heavy fire, no air presence.</summary>
    Ground,
    /// <summary>Crewed aircraft: long reach, contests the sky, needs the sky to survive it.</summary>
    Air,
    /// <summary>Unmanned: cheaper than aircraft, longer reach than guns, fragile over a front.</summary>
    Drone,
    /// <summary>Missiles and radar: never leaves home, and makes the enemy wish the same.</summary>
    AirDefence
}

/// <summary>One long-range formation a country owns: what it is, where it stands, what it is doing.</summary>
public sealed class SupportFormation
{
    public int Id;
    public int NationId;
    public string TypeId = "artillery_brigade";
    /// <summary>The region the formation is based in. Bases are why ground is worth taking.</summary>
    public int StateMapIndex;
    /// <summary>The province the range is measured from: a controlled province of the base region.</summary>
    public int BaseProvinceId = -1;
    /// <summary>0 to 1: crews, airframes, barrels and readiness together. Recovers with the arms market.</summary>
    public float Strength = 1f;
    public float Experience;
    /// <summary>One of the type's missions - standby when the player has not said anything.</summary>
    public string Mission = "standby";
}

/// <summary>One order in training: what was asked for, where it will stand, how far along it is.</summary>
public sealed class SupportOrder
{
    public int Id;
    public int NationId;
    public int StateMapIndex;
    public string TypeId = "artillery_brigade";
    public float PaidCost;
    public int Personnel;
    public int DurationDays;
    public int TrainedDays;
    public int LastProcessedDay;
}

public sealed partial class Campaign
{
    /// <summary>The long-range forces of every country: guns, aircraft, drones and missile brigades.</summary>
    public readonly List<SupportFormation> SupportFormations = new();
    public readonly List<SupportOrder> SupportOrders = new();
    /// <summary>The same forces by nation, rebuilt each morning. A battle asks who reaches
    /// into it, and with a hundred and forty wars running the answer has to be a lookup
    /// rather than a walk of the world's whole establishment.</summary>
    public readonly Dictionary<int, List<SupportFormation>> SupportByNation = new();
    public int NextSupportId = 1;
}

/// <summary>One entry of common/support, in the shape its file states it in.</summary>
public sealed record SupportTypeDefinition(
    string Id,
    string Name,
    SupportKind Kind,
    string Icon,
    float RangeKm,
    float SupportFire,
    float AirSuperiority,
    float Interdiction,
    float AirDefence,
    float Recon,
    float RecruitmentCost,
    int BuildDays,
    int Personnel)
{
    public List<string> Missions { get; init; } = new();
}

/// <summary>Paradox-style common/support catalogue, consumed by simulation and rendering.</summary>
public static class SupportTypeCatalog
{
    private static readonly Dictionary<string, SupportTypeDefinition> Definitions = new();

    static SupportTypeCatalog() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Definitions.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "support"))
            using (document) Merge(document.RootElement, errors);
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var next = Read(property.Name, property.Value);
            if (next is not null) Definitions[property.Name] = next;
        }
    }

    public static SupportTypeDefinition Get(string id) =>
        id is not null && Definitions.TryGetValue(id, out var definition)
            ? definition
            : Definitions.TryGetValue("artillery_brigade", out var fallback) ? fallback : null;

    public static bool TryGet(string id, out SupportTypeDefinition definition)
    {
        definition = null;
        return !string.IsNullOrWhiteSpace(id) && Definitions.TryGetValue(id, out definition);
    }

    public static IEnumerable<SupportTypeDefinition> All => Definitions.Values;

    private static SupportTypeDefinition Read(string id, JsonElement value)
    {
        var name = value.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() : id;
        var kind = value.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String &&
                   Enum.TryParse<SupportKind>(kindElement.GetString(), true, out var parsed)
            ? parsed : SupportKind.Ground;
        var icon = value.TryGetProperty("icon", out var iconElement) && iconElement.ValueKind == JsonValueKind.String
            ? iconElement.GetString() : "artillery";
        float Number(string key, float fallback = 0f) =>
            value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.Number &&
            element.TryGetSingle(out var figure) ? figure : fallback;
        int Whole(string key, int fallback = 0) =>
            value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var figure) ? figure : fallback;
        var missions = new List<string> { "standby" };
        if (value.TryGetProperty("missions", out var missionElement) && missionElement.ValueKind == JsonValueKind.Array)
        {
            missions = missionElement.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.String)
                .Select(entry => entry.GetString())
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (!missions.Contains("standby")) missions.Insert(0, "standby");
        }
        var definition = new SupportTypeDefinition(
            id, name, kind, icon,
            MathF.Max(1f, Number("range_km", 45f)),
            MathF.Max(0f, Number("support_fire")),
            MathF.Max(0f, Number("air_superiority")),
            MathF.Max(0f, Number("interdiction")),
            MathF.Max(0f, Number("air_defence")),
            MathF.Max(0f, Number("recon")),
            MathF.Max(0.05f, Number("recruitment_cost", 0.5f)),
            Math.Max(7, Whole("build_days", 45)),
            Math.Max(100, Whole("personnel", 4500)))
        { Missions = missions };
        return definition;
    }
}

/// <summary>
/// The long-range forces of the world: training, readiness, missions and the reach of
/// all of them into the land war.
///
/// Everything here runs off the campaign instance alone - no statics, no caches - so two
/// campaigns can tick side by side in one process without reading each other's notes,
/// which is the invariant the whole simulation is written under.
/// </summary>
public static class SupportCore
{
    public const string MissionStandby = "standby";
    public const string MissionCloseAirSupport = "close_air_support";
    public const string MissionAirSuperiority = "air_superiority";
    public const string MissionInterdiction = "interdiction";

    /// <summary>
    /// Rebuild the by-nation reach index, once at the top of every step.
    ///
    /// Battles resolve on the first step of a day, BEFORE the day pass that trains and
    /// reassigns the long-range forces - so an index built inside the day pass leaves a
    /// restored client fighting its first battles with an empty one while the host still
    /// holds yesterday's, which is a desync with nothing to say why. Built here instead,
    /// every step sees the same index on both sides of the wire, one step stale at most -
    /// and equally stale, which is the whole requirement.
    /// </summary>
    public static void IndexReach(Campaign campaign)
    {
        campaign.SupportByNation.Clear();
        foreach (var formation in campaign.SupportFormations)
        {
            if (!campaign.SupportByNation.TryGetValue(formation.NationId, out var list))
                campaign.SupportByNation[formation.NationId] = list = new List<SupportFormation>();
            list.Add(formation);
        }
    }

    /// <summary>The daily pass: training completes, readiness recovers, the AI minds its missions.</summary>
    public static void Tick(Campaign campaign)
    {
        Train(campaign);
        Recover(campaign);
        CommandByAir(campaign);
    }

    // ------------------------------------------------------------------ training

    /// <summary>What raising one formation of this type costs this country.</summary>
    public static float CostOf(Nation nation, SupportTypeDefinition type) =>
        type.RecruitmentCost * NationalModifiers.Factor(nation, "recruitment_cost");

    /// <summary>How many days the formation takes to stand up, with the country's own speed.</summary>
    public static int BuildDaysOf(Nation nation, SupportTypeDefinition type) =>
        Math.Max(1, (int)MathF.Round(type.BuildDays / NationalModifiers.Factor(nation, "recruitment_speed")));

    /// <summary>How many formations a region can host: two of anything, and two more per barracks.</summary>
    public static int CapacityOf(Campaign campaign, ScenarioState state)
    {
        if (state is null) return 0;
        return 2 + 2 * state.Buildings.GetValueOrDefault(BuildingKind.Barracks);
    }

    /// <summary>Formations already based in a region, against what the region can host.</summary>
    public static int PostsUsed(Campaign campaign, int stateMapIndex, int exceptFormationId = -1) =>
        campaign.SupportFormations.Count(formation =>
            formation.StateMapIndex == stateMapIndex && formation.Id != exceptFormationId);

    /// <summary>A controlled province of the region, which the range is measured from. -1 if there is none.</summary>
    public static int BaseProvinceOf(Campaign campaign, Nation nation, int stateMapIndex)
    {
        var map = campaign.ProvinceMap;
        if (nation is null || map is null) return -1;
        var controllers = campaign.ProvinceController ?? map.ProvinceToNation;
        var centre = Vector3.Zero;
        var candidates = new List<int>();
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (map.ProvinceToState[province] != stateMapIndex) continue;
            if (controllers[province] != nation.MapIndex) continue;
            candidates.Add(province);
            centre += BaseDirection(map.CenterOf(province));
        }
        if (candidates.Count == 0) return -1;
        var best = -1;
        var bestScore = float.NegativeInfinity;
        foreach (var province in candidates)
        {
            var score = Vector3.Dot(BaseDirection(map.CenterOf(province)), centre);
            if (score <= bestScore) continue;
            best = province;
            bestScore = score;
        }
        return best;
    }

    private static Vector3 BaseDirection((float Latitude, float Longitude) centre)
    {
        var lat = centre.Latitude * (MathF.PI / 180f);
        var lon = centre.Longitude * (MathF.PI / 180f);
        return new Vector3(MathF.Cos(lat) * MathF.Cos(lon), MathF.Sin(lat), MathF.Cos(lat) * MathF.Sin(lon));
    }

    private static void Train(Campaign campaign)
    {
        foreach (var order in campaign.SupportOrders.ToArray())
        {
            if (order.LastProcessedDay >= campaign.ElapsedDays) continue;
            order.LastProcessedDay = campaign.ElapsedDays;
            var nation = campaign.NationById(order.NationId);
            if (nation is null || !SupportTypeCatalog.TryGet(order.TypeId, out var type)) { campaign.SupportOrders.Remove(order); continue; }
            // A base overrun mid-build pauses the order rather than refunding it: the
            // region is the point of the whole purchase, and the war can still turn.
            if (BaseProvinceOf(campaign, nation, order.StateMapIndex) <= 0) continue;
            order.TrainedDays++;
            if (order.TrainedDays < order.DurationDays) continue;
            campaign.SupportOrders.Remove(order);
            var province = BaseProvinceOf(campaign, nation, order.StateMapIndex);
            campaign.SupportFormations.Add(new SupportFormation
            {
                Id = campaign.NextSupportId++,
                NationId = nation.Id,
                TypeId = order.TypeId,
                StateMapIndex = order.StateMapIndex,
                BaseProvinceId = province,
                Strength = 1f,
                Experience = 0.10f,
                Mission = MissionStandby
            });
            campaign.MarkStrategicChange();
            DiagnosticLogs.Write("simulation",
                $"SUPPORT_RAISED order={order.Id} nation={nation.Id} type={order.TypeId} state={order.StateMapIndex}");
        }
    }

    /// <summary>Readiness again, out of the arms on the national market: a wing without planes does not fly.</summary>
    private static void Recover(Campaign campaign)
    {
        foreach (var formation in campaign.SupportFormations)
        {
            var nation = campaign.NationById(formation.NationId);
            if (nation is null) continue;
            var arms = nation.Market.TryGetValue(Commodity.Arms, out var quote) ? quote.Satisfaction : 1f;
            var equipment = 0.50f + 0.50f * M.Clamp01(arms);
            // Standing by repairs faster: a formation working its missions wears and a
            // formation resting does not, and the difference is a decision worth having.
            var resting = formation.Mission == MissionStandby ? 1.6f : 1f;
            formation.Strength = M.Clamp01(formation.Strength +
                campaign.Defines.SupportRegenPerDay * equipment * resting * (formation.Strength < 1f ? 1f : 0f));
            // Experience drifts toward what the formation is doing, slowly, the way it does.
            var target = formation.Mission == MissionStandby ? 0.15f : 0.45f;
            formation.Experience = M.Lerp(formation.Experience, target, 0.002f);
        }
    }

    /// <summary>The countries nobody plays get their missions minded: at war, everything that can
    /// work the front does; at peace, everything rests. Deliberately cheap and periodic - a
    /// full re-plan per day is a budget the war does not need spent on it.</summary>
    private static void CommandByAir(Campaign campaign)
    {
        if (campaign.ElapsedDays % 30 != 0) return;
        var wars = new Dictionary<int, int>();
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            wars[war.AttackerId] = war.Id;
            wars[war.DefenderId] = war.Id;
        }
        foreach (var formation in campaign.SupportFormations)
        {
            var nation = campaign.NationById(formation.NationId);
            if (nation is null || nation.IsHuman) continue;
            if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) continue;
            // Displaced bases fly home: a formation whose ground is gone redeploys to the
            // most populous region the country still holds rather than going silent.
            if (Displaced(campaign, formation))
                Rebase(campaign, formation, HomeStateOf(campaign, formation.NationId));
            var atWar = wars.ContainsKey(formation.NationId);
            formation.Mission = !atWar || type.Kind == SupportKind.AirDefence || formation.BaseProvinceId <= 0
                ? MissionStandby
                : type.Missions.Contains(MissionCloseAirSupport) ? MissionCloseAirSupport
                : type.Missions.Contains(MissionInterdiction) ? MissionInterdiction
                : MissionStandby;
        }
    }

    // ------------------------------------------------------------------ reach

    /// <summary>Great-circle kilometres between two provinces, from their centres.</summary>
    public static float DistanceKm(Campaign campaign, int fromProvince, int toProvince)
    {
        var map = campaign.ProvinceMap;
        if (map is null) return float.MaxValue;
        var from = map.CenterOf(fromProvince);
        var to = map.CenterOf(toProvince);
        var radians = MathF.PI / 180f;
        var dLat = (to.Latitude - from.Latitude) * radians;
        var dLon = (to.Longitude - from.Longitude) * radians;
        var sinLat = MathF.Sin(dLat * 0.5f);
        var sinLon = MathF.Sin(dLon * 0.5f);
        var haversine = sinLat * sinLat +
                        MathF.Cos(from.Latitude * radians) * MathF.Cos(to.Latitude * radians) * sinLon * sinLon;
        return 6371f * 2f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(haversine)));
    }

    /// <summary>A base is displaced when the province it measures from is no longer the country's
    /// own ground. The formation still exists; it projects nothing until it is rebased.</summary>
    public static bool Displaced(Campaign campaign, SupportFormation formation)
    {
        var map = campaign.ProvinceMap;
        if (map is null || formation.BaseProvinceId <= 0) return true;
        var controllers = campaign.ProvinceController ?? map.ProvinceToNation;
        var nation = campaign.NationById(formation.NationId);
        return nation is null || controllers[formation.BaseProvinceId] != nation.MapIndex;
    }

    /// <summary>Whether the formation reaches a province at all: a base that holds, inside the radius.</summary>
    public static bool InRange(Campaign campaign, SupportFormation formation, SupportTypeDefinition type, int province)
    {
        if (type is null || formation.BaseProvinceId <= 0 || province <= 0) return false;
        return DistanceKm(campaign, formation.BaseProvinceId, province) <= type.RangeKm;
    }

    /// <summary>The country's most populous region, for a formation that has lost its own.</summary>
    public static int HomeStateOf(Campaign campaign, int nationId)
    {
        var nation = campaign.NationById(nationId);
        if (nation is null) return -1;
        var best = -1;
        var bestPopulation = -1L;
        foreach (var state in campaign.States)
        {
            if (state.NationMapIndex != nation.MapIndex) continue;
            if (state.Population > bestPopulation) { bestPopulation = state.Population; best = state.MapIndex; }
        }
        return best;
    }

    /// <summary>Move a formation to a region the country holds, onto a province of it.</summary>
    public static string Rebase(Campaign campaign, SupportFormation formation, int stateMapIndex)
    {
        var nation = campaign.NationById(formation.NationId);
        var state = campaign.States.Find(candidate => candidate.MapIndex == stateMapIndex);
        if (nation is null || state is null || state.NationMapIndex != nation.MapIndex)
            return "Choose a region the country holds.";
        if (PostsUsed(campaign, stateMapIndex, formation.Id) >= CapacityOf(campaign, state) &&
            formation.StateMapIndex != stateMapIndex)
            return "That region has no post free.";
        var province = BaseProvinceOf(campaign, nation, stateMapIndex);
        if (province <= 0) return "That region has no controlled province to base in.";
        formation.StateMapIndex = stateMapIndex;
        formation.BaseProvinceId = province;
        campaign.MarkStrategicChange();
        return "";
    }

    // ------------------------------------------------------------------ the reach into a battle

    /// <summary>What the long-range forces of a set of nations do over one province.</summary>
    public sealed class SideReach
    {
        public float Fire;
        public float Interdiction;
        public float AirSuperiority;
        public float AirDefence;
    }

    /// <summary>Sum the formations of these nations that reach the province, by what they are doing.</summary>
    public static SideReach ReachOf(Campaign campaign, IEnumerable<int> nationIds, int province)
    {
        var reach = new SideReach();
        if (campaign.ProvinceMap is null) return reach;
        var nations = nationIds.ToHashSet();
        foreach (var nationId in nations)
        {
            if (!campaign.SupportByNation.TryGetValue(nationId, out var formations)) continue;
            foreach (var formation in formations)
            {
                if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) continue;
                if (Displaced(campaign, formation)) continue;
                // A missile brigade covers its own ground whatever it was told; everything
                // else works only the mission it was given.
                var passive = type.Kind == SupportKind.AirDefence;
                if (!passive && formation.Mission == MissionStandby) continue;
                if (!InRange(campaign, formation, type, province)) continue;
                var weight = formation.Strength * (1f + formation.Experience * 0.35f);
                if (passive)
                    reach.AirDefence += type.AirDefence * weight;
                else
                {
                    if (formation.Mission == MissionCloseAirSupport) reach.Fire += type.SupportFire * weight;
                    if (formation.Mission == MissionInterdiction) reach.Interdiction += type.Interdiction * weight;
                    if (formation.Mission == MissionAirSuperiority) reach.AirSuperiority += type.AirSuperiority * weight;
                }
            }
        }
        return reach;
    }

    /// <summary>
    /// What the battle at a province is worth to each side's long-range forces, as the
    /// multipliers the combat resolution applies. The sums saturate - the first artillery
    /// brigade is a real help and the eleventh is a rumour - and the enemy's sky and
    /// missile brigades take their cut out of whatever the other side's aircraft managed
    /// to contribute in the first place.
    /// </summary>
    public static (float AttackerFire, float DefenderFire, float AttackerInterdict, float DefenderInterdict,
        float AttackerSupport, float DefenderSupport) Engagement(
        Campaign campaign, int province, IReadOnlyCollection<int> attackerNations, IReadOnlyCollection<int> defenderNations)
    {
        if (campaign.SupportByNation.Count == 0 || campaign.ProvinceMap is null)
            return (1f, 1f, 1f, 1f, 0f, 0f);

        // One pass over the ENGAGED nations' formations answers both sides at once: a
        // battle asks this every day it runs, and walking the world's whole establishment
        // for it is a price the front cannot pay twice a day, let alone twice a pass.
        var attacker = new SideReach();
        var defender = new SideReach();
        var attackerSet = attackerNations as HashSet<int> ?? new HashSet<int>(attackerNations);
        var defenderSet = defenderNations as HashSet<int> ?? new HashSet<int>(defenderNations);
        foreach (var (nationId, formations) in campaign.SupportByNation)
        {
            var forAttacker = attackerSet.Contains(nationId);
            var forDefender = defenderSet.Contains(nationId);
            if (!forAttacker && !forDefender) continue;
            foreach (var formation in formations)
            {
                if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) continue;
                if (Displaced(campaign, formation)) continue;
                var passive = type.Kind == SupportKind.AirDefence;
                if (!passive && formation.Mission == MissionStandby) continue;
                if (!InRange(campaign, formation, type, province)) continue;
                var weight = formation.Strength * (1f + formation.Experience * 0.35f);
                var side = forAttacker ? attacker : defender;
                if (passive)
                    side.AirDefence += type.AirDefence * weight;
                else
                {
                    if (formation.Mission == MissionCloseAirSupport) side.Fire += type.SupportFire * weight;
                    if (formation.Mission == MissionInterdiction) side.Interdiction += type.Interdiction * weight;
                    if (formation.Mission == MissionAirSuperiority) side.AirSuperiority += type.AirSuperiority * weight;
                }
            }
        }
        var defines = campaign.Defines;

        float FireBonus(SideReach side, SideReach enemy) =>
            1f + defines.MaxSupportFireBonus * side.Fire / (side.Fire + defines.SupportFireSaturation) *
                SuppressionOf(enemy);
        float SuppressionOf(SideReach enemy) =>
            1f / (1f + defines.AirSuppressionPerPoint * (enemy.AirSuperiority + enemy.AirDefence));
        float InterdictFactor(SideReach side) =>
            1f - defines.MaxSupportInterdiction * side.Interdiction / (side.Interdiction + defines.InterdictionSaturation);

        // The shown figures ride along: the battle report wants raw reach points, and a
        // second pass over the same formations for them is a price the front pays daily.
        return (
            FireBonus(attacker, defender),
            FireBonus(defender, attacker),
            InterdictFactor(defender),
            InterdictFactor(attacker),
            attacker.Fire + attacker.AirSuperiority,
            defender.Fire + defender.AirSuperiority);
    }



    // ------------------------------------------------------------------ the opening world

    /// <summary>
    /// What the world opens with. A modern country's long-range forces are not an
    /// achievement the simulation is asked to grow from nothing: the artillery exists,
    /// the air forces exist, and the campaign starts where the wars do. Scaled by the
    /// size of the economy, deterministic to the digit, spread across regions with
    /// free posts instead of silently overfilling the capital.
    /// </summary>
    public static void EnsureInitial(Campaign campaign)
    {
        if (campaign.SupportFormations.Count > 0) return;
        if (campaign.ProvinceMap is null) return;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController ?? map.ProvinceToNation;
        var bases = new Dictionary<(int Nation, int State), int>();
        var centroids = new Dictionary<(int Nation, int State), Vector3>();
        var scores = new Dictionary<(int Nation, int State), float>();
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            var key = ((int)controllers[province], (int)map.ProvinceToState[province]);
            if (key.Item1 <= 0 || key.Item2 <= 0) continue;
            centroids[key] = centroids.GetValueOrDefault(key) + BaseDirection(map.CenterOf(province));
        }
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            var key = ((int)controllers[province], (int)map.ProvinceToState[province]);
            if (!centroids.TryGetValue(key, out var centre)) continue;
            var score = Vector3.Dot(BaseDirection(map.CenterOf(province)), centre);
            if (bases.ContainsKey(key) && score <= scores[key]) continue;
            bases[key] = province;
            scores[key] = score;
        }
        foreach (var nation in campaign.Nations)
        {
            var states = campaign.States
                .Where(candidate => candidate.NationMapIndex == nation.MapIndex)
                .OrderByDescending(candidate => candidate.Population)
                .ThenBy(candidate => candidate.MapIndex)
                .ToArray();
            if (states.Length == 0) continue;
            IEnumerable<string> types = nation.Gdp >= 1000f
                ? new[] { "fighter_wing", "fighter_wing", "attack_aircraft_wing", "strike_drone_group",
                    "artillery_brigade", "artillery_brigade", "mlrs_battalion", "sam_brigade" }
                : nation.Gdp >= 200f
                ? new[] { "fighter_wing", "strike_drone_group", "artillery_brigade", "mlrs_battalion", "sam_brigade" }
                : nation.Gdp >= 50f
                ? new[] { "artillery_brigade", "recon_drone_group" }
                : nation.Gdp >= 10f
                ? new[] { "artillery_brigade" }
                : Array.Empty<string>();
            foreach (var typeId in types)
            {
                if (!SupportTypeCatalog.TryGet(typeId, out var type)) continue;
                var state = states.FirstOrDefault(candidate =>
                    PostsUsed(campaign, candidate.MapIndex) < CapacityOf(campaign, candidate) &&
                    bases.ContainsKey((nation.MapIndex, candidate.MapIndex)));
                if (state is null) break;
                var province = bases[(nation.MapIndex, state.MapIndex)];
                campaign.SupportFormations.Add(new SupportFormation
                {
                    Id = campaign.NextSupportId++,
                    NationId = nation.Id,
                    TypeId = typeId,
                    StateMapIndex = state.MapIndex,
                    BaseProvinceId = province,
                    Strength = 0.85f + 0.15f * M.Clamp01(nation.Gdp / 500f),
                    Experience = nation.Gdp >= 200f ? 0.35f : 0.20f,
                    Mission = MissionStandby
                });
            }
        }
    }

    /// <summary>Validate a raise order the way recruitment validates a division batch.</summary>
    public static string Validate(Campaign campaign, int issuer, int stateMapIndex, string typeId)
    {
        var nation = campaign.NationById(issuer);
        var state = campaign.States.Find(candidate => candidate.MapIndex == stateMapIndex);
        if (nation is null || campaign.ProvinceMap is null) return "Campaign map is not ready.";
        if (state is null || state.NationMapIndex != nation.MapIndex) return "Choose an owned region to base in.";
        if (!SupportTypeCatalog.TryGet(typeId, out var type)) return "No such formation type.";
        if (BaseProvinceOf(campaign, nation, stateMapIndex) <= 0) return "That region has no controlled province.";
        if (campaign.SupportOrders.Count(order => order.NationId == issuer) >= 8) return "Training capacity reached: 8 orders.";
        if (PostsUsed(campaign, stateMapIndex) + campaign.SupportOrders.Count(order =>
                order.NationId == issuer && order.StateMapIndex == stateMapIndex) >= CapacityOf(campaign, state))
            return "That region has no post free.";
        if (nation.Treasury < CostOf(nation, type)) return "Insufficient treasury.";
        if (nation.ManpowerReserve < type.Personnel) return "Insufficient manpower reserve.";
        return "";
    }
}

// ---------------------------------------------------------------------- the orders a player gives

public sealed class RaiseSupportCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; }
    public string TypeId { get; init; } = "artillery_brigade";
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        Refusal = SupportCore.Validate(campaign, IssuerId, StateMapIndex, TypeId);
        if (Refusal.Length > 0) return;
        var nation = campaign.NationById(IssuerId);
        var type = SupportTypeCatalog.Get(TypeId);
        var days = SupportCore.BuildDaysOf(nation, type);
        var cost = SupportCore.CostOf(nation, type);
        nation.Treasury -= cost;
        nation.ManpowerReserve -= type.Personnel;
        var id = 1;
        foreach (var order in campaign.SupportOrders) id = Math.Max(id, order.Id + 1);
        campaign.SupportOrders.Add(new SupportOrder
        {
            Id = id, NationId = IssuerId, StateMapIndex = StateMapIndex, TypeId = TypeId,
            PaidCost = cost, Personnel = type.Personnel, DurationDays = days,
            LastProcessedDay = campaign.ElapsedDays - 1
        });
        campaign.MarkStrategicChange();
    }
}

public sealed class CancelSupportOrderCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int OrderId { get; init; }

    public void Apply(Campaign campaign)
    {
        var order = campaign.SupportOrders.Find(candidate =>
            candidate.Id == OrderId && candidate.NationId == IssuerId);
        if (order is null) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is not null)
        {
            // The same refund a cancelled division batch gets: what is left of the money,
            // and the men whole - they have not been issued anything yet.
            nation.Treasury += order.PaidCost * 0.75f * (1f - order.TrainedDays / (float)order.DurationDays);
            nation.ManpowerReserve += order.Personnel;
        }
        campaign.SupportOrders.Remove(order);
        campaign.MarkStrategicChange();
    }
}

public sealed class SetSupportMissionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int FormationId { get; init; }
    public string Mission { get; init; } = SupportCore.MissionStandby;
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        var formation = campaign.SupportFormations.Find(candidate =>
            candidate.Id == FormationId && candidate.NationId == IssuerId);
        if (formation is null) { Refusal = "No such formation."; return; }
        if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) return;
        if (!type.Missions.Contains(Mission)) { Refusal = "That formation cannot fly that mission."; return; }
        formation.Mission = Mission;
        campaign.MarkStrategicChange();
    }
}

public sealed class RebaseSupportCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int FormationId { get; init; }
    public int StateMapIndex { get; init; }
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        var formation = campaign.SupportFormations.Find(candidate =>
            candidate.Id == FormationId && candidate.NationId == IssuerId);
        if (formation is null) { Refusal = "No such formation."; return; }
        Refusal = SupportCore.Rebase(campaign, formation, StateMapIndex);
    }
}
