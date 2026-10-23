using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// When a country may swallow another one whole.
///
/// The checks are about the edges: a people split by a border, a faction and its parent,
/// and a decisive victory whose last province the war score could not buy. The bug they
/// are written against is a peace that always left the loser standing because the
/// arithmetic of a border bargain was applied to the end of a country.
/// </summary>
static class AnnexationTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("annexation: " + label);
        Console.WriteLine("PASS annexation: " + label);
    }

    public static void Run()
    {
        var campaign = new Campaign(20261019) { ScenarioId = "annexation_fixture" };
        var germany = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "ANX", Name = "Germany", Population = 1_000_000,
            Gdp = 1000f, CapitalStateMapIndex = 1, Stability = .8f, WarSupport = .8f,
            Government = "presidential_republic", Ideology = "nationalism"
        };
        var austria = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "AN2", Name = "Austria", Population = 200_000,
            Gdp = 10f, CapitalStateMapIndex = 2, Stability = .8f, WarSupport = .8f,
            Government = "presidential_republic", Ideology = "nationalism"
        };
        campaign.Nations.AddRange(new[] { germany, austria });
        DemographyCatalog.Declare(germany.Tag, new[] { "german" });
        DemographyCatalog.Declare(austria.Tag, new[] { "german" });

        // Four regions in a row: Germany, Austria, Austria, Germany. Austria has two
        // provinces, so a half-occupied country is a real share rather than one cell.
        const int width = 8, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        var regions = new[]
        {
            (Index: 1, X: 1, Owner: (ushort)1),
            (Index: 2, X: 2, Owner: (ushort)2),
            (Index: 3, X: 3, Owner: (ushort)2),
            (Index: 4, X: 4, Owner: (ushort)1)
        };
        foreach (var row in regions)
        {
            var cell = height * width / 2 + row.X;
            states[cell] = (ushort)row.Index;
            owners[cell] = row.Owner;
            campaign.States.Add(new ScenarioState
            {
                MapIndex = row.Index, Id = "region_" + row.Index, Name = "Region " + row.Index,
                NationMapIndex = row.Owner, Population = row.Owner == 1 ? 1_000_000 : 100_000,
                PopulationGroups =
                {
                    new PopulationGroup { Culture = "german", Size = row.Owner == 1 ? 1_000_000 : 100_000 }
                }
            });
        }
        var topology = ProvinceTopology.Build(states, owners, width, height, _ => false);
        campaign.AttachProvinceTopology(topology);
        int ProvinceOf(int region) => Enumerable.Range(1, topology.ProvinceCount)
            .First(p => topology.ProvinceToState[p] == region);
        germany.CapitalProvinceId = ProvinceOf(1);
        austria.CapitalProvinceId = ProvinceOf(2);
        for (var p = 1; p <= topology.ProvinceCount; p++)
            campaign.SetProvinceController(p, topology.ProvinceToNation[p]);


        Check(AnnexationAmbition.IsReunification(campaign, germany, austria),
            "Germany and Austria are two halves of one people");
        Check(!AnnexationAmbition.IsReunification(campaign, germany, campaign.NationByTag("SRB")),
            "Serbia is not a German reunification target");

        // The weaker half does not plan to swallow the stronger one.
        Check(AnnexationAmbition.Aim(campaign, austria, germany) is null,
            "Austria does not plan to swallow Germany");

        var aim = AnnexationAmbition.Aim(campaign, germany, austria);
        Check(aim is { Cause: "reunification" }, "a strong Germany aims to reunite with Austria");
        Check(aim is { } chosen && chosen.ClaimedState == 2,
            "the war is aimed at the Austrian capital");

        // A faction and its parent, or two factions of one parent, are a civil war.
        var parent = new Nation { Id = 9001, Tag = "PAR", Name = "Parent" };
        var rebel = new Nation { Id = 9002, Tag = "REB", Name = "Rebel", SecededFromId = parent.Id };
        var rival = new Nation { Id = 9003, Tag = "RI2", Name = "Rival", SecededFromId = parent.Id };
        Check(AnnexationAmbition.IsCivilWarBetween(rebel, parent),
            "a faction and its parent are a civil war");
        Check(AnnexationAmbition.IsCivilWarBetween(rebel, rival),
            "two factions of one parent are a civil war");
        Check(!AnnexationAmbition.IsCivilWarBetween(parent, new Nation { Id = 9004, Tag = "OTH" }),
            "unrelated countries are not a civil war");

        // A decisive reunification does not leave a rump because the war score cannot buy
        // the last province: the rest of the government capitulates.
        var war = new StrategicWar
        {
            Id = 1, AttackerId = germany.Id, DefenderId = austria.Id,
            Justification = "reunification", StartedDay = campaign.ElapsedDays - 100, WarScore = 60f
        };
        for (var p = 1; p <= topology.ProvinceCount; p++)
            war.OriginalController[p] = campaign.ProvinceController[p];
        var austrianProvinces = Enumerable.Range(1, topology.ProvinceCount)
            .Where(p => topology.ProvinceToNation[p] == austria.MapIndex).ToArray();
        Check(austrianProvinces.Length > 1, "Austria has more than one province");
        campaign.SetProvinceController(austrianProvinces[0], (ushort)germany.MapIndex);
        campaign.ProvinceOccupation[austrianProvinces[0]] = 1f;
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = germany.Id };
        var ledger = new PeaceLedger(campaign, war, germany.Id);
        var decisive = AiStatecraft.WriteDecisiveSettlement(campaign, germany, war, ledger, demand);
        Check(decisive, "a decisive reunification writes the whole country");
        Check(demand.Unconditional, "and is not refused for want of war score");
        Check(austrianProvinces.All(demand.Provinces.Contains), "every Austrian province is in the demand");

        var block = PeaceConference.BlockReason(campaign, war, demand);
        Check(block.Length == 0, "the conference signs an unconditional capitulation: " + block);

        // A strong, unrestrained government can also name a conquest without a national claim.
        DemographyCatalog.Declare(austria.Tag, new[] { "persian" });
        foreach (var state in campaign.StatesOf(austria))
        {
            state.PopulationGroups.Clear();
            state.PopulationGroups.Add(new PopulationGroup { Culture = "persian", Size = state.Population });
        }
        germany.Government = "military_junta";
        var conquest = AnnexationAmbition.Aim(campaign, germany, austria);
        Check(AnnexationAmbition.HegemonyCause(campaign, germany, austria),
            "a strong junta can name a conquest of a weak neighbour");
        Check(conquest is { Cause: "hegemony" }, "the conquest is declared as hegemony");

        var hegemony = new StrategicWar
        {
            Id = 2, AttackerId = germany.Id, DefenderId = austria.Id, Justification = "hegemony"
        };
        Check(WarAims.Of(campaign, hegemony, germany) == WarAimKind.Total,
            "a hegemony war is read as a total war");
        Check(AiStatecraft.StrategicWarAim(campaign, germany, hegemony, campaign.StatesOf(austria)[0]),
            "and every enemy region is a strategic aim");
    }
}
