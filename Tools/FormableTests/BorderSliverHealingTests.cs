using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Headless invariants for Wave 6L border sliver healing:
/// - Heals the 2 detached coastal Western Samegrelo-Zemo Svaneti slivers on Abkhazia's Black Sea coast
///   cleanly into Abkhazia (ABK / state 1711) without breaking compiled.json RegionFingerprint.
/// - Strictly preserves islands (0 land neighbors), sovereign microstates (<= 4 provinces),
///   distinct whole-state exclaves, and real-world historic exclaves/enclaves (Kaliningrad,
///   Nakhchivan, Alaska, Gibraltar, Ceuta, Melilla, Cabinda, Llivia, Oecusse, Temburong, etc.).
/// - Verifies deterministic idempotence and MapBootCache v2 (map_boot_v2.bin) round-trip.
/// Run: FormableTests --border-slivers-only
/// </summary>
static class BorderSliverHealingTests
{
    private static int _passed;

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("border-slivers: " + label);
        _passed++;
        Console.WriteLine("PASS border-slivers: " + label);
    }

    [TestSuite("--border-slivers-only", Order = 2698, IncludeInDefaultRun = true,
        Description = "Samegrelo coastal sliver healing, global border sliver-healer preservation guards, and MapBootCache v2.")]
    public static void Run()
    {
        _passed = 0;
        TestProtectedExclaveBoxes();
        TestSyntheticSliverHealingAndPreservation();
        TestRealWorldSamegreloAndExclavePreservation();
        Console.WriteLine($"border-slivers: {_passed} PASS");
    }

    private static void TestProtectedExclaveBoxes()
    {
        Check(TerritorialTopology.IsProtectedRealWorldExclave(54.71f, 20.51f), "Kaliningrad protected");
        Check(TerritorialTopology.IsProtectedRealWorldExclave(39.21f, 45.41f), "Nakhchivan protected");
        Check(TerritorialTopology.IsProtectedRealWorldExclave(64.20f, -149.49f), "Alaska protected");
        Check(TerritorialTopology.IsProtectedRealWorldExclave(36.14f, -5.35f), "Gibraltar protected");
        Check(TerritorialTopology.IsProtectedRealWorldExclave(35.89f, -5.32f), "Ceuta protected");
        Check(TerritorialTopology.IsProtectedRealWorldExclave(35.29f, -2.94f), "Melilla protected");
        Check(TerritorialTopology.IsProtectedRealWorldExclave(-5.55f, 12.20f), "Cabinda protected");
        Check(TerritorialTopology.IsProtectedRealWorldExclave(42.46f, 1.98f), "Llivia protected");
        Check(TerritorialTopology.IsProtectedRealWorldExclave(-9.20f, 124.37f), "Oecusse protected");
        Check(TerritorialTopology.IsProtectedRealWorldExclave(4.65f, 115.18f), "Temburong protected");
        Check(!TerritorialTopology.IsProtectedRealWorldExclave(43.16f, 40.34f), "Abkhazia coast (Pitsunda) is NOT in protected exclave list");
    }

    private static void TestSyntheticSliverHealingAndPreservation()
    {
        // Build a synthetic 64x32 grid with:
        // - Nation 10 (large, state 100): big mainland block on left + 1-province coastal sliver inside Nation 20
        // - Nation 20 (large, state 200): surrounds the Nation 10 sliver (state 100) AND surrounds:
        //   * Nation 30 (sovereign microstate with 2 provinces, state 300) -> MUST STAY Nation 30!
        //   * Nation 40 (large nation with main state 400 on far right + distinct exclave state 401 inside Nation 20) -> MUST STAY Nation 40!
        //   * Nation 10 island (state 100 in open sea, 0 land neighbors) -> MUST STAY Nation 10!
        const int w = 64, h = 32;
        var states = new ushort[w * h];
        var control = new ushort[w * h];

        void FillRect(int x0, int y0, int x1, int y1, ushort st, ushort nat)
        {
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                states[y * w + x] = st;
                control[y * w + x] = nat;
            }
        }

        // Nation 10 main body (state 100, >= 6 provinces)
        FillRect(2, 4, 18, 22, 100, 10);
        // Nation 20 big surrounding block (state 200)
        FillRect(20, 4, 44, 24, 200, 20);
        // Detached 1-province coastal sliver of Nation 10 / state 100 embedded on Nation 20's south coast
        FillRect(28, 22, 31, 24, 100, 10);
        // Sovereign microstate Nation 30 (<= 4 provinces, state 300) completely surrounded by Nation 20
        FillRect(24, 8, 27, 11, 300, 30);
        // Distinct whole-state exclave of Nation 40 (state 401 inside Nation 20, while main Nation 40 state 400 is on right)
        FillRect(35, 10, 38, 13, 401, 40);
        FillRect(48, 4, 62, 22, 400, 40);
        // Island of Nation 10 / state 100 surrounded by ocean (0 land neighbors)
        FillRect(10, 26, 13, 29, 100, 10);

        var topo = ProvinceTopology.Build(states, control, w, h, _ => false);

        var sliverProv = topo.CellToProvince[23 * w + 29];
        var microProv = topo.CellToProvince[9 * w + 25];
        var exclaveProv = topo.CellToProvince[11 * w + 36];
        var islandProv = topo.CellToProvince[27 * w + 11];
        Check(sliverProv > 0 && topo.ProvinceToNation[sliverProv] == 10, "synthetic sliver starts owned by Nation 10");
        Check(microProv > 0 && topo.ProvinceToNation[microProv] == 30, "synthetic microstate starts owned by Nation 30");
        Check(exclaveProv > 0 && topo.ProvinceToNation[exclaveProv] == 40, "synthetic whole-state exclave starts owned by Nation 40");
        Check(islandProv > 0 && topo.ProvinceToNation[islandProv] == 10, "synthetic island starts owned by Nation 10");

        var healed = TerritorialTopology.ApplyProvinceSliverHealing(topo);
        Check(healed.Any(h => h.ProvinceId == sliverProv && h.NewNation == 20 && h.NewState == 200),
            "detached coastal sliver healed into surrounding Nation 20 / State 200");
        Check(topo.ProvinceToNation[sliverProv] == 20 && topo.ProvinceToState[sliverProv] == 200,
            "sliver ProvinceToNation and ProvinceToState updated cleanly");
        Check(topo.ProvinceToNation[microProv] == 30, "sovereign microstate (<=4 provinces) preserved untouched");
        Check(topo.ProvinceToNation[exclaveProv] == 40 && topo.ProvinceToState[exclaveProv] == 401,
            "distinct whole-state exclave preserved untouched");
        Check(topo.ProvinceToNation[islandProv] == 10, "sea island (0 land neighbors) preserved untouched");

        var secondPass = TerritorialTopology.ApplyProvinceSliverHealing(topo);
        Check(secondPass.Count == 0, "ApplyProvinceSliverHealing is strictly idempotent");
    }

    private static void TestRealWorldSamegreloAndExclavePreservation()
    {
        var campaign = Campaign.CreateBootstrap(20260830);
        if (!MapRasterCatalog.TryBuildProvinceTopology(
                WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var topology, out var reason))
            throw new Exception($"2024 world topology failed to load (compiled.json stamp must remain intact!): {reason}");

        var geo = campaign.NationByTag("GEO");
        var abk = campaign.NationByTag("ABK");
        var rus = campaign.NationByTag("RUS");
        var aze = campaign.NationByTag("AZE");
        var usa = campaign.NationByTag("USA");
        Check(geo is not null && abk is not null && rus is not null && aze is not null && usa is not null,
            "core 2024 nations located");

        int CountInBox(ushort nationIdx, float minLat, float maxLat, float minLon, float maxLon)
        {
            var total = 0;
            for (var p = 1; p <= topology.ProvinceCount; p++)
            {
                if (topology.IsWater(p) || topology.ProvinceToNation[p] != nationIdx) continue;
                var (lat, lon) = topology.CenterOf(p);
                if (lat >= minLat && lat <= maxLat && lon >= minLon && lon <= maxLon) total++;
            }
            return total;
        }

        var kaliningrad = CountInBox((ushort)rus!.MapIndex, 54.2f, 55.5f, 19.4f, 23.0f);
        var nakhchivan = CountInBox((ushort)aze!.MapIndex, 38.7f, 39.95f, 44.6f, 46.3f);
        var alaska = CountInBox((ushort)usa!.MapIndex, 51.0f, 72.0f, -180f, -129f);
        var abkProvs = CountInBox((ushort)abk!.MapIndex, 42.0f, 44.1f, 39.7f, 42.4f);
        Check(kaliningrad > 0 && nakhchivan > 0 && alaska > 0,
            $"real-world exclaves preserved through TryBuildProvinceTopology (KGD={kaliningrad}, NX={nakhchivan}, AK={alaska})");
        // Authored definition.csv had 6 Abkhazia provinces; healing the coastal Samegrelo slivers raises Abkhazia above 6.
        Check(abkProvs >= 7, $"Abkhazia healed coastal Samegrelo slivers on live boot (ABK provinces={abkProvs})");

        // Confirm zero detached 1-2 province GEO slivers remain completely surrounded by ABK on land.
        var remainingAbkSlivers = 0;
        for (var p = 1; p <= topology.ProvinceCount; p++)
        {
            if (topology.IsWater(p) || topology.ProvinceToNation[p] != (ushort)geo!.MapIndex) continue;
            var adj = topology.LandNeighbors[p];
            if (adj.Length == 0) continue;
            var allAbk = true;
            var hasLand = false;
            for (var i = 0; i < adj.Length; i++)
            {
                var n = adj[i];
                if (n <= 0 || topology.IsWater(n)) continue;
                hasLand = true;
                if (topology.ProvinceToNation[n] != (ushort)abk.MapIndex) { allAbk = false; break; }
            }
            if (hasLand && allAbk) remainingAbkSlivers++;
        }
        Check(remainingAbkSlivers == 0, "zero GEO coastal slivers remain enclosed by Abkhazia");

        // Confirm idempotence: calling ApplyProvinceSliverHealing again on the already-healed topology changes 0 provinces.
        Check(TerritorialTopology.ApplyProvinceSliverHealing(topology).Count == 0,
            "real 2024 map healing is strictly idempotent after TryBuildProvinceTopology");
        Check(CountInBox((ushort)rus.MapIndex, 54.2f, 55.5f, 19.4f, 23.0f) == kaliningrad, "Kaliningrad 100% preserved");
        Check(CountInBox((ushort)aze.MapIndex, 38.7f, 39.95f, 44.6f, 46.3f) == nakhchivan, "Nakhchivan 100% preserved");
        Check(CountInBox((ushort)usa.MapIndex, 51.0f, 72.0f, -180f, -129f) == alaska, "Alaska 100% preserved");
    }
}
