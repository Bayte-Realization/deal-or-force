using System;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

internal static class ConflictAuditEntry
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--selftest") { LandArrangementTests.Run(); return 0; }
        if (args.Length < 2) { Console.WriteLine("ConflictAudit <save folder> <slot> [days] OR --seed <seed> [days]"); return 2; }
        // Bootstrap initialises the same authored world rasters as the normal SimBench entry.
        var c = Campaign.CreateBootstrap(args[0] == "--seed" ? int.Parse(args[1]) : 20260830);
        if (args[0] != "--seed") c = SaveSystem.Load(args[0], args[1], "").Campaign;
        if (c is null) return 2;
        MapRasterCatalog.ResetCatalog();
        HydrologyCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var map, out var message))
        { Console.WriteLine(message); return 2; }
        if (MapRasterCatalog.TryLoadHydrology(WorldMapSource.Width, WorldMapSource.Height, out var hydrology, out _))
            map.AttachHydrology(hydrology);
        c.AttachProvinceTopology(map);
        SpriteCatalog.Load();
        if (args.Length > 3 && args[3] == "--probe-republic") return ConflictIntegrityReport.ProbeRepublic(c);
        return ConflictIntegrityReport.Run(c, "RUS", "UKR", args.Length > 2 ? int.Parse(args[2]) : 0);
    }
}
