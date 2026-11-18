using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DealOrForce.Simulation;

/// <summary>
/// The framed binary save ("BDF3", SaveContainer) of 18 November 2026: every older save still
/// loads as the same world, the new one comes back as it went - history to the bit - a damaged
/// one is refused rather than loaded wrong, and the history table reads files with fewer or
/// more columns than this version writes.
/// </summary>
static class SaveContainerTests
{
    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(61117);
        var state = campaign.States.First(s => s.NationMapIndex == campaign.Player.MapIndex);
        campaign.AttachProvinceTopology(ProvinceTopology.Build(
            Enumerable.Repeat((ushort)state.MapIndex, 512).ToArray(),
            Enumerable.Repeat((ushort)campaign.Player.MapIndex, 512).ToArray(), 32, 16, _ => false));
        campaign.Armies.Clear();
        foreach (var nation in campaign.Nations) TacticalDoctrines.EnsureShelves(nation);
        // A history with awkward numbers in every column: negative zero, subnormals, the
        // largest float, values JSON prints with seventeen digits. The binary table must give
        // back the same bits, and the JSON one the same values.
        campaign.History.Restore(SyntheticHistory(campaign), campaign.Defines);
        var folder = Path.Combine(Path.GetTempPath(), "dof-save-container-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        campaign.Defines.SaveTextFormat = false;
        var saved = SaveSystem.Save(folder, "framed", campaign, "tests");
        var framed = File.ReadAllBytes(Path.Combine(folder, "framed.bdfsave"));
        // Reference values come from the world as it loads, not as it stood: Restore normalises
        // a few derived fields, and the claim is that every container restores the same world.
        var reference = SaveSystem.Load(folder, "framed", "tests");
        Check(reference.Success, "a framed save loads: " + reference.Warning);
        var fingerprint = CampaignFingerprint.Of(reference.Campaign);
        var history = HistoryDigest(reference.Campaign);
        Check(HistoryDigest(campaign) == history, "the framed save brings back the history bit for bit");
        Check(Encoding.ASCII.GetString(framed, 0, 4) == "BDF3", "a binary save is written framed (BDF3)");
        using (var header = new MemoryStream(framed))
        {
            var h = SaveContainer.ReadHeader(header);
            Check(h.Version == SaveContainer.ContainerVersion && h.FormatVersion == SaveSystem.FormatVersion &&
                  h.Sections.Length == 2 && h.Sections.All(s => s.Codec == SaveContainer.BrotliCodec && s.Level == 1) &&
                  h.Find(SaveContainer.GraphSection) is not null && h.Find(SaveContainer.HistorySection) is not null,
                "the header names the container version, format version, two Brotli quality-1 sections");
            Check(h.Sections.Sum(s => s.StoredLength) + h.Length == framed.Length, "section lengths add up to the file");
        }
        Check(saved.Checksum.Length == 64 && saved.CompressedBytes == framed.Length, "the save reports its checksum and size");

        // Every older generation of the same world, made from the text save the way each one
        // was written: plain JSON, with a byte-order mark, gzip, and Brotli under "BDF2".
        campaign.Defines.SaveTextFormat = true;
        SaveSystem.Save(folder, "text", campaign, "tests");
        var plain = File.ReadAllBytes(Path.Combine(folder, "text.bdfsave"));
        Check(plain[0] == (byte)'{', "the text save is still the editable JSON the setting chose");
        var variants = new Dictionary<string, byte[]>
        {
            ["text"] = plain,
            ["bom"] = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(plain).ToArray(),
        };
        using (var output = new MemoryStream())
        {
            using (var zip = new GZipStream(output, CompressionLevel.Fastest, true)) zip.Write(plain);
            variants["gzip"] = output.ToArray();
        }
        foreach (var level in new[] { CompressionLevel.Optimal, CompressionLevel.Fastest })
            using (var output = new MemoryStream())
            {
                output.Write("BDF2"u8);
                using (var zip = new BrotliStream(output, level, true)) zip.Write(plain);
                variants["bdf2_" + level.ToString().ToLowerInvariant()] = output.ToArray();
            }
        foreach (var (name, bytes) in variants)
        {
            File.WriteAllBytes(Path.Combine(folder, name + ".bdfsave"), bytes);
            var loaded = SaveSystem.Load(folder, name, "tests");
            Check(loaded.Success, $"an older save ({name}) still loads: {loaded.Warning}");
            Check(CampaignFingerprint.Of(loaded.Campaign) == fingerprint && HistoryDigest(loaded.Campaign) == history,
                $"an older save ({name}) restores the same world and the same history as the framed one");
        }
        // The pre-2026 sidecar name still loads too.
        File.WriteAllBytes(Path.Combine(folder, "legacy.bdfsave.json"), plain);
        var legacy = SaveSystem.Load(folder, "legacy", "tests");
        Check(legacy.Success && CampaignFingerprint.Of(legacy.Campaign) == fingerprint, "a legacy .bdfsave.json still loads");

        // The slot list reads the framed file's embedded metadata from its first section.
        var listed = SaveSystem.List(folder).FirstOrDefault(m => m.Slot == "framed");
        Check(listed is not null && listed.NationTag == campaign.Player.Tag && listed.CompressedBytes == framed.Length,
            "the save list reads a framed slot's metadata");

        // Damage. Each of these must be refused, never loaded as some other world.
        Refused(folder, "truncated", framed.Take(framed.Length - 1).ToArray(), "a framed save missing its last byte");
        Refused(folder, "half", framed.Take(framed.Length / 2).ToArray(), "a framed save cut in half");
        Refused(folder, "header-only", framed.Take(90).ToArray(), "a framed save that is only its header");
        foreach (var at in new[] { 12, 30, 50, 100, framed.Length / 3, framed.Length / 2, framed.Length - 10 })
        {
            var flipped = (byte[])framed.Clone();
            flipped[at] ^= 0x10;
            Refused(folder, "flip" + at, flipped, $"a framed save with one bit flipped at byte {at}");
        }
        var longer = framed.Concat(new byte[] { 0 }).ToArray();
        // A trailing byte is not covered by any section; it is the one change that is harmless,
        // and the reader may load it. It must not load a different world.
        File.WriteAllBytes(Path.Combine(folder, "longer.bdfsave"), longer);
        var longerLoaded = SaveSystem.Load(folder, "longer", "tests");
        Check(!longerLoaded.Success || CampaignFingerprint.Of(longerLoaded.Campaign) == fingerprint,
            "a trailing byte never loads as another world");

        // Stored (uncompressed) sections: nothing but the checksum stands between a flipped bit
        // in a figure and a wrong chart. This is the case the checksum exists for.
        var payload = SaveSystem.CampaignSave.Capture(campaign, "tests");
        using (var stored = new FileStream(Path.Combine(folder, "stored.bdfsave"), FileMode.Create, FileAccess.ReadWrite))
            SaveSystem.WriteBinary(stored, payload, null, SaveContainer.StoredCodec, 0);
        var storedBytes = File.ReadAllBytes(Path.Combine(folder, "stored.bdfsave"));
        var storedLoaded = SaveSystem.Load(folder, "stored", "tests");
        Check(storedLoaded.Success && CampaignFingerprint.Of(storedLoaded.Campaign) == fingerprint &&
              HistoryDigest(storedLoaded.Campaign) == history, "an uncompressed framed save round-trips");
        var figure = (byte[])storedBytes.Clone();
        figure[^5] ^= 0x01;
        Refused(folder, "stored-figure", figure, "an uncompressed save with one figure's last bit changed");

        HistoryColumns();
        Directory.Delete(folder, recursive: true);
        Console.WriteLine("PASS save container: framed save, older saves, damage and history columns");
    }

