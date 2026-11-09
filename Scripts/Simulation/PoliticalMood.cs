using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How strongly a comfortable, literate country leans towards the market rather than the
    /// state, and how strongly a settled one leans towards authority. Together with the next two
    /// these place the residents on the same two axes the ideologies are already placed on, so
    /// "who does this country actually agree with" needs no table of opinions between ideologies.
    /// </summary>
    public float PoliticalMoodLivingWeight { get; set; } = 1.6f;
    public float PoliticalMoodLiteracyWeight { get; set; } = 0.8f;
    public float PoliticalMoodUrbanWeight { get; set; } = 0.6f;
    public float PoliticalMoodStabilityWeight { get; set; } = 1.2f;
    public float PoliticalMoodWarWeight { get; set; } = 0.6f;

    /// <summary>How quickly an ideology's appeal falls off with the distance between its
    /// position and the residents'. Small means only the nearest positions matter, which is what
    /// makes a mandate a real share rather than one twelfth of everything.</summary>
    public float PoliticalMoodTemperature { get; set; } = 0.5f;

    /// <summary>Weekly radicalism a country without a mandate breeds, and the calm a country
    /// with one restores. Both are small on purpose: this is weather, not an event, and it takes
    /// months of a government nobody supports to matter.</summary>
    public float PoliticalMoodRadicalismPerWeek { get; set; } = 0.0022f;
    public float PoliticalMoodCalmPerWeek { get; set; } = 0.0016f;

    /// <summary>The mandate a government needs, as a multiple of an even share. With twelve
    /// ideologies an even share is one twelfth; twice that is a real plurality, and the multiple
    /// is why a mod that adds a thirteenth ideology does not silently raise the bar.</summary>
    public float PoliticalMoodMandateMultiple { get; set; } = 2.0f;

    public int PoliticalMoodReviewDays { get; set; } = 7;

    /// <summary>
    /// How far a region that rejects the laws it lives under moves away from whoever wrote
    /// them, along both axes. Zero would mean the laws never matter to what people believe;
    /// the default makes a thoroughly rejected law set the difference between "the nearest
    /// position" and "the opposite one".
    /// </summary>
    public float PoliticalMoodLawWeight { get; set; } = 0.45f;

    /// <summary>
    /// How much an ideology that names the country's own form of government among the ones it
    /// is comfortable with appeals to the people living under it, and how much one that does
    /// not loses. Symmetric, so it moves shares between positions rather than inventing
    /// enthusiasm; zero turns the form of government back into a fact that changes nothing.
    /// </summary>
    public float PoliticalMoodGovernmentFormWeight { get; set; } = 0.18f;
}

/// <summary>One ideology's standing among the residents of a country.</summary>
public sealed record PoliticalMoodShare(string Ideology, string Name, float Share, bool Governing);

/// <summary>
/// Where one demographic group of a country actually stands: its own share of the population,
/// the ideology most of it leans towards, and the share that ideology holds among the group.
///
/// The group's position is the one its own regions have, weighted by how many of its people
/// live in each - so a people is not given an opinion by its name. A group whose regions are
/// poor and rural leans differently from the same group living in a rich and urban one, because
/// what changed is where they live, not who they are.
/// </summary>
public sealed record PoliticalGroupLean(string Culture, string CultureName, string Religion, string ReligionName,
    float Population, float Share, string Ideology, string IdeologyName, float Lean, bool Governing);


