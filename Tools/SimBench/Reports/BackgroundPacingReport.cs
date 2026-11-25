using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

internal static class BackgroundPacingReport
{
    [SimBenchReport("--background-pace-save", SimBenchPhase.AfterMap, Order = 334,
        Description = "Time the mobile simulation worker and a synthetic UI reader on a saved campaign")]
    public static int Run(SimBenchContext ctx)
    {
        var option = ctx.IndexOf("--background-pace-save");
        if (option + 1 >= ctx.Args.Length || ctx.Campaign.ProvinceMap is null) return 2;
        var path = Path.GetFullPath(ctx.Args[option + 1]);
        var days = option + 2 < ctx.Args.Length && int.TryParse(ctx.Args[option + 2], out var count)
            ? Math.Clamp(count, 1, 90) : 14;
        var fps = option + 3 < ctx.Args.Length && int.TryParse(ctx.Args[option + 3], out var rate)
            ? Math.Clamp(rate, 15, 120) : 30;
        var loaded = SaveSystem.Load(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), "");
        if (!loaded.Success || loaded.Campaign is null)
        {
            Console.WriteLine("Cannot load the pacing fixture. " + loaded.Warning);
            return 2;
        }

        var campaign = loaded.Campaign;
        campaign.AttachProvinceTopology(ctx.Campaign.ProvinceMap);
        campaign.ObserverMode = true;
        campaign.IsPaused = true;
        campaign.Speed = 5;
        // Match normal gameplay, where detailed simulation profiling is opt-in.
        SimProfiler.Enabled = false;
        Campaign.StageSliceMilliseconds = 1.5;
        using var runner = new BackgroundSimRunner { MobilePacing = true, MinSpeed5DayIntervalMs = 22.0, MeasurePacing = true };
        using var process = Process.GetCurrentProcess();
        runner.Start(campaign);
        var startingHours = campaign.ElapsedHours;
        var destination = startingHours + days * 24L;
        var requestedFrames = 0;
        var readFrames = 0;
        var lastRead = -1.0;
        var readGaps = new List<double>();
        var nextFrame = 0.0;
        var frameMs = 1000.0 / fps;
        var startingAllocation = GC.GetTotalAllocatedBytes(precise: true);
        var startingCpu = process.TotalProcessorTime;
        var startingGen2 = GC.CollectionCount(2);
        var initialSnapshots = runner.ReadSnapshotTimings();
        var clock = Stopwatch.StartNew();
        runner.RunWrite(c => c.IsPaused = false, refreshSnapshot: false);
        while (clock.Elapsed.TotalSeconds < 120 && campaign.RenderSnapshot.ElapsedHours < destination)
        {
            if (runner.DrainMainThreadUpdates(out _, out _, out _, out var fault) && fault is not null)
                throw new InvalidOperationException("The background worker failed.", fault);
            var now = clock.Elapsed.TotalMilliseconds;
            if (now < nextFrame) { Thread.Sleep(1); continue; }
            nextFrame += frameMs;
            if (nextFrame <= now) nextFrame = now + frameMs;
            requestedFrames++;
            if (!campaign.TryEnterRead(0, requestWindowOnMiss: true)) continue;

            campaign.ExitRead();
            readFrames++;
            if (lastRead >= 0.0) readGaps.Add(now - lastRead);
            lastRead = now;
        }

        var wallMs = clock.Elapsed.TotalMilliseconds;
        var publishedHours = campaign.RenderSnapshot.ElapsedHours - startingHours;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - startingAllocation;
        var cpuMs = (process.TotalProcessorTime - startingCpu).TotalMilliseconds;
        var gen2 = GC.CollectionCount(2) - startingGen2;
        var worker = runner.ReadPacingTimings();
        var snapshots = runner.ReadSnapshotTimings();
        runner.RunWrite(c => c.IsPaused = true);
        if (publishedHours < days * 24L || readGaps.Count == 0)
        {
            Console.WriteLine($"PACING incomplete hours={publishedHours} reads={readFrames}/{requestedFrames}");
            return 1;
        }

        readGaps.Sort();
        var p95 = readGaps[(int)Math.Ceiling(.95 * readGaps.Count) - 1];
        var p99 = readGaps[(int)Math.Ceiling(.99 * readGaps.Count) - 1];
        var simulatedDays = publishedHours / 24d;
        var timing = FormattableString.Invariant($"ms_per_day={wallMs / simulatedDays:F2} cpu_ms_per_day={cpuMs / simulatedDays:F2}");
        var memory = FormattableString.Invariant($"alloc_mb_per_day={allocated / 1_000_000d / simulatedDays:F2} gen2={gen2}");
        var reads = FormattableString.Invariant($"read_frames={readFrames}/{requestedFrames} read_gap_p95_ms={p95:F2} read_gap_p99_ms={p99:F2} read_gap_max_ms={readGaps[^1]:F2}");
        Console.WriteLine($"PACING seed={ctx.Seed} fps={fps} hours={publishedHours} {timing} {memory} {reads}");
        Console.WriteLine(FormattableString.Invariant(
            $"PACING worker_acquire_ms_per_day={worker.AcquireMs / simulatedDays:F2} worker_work_ms_per_day={worker.WorkMs / simulatedDays:F2} worker_release_ms_per_day={worker.ReleaseMs / simulatedDays:F2}"));
        Console.WriteLine(FormattableString.Invariant(
            $"PACING snapshots={snapshots.Count - initialSnapshots.Count} capture_ms_per_day={(snapshots.Milliseconds - initialSnapshots.Milliseconds) / simulatedDays:F2} capture_mb_per_day={(snapshots.Bytes - initialSnapshots.Bytes) / 1_000_000d / simulatedDays:F2}"));
        Console.WriteLine($"PACING final_hour={campaign.ElapsedHours} fingerprint={CampaignFingerprint.Of(campaign):X16}");
        return 0;
    }
}
