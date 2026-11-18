using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using static DealOrForce.Tools.BenchChecks;

namespace DealOrForce.Tools;

internal static class SimulationProbeReports
{
    [SimBenchReport("--census", SimBenchPhase.PostLoadCensus, Order = 104,
        Description = "Print nations, movements, and relations census")]
    public static int RunCensus(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var countries = campaign.Nations.Count(n => n.Movement is null && n.Population > 0);
        var gone = campaign.Nations.Count(n => n.Movement is null && n.Population <= 0);
        var live = campaign.Nations.Where(n => n.Movement is { Dissolved: false }).ToList();
        var dissolved = campaign.Nations.Count(n => n.Movement is { Dissolved: true });
        Console.WriteLine($"CENSUS day {campaign.ElapsedDays}: nations {campaign.Nations.Count} = countries {countries} + gone {gone} + live movements {live.Count} " +
                          $"(landless {live.Count(n => n.Movement.Landless)}, exile {live.Count(n => n.Movement.Exile)}) + dissolved {dissolved}");
        string Kind(int id) => campaign.NationById(id) is not { } n ? "none" : n.Movement is { Dissolved: false } ? "movement" :
            n.Movement is not null ? "dissolved" : n.Population > 0 ? "country" : "gone";
        foreach (var group in campaign.Relations.Values.GroupBy(r => string.Join("-", new[] { Kind(r.A), Kind(r.B) }.OrderBy(k => k)))
                     .OrderByDescending(g => g.Count()))
            Console.WriteLine($"CENSUS relations {group.Key}: {group.Count()}");
        Console.WriteLine($"CENSUS dormant relations {campaign.DormantRelations.Count}");
        foreach (var group in live.GroupBy(n => Movements.DefinitionOf(n)?.Id ?? "?").OrderByDescending(g => g.Count()).Take(12))
            Console.WriteLine($"CENSUS movement {group.Key}: {group.Count()}");
        var born = live.GroupBy(n => n.Movement.Exile ? "exile" : n.Movement.Landless ? "landless" : "landed");
        foreach (var g in born)
            Console.WriteLine($"CENSUS movements {g.Key}: {g.Count()}, cadres median {g.Select(n => n.Movement.Cadres).OrderBy(x => x).ElementAt(g.Count() / 2):0}");
        return 0;
    }

    [SimBenchReport("--regions", SimBenchPhase.BeforeMap, Order = 172,
        Description = "Print region ids, names, and owner ids")]
    public static int RunRegions(SimBenchContext ctx)
    {
        foreach (var state in ctx.Campaign.States)
            Console.WriteLine(state.Id + "\t" + state.Name + "\t" + state.NationId);
        return 0;
    }

    [SimBenchReport("--gore-maps", SimBenchPhase.BeforeMap, Order = 185,
        Description = "Write region maps after a decade with and without border rounding")]
    public static int? RunGoreMaps(SimBenchContext ctx)
    {
        var goreAt = ctx.IndexOf("--gore-maps");
        if (goreAt < 0 || goreAt + 1 >= ctx.Args.Length) return null;
        return WriteGoreMaps(ctx.Args[goreAt + 1]);
    }

