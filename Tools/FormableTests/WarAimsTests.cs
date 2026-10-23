using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class WarAimsTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("war aims: " + label);
        Console.WriteLine("PASS war aims: " + label);
    }

    public static void Run()
    {
        var campaign = new Campaign(20261019) { ScenarioId = "war_aims_fixture" };
        var parent = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "PAR", Name = "Parent", Population = 1_000_000,
            Gdp = 100, CapitalStateMapIndex = 1, Stability = .8f, WarSupport = .6f
        };
        var rebel = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "REB", Name = "Rising", Population = 300_000,
            Gdp = 30, CapitalStateMapIndex = 2, SecededFromId = parent.Id,
            Stability = .8f, WarSupport = .9f
        };
        campaign.Nations.AddRange(new[] { parent, rebel });
        rebel.FoundedCultures.Add("tigrayan");
        DemographyCatalog.Declare(rebel.Tag, new[] { "tigrayan" });
        campaign.Homelands[rebel.Id] = new List<int> { 2 };

        const int width = 8, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        var regions = new[]
        {
            (Index: 1, X: 1, Owner: (ushort)1, Tigrayan: 0L, Other: 100_000L),
            (Index: 2, X: 2, Owner: (ushort)2, Tigrayan: 60_000L, Other: 40_000L),
            (Index: 3, X: 3, Owner: (ushort)1, Tigrayan: 70_000L, Other: 30_000L),
            (Index: 4, X: 4, Owner: (ushort)1, Tigrayan: 0L, Other: 100_000L),
            (Index: 5, X: 5, Owner: (ushort)1, Tigrayan: 70_000L, Other: 30_000L)
        };
        foreach (var row in regions)
        {
            var cell = height * width / 2 + row.X;
            states[cell] = (ushort)row.Index;
            owners[cell] = row.Owner;
            var region = new ScenarioState
            {
                MapIndex = row.Index,
                Id = "region_" + row.Index,
                Name = "Region " + row.Index,
                NationMapIndex = row.Owner,
                Population = row.Tigrayan + row.Other
            };
            if (row.Tigrayan > 0)
                region.PopulationGroups.Add(new PopulationGroup { Culture = "tigrayan", Size = row.Tigrayan });
            if (row.Other > 0)
                region.PopulationGroups.Add(new PopulationGroup { Culture = "other", Size = row.Other });
            campaign.States.Add(region);
        }
        var topology = ProvinceTopology.Build(states, owners, width, height, _ => false);
        campaign.AttachProvinceTopology(topology);
        int ProvinceOf(int region) => Enumerable.Range(1, topology.ProvinceCount)
            .First(p => topology.ProvinceToState[p] == region);
        var own = ProvinceOf(2);
        var coEthnic = ProvinceOf(3);
        var unrelated = ProvinceOf(4);
        var remote = ProvinceOf(5);
        Check(topology.Neighbors[own].Contains(coEthnic), "the fixture puts the national neighbour on the rising's border");
        Check(!topology.Neighbors[own].Contains(unrelated), "the unrelated neighbour is not on the rising's border");
        Check(!topology.Neighbors[own].Contains(remote), "the remote co-ethnic region is not a border claim");


        var war = new StrategicWar
        {
            Id = 1, AttackerId = parent.Id, DefenderId = rebel.Id, CivilConflict = true,
            ClaimedStateMapIndex = 2, WarScore = -100f, DefenderWarSupport = 0f, StartedDay = 0
        };
        campaign.Wars.Add(war);

        Check(WarAims.Of(campaign, war, rebel) == WarAimKind.Secessionist,
            "a civil conflict is read as a secessionist war");
        war.Justification = "revolution";
        Check(WarAims.Of(campaign, war, rebel) == WarAimKind.Revolutionary,
            "a revolutionary rising is read separately from a national secession");
        war.Justification = "reconquest"; war.CivilConflict = false;
        Check(WarAims.Of(campaign, war, parent) == WarAimKind.Reconquest,
            "a war to recover lost ground is read as a reconquest");
        war.CivilConflict = true; war.Justification = "";
        Check(WarAims.RebelClaim(campaign, war, rebel, campaign.States[2]),
            "a rising may claim a bordering region of its own people");
        Check(!WarAims.RebelClaim(campaign, war, rebel, campaign.States[3]),
            "an unrelated bordering region is not a national claim");
        Check(!WarAims.RebelClaim(campaign, war, rebel, campaign.States[4]),
            "a co-ethnic region behind another region is not a border claim");
        Check(!WarAims.RebelClaim(campaign, war, parent, campaign.States[2]),
            "the old state does not inherit the rising's national claim");
        Check(Math.Abs(WarAims.PeaceFloor(campaign, rebel) - 18f) < 0.01f &&
              Math.Abs(WarAims.PeaceFloor(campaign, parent) - 35f) < 0.01f,
            "a rising may negotiate a small national peace while ordinary wars keep the higher floor");

        var ledger = new PeaceLedger(campaign, war, rebel.Id);
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = rebel.Id };
        PeaceShape.Write(campaign, rebel, war, ledger, demand, ledger.Budget);
        Check(demand.Provinces.Contains(coEthnic),
            "a winning rising writes its adjacent national claim into the treaty");
        Check(!demand.Provinces.Contains(unrelated) && !demand.Provinces.Contains(remote),
            "the treaty does not invent unrelated or disconnected ground");
        Check(AiStatecraft.StrategicWarAim(campaign, rebel, war, campaign.States[2]),
            "the AI presses the national claim instead of settling for the old line");
        Check(!AiStatecraft.StrategicWarAim(campaign, rebel, war, campaign.States[3]),
            "the AI does not press an unrelated neighbour's ground");

        // A decisive rising that has overrun the parent state takes it whole, seat or no
        // seat. The old state keeps only its capital; the country has still been overrun.
        var parentRegion = regions.First(region => region.Owner == parent.MapIndex);
        parent.CapitalProvinceId = Enumerable.Range(1, topology.ProvinceCount)
            .First(p => topology.ProvinceToState[p] != 2 && topology.ProvinceToNation[p] == (ushort)parent.MapIndex);
        foreach (var region in regions.Where(region => region.Owner == parent.MapIndex))
            foreach (var province in Enumerable.Range(1, topology.ProvinceCount)
                         .Where(p => topology.ProvinceToState[p] == region.Index &&
                                     p != parent.CapitalProvinceId))
            {
                campaign.SetProvinceController(province, (ushort)rebel.MapIndex);
                campaign.ProvinceOccupation[province] = 1f;
            }
        var decisive = new PeaceDemand { WarId = war.Id, ProposerId = rebel.Id };
        Check(AiStatecraft.WriteDecisiveSettlement(campaign, rebel, war, ledger, decisive),
            "a decisive rising that has overrun the parent state absorbs it");

        // And when the rising takes the government's own seat, the war ends with one
        // country again: the winner absorbs the whole old state rather than carving it.
        campaign.SetProvinceController(parent.CapitalProvinceId, (ushort)rebel.MapIndex);
        var seat = new PeaceDemand { WarId = war.Id, ProposerId = rebel.Id };
        Check(AiStatecraft.WriteDecisiveSettlement(campaign, rebel, war, ledger, seat),
            "a rising holding the government seat absorbs the whole old state");
        var absorbed = seat.Provinces.Count(province => topology.ProvinceToNation[province] == (ushort)parent.MapIndex);
        Check(absorbed == seat.Provinces.Count && seat.Subjugate == "",
            "the absorption is the whole old state, annexed outright");

        var clamp = new Defines();
        Check(WarScoring.ClampScore(clamp, 170f) == 100f && WarScoring.ClampScore(clamp, -170f) == -100f,
            "war score is bounded at plus and minus one hundred");
        clamp.WarScoreMax = 80f; clamp.WarScoreMin = -60f;
        Check(WarScoring.ClampScore(clamp, 170f) == 80f && WarScoring.ClampScore(clamp, -170f) == -60f,
            "a mod can narrow the same public bounds without changing the code");
        Check(WarScoring.ClampScore(clamp, float.NaN) == 0f, "an invalid score becomes zero rather than a corrupt war");
    }
}
