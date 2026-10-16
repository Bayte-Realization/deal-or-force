using DealOrForce.Simulation;

static class DelegatedFrontTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new System.Exception("delegated front: " + label);
        System.Console.WriteLine("PASS delegated front: " + label);
    }

    public static void Run()
    {
        var (campaign, home, idle, pinned) = World();
        FrontManager.Tick(campaign);
        Check(idle.OrderKind == ArmyOrder.Manual && idle.AssignedWarId < 0,
            "without the staff, an idle division stays where the player left it");
        Check(pinned.OrderKind == ArmyOrder.Manual && pinned.DirectOrder,
            "a direct order is not a vacancy");

        Delegation.Set(home, Delegation.Command, true);
        FrontManager.Tick(campaign);
        Check(idle.AssignedWarId == 1 && idle.OrderKind != ArmyOrder.Manual,
            "handed the field, the staff sends the idle division to the war: " + idle.OrderKind);
        Check(pinned.OrderKind == ArmyOrder.Manual && pinned.AssignedWarId < 0,
            "a direct order is still the player's after the staff is handed the rest");

        Delegation.Set(home, Delegation.Command, false);
        var late = new Army
        {
            Id = 3, NationId = home.Id, ProvinceId = idle.ProvinceId,
            OrderKind = ArmyOrder.Manual, Divisions = 1, Strength = 1f, Organisation = 1f
        };
        campaign.Armies.Add(late);
        FrontManager.Tick(campaign);
        Check(late.OrderKind == ArmyOrder.Manual && late.AssignedWarId < 0,
            "taking the staff back stops the call-up");
    }

    static (Campaign Campaign, Nation Home, Army Idle, Army Pinned) World()
    {
        var campaign = new Campaign(20261015);
        var home = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "AAA", Name = "Home", Population = 100,
            IsHuman = true, CapitalStateMapIndex = 1
        };
        var other = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "BBB", Name = "Other", Population = 100,
            IsHuman = false, CapitalStateMapIndex = 9
        };
        campaign.Nations.Add(home);
        campaign.Nations.Add(other);
        const int width = 4, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        void Cell(int x, int y, ushort region, ushort owner)
        {
            states[y * width + x] = region;
            owners[y * width + x] = owner;
        }
        Cell(1, 1, 1, 1);
        Cell(2, 1, 9, 2);
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, (ushort[])map.ProvinceToNation.Clone());
        int homeProvince = 0;
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToNation[province] == 1) { homeProvince = province; break; }
        var war = new StrategicWar { Id = 1, AttackerId = other.Id, DefenderId = home.Id };
        war.DefenderFront.Add(homeProvince);
        campaign.Wars.Add(war);
        var idle = new Army
        {
            Id = 1, NationId = home.Id, ProvinceId = homeProvince,
            OrderKind = ArmyOrder.Manual, Divisions = 1, Strength = 1f, Organisation = 1f
        };
        var pinned = new Army
        {
            Id = 2, NationId = home.Id, ProvinceId = homeProvince, DirectOrder = true,
            OrderKind = ArmyOrder.Manual, Divisions = 1, Strength = 1f, Organisation = 1f
        };
        campaign.Armies.Add(idle);
        campaign.Armies.Add(pinned);
        return (campaign, home, idle, pinned);
    }
}
