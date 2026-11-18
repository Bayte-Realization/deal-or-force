using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using DealOrForce.Simulation;
using MemoryPack;
using MessagePack;

namespace DealOrForce.Tools;

/// <summary>
/// The one part of a save every candidate can write without a single annotation: the history
/// table, a dictionary of lists of a plain numeric record struct. MemoryPack cannot touch the
/// rest of the payload as it stands (see TypeSurvey), so this is where the three are compared
/// on equal terms - and it is the largest single block of a long campaign.
/// </summary>
public static class HistoryBench
{
    public static int Run(string savePath, int samples, int warmup)
    {
        _ = Campaign.CreateBootstrap(947986505);
        var loaded = SaveSystem.Load(Path.GetDirectoryName(savePath)!, Path.GetFileNameWithoutExtension(savePath), "");
        if (loaded.Campaign is null) { Console.WriteLine("LOAD_FAILED " + loaded.Warning); return 1; }
        var payload = SaveSystem.CampaignSave.Capture(loaded.Campaign, "", includeRelations: false);
        var history = payload.History;
        loaded = default; payload = null;
        Program.FullCollect();
        var rows = history.Values.Sum(l => l?.Count ?? 0);
        Console.WriteLine($"HISTORY countries={history.Count} rows={rows} heap_mb={GC.GetTotalMemory(false) / 1048576}");

        var reference = Digest(history);
        var mpack = Formats.MsgPackPlain;
        var cases = new (string Name, Func<Dictionary<int, List<NationHistorySample>>, byte[]> Encode, Func<byte[], Dictionary<int, List<NationHistorySample>>> Decode)[]
        {
            ("history-json", h => JsonSerializer.SerializeToUtf8Bytes(h, Formats.Compact),
                b => JsonSerializer.Deserialize<Dictionary<int, List<NationHistorySample>>>(b, Formats.Compact)),
            ("history-mpack", h => MessagePackSerializer.Serialize(h, mpack),
                b => MessagePackSerializer.Deserialize<Dictionary<int, List<NationHistorySample>>>(b, mpack)),
            // What [MessagePackObject] with [Key(0)]..[Key(21)] on the record would write: an
            // array of 22 numbers, no names. Hand-written here because the game type cannot be
            // annotated in this pass; the source generator would emit the same calls.
            ("history-mpack-keys", h => MessagePackSerializer.Serialize(h, Formats.MsgPackHistoryKeyed),
                b => MessagePackSerializer.Deserialize<Dictionary<int, List<NationHistorySample>>>(b, Formats.MsgPackHistoryKeyed)),
            ("history-memorypack", h => MemoryPackSerializer.Serialize(h),
                b => MemoryPackSerializer.Deserialize<Dictionary<int, List<NationHistorySample>>>(b)),
            // The game's own table since 18 November (SaveHistoryTable): BinaryWriter by hand,
            // a version and a column count, no library.
            ("history-binarywriter", h =>
            {
                using var memory = new MemoryStream();
                using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, leaveOpen: true))
                    SaveHistoryTable.Write(writer, h);
                return memory.ToArray();
            }, b =>
            {
                using var reader = new BinaryReader(new MemoryStream(b));
                return SaveHistoryTable.Read(reader);
            }),
        };
        foreach (var c in cases)
        {
            byte[] bytes = null;
            var enc = new List<(double ms, double alloc)>();
            var dec = new List<(double ms, double alloc)>();
            long check = 0;
            for (var i = 0; i < warmup + samples; i++)
            {
                Program.FullCollect();
                var a0 = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                bytes = c.Encode(history);
                sw.Stop();
                if (i >= warmup) enc.Add((sw.Elapsed.TotalMilliseconds, (GC.GetTotalAllocatedBytes(true) - a0) / 1048576.0));
            }
            for (var i = 0; i < warmup + samples; i++)
            {
                Program.FullCollect();
                var a0 = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                var back = c.Decode(bytes);
                sw.Stop();
                check += back.Count;
                if (i >= warmup) dec.Add((sw.Elapsed.TotalMilliseconds, (GC.GetTotalAllocatedBytes(true) - a0) / 1048576.0));
            }
            var same = Digest(c.Decode(bytes)).AsSpan().SequenceEqual(reference);
            var zstd = new PooledBuffer(bytes.Length);
            Formats.Zstd(3)(bytes, zstd);
            Console.WriteLine($"HRESULT|{c.Name}|{bytes.Length}|{zstd.Length}|{Med(enc.Select(e => e.ms))}|{P95(enc.Select(e => e.ms))}|{Med(enc.Select(e => e.alloc))}|" +
                              $"{Med(dec.Select(e => e.ms))}|{P95(dec.Select(e => e.ms))}|{Med(dec.Select(e => e.alloc))}|{same}|{check}");
            zstd.Dispose();
        }
        return 0;
    }

    private static byte[] Digest(Dictionary<int, List<NationHistorySample>> history) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(history, Formats.Compact));

    private static string Med(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToArray();
        return (s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2).ToString("0.0");
    }

    private static string P95(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToArray();
        return s[Math.Clamp((int)Math.Ceiling(0.95 * s.Length) - 1, 0, s.Length - 1)].ToString("0.0");
    }
}
