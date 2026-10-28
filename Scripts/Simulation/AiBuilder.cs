using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// A good the country's market supplies less than this share of is one a country nobody
    /// plays builds for, at peace and with nothing more pressing to build.
    /// </summary>
    public float AiBuildShortage { get; set; } = 0.9f;

    /// <summary>How many of the worst shortages it looks at before it settles for civilian industry.</summary>
    public int AiBuildShortagesConsidered { get; set; } = 3;

    /// <summary>The deposit a region must hold of a good before a country opens a mine there for it.</summary>
    public float AiMineDeposit { get; set; } = 0.5f;

    /// <summary>
    /// A good the whole world makes this many times what it wants is one a country short of it
    /// buys rather than builds for: its shortage is a trade route, not a missing factory.
    /// </summary>
    public float AiBuildWorldGlut { get; set; } = 1.4f;
}

/// <summary>
/// What a country nobody plays builds when it is at peace and has nothing more pressing to
/// build - no congested supply lines, a literate people, a decent standard of living.
///
/// It used to build civilian industry, always: the AI put up five kinds of building out of
/// seventeen, and never a mine, a power plant, a refinery or a port, whatever its market was
/// short of and whatever lay in its ground. Now it looks at its market's worst shortages, finds
/// the buildings whose recipes make that good (<see cref="ProductionChains.RolesOf"/>, so a
/// chain a mod adds is found the same way), the specialist first - the building that makes the
/// fewest different goods - and builds the first that can go somewhere:
///
/// - a mine where the deposit of that good is richest, for the mines already there;
/// - anything else in the biggest region with fewest of it, as before - where water allows.
///
/// With no shortage worth building for, it builds civilian industry, as it always did.
/// </summary>
public static class AiBuilder
{
    /// <summary>What each kind of building has been chosen for, for the bench.</summary>
    public static readonly Dictionary<BuildingKind, int> Chosen = new();

    private static readonly List<Commodity> Makes = new();
    private static readonly List<Commodity> Uses = new();
    private static readonly List<(Commodity Good, float Weight)> Shortages = new();
    private static readonly List<(BuildingKind Kind, int Goods)> Candidates = new();

    public static bool Choose(Campaign campaign, Nation nation, ScenarioState[] states,
        out BuildingKind kind, out ScenarioState where)
    {
        kind = BuildingKind.CivilianIndustry;
        where = null;
        var defines = campaign.Defines;

        // The worst shortages first, by what the unmet part is worth.
        Shortages.Clear();
        foreach (var quote in nation.Market.Values)
        {
            if (quote.Demand <= 0.05f || quote.Satisfaction >= defines.AiBuildShortage) continue;
            // Twenty-eight countries short of coal while the world mined six times what it burned
            // each opened mines of their own, and the glut fed on itself for five years.
            if (WorldRatio(campaign, quote.Commodity) > defines.AiBuildWorldGlut) continue;
            Shortages.Add((quote.Commodity, (1f - quote.Satisfaction) * quote.Demand * MathF.Max(0.01f, quote.Price)));
        }
        if (Shortages.Count == 0) return false;
        Shortages.Sort((a, b) => b.Weight != a.Weight ? b.Weight.CompareTo(a.Weight) : a.Good.CompareTo(b.Good));

        var considered = Math.Min(Shortages.Count, Math.Max(1, defines.AiBuildShortagesConsidered));
        for (var index = 0; index < considered; index++)
        {
            var good = Shortages[index].Good;

            // Of the buildings that make it, the specialist first: a power plant for energy
            // rather than a mine whose coal might become some, an electronics plant for
            // electronics rather than the civilian industry that makes a little of everything.
            Candidates.Clear();
            foreach (var building in BuildingCatalog.All)
            {
                ProductionChains.RolesOf(building, Makes, Uses);
                if (Makes.Contains(good)) Candidates.Add((building, Makes.Count));
            }
            Candidates.Sort((a, b) => a.Goods != b.Goods ? a.Goods.CompareTo(b.Goods) : a.Kind.CompareTo(b.Kind));

            foreach (var (candidate, _) in Candidates)
            {
                var target = candidate == BuildingKind.ExtractionIndustry
                    ? RichestFor(campaign, states, good)
                    : Biggest(campaign, states, candidate);
                if (target is null) continue;
                kind = candidate;
                where = target;
                Chosen[kind] = Chosen.GetValueOrDefault(kind) + 1;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the country is short of anything an arms factory makes. A country at war built
    /// one every month whatever its arsenals held: in five years the world made four times the
    /// missiles, tanks and aircraft it wanted.
    /// </summary>
    public static bool ShortOfArms(Campaign campaign, Nation nation)
    {
        ProductionChains.RolesOf(BuildingKind.ArmsIndustry, Makes, Uses);
        foreach (var good in Makes)
            if (nation.Market.TryGetValue(good, out var quote) && quote.Demand > 0.05f &&
                quote.Satisfaction < campaign.Defines.AiBuildShortage &&
                WorldRatio(campaign, good) <= campaign.Defines.AiBuildWorldGlut)
                return true;
        return false;
    }

    /// <summary>What the world makes of a good for each unit it wants.</summary>
    internal static float WorldRatio(Campaign campaign, Commodity good)
    {
        var production = 0f;
        var demand = 0f;
        foreach (var nation in campaign.Nations)
        {
            if (!nation.Market.TryGetValue(good, out var quote)) continue;
            production += quote.Production;
            demand += quote.Demand;
        }
        return demand <= 0.001f ? float.MaxValue : production / demand;
    }

    /// <summary>The region whose deposit of this good is richest for the mines it already has.</summary>
    private static ScenarioState RichestFor(Campaign campaign, ScenarioState[] states, Commodity good)
    {
        ScenarioState best = null;
        var bestScore = 0f;
        foreach (var state in states)
        {
            var deposit = state.ResourceDeposits.GetValueOrDefault(good);
            if (deposit < campaign.Defines.AiMineDeposit) continue;
            if (Waterfront.Refusal(campaign, state.MapIndex, BuildingKind.ExtractionIndustry).Length > 0) continue;
            var score = deposit / (1f + state.Buildings.GetValueOrDefault(BuildingKind.ExtractionIndustry));
            if (score <= bestScore) continue;
            bestScore = score;
            best = state;
        }
        return best;
    }

    /// <summary>The biggest region with fewest of these, where the building may stand at all.</summary>
    private static ScenarioState Biggest(Campaign campaign, ScenarioState[] states, BuildingKind kind)
    {
        ScenarioState best = null;
        var bestScore = float.MinValue;
        foreach (var state in states)
        {
            if (Waterfront.Refusal(campaign, state.MapIndex, kind).Length > 0) continue;
            var score = state.Population / 1_000_000f - state.Buildings.GetValueOrDefault(kind) * campaign.Defines.AiBuildSaturation;
            if (score <= bestScore) continue;
            bestScore = score;
            best = state;
        }
        return best;
    }
}
