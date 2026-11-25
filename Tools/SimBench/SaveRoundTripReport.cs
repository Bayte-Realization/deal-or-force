using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--save-roundtrip-file &lt;file.dofsave&gt; [N] [out-folder]`: the player's own campaign through the
/// game's own save path, N times, timed by halves, with the peak memory of each half.
///
/// SaveFormatBench measures formats on a captured payload; this measures what the player waits
/// for - <see cref="SaveSystem.Prepare"/> on the frame, <see cref="SaveSystem.PreparedSave.Commit"/>
/// on the worker and <see cref="SaveSystem.Load"/> on the way back - and judges every round by
/// the world, not by the bytes: the campaign fingerprint and the history table (which the
/// fingerprint does not cover) must both come back as they went. It uses nothing but those
/// three public calls, so the same file compiles against the code before the change of the
/// binary format and after it: run on each build, it is the before-and-after. Run fifty times
/// beside a heavy neighbour with the GC's heap verification on, it is the stress test for the
/// access violation the format bench saw once.
///
/// After the rounds, the same world is written in every older shape the game still reads - the
/// text save, gzip, and "BDF2" Brotli at quality 4 exactly as builds before 18 November wrote
/// it - and each must load as the same world with the same history.
///
/// The source file is opened read-only and never written; rounds go to the out-folder.
/// </summary>
internal static class SaveRoundTripReport
{
    public static int Run(Campaign fresh, string path, int rounds, string outFolder)
    {
        if (fresh.ProvinceMap is null) { Console.WriteLine("  province map unavailable"); return 2; }
        rounds = Math.Max(1, rounds);
        var source = Path.GetFullPath(path);
        outFolder = string.IsNullOrEmpty(outFolder)
            ? Path.Combine(Path.GetTempPath(), "dof-roundtrip-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(outFolder);
        Directory.CreateDirectory(outFolder);

        Collect();
        var sourceLoad = Measured(() => SaveSystem.Load(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source), ""));
        var loaded = sourceLoad.Value;
        if (!loaded.Success) { Console.WriteLine($"  could not load {source}: {loaded.Warning}"); return 2; }
        var original = loaded.Campaign;
        original.AttachProvinceTopology(fresh.ProvinceMap);
        Console.WriteLine($"ROUNDTRIP source {source}: {new FileInfo(source).Length:N0} bytes, loaded in {sourceLoad.Ms:N0} ms " +
                          $"(peak +{sourceLoad.PeakMb:N0} MB), day {original.ElapsedDays:N0} ({original.Date}), {original.Nations.Count:N0} countries");
        var fingerprint = CampaignFingerprint.Of(original);
        var history = HistoryHash(original);
        Console.WriteLine($"ROUNDTRIP source fingerprint {fingerprint:X16} history {history}");

        var prepares = new List<double>();
        var commits = new List<double>();
        var loads = new List<double>();
        var commitPeaks = new List<double>();
        var loadPeaks = new List<double>();
        long size = 0;
        var failures = 0;
        var magic = "";
        original.Defines.SaveTextFormat = false;
        for (var round = 1; round <= rounds; round++)
        {
            Collect();
            var prepare = Measured(() => SaveSystem.Prepare(outFolder, "roundtrip", original, ""));
            var commit = Measured(() => prepare.Value.Commit());
            var file = Path.Combine(outFolder, "roundtrip.dofsave");
            size = new FileInfo(file).Length;
            magic = Magic(file);
            var prepareMs = prepare.Ms;
            prepare = default;
            // Each round reads a world as large as the one it wrote; collect the last one first so
            // a round measures the format and not the heap the previous round left behind.
            Collect();
            var load = Measured(() => SaveSystem.Load(outFolder, "roundtrip", ""));
            var back = load.Value;
            if (!back.Success)
            {
                failures++;
                Console.WriteLine($"ROUNDTRIP round {round}: LOAD FAILED {back.Warning}");
                continue;
            }
            back.Campaign.AttachProvinceTopology(fresh.ProvinceMap);
            var again = CampaignFingerprint.Of(back.Campaign);
            var againHistory = HistoryHash(back.Campaign);
            var same = again == fingerprint && againHistory == history;
            if (!same) failures++;
            prepares.Add(prepareMs);
            commits.Add(commit.Ms);
            loads.Add(load.Ms);
            commitPeaks.Add(commit.PeakMb);
            loadPeaks.Add(load.PeakMb);
            Console.WriteLine($"ROUNDTRIP round {round}: prepare {prepareMs:N0} ms, commit {commit.Ms:N0} ms (peak +{commit.PeakMb:N0} MB), " +
                              $"load {load.Ms:N0} ms (peak +{load.PeakMb:N0} MB), {size:N0} bytes {magic}, " +
                              $"fingerprint {again:X16} history {againHistory} {(same ? "SAME" : "DIFFERENT")}");
            back = default;
            load = default;
        }

        // The editable format once, for its size and its time: it is the other half of the setting.
        original.Defines.SaveTextFormat = true;
        Collect();
        var textSave = Measured(() => SaveSystem.Save(outFolder, "roundtrip_text", original, ""));
        var textPath = Path.Combine(outFolder, "roundtrip_text.dofsave");
        var textSize = new FileInfo(textPath).Length;
        original.Defines.SaveTextFormat = false;
        Collect();
        var textLoad = Measured(() => SaveSystem.Load(outFolder, "roundtrip_text", ""));
        var textSame = Same(textLoad.Value, fresh, fingerprint, history);
        if (!textSame) failures++;
        Console.WriteLine($"ROUNDTRIP text: save {textSave.Ms:N0} ms (peak +{textSave.PeakMb:N0} MB), load {textLoad.Ms:N0} ms " +
                          $"(peak +{textLoad.PeakMb:N0} MB), {textSize:N0} bytes, {(textSame ? "SAME" : "DIFFERENT " + textLoad.Value.Warning)}");
        textLoad = default;

        // Every older binary shape, made from the text save's bytes the way each was written.
        var plain = File.ReadAllBytes(textPath);
        var older = new List<(string Name, string Extension, Action<Stream> Write)>
        {
            ("legacy_bdf2_q4", SaveSystem.LegacyExtension, s => { s.Write("BDF2"u8); using var b = new BrotliStream(s, CompressionLevel.Optimal, true); b.Write(plain); }),
            ("legacy_gzip", SaveSystem.PreviousExtension, s => { using var g = new GZipStream(s, CompressionLevel.Fastest, true); g.Write(plain); }),
        };
        foreach (var (name, ext, write) in older)
        {
            var legacyPath = Path.Combine(outFolder, name + ext);
            using (var file = File.Create(legacyPath)) write(file);
            Collect();
            var load = Measured(() => SaveSystem.Load(outFolder, name, ""));
            var same = Same(load.Value, fresh, fingerprint, history);
            if (!same) failures++;
            Console.WriteLine($"ROUNDTRIP {name}: load {load.Ms:N0} ms (peak +{load.PeakMb:N0} MB), " +
                              $"{new FileInfo(legacyPath).Length:N0} bytes, {(same ? "SAME" : "DIFFERENT " + load.Value.Warning)}");
        }
        plain = null;

        Console.WriteLine($"ROUNDTRIP summary over {prepares.Count} round(s): prepare median {Median(prepares):N0} ms, " +
                          $"commit median {Median(commits):N0} / max {Max(commits):N0} ms (peak median +{Median(commitPeaks):N0} / max +{Max(commitPeaks):N0} MB), " +
                          $"load median {Median(loads):N0} / max {Max(loads):N0} ms (peak median +{Median(loadPeaks):N0} / max +{Max(loadPeaks):N0} MB), " +
                          $"binary {size:N0} bytes {magic}, text {textSize:N0} bytes; failures {failures}");
        Console.WriteLine(failures == 0 ? "ROUNDTRIP PASS" : "ROUNDTRIP FAIL");
        return failures == 0 ? 0 : 1;
    }

    private static bool Same(SaveLoadResult loaded, Campaign fresh, ulong fingerprint, string history)
    {
        if (!loaded.Success) return false;
        loaded.Campaign.AttachProvinceTopology(fresh.ProvinceMap);
        return CampaignFingerprint.Of(loaded.Campaign) == fingerprint && HistoryHash(loaded.Campaign) == history;
    }

    private static string Magic(string file)
    {
        var bytes = new byte[4];
        using var stream = File.OpenRead(file);
        stream.ReadExactly(bytes);
        return bytes[0] == (byte)'{' ? "text" : bytes[0] == 0x1f ? "gzip" : System.Text.Encoding.ASCII.GetString(bytes);
    }

    private readonly record struct Timed<T>(T Value, double Ms, double PeakMb);

    /// <summary>
    /// Time and the process's peak working set above where it started, polled every
    /// millisecond: the whole process, because a compressor's buffers and the native decoder
    /// live outside the managed heap and a phone runs out of both alike.
    /// </summary>
    private static Timed<T> Measured<T>(Func<T> work)
    {
        var baseline = Environment.WorkingSet;
        var peak = baseline;
        var running = true;
        var poller = new System.Threading.Thread(() =>
        {
            while (System.Threading.Volatile.Read(ref running))
            {
                var now = Environment.WorkingSet;
                if (now > peak) peak = now;
                System.Threading.Thread.Sleep(1);
            }
        }) { IsBackground = true };
        poller.Start();
        var watch = Stopwatch.StartNew();
        var value = work();
        var ms = watch.Elapsed.TotalMilliseconds;
        System.Threading.Volatile.Write(ref running, false);
        poller.Join();
        var end = Environment.WorkingSet;
        if (end > peak) peak = end;
        return new Timed<T>(value, ms, (peak - baseline) / 1048576.0);
    }

    private static void Collect()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    /// <summary>The history table as numbers, in a fixed order. CampaignFingerprint leaves the
    /// ledger out (it is a record of the world, not the world), so a format that moved the
    /// ledger into its own section has to be judged on it separately.</summary>
    internal static string HistoryHash(Campaign campaign)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> row = stackalloc byte[22 * 4];
        var table = campaign.History.Export();
        long rows = 0;
        foreach (var key in table.Keys.OrderBy(key => key))
        {
            BitConverter.TryWriteBytes(row, key);
            hash.AppendData(row[..4]);
            foreach (var s in table[key])
            {
                var values = new[]
                {
                    s.Gdp, s.Treasury, s.PopulationMillions, s.WeeklyIncome, s.WeeklyExpenses, s.WeeklyBalance,
                    s.Productivity, s.StandardOfLiving, s.Literacy, s.Stability, s.MarketAccess, s.ShortageIndex,
                    s.TradeBalance, s.MilitaryPower, s.ManpowerMillions, s.WarSupport, s.FoodPriceIndex,
                    s.ConsumerPriceIndex, s.IndustrialPriceIndex, s.EnergyPriceIndex, s.ArmsPriceIndex
                };
                BitConverter.TryWriteBytes(row, s.Day);
                for (var i = 0; i < values.Length; i++) BitConverter.TryWriteBytes(row[((i + 1) * 4)..], values[i]);
                hash.AppendData(row);
                rows++;
            }
        }
        return $"{Convert.ToHexString(hash.GetHashAndReset())[..16]}/{rows}";
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static double Max(List<double> values) => values.Count == 0 ? 0 : values.Max();
}
