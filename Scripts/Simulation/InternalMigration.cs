using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Whether people move between the regions of their own country (InternalMigration).</summary>
    public bool InternalMigrationEnabled { get; set; } = true;

    /// <summary>
    /// A year's share of a region's people who leave it for a better one at home, per unit by which
    /// the region falls short of its country's average pull (InternalMigration.Pull: the standard of
    /// living, work, peace and order). 0.03 moves about one person in a hundred a year out of a
    /// region a third below the average - the scale of rural flight in a country that is growing.
    /// </summary>
    public float InternalMigrationAnnual { get; set; } = 0.03f;

    /// <summary>
    /// A review's share of a region's people who flee to the safe part of their own country, per
    /// unit of war damage past a tenth. Most of a war's displaced never cross a border.
    /// </summary>
    public float InternalRefugeeShare { get; set; } = 0.04f;

    /// <summary>The most of a region's people who leave it in one review.</summary>
    public float InternalMigrationMaxShare { get; set; } = 0.01f;

    /// <summary>The distance, in kilometres, at which a destination's pull is halved.</summary>
    public float InternalMigrationReachKm { get; set; } = 600f;

    /// <summary>
    /// A year's settlers, as a share of a region's people, that a country sends into a region
    /// where its own people are under half, per unit of its "settlement" modifier. 0.008 at a
    /// full unit is the Soviet Baltic: the Russian share of Latvia went from a tenth to a third
    /// in forty-four years, about half a point a year.
    /// </summary>
    public float SettlementAnnual { get; set; } = 0.008f;

    /// <summary>Below this share of a region the country's own people are settled there; at or above it, not.</summary>
    public float SettlementBelowShare { get; set; } = 0.5f;
}

public sealed partial class ScenarioState
{
    /// <summary>People who arrived from (+) or left for (−) the country's other regions at the last review.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long ReviewInternalMigration;
}

/// <summary>
/// People moving inside their own country.
///
/// The game moved people only across borders (BrainDrain), so within a country nobody ever moved:
/// a region shelled for a year kept every inhabitant it had, the capital never grew on the
/// provinces, and no people ever spread across a country it shared - which is how the native
/// peoples of most of the world were displaced, by settlers from the same state rather than by
/// armies. Now at every household review each region is weighed against its country's average:
///
///   - its pull is the standard of living, work, order and peace, with the capital's weight
///     (Pull);
///   - a region below the average sends people to the ones above it, in proportion to the gap,
///     the nearer and the larger the destination the more of them (InternalMigrationReachKm);
///   - war damage past a tenth drives people out to the safe regions on top of that - the
///     internally displaced;
///   - and the people who move are the people who live there, profession and faith and culture:
///     a Russian worker who moves to Almaty is a Russian worker in Almaty.
///
/// The country's total never changes. What changes is where its peoples live, and so, over the
/// decades, whose land a region is - the slow half of what DemographicCycle's floods and
/// assimilation do from outside.
/// </summary>
public static class InternalMigration
{
    /// <summary>People moved inside their countries on the current day, for the benches.</summary>
    public static long MovedToday { get; private set; }

    private static readonly List<ScenarioState> Regions = new();
    private static readonly List<float> Pulls = new();
    private static readonly List<(ScenarioState State, double Weight)> Destinations = new();
    private static readonly Dictionary<int, (float Latitude, float Longitude)> Centres = new();
    private static Campaign _centresOf;
    private static int _centresRevision = -1;
    private static int _movedDay = -1;
    private static Campaign _movedCampaign;

