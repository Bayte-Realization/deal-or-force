using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CommunityToolkit.HighPerformance;

namespace DealOrForce.Simulation;

/// <summary>
/// Small, reviewable corrections for topological errors in the baked political raster.
/// Rules operate on country map indices, so render, picking, AI adjacency and future saves
/// all see the same result. Mods can replace this catalogue without touching the renderer.
/// </summary>
public static partial class TerritorialTopology
{
    private static readonly ConditionalWeakTable<ushort[], HashSet<string>> AppliedRules = new();
    private static readonly ConditionalWeakTable<ushort[], PreRepairedTag> PreRepairedGrids = new();

    private sealed class PreRepairedTag
    {
        public ulong AppliedPartitionMask;
        public CachedBootRuleDonor[] RuleDonors;
    }

    public readonly record struct RepairSummary(
        string SubjectTag,
        string NeighborTag,
        int ChangedCells,
        int ConnectedComponents = 0,
        string ClippedAway = "");

    /// <summary>One share of a region that is being split, and where its centre is.</summary>
    private readonly record struct RegionPiece(
        int MapIndex, string Name, string Iso, string Kind, long Population,
        float Longitude, float Latitude);

    /// <summary>A region split into several shares grown from centre seeds.</summary>
    private readonly record struct RegionPartition(
        string NationTag, string CarveFromIso, RegionPiece[] Pieces,
        string[] AlsoCarveFromIso = null, bool Polar = false);

    private readonly record struct AdjacencyRule(
        string SubjectTag,
        string NeighborTag,
        string ThroughTag,
        float MinLongitude,
        float MaxLongitude,
        float MinLatitude,
        float MaxLatitude,
        int CorridorRadius);

    private readonly record struct RegionRule(
        int MapIndex,
        string NationTag,
        string SourceNationTag,
        string Name,
        string Iso,
        string Kind,
        string DonorIso,
        long Population,
        float MinLongitude,
        float MaxLongitude,
        float MinLatitude,
        float MaxLatitude,
        GeoPoint[] Polygon,
        RegionSeed[] Seeds,
        string CarveFromIso = "",
        bool ReplaceExisting = false);

    private readonly record struct GeoPoint(float Longitude, float Latitude);
    private readonly record struct RegionSeed(float Longitude, float Latitude, int RadiusCells);
    private readonly record struct CellTransfer(string Id, string FromIso, string ToIso,
        string FromNation, string ToNation, GeoPoint[] Points);

    private static readonly List<CellTransfer> CellTransfers = new();
    private static readonly ImmutableArray<AdjacencyRule> DefaultAdjacencyRules = ImmutableArray.Create(
        // The source admin-1 raster leaves a narrow Georgian artefact between Abkhazia
        // and Russia. In reality Abkhazia reaches the Russian border at the Psou/Caucasus.
        new AdjacencyRule("ABK", "RUS", "GEO", 39.7f, 42.3f, 42.0f, 44.1f, 8));

    private static readonly ImmutableArray<RegionRule> DefaultRegionRules = ImmutableArray.Create(
        new RegionRule(1711, "ABK", "", "Abkhazia", "ABK-X01", "Disputed region", "", 245_000,
            39.7f, 42.4f, 42.0f, 44.1f, Array.Empty<GeoPoint>(), Array.Empty<RegionSeed>()),
        new RegionRule(1712, "OST", "", "South Ossetia", "OST-X01", "Disputed region", "", 56_500,
            42.8f, 44.9f, 41.6f, 43.1f, Array.Empty<GeoPoint>(), Array.Empty<RegionSeed>()),
        new RegionRule(1713, "IND", "", "Andaman and Nicobar Islands", "IN-AN", "Union Territory", "IN-RJ", 434_192,
            91.4f, 94.7f, 6.0f, 14.8f, Array.Empty<GeoPoint>(), new[]
            {
                new RegionSeed(92.9f, 13.2f, 1), new RegionSeed(92.75f, 11.65f, 1),
                new RegionSeed(92.75f, 9.2f, 1), new RegionSeed(93.85f, 7.0f, 1)
            }),
        new RegionRule(1714, "IND", "", "Lakshadweep", "IN-LD", "Union Territory", "IN-RJ", 64_473,
            71.0f, 74.2f, 7.5f, 13.8f, Array.Empty<GeoPoint>(), new[]
            {
                new RegionSeed(72.19f, 10.85f, 1), new RegionSeed(72.64f, 10.57f, 1),
                new RegionSeed(73.05f, 8.28f, 1)
            }),
        new RegionRule(1715, "PMR", "MDA", "Transnistria", "PMR-X01", "De facto republic", "", 367_776,
            28.45f, 29.98f, 46.54f, 48.19f,
            new GeoPoint[]
            {
                new(29.39072f, 47.37976f), new(29.42705f, 47.29215f), new(29.57406f, 47.37047f),
                new(29.57992f, 47.13576f), new(29.49475f, 47.13152f), new(29.53020f, 47.07545f),
                new(29.61240f, 47.09960f), new(29.57209f, 46.94025f), new(29.97993f, 46.81971f),
                new(29.93517f, 46.80578f), new(29.96328f, 46.58376f), new(29.87597f, 46.54671f),
                new(29.72349f, 46.59912f), new(29.76346f, 46.66829f), new(29.60161f, 46.67928f),
                new(29.65367f, 46.74936f), new(29.56823f, 46.70467f), new(29.51037f, 46.79283f),
                new(29.39328f, 46.75915f), new(29.43278f, 46.84884f), new(29.49008f, 46.85380f),
                new(29.47127f, 46.94954f), new(29.29133f, 46.98543f), new(29.37569f, 46.99451f),
                new(29.39162f, 47.05921f), new(29.11237f, 47.28642f), new(29.17156f, 47.32897f),
                new(29.12353f, 47.32200f), new(29.16122f, 47.35530f), new(29.07571f, 47.41474f),
                new(29.11869f, 47.46349f), new(29.01914f, 47.52503f), new(28.96587f, 47.71888f),
                new(29.02372f, 47.84931f), new(28.82621f, 47.90131f), new(28.69745f, 48.02413f),
                new(28.53715f, 47.99958f), new(28.63523f, 48.07891f), new(28.50294f, 48.15079f),
                new(28.84112f, 48.12554f), new(28.83945f, 48.03495f), new(28.92585f, 47.96014f),
                new(29.17532f, 47.99378f), new(29.20064f, 47.88437f), new(29.28157f, 47.88795f),
                new(29.19854f, 47.81851f), new(29.27639f, 47.80208f), new(29.20436f, 47.71907f),
                new(29.21766f, 47.61229f), new(29.11695f, 47.55442f), new(29.18967f, 47.51397f),
                new(29.18950f, 47.42607f), new(29.31749f, 47.44920f), new(29.33593f, 47.37723f),
                new(29.16769f, 47.30719f), new(29.39072f, 47.37976f)
            }, Array.Empty<RegionSeed>()),
        new RegionRule(1716, "MDV", "", "Maldives", "MV-X01", "Atoll republic", "", 530_953,
            72.3f, 73.8f, -0.8f, 7.4f, Array.Empty<GeoPoint>(), new[]
            {
                new RegionSeed(73.15f, 6.75f, 1), new RegionSeed(73.48f, 4.18f, 1),
                new RegionSeed(73.10f, 1.95f, 1), new RegionSeed(73.18f, -0.30f, 1)
            }),
        new RegionRule(1717, "VAT", "ITA", "Vatican City", "VA-X01", "Ecclesiastical state", "IT-RM", 825,
            12.4534f, 12.4534f, 41.9029f, 41.9029f, Array.Empty<GeoPoint>(), new[]
            {
                new RegionSeed(12.4534f, 41.9029f, 0)
            }),
        new RegionRule(1718, "CYN", "CYP", "Northern Cyprus", "CYN-X01", "De facto republic", "CY-01", 326_000,
            32.20f, 34.95f, 34.99f, 35.95f,
            new GeoPoint[]
            {
                new(32.20f, 35.10f), new(32.90f, 35.06f), new(33.15f, 35.16f), new(33.35f, 35.19f),
                new(33.45f, 35.16f), new(33.70f, 35.04f), new(33.95f, 34.99f), new(34.15f, 34.99f),
                new(34.95f, 35.95f), new(32.20f, 35.95f), new(32.20f, 35.10f)
            }, Array.Empty<RegionSeed>()),
        new RegionRule(1720, "AZE", "AZE", "Nakhchivan", "AZ-NX", "Autonomous Republic", "", 462_500,
            44.62f, 46.26f, 38.74f, 39.85f,
            new GeoPoint[]
            {
                new(44.70f, 39.85f), new(45.42f, 39.72f), new(46.20f, 38.94f), new(46.26f, 38.86f),
                new(45.60f, 38.74f), new(45.00f, 38.90f), new(44.62f, 39.28f), new(44.70f, 39.85f)
            },
            new[]
            {
                new RegionSeed(45.71875f, 39.53125f, 1), new RegionSeed(45.90625f, 39.28125f, 1)
            }),
        new RegionRule(1719, "GIB", "ESP", "Gibraltar", "GI-X01", "Overseas territory", "ES-CA", 33_701,
            -5.3536f, -5.3536f, 36.1408f, 36.1408f, Array.Empty<GeoPoint>(), new[]
            {
                new RegionSeed(-5.3536f, 36.1408f, 0)
            }));

