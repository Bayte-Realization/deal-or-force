using System;
using System.Linq;
using DealOrForce.Simulation;

static class PeaceIntentTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception(note); Console.WriteLine("PASS peace intent: " + note); }

    public static void Run()
    {
        var c = new Campaign(20261015) { ScenarioId = "peace_intent_fixture" };
        var winner = new Nation { Id = 0, MapIndex = 1, Tag = "PWA", Name = "Winner", Stability = .9f, WarSupport = .9f, Gdp = 100 };
        var loser = new Nation { Id = 1, MapIndex = 2, Tag = "PLO", Name = "Loser", Gdp = 100 };
        c.Nations.AddRange(new[] { winner, loser });
        var states = new ushort[8 * 3]; var owners = new ushort[states.Length];
        foreach (var (x, region, owner) in new[] { (1, 1, 1), (2, 2, 2), (3, 3, 2), (6, 7, 2) })
        {
            states[8 + x] = (ushort)region; owners[8 + x] = (ushort)owner;
            c.States.Add(new ScenarioState { MapIndex = region, Id = "region_" + region,
                Name = "Region " + region, NationMapIndex = owner, Population = 100000 });
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 8, 3, _ => false));
        winner.CapitalStateMapIndex = 1; loser.CapitalStateMapIndex = 3;
        c.Defines.PeaceWholeRegions = true;
        // A remote occupied pocket is not a new land border. No coastal bridgeheads in this fixture.
        c.Defines.PeaceShapeAllowCoastalBridgeheads = false;
        var map = c.ProvinceMap;
        int Province(int region) => Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToState[p] == region);
        var front = Province(2); var rear = Province(3); var pocket = Province(7);
        var war = new StrategicWar { Id = 1, AttackerId = winner.Id, DefenderId = loser.Id, ClaimedStateMapIndex = 3, WarScore = 100 };
        c.Wars.Add(war);
        foreach (var p in new[] { front, rear, pocket })
        { c.SetProvinceController(p, (ushort)winner.MapIndex); c.ProvinceOccupation[p] = 1f; }
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        demand.Provinces.Add(front);
        Check(!AiStatecraft.WantsLongTruce(c, winner, war, demand), "an unfinished declared objective does not buy an automatic decade of quiet");
        demand.Provinces.Add(rear);
        Check(AiStatecraft.WantsLongTruce(c, winner, war, demand), "a completed objective can justify the longer settlement");
        c.States.First(s => s.MapIndex == 7).CoreTags.Add(winner.Tag);
        Check(!AiStatecraft.WantsLongTruce(c, winner, war, demand), "a remaining homeland claim also remains a strategic interest");
        winner.Stability = 0;
        Check(AiStatecraft.WantsLongTruce(c, winner, war, demand), "domestic exhaustion can outweigh unfinished claims");
        winner.Stability = .9f; winner.WarSupport = 0;
        Check(AiStatecraft.WantsLongTruce(c, winner, war, demand), "low war support favours a longer recovery");
        winner.WarSupport = .9f; demand.Subjugate = "protectorate";
        Check(AiStatecraft.WantsLongTruce(c, winner, war, demand), "a complete dependent settlement is not treated as an unfinished annexation");
        demand.Subjugate = ""; demand.Provinces.Clear(); demand.Provinces.Add(front); demand.Provinces.Add(pocket);
        var before = CampaignFingerprint.Of(c);
        var rounded = PeaceConference.Round(c, war, demand);
        Check(rounded.Provinces.Contains(front) && !rounded.Provinces.Contains(pocket), "the final AI treaty drops a disconnected occupied pocket");
        Check(demand.Provinces.Contains(pocket) && CampaignFingerprint.Of(c) == before, "treaty inspection leaves the draft and campaign untouched");
        demand.ReleaseRegions = true;
        Check(PeaceConference.Round(c, war, demand).Provinces.Contains(pocket), "liberation is not forced to border its sponsor");
        demand.ReleaseRegions = false; winner.IsHuman = true;
        Check(PeaceConference.Round(c, war, demand).Provinces.Contains(pocket), "a human's explicit painted treaty is not silently rewritten");
    }
}