/// <summary>
/// Who the residents of a country actually are, politically.
///
/// The game already places every ideology on two axes - economic and authority - and reads the
/// distance between two positions for diplomacy. This does the same for the population: a
/// comfortable, literate country leans towards the market and a poor, rural one towards the
/// state; a settled country accepts more authority than a shaken one, and a war pushes it
/// further still. The residents' position is therefore derived from indicators the simulation
/// already keeps rather than invented, and each ideology's share is how close its position is to
/// theirs.
///
/// A government whose ideology most of the country does not share has no mandate, and a country
/// without a mandate breeds unrest every week. That is the whole mechanic: no new saved state,
/// no random draw, no fixed bonus per tag, and an ideology a mod adds is placed on the axes like
/// every other. It is a mood, not a cause - it makes an existing grievance grow faster, and a
/// contented country simply does not notice it.
/// </summary>
public static class PoliticalMood
{
    /// <summary>Where one region's residents stand, from that region's own conditions.</summary>
    public static (float Economic, float Authority) RegionPosition(Campaign c, ScenarioState region, bool atWar)
    {
        if (c is null || region is null) return (0f, 0f);
        var living = M.Clamp01(region.StandardOfLiving);
        var literacy = M.Clamp01(region.Literacy);
        var stability = M.Clamp01(region.Stability);
        var urban = M.Clamp01(region.Urbanization);
        var economic = M.Clamp((living - .45f) * c.Defines.PoliticalMoodLivingWeight +
            (literacy - .50f) * c.Defines.PoliticalMoodLiteracyWeight +
            (urban - .50f) * c.Defines.PoliticalMoodUrbanWeight, -1f, 1f);
        var authority = M.Clamp((stability - .60f) * c.Defines.PoliticalMoodStabilityWeight +
            (atWar ? .25f : 0f), -1f, 1f);
        return (economic, authority);
    }

    /// <summary>
    /// Where a country's residents stand: the position of its regions, weighted by the people
    /// who live in them. A capital and a poor province do not average into a nation that thinks
    /// nothing - they average into a nation that is split.
    /// </summary>
    public static (float Economic, float Authority) PopularPosition(Campaign c, Nation nation)
    {
        if (c is null || nation is null) return (0f, 0f);
        var atWar = c.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        double people = 0, economic = 0, authority = 0;
        foreach (var region in c.StatesOf(nation))
        {
            if (region.Population <= 0) continue;
            var (regionEconomic, regionAuthority) = RegionPosition(c, region, atWar);
            people += region.Population;
            economic += region.Population * (double)regionEconomic;
            authority += region.Population * (double)regionAuthority;
        }
        if (people > 0) return ((float)(economic / people), (float)(authority / people));
        // A country with no regions yet still has a government and still has people in the
        // figures, so fall back to the national aggregates rather than claiming no opinion.
        var support = M.Clamp01(nation.WarSupport);
        return (M.Clamp((M.Clamp01(nation.StandardOfLiving) - .45f) * c.Defines.PoliticalMoodLivingWeight +
                (M.Clamp01(nation.Literacy) - .50f) * c.Defines.PoliticalMoodLiteracyWeight, -1f, 1f),
            M.Clamp((M.Clamp01(nation.Stability) - .60f) * c.Defines.PoliticalMoodStabilityWeight +
                (support - .50f) * c.Defines.PoliticalMoodWarWeight + (atWar ? .25f : 0f), -1f, 1f));
    }

