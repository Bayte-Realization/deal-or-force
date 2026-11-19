using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// Whether a region's slivers are folded into their nearest kin at the weekly demography
    /// step (PopulationFolding). Off - or the whole diet off - and the table is as it was.
    ///
    /// Shipped OFF (16 November 2026). Folding changes the world - people are counted under
    /// their nearest kin - and a change of that kind is the owner's to switch on with the
    /// numbers in front of him; the pass that wrote it was cut short before it was reviewed.
    /// </summary>
    public bool FoldSliverGroups { get; set; } = true;

    /// <summary>A trade's sliver: at most this many people ...</summary>
    public int FoldTradeSliverPeople { get; set; } = 2500;

    /// <summary>... and at most this share of the region. It joins another trade of the same people and faith.</summary>
    public float FoldTradeSliverShare { get; set; } = 0.002f;

    /// <summary>
    /// A people's (or a faith's) last sliver in a region: at most this many of them there ...
    /// A hundred, which is dust: migrants arrive a few hundred at a time, and a threshold above
    /// what a week brings folds every week's arrivals before they can become a community - at
    /// 500 a world from 2024 counted 2.4 million people a year under another people this way,
    /// at 100 it is a fifth of that, and the table is only a tenth larger for it.
    /// </summary>
    public int FoldPeopleSliverPeople { get; set; } = 100;

    /// <summary>... and at most this share of the region. It joins its own people of another faith, or failing that another people of its faith.</summary>
    public float FoldPeopleSliverShare { get; set; } = 0.0003f;

    /// <summary>
    /// How often a region's last slivers of a faith and of a people are looked at, in weeks; each
    /// region on a week of its own. A year: a sliver is folded for having stayed a sliver, not
    /// for having arrived this week - two newcomers a week are a community of a hundred before
    /// their region's turn comes round. Trades are folded every week whatever this says. 1:
    /// every week.
    /// </summary>
    public int FoldPeopleEveryWeeks { get; set; } = 52;

    /// <summary>... and, to be folded into another people, at most this share of its own people in the world.</summary>
    public float FoldPeopleSliverOfPeople { get; set; } = 0.0005f;

    /// <summary>A people with fewer than this many in the whole world is never folded into another, anywhere.</summary>
    public long FoldKeepCulturePeople { get; set; } = 250_000;

    /// <summary>A region with fewer groups than this is left alone: there is nothing to save.</summary>
    public int FoldMinRegionGroups { get; set; } = 24;
}

public sealed partial class Campaign
{
    /// <summary>
    /// What folding has done since this campaign was made or loaded: groups gone, and the people
    /// whose trade, whose faith and whose people the table now counts differently. A gauge for
    /// the bench and the tests - not saved, not part of the world.
    /// </summary>
    public long FoldedGroups, FoldedTradePeople, FoldedFaithPeople, FoldedCulturePeople;
}

