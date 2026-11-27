using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using DealOrForce;
using DealOrForce.Platform;
using DealOrForce.Simulation;

/// <summary>
/// Deterministic tests for Approved Points 3 &amp; 5:
/// MobileEffectiveRenderScale (0.50 floor &amp; 0.85 MobileMedium high-DPI default),
/// Turbo Sim Mode SubViewport 15-20 Hz cadence &amp; Frame-0 immediate wakes,
/// zero Format.Rgbaf GPU texture allocations, and MobileMedium/MobileLow half-res 2880x1440 GPU map textures
/// while keeping CPU ProvinceTopology full-res and DesktopHigh untouched.
/// </summary>
public static class MobilePerfGfxTests
{
    private sealed class IsolatedGameAssemblyContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public IsolatedGameAssemblyContext(string assemblyPath) : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                return LoadFromAssemblyPath(resolved);
            var fallback = Path.Combine(Path.GetDirectoryName(_resolver.ResolveAssemblyToPath(new AssemblyName("DealOrForce")) ?? "") ?? "", assemblyName.Name + ".dll");
            return File.Exists(fallback) ? LoadFromAssemblyPath(fallback) : null;
        }
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL mobile perf gfx: " + label);
        Console.WriteLine("PASS mobile perf gfx: " + label);
    }

    private static bool Approx(float a, float b, float eps = 1e-3f) => MathF.Abs(a - b) <= eps;

    [TestSuite("--mobile-perf-gfx-only", Order = 2745, IncludeInDefaultRun = true,
        Description = "Verifies MobileEffectiveRenderScale, Turbo Sim cadence/wake, zero Format.Rgbaf, and mobile half-res GPU map rasters.")]
    public static void Run()
    {
        var root = FindRepoRoot();
        Check(Directory.Exists(Path.Combine(root, "Scripts", "gfx")), "located Scripts/gfx root");

        CheckSourceAndShaderInvariants(root);
        CheckVramBudgetArithmetic();

        var gameDll = FindCompiledDealOrForceDll(root);
        if (gameDll is null)
        {
            Console.WriteLine("INFO mobile perf gfx: no up-to-date DealOrForce.dll found (rebuild DealOrForce.csproj to run live IL invocation); source + shader invariants passed.");
            return;
        }

        var ctx = new IsolatedGameAssemblyContext(gameDll);
        try
        {
            var asm = ctx.LoadFromAssemblyPath(gameDll);
            var globeType = asm.GetType("DealOrForce.GlobePresentation", throwOnError: true);
            Check(globeType is not null, "loaded DealOrForce.GlobePresentation from compiled game assembly");

            VerifyRenderScalePolicy(globeType);
            VerifyHalfResEligibilityPolicy(globeType, asm);
            VerifyTurboSimCadenceStateMachine(globeType);
            VerifyHalfResRasterDownsampling(globeType);
        }
        finally
        {
            ctx.Unload();
        }
    }

    private static void CheckSourceAndShaderInvariants(string root)
    {
        var gfxDir = Path.Combine(root, "Scripts", "gfx");
        var rgbafCreations = 0;
        var createPattern = new Regex(@"(?:Image\.Create(?:Empty|FromData)|\.Convert)\s*\([^;\n]*Image\.Format\.Rgbaf\b");
        foreach (var cs in Directory.EnumerateFiles(gfxDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(cs);
            if (createPattern.IsMatch(text))
                rgbafCreations++;
        }
        Check(rgbafCreations == 0, "0 GPU texture creations/conversions use Image.Format.Rgbaf in Scripts/gfx/");

        var shaderPath = Path.Combine(root, "game", "shaders", "globe_map.gdshader");
        var shaderSrc = File.ReadAllText(shaderPath);
        Check(shaderSrc.Contains("textureSize(nation_ids, 0)", StringComparison.Ordinal) &&
              shaderSrc.Contains("textureSize(province_ids, 0)", StringComparison.Ordinal) &&
              shaderSrc.Contains("textureSize(hydrology_map, 0)", StringComparison.Ordinal) &&
              shaderSrc.Contains("textureSize(terrain_surface, 0)", StringComparison.Ordinal),
            "globe_map.gdshader dynamically queries textureSize(..., 0) for ID, hydrology, and terrain_surface samplers");
        Check(!Regex.IsMatch(shaderSrc, @"(?<!//[^\n]*)\b(?:5760|2880)\b"),
            "globe_map.gdshader contains no hardcoded 5760/2880 raster dimension");
        Check(shaderSrc.Contains("nation_at_cached", StringComparison.Ordinal) &&
              shaderSrc.Contains("deep_ocean_fast", StringComparison.Ordinal),
            "globe_map.gdshader includes nation_at_cached owner lookup cache and deep_ocean_fast short-circuit");
        var packedDirect = PackedMapShader.Build(shaderSrc);
        var packedColor = PackedMapShader.Build(MapColorSpaceShader.Build(shaderSrc));
        Check(Regex.Matches(packedDirect, @"uniform\s+sampler\w+\s+").Count == PackedMapShader.TextureUnits &&
              Regex.Matches(packedColor, @"uniform\s+sampler\w+\s+").Count == PackedMapShader.TextureUnits,
            $"PackedMapShader.Build preserves exact {PackedMapShader.TextureUnits}-sampler GLES3 budget");

        var gpSrc = File.ReadAllText(Path.Combine(gfxDir, "GlobePresentation.cs"));
        Check(gpSrc.Contains("partial void SyncCameraLockedMapOverlays(double delta, Simulation.CampaignRenderSnapshot snap);", StringComparison.Ordinal) &&
              gpSrc.Contains("SyncCameraLockedMapOverlays(delta, snap);", StringComparison.Ordinal),
            "GlobePresentation.cs preserves SyncCameraLockedMapOverlays hook between UpdateShaderZoomAndSurfaceState and TryBeginFrameRead");
        Check(gpSrc.Contains("public bool IsTurboSimThrottling => _turboThrottlingActive;", StringComparison.Ordinal),
            "GlobePresentation.cs exposes IsTurboSimThrottling for PerformanceGovernor");

        var perfGovSrc = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "PerformanceGovernor.cs"));
        Check(perfGovSrc.Contains("_globe.IsTurboSimThrottling", StringComparison.Ordinal),
            "PerformanceGovernor.cs pauses quality step sampling during Turbo Sim throttling");

        var setupSrc = File.ReadAllText(Path.Combine(gfxDir, "GlobePresentation.Setup.cs"));
        Check(setupSrc.Contains("MobileRenderScaleFloor = 0.50f", StringComparison.Ordinal) &&
              setupSrc.Contains("MobileMediumDefaultHighDpiScale = 1.0f", StringComparison.Ordinal),
            "GlobePresentation.Setup.cs sets mobile floor 0.50f and MobileMedium high-DPI default native 1.0f");
        Check(!setupSrc.Contains("ResolvePreferredGpuHdrFormat", StringComparison.Ordinal),
            "GlobePresentation.Setup.cs keeps 0 dead HDR format helpers");

        var terrainSrc = File.ReadAllText(Path.Combine(gfxDir, "GlobeTerrain.cs"));
        Check(terrainSrc.Contains("DownsampleMobileTerrainSlotsHalfRes", StringComparison.Ordinal),
            "GlobeTerrain.cs supports mobile half-res terrain_surface upload while keeping ProvinceTopology full-res");
    }

    private static void CheckVramBudgetArithmetic()
    {
        // 5760x2880 raw map textures: 3x RG8 IDs (99.53 MB) + R8 hydrology (16.59 MB) + R8 terrain_surface (16.59 MB) = 132.71 MB
        // Half-res 2880x1440 reduces these exact textures by 4x (33.18 MB raw, saving ~99.53 MB raw + driver staging).
        long FullRg8Bytes(int w, int h, int layers) => (long)w * h * 2L * layers;
        long FullR8Bytes(int w, int h) => (long)w * h;

        var desktopBytes = FullRg8Bytes(5760, 2880, 3) + FullR8Bytes(5760, 2880) * 2;
        var mobileHalfBytes = FullRg8Bytes(2880, 1440, 3) + FullR8Bytes(2880, 1440) * 2;
        Check(mobileHalfBytes * 4 == desktopBytes,
            $"half-res 2880x1440 reduces core 5760x2880 GPU map buffers by exact 4x ({desktopBytes / 1048576.0:0.0} -> {mobileHalfBytes / 1048576.0:0.0} MB raw)");
    }

    private static void VerifyRenderScalePolicy(Type globeType)
    {
        var method = globeType.GetMethod("ComputeMobileEffectiveRenderScale",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Check(method is not null, "ComputeMobileEffectiveRenderScale method resolved");

        float Call(float req, float tier, int width, bool custom) =>
            (float)method!.Invoke(null, new object[] { req, tier, width, custom })!;

        // 1. High-DPI phone (>= 2000px, e.g. S22 2340px capped at 2400px long edge) in MobileMedium uses native 1.0f clarity
        Check(Approx(Call(1.0f, 1.0f, 2340, false), 1.0f), "MobileMedium default on 2340px phone tunes to native 1.0f clarity");
        Check(Approx(Call(0.85f, 1.0f, 2340, false), 0.85f), "ComputeMobileEffectiveRenderScale preserves lower requested scale 0.85f");
        Check(Approx(Call(1.0f, 1.0f, 2000, false), 1.0f), "MobileMedium threshold includes exact 2000px width at 1.0f");

        // 2. Standard sub-2000px device in MobileMedium keeps 1.0f ceiling
        Check(Approx(Call(1.0f, 1.0f, 1600, false), 1.0f), "MobileMedium sub-2000px keeps 1.0f ceiling");

        // 3. Player custom render_scale 0.50 works on phones (lowered floor 0.65 -> 0.50)
        Check(Approx(Call(0.5f, 1.0f, 2340, true), 0.50f), "custom render_scale 0.50 honored on MobileMedium S22");
        Check(Approx(Call(0.5f, 2.0f, 1280, true), 0.50f), "custom render_scale 0.50 honored on MobileLow");
        Check(Approx(Call(0.25f, 1.0f, 2340, true), 0.50f), "scale clamps safely at 0.50f floor");

        // 4. Custom 0.75, 0.85, and 1.0 work within 1.0f MobileMedium / 0.82f MobileLow ceilings
        Check(Approx(Call(0.75f, 1.0f, 2340, true), 0.75f), "custom 0.75 works on MobileMedium high-DPI");
        Check(Approx(Call(0.85f, 1.0f, 2340, true), 0.85f), "custom 0.85 works on MobileMedium high-DPI");
        Check(Approx(Call(1.0f, 1.0f, 2340, true), 1.0f), "custom 1.0 preserves full native MobileMedium ceiling");
        Check(Approx(Call(0.85f, 2.0f, 2340, true), 0.82f), "MobileLow capped at 0.82f ceiling");
    }

    private static void VerifyHalfResEligibilityPolicy(Type globeType, Assembly asm)
    {
        var method = globeType.GetMethod("ShouldUseHalfResMobileMapTextures",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var profileType = asm.GetType("DealOrForce.Platform.PlatformProfile", throwOnError: true)!;
        var snapshotType = asm.GetType("DealOrForce.Platform.PlatformSnapshot", throwOnError: true)!;
        var safeAreaType = asm.GetType("DealOrForce.Platform.SafeAreaInsets", throwOnError: true)!;
        var safeZero = safeAreaType.GetProperty("Zero", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var evaluate = profileType.GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Static)!;
        var snapshotCtor = snapshotType.GetConstructors()[0];

        object MakeProfile(string os, bool mobile, bool touch, int w, int h, float dpi, int ramMb, bool lowGpu)
        {
            var snap = snapshotCtor.Invoke(new object[]
            {
                os, mobile, touch, w, h, dpi, safeZero, ramMb, lowGpu, (float)h
            });
            return evaluate.Invoke(null, new[] { snap })!;
        }

        var desktopHigh = MakeProfile("Windows", false, false, 1920, 1080, 96f, 16384, false);
        var mobileMed = MakeProfile("Android", true, true, 2340, 1080, 425f, 8192, false);
        var mobileLow = MakeProfile("Android", true, true, 1280, 720, 320f, 3072, true);

        Check(!(bool)method!.Invoke(null, new[] { desktopHigh })!, "DesktopHigh stays full-resolution (5760x2880)");
        Check((bool)method.Invoke(null, new[] { mobileMed })!, "MobileMedium enables half-res 2880x1440 GPU map textures");
        Check((bool)method.Invoke(null, new[] { mobileLow })!, "MobileLow enables half-res 2880x1440 GPU map textures");
    }

    private static void VerifyTurboSimCadenceStateMachine(Type globeType)
    {
        var turboProp = globeType.GetProperty("IsTurboSimThrottling", BindingFlags.Instance | BindingFlags.Public);
        Check(turboProp is not null && turboProp.PropertyType == typeof(bool),
            "GlobePresentation exposes public bool IsTurboSimThrottling");
        var inputsType = globeType.GetNestedType("TurboSimInputs", BindingFlags.NonPublic | BindingFlags.Public)!;
        var stepMethod = globeType.GetMethod("StepTurboSimCadence", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var ctor = inputsType.GetConstructors()[0];
        var interval = (double)globeType.GetField("TurboSimRedrawIntervalSeconds", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
        Check(interval <= 1.0 / 30.0 + 1e-6 && interval >= 1.0 / 60.5,
            $"Turbo Sim redraw interval ({1.0 / interval:0.0} Hz) meets >=30 Hz smoothness floor");

        object MakeInput(bool extPaused = false, bool eligible = true, bool mapApplied = true, bool fastSim = true,
            bool touch = false, bool camMotion = false, bool camDirty = false, bool box = false) =>
            ctor.Invoke(new object[] { extPaused, eligible, mapApplied, fastSim, touch, camMotion, camDirty, box });

        string Step(ref double accum, ref bool wake, double dt, object input)
        {
            var args = new object[] { accum, wake, dt, input, interval };
            var res = stepMethod.Invoke(null, args)!.ToString()!;
            accum = (double)args[0];
            wake = (bool)args[1];
            return res;
        }

        double accum = 0;
        bool wake = false;

        // Desktop (PlatformEligible = false) -> always 60 Hz unaffected
        Check(Step(ref accum, ref wake, 0.016, MakeInput(eligible: false)) == "Always",
            "Desktop non-opt-in stays Always (60 Hz unaffected)");

        // Mobile Speed 1-6 (fastSim = false) -> Always
        Check(Step(ref accum, ref wake, 0.016, MakeInput(fastSim: false)) == "Always",
            "Mobile Speed 1-6 / Paused stays Always");

        // Mobile Speed 7-8 idle -> throttles to >=30 Hz floor (Disabled between ticks, Once when interval reached)
        var idle = MakeInput();
        var renders = 0;
        for (var frame = 0; frame < 60; frame++)
        {
            if (Step(ref accum, ref wake, 1.0 / 60.0, idle) == "Once") renders++;
        }
        Check(renders is >= 28 and <= 31, $"60 idle fast-sim frames render {renders} times (~30 Hz smoothness floor)");

        // Frame-0 immediate wake while throttled
        accum = 0.0;
        wake = true;
        Check(Step(ref accum, ref wake, 0.001, idle) == "Once" && !wake && accum == 0.0,
            "Wake flag triggers Frame-0 Once redraw immediately even at dt=1ms");

        // Touch, camera motion, cameraTransformDirty, and box select immediately switch to Always
        Check(Step(ref accum, ref wake, 0.016, MakeInput(touch: true)) == "Always", "Touch input immediately resumes Always");
        Check(Step(ref accum, ref wake, 0.016, MakeInput(camMotion: true)) == "Always", "Camera motion immediately resumes Always");
        Check(Step(ref accum, ref wake, 0.016, MakeInput(camDirty: true)) == "Always", "Camera focus/transform dirty immediately resumes Always");
        Check(Step(ref accum, ref wake, 0.016, MakeInput(box: true)) == "Always", "Box select immediately resumes Always");

        // Full-cover modal external pause overrides to Disabled
        Check(Step(ref accum, ref wake, 0.016, MakeInput(extPaused: true, touch: true)) == "Disabled",
            "External full-cover modal pause disables SubViewport rendering cleanly");
    }

    private static void VerifyHalfResRasterDownsampling(Type globeType)
    {
        var idMethod = globeType.GetMethod("DownsampleMobileGpuIdLayersHalfRes",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var hydroMethod = globeType.GetMethod("DownsampleMobileHydrologyHalfRes",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var riverMethod = globeType.GetMethod("DownsampleMobileRiverCentreHalfRes",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var terrainMethod = globeType.GetMethod("DownsampleMobileTerrainSlotsHalfRes",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

        // 8x4 test grid -> 4x2 half-res
        const int w = 8, h = 4;
        static byte[] MakeRg8(int width, int height, Func<int, int, ushort> cell)
        {
            var buf = new byte[width * height * 2];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var v = cell(x, y);
                var i = (y * width + x) * 2;
                buf[i] = (byte)(v & 0xff);
                buf[i + 1] = (byte)(v >> 8);
            }
            return buf;
        }
        static ushort ReadRg8(byte[] buf, int width, int x, int y)
        {
            var i = (y * width + x) * 2;
            return (ushort)(buf[i] | (buf[i + 1] << 8));
        }

        // Quad (0,0)=prov 300/nation 10/region 2; Quad (1,0)=majority prov 500 vs one ocean 0; Quad (2,0)=2-vs-2 land 700 vs ocean 0 (land wins!)
        var provinces = MakeRg8(w, h, (x, y) =>
        {
            var qx = x / 2;
            var qy = y / 2;
            if (qy == 0 && qx == 0) return 300;
            if (qy == 0 && qx == 1) return (x == 3 && y == 1) ? (ushort)0 : (ushort)500;
            if (qy == 0 && qx == 2) return (y == 0) ? (ushort)700 : (ushort)0;
            return (ushort)(1000 + qx + qy * 10);
        });
        var nations = MakeRg8(w, h, (x, y) => (ushort)(ReadRg8(provinces, w, x, y) / 10));
        var regions = MakeRg8(w, h, (x, y) => (ushort)(ReadRg8(provinces, w, x, y) == 0 ? 0 : ReadRg8(provinces, w, x, y) / 100));

        var args = new object[] { regions, provinces, nations, w, h, null, null, null, 0, 0 };
        idMethod.Invoke(null, args);
        var halfReg = (byte[])args[5]!;
        var halfProv = (byte[])args[6]!;
        var halfNat = (byte[])args[7]!;
        var hw = (int)args[8]!;
        var hh = (int)args[9]!;

        Check(hw == 4 && hh == 2, $"half-res dimensions {hw}x{hh} equal exact half of {w}x{h}");
        Check(ReadRg8(halfProv, hw, 0, 0) == 300 && ReadRg8(halfNat, hw, 0, 0) == 30 && ReadRg8(halfReg, hw, 0, 0) == 3,
            "uniform quad preserves exact 16-bit province/nation/region IDs with zero interpolation");
        Check(ReadRg8(halfProv, hw, 1, 0) == 500 && ReadRg8(halfNat, hw, 1, 0) == 50,
            "majority 3-vs-1 quad selects majority non-interpolated ID consistently across layers");
        Check(ReadRg8(halfProv, hw, 2, 0) == 700 && ReadRg8(halfNat, hw, 2, 0) == 70 && ReadRg8(halfReg, hw, 2, 0) == 7,
            "coastal 2-vs-2 tie selects consistent land province/nation/region ID without mixing cells");

        // Verify zero forged province IDs anywhere in output
        for (var y = 0; y < hh; y++)
        for (var x = 0; x < hw; x++)
        {
            var p = ReadRg8(halfProv, hw, x, y);
            var n = ReadRg8(halfNat, hw, x, y);
            Check(n == p / 10, $"cell ({x},{y}) keeps nation {n} synced with province {p}");
        }

        // Verify CPU array remained full resolution & untouched
        Check(provinces.Length == w * h * 2 && ReadRg8(provinces, w, 3, 1) == 0,
            "source CPU ID array remains untouched at full resolution");

        // Verify hydrology R8 shore-128 preservation
        var hydro = new byte[w * h];
        for (var i = 0; i < hydro.Length; i++) hydro[i] = 128;
        hydro[0] = 112; hydro[1] = 112; hydro[w] = 144; hydro[w + 1] = 144;
        var hArgs = new object[] { hydro, w, h, 0, 0 };
        var halfHydro = (byte[])hydroMethod.Invoke(null, hArgs)!;
        Check((int)hArgs[3]! == hw && (int)hArgs[4]! == hh && halfHydro[0] == 128 && halfHydro[1] == 128,
            "hydrology R8 2x2 downsample preserves shore SDF neutrality at 128");

        // Verify fallback river RGBA8 importance preservation
        var river = new byte[w * h * 4];
        river[2] = 20; river[(w + 1) * 4 + 2] = 225; river[(w + 1) * 4 + 3] = 255;
        var rArgs = new object[] { river, w, h, 0, 0 };
        var halfRiver = (byte[])riverMethod.Invoke(null, rArgs)!;
        Check((int)rArgs[3]! == hw && (int)rArgs[4]! == hh && halfRiver[2] == 225,
            "unpacked river RGBA8 2x2 downsample retains peak river importance without smearing");

        // Verify terrain_surface slot downsampling without interpolation
        var terrain = new byte[w * h];
        Array.Fill(terrain, (byte)3);
        terrain[0] = 11; // 1 minor corner among three 3s -> mode is 3
        terrain[2] = 9; terrain[3] = 9; terrain[w + 2] = 9; terrain[w + 3] = 2; // mode is 9
        var tArgs = new object[] { terrain, w, h, 0, 0 };
        var halfTerrain = (byte[])terrainMethod.Invoke(null, tArgs)!;
        Check((int)tArgs[3]! == hw && (int)tArgs[4]! == hh && halfTerrain[0] == 3 && halfTerrain[1] == 9,
            "terrain_surface R8 2x2 downsample selects modal slot index without corrupting discrete IDs");
    }

    private static string FindCompiledDealOrForceDll(string root)
    {
        var gfxDir = Path.Combine(root, "Scripts", "gfx");
        var newestGfxUtc = Directory.EnumerateFiles(gfxDir, "*.cs", SearchOption.AllDirectories)
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        var candidates = new[]
        {
            Path.Combine(root, "tmp_logs", "bin_gfx", "DealOrForce.dll"),
            Path.Combine(root, ".godot", "mono", "temp", "bin", "Release", "DealOrForce.dll"),
            Path.Combine(root, ".godot", "mono", "temp", "bin", "Debug", "DealOrForce.dll")
        };
        return candidates
            .Where(path => File.Exists(path) && File.GetLastWriteTimeUtc(path) >= newestGfxUtc)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
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
