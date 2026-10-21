using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Occupation is not free. The slip rule is held in the hands as a pure function first,
/// then put into the smallest world that can be occupied: one war ledger, one held
/// province, one garrison. The fixture honours the three traps every hermetic world here
/// has: regions added by hand drop the lazy state index, the raster is large enough for
/// grown provinces, and province ids are found through the province-to-state ledger
/// instead of being invented.
/// </summary>
static class OccupationResistanceTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("occupation resistance: " + label); Console.WriteLine("PASS occupation resistance: " + label); }

    public static void Run()
    {
        Pure();
        World();
    }

    static void Pure()
    {
        var d = new Defines { OccupationResistanceEnabled = true };
        Check(OccupationResistance.Slip(0.8f, 0.5f, 0f, d) > 0f,
            "an ungarrisoned occupation slips");
        Check(OccupationResistance.Slip(0.8f, 0.9f, 0f, d) > OccupationResistance.Slip(0.8f, 0.1f, 0f, d),
            "a radical population slips faster than a calm one");
        Check(OccupationResistance.Slip(0.8f, 0.5f, 9f, d) < OccupationResistance.Slip(0.8f, 0.5f, 0f, d),
            "a garrison suppresses the slip");
        Check(OccupationResistance.Slip(0f, 0.5f, 0f, d) == 0f,
            "there is nothing to slip where nothing is held");
        Check(OccupationResistance.Slip(0.01f, 1f, 0f, d) <= 0.01f,
            "the slip can never exceed the occupation itself");
        var off = new Defines();
        Check(!off.OccupationResistanceEnabled && OccupationResistance.Slip(0.8f, 0.5f, 0f, off) == 0f,
            "with the rule off occupation stays free, as it has always been");
        Check(OccupationResistance.PartisanCasualties(15000f, 0.8f, 0.8f, d) > 0f,
            "partisans cost the garrison people where the province is radical and held");
        Check(OccupationResistance.PartisanCasualties(15000f, 0.9f, 0.8f, d) >
              OccupationResistance.PartisanCasualties(15000f, 0.3f, 0.8f, d),
            "an angrier population bleeds the garrison faster");
        Check(OccupationResistance.PartisanCasualties(15000f, 0f, 0.8f, d) == 0f,
            "a calm province breeds no partisans");
        Check(OccupationResistance.PartisanCasualties(15000f, 0.8f, 0.8f, d) <= 15000f,
            "a week of ambushes cannot eat more men than the formation has");
        Check(OccupationResistance.PartisanCasualties(15000f, 0.8f, 0.8f, off) == 0f,
            "with the rule off the rear areas are perfectly safe");

        var collab = new ScenarioState { Id = "collab", Name = "Collab", MapIndex = 3009,
            NationMapIndex = 501, NationId = "RS0", Population = 1_000_000 };
        collab.PopulationGroups.Add(new PopulationGroup { Culture = "russian", Size = 600_000 });
        collab.PopulationGroups.Add(new PopulationGroup { Culture = "ukrainian", Size = 400_000 });
        Check(OccupationResistance.Collaboration(collab, "RUS", d) > 0f,
            "the occupier's own people in a region damp its resistance");
        Check(OccupationResistance.Collaboration(collab, "RS1", d) == 0f,
            "a house with no people in the region gets no collaboration");
        Check(OccupationResistance.Collaboration(collab, "RUS", off) == 0f,
            "with the rule off collaboration damps nothing");
    }

    static void World()
    {
        var c = Fixture();
        var war = c.Wars[0];
        var topology = c.ProvinceMap;
        var province = Enumerable.Range(1, topology.ProvinceCount).First(p => topology.ProvinceToState[p] == 3002);
        var own = Enumerable.Range(1, topology.ProvinceCount).First(p => topology.ProvinceToState[p] == 3001);

        c.SetProvinceController(province, 502);
        war.OriginalController.TryAdd(province, 501);
        war.OriginalController.TryAdd(own, 501);
        c.ProvinceOccupation[province] = 0.8f;
        OccupationResistance.Tick(c);
        var bareSlip = 0.8f - c.ProvinceOccupation[province];
        Check(bareSlip > 0f, "a week of radical rule takes the ungarrisoned occupation down");
        var parallelWar = new StrategicWar { Id = 2, AttackerId = 0, DefenderId = 1 };
        parallelWar.OriginalController.TryAdd(province, 501);
        c.Wars.Add(parallelWar);
        c.ProvinceOccupation[province] = .8f;
        OccupationResistance.Tick(c);
        Check(MathF.Abs((.8f - c.ProvinceOccupation[province]) - bareSlip) < .000001f,
            "parallel war ledgers cannot charge the same province twice in one weekly pass");
        c.Wars.Remove(parallelWar);

        // The same province with the occupier's own formation standing in it: still slips,
        // but a fraction of what it did with nobody there. Holding ground costs a soldier.
        c.ProvinceOccupation[province] = 0.8f;
        var posted = new Army { Id = 91, NationId = c.Nations[1].Id, ProvinceId = province, Divisions = 9f };
        c.Armies.Add(posted);
        var peopleBefore = posted.Personnel;
        SetDay(c, 19);
        OccupationResistance.Tick(c);
        var heldSlip = 0.8f - c.ProvinceOccupation[province];
        Check(heldSlip > 0f && heldSlip < bareSlip,
            "the occupier's formation slows the melt but does not stop time");
        Check(posted.Personnel < peopleBefore && posted.Recovering > 0f,
            "the week the hold slipped the garrison bled, and its wounded are on the way back");

        c.Defines.OccupationResistanceEnabled = false;
        c.ProvinceOccupation[province] = 0.8f;
        SetDay(c, 26);
        OccupationResistance.Tick(c);
        Check(c.ProvinceOccupation[province] == 0.8f, "with the rule off the occupation never slips");

        // Ground the owner still holds is nobody's business: the ledger skips it, even
        // with an occupation number standing in the array from a war that walked through.
        c.Defines.OccupationResistanceEnabled = true;
        c.ProvinceOccupation[own] = 0.5f;
        SetDay(c, 33);
        OccupationResistance.Tick(c);
        Check(c.ProvinceOccupation[own] == 0.5f, "ground the owner still holds does not slip");

        // A calm province keeps its slow slip (the baseline appetite) but breeds no
        // partisans: the garrison stands there unmolested.
        var east = c.States.Find(s => s.MapIndex == 3002);
        east.Radicalism = 0f;
        c.ProvinceOccupation[province] = 0.8f;
        var calmBefore = posted.Personnel;
        SetDay(c, 40);
        OccupationResistance.Tick(c);
        Check(posted.Personnel == calmBefore && c.ProvinceOccupation[province] < 0.8f,
            "a calm province breeds no partisans, though the hold still settles");

        // The occupier's own people: the region turns part co-ethnic and the slip shrinks.
        // Ungarrisoned on purpose, so the only thing that changes between the two weeks is
        // who lives in the region.
        c.Armies.Clear();
        east.Radicalism = 0.6f;
        c.ProvinceOccupation[province] = 0.8f;
        SetDay(c, 47);
        OccupationResistance.Tick(c);
        var slipStrangers = 0.8f - c.ProvinceOccupation[province];
        c.ProvinceOccupation[province] = 0.8f;
        east.PopulationGroups.Add(new PopulationGroup { Culture = "russian", Size = 5_000_000 });
        c.Nations[1].Tag = "RUS";
        SetDay(c, 54);
        OccupationResistance.Tick(c);
        var slipKin = 0.8f - c.ProvinceOccupation[province];
        Check(slipKin > 0f && slipKin < slipStrangers,
            "where the occupier's own people live, resistance speaks with a smaller voice");
    }

    static Campaign Fixture()
    {
        var defines = new Defines { OccupationResistanceEnabled = true };
        var c = new Campaign(824, defines) { ScenarioId = "resistance_fixture" };
        c.AddNation(new Nation { Tag = "RS0", MapIndex = 501, Population = 20_000_000, Gdp = 100, Treasury = 10, OutputScale = 1 });
        c.AddNation(new Nation { Tag = "RS1", MapIndex = 502, Population = 20_000_000, Gdp = 100, Treasury = 10, OutputScale = 1 });
        c.States.Add(new ScenarioState { Id = "resistance_west", Name = "Resistance west", MapIndex = 3001,
            NationMapIndex = 501, NationId = "RS0", Population = 10_000_000, Radicalism = 0.6f });
        c.States.Add(new ScenarioState { Id = "resistance_east", Name = "Resistance east", MapIndex = 3002,
            NationMapIndex = 501, NationId = "RS0", Population = 10_000_000, Radicalism = 0.6f });
        c.InvalidateStateIndex();
        var cells = Enumerable.Range(0, 800).Select(i => (ushort)(i < 400 ? 3001 : 3002)).ToArray();
        var owners = Enumerable.Range(0, 800).Select(_ => (ushort)501).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, 40, 20, _ => false));
        var war = new StrategicWar { Id = 1, AttackerId = c.Nations[1].Id, DefenderId = c.Nations[0].Id };
        c.Wars.Add(war);
        SetDay(c, 12);
        return c;
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
}

