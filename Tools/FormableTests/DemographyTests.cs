using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Who is born, who dies, who arrives, and what that does to a country over a lifetime.
///
/// Each check is one sentence the mechanic promises: an old country has fewer mothers and more
/// deaths; a country nobody is born into grows old and carries the dependency; a flood of migrants
/// arrives as itself and can take a region over; a minority nobody adds to fades; and the country's
/// own shape is aggregated for everything that reads it. The rates are aggregate game rates, so the
/// checks are about directions and thresholds rather than real demography - a test that demanded an
/// exact fertility rate would be a test of arithmetic nobody promised.
/// </summary>
static class DemographyTests
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS demography: " + label);
    }

    static void SetDay(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(campaign, (long)day * 24);

    /// <summary>One review: births, deaths and migration are what the household pass has just counted.</summary>
    static void Review(Campaign campaign, ScenarioState state, int day, long births, long deaths, long migration)
    {
        SetDay(campaign, day);
        state.LastHouseholdReviewDay = campaign.ElapsedDays;
        state.ReviewBirths = births;
        state.ReviewDeaths = deaths;
        state.ReviewNetMigration = migration;
        DemographicCycle.Tick(campaign);
    }

    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(20260928);
        var china = campaign.NationByTag("CHN");
        Check(china is not null && campaign.StatesOf(china).Length > 0, "the scenario holds a country to try this on");
        var state = campaign.StatesOf(china)[0];

        // Fertility follows the young.
        state.YoungShare = 0.30f;
        var youngFertility = DemographicCycle.FertilityFactor(state);
        state.YoungShare = 0.18f;
        var oldFertility = DemographicCycle.FertilityFactor(state);
        Check(oldFertility < youngFertility,
            "a country with fewer young people has fewer mothers: " + youngFertility.ToString("0.00") +
            " -> " + oldFertility.ToString("0.00"));

        // Mortality follows the old.
        state.OldShare = 0.09f;
        var youngCountryDeaths = DemographicCycle.MortalityFactor(state, campaign.Defines);
        state.OldShare = 0.28f;
        var oldCountryDeaths = DemographicCycle.MortalityFactor(state, campaign.Defines);
        Check(oldCountryDeaths > youngCountryDeaths,
            "and one with more old people buries more of them: " + youngCountryDeaths.ToString("0.00") +
            " -> " + oldCountryDeaths.ToString("0.00"));

        // Ageing: nobody born, nobody dying, and the country still grows old. A review is a week,
        // so forty years is two thousand of them - the check is about the direction and about the
        // size of the effect, not about a rate nobody promised.
        state.YoungShare = 0.10f;
        state.OldShare = 0.10f;
        var workingBefore = DemographicCycle.WorkingShare(state);
        for (var week = 0; week < 40 * 52; week++)
            Review(campaign, state, 500 + week * 7, 0, 0, 0);
        Check(state.OldShare > 0.2f, "a country nobody is born into grows old: " + state.OldShare.ToString("0.000"));
        Check(DemographicCycle.WorkingShare(state) < workingBefore,
            "and the share of people who can work falls: " + workingBefore.ToString("0.000") +
            " -> " + DemographicCycle.WorkingShare(state).ToString("0.000"));
        Check(state.YoungShare < 0.10f, "and it runs out of young people: " + state.YoungShare.ToString("0.000"));

        // A flood: the arrivals come as themselves, from the country they are coming from. Every
        // move is made by InternalMigration.Move - inside a country and, for BrainDrain, across a
        // border - so the people who arrive are the people who left.
        var sender = campaign.Nations.First(nation => nation.Id != china.Id && nation.Movement is null);
        var senderCulture = DemographyCatalog.PrimaryCultures(sender.Tag)[0];
        var origin = new ScenarioState { Id = "flood_origin", MapIndex = 65000, Population = 2_000_000_000 };
        origin.PopulationGroups.Add(new PopulationGroup
        {
            Culture = senderCulture, Religion = DemographyCatalog.FaithOf(senderCulture)[0].Religion,
            Profession = "labourers", Class = SocialClass.Lower, Sector = Sector.Industry, Size = origin.Population
        });
        state.Population = Math.Max(state.Population, state.PopulationGroups.Sum(group => group.Size));
        var before = state.PopulationGroups.Sum(group => group.Size) + origin.Population;
        // A flood is what the threshold says it is: at least DemographyFloodShare of the population in
        // one review. Above a fortieth is a crisis, not a trickle.
        var flood = Math.Max(1000, state.Population / 40);
        InternalMigration.Move(origin, state, flood, withinCountry: false);
        Review(campaign, state, 2000, 0, 0, flood);
        var arrived = state.PopulationGroups.FirstOrDefault(group =>
            string.Equals(group.Culture, senderCulture, StringComparison.OrdinalIgnoreCase));
        Check(arrived is not null && arrived.Size > 0,
            "a flood arrives as its own culture, not as somebody else's: " + senderCulture);
        Check(state.PopulationGroups.Sum(group => group.Size) + origin.PopulationGroups.Sum(group => group.Size) == before &&
              state.PopulationGroups.Sum(group => group.Size) == state.Population,
            "and every person who arrived left somewhere: a move conserves people");

        // Over decades the arrivals can stop being a minority of one region. A review is a week.
        for (var week = 0; week < 60 * 52 && arrived.Size < state.PopulationGroups.Max(group => group.Size); week++)
        {
            InternalMigration.Move(origin, state, flood, withinCountry: false);
            Review(campaign, state, 3000 + week * 7, 0, 0, flood);
        }
        Check(arrived.Size >= state.PopulationGroups.Max(group => group.Size),
            "a region flooded for decades can change whose region it is: " + arrived.Size);

        // Assimilation: once nobody keeps adding to them, the arrivals slowly adopt the culture of
        // the country around them - slowly: a year takes well under a tenth of them, where the
        // old weekly arithmetic took a quarter and left no minority anywhere after a decade.
        var size = arrived.Size;
        for (var week = 0; week < 52; week++)
            Review(campaign, state, 20000 + week * 7, 0, 0, 0);
        Check(arrived.Size < size, "a minority nobody adds to fades: " + size + " -> " + arrived.Size);
        Check(arrived.Size > size * 0.9, "but slowly, not a quarter of it a year: " + size + " -> " + arrived.Size);

        // And the country's own shape is aggregated for everything that reads it.
        DemographicCycle.Aggregate(campaign);
        Check(china.WorkingShare > 0f && china.WorkingShare < 1f,
            "the country's working share is aggregated: " + china.WorkingShare.ToString("0.000"));
        Check(china.OldShare > china.OldShareExcess,
            "and the country keeps the excess it is paying for: " + china.OldShareExcess.ToString("0.000"));
        Check(DemographicCycle.Explain(campaign, china).Contains("Births"),
            "and the explanation a tooltip shows names the births, deaths and migration");
    }
}