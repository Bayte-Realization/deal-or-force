using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The dynasties the world actually generates.
///
/// A name pool that is never read is a file, not a feature. These checks ask the same
/// question the game asks - what is this country's next ruler called - across every
/// continent, and they insist that the answer comes from that country's own people: a
/// Japanese premier with a Slavic surname, or a Nigerian general called Alexander Vance,
/// is the bug this is written against.
/// </summary>
static class RulerNameTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("ruler names: " + label);
        Console.WriteLine("PASS ruler names: " + label);
    }

    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(20261019);
        var cases = new (string Tag, string[] Pools)[]
        {
            ("RUS", new[] { "russian_east_slavic" }), ("UKR", new[] { "ukrainian" }),
            ("POL", new[] { "west_slavic" }), ("SRB", new[] { "south_slavic" }),
            ("USA", new[] { "anglo" }), ("FRA", new[] { "french" }), ("DEU", new[] { "germanic" }),
            ("SWE", new[] { "nordic" }), ("ITA", new[] { "italian" }), ("ESP", new[] { "hispanic" }),
            ("PRT", new[] { "lusophone" }), ("GRC", new[] { "greek", "balkan_hellenic" }),
            ("LTU", new[] { "baltic" }), ("GEO", new[] { "georgian", "caucasian" }),
            ("TUR", new[] { "turkic" }), ("IRN", new[] { "persian", "persian_kurdish" }),
            ("ISR", new[] { "hebrew" }), ("YEM", new[] { "arab_yemeni", "arabic" }),
            ("CHN", new[] { "chinese", "han" }), ("JPN", new[] { "japanese" }), ("KOR", new[] { "korean" }),
            ("MNG", new[] { "mongolic" }), ("VNM", new[] { "vietnamese", "southeast_asian" }),
            ("IND", new[] { "south_asian" }),
            ("NGA", new[] { "yoruba", "igbo", "hausa", "west_african" }),
            ("ETH", new[] { "amhara", "oromo", "east_horn_african" }),
            ("ZAF", new[] { "zulu", "xhosa", "shona", "swahili", "central_southern_african" })
        };
        foreach (var (tag, pools) in cases)
        {
            var nation = campaign.NationByTag(tag);
            Check(nation is not null, tag + " is in the scenario");
            if (nation is null) continue;
            var chosen = Rulers.PoolFor(nation);
            Check(pools.Contains(chosen, StringComparer.OrdinalIgnoreCase),
                tag + " draws on its own people (got " + chosen + ")");
            var names = Enumerable.Range(0, 8)
                .Select(generation => Rulers.GenerateName(nation, generation, isState: true))
                .Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
            Check(names.Count == 8, tag + " generates eight rulers");
            Check(names.Distinct(StringComparer.Ordinal).Count() >= 5,
                tag + " generates different people: " + string.Join(", ", names.Take(4)));
            Check(names.All(name => name != "Alexander Vance"), tag + " never falls back to the default");
        }

        // Almost every country in the world should draw on a people, not the cosmopolitan fallback.
        var defaulted = campaign.Nations
            .Where(nation => nation.Population > 0 && Rulers.PoolFor(nation) == "default")
            .Select(nation => nation.Tag).OrderBy(tag => tag).ToList();
        Console.WriteLine("DEFAULT_POOLS " + string.Join(",", defaulted));
        Check(defaulted.Count <= 20, "almost every country draws on a people, not the default (" +
            defaulted.Count + " of " + campaign.Nations.Count(n => n.Population > 0) + ")");

        // The same country and the same generation must give the same person on every machine.
        var japan = campaign.NationByTag("JPN");
        Check(Rulers.GenerateName(japan, 3, true) == Rulers.GenerateName(japan, 3, true),
            "a ruler's name is deterministic for a seed and a generation");
        Check(Rulers.GenerateName(japan, 3, true) != Rulers.GenerateName(japan, 4, true),
            "and the next generation is somebody else");

        // The pool of people a government can actually recruit from.
        var candidates = Rulers.CandidatePool(campaign, japan, 8);
        Check(candidates.Count == 8, "Japan offers eight politicians to choose from");
        Check(candidates.Select(candidate => candidate.Name).Distinct(StringComparer.Ordinal).Count() >= 7,
            "the candidates are different people: " + string.Join(", ", candidates.Take(4).Select(c => c.Name)));
        Check(candidates.All(candidate => candidate.Age >= 26 && candidate.Age <= 85),
            "the candidates are of a plausible age");
        Check(candidates.All(candidate => !string.IsNullOrEmpty(candidate.TraitId) &&
                                          !string.IsNullOrEmpty(candidate.TraitName)),
            "every candidate has a character");
        Check(Rulers.CandidatePool(campaign, japan, 8)
                .Select(candidate => candidate.Name).SequenceEqual(candidates.Select(candidate => candidate.Name)),
            "the same country offers the same candidates until the pool is refreshed");

        // Recruiting one as the national leader is a real substitution, not a rename.
        var america = campaign.NationByTag("USA");
        america.Treasury = 1000f;
        var beforeLeader = Rulers.HeadOfState(campaign, america).Name;
        var american = Rulers.CandidatePool(campaign, america, 4);
        new RecruitPoliticianCommand { IssuerId = america.Id, CandidateSlot = 0, AsNationalLeader = true }
            .Apply(campaign);
        var afterLeader = Rulers.HeadOfState(campaign, america);
        Check(afterLeader.Name == american[0].Name && afterLeader.Name != beforeLeader,
            "a recruited politician becomes the head of state: " + beforeLeader + " -> " + afterLeader.Name);
        Check(afterLeader.TraitId == american[0].TraitId,
            "and brings their own character into the office");

        // A ruler's character reaches the AI, not only the dossier.
        var warlike = new Nation
        {
            Tag = "TST", Name = "Testland", Government = "presidential_republic",
            Ideology = "nationalism", HeadOfStateName = "A. Test", HeadOfStateTrait = "hawk"
        };
        var patient = new Nation
        {
            Tag = "TS2", Name = "Patiencia", Government = "presidential_republic",
            Ideology = "nationalism", HeadOfStateName = "B. Test", HeadOfStateTrait = "diplomat"
        };
        var hawk = Rulers.TemperamentShift(campaign, warlike);
        var dove = Rulers.TemperamentShift(campaign, patient);
        Check(hawk.Opportunism > dove.Opportunism && dove.Cunning > hawk.Cunning,
            "a ruler's character reaches the AI's judgement");
    }
}