    private static readonly List<AdjacencyRule> BaseRules = new();
    private static readonly List<RegionRule> RegionRules = new();
    private static readonly List<RegionPartition> Partitions = new();

    private static HashSet<int> _shippedStateIndices;
    private static Dictionary<string, ushort> _stateIndexByIso;
    private static readonly BootKeyCache BootKeys = new();

    private sealed class BootKeyCache
    {
        public string ControlPath;
        public long ControlLen;
        public long ControlTicks;
        public string StatesPath;
        public long StatesLen;
        public long StatesTicks;
        public ulong SourcePngKey;

        public void Clear()
        {
            ControlPath = null;
            ControlLen = 0;
            ControlTicks = 0;
            StatesPath = null;
            StatesLen = 0;
            StatesTicks = 0;
            SourcePngKey = 0;
        }
    }

    static TerritorialTopology() => ResetCatalog();

    /// <summary>The population a region correction claims for itself, if any.</summary>
    public static bool AuthoredPopulation(int mapIndex, out long population)
    {
        foreach (var rule in RegionRules)
        {
            if (rule.MapIndex != mapIndex || rule.Population <= 0) continue;
            population = rule.Population;
            return true;
        }
        foreach (var partition in Partitions)
        {
            foreach (var piece in partition.Pieces)
            {
                if (piece.MapIndex != mapIndex || piece.Population <= 0) continue;
                population = piece.Population;
                return true;
            }
        }
        population = 0;
        return false;
    }

    /// <summary>How many authoritative region corrections are loaded, so scenario import checks arithmetic cleanly.</summary>
    public static int RegionRuleCount =>
        RegionRules.Count + Partitions.Sum(partition =>
            partition.Pieces.Count(piece => !ShippedStateIndices.Contains(piece.MapIndex))) +
        LakeRegions.CreatedCount;

    public static void InvalidateSourceIndices()
    {
        _shippedStateIndices = null;
        _stateIndexByIso = null;
        lock (BootKeys) BootKeys.Clear();
    }

    private static HashSet<int> ShippedStateIndices
    {
        get
        {
            if (_shippedStateIndices is not null) return _shippedStateIndices;
            _shippedStateIndices = new HashSet<int>(WorldMapTables.StateRows.Length);
            foreach (var row in WorldMapTables.StateRows)
            {
                var sep = row.IndexOf('|');
                var span = sep >= 0 ? row.AsSpan(0, sep) : row.AsSpan();
                if (int.TryParse(span, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var index))
                    _shippedStateIndices.Add(index);
            }
            return _shippedStateIndices;
        }
    }

    public static void ResetCatalog()
    {
        lock (BootKeys) BootKeys.Clear();
        BaseRules.Clear();
        BaseRules.AddRange(DefaultAdjacencyRules);
        RegionRules.Clear();
        RegionRules.AddRange(DefaultRegionRules);
        Partitions.Clear();
        CellTransfers.Clear();
        LakeRegions.ResetCatalog();
        var file = GameDataLoader.DataFiles("map_data", "state_regions").FirstOrDefault();
        if (file is null) return;
        try { LoadOverrides(StructuredData.ReadJson(file), null); }
        catch { /* Compiled corrections deliberately remain bootable. */ }
    }

    internal static void MarkPreRepaired(
        ushort[] grid,
        ulong appliedPartitionMask = ulong.MaxValue,
        CachedBootRuleDonor[] ruleDonors = null)
    {
        if (grid is null) return;
        var tag = PreRepairedGrids.GetValue(grid, static _ => new PreRepairedTag());
        tag.AppliedPartitionMask = appliedPartitionMask;
        tag.RuleDonors = ruleDonors;
    }

    internal static bool IsPreRepaired(ushort[] grid) =>
        grid is not null && PreRepairedGrids.TryGetValue(grid, out _);

    internal static void SyncBootLakes(CachedBootLake[] lakes)
    {
        if (lakes is not null && lakes.Length > 0) LakeRegions.RestoreBootLakes(lakes);
    }

    internal static bool TryPeekBootCacheRegionFingerprint(int width, int height, out ulong regionFingerprint)
    {
        regionFingerprint = 0;
        if (ModOverlayPolicy.IsActive || width != WorldMapSource.Width || height != WorldMapSource.Height)
            return false;
        ComputeBootRasterKeys(width, height, out var contentKey, out var sourcePngKey);
        if (!MapBootCache.TryReadBootLakes(
                MapBootCache.DefaultWritableCacheDirectory(),
                contentKey, sourcePngKey, width, height,
                out var lakes, out _, out regionFingerprint, out _,
                GameDataLoader.FindDataFile("cache", MapBootCache.BootRasterFileName)) ||
            regionFingerprint == 0)
            return false;
        SyncBootLakes(lakes);
        return true;
    }

    internal static bool TryPrebakeBootRasters(string cacheDirectory, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(cacheDirectory)) { error = "empty cache directory"; return false; }
        if (ModOverlayPolicy.IsActive) { error = "bypassed: mod overlay active"; return false; }

        var width = WorldMapSource.Width;
        var height = WorldMapSource.Height;
        if (string.IsNullOrWhiteSpace(MapRasterCatalog.SourceControlPath) || !File.Exists(MapRasterCatalog.SourceControlPath) ||
            string.IsNullOrWhiteSpace(MapRasterCatalog.SourceStatesPath) || !File.Exists(MapRasterCatalog.SourceStatesPath))
        {
            error = "missing source_control.png or source_states.png";
            return false;
        }

