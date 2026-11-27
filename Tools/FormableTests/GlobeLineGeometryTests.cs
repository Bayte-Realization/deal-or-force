using System;
using System.Collections.Generic;
using System.IO;
using DealOrForce;
using DealOrForce.Simulation;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

/// <summary>
/// Deterministic invariant tests for globe and flat-map line geometry builders:
/// province/endpoint bounds, Null-Island fallbacks, NaN/Inf clamping, pole Asin/cross safety,
/// degenerate tangent/foldback fallbacks, antipodal sphere arcs, antimeridian cuts, near-plane
/// screen ribbon culling, and vertex/segment sanity gates.
/// </summary>
public static class GlobeLineGeometryTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("globe line geometry: " + label);
        Console.WriteLine("PASS globe line geometry: " + label);
    }

    [TestSuite("--globe-line-geometry-only", IncludeInDefaultRun = true,
        Description = "Validates line/ribbon/plan/battle/supply geometry guards against infinity spikes")]
    public static void Run()
    {
        InvalidProvinceAndNullIslandRejected();
        NanAndInfinityClampedCleanly();
        DegenerateTangentsFoldbacksAndPolesSafe();
        AntipodalSphereArcsStayOnSurface();
        AntimeridianAndOversizedJumpsSplitRuns();
        EmittedVertexEdgeAndNearPlaneGatesEnforced();
        TradeTrafficScaleGuardPreserved();
        AdversarialStressSweepPreservesSurfaceAndFingerprint();
    }

    private static void InvalidProvinceAndNullIslandRejected()
    {
        int[] centerCells = { -1, 120, -1, 450, 900, 0 };
        (float Latitude, float Longitude) CenterOf(int id)
        {
            if (id <= 0 || id >= centerCells.Length || centerCells[id] <= 0) return (0f, 0f);
            return id switch
            {
                1 => (41.3f, 69.2f),   // Tashkent-like Central Asia
                3 => (38.5f, 68.8f),   // Dushanbe-like
                4 => (37.5f, 71.5f),   // Khorog-like
                _ => (0f, 0f)
            };
        }

        Check(!GlobeLineSanity.TryValidateProvinceId(0, 5, centerCells),
            "province 0 is rejected before CenterOf");
        Check(!GlobeLineSanity.TryValidateProvinceId(-12, 5, centerCells),
            "negative province id is rejected");
        Check(!GlobeLineSanity.TryValidateProvinceId(6, 5, centerCells),
            "out-of-range province id > ProvinceCount is rejected");
        Check(!GlobeLineSanity.TryValidateProvinceId(2, 5, centerCells),
            "province with uninitialized negative center cell is rejected");
        Check(!GlobeLineSanity.TryValidateProvinceId(5, 5, centerCells),
            "province with zero-initialized center cell 0 (pixel (0,0) North Pole) is rejected");
        Check(GlobeLineSanity.TryValidateProvinceId(1, 5, centerCells),
            "valid province with positive center cell is accepted");

        Check(GlobeLineSanity.TryResolveEntityGeo(0f, 0f, 1, 5, centerCells, CenterOf, out var fallbackFromNullIsland) &&
              MathF.Abs(fallbackFromNullIsland.Latitude - 41.3f) < 0.01f &&
              MathF.Abs(fallbackFromNullIsland.Longitude - 69.2f) < 0.01f,
            "uninitialized (0,0) entity with valid province falls back to province center instead of Null Island");

        Check(GlobeLineSanity.TryResolveEntityGeo(float.NaN, float.PositiveInfinity, 3, 5, centerCells, CenterOf, out var fallbackFromNan) &&
              MathF.Abs(fallbackFromNan.Latitude - 38.5f) < 0.01f,
            "non-finite entity coords with valid province fall back cleanly");

        Check(!GlobeLineSanity.TryResolveEntityGeo(0f, 0f, 0, 5, centerCells, CenterOf, out _),
            "(0,0) entity with invalid province is rejected");
        Check(!GlobeLineSanity.TryResolveEntityGeo(0f, 0f, 5, 5, centerCells, CenterOf, out _),
            "(0,0) entity backed only by unassigned center cell 0 is rejected");
        Check(!GlobeLineSanity.TryResolveEntityGeo(float.NaN, 10f, 99, 5, centerCells, CenterOf, out _),
            "non-finite coords with out-of-range province are rejected");
    }

    private static void NanAndInfinityClampedCleanly()
    {
        Check(GlobeLineSanity.ClampUnit(float.NaN) == 0f, "ClampUnit converts NaN to 0");
        Check(GlobeLineSanity.ClampUnit(1.000001f) == 1f, "ClampUnit clamps > 1f for safe Asin");
        Check(GlobeLineSanity.ClampUnit(-1.000001f) == -1f, "ClampUnit clamps < -1f for safe Asin");
        Check(GlobeLineSanity.SafeClamp(float.NaN, 0.36f, 1.08f, 0.72f) == 0.72f,
            "SafeClamp replaces NaN instead of propagating NaN like Math.Clamp");
        Check(GlobeLineSanity.SafeClamp(float.PositiveInfinity, 0.36f, 1.08f, 0.72f) == 0.72f,
            "SafeClamp replaces positive infinity with fallback");
        Check(GlobeLineSanity.SafeClamp(99f, 0.36f, 1.08f, 0.72f) == 1.08f,
            "SafeClamp bounds finite overshoot to max");

        var poleGeo = GlobeLineSanity.GeographicFromNormalSafe(new SysVec3(0f, 1.00001f, 0f));
        Check(float.IsFinite(poleGeo.Latitude) && float.IsFinite(poleGeo.Longitude) &&
              MathF.Abs(poleGeo.Latitude - 90f) < 0.001f,
            "near-pole float overshoot cannot poison GeographicFromNormal with Asin(NaN)");

        var zeroGeo = GlobeLineSanity.GeographicFromNormalSafe(SysVec3.Zero);
        var nanGeo = GlobeLineSanity.GeographicFromNormalSafe(new SysVec3(float.NaN, 1f, 0f));
        Check(float.IsFinite(zeroGeo.Latitude) && float.IsFinite(zeroGeo.Longitude) &&
              float.IsFinite(nanGeo.Latitude) && float.IsFinite(nanGeo.Longitude),
            "zero or NaN normal never yields non-finite geographic coords");

        Check(GlobeLineSanity.WrapDeltaDegrees(float.NaN) == 0f &&
              GlobeLineSanity.WrapDeltaDegrees(float.PositiveInfinity) == 0f &&
              GlobeLineSanity.WrapDeltaDegrees(float.NegativeInfinity) == 0f,
            "non-finite longitude deltas return immediately without hanging wrap loops");
        Check(MathF.Abs(GlobeLineSanity.WrapDeltaDegrees(350f) + 10f) < 1e-4f &&
              MathF.Abs(GlobeLineSanity.WrapDeltaDegrees(-350f) - 10f) < 1e-4f &&
              MathF.Abs(GlobeLineSanity.WrapDeltaDegrees(180f) - 180f) < 1e-4f,
            "large longitude deltas normalize cleanly into [-180, 180]");
    }

    private static void DegenerateTangentsFoldbacksAndPolesSafe()
    {
        var surfacePt = SysVec3.Normalize(new SysVec3(1f, 1f, 1f)) * 5.006f;
        Check(GlobeLineSanity.TrySafeRibbonFrame(surfacePt, surfacePt, surfacePt,
                  flatMap: false, flatPositiveZNormal: false, out var norm, out var tan, out var side) &&
              GlobeLineSanity.IsFinite(norm) && GlobeLineSanity.IsFinite(tan) && GlobeLineSanity.IsFinite(side) &&
              MathF.Abs(side.Length() - 1f) < 1e-4f && MathF.Abs(SysVec3.Dot(norm, side)) < 1e-4f,
            "duplicate points produce a unit orthogonal ribbon frame instead of zero side vector");

        var ahead = SysVec3.Normalize(surfacePt + new SysVec3(0.1f, -0.05f, 0.02f)) * 5.006f;
        Check(GlobeLineSanity.TrySafeRibbonFrame(surfacePt, ahead, ahead,
                  flatMap: false, flatPositiveZNormal: false, out _, out tan, out side) &&
              MathF.Abs(tan.Length() - 1f) < 1e-4f && MathF.Abs(side.Length() - 1f) < 1e-4f,
            "180-degree A->B->A foldback falls back to segment tangent cleanly");

        var northPole = new SysVec3(0f, 5.006f, 0f);
        Check(GlobeLineSanity.TrySafeRibbonFrame(northPole, northPole, northPole,
                  flatMap: false, flatPositiveZNormal: false, out norm, out tan, out side) &&
              MathF.Abs(side.Length() - 1f) < 1e-4f && MathF.Abs(SysVec3.Dot(norm, side)) < 1e-4f,
            "exact pole point cross product uses orthogonal fallback axis instead of zero");

        Check(GlobeLineSanity.TrySafeRibbonFrame(surfacePt, surfacePt * 0.9f, surfacePt * 1.1f,
                  flatMap: false, flatPositiveZNormal: false, out _, out _, out side) &&
              MathF.Abs(side.Length() - 1f) < 1e-4f,
            "purely radial tangent delta orthogonalizes instead of vanishing in Cross");

        Check(!GlobeLineSanity.TrySafeRibbonFrame(SysVec3.Zero, ahead, ahead,
                  flatMap: false, flatPositiveZNormal: false, out _, out _, out _),
            "globe origin (0,0,0) fails frame generation instead of emitting center spike");
    }

    private static void AntipodalSphereArcsStayOnSurface()
    {
        var a = new SysVec3(5.006f, 0f, 0f);
        var b = new SysVec3(-5.006f, 0f, 0f);
        Check(GlobeLineSanity.TrySafeSphereQuadMidpoint(a, b, a, 5.006f, out var mid) &&
              GlobeLineSanity.IsFinite(mid) &&
              MathF.Abs(mid.Length() - 5.006f) < 0.002f,
            "antipodal offensive Bezier midpoint stays on globe surface instead of plunging to (0,0,0)");

        for (var i = 0; i <= 16; i++)
        {
            var pt = GlobeLineSanity.InterpolateSurfacePoint(a, b, i / 16f, flatMap: false);
            Check(GlobeLineSanity.IsFinite(pt) && MathF.Abs(pt.Length() - 5.006f) < 0.01f,
                $"antipodal interpolation step {i} stays on globe radius");
        }

        var c = new SysVec3(0f, 5.006f, 0f);
        var quarter = GlobeLineSanity.InterpolateSurfacePoint(a, c, 0.5f, flatMap: false);
        Check(MathF.Abs(quarter.Length() - 5.006f) < 0.01f,
            "90-degree spherical interpolation stays on surface without chord sag into Earth interior");
    }

    private static void AntimeridianAndOversizedJumpsSplitRuns()
    {
        var runs = new List<DateLineSeam.Run>();

        // Bug repro 1: 2-point flat segment crossing the ±180 date line (previously missed by PlanStroke loop i=1..n-2).
        SysVec3[] twoPointWrap =
        {
            new(-9.95f, 2.1f, 0.004f),
            new(9.95f, 2.1f, 0.004f)
        };
        GlobeLineSanity.CollectLineRuns(twoPointWrap, flatMap: true, GlobeLineSanity.FlatMapWidth,
            GlobeLineSanity.MaxFlatSegmentLength, runs);
        Check(runs.Count == 0, "two-point date-line crossing emits zero bridging runs");

        // Bug repro 2: jump at final segment points[^2] -> points[^1] (previously unguarded in PlanStroke).
        SysVec3[] tailWrap =
        {
            new(-9.70f, 2.0f, 0.004f),
            new(-9.85f, 2.05f, 0.004f),
            new(9.95f, 2.10f, 0.004f)
        };
        GlobeLineSanity.CollectLineRuns(tailWrap, flatMap: true, GlobeLineSanity.FlatMapWidth,
            GlobeLineSanity.MaxFlatSegmentLength, runs);
        Check(runs.Count == 1 && runs[0] == new DateLineSeam.Run(0, 2),
            "final-segment date-line jump splits cleanly before the last vertex");

        // Rebased adjacent copy stays one continuous short run.
        var rebasedX = DateLineSeam.NearestCopy(9.95f, -9.85f, GlobeLineSanity.FlatMapWidth);
        SysVec3[] rebased =
        {
            new(-9.70f, 2.0f, 0.004f),
            new(-9.85f, 2.05f, 0.004f),
            new(rebasedX, 2.10f, 0.004f)
        };
        GlobeLineSanity.CollectLineRuns(rebased, flatMap: true, GlobeLineSanity.FlatMapWidth,
            GlobeLineSanity.MaxFlatSegmentLength, runs, refFlatX: -9.8f);
        Check(runs.Count == 1 && runs[0] == new DateLineSeam.Run(0, 3),
            "date-line neighbor rebased via NearestCopy forms a single continuous short run");

        // Globe teleport / Earth-center / NaN split cleanly.
        SysVec3[] brokenGlobe =
        {
            new(5.006f, 0f, 0f),
            SysVec3.Normalize(new SysVec3(5f, 0.3f, 0f)) * 5.006f,
            SysVec3.Zero,
            new(0f, 5.006f, 0f),
            SysVec3.Normalize(new SysVec3(0.2f, 5f, 0f)) * 5.006f
        };
        GlobeLineSanity.CollectLineRuns(brokenGlobe, flatMap: false, GlobeLineSanity.FlatMapWidth,
            GlobeLineSanity.MaxGlobeSegmentLength, runs);
        Check(runs.Count == 2 &&
              runs[0] == new DateLineSeam.Run(0, 2) &&
              runs[1] == new DateLineSeam.Run(3, 2),
            "invalid globe origin splits around bad vertex without bridging segments");
    }

    private static void EmittedVertexEdgeAndNearPlaneGatesEnforced()
    {
        Check(!GlobeLineSanity.IsValidEmittedVertex(SysVec3.Zero, flatMap: false),
            "globe vertex at origin is rejected");
        Check(!GlobeLineSanity.IsValidEmittedVertex(new SysVec3(100f, 0f, 0f), flatMap: false),
            "globe vertex blown out past MaxGlobeVertexRadius is rejected");
        Check(!GlobeLineSanity.IsValidEmittedVertex(new SysVec3(float.NaN, 0f, 5f), flatMap: false),
            "NaN globe vertex is rejected");
        Check(GlobeLineSanity.IsValidEmittedVertex(new SysVec3(5.006f, 0f, 0f), flatMap: false),
            "nominal surface globe vertex is accepted");

        Check(!GlobeLineSanity.IsValidEmittedVertex(new SysVec3(120f, 0f, 0.004f), flatMap: true, refFlatX: 0f),
            "flat vertex far outside sheet bounds is rejected");
        Check(!GlobeLineSanity.IsValidEmittedVertex(new SysVec3(0f, float.PositiveInfinity, 0.004f), flatMap: true),
            "flat vertex with infinite Y is rejected");
        Check(GlobeLineSanity.IsValidEmittedVertex(new SysVec3(-10.1f, 3.2f, 0.004f), flatMap: true, refFlatX: -9.8f),
            "flat wrapped neighbor near active camera sheet is accepted");

        var g1 = new SysVec3(5.006f, 0f, 0f);
        var g2 = new SysVec3(-5.006f, 0f, 0f);
        Check(!GlobeLineSanity.IsValidEmittedEdge(g1, g2, flatMap: false),
            "cross-planet triangle edge is rejected");
        var gNear = SysVec3.Normalize(new SysVec3(5f, 0.2f, 0f)) * 5.006f;
        Check(GlobeLineSanity.IsValidEmittedEdge(g1, gNear, flatMap: false),
            "local surface triangle edge passes edge gate");

        Check(MathF.Abs(GlobeLineSanity.MaxBattleArrowChordGlobe - GlobeLineSanity.GlobeRadius * 0.28f) < 1e-6f &&
              MathF.Abs(GlobeLineSanity.MaxBattleArrowChordFlat - GlobeLineSanity.FlatMapWidth * 0.08f) < 1e-6f,
            "tightened battle arrow chord caps prevent cross-continental spans");
        var gDistant = SysVec3.Normalize(new SysVec3(3.5f, 3.5f, 0f)) * 5.006f;
        Check(!GlobeLineSanity.IsValidEmittedEdge(g1, gDistant, flatMap: false,
                  maxSegmentLength: GlobeLineSanity.MaxBattleArrowChordGlobe),
            "excessive multi-continent battle chord exceeds tightened globe battle arrow cap");

        var flatBattle = new SysVec3(-9.9f, 2.0f, 0.004f);
        var flatOriginRaw = new SysVec3(9.9f, 2.05f, 0.004f);
        var flatOriginRebased = new SysVec3(
            DateLineSeam.NearestCopy(flatOriginRaw.X, flatBattle.X, GlobeLineSanity.FlatMapWidth),
            flatOriginRaw.Y, flatOriginRaw.Z);
        Check(GlobeLineSanity.IsValidEmittedEdge(flatOriginRebased, flatBattle, flatMap: true,
                  refFlatX: -9.9f, maxSegmentLength: GlobeLineSanity.MaxBattleArrowChordFlat),
            "date-line battle arrow rebased via NearestCopy fits tightened flat arrow cap");
        Check(!GlobeLineSanity.IsValidEmittedEdge(new SysVec3(-7.0f, 2.0f, 0.004f), flatBattle, flatMap: true,
                  refFlatX: -9.9f, maxSegmentLength: GlobeLineSanity.MaxBattleArrowChordFlat),
            "long cross-region flat chord is rejected by tightened flat battle arrow cap");

        Check(!GlobeLineSanity.IsValidScreenRibbonSegment(
                  new SysVec2(100f, 100f), new SysVec2(140f, 120f),
                  depthA: 0.01f, depthB: 2.0f, minDepth: 0.05f, maxScreenSpan: 2400f),
            "segment crossing inside camera near-plane is culled before Unproject/Project blowup");
        Check(!GlobeLineSanity.IsValidScreenRibbonSegment(
                  new SysVec2(0f, 0f), new SysVec2(25000f, 25000f),
                  depthA: 1.0f, depthB: 1.0f, minDepth: 0.05f, maxScreenSpan: 2400f),
            "near-horizon screen span explosion is rejected");
        Check(GlobeLineSanity.IsValidScreenRibbonSegment(
                  new SysVec2(400f, 300f), new SysVec2(440f, 320f),
                  depthA: 1.5f, depthB: 1.6f, minDepth: 0.05f, maxScreenSpan: 2400f),
            "healthy screen-projected support ribbon segment passes gate");
    }

    private static void AdversarialStressSweepPreservesSurfaceAndFingerprint()
    {
        var campaign = new Campaign(20261126);
        campaign.Nations.Add(new Nation { Id = 0, Tag = "TST", Name = "Test" });
        campaign.ConfigureSinglePlayer(0);
        var before = CampaignFingerprint.Of(campaign);

        var rng = new Random(1337);
        var runs = new List<DateLineSeam.Run>();
        var samples = new SysVec3[64];
        for (var trial = 0; trial < 100; trial++)
        {
            var flat = (trial & 1) == 1;
            var refX = flat ? (rng.NextSingle() - 0.5f) * 20f : 0f;
            for (var i = 0; i < samples.Length; i++)
            {
                var mode = rng.Next(12);
                samples[i] = mode switch
                {
                    0 => SysVec3.Zero,
                    1 => new SysVec3(float.NaN, 1f, 2f),
                    2 => new SysVec3(float.PositiveInfinity, 0f, 0f),
                    3 => flat ? new SysVec3(refX + 80f, 0f, 0f) : new SysVec3(40f, 0f, 0f),
                    _ => flat
                        ? new SysVec3(refX + (rng.NextSingle() - 0.5f) * 6f, (rng.NextSingle() - 0.5f) * 8f, 0.004f)
                        : SysVec3.Normalize(new SysVec3(
                            rng.NextSingle() - 0.5f,
                            rng.NextSingle() - 0.5f,
                            rng.NextSingle() - 0.5f + 1e-4f)) * 5.006f
                };
            }

            GlobeLineSanity.CollectLineRuns(samples, flat, GlobeLineSanity.FlatMapWidth,
                flat ? GlobeLineSanity.MaxFlatSegmentLength : GlobeLineSanity.MaxGlobeSegmentLength,
                runs, refX);

            foreach (var run in runs)
            {
                for (var idx = run.Start + 1; idx < run.Start + run.Count; idx++)
                {
                    var a = samples[idx - 1];
                    var b = samples[idx];
                    Check(GlobeLineSanity.IsValidEmittedEdge(a, b, flat, refX),
                        $"stress trial {trial} emitted edge {idx} stays finite, bounded, and on-surface");
                    Check(GlobeLineSanity.TrySafeRibbonFrame(b, a, samples[Math.Min(idx + 1, run.Start + run.Count - 1)],
                              flat, flatPositiveZNormal: false, out _, out _, out var side) &&
                          GlobeLineSanity.IsFinite(side),
                        $"stress trial {trial} ribbon frame {idx} succeeds cleanly");
                }
            }
        }

        Check(CampaignFingerprint.Of(campaign) == before,
            "running geometry sanity sweep does not mutate CampaignFingerprint");
    }

    private static void TradeTrafficScaleGuardPreserved()
    {
        var zoomedOutShipScale = GlobeLineSanity.SafeClamp(0.03f / 300f * 0.45f, 1e-5f, 0.05f, 0.03f / 300f);
        Check(MathF.Abs(zoomedOutShipScale - 4.5e-5f) < 1e-7f && zoomedOutShipScale < 0.0001f,
            "zoomed-out container ship scale stays at 4.5e-5f (< 0.0001f) instead of clamping up to 0.001f");

        var zoomedInShipScale = GlobeLineSanity.SafeClamp(0.03f / 300f * 2f, 1e-5f, 0.05f, 0.03f / 300f);
        Check(MathF.Abs(zoomedInShipScale - 2e-4f) < 1e-7f,
            "zoomed-in ship scale clamps cleanly within tiny world bounds");

        var fallbackShipScale = GlobeLineSanity.SafeClamp(float.NaN, 1e-5f, 0.05f, 0.03f / 300f);
        Check(MathF.Abs(fallbackShipScale - 0.0001f) < 1e-7f,
            "non-finite ship scale falls back to ShipDisplayLength / 300f");

        var tradeTrafficPath = FindRepoFile("Scripts", "gfx", "GlobeTradeTraffic.cs");
        Check(!string.IsNullOrEmpty(tradeTrafficPath) && File.Exists(tradeTrafficPath),
            "located Scripts/gfx/GlobeTradeTraffic.cs for mover scale contract check");
        var source = File.ReadAllText(tradeTrafficPath);
        Check(!source.Contains("SafeClamp((model.Ship ? ShipDisplayLength / 300f : LorryDisplayLength / 16f) * zoom, 0.001f",
                  StringComparison.Ordinal),
            "GlobeTradeTraffic.cs does not clamp trade mover scale up to 0.001f");
        Check(source.Contains("GlobeLineSanity.SafeClamp((model.Ship ? ShipDisplayLength / 300f : LorryDisplayLength / 16f) * zoom, 1e-5f, 0.05f, ShipDisplayLength / 300f)",
                  StringComparison.Ordinal),
            "GlobeTradeTraffic.cs uses expected mover scale bounds (1e-5f..0.05f)");
    }

    private static string FindRepoFile(params string[] relativeParts)
    {
        string[] roots = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in roots)
        {
            var dir = start;
            for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(dir); depth++)
            {
                if (File.Exists(Path.Combine(dir, "DealOrForce.csproj")))
                {
                    var parts = new string[1 + relativeParts.Length];
                    parts[0] = dir;
                    Array.Copy(relativeParts, 0, parts, 1, relativeParts.Length);
                    var candidate = Path.Combine(parts);
                    if (File.Exists(candidate)) return candidate;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }
        }
        return "";
    }
}
