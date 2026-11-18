using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;
using static DealOrForce.Tools.BenchChecks;

namespace DealOrForce.Tools;

internal static class PerformanceProbeReports
{
    [SimBenchReport("--profiler-overhead", SimBenchPhase.PreCampaign, Order = 66,
        Description = "Measure per-scope overhead and allocation of SimProfiler.Measure")]
    public static int RunProfilerOverhead(SimBenchContext ctx) => ProfilerOverhead();

    [SimBenchReport("--creation-profile", SimBenchPhase.PreCampaign, Order = 71,
        Description = "Measure cold and warm campaign bootstrap timings by phase")]
    public static int RunCreationProfile(SimBenchContext ctx)
    {
        foreach (var pass in new[] { "cold", "warm" })
        {
            ScenarioCatalog.BuildTimings = new List<(string Phase, double Milliseconds)>();
            var clock = Stopwatch.StartNew();
            Campaign.CreateBootstrap(ctx.Seed);
            Console.WriteLine($"CREATION {pass}: {clock.ElapsedMilliseconds:N0} ms");
            foreach (var (phase, ms) in ScenarioCatalog.BuildTimings.OrderByDescending(row => row.Milliseconds))
                Console.WriteLine($"  {phase,-22} {ms,8:N1} ms");
        }
        ScenarioCatalog.BuildTimings = null;
        return 0;
    }

    [SimBenchReport("--profile-save", SimBenchPhase.AfterMap, Order = 333,
        Description = "Load a save and profile tick performance over N days")]
    public static int? RunProfileSave(SimBenchContext ctx)
    {
        var profileSaveAt = ctx.IndexOf("--profile-save");
        if (profileSaveAt < 0 || profileSaveAt + 1 >= ctx.Args.Length) return null;
        var savedDays = profileSaveAt + 2 < ctx.Args.Length && int.TryParse(ctx.Args[profileSaveAt + 2], out var d) ? d : 60;
        return ProfileSave(ctx.Campaign, ctx.Args[profileSaveAt + 1], savedDays);
    }

