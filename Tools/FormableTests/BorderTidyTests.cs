using System.Linq;
using DealOrForce.Simulation;

static class BorderTidyTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new System.Exception("border tidy: " + label);
        System.Console.WriteLine("PASS border tidy: " + label);
    }

    public static void Run()
    {
        var c = new Campaign(20261015);
        var home = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "AAA", Name = "Home", Population = 100,
            Treasury = 80f, IsHuman = false, CapitalStateMapIndex = 1
        };
        var other = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "BBB", Name = "Other", Population = 100,
            Treasury = 80f, IsHuman = false, CapitalStateMapIndex = 9
        };
        var isle = new Nation
        {
            Id = 2, MapIndex = 3, Tag = "CCC", Name = "Isle", Population = 10,
            Treasury = 10f, IsHuman = false, CapitalStateMapIndex = 8
        };
        c.Nations.Add(home);
        c.Nations.Add(other);
        c.Nations.Add(isle);
        const int width = 6, height = 4;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        void Cell(int x, int y, ushort region, ushort owner)
        {
            states[y * width + x] = region;
            owners[y * width + x] = owner;
        }
        Cell(1, 1, 1, 1);
        Cell(2, 1, 1, 1);
        Cell(1, 2, 1, 1);
        Cell(2, 2, 3, 2);
        Cell(4, 1, 4, 1);
        Cell(5, 3, 8, 3);
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(c, map);
        c.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 1, Id = "home", Name = "Home", Population = 40 });
        c.States.Add(new ScenarioState { MapIndex = 3, NationMapIndex = 2, Id = "dot", Name = "Dot", Population = 5 });
        c.States.Add(new ScenarioState { MapIndex = 4, NationMapIndex = 1, Id = "scrap", Name = "Scrap", Population = 5 });
        c.States.Add(new ScenarioState { MapIndex = 8, NationMapIndex = 3, Id = "isle", Name = "Isle", Population = 2 });

        Check(Corridors.TryReportPore(c, home, out var holder, out var count) && count == 1 && holder == other.DisplayName,
            "a foreign province shut in the border is named, and a sea island is not: " + holder);
        var before = CampaignFingerprint.Of(c);
        Check(Corridors.TryReportPore(c, home, out _, out _) && CampaignFingerprint.Of(c) == before,
            "naming the pore does not buy it");

        int Owner(ushort region)
        {
            for (var province = 1; province <= map.ProvinceCount; province++)
                if (map.ProvinceToState[province] == region) return map.ProvinceToNation[province];
            return 0;
        }
        Check(Owner(4) == 1, "the scrap starts as ours");
        var note = Corridors.ProposePore(c, home);
        Check(Owner(1) == 1, "buying a pore does not give away the country around it: " + note);
        if (note.Length == 0) Check(Owner(3) == 1, "an accepted purchase takes the dot and not the rest of them");
        else Check(Owner(3) == 2, "a refused purchase leaves the dot: " + note);
    }
}
