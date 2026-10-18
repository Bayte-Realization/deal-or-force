using System;
using System.Linq;
using DealOrForce.Simulation;

static class TerritorialFrontTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("territorial front: " + note); Console.WriteLine("PASS territorial front: " + note); }

    static Campaign Fixture()
    {
        var c = new Campaign(20261018) { ScenarioId = "local_militia_front" };
        c.AddNation(new Nation { Tag = "TF0", MapIndex = 501, Population = 1000000 });
        c.AddNation(new Nation { Tag = "TF1", MapIndex = 502, Population = 1000000 });
        const int width = 64, height = 48;
        var regions = new ushort[width * height]; var owners = new ushort[regions.Length];
        var rgb = new byte[regions.Length * 3];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var i = y * width + x; var p = y / 16 * 4 + x / 16 + 1;
            regions[i] = (ushort)(x < 32 ? p == 10 ? 3003 : 3001 : 3002);
            owners[i] = (ushort)(x < 32 ? 501 : 502); rgb[i * 3 + 2] = (byte)p;
        }
        for (var i = 0; i < 3; i++) c.States.Add(new ScenarioState {
            MapIndex = 3001 + i, Id = "tf_" + i, NationMapIndex = i == 1 ? 502 : 501, Population = 100000 });
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.FromColorRaster(regions, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1,12).ToDictionary(p => p, p => p)));
        var war = new StrategicWar { Id = 9, AttackerId = 1, DefenderId = 0 };
        war.DefenderFront.UnionWith(new[] {2,6,10}); c.Wars.Add(war);
        c.Defines.FrontSettleDays = 0;
        var centre = c.ProvinceMap.CenterOf(5);
        c.Armies.Add(new Army { Id = 1, NationId = 0, ProvinceId = 5, StateMapIndex = 3001,
            TemplateId = MilitiaCore.TemplateId, Divisions = 1, Strength = 1, Organisation = 1,
            OrderKind = ArmyOrder.Manual, PlanActive = false, Latitude = centre.Latitude, Longitude = centre.Longitude });
        return c;
    }

    public static void Run()
    {
        var c = Fixture(); var unit = c.Armies[0]; var start = unit.ProvinceId;
        FrontManager.Tick(c);
        Check(unit.AssignedWarId < 0 && unit.OrderKind == ArmyOrder.Manual && !unit.PlanActive,
            "local militia keeps its territorial status without an expeditionary assignment");
        Check(unit.OrderProvinceId is 2 or 6 && unit.Route.Count > 0 && unit.ProvinceId == start,
            "a defensive gap receives an actual marching order, not a teleport");
        Check(unit.Route.All(p => c.ProvinceMap.ProvinceToState[p] == 3001) && unit.OrderProvinceId != 10,
            "territorial troops neither leave their region nor fill another region's front");
        var order = unit.OrderProvinceId; var route = unit.Route.ToArray();
        FrontManager.Tick(c);
        Check(unit.OrderProvinceId == order && unit.Route.SequenceEqual(route),
            "another staff look does not churn an already useful local march");
        FrontPlanner.ReassignAi(c, c.Wars[0]);
        Check(unit.OrderKind == ArmyOrder.Manual && unit.AssignedWarId < 0,
            "territorial duty remains outside the expeditionary offensive role shares");

        var human = Fixture(); human.Nations[0].IsHuman = true;
        FrontManager.Tick(human);
        Check(human.Armies[0].Route.Count == 0, "a human keeps control without field-staff delegation");
        Delegation.Set(human.Nations[0], Delegation.Command, true); FrontManager.Tick(human);
        Check(human.Armies[0].Route.Count > 0, "delegated local defence uses the same rules as AI");
        var direct = Fixture(); direct.Armies[0].DirectOrder = true; FrontManager.Tick(direct);
        Check(direct.Armies[0].Route.Count == 0, "explicit orders are never silently commandeered");
        var ended = Fixture(); ended.Wars[0].Ended = true; FrontManager.Tick(ended);
        Check(ended.Armies[0].Route.Count == 0, "peace does not call up a territorial army");
    }
}
