using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation;

public static partial class MovementRules
{
    /// <summary>Its own peoples in a region, and the believers of its faiths who are not.</summary>
    // One index per campaign. It is rebuilt when province ownership moves, or when the
    // people in a region change. The population stamp is the demography signature taken
    // while the index is built: culture, faith and size. Not a second census, and not
    // the object identity of those strings. A later day with the same stamp reuses it.
    internal sealed class MovementPeopleCache
    {
        public int Day = -1;
        public int MapRevision = -1;
        public int OwnershipRevision = int.MinValue;
        public int States = -1;
        public int IndexBuilds;
        public readonly Dictionary<MovementDefinition, Dictionary<ScenarioState, (long Own, long Faithful)>> PeopleToday = new();
        public readonly Dictionary<string, List<PopulationGroupAt>> ByCulture = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<PopulationGroupAt>> ByReligion = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Dictionary<ScenarioState, long>> CultureTotals = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Stack<Dictionary<ScenarioState, (long Own, long Faithful)>> PeoplePool = new();
        internal readonly Stack<Dictionary<ScenarioState, long>> CultureTotalsPool = new();
        internal readonly Dictionary<string, List<CultureRegionBucket>> ByCultureRegions = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, List<ReligionCultureBucket>> ByReligionBuckets = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, long> RegionCultures = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<ReligionCultureKey, long> RegionRelCults = new();
        internal ScenarioState[] SortedStates;
        internal ScenarioState[] RawStatesSnapshot;
        internal Campaign SortedStatesCampaign;
        internal int SortedStatesRevision = int.MinValue;
        internal int SortedStatesCount = -1;
        internal bool StrictMapIndexOrder;
        internal ParallelStateSlot[] ParallelSlots = Array.Empty<ParallelStateSlot>();

