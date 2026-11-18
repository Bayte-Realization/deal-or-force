using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

internal enum SimBenchPhase
{
    PreCampaign,
    PostLoadCensus,
    BeforeMap,
    PreWarm,
    AfterMap,
    PostOpening
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
internal sealed class SimBenchReportAttribute : Attribute
{
    public SimBenchReportAttribute(string flag, SimBenchPhase phase, int order = 0, string description = "")
    {
        Flag = flag;
        Phase = phase;
        Order = order;
        Description = description;
    }

    public string Flag { get; }
    public SimBenchPhase Phase { get; }
    public int Order { get; set; }
    public string Description { get; set; }
}

internal sealed class SimBenchContext
{
    public required string[] Args { get; init; }
    public required int Days { get; init; }
    public required int Seed { get; init; }
    public Campaign Campaign { get; set; } = null!;
    public HashSet<string> BookmarkTags { get; set; } = new(StringComparer.Ordinal);
    public bool Mapped { get; set; }
    public string AuthoredStatistics { get; set; } = "";
    public Stopwatch SetupWatch { get; init; } = null!;

    public bool Has(string flag) => Array.IndexOf(Args, flag) >= 0;
    public int IndexOf(string flag) => Array.IndexOf(Args, flag);

    public string Arg(string flag, int offset = 1)
    {
        var idx = IndexOf(flag);
        return idx >= 0 && idx + offset < Args.Length ? Args[idx + offset] : null;
    }

    public int IntArg(string flag, int defaultValue, int offset = 1, NumberStyles styles = NumberStyles.Integer)
    {
        var idx = IndexOf(flag);
        return idx >= 0 && idx + offset < Args.Length &&
               int.TryParse(Args[idx + offset], styles, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }
}

internal sealed record RegisteredSimBenchReport(
    string Flag,
    SimBenchPhase Phase,
    int Order,
    string Description,
    Func<SimBenchContext, int?> TryRun);

internal static class SimBenchReportRegistry
{
    private static readonly string[] ModifierFlags =
    {
        "--load",
        "--calendar",
        "--define",
        "--warm",
        "--out",
        "--join",
        "--war-of",
        "--provoke",
        "--century-save",
        "--diet",
        "--fold-now",
        "--fold-first",
        "--group-census",
        "--frames",
        "--frame-days"
    };

    private static readonly Lazy<IReadOnlyList<RegisteredSimBenchReport>> CachedReports =
        new(DiscoverAndBuildReports);

    public static IReadOnlyList<RegisteredSimBenchReport> Reports => CachedReports.Value;

    public static IReadOnlyList<string> AllFlags()
    {
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var report in Reports)
            flags.Add(report.Flag);
        foreach (var modifier in ModifierFlags)
            flags.Add(modifier);
        return flags.ToArray();
    }

    public static bool TryDispatch(SimBenchPhase phase, SimBenchContext ctx, out int exitCode)
    {
        foreach (var report in Reports)
        {
            if (report.Phase != phase) continue;
            var result = report.TryRun(ctx);
            if (!result.HasValue) continue;
            exitCode = result.Value;
            return true;
        }
        exitCode = 0;
        return false;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: SimBench [days] [seed] [report-flag] [options]");
        Console.WriteLine();
        Console.WriteLine($"Registered report flags ({Reports.Count} reports, {AllFlags().Count} total flags):");
        foreach (var report in Reports.OrderBy(r => r.Flag, StringComparer.Ordinal))
        {
            var desc = string.IsNullOrEmpty(report.Description) ? "" : $" - {report.Description}";
            Console.WriteLine($"  {report.Flag,-32} [{report.Phase}]{desc}");
        }
        Console.WriteLine();
        Console.WriteLine("Modifier / input flags:");
        foreach (var flag in ModifierFlags)
            Console.WriteLine($"  {flag}");
    }

    private static IReadOnlyList<RegisteredSimBenchReport> DiscoverAndBuildReports()
    {
        var list = new List<RegisteredSimBenchReport>();
        AddAttributeReports(list);
        AddExternalReports(list);
        list.Sort(static (a, b) =>
        {
            var phaseCmp = a.Phase.CompareTo(b.Phase);
            if (phaseCmp != 0) return phaseCmp;
            var orderCmp = a.Order.CompareTo(b.Order);
            return orderCmp != 0 ? orderCmp : StringComparer.Ordinal.Compare(a.Flag, b.Flag);
        });
        return list;
    }

