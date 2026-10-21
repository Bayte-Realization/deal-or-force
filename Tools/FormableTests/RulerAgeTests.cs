using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Ages of the people who govern.
///
/// There are two populations here and they are checked differently. The 2024 bookmark names a
/// real head of state and head of government for every country it covers, with the age that
/// person actually was on 1 January 2024, and those ages are facts: an anchored handful is
/// asserted by name, and the whole table is kept inside the range a living politician occupies.
/// Everybody else - a successor, a minister, a country a mod adds, a state a treaty releases -
/// is generated, and the generated draw is checked for the thing that was wrong before: a cabinet
/// of people born in the same five years. A distribution that never produces anyone under forty
/// or over seventy is not a description of the world, it is a constant with a hash in front of it.
/// </summary>
static class RulerAgeTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("ruler ages: " + label);
        Console.WriteLine("PASS ruler ages: " + label);
    }

    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(20261020);

        // ---- The authored 2024 table -------------------------------------------------
        var path = GameDataLoader.FindDataFile("history", "countries", "2024", "25_rulers.json");
        Check(path is not null, "the 2024 ruler table is where the loader looks for it");
        if (path is null) return;
        var clean = string.Join("\n", File.ReadAllText(path)
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        using var document = JsonDocument.Parse(clean);
        var entries = document.RootElement.EnumerateObject()
            .ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        Check(entries.Count >= 240,
            "the table covers the world, not a handful of great powers (" + entries.Count + " countries)");

        var nameless = new List<string>();
        var implausible = new List<string>();
        foreach (var (tag, value) in entries)
        {
            var head = value.TryGetProperty("head_of_state", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString() ?? "" : "";
            if (head.Length == 0) nameless.Add(tag);
            if (value.TryGetProperty("state_age", out var age) && age.TryGetInt32(out var years) &&
                (years < 18 || years > 95)) implausible.Add(tag + " head " + years);
            if (value.TryGetProperty("gov_age", out var gov) && gov.TryGetInt32(out var govYears) &&
                (govYears < 18 || govYears > 95)) implausible.Add(tag + " government " + govYears);
        }
        Check(nameless.Count == 0, "every country in the table has a named head of state" +
            (nameless.Count > 0 ? ": " + string.Join(", ", nameless) : ""));
        Check(implausible.Count == 0, "every authored age is one a living politician could be" +
            (implausible.Count > 0 ? ": " + string.Join(", ", implausible) : ""));


        // Anchors that are not in dispute as of 1 January 2024. If an edit shifts the table by a
        // year or attaches the wrong person to the wrong country, one of these fails.
        var anchors = new (string Tag, string Name, string Office, int Age)[]
        {
            ("USA", "Joe Biden", "head_of_state", 81), ("RUS", "Vladimir Putin", "head_of_state", 71),
            ("CHN", "Xi Jinping", "head_of_state", 70), ("IND", "Narendra Modi", "head_of_government", 73),
            ("BRA", "Luiz Inacio Lula da Silva", "head_of_state", 78), ("GBR", "Rishi Sunak", "head_of_government", 43),
            ("FRA", "Emmanuel Macron", "head_of_state", 46), ("ZAF", "Cyril Ramaphosa", "head_of_state", 71),
            ("JPN", "Fumio Kishida", "head_of_government", 66), ("DEU", "Olaf Scholz", "head_of_government", 65),
            ("NGA", "Bola Tinubu", "head_of_state", 71), ("IDN", "Joko Widodo", "head_of_state", 62),
            ("MEX", "Andres Manuel Lopez Obrador", "head_of_state", 70)
        };
        foreach (var (tag, name, office, age) in anchors)
        {
            Check(entries.TryGetValue(tag, out var value), tag + " is in the ruler table");
            if (!entries.TryGetValue(tag, out value)) continue;
            var person = value.TryGetProperty(office, out var property) ? property.GetString() : null;
            var authored = value.TryGetProperty(office == "head_of_state" ? "state_age" : "gov_age", out var a) &&
                           a.TryGetInt32(out var years) ? years : -1;
            Check(person == name && authored == age,
                tag + " keeps " + name + " at " + age + " (found " + person + " at " + authored + ")");
        }

        // ---- Everybody the table does not name ---------------------------------------
        var authoredTags = entries.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var populated = campaign.Nations.Where(nation => nation.Population > 0).ToList();
        var unruled = populated.Where(nation => !authoredTags.Contains(nation.Tag))
            .Select(nation => nation.Tag).OrderBy(tag => tag).ToList();
        Console.WriteLine("UNRULED " + string.Join(",", unruled));
        // No civilian government to name: uninhabited rocks and reefs, an icefield, a leased
        // cosmodrome and the sovereign base areas. The generator gives each of them an official.
        var ungoverned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ATF", "CSI", "ESB", "IOA", "IOT", "KAB", "KAS", "NFK", "PCN", "PGA", "SGS",
            "SHN", "UMI", "USG", "WSB"
        };
        var unexpected = unruled.Where(tag => !ungoverned.Contains(tag)).ToList();
        Check(unexpected.Count == 0, "every country with a government has a named leader (" +
            (unexpected.Count > 0 ? string.Join(", ", unexpected) : "all covered") + ")");

        // A successor. Two generations of forty countries, which is eighty people.
        var ages = new List<int>();
        foreach (var nation in populated.OrderBy(nation => nation.Tag, StringComparer.Ordinal).Take(40))
            for (var generation = 0; generation < 2; generation++)
            {
                Rulers.ReplaceHeadOfState(campaign, nation, reason: "");
                ages.Add(campaign.Date.Year - nation.HeadOfStateBirthYear);
            }
        Check(ages.All(age => age >= 18 && age <= 92),
            "a generated ruler is an adult and not a legend (" + ages.Min() + ".." + ages.Max() + ")");
        Check(ages.Max() - ages.Min() >= 30,
            "and they span a working lifetime rather than a decade (" + ages.Min() + ".." + ages.Max() + ")");
        var mean = ages.Average();
        var deviation = Math.Sqrt(ages.Average(age => (age - mean) * (age - mean)));
        Check(deviation >= 7.0, "and are spread around it, not clustered (sd " + deviation.ToString("0.0") + ")");
        Check(ages.Distinct().Count() >= 25,
            "their ages are actually different from one another (" + ages.Distinct().Count() + " of " + ages.Count + ")");
        Check(mean >= 48 && mean <= 72,
            "and sit where national leaders sit, about sixty (mean " + mean.ToString("0.0") + ")");

        // A cabinet. Portfolios are what a government fills when nobody has written the names.
        var cabinet = populated.Take(40)
            .SelectMany(nation => Rulers.AllPortfolios.Select(portfolio => Rulers.GeneratedMinisterAge(nation, portfolio.Id)))
            .ToList();
        Check(cabinet.All(age => age >= 26 && age <= 92),
            "a generated minister is old enough to have a career and not older than the country (" +
            cabinet.Min() + ".." + cabinet.Max() + ")");
        Check(cabinet.Max() - cabinet.Min() >= 20,
            "and a cabinet is not one age repeated (" + cabinet.Min() + ".." + cabinet.Max() + ")");

        // The draw must not depend on the machine or the run: same country, same generation, same age.
        var japan = campaign.NationByTag("JPN");
        Check(japan is not null, "Japan is in the scenario");
        if (japan is not null)
        {
            Rulers.ReplaceHeadOfState(campaign, japan, reason: "");
            var first = japan.HeadOfStateBirthYear;
            japan.RulerGeneration--;
            Rulers.ReplaceHeadOfState(campaign, japan, reason: "");
            Check(japan.HeadOfStateBirthYear == first,
                "a generated age is deterministic for a country and a generation");
        }
    }
}
