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

        var states = new ushort[128 * 64]; var owners = new ushort[128 * 64];
        for (var y = 0; y < 64; y++) for (var x = 0; x < 128; x++)
        { states[y * 128 + x] = (ushort)(x < 64 ? 3001 : 3002); owners[y * 128 + x] = 502; }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 128, 64, _ => false));

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

        // Friendship is not a land-transfer mandate, including when the holder is a player.
        var (guard, claimant, friendly, claimed, _) = Fixture();
        var fingerprint = CampaignFingerprint.Of(guard);
        Check(!Restoration.CanRestore(guard, claimant, claimed) && Restoration.BestRegion(guard, claimant) is null,
            "a friendly annexer does not automatically give the homeland back");
        Check(CampaignFingerprint.Of(guard) == fingerprint, "restoration previews do not create relations or mutate the campaign");
        friendly.IsHuman = false;
        Check(!Restoration.CanRestore(guard, claimant, claimed), "AI friendship is not automatic consent either");
        guard.Relations.Clear();
        fingerprint = CampaignFingerprint.Of(guard);
        Check(!Restoration.Consents(guard, claimant, friendly) && CampaignFingerprint.Of(guard) == fingerprint,
            "inspecting a missing diplomatic pair creates no relation");

        // ---- the way home: the holder explicitly authorises the return -------------------
        var (c, exile, holder, home, far) = Fixture();
        new MovementDealingCommand { IssuerId = holder.Id, MovementId = exile.Id, Action = "authorise_restoration" }.Apply(c);
        Check(Restoration.Refusal(c, exile, home) == "" && Restoration.CanRestore(c, exile, home),
            "a government in exile can be restored to its own homeland when the holder will give it back");
        Check(Restoration.Refusal(c, exile, far) != "",
            "a region that is not its homeland cannot be a restoration");
        Check(Restoration.BestRegion(c, exile)?.MapIndex == home.MapIndex,
            "the AI's restoration target is the homeland it may actually return to");
        Check(Restoration.Consents(c, exile, holder), "the holder explicitly consents to the hand-back");
        exile.CapitalName = "Tampa";
        exile.CapitalLatitude = 28f; exile.CapitalLongitude = -82f;

        var beforeTag = exile.Tag; var beforeName = exile.Name; var beforeColour = exile.Color;
        var beforeGovernment = exile.Government;
        var holderGdpBefore = holder.Gdp; var holderPopulationBefore = holder.Population;

        new MovementActionCommand { IssuerId = exile.Id, Action = "restore", StateMapIndex = home.MapIndex }.Apply(c);

        Check(c.StatesOf(exile).Length > 0 && home.NationMapIndex == exile.MapIndex,
            "the homeland is the country's own again, through the ordinary border machinery");
        // A movement holding its own ground is a country now: the movement rolls close,
        // the tag, name, colour and government continue as the state's own.
        Check(exile.Movement is null && exile.Population == 500000,
            "the restored country stands on its own ground as a country, not as a movement");
        Check(exile.Tag == beforeTag && exile.Name == beforeName && exile.Color == beforeColour,
            "the restored country is the same country: its tag, name and colour were never given up");
        Check(exile.Government == beforeGovernment,
            "the government it had when it fell returns, rather than a new provisional one being founded");
        Check(exile.CapitalName == home.Name && exile.CapitalStateMapIndex == home.MapIndex &&
              c.ProvinceMap.ProvinceToNation[exile.CapitalProvinceId] == exile.MapIndex,
            "the restored capital is its home region, not the former foreign headquarters in Tampa");
        Check(!Restoration.Consents(c, exile, holder), "a return consumes its one-use permission");
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
        var first = Enumerable.Range(1, c2.ProvinceMap.ProvinceCount)
            .First(p => c2.ProvinceMap.ProvinceToState[p] == home2.MapIndex);
        c2.SetProvinceController(first, (ushort)exile2.MapIndex);
        Check(!Restoration.Controls(c2, exile2, home2) && !Restoration.CanRestore(c2, exile2, home2),
            "holding one province does not confiscate the rest of its region");
        for (var province = 1; province <= c2.ProvinceMap.ProvinceCount; province++)
            if (c2.ProvinceMap.ProvinceToState[province] == (ushort)home2.MapIndex)
                c2.SetProvinceController(province, (ushort)exile2.MapIndex);
        Check(Restoration.CanRestore(c2, exile2, home2),
            "a government that already stands on its own homeland returns without the holder's leave");
        Check(Restoration.Return(c2, exile2, home2) && c2.StatesOf(exile2).Length > 0,
            "the military return makes the ground its own through the same accounting");

        var (revoked, revokedExile, revoker, revokedHome, _) = Fixture();
        new MovementDealingCommand { IssuerId = revoker.Id, MovementId = revokedExile.Id, Action = "authorise_restoration" }.Apply(revoked);
        var permissionSnapshot = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(revoked, "restoration"), "restoration");
        Check(permissionSnapshot.Success && Restoration.Consents(permissionSnapshot.Campaign,
                permissionSnapshot.Campaign.NationById(revokedExile.Id), permissionSnapshot.Campaign.NationById(revoker.Id)),
            "explicit permission survives a real campaign snapshot");
        new MovementDealingCommand { IssuerId = revoker.Id, MovementId = revokedExile.Id, Action = "revoke_restoration" }.Apply(revoked);
        Check(!Restoration.CanRestore(revoked, revokedExile, revokedHome), "revoked permission cannot transfer a region");
        new MovementDealingCommand { IssuerId = revoker.Id, MovementId = revokedExile.Id, Action = "authorise_restoration" }.Apply(revoked);
        var key = Restoration.ConsentKey(revoker.Id);
        revokedExile.Movement.ReadyOnDay[key] = revoked.ElapsedDays;
        Check(!Restoration.CanRestore(revoked, revokedExile, revokedHome), "permission expires exactly on its deadline");
        new MovementDealingCommand { IssuerId = revoker.Id, MovementId = revokedExile.Id, Action = "authorise_restoration" }.Apply(revoked);
        revoked.Wars.Add(new StrategicWar { Id = 1, AttackerId = revoker.Id, DefenderId = revokedExile.Id });
        Check(!Restoration.CanRestore(revoked, revokedExile, revokedHome), "war invalidates diplomatic permission");

        VerifyAccessionDoesNotFlee();

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

    static void VerifyAccessionDoesNotFlee()
    {
        var (c, partner, founder, home, _) = Fixture();
        partner.Movement = null;
        var ground = Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
            .Where(p => c.ProvinceMap.ProvinceToState[p] == home.MapIndex).ToArray();
        StatePartition.Annex(c, ground, partner, asConquest: false);
        partner.Population = home.Population;
        // A third country gives a defeated government somewhere to flee. Force the roll
        // to succeed: without the accession guard this fixture always creates an exile.
        var host = c.AddNation(new Nation { Tag = "QRC", MapIndex = 503, Name = "Host", Gdp = 100,
            Population = 500000, Stability = .8f });
        var far = c.States.First(s => s.MapIndex != home.MapIndex);
        StatePartition.Annex(c, Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
            .Where(p => c.ProvinceMap.ProvinceToState[p] == far.MapIndex).ToArray(), host, false);
        c.Defines.MovementExileBase = 2f;
        var definition = new FormableNation("test_union", "Union", "", new[] { founder.Tag },
            Array.Empty<string>(), ground, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), "#123456")
            { PeacefulAccession = true };
        // A partner led by a player signs the treaty; accession is never a neighbour's decree.
        partner.DecisionAvailableDay[PeacefulFormation.ConsentKey(founder.Id, definition.Id)] =
            c.ElapsedDays + 365;
        PeacefulFormation.Integrate(c, founder, definition);
        Check(c.Fallen.Contains(partner.Id) && !c.HasOwnedLand(partner),
            "a fully acceded country retires its government instead of treating union as defeat");
        MovementFall.Decide(c);
        Check(partner.Movement is null && c.Homelands.ContainsKey(partner.Id),
            "the next fall pass creates no exile, while historical homeland titles remain");
        c.Fallen.Remove(partner.Id);
        MovementFall.Decide(c);
        Check(partner.Movement is { Exile: true }, "the control case still creates an exile after a genuine defeat");
    }
}
