using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The marks a division card is drawn from.
///
/// A card says whose it is with a flag and a national rim, and what it is with a branch colour
/// and a NATO symbol. The flag and the rim come from the country, so nothing can be missing;
/// the branch comes from `common/units`, and a template that forgets its `counter_symbol` would
/// quietly be drawn as line infantry - a gun battery that looks like a rifle company, which is
/// exactly the mistake the card exists to prevent.
/// </summary>
static class CounterCardTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("counter cards: " + label);
        Console.WriteLine("PASS counter cards: " + label);
    }

    public static void Run()
    {
        var types = UnitTypeCatalog.All.ToList();
        Check(types.Count >= 7, "the formation catalogue is loaded (" + types.Count + " templates)");

        var unnamed = types.Where(type => string.IsNullOrWhiteSpace(type.CounterSymbol))
            .Select(type => type.Id).ToList();
        Check(unnamed.Count == 0, "every formation says which branch mark it wears" +
            (unnamed.Count > 0 ? ": " + string.Join(", ", unnamed) : ""));

        var symbols = types.Select(type => type.CounterSymbol)
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var expected in new[] { "infantry", "motorized", "mechanized", "armor", "artillery" })
            Check(symbols.Contains(expected, StringComparer.OrdinalIgnoreCase),
                "the map can draw " + expected + " (symbols: " + string.Join(", ", symbols) + ")");

        // The guns must not fall through to the infantry cross: that was the old behaviour, and
        // it is the one thing a player cannot be expected to notice from a symbol that is merely
        // wrong rather than absent.
        var artillery = types.Where(type => type.CounterSymbol == "artillery").Select(type => type.Id).ToList();
        Check(artillery.Count > 0, "at least one formation is drawn as artillery: " + string.Join(", ", artillery));
    }
}