/// <summary>
/// Slivers folded: the table of who lives where must not grow for ever.
///
/// A region's people are counted by people, faith, trade and class, and every migrant arrives
/// as who he is. That is right for one migration and wrong for a century of them: after 107
/// years a save held 268,313 groups in 2,113 regions - Jiangsu alone 1,589, of 304 peoples and
/// 17 faiths in ten trades - and 116,204 of those groups were under a thousand people, 0.42 %
/// of mankind between them. Every weekly pass over the people of the world (births and deaths,
/// migration, the damage of war, the movements' count of their own) walked each of them.
///
/// So once a week, where the cohorts move (DemographicCycle), a region's slivers join their
/// nearest kin, in this order - the first every week, the other two one week in
/// FoldPeopleEveryWeeks, a year as shipped, so that what is folded is what has stayed a sliver
/// and not whoever arrived this week:
///
///   1. A TRADE's sliver - a group small in number and in share of its region - joins another
///      trade of the same people and the same faith there: the same class if there is one that
///      is no sliver, otherwise the largest. Nobody changes people or faith.
///   2. A FAITH's sliver - everyone of one people and one faith in the region, when that is
///      small - joins the same people's largest faith there. They keep their people.
///   3. A PEOPLE's sliver - everyone of one people in the region, when even that is small -
///      joins the largest people of its faith there, or failing that the largest people. This
///      is assimilation's last step taken at once: the rate (DemographicCycle.Assimilate)
///      rounds to nobody for a group of a few hundred, so the last of a minority never left.
///
/// What is never folded into another people: the people the region's owner is for or declares
/// itself of; the people of any country, living or fallen, whose homeland the region is; the
/// people of a living movement that has support, fighters or its headquarters there; a people with
/// fewer than FoldKeepCulturePeople in the whole world; a sliver that is more than
/// FoldPeopleSliverOfPeople of its whole people (small beside its region is not enough - it
/// must be small beside its own people too, or a scattered people would be folded away piece
/// by piece); and any people in the region where its largest group in the world lives - so no
/// people can leave the world this way.
///
/// People are conserved exactly: a merge adds one count to another. What the merged carried -
/// employment, wealth, living, anger - goes in weighted by people. The whole of it reads one
/// region and writes that region, in the region's own order, with ties broken by position, so
/// regions can be worked side by side and two machines agree to the bit.
/// </summary>
public static class PopulationFolding
{
    /// <summary>What a cycle's regions need to know about the world before any of them is folded.</summary>
    internal sealed class Shield
    {
        public int Day = -1;
        public int Regions = -1;
        /// <summary>People of each culture in the world, and the map index of the region its largest group lives in.</summary>
        public readonly Dictionary<string, (long People, long Largest, int Anchor)> World = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The peoples of living movements that stand in a region, by map index.</summary>
        public readonly Dictionary<int, List<MovementDefinition>> Movements = new();
        public readonly List<List<MovementDefinition>> Spare = new();
        /// <summary>The countries - living or fallen - a region is the homeland of, by map index.</summary>
        public readonly Dictionary<int, List<Nation>> Homes = new();
        public readonly List<List<Nation>> SpareHomes = new();
    }

    // Per campaign: two worlds in one process must not share a day's answers (the tests run several).
    private static readonly ConditionalWeakTable<Campaign, Shield> Shields = new();

    /// <summary>Whether folding runs in this campaign at all.</summary>
    public static bool Enabled(Campaign campaign)
    {
        var defines = campaign.Defines;
        return defines.LongCampaignEnabled && defines.FoldSliverGroups &&
               (TradeCap(defines) > 0 || PeopleCap(defines) > 0);
    }

    private static long TradeCap(Defines d) => d.FoldTradeSliverShare > 0f ? Math.Max(0, d.FoldTradeSliverPeople) : 0;
    private static long PeopleCap(Defines d) => d.FoldPeopleSliverShare > 0f ? Math.Max(0, d.FoldPeopleSliverPeople) : 0;

