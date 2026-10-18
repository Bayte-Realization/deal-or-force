using System;
using DealOrForce.Simulation;

/// <summary>
/// What a country is called once its politics have changed.
///
/// The machinery (CountryNames) asks three questions in order: the exact name for what the country
/// has become, the general form-of-state template, and finally the name it had. Each of the three
/// is worth one check, because each can be the one that answers - and a name that quietly stops
/// answering is a country on the map called something its government no longer is.
/// </summary>
static class CountryNameTests
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS names: " + label);
    }

    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(20260928);
        // The template form only answers for a country that has changed: this is what the campaign
        // does on its first day, and a test that forgets it is a test of nothing.
        CountryNames.Remember(campaign);
        var previous = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var china = campaign.NationByTag("CHN");
            Check(china is not null, "the scenario holds the country this check is about");
            var before = CountryNames.Resolve(china, false);
            china.Ideology = "communism";
            var after = CountryNames.Resolve(china, false);
            Check(after == "Китайская Народная Республика",
                "a communist China is called what it became, not what it was: " + after);
            Check(after != before, "and it is not the name it had: " + before + " -> " + after);

            var japan = campaign.NationByTag("JPN");
            Check(japan is not null, "and the monarchy case is in the scenario too");
            japan.Government = "absolute_monarchy";
            Check(CountryNames.Resolve(japan, false) == "Императорская Япония",
                "a Japan under an absolute monarchy is called what it became");

            // Where nothing was written, the general template answers, and it must still answer:
            // the point of the specific rows is the exceptions, not the rule.
            var peru = campaign.NationByTag("PER");
            Check(peru is not null, "a country with no name row of its own is in the scenario");
            var peruBefore = CountryNames.Resolve(peru, false);
            peru.Ideology = "communism";
            var peruAfter = CountryNames.Resolve(peru, false);
            Check(peruAfter != peruBefore, "a country with no row is still renamed by the template: " +
                peruBefore + " -> " + peruAfter);

            // A movement that takes power changes what it is called, and that is the one moment its
            // name moves at all: a campaign for independence is not a government until it is one.
            var movement = new Nation { Tag = "SCV", Government = "provisional_government", Ideology = "social_democracy" };
            Check(CountryNames.Resolve(movement, false) == "Временное правительство Шотландии",
                "a movement that becomes a government is called what it became: " +
                CountryNames.Resolve(movement, false));

            // A monarchy that becomes a republic, and a restored country whose politics change.
            var spain = campaign.NationByTag("ESP");
            spain.Government = "parliamentary_republic";
            Check(CountryNames.Resolve(spain, false) == "Испанская Республика",
                "a Spain that becomes a republic takes its republican name: " + CountryNames.Resolve(spain, false));

            var sweden = campaign.NationByTag("SWE");
            sweden.Government = "parliamentary_republic";
            Check(CountryNames.Resolve(sweden, false) == "Республика Швеция",
                "a monarchy without a custom row uses the republican template when it abolishes the crown: " +
                CountryNames.Resolve(sweden, false));

            var restored = new Nation
            {
                Tag = "SRB",
                FormedNationId = "restore_mne",
                StartGovernment = "parliamentary_republic",
                StartIdeology = "liberalism",
                Government = "constitutional_monarchy",
                Ideology = "conservatism"
            };
            Check(CountryNames.Resolve(restored, false) == "Королевство Черногория",
                "a restored country still takes the form of state its new politics give it: " +
                CountryNames.Resolve(restored, false));
        }
        finally
        {
            Loc.Language = previous;
        }
    }
}
