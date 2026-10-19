using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// The least real time between two autosaves, in seconds, whatever the calendar says.
    ///
    /// An autosave came due every thirty campaign days, and at the top speed thirty days is a
    /// second and a half. Thirty-six years in, a save costs a third of a second on the frame
    /// and half a gigabyte of garbage, so the fastest speed was a stutter every second and a
    /// half - the single largest stall in the running game. An autosave is insurance against
    /// losing real time, so it is paced by real time: at the slow speeds the month still
    /// decides, at the fast ones this does.
    /// </summary>
    public float AutosaveMinimumSeconds { get; set; } = 120f;
}

public sealed class SaveMetadata
{
    public int FormatVersion { get; set; } = SaveSystem.FormatVersion;
    public string Slot { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string NationTag { get; set; } = "";
    public string NationName { get; set; } = "";
    public string GameDate { get; set; } = "";
    public int ElapsedDays { get; set; }
    public int Seed { get; set; }
    public DateTime SavedUtc { get; set; }
    public string GameVersion { get; set; } = SaveSystem.GameVersion;
    public string MapDataVersion { get; set; } = SaveSystem.MapDataVersion;
    public int ProvinceCount { get; set; }
    public string ModFingerprint { get; set; } = "";
    public string Checksum { get; set; } = "";
    public long CompressedBytes { get; set; }
    public bool IsAutosave { get; set; }
}

public readonly record struct SaveLoadResult(Campaign Campaign, SaveMetadata Metadata, string Warning)
{
    public bool Success => Campaign is not null;
}

/// <summary>
/// Versioned and atomic campaign persistence (readable JSON by default). Saves contain simulation
/// state only: presentation can change without invalidating a campaign. New files carry
/// their own index at the front; legacy sidecars are still understood on load.
/// </summary>
public static class SaveSystem
{
    public const int FormatVersion = 2;
    public const string GameVersion = "0.4-migration";
    // Bumped whenever provinces.png is recut: a save's province ids mean nothing on another cut.
    public const string MapDataVersion = "2024.10.06-a";
    public const string QuickSlot = "quicksave";
    public const string AutoSlot = "autosave";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // Members still at a fresh object's value are not written (SaveDefaults).
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers = { SaveDefaults.Modify }
        }
    };

    private static readonly JsonSerializerOptions MetadataOptions = new(JsonOptions) { WriteIndented = true };

    // A scenario is also an editable document. Its inspector deliberately protects all
    // stored fields, including zero owners and empty colours; save-size elision belongs
    // to campaign persistence, not to this complete authoring schema.
    private static readonly JsonSerializerOptions ScenarioOptions = new(JsonOptions)
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    /// <summary>The payload with line breaks and indentation: the format a text editor can
    /// be pointed at. The bulk of a long campaign is the history table, and that stays
    /// compact even in a text save - it is numbers, and prose adds no meaning to it.</summary>
    private static readonly JsonSerializerOptions TextOptions = CreateTextOptions();

    private static JsonSerializerOptions CreateTextOptions()
    {
        var options = new JsonSerializerOptions(JsonOptions) { WriteIndented = true };
        // Dense map columns are still ordinary editable JSON arrays. Keeping each one on
        // one line avoids four lines per province (over 150,000 lines on the world map).
        options.Converters.Add(new CompactArrayConverter<ushort>());
        options.Converters.Add(new CompactArrayConverter<float>());
        options.Converters.Add(new CompactArrayConverter<int>());
        // Population groups are already self-describing through their region and the
        // documented ten-column row. Keep one row per line, not ten lines per row.
        options.Converters.Add(new CompactPopulationGroupConverter());
        return options;
    }

    private sealed class CompactPopulationGroupConverter : JsonConverter<PopulationGroup>
    {
        public override PopulationGroup Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<PopulationGroup>(ref reader, JsonOptions);

        public override void Write(Utf8JsonWriter writer, PopulationGroup value, JsonSerializerOptions options) =>
            writer.WriteRawValue(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), skipInputValidation: true);
    }

    private sealed class CompactArrayConverter<T> : JsonConverter<T[]>
    {
        public override T[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<T[]>(ref reader, JsonOptions);

        public override void Write(Utf8JsonWriter writer, T[] value, JsonSerializerOptions options) =>
            writer.WriteRawValue(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), skipInputValidation: true);
    }

    /// <summary>Independent, readable scenario; no contemporary bootstrap is used on import.</summary>
    public static string ExportScenarioJson(Campaign campaign, string scenarioId)
    {
        if (campaign.ProvinceMap is null) throw new InvalidOperationException("Wait for the map to finish loading.");
        var payload = CampaignSave.Capture(campaign, "");
        payload.ScenarioId = scenarioId;
        payload.ScenarioRules = campaign.Defines;
        // Keep the clock origin: orders, cooldowns and event dates refer to elapsed days.
        payload.IsPaused = true;
        payload.History.Clear();
        payload.Chronicle.Clear();
        payload.ScenarioTerritories = campaign.Nations.ToDictionary(n => n.Tag, n =>
            Enumerable.Range(1, campaign.ProvinceMap.ProvinceCount).Where(p => payload.ProvinceOwner[p] == n.MapIndex).ToArray());
        return JsonSerializer.Serialize(payload, ScenarioOptions);
    }

    public static Campaign ImportScenarioJson(string json, string scenarioId, int expectedProvinceCount = 0,
        int? seedOverride = null)
    {
        var payload = JsonSerializer.Deserialize<CampaignSave>(json, JsonOptions) ?? throw new InvalidDataException("Empty scenario.");
        if (payload.FormatVersion != FormatVersion || payload.MapDataVersion != MapDataVersion || payload.ProvinceCount <= 0 ||
            (expectedProvinceCount > 0 && payload.ProvinceCount != expectedProvinceCount) ||
            payload.ProvinceOwner.Length != payload.ProvinceCount + 1 ||
            payload.ProvinceState.Length != payload.ProvinceCount + 1 ||
            payload.ProvinceController.Length != payload.ProvinceCount + 1 ||
            payload.ProvinceOccupation.Length != payload.ProvinceCount + 1)
            throw new InvalidDataException("Scenario requires a complete political layer for the current map.");
        _ = new DateTime(payload.Date.Year, payload.Date.Month, payload.Date.Day);
        if (payload.Nations.Count == 0 || !payload.Nations.Any(n => n.Id == payload.PlayerId) ||
            payload.Nations.Select(n => n.Id).Distinct().Count() != payload.Nations.Count ||
            payload.Nations.Select(n => n.MapIndex).Distinct().Count() != payload.Nations.Count ||
            payload.Nations.Select(n => n.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Nations.Count ||
            payload.Nations.Any(n => n.MapIndex <= 0 || n.MapIndex > ushort.MaxValue))
            throw new InvalidDataException("Invalid country IDs, tags or player in scenario.");
        var owners = payload.Nations.Select(n => n.MapIndex).Append(0).ToHashSet();
        var states = payload.States.Select(s => s.MapIndex).Append(0).ToHashSet();
        if (payload.States.Select(s => s.Id).Distinct().Count() != payload.States.Count ||
            states.Count != payload.States.Count + 1 || payload.States.Any(s => s.MapIndex <= 0 || s.MapIndex > ushort.MaxValue || !owners.Contains(s.NationMapIndex)) ||
            payload.Date.Hour is < 0 or > 23 ||
            payload.Armies.Select(a => a.Id).Distinct().Count() != payload.Armies.Count ||
            payload.Armies.Any(a => !payload.Nations.Any(n => n.Id == a.NationId) || a.ProvinceId < 0 || a.ProvinceId > payload.ProvinceCount) ||
            payload.ScenarioTerritories.Values.Any(ps => ps is null || ps.Any(p => p <= 0 || p > payload.ProvinceCount)))
            throw new InvalidDataException("Invalid state, army, territory or clock references.");
        if (payload.ProvinceOwner.Any(p => !owners.Contains(p)) || payload.ProvinceController.Any(p => !owners.Contains(p)) ||
            payload.ProvinceState.Any(p => !states.Contains(p))) throw new InvalidDataException("Political map references an unknown country or state.");
        // An authored scenario supplies the world, not an irrevocable random stream. The
        // picker can replay that same world with another seed without importing 2024 data.
        if (seedOverride.HasValue)
        {
            if (seedOverride.Value <= 0) throw new InvalidDataException("Scenario seed must be positive.");
            payload.Seed = seedOverride.Value;
            payload.RandomState = new SimRandom(seedOverride.Value).State;
        }
        payload.ScenarioId = scenarioId;
        payload.IsPaused = true;
        return payload.Restore();
    }

    public static SaveMetadata Save(string root, string slot, Campaign campaign, string modFingerprint,
        string displayName = "", bool autosave = false) =>
        Prepare(root, slot, campaign, modFingerprint, displayName, autosave).Commit();

    /// <summary>
    /// The half of saving that reads the campaign, and so has to happen where the campaign is
    /// safe to read: the snapshot and its JSON. Everything after that - optional compression
    /// and writing the one-file save - touches only these bytes and is <see cref="PreparedSave.Commit"/>,
    /// which may run on any thread.
    ///
    /// Split because the second half was most of the cost. A five-year save was 2.9 seconds on
    /// the frame that took it, 2.1 of them in the compressor, and that was the freeze that came
    /// back every thirty days and grew with the campaign.
    /// </summary>
    public static PreparedSave Prepare(string root, string slot, Campaign campaign, string modFingerprint,
        string displayName = "", bool autosave = false)
    {
        // A step the smoothed clock has begun is finished first: a save of half a day would
        // load as a day that never happened.
        campaign?.FinishStep();
        using var timing = SimProfiler.Measure("save.prepare");
        DiagnosticLogs.Write("system", $"SAVE_BEGIN slot={SafeSlot(slot)} autosave={autosave} day={campaign?.ElapsedDays}");
        if (campaign?.Player is null) throw new InvalidOperationException("No active campaign to save.");
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null || campaign.ProvinceOccupation is null)
            throw new InvalidOperationException("The tactical map is still being prepared; try saving again in a moment.");
        slot = SafeSlot(slot);
        // The format is the campaign's own define, so every caller - the save screen, the
        // console, the autosave - writes the same way without each having to be told.
        var text = campaign.Defines.SaveTextFormat;
        var payload = CampaignSave.Capture(campaign, modFingerprint);
        var issues = NumericIntegrity.Inspect(payload);
        if (campaign.SimulationFault.Length > 0) issues.Insert(0, campaign.SimulationFault);
        if (issues.Count > 0)
        {
            campaign.IsPaused = true;
            var diagnostic = TryWriteDiagnostic(root, payload, issues);
            throw new CampaignIntegrityException(string.Join("; ", issues), diagnostic);
        }
        var metadata = new SaveMetadata
        {
            Slot = slot,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? SlotTitle(slot) : displayName.Trim(),
            NationTag = campaign.Player.Tag,
            NationName = campaign.Player.Name,
            GameDate = campaign.Date.ToString(),
            ElapsedDays = campaign.ElapsedDays,
            Seed = campaign.Seed,
            SavedUtc = DateTime.UtcNow,
            MapDataVersion = MapDataVersion,
            ProvinceCount = payload.ProvinceCount,
            ModFingerprint = modFingerprint ?? "",
            IsAutosave = autosave
        };
        payload.EmbeddedMetadata = metadata;
        // The history is written by the worker. It is half of every save, and it is the one
        // part of the snapshot that already belongs to the save alone: Capture copies it,
        // week by week, into lists nothing else holds, so nothing can change it while it is
        // written. Its field comes last in the file, so here it is written as an empty table
        // at the very end, and Commit writes the real one in its place.
        var history = payload.History;
        payload.History = new Dictionary<int, List<NationHistorySample>>();
        var options = text ? TextOptions : JsonOptions;
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, options);
        if (!json.AsSpan().EndsWith(text ? TextHistoryTail : EmptyHistoryTail))
        {
            // Not where it should be - somebody has reordered the save. Write it here, as it
            // always was, rather than splice it into the wrong place.
            payload.History = history;
            history = null;
            json = JsonSerializer.SerializeToUtf8Bytes(payload, options);
        }
        return new PreparedSave(root, slot, json, history, metadata, text);
    }

    /// <summary>Never a loadable save and never an overwrite of the last good slot.</summary>
    public static string WriteDiagnosticSnapshot(string root, Campaign campaign, string issue)
    {
        if (campaign?.ProvinceMap is null) return "";
        return TryWriteDiagnostic(root, CampaignSave.Capture(campaign, ""), new List<string> { issue });
    }

    private static string TryWriteDiagnostic(string root, CampaignSave payload, List<string> issues)
    {
        try
        {
            var folder = Path.Combine(Path.GetFullPath(root), "diagnostics");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"campaign_{payload.ElapsedDays}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.diagnostic.json");
            var options = new JsonSerializerOptions(JsonOptions)
            { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, new
            {
                Kind = "diagnostic-only; not a playable save",
                SavedUtc = DateTime.UtcNow,
                Issues = issues,
                Campaign = payload
            }, options);
            DiagnosticLogs.Write("errors", $"CAMPAIGN_DIAGNOSTIC path={path} issues={string.Join(';', issues)}");
            return path;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DiagnosticLogs.Write("errors", "CAMPAIGN_DIAGNOSTIC_FAILED " + exception.Message);
            return "";
        }
    }

    /// <summary>How the frame's half of a save ends: the history's field, empty, closing the file.</summary>
    private static ReadOnlySpan<byte> EmptyHistoryTail => "\"History\":{}}"u8;

    /// <summary>The same place in the readable format: the field on its own line, and the
    /// file's closing brace on the one after it.</summary>
    private static ReadOnlySpan<byte> TextHistoryTail => "\"History\": {}\n}"u8;

    /// <summary>A save that has been taken but not yet written. See <see cref="Prepare"/>.</summary>
    public sealed class PreparedSave
    {
        private readonly string _root;
        private readonly string _slot;
        private readonly byte[] _json;
        private readonly Dictionary<int, List<NationHistorySample>> _history;

        internal PreparedSave(string root, string slot, byte[] json,
            Dictionary<int, List<NationHistorySample>> history, SaveMetadata metadata, bool text)
        {
            _root = root;
            _slot = slot;
            _json = json;
            _history = history;
            _text = text;
            Metadata = metadata;
        }

        private readonly bool _text;

        /// <summary>
        /// The file whole: the frame's JSON with the history written into the empty table it
        /// ends on. The same bytes the frame would have written had it written them all.
        /// </summary>
        private byte[] Whole()
        {
            if (_history is null) return _json;
            // The history stays compact even in a text save: it is the bulk of a long
            // campaign, it is nothing but numbers, and indenting it multiplies the size of
            // the file for no gain a reader can see.
            var history = JsonSerializer.SerializeToUtf8Bytes(_history, JsonOptions);
            if (_text)
            {
                // ..."History": {} then the file's closing brace  ->  ..."History": <table> then it
                var head = _json.Length - TextHistoryTail.Length;
                var whole = new byte[head + history.Length + 2];
                _json.AsSpan(0, head).CopyTo(whole);
                history.CopyTo(whole.AsSpan(head));
                whole[^2] = (byte)'\n';
                whole[^1] = (byte)'}';
                return whole;
            }
            // ...,"History":{}}  ->  ...,"History":<table>}
            var compactHead = _json.Length - 3;
            var file = new byte[compactHead + history.Length + 1];
            _json.AsSpan(0, compactHead).CopyTo(file);
            history.CopyTo(file.AsSpan(compactHead));
            file[^1] = (byte)'}';
            return file;
        }

        public SaveMetadata Metadata { get; }

        /// <summary>
        /// Write the history and selected format. Safe on any thread: it reads
        /// nothing but its own bytes and its own copy of the history.
        /// An autosave is compressed for speed rather than for size - a tenth larger, in less
        /// than half the time - because it is taken every month and read almost never.
        /// </summary>
        public SaveMetadata Commit()
        {
            Directory.CreateDirectory(_root);
            var json = Whole();
            if (_text)
            {
                // Readable and deliberately unguarded. The point of a text save is that a
                // player can open it in any editor and change a number, so a checksum would
                // only turn every edit into a refusal to load. The shared .bdfsave extension
                // describes a campaign, while the first bytes identify its encoding.
                Metadata.Checksum = "";
                Metadata.CompressedBytes = json.LongLength;
                AtomicWrite(DataPath(_root, _slot), json);
                // Only older releases used a second filename for readable saves. Remove
                // that copy after the canonical file has been committed successfully.
                Stale(LegacyTextPath(_root, _slot));
                Stale(MetaPath(_root, _slot));
                DiagnosticLogs.Write("system", $"SAVE_END slot={_slot} text=true bytes={json.LongLength} format={FormatVersion} map={MapDataVersion}");
                return Metadata;
            }
            byte[] compressed;
            using (var memory = new MemoryStream())
            {
                using (var gzip = new GZipStream(memory,
                           Metadata.IsAutosave ? CompressionLevel.Optimal : CompressionLevel.SmallestSize, true))
                    gzip.Write(json);
                compressed = memory.ToArray();
            }
            // Gzip has its own CRC. There is no separate checksum file in the one-file format.
            Metadata.Checksum = "";
            Metadata.CompressedBytes = compressed.LongLength;
            AtomicWrite(DataPath(_root, _slot), compressed);
            Stale(LegacyTextPath(_root, _slot));
            Stale(MetaPath(_root, _slot));
            DiagnosticLogs.Write("system", $"SAVE_END slot={_slot} bytes={compressed.LongLength} json_bytes={json.LongLength} checksum={Metadata.Checksum} format={FormatVersion} map={MapDataVersion}");
            return Metadata;
        }

        /// <summary>Remove the other format's file for this slot, if one is left over from
        /// before the setting changed.</summary>
        private void Stale(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception exception) { DiagnosticLogs.Write("errors", $"SAVE_STALE_REMOVE {path}: {exception.Message}"); }
        }
    }

    /// <summary>Compact authoritative snapshot used for joining and resynchronising LAN peers.</summary>
    public static byte[] ExportNetworkSnapshot(Campaign campaign, string modFingerprint)
    {
        campaign?.FinishStep();
        if (campaign?.Player is null || campaign.ProvinceMap is null || campaign.ProvinceController is null ||
            campaign.ProvinceOccupation is null) return Array.Empty<byte>();
        var payload = CampaignSave.Capture(campaign, modFingerprint ?? "");
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true)) gzip.Write(json);
        return memory.ToArray();
    }

    public static SaveLoadResult ImportNetworkSnapshot(byte[] compressed, string activeModFingerprint)
    {
        if (compressed is null || compressed.Length == 0)
            return new SaveLoadResult(null, null, "The host sent an empty campaign snapshot.");
        try
        {
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var payload = JsonSerializer.Deserialize<CampaignSave>(gzip, JsonOptions);
            if (payload is null || payload.FormatVersion != FormatVersion ||
                !string.Equals(payload.MapDataVersion, MapDataVersion, StringComparison.Ordinal) ||
                payload.ProvinceCount <= 0 || payload.ProvinceController.Length != payload.ProvinceCount + 1 ||
                payload.ProvinceOccupation.Length != payload.ProvinceCount + 1)
                return new SaveLoadResult(null, null, "Host campaign uses incompatible save or map data.");
            var campaign = payload.Restore();
            var warning = string.Equals(payload.ModFingerprint ?? "", activeModFingerprint ?? "", StringComparison.Ordinal)
                ? "" : "Host and client mod fingerprints differ; the session may desynchronise.";
            return new SaveLoadResult(campaign, null, warning);
        }
        catch (Exception exception)
        {
            return new SaveLoadResult(null, null, $"Could not import host snapshot: {exception.Message}");
        }
    }

    public static SaveLoadResult Load(string root, string slot, string activeModFingerprint)
    {
        slot = SafeSlot(slot);
        var dataPath = ExistingDataPath(root, slot);
        if (!File.Exists(dataPath)) return new SaveLoadResult(null, null, "Save slot is empty.");
        using var file = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.SequentialScan);
        var storedLength = file.Length;
        var first = file.ReadByte();
        var second = file.ReadByte();
        file.Position = 0;
        var metadata = ReadMetadata(root, slot);
        // Text saves carry no checksum on purpose: they are meant to be edited, and a guard
        // that fires on every edit is a guard against the feature itself. Compressed saves
        // keep theirs - they cannot be repaired by hand, so a mismatch is worth refusing.
        if (first == 0x1f && second == 0x8b && metadata is not null && metadata.Checksum.Length > 0)
        {
            var checksum = Convert.ToHexString(SHA256.HashData(file));
            file.Position = 0;
            if (!string.Equals(metadata.Checksum, checksum, StringComparison.OrdinalIgnoreCase) &&
                ReadEmbeddedMetadata(dataPath) is null)
                return new SaveLoadResult(null, metadata, "Save checksum mismatch; the file may be incomplete or corrupted.");
        }

        CampaignSave payload;
        try
        {
            // Deserialize directly: do not retain the whole compressed input or
            // a second expanded JSON buffer alongside the restored world.
            using var source = OpenPayloadStream(file);
            payload = JsonSerializer.Deserialize<CampaignSave>(source, JsonOptions);
        }
        catch (Exception exception)
        {
            return new SaveLoadResult(null, metadata, $"Could not read save: {exception.Message}");
        }
        if (payload is null || payload.FormatVersion > FormatVersion)
            return new SaveLoadResult(null, metadata, "This save was created by a newer, unsupported format.");
        if (payload.FormatVersion < FormatVersion)
            return new SaveLoadResult(null, metadata,
                "This save predates the current map topology and cannot be restored safely.");
        if (!string.Equals(payload.MapDataVersion, MapDataVersion, StringComparison.Ordinal))
            return new SaveLoadResult(null, metadata,
                $"This save uses map data '{payload.MapDataVersion}', but the active map is '{MapDataVersion}'.");
        if (payload.ProvinceCount <= 0 ||
            payload.ProvinceController.Length != payload.ProvinceCount + 1 ||
            payload.ProvinceOccupation.Length != payload.ProvinceCount + 1)
            return new SaveLoadResult(null, metadata,
                "The save contains an incomplete province-control snapshot.");

        metadata = payload.EmbeddedMetadata ?? metadata;
        if (metadata is not null) metadata.CompressedBytes = storedLength;

        try
        {
            var campaign = payload.Restore();
            var warning = string.Equals(payload.ModFingerprint ?? "", activeModFingerprint ?? "", StringComparison.Ordinal)
                ? ""
                : "Loaded with a different mod set; unknown content may use base definitions.";
            // Local visual migration is authoritative on the loading host. ImportNetworkSnapshot
            // must keep the colours that host actually sent, not run its own migration.
            SecessionColours.MigrateLegacy(campaign);
            return new SaveLoadResult(campaign, metadata, warning);
        }
        catch (Exception exception)
        {
            return new SaveLoadResult(null, metadata, $"Save migration failed: {exception.Message}");
        }
    }

    public static IReadOnlyList<SaveMetadata> List(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<SaveMetadata>();
        return Directory.EnumerateFiles(root, "*.bdfsave*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".bdfsave", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".bdfsave.json", StringComparison.OrdinalIgnoreCase))
            .GroupBy(SlotFromPath, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var path = group.OrderByDescending(File.GetLastWriteTimeUtc).First();
                var metadata = ReadEmbeddedMetadata(path) ?? ReadMetadata(root, group.Key) ??
                               ReadEmbeddedMetadataAnywhere(path);
                if (metadata is null) return null;
                metadata.Slot = group.Key;
                metadata.CompressedBytes = new FileInfo(path).Length;
                return metadata;
            })
            .Where(item => item is not null)
            .OrderByDescending(item => item.SavedUtc)
            .ToArray();
    }

    private static string SlotFromPath(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".bdfsave.json", StringComparison.OrdinalIgnoreCase)
            ? name[..^".bdfsave.json".Length] : name[..^".bdfsave".Length];
    }

    /// <summary>Read only the first 64 KiB, not a campaign-sized JSON tree, to list a slot.
    /// EmbeddedMetadata is intentionally the first property in new files.</summary>
    private static SaveMetadata ReadEmbeddedMetadata(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using Stream source = OpenPayloadStream(file);
            var buffer = new byte[64 * 1024];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = source.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
            var reader = new Utf8JsonReader(buffer.AsSpan(0, count), isFinalBlock: false, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject ||
                !reader.Read() || reader.TokenType != JsonTokenType.PropertyName ||
                reader.GetString() != "EmbeddedMetadata" ||
                !reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.Deserialize<SaveMetadata>(MetadataOptions);
        }
        catch { return null; }
    }

    // A player may run a JSON formatter that sorts properties. Listing should still work;
    // the slow full-file path is reserved for files without a header or legacy sidecar.
    private static SaveMetadata ReadEmbeddedMetadataAnywhere(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using Stream source = OpenPayloadStream(file);
            using var doc = JsonDocument.Parse(source);
            return doc.RootElement.TryGetProperty("EmbeddedMetadata", out var element)
                ? element.Deserialize<SaveMetadata>(MetadataOptions) : null;
        }
        catch { return null; }
    }

    private static CampaignSave ReadCompressed(byte[] stored)
    {
        using var input = new MemoryStream(stored);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<CampaignSave>(gzip, JsonOptions);
    }

    private static bool IsGzip(byte[] stored) =>
        stored is { Length: >= 2 } && stored[0] == 0x1f && stored[1] == 0x8b;

    /// <summary>Both editable JSON and optional gzip use the same .bdfsave extension.
    /// Legacy .bdfsave.json files also work; sniff contents instead of trusting the name.</summary>
    private static Stream OpenPayloadStream(FileStream file)
    {
        var first = file.ReadByte();
        var second = file.ReadByte();
        var third = file.ReadByte();
        var fourth = file.ReadByte();
        file.Position = 0;
        if (first == 0x1f && second == 0x8b)
            return new GZipStream(file, CompressionMode.Decompress);
        if (first == (byte)'B' && second == (byte)'D' && third == (byte)'F' && fourth == (byte)'2')
        {
            file.Position = 4;
            return new BrotliStream(file, CompressionMode.Decompress);
        }
        return file;
    }

    public static bool Delete(string root, string slot)
    {
        slot = SafeSlot(slot);
        var removed = false;
        foreach (var path in new[] { DataPath(root, slot), LegacyTextPath(root, slot), MetaPath(root, slot) })
            if (File.Exists(path)) { File.Delete(path); removed = true; }
        return removed;
    }

    private static SaveMetadata ReadMetadata(string root, string slot)
    {
        var path = MetaPath(root, slot);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SaveMetadata>(File.ReadAllText(path), MetadataOptions); }
        catch { return null; }
    }

    /// <summary>
    /// Commit save data so a crash cannot leave a slot holding half a campaign.
    ///
    /// Two separate hazards, and the old code covered only the first. Writing a temporary file
    /// and renaming it means the visible file is always a complete one - but a rename can reach
    /// the disk before the bytes it points at do, so a power loss could still leave a slot whose
    /// "complete" file is empty or stale; hence the flush to hardware before the swap. And with
    /// no previous copy kept, a slot that goes bad - or that the player overwrites by hand - is
    /// simply gone; File.Replace keeps the outgoing version as .bak in the same atomic step, the
    /// way ContentWorkbench already saves authored scenario files.
    ///
    /// The backup is one generation deep, so a slot costs twice its size on disk and no more.
    /// List() filters on the .bdfsave suffix, so the .bak never appears as a save.
    /// </summary>
    private static void AtomicWrite(string target, byte[] data)
    {
        var temporary = target + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            file.Write(data);
            file.Flush(true);
        }
        if (File.Exists(target)) File.Replace(temporary, target, target + ".bak");
        else File.Move(temporary, target);
    }

    private static string SafeSlot(string slot)
    {
        var safe = new string((slot ?? "").Where(character => char.IsLetterOrDigit(character) || character is '_' or '-').ToArray());
        if (string.IsNullOrWhiteSpace(safe)) throw new ArgumentException("Save slot has no valid characters.", nameof(slot));
        return safe.ToLowerInvariant();
    }

    private static string DataPath(string root, string slot) => Path.Combine(root, slot + ".bdfsave");
    private static string LegacyTextPath(string root, string slot) => Path.Combine(root, slot + ".bdfsave.json");

    /// <summary>Whichever of a slot's two files is on disk. The format is a preference, and
    /// a save made under the other one still has to load.</summary>
    private static string ExistingDataPath(string root, string slot)
    {
        var canonical = DataPath(root, slot);
        var legacyText = LegacyTextPath(root, slot);
        if (!File.Exists(canonical)) return legacyText;
        if (!File.Exists(legacyText)) return canonical;
        // Both can coexist when upgrading an older save or after an interrupted write.
        return File.GetLastWriteTimeUtc(canonical) >= File.GetLastWriteTimeUtc(legacyText)
            ? canonical : legacyText;
    }
    private static string MetaPath(string root, string slot) => Path.Combine(root, slot + ".meta.json");
    private static string SlotTitle(string slot) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slot.Replace('_', ' '));

    private sealed class CampaignSave
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyOrder(-1)]
        public SaveMetadata EmbeddedMetadata;
        public int FormatVersion = SaveSystem.FormatVersion;
        public string GameVersion = SaveSystem.GameVersion;
        public string MapDataVersion = SaveSystem.MapDataVersion;
        public string ModFingerprint = "";
        public int ProvinceCount;
        public int Seed;
        /// <summary>New snapshots keep intentional -1 AI production policies; older saves
        /// still receive the percentage-to-units migration.</summary>
        public bool ProductionUnitsSaved;
        public string ScenarioId = "2024";
        public Defines ScenarioRules;
        public Dictionary<string, bool> FeatureDefaults;
        public Dictionary<string, string> FeatureUnlocks;
        public Dictionary<string, string> FeatureCommands;
        public Dictionary<string, string> FeatureScreens;
        public Dictionary<string, string> FeatureSystems;
        public Dictionary<string, bool> FeatureOverrides = new();
        public Dictionary<string, int[]> ScenarioTerritories = new();
        public GameDate Date;
        public int ElapsedDays;

        /// <summary>Whether the campaign was being watched rather than played.</summary>
        public bool ObserverMode;
        // Derived authority, separate from the cinematic/no-popups ObserverMode.
        // Console observation has no human seats; that must not become a human country
        // on reload. Absent in legacy saves, where the historical fallback is retained.
        public bool NoHumanAuthority;

        /// <summary>The campaign's own options: its step, how often it saves. Absent in older saves.</summary>
        public Dictionary<string, string> Settings = new();
        public bool IsPaused;
        public uint RandomState;
        public int PlayerId;
        public int ActiveSeat;
        public int NextBattleId;
        public int MapRevision;
        public bool InitialDeploymentCompleted;
        public List<int> HumanSeats = new();
        public List<Nation> Nations = new();
        public List<ScenarioState> States = new();
        public List<Army> Armies = new();
        public List<RecruitmentOrder> Recruitment = new();
        public int NextRecruitmentId = 1;
        public List<SupportFormation> SupportFormations = new();
        public List<SupportOrder> SupportOrders = new();
        public int NextSupportId = 1;
        public List<ArmyGroup> ArmyGroups = new();
        public List<Fleet> Fleets = new();
        public List<StrategicWar> Wars = new();
        // Null only for older payloads: reconstruct their surviving outbreak evidence once.
        public CivilWarBudgetState CivilWarBudget;

        /// <summary>Who is backing whom in somebody else's war (ProxyWars). Absent in older saves.</summary>
        public List<Sponsorship> Sponsorships = new();
        public List<InternationalOrganization> Organizations = new();
        public List<DealOffer> DealOffers = new();
        public int NextDealOfferId = 1;
        public List<UltimatumPaper> UltimatumPapers = new();
        public int NextUltimatumId = 1;
        public List<ForeignAssistanceRequest> AssistanceRequests = new();
        public List<ForeignAssistanceCooldown> AssistanceCooldowns = new();
        public int NextAssistanceRequestId = 1;
        public int AssistanceLastTickDay = -1;
        /// <summary>The ground each country first held, and which countries' falls are decided
        /// (Movements). Neither was saved: a loaded campaign forgot every government in exile's
        /// homeland, and a client's world parted from its host's within a day, because the
        /// territorial titles and the tension they feed read this. Absent in older saves.</summary>
        public Dictionary<int, List<int>> Homelands = new();
        public List<int> Fallen = new();
        public int NextOrganizationId = 1;
        public int NextSponsorshipId = 1;

        /// <summary>Who the world has agreed to contain. Notoriety rides along on the nations.</summary>
        public List<Coalition> Coalitions = new();

        /// <summary>
        /// The lines the player drew, and the next id to hand out.
        ///
        /// They were not in the save at all, which was quiet and expensive: a formation
        /// carries the id of the line it was put on, that id was written down, and the line
        /// itself was not - so a reload gave every division a plan number pointing at
        /// nothing, they all silently reverted to the war's own front, and the player's
        /// dispositions were gone with no message anywhere.
        /// </summary>
        public List<DrawnFrontLine> FrontLines = new();

        /// <summary>
        /// What each good and building was called when this was written, against the number
        /// it was stored as.
        ///
        /// A good is an enum value, which is to say a number, and the game's own twenty
        /// always have the same numbers. A good declared by a mod is handed the next free
        /// one, so which number it gets depends on the order the mods were read - which
        /// means the same save, opened with the same mods in a different order, would find
        /// its aluminium where somebody else's fertiliser used to be. Silently: a number is
        /// a number and nothing would complain.
        ///
        /// So the names travel with the save and the numbers are re-derived on the way back
        /// in. Absent - every save written before this existed - means the twenty and the
        /// seventeen the game ships, which have never moved.
        /// </summary>
        public Dictionary<string, int> GoodIds = new();
        public Dictionary<string, int> BuildingIds = new();
        public int NextFrontLineId;
        public List<LandBattle> Battles = new();
        public List<ConstructionProject> Construction = new();
        public List<PendingCampaignEvent> PendingEvents = new();
        public List<ChronicleEntry> Chronicle = new();
        public Dictionary<long, DiplomaticRelation> Relations = new();
        /// <summary>
        /// Written last in the file, so that it can be written last in time: see
        /// <see cref="SaveSystem.Prepare"/>. Reading does not care where it is.
        /// </summary>
        [JsonPropertyOrder(1)]
        public Dictionary<int, List<NationHistorySample>> History = new();
        public HashSet<int> DestroyedProvinces = new();
        public HashSet<long> CrossoverScars = new();
        public List<CrossoverWork> CrossoverWorks = new();
        public List<CrossoverImpact> CrossoverImpacts = new();
        public ushort[] ProvinceController = Array.Empty<ushort>();
        public float[] ProvinceOccupation = Array.Empty<float>();

        /// <summary>Who owns each province, which a peace can redraw and the raster cannot know.</summary>
        public ushort[] ProvinceOwner = Array.Empty<ushort>();

        /// <summary>Which region each province belongs to, after any the border split.</summary>
        public ushort[] ProvinceState = Array.Empty<ushort>();

        public static CampaignSave Capture(Campaign campaign, string modFingerprint) => new()
        {
            ModFingerprint = modFingerprint ?? "",
            ProvinceCount = campaign.ProvinceMap?.ProvinceCount ??
                Math.Max(0, (campaign.ProvinceController?.Length ?? 1) - 1),
            Seed = campaign.Seed,
            ProductionUnitsSaved = true,
            ScenarioId = campaign.ScenarioId,
            ScenarioRules = campaign.ScenarioId != "2024" ? campaign.Defines : null,
            ScenarioTerritories = new Dictionary<string, int[]>(campaign.ScenarioTerritories),
            Date = campaign.Date,
            ElapsedDays = campaign.ElapsedDays,
            ObserverMode = campaign.ObserverMode,
            NoHumanAuthority = campaign.HumanSeats.Count == 0 && campaign.Nations.All(n => !n.IsHuman),
            Settings = new Dictionary<string, string>(campaign.Settings),
            FeatureDefaults = new Dictionary<string, bool>(campaign.Defines.Features),
            FeatureUnlocks = new Dictionary<string, string>(campaign.Defines.FeatureUnlocks),
            FeatureCommands = new Dictionary<string, string>(campaign.Defines.FeatureCommands),
            FeatureScreens = new Dictionary<string, string>(campaign.Defines.FeatureScreens),
            FeatureSystems = new Dictionary<string, string>(campaign.Defines.FeatureSystems),
            FeatureOverrides = new Dictionary<string, bool>(campaign.FeatureOverrides),
            IsPaused = campaign.IsPaused,
            RandomState = campaign.Random.State,
            PlayerId = campaign.Player?.Id ?? 0,
            ActiveSeat = campaign.ActiveSeat,
            NextBattleId = campaign.NextBattleId,
            MapRevision = campaign.MapRevision,
            InitialDeploymentCompleted = campaign.InitialDeploymentCompleted,
            HumanSeats = new List<int>(campaign.HumanSeats),
            Nations = new List<Nation>(campaign.Nations),
            States = new List<ScenarioState>(campaign.States),
            Armies = new List<Army>(campaign.Armies),
            Recruitment = new List<RecruitmentOrder>(campaign.Recruitment),
            NextRecruitmentId = campaign.NextRecruitmentId,
            SupportFormations = new List<SupportFormation>(campaign.SupportFormations),
            SupportOrders = new List<SupportOrder>(campaign.SupportOrders),
            NextSupportId = campaign.NextSupportId,
            ArmyGroups = new List<ArmyGroup>(campaign.ArmyGroups),
            Fleets = new List<Fleet>(campaign.Fleets),
            Wars = new List<StrategicWar>(campaign.Wars),
            CivilWarBudget = CivilWarOutbreak.Budget(campaign).Copy(campaign.ElapsedDays),
            Sponsorships = new List<Sponsorship>(campaign.Sponsorships),
            Organizations = new List<InternationalOrganization>(campaign.Organizations),
            DealOffers = campaign.DealOffers.Select(DealNegotiations.Copy).ToList(),
            NextDealOfferId = campaign.NextDealOfferId,
            UltimatumPapers = campaign.UltimatumPapers.Select(Ultimatum.Copy).ToList(),
            NextUltimatumId = campaign.NextUltimatumId,
            AssistanceRequests = campaign.AssistanceRequests.Select(ForeignAssistance.Copy).ToList(),
            AssistanceCooldowns = campaign.AssistanceCooldowns.Select(ForeignAssistance.Copy).ToList(),
            NextAssistanceRequestId = campaign.NextAssistanceRequestId,
            AssistanceLastTickDay = campaign.AssistanceLastTickDay,
            Homelands = campaign.Homelands.ToDictionary(pair => pair.Key, pair => new List<int>(pair.Value)),
            Fallen = campaign.Fallen.OrderBy(id => id).ToList(),
            NextOrganizationId = campaign.NextOrganizationId,
            NextSponsorshipId = campaign.NextSponsorshipId,
            Coalitions = new List<Coalition>(campaign.Coalitions),
            FrontLines = new List<DrawnFrontLine>(campaign.FrontLines),
            NextFrontLineId = campaign.NextFrontLineId,
            Battles = new List<LandBattle>(campaign.Battles),
            Construction = new List<ConstructionProject>(campaign.Construction),
            PendingEvents = new List<PendingCampaignEvent>(campaign.PendingEvents),
            Chronicle = new List<ChronicleEntry>(campaign.Chronicle.Entries),
            Relations = new Dictionary<long, DiplomaticRelation>(campaign.Relations),
            History = campaign.History.Export(),
            GoodIds = CommodityIdTable(),
            BuildingIds = BuildingIdTable(),
            ProvinceController = campaign.ProvinceController is null ? Array.Empty<ushort>() : (ushort[])campaign.ProvinceController.Clone(),
            ProvinceOccupation = campaign.ProvinceOccupation is null ? Array.Empty<float>() : (float[])campaign.ProvinceOccupation.Clone(),
            DestroyedProvinces = new HashSet<int>(campaign.DestroyedProvinces),
            CrossoverScars = new HashSet<long>(campaign.CrossoverScars),
            CrossoverWorks = campaign.CrossoverWorks.Select(w => w.Copy()).ToList(),
            CrossoverImpacts = new List<CrossoverImpact>(campaign.CrossoverImpacts),
            ProvinceOwner = campaign.ProvinceMap is null ? Array.Empty<ushort>() : (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone(),
            ProvinceState = campaign.ProvinceMap is null ? Array.Empty<ushort>() : (ushort[])campaign.ProvinceMap.ProvinceToState.Clone()
        };

        public Campaign Restore()
        {
            var campaign = new Campaign(Seed, ScenarioRules) { ScenarioId = ScenarioId,
                ScenarioTerritories = new Dictionary<string, int[]>(ScenarioTerritories, StringComparer.OrdinalIgnoreCase) };
            Replace(campaign.Nations, Nations);
            if (FeatureDefaults is not null) campaign.Defines.Features = new(FeatureDefaults, StringComparer.OrdinalIgnoreCase);
            if (FeatureUnlocks is not null) campaign.Defines.FeatureUnlocks = new(FeatureUnlocks, StringComparer.OrdinalIgnoreCase);
            if (FeatureCommands is not null) campaign.Defines.FeatureCommands = new(FeatureCommands, StringComparer.OrdinalIgnoreCase);
            if (FeatureScreens is not null) campaign.Defines.FeatureScreens = new(FeatureScreens, StringComparer.OrdinalIgnoreCase);
            if (FeatureSystems is not null) campaign.Defines.FeatureSystems = new(FeatureSystems, StringComparer.OrdinalIgnoreCase);
            foreach (var (id, enabled) in FeatureOverrides ?? new())
                if (MechanicFeatures.ValidId(id)) campaign.FeatureOverrides[id] = enabled;
            Replace(campaign.States, States);
            Replace(campaign.Armies, Armies);
            Replace(campaign.Recruitment, Recruitment);
            campaign.NextRecruitmentId = Math.Max(NextRecruitmentId, campaign.Recruitment.Count == 0 ? 1 : campaign.Recruitment.Max(o => o.Id) + 1);
            Replace(campaign.SupportFormations, SupportFormations);
            Replace(campaign.SupportOrders, SupportOrders);
            // Never below what the restored formations already use, or the next raised
            // one would be handed an id somebody is standing on.
            campaign.NextSupportId = Math.Max(NextSupportId,
                campaign.SupportFormations.Count == 0 && campaign.SupportOrders.Count == 0
                    ? 1
                    : Math.Max(
                        campaign.SupportFormations.Count == 0 ? 0 : campaign.SupportFormations.Max(f => f.Id),
                        campaign.SupportOrders.Count == 0 ? 0 : campaign.SupportOrders.Max(o => o.Id)) + 1);
            Replace(campaign.ArmyGroups, ArmyGroups);
            Replace(campaign.Fleets, Fleets);
            Replace(campaign.Wars, Wars);
            Replace(campaign.Sponsorships, Sponsorships);
            Replace(campaign.Organizations, Organizations ?? new());
            DealNegotiations.Restore(campaign, DealOffers, NextDealOfferId);
            Ultimatum.Restore(campaign, UltimatumPapers, NextUltimatumId);
            campaign.Homelands.Clear();
            foreach (var (id, regions) in Homelands ?? new()) campaign.Homelands[id] = new List<int>(regions ?? new());
            campaign.Fallen.Clear();
            foreach (var id in Fallen ?? new()) campaign.Fallen.Add(id);
            campaign.NextOrganizationId = Math.Max(NextOrganizationId, campaign.Organizations.Count == 0 ? 1 : campaign.Organizations.Max(o => o.Id) + 1);
            campaign.NextSponsorshipId = Math.Max(NextSponsorshipId,
                campaign.Sponsorships.Count == 0 ? 1 : campaign.Sponsorships.Max(s => s.Id) + 1);
            Replace(campaign.Coalitions, Coalitions);
            Replace(campaign.FrontLines, FrontLines);
            // Never below what the restored lines already use, or the next drawn line would
            // be handed an id somebody is standing on.
            campaign.NextFrontLineId = Math.Max(NextFrontLineId,
                campaign.FrontLines.Count == 0 ? 0 : campaign.FrontLines.Max(line => line.Id) + 1);
            Replace(campaign.Battles, Battles);
            Replace(campaign.Construction, Construction);
            Replace(campaign.PendingEvents, PendingEvents);
            campaign.Chronicle.Restore(Chronicle);
            campaign.Relations.Clear();
            foreach (var entry in Relations) campaign.Relations[entry.Key] = entry.Value;
            campaign.History.Restore(History);
            campaign.ObserverMode = ObserverMode;
            foreach (var (id, value) in Settings) campaign.Settings[id] = value;
            campaign.DestroyedProvinces.UnionWith(DestroyedProvinces ?? new HashSet<int>());
            campaign.CrossoverScars.UnionWith(CrossoverScars ?? new());
            campaign.CrossoverWorks.AddRange((CrossoverWorks ?? new()).Select(w => w.Copy()));
            campaign.CrossoverImpacts.AddRange(CrossoverImpacts ?? new());
            campaign.RestoreRuntimeState(Date, ElapsedDays, IsPaused, RandomState, PlayerId,
                HumanSeats, ActiveSeat, NextBattleId, MapRevision, InitialDeploymentCompleted,
                ProvinceController, ProvinceOccupation, ProvinceOwner, ProvinceState, NoHumanAuthority);
            ForeignAssistance.Restore(campaign, AssistanceRequests, AssistanceCooldowns,
                NextAssistanceRequestId, AssistanceLastTickDay);
            campaign.CivilWarBudget = CivilWarBudget?.Copy(campaign.ElapsedDays) ??
                CivilWarBudgetState.FromLegacy(campaign);
            // Both memories were introduced together. Never infer new state while loading
            // a current payload; its negative military date can be an intentional value.
            if (CivilWarBudget is null) CivilWarOutbreak.RestoreMilitaryCoupMemory(campaign);

            // The state list was just replaced wholesale, so the ownership index built
            // against the bootstrap campaign no longer describes it.
            campaign.InvalidateStateIndex();
            // Only the books of a save from before public debt need opening. A country whose
            // books are open was saved as it stood - an overdraft between two weekly
            // settlements included - and moving that overdraft into debt here made a loaded
            // world differ from the one that was saved, which is a desync in multiplayer.
            foreach (var loadedNation in campaign.Nations)
                if (!loadedNation.DebtInitialized) SovereignFinance.NormalizeAccounts(campaign, loadedNation);

            // And any country this campaign founded rather than loaded says again who it is
            // for. The demography catalogue is read from files and has never heard of it.
            campaign.DeclareFoundedPeoples();

            // Before anything reads a good or a building out of the restored campaign.
            RemapContent(campaign);
            if (!ProductionUnitsSaved)
                foreach (var nation in campaign.Nations)
                    ProductionProgramme.MigrateLegacy(nation, ProductionProgramme.Allocate(campaign, nation));

            // A save written before output became emergent carries no calibration factor.
            // Recalibrating against the GDP the save already holds is exactly right for
            // both cases: an old save keeps the economy it was played with, and a current
            // save re-derives the same factor it was saved with.
            if (campaign.Nations.Any(nation => nation.OutputScale <= 0f))
                EconomySystem.CalibrateOutput(campaign);

            // Old saves could not restore private breakdown parts. New ones do, and a
            // blanket recalculation here would silently replace a saved explanation with
            // one derived under today's formulas before the player even advances time.
            if (campaign.Nations.All(nation => nation.Budget.Title.Length == 0 &&
                nation.StabilityDrivers.Title.Length == 0)) EconomySystem.Explain(campaign);
            return campaign;
        }

        private static Dictionary<string, int> CommodityIdTable()
        {
            var table = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var commodity in CommodityCatalog.All)
                table[CommodityCatalog.IdOf(commodity)] = (int)commodity;
            return table;
        }

        private static Dictionary<string, int> BuildingIdTable()
        {
            var table = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kind in BuildingCatalog.All)
                table[BuildingCatalog.IdOf(kind)] = (int)kind;
            return table;
        }

        /// <summary>
        /// Put the campaign's goods and buildings back on the numbers this catalog uses.
        ///
        /// Does nothing at all in the ordinary case, which is every save without mods and
        /// most saves with them: the numbers only move when a mod that declared a good is
        /// read in a different position than it was. Six things hold one of these values -
        /// a country's market, a region's buildings and its deposits, a market quote's own
        /// note of which good it is, a construction project, and a trade route.
        /// </summary>
        private void RemapContent(Campaign campaign)
        {
            var goods = Moved(GoodIds, id => CommodityCatalog.TryParse(id, out var now) ? (int)now : -1);
            var buildings = Moved(BuildingIds, id => BuildingCatalog.TryParse(id, out var now) ? (int)now : -1);
            if (goods.Count == 0 && buildings.Count == 0) return;

            DiagnosticLogs.Write("system",
                $"SAVE_REMAP goods={goods.Count} buildings={buildings.Count}");

            foreach (var nation in campaign.Nations)
            {
                Rekey(nation.Market, goods, key => (Commodity)key);
                foreach (var line in nation.ProductionLines)
                    if (line is not null && goods.TryGetValue((int)line.Good, out var lineGood))
                        line.Good = (Commodity)lineGood;
                foreach (var quote in nation.Market.Values)
                    if (goods.TryGetValue((int)quote.Commodity, out var moved))
                        quote.Commodity = (Commodity)moved;
            }
            foreach (var state in campaign.States)
            {
                Rekey(state.Buildings, buildings, key => (BuildingKind)key);
                Rekey(state.ResourceDeposits, goods, key => (Commodity)key);
            }
            foreach (var project in campaign.Construction)
                if (buildings.TryGetValue((int)project.Building, out var moved))
                    project.Building = (BuildingKind)moved;
            foreach (var route in campaign.TradeRoutes)
                if (goods.TryGetValue((int)route.Commodity, out var moved))
                    route.Commodity = (Commodity)moved;
        }

        /// <summary>Only the ones whose number actually changed; a name the catalog no longer
        /// has is left where it is, so removing a mod costs its own goods and nothing else.</summary>
        private static Dictionary<int, int> Moved(Dictionary<string, int> saved, Func<string, int> now)
        {
            var moved = new Dictionary<int, int>();
            if (saved is null) return moved;
            foreach (var entry in saved)
            {
                var current = now(entry.Key);
                if (current >= 0 && current != entry.Value) moved[entry.Value] = current;
            }
            return moved;
        }

        private static void Rekey<TKey, TValue>(Dictionary<TKey, TValue> table,
            Dictionary<int, int> moved, Func<int, TKey> asKey) where TKey : struct, Enum
        {
            if (moved.Count == 0 || table.Count == 0) return;
            var rebuilt = new List<KeyValuePair<TKey, TValue>>(table.Count);
            foreach (var entry in table)
            {
                var number = Convert.ToInt32(entry.Key);
                rebuilt.Add(moved.TryGetValue(number, out var to)
                    ? new KeyValuePair<TKey, TValue>(asKey(to), entry.Value)
                    : entry);
            }
            table.Clear();
            foreach (var entry in rebuilt) table[entry.Key] = entry.Value;
        }

        private static void Replace<T>(List<T> target, List<T> source)
        {
            target.Clear();
            if (source is not null) target.AddRange(source);
        }
    }
}
