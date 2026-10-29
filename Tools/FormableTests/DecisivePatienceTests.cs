using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The patience of the world with a finished war.
///
/// Measured on three seeds (WAR_CALIBRATION_2026_10_26): wars of 618 and 730 days
/// sitting at a war score of one hundred, unresolved, because the decisive gate waited
/// for an occupation map to convert to "secure" that the war itself had outlived. The
/// gate now accepts a verdict that has stood: score far past decisive, the war older
/// than the world's patience, and the winner holding two-thirds of the ground.
/// </summary>
static class DecisivePatienceTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("decisive patience: " + label);
        Console.WriteLine("PASS decisive patience: " + label);
    }

    private static (Campaign Campaign, Nation Winner, Nation Loser, ProvinceTopology Topology) World()
    {
        var campaign = new Campaign(20261027) { ScenarioId = "decisive_patience_fixture" };
        var winner = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "PAT", Name = "Winner", Population = 1_000_000,
            Gdp = 1000f, CapitalStateMapIndex = 1, Stability = .8f, WarSupport = .8f,
            Government = "presidential_republic", Ideology = "nationalism"
        };
        var loser = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "LO2", Name = "Loser", Population = 200_000,
            Gdp = 10f, CapitalStateMapIndex = 2, Stability = .3f, WarSupport = .2f,
            Government = "presidential_republic", Ideology = "nationalism"
        };
        campaign.Nations.AddRange(new[] { winner, loser });
        DemographyCatalog.Declare(winner.Tag, new[] { "german" });
        DemographyCatalog.Declare(loser.Tag, new[] { "west_slavic" });

        // A row of neighbours: one winner region, three loser regions, one winner region.
        // Three loser provinces make two-thirds a whole number of cells.
        const int width = 8, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        var regions = new[]
        {
            (Index: 1, X: 1, Owner: (ushort)1),
            (Index: 2, X: 2, Owner: (ushort)2),
            (Index: 3, X: 3, Owner: (ushort)2),
            (Index: 4, X: 4, Owner: (ushort)2),
            (Index: 5, X: 5, Owner: (ushort)1)
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
                    new PopulationGroup { Culture = row.Owner == 1 ? "german" : "west_slavic",
                        Size = row.Owner == 1 ? 1_000_000 : 100_000 }
                }
            });
        }
        var topology = ProvinceTopology.Build(states, owners, width, height, _ => false);
        campaign.AttachProvinceTopology(topology);
        // The synthetic strip's cell distances are not the world's kilometres; the peace
        // shape's reach check is not what these tests are about.
        campaign.Defines.PeaceShapeBridgeheadKm = 0f;
        winner.CapitalProvinceId = Enumerable.Range(1, topology.ProvinceCount)
            .First(p => topology.ProvinceToState[p] == 1);
        loser.CapitalProvinceId = Enumerable.Range(1, topology.ProvinceCount)
            .First(p => topology.ProvinceToState[p] == 2);
        for (var p = 1; p <= topology.ProvinceCount; p++)
            campaign.SetProvinceController(p, topology.ProvinceToNation[p]);
        return (campaign, winner, loser, topology);
    }

    public static void Run()
    {
        int[] LoserProvinces(Campaign c, ProvinceTopology t) =>
            Enumerable.Range(1, t.ProvinceCount).Where(p => t.ProvinceToNation[p] == 2).ToArray();

        // The war the measurement found: a hundred points, held ground, an occupation that
        // never converted to secure, and a war older than the world's patience.
        {
            var (c, winner, loser, topology) = World();
            var provinces = LoserProvinces(c, topology);
            var war = new StrategicWar
            {
                Id = 1, AttackerId = winner.Id, DefenderId = loser.Id,
                Justification = "conquest", StartedDay = c.ElapsedDays - 600, WarScore = 100f
            };
            for (var p = 1; p <= topology.ProvinceCount; p++)
                war.OriginalController[p] = c.ProvinceController[p];
            // Two of three held, nothing secure: control without the paperwork.
            foreach (var p in provinces.Take(2))
            {
                c.SetProvinceController(p, (ushort)winner.MapIndex);
                c.ProvinceOccupation[p] = c.Defines.OccupationSecureThreshold * 0.5f;
            }
            var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
            var ledger = new PeaceLedger(c, war, winner.Id);
            Check(AiStatecraft.WriteDecisiveSettlement(c, winner, war, ledger, demand),
                "a hundred points for six hundred days over two-thirds of the ground ends the war");
            Check(demand.Unconditional, "the verdict that has stood is not refused for want of paperwork");
            Check(provinces.All(demand.Provinces.Contains), "the whole country is in the demand");
        }

        // The same war one season old: patience not yet spent, the old gate applies.
        {
            var (c, winner, loser, topology) = World();
            var provinces = LoserProvinces(c, topology);
            var war = new StrategicWar
            {
                Id = 1, AttackerId = winner.Id, DefenderId = loser.Id,
                Justification = "conquest", StartedDay = c.ElapsedDays - 200, WarScore = 100f
            };
            for (var p = 1; p <= topology.ProvinceCount; p++)
                war.OriginalController[p] = c.ProvinceController[p];
            foreach (var p in provinces.Take(2))
            {
                c.SetProvinceController(p, (ushort)winner.MapIndex);
                c.ProvinceOccupation[p] = c.Defines.OccupationSecureThreshold * 0.5f;
            }
            var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
            var ledger = new PeaceLedger(c, war, winner.Id);
            Check(!AiStatecraft.WriteDecisiveSettlement(c, winner, war, ledger, demand),
                "a young war still waits for the occupation to convert");
        }

        // A long war that holds only half the ground: time alone does not annex what the
        // army never took.
        {
            var (c, winner, loser, topology) = World();
            var provinces = LoserProvinces(c, topology);
            var war = new StrategicWar
            {
                Id = 1, AttackerId = winner.Id, DefenderId = loser.Id,
                Justification = "conquest", StartedDay = c.ElapsedDays - 700, WarScore = 100f
            };
            for (var p = 1; p <= topology.ProvinceCount; p++)
                war.OriginalController[p] = c.ProvinceController[p];
            foreach (var p in provinces.Take(provinces.Length / 2))
            {
                c.SetProvinceController(p, (ushort)winner.MapIndex);
                c.ProvinceOccupation[p] = c.Defines.OccupationSecureThreshold * 0.5f;
            }
            var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
            var ledger = new PeaceLedger(c, war, winner.Id);
            Check(!AiStatecraft.WriteDecisiveSettlement(c, winner, war, ledger, demand),
                "a score of a hundred over half the ground is not a conquest");
        }

        // A repelled aggression is the defender's victory: liberation and money, never
        // annexation. The defender (the war's target) wins on points; its own provinces
        // still under the attacker's boot are handed back with reparations on top.
        {
            var (c, winner, loser, topology) = World();
            // This time the ATTACKER is the measured loser: winner here is the side that
            // was invaded, so build the war the other way round.
            var war = new StrategicWar
            {
                Id = 1, AttackerId = loser.Id, DefenderId = winner.Id,
                Justification = "conquest", StartedDay = c.ElapsedDays - 600,
                // Negative from the attacker's side: the defender leads by 47.
                WarScore = -47f
            };
            for (var p = 1; p <= topology.ProvinceCount; p++)
                war.OriginalController[p] = c.ProvinceController[p];
            // The attacker still stands on one province of the defender's own.
            var winnerProvinces = Enumerable.Range(1, topology.ProvinceCount)
                .Where(p => topology.ProvinceToNation[p] == winner.MapIndex).ToArray();
            Check(winnerProvinces.Length >= 2, "the defender has ground of its own");
            c.SetProvinceController(winnerProvinces[0], (ushort)loser.MapIndex);
            c.ProvinceOccupation[winnerProvinces[0]] = c.Defines.OccupationSecureThreshold * 0.5f;
            var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
            var ledger = new PeaceLedger(c, war, winner.Id);
            Check(AiStatecraft.WriteDecisiveSettlement(c, winner, war, ledger, demand),
                "a war the invader is losing by forty-seven points is the defender's victory");
            Check(demand.Provinces.Contains(winnerProvinces[0]) &&
                  demand.Provinces.All(p => topology.ProvinceToNation[p] == winner.MapIndex),
                "the settlement liberates the defender's ground and takes none of the aggressor's");
            Check(!demand.Unconditional, "a repelled aggression is not an capitulation");
            Check(ledger.Budget > 1f, "the verdict still has a budget to spend on terms");
        }

        // The Cuba rule: a holder of the deterrent is never conquered by another holder.
        // Both sides hold tier 1 (the technology, not the authored club - acquisition in
        // play is research, and research is a list); the war ends in limited terms.
        {
            var (c, winner, loser, topology) = World();
            winner.Technologies.Add("nuclear_deterrence");
            loser.Technologies.Add("nuclear_deterrence");
            Check(Deterrence.Brinkmanship(c, null) == 0f, "no war, no shadow");
            var war = new StrategicWar
            {
                Id = 1, AttackerId = winner.Id, DefenderId = loser.Id,
                Justification = "conquest", StartedDay = c.ElapsedDays - 600, WarScore = 100f
            };
            for (var p = 1; p <= topology.ProvinceCount; p++)
                war.OriginalController[p] = c.ProvinceController[p];
            Check(Deterrence.Brinkmanship(c, war) == 1f, "two tier-1 holders cast a shadow of one");
            var provinces = LoserProvinces(c, topology);
            foreach (var p in provinces.Take(2))
            {
                c.SetProvinceController(p, (ushort)winner.MapIndex);
                c.ProvinceOccupation[p] = c.Defines.OccupationSecureThreshold * 1.2f;
            }
            var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
            var ledger = new PeaceLedger(c, war, winner.Id);
            Check(AiStatecraft.WriteDecisiveSettlement(c, winner, war, ledger, demand),
                "a holders' war still ends - the world pushes");
            Check(!demand.Unconditional &&
                  demand.Provinces.All(p => topology.ProvinceToNation[p] == winner.MapIndex),
                "and it ends without the aggressor's homeland: liberation only");
            // The define switches the shadow off, and with it the door opens.
            c.Defines.MutualDeterrenceEnabled = false;
            var demand2 = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
            var ledger2 = new PeaceLedger(c, war, winner.Id);
            Check(Deterrence.Brinkmanship(c, war) == 0f, "no deterrent, no shadow");
            Check(AiStatecraft.WriteDecisiveSettlement(c, winner, war, ledger2, demand2) &&
                  demand2.Provinces.Any(p => topology.ProvinceToNation[p] == loser.MapIndex),
                "with the wall's door open the conquest road returns");
        }

        // The proliferation race: a country facing a rival holder reaches for a deterrent
        // of its own ahead of every cheaper thing on the shelf; a country at peace with
        // the club, or one that already holds one, shops the ordinary way.
        {
            var (c, winner, loser, topology) = World();
            loser.Technologies.Add("nuclear_deterrence");
            var rival = CampaignDiplomacy.Get(c, winner.Id, loser.Id);
            rival.Stance = DiplomaticStance.Rival;
            Check(ResearchSystem.Choose(c, winner)?.Id == "nuclear_deterrence",
                "a rival of a holder reaches for the deterrent first");
            rival.Stance = DiplomaticStance.Neutral;
            Check(ResearchSystem.Choose(c, winner)?.Id != "nuclear_deterrence",
                "a country at peace with the club shops the ordinary shelf");
            winner.Technologies.Add("nuclear_deterrence");
            rival.Stance = DiplomaticStance.Rival;
            Check(ResearchSystem.Choose(c, winner)?.Id != "nuclear_deterrence",
                "a holder already in the club does not buy a second bomb");
        }

        // The patience is a define: a world that has none settles the same war at once.
        {
            var (c, winner, loser, topology) = World();
            c.Defines.AiPeaceDecisivePatienceDays = 1;
            var provinces = LoserProvinces(c, topology);
            var war = new StrategicWar
            {
                Id = 1, AttackerId = winner.Id, DefenderId = loser.Id,
                Justification = "conquest", StartedDay = c.ElapsedDays - 30, WarScore = 95f
            };
            for (var p = 1; p <= topology.ProvinceCount; p++)
                war.OriginalController[p] = c.ProvinceController[p];
            foreach (var p in provinces.Take(2))
            {
                c.SetProvinceController(p, (ushort)winner.MapIndex);
                c.ProvinceOccupation[p] = c.Defines.OccupationSecureThreshold * 0.5f;
            }
            var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
            var ledger = new PeaceLedger(c, war, winner.Id);
            Check(AiStatecraft.WriteDecisiveSettlement(c, winner, war, ledger, demand),
                "the patience of the world is a define");
        }
    }
}
