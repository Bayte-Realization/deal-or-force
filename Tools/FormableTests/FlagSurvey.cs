using System;
using System.Linq;
using DealOrForce.Simulation;

static class FlagSurvey
{
    public static void Run()
    {
        Loc.ResetCatalog();
        SpriteCatalog.Load();
        var c = Campaign.CreateBootstrap(20261019);
        MapRasterCatalog.ResetCatalog();
        MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out _);
        c.AttachProvinceTopology(map);

        foreach (var tag in new[] { "SYR", "IRQ", "LBY", "SDN", "YEM", "AFG", "SOM", "MMR", "UKR", "SDN" })
        {
            var n = c.NationByTag(tag);
            if (n is null) { Console.WriteLine($"FLAG {tag}: absent"); continue; }
            var resolved = FlagCatalog.Resolve(c, n);
            Console.WriteLine($"FLAG {tag} gov={n.Government} ide={n.Ideology} -> {(resolved.Length > 0 ? resolved : "<none>")}");
        }

        // What the whole candidate chain looks like for Syria, so the answer is not a guess.
        var syr = c.NationByTag("SYR");
        if (syr is not null)
        {
            Console.WriteLine("FLAG SYR chain:");
            foreach (var name in FlagCatalog.Candidates(c, syr))
                Console.WriteLine($"   {name,-44} art={(SpriteCatalog.Has(name) ? "yes" : "-")}");
        }
    }
}
