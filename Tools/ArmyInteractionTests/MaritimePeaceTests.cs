using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class MaritimePeaceTests
{
    public static void Run(Campaign c)
    {
        var map = c.ProvinceMap;
        var winner = c.NationByTag("JPN");
        var loser = c.NationByTag("CHN");
        // How a landing anchors a treaty, not how far away it may be: the fixture's beach is
        // wherever China's smallest coastal region is. AllyGroundTests holds the reach.
        c.Defines.PeaceShapeBridgeheadKm = 0f;
        var war = new StrategicWar { Id = 990, AttackerId = winner.Id, DefenderId = loser.Id, WarScore = 100 };
        var coast = SeaLanes.CoastalProvinces(map).ToHashSet();
        var provinces = Enumerable.Range(1, map.ProvinceCount).Where(p => map.ProvinceToNation[p] == loser.MapIndex).ToList();
        var byRegion = provinces.GroupBy(p => (int)map.ProvinceToState[p]).ToDictionary(g => g.Key, g => g.ToList());
        var beach = c.States.Where(s => s.NationMapIndex == loser.MapIndex && byRegion.ContainsKey(s.MapIndex) &&
                byRegion[s.MapIndex].Any(coast.Contains) && byRegion[s.MapIndex].Any(p =>
                    map.Neighbors[p].Any(n => map.ProvinceToState[n] != s.MapIndex)))
            .OrderBy(s => s.Population).First();
        var inland = byRegion.First(g => g.Value.All(p => !coast.Contains(p))).Key;
        void Check(bool ok, string label)
        { Console.WriteLine((ok ? "PASS " : "FAIL ") + label); if (!ok) throw new Exception(label); }
        void Occupy(int region, float security)
        {
            foreach (var p in byRegion[region])
            { c.SetProvinceController(p, (ushort)winner.MapIndex); c.ProvinceOccupation[p] = security; }
        }
        void Reset()
        {
            foreach (var p in provinces)
            { c.SetProvinceController(p, (ushort)loser.MapIndex); c.ProvinceOccupation[p] = 0; }
            war.ClaimedStateMapIndex = -1;
        }
        var losers = new HashSet<int> { loser.MapIndex };
        Reset(); Occupy(beach.MapIndex, 1);
        Console.WriteLine($"Maritime fixture: Japan -> China; beachhead={beach.Id}, inland={inland}");
        var chosen = new HashSet<int> { beach.MapIndex };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.Contains(beach.MapIndex), "secured overseas beachhead survives treaty shaping without a land border");
        var settled = PeaceShape.SettleProvinces(c, war, byRegion[beach.MapIndex], winner);
        Check(settled.Any(p => map.ProvinceToState[p] == beach.MapIndex), "enforced naval peace retains a secured landing region");
        c.Defines.PeaceCostPerProvince = 0.01f;
        var ledger = new PeaceLedger(c, war, winner.Id);
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        PeaceShape.Write(c, winner, war, ledger, demand, 85);
        Check(demand.Provinces.Any(p => map.ProvinceToState[p] == beach.MapIndex), "AI can write a maritime territorial demand");
        Check(ledger.Cost(demand) <= 85 && PeaceConference.BlockReason(c, war, demand) == "",
            "maritime demand respects budget and conference validation");
        demand.Provinces.Clear();
        PeaceShape.Write(c, winner, war, ledger, demand, 0);
        Check(demand.Provinces.Count == 0, "secured coast is not a free annexation at zero budget");

        c.Defines.PeaceShapeAllowCoastalBridgeheads = false;
        chosen = new HashSet<int> { beach.MapIndex };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.Count == 0, "mod rule can disable overseas treaty anchors");
        c.Defines.PeaceShapeAllowCoastalBridgeheads = true;

        var largeBeach = byRegion.First(g => g.Value.Count >= 8 && g.Value.Any(coast.Contains));
        var coastalProvince = largeBeach.Value.First(coast.Contains);
        Reset();
        c.SetProvinceController(coastalProvince, (ushort)winner.MapIndex);
        c.ProvinceOccupation[coastalProvince] = 1;
        chosen = new HashSet<int> { largeBeach.Key };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.Count == 0, "one secured port below the regional occupation threshold is insufficient");

        Reset(); Occupy(beach.MapIndex, c.Defines.OccupationSecureThreshold - 0.01f);
        chosen = new HashSet<int> { beach.MapIndex };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.Count == 0, "landing just below security threshold cannot anchor peace");
        Occupy(beach.MapIndex, c.Defines.OccupationSecureThreshold);
        chosen = new HashSet<int> { beach.MapIndex };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.Contains(beach.MapIndex), "landing at the configured security threshold can anchor peace");

        Reset(); Occupy(beach.MapIndex, 0);
        chosen = new HashSet<int> { beach.MapIndex };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.Count == 0, "unsecured landing alone does not anchor an overseas treaty");
        Reset(); Occupy(inland, 1); war.ClaimedStateMapIndex = inland;
        chosen = new HashSet<int> { inland };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.Count == 0, "remote inland war goal cannot bypass territorial connectivity");
        Reset(); war.ClaimedStateMapIndex = beach.MapIndex;
        chosen = new HashSet<int> { beach.MapIndex };
        PeaceShape.Settle(c, war, chosen, winner, losers);
        Check(chosen.Count == 0, "unoccupied coastal war goal cannot invent a beachhead");

        Reset(); Occupy(beach.MapIndex, 1);
        c.Wars.Add(war);
        foreach (var p in byRegion[beach.MapIndex]) war.OriginalController[p] = (ushort)loser.MapIndex;
        demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        ledger = new PeaceLedger(c, war, winner.Id);
        PeaceShape.Write(c, winner, war, ledger, demand, 85);
        var quoted = PeaceConference.Round(c, war, demand).Provinces;
        var before = (ushort[])map.ProvinceToNation.Clone();
        Check(quoted.Count > 0 && PeaceConference.Resolve(c, war, demand), "maritime treaty signs and ends the war");
        Check(war.Ended && Enumerable.Range(1, map.ProvinceCount).All(p =>
                map.ProvinceToNation[p] == (quoted.Contains(p) ? winner.MapIndex : before[p])),
            "maritime signing transfers exactly the quoted land and leaves the rest unchanged");
        Check(war.CreatedStates == 0, "maritime settlement does not split administrative regions");
    }
}
