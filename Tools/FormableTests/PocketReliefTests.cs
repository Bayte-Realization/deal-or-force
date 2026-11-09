using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The staff spends one existing re-posting on the empty post that reconnects a pocket.
/// A free reserve goes there and the reason says so. The pocket is not disbanded.
/// The strip does not wrap the pocket back onto the reserve: neutral columns sit on both edges.
/// </summary>
static class PocketReliefTests
{
    const string Reopen = "A free reserve is reopening the road.";
    const int PocketProvince = 2;
    const int Mouth = 4;
    const int ReserveProvince = 5;
    const int HotPost = 11;

    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("pocket relief: " + label);
        Console.WriteLine("PASS pocket relief: " + label);
    }

    public static void Run()
    {
        ReserveReopensTheRoad();
        SupplyDistanceAloneIsAPocket();
        DirectOrderStays();
        HumanIdleStays();
        DelegatedCommandMay();
        AttackStaysWhenNoReserveCanReach();
        RuleOffFillsByThreat();
        ReadingDoesNotChangeTheFingerprint();
    }

    static void ReserveReopensTheRoad()
    {
        var world = Map();
        var pocket = Pocket(world, days: 14, distance: -1);
        var reserve = Reserve(world, ReserveProvince);
        Enemy(world, 12, 6f);
        var divisions = pocket.Divisions;
        var days = pocket.DaysCutOff;
        var strength = pocket.Strength;
        Check(!world.Defines.FrontStaffMovesAdvances, "advances are not spread along the front");
        Check(world.Defines.SupplyCutoffDays == 14, "the 14-day bleed span is unchanged");
        FrontManager.Balance(world, world.Wars[0]);
        Check(world.Armies.Contains(pocket) && pocket.Divisions == divisions && pocket.ProvinceId == PocketProvince &&
              pocket.DaysCutOff == days && pocket.OrderKind == ArmyOrder.DefendFront,
            "the pocket is not disbanded and is not the formation sent");
        Check(reserve.OrderKind == ArmyOrder.DefendFront && reserve.OrderProvinceId == Mouth && reserve.Route.Contains(Mouth),
            "a free reserve is ordered onto the reconnecting post, not the hotter hole: post " + reserve.OrderProvinceId);
        Check(reserve.StaffNote == StaffNote.ReopenRoad, "the move is recorded as reopening the road");
        Check(StaffReasons.Explain(world, reserve) == Loc.Get(Reopen),
            "the reason says the reserve is reopening the road: " + StaffReasons.Explain(world, reserve));
        Check(StaffReasons.Label(world, reserve) == Loc.Get("Reopen road"), "the clipped label is the road mark");
        Check(!world.Defines.FrontStaffMovesAdvances, "the look does not turn advance-spreading on");
        var kind = pocket.OrderKind;
        pocket.OrderKind = ArmyOrder.AdvanceFront;
        pocket.PlanActive = false;
        pocket.Planning = 1f;
        FrontPlanner.UpdateAiExecution(world, world.Wars[0]);
        Check(!pocket.PlanActive && StaffReasons.SupplyHoldsAttack(world, pocket),
            "after the cutoff span the pocket still cannot start an attack");
        Check(!AssaultReadiness.Assess(world, pocket).Ready, "the assault read still refuses the dry pocket");
        pocket.OrderKind = kind;
        pocket.PlanActive = false;
        var opened = world.GetType().GetProperty(nameof(Campaign.StepOpensNewDay));
        opened!.SetValue(world, false);
        Attrition.Tick(world);
        Check(pocket.Strength < strength && world.Armies.Contains(pocket) && pocket.Divisions == divisions &&
              pocket.DaysCutOff == days,
            "the existing bleed still takes strength and still does not remove the pocket");
    }

    static void SupplyDistanceAloneIsAPocket()
    {
        var world = Map();
        var pocket = Pocket(world, days: 0, distance: -1);
        var reserve = Reserve(world, ReserveProvince);
        Enemy(world, 12, 6f);
        FrontManager.Balance(world, world.Wars[0]);
        Check(pocket.ProvinceId == PocketProvince && pocket.Divisions > 0f && pocket.DaysCutOff == 0,
            "a formation with no supply distance is a pocket and is not disbanded");
        Check(reserve.OrderProvinceId == Mouth && reserve.StaffNote == StaffNote.ReopenRoad,
            "no counted days are required before the reserve is sent to reopen the road");
    }

    static void DirectOrderStays()
    {
        var world = Map();
        var pocket = Pocket(world, days: 4, distance: -1);
        var reserve = Reserve(world, ReserveProvince);
        reserve.DirectOrder = true;
        reserve.OrderProvinceId = ReserveProvince;
        Enemy(world, 12, 6f);
        FrontManager.Balance(world, world.Wars[0]);
        Check(reserve.DirectOrder && reserve.OrderKind == ArmyOrder.ReserveFront && reserve.OrderProvinceId == ReserveProvince &&
              reserve.Route.Count == 0 && reserve.StaffNote == StaffNote.None,
            "a direct player order is not overwritten");
        Check(StaffReasons.Explain(world, reserve) == Loc.Get("The direct order was kept."),
            "the reason says the direct order was kept");
        Check(pocket.ProvinceId == PocketProvince && pocket.Divisions > 0f, "the pocket stays when the reserve cannot be taken");
    }

    static void HumanIdleStays()
    {
        var world = Map();
        world.Nations[0].IsHuman = true;
        var pocket = Pocket(world, days: 4, distance: -1);
        var reserve = Reserve(world, ReserveProvince);
        var idle = Unit(world, 8, 10, ArmyOrder.Manual);
        idle.AssignedWarId = -1;
        Enemy(world, 12, 6f);
        FrontManager.Tick(world);
        Check(reserve.OrderKind == ArmyOrder.ReserveFront && reserve.ProvinceId == ReserveProvince && reserve.Route.Count == 0,
            "without delegation a human's reserve is not moved onto the road");
        Check(idle.OrderKind == ArmyOrder.Manual && idle.AssignedWarId < 0 && idle.Route.Count == 0,
            "without delegation a human's idle division is not moved");
        Check(pocket.ProvinceId == PocketProvince && pocket.Divisions > 0f, "the human's pocket is not disbanded");
    }

    static void DelegatedCommandMay()
    {
        var world = Map();
        world.Nations[0].IsHuman = true;
        Delegation.Set(world.Nations[0], Delegation.Command, true);
        Pocket(world, days: 4, distance: -1);
        var reserve = Reserve(world, ReserveProvince);
        Enemy(world, 12, 6f);
        FrontManager.Balance(world, world.Wars[0]);
        Check(reserve.OrderProvinceId == Mouth && reserve.StaffNote == StaffNote.ReopenRoad &&
              StaffReasons.Explain(world, reserve) == Loc.Get(Reopen),
            "delegated command may send the reserve to reopen the road");
    }

    static void AttackStaysWhenNoReserveCanReach()
    {
        var world = Map();
        world.Wars[0].AttackerFront.Remove(HotPost);
        var pocket = Pocket(world, days: 6, distance: -1);
        var stranded = Reserve(world, PocketProvince);
        stranded.SupplyDistance = 1;
        var attack = Unit(world, 11, ReserveProvince, ArmyOrder.AdvanceFront);
        attack.OrderProvinceId = 3;
        var spear = Unit(world, 12, 10, ArmyOrder.Spearhead);
        spear.OrderProvinceId = 12;
        Enemy(world, 3, 8f);
        FrontManager.Balance(world, world.Wars[0]);
        Check(attack.OrderKind == ArmyOrder.AdvanceFront && attack.OrderProvinceId == 3 && attack.Route.Count == 0,
            "when no reserve can reach, an attack is not pulled off the line");
        Check(spear.OrderKind == ArmyOrder.Spearhead && spear.OrderProvinceId == 12 && spear.Route.Count == 0,
            "a spearhead stays concentrated");
        Check(pocket.ProvinceId == PocketProvince && pocket.Divisions > 0f && stranded.ProvinceId == PocketProvince,
            "the pocket and the stranded reserve are not disbanded");
        Check(!world.Defines.FrontStaffMovesAdvances, "advance-spreading stays off");
    }

    static void RuleOffFillsByThreat()
    {
        var world = Map();
        world.Defines.FrontStaffRelievesPockets = false;
        var pocket = Pocket(world, days: 9, distance: -1);
        var reserve = Reserve(world, ReserveProvince);
        Enemy(world, 12, 6f);
        FrontManager.Balance(world, world.Wars[0]);
        Check(reserve.OrderProvinceId == HotPost && reserve.StaffNote != StaffNote.ReopenRoad,
            "with the rule off the reserve fills the threatened hole, not the road: post " + reserve.OrderProvinceId);
        Check(StaffReasons.Explain(world, reserve) != Loc.Get(Reopen),
            "the road sentence is absent when the relief move did not happen");
        Check(pocket.ProvinceId == PocketProvince && pocket.Divisions > 0f, "turning the rule off does not disband the pocket");
        var before = CampaignFingerprint.Of(world);
        _ = StaffReasons.Explain(world, reserve);
        Check(CampaignFingerprint.Of(world) == before, "reading the ordinary fill does not change the fingerprint");
    }

    static void ReadingDoesNotChangeTheFingerprint()
    {
        var world = Map();
        var pocket = Pocket(world, days: 3, distance: -1);
        var reserve = Reserve(world, ReserveProvince);
        Enemy(world, 12, 6f);
        FrontManager.Balance(world, world.Wars[0]);
        var kind = reserve.OrderKind;
        var post = reserve.OrderProvinceId;
        var before = CampaignFingerprint.Of(world);
        var english = StaffReasons.Explain(world, reserve);
        _ = StaffReasons.Label(world, reserve);
        _ = StaffReasons.Explain(world, pocket);
        Check(english == Loc.Get(Reopen) && CampaignFingerprint.Of(world) == before &&
              reserve.OrderKind == kind && reserve.OrderProvinceId == post,
            "reading the reason does not issue an order and does not change the fingerprint");
        var language = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var russian = StaffReasons.Explain(world, reserve);
            Check(russian.Contains("заново открывает дорогу") && CampaignFingerprint.Of(world) == before,
                "the Russian sentence is the same fact and does not change the fingerprint: " + russian);
        }
        finally { Loc.Language = language; }
        Check(SaveSystem.FormatVersion == 2, "save format stays 2");
    }

    static Campaign Map()
    {
        const int width = 96, height = 32;
        var owner = new ushort[]
        {
            0,
            502, 500, 501, 500, 500, 502,
            502, 501, 501, 500, 500, 501
        };
        var states = new ushort[width * height];
        var controllers = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var province = (y < 16 ? 6 : 0) + x / 16 + 1;
            var index = y * width + x;
            states[index] = (ushort)(3000 + province);
            controllers[index] = owner[province];
            rgb[index * 3 + 2] = (byte)province;
        }
        var map = ProvinceTopology.FromColorRaster(states, controllers, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 12).ToDictionary(p => p, p => p));
        var campaign = new Campaign(20261109) { ScenarioId = "pocket_relief" };
        campaign.Nations.Add(new Nation
        {
            Id = 0, MapIndex = 500, Tag = "ATK", Name = "Attacker", Population = 4_000_000, IsHuman = false
        });
        campaign.Nations.Add(new Nation
        {
            Id = 1, MapIndex = 501, Tag = "DEF", Name = "Defender", Population = 4_000_000, IsHuman = false
        });
        campaign.Nations.Add(new Nation
        {
            Id = 2, MapIndex = 502, Tag = "NEU", Name = "Neutral", Population = 1_000_000, IsHuman = false
        });
        Check(campaign.Nations[0].Id == 0 && campaign.NationById(0) == campaign.Nations[0],
            "nation id 0 stands at index 0");
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, (ushort[])map.ProvinceToNation.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(campaign, new float[map.ProvinceCount + 1]);
        var war = new StrategicWar { Id = 3, AttackerId = 0, DefenderId = 1 };
        war.AttackerFront.Add(PocketProvince);
        war.AttackerFront.Add(Mouth);
        war.AttackerFront.Add(HotPost);
        campaign.Wars.Add(war);
        return campaign;
    }

    static Army Pocket(Campaign campaign, int days, int distance)
    {
        var army = Unit(campaign, 1, PocketProvince, ArmyOrder.DefendFront);
        army.CutOff = true;
        army.DaysCutOff = days;
        army.SupplyDistance = distance;
        army.Divisions = 2f;
        army.Strength = 1f;
        army.PlanActive = false;
        return army;
    }

    static Army Reserve(Campaign campaign, int province)
    {
        var army = Unit(campaign, 2, province, ArmyOrder.ReserveFront);
        army.SupplyDistance = 2;
        army.CutOff = false;
        army.DaysCutOff = 0;
        return army;
    }

    static void Enemy(Campaign campaign, int province, float divisions) =>
        Unit(campaign, 3, province, ArmyOrder.Manual, nation: 1, divisions: divisions);

    static Army Unit(Campaign campaign, int id, int province, ArmyOrder duty, int nation = 0, float divisions = 1f)
    {
        var centre = campaign.ProvinceMap.CenterOf(province);
        var army = new Army
        {
            Id = id, NationId = nation, ProvinceId = province,
            StateMapIndex = campaign.ProvinceMap.ProvinceToState[province], OrderProvinceId = province,
            AssignedWarId = campaign.Wars[0].Id, OrderKind = duty, Divisions = divisions,
            Latitude = centre.Latitude, Longitude = centre.Longitude, OrderIssuedDay = -100,
            Strength = 1f, Organisation = 1f, SupplyLevel = 1f, DirectOrder = false, FrontLineId = -1
        };
        campaign.Armies.Add(army);
        return army;
    }
}