        internal sealed class ParallelStateSlot
        {
            public bool NeedsMainThread;
            public ulong Sig;
            public int[] CultIds = Array.Empty<int>();
            public int CultCount;
            public long[] CultSums = Array.Empty<long>();
            public (int RelId, string RawCulture, long Size)[] RelCults = Array.Empty<(int, string, long)>();
            public int RelCultCount;
            public int[] NewRelIds = Array.Empty<int>();
            public int NewRelCount;
        }
        internal readonly Dictionary<string, (int Id, string Canonical)> CultureIdMap = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, (int Id, string Canonical)> CultureIdIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> CanonicalCultures = new();
        internal readonly List<List<CultureRegionBucket>> CultureBucketsById = new();
        internal readonly Dictionary<string, int> ReligionIdMap = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> ReligionIdIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<List<ReligionCultureBucket>> ReligionBucketsById = new();
        internal int[] CultureStamp = Array.Empty<int>();
        internal long[] CultureSum = Array.Empty<long>();
        internal readonly List<int> TouchedCultures = new();
        internal int CultureStampTick;
        internal readonly List<(int RelId, int CultId, string RawCulture, long Size)> RegionRelCultList = new();
        internal ulong[] StateDemographySigs = Array.Empty<ulong>();
        /// <summary>Today's stamps, taken side by side before they are compared; scratch, not saved.</summary>
        internal ulong[] FreshSigs = Array.Empty<ulong>();
        internal int[][] StateCultIds = Array.Empty<int[]>();
        internal int[] StateCultCounts = Array.Empty<int>();
        internal int[][] StateRelIds = Array.Empty<int[]>();
        internal int[] StateRelCounts = Array.Empty<int>();
        internal readonly List<int> DirtySlots = new();
        internal bool IncrementalReady;
        internal int[] NewCultStamp = Array.Empty<int>();
        internal int[] PatchCultStamp = Array.Empty<int>();
        internal int[] PatchCultHead = Array.Empty<int>();
        internal int[] PatchCultTail = Array.Empty<int>();
        internal readonly List<int> TouchedPatchCults = new();
        internal int[] NewRelStamp = Array.Empty<int>();
        internal int[] PatchRelStamp = Array.Empty<int>();
        internal int[] PatchRelHead = Array.Empty<int>();
        internal int[] PatchRelTail = Array.Empty<int>();
        internal readonly List<int> TouchedPatchRels = new();
        internal int PatchTick;
        internal readonly List<(int MapIndex, ScenarioState Region, long Size)> ScratchCultOps = new();
        internal readonly List<int> ScratchCultOpsNext = new();
        internal readonly List<(int MapIndex, ScenarioState Region, string RawCulture, long Size)> ScratchRelOps = new();
        internal readonly List<int> ScratchRelOpsNext = new();
        internal readonly List<int[]> PendingStateCultIds = new();
        internal readonly List<int[]> PendingStateRelIds = new();
        internal readonly List<ulong> PendingStateSigs = new();
        internal readonly List<int> ScratchNewRelIds = new();
        internal readonly List<CultureRegionBucket> ScratchCultBuckets = new();
        internal readonly List<ReligionCultureBucket> ScratchRelBuckets = new();
        // The culture and faith names by the string OBJECT that carries them. A late world has
        // two hundred thousand groups and a few hundred different names. Hashing the text of
        // two names per group was 12 ms of every day for the population stamp alone and most
        // of a 60 ms index build. So each different text has one row and one string object
        // that stands for it; a group met carrying an equal string of its own (a text save
        // gives every group its own) is handed that object instead (CanonicalRow), and from
        // then on its names are found by reference, hashed and resolved once. Row 0 is the
        // null name. RawKeys/RawKeyRows are an open-addressed table of those objects (RawSlot),
        // RawByText the same rows by text; the Raw* arrays are by row.
        internal string[] RawKeys = new string[256];
        internal int[] RawKeyRows = new int[256];
        internal readonly Dictionary<string, int> RawByText = new(StringComparer.Ordinal);
        internal int RawCount = 1;
        internal string[] RawStrings = new string[64];
        internal int[] RawHash = new int[64];
        internal int[] RawCultureId = Unresolved(64);
        internal int[] RawReligionId = Unresolved(64);
        /// <summary>Where each name stands among the others by ordinal comparison; equal text, equal rank. Current only while RawRanksStale is false.</summary>
        internal int[] RawRank = new int[64];
        internal bool RawRanksStale = true;
        // One region's (faith, culture) rows while its groups are read: the rows of one culture
        // are chained from CultureRowHead, so finding a group's row is a step or two.
        internal int[] CultureRowHead = Array.Empty<int>();
        internal int[] RowRel = new int[64];
        internal int[] RowCult = new int[64];
        internal string[] RowRaw = new string[64];
        internal long[] RowSize = new long[64];
        internal int[] RowNext = new int[64];
        internal int[] RowRank = new int[64];
        internal long[] RowKeys = new long[64];
        public bool Indexed;
        internal readonly object DirtyLock = new();
        internal ulong[] DirtyStateWords = new ulong[64];
        internal int DirtyMaxWord = -1;
        internal volatile bool DirtyWorldReset;
        internal bool StepDirtyPrimed;
        internal int TrackedClockSeq = -1;
        internal long[] StateHeaderPop = Array.Empty<long>();
        internal int[] StateHeaderGroupCount = Array.Empty<int>();

        internal int[] RawOrderScratch = Array.Empty<int>();
        internal readonly RawRowComparer RawRowOrderComparer = new();
        internal ulong CachedPopulationStamp;

        internal sealed class RawRowComparer : IComparer<int>
        {
            public string[] Strings;
            public int Compare(int a, int b) => string.CompareOrdinal(Strings[a] ?? "", Strings[b] ?? "");
        }

        internal static int[] Unresolved(int length)
        {
            var ids = new int[length];
            Array.Fill(ids, -1);
            return ids;
        }
    }

    internal readonly record struct PopulationGroupAt(ScenarioState Region, PopulationGroup Group);

