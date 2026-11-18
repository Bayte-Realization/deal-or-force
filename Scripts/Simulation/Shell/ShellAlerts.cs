using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation.Shell;

public enum ShellAlertSeverity
{
    Critical = 0,
    Warning = 1,
    Important = 2,
    Opportunity = 3,
    NextHint = 4
}

public sealed record ShellAlertEntry(
    string Id,
    string TopicKey,
    string Title,
    string Summary,
    ShellAlertSeverity Severity,
    CampaignAlertRoute Route,
    int TargetId,
    int DaysRemaining,
    string DeadlineLabel,
    bool IsViewed,
    bool Snoozable,
    bool IsNextHint);

public sealed record ShellOutlinerRow(
    string Id,
    string TopicKey,
    string Title,
    string Value,
    ShellAlertSeverity Severity,
    string RouteKind,
    int TargetId);

public sealed record ShellOutlinerGroup(
    string TopicKey,
    string Title,
    IReadOnlyList<ShellOutlinerRow> Rows);

public sealed record ShellAlertSnapshot(
    IReadOnlyList<ShellAlertEntry> Alerts,
    int HiddenAlertCount,
    IReadOnlyList<ShellOutlinerGroup> OutlinerGroups,
    bool NextHintShown);

/// <summary>
/// Unified alert + "Watching" outliner pipeline enforcing Rules 1-7:
/// single source of obligations, no top-bar / DEFCON duplication, deadline-first sorting,
/// non-truncating deadline labels, viewed checkmarks, "Next" strictly last in opening days,
/// max 4 rows folded / 3 open outliner, and zero empty outliner groups or duplicated topics.
/// </summary>
public static class ShellAlerts
{
    public static ShellAlertSnapshot BuildSnapshot(
        Campaign campaign,
        Nation nation,
        bool outlinerOpen,
        bool nextHintsEnabled,
        bool observer = false,
        IReadOnlySet<string> viewedIds = null,
        CampaignAlertSnoozes snoozes = null,
        IReadOnlySet<Commodity> watchedGoods = null)
    {
        if (campaign is null || nation is null || observer)
            return new(Array.Empty<ShellAlertEntry>(), 0, Array.Empty<ShellOutlinerGroup>(), false);

        var maxAlerts = outlinerOpen
            ? (int)InterfaceSkin.Metric("shell.alerts_max_outliner", 3f)
            : (int)InterfaceSkin.Metric("shell.alerts_max_folded", 4f);
        if (maxAlerts < 1) maxAlerts = 1;

        var raw = CampaignAlerts.Read(campaign, nation, observer: false);
        snoozes?.Reconcile(raw, campaign.ElapsedDays);

        var candidates = new List<ShellAlertEntry>(raw.Count + 1);
        var seenTopics = new HashSet<string>(StringComparer.Ordinal);

        foreach (var alert in raw)
        {
            if (snoozes is not null && snoozes.Hidden(alert, campaign.ElapsedDays))
                continue;

            // Rule 2: no duplicate DEFCON/World overview bar (lives on DatePlate World strip)
            var topic = TopicOfAlert(alert);
            if (string.Equals(topic, "world_strip", StringComparison.Ordinal))
                continue;
            if (!seenTopics.Add(topic))
                continue;

            var daysLeft = EstimateDaysRemaining(campaign, nation, alert);
            var sev = MapSeverity(alert.Group);
            var deadline = FormatDeadline(daysLeft);
            var viewed = viewedIds is not null && viewedIds.Contains(alert.Id);
            candidates.Add(new ShellAlertEntry(
                alert.Id,
                topic,
                alert.Title,
                alert.Summary,
                sev,
                alert.Route,
                alert.TargetId,
                daysLeft,
                deadline,
                viewed,
                Snoozable: alert.Group != CampaignAlertGroup.Must,
                IsNextHint: false));
        }

        // Rule 3: dated deadlines before undated of comparable importance, nearest expiry first
        candidates.Sort((a, b) =>
        {
            var aDated = a.DaysRemaining >= 0;
            var bDated = b.DaysRemaining >= 0;
            if (aDated != bDated)
            {
                if (a.Severity <= ShellAlertSeverity.Warning || b.Severity <= ShellAlertSeverity.Warning)
                {
                    if (a.Severity != b.Severity && (a.Severity == ShellAlertSeverity.Critical || b.Severity == ShellAlertSeverity.Critical))
                        return a.Severity.CompareTo(b.Severity);
                    return aDated ? -1 : 1;
                }
                return aDated ? -1 : 1;
            }
            if (aDated && bDated && a.DaysRemaining != b.DaysRemaining)
                return a.DaysRemaining.CompareTo(b.DaysRemaining);
            var bySev = a.Severity.CompareTo(b.Severity);
            return bySev != 0 ? bySev : string.CompareOrdinal(a.Id, b.Id);
        });

        // Rule 5: optional early-game "Next" guidance goes strictly LAST and only within first 60 days
        ShellAlertEntry nextEntry = null;
        var nextCutoff = (int)InterfaceSkin.Metric("shell.next_hint_days", 60f);
        if (nextHintsEnabled && campaign.ElapsedDays < nextCutoff)
            nextEntry = BuildEarlyGameNextHint(campaign, nation);

        var shown = new List<ShellAlertEntry>(maxAlerts);
        var regularLimit = nextEntry is not null && maxAlerts > 1 ? maxAlerts - 1 : maxAlerts;
        foreach (var item in candidates)
        {
            if (shown.Count >= (nextEntry is not null && candidates.Count <= regularLimit ? regularLimit : maxAlerts))
                break;
            shown.Add(item);
        }

        var nextAdded = false;
        if (nextEntry is not null && shown.Count < maxAlerts && !shown.Any(a => a.TopicKey == nextEntry.TopicKey))
        {
            shown.Add(nextEntry);
            nextAdded = true;
        }

        var hiddenCount = Math.Max(0, candidates.Count - (shown.Count - (nextAdded ? 1 : 0)));

        // Rule 7: Outliner contains ONLY non-empty live groups whose topics don't repeat active alerts above
        var alertTopics = new HashSet<string>(shown.Select(s => s.TopicKey), StringComparer.Ordinal);
        var outlinerGroups = BuildOutlinerGroups(campaign, nation, alertTopics, watchedGoods);

        return new ShellAlertSnapshot(shown, hiddenCount, outlinerGroups, nextAdded);
    }

