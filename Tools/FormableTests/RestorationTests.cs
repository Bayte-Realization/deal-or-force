using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class RestorationTests
{
    static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS restoration: " + label); }

    // A country that lost the last of its ground and governs from abroad, and the country that
    // holds its homeland now. The exile owns nothing; its remembered homeland is region 3001.
    static (Campaign c, Nation exile, Nation holder, ScenarioState home, ScenarioState far) Fixture()
    {
        var c = new Campaign(777) { ScenarioId = "restoration_fixture" };
        var exile = new Nation
        {
            Id = 0, Tag = "QRA", Name = "Oldland", MapIndex = 501, Population = 0, Gdp = 0.05f,
            Treasury = 1f, Stability = .6f, Government = "parliamentary_republic", Ideology = "liberalism",
            Color = "#123456"
        };
        var holder = new Nation
        {
            Id = 1, Tag = "QRB", Name = "Holder", MapIndex = 502, Population = 1000000,
            Gdp = 1000f, Treasury = 100f, Stability = .8f
        };
        c.Nations.Add(exile); c.Nations.Add(holder);

        var home = new ScenarioState { MapIndex = 3001, NationMapIndex = 502, Id = "old_home", Name = "Old Home", Population = 500000 };
        home.PopulationGroups.Add(new PopulationGroup { Culture = "russian", Religion = "orthodox", Size = 500000 });
        var far = new ScenarioState { MapIndex = 3002, NationMapIndex = 502, Id = "far_land", Name = "Far Land", Population = 500000 };
        far.PopulationGroups.Add(new PopulationGroup { Culture = "russian", Religion = "orthodox", Size = 500000 });
        c.States.Add(home); c.States.Add(far);

        var states = new ushort[24]; var owners = new ushort[24];
        for (var y = 0; y < 4; y++) for (var x = 0; x < 6; x++)
        { states[y * 6 + x] = (ushort)(x < 3 ? 3001 : 3002); owners[y * 6 + x] = 502; }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 6, 4, _ => false));

        // The ground it held when the world first saw it, and the country it still is.
        c.Homelands[exile.Id] = new List<int> { 3001 };
        exile.Movement = new MovementState
        {
            Id = "", Exile = true, Landless = true, Dissolved = false,
            HeadquartersStateMapIndex = 3001, Legitimacy = .6f, Cadres = 4000f
        };
        exile.Movement.Homeland.Add("old_home");
        exile.Movement.Peoples.Add("russian");
        // A friend holds the homeland, and would give it back.
        CampaignDiplomacy.Get(c, holder.Id, exile.Id).Opinion = 50f;
        c.ConfigureSinglePlayer(holder.Id);
        return (c, exile, holder, home, far);
    }

    public static void Run()
    {
        DemographyCatalog.ResetCatalog(); Loc.ResetCatalog(); OpinionCatalog.ResetCatalog();
        MovementCatalog.ResetCatalog();

        // ---- the way home: a friend hands the homeland back -------------------------------
        var (c, exile, holder, home, far) = Fixture();
        Check(Restoration.Refusal(c, exile, home) == "" && Restoration.CanRestore(c, exile, home),
            "a government in exile can be restored to its own homeland when the holder will give it back");
        Check(Restoration.Refusal(c, exile, far) != "",
            "a region that is not its homeland cannot be a restoration");
        Check(Restoration.BestRegion(c, exile)?.MapIndex == home.MapIndex,
            "the AI's restoration target is the homeland it may actually return to");
        Check(Restoration.Consents(c, exile, holder), "a friendly holder consents to the hand-back");

        var beforeTag = exile.Tag; var beforeName = exile.Name; var beforeColour = exile.Color;
        var beforeGovernment = exile.Government;
        var holderGdpBefore = holder.Gdp; var holderPopulationBefore = holder.Population;

        new MovementActionCommand { IssuerId = exile.Id, Action = "restore", StateMapIndex = home.MapIndex }.Apply(c);

        Check(c.StatesOf(exile).Length > 0 && home.NationMapIndex == exile.MapIndex,
            "the homeland is the country's own again, through the ordinary border machinery");
        Check(exile.Movement.Landless == false && exile.Population == 500000,
            "the country stands on its own ground with the people who live on it");
        Check(exile.Tag == beforeTag && exile.Name == beforeName && exile.Color == beforeColour,
            "the restored country is the same country: its tag, name and colour were never given up");
        Check(exile.Government == beforeGovernment,
            "the government it had when it fell returns, rather than a new provisional one being founded");
        Check(Math.Abs(exile.Gdp - 500f) < 1f && float.IsFinite(exile.Gdp) &&
              float.IsFinite(exile.Treasury) && float.IsFinite(exile.PublicDebt),
            "the region's output moves to the country and every account stays finite");
        Check(Math.Abs(holder.Gdp - (holderGdpBefore - 500f)) < 1f && holder.Population < holderPopulationBefore,
            "the country that handed it back keeps no claim to the people or the output it gave away");
        Check(c.Chronicle.Entries.Any(entry => entry.Kind == "restoration" && entry.ActorId == exile.Id),
            "the restoration is recorded in the world chronicle");
        Check(Restoration.Refusal(c, exile, home) != "",
            "a country that already stands is not restored a second time - no duplicate is created");
        Check(OpinionCatalog.Find("restored_us") is not null,
            "the country that gave the ground back is remembered for it");

        // ---- the military way home: the government already stands on the ground ----------
        var (c2, exile2, holder2, home2, _) = Fixture();
        CampaignDiplomacy.Get(c2, holder2.Id, exile2.Id).Opinion = -50f;
        Check(Restoration.Refusal(c2, exile2, home2) != "",
            "a hostile holder that still owns the ground does not hand it back");
        for (var province = 1; province <= c2.ProvinceMap.ProvinceCount; province++)
            if (c2.ProvinceMap.ProvinceToState[province] == (ushort)home2.MapIndex)
                c2.SetProvinceController(province, (ushort)exile2.MapIndex);
        Check(Restoration.CanRestore(c2, exile2, home2),
            "a government that already stands on its own homeland returns without the holder's leave");
        Check(Restoration.Return(c2, exile2, home2) && c2.StatesOf(exile2).Length > 0,
            "the military return makes the ground its own through the same accounting");

        // ---- what is not a restoration ----------------------------------------------------
        var (c3, exile3, _, home3, _) = Fixture();
        exile3.Movement.Exile = false;
        Check(Restoration.Refusal(c3, exile3, home3) != "", "an ordinary movement is not a government in exile");
        var (c4, exile4, _, home4, _) = Fixture();
        exile4.Movement = null;
        Check(Restoration.Refusal(c4, exile4, home4) != "", "a country with no government at all is not restored");

        // ---- the wording travels with the mechanic ---------------------------------------
        Check(Loc.TextIn("russian", "Restore the country") is not null &&
              Loc.TextIn("english", "Restore the country") is not null,
            "the restoration action is named in both languages");
        Check(Loc.Placeholders(Loc.TextIn("english", "The government has returned to {0}: {1} stands again.")).SetEquals(
                  Loc.Placeholders(Loc.TextIn("russian", "The government has returned to {0}: {1} stands again."))),
            "the restoration report keeps every formatting argument in both languages");
        Check(MovementActions.Kinds.Any(kind => kind.Id == "restore" && kind.Trait == "government_in_exile"),
            "the restore action is offered only to governments in exile");
    }
}
