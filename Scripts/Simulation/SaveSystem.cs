using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;
using ZLinq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// The least real time between two autosaves, in seconds, whatever the calendar says.
    /// </summary>
    public float AutosaveMinimumSeconds { get; set; } = 120f;

    /// <summary>
    /// Use the slowest Brotli setting for a manual save. Off by default.
    /// </summary>
    public bool SaveCompressionThorough { get; set; }
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
    public string MapId { get; set; } = "";
    public int ProvinceCount { get; set; }
    public string ModFingerprint { get; set; } = "";
    public string Checksum { get; set; } = "";
    public long CompressedBytes { get; set; }
    public bool IsAutosave { get; set; }
    [JsonIgnore]
    public string FormatLabel { get; set; } = "";
}

public readonly record struct SaveLoadResult(Campaign Campaign, SaveMetadata Metadata, string Warning)
{
    public bool Success => Campaign is not null;
}

/// <summary>
/// Versioned and atomic campaign persistence. Saves contain simulation state only.
/// </summary>
public static partial class SaveSystem
{
    public const int FormatVersion = 2;
    public const string GameVersion = GameBuildInfo.Version;
    public const string MapDataVersion = "2024.11.09-a";
    public const string QuickSlot = "quicksave";
    public const string AutoSlot = "autosave";
    public const string Extension = ".dofsave";
    public const string PreviousExtension = ".dfsave";
    public const string LegacyExtension = ".bdfsave";
    public const string LegacyJsonExtension = ".bdfsave.json";