    [SimBenchReport("--fingerprint", SimBenchPhase.PostOpening, Order = 395,
        Description = "Tick N days and print 64-bit world fingerprint every 30 days")]
    public static int RunFingerprint(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var fingerprintAt = ctx.IndexOf("--fingerprint");
        var fingerprintDays = fingerprintAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[fingerprintAt + 1], out var fd) ? fd : 60;
        campaign.IsPaused = false;
        var firstDay = campaign.ElapsedDays;
        for (var day = 1; day <= fingerprintDays; day++)
        {
            campaign.TickDay();
            if (!string.IsNullOrEmpty(campaign.SimulationFault) || campaign.ElapsedDays != firstDay + day)
            {
                Console.WriteLine($"FINGERPRINT FAIL iteration {day}: actual day {campaign.ElapsedDays}, " +
                                  $"expected {firstDay + day}; {campaign.SimulationFault}");
                return 1;
            }
            if (day % 30 == 0 || day == fingerprintDays)
                Console.WriteLine($"FINGERPRINT day {day}: {CampaignFingerprint.Of(campaign)} " +
                                  $"(without the retirement clock {CampaignFingerprint.Of(campaign, longCampaignClock: false)})");
        }
        LongCampaignReport.Census(campaign, $"after {fingerprintDays} day(s)");
        {
            long groups = 0, people = 0;
            foreach (var state in campaign.States) { groups += state.PopulationGroups.Count; people += state.Population; }
            Console.WriteLine($"GROUPS after {fingerprintDays} day(s): {groups:N0} of {people:N0} people; folded {campaign.FoldedGroups:N0} group(s), " +
                              $"counted under another trade {campaign.FoldedTradePeople:N0}, another faith {campaign.FoldedFaithPeople:N0}, " +
                              $"another people {campaign.FoldedCulturePeople:N0}.");
        }
        return 0;
    }

    [SimBenchReport("--separate-peace", SimBenchPhase.PostOpening, Order = 431,
        Description = "Track coalition members leaving wars via separate peace")]
    public static int RunSeparatePeace(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var separateAt = ctx.IndexOf("--separate-peace");
        var spDays = separateAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[separateAt + 1], out var sd) ? sd : 365;
        campaign.IsPaused = false;
        var peakWish = new Dictionary<(int War, int Nation), float>();
        for (var day = 1; day <= spDays; day++)
        {
            campaign.TickDay();
            if (day % 7 != 0) continue;
            foreach (var w in campaign.Wars.Where(w => !w.Ended))
                foreach (var id in w.AttackerAllies.Concat(w.DefenderAllies))
                    if (campaign.NationById(id) is { } n)
                    {
                        var wish = SeparatePeace.Wish(campaign, w, n);
                        if (!peakWish.TryGetValue((w.Id, id), out var was) || wish > was) peakWish[(w.Id, id)] = wish;
                    }
            if (day % 91 == 0)
                Console.WriteLine($"SP day {day}: coalition wars {campaign.Wars.Count(w => !w.Ended && w.AttackerAllies.Count + w.DefenderAllies.Count > 0)}, " +
                                  $"members {campaign.Wars.Where(w => !w.Ended).Sum(w => w.AttackerAllies.Count + w.DefenderAllies.Count)}, " +
                                  $"separate peaces so far {campaign.Wars.Sum(w => w.SeparatePeaces.Count)}");
        }
        foreach (var w in campaign.Wars)
            foreach (var left in w.SeparatePeaces)
                Console.WriteLine($"SP LEFT day {left.Day}: {campaign.NationById(left.NationId)?.Tag} from {WarNames.Of(campaign, w)} with {campaign.NationById(left.WithId)?.Tag}: " +
                                  $"{left.Terms}{(left.SwitchedSides ? " + switched sides" : "")}, paid {left.Payment:0.00}, ceded {left.ProvincesCeded}");
        foreach (var pair in peakWish.OrderByDescending(p => p.Value).Take(15))
            Console.WriteLine($"SP WISH peak {pair.Value:0} {campaign.NationById(pair.Key.Nation)?.Tag} in {WarNames.Of(campaign, campaign.Wars.First(w => w.Id == pair.Key.War))}");
        Console.WriteLine($"SP subsidies {campaign.Chronicle.Entries.Count(e => e.Kind == "war_subsidy")}");
        return 0;
    }

    [SimBenchReport("--century", SimBenchPhase.PostOpening, Order = 465,
        Description = "Multi-year throughput, memory, and hot-system census (optional --century-save)")]
    public static int RunCentury(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var centuryAt = ctx.IndexOf("--century");
        var years = centuryAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[centuryAt + 1], out var cy) ? cy : 20;
        campaign.IsPaused = false;
        var clock = Stopwatch.StartNew();
        var lastTotals = new Dictionary<string, double>();
        for (var year = 1; year <= years; year++)
        {
            var yearStart = clock.Elapsed.TotalMilliseconds;
            var worst = 0.0;
            for (var day = 0; day < 365; day++)
            {
                var before = clock.Elapsed.TotalMilliseconds;
                campaign.TickDay();
                worst = Math.Max(worst, clock.Elapsed.TotalMilliseconds - before);
            }
            var centuryPerDay = (clock.Elapsed.TotalMilliseconds - yearStart) / 365.0;
            long images = 0, decisions = 0, modifiers = 0;
            foreach (var n in campaign.Nations)
            {
                images += n.PublicImages?.Count ?? 0;
                decisions += n.DecisionAvailableDay.Count;
                modifiers += n.Modifiers.Count;
            }
            Console.WriteLine($"CENTURY year {year} ({campaign.Date}): {centuryPerDay:0.0} ms/day, worst day {worst:0} ms, heap {GC.GetTotalMemory(false) / 1048576.0:0} MB | " +
                              $"nations {campaign.Nations.Count} (alive {campaign.Nations.Count(n => n.Population > 0)}), regions {campaign.States.Count}, " +
                              $"wars {campaign.Wars.Count} (open {campaign.Wars.Count(w => !w.Ended)}), relations {campaign.Relations.Count}+{campaign.DormantRelations.Count} dormant, " +
                              $"armies {campaign.Armies.Count}, battles {campaign.Battles.Count}, chronicle {campaign.Chronicle.Entries.Count}, images {images}, decisions {decisions}, modifiers {modifiers}, " +
                              $"events {campaign.PendingEvents.Count}, sponsorships {campaign.Sponsorships.Count}, organisations {campaign.Organizations.Count}, construction {campaign.Construction.Count}, routes {campaign.TradeRoutes.Count}");
            var systems = SimProfiler.All.Where(sample => !sample.Name.StartsWith(" "))
                .Select(sample => (sample.Name, Ms: sample.TotalMilliseconds - lastTotals.GetValueOrDefault(sample.Name)))
                .OrderByDescending(pair => pair.Ms).Take(12).Select(pair => $"{pair.Name} {pair.Ms / 365.0:0.0}");
            Console.WriteLine($"CENTURY systems year {year} (ms/day): " + string.Join(", ", systems));
            var parts = SimProfiler.All.Where(sample => sample.Name.StartsWith(" "))
                .Select(sample => (Name: sample.Name.Trim(), Ms: sample.TotalMilliseconds - lastTotals.GetValueOrDefault(sample.Name)))
                .OrderByDescending(pair => pair.Ms).Take(15).Select(pair => $"{pair.Name} {pair.Ms / 365.0:0.0}");
            Console.WriteLine($"CENTURY parts year {year} (ms/day): " + string.Join(", ", parts));
            foreach (var sample in SimProfiler.All) lastTotals[sample.Name] = sample.TotalMilliseconds;
            var centurySaveAt = ctx.IndexOf("--century-save");
            if (centurySaveAt >= 0 && centurySaveAt + 1 < ctx.Args.Length && year % 5 == 0)
            {
                SaveSystem.Save(ctx.Args[centurySaveAt + 1], $"century_y{year}", campaign, "");
                Console.WriteLine($"CENTURY saved century_y{year}");
            }
        }
        return 0;
    }

    [SimBenchReport("--trace", SimBenchPhase.PostOpening, Order = 513,
        Description = "Trace single nation population, treasury, inflation and stability over N days")]
    public static int? RunTrace(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var traceAt = ctx.IndexOf("--trace");
        if (traceAt < 0 || traceAt + 1 >= ctx.Args.Length || campaign.NationByTag(ctx.Args[traceAt + 1]) is not { } traced)
            return null;

        campaign.IsPaused = false;
        void Line(int day)
        {
            var regions = campaign.StatesOf(traced);
            var people = regions.Sum(r => r.Population);
            var held = campaign.States.Where(r => r.HeldById == traced.Id || r.NationMapIndex == traced.MapIndex).Sum(r => r.Population);
            var occupied = regions.Sum(r => (long)(r.Population * StabilityCore.OccupiedShare(campaign, r, traced)));
            var deaths = regions.Sum(r => r.ReviewDeaths);
            var births = regions.Sum(r => r.ReviewBirths);
            var migration = regions.Sum(r => r.ReviewNetMigration);
            Console.WriteLine($"TRACE day {day,4} {campaign.Date}: pop {traced.Population:N0} (regions {regions.Length}, their people {people:N0}, occupied {occupied:N0}) " +
                              $"births {births:N0} deaths {deaths:N0} net migration {migration:N0} | treasury {traced.Treasury:0.00} bn, weekly {traced.WeeklyBalance:0.000} " +
                              $"(tax {traced.WeeklyTaxIncome:0.000}, spend {traced.WeeklyExpenses:0.000}), inflation {traced.Inflation:P1}, stability {traced.Stability:0.00}, " +
                              $"wars {campaign.Wars.Count(w => !w.Ended && w.Involves(traced.Id))}");
        }
        Line(0);
        for (var day = 1; day <= ctx.Days; day++)
        {
            campaign.TickDay();
            if (day % 15 == 0 || day == ctx.Days) Line(day);
        }
        return 0;
    }

    [SimBenchReport("--world-stability", SimBenchPhase.PostOpening, Order = 556,
        Description = "Track global stability, standard of living, and conflict counts")]
    public static int? RunWorldStability(SimBenchContext ctx)
    {
        if (!ctx.Mapped) return null;
        var campaign = ctx.Campaign;
        campaign.IsPaused = false;
        for (var day = 1; day <= ctx.Days; day++)
        {
            campaign.TickDay();
            if (day % 1825 != 0 && day != ctx.Days) continue;
            var big = campaign.Nations.Where(n => n.Population > 1_000_000 && n.Movement is null).ToList();
            float Q(IEnumerable<float> values, float q) { var s = values.OrderBy(v => v).ToList(); return s.Count == 0 ? 0 : s[(int)(q * (s.Count - 1))]; }
            Console.WriteLine($"  year {day / 365,3}: stability p10 {Q(big.Select(n => n.Stability), .1f):0.00} median {Q(big.Select(n => n.Stability), .5f):0.00} " +
                              $"p90 {Q(big.Select(n => n.Stability), .9f):0.00} | living p10 {Q(big.Select(n => n.StandardOfLiving), .1f):0.00} " +
                              $"median {Q(big.Select(n => n.StandardOfLiving), .5f):0.00} p90 {Q(big.Select(n => n.StandardOfLiving), .9f):0.00} | " +
                              $"civil wars on {campaign.Wars.Count(w => !w.Ended && w.CivilConflict)}, wars on {campaign.Wars.Count(w => !w.Ended && !w.CivilConflict)} | " +
                              string.Join(" ", new[] { "JPN", "DEU", "IND", "CHN", "USA", "NGA", "BRA" }.Select(tag => campaign.NationByTag(tag))
                                  .Where(n => n is not null).Select(n => $"{n.Tag}:{n.Stability:0.00}/{n.StandardOfLiving:0.00}")));
        }
        return 0;
    }

    [SimBenchReport("--front", SimBenchPhase.PostOpening, Order = 624,
        Description = "Track Russo-Ukrainian front movement over N days")]
    public static int RunFront(SimBenchContext ctx) => Front(ctx.Campaign, ctx.Days);

    [SimBenchReport("--peoples", SimBenchPhase.PostOpening, Order = 628,
        Description = "Report regional ethnic mix trajectories over N days")]
    public static int RunPeoples(SimBenchContext ctx) => Peoples(ctx.Campaign, ctx.Days);

    [SimBenchReport("--decision-resources", SimBenchPhase.PostOpening, Order = 637,
        Description = "Trace decision resource triggers across ticks")]
    public static int RunDecisionResources(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var resources = new DecisionResourceTrace(campaign);
        campaign.StepCompleted += resources.Observe;
        campaign.IsPaused = false;
        for (var day = 0; day < ctx.Days; day++) campaign.TickDay();
        campaign.StepCompleted -= resources.Observe;
        return resources.Print(campaign);
    }

    [SimBenchReport("--world", SimBenchPhase.PostOpening, Order = 812,
        Description = "Long-run world tension, intelligence networks, and world war report")]
    public static int RunWorld(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var worldAt = ctx.IndexOf("--world");
        var worldDays = worldAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[worldAt + 1], out var wd) ? wd : 7300;
        var worldEvery = worldAt + 2 < ctx.Args.Length && int.TryParse(ctx.Args[worldAt + 2], out var we) ? we : 365;
        campaign.IsPaused = false;
        var incidents = 0;
        var exposed = 0;
        long seen = 0;
        for (var day = 1; day <= worldDays; day++)
        {
            campaign.TickDay();
            var entries = campaign.Chronicle.Read(-1, -1, true);
            foreach (var entry in entries.Where(e => e.Sequence > seen))
            {
                if (entry.Kind == "world_incident") { incidents++; Console.WriteLine($"   INCIDENT day {entry.Day}: {entry.ActorName} - {entry.TargetName}"); }
                if (entry.Kind == "world_war_began") Console.WriteLine($"   WORLD WAR day {entry.Day}: {entry.Subject} ({entry.ActorName} vs {entry.TargetName})");
                if (entry.Kind == "spy_network_exposed") exposed++;
            }
            if (entries.Length > 0) seen = Math.Max(seen, entries.Max(e => e.Sequence));
            if (day % worldEvery != 0 && day != worldDays) continue;
            var big = campaign.Wars.Where(w => !w.Ended).OrderByDescending(w => w.BelligerentCount).FirstOrDefault();
            var interstate = campaign.Wars.Where(w => !w.CivilConflict && w.StartedDay > 0).ToList();
            Console.WriteLine($"WORLD day {campaign.ElapsedDays} ({campaign.Date}): tension {WorldWar.Tension(campaign):0.0}, incidents so far {incidents}, " +
                              $"world wars {campaign.Wars.Count(w => w.WorldWarDay >= 0)} ({campaign.Wars.Count(w => w.WorldWarDay >= 0 && !w.Ended)} active), " +
                              $"largest war {(big is null ? "-" : $"{WarNames.Of(campaign, big)} with {big.BelligerentCount}")}, " +
                              $"wars between states begun {interstate.Count} ({interstate.Count(w => !w.Ended)} on, most belligerents {(interstate.Count == 0 ? 0 : interstate.Max(w => w.BelligerentCount))})");
            var networks = campaign.Nations.SelectMany(n => n.IntelNetworks.Values).ToList();
            Console.WriteLine($"   intel: {networks.Count} networks, mean strength {(networks.Count == 0 ? 0 : networks.Average()):0.00}, " +
                              $"{networks.Count(v => v >= campaign.Defines.IntelInspectStrength)} open a dossier, {exposed} exposed so far");
            foreach (var (a, b, pair) in WorldWar.Pairs(campaign).Take(3))
                Console.WriteLine($"   pair {campaign.NationById(a)?.Tag}-{campaign.NationById(b)?.Tag} {pair:0.0}");
        }
        return 0;
    }

    [SimBenchReport("--mobility", SimBenchPhase.PostOpening, Order = 851,
        Description = "Track front movement, battle outcomes, and readiness every K days")]
    public static int RunMobility(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var mobilityAt = ctx.IndexOf("--mobility");
        var mobilityDays = mobilityAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[mobilityAt + 1], out var mdays) ? mdays : 365;
        var mobilityEvery = mobilityAt + 2 < ctx.Args.Length && int.TryParse(ctx.Args[mobilityAt + 2], out var mevery) ? mevery : 30;
        campaign.IsPaused = false;
        var previous = (ushort[])campaign.ProvinceController.Clone();
        var lastMove = new Dictionary<int, int>();
        var seenBattles = new HashSet<int>();
        var movedTotal = 0;
        Warfare.OutcomeTally.Clear();
        for (var day = 1; day <= mobilityDays; day++)
        {
            campaign.TickDay();
            var control = campaign.ProvinceController;
            for (var p = 1; p < control.Length && p < previous.Length; p++)
            {
                if (control[p] == previous[p]) continue;
                movedTotal++;
                foreach (var war in campaign.Wars)
                    if (!war.Ended && war.OriginalController.ContainsKey(p)) lastMove[war.Id] = day;
                previous[p] = control[p];
            }
            if (day % mobilityEvery != 0 && day != mobilityDays) continue;
            var fighting = campaign.Wars.Where(w => !w.Ended && campaign.Armies.Any(a => a.AssignedWarId == w.Id)).ToList();
            var stalled = fighting.Count(w => day - lastMove.GetValueOrDefault(w.Id, 0) >= 90 &&
                                              campaign.ElapsedDays - w.StartedDay >= 90);
            var ended = campaign.Battles.Where(b => b.Ended && seenBattles.Add(b.Id)).ToList();
            var attackerWins = ended.Count(b => b.WinnerId == b.AttackerId);
            Console.WriteLine($"FRONTS day {campaign.ElapsedDays}: provinces changed hands {movedTotal:N0} in the period, " +
                              $"wars with armies in the field {fighting.Count} ({stalled} still for 90+ days), " +
                              $"battles ended {ended.Count} (attacker won {attackerWins}), open {campaign.Battles.Count(b => !b.Ended)}; " +
                              "outcomes " + string.Join(", ", Warfare.OutcomeTally.OrderByDescending(p => p.Value).Take(6).Select(p => $"{p.Key} {p.Value}")));
            var largest = fighting.OrderByDescending(w => campaign.Armies.Count(a => a.AssignedWarId == w.Id)).FirstOrDefault();
            if (largest is not null)
                foreach (var sideId in new[] { largest.AttackerId, largest.DefenderId })
                {
                    var side = campaign.Armies.Where(a => a.AssignedWarId == largest.Id && a.NationId == sideId && a.TemplateId != MilitiaCore.TemplateId).ToList();
                    if (side.Count == 0) continue;
                    Console.WriteLine($"   {campaign.NationById(sideId)?.Tag} in {largest.Name}: {side.Count} div, " +
                                      $"advance {side.Count(a => a.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead)}, " +
                                      $"defend {side.Count(a => a.OrderKind == ArmyOrder.DefendFront)}, reserve {side.Count(a => a.OrderKind == ArmyOrder.ReserveFront)}, " +
                                      $"in battle {side.Count(a => a.InBattle)}, resting {side.Count(a => a.ReadyDay > campaign.ElapsedDays)}, " +
                                      $"marching {side.Count(a => a.Route.Count > 0)}, org {side.Average(a => a.Organisation):0.00}, " +
                                      $"planning {side.Average(a => a.Planning):0.00}, readiness {FrontPlanner.Readiness(campaign, largest, campaign.NationById(sideId)):0.00}");
                }
            movedTotal = 0;
            Warfare.OutcomeTally.Clear();
        }
        return 0;
    }

    [SimBenchReport("--goods-probe", SimBenchPhase.PostOpening, Order = 915,
        Description = "Inspect market production, demand, prices, and trade shares per commodity")]
    public static int RunGoodsProbe(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var probeGoodsAt = ctx.IndexOf("--goods-probe");
        var wanted = probeGoodsAt + 1 < ctx.Args.Length && !ctx.Args[probeGoodsAt + 1].StartsWith("--", StringComparison.Ordinal) &&
                     !int.TryParse(ctx.Args[probeGoodsAt + 1], out _)
            ? ctx.Args[probeGoodsAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries)
            : null;
        var probeDays = ctx.Args.Skip(probeGoodsAt + 1).Take(2).Select(a => int.TryParse(a, out var d) ? d : -1).FirstOrDefault(d => d >= 0);
        campaign.IsPaused = false;
        for (var day = 0; day < probeDays; day++) campaign.TickDay();
        var goods = wanted is null
            ? CommodityCatalog.All.OrderBy(CommodityCatalog.IdOf).ToList()
            : wanted.Select(id => CommodityCatalog.TryParse(id, out var g) ? g : (Commodity?)null).Where(g => g is not null).Select(g => g!.Value).ToList();
        var living = campaign.Nations.Where(n => n.Population > 0).ToList();
        foreach (var good in goods)
        {
            double Made(Nation n) => n.Market.TryGetValue(good, out var q) ? q.Production : 0;
            double Asked(Nation n) => n.Market.TryGetValue(good, out var q) ? q.Demand : 0;
            double Sold(Nation n) => n.Market.TryGetValue(good, out var q) ? q.Exports : 0;
            double Bought(Nation n) => n.Market.TryGetValue(good, out var q) ? q.Imports : 0;
            var made = living.Sum(Made);
            var asked = living.Sum(Asked);
            var sold = Math.Max(1e-6, living.Sum(Sold));
            var bought = Math.Max(1e-6, living.Sum(Bought));
            var paid = living.Sum(n => n.Market.TryGetValue(good, out var q) ? (double)q.Demand * q.Price : 0);
            var price = asked > 1e-6 ? paid / asked / CommodityCatalog.Get(good).BasePrice : 0;
            var shortOf = living.Count(n => n.Market.TryGetValue(good, out var q) && q.Demand > 0.01f && q.IsShortage);
            var importPrice = living.Sum(n => n.Market.TryGetValue(good, out var q) ? (double)q.Imports * q.Price : 0) / bought / CommodityCatalog.Get(good).BasePrice;
            Console.WriteLine($"GOODS {CommodityCatalog.IdOf(good)} day {campaign.ElapsedDays}: made {made:0.00} asked {asked:0.00} ratio {made / Math.Max(1e-6, asked):0.00}; " +
                              $"price x{price:0.00}, importers pay x{importPrice:0.00}; traded {sold / Math.Max(1e-6, made):P0} of output; {shortOf} countries short");
            var surplus = living.Sum(n => Math.Max(0, Made(n) - Asked(n)));
            Console.WriteLine($"   surplus {surplus:0.0}, exported {sold:0.0} ({sold / Math.Max(1e-6, surplus):P0} of it), imported {bought:0.0}");
            Console.WriteLine("   makers: " + string.Join(", ", living.OrderByDescending(Made).Take(8).Select(n => $"{n.Tag} {Made(n) / Math.Max(1e-6, made):P0}")));
            Console.WriteLine("   exporters: " + string.Join(", ", living.OrderByDescending(Sold).Take(8).Where(n => Sold(n) > 0).Select(n => $"{n.Tag} {Sold(n) / sold:P0}")));
            Console.WriteLine("   importers: " + string.Join(", ", living.OrderByDescending(Bought).Take(8).Where(n => Bought(n) > 0).Select(n => $"{n.Tag} {Bought(n) / bought:P0}")));
            Console.WriteLine("   supplied (largest economies): " + string.Join(", ", living.OrderByDescending(n => n.Gdp).Take(12)
                .Select(n => n.Market.TryGetValue(good, out var q) ? $"{n.Tag} {q.Satisfaction:P0}" : n.Tag)));
        }
        return 0;
    }

    [SimBenchReport("--goods-shock", SimBenchPhase.PostOpening, Order = 967,
        Description = "Simulate commodity supply shock across selected exporter tags")]
    public static int? RunGoodsShock(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var shockAt = ctx.IndexOf("--goods-shock");
        if (shockAt < 0 || shockAt + 3 >= ctx.Args.Length) return null;
        var struck = ctx.Args[shockAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var share = M.Clamp01(float.Parse(ctx.Args[shockAt + 2], CultureInfo.InvariantCulture));
        var shockDays = int.Parse(ctx.Args[shockAt + 3]);
        var shockGoods = (shockAt + 4 < ctx.Args.Length && !ctx.Args[shockAt + 4].StartsWith("--", StringComparison.Ordinal)
                ? ctx.Args[shockAt + 4].Split(',', StringSplitOptions.RemoveEmptyEntries)
                : new[] { "Food", "Grain", "Oilseeds", "Semiconductors", "AdvancedChips", "Electronics", "ConsumerGoods" })
            .Select(id => CommodityCatalog.TryParse(id, out var g) ? g : (Commodity?)null).Where(g => g is not null).Select(g => g!.Value).ToList();
        var watch = (Environment.GetEnvironmentVariable("DOF_SHOCK_WATCH") ?? "EGY,TUR,IDN,CHN,USA,DEU,JPN,KOR,IND")
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        campaign.IsPaused = false;
        for (var day = 0; day < 28; day++) campaign.TickDay();
        if (share > 0f)
            foreach (var tag in struck)
            {
                var nation = campaign.NationByTag(tag.Trim());
                if (nation is null) continue;
                foreach (var state in campaign.StatesOf(nation))
                {
                    foreach (var good in state.ResourceDeposits.Keys.ToArray()) state.ResourceDeposits[good] *= 1f - share;
                    state.WarDamage = Math.Max(state.WarDamage, share);
                }
            }
        for (var day = 0; day < shockDays; day++) campaign.TickDay();
        var alive = campaign.Nations.Where(n => n.Population > 0).ToList();
        Console.WriteLine($"SHOCK {string.Join("+", struck)} share {share:0.00} after {shockDays} days: world GDP {alive.Sum(n => (double)n.Gdp):N0} bn");
        foreach (var good in shockGoods)
        {
            var asked = alive.Sum(n => n.Market.TryGetValue(good, out var q) ? (double)q.Demand : 0);
            var paid = alive.Sum(n => n.Market.TryGetValue(good, out var q) ? (double)q.Demand * q.Price : 0);
            var made = alive.Sum(n => n.Market.TryGetValue(good, out var q) ? (double)q.Production : 0);
            var shortOf = alive.Count(n => n.Market.TryGetValue(good, out var q) && q.Demand > 0.01f && q.IsShortage);
            var imported = alive.Sum(n => n.Market.TryGetValue(good, out var q) ? (double)q.Imports : 0);
            var importPrice = imported > 1e-6 ? alive.Sum(n => n.Market.TryGetValue(good, out var q) ? (double)q.Imports * q.Price : 0) / imported / CommodityCatalog.Get(good).BasePrice : 0;
            var exported = Math.Max(1e-6, alive.Sum(n => n.Market.TryGetValue(good, out var q) ? (double)q.Exports : 0));
            Console.WriteLine($"   {CommodityCatalog.IdOf(good)}: made {made:0.0}, price x{(asked > 1e-6 ? paid / asked / CommodityCatalog.Get(good).BasePrice : 0):0.00}, importers pay x{importPrice:0.00}, " +
                              $"world exports {exported:0.0} ({string.Join(", ", alive.OrderByDescending(n => n.Market.TryGetValue(good, out var q) ? q.Exports : 0).Take(5).Select(n => $"{n.Tag} {n.Market[good].Exports / exported:P0}"))}), {shortOf} countries short; " +
                              string.Join(" ", watch.Select(tag => campaign.NationByTag(tag) is { } n && n.Market.TryGetValue(good, out var q)
                                  ? $"{tag} {q.Satisfaction:P0}/x{q.Price / CommodityCatalog.Get(good).BasePrice:0.00}" : "")));
        }
        Console.WriteLine($"   GDP: " + string.Join(" ", watch.Select(tag => campaign.NationByTag(tag) is { } n ? $"{tag} {n.Gdp:N0}" : "")));
        return 0;
    }

    [SimBenchReport("--growth", SimBenchPhase.PostOpening, Order = 1015,
        Description = "Long-run economic growth, capital deepening, and construction tally report")]
    public static int RunGrowth(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var growthAt = ctx.IndexOf("--growth");
        var growthDays = growthAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[growthAt + 1], out var gd) ? gd : 3650;
        var growthEvery = growthAt + 2 < ctx.Args.Length && int.TryParse(ctx.Args[growthAt + 2], out var ge) ? ge : 365;
        campaign.IsPaused = false;
        double WorldGdp() => campaign.Nations.Where(n => n.Population > 0).Sum(n => (double)n.Gdp);
        long WorldPeople() => campaign.Nations.Where(n => n.Population > 0).Sum(n => n.Population);
        var startGdp = WorldGdp();
        var startPeople = WorldPeople();
        void Report()
        {
            var gdp = WorldGdp();
            var people = WorldPeople();
            var years = MathF.Max(1f / 365f, campaign.ElapsedDays / 365f);
            var perHead = gdp * 1e9 / Math.Max(1, people);
            var startPerHead = startGdp * 1e9 / Math.Max(1, startPeople);
            var alive = campaign.Nations.Where(n => n.Population > 0 && n.Gdp > 0f).ToList();
            Console.WriteLine($"GROWTH day {campaign.ElapsedDays}: world {gdp:N0} bn ({(Math.Pow(gdp / startGdp, 1 / years) - 1) * 100:+0.00;-0.00}%/yr), " +
                              $"{people / 1e9:0.000} bn people, {perHead:N0} per head ({(Math.Pow(perHead / startPerHead, 1 / years) - 1) * 100:+0.00;-0.00}%/yr), " +
                              $"mean drift {(alive.Count == 0 ? 0 : alive.Average(n => n.SecularDrift)) * 100:+0.00;-0.00}%");
            Console.WriteLine("   " + WorldMarketLine(campaign));
            var intensities = alive.Select(n => (n.Tag, Jobs: campaign.StatesOf(n).Sum(MarketSystem.JobsDemanded),
                    Workers: campaign.StatesOf(n).Sum(MarketSystem.WorkersAvailable)))
                .Where(x => x.Workers > 0.01f).Select(x => (x.Tag, Ratio: x.Jobs / x.Workers)).OrderByDescending(x => x.Ratio).ToList();
            Console.WriteLine($"   jobs per worker: median {intensities[intensities.Count / 2].Ratio:0.00}, " +
                              $"{alive.Count(n => n.CapitalIntensityReference > 0f && campaign.StatesOf(n).Sum(MarketSystem.JobsDemanded) / Math.Max(0.01f, campaign.StatesOf(n).Sum(MarketSystem.WorkersAvailable)) > n.CapitalIntensityReference * campaign.Defines.CapitalDeepeningHeadroom)} past their headroom; highest " +
                              string.Join(", ", intensities.Take(12).Select(x => $"{x.Tag} {x.Ratio:0.00}")));
            Console.WriteLine("   top: " + string.Join(", ", alive.OrderByDescending(n => n.Gdp).Take(10)
                .Select(n => $"{n.Tag} {n.Gdp:N0} ({n.Gdp * 1e9 / Math.Max(1, n.Population):N0}/head)")));
            var shortPairs = alive.Sum(n => n.Market.Values.Count(q => q.Demand > 0.01f && q.IsShortage));
            var pairs = alive.Sum(n => n.Market.Values.Count(q => q.Demand > 0.01f));
            var alivePeople = (double)Math.Max(1L, alive.Sum(n => n.Population));
            Console.WriteLine($"   market: {shortPairs} of {pairs} demanded pairs short; {alive.Count(n => n.ShortageIndex > 0.05f)} countries with shortage index over 5%; " +
                              $"population-weighted shortage index {alive.Sum(n => (double)n.ShortageIndex * n.Population) / alivePeople:0.0000}; goods {CommodityCatalog.All.Count()}");
            Console.WriteLine("   goods (made/asked, price x base): " + string.Join(", ", CommodityCatalog.All.OrderBy(CommodityCatalog.IdOf).Select(good =>
            {
                var made = alive.Sum(n => n.Market.TryGetValue(good, out var q) ? (double)q.Production : 0);
                var asked = alive.Sum(n => n.Market.TryGetValue(good, out var q) ? (double)q.Demand : 0);
                var paid = alive.Sum(n => n.Market.TryGetValue(good, out var q) ? (double)q.Demand * q.Price : 0);
                var price = asked > 0.001 ? paid / asked / CommodityCatalog.Get(good).BasePrice : 0;
                return $"{CommodityCatalog.IdOf(good)} {made / Math.Max(0.001, asked):0.00} x{price:0.00}";
            })));
            foreach (var tag in (Environment.GetEnvironmentVariable("DOF_GROWTH_TAGS") ?? "USA,CHN,IND,ITA,FRA,DEU,JPN,RUS")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var n = campaign.NationByTag(tag.Trim());
                if (n is null || n.Population <= 0) continue;
                Console.WriteLine($"   {n.Tag}: gdp {n.Gdp:N0}, gross {n.GrossOutput:0.###}, scale {n.OutputScale:0.####}, " +
                                  $"real {n.Market.Values.Sum(q => q.Production * CommodityCatalog.Get(q.Commodity).BasePrice):0.###}, " +
                                  $"productivity {n.Productivity:0.###}, employed {n.EmployedShare:P0} (jobs {campaign.StatesOf(n).Sum(MarketSystem.JobsDemanded):0.0}M of {campaign.StatesOf(n).Sum(MarketSystem.WorkersAvailable):0.0}M, returns {MarketSystem.CapitalReturns(campaign, n, campaign.StatesOf(n).Sum(MarketSystem.JobsDemanded), campaign.StatesOf(n).Sum(MarketSystem.WorkersAvailable)):0.00}), drift {n.SecularDrift * 100:+0.00;-0.00}%, " +
                                  $"pop {n.Population / 1e6:0.0}M, stab {n.Stability:0.00}, hc {n.HumanCapital:0.00}, debt {n.DebtCondition}, " +
                                  $"sites {campaign.Construction.Count(p => p.NationId == n.Id)}/{ConstructionCost.Limit(campaign, n, campaign.Defines.MaxConstructionProjects)}, " +
                                  $"treasury {n.Treasury:0.0}, capacity {n.ConstructionCapacity:0.00}, literacy {n.Literacy:0.00}, sol {n.StandardOfLiving:0.00}, " +
                                  $"price level {ConstructionCost.PriceLevel(campaign, n):0.00}");
                Console.WriteLine("      goods: " + string.Join(", ", n.Market.Values
                    .OrderByDescending(q => q.Production * CommodityCatalog.Get(q.Commodity).BasePrice).Take(6)
                    .Select(q => $"{q.Commodity} {q.Production * CommodityCatalog.Get(q.Commodity).BasePrice:0.0} (sat {q.Satisfaction:P0}, price x{q.Price / MathF.Max(0.01f, CommodityCatalog.Get(q.Commodity).BasePrice):0.00})")));
                Console.WriteLine("      buildings: " + string.Join(", ", campaign.StatesOf(n).SelectMany(s => s.Buildings)
                    .GroupBy(p => p.Key).Select(g => (g.Key, Count: g.Sum(p => p.Value))).OrderByDescending(g => g.Count)
                    .Select(g => $"{g.Key} {g.Count}")) + $"; output factor {NationalModifiers.Factor(n, "output"):0.00}");
            }
        }
        void Tallies()
        {
            var built = AiStatecraft.Tally.Where(p => p.Key.StartsWith("construction", StringComparison.Ordinal))
                .OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}");
            Console.WriteLine("   construction tally: " + string.Join(", ", built));
            AiStatecraft.Tally.Clear();
        }
        Report();
        for (var day = 1; day <= growthDays; day++)
        {
            campaign.TickDay();
            if (day % growthEvery == 0 || day == growthDays) { Report(); Tallies(); }
        }
        return 0;
    }

    [SimBenchReport("--civil-war-probe", SimBenchPhase.PostOpening, Order = 1107,
        Description = "Inspect civil war outbreak eligibility and chances")]
    public static int RunCivilWarProbe(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var probeAt = ctx.IndexOf("--civil-war-probe");
        var probeDays = probeAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[probeAt + 1], out var pdays) ? pdays : 365;
        campaign.IsPaused = false;
        for (var day = 0; day < probeDays; day++) campaign.TickDay();
        Console.WriteLine($"CIVIL WAR PROBE day {campaign.ElapsedDays}: starts in the last year {CivilWarOutbreak.StartsInLastYear(campaign)} of {campaign.Defines.CivilWarWorldPerYear}");
        foreach (var nation in campaign.Nations.Where(n => n.Movement is null && n.Population > 0 && campaign.StatesOf(n).Length > 0)
                     .OrderBy(n => n.Stability).Take(20))
            Console.WriteLine($"  {nation.Tag} stability {nation.Stability:0.00} regions {campaign.StatesOf(nation).Length} " +
                              $"chance {CivilWarOutbreak.Chance(campaign, nation):0.0000} last {nation.LastCivilWarDay}: " +
                              $"{CivilWarOutbreak.EligibilityReason(campaign, nation)}");
        return 0;
    }

    [SimBenchReport("--migration", SimBenchPhase.PostOpening, Order = 1140,
        Description = "Track net migration and largest cultural demographic shifts")]
    public static int RunMigration(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var migrationAt = ctx.IndexOf("--migration");
        var migrationDays = migrationAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[migrationAt + 1], out var md) ? md : 3650;
        var before = campaign.States.ToDictionary(s => s.MapIndex, s => s.PopulationGroups
            .GroupBy(g => g.Culture ?? "").ToDictionary(g => g.Key, g => g.Sum(x => x.Size)));
        campaign.IsPaused = false;
        for (var day = 1; day <= migrationDays; day++) campaign.TickDay();
        foreach (var state in campaign.States.OrderByDescending(s => s.Population))
        {
            var was = before.GetValueOrDefault(state.MapIndex) ?? new Dictionary<string, long>();
            foreach (var group in state.PopulationGroups.GroupBy(g => g.Culture ?? ""))
            {
                var now = group.Sum(g => g.Size);
                var gained = now - was.GetValueOrDefault(group.Key);
                if (gained >= 1_000_000 || gained >= 100_000 && gained >= state.Population / 10)
                    Console.WriteLine($"MIGRATION {state.DisplayName} ({campaign.NationsByMapIndex().GetValueOrDefault((ushort)state.NationMapIndex)?.Tag}): " +
                                      $"{group.Key} {was.GetValueOrDefault(group.Key):N0} -> {now:N0} of {state.Population:N0}");
            }
        }
        foreach (var n in campaign.Nations.Where(n => n.CumulativeNetMigration != 0).OrderByDescending(n => Math.Abs(n.CumulativeNetMigration)).Take(15))
            Console.WriteLine($"MIGRATION net {n.Tag}: {n.CumulativeNetMigration:N0} of {n.Population:N0}");
        return 0;
    }

    [SimBenchReport("--pulse", SimBenchPhase.PostOpening, Order = 1164,
        Description = "Comprehensive multi-year pulse telemetry across warfare, defcon, land, treasury, and strikes")]
    public static int RunPulse(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var pulseAt = ctx.IndexOf("--pulse");
        var pulseDays = pulseAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[pulseAt + 1], out var pd) ? pd : 3650;
        campaign.IsPaused = false;
        var year = new Dictionary<string, int>();
        var total = new Dictionary<string, int>();
        long pulseSeen = 0;
        long covertSeen = 0;
        var covert = new Dictionary<string, int>();
        var largest = 0;
        var bornFrom = campaign.Nations.Count;
        var defconDays = new int[6];
        var holderWars = new HashSet<string>();
        var lastDefcon = campaign.DefconLevel;
        for (var day = 1; day <= pulseDays; day++)
        {
            campaign.TickDay();
            defconDays[Math.Clamp(campaign.DefconLevel, 1, 5)]++;
            foreach (var war in campaign.Wars)
            {
                if (war.Ended || war.CivilConflict) continue;
                var holdersA = war.Side(true).Where(id => Deterrence.TierOf(campaign, campaign.NationById(id)) >= 1).ToList();
                var holdersD = war.Side(false).Where(id => Deterrence.TierOf(campaign, campaign.NationById(id)) >= 1).ToList();
                if (holdersA.Count == 0 || holdersD.Count == 0) continue;
                var key = $"{war.Id}:{string.Join(",", holdersA)}:{string.Join(",", holdersD)}";
                if (!holderWars.Add(key)) continue;
                string Tags(IEnumerable<int> ids) => string.Join("+", ids.Select(id => campaign.NationById(id)?.Tag));
                Console.WriteLine($"   HOLDERS AT WAR day {day}: {campaign.NationById(war.AttackerId)?.Tag} -> {campaign.NationById(war.DefenderId)?.Tag} " +
                                  $"(started day {war.StartedDay}, {war.BelligerentCount} in it): {Tags(holdersA)} vs {Tags(holdersD)}; attackers {Tags(war.Side(true))}; defenders {Tags(war.Side(false))}");
            }
            if (campaign.DefconLevel != lastDefcon)
            {
                DefconSystem.EvaluateTarget(campaign, out var why);
                Console.WriteLine($"   DEFCON day {day}: {lastDefcon} -> {campaign.DefconLevel} ({why})");
                lastDefcon = campaign.DefconLevel;
            }
            var entries = campaign.Chronicle.Read(-1, -1, true);
            foreach (var entry in entries.Where(e => e.Sequence > pulseSeen))
            {
                year[entry.Kind] = year.GetValueOrDefault(entry.Kind) + 1;
                total[entry.Kind] = total.GetValueOrDefault(entry.Kind) + 1;
                if (entry.Kind == "war_started") Console.WriteLine($"   WAR day {entry.Day}: {entry.ActorName} -> {entry.TargetName}");
                if (entry.Kind is "civil_war_third_front" or "movement_merged" or "movement_absorbed" or "movement_dissolved" or "movement_emerged" or "coup")
                    Console.WriteLine($"   {entry.Kind.ToUpperInvariant()} day {entry.Day}: {entry.ActorName} -> {entry.TargetName} {entry.Subject}");
                if (entry.Kind == "volunteers_sent") Console.WriteLine($"   VOLUNTEERS day {entry.Day}: {entry.ActorName} -> {entry.TargetName} ({entry.Place})");
                if (entry.Kind is "nuclear_strike" or "nuclear_retaliation" or "organization_sanction" or "sanctions_imposed")
                    Console.WriteLine($"   NUCLEAR day {entry.Day}: {entry.Kind} {entry.ActorName} -> {entry.TargetName} ({entry.Subject}{entry.Place})");
            }
            if (entries.Length > 0) pulseSeen = Math.Max(pulseSeen, entries.Max(e => e.Sequence));
            foreach (var entry in campaign.Chronicle.Entries)
            {
                if (entry.Sequence <= covertSeen) continue;
                covertSeen = entry.Sequence;
                if (!entry.Public && entry.Kind.StartsWith("covert_", StringComparison.Ordinal))
                    covert[entry.Kind] = covert.GetValueOrDefault(entry.Kind) + 1;
            }
            foreach (var war in campaign.Wars) if (!war.Ended && !war.CivilConflict) largest = Math.Max(largest, war.BelligerentCount);
            if (day % 365 != 0 && day != pulseDays) continue;
            Console.WriteLine($"PULSE year to day {campaign.ElapsedDays} ({campaign.Date}): largest war between states {largest}; " +
                              string.Join(", ", year.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key).Select(entry => $"{entry.Key} {entry.Value}")));
            year.Clear();
            largest = 0;
        }
        Console.WriteLine($"PULSE defcon days: 5={defconDays[5]} 4={defconDays[4]} 3={defconDays[3]} 2={defconDays[2]} 1={defconDays[1]}; peak {campaign.DefconPeakLevel}");
        Console.WriteLine($"PULSE world GDP {campaign.Nations.Where(n => n.Population > 0).Sum(n => (double)n.Gdp):N0} bn; " + WorldMarketLine(campaign));
        Console.WriteLine("PULSE total: " + string.Join(", ", total.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key).Select(entry => $"{entry.Key} {entry.Value}")));
        Console.WriteLine($"PULSE army: formations lost {campaign.Nations.Sum(n => n.FormationsLost)}, surrendered {WarLosses.SurrenderedFormations}, " +
                          $"starved in pockets {Attrition.StarvedInPockets}; outcomes " +
                          string.Join(", ", Warfare.OutcomeTally.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(8).Select(p => $"{p.Key} {p.Value}")));
        Console.WriteLine("PULSE garrisons: " + string.Join(", ", StrandedGarrisons.Tally.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value}")) +
                          $"; in pockets now {campaign.Armies.Count(a => a.CutOff)} (holding out {campaign.Armies.Count(a => a.HoldoutSinceDay >= 0)}), " +
                          $"below 75% strength {campaign.Armies.Count(a => a.Strength < 0.75f)} of {campaign.Armies.Count}");
        Console.WriteLine("PULSE covert (private): " + string.Join(", ", covert.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key).Select(entry => $"{entry.Key} {entry.Value}")));
        Console.WriteLine("PULSE failure: " + string.Join(", ", StateFailure.Tally.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key} {entry.Value}")) +
                          $"; born in play alive {campaign.Nations.Skip(bornFrom).Count(n => !campaign.Fallen.Contains(n.Id) && n.Population > 0 && n.Movement is null)} of {campaign.Nations.Count - bornFrom}");
        foreach (var line in StateFailure.Log) Console.WriteLine("PULSE failure " + line);
        Console.WriteLine($"PULSE land: leased={LandArrangementSystem.Leased} entered={LandArrangementSystem.Entered} " +
                          $"annexed={LandArrangementSystem.Annexed} fought={LandArrangementSystem.Fought} stayed={LandArrangementSystem.Stayed} " +
                          $"withdrew={LandArrangementSystem.Withdrew} republics={LandArrangementSystem.Republics}; passed over: " + string.Join(" ", LandArrangementSystem.Passed.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")) + "; standing: " + string.Join(", ", campaign.LandArrangements.Select(a =>
                              $"{a.Kind} {campaign.NationById(a.HolderId)?.Tag} in {LandArrangementSystem.RegionNames(campaign, a)} ({campaign.NationById(a.OwnerId)?.Tag})")));
        var treasuries = campaign.Nations.Where(n => n.Movement is null && n.Gdp > 0f && n.Population > 0)
            .OrderByDescending(n => n.Gdp).Take(20).Select(n => n.Treasury / n.Gdp).OrderBy(x => x).ToList();
        Console.WriteLine($"PULSE treasury: crises={TreasuryDynamics.Crises} contagions={TreasuryDynamics.Contagions} " +
                          $"disasters={TreasuryDynamics.Disasters} pandemics={TreasuryDynamics.Pandemics} spent={TreasuryDynamics.Spent:0} " +
                          $"leaked={TreasuryDynamics.Leaked:0} eroded={TreasuryDynamics.Eroded:0} leverage={campaign.WorldLeverage:0.00}; " +
                          $"top-20 treasury/GDP min {treasuries.FirstOrDefault():0.00} median {treasuries.ElementAtOrDefault(treasuries.Count / 2):0.00} max {treasuries.LastOrDefault():0.00}; " +
                          string.Join(", ", campaign.Nations.Where(n => n.Movement is null && n.Gdp > 0f).OrderByDescending(n => n.Gdp).Take(8)
                              .Select(n => $"{n.Tag} {n.Treasury:0}/{n.Gdp:0}")));
        Console.WriteLine($"PULSE long-range: salvos={LongRangeStrikes.Salvos} dry={LongRangeStrikes.DryLaunches} " +
                          $"launched={LongRangeStrikes.LaunchedPoints:0} intercepted={LongRangeStrikes.InterceptedPoints:0} " +
                          $"landed={LongRangeStrikes.LandedPoints:0} procured={LongRangeStrikes.Procured}; targets " +
                          string.Join(" ", LongRangeStrikes.ByTarget.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")) +
                          "; on strike now " + campaign.SupportFormations.Count(f => f.Mission == SupportCore.MissionStrike) +
                          " of " + campaign.SupportFormations.Count + " formations; missions " +
                          string.Join(" ", campaign.SupportFormations.GroupBy(f => f.Mission).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));
        Console.WriteLine("PULSE land restraint: " + string.Join(", ", new[] { "RUS", "CHN", "TUR", "USA", "IND", "ISR", "AZE", "ARM", "SRB", "IRN", "PRK", "DEU", "FRA", "GBR", "VEN", "HUN", "BLR", "SAU", "EGY", "PAK" }
            .Select(tag => campaign.NationByTag(tag)).Where(n => n is not null).Select(n => $"{n.Tag} {AiStatecraft.Restraint(campaign, n):0.00} ({n.Government}/{n.Ideology})")));
        foreach (var ended in LandArrangementSystem.EndedLog) Console.WriteLine("PULSE land ended " + ended);
        foreach (var born in campaign.Nations.Skip(bornFrom))
            Console.WriteLine($"PULSE born {born.Tag} \"{born.DisplayName}\" from {campaign.NationById(born.SecededFromId)?.Tag ?? "-"} " +
                              $"people {string.Join("/", born.FoundedCultures.Take(2))} regions {campaign.StatesOf(born).Length}" +
                              (born.Movement is not null ? " movement" : ""));
        Console.WriteLine($"PULSE borders: reviews={BorderDiplomacy.Reviews} swaps={BorderDiplomacy.Swaps} purchases={BorderDiplomacy.Purchases} " +
                          $"sales={BorderDiplomacy.Sales} ultimatums={BorderDiplomacy.Ultimatums} (yielded {BorderDiplomacy.UltimatumsYielded}) disputes={BorderDiplomacy.Disputes}");
        Console.WriteLine("PULSE land pairs: " + string.Join("; ", LandArrangementSystem.PassedPairs.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}")));
        foreach (var entry in campaign.Chronicle.Entries.Where(e => e.Kind is "lease_voided_by_war" or "troops_without_war" or "annexed_by_referendum" or "republic_proclaimed" or "presence_became_war" or "lease_overstayed" or "lease_granted"))
            Console.WriteLine($"PULSE land event day {entry.Day}: {entry.Kind} {campaign.NationById(entry.ActorId)?.Tag} -> {campaign.NationById(entry.TargetId)?.Tag} {entry.Place} {entry.Subject}");
        return 0;
    }

    [SimBenchReport("--border-deals", SimBenchPhase.PostOpening, Order = 1287,
        Description = "Track bilateral border grievances, swaps, purchases, sales, and ultimatums")]
    public static int RunBorderDeals(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var bordersAt = ctx.IndexOf("--border-deals");
        var borderDays = bordersAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[bordersAt + 1], out var bd) ? bd : 3650;
        campaign.IsPaused = false;
        BorderDiplomacy.ResetCounters();
        string Tag(int mapIndex) => campaign.NationsByMapIndex().GetValueOrDefault((ushort)mapIndex)?.Tag ?? mapIndex.ToString();
        void ShowPairs(string when)
        {
            var pairs = BorderShape.Pairs(campaign);
            Console.WriteLine($"BORDERS {when}: {pairs.Count} grievance pairs, {pairs.Count(pair => pair.Grievance >= campaign.Defines.BorderDealMinGrievance)} at or above the review line; worst: " +
                              string.Join(", ", pairs.Take(16).Select(pair => $"{Tag(pair.Nation)}>{Tag(pair.Other)} {pair.Grievance:0.00}")));
        }
        ShowPairs("at start");
        var counts = new Dictionary<string, int>();
        var year = new Dictionary<string, int>();
        long borderSeen = 0;
        for (var day = 1; day <= borderDays; day++)
        {
            campaign.TickDay();
            var entries = campaign.Chronicle.Read(-1, -1, true);
            foreach (var entry in entries.Where(e => e.Sequence > borderSeen).OrderBy(e => e.Sequence))
            {
                var kind = entry.Kind;
                if (kind == "war_started")
                {
                    var a = campaign.NationById(entry.ActorId);
                    var b = campaign.NationById(entry.TargetId);
                    if (a is null || b is null || BorderShape.Grievance(campaign, a, b) <= 0f && BorderShape.Grievance(campaign, b, a) <= 0f) continue;
                    if (!campaign.Wars.Any(w => !w.Ended && !w.CivilConflict && w.AttackerId == a.Id && w.DefenderId == b.Id)) continue;
                    kind = "border_war";
                }
                else if (!kind.StartsWith("border_", StringComparison.Ordinal) && kind is not ("ultimatum_accepted" or "ultimatum_refused")) continue;
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                year[kind] = year.GetValueOrDefault(kind) + 1;
                if (kind.StartsWith("border_", StringComparison.Ordinal))
                    Console.WriteLine($"   {kind.ToUpperInvariant()} day {entry.Day}: {entry.ActorName} / {entry.TargetName} {entry.Subject}");
            }
            if (entries.Length > 0) borderSeen = Math.Max(borderSeen, entries.Max(e => e.Sequence));
            if (day % 365 != 0 && day != borderDays) continue;
            Console.WriteLine($"BORDERS year to day {campaign.ElapsedDays}: " + string.Join(", ", year.OrderBy(e => e.Key).Select(e => $"{e.Key} {e.Value}")));
            year.Clear();
        }
        ShowPairs("at end");
        Console.WriteLine($"BORDERS total: " + string.Join(", ", counts.OrderBy(e => e.Key).Select(e => $"{e.Key} {e.Value}")));
        Console.WriteLine($"BORDERS counters: reviews={BorderDiplomacy.Reviews} swaps={BorderDiplomacy.Swaps} purchases={BorderDiplomacy.Purchases} " +
                          $"sales={BorderDiplomacy.Sales} offered-to-players={BorderDiplomacy.Offered} ultimatums={BorderDiplomacy.Ultimatums} " +
                          $"(yielded {BorderDiplomacy.UltimatumsYielded}) disputes={BorderDiplomacy.Disputes}");
        return 0;
    }

    [SimBenchReport("--orgs", SimBenchPhase.PostOpening, Order = 1340,
        Description = "Track international organization memberships, cohesion, and suspensions")]
    public static int RunOrgs(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var orgsAt = ctx.IndexOf("--orgs");
        var orgDays = orgsAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[orgsAt + 1], out var od) ? od : 7300;
        var orgEvery = orgsAt + 2 < ctx.Args.Length && int.TryParse(ctx.Args[orgsAt + 2], out var oe) ? oe : 730;
        campaign.IsPaused = false;
        var counts = new Dictionary<string, int>();
        long orgSeen = 0;
        for (var day = 1; day <= orgDays; day++)
        {
            campaign.TickDay();
            var entries = campaign.Chronicle.Read(-1, -1, true);
            foreach (var entry in entries.Where(e => e.Sequence > orgSeen && e.Kind.StartsWith("organization_", StringComparison.Ordinal)))
            {
                counts[entry.Kind] = counts.GetValueOrDefault(entry.Kind) + 1;
                Console.WriteLine($"   {entry.Kind.Substring(13).ToUpperInvariant()} day {entry.Day}: {entry.ActorName}{(string.IsNullOrEmpty(entry.TargetName) ? "" : " / " + entry.TargetName)} - {entry.Subject}");
            }
            if (entries.Length > 0) orgSeen = Math.Max(orgSeen, entries.Max(e => e.Sequence));
            if (day % orgEvery != 0 && day != orgDays) continue;
            var active = InternationalOrganizations.Active(campaign).ToList();
            Console.WriteLine($"ORGS day {campaign.ElapsedDays} ({campaign.Date}): {active.Count} active, {active.Sum(o => o.Members.Count)} memberships, " +
                              $"mean cohesion {(active.Count == 0 ? 0 : active.Average(o => o.Cohesion)):0.00}; " +
                              string.Join(", ", counts.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key.Substring(13)} {entry.Value}")));
            foreach (var org in active.Where(o => o.Suspended.Count > 0))
                Console.WriteLine($"   SUSPENDED in {InternationalOrganizations.DisplayName(campaign, org)}: " +
                                  string.Join(", ", org.Suspended.Select(s => $"{campaign.NationById(s.Key)?.Tag} since day {s.Value} ({org.SuspendedFor.GetValueOrDefault(s.Key)})")));
            foreach (var org in active.Where(o => o.Members.Count >= 3).OrderByDescending(o => o.Members.Count).Take(8))
                Console.WriteLine($"   {InternationalOrganizations.DisplayName(campaign, org)}: {org.Members.Count} members, cohesion {org.Cohesion:0.00} (target {OrganizationDynamics.Target(campaign, org):0.00}), " +
                                  $"least belonging {org.Members.Select(campaign.NationById).Where(n => n is not null).Select(n => (n.Tag, OrganizationDynamics.Belonging(campaign, org, n))).OrderBy(x => x.Item2).Take(2).Select(x => $"{x.Tag} {x.Item2:0.00}").DefaultIfEmpty("-").Aggregate((a, b) => a + " " + b)}");
        }
        return 0;
    }

    [SimBenchReport("--unions", SimBenchPhase.PostOpening, Order = 1375,
        Description = "Track formable union consolidation ambition and progress")]
    public static int RunUnions(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var unionsAt = ctx.IndexOf("--unions");
        var unionDays = unionsAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[unionsAt + 1], out var ud) ? ud : 3650;
        var unionEvery = unionsAt + 2 < ctx.Args.Length && int.TryParse(ctx.Args[unionsAt + 2], out var ue) ? ue : 365;
        campaign.IsPaused = false;
        for (var day = 1; day <= unionDays; day++)
        {
            campaign.TickDay();
            if (day % unionEvery != 0 && day != unionDays) continue;
            var leaders = campaign.Nations.Where(n => !string.IsNullOrEmpty(n.ConsolidationProject)).OrderBy(n => n.Tag).ToList();
            Console.WriteLine($"UNIONS day {campaign.ElapsedDays} ({campaign.Date}): {leaders.Count} project(s)");
            foreach (var n in leaders)
            {
                var focus = campaign.NationById(n.ConsolidationFocusId);
                var opinion = focus is null ? 0f : CampaignDiplomacy.Get(campaign, n.Id, focus.Id)?.Opinion ?? 0f;
                Console.WriteLine($"   {n.Tag} -> {n.ConsolidationProject} ambition {n.ConsolidationAmbition:0.00} focus {focus?.Tag ?? "-"} opinion {opinion:0} " +
                                  $"willing {(focus is null ? 0f : PeacefulFormation.Willingness(campaign, n, focus)):0} stability {n.Stability:0.00} {n.Ideology}" +
                                  (focus is null || FormableNations.Find(n.ConsolidationProject) is not { } union ? "" :
                                      " | " + PeacefulFormation.Refusal(campaign, n, focus, union) +
                                      " | missing: " + string.Join("; ", FormableNations.Evaluate(campaign, n, union).Requirements.Where(r => !r.Met).Select(r => r.Label))));
            }
            foreach (var n in campaign.Nations.Where(n => !string.IsNullOrEmpty(n.FormedNationId)))
                Console.WriteLine($"   FORMED {n.Tag} as {n.FormedNationId} on day {n.FormedNationDay}, holds {campaign.StatesOf(n).Length} regions");
        }
        return 0;
    }

    [SimBenchReport("--fronts", SimBenchPhase.PostOpening, Order = 1427,
        Description = "Report front post coverage and holes over N days")]
    public static int RunFronts(SimBenchContext ctx)
    {
        var frontsAt = ctx.IndexOf("--fronts");
        var frontDays = frontsAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[frontsAt + 1], out var fd) ? fd : 180;
        return FrontsReport(ctx.Campaign, frontDays);
    }

    [SimBenchReport("--sea", SimBenchPhase.PostOpening, Order = 1431,
        Description = "Render sea-lane routing diagnostics image")]
    public static int RunSea(SimBenchContext ctx)
    {
        var seaAt = ctx.IndexOf("--sea");
        return SeaReport(ctx.Campaign, seaAt + 1 < ctx.Args.Length ? ctx.Args[seaAt + 1] : "sea_lanes.png");
    }

    [SimBenchReport("--opportunity-report", SimBenchPhase.PostOpening, Order = 1484,
        Description = "Measure AI war declaration opportunism against target strength window")]
    public static int RunOpportunityReport(SimBenchContext ctx)
    {
        var at = ctx.IndexOf("--opportunity-report");
        var opDays = at + 1 < ctx.Args.Length && int.TryParse(ctx.Args[at + 1], out var d) ? d : 730;
        var opWin = at + 2 < ctx.Args.Length && int.TryParse(ctx.Args[at + 2], out var w) ? w : 365;
        return OpportunityReport(ctx.Campaign, opDays, opWin);
    }

    [SimBenchReport("--war-judgement", SimBenchPhase.PostOpening, Order = 1494,
        Description = "Diagnose AI neighbour war judgement scores")]
    public static int RunWarJudgement(SimBenchContext ctx)
    {
        var at = ctx.IndexOf("--war-judgement");
        var jDays = at + 1 < ctx.Args.Length && int.TryParse(ctx.Args[at + 1], out var d) ? d : 365;
        return WarJudgementReport(ctx.Campaign, jDays);
    }
}
