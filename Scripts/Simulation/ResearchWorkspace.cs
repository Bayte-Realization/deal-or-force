using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Read-only browsing; filtering and inspection never choose an authoritative project.</summary>
public static class ResearchWorkspace
{
    public enum State { Known, Active, Available, Blocked }
    public enum Filter { All, Available, Blocked, Known, Invested }

    public static bool CanStart(Campaign campaign, Nation nation, TechnologyDefinition technology) =>
        campaign is not null && nation is not null && technology is not null && campaign.FeatureEnabled("research") &&
        (!campaign.Defines.FeatureCommands.TryGetValue(nameof(SetResearchCommand), out var feature) ||
            string.IsNullOrWhiteSpace(feature) || campaign.FeatureEnabled(feature)) &&
        ResearchSystem.Available(nation, technology) &&
        !string.Equals(nation.CurrentResearch, technology.Id, StringComparison.OrdinalIgnoreCase);

    public static State Status(Nation nation, TechnologyDefinition technology)
    {
        if (nation.HasTechnology(technology.Id)) return State.Known;
        if (string.Equals(nation.CurrentResearch, technology.Id, StringComparison.OrdinalIgnoreCase)) return State.Active;
        return ResearchSystem.Available(nation, technology) ? State.Available : State.Blocked;
    }

    public static string Caption(State state) => state switch
    {
        State.Known => "Researched", State.Active => "Researching",
        State.Available => "Available", _ => "Prerequisites missing"
    };

    public static TechnologyDefinition[] Browse(IEnumerable<TechnologyDefinition> definitions, Nation nation,
        string category, Filter filter, string query, Func<string, string> translate)
    {
        query = query?.Trim() ?? "";
        return definitions.Where(technology =>
        {
            if (!string.IsNullOrEmpty(category) && !string.Equals(category, technology.Category, StringComparison.OrdinalIgnoreCase)) return false;
            var state = Status(nation, technology);
            if (filter == Filter.Available && state is not (State.Available or State.Active)
                || filter == Filter.Blocked && state != State.Blocked
                || filter == Filter.Known && state != State.Known
                || filter == Filter.Invested && (state == State.Known || nation.ResearchProgress.GetValueOrDefault(technology.Id) <= 0)) return false;
            return query.Length == 0 || technology.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Matches(technology.Name) || Matches(technology.Description)
                || technology.Effects.Keys.Any(effect => effect.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || Matches(NationalModifiers.Of(effect)?.Name ?? effect));
            bool Matches(string text) => (translate(text) ?? "").Contains(query, StringComparison.OrdinalIgnoreCase);
        }).ToArray();
    }

    /// <summary>Cross-category ancestors; missing definitions and malformed mod cycles are bounded.</summary>
    public static HashSet<string> DependencyPath(IEnumerable<TechnologyDefinition> definitions, string selected)
    {
        var catalog = definitions.ToDictionary(technology => technology.Id, StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        if (!string.IsNullOrEmpty(selected)) pending.Push(selected);
        while (pending.TryPop(out var id))
        {
            if (!catalog.TryGetValue(id, out var technology) || !result.Add(id)) continue;
            foreach (var prerequisite in technology.Requires) pending.Push(prerequisite);
        }
        return result;
    }

    public static int? Weeks(float remaining, float weeklyPoints)
    {
        if (!float.IsFinite(remaining) || !float.IsFinite(weeklyPoints) || weeklyPoints <= 0) return null;
        return (int)Math.Min(int.MaxValue, Math.Ceiling(Math.Max(0, (double)remaining) / weeklyPoints));
    }
}
