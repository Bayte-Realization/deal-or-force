using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// A repeatable full-world endurance run. The weekly economy and AI, wars, movements,
/// secession and actual province map all run; observations are sparse so the observer
/// cannot become the bottleneck. Hard failures are broken invariants, while worrying
/// trends are reported as evidence instead of being "fixed" by a brittle test threshold.
/// </summary>
internal static class WorldSoakReport
{
    private sealed class Result
    {
        public int Seed { get; set; }
        public int RequestedDays { get; set; }
        public int HoursPerTick { get; set; }
        public int CompletedDays { get; set; }
        public List<Snapshot> Checkpoints { get; } = new();
        public List<string> Failures { get; } = new();
        public List<string> Alerts { get; } = new();
    }

    private sealed class Snapshot
    {
        public int Day { get; set; }
        public int Nations { get; set; }
        public int Regions { get; set; }
        public int ActiveWars { get; set; }
        public int OldestWarDays { get; set; }
        public int QuietWarsOverSixMonths { get; set; }
        public int SaturatedRegions { get; set; }
        public int RecoveringRegions { get; set; }
        public double PopulationWeightedRadicalism { get; set; }
        public double WorldGdp { get; set; }
        public int CountriesWithSevereDeficits { get; set; }
        public int CountriesWithHighInflation { get; set; }
        public double MeanStability { get; set; }
        public double MillisecondsPerDay { get; set; }
        public double AllocatedKilobytesPerDay { get; set; }
        public string[] OldestWars { get; set; } = Array.Empty<string>();
        public string[] QuietWars { get; set; } = Array.Empty<string>();
        public string[] LargestGdpDeclines { get; set; } = Array.Empty<string>();
    }

    public static int Run(Campaign campaign, int days, int seed, string outputPath)
    {
        var result = new Result { Seed = seed, RequestedDays = days, HoursPerTick = campaign.TickHours };
        var startingGdp = campaign.Nations.ToDictionary(n => n.Id, n => n.Gdp);
        var initialWorldGdp = campaign.Nations.Sum(n => (double)Math.Max(0, n.Gdp));
        var peaks = new Dictionary<int, float>();
        campaign.IsPaused = false;
        Console.WriteLine($"World soak: seed={seed}, days={days}, step={campaign.TickHours}h, " +
                          $"sample every 90 days; initial GDP={initialWorldGdp:0.0}B");
        Sample(campaign, result, startingGdp, initialWorldGdp, peaks, 0, 0);

        var watch = Stopwatch.StartNew();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var intervalDays = 0;
        for (var day = 1; day <= days; day++)
        {
            campaign.TickDay();
            intervalDays++;
            if (day % 90 != 0 && day != days) continue;
            watch.Stop();
            var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            Sample(campaign, result, startingGdp, initialWorldGdp, peaks,
                watch.Elapsed.TotalMilliseconds / intervalDays, allocated / 1024.0 / intervalDays);
            if (result.Failures.Count > 0) break;
            intervalDays = 0;
            allocatedBefore = GC.GetTotalAllocatedBytes(true);
            watch.Restart();
        }

        result.CompletedDays = campaign.ElapsedDays;
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var absolute = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(absolute, JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Machine-readable report: {absolute}");
        }
        foreach (var alert in result.Alerts.Distinct()) Console.WriteLine("ALERT: " + alert);
        foreach (var failure in result.Failures.Distinct()) Console.WriteLine("FAIL: " + failure);
        Console.WriteLine($"Soak {(result.Failures.Count == 0 ? "PASS" : "FAIL")}: " +
                          $"{result.CompletedDays}/{days} days, {result.Alerts.Count} alert(s), " +
                          $"{result.Failures.Count} invariant failure(s)");
        return result.Failures.Count == 0 ? 0 : 1;
    }