    [SimBenchReport("--save-cost", SimBenchPhase.PostOpening, Order = 597,
        Description = "Measure save serialization and deserialization time over N days")]
    public static int RunSaveCost(SimBenchContext ctx)
    {
        var saveCostAt = ctx.IndexOf("--save-cost");
        var saveDays = saveCostAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[saveCostAt + 1], out var d) ? d : 365;
        return SaveCost(ctx.Campaign, saveDays);
    }

    [SimBenchReport("--alloc", SimBenchPhase.PostOpening, Order = 604,
        Description = "Measure allocation breakdown by type over N days")]
    public static int RunAlloc(SimBenchContext ctx)
    {
        var allocAt = ctx.IndexOf("--alloc");
        var allocDays = allocAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[allocAt + 1], out var d) ? d : 60;
        return Allocations(ctx.Campaign, allocDays);
    }

    [SimBenchReport("--replay", SimBenchPhase.PostOpening, Order = 612,
        Description = "Replay an existing save slot from disk")]
    public static int RunReplay(SimBenchContext ctx)
    {
        var replayAt = ctx.IndexOf("--replay");
        var root = replayAt + 1 < ctx.Args.Length ? ctx.Args[replayAt + 1] : DefaultSaveRoot();
        var slot = replayAt + 2 < ctx.Args.Length ? ctx.Args[replayAt + 2] : "autosave";
        var length = replayAt + 3 < ctx.Args.Length && int.TryParse(ctx.Args[replayAt + 3], out var replayDays) ? replayDays : 30;
        return ReplaySave(root, slot, length);
    }

    [SimBenchReport("--resave", SimBenchPhase.PostOpening, Order = 721,
        Description = "Load a save, re-write compressed & text formats, verify fingerprint")]
    public static int? RunResave(SimBenchContext ctx)
    {
        var resaveAt = ctx.IndexOf("--resave");
        if (resaveAt < 0 || resaveAt + 3 >= ctx.Args.Length) return null;
        var clock = Stopwatch.StartNew();
        var loaded = SaveSystem.Load(ctx.Args[resaveAt + 1], ctx.Args[resaveAt + 2], "");
        if (loaded.Campaign is null) { Console.WriteLine("  resave: " + loaded.Warning); return 1; }
        var original = loaded.Campaign;
        if (original.ProvinceMap is null) AttachMap(original);
        Console.WriteLine($"  resave: loaded in {clock.ElapsedMilliseconds} ms, day {original.ElapsedDays}");
        var before = CampaignFingerprint.Of(original);
        var outRoot = ctx.Args[resaveAt + 3];
        Directory.CreateDirectory(outRoot);
        original.Defines.SaveTextFormat = false;
        clock.Restart();
        SaveSystem.Save(outRoot, "resaved", original, "");
        Console.WriteLine($"  resave: compressed written in {clock.ElapsedMilliseconds} ms" +
                          $" (defaults left out: {SaveDefaults.Types} types, {SaveDefaults.Members} members)");
        original.Defines.SaveTextFormat = true;
        SaveSystem.Save(outRoot, "resaved_text", original, "");
        foreach (var file in Directory.GetFiles(outRoot))
            Console.WriteLine($"  resave: {Path.GetFileName(file)} {new FileInfo(file).Length / 1024:N0} KiB");
        clock.Restart();
        var back = SaveSystem.Load(outRoot, "resaved", "");
        if (back.Campaign is null) { Console.WriteLine("  resave: reload failed: " + back.Warning); return 1; }
        if (back.Campaign.ProvinceMap is null) AttachMap(back.Campaign);
        var after = CampaignFingerprint.Of(back.Campaign);
        Console.WriteLine($"  resave: reloaded in {clock.ElapsedMilliseconds} ms; fingerprint {before:X16} -> {after:X16} {(before == after ? "SAME" : "DIFFERENT")}");
        return before == after ? 0 : 1;
    }

    [SimBenchReport("--save-size", SimBenchPhase.PostOpening, Order = 756,
        Description = "Measure save file size and field breakdown across long runs")]
    public static int RunSaveSize(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var sizeAt = ctx.IndexOf("--save-size");
        var sizeDays = sizeAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[sizeAt + 1], out var sdays) ? sdays : 3650;
        var every = sizeAt + 2 < ctx.Args.Length && int.TryParse(ctx.Args[sizeAt + 2], out var sev) ? sev : 3650;
        campaign.IsPaused = false;
        var sizeFolder = Path.Combine(Path.GetTempPath(), "dof-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sizeFolder);
        for (var day = 1; day <= sizeDays; day++)
        {
            campaign.TickDay();
            if (day % every != 0 && day != sizeDays) continue;
            campaign.Defines.SaveTextFormat = false;
            SaveSystem.Save(sizeFolder, "size", campaign, "bench");
            var file = new FileInfo(Directory.GetFiles(sizeFolder, "size.*").Where(f => !f.EndsWith(".json")).First());
            var compressedBytes = file.Length;
            campaign.Defines.SaveTextFormat = true;
            SaveSystem.Save(sizeFolder, "size_text", campaign, "bench");
            var textFile = new FileInfo(Directory.GetFiles(sizeFolder, "size_text*").Where(f => !f.EndsWith(".json")).First());
            Console.WriteLine($"TEXT day {campaign.ElapsedDays}: {textFile.Length / 1048576.0:0.0} MB as a text save");
            campaign.Defines.SaveTextFormat = false;
            var wire = SaveSystem.ExportNetworkSnapshot(campaign, "bench");
            using var input = new MemoryStream(wire);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var root = JsonNode.Parse(gzip)!.AsObject();
            var parts = root.Select(pair => (pair.Key, Bytes: (long)(pair.Value?.ToJsonString().Length ?? 0)))
                .OrderByDescending(part => part.Bytes).ToList();
            var total = parts.Sum(part => part.Bytes);
            Console.WriteLine($"SAVE day {campaign.ElapsedDays} ({campaign.Date}): file {compressedBytes / 1048576.0:0.0} MB compressed, " +
                              $"{total / 1048576.0:0.0} MB of JSON; {campaign.Nations.Count} countries, {campaign.Wars.Count} wars ({campaign.Wars.Count(w => w.Ended)} ended), {campaign.Battles.Count} battles");
            foreach (var (key, bytes) in parts.Take(14))
                Console.WriteLine($"   {key,-34} {bytes / 1048576.0,8:0.00} MB  {100.0 * bytes / Math.Max(1, total),5:0.0} %");
            if (root["Nations"] is JsonArray nations)
            {
                var fields = new Dictionary<string, long>();
                foreach (var nation in nations.OfType<JsonObject>())
                    foreach (var (key, value) in nation)
                        fields[key] = fields.GetValueOrDefault(key) + (value?.ToJsonString().Length ?? 0);
                Console.WriteLine("   in the countries: " + string.Join(", ", fields.OrderByDescending(f => f.Value).Take(10)
                    .Select(f => $"{f.Key} {f.Value / 1048576.0:0.00} MB")));
            }
            if (root["Wars"] is JsonArray warList)
            {
                var fields = new Dictionary<string, long>();
                foreach (var war in warList.OfType<JsonObject>())
                    foreach (var (key, value) in war)
                        fields[key] = fields.GetValueOrDefault(key) + (value?.ToJsonString().Length ?? 0);
                Console.WriteLine("   in the wars: " + string.Join(", ", fields.OrderByDescending(f => f.Value).Take(8)
                    .Select(f => $"{f.Key} {f.Value / 1048576.0:0.00} MB")));
            }
        }
        Directory.Delete(sizeFolder, true);
        return 0;
    }

    [SimBenchReport("--memory", SimBenchPhase.PostOpening, Order = 1126,
        Description = "Report managed heap, working set, and entity counts")]
    public static int RunMemory(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var heap = GC.GetTotalMemory(true);
        var groups = campaign.States.Sum(s => (long)s.PopulationGroups.Count);
        Console.WriteLine($"MEMORY heap {heap / 1048576.0:0} MB; process {Environment.WorkingSet / 1048576.0:0} MB");
        Console.WriteLine($"MEMORY states {campaign.States.Count}, population groups {groups:N0} (largest region {campaign.States.Max(s => s.PopulationGroups.Count):N0}), " +
                          $"distinct cultures {campaign.States.SelectMany(s => s.PopulationGroups).Select(g => g.Culture).Distinct().Count()}");
        Console.WriteLine($"MEMORY nations {campaign.Nations.Count}, relations {campaign.Relations.Count:N0}, armies {campaign.Armies.Count}, wars {campaign.Wars.Count} " +
                          $"(ended {campaign.Wars.Count(w => w.Ended)}), battles {campaign.Battles.Count}, chronicle {campaign.Chronicle.Entries.Count}");
        foreach (var s in campaign.States.OrderByDescending(s => s.PopulationGroups.Count).Take(5))
            Console.WriteLine($"MEMORY   {s.DisplayName}: {s.PopulationGroups.Count:N0} groups, {s.PopulationGroups.Select(g => g.Culture).Distinct().Count()} cultures");
        return 0;
    }

    [SimBenchReport("--save-roundtrip", SimBenchPhase.PostOpening, Order = 1404,
        Description = "Save to temporary folder, reload, and verify matching fingerprint")]
    public static int RunSaveRoundtrip(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var roundtripAt = ctx.IndexOf("--save-roundtrip");
        var runDays = roundtripAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[roundtripAt + 1], out var rd) ? rd : 0;
        for (var day = 0; day < runDays; day++) campaign.TickDay();
        var folder = Path.Combine(Path.GetTempPath(), "dof-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        campaign.Defines.SaveTextFormat = true;
        SaveSystem.Save(folder, "roundtrip", campaign, "bench");
        var back = SaveSystem.Load(folder, "roundtrip", "bench");
        if (back.Campaign is null) { Console.WriteLine("  roundtrip: reload failed: " + back.Warning); return 1; }
        back.Campaign.AttachProvinceTopology(campaign.ProvinceMap.CloneForOverrides());
        var same = CampaignFingerprint.Of(back.Campaign) == CampaignFingerprint.Of(campaign);
        Console.WriteLine($"  roundtrip after {runDays} day(s): fingerprint {(same ? "SAME" : "DIFFERENT")}");
        if (!same)
        {
            Console.WriteLine("  first difference: " + WhatDiffers(campaign, back.Campaign));
            Console.WriteLine("  snapshot differences: " + SnapshotDifferences(campaign, back.Campaign));
        }
        Directory.Delete(folder, true);
        return same ? 0 : 1;
    }

    [SimBenchReport("--smooth", SimBenchPhase.PostOpening, Order = 1436,
        Description = "Measure staged StepFor frame-time percentiles")]
    public static int RunSmooth(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var smoothAt = ctx.IndexOf("--smooth");
        var smoothDays = smoothAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[smoothAt + 1], out var sd) ? sd : 30;
        var frameBudget = smoothAt + 2 < ctx.Args.Length && double.TryParse(ctx.Args[smoothAt + 2],
            NumberStyles.Float, CultureInfo.InvariantCulture, out var fb) ? fb : 6.0;
        campaign.IsPaused = false;
        for (var warm = 0; warm < 3; warm++) campaign.TickDay();
        var frames = new List<double>();
        var frameClock = Stopwatch.StartNew();
        var smoothUntil = campaign.ElapsedDays + smoothDays;
        var stepsDone = 0;
        while (campaign.ElapsedDays < smoothUntil)
        {
            var totalsBefore = SimProfiler.All.ToDictionary(sample => sample.Name, sample => sample.TotalMilliseconds);
            frameClock.Restart();
            if (campaign.StepFor(frameBudget)) stepsDone++;
            var frameMs = frameClock.Elapsed.TotalMilliseconds;
            frames.Add(frameMs);
            if (frameMs > 30)
                Console.WriteLine($"  heavy frame {frameMs:0.0} ms on day {campaign.ElapsedDays}: " + string.Join(", ",
                    SimProfiler.All.Select(sample => (sample.Name, Ms: sample.TotalMilliseconds - totalsBefore.GetValueOrDefault(sample.Name)))
                        .Where(part => part.Ms >= 2).OrderByDescending(part => part.Ms).Take(6)
                        .Select(part => $"{part.Name.Trim()} {part.Ms:0.0}")));
        }
        campaign.FinishStep();
        var ordered = frames.OrderBy(ms => ms).ToList();
        double Share(double share) => ordered[Math.Min(ordered.Count - 1, (int)(ordered.Count * share))];
        Console.WriteLine($"Smoothed at {frameBudget:0.#} ms a frame: {frames.Count:N0} frames for {stepsDone:N0} steps; " +
                          $"median {Share(.5):0.00} ms, p95 {Share(.95):0.00}, p99 {Share(.99):0.00}, " +
                          $"worst {ordered[^1]:0.0}; frames over 16 ms: {frames.Count(ms => ms > 16):N0}, over 33 ms: {frames.Count(ms => ms > 33):N0}.");
        Console.WriteLine($"  world fingerprint after {campaign.ElapsedDays} day(s): {CampaignFingerprint.Of(campaign):X16}");
        return 0;
    }

    [SimBenchReport("--profile", SimBenchPhase.PostOpening, Order = 1476,
        Description = "Profile campaign tick timings over N days")]
    public static int RunProfile(SimBenchContext ctx)
    {
        var profileAt = ctx.IndexOf("--profile");
        var profileDays = profileAt + 1 < ctx.Args.Length && int.TryParse(ctx.Args[profileAt + 1], out var d) ? d : 120;
        return Profile(ctx.Campaign, profileDays);
    }

    [SimBenchReport("--frames", SimBenchPhase.PostOpening, Order = 1480,
        Description = "Drive SimulationFrameClock frame-by-frame at speed(s) e.g. --frames 5,3 [--frame-days N]")]
    public static int? RunFrames(SimBenchContext ctx)
    {
        var framesSpec = ctx.Arg("--frames");
        if (string.IsNullOrWhiteSpace(framesSpec)) return null;
        var frameDays = ctx.IntArg("--frame-days", 20);
        foreach (var part in framesSpec.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, out var speed)) FramesAtSpeed(ctx.Campaign, speed, frameDays);
        return 0;
    }
}
