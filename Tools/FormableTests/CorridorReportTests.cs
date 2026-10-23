using System;
using System.Linq;
using DealOrForce.Simulation;

static class CorridorReportTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("corridor: " + label);
        Console.WriteLine("PASS corridor: " + label);
    }

    public static void Run()
    {
        var c = new Campaign(20261015);
        c.Defines.CorridorMinimumProvinces = 1;
        var home = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "AAA", Name = "Home", Population = 100,
            Treasury = 40f, IsHuman = true, CapitalStateMapIndex = 1
        };
        var other = new Nation { Id = 1, MapIndex = 2, Tag = "BBB", Name = "Other", Population = 100, Treasury = 40f };
        c.Nations.Add(home);
        c.Nations.Add(other);
        c.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 1, Id = "home", Name = "Home", Population = 50 });
        c.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 1, Id = "far", Name = "Far", Population = 20 });
        c.States.Add(new ScenarioState { MapIndex = 3, NationMapIndex = 2, Id = "gate", Name = "Gate", Population = 20 });
        c.States.Add(new ScenarioState { MapIndex = 4, NationMapIndex = 2, Id = "rest", Name = "Rest", Population = 40 });
        const int width = 5, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        void Cell(int x, int y, ushort region, ushort owner)
        {
            states[y * width + x] = region;
            owners[y * width + x] = owner;
        }
        Cell(1, 1, 1, 1);
        Cell(2, 1, 3, 2);
        Cell(3, 1, 2, 1);
        Cell(2, 2, 4, 2);
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(c, map);

        Check(!Corridors.TryReport(c, other, out _), "a country in one piece is not offered a corridor");
        Check(Corridors.TryReport(c, home, out var report), "a detached province is reported");
        Check(report.Provinces >= 1 && report.WayProvinces >= 1 && report.Holder.Length > 0,
            "the report names the piece and who holds the way");
        var finger = CampaignFingerprint.Of(c);
        Check(Corridors.TryReport(c, home, out _) && CampaignFingerprint.Of(c) == finger,
            "reading the cut-off piece changes nothing");
        ushort OwnerOf(ushort region)
        {
            for (var province = 1; province <= map.ProvinceCount; province++)
                if (map.ProvinceToState[province] == region) return map.ProvinceToNation[province];
            return 0;
        }
        var note = Corridors.ProposeBuy(c, home);
        Check(OwnerOf(2) == 1, "the detached piece itself is not sold: " + note);
        if (note.Length == 0)
            Check(OwnerOf(3) == 1, "an accepted ask joins the way through");
        else
            Check(OwnerOf(3) == 2 && c.DealOffers.All(offer => !offer.Pending), "a refused ask leaves the way where it was: " + note);
    }
}