    private static void AddAttributeReports(List<RegisteredSimBenchReport> list)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        foreach (var type in typeof(SimBenchReportRegistry).Assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(flags))
            {
                var attrs = method.GetCustomAttributes<SimBenchReportAttribute>(inherit: false).ToArray();
                if (attrs.Length == 0) continue;

                var parameters = method.GetParameters();
                Func<SimBenchContext, int?> runner;
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(SimBenchContext) && method.ReturnType == typeof(int?))
                {
                    runner = (Func<SimBenchContext, int?>)Delegate.CreateDelegate(typeof(Func<SimBenchContext, int?>), method);
                }
                else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(SimBenchContext) && method.ReturnType == typeof(int))
                {
                    var fn = (Func<SimBenchContext, int>)Delegate.CreateDelegate(typeof(Func<SimBenchContext, int>), method);
                    runner = ctx => fn(ctx);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"[SimBenchReport] method {type.FullName}.{method.Name} must take (SimBenchContext) and return int or int?.");
                }

                foreach (var attr in attrs)
                {
                    list.Add(new RegisteredSimBenchReport(
                        attr.Flag,
                        attr.Phase,
                        attr.Order,
                        attr.Description,
                        ctx =>
                        {
                            if (!ctx.Has(attr.Flag)) return null;
                            return runner(ctx);
                        }));
                }
            }
        }
    }

    private static void AddExternalReports(List<RegisteredSimBenchReport> list)
    {
        void Add(string flag, SimBenchPhase phase, int order, string description, Func<SimBenchContext, int?> tryRun)
            => list.Add(new RegisteredSimBenchReport(flag, phase, order, description, tryRun));

        Add("--world-build", SimBenchPhase.PreCampaign, 46, "Phase-by-phase world-building timing",
            ctx => ctx.IndexOf("--world-build") is var idx and >= 0
                ? WorldBuildReport.Run(ctx.Seed, ctx.IntArg("--world-build", 1))
                : null);

        Add("--write-regions", SimBenchPhase.BeforeMap, 160, "Write bookmark regions to folder",
            ctx => ctx.IndexOf("--write-regions") is var idx and >= 0 && idx + 1 < ctx.Args.Length
                ? RegionWriter.Write(ctx.Campaign, ctx.Args[idx + 1])
                : null);

        Add("--dump-regions", SimBenchPhase.BeforeMap, 162, "Dump dealt region table to file",
            ctx => ctx.IndexOf("--dump-regions") is var idx and >= 0 && idx + 1 < ctx.Args.Length
                ? RegionWriter.Dump(ctx.Campaign, ctx.Args[idx + 1])
                : null);

        Add("--diagnose-territory", SimBenchPhase.PreWarm, 271, "Diagnose territory attribution on map",
            ctx => ctx.IndexOf("--diagnose-territory") is var idx and >= 0 && idx + 2 < ctx.Args.Length
                ? (ctx.Mapped ? TerritoryDiagnosisReport.Run(ctx.Campaign, ctx.Args[idx + 1], ctx.Args[idx + 2]) : 2)
                : null);

        Add("--diagnose-war", SimBenchPhase.AfterMap, 292, "Diagnose a war or replay save",
            ctx => ctx.IndexOf("--diagnose-war") is var idx and >= 0 && idx + 1 < ctx.Args.Length
                ? (ctx.Mapped ? WarDiagnosisReport.Run(ctx.Campaign, ctx.Args[idx + 1],
                    idx + 2 < ctx.Args.Length && int.TryParse(ctx.Args[idx + 2], out var replayDays) ? replayDays : 0) : 2)
                : null);

        Add("--peace-watch", SimBenchPhase.AfterMap, 297, "Day-by-day watch of a war between two tags",
            ctx => ctx.IndexOf("--peace-watch") is var idx and >= 0 && idx + 2 < ctx.Args.Length
                ? (ctx.Mapped ? PeaceWatchReport.Run(ctx.Campaign, ctx.Args[idx + 1], ctx.Args[idx + 2],
                    idx + 3 < ctx.Args.Length && int.TryParse(ctx.Args[idx + 3], out var watchDays)
                        ? Math.Clamp(watchDays, 1, 3650) : 365) : 2)
                : null);

        Add("--waterfront-check", SimBenchPhase.AfterMap, 302, "Verify waterfront references",
            ctx => ctx.Has("--waterfront-check")
                ? (ctx.Mapped ? WaterfrontReferenceCheck.Run(ctx.Campaign) : 2)
                : null);

        Add("--trade-profile", SimBenchPhase.AfterMap, 304, "Profile trade evaluation latency",
            ctx => ctx.IndexOf("--trade-profile") is var idx and >= 0
                ? (ctx.Mapped ? TradeLatencyReport.Run(ctx.Campaign,
                    idx + 1 < ctx.Args.Length && int.TryParse(ctx.Args[idx + 1], out var tradeRepeats) ? tradeRepeats : 20) : 2)
                : null);

        Add("--opening-territory-regressions", SimBenchPhase.AfterMap, 314, "Verify opening territory checks reject real corruption",
            ctx => ctx.Has("--opening-territory-regressions")
                ? BenchChecks.Check("opening territory checks reject real corruption",
                    OpeningTerritoryChecks.Regressions(ctx.Campaign, BenchChecks.ProvincesSitWhereTheirOwnerIs, BenchChecks.EveryNationHasTerritory))
                : null);

        Add("--integrity-save", SimBenchPhase.AfterMap, 321, "Check campaign integrity on a loaded save",
            ctx => ctx.IndexOf("--integrity-save") is var idx and >= 0 && idx + 1 < ctx.Args.Length
                ? CampaignIntegrityReport.Run(ctx.Campaign, ctx.Args[idx + 1],
                    idx + 2 < ctx.Args.Length && int.TryParse(ctx.Args[idx + 2], out var replayDays) ? replayDays : 0)
                : null);

        Add("--save-roundtrip-file", SimBenchPhase.AfterMap, 328, "Round-trip save file and inspect halves",
            ctx => ctx.IndexOf("--save-roundtrip-file") is var idx and >= 0 && idx + 1 < ctx.Args.Length
                ? SaveRoundTripReport.Run(ctx.Campaign, ctx.Args[idx + 1],
                    idx + 2 < ctx.Args.Length && int.TryParse(ctx.Args[idx + 2], out var trips) ? trips : 3,
                    idx + 3 < ctx.Args.Length && !ctx.Args[idx + 3].StartsWith("--") ? ctx.Args[idx + 3] : null)
                : null);

        Add("--soak-selftest", SimBenchPhase.AfterMap, 337, "Run WorldSoakReport self-test",
            ctx => ctx.Has("--soak-selftest") ? WorldSoakReport.SelfTest(ctx.Campaign) : null);

        Add("--soak", SimBenchPhase.AfterMap, 338, "Long headless soak report",
            ctx => ctx.Has("--soak")
                ? (!ctx.Mapped ? 2 : WorldSoakReport.Run(ctx.Campaign, ctx.Days, ctx.Seed, ctx.Arg("--out")))
                : null);

        Add("--construction", SimBenchPhase.AfterMap, 346, "Construction economy vs revenue",
            ctx => ctx.Has("--construction") ? (ctx.Mapped ? ConstructionEconomyReport.Run(ctx.Campaign, ctx.Days) : 2) : null);

        Add("--radicalism", SimBenchPhase.AfterMap, 347, "Radicalism lifecycle probe",
            ctx => ctx.Has("--radicalism") ? RadicalismReport.Run(ctx.Campaign, ctx.Days) : null);

        Add("--kin", SimBenchPhase.AfterMap, 348, "Kin statecraft ladder bench",
            ctx => ctx.Has("--kin") ? (ctx.Mapped ? KinBenchReport.Run(ctx.Campaign, ctx.Days) : 2) : null);

        Add("--deals", SimBenchPhase.AfterMap, 351, "Deals and refusal telemetry",
            ctx => ctx.Has("--deals") ? (ctx.Mapped ? DealReport.Run(ctx.Campaign, ctx.Days) : 2) : null);

        Add("--exiles", SimBenchPhase.AfterMap, 352, "Exile government probe",
            ctx => ctx.Has("--exiles") ? (ctx.Mapped ? ExileReport.Run(ctx.Campaign, ctx.Days) : 2) : null);

        Add("--movement-births", SimBenchPhase.AfterMap, 354, "Movement birth triggers report",
            ctx => ctx.Has("--movement-births") ? (ctx.Mapped ? MovementBirthReport.Run(ctx.Campaign, ctx.Days) : 2) : null);

        Add("--conquest", SimBenchPhase.AfterMap, 357, "Conquest dynamics report",
            ctx => ctx.Has("--conquest") ? (ctx.Mapped ? ConquestReport.Run(ctx.Campaign, ctx.Days, ctx.Args) : 2) : null);

        Add("--wars", SimBenchPhase.AfterMap, 358, "Report wars older than a year",
            ctx => ctx.Has("--wars") ? (ctx.Mapped ? WarStuckReport.Run(ctx.Campaign, ctx.Days) : 2) : null);

        Add("--long-range", SimBenchPhase.AfterMap, 360, "Long-range strike & air-defence telemetry",
            ctx => ctx.Has("--long-range") ? (ctx.Mapped ? LongRangeReport.Run(ctx.Campaign, ctx.Days) : 2) : null);

        Add("--border-probe", SimBenchPhase.AfterMap, 362, "Border probe for specific tags",
            ctx => ctx.IndexOf("--border-probe") is var idx and >= 0 && idx + 2 < ctx.Args.Length
                ? (ctx.Mapped ? BorderProbeReport.Run(ctx.Campaign, ctx.Args[idx + 1], ctx.Args[idx + 2], ctx.Days,
                    ctx.Arg("--join"), ctx.Arg("--war-of")) : 2)
                : null);

        Add("--ai-wars", SimBenchPhase.AfterMap, 367, "AI war initiation breakdown",
            ctx => ctx.Has("--ai-wars") ? AiWarReport.Run(ctx.Campaign, ctx.Days) : null);

        Add("--fiscal-pulse", SimBenchPhase.AfterMap, 368, "Fiscal and debt pulse",
            ctx => ctx.Has("--fiscal-pulse") ? FiscalPulseReport.Run(ctx.Campaign, ctx.Days) : null);

        Add("--gdp-watch", SimBenchPhase.AfterMap, 369, "GDP trajectory watch",
            ctx => ctx.Has("--gdp-watch") ? GdpWatchReport.Run(ctx.Campaign, ctx.Days, ctx.Args) : null);

        Add("--diagnose-snapshot", SimBenchPhase.AfterMap, 370, "Inspect NaN/Inf trap on snapshot save",
            ctx => ctx.IndexOf("--diagnose-snapshot") is var idx and >= 0 && idx + 1 < ctx.Args.Length
                ? NanTrapReport.RunSnapshot(ctx.Campaign, ctx.Args[idx + 1],
                    idx + 2 < ctx.Args.Length && int.TryParse(ctx.Args[idx + 2], out var snapshotDays) ? snapshotDays : 28)
                : null);

        Add("--nan-trap", SimBenchPhase.AfterMap, 374, "Run NaN/Inf arithmetic trap",
            ctx => ctx.Has("--nan-trap") ? NanTrapReport.Run(ctx.Campaign, ctx.Days) : null);

        Add("--aging", SimBenchPhase.AfterMap, 375, "Demography aging curve probe",
            ctx => ctx.Has("--aging") ? (ctx.Mapped ? AgingReport.Run(ctx.Campaign, ctx.Days) : 2) : null);

        Add("--opinion", SimBenchPhase.AfterMap, 377, "Bilateral opinion drift probe",
            ctx => ctx.Has("--opinion") ? OpinionReport.Run(ctx.Campaign, ctx.Days, ctx.Arg("--provoke") ?? "") : null);

        Add("--survival", SimBenchPhase.AfterMap, 383, "Nation survival and collapse probe",
            ctx => ctx.Has("--survival") ? SurvivalReport.Run(ctx.Campaign, ctx.Days) : null);

        Add("--protests", SimBenchPhase.AfterMap, 384, "Domestic protests probe",
            ctx => ctx.Has("--protests") ? ProtestReport.Run(ctx.Campaign, ctx.Days) : null);

        Add("--traits", SimBenchPhase.AfterMap, 385, "Ruler/movement traits protest probe",
            ctx => ctx.Has("--traits") ? ProtestReport.Traits(ctx.Campaign, ctx.Days) : null);

        Add("--peace", SimBenchPhase.AfterMap, 386, "Peace negotiation report",
            ctx => ctx.Has("--peace") ? PeaceReport.Run(ctx.Campaign, ctx.Days) : null);

        Add("--navy", SimBenchPhase.AfterMap, 387, "Naval warfare peace report",
            ctx => ctx.Has("--navy") ? PeaceReport.Navy(ctx.Campaign, ctx.Days) : null);

        Add("--why", SimBenchPhase.PostOpening, 538, "Peace acceptance diagnosis for a tag",
            ctx => ctx.IndexOf("--why") is var idx and >= 0 && idx + 1 < ctx.Args.Length
                ? PeaceReport.Why(ctx.Campaign, ctx.Days, ctx.Args[idx + 1])
                : null);

        Add("--ai-appetite", SimBenchPhase.PostOpening, 542, "AI territorial appetite report",
            ctx => ctx.Has("--ai-appetite") ? AiAppetiteReport.Run(ctx.Campaign, ctx.Days) : null);

        Add("--secession-war", SimBenchPhase.PostOpening, 545, "Secession war report for region/culture",
            ctx => ctx.IndexOf("--secession-war") is var idx and >= 0 && idx + 1 < ctx.Args.Length
                ? SecessionWarReport.Run(ctx.Campaign, ctx.Days, ctx.Args[idx + 1],
                    idx + 2 < ctx.Args.Length && !ctx.Args[idx + 2].StartsWith("--") ? ctx.Args[idx + 2] : "")
                : null);

        Add("--factions", SimBenchPhase.PostOpening, 550, "Faction lifecycle report",
            ctx => ctx.Has("--factions") ? (ctx.Mapped ? FactionReport.Run(ctx.Campaign, ctx.Days) : 2) : null);

        Add("--pockets", SimBenchPhase.PostOpening, 553, "Encircled pocket supply & attrition report",
            ctx => ctx.Has("--pockets") ? (ctx.Mapped ? PocketReport.Run(ctx.Campaign, ctx.Days) : 2) : null);

        Add("--stability", SimBenchPhase.PostOpening, 574, "Single-country stability breakdown",
            ctx => ctx.IndexOf("--stability") is var idx and >= 0 && idx + 1 < ctx.Args.Length
                ? (ctx.Mapped ? StabilityReport.Run(ctx.Campaign, ctx.Days, ctx.Args[idx + 1]) : 2)
                : null);

        Add("--judge", SimBenchPhase.PostOpening, 576, "Faction war judgement probe",
            ctx => ctx.IndexOf("--judge") is var idx and >= 0 && idx + 1 < ctx.Args.Length
                ? (ctx.Mapped ? FactionReport.Judge(ctx.Campaign, ctx.Days, ctx.Args[idx + 1]) : 2)
                : null);

        Add("--provinces", SimBenchPhase.PostOpening, 578, "Province breakdown for a tag",
            ctx => ctx.IndexOf("--provinces") is var idx and >= 0 && idx + 1 < ctx.Args.Length
                ? FactionReport.Provinces(ctx.Campaign, ctx.Args[idx + 1])
                : null);

        Add("--war-ledger", SimBenchPhase.PostOpening, 580, "War ledger breakdown",
            ctx => ctx.IndexOf("--war-ledger") is var idx and >= 0
                ? WarLedgerReport.Run(ctx.Campaign, ctx.Days,
                    idx + 1 < ctx.Args.Length && !ctx.Args[idx + 1].StartsWith("--") ? ctx.Args[idx + 1] : "")
                : null);

        Add("--land", SimBenchPhase.PostOpening, 585, "Land arrangement telemetry",
            ctx => ctx.Has("--land") ? (ctx.Mapped ? LandReport.Run(ctx.Campaign, ctx.Days) : 2) : null);

        Add("--alliances", SimBenchPhase.PostOpening, 589, "Alliance formation and breakup report",
            ctx => ctx.Has("--alliances") ? AllianceReport.Run(ctx.Campaign, ctx.Days) : null);

        Add("--trade-why", SimBenchPhase.PostOpening, 913, "Trade route refusal probe",
            ctx => ctx.IndexOf("--trade-why") is var idx and >= 0
                ? TradeWhyReport.Run(ctx.Campaign, ctx.Args, idx)
                : null);

        Add("--personnel-lifecycle", SimBenchPhase.PostOpening, 1472, "Long-run political lifecycle probe",
            ctx => ctx.Has("--personnel-lifecycle") ? PoliticalLifecycleProbe.Run(ctx.Campaign, Math.Min(ctx.Days, 36500)) : null);

        Add("--staff-watch", SimBenchPhase.PostOpening, 1473, "Staff assignment watch probe",
            ctx => ctx.Has("--staff-watch") ? StaffWatchProbe.Run(ctx.Campaign, Math.Min(ctx.Days, 3650)) : null);

        Add("--diplomacy-world", SimBenchPhase.PostOpening, 1474, "World diplomacy telemetry probe",
            ctx => ctx.Has("--diplomacy-world") ? DiplomaticWorldProbe.Run(ctx.Campaign, Math.Min(ctx.Days, 3650)) : null);

        Add("--front-disengagement-world", SimBenchPhase.PostOpening, 1475, "Front disengagement telemetry probe",
            ctx => ctx.Has("--front-disengagement-world") ? FrontDisengagementProbe.Run(ctx.Campaign, ctx.Days) : null);
    }
}
