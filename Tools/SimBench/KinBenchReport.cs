using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Whether the kin-statecraft ladder actually happens in a world nobody is playing.
///
/// There is no player in the bench, so anything recorded here was done by an AI on its own
/// initiative - which is exactly the question "self-emergence" asks. The world chronicle is a
/// ring buffer of 2048 lines, so flows are counted incrementally by sequence number; reading
/// the buffer at the end would report the last two thousand lines of ten years and call it a total.
/// </summary>
internal static class KinBenchReport
{
    static readonly string[] Tracked =
    {
        "kin_agitation", "kin_agitation_exposed", "kin_volunteers", "kin_volunteers_exposed",
        "proxy_founded", "proxy_founded_exposed", "administration_opened", "administration_closed",
        "administration_shut", "administration_exposed", "civil_war", "secession"
    };

    public static int Run(Campaign campaign, int days)
    {
        campaign.IsPaused = false;
        var tally = new Dictionary<string, int>();
        var seen = campaign.Chronicle.Entries.Count == 0 ? 0L : campaign.Chronicle.Entries.Max(e => e.Sequence);
        foreach (var entry in campaign.Chronicle.Entries)
            if (Tracked.Contains(entry.Kind)) tally[entry.Kind] = tally.GetValueOrDefault(entry.Kind) + 1;
        Report(campaign, tally, 0);

        for (var day = 1; day <= days; day++)
        {
            campaign.TickDay();
            // Take what this day produced before the ring buffer can drop it.
            foreach (var entry in campaign.Chronicle.Entries)
            {
                if (entry.Sequence <= seen) continue;
                seen = entry.Sequence;
                if (Tracked.Contains(entry.Kind)) tally[entry.Kind] = tally.GetValueOrDefault(entry.Kind) + 1;
            }
            if (day % 365 == 0 || day == days) Report(campaign, tally, day);
        }
        return Verdict(campaign, tally);
    }

    private static void Report(Campaign c, Dictionary<string, int> tally, int day)
    {
        int N(string kind) => tally.GetValueOrDefault(kind);
        var offices = c.States.Where(ParallelAdministration.Any).ToArray();
        var sponsored = c.Nations.Where(n => n.Movement is { Landless: true } && n.Movement.SponsoredBy.Count > 0).ToArray();
        Console.WriteLine($"DAY {c.ElapsedDays}: agit={N("kin_agitation")}+{N("kin_agitation_exposed")}exposed " +
                          $"volunteers={N("kin_volunteers")}+{N("kin_volunteers_exposed")}exposed " +
                          $"proxies={N("proxy_founded")}+{N("proxy_founded_exposed")}exposed live={sponsored.Length} " +
                          $"offices={offices.Length} opened={N("administration_opened")} " +
                          $"closed={N("administration_closed")} shut={N("administration_shut")} exposed={N("administration_exposed")} " +
                          $"civilwar={N("civil_war")} secession={N("secession")}");
        if (day == 0 || offices.Length == 0) return;
        // Which hosts are paying for an office, and out of what: an office that takes more than
        // the host holds is not a foothold, it is a wound.
        var byHost = offices.GroupBy(s => s.AdministeredById)
            .Select(g => (Host: g.Key, Regions: g.Count(), Taken: g.Sum(s => s.AdministrationTaken)))
            .OrderByDescending(g => g.Taken).Take(3);
        foreach (var host in byHost)
        {
            var nation = c.Nations.FirstOrDefault(n => n.Id == host.Host);
            Console.WriteLine($"  OFFICE host={nation?.Tag ?? "?"} regions={host.Regions} taken={host.Taken:F3} " +
                              $"treasury={nation?.Treasury:F3} gdpWeek={(nation is null ? 0f : nation.Gdp / 52f):F3}");
        }
    }

    /// <summary>Broken invariants only: a measurement tool that also votes on the world it measures is useless.</summary>
    private static int Verdict(Campaign c, Dictionary<string, int> tally)
    {
        var problems = new List<string>();
        foreach (var region in c.States.Where(ParallelAdministration.Any))
        {
            if (c.Nations.All(n => n.Id != region.AdministeredById))
                problems.Add($"{region.DisplayName}: administered by missing country {region.AdministeredById}");
            if (region.AdministrationTaken < 0)
                problems.Add($"{region.DisplayName}: took {region.AdministrationTaken}");
            if (region.AdministeredSinceDay > c.ElapsedDays || region.AdministeredSinceDay < 0)
                problems.Add($"{region.DisplayName}: since day {region.AdministeredSinceDay}, today {c.ElapsedDays}");
        }
        foreach (var nation in c.Nations.Where(n => n.Movement is { Landless: true } && n.Movement.SponsoredBy.Count > 0))
            foreach (var sponsor in nation.Movement.SponsoredBy.Keys)
                if (c.Nations.All(n => n.Id != sponsor))
                    problems.Add($"{nation.Tag}: sponsored by missing country {sponsor}");
        var opened = tally.GetValueOrDefault("administration_opened");
        var closed = tally.GetValueOrDefault("administration_closed") + tally.GetValueOrDefault("administration_shut");
        if (closed > opened) problems.Add($"closed {closed} administrations but opened only {opened}");
        foreach (var text in problems.Distinct()) Console.WriteLine("FAULT: " + text);
        Console.WriteLine(problems.Count == 0
            ? "Kin bench PASS: every office has a living host and every proxy a living sponsor."
            : $"Kin bench FAIL: {problems.Distinct().Count()} broken invariant(s).");
        return problems.Count == 0 ? 0 : 1;
    }
}