    private static void Sample(Campaign c, Result result, Dictionary<int, float> startingGdp,
        double initialWorldGdp, Dictionary<int, float> peaks, double msPerDay, double kbPerDay)
    {
        var issues = Inspect(c);
        foreach (var issue in issues) result.Failures.Add($"day {c.ElapsedDays}: {issue}");
        var wars = c.Wars.Where(w => !w.Ended).ToArray();
        var population = c.States.Sum(s => Math.Max(0L, s.Population));
        var weightedRadicalism = population == 0 ? 0 : c.States.Sum(s =>
            Math.Max(0L, s.Population) * (double)(float.IsFinite(s.Radicalism) ? s.Radicalism : 0)) / population;
        var recovering = 0;
        foreach (var state in c.States)
        {
            var radicalism = float.IsFinite(state.Radicalism) ? state.Radicalism : 0;
            var peak = Math.Max(radicalism, peaks.GetValueOrDefault(state.MapIndex));
            peaks[state.MapIndex] = peak;
            if (peak >= .25f && radicalism <= peak - .05f) recovering++;
        }
        var worldGdp = c.Nations.Sum(n => (double)(float.IsFinite(n.Gdp) ? Math.Max(0, n.Gdp) : 0));
        var severeDeficits = c.Nations.Count(n => n.Gdp > 1 && n.WeeklyBalance < -n.Gdp / 520f);
        var highInflation = c.Nations.Count(n => n.Inflation >= .25f);
        var quietWars = wars.Where(w => c.ElapsedDays - w.StartedDay >= 180 &&
            w.AttackerLosses + w.DefenderLosses < .001f).ToArray();
        var snapshot = new Snapshot
        {
            Day = c.ElapsedDays,
            Nations = c.Nations.Count,
            Regions = c.States.Count,
            ActiveWars = wars.Length,
            OldestWarDays = wars.Length == 0 ? 0 : wars.Max(w => c.ElapsedDays - w.StartedDay),
            QuietWarsOverSixMonths = quietWars.Length,
            SaturatedRegions = c.States.Count(s => s.Radicalism >= .99f),
            RecoveringRegions = recovering,
            PopulationWeightedRadicalism = weightedRadicalism,
            WorldGdp = worldGdp,
            CountriesWithSevereDeficits = severeDeficits,
            CountriesWithHighInflation = highInflation,
            MeanStability = c.Nations.Average(n => float.IsFinite(n.Stability) ? n.Stability : 0),
            MillisecondsPerDay = msPerDay,
            AllocatedKilobytesPerDay = kbPerDay,
            OldestWars = wars.OrderByDescending(w => c.ElapsedDays - w.StartedDay).Take(5)
                .Select(w => $"{c.NationById(w.AttackerId)?.Tag}->{c.NationById(w.DefenderId)?.Tag} " +
                             $"age={c.ElapsedDays - w.StartedDay} score={w.WarScore:0.0} " +
                             $"losses={w.AttackerLosses + w.DefenderLosses:0.000}").ToArray(),
            QuietWars = quietWars.Select(w => DescribeQuietWar(c, w)).ToArray(),
            LargestGdpDeclines = c.Nations.Where(n => startingGdp.GetValueOrDefault(n.Id) >= 1f && n.Gdp >= 0)
                .OrderBy(n => n.Gdp / startingGdp[n.Id]).Take(5)
                .Select(n => $"{n.Tag} {n.Gdp / startingGdp[n.Id]:P0} of start").ToArray()
        };
        result.Checkpoints.Add(snapshot);
        Console.WriteLine($"DAY {snapshot.Day,5}: rad={weightedRadicalism:P1} " +
                          $"sat={snapshot.SaturatedRegions}/{snapshot.Regions} " +
                          $"recovered={recovering} GDP={worldGdp:0}B " +
                          $"wars={wars.Length} oldest={snapshot.OldestWarDays}d " +
                          $"quiet={snapshot.QuietWarsOverSixMonths} deficits={severeDeficits} " +
                          $"inflation>25%={highInflation} {msPerDay:0.0}ms/day {kbPerDay:0}kB/day");
        if (snapshot.OldestWarDays >= 365)
            Console.WriteLine("  oldest: " + string.Join("; ", snapshot.OldestWars));
        foreach (var quiet in snapshot.QuietWars) Console.WriteLine("  quiet: " + quiet);
        if (c.ElapsedDays >= 365 && worldGdp < initialWorldGdp * .25)
            result.Failures.Add($"day {c.ElapsedDays}: world GDP fell below 25% of its starting level");
        if (c.ElapsedDays >= 730 && snapshot.SaturatedRegions > snapshot.Regions / 4)
            result.Alerts.Add($"day {c.ElapsedDays}: more than a quarter of regions have 99% radicalism");
        foreach (var war in quietWars)
        {
            var prefix = $"war {war.Id}:";
            if (!result.Alerts.Any(a => a.StartsWith(prefix, StringComparison.Ordinal)))
                result.Alerts.Add($"{prefix} no fighting after {c.ElapsedDays - war.StartedDay} days " +
                    $"({c.NationById(war.AttackerId)?.Tag}->{c.NationById(war.DefenderId)?.Tag})");
        }
        if (highInflation > c.Nations.Count / 5)
            result.Alerts.Add($"day {c.ElapsedDays}: more than a fifth of countries exceed 25% inflation");
    }

