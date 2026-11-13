using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--aging [days]`: does a day cost more in year ten than in year one, and what grew?
///
/// A thousand-year campaign is only playable if a day's cost does not climb with the calendar.
/// Two things make it climb: a pass whose work grows with history (it walks every war ever
/// fought, every memory ever made), and a collection that is appended to and never pruned. This
/// measures both, year by year: each pass's time and garbage per day against its first year, and
/// every collection held by the campaign, by its countries and by their relations, found by
/// reflection so a list nobody thought to name is still counted.
/// </summary>
internal static class AgingReport
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        Dictionary<string, (double Ms, long Bytes)> firstYear = null;
        var firstSizes = Sizes(c);
        Console.WriteLine($"Aging: {days} day(s), step {c.TickHours}h.");
        var startedDay = c.ElapsedDays;
        var yearStart = 0;
        var nextYear = 365;
        SimProfiler.Reset();
        var gc0 = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var allocated = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        double worst = 0;
        while (c.ElapsedDays - startedDay < days)
        {
            var dayClock = Stopwatch.GetTimestamp();
            if (!SimulationReportProgress.TickDay(c, out var failure))
            { Console.WriteLine("AGING_FAIL " + failure); return 1; }
            worst = Math.Max(worst, Stopwatch.GetElapsedTime(dayClock).TotalMilliseconds);
            var day = c.ElapsedDays - startedDay;
            if (day < nextYear && day < days) continue;

            var span = day - yearStart;
            var passes = SimProfiler.All.ToDictionary(s => s.Name, s => (s.TotalMilliseconds / span, s.AllocatedBytes / span));
            firstYear ??= passes;
            var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
            Console.WriteLine($"YEAR to day {day}: {clock.Elapsed.TotalMilliseconds / span:0.0} ms/day, worst day {worst:0} ms, " +
                              $"{bytes / 1024.0 / span:N0} kB/day, GC {GC.CollectionCount(0) - gc0.Item1}/{GC.CollectionCount(1) - gc0.Item2}/{GC.CollectionCount(2) - gc0.Item3}, " +
                              $"heap {GC.GetTotalMemory(false) / 1048576.0:N0} MB, countries {c.Nations.Count}, wars {c.Wars.Count(w => !w.Ended)}/{c.Wars.Count}");
            // Only the top level of the profile: nested scopes are indented and already inside a parent.
            var grown = passes.Where(p => !p.Key.StartsWith(" ", StringComparison.Ordinal))
                .Select(p => (Name: p.Key, Now: p.Value, Then: firstYear.GetValueOrDefault(p.Key)))
                .OrderByDescending(x => x.Now.Item1 - x.Then.Ms).Take(10);
            Console.WriteLine("  passes, ms/day now (year one) kB/day: " + string.Join("; ", grown.Select(x =>
                $"{x.Name.Trim()} {x.Now.Item1:0.0} ({x.Then.Ms:0.0}) {x.Now.Item2 / 1024.0:N0}")));
            var sizes = Sizes(c);
            var growers = sizes.Select(p => (p.Key, Now: p.Value, Then: firstSizes.GetValueOrDefault(p.Key)))
                .Where(x => x.Now > x.Then + 50 && x.Now > x.Then * 1.5)
                .OrderByDescending(x => x.Now - x.Then).Take(14);
            var nested = passes.Where(p => p.Key.StartsWith(" ", StringComparison.Ordinal))
                .Select(p => (Name: p.Key, Now: p.Value, Then: firstYear.GetValueOrDefault(p.Key)))
                .OrderByDescending(x => x.Now.Item1 - x.Then.Ms).Take(10);
            Console.WriteLine("  inner scopes grown: " + string.Join("; ", nested.Select(x =>
                $"{x.Name.Trim()} {x.Now.Item1:0.0} ({x.Then.Ms:0.0})")));
            Console.WriteLine("  collections grown since day 0: " + string.Join("; ", growers.Select(x => $"{x.Key} {x.Then:N0}->{x.Now:N0}")));

            if (Environment.GetEnvironmentVariable("DOF_AGING_SAVES") is { Length: > 0 } folder)
            {
                System.IO.Directory.CreateDirectory(folder);
                SaveSystem.Save(folder, $"aged_{day / 365}y", c, "bench");
            }
            SimProfiler.Reset();
            yearStart = day;
            nextYear = (day / 365 + 1) * 365;
            worst = 0;
            gc0 = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            allocated = GC.GetTotalAllocatedBytes(true);
            clock.Restart();
        }
        return 0;
    }

    /// <summary>Every collection on the campaign, and summed over every country and relation, by field name.</summary>
    private static Dictionary<string, long> Sizes(Campaign c)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        Count(c, "Campaign.", sizes);
        foreach (var nation in c.Nations) Count(nation, "Nation.", sizes);
        foreach (var relation in Relations(c)) Count(relation, "Relation.", sizes);
        foreach (var war in c.Wars) Count(war, "War.", sizes);
        foreach (var state in c.States) Count(state, "State.", sizes);
        return sizes;
    }

    private static IEnumerable<object> Relations(Campaign c)
    {
        var field = typeof(Campaign).GetField("Relations", Fields) ?? (MemberInfo)null as FieldInfo;
        var value = field?.GetValue(c) ?? typeof(Campaign).GetProperty("Relations", Fields)?.GetValue(c);
        if (value is IDictionary dictionary) foreach (var item in dictionary.Values) yield return item;
        else if (value is IEnumerable list) foreach (var item in list) yield return item;
    }

    private static readonly Dictionary<Type, FieldInfo[]> Collections = new();

    private static void Count(object owner, string prefix, Dictionary<string, long> sizes)
    {
        if (owner is null) return;
        var type = owner.GetType();
        if (!Collections.TryGetValue(type, out var fields))
        {
            fields = type.GetFields(Fields).Where(f => typeof(ICollection).IsAssignableFrom(f.FieldType) ||
                f.FieldType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)))
                .Where(f => f.FieldType != typeof(string)).ToArray();
            Collections[type] = fields;
        }
        foreach (var field in fields)
        {
            var value = field.GetValue(owner);
            var count = value switch
            {
                ICollection collection => collection.Count,
                null => 0,
                _ => value.GetType().GetProperty("Count")?.GetValue(value) is int n ? n : 0
            };
            var key = prefix + field.Name.TrimStart('<').Split('>')[0];
            sizes[key] = sizes.GetValueOrDefault(key) + count;
        }
    }
}