    /// <summary>
    /// The world as the cycle finds it, read once on the stage's own thread before its regions are
    /// worked; the workers only read it. Built again when the day has moved, so every region of
    /// one week's cycle is judged against the same world whichever stage it falls in.
    /// </summary>
    internal static Shield Prepare(Campaign campaign)
    {
        var shield = Shields.GetValue(campaign, static _ => new Shield());
        if (shield.Day == campaign.ElapsedDays && shield.Regions == campaign.States.Count) return shield;
        shield.Day = campaign.ElapsedDays;
        shield.Regions = campaign.States.Count;
        shield.World.Clear();
        for (var index = 0; index < campaign.States.Count; index++)
        {
            var groups = campaign.States[index]?.PopulationGroups;
            if (groups is null) continue;
            // Neighbouring groups hold the same name object: a run is added up before the table
            // is asked (the same trick as DemographicCycle.Assimilate).
            string run = null;
            long people = 0, largest = 0;
            for (var g = 0; g <= groups.Count; g++)
            {
                var group = g < groups.Count ? groups[g] : null;
                if (group is not null && (group.Size <= 0 || group.Culture is null)) continue;
                if (group is not null && ReferenceEquals(group.Culture, run))
                {
                    people += group.Size;
                    if (group.Size > largest) largest = group.Size;
                    continue;
                }
                if (run is not null)
                {
                    ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(shield.World, run, out var known);
                    if (!known) entry.Anchor = -1;
                    entry.People += people;
                    // The first region to hold the largest group keeps the anchor: regions are
                    // walked in the campaign's order, so this is the same on every machine. The
                    // anchor is the region's map index, because that is what FoldRegion holds it
                    // against; the place in this list is another number, and with it the
                    // exemption fell on whichever region happened to carry that number.
                    if (largest > entry.Largest) { entry.Largest = largest; entry.Anchor = campaign.States[index].MapIndex; }
                }
                if (group is null) break;
                run = group.Culture;
                people = largest = group.Size;
            }
        }
        foreach (var list in shield.Movements.Values) { list.Clear(); shield.Spare.Add(list); }
        shield.Movements.Clear();
        foreach (var nation in campaign.ActiveNations)
        {
            var movement = nation.Movement;
            if (movement is null || movement.Dissolved) continue;
            var definition = Movements.DefinitionOf(nation);
            if (definition is null || definition.Peoples.Count == 0) continue;
            foreach (var region in movement.Support.Keys) Stand(shield, region, definition);
            foreach (var region in movement.Infiltrated.Keys) Stand(shield, region, definition);
            Stand(shield, movement.HeadquartersStateMapIndex, definition);
        }
        // Whose homeland each region is. A fallen country's people in its own homeland are what
        // it comes back to (FormableRestorations, MovementFall): a retired record counts here as
        // much as a living country, which is why this reads the whole list and not the walked one.
        foreach (var list in shield.Homes.Values) { list.Clear(); shield.SpareHomes.Add(list); }
        shield.Homes.Clear();
        foreach (var (id, regions) in campaign.Homelands)
        {
            if (regions is null || regions.Count == 0) continue;
            var nation = campaign.NationById(id);
            if (nation is null || (DemographyCatalog.PrimarySet(nation.Tag) is null && nation.DeclaredCulture.Length == 0)) continue;
            foreach (var region in regions)
            {
                if (!shield.Homes.TryGetValue(region, out var list))
                {
                    if (shield.SpareHomes.Count > 0) { list = shield.SpareHomes[^1]; shield.SpareHomes.RemoveAt(shield.SpareHomes.Count - 1); }
                    else list = new List<Nation>(2);
                    shield.Homes[region] = list;
                }
                if (!list.Contains(nation)) list.Add(nation);
            }
        }
        return shield;
    }

    /// <summary>Whether a people is one this country is for: its own by the catalogue or by its founding, or the one it declares.</summary>
    private static bool Own(Nation nation, string people)
    {
        if (nation is null) return false;
        if (nation.DeclaredCulture.Length > 0 && string.Equals(nation.DeclaredCulture, people, StringComparison.OrdinalIgnoreCase)) return true;
        return DemographyCatalog.PrimarySet(nation.Tag) is { } primary && primary.Contains(people);
    }

    private static void Stand(Shield shield, int region, MovementDefinition definition)
    {
        if (!shield.Movements.TryGetValue(region, out var list))
        {
            if (shield.Spare.Count > 0) { list = shield.Spare[^1]; shield.Spare.RemoveAt(shield.Spare.Count - 1); }
            else list = new List<MovementDefinition>(2);
            shield.Movements[region] = list;
        }
        if (list.Contains(definition)) return;
        // Asked once here so its lookup is built on this thread: the workers only read it.
        definition.HasPeople("");
        list.Add(definition);
    }

    /// <summary>One thread's scratch: the region's groups numbered by people, faith and the pair of them.</summary>
    private sealed class Scratch
    {
        public readonly Dictionary<string, int> Cultures = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Faiths = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<long, int> Pairs = new();
        public readonly List<string> CultureNames = new();
        // By group: its size as the region came in, its pair, the next group of the same pair.
        public long[] Size;
        public int[] Pair, Next;
        // By pair: its people, its faith, its first and last group, its largest group, its total.
        public int[] PairCulture, PairFaith, PairFirst, PairLast, PairBest;
        public long[] PairTotal;
        // By people: its total here, its largest pair, whether it stays. By faith: where its leavers go.
        public long[] CultureTotal;
        public int[] CultureBest, FaithBest;
        public bool[] CultureKept;

        /// <summary>There are never more pairs, peoples or faiths than groups, so one length serves all.</summary>
        public void Room(int groups)
        {
            if (Size is not null && Size.Length >= groups) return;
            var size = Math.Max(256, Math.Max(groups, (Size?.Length ?? 0) * 2));
            Size = new long[size]; Pair = new int[size]; Next = new int[size];
            PairCulture = new int[size]; PairFaith = new int[size]; PairFirst = new int[size];
            PairLast = new int[size]; PairBest = new int[size]; PairTotal = new long[size];
            CultureTotal = new long[size]; CultureBest = new int[size]; FaithBest = new int[size];
            CultureKept = new bool[size];
        }
    }