    /// <summary>
    /// Set BDOF_NO_CANONICAL_PEOPLE_ORDER=1 to leave the people index in whatever order the
    /// scratch dictionaries produced. Only attribution runs use it: the canonical order is
    /// what makes the index independent of Dictionary internals - and of the thread count
    /// that builds it - so it is the default and the precondition for parallelising the build.
    /// </summary>
    private static readonly bool CanonicalPeopleOrder =
        System.Environment.GetEnvironmentVariable("BDOF_NO_CANONICAL_PEOPLE_ORDER") != "1";

    /// <summary>Remembered for the day.</summary>
    internal static long PeopleOf(Campaign campaign, ScenarioState region, MovementDefinition definition, out long faithful)
    {
        var regions = PeopleIn(campaign, definition);
        if (regions.TryGetValue(region, out var known))
        {
            faithful = known.Faithful;
            return known.Own;
        }
        faithful = 0;
        return 0;
    }

    /// <summary>Every region with any of a movement's peoples or believers in it, for the current day.</summary>
    internal static Dictionary<ScenarioState, (long Own, long Faithful)> PeopleIn(
        Campaign campaign, MovementDefinition definition)
    {
        EnsurePeopleCache(campaign);
        var cache = campaign.MovementPeople;
        if (!cache.PeopleToday.TryGetValue(definition, out var regions))
            cache.PeopleToday[definition] = regions = CountPeople(campaign, definition);
        return regions;
    }

    private static void ReturnTodayDictionaries(MovementPeopleCache cache)
    {
        if (cache.PeopleToday.Count > 0)
        {
            foreach (var regions in cache.PeopleToday.Values)
            {
                regions.Clear();
                cache.PeoplePool.Push(regions);
            }
            cache.PeopleToday.Clear();
        }
        if (cache.CultureTotals.Count > 0)
        {
            foreach (var d in cache.CultureTotals.Values) { d.Clear(); cache.CultureTotalsPool.Push(d); }
            cache.CultureTotals.Clear();
        }
    }

    private static IReadOnlyList<ScenarioState> OrderedStatesOf(MovementPeopleCache cache, Campaign campaign)
    {
        if (!CanonicalPeopleOrder)
        {
            cache.StrictMapIndexOrder = false;
            return campaign.States;
        }
        var states = campaign.States;
        var count = states.Count;
        var snap = cache.RawStatesSnapshot;
        if (cache.SortedStates is not null &&
            ReferenceEquals(cache.SortedStatesCampaign, campaign) &&
            cache.SortedStatesCount == count &&
            snap is not null && snap.Length == count)
        {
            var same = true;
            for (var i = 0; i < count; i++)
            {
                if (!ReferenceEquals(snap[i], states[i])) { same = false; break; }
            }
            if (same) return cache.SortedStates;
        }

        var rawSnap = snap is not null && snap.Length == count ? snap : new ScenarioState[count];
        var arr = new ScenarioState[count];
        for (var i = 0; i < count; i++)
        {
            var st = states[i];
            rawSnap[i] = st;
            arr[i] = st;
        }
        Array.Sort(arr, static (a, b) => a.MapIndex.CompareTo(b.MapIndex));
        var strict = true;
        for (var i = 1; i < arr.Length; i++)
        {
            if (arr[i].MapIndex <= arr[i - 1].MapIndex)
            {
                strict = false;
                break;
            }
        }
        cache.SortedStates = arr;
        cache.RawStatesSnapshot = rawSnap;
        cache.SortedStatesCampaign = campaign;
        cache.SortedStatesCount = count;
        cache.SortedStatesRevision = campaign.OwnershipRevision;
        cache.StrictMapIndexOrder = strict;
        cache.IncrementalReady = false;
        return cache.SortedStates;
    }

    /// <summary>Marks one region dirty for the incremental movement-people cache (thread-safe).</summary>
    public static void MarkPeopleStateDirty(Campaign campaign, ScenarioState state)
    {
        if (campaign is null || state is null) return;
        MarkPeopleStateDirty(campaign, state.MapIndex);
    }

