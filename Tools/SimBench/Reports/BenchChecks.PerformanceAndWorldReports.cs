using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

internal static partial class BenchChecks
{

    /// <summary>
    /// Nanoseconds per SimProfiler.Measure scope: 64 distinct names (a day uses a few hundred,
    /// so the dictionary is not a one-entry toy), a million scopes a sample, five warm-up samples
    /// and 21 measured. The checksum is the profiler's own run count, read back at the end, so
    /// the scopes cannot be optimised away. Allocation is per thread and should be zero.
    /// </summary>
    internal static int ProfilerOverhead()
    {
        var names = Enumerable.Range(0, 64).Select(index => "overhead:" + index).ToArray();
        const int scopes = 1_000_000;
        var samples = new List<double>();
        long allocated = 0;
        for (var sample = 0; sample < 26; sample++)
        {
            SimProfiler.Reset();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            for (var index = 0; index < scopes; index++)
                using (SimProfiler.Measure(names[index & 63])) { }
            clock.Stop();
            var allocatedNow = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            if (sample < 5) continue;
            samples.Add(clock.Elapsed.TotalMilliseconds * 1_000_000.0 / scopes);
            allocated += allocatedNow;
        }
        var checksum = SimProfiler.All.Sum(entry => (long)entry.Runs);
        samples.Sort();
        var tracy =
#if DOF_TRACY
            TracyBridge.On ? (TracyBridge.Live ? "compiled in, on, viewer attached" : "compiled in, on, no viewer") : "compiled in, off";
#else
            "not compiled";
#endif
        Console.WriteLine($"PROFILER_OVERHEAD tracy={tracy} samples={samples.Count} scopes/sample={scopes:N0} " +
                          $"median={samples[samples.Count / 2]:0.0} ns p95={samples[(int)(samples.Count * 0.95)]:0.0} ns " +
                          $"min={samples[0]:0.0} ns allocated_per_sample={allocated / samples.Count} B checksum={checksum}");
        return checksum == scopes ? 0 : 1;
    }

