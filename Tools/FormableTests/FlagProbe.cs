using System;
using System.Linq;
using DealOrForce.Simulation;

static class FlagProbe
{
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261019);
        MapRasterCatalog.ResetCatalog();
        MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out _);
        c.AttachProvinceTopology(map);

        foreach (var (parentTag, regionId) in new[]
                 {
                     ("RUS", "ru_irk"), ("RUS", "ru_ros"), ("GBR", "gb_hld"), ("USA", "us_ca"),
                     ("TUR", "tr_54"), ("MAR", "ma_09"), ("CHN", "cn_js"), ("PAK", "pk_pb"),
                     ("DEU", "de_bw"), ("FRA", "fr_26")
                 })
        {
            var parent = c.NationByTag(parentTag);
            var state = c.States.Find(s => s.Id == regionId);
            if (parent is null || state is null) continue;
            var born = CountryRelease.Release(c, parent, new CountryRelease.ReleaseOptions
            {
                StateMapIndices = new[] { state.MapIndex }.ToList(),
                AsSubject = false,
                Culture = EconomySystem.Cultures(state).OrderByDescending(p => p.Size).FirstOrDefault().Culture ?? "",
                Government = parent.Government,
                Ideology = parent.Ideology
            }, out _);
            if (born is null) continue;
            var name = CountryNames.Resolve(born, false);
            var flag = FlagCatalog.Resolve(c, born);
            var parentFlag = FlagCatalog.Resolve(c, parent);
            Console.WriteLine($"FLAG {parentTag}/{regionId,-8} {name,-34} flag={(flag.Length > 0 ? flag : "<none>")}" +
                $"  parent={(parentFlag.Length > 0 ? parentFlag : "<none>")}" +
                (flag == parentFlag ? "   SAME-AS-PARENT" : ""));
        }
    }
}
