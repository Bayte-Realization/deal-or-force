using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// How settled a country is, worked out from the country rather than stored as a number.
///
/// Stability used to be a single figure that drifted by a thousandth a week. It could not
/// answer the question a player actually asks - *why* is my country coming apart - because
/// there was nothing underneath it to point at, and it could not be pushed by anything
/// local: a region could be starving, foreign and furious, and the national figure would
/// not notice.
///
/// Now every region has a stability of its own, and the country's is what those add up to.
/// The capital counts for far more than its population, which is the historically honest
/// part: a state survives a furious province and falls to a furious capital. Petrograd in
/// 1917 was one city in an empire of a hundred and fifty million.
///
/// Everything about the shape of it is in `common/stability`, including how much the
/// capital is worth, so a mod that thinks provinces should matter more than palaces can
/// say so in one file.
/// </summary>
public sealed class StabilityWeights
{
    /// <summary>What a region with nothing wrong with it is worth.</summary>
    public float Base = 0.62f;

    /// <summary>Standard of living above <see cref="LivingTolerance"/> settles a region; below it does not.</summary>
    public float Living = 0.55f;
    public float LivingTolerance = 0.45f;

    /// <summary>How much of a region's stability its radicalism takes away.</summary>
    public float Radicalism = 0.85f;

    /// <summary>
    /// How much a national minority costs. Not because people of another nationality are
    /// trouble, but because a state that is not theirs has less of their consent - which is
    /// the same reason the secession rules will read this number.
    /// </summary>
    public float Minority = 0.30f;

    /// <summary>What it costs to have someone else's army standing in the region.</summary>
    public float Occupation = 0.55f;

    /// <summary>A literate population is harder to govern badly. Small, and deliberately so.</summary>
    public float Literacy = 0.04f;

    /// <summary>
    /// The share of national stability the capital's own region decides. The rest is the
    /// country by population.
    /// </summary>
    public float CapitalShare = 0.35f;

    /// <summary>What being at war costs, and what each point of lost war support adds to it.</summary>
    public float War = 0.06f;
    public float WarSupport = 0.10f;

    /// <summary>How much of the gap to its target a country closes in a week.</summary>
    public float Approach = 0.10f;

    /// <summary>
    /// The most that how a country is being governed can be worth, either way, and how much
    /// of that it gives back each week when the governing stops. Bounded on purpose: a
    /// cabinet should be able to steady a country, not to make it permanently unfallable.
    /// </summary>
    public float GovernanceLimit = 0.15f;
    public float GovernanceDecay = 0.004f;

    public float Floor = 0.05f;
    public float Ceiling = 0.98f;
}

public static class StabilityCore
{
    public static StabilityWeights Weights { get; private set; } = new();