    private static string DescribeQuietWar(Campaign c, StrategicWar war)
    {
        var attacker = c.NationById(war.AttackerId);
        var defender = c.NationById(war.DefenderId);
        var attackField = c.Armies.Where(a => war.IsAttacking(a.NationId)).ToArray();
        var defendField = c.Armies.Where(a => war.IsDefending(a.NationId)).ToArray();
        var ordered = attackField.Count(a => a.AssignedWarId == war.Id);
        var moving = attackField.Count(a => a.IsMoving);
        var control = c.ProvinceController;
        var changed = control is null ? 0 : war.OriginalController.Count(p =>
            p.Key > 0 && p.Key < control.Length && control[p.Key] != p.Value);
        return $"{attacker?.Tag}->{defender?.Tag} id={war.Id} age={c.ElapsedDays - war.StartedDay} " +
               $"score={war.WarScore:0.0} fronts={war.AttackerFront.Count}/{war.DefenderFront.Count} " +
               $"armies={attackField.Length}/{defendField.Length} assigned={ordered} moving={moving} " +
               $"landless={attacker?.Landless}/{defender?.Landless} controlChanged={changed} " +
               $"ready={AiStatecraft.CanStageWar(c, attacker, defender)} " +
               $"proposal={war.LastPeaceProposalDay} pending={war.PendingOfferFromId} " +
               $"negotiation='{war.LastNegotiation}'";
    }

    private static List<string> Inspect(Campaign c)
    {
        var failures = new List<string>();
        foreach (var state in c.States)
        {
            if (state.Population < 0) failures.Add($"region {state.Id}: negative population");
            if (!float.IsFinite(state.Radicalism) || state.Radicalism < 0 || state.Radicalism > 1)
                failures.Add($"region {state.Id}: invalid radicalism {state.Radicalism}");
            if (!float.IsFinite(state.StandardOfLiving) || state.StandardOfLiving < 0 || state.StandardOfLiving > 1)
                failures.Add($"region {state.Id}: invalid living standard {state.StandardOfLiving}");
        }
        foreach (var nation in c.Nations)
        {
            if (nation.Population < 0 || !float.IsFinite(nation.Gdp) || nation.Gdp < 0 ||
                !float.IsFinite(nation.Treasury) || !float.IsFinite(nation.Inflation) ||
                !float.IsFinite(nation.WeeklyBalance) || !float.IsFinite(nation.Stability) ||
                nation.Stability < 0 || nation.Stability > 1)
                failures.Add($"country {nation.Tag}: invalid population/economy/stability");
        }
        foreach (var war in c.Wars.Where(w => !w.Ended))
        {
            if (!float.IsFinite(war.WarScore) || !float.IsFinite(war.AttackerLosses) ||
                !float.IsFinite(war.DefenderLosses))
                failures.Add($"war {war.Id}: non-finite score or losses");
            var human = c.NationById(war.AttackerId)?.IsHuman == true ||
                        c.NationById(war.DefenderId)?.IsHuman == true ||
                        war.AttackerAllies.Any(id => c.NationById(id)?.IsHuman == true) ||
                        war.DefenderAllies.Any(id => c.NationById(id)?.IsHuman == true);
            var expiry = Math.Max(war.StartedDay + c.Defines.PeaceForcedEndDays,
                                  war.AiNegotiationUnlockDay);
            if (!human && c.ElapsedDays > expiry + 30)
                failures.Add($"war {war.Id}: AI war outlived forced peace by over 30 days");
        }
        return failures;
    }

    public static int SelfTest(Campaign c)
    {
        var state = c.States[0];
        var nation = c.Nations[0];
        var originalRadicalism = state.Radicalism;
        var originalGdp = nation.Gdp;
        try
        {
            if (Inspect(c).Count != 0) throw new Exception("baseline campaign is already invalid");
            state.Radicalism = float.NaN;
            if (!Inspect(c).Any(x => x.Contains("invalid radicalism")))
                throw new Exception("radicalism detector missed NaN");
            state.Radicalism = originalRadicalism;
            nation.Gdp = float.PositiveInfinity;
            if (!Inspect(c).Any(x => x.Contains("invalid population/economy/stability")))
                throw new Exception("economy detector missed infinity");
            Console.WriteLine("PASS: healthy baseline, invalid radicalism and non-finite GDP detection");
            return 0;
        }
        finally
        {
            state.Radicalism = originalRadicalism;
            nation.Gdp = originalGdp;
        }
    }
}
