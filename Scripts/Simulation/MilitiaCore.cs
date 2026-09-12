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
        Enemies.Clear();
        CollectEnemies(campaign, owner, Enemies);
        Tally.Clear();
        ControlTally.Of(campaign, owner, Enemies, Tally);
        return Threat(state, Tally);
    }

    /// <summary>The same, against a tally already taken. Used inside the day.</summary>
    private static float Threat(ScenarioState state, Dictionary<int, RegionControl> tally)
    {
        if (state is null) return 0f;
        if (!tally.TryGetValue(state.MapIndex, out var control) || control.Provinces == 0) return 0f;
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
    private static readonly Dictionary<int, RegionControl> Tally = new();

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
            var politics = PoliticsMultiplier(nation);
            if (politics <= 0f) continue;
            Tally.Clear();
            ControlTally.Of(campaign, nation, Enemies, Tally);

            foreach (var state in campaign.StatesOf(nation.MapIndex))
            {
                // A village does not field a division, however frightened it is.
                if (state.Population < weights.PopulationPerFormation) continue;
                var threat = Threat(state, Tally);
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

        var province = -1;
        for (var candidate = 1; candidate <= topology.ProvinceCount && candidate < control.Length; candidate++)
        {
            if (topology.ProvinceToState[candidate] != (ushort)state.MapIndex) continue;
            if (control[candidate] != flag) continue;
            province = candidate;
            break;
        }
        // A region entirely under the enemy raises nobody: the people are there, the ground
        // to form up on is not. That is what a partisan rule would be for, and it is not
        // this one.
        if (province <= 0) return false;

        var men = (int)Weights.Formation;
        var position = topology.CenterOf(province);
        var id = 0;
        var serial = 0;
        foreach (var army in campaign.Armies)
        {
            if (army.Id > id) id = army.Id;
            if (army.NationId == nation.Id && army.TemplateId == TemplateId) serial++;
        }

        campaign.Armies.Add(new Army
        {
            Id = id + 1,
            NationId = nation.Id,
            StateMapIndex = state.MapIndex,
            ProvinceId = province,
            Name = $"{serial + 1}. {nation.Tag} Militia",
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
            $"MILITIA_RAISED nation={nation.Tag} region={state.Name} province={province} men={men}");
        campaign.MarkStrategicChange();
        return true;
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
                Tally.Clear();
                ControlTally.Of(campaign, nation, Enemies, Tally);
                lastNation = army.NationId;
            }

            ScenarioState state = null;
            foreach (var candidate in campaign.States)
                if (candidate.MapIndex == army.StateMapIndex) { state = candidate; break; }

            var threatened = state is not null && Threat(state, Tally) > 0.0001f;
            if (threatened || army.InBattle) { nation.MilitiaQuietSince.Remove(army.Id); continue; }

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
        }
        campaign.MarkStrategicChange();
    }
}
