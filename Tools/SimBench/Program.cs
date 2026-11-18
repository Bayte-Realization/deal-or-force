using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Runs the campaign simulation without Godot: dispatches standalone reports declared via
/// <see cref="SimBenchReportRegistry"/> or runs the full benchmark &amp; invariant suite.
/// Usage: SimBench [days] [seed] [report-flag]   (default 730 - two campaign years)
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var days = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 730;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("Deal or Force - headless simulation bench");
        Console.WriteLine(new string('-', 62));

        if (args.Contains("--help") || args.Contains("-h") || args.Contains("--list-reports"))
        {
            SimBenchReportRegistry.PrintHelp();
            return 0;
        }

        var seed = args.Length > 1 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
                   int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var chosen)
            ? chosen
            : 20260830;

        var ctx = new SimBenchContext
        {
            Args = args,
            Days = days,
            Seed = seed,
            SetupWatch = Stopwatch.StartNew()
        };

        if (SimBenchReportRegistry.TryDispatch(SimBenchPhase.PreCampaign, ctx, out var preExit))
            return preExit;

        var campaign = Campaign.CreateBootstrap(seed);
        var bookmarkTags = new HashSet<string>(
            campaign.Nations.Where(n => !string.IsNullOrWhiteSpace(n.Tag)).Select(n => n.Tag),
            StringComparer.Ordinal);

        var loadAt = Array.IndexOf(args, "--load");
        if (loadAt >= 0 && loadAt + 2 < args.Length)
        {
            var loaded = SaveSystem.Load(args[loadAt + 1], args[loadAt + 2], "");
            if (loaded.Campaign is null) { Console.WriteLine("  load: " + loaded.Warning); return 1; }
            campaign = loaded.Campaign;
            Console.WriteLine($"  loaded {args[loadAt + 2]}: day {campaign.ElapsedDays}, {campaign.Date}");
        }

        ctx.Campaign = campaign;
        ctx.BookmarkTags = bookmarkTags;

        if (SimBenchReportRegistry.TryDispatch(SimBenchPhase.PostLoadCensus, ctx, out var censusExit))
            return censusExit;

        var calendarAt = Array.IndexOf(args, "--calendar");
        if (calendarAt >= 0)
        {
            if (calendarAt + 1 >= args.Length || !HistoricalDate.TryParse(args[calendarAt + 1], out var calendar))
            {
                Console.WriteLine("--calendar requires YYYY-MM-DD, optionally followed by BCE/CE inside the quoted value.");
                return 1;
            }
            campaign.Date = new GameDate(calendar.Year, calendar.Month, calendar.Day);
            Console.WriteLine("  calendar stress origin: " + campaign.Date);
        }

        ctx.SetupWatch.Stop();
        Console.WriteLine($"Campaign created in {ctx.SetupWatch.ElapsedMilliseconds} ms: " +
                          $"{campaign.Nations.Count} nations, {campaign.States.Count} states.");

        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index] != "--define") continue;
            var pair = args[index + 1].Split('=', 2);
            var property = pair.Length == 2 ? typeof(Defines).GetProperty(pair[0]) : null;
            if (property is null || !property.CanWrite)
            {
                Console.WriteLine($"  --define {args[index + 1]}: no such define");
                return 2;
            }
            var value = Convert.ChangeType(pair[1], property.PropertyType, CultureInfo.InvariantCulture);
            property.SetValue(campaign.Defines, value);
            Console.WriteLine($"  define {pair[0]} = {value}");
        }

        if (SimBenchReportRegistry.TryDispatch(SimBenchPhase.BeforeMap, ctx, out var beforeMapExit))
            return beforeMapExit;

        ctx.AuthoredStatistics = BenchChecks.TheStatedStatisticsCameTrue(campaign);
        void DebugRegion(string when)
        {
            var probe = Environment.GetEnvironmentVariable("CLAUDE_REGION_PROBE");
            if (string.IsNullOrEmpty(probe)) return;
            foreach (var s in campaign.States.Where(s => s.Id == probe))
                Console.WriteLine($"PROBE {when}: {s.Id} map {s.MapIndex} owner {s.NationMapIndex} '{s.NationId}' unclaimed {s.Unclaimed} pop {s.Population}");
        }
        DebugRegion("before map");
        ctx.Mapped = BenchChecks.AttachMap(campaign);
        DebugRegion("after map");

        if (SimBenchReportRegistry.TryDispatch(SimBenchPhase.PreWarm, ctx, out var preWarmExit))
            return preWarmExit;

        var warmAt = Array.IndexOf(args, "--warm");
        if (warmAt >= 0 && warmAt + 1 < args.Length && int.TryParse(args[warmAt + 1], out var warmDays))
        {
            campaign.IsPaused = false;
            for (var day = 0; day < warmDays; day++) campaign.TickDay();
        }

        if (SimBenchReportRegistry.TryDispatch(SimBenchPhase.AfterMap, ctx, out var afterMapExit))
            return afterMapExit;

        if (OpeningOccupation.LastScaled.Count > 0)
            Console.WriteLine("  opening occupation: kept the written figure over the regions still held by " +
                              string.Join(" | ", OpeningOccupation.LastScaled.Select(row => $"{row.Tag} x{row.Scale:0.00} [{row.Lost}]")));

        if (SimBenchReportRegistry.TryDispatch(SimBenchPhase.PostOpening, ctx, out var postOpeningExit))
            return postOpeningExit;

        return DefaultBenchRunner.Run(ctx);
    }

    internal static string WorldMarketLine(Campaign campaign) => BenchChecks.WorldMarketLine(campaign);
}