    /// <summary>A file written before a column was appended reads the new column as zero; a
    /// file with a column this version does not know skips it; a new version is refused.</summary>
    static void HistoryColumns()
    {
        var table = new Dictionary<int, List<NationHistorySample>>
        {
            [3] = new() { Row(7, 1.5f), Row(14, -2.25f) },
            [9] = new(),
            [11] = null,
        };
        Dictionary<int, List<NationHistorySample>> RoundTrip(int columns, ushort? version = null)
        {
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
                SaveHistoryTable.Write(writer, table, columns);
            if (version is { } v) { memory.Position = 0; memory.Write(BitConverter.GetBytes(v)); }
            memory.Position = 0;
            using var reader = new BinaryReader(memory);
            var back = SaveHistoryTable.Read(reader);
            Check(memory.Position == memory.Length, $"a {columns}-column table is read to its end");
            return back;
        }
        var same = RoundTrip(SaveHistoryTable.Columns);
        Check(same[3].SequenceEqual(table[3]) && same[9].Count == 0 && same[11] is null, "the history table round-trips");
        var older = RoundTrip(SaveHistoryTable.Columns - 1);
        Check(older[3].Select(s => s with { ArmsPriceIndex = 0 }).SequenceEqual(table[3].Select(s => s with { ArmsPriceIndex = 0 })) &&
              older[3].All(s => s.ArmsPriceIndex == 0),
            "a file written before the last history column existed loads, the new column reading as zero");
        var oldest = RoundTrip(1);
        Check(oldest[3].Select(s => s.Day).SequenceEqual(new[] { 7, 14 }) && oldest[3].All(s => s.Gdp == 0 && s.ArmsPriceIndex == 0),
            "a file with only the day column loads with every figure zero");
        var newer = RoundTrip(SaveHistoryTable.Columns + 3);
        Check(newer[3].SequenceEqual(table[3]), "a file with columns this version does not know skips them");
        try
        {
            RoundTrip(SaveHistoryTable.Columns, version: SaveHistoryTable.Version + 1);
            Check(false, "a history table of a newer version is refused");
        }
        catch (InvalidDataException) { Check(true, "a history table of a newer version is refused, not misread"); }

        // A damaged row count (the checksum is judged after the read) is refused before it can
        // ask for gigabytes: version u16, columns u16, count i32, key i32, then the rows at 12.
        using (var memory = new MemoryStream())
        {
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
                SaveHistoryTable.Write(writer, table);
            memory.Position = 12;
            memory.Write(BitConverter.GetBytes(int.MaxValue));
            memory.Position = 0;
            try
            {
                SaveHistoryTable.Read(new BinaryReader(memory));
                Check(false, "a history table with a damaged row count is refused");
            }
            catch (InvalidDataException) { Check(true, "a history table with a damaged row count is refused without allocating it"); }
        }
    }

