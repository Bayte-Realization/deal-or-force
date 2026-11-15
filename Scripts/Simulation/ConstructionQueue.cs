using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// Weeks of the queued construction bill the army staff leaves in the treasury when it raises a
    /// division on a government's behalf (Attrition.RebuildTheLost). See common/defines/93_construction_queue.json.
    /// </summary>
    public float ArmyStaffConstructionReserveWeeks { get; set; } = 4f;

    /// <summary>Sites every country works at once however little industry it has (ConstructionCost.WorkingSites).</summary>
    public float ConstructionSitesBase { get; set; } = 2f;

    /// <summary>More sites worked at once for each civilian factory standing: 0.25 is one more per four.</summary>
    public float ConstructionSitesPerCivilianIndustry { get; set; } = 0.25f;

    /// <summary>The most sites any country works at once.</summary>
    public int ConstructionSitesMax { get; set; } = 30;

    /// <summary>Every catalogue price times this (ConstructionCost.Of). 1 is the catalogue as written; 0.6 since 15 November 2026.</summary>
    public float ConstructionCostScale { get; set; } = 0.6f;

    /// <summary>Every catalogue build time times this (ConstructionCost.WorkDays). 1 is the catalogue as written; 0.75 since 15 November 2026.</summary>
    public float ConstructionTimeScale { get; set; } = 0.75f;

    /// <summary>
    /// The most construction may draw from the treasury in a week, as a share of the week's budget
    /// income (ConstructionQueue.WeeklyCeiling). Sites past it wait for the next day's money and say
    /// so. 0 or less: no ceiling, construction is paid until the treasury is empty. The first slice
    /// of the budget's planned "Construction" slider (docs/CONSTRUCTION_ECONOMY_REWORK_RU.md).
    /// </summary>
    public float ConstructionBudgetShare { get; set; } = 0.35f;

    /// <summary>
    /// The ceiling also lets construction draw on savings: this many weeks to spend the treasury,
    /// so a week may take one part in this many of it on top of the income share. A country with
    /// money in the bank builds with it - Albania's 0.75 at 35 % of a 0.014 income alone built an
    /// arms plant at a seventh of its speed - and, a fraction of what is left each week, the
    /// ceiling can never empty the treasury. 0: the income share alone.
    /// </summary>
    public float ConstructionTreasuryDrawWeeks { get; set; } = 26f;
}

/// <summary>
/// What each construction site did today, and when the ones still waiting can be expected to
/// start and finish.
///
/// A player in Albania queued one of everything on the first morning and a year later had nothing:
/// the treasury ran dry on day 47 and every site stopped, and the only thing on the screen was the
/// first site's "17 %". ConstructionSystem now writes on every site why it did not work
/// (<see cref="ConstructionProject.Waiting"/>), and this reads it back - for the lists, for the
/// alert, and for the market, which should only buy materials for the sites that are worked.
/// </summary>
public static class ConstructionQueue
{
    /// <summary>Worked today.</summary>
    public const string Working = "working";
    /// <summary>The treasury could not pay today's share of this site, so it and every newer one waited.</summary>
    public const string Treasury = "treasury";
    /// <summary>Waiting for a free site: the country works only so many at once (ConstructionCost.WorkingSites), oldest first.</summary>
    public const string Queued = "queued";
    /// <summary>A site whose accounts are not numbers; nothing behind it works.</summary>
    public const string Invalid = "invalid";
    /// <summary>
    /// One of the sites worked at once, but today's share of the week's construction ceiling
    /// (ConstructionBudgetShare) was spent on the older sites: it waits for tomorrow's money.
    /// </summary>
    public const string Budget = "budget";

    /// <summary>Called by ConstructionSystem before a country's sites work: every site waits until the pass says otherwise.</summary>
    internal static void Reset(List<ConstructionProject> sites)
    {
        foreach (var site in sites) { site.Waiting = Queued; site.WaitingBehind = -1; }
    }

