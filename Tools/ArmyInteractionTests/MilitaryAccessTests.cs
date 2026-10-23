using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class MilitaryAccessTests
{
    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL military access: " + name);
        Console.WriteLine("PASS military access: " + name);
    }

    static Campaign World()
    {
        var c = new Campaign(218) { ScenarioId = "military_access_fixture" };
        c.Defines.HoursPerTick = 24;
        for (var id = 0; id < 4; id++) c.Nations.Add(new Nation { Id = id, MapIndex = 500 + id,
            Tag = "MA" + id, Name = "Access country " + id, Population = 1_000_000,
            Gdp = 100, Treasury = 20, OutputScale = 1f, DebtInitialized = true });
        var map = FriendlyRouteSearchTests.Map();
        ushort[] owners = { 0, 500, 501, 501, 501, 502, 501, 501, 500 };
        for (var p = 1; p <= 8; p++)
        {
            map.ProvinceToNation[p] = owners[p];
            c.States.Add(new ScenarioState { Id = "military_access_" + p, MapIndex = 2000 + p,
                NationMapIndex = owners[p], NationId = "MA" + (owners[p] - 500), Name = "Access region " + p,
                Population = 100_000 });
        }
        c.AttachProvinceTopology(map); c.Armies.Clear(); c.Wars.Clear(); c.Battles.Clear();
        c.ConfigureSinglePlayer(0); c.IsPaused = true;
        c.Armies.Add(new Army { Id = 9001, NationId = 0, ProvinceId = 1, StateMapIndex = 2001,
            Divisions = 1, Strength = .8f, Organisation = .7f, Personnel = 12000, Equipment = .6f,
            CutOff = true, SupplyLevel = .1f });
        return c;
    }

    static MilitaryAccessCommand Act(Campaign c, int issuer, int target, MilitaryAccessAction action)
    {
        var command = new MilitaryAccessCommand { IssuerId = issuer, TargetId = target, Action = action };
        command.Apply(c); return command;
    }

    public static void Run()
    {
        var c = World(); var army = c.Armies.Single();
        var before = CampaignFingerprint.Of(c); var rng = c.Random.State; var time = c.ElapsedHours;
        for (var i = 0; i < 3; i++)
        { _ = MilitaryAccess.Refusal(c, c.Nations[0], c.Nations[1]); _ = MilitaryAccess.CanEnter(c, 0, 501); _ = MilitaryAccess.Incoming(c, 1); }
        Check(CampaignFingerprint.Of(c) == before && c.Random.State == rng, "previews neither create relations nor change RNG");
        Check(!MilitaryAccess.CanEnter(c, 0, 501) && !MilitaryAccess.CanEnter(c, 1, 500), "neutral borders begin closed in both directions");
        Check(WarCoalition.FriendlyRoute(c, null, 0, 1, 8).Length == 0, "cached friendly route begins closed");
        new MoveArmyCommand { IssuerId = 0, ArmyId = army.Id, TargetProvinceId = 8 }.Apply(c);
        Check(army.Route.Count == 0, "a friendly destination does not imply permission to cross the country between");
        Act(c, 1, 0, MilitaryAccessAction.Grant);
        Check(MilitaryAccess.CanEnter(c, 0, 501) && !MilitaryAccess.CanEnter(c, 1, 500), "host grant is directional, including nation id zero");
        Check(WarCoalition.FriendlyRoute(c, null, 0, 1, 8).Length > 0, "same-hour grant invalidates a failed route cache");
        Check(c.IsPaused && c.ElapsedHours == time && c.Random.State == rng, "grant applies without unpausing or drawing randomness");
        var owners = (ushort[])c.ProvinceMap.ProvinceToNation.Clone(); var controls = (ushort[])c.ProvinceController.Clone();
        new MoveArmyCommand { IssuerId = 0, ArmyId = army.Id, TargetProvinceId = 8 }.Apply(c);
        Check(army.Route.Count > 0 && army.Route[^1] == 8 && army.Route.All(p => p != 5), "manual march crosses authorised hosts but not an uninvited third country");
        var route = army.Route.ToArray();
        for (var i = 0; i < route.Length * 100 && army.Route.Count > 0; i++)
        { LogisticsSystem.Tick(c); MilitaryOperations.Tick(c); }
        Check(army.ProvinceId == 8 && army.Route.Count == 0, "the ordinary marcher actually completes the transit");
        Check(c.ProvinceController.SequenceEqual(controls) && c.ProvinceMap.ProvinceToNation.SequenceEqual(owners) && c.Wars.Count == 0,
            "transit neither captures provinces nor starts a war");
        Act(c, 0, 1, MilitaryAccessAction.Renounce);
        Check(!MilitaryAccess.Granted(c, 0, 1), "guest can renounce permission without changing the opposite direction");
        Check(WarCoalition.FriendlyRoute(c, null, 0, 1, 8).Length == 0, "same-hour renunciation invalidates a successful route cache");

        c = World(); c.Nations[1].OverlordId = 0; c.Nations[1].SubjectType = "vassal";
        Check(MilitaryAccess.CanEnter(c, 0, 501) && MilitaryAccess.CanEnter(c, 1, 500), "genuine subject of nation zero shares military passage");
        Check(!MilitaryAccess.CanEnter(c, 0, 502), "zero sentinel does not turn an independent neighbour into a subject");
        c = World(); c.Nations[0].IsHuman = false; c.Wars.Add(new StrategicWar { Id = 42, AttackerId = 0, DefenderId = 2 });
        CampaignDiplomacy.Get(c, 0, 1).Opinion = 70;
        Check(AiStatecraft.TryFriendlyMove(c, c.Nations[0], c.Nations[1]) && MilitaryAccess.Granted(c, 0, 1),
            "AI asks for a useful neighbouring corridor to a live opponent through the ordinary command");

        c = World(); army = c.Armies.Single(); army.ProvinceId = 7;
        army.Route.Add(6); army.PlannedRoute.Add(6); army.FrontLineId = 11; army.AssignedWarId = 12;
        var hp = army.Strength; var men = army.Personnel; rng = c.Random.State;
        MilitaryAccess.Tick(c);
        Check(army.ProvinceId == 8 && army.StateMapIndex == 2008, "trespasser returns to the nearest owned, controlled home, not its capital");
        Check(army.Strength == hp && army.Personnel == men && army.Equipment == .6f && c.Random.State == rng,
            "repatriation preserves people, strength, equipment and RNG rather than healing or killing");
        Check(!army.CutOff && army.Route.Count == 0 && army.PlannedRoute.Count == 0 && army.AssignedWarId == -1 && army.FrontLineId == -1 && !army.PlanActive,
            "return clears foreign plans, front and starvation state");
        Check(army.ReadyDay >= c.ElapsedDays + 3, "returning formation must regroup before combat");
        before = CampaignFingerprint.Of(c); MilitaryAccess.Tick(c);
        Check(CampaignFingerprint.Of(c) == before, "a second return pass is idempotent");
        c = World(); army = c.Armies.Single(); army.ProvinceId = 7; c.ProvinceController[8] = 502;
        MilitaryAccess.Tick(c); Check(army.ProvinceId == 1, "occupied home is not a safe destination");
        c = World(); army = c.Armies.Single(); army.ProvinceId = 7; c.ProvinceMap.WaterByProvince[8] = true;
        MilitaryAccess.Tick(c); Check(army.ProvinceId == 1, "home lake cannot receive repatriated soldiers");
        c = World(); army = c.Armies.Single(); army.ProvinceId = 7; c.ProvinceController[1] = c.ProvinceController[8] = 502;
        MilitaryAccess.Tick(c); LogisticsSystem.Tick(c); MilitaryOperations.Tick(c); Attrition.Tick(c);
        Check(c.Armies.Contains(army) && army.ProvinceId == 7 && !army.CutOff && army.Strength >= .8f,
            "with no safe home, an interned neutral unit is not starved to death");
        c.ProvinceController[8] = 500; MilitaryAccess.Tick(c);
        Check(army.ProvinceId == 8, "internment ends when a safe home becomes available");
        c = World(); army = c.Armies.Single(); army.ProvinceId = 7;
        c.Wars.Add(new StrategicWar { Id = 9, AttackerId = 0, DefenderId = 1 });
        Check(!MilitaryAccess.MustReturn(c, army), "hostile encirclement is not neutral internment");
        MilitaryAccess.Tick(c); Attrition.Tick(c);
        Check(army.ProvinceId == 7 && army.Strength < .8f, "real enemy pocket still suffers attrition, not free evacuation");
        c = World(); army = c.Armies.Single(); army.ProvinceId = 7; army.EmbarkedFleetId = 10;
        MilitaryAccess.Tick(c); Check(army.ProvinceId == 7, "ships retain authority over embarked formations");
        c = World(); army = c.Armies.Single(); army.ProvinceId = 7; army.BattleId = 9;
        c.Battles.Add(new LandBattle { Id = 9, ProvinceId = 7 });
        MilitaryAccess.Tick(c); Check(army.ProvinceId == 7 && army.BattleId == 9, "repatriation cannot escape a live battle");
        c = World(); army = c.Armies.Single(); army.ProvinceId = 7;
        var war = new StrategicWar { Id = 10, AttackerId = 0, DefenderId = 3 }; war.AttackerAllies.Add(1); c.Wars.Add(war);
        MilitaryAccess.Tick(c); LogisticsSystem.Tick(c);
        Check(army.ProvinceId == 7 && !army.CutOff, "co-belligerents may stay and are provisioned on the same side");

        c = World(); CampaignDiplomacy.Get(c, 0, 1).Opinion = -80;
        Check(Act(c, 0, 1, MilitaryAccessAction.Request).Result == "Military access refused." && !MilitaryAccess.Granted(c, 0, 1), "rival government can refuse, not auto-sign");
        Check(MilitaryAccess.Refusal(c, c.Nations[0], c.Nations[1]).Length > 0, "refusal has a persistent retry cooldown");
        c = World(); CampaignDiplomacy.Get(c, 0, 1).Opinion = 70;
        Act(c, 0, 1, MilitaryAccessAction.Request);
        Check(MilitaryAccess.Granted(c, 0, 1) && !MilitaryAccess.Granted(c, 1, 0), "AI grants a plausible request under the same directional command");
        Act(c, 1, 0, MilitaryAccessAction.Revoke);
        Check(!MilitaryAccess.CanEnter(c, 0, 501), "host revocation changes route permission immediately");
        c = World(); c.AddHumanSeat(1);
        Act(c, 0, 1, MilitaryAccessAction.Request);
        Check(MilitaryAccess.Incoming(c, 1) == 1 && !MilitaryAccess.Granted(c, 0, 1), "another player receives a pending request, never a forced signature");
        var pendingFp = CampaignFingerprint.Of(c); var r = CampaignDiplomacy.Peek(c, 0, 1);
        r.MilitaryAccessRequestedDay++; Check(CampaignFingerprint.Of(c) != pendingFp, "pending request expiry is authoritative fingerprint state"); r.MilitaryAccessRequestedDay--;
        var wire = SaveSystem.ExportNetworkSnapshot(c, "access-fixture");
        var restored = SaveSystem.ImportNetworkSnapshot(wire, "access-fixture").Campaign;
        // Network snapshots carry political layers, not the shared immutable geometry.
        // Attach this tiny fixture's geometry without triggering a fresh army deployment.
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap)).SetValue(restored, c.ProvinceMap);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController)).SetValue(restored, (ushort[])c.ProvinceController.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation)).SetValue(restored, (float[])c.ProvinceOccupation.Clone());
        Check(MilitaryAccess.Incoming(restored, 1) == 1 && CampaignFingerprint.Of(restored) == CampaignFingerprint.Of(c), "snapshot retains exact pending consent and fingerprint");
        Act(restored, 1, 0, MilitaryAccessAction.Grant);
        Check(MilitaryAccess.Granted(restored, 0, 1), "rightful host can accept the restored letter");
        var grantedCopy = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(restored, "access-fixture"), "access-fixture").Campaign;
        Check(MilitaryAccess.Granted(grantedCopy, 0, 1) && !MilitaryAccess.Granted(grantedCopy, 1, 0), "signed directional permission survives the snapshot too");
        Act(c, 1, 0, MilitaryAccessAction.Decline);
        Check(MilitaryAccess.Incoming(c, 1) == 0 && !MilitaryAccess.Granted(c, 0, 1), "declining clears the letter without granting access");
        var legacy = JsonSerializer.Deserialize<DiplomaticRelation>("{\"A\":0,\"B\":1}", new JsonSerializerOptions { IncludeFields = true });
        Check(!legacy.GrantsMilitaryAccess(1) && legacy.MilitaryAccessRequestedBy == -1, "old relations load closed with no invented pending request");
        ICampaignCommand encoded = new MilitaryAccessCommand { IssuerId = 1, TargetId = 0, Action = MilitaryAccessAction.Grant };
        Check(NetworkCommandCodec.TryEncode(encoded, out var name, out var json) && NetworkCommandCodec.TryDecode(name, json, out var decoded) &&
            decoded is MilitaryAccessCommand { IssuerId: 1, TargetId: 0, Action: MilitaryAccessAction.Grant }, "authoritative command survives the existing network codec");
    }
}