    // One per thread: regions are worked side by side (DemographicCycle.Stages).
    [ThreadStatic] private static Scratch _scratch;

    /// <summary>
    /// Every region now, in the campaign's order, and every kind of sliver whatever week it is:
    /// what the weekly cycle does a stage at a time and a year round, for a test and for the
    /// bench. Returns how many groups went.
    /// </summary>
    public static int FoldWorld(Campaign campaign)
    {
        if (!Enabled(campaign)) return 0;
        campaign.NationsByMapIndex();
        var shield = Prepare(campaign);
        var before = campaign.FoldedGroups;
        foreach (var state in campaign.States)
        {
            if (state is null || state.Unclaimed || state.Population <= 0) continue;
            if (FoldRegion(campaign, state, shield)) state.PopulationGroups.RemoveAll(static group => group.Size <= 0);
        }
        return (int)(campaign.FoldedGroups - before);
    }

    /// <summary>
    /// One region's slivers folded. Emptied groups are left in the list with nobody in them for
    /// the caller to drop (DemographicCycle.MoveCultures does that anyway). True if any group was.
    /// </summary>
    /// <summary>
    /// Whether this is the week a region's slivers of a faith and of a people are looked at
    /// (Defines.FoldPeopleEveryWeeks): one week in so many, and a different one for each region,
    /// so the year's folding is not all on one day.
    /// </summary>
    internal static bool PeoplesTurn(Campaign campaign, ScenarioState state)
    {
        var every = Math.Max(1, campaign.Defines.FoldPeopleEveryWeeks);
        if (every == 1) return true;
        var week = campaign.ElapsedDays / Math.Max(1, campaign.Defines.EconomyTickDays);
        return ((week + state.MapIndex) % every + every) % every == 0;
    }

    /// <param name="peoplesToo">False in a week that is not this region's turn: only trades are folded.</param>
    internal static bool FoldRegion(Campaign campaign, ScenarioState state, Shield shield, bool peoplesToo = true)
    {
        var defines = campaign.Defines;
        var groups = state.PopulationGroups;
        var count = groups.Count;
        if (count < Math.Max(2, defines.FoldMinRegionGroups)) return false;

        long population = 0, smallest = long.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var size = groups[i].Size;
            if (size <= 0) continue;
            population += size;
            if (size < smallest) smallest = size;
        }
        if (population <= 0) return false;
        // Both halves of a threshold must hold: few in number and a small share of this region.
        var tradeLimit = Math.Min(TradeCap(defines), (long)(defines.FoldTradeSliverShare * (double)population));
        var peopleLimit = peoplesToo ? Math.Min(PeopleCap(defines), (long)(defines.FoldPeopleSliverShare * (double)population)) : 0;
        if (smallest > tradeLimit && smallest > peopleLimit) return false;

        var s = _scratch ??= new Scratch();
        s.Room(count);
        s.Cultures.Clear(); s.Faiths.Clear(); s.Pairs.Clear(); s.CultureNames.Clear();
        var pairs = 0;
        string lastCulture = null, lastFaith = null;
        int cultureId = -1, faithId = -1;
        for (var i = 0; i < count; i++)
        {
            var group = groups[i];
            s.Size[i] = group.Size;
            s.Next[i] = -1;
            if (group.Size <= 0) { s.Pair[i] = -1; continue; }
            if (cultureId < 0 || !ReferenceEquals(group.Culture, lastCulture))
            {
                lastCulture = group.Culture;
                var name = lastCulture ?? "";
                if (!s.Cultures.TryGetValue(name, out cultureId))
                {
                    s.Cultures[name] = cultureId = s.Cultures.Count;
                    s.CultureNames.Add(name);
                    s.CultureTotal[cultureId] = 0;
                    s.CultureBest[cultureId] = -1;
                }
            }
            if (faithId < 0 || !ReferenceEquals(group.Religion, lastFaith))
            {
                lastFaith = group.Religion;
                var name = lastFaith ?? "";
                if (!s.Faiths.TryGetValue(name, out faithId)) s.Faiths[name] = faithId = s.Faiths.Count;
            }
            var key = ((long)cultureId << 20) | (uint)faithId;
            if (!s.Pairs.TryGetValue(key, out var pair))
            {
                s.Pairs[key] = pair = pairs++;
                s.PairCulture[pair] = cultureId;
                s.PairFaith[pair] = faithId;
                s.PairFirst[pair] = i;
                s.PairBest[pair] = i;
                s.PairTotal[pair] = 0;
            }
            else s.Next[s.PairLast[pair]] = i;
            s.PairLast[pair] = i;
            s.Pair[i] = pair;
            s.PairTotal[pair] += group.Size;
            // The largest of the pair; of two the same size, the one that stands first.
            if (group.Size > s.Size[s.PairBest[pair]]) s.PairBest[pair] = i;
            s.CultureTotal[cultureId] += group.Size;
        }

