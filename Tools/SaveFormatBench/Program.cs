using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Which save format, decided by measurement on the owner's own campaign.
///
/// One process measures one candidate. A campaign of 2131 is a gigabyte of objects; two
/// candidates in one process would each be measured on a heap the other one left behind, and
/// the "peak memory" column would be a property of the order they ran in.
///
/// What a sample is: the payload the game writes (<c>CampaignSave</c>, exactly as
/// <c>SaveSystem.Prepare</c> captures it) turned into the bytes that would go into the file,
/// and those bytes turned back into a payload. Capture and Restore are the same for every
/// format, so they are measured once and kept out of the columns; disk is kept out because a
/// file system cache makes it noise and size already says what disk will cost.
///
/// Usage, from the repository root:
///   SaveFormatBench --types
///   SaveFormatBench --list
///   SaveFormatBench &lt;save.dofsave&gt; --sizes | --history [--samples 15] [--warmup 2]
///   SaveFormatBench &lt;save.dofsave&gt; --candidate &lt;name&gt; [--samples 15] [--warmup 2]
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--types")) return TypeSurvey.Run();
        if (args.Contains("--list"))
        {
            foreach (var c in Candidates.All) Console.WriteLine(c.Name + "  " + c.Note);
            return 0;
        }
        if (args.Contains("--sizes")) return Sizes(Path.GetFullPath(args[0]));
        if (args.Contains("--history"))
            return HistoryBench.Run(Path.GetFullPath(args[0]), int.Parse(Arg(args, "--samples", "15")), int.Parse(Arg(args, "--warmup", "2")));
        if (args.Length < 3 || Array.IndexOf(args, "--candidate") < 0)
        {
            Console.WriteLine("usage: SaveFormatBench <save.dofsave> --candidate <name> [--samples N] [--warmup N] | --types | --list");
            return 2;
        }
        var savePath = Path.GetFullPath(args[0]);
        var name = Arg(args, "--candidate", "");
        var samples = int.Parse(Arg(args, "--samples", "15"));
        var warmup = int.Parse(Arg(args, "--warmup", "2"));
        var candidate = Candidates.All.FirstOrDefault(c => c.Name == name);
        if (candidate is null) { Console.WriteLine("unknown candidate " + name); return 2; }
        return Run(savePath, candidate, samples, warmup);
    }

    private static string Arg(string[] args, string key, string fallback)
    {
        var at = Array.IndexOf(args, key);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
    }

    private static int Run(string savePath, Candidate candidate, int samples, int warmup)
    {
        Console.WriteLine($"SAVEBENCH candidate={candidate.Name} samples={samples} warmup={warmup} " +
                          $"runtime={Environment.Version} cores={Environment.ProcessorCount} servergc={System.Runtime.GCSettings.IsServerGC}");
        var clock = Stopwatch.StartNew();
        // The catalogues and map tables are static; a bare process builds them once before a
        // save that refers to them can be read (same as FormableTests --replay-save).
        _ = Campaign.CreateBootstrap(947986505);
        Console.WriteLine($"BOOT ms={clock.ElapsedMilliseconds}");

        // Load as the game loads: SaveSystem.Load on a slot. The owner's file is never
        // written; it is read from wherever the caller copied it.
        clock.Restart();
        var loaded = SaveSystem.Load(Path.GetDirectoryName(savePath)!, Path.GetFileNameWithoutExtension(savePath), "");
        if (loaded.Campaign is null) { Console.WriteLine("LOAD_FAILED " + loaded.Warning); return 1; }
        var campaign = loaded.Campaign;
        Console.WriteLine($"LOAD ms={clock.ElapsedMilliseconds} day={campaign.ElapsedDays} nations={campaign.Nations.Count} states={campaign.States.Count}");
        AttachMap(campaign);
        var originalFingerprint = CampaignFingerprint.Of(campaign);
        Console.WriteLine($"FINGERPRINT original={originalFingerprint:X16}");

        // The payload, as Prepare captures it. The live campaign is let go afterwards: the
        // payload is all a format ever sees, and holding both would double the heap every
        // sample is measured against.
        clock.Restart();
        campaign.FinishStep();
        foreach (var n in campaign.Nations) TacticalDoctrines.EnsureShelves(n);
        var payload = SaveSystem.CampaignSave.Capture(campaign, "", includeRelations: true);
        payload.EmbeddedMetadata = new SaveMetadata
        {
            Slot = "bench", DisplayName = "bench", NationTag = campaign.Player?.Tag ?? "",
            NationName = campaign.Player?.Name ?? "", GameDate = campaign.Date.ToString(),
            ElapsedDays = campaign.ElapsedDays, Seed = campaign.Seed, MapDataVersion = SaveSystem.MapDataVersion,
            ProvinceCount = payload.ProvinceCount
        };
        Console.WriteLine($"CAPTURE ms={clock.ElapsedMilliseconds}");
        campaign = null;
        loaded = default;
        FullCollect();

        var reference = Formats.JsonDigest(payload);
        Console.WriteLine($"PAYLOAD_DIGEST {Convert.ToHexString(reference)[..16]} heap_mb={GC.GetTotalMemory(false) / 1048576}");

        // Warm-up, then samples. Every sample starts from a collected heap so that one
        // sample's garbage is not the next one's pause.
        var output = new PooledBuffer(64 << 20);
        var enc = new List<Sample>();
        byte[] encoded = null;
        try
        {
            for (var i = 0; i < warmup + samples; i++)
            {
                FullCollect();
                var s = Measure(() => { output.Reset(); candidate.Encode(payload, output); return output.Length; });
                if (i >= warmup) enc.Add(s);
            }
            encoded = output.WrittenSpan.ToArray();
        }
        catch (Exception error)
        {
            Console.WriteLine($"ENCODE_FAILED type={error.GetType().Name} message={Flat(error)}");
            Console.WriteLine($"RESULT|{candidate.Name}|FAILED|encode|{Flat(error)}");
            return 3;
        }
        output.Dispose();
        var fileHash = Convert.ToHexString(SHA256.HashData(encoded))[..16];
        Console.WriteLine($"ENCODED bytes={encoded.Length} sha={fileHash}");

        var dec = new List<Sample>();
        long checksum = 0;
        try
        {
            for (var i = 0; i < warmup + samples; i++)
            {
                FullCollect();
                var s = Measure(() =>
                {
                    var back = candidate.Decode(encoded);
                    // Something the optimiser cannot drop: counts from every big list.
                    return back.Nations.Count + back.States.Count + back.Relations.Count + back.Chronicle.Count +
                           back.History.Count + back.Armies.Count;
                });
                checksum += s.Result;
                if (i >= warmup) dec.Add(s);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"DECODE_FAILED type={error.GetType().Name} message={Flat(error)}");
            Console.WriteLine($"RESULT|{candidate.Name}|FAILED|decode|{Flat(error)}");
            return 3;
        }

        // Round trip, twice over. First the payload itself, written as the game's JSON: if a
        // single member was lost or changed anywhere in the graph, the bytes differ. Then the
        // campaign restored from it, with the same migrations Load runs, fingerprinted.
        FullCollect();
        var decoded = candidate.Decode(encoded);
        var digest = Formats.JsonDigest(decoded);
        var payloadSame = digest.AsSpan().SequenceEqual(reference);
        if (!payloadSame) Formats.ExplainDifference(payload, decoded);
        payload = null;
        FullCollect();
        clock.Restart();
        var restored = RestoreLikeLoad(decoded);
        var restoreMs = clock.ElapsedMilliseconds;
        AttachMap(restored);
        var fingerprint = CampaignFingerprint.Of(restored);
        Console.WriteLine($"ROUNDTRIP payload_json_identical={payloadSame} fingerprint={fingerprint:X16} " +
                          $"same={(fingerprint == originalFingerprint)} restore_ms={restoreMs}");

        var proc = Process.GetCurrentProcess();
        Console.WriteLine(
            $"RESULT|{candidate.Name}|{encoded.Length}|" +
            $"{Median(enc, s => s.Ms):0}|{P95(enc, s => s.Ms):0}|{Median(enc, s => s.AllocMb):0}|{Median(enc, s => s.PeakMb):0}|" +
            $"{Median(dec, s => s.Ms):0}|{P95(dec, s => s.Ms):0}|{Median(dec, s => s.AllocMb):0}|{Median(dec, s => s.PeakMb):0}|" +
            $"{payloadSame}|{fingerprint == originalFingerprint}|{checksum}|{proc.PeakWorkingSet64 / 1048576}|" +
            $"{Median(enc, s => s.PeakWsMb):0}|{Max(enc, s => s.PeakWsMb):0}|{Median(dec, s => s.PeakWsMb):0}|{Max(dec, s => s.PeakWsMb):0}|" +
            $"{Max(enc, s => s.PeakMb):0}|{Max(dec, s => s.PeakMb):0}");
        Console.WriteLine("ENC_MS " + string.Join(",", enc.Select(s => s.Ms.ToString("0"))));
        Console.WriteLine("DEC_MS " + string.Join(",", dec.Select(s => s.Ms.ToString("0"))));
        // The collector's own account, so a run under DOTNET_HeapVerify can be told from a run
        // without it by more than its timing: verification is paid inside the pauses.
        Console.WriteLine($"GC pause_ms={GC.GetTotalPauseDuration().TotalMilliseconds:0} gen0={GC.CollectionCount(0)} " +
                          $"gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} " +
                          $"heap_verify={Environment.GetEnvironmentVariable("DOTNET_HeapVerify") ?? "-"}");
        return payloadSame && fingerprint == originalFingerprint ? 0 : 1;
    }

    /// <summary>
    /// Where the bytes are: each top-level member of the payload as the game's compact JSON
    /// and as contractless MessagePack, plus how well zstd-3 packs each. Explains a total
    /// rather than timing it, so one pass, no samples.
    /// </summary>
    private static int Sizes(string savePath)
    {
        _ = Campaign.CreateBootstrap(947986505);
        var loaded = SaveSystem.Load(Path.GetDirectoryName(savePath)!, Path.GetFileNameWithoutExtension(savePath), "");
        if (loaded.Campaign is null) { Console.WriteLine("LOAD_FAILED " + loaded.Warning); return 1; }
        var payload = SaveSystem.CampaignSave.Capture(loaded.Campaign, "", includeRelations: true);
        loaded = default;
        FullCollect();
        SaveStringTable.Use(payload.StringTable);
        Console.WriteLine("SIZE|member|json|json_zstd3|mpack|mpack_zstd3");
        foreach (var field in typeof(SaveSystem.CampaignSave).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            var value = field.GetValue(payload);
            var json = JsonSerializer.SerializeToUtf8Bytes(value, field.FieldType, Formats.Compact);
            var mpack = MessagePack.MessagePackSerializer.Serialize(field.FieldType, value, Formats.MsgPackPlain);
            if (json.Length < 100_000 && mpack.Length < 100_000) continue;
            Console.WriteLine($"SIZE|{field.Name}|{json.Length}|{Packed(json)}|{mpack.Length}|{Packed(mpack)}");
        }
        return 0;

        static int Packed(byte[] bytes)
        {
            using var target = new PooledBuffer(bytes.Length / 2 + 1024);
            Formats.Zstd(3)(bytes, target);
            return target.Length;
        }
    }

    private static string Flat(Exception error)
    {
        var text = error.Message;
        for (var inner = error.InnerException; inner is not null; inner = inner.InnerException) text += " <- " + inner.Message;
        return text.Replace('\n', ' ').Replace('\r', ' ').Replace('|', '/');
    }

    /// <summary>Everything Load does to a payload after reading it, in the same order
    /// (SaveSystem.Load), so a restored campaign here is the one a player would get.</summary>
    internal static Campaign RestoreLikeLoad(SaveSystem.CampaignSave payload)
    {
        var campaign = payload.Restore();
        SecessionColours.MigrateLegacy(campaign);
        NationNaming.MigrateLegacy(campaign);
        WorldResourceMigration.Apply(campaign);
        foreach (var n in campaign.Nations) TacticalDoctrines.EnsureShelves(n);
        campaign.RepairDiminishedRestorationsAfterAttach = true;
        foreach (var n in campaign.Nations)
            if (n.Movement is not null && campaign.HasOwnedLand(n))
                MovementRules.SynchroniseTerritory(campaign, n);
        Factions.SettleDefenselessRebelRemnants(campaign);
        return campaign;
    }

    // Same as SimBench's AttachMap: the fingerprint reads the province map, and a campaign
    // without it would be compared on less than the whole world.
    private static void AttachMap(Campaign campaign)
    {
        MapRasterCatalog.ResetCatalog();
        HydrologyCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var topology, out var message))
            throw new Exception("province map unavailable: " + message);
        if (MapRasterCatalog.TryLoadHydrology(WorldMapSource.Width, WorldMapSource.Height, out var hydrology, out _))
            topology.AttachHydrology(hydrology);
        campaign.AttachProvinceTopology(topology);
    }

    internal static void FullCollect()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private readonly record struct Sample(double Ms, double AllocMb, double PeakMb, long Result, double PeakWsMb = 0);

    /// <summary>
    /// Time, bytes allocated on every thread (a compressor may use several), and the highest
    /// managed heap seen while it ran above where it started. The peak is polled every
    /// millisecond from a second thread: a short spike between two polls is missed, which
    /// errs low, and is the same error for every candidate.
    /// </summary>
    private static Sample Measure(Func<long> work)
    {
        var baseline = GC.GetTotalMemory(false);
        long peak = baseline;
        // The process's working set too: a compressor's window and buffers are native memory
        // the managed heap does not see (Brotli's encoder, ZstdSharp's contexts), and it is
        // the whole process that a phone runs out of.
        var baselineWs = Environment.WorkingSet;
        long peakWs = baselineWs;
        var running = true;
        var poller = new System.Threading.Thread(() =>
        {
            while (Volatile.Read(ref running))
            {
                var now = GC.GetTotalMemory(false);
                if (now > peak) peak = now;
                var ws = Environment.WorkingSet;
                if (ws > peakWs) peakWs = ws;
                System.Threading.Thread.Sleep(1);
            }
        }) { IsBackground = true, Priority = System.Threading.ThreadPriority.AboveNormal };
        poller.Start();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        var result = work();
        clock.Stop();
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var end = GC.GetTotalMemory(false);
        Volatile.Write(ref running, false);
        poller.Join();
        if (end > peak) peak = end;
        var endWs = Environment.WorkingSet;
        if (endWs > peakWs) peakWs = endWs;
        return new Sample(clock.Elapsed.TotalMilliseconds, (allocatedAfter - allocated) / 1048576.0,
            (peak - baseline) / 1048576.0, result, (peakWs - baselineWs) / 1048576.0);
    }

    private static class Volatile
    {
        public static bool Read(ref bool location) => System.Threading.Volatile.Read(ref location);
        public static void Write(ref bool location, bool value) => System.Threading.Volatile.Write(ref location, value);
    }

    private static double Median(List<Sample> list, Func<Sample, double> pick)
    {
        var sorted = list.Select(pick).OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static double Max(List<Sample> list, Func<Sample, double> pick) => list.Count == 0 ? 0 : list.Max(pick);

    // Nearest-rank p95: with 15 samples it is the 15th, i.e. the worst one. Said so in the
    // document rather than dressed up as interpolation.
    private static double P95(List<Sample> list, Func<Sample, double> pick)
    {
        var sorted = list.Select(pick).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(0.95 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
