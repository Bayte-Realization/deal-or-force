using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The province card's "Garrison here" asks the simulation for the nearest free division.
/// These checks use a real four-province map and real Army objects, without Godot, so the
/// choice cannot drift into a UI-only rule.
/// </summary>
static class GarrisonPlannerTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("garrison planner: " + label); Console.WriteLine("PASS garrison planner: " + label); }

    public static void Run()
    {
        var campaign = Fixture();
        var player = campaign.NationByTag("AAA");
        var enemy = campaign.NationByTag("BBB");
        var map = campaign.ProvinceMap;
        var target = 2;
        var centre = map.CenterOf(target);
        var others = Enumerable.Range(1, map.ProvinceCount).Where(province => province != target)
            .OrderBy(province =>
            {
                var point = map.CenterOf(province);
                var latitude = point.Latitude - centre.Latitude;
                var longitude = point.Longitude - centre.Longitude;
                return latitude * latitude + longitude * longitude;
            }).ToArray();
        var near = Unit(campaign, player, 0, others[0]);
        var far = Unit(campaign, player, 1, others[^1]);
        var foreign = Unit(campaign, enemy, 2, others[0]);

        var chosen = GarrisonPlanner.NearestFree(campaign, player, target);
        Check(chosen == near,
            $"the nearest free division is chosen, not the first in the list (near {near.ProvinceId}, far {far.ProvinceId}, chosen {chosen?.Id}/{chosen?.ProvinceId})");
        near.Divisions = 0f;
        Check(GarrisonPlanner.NearestFree(campaign, player, target) == far,
            "a shattered formation with no divisions left is not offered as a garrison");
        near.Divisions = 1f;
        near.BattleId = 9;
        Check(GarrisonPlanner.NearestFree(campaign, player, target) == far,
            "a division in battle is not offered as a garrison");
        near.BattleId = -1;
        far.EmbarkedFleetId = 7;
        Check(GarrisonPlanner.NearestFree(campaign, player, target) == near,
            "a division at sea is not offered while a free one remains");
        far.EmbarkedFleetId = -1;
        near.ProvinceId = target;
        Check(GarrisonPlanner.NearestFree(campaign, player, target) == far,
            "a division already standing in the province is not sent there again, and a foreign division never is");
        far.BattleId = 4;
        Check(GarrisonPlanner.NearestFree(campaign, player, target) == null,
            "when every formation is committed, no garrison is invented");
        Check(GarrisonPlanner.ResultText(ArmyMoveResult.Issued).Contains("march", StringComparison.OrdinalIgnoreCase),
            "the player reads that the division is marching, not an internal enum");
    }

    static Campaign Fixture()
    {
        var c = new Campaign(4242) { ScenarioId = "garrison_fixture" };
        var player = c.AddNation(new Nation { Tag = "AAA", MapIndex = 501, Population = 2_000_000, Gdp = 50 });
        var enemy = c.AddNation(new Nation { Tag = "BBB", MapIndex = 502, Population = 1_000_000, Gdp = 30 });
        for (var i = 0; i < 4; i++)
            c.States.Add(new ScenarioState { Id = "garrison_region_" + i, MapIndex = 1001 + i,
                Name = "Garrison region " + i, NationId = i < 3 ? player.Tag : enemy.Tag,
                NationMapIndex = i < 3 ? player.MapIndex : enemy.MapIndex, Population = 500_000 });
        var cells = Enumerable.Range(0, 80).Select(i => (ushort)(1001 + i / 20)).ToArray();
        var owners = Enumerable.Range(0, 80).Select(i => (ushort)(i / 20 < 3 ? 501 : 502)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, 20, 4, _ => false));
        for (var province = 1; province <= c.ProvinceMap.ProvinceCount; province++)
            c.ProvinceMap.Neighbors[province] = province == 1 ? new[] { 2 } :
                province == c.ProvinceMap.ProvinceCount ? new[] { province - 1 } :
                new[] { province - 1, province + 1 };
        return c;
    }

    static Army Unit(Campaign c, Nation nation, int id, int province)
    {
        var army = new Army { Id = id, NationId = nation.Id, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], TemplateId = "line_infantry",
            Divisions = 1, Personnel = 10000, MaxPersonnel = 10000 };
        c.Armies.Add(army);
        return army;
    }
}
