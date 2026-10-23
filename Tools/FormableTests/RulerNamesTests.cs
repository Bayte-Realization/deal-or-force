using System;
using System.Linq;
using DealOrForce.Simulation;

static class RulerNamesTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS rulers: " + label); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(805);
        Rulers.Found(c);
        var n = c.NationByTag("ROU");
        var s = c.NationByTag("SDN");

        // Every country with people has a leadership: the two chairs, or one person in both.
        var withRuler = c.Nations.Count(country => country.Population > 0 && !string.IsNullOrEmpty(Rulers.HeadOfState(c, country).Name));
        var withPeople = c.Nations.Count(country => country.Population > 0);
        Check(withRuler >= withPeople * 9 / 10,
            $"almost every country has leadership after Found ({withRuler}/{withPeople})");

        Check(!string.IsNullOrEmpty(Rulers.HeadOfState(c, n).Name) || !string.IsNullOrEmpty(Rulers.HeadOfGovernment(c, n).Name), "Romania has a leader");
        Check(!string.IsNullOrEmpty(Rulers.HeadOfState(c, n).Name), "the leader has a name");

        // Diversity: the same pool does not hand two major countries identical leaders.
        var names = c.Nations.Where(country => country.Population > 10_000_000)
            .Select(country => Rulers.HeadOfState(c, country).Name ?? "")
            .Where(name => name.Length > 0).ToList();
        Check(names.Distinct().Count() >= names.Count * 9 / 10,
            $"leader names are drawn wide, not from a hat of ten ({names.Distinct().Count()}/{names.Count})");

        // Succession: kill the head of state by ageing them past their end, then tick.
        var before = Rulers.HeadOfState(c, n).Name;
        n.HeadOfStateBirthYear = c.Date.Year - 100;
        Rulers.Tick(c);
        var after = Rulers.HeadOfState(c, n);
        Check(!string.IsNullOrEmpty(after.Name) && after.Name != before,
            "a ruler past their end is replaced by a successor");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "ruler_died"),
            "the death is public chronicle");

        // Determinism: the same seed and day roll the same successor.
        var successor = after.Name;
        var fresh = Campaign.CreateBootstrap(805);
        Rulers.Found(fresh);
        var freshRou = fresh.NationByTag("ROU");
        freshRou.HeadOfStateBirthYear = fresh.Date.Year - 100;
        Rulers.Tick(fresh);
        Check(Rulers.HeadOfState(fresh, freshRou).Name == successor,
            "succession is deterministic in the campaign seed");
    }
}