    static StabilityCore() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Weights = new StabilityWeights();
        foreach (var document in GameDataLoader.DataDocuments("common", "stability"))
            using (document)
                Merge(document.RootElement, errors);
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
    }

    /// <summary>Later files win, field by field, so a mod names only what it disagrees with.</summary>
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
                case "living": Weights.Living = value; break;
                case "living_tolerance": Weights.LivingTolerance = value; break;
                case "radicalism": Weights.Radicalism = value; break;
                case "minority": Weights.Minority = value; break;
                case "occupation": Weights.Occupation = value; break;
                case "literacy": Weights.Literacy = value; break;
                case "capital_share": Weights.CapitalShare = value; break;
                case "war": Weights.War = value; break;
                case "war_support": Weights.WarSupport = value; break;
                case "approach": Weights.Approach = value; break;
                case "governance_limit": Weights.GovernanceLimit = value; break;
                case "governance_decay": Weights.GovernanceDecay = value; break;
                case "floor": Weights.Floor = value; break;
                case "ceiling": Weights.Ceiling = value; break;
                default: errors?.Add($"common/stability: '{entry.Name}' is not a weight this game has."); break;
            }
        }
    }

    /// <summary>
    /// How settled one region is: what it lives on, what it thinks, who it is, and whose
    /// soldiers are standing in it.
    /// </summary>
    public static float RegionStability(Campaign campaign, Nation owner, ScenarioState state)
    {
        return RegionStability(owner, state,
            ControlTally.World(campaign, owner?.MapIndex ?? 0, state?.MapIndex ?? 0));
    }

    /// <summary>The same, against a tally already taken. Used inside the weekly pass.</summary>
    private static float RegionStability(Nation owner, ScenarioState state,
        RegionControl control)
    {
        if (state is null || owner is null) return Weights.Base;
        var weights = Weights;
        var value = weights.Base
                    + (state.StandardOfLiving - weights.LivingTolerance) * weights.Living
                    - state.Radicalism * weights.Radicalism
                    - EconomySystem.ForeignShare(state, owner.Tag) * weights.Minority
                    + (state.Literacy + owner.LiteracyOffset - 0.5f) * weights.Literacy
                    - control.OccupiedShare * weights.Occupation;
        return M.Clamp(value, 0f, 1f);
    }

    /// <summary>How much of a region is under somebody else's flag right now.</summary>
    public static float OccupiedShare(Campaign campaign, ScenarioState state, Nation owner)
    {
        var control = ControlTally.World(campaign, owner?.MapIndex ?? 0, state?.MapIndex ?? 0);
        return control.OccupiedShare;
    }

    // Scratch, rebuilt on the way in and never read between calls.
    private static readonly Dictionary<int, RegionControl> Tally = new();

    /// <summary>
    /// What the country's stability is being pulled towards: its capital, weighted far
    /// above its size, the rest of it by population, what the war is costing, and the
    /// country's own baseline.
    /// </summary>
    public static float Target(Campaign campaign, Nation nation, Breakdown drivers = null)
    {
        if (campaign is null || nation is null) return Weights.Base;
        var weights = Weights;

        var capitalStability = -1f;
        double weighted = 0;
        long people = 0;
        foreach (var state in campaign.StatesOf(nation.MapIndex))
        {
            // One walk of the map a day for the whole world, shared with everything that asks.
            var settled = RegionStability(nation, state,
                ControlTally.World(campaign, nation.MapIndex, state.MapIndex));
            state.Stability = settled;
            if (state.MapIndex == nation.CapitalStateMapIndex) capitalStability = settled;
            if (state.Population <= 0) continue;
            weighted += settled * (double)state.Population;
            people += state.Population;
        }

        var country = people > 0 ? (float)(weighted / people) : weights.Base;
        // A country whose capital has been lost, or that never had one on the map, is
        // judged by the rest of itself rather than by nothing.
        if (capitalStability < 0f) capitalStability = country;

        var share = M.Clamp01(weights.CapitalShare);
        var geography = capitalStability * share + country * (1f - share);

        var atWar = false;
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
        var warCost = atWar ? weights.War + (1f - nation.WarSupport) * weights.WarSupport : 0f;

        var memory = SocialMemory.StabilityEffect(campaign, nation);
        var target = geography - warCost + nation.StabilityBias + nation.StabilityGovernance + memory;

        if (drivers is not null)
        {
            drivers.Begin("What the country is settled at", "share");
            drivers.Add("The capital and the region around it", capitalStability * share);
            drivers.Add("The rest of the country, by population", country * (1f - share));
            if (warCost > 0f) drivers.Add("The war", -warCost);
            drivers.Add("What this country is, beyond its conditions", nation.StabilityBias);
            if (MathF.Abs(nation.StabilityGovernance) > 0.0005f)
                drivers.Add("How it has been governed lately", nation.StabilityGovernance);
            if (MathF.Abs(memory) > 0.0005f)
                drivers.Add(Loc.Get("Institutions and living-standard expectations"), memory);
            drivers.Settle(M.Clamp(target, weights.Floor, weights.Ceiling) - target,
                target > weights.Ceiling ? "already as settled as a country gets"
                    : target < weights.Floor ? "held up off the floor" : "rounding");
        }

        return M.Clamp(target, weights.Floor, weights.Ceiling);
    }

    /// <summary>
    /// Make the formula agree with the scenario it is being switched on in.
    ///
    /// Every country in 2024 is authored with a stability the scenario means: 74 % for
    /// South Korea is a statement about South Korea, not an accident of its regions'
    /// radicalism. The bias is the difference between that and what conditions alone
    /// explain - institutions, legitimacy, a century of habit - and it is what the formula
    /// carries forward. Without it, switching stability to a formula would quietly restate
    /// the settledness of all 261 countries on the first week.
    /// </summary>
    /// <summary>
    /// Whether this world has had its baselines taken.
    ///
    /// Asked of the world rather than remembered in a field, because a world can arrive from
    /// somewhere else - a multiplayer snapshot, a save - already calibrated, and a flag that
    /// lives in this process would say otherwise. A world nobody has calibrated has every
    /// baseline at exactly zero, which no calibrated world does.
    /// </summary>
    public static bool IsCalibrated(Campaign campaign)
    {
        if (campaign is null) return true;
        foreach (var nation in campaign.Nations)
            if (MathF.Abs(nation.StabilityBias) > 0.0001f) return true;
        return false;
    }

    public static void Calibrate(Campaign campaign)
    {
        if (campaign is null) return;
        foreach (var nation in campaign.Nations)
        {
            nation.StabilityBias = 0f;
            nation.StabilityGovernance = 0f;
            var conditions = Target(campaign, nation);
            nation.StabilityBias = nation.Stability - conditions;
        }
    }
}