    /// <summary>
    /// Days of the world with the profiler reset first, reported by total time. Each pass is
    /// shown with its share of the whole, its mean cost and how often it ran - a cheap pass
    /// run every hour and a dear one run every month look the same in a mean and nothing
    /// alike in a total.
    /// </summary>
    internal static int ProfileSave(Campaign fresh, string path, int days)
    {
        if (fresh.ProvinceMap is null) { Console.WriteLine("  province map unavailable"); return 2; }
        var watch = Stopwatch.StartNew();
        var loaded = SaveSystem.Load(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path), "");
        if (!loaded.Success || loaded.Campaign is null) { Console.WriteLine($"  could not load {path}: {loaded.Warning}"); return 2; }
        var campaign = loaded.Campaign;
        // The topology the fresh campaign built, handed over the way the game hands it from one
        // campaign to the next; the save lays its own borders back over it.
        campaign.AttachProvinceTopology(fresh.ProvinceMap);
        Console.WriteLine($"Loaded {path} in {watch.ElapsedMilliseconds:N0} ms: {campaign.Date}, day {campaign.ElapsedDays:N0}, " +
                          $"{campaign.Nations.Count(n => n.Population > 0)} countries with people, {campaign.Wars.Count(w => !w.Ended)} war(s).");
        Console.WriteLine($"Retained world: nations={campaign.Nations.Count:N0}, relations={campaign.Relations.Count:N0}, " +
                          $"wars={campaign.Wars.Count:N0} (ended={campaign.Wars.Count(w => w.Ended):N0}), " +
                          $"regions={campaign.States.Count:N0}, armies={campaign.Armies.Count:N0}.");
        // The run's `--define`s are for the world being measured, which here is the loaded one.
        var given = Environment.GetCommandLineArgs();
        for (var index = 0; index + 1 < given.Length; index++)
        {
            if (given[index] != "--define") continue;
            var pair = given[index + 1].Split('=', 2);
            if (pair.Length == 2 && typeof(Defines).GetProperty(pair[0]) is { CanWrite: true } property)
                property.SetValue(campaign.Defines, Convert.ChangeType(pair[1], property.PropertyType, CultureInfo.InvariantCulture));
        }
        // `--diet`: weigh the world, put it through the long-campaign sweep now, weigh it again
        // (LongCampaignReport). Otherwise it is only weighed, and the sweep comes on its own day.
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--group-census") >= 0)
        {
            LongCampaignReport.GroupCensus(campaign);
            return 0;
        }
        // `--fold-now`: fold the slivers once and stop (LongCampaignReport.FoldNow).
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--fold-now") >= 0)
        {
            LongCampaignReport.FoldNow(campaign);
            LongCampaignReport.GroupCensus(campaign);
            return 0;
        }
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--diet") >= 0)
            LongCampaignReport.Diet(campaign, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "diet_out"));
        else LongCampaignReport.Census(campaign, "as loaded");
        // `--fold-first`: fold the slivers before the clock starts. The weekly cycle folds a
        // region the week its turn comes, so a thirty-day profile of a century-old save measures
        // the folding itself; this measures the day after it, which is every day from then on.
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--fold-first") >= 0)
        {
            LongCampaignReport.FoldNow(campaign);
            LongCampaignReport.Census(campaign, "after the fold");
            LongCampaignReport.Sizes(campaign, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "diet_out"), "folded");
        }
        var loadedOn = campaign.ElapsedDays;
        var result = Profile(campaign, days);
        // By the calendar, not by the argument: the profile warms up for three days and follows
        // its measured days with two thirty-day probes, so "after 30 days" was after 93.
        LongCampaignReport.Census(campaign, $"after {campaign.ElapsedDays - loadedOn} day(s)");
        // `--frames 5,3 [--frame-days N]`: after everything above (so the fingerprint it printed
        // still compares with a run without it), the game's own scheduler frame by frame.
        var framesAt = Array.IndexOf(given, "--frames");
        if (framesAt >= 0 && framesAt + 1 < given.Length)
        {
            var frameDaysAt = Array.IndexOf(given, "--frame-days");
            var frameDays = frameDaysAt >= 0 && frameDaysAt + 1 < given.Length && int.TryParse(given[frameDaysAt + 1], out var fd) ? fd : 20;
            foreach (var part in given[framesAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out var speed)) FramesAtSpeed(campaign, speed, frameDays);
        }
        return result;
    }

    /// <summary>
    /// What a player at this speed feels, headless: <see cref="SimulationFrameClock"/> - the loop
    /// DealOrForceGame._Process runs - driven one frame at a time with smooth time on, as shipped.
    ///
    /// A frame's delta is what the game's would be: a sixtieth of a second, or longer when the
    /// last frame's simulation itself took longer (the drawing is not here, so this is the floor
    /// of the real frame, never more than it). The number per frame is the simulation's share of
    /// it - the time the picture waited for the world. That is the number a hitch is.
    /// </summary>
    internal static void FramesAtSpeed(Campaign campaign, int speed, int days)
    {
        campaign.IsPaused = false;
        var clock = new SimulationFrameClock();
        var frames = new List<double>();
        var worst = new List<(double Ms, int Day, string Parts)>();
        var before = new Dictionary<string, double>();
        var stopAt = campaign.ElapsedDays + days;
        var startDay = campaign.ElapsedDays;
        var delta = 1.0 / 60.0;
        var seconds = 0.0;
        while (campaign.ElapsedDays < stopAt && string.IsNullOrEmpty(campaign.SimulationFault) && frames.Count < 2_000_000)
        {
            before.Clear();
            foreach (var sample in SimProfiler.All) before[sample.Name] = sample.TotalMilliseconds;
            var gen2 = GC.CollectionCount(2);
            var pause = GC.GetTotalPauseDuration();
            var started = Stopwatch.GetTimestamp();
            clock.RunFrame(campaign, delta, speed, smooth: true);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            frames.Add(ms);
            seconds += delta;
            if (ms >= 25.0 && (worst.Count < 12 || ms > worst[^1].Ms))
            {
                var parts = SimProfiler.All
                    .Select(sample => (Name: sample.Name, Ms: sample.TotalMilliseconds - before.GetValueOrDefault(sample.Name)))
                    .Where(item => item.Ms >= 1.0 && item.Name != "campaign.day")
                    .OrderByDescending(item => item.Ms).Take(6)
                    .Select(item => $"{item.Name.Trim()} {item.Ms:0}").ToList();
                var collected = GC.CollectionCount(2) - gen2;
                var paused = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
                if (paused >= 1) parts.Add($"gc {paused:0} ms ({collected} gen2)");
                worst.Add((ms, campaign.ElapsedDays, string.Join(", ", parts)));
                worst.Sort((left, right) => right.Ms.CompareTo(left.Ms));
                if (worst.Count > 12) worst.RemoveAt(12);
            }
            delta = Math.Max(1.0 / 60.0, ms / 1000.0);
        }
        if (frames.Count == 0) return;
        var sorted = frames.OrderBy(ms => ms).ToList();
        double At(double share) => sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * share))];
        var budget = SimulationFrameClock.FrameBudgetMs(campaign.Defines, speed, smooth: true);
        Console.WriteLine($"FRAMES speed {speed} (smooth, {budget:0} ms slice, {campaign.ElapsedDays - startDay} days): {frames.Count:N0} frames; " +
                          $"median {At(0.5):0.00} ms, p99 {At(0.99):0.00}, p99.9 {At(0.999):0.00}, worst {sorted[^1]:0.0}; " +
                          $"over 33 ms {frames.Count(ms => ms > 33.3):N0}, over 50 ms {frames.Count(ms => ms > 50.0):N0}; " +
                          $"{(campaign.ElapsedDays - startDay) / Math.Max(0.001, seconds):0.00} days a second.");
        foreach (var (ms, day, parts) in worst)
            Console.WriteLine($"  frame {ms:0.0} ms on day {day}: {parts}");
    }

    /// <summary>
    /// How well the world times its wars. For every state-on-state war that starts in the run,
    /// the target's strength on the day it was struck is set among that same target's strengths
    /// over the days the attacker could have waited instead. The yardstick is the figure the AI
    /// itself weighs a fight with (AiStatecraft.StrengthOf through its daily cache), so a bad
    /// number means a missed window rather than a different ruler. Reported, never asserted:
    /// AI-05 asks for opportunism measured before it is tuned, and a threshold set today would
    /// be a taste written as a contract.
    /// </summary>
    internal static int OpportunityReport(Campaign campaign, int days, int window)
    {
        campaign.IsPaused = false;
        var byNation = new Dictionary<int, Dictionary<int, float>>();
        var known = new HashSet<int>();
        var declared = new List<(int Attacker, int Defender, int Day, bool Engaged)>();
        // Who was fighting on which day. A target's strength after it has been invaded is partly
        // the invasion: measured against those days, "the AI could have waited for a weaker
        // target" is partly "the AI could have waited to hit it, then hit it again". Only days
        // when the target was at peace are kept below, so the window is a window the attacker
        // really could have had.
        var atWar = new HashSet<(int Nation, int Day)>();
        var until = campaign.ElapsedDays + Math.Max(1, days);
        while (campaign.ElapsedDays < until)
        {
            campaign.TickDay();
            var today = campaign.ElapsedDays;
            foreach (var nation in campaign.Nations)
            {
                if (!byNation.TryGetValue(nation.Id, out var byDay))
                    byNation[nation.Id] = byDay = new Dictionary<int, float>();
                byDay[today] = AiStatecraft.CachedStrength(campaign, nation);
            }
            foreach (var live in campaign.Wars)
            {
                if (live.Ended || live.CivilConflict) continue;
                atWar.Add((live.AttackerId, today));
                atWar.Add((live.DefenderId, today));
            }
            foreach (var war in campaign.Wars)
            {
                if (!known.Add(war.Id)) continue;
                // Was the target already bleeding for someone else on the day it was struck?
                // Answered here, while the live list is in front of us - the same question asked
                // at the end of the run would answer a different one.
                var engaged = campaign.Wars.Any(other => other.Id != war.Id && !other.Ended &&
                    !other.CivilConflict && (other.AttackerId == war.DefenderId || other.DefenderId == war.DefenderId));
                declared.Add((war.AttackerId, war.DefenderId, today, engaged));
            }
        }
        var last = campaign.ElapsedDays;

        var timed = new List<(string Attacker, string Defender, int Day, float At, float Low, double Percentile, bool Engaged)>();
        var authored = 0;
        // Wars the AI did choose but that cannot be scored, because the target spent the whole
        // window at war and has no peaceful days left to compare against. Reported apart from
        // "nobody chose a war": the two read the same and mean opposite things, and the second
        // is a fact about the AI while the first is a fact about the measurement.
        var unmeasurable = 0;
        // The honest half of the question. Everything measured against the days after the strike
        // is measured through the war's own effects, which is why that percentile swung from 0.30
        // to 0.91 on the same seeds when the window changed. This compares the target's strength
        // on the day it was struck against its own peaceful days before that - a window that
        // existed no matter what the attacker chose, so it cannot be flattered or condemned by
        // the war it is measuring.
        var baselined = new List<(string Attacker, string Defender, int Day, double Baseline)>();
        foreach (var war in declared)
        {
            // The scenario opens with its own wars already lit. Timing them measures the
            // author's day-one situation, not anybody's judgement, and on a year's run they
            // outnumber the wars the AI actually chose several to one - so they are counted
            // and set aside rather than averaged in.
            if (war.Day <= 1) { authored++; continue; }
            if (!byNation.TryGetValue(war.Defender, out var byDay)) continue;
            if (!byDay.TryGetValue(war.Day, out var at)) continue;
            var attacker = campaign.NationById(war.Attacker);
            var defender = campaign.NationById(war.Defender);
            // Computed before the drop below: the clean window stands on its own, and filtering it
            // by the contaminated one's sufficiency would quietly bias the honest number toward
            // wars whose target happened to have quiet years afterwards.
            var behind = new List<float>();
            for (var day = Math.Max(0, war.Day - Math.Max(1, window)); day < war.Day; day++)
            {
                if (atWar.Contains((war.Defender, day))) continue;
                if (byDay.TryGetValue(day, out var value)) behind.Add(value);
            }
            if (behind.Count >= 8)
            {
                var strongerBefore = behind.Count(value => value < at);
                baselined.Add((attacker?.Tag ?? "?", defender?.Tag ?? "?", war.Day,
                    strongerBefore / (double)(behind.Count - 1)));
            }
            var ahead = new List<float>();
            for (var day = war.Day; day <= Math.Min(last, war.Day + Math.Max(1, window)); day++)
            {
                // Peace days only: a target weakened by the war being measured is not a window
                // that was available before it.
                if (atWar.Contains((war.Defender, day))) continue;
                if (byDay.TryGetValue(day, out var value)) ahead.Add(value);
            }
            // A target that has since dissolved, or that was at war for the whole window, has no
            // peaceful trajectory to compare against. Either way the war is dropped rather than
            // scored on days the attacker itself made unusable.
            if (ahead.Count < 8) { unmeasurable++; continue; }
            var lower = ahead.Count(value => value < at);
            timed.Add((attacker?.Tag ?? "?", defender?.Tag ?? "?", war.Day, at, ahead.Min(),
                lower / (double)(ahead.Count - 1), war.Engaged));
        }

        var chosen = declared.Count(war => war.Day > 1);
        // Printed before the early return below. The clean window stands on its own evidence: if
        // it were reported only when the after-strike window happened to have enough peace days,
        // the honest number would vanish from exactly the runs where the dishonest one was
        // unusable - which is the opposite of a fallback.
        if (baselined.Count > 0)
        {
            var ordered = baselined.Select(entry => entry.Baseline).OrderBy(value => value).ToList();
            Console.WriteLine($"  opportunity (clean): {baselined.Count} of {chosen} chosen war(s) have >= {Math.Max(1, window)} peaceful " +
                              $"days of their target's own history before the strike; on that record the target stood at percentile " +
                              $"{baselined.Average(entry => entry.Baseline):0.00} (median {ordered[ordered.Count / 2]:0.00}) where 0.5 is an " +
                              "ordinary day for it - above 0.5 means the AI waited until the target was stronger than usual, below means " +
                              "it struck one already weaker than its own norm");
            foreach (var entry in baselined.OrderByDescending(entry => entry.Baseline).Take(5))
                Console.WriteLine($"    {entry.Attacker} strikes {entry.Defender} on day {entry.Day}: target at percentile " +
                                  $"{entry.Baseline:0.00} of its own peaceful past {Math.Max(1, window)} days");
        }
        if (timed.Count == 0)
        {
            // Two different nulls, said apart: nobody deciding, and a decision that cannot be
            // scored, read identically in a log and send somebody hunting the wrong thing.
            Console.WriteLine(chosen == 0
                ? $"  opportunity: no war was chosen in {days} day(s) - the {authored} that exist opened with the " +
                  "scenario and are not anybody's decision. There is nothing to time."
                : $"  opportunity: {chosen} war(s) were chosen in {days} day(s) but none can be scored - the target spent " +
                  $"{Math.Max(1, window)} day(s) at war and has no peaceful days to compare against. This measures the " +
                  "instrument, not the AI.");
            return 0;
        }
        Console.WriteLine($"  opportunity: {chosen} war(s) chosen by their own government, {timed.Count} of them scoreable" +
                          (unmeasurable > 0 ? $" ({unmeasurable} dropped for want of peaceful comparison days)" : "") +
                          $", on a world where {authored} more opened with the scenario and are not anybody's decision; " +
                          $"target strength percentile at the strike averages " +
                          $"{timed.Average(war => war.Percentile):0.00} (0 = struck the weakest moment the wait allowed, " +
                          "0.5 = as if at random, 1 = struck the strongest)");
        Console.WriteLine($"  opportunity: {timed.Count(war => war.Engaged)} of them fell on a target already fighting someone else");
        var missed = timed.Select(war => (war.At - war.Low) / MathF.Max(1f, war.At)).OrderBy(share => share).ToList();
        Console.WriteLine($"  opportunity: the median target was {missed[missed.Count / 2] * 100:0}% stronger on the day it was struck " +
                          $"than on its weakest day within {Math.Max(1, window)} days of waiting");
        foreach (var war in timed.OrderByDescending(war => war.Percentile).Take(5))
            Console.WriteLine($"    {war.Attacker} strikes {war.Defender} on day {war.Day}: target {war.At:N0} against a window low of " +
                              $"{war.Low:N0} - percentile {war.Percentile:0.00}" + (war.Engaged ? ", target already at war" : ""));
        return 0;
    }

    /// <summary>
    /// Which of the two ways a war stops being worth it is the one that actually stops it.
    ///
    /// The five-seed funnel showed the AI choosing war one to three times a year across 261
    /// countries, and showed the cause is not the die nor the choice of target but that
    /// <c>WarJudgement</c> hands back zero or less for almost every neighbour. That sum has two
    /// separate ways to fail and they want opposite fixes: nothing is wanted at all, or
    /// something is wanted and the price of taking it is higher. WarJudgement returns exactly
    /// -10 for the first (and for an ally below the betrayal nerve), and an ordinary negative
    /// number for the second, so the two can be counted apart without touching the game.
    /// </summary>
    internal static int WarJudgementReport(Campaign campaign, int days)
    {
        campaign.IsPaused = false;
        var until = campaign.ElapsedDays + Math.Max(1, days);
        while (campaign.ElapsedDays < until) campaign.TickDay();

        var threshold = campaign.Defines.AiWarDesireThreshold;
        var noValue = 0; var pricedOut = 0; var wantsButSmall = 0; var wantsEnough = 0; var noValueAllies = 0;
        // WarValue's first early return is `if (mine <= 0.01f) return 0f` - the attacker has no
        // estimated army at all. That one is reproducible from outside the game, because
        // StrengthOf is public and is exactly what WarValue calls, so the "no hand" case can be
        // separated from the rest of the -10 bucket without a diagnostic hook in AiJudgement.
        // The advantage gate below it cannot be reproduced here: it needs HelpFor, which is
        // private, and reconstructing it by hand would be a second implementation to keep in
        // step rather than a measurement.
        var noValueNoArmy = 0;
        var noGateAdvantage = 0; var noMotiveAtAll = 0;
        var pairs = 0;
        var nations = campaign.Nations.Where(nation => nation.Population > 0 && nation.MapIndex > 0)
            .OrderBy(nation => nation.Id).ToArray();
        var hand = nations.ToDictionary(nation => nation.Id, nation => AiStatecraft.StrengthOf(campaign, nation));
        foreach (var attacker in nations)
            foreach (var target in nations)
            {
                if (target.Id == attacker.Id) continue;
                if (!CampaignDiplomacy.SharesLandBorder(campaign, attacker.MapIndex, target.MapIndex)) continue;
                pairs++;
                var judgement = AiStatecraft.JudgeWarFor(campaign, attacker, target);
                if (judgement == -10f)
                {
                    noValue++;
                    // WarJudgement returns -10 for an ally only after the value check passed,
                    // so allied pairs are the ambiguous case and non-allied -10 is unambiguous.
                    // The sub-counts are kept disjoint on purpose: an ally with no army would
                    // land in two of them, and the verdict below subtracts them from the total.
                    var allied = CampaignDiplomacy.Peek(campaign, attacker.Id, target.Id) is
                        { Stance: DiplomaticStance.Allied };
                    if (allied) { noValueAllies++; }
                    else if (hand[attacker.Id] <= 0.01f) { noValueNoArmy++; }
                    else
                    {
                        // JudgeWarParts already exists for exactly this question, and WarValue
                        // fills its parts list only after both early returns - so an empty list
                        // here means the pair never reached a motive, and a filled one means it
                        // did and the motives summed to nothing.
                        var parts = AiStatecraft.JudgeWarParts(campaign, attacker, target).ValueParts;
                        if (parts.Count == 0) noGateAdvantage++; else noMotiveAtAll++;
                    }
                }
                else if (judgement < 0f) pricedOut++;
                else if (judgement < threshold) wantsButSmall++;
                else wantsEnough++;
            }

        double Share(int count) => pairs == 0 ? 0 : count * 100.0 / pairs;
        // The -10 bucket is two different things: a pair with nothing to fight over, and an
        // ally whose betrayal nerve has not been reached. Only the first is the diagnosis, so
        // split it - and the split needs no new game API, because WarJudgement returns -10 for
        // an ally only after the value check passed, which leaves non-allied -10 unambiguous.
        var alliedAmongNoValue = noValueAllies;
        Console.WriteLine($"  war judgement: {pairs} touching country pairs after {days} day(s), against a desire threshold of {threshold:0.00}");
        Console.WriteLine($"    nothing wanted at all (or an ally below the nerve): {noValue} ({Share(noValue):0.0} %)");
        Console.WriteLine($"      of those, pairs that are allied - the ambiguous case:      {alliedAmongNoValue}");
        Console.WriteLine($"      of those, not allied (so not the ally-below-nerve case):   {noValue - alliedAmongNoValue} " +
                          $"({(pairs == 0 ? 0 : (noValue - alliedAmongNoValue) * 100.0 / pairs):0.0} % of all pairs)");
        // Printed at the level of the whole -10 bucket, not nested under the non-allied line:
        // it is counted over every -10 pair, so showing it as a subset of a number it is not a
        // subset of would be the same conflation this report exists to avoid.
        Console.WriteLine($"    attacker has no estimated army at all (WarValue's first exit): {noValueNoArmy} ({Share(noValueNoArmy):0.0} %)");
        // Split by whether WarValue reached the motives at all: JudgeWarParts fills its list
        // only after both early returns, so an empty list is the advantage gate and a filled one
        // is a country that looked for a reason, weighed every one, and found none.
        Console.WriteLine($"    stopped at the advantage gate (no motive ever weighed): {noGateAdvantage} ({Share(noGateAdvantage):0.0} %)");
        Console.WriteLine($"    weighed every motive and still valued it at nothing:   {noMotiveAtAll} ({Share(noMotiveAtAll):0.0} %)");
        Console.WriteLine($"    wanted, but the price of the war is higher:         {pricedOut} ({Share(pricedOut):0.0} %)");
        Console.WriteLine($"    worth it, under the threshold:                      {wantsButSmall} ({Share(wantsButSmall):0.0} %)");
        Console.WriteLine($"    worth it and over the threshold:                    {wantsEnough} ({Share(wantsEnough):0.0} %)");
        // The verdict says what is known and names what is not. It used to claim "there is no
        // title worth fighting for", which conflated an absent motive with an insufficient
        // advantage and an unestimated army - three different fixes hiding in one bucket.
        Console.WriteLine(pairs == 0
            ? "  war judgement: no touching pairs to measure."
            : "  war judgement: " + (noValue >= pricedOut + wantsButSmall + wantsEnough
                ? $"what binds is WarValue returning zero or less ({noValue} of {pairs} pairs): " +
                  $"{noValueNoArmy} have no army to fight with, {noGateAdvantage} stop at the advantage " +
                  $"gate, {noMotiveAtAll} weigh every motive and find nothing, and {alliedAmongNoValue} " +
                  "are allies below the betrayal nerve. The advantage gate is not a threshold to " +
                  "lower: the estimate of a target carries what its allies would add, so most " +
                  "neighbours genuinely are not weaker than us."
                : pricedOut >= wantsButSmall + wantsEnough ? "what binds is the price of the war, not the desire."
                : "what binds is the desire threshold, not the price and not an absent title."));
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>Processor time the calling thread has been given, in milliseconds; zero where the system will not say.</summary>
    internal static double ThreadCpuMilliseconds()
    {
        try
        {
            if (OperatingSystem.IsWindows() && GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user))
                return (kernel + user) / 10_000.0;
        }
        catch (Exception) { }
        return 0;
    }

    internal static int Profile(Campaign campaign, int days)
    {
        campaign.IsPaused = false;
        for (var day = 0; day < 3; day++) campaign.TickDay();   // warm the JIT and the caches
        SimProfiler.Reset();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var gcBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var pauseBefore = GC.GetTotalPauseDuration();
        // And the processor time behind the wall time. On a machine doing other work the clock
        // on the wall swings by a fifth from one run to the next; the time this thread was
        // actually given swings far less, and is what two builds are compared by.
        var threadCpuBefore = ThreadCpuMilliseconds();
        var processCpuBefore = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        var run = Stopwatch.StartNew();
        // Step by step rather than a day at a time, timing each: what a player feels is not the
        // mean but the worst frame, and a frame at the top speed is twelve steps.
        var steps = new List<double>();
        var step = Stopwatch.StartNew();
        var until = campaign.ElapsedDays + days;
        // And what the heaviest steps were made of: the profiler's totals before and after each,
        // kept for the three worst.
        var before = new Dictionary<string, double>();
        var heaviest = new List<(double Ms, int Day, List<(string Name, double Ms)> Parts)>();
        // And the same totals by the kind of step. "Naval is 8 % of the run" does not say whether
        // that is a little every hour or everything on one day of the week, and the two are cured
        // differently: an hour's step, a step that opens an ordinary day, a step that opens the
        // week's heavy day. Slot 7 is the hours; 0-6 the day-opening steps by day of the week.
        var kindMs = new double[8];
        var kindSteps = new int[8];
        var kindBytes = new long[8];
        var kindParts = new Dictionary<string, double>[8];
        for (var kind = 0; kind < 8; kind++) kindParts[kind] = new Dictionary<string, double>();
        while (campaign.ElapsedDays < until)
        {
            before.Clear();
            foreach (var sample in SimProfiler.All) before[sample.Name] = sample.TotalMilliseconds;
            var day = campaign.ElapsedDays;
            var bytesBefore = GC.GetTotalAllocatedBytes(false);
            step.Restart();
            campaign.Tick();
            var took = step.Elapsed.TotalMilliseconds;
            steps.Add(took);
            {
                var kind = campaign.ElapsedDays != day ? day % 7 : 7;
                kindSteps[kind]++;
                kindMs[kind] += took;
                kindBytes[kind] += GC.GetTotalAllocatedBytes(false) - bytesBefore;
                var into = kindParts[kind];
                foreach (var sample in SimProfiler.All)
                {
                    var spent = sample.TotalMilliseconds - before.GetValueOrDefault(sample.Name);
                    if (spent > 0) into[sample.Name] = into.GetValueOrDefault(sample.Name) + spent;
                }
            }
            if (heaviest.Count < 3 || took > heaviest[^1].Ms)
            {
                var parts = SimProfiler.All
                    .Select(sample => (Name: sample.Name, Ms: sample.TotalMilliseconds - before.GetValueOrDefault(sample.Name)))
                    .Where(part => part.Ms >= 1.0 && !part.Name.StartsWith(" ", StringComparison.Ordinal))
                    .OrderByDescending(part => part.Ms).Take(8).ToList();
                heaviest.Add((took, day, parts));
                heaviest.Sort((left, right) => right.Ms.CompareTo(left.Ms));
                if (heaviest.Count > 3) heaviest.RemoveAt(3);
            }
        }
        foreach (var (ms, day, parts) in heaviest)
            Console.WriteLine($"  heavy step {ms:0.0} ms on day {day} (day of week {day % 7}): " +
                              string.Join(", ", parts.Select(part => $"{part.Name.Trim()} {part.Ms:0.0}")));
        run.Stop();
        {
            var threadCpu = ThreadCpuMilliseconds() - threadCpuBefore;
            var processCpu = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds - processCpuBefore;
            Console.WriteLine($"CPU: the simulation's own thread {threadCpu / Math.Max(1, days):0.0} ms a day, " +
                              $"every thread of the process {processCpu / Math.Max(1, days):0.0} ms a day (wall {run.Elapsed.TotalMilliseconds / Math.Max(1, days):0.0}).");
        }
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        if (steps.Count > 0)
        {
            var sorted = steps.OrderBy(ms => ms).ToList();
            double At(double share) => sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * share))];
            var frame = Math.Max(1, campaign.Defines.MaxDaysPerFrame);
            var worstFrame = 0.0;
            for (var start = 0; start + frame <= steps.Count; start++)
                worstFrame = Math.Max(worstFrame, steps.Skip(start).Take(frame).Sum());
            Console.WriteLine($"Steps: {steps.Count:N0} of {campaign.TickHours}h; median {At(0.5):0.00} ms, p95 {At(0.95):0.00}, " +
                              $"p99 {At(0.99):0.00}, worst {sorted[^1]:0.0}; worst {frame} steps together (a frame at the top speed) {worstFrame:0.0} ms.");
        }
        // By the kind of step: where a day's cost sits.
        {
            var heavyDay = 0;
            for (var weekday = 1; weekday < 7; weekday++)
                if (kindSteps[weekday] > 0 && kindMs[weekday] / kindSteps[weekday] >
                    (kindSteps[heavyDay] > 0 ? kindMs[heavyDay] / kindSteps[heavyDay] : 0)) heavyDay = weekday;
            void Kind(string label, Func<int, bool> which)
            {
                double ms = 0; var count = 0; long bytes = 0;
                var parts = new Dictionary<string, double>();
                for (var kind = 0; kind < 8; kind++)
                {
                    if (!which(kind)) continue;
                    ms += kindMs[kind]; count += kindSteps[kind]; bytes += kindBytes[kind];
                    foreach (var (name, spent) in kindParts[kind]) parts[name] = parts.GetValueOrDefault(name) + spent;
                }
                if (count == 0) return;
                Console.WriteLine($"STEPKIND {label}: {count:N0} step(s), {ms:N0} ms in all ({ms / Math.Max(1, days):0.0} ms a day of the run), " +
                                  $"{ms / count:0.00} ms a step, {bytes / 1024.0 / count:N0} kB a step.");
                foreach (var (name, spent) in parts.Where(part => !part.Key.StartsWith(" ", StringComparison.Ordinal))
                             .OrderByDescending(part => part.Value).Take(45))
                    Console.WriteLine($"    {name,-26} {spent,9:N0} ms  {spent / count,9:0.000} ms a step  {spent / ms,6:P1}");
                // And the scopes inside them, which is where a pass says what it was doing.
                foreach (var (name, spent) in parts.Where(part => part.Key.StartsWith(" ", StringComparison.Ordinal) && part.Value / count >= 0.5)
                             .OrderByDescending(part => part.Value).Take(70))
                    Console.WriteLine($"    inside: {name.Trim(),-26} {spent,9:N0} ms  {spent / count,9:0.000} ms a step");
            }
            Kind("an hour inside a day", kind => kind == 7);
            Kind("opening an ordinary day", kind => kind < 7 && kind != heavyDay);
            Kind($"opening the week's heavy day (day of week {heavyDay})", kind => kind == heavyDay);
        }
        // Report this phase BEFORE the two additional 30-day probes. Mixing their
        // profiler totals into a denominator of only `days` overstated every share.
        Console.WriteLine($"Profile: {days} day(s) in {run.ElapsedMilliseconds:N0} ms, " +
                          $"{run.Elapsed.TotalMilliseconds / days:0.00} ms a day, " +
                          $"{allocated / days / 1024.0:N0} kB a day; {campaign.Wars.Count(war => !war.Ended)} war(s) open, " +
                          $"{campaign.Armies.Count:N0} division(s).");
        Console.WriteLine($"GC phase: gen0={GC.CollectionCount(0) - gcBefore.Item1} gen1={GC.CollectionCount(1) - gcBefore.Item2} " +
                          $"gen2={GC.CollectionCount(2) - gcBefore.Item3} pause_ms={(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:0.000} " +
                          $"managed_bytes={GC.GetTotalMemory(false)} resident_bytes={Environment.WorkingSet}; no forced collection inside measurement.");
        var total = Math.Max(0.001, run.Elapsed.TotalMilliseconds);
        foreach (var sample in SimProfiler.All.OrderByDescending(entry => entry.TotalMilliseconds))
            Console.WriteLine($"  {sample.Name,-20} {sample.TotalMilliseconds,10:N0} ms  " +
                              $"{sample.TotalMilliseconds / total,6:P1}  " +
                              $"{sample.MeanMilliseconds,8:0.000} ms/run  ×{sample.Runs:N0}  " +
                              $"{sample.AllocatedBytes / 1_048_576.0,8:0.0} MB");
        // Headless simulation slices, NOT rendered frame times: rendering, UI, GPU
        // waits and OS scheduling require the native in-game frame capture as well.
        // And what a frame feels with smooth time on, which is how the game runs: the simulation
        // given its budget a frame (Campaign.StepFor) for thirty more days. A frame's cost is the
        // budget plus the one stage it could not cut short - the number that is a hitch.
        {
            var budgetMs = Math.Max(1.0, campaign.Defines.FrameSimulationBudgetMs);
            var frames = new List<double>();
            var frameClock = Stopwatch.StartNew();
            var stopAt = campaign.ElapsedDays + 30;
            while (campaign.ElapsedDays < stopAt)
            {
                frameClock.Restart();
                campaign.StepFor(budgetMs);
                frames.Add(frameClock.Elapsed.TotalMilliseconds);
            }
            var sortedFrames = frames.OrderBy(ms => ms).ToList();
            double FrameAt(double share) => sortedFrames[Math.Min(sortedFrames.Count - 1, (int)(sortedFrames.Count * share))];
            Console.WriteLine($"Frames with smooth time ({budgetMs:0} ms budget, 30 days): {frames.Count:N0}; median {FrameAt(0.5):0.00} ms, " +
                              $"p99 {FrameAt(0.99):0.00}, p99.9 {FrameAt(0.999):0.00}, worst {sortedFrames[^1]:0.0}; " +
                              $"{frames.Count(ms => ms > 33.3):N0} over 33 ms.");
            // The indivisible pieces themselves: one stage a call, named by what the profiler
            // counted during it - the list an optimisation of hitches works down.
            //
            // A stage can also be long with nothing named inside it, and that used to read as
            // "some unnamed code". Usually it is not code at all: the runtime stopped the
            // process to collect. The pause and the collection counts are captured per stage so
            // an unexplained 46 ms says whether it was the collector or a pass nobody measured -
            // without that, the next hour goes into hunting code that was never running.
            var pieces = new List<(double Ms, int Day, string Parts)>();
            var mark = new Dictionary<string, double>();
            var pieceStop = campaign.ElapsedDays + 30;
            // A stage with nothing named inside it can be found by its neighbours: the profiler
            // already fires a hook as each measured scope closes, so the three passes that
            // finished just before the long stage say where in the day's order it sits. Without
            // that, "nothing named" is an invitation to guess, and guessing is what this whole
            // loop exists to avoid.
            var recent = new List<string>(4);
            static void Remember(List<string> into, string name)
            {
                var trimmed = name.Trim();
                if (trimmed.Length == 0) return;
                if (into.Count > 0 && into[^1] == trimmed) return;
                into.Add(trimmed);
                if (into.Count > 3) into.RemoveAt(0);
            }
            SimProfiler.StageProbe = name => Remember(recent, name);
            try
            {
                while (campaign.ElapsedDays < pieceStop)
                {
                    mark.Clear();
                    recent.Clear();
                    foreach (var sample in SimProfiler.All) mark[sample.Name] = sample.TotalMilliseconds;
                    var stagePauseBefore = GC.GetTotalPauseDuration();
                    var stageGen0 = GC.CollectionCount(0);
                    var stageGen1 = GC.CollectionCount(1);
                    var stageGen2 = GC.CollectionCount(2);
                    frameClock.Restart();
                    campaign.StepFor(0.0001);
                    var ms = frameClock.Elapsed.TotalMilliseconds;
                    if (ms < 15) continue;
                    var paused = GC.GetTotalPauseDuration() - stagePauseBefore;
                    var collections = (GC.CollectionCount(0) - stageGen0) + (GC.CollectionCount(1) - stageGen1) +
                                      (GC.CollectionCount(2) - stageGen2);
                    var parts = string.Join(", ", SimProfiler.All
                        .Select(sample => (Name: sample.Name.Trim(), Ms: sample.TotalMilliseconds - mark.GetValueOrDefault(sample.Name)))
                        .Where(part => part.Ms >= 2.0).OrderByDescending(part => part.Ms).Take(5).Select(part => $"{part.Name} {part.Ms:0}"));
                    if (collections > 0 || paused.TotalMilliseconds >= 1)
                        parts = (parts.Length > 0 ? parts + ", " : "") +
                                $"gc {paused.TotalMilliseconds:0} ms in {collections} collection(s)";
                    // What no scope claimed. A stage is one MoveNext inside StepFor, so this is
                    // the iterator scaffolding itself - resuming, running finally blocks, the
                    // frame loop's own bookkeeping - and it is the only explanation left when
                    // nothing named is slow and the collector did not run.
                    var covered = 0.0;
                    foreach (var sample in SimProfiler.All) covered += sample.TotalMilliseconds - mark.GetValueOrDefault(sample.Name);
                    var unattributed = ms - covered;
                    if (parts.Length == 0)
                        parts = recent.Count > 0
                            ? "nothing named after " + string.Join(" > ", recent)
                            : "nothing named, and the day had not started";
                    if (unattributed >= 5) parts += $", unattributed {unattributed:0} ms";
                    pieces.Add((ms, campaign.ElapsedDays, parts));
                }
            }
            finally
            {
                SimProfiler.StageProbe = null;
            }
            foreach (var (ms, day, parts) in pieces.OrderByDescending(piece => piece.Ms).Take(10))
                Console.WriteLine($"  indivisible {ms:0.0} ms on day {day}: {parts}");
        }
        // The world it ended in, as one number: an optimisation that is meant to change how
        // fast the world runs and nothing else has to end in the same one.
        Console.WriteLine($"  world fingerprint after {campaign.ElapsedDays} day(s): {CampaignFingerprint.Of(campaign):X16}");
        return 0;
    }

    internal static int FrontsReport(Campaign campaign, int days)
    {
        campaign.IsPaused = false;
        var samples = new List<(int Day, int Posts, int Holes, int Piled, int Held)>();
        var bare = new List<(int Day, int Pairs, int Bare)>();
        var idle = 0;
        for (var day = 1; day <= days; day++)
        {
            campaign.TickDay();
            if (day % 30 != 0 && day != days) continue;
            var (posts, holes, piled, held) = FrontCensus(campaign, day == days);
            samples.Add((day, posts, holes, piled, held));
            var (pairs, empty) = SecondFrontCensus(campaign, day == days);
            bare.Add((day, pairs, empty));
            idle += campaign.Armies.Count(army => army.OrderKind == ArmyOrder.Manual && army.AssignedWarId < 0 &&
                !army.DirectOrder && !army.InBattle && army.EmbarkedFleetId < 0 && army.TemplateId != MilitiaCore.TemplateId &&
                campaign.NationById(army.NationId) is { IsHuman: false } &&
                campaign.Wars.Any(war => !war.Ended && war.Involves(army.NationId)));
        }
        foreach (var (day, posts, holes, piled, held) in samples)
            Console.WriteLine($"  day {day,4}: {posts,5:N0} front posts, {holes,4:N0} held by nobody ({holes / (float)Math.Max(1, posts):P0}), " +
                              $"{piled,4:N0} holding three or more above their share; {held:N0} divisions holding them");
        Console.WriteLine($"  countries in more than one war: {bare.Sum(sample => sample.Pairs):N0} country-war pair(s) sampled, " +
                          $"{bare.Sum(sample => sample.Bare):N0} with a front of their own and nobody of theirs on it.");
        Console.WriteLine($"  divisions of countries at war serving no war, summed over {bare.Count} samples: {idle:N0}.");
        Console.WriteLine($"  war emergency: reinforced {WarEmergency.Reinforced:N0}, rushed {WarEmergency.Rushed:N0}, raised {WarEmergency.Raised:N0}.");
        Console.WriteLine($"  the staff re-posted {FrontManager.Reposts:N0} division(s) in {days} day(s).");
        Console.WriteLine($"  at peace, {ThreatPosture.Moves:N0} division(s) were moved to the borders their countries fear.");
        Console.WriteLine($"  the disposition moved {ArmyDisposition.Relieved:N0} starving formation(s) to ground that could feed them " +
                          $"and garrisoned {ArmyDisposition.Garrisoned:N0} region(s) from idle heaps.");
        return 0;
    }

    /// <summary>
    /// Countries fighting more than one war, and how many of those wars have a front of the
    /// country's own with none of its army assigned to it - the second front an army that
    /// served only the first war in the list left to whoever walked in.
    /// </summary>
    internal static (int Pairs, int Bare) SecondFrontCensus(Campaign campaign, bool print)
    {
        var controllers = campaign.ProvinceController;
        var pairs = 0;
        var bare = 0;
        foreach (var nation in campaign.Nations)
        {
            var wars = campaign.Wars.Where(war => !war.Ended && war.Involves(nation.Id)).ToList();
            if (wars.Count < 2) continue;
            var army = campaign.Armies.Where(unit => unit.NationId == nation.Id && unit.TemplateId != MilitiaCore.TemplateId).ToList();
            if (army.Count < 6) continue;
            var line = new List<string>();
            foreach (var war in wars)
            {
                var posts = war.FriendlyFront(nation.Id).Count(province => controllers[province] == (ushort)nation.MapIndex);
                if (posts == 0) continue;
                var holders = army.Count(unit => unit.AssignedWarId == war.Id);
                pairs++;
                if (holders == 0) bare++;
                line.Add($"{war.Name}: {posts} posts, {holders} divisions");
            }
            if (print && line.Count > 0)
            {
                var elsewhere = army.Count(unit => wars.All(war => war.Id != unit.AssignedWarId));
                var kinds = string.Join(" ", army.GroupBy(unit => unit.OrderKind).Select(group => $"{group.Key}x{group.Count()}"));
                Console.WriteLine($"  {nation.Tag} ({army.Count} divisions, {elsewhere} serving none of its {wars.Count} wars; {kinds}) - " +
                                  string.Join("; ", line));
            }
        }
        return (pairs, bare);
    }

    /// <summary>Every country at war with a field army, on its own sector of its side's front.</summary>
    internal static (int Posts, int Holes, int Piled, int Held) FrontCensus(Campaign campaign, bool print)
    {
        var controllers = campaign.ProvinceController;
        var totalPosts = 0;
        var totalHoles = 0;
        var totalPiled = 0;
        var totalHeld = 0;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            foreach (var attacking in new[] { true, false })
            foreach (var nationId in war.Side(attacking))
            {
                var nation = campaign.NationById(nationId);
                if (nation is null) continue;
                var posts = new HashSet<int>();
                foreach (var province in war.FriendlyFront(nationId))
                    if (controllers[province] == (ushort)nation.MapIndex) posts.Add(province);
                var holders = campaign.Armies.Where(army => army.NationId == nationId && army.AssignedWarId == war.Id &&
                    army.TemplateId != MilitiaCore.TemplateId && army.EmbarkedFleetId < 0 && army.ProvinceId > 0).ToList();
                if (posts.Count == 0 || holders.Count < 6) continue;
                var byPost = new Dictionary<int, int>();
                foreach (var army in holders)
                {
                    var post = posts.Contains(army.OrderProvinceId) ? army.OrderProvinceId
                        : posts.Contains(army.ProvinceId) ? army.ProvinceId : -1;
                    if (post > 0) byPost[post] = byPost.GetValueOrDefault(post) + 1;
                }
                var holes = posts.Count(post => !byPost.ContainsKey(post));
                var share = (int)Math.Ceiling(holders.Count / (double)posts.Count);
                var piled = byPost.Count(pair => pair.Value >= share + 3);
                totalPosts += posts.Count;
                totalHoles += Math.Min(holes, Math.Max(0, posts.Count - holders.Count) > 0 ? holes - Math.Max(0, posts.Count - holders.Count) : holes);
                totalPiled += piled;
                totalHeld += holders.Count;
                if (print)
                {
                    var largest = byPost.Count == 0 ? -1 : byPost.OrderByDescending(pair => pair.Value).First().Key;
                    var kinds = string.Join(" ", holders.Where(army => army.ProvinceId == largest || army.OrderProvinceId == largest)
                        .GroupBy(army => army.OrderKind).Select(group => $"{group.Key}x{group.Count()}"));
                    Console.WriteLine($"  {nation.Tag} in {war.Name}: {posts.Count} posts, {holders.Count} divisions, {holes} unheld, " +
                                      $"largest post {(byPost.Count == 0 ? 0 : byPost.Values.Max())} ({kinds}), {piled} piled");
                }
            }
        }
        return (totalPosts, totalHoles, totalPiled, totalHeld);
    }

    /// <summary>
    /// The voyages the sea lanes are judged by: each one only makes sense if a ship goes
    /// round something - a continent, a peninsula, a strait somebody holds.
    /// </summary>
    internal static readonly (string Tag, string To, float Latitude, float Longitude)[] Voyages =
    {
        ("RUS", "Baltiysk", 54.65f, 19.90f),
        ("USA", "San Diego", 32.70f, -117.25f),
        ("TUR", "Sevastopol", 44.60f, 33.45f),
        ("TUR", "Toulon", 43.05f, 5.95f),
        ("IND", "Toulon", 43.05f, 5.95f),
        ("GBR", "Saint Petersburg", 59.95f, 29.60f),
        ("CHN", "Singapore", 1.20f, 103.85f),
        ("JPN", "Persian Gulf", 26.60f, 51.00f),
        ("AUS", "Cape Town", -34.00f, 18.30f),
        ("BRA", "New York", 40.45f, -73.90f)
    };

    internal static int SeaReport(Campaign campaign, string path)
    {
        var map = campaign.ProvinceMap;
        if (map is null) { Console.WriteLine("no province map; no sea to draw"); return 1; }
        SeaLanes.Invalidate();
        var built = Stopwatch.StartNew();
        SeaLanes.IsWater(map, 0);
        built.Stop();
        Console.WriteLine($"sea lanes: built in {built.ElapsedMilliseconds} ms, {SeaLanes.BasinCount:N0} bodies of water, " +
                          $"{SeaLanes.CoastalProvinces(map).Count:N0} provinces with a harbour.");
        foreach (var passage in SeaPassageCatalog.All)
        {
            var holder = NavalOperations.HolderOf(campaign, SeaLanes.HolderOf(map, SeaPassageCatalog.All.ToList().IndexOf(passage)));
            Console.WriteLine($"  passage {passage.Id,-22} held by {holder?.Tag ?? "-",-4} " +
                              $"{(passage.ClosesToEnemies ? "shut to its holder's enemies" : "open to all")}");
        }

        Console.WriteLine("fleets:");
        foreach (var fleet in campaign.Fleets)
        {
            var nation = campaign.NationById(fleet.NationId);
            var home = fleet.HomeProvinceId;
            var harbour = SeaLanes.HarbourOf(map, home);
            var holder = NavalOperations.HolderOf(campaign, home);
            Console.WriteLine($"  {nation?.Tag,-4} {fleet.Name,-28} {fleet.Hulls,4} hulls  home {home,6} " +
                              $"({(harbour >= 0 ? "harbour" : "NO HARBOUR")}, held by {holder?.Tag ?? "-"}) " +
                              $"{(fleet.InPort ? "in port" : "at sea")}  basin {SeaLanes.BasinAt(map, fleet.Latitude, fleet.Longitude)}");
        }

        var routes = new List<List<(float, float)>>();
        Console.WriteLine("voyages:");
        foreach (var (tag, to, latitude, longitude) in Voyages)
        {
            var nation = campaign.NationByTag(tag);
            var fleet = nation is null ? null : campaign.Fleets.Find(candidate => candidate.NationId == nation.Id);
            if (fleet is null) { Console.WriteLine($"  {tag}: no fleet"); continue; }
            var course = new List<float>();
            var clock = Stopwatch.StartNew();
            var found = SeaLanes.Plan(campaign, fleet.NationId, fleet.Latitude, fleet.Longitude, latitude, longitude,
                course, out var length);
            clock.Stop();
            var straight = NavalOperations.Distance(fleet.Latitude, fleet.Longitude, latitude, longitude);
            Console.WriteLine(found
                ? $"  {tag} {fleet.Name} to {to}: {length:0}° of sea ({straight:0}° as the crow flies), " +
                  $"{course.Count / 2} leg(s), about {length / campaign.Defines.FleetDegreesPerDay:0} days, plotted in {clock.Elapsed.TotalMilliseconds:0.0} ms"
                : $"  {tag} {fleet.Name} to {to}: NO COURSE ({clock.Elapsed.TotalMilliseconds:0.0} ms)");
            if (!found) continue;
            var line = new List<(float, float)> { (fleet.Latitude, fleet.Longitude) };
            for (var index = 0; index + 1 < course.Count; index += 2) line.Add((course[index], course[index + 1]));
            routes.Add(line);
        }

        // A strait somebody holds is shut to the holder's enemies: at war with Turkey, Italy's
        // fleet has no way into the Black Sea; at war with Egypt, none through Suez.
        var italy = campaign.NationByTag("ITA");
        var italian = italy is null ? null : campaign.Fleets.Find(candidate => candidate.NationId == italy.Id);
        foreach (var (holderTag, to, latitude, longitude) in new[]
                 { ("TUR", "Sevastopol", 44.60f, 33.45f), ("EGY", "the Red Sea", 20.0f, 38.5f) })
        {
            var holder = campaign.NationByTag(holderTag);
            if (italian is null || holder is null) continue;
            var war = new StrategicWar
            {
                Id = 91_000, Name = "Bench strait war", AttackerId = italy.Id, DefenderId = holder.Id,
                StartedDay = campaign.ElapsedDays, AiNegotiationUnlockDay = int.MaxValue
            };
            campaign.Wars.Add(war);
            var course = new List<float>();
            var found = SeaLanes.Plan(campaign, italy.Id, italian.Latitude, italian.Longitude, latitude, longitude,
                course, out var length);
            campaign.Wars.Remove(war);
            var open = SeaLanes.Plan(campaign, italy.Id, italian.Latitude, italian.Longitude, latitude, longitude,
                course, out var peaceful);
            Console.WriteLine($"  ITA at war with {holderTag}, to {to}: " +
                              (found ? $"{length:0}° of sea" : "no course") +
                              $"; at peace: {(open ? $"{peaceful:0}°" : "no course")}");
        }

        const int scale = 4;
        var width = SeaLanes.Columns * scale;
        var height = SeaLanes.Rows * scale;
        var pixels = new byte[width * height * 3];
        void Paint(int x, int y, byte r, byte g, byte b)
        {
            x = ((x % width) + width) % width;
            if (y < 0 || y >= height) return;
            var at = (y * width + x) * 3;
            pixels[at] = r; pixels[at + 1] = g; pixels[at + 2] = b;
        }
        // The raster itself for land and sea, then the sea's pieces on top: each one a dot,
        // joined to the pieces it touches, in the colour of its body of water.
        var raster = map.CellToProvince;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceX = x * map.Width / width;
            var sourceY = y * map.Height / height;
            var sea = raster[sourceY * map.Width + sourceX] == 0;
            Paint(x, y, sea ? (byte)30 : (byte)96, sea ? (byte)52 : (byte)92, sea ? (byte)88 : (byte)84);
        }
        var nodeCount = SeaLanes.NodeCount(map);
        var basinSize = new Dictionary<int, int>();
        for (var node = 0; node < nodeCount; node++)
        {
            var basin = SeaLanes.Node(map, node).Basin;
            basinSize[basin] = basinSize.GetValueOrDefault(basin) + 1;
        }
        var ocean = basinSize.OrderByDescending(pair => pair.Value).First().Key;
        Console.WriteLine($"  {nodeCount:N0} pieces of sea, {basinSize.Count:N0} bodies of water, the ocean {basinSize[ocean]:N0} pieces");
        for (var node = 0; node < nodeCount; node++)
        {
            var (latitude, longitude, basin, passage) = SeaLanes.Node(map, node);
            var from = ((int)((longitude + 180f) * scale), (int)((90f - latitude) * scale));
            foreach (var next in SeaLanes.Links(map, node))
            {
                if (next < node) continue;
                var there = SeaLanes.Node(map, next);
                var to = ((int)((there.Longitude + 180f) * scale), (int)((90f - there.Latitude) * scale));
                if (Math.Abs(to.Item1 - from.Item1) > width / 2) continue;
                var steps = Math.Max(Math.Abs(to.Item1 - from.Item1), Math.Abs(to.Item2 - from.Item2)) + 1;
                for (var step = 0; step <= steps; step++)
                    Paint(from.Item1 + (to.Item1 - from.Item1) * step / steps,
                        from.Item2 + (to.Item2 - from.Item2) * step / steps, 52, 88, 138);
            }
            if (passage) Paint(from.Item1, from.Item2, 230, 60, 50);
            else if (basin == ocean) Paint(from.Item1, from.Item2, 120, 170, 220);
            else Paint(from.Item1, from.Item2, 240, 150, 60);
        }
        foreach (var province in SeaLanes.CoastalProvinces(map))
        {
            if (!SeaLanes.TryHarbourPoint(map, province, out var latitude, out var longitude)) continue;
            Paint((int)((longitude + 180f) * scale), (int)((90f - latitude) * scale), 150, 210, 150);
        }
        (int X, int Y) At(float latitude, float longitude) =>
            ((int)((longitude + 180f) * scale), (int)((90f - latitude) * scale));
        foreach (var passage in SeaPassageCatalog.All)
        foreach (var (latitude, longitude) in passage.Points)
        {
            var (x, y) = At(latitude, longitude);
            for (var d = -1; d <= 1; d++) { Paint(x + d, y, 230, 60, 50); Paint(x, y + d, 230, 60, 50); }
        }
        foreach (var line in routes)
        {
            for (var index = 1; index < line.Count; index++)
            {
                var (fromLatitude, fromLongitude) = line[index - 1];
                var (toLatitude, toLongitude) = line[index];
                var deltaLongitude = toLongitude - fromLongitude;
                while (deltaLongitude > 180f) deltaLongitude -= 360f;
                while (deltaLongitude < -180f) deltaLongitude += 360f;
                var samples = (int)(MathF.Max(MathF.Abs(toLatitude - fromLatitude), MathF.Abs(deltaLongitude)) * scale * 2) + 1;
                for (var sample = 0; sample <= samples; sample++)
                {
                    var t = sample / (float)samples;
                    var (x, y) = At(fromLatitude + (toLatitude - fromLatitude) * t, fromLongitude + deltaLongitude * t);
                    Paint(x, y, 250, 245, 225);
                }
            }
        }
        foreach (var fleet in campaign.Fleets)
        {
            var (x, y) = At(fleet.Latitude, fleet.Longitude);
            for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                Paint(x + dx, y + dy, 250, 205, 60);
        }
        WritePng(path, width, height, pixels);
        Console.WriteLine($"sea lanes drawn to {Path.GetFullPath(path)}");
        return 0;
    }

    /// <summary>A PNG with nothing but what a viewer needs: one chunk of RGB, filtered by nothing.</summary>
    internal static void WritePng(string path, int width, int height, byte[] rgb)
    {
        using var stream = File.Create(path);
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);
            var typed = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
            data.CopyTo(typed, 4);
            stream.Write(typed);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
            stream.Write(crc);
        }
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        Chunk("IHDR", header);
        using var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(rgb, y * width * 3, width * 3);
            }
        }
        Chunk("IDAT", packed.ToArray());
        Chunk("IEND", Array.Empty<byte>());
    }

    internal static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    internal static int Peoples(Campaign campaign, int days)
    {
        static Dictionary<string, long> Mix(ScenarioState state)
        {
            var mix = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in state.PopulationGroups)
                if (group.Size > 0) mix[group.Culture ?? ""] = mix.GetValueOrDefault(group.Culture ?? "") + group.Size;
            return mix;
        }
        (double Minority, int Mixed) World()
        {
            long all = 0, minority = 0;
            var mixed = 0;
            foreach (var state in campaign.States)
            {
                if (state.Unclaimed || state.Population <= 0) continue;
                var mix = Mix(state);
                if (mix.Count == 0) continue;
                var total = mix.Values.Sum();
                var top = mix.Values.Max();
                all += total;
                minority += total - top;
                if (top < total * 0.8) mixed++;
            }
            return (minority / (double)Math.Max(1, all), mixed);
        }
        string Describe(ScenarioState state)
        {
            var mix = Mix(state);
            var total = Math.Max(1, mix.Values.Sum());
            return string.Join(", ", mix.OrderByDescending(p => p.Value).Take(3)
                .Select(p => $"{p.Key} {p.Value / (double)total:P0}"));
        }
        var watched = campaign.States.Where(state => !state.Unclaimed && state.Population >= 300_000)
            .Select(state => (state, mix: Mix(state)))
            .Where(item => item.mix.Count > 1)
            .OrderBy(item => item.mix.Values.Max() / (double)Math.Max(1, item.mix.Values.Sum()))
            .Take(8).Select(item => item.state).ToList();
        // And the regions a reader asks about by id (DOF_PEOPLES_WATCH=cn_xj,ro_hr,...).
        foreach (var id in (Environment.GetEnvironmentVariable("DOF_PEOPLES_WATCH") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (campaign.States.FirstOrDefault(state => string.Equals(state.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) is { } asked &&
                !watched.Contains(asked)) watched.Add(asked);
        var (startMinority, startMixed) = World();
        Console.WriteLine($"  day 0: {startMinority:P1} of the world's people are not of their region's majority; {startMixed:N0} regions are mixed (largest people under 80 %).");
        foreach (var state in watched) Console.WriteLine($"    {state.Id,-10} {Describe(state)}");
        campaign.IsPaused = false;
        long moved = 0;
        for (var day = 1; day <= days; day++)
        {
            campaign.TickDay();
            moved += InternalMigration.MovedToday;
            if (day % 365 != 0 && day != days) continue;
            var (minority, mixedNow) = World();
            Console.WriteLine($"  day {day}: {minority:P1} minority, {mixedNow:N0} mixed regions, {Fmt.People(moved)} moved inside their own country so far.");
        }
        foreach (var state in watched) Console.WriteLine($"    {state.Id,-10} {Describe(state)}");
        var (endMinority, _) = World();
        return Check("minorities are not dissolved by the years alone",
            endMinority >= startMinority * Math.Pow(0.97, days / 365.0)
                ? ""
                : $"the world's minority share fell from {startMinority:P1} to {endMinority:P1} in {days} day(s)");
    }

    internal static int Front(Campaign campaign, int days)
    {
        var before = new FrontSnapshot(campaign);
        campaign.IsPaused = false;
        var trace = new List<int>();
        var ukraineIndex = campaign.NationByTag("UKR")?.MapIndex ?? -1;
        var traceFront = Environment.GetEnvironmentVariable("DOF_TRACE_FRONT") == "1";
        var ukraineId = campaign.NationByTag("UKR")?.Id ?? -1;
        long seenEntry = 0;
        var lastHeld = ukraineIndex >= 0 ? HeldBy(campaign, ukraineIndex) : 0;
        for (var day = 0; day < days; day++)
        {
            campaign.TickDay();
            if (traceFront && ukraineIndex >= 0)
            {
                // What happened to Ukraine today, and any day its holding jumps: a country that
                // lost everything and came back with more than it started with is a peace or a
                // restoration somebody has to be able to name.
                var entries = campaign.Chronicle.Entries;
                for (var i = entries.Count - 1; i >= 0 && entries[i].Sequence > seenEntry; i--)
                {
                    var e = entries[i];
                    if (e.ActorId == ukraineId || e.TargetId == ukraineId)
                        Console.WriteLine($"  UKR day {e.Day}: {e.Kind} {e.ActorName} -> {e.TargetName} {e.Subject} {e.Place}");
                }
                if (entries.Count > 0) seenEntry = Math.Max(seenEntry, entries[^1].Sequence);
                var held = HeldBy(campaign, ukraineIndex);
                if (Math.Abs(held - lastHeld) >= 20)
                    Console.WriteLine($"  UKR day {day + 1}: holding {lastHeld} -> {held}, wars open: " +
                        string.Join("; ", campaign.Wars.Where(w => !w.Ended && w.Involves(ukraineId)).Select(w => w.Name)));
                lastHeld = held;
            }
            if (day % 15 == 0 && ukraineIndex >= 0 && campaign.ProvinceController is not null)
                trace.Add(HeldBy(campaign, ukraineIndex));
            if (traceFront && day % 30 == 0) TraceFront(campaign, day);
            if (day % 60 == 59) WarsOf(campaign, "UKR", day + 1);
        }
        foreach (var tag in new[] { "RUS", "UKR" })
        {
            var nation = campaign.NationByTag(tag);
            var idleNow = campaign.Armies.Count(army => army.NationId == nation.Id && army.OrderKind == ArmyOrder.Manual &&
                                                        army.AssignedWarId < 0 && army.TemplateId != MilitiaCore.TemplateId);
            Console.WriteLine($"  {tag}: {FrontManager.CalledUpBy.GetValueOrDefault(nation.Id):N0} division(s) called up from idleness, " +
                              $"{idleNow} idle now, {campaign.Armies.Count(army => army.NationId == nation.Id):N0} divisions in all");
        }
        var failures = Check("the Russo-Ukrainian front is still a stalemate",
            FrontIsAStalemate(campaign, before, trace, days));
        failures += Check("a war costs formations and an army replaces them", WarCostsFormations(campaign, days));
        failures += Check("the peace an AI writes hangs together", ThePeaceAnAiWritesHangsTogether(campaign));
        return failures;
    }

    /// <summary>Which wars a country is fighting and how its army is split between them.</summary>
    internal static void WarsOf(Campaign campaign, string tag, int day)
    {
        var nation = campaign.NationByTag(tag);
        if (nation is null) return;
        var wars = campaign.Wars.Where(war => !war.Ended && war.Involves(nation.Id)).ToList();
        if (wars.Count < 2) return;
        var army = campaign.Armies.Where(unit => unit.NationId == nation.Id && unit.TemplateId != MilitiaCore.TemplateId).ToList();
        Console.WriteLine($"  day {day}: {tag} fights {wars.Count} wars with {army.Count} divisions - " + string.Join("; ", wars.Select(war =>
            $"{war.Name} ({war.FriendlyFront(nation.Id).Count(province => campaign.ProvinceController[province] == (ushort)nation.MapIndex)} own posts, " +
            $"{army.Count(unit => unit.AssignedWarId == war.Id)} divisions)")));
    }

    internal static int Allocations(Campaign campaign, int days)
    {
        campaign.IsPaused = false;
        for (var day = 0; day < days; day++) campaign.TickDay();
        Console.WriteLine($"Allocations after {days} day(s), {campaign.Armies.Count:N0} division(s), " +
                          $"{campaign.Wars.Count(war => !war.Ended)} war(s) open:");
        // Each pass on its own, the order Campaign.Tick runs them in. Run alone a pass still
        // does its real work on the real world - a march advances, a reserve refills - so
        // the garbage is the garbage that work makes; the world is not used for anything after.
        var passes = new (string Name, Action Run)[]
        {
            ("commands", () => campaign.Commands.ApplyAll(campaign)),
            ("logistics", () => LogisticsSystem.Tick(campaign)),
            ("military", () => MilitaryOperations.Tick(campaign)),
            ("naval", () => { AiAdmiralty.Tick(campaign); NavalOperations.Tick(campaign); }),
            ("warfare", () => Warfare.Tick(campaign)),
            ("attrition", () => Attrition.Tick(campaign)),
            ("construction", () => ConstructionSystem.Tick(campaign)),
            ("whole step", campaign.Tick),
        };
        const int runs = 240;
        using var listener = new AllocationListener();
        foreach (var (name, run) in passes)
        {
            run();
            listener.Reset();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < runs; index++) run();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            // The runtime hands its events to a listener on a thread of its own.
            System.Threading.Thread.Sleep(300);
            Console.WriteLine($"  {name,-14} {bytes / (double)runs / 1024.0,9:N1} kB a run");
            foreach (var (type, sampled) in listener.Largest(8))
                Console.WriteLine($"      {sampled / (double)runs / 1024.0,9:N1} kB  {type}");
        }
        return 0;
    }

    /// <summary>
    /// The runtime's allocation sample, by type. The runtime reports roughly every hundred
    /// kilobytes allocated and names the type of the object that crossed the line, so a
    /// type's share of the samples is its share of the garbage - an estimate, and a good one
    /// over a few hundred runs.
    /// </summary>
    internal sealed class AllocationListener : System.Diagnostics.Tracing.EventListener
    {
        private readonly Dictionary<string, long> _sampled = new();

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose,
                    (System.Diagnostics.Tracing.EventKeywords)0x1);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs data)
        {
            if (data.EventName is null || !data.EventName.Contains("AllocationTick") || data.PayloadNames is null) return;
            var typeAt = data.PayloadNames.IndexOf("TypeName");
            var amountAt = data.PayloadNames.IndexOf("AllocationAmount64");
            if (typeAt < 0 || amountAt < 0) return;
            var type = data.Payload[typeAt] as string ?? "?";
            var amount = Convert.ToInt64(data.Payload[amountAt], CultureInfo.InvariantCulture);
            lock (_sampled) _sampled[type] = _sampled.GetValueOrDefault(type) + amount;
        }

        public void Reset() { lock (_sampled) _sampled.Clear(); }

        public List<(string Type, long Sampled)> Largest(int count)
        {
            lock (_sampled)
                return _sampled.OrderByDescending(entry => entry.Value).Take(count)
                    .Select(entry => (entry.Key, entry.Value)).ToList();
        }
    }

    internal static int SaveCost(Campaign campaign, int days)
    {
        campaign.IsPaused = false;
        for (var day = 0; day < days; day++) campaign.TickDay();
        var folder = Path.Combine(Path.GetTempPath(), "dof_save_cost");
        Directory.CreateDirectory(folder);
        SimProfiler.Reset();
        var watch = Stopwatch.StartNew();
        var prepared = SaveSystem.Prepare(folder, "probe", campaign, "", "probe", true);
        var prepareMs = watch.Elapsed.TotalMilliseconds;
        var metadata = prepared.Commit();
        var saveMs = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  on the frame: {prepareMs:N0} ms (snapshot and JSON); on a worker: {saveMs - prepareMs:N0} ms " +
                          "(compress, checksum, write).");
        // The first save of a session also pays for the serializer learning the types; the
        // month's autosave after it does not, and that is the one a player feels every month.
        var warm = new List<double>();
        for (var again = 0; again < 3; again++)
        {
            watch.Restart();
            SaveSystem.Prepare(folder, "probe_warm", campaign, "", "probe", true);
            warm.Add(watch.Elapsed.TotalMilliseconds);
        }
        foreach (var sample in SimProfiler.All.OrderByDescending(entry => entry.TotalMilliseconds))
            if (sample.Runs > 0)
                Console.WriteLine($"    save phase {sample.Name,-20} {sample.TotalMilliseconds,8:N0} ms  " +
                                  $"×{sample.Runs}  {sample.AllocatedBytes / 1048576.0,7:0.0} MB");
        watch.Restart();
        var historyBytes = JsonSerializer.SerializeToUtf8Bytes(
            Enumerable.Range(0, campaign.Nations.Count).ToDictionary(id => id, id => campaign.History.ForNation(id)));
        Console.WriteLine($"  on the frame once warm: {string.Join(", ", warm.Select(ms => $"{ms:N0}"))} ms; " +
                          $"the history alone {watch.Elapsed.TotalMilliseconds:N0} ms for {historyBytes.Length / 1048576.0:0.0} MB.");
        var compressed = File.ReadAllBytes(Path.Combine(folder, "probe.dofsave"));
        byte[] json;
        // Whatever container was written (framed BDF3, Brotli BDF2, gzip, plain JSON), opened
        // the way the game opens it. The framed file's history is its own binary section, so
        // the JSON here is the campaign without it.
        using (var file = new FileStream(Path.Combine(folder, "probe.dofsave"), FileMode.Open, FileAccess.Read))
        using (var source = SaveSystem.OpenJson(file))
        using (var output = new MemoryStream())
        {
            source.CopyTo(output);
            json = output.ToArray();
        }
        Console.WriteLine($"Save after {days} day(s): {saveMs:N0} ms in all, {compressed.Length / 1048576.0:0.0} MB written, " +
                          $"{json.Length / 1048576.0:0.0} MB of JSON.");
        foreach (var level in new[] { System.IO.Compression.CompressionLevel.SmallestSize,
                     System.IO.Compression.CompressionLevel.Optimal, System.IO.Compression.CompressionLevel.Fastest })
        {
            watch.Restart();
            using var memory = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(memory, level, true)) gzip.Write(json);
            Console.WriteLine($"  gzip {level,-12} {watch.Elapsed.TotalMilliseconds,7:N0} ms  {memory.Length / 1048576.0,6:0.0} MB");
        }
        foreach (var level in new[] { System.IO.Compression.CompressionLevel.Optimal,
                     System.IO.Compression.CompressionLevel.SmallestSize })
        {
            watch.Restart();
            using var memory = new MemoryStream();
            using (var brotli = new System.IO.Compression.BrotliStream(memory, level, true)) brotli.Write(json);
            Console.WriteLine($"  brotli {level,-10} {watch.Elapsed.TotalMilliseconds,7:N0} ms  {memory.Length / 1048576.0,6:0.0} MB");
        }
        watch.Restart();
        using (var document = System.Text.Json.JsonDocument.Parse(json))
        {
            Console.WriteLine($"  parse {watch.Elapsed.TotalMilliseconds:N0} ms. Largest parts:");
            var parts = document.RootElement.EnumerateObject()
                .Select(property => (property.Name, Bytes: property.Value.GetRawText().Length,
                    Count: property.Value.ValueKind == System.Text.Json.JsonValueKind.Array ? property.Value.GetArrayLength() : -1))
                .OrderByDescending(part => part.Bytes).Take(14);
            foreach (var (name, bytes, count) in parts)
                Console.WriteLine($"    {name,-28} {bytes / 1048576.0,7:0.00} MB{(count >= 0 ? $"  ({count:N0} items)" : "")}");
            // Inside the two largest collections: which fields of an item are carrying it.
            foreach (var collection in new[] { "States", "Nations" })
            {
                if (!document.RootElement.TryGetProperty(collection, out var items) ||
                    items.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                var fields = new Dictionary<string, long>();
                foreach (var item in items.EnumerateArray())
                    if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var field in item.EnumerateObject())
                            fields[field.Name] = fields.GetValueOrDefault(field.Name) + field.Value.GetRawText().Length + field.Name.Length + 3;
                Console.WriteLine($"  in {collection}: " + string.Join(", ", fields.OrderByDescending(entry => entry.Value).Take(8)
                    .Select(entry => $"{entry.Key} {entry.Value / 1048576.0:0.00} MB")));
            }
        }
        // And back: the file just written, and - when one is named - a save written by an
        // older build, which has to load exactly as it did.
        foreach (var (label, root, slot) in new[] { ("this build's", folder, "probe") }
                     .Concat(Environment.GetEnvironmentVariable("DOF_OLD_SAVE") is { Length: > 0 } old
                         ? new[] { ("an older build's", Path.GetDirectoryName(old)!, Path.GetFileNameWithoutExtension(old)) }
                         : Array.Empty<(string, string, string)>()))
        {
            watch.Restart();
            var loaded = SaveSystem.Load(root, slot, "");
            var samples = loaded.Campaign?.Nations.Sum(nation => loaded.Campaign.History.ForNation(nation.Id).Count) ?? 0;
            var russia = loaded.Campaign?.NationByTag("RUS");
            var last = russia is null ? default : loaded.Campaign.History.ForNation(russia.Id).LastOrDefault();
            Console.WriteLine($"  load {label} save: {(loaded.Success ? "ok" : "FAILED " + loaded.Warning)} in " +
                              $"{watch.Elapsed.TotalMilliseconds:N0} ms; {samples:N0} history sample(s), RUS last week " +
                              $"day {last.Day} GDP {last.Gdp:0.00} treasury {last.Treasury:0.00} arms index {last.ArmsPriceIndex:0.00}");
        }
        _ = metadata;
        return 0;
    }

}
