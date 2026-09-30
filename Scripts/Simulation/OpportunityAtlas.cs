using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Read-only, on-demand overview. Never executes decisions or predicts a return on investment.</summary>
public static class OpportunityAtlas
{
    public sealed record Formation(FormableNation Definition, FormationStatus Status);
    public sealed record Decision(DecisionDefinition Definition, string Block, float Cost);
    public sealed record Shortage(Commodity Good, float Satisfaction, float Production, float Demand, float Imports, float Exports);
    public sealed record Snapshot(Formation[] Formations, Decision[] Decisions, Shortage[] Shortages);

    public static Snapshot Read(Campaign campaign, Nation nation, float shortageThreshold = .985f)
    {
        if (campaign is null || nation is null) return new(Array.Empty<Formation>(), Array.Empty<Decision>(), Array.Empty<Shortage>());
        shortageThreshold = float.IsFinite(shortageThreshold) ? Math.Clamp(shortageThreshold, 0, .985f) : .985f;
        var formations = string.IsNullOrEmpty(nation.FormedNationId)
            ? FormableNations.All.Where(d => FormableNations.Eligible(nation, d))
                .Select(d => new Formation(d, FormableNations.Evaluate(campaign, nation, d)))
                .OrderByDescending(r => r.Status.CanForm)
                .ThenByDescending(r => r.Status.Total > 0 ? (double)r.Status.Owned / r.Status.Total : 0)
                .ThenBy(r => r.Definition.Id, StringComparer.Ordinal).ToArray()
            : Array.Empty<Formation>();
        var decisions = DecisionCatalog.All.Select(d => new Decision(d, DecisionCatalog.BlockReason(campaign, nation, d), DecisionCatalog.Cost(nation, d)))
            .OrderBy(r => r.Block.Length > 0).ThenBy(r => r.Cost).ThenBy(r => r.Definition.Id, StringComparer.Ordinal).ToArray();
        var shortages = nation.Market.Values.Where(q => q.Demand > .001f && float.IsFinite(q.Satisfaction) && q.Satisfaction < shortageThreshold)
            .OrderBy(q => q.Satisfaction).ThenBy(q => q.Commodity)
            .Select(q => new Shortage(q.Commodity, q.Satisfaction, q.Production, q.Demand, q.Imports, q.Exports)).ToArray();
        return new(formations, decisions, shortages);
    }
}