    /// <summary>Called by ConstructionSystem after a country's sites worked: whom each waiting site waits behind.</summary>
    internal static void Settle(Campaign campaign, List<ConstructionProject> sites)
    {
        ConstructionProject blocked = null, ahead = null;
        foreach (var site in sites)
        {
            if (site.Waiting == Treasury)
            {
                blocked = site;
                if (site.StalledSinceDay < 0) site.StalledSinceDay = campaign.ElapsedDays;
            }
            else if (site.Waiting == Working) site.StalledSinceDay = -1;
            else if (site.Waiting == Queued) site.WaitingBehind = (blocked ?? ahead)?.Id ?? -1;
            if (site.Waiting is Working or Treasury or Budget) ahead = site;
        }
    }

    /// <summary>Whether the site was worked on the last construction day - what the market buys materials for.</summary>
    public static bool WorkedToday(ConstructionProject project) => project?.Waiting == Working;

    /// <summary>
    /// A country has sites and none of them is working, because the first could not be paid. The
    /// alert's condition: every site frozen for lack of money, not a queue that is simply long.
    /// Judged from the sites the last construction day looked at: one queued since then ("") has not
    /// been tried yet and will wait behind the unpaid site like the rest, so it must not lift the
    /// freeze - it used to hide the alert and bring back the dates until the next day.
    /// </summary>
    public static bool FrozenForMoney(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return false;
        var any = false;
        foreach (var project in campaign.Construction)
        {
            if (project.NationId != nation.Id) continue;
            if (project.Waiting == Working) return false;
            if (project.Waiting == Treasury) any = true;
        }
        return any;
    }

    /// <summary>What the country's open sites still cost in total, outstanding contract by contract.</summary>
    public static float Outstanding(Campaign campaign, Nation nation)
    {
        var total = 0f;
        if (campaign is null || nation is null) return 0f;
        foreach (var project in campaign.Construction)
            if (project.NationId == nation.Id && float.IsFinite(project.Cost) && float.IsFinite(project.Paid))
                total += MathF.Max(0f, project.Cost - project.Paid);
        return total;
    }

    /// <summary>
    /// What the sites the country works at once will be paid over the next <paramref name="weeks"/>
    /// weeks, at today's pace and within the weekly ceiling: the reserve the army staff leaves the
    /// builders. Never more than the sites still owe.
    /// </summary>
    public static float Bill(Campaign campaign, Nation nation, float weeks)
    {
        if (campaign is null || nation is null || !(weeks > 0f)) return 0f;
        return MathF.Min(Outstanding(campaign, nation), WeeklyBill(campaign, nation) * weeks);
    }

    /// <summary>
    /// What the sites worked at once cost a week at today's pace, before the ceiling. Only sites
    /// with money still owed count.
    /// </summary>
    public static float WeeklyDue(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return 0f;
        var sites = Sites(campaign, nation);
        var slots = ConstructionCost.WorkingSites(campaign, nation);
        var pace = ConstructionCost.Pace(campaign, nation);
        var daily = 0f;
        for (var i = 0; i < sites.Count && i < slots; i++)
        {
            var site = sites[i];
            if (!float.IsFinite(site.Cost) || site.Cost <= 0f || !float.IsFinite(site.Paid) || site.Paid >= site.Cost) continue;
            daily += site.Cost / Math.Max(1, site.DurationDays) * pace;
        }
        return daily * 7f;
    }

    /// <summary>What the sites worked at once will draw a week: their cost, held to the ceiling.</summary>
    public static float WeeklyBill(Campaign campaign, Nation nation) =>
        MathF.Min(WeeklyDue(campaign, nation), WeeklyCeiling(campaign, nation));

