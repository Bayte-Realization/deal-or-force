using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Read-only agenda projection. No commands, forecasts, caches or second world.</summary>
public static class AgendaWorkspace
{
    public enum Section { Decisions, Promises, Goals }
    public enum Filter { All, Ready, Active, Completed }
    public enum Status { Ready, Blocked, Active, Completed }
    public readonly record struct Entry(string Id, string Name, string Description, Status State, string Reason);

    public static List<Entry> Entries(Campaign c, Nation nation, Section section,
        string query = "", Filter filter = Filter.All)
    {
        var result = new List<Entry>();
        if (c is null || nation is null) return result;
        query = (query ?? "").Trim();
        void Add(string id, string name, string description, Status state, string reason)
        {
            if (!Matches(filter, state)) return;
            bool Has(string text) => (text ?? "").Contains(query, StringComparison.OrdinalIgnoreCase);
            if (query.Length > 0 && !Has(id) && !Has(Loc.Get(name)) && !Has(Loc.Get(description))) return;
            result.Add(new(id, name, description, state, reason));
        }
        switch (section)
        {
            case Section.Decisions:
                foreach (var d in DecisionCatalog.All)
                {
                    var reason = DecisionCatalog.BlockReason(c, nation, d);
                    Add(d.Id, d.Name, d.Description, reason.Length == 0 ? Status.Ready : Status.Blocked, reason);
                }
                break;
            case Section.Promises when c.FeatureEnabled("government_promises"):
                foreach (var d in PromiseCatalog.All)
                {
                    var active = nation.Promises.Any(p => p.Id == d.Id);
                    var reason = GovernmentPromises.BlockReason(c, nation, d);
                    Add(d.Id, d.Name, d.Description, active ? Status.Active :
                        reason.Length == 0 ? Status.Ready : Status.Blocked, reason);
                }
                break;
            case Section.Goals when c.FeatureEnabled("strategic_goals"):
                foreach (var d in GoalCatalog.All)
                {
                    var active = nation.Goals.FirstOrDefault(g => g.Id == d.Id);
                    var reason = active is null ? StrategicGoals.BlockReason(c, nation, d) : "";
                    Add(d.Id, d.Name, d.Description, active is not null ?
                        active.CompletedDay >= 0 ? Status.Completed : Status.Active :
                        reason.Length == 0 ? Status.Ready : Status.Blocked, reason);
                }
                break;
        }
        return result;
    }

    private static bool Matches(Filter filter, Status state) => filter switch
    {
        Filter.All => true, Filter.Ready => state == Status.Ready,
        Filter.Active => state == Status.Active, Filter.Completed => state == Status.Completed, _ => false
    };
}
