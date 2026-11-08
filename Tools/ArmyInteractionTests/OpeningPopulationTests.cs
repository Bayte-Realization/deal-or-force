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
        Check(OpeningOccupation.LastScaled.All(row => row.Tag != "LBN"),
            "Lebanon loses no region on the first morning (a border province once took a third of its people to Israel)");
        Check(OpeningOccupation.LastScaled.All(row => row.Tag != "YEM"),
            "Yemen's figure stays the whole country's: the Houthis are cut out after, by Factions");
    }
}
