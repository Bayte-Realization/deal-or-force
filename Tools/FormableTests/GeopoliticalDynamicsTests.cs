using System;
using System.Linq;
using DealOrForce.Simulation;

static class GeopoliticalDynamicsTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }

    public static void Run()
    {
        TestGeopoliticalCycle();
        TestWarAimResolveStalemateDiscount();
        TestCapitalEvacuationAndRestoration();
        TestCoalitionExpansion();
    }

    private static void TestGeopoliticalCycle()
    {
        var campaign = new Campaign(424242) { ScenarioId = "geopolitical_cycle_test" };
        var c0 = GeopoliticalCycle.GlobalTensionOffset(campaign);
        Check(c0 == 0f, "Day 0 global tension offset is zero");

        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, 500L * 24);
        var c500 = GeopoliticalCycle.GlobalTensionOffset(campaign);
        Check(c500 >= -8.0f && c500 <= 10.0f, "Day 500 global tension offset is within bounds");

        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, 1500L * 24);
        var c1500 = GeopoliticalCycle.GlobalTensionOffset(campaign);
        Check(c1500 != c500, "Global tension moves organically across historical cycles");
    }

    private static void TestWarAimResolveStalemateDiscount()
    {
        var campaign = new Campaign(1001) { ScenarioId = "war_aim_stalemate_test" };
        var a = new Nation { Id = 0, MapIndex = 1, Tag = "BLR", Stability = 0.8f, WarSupport = 0.8f, Population = 5_000_000 };
        var b = new Nation { Id = 1, MapIndex = 2, Tag = "POL", Stability = 0.8f, WarSupport = 0.8f, Population = 35_000_000 };
        campaign.Nations.AddRange(new[] { a, b });

        var war = new StrategicWar
        {
            Id = 1,
            AttackerId = a.Id,
            DefenderId = b.Id,
            WarScore = -15f,
            StartedDay = 0
        };
        campaign.Wars.Add(war);

        // Day 50: early war -> full aim resolve penalty
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, 50L * 24);
        var earlyAcceptance = PeaceNegotiation.Acceptance(campaign, war, a.Id, PeaceTerms.StatusQuo);

        // Day 800 + stalled war -> stalemate discount softens resolve against Status Quo
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, 800L * 24);
        war.Stalled = true;
        var lateAcceptance = PeaceNegotiation.Acceptance(campaign, war, a.Id, PeaceTerms.StatusQuo);

        Check(lateAcceptance > earlyAcceptance,
            "Prolonged stalemated wars soften war aim resolve against de facto Status Quo");
    }

    private static void TestCapitalEvacuationAndRestoration()
    {
        var campaign = new Campaign(2026) { ScenarioId = "capital_evac_test" };
        var nation = new Nation
        {
            Id = 0,
            MapIndex = 1,
            Tag = "TEST",
            CapitalStateMapIndex = 10,
            CapitalProvinceId = 5,
            CapitalName = "CapitalCity",
            WarSupport = 0.80f,
            StabilityGovernance = 0.05f
        };
        campaign.Nations.Add(nation);

        const int width = 6, height = 3;
        var cells = new ushort[width * height];
        var owners = new ushort[cells.Length];
        for (var x = 1; x <= 4; x++)
        {
            cells[width + x] = (ushort)(x <= 2 ? 10 : 20);
            owners[width + x] = 1;
        }
        var map = ProvinceTopology.Build(cells, owners, width, height, _ => false);
        campaign.AttachProvinceTopology(map);

        var capitalState = new ScenarioState { MapIndex = 10, Id = "capital_state", NationMapIndex = 1, Population = 2_000_000 };
        var backupState = new ScenarioState { MapIndex = 20, Id = "backup_state", NationMapIndex = 1, Population = 1_500_000 };
        campaign.States.AddRange(new[] { capitalState, backupState });

        var initialSupport = nation.WarSupport;
        var initialGov = nation.StabilityGovernance;

        // Enemy occupies capital province 5
        var pCapital = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToState[p] == 10);
        campaign.SetProvinceController(pCapital, 99); // enemy occupies capital
        nation.CapitalProvinceId = pCapital;

        CapitalCore.Tick(campaign);

        Check(nation.GovernmentSeatProvinceId > 0, "Government evacuates to a backup seat when legal capital is occupied");
        Check(nation.WarSupport < initialSupport, "Evacuation inflicts national morale shock to war support");
        Check(nation.StabilityGovernance < initialGov, "Evacuation strains governance stability during wartime dislocation");

        // Capital liberated!
        campaign.SetProvinceController(pCapital, 1); // liberated
        var postEvacSupport = nation.WarSupport;
        var postEvacGov = nation.StabilityGovernance;

        CapitalCore.Tick(campaign);

        Check(nation.GovernmentSeatProvinceId == 0, "Government returns to legal capital upon liberation");
        Check(nation.WarSupport > postEvacSupport, "Liberation rallies national war support");
        Check(nation.StabilityGovernance > postEvacGov, "Liberation restores governance stability");
    }

    private static void TestCoalitionExpansion()
    {
        var campaign = new Campaign(5555) { ScenarioId = "coalition_expand_test" };
        var hegemon = new Nation { Id = 0, MapIndex = 1, Tag = "HEG", Population = 50_000_000, Notoriety = 50f, CapitalLatitude = 50f, CapitalLongitude = 10f };
        var n1 = new Nation { Id = 1, MapIndex = 2, Tag = "N1", Population = 10_000_000, CapitalLatitude = 51f, CapitalLongitude = 11f };
        var n2 = new Nation { Id = 2, MapIndex = 3, Tag = "N2", Population = 10_000_000, CapitalLatitude = 52f, CapitalLongitude = 12f };
        var n3 = new Nation { Id = 3, MapIndex = 4, Tag = "N3", Population = 10_000_000, CapitalLatitude = 53f, CapitalLongitude = 13f };
        campaign.Nations.AddRange(new[] { hegemon, n1, n2, n3 });

        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, 7L * 24);
        Statecraft.Tick(campaign);

        var coalition = Statecraft.CoalitionAgainst(campaign, hegemon.Id);
        Check(coalition is not null, "High notoriety forms a containment coalition");
        Check(coalition.MemberIds.Count >= 2, "Containment coalition includes multiple threatened regional nations");
    }
}
