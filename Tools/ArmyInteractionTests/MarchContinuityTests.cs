using System;
using System.Linq;
using DealOrForce.Simulation;

static class MarchContinuityTests
{
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("march continuity: " + text); Console.WriteLine("PASS march continuity: " + text); }

    public static void Run()
    {
        var (c, a) = World();
        Order(c, a, ArmyOrder.AdvanceFront);
        Check(a.OrderProvinceId == 7 && a.Route[0] == 4 && a.MoveProgress == .65f,
            "reissuing an unchanged front order preserves distance already marched on the same edge");

        (c, a) = World(); Order(c, a, ArmyOrder.Spearhead);
        Check(a.OrderKind == ArmyOrder.Spearhead && a.MoveProgress == .65f,
            "promotion from advance to spearhead does not restart the same march");
        Order(c, a, ArmyOrder.AdvanceFront);
        Check(a.OrderKind == ArmyOrder.AdvanceFront && a.MoveProgress == .65f,
            "return from spearhead to advance does not restart the same march");

        (c, a) = World(); c.Wars[0].AttackerFront.Add(6); Order(c, a, ArmyOrder.DefendFront, 6);
        Check(a.OrderProvinceId == 6 && a.Route[0] == 4 && a.MoveProgress == .65f,
            "a genuinely new goal still keeps travel along an unchanged first edge");
        (c, a) = World(); c.Wars[0].AttackerFront.Add(2); Order(c, a, ArmyOrder.DefendFront, 2);
        Check(a.OrderProvinceId == 2 && a.Route[0] == 2 && a.MoveProgress == 0,
            "a reversal cannot spend progress made in the other direction");

        (c, a) = World(); a.OrderKind = ArmyOrder.Manual; a.DirectOrder = true;
        Order(c, a, ArmyOrder.AdvanceFront);
        Check(a.MoveProgress == 0 && !a.DirectOrder, "front reassignment still cancels a direct manual march cleanly");
        (c, a) = World(); a.AssignedWarId = 999;
        Order(c, a, ArmyOrder.AdvanceFront);
        Check(a.MoveProgress == 0 && a.AssignedWarId == 883, "changing war does not inherit unrelated order progress");

        (c, a) = World(); c.SetProvinceController(4, 502);
        Order(c, a, ArmyOrder.AdvanceFront);
        Check(a.Route.Count == 0 && a.MoveProgress == 0, "a route cut by neutral territory is not preserved");
        (c, a) = World(); var before = CampaignFingerprint.Of(c);
        new SetArmyOrderCommand { IssuerId = 1, ArmyId = a.Id, WarId = 883, Order = ArmyOrder.AdvanceFront }.Apply(c);
        Check(CampaignFingerprint.Of(c) == before, "foreign issuers cannot alter march state");

        (c, a) = World(); c.Defines.MarchProgressPerDay = .3f;
        for (var i = 0; i < 5; i++) { Order(c, a, i % 2 == 0 ? ArmyOrder.Spearhead : ArmyOrder.AdvanceFront); MilitaryOperations.Tick(c); }
        Check(a.ProvinceId != 3 && a.OrderStepsCompleted > 0,
            "repeated staff role changes no longer prevent actual province arrival");

        var (first, left) = World(); var (second, right) = World();
        Order(first, left, ArmyOrder.Spearhead); Order(second, right, ArmyOrder.Spearhead);
        Check(CampaignFingerprint.Of(first) == CampaignFingerprint.Of(second), "the same role transition remains deterministic between campaigns");
        first.ConfigureSinglePlayer(0);
        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(first, "march"), "march");
        Check(loaded.Success && loaded.Campaign.Armies.Single().MoveProgress == left.MoveProgress &&
            loaded.Campaign.Armies.Single().Route.SequenceEqual(left.Route),
            "existing snapshot fields carry the preserved march without a save-format extension: " + loaded.Warning);
    }

    static void Order(Campaign c, Army a, ArmyOrder order, int post = -1) => new SetArmyOrderCommand
    { IssuerId = 0, ArmyId = a.Id, WarId = 883, Order = order, PreferredProvinceId = post, ObjectiveStateMapIndex = 3008 }.Apply(c);

    internal static (Campaign, Army) World()
    {
        const int width = 256, height = 32;
        var states = new ushort[width * height]; var owners = new ushort[states.Length]; var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var p = x / 32 + 1; var i = y * width + x;
            states[i] = (ushort)(3000+p); owners[i] = (ushort)(p == 1 ? 502 : p == 8 ? 501 : 500); rgb[i*3+2] = (byte)p;
        }
        var map = ProvinceTopology.FromColorRaster(states, owners, width, height, new RgbDataMap(width, height, rgb),
            Enumerable.Range(1,8).ToDictionary(x=>x,x=>x));
        var c = new Campaign(20261018) { ScenarioId = "march_continuity_fixture" };
        c.Defines.HoursPerTick = 24;
        for (var n = 0; n < 3; n++) c.AddNation(new Nation { Tag = "MC"+n, MapIndex = 500+n, Name = "Country "+n, Population = 1000000 });
        for (var p = 1; p <= 8; p++) c.States.Add(new ScenarioState { Id = "march_"+p, MapIndex = 3000+p,
            NationMapIndex = p == 1 ? 502 : p == 8 ? 501 : 500, Population = 100000 });
        c.AttachProvinceTopology(map); c.Armies.Clear();
        var war = new StrategicWar { Id = 883, AttackerId = 0, DefenderId = 1, ClaimedStateMapIndex = 3008 };
        war.AttackerFront.Add(7); war.DefenderFront.Add(8); c.Wars.Add(war);
        var centre = map.CenterOf(3);
        var a = new Army { Id = 1, NationId = 0, ProvinceId = 3, StateMapIndex = 3003, TemplateId = "line_infantry",
            OrderKind = ArmyOrder.AdvanceFront, OrderProvinceId = 7, OrderStateMapIndex = 3007, AssignedWarId = 883,
            Organisation = 1, SupplyLevel = 1, Strength = 1, Divisions = 1, Latitude = centre.Latitude, Longitude = centre.Longitude,
            MoveProgress = .65f, OrderIssuedDay = -10, OrderRouteSteps = 4 };
        a.Route.AddRange(new[] {4,5,6,7}); a.PlannedRoute.AddRange(a.Route); c.Armies.Add(a);
        return (c,a);
    }
}