    /// <summary>Marks one region by <see cref="ScenarioState.MapIndex"/> dirty for the incremental movement-people cache (thread-safe).</summary>
    public static void MarkPeopleStateDirty(Campaign campaign, int stateMapIndex)
    {
        if (campaign is null || stateMapIndex < 0) return;
        var cache = campaign.MovementPeople;
        var word = stateMapIndex >> 6;
        var bit = 1UL << (stateMapIndex & 63);
        var words = System.Threading.Volatile.Read(ref cache.DirtyStateWords);
        if ((uint)word >= (uint)words.Length)
        {
            lock (cache.DirtyLock)
            {
                words = cache.DirtyStateWords;
                if (word >= words.Length)
                {
                    var nextLen = words.Length;
                    while (nextLen <= word) nextLen *= 2;
                    Array.Resize(ref words, nextLen);
                    System.Threading.Volatile.Write(ref cache.DirtyStateWords, words);
                }
            }
        }
        System.Threading.Interlocked.Or(ref words[word], bit);
        while (true)
        {
            var cur = System.Threading.Volatile.Read(ref cache.DirtyMaxWord);
            if (word <= cur) break;
            if (System.Threading.Interlocked.CompareExchange(ref cache.DirtyMaxWord, word, cur) == cur) break;
        }
    }

    /// <summary>Forces a full people-cache verification sweep on the next read.</summary>
    public static void MarkPeopleWorldDirty(Campaign campaign)
    {
        if (campaign is null) return;
        campaign.MovementPeople.DirtyWorldReset = true;
    }

    private static bool IsDirtyStateBitSet(ulong[] words, int maxWord, int mapIndex)
    {
        if (mapIndex < 0 || maxWord < 0) return false;
        var word = mapIndex >> 6;
        if ((uint)word > (uint)maxWord || (uint)word >= (uint)words.Length) return false;
        return (words[word] & (1UL << (mapIndex & 63))) != 0UL;
    }

    private static void ClearDirtyStateBits(MovementPeopleCache cache)
    {
        var maxWord = System.Threading.Volatile.Read(ref cache.DirtyMaxWord);
        if (maxWord >= 0)
        {
            var words = System.Threading.Volatile.Read(ref cache.DirtyStateWords);
            var clearCount = Math.Min(words.Length, maxWord + 1);
            if (clearCount > 0) Array.Clear(words, 0, clearCount);
            System.Threading.Volatile.Write(ref cache.DirtyMaxWord, -1);
        }
        cache.DirtyWorldReset = false;
    }

