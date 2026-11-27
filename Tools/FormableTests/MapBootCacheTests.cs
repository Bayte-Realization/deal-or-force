using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce;
using DealOrForce.Simulation;

/// <summary>
/// Headless invariants for Wave 5G binary map boot cache round-trip, invalidation,
/// shipped fallback, ModOverlayPolicy bypass, ReducedMap fast-load, and Android Mono GCSettings safety.
/// Run: FormableTests --map-boot-cache-only
/// </summary>
static class MapBootCacheTests
{
    private static int _passed;

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("map-boot-cache: " + label);
        _passed++;
        Console.WriteLine("PASS map-boot-cache: " + label);
    }

    [TestSuite("--map-boot-cache-only", Order = 2690, IncludeInDefaultRun = true,
        Description = "Binary map boot cache round-trip, invalidation, and ReducedMap fast-load invariants.")]
    public static void Run()
    {
        _passed = 0;
        ModOverlayPolicy.Reset();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"dof_map_boot_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            TestBinaryRoundTripAndReducedMode(tempRoot);
            TestShippedFallbackAndCorruptionSafety(tempRoot);
            TestModOverlayBypass(tempRoot);
            TestAndroidGcSettingsAndFastLoadSourceContracts();
            TestWave6DTerrainRelief3DContracts();
            TestWave6FPcFreezeVramAndGovernorContracts();
            TestHydrologyBootCacheAndWaveM1Contracts(tempRoot);
            TestPoliticalRasterBootCacheAndWaveM3AContracts(tempRoot);
        }
        finally
        {
            ModOverlayPolicy.Reset();
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
        Console.WriteLine($"map-boot-cache: {_passed} PASS");
    }

    private static (ProvinceTopology Topology, ushort[] States, ulong Fingerprint, MapBootCacheData Bundle)
        BuildSyntheticFixture()
    {
        const int w = 48;
        const int h = 24;
        var states = new ushort[w * h];
        var control = new ushort[w * h];
        for (var y = 2; y < h - 2; y++)
        for (var x = 2; x < w - 2; x++)
        {
            var idx = y * w + x;
            var east = x >= w / 2;
            var south = y >= h / 2;
            states[idx] = (ushort)((east ? 2 : 1) + (south ? 2 : 0));
            control[idx] = (ushort)(east ? 20 : 10);
        }

        var topo = ProvinceTopology.Build(states, control, w, h, _ => false);

        // Attach synthetic river along vertical midline + lake pocket so hydrology & crossings are non-empty.
        var hydroPixels = new byte[w * h * 2];
        for (var y = 3; y < h - 3; y++)
        {
            var riverCell = y * w + (w / 2);
            hydroPixels[riverCell * 2 + 1] = 200;
        }
        hydroPixels[(5 * w + 5) * 2] = 180; // lake
        topo.AttachHydrology(new HydrologyRaster(w, h, hydroPixels, RiverCount: 1, LakeCount: 1, Source: "synthetic"));

        // Attach synthetic 48x24 terrain map (never read disk 5760x2880 terrain.png against synthetic 48x24 topology).
        var terrains = TerrainCatalog.All;
        var colA = terrains.Count > 0 ? terrains[0].Color : 0x80A060;
        var colB = terrains.Count > 1 ? terrains[1].Color : colA;
        var terrainRgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = x < w / 2 ? colA : colB;
            var off = (y * w + x) * 3;
            terrainRgb[off] = (byte)((c >> 16) & 0xFF);
            terrainRgb[off + 1] = (byte)((c >> 8) & 0xFF);
            terrainRgb[off + 2] = (byte)(c & 0xFF);
        }
        topo.AttachTerrain(new RgbDataMap(w, h, terrainRgb));
        var surfaceSlots = new byte[w * h];
        for (var i = 0; i < surfaceSlots.Length; i++) surfaceSlots[i] = (byte)(i % 5);
        topo.TerrainSurfaceSlots = surfaceSlots;

        var fp = MapRasterCatalog.RegionFingerprint(states);
        var bundle = new MapBootCacheData
        {
            Provinces = topo,
            ProvinceSource = "synthetic_provinces.png",
            HydrologySource = "synthetic_hydro",
            CountryAnchors = new[]
            {
                new CachedLabelAnchor(10, 12.5f, 11.5f, 1f, 0f, 8f, 4f, 400),
                new CachedLabelAnchor(20, 35.5f, 11.5f, 0.9f, 0.4f, 9f, 4.5f, 420)
            },
            RegionAnchors = new[]
            {
                new CachedLabelAnchor(1, 10f, 7f, 1f, 0f, 5f, 3f, 200),
                new CachedLabelAnchor(4, 34f, 17f, 1f, 0f, 5f, 3f, 210)
            },
            RiverAnchors = new[]
            {
                new CachedRiverAnchor("synthetic_midline", "Midline River", 1, 0f, 0f, 0f, 1f, 18.5f)
            },
            HasTerrainSurfaceSlots = true
        };
        return (topo, states, fp, bundle);
    }

    private static void TestBinaryRoundTripAndReducedMode(string tempRoot)
    {
        var (orig, _, fp, bundle) = BuildSyntheticFixture();
        var fullDir = Path.Combine(tempRoot, "full");
        var okWrite = MapBootCache.TryWrite(fullDir, fp, bundle, out var writeErr, includeTerrainSurfaceSlots: true);
        Check(okWrite && string.IsNullOrEmpty(writeErr), $"write full cache ({writeErr})");

        var okRead = MapBootCache.TryRead(fullDir, fp, orig.Width, orig.Height, out var restored, out var reason, attachCrossings: false);
        Check(okRead && restored?.Provinces is not null, $"read full cache ({reason})");

        var p = restored!.Provinces;
        Check(p.Width == orig.Width && p.Height == orig.Height, "dimensions preserved");
        Check(p.ProvinceCount == orig.ProvinceCount && p.EdgeCount == orig.EdgeCount, "province and edge counts preserved");
        Check(p.CellToProvince.SequenceEqual(orig.CellToProvince), "RLE CellToProvince bit-identical");
        Check(p.ProvinceToState.SequenceEqual(orig.ProvinceToState), "ProvinceToState bit-identical");
        Check(p.ProvinceToNation.SequenceEqual(orig.ProvinceToNation), "ProvinceToNation bit-identical");
        Check(p.ProvinceCenterCell.SequenceEqual(orig.ProvinceCenterCell), "ProvinceCenterCell bit-identical");
        Check(p.RiverIntensityByProvince.SequenceEqual(orig.RiverIntensityByProvince), "RiverIntensityByProvince bit-identical");
        Check(p.LakeAccessByProvince.SequenceEqual(orig.LakeAccessByProvince), "LakeAccessByProvince bit-identical");
        Check(p.RiverCrossingCount == orig.RiverCrossingCount && p.RiverCrossingCount > 0, "RiverCrossingCount preserved");

        var neighborsMatch = true;
        var crossingsMatch = true;
        for (var i = 1; i <= orig.ProvinceCount; i++)
        {
            if (!p.LandNeighbors[i].SequenceEqual(orig.LandNeighbors[i])) { neighborsMatch = false; break; }
            foreach (var n in orig.LandNeighbors[i])
            {
                if (p.IsRiverCrossing(i, n) != orig.IsRiverCrossing(i, n) ||
                    MathF.Abs(p.RiverCrossingStrength(i, n) - orig.RiverCrossingStrength(i, n)) > 1e-5f)
                {
                    crossingsMatch = false;
                    break;
                }
            }
        }
        Check(neighborsMatch, "LandNeighbors graph bit-identical");
        Check(crossingsMatch, "RiverCrossing edge predicates and strengths bit-identical");

        var terrainMatch = true;
        for (var i = 1; i <= orig.ProvinceCount; i++)
            if (p.TerrainAt(i).Id != orig.TerrainAt(i).Id) { terrainMatch = false; break; }
        Check(terrainMatch, "TerrainByProvince preserved");
        Check(restored.HasTerrainSurfaceSlots && p.TerrainSurfaceSlots.SequenceEqual(orig.TerrainSurfaceSlots),
            "RLE TerrainSurfaceSlots bit-identical");
        Check(restored.CountryAnchors.SequenceEqual(bundle.CountryAnchors), "CountryAnchors preserved");
        Check(restored.RegionAnchors.SequenceEqual(bundle.RegionAnchors), "RegionAnchors preserved");
        Check(restored.RiverAnchors.SequenceEqual(bundle.RiverAnchors), "RiverAnchors preserved");
        Check(restored.ProvinceSource == bundle.ProvinceSource && restored.HydrologySource == bundle.HydrologySource,
            "source strings preserved");

        // ReducedMap fast-load mode: write without TerrainSurfaceSlots and confirm clean round-trip.
        var reducedDir = Path.Combine(tempRoot, "reduced");
        Check(MapBootCache.TryWrite(reducedDir, fp, bundle, out _, includeTerrainSurfaceSlots: false),
            "write ReducedMap cache without TerrainSurfaceSlots");
        Check(MapBootCache.TryRead(reducedDir, fp, orig.Width, orig.Height, out var reducedData, out _, attachCrossings: false),
            "read ReducedMap cache");
        Check(!reducedData!.HasTerrainSurfaceSlots && reducedData.Provinces.TerrainSurfaceSlots.Length == 0,
            "ReducedMap cache omits TerrainSurfaceSlots");
    }

    private static void TestShippedFallbackAndCorruptionSafety(string tempRoot)
    {
        var (orig, _, fp, bundle) = BuildSyntheticFixture();
        var shippedDir = Path.Combine(tempRoot, "shipped");
        var emptyWritableDir = Path.Combine(tempRoot, "writable_empty");
        Directory.CreateDirectory(emptyWritableDir);

        Check(MapBootCache.TryWrite(shippedDir, fp, bundle, out _), "write shipped fallback cache");
        var shippedPath = Path.Combine(shippedDir, MapBootCache.FileName);

        // 1. Empty writable dir falls back to shipped cache (Android first-boot pre-baked path).
        Check(MapBootCache.ResolveReadableCachePath(emptyWritableDir, shippedPath) == shippedPath,
            "ResolveReadableCachePath falls back to shipped APK cache");
        Check(MapBootCache.TryRead(emptyWritableDir, fp, orig.Width, orig.Height, out var fallbackData, out _,
                shippedFallbackPath: shippedPath, attachCrossings: false) && fallbackData?.Provinces is not null,
            "TryRead succeeds via shipped fallback");

        // 2. Wrong fingerprint invalidates cleanly.
        Check(!MapBootCache.TryRead(shippedDir, fp ^ 0x1234UL, orig.Width, orig.Height, out _, out var fpReason, attachCrossings: false),
            $"wrong fingerprint rejected ({fpReason})");

        // 3. Wrong dimensions invalidate cleanly.
        Check(!MapBootCache.TryRead(shippedDir, fp, orig.Width + 1, orig.Height, out _, out var dimReason, attachCrossings: false),
            $"wrong dimensions rejected ({dimReason})");

        // 4. Single-bit payload corruption fails FNV-1a checksum and falls back cleanly without throwing.
        var corruptDir = Path.Combine(tempRoot, "corrupt");
        Directory.CreateDirectory(corruptDir);
        var corruptPath = Path.Combine(corruptDir, MapBootCache.FileName);
        var bytes = File.ReadAllBytes(shippedPath);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(corruptPath, bytes);
        Check(!MapBootCache.TryReadFile(corruptPath, fp, orig.Width, orig.Height, out _, out var crcReason, attachCrossings: false),
            $"single-bit payload flip rejected by checksum ({crcReason})");

        // 5. Corrupt writable cache gracefully falls through to valid shipped cache.
        Check(MapBootCache.TryRead(corruptDir, fp, orig.Width, orig.Height, out var rescued, out _,
                shippedFallbackPath: shippedPath, attachCrossings: false) && rescued?.Provinces is not null,
            "corrupt writable cache falls back cleanly to valid shipped cache");

        // 6. Truncated header fails cleanly.
        var truncPath = Path.Combine(tempRoot, "trunc.bin");
        File.WriteAllBytes(truncPath, new byte[12]);
        Check(!MapBootCache.TryReadFile(truncPath, fp, orig.Width, orig.Height, out _, out _, attachCrossings: false),
            "truncated cache file rejected cleanly");

        // 7. Wave 6L cache version bump (v1 -> v2): FileName is map_boot_v2.bin and stale v1 header is rejected.
        Check(MapBootCache.FileName == "map_boot_v2.bin" && MapBootCache.FormatVersion == 2,
            "MapBootCache bumped to v2 (map_boot_v2.bin)");
        var staleV1Path = Path.Combine(tempRoot, "stale_v1.bin");
        var v1Bytes = File.ReadAllBytes(shippedPath);
        BitConverter.GetBytes(1).CopyTo(v1Bytes, 4); // overwrite FormatVersion=2 with stale 1
        File.WriteAllBytes(staleV1Path, v1Bytes);
        Check(!MapBootCache.TryReadFile(staleV1Path, fp, orig.Width, orig.Height, out _, out var v1Reason, attachCrossings: false),
            $"stale v1 cache header rejected cleanly ({v1Reason})");
    }

    private static void TestModOverlayBypass(string tempRoot)
    {
        var (orig, _, fp, bundle) = BuildSyntheticFixture();
        var dir = Path.Combine(tempRoot, "mod_bypass");
        Check(MapBootCache.TryWrite(dir, fp, bundle, out _), "baseline write before mod overlay");

        ModOverlayPolicy.Configure(new[]
        {
            new ModLayerSpec("test_tc_mod", TotalConversion: true,
                ReplacePaths: Array.Empty<string>(), ExcludePaths: Array.Empty<string>())
        });
        try
        {
            Check(ModOverlayPolicy.IsActive, "ModOverlayPolicy active");
            Check(MapBootCache.ResolveReadableCachePath(dir) is null, "ResolveReadableCachePath bypassed when mod active");
            Check(MapBootCache.ResolveWritableCachePath(dir) is null, "ResolveWritableCachePath bypassed when mod active");
            Check(!MapBootCache.TryRead(dir, fp, orig.Width, orig.Height, out _, out _, attachCrossings: false),
                "TryRead bypassed when mod overlay active");
            Check(!MapBootCache.TryWrite(dir, fp, bundle, out _),
                "TryWrite bypassed when mod overlay active");
        }
        finally
        {
            ModOverlayPolicy.Reset();
        }

        Check(MapBootCache.TryRead(dir, fp, orig.Width, orig.Height, out _, out _, attachCrossings: false),
            "TryRead resumes cleanly after ModOverlayPolicy.Reset()");
    }

    private static void TestAndroidGcSettingsAndFastLoadSourceContracts()
    {
        var root = FindRepoRoot();
        var scriptsDir = Path.Combine(root, "Scripts");
        Check(Directory.Exists(scriptsDir), "located Scripts/ directory");

        // 1. Static audit: NO unguarded GCSettings.LatencyMode or GCSettings.LargeObjectHeapCompactionMode anywhere in Scripts/.
        var gcHits = 0;
        var unguarded = new List<string>();
        foreach (var file in Directory.EnumerateFiles(scriptsDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("GCSettings.LatencyMode", StringComparison.Ordinal) &&
                !text.Contains("GCSettings.LargeObjectHeapCompactionMode", StringComparison.Ordinal))
                continue;
            gcHits++;
            var hasOsGuard = text.Contains("OperatingSystem.IsAndroid()", StringComparison.Ordinal) &&
                             text.Contains("OperatingSystem.IsIOS()", StringComparison.Ordinal);
            var hasCatch = text.Contains("PlatformNotSupportedException", StringComparison.Ordinal);
            if (!hasOsGuard || !hasCatch)
                unguarded.Add(Path.GetRelativePath(root, file));
        }
        Check(gcHits > 0 && unguarded.Count == 0,
            $"all {gcHits} GCSettings sites guarded for Android/iOS + PlatformNotSupportedException");

        // 2. ReducedMap & mobile fast-load contracts in GlobePresentation, GlobeTerrain, DealOrForceGame.
        var globeSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobePresentation.cs"));
        Check(globeSrc.Contains("if (!reducedMap)", StringComparison.Ordinal),
            "GlobePresentation skips HydrologySurfaceData on ReducedMap");
        Check(globeSrc.Contains("Parallel.For(0, politicalHeight", StringComparison.Ordinal),
            "GlobePresentation parallelizes 16.6M pixel loop in PoliticalScale-aligned strips");
        Check(!globeSrc.Contains("seaWork.Wait()", StringComparison.Ordinal),
            "GlobePresentation does not block map load on seaWork.Wait()");

        var terrainSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobeTerrain.cs"));
        Check(terrainSrc.Contains("isTouchPrimary ? 128 : 2048", StringComparison.Ordinal) &&
              terrainSrc.Contains("Image.Interpolation.Bilinear", StringComparison.Ordinal) &&
              terrainSrc.Contains("TerrainCatalog.SeamlessTiles && !isTouchPrimary", StringComparison.Ordinal),
            "GlobeTerrain caps mobile tile resolution to 128 Bilinear and skips CPU SeamlessTile on touch/mobile");

        var gameSrc = File.ReadAllText(Path.Combine(scriptsDir, "DealOrForceGame.cs"));
        Check(gameSrc.Contains("IsTouchPrimary", StringComparison.Ordinal) &&
              gameSrc.Contains("ApplyDesktopCustomCursors", StringComparison.Ordinal),
            "DealOrForceGame skips hardware cursor decode on touch/mobile");
        Check(gameSrc.Contains("_saveWarmupTask", StringComparison.Ordinal) &&
              gameSrc.Contains("StartCampaignAsync", StringComparison.Ordinal) &&
              gameSrc.Contains("Task.WhenAll", StringComparison.Ordinal),
            "DealOrForceGame parallelizes catalogs, backgrounds SaveSystem warmup and Campaign.CreateBootstrap");
    }

    private static void TestWave6DTerrainRelief3DContracts()
    {
        var root = FindRepoRoot();
        var shaderPath = Path.Combine(root, "game", "shaders", "globe_map.gdshader");
        var shaderSrc = File.ReadAllText(shaderPath);

        Check(shaderSrc.Contains("uniform float relief_displacement", StringComparison.Ordinal) &&
              shaderSrc.Contains("uniform float relief_ocean_depth", StringComparison.Ordinal),
            "globe_map.gdshader declares relief_displacement and relief_ocean_depth uniforms");
        Check(shaderSrc.Contains("void vertex()", StringComparison.Ordinal) &&
              shaderSrc.Contains("textureLod(relief_height,", StringComparison.Ordinal) &&
              shaderSrc.Contains("smoothstep(0.92, 0.25, paper_mix)", StringComparison.Ordinal) &&
              shaderSrc.Contains("smoothstep(0.495, 0.435, abs(uv.y - 0.5))", StringComparison.Ordinal),
            "globe_map.gdshader vertex() uses mip-smoothed relief_height with pole and paper_mix attenuation");

        // Packed GLES3 compatibility + color-space transform must preserve exact 12-sampler budget.
        var packed = PackedMapShader.Build(MapColorSpaceShader.Build(shaderSrc));
        var packedSamplers = Regex.Matches(packed, @"uniform\s+sampler\w+\s+").Count;
        Check(packedSamplers <= PackedMapShader.TextureUnits && packed.Contains("void vertex()", StringComparison.Ordinal),
            $"PackedMapShader preserves vertex() within GLES3 sampler budget ({packedSamplers}/{PackedMapShader.TextureUnits})");

        // Settings JSON + live runtime toggle in SettingsApply.cs + Paper/ReducedMap/MobileLow guard + FSR 1.0/2.2 controls.
        var settingsJson = File.ReadAllText(Path.Combine(root, "game", "common", "settings", "12_map_style.json"));
        using (var doc = JsonDocument.Parse(settingsJson))
        {
            Check(doc.RootElement.TryGetProperty("terrain_3d_relief", out var opt) &&
                  opt.GetProperty("category").GetString() == "video" &&
                  opt.GetProperty("kind").GetString() == "toggle" &&
                  opt.GetProperty("default").GetBoolean(),
                "12_map_style.json registers terrain_3d_relief video toggle defaulting to true");
            Check(doc.RootElement.TryGetProperty("map_upscaling_mode", out var up) &&
                  up.GetProperty("category").GetString() == "video" &&
                  up.GetProperty("kind").GetString() == "choice" &&
                  up.GetProperty("default").GetString() == "auto",
                "12_map_style.json registers map_upscaling_mode video choice defaulting to auto");
            Check(doc.RootElement.TryGetProperty("map_fsr_sharpness", out var sharp) &&
                  sharp.GetProperty("category").GetString() == "video" &&
                  sharp.GetProperty("kind").GetString() == "slider",
                "12_map_style.json registers map_fsr_sharpness video slider");
        }

        var settingsApply = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "SettingsApply.cs"));
        Check(settingsApply.Contains("case \"terrain_3d_relief\":", StringComparison.Ordinal) &&
              settingsApply.Contains("case \"map_upscaling_mode\":", StringComparison.Ordinal) &&
              settingsApply.Contains("case \"map_fsr_sharpness\":", StringComparison.Ordinal),
            "SettingsApply.cs handles terrain_3d_relief, map_upscaling_mode, and map_fsr_sharpness live");

        var setupSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.Setup.cs"));
        Check(setupSrc.Contains("Scaling3DModeEnum.Fsr2", StringComparison.Ordinal) &&
              setupSrc.Contains("Scaling3DModeEnum.Fsr", StringComparison.Ordinal) &&
              setupSrc.Contains("map_upscaling_mode", StringComparison.Ordinal) &&
              setupSrc.Contains("map_fsr_sharpness", StringComparison.Ordinal) &&
              setupSrc.Contains("gl_compatibility", StringComparison.Ordinal),
            "GlobePresentation.Setup.cs wires FSR 1.0 / FSR 2.2 / Bilinear and FsrSharpness with gl_compatibility fallback");

        var visualStyle = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeVisualStyle.cs"));
        Check(visualStyle.Contains("!ReducedMap && !_mapLookPaper && tier < 1.5f", StringComparison.Ordinal) &&
              visualStyle.Contains("ApplyReliefMeshSubdivision", StringComparison.Ordinal) &&
              visualStyle.Contains("ApplyReliefState", StringComparison.Ordinal),
            "GlobeVisualStyle guards 3D relief on Paper/ReducedMap/MobileLow and updates subdivision + marker clearance");

        // EN + RU localization parity for terrain_3d_relief and FSR upscaling controls.
        var enPath = Path.Combine(root, "game", "localization", "english", "99_terrain_relief_wave6.json");
        var ruPath = Path.Combine(root, "game", "localization", "russian", "99_terrain_relief_wave6.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU 99_terrain_relief_wave6.json exist");
        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enKeys = enDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray();
        var ruKeys = ruDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray();
        Check(enKeys.Length >= 10 && enKeys.SequenceEqual(ruKeys) &&
              ruDoc.RootElement.EnumerateObject().All(p => !string.IsNullOrWhiteSpace(p.Value.GetString())),
            $"99_terrain_relief_wave6.json EN/RU parity ({enKeys.Length} keys)");
    }

    private static void TestWave6FPcFreezeVramAndGovernorContracts()
    {
        var root = FindRepoRoot();

        // 1. Bounded LRU + compact 128x64 canvas on _conditionTextures (fixes +822 MiB VRAM leak).
        var battleMapSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "BattleMapPresentation.cs"));
        Check(battleMapSrc.Contains("ConditionTextureCap = 256", StringComparison.Ordinal) &&
              battleMapSrc.Contains("ConditionCanvasWidth = 128", StringComparison.Ordinal) &&
              battleMapSrc.Contains("ConditionCanvasHeight = 64", StringComparison.Ordinal) &&
              battleMapSrc.Contains("using var img = Image.CreateFromData", StringComparison.Ordinal),
            "BattleMapPresentation bounds _conditionTextures LRU<=256 at 128x64 with using-disposed Image");

        // 2. Pooled SurfaceBattleBadge SubViewports.
        var milSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "MilitaryPresentation.cs"));
        var badgeSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "SurfaceBattleBadge.cs"));
        Check(milSrc.Contains("_battleBadgePool", StringComparison.Ordinal) &&
              badgeSrc.Contains("PrepareForPool", StringComparison.Ordinal) &&
              badgeSrc.Contains("RestoreFromPool", StringComparison.Ordinal),
            "SurfaceBattleBadge SubViewports are pooled via PrepareForPool/RestoreFromPool");

        // 3. Compact R8 hydrology_map (-47.5 MiB VRAM) + BuildLakeSdf equivalence check.
        var hydroPixels = new byte[16 * 8 * 2];
        hydroPixels[(4 * 16 + 6) * 2] = 200; // lake cell
        hydroPixels[(4 * 16 + 7) * 2] = 200;
        var hydroRaster = new HydrologyRaster(16, 8, hydroPixels, 0, 1);
        var r8 = HydrologySurfaceData.BuildLakeSdf(hydroRaster);
        var rgb8 = HydrologySurfaceData.Build(hydroRaster);
        var r8Exact = r8.Length == 16 * 8;
        for (var i = 0; i < r8.Length && r8Exact; i++)
            if (r8[i] != rgb8[i * 3]) r8Exact = false;
        Check(r8Exact, "HydrologySurfaceData.BuildLakeSdf R8 matches channel 0 of Build() bit-for-bit");

        var globeSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.cs"));
        Check(globeSrc.Contains("Image.Format.R8, result.HydrologyPixels", StringComparison.Ordinal) &&
              globeSrc.Contains("BuildLakeSdf", StringComparison.Ordinal),
            "GlobePresentation uploads R8 hydrology_map via BuildLakeSdf");
        Check(!globeSrc.Contains("GCLatencyMode.SustainedLowLatency", StringComparison.Ordinal),
            "GlobePresentation does not pin GCLatencyMode.SustainedLowLatency permanently");

        // 4. runtimeconfig.template.json shipped by Tools/build.ps1.
        var buildPs1 = File.ReadAllText(Path.Combine(root, "Tools", "build.ps1"));
        Check(buildPs1.Contains("runtimeconfig.template.json", StringComparison.Ordinal),
            "Tools/build.ps1 copies runtimeconfig.template.json into export staging");

        // 5. FrontierField LOH scratch pooling & bounded parallelism + bit-identical repeated builds.
        var ffSrc = File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "FrontierField.cs"));
        Check(ffSrc.Contains("_poolHolders", StringComparison.Ordinal) &&
              ffSrc.Contains("Environment.ProcessorCount / 2", StringComparison.Ordinal),
            "FrontierField pools LOH scratch buffers and bounds Parallel.For threads");
        var ffHolders = new ushort[32 * 16];
        var ffProv = new ushort[32 * 16];
        for (var i = 0; i < ffHolders.Length; i++) { ffHolders[i] = (ushort)(i % 32 < 16 ? 1 : 2); ffProv[i] = ffHolders[i]; }
        var ffOwners = new ushort[] { 0, 1, 2 };
        var ff1 = FrontierField.BuildManaged(ffHolders, ffProv, 32, 16, ffOwners, ffOwners);
        var ff2 = FrontierField.BuildManaged(ffHolders, ffProv, 32, 16, ffOwners, ffOwners);
        Check(ff1.SequenceEqual(ff2), "FrontierField pooled BuildManaged is 100% deterministic across repeated calls");

        // 6. FrameRateGovernor bug fixes: actual frameSeconds accumulation, sustained <7.3 FPS slideshow step-down, !gpuKnown fallback.
        QualityProfiles.ResetCatalog();
        var ladder = QualityProfiles.Ladder;
        // 6a. Frame-time accumulation must NOT step down on the very first full window (must wait StepDownSeconds).
        var timingGov = new FrameRateGovernor(ladder[0], automatic: true, windowFrames: 12);
        // Warm up 20s with fast 20ms frames (1000 frames)
        for (var f = 0; f < 1005; f++) timingGov.Sample(20.0, 10.0);
        // Feed 12 slow frames (50ms each = 0.6s < StepDownSeconds 4.0s): MUST NOT step down yet!
        QualityProfile premature = null;
        for (var f = 0; f < 12; f++) premature = timingGov.Sample(50.0, 45.0) ?? premature;
        Check(premature is null, "FrameRateGovernor accumulates frameSeconds (does not step down prematurely on first full window)");

        // 6b. Sustained <7.3 FPS slideshow (220ms > SlowFrameMs*4 = 136ms) MUST step down (previously discarded forever!).
        var slideGov = new FrameRateGovernor(ladder[0], automatic: true, windowFrames: 12);
        QualityProfile slideStep = null;
        for (var f = 0; f < 400 && slideStep is null; f++) slideStep = slideGov.Sample(220.0, 180.0);
        Check(slideStep is not null && slideStep.Order > ladder[0].Order,
            $"FrameRateGovernor steps down on sustained <7.3 FPS slideshow (now {slideGov.Profile.Id})");

        // 6c. !gpuKnown fallback (gpuMilliseconds = -1) steps down on sustained >1.25x SlowFrameMs frames.
        var noGpuGov = new FrameRateGovernor(ladder[0], automatic: true, windowFrames: 12);
        QualityProfile noGpuStep = null;
        for (var f = 0; f < 800 && noGpuStep is null; f++) noGpuStep = noGpuGov.Sample(60.0, -1.0);
        Check(noGpuStep is not null && noGpuStep.Order > ladder[0].Order,
            $"FrameRateGovernor steps down via frame-time fallback when GPU timer unavailable (now {noGpuGov.Profile.Id})");

        // 7. Shader fragment-fetch guards + PackedMapShader 12-sampler contract.
        var shaderSrc = File.ReadAllText(Path.Combine(root, "game", "shaders", "globe_map.gdshader"));
        Check(shaderSrc.Contains("if (!paper_only && political.a > 0.001)", StringComparison.Ordinal) &&
              shaderSrc.Contains("bool need_region_bounds", StringComparison.Ordinal) &&
              shaderSrc.Contains("bool need_province_bounds", StringComparison.Ordinal),
            "globe_map.gdshader guards terrain_coverage, region_ids, and province_ids behind land/visibility checks");
    }

    private static void TestHydrologyBootCacheAndWaveM1Contracts(string tempRoot)
    {
        const int w = 64;
        const int h = 32;
        const ulong fp = 0x55AA12349876CDEFUL;
        var lake = new byte[w * h];
        var river = new byte[w * h * 4];
        for (var i = 0; i < lake.Length; i++) lake[i] = (byte)(i % 17 == 0 ? 12 : 255);
        for (var i = 0; i < river.Length; i += 4)
        {
            river[i] = 128;
            river[i + 1] = 128;
            river[i + 2] = (byte)(i % 64 == 0 ? 180 : 0);
            river[i + 3] = (byte)(i % 64 == 0 ? 220 : 0);
        }
        var bundle = new HydrologyBootCacheData
        {
            LakeSdfR8 = lake,
            RiverCentreRgba8 = river,
            HydrologySource = "synthetic_hydro_v2"
        };

        var dir = Path.Combine(tempRoot, "hydro_roundtrip");
        Check(MapBootCache.TryWriteHydrology(dir, fp, w, h, bundle, out var writeErr),
            $"hydrology_v2.bin TryWriteHydrology succeeded ({writeErr})");
        var filePath = Path.Combine(dir, MapBootCache.HydrologyFileName);
        Check(File.Exists(filePath), "hydrology_v2.bin created on disk");

        Check(MapBootCache.TryReadHydrology(dir, fp, w, h, out var loaded, out var readReason),
            $"hydrology_v2.bin TryReadHydrology hit ({readReason})");
        Check(loaded.HydrologySource == "synthetic_hydro_v2" &&
              loaded.LakeSdfR8.SequenceEqual(lake) &&
              loaded.RiverCentreRgba8.SequenceEqual(river),
            "hydrology_v2.bin LakeSdfR8, RiverCentreRgba8, and HydrologySource round-trip bit-identically");

        // Wrong fingerprint & dimension mismatch rejection
        Check(!MapBootCache.TryReadHydrology(dir, fp ^ 1UL, w, h, out _, out _),
            "hydrology_v2.bin rejects mismatched region fingerprint");
        Check(!MapBootCache.TryReadHydrology(dir, fp, w + 1, h, out _, out _),
            "hydrology_v2.bin rejects mismatched dimensions");

        // Shipped fallback when writable file is corrupted
        var shippedDir = Path.Combine(tempRoot, "hydro_shipped");
        Check(MapBootCache.TryWriteHydrology(shippedDir, fp, w, h, bundle, out _), "wrote shipped hydrology_v2.bin");
        var shippedPath = Path.Combine(shippedDir, MapBootCache.HydrologyFileName);
        var raw = File.ReadAllBytes(filePath);
        raw[^1] ^= 0x5A;
        File.WriteAllBytes(filePath, raw);
        Check(!MapBootCache.TryReadHydrology(dir, fp, w, h, out _, out _),
            "corrupted hydrology_v2.bin rejected cleanly by FNV-1a checksum");
        Check(MapBootCache.TryReadHydrology(dir, fp, w, h, out var fromShipped, out _, shippedPath) &&
              fromShipped.LakeSdfR8.SequenceEqual(lake),
            "corrupted writable hydrology_v2.bin falls back cleanly to shipped fallback");

        // ModOverlayPolicy bypass
        ModOverlayPolicy.Configure(new[]
        {
            new ModLayerSpec("test_hydro_mod", TotalConversion: true,
                ReplacePaths: Array.Empty<string>(), ExcludePaths: Array.Empty<string>())
        });
        try
        {
            Check(MapBootCache.ResolveReadableHydrologyCachePath(shippedDir) is null,
                "ResolveReadableHydrologyCachePath bypassed when mod overlay active");
            Check(!MapBootCache.TryReadHydrology(shippedDir, fp, w, h, out _, out _),
                "TryReadHydrology bypassed when mod overlay active");
            Check(!MapBootCache.TryWriteHydrology(shippedDir, fp, w, h, bundle, out _),
                "TryWriteHydrology bypassed when mod overlay active");
        }
        finally
        {
            ModOverlayPolicy.Reset();
        }

        // Static Wave M1 contracts (Zero-Leak Touch, BlueStacks/Mobile 40-60 FPS, <15s Map Boot)
        var root = FindRepoRoot();
        var scriptsDir = Path.Combine(root, "Scripts");

        var inputSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobePresentation.Input.cs"));
        Check(inputSrc.Contains("IsTouchEmulatedMouse", StringComparison.Ordinal) &&
              inputSrc.Contains("mouseEvent.Device == -1", StringComparison.Ordinal) &&
              inputSrc.Contains("350_000UL", StringComparison.Ordinal) &&
              inputSrc.Contains("_dragging && !_boxSelecting", StringComparison.Ordinal),
            "GlobePresentation.Input swallows touch-emulated mouse events and guards desktop BoxSelect drag");

        var hooksSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobePlatformHooks.cs"));
        Check(hooksSrc.Contains("internal TouchCameraAdapter TouchAdapter => _touchAdapter;", StringComparison.Ordinal) &&
              hooksSrc.Contains("_touchAdapter.BoxSelectUpdated", StringComparison.Ordinal) &&
              hooksSrc.Contains("_touchAdapter.BoxSelectCompleted", StringComparison.Ordinal),
            "GlobePlatformHooks exposes TouchAdapter and wires touch BoxSelect marquee + DispatchBoxUnitSelection");

        var setupSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobePresentation.Setup.cs"));
        Check(setupSrc.Contains("longEdge > 1600", StringComparison.Ordinal) &&
              setupSrc.Contains("longEdge > 2048", StringComparison.Ordinal) &&
              !setupSrc.Contains("longEdge > 1280 || shortEdge > 720", StringComparison.Ordinal) &&
              setupSrc.Contains("MobileAntialiasing()", StringComparison.Ordinal) &&
              setupSrc.Contains("Viewport.Msaa.Msaa2X", StringComparison.Ordinal) &&
              setupSrc.Contains("Viewport.Msaa.Disabled", StringComparison.Ordinal) &&
              setupSrc.Contains("MobileEffectiveRenderScale(", StringComparison.Ordinal) &&
              setupSrc.Contains("0.82f", StringComparison.Ordinal) &&
              setupSrc.Contains("0.85f", StringComparison.Ordinal) &&
              setupSrc.Contains("0.95f", StringComparison.Ordinal) &&
              setupSrc.Contains("0.50f", StringComparison.Ordinal) &&
              !setupSrc.Contains("0.68f", StringComparison.Ordinal) &&
              !setupSrc.Contains("0.78f", StringComparison.Ordinal),
            "GlobePresentation.Setup caps mobile SubViewport long edge at 1600/2048, uses Msaa2X on High and 0.82f/0.85f/0.95f/0.50f MobileEffectiveRenderScale bounds");

        var globeSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobePresentation.cs"));
        Check(globeSrc.Contains("TryReadHydrology(", StringComparison.Ordinal) &&
              globeSrc.Contains("TryWriteHydrology(", StringComparison.Ordinal) &&
              globeSrc.Contains("CurrentShaderQualityTier() < 0.5f", StringComparison.Ordinal),
            "GlobePresentation wires hydrology_v2.bin read/write and restricts 3D settlement models to DesktopHigh");

        var weatherSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobeWeather.cs"));
        var tradeSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobeTradeTraffic.cs"));
        Check(weatherSrc.Contains("_weatherQualityTier >= 1.5f", StringComparison.Ordinal) &&
              weatherSrc.Contains("_weatherMobileAccum", StringComparison.Ordinal) &&
              tradeSrc.Contains("_tradeQualityTier >= 1.5f", StringComparison.Ordinal) &&
              tradeSrc.Contains("_tradeMobileAccum", StringComparison.Ordinal),
            "GlobeWeather and GlobeTradeTraffic skip MobileLow layers and halve MobileMedium cadence");
    }

    private static void TestPoliticalRasterBootCacheAndWaveM3AContracts(string tempRoot)
    {
        Check(MapBootCache.PoliticalRasterFileName == "political_rasters_v1.bin" &&
              MapBootCache.PoliticalRasterMagic == 0x43504D44u &&
              MapBootCache.PoliticalRasterFormatVersion == 1,
            "MapBootCache political raster constants match political_rasters_v1.bin v1 (0x43504D44)");

        const int width = 48;
        const int height = 24;
        const int politicalScale = 3;
        const int politicalWidth = width / politicalScale;
        const int politicalHeight = height / politicalScale;
        const int plateCells = politicalWidth * politicalHeight;
        const int fullRgBytes = width * height * 2;
        const ulong fingerprint = 0xCAFEBABE12345678UL;

        var plateProvinces = new ushort[plateCells];
        var plateHolders = new ushort[plateCells];
        var plateGeography = new byte[plateCells];
        var plateClass = new byte[plateCells];
        for (var i = 0; i < plateCells; i++)
        {
            plateProvinces[i] = (i % 5 == 0) ? (ushort)0 : (ushort)(300 + i);
            plateHolders[i] = (i % 5 == 0) ? (ushort)0 : (ushort)(10 + (i % 19));
            plateGeography[i] = (byte)(i % 4);
            plateClass[i] = (byte)(i % 4);
        }

        var regionIds = new byte[fullRgBytes];
        var provinceIds = new byte[fullRgBytes];
        var nationIds = new byte[fullRgBytes];
        for (var p = 0; p < width * height; p++)
        {
            var write = p * 2;
            var reg = (ushort)((p % 9 == 0) ? 0 : (1000 + (p % 311)));
            var prov = (ushort)((p % 9 == 0) ? 0 : (1 + (p % 1999)));
            var nat = (ushort)((p % 9 == 0) ? 0 : (1 + (p % 47)));
            regionIds[write] = (byte)(reg & 0xFF);
            regionIds[write + 1] = (byte)((reg >> 8) & 0xFF);
            provinceIds[write] = (byte)(prov & 0xFF);
            provinceIds[write + 1] = (byte)((prov >> 8) & 0xFF);
            nationIds[write] = (byte)(nat & 0xFF);
            nationIds[write + 1] = (byte)((nat >> 8) & 0xFF);
        }

        var rasterBundle = new PoliticalRasterBootCacheData
        {
            PlateProvinces = plateProvinces,
            PlateHolders = plateHolders,
            PlateGeography = plateGeography,
            PlateClass = plateClass,
            RegionIds = regionIds,
            ProvinceIds = provinceIds,
            NationIds = nationIds
        };

        var writableDir = Path.Combine(tempRoot, "political_rasters_writable");
        Check(MapBootCache.TryWritePoliticalRasters(writableDir, fingerprint, width, height, politicalScale, rasterBundle, out var writeErr),
            $"TryWritePoliticalRasters succeeds ({writeErr})");
        var cachePath = Path.Combine(writableDir, MapBootCache.PoliticalRasterFileName);
        Check(File.Exists(cachePath) && new FileInfo(cachePath).Length > 40, "political_rasters_v1.bin exists and is non-empty");
        Check(MapBootCache.ResolveReadablePoliticalRasterCachePath(writableDir) == cachePath,
            "ResolveReadablePoliticalRasterCachePath finds writable political_rasters_v1.bin");

        Check(MapBootCache.TryReadPoliticalRasters(
                writableDir, fingerprint, width, height, politicalScale,
                out var restored, out var readReason) &&
              restored is not null &&
              restored.PlateProvinces.SequenceEqual(plateProvinces) &&
              restored.PlateHolders.SequenceEqual(plateHolders) &&
              restored.PlateGeography.SequenceEqual(plateGeography) &&
              restored.PlateClass.SequenceEqual(plateClass) &&
              restored.RegionIds.SequenceEqual(regionIds) &&
              restored.ProvinceIds.SequenceEqual(provinceIds) &&
              restored.NationIds.SequenceEqual(nationIds),
            $"political_rasters_v1.bin LZ4 round-trip exact across all 7 buffers ({readReason})");

        // Key invalidations: fingerprint, width, height, scale, invalid write buffer.
        Check(!MapBootCache.TryReadPoliticalRasters(writableDir, fingerprint ^ 1UL, width, height, politicalScale, out _, out _),
            "political raster fingerprint mismatch rejected cleanly");
        Check(!MapBootCache.TryReadPoliticalRasters(writableDir, fingerprint, width + 3, height, politicalScale, out _, out _),
            "political raster width mismatch rejected cleanly");
        Check(!MapBootCache.TryReadPoliticalRasters(writableDir, fingerprint, width, height + 3, politicalScale, out _, out _),
            "political raster height mismatch rejected cleanly");
        Check(!MapBootCache.TryReadPoliticalRasters(writableDir, fingerprint, width, height, politicalScale + 1, out _, out _),
            "political raster scale mismatch rejected cleanly");
        Check(!MapBootCache.TryWritePoliticalRasters(writableDir, fingerprint, width, height, politicalScale,
                new PoliticalRasterBootCacheData { PlateProvinces = new ushort[1] }, out _),
            "TryWritePoliticalRasters rejects mismatched buffer lengths cleanly");

        // Shipped fallback + corruption safety (bad byte, truncated header, bad magic, wrong version).
        var emptyDir = Path.Combine(tempRoot, "political_rasters_empty");
        Directory.CreateDirectory(emptyDir);
        Check(MapBootCache.ResolveReadablePoliticalRasterCachePath(emptyDir, cachePath) == cachePath,
            "ResolveReadablePoliticalRasterCachePath falls back to shipped APK cache");
        Check(MapBootCache.TryReadPoliticalRasters(
                emptyDir, fingerprint, width, height, politicalScale,
                out var fromShipped, out _, shippedFallbackPath: cachePath) &&
              fromShipped is not null &&
              fromShipped.NationIds.SequenceEqual(nationIds),
            "TryReadPoliticalRasters loads from shipped fallback path");

        var corruptDir = Path.Combine(tempRoot, "political_rasters_corrupt");
        Directory.CreateDirectory(corruptDir);
        var corruptPath = Path.Combine(corruptDir, MapBootCache.PoliticalRasterFileName);
        var bytes = File.ReadAllBytes(cachePath);
        bytes[^1] ^= 0x04;
        File.WriteAllBytes(corruptPath, bytes);
        Check(!MapBootCache.TryReadPoliticalRastersFile(corruptPath, fingerprint, width, height, politicalScale, out _, out var crcErr),
            $"political raster single-bit flip rejected by FNV-1a checksum ({crcErr})");
        Check(MapBootCache.TryReadPoliticalRasters(
                corruptDir, fingerprint, width, height, politicalScale,
                out var rescued, out _, shippedFallbackPath: cachePath) &&
              rescued is not null &&
              rescued.ProvinceIds.SequenceEqual(provinceIds),
            "corrupt writable political raster falls back cleanly to valid shipped cache");

        var truncPath = Path.Combine(tempRoot, "political_rasters_trunc.bin");
        File.WriteAllBytes(truncPath, new byte[20]);
        Check(!MapBootCache.TryReadPoliticalRastersFile(truncPath, fingerprint, width, height, politicalScale, out _, out _),
            "truncated political raster cache file rejected cleanly");

        var badMagicPath = Path.Combine(tempRoot, "political_rasters_magic.bin");
        var badMagicBytes = File.ReadAllBytes(cachePath);
        badMagicBytes[0] ^= 0xFF;
        File.WriteAllBytes(badMagicPath, badMagicBytes);
        Check(!MapBootCache.TryReadPoliticalRastersFile(badMagicPath, fingerprint, width, height, politicalScale, out _, out _),
            "bad magic political raster file rejected cleanly");

        var staleVersionPath = Path.Combine(tempRoot, "political_rasters_v99.bin");
        var staleBytes = File.ReadAllBytes(cachePath);
        BitConverter.GetBytes(99).CopyTo(staleBytes, 4);
        File.WriteAllBytes(staleVersionPath, staleBytes);
        Check(!MapBootCache.TryReadPoliticalRastersFile(staleVersionPath, fingerprint, width, height, politicalScale, out _, out _),
            "wrong version political raster file rejected cleanly");

        // ModOverlayPolicy bypass.
        ModOverlayPolicy.Configure(new[]
        {
            new ModLayerSpec("test_political_mod", TotalConversion: true,
                ReplacePaths: Array.Empty<string>(), ExcludePaths: Array.Empty<string>())
        });
        try
        {
            Check(MapBootCache.ResolveReadablePoliticalRasterCachePath(writableDir, cachePath) is null,
                "ResolveReadablePoliticalRasterCachePath bypassed when mod overlay active");
            Check(!MapBootCache.TryWritePoliticalRasters(writableDir, fingerprint, width, height, politicalScale, rasterBundle, out _),
                "TryWritePoliticalRasters bypassed when mod overlay active");
            Check(!MapBootCache.TryReadPoliticalRasters(writableDir, fingerprint, width, height, politicalScale, out _, out _, cachePath),
                "TryReadPoliticalRasters bypassed when mod overlay active");
        }
        finally
        {
            ModOverlayPolicy.Reset();
        }

        // Static Wave M3A crisp Android rendering, Speed 4/5 throttle, MapBootCache purity, and Desktop relief preservation.
        var root = FindRepoRoot();
        var scriptsDir = Path.Combine(root, "Scripts");

        var cacheSrc = File.ReadAllText(Path.Combine(scriptsDir, "Simulation", "MapBootCache.cs"));
        Check(!cacheSrc.Contains("Godot", StringComparison.Ordinal) &&
              !cacheSrc.Contains(".GetHashCode(", StringComparison.Ordinal),
            "MapBootCache stays 100% pure C# (zero Godot references, zero string.GetHashCode)");

        var setupSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobePresentation.Setup.cs"));
        Check(setupSrc.Contains("Anisotropy4X", StringComparison.Ordinal) &&
              setupSrc.Contains("Anisotropy8X", StringComparison.Ordinal) &&
              setupSrc.Contains("-0.35f", StringComparison.Ordinal) &&
              setupSrc.Contains("-0.20f", StringComparison.Ordinal) &&
              setupSrc.Contains("ScreenSpaceAAEnum.Disabled", StringComparison.Ordinal) &&
              setupSrc.Contains("CloudsEnabled = false", StringComparison.Ordinal) &&
              setupSrc.Contains("Scaling3DModeEnum.Bilinear", StringComparison.Ordinal) &&
              setupSrc.Contains("PoliticalRasterFileName", StringComparison.Ordinal),
            "GlobePresentation.Setup wires M3A mobile crispness (4X aniso, -0.35 bias, no ScreenSpaceAA, Bilinear, default clouds off, raster cache)");

        var weatherSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobeWeather.cs"));
        Check(weatherSrc.Contains("!(Platform.PlatformProfile.Current?.IsTouchPrimary == true)", StringComparison.Ordinal) &&
              weatherSrc.Contains("Has(\"map_clouds\")", StringComparison.Ordinal),
            "GlobeWeather defaults CloudsShown to false on IsTouchPrimary while respecting player settings");

        var globeSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobePresentation.cs"));
        Check(globeSrc.Contains("TryReadPoliticalRasters(", StringComparison.Ordinal) &&
              globeSrc.Contains("TryWritePoliticalRasters(", StringComparison.Ordinal) &&
              globeSrc.Contains("UnpackLittleEndianUShorts(", StringComparison.Ordinal) &&
              globeSrc.Contains("_routeVisualAccumulator", StringComparison.Ordinal) &&
              globeSrc.Contains("Speed: >= 4", StringComparison.Ordinal),
            "GlobePresentation integrates political_rasters_v1.bin skip/write paths and Speed 4/5 visual reconcile throttle");

        var militarySrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "MilitaryPresentation.cs"));
        var labelsSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobePresentation.Labels.cs"));
        var regionsSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobeRegionLabels.cs"));
        var citiesSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobeCities.cs"));
        var unitsSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobePresentation.UnitsAndMarkers.cs"));
        var fleetsSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobePresentation.FleetsAndCapitals.cs"));
        var supportSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobeSupportPresentation.cs"));
        var movementSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobeMovements.cs"));
        var compactSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "CompactCounterTextures.cs"));
        Check(labelsSrc.Contains("LinearWithMipmapsAnisotropic", StringComparison.Ordinal) &&
              regionsSrc.Contains("LinearWithMipmapsAnisotropic", StringComparison.Ordinal) &&
              citiesSrc.Contains("LinearWithMipmapsAnisotropic", StringComparison.Ordinal) &&
              unitsSrc.Contains("LinearWithMipmapsAnisotropic", StringComparison.Ordinal) &&
              fleetsSrc.Contains("LinearWithMipmapsAnisotropic", StringComparison.Ordinal) &&
              supportSrc.Contains("LinearWithMipmapsAnisotropic", StringComparison.Ordinal) &&
              movementSrc.Contains("LinearWithMipmapsAnisotropic", StringComparison.Ordinal) &&
              citiesSrc.Contains("GenerateMipmaps()", StringComparison.Ordinal) &&
              militarySrc.Contains("GenerateMipmaps()", StringComparison.Ordinal) &&
              militarySrc.Contains("Speed: >= 4", StringComparison.Ordinal) &&
              fleetsSrc.Contains("GenerateMipmaps()", StringComparison.Ordinal) &&
              supportSrc.Contains("GenerateMipmaps()", StringComparison.Ordinal) &&
              movementSrc.Contains("GenerateMipmaps()", StringComparison.Ordinal) &&
              compactSrc.Contains("GenerateMipmaps()", StringComparison.Ordinal),
            "3D labels and sprite counters generate mipmaps and enable LinearWithMipmapsAnisotropic filtering");

        var visualStyleSrc = File.ReadAllText(Path.Combine(scriptsDir, "gfx", "GlobeVisualStyle.cs"));
        var globeShaderSrc = File.ReadAllText(Path.Combine(root, "game", "shaders", "globe_map.gdshader"));
        Check(visualStyleSrc.Contains("relief_displacement", StringComparison.Ordinal) &&
              globeShaderSrc.Contains("relief_displacement", StringComparison.Ordinal),
            "Desktop PC 3D heightmap relief_displacement remains 100% intact");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
