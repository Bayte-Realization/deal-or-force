using System.Linq;
using DealOrForce.Simulation;

static class PeaceCompactTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new System.Exception("peace compact: " + label);
        System.Console.WriteLine("PASS peace compact: " + label);
    }

    public static void Run()
    {
        var c = new Campaign(20261015);
        c.Defines.PeaceWholeRegions = false;
        var winner = new Nation { Id = 0, MapIndex = 1, Tag = "WIN", Name = "Winner", Population = 100, CapitalStateMapIndex = 1 };
        var loser = new Nation { Id = 1, MapIndex = 2, Tag = "LOS", Name = "Loser", Population = 100, CapitalStateMapIndex = 9 };
        c.Nations.Add(winner);
        c.Nations.Add(loser);
        const int width = 8, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        void Cell(int x, int y, ushort region, ushort owner)
        {
            states[y * width + x] = region;
            owners[y * width + x] = owner;
        }
        Cell(1, 1, 1, 1);
        Cell(2, 1, 2, 2);
        Cell(2, 0, 8, 2);
        Cell(3, 0, 9, 2);
        Cell(3, 1, 3, 2);
        Cell(6, 1, 7, 2);
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(c, map);
        int At(int x)
        {
            for (var province = 1; province <= map.ProvinceCount; province++)
                if (map.ProvinceToState[province] == x) return province;
            return 0;
        }
        var front = At(2);
        var hole = At(3);
        var cap = At(9);
        var pocket = At(7);
        Check(front > 0 && hole > 0 && cap > 0 && pocket > 0, "the fixture has a front, a pore, and a pocket");
        var war = new StrategicWar { Id = 1, AttackerId = winner.Id, DefenderId = loser.Id };
        var compact = PeaceShape.Compact(c, winner, new[] { front, cap, At(8), pocket }, war);
        Check(compact.Contains(front), "the province that touches home stays");
        Check(compact.Contains(hole), "a province shut inside the new line is taken with the line");
        Check(!compact.Contains(pocket), "a pocket that does not touch home is not a peace");
        Check(compact.Contains(cap), "the line around the pore stays");
    }
}