    private static void EnsurePeopleCache(Campaign campaign)
    {
        var cache = campaign.MovementPeople;
        // Derived counts are never saved. A reloaded campaign builds them again from the
        // people it loaded. Within a run the index stays while the campaign, the ownership
        // revision and the population stamp are the ones it was built from.
        var day = campaign.ElapsedDays;
        var ownership = campaign.OwnershipRevision;
        if (cache.Indexed && cache.Day == day && cache.States == campaign.States.Count &&
            cache.OwnershipRevision == ownership)
            return;

        var previous = cache.SortedStates;
        var orderedStates = OrderedStatesOf(cache, campaign);
        var totalStates = orderedStates.Count;
        var dirtySlots = cache.DirtySlots;
        dirtySlots.Clear();
        // A new day is not itself a reason to throw the index away. A missing index or a
        // different region list is. Otherwise the stored signatures say whether anyone's
        // people changed. An ownership revision that still names these same regions, with
        // the same stamp, keeps the buckets: they do not store the owner.
        var canCompare = cache.Indexed && cache.States == totalStates && cache.StateDemographySigs.Length == totalStates;
        if (!canCompare)
        {
            ReturnTodayDictionaries(cache);
            ClearDirtyStateBits(cache);
            cache.StepDirtyPrimed = false;
            cache.TrackedClockSeq = campaign.SimulationTickClockSeq;
            cache.IncrementalReady = false;
            cache.Indexed = false;
        }
        else
        {
            var sameRegions = previous is not null && previous.Length == totalStates;
            if (sameRegions)
            {
                for (var i = 0; i < totalStates; i++)
                {
                    if (!ReferenceEquals(previous[i], orderedStates[i])) { sameRegions = false; break; }
                }
            }
            var useDirtyBits = cache.StepDirtyPrimed && !cache.DirtyWorldReset && sameRegions &&
                cache.TrackedClockSeq != campaign.SimulationTickClockSeq &&
                cache.StateHeaderPop.Length == totalStates &&
                cache.StateHeaderGroupCount.Length == totalStates;

            var dirtyWords = System.Threading.Volatile.Read(ref cache.DirtyStateWords);
            var dirtyMax = System.Threading.Volatile.Read(ref cache.DirtyMaxWord);

            if (useDirtyBits)
            {
                var headerPop = cache.StateHeaderPop;
                var headerGroups = cache.StateHeaderGroupCount;
                for (var i = 0; i < totalStates; i++)
                {
                    var st = orderedStates[i];
                    var dirty = st is not null && (IsDirtyStateBitSet(dirtyWords, dirtyMax, st.MapIndex) ||
                        st.Population != headerPop[i] || st.PopulationGroups.Count != headerGroups[i]);
                    if (!dirty) continue;
                    var sig = StateDemographySignature(cache, st);
                    if (sig != cache.StateDemographySigs[i]) dirtySlots.Add(i);
                    headerPop[i] = st.Population;
                    headerGroups[i] = st.PopulationGroups.Count;
                }
            }
            else
            {
                // Every region's stamp, side by side (DeterministicParallel): a stamp reads only that
                // region's people and the index's own table of names, which nothing writes while
                // the stamps are taken, and the regions that changed are then listed on this thread
                // in the same ascending order as before.
                var fresh = cache.FreshSigs.Length >= totalStates ? cache.FreshSigs : cache.FreshSigs = new ulong[totalStates];
                if (totalStates >= 256 && DeterministicParallel.Enabled(campaign))
                {
                    DeterministicParallel.ForRange(0, totalStates, (from, to) =>
                    {
                        for (var i = from; i < to; i++)
                            fresh[i] = StateDemographySignature(cache, orderedStates[i]);
                    }, minItems: 256);
                }
                else
                {
                    for (var i = 0; i < totalStates; i++)
                        fresh[i] = StateDemographySignature(cache, orderedStates[i]);
                }
                for (var i = 0; i < totalStates; i++)
                {
                    if (fresh[i] != cache.StateDemographySigs[i])
                        dirtySlots.Add(i);
                }
            }
            ClearDirtyStateBits(cache);
            cache.TrackedClockSeq = campaign.SimulationTickClockSeq;
            if (cache.OwnershipRevision != ownership && !sameRegions)
            {
                ReturnTodayDictionaries(cache);
                dirtySlots.Clear();
                cache.StepDirtyPrimed = false;
                cache.IncrementalReady = false;
                cache.Indexed = false;
            }
            else if (dirtySlots.Count > 0)
            {
                ReturnTodayDictionaries(cache);
                cache.Indexed = false;
                if (!CanonicalPeopleOrder || !cache.StrictMapIndexOrder || dirtySlots.Count > Math.Max(64, (totalStates * 3) / 4))
                    cache.IncrementalReady = false;
            }
            else
            {
                cache.OwnershipRevision = ownership;
                cache.IncrementalReady = cache.StrictMapIndexOrder && CanonicalPeopleOrder;
            }
        }
        cache.Day = day;
        cache.States = totalStates;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong sig, ulong value) => unchecked((sig ^ value) * 1099511628211UL);

