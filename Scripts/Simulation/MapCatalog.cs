#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>Descriptor for a built-in or mod-provided playable world map pack.</summary>
public sealed record MapPackEntry(
    string Id,
    string Name,
    string NameEn,
    string NameRu,
    string Description,
    string DescriptionEn,
    string DescriptionRu,
    string Author,
    string Directory,
    string ProvincesPath,
    string DefinitionPath,
    string OwnershipPath,
    string RegionFingerprintPath,
    string CompiledStampPath,
    int CompiledProvinceCount,
    ProvinceBuildProfile Profile,
    bool InheritsBaseHydrology,
    bool IsBuiltIn,
    bool IsMod)
{
    public bool HasCustomProvinces =>
        !string.IsNullOrWhiteSpace(ProvincesPath) && File.Exists(ProvincesPath) &&
        !string.IsNullOrWhiteSpace(DefinitionPath) && File.Exists(DefinitionPath);

    public bool IsPlayable =>
        string.Equals(Id, MapCatalog.StandardId, StringComparison.Ordinal) ||
        (!string.IsNullOrWhiteSpace(Directory) && HasCustomProvinces);

    public string LocalizedName(string? language) =>
        string.Equals(language, "russian", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(NameRu)
            ? NameRu
            : (!string.IsNullOrWhiteSpace(NameEn) ? NameEn : Name);

    public string LocalizedDescription(string? language) =>
        string.Equals(language, "russian", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(DescriptionRu)
            ? DescriptionRu
            : (!string.IsNullOrWhiteSpace(DescriptionEn) ? DescriptionEn : Description);
}

/// <summary>
/// Catalogue of playable world map packs (built-in and mod-provided).
///
/// The standard 34,138-province world (<see cref="StandardId"/>) stays anchored on
/// <c>game/map_data/</c> and <c>game/history/provinces/ownership.csv</c> so every vanilla
/// benchmark, fingerprint, and legacy save keeps identical behaviour. Additional compiled packs in
/// <c>game/maps/&lt;id&gt;/map.json</c> (such as <c>world_tactical</c> and <c>world_compact</c>)
/// and mod packs under <c>mods/&lt;mod&gt;/maps/&lt;id&gt;/map.json</c> override only the
/// pack-specific political topology and ownership while inheriting shared terrain, heightmap,
/// rivers, and lakes unless the pack provides its own.
/// </summary>
public static class MapCatalog
{
    public const string StandardId = "world_standard";
    public const string TacticalId = "world_tactical";
    public const string CompactId = "world_compact";
    public const string ManifestFile = "map.json";
    public const string RegionFingerprintFile = "region_fingerprint.txt";

    private static readonly object Sync = new();
    private static readonly List<MapPackEntry> Maps = new();
    private static readonly Dictionary<string, MapPackEntry> ById = new(StringComparer.Ordinal);
    private static readonly List<string> ModLoaderRoots = new();
    private static readonly List<string> RegisteredModRoots = new();
    private static bool _modLoaderRootsSet;

    private static readonly HashSet<string> StructuralPackFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        MapRasterCatalog.DescriptorFile,
        MapRasterCatalog.ProvinceFile,
        MapRasterCatalog.DefinitionFile,
        MapRasterCatalog.StampFile,
        RegionFingerprintFile
    };

    private static readonly HashSet<string> HydrologyFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        MapRasterCatalog.RiverFile,
        MapRasterCatalog.LakeFile,
        "hydrography/lakes.json"
    };

    static MapCatalog()
    {
        lock (Sync)
        {
            RefreshCatalogLocked();
            ActiveMapId = StandardId;
            ActiveEntry = ById[StandardId];
        }
    }

    public static string ActiveMapId { get; private set; } = StandardId;
    public static MapPackEntry ActiveEntry { get; private set; } = null!;

    public static IReadOnlyList<MapPackEntry> AvailableMaps
    {
        get
        {
            lock (Sync)
            {
                if (Maps.Count == 0) RefreshCatalogLocked();
                return Maps.ToArray();
            }
        }
    }

    public static bool IsAvailable(string? mapId)
    {
        var id = NormalizeId(mapId);
        lock (Sync)
        {
            if (Maps.Count == 0) RefreshCatalogLocked();
            return ById.TryGetValue(id, out var entry) && entry.IsPlayable;
        }
    }

    public static bool TryGet(string? mapId, out MapPackEntry entry)
    {
        var id = NormalizeId(mapId);
        lock (Sync)
        {
            if (Maps.Count == 0) RefreshCatalogLocked();
            return ById.TryGetValue(id, out entry!);
        }
    }

    /// <summary>Resolve a save's map pack id, inferring legacy pre-MapId saves from persisted province count.</summary>
    public static string ResolveSaveMapId(string? rawMapId, int provinceCount, string? fallbackMapId = StandardId)
    {
        if (!string.IsNullOrWhiteSpace(rawMapId)) return rawMapId.Trim();
        if (provinceCount > 0)
        {
            lock (Sync)
            {
                if (Maps.Count == 0) RefreshCatalogLocked();
                var match = Maps.FirstOrDefault(m => m.IsPlayable && m.CompiledProvinceCount == provinceCount);
                if (match is not null) return match.Id;
            }
        }
        return string.IsNullOrWhiteSpace(fallbackMapId) ? "" : fallbackMapId.Trim();
    }

    /// <summary>Switch the active world map pack and invalidate cached raster/ownership/terrain catalogs.</summary>
    public static bool SetActiveMap(string? mapId)
    {
        var normalized = NormalizeId(mapId);
        bool changed;
        lock (Sync)
        {
            if (Maps.Count == 0) RefreshCatalogLocked();
            if (!ById.TryGetValue(normalized, out var entry) || !entry.IsPlayable)
                return false;

            changed = !string.Equals(ActiveMapId, entry.Id, StringComparison.Ordinal);
            ActiveMapId = entry.Id;
            ActiveEntry = entry;
        }

        if (changed) InvalidateMapCaches();
        return true;
    }

    /// <summary>Register an additional mod root or direct <c>maps/</c> directory and refresh the catalog.</summary>
    public static void RegisterModMapRoot(string? modRootOrMapsRoot)
    {
        if (string.IsNullOrWhiteSpace(modRootOrMapsRoot)) return;
        string fullPath;
        try { fullPath = Path.GetFullPath(modRootOrMapsRoot); }
        catch (Exception) { return; }

        lock (Sync)
        {
            if (!RegisteredModRoots.Any(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)))
                RegisteredModRoots.Add(fullPath);
        }

        RefreshCatalog();
    }

    /// <summary>Replace the mod root list supplied by <c>ModLoader</c> and refresh the catalog.</summary>
    public static void SetModRoots(IEnumerable<string>? modDirectories)
    {
        lock (Sync)
        {
            _modLoaderRootsSet = true;
            ModLoaderRoots.Clear();
            if (modDirectories is not null)
            {
                foreach (var dir in modDirectories)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        var full = Path.GetFullPath(dir);
                        if (!ModLoaderRoots.Any(existing => string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)))
                            ModLoaderRoots.Add(full);
                    }
                    catch (Exception)
                    {
                        // Skip malformed mod directory entries cleanly.
                    }
                }
            }
        }

        RefreshCatalog();
    }

    /// <summary>Clear explicitly registered mod map roots (primarily for tests).</summary>
    public static void ClearRegisteredModRoots()
    {
        lock (Sync)
        {
            _modLoaderRootsSet = false;
            ModLoaderRoots.Clear();
            RegisteredModRoots.Clear();
        }

        RefreshCatalog();
    }

    public static void Refresh() => RefreshCatalog();

    public static void RefreshCatalog()
    {
        bool fellBackToStandard = false;
        lock (Sync)
        {
            RefreshCatalogLocked();
            if (ById.TryGetValue(ActiveMapId, out var current) && current.IsPlayable)
            {
                ActiveEntry = current;
            }
            else
            {
                fellBackToStandard = !string.Equals(ActiveMapId, StandardId, StringComparison.Ordinal);
                ActiveMapId = StandardId;
                ActiveEntry = ById[StandardId];
            }
        }

        if (fellBackToStandard) InvalidateMapCaches();
    }

    /// <summary>
    /// Resolve a file logically belonging to <c>game/map_data/</c> for the active map pack.
    /// Pack-local overrides take precedence; structural files of a custom province pack never fall
    /// back to <c>world_standard</c>'s <c>compiled.json</c> / <c>provinces.png</c>.
    /// </summary>
    public static string? ResolveMapFile(string fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null : ResolveMapFile(new[] { fileName });

    public static string? ResolveMapFile(params string[] relativeParts)
    {
        if (relativeParts is null || relativeParts.Length == 0) return null;

        MapPackEntry entry;
        string activeId;
        lock (Sync)
        {
            if (Maps.Count == 0) RefreshCatalogLocked();
            activeId = ActiveMapId;
            entry = ActiveEntry ?? ById[StandardId];
        }

        if (!string.Equals(activeId, StandardId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(entry.Directory))
        {
            var relNormalized = string.Join("/", relativeParts).Replace('\\', '/').TrimStart('/');
            var configured = ResolveConfiguredPackPath(entry, relNormalized);
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            var combined = Path.Combine(new[] { entry.Directory }.Concat(relativeParts).ToArray());
            if (File.Exists(combined))
                return combined;

            if (entry.HasCustomProvinces && StructuralPackFiles.Contains(relNormalized))
                return null;

            if (!entry.InheritsBaseHydrology && HydrologyFiles.Contains(relNormalized))
                return null;
        }

        var args = new string[relativeParts.Length + 1];
        args[0] = "map_data";
        Array.Copy(relativeParts, 0, args, 1, relativeParts.Length);
        return GameDataLoader.FindDataFile(args);
    }

    /// <summary>
    /// Resolve an explicit <c>ownership.csv</c> file path for the active map pack, or <c>null</c>
    /// when standard <c>game/history/provinces/</c> ownership should be loaded.
    /// </summary>
    public static string? ResolveOwnershipPath()
    {
        MapPackEntry entry;
        string activeId;
        lock (Sync)
        {
            if (Maps.Count == 0) RefreshCatalogLocked();
            activeId = ActiveMapId;
            entry = ActiveEntry ?? ById[StandardId];
        }

        if (string.Equals(activeId, StandardId, StringComparison.Ordinal))
            return null;

        if (!string.IsNullOrWhiteSpace(entry.OwnershipPath) && File.Exists(entry.OwnershipPath))
            return entry.OwnershipPath;

        if (!string.IsNullOrWhiteSpace(entry.Directory))
        {
            var fallback = Path.Combine(entry.Directory, "ownership.csv");
            if (File.Exists(fallback)) return fallback;
        }

        return null;
    }

    /// <summary>Return canonical generation presets for named built-in map profiles.</summary>
    public static ProvinceBuildProfile BuiltInProfileFor(string? mapId) => (mapId ?? "").Trim() switch
    {
        TacticalId => new ProvinceBuildProfile(
            CellsPerProvince: 250,
            DisputedCellsPerProvince: 250,
            PolarFromLatitude: ProvinceTopology.PolarFromLatitude,
            PopulatedShare: ProvinceTopology.PopulatedShare,
            SiberiaMultiplier: 3.5f,
            PolarMultiplier: 5.0f,
            MinimumProvinceCells: 32),
        CompactId => new ProvinceBuildProfile(
            CellsPerProvince: 520,
            DisputedCellsPerProvince: 520,
            PolarFromLatitude: 58f,
            PopulatedShare: ProvinceTopology.PopulatedShare,
            SiberiaMultiplier: 4.0f,
            PolarMultiplier: 6.0f,
            MinimumProvinceCells: 48),
        _ => ProvinceBuildProfile.Standard
    };

    /// <summary>Build clean <c>map.json</c> manifest JSON matching the shared MapCatalog and ModValidator contract.</summary>
    public static string SerializeManifest(
        string id,
        string nameEn,
        string nameRu,
        string descriptionEn,
        string descriptionRu,
        string author,
        ProvinceBuildProfile profile,
        bool inheritsBaseHydrology = true,
        string provinces = MapRasterCatalog.ProvinceFile,
        string definition = MapRasterCatalog.DefinitionFile,
        string ownership = "ownership.csv")
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = string.IsNullOrWhiteSpace(nameEn) ? id : nameEn,
            ["name_en"] = string.IsNullOrWhiteSpace(nameEn) ? id : nameEn,
            ["name_ru"] = string.IsNullOrWhiteSpace(nameRu) ? nameEn : nameRu,
            ["description"] = descriptionEn ?? "",
            ["description_en"] = descriptionEn ?? "",
            ["description_ru"] = string.IsNullOrWhiteSpace(descriptionRu) ? (descriptionEn ?? "") : descriptionRu,
            ["author"] = string.IsNullOrWhiteSpace(author) ? "Bayte Realization" : author,
            ["cells_per_province"] = profile.CellsPerProvince,
            ["disputed_cells_per_province"] = profile.DisputedCellsPerProvince,
            ["polar_from_latitude"] = Math.Round(profile.PolarFromLatitude, 2),
            ["populated_share"] = Math.Round(profile.PopulatedShare, 3),
            ["siberia_multiplier"] = Math.Round(profile.SiberiaMultiplier, 3),
            ["polar_multiplier"] = Math.Round(profile.PolarMultiplier, 3),
            ["minimum_province_cells"] = profile.MinimumProvinceCells,
            ["inherit_base_hydrology"] = inheritsBaseHydrology,
            ["provinces"] = provinces,
            ["definition"] = definition,
            ["ownership"] = ownership
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string? ResolveConfiguredPackPath(MapPackEntry entry, string relNormalized)
    {
        if (string.Equals(relNormalized, MapRasterCatalog.ProvinceFile, StringComparison.OrdinalIgnoreCase))
            return entry.ProvincesPath;
        if (string.Equals(relNormalized, MapRasterCatalog.DefinitionFile, StringComparison.OrdinalIgnoreCase))
            return entry.DefinitionPath;
        if (string.Equals(relNormalized, MapRasterCatalog.StampFile, StringComparison.OrdinalIgnoreCase))
            return entry.CompiledStampPath;
        if (string.Equals(relNormalized, RegionFingerprintFile, StringComparison.OrdinalIgnoreCase))
            return entry.RegionFingerprintPath;
        return null;
    }

    private static void InvalidateMapCaches()
    {
        WorldMapSource.ReleaseDecodedGrids();
        MapRasterCatalog.ResetCatalog();
        ProvinceOwnership.ResetCatalog();
        TerrainCatalog.ResetCatalog();
    }

    private static void RefreshCatalogLocked()
    {
        Maps.Clear();
        ById.Clear();

        var standard = new MapPackEntry(
            Id: StandardId, Name: "Standard World", NameEn: "Standard World", NameRu: "Стандартный мир",
            Description: "Standard high-detail 34,138-province world map.",
            DescriptionEn: "Standard high-detail 34,138-province world map.",
            DescriptionRu: "Стандартная детализированная карта мира (34 138 провинций).",
            Author: "Bayte Realization", Directory: "", ProvincesPath: "", DefinitionPath: "", OwnershipPath: "",
            RegionFingerprintPath: "", CompiledStampPath: "", CompiledProvinceCount: 34_138,
            Profile: ProvinceBuildProfile.Standard, InheritsBaseHydrology: true, IsBuiltIn: true, IsMod: false);

        RegisterEntryLocked(standard);

        // Built-in compiled packs in game/maps/<id>/map.json
        foreach (var root in CandidateBaseRoots())
        {
            var mapsDir = Path.Combine(root, "game", "maps");
            ScanMapsDirectoryLocked(mapsDir, isBuiltIn: true, isMod: false);
        }

        // Mod-provided map packs: mods/<mod>/maps/<id>/map.json (and directly registered maps/ roots)
        foreach (var modRoot in CandidateModRootsLocked())
        {
            var asDirectMaps = string.Equals(Path.GetFileName(modRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "maps", StringComparison.OrdinalIgnoreCase) && Directory.Exists(modRoot);
            if (asDirectMaps)
            {
                ScanMapsDirectoryLocked(modRoot, isBuiltIn: false, isMod: true);
            }
            else
            {
                ScanMapsDirectoryLocked(Path.Combine(modRoot, "maps"), isBuiltIn: false, isMod: true);
            }
        }
    }

    private static IEnumerable<string> CandidateBaseRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dataFolder = GameDataLoader.DataFolder();
        if (!string.IsNullOrWhiteSpace(dataFolder))
        {
            var parent = Directory.GetParent(dataFolder)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) && seen.Add(parent)) yield return parent;
        }

        var cwd = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(cwd) && seen.Add(cwd)) yield return cwd;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 7 && dir is not null; depth++, dir = dir.Parent)
        {
            if (seen.Add(dir.FullName)) yield return dir.FullName;
        }
    }

    private static IReadOnlyList<string> CandidateModRootsLocked()
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_modLoaderRootsSet)
        {
            foreach (var dir in ModLoaderRoots)
                if (seen.Add(dir)) roots.Add(dir);
        }
        else
        {
            foreach (var baseRoot in CandidateBaseRoots())
            {
                var modsDir = Path.Combine(baseRoot, "mods");
                if (!Directory.Exists(modsDir)) continue;
                foreach (var modDir in Directory.EnumerateDirectories(modsDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    if (seen.Add(modDir)) roots.Add(modDir);
            }

            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var docsMods = GameFolders.For(docs)?.Mods;
                if (!string.IsNullOrWhiteSpace(docsMods) && Directory.Exists(docsMods))
                {
                    foreach (var modDir in Directory.EnumerateDirectories(docsMods).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                        if (seen.Add(modDir)) roots.Add(modDir);
                }
            }
            catch (Exception)
            {
                // Ignore restricted Documents folders on sandboxed hosts.
            }
        }

        foreach (var reg in RegisteredModRoots)
            if (seen.Add(reg)) roots.Add(reg);

        return roots;
    }

    private static void ScanMapsDirectoryLocked(string mapsDir, bool isBuiltIn, bool isMod)
    {
        if (string.IsNullOrWhiteSpace(mapsDir) || !Directory.Exists(mapsDir)) return;
        foreach (var packDir in Directory.EnumerateDirectories(mapsDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(packDir, ManifestFile);
            if (!File.Exists(manifestPath)) continue;
            var entry = TryLoadManifest(packDir, manifestPath, isBuiltIn, isMod);
            if (entry is not null) RegisterEntryLocked(entry);
        }
    }

    private static void RegisterEntryLocked(MapPackEntry entry)
    {
        if (ById.TryGetValue(entry.Id, out var existing))
        {
            // Keep StandardId authoritative for vanilla, allow later/mod overlays to override custom packs.
            if (string.Equals(entry.Id, StandardId, StringComparison.Ordinal)) return;
            var idx = Maps.IndexOf(existing);
            if (idx >= 0) Maps[idx] = entry;
            ById[entry.Id] = entry;
            return;
        }

        Maps.Add(entry);
        ById[entry.Id] = entry;
    }

    private static MapPackEntry? TryLoadManifest(string packDir, string manifestPath, bool isBuiltIn, bool isMod)
    {
        try
        {
            using var doc = StructuredData.ReadDocument(manifestPath);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var folderId = Path.GetFileName(packDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var id = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(id)) id = folderId;
            id = id.Trim();
            if (string.IsNullOrWhiteSpace(id)) return null;

            var name = ReadString(root, "name");
            var nameEn = ReadString(root, "name_en");
            var nameRu = ReadString(root, "name_ru");
            if (string.IsNullOrWhiteSpace(nameEn)) nameEn = !string.IsNullOrWhiteSpace(name) ? name : id;
            if (string.IsNullOrWhiteSpace(name)) name = nameEn;
            if (string.IsNullOrWhiteSpace(nameRu)) nameRu = nameEn;

            var desc = ReadString(root, "description");
            var descEn = ReadString(root, "description_en");
            var descRu = ReadString(root, "description_ru");
            if (string.IsNullOrWhiteSpace(descEn)) descEn = desc ?? "";
            if (string.IsNullOrWhiteSpace(desc)) desc = descEn;
            if (string.IsNullOrWhiteSpace(descRu)) descRu = descEn;

            var author = ReadString(root, "author");
            if (string.IsNullOrWhiteSpace(author)) author = isMod ? "Mod Author" : "Bayte Realization";

            var baseProfile = BuiltInProfileFor(id);
            var profile = new ProvinceBuildProfile(
                CellsPerProvince: ReadInt(root, "cells_per_province", baseProfile.CellsPerProvince, 12, 10_000),
                DisputedCellsPerProvince: ReadInt(root, "disputed_cells_per_province", baseProfile.DisputedCellsPerProvince, 12, 10_000),
                PolarFromLatitude: ReadFloat(root, "polar_from_latitude", baseProfile.PolarFromLatitude, 15f, 89f),
                PopulatedShare: ReadFloat(root, "populated_share", baseProfile.PopulatedShare, 0.25f, 2f),
                SiberiaMultiplier: ReadFloat(root, "siberia_multiplier", baseProfile.SiberiaMultiplier, 0.25f, 16f),
                PolarMultiplier: ReadFloat(root, "polar_multiplier", baseProfile.PolarMultiplier, 0.25f, 16f),
                MinimumProvinceCells: ReadInt(root, "minimum_province_cells",
                    ReadInt(root, "min_province_cells", baseProfile.MinimumProvinceCells, 4, 2_000), 4, 2_000));

            var inheritsHydrology = ReadBool(root, "inherit_base_hydrology",
                ReadBool(root, "inherits_base_hydrology", true));

            var provincesRel = ReadString(root, "provinces");
            var definitionRel = ReadString(root, "definition");
            var ownershipRel = ReadString(root, "ownership");

            var provincesPath = ResolveSafeSubpath(packDir, string.IsNullOrWhiteSpace(provincesRel) ? MapRasterCatalog.ProvinceFile : provincesRel);
            var definitionPath = ResolveSafeSubpath(packDir, string.IsNullOrWhiteSpace(definitionRel) ? MapRasterCatalog.DefinitionFile : definitionRel);
            var ownershipPath = ResolveSafeSubpath(packDir, string.IsNullOrWhiteSpace(ownershipRel) ? "ownership.csv" : ownershipRel);
            var stampPath = Path.Combine(packDir, MapRasterCatalog.StampFile);
            var fpPath = Path.Combine(packDir, RegionFingerprintFile);
            var compiledProvinces = ReadCompiledProvinceCount(stampPath);

            return new MapPackEntry(
                Id: id, Name: name, NameEn: nameEn, NameRu: nameRu,
                Description: desc, DescriptionEn: descEn, DescriptionRu: descRu, Author: author,
                Directory: packDir, ProvincesPath: provincesPath, DefinitionPath: definitionPath,
                OwnershipPath: ownershipPath, RegionFingerprintPath: fpPath, CompiledStampPath: stampPath,
                CompiledProvinceCount: compiledProvinces, Profile: profile,
                InheritsBaseHydrology: inheritsHydrology, IsBuiltIn: isBuiltIn, IsMod: isMod);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ResolveSafeSubpath(string rootDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "";
        if (Path.IsPathRooted(relativePath)) return "";
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var parts = normalized.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains("..")) return "";
        return Path.GetFullPath(Path.Combine(rootDir, normalized));
    }

    private static int ReadCompiledProvinceCount(string stampPath)
    {
        if (string.IsNullOrWhiteSpace(stampPath) || !File.Exists(stampPath)) return 0;
        try
        {
            using var doc = StructuredData.ReadDocument(stampPath);
            if (doc.RootElement.TryGetProperty("provinces", out var p) && p.TryGetInt32(out var count))
                return Math.Max(0, count);
        }
        catch (Exception)
        {
            // Malformed stamp is checked later during topology load.
        }
        return 0;
    }

    private static string NormalizeId(string? mapId) =>
        string.IsNullOrWhiteSpace(mapId) ? StandardId : mapId.Trim();

    private static string ReadString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String ? (val.GetString() ?? "") : "";

    private static int ReadInt(JsonElement obj, string key, int fallback, int min, int max) =>
        obj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var n)
            ? Math.Clamp(n, min, max)
            : fallback;

    private static float ReadFloat(JsonElement obj, string key, float fallback, float min, float max) =>
        obj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number && val.TryGetSingle(out var f) && float.IsFinite(f)
            ? Math.Clamp(f, min, max)
            : fallback;

    private static bool ReadBool(JsonElement obj, string key, bool fallback)
    {
        if (!obj.TryGetProperty(key, out var val)) return fallback;
        return val.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }
}
