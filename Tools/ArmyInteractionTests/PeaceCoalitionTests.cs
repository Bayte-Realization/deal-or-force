using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class PeaceCoalitionTests
{
    public static void Run()
    {
        var c = new Campaign(20260928) { ScenarioId = "coalition-test" };
        for (var i = 0; i < 5; i++)
            c.Nations.Add(new Nation { Id = i, MapIndex = i + 1, Tag = "T" + i,
                Name = "Test " + i, Population = 1000000, Gdp = 10, Treasury = 5 });
        // Attacker + ally (0,3), defender + ally (1,2), neutral (4).
        var war = new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 1, WarScore = 100 };
        war.AttackerAllies.Add(3); war.DefenderAllies.Add(2); c.Wars.Add(war);
        const int width = 12, height = 5;
        var states = new ushort[width * height]; var owners = new ushort[states.Length];
        ushort[] regionOwners = { 0, 1, 2, 2, 3, 4, 5 };
        for (var r = 1; r <= 6; r++)
        {
            states[2 * width + r] = (ushort)r; owners[2 * width + r] = regionOwners[r];
            c.States.Add(new ScenarioState { Id = "r" + r, MapIndex = r,
                NationMapIndex = regionOwners[r], Population = 100000 });
        }
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(c, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(c, (ushort[])map.ProvinceToNation.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(c, new float[map.ProvinceCount + 1]);
        int P(int r) => Enumerable.Range(1, map.ProvinceCount).Single(p => map.ProvinceToState[p] == r);
        foreach (var nation in c.Nations)
        {
            var capital = c.States.First(s => s.NationMapIndex == nation.MapIndex);
            nation.CapitalStateMapIndex = capital.MapIndex;
            (nation.CapitalLatitude, nation.CapitalLongitude) = map.CenterOf(P(capital.MapIndex));
        }
        var failed = new List<string>();
        void Check(bool ok, string label)
        { Console.WriteLine((ok ? "PASS " : "FAIL ") + label); if (!ok) failed.Add(label); }
        PeaceDemand Demand(params int[] regions)
        {
            var d = new PeaceDemand { WarId = war.Id, ProposerId = 0 };
            foreach (var r in regions) d.Provinces.Add(P(r));
            return d;
        }
        var ledger = new PeaceLedger(c, war, 0);
        Check(ledger.Price(P(4)).Cost > 0, "enemy ally land has a real price, not zero");
        Check(ledger.ProvincesPerState.ContainsKey(4), "conference survey includes enemy ally regions");
        Check(PeaceConference.BlockReason(c, war, Demand(4)) == "", "affordable enemy ally demand can be signed");
        Check(PeaceConference.Budget(c, war, 3) == PeaceConference.Budget(c, war, 0), "attacking ally gets its side's score");
        Check(new PeaceLedger(c, war, 3).Loser == c.Nations[1], "attacking ally negotiates with enemy leader, not own leader");
        var defending = new PeaceLedger(c, war, 2);
        Check(defending.Loser == c.Nations[0] && defending.Price(P(5)).Cost > 0,
            "defending ally can price land of attacking ally");
        var normal = c.Defines.PeaceCostCapitalFactor;
        c.Defines.PeaceCostCapitalFactor = 1;
        var ordinaryCost = ledger.Price(P(4)).Cost;
        c.Defines.PeaceCostCapitalFactor = 3;
        Check(Math.Abs(ledger.Price(P(4)).Cost - ordinaryCost * 3) < 0.0001f && ordinaryCost > 0,
            "enemy ally capital receives capital surcharge");
        c.Defines.PeaceCostCapitalFactor = normal;
        foreach (var region in new[] { 1, 5, 6 })
            Check(PeaceConference.BlockReason(c, war, Demand(region)).Length > 0,
                "own, friendly or neutral region refused: " + region);
        war.WarScore = 0;
        Check(PeaceConference.BlockReason(c, war, Demand(4)).Length > 0, "enemy ally territory cannot be obtained with zero budget");
        war.WarScore = 100;
        var neutralProvince = P(6);
        var mixedRegionDemand = Demand(4);
        try
        {
            map.ProvinceToState[neutralProvince] = 4;
            Check(PeaceConference.BlockReason(c, war, mixedRegionDemand).Length > 0,
                "whole-region rounding cannot sweep in neutral provinces of a mixed-owner region");
        }
        finally { map.ProvinceToState[neutralProvince] = 6; }
        var wholeLeader = Demand(2, 3); wholeLeader.Subjugate = "puppet";
        Check(PeaceConference.BlockReason(c, war, wholeLeader).Contains("annexing whole"),
            "enemy ally land cannot stand in for an annexed subject's own remaining territory");
        var invalid = Demand(); invalid.Provinces.Add(map.ProvinceCount + 100);
        try { Check(!PeaceConference.Resolve(c, war, invalid), "invalid province command rejected without mutation"); }
        catch (Exception e) { Check(false, "invalid province must not throw: " + e.GetType().Name); }
        Check(!war.Ended && regionOwners.Skip(1).SequenceEqual(Enumerable.Range(1, 6).Select(r => map.ProvinceToNation[P(r)])),
            "refused demand changes no ownership or war outcome");
        c.Nations[2].IsHuman = true;
        Check(!PeaceOffers.Put(c, war, c.Nations[0], c.Nations[1], Demand(4)) && !war.Ended,
            "AI cannot sign away a human enemy ally's territory without consent");
        Check(!PeaceOffers.Put(c, war, c.Nations[0], c.Nations[1], PeaceTerms.Capitulate) && !war.Ended,
            "standing terms also cannot bypass a human coalition participant");
        c.Nations[2].IsHuman = false; c.Nations[3].IsHuman = true;
        Check(!PeaceOffers.Put(c, war, c.Nations[0], c.Nations[1], Demand(4)) && !war.Ended,
            "AI does not end its human ally's war behind their back");
        c.Nations[3].IsHuman = false; c.Nations[1].IsHuman = true;
        Check(PeaceOffers.Put(c, war, c.Nations[0], c.Nations[1], Demand(4)) && !war.Ended,
            "ordinary offer to human enemy still waits for an answer");
        Check(!PeaceOffers.AwaitingAnswer(war, 3) && !PeaceOffers.Answer(c, war, 3, true) && !war.Ended,
            "proposer's ally cannot accept its own coalition's proposal");
        Check(PeaceOffers.Answer(c, war, 1, false) && war.PendingOfferFromId < 0 && !war.Ended,
            "human enemy may decline normally");
        c.Nations[1].IsHuman = false;

        StrategicWar Standing()
        {
            var w = new StrategicWar { Id = c.Wars.Count + 1, AttackerId = 0, DefenderId = 1,
                StartedDay = -1000, WarScore = 100 };
            w.AttackerAllies.Add(3); w.DefenderAllies.Add(2); c.Wars.Add(w); return w;
        }
        var ownLeaderCash = c.Nations[0].Treasury;
        var enemyCash = c.Nations[1].Treasury;
        var allyCash = c.Nations[3].Treasury;
        var moneyWar = Standing();
        PeaceNegotiation.Resolve(c, moneyWar, 3, PeaceTerms.TradeConcessions);
        Check(moneyWar.Ended && c.Nations[0].Treasury == ownLeaderCash &&
              c.Nations[1].Treasury < enemyCash && c.Nations[3].Treasury > allyCash,
            "attacking ally receives concessions from enemy, never from own leader");
        Check(CampaignDiplomacy.Get(c, 3, 1).TradeAgreement && !CampaignDiplomacy.Get(c, 0, 1).TradeAgreement,
            "trade concession belongs to the actual signatories");
        var surrender = Standing();
        PeaceNegotiation.Resolve(c, surrender, 3, PeaceTerms.Capitulate);
        Check(surrender.Ended && surrender.WinnerId == 1,
            "attacking ally's surrender credits the enemy leader");
        if (failed.Count > 0) throw new Exception(string.Join("; ", failed));

        var signed = Demand(2, 4);
        Check(PeaceConference.Resolve(c, war, signed), "coalition treaty resolves through real annexation");
        Check(war.Ended && map.ProvinceToNation[P(2)] == 1 && map.ProvinceToNation[P(4)] == 1 &&
              map.ProvinceToNation[P(3)] == 2 && map.ProvinceToNation[P(5)] == 4 && map.ProvinceToNation[P(6)] == 5,
            "only demanded enemy regions change owner");
        Check(c.Nations[1].Modifiers.Any(m => m.Id == "ceded_territory") &&
              c.Nations[2].Modifiers.Any(m => m.Id == "ceded_territory") &&
              !c.Nations[4].Modifiers.Any(m => m.Id == "ceded_territory"),
            "territorial loss modifier follows actual former owners, never neutral countries");
        if (failed.Count > 0) throw new Exception(string.Join("; ", failed));
    }

    public static void RunReal(Campaign c)
    {
        void Check(bool ok, string label)
        { Console.WriteLine((ok ? "PASS " : "FAIL ") + label); if (!ok) throw new Exception(label); }
        var map = c.ProvinceMap;
        var winner = c.NationByTag("RUS");
        var defender = c.NationByTag("UKR");
        var ally = c.NationByTag("POL");
        var war = c.Wars.First(w => !w.Ended && w.Involves(winner.Id) && w.Involves(defender.Id));
        // A hypothetical coalition fixture, not a change to the shipped scenario.
        war.DefenderAllies.Add(ally.Id);
        war.WarScore = 100;
        c.Defines.PeaceCostPerProvince = 0.01f; // Permit whole regions in this test budget.
        var border = Enumerable.Range(1, map.ProvinceCount).First(p =>
            map.ProvinceToNation[p] == ally.MapIndex &&
            map.Neighbors[p].Any(n => map.ProvinceToNation[n] == winner.MapIndex));
        var region = map.ProvinceToState[border];
        foreach (var p in PeaceConference.ProvincesOfState(map, region))
        {
            war.OriginalController.TryAdd(p, c.ProvinceController[p]);
            c.SetProvinceController(p, (ushort)winner.MapIndex);
            c.ProvinceOccupation[p] = 1;
        }
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        var ledger = new PeaceLedger(c, war, winner.Id);
        AiStatecraft.WriteDemand(c, winner, war, ledger, demand, ledger.Budget * 0.85f);
        Check(demand.Provinces.Any(p => map.ProvinceToNation[p] == ally.MapIndex),
            "AI includes occupied bordering enemy-ally region on the real map");
        Check(demand.Provinces.All(p => ledger.Price(p).Cost > 0), "every AI territorial demand is priced");
        var refusal = PeaceConference.BlockReason(c, war, demand);
        Check(refusal.Length == 0, "real-map coalition draft is signable: " + refusal);
        var rounded = PeaceConference.Round(c, war, demand).Provinces;
        var before = (ushort[])map.ProvinceToNation.Clone();
        Check(PeaceConference.Resolve(c, war, demand), "real-map coalition draft resolves");
        Check(Enumerable.Range(1, map.ProvinceCount).All(p =>
                map.ProvinceToNation[p] == (rounded.Contains(p) ? winner.MapIndex : before[p])),
            "signing changes exactly the quoted provinces across the real world map");
        Check(war.CreatedStates == 0, "whole-region coalition settlement does not create region fragments");
    }
}
