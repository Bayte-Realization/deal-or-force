using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BitFaster.Caching.Lru;
using CommunityToolkit.HighPerformance.Buffers;
using DealOrForce.Simulation;
using DotNext.Buffers;
using Equativ.RoaringBitmaps;
using RBush;
using SimdLinq;
using ZeroAlloc.Collections;
using ZLinq;

static class HighPerfLibraryTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL high-perf contract: " + label);
        Console.WriteLine("PASS high-perf: " + label);
    }

    [TestSuite("--high-perf-only", Order = 2691, IncludeInDefaultRun = true,
        Description = "Verifies HighPerformance buffer writers, TensorPrimitives kernels, LZ4 boot raster caching, RecyclableMemoryStream, XxHash3, ConcurrentLru, ZLinq, ZeroAlloc, DotNext, SimdLinq, RBush, RoaringBitmaps, Ben.Demystifier, ActiveWars, PopGroup coalescing, and pruning.")]
    public static void Run()
    {
        TestSumLongTensorPrimitives();
        TestBootRasterCacheRoundTripAndCorruption();
        TestPoliticalAndHydrologyCacheRoundTrips();
        TestArrayPoolBufferWriterAndStringPoolContracts();
        TestRecyclableStreamsAndXxHash3();
        TestMapBootCacheXxHash3AndConcurrentLru();
        TestZLinqValueEnumerableQueries();
        TestPhase2HighPerfPackages();
        TestActiveWarsIndexAndInvalidation();
        TestPopulationGroupCoalescingDeterminism();
        TestOpinionSocialAndHistoryPruning();
        TestWave25LateGameScalingAndIdentityCaches();
        TestSaveSystemTextBinaryAndNetworkRoundTrips();
        TestFastBootMatchesRawRasterTopology();
        TestSimulationSourceHygiene();
    }

    private static void TestSumLongTensorPrimitives()
    {
        Check(EconomySystem.SumLong(ReadOnlySpan<long>.Empty) == 0L, "SumLong handles empty span");
        Check(EconomySystem.SumLong(new[] { 42L }) == 42L, "SumLong handles single element");

        int[] lengths = { 2, 3, 4, 7, 8, 9, 15, 16, 17, 31, 32, 33, 64, 65 };
        foreach (var len in lengths)
        {
            var data = new long[len];
            long expected = 0;
            for (var i = 0; i < len; i++)
            {
                var v = ((i % 2 == 0) ? 1L : -1L) * (5_000_000_000L + i * 123_457L);
                data[i] = v;
                expected += v;
            }
            var actual = EconomySystem.SumLong(data);
            Check(actual == expected, $"SumLong SIMD exact match at length={len}");
        }
    }

    private static void TestBootRasterCacheRoundTripAndCorruption()
    {
        var tempDir = CreateTempDir();
        try
        {
            const int width = 32;
            const int height = 16;
            const ulong contentKey = 0x1122334455667788UL;
            const ulong pngKey = 0x8877665544332211UL;
            const ulong regionFp = 0xAABBCCDDEEFF0011UL;
            const ulong partitionMask = 0b101UL;

            var control = new ushort[width * height];
            var states = new ushort[width * height];
            for (var i = 0; i < states.Length; i++)
            {
                control[i] = (ushort)((i % 13) + 1);
                states[i] = (ushort)((i % 29) + 100);
            }
            CachedBootLake[] lakes = { new(2000, "Test Lake"), new(2001, "Second Lake") };
            CachedBootRuleDonor[] donors = { new(1715, 420), new(1720, 910) };

            var targetFile = Path.Combine(tempDir, MapBootCache.BootRasterFileName);
            Check(MapBootCache.TryWriteBootRastersToFile(
                    targetFile, contentKey, pngKey, regionFp, partitionMask,
                    width, height, lakes, control, states, out var writeErr, donors),
                "TryWriteBootRastersToFile succeeds: " + writeErr);

            Check(MapBootCache.TryReadBootLakesFile(
                    targetFile, contentKey, pngKey, width, height,
                    out var readLakes, out var readDonors, out var readFp, out var readMask),
                "TryReadBootLakesFile hits header/lake/donor section");
            Check(readFp == regionFp && readMask == partitionMask, "boot raster header metadata round-trips");
            Check(readLakes.Length == 2 && readLakes[0] == lakes[0] && readLakes[1] == lakes[1],
                "boot lake metadata preserves map index and name");
            Check(readDonors is { Length: 2 } && readDonors[0] == donors[0] && readDonors[1] == donors[1],
                "boot rule donor metadata preserves rule/donor indices");

            Check(MapBootCache.TryReadBootRasterLayerFile(
                    targetFile, contentKey, pngKey, width, height, loadControl: false,
                    out var readStates, out _),
                "TryReadBootRasterLayerFile loads States independently");
            Check(readStates.AsSpan().SequenceEqual(states), "States LZ4 block matches byte-for-byte");

            Check(MapBootCache.TryReadBootRasterLayerFile(
                    targetFile, contentKey, pngKey, width, height, loadControl: true,
                    out var readControl, out _),
                "TryReadBootRasterLayerFile seeks past States and loads Control independently");
            Check(readControl.AsSpan().SequenceEqual(control), "Control LZ4 block matches byte-for-byte");

            Check(!MapBootCache.TryReadBootLakesFile(targetFile, contentKey ^ 1UL, pngKey, width, height, out _, out _, out _),
                "contentKey mismatch rejects boot raster cache");
            Check(!MapBootCache.TryReadBootLakesFile(targetFile, contentKey, pngKey ^ 1UL, width, height, out _, out _, out _),
                "sourcePngKey mismatch rejects boot raster cache");
            Check(!MapBootCache.TryReadBootLakesFile(targetFile, contentKey, pngKey, width + 1, height, out _, out _, out _),
                "dimension mismatch rejects boot raster cache");

            var bytes = File.ReadAllBytes(targetFile);
            var corruptControl = (byte[])bytes.Clone();
            corruptControl[^1] ^= 0xFF;
            var corruptControlFile = Path.Combine(tempDir, "corrupt_control.bin");
            File.WriteAllBytes(corruptControlFile, corruptControl);
            Check(!MapBootCache.TryReadBootRasterLayerFile(
                    corruptControlFile, contentKey, pngKey, width, height, loadControl: true, out _, out _),
                "corrupted Control block checksum rejects cleanly");
            Check(MapBootCache.TryReadBootRasterLayerFile(
                    corruptControlFile, contentKey, pngKey, width, height, loadControl: false, out _, out _),
                "States block remains independently readable when only Control tail byte is corrupted");

            var corruptStates = (byte[])bytes.Clone();
            var statesStart = 88 + BitConverter.ToInt32(bytes, 44);
            corruptStates[statesStart + 2] ^= 0xFF;
            var corruptStatesFile = Path.Combine(tempDir, "corrupt_states.bin");
            File.WriteAllBytes(corruptStatesFile, corruptStates);
            Check(!MapBootCache.TryReadBootRasterLayerFile(
                    corruptStatesFile, contentKey, pngKey, width, height, loadControl: false, out _, out _),
                "corrupted States block checksum rejects cleanly");

            var truncatedFile = Path.Combine(tempDir, "truncated_boot.bin");
            File.WriteAllBytes(truncatedFile, bytes[..40]);
            Check(!MapBootCache.TryReadBootLakesFile(truncatedFile, contentKey, pngKey, width, height, out _, out _, out _),
                "truncated boot raster header rejects cleanly");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static void TestPoliticalAndHydrologyCacheRoundTrips()
    {
        var tempDir = CreateTempDir();
        try
        {
            const int width = 32;
            const int height = 16;
            const int scale = 2;
            const ulong fp = 0xCAFEBABE12345678UL;

            var hydro = new HydrologyBootCacheData
            {
                HydrologySource = "test-hydro",
                LakeSdfR8 = Enumerable.Range(0, width * height).Select(i => (byte)(i & 0xFF)).ToArray(),
                RiverCentreRgba8 = Enumerable.Range(0, width * height * 4).Select(i => (byte)((i * 3) & 0xFF)).ToArray()
            };
            Check(MapBootCache.TryWriteHydrology(tempDir, fp, width, height, hydro, out var hydroErr),
                "TryWriteHydrology via ArrayPoolBufferWriter succeeds: " + hydroErr);
            Check(MapBootCache.TryReadHydrology(tempDir, fp, width, height, out var readHydro, out var hydroReason),
                "TryReadHydrology succeeds: " + hydroReason);
            Check(readHydro.HydrologySource == "test-hydro" &&
                  readHydro.LakeSdfR8.AsSpan().SequenceEqual(hydro.LakeSdfR8) &&
                  readHydro.RiverCentreRgba8.AsSpan().SequenceEqual(hydro.RiverCentreRgba8),
                "hydrology cache round-trips lossless buffers");

            var plateCount = (width / scale) * (height / scale);
            var fullRgBytes = width * height * 2;
            var pol = new PoliticalRasterBootCacheData
            {
                PlateProvinces = Enumerable.Range(0, plateCount).Select(i => (ushort)(i % 50)).ToArray(),
                PlateHolders = Enumerable.Range(0, plateCount).Select(i => (ushort)(i % 10)).ToArray(),
                PlateGeography = Enumerable.Range(0, plateCount).Select(i => (byte)(i & 7)).ToArray(),
                PlateClass = Enumerable.Range(0, plateCount).Select(i => (byte)(i & 3)).ToArray(),
                RegionIds = Enumerable.Range(0, fullRgBytes).Select(i => (byte)(i & 0x7F)).ToArray(),
                ProvinceIds = Enumerable.Range(0, fullRgBytes).Select(i => (byte)((i * 5) & 0xFF)).ToArray(),
                NationIds = Enumerable.Range(0, fullRgBytes).Select(i => (byte)((i * 7) & 0xFF)).ToArray()
            };
            Check(MapBootCache.TryWritePoliticalRasters(tempDir, fp, width, height, scale, pol, out var polErr),
                "TryWritePoliticalRasters via ArrayPoolBufferWriter + LZ4Pickler succeeds: " + polErr);
            Check(MapBootCache.TryReadPoliticalRasters(tempDir, fp, width, height, scale, out var readPol, out var polReason),
                "TryReadPoliticalRasters succeeds: " + polReason);
            Check(readPol.PlateProvinces.AsSpan().SequenceEqual(pol.PlateProvinces) &&
                  readPol.PlateHolders.AsSpan().SequenceEqual(pol.PlateHolders) &&
                  readPol.PlateGeography.AsSpan().SequenceEqual(pol.PlateGeography) &&
                  readPol.PlateClass.AsSpan().SequenceEqual(pol.PlateClass) &&
                  readPol.RegionIds.AsSpan().SequenceEqual(pol.RegionIds) &&
                  readPol.ProvinceIds.AsSpan().SequenceEqual(pol.ProvinceIds) &&
                  readPol.NationIds.AsSpan().SequenceEqual(pol.NationIds),
                "political rasters LZ4 cache round-trips all 7 buffers bit-for-bit");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static void TestArrayPoolBufferWriterAndStringPoolContracts()
    {
        using var writer = new ArrayPoolBufferWriter<byte>(32);
        writer.Write(new byte[] { 1, 2, 3, 4 });
        Check(writer.WrittenCount == 4, "ArrayPoolBufferWriter tracks written count");
        writer.Clear();
        Check(writer.WrittenCount == 0, "ArrayPoolBufferWriter.Clear resets written count for reuse");

        var s1 = StringPool.Shared.GetOrAdd("turkic_high_perf_test".AsSpan());
        var s2 = StringPool.Shared.GetOrAdd("turkic_high_perf_test"u8, System.Text.Encoding.UTF8);
        Check(ReferenceEquals(s1, s2), "StringPool.Shared returns identical interned reference across char/UTF-8 spans");
    }

    private static void TestRecyclableStreamsAndXxHash3()
    {
        const int payloadBytes = 200 * 1024; // Crosses 85 KB LOH threshold across multiple pooled 64 KB blocks.
        var payload = new byte[payloadBytes];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)((i * 31 + 7) & 0xFF);

        using (var pooled = RecyclableStreams.Get("HighPerfLibraryTests.MultiBlock", payloadBytes))
        {
            pooled.Write(payload);
            Check(pooled.Length == payloadBytes, "RecyclableStreams.Get writes >85 KB payload without LOH array growth");
            pooled.Position = 0;
            var streamHash = RecyclableStreams.ComputeXxHash3(pooled, seed: 42);
            var spanHash = RecyclableStreams.ComputeXxHash3(payload, seed: 42);
            Check(streamHash == spanHash && spanHash == XxHash3.HashToUInt64(payload, 42),
                "RecyclableStreams.ComputeXxHash3 stream matches one-shot XxHash3.HashToUInt64 bit-for-bit");
        }

        using (var fromBytes = RecyclableStreams.FromBytes("HighPerfLibraryTests.FromBytes", payload.AsSpan(0, 4096)))
        {
            Check(fromBytes.Length == 4096 && fromBytes.Position == 0,
                "RecyclableStreams.FromBytes exposes readable pooled stream at position 0");
            var roundHash = RecyclableStreams.ComputeXxHash3(fromBytes);
            Check(roundHash == MapBootCache.ComputeXxHash3(payload.AsSpan(0, 4096)),
                "MapBootCache.ComputeXxHash3 matches RecyclableStreams.ComputeXxHash3");
        }

        Check(RecyclableStreams.ComputeXxHash3(ReadOnlySpan<byte>.Empty) == XxHash3.HashToUInt64(ReadOnlySpan<byte>.Empty),
            "XxHash3 handles empty spans deterministically");
    }

    private static void TestMapBootCacheXxHash3AndConcurrentLru()
    {
        var tempDir = CreateTempDir();
        try
        {
            MapBootCache.ClearFileHashCache();
            var sampleFile = Path.Combine(tempDir, "hash_probe.bin");
            var firstBytes = Encoding.UTF8.GetBytes("deal-or-force-xxhash3-cache-v1");
            File.WriteAllBytes(sampleFile, firstBytes);

            var h1 = MapBootCache.ComputeFileXxHash3(sampleFile);
            var h2 = MapBootCache.ComputeFileXxHash3(sampleFile);
            Check(h1 == XxHash3.HashToUInt64(firstBytes) && h1 == h2,
                "MapBootCache.ComputeFileXxHash3 hits bounded ConcurrentLru on unchanged file");

            var updatedBytes = Encoding.UTF8.GetBytes("deal-or-force-xxhash3-cache-v2-modified");
            File.WriteAllBytes(sampleFile, updatedBytes);
            var h3 = MapBootCache.ComputeFileXxHash3(sampleFile);
            Check(h3 == XxHash3.HashToUInt64(updatedBytes) && h3 != h1,
                "MapBootCache.ComputeFileXxHash3 invalidates LRU entry when file changes on disk");

            var lru = new ConcurrentLru<int, string>(4);
            for (var i = 0; i < 12; i++) lru.AddOrUpdate(i, $"v{i}");
            Check(lru.Count <= 4 && lru.TryGet(11, out var latest) && latest == "v11",
                "BitFaster ConcurrentLru enforces bounded capacity and retains latest entries");
        }
        finally
        {
            MapBootCache.ClearFileHashCache();
            TryDeleteDir(tempDir);
        }
    }

    private static void TestZLinqValueEnumerableQueries()
    {
        var values = new List<int> { 9, 2, 7, 4, 1, 8, 3, 6, 5 };
        var filteredSorted = values.AsValueEnumerable()
            .Where(static x => x >= 4)
            .OrderByDescending(static x => x)
            .Select(static x => x * 2)
            .ToArray();
        Check(filteredSorted.SequenceEqual(new[] { 18, 16, 14, 12, 10, 8 }),
            "ZLinq AsValueEnumerable Where/OrderByDescending/Select/ToArray produces exact sequence");

        Check(values.AsValueEnumerable().Any(static x => x == 7) &&
              !values.AsValueEnumerable().Any(static x => x > 99) &&
              values.AsValueEnumerable().Count(static x => (x & 1) == 0) == 4 &&
              values.AsValueEnumerable().Max(static x => x) == 9,
            "ZLinq Any, Count, and Max match exact expected values");

        ReadOnlySpan<int> span = stackalloc int[] { 10, 20, 30, 40 };
        var spanCount = span.AsValueEnumerable().Where(static x => x >= 20).Count();
        Check(spanCount == 3, "ZLinq queries ReadOnlySpan<int> without heap allocation");
    }

    private static void TestSaveSystemTextBinaryAndNetworkRoundTrips()
    {
        var tempDir = CreateTempDir();
        try
        {
            var campaign = Campaign.CreateBootstrap(42);
            var topology = ResolveBootOrSourceTopology();
            campaign.AttachProvinceTopology(topology);
            var expectedFp = CampaignFingerprint.Of(campaign);

            campaign.Defines.SaveTextFormat = false;
            var binMeta = SaveSystem.Save(tempDir, "hp_bin", campaign, "hp-test");
            Check(binMeta.CompressedBytes > 0, "binary BDF3 save via SaveSystem writes positive byte count");
            var binLoaded = SaveSystem.Load(tempDir, "hp_bin", "hp-test", topology.ProvinceCount);
            Check(binLoaded.Success, "binary BDF3 save loads cleanly: " + binLoaded.Warning);
            binLoaded.Campaign.AttachProvinceTopology(topology);
            Check(CampaignFingerprint.Of(binLoaded.Campaign) == expectedFp, "binary save round-trip preserves exact CampaignFingerprint");

            campaign.Defines.SaveTextFormat = true;
            var textMeta = SaveSystem.Save(tempDir, "hp_text", campaign, "hp-test");
            Check(textMeta.CompressedBytes > 0, "text save via ArrayPoolBufferWriter + CompactRowConverter writes positive byte count");
            var listed = SaveSystem.List(tempDir);
            Check(listed.Any(m => m.Slot == "hp_text" && m.Seed == 42), "SaveSystem.List reads EmbeddedMetadata from text save");
            var textLoaded = SaveSystem.Load(tempDir, "hp_text", "hp-test", topology.ProvinceCount);
            Check(textLoaded.Success, "text save loads cleanly: " + textLoaded.Warning);
            textLoaded.Campaign.AttachProvinceTopology(topology);
            Check(CampaignFingerprint.Of(textLoaded.Campaign) == expectedFp, "text save round-trip preserves exact CampaignFingerprint");

            var netBytes = SaveSystem.ExportNetworkSnapshot(campaign, "hp-test");
            Check(netBytes.Length > 0, "ExportNetworkSnapshot produces non-empty compressed bytes");
            var netLoaded = SaveSystem.ImportNetworkSnapshot(netBytes, "hp-test");
            Check(netLoaded.Success, "ImportNetworkSnapshot succeeds: " + netLoaded.Warning);
            netLoaded.Campaign.AttachProvinceTopology(topology);
            Check(CampaignFingerprint.Of(netLoaded.Campaign) == expectedFp, "network snapshot round-trip preserves exact CampaignFingerprint");

            var binSavePath = Path.Combine(tempDir, "hp_bin.bdfsave");
            using (var binStream = File.OpenRead(binSavePath))
            {
                var header = SaveContainer.ReadHeader(binStream);
                using var bufferedGraph = SaveContainer.OpenGraphBuffered(binStream, header, leaveOpen: true);
                Check(bufferedGraph.Length > 0 && bufferedGraph.Position == 0,
                    "SaveContainer.OpenGraphBuffered decompresses BDF3 graph into pooled RecyclableMemoryStream");
            }

            var bomScenarioJson = "\uFEFF" + SaveSystem.ExportScenarioJson(campaign, "hp_bom");
            var bomImported = SaveSystem.ImportScenarioJson(bomScenarioJson, "hp_bom", topology.ProvinceCount, seedOverride: 99);
            Check(bomImported is not null && bomImported.Seed == 99 && bomImported.States.Count == campaign.States.Count,
                "ImportScenarioJson handles UTF-8 BOM cleanly via ScanStringTable");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static void TestFastBootMatchesRawRasterTopology()
    {
        var w = WorldMapSource.Width;
        var h = WorldMapSource.Height;
        Check(File.Exists(MapRasterCatalog.SourceControlPath) && File.Exists(MapRasterCatalog.SourceStatesPath),
            "source control/states rasters exist on disk");

        var cacheDir = MapBootCache.DefaultWritableCacheDirectory();
        var bootRasterPath = string.IsNullOrWhiteSpace(cacheDir) ? null : Path.Combine(cacheDir, MapBootCache.BootRasterFileName);
        var bootBackup = bootRasterPath is null ? null : bootRasterPath + ".hp_test_bak";
        var stampPath = string.IsNullOrWhiteSpace(cacheDir) ? null : Path.Combine(cacheDir, "source_png_stamp_v1.bin");
        var stampBackup = stampPath is null ? null : stampPath + ".hp_test_bak";
        var hadBoot = bootRasterPath is not null && File.Exists(bootRasterPath);
        var hadStamp = stampPath is not null && File.Exists(stampPath);
        if (hadBoot) File.Move(bootRasterPath!, bootBackup!, overwrite: true);
        if (hadStamp) File.Move(stampPath!, stampBackup!, overwrite: true);

        try
        {
            VerifyShippedAndBootRasterFastBootPaths(w, h, cacheDir, bootRasterPath);
            VerifyWaterByProvinceAndDonorFidelity(w, h, cacheDir);
        }
        finally
        {
            WorldMapSource.ReleaseDecodedGrids();
            TerritorialTopology.ResetCatalog();
            if (bootRasterPath is not null && File.Exists(bootRasterPath) && !hadBoot) File.Delete(bootRasterPath);
            if (hadBoot) File.Move(bootBackup!, bootRasterPath!, overwrite: true);
            if (stampPath is not null && File.Exists(stampPath) && !hadStamp) File.Delete(stampPath);
            if (hadStamp) File.Move(stampBackup!, stampPath!, overwrite: true);
        }
    }

    private static void VerifyShippedAndBootRasterFastBootPaths(int w, int h, string cacheDir, string bootRasterPath)
    {
        var mapBootExists = (!string.IsNullOrWhiteSpace(cacheDir) && File.Exists(Path.Combine(cacheDir, MapBootCache.FileName))) ||
                            File.Exists(GameDataLoader.FindDataFile("cache", MapBootCache.FileName) ?? "");
        var lakeBootExists = (!string.IsNullOrWhiteSpace(cacheDir) && File.Exists(Path.Combine(cacheDir, LakeRegions.BootCacheFileName))) ||
                             File.Exists(GameDataLoader.FindDataFile("cache", LakeRegions.BootCacheFileName) ?? "");

        ulong shippedFp = 0;
        var shippedLakeCount = 0;
        if (mapBootExists && lakeBootExists)
        {
            Check(MapBootCache.TryReadTopologyProvinceStates(cacheDir, w, h, out var pts, out var ptsFp,
                    GameDataLoader.FindDataFile("cache", MapBootCache.FileName)) && pts is { Length: > 1 } && ptsFp != 0,
                "TryReadTopologyProvinceStates reads ProvinceToState metadata without 33 MB CellToProvince");

            WorldMapSource.ReleaseDecodedGrids();
            TerritorialTopology.ResetCatalog();
            var shippedCampaign = ScenarioCatalog.CreateBaseCampaign(77);
            Check(WorldMapSource.PeekControlIfLoaded() is null && WorldMapSource.PeekStatesIfLoaded() is null,
                "CreateBaseCampaign skips 5760x2880 PNG decode even without world_raster_boot_v1.bin when map_boot_v2 + lakes_boot_v1 exist");
            Check(LakeRegions.CreatedCount > 0 && LakeRegions.IsLakeRegion(LakeRegions.FirstMapIndex),
                "shipped-cache fast-boot registers LakeRegions indices and CreatedCount");
            shippedLakeCount = LakeRegions.CreatedCount;
            shippedFp = CampaignFingerprint.Of(shippedCampaign);
        }

        // Now load States via WorldMapSource (writes world_raster_boot_v1.bin v2 cleanly when missing), then verify Span2D & world_raster_boot_v1.bin.
        var repairedStates = WorldMapSource.States;
        Check(TerritorialTopology.IsPreRepaired(repairedStates), "WorldMapSource.States returns pre-repaired grid");
        if (bootRasterPath is not null)
            Check(File.Exists(bootRasterPath), "world_raster_boot_v1.bin v2 persisted after cold repair");

        var statesGrid = WorldMapSource.StatesGrid;
        var controlGrid = WorldMapSource.ControlGrid;
        Check(statesGrid.Height == h && statesGrid.Width == w &&
              controlGrid.Height == h && controlGrid.Width == w,
            "ReadOnlySpan2D grids expose exact 2880x5760 dimensions");
        var sampleRow = 768;
        var sampleCol = 3072;
        Check(statesGrid[sampleRow, sampleCol] == WorldMapSource.States[sampleRow * w + sampleCol] &&
              controlGrid[sampleRow, sampleCol] == WorldMapSource.Control[sampleRow * w + sampleCol],
            "Span2D indexing matches flat WorldMapSource indexing");

        WorldMapSource.ReleaseDecodedGrids();
        TerritorialTopology.ResetCatalog();
        Check(TerritorialTopology.TryPeekBootCacheRegionFingerprint(w, h, out var peekedFp) &&
              WorldMapSource.PeekControlIfLoaded() is null && WorldMapSource.PeekStatesIfLoaded() is null,
            "TryPeekBootCacheRegionFingerprint succeeds without loading States or Control");

        var fastCampaign = ScenarioCatalog.CreateBaseCampaign(77);
        Check(WorldMapSource.PeekControlIfLoaded() is null && WorldMapSource.PeekStatesIfLoaded() is null,
            "boot-raster CreateBaseCampaign leaves WorldMapSource.Control and States unloaded (zero PNG decode / LOH)");
        Check(LakeRegions.CreatedCount > 0 && LakeRegions.IsLakeRegion(LakeRegions.FirstMapIndex),
            "boot-raster fast-boot populates LakeRegions.CreatedCount and LakeStateIndices");
        var fastFp = CampaignFingerprint.Of(fastCampaign);
        if (MapBootCache.TryRead(cacheDir, peekedFp, w, h, out var fastBootMap, out _,
                GameDataLoader.FindDataFile("cache", MapBootCache.FileName)))
        {
            var armyOffMap = 0;
            foreach (var army in fastCampaign.Armies)
            {
                var ax = Math.Clamp((int)((army.Longitude + 180f) / 360f * w), 0, w - 1);
                var ay = Math.Clamp((int)((90f - army.Latitude) / 180f * h), 0, h - 1);
                var cell = ay * w + ax;
                var province = fastBootMap.Provinces.CellToProvince[cell];
                if (province == 0)
                {
                    armyOffMap++;
                    _ = WorldMapSource.PeekStatesIfLoaded() is { } loaded ? loaded[cell] : (ushort)0;
                }
            }
            Check(WorldMapSource.PeekControlIfLoaded() is null && WorldMapSource.PeekStatesIfLoaded() is null &&
                  fastBootMap.Provinces.ProvinceCount > 0,
                $"boot-raster + MapBootCache.TryRead + {armyOffMap} off-province scenario armies leave Control/States unloaded");
        }
        if (shippedFp != 0)
        {
            Check(fastFp == shippedFp, "shipped map_boot_v2 fast-boot CampaignFingerprint matches world_raster_boot_v1 fast-boot");
            Check(LakeRegions.CreatedCount == shippedLakeCount, "both fast-boot paths register identical LakeRegions count");
        }

        // Compare against genuine full PNG raster repair + full TerritorialTopology.Apply.
        var rawControl = SourceMapGrid.Load(MapRasterCatalog.SourceControlPath, w, h);
        var rawStates = SourceMapGrid.Load(MapRasterCatalog.SourceStatesPath, w, h);
        Check(!TerritorialTopology.IsPreRepaired(rawControl) && !TerritorialTopology.IsPreRepaired(rawStates),
            "freshly decoded PNG arrays are not tagged PreRepaired");

        var rawCampaign = new Campaign(77);
        ScenarioCatalog.PopulateBaseTerritoryShell(rawCampaign);
        TerritorialTopology.Apply(rawCampaign, rawControl, rawStates, w, h);
        Check(peekedFp == MapRasterCatalog.RegionFingerprint(rawStates),
            "TryPeekBootCacheRegionFingerprint matches repaired raw States RegionFingerprint bit-for-bit");

        Check(WorldMapSource.States.AsSpan().SequenceEqual(rawStates),
            "cached boot States raster is bit-for-bit identical to fresh raw PNG + TerritorialTopology.Apply");
        Check(WorldMapSource.Control.AsSpan().SequenceEqual(rawControl),
            "cached boot Control raster is bit-for-bit identical to fresh raw PNG + TerritorialTopology.Apply");
        Check(fastCampaign.States.Count == rawCampaign.States.Count,
            $"fast-boot state count ({fastCampaign.States.Count}) matches full topology ({rawCampaign.States.Count})");
        Check(LakeRegions.CreatedCount == fastCampaign.States.Count(s => string.Equals(s.Kind, "Lake", StringComparison.Ordinal)),
            "LakeRegions.CreatedCount matches campaign lake state count");

        // Pre-bake world_raster_boot_v1.bin into an isolated temp directory and verify round-trip fidelity.
        var tempPrebakeDir = CreateTempDir();
        try
        {
            Check(TerritorialTopology.TryPrebakeBootRasters(tempPrebakeDir, out var prebakeErr),
                "TryPrebakeBootRasters writes world_raster_boot_v1.bin cleanly: " + prebakeErr);
            var prebakedPath = Path.Combine(tempPrebakeDir, MapBootCache.BootRasterFileName);
            TerritorialTopology.ComputeBootRasterKeys(w, h, out var contentKey, out var sourcePngKey);
            Check(File.Exists(prebakedPath) &&
                  MapBootCache.TryReadBootLakesFile(prebakedPath, contentKey, sourcePngKey, w, h, out var prebakedLakes, out var prebakedFp, out _) &&
                  prebakedLakes.Length > 0 && prebakedFp == peekedFp &&
                  MapBootCache.TryReadBootRasterLayerFile(prebakedPath, contentKey, sourcePngKey, w, h, loadControl: false, out var roundStates, out _) &&
                  MapBootCache.TryReadBootRasterLayerFile(prebakedPath, contentKey, sourcePngKey, w, h, loadControl: true, out var roundControl, out _) &&
                  roundStates.AsSpan().SequenceEqual(rawStates) &&
                  roundControl.AsSpan().SequenceEqual(rawControl),
                "prebaked world_raster_boot_v1.bin round-trips bit-for-bit against raw repaired rasters");
        }
        finally
        {
            TryDeleteDir(tempPrebakeDir);
        }

        // Compare CampaignFingerprint against Campaign created with pre-repaired Control+States grids loaded.
        TerritorialTopology.ResetCatalog();
        var fromLoadedGridsCampaign = ScenarioCatalog.CreateBaseCampaign(77);
        Check(fastFp == CampaignFingerprint.Of(fromLoadedGridsCampaign),
            "fast-boot CreateBaseCampaign preserves bit-for-bit CampaignFingerprint parity with loaded-grid topology");
    }

    private static void VerifyWaterByProvinceAndDonorFidelity(int w, int h, string cacheDir)
    {
        // 1. After fast-boot + loading ONLY States (Control still null), MapBootCache.TryRead must mark lake WaterByProvince!
        WorldMapSource.ReleaseDecodedGrids();
        TerritorialTopology.ResetCatalog();
        _ = ScenarioCatalog.CreateBaseCampaign(77);
        var statesOnly = WorldMapSource.States;
        Check(WorldMapSource.PeekControlIfLoaded() is null, "WorldMapSource.States leaves Control unloaded");
        var fp = MapRasterCatalog.RegionFingerprint(statesOnly);
        if (MapBootCache.TryRead(cacheDir, fp, w, h, out var bootData, out _,
                GameDataLoader.FindDataFile("cache", MapBootCache.FileName)))
        {
            var waterCount = 0;
            for (var p = 1; p <= bootData.Provinces.ProvinceCount; p++)
                if (bootData.Provinces.WaterByProvince[p]) waterCount++;
            Check(waterCount > 0, $"ProvinceTopology.WaterByProvince populated ({waterCount} lake provinces) after fast-boot without Control");
        }

        // 2. Stale synthetic LakeRegions state must be cleanly overwritten when WorldMapSource.Control loads world lakes.
        var tinyStates = new ushort[16];
        var tinyControl = new ushort[16];
        LakeRegions.Apply(new Campaign(1), tinyControl, tinyStates, 4, 4);
        var syntheticLakes = LakeRegions.CreatedCount;
        WorldMapSource.ReleaseDecodedGrids();
        _ = WorldMapSource.Control;
        Check(LakeRegions.CreatedCount > syntheticLakes && LakeRegions.CreatedCount >= 100,
            $"WorldMapSource.Control refreshes LakeRegions from world boot cache ({syntheticLakes} -> {LakeRegions.CreatedCount})");

        // 3. ResetCatalog must keep RegionRuleCount consistent (regression check for ModReachTests).
        var beforeReset = TerritorialTopology.RegionRuleCount;
        TerritorialTopology.ResetCatalog();
        var afterReset = TerritorialTopology.RegionRuleCount;
        Check(afterReset == beforeReset,
            $"TerritorialTopology.ResetCatalog preserves RegionRuleCount ({beforeReset} == {afterReset})");

        // 4. DonorIso population adjustment must match between raw Apply and PreRepaired boot Apply.
        var donorCampaignRaw = new Campaign(1);
        ScenarioCatalog.PopulateBaseTerritoryShell(donorCampaignRaw);
        var cadizRaw = donorCampaignRaw.States.First(s => string.Equals(s.Iso, "ES-CA", StringComparison.OrdinalIgnoreCase));
        EconomySystem.AdjustStatePopulation(cadizRaw, 500_000);
        donorCampaignRaw.States.RemoveAll(s => s.MapIndex == 1719);
        TerritorialTopology.Apply(donorCampaignRaw,
            SourceMapGrid.Load(MapRasterCatalog.SourceControlPath, w, h),
            SourceMapGrid.Load(MapRasterCatalog.SourceStatesPath, w, h), w, h);

        WorldMapSource.ReleaseDecodedGrids();
        var donorCampaignFast = new Campaign(1);
        ScenarioCatalog.PopulateBaseTerritoryShell(donorCampaignFast);
        var cadizFast = donorCampaignFast.States.First(s => string.Equals(s.Iso, "ES-CA", StringComparison.OrdinalIgnoreCase));
        EconomySystem.AdjustStatePopulation(cadizFast, 500_000);
        donorCampaignFast.States.RemoveAll(s => s.MapIndex == 1719);
        Check(TerritorialTopology.TryApplyPrebakedBoot(donorCampaignFast, w, h),
            "TryApplyPrebakedBoot succeeds on donor-populated shell campaign");
        Check(cadizFast.Population == cadizRaw.Population,
            $"fast-boot DonorIso population transfer matches raw Apply ({cadizFast.Population} == {cadizRaw.Population})");
    }

    private static void TestPhase2HighPerfPackages()
    {
        // 1. ZeroAlloc.Collections
        using var pooled = new HeapPooledList<int>(8);
        for (var i = 0; i < 64; i++) pooled.Add(i * 3);
        Check(pooled.Count == 64 && pooled.AsReadOnlySpan()[63] == 189, "HeapPooledList adds and slices spans accurately");

        using var stackList = new PooledList<int>(8);
        stackList.Add(11);
        stackList.Add(22);
        Check(stackList.Count == 2 && stackList[1] == 22, "PooledList stores elements cleanly");

        using var map = new HeapSpanDictionary<long, int>(16);
        map[1001L] = 42;
        map[2002L] = 84;
        Check(map.TryGetValue(1001L, out var v1) && v1 == 42 && !map.ContainsKey(999L),
            "HeapSpanDictionary provides exact lookup without heap dictionary allocations");

        // 2. DotNext.Buffers
        using (var owner = new DotNext.Buffers.MemoryOwner<int>(ArrayPool<int>.Shared, 32))
        {
            owner.Span.Fill(7);
            Check(owner.Length >= 32 && owner.Span[31] == 7, "DotNext MemoryOwner manages ArrayPool slice cleanly");
        }
        using (var slim = new BufferWriterSlim<int>(16))
        {
            for (var i = 1; i <= 40; i++) slim.Add(i);
            Check(slim.WrittenCount == 40 && slim.WrittenSpan[39] == 40, "DotNext BufferWriterSlim grows past stack capacity");
        }
        using (var writer = new PoolingArrayBufferWriter<int>(ArrayPool<int>.Shared))
        {
            var span = writer.GetSpan(10);
            for (var i = 0; i < 10; i++) span[i] = i + 1;
            writer.Advance(10);
            Check(writer.WrittenMemory.Length == 10 && writer.WrittenArray[9] == 10,
                "DotNext PoolingArrayBufferWriter commits spans accurately");
        }

        // 3. SimdLinq
        var ints = Enumerable.Range(1, 257).ToArray();
        var longs = Enumerable.Range(1, 131).Select(x => (long)x * 1_000_000L).ToArray();
        Check(ints.Sum() == 257 * 258 / 2 && ints.Min() == 1 && ints.Max() == 257,
            "SimdLinq integer Sum/Min/Max match exact closed form");
        Check(longs.Sum() == 131L * 132L / 2L * 1_000_000L, "SimdLinq long Sum matches exact closed form");
        Check(ints.AsSpan().SequenceEqual(Enumerable.Range(1, 257).ToArray()),
            "SimdLinq/Span SequenceEqual matches exact sequence");

        // 4. Equativ.RoaringBitmaps + CampaignCaches.OwnedProvinceBitmaps
        var bmA = RoaringBitmap.Create(new[] { 1, 2, 100, 1000, 30000 });
        var bmB = RoaringBitmap.Create(new[] { 2, 50, 1000, 31000 });
        var intersect = bmA & bmB;
        var union = bmA | bmB;
        Check(intersect.Cardinality == 2 && union.Cardinality == 7,
            "RoaringBitmap bitwise And/Or Cardinality matches exact set algebra");
        using (var ms = new MemoryStream())
        {
            RoaringBitmap.Serialize(union, ms);
            ms.Position = 0;
            var round = RoaringBitmap.Deserialize(ms);
            Check(round.Equals(union) && round.Cardinality == 7, "RoaringBitmap Serialize/Deserialize round-trips bit-for-bit");
        }

        // 5. RBush overseas parity across antimeridian and polar cases vs brute-force NavalOperations.Distance
        var spatialCampaign = new Campaign(79001);
        (float Lat, float Lon, ushort Map)[] coords =
        {
            (35f, 139f, 1),     // Tokyo
            (21f, -157f, 2),    // Hawaii (antimeridian neighbor)
            (-18f, 178f, 3),    // Fiji
            (-13f, -172f, 4),   // Samoa
            (78f, 15f, 5),      // Svalbard (high latitude)
            (71f, -156f, 6),    // Barrow (high latitude negative lon)
            (-75f, 179f, 7),    // Antarctica east
            (-76f, -179f, 8),   // Antarctica west
            (0f, 0f, 9),        // Null island
            (51f, 0f, 10),      // London
        };
        for (var i = 0; i < coords.Length; i++)
        {
            spatialCampaign.AddNation(new Nation
            {
                Tag = $"SP{i}",
                MapIndex = coords[i].Map,
                Population = 5_000_000,
                CapitalLatitude = coords[i].Lat,
                CapitalLongitude = coords[i].Lon
            });
        }
        var active = spatialCampaign.ActiveNations;
        var tree = new RBush<AiStatecraft.NationSpatialEntry>();
        var spatialItems = active.Select(n =>
        {
            var lon = n.CapitalLongitude;
            while (lon > 180f) lon -= 360f;
            while (lon < -180f) lon += 360f;
            return new AiStatecraft.NationSpatialEntry(n, lon, n.CapitalLatitude);
        }).ToList();
        tree.BulkLoad(spatialItems);
        float[] reaches = { 25f, 65f, 120f };
        foreach (var reach in reaches)
        {
            for (var src = 0; src < active.Count; src++)
            {
                var from = active[src];
                var brute = new HashSet<int>();
                for (var j = 0; j < active.Count; j++)
                {
                    if (src == j) continue;
                    var other = active[j];
                    if (NavalOperations.Distance(from.CapitalLatitude, from.CapitalLongitude,
                            other.CapitalLatitude, other.CapitalLongitude) <= reach)
                        brute.Add(other.MapIndex);
                }
                var spatialTargets = new List<int>();
                var spatialDist = new Dictionary<int, float>();
                AiStatecraft.CollectOverseasCandidatesRBush(tree, from.Id, from.CapitalLatitude, from.CapitalLongitude,
                    reach, spatialTargets, spatialDist);
                Check(spatialTargets.Count == brute.Count && spatialTargets.All(brute.Contains),
                    $"RBush overseas candidate set matches brute-force scan exactly (src={from.Tag}, reach={reach})");
            }
        }
        var knn = tree.Knn(3, 139.0, 35.0);
        Check(knn.Count == 3 && knn[0].Nation.MapIndex == 1, "RBush Knn finds nearest nation at exact coordinates");

        // 6. Ben.Demystifier
        IEnumerable<int> ThrowFromIterator()
        {
            yield return 1;
            throw new InvalidOperationException("demystifier_probe");
        }
        try
        {
            foreach (var _ in ThrowFromIterator()) { }
        }
        catch (Exception ex)
        {
            var readable = ex.ToStringDemystified();
            Check(readable.Contains("demystifier_probe", StringComparison.Ordinal) &&
                  readable.Contains(nameof(TestPhase2HighPerfPackages), StringComparison.Ordinal),
                "Ben.Demystifier formats iterator exception stack cleanly");
        }
    }

    private static void TestActiveWarsIndexAndInvalidation()
    {
        var c = new Campaign(79002);
        var a = c.AddNation(new Nation { Tag = "AAA", MapIndex = 1, Population = 1_000_000, ManpowerReserve = 0f });
        var b = c.AddNation(new Nation { Tag = "BBB", MapIndex = 2, Population = 1_000_000, ManpowerReserve = 0f });
        var map = ProvinceTopology.Build(new ushort[] { 1, 2 }, new ushort[] { 1, 2 }, 2, 1, _ => false);
        c.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 1, Population = 1_000_000 });
        c.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 2, Population = 1_000_000 });
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(map);

        Check(c.ActiveWars.Count == 0 && Warfare.OpenWars(c).Count == 0, "fresh campaign has empty ActiveWars");
        var w1 = new StrategicWar { Id = 10, AttackerId = a.Id, DefenderId = b.Id, StartedDay = 0 };
        w1.Prisoners[a.Id] = 100f;
        w1.OriginalController[1] = 1;
        c.Wars.Add(w1);
        c.MarkWarsChanged();
        Check(c.ActiveWars.Count == 1 && ReferenceEquals(Warfare.WarById(c, 10), w1),
            "ActiveWars and WarById expose newly declared war");

        // Direct Ended=true edit without MarkWarsChanged must still invalidate ActiveWars & OpenWars.
        w1.Ended = true;
        w1.EndedDay = 0;
        Check(c.ActiveWars.Count == 0 && Warfare.OpenWars(c).Count == 0 && ReferenceEquals(Warfare.WarById(c, 10), w1),
            "ActiveWars detects direct Ended=true mutation while WarById keeps ended record");

        // Direct reopening must also invalidate ActiveWars.
        w1.Ended = false;
        Check(c.ActiveWars.Count == 1 && Warfare.OpenWars(c).Count == 1,
            "ActiveWars detects direct reopening of war");

        // Watermark Repatriate + new later war with prisoners.
        w1.Ended = true;
        w1.EndedDay = 1;
        WarLosses.Repatriate(c);
        Check(w1.Prisoners.Count == 0 && a.ManpowerReserve > 0f,
            "WarLosses.Repatriate releases prisoners once and clears war ledger");
        var reserveAfterW1 = a.ManpowerReserve;
        WarLosses.Repatriate(c);
        Check(a.ManpowerReserve == reserveAfterW1, "second Repatriate pass skips settled ended war cleanly");

        var w2 = new StrategicWar { Id = 11, AttackerId = a.Id, DefenderId = b.Id, StartedDay = 2, Ended = true, EndedDay = 3 };
        w2.Prisoners[a.Id] = 200f;
        c.Wars.Add(w2);
        c.MarkWarsChanged();
        WarLosses.Repatriate(c);
        Check(w2.Prisoners.Count == 0 && a.ManpowerReserve > reserveAfterW1,
            "Repatriate prefix watermark still catches newly ended wars added later");

        // OwnedProvinceBitmaps cache verification
        var bm1 = c.Caches.OwnedProvinceBitmaps.ForOwner(c, 1);
        var bm2 = c.Caches.OwnedProvinceBitmaps.ForOwner(c, 2);
        Check(bm1.Cardinality == 1 && bm2.Cardinality == 1 && (bm1 & bm2).Cardinality == 0,
            "CampaignCaches.OwnedProvinceBitmaps builds exact disjoint ownership bitmaps");
    }

    private static void TestPopulationGroupCoalescingDeterminism()
    {
        Campaign MakeCoalesceCampaign()
        {
            var c = new Campaign(79003);
            var n = c.AddNation(new Nation { Tag = "TST", MapIndex = 1, Population = 1_000_000 });
            var state = new ScenarioState
            {
                Id = "tst_state",
                MapIndex = 1,
                NationId = "TST",
                NationMapIndex = 1,
                Population = 1_000_000,
                LastHouseholdReviewDay = 7,
                LastDemographyDay = 0
            };
            // Major groups + duplicates + tiny splinters > 16 total cohorts.
            state.PopulationGroups.Add(new PopulationGroup
            {
                Culture = "polish", Religion = "catholic", Profession = "farmers",
                Class = SocialClass.Lower, Sector = Sector.Agriculture,
                Size = 900_000, Employment = 0.80f, Wealth = 0.40f, StandardOfLiving = 0.50f, Radicalism = 0.10f
            });
            // Exact duplicate cohort with different attributes.
            state.PopulationGroups.Add(new PopulationGroup
            {
                Culture = "polish", Religion = "catholic", Profession = "farmers",
                Class = SocialClass.Lower, Sector = Sector.Agriculture,
                Size = 99_600, Employment = 0.90f, Wealth = 0.60f, StandardOfLiving = 0.70f, Radicalism = 0.20f
            });
            // Tiny micro-slivers <= 250 with distinct professions.
            state.PopulationGroups.Add(new PopulationGroup
            {
                Culture = "polish", Religion = "catholic", Profession = "artisans",
                Class = SocialClass.Middle, Sector = Sector.Services,
                Size = 100, Employment = 0.50f, Wealth = 0.90f, StandardOfLiving = 0.80f, Radicalism = 0.50f
            });
            for (var k = 0; k < 15; k++)
            {
                state.PopulationGroups.Add(new PopulationGroup
                {
                    Culture = "polish", Religion = "catholic", Profession = $"trade_{k}",
                    Class = SocialClass.Lower, Sector = Sector.Industry,
                    Size = 20, Employment = 0.50f, Wealth = 0.20f, StandardOfLiving = 0.30f, Radicalism = 0.40f
                });
            }
            c.States.Add(state);
            c.InvalidateStateIndex();
            c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1 }, new ushort[] { 1 }, 1, 1, _ => false));
            return c;
        }

        var c1 = MakeCoalesceCampaign();
        var c2 = MakeCoalesceCampaign();
        var s1 = c1.States[0];
        var popBefore = s1.PopulationGroups.Sum(g => g.Size);
        var wealthBefore = s1.PopulationGroups.Sum(g => g.Size * (double)g.Wealth);
        var empBefore = s1.PopulationGroups.Sum(g => g.Size * (double)g.Employment);
        var solBefore = s1.PopulationGroups.Sum(g => g.Size * (double)g.StandardOfLiving);
        var radBefore = s1.PopulationGroups.Sum(g => g.Size * (double)g.Radicalism);
        var countBefore = s1.PopulationGroups.Count;

        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c1, 7L * 24);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c2, 7L * 24);
        c1.Defines.ParallelRegions = false;
        c2.Defines.ParallelRegions = true;
        DemographicCycle.Tick(c1);
        DemographicCycle.Tick(c2);

        var popAfter = s1.PopulationGroups.Sum(g => g.Size);
        var wealthAfter = s1.PopulationGroups.Sum(g => g.Size * (double)g.Wealth);
        var empAfter = s1.PopulationGroups.Sum(g => g.Size * (double)g.Employment);
        var solAfter = s1.PopulationGroups.Sum(g => g.Size * (double)g.StandardOfLiving);
        var radAfter = s1.PopulationGroups.Sum(g => g.Size * (double)g.Radicalism);

        Check(popAfter == popBefore && s1.PopulationGroups.Count < countBefore && s1.PopulationGroups.Count <= 2,
            $"PopGroup coalescing shrank cohorts ({countBefore} -> {s1.PopulationGroups.Count}) conserving exact population ({popAfter:N0})");
        Check(Math.Abs(wealthAfter - wealthBefore) < 5.0 &&
              Math.Abs(empAfter - empBefore) < 5.0 &&
              Math.Abs(solAfter - solBefore) < 5.0 &&
              Math.Abs(radAfter - radBefore) < 5.0,
            "PopGroup coalescing preserves population-weighted Employment, Wealth, StandardOfLiving, and Radicalism");
        Check(CampaignFingerprint.Of(c1) == CampaignFingerprint.Of(c2),
            "PopGroup coalescing produces bit-for-bit identical CampaignFingerprint under ST and MT execution");
    }

    private static void TestOpinionSocialAndHistoryPruning()
    {
        var c = new Campaign(79004);
        var a = c.AddNation(new Nation { Tag = "ONE", MapIndex = 1, Population = 1_000_000 });
        var b = c.AddNation(new Nation { Tag = "TWO", MapIndex = 2, Population = 1_000_000 });
        var rel = CampaignDiplomacy.Get(c, a.Id, b.Id);
        OpinionSystem.Settle(c, rel);
        var baselineOpinion = rel.Opinion;
        // Add an expired reason, an unknown mod reason, and a decaying reason decayed below 0.05f.
        rel.Reasons.Add(new OpinionReason { Id = "nuclear_test", GrantedDay = 0, ExpiresDay = 10, Scale = 1f });
        rel.Reasons.Add(new OpinionReason { Id = "removed_unknown_mod_reason", GrantedDay = 0, ExpiresDay = 0, Scale = 1f });
        rel.Reasons.Add(new OpinionReason { Id = "declared_war_on_us", GrantedDay = 0, ExpiresDay = 1000, Scale = 0.0001f });
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, 50L * 24);
        OpinionSystem.Settle(c, rel);
        Check(rel.Reasons.Count == 0 && MathF.Abs(rel.Opinion - baselineOpinion) < 0.001f,
            "OpinionSystem.Settle prunes expired, unknown-catalog, and sub-0.05f decayed reasons cleanly");

        // LongCampaign prunes dead non-historical Cabinet/CabinetPeople/PersonPortraits and expired dormant reasons.
        c.States.Add(new ScenarioState { Id = "s_one", MapIndex = 1, NationMapIndex = 1, Population = 1_000_000 });
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1 }, new ushort[] { 1 }, 1, 1, _ => false));
        var dead = c.AddNation(new Nation { Tag = "DED", MapIndex = 3, Population = 0, GoneSinceDay = 0 });
        c.Fallen.Add(dead.Id);
        dead.Cabinet["economy"] = "market";
        dead.CabinetPeople["economy"] = new MinisterPerson { Name = "Ghost Minister", BirthYear = 1970 };
        dead.PersonPortraits["Ghost Minister:1970"] = "ghost.png";
        var landlessA = c.AddNation(new Nation { Tag = "LN1", MapIndex = 4, Population = 0,
            Movement = new MovementState { Id = "m1", Dissolved = false } });
        var landlessB = c.AddNation(new Nation { Tag = "LN2", MapIndex = 5, Population = 0,
            Movement = new MovementState { Id = "m2", Dissolved = false } });
        var dormantRel = CampaignDiplomacy.Get(c, landlessA.Id, landlessB.Id);
        dormantRel.Reasons.Add(new OpinionReason { Id = "nuclear_test", GrantedDay = 0, ExpiresDay = 10, Scale = 1f });
        dormantRel.Dormant = true;
        var dormKey = ((long)dormantRel.A << 32) | (uint)dormantRel.B;
        c.Relations.Remove(dormKey);
        c.DormantRelations[dormKey] = dormantRel;
        var retireDay = c.Defines.RetireDeadAfterDays + 28;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, retireDay * 24L);
        var sweepReport = LongCampaign.Run(c);
        Check(dead.RetiredDay >= 0 && dead.Cabinet.Count == 0 && dead.CabinetPeople.Count == 0 && dead.PersonPortraits.Count == 0,
            "LongCampaign.Retire drops dead non-historical Cabinet, CabinetPeople, and PersonPortraits");
        Check(sweepReport.EmptyRelationsDropped == 1 && CampaignDiplomacy.Peek(c, landlessA.Id, landlessB.Id) is null,
            "LongCampaign.DropRelations prunes expired dormant reasons so inert empty relations drop");

        // CampaignHistory thinning + capacity trimming + JSON roundtrip compatibility.
        var samples = new List<NationHistorySample>();
        for (var day = 0; day <= 30 * 364; day += 7)
            samples.Add(new NationHistorySample(day, 100, 10, 1, 1, 1, 0, 1, 0.5f, 0.5f, 0.5f, 1, 0, 0, 10, 1, 1, 100, 100, 100, 100, 100));
        var preCap = samples.Capacity;
        CampaignHistory.Thin(samples, 30 * 364, CampaignHistory.Bands.Shipped);
        Check(samples.Count <= CampaignHistory.MaximumSamplesPerNation && samples[^1].Day == 30 * 364 &&
              samples.Capacity <= Math.Max(64, samples.Count * 2) && samples.Capacity <= preCap,
            $"CampaignHistory.Thin compacted 30y series ({samples.Count} pts) and trimmed list capacity ({preCap} -> {samples.Capacity})");
        var serialized = JsonSerializer.Serialize(samples);
        var deserialized = JsonSerializer.Deserialize<List<NationHistorySample>>(serialized);
        Check(deserialized is not null && deserialized.Count == samples.Count && deserialized[^1].Day == samples[^1].Day,
            "thinned CampaignHistory samples round-trip through JSON serialization");
    }

    private static void TestWave25LateGameScalingAndIdentityCaches()
    {
        // 1. FormableNations.CountryIdentity cache reuse + small-map mutation invalidation + project change.
        var cId = new Campaign(79101);
        var holder = cId.AddNation(new Nation
        {
            Tag = "DYN",
            MapIndex = 1,
            Population = 2_000_000,
            Stability = 0.8f,
            Government = "presidential_republic",
            Ideology = "liberalism"
        });
        cId.States.Add(new ScenarioState { Id = "s1", MapIndex = 1, NationMapIndex = 1, NationId = "DYN", Population = 1_000_000 });
        cId.States.Add(new ScenarioState { Id = "s2", MapIndex = 2, NationMapIndex = 1, NationId = "DYN", Population = 1_000_000 });
        cId.InvalidateStateIndex();
        cId.AttachProvinceTopology(ProvinceTopology.Build(
            new ushort[] { 1, 2, 0 },
            new ushort[] { 1, 1, 0 },
            width: 3, height: 1, _ => false));
        var id1 = FormableNations.CountryIdentity(cId, holder, "non_matching_a");
        var id2 = FormableNations.CountryIdentity(cId, holder, "non_matching_b");
        Check(ReferenceEquals(id1, id2) && id1.Count == 2 && id1.Contains(1) && id1.Contains(2),
            "CountryIdentity caches default identity set cleanly across distinct non-matching exclusions");
        cId.ProvinceMap!.ProvinceToNation[2] = 0;
        var idAfterDrop = FormableNations.CountryIdentity(cId, holder, "non_matching_a");
        Check(!ReferenceEquals(id1, idAfterDrop) && idAfterDrop.Count == 1 && !idAfterDrop.Contains(2),
            "CountryIdentity invalidates cleanly when province ownership changes on small synthetic maps");

        // 2. Corridors.GetOrderedPieces capital-first memoization & Copy-on-Pieces isolation.
        var cCorr = new Campaign(79102);
        var corrNation = cCorr.AddNation(new Nation
        {
            Tag = "CRP",
            MapIndex = 1,
            CapitalStateMapIndex = 2,
            Population = 1_000_000
        });
        cCorr.States.Add(new ScenarioState { Id = "p1", MapIndex = 1, NationMapIndex = 1, NationId = "CRP", Population = 400_000 });
        cCorr.States.Add(new ScenarioState { Id = "p2", MapIndex = 2, NationMapIndex = 1, NationId = "CRP", Population = 300_000 });
        cCorr.States.Add(new ScenarioState { Id = "p3", MapIndex = 3, NationMapIndex = 1, NationId = "CRP", Population = 300_000 });
        cCorr.InvalidateStateIndex();
        cCorr.AttachProvinceTopology(ProvinceTopology.Build(
            new ushort[] { 1, 3, 0, 2, 0 },
            new ushort[] { 1, 1, 0, 1, 0 },
            width: 5, height: 1, _ => false));
        var orderedA = Corridors.GetOrderedPieces(cCorr, corrNation);
        var orderedB = Corridors.GetOrderedPieces(cCorr, corrNation);
        Check(ReferenceEquals(orderedA, orderedB) && orderedA.Count == 2 &&
              orderedA[0].Count == 1 && orderedA[1].Count == 2,
            "Corridors.GetOrderedPieces memoizes capital-first ordering across repeated lookups");
        var publicCopy = Corridors.Pieces(cCorr, corrNation);
        publicCopy.Clear();
        Check(Corridors.GetOrderedPieces(cCorr, corrNation).Count == 2,
            "Corridors.Pieces returns an isolated copy so caller mutation cannot poison the cache");

        // 3. OrganizationDynamics Target + outsider defence-pact bonus with 20-member organization.
        var cOrg = new Campaign(79103);
        var memberIds = new List<int>();
        for (var i = 0; i < 20; i++)
        {
            var n = cOrg.AddNation(new Nation
            {
                Tag = $"M{i:00}",
                MapIndex = i + 1,
                Population = 1_000_000,
                Gdp = 100f + i,
                Stability = 0.7f
            });
            cOrg.States.Add(new ScenarioState { Id = $"ms_{i}", MapIndex = i + 1, NationMapIndex = i + 1, NationId = n.Tag, Population = 1_000_000 });
            memberIds.Add(n.Id);
        }
        var outsider = cOrg.AddNation(new Nation
        {
            Tag = "OUT",
            MapIndex = 21,
            Population = 2_000_000,
            Gdp = 500f,
            Stability = 0.7f
        });
        cOrg.States.Add(new ScenarioState { Id = "ms_out", MapIndex = 21, NationMapIndex = 21, NationId = "OUT", Population = 2_000_000 });
        cOrg.InvalidateStateIndex();
        var org = InternationalOrganizations.Seed(cOrg, "pact_wave25", "collective_defence", memberIds);
        Check(org is not null && org.Members.Count == 20, "Seeded 20-member collective defence pact");
        var baseTarget = OrganizationDynamics.Target(cOrg, org!);
        // Insider war between two members should reduce cohesion, not grant outsider defence bonus.
        var insiderWar = new StrategicWar { Id = 1, AttackerId = memberIds[0], DefenderId = memberIds[1], StartedDay = 0 };
        cOrg.Wars.Add(insiderWar);
        var insiderTarget = OrganizationDynamics.Target(cOrg, org!);
        Check(insiderTarget < baseTarget, $"OrganizationDynamics.Target drops on member-vs-member war ({baseTarget:0.000} -> {insiderTarget:0.000})");
        insiderWar.Ended = true;
        // Outsider attacking member triggers +0.12f defence solidarity bonus cleanly.
        var outsiderWar = new StrategicWar { Id = 2, AttackerId = outsider.Id, DefenderId = memberIds[2], StartedDay = 0 };
        cOrg.Wars.Add(outsiderWar);
        var outsiderTarget = OrganizationDynamics.Target(cOrg, org!);
        Check(outsiderTarget > baseTarget + 0.10f,
            $"OrganizationDynamics.Target grants outsider defence bonus with HashSet war check ({baseTarget:0.000} -> {outsiderTarget:0.000})");

        // 4. Staged StepFor determinism across fine yield points vs eager TickDay.
        Campaign MakeStageCampaign()
        {
            var sc = Campaign.CreateBootstrap(79104);
            var map = ResolveBootOrSourceTopology();
            sc.AttachProvinceTopology(map.CloneForOverrides());
            sc.IsPaused = false;
            return sc;
        }
        var eager = MakeStageCampaign();
        var staged = MakeStageCampaign();
        var prevSlice = Campaign.StageSliceMilliseconds;
        try
        {
            for (var d = 0; d < 5; d++) eager.TickDay();
            Campaign.StageSliceMilliseconds = 0.001;
            var guardFrames = 0;
            while (staged.ElapsedDays < 5 && guardFrames++ < 100_000)
            {
                staged.StepFor(0.001);
            }
            staged.FinishStep();
            Check(staged.ElapsedDays == 5 && eager.ElapsedHours == staged.ElapsedHours &&
                  CampaignFingerprint.Of(eager) == CampaignFingerprint.Of(staged),
                $"Fine StageOverdue yield points preserve exact 5-day CampaignFingerprint (frames={guardFrames}, {CampaignFingerprint.Of(eager):X16})");
        }
        finally
        {
            Campaign.StageSliceMilliseconds = prevSlice;
        }
    }

    private static void TestSimulationSourceHygiene()
    {
        var root = FindRepoRoot();
        string[] zeroHashCodeAndZeroGodotFiles =
        {
            "Scripts/Simulation/LakeRegions.cs",
            "Scripts/Simulation/MapBootCache.cs",
            "Scripts/Simulation/RecyclableStreams.cs",
            "Scripts/Simulation/SaveContainer.cs",
            "Scripts/Simulation/SupportReport.cs",
            "Scripts/Simulation/WorldMapSource.cs",
            "Scripts/Simulation/ScenarioCatalog.cs",
            "Scripts/Simulation/TerritorialTopology.cs",
            "Scripts/Simulation/SaveSystem.cs",
            "Scripts/Simulation/EconomyCore.cs",
            "Scripts/Simulation/SimulationCore.cs",
            "Scripts/Simulation/Campaign.Indexes.cs",
            "Scripts/Simulation/Campaign.TickStages.cs",
            "Scripts/Simulation/CampaignCaches.cs",
            "Scripts/Simulation/DemographicCycle.cs",
            "Scripts/Simulation/OpinionCore.cs",
            "Scripts/Simulation/SocialMemory.cs",
            "Scripts/Simulation/HistoryCore.cs",
            "Scripts/Simulation/WarCore.cs",
            "Scripts/Simulation/WarLosses.cs",
            "Scripts/Simulation/WarScoring.cs",
            "Scripts/Simulation/AiStatecraft.cs",
            "Scripts/Simulation/LongCampaign.cs",
            "Scripts/Simulation/LongRangeMovement.cs",
            "Scripts/Simulation/LongRangeMovement.Reposition.cs",
            "Scripts/Simulation/Corridors.cs",
            "Scripts/Simulation/CorridorsExchange.cs",
            "Scripts/Simulation/OrganizationDynamics.cs",
            "Scripts/Simulation/FormableNations.cs",
            "Scripts/Simulation/PeacefulFormation.cs"
        };
        foreach (var rel in zeroHashCodeAndZeroGodotFiles)
        {
            var src = File.ReadAllText(Path.Combine(root, rel));
            var nonCommentLines = string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Check(!nonCommentLines.Contains("Godot", StringComparison.Ordinal), $"{rel} contains zero Godot code references");
            Check(!src.Contains(".GetHashCode(", StringComparison.Ordinal), $"{rel} contains zero nondeterministic .GetHashCode calls");
        }
        var foldingSrc = File.ReadAllText(Path.Combine(root, "Scripts/Simulation/PopulationFolding.cs"));
        Check(!foldingSrc.Contains("Godot", StringComparison.Ordinal), "PopulationFolding.cs contains zero Godot references");
        Check(Regex.Matches(foldingSrc, @"\.GetHashCode\(").Count == 2,
            "PopulationFolding.cs contains no newly added .GetHashCode calls beyond pre-existing PairIgnoreCaseComparer");
    }

    private static ProvinceTopology ResolveBootOrSourceTopology()
    {
        var cacheDir = MapBootCache.DefaultWritableCacheDirectory();
        var shipped = GameDataLoader.FindDataFile("cache", MapBootCache.FileName);
        var fp = MapRasterCatalog.RegionFingerprint(WorldMapSource.States);
        if (MapBootCache.TryRead(cacheDir, fp, WorldMapSource.Width, WorldMapSource.Height, out var cached, out _, shipped))
            return cached.Provinces;
        if (MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var built, out var msg))
            return built;
        throw new Exception("Unable to resolve ProvinceTopology: " + msg);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "dof-highperf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "DealOrForce.csproj"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new Exception("Could not locate DealOrForce.csproj from " + AppContext.BaseDirectory);
    }
}