    public static string TopicOfAlert(CampaignAlert alert)
    {
        if (alert.Id.StartsWith("war:", StringComparison.Ordinal)) return alert.Id;
        if (alert.Id == "active_battles") return "battles";
        if (alert.Id == "research_idle") return "research";
        if (alert.Id == "construction_frozen") return "construction";
        if (alert.Id is "world_overview" or "defcon_status") return "world_strip";
        if (alert.Id.StartsWith("event:", StringComparison.Ordinal)) return alert.Id;
        if (alert.Id is "peace_offer" || alert.Situation?.Route == SituationRoute.PeaceOffer) return "peace_offer";
        return alert.Id;
    }

    private static ShellAlertSeverity MapSeverity(CampaignAlertGroup group) => group switch
    {
        CampaignAlertGroup.Must => ShellAlertSeverity.Critical,
        CampaignAlertGroup.Should => ShellAlertSeverity.Warning,
        CampaignAlertGroup.Important => ShellAlertSeverity.Important,
        _ => ShellAlertSeverity.Opportunity
    };

    private static int EstimateDaysRemaining(Campaign campaign, Nation nation, CampaignAlert alert)
    {
        if (alert.Situation is { DeadlineDay: >= 0 } situation)
            return Math.Max(0, situation.DeadlineDay - campaign.ElapsedDays);

        if (alert.Id.StartsWith("event:", StringComparison.Ordinal))
        {
            var idText = alert.Id["event:".Length..];
            if (int.TryParse(idText, out var eventId))
            {
                var ev = campaign.PendingEvents.FirstOrDefault(e => e.NationId == nation.Id && e.Id == eventId);
                if (ev is not null && ev.ExpiresDay >= 0)
                    return Math.Max(0, ev.ExpiresDay - campaign.ElapsedDays);
            }
        }

        if (alert.Id == "nuclear_struck" && nation.NuclearStruckDay >= 0)
            return Math.Max(0, campaign.Defines.NuclearRetaliationDays - (campaign.ElapsedDays - nation.NuclearStruckDay));

        if (alert.Id == "election_handover" && nation.Elections is { } election && election.TransferOnDay >= campaign.ElapsedDays)
            return Math.Max(0, election.TransferOnDay - campaign.ElapsedDays);

        if (alert.Id == "election_calendar" && nation.NextElectionDay >= campaign.ElapsedDays)
            return Math.Max(0, nation.NextElectionDay - campaign.ElapsedDays);

        return -1;
    }

    public static string FormatDeadline(int daysRemaining)
    {
        if (daysRemaining < 0) return "";
        if (daysRemaining == 0) return Loc.Get("Now");
        var unit = Loc.Get("shell.day_suffix");
        return $"{daysRemaining}{unit}";
    }

    private static ShellAlertEntry BuildEarlyGameNextHint(Campaign campaign, Nation nation)
    {
        if (campaign.FeatureEnabled("research") && string.IsNullOrEmpty(nation.CurrentResearch) && ResearchSystem.AvailableTo(nation).Any())
            return new("next:research", "next_hint", Loc.Get("Next: Choose research"),
                Loc.Get("Pick an opening research priority for your state."), ShellAlertSeverity.NextHint,
                CampaignAlertRoute.Research, -1, -1, "", false, false, true);

        if (ConstructionQueue.Sites(campaign, nation).Count == 0 && nation.Treasury > 0)
            return new("next:build", "next_hint", Loc.Get("Next: Start construction"),
                Loc.Get("Commission an economic or infrastructure site in Economy."), ShellAlertSeverity.NextHint,
                CampaignAlertRoute.Decisions, -1, -1, "", false, false, true);

        return new("next:time", "next_hint", Loc.Get("Next: Advance time [Space]"),
            Loc.Get("Unpause time when your opening plans are ready."), ShellAlertSeverity.NextHint,
            CampaignAlertRoute.Situation, -1, -1, "", false, false, true);
    }

