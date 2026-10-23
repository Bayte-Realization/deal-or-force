using System;
using System.Linq;
using DealOrForce.Simulation;

static class RevoltOccupationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("occupied uprising: " + note); Console.WriteLine("PASS occupied uprising: " + note); }

    static Campaign Fixture(bool fullyOccupied = false)
    {
        var c = new Campaign(20261018) { ScenarioId = "occupied_uprising_fixture" };
        c.AddNation(new Nation { Tag = "ORT", Name = "Parent", MapIndex = 501,
            Population = 2000000, Gdp = 200, Treasury = 40, PublicDebt = 20, ManpowerReserve = 20000,
            Government = "presidential_republic", Stability = .8f, WarSupport = .8f });
        c.AddNation(new Nation { Tag = "OCC", Name = "Occupier", MapIndex = 502,
            Population = 1000000, Gdp = 100, Treasury = 10 });
        c.ConfigureSinglePlayer(0);
        for (var i = 0; i < 3; i++)
        {
            var s = new ScenarioState { Id = "revolt_" + i, Name = "Region " + i,
                MapIndex = 3001 + i, NationMapIndex = i < 2 ? 501 : 502,
                NationId = i < 2 ? "ORT" : "OCC", Population = 1000000 };
            s.PopulationGroups.Add(new PopulationGroup { Culture = "english", Size = 1000000 });
            c.States.Add(s);
        }
        const int width = 96, height = 32;
        var states = new ushort[width * height]; var owners = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var i = y * width + x;
            states[i] = (ushort)(3001 + x / 32); owners[i] = (ushort)(x < 64 ? 501 : 502);
            rgb[i * 3 + 2] = (byte)(y / 16 * 6 + x / 16 + 1);
        }
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.FromColorRaster(states, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1,12).ToDictionary(p => p, p => p)));
        c.Nations[0].CapitalStateMapIndex = 3001; c.Nations[1].CapitalStateMapIndex = 3003;
        foreach (var p in fullyOccupied ? new[] {3,4,9,10} : new[] {3,9})
            c.SetProvinceController(p, 502);
        c.Wars.Add(new StrategicWar { Id = 7, AttackerId = 1, DefenderId = 0, ClaimedStateMapIndex = 3002 });
        var unit = new Army { Id = 100, NationId = 1, ProvinceId = 3, StateMapIndex = 3002,
            AssignedWarId = 7, Divisions = 1, Strength = 1, Organisation = 1 };
        unit.Route.Add(9); c.Armies.Add(unit);
        return c;
    }

    public static void Run()
    {
        var c = Fixture(); var parent = c.Nations[0]; var invader = c.Nations[1];
        var originalWar = c.Wars[0]; var unit = c.Armies.Single();
        var cash = c.Nations.Sum(n => n.Treasury); var gdp = c.Nations.Sum(n => n.Gdp);
        var debt = c.Nations.Sum(n => n.PublicDebt);
        var report = SecessionEvents.Force(c, c.States.Single(s => s.MapIndex == 3002), "english");
        Check(report?.Seceded is not null, "a partly occupied region can still break away on its free ground");
        var born = report.Seceded;
        Check(new[] {3,9}.All(p => c.ProvinceMap.ProvinceToNation[p] == 501 && c.ProvinceController[p] == 502),
            "occupied provinces retain the parent's title and the occupier's actual control");
        Check(new[] {4,10}.All(p => c.ProvinceMap.ProvinceToNation[p] == born.MapIndex &&
            c.ProvinceController[p] == born.MapIndex), "only government-held provinces change administration");
        Check(unit.ProvinceId == 3 && unit.Route.SequenceEqual(new[] {9}) && unit.NationId == invader.Id &&
            unit.AssignedWarId == 7, "foreign troops retain their position, route, allegiance and war");
        Check(!originalWar.Ended && !c.Wars.Any(w => !w.Ended && w.Opposes(born.Id, invader.Id)),
            "secession neither ends the invasion nor invents a war against the new state");
        Check(born.Population == 500000 && parent.Population == 1500000 &&
            report.Population == born.Population && c.Nations.Sum(n => n.Population) == 3000000,
            "population is partitioned for the real departing half, not duplicated");
        Check(Math.Abs(born.Gdp - 50) < .001 && Math.Abs(born.Treasury - 10) < .001 &&
            Math.Abs(born.PublicDebt - 5) < .001 && Math.Abs(c.Nations.Sum(n => n.Gdp) - gdp) < .001 &&
            Math.Abs(c.Nations.Sum(n => n.Treasury) - cash) < .001 &&
            Math.Abs(c.Nations.Sum(n => n.PublicDebt) - debt) < .001,
            "the smaller settlement receives exactly its share and conserves national accounts");
        Check(c.Armies.Where(a => a.NationId == born.Id).All(a => c.ProvinceController[a.ProvinceId] == born.MapIndex),
            "founding militia never spawns behind the foreign occupier's lines");
        var twin = Fixture(); SecessionEvents.Force(twin, twin.States.Single(s => s.MapIndex == 3002), "english");
        Check(CampaignFingerprint.Of(c) == CampaignFingerprint.Of(twin), "the occupied split is deterministic");

        var full = Fixture(true); var before = CampaignFingerprint.Of(full);
        Check(SecessionEvents.Force(full, full.States.Single(s => s.MapIndex == 3002), "english") is null &&
            CampaignFingerprint.Of(full) == before, "a fully occupied region cannot create a free state by repainting the army");

        var civil = Fixture(); var civilParent = civil.Nations[0];
        var rising = SecessionEvents.Uprising(civil, civilParent,
            new[] { civil.States.Single(s => s.MapIndex == 3002) }, _ => {});
        Check(rising?.Seceded is not null && civil.Wars.Any(w => !w.Ended && w.Opposes(civilParent.Id, rising.Seceded.Id)) &&
            civil.ProvinceController[3] == 502 && civil.ProvinceMap.ProvinceToNation[3] == 501,
            "civil wars use the same occupation protection and still fight their parent");

        var movement = full.AddNation(new Nation { Tag = "OMV", MapIndex = 503, Treasury = 10,
            Movement = new MovementState { Id = "bavaria" } });
        before = CampaignFingerprint.Of(full);
        Check(!MovementRules.TakeRegion(full, movement, full.States.Single(s => s.MapIndex == 3002),
            full.Nations[0], restoration: true) && CampaignFingerprint.Of(full) == before,
            "whole-region restorations and movement settlements cannot bypass the foreign occupation guard");
    }
}
