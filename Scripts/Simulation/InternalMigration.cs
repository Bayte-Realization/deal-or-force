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

    [ThreadStatic] private static List<ScenarioState> _regionsScratch;
    [ThreadStatic] private static List<float> _pullsScratch;
    [ThreadStatic] private static List<(ScenarioState State, double Weight)> _destinationsScratch;
    [ThreadStatic] private static long[] _migrationResultsScratch;

    private static List<ScenarioState> Regions => _regionsScratch ??= new();
    private static List<float> Pulls => _pullsScratch ??= new();
    private static List<(ScenarioState State, double Weight)> Destinations => _destinationsScratch ??= new();

    private sealed class MovedState
    {
        public int Day = int.MinValue;
        public long Moved;
        public readonly List<Nation> ActiveNations = new();
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, MovedState> MovedStates = new();

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

    internal static bool IsNationDue(Campaign campaign, int nationId, int cycle, bool staggered) =>
        staggered
            ? MovementRules.OnItsDay(campaign, nationId, cycle)
            : campaign.ElapsedDays % cycle == 0;

    /// <summary>The same migration pass, yielded between batches of regions and countries.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null) yield break;
        var movedState = MovedStates.GetOrCreateValue(campaign);
        if (movedState.Day != campaign.ElapsedDays)
        { movedState.Day = campaign.ElapsedDays; movedState.Moved = 0; MovedToday = 0; }
        var defines = campaign.Defines;
        if (!defines.InternalMigrationEnabled || !defines.HouseholdDynamicsEnabled) yield break;
        if (campaign.ElapsedDays <= 0) yield break;
        var every = Math.Max(1, defines.EconomyTickDays);
        var staggered = every > 1 && (defines.StaggerWeeklyEconomy || defines.EconomyWeekStaggered);
        if (!staggered && campaign.ElapsedDays % every != 0) yield break;

        campaign.StatesOf(0);
        if (!staggered)
        {
            var states = campaign.States;
            for (var i = 0; i < states.Count; i++) states[i].ReviewInternalMigration = 0;
        }

        var active = movedState.ActiveNations;
        active.Clear();
        var nations = campaign.ActiveNations;
        for (var i = 0; i < nations.Count; i++)
        {
            var nation = nations[i];
            if (nation.Movement is not null) continue;
            if (staggered)
            {
                if (!IsNationDue(campaign, nation.Id, every, staggered: true)) continue;
                var ownedStates = campaign.StatesOf(nation);
                for (var s = 0; s < ownedStates.Length; s++) ownedStates[s].ReviewInternalMigration = 0;
                if (nation.Population <= 0 || ownedStates.Length < 2) continue;
            }
            else if (nation.Population <= 0 || campaign.StatesOf(nation).Length < 2)
            {
                continue;
            }
            active.Add(nation);
        }
        if (active.Count == 0) yield break;
        var interval = Math.Clamp(every, 1, 365) / 365f;
        var centres = LandAtlas.CentresOf(campaign);
        // Largest-Processing-Time-first by region count (ascending Nation.Id tie-break) so
        // multi-region empires run at the front of worker batches rather than creating tail lag.
        if (active.Count > 2)
        {
            active.Sort((a, b) =>
            {
                var cmp = campaign.StatesOf(b).Length.CompareTo(campaign.StatesOf(a).Length);
                return cmp != 0 ? cmp : a.Id.CompareTo(b.Id);
            });
        }
        var activeCount = active.Count;
        const int batchSize = 16;
        for (var start = 0; start < activeCount; start += batchSize)
        {
            var count = Math.Min(batchSize, activeCount - start);
            if (_migrationResultsScratch is null || _migrationResultsScratch.Length < count)
                _migrationResultsScratch = new long[Math.Max(count, batchSize)];
            var batchStart = start;
            DeterministicParallel.Slots(campaign, count, _migrationResultsScratch,
                i => MigrateNation(campaign, active[batchStart + i], defines, interval, centres),
                (_, moved) => { movedState.Moved += moved; MovedToday = movedState.Moved; });
            if (start + count < activeCount && Campaign.StageOverdue()) yield return 0;
        }
        active.Clear();
    }

    private static long MigrateNation(Campaign campaign, Nation nation, Defines defines, float interval,
        (float Latitude, float Longitude)[] centres)
    {
        if (nation.Population <= 0 || nation.Movement is not null) return 0;
        var states = campaign.StatesOf(nation);
        if (states.Length < 2) return 0;
        var regions = Regions;
        var pulls = Pulls;
        var destinations = Destinations;
        regions.Clear();
        pulls.Clear();
        double weighted = 0, people = 0;
        foreach (var state in states)
        {
            if (state.Unclaimed || state.Population <= 0) continue;
            var pull = Pull(nation, state);
            regions.Add(state);
            pulls.Add(pull);
            weighted += pull * (double)state.Population;
            people += state.Population;
        }
        if (regions.Count < 2 || people <= 0) return 0;
        var average = (float)(weighted / people);
        long movedTotal = 0;

        for (var from = 0; from < regions.Count; from++)
        {
            var source = regions[from];
            var gap = average - pulls[from];
            var refugees = MathF.Max(0f, source.WarDamage - 0.10f) * defines.InternalRefugeeShare;
            var share = MathF.Max(0f, gap) * defines.InternalMigrationAnnual * interval + refugees;
            share = MathF.Min(share, MathF.Max(0f, defines.InternalMigrationMaxShare));
            var leaving = (long)Math.Round(source.Population * (double)share);
            if (leaving < 100) continue;

            destinations.Clear();
            double total = 0;
            (float Latitude, float Longitude) here = (uint)source.MapIndex < (uint)centres.Length ? centres[source.MapIndex] : default;
            for (var to = 0; to < regions.Count; to++)
            {
                if (to == from) continue;
                var target = regions[to];
                var advantage = pulls[to] - MathF.Max(pulls[from], average - 0.02f);
                if (advantage <= 0f || target.WarDamage >= 0.10f) continue;
                (float Latitude, float Longitude) there = (uint)target.MapIndex < (uint)centres.Length ? centres[target.MapIndex] : default;
                var km = NavalOperations.Distance(here.Latitude, here.Longitude, there.Latitude, there.Longitude) * 111f;
                var weight = advantage * Math.Sqrt(target.Population) /
                             (1.0 + km / Math.Max(1f, defines.InternalMigrationReachKm));
                if (weight <= 0) continue;
                destinations.Add((target, weight));
                total += weight;
            }
            if (total <= 0) continue;
            destinations.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            if (destinations.Count > 3) destinations.RemoveRange(3, destinations.Count - 3);
            total = 0;
            foreach (var (_, weight) in destinations) total += weight;
            var left = leaving;
            for (var index = 0; index < destinations.Count && left > 0; index++)
            {
                var (target, weight) = destinations[index];
                var count = index == destinations.Count - 1 ? left : (long)Math.Round(leaving * weight / total);
                count = Math.Min(count, left);
                var moved = Move(source, target, count);
                if (moved > 0)
                {
                    MovementRules.MarkPeopleStateDirty(campaign, source);
                    MovementRules.MarkPeopleStateDirty(campaign, target);
                }
                left -= moved;
                movedTotal += moved;
            }
        }
        movedTotal += Settle(campaign, nation, interval);
        return movedTotal;
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
    private static long Settle(Campaign campaign, Nation nation, float interval)
    {
        var policy = NationalModifiers.OfSlot(nation, NationalModifiers.SlotSettlement);
        if (policy <= 0f) return 0;
        var defines = campaign.Defines;
        var national = DemographyCatalog.PrimaryCulture(nation.Tag);
        if (string.IsNullOrEmpty(national)) return 0;
        var regions = Regions;
        ScenarioState home = null;
        long homePeople = 0;
        for (var i = 0; i < regions.Count; i++)
        {
            var state = regions[i];
            var own = PeopleOf(state, national);
            if (own < state.Population / 2 || own <= homePeople) continue;
            home = state;
            homePeople = own;
        }
        if (home is null) return 0;
        long movedTotal = 0;
        var ownerPrimary = DemographyCatalog.PrimarySet(nation.Tag);
        for (var i = 0; i < regions.Count; i++)
        {
            var state = regions[i];
            if (ReferenceEquals(state, home) || state.WarDamage >= 0.10f) continue;
            if (PeopleOf(state, national) >= state.Population * (double)defines.SettlementBelowShare) continue;
            // Not into the land of another of the country's own peoples: settlement is done to
            // the peoples a state is not for.
            var majority = MajorityOf(state);
            if (ownerPrimary is not null && majority is not null && ownerPrimary.Contains(majority)) continue;
            var settlers = (long)Math.Round(state.Population * (double)(defines.SettlementAnnual * policy * interval));
            settlers = Math.Min(settlers, PeopleOf(home, national) / 100);
            if (settlers < 50) continue;
            var movedSettlers = Move(home, state, settlers, culture: national);
            if (movedSettlers > 0)
            {
                MovementRules.MarkPeopleStateDirty(campaign, home);
                MovementRules.MarkPeopleStateDirty(campaign, state);
            }
            movedTotal += movedSettlers;
        }
        return movedTotal;
    }

    [ThreadStatic] private static Dictionary<string, long> _tallyScratch;
    private static Dictionary<string, long> Tally => _tallyScratch ??= new(StringComparer.OrdinalIgnoreCase);

    private static string MajorityOf(ScenarioState state)
    {
        Tally.Clear();
        var groups = state.PopulationGroups;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group.Size > 0)
                System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(Tally, group.Culture ?? "", out _) += group.Size;
        }
        string majority = "";
        long most = -1;
        foreach (var (culture, people) in Tally)
            if (people > most || people == most && string.CompareOrdinal(culture, majority) < 0) { majority = culture; most = people; }
        return majority;
    }

    private static long PeopleOf(ScenarioState state, string culture)
    {
        long people = 0;
        var groups = state.PopulationGroups;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (string.Equals(group.Culture, culture, StringComparison.OrdinalIgnoreCase)) people += Math.Max(0, group.Size);
        }
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
        Arrivals arrivals = null;
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
            // Inside a country a group nearly always finds its own kind near the top of the
            // destination's list, and looking down the list is the cheaper way (internal
            // migration 37 ms over the 14 late days against 47 with the links). Across a border
            // it usually finds nobody, having read the whole list to learn it - that is what
            // the links are for.
            if (withinCountry) Arrive(to, group, take);
            else
            {
                if (arrivals is null) (arrivals = _arrivals ??= new Arrivals()).Read(to.PopulationGroups);
                Arrive(to, group, take, arrivals);
            }
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

    /// <summary>
    /// The groups of the region being moved into, threaded by kind - one people of one faith -
    /// for the length of one move.
    ///
    /// Every group that left looked for its own kind down the destination's list, comparing two
    /// names without regard to case for each group it passed. Emigrants mostly find nobody of
    /// their kind abroad, so each of them read the whole list of a region that holds hundreds of
    /// groups: 114 microseconds a move on the late save, 67 ms of the 14 days for the 586 moves
    /// brain drain made. Now each name is numbered - the same case-blind equality, asked of a
    /// table a name at a time and remembered by the name object, since a name never changes
    /// what it spells - and each group of the destination is linked to the next of its kind,
    /// so an arrival walks only the groups it could join. They are still met in the region's
    /// own order, so the same group is joined and a new one is added in the same place
    /// (brain drain 111 -> 76 ms).
    ///
    /// One per thread: countries migrate side by side (TickStages). The numbering is kept
    /// between moves, because it is a fact about names and not about a world; the links are
    /// made again for every move.
    /// </summary>
    private sealed class Arrivals
    {
        private readonly Dictionary<string, int> _cultures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _religions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _cultureObjects = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _religionObjects = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<long, int> _kinds = new();
        private int[] _first = new int[64];
        private int[] _last = new int[64];
        private int[] _next = new int[256];
        private int _count;
        private string _lastCulture, _lastReligion;
        private int _lastCultureNumber, _lastReligionNumber;
        private bool _cultureRead, _religionRead;
        private long _lastKey = -1;
        private int _lastKind;

        /// <summary>Link the destination's groups as they stand.</summary>
        public void Read(List<PopulationGroup> groups)
        {
            // A save that gave every group a name object of its own would grow the object
            // tables without end; past this they start again, which costs a lookup by spelling.
            if (_cultureObjects.Count > 8192 || _religionObjects.Count > 8192)
            {
                _cultures.Clear();
                _religions.Clear();
                _cultureObjects.Clear();
                _religionObjects.Clear();
                _cultureRead = _religionRead = false;
            }
            _kinds.Clear();
            _lastKey = -1;
            _count = 0;
            if (_next.Length < groups.Count) _next = new int[Math.Max(groups.Count, _next.Length * 2)];
            for (var i = 0; i < groups.Count; i++) Append(KindOf(groups[i]));
        }

        /// <summary>The kind this group is: its people and its faith, as one number for this move.</summary>
        public int KindOf(PopulationGroup group)
        {
            var culture = group.Culture;
            if (!_cultureRead || !ReferenceEquals(culture, _lastCulture))
            {
                _cultureRead = true;
                _lastCulture = culture;
                _lastCultureNumber = Number(_cultureObjects, _cultures, culture);
            }
            var religion = group.Religion;
            if (!_religionRead || !ReferenceEquals(religion, _lastReligion))
            {
                _religionRead = true;
                _lastReligion = religion;
                _lastReligionNumber = Number(_religionObjects, _religions, religion);
            }
            var key = ((long)_lastCultureNumber << 32) | (uint)_lastReligionNumber;
            if (key == _lastKey) return _lastKind;
            if (!_kinds.TryGetValue(key, out var kind))
            {
                kind = _kinds.Count;
                _kinds[key] = kind;
                if (kind == _first.Length)
                {
                    Array.Resize(ref _first, kind * 2);
                    Array.Resize(ref _last, kind * 2);
                }
                _first[kind] = _last[kind] = -1;
            }
            _lastKey = key;
            return _lastKind = kind;
        }

        /// <summary>The first group of this kind in the destination, or -1.</summary>
        public int First(int kind) => _first[kind];

        /// <summary>The next group of the same kind after this one, or -1.</summary>
        public int Next(int group) => _next[group];

        /// <summary>A group of this kind joined the end of the destination's list.</summary>
        public void Append(int kind)
        {
            if (_count == _next.Length) Array.Resize(ref _next, _count * 2);
            _next[_count] = -1;
            if (_last[kind] < 0) _first[kind] = _count;
            else _next[_last[kind]] = _count;
            _last[kind] = _count;
            _count++;
        }

        // No name at all is a kind of its own, equal only to itself (string.Equals).
        private static int Number(Dictionary<string, int> byObject, Dictionary<string, int> bySpelling, string name)
        {
            if (name is null) return 0;
            if (byObject.TryGetValue(name, out var number)) return number;
            if (!bySpelling.TryGetValue(name, out number)) bySpelling[name] = number = bySpelling.Count + 1;
            byObject[name] = number;
            return number;
        }
    }

    [ThreadStatic] private static Arrivals _arrivals;

    private static void Arrive(ScenarioState to, PopulationGroup like, long people)
    {
        PopulationGroup sameFaithHost = null;
        var coalesceSmall = people <= 25 && to.PopulationGroups.Count >= 16;
        foreach (var group in to.PopulationGroups)
        {
            if (!string.Equals(group.Culture, like.Culture, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(group.Religion, like.Religion, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(group.Profession, like.Profession, StringComparison.OrdinalIgnoreCase) &&
                group.Class == like.Class)
            {
                group.Size += people;
                return;
            }
            if (coalesceSmall && (sameFaithHost is null || group.Size > sameFaithHost.Size))
                sameFaithHost = group;
        }
        if (sameFaithHost is not null)
        {
            sameFaithHost.Size += people;
            return;
        }
        to.PopulationGroups.Add(new PopulationGroup
        {
            Class = like.Class, Sector = like.Sector, Culture = like.Culture, Religion = like.Religion,
            Profession = like.Profession, Size = people, Employment = like.Employment, Wealth = like.Wealth,
            StandardOfLiving = to.StandardOfLiving, Radicalism = like.Radicalism
        });
    }

    private static void Arrive(ScenarioState to, PopulationGroup like, long people, Arrivals arrivals)
    {
        var groups = to.PopulationGroups;
        var kind = arrivals.KindOf(like);
        PopulationGroup sameFaithHost = null;
        var coalesceSmall = people <= 25 && groups.Count >= 16;
        for (var i = arrivals.First(kind); i >= 0; i = arrivals.Next(i))
        {
            var group = groups[i];
            if (string.Equals(group.Profession, like.Profession, StringComparison.OrdinalIgnoreCase) &&
                group.Class == like.Class)
            {
                group.Size += people;
                return;
            }
            if (coalesceSmall && (sameFaithHost is null || group.Size > sameFaithHost.Size))
                sameFaithHost = group;
        }
        if (sameFaithHost is not null)
        {
            sameFaithHost.Size += people;
            return;
        }
        groups.Add(new PopulationGroup
        {
            Class = like.Class, Sector = like.Sector, Culture = like.Culture, Religion = like.Religion,
            Profession = like.Profession, Size = people, Employment = like.Employment, Wealth = like.Wealth,
            StandardOfLiving = to.StandardOfLiving, Radicalism = like.Radicalism
        });
        arrivals.Append(kind);
    }
}
