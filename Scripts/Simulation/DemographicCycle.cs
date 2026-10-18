using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Where a region's age structure starts when the scenario says nothing: young, working, old.</summary>
    public float DemographyYoungShareDefault { get; set; } = 0.30f;
    public float DemographyOldShareDefault { get; set; } = 0.09f;

    /// <summary>Share of the young who become workers in a year, and of the workers who grow old.</summary>
    public float DemographyMaturationAnnual { get; set; } = 0.055f;
    public float DemographyAgeingAnnual { get; set; } = 0.018f;

    /// <summary>The young share a country is expected to have; births scale with how it compares.</summary>
    public float DemographyReferenceYoungShare { get; set; } = 0.28f;

    /// <summary>How hard a young population is on births: 1 means the effect is proportional to the shares.</summary>
    public float DemographyFertilityYoungWeight { get; set; } = 1.0f;

    /// <summary>The old share a country is expected to have; deaths past it scale with the excess.</summary>
    public float DemographyReferenceOldShare { get; set; } = 0.10f;

    /// <summary>How hard an old population is on deaths: 1 means the shares speak for themselves.</summary>
    public float DemographyMortalityOldWeight { get; set; } = 1.4f;

    /// <summary>People per thousands per year a country loses to old age at double the reference old share.</summary>
    public float DemographySenescenceDeathAnnual { get; set; } = 0.22f;

    /// <summary>Share of a working population that retires in a year at double the reference old share.</summary>
    public float DemographySenescenceOutputAnnual { get; set; } = 0.10f;

    /// <summary>Net migration as a share of the population that counts as a flood rather than a trickle.</summary>
    public float DemographyFloodShare { get; set; } = 0.012f;

    /// <summary>Share of a minority who adopt the country's own culture in a year.</summary>
    public float DemographyAssimilationAnnual { get; set; } = 0.006f;

    /// <summary>Where new fields start for a save written before this existed: nobody is old, nobody is young.</summary>
    public bool DemographyAgeStructureEnabled { get; set; } = true;
}

public sealed partial class ScenarioState
{
    /// <summary>How much of this region is too young to work. Read and written by DemographicCycle.</summary>
    public float YoungShare = 0.30f;

    /// <summary>How much of this region is past working age: the dependency a country carries.</summary>
    public float OldShare = 0.09f;

    /// <summary>The household review this region's age structure was last moved on.</summary>
    public int LastDemographyDay = -1;
}

public sealed partial class Nation
{
    /// <summary>Share of the country too young to work, aggregated from its regions.</summary>
    public float YoungShare = 0.30f;

    /// <summary>Share of the country past working age: the dependency it carries.</summary>
    public float OldShare = 0.09f;

    /// <summary>Share of the country of working age, which is what is left.</summary>
    public float WorkingShare = 0.62f;

    /// <summary>How far past the reference old share the country is, kept so a modifier source can read it.</summary>
    public float OldShareExcess;

    /// <summary>Births, deaths and net migration over the country in its latest review.</summary>
    public long ReviewBirths, ReviewDeaths, ReviewNetMigration;
}

/// <summary>
/// The shape of a population: how many are too young to work, how many are past it, and what that
/// does to the country that carries them.
///
/// The game already counted births and deaths (HouseholdEconomy) and already moved people across
/// borders (BrainDrain), but it counted them for a population with no age at all: the share of
/// people of working age was the constant 0.62 in three files, so a country could lose its young
/// for fifty years and be exactly as strong on the fiftieth. This is the missing dimension. Regions
/// carry three bins - young, working, old - moved by the births and deaths that already happen and
/// by two rates of ageing, and then feed back:
///
///   * births scale with how young a country is (a country whose young share has halved has fewer
///     mothers, and that is a spiral rather than a dip),
///   * deaths scale with how old it is, over and above what medicine can do,
///   * output and stability carry the dependency: past the reference old share a country pays for
///     the people it keeps, and it pays in the numbers a player already watches,
///   * and migration is what decides whether any of this can be outrun: an old, rich, stable
///     country pulls people in, a young, unstable one pushes them out, and whoever arrives brings
///     the culture of where they came from.
///
/// That last part is the other half of the user-facing promise: a region flooded by migrants from
/// one place can, over decades, stop being a region of its own culture - while a trickle is
/// absorbed, and a minority that nobody adds to slowly adopts the country around it (assimilation).
/// Nothing here is a real demographic forecast: like the household review, these are aggregate game
/// rates, chosen so that the sum of the parts is what a player sees.
/// </summary>
public static class DemographicCycle
{
    /// <summary>How much of a region is of working age: neither too young nor past it.</summary>
    public static float WorkingShare(ScenarioState state) =>
        state is null ? 0.62f : Math.Clamp(1f - state.YoungShare - state.OldShare, 0.05f, 0.95f);