    /// <summary>
    /// What the budget lines raised in the last closed week - taxes and the other revenue lines, the
    /// income the ledger shows. Before the first week closes, the budget as it stands today.
    /// </summary>
    public static float WeeklyIncome(Campaign campaign, Nation nation)
    {
        if (nation is null) return 0f;
        var income = 0f;
        var lines = BudgetCore.All;
        var closed = nation.LedgerBudgetLastWeek;
        if (closed is not null && closed.Length == lines.Count)
        {
            foreach (var amount in closed) if (amount > 0f && float.IsFinite(amount)) income += amount;
            return income;
        }
        foreach (var line in lines)
        {
            if (line.Side != BudgetSide.Revenue) continue;
            var amount = BudgetCore.Amount(nation, line, nation.Gdp / 52f, false);
            if (amount > 0f && float.IsFinite(amount)) income += amount;
        }
        return income;
    }

    /// <summary>
    /// The most construction may draw in a week: ConstructionBudgetShare of the week's income, plus
    /// one part in ConstructionTreasuryDrawWeeks of the treasury. Infinity when there is no ceiling:
    /// the share is 0 or less, or the country has no budget income to take a share of (a movement,
    /// a test fixture, a state whose books are not yet drawn up) - then the treasury alone decides,
    /// and an empty one still stops the sites.
    /// </summary>
    public static float WeeklyCeiling(Campaign campaign, Nation nation)
    {
        var share = campaign?.Defines?.ConstructionBudgetShare ?? 0f;
        if (!float.IsFinite(share) || share <= 0f || nation is null) return float.PositiveInfinity;
        var income = WeeklyIncome(campaign, nation);
        if (!(income > 0f) || !float.IsFinite(income)) return float.PositiveInfinity;
        return share * income + SavingsDraw(campaign, nation);
    }

    /// <summary>What the ceiling lets construction take from savings this week (ConstructionTreasuryDrawWeeks).</summary>
    public static float SavingsDraw(Campaign campaign, Nation nation)
    {
        var weeks = campaign?.Defines?.ConstructionTreasuryDrawWeeks ?? 0f;
        if (nation is null || !float.IsFinite(weeks) || weeks <= 0f || !float.IsFinite(nation.Treasury)) return 0f;
        return MathF.Max(0f, nation.Treasury) / weeks;
    }

    /// <summary>A country's open sites, oldest first - the order they work in.</summary>
    public static List<ConstructionProject> Sites(Campaign campaign, Nation nation)
    {
        var sites = new List<ConstructionProject>();
        if (campaign is null || nation is null) return sites;
        foreach (var project in campaign.Construction) if (project.NationId == nation.Id) sites.Add(project);
        sites.Sort((a, b) => a.Id.CompareTo(b.Id));
        return sites;
    }

    /// <summary>When a site can be expected to start and to finish, in days from today; -1 when it cannot be said.</summary>
    public readonly record struct Estimate(ConstructionProject Project, int StartDays, int FinishDays, bool Funded);

    /// <summary>
    /// The queue played forward: the oldest <c>WorkingSites</c> sites work, and the next in line takes
    /// the first slot that frees - exactly the rule ConstructionSystem follows - at today's pace. Money
    /// is not forecast: while the first site cannot be paid nothing can be promised (Funded false,
    /// days -1), because the queue does not move until money comes.
    /// </summary>
    public static List<Estimate> Schedule(Campaign campaign, Nation nation)
    {
        var result = new List<Estimate>();
        var sites = Sites(campaign, nation);
        if (sites.Count == 0) return result;
        var funded = !FrozenForMoney(campaign, nation);
        // A ceiling below what the worked sites cost slows them all by the same share: an estimate,
        // where the system pays the oldest first in full and the newest last.
        var due = WeeklyDue(campaign, nation);
        var ceiling = WeeklyCeiling(campaign, nation);
        var paid = due > 0f && ceiling < due ? MathF.Max(0.02f, ceiling / due) : 1f;
        var pace = MathF.Max(0.01f, ConstructionCost.Pace(campaign, nation) * paid);
        var slots = new float[Math.Max(1, ConstructionCost.WorkingSites(campaign, nation))];
        foreach (var site in sites)
        {
            if (!funded) { result.Add(new Estimate(site, -1, -1, false)); continue; }
            var free = 0;
            for (var i = 1; i < slots.Length; i++) if (slots[i] < slots[free]) free = i;
            var start = slots[free];
            var remaining = MathF.Max(0f, site.DurationDays - (float.IsFinite(site.ProgressDays) ? site.ProgressDays : 0f)) / pace;
            slots[free] = start + remaining;
            result.Add(new Estimate(site, (int)MathF.Ceiling(start - 0.001f), (int)MathF.Ceiling(start + remaining - 0.001f), true));
        }
        return result;
    }

