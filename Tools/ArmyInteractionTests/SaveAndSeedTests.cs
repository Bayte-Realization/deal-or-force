using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

static class SaveAndSeedTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static void Run(Campaign campaign)
    {
        const string pendingEffectJson = "{\"Entries\":[{\"Key\":\"stability\",\"Value\":0.1}],\"Grants\":[{\"Id\":\"test_spirit\",\"Days\":9}],\"Revokes\":[\"old_spirit\"]}";
        var pendingEffect = JsonSerializer.Deserialize<ScriptedEffects.Effect>(pendingEffectJson)!;
        var restoredEffect = JsonSerializer.Deserialize<ScriptedEffects.Effect>(
            JsonSerializer.Serialize(pendingEffect))!;
        Check(restoredEffect.Entries.Count == 1 && restoredEffect.Entries[0].Key == "stability" &&
              Math.Abs(restoredEffect.Entries[0].Value - .1f) < .0001f &&
              restoredEffect.Grants.Count == 1 && restoredEffect.Grants[0].Id == "test_spirit" &&
              restoredEffect.Grants[0].Days == 9 &&
              restoredEffect.Revokes.SequenceEqual(new[] { "old_spirit" }),
            "pending event entries, granted spirits and revoked spirits survive save/load");
        var legacyGrant = JsonSerializer.Deserialize<ScriptedEffects.Effect>(
            "{\"Grants\":[{\"Item1\":\"legacy_spirit\",\"Item2\":12}]}" )!;
        Check(legacyGrant.Grants.Count == 1 && legacyGrant.Grants[0].Id == "legacy_spirit" &&
              legacyGrant.Grants[0].Days == 12, "older tuple-shaped event grants remain readable");

        Check(CampaignSeedSelection.TryParse("2147483647", out var max) && max == int.MaxValue,
            "full positive seed range is accepted");
        Check(!CampaignSeedSelection.TryParse("0", out _) &&
              !CampaignSeedSelection.TryParse("-1", out _) &&
              !CampaignSeedSelection.TryParse("2147483648", out _) &&
              !CampaignSeedSelection.TryParse("12.5", out _), "invalid seeds are rejected");
        var rolled = Enumerable.Range(0, 32).Select(_ => CampaignSeedSelection.Roll()).ToArray();
        Check(rolled.All(seed => seed > 0) && rolled.Distinct().Count() > 1,
            "random seed button can roll valid distinct seeds");

        var folder = Path.Combine(Path.GetTempPath(), "dof-save-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            campaign.Defines.SaveTextFormat = true;
            var metadata = SaveSystem.Save(folder, "editable", campaign, "test-mod", "Editable fixture");
            var textPath = Path.Combine(folder, "editable.bdfsave");
            Check(File.Exists(textPath) && File.ReadAllText(textPath).TrimStart().StartsWith('{') &&
                  metadata.Checksum == "", "default .bdfsave is editable JSON without an edit-blocking checksum");
            Check(Directory.GetFiles(folder).Length == 1, "new text save is one file with its own metadata");
            Check(metadata.Seed == campaign.Seed && SaveSystem.List(folder).Single().Seed == campaign.Seed,
                "save list metadata exposes the world seed");
            var untouched = SaveSystem.Load(folder, "editable", "test-mod");
            Check(untouched.Success, "untouched text campaign loads");
            untouched.Campaign.AttachProvinceTopology(campaign.ProvinceMap.CloneForOverrides());
            Check(CampaignFingerprint.Of(untouched.Campaign) == CampaignFingerprint.Of(campaign),
                "untouched save preserves the strategic world fingerprint");
            var beforeSnapshot = NetworkJson(campaign);
            var afterSnapshot = NetworkJson(untouched.Campaign);
            // Attaching the restored topology increments its cache-revision counter once.
            beforeSnapshot.AsObject().Remove("MapRevision");
            afterSnapshot.AsObject().Remove("MapRevision");
            var identical = JsonNode.DeepEquals(beforeSnapshot, afterSnapshot);
            Check(identical, "untouched save preserves every authored/active serialised field" +
                (identical ? "" : ": " + FirstDifference(beforeSnapshot, afterSnapshot)));
            SaveSystem.Save(folder, "roundtrip", untouched.Campaign, "test-mod");
            var twice = SaveSystem.Load(folder, "roundtrip", "test-mod");
            Check(twice.Success, "loaded campaign can be saved again");
            twice.Campaign.AttachProvinceTopology(campaign.ProvinceMap.CloneForOverrides());
            var twiceSnapshot = NetworkJson(twice.Campaign);
            twiceSnapshot.AsObject().Remove("MapRevision");
            Check(JsonNode.DeepEquals(beforeSnapshot, twiceSnapshot),
                "repeated save/load does not discard serialised campaign state");

            var savedText = File.ReadAllText(textPath);
            Check(savedText.Split('\n').Any(line => line.Contains("\"ProvinceOwner\": [") && line.TrimEnd().EndsWith("],")),
                "large province arrays stay compact but remain editable JSON numbers");

            var document = JsonNode.Parse(File.ReadAllText(textPath))!;
            var nations = document["Nations"]!.AsArray();
            var player = nations.Single(node => (int)node!["Id"]! == campaign.Player.Id)!;
            player["Treasury"] = 123456789f;
            var expanded = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine($"SIZE: compact-readable={new FileInfo(textPath).Length:N0} bytes, " +
                              $"fully-expanded={Encoding.UTF8.GetByteCount(expanded):N0} bytes");
            Check(new FileInfo(textPath).Length < Encoding.UTF8.GetByteCount(expanded) * .7,
                "one-row-per-population-group reduces readable save size substantially");
            File.WriteAllText(textPath, expanded);
            var loaded = SaveSystem.Load(folder, "editable", "test-mod");
            Check(loaded.Success, "hand-edited JSON campaign loads: " + loaded.Warning);
            Check(loaded.Campaign.Seed == campaign.Seed &&
                  Math.Abs(loaded.Campaign.Player.Treasury - 123456789f) < 1f,
                "hand-edited treasury and original seed survive restore");

            var header = document["EmbeddedMetadata"]!.DeepClone();
            document.AsObject().Remove("EmbeddedMetadata");
            document.AsObject().Add("EmbeddedMetadata", header);
            File.WriteAllText(textPath, document.ToJsonString());
            Check(SaveSystem.List(folder).Any(item => item.Slot == "editable"),
                "save remains visible after an editor reorders JSON properties");

            // The first one-file release used .bdfsave.json for its readable payload.
            // It must remain loadable even though new writes use .bdfsave exclusively.
            var previousText = Path.Combine(folder, "previous_text.bdfsave.json");
            File.WriteAllText(previousText, savedText);
            Check(SaveSystem.List(folder).Any(item => item.Slot == "previous_text") &&
                  SaveSystem.Load(folder, "previous_text", "test-mod").Success,
                "previous one-file .bdfsave.json is still listed and loadable");
            SaveSystem.Save(folder, "previous_text", campaign, "test-mod");
            Check(File.Exists(Path.Combine(folder, "previous_text.bdfsave")) &&
                  !File.Exists(previousText) &&
                  Directory.GetFiles(folder, "previous_text.*").Length == 1,
                "rewriting a previous one-file save adopts the single .bdfsave extension");

            // Files written before the embedded-header change had a separate .meta.json.
            // Build that exact layout to guarantee old campaigns remain discoverable.
            document.AsObject().Remove("EmbeddedMetadata");
            document.AsObject().Remove("ProductionUnitsSaved");
            var oldJson = document.ToJsonString();
            File.WriteAllText(Path.Combine(folder, "old_text.bdfsave.json"), oldJson);
            metadata.Slot = "old_text";
            File.WriteAllText(Path.Combine(folder, "old_text.meta.json"),
                JsonSerializer.Serialize(metadata));
            Check(SaveSystem.List(folder).Any(item => item.Slot == "old_text") &&
                  SaveSystem.Load(folder, "old_text", "test-mod").Success,
                "legacy text save with sidecar still lists and loads");
            SaveSystem.Save(folder, "old_text", campaign, "test-mod");
            Check(File.Exists(Path.Combine(folder, "old_text.bdfsave")) &&
                  !File.Exists(Path.Combine(folder, "old_text.bdfsave.json")) &&
                  !File.Exists(Path.Combine(folder, "old_text.meta.json")) &&
                  SaveSystem.Load(folder, "old_text", "test-mod").Success,
                "overwriting a legacy slot migrates it to one file safely");

            var oldCompressed = Path.Combine(folder, "old_gzip.bdfsave");
            using (var file = File.Create(oldCompressed))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new StreamWriter(gzip)) writer.Write(oldJson);
            metadata.Slot = "old_gzip";
            metadata.Checksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(oldCompressed)));
            File.WriteAllText(Path.Combine(folder, "old_gzip.meta.json"),
                JsonSerializer.Serialize(metadata));
            Check(SaveSystem.List(folder).Any(item => item.Slot == "old_gzip") &&
                  SaveSystem.Load(folder, "old_gzip", "test-mod").Success,
                "legacy compressed save with checksum still lists and loads");

            campaign.Defines.SaveTextFormat = false;
            var compressed = SaveSystem.Save(folder, "legacy", campaign, "test-mod");
            Check(File.Exists(Path.Combine(folder, "legacy.bdfsave")) &&
                  !File.Exists(Path.Combine(folder, "legacy.meta.json")) &&
                  SaveSystem.List(folder).Any(item => item.Slot == "legacy" && item.Seed == campaign.Seed),
                "optional compressed format is also one file and is listed correctly");
            Check(SaveSystem.Load(folder, "legacy", "test-mod").Success,
                "new compressed save remains loadable");
            campaign.Defines.SaveTextFormat = true;
            SaveSystem.Save(folder, "format_switch", campaign, "test-mod");
            campaign.Defines.SaveTextFormat = false;
            SaveSystem.Save(folder, "format_switch", campaign, "test-mod");
            Check(Directory.GetFiles(folder, "format_switch.*").Length == 1 &&
                  SaveSystem.Load(folder, "format_switch", "test-mod").Success,
                "changing format keeps one .bdfsave without losing the campaign");
            campaign.Defines.SaveTextFormat = true;
            SaveSystem.Save(folder, "format_switch", campaign, "test-mod");
            Check(Directory.GetFiles(folder, "format_switch.*").Length == 1 &&
                  File.ReadAllText(Path.Combine(folder, "format_switch.bdfsave")).TrimStart().StartsWith('{') &&
                  SaveSystem.Load(folder, "format_switch", "test-mod").Success,
                "switching back makes the same .bdfsave editable again");
            File.WriteAllText(Path.Combine(folder, "legacy.meta.json"),
                JsonSerializer.Serialize(new SaveMetadata { Slot = "legacy", Checksum = "STALE" }));
            Check(SaveSystem.Load(folder, "legacy", "test-mod").Success,
                "a stale legacy sidecar cannot invalidate a completed one-file save");
            var staleText = Path.Combine(folder, "legacy.bdfsave.json");
            File.WriteAllText(staleText, expanded);
            File.SetLastWriteTimeUtc(staleText, DateTime.UtcNow.AddDays(-2));
            Check(Math.Abs(SaveSystem.Load(folder, "legacy", "test-mod").Campaign.Player.Treasury -
                  campaign.Player.Treasury) < 1f,
                "an interrupted format switch loads the newer complete file");
        }
        finally
        {
            // This exact directory was made by this test with a unique name.
            Directory.Delete(folder, recursive: true);
        }
    }

    private static JsonNode NetworkJson(Campaign campaign)
    {
        var compressed = SaveSystem.ExportNetworkSnapshot(campaign, "test-mod");
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonNode.Parse(gzip)!;
    }

    private static string FirstDifference(JsonNode left, JsonNode right, string path = "$")
    {
        if (JsonNode.DeepEquals(left, right)) return "none";
        if (left is JsonObject a && right is JsonObject b)
        {
            foreach (var key in a.Select(pair => pair.Key).Union(b.Select(pair => pair.Key)))
            {
                if (!a.ContainsKey(key) || !b.ContainsKey(key)) return path + "." + key + " missing";
                if (!JsonNode.DeepEquals(a[key], b[key])) return FirstDifference(a[key], b[key], path + "." + key);
            }
        }
        if (left is JsonArray x && right is JsonArray y)
        {
            if (x.Count != y.Count) return path + $" count {x.Count} != {y.Count}";
            for (var i = 0; i < x.Count; i++)
                if (!JsonNode.DeepEquals(x[i], y[i])) return FirstDifference(x[i], y[i], path + $"[{i}]");
        }
        return path + " " + left?.ToJsonString()[..Math.Min(80, left.ToJsonString().Length)] +
               " != " + right?.ToJsonString()[..Math.Min(80, right.ToJsonString().Length)];
    }
}