    /// <summary>Every ideology, ordered by the share of the country that leans towards it.</summary>
    public static IReadOnlyList<PoliticalMoodShare> Shares(Campaign c, Nation nation)
    {
        if (c is null || nation is null) return Array.Empty<PoliticalMoodShare>();
        var (economic, authority) = PopularPosition(c, nation);
        var governing = IdeologyCatalog.Knows(nation.Ideology) ? nation.Ideology : "";
        var floor = MathF.Max(.05f, c.Defines.PoliticalMoodTemperature);
        var appeals = new List<(IdeologyDefinition Ideology, float Appeal)>();
        var total = 0f;
        foreach (var ideology in IdeologyCatalog.All)
        {
            var distance = MathF.Abs(economic - ideology.Economic) + MathF.Abs(authority - ideology.Authority);
            var appeal = MathF.Exp(-distance / floor);
            appeals.Add((ideology, appeal));
            total += appeal;
        }
        if (total <= 0f) return Array.Empty<PoliticalMoodShare>();
        return appeals
            .Select(pair => new PoliticalMoodShare(pair.Ideology.Id, pair.Ideology.Name, pair.Appeal / total,
                string.Equals(pair.Ideology.Id, governing, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(share => share.Share)
            .ThenBy(share => share.Ideology, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Where one region's residents stand, with the laws they live under taken into account.
    ///
    /// The base position is the region's own conditions. Rejection of the laws then bends it
    /// away from whoever wrote them, along both axes, by the share that rejects them -
    /// <see cref="PoliticalCohorts"/>'s own reading, not a second opinion. A region that
    /// accepts its laws is left exactly where its conditions put it, and a region never
    /// reviewed yet (approval still negative) is left alone rather than guessed at.
    /// </summary>
    public static (float Economic, float Authority) RegionPositionWithLaws(Campaign c, ScenarioState region,
        Nation nation, bool atWar)
    {
        var (economic, authority) = RegionPosition(c, region, atWar);
        if (c is null || region is null || nation is null || region.LawApproval < 0f) return (economic, authority);
        IdeologyDefinition governing = null;
        foreach (var ideology in IdeologyCatalog.All)
            if (string.Equals(ideology.Id, nation.Ideology, StringComparison.OrdinalIgnoreCase)) { governing = ideology; break; }
        if (governing is null) return (economic, authority);
        var pull = M.Clamp01(1f - region.LawApproval) * MathF.Max(0f, c.Defines.PoliticalMoodLawWeight);
        return (M.Clamp(economic - governing.Economic * pull, -1f, 1f),
            M.Clamp(authority - governing.Authority * pull, -1f, 1f));
    }

    /// <summary>
    /// Every ideology's standing among the residents of one region: the same ideologies the
    /// government itself is chosen from, read from the region's own conditions, the laws it
    /// lives under and the form of government it lives in.
    ///
    /// This is the question the country-wide <see cref="Shares"/> cannot answer, because a
    /// country is not of one mind: a rich city and a poor countryside under the same flag are
    /// nearest to different positions, and telling them apart is the whole point of looking at
    /// a region rather than at a nation.
    /// </summary>
    public static IReadOnlyList<PoliticalMoodShare> RegionShares(Campaign c, ScenarioState region, Nation nation)
    {
        if (c is null || region is null || nation is null) return Array.Empty<PoliticalMoodShare>();
        var atWar = false;
        foreach (var war in c.Wars)
            if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
        var (economic, authority) = RegionPositionWithLaws(c, region, nation, atWar);
        var governing = IdeologyCatalog.Knows(nation.Ideology) ? nation.Ideology : "";
        var temperature = MathF.Max(.05f, c.Defines.PoliticalMoodTemperature);
        var formWeight = MathF.Max(0f, c.Defines.PoliticalMoodGovernmentFormWeight);
        var appeals = new List<(IdeologyDefinition Ideology, float Appeal)>();
        var total = 0f;
        foreach (var ideology in IdeologyCatalog.All)
        {
            var distance = MathF.Abs(economic - ideology.Economic) + MathF.Abs(authority - ideology.Authority);
            var appeal = MathF.Exp(-distance / temperature);
            // The form of government people actually live under is one they have come to terms
            // with: an ideology comfortable in it appeals a little more, one that is not a
            // little less. Symmetric, so it moves shares between positions rather than
            // inventing enthusiasm, and an ideology that minds no arrangement is left alone.
            if (formWeight > 0f && ideology.PrefersGovernments.Count > 0)
            {
                var comfortable = ideology.PrefersGovernments.Contains(nation.Government, StringComparer.OrdinalIgnoreCase);
                appeal *= comfortable ? 1f + formWeight : 1f - formWeight;
            }
            appeals.Add((ideology, appeal));
            total += appeal;
        }
        if (total <= 0f) return Array.Empty<PoliticalMoodShare>();
        return appeals
            .Select(pair => new PoliticalMoodShare(pair.Ideology.Id, pair.Ideology.Name, pair.Appeal / total,
                string.Equals(pair.Ideology.Id, governing, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(share => share.Share)
            .ThenBy(share => share.Ideology, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The share of the country that leans towards the government's own ideology.</summary>
    public static float Mandate(Campaign c, Nation nation)
    {
        foreach (var share in Shares(c, nation)) if (share.Governing) return share.Share;
        return 0f;
    }

    /// <summary>What the residents want that the government is not giving them, 0 to 1.</summary>
    public static float Opposition(Campaign c, Nation nation) => MathF.Max(0f, 1f - Mandate(c, nation));

    /// <summary>
    /// The country broken down by the groups the population data actually has - culture and
    /// religion - each with its share of the people and the ideology most of it leans towards.
    ///
    /// Class and profession are not dimensions this game models, so they are not invented here:
    /// a chart of carpenters would be a chart of a number nobody keeps. What the data does keep
    /// is who lives where, so a group's leaning is the leaning of its own regions, weighted by
    /// how many of its people are in each. A people is never given an opinion by its name.
    /// </summary>
    public static IReadOnlyList<PoliticalGroupLean> GroupLeans(Campaign c, Nation nation, int limit = 6)
    {
        if (c is null || nation is null) return Array.Empty<PoliticalGroupLean>();
        var atWar = c.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        var governing = IdeologyCatalog.Knows(nation.Ideology) ? nation.Ideology : "";
        var temperature = MathF.Max(.05f, c.Defines.PoliticalMoodTemperature);
        var groups = new Dictionary<(string Culture, string Religion), (double People, double Economic, double Authority)>();
        double total = 0;
        foreach (var region in c.StatesOf(nation))
        {
            var (economic, authority) = RegionPosition(c, region, atWar);
            foreach (var group in region.PopulationGroups)
            {
                if (group.Size <= 0) continue;
                var key = (group.Culture ?? "", group.Religion ?? "");
                var current = groups.GetValueOrDefault(key);
                groups[key] = (current.People + group.Size, current.Economic + group.Size * (double)economic,
                    current.Authority + group.Size * (double)authority);
                total += group.Size;
            }
        }
        if (total <= 0) return Array.Empty<PoliticalGroupLean>();
        var leans = new List<PoliticalGroupLean>(groups.Count);
        foreach (var (key, value) in groups)
        {
            if (value.People <= 0) continue;
            var economic = (float)(value.Economic / value.People);
            var authority = (float)(value.Authority / value.People);
            IdeologyDefinition best = null;
            var bestAppeal = -1f;
            var appealTotal = 0f;
            foreach (var ideology in IdeologyCatalog.All)
            {
                var distance = MathF.Abs(economic - ideology.Economic) + MathF.Abs(authority - ideology.Authority);
                var appeal = MathF.Exp(-distance / temperature);
                appealTotal += appeal;
                if (appeal > bestAppeal) { bestAppeal = appeal; best = ideology; }
            }
            if (best is null || appealTotal <= 0f) continue;
            leans.Add(new PoliticalGroupLean(key.Culture, DemographyCatalog.Culture(key.Culture)?.Name ?? key.Culture,
                key.Religion, DemographyCatalog.Religion(key.Religion)?.Name ?? key.Religion,
                (float)value.People, (float)(value.People / total), best.Id, best.Name, bestAppeal / appealTotal,
                string.Equals(best.Id, governing, StringComparison.OrdinalIgnoreCase)));
        }
        return leans
            .OrderByDescending(lean => lean.Population)
            .ThenBy(lean => lean.Culture, StringComparer.Ordinal)
            .ThenBy(lean => lean.Religion, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same review, yielded between countries so every region's temper is not
    /// taken in one frame.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(PoliticalMood))) yield break;
        var interval = c.Defines.PoliticalMoodReviewDays;
        if (interval <= 0 || c.ElapsedDays % interval != 0) yield break;
        var floor = MathF.Max(0f, c.Defines.PoliticalMoodMandateMultiple) /
            MathF.Max(1f, IdeologyCatalog.All.Count);
        var rise = MathF.Max(0f, c.Defines.PoliticalMoodRadicalismPerWeek);
        var calm = MathF.Max(0f, c.Defines.PoliticalMoodCalmPerWeek);
        var scanned = 0;
        foreach (var nation in c.Nations)
        {
            if (++scanned % 16 == 0) yield return 0;
            if (nation.Population <= 0 || nation.Movement is not null || !IdeologyCatalog.Knows(nation.Ideology)) continue;
            var mandate = Mandate(c, nation);
            var missing = MathF.Max(0f, floor - mandate);
            var surplus = MathF.Max(0f, mandate - floor);
            if (missing <= 0f && surplus <= 0f) continue;
            foreach (var region in c.StatesOf(nation))
            {
                if (region.Population <= 0 || !float.IsFinite(region.Radicalism)) continue;
                var current = M.Clamp01(region.Radicalism);
                // A government nobody voted for makes an existing grievance grow faster; it does
                // not invent one. The (half plus current) factor is why an already angry region
                // moves further than a calm one under the same government.
                var next = missing > 0f
                    ? current + missing * rise * (.5f + current)
                    : current - surplus * calm;
                region.Radicalism = M.Clamp01(next);
            }
        }
    }
}
