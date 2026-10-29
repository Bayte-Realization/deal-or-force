using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// A movement that wins, wins its zone of interest rather than a postcode.
///
/// An uprising that carries its flashpoint does not stop at the city limits and a referendum
/// that carries is not re-asked county by county for a decade. What the movement's own data
/// names as its homeland, and what one holder still governs, comes over in the same act. The
/// brakes matter as much as the sweep: a homeland chunk another country holds is somebody
/// else's war, a region of the same holder that is not homeland stays put, and a ballot frees
/// only ground where the movement's own residents actually voted for it.
///
/// The settlement belongs to the act, not to each region: one militia, one war, one
/// proclamation. Taking three regions used to raise three separate uprisings' worth of
/// militia, declare three wars and write three statehood entries for one country.
/// </summary>
static class MovementZoneTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("movement zone: " + label); Console.WriteLine("PASS movement zone: " + label); }

    const string ArmedId = "zone_test_armed";
    const string CivicId = "zone_test_civic";

    /// <summary>Two fixture definitions, merged through the catalogue the game reads.</summary>
    static void DeclareCatalog()
    {
        MovementCatalog.ResetCatalog();
        using var armed = JsonDocument.Parse("""
        {
          "zone_test_armed": {
            "tag": "ZTA", "name": "Zone test rising", "description": "fixture",
            "colour": "#a05050", "peoples": ["english"], "headquarters": "z_0",
            "homeland": ["z_0", "z_1", "z_2", "z_4"],
            "support": { "z_0": 0.9, "z_1": 0.9, "z_2": 0.9 },
            "treasury": 10, "cadres": 30000, "legitimacy": 0.6,
            "government": "provisional_government", "ideology": "nationalism",
            "state_name": "Zetland", "traits": ["armed_wing"]
          }
        }
        """);
        MovementCatalog.Merge(armed.RootElement, null);
        using var civic = JsonDocument.Parse("""
        {
          "zone_test_civic": {
            "tag": "ZTC", "name": "Zone test campaign", "description": "fixture",
            "colour": "#5070a0", "peoples": ["english"], "headquarters": "z_0",
            "homeland": ["z_0", "z_1", "z_2"],
            "support": { "z_0": 0.9, "z_1": 0.9, "z_2": 0.9 },
            "treasury": 10, "cadres": 5000, "legitimacy": 0.7,
            "government": "provisional_government", "ideology": "social_democracy",
            "state_name": "Zetland", "traits": ["civic_independence", "nonviolence"]
          }
        }
        """);
        MovementCatalog.Merge(civic.RootElement, null);
    }


    /// <summary>
    /// z_0..z_3 and z_5 belong to the parent, z_4 to its neighbour. The movements claim
    /// z_0..z_4; z_3 is the parent's ground the claim does not name.
    /// </summary>
    static Campaign Fixture(bool minorityInZ1)
    {
        var c = new Campaign(20261021) { ScenarioId = "movement_zone_fixture" };
        c.AddNation(new Nation
        {
            Tag = "ZPA", Name = "Parent", MapIndex = 501, Population = 5_000_000, Gdp = 500f,
            Treasury = 60f, ManpowerReserve = 50_000, Stability = .8f, WarSupport = .7f,
            Government = "parliamentary_republic", Ideology = "liberalism"
        });
        c.AddNation(new Nation
        {
            Tag = "ZNB", Name = "Neighbour", MapIndex = 502, Population = 1_000_000, Gdp = 100f,
            Treasury = 20f, Stability = .8f, Government = "parliamentary_republic", Ideology = "liberalism"
        });
        string[] ids = { "z_0", "z_1", "z_2", "z_3", "z_4", "z_5" };
        for (var i = 0; i < ids.Length; i++)
        {
            var state = new ScenarioState
            {
                Id = ids[i], Name = "Zone " + i, MapIndex = 3001 + i,
                NationMapIndex = i == 4 ? 502 : 501, Population = 1_000_000
            };
            state.PopulationGroups.Add(new PopulationGroup
            {
                Culture = "english", Religion = "protestant",
                Size = minorityInZ1 && i == 1 ? 300_000 : 1_000_000
            });
            if (minorityInZ1 && i == 1)
                state.PopulationGroups.Add(new PopulationGroup { Culture = "french", Religion = "catholic", Size = 700_000 });
            c.States.Add(state);
        }
        const int width = 96, height = 16;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var cell = y * width + x;
                var index = x / 16;
                states[cell] = (ushort)(3001 + index);
                owners[cell] = (ushort)(index == 4 ? 502 : 501);
            }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, width, height, _ => false));
        c.Nations[0].CapitalStateMapIndex = 3001;
        c.Nations[1].CapitalStateMapIndex = 3005;
        c.ConfigureSinglePlayer(c.Nations[0].Id);
        return c;
    }

    static Campaign WithArmed(out Nation movement)
    {
        var c = Fixture(false);
        Movements.Activate(c, MovementCatalog.Of(ArmedId));
        movement = c.NationByTag("ZTA");
        if (movement?.Movement is not { } state) throw new Exception("movement zone: armed fixture not founded");
        // An armed struggle, so the uprising is available at all; the numbers are the fixture's.
        state.Militancy = 0.9f;
        state.Cadres = 30_000f;
        state.Legitimacy = 0.6f;
        movement.Treasury = 10f;
        foreach (var index in new[] { 3001, 3002, 3003 }) state.Support[index] = 0.9f;
        return c;
    }

    /// <summary>Move the calendar to a day where this roll would carry. Read-only until it does.</summary>
    static void DayThatCarries(Campaign c, Nation movement, ScenarioState region, float chance)
    {
        var hours = typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours));
        for (var day = 1; day < 20000; day++)
            if (MovementRules.Roll(c.Seed, movement.Id, day, region.MapIndex) < chance)
            {
                hours.SetValue(c, (long)day * 24);
                return;
            }
        throw new Exception("movement zone: no campaign day gives a successful uprising roll");
    }

    public static void Run()
    {
        Loc.ResetCatalog();
        DemographyCatalog.ResetCatalog();
        OpinionCatalog.ResetCatalog();
        DeclareCatalog();

        // ---- an uprising carries its homeland ---------------------------------------------
        var c = WithArmed(out var rising);
        var parent = c.Nations[0];
        var seed = Movements.StateById(c, "z_0");
        var chance = MovementRules.UprisingChance(c, rising, seed);
        Check(chance > 0.05f, $"an armed movement with a following can rise ({chance:0.00})");
        DayThatCarries(c, rising, seed, chance);
        var warsBefore = c.Wars.Count(w => !w.Ended && w.Involves(rising.Id));
        Check(MovementRules.Rise(c, rising, seed), "the uprising in the flashpoint succeeds");

        Check(Movements.StateById(c, "z_0").NationMapIndex == rising.MapIndex &&
              Movements.StateById(c, "z_1").NationMapIndex == rising.MapIndex &&
              Movements.StateById(c, "z_2").NationMapIndex == rising.MapIndex,
            "and the whole homeland the same holder governs is ours, not one region");
        Check(c.StatesOf(rising).Length == 3, "three regions came over in one act");
        Check(Movements.StateById(c, "z_3").NationMapIndex == parent.MapIndex,
            "the holder's ground the claim does not name stays where it is");
        Check(Movements.StateById(c, "z_4").NationMapIndex == 502,
            "a homeland chunk another country holds is another war, for another day");
        var question = c.PendingEvents.Single(e => e.NationId == parent.Id);
        new ResolveCampaignEventCommand { IssuerId = parent.Id, EventId = question.Id, OptionIndex = 2 }.Apply(c);
        Check(c.Wars.Count(w => !w.Ended && w.Involves(rising.Id)) == warsBefore + 1,
            "one uprising declares one war, however many regions rose");
        Check(c.Armies.Count(a => a.NationId == rising.Id) <= c.Defines.MovementUprisingMilitiaMax,
            "one militia, capped once, not a fresh cap per region");
        Check(c.Chronicle.Entries.Count(e => e.Kind == "movement_statehood" && e.ActorId == rising.Id) == 1,
            "the country is proclaimed once");
        Check(rising.Movement is null && c.StatesOf(rising).Length == 3,
            "the movement that took its homeland is a state now, not a movement holding land");

        // ---- a referendum carries region by region ----------------------------------------
        var civicWorld = Fixture(true);
        Movements.Activate(civicWorld, MovementCatalog.Of(CivicId));
        var civic = civicWorld.NationByTag("ZTC");
        var holder = civicWorld.Nations[0];
        if (civic?.Movement is not { } votes) throw new Exception("movement zone: civic fixture not founded");
        votes.Legitimacy = 0.9f;
        foreach (var index in new[] { 3001, 3002, 3003 }) votes.Support[index] = 0.9f;
        votes.ReadyOnDay[CivilIndependence.ConsentKey(holder.Id)] = civicWorld.ElapsedDays + 500;
        // Counted region by region: the settlement a quarter of votes get (Schleswig, 1920).
        civicWorld.Defines.CivilReferendumByRegionShare = 1f;
        var civicSeed = Movements.StateById(civicWorld, "z_0");
        Check(CivilIndependence.Refusal(civicWorld, civic, civicSeed).Length == 0,
            "the parent's consent and a local majority qualify the vote");
        var wars = civicWorld.Wars.Count;
        Check(CivilIndependence.Ratify(civicWorld, civic, civicSeed), "the agreed vote is ratified");

        Check(Movements.StateById(civicWorld, "z_0").NationMapIndex == civic.MapIndex &&
              Movements.StateById(civicWorld, "z_2").NationMapIndex == civic.MapIndex,
            "every homeland region whose own majority carried becomes independent");
        Check(Movements.StateById(civicWorld, "z_1").NationMapIndex == holder.MapIndex,
            "a region whose people did not vote for it stays put: the vote was not a landslide everywhere");
        Check(civicWorld.StatesOf(civic).Length == 2 && civicWorld.Wars.Count == wars,
            "two regions, one peaceful act, no war");
        Check(!civicWorld.Armies.Any(a => a.NationId == civic.Id),
            "a nonviolent campaign raises no militia");
        Check(civicWorld.Chronicle.Entries.Count(e => e.Kind == "peaceful_independence" && e.ActorId == civic.Id) == 1,
            "one peaceful independence is written into the chronicle");

        // ---- most votes are one question for the whole homeland ---------------------------
        var oneVote = Fixture(true);
        Movements.Activate(oneVote, MovementCatalog.Of(CivicId));
        var scots = oneVote.NationByTag("ZTC");
        var crown = oneVote.Nations[0];
        scots.Movement.Legitimacy = 0.9f;
        foreach (var index in new[] { 3001, 3002, 3003 }) scots.Movement.Support[index] = 0.9f;
        scots.Movement.ReadyOnDay[CivilIndependence.ConsentKey(crown.Id)] = oneVote.ElapsedDays + 500;
        oneVote.Defines.CivilReferendumByRegionShare = 0f;
        Check(CivilIndependence.Ratify(oneVote, scots, Movements.StateById(oneVote, "z_0")) &&
              Movements.StateById(oneVote, "z_1").NationMapIndex == scots.MapIndex,
            "a vote carried across the homeland takes the region that voted no along with the rest");

        // ---- and a vote can be lost ------------------------------------------------------
        var lostVote = Fixture(true);
        Movements.Activate(lostVote, MovementCatalog.Of(CivicId));
        var hopefuls = lostVote.NationByTag("ZTC");
        var state = lostVote.Nations[0];
        hopefuls.Movement.Legitimacy = 0.9f;
        foreach (var index in new[] { 3001, 3002, 3003 }) hopefuls.Movement.Support[index] = 0.45f;
        hopefuls.Movement.ReadyOnDay[CivilIndependence.ConsentKey(state.Id)] = lostVote.ElapsedDays + 500;
        lostVote.Defines.CivilReferendumSwing = 0f;
        var asked = Movements.StateById(lostVote, "z_0");
        Check(CivilIndependence.ResidentSupport(hopefuls, asked) is > 0.42f and < 0.5f &&
              CivilIndependence.Refusal(lostVote, hopefuls, asked).Length == 0,
            "a campaign short of a majority may still hold its vote");
        var legitimacy = hopefuls.Movement.Legitimacy;
        Check(!CivilIndependence.Ratify(lostVote, hopefuls, asked) && asked.NationMapIndex == state.MapIndex,
            "and lose it: the region stays");
        Check(hopefuls.Movement.Legitimacy < legitimacy && CivilIndependence.Refusal(lostVote, hopefuls, asked).Length > 0 &&
              lostVote.Chronicle.Entries.Any(e => e.Kind == "referendum_lost"),
            "a lost vote costs the campaign standing and closes the question for years");


        // ---- an exile comes home to its whole homeland -------------------------------------
        var home = Fixture(false);
        var country = home.AddNation(new Nation
        {
            Tag = "ZEX", Name = "Exile", MapIndex = 503, Population = 0, Gdp = 0.05f, Treasury = 1f,
            Stability = .6f, Government = "parliamentary_republic", Ideology = "liberalism"
        });
        country.Movement = new MovementState
        {
            Id = "", Exile = true, Landless = true, Dissolved = false,
            HeadquartersStateMapIndex = 3001, Legitimacy = .6f, Cadres = 4000f
        };
        country.Movement.Homeland.Add("z_0");
        country.Movement.Homeland.Add("z_1");
        country.Movement.Peoples.Add("english");
        home.Homelands[country.Id] = new List<int> { 3001, 3002 };
        var host = home.Nations[0];
        country.Movement.ReadyOnDay[Restoration.ConsentKey(host.Id)] = home.ElapsedDays + 500;
        Check(Restoration.CanRestore(home, country, Movements.StateById(home, "z_0")),
            "the holder's permission qualifies the return");
        Check(Restoration.Return(home, country, Movements.StateById(home, "z_0")),
            "the government comes home");
        Check(Movements.StateById(home, "z_0").NationMapIndex == country.MapIndex &&
              Movements.StateById(home, "z_1").NationMapIndex == country.MapIndex,
            "and the homeland comes home with it, not only the doorstep");
        Check(Movements.StateById(home, "z_2").NationMapIndex == host.MapIndex,
            "ground outside the homeland is untouched by a restoration");

        // ---- the zone is not a licence to take half the world -----------------------------
        var narrow = Fixture(false);
        Movements.Activate(narrow, MovementCatalog.Of(CivicId));
        var narrowCivic = narrow.NationByTag("ZTC");
        var zone = MovementRules.ZoneOf(narrow, narrowCivic, Movements.DefinitionOf(narrowCivic),
            Movements.StateById(narrow, "z_0"), narrow.Nations[0], peacefulVote: false);
        Check(zone.Select(region => region.Id).SequenceEqual(new[] { "z_0", "z_1", "z_2" }),
            "the zone is the authored homeland this holder governs, in map order");

        // A mandate is not a military victory over an unrelated occupier.
        var occupied = Fixture(false);
        Movements.Activate(occupied, MovementCatalog.Of(CivicId));
        var ballot = occupied.NationByTag("ZTC"); var original = occupied.Nations[0];
        ballot.Movement.Legitimacy = .9f;
        foreach (var index in new[] { 3001, 3002, 3003 }) ballot.Movement.Support[index] = .9f;
        ballot.Movement.ReadyOnDay[CivilIndependence.ConsentKey(original.Id)] = 500;
        occupied.Defines.CivilReferendumByRegionShare = 1f;
        var occupiedProvince = Enumerable.Range(1, occupied.ProvinceMap.ProvinceCount)
            .First(p => occupied.ProvinceMap.ProvinceToState[p] == 3002);
        occupied.ProvinceController[occupiedProvince] = (ushort)occupied.Nations[1].MapIndex;
        var before = CampaignFingerprint.Of(occupied); var random = occupied.Random.State;
        var preview = MovementRules.ZoneOf(occupied, ballot, Movements.DefinitionOf(ballot),
            Movements.StateById(occupied, "z_0"), original, peacefulVote: true);
        Check(preview.Select(r => r.Id).SequenceEqual(new[] { "z_0", "z_2" }) &&
              CampaignFingerprint.Of(occupied) == before && occupied.Random.State == random,
            "the actual zone preview is read-only and excludes a foreign-occupied secondary region");
        Check(CivilIndependence.Ratify(occupied, ballot, Movements.StateById(occupied, "z_0")) &&
              Movements.StateById(occupied, "z_2").NationMapIndex == ballot.MapIndex &&
              Movements.StateById(occupied, "z_1").NationMapIndex == original.MapIndex &&
              occupied.ProvinceController[occupiedProvince] == occupied.Nations[1].MapIndex,
            "one referendum takes every qualified free region while preserving the occupier's actual control");

        var allHome = Fixture(false);
        Movements.Activate(allHome, MovementCatalog.Of(CivicId));
        var ambitious = allHome.NationByTag("ZTC"); var parentHome = allHome.Nations[0];
        ambitious.Movement.Legitimacy = .9f;
        allHome.Homelands[ambitious.Id] = allHome.StatesOf(parentHome).Select(r => r.MapIndex).ToList();
        foreach (var region in allHome.StatesOf(parentHome)) ambitious.Movement.Support[region.MapIndex] = .9f;
        ambitious.Movement.ReadyOnDay[CivilIndependence.ConsentKey(parentHome.Id)] = 500;
        Check(CivilIndependence.Ratify(allHome, ambitious, Movements.StateById(allHome, "z_0")) &&
              allHome.StatesOf(ambitious).Length > 1 && allHome.StatesOf(parentHome).Length == 1 && allHome.Wars.Count == 0,
            "a large peaceful zone still leaves a sovereign parent instead of silently dissolving it after its capital voted first");
    }
}