        var folded = 0;
        long tradePeople = 0, faithPeople = 0, culturePeople = 0;

        // 1. Trades. Judged by the sizes the region came in with, so the answer does not depend
        // on which sliver was met first; the largest of a pair never goes.
        if (tradeLimit > 0 && smallest <= tradeLimit)
        {
            for (var i = 0; i < count; i++)
            {
                var size = s.Size[i];
                if (size <= 0 || size > tradeLimit) continue;
                var pair = s.Pair[i];
                var best = s.PairBest[pair];
                if (best == i) continue;
                var mine = groups[i];
                // Among those that are no sliver themselves: the largest of the same class in the
                // same sector, failing that of the same class, failing that the largest of all.
                // Ties go to the group that stands first.
                var host = -1;
                var sameSector = -1;
                for (var j = s.PairFirst[pair]; j >= 0; j = s.Next[j])
                {
                    if (j == i || s.Size[j] <= tradeLimit || groups[j].Class != mine.Class) continue;
                    if (host < 0 || s.Size[j] > s.Size[host]) host = j;
                    if (groups[j].Sector == mine.Sector && (sameSector < 0 || s.Size[j] > s.Size[sameSector])) sameSector = j;
                }
                if (sameSector >= 0) host = sameSector;
                if (host < 0) host = best;
                Merge(groups[host], mine);
                tradePeople += size;
                folded++;
            }
        }

        // 2 and 3. A faith's last sliver of a people here, then a people's.
        if (peopleLimit > 0)
        {
            var any = false;
            for (var pair = 0; pair < pairs && !any; pair++) any = s.PairTotal[pair] <= peopleLimit;
            if (any)
            {
                // The largest pair of each people: where its faith-slivers go.
                for (var pair = 0; pair < pairs; pair++)
                {
                    var culture = s.PairCulture[pair];
                    var best = s.CultureBest[culture];
                    if (best < 0 || s.PairTotal[pair] > s.PairTotal[best]) s.CultureBest[culture] = pair;
                }
                for (var pair = 0; pair < pairs; pair++)
                {
                    if (s.PairTotal[pair] > peopleLimit) continue;
                    var into = s.CultureBest[s.PairCulture[pair]];
                    if (into == pair) continue;
                    var moved = s.PairTotal[pair];
                    folded += Pour(groups, s, pair, into);
                    s.PairTotal[into] += moved;
                    s.PairTotal[pair] = 0;
                    faithPeople += moved;
                }

                // Which peoples stay: the large, and those somebody here is for.
                var owner = campaign.NationsByMapIndex().GetValueOrDefault(state.NationMapIndex);
                shield.Movements.TryGetValue(state.MapIndex, out var movements);
                shield.Homes.TryGetValue(state.MapIndex, out var homes);
                var keep = Math.Max(0, defines.FoldKeepCulturePeople);
                var ofPeople = Math.Clamp(defines.FoldPeopleSliverOfPeople, 0f, 1f);
                var cultures = s.CultureNames.Count;
                var leaving = false;
                for (var culture = 0; culture < cultures; culture++)
                {
                    var kept = s.CultureTotal[culture] > peopleLimit;
                    if (!kept)
                    {
                        var name = s.CultureNames[culture];
                        kept = name.Length == 0 || Own(owner, name) ||
                               !shield.World.TryGetValue(name, out var world) || world.People < keep ||
                               world.Anchor == state.MapIndex || world.Anchor < 0 ||
                               s.CultureTotal[culture] > (long)(world.People * (double)ofPeople);
                        if (!kept && movements is not null)
                            for (var m = 0; m < movements.Count && !kept; m++) kept = movements[m].HasPeople(name);
                        if (!kept && homes is not null)
                            for (var h = 0; h < homes.Count && !kept; h++) kept = Own(homes[h], name);
                    }
                    s.CultureKept[culture] = kept;
                    if (!kept) leaving = true;
                }
                if (leaving)
                {
                    // Where a people that leaves goes: the largest pair of its faith among those
                    // that stay, or the largest pair of all. Chosen before anyone is poured, so
                    // the order of the pouring cannot change it.
                    var faiths = s.Faiths.Count;
                    for (var faith = 0; faith < faiths; faith++) s.FaithBest[faith] = -1;
                    var largest = -1;
                    for (var pair = 0; pair < pairs; pair++)
                    {
                        if (s.PairTotal[pair] <= 0 || !s.CultureKept[s.PairCulture[pair]]) continue;
                        var faith = s.PairFaith[pair];
                        if (s.FaithBest[faith] < 0 || s.PairTotal[pair] > s.PairTotal[s.FaithBest[faith]]) s.FaithBest[faith] = pair;
                        if (largest < 0 || s.PairTotal[pair] > s.PairTotal[largest]) largest = pair;
                    }
                    if (largest >= 0)
                    {
                        for (var culture = 0; culture < cultures; culture++)
                        {
                            if (s.CultureKept[culture]) continue;
                            // After step 2 the whole of it stands in its largest pair.
                            var pair = s.CultureBest[culture];
                            if (pair < 0 || s.PairTotal[pair] <= 0) continue;
                            var into = s.FaithBest[s.PairFaith[pair]];
                            if (into < 0) into = largest;
                            var moved = s.PairTotal[pair];
                            folded += Pour(groups, s, pair, into);
                            culturePeople += moved;
                        }
                    }
                }
            }
        }

