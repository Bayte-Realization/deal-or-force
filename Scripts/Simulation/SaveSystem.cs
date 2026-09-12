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

public sealed class SaveMetadata
{
    public int FormatVersion { get; set; } = SaveSystem.FormatVersion;
    public string Slot { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string NationTag { get; set; } = "";
    public string NationName { get; set; } = "";
    public string GameDate { get; set; } = "";
    public int ElapsedDays { get; set; }
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
/// Versioned, compressed and atomic campaign persistence. Saves contain simulation
/// state only: presentation can change without invalidating a campaign. A readable
/// sidecar index lets the menu enumerate slots without inflating every full save.
/// </summary>
public static class SaveSystem
{
    public const int FormatVersion = 2;
    public const string GameVersion = "0.4-migration";
    public const string MapDataVersion = "2024.09.01-e";
    public const string QuickSlot = "quicksave";
    public const string AutoSlot = "autosave";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly JsonSerializerOptions MetadataOptions = new(JsonOptions) { WriteIndented = true };

    public static SaveMetadata Save(string root, string slot, Campaign campaign, string modFingerprint,
        string displayName = "", bool autosave = false)
    {
        using var timing = SimProfiler.Measure("save.write");
        DiagnosticLogs.Write("system", $"SAVE_BEGIN slot={SafeSlot(slot)} autosave={autosave} day={campaign?.ElapsedDays}");
        if (campaign?.Player is null) throw new InvalidOperationException("No active campaign to save.");
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null || campaign.ProvinceOccupation is null)
            throw new InvalidOperationException("The tactical map is still being prepared; try saving again in a moment.");
        Directory.CreateDirectory(root);
        slot = SafeSlot(slot);
        var payload = CampaignSave.Capture(campaign, modFingerprint);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        byte[] compressed;
        using (var memory = new MemoryStream())
        {
            using (var gzip = new GZipStream(memory, CompressionLevel.SmallestSize, true))
                gzip.Write(json);
            compressed = memory.ToArray();
        }

        var metadata = new SaveMetadata
        {
            Slot = slot,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? SlotTitle(slot) : displayName.Trim(),
            NationTag = campaign.Player.Tag,
            NationName = campaign.Player.Name,
            GameDate = campaign.Date.ToString(),
            ElapsedDays = campaign.ElapsedDays,
            SavedUtc = DateTime.UtcNow,
            MapDataVersion = MapDataVersion,
            ProvinceCount = payload.ProvinceCount,
            ModFingerprint = modFingerprint ?? "",
            Checksum = Convert.ToHexString(SHA256.HashData(compressed)),
            CompressedBytes = compressed.LongLength,
            IsAutosave = autosave
        };

        AtomicWrite(DataPath(root, slot), compressed);
        AtomicWrite(MetaPath(root, slot), JsonSerializer.SerializeToUtf8Bytes(metadata, MetadataOptions));
        DiagnosticLogs.Write("system", $"SAVE_END slot={slot} bytes={compressed.LongLength} checksum={metadata.Checksum} format={FormatVersion} map={MapDataVersion}");
        return metadata;
    }

    /// <summary>Compact authoritative snapshot used for joining and resynchronising LAN peers.</summary>
    public static byte[] ExportNetworkSnapshot(Campaign campaign, string modFingerprint)
    {
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
        var dataPath = DataPath(root, slot);
        if (!File.Exists(dataPath)) return new SaveLoadResult(null, null, "Save slot is empty.");
        var compressed = File.ReadAllBytes(dataPath);
        var metadata = ReadMetadata(root, slot);
        var checksum = Convert.ToHexString(SHA256.HashData(compressed));
        if (metadata is not null && !string.Equals(metadata.Checksum, checksum, StringComparison.OrdinalIgnoreCase))
            return new SaveLoadResult(null, metadata, "Save checksum mismatch; the file may be incomplete or corrupted.");

        CampaignSave payload;
        try
        {
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            payload = JsonSerializer.Deserialize<CampaignSave>(gzip, JsonOptions);
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

        try
        {
            var campaign = payload.Restore();
            var warning = string.Equals(payload.ModFingerprint ?? "", activeModFingerprint ?? "", StringComparison.Ordinal)
                ? ""
                : "Loaded with a different mod set; unknown content may use base definitions.";
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
        return Directory.EnumerateFiles(root, "*.meta.json", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                try { return JsonSerializer.Deserialize<SaveMetadata>(File.ReadAllText(path), MetadataOptions); }
                catch { return null; }
            })
            .Where(item => item is not null)
            .OrderByDescending(item => item.SavedUtc)
            .ToArray();
    }

    public static bool Delete(string root, string slot)
    {
        slot = SafeSlot(slot);
        var removed = false;
        foreach (var path in new[] { DataPath(root, slot), MetaPath(root, slot) })
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

    private static void AtomicWrite(string target, byte[] data)
    {
        var temporary = target + ".tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, target, true);
    }

    private static string SafeSlot(string slot)
    {
        var safe = new string((slot ?? "").Where(character => char.IsLetterOrDigit(character) || character is '_' or '-').ToArray());
        if (string.IsNullOrWhiteSpace(safe)) throw new ArgumentException("Save slot has no valid characters.", nameof(slot));
        return safe.ToLowerInvariant();
    }

    private static string DataPath(string root, string slot) => Path.Combine(root, slot + ".bdfsave");
    private static string MetaPath(string root, string slot) => Path.Combine(root, slot + ".meta.json");
    private static string SlotTitle(string slot) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slot.Replace('_', ' '));

    private sealed class CampaignSave
    {
        public int FormatVersion = SaveSystem.FormatVersion;
        public string GameVersion = SaveSystem.GameVersion;
        public string MapDataVersion = SaveSystem.MapDataVersion;
        public string ModFingerprint = "";
        public int ProvinceCount;
        public int Seed;
        public GameDate Date;
        public int ElapsedDays;
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
        public List<ArmyGroup> ArmyGroups = new();
        public List<Fleet> Fleets = new();
        public List<StrategicWar> Wars = new();

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
        public Dictionary<long, DiplomaticRelation> Relations = new();
        public Dictionary<int, List<NationHistorySample>> History = new();
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
            Date = campaign.Date,
            ElapsedDays = campaign.ElapsedDays,
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
            ArmyGroups = new List<ArmyGroup>(campaign.ArmyGroups),
            Fleets = new List<Fleet>(campaign.Fleets),
            Wars = new List<StrategicWar>(campaign.Wars),
            Coalitions = new List<Coalition>(campaign.Coalitions),
            FrontLines = new List<DrawnFrontLine>(campaign.FrontLines),
            NextFrontLineId = campaign.NextFrontLineId,
            Battles = new List<LandBattle>(campaign.Battles),
            Construction = new List<ConstructionProject>(campaign.Construction),
            PendingEvents = new List<PendingCampaignEvent>(campaign.PendingEvents),
            Relations = new Dictionary<long, DiplomaticRelation>(campaign.Relations),
            History = campaign.History.Export(),
            GoodIds = CommodityIdTable(),
            BuildingIds = BuildingIdTable(),
            ProvinceController = campaign.ProvinceController is null ? Array.Empty<ushort>() : (ushort[])campaign.ProvinceController.Clone(),
            ProvinceOccupation = campaign.ProvinceOccupation is null ? Array.Empty<float>() : (float[])campaign.ProvinceOccupation.Clone(),
            ProvinceOwner = campaign.ProvinceMap is null ? Array.Empty<ushort>() : (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone(),
            ProvinceState = campaign.ProvinceMap is null ? Array.Empty<ushort>() : (ushort[])campaign.ProvinceMap.ProvinceToState.Clone()
        };

        public Campaign Restore()
        {
            var campaign = Campaign.CreateBootstrap(Seed);
            Replace(campaign.Nations, Nations);
            Replace(campaign.States, States);
            Replace(campaign.Armies, Armies);
            Replace(campaign.Recruitment, Recruitment);
            campaign.NextRecruitmentId = Math.Max(NextRecruitmentId, campaign.Recruitment.Count == 0 ? 1 : campaign.Recruitment.Max(o => o.Id) + 1);
            Replace(campaign.ArmyGroups, ArmyGroups);
            Replace(campaign.Fleets, Fleets);
            Replace(campaign.Wars, Wars);
            Replace(campaign.Coalitions, Coalitions);
            Replace(campaign.FrontLines, FrontLines);
            // Never below what the restored lines already use, or the next drawn line would
            // be handed an id somebody is standing on.
            campaign.NextFrontLineId = Math.Max(NextFrontLineId,
                campaign.FrontLines.Count == 0 ? 0 : campaign.FrontLines.Max(line => line.Id) + 1);
            Replace(campaign.Battles, Battles);
            Replace(campaign.Construction, Construction);
            Replace(campaign.PendingEvents, PendingEvents);
            campaign.Relations.Clear();
            foreach (var entry in Relations) campaign.Relations[entry.Key] = entry.Value;
            campaign.History.Restore(History);
            campaign.RestoreRuntimeState(Date, ElapsedDays, IsPaused, RandomState, PlayerId,
                HumanSeats, ActiveSeat, NextBattleId, MapRevision, InitialDeploymentCompleted,
                ProvinceController, ProvinceOccupation, ProvinceOwner, ProvinceState);

            // The state list was just replaced wholesale, so the ownership index built
            // against the bootstrap campaign no longer describes it.
            campaign.InvalidateStateIndex();

            // And any country this campaign founded rather than loaded says again who it is
            // for. The demography catalogue is read from files and has never heard of it.
            campaign.DeclareFoundedPeoples();

            // Before anything reads a good or a building out of the restored campaign.
            RemapContent(campaign);

            // A save written before output became emergent carries no calibration factor.
            // Recalibrating against the GDP the save already holds is exactly right for
            // both cases: an old save keeps the economy it was played with, and a current
            // save re-derives the same factor it was saved with.
            if (campaign.Nations.Any(nation => nation.OutputScale <= 0f))
                EconomySystem.CalibrateOutput(campaign);

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
