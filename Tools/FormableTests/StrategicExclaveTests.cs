using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

internal static class StrategicExclaveTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("FAIL strategic exclave: " + message); Console.WriteLine("PASS strategic exclave: " + message); }

    internal static void Run()
    {
        var c = ExclaveSupplyTests.Rack(); var rng = c.Random.State;
        var posts = StrategicExclaves.EntryPosts(c, c.Nations[0]);
        Check(posts.Count == 1 && posts[0] == 5, "one coastal post in the substantial exclave, not its detached spit");
        Check(StrategicExclaves.Importance(c, 1) == 0 && StrategicExclaves.Importance(c, 4) == 0,
            "mainlands and the neighbouring country's ordinary border do not become exclaves");
        Check(StrategicExclaves.Importance(c, 5) > StrategicExclaves.Importance(c, 3) &&
            StrategicExclaves.Importance(c, 3) > 0, "sea entrance has higher priority than its inland hinterland");
        c.Armies.Clear();
        for (var i = 0; i < 12; i++) c.Armies.Add(new Army { Id = i + 1, NationId = 0,
            ProvinceId = 1, Divisions = 1, Personnel = 10000, MaxPersonnel = 10000, Strength = 1,
            TemplateId = "line_infantry" });
        StrategicDeploymentPlanner.Deploy(c);
        Check(c.Armies.Count == 12 && c.Armies.Any(a => a.ProvinceId == 5) && c.Armies[^1].ProvinceId == 1,
            "opening deployment covers the exclave using existing reserves and retains the capital reserve");
        Check(c.Random.State == rng, "geographic priorities and deployment consume no campaign RNG");

        c.Armies.Clear(); c.SetProvinceController(2, 101);
        var war = new StrategicWar { Id = 1, AttackerId = 1, DefenderId = 0 };
        c.Wars.Add(war);
        var attacker = new Army { Id = 1, NationId = 1, ProvinceId = 4, Divisions = 1,
            OrderKind = ArmyOrder.AdvanceFront, TemplateId = "line_infantry" };
        c.Armies.Add(attacker);
        typeof(Warfare).GetMethod("EnsureIndexed", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { c });
        var choose = typeof(Warfare).GetMethod("ChooseAssaultTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        int Target() => (int)choose.Invoke(null, new object[] { c, c.ProvinceMap, war, attacker,
            c.ProvinceController, new HashSet<ushort> { 100 } });
        c.Defines.ExclaveAssaultWeight = 0;
        var ordinary = Target();
        c.Defines.ExclaveAssaultWeight = .32f;
        var strategic = Target();
        Check(ordinary == 3 && strategic == 5, "actual assault selector prefers the coastal entrance over an equally open inland target");
        attacker.DirectOrder = true; attacker.OrderProvinceId = 3;
        Check(Target() == 3, "a player's explicit target still overrides the strategic preference");
    }
}
