using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DealOrForce.Simulation;

static class PeaceTidyTests
{
    public static void Run(Campaign campaign)
    {
        var map = campaign.ProvinceMap;
        var groups = Enumerable.Range(1, map.ProvinceCount)
            .GroupBy(p => (int)map.ProvinceToState[p])
            .Where(g => g.Key > 0 && g.Count() >= 4)
            .Take(48)
            .Select(g => g.ToArray())
            .ToArray();
        if (groups.Length < 2) throw new Exception("Peace rounding fixture needs two multi-province regions");

        var brush = groups.SelectMany(g => g.Take((g.Length + 1) / 2)).ToArray();
        for (var i = 0; i < 8; i++) PeaceConference.Tidy(campaign, null, brush, 1);
        const int repetitions = 100;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < repetitions; i++) PeaceConference.Tidy(campaign, null, brush, 1);
        clock.Stop();
        Console.WriteLine($"Peace rounding: {map.ProvinceCount} provinces, {groups.Length} regions, " +
                          $"{repetitions} runs in {clock.Elapsed.TotalMilliseconds:0.0} ms");

        var whole = groups[0];
        var fragment = groups[1];
        var drawn = whole.Concat(new[] { fragment[0] }).ToArray();
        var noRoundingUp = PeaceConference.Tidy(campaign, null, drawn, 1, roundUp: false);
        Check(noRoundingUp.ToHashSet().SetEquals(whole),
            "disabled round-up returns the sliver while retaining the complete region");

        var half = fragment.Take((fragment.Length + 1) / 2).ToArray();
        var rounded = PeaceConference.Tidy(campaign, null, half, 1);
        Check(rounded.ToHashSet().SetEquals(fragment), "majority claim expands to one complete region");

        var fallback = PeaceConference.Tidy(campaign, null, new[] { fragment[0] }, 1, roundUp: false);
        Check(fallback.ToHashSet().SetEquals(fragment), "a sole sliver falls back to one complete region");

        var expected = groups.SelectMany(g => g).ToHashSet();
        Check(PeaceConference.Tidy(campaign, null, brush, 1).ToHashSet().SetEquals(expected),
            "large brush resolves every touched majority region");

    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
}