        if (folded == 0) return false;
        System.Threading.Interlocked.Add(ref campaign.FoldedGroups, folded);
        System.Threading.Interlocked.Add(ref campaign.FoldedTradePeople, tradePeople);
        System.Threading.Interlocked.Add(ref campaign.FoldedFaithPeople, faithPeople);
        System.Threading.Interlocked.Add(ref campaign.FoldedCulturePeople, culturePeople);
        return true;
    }

    /// <summary>
    /// Everyone of one pair into another: each group to the same trade and class there if it
    /// still has anybody, otherwise the same class, otherwise the pair's largest group.
    /// </summary>
    private static int Pour(List<PopulationGroup> groups, Scratch s, int from, int into)
    {
        var poured = 0;
        for (var i = s.PairFirst[from]; i >= 0; i = s.Next[i])
        {
            var mine = groups[i];
            if (mine.Size <= 0) continue;
            var host = -1;
            var sameClass = -1;
            for (var j = s.PairFirst[into]; j >= 0; j = s.Next[j])
            {
                var other = groups[j];
                if (other.Size <= 0 || other.Class != mine.Class) continue;
                if (string.Equals(other.Profession, mine.Profession, StringComparison.OrdinalIgnoreCase)) { host = j; break; }
                if (sameClass < 0 || other.Size > groups[sameClass].Size) sameClass = j;
            }
            if (host < 0) host = sameClass;
            if (host < 0) host = s.PairBest[into];
            Merge(groups[host], mine);
            poured++;
        }
        return poured;
    }

    /// <summary>One count added to another, and what they carried weighted by how many carried it.</summary>
    private static void Merge(PopulationGroup host, PopulationGroup gone)
    {
        var total = host.Size + gone.Size;
        if (total > 0)
        {
            var share = gone.Size / (double)total;
            host.Employment = (float)(host.Employment + (gone.Employment - host.Employment) * share);
            host.Wealth = (float)(host.Wealth + (gone.Wealth - host.Wealth) * share);
            host.StandardOfLiving = (float)(host.StandardOfLiving + (gone.StandardOfLiving - host.StandardOfLiving) * share);
            host.Radicalism = (float)(host.Radicalism + (gone.Radicalism - host.Radicalism) * share);
        }
        host.Size = total;
        gone.Size = 0;
    }
}
