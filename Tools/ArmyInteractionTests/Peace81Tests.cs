using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class Peace81Tests
{
    public static void Run()
    {
        // Two pre-existing land components of one loser. Taking region 5 bisects
        // the secondary component, not the one containing its capital:
        //   1--2--3
        //   (sea)
        //   4--5--6
        //      7 (winner)
        var c = new Campaign(20260928) { ScenarioId = "peace-fixture" };
        var winner = new Nation { Id = 0, MapIndex = 1, Tag = "WIN", CapitalStateMapIndex = 7 };
        var loser = new Nation { Id = 1, MapIndex = 2, Tag = "LOS", CapitalStateMapIndex = 1 };
        c.Nations.Add(winner); c.Nations.Add(loser);
        for (var i = 1; i <= 7; i++)
            c.States.Add(new ScenarioState { Id = "test_" + i, MapIndex = i,
                NationMapIndex = i == 7 ? 1 : 2, Population = 100 });
        const int width = 9, height = 7;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        void Cell(int x, int y, ushort region, ushort owner)
        { states[y * width + x] = region; owners[y * width + x] = owner; }
        Cell(2, 1, 1, 2); Cell(3, 1, 2, 2); Cell(4, 1, 3, 2);
        Cell(2, 4, 4, 2); Cell(3, 4, 5, 2); Cell(4, 4, 6, 2);
        Cell(3, 5, 7, 1);
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        // Unit fixture: do not run scenario reconciliation, armies or economy.
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(c, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(c, (ushort[])map.ProvinceToNation.Clone());
        var war = new StrategicWar { AttackerId = winner.Id, DefenderId = loser.Id, ClaimedStateMapIndex = -1 };
        var losers = new HashSet<int> { loser.MapIndex };
        void Check(bool ok, string label)
        { Console.WriteLine((ok ? "PASS " : "FAIL ") + label); if (!ok) throw new Exception(label); }

        var chosen = new HashSet<int> { 5 };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.Count == 0, "treaty cannot bisect an existing exclave outside the capital component");

        loser.CapitalStateMapIndex = 4;
        chosen = new HashSet<int> { 5 };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.Count == 0, "capital component retains the same protection");

        loser.CapitalStateMapIndex = 1;
        c.States.Single(s => s.MapIndex == 6).Population = 10;
        chosen = new HashSet<int> { 5 };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.SetEquals(new[] { 5, 6 }), "small new pocket is included without taking an unrelated old component");
        var province = Enumerable.Range(1, map.ProvinceCount).Single(p => map.ProvinceToState[p] == 5);
        var settled = PeaceShape.SettleProvinces(c, war, new[] { province }, winner);
        Check(settled.Select(p => (int)map.ProvinceToState[p]).ToHashSet().SetEquals(new[] { 5, 6 }),
            "enforced treaty path applies the component repair to actual provinces");

        chosen = new HashSet<int> { 5 };
        PeaceShape.Settle(c, war, chosen, winner, losers, affordable: () => chosen.Count <= 1);
        Check(chosen.Count == 0, "unaffordable pocket is repaired by returning the cut, never by free annexation");

        chosen = new HashSet<int> { 4, 5, 6 };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.SetEquals(new[] { 4, 5, 6 }), "whole component annexation leaves the separate mainland untouched");
    }
}
