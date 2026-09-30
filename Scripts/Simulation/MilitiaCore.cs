using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// People who take up arms without being told to.
///
/// Every formation in the game until now existed because a government paid for it and
/// waited sixty days. That is most of how armies are made and none of how countries are
/// actually defended: the militia, the home guard, the territorial battalion and the men
/// who simply did not leave are a real and decisive part of why an invasion of a country
/// costs more than an invasion of a map.
///
/// What makes it worth simulating rather than granting is that it is not free and it is not
/// universal. A region raises militia in proportion to how far it thinks the state is
/// *theirs*: a region of the country's own people that lives decently and still believes in
/// the war fills a formation in weeks, and a region held down by a state it does not
/// recognise fills none at all however many people live in it. The same number that makes
/// a region rise for its government is the one that will make it rise against it.
///
/// Everything about it is in `common/militia`.
/// </summary>
public sealed class MilitiaWeights
{
    public float Base = 0.020f;
    public float PopulationPerPoint = 4_000_000f;
    public float Nationality = 1.60f;

    public float WarSupportFloor = 0.25f;
    public float WarSupport = 0.75f;

    public float StabilityFloor = 0.45f;
    public float Stability = 0.55f;

    public float ThreatFloor = 0.35f;
    public float Threat = 0.65f;

    public int PerRegion = 3;
    public int PerCountry = 40;

    public float Formation = 9_000f;
    public float PopulationPerFormation = 250_000f;
    public int StandDownDays = 30;
}

public static class MilitiaCore
{
    public const string TemplateId = "militia";

    public static MilitiaWeights Weights { get; private set; } = new();

