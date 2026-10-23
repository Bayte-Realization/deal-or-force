using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Deterrence is a price rather than a script, so what a test can hold in its hands is the
/// arithmetic: who holds a deterrent, what a direct war between two holders costs on top of
/// everything else, and that every other war stays an ordinary war. No century is simulated
/// and none needs to be - the Cold War shape is a property of the sum, and the sum is here.
/// </summary>
static class DeterrenceTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("deterrence: " + label); Console.WriteLine("PASS deterrence: " + label); }

    public static void Run()
    {
        Tiers();
        Price();
    }

    static void Tiers()
    {
        var c = new Campaign(824, new Defines());
        var rus = new Nation { Tag = "RUS", MapIndex = 501, Population = 100_000_000, Gdp = 1000, OutputScale = 1 };
        var bra = new Nation { Tag = "BRA", MapIndex = 502, Population = 200_000_000, Gdp = 800, OutputScale = 1 };
        c.AddNation(rus);
        c.AddNation(bra);

        Check(Deterrence.Holders.Count >= 9, "the 2024 club is authored in data, not in code");
        Check(Deterrence.TierOf(c, rus) == 1, "an authored holder holds tier 1");
        Check(Deterrence.TierOf(c, bra) == 0, "a country with no row and no research holds nothing");

        // Proliferation arrives the way everything else arrives in this game: by being
        // researched. The cache behind the bonus keys off the count of what is known, so
        // adding the technology is exactly what the research system does when it finishes.
        bra.Technologies.Add("nuclear_deterrence");
        Check(Deterrence.TierOf(c, bra) == 1, "researching the deterrent technology joins the club");

        // Off means off: with the define down, every holder is an ordinary neighbour again,
        // which is what a mod for an era before the bomb gets without shipping a second tree.
        var off = new Campaign(824, new Defines { MutualDeterrenceEnabled = false });
        off.AddNation(new Nation { Tag = "RUS", MapIndex = 501, Population = 100_000_000, Gdp = 1000, OutputScale = 1 });
        Check(Deterrence.TierOf(off, off.Nations[0]) == 0, "with the rule off nobody holds anything");
    }

    static void Price()
    {
        var c = World();
        var rus = c.Nations[0];
        var usa = c.Nations[1];
        var bra = c.Nations[2];

        var between = AiStatecraft.JudgeWarParts(c, rus, usa);
        Check(between.PriceParts.Any(p => p.Part == "mutual deterrence" &&
              MathF.Abs(p.Amount - c.Defines.AiMutualDeterrencePrice) < 0.001f),
            "a direct war between two holders carries the mutual deterrence price");
        Check(!AiStatecraft.JudgeWarParts(c, rus, bra).PriceParts.Any(p => p.Part == "mutual deterrence"),
            "a holder's war against anybody else stays an ordinary war");

        c.Defines.MutualDeterrenceEnabled = false;
        Check(!AiStatecraft.JudgeWarParts(c, rus, usa).PriceParts.Any(p => p.Part == "mutual deterrence"),
            "with the rule off the price is not on the list");
    }

    /// <summary>
    /// Three countries on one strip of map: two deterrent holders and one ordinary neighbour
    /// between the arithmetic and the absence of it. Built the way every hermetic fixture
    /// here must be - regions added by hand, the lazy state index dropped, the capitals
    /// named after the attach, because attaching the map re-locates them from coordinates
    /// a synthetic world never authored.
    /// </summary>
    static Campaign World()
    {
        var c = new Campaign(824, new Defines()) { ScenarioId = "deterrence_fixture" };
        c.AddNation(new Nation { Tag = "RUS", MapIndex = 501, Population = 100_000_000, Gdp = 1000, OutputScale = 1 });
        c.AddNation(new Nation { Tag = "USA", MapIndex = 502, Population = 300_000_000, Gdp = 2000, OutputScale = 1 });
        c.AddNation(new Nation { Tag = "BRA", MapIndex = 503, Population = 200_000_000, Gdp = 800, OutputScale = 1 });
        for (var i = 0; i < 3; i++)
            c.States.Add(new ScenarioState { Id = "deterrence_region_" + i, Name = "Deterrence region " + i,
                MapIndex = 3001 + i, NationMapIndex = 501 + i, NationId = c.Nations[i].Tag, Population = 10_000_000 });
        c.InvalidateStateIndex();
        var cells = Enumerable.Range(0, 900).Select(i => (ushort)(3001 + i / 300)).ToArray();
        var owners = Enumerable.Range(0, 900).Select(i => (ushort)(501 + i / 300)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, 45, 20, _ => false));
        for (var i = 0; i < 3; i++)
        {
            c.Nations[i].CapitalStateMapIndex = 3001 + i;
            c.Nations[i].CapitalProvinceId = i * 300 + 1;
        }
        return c;
    }
}
