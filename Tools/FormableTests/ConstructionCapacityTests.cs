using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Construction made cheaper, faster and parallel (15 November 2026).
///
/// A friend playing from 1 January 2024: "the flasks are green and the money flies away", and the
/// owner: building should be cheaper, faster, and more of it at once. Measured headlessly first
/// (SimBench --construction, tmp_logs/construction): one site at a time for every economy under 17
/// units of weekly output - Italy, France, Germany and Poland among them - seventy per cent of the
/// world's open sites standing in line, and the payments hidden in one ledger line with rebuilding
/// and capital moves. Pinned here:
///
///   1. how many sites work at once follows the civilian factories standing, by one rule for all;
///   2. those sites really work at once, and finish together;
///   3. the catalogue's price and time scales reach the contract, the list and the AI alike;
///   4. a week's construction is held to a share of the week's income, and a site past it says why;
///   5. a site paid part of its day does part of a day's work, so no date is promised early;
///   6. the sites are their own line in the ledger, with the queue's weekly cost beside them.
/// </summary>
static class ConstructionCapacityTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("construction capacity: " + label); Console.WriteLine("PASS construction capacity: " + label); }

    public static void Run()
    {
        SitesFollowCivilianIndustry();
        SitesWorkTogetherAndFinishTogether();
        PriceAndTimeScalesReachEveryReader();
        TheCeilingHoldsAndSaysWhy();
        WorkFollowsTheMoney();
        TheSitesHaveTheirOwnLedgerLine();
        TheDefinesFileCarriesTheRebalance();
    }

    static (Campaign C, Nation N, ScenarioState S) World(int seed, int civilian = 0, float treasury = 100f, bool player = true)
    {
        var c = new Campaign(seed);
        c.Defines.BuildingDepreciationPerYear = 0f;
        c.Defines.ConstructionBudgetShare = 0f;
        var n = c.AddNation(new Nation { Id = 1, MapIndex = 1, Tag = "TST", Name = "Test", Gdp = 15, Treasury = treasury, ConstructionCapacity = 1f });
        var s = new ScenarioState { Id = "site", MapIndex = 1, NationMapIndex = 1, Population = 1_000_000 };
        if (civilian > 0) s.Buildings[BuildingKind.CivilianIndustry] = civilian;
        c.States.Add(s);
        c.InvalidateStateIndex();
        if (player) c.ConfigureSinglePlayer(n.Id);
        return (c, n, s);
    }

    static ConstructionProject Site(Campaign c, Nation n, int id, float cost = 1f, int days = 100) =>
        new() { Id = id, NationId = n.Id, StateMapIndex = 1, Building = BuildingKind.Agriculture, Cost = cost, DurationDays = days };

    static void TheDefinesFileCarriesTheRebalance()
    {
        var path = Path.Combine(GameDataLoader.DataFolder(), "common", "defines", "93_construction_queue.json");
        using var defines = JsonDocument.Parse(File.ReadAllText(path), GameDataLoader.ForgivingJson);
        var root = defines.RootElement;
        foreach (var key in new[] { "ConstructionSitesBase", "ConstructionSitesPerCivilianIndustry", "ConstructionSitesMax",
                     "ConstructionCostScale", "ConstructionTimeScale", "ConstructionBudgetShare" })
            Check(root.TryGetProperty(key, out _), "the defines file carries " + key);
        var shipped = new Campaign(1500).Defines;
        Check(shipped.ConstructionCostScale < 1f && shipped.ConstructionTimeScale < 1f,
            $"building is cheaper and faster than the catalogue as written ({shipped.ConstructionCostScale}, {shipped.ConstructionTimeScale})");
        Check(shipped.ConstructionSitesBase >= 2f, "every country works at least two sites at once");
        foreach (var file in new[] { Path.Combine("localization", "english", "zz_331_construction_capacity.json"),
                     Path.Combine("localization", "russian", "zz_331_construction_capacity.json") })
        {
            using var table = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(), file)), GameDataLoader.ForgivingJson);
            Check(table.RootElement.TryGetProperty("Building sites", out _), "the table parses and names the ledger line: " + file);
        }
    }

    static void SitesFollowCivilianIndustry()
    {
        var (c, n, s) = World(1501);
        c.Defines.ConstructionSitesBase = 2f;
        c.Defines.ConstructionSitesPerCivilianIndustry = 0.25f;
        c.Defines.ConstructionSitesMax = 30;
        Check(ConstructionCost.WorkingSites(c, n) == 2, "no industry: the base two sites");
        s.Buildings[BuildingKind.CivilianIndustry] = 8;
        Check(ConstructionCost.CivilianIndustry(c, n) == 8 && ConstructionCost.WorkingSites(c, n) == 4,
            "eight civilian factories: two more sites, one per four");
        s.Buildings[BuildingKind.CivilianIndustry] = 400;
        Check(ConstructionCost.WorkingSites(c, n) == 30, "never more than the ceiling");
        c.Defines.ConstructionSitesInParallel = false;
        Check(ConstructionCost.WorkingSites(c, n) == 1, "the old one-at-a-time queue is still a define away");
        c.Defines.ConstructionSitesInParallel = true;
        c.Defines.ConstructionSitesBase = float.NaN;
        Check(ConstructionCost.WorkingSites(c, n) >= 1, "a broken define still leaves one site working");

        // Who plays the country does not enter: the same economy under the computer gets the same sites.
        var (ai, aiNation, _) = World(1502, civilian: 8, player: false);
        var (human, humanNation, _) = World(1502, civilian: 8);
        Check(ConstructionCost.WorkingSites(ai, aiNation) == ConstructionCost.WorkingSites(human, humanNation),
            "a computer's country and a player's with the same industry work the same number of sites");
    }

    static void SitesWorkTogetherAndFinishTogether()
    {
        var (c, n, _) = World(1503);
        c.Defines.ConstructionSitesBase = 2f;
        c.Defines.ConstructionSitesPerCivilianIndustry = 0.25f;
        for (var id = 1; id <= 3; id++) c.Construction.Add(Site(c, n, id, 1f, 10));
        var plan = ConstructionQueue.Schedule(c, n);
        Check(plan[0].StartDays == 0 && plan[1].StartDays == 0 && plan[2].StartDays == 10 && plan[2].FinishDays == 20,
            $"the estimate plays two sites at once ({string.Join(" | ", plan.Select(e => e.StartDays + "-" + e.FinishDays))})");
        ConstructionSystem.Tick(c);
        var sites = ConstructionQueue.Sites(c, n);
        Check(sites[0].Waiting == ConstructionQueue.Working && sites[1].Waiting == ConstructionQueue.Working &&
              sites[2].Waiting == ConstructionQueue.Queued, "the two oldest work, the third waits its turn");
        Check(MathF.Abs(n.Treasury - (100f - 0.2f)) < 1e-4f, $"both are paid a day of their price ({n.Treasury:0.0000})");
        for (var day = 1; day < 10; day++) ConstructionSystem.Tick(c);
        Check(c.Construction.Count == 1 && c.Construction[0].Id == 3, "the first two finish on the same day");
    }

    static void PriceAndTimeScalesReachEveryReader()
    {
        var (c, n, s) = World(1504, treasury: 1000f);
        c.Defines.ConstructionCostScale = 1f;
        c.Defines.ConstructionTimeScale = 1f;
        var price = ConstructionCost.Of(c, n, BuildingKind.CivilianIndustry);
        var days = ConstructionCost.WorkDays(c, n, BuildingKind.CivilianIndustry);
        c.Defines.ConstructionCostScale = 0.6f;
        c.Defines.ConstructionTimeScale = 0.75f;
        Check(MathF.Abs(ConstructionCost.Of(c, n, BuildingKind.CivilianIndustry) - price * 0.6f) < 1e-4f, "the price scale reaches the price");
        var scaled = ConstructionCost.WorkDays(c, n, BuildingKind.CivilianIndustry);
        Check(scaled == (int)MathF.Round(days * 0.75f), $"the time scale reaches the work days ({days} -> {scaled})");
        Check(ConstructionCost.ExpectedDays(c, n, BuildingKind.CivilianIndustry) == scaled, "the list says the days the site is contracted for, at pace one");
        var weekly = ConstructionCost.WeeklyPayment(c, n, BuildingKind.CivilianIndustry);
        Check(MathF.Abs(weekly - price * 0.6f / scaled * 7f) < 1e-4f, $"the weekly payment is the price over the work days, a week of it ({weekly:0.0000})");
        c.Defines.ConstructionCostScale = float.NaN;
        Check(MathF.Abs(ConstructionCost.Of(c, n, BuildingKind.CivilianIndustry) - price) < 1e-4f, "a broken scale is the catalogue price");
        c.Defines.ConstructionCostScale = 0.6f;

        c.Commands.Enqueue(new QueueConstructionCommand { IssuerId = n.Id, StateMapIndex = s.MapIndex, Building = BuildingKind.CivilianIndustry });
        c.Commands.ApplyAll(c);
        var site = c.Construction.FirstOrDefault(p => p.NationId == n.Id);
        Check(site is not null && site.DurationDays == scaled && MathF.Abs(site.Cost - price * 0.6f) < 1e-4f,
            $"a queued site is contracted at the scaled price and time ({site?.Cost:0.000}, {site?.DurationDays})");
    }

    static void TheCeilingHoldsAndSaysWhy()
    {
        var (c, n, _) = World(1505);
        c.Defines.ConstructionSitesBase = 3f;
        c.Defines.ConstructionSitesPerCivilianIndustry = 0f;
        c.Defines.ConstructionBudgetShare = 0.5f;
        c.Defines.ConstructionTreasuryDrawWeeks = 0f;
        // Income 0.7 a week: the ceiling is 0.35 a week, 0.05 a day. Three sites each due 0.04 a day.
        n.LedgerBudgetLastWeek = new float[BudgetCore.All.Count];
        n.LedgerBudgetLastWeek[0] = 0.7f;
        Check(MathF.Abs(ConstructionQueue.WeeklyIncome(c, n) - 0.7f) < 1e-5f && MathF.Abs(ConstructionQueue.WeeklyCeiling(c, n) - 0.35f) < 1e-5f,
            "the ceiling is the share of the last week's income");
        for (var id = 1; id <= 3; id++) c.Construction.Add(Site(c, n, id, 4f, 100));
        Check(MathF.Abs(ConstructionQueue.WeeklyDue(c, n) - 0.84f) < 1e-4f && MathF.Abs(ConstructionQueue.WeeklyBill(c, n) - 0.35f) < 1e-4f,
            "the queue would cost 0.84 a week and is held to 0.35");
        var before = n.Treasury;
        ConstructionSystem.Tick(c);
        var sites = ConstructionQueue.Sites(c, n);
        Check(MathF.Abs(before - n.Treasury - 0.05f) < 1e-4f, $"a day takes exactly a seventh of the ceiling ({before - n.Treasury:0.0000})");
        Check(sites[0].Waiting == ConstructionQueue.Working && sites[1].Waiting == ConstructionQueue.Working &&
              sites[2].Waiting == ConstructionQueue.Budget, "the oldest are paid first; the one past the ceiling waits for tomorrow");
        Check(ConstructionQueue.Describe(c, sites[2]) == Loc.Get("Waiting: this week's construction budget went to the older sites"),
            "and says so in a sentence");
        Check(!ConstructionQueue.FrozenForMoney(c, n), "a ceiling is not a frozen treasury: no alarm");
        var plan = ConstructionQueue.Schedule(c, n);
        Check(plan.All(e => e.Funded) && plan[0].FinishDays > 100, $"dates are still promised, later for the ceiling ({plan[0].FinishDays} days)");
        Check(ConstructionQueue.Summary(c, n).Length > 0 && ConstructionQueue.Summary(c, n).Contains(Fmt.Money(0.35f)),
            "the summary names what the queue draws a week");

        // Savings: a treasury of 13 drawn over 26 weeks adds 0.5 a week to the 0.35.
        c.Defines.ConstructionTreasuryDrawWeeks = 26f;
        n.Treasury = 13f;
        Check(MathF.Abs(ConstructionQueue.WeeklyCeiling(c, n) - 0.85f) < 1e-4f,
            $"the ceiling adds a twenty-sixth of the treasury to the income share ({ConstructionQueue.WeeklyCeiling(c, n):0.000})");
        // The income share comes out of the week's income; what comes out of savings is a part of
        // what is left, so ten years of it leave money in the bank.
        for (var week = 0; week < 520; week++) n.Treasury -= ConstructionQueue.SavingsDraw(c, n);
        Check(n.Treasury > 0f, $"ten years of drawing on savings never empties the treasury ({n.Treasury:0.0000} of 13 left)");
        n.Treasury = 100f;

        c.Defines.ConstructionBudgetShare = 0f;
        Check(float.IsPositiveInfinity(ConstructionQueue.WeeklyCeiling(c, n)), "share nought: no ceiling");
        before = n.Treasury;
        ConstructionSystem.Tick(c);
        Check(MathF.Abs(before - n.Treasury - 0.12f) < 1e-4f && sites.All(p => p.Waiting == ConstructionQueue.Working),
            "with no ceiling every worked site is paid in full");
    }

    static void WorkFollowsTheMoney()
    {
        var (c, n, _) = World(1506, treasury: 0.005f);
        c.Defines.ConstructionSitesBase = 1f;
        c.Defines.ConstructionSitesPerCivilianIndustry = 0f;
        c.Construction.Add(Site(c, n, 1, 1f, 100));
        ConstructionSystem.Tick(c);
        var site = c.Construction[0];
        Check(MathF.Abs(site.ProgressDays - 0.5f) < 1e-4f && MathF.Abs(site.Paid - 0.005f) < 1e-5f && n.Treasury <= 1e-6f,
            $"half a day's money buys half a day's work ({site.ProgressDays:0.000} days, paid {site.Paid:0.0000})");
        ConstructionSystem.Tick(c);
        Check(site.Waiting == ConstructionQueue.Treasury && ConstructionQueue.FrozenForMoney(c, n),
            "an empty treasury still stops the site and says why");
    }

    static void TheSitesHaveTheirOwnLedgerLine()
    {
        var (c, n, _) = World(1507);
        c.Defines.ConstructionSitesBase = 2f;
        c.Defines.ConstructionSitesPerCivilianIndustry = 0f;
        c.Construction.Add(Site(c, n, 1, 1f, 10));
        c.Construction.Add(Site(c, n, 2, 1f, 10));
        MoneyLedger.Close(c, n);
        MoneyLedger.BudgetWeek(n)[0] = 1f;
        ConstructionSystem.Tick(c);
        MoneyLedger.Close(c, n);
        Check(MathF.Abs(MoneyLedger.LastOut(n, MoneyFlow.BuildingSites) + 0.2f) < 1e-4f && MoneyLedger.LastOut(n, MoneyFlow.Construction) == 0f,
            "a day's building is booked on the sites' own line, not with rebuilding and resettlement");
        var rows = MoneyLedgerView.ForNation(c, n);
        var line = rows.FirstOrDefault(r => r.Group == MoneyLedgerView.GroupConstruction && !r.Note);
        var queue = rows.FirstOrDefault(r => r.Group == MoneyLedgerView.GroupConstruction && r.Note);
        Check(line is not null && line.Caption == Loc.Get("Building sites") && MathF.Abs(line.Weekly + 0.2f) < 1e-4f,
            "the ledger shows the sites as their own line under their own heading");
        Check(queue is not null && MathF.Abs(queue.Weekly + 1.4f) < 1e-3f && queue.Explanation.Length > 0,
            $"and beside it what the queue costs a week now ({queue?.Weekly:0.000})");
        var headings = rows.Select(r => r.Group).Distinct().ToList();
        Check(headings.IndexOf(MoneyLedgerView.GroupConstruction) == headings.IndexOf(MoneyLedgerView.GroupSpending) + 1,
            "straight after the budget's spending, where a player reads where the money went");
    }
}
