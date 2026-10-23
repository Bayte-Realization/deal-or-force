using System;
using System.Linq;
using DealOrForce.Simulation;

static class DiasporaProbe
{
    public static void Run()
    {
        Loc.ResetCatalog();
        var c = Campaign.CreateBootstrap(803);
        MapRasterCatalog.ResetCatalog();
        MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out _);
        c.AttachProvinceTopology(map);

        var gbr = c.NationByTag("GBR");
        foreach (var (culture, regionId) in new[] { ("scottish", "us_ca"), ("scottish", "gb_hld") })
        {
            var state = c.States.Find(s => s.Id == regionId);
            if (state is null) { Console.WriteLine($"DIASP {regionId}: no such state"); continue; }
            var owner = c.Nations.FirstOrDefault(n => n.MapIndex == state.NationMapIndex);
            var recipe = NationNaming.BaseFor(c, gbr, new[] { state.MapIndex }, culture, "auto");
            Console.WriteLine($"DIASP {culture}/{regionId} stateName='{state.Name}' owner={owner?.Tag} " +
                $"recipe={recipe.Kind}:{recipe.Id} " +
                $"land={(LandAtlas.BestFor(c, new[] { state.MapIndex }, gbr?.Tag ?? "")?.Id ?? "none")} " +
                $"nameFor='{SecessionEvents.NameFor(c, culture, regionId, state.Name, gbr)}'");
            var chosen = LandAtlas.Find(recipe.Id);
            if (chosen is not null)
                Console.WriteLine($"DIASP   land {chosen.Id}: regions=[{string.Join(",", chosen.RegionIds)}] " +
                    $"names=[{string.Join(",", chosen.RegionNames)}] macro={chosen.Macro} box={chosen.HasBox}");
            Console.WriteLine($"DIASP   state {state.Id} cores=[{string.Join(",", state.CoreTags)}] " +
                $"claims=[{string.Join(",", state.Claims)}] declares={NationNaming.DeclaresGround(chosen, c, new[] { state.MapIndex })} " +
                $"place='{NationNaming.StripAdministrativeSuffix(RegionNames.Resolve(state))}'");
            foreach (var n in c.Nations.Where(n => n.Movement is not null).Take(8))
                Console.WriteLine($"DIASP   movement {n.Tag} name='{CountryNames.Resolve(n, false)}' raw='{n.Name}'");
        }
    }
}
