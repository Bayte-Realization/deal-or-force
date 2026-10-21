using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool CampaignAlertsEnabled { get; set; } = true;
    public int CampaignAlertSnoozeDays { get; set; } = 7;
}

public enum CampaignAlertGroup { Must, Should, Could, Important }
public enum CampaignAlertRoute { Situation, Event, Elections, Decisions, Battle, War, Research, Personnel, Doctrine, Army }
public sealed record CampaignAlert(string Id, CampaignAlertGroup Group, string Title, string Summary,
    CampaignAlertRoute Route, int TargetId = -1, SituationItem Situation = null);

/// <summary>A bounded, read-only index of the player's existing obligations and opportunities.
/// No initialization, speculative forecasts or global news. Snoozing is local UI memory.</summary>
public static class CampaignAlerts
{
    public static string GroupName(CampaignAlertGroup group) => group switch
    {
        CampaignAlertGroup.Must => "Must do", CampaignAlertGroup.Should => "Should do",
        CampaignAlertGroup.Could => "Can do", _ => "Important developments"
    };

    public static IReadOnlyList<CampaignAlert> Read(Campaign c, Nation n,
        IReadOnlyList<SituationItem> report = null, bool observer = false)
    {
        if (c is null || n is null || !c.Defines.CampaignAlertsEnabled || observer) return Array.Empty<CampaignAlert>();
        var result = new List<CampaignAlert>();
        foreach (var item in report ?? SituationReport.Read(c, n))
        {
            var group = item.Priority switch
            {
                SituationPriority.Critical => CampaignAlertGroup.Must,
                SituationPriority.Warning => CampaignAlertGroup.Should,
                SituationPriority.Awaiting when item.DeadlineDay >= 0 => CampaignAlertGroup.Must,
                SituationPriority.Awaiting => CampaignAlertGroup.Important,
                _ => CampaignAlertGroup.Could
            };
            result.Add(new(item.Id, group, item.Title, item.Summary, CampaignAlertRoute.Situation, item.TargetId, item));
        }
        foreach (var e in c.PendingEvents.Where(e => e.NationId == n.Id).OrderBy(e => e.ExpiresDay).ThenBy(e => e.Id).Take(4))
            result.Add(new("event:" + e.Id, CampaignAlertGroup.Must, Loc.Get(e.Title), Loc.Get(e.Description), CampaignAlertRoute.Event, e.Id));
        if (ElectoralPolitics.Enabled(c) && ElectoralPolitics.Eligible(n) && n.Elections is { } election)
        {
            if (election.TransferOnDay >= c.ElapsedDays)
                result.Add(new("election_handover", CampaignAlertGroup.Must, Loc.Get("Election result awaits a response"),
                    Loc.Format("Winner: {0}. Handover in {1} day(s).", election.Winner, election.TransferOnDay - c.ElapsedDays), CampaignAlertRoute.Elections));
            else if (!election.Suspended && n.NextElectionDay >= c.ElapsedDays && n.NextElectionDay - c.ElapsedDays <= 90)
                result.Add(new("election_calendar", CampaignAlertGroup.Important, Loc.Get("Election approaching"),
                    Loc.Format("Next election in {0} day(s)", n.NextElectionDay - c.ElapsedDays), CampaignAlertRoute.Elections));
        }
        if (c.FeatureEnabled("research") && string.IsNullOrEmpty(n.CurrentResearch) && ResearchSystem.AvailableTo(n).Any())
            result.Add(new("research_idle", Delegation.Holds(n, Delegation.Research) ? CampaignAlertGroup.Important : CampaignAlertGroup.Should,
                Loc.Get("No current research"), Loc.Get(Delegation.Holds(n, Delegation.Research)
                    ? "The research ministry will select a project on its next weekly review. You may choose one now."
                    : "Research is under manual control. Choose a technology or delegate the ministry."), CampaignAlertRoute.Research));
        if (c.Defines.RulersEnabled && c.FeatureEnabled("rulers") && Rulers.AllPortfolios.Any(p => !n.Cabinet.ContainsKey(p.Id)))
            result.Add(new("cabinet_vacancies", CampaignAlertGroup.Could, Loc.Get("Cabinet vacancies"),
                Loc.Get("Review candidates, effects and appointment costs, or delegate personnel."), CampaignAlertRoute.Personnel));
        if (MechanicFeatures.ScreenEnabled(c, "Decisions"))
        {
            var count = DecisionCatalog.All.Count(d => DecisionCatalog.BlockReason(c, n, d).Length == 0);
            if (count > 0) result.Add(new("available_decisions", CampaignAlertGroup.Could, Loc.Format("Decisions: {0}", count),
                Loc.Get("National decisions are available."), CampaignAlertRoute.Decisions));
        }
        if (n.PoliticalPoints >= 60f)
            result.Add(new("unspent_pp", CampaignAlertGroup.Could, Loc.Get("Spend political points"),
                Loc.Get("Surplus political points can be invested in national decisions, reforms, appointments, or diplomatic efforts."), CampaignAlertRoute.Decisions));
        if (n.ArmyExperience >= 35f)
            result.Add(new("unspent_army_xp", CampaignAlertGroup.Could, Loc.Get("Spend military experience"),
                Loc.Get("Accumulated combat experience is ready to be used for doctrines, maneuvers, and army reforms."), CampaignAlertRoute.Doctrine));
        if (c.Wars.Any(w => !w.Ended && w.Involves(n.Id)) && c.Armies.Any(a => a.NationId == n.Id && a.FrontLineId < 0 && a.AssignedWarId < 0 && !a.DirectOrder && !a.Training && !a.InBattle))
            result.Add(new("unassigned_divisions", CampaignAlertGroup.Must, Loc.Get("Unassigned divisions at war"),
                Loc.Get("Frontline divisions lack sector assignments while the country is at war."), CampaignAlertRoute.Army));
        var strained = c.Relations.Values.FirstOrDefault(r => (r.A == n.Id || r.B == n.Id) && r.Stance == DiplomaticStance.Allied && r.AllianceStrainedDays > 0);
        if (strained is not null)
            result.Add(new("alliance_strained", CampaignAlertGroup.Should, Loc.Get("Alliance cohesion strained"),
                Loc.Get("An alliance is nearing its breaking point. Reaffirm mutual commitments to prevent collapse."), CampaignAlertRoute.Situation));
        if (c.FeatureEnabled("public_debt") && n.DebtCondition is DebtCondition.Stressed or DebtCondition.Insolvent or DebtCondition.Default)
            result.Add(new("debt_distress", CampaignAlertGroup.Must, Loc.Get("Sovereign debt distress"),
                Loc.Get("Interest and bond payments are severely straining the state budget."), CampaignAlertRoute.Situation));
        foreach (var war in c.Wars.Where(w => !w.Ended && w.Involves(n.Id)).OrderBy(w => w.Id).Take(6))
            result.Add(new("war:" + war.Id, CampaignAlertGroup.Important, Loc.Get("War in progress"),
                Loc.Get("Review the front, war score and peace terms."), CampaignAlertRoute.War, war.Id));
        var battle = c.Battles.FirstOrDefault(b => !b.Ended && (b.AttackerId == n.Id || b.DefenderId == n.Id));
        if (battle is not null) result.Add(new("active_battles", CampaignAlertGroup.Important, Loc.Get("Active battles"),
            Loc.Get("Active battles involving your country."), CampaignAlertRoute.Battle, battle.Id));
        return result.OrderBy(a => a.Group).ThenBy(a => a.Id, StringComparer.Ordinal).Take(64).ToArray();
    }
}

/// <summary>Not campaign/save/network state. Clear when the campaign/seat changes.
/// Escalation or disappearance cancels a snooze; translation and changing totals do not.</summary>
public sealed class CampaignAlertSnoozes
{
    private readonly Dictionary<string, (CampaignAlertGroup Group, int Until)> _hidden = new(StringComparer.Ordinal);
    public int Count => _hidden.Count;
    public void Clear() => _hidden.Clear();
    public void Hide(CampaignAlert alert, int day, int days) => _hidden[alert.Id] =
        (alert.Group, (int)Math.Min(int.MaxValue, (long)day + Math.Clamp(days, 1, alert.Group == CampaignAlertGroup.Must ? 1 : 30)));
    public bool Hidden(CampaignAlert alert, int day) => _hidden.TryGetValue(alert.Id, out var h) &&
        h.Until > day && h.Group <= alert.Group;
    public void Reconcile(IReadOnlyList<CampaignAlert> current, int day)
    {
        var live = current.ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var id in _hidden.Keys.Where(id => !live.TryGetValue(id, out var a) || !Hidden(a, day)).ToArray()) _hidden.Remove(id);
    }
}