    /// <summary>
    /// What draws people to a region, and keeps them: the standard of living, work, order and
    /// peace, and the capital's weight. A number to compare a country's regions by, not a scale.
    /// </summary>
    public static float Pull(Nation nation, ScenarioState state)
    {
        if (state is null) return 0f;
        var capital = nation is not null && state.MapIndex == nation.CapitalStateMapIndex ? 0.15f : 0f;
        return state.StandardOfLiving + capital +
               0.5f * (state.EmploymentTarget - 0.88f) -
               1.2f * M.Clamp01(state.WarDamage) -
               0.4f * M.Clamp01(state.LivelihoodStress) -
               0.6f * MathF.Max(0f, 0.35f - state.Stability);
    }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same migration pass, yielded between batches of regions and countries.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null) yield break;
        if (_movedDay != campaign.ElapsedDays || !ReferenceEquals(_movedCampaign, campaign))
        { MovedToday = 0; _movedDay = campaign.ElapsedDays; _movedCampaign = campaign; }
        var defines = campaign.Defines;
        if (!defines.InternalMigrationEnabled || !defines.HouseholdDynamicsEnabled) yield break;
        if (campaign.ElapsedDays <= 0 || campaign.ElapsedDays % Math.Max(1, defines.EconomyTickDays) != 0) yield break;
        var interval = Math.Clamp(defines.EconomyTickDays, 1, 365) / 365f;
        EnsureCentres(campaign);
        var reset = 0;
        foreach (var state in campaign.States) { if (++reset % 512 == 0) yield return 0; state.ReviewInternalMigration = 0; }
        yield return 0;

        var ticked = 0;
        foreach (var nation in campaign.Nations)
        {
            if (++ticked % 8 == 0) yield return 0;
            if (nation.Population <= 0 || nation.Movement is not null) continue;
            var states = campaign.StatesOf(nation);
            if (states.Length < 2) continue;
            Regions.Clear();
            Pulls.Clear();
            double weighted = 0, people = 0;
            foreach (var state in states)
            {
                if (state.Unclaimed || state.Population <= 0) continue;
                var pull = Pull(nation, state);
                Regions.Add(state);
                Pulls.Add(pull);
                weighted += pull * (double)state.Population;
                people += state.Population;
            }
            if (Regions.Count < 2 || people <= 0) continue;
            var average = (float)(weighted / people);

            for (var from = 0; from < Regions.Count; from++)
            {
                var source = Regions[from];
                var gap = average - Pulls[from];
                var refugees = MathF.Max(0f, source.WarDamage - 0.10f) * defines.InternalRefugeeShare;
                var share = MathF.Max(0f, gap) * defines.InternalMigrationAnnual * interval + refugees;
                share = MathF.Min(share, MathF.Max(0f, defines.InternalMigrationMaxShare));
                var leaving = (long)Math.Round(source.Population * (double)share);
                if (leaving < 100) continue;

                Destinations.Clear();
                double total = 0;
                var here = Centres.GetValueOrDefault(source.MapIndex);
                for (var to = 0; to < Regions.Count; to++)
                {
                    if (to == from) continue;
                    var target = Regions[to];
                    var advantage = Pulls[to] - MathF.Max(Pulls[from], average - 0.02f);
                    if (advantage <= 0f || target.WarDamage >= 0.10f) continue;
                    var there = Centres.GetValueOrDefault(target.MapIndex);
                    var km = NavalOperations.Distance(here.Latitude, here.Longitude, there.Latitude, there.Longitude) * 111f;
                    var weight = advantage * Math.Sqrt(target.Population) /
                                 (1.0 + km / Math.Max(1f, defines.InternalMigrationReachKm));
                    if (weight <= 0) continue;
                    Destinations.Add((target, weight));
                    total += weight;
                }
                if (total <= 0) continue;
                Destinations.Sort((a, b) => b.Weight.CompareTo(a.Weight));
                if (Destinations.Count > 3) Destinations.RemoveRange(3, Destinations.Count - 3);
                total = 0;
                foreach (var (_, weight) in Destinations) total += weight;
                var left = leaving;
                for (var index = 0; index < Destinations.Count && left > 0; index++)
                {
                    var (target, weight) = Destinations[index];
                    var count = index == Destinations.Count - 1 ? left : (long)Math.Round(leaving * weight / total);
                    count = Math.Min(count, left);
                    var moved = Move(source, target, count);
                    left -= moved;
                    MovedToday += moved;
                }
            }
            Settle(campaign, nation, interval);
        }
    }

    /// <summary>
    /// Settlement: a state that moves its own people into the land where they are few. The way
    /// most of the world's native peoples were displaced - Russians into the Baltic and Kazakhstan,
    /// Han into Xinjiang and Tibet, settlers into occupied ground - was not a war but a policy,
    /// and a policy is a modifier key ("settlement", carried by ideologies in the base game). The
    /// settlers are the country's own people from its most populous region of its own people, and
    /// they go to every region of the country, at peace, where those people are under
    /// SettlementBelowShare - after which assimilation runs towards them (DemographicCycle).
    /// </summary>
    private static void Settle(Campaign campaign, Nation nation, float interval)
    {
        var policy = NationalModifiers.Of(nation, "settlement");
        if (policy <= 0f) return;
        var defines = campaign.Defines;
        var national = DemographyCatalog.PrimaryCulture(nation.Tag);
        if (string.IsNullOrEmpty(national)) return;
        ScenarioState home = null;
        long homePeople = 0;
        foreach (var state in Regions)
        {
            var own = PeopleOf(state, national);
            if (own < state.Population / 2 || own <= homePeople) continue;
            home = state;
            homePeople = own;
        }
        if (home is null) return;
        foreach (var state in Regions)
        {
            if (ReferenceEquals(state, home) || state.WarDamage >= 0.10f) continue;
            if (PeopleOf(state, national) >= state.Population * (double)defines.SettlementBelowShare) continue;
            // Not into the land of another of the country's own peoples: settlement is done to
            // the peoples a state is not for.
            if (DemographyCatalog.IsPrimary(nation.Tag, MajorityOf(state))) continue;
            var settlers = (long)Math.Round(state.Population * (double)(defines.SettlementAnnual * policy * interval));
            settlers = Math.Min(settlers, PeopleOf(home, national) / 100);
            if (settlers < 50) continue;
            var moved = Move(home, state, settlers, culture: national);
            MovedToday += moved;
        }
    }

    private static readonly Dictionary<string, long> Tally = new(StringComparer.OrdinalIgnoreCase);

    private static string MajorityOf(ScenarioState state)
    {
        Tally.Clear();
        foreach (var group in state.PopulationGroups)
            if (group.Size > 0) Tally[group.Culture ?? ""] = Tally.GetValueOrDefault(group.Culture ?? "") + group.Size;
        string majority = "";
        long most = -1;
        foreach (var (culture, people) in Tally)
            if (people > most || people == most && string.CompareOrdinal(culture, majority) < 0) { majority = culture; most = people; }
        return majority;
    }

    private static long PeopleOf(ScenarioState state, string culture)
    {
        long people = 0;
        foreach (var group in state.PopulationGroups)
            if (string.Equals(group.Culture, culture, StringComparison.OrdinalIgnoreCase)) people += Math.Max(0, group.Size);
        return people;
    }

    /// <summary>
    /// Move people, as the people they are, from one region to another: each group gives its share,
    /// and joins the group of the same culture, faith and trade at the destination. Across a
    /// border (BrainDrain) the move is not counted as the country's internal migration.
    /// </summary>
    internal static long Move(ScenarioState from, ScenarioState to, long count, bool withinCountry = true,
        string culture = null)
    {
        if (from is null || to is null || count <= 0 || from.Population <= 0) return 0;
        // A region whose groups have drifted from its population is matched first, the way
        // every other change of a region's numbers does it, so what moves is what is there.
        EconomySystem.MatchGroups(from);
        EconomySystem.MatchGroups(to);
        count = Math.Min(count, from.Population - 1);
        long total = 0;
        foreach (var group in from.PopulationGroups)
            if (Moves(group, culture)) total += Math.Max(0, group.Size);
        count = Math.Min(count, total);
        if (total <= 0 || count <= 0) return 0;
        long moved = 0;
        var groups = from.PopulationGroups;
        var existing = groups.Count;
        for (var index = 0; index < existing && moved < count; index++)
        {
            var group = groups[index];
            if (group.Size <= 0 || !Moves(group, culture)) continue;
            var take = index == existing - 1
                ? count - moved
                : (long)Math.Round(count * group.Size / (double)total);
            take = Math.Min(take, Math.Min(group.Size, count - moved));
            if (take <= 0) continue;
            group.Size -= take;
            Arrive(to, group, take);
            moved += take;
        }
        from.Population -= moved;
        to.Population += moved;
        if (withinCountry)
        {
            from.ReviewInternalMigration -= moved;
            to.ReviewInternalMigration += moved;
        }
        return moved;
    }

    private static bool Moves(PopulationGroup group, string culture) =>
        culture is null || string.Equals(group.Culture, culture, StringComparison.OrdinalIgnoreCase);

    private static void Arrive(ScenarioState to, PopulationGroup like, long people)
    {
        foreach (var group in to.PopulationGroups)
        {
            if (!string.Equals(group.Culture, like.Culture, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(group.Religion, like.Religion, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(group.Profession, like.Profession, StringComparison.OrdinalIgnoreCase) ||
                group.Class != like.Class) continue;
            group.Size += people;
            return;
        }
        to.PopulationGroups.Add(new PopulationGroup
        {
            Class = like.Class, Sector = like.Sector, Culture = like.Culture, Religion = like.Religion,
            Profession = like.Profession, Size = people, Employment = like.Employment, Wealth = like.Wealth,
            StandardOfLiving = to.StandardOfLiving, Radicalism = like.Radicalism
        });
    }

    /// <summary>Every region's centre, once per map: a region's provinces averaged.</summary>
    private static void EnsureCentres(Campaign campaign)
    {
        if (ReferenceEquals(_centresOf, campaign) && _centresRevision == campaign.MapRevision) return;
        _centresOf = campaign;
        _centresRevision = campaign.MapRevision;
        Centres.Clear();
        var topology = campaign.ProvinceMap;
        if (topology is null) return;
        var sums = new Dictionary<int, (double Latitude, double Longitude, int Count)>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var state = topology.ProvinceToState[province];
            if (state == 0) continue;
            var centre = topology.CenterOf(province);
            var sum = sums.GetValueOrDefault(state);
            sums[state] = (sum.Latitude + centre.Latitude, sum.Longitude + centre.Longitude, sum.Count + 1);
        }
        foreach (var (state, sum) in sums)
            Centres[state] = ((float)(sum.Latitude / sum.Count), (float)(sum.Longitude / sum.Count));
    }
}
