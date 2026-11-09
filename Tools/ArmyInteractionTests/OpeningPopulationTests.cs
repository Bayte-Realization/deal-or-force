using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>A country's written population is the people it governs on the first morning (OpeningOccupation).</summary>
internal static class OpeningPopulationTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static void Run(Campaign c)
    {
        var ukraine = c.NationByTag("UKR");
        Check(Math.Abs(ukraine.Population - 25_000_000) < 250_000,
            $"Ukraine opens at its written 25 million on the ground it holds ({ukraine.Population:N0})");
        Check(ukraine.Population == c.StatesOf(ukraine).Sum(s => s.Population),
            "and that is the people of its regions, not a figure beside them");
        var russia = c.NationByTag("RUS");
        Check(russia.Population == c.StatesOf(russia).Sum(s => s.Population) && russia.Population > 146_000_000,
            "the regions Russia holds count for Russia with the people they were given");
        Check(OpeningOccupation.LastScaled.All(row => row.Scale <= c.Defines.OpeningOccupationMaximumScale),
            "no country is scaled past the bound");
        // The Shebaa patch at the foot of Mount Hermon is Israel's (its source cells are), and it
        // costs Lebanon a few thousand people of Southern Beqaa - not the third of the country one
        // border province once took with it.
        Check(OpeningOccupation.LastScaled.All(row => row.Tag != "LBN" || row.Scale < 1.01),
            "Lebanon loses under one per cent on the first morning (a border province once took a third of its people to Israel)");
        Check(OpeningOccupation.LastScaled.All(row => row.Tag != "YEM"),
            "Yemen's figure stays the whole country's: the Houthis are cut out after, by Factions");
    }
}