    /// <summary>
    /// A country's own newborns: the further the young share has fallen below the reference, the
    /// fewer of them there are. 1 at the reference and above, never below a third.
    /// </summary>
    public static float FertilityFactor(ScenarioState state)
    {
        if (state is null) return 1f;
        var reference = 0.28f;
        return Math.Clamp(1f - (reference - state.YoungShare) * 2f, 0.35f, 1.25f);
    }

    /// <summary>
    /// A country's own deaths: an old population dies at a higher rate even where medicine is good,
    /// because medicine does not make anybody younger. 1 at the reference and below.
    /// </summary>
    public static float MortalityFactor(ScenarioState state, Defines defines)
    {
        if (state is null || defines is null) return 1f;
        var excess = Math.Max(0f, state.OldShare - defines.DemographyReferenceOldShare);
        return 1f + excess * defines.DemographyMortalityOldWeight * 3f;
    }

    /// <summary>
    /// Once per household review, region by region: the bins move. Piggybacking on that review is
    /// deliberate - its births and deaths are the figures this moves on, and a second clock of its
    /// own would drift away from them.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(DemographicCycle))) return;
        var defines = campaign.Defines;
        if (!defines.DemographyAgeStructureEnabled) return;
        var interval = Math.Clamp(defines.EconomyTickDays, 1, 365) / 365f;
        foreach (var state in campaign.States)
        {
            if (state is null || state.Unclaimed || state.Population <= 0) continue;
            if (state.LastDemographyDay == state.LastHouseholdReviewDay) continue;
            state.LastDemographyDay = state.LastHouseholdReviewDay;
            var population = Math.Max(1f, state.Population);
            var born = Math.Clamp(state.ReviewBirths / population, 0f, 0.2f);
            var died = Math.Clamp(state.ReviewDeaths / population, 0f, 0.2f);
            var young = state.YoungShare;
            var old = state.OldShare;
            var working = WorkingShare(state);
            // Ageing: the young grow up, the workers grow old, and the old are the ones who die.
            var matured = young * defines.DemographyMaturationAnnual * interval;
            var aged = working * defines.DemographyAgeingAnnual * interval;
            var oldDeaths = Math.Min(old, died * defines.DemographyMortalityOldWeight);
            var rest = Math.Max(0f, died - oldDeaths);
            state.YoungShare = Math.Clamp(young - matured - rest * 0.5f + born, 0.05f, 0.62f);
            state.OldShare = Math.Clamp(old + aged - oldDeaths, 0.02f, 0.45f);
            MoveCultures(campaign, state);
        }
    }

    /// <summary>
    /// Who the newcomers are, and what happens to a people nobody adds to.
    ///
    /// A trickle joins whichever community is already there; a flood arrives as itself, from the
    /// country the people are coming from, and can grow into the majority of a region over decades.
    /// In the other direction a minority that nothing keeps adding to slowly adopts the country
    /// around it, so a region's culture can move either way and neither way is the default.
    ///
    /// Nothing here changes a region's total: BrainDrain has already moved the people, and this only
    /// decides which community they are counted under.
    /// </summary>
    private static void MoveCultures(Campaign campaign, ScenarioState state)
    {
        var groups = state.PopulationGroups;
        if (groups.Count == 0 || state.Population <= 0) return;
        var dominant = groups.OrderByDescending(group => group.Size).First();
        var net = state.ReviewNetMigration;
        if (net > 0)
        {
            var share = net / (float)state.Population;
            PopulationGroup host = null;
            if (share >= campaign.Defines.DemographyFloodShare && Sender(campaign, state) is { Length: > 0 } culture)
                host = Group(groups, culture, state);
            host ??= groups.Where(group => !string.Equals(group.Culture, dominant.Culture, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(group => group.Size).FirstOrDefault();
            if (host is not null)
            {
                var moved = Math.Min(net, Math.Max(0, dominant.Size - 1));
                dominant.Size -= moved;
                host.Size += moved;
            }
        }
        else if (net < 0)
        {
            // Emigration takes from everyone, and takes most where there are skills to sell.
            var leaving = -net;
            long total = 0;
            foreach (var group in groups) total += group.Size;
            total = Math.Max(1, total);
            foreach (var group in groups.OrderByDescending(group => group.Wealth))
            {
                if (leaving <= 0) break;
                var take = Math.Min(leaving, (long)Math.Round(leaving * group.Size / (double)total));
                take = Math.Min(take, group.Size);
                group.Size -= take;
                leaving -= take;
            }
            if (leaving > 0) dominant.Size = Math.Max(0, dominant.Size - leaving);
        }
        // Assimilation: a minority nothing keeps adding to becomes the country around it, slowly.
        var adopted = campaign.Defines.DemographyAssimilationAnnual;
        if (adopted <= 0f) return;
        foreach (var group in groups.ToArray())
        {
            if (ReferenceEquals(group, dominant) || group.Size <= 0) continue;
            var moved = (long)Math.Floor(group.Size * adopted);
            if (moved <= 0) continue;
            group.Size -= moved;
            dominant.Size += moved;
        }
    }

    /// <summary>The culture arriving in this region: the country's chief sender of migrants, if known.</summary>
    private static string Sender(Campaign campaign, ScenarioState state)
    {
        var owner = campaign.Nations.Find(nation => nation.MapIndex == state.NationMapIndex);
        if (owner is null || owner.BrainGainSourceId < 0) return "";
        var source = campaign.NationById(owner.BrainGainSourceId);
        if (source is null) return "";
        var cultures = DemographyCatalog.PrimaryCultures(source.Tag);
        return cultures.Count > 0 ? cultures[0] : "";
    }

    /// <summary>The group of that culture in this region, made room for if the region has none.</summary>
    private static PopulationGroup Group(List<PopulationGroup> groups, string culture, ScenarioState state)
    {
        foreach (var group in groups)
            if (string.Equals(group.Culture, culture, StringComparison.OrdinalIgnoreCase)) return group;
        var faiths = DemographyCatalog.FaithOf(culture);
        var created = new PopulationGroup
        {
            Class = SocialClass.Lower,
            Sector = Sector.Industry,
            Culture = culture,
            Religion = faiths.Count > 0 ? faiths[0].Religion : "",
            Profession = "labourers",
            Size = 0,
            Employment = 0.9f,
            Wealth = 0.38f,
            StandardOfLiving = state.StandardOfLiving,
            Radicalism = state.Radicalism
        };
        groups.Add(created);
        return created;
    }

    /// <summary>
    /// The country's age structure, aggregated once per review so that anything that needs it - a
    /// modifier source with no campaign in hand, a tooltip, a chart - reads one number instead of
    /// walking a thousand regions.
    /// </summary>
    public static void Aggregate(Campaign campaign)
    {
        foreach (var nation in campaign.Nations)
        {
            var states = campaign.StatesOf(nation);
            if (states.Length == 0)
            {
                nation.YoungShare = campaign.Defines.DemographyYoungShareDefault;
                nation.OldShare = campaign.Defines.DemographyOldShareDefault;
                nation.ReviewBirths = nation.ReviewDeaths = nation.ReviewNetMigration = 0;
                nation.WorkingShare = WorkingShare(null);
                continue;
            }
            double people = 0, young = 0, old = 0;
            long births = 0, deaths = 0, migration = 0;
            foreach (var state in states)
            {
                var size = Math.Max(0, state.Population);
                people += size;
                young += size * state.YoungShare;
                old += size * state.OldShare;
                births += state.ReviewBirths;
                deaths += state.ReviewDeaths;
                migration += state.ReviewNetMigration;
            }
            var total = Math.Max(1d, people);
            nation.YoungShare = (float)(young / total);
            nation.OldShare = (float)(old / total);
            nation.WorkingShare = Math.Clamp(1f - nation.YoungShare - nation.OldShare, 0.05f, 0.95f);
            nation.OldShareExcess = Math.Max(0f, nation.OldShare - campaign.Defines.DemographyReferenceOldShare);
            nation.ReviewBirths = births;
            nation.ReviewDeaths = deaths;
            nation.ReviewNetMigration = migration;
        }
    }

    /// <summary>
    /// What a country's age structure costs it, through the ordinary modifier sources: past the
    /// reference old share a government carries pensions, hospitals and a smaller workforce, and an
    /// unusually young country carries schools instead. Both are paid in the numbers a player already
    /// watches, and neither is a rule of its own.
    /// </summary>
    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation nation)
    {
        if (nation is null || nation.WorkingShare <= 0f) yield break;
        // The dictionary is the country's own, kept and refilled: asked once a day for every
        // country, and a fresh dictionary a day was the collector's budget.
        var modifiers = nation.DemographicCycleModifiers ??= new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        modifiers.Clear();
        var excess = nation.OldShareExcess;
        if (excess > 0.005f)
        {
            modifiers["stability_weekly"] = -excess * 0.05f;
            modifiers["living_weekly"] = -excess * 0.04f;
            yield return (Loc.Get("A country growing old"), modifiers);
            yield break;
        }
        if (nation.YoungShare > 0.34f)
        {
            // Schools to build and jobs to find, and the energy that comes with them.
            modifiers["living_weekly"] = -0.004f;
            modifiers["stability_weekly"] = 0.003f;
            yield return (Loc.Get("A country full of young people"), modifiers);
        }
    }

    /// <summary>
    /// Why the population moved, in the words a tooltip shows: births, deaths, who came and who
    /// left, and what the age structure is doing about it. Every figure here is one the simulation
    /// has already worked out for its own reasons.
    /// </summary>
    public static string Explain(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return "";
        var defines = campaign.Defines;
        var young = nation.YoungShare;
        var working = nation.WorkingShare;
        var old = nation.OldShare;
        var net = nation.ReviewBirths - nation.ReviewDeaths + nation.ReviewNetMigration;
        var (_, _, _, vitalStatus) = HouseholdEconomy.NationalVitalSummary(campaign, nation);
        var lines = new List<string>
        {
            $"{nation.DisplayName} · {Loc.Get(vitalStatus)}",
            Loc.Format("Births {0}, deaths {1}, migration {2} since the last review: {3}.",
                Fmt.Number(nation.ReviewBirths), Fmt.Number(nation.ReviewDeaths),
                Fmt.Number(nation.ReviewNetMigration), (net >= 0 ? "+" : "−") + Fmt.Number(Math.Abs(net))),
            Loc.Format("Young {0}, of working age {1}, past it {2}.", Fmt.Percent(young),
                Fmt.Percent(working), Fmt.Percent(old))
        };
        if (vitalStatus == "Demographic winter (dying of old age)")
            lines.Add(Loc.Get("Demographic collapse from aging: the shrinking working generation cannot support the retired cohorts."));
        else if (vitalStatus == "Demographic boom")
            lines.Add(Loc.Get("Demographic explosion: abundant youth cohort fuels economic growth but demands schools and employment."));
        else if (old > defines.DemographyReferenceOldShare + 0.02f)
            lines.Add(Loc.Get("The country is growing old: fewer are born, more die, and the workers carry the rest."));
        else if (young > 0.34f)
            lines.Add(Loc.Get("The country is young: births are high and there are more mouths than jobs."));
        else
            lines.Add(Loc.Get("The age structure is close to what the country is used to."));

        var migShare = Math.Abs(nation.ReviewNetMigration) / (double)Math.Max(1, nation.Population);
        if (nation.ReviewNetMigration > 0)
        {
            if (migShare >= defines.DemographyFloodShare)
                lines.Add(Loc.Get("Massive immigration influx: foreign cultures are rapidly growing in host regions."));
            else
                lines.Add(Loc.Get("More people are arriving than leaving, and they bring the culture of where they came from."));
        }
        else if (nation.ReviewNetMigration < 0)
        {
            if (migShare >= defines.DemographyFloodShare)
                lines.Add(Loc.Get("Massive emigration outflow: specialists and workers are fleeing abroad."));
            else
                lines.Add(Loc.Get("More people are leaving than arriving, and they leave with their skills."));
        }
        return string.Join("\n", lines);
    }
}

public sealed partial class Nation
{
    /// <summary>What the age structure adds, kept between asks. Not saved; refilled from scratch.</summary>
    internal Dictionary<string, float> DemographicCycleModifiers;
}