    /// <summary>
    /// The population stamp for one region: who lives there, not which string object holds
    /// the name. Empty groups are shells the index already skips.
    /// </summary>
    private static ulong StateDemographySignature(MovementPeopleCache cache, ScenarioState state)
    {
        if (state is null) return 0UL;
        ulong sig = Mix(14695981039346656037UL, (ulong)(uint)state.MapIndex);
        sig = Mix(sig, (ulong)state.Population);
        var groups = CollectionsMarshal.AsSpan(state.PopulationGroups);
        // The text's own hash (0 for no name), as before - but for a name the index has
        // already met, taken once and read back by reference, and a group carrying the same
        // string as the group before it does not even reach the table. A string the index
        // has not met is hashed here as it always was; this reading changes nothing.
        string lastCulture = null, lastReligion = null;
        uint cultureHash = 0, religionHash = 0;
        var rawHash = cache.RawHash;
        for (var gi = 0; gi < groups.Length; gi++)
        {
            var g = groups[gi];
            if (g.Size <= 0) continue;
            if (!ReferenceEquals(g.Culture, lastCulture))
            {
                lastCulture = g.Culture;
                var row = FindRawRow(cache, lastCulture);
                cultureHash = (uint)(row >= 0 ? rawHash[row] : lastCulture.GetHashCode());
            }
            if (!ReferenceEquals(g.Religion, lastReligion))
            {
                lastReligion = g.Religion;
                var row = FindRawRow(cache, lastReligion);
                religionHash = (uint)(row >= 0 ? rawHash[row] : lastReligion.GetHashCode());
            }
            sig = Mix(sig, (ulong)g.Size);
            sig = Mix(sig, cultureHash);
            sig = Mix(sig, religionHash);
        }
        return sig;
    }

    /// <summary>
    /// Where a string object starts looking for itself in RawKeys: by its identity, not by
    /// its text - hashing the text is what this table exists to avoid, and the identity
    /// spreads evenly however alike the names are.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RawSlot(string value) => RuntimeHelpers.GetHashCode(value);

    /// <summary>The row of this very string object, or -1 if it is not the one that stands for its text. Null is row 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindRawRow(MovementPeopleCache cache, string value)
    {
        if (value is null) return 0;
        var keys = cache.RawKeys;
        var mask = keys.Length - 1;
        var at = RawSlot(value) & mask;
        while (true)
        {
            var key = keys[at];
            if (ReferenceEquals(key, value)) return cache.RawKeyRows[at];
            if (key is null) return -1;
            at = (at + 1) & mask;
        }
    }

    /// <summary>
    /// The row of this text, and <paramref name="value"/> made the string object that stands
    /// for it: the same characters, so nothing that reads the name can tell, and the next
    /// reading finds it by reference. A text met for the first time is given a row and stands
    /// for itself.
    /// </summary>
    private static int CanonicalRow(MovementPeopleCache cache, ref string value)
    {
        if (cache.RawByText.TryGetValue(value, out var met))
        {
            value = cache.RawStrings[met];
            return met;
        }
        var row = cache.RawCount++;
        cache.RawByText[value] = row;
        if (row == cache.RawStrings.Length)
        {
            var capacity = row * 2;
            Array.Resize(ref cache.RawStrings, capacity);
            Array.Resize(ref cache.RawHash, capacity);
            Array.Resize(ref cache.RawRank, capacity);
            Array.Resize(ref cache.RawCultureId, capacity);
            Array.Resize(ref cache.RawReligionId, capacity);
            Array.Fill(cache.RawCultureId, -1, row, capacity - row);
            Array.Fill(cache.RawReligionId, -1, row, capacity - row);
        }
        cache.RawStrings[row] = value;
        cache.RawHash[row] = value.GetHashCode();
        cache.RawCultureId[row] = -1;
        cache.RawReligionId[row] = -1;
        cache.RawRanksStale = true;
        if (cache.RawCount * 2 > cache.RawKeys.Length)
        {
            // Half full: twice the table, every row put back.
            cache.RawKeys = new string[cache.RawKeys.Length * 2];
            cache.RawKeyRows = new int[cache.RawKeys.Length];
            for (var known = 1; known < row; known++) PlaceRawRow(cache, cache.RawStrings[known], known);
        }
        PlaceRawRow(cache, value, row);
        return row;
    }

