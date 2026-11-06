using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>Read-only diagnostics of a player's save. Never rewrites the input slot.</summary>
static class SaveAudit
{
    public static void Run(string[] args)
    {
        var at = Array.IndexOf(args, "--inspect-save");
        var path = Path.GetFullPath(args[at + 1]);
        var originalDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var clock = Stopwatch.StartNew();
        using (var file = File.OpenRead(path))
        {
            var magic = new byte[4]; file.ReadExactly(magic); file.Position = 0;
            using Stream source = Encoding.ASCII.GetString(magic) == "BDF2"
                ? Brotli(file) : magic[0] == 0x1f && magic[1] == 0x8b
                    ? new GZipStream(file, CompressionMode.Decompress) : file;
            using var json = JsonDocument.Parse(source);
            Console.WriteLine($"SAVE_AUDIT bytes={file.Length} format={(source == file ? "editable-json" : "compressed")} parse_ms={clock.ElapsedMilliseconds}");
            foreach (var item in json.RootElement.EnumerateObject()
                         .Select(p => (p.Name, Bytes: Encoding.UTF8.GetByteCount(p.Value.GetRawText())))
                         .OrderByDescending(p => p.Bytes).Take(12))
                Console.WriteLine($"SAVE_SECTION {item.Name} bytes={item.Bytes}");
        }
        _ = Campaign.CreateBootstrap(947986505);
        clock.Restart();
        var loaded = SaveSystem.Load(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path), "");
        if (!loaded.Success) throw new Exception(loaded.Warning);
        var c = loaded.Campaign;
        Console.WriteLine($"SAVE_LOADED ms={clock.ElapsedMilliseconds} regions={c.States.Count} day={c.ElapsedDays}");
        Print(c, "before-map");
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var topology, out var why)) throw new Exception(why);
        clock.Restart(); c.AttachProvinceTopology(topology);
        Console.WriteLine($"SAVE_ATTACHED ms={clock.ElapsedMilliseconds} regions={c.States.Count}");
        Print(c, "after-map");
        var snapshot = CampaignFingerprint.Of(c);
        var merged = StatePartition.Reunify(c);
        Console.WriteLine($"SAVE_REUNIFY merged={merged} idempotent={snapshot == CampaignFingerprint.Of(c)}");
        if (args.Contains("--assert-orientale"))
        {
            if (c.States.Any(s => s.MapIndex == 4030) || c.ProvinceMap.ProvinceToState[33855] != 194 || merged != 0)
                throw new Exception("Reported Orientale shard did not rejoin its original region.");
            Console.WriteLine("PASS player save: province 33855 joins region 194 on load; foreign provinces retain their flag");
        }
        if (args.Contains("--compress-copy"))
        {
            var folder = Path.Combine(Path.GetTempPath(), "dof-save-roundtrip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            // Compare the supported compact container, not a lossy sample or a pruned history.
            // This changes only the isolated loaded copy, never the player's preference/file.
            c.Defines.SaveTextFormat = false;
            clock.Restart(); var prepared = SaveSystem.Prepare(folder, "compressed", c, "");
            var prepareMs = clock.ElapsedMilliseconds;
            var expected = CampaignFingerprint.Of(c);
            clock.Restart(); prepared.Commit(); var commitMs = clock.ElapsedMilliseconds;
            var written = Path.Combine(folder, "compressed.bdfsave");
            clock.Restart(); var copy = SaveSystem.Load(folder, "compressed", "");
            if (!copy.Success) throw new Exception(copy.Warning);
            copy.Campaign.AttachProvinceTopology(topology);
            var equal = expected == CampaignFingerprint.Of(copy.Campaign);
            Console.WriteLine($"SAVE_COMPACT bytes={new FileInfo(written).Length} prepare_ms={prepareMs} worker_ms={commitMs} load_attach_ms={clock.ElapsedMilliseconds} fingerprint_equal={equal} path={written}");
            if (!equal) throw new Exception("Compact save round-trip changed campaign state.");
        }
        if (originalDigest != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            throw new Exception("Input save changed during read-only audit.");
        Console.WriteLine("PASS player save: source SHA256 unchanged");
    }

    static Stream Brotli(FileStream file)
    { file.Position = 4; return new BrotliStream(file, CompressionMode.Decompress); }

    static void Print(Campaign c, string phase)
    {
        var origin = c.States.FirstOrDefault(s => s.Name == "Orientale");
        foreach (var s in c.States.Where(s => s == origin || s.Name.Contains("Orientale", StringComparison.OrdinalIgnoreCase)))
        {
            var ground = c.ProvinceMap is null ? "not-attached" : string.Join(",", Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
                .Where(p => c.ProvinceMap.ProvinceToState[p] == s.MapIndex)
                .GroupBy(p => c.ProvinceMap.ProvinceToNation[p]).Select(g => $"{g.Key}:{g.Count()}"));
            Console.WriteLine($"SAVE_REGION {phase} map={s.MapIndex} parent={s.ParentMapIndex} owner={s.NationMapIndex} name={s.Name} population={s.Population} ground={ground}");
        }
    }
}
