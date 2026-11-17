using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A country back on the map is not fallen (MovementFall.ReturnTheLanded).
///
/// The owner's 107-year campaign had 41 countries - Syria, France, Congo, Pakistan, Uganda, Libya,
/// Namibia and 34 more - holding regions with people in them while still on the fallen list, and
/// the economy emptied every one of them each week: 360 countries with ground, 320 with people.
/// The weekly review puts a country on the list when it holds no region; only four restoration
/// paths took it off, and every other road back to the map - a status quo peace that leaves a
/// fallen country's surviving divisions what they hold, a coalition handing an overrun ally its
/// land, a civil war returning ground to its parent - goes through StatePartition.Annex, which did
/// not know the list existed. Pinned here on a fresh world, on the real fall and the real transfer,
/// and on an old save that carries the bad state.
/// </summary>
static class FallenGroundTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("fallen ground: " + note); Console.WriteLine("PASS fallen ground: " + note); }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, day * 24L);

    static Campaign World(int seed)
    {
        var c = Campaign.CreateBootstrap(seed);
        MapRasterCatalog.ResetCatalog(); TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        // Its government stays home: this is the country the fall leaves without a movement,
        // the one no restoration path was written for.
        c.Defines.MovementExileBase = -10f;
        c.Defines.MovementUndergroundBase = -10f;
        return c;
    }

    static int[] ProvincesOf(Campaign c, Nation nation)
    {
        var map = c.ProvinceMap;
        return Enumerable.Range(1, map.ProvinceCount)
            .Where(p => map.ProvinceToNation[p] == nation.MapIndex && !map.IsWater(p)).ToArray();
    }

    public static void Run()
    {
        var c = World(20261117);
        var small = c.NationByTag("LUX");
        var taker = c.NationByTag("BEL");
        Check(small is not null && taker is not null && small.Movement is null && c.StatesOf(small).Length > 0,
            "Luxembourg and Belgium stand on the 2024 map");
        var home = ProvincesOf(c, small);
        var firstRegion = c.StatesOf(small).OrderBy(s => s.MapIndex).First().MapIndex;
        var giveBack = home.Where(p => c.ProvinceMap.ProvinceToState[p] == firstRegion).ToArray();
        Check(giveBack.Length > 0, "the ground to give back is a whole region of its own");
        var gdp2024 = small.Gdp;
        var people2024 = small.Population;

        // Overrun: every province goes, and the weekly review rules the fall.
        StatePartition.Annex(c, home, taker, asConquest: true);
        Check(c.StatesOf(small).Length == 0, "Luxembourg holds no region");
        MovementFall.Decide(c);
        Check(c.Fallen.Contains(small.Id) && small.Movement is null,
            "the weekly review puts it on the fallen list, with no government abroad");
        // What the fall does to the rest of it: the books closed at zero by the conquest that took
        // the last province, and the seat taken away by the capital watch.
        for (var tick = 0; tick < 4; tick++) CapitalCore.Tick(c);
        Check(small.Gdp == 0f && small.CapitalProvinceId == 0 && small.CapitalStateMapIndex == 0 && !CapitalCore.HasSeat(c, small),
            "fallen, its books are closed at zero and it has no capital and no seat");

        // Back on the map by the road a status quo peace or a coalition's liberation takes: the
        // same transfer every peace uses. Before the fix this left it on the list.
        StatePartition.Annex(c, giveBack, small, asConquest: true);
        var held = c.StatesOf(small);
        Check(held.Length > 0, "the region is Luxembourg's again");
        Check(!c.Fallen.Contains(small.Id), "and Luxembourg is off the fallen list the moment the border moves");
        Check(small.Population == held.Sum(s => s.Population) && small.Population > 0,
            $"its people are counted at once ({small.Population:N0})");
        // Its books reopened at what those people make by its own 2024 measure, not at zero to
        // crawl back a tenth a week: before the fix a returned country taxed nothing for months.
        var expected = gdp2024 * (small.Population / (double)people2024);
        Check(small.Gdp > 0.5 * expected && small.Gdp < 2 * expected,
            $"its GDP is reopened at what its people make ({small.Gdp:0.###}, its 2024 output per head says {expected:0.###})");
        // And a seat, as the four older roads back gave one. CapitalCore never places a capital
        // for a country with none; before the fix only saving and opening the campaign did.
        Check(small.CapitalProvinceId > 0 && small.CapitalStateMapIndex > 0 && CapitalCore.HasSeat(c, small) &&
              held.Any(s => s.MapIndex == small.CapitalStateMapIndex),
            $"it has a capital in its own region again (province {small.CapitalProvinceId}) without a save and a reload");
        for (var tick = 0; tick < 4; tick++) CapitalCore.Tick(c);
        Check(small.CapitalProvinceId > 0 && CapitalCore.HasSeat(c, small), "and the capital watch keeps it");
        var running = (small.CapitalProvinceId, small.CapitalStateMapIndex, small.GovernmentSeatProvinceId);
        var folder = Path.Combine(Path.GetTempPath(), "dof-fallen-seat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            SaveSystem.Save(folder, "seat", c, "");
            var reopened = SaveSystem.Load(folder, "seat", "");
            Check(reopened.Success, "the campaign with the returned country saves and opens: " + reopened.Warning);
            // A map of its own, built the way opening a save builds it.
            Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var fresh, out var why), why);
            reopened.Campaign.AttachProvinceTopology(fresh);
            var again = reopened.Campaign.NationByTag("LUX");
            Check((again.CapitalProvinceId, again.CapitalStateMapIndex, again.GovernmentSeatProvinceId) == running,
                $"the world opened from the save has the same seat as the one that kept running ({running})");
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
        MovementFall.Decide(c);
        Check(!c.Fallen.Contains(small.Id), "the next weekly review does not rule it fallen again");

        // The economy's week: a country with ground is an economy, and its people stay.
        SetDay(c, c.Defines.EconomyTickDays * 3);
        EconomySystem.Tick(c);
        Check(small.Population > 0 && small.Gdp > 0f,
            $"the economy's week keeps its people and gives it an output ({small.Population:N0} people, GDP {small.Gdp:0.###})");

        // The economy no longer empties a landed country on the strength of the list alone.
        c.Fallen.Add(small.Id);
        SetDay(c, c.Defines.EconomyTickDays * 4);
        EconomySystem.Tick(c);
        Check(small.Population > 0, "a stale list entry does not empty a country that holds a region");
        c.Fallen.Remove(small.Id);

        // A fallen country that is still landless stays fallen and empty.
        var gone = c.NationByTag("BEL");
        var monaco = c.NationByTag("MCO") ?? c.NationByTag("AND");
        if (monaco is not null)
        {
            StatePartition.Annex(c, ProvincesOf(c, monaco), c.NationByTag("FRA") ?? gone, asConquest: true);
            MovementFall.Decide(c);
            Check(c.Fallen.Contains(monaco.Id) && c.StatesOf(monaco).Length == 0 && MovementFall.ReturnTheLanded(c) == 0 &&
                  c.Fallen.Contains(monaco.Id), "a fallen country with no ground stays on the list");
        }

        OldSaveRepaired();
    }

    /// <summary>A save written before the fix carries a landed country on the list; it opens repaired.</summary>
    static void OldSaveRepaired()
    {
        var c = World(20261118);
        var stale = c.NationByTag("LUX");
        var regionPeople = c.StatesOf(stale).Sum(s => s.Population);
        var gdp2024 = stale.Gdp;
        // The state the bug left: ground, people in the regions, nobody counted, on the list.
        c.Fallen.Add(stale.Id);
        stale.Population = 0;
        stale.Gdp = 0f;
        var folder = Path.Combine(Path.GetTempPath(), "dof-fallen-ground-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            SaveSystem.Save(folder, "stale", c, "");
            var loaded = SaveSystem.Load(folder, "stale", "");
            Check(loaded.Success && loaded.Campaign is not null, "the save with the stale list opens: " + loaded.Warning);
            var back = loaded.Campaign.NationByTag("LUX");
            Check(!loaded.Campaign.Fallen.Contains(back.Id), "the loaded campaign does not have it on the fallen list");
            Check(back.Population == regionPeople && back.Population > 0,
                $"its people are counted on load ({back.Population:N0} = the regions' {regionPeople:N0})");
            Check(back.Gdp > 0.5f * gdp2024 && back.Gdp < 2f * gdp2024,
                $"and its books, closed at zero in the save, open at what they make ({back.Gdp:0.###}; 2024: {gdp2024:0.###})");
            var landedFallen = loaded.Campaign.Nations.Count(n => n.Movement is null && loaded.Campaign.Fallen.Contains(n.Id) &&
                                                                  loaded.Campaign.StatesOf(n).Length > 0);
            Check(landedFallen == 0, "no country with ground is on the list after load");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
