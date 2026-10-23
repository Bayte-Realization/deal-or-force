using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Find the names that read as nonsense, and say which route produced them.
///
/// A player was shown a country called "Republic of South Republic of India" - a form of
/// state laid over a base that already had one, which is the naming equivalent of saying
/// "ATM machine". Eighty-one of the eighty-six formables have no name row, and a name that
/// resolves to nothing used to arrive as its own key, so this looks for both: a name with a
/// form phrase in it twice, and a name that is still a localisation key.
/// </summary>
static class TautologyNameProbe
{
    // Every form of state the game can give a name, in English and in Russian, taken from
    // name.found.* rather than written out here so a mod that adds one is covered.
    private static readonly string[] FormPhrases =
    {
        "Republic of", "Kingdom of", "Islamic Republic of", "Holy State of",
        "People's Republic of", "Military Government of", "Federation of",
        "Commune of", "Technate of", "Provisional Government of",
        "Республика", "Королевство", "Исламская Республика", "Святое Государство",
        "Народная Республика", "Военное Правительство", "Федерация", "Коммуна", "Технат"
    };

    public static void Run()
    {
        var seeds = new[] { 20261019, 1, 7, 42, 101, 777, 2024, 31337 };
        foreach (var seed in seeds)
        {
            try { RunSeed(seed); }
            catch (Exception ex) { Console.WriteLine($"TAUT seed {seed} threw {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private static void RunSeed(int seed)
    {
        var campaign = Campaign.CreateBootstrap(seed);
        MapRasterCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapError))
        { Console.WriteLine("TAUT map failed: " + mapError); return; }
        campaign.AttachProvinceTopology(map);

        var reported = new HashSet<string>();
        for (var day = 0; day < 900; day++)
        {
            campaign.TickDay();
            if (day % 25 != 24) continue;
            foreach (var nation in campaign.Nations)
            {
                var name = CountryNames.Resolve(nation, false);
                var why = Fault(campaign, name, nation);
                if (why.Length == 0) continue;
                var line = $"seed={seed} day={campaign.ElapsedDays} {nation.Tag} = \"{name}\" ({why})";
                if (!reported.Add(line)) continue;
                Console.WriteLine("TAUT " + line);
                Describe(campaign, nation);
                if (reported.Count > 12) { Report(campaign); return; }
            }
            foreach (var war in campaign.Wars.Where(w => !w.Ended))
            {
                var name = WarNames.Of(campaign, war);
                var why = WarFault(war, name);
                if (why.Length == 0) continue;
                var line = $"seed={seed} day={campaign.ElapsedDays} war {war.Id} = \"{name}\" ({why})";
                if (!reported.Add(line)) continue;
                Console.WriteLine("TAUT " + line);
                var attacker = campaign.NationById(war.AttackerId);
                var defender = campaign.NationById(war.DefenderId);
                Console.WriteLine($"TAUT   civil={war.CivilConflict} stored={war.Name} " +
                    $"attacker={attacker?.Tag}/{attacker?.DisplayName} secededFrom={attacker?.SecededFromId} " +
                    $"defender={defender?.Tag}/{defender?.DisplayName} secededFrom={defender?.SecededFromId}");
                if (reported.Count > 12) { Report(campaign); return; }
            }

        }
        Console.WriteLine($"TAUT seed={seed} clean");
    }

    /// <summary>Why this name reads as nonsense, or nothing when it does not.</summary>
    private static string Fault(Campaign campaign, string name, Nation nation)
    {
        if (string.IsNullOrWhiteSpace(name)) return "empty";
        // A name that is still a key: the leak that put "formable.gulf_union.name" on a map.
        if (NationNaming.LooksLikeKey(name)) return "raw localisation key";
        // Names the game composed are a base plus a form; names the data wrote are names.
        // "United Republic of Tanzania" is not a form applied twice, it is Tanzania.
        var composed = !string.IsNullOrEmpty(nation.FoundNameKind) ||
            !string.IsNullOrEmpty(nation.FormedNationId) ||
            !string.IsNullOrWhiteSpace(nation.CustomName);
        if (!composed) return "";
        // A country named only for a side of another country: "Republic of East".
        if (NationNaming.CompassDirection(NationNaming.BaseOf(campaign, nation)) is { Length: > 0 })
            return "name is only a side of a country";
        // The longest wrapper the name carries; anything left with another wrapper in it is
        // the same form of state twice.
        var longest = FormPhrases.OrderByDescending(phrase => phrase.Length)
            .FirstOrDefault(phrase => name.Contains(phrase, StringComparison.OrdinalIgnoreCase));
        if (longest is null) return "";
        var rest = name.Remove(name.IndexOf(longest, StringComparison.OrdinalIgnoreCase), longest.Length);
        return FormPhrases.Any(phrase => rest.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            ? "form of state twice" : "";
    }

    /// <summary>Why a war's shown name reads as nonsense, or nothing.</summary>
    private static string WarFault(StrategicWar war, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "empty war name";
        if (NationNaming.LooksLikeKey(name)) return "raw localisation key";
        if (war.CivilConflict && name.Contains('\u2013') && name.EndsWith(" War", StringComparison.Ordinal))
            return "civil war named as two countries";
        return "";
    }

    /// <summary>The recipe behind the name, so the route is named rather than guessed at.</summary>
    private static void Describe(Campaign campaign, Nation nation)
    {
        Console.WriteLine($"TAUT   recipe kind={nation.FoundNameKind} id={nation.FoundNameId} " +
            $"form={nation.FoundNameForm} base={nation.FoundNameBase} " +
            $"custom={nation.CustomName} formed={nation.FormedNationId} " +
            $"gov={nation.Government} ideology={nation.Ideology} raw={nation.Name}");
        var parent = campaign.NationById(nation.ReleasedFromId);
        if (parent is not null)
            Console.WriteLine($"TAUT   parent={parent.Tag} parentKind={parent.FoundNameKind} " +
                $"parentId={parent.FoundNameId} parentFormed={parent.FormedNationId} parentName={parent.Name}");
    }

    private static void Report(Campaign campaign)
    {
        Console.WriteLine("TAUT --- every faulty name in the world at this moment ---");
        foreach (var nation in campaign.Nations)
        {
            var name = CountryNames.Resolve(nation, false);
            if (Fault(campaign, name, nation).Length > 0) Console.WriteLine($"TAUT   {nation.Tag} = \"{name}\"");
        }
    }
}