    private static IReadOnlyList<ShellOutlinerGroup> BuildOutlinerGroups(
        Campaign campaign,
        Nation nation,
        HashSet<string> activeAlertTopics,
        IReadOnlySet<Commodity> watchedGoods)
    {
        var groups = new List<ShellOutlinerGroup>();

        // Wars group (deduplicated against "war:<id>" alert rows)
        var warRows = new List<ShellOutlinerRow>();
        foreach (var war in Warfare.OpenWars(campaign).Where(w => !w.Ended && w.Involves(nation.Id)).OrderBy(w => w.Id).Take(4))
        {
            var topic = "war:" + war.Id;
            if (activeAlertTopics.Contains(topic)) continue;
            var enemyId = war.EnemiesOf(nation.Id).FirstOrDefault();
            var enemyName = campaign.NationById(enemyId)?.DisplayName ?? Loc.Get("Enemy");
            var score = (int)Math.Round(war.IsAttacking(nation.Id) ? war.WarScore : -war.WarScore);
            warRows.Add(new("outliner:" + topic, topic, enemyName, $"{score:+#;-#;0}%",
                ShellAlertSeverity.Important, "War", war.Id));
        }
        if (warRows.Count > 0)
            groups.Add(new("wars", Loc.Get("Wars"), warRows));

        // Active Battles group (skip if alert row already tracks battles)
        if (!activeAlertTopics.Contains("battles"))
        {
            var battleRows = new List<ShellOutlinerRow>();
            foreach (var battle in campaign.Battles.Where(b => !b.Ended && (b.AttackerId == nation.Id || b.DefenderId == nation.Id)).Take(3))
            {
                var side = battle.AttackerId == nation.Id ? battle.DefenderId : battle.AttackerId;
                var other = campaign.NationById(side)?.DisplayName ?? Loc.Get("Front");
                battleRows.Add(new("outliner:battle:" + battle.Id, "battles", other, Loc.Get("Live"),
                    ShellAlertSeverity.Warning, "Battle", battle.Id));
            }
            if (battleRows.Count > 0)
                groups.Add(new("battles", Loc.Get("Battles"), battleRows));
        }

        // Construction group (only if non-empty & not already flagged as frozen in top alerts)
        var sites = ConstructionQueue.Sites(campaign, nation);
        if (sites.Count > 0 && !activeAlertTopics.Contains("construction"))
        {
            var lead = sites[0];
            var siteName = Loc.Get(lead.Building.ToString());
            var rows = new List<ShellOutlinerRow>
            {
                new("outliner:construction:lead", "construction",
                    sites.Count > 1 ? $"{siteName} x{sites.Count}" : siteName,
                    $"{Math.Clamp(lead.Progress * 100f, 0f, 100f):0}%",
                    ShellAlertSeverity.Important, "Economy", -1)
            };
            groups.Add(new("construction", Loc.Get("Construction"), rows));
        }

        // Active Research (only when actually researching something)
        if (campaign.FeatureEnabled("research") && !string.IsNullOrEmpty(nation.CurrentResearch) && !activeAlertTopics.Contains("research"))
        {
            var tech = TechnologyCatalog.Of(nation.CurrentResearch);
            var techTitle = tech is not null ? Loc.Get(tech.Name) : nation.CurrentResearch;
            var invested = nation.ResearchProgress.GetValueOrDefault(nation.CurrentResearch);
            var pct = tech is not null && tech.Cost > 0f
                ? Math.Clamp(invested / tech.Cost * 100f, 0f, 100f)
                : 0f;
            groups.Add(new("research", Loc.Get("Research"), new[]
            {
                new ShellOutlinerRow("outliner:research", "research", techTitle, $"{pct:0}%",
                    ShellAlertSeverity.Opportunity, "Research", -1)
            }));
        }

        // Watchlist commodities (only if player starred >= 1 commodity)
        if (watchedGoods is { Count: > 0 })
        {
            var marketRows = new List<ShellOutlinerRow>();
            foreach (var good in watchedGoods.Take(4))
            {
                if (!nation.Market.TryGetValue(good, out var quote)) continue;
                var def = CommodityCatalog.Get(good);
                marketRows.Add(new("outliner:good:" + good, "watchlist:" + good,
                    Loc.Get(def.Name), $"{Math.Clamp(quote.Satisfaction * 100f, 0f, 100f):0}%",
                    quote.Satisfaction < 0.7f ? ShellAlertSeverity.Warning : ShellAlertSeverity.Opportunity,
                    "Trade", (int)good));
            }
            if (marketRows.Count > 0)
                groups.Add(new("watchlist", Loc.Get("Watching"), marketRows));
        }

        return groups;
    }
}