    static MilitiaCore() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Weights = new MilitiaWeights();
        foreach (var document in GameDataLoader.DataDocuments("common", "militia"))
            using (document)
                Merge(document.RootElement, errors);
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Number) continue;
            var value = entry.Value.GetSingle();
            switch (entry.Name.ToLowerInvariant())
            {
                case "base": Weights.Base = value; break;
                case "population_per_point": Weights.PopulationPerPoint = MathF.Max(1f, value); break;
                case "nationality": Weights.Nationality = value; break;
                case "war_support_floor": Weights.WarSupportFloor = value; break;
                case "war_support": Weights.WarSupport = value; break;
                case "stability_floor": Weights.StabilityFloor = value; break;
                case "stability": Weights.Stability = value; break;
                case "threat_floor": Weights.ThreatFloor = value; break;
                case "threat": Weights.Threat = value; break;
                case "per_region": Weights.PerRegion = (int)MathF.Round(value); break;
                case "per_country": Weights.PerCountry = (int)MathF.Round(value); break;
                case "formation": Weights.Formation = MathF.Max(1f, value); break;
                case "population_per_formation":
                    Weights.PopulationPerFormation = MathF.Max(1f, value);
                    break;
                case "stand_down_days": Weights.StandDownDays = (int)MathF.Round(value); break;
                default: errors?.Add($"common/militia: '{entry.Name}' is not something this game reads."); break;
            }
        }
    }

    /// <summary>
    /// How much of a region is either held by somebody else or next door to ground that is.
    /// Being surrounded is enough; the enemy does not have to have arrived.
    /// </summary>
    public static float Threat(Campaign campaign, ScenarioState state, Nation owner)
    {
        // The world tally already knows who is hostile to whom, so the enemy list this used
        // to build for one country is redundant here.
        if (campaign is null || state is null || owner is null) return 0f;
        return Threat(ControlTally.World(campaign, owner.MapIndex, state.MapIndex));
    }

    /// <summary>The same, against a tally already taken. Used inside the day.</summary>
    private static float Threat(RegionControl control)
    {
        if (control.Provinces == 0) return 0f;
        var weights = Weights;
        // A border is not a fraction. A region with one province facing an enemy army knows
        // the enemy is there; counting the share of its provinces that do made a front-line
        // oblast read as a tenth threatened and nobody rose in two hundred days.
        var near = control.Bordered > 0 ? weights.ThreatFloor : 0f;
        return M.Clamp01(control.OccupiedShare * (weights.ThreatFloor + weights.Threat) + near);
    }

    /// <summary>
    /// Every flag this country is at war with. Worked out once and handed down, because the
    /// alternative is a list lookup per province per neighbour per day.
    /// </summary>
    private static void CollectEnemies(Campaign campaign, Nation nation, HashSet<ushort> into)
    {
        if (campaign is null || nation is null) return;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(nation.Id)) continue;
            foreach (var other in campaign.Nations)
                if (other.Id != nation.Id && war.Involves(other.Id)) into.Add((ushort)other.MapIndex);
        }
    }

    // Scratch, cleared on the way in. Safe where the readiness was not: nothing is carried
    // from one call to the next, so two worlds in one process cannot see each other here.
    private static readonly HashSet<ushort> Enemies = new();
    private static readonly Dictionary<int, int> Standing = new();
    private static readonly Dictionary<long, int> Here = new();

    /// <summary>
    /// A day of it: who is rising, who has had enough, and who is going home.
    ///
    /// Loops and scratch rather than LINQ and fresh collections. This runs once a day for
    /// 261 countries against every army in the world, and a campaign day is held to 300 kB
    /// of garbage - the first draft of this spent 15 of them on counting.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null) return;

        var fighting = false;
        foreach (var war in campaign.Wars)
            if (!war.Ended) { fighting = true; break; }

        StandDown(campaign, fighting);
        if (!fighting) return;

        var weights = Weights;
        Standing.Clear();
        Here.Clear();
        foreach (var army in campaign.Armies)
        {
            if (army.TemplateId != TemplateId) continue;
            Standing[army.NationId] = Standing.GetValueOrDefault(army.NationId) + 1;
            var key = ((long)army.NationId << 32) | (uint)army.StateMapIndex;
            Here[key] = Here.GetValueOrDefault(key) + 1;
        }

        foreach (var nation in campaign.Nations)
        {
            Enemies.Clear();
            CollectEnemies(campaign, nation, Enemies);
            if (Enemies.Count == 0)
            {
                if (nation.MilitiaReadiness.Count > 0) nation.MilitiaReadiness.Clear();
                continue;
            }

            var raised = Standing.GetValueOrDefault(nation.Id);
            if (raised >= weights.PerCountry) continue;
            // What the government and its beliefs are worth, and what it has prepared: a country
            // that registered and armed its volunteers before the war raises them in days.
            var politics = PoliticsMultiplier(nation) * NationalModifiers.Factor(nation, "militia_readiness");
            if (politics <= 0f) continue;
            foreach (var state in campaign.StatesOf(nation.MapIndex))
            {
                // A village does not field a division, however frightened it is.
                if (state.Population < weights.PopulationPerFormation) continue;
                var threat = Threat(ControlTally.World(campaign, nation.MapIndex, state.MapIndex));
                if (threat <= 0.0001f) { nation.MilitiaReadiness.Remove(state.MapIndex); continue; }
                var key = ((long)nation.Id << 32) | (uint)state.MapIndex;
                if (Here.GetValueOrDefault(key) >= weights.PerRegion) continue;

                var own = 1f - EconomySystem.ForeignShare(state, nation.Tag);
                var willing = MathF.Pow(M.Clamp01(own), MathF.Max(0.01f, weights.Nationality));
                var gained = weights.Base
                             * willing
                             * (weights.WarSupportFloor + weights.WarSupport * M.Clamp01(nation.WarSupport))
                             * (weights.StabilityFloor + weights.Stability * M.Clamp01(state.Stability))
                             * threat
                             * (state.Population / weights.PopulationPerPoint)
                             * politics;
                if (gained <= 0f) continue;

                var readiness = nation.MilitiaReadiness.GetValueOrDefault(state.MapIndex) + gained;
                if (readiness < 1f) { nation.MilitiaReadiness[state.MapIndex] = readiness; continue; }
                if (!Raise(campaign, nation, state))
                {
                    nation.MilitiaReadiness[state.MapIndex] = 1f;
                    continue;
                }
                nation.MilitiaReadiness[state.MapIndex] = readiness - 1f;
                Here[key] = Here.GetValueOrDefault(key) + 1;
                if (++raised >= weights.PerCountry) break;
            }
        }
    }

    /// <summary>
    /// What the government and what it believes are worth to a population deciding whether
    /// to fetch a rifle. Both are data; a catalogue that says nothing is worth exactly one.
    /// </summary>
    private static float PoliticsMultiplier(Nation nation) =>
        MathF.Max(0f, PoliticsCore.GovernmentOf(nation).Militia * PoliticsCore.IdeologyOf(nation).Militia);

    /// <summary>
    /// Stand a formation up outside the daily pass, for a country that has just declared
    /// itself. <see cref="SecessionCore"/> is the only caller: a state born this morning has
    /// exactly the condition the daily rules read, and the weeks they would take to answer
    /// are the weeks it may not be given.
    /// </summary>
    public static bool Found(Campaign campaign, Nation nation, ScenarioState state) =>
        campaign?.ProvinceMap is not null && nation is not null && state is not null &&
        Raise(campaign, nation, state);

    /// <summary>
    /// Stand one formation up on ground the country still holds.
    ///
    /// It costs no manpower. That is the point of it: these are not reservists the ministry
    /// called up, they are the people who live there, and taking them out of the pool the
    /// regular army reinforces from produced the exact opposite of the intended effect -
    /// a country that raised militia and then could not replace its line divisions.
    /// </summary>
    private static bool Raise(Campaign campaign, Nation nation, ScenarioState state)
    {
        var topology = campaign.ProvinceMap;
        var control = campaign.ProvinceController;
        var flag = (ushort)nation.MapIndex;

        var (province, town) = Seat(topology, control, flag, (ushort)state.MapIndex);
        // A region entirely under the enemy raises nobody: the people are there, the ground
        // to form up on is not. That is what a partisan rule would be for, and it is not
        // this one.
        if (province <= 0) return false;

        var men = (int)Weights.Formation;
        var position = topology.CenterOf(province);
        // Named for the town it stood up in - "1. Kharkiv Militia" - because that is what it
        // is; a region without a town on the map lends its own name.
        var place = town?.Name ?? state.Name;
        var suffix = " " + place + " Militia";
        var id = 0;
        var serial = 0;
        foreach (var army in campaign.Armies)
        {
            if (army.Id > id) id = army.Id;
            if (army.NationId == nation.Id && army.TemplateId == TemplateId &&
                army.Name.EndsWith(suffix, StringComparison.Ordinal)) serial++;
        }

        campaign.Armies.Add(new Army
        {
            Id = id + 1,
            NationId = nation.Id,
            StateMapIndex = state.MapIndex,
            ProvinceId = province,
            Name = $"{serial + 1}.{suffix}",
            TemplateId = TemplateId,
            Divisions = 1,
            Personnel = men,
            MaxPersonnel = men,
            // It is not a trained formation and does not pretend to be. What it has is that
            // it is already where it is needed and it is not going anywhere.
            Organisation = 0.45f,
            Strength = 1,
            Equipment = 0.40f,
            Experience = 0f,
            Cohesion = 0.55f,
            SupplyLevel = 0.80f,
            Entrenchment = 0.25f,
            OrderKind = ArmyOrder.Manual,
            PlanActive = false,
            Latitude = position.Latitude,
            Longitude = position.Longitude
        });
        DiagnosticLogs.Write("simulation",
            $"MILITIA_RAISED nation={nation.Tag} region={state.Name} town={place} province={province} men={men}");
        // In the country's own chronicle: a town taking up arms is news to the people whose
        // town it is, and forty of them across a world war are noise to everybody else.
        campaign.Chronicle.Record(campaign, "militia_raised", nation.Id,
            place: town is null ? state.Name : $"city.{town.Id}.name", isPublic: false);
        campaign.MarkStrategicChange();
        return true;
    }

    /// <summary>
    /// Where a region's militia forms up: its largest town the country still holds.
    ///
    /// It used to be whichever of the region's provinces the map compiler happened to number
    /// first, which put militia on offshore islands, in marshes and in the corner of the region
    /// furthest from the war. People take up arms where they live, and where they live is the
    /// towns: Kharkiv's militia is in Kharkiv. A region with no town on the map, or whose towns
    /// have all fallen, forms up in the middle of the ground it still holds.
    /// </summary>
    public static (int Province, CityDefinition Town) Seat(ProvinceTopology topology, ushort[] control,
        ushort flag, ushort region)
    {
        if (topology is null || control is null) return (-1, null);
        CityDefinition town = null;
        var seat = -1;
        foreach (var city in CityCatalog.All)
        {
            if (town is not null && city.Population <= town.Population) continue;
            var province = ProvinceAt(topology, city.Latitude, city.Longitude);
            if (province <= 0 || province >= control.Length || topology.IsWater(province)) continue;
            if (topology.ProvinceToState[province] != region || control[province] != flag) continue;
            town = city;
            seat = province;
        }
        if (town is not null) return (seat, town);

        // The middle of what is still held, as a direction from the centre of the Earth, so a
        // region across the date line does not average to the far side of the planet.
        double x = 0, y = 0, z = 0;
        for (var province = 1; province <= topology.ProvinceCount && province < control.Length; province++)
        {
            if (topology.ProvinceToState[province] != region || control[province] != flag || topology.IsWater(province))
                continue;
            var (latitude, longitude) = topology.CenterOf(province);
            var (px, py, pz) = Direction(latitude, longitude);
            x += px; y += py; z += pz;
        }
        var best = double.MinValue;
        for (var province = 1; province <= topology.ProvinceCount && province < control.Length; province++)
        {
            if (topology.ProvinceToState[province] != region || control[province] != flag || topology.IsWater(province))
                continue;
            var (latitude, longitude) = topology.CenterOf(province);
            var (px, py, pz) = Direction(latitude, longitude);
            var toward = px * x + py * y + pz * z;
            if (toward <= best) continue;
            best = toward;
            seat = province;
        }
        return (seat, null);
    }

    private static (double X, double Y, double Z) Direction(float latitude, float longitude)
    {
        var phi = latitude * Math.PI / 180.0;
        var lambda = longitude * Math.PI / 180.0;
        return (Math.Cos(phi) * Math.Cos(lambda), Math.Cos(phi) * Math.Sin(lambda), Math.Sin(phi));
    }

    /// <summary>The province under a point of the globe, or 0 for none.</summary>
    private static int ProvinceAt(ProvinceTopology topology, float latitude, float longitude)
    {
        if (topology.Width <= 0 || topology.Height <= 0) return 0;
        var x = (int)MathF.Floor((longitude + 180f) / 360f * topology.Width);
        var y = (int)MathF.Floor((90f - latitude) / 180f * topology.Height);
        x = ((x % topology.Width) + topology.Width) % topology.Width;
        y = Math.Clamp(y, 0, topology.Height - 1);
        return topology.CellToProvince[y * topology.Width + x];
    }

    /// <summary>
    /// Militia goes home. Once nothing has threatened the ground it stood up on for long
    /// enough, it dissolves - otherwise a war fought in 2026 leaves a country carrying forty
    /// free divisions in 2040. The men are not returned to any pool because they never came
    /// out of one; they go back to the region they were always in.
    /// </summary>
    private static void StandDown(Campaign campaign, bool fighting)
    {
        var weights = Weights;
        List<Army> going = null;
        var lastNation = -1;
        foreach (var army in campaign.Armies)
        {
            if (army.TemplateId != TemplateId) continue;
            var nation = campaign.NationById(army.NationId);
            if (nation is null) continue;

            // The enemy list changes per country, not per army, and militia comes in runs
            // of the same country because that is the order they were raised in.
            if (army.NationId != lastNation)
            {
                Enemies.Clear();
                if (fighting) CollectEnemies(campaign, nation, Enemies);
                lastNation = army.NationId;
            }

            ScenarioState state = null;
            foreach (var candidate in campaign.States)
                if (candidate.MapIndex == army.StateMapIndex) { state = candidate; break; }

            var threatened = state is not null && nation is not null &&
                             Threat(ControlTally.World(campaign, nation.MapIndex, state.MapIndex)) > 0.0001f;
            // A new war cancels the quiet-period clock even before the enemy reaches
            // this region. Otherwise a reserve could disappear on the declaration day.
            if (Enemies.Count > 0 || threatened || army.InBattle)
            { nation.MilitiaQuietSince.Remove(army.Id); continue; }

            if (!nation.MilitiaQuietSince.TryGetValue(army.Id, out var since))
            {
                nation.MilitiaQuietSince[army.Id] = campaign.ElapsedDays;
                continue;
            }
            if (campaign.ElapsedDays - since < weights.StandDownDays) continue;
            (going ??= new List<Army>()).Add(army);
        }
        if (going is null) return;

        foreach (var army in going)
        {
            campaign.NationById(army.NationId)?.MilitiaQuietSince.Remove(army.Id);
            campaign.Armies.Remove(army);
            DiagnosticLogs.Write("simulation", $"MILITIA_STOOD_DOWN army={army.Id}");
            campaign.Chronicle.Record(campaign, "militia_stood_down", army.NationId, subject: army.Name, isPublic: false);
        }
        campaign.MarkStrategicChange();
    }
}
