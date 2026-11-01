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

    /// <summary>
    /// What a formation on the "strike" mission aims at (LongRangeStrikes): "front" for the enemy's
    /// formations in the line, "logistics" for the depots and railheads that feed them, "industry"
    /// for the plants that make their shells, "energy" for the grid their cities run on. Empty
    /// leaves the choice to the formation's own staff, which picks by what it fires - glide bombs
    /// and rockets at the line, missiles at the depots, long-range drones at the grid. Empty in
    /// every save written before strikes existed, which is exactly the automatic choice.
    /// </summary>
    public string StrikeTarget = "";

    /// <summary>
    /// The region the player named as the target, or -1 to let the staff find the best one of
    /// the chosen kind inside the reach. A region that has fallen out of reach or out of enemy
    /// hands is not struck; the staff picks again and the report says why.
    /// </summary>
    public int TargetStateMapIndex = -1;
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
    /// <summary>What each formation flew into today, by id: the worst sky over any battle it
    /// worked. Filled by the battles, spent by the day pass, which runs later in the same step -
    /// so it never has to survive a save.</summary>
    public readonly Dictionary<int, float> SupportExposure = new();
    /// <summary>What the long-range forces lost yesterday, for the page that shows them.</summary>
    public readonly Dictionary<int, float> SupportLossesToday = new();
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
    public string RequiredFeature { get; init; } = "";

    /// <summary>Deep-strike power of one weekly salvo at full strength (LongRangeStrikes). Zero: cannot strike.</summary>
    public float Strike { get; init; }

    /// <summary>
    /// The depot good one salvo is fired from - "Missiles", "Drones" or "Ammunition" - and how
    /// much of it a full-strength salvo takes. A formation whose depot is empty does not fire:
    /// the strike war is a war of factories as much as of launchers.
    /// </summary>
    public string Munition { get; init; } = "";
    public float MunitionPerSalvo { get; init; }

    /// <summary>
    /// 0 to 1: the share of the enemy's interception the weapon shrugs off. A ballistic missile
    /// is hard to stop, a cruise missile less so, a slow one-way drone is the easiest of all -
    /// which is why it is thrown in swarms, and why a swarm saturates a screen a few cannot.
    /// </summary>
    public float Evasion { get; init; }
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
            // Key by key, like every other catalogue: a mod that wants longer-legged rocket
            // artillery writes { "mlrs_battalion": { "range_km": 120 } } and keeps the rest.
            // Replacing the whole entry made that one line a rocket battery with no fire.
            Definitions.TryGetValue(property.Name, out var earlier);
            var next = Read(property.Name, property.Value, earlier);
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

    private static SupportTypeDefinition Read(string id, JsonElement value, SupportTypeDefinition earlier = null)
    {
        var name = value.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() : earlier?.Name ?? id;
        var kind = value.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String &&
                   Enum.TryParse<SupportKind>(kindElement.GetString().Replace("_", "").Replace("-", ""), true, out var parsed)
            ? parsed : earlier?.Kind ?? SupportKind.Ground;
        var icon = value.TryGetProperty("icon", out var iconElement) && iconElement.ValueKind == JsonValueKind.String
            ? iconElement.GetString() : earlier?.Icon ?? "artillery";
        float Number(string key, float fallback = 0f) =>
            value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.Number &&
            element.TryGetSingle(out var figure) ? figure : fallback;
        int Whole(string key, int fallback = 0) =>
            value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var figure) ? figure : fallback;
        var missions = earlier is not null ? new List<string>(earlier.Missions) : new List<string> { "standby" };
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
            MathF.Max(1f, Number("range_km", earlier?.RangeKm ?? 45f)),
            MathF.Max(0f, Number("support_fire", earlier?.SupportFire ?? 0f)),
            MathF.Max(0f, Number("air_superiority", earlier?.AirSuperiority ?? 0f)),
            MathF.Max(0f, Number("interdiction", earlier?.Interdiction ?? 0f)),
            MathF.Max(0f, Number("air_defence", earlier?.AirDefence ?? 0f)),
            MathF.Max(0f, Number("recon", earlier?.Recon ?? 0f)),
            MathF.Max(0.05f, Number("recruitment_cost", earlier?.RecruitmentCost ?? 0.5f)),
            Math.Max(7, Whole("build_days", earlier?.BuildDays ?? 45)),
            Math.Max(100, Whole("personnel", earlier?.Personnel ?? 4500)))
        {
            Missions = missions,
            RequiredFeature = value.TryGetProperty("required_feature", out var feature) &&
                feature.ValueKind == JsonValueKind.String ? feature.GetString() : earlier?.RequiredFeature ?? "",
            Strike = MathF.Max(0f, Number("strike", earlier?.Strike ?? 0f)),
            Munition = value.TryGetProperty("munition", out var munition) && munition.ValueKind == JsonValueKind.String
                ? munition.GetString() ?? "" : earlier?.Munition ?? "",
            MunitionPerSalvo = MathF.Max(0f, Number("munition_per_salvo", earlier?.MunitionPerSalvo ?? 0f)),
            Evasion = M.Clamp01(Number("evasion", earlier?.Evasion ?? 0f))
        };
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
    /// <summary>Deep strike: a weekly salvo at a chosen target behind the line (LongRangeStrikes).</summary>
    public const string MissionStrike = "strike";

    public static bool Enabled(Campaign campaign, SupportTypeDefinition type) => type is not null &&
        campaign is not null && campaign.FeatureEnabled("long_range_forces") &&
        campaign.FeatureEnabled(MechanicFeatures.Of(type.Kind)) &&
        (string.IsNullOrEmpty(type.RequiredFeature) || campaign.FeatureEnabled(type.RequiredFeature));

    public static bool Enabled(Campaign campaign, string typeId) =>
        SupportTypeCatalog.TryGet(typeId, out var type) && Enabled(campaign, type);

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
        foreach (var list in campaign.SupportByNation.Values) list.Clear();
        foreach (var formation in campaign.SupportFormations)
        {
            if (!Enabled(campaign, formation.TypeId)) continue;
            if (!campaign.SupportByNation.TryGetValue(formation.NationId, out var list))
                campaign.SupportByNation[formation.NationId] = list = new List<SupportFormation>(8);
            list.Add(formation);
        }
    }

    /// <summary>The daily pass: training completes, readiness recovers, the AI minds its missions.</summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(SupportCore))) return;
        Train(campaign);
        AirReconnaissance.Expose(campaign);
        // The salvos go before the day's losses are counted: an aircraft that flew into a
        // defended sky to launch is charged for it the same evening.
        LongRangeStrikes.Fire(campaign);
        Attrition(campaign);
        Recover(campaign);
        CommandByAir(campaign);
        LongRangeStrikes.Procure(campaign);
    }

    // ------------------------------------------------------------------ the air war

    /// <summary>
    /// What a day over the front cost. Every formation that worked a battle has the worst sky
    /// it met there on its account - the enemy's fighters and missile brigades for anything
    /// that flies, their guns and strikes for the guns - and pays a share of its strength for
    /// it, saturating, so a wing thrown at a defended front melts over weeks and a wing the
    /// sky has been cleared for does not. It is what makes air superiority worth flying: the
    /// fighters overhead are what the attack aircraft are not losing.
    /// </summary>
    private static void Attrition(Campaign campaign)
    {
        campaign.SupportLossesToday.Clear();
        if (campaign.SupportExposure.Count == 0) return;
        var defines = campaign.Defines;
        foreach (var formation in campaign.SupportFormations)
        {
            if (!Enabled(campaign, formation.TypeId) || !campaign.SupportExposure.TryGetValue(formation.Id, out var threat) || threat <= 0f) continue;
            var loss = defines.SupportAttritionPerDay * threat / (threat + defines.SupportAttritionSaturation) *
                       NationalModifiers.Factor(campaign.NationById(formation.NationId), "support_losses");
            loss = MathF.Min(loss, MathF.Max(0f, formation.Strength - defines.SupportAttritionFloor));
            if (loss <= 0f) continue;
            formation.Strength -= loss;
            campaign.SupportLossesToday[formation.Id] = loss;
            // Crews that come back have learned something.
            formation.Experience = M.Clamp01(formation.Experience + loss * 0.08f);
        }
        campaign.SupportExposure.Clear();
    }

    /// <summary>
    /// The sky one formation meets over a battle: for aircraft and drones the enemy's fighters and
    /// missile brigades, thinned by its own side's fighters; for guns the enemy's counter-battery
    /// fire and strikes; for a missile brigade, the strikes flown at it.
    /// </summary>
    internal static float ThreatTo(SupportTypeDefinition type, string mission, SideReach own, SideReach enemy)
    {
        switch (type.Kind)
        {
            case SupportKind.Air:
            case SupportKind.Drone:
                var sky = enemy.AirSuperiority + enemy.AirDefence * 1.3f;
                // A fighter on a sweep is the one doing the thinning; everyone else is covered by it.
                var cover = mission == MissionAirSuperiority ? 0f : own.AirSuperiority;
                var fragile = type.Kind == SupportKind.Drone ? 1.35f : 1f;
                return sky * fragile / (1f + 0.04f * cover);
            case SupportKind.AirDefence:
                return enemy.Interdiction * 0.6f;
            default:
                return enemy.Fire * 0.35f + enemy.Interdiction * 0.5f;
        }
    }

    // ------------------------------------------------------------------ training

    /// <summary>What raising one formation of this type costs this country.</summary>
    public static float CostOf(Nation nation, SupportTypeDefinition type) =>
        type.RecruitmentCost * NationalModifiers.Factor(nation, "recruitment_cost");

    /// <summary>How many days the formation takes to stand up, with the country's own speed.</summary>
    public static int BuildDaysOf(Nation nation, SupportTypeDefinition type) =>
        Math.Max(1, (int)MathF.Round(type.BuildDays / NationalModifiers.Factor(nation, "recruitment_speed")));

    /// <summary>Support formations each barracks lets its region host.</summary>
    public const int PostsPerBarracks = 2;

    /// <summary>How many formations a region can host: two of anything, and two more per barracks.</summary>
    public static int CapacityOf(Campaign campaign, ScenarioState state)
    {
        if (state is null) return 0;
        return 2 + PostsPerBarracks * state.Buildings.GetValueOrDefault(BuildingKind.Barracks);
    }

    /// <summary>Formations already based in a region, against what the region can host.</summary>
    public static int PostsUsed(Campaign campaign, int stateMapIndex, int exceptFormationId = -1) =>
        campaign.SupportFormations.Count(formation =>
            formation.StateMapIndex == stateMapIndex && formation.Id != exceptFormationId && Enabled(campaign, formation.TypeId));

    /// <summary>A controlled province of the region, which the range is measured from. -1 if there is none.</summary>
    public static int BaseProvinceOf(Campaign campaign, Nation nation, int stateMapIndex)
    {
        var map = campaign.ProvinceMap;
        if (nation is null || map is null) return -1;
        var controllers = campaign.ProvinceController ?? map.ProvinceToNation;
        var candidates = new List<int>();
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (map.ProvinceToState[province] != stateMapIndex) continue;
            if (controllers[province] != nation.MapIndex) continue;
            candidates.Add(province);
        }
        return ChooseBase(map, candidates);
    }

    /// <summary>
    /// Where a region's wings and batteries stand: on the region's main ground - the largest
    /// run of its held provinces that touch one another - at the leading city there, or at
    /// the middle of that ground when it has no city. The province nearest the centroid of
    /// all of them used to be chosen, and a region with islands off its coast has a centroid
    /// at sea: Gyeonggi's air wings stood on Yeonpyeong, a dot off the North Korean coast,
    /// and drew their reach from there.
    /// </summary>
    public static int ChooseBase(ProvinceTopology map, IReadOnlyList<int> candidates)
    {
        if (map is null || candidates is null || candidates.Count == 0) return -1;
        if (candidates.Count == 1) return candidates[0];
        var held = new HashSet<int>(candidates);
        var ground = new List<int>();
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (var start in candidates)
        {
            if (!seen.Add(start)) continue;
            var run = new List<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var province = queue.Dequeue();
                run.Add(province);
                foreach (var next in map.Neighbors[province])
                    if (held.Contains(next) && seen.Add(next)) queue.Enqueue(next);
            }
            if (run.Count > ground.Count) ground = run;
        }
        var cities = CityCatalog.All;
        var under = CityCatalog.ProvincesOf(map);
        var onGround = new HashSet<int>(ground);
        for (var index = 0; index < cities.Count && index < under.Length; index++)
            if (onGround.Contains(under[index]) && cities[index].Tier <= 2) return under[index];
        var centre = Vector3.Zero;
        foreach (var province in ground) centre += BaseDirection(map.CenterOf(province));
        var best = -1;
        var bestScore = float.NegativeInfinity;
        foreach (var province in ground)
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
            if (!Enabled(campaign, order.TypeId)) { order.LastProcessedDay = campaign.ElapsedDays; continue; }
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
            if (nation is null || !Enabled(campaign, formation.TypeId)) continue;
            var arms = nation.Market.TryGetValue(Commodity.Arms, out var quote) ? quote.Satisfaction : 1f;
            var equipment = 0.50f + 0.50f * M.Clamp01(arms);
            // Standing by repairs faster: a formation working its missions wears and a
            // formation resting does not, and the difference is a decision worth having.
            var resting = formation.Mission == MissionStandby ? 1.6f : 1f;
            formation.Strength = M.Clamp01(formation.Strength +
                campaign.Defines.SupportRegenPerDay * equipment * resting * NationalModifiers.Factor(nation, "support_recovery") *
                (formation.Strength < 1f ? 1f : 0f));
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
            if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type) || !Enabled(campaign, type)) continue;
            // Displaced bases fly home: a formation whose ground is gone redeploys to the
            // most populous region the country still holds rather than going silent.
            if (Displaced(campaign, formation))
                Rebase(campaign, formation, HomeStateOf(campaign, formation.NationId));
            var atWar = wars.ContainsKey(formation.NationId);
            // A wing worn down over the front is pulled out to recover, and goes back when it has
            // - with room between the two, so it does not flicker in and out every month.
            var resting = formation.Mission == MissionStandby;
            var fit = formation.Strength >= (resting ? 0.75f : 0.45f);
            formation.Mission = !atWar || !fit || type.Kind == SupportKind.AirDefence || formation.BaseProvinceId <= 0
                ? MissionStandby
                : RoleOf(campaign, nation, type);
        }
    }

    /// <summary>
    /// What a staff nobody plays does with a formation at war, by what the formation is FOR rather
    /// than by the first mission on its card - the old rule sent fighters to bomb trenches and the
    /// reconnaissance drones to shoot at them, and never fired a missile at anything.
    ///
    ///   - the eyes look: a formation built for reconnaissance flies it, because it is what makes
    ///     every strike behind it land;
    ///   - fighters sweep the sky when the enemy has anything in it, and support the battles when not;
    ///   - a weapon built to strike deep (its strike outweighs its fire) strikes, as long as the
    ///     depot holds two salvos of what it fires - an empty depot is a reason to wait, not to
    ///     fire nothing; the target is the staff's automatic choice (LongRangeStrikes.AutoTarget);
    ///   - everything else supports the battles in reach, or interdicts when it cannot.
    /// The same rule a player's "Auto" target follows, so neither side is given a better staff.
    /// </summary>
    internal static string RoleOf(Campaign campaign, Nation nation, SupportTypeDefinition type)
    {
        if (type.Missions.Contains(AirReconnaissance.Mission) &&
            type.Recon >= campaign.Defines.ReconFullSight * 0.66f)
            return AirReconnaissance.Mission;
        if (type.Kind == SupportKind.Air && type.Missions.Contains(MissionAirSuperiority) &&
            type.AirSuperiority >= 2f * type.SupportFire)
            return LongRangeStrikes.EnemyFlies(campaign, nation.Id) ? MissionAirSuperiority
                : type.Missions.Contains(MissionCloseAirSupport) ? MissionCloseAirSupport : MissionAirSuperiority;
        if (type.Missions.Contains(MissionStrike) && type.Strike > type.SupportFire)
            return LongRangeStrikes.DepotHolds(campaign, nation, type, 2f) ? MissionStrike : MissionStandby;
        return type.Missions.Contains(MissionCloseAirSupport) ? MissionCloseAirSupport
            : type.Missions.Contains(MissionInterdiction) ? MissionInterdiction
            : MissionStandby;
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
        if (map is null || formation.BaseProvinceId <= 0 || formation.BaseProvinceId > map.ProvinceCount) return true;
        var controllers = campaign.ProvinceController ?? map.ProvinceToNation;
        var nation = campaign.NationById(formation.NationId);
        return nation is null || controllers[formation.BaseProvinceId] != nation.MapIndex;
    }

    /// <summary>Whether the formation reaches a province at all: a base that holds, inside the radius.</summary>
    public static bool InRange(Campaign campaign, SupportFormation formation, SupportTypeDefinition type, int province)
    {
        if (!Enabled(campaign, type) || formation.BaseProvinceId <= 0 || province <= 0) return false;
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
        if (formation is null) return "No such formation.";
        var plan = PreviewRebase(campaign, formation.NationId, new[] { formation.Id }, stateMapIndex);
        if (plan.Refusal.Length > 0) return plan.Refusal;
        formation.StateMapIndex = stateMapIndex;
        formation.BaseProvinceId = plan.BaseProvinceId;
        campaign.MarkStrategicChange();
        return "";
    }

    /// <summary>Read-only, shared by map previews and commands. Training reserves real posts.
    /// A group is accepted whole or refused whole; stale/foreign ids never silently disappear.</summary>
    public sealed record RebasePreview(int BaseProvinceId, int Moving, int Free, int Reserved, string Refusal);

    public static RebasePreview PreviewRebase(Campaign campaign, int issuer, IEnumerable<int> formationIds, int stateMapIndex)
    {
        var ids = formationIds?.Distinct().OrderBy(id => id).Take(4097).ToArray() ?? Array.Empty<int>();
        var nation = campaign.NationById(issuer);
        var state = campaign.States.Find(s => s.MapIndex == stateMapIndex);
        RebasePreview Reject(string reason) => new(-1, 0, 0, 0, reason);
        if (ids.Length == 0 || ids.Length > 4096) return Reject("No such formation.");
        var formations = ids.Select(id => campaign.SupportFormations.Find(f => f.Id == id && f.NationId == issuer)).ToArray();
        if (formations.Any(f => f is null)) return Reject("No such formation.");
        if (formations.Any(f => !Enabled(campaign, f.TypeId))) return Reject("This mechanic is disabled in this scenario.");
        if (nation is null || state is null || state.NationMapIndex != nation.MapIndex)
            return Reject("Choose a region the country holds.");
        var province = BaseProvinceOf(campaign, nation, stateMapIndex);
        if (province <= 0) return Reject("That region has no controlled province to base in.");
        var reserved = campaign.SupportOrders.Count(o => o.StateMapIndex == stateMapIndex && Enabled(campaign, o.TypeId));
        var free = Math.Max(0, CapacityOf(campaign, state) - PostsUsed(campaign, stateMapIndex) - reserved);
        var moving = formations.Count(f => f.StateMapIndex != stateMapIndex);
        return new(province, moving, free, reserved, moving > free ? "Not enough posts for the whole selection." : "");
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
        var present = new List<(SupportFormation Formation, SupportTypeDefinition Type, bool Attacker)>();
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
                // A formation on deep strike is aiming at something else this week: it neither
                // helps the battle nor is charged for its sky (LongRangeStrikes charges its own).
                if (!passive && formation.Mission == MissionStrike) continue;
                if (!InRange(campaign, formation, type, province)) continue;
                var weight = formation.Strength * (1f + formation.Experience * 0.35f);
                var side = forAttacker ? attacker : defender;
                present.Add((formation, type, forAttacker));
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

        // The price of being there: each formation is charged the worst sky it met today.
        foreach (var (formation, type, onAttack) in present)
        {
            var threat = ThreatTo(type, formation.Mission, onAttack ? attacker : defender, onAttack ? defender : attacker);
            if (threat <= 0f) continue;
            if (!campaign.SupportExposure.TryGetValue(formation.Id, out var worst) || threat > worst)
                campaign.SupportExposure[formation.Id] = threat;
        }

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
        var held = new Dictionary<(int Nation, int State), List<int>>();
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            var key = ((int)controllers[province], (int)map.ProvinceToState[province]);
            if (key.Item1 <= 0 || key.Item2 <= 0) continue;
            if (!held.TryGetValue(key, out var list)) held[key] = list = new List<int>();
            list.Add(province);
        }
        var bases = new Dictionary<(int Nation, int State), int>();
        foreach (var (key, list) in held)
            if (ChooseBase(map, list) is var chosen and > 0) bases[key] = chosen;
        foreach (var nation in campaign.Nations)
        {
            var states = campaign.States
                .Where(candidate => candidate.NationMapIndex == nation.MapIndex)
                .OrderByDescending(candidate => candidate.Population)
                .ThenBy(candidate => candidate.MapIndex)
                .ToArray();
            if (states.Length == 0) continue;
            // The deep-strike arm of 2024 is part of the opening world too: every large military
            // fielded cruise or ballistic missiles, and the middle powers one-way drones.
            IEnumerable<string> types = nation.Gdp >= 1000f
                ? new[] { "fighter_wing", "fighter_wing", "attack_aircraft_wing", "strike_drone_group",
                    "artillery_brigade", "artillery_brigade", "mlrs_battalion", "sam_brigade", "strike_missile_brigade" }
                : nation.Gdp >= 200f
                ? new[] { "fighter_wing", "strike_drone_group", "artillery_brigade", "mlrs_battalion", "sam_brigade",
                    "long_range_drone_group" }
                : nation.Gdp >= 50f
                ? new[] { "artillery_brigade", "recon_drone_group" }
                : nation.Gdp >= 10f
                ? new[] { "artillery_brigade" }
                : Array.Empty<string>();
            foreach (var typeId in types)
            {
                if (!SupportTypeCatalog.TryGet(typeId, out var type) || !Enabled(campaign, type)) continue;
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
        if (!Enabled(campaign, type)) return "This mechanic is disabled in this scenario.";
        if (BaseProvinceOf(campaign, nation, stateMapIndex) <= 0) return "That region has no controlled province.";
        if (campaign.SupportOrders.Count(order => order.NationId == issuer && Enabled(campaign, order.TypeId)) >= 8) return "Training capacity reached: 8 orders.";
        if (PostsUsed(campaign, stateMapIndex) + campaign.SupportOrders.Count(order =>
                order.NationId == issuer && order.StateMapIndex == stateMapIndex && Enabled(campaign, order.TypeId)) >= CapacityOf(campaign, state))
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
        if (!MechanicFeatures.Allows(campaign, this)) return;
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
        if (!MechanicFeatures.Allows(campaign, this)) return;
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
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var formation = campaign.SupportFormations.Find(candidate =>
            candidate.Id == FormationId && candidate.NationId == IssuerId);
        if (formation is null) { Refusal = "No such formation."; return; }
        if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) return;
        if (!SupportCore.Enabled(campaign, type)) { Refusal = "This mechanic is disabled in this scenario."; return; }
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
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var formation = campaign.SupportFormations.Find(candidate =>
            candidate.Id == FormationId && candidate.NationId == IssuerId);
        if (formation is null) { Refusal = "No such formation."; return; }
        Refusal = SupportCore.Rebase(campaign, formation, StateMapIndex);
    }
}

/// <summary>One replay/network order, with authoritative all-or-nothing validation.</summary>
public sealed class RebaseSupportGroupCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int[] FormationIds { get; init; } = Array.Empty<int>();
    public int StateMapIndex { get; init; }
    public string Refusal { get; private set; } = "";
    public int Moved { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        Moved = 0;
        var plan = SupportCore.PreviewRebase(campaign, IssuerId, FormationIds, StateMapIndex);
        Refusal = plan.Refusal;
        if (Refusal.Length > 0) return;
        foreach (var id in FormationIds.Distinct().OrderBy(id => id))
        {
            var formation = campaign.SupportFormations.Find(f => f.Id == id && f.NationId == IssuerId);
            if (formation.StateMapIndex == StateMapIndex && formation.BaseProvinceId == plan.BaseProvinceId) continue;
            formation.StateMapIndex = StateMapIndex;
            formation.BaseProvinceId = plan.BaseProvinceId;
            Moved++;
        }
        if (Moved > 0) campaign.MarkStrategicChange();
    }
}
