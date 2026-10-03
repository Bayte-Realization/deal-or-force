using System;
using System.Linq;
using DealOrForce.Simulation;

static class PlanErasingTests
{
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS erasing: " + label); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(101);
        var own = c.NationByTag("POL"); var enemy = c.NationByTag("BLR");
        c.ConfigureSinglePlayer(own.Id);
        var a = c.Armies.First(x => x.NationId == own.Id);
        var b = c.Armies.Last(x => x.NationId == own.Id);
        var foreign = c.Armies.First(x => x.NationId == enemy.Id);
        var line = new DrawnFrontLine { Id = 8100, NationId = own.Id, Kind = FrontLineKind.Offensive, BorderNationId = enemy.Id };
        line.SourceProvinces.UnionWith(new[] { 1, 2 }); line.Provinces.Add(3); line.ObjectiveSequence.Add(3);
        line.ArmyIds.AddRange(new[] { a.Id, 999999, foreign.Id }); // b missing from stale roster
        line.Objectives[a.Id] = 3;
        c.FrontLines.Add(line);
        var untouched = new DrawnFrontLine { Id = 8101, NationId = enemy.Id, Kind = FrontLineKind.Defence };
        c.FrontLines.Add(untouched);
        foreach (var army in new[] { a, b })
        { army.FrontLineId = line.Id; army.Route.Add(1); army.PlannedRoute.Add(3); army.PlanActive = true;
          army.OrderKind = ArmyOrder.AdvanceFront; army.DirectOrder = true; army.ObjectiveStateMapIndex = 55; }
        a.BattleId = 890; var province = a.ProvinceId;
        new DeleteBattlePlanCommand { IssuerId = enemy.Id, FrontLineId = line.Id }.Apply(c);
        Check(c.FrontLines.Contains(line) && a.PlanActive, "foreign issuer cannot erase our plan");
        var command = new DeleteBattlePlanCommand { IssuerId = own.Id, FrontLineId = line.Id, ObjectiveOnly = true };
        Check(NetworkCommandCodec.TryEncode(command, out var name, out var json) &&
              NetworkCommandCodec.TryDecode(name, json, out var decoded) && decoded is DeleteBattlePlanCommand { ObjectiveOnly: true },
            "eraser command survives network codec");
        command.Apply(c);
        Check(c.FrontLines.Contains(line) && line.Kind == FrontLineKind.Defence && line.Provinces.SetEquals(new[] { 1, 2 }),
            "erase objective keeps original departure front and stable plan ID");
        Check(line.ObjectiveSequence.Count == 0 && line.Objectives.Count == 0 && line.SourceProvinces.Count == 0,
            "all offensive intent caches cleared");
        Check(line.ArmyIds.OrderBy(x => x).SequenceEqual(new[] { a.Id, b.Id }.OrderBy(x => x)),
            "stale dead/foreign roster repaired and missing current participant included");
        Check(new[] { a, b }.All(x => x.Route.Count == 0 && x.PlannedRoute.Count == 0 && !x.PlanActive && !x.DirectOrder && x.OrderKind == ArmyOrder.DefendFront),
            "offensive routes stop for every participant");
        Check(a.BattleId == 890 && a.ProvinceId == province, "erasing does not cancel a battle or teleport units");
        new DeleteBattlePlanCommand { IssuerId = own.Id, FrontLineId = untouched.Id }.Apply(c);
        Check(c.FrontLines.Contains(untouched), "foreign plans protected");
        new DeleteBattlePlanCommand { IssuerId = own.Id, FrontLineId = line.Id, ObjectiveOnly = true }.Apply(c);
        Check(c.FrontLines.Contains(line), "objective-only click on a non-offensive plan is harmless");
        new DeleteBattlePlanCommand { IssuerId = own.Id, FrontLineId = line.Id }.Apply(c);
        Check(!c.FrontLines.Contains(line) && new[] { a, b }.All(x => x.FrontLineId < 0 && x.OrderKind == ArmyOrder.Manual),
            "erase complete plan removes it and leaves units under manual control");
        new DeleteBattlePlanCommand { IssuerId = own.Id, FrontLineId = line.Id }.Apply(c);
        Check(c.FrontLines.Count == 1, "repeated deletion is idempotent and keeps unrelated plans");
        var ghost = new DrawnFrontLine { Id = 8102, NationId = own.Id }; ghost.ArmyIds.Add(999999); c.FrontLines.Add(ghost);
        new DeleteBattlePlanCommand { IssuerId = own.Id, FrontLineId = ghost.Id }.Apply(c);
        Check(!c.FrontLines.Contains(ghost), "orphaned plans can be deleted without surviving divisions");
    }
}