    static NationHistorySample Row(int day, float seed) =>
        new(day, seed, seed * 2, seed * 3, seed * 4, seed * 5, seed * 6, seed * 7, seed * 8, seed * 9, seed * 10,
            seed * 11, seed * 12, seed * 13, seed * 14, seed * 15, seed * 16, seed * 17, seed * 18, seed * 19, seed * 20, seed * 21);

    static Dictionary<int, List<NationHistorySample>> SyntheticHistory(Campaign campaign)
    {
        var random = new Random(18);
        var awkward = new[] { -0f, float.Epsilon, float.MaxValue, -float.MaxValue, 0.1f, 1f / 3f, 123456.789f, 1e-30f };
        var table = new Dictionary<int, List<NationHistorySample>>();
        foreach (var nation in campaign.Nations.Take(40))
        {
            var series = new List<NationHistorySample>();
            for (var week = 0; week < 30; week++)
            {
                float F(int column) => (week + column) % 9 == 0 ? awkward[(week + column) % awkward.Length]
                    : (float)(random.NextDouble() * 2000 - 1000);
                series.Add(new NationHistorySample(week * 7, F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9), F(10),
                    F(11), F(12), F(13), F(14), F(15), F(16), F(17), F(18), F(19), F(20), F(21)));
            }
            table[nation.Id] = series;
        }
        return table;
    }

    /// <summary>The history as its bits, in key order: CampaignFingerprint leaves the ledger out.</summary>
    internal static string HistoryDigest(Campaign campaign)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var table = campaign.History.Export();
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
            SaveHistoryTable.Write(writer, table.OrderBy(e => e.Key).ToDictionary(e => e.Key, e => e.Value));
        return Convert.ToHexString(SHA256.HashData(memory.ToArray()))[..16];
    }

    static void Refused(string folder, string slot, byte[] bytes, string what)
    {
        File.WriteAllBytes(Path.Combine(folder, slot + ".bdfsave"), bytes);
        var loaded = SaveSystem.Load(folder, slot, "tests");
        Check(!loaded.Success, what + " is refused" + (loaded.Success ? "" : $" ({loaded.Warning})"));
    }

    static void Check(bool ok, string note)
    {
        if (!ok) throw new Exception("FAIL save container: " + note);
        Console.WriteLine("PASS save container: " + note);
    }
}