    private const int DefaultIoBufferBytes = 64 * 1024;
    private const int ScanIoBufferBytes = 16 * 1024;
    private const int CompactRowInitialBytes = 512;
    private const int MaxHeaderScanBytes = 4 * 1024 * 1024;
    private const int MaxEmbeddedMetadataBytes = 512 * 1024;
    private const byte GzipMagic0 = 0x1f;
    private const byte GzipMagic1 = 0x8b;
    private const int DefconFallback = 5;
    private const int InvalidDaySentinel = -100000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultBufferSize = DefaultIoBufferBytes,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers = { SaveDefaults.Modify }
        }
    };

    private static readonly JsonSerializerOptions MetadataOptions = new(JsonOptions) { WriteIndented = true };

    private static readonly JsonSerializerOptions ScenarioOptions = new(JsonOptions)
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private static readonly JsonSerializerOptions TextOptions = CreateTextOptions();

    private static JsonSerializerOptions CreateTextOptions()
    {
        var options = new JsonSerializerOptions(JsonOptions) { WriteIndented = true };
        options.Converters.Add(new CompactArrayConverter<ushort>());
        options.Converters.Add(new CompactArrayConverter<float>());
        options.Converters.Add(new CompactArrayConverter<int>());
        options.Converters.Add(new CompactRowConverter<PopulationGroup>());
        options.Converters.Add(new CompactRowConverter<PublicImage>());
        options.Converters.Add(new CompactRowConverter<MarketQuote>());
        options.Converters.Add(new CompactRowConverter<OpinionReason>());
        options.Converters.Add(new CompactRowConverter<ChronicleEntry>());
        options.Converters.Add(new CompactRowConverter<DiplomaticRelation>());
        options.Converters.Add(new CompactRowConverter<TradeRoute>());
        return options;
    }

    private sealed class CompactRowConverter<T> : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<T>(ref reader, JsonOptions);

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            using var rowBuffer = new ArrayPoolBufferWriter<byte>(CompactRowInitialBytes);
            using (var rowWriter = new Utf8JsonWriter(rowBuffer))
                JsonSerializer.Serialize(rowWriter, value, JsonOptions);
            writer.WriteRawValue(rowBuffer.WrittenSpan, skipInputValidation: true);
        }
    }

    private sealed class CompactArrayConverter<T> : JsonConverter<T[]>
    {
        public override T[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<T[]>(ref reader, JsonOptions);

        public override void Write(Utf8JsonWriter writer, T[] value, JsonSerializerOptions options)
        {
            var hint = value is null ? 64 : Math.Max(CompactRowInitialBytes, value.Length * 4);
            using var rowBuffer = new ArrayPoolBufferWriter<byte>(hint);
            using (var rowWriter = new Utf8JsonWriter(rowBuffer))
                JsonSerializer.Serialize(rowWriter, value, JsonOptions);
            writer.WriteRawValue(rowBuffer.WrittenSpan, skipInputValidation: true);
        }
    }

    /// <summary>Independent, readable scenario; no contemporary bootstrap is used on import.</summary>
    public static string ExportScenarioJson(Campaign campaign, string scenarioId)
    {
        if (campaign.ProvinceMap is null) throw new InvalidOperationException("Wait for the map to finish loading.");
        var payload = CampaignSave.Capture(campaign, "");
        SaveStringTable.Use(payload.StringTable);
        payload.ScenarioId = scenarioId;
        payload.ScenarioRules = campaign.Defines;
        payload.IsPaused = true;
        payload.History.Clear();
        payload.Chronicle.Clear();
        payload.ScenarioTerritories = campaign.Nations.ToDictionary(n => n.Tag, n =>
            Enumerable.Range(1, campaign.ProvinceMap.ProvinceCount).Where(p => payload.ProvinceOwner[p] == n.MapIndex).ToArray());
        var interned = SaveStringTable.InternIds;
        SaveStringTable.InternIds = false;
        try { return JsonSerializer.Serialize(payload, ScenarioOptions); }
        finally { SaveStringTable.InternIds = interned; }
    }

    public static Campaign ImportScenarioJson(string json, string scenarioId, int expectedProvinceCount = 0,
        int? seedOverride = null)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(json.Length);
        var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            var actualBytes = Encoding.UTF8.GetBytes(json, rented);
            ReadOnlySpan<byte> span = rented.AsSpan(0, actualBytes);
            span = StripUtf8Bom(span);
            SaveStringTable.Use(ScanStringTable(span));
            var payload = JsonSerializer.Deserialize<CampaignSave>(span, JsonOptions)
                ?? throw new InvalidDataException("Empty scenario.");
            ValidateScenarioPayload(payload, expectedProvinceCount);
            ApplyScenarioSeedOverride(payload, seedOverride);
            payload.ScenarioId = scenarioId;
            payload.IsPaused = true;
            return payload.Restore();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ValidateScenarioPayload(CampaignSave payload, int expectedProvinceCount)
    {
        if (payload.FormatVersion != FormatVersion || payload.MapDataVersion != MapDataVersion || payload.ProvinceCount <= 0 ||
            (expectedProvinceCount > 0 && payload.ProvinceCount != expectedProvinceCount) ||
            payload.ProvinceOwner.Length != payload.ProvinceCount + 1 ||
            payload.ProvinceState.Length != payload.ProvinceCount + 1 ||
            payload.ProvinceController.Length != payload.ProvinceCount + 1 ||
            payload.ProvinceOccupation.Length != payload.ProvinceCount + 1)
            throw new InvalidDataException("Scenario requires a complete political layer for the current map.");
        if (!HistoricalDate.TryCreate(payload.Date.Year, payload.Date.Month, payload.Date.Day, out _))
            throw new InvalidDataException("Choose a valid calendar date (astronomical years -9999 to 9999).");
        if (payload.Nations.Count == 0 || !payload.Nations.AsValueEnumerable().Any(n => n.Id == payload.PlayerId) ||
            payload.Nations.AsValueEnumerable().Select(n => n.Id).Distinct().Count() != payload.Nations.Count ||
            payload.Nations.AsValueEnumerable().Select(n => n.MapIndex).Distinct().Count() != payload.Nations.Count ||
            payload.Nations.AsValueEnumerable().Select(n => n.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Nations.Count ||
            payload.Nations.AsValueEnumerable().Any(static n => n.MapIndex <= 0 || n.MapIndex > ushort.MaxValue))
            throw new InvalidDataException("Invalid country IDs, tags or player in scenario.");

        var owners = payload.Nations.Select(n => n.MapIndex).Append(0).ToHashSet();
        var states = payload.States.Select(s => s.MapIndex).Append(0).ToHashSet();
        if (payload.States.Select(s => s.Id).Distinct().Count() != payload.States.Count ||
            states.Count != payload.States.Count + 1 ||
            payload.States.Any(s => s.MapIndex <= 0 || s.MapIndex > ushort.MaxValue || !owners.Contains(s.NationMapIndex)) ||
            payload.Date.Hour is < 0 or > 23 ||
            payload.Armies.Select(a => a.Id).Distinct().Count() != payload.Armies.Count ||
            payload.Armies.Any(a => !payload.Nations.Any(n => n.Id == a.NationId) || a.ProvinceId < 0 || a.ProvinceId > payload.ProvinceCount) ||
            payload.ScenarioTerritories.Values.Any(ps => ps is null || ps.Any(p => p <= 0 || p > payload.ProvinceCount)))
            throw new InvalidDataException("Invalid state, army, territory or clock references.");
        if (payload.ProvinceOwner.Any(p => !owners.Contains(p)) ||
            payload.ProvinceController.Any(p => !owners.Contains(p)) ||
            payload.ProvinceState.Any(p => !states.Contains(p)))
            throw new InvalidDataException("Political map references an unknown country or state.");
    }

    private static void ApplyScenarioSeedOverride(CampaignSave payload, int? seedOverride)
    {
        if (!seedOverride.HasValue) return;
        if (seedOverride.Value <= 0) throw new InvalidDataException("Scenario seed must be positive.");
        payload.Seed = seedOverride.Value;
        payload.RandomState = new SimRandom(seedOverride.Value).State;
    }

    public static SaveMetadata Save(string root, string slot, Campaign campaign, string modFingerprint,
        string displayName = "", bool autosave = false) =>
        Prepare(root, slot, campaign, modFingerprint, displayName, autosave).Commit();

    public static PreparedSave Prepare(string root, string slot, Campaign campaign, string modFingerprint,
        string displayName = "", bool autosave = false)
    {
        var writeEntered = false;
        if (campaign is not null && campaign.DeferMainThreadNotifications && !campaign.AccessGate.IsWriterHeldByCurrentThread)
        {
            campaign.AccessGate.EnterWrite();
            writeEntered = true;
        }
        try { return PrepareCore(root, slot, campaign, modFingerprint, displayName, autosave); }
        finally { if (writeEntered) campaign!.AccessGate.ExitWrite(); }
    }

    private static PreparedSave PrepareCore(string root, string slot, Campaign campaign, string modFingerprint,
        string displayName, bool autosave)
    {
        campaign?.FinishStep();
        using var timing = SimProfiler.Measure("save.prepare");
        DiagnosticLogs.Write("system", $"SAVE_BEGIN slot={SafeSlot(slot)} autosave={autosave} day={campaign?.ElapsedDays}");
        if (campaign?.Player is null) throw new InvalidOperationException("No active campaign to save.");
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null || campaign.ProvinceOccupation is null)
            throw new InvalidOperationException("The tactical map is still being prepared; try saving again in a moment.");
        slot = SafeSlot(slot);
        var text = campaign.Defines.SaveTextFormat;
        foreach (var n in campaign.Nations) TacticalDoctrines.EnsureShelves(n);

        CampaignSave payload;
        using (SimProfiler.Measure("  save:capture"))
            payload = CampaignSave.Capture(campaign, modFingerprint, includeRelations: campaign.Defines.SaveDeepIntegrity);

        ValidatePreparedIntegrity(root, campaign, payload);
        var metadata = BuildSaveMetadata(slot, displayName, campaign, payload.ProvinceCount, modFingerprint, autosave);
        payload.EmbeddedMetadata = metadata;
        var history = ExtractAndValidateHistory(root, campaign, payload);

        using (SimProfiler.Measure("  save:snapshot"))
            payload.Isolate(campaign);
        return new PreparedSave(root, slot, payload, history, metadata, text, campaign.Defines.SaveCompressionThorough);
    }

    private static void ValidatePreparedIntegrity(string root, Campaign campaign, CampaignSave payload)
    {
        var issues = new List<string>();
        string live;
        using (SimProfiler.Measure("  save:live"))
            live = NumericIntegrity.FirstLiveIssue(campaign);
        if (!string.IsNullOrEmpty(live)) issues.Add(live);
        if (campaign.Defines.SaveDeepIntegrity)
            using (SimProfiler.Measure("  save:integrity"))
                foreach (var issue in NumericIntegrity.Inspect(payload))
                    if (!issues.Contains(issue)) issues.Add(issue);
        if (campaign.SimulationFault.Length > 0) issues.Insert(0, campaign.SimulationFault);
        if (issues.Count == 0) return;

        campaign.IsPaused = true;
        payload.EnsureRelationsCaptured(campaign);
        var diagnostic = TryWriteDiagnostic(root, payload, issues);
        throw new CampaignIntegrityException(string.Join("; ", issues), diagnostic);
    }

    private static SaveMetadata BuildSaveMetadata(
        string slot,
        string displayName,
        Campaign campaign,
        int provinceCount,
        string modFingerprint,
        bool autosave) => new()
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
        MapId = string.IsNullOrWhiteSpace(campaign.MapId) ? MapCatalog.StandardId : campaign.MapId,
        ProvinceCount = provinceCount,
        ModFingerprint = modFingerprint ?? "",
        IsAutosave = autosave
    };

    private static Dictionary<int, List<NationHistorySample>> ExtractAndValidateHistory(
        string root,
        Campaign campaign,
        CampaignSave payload)
    {
        var history = payload.History;
        payload.History = new Dictionary<int, List<NationHistorySample>>();
        try
        {
            if (history is not null)
                foreach (var series in history.Values)
                {
                    if (series is null) continue;
                    for (var i = 0; i < series.Count; i++)
                        if (!NationHistorySampleConverter.AllFinite(series[i]))
                            throw new ArgumentException("History contains a non-finite number.");
                }
            return history;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            if (history is not null) payload.History = history;
            payload.EnsureRelationsCaptured(campaign);
            var issues = new List<string>();
            foreach (var issue in NumericIntegrity.Inspect(payload))
                if (!issues.Contains(issue)) issues.Add(issue);
            campaign.IsPaused = true;
            var diagnostic = TryWriteDiagnostic(root, payload, issues);
            var message = issues.Count > 0 ? string.Join("; ", issues) : exception.Message;
            throw new CampaignIntegrityException(message, diagnostic);
        }
    }

    /// <summary>A diagnostic snapshot back as a campaign - for a bench, never for play.</summary>
    public static Campaign ReadDiagnosticSnapshot(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var body = document.RootElement.GetProperty("Campaign");
        if (body.TryGetProperty("StringTable", out var table) && table.ValueKind == JsonValueKind.Array)
            SaveStringTable.Use(table.EnumerateArray().Select(entry => StringPool.Shared.GetOrAdd(entry.GetString() ?? "")).ToArray());
        var options = new JsonSerializerOptions(JsonOptions)
        { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var payload = body.Deserialize<CampaignSave>(options);
        return payload?.Restore();
    }

    public static string WriteDiagnosticSnapshot(string root, Campaign campaign, string issue, Exception failure = null)
    {
        if (campaign?.ProvinceMap is null) return "";
        try
        {
            return TryWriteDiagnostic(root, CampaignSave.Capture(campaign, ""), new List<string> { issue }, failure);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DiagnosticLogs.Write("errors", "CAMPAIGN_DIAGNOSTIC_CAPTURE_FAILED " + exception);
            return "";
        }
    }

    private static string TryWriteDiagnostic(string root, CampaignSave payload, List<string> issues, Exception failure = null)
    {
        try
        {
            if (payload?.StringTable is { Length: > 0 }) SaveStringTable.Use(payload.StringTable);
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
                ExceptionType = failure?.GetType().FullName,
                StackTrace = failure?.StackTrace,
                ExceptionDetails = failure?.ToString(),
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

    private static ReadOnlySpan<byte> TextHistoryTailLf => "\"History\": {}\n}"u8;
    private static ReadOnlySpan<byte> TextHistoryTailCrLf => "\"History\": {}\r\n}"u8;

    /// <summary>A save that has been taken but not yet written. See <see cref="Prepare"/>.</summary>
    public sealed class PreparedSave
    {
        private readonly string _root;
        private readonly string _slot;
        private readonly CampaignSave _payload;
        private readonly Dictionary<int, List<NationHistorySample>> _history;
        private readonly bool _text;
        private readonly bool _thorough;

        internal PreparedSave(string root, string slot, CampaignSave payload,
            Dictionary<int, List<NationHistorySample>> history, SaveMetadata metadata, bool text,
            bool thorough = false)
        {
            _root = root;
            _slot = slot;
            _payload = payload;
            _history = history;
            _text = text;
            _thorough = thorough;
            Metadata = metadata;
        }

        public SaveMetadata Metadata { get; }

        public SaveMetadata Commit()
        {
            Directory.CreateDirectory(_root);
            return _text ? CommitText() : CommitBinary();
        }

        private SaveMetadata CommitText()
        {
            var options = TextOptions;
            var interned = SaveStringTable.InternIds;
            SaveStringTable.Use(_payload.StringTable);
            SaveStringTable.InternIds = false;
            try
            {
                long jsonBytes = 0;
                AtomicWrite(DataPath(_root, _slot), file =>
                {
                    JsonSerializer.Serialize(file, _payload, options);
                    if (_history is not null && !TryWriteTextHistory(file))
                    {
                        // The history field is normally last. A different JSON contract can
                        // still write the same data with the regular serializer.
                        _payload.History = _history;
                        file.SetLength(0);
                        file.Position = 0;
                        JsonSerializer.Serialize(file, _payload, options);
                    }
                    jsonBytes = file.Position;
                });

                Metadata.Checksum = "";
                Metadata.CompressedBytes = jsonBytes;
                Metadata.FormatLabel = "JSON";
                RemoveLegacyVariants(_root, _slot);
                DiagnosticLogs.Write("system", $"SAVE_END slot={_slot} text=true bytes={jsonBytes} format={FormatVersion} map={MapDataVersion}");
                return Metadata;
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                throw ThrowIntegrityFailure(exception);
            }
            finally
            {
                SaveStringTable.InternIds = interned;
            }
        }

        private bool TryWriteTextHistory(FileStream file)
        {
            var end = file.Position;
            if (end < TextHistoryTailLf.Length) return false;
            Span<byte> tail = stackalloc byte[TextHistoryTailCrLf.Length];
            tail = tail[..(int)Math.Min(tail.Length, end)];
            file.Position = end - tail.Length;
            file.ReadExactly(tail);
            var crlf = tail.EndsWith(TextHistoryTailCrLf);
            if (!crlf && !tail.EndsWith(TextHistoryTailLf)) return false;

            // Replace the empty object in-place, keeping the large ledger compact and the
            // surrounding save indented. The file is still the unpublished atomic-write temp.
            file.Position = end - (crlf ? "{}\r\n}"u8.Length : "{}\n}"u8.Length);
            JsonSerializer.Serialize(file, _history, JsonOptions);
            file.Write(crlf ? "\r\n}"u8 : "\n}"u8);
            return true;
        }

        private SaveMetadata CommitBinary()
        {
            try
            {
                long bytes = 0;
                var checksum = "";
                AtomicWrite(DataPath(_root, _slot), file =>
                    (bytes, checksum) = WriteBinary(file, _payload, _history, StorageCodec, StorageLevel(_thorough)));
                Metadata.Checksum = checksum;
                Metadata.CompressedBytes = bytes;
                Metadata.FormatLabel = "DOF3";
                RemoveLegacyVariants(_root, _slot);
                DiagnosticLogs.Write("system", $"SAVE_END slot={_slot} bytes={bytes} container=BDF3 codec={StorageCodec} " +
                                               $"level={StorageLevel(_thorough)} checksum={checksum[..Math.Min(16, checksum.Length)]} " +
                                               $"format={FormatVersion} map={MapDataVersion}");
                return Metadata;
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                throw ThrowIntegrityFailure(exception);
            }
        }

        private static void RemoveLegacyVariants(string root, string slot)
        {
            Stale(PreviousDfPath(root, slot));
            Stale(PreviousDfPath(root, slot) + ".bak");
            Stale(LegacyBdfPath(root, slot));
            Stale(LegacyBdfPath(root, slot) + ".bak");
            Stale(LegacyTextPath(root, slot));
            Stale(MetaPath(root, slot));
        }

        private CampaignIntegrityException ThrowIntegrityFailure(Exception exception)
        {
            if (_history is not null) _payload.History = _history;
            var issues = new List<string>();
            foreach (var issue in NumericIntegrity.Inspect(_payload))
                if (!issues.Contains(issue)) issues.Add(issue);
            var diagnostic = TryWriteDiagnostic(_root, _payload, issues);
            var message = issues.Count > 0 ? string.Join("; ", issues) : exception.Message;
            return new CampaignIntegrityException(message, diagnostic);
        }

        private static void Stale(string path)
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
        SaveStringTable.Use(payload.StringTable);

        using var jsonBuffer = new ArrayPoolBufferWriter<byte>(DefaultIoBufferBytes);
        var writerOptions = new JsonWriterOptions
        {
            Encoder = JsonOptions.Encoder,
            MaxDepth = JsonOptions.MaxDepth
        };
        using (var writer = new Utf8JsonWriter(jsonBuffer, writerOptions))
            JsonSerializer.Serialize(writer, payload, JsonOptions);

        using var compressedBuffer = new ArrayPoolBufferWriter<byte>(Math.Max(ScanIoBufferBytes, jsonBuffer.WrittenCount / 4));
        using (var gzip = new GZipStream(compressedBuffer.AsStream(), CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(jsonBuffer.WrittenSpan);
        return compressedBuffer.WrittenSpan.ToArray();
    }

    public static SaveLoadResult ImportNetworkSnapshot(byte[] compressed, string activeModFingerprint)
    {
        if (compressed is null || compressed.Length == 0)
            return new SaveLoadResult(null, null, "The host sent an empty campaign snapshot.");
        try
        {
            var json = DecodeJson(compressed);
            SaveStringTable.Use(ScanStringTable(json));
            var payload = JsonSerializer.Deserialize<CampaignSave>(json.AsSpan(), JsonOptions);
            if (payload is null || payload.FormatVersion != FormatVersion ||
                !string.Equals(payload.MapDataVersion, MapDataVersion, StringComparison.Ordinal) ||
                payload.ProvinceCount <= 0 || payload.ProvinceController.Length != payload.ProvinceCount + 1 ||
                payload.ProvinceOccupation.Length != payload.ProvinceCount + 1)
                return new SaveLoadResult(null, null, "Host campaign uses incompatible save or map data.");
            var requestedMapId = MapCatalog.ResolveSaveMapId(payload.MapId, payload.ProvinceCount, MapCatalog.ActiveMapId);
            if (!string.IsNullOrWhiteSpace(requestedMapId))
            {
                if (!MapCatalog.IsAvailable(requestedMapId))
                    return new SaveLoadResult(null, null,
                        Loc.Format("This save requires map pack '{0}', which is not available.", requestedMapId));
                MapCatalog.SetActiveMap(requestedMapId);
                payload.MapId = requestedMapId;
            }
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

    public static SaveLoadResult Load(string root, string slot, string activeModFingerprint, int activeProvinceCount = 0)
    {
        slot = SafeSlot(slot);
        var dataPath = ExistingDataPath(root, slot);
        if (!File.Exists(dataPath)) return new SaveLoadResult(null, null, "Save slot is empty.");
        using var file = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            DefaultIoBufferBytes, FileOptions.SequentialScan);
        var storedLength = file.Length;
        var metadata = ReadMetadata(root, slot);
        var detectedFormat = DetectContainerFormat(file);

        if (!TryReadValidatedPayload(file, dataPath, metadata, out var payload, out var failureResult))
            return failureResult;
        if (!ValidateLoadedPayload(payload, metadata, activeProvinceCount, out var validationError))
            return new SaveLoadResult(null, metadata, validationError);

        metadata = payload.EmbeddedMetadata ?? metadata;
        if (metadata is not null)
        {
            metadata.MapId = MapCatalog.ResolveSaveMapId(
                !string.IsNullOrWhiteSpace(metadata.MapId) ? metadata.MapId : payload.MapId,
                metadata.ProvinceCount > 0 ? metadata.ProvinceCount : payload.ProvinceCount);
            metadata.CompressedBytes = storedLength;
            metadata.FormatLabel = detectedFormat;
        }
        return FinalizeLoadedCampaign(payload, metadata, activeModFingerprint);
    }

    private static bool TryReadValidatedPayload(FileStream file, string dataPath, SaveMetadata metadata,
        out CampaignSave payload, out SaveLoadResult failureResult)
    {
        payload = null;
        failureResult = default;
        var first = file.ReadByte();
        var second = file.ReadByte();
        file.Position = 0;

        if (first == GzipMagic0 && second == GzipMagic1 && metadata is not null && metadata.Checksum.Length > 0)
        {
            var checksum = Convert.ToHexString(SHA256.HashData(file));
            file.Position = 0;
            if (!string.Equals(metadata.Checksum, checksum, StringComparison.OrdinalIgnoreCase) &&
                ReadEmbeddedMetadata(dataPath) is null)
            {
                failureResult = new SaveLoadResult(null, metadata, "Save checksum mismatch; the file may be incomplete or corrupted.");
                return false;
            }
        }

        try
        {
            if (first == 'B' && second == 'D' && IsFramed(file))
                payload = ReadBinary(file);
            else
            {
                var scan = OpenPayloadStream(file, leaveOpen: true);
                try { SaveStringTable.Use(SaveStringTableScanner.Read(scan)); }
                finally { if (!ReferenceEquals(scan, file)) scan.Dispose(); }
                file.Position = 0;
                using var source = OpenPayloadStream(file);
                payload = JsonSerializer.Deserialize<CampaignSave>(source, JsonOptions);
            }
            return true;
        }
        catch (Exception exception)
        {
            failureResult = new SaveLoadResult(null, metadata, $"Could not read save: {exception.Message}");
            return false;
        }
    }

    private static bool ValidateLoadedPayload(
        CampaignSave payload,
        SaveMetadata metadata,
        int activeProvinceCount,
        out string error)
    {
        if (payload is null || payload.FormatVersion > FormatVersion)
        {
            error = "This save was created by a newer, unsupported format.";
            return false;
        }
        if (payload.FormatVersion < FormatVersion)
        {
            error = "This save predates the current map topology and cannot be restored safely.";
            return false;
        }
        if (!string.Equals(payload.MapDataVersion, MapDataVersion, StringComparison.Ordinal))
        {
            error = $"This save uses map data '{payload.MapDataVersion}', but the active map is '{MapDataVersion}'.";
            return false;
        }
        var rawMapId = !string.IsNullOrWhiteSpace(payload.MapId) ? payload.MapId : (payload.EmbeddedMetadata?.MapId ?? metadata?.MapId);
        var countHint = payload.ProvinceCount > 0 ? payload.ProvinceCount : (metadata?.ProvinceCount ?? 0);
        var requestedMapId = MapCatalog.ResolveSaveMapId(rawMapId, countHint, MapCatalog.ActiveMapId);
        var mapSwitched = false;
        if (!string.IsNullOrWhiteSpace(requestedMapId))
        {
            if (!MapCatalog.IsAvailable(requestedMapId))
            {
                error = Loc.Format("This save requires map pack '{0}', which is not available.", requestedMapId);
                return false;
            }
            var previousMapId = MapCatalog.ActiveMapId;
            MapCatalog.SetActiveMap(requestedMapId);
            mapSwitched = !string.Equals(previousMapId, MapCatalog.ActiveMapId, StringComparison.Ordinal);
            payload.MapId = requestedMapId;
        }
        var expectedCount = mapSwitched
            ? (MapCatalog.TryGet(MapCatalog.ActiveMapId, out var switchedPack) ? switchedPack.CompiledProvinceCount : 0)
            : activeProvinceCount;
        if (expectedCount > 0 && payload.ProvinceCount > 0 && payload.ProvinceCount != expectedCount)
        {
            error = Loc.Format(
                "This save was made on another map ({0:N0} provinces; this map has {1:N0}). Start a new campaign.",
                payload.ProvinceCount, expectedCount);
            return false;
        }
        if (payload.ProvinceCount <= 0 ||
            payload.ProvinceController.Length != payload.ProvinceCount + 1 ||
            payload.ProvinceOccupation.Length != payload.ProvinceCount + 1)
        {
            error = "The save contains an incomplete province-control snapshot.";
            return false;
        }
        error = null;
        return true;
    }

    private static SaveLoadResult FinalizeLoadedCampaign(CampaignSave payload, SaveMetadata metadata, string activeModFingerprint)
    {
        try
        {
            var campaign = payload.Restore();
            var warning = string.Equals(payload.ModFingerprint ?? "", activeModFingerprint ?? "", StringComparison.Ordinal)
                ? ""
                : "Loaded with a different mod set; unknown content may use base definitions.";
            SecessionColours.MigrateLegacy(campaign);
            NationNaming.MigrateLegacy(campaign);
            WorldResourceMigration.Apply(campaign);
            foreach (var n in campaign.Nations) TacticalDoctrines.EnsureShelves(n);
            campaign.RepairDiminishedRestorationsAfterAttach = true;
            foreach (var n in campaign.Nations)
                if (n.Movement is not null && campaign.HasOwnedLand(n))
                    MovementRules.SynchroniseTerritory(campaign, n);
            var returned = MovementFall.ReturnTheLanded(campaign);
            if (returned > 0) DiagnosticLogs.Write("simulation", $"SAVE_REPAIR fallen_with_ground={returned}");
            Factions.SettleDefenselessRebelRemnants(campaign);
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
        var bySlot = new Dictionary<string, (string Path, DateTime WriteUtc)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*.*fsave*", SearchOption.TopDirectoryOnly))
        {
            if (!IsSavePayloadPath(path))
                continue;
            var slot = SlotFromPath(path);
            var writeUtc = File.GetLastWriteTimeUtc(path);
            if (!bySlot.TryGetValue(slot, out var existing) || writeUtc > existing.WriteUtc ||
                (writeUtc == existing.WriteUtc && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)))
                bySlot[slot] = (path, writeUtc);
        }
        if (bySlot.Count == 0) return Array.Empty<SaveMetadata>();

        var entries = new List<SaveMetadata>(bySlot.Count);
        foreach (var (slot, info) in bySlot)
        {
            var metadata = ReadEmbeddedMetadata(info.Path) ?? ReadMetadata(root, slot) ??
                           ReadEmbeddedMetadataAnywhere(info.Path);
            if (metadata is null) continue;
            metadata.Slot = slot;
            metadata.MapId = MapCatalog.ResolveSaveMapId(metadata.MapId, metadata.ProvinceCount);
            metadata.CompressedBytes = new FileInfo(info.Path).Length;
            metadata.FormatLabel = DetectContainerFormat(info.Path);
            entries.Add(metadata);
        }
        return entries.AsValueEnumerable()
            .OrderByDescending(static item => item.SavedUtc)
            .ToArray();
    }

    private static bool IsSavePayloadPath(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(PreviousExtension, StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".dofsave.json", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".dfsave.json", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(LegacyJsonExtension, StringComparison.OrdinalIgnoreCase);

    public static string SlotFromPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".dofsave.json", StringComparison.OrdinalIgnoreCase))
            return name[..^".dofsave.json".Length];
        if (name.EndsWith(".dfsave.json", StringComparison.OrdinalIgnoreCase))
            return name[..^".dfsave.json".Length];
        if (name.EndsWith(LegacyJsonExtension, StringComparison.OrdinalIgnoreCase))
            return name[..^LegacyJsonExtension.Length];
        if (name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            return name[..^Extension.Length];
        if (name.EndsWith(PreviousExtension, StringComparison.OrdinalIgnoreCase))
            return name[..^PreviousExtension.Length];
        if (name.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase))
            return name[..^LegacyExtension.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    private static string DetectContainerFormat(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64);
            return DetectContainerFormat(file);
        }
        catch { return ""; }
    }

    private static string DetectContainerFormat(Stream file)
    {
        if (!file.CanSeek) return "";
        var pos = file.Position;
        file.Position = 0;
        Span<byte> prefix = stackalloc byte[4];
        var read = file.Read(prefix);
        file.Position = pos;
        if (read == 4 && prefix[0] == (byte)'B' && prefix[1] == (byte)'D' && prefix[2] == (byte)'F')
        {
            if (prefix[3] == (byte)'3') return "DOF3";
            if (prefix[3] == (byte)'2') return "BROTLI";
        }
        if (read >= 2 && prefix[0] == GzipMagic0 && prefix[1] == GzipMagic1) return "GZIP";
        return "JSON";
    }

    /// <summary>Read only the header of a save, not a campaign-sized JSON tree, to list a slot.</summary>
    private static SaveMetadata ReadEmbeddedMetadata(string path)
    {
        byte[] buffer = null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                ScanIoBufferBytes, FileOptions.SequentialScan);
            using Stream source = OpenPayloadStream(file);
            buffer = ArrayPool<byte>.Shared.Rent(DefaultIoBufferBytes);
            var count = FillScanBuffer(source, buffer, 0, out var eof);
            if (count == 0) return null;
            if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            {
                Buffer.BlockCopy(buffer, 3, buffer, 0, count - 3);
                count -= 3;
            }
            return ScanForEmbeddedMetadata(source, ref buffer, count, eof);
        }
        catch { return null; }
        finally { if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static SaveMetadata ScanForEmbeddedMetadata(Stream source, ref byte[] buffer, int count, bool eof)
    {
        var totalScanned = 0;
        var state = default(JsonReaderState);
        var depth = 0;
        var expectingMetadata = false;

        while (totalScanned < MaxHeaderScanBytes && count > 0)
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(0, count), isFinalBlock: eof, state);
            while (reader.Read())
            {
                var token = reader.TokenType;
                if (depth == 1 && expectingMetadata)
                    return TryReadMetadataObject(source, ref buffer, count, eof, token, (int)reader.TokenStartIndex);

                if (token == JsonTokenType.StartObject || token == JsonTokenType.StartArray) depth++;
                else if (token == JsonTokenType.EndObject || token == JsonTokenType.EndArray) { if (--depth <= 0) return null; }
                else if (depth == 1 && token == JsonTokenType.PropertyName)
                    expectingMetadata = reader.ValueTextEquals("EmbeddedMetadata"u8);
            }

            var consumed = (int)reader.BytesConsumed;
            totalScanned += consumed;
            state = reader.CurrentState;
            if (!AdvanceScanBuffer(source, ref buffer, consumed, ref count, ref eof)) break;
        }
        return null;
    }

    private static SaveMetadata TryReadMetadataObject(
        Stream source,
        ref byte[] buffer,
        int count,
        bool eof,
        JsonTokenType token,
        int objectStart)
    {
        if (token != JsonTokenType.StartObject) return null;
        var tail = count - objectStart;
        if (objectStart > 0 && tail > 0) Buffer.BlockCopy(buffer, objectStart, buffer, 0, tail);
        count = tail;
        while (true)
        {
            var metaReader = new Utf8JsonReader(buffer.AsSpan(0, count), isFinalBlock: eof, state: default);
            if (metaReader.Read() && JsonDocument.TryParseValue(ref metaReader, out var doc))
                using (doc) return doc.RootElement.Deserialize<SaveMetadata>(MetadataOptions);
            if (eof || count >= MaxEmbeddedMetadataBytes) return null;
            if (count == buffer.Length) buffer = GrowScanBuffer(buffer, count);
            count = FillScanBuffer(source, buffer, count, out eof);
        }
    }

    private static bool AdvanceScanBuffer(Stream source, ref byte[] buffer, int consumed, ref int count, ref bool eof)
    {
        var leftover = count - consumed;
        if (eof)
        {
            if (leftover == 0 || consumed == 0) return false;
            Buffer.BlockCopy(buffer, consumed, buffer, 0, leftover);
            count = leftover;
            return true;
        }
        if (leftover > 0 && consumed > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, leftover);
        if (leftover == buffer.Length)
        {
            if (buffer.Length >= MaxHeaderScanBytes) return false;
            buffer = GrowScanBuffer(buffer, leftover);
        }
        count = FillScanBuffer(source, buffer, leftover, out eof);
        return true;
    }

    private static int FillScanBuffer(Stream stream, byte[] buf, int offset, out bool atEof)
    {
        atEof = false;
        var total = offset;
        while (total < buf.Length)
        {
            var read = stream.Read(buf, total, buf.Length - total);
            if (read == 0) { atEof = true; break; }
            total += read;
        }
        return total;
    }

    private static byte[] GrowScanBuffer(byte[] current, int validBytes)
    {
        var next = ArrayPool<byte>.Shared.Rent(current.Length * 2);
        Buffer.BlockCopy(current, 0, next, 0, validBytes);
        ArrayPool<byte>.Shared.Return(current);
        return next;
    }

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

    /// <summary>The JSON behind whichever framing a save or snapshot arrived in.</summary>
    private static byte[] DecodeJson(byte[] stored)
    {
        if (IsBrotli(stored))
        {
            using var input = RecyclableStreams.FromBytes("SaveSystem.DecodeJson.Brotli", stored.AsSpan(4));
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new ArrayPoolBufferWriter<byte>(Math.Max(DefaultIoBufferBytes, stored.Length * 4));
            brotli.CopyTo(output.AsStream());
            return output.WrittenSpan.ToArray();
        }
        if (IsGzip(stored))
        {
            using var input = RecyclableStreams.FromBytes("SaveSystem.DecodeJson.Gzip", stored);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new ArrayPoolBufferWriter<byte>(Math.Max(DefaultIoBufferBytes, stored.Length * 4));
            gzip.CopyTo(output.AsStream());
            return output.WrittenSpan.ToArray();
        }
        return stored;
    }

    private static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> utf8) =>
        utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF ? utf8[3..] : utf8;

    private static string[] ScanStringTable(ReadOnlySpan<byte> json)
    {
        try
        {
            json = StripUtf8Bom(json);
            var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return Array.Empty<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) { reader.Skip(); continue; }
                if (!reader.ValueTextEquals("StringTable"u8))
                {
                    reader.Skip();
                    continue;
                }
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return Array.Empty<string>();
                var values = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String) continue;
                    var interned = !reader.HasValueSequence && !reader.ValueIsEscaped
                        ? (reader.ValueSpan.IsEmpty ? "" : StringPool.Shared.GetOrAdd(reader.ValueSpan, Encoding.UTF8))
                        : StringPool.Shared.GetOrAdd(reader.GetString() ?? "");
                    values.Add(interned);
                }
                return values.ToArray();
            }
        }
        catch { }
        return Array.Empty<string>();
    }

    private static bool IsFramed(FileStream file)
    {
        if (file.Length < 4) return false;
        Span<byte> magic = stackalloc byte[4];
        file.Position = 0;
        file.ReadExactly(magic);
        file.Position = 0;
        return SaveContainer.IsContainer(magic);
    }

    private static bool IsGzip(byte[] stored) =>
        stored is { Length: >= 2 } && stored[0] == GzipMagic0 && stored[1] == GzipMagic1;

    private const string BrotliMagic = "BDF2";

    internal static CompressionLevel StorageCompression(bool thorough) =>
        thorough ? CompressionLevel.SmallestSize : CompressionLevel.Fastest;

    internal const byte StorageCodec = SaveContainer.BrotliCodec;
    internal static int StorageLevel(bool thorough) => thorough ? 11 : 1;

    internal static (long Bytes, string Checksum) WriteBinary(Stream file, CampaignSave payload,
        Dictionary<int, List<NationHistorySample>> history, byte codec, int level)
    {
        var interned = SaveStringTable.InternIds;
        var own = payload.History;
        history ??= own;
        payload.History = new Dictionary<int, List<NationHistorySample>>();
        SaveStringTable.Use(payload.StringTable);
        try
        {
            return SaveContainer.Write(file, graph => JsonSerializer.Serialize(graph, payload, JsonOptions),
                history, codec, level, FormatVersion);
        }
        finally
        {
            payload.History = own;
            SaveStringTable.InternIds = interned;
        }
    }

    internal static CampaignSave ReadBinary(Stream file)
    {
        var start = file.Position;
        var header = SaveContainer.ReadHeader(file);
        using (var scan = SaveContainer.OpenGraph(file, header, leaveOpen: true))
            SaveStringTable.Use(SaveStringTableScanner.Read(scan));
        file.Position = start;
        var (payload, history) = SaveContainer.Read(file, header,
            graph => JsonSerializer.Deserialize<CampaignSave>(graph, JsonOptions));
        payload.History = history;
        return payload;
    }

    internal static Stream OpenJson(FileStream file) => OpenPayloadStream(file);

    private static bool IsBrotli(byte[] stored) =>
        stored is { Length: >= 4 } && Encoding.ASCII.GetString(stored, 0, 4) == BrotliMagic;

    private static Stream OpenPayloadStream(FileStream file, bool leaveOpen = false)
    {
        Span<byte> prefix = stackalloc byte[4];
        var read = file.Read(prefix);
        file.Position = 0;
        if (read == 4 && prefix[0] == (byte)'B' && prefix[1] == (byte)'D' && prefix[2] == (byte)'F' && prefix[3] == (byte)'3')
            return SaveContainer.OpenGraph(file, SaveContainer.ReadHeader(file), leaveOpen);
        if (read >= 2 && prefix[0] == GzipMagic0 && prefix[1] == GzipMagic1)
            return new GZipStream(file, CompressionMode.Decompress, leaveOpen);
        if (read == 4 && prefix[0] == (byte)'B' && prefix[1] == (byte)'D' && prefix[2] == (byte)'F' && prefix[3] == (byte)'2')
        {
            file.Position = 4;
            return new BrotliStream(file, CompressionMode.Decompress, leaveOpen);
        }
        return file;
    }

    public static bool Delete(string root, string slot)
    {
        slot = SafeSlot(slot);
        var removed = false;
        foreach (var path in new[]
        {
            DataPath(root, slot),
            PreviousDfPath(root, slot),
            LegacyBdfPath(root, slot),
            LegacyTextPath(root, slot),
            MetaPath(root, slot)
        })
        {
            if (File.Exists(path)) { File.Delete(path); removed = true; }
        }
        foreach (var bak in new[]
        {
            DataPath(root, slot) + ".bak",
            PreviousDfPath(root, slot) + ".bak",
            LegacyBdfPath(root, slot) + ".bak"
        })
        {
            if (File.Exists(bak)) File.Delete(bak);
        }
        return removed;
    }

    private static SaveMetadata ReadMetadata(string root, string slot)
    {
        var path = MetaPath(root, slot);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SaveMetadata>(File.ReadAllText(path), MetadataOptions); }
        catch { return null; }
    }

    private static void AtomicWrite(string target, Action<FileStream> writeTo)
    {
        var temporary = target + ".tmp";
        try
        {
            using var file = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, DefaultIoBufferBytes);
            writeTo(file);
            file.Flush(true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        if (File.Exists(target)) File.Replace(temporary, target, target + ".bak");
        else File.Move(temporary, target);
    }

    private static string SafeSlot(string slot)
    {
        var safe = new string((slot ?? "").AsValueEnumerable()
            .Where(static character => char.IsLetterOrDigit(character) || character is '_' or '-')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safe)) throw new ArgumentException("Save slot has no valid characters.", nameof(slot));
        return safe.ToLowerInvariant();
    }

    private static string DataPath(string root, string slot) => Path.Combine(root, slot + Extension);
    private static string PreviousDfPath(string root, string slot) => Path.Combine(root, slot + PreviousExtension);
    private static string LegacyBdfPath(string root, string slot) => Path.Combine(root, slot + LegacyExtension);
    private static string LegacyTextPath(string root, string slot) => Path.Combine(root, slot + LegacyJsonExtension);

    private static string ExistingDataPath(string root, string slot)
    {
        string bestPath = null;
        DateTime bestWrite = DateTime.MinValue;
        foreach (var candidate in new[] { DataPath(root, slot), PreviousDfPath(root, slot), LegacyBdfPath(root, slot), LegacyTextPath(root, slot) })
        {
            if (!File.Exists(candidate)) continue;
            var writeUtc = File.GetLastWriteTimeUtc(candidate);
            if (bestPath is null || writeUtc > bestWrite)
            {
                bestPath = candidate;
                bestWrite = writeUtc;
            }
        }
        return bestPath ?? DataPath(root, slot);
    }

    private static string MetaPath(string root, string slot) => Path.Combine(root, slot + ".meta.json");
    private static string SlotTitle(string slot) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slot.Replace('_', ' '));

    internal sealed class CampaignSave
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyOrder(-30)]
        public SaveMetadata EmbeddedMetadata;
        public int FormatVersion = SaveSystem.FormatVersion;
        public string GameVersion = SaveSystem.GameVersion;
        public string MapDataVersion = SaveSystem.MapDataVersion;
        public string ModFingerprint = "";
        public int ProvinceCount;
        public int Seed;
        public bool ProductionUnitsSaved;
        public bool PrestigeSaved;
        public string ScenarioId = "2024";
        public string MapId = "";
        public string SkinOverlay = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CalendarProfile Calendar;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScenarioMetadata ScenarioMeta;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string> RestrictedFeatures;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<GrandProjectInstance> GrandProjects;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int NextGrandProjectId;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<WorldSituationInstance> WorldSituations;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int NextSituationId;
        [JsonPropertyOrder(-20)]
        public string[] StringTable = Array.Empty<string>();
        public Defines ScenarioRules;
        public Dictionary<string, bool> FeatureDefaults;
        public Dictionary<string, string> FeatureUnlocks;
        public Dictionary<string, string> FeatureCommands;
        public Dictionary<string, string> FeatureScreens;
        public Dictionary<string, string> FeatureSystems;
        public Dictionary<string, string> FeatureDefines;
        public Dictionary<string, bool> FeatureOverrides = new();
        public Dictionary<string, int[]> ScenarioTerritories = new();
        public GameDate Date;
        public int ElapsedDays;
        public bool ObserverMode;
        public string Curse;
        public int CurseDay = -1;
        public int CurseDrowned;
        public bool NoHumanAuthority;
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
        public int NextArmyGroupId;
        public List<Fleet> Fleets = new();
        public List<StrategicWar> Wars = new();
        public CivilWarBudgetState CivilWarBudget;
        public List<Sponsorship> Sponsorships = new();
        public List<InternationalOrganization> Organizations = new();
        public List<DealOffer> DealOffers = new();
        public int NextDealOfferId = 1;
        public List<UltimatumPaper> UltimatumPapers = new();
        public int NextUltimatumId = 1;
        public List<LandArrangement> LandArrangements = new();
        public int NextLandArrangementId = 1;
        public List<SeaLiftOrder> SeaLifts = new();
        public List<LeaseOffer> LeaseOffers = new();
        public int NextLeaseOfferId = 1;
        public float WorldLeverage = -1f;
        public int LastFinancialCrisisDay = InvalidDaySentinel;
        public int DefconLevel = DefconFallback;
        public int DefconDaysInStage;
        public int DefconPeakLevel = DefconFallback;
        public List<ForeignAssistanceRequest> AssistanceRequests = new();
        public List<ForeignAssistanceCooldown> AssistanceCooldowns = new();
        public int NextAssistanceRequestId = 1;
        public int AssistanceLastTickDay = -1;
        public List<ArmsShipmentRequest> ArmsShipmentRequests = new();
        public List<ArmsShipmentCooldown> ArmsShipmentCooldowns = new();
        public int NextArmsShipmentRequestId = 1;
        public Dictionary<int, List<int>> Homelands = new();
        public List<int> Fallen = new();
        public int NextOrganizationId = 1;
        public int NextSponsorshipId = 1;
        public List<Coalition> Coalitions = new();
        public List<DrawnFrontLine> FrontLines = new();
        public Dictionary<string, int> GoodIds = new();
        public Dictionary<string, int> BuildingIds = new();
        public int NextFrontLineId;
        public List<LandBattle> Battles = new();
        public List<ConstructionProject> Construction = new();
        public List<PendingCampaignEvent> PendingEvents = new();
        public List<ChronicleEntry> Chronicle = new();
        public Dictionary<long, DiplomaticRelation> Relations = new(PairKeyComparer.Instance);
        public int TradeRoutesDay = -1;
        public List<TradeRoute> TradeRoutes = new();
        [JsonPropertyOrder(1)]
        public Dictionary<int, List<NationHistorySample>> History = new();
        public HashSet<int> DestroyedProvinces = new();
        public HashSet<long> CrossoverScars = new();
        public List<CrossoverWork> CrossoverWorks = new();
        public List<CrossoverImpact> CrossoverImpacts = new();
        public ushort[] ProvinceController = Array.Empty<ushort>();
        public float[] ProvinceOccupation = Array.Empty<float>();
        public ushort[] ProvinceOwner = Array.Empty<ushort>();
        public ushort[] ProvinceState = Array.Empty<ushort>();

        public static CampaignSave Capture(Campaign campaign, string modFingerprint) =>
            Capture(campaign, modFingerprint, includeRelations: true);

        internal static CampaignSave Capture(Campaign campaign, string modFingerprint, bool includeRelations)
        {
            var save = CaptureCoreHeader(campaign, modFingerprint);
            PopulateCapturedCollections(save, campaign, includeRelations);
            return save;
        }

        private static CampaignSave CaptureCoreHeader(Campaign campaign, string modFingerprint) => new()
        {
            ModFingerprint = modFingerprint ?? "",
            ProvinceCount = campaign.ProvinceMap?.ProvinceCount ?? Math.Max(0, (campaign.ProvinceController?.Length ?? 1) - 1),
            Seed = campaign.Seed,
            ProductionUnitsSaved = true,
            PrestigeSaved = true,
            ScenarioId = campaign.ScenarioId,
            MapId = string.IsNullOrWhiteSpace(campaign.MapId) ? MapCatalog.StandardId : campaign.MapId,
            SkinOverlay = campaign.SkinOverlay ?? "",
            Calendar = campaign.Calendar is not null && !campaign.Calendar.IsDefault ? campaign.Calendar.Clone() : null,
            ScenarioMeta = campaign.ScenarioMeta is not null && !campaign.ScenarioMeta.IsEmpty ? campaign.ScenarioMeta.Clone() : null,
            RestrictedFeatures = campaign.RestrictedFeatures is { Count: > 0 }
                ? campaign.RestrictedFeatures.AsValueEnumerable().OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToList()
                : null,
            GrandProjects = campaign.GrandProjects.Count > 0 ? global::DealOrForce.Simulation.GrandProjects.CaptureSave(campaign) : null,
            NextGrandProjectId = campaign.GrandProjects.Count > 0 || campaign.NextGrandProjectId > 1 ? campaign.NextGrandProjectId : 0,
            WorldSituations = campaign.WorldSituations.Count > 0 ? global::DealOrForce.Simulation.WorldSituations.CaptureSave(campaign) : null,
            NextSituationId = campaign.WorldSituations.Count > 0 || campaign.NextSituationId > 1 ? campaign.NextSituationId : 0,
            StringTable = SaveStringTable.BuildForStates(campaign.States),
            ScenarioRules = campaign.ScenarioId != "2024" ? campaign.Defines : null,
            ScenarioTerritories = new Dictionary<string, int[]>(campaign.ScenarioTerritories),
            Date = campaign.Date,
            ElapsedDays = campaign.ElapsedDays,
            ObserverMode = campaign.ObserverMode,
            Curse = campaign.Curse.Length > 0 ? campaign.Curse : null,
            CurseDay = campaign.CurseDay,
            CurseDrowned = campaign.CurseDrowned,
            NoHumanAuthority = campaign.HumanSeats.Count == 0 && HasNoHumanNation(campaign.Nations),
            Settings = new Dictionary<string, string>(campaign.Settings),
            FeatureDefaults = new Dictionary<string, bool>(campaign.Defines.Features),
            FeatureUnlocks = new Dictionary<string, string>(campaign.Defines.FeatureUnlocks),
            FeatureCommands = new Dictionary<string, string>(campaign.Defines.FeatureCommands),
            FeatureScreens = new Dictionary<string, string>(campaign.Defines.FeatureScreens),
            FeatureSystems = new Dictionary<string, string>(campaign.Defines.FeatureSystems),
            FeatureDefines = new Dictionary<string, string>(campaign.Defines.FeatureDefines),
            FeatureOverrides = new Dictionary<string, bool>(campaign.FeatureOverrides),
            IsPaused = campaign.IsPaused,
            RandomState = campaign.Random.State,
            PlayerId = campaign.Player?.Id ?? 0,
            ActiveSeat = campaign.ActiveSeat,
            NextBattleId = campaign.NextBattleId,
            MapRevision = campaign.MapRevision,
            InitialDeploymentCompleted = campaign.InitialDeploymentCompleted,
            HumanSeats = new List<int>(campaign.HumanSeats)
        };

        private static void PopulateCapturedCollections(CampaignSave save, Campaign campaign, bool includeRelations)
        {
            save.Nations = new List<Nation>(campaign.Nations);
            save.States = new List<ScenarioState>(campaign.States);
            save.Armies = new List<Army>(campaign.Armies);
            save.Recruitment = new List<RecruitmentOrder>(campaign.Recruitment);
            save.NextRecruitmentId = campaign.NextRecruitmentId;
            save.SupportFormations = new List<SupportFormation>(campaign.SupportFormations);
            save.SupportOrders = new List<SupportOrder>(campaign.SupportOrders);
            save.NextSupportId = campaign.NextSupportId;
            save.ArmyGroups = new List<ArmyGroup>(campaign.ArmyGroups);
            save.NextArmyGroupId = campaign.NextArmyGroupId;
            save.Fleets = new List<Fleet>(campaign.Fleets);
            save.Wars = new List<StrategicWar>(campaign.Wars);
            save.CivilWarBudget = CivilWarOutbreak.Budget(campaign).Copy(campaign.ElapsedDays);
            save.Sponsorships = new List<Sponsorship>(campaign.Sponsorships);
            save.Organizations = new List<InternationalOrganization>(campaign.Organizations);
            save.DealOffers = CopyList(campaign.DealOffers, DealNegotiations.Copy);
            save.NextDealOfferId = campaign.NextDealOfferId;
            save.UltimatumPapers = CopyList(campaign.UltimatumPapers, Ultimatum.Copy);
            save.NextUltimatumId = campaign.NextUltimatumId;
            save.LandArrangements = new List<LandArrangement>(campaign.LandArrangements);
            save.SeaLifts = new List<SeaLiftOrder>(campaign.SeaLifts);
            save.NextLandArrangementId = campaign.NextLandArrangementId;
            save.LeaseOffers = new List<LeaseOffer>(campaign.LeaseOffers);
            save.NextLeaseOfferId = campaign.NextLeaseOfferId;
            save.WorldLeverage = campaign.WorldLeverage;
            save.LastFinancialCrisisDay = campaign.LastFinancialCrisisDay;
            save.DefconLevel = campaign.DefconLevel;
            save.DefconDaysInStage = campaign.DefconDaysInStage;
            save.DefconPeakLevel = campaign.DefconPeakLevel;
            save.AssistanceRequests = CopyList(campaign.AssistanceRequests, ForeignAssistance.Copy);
            save.AssistanceCooldowns = CopyList(campaign.AssistanceCooldowns, ForeignAssistance.Copy);
            save.NextAssistanceRequestId = campaign.NextAssistanceRequestId;
            save.AssistanceLastTickDay = campaign.AssistanceLastTickDay;
            save.ArmsShipmentRequests = CopyList(campaign.ArmsShipmentRequests, global::DealOrForce.Simulation.ArmsShipmentRequests.Copy);
            save.ArmsShipmentCooldowns = CopyList(campaign.ArmsShipmentCooldowns, global::DealOrForce.Simulation.ArmsShipmentRequests.Copy);
            save.NextArmsShipmentRequestId = campaign.NextArmsShipmentRequestId;
            save.Homelands = CopyHomelands(campaign.Homelands);
            save.Fallen = SortedFallen(campaign.Fallen);
            save.NextOrganizationId = campaign.NextOrganizationId;
            save.NextSponsorshipId = campaign.NextSponsorshipId;
            save.Coalitions = new List<Coalition>(campaign.Coalitions);
            save.FrontLines = new List<DrawnFrontLine>(campaign.FrontLines);
            save.NextFrontLineId = campaign.NextFrontLineId;
            save.Battles = new List<LandBattle>(campaign.Battles);
            save.Construction = new List<ConstructionProject>(campaign.Construction);
            save.PendingEvents = new List<PendingCampaignEvent>(campaign.PendingEvents);
            save.Chronicle = new List<ChronicleEntry>(campaign.Chronicle.Entries);
            if (includeRelations) save.Relations = AllRelations(campaign);
            save.TradeRoutesDay = campaign.TradeRoutesDay;
            save.TradeRoutes = CopyTradeRoutes(campaign.TradeRoutes);
            save.History = campaign.History.Export();
            save.GoodIds = CommodityIdTable();
            save.BuildingIds = BuildingIdTable();
            save.ProvinceController = campaign.ProvinceController is null ? Array.Empty<ushort>() : (ushort[])campaign.ProvinceController.Clone();
            save.ProvinceOccupation = campaign.ProvinceOccupation is null ? Array.Empty<float>() : (float[])campaign.ProvinceOccupation.Clone();
            save.DestroyedProvinces = new HashSet<int>(campaign.DestroyedProvinces);
            save.CrossoverScars = new HashSet<long>(campaign.CrossoverScars);
            save.CrossoverWorks = CopyCrossoverWorks(campaign.CrossoverWorks);
            save.CrossoverImpacts = new List<CrossoverImpact>(campaign.CrossoverImpacts);
            save.ProvinceOwner = campaign.ProvinceMap is null ? Array.Empty<ushort>() : (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone();
            save.ProvinceState = campaign.ProvinceMap is null ? Array.Empty<ushort>() : (ushort[])campaign.ProvinceMap.ProvinceToState.Clone();
        }

        private static Dictionary<long, DiplomaticRelation> AllRelations(Campaign campaign)
        {
            var all = new Dictionary<long, DiplomaticRelation>(
                campaign.Relations.Count + campaign.DormantRelations.Count, PairKeyComparer.Instance);
            foreach (var entry in campaign.Relations) all[entry.Key] = entry.Value;
            foreach (var entry in campaign.DormantRelations) all[entry.Key] = entry.Value;
            return all;
        }

        private static bool HasNoHumanNation(List<Nation> nations)
        {
            for (var i = 0; i < nations.Count; i++)
                if (nations[i].IsHuman) return false;
            return true;
        }

        private static List<T> CopyList<T>(List<T> source, Func<T, T> copy)
        {
            var result = new List<T>(source.Count);
            for (var i = 0; i < source.Count; i++) result.Add(copy(source[i]));
            return result;
        }

        private static Dictionary<int, List<int>> CopyHomelands(Dictionary<int, List<int>> source)
        {
            var result = new Dictionary<int, List<int>>(source.Count);
            foreach (var pair in source) result.Add(pair.Key, new List<int>(pair.Value));
            return result;
        }

        private static List<int> SortedFallen(HashSet<int> source)
        {
            var result = new List<int>(source);
            result.Sort();
            return result;
        }

        private static List<TradeRoute> CopyTradeRoutes(List<TradeRoute> source)
        {
            var result = new List<TradeRoute>(source.Count);
            for (var i = 0; i < source.Count; i++)
            {
                var route = source[i];
                result.Add(new TradeRoute
                {
                    SellerId = route.SellerId, BuyerId = route.BuyerId,
                    Commodity = route.Commodity, Volume = route.Volume, Value = route.Value
                });
            }
            return result;
        }

        private static List<CrossoverWork> CopyCrossoverWorks(List<CrossoverWork> source)
        {
            var result = new List<CrossoverWork>(source.Count);
            for (var i = 0; i < source.Count; i++) result.Add(source[i].Copy());
            return result;
        }

        internal void EnsureRelationsCaptured(Campaign campaign)
        {
            if (Relations.Count == 0 && campaign is not null &&
                (campaign.Relations.Count > 0 || campaign.DormantRelations.Count > 0))
                Relations = AllRelations(campaign);
        }

        internal void Isolate() => Isolate(null);

        internal void Isolate(Campaign sourceCampaign)
        {
            var visited = SaveSnapshotCloner.RentVisited();
            try
            {
                if (ScenarioRules is not null)
                    ScenarioRules = (Defines)SaveSnapshotCloner.CloneObject(ScenarioRules, visited);
                SaveSnapshotCloner.IsolateDictionaryValues(ScenarioTerritories, visited);
                SaveSnapshotCloner.IsolateListElements(Nations, visited);
                SaveSnapshotCloner.IsolateListElements(States, visited);
                SaveSnapshotCloner.IsolateListElements(Armies, visited);
                SaveSnapshotCloner.IsolateListElements(Recruitment, visited);
                SaveSnapshotCloner.IsolateListElements(SupportFormations, visited);
                SaveSnapshotCloner.IsolateListElements(SupportOrders, visited);
                SaveSnapshotCloner.IsolateListElements(ArmyGroups, visited);
                SaveSnapshotCloner.IsolateListElements(Fleets, visited);
                SaveSnapshotCloner.IsolateListElements(Wars, visited);
                if (CivilWarBudget is not null)
                    CivilWarBudget = (CivilWarBudgetState)SaveSnapshotCloner.CloneObject(CivilWarBudget, visited);
                SaveSnapshotCloner.IsolateListElements(Sponsorships, visited);
                SaveSnapshotCloner.IsolateListElements(Organizations, visited);
                SaveSnapshotCloner.IsolateListElements(DealOffers, visited);
                SaveSnapshotCloner.IsolateListElements(UltimatumPapers, visited);
                SaveSnapshotCloner.IsolateListElements(LandArrangements, visited);
                SaveSnapshotCloner.IsolateListElements(SeaLifts, visited);
                SaveSnapshotCloner.IsolateListElements(LeaseOffers, visited);
                SaveSnapshotCloner.IsolateListElements(AssistanceRequests, visited);
                SaveSnapshotCloner.IsolateListElements(AssistanceCooldowns, visited);
                SaveSnapshotCloner.IsolateListElements(ArmsShipmentRequests, visited);
                SaveSnapshotCloner.IsolateListElements(ArmsShipmentCooldowns, visited);
                SaveSnapshotCloner.IsolateListElements(Coalitions, visited);
                SaveSnapshotCloner.IsolateListElements(FrontLines, visited);
                SaveSnapshotCloner.IsolateListElements(Battles, visited);
                SaveSnapshotCloner.IsolateListElements(Construction, visited);
                SaveSnapshotCloner.IsolateListElements(PendingEvents, visited);
                SaveSnapshotCloner.IsolateListElements(Chronicle, visited);
                IsolateRelations(sourceCampaign, visited);
                SaveSnapshotCloner.IsolateListElements(CrossoverWorks, visited);
                SaveSnapshotCloner.IsolateListElements(CrossoverImpacts, visited);
                if (GrandProjects is not null) SaveSnapshotCloner.IsolateListElements(GrandProjects, visited);
                if (WorldSituations is not null) SaveSnapshotCloner.IsolateListElements(WorldSituations, visited);
            }
            finally
            {
                SaveSnapshotCloner.ReturnVisited(visited);
            }
        }

        private void IsolateRelations(Campaign sourceCampaign, Dictionary<object, object> visited)
        {
            if (Relations.Count == 0 && sourceCampaign is not null &&
                (sourceCampaign.Relations.Count > 0 || sourceCampaign.DormantRelations.Count > 0))
            {
                var cloned = new Dictionary<long, DiplomaticRelation>(
                    sourceCampaign.Relations.Count + sourceCampaign.DormantRelations.Count, PairKeyComparer.Instance);
                SaveSnapshotCloner.CopyClonedDictionaryValues(sourceCampaign.Relations, cloned, visited);
                SaveSnapshotCloner.CopyClonedDictionaryValues(sourceCampaign.DormantRelations, cloned, visited);
                Relations = cloned;
                return;
            }
            Relations = SaveSnapshotCloner.CloneDictionaryValues(Relations, visited);
        }

        public Campaign Restore()
        {
            var campaign = CreateRestoredShell();
            RestoreFeatureSettings(campaign);
            RestoreEntityCollections(campaign);
            RestoreDiplomacyAndHistory(campaign);
            NormalizeAfterRestore(campaign);
            return campaign;
        }

        private Campaign CreateRestoredShell()
        {
            var campaign = new Campaign(Seed, ScenarioRules)
            {
                ScenarioId = ScenarioId,
                MapId = MapCatalog.ResolveSaveMapId(MapId, ProvinceCount, MapCatalog.ActiveMapId),
                SkinOverlay = SkinOverlay ?? "",
                Calendar = Calendar is not null && !Calendar.IsDefault ? Calendar.Clone() : null,
                ScenarioMeta = ScenarioMeta is not null && !ScenarioMeta.IsEmpty ? ScenarioMeta.Clone() : null,
                RestrictedFeatures = RestrictedFeatures is { Count: > 0 }
                    ? new HashSet<string>(RestrictedFeatures, StringComparer.OrdinalIgnoreCase)
                    : null,
                ScenarioTerritories = new Dictionary<string, int[]>(ScenarioTerritories, StringComparer.OrdinalIgnoreCase)
            };
            if (GrandProjects is not null || NextGrandProjectId > 0)
                global::DealOrForce.Simulation.GrandProjects.RestoreSave(campaign, GrandProjects ?? new List<GrandProjectInstance>(), Math.Max(1, NextGrandProjectId));
            if (WorldSituations is not null || NextSituationId > 0)
                global::DealOrForce.Simulation.WorldSituations.RestoreSave(campaign, WorldSituations ?? new List<WorldSituationInstance>(), Math.Max(1, NextSituationId));
            return campaign;
        }

        private void RestoreFeatureSettings(Campaign campaign)
        {
            if (FeatureDefaults is not null) campaign.Defines.Features = new(FeatureDefaults, StringComparer.OrdinalIgnoreCase);
            if (FeatureUnlocks is not null) campaign.Defines.FeatureUnlocks = new(FeatureUnlocks, StringComparer.OrdinalIgnoreCase);
            if (FeatureCommands is not null) campaign.Defines.FeatureCommands = new(FeatureCommands, StringComparer.OrdinalIgnoreCase);
            if (FeatureScreens is not null) campaign.Defines.FeatureScreens = new(FeatureScreens, StringComparer.OrdinalIgnoreCase);
            if (FeatureSystems is not null) campaign.Defines.FeatureSystems = new(FeatureSystems, StringComparer.OrdinalIgnoreCase);
            if (FeatureDefines is not null) campaign.Defines.FeatureDefines = new(FeatureDefines, StringComparer.OrdinalIgnoreCase);
            if (FeatureOverrides is not null)
                foreach (var (id, enabled) in FeatureOverrides)
                    if (MechanicFeatures.ValidId(id)) campaign.FeatureOverrides[id] = enabled;
            MechanicFeatures.SyncDefines(campaign);
        }

        private void RestoreEntityCollections(Campaign campaign)
        {
            Replace(campaign.Nations, Nations);
            Replace(campaign.States, States);
            Replace(campaign.Armies, Armies);
            Replace(campaign.Recruitment, Recruitment);
            campaign.NextRecruitmentId = Math.Max(NextRecruitmentId, campaign.Recruitment.Count == 0 ? 1 : campaign.Recruitment.AsValueEnumerable().Max(static o => o.Id) + 1);
            Replace(campaign.SupportFormations, SupportFormations);
            Replace(campaign.SupportOrders, SupportOrders);
            campaign.NextSupportId = Math.Max(NextSupportId,
                campaign.SupportFormations.Count == 0 && campaign.SupportOrders.Count == 0
                    ? 1
                    : Math.Max(
                        campaign.SupportFormations.Count == 0 ? 0 : campaign.SupportFormations.AsValueEnumerable().Max(static f => f.Id),
                        campaign.SupportOrders.Count == 0 ? 0 : campaign.SupportOrders.AsValueEnumerable().Max(static o => o.Id)) + 1);
            Replace(campaign.ArmyGroups, ArmyGroups);
            campaign.NextArmyGroupId = Math.Max(0, NextArmyGroupId);
            Replace(campaign.Fleets, Fleets);
            Replace(campaign.Wars, Wars);
            Replace(campaign.Sponsorships, Sponsorships);
            Replace(campaign.Organizations, Organizations ?? new());
            DealNegotiations.Restore(campaign, DealOffers, NextDealOfferId, ElapsedDays);
            Ultimatum.Restore(campaign, UltimatumPapers, NextUltimatumId);
            Replace(campaign.LandArrangements, LandArrangements ?? new());
            Replace(campaign.SeaLifts, SeaLifts ?? new());
            Replace(campaign.LeaseOffers, LeaseOffers ?? new());
            campaign.NextLandArrangementId = Math.Max(NextLandArrangementId,
                campaign.LandArrangements.Count == 0 ? 1 : campaign.LandArrangements.AsValueEnumerable().Max(static a => a.Id) + 1);
            campaign.NextLeaseOfferId = Math.Max(NextLeaseOfferId,
                campaign.LeaseOffers.Count == 0 ? 1 : campaign.LeaseOffers.AsValueEnumerable().Max(static o => o.Id) + 1);
            campaign.NextOrganizationId = Math.Max(NextOrganizationId,
                campaign.Organizations.Count == 0 ? 1 : campaign.Organizations.AsValueEnumerable().Max(static o => o.Id) + 1);
            campaign.NextSponsorshipId = Math.Max(NextSponsorshipId,
                campaign.Sponsorships.Count == 0 ? 1 : campaign.Sponsorships.AsValueEnumerable().Max(static s => s.Id) + 1);
            Replace(campaign.Coalitions, Coalitions);
            Replace(campaign.FrontLines, FrontLines);
            campaign.NextFrontLineId = Math.Max(NextFrontLineId,
                campaign.FrontLines.Count == 0 ? 0 : campaign.FrontLines.AsValueEnumerable().Max(static line => line.Id) + 1);
            Replace(campaign.Battles, Battles);
            Replace(campaign.Construction, Construction);
            Replace(campaign.PendingEvents, PendingEvents);
        }

        private void RestoreDiplomacyAndHistory(Campaign campaign)
        {
            campaign.WorldLeverage = WorldLeverage;
            campaign.LastFinancialCrisisDay = LastFinancialCrisisDay;
            campaign.DefconLevel = DefconLevel > 0 ? DefconLevel : DefconFallback;
            campaign.DefconDaysInStage = DefconDaysInStage;
            campaign.DefconPeakLevel = DefconPeakLevel > 0 ? DefconPeakLevel : DefconFallback;
            campaign.Homelands.Clear();
            if (Homelands is not null)
                foreach (var (id, regions) in Homelands) campaign.Homelands[id] = new List<int>(regions ?? new());
            campaign.Fallen.Clear();
            if (Fallen is not null)
                foreach (var id in Fallen) campaign.Fallen.Add(id);

            campaign.Chronicle.Restore(Chronicle);
            campaign.Relations.Clear();
            campaign.DormantRelations.Clear();
            foreach (var entry in Relations)
                (entry.Value.Dormant ? campaign.DormantRelations : campaign.Relations)[entry.Key] = entry.Value;
            TradeTermsLifecycle.Normalize(campaign);
            campaign.TradeRoutesDay = TradeRoutesDay;
            Replace(campaign.TradeRoutes, TradeRoutes);
            campaign.History.Restore(History, campaign.Defines);
            campaign.ObserverMode = ObserverMode;
            campaign.Curse = Curse ?? "";
            campaign.CurseDay = CurseDay;
            campaign.CurseDrowned = CurseDrowned;
            foreach (var (id, value) in Settings) campaign.Settings[id] = value;
            if (DestroyedProvinces is not null) campaign.DestroyedProvinces.UnionWith(DestroyedProvinces);
            if (CrossoverScars is not null) campaign.CrossoverScars.UnionWith(CrossoverScars);
            if (CrossoverWorks is not null) campaign.CrossoverWorks.AddRange(CrossoverWorks.Select(w => w.Copy()));
            if (CrossoverImpacts is not null) campaign.CrossoverImpacts.AddRange(CrossoverImpacts);
            campaign.RestoreRuntimeState(Date, ElapsedDays, IsPaused, RandomState, PlayerId,
                HumanSeats, ActiveSeat, NextBattleId, MapRevision, InitialDeploymentCompleted,
                ProvinceController, ProvinceOccupation, ProvinceOwner, ProvinceState, NoHumanAuthority);
            ForeignAssistance.Restore(campaign, AssistanceRequests, AssistanceCooldowns,
                NextAssistanceRequestId, AssistanceLastTickDay);
            global::DealOrForce.Simulation.ArmsShipmentRequests.Restore(campaign, ArmsShipmentRequests, ArmsShipmentCooldowns, NextArmsShipmentRequestId);
            campaign.CivilWarBudget = CivilWarBudget?.Copy(campaign.ElapsedDays) ??
                CivilWarBudgetState.FromLegacy(campaign);
            if (CivilWarBudget is null) CivilWarOutbreak.RestoreMilitaryCoupMemory(campaign);
        }

        private void NormalizeAfterRestore(Campaign campaign)
        {
            campaign.InvalidateStateIndex();
            RevoltRecovery.RestoreLegacy(campaign);
            foreach (var loadedNation in campaign.Nations)
                if (!loadedNation.DebtInitialized) SovereignFinance.NormalizeAccounts(campaign, loadedNation);

            campaign.DeclareFoundedPeoples();
            foreach (var relation in campaign.Relations.Values)
                OpinionSystem.Explain(campaign, relation, updateOpinion: false);

            RemapContent(campaign);
            if (!ProductionUnitsSaved)
                foreach (var nation in campaign.Nations)
                    ProductionProgramme.MigrateLegacy(nation, ProductionProgramme.Allocate(campaign, nation));

            if (campaign.Nations.AsValueEnumerable().Any(static nation => nation.OutputScale <= 0f))
                EconomySystem.CalibrateOutput(campaign);

            if (campaign.Nations.AsValueEnumerable().All(static nation => nation.Budget.Title.Length == 0 &&
                nation.StabilityDrivers.Title.Length == 0))
                EconomySystem.Explain(campaign);
            BattleMovementOrders.Normalise(campaign);
            TacticalDoctrines.BindHomes(campaign);
            TradeNetwork.FileLoadedRoutes(campaign);
            global::DealOrForce.Simulation.DiseasesAndDisasters.NormalizeAfterLoad(campaign);
            global::DealOrForce.Simulation.PrestigeSystem.NormalizeAfterLoad(campaign, seedLegacy: !PrestigeSaved);
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

        private void RemapContent(Campaign campaign)
        {
            var goods = Moved(GoodIds, id => CommodityCatalog.TryParse(id, out var now) ? (int)now : -1);
            var buildings = Moved(BuildingIds, id => BuildingCatalog.TryParse(id, out var now) ? (int)now : -1);
            if (goods.Count == 0 && buildings.Count == 0) return;

            DiagnosticLogs.Write("system", $"SAVE_REMAP goods={goods.Count} buildings={buildings.Count}");
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
