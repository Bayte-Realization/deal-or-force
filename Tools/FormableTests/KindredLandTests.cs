using System.Linq;
using DealOrForce.Simulation;

static class KindredLandTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new System.Exception("kindred land: " + label);
        System.Console.WriteLine("PASS kindred land: " + label);
    }

    public static void Run()
    {
        var c = new Campaign(20261015);
        var buyer = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "POL", Name = "Poland", Population = 100_000,
            Treasury = 500f, Gdp = 80f, IsHuman = false, CapitalStateMapIndex = 1
        };
        var seller = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "BLR", Name = "Belarus", Population = 100_000,
            Treasury = 20f, IsHuman = false, CapitalStateMapIndex = 9
        };
        c.Nations.Add(buyer);
        c.Nations.Add(seller);
        const int width = 5, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        void Cell(int x, int y, ushort region, ushort owner)
        {
            states[y * width + x] = region;
            owners[y * width + x] = owner;
        }
        Cell(1, 1, 1, 1);
        Cell(2, 1, 2, 2);
        Cell(4, 1, 9, 2);
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(c, map);
        var home = new ScenarioState { MapIndex = 1, NationMapIndex = 1, Id = "warsaw", Name = "Warsaw", Population = 40 };
        var kin = new ScenarioState { MapIndex = 2, NationMapIndex = 2, Id = "lviv", Name = "Lviv", Population = 10 };
        kin.PopulationGroups.Add(new PopulationGroup { Culture = "polish", Size = 8 });
        kin.PopulationGroups.Add(new PopulationGroup { Culture = "belarusian", Size = 2 });
        var kept = new ScenarioState { MapIndex = 9, NationMapIndex = 2, Id = "minsk", Name = "Minsk", Population = 30 };
        c.States.Add(home);
        c.States.Add(kin);
        c.States.Add(kept);

        kin.PopulationGroups.Clear();
        kin.PopulationGroups.Add(new PopulationGroup { Culture = "belarusian", Size = 10 });
        var offers = c.DealOffers.Count;
        Check(!AiStatecraft.TryBuyKindredLand(c, buyer) && c.DealOffers.Count == offers,
            "a neighbour's own people are not bought just because they touch");

        kin.PopulationGroups.Clear();
        kin.PopulationGroups.Add(new PopulationGroup { Culture = "polish", Size = 9 });
        kin.PopulationGroups.Add(new PopulationGroup { Culture = "belarusian", Size = 1 });
        buyer.DecisionAvailableDay.Remove("kindred/" + seller.Id);
        int kinProvince = 0;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (map.ProvinceToNation[province] != 2) continue;
            foreach (var next in map.Neighbors[province])
                if (next > 0 && next <= map.ProvinceCount && map.ProvinceToNation[next] == 1) kinProvince = province;
        }
        Check(kinProvince > 0, "the kindred province touches Poland");
        var note = AiStatecraft.TryBuyKindredLand(c, buyer);
        Check(map.ProvinceToNation.Count(owner => owner == 1) >= 1, "Poland still has its own ground: " + note);
        if (note)
            Check(map.ProvinceToNation[kinProvince] == 1 || c.DealOffers.Any(offer => offer.FromId == buyer.Id && offer.ToId == seller.Id),
                "a Polish majority next door is asked for, or bought when the price is fair");
        else
            Check(map.ProvinceToNation[kinProvince] == 2, "when the cheque cannot be written, the region stays");
    }
}