    private static void PlaceRawRow(MovementPeopleCache cache, string value, int row)
    {
        var keys = cache.RawKeys;
        var mask = keys.Length - 1;
        var at = RawSlot(value) & mask;
        while (keys[at] is not null) at = (at + 1) & mask;
        keys[at] = value;
        cache.RawKeyRows[at] = row;
    }

    /// <summary>
    /// Every remembered name's place by ordinal comparison, the null name standing as the
    /// empty one. A region's rows are then put in order by comparing integers instead of text.
    /// </summary>
    private static void RankRawRows(MovementPeopleCache cache)
    {
        var count = cache.RawCount;
        var strings = cache.RawStrings;
        var order = cache.RawOrderScratch;
        if (order.Length < count) cache.RawOrderScratch = order = new int[Math.Max(count, 64)];
        for (var row = 0; row < count; row++) order[row] = row;
        var comparer = cache.RawRowOrderComparer;
        comparer.Strings = strings;
        Array.Sort(order, 0, count, comparer);
        var rank = 0;
        for (var k = 0; k < count; k++)
        {
            if (k > 0 && string.CompareOrdinal(strings[order[k - 1]] ?? "", strings[order[k]] ?? "") != 0) rank++;
            cache.RawRank[order[k]] = rank;
        }
        cache.RawRanksStale = false;
    }

    private static (int Id, string Canonical) ResolveCulture(MovementPeopleCache cache, string raw)
    {
        if (cache.CultureIdMap.TryGetValue(raw, out var entry)) return entry;
        if (!cache.CultureIdIgnoreCase.TryGetValue(raw, out entry))
        {
            var id = cache.CanonicalCultures.Count;
            entry = (id, raw);
            cache.CanonicalCultures.Add(raw);
            cache.CultureIdIgnoreCase[raw] = entry;
            var list = new List<CultureRegionBucket>();
            cache.ByCultureRegions[raw] = list;
            cache.CultureBucketsById.Add(list);
            if (cache.CultureStamp.Length < cache.CanonicalCultures.Count)
            {
                var oldLen = cache.CultureStamp.Length;
                var nextCap = Math.Max(64, cache.CanonicalCultures.Count * 2);
                Array.Resize(ref cache.CultureStamp, nextCap);
                Array.Resize(ref cache.CultureSum, nextCap);
                Array.Resize(ref cache.PatchCultStamp, nextCap);
                Array.Resize(ref cache.PatchCultHead, nextCap);
                Array.Resize(ref cache.PatchCultTail, nextCap);
                Array.Resize(ref cache.CultureRowHead, nextCap);
                Array.Fill(cache.PatchCultHead, -1, oldLen, nextCap - oldLen);
            }
        }
        cache.CultureIdMap[raw] = entry;
        return entry;
    }

    private static int ResolveReligion(MovementPeopleCache cache, string raw)
    {
        if (cache.ReligionIdMap.TryGetValue(raw, out var id)) return id;
        if (!cache.ReligionIdIgnoreCase.TryGetValue(raw, out id))
        {
            id = cache.ReligionBucketsById.Count;
            cache.ReligionIdIgnoreCase[raw] = id;
            var list = new List<ReligionCultureBucket>();
            cache.ByReligionBuckets[raw] = list;
            cache.ReligionBucketsById.Add(list);
            if (cache.NewRelStamp.Length < cache.ReligionBucketsById.Count)
            {
                var oldLen = cache.NewRelStamp.Length;
                var nextCap = Math.Max(64, cache.ReligionBucketsById.Count * 2);
                Array.Resize(ref cache.NewRelStamp, nextCap);
                Array.Resize(ref cache.PatchRelStamp, nextCap);
                Array.Resize(ref cache.PatchRelHead, nextCap);
                Array.Resize(ref cache.PatchRelTail, nextCap);
                Array.Fill(cache.PatchRelHead, -1, oldLen, nextCap - oldLen);
            }
        }
        cache.ReligionIdMap[raw] = id;
        return id;
    }

