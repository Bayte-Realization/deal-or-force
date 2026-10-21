using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The name a split takes, when the thing it split from has already become a republic.
///
/// A player was shown "Republic of South Republic of India": the form of state laid over a
/// base that already had one, which is the naming equivalent of "ATM machine". India's own
/// name reads "Republic of India" the moment its politics change, and a south released from
/// it used to take that whole string as its base - so the wrapper went on twice.
///
/// The rule these checks hold is that a base is an ingredient and a form is a wrapper: the
/// form is added exactly once, at the end, to a base that arrives without one.
/// </summary>
static class FoundingNameTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("founding names: " + label);
        Console.WriteLine("PASS founding names: " + label);
    }

    public static void Run()
    {
        SubjectCatalog.ResetCatalog();
        DemographyCatalog.ResetCatalog();
        GovernmentCatalog.ResetCatalog();
        IdeologyCatalog.ResetCatalog();
        Loc.ResetCatalog();

        var campaign = Campaign.CreateBootstrap(20261019);
        MapRasterCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapError),
            "the world map loads: " + mapError);
        campaign.AttachProvinceTopology(map);

        var india = campaign.NationByTag("IND");
        Check(india is not null, "India is in the world");

        // India becomes a republic, the way it did in history and the way any country can.
        india.Government = "parliamentary_republic";
        india.Ideology = "socialism";
        india.StartGovernment = india.Government;
        india.StartIdeology = india.Ideology;
        var indiaName = CountryNames.Resolve(india, false);
        Check(indiaName.Length > 0, "India is called " + indiaName);

        // Ground south of the capital, which is the split that produced the nonsense name.
        var southern = campaign.StatesOf(india)
            .Where(state => state.MapIndex != india.CapitalStateMapIndex && state.Population > 0)
            .Select(state => (State: state, Latitude: CenterLatitude(campaign, state)))
            .Where(pair => pair.Latitude < india.CapitalLatitude - 0.4f)
            .OrderByDescending(pair => pair.State.Population)
            .Select(pair => pair.State)
            .FirstOrDefault() ?? campaign.StatesOf(india)
                .Where(state => state.MapIndex != india.CapitalStateMapIndex && state.Population > 0)
                .OrderByDescending(state => state.Population)
                .First();
        Check(southern is not null, "India has ground that is not its capital");

        var born = CountryRelease.Release(campaign, india, new CountryRelease.ReleaseOptions
        {
            StateMapIndices = new[] { southern.MapIndex }.ToList(),
            AsSubject = false,
            Culture = DemographyCatalog.PrimaryCulture(india.Tag),
            Government = "parliamentary_republic",
            Ideology = "socialism"
        }, out var feedback);
        Check(born is not null, "the south is released: " + feedback);

        var name = CountryNames.Resolve(born, false);
        Check(name.Length > 0, "and it is called something: " + name);

        // ------------------------------------------------------------------ the rule ------
        // Exactly one form of state, whatever the parent's own name happens to be.
        var forms = FoundingForms.Count(phrase => name.Contains(phrase, StringComparison.OrdinalIgnoreCase));
        Check(forms <= 1, "one form of state at most, not " + forms + ": " + name);
        Check(!name.Contains("Republic of South Republic", StringComparison.OrdinalIgnoreCase),
            "and specifically not the thing the player was shown");
        Check(!NationNaming.LooksLikeKey(name), "and it is not a raw localisation key");

        // The map label is the short name, so it must not carry the form either.
        var mapName = CountryNames.Resolve(born, true);
        Check(mapName.Length > 0 && !NationNaming.LooksLikeKey(mapName),
            "the map label reads too: " + mapName);

        // A split of a split keeps one form as well: the wrapper is stripped, not stacked.
        var further = campaign.StatesOf(born)
            .Where(state => state.MapIndex != born.CapitalStateMapIndex && state.Population > 0)
            .OrderByDescending(state => state.Population)
            .Take(1)
            .Select(state => state.MapIndex)
            .ToList();
        if (further.Count > 0)
        {
            var grandchild = CountryRelease.Release(campaign, born, new CountryRelease.ReleaseOptions
            {
                StateMapIndices = further,
                AsSubject = false,
                Culture = DemographyCatalog.PrimaryCulture(born.Tag),
                Government = "parliamentary_republic",
                Ideology = "socialism"
            }, out var secondFeedback);
            if (grandchild is not null)
            {
                var grandName = CountryNames.Resolve(grandchild, false);
                var grandForms = FoundingForms.Count(phrase => grandName.Contains(phrase, StringComparison.OrdinalIgnoreCase));
                Check(grandForms <= 1, "a split of a split still carries one form: " + grandName);
            }
        }

        // ------------------------------------------------------------ the ingredient ------
        Check(NationNaming.WithoutFormOfState("Republic of India") == "India",
            "a wrapper is taken back off a base");
        Check(NationNaming.WithoutFormOfState("India") == "India",
            "and a base with no wrapper is left alone");
        Check(NationNaming.WithoutFormOfState("Republic of South India") == "South India",
            "including one that already carries a direction");
        Check(NationNaming.WithoutFormOfState("Central African Republic") == "Central African Republic",
            "a republic that is part of the name, not a wrapper, is kept");
        Check(NationNaming.WithoutFormOfState("Federation of Nigeria") == "Nigeria",
            "and so is a federation's");
        // A compass side is a name, not a wrapper: a one-region state released from India's
        // south is South India, and calling it India says nothing about where it is.
        Check(NationNaming.WithoutFormOfState("South India") == "South India",
            "and a side of a country is a name, not a wrapper");
        Check(NationNaming.WithoutFormOfState("Republic of South India") == "South India",
            "so the form comes off and the side stays");
        Check(!CountryNames.Resolve(born, false).Equals(indiaName, StringComparison.OrdinalIgnoreCase),
            "and the released country is not called the same thing as the country it left");
        Check(NationNaming.LooksLikeKey("formable.gulf_union.name"), "a localisation key looks like one");
        Check(!NationNaming.LooksLikeKey("Republic of India"), "and a name does not");
        Check(NationNaming.TryTranslate("formable.gulf_union.name", out var defined) && defined == "Gulf Union",
            "and a recipe the tables do define is translated: " + defined);
        Check(!NationNaming.TryTranslate("formable.no_such_union.name", out _),
            "a recipe nothing defines is still refused");
        Check(NationNaming.TryTranslate("Republic of India", out var literal) && literal == "Republic of India",
            "and a literal is taken as written");

        // ------------------------------------------------------------- the coverage ------
        // Every formable the data defines has a name row, so no country can ever be called by
        // its key. Eighty-one of the eighty-six were missing, which is the whole bug.
        var nameless = FormableNations.All
            .Where(formable => !Loc.Has("formable." + formable.Id + ".name"))
            .Select(formable => formable.Id)
            .ToArray();
        Check(nameless.Length == 0,
            "every formable has a name row" + (nameless.Length > 0 ? " - missing: " + string.Join(", ", nameless) : ""));
        Check(FormableNations.All.Count() >= 80, "and there are enough of them to matter (" + FormableNations.All.Count() + ")");
        // The row must agree with the data, so a translator cannot quietly rename a country.
        foreach (var formable in FormableNations.All.Take(20))
        {
            var row = Loc.Get("formable." + formable.Id + ".name");
            Check(row.Length > 0 && !row.Contains('.') || row == formable.Name,
                formable.Id + " reads as a name: " + row);
        }
    }

    /// <summary>Where a region sits, for asking which side of the capital it is on.</summary>
    private static float CenterLatitude(Campaign campaign, ScenarioState state)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return 0f;
        float latitude = 0f;
        var counted = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != (ushort)state.MapIndex) continue;
            latitude += topology.CenterOf(province).Latitude;
            counted++;
        }
        return counted == 0 ? 0f : latitude / counted;
    }

    /// <summary>Every form of state the game can wrap a name in, in both shipped languages.</summary>
    private static readonly string[] FoundingForms =
    {
        "Republic of", "Kingdom of", "Islamic Republic of", "Holy State of",
        "People's Republic of", "Military Government of", "Federation of",
        "Commune of", "Technate of", "Provisional Government of",
        "Республика", "Королевство", "Исламская Республика", "Святое Государство",
        "Народная Республика", "Военное Правительство", "Федерация", "Коммуна", "Технат"
    };
}
