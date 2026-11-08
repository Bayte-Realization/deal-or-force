using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class PeacetimeArmyTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("Peacetime army: " + label);
        Console.WriteLine("PASS Peacetime army: " + label);
    }

    public static void Run()
    {
        PeaceReleasesItsOrders(false);
        PeaceReleasesItsOrders(true);
        ForeignSingletonMarchesHome();
        OldManualPlanDoesNotFreezeDisposition();
        ReturnRespectsAuthorityAndDuties();
        ReturnHasABudgetAndDoesNotInventCorridors();
        BorderReviewPreservesARealMarch();
    }

    static Campaign World()
    {
        var c = AdvanceTargetTests.Fixture();
        c.Wars.Clear();
        c.Defines.HoursPerTick = 24;
        c.Defines.DispositionSupplyLimit = 0;
        c.Defines.DispositionCongestionLimit = 0;
        c.Defines.DispositionGarrisonShare = 0;
        c.Defines.ExclaveDefenceWeight = 0;
        // Actual home is cell 14; cell 8 (north of it) and cell 2 belong to the host.
        var r = CampaignDiplomacy.Get(c, 0, 1);
        r.MilitaryAccessByB = true;
        return c;
    }

    static Army Unit(Campaign c, int id, int province)
    {
        var centre = c.ProvinceMap.CenterOf(province);
        var a = new Army { Id = id, NationId = 0, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], Divisions = 1,
            Organisation = 1, Strength = .7f, Equipment = .6f, SupplyLevel = 1,
            Latitude = centre.Latitude, Longitude = centre.Longitude,
            PlanActive = false, OrderIssuedDay = -100 };
        c.Armies.Add(a);
        return a;
    }

    static void PeaceReleasesItsOrders(bool conference)
    {
        var c = World();
        var war = new StrategicWar { Id = 884, AttackerId = 0, DefenderId = 1, StartedDay=-365, WarScore=100 };
        c.Wars.Add(war);
        var a = Unit(c, 1, 14);
        a.AssignedWarId = war.Id; a.OrderKind = ArmyOrder.AdvanceFront;
        a.PlanActive = true; a.DirectOrder = true; a.SupportOnly = true;
        a.OrderProvinceId = 8; a.OrderStateMapIndex = 3008; a.ObjectiveStateMapIndex = 3008;
        a.Route.Add(8); a.PlannedRoute.Add(8); a.MoveProgress = .4f;
        var otherWar = new StrategicWar { Id = 885, AttackerId = 0, DefenderId = 1 };
        c.Wars.Add(otherWar);
        var other = Unit(c, 2, 14); other.AssignedWarId = otherWar.Id;
        other.PlanActive = true; other.OrderKind = ArmyOrder.DefendFront;
        var before = Snapshot(other);
        var rng = c.Random.State;
        if (conference)
            Check(PeaceConference.Resolve(c, war, new PeaceDemand { WarId = war.Id, ProposerId = 0, Humiliate=true }),
                "an affordable written peace resolves");
        else PeaceNegotiation.Resolve(c, war, 0, PeaceTerms.WhitePeace);
        Check(war.Ended, "the fixture actually signs peace: " + war.LastNegotiation);
        Check(war.Ended && a.AssignedWarId < 0 && a.OrderKind == ArmyOrder.Manual &&
            !a.PlanActive && !a.DirectOrder && !a.SupportOnly,
            (conference ? "written" : "fixed-term") + " peace cancels obsolete attack authority");
        Check(a.Route.Count == 0 && a.PlannedRoute.Count == 0 && a.MoveProgress == 0 &&
            a.OrderProvinceId < 0 && a.OrderStateMapIndex < 0 && a.ObjectiveStateMapIndex < 0,
            "peace clears the ended operation's destination and progress");
        Check(a.Strength == .7f && a.Equipment == .6f && a.ProvinceId == 14,
            "release neither heals nor teleports a formation");
        Check(Snapshot(other) == before, "another active war's formation keeps its full order");
        Check(c.Random.State == rng, "order release does not consume campaign randomness");
    }

    static void ForeignSingletonMarchesHome()
    {
        var c = World(); var a = Unit(c, 1, 2);
        var rng = c.Random.State;
        ArmyDisposition.Tick(c);
        Check(a.ProvinceId == 2 && a.OrderProvinceId == 14 && a.Route.SequenceEqual(new[] { 8, 14 }),
            "a solitary idle guest receives a legal homeward march, not a teleport");
        Check(!a.DirectOrder && !a.PlanActive && a.AssignedWarId < 0,
            "the homeward march remains an autonomous peaceful order");
        var orderDay = a.OrderIssuedDay;
        for (var day = 0; day < 60 && a.Route.Count > 0; day++)
        { MilitaryOperations.Tick(c); ArmyDisposition.Tick(c); }
        Check(a.ProvinceId == 14 && a.Route.Count == 0,
            "MilitaryOperations actually brings the guest onto its own homeland");
        Check(a.Strength == .7f && a.Equipment == .6f && a.OrderIssuedDay == orderDay,
            "homecoming preserves losses and does not restart the march");
        Check(c.Random.State == rng, "homeward disposition consumes no randomness");
    }

    static void OldManualPlanDoesNotFreezeDisposition()
    {
        var c = World(); var a = Unit(c, 1, 2); a.PlanActive = true;
        ArmyDisposition.Tick(c);
        Check(a.OrderProvinceId == 14 && a.Route.Count > 0,
            "a legacy manual formation's stale execution flag does not freeze it abroad");
    }

    static void ReturnRespectsAuthorityAndDuties()
    {
        var guards = new (string Name, Action<Campaign, Army> Block)[]
        {
            ("a human's nondelegated formation", (c,a) => c.Nations[0].IsHuman = true),
            ("a direct deployment", (c,a) => a.DirectOrder = true),
            ("a drawn-line garrison", (c,a) => a.FrontLineId = 9),
            ("a lent volunteer", (c,a) => a.LentById = 1),
            ("an embarked formation", (c,a) => a.EmbarkedFleetId = 9),
            ("a fighting formation", (c,a) => a.BattleId = 9),
            ("a field militia", (c,a) => a.TemplateId = MilitiaCore.TemplateId),
            ("an active war", (c,a) => c.Wars.Add(new StrategicWar { Id=884, AttackerId=0, DefenderId=1 })),
            ("an active limited war", (c,a) => c.Wars.Add(new StrategicWar { Id=884, AttackerId=0, DefenderId=1, Limited=true })),
            ("a recovering formation", (c,a) => a.ReadyDay = c.ElapsedDays + 1),
            ("a disabled disposition rule", (c,a) => c.Defines.ArmyDispositionEnabled = false),
            ("a disabled voluntary return rule", (c,a) => c.Defines.ArmyHomecomingEnabled = false),
            ("an existing march", (c,a) => { a.Route.Add(8); a.PlannedRoute.Add(8); a.OrderProvinceId=8; a.MoveProgress=.4f; })
        };
        foreach (var (label, block) in guards)
        {
            var c = World(); var a = Unit(c, 1, 2); block(c,a);
            var before = Snapshot(a); ArmyDisposition.Tick(c);
            Check(Snapshot(a) == before, "homecoming leaves " + label + " alone");
        }
        var delegated = World(); delegated.Nations[0].IsHuman = true;
        Delegation.Set(delegated.Nations[0], Delegation.Command, true);
        var own = Unit(delegated, 1, 2); ArmyDisposition.Tick(delegated);
        Check(own.OrderProvinceId == 14, "delegated command can bring the player's free guest home");
    }

    static void ReturnHasABudgetAndDoesNotInventCorridors()
    {
        var c = World(); c.Defines.DispositionMovesPerWeek = 2;
        for (var id = 1; id <= 5; id++) Unit(c, id, 2);
        ArmyDisposition.Tick(c);
        Check(c.Armies.Count(a => a.Route.Count > 0) == 2,
            "homecoming shares the existing weekly move budget instead of ordering the whole army");
        var twin = World(); twin.Defines.DispositionMovesPerWeek = 2;
        for (var id = 1; id <= 5; id++) Unit(twin, id, 2);
        ArmyDisposition.Tick(twin);
        Check(CampaignFingerprint.Of(c) == CampaignFingerprint.Of(twin),
            "same-seed reviews keep identical authoritative campaign state");
        c.ConfigureSinglePlayer(0); // snapshot transport requires an actual player seat
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "homecoming-tests"), "homecoming-tests");
        Check(saved.Success, "snapshot imports: " + saved.Warning);
        Check(saved.Campaign.Armies[0].Route.SequenceEqual(c.Armies[0].Route) &&
            !saved.Campaign.Armies[0].DirectOrder && saved.Campaign.Defines.ArmyHomecomingEnabled,
            "the ordinary homeward route and mod rule survive a save/network snapshot");
        var isolated = World(); isolated.SetProvinceController(8, 0);
        // No permission through any other neighbour: only the host's cell 2 is accessible.
        foreach (var p in Enumerable.Range(1, isolated.ProvinceMap.ProvinceCount))
            if (p != 2 && p != 14) isolated.SetProvinceController(p, 0);
        var stranded = Unit(isolated, 1, 2); var before = Snapshot(stranded);
        ArmyDisposition.Tick(isolated);
        Check(Snapshot(stranded) == before, "an overseas or disconnected guest does not cross neutral land or teleport");
    }

    static void BorderReviewPreservesARealMarch()
    {
        Campaign BorderWorld()
        {
            var w = World(); w.Defines.ThreatPostureReviewDays = 1;
            // Isolated subsystem fixture: advance the clock without unrelated economies.
            typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(w, 24L);
            var r = CampaignDiplomacy.Get(w, 0, 1); r.Opinion = -100; r.Stance = DiplomaticStance.Rival;
            foreach (var p in new[] { 7, 8 })
            { w.SetProvinceController(p, 500); w.ProvinceMap.ProvinceToNation[p] = 500; }
            return w;
        }
        var c = BorderWorld();
        var a = Unit(c, 1, 2); a.Route.Add(8); a.Route.Add(14);
        a.PlannedRoute.AddRange(a.Route); a.OrderProvinceId=14; a.MoveProgress=.4f;
        var control = BorderWorld(); var standing = Unit(control, 1, 2);
        ThreatPosture.Tick(control);
        Check(standing.OrderProvinceId == 8 && standing.Route.Count > 0,
            "the control really has a reason to choose a different threatened border post");
        var before = Snapshot(a); ThreatPosture.Tick(c);
        Check(Snapshot(a) == before, "a threat review does not turn a homeward column around before it arrives");
    }

    static string Snapshot(Army a) => JsonSerializer.Serialize(a, new JsonSerializerOptions { IncludeFields = true });
}
