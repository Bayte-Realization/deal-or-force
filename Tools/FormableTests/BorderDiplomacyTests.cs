using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Ugly borders fixed without a war (BorderDiplomacy, BorderShape.Relief, the deal valuation):
/// a fair swap accepted, an enclave bought, and a refusal that turns into pressure.
/// </summary>
static class BorderDiplomacyTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("border diplomacy: " + note); Console.WriteLine("PASS border diplomacy: " + note); }

    const int Cols = 10, Rows = 4, Block = 16;
    static int Region(int col, int row) => 5001 + col + row * Cols;

    /// <summary>
    /// Two countries side by side, ten columns of regions by four rows: A holds the five on the
    /// left, B the five on the right, except the regions named in <paramref name="toB"/> (inside
    /// A's half, so B's enclaves) and <paramref name="toA"/> (A's enclaves inside B's half).
    /// </summary>
    static (Campaign c, Nation a, Nation b) World((int col, int row)[] toB, (int col, int row)[] toA, long enclavePopulation = 1_000_000)
    {
        var c = new Campaign(77) { ScenarioId = "border_diplomacy_fixture" };
        var a = new Nation { Id = 0, MapIndex = 801, Tag = "BDA", Name = "Westland", Treasury = 100, Population = 20_000_000, Gdp = 200, Stability = 0.6f };
        var b = new Nation { Id = 1, MapIndex = 802, Tag = "BDB", Name = "Eastland", Treasury = 100, Population = 20_000_000, Gdp = 200, Stability = 0.6f };
        c.Nations.Add(a); c.Nations.Add(b);
        int OwnerOf(int col, int row) =>
            toB.Contains((col, row)) ? b.MapIndex : toA.Contains((col, row)) ? a.MapIndex : col < Cols / 2 ? a.MapIndex : b.MapIndex;
        for (var row = 0; row < Rows; row++)
        for (var col = 0; col < Cols; col++)
        {
            var owner = OwnerOf(col, row);
            var enclave = toB.Contains((col, row)) || toA.Contains((col, row));
            c.States.Add(new ScenarioState { MapIndex = Region(col, row), NationMapIndex = owner, NationId = owner == a.MapIndex ? a.Tag : b.Tag,
                Id = $"bd_{col}_{row}", Name = $"Region {col}-{row}", Population = enclave ? enclavePopulation : 1_000_000 });
        }
        var width = Cols * Block; var height = Rows * Block;
        var cells = new ushort[width * height]; var owners = new ushort[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            cells[y * width + x] = (ushort)Region(x / Block, y / Block);
            owners[y * width + x] = (ushort)OwnerOf(x / Block, y / Block);
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, width, height, _ => false));
        c.ConfigureSinglePlayer(0);
        foreach (var n in c.Nations) n.IsHuman = false;
        a.CapitalStateMapIndex = Region(0, 0);
        b.CapitalStateMapIndex = Region(Cols - 1, 0);
        return (c, a, b);
    }

    static void Grudge(Campaign c, Nation a, Nation b)
    {
        var relation = CampaignDiplomacy.Get(c, a.Id, b.Id);
        for (var i = 0; i < 3; i++) OpinionSystem.Remember(c, relation, "denounced");
        c.Defines.DealGrudgePenalty = 1000f;
    }

    static int Owner(Campaign c, int region) => c.States.First(s => s.MapIndex == region).NationMapIndex;

    public static void Run()
    {
        FairSwap();
        EnclaveBought();
        RefusalBringsPressure();
    }

    /// <summary>India and Bangladesh, 2015: each side's enclave for the other's.</summary>
    static void FairSwap()
    {
        // B's enclave in A is worth more (more people) than A's in B: on land alone B would refuse.
        var (c, a, b) = World(new[] { (1, 1) }, new[] { (7, 2) });
        c.States.First(s => s.MapIndex == Region(7, 2)).Population = 600_000;
        Check(BorderShape.Grievance(c, a, b) > 0.6f && BorderShape.Grievance(c, b, a) > 0.6f,
            "each side's enclave in the other gives both a grievance (fixture precondition)");
        var swap = new Ultimatum.Deal(new Ultimatum.Side { Regions = new[] { Region(7, 2) } }, new Ultimatum.Side { Regions = new[] { Region(1, 1) } });
        Check(BorderShape.Relief(c, b, a, new[] { Region(7, 2) }, new[] { Region(1, 1) }) > 0.6f,
            "the swap would rid B of its whole grievance");
        var worth = c.Defines.DealBorderReliefWorth;
        c.Defines.DealBorderReliefWorth = 0f;
        Check(!Ultimatum.AssessDeal(c, a, b, swap).Acceptable, "blind to the border, B would refuse to give more people than it gets");
        c.Defines.DealBorderReliefWorth = worth;
        var assessment = Ultimatum.AssessDeal(c, a, b, swap);
        Check(assessment.Acceptable && assessment.ToValuation.Borders > 0f,
            $"counting the cleaner border, B accepts the fair swap from the other side (borders +{assessment.ToValuation.Borders:0.0})");

        var result = BorderDiplomacy.Approach(c, a, b, BorderShape.Grievance(c, a, b));
        Check(result == "swap" && Owner(c, Region(1, 1)) == a.MapIndex && Owner(c, Region(7, 2)) == b.MapIndex,
            $"the cheapest working means is the swap, and it is signed ({result})");
        Check(BorderShape.Grievance(c, a, b) == 0f && BorderShape.Grievance(c, b, a) == 0f, "both borders are clean afterwards");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "border_swap" && e.ActorId == a.Id && e.TargetId == b.Id) &&
              Loc.Get("chronicle.border_swap") != "chronicle.border_swap", "the exchange is news with a sentence of its own");
    }

    /// <summary>An enclave nobody can supply, bought for money.</summary>
    static void EnclaveBought()
    {
        var (c, a, b) = World(new[] { (1, 1) }, Array.Empty<(int, int)>(), enclavePopulation: 3_000_000);
        Check(BorderShape.Grievance(c, a, b) > 0.3f, "A resents B's enclave inside it (fixture precondition)");
        var before = (a.Treasury, b.Treasury);
        var result = BorderDiplomacy.Approach(c, a, b, BorderShape.Grievance(c, a, b));
        Check(result == "purchase" && Owner(c, Region(1, 1)) == a.MapIndex, $"with no pocket of its own to offer, A buys the enclave ({result})");
        Check(a.Treasury < before.Item1 && b.Treasury > before.Item2 && Math.Abs(a.Treasury + b.Treasury - before.Item1 - before.Item2) < 0.001f,
            $"the price moves from buyer to seller and no money is made or lost ({before.Item1 - a.Treasury:0.00})");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "border_purchase" && e.ActorId == a.Id), "the purchase is news");
        Check(BorderDiplomacy.Review(c, a) == false, "the border fixed, there is nothing left to review");
    }

    /// <summary>No money and no swap: the weaker side lodges a dispute, the stronger one demands the land.</summary>
    static void RefusalBringsPressure()
    {
        var enclaves = new[] { (1, 1), (3, 2) };
        var (c, a, b) = World(enclaves, Array.Empty<(int, int)>(), enclavePopulation: 3_000_000);
        // B bears a grudge no table can cover: it refuses every swap, sale and purchase.
        Grudge(c, a, b);
        var grievance = BorderShape.Grievance(c, a, b);
        Check(grievance >= c.Defines.BorderPressureGrievance, $"two enclaves are a grievance worth pressing ({grievance:0.00})");
        var tension = CampaignDiplomacy.Get(c, a.Id, b.Id).Tension;
        var result = BorderDiplomacy.Approach(c, a, b, grievance);
        Check(result == "dispute" && CampaignDiplomacy.Get(c, a.Id, b.Id).Tension > tension &&
              OpinionSystem.Has(CampaignDiplomacy.Peek(c, a.Id, b.Id), "refused_our_border_settlement") &&
              c.Chronicle.Entries.Any(e => e.Kind == "border_dispute" && e.ActorId == a.Id),
            $"refused everything, a country no stronger than the other lodges a dispute: tension up, grievance written down ({result})");

        var (c2, strong, weak) = World(enclaves, Array.Empty<(int, int)>(), enclavePopulation: 3_000_000);
        Grudge(c2, strong, weak); strong.Gdp = 5000f;
        var answer = BorderDiplomacy.Approach(c2, strong, weak, BorderShape.Grievance(c2, strong, weak));
        Check(answer.StartsWith("ultimatum", StringComparison.Ordinal) &&
              c2.Chronicle.Entries.Any(e => e.Kind is "ultimatum_accepted" or "ultimatum_refused" && e.ActorId == strong.Id),
            $"refused everything, a much stronger country demands the land by ultimatum ({answer})");
        Check(answer == "ultimatum_yielded"
                ? strong.MapIndex == Owner(c2, Region(1, 1)) || strong.MapIndex == Owner(c2, Region(3, 2))
                : Statecraft.CasusBelli(c2, strong, weak) == "ultimatum_refusal",
            "the ultimatum either moves the border or leaves a reason for war");
        Check(strong.DecisionAvailableDay.GetValueOrDefault("ultimatum/" + weak.Id) > c2.ElapsedDays,
            "the ultimatum shares the ordinary once-a-year limit");
    }
}