    private static Dictionary<ScenarioState, long> CultureTotalsOf(Campaign campaign, string culture)
    {
        var cache = campaign.MovementPeople;
        if (cache.CultureTotals.TryGetValue(culture, out var totals)) return totals;
        totals = cache.CultureTotalsPool.Count > 0 ? cache.CultureTotalsPool.Pop() : new Dictionary<ScenarioState, long>();
        if (cache.ByCulture.TryGetValue(culture, out var groups))
        {
            var span = CollectionsMarshal.AsSpan(groups);
            for (var i = 0; i < span.Length; i++)
            {
                var (region, group) = span[i];
                totals[region] = totals.GetValueOrDefault(region) + group.Size;
            }
        }
        cache.CultureTotals[culture] = totals;
        return totals;
    }

    private static Dictionary<ScenarioState, (long Own, long Faithful)> CountPeople(Campaign campaign, MovementDefinition definition)
    {
        var cache = campaign.MovementPeople;
        if (!cache.Indexed)
            foreach (var _ in EnsurePeopleIndexedStages(campaign, insideMovRules: false)) { }
        using var countScope = SimProfiler.Measure("        mov:people:count");
        // A finished census no longer belongs to a movement. Reuse its capacity even
        // if that movement dissolves or changes its definition before the next census.
        if (!cache.PeoplePool.TryPop(out var counted))
            counted = new Dictionary<ScenarioState, (long Own, long Faithful)>();
        var peoples = definition.DistinctPeoples;
        for (var pi = 0; pi < peoples.Length; pi++)
            if (cache.ByCultureRegions.TryGetValue(peoples[pi], out var regions))
            {
                var span = CollectionsMarshal.AsSpan(regions);
                for (var i = 0; i < span.Length; i++)
                {
                    var bucket = span[i];
                    ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(counted, bucket.Region, out _);
                    entry.Own += bucket.Size;
                }
            }
        var faiths = definition.DistinctFaiths;
        if (faiths.Length > 0)
        {
            var hasPeoples = peoples.Length > 0;
            for (var fi = 0; fi < faiths.Length; fi++)
                if (cache.ByReligionBuckets.TryGetValue(faiths[fi], out var buckets))
                {
                    var span = CollectionsMarshal.AsSpan(buckets);
                    for (var i = 0; i < span.Length; i++)
                    {
                        var bucket = span[i];
                        if (hasPeoples && definition.HasPeople(bucket.Culture)) continue;
                        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(counted, bucket.Region, out _);
                        entry.Faithful += bucket.Size;
                    }
                }
        }
        return counted;
    }

    internal static long PeopleOf(ScenarioState region, MovementDefinition definition, out long faithful)
    {
        long own = 0;
        faithful = 0;
        var groups = CollectionsMarshal.AsSpan(region.PopulationGroups);
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            if (group.Size <= 0) continue;
            if (definition.HasPeople(group.Culture)) own += group.Size;
            else if (definition.HasFaith(group.Religion)) faithful += group.Size;
        }
        return own;
    }
}

internal readonly record struct CultureRegionBucket(ScenarioState Region, long Size);

internal readonly record struct ReligionCultureBucket(ScenarioState Region, string Culture, long Size);

internal readonly struct ReligionCultureKey : IEquatable<ReligionCultureKey>
{
    public readonly string Religion;
    public readonly string Culture;

    public ReligionCultureKey(string religion, string culture)
    {
        Religion = religion;
        Culture = culture;
    }

    public bool Equals(ReligionCultureKey other) =>
        string.Equals(Religion, other.Religion, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Culture, other.Culture, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object obj) => obj is ReligionCultureKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(Religion),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Culture));
}