        var control = SourceMapGrid.Load(MapRasterCatalog.SourceControlPath, width, height);
        var states = SourceMapGrid.Load(MapRasterCatalog.SourceStatesPath, width, height);
        if (WorldMapTables.CountryRows.Length == 0 || WorldMapTables.StateRows.Length == 0)
            WorldMapTables.ResetCatalog();
        var shell = new Campaign(1);
        ScenarioCatalog.PopulateBaseTerritoryShell(shell);
        IndexCampaignStates(shell, out var stateByMapIndex, out var stateByIso);
        var summaries = new List<RepairSummary>(BaseRules.Count + RegionRules.Count);
        RunColdRasterRepairs(shell, control, states, width, height, stateByMapIndex, stateByIso, summaries,
            out var partitionMask, out var ruleDonors, out _);

        ComputeBootRasterKeys(width, height, out var contentKey, out var sourcePngKey);
        var lakes = CollectBootLakes(shell);
        var regionFingerprint = MapRasterCatalog.RegionFingerprint(states);
        var targetPath = Path.Combine(cacheDirectory, MapBootCache.BootRasterFileName);
        return MapBootCache.TryWriteBootRastersToFile(
            targetPath, contentKey, sourcePngKey, regionFingerprint, partitionMask,
            width, height, lakes, control, states, out error, ruleDonors);
    }

    internal static bool TryReadBootRasterFromCache(
        int width,
        int height,
        bool loadControl,
        out ushort[] raster,
        out CachedBootLake[] lakes)
    {
        raster = null;
        lakes = null;
        if (ModOverlayPolicy.IsActive || width != WorldMapSource.Width || height != WorldMapSource.Height)
            return false;
        ComputeBootRasterKeys(width, height, out var contentKey, out var sourcePngKey);
        return MapBootCache.TryReadBootRasterLayer(
            MapBootCache.DefaultWritableCacheDirectory(),
            contentKey, sourcePngKey, width, height, loadControl,
            out raster, out lakes,
            GameDataLoader.FindDataFile("cache", MapBootCache.BootRasterFileName));
    }

    internal static void RepairRawGridsForBoot(ushort[] control, ushort[] states, int width, int height)
    {
        var shell = new Campaign(1);
        ScenarioCatalog.PopulateBaseTerritoryShell(shell);
        ApplyCore(shell, control, states, width, height, forcePersistBootCache: true);
    }

    /// <summary>
    /// Populate campaign correction states and lake states from prebaked binary caches without
    /// decoding <c>source_control.png</c> or <c>source_states.png</c>, or running pixel geometry.
    /// </summary>
    internal static bool TryApplyPrebakedBoot(Campaign campaign, int width, int height)
    {
        if (campaign is null || ModOverlayPolicy.IsActive ||
            width != WorldMapSource.Width || height != WorldMapSource.Height)
            return false;

        if (WorldMapSource.PeekControlIfLoaded() is { } loadedControl &&
            WorldMapSource.PeekStatesIfLoaded() is { } loadedStates &&
            IsPreRepaired(loadedControl) && IsPreRepaired(loadedStates))
        {
            PreRepairedGrids.TryGetValue(loadedStates, out var tag);
            var mask = tag?.AppliedPartitionMask ?? ulong.MaxValue;
            IndexCampaignStates(campaign, out var byMap, out var byIso);
            ApplyRegionRuleStatesOnly(campaign, byMap, byIso, tag?.RuleDonors);
            ApplyPartitionsMetadataOnly(campaign, byMap, mask);
            LakeRegions.Apply(campaign, loadedControl, loadedStates, width, height);
            return true;
        }

        ComputeBootRasterKeys(width, height, out var contentKey, out var sourcePngKey);
        if (MapBootCache.TryReadBootLakes(
                MapBootCache.DefaultWritableCacheDirectory(),
                contentKey, sourcePngKey, width, height,
                out var lakes, out var ruleDonors, out _, out var appliedPartitionMask,
                GameDataLoader.FindDataFile("cache", MapBootCache.BootRasterFileName)))
        {
            ApplyBootMetadataAndLakes(campaign, lakes, ruleDonors, appliedPartitionMask);
            return true;
        }

        return TryApplyFromShippedTopologyAndLakes(campaign, width, height);
    }

    private static void ApplyBootMetadataAndLakes(
        Campaign campaign,
        CachedBootLake[] lakes,
        CachedBootRuleDonor[] ruleDonors,
        ulong appliedPartitionMask)
    {
        IndexCampaignStates(campaign, out var stateByMapIndex, out var stateByIso);
        ApplyRegionRuleStatesOnly(campaign, stateByMapIndex, stateByIso, ruleDonors);
        ApplyPartitionsMetadataOnly(campaign, stateByMapIndex, appliedPartitionMask);
        AppendPrebakedLakeStates(campaign, stateByMapIndex, lakes);
        SyncBootLakes(lakes);
    }

    private static bool TryApplyFromShippedTopologyAndLakes(Campaign campaign, int width, int height)
    {
        if (!MapBootCache.TryReadTopologyProvinceStates(
                MapBootCache.DefaultWritableCacheDirectory(), width, height,
                out var provinceToState, out _,
                GameDataLoader.FindDataFile("cache", MapBootCache.FileName)))
            return false;
        if (!LakeRegions.TryReadPrebakedBootLakeNames(width, height, out var lakeNames))
            return false;

        IndexCampaignStates(campaign, out var stateByMapIndex, out _);
        var present = ArrayPool<bool>.Shared.Rent(ushort.MaxValue + 1);
        Array.Clear(present, 0, ushort.MaxValue + 1);
        try
        {
            var lakeIndices = CollectPresentStateIndices(provinceToState, present);
            if (!ValidateBootLakeSequence(lakeIndices, lakeNames, out var lakes)) return false;
            if (!ValidateBootRulesPresent(campaign, stateByMapIndex, present)) return false;
            if (!ValidateBootCellTransfersPresent(campaign, present)) return false;
            if (!TryComputeShippedPartitionMask(campaign, present, out var partitionMask)) return false;

            ApplyBootMetadataAndLakes(campaign, lakes, null, partitionMask);
            return true;
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(present);
        }
    }

    private static bool ValidateBootCellTransfersPresent(Campaign campaign, bool[] present)
    {
        foreach (var transfer in CellTransfers)
        {
            var from = StateIndexByIso(transfer.FromIso);
            var to = StateIndexByIso(transfer.ToIso);
            if (from == 0 || to == 0) continue;
            if (campaign.NationByTag(transfer.FromNation) is null || campaign.NationByTag(transfer.ToNation) is null)
                continue;
            if ((uint)to >= (uint)present.Length || !present[to]) return false;
        }
        return true;
    }

    private static List<int> CollectPresentStateIndices(ushort[] provinceToState, bool[] present)
    {
        const int initialLakeCapacity = 128;
        var lakeIndices = new List<int>(initialLakeCapacity);
        for (var i = 1; i < provinceToState.Length; i++)
        {
            var idx = provinceToState[i];
            if (idx == 0 || present[idx]) continue;
            present[idx] = true;
            if (idx >= LakeRegions.FirstMapIndex) lakeIndices.Add(idx);
        }
        lakeIndices.Sort();
        return lakeIndices;
    }

    private static bool ValidateBootLakeSequence(
        List<int> lakeIndices,
        string[] lakeNames,
        out CachedBootLake[] lakes)
    {
        lakes = null;
        if (lakeIndices.Count != lakeNames.Length) return false;
        var result = new CachedBootLake[lakeNames.Length];
        for (var i = 0; i < lakeNames.Length; i++)
        {
            if (lakeIndices[i] != LakeRegions.FirstMapIndex + i) return false;
            result[i] = new CachedBootLake((ushort)lakeIndices[i], lakeNames[i]);
        }
        lakes = result;
        return true;
    }

    private static bool ValidateBootRulesPresent(
        Campaign campaign,
        Dictionary<int, ScenarioState> stateByMapIndex,
        bool[] present)
    {
        var hasPopulatedStates = HasAnyPopulatedState(campaign.States);
        foreach (var rule in RegionRules)
        {
            var nation = campaign.NationByTag(rule.NationTag);
            if (nation is null) continue;
            var sourceNation = string.IsNullOrWhiteSpace(rule.SourceNationTag)
                ? nation
                : campaign.NationByTag(rule.SourceNationTag);
            if (sourceNation is null) continue;
            if ((uint)rule.MapIndex >= (uint)present.Length || !present[rule.MapIndex]) return false;
            if (!stateByMapIndex.ContainsKey(rule.MapIndex) &&
                rule.Population > 0 &&
                string.IsNullOrWhiteSpace(rule.DonorIso) &&
                hasPopulatedStates)
                return false;
        }
        return true;
    }

    private static bool HasAnyPopulatedState(IReadOnlyList<ScenarioState> states)
    {
        for (var i = 0; i < states.Count; i++)
            if (states[i].Population > 0) return true;
        return false;
    }

    private static bool TryComputeShippedPartitionMask(Campaign campaign, bool[] present, out ulong partitionMask)
    {
        partitionMask = 0;
        for (var pi = 0; pi < Partitions.Count; pi++)
        {
            var partition = Partitions[pi];
            var nation = campaign.NationByTag(CountryMerges.OwnerOf(partition.NationTag));
            var donorIndex = StateIndexByIso(partition.CarveFromIso);
            if ((nation is null && !CountryMerges.IsMerged(partition.NationTag)) || donorIndex == 0) continue;
            if (!ArePartitionPiecesPresent(partition, present, out var missing)) return false;
            if (!missing && pi < 64) partitionMask |= 1UL << pi;
        }
        return true;
    }

    private static bool ArePartitionPiecesPresent(RegionPartition partition, bool[] present, out bool missing)
    {
        missing = false;
        var hasNewPieces = false;
        foreach (var piece in partition.Pieces)
        {
            if (ShippedStateIndices.Contains(piece.MapIndex)) continue;
            hasNewPieces = true;
            if ((uint)piece.MapIndex >= (uint)present.Length || !present[piece.MapIndex])
            {
                missing = true;
                return false;
            }
        }
        if (hasNewPieces) return true;
        return (uint)StateIndexByIso(partition.CarveFromIso) < (uint)present.Length;
    }

    public static IReadOnlyList<RepairSummary> Apply(
        Campaign campaign,
        ushort[] control,
        ushort[] states,
        int width,
        int height) =>
        ApplyCore(campaign, control, states, width, height, forcePersistBootCache: false);

    private static IReadOnlyList<RepairSummary> ApplyCore(
        Campaign campaign,
        ushort[] control,
        ushort[] states,
        int width,
        int height,
        bool forcePersistBootCache)
    {
        var summaries = new List<RepairSummary>(BaseRules.Count + RegionRules.Count);
        IndexCampaignStates(campaign, out var stateByMapIndex, out var stateByIso);

        if (IsPreRepaired(control) && IsPreRepaired(states))
        {
            PreRepairedGrids.TryGetValue(states, out var tag);
            var mask = tag?.AppliedPartitionMask ?? ulong.MaxValue;
            ApplyRegionRuleStatesOnly(campaign, stateByMapIndex, stateByIso, tag?.RuleDonors);
            ApplyPartitionsMetadataOnly(campaign, stateByMapIndex, mask);
            var cachedLakes = LakeRegions.Apply(campaign, control, states, width, height);
            if (cachedLakes > 0) summaries.Add(new RepairSummary($"{cachedLakes:N0} lake(s)", "LAKES", cachedLakes));
            return summaries;
        }

        RunColdRasterRepairs(campaign, control, states, width, height, stateByMapIndex, stateByIso, summaries,
            out var partitionMask, out var ruleDonors, out var lakes);
        if (lakes > 0) summaries.Add(new RepairSummary($"{lakes:N0} lake(s)", "LAKES", lakes));

        TryCacheBootGrids(campaign, partitionMask, ruleDonors, control, states, width, height, forcePersistBootCache);
        return summaries;
    }

    private static void RunColdRasterRepairs(
        Campaign campaign,
        ushort[] control,
        ushort[] states,
        int width,
        int height,
        Dictionary<int, ScenarioState> stateByMapIndex,
        Dictionary<string, ScenarioState> stateByIso,
        List<RepairSummary> summaries,
        out ulong partitionMask,
        out CachedBootRuleDonor[] ruleDonors,
        out int lakeCount)
    {
        ruleDonors = ApplyRegionRules(campaign, control, states, width, height, stateByMapIndex, stateByIso, summaries);
        partitionMask = ApplyPartitionsBatch(campaign, control, states, width, height, stateByMapIndex, summaries);
        ApplyCellTransferRules(campaign, control, states, width, height, summaries);
        lakeCount = LakeRegions.Apply(campaign, control, states, width, height);
        ApplyAdjacencyCorridors(campaign, control, states, width, height, summaries);
        MapRasterCatalog.ReconcileCrossArchitectureRegions(states, control, width, height);
    }

    private static void IndexCampaignStates(
        Campaign campaign,
        out Dictionary<int, ScenarioState> stateByMapIndex,
        out Dictionary<string, ScenarioState> stateByIso)
    {
        stateByMapIndex = new Dictionary<int, ScenarioState>(campaign.States.Count + RegionRules.Count);
        stateByIso = new Dictionary<string, ScenarioState>(campaign.States.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < campaign.States.Count; i++)
        {
            var existing = campaign.States[i];
            stateByMapIndex.TryAdd(existing.MapIndex, existing);
            if (!string.IsNullOrEmpty(existing.Iso)) stateByIso.TryAdd(existing.Iso, existing);
        }
    }

    private static void ApplyRegionRuleStatesOnly(
        Campaign campaign,
        Dictionary<int, ScenarioState> stateByMapIndex,
        Dictionary<string, ScenarioState> stateByIso,
        CachedBootRuleDonor[] ruleDonors)
    {
        Dictionary<int, int> donorByRule = null;
        if (ruleDonors is { Length: > 0 })
        {
            donorByRule = new Dictionary<int, int>(ruleDonors.Length);
            for (var i = 0; i < ruleDonors.Length; i++)
                donorByRule[ruleDonors[i].RuleMapIndex] = ruleDonors[i].DonorMapIndex;
        }

        foreach (var rule in RegionRules)
        {
            var nation = campaign.NationByTag(rule.NationTag);
            if (nation is null) continue;
            var sourceNation = string.IsNullOrWhiteSpace(rule.SourceNationTag)
                ? nation
                : campaign.NationByTag(rule.SourceNationTag);
            if (sourceNation is null) continue;
            if (stateByMapIndex.ContainsKey(rule.MapIndex)) continue;

            ScenarioState donor = null;
            if (donorByRule is not null && donorByRule.TryGetValue(rule.MapIndex, out var donorMapIndex) && donorMapIndex > 0)
                stateByMapIndex.TryGetValue(donorMapIndex, out donor);
            else if (!string.IsNullOrWhiteSpace(rule.DonorIso))
                stateByIso.TryGetValue(rule.DonorIso, out donor);
            AddRegionRuleState(campaign, rule, nation, donor, stateByMapIndex, stateByIso);
        }
    }

    private static CachedBootRuleDonor[] ApplyRegionRules(
        Campaign campaign,
        ushort[] control,
        ushort[] states,
        int width,
        int height,
        Dictionary<int, ScenarioState> stateByMapIndex,
        Dictionary<string, ScenarioState> stateByIso,
        List<RepairSummary> summaries)
    {
        var donors = new List<CachedBootRuleDonor>(RegionRules.Count);
        foreach (var rule in RegionRules)
        {
            var nation = campaign.NationByTag(rule.NationTag);
            if (nation is null) continue;
            var sourceNation = string.IsNullOrWhiteSpace(rule.SourceNationTag)
                ? nation
                : campaign.NationByTag(rule.SourceNationTag);
            if (sourceNation is null) continue;

            if (!stateByMapIndex.ContainsKey(rule.MapIndex))
            {
                var dominant = string.IsNullOrWhiteSpace(rule.DonorIso)
                    ? DominantState(control, states, width, height, (ushort)sourceNation.MapIndex, rule)
                    : -1;
                var donor = string.IsNullOrWhiteSpace(rule.DonorIso)
                    ? stateByMapIndex.GetValueOrDefault(dominant)
                    : stateByIso.GetValueOrDefault(rule.DonorIso);
                AddRegionRuleState(campaign, rule, nation, donor, stateByMapIndex, stateByIso);
                donors.Add(new CachedBootRuleDonor((ushort)rule.MapIndex, (ushort)(donor?.MapIndex ?? 0)));
            }

            var carveFrom = StateIndexByIso(rule.CarveFromIso);
            var changed = PaintRegion(control, states, width, height, (ushort)nation.MapIndex,
                (ushort)sourceNation.MapIndex, (ushort)rule.MapIndex, rule, carveFrom,
                out var connectedComponents, out var clipped);
            summaries.Add(new RepairSummary(rule.Name, "REGION", changed, connectedComponents, clipped));
        }
        return donors.ToArray();
    }

    private static ScenarioState AddRegionRuleState(
        Campaign campaign,
        RegionRule rule,
        Nation nation,
        ScenarioState donor,
        Dictionary<int, ScenarioState> stateByMapIndex,
        Dictionary<string, ScenarioState> stateByIso)
    {
        var state = new ScenarioState
        {
            MapIndex = rule.MapIndex,
            NationMapIndex = nation.MapIndex,
            NationId = nation.Tag,
            Id = rule.Iso.ToLowerInvariant().Replace('-', '_'),
            Name = rule.Name,
            Iso = rule.Iso,
            Kind = rule.Kind
        };
        campaign.States.Add(state);
        stateByMapIndex[rule.MapIndex] = state;
        if (!string.IsNullOrEmpty(state.Iso)) stateByIso.TryAdd(state.Iso, state);

        if (donor is not null && donor != state)
            EconomySystem.AdjustStatePopulation(donor, -rule.Population);
        EconomySystem.SeedState(campaign, state, nation, rule.Population);
        return state;
    }

    private static void ApplyPartitionsMetadataOnly(
        Campaign campaign,
        Dictionary<int, ScenarioState> stateByMapIndex,
        ulong appliedPartitionMask)
    {
        for (var pi = 0; pi < Partitions.Count; pi++)
        {
            if (pi < 64 && (appliedPartitionMask & (1UL << pi)) == 0) continue;
            var partition = Partitions[pi];
            var nation = campaign.NationByTag(CountryMerges.OwnerOf(partition.NationTag));
            var donorIndex = StateIndexByIso(partition.CarveFromIso);
            if ((nation is null && !CountryMerges.IsMerged(partition.NationTag)) || donorIndex == 0) continue;

            var carved = new HashSet<ushort> { donorIndex };
            foreach (var iso in partition.AlsoCarveFromIso ?? Array.Empty<string>())
            {
                var extra = StateIndexByIso(iso);
                if (extra != 0) carved.Add(extra);
            }
            var donor = stateByMapIndex.GetValueOrDefault(donorIndex);
            foreach (var piece in partition.Pieces)
            {
                var state = stateByMapIndex.GetValueOrDefault(piece.MapIndex);
                if (state is null)
                {
                    NewShare(campaign, nation, donor, piece, stateByMapIndex);
                    continue;
                }
                if (!carved.Contains((ushort)piece.MapIndex) || string.IsNullOrWhiteSpace(piece.Name)) continue;
                state.Name = piece.Name;
                if (!partition.Polar && !string.IsNullOrWhiteSpace(piece.Iso))
                {
                    state.Iso = piece.Iso;
                    state.Id = piece.Iso.ToLowerInvariant().Replace('-', '_');
                    Movements.NoteRegionRenamed(campaign);
                }
                if (!string.IsNullOrWhiteSpace(piece.Kind)) state.Kind = piece.Kind;
            }
        }
    }

    private static ulong ApplyPartitionsBatch(
        Campaign campaign,
        ushort[] control,
        ushort[] states,
        int width,
        int height,
        Dictionary<int, ScenarioState> stateByMapIndex,
        List<RepairSummary> summaries)
    {
        if (Partitions.Count == 0) return 0UL;
        var batchBuckets = new List<int>[Partitions.Count];
        var canBatch = new bool[Partitions.Count];
        CollectBatchPartitionGround(campaign, states, canBatch, batchBuckets);

        ulong appliedMask = 0;
        for (var pi = 0; pi < Partitions.Count; pi++)
        {
            var before = summaries.Count;
            ApplyPartition(campaign, control, states, width, height, Partitions[pi], summaries,
                stateByMapIndex, canBatch[pi] ? batchBuckets[pi] : null);
            if (summaries.Count > before && pi < 64) appliedMask |= 1UL << pi;
        }
        return appliedMask;
    }

    private static void CollectBatchPartitionGround(
        Campaign campaign,
        ushort[] states,
        bool[] canBatch,
        List<int>[] batchBuckets)
    {
        var stateToSlot = ArrayPool<int>.Shared.Rent(ushort.MaxValue + 1);
        Array.Clear(stateToSlot, 0, ushort.MaxValue + 1);
        var anyBatch = false;
        try
        {
            for (var pi = 0; pi < Partitions.Count; pi++)
            {
                var partition = Partitions[pi];
                if (partition.Polar) continue;
                var nation = campaign.NationByTag(CountryMerges.OwnerOf(partition.NationTag));
                var donorIndex = StateIndexByIso(partition.CarveFromIso);
                if ((nation is null && !CountryMerges.IsMerged(partition.NationTag)) || donorIndex == 0) continue;

                canBatch[pi] = true;
                MarkShareSlot(stateToSlot, canBatch, pi, donorIndex);
                foreach (var piece in partition.Pieces)
                    MarkShareSlot(stateToSlot, canBatch, pi, (ushort)piece.MapIndex);
                foreach (var iso in partition.AlsoCarveFromIso ?? Array.Empty<string>())
                {
                    var extra = StateIndexByIso(iso);
                    if (extra != 0) MarkShareSlot(stateToSlot, canBatch, pi, extra);
                }
                if (!canBatch[pi]) continue;
                batchBuckets[pi] = new List<int>();
                anyBatch = true;
            }
            if (!anyBatch) return;

            for (var cell = 0; cell < states.Length; cell++)
            {
                var slot = stateToSlot[states[cell]];
                if (slot > 0 && canBatch[slot - 1]) batchBuckets[slot - 1].Add(cell);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(stateToSlot);
        }
    }

    private static void MarkShareSlot(int[] stateToSlot, bool[] canBatch, int pi, ushort idx)
    {
        var existing = stateToSlot[idx];
        if (existing == 0)
        {
            stateToSlot[idx] = pi + 1;
            return;
        }
        if (existing == pi + 1) return;
        canBatch[existing - 1] = false;
        canBatch[pi] = false;
    }

    private static void ApplyCellTransferRules(
        Campaign campaign,
        ushort[] control,
        ushort[] states,
        int width,
        int height,
        List<RepairSummary> summaries)
    {
        if (CellTransfers.Count == 0) return;
        var statesGrid = new Span2D<ushort>(states, height, width);
        var controlGrid = new Span2D<ushort>(control, height, width);
        foreach (var rule in CellTransfers)
        {
            var from = StateIndexByIso(rule.FromIso);
            var to = StateIndexByIso(rule.ToIso);
            var source = campaign.NationByTag(rule.FromNation);
            var destination = campaign.NationByTag(rule.ToNation);
            if (from == 0 || to == 0 || source is null || destination is null) continue;
            var changed = 0;
            foreach (var point in rule.Points)
            {
                var x = LongitudeToX(point.Longitude, width);
                var y = LatitudeToY(point.Latitude, height);
                if (statesGrid[y, x] != from || controlGrid[y, x] != source.MapIndex) continue;
                statesGrid[y, x] = to;
                controlGrid[y, x] = (ushort)destination.MapIndex;
                changed++;
            }
            if (changed > 0) summaries.Add(new RepairSummary(rule.Id, "CELL_TRANSFER", changed));
        }
    }

    private static void ApplyAdjacencyCorridors(
        Campaign campaign,
        ushort[] control,
        ushort[] states,
        int width,
        int height,
        List<RepairSummary> summaries)
    {
        var applied = AppliedRules.GetValue(control, static _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var rule in BaseRules)
        {
            var ruleKey = $"{rule.SubjectTag}>{rule.NeighborTag}:{rule.CorridorRadius}";
            if (!applied.Add(ruleKey)) continue;
            var subject = campaign.NationByTag(rule.SubjectTag);
            var neighbor = campaign.NationByTag(rule.NeighborTag);
            var through = campaign.NationByTag(rule.ThroughTag);
            if (subject is null || neighbor is null || through is null) continue;

            ScenarioState subjectState = null;
            for (var si = 0; si < campaign.States.Count; si++)
                if (campaign.States[si].NationMapIndex == subject.MapIndex) { subjectState = campaign.States[si]; break; }
            if (subjectState is null) continue;

            var changed = Connect(
                control, states, width, height,
                (ushort)subject.MapIndex, (ushort)neighbor.MapIndex, (ushort)through.MapIndex,
                (ushort)subjectState.MapIndex, rule);
            summaries.Add(new RepairSummary(rule.SubjectTag, rule.NeighborTag, changed));
        }
    }

    private static void AppendPrebakedLakeStates(
        Campaign campaign,
        Dictionary<int, ScenarioState> stateByMapIndex,
        CachedBootLake[] lakes)
    {
        for (var i = 0; i < lakes.Length; i++)
        {
            var lake = lakes[i];
            if (stateByMapIndex.TryGetValue(lake.MapIndex, out var existing))
            {
                existing.Name = lake.Name;
                existing.NationMapIndex = 0;
                existing.NationId = "";
                continue;
            }
            var state = new ScenarioState
            {
                MapIndex = lake.MapIndex,
                NationMapIndex = 0,
                NationId = "",
                Id = $"lake_{lake.MapIndex}",
                Name = lake.Name,
                Iso = $"LAKE-{lake.MapIndex}",
                Kind = "Lake"
            };
            campaign.States.Add(state);
            stateByMapIndex[lake.MapIndex] = state;
        }
    }

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const uint SourcePngStampMagic = 0x53504D44u; // "DMPS"
    private const int SourcePngStampVersion = 1;
    private const int SourcePngStampBytes = 64;
    private const int SourcePngStampBodyBytes = SourcePngStampBytes - sizeof(ulong);
    private const string SourcePngStampFileName = "source_png_stamp_v1.bin";
    private const int BitsPerByte = 8;
    private const int BytesPerUInt64 = 8;

    private static void TryCacheBootGrids(
        Campaign campaign,
        ulong partitionMask,
        CachedBootRuleDonor[] ruleDonors,
        ushort[] control,
        ushort[] states,
        int width,
        int height,
        bool forcePersist)
    {
        if (ModOverlayPolicy.IsActive || width != WorldMapSource.Width || height != WorldMapSource.Height)
            return;
        if (!forcePersist &&
            (!ReferenceEquals(control, WorldMapSource.PeekControlIfLoaded()) ||
             !ReferenceEquals(states, WorldMapSource.PeekStatesIfLoaded())))
            return;

        MarkPreRepaired(control, partitionMask, ruleDonors);
        MarkPreRepaired(states, partitionMask, ruleDonors);
        var cacheDir = MapBootCache.DefaultWritableCacheDirectory();
        if (string.IsNullOrWhiteSpace(cacheDir)) return;

        ComputeBootRasterKeys(width, height, out var contentKey, out var sourcePngKey);
        if (MapBootCache.TryReadBootLakes(cacheDir, contentKey, sourcePngKey, width, height, out _, out _, out _, out _))
            return;

        var lakes = CollectBootLakes(campaign);
        var regionFingerprint = MapRasterCatalog.RegionFingerprint(states);
        MapBootCache.TryWriteBootRasters(cacheDir, contentKey, sourcePngKey, regionFingerprint, partitionMask,
            width, height, lakes, control, states, out _, ruleDonors);
    }

    private static CachedBootLake[] CollectBootLakes(Campaign campaign)
    {
        var lakes = new List<CachedBootLake>(LakeRegions.CreatedCount);
        for (var i = 0; i < campaign.States.Count; i++)
        {
            var s = campaign.States[i];
            if (LakeRegions.IsLakeRegion(s.MapIndex))
                lakes.Add(new CachedBootLake((ushort)s.MapIndex, s.Name ?? ""));
        }
        return lakes.ToArray();
    }

    internal static void ComputeBootRasterKeys(int width, int height, out ulong contentKey, out ulong sourcePngKey)
    {
        var hash = FnvOffsetBasis;
        hash = MixUInt64(hash, (ulong)width);
        hash = MixUInt64(hash, (ulong)height);
        hash = MixUInt64(hash, HydrologyCatalog.LakeFingerprint);
        hash = MixUInt64(hash, (ulong)LakeRegions.MinimumCells);
        hash = MixUInt64(hash, (ulong)LakeRegions.MinimumWidthCells);
        hash = MixUInt64(hash, (ulong)LakeRegions.FirstMapIndex);
        hash = MixStrings(hash, WorldMapTables.CountryRows);
        hash = MixStrings(hash, WorldMapTables.StateRows);
        hash = MixCountryMerges(hash);
        hash = MixAdjacencyRules(hash);
        hash = MixRegionRules(hash);
        hash = MixPartitions(hash);
        hash = MixCellTransfers(hash);
        contentKey = hash;
        sourcePngKey = ResolveSourcePngKey();
    }

    private static ulong MixCountryMerges(ulong hash)
    {
        var merged = CountryMerges.GroundByMapIndex();
        hash = MixUInt64(hash, (ulong)merged.Count);
        if (merged.Count == 0) return hash;
        var pairs = merged.OrderBy(kv => kv.Key);
        foreach (var kv in pairs)
        {
            hash = MixUInt64(hash, (ulong)kv.Key);
            hash = MixUInt64(hash, (ulong)kv.Value);
        }
        return hash;
    }

    private static ulong MixAdjacencyRules(ulong hash)
    {
        hash = MixUInt64(hash, (ulong)BaseRules.Count);
        foreach (var r in BaseRules)
        {
            hash = MixString(hash, r.SubjectTag);
            hash = MixString(hash, r.NeighborTag);
            hash = MixString(hash, r.ThroughTag);
            hash = MixFloat(hash, r.MinLongitude);
            hash = MixFloat(hash, r.MaxLongitude);
            hash = MixFloat(hash, r.MinLatitude);
            hash = MixFloat(hash, r.MaxLatitude);
            hash = MixUInt64(hash, (ulong)r.CorridorRadius);
        }
        return hash;
    }

    private static ulong MixRegionRules(ulong hash)
    {
        hash = MixUInt64(hash, (ulong)RegionRules.Count);
        foreach (var r in RegionRules)
        {
            hash = MixUInt64(hash, (ulong)r.MapIndex);
            hash = MixString(hash, r.NationTag);
            hash = MixString(hash, r.SourceNationTag);
            hash = MixString(hash, r.Name);
            hash = MixString(hash, r.Iso);
            hash = MixString(hash, r.Kind);
            hash = MixString(hash, r.DonorIso);
            hash = MixUInt64(hash, (ulong)r.Population);
            hash = MixFloat(hash, r.MinLongitude);
            hash = MixFloat(hash, r.MaxLongitude);
            hash = MixFloat(hash, r.MinLatitude);
            hash = MixFloat(hash, r.MaxLatitude);
            hash = MixString(hash, r.CarveFromIso);
            hash = MixUInt64(hash, r.ReplaceExisting ? 1UL : 0UL);
            hash = MixUInt64(hash, (ulong)r.Polygon.Length);
            for (var i = 0; i < r.Polygon.Length; i++)
            {
                hash = MixFloat(hash, r.Polygon[i].Longitude);
                hash = MixFloat(hash, r.Polygon[i].Latitude);
            }
            hash = MixUInt64(hash, (ulong)r.Seeds.Length);
            for (var i = 0; i < r.Seeds.Length; i++)
            {
                hash = MixFloat(hash, r.Seeds[i].Longitude);
                hash = MixFloat(hash, r.Seeds[i].Latitude);
                hash = MixUInt64(hash, (ulong)r.Seeds[i].RadiusCells);
            }
        }
        return hash;
    }

    private static ulong MixPartitions(ulong hash)
    {
        hash = MixUInt64(hash, (ulong)Partitions.Count);
        foreach (var p in Partitions)
        {
            hash = MixString(hash, p.NationTag);
            hash = MixString(hash, p.CarveFromIso);
            hash = MixUInt64(hash, p.Polar ? 1UL : 0UL);
            hash = MixStrings(hash, p.AlsoCarveFromIso ?? Array.Empty<string>());
            hash = MixUInt64(hash, (ulong)p.Pieces.Length);
            foreach (var piece in p.Pieces)
            {
                hash = MixUInt64(hash, (ulong)piece.MapIndex);
                hash = MixString(hash, piece.Name);
                hash = MixString(hash, piece.Iso);
                hash = MixString(hash, piece.Kind);
                hash = MixUInt64(hash, (ulong)piece.Population);
                hash = MixFloat(hash, piece.Longitude);
                hash = MixFloat(hash, piece.Latitude);
            }
        }
        return hash;
    }

    private static ulong MixCellTransfers(ulong hash)
    {
        hash = MixUInt64(hash, (ulong)CellTransfers.Count);
        foreach (var t in CellTransfers)
        {
            hash = MixString(hash, t.Id);
            hash = MixString(hash, t.FromIso);
            hash = MixString(hash, t.ToIso);
            hash = MixString(hash, t.FromNation);
            hash = MixString(hash, t.ToNation);
            hash = MixUInt64(hash, (ulong)t.Points.Length);
            for (var i = 0; i < t.Points.Length; i++)
            {
                hash = MixFloat(hash, t.Points[i].Longitude);
                hash = MixFloat(hash, t.Points[i].Latitude);
            }
        }
        return hash;
    }

    private static ulong ResolveSourcePngKey()
    {
        var ctrlPath = MapRasterCatalog.SourceControlPath;
        var statesPath = MapRasterCatalog.SourceStatesPath;
        if (!TryStatFile(ctrlPath, out var ctrlLen, out var ctrlTicks) ||
            !TryStatFile(statesPath, out var statesLen, out var statesTicks))
            return 0UL;

        lock (BootKeys)
        {
            if (BootKeys.SourcePngKey != 0 &&
                BootKeys.ControlLen == ctrlLen && BootKeys.ControlTicks == ctrlTicks &&
                BootKeys.StatesLen == statesLen && BootKeys.StatesTicks == statesTicks &&
                string.Equals(BootKeys.ControlPath, ctrlPath, StringComparison.Ordinal) &&
                string.Equals(BootKeys.StatesPath, statesPath, StringComparison.Ordinal))
                return BootKeys.SourcePngKey;

            var pathHash = MixString(MixString(FnvOffsetBasis, ctrlPath), statesPath);
            var cacheDir = ModOverlayPolicy.IsActive ? null : MapBootCache.DefaultWritableCacheDirectory();
            if (!TryReadSourcePngStamp(cacheDir, pathHash, ctrlLen, ctrlTicks, statesLen, statesTicks, out var key))
            {
                var ctrlHash = MapBootCache.ComputeFileFnv1a64(ctrlPath);
                var stateHash = MapBootCache.ComputeFileFnv1a64(statesPath);
                key = ctrlHash != 0 && stateHash != 0 ? MixUInt64(ctrlHash, stateHash) : 0UL;
                if (key != 0 && !string.IsNullOrWhiteSpace(cacheDir))
                    TryWriteSourcePngStamp(cacheDir, pathHash, ctrlLen, ctrlTicks, statesLen, statesTicks, key);
            }

            BootKeys.ControlPath = ctrlPath;
            BootKeys.ControlLen = ctrlLen;
            BootKeys.ControlTicks = ctrlTicks;
            BootKeys.StatesPath = statesPath;
            BootKeys.StatesLen = statesLen;
            BootKeys.StatesTicks = statesTicks;
            BootKeys.SourcePngKey = key;
            return key;
        }
    }

    private static bool TryStatFile(string path, out long length, out long lastWriteTicks)
    {
        length = 0;
        lastWriteTicks = 0;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var info = new FileInfo(path);
        length = info.Length;
        lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        return length > 0;
    }

    private static bool TryReadSourcePngStamp(
        string cacheDir,
        ulong expectedPathHash,
        long ctrlLen,
        long ctrlTicks,
        long statesLen,
        long statesTicks,
        out ulong sourcePngKey)
    {
        sourcePngKey = 0;
        if (string.IsNullOrWhiteSpace(cacheDir)) return false;
        var path = Path.Combine(cacheDir, SourcePngStampFileName);
        if (!File.Exists(path)) return false;
        try
        {
            Span<byte> buf = stackalloc byte[SourcePngStampBytes];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != SourcePngStampBytes) return false;
                stream.ReadExactly(buf);
            }
            var offset = 0;
            if (ReadUInt32(buf, ref offset) != SourcePngStampMagic ||
                ReadInt32(buf, ref offset) != SourcePngStampVersion ||
                ReadUInt64(buf, ref offset) != expectedPathHash ||
                ReadInt64(buf, ref offset) != ctrlLen ||
                ReadInt64(buf, ref offset) != ctrlTicks ||
                ReadInt64(buf, ref offset) != statesLen ||
                ReadInt64(buf, ref offset) != statesTicks)
                return false;
            var key = ReadUInt64(buf, ref offset);
            var check = ReadUInt64(buf, ref offset);
            if (key == 0 || check != MapBootCache.ComputeFnv1a64(buf[..SourcePngStampBodyBytes])) return false;
            sourcePngKey = key;
            return true;
        }
        catch { return false; }
    }

    private static void TryWriteSourcePngStamp(
        string cacheDir,
        ulong pathHash,
        long ctrlLen,
        long ctrlTicks,
        long statesLen,
        long statesTicks,
        ulong sourcePngKey)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            Span<byte> buf = stackalloc byte[SourcePngStampBytes];
            var offset = 0;
            WriteUInt32(buf, ref offset, SourcePngStampMagic);
            WriteInt32(buf, ref offset, SourcePngStampVersion);
            WriteUInt64(buf, ref offset, pathHash);
            WriteInt64(buf, ref offset, ctrlLen);
            WriteInt64(buf, ref offset, ctrlTicks);
            WriteInt64(buf, ref offset, statesLen);
            WriteInt64(buf, ref offset, statesTicks);
            WriteUInt64(buf, ref offset, sourcePngKey);
            WriteUInt64(buf, ref offset, MapBootCache.ComputeFnv1a64(buf[..SourcePngStampBodyBytes]));
            var path = Path.Combine(cacheDir, SourcePngStampFileName);
            var tmp = $"{path}.tmp.{Guid.NewGuid():N}";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(buf);
                stream.Flush();
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* Non-fatal performance stamp cache. */ }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> s, ref int o) { var v = BinaryPrimitives.ReadUInt32LittleEndian(s[o..]); o += sizeof(uint); return v; }
    private static int ReadInt32(ReadOnlySpan<byte> s, ref int o) { var v = BinaryPrimitives.ReadInt32LittleEndian(s[o..]); o += sizeof(int); return v; }
    private static long ReadInt64(ReadOnlySpan<byte> s, ref int o) { var v = BinaryPrimitives.ReadInt64LittleEndian(s[o..]); o += sizeof(long); return v; }
    private static ulong ReadUInt64(ReadOnlySpan<byte> s, ref int o) { var v = BinaryPrimitives.ReadUInt64LittleEndian(s[o..]); o += sizeof(ulong); return v; }
    private static void WriteUInt32(Span<byte> s, ref int o, uint v) { BinaryPrimitives.WriteUInt32LittleEndian(s[o..], v); o += sizeof(uint); }
    private static void WriteInt32(Span<byte> s, ref int o, int v) { BinaryPrimitives.WriteInt32LittleEndian(s[o..], v); o += sizeof(int); }
    private static void WriteInt64(Span<byte> s, ref int o, long v) { BinaryPrimitives.WriteInt64LittleEndian(s[o..], v); o += sizeof(long); }
    private static void WriteUInt64(Span<byte> s, ref int o, ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(s[o..], v); o += sizeof(ulong); }

    private static ulong MixFloat(ulong hash, float value) =>
        MixUInt64(hash, BitConverter.SingleToUInt32Bits(value));

    private static ulong MixUInt64(ulong hash, ulong value)
    {
        for (var i = 0; i < BytesPerUInt64; i++)
            hash = (hash ^ (byte)(value >> (i * BitsPerByte))) * FnvPrime;
        return hash;
    }

    private static ulong MixString(ulong hash, string value)
    {
        if (string.IsNullOrEmpty(value)) return MixUInt64(hash, 0UL);
        hash = MixUInt64(hash, (ulong)value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            hash = (hash ^ (byte)ch) * FnvPrime;
            hash = (hash ^ (byte)(ch >> BitsPerByte)) * FnvPrime;
        }
        return hash;
    }

    private static ulong MixStrings(ulong hash, string[] values)
    {
        hash = MixUInt64(hash, (ulong)values.Length);
        for (var i = 0; i < values.Length; i++) hash = MixString(hash, values[i]);
        return hash;
    }

    /// <summary>Heal detached 1–2 province border slivers deterministically without changing <c>compiled.json</c>.</summary>
    public static IReadOnlyList<SliverHealSummary> ApplyProvinceSliverHealing(ProvinceTopology topology) =>
        HealBorderSlivers(topology);

    private static ushort StateIndexByIso(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return 0;
        if (_stateIndexByIso is null)
        {
            _stateIndexByIso = new Dictionary<string, ushort>(WorldMapTables.StateRows.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var row in WorldMapTables.StateRows)
            {
                var span = row.AsSpan();
                var p0 = span.IndexOf('|');
                if (p0 <= 0) continue;
                var s0 = span[..p0];
                span = span[(p0 + 1)..];
                var p1 = span.IndexOf('|');
                if (p1 < 0) continue;
                span = span[(p1 + 1)..];
                var p2 = span.IndexOf('|');
                if (p2 < 0) continue;
                span = span[(p2 + 1)..];
                var p3 = span.IndexOf('|');
                var s3 = p3 >= 0 ? span[..p3] : span;
                if (!int.TryParse(s0, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index) ||
                    index <= 0 || index > ushort.MaxValue || s3.IsEmpty) continue;
                _stateIndexByIso[s3.ToString()] = (ushort)index;
            }
        }
        return _stateIndexByIso.TryGetValue(iso, out var found) ? found : (ushort)0;
    }

    private static int LongitudeToX(float longitude, int width) =>
        Math.Clamp((int)((longitude + 180f) / 360f * width), 0, width - 1);

    private static int LatitudeToY(float latitude, int height) =>
        Math.Clamp((int)((90f - latitude) / 180f * height), 0, height - 1);

    private static float XToLongitude(int x, int width) => -180f + (x + 0.5f) * 360f / width;
    private static float YToLatitude(int y, int height) => 90f - (y + 0.5f) * 180f / height;
}