    /// <summary>The site's state in a sentence, for the lists.</summary>
    public static string Describe(Campaign campaign, ConstructionProject project)
    {
        if (project is null) return "";
        switch (project.Waiting)
        {
            case Working: return Loc.Get("Working");
            case Treasury: return Loc.Get("Stopped: the treasury cannot pay it");
            case Invalid: return Loc.Get("Stopped: its accounts are invalid");
            case Budget: return Loc.Get("Waiting: this week's construction budget went to the older sites");
            case Queued:
                var ahead = campaign?.Construction.Find(other => other.Id == project.WaitingBehind);
                if (ahead?.Waiting == Treasury)
                    return Loc.Format("Waiting: the treasury cannot pay {0} ahead of it", Loc.Get(BuildingCatalog.Get(ahead.Building).Name));
                return ahead is null ? Loc.Get("Waiting for a free site")
                    : Loc.Format("Waiting for a free site, behind {0}", Loc.Get(BuildingCatalog.Get(ahead.Building).Name));
            default:
                // Not tried yet. An older site the treasury could not pay stops every newer one, so
                // this one will not start either: say what it will wait behind.
                var unpaid = campaign?.Construction.Find(other => other.NationId == project.NationId &&
                    other.Id < project.Id && other.Waiting == Treasury);
                return unpaid is not null
                    ? Loc.Format("Waiting: the treasury cannot pay {0} ahead of it", Loc.Get(BuildingCatalog.Get(unpaid.Building).Name))
                    : Loc.Get("Starts on the next construction day");
        }
    }

    /// <summary>
    /// The country's building in one line, for the head of every list where sites are queued: how
    /// many it works at once, what they cost a week, and that against the week's income.
    /// </summary>
    public static string Summary(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return "";
        var slots = ConstructionCost.WorkingSites(campaign, nation);
        var open = 0;
        foreach (var project in campaign.Construction) if (project.NationId == nation.Id) open++;
        var bill = WeeklyBill(campaign, nation);
        var income = WeeklyIncome(campaign, nation);
        var ceiling = WeeklyCeiling(campaign, nation);
        var text = Loc.Format("Building now: {0} a week, {1} of income · {2} of {3} open sites worked at once",
            Fmt.Money(bill), income > 0f ? Fmt.Percent(bill / income) : "—", Math.Min(open, slots), open);
        if (float.IsFinite(ceiling))
            text += " · " + (campaign.Defines.ConstructionTreasuryDrawWeeks > 0f
                ? Loc.Format("ceiling {0} a week: {1} of income and 1/{2} of the treasury", Fmt.Money(ceiling),
                    Fmt.Percent(campaign.Defines.ConstructionBudgetShare), MathF.Round(campaign.Defines.ConstructionTreasuryDrawWeeks))
                : Loc.Format("ceiling {0} a week ({1} of income)", Fmt.Money(ceiling),
                    Fmt.Percent(campaign.Defines.ConstructionBudgetShare)));
        return text;
    }

    /// <summary>The estimate in a sentence, for the lists.</summary>
    public static string When(Estimate estimate)
    {
        if (!estimate.Funded) return Loc.Get("No date while the treasury is empty");
        return estimate.StartDays <= 0
            ? Loc.Format("Done in about {0} days", estimate.FinishDays)
            : Loc.Format("Starts in about {0} days, done in about {1}", estimate.StartDays, estimate.FinishDays);
    }
}
