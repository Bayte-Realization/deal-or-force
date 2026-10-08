using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class ScenarioState
{
    /// <summary>Authored homeland titles, independent of the actual owner/controller.
    /// They are game rules, not a claim that every territorial demand is lawful.</summary>
    public List<string> CoreTags = new();
}

public sealed partial class Nation
{
    // Original region identities, so giving up a title also covers every partition.
    public HashSet<int> RenouncedTerritoryOrigins = new();
}

public sealed partial class Defines
{
    public bool AiPrioritisesLostHomeland { get; set; } = true;
    public float PeaceCostHomelandFactor { get; set; } = .65f;
    public float TerritorialClaimWeight { get; set; } = .3f;
}

public enum TerritorialTitle { None, Claim, Homeland }
public readonly record struct TerritorialDispute(ScenarioState Region, Nation Claimant, Nation Holder, TerritorialTitle Title);

/// <summary>Title, ownership and occupation answer three different questions. A treaty
/// changes ownership; it does not silently erase homeland memory. Partitioned regions
/// retain the original identity, including claims on the smaller piece.</summary>
public static class TerritorialClaims
{
    public static int Origin(ScenarioState state) => state.ParentMapIndex > 0 ? state.ParentMapIndex : state.MapIndex;

    public static TerritorialTitle TitleOf(Campaign c, Nation nation, ScenarioState region)
    {
        if (c is null || nation is null || region is null || region.Unclaimed ||
            nation.RenouncedTerritoryOrigins.Contains(Origin(region))) return TerritorialTitle.None;
        if (region.CoreTags.Contains(nation.Tag, StringComparer.OrdinalIgnoreCase) ||
            (c.Homelands.TryGetValue(nation.Id, out var home) &&
             (home.Contains(region.MapIndex) || home.Contains(Origin(region))))) return TerritorialTitle.Homeland;
        return region.Claims.Contains(nation.Tag, StringComparer.OrdinalIgnoreCase) ? TerritorialTitle.Claim : TerritorialTitle.None;
    }

    public static bool HoldsOurHomeland(Campaign c, Nation nation, Nation holder) =>
        nation is not null && holder is not null && nation.Id != holder.Id &&
        !holder.AnswersTo(nation.Id) && !nation.AnswersTo(holder.Id) &&
        c.StatesOf(holder).Any(s => TitleOf(c, nation, s) == TerritorialTitle.Homeland);

    public static ScenarioState RecoveryGoal(Campaign c, Nation nation, Nation holder) =>
        c.StatesOf(holder).Where(s => TitleOf(c, nation, s) == TerritorialTitle.Homeland)
            .OrderByDescending(s => s.Population).ThenBy(s => s.MapIndex).FirstOrDefault();

    /// <summary>Recovery takes precedence only among affordable, reachable positive
    /// judgements. A claim neither breaks a truce nor removes military deterrence.</summary>
    internal static bool PreferTarget(Campaign c, Nation nation, Nation candidate, float judgement,
        Nation current, float currentJudgement)
    {
        if (judgement <= 0) return current is null && judgement > currentJudgement;
        if (c.Defines.AiPrioritisesLostHomeland && current is not null && currentJudgement > 0)
        {
            var candidateHome = HoldsOurHomeland(c, nation, candidate);
            var currentHome = HoldsOurHomeland(c, nation, current);
            if (candidateHome != currentHome) return candidateHome;
        }
        return current is null || judgement > currentJudgement;
    }

    public static IReadOnlyList<TerritorialDispute> Disputes(Campaign c)
    {
        var rows = new List<TerritorialDispute>();
        if (c is null) return rows;
        foreach (var region in c.States.OrderBy(s => s.MapIndex))
        {
            var holder = c.Nations.Find(n => n.MapIndex == region.NationMapIndex);
            if (holder is null || region.Unclaimed || region.Population <= 0) continue;
            foreach (var nation in c.Nations)
            {
                if (nation.Id == holder.Id || nation.Movement is not null || nation.Population <= 0 ||
                    holder.AnswersTo(nation.Id) || nation.AnswersTo(holder.Id)) continue;
                var title = TitleOf(c, nation, region);
                if (title != TerritorialTitle.None) rows.Add(new(region, nation, holder, title));
            }
        }
        return rows;
    }

    public static void Renounce(Campaign c, Nation nation, ScenarioState region)
    {
        if (c is null || nation is null || region is null) return;
        var origin = Origin(region);
        nation.RenouncedTerritoryOrigins.Add(origin);
        if (c.Homelands.TryGetValue(nation.Id, out var home))
            home.RemoveAll(id => id == origin || c.States.Find(s => s.MapIndex == id) is { } s && Origin(s) == origin);
        c.MarkStrategicChange();
    }
}
