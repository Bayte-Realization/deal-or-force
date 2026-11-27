using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using RBush;

namespace DealOrForce.Simulation;

/// <summary>
/// What a country does when nobody is playing it.
///
/// Until now: nothing. The operational AI moved divisions once a war existed, but no war
/// ever started - `ai_war_chance` was declared in the defines and read by nothing at all,
/// so two hundred and sixty countries sat at peace forever, never raised a tax, never
/// built anything, never improved or soured a relationship, and never signed a peace they
/// were winning. The scenario's one war was the only thing that ever happened.
///
/// This is the other half of an AI: the half that decides. Every country forms opinions of
/// its neighbours and acts on them; keeps its budget solvent; builds what it is short of;
/// and, when a neighbour is weak, hostile and worth taking, declares war on it and later
/// writes a peace out of what it won.
///
/// It is deliberately cheap. Countries are staggered across the week and the month so no
/// single day pays for all of them, and the border graph every decision leans on is built
/// once per change to the map rather than once per question.
/// </summary>
public static partial class AiStatecraft
{
    /// <summary>
    /// A running count of what the AI decided and why it stopped deciding. Not campaign
    /// state and not saved: it exists so "the AI never does anything" can be answered with
    /// the step it actually fails at rather than with a shrug.
    /// </summary>
    public static readonly Dictionary<string, int> Tally = new(StringComparer.Ordinal);

    private static void Count(string what) => Tally[what] = Tally.GetValueOrDefault(what) + 1;

    private static string FiscalReasonKey(AiFiscalReason reason) => reason switch
    {
        AiFiscalReason.Ready => "war_fiscal_Ready",
        AiFiscalReason.InvalidAccounts => "war_fiscal_InvalidAccounts",
        AiFiscalReason.OverdueBills => "war_fiscal_OverdueBills",
        AiFiscalReason.DebtWorkout => "war_fiscal_DebtWorkout",
        AiFiscalReason.ShortRunway => "war_fiscal_ShortRunway",
        _ => "war_fiscal_" + reason
    };

    /// <summary>
    /// One campaign's daily statecraft tables - military strength, a map-index lookup,
    /// regions held, the great powers and who has signed what.
    ///
    /// They used to be static scratch stamped with the last campaign that asked, which is
    /// correct for exactly one world at a time: a second campaign stepping beside the first
    /// flipped the stamp every time it ticked, so the first world's tables were rebuilt
    /// from its own mid-day state every hour where the solo run kept the morning's answer.
    /// Same inputs, different freshness, different wars - the march-pace divergence one
    /// table over. Per-campaign tables make "worked out once a day" mean the same thing in
    /// both runs.
    /// </summary>
    internal sealed class DailyTables
    {
        private const int MaxDenseCap = 65536;

        internal int Day = int.MinValue;
        /// <summary>False until the first <see cref="EnsureDailyTables"/> has filled the maps.</summary>
        internal bool Built;
        internal float[] StrengthDense = Array.Empty<float>();
        internal bool[] HasStrengthDense = Array.Empty<bool>();
        internal Dictionary<int, float> StrengthOverflow;
        internal Nation[] NationByMapDense = Array.Empty<Nation>();
        internal Dictionary<int, Nation> NationByMapOverflow;
        internal int[] RegionCountDense = Array.Empty<int>();
        internal Dictionary<int, int> RegionCountOverflow;
        internal List<int>[] SignedWithDense = Array.Empty<List<int>>();
        internal Dictionary<int, List<int>> SignedWithOverflow;
        internal readonly Stack<List<int>> SignedListPool = new();
        internal readonly List<Nation> GreatPowers = new();
        internal int SignedWithTreatyVersion = int.MinValue;
        internal int SignedWithRelationCount = -1;
        internal readonly Dictionary<int, (int Hulls, List<int> Targets)> Overseas = new();
        internal int OverseasNationCount = -1;
        internal readonly Dictionary<int, List<int>> FriendsOf = new();
        internal int FriendsDay = int.MinValue;
        internal AllianceSystem.OfferScan AllianceScan;
        internal Dictionary<int, ScenarioState> StateByMapIndex;
        internal int GeographyWeek = int.MinValue;
        internal int GeographyStates = -1;
        internal readonly List<Nation> LiveNationsSorted = new();
        internal int LiveNationsDay = int.MinValue;
        internal int LiveNationsCount = -1;
        internal RBush<NationSpatialEntry> NationSpatialTree;
        internal int NationSpatialDay = int.MinValue;
        internal int NationSpatialCount = -1;
        internal readonly object SpatialGate = new();
        internal readonly List<StrategicWar> WarmakingOpenScratch = new();
        internal bool InStatecraftTick;
        internal List<(Nation Nation, float Spare)>[] ExportersByGood;
        internal int ExportersDay = int.MinValue;
        internal int ExportersNationCount = -1;
        internal readonly DiplomaticOutreach.OfferScan MissionScan = new();

        internal void ResetNationTables(int maxId, int maxMapIndex)
        {
            var idCap = Math.Min(Math.Max(maxId + 1, 256), MaxDenseCap);
            if (StrengthDense.Length < idCap)
            {
                StrengthDense = new float[idCap];
                HasStrengthDense = new bool[idCap];
            }
            else
            {
                Array.Clear(HasStrengthDense, 0, idCap);
            }
            StrengthOverflow?.Clear();

            var mapCap = Math.Min(Math.Max(maxMapIndex + 1, 256), MaxDenseCap);
            if (NationByMapDense.Length < mapCap)
            {
                NationByMapDense = new Nation[mapCap];
                RegionCountDense = new int[mapCap];
            }
            else
            {
                Array.Clear(NationByMapDense, 0, mapCap);
                Array.Clear(RegionCountDense, 0, mapCap);
            }
            NationByMapOverflow?.Clear();
            RegionCountOverflow?.Clear();
        }

        internal void SetNationAndStrength(int id, int mapIndex, Nation nation, float strength)
        {
            if ((uint)id < (uint)HasStrengthDense.Length)
            {
                HasStrengthDense[id] = true;
                StrengthDense[id] = strength;
            }
            else
            {
                (StrengthOverflow ??= new Dictionary<int, float>())[id] = strength;
            }
            if ((uint)mapIndex < (uint)NationByMapDense.Length)
                NationByMapDense[mapIndex] = nation;
            else
                (NationByMapOverflow ??= new Dictionary<int, Nation>())[mapIndex] = nation;
        }

        internal void AddStrength(int nationId, float delta)
        {
            if ((uint)nationId < (uint)HasStrengthDense.Length)
            {
                if (HasStrengthDense[nationId]) StrengthDense[nationId] += delta;
            }
            else if (StrengthOverflow is not null && StrengthOverflow.TryGetValue(nationId, out var cur))
            {
                StrengthOverflow[nationId] = cur + delta;
            }
        }

        internal bool TryGetStrength(int nationId, out float value)
        {
            if ((uint)nationId < (uint)HasStrengthDense.Length && HasStrengthDense[nationId])
            {
                value = StrengthDense[nationId];
                return true;
            }
            if (StrengthOverflow is not null && StrengthOverflow.TryGetValue(nationId, out value))
                return true;
            value = 0f;
            return false;
        }

        internal void AddRegionCount(int mapIndex)
        {
            if ((uint)mapIndex < (uint)RegionCountDense.Length)
                RegionCountDense[mapIndex]++;
            else
                (RegionCountOverflow ??= new Dictionary<int, int>())[mapIndex] =
                    (RegionCountOverflow?.GetValueOrDefault(mapIndex) ?? 0) + 1;
        }

        internal int GetRegionCount(int mapIndex)
        {
            if ((uint)mapIndex < (uint)RegionCountDense.Length)
                return RegionCountDense[mapIndex];
            return RegionCountOverflow is not null ? RegionCountOverflow.GetValueOrDefault(mapIndex) : 0;
        }

        internal void ResetSignedWith(int maxId)
        {
            var idCap = Math.Min(Math.Max(maxId + 1, 256), MaxDenseCap);
            if (SignedWithDense.Length < idCap)
            {
                var next = new List<int>[idCap];
                Array.Copy(SignedWithDense, next, SignedWithDense.Length);
                SignedWithDense = next;
            }
            for (var i = 0; i < SignedWithDense.Length; i++)
            {
                var list = SignedWithDense[i];
                if (list is null) continue;
                list.Clear();
                SignedListPool.Push(list);
                SignedWithDense[i] = null;
            }
            if (SignedWithOverflow is not null)
            {
                foreach (var list in SignedWithOverflow.Values) { list.Clear(); SignedListPool.Push(list); }
                SignedWithOverflow.Clear();
            }
        }

        internal void AddSignedWith(int a, int b)
        {
            AddDirected(a, b);
            AddDirected(b, a);

            void AddDirected(int owner, int other)
            {
                if ((uint)owner < (uint)SignedWithDense.Length)
                {
                    var list = SignedWithDense[owner];
                    if (list is null)
                        SignedWithDense[owner] = list = SignedListPool.Count > 0 ? SignedListPool.Pop() : new List<int>(4);
                    list.Add(other);
                }
                else
                {
                    SignedWithOverflow ??= new Dictionary<int, List<int>>();
                    if (!SignedWithOverflow.TryGetValue(owner, out var list))
                        SignedWithOverflow[owner] = list = SignedListPool.Count > 0 ? SignedListPool.Pop() : new List<int>(4);
                    list.Add(other);
                }
            }
        }

        internal IReadOnlyList<int> GetSignedWith(int nationId)
        {
            if ((uint)nationId < (uint)SignedWithDense.Length)
                return SignedWithDense[nationId];
            return SignedWithOverflow is not null && SignedWithOverflow.TryGetValue(nationId, out var list) ? list : null;
        }
    }

    private static DailyTables TablesOf(Campaign campaign) =>
        campaign.AiStatecraftTables ??= new DailyTables();

    internal static AllianceSystem.OfferScan AllianceScanFor(Campaign campaign) =>
        TablesOf(campaign).AllianceScan ??= new AllianceSystem.OfferScan(campaign);

    /// <summary>
    /// The countries everybody has an opinion about, and who has already signed what, are
    /// part of <see cref="DailyTables"/> - questions about the whole world that are free
    /// once a day and ruinous asked by each country for each of its neighbours.
    /// </summary>

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The day's statecraft in stages, a country at a time, for the smoothed step.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(AiStatecraft))) yield break;
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) yield break;

        using (SimProfiler.Measure("statecraft"))
        {
            // An offer nobody answered is withdrawn rather than left standing forever, so a
            // player who ignored one is asked again later instead of never.
            PeaceOffers.Expire(campaign);
        }
        // Foundations, a founding country at a time. The two reviews together were one
        // indivisible sixty-five milliseconds in a fifty-year world, and a frame budget cannot
        // shorten a stage that never ends: the smoother had to take the whole of it in one
        // frame, once a day, every day. Their order is unchanged - the proclamations, then the
        // peaceful projects, then the day's tables built from whatever world they left behind.
        using (var foundations = FoundationStages(campaign).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("  ai:foundations")) more = foundations.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        // The day's tables in slices. They are built once a day, but the day they are built is the
        // day the world moved: one piece of it was measured at 36 ms against a nine millisecond
        // frame, and a frame budget cannot shorten a stage that never ends.
        using (var tables = EnsureDailyTablesStages(campaign).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("statecraft")) more = tables.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        yield return 0;
        foreach (var _ in ThinkAboutTheNeighbours(campaign)) yield return 0;
    }

    /// <summary>
    /// The two founding reviews as one staged pass: a proclamation, then a peaceful project,
    /// each handing the frame back per founding country. Same countries, same order.
    /// </summary>
    private static IEnumerable<int> FoundationStages(Campaign campaign)
    {
        // Two reviews share one scope in the day's table; named separately here so a heavy day
        // says which of them was heavy instead of leaving "the foundations" as the answer.
        using (var proclamations = FormableProclamationAi.TickStages(campaign).GetEnumerator())
        {
            while (true)
            {
                if (Campaign.StageOverdue()) yield return 0;
                bool more;
                using (SimProfiler.Measure("    ai:foundations.proclaim")) more = proclamations.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        using (var unions = PeacefulFormationAi.TickStages(campaign).GetEnumerator())
        {
            while (true)
            {
                if (Campaign.StageOverdue()) yield return 0;
                bool more;
                using (SimProfiler.Measure("    ai:foundations.union")) more = unions.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
    }

    /// <summary>
    /// What the AI needs to know about the world today, worked out once for everybody.
    ///
    /// Military strength is a sum over every division in the world, and it was being
    /// recomputed for each neighbour of each country every week - which made the AI the
    /// most expensive system in the simulation by a factor of three. It changes once a day
    /// at most, so it is worked out once a day.
    ///
    /// The same is true of how much ground a country holds, which is how anyone without
    /// intelligence on a stranger guesses at its strength. That was a walk of the world's
    /// three thousand regions, with a predicate closing over the stranger, for every
    /// neighbour of every country every week.
    ///
    /// Stamped with the campaign as well as the day: a client rebuilding from a host's
    /// snapshot holds two campaigns at once, and a table that only knew what day it was
    /// would answer one of them with the other's world.
    /// </summary>
    private static void EnsureDailyTables(Campaign campaign)
    {
        foreach (var _ in EnsureDailyTablesStages(campaign)) { }
    }

    /// <summary>How many countries, regions or divisions one slice of the day's tables works through.</summary>
    private const int TableStageItems = 128;

    /// <summary>How many signed relations one slice of the day's tables walks.</summary>
    private const int TableStageRelations = 2048;

    /// <summary>
    /// The same tables, a slice at a time.
    ///
    /// They are built once a day and cost 2,4 ms on an ordinary day - but the day they are built
    /// is the day the world moved, and on a fifty-year save that was measured at **36 ms in one
    /// piece**: 44 000 relations and 1 900 regions against a nine millisecond frame. The order is
    /// untouched, because the slices run in the order the whole pass did and the tables are only
    /// read after the last of them.
    ///
    /// A decision that needs the tables mid-day calls the wrapper above, which runs every slice
    /// at once; the day's own tick calls this and hands the frame back between slices.
    /// </summary>
    private static IEnumerable<int> EnsureDailyTablesStages(Campaign campaign)
    {
        var tables = TablesOf(campaign);
        if (tables.Day == campaign.ElapsedDays) yield break;
        // Mark incomplete until every slice finishes: CachedStrength and synchronous mid-day
        // EnsureDailyTables calls must not observe a half-built StrengthCache or skip completion
        // if the frame budget yields between slices.
        tables.Day = int.MinValue;
        tables.Built = false;
        tables.Overseas.Clear();
        tables.OverseasNationCount = -1;

        var active = campaign.ActiveNations;
        var maxId = 0;
        var maxMapIndex = 0;
        for (var i = 0; i < active.Count; i++)
        {
            var n = active[i];
            if (n.Id > maxId) maxId = n.Id;
            if (n.MapIndex > maxMapIndex) maxMapIndex = n.MapIndex;
        }
        var states = campaign.States;
        for (var i = 0; i < states.Count; i++)
        {
            var map = states[i].NationMapIndex;
            if (map > maxMapIndex) maxMapIndex = map;
        }
        tables.ResetNationTables(maxId, maxMapIndex);

        // Who is where, and how strong, a few countries at a time.
        for (var first = 0; first < active.Count; first += TableStageItems)
        {
            if (tables.Day == campaign.ElapsedDays) yield break;
            var last = Math.Min(active.Count, first + TableStageItems);
            using (SimProfiler.Measure("  ai:tables"))
                for (var index = first; index < last; index++)
                {
                    var candidate = active[index];
                    tables.SetNationAndStrength(candidate.Id, candidate.MapIndex, candidate,
                        candidate.Gdp * campaign.Defines.AiEconomicWeight);
                }
            yield return 0;
        }
        if (tables.Day == campaign.ElapsedDays) yield break;

        // How much ground each of them holds.
        for (var first = 0; first < campaign.States.Count; first += TableStageItems)
        {
            if (tables.Day == campaign.ElapsedDays) yield break;
            var last = Math.Min(campaign.States.Count, first + TableStageItems);
            using (SimProfiler.Measure("  ai:tables"))
                for (var index = first; index < last; index++)
                {
                    var state = campaign.States[index];
                    tables.AddRegionCount(state.NationMapIndex);
                }
            yield return 0;
        }
        if (tables.Day == campaign.ElapsedDays) yield break;

        // And what stands in the field.
        for (var first = 0; first < campaign.Armies.Count; first += TableStageItems)
        {
            if (tables.Day == campaign.ElapsedDays) yield break;
            var last = Math.Min(campaign.Armies.Count, first + TableStageItems);
            using (SimProfiler.Measure("  ai:tables"))
                for (var index = first; index < last; index++)
                {
                    var army = campaign.Armies[index];
                    tables.AddStrength(army.NationId, ArmyStrengthTerm(army));
                }
            yield return 0;
        }
        if (tables.Day == campaign.ElapsedDays) yield break;

        // The great powers: one list built and sorted, small enough to be a slice of its own.
        using (SimProfiler.Measure("  ai:tables"))
        {
            var powers = tables.GreatPowers;
            powers.Clear();
            for (var i = 0; i < active.Count; i++)
            {
                var candidate = active[i];
                if (candidate.Population <= 0) continue;
                powers.Add(candidate);
            }
            powers.Sort((left, right) =>
            {
                tables.TryGetStrength(right.Id, out var rStr);
                tables.TryGetStrength(left.Id, out var lStr);
                var difference = rStr.CompareTo(lStr);
                return difference != 0 ? difference : left.Id.CompareTo(right.Id);
            });
            if (powers.Count > campaign.Defines.AiGreatPowers)
                powers.RemoveRange(campaign.Defines.AiGreatPowers,
                    powers.Count - campaign.Defines.AiGreatPowers);
        }
        yield return 0;
        if (tables.Day == campaign.ElapsedDays) yield break;

        // Who has already signed what: only rebuild when a treaty flag (Allied/NAP/Trade/Guarantee)
        // or the relation table count actually changed since yesterday.
        var treatyVer = DiplomaticRelation.TreatyMutationVersion;
        var relCount = campaign.Relations.Count;
        if (tables.SignedWithTreatyVersion != treatyVer || tables.SignedWithRelationCount != relCount)
        {
            tables.ResetSignedWith(maxId);
            using (var relations = campaign.Relations.Values.GetEnumerator())
            {
                var exhausted = false;
                while (!exhausted)
                {
                    if (tables.Day == campaign.ElapsedDays) yield break;
                    using (SimProfiler.Measure("  ai:tables"))
                    {
                        for (var step = 0; step < TableStageRelations; step++)
                        {
                            if (!relations.MoveNext()) { exhausted = true; break; }
                            var relation = relations.Current;
                            var isSigned = relation.Stance == DiplomaticStance.Allied ||
                                           relation.NonAggressionPact || relation.TradeAgreement ||
                                           relation.GuaranteeByA || relation.GuaranteeByB;
                            if (!isSigned) continue;
                            tables.AddSignedWith(relation.A, relation.B);
                        }
                    }
                    if (!exhausted) yield return 0;
                }
            }
            tables.SignedWithTreatyVersion = DiplomaticRelation.TreatyMutationVersion;
            tables.SignedWithRelationCount = campaign.Relations.Count;
        }
        EnsureNationSpatialTree(tables, active, campaign.ElapsedDays);
        tables.Day = campaign.ElapsedDays;
        tables.Built = true;
    }

    internal sealed class NationSpatialEntry : ISpatialData
    {
        public readonly Nation Nation;
        private readonly Envelope _envelope;

        public NationSpatialEntry(Nation nation, float normalizedLon, float lat)
        {
            Nation = nation;
            _envelope = new Envelope(normalizedLon, lat, normalizedLon, lat);
        }

        public ref readonly Envelope Envelope => ref _envelope;
    }

    private static float WrapLongitudeDegrees(float lon)
    {
        if (!float.IsFinite(lon)) return 0f;
        var wrapped = lon;
        while (wrapped > 180f) wrapped -= 360f;
        while (wrapped < -180f) wrapped += 360f;
        return wrapped;
    }

    internal static RBush<NationSpatialEntry> EnsureNationSpatialTree(DailyTables dailyTables,
        IReadOnlyList<Nation> active, int elapsedDays)
    {
        var count = active.Count;
        var tree = System.Threading.Volatile.Read(ref dailyTables.NationSpatialTree);
        if (tree is not null && dailyTables.NationSpatialDay == elapsedDays && dailyTables.NationSpatialCount == count)
            return tree;
        lock (dailyTables.SpatialGate)
        {
            tree = dailyTables.NationSpatialTree;
            if (tree is not null && dailyTables.NationSpatialDay == elapsedDays && dailyTables.NationSpatialCount == count)
                return tree;
            var entries = new List<NationSpatialEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var n = active[i];
                if (n is null || n.Population <= 0 || !float.IsFinite(n.CapitalLatitude) || !float.IsFinite(n.CapitalLongitude))
                    continue;
                entries.Add(new NationSpatialEntry(n, WrapLongitudeDegrees(n.CapitalLongitude), n.CapitalLatitude));
            }
            var built = new RBush<NationSpatialEntry>();
            if (entries.Count > 0) built.BulkLoad(entries);
            dailyTables.NationSpatialCount = count;
            dailyTables.NationSpatialDay = elapsedDays;
            System.Threading.Volatile.Write(ref dailyTables.NationSpatialTree, built);
            return built;
        }
    }

    internal static void CollectOverseasCandidatesRBush(RBush<NationSpatialEntry> tree, int selfId,
        float fromLat, float fromLon, float reach, List<int> targets, Dictionary<int, float> distByMapIndex)
    {
        if (tree is null || tree.Count == 0 || !float.IsFinite(fromLat) || !float.IsFinite(fromLon) || reach < 0f)
            return;

        var clampedLat = Math.Clamp(fromLat, -90f, 90f);
        var normLon = WrapLongitudeDegrees(fromLon);
        var minLat = (double)clampedLat - reach - 0.05;
        var maxLat = (double)clampedLat + reach + 0.05;
        var maxAbsMidLat = Math.Min(89.9, Math.Abs((double)clampedLat) + reach * 0.5 + 0.05);
        var cosMin = Math.Cos(maxAbsMidLat * Math.PI / 180.0);
        var lonSpan = cosMin > 1e-4 ? (reach / cosMin) + 0.05 : 181.0;

        if (lonSpan >= 180.0)
        {
            ScanSpatialEnvelope(tree, new Envelope(-180.1, minLat, 180.1, maxLat),
                selfId, fromLat, fromLon, reach, targets, distByMapIndex);
            return;
        }

        var lo = (double)normLon - lonSpan;
        var hi = (double)normLon + lonSpan;
        if (lo < -180.0)
        {
            // Antimeridian split boxes are strictly disjoint because lonSpan < 180.
            ScanSpatialEnvelope(tree, new Envelope(lo + 360.0, minLat, 180.1, maxLat),
                selfId, fromLat, fromLon, reach, targets, distByMapIndex);
            ScanSpatialEnvelope(tree, new Envelope(-180.1, minLat, hi, maxLat),
                selfId, fromLat, fromLon, reach, targets, distByMapIndex);
        }
        else if (hi > 180.0)
        {
            ScanSpatialEnvelope(tree, new Envelope(lo, minLat, 180.1, maxLat),
                selfId, fromLat, fromLon, reach, targets, distByMapIndex);
            ScanSpatialEnvelope(tree, new Envelope(-180.1, minLat, hi - 360.0, maxLat),
                selfId, fromLat, fromLon, reach, targets, distByMapIndex);
        }
        else
        {
            ScanSpatialEnvelope(tree, new Envelope(lo, minLat, hi, maxLat),
                selfId, fromLat, fromLon, reach, targets, distByMapIndex);
        }
    }

    private static void ScanSpatialEnvelope(RBush<NationSpatialEntry> tree, in Envelope env, int selfId,
        float fromLat, float fromLon, float reach, List<int> targets, Dictionary<int, float> distByMapIndex)
    {
        var hits = tree.Search(env);
        for (var i = 0; i < hits.Count; i++)
        {
            var other = hits[i].Nation;
            if (other.Id == selfId || other.Population <= 0) continue;
            var distance = NavalOperations.Distance(fromLat, fromLon, other.CapitalLatitude, other.CapitalLongitude);
            if (distance > reach) continue;
            if (distByMapIndex.TryAdd(other.MapIndex, distance))
                targets.Add(other.MapIndex);
        }
    }

    /// <summary>
    /// The countries this one has an opinion about this week.
    ///
    /// Four reasons, in the order a foreign ministry would give them: we share a border, we
    /// have already signed something, they are large enough that everybody has a view, and
    /// they are fighting the country we like least. A country with none of the four - and
    /// there are a few, mostly islands - has no foreign policy this week, which is honest.
    /// </summary>
    [ThreadStatic] private static List<Nation> _circleScratch;
    [ThreadStatic] private static HashSet<int> _circleSeenScratch;
    private static List<Nation> Circle => _circleScratch ??= new();
    private static HashSet<int> CircleSeen => _circleSeenScratch ??= new();

    private static void ConsiderCircle(List<Nation> circle, HashSet<int> seen, Nation other)
    {
        if (other is null || other.Population <= 0 || !seen.Add(other.Id)) return;
        circle.Add(other);
    }

    [ThreadStatic] private static List<InternationalOrganization> _circleOrgsScratch;

    private static void BuildCircle(Campaign campaign, Nation nation, Nation worstRival)
    {
        EnsureDailyTables(campaign);
        BuildCircleInto(campaign, TablesOf(campaign), nation, worstRival,
            Circle, CircleSeen, _circleOrgsScratch ??= new List<InternationalOrganization>(8));
    }

    private static void BuildCircleInto(Campaign campaign, DailyTables tables, Nation nation, Nation worstRival,
        List<Nation> circle, HashSet<int> seen, List<InternationalOrganization> orgs)
    {
        circle.Clear();
        seen.Clear();
        seen.Add(nation.Id);
        // The shared neighbour index is a HashSet: walked as one, its enumerator stays on the
        // stack instead of being boxed through the interface for every country every week. Same
        // set, same order.
        var neighbours = NeighboursOf(campaign, nation.MapIndex);
        if (neighbours is HashSet<int> neighbourSet)
            foreach (var mapIndex in neighbourSet) ConsiderCircle(circle, seen, ByMapIndex(campaign, mapIndex));
        else
            foreach (var mapIndex in neighbours) ConsiderCircle(circle, seen, ByMapIndex(campaign, mapIndex));
        InternationalOrganizations.FillMemberships(campaign, nation.Id, orgs);
        for (var oi = 0; oi < orgs.Count; oi++)
        {
            var members = orgs[oi].Members;
            for (var mi = 0; mi < members.Count; mi++)
                ConsiderCircle(circle, seen, campaign.NationById(members[mi]));
        }
        var partners = tables.GetSignedWith(nation.Id);
        if (partners is not null)
            for (var pi = 0; pi < partners.Count; pi++) ConsiderCircle(circle, seen, campaign.NationById(partners[pi]));
        var greatPowers = tables.GreatPowers;
        for (var gpi = 0; gpi < greatPowers.Count; gpi++) ConsiderCircle(circle, seen, greatPowers[gpi]);
        if (worstRival is not null)
        {
            var live = Warfare.LiveWars(campaign);
            for (var index = 0; index < live.Count; index++)
            {
                var war = live[index];
                if (war.Ended || !war.Involves(worstRival.Id)) continue;
                ConsiderCircle(circle, seen, campaign.NationById(war.AttackerId == worstRival.Id ? war.DefenderId : war.AttackerId));
            }
        }
    }

    /// <summary>How much ground a country holds, from the day's table.</summary>
    internal static int RegionsHeldBy(Campaign campaign, int mapIndex)
    {
        if (campaign is null) return 0;
        EnsureDailyTables(campaign);
        return TablesOf(campaign).GetRegionCount(mapIndex);
    }

    private readonly struct DueEntry
    {
        public readonly Nation Nation;
        public readonly bool IsHuman;
        public readonly bool DiplomacyDue;
        public readonly bool EconomyDue;
        public readonly bool WarmakingDue;

        public DueEntry(Nation nation, bool isHuman, bool diplomacyDue, bool economyDue, bool warmakingDue)
        {
            Nation = nation;
            IsHuman = isHuman;
            DiplomacyDue = diplomacyDue;
            EconomyDue = economyDue;
            WarmakingDue = warmakingDue;
        }
    }

    private sealed class DueSlot
    {
        public int NationId = -1;
        public readonly List<Nation> Circle = new();
        public readonly HashSet<int> CircleSeen = new();
        public readonly List<InternationalOrganization> CircleOrgs = new(8);
        public readonly List<WeeklyPairPrecheck> Prechecks = new();
        public readonly List<bool> SkipFriendly = new();

        public bool HasWarmaking;
        public readonly List<int> ReachableTargets = new();
        public readonly HashSet<int> ReachableSeen = new();
        public readonly Dictionary<int, float> OverseasDistance = new();
        public bool ComputedNewOverseas;
        public int NewHulls;
        public List<int> NewOverseasTargets;
        public readonly List<float> Judgements = new();

        public void Reset(int nationId)
        {
            NationId = nationId;
            Circle.Clear();
            CircleSeen.Clear();
            CircleOrgs.Clear();
            Prechecks.Clear();
            SkipFriendly.Clear();
            HasWarmaking = false;
            ReachableTargets.Clear();
            ReachableSeen.Clear();
            OverseasDistance.Clear();
            ComputedNewOverseas = false;
            NewHulls = 0;
            NewOverseasTargets = null;
            Judgements.Clear();
        }
    }

    [ThreadStatic] private static List<DueEntry> _dueEntriesScratch;
    [ThreadStatic] private static List<DueSlot> _dueSlotsScratch;
    [ThreadStatic] private static HashSet<int> _mutatedNationsScratch;
    [ThreadStatic] private static HashSet<long> _mutatedPairsScratch;
    [ThreadStatic] private static HashSet<int> _atWarNationsScratch;
    [ThreadStatic] private static Dictionary<int, (int Hulls, float Lat, float Lon)> _fleetHullsScratch;
    [ThreadStatic] private static List<(Nation Friend, float Opinion, int CircleIdx)> _friendlyTriplesScratch;

    private static IEnumerable<int> ThinkAboutTheNeighbours(Campaign campaign)
    {
        var tables = TablesOf(campaign);
        tables.InStatecraftTick = true;
        try
        {
            var active = campaign.ActiveNations;
            var due = _dueEntriesScratch ??= new List<DueEntry>(64);
            due.Clear();
            var elapsed = campaign.ElapsedDays;
            var checkCalm = campaign.Defines.AiCalmCadenceDays > 1;
            var atWarSet = _atWarNationsScratch ??= new HashSet<int>();
            atWarSet.Clear();
            var liveWars = Warfare.LiveWars(campaign);
            for (var wi = 0; wi < liveWars.Count; wi++)
            {
                var w = liveWars[wi];
                if (w.Ended) continue;
                atWarSet.Add(w.AttackerId);
                atWarSet.Add(w.DefenderId);
                foreach (var p in w.AttackerAllies) atWarSet.Add(p);
                foreach (var p in w.DefenderAllies) atWarSet.Add(p);
            }

            for (var index = 0; index < active.Count; index++)
            {
                var nation = active[index];
                if (nation.Population <= 0) continue;
                var day = elapsed + nation.Id;
                var isHuman = campaign.IsHumanControlled(nation);
                if (isHuman)
                {
                    var eco = day % 30 == 0;
                    var dip = day % 7 == 0 && Delegation.Holds(nation, Delegation.Diplomacy);
                    if (eco || dip) due.Add(new DueEntry(nation, true, dip, eco, false));
                    continue;
                }

                int diplomacyDays, warmakingDays;
                var inWar = atWarSet.Contains(nation.Id);
                if (checkCalm && IsCalmAiFast(campaign, nation, inWar))
                {
                    CalmCadences(campaign, nation, out diplomacyDays, out warmakingDays);
                }
                else
                {
                    diplomacyDays = 7;
                    warmakingDays = 11;
                }
                var dipDue = day % diplomacyDays == 0;
                var ecoDue = day % 30 == 0;
                var warDue = day % warmakingDays == 0;
                if (dipDue || ecoDue || warDue)
                    due.Add(new DueEntry(nation, false, dipDue, ecoDue, warDue));
            }

            var dueCount = due.Count;
            if (dueCount == 0) yield break;
            if (dueCount > 1)
                due.Sort(static (left, right) => left.Nation.Id.CompareTo(right.Nation.Id));

            // Phase 1: warm shared caches once, then pre-evaluate read-only checks across all due nations
            // using DeterministicParallel.ForRange (MT == ST identical slot outputs).
            EnsureDailyTables(campaign);
            if (tables.FriendsDay != elapsed)
            {
                tables.FriendsDay = elapsed;
                campaign.Caches.Protectors.CopyProtectorIdsInto(campaign, tables.FriendsOf);
            }
            InternationalOrganizations.WarmDefencePartners(campaign);
            DiplomaticOutreach.WarmCensus(campaign);
            EnsureGeography(campaign);
            _ = campaign.Caches.Protectors.AlliesOf(campaign, 0);
            if (tables.OverseasNationCount != active.Count)
            {
                tables.Overseas.Clear();
                tables.OverseasNationCount = active.Count;
            }
            for (var i = 0; i < active.Count; i++) active[i].CachedSocialKey();

            var fleetHulls = _fleetHullsScratch ??= new Dictionary<int, (int, float, float)>();
            fleetHulls.Clear();
            var fleets = campaign.Fleets;
            for (var fi = 0; fi < fleets.Count; fi++)
            {
                var fleet = fleets[fi];
                if (fleet.Hulls <= 0) continue;
                if (fleetHulls.TryGetValue(fleet.NationId, out var cur))
                    fleetHulls[fleet.NationId] = (cur.Hulls + fleet.Hulls, cur.Lat, cur.Lon);
                else
                    fleetHulls[fleet.NationId] = (fleet.Hulls, fleet.HomeLatitude, fleet.HomeLongitude);
            }

            var anyWarmaking = false;
            var checkFormables = campaign.FeatureEnabled("formable_nations");
            for (var i = 0; i < dueCount; i++)
            {
                var e = due[i];
                _ = NeighboursOf(campaign, e.Nation.MapIndex);
                if (e.WarmakingDue && !e.IsHuman)
                {
                    anyWarmaking = true;
                    if (checkFormables && !string.IsNullOrEmpty(e.Nation.ConsolidationProject))
                        _ = Consolidation.WarValue(campaign, e.Nation, e.Nation);
                }
            }
            if (anyWarmaking)
            {
                foreach (var _ in BorderShape.EnsureStages(campaign)) { }
                NuclearPosture.WarmUmbrellaCache(campaign);
            }
            campaign.Caches.TerritorialDisputes.WarmForTick(campaign);

            var slots = _dueSlotsScratch ??= new List<DueSlot>(64);
            while (slots.Count < dueCount) slots.Add(new DueSlot());
            for (var i = 0; i < dueCount; i++) slots[i].Reset(due[i].Nation.Id);

            var defines = campaign.Defines;
            var identityOn = campaign.FeatureEnabled("state_identity");
            var intelligenceOn = campaign.FeatureEnabled("intelligence");
            var standingMultiMask = ComputeStandingMultiMask();
            var scanMemory = WeeklyScanOf(campaign);
            var minHulls = defines.AiInvasionMinimumHulls;
            var reach = defines.AiInvasionSearchDegrees;
            var wantedOverseas = Math.Max(1, defines.AiOverseasTargetsConsidered);
            var canDeclareWar = defines.AiMayDeclareWar;
            var warSupportFloor = defines.AiWarSupportFloor;
            var stabilityFloor = defines.AiWarStabilityFloor;
            var treasuryFloor = defines.AiTreasuryFloor;

            DeterministicParallel.ForRange(campaign, 0, dueCount, (from, to) =>
            {
                for (var i = from; i < to; i++)
                {
                    var e = due[i];
                    var slot = slots[i];
                    var nation = e.Nation;
                    if (e.DiplomacyDue)
                    {
                        BuildCircleInto(campaign, tables, nation, campaign.NationById(nation.WarPlanTargetId),
                            slot.Circle, slot.CircleSeen, slot.CircleOrgs);
                        var circleCount = slot.Circle.Count;
                        if (circleCount > 0)
                        {
                            var neighbours = NeighboursOf(campaign, nation.MapIndex);
                            var borderSet = neighbours as HashSet<int>;
                            var myStrength = M.Max(0.01f, Strength(campaign, nation));
                            for (var ci = 0; ci < circleCount; ci++)
                            {
                                var partner = slot.Circle[ci];
                                if (partner is null || partner.Id == nation.Id)
                                {
                                    slot.Prechecks.Add(new WeeklyPairPrecheck { Unchanged = true });
                                    slot.SkipFriendly.Add(false);
                                    continue;
                                }
                                var borders = borderSet is not null
                                    ? borderSet.Contains(partner.MapIndex)
                                    : neighbours.Contains(partner.MapIndex);
                                var pre = PrecheckWeeklyPair(campaign, scanMemory, nation, partner, borders, myStrength,
                                    identityOn, intelligenceOn, standingMultiMask);
                                slot.Prechecks.Add(pre);
                                var skipFriendly = pre.Unchanged &&
                                    pre.Peeked is { Stance: not DiplomaticStance.AtWar } rel &&
                                    rel.Opinion >= defines.AiFriendshipThreshold &&
                                    SkipUnchangedFriendly(campaign, nation, partner, borders);
                                slot.SkipFriendly.Add(skipFriendly);
                            }
                        }
                    }

                    if (e.WarmakingDue && !e.IsHuman && canDeclareWar &&
                        !atWarSet.Contains(nation.Id) &&
                        nation.WarSupport >= warSupportFloor &&
                        nation.Stability >= stabilityFloor &&
                        AiFiscalPolicy.Assess(campaign, nation, treasuryFloor).CanSpend)
                    {
                        slot.HasWarmaking = true;
                        var targets = slot.ReachableTargets;
                        var seenT = slot.ReachableSeen;
                        var landNeighbours = CampaignDiplomacy.Neighbours(campaign, nation.MapIndex);
                        if (landNeighbours is HashSet<int> landSet)
                        {
                            foreach (var mapIndex in landSet)
                                if (seenT.Add(mapIndex)) targets.Add(mapIndex);
                        }
                        else
                        {
                            foreach (var mapIndex in landNeighbours)
                                if (seenT.Add(mapIndex)) targets.Add(mapIndex);
                        }

                        if (fleetHulls.TryGetValue(nation.Id, out var naval) && naval.Hulls >= minHulls)
                        {
                            if (tables.Overseas.TryGetValue(nation.Id, out var cachedOv) && cachedOv.Hulls == naval.Hulls)
                            {
                                var ovTargets = cachedOv.Targets;
                                for (var oi = 0; oi < ovTargets.Count; oi++)
                                    if (seenT.Add(ovTargets[oi])) targets.Add(ovTargets[oi]);
                            }
                            else
                            {
                                var ov = cachedOv.Targets is not null ? new List<int>(cachedOv.Targets.Count) : new List<int>();
                                var dist = slot.OverseasDistance;
                                var tree = tables.NationSpatialTree ?? EnsureNationSpatialTree(tables, active, elapsed);
                                CollectOverseasCandidatesRBush(tree, nation.Id, naval.Lat, naval.Lon, reach, ov, dist);
                                ov.Sort((left, right) =>
                                {
                                    var order = dist.GetValueOrDefault(left, float.MaxValue)
                                        .CompareTo(dist.GetValueOrDefault(right, float.MaxValue));
                                    return order != 0 ? order : left.CompareTo(right);
                                });
                                if (ov.Count > wantedOverseas) ov.RemoveRange(wantedOverseas, ov.Count - wantedOverseas);
                                dist.Clear();
                                slot.ComputedNewOverseas = true;
                                slot.NewHulls = naval.Hulls;
                                slot.NewOverseasTargets = ov;
                                for (var oi = 0; oi < ov.Count; oi++)
                                    if (seenT.Add(ov[oi])) targets.Add(ov[oi]);
                            }
                        }

                        var tCount = targets.Count;
                        for (var ri = 0; ri < tCount; ri++)
                        {
                            var other = ByMapIndex(campaign, targets[ri]);
                            if (other is null || other.Id == nation.Id ||
                                (campaign.IsHumanControlled(other) && !defines.AiMayAttackPlayer))
                            {
                                slot.Judgements.Add(float.NaN);
                                continue;
                            }
                            var allied = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id) is { Stance: DiplomaticStance.Allied };
                            if (!allied && !CampaignDiplomacy.CanDeclareWarQuiet(campaign, nation.Id, other.Id))
                            {
                                slot.Judgements.Add(float.NegativeInfinity);
                                continue;
                            }
                            var judgement = JudgeWarForReadOnly(campaign, nation, other);
                            var appetite = AnnexationAmbition.Appetite(campaign, nation, other);
                            if (appetite > 0f) judgement += defines.AiAnnexationTargetBonus * appetite;
                            if (allied && judgement <= 0f)
                            {
                                slot.Judgements.Add(float.NegativeInfinity);
                                continue;
                            }
                            if (!CampaignDiplomacy.SharesLandBorder(campaign, nation.MapIndex, other.MapIndex))
                                judgement -= defines.AiOverseasCaution;
                            slot.Judgements.Add(judgement);
                        }
                    }
                }
            }, minItems: 8);

            // Commit any newly built Overseas target caches deterministically in ascending order.
            for (var i = 0; i < dueCount; i++)
            {
                var slot = slots[i];
                if (slot.ComputedNewOverseas)
                    tables.Overseas[slot.NationId] = (slot.NewHulls, slot.NewOverseasTargets);
            }
            fleetHulls.Clear();

            // Phase 2: apply actions sequentially in strictly ascending Nation.Id order.
            var initialWarRev = campaign.WarRevision;
            var mutatedNations = _mutatedNationsScratch ??= new HashSet<int>();
            mutatedNations.Clear();
            var mutatedPairs = _mutatedPairsScratch ??= new HashSet<long>();
            mutatedPairs.Clear();
            var worldInvalidated = false;

            for (var index = 0; index < dueCount; index++)
            {
                var entry = due[index];
                var slot = worldInvalidated ? null : slots[index];
                var warRev = campaign.WarRevision;
                var allianceRev = DiplomaticRelation.AllianceStanceVersion;
                var protectorRev = DiplomaticRelation.ProtectorMutationVersion;
                var ownershipRev = campaign.OwnershipRevision;
                var nationInWar = initialWarRev == warRev
                    ? atWarSet.Contains(entry.Nation.Id)
                    : InAnyWar(campaign, entry.Nation.Id);
                using var steps = ThinkTodayStages(campaign, entry, slot, mutatedNations, mutatedPairs,
                    worldInvalidated, nationInWar, intelligenceOn, standingMultiMask).GetEnumerator();
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("statecraft")) more = steps.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
                if (warRev != campaign.WarRevision ||
                    allianceRev != DiplomaticRelation.AllianceStanceVersion ||
                    protectorRev != DiplomaticRelation.ProtectorMutationVersion ||
                    ownershipRev != campaign.OwnershipRevision)
                    worldInvalidated = true;
                // Between countries only when the frame's slice is spent: a yield is not part of
                // the world, and an unconditional one was a trip through every enclosing stage for
                // each of the day's due countries.
                if (Campaign.StageOverdue()) yield return 0;
            }
            atWarSet.Clear();
            mutatedNations.Clear();
            mutatedPairs.Clear();
            for (var i = 0; i < dueCount; i++) slots[i].Reset(-1);
            due.Clear();
        }
        finally
        {
            tables.InStatecraftTick = false;
            campaign.Caches.TerritorialDisputes.EndTick();
        }
    }

    internal static bool InAnyWar(Campaign campaign, int nationId)
    {
        var liveWars = Warfare.LiveWars(campaign);
        for (var i = 0; i < liveWars.Count; i++)
        {
            var w = liveWars[i];
            if (!w.Ended && w.Involves(nationId)) return true;
        }
        return false;
    }

    internal static bool HasPendingPeaceOffer(Campaign campaign, int nationId)
    {
        var liveWars = Warfare.LiveWars(campaign);
        for (var i = 0; i < liveWars.Count; i++)
        {
            var w = liveWars[i];
            if (!w.Ended && w.Involves(nationId) && w.PendingOfferFromId >= 0) return true;
        }
        return false;
    }

    internal static bool HasPendingUltimatum(Campaign campaign, int nationId)
    {
        if (campaign.UltimatumPapers.Count == 0) return false;
        for (var i = 0; i < campaign.UltimatumPapers.Count; i++)
        {
            var paper = campaign.UltimatumPapers[i];
            if (paper.Pending && (paper.FromId == nationId || paper.ToId == nationId)) return true;
        }
        return false;
    }

    internal static bool IsCalmAi(Campaign campaign, Nation nation) =>
        !campaign.IsHumanControlled(nation) && IsCalmAiFast(campaign, nation, InAnyWar(campaign, nation.Id));

    private static bool IsCalmAiFast(Campaign campaign, Nation nation, bool inWar)
    {
        if (inWar) return false;
        if (nation.WarPlanTargetId > 0) return false;
        if (nation.DomesticCrisis is { } crisis && (crisis.CoupPressure > 0.35f || crisis.DaysAboveThreshold > 0)) return false;
        if (HasPendingPeaceOffer(campaign, nation.Id)) return false;
        if (HasPendingUltimatum(campaign, nation.Id)) return false;
        return true;
    }

    private static void CalmCadences(Campaign campaign, Nation nation, out int diplomacyDays, out int warmakingDays)
    {
        var cadence = Math.Max(2, campaign.Defines.AiCalmCadenceDays);
        var regionsHeld = campaign.StatesOf(nation).Length;
        var isMajor = nation.Population >= 25_000_000 || nation.Gdp >= 400f || regionsHeld >= 12;
        var isMicro = !isMajor && regionsHeld <= 2 && nation.Population < 5_000_000;
        diplomacyDays = isMajor ? Math.Max(7, cadence) : Math.Max(14, cadence * 2);
        warmakingDays = isMajor ? Math.Max(14, cadence * 2)
            : isMicro ? Math.Max(28, cadence * 4)
            : Math.Max(21, cadence * 3);
    }

    private static bool ThinksToday(Campaign campaign, Nation nation)
    {
        var day = campaign.ElapsedDays + nation.Id;
        if (campaign.IsHumanControlled(nation)) return day % 30 == 0 || (day % 7 == 0 && Delegation.Holds(nation, Delegation.Diplomacy));
        if (campaign.Defines.AiCalmCadenceDays > 1 && IsCalmAi(campaign, nation))
        {
            CalmCadences(campaign, nation, out var dipDays, out var warDays);
            return day % dipDays == 0 || day % 30 == 0 || day % warDays == 0;
        }
        return day % 7 == 0 || day % 30 == 0 || day % 11 == 0;
    }

    private static IEnumerable<int> ThinkTodayStages(Campaign campaign, DueEntry entry, DueSlot slot,
        HashSet<int> mutatedNations, HashSet<long> mutatedPairs, bool worldInvalidated,
        bool nationInWar, bool intelligenceOn, int standingMultiMask)
    {
        var nation = entry.Nation;
        // A player's country is governed by its player - except for the portfolios that
        // player has handed to its own civil service, which are run here on the same
        // monthly beat as everybody else's. See `Delegation`.
        if (entry.IsHuman)
        {
            if (entry.EconomyDue)
                using (SimProfiler.Measure("  ai:delegated")) Delegation.Tick(campaign, nation);
            // The foreign ministry keeps the same weekly round as everybody else's.
            if (entry.DiplomacyDue)
                using (SimProfiler.Measure("  ai:delegated"))
                    foreach (var _ in DiplomacyStages(campaign, nation, slot, mutatedNations, mutatedPairs,
                        worldInvalidated, nationInWar, intelligenceOn, standingMultiMask)) { }
            yield break;
        }

        if (entry.DiplomacyDue)
        {
            foreach (var _ in DiplomacyStages(campaign, nation, slot, mutatedNations, mutatedPairs,
                worldInvalidated, nationInWar, intelligenceOn, standingMultiMask)) yield return 0;
            if (Campaign.StageOverdue()) yield return 0;
        }
        if (entry.EconomyDue)
        {
            if (Campaign.StageOverdue()) yield return 0;
            using (SimProfiler.Measure("  ai:economy")) Economy(campaign, nation, nationInWar);
            mutatedNations.Add(nation.Id);
            campaign.Caches.TerritorialDisputes.Invalidate();
            if (Campaign.StageOverdue()) yield return 0;
        }
        if (entry.WarmakingDue)
        {
            using var warSteps = WarmakingStages(campaign, nation, slot, mutatedNations, mutatedPairs, worldInvalidated).GetEnumerator();
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("  ai:warmaking")) more = warSteps.MoveNext();
                if (!more) break;
                yield return 0;
            }
            mutatedNations.Add(nation.Id);
            campaign.Caches.TerritorialDisputes.Invalidate();
        }
    }

    /// <summary>Countries this one can walk to. One shared index, built once per map change.</summary>
    public static IReadOnlyCollection<int> NeighboursOf(Campaign campaign, int nationMapIndex) =>
        CampaignDiplomacy.Neighbours(campaign, nationMapIndex);

    [ThreadStatic] private static List<int> _reachableTargetsScratch;
    [ThreadStatic] private static HashSet<int> _reachableSeenScratch;
    [ThreadStatic] private static Dictionary<int, float> _overseasDistanceScratch;
    private static List<int> ReachableTargets => _reachableTargetsScratch ??= new();
    private static HashSet<int> ReachableSeen => _reachableSeenScratch ??= new();
    private static Dictionary<int, float> OverseasDistance => _overseasDistanceScratch ??= new();

    private static IReadOnlyList<int> Reachable(Campaign campaign, Nation nation)
    {
        var tables = TablesOf(campaign);
        var active = campaign.ActiveNations;
        if (tables.OverseasNationCount != active.Count)
        {
            tables.Overseas.Clear();
            tables.OverseasNationCount = active.Count;
        }
        ReachableTargets.Clear();
        ReachableSeen.Clear();
        var landNeighbours = CampaignDiplomacy.Neighbours(campaign, nation.MapIndex);
        if (landNeighbours is HashSet<int> landSet)
        {
            foreach (var mapIndex in landSet)
                if (ReachableSeen.Add(mapIndex)) ReachableTargets.Add(mapIndex);
        }
        else
        {
            foreach (var mapIndex in landNeighbours)
                if (ReachableSeen.Add(mapIndex)) ReachableTargets.Add(mapIndex);
        }

        var hulls = 0;
        var fromLatitude = 0f;
        var fromLongitude = 0f;
        foreach (var fleet in campaign.Fleets)
        {
            if (fleet.NationId != nation.Id || fleet.Hulls <= 0) continue;
            if (hulls == 0) { fromLatitude = fleet.HomeLatitude; fromLongitude = fleet.HomeLongitude; }
            hulls += fleet.Hulls;
        }
        if (hulls < campaign.Defines.AiInvasionMinimumHulls) return ReachableTargets;

        if (!tables.Overseas.TryGetValue(nation.Id, out var cached) || cached.Hulls != hulls)
        {
            var targets = cached.Targets ?? new List<int>();
            targets.Clear();
            var reach = campaign.Defines.AiInvasionSearchDegrees;
            var wanted = Math.Max(1, campaign.Defines.AiOverseasTargetsConsidered);
            var tree = tables.NationSpatialTree ?? EnsureNationSpatialTree(tables, active, campaign.ElapsedDays);
            CollectOverseasCandidatesRBush(tree, nation.Id, fromLatitude, fromLongitude, reach, targets, OverseasDistance);
            targets.Sort((left, right) =>
            {
                var order = OverseasDistance.GetValueOrDefault(left, float.MaxValue)
                    .CompareTo(OverseasDistance.GetValueOrDefault(right, float.MaxValue));
                return order != 0 ? order : left.CompareTo(right);
            });
            if (targets.Count > wanted) targets.RemoveRange(wanted, targets.Count - wanted);
            OverseasDistance.Clear();
            cached = (hulls, targets);
            tables.Overseas[nation.Id] = cached;
        }

        foreach (var mapIndex in cached.Targets)
            if (ReachableSeen.Add(mapIndex)) ReachableTargets.Add(mapIndex);
        return ReachableTargets;
    }

    // --------------------------------------------------------------- diplomacy ---------

    private static IEnumerable<int> DiplomacyStages(Campaign campaign, Nation nation, DueSlot slot = null,
        HashSet<int> mutatedNations = null, HashSet<long> mutatedPairs = null, bool worldInvalidated = false,
        bool? nationInWar = null, bool? intelligenceEnabled = null, int standingMultiMask = -1)
    {
        var defines = campaign.Defines;
        Nation bestFriend = null;
        Nation worstRival = null;
        DiplomaticRelation bestFriendRelation = null;
        DiplomaticRelation worstRivalRelation = null;
        var bestOpinion = float.MinValue;
        var worstOpinion = float.MaxValue;
        var canUseSlot = !worldInvalidated && slot is not null && (mutatedNations is null || !mutatedNations.Contains(nation.Id));
        var neighbours = NeighboursOf(campaign, nation.MapIndex);
        var myStrength = M.Max(0.01f, Strength(campaign, nation));
        var identityOn = campaign.FeatureEnabled("state_identity");
        var intelligenceOn = intelligenceEnabled ?? campaign.FeatureEnabled("intelligence");
        var inWar = nationInWar ?? InAnyWar(campaign, nation.Id);
        var scanMemory = WeeklyScanOf(campaign);
        List<Nation> circle;

        using (SimProfiler.Measure("  ai:diplomacy"))
        {
            using (SimProfiler.Measure("    dip:circle"))
            {
                if (canUseSlot)
                {
                    circle = slot.Circle;
                }
                else
                {
                    BuildCircle(campaign, nation, campaign.NationById(nation.WarPlanTargetId));
                    circle = Circle;
                }
            }
            if (circle.Count == 0)
            {
                if (TryOfferCrisisGrant(campaign, nation)) { mutatedNations?.Add(nation.Id); yield break; }
                if (TryHedgeSupply(campaign, nation)) mutatedNations?.Add(nation.Id);
                yield break;
            }
        }

        var circleCount = circle.Count;
        var slots = System.Buffers.ArrayPool<WeeklyPairPrecheck>.Shared.Rent(circleCount);
        var skipFlags = canUseSlot && slot.SkipFriendly.Count == circleCount ? slot.SkipFriendly : null;
        try
        {
            using (SimProfiler.Measure("  ai:diplomacy"))
            using (SimProfiler.Measure("    dip:opinions"))
            {
                if (canUseSlot && slot.Prechecks.Count == circleCount)
                {
                    var borderSet = neighbours as HashSet<int>;
                    for (var i = 0; i < circleCount; i++)
                    {
                        var partner = circle[i];
                        if (partner is null || partner.Id == nation.Id ||
                            ((mutatedNations is null || !mutatedNations.Contains(partner.Id)) &&
                             (mutatedPairs is null || !mutatedPairs.Contains(WeeklyPairKey(nation.Id, partner.Id)))))
                        {
                            slots[i] = slot.Prechecks[i];
                            continue;
                        }
                        var borders = borderSet is not null
                            ? borderSet.Contains(partner.MapIndex)
                            : neighbours.Contains(partner.MapIndex);
                        slots[i] = PrecheckWeeklyPair(campaign, scanMemory, nation, partner, borders, myStrength,
                            identityOn, intelligenceOn, standingMultiMask);
                    }
                }
                else
                {
                    nation.CachedSocialKey();
                    for (var ci = 0; ci < circleCount; ci++) circle[ci]?.CachedSocialKey();
                    var borderSet = neighbours as HashSet<int>;
                    if (circleCount >= 96 && DeterministicParallel.Enabled(campaign))
                    {
                        DeterministicParallel.ForRange(campaign, 0, circleCount, (from, to) =>
                        {
                            for (var i = from; i < to; i++)
                            {
                                var partner = circle[i];
                                if (partner is null || partner.Id == nation.Id)
                                {
                                    slots[i] = new WeeklyPairPrecheck { Unchanged = true };
                                    continue;
                                }
                                var borders = borderSet is not null
                                    ? borderSet.Contains(partner.MapIndex)
                                    : neighbours.Contains(partner.MapIndex);
                                slots[i] = PrecheckWeeklyPair(campaign, scanMemory, nation, partner, borders, myStrength,
                                    identityOn, intelligenceOn, standingMultiMask);
                            }
                        }, minItems: 48);
                    }
                    else
                    {
                        for (var i = 0; i < circleCount; i++)
                        {
                            var partner = circle[i];
                            if (partner is null || partner.Id == nation.Id)
                            {
                                slots[i] = new WeeklyPairPrecheck { Unchanged = true };
                                continue;
                            }
                            var borders = borderSet is not null
                                ? borderSet.Contains(partner.MapIndex)
                                : neighbours.Contains(partner.MapIndex);
                            slots[i] = PrecheckWeeklyPair(campaign, scanMemory, nation, partner, borders, myStrength,
                                identityOn, intelligenceOn, standingMultiMask);
                        }
                    }
                }
            }

            for (var ci = 0; ci < circleCount; ci++)
            {
                var other = circle[ci];
                if (other is null || other.Id == nation.Id) continue;
                var pre = slots[ci];
                DiplomaticRelation relation;
                if (pre.Unchanged)
                {
                    relation = pre.Peeked;
                }
                else
                {
                    // Only when the frame's slice is spent; the forced yield every eight written
                    // pairs was a trip through every enclosing stage and its profiler scopes.
                    if (Campaign.StageOverdue()) yield return 0;
                    using (SimProfiler.Measure("  ai:diplomacy"))
                    using (SimProfiler.Measure("    dip:opinions"))
                    {
                        var assessedDayBefore = other.AssessedDay;
                        var assessedStrengthBefore = other.AssessedStrength;
                        if (CommitWeeklyPair(campaign, scanMemory, nation, other, myStrength, identityOn, pre, out relation))
                        {
                            mutatedPairs?.Add(WeeklyPairKey(nation.Id, other.Id));
                            if (other.AssessedDay != assessedDayBefore || other.AssessedStrength != assessedStrengthBefore)
                                mutatedNations?.Add(other.Id);
                        }
                    }
                    slots[ci].Peeked = relation;
                }
                if (relation is null || relation.Stance == DiplomaticStance.AtWar) continue;
                if (relation.Opinion > bestOpinion) { bestOpinion = relation.Opinion; bestFriend = other; bestFriendRelation = relation; }
                if (relation.Opinion < worstOpinion) { worstOpinion = relation.Opinion; worstRival = other; worstRivalRelation = relation; }
            }

        using (SimProfiler.Measure("  ai:diplomacy"))
        {

        if (bestFriend is not null && bestOpinion >= defines.AiFriendshipThreshold &&
            MathF.Abs(bestOpinion) >= MathF.Abs(worstOpinion))
        {
            using var friendlySample = SimProfiler.Measure("    dip:friendly");
            DiplomaticOutreach.WarmCensus(campaign);
            var pairs = _friendlyTriplesScratch ??= new List<(Nation Friend, float Opinion, int CircleIdx)>(16);
            pairs.Clear();
            for (var ci = 0; ci < circleCount; ci++)
            {
                var other = circle[ci];
                if (other is null || other.Id == nation.Id) continue;
                var bond = slots[ci].Peeked;
                if (bond is null) continue;
                if (bond.Stance != DiplomaticStance.AtWar &&
                    bond.Opinion >= defines.AiFriendshipThreshold)
                    pairs.Add((other, bond.Opinion, ci));
            }
            if (pairs.Count > 1)
                pairs.Sort(static (left, right) =>
                {
                    var byOpinion = right.Opinion.CompareTo(left.Opinion);
                    return byOpinion != 0 ? byOpinion : left.Friend.Id.CompareTo(right.Friend.Id);
                });
            var canConfrontRival = worstRival is not null &&
                (worstOpinion <= defines.AiRivalryThreshold ||
                 (worstRivalRelation is not { Stance: DiplomaticStance.Allied } &&
                  (TerritorialClaims.HoldsOurHomeland(campaign, nation, worstRival) ||
                   LostLands.HasClaimedLand(campaign, nation, worstRival) ||
                   BorderShape.Grievance(campaign, nation, worstRival) > 0.25f)));
            var didFriendlyMove = false;
            var tables = TablesOf(campaign);
            var missionScan = tables.MissionScan;
            missionScan.Reset(campaign, nation.Id);
            var allianceScan = AllianceScanFor(campaign);
            var friendBorders = neighbours as HashSet<int>;
            var limit = Math.Min(8, pairs.Count);
            for (var fi = 0; fi < limit; fi++)
            {
                var (friend, _, ci) = pairs[fi];
                var friendMutated = (mutatedNations is not null && mutatedNations.Contains(friend.Id)) ||
                                    (mutatedPairs is not null && mutatedPairs.Contains(WeeklyPairKey(nation.Id, friend.Id)));
                if (skipFlags is not null && slots[ci].Unchanged && !friendMutated)
                {
                    if (skipFlags[ci]) continue;
                }
                else
                {
                    var bordersFriend = friendBorders is not null
                        ? friendBorders.Contains(friend.MapIndex)
                        : neighbours.Contains(friend.MapIndex);
                    if (SkipUnchangedFriendly(campaign, nation, friend, bordersFriend)) continue;
                }
                if (TryFriendlyMove(campaign, nation, friend, missionScan, allianceScan, inWar))
                {
                    mutatedNations?.Add(nation.Id);
                    mutatedNations?.Add(friend.Id);
                    if (!canConfrontRival) yield break;
                    didFriendlyMove = true;
                    break;
                }
            }
            if (didFriendlyMove)
            {
                var relation = worstRivalRelation ?? CampaignDiplomacy.Get(campaign, nation.Id, worstRival.Id);
                CampaignDiplomacy.RefreshExpiredTruce(campaign, relation);
                if (relation.Stance is DiplomaticStance.Neutral or DiplomaticStance.Friendly)
                {
                    var lastAction = relation.LastActionDay;
                    new DenounceCommand { IssuerId = nation.Id, TargetId = worstRival.Id }.Apply(campaign);
                    if (relation.LastActionDay != lastAction)
                    {
                        Count("denounced");
                        mutatedNations?.Add(nation.Id);
                        mutatedNations?.Add(worstRival.Id);
                    }
                }
                else if (relation.Stance != DiplomaticStance.Truce && ActShortOfWar(campaign, nation, worstRival))
                {
                    mutatedNations?.Add(nation.Id);
                    mutatedNations?.Add(worstRival.Id);
                }
                yield break;
            }
        }
        }
        }
        finally
        {
            System.Buffers.ArrayPool<WeeklyPairPrecheck>.Shared.Return(slots, clearArray: true);
        }
        using (SimProfiler.Measure("  ai:diplomacy"))
        {

        // Frightened, and somebody large enough to be worth asking: ask for a guarantee.
        //
        // Not courtship. Courting a distant great power costs a week now and pays in five
        // months for a pact and two years for an alliance, which is past the end of most
        // campaigns - two versions of that were measured and both cost seventeen pacts and
        // bought nothing. A guarantee is the instrument that fits: it commits nothing until
        // somebody attacks, it is decided in one week rather than two years, and it makes
        // the country asking measurably less worth invading, because the war judgement
        // counts a guarantor exactly as it counts an ally.
        float? worstRivalStrength = null;
        float GetWorstRivalStrength() => worstRivalStrength ??= CampaignIntel.EstimatedStrength(campaign, nation.Id, worstRival);

        using (SimProfiler.Measure("    dip:shelter"))
        if (worstRival is not null && worstOpinion <= defines.AiRivalryThreshold &&
            GetWorstRivalStrength() >
            Strength(campaign, nation) * defines.AiCounterweightFear &&
            AskForShelter(campaign, nation, worstRival))
        {
            mutatedNations?.Add(nation.Id);
            yield break;
        }

        // Still frightened, and no patron came: buy the bully's peace outright. A weak
        // country cannot out-build the gap, but it can pay for a pact with the neighbour
        // it fears - the deal table's own rule that the strong side does not sell what it
        // could take, so the cheque only ever lands where the fear was real.
        // And only from somebody who could actually come - a neighbour, or a power that can put
        // an army ashore - and is a real danger rather than merely disliked. Canada was paying
        // Germany eight billion for a pact across the Atlantic.
        // Check the cheap refusal gates first. Naval feasibility performs real route
        // searches; an existing pact or an insufficient threat makes that work pointless.
        // The weekly opinion pass has already refreshed this rival's intelligence estimate.
        using (SimProfiler.Measure("    dip:buy_peace"))
        if (worstRival is not null && worstOpinion <= defines.AiRivalryThreshold &&
            nation.Treasury > 0.01f &&
            worstRivalRelation is { NonAggressionPact: false, Stance: not DiplomaticStance.AtWar } &&
            Strength(campaign, nation) < GetWorstRivalStrength() &&
            ThreatPosture.ThreatOf(campaign, nation, worstRival) >= defines.AiBuyPeaceThreat &&
            (neighbours.Contains(worstRival.MapIndex) || CanStageWar(campaign, worstRival, nation)) &&
            BuyPeace(campaign, nation, worstRival))
        {
            mutatedNations?.Add(nation.Id);
            mutatedNations?.Add(worstRival.Id);
            yield break;
        }

        // A week spent on our own people across the fence, before a week spent on being liked.
        // The cheque is the deal table's. A player on the other side of it still has to sign.
        using (SimProfiler.Measure("    dip:kindred"))
        if (TryBuyKindredLand(campaign, nation, out var kindredSellerId))
        {
            mutatedNations?.Add(nation.Id);
            if (kindredSellerId >= 0) mutatedNations?.Add(kindredSellerId);
            yield break;
        }

        // A client whose institutions are failing, after our own fear and our own people.
        // The cheque is the ordinary deal table. A player still has to sign.
        using (SimProfiler.Measure("    dip:crisis_grant"))
        if (TryOfferCrisisGrant(campaign, nation, out var grantClientId))
        {
            mutatedNations?.Add(nation.Id);
            if (grantClientId >= 0) mutatedNations?.Add(grantClientId);
            yield break;
        }

        // A scarce good that arrives from one partner, before a week spent only on being liked.
        // The paper is a trade agreement. A player who is asked still has to sign. No land moves.
        using (SimProfiler.Measure("    dip:supply_hedge"))
        if (TryHedgeSupply(campaign, nation, out var hedgedPartnerId))
        {
            mutatedNations?.Add(nation.Id);
            if (hedgedPartnerId >= 0) mutatedNations?.Add(hedgedPartnerId);
            yield break;
        }

        // Nothing has crossed a threshold yet, which is where every relationship starts:
        // neighbours dislike each other by default, so warmth has to be worked at. A
        // country spends the week courting whichever neighbour it is least unhappy with.
        // Left to drift alone, no two countries in the world would ever sign anything.
        using (SimProfiler.Measure("    dip:courting"))
        {
        var rivalTrigger = worstRival is not null &&
            (worstOpinion <= defines.AiRivalryThreshold ||
             (worstRivalRelation is not { Stance: DiplomaticStance.Allied } &&
              (TerritorialClaims.HoldsOurHomeland(campaign, nation, worstRival) ||
               LostLands.HasClaimedLand(campaign, nation, worstRival) ||
               BorderShape.Grievance(campaign, nation, worstRival) > 0.25f)));
        if (rivalTrigger)
        {
            var relation = worstRivalRelation ?? CampaignDiplomacy.Get(campaign, nation.Id, worstRival.Id);
            CampaignDiplomacy.RefreshExpiredTruce(campaign, relation);
            if (relation.Stance is DiplomaticStance.Neutral or DiplomaticStance.Friendly)
            {
                // A denunciation is a visible, remembered diplomatic action, not an
                // unexplained stance switch. It observes the same mission cooldown.
                var lastAction = relation.LastActionDay;
                new DenounceCommand { IssuerId = nation.Id, TargetId = worstRival.Id }.Apply(campaign);
                if (relation.LastActionDay != lastAction)
                {
                    Count("denounced");
                    mutatedNations?.Add(nation.Id);
                    mutatedNations?.Add(worstRival.Id);
                    yield break;
                }
            }

            if (relation.Stance != DiplomaticStance.Truce && ActShortOfWar(campaign, nation, worstRival))
            {
                mutatedNations?.Add(nation.Id);
                mutatedNations?.Add(worstRival.Id);
                yield break;
            }
        }

        if (bestFriend is not null && (worstRival is null || bestOpinion > defines.AiRivalryThreshold))
        {
            var relation = bestFriendRelation ?? CampaignDiplomacy.Get(campaign, nation.Id, bestFriend.Id);
            CampaignDiplomacy.RefreshExpiredTruce(campaign, relation);
            if (relation.Stance is DiplomaticStance.Neutral or DiplomaticStance.Friendly)
            {
                // Through the opinion system, not around it. This used to write the number
                // directly, and the weekly settle - which computes an opinion as the sum of
                // its named reasons - erased it every seven days. A country could court a
                // neighbour for a decade and end exactly where it started, which is why in
                // two years of a world running itself not one alliance was ever signed.
                new ImproveRelationsCommand { IssuerId = nation.Id, TargetId = bestFriend.Id }.Apply(campaign);
                mutatedNations?.Add(nation.Id);
                mutatedNations?.Add(bestFriend.Id);
                yield break;
            }
        }
        }
        }
    }

    /// <summary>
    /// Offer one cash grant to the ally or guarantor-client whose institutions are furthest
    /// gone. No land is on the table. An AI client answers by the deal table; a player
    /// gets the paper and nothing moves until they sign.
    /// </summary>
    internal static bool TryOfferCrisisGrant(Campaign campaign, Nation nation) =>
        TryOfferCrisisGrant(campaign, nation, out _);

    internal static bool TryOfferCrisisGrant(Campaign campaign, Nation nation, out int clientId)
    {
        clientId = -1;
        var defines = campaign?.Defines;
        if (defines is null || nation is null || campaign.IsHumanControlled(nation) || nation.Movement is not null) return false;
        if (!defines.AiCrisisGrants || !campaign.FeatureEnabled("deals")) return false;
        if (!float.IsFinite(nation.Stability) || nation.Stability < defines.CivilWarStabilityThreshold) return false;

        // Whether a client is failing is a fact about the client, and almost no country is.
        // It is asked first, and of a table of who is in a civil war: the pass used to look
        // every country in the world up among sixty-four thousand relations before asking it,
        // and then walk every live war for each - 0.12 ms a call, 62 ms over fourteen late
        // days, for a grant that is offered a few times a year. Both questions only read, so
        // their order is not part of the answer.
        var civilWars = _civilWarScratch ??= new List<StrategicWar>();
        civilWars.Clear();
        var nationCount = campaign.Nations.Count;
        if (_civilWarPartyScratch is null || _civilWarPartyScratch.Length < nationCount)
            _civilWarPartyScratch = new bool[nationCount + 64];
        var inCivilWar = _civilWarPartyScratch;
        Array.Clear(inCivilWar, 0, inCivilWar.Length);
        var liveWars = Warfare.LiveWars(campaign);
        for (var index = 0; index < liveWars.Count; index++)
        {
            var war = liveWars[index];
            if (war.Ended || !war.CivilConflict) continue;
            civilWars.Add(war);
            // Everybody StrategicWar.Involves answers yes for, marked once, so the walk below
            // asks an array instead of every civil war for every country.
            MarkCivilWarParty(inCivilWar, war.AttackerId);
            MarkCivilWarParty(inCivilWar, war.DefenderId);
            foreach (var ally in war.AttackerAllies) MarkCivilWarParty(inCivilWar, ally);
            foreach (var ally in war.DefenderAllies) MarkCivilWarParty(inCivilWar, ally);
        }

        Nation client = null;
        var worst = float.MaxValue;
        foreach (var other in campaign.Nations)
        {
            if (other.Id == nation.Id || other.Movement is not null || other.Population <= 0 || other.IsSubject) continue;
            if (!InstitutionsFailing(campaign, other, civilWars, inCivilWar) || !StandsBehind(campaign, nation, other)) continue;
            if (CampaignDiplomacy.AtWar(campaign, nation.Id, other.Id)) continue;
            if (nation.DecisionAvailableDay.Count > 0 &&
                nation.DecisionAvailableDay.GetValueOrDefault(CrisisGrantKey(other.Id)) > campaign.ElapsedDays) continue;
            if (other.Stability < worst || (other.Stability == worst && client is not null && other.Id < client.Id))
            {
                worst = other.Stability;
                client = other;
            }
        }
        if (client is null) return false;
        if (!Ultimatum.CanNegotiateDeal(campaign, nation, client, out _)) return false;
        var package = AgreementSystem.AidAmount(campaign, nation, client, out _);
        if (!float.IsFinite(package) || package < 0.01f) return false;
        var fiscal = AiFiscalPolicy.Assess(campaign, nation, defines.AiTreasuryFloor);
        if (!fiscal.CanSpend) return false;
        package = MathF.Min(package, fiscal.DiscretionaryCash);
        if (package < 0.01f) return false;

        var deal = new Ultimatum.Deal(new Ultimatum.Side { Cash = package }, new Ultimatum.Side());
        var outcome = DealNegotiations.Submit(campaign, nation, client, deal);
        var pending = false;
        for (var i = 0; i < campaign.DealOffers.Count; i++)
        {
            var offer = campaign.DealOffers[i];
            if (offer.Pending && offer.FromId == nation.Id && offer.ToId == client.Id) { pending = true; break; }
        }
        // Answered at once by the client player's standing rule: the grant was offered, so the
        // patron waits as it would after any answer instead of offering it again every week.
        if (!outcome.Accepted && !pending && !DealNegotiations.AnsweredByRuleToday(campaign, nation.Id, client.Id))
            return false;
        clientId = client.Id;
        nation.DecisionAvailableDay[CrisisGrantKey(client.Id)] =
            campaign.ElapsedDays + Math.Max(30, defines.AiCrisisGrantRetryDays);
        Count("crisis_grant");
        return true;
    }

    private static readonly string[] CrisisGrantKeys = BuildPrefixedIdKeys("crisis_grant/", 384);

    internal static string CrisisGrantKey(int clientId) =>
        (uint)clientId < (uint)CrisisGrantKeys.Length ? CrisisGrantKeys[clientId] : "crisis_grant/" + clientId;

    /// <summary>The live civil wars, gathered once for a pass that asks every country about them.</summary>
    [ThreadStatic] private static List<StrategicWar> _civilWarScratch;

    /// <summary>The same wars as a yes or no for each country id, for the same pass.</summary>
    [ThreadStatic] private static bool[] _civilWarPartyScratch;

    private static void MarkCivilWarParty(bool[] parties, int nationId)
    {
        if ((uint)nationId < (uint)parties.Length) parties[nationId] = true;
    }

    private static bool StandsBehind(Campaign campaign, Nation patron, Nation client)
    {
        var relation = CampaignDiplomacy.Peek(campaign, patron.Id, client.Id);
        if (relation is null || relation.Stance == DiplomaticStance.AtWar) return false;
        return relation.Stance == DiplomaticStance.Allied || relation.GuaranteedBy(patron.Id);
    }

    /// <summary>
    /// The same strain a civil war reads, including in a player's country when random
    /// outbreaks are switched off. A subject is somebody else's crisis.
    /// </summary>
    private static bool InstitutionsFailing(Campaign campaign, Nation client, List<StrategicWar> civilWars,
        bool[] inCivilWar)
    {
        if (client.IsSubject || campaign.Fallen.Contains(client.Id) || client.Population <= 0) return false;
        if ((uint)client.Id < (uint)inCivilWar.Length)
        {
            if (inCivilWar[client.Id]) return true;
        }
        else
        {
            // An id the array has no place for is asked the long way.
            for (var index = 0; index < civilWars.Count; index++)
                if (civilWars[index].Involves(client.Id)) return true;
        }
        var defines = campaign.Defines;
        if (!float.IsFinite(client.Stability) || client.Stability >= defines.CivilWarStabilityThreshold) return false;
        if (client.Population < defines.CivilWarMinimumPopulation) return false;
        var regions = 0;
        foreach (var state in campaign.StatesOf(client))
            if (state.Population > 0) regions++;
        return regions >= defines.CivilWarMinimumRegions;
    }

    // ---------------------------------------------------------------- economy ----------

    /// <summary>
    /// A month of governing. Nothing clever - a treasury that is emptying gets taxed, a
    /// country at war spends on its army, an angry country spends on its people, and
    /// whatever is left over is built with. It is the difference between a country that
    /// develops and one that is a fixed set of numbers with an army standing on it.
    /// </summary>
    private static void Economy(Campaign campaign, Nation nation, bool? nationInWar = null)
    {
        GovernTheBudget(campaign, nation);
        using (SimProfiler.Measure("    eco:cabinet")) Cabinet(campaign, nation, nationInWar);
    }


    /// <summary>
    /// One decision a month, if the country wants one enough.
    ///
    /// Decisions existed and no AI had ever taken one: two hundred and sixty governments
    /// sat on the same five levers for the whole campaign while the player pulled them.
    /// How much a country wants a decision is a scripted score - `ai_will_do` in the
    /// decision, resolved out of `common/scripted_modifiers` - so tuning an AI that
    /// mobilises too eagerly is a line in a file rather than a rebuild, and a mod's own
    /// decisions are weighed by the same rule as the shipped ones.
    ///
    /// A decision with no score declared is never taken. That is deliberate: an AI that
    /// takes anything a modder forgot to weigh is worse than one that waits to be told.
    ///
    /// This is the cabinet's monthly sitting, including for a player who handed it over
    /// (Delegation).
    /// </summary>
    public static void RunTheCabinet(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return;
        Cabinet(campaign, nation);
    }

    private static void Cabinet(Campaign campaign, Nation nation, bool? nationInWar = null)
    {
        if (!campaign.FeatureEnabled("decisions")) return;
        var bestScore = campaign.Defines.AiDecisionThreshold;
        var best = "";
        var decisions = DecisionCatalog.All;
        var hasCooldowns = nation.DecisionAvailableDay.Count > 0;
        var today = campaign.ElapsedDays;
        for (var di = 0; di < decisions.Count; di++)
        {
            var definition = decisions[di];
            if (string.IsNullOrEmpty(definition.AiWillDo)) continue;
            if (hasCooldowns && nation.DecisionAvailableDay.GetValueOrDefault(definition.Id) > today) continue;
            if (nation.Treasury < DecisionCatalog.Cost(nation, definition)) continue;
            if (!DecisionResources.CanAfford(nation, definition, keepAiReserve: true)) continue;
            if (DecisionCatalog.IsBlockedAfterCost(campaign, nation, definition, nationInWar)) continue;
            var score = ScriptLibrary.Score(campaign, nation, definition.AiWillDo);
            if (score <= bestScore) continue;
            bestScore = score;
            best = definition.Id;
        }
        if (best.Length == 0) return;
        new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = best }.Apply(campaign);
    }


    // ------------------------------------------------------------- war and peace -------

    /// <summary>
    /// Whether to start a war, and whether to end one. Both questions are asked of the
    /// same country on the same day, because a country that is already fighting should not
    /// be shopping for a second front.
    /// </summary>
    private static void Warmaking(Campaign campaign, Nation nation)
    {
        foreach (var _ in WarmakingStages(campaign, nation)) { }
    }

    private static IEnumerable<int> WarmakingStages(Campaign campaign, Nation nation, DueSlot slot = null,
        HashSet<int> mutatedNations = null, HashSet<long> mutatedPairs = null, bool worldInvalidated = false)
    {
        // Every war it is in, not just the first one in the world's list. A country fighting three
        // wars asked about whichever war the enumeration happened to reach first and never spoke
        // about the other two: the bench found them at minus twenty with LastNegotiation empty and
        // no offer ever made, for years, while the same country's first war was negotiated normally.
        // A snapshot, because a settlement can end a war and a list being read must not change.
        var live = Warfare.LiveWars(campaign);
        var negotiating = false;
        for (var index = 0; index < live.Count; index++)
        {
            if (live[index].Involves(nation.Id)) { negotiating = true; break; }
        }
        if (negotiating)
        {
            var open = TablesOf(campaign).WarmakingOpenScratch;
            open.Clear();
            for (var index = 0; index < live.Count; index++) open.Add(live[index]);
            for (var oi = 0; oi < open.Count; oi++)
            {
                if (oi > 0 && Campaign.StageOverdue()) yield return 0;
                var existing = open[oi];
                if (existing.Ended || !existing.Involves(nation.Id)) continue;
                // In trouble, ask somebody in before deciding whether to ask the enemy for terms.
                // Either can be a long piece for a country in a big war (StageCosts): announced,
                // it starts a frame instead of finishing one.
                if (StageCosts.Heavy("war:rescue", nation.Id)) { Campaign.HeavyNext(); yield return 0; }
                var rescueStarted = StageCosts.Start();
                using (SimProfiler.Measure("    war:rescue")) SeekRescue(campaign, nation, existing);
                StageCosts.Stop("war:rescue", nation.Id, rescueStarted);
                if (Campaign.StageOverdue()) yield return 0;
                if (!existing.Ended && existing.Involves(nation.Id))
                {
                    using var negotiateSteps = NegotiateStages(campaign, nation, existing).GetEnumerator();
                    while (true)
                    {
                        if (StageCosts.Heavy("war:negotiate", nation.Id)) { Campaign.HeavyNext(); yield return 0; }
                        var negotiateStarted = StageCosts.Start();
                        bool more;
                        using (SimProfiler.Measure("    war:negotiate")) more = negotiateSteps.MoveNext();
                        StageCosts.Stop("war:negotiate", nation.Id, negotiateStarted);
                        if (!more) break;
                        yield return 0;
                    }
                }
                if (Campaign.StageOverdue()) yield return 0;
            }
            mutatedNations?.Add(nation.Id);
            yield break;
        }
        if (!campaign.Defines.AiMayDeclareWar) yield break;
        var defines = campaign.Defines;
        var fiscal = AiFiscalPolicy.Assess(campaign, nation, defines.AiTreasuryFloor);
        if (!fiscal.CanSpend)
        {
            if (fiscal.Reason != AiFiscalReason.ShortRunway || nation.PaymentArrears > 0 || nation.Treasury < nation.Gdp * defines.AiTreasuryFloor * 0.25f)
            {
                Count(FiscalReasonKey(fiscal.Reason));
                yield break;
            }
        }

        // A partition agreed with allies is this week's business when it happens: the same
        // government cannot go shopping for a private war on top of carving up a neighbour.
        bool partitioned;
        using (SimProfiler.Measure("    war:partition")) partitioned = TryCoordinatePartition(campaign, nation);
        if (partitioned) { mutatedNations?.Add(nation.Id); yield break; }
        if (Campaign.StageOverdue()) yield return 0;

        // First, whatever we already decided to do about somebody. A country with a plan
        // spends its week on the plan rather than on shopping for a different war.
        var treasuryBeforePlan = nation.Treasury;
        var planBefore = nation.WarPlanTargetId;
        using (SimProfiler.Measure("    war:plan")) PursueWarPlan(campaign, nation);
        if (nation.Treasury != treasuryBeforePlan || nation.WarPlanTargetId != planBefore)
            mutatedNations?.Add(nation.Id);

        if (nation.WarSupport < defines.AiWarSupportFloor) yield break;
        if (nation.Stability < defines.AiWarStabilityFloor) yield break;

        Nation target = null;
        var bestJudgement = float.MinValue;
        var useSlot = !worldInvalidated && slot is not null && slot.HasWarmaking &&
                      (mutatedNations is null || !mutatedNations.Contains(nation.Id));
        if (useSlot)
        {
            var targets = slot.ReachableTargets;
            var judgements = slot.Judgements;
            var reachableCount = targets.Count;
            for (var ri = 0; ri < reachableCount; ri++)
            {
                if (ri > 0 && Campaign.StageOverdue()) yield return 0;
                var other = ByMapIndex(campaign, targets[ri]);
                if (other is null || other.Id == nation.Id) continue;
                Count("considered");
                if (campaign.IsHumanControlled(other) && !defines.AiMayAttackPlayer) continue;

                float judgement;
                var cachedJudgement = judgements[ri];
                if ((mutatedNations is not null && mutatedNations.Contains(other.Id)) ||
                    (mutatedPairs is not null && mutatedPairs.Contains(WeeklyPairKey(nation.Id, other.Id))))
                {
                    var allied = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id) is { Stance: DiplomaticStance.Allied };
                    if (!allied && !CampaignDiplomacy.CanDeclareWarQuiet(campaign, nation.Id, other.Id))
                    { Count("blocked"); continue; }
                    using (SimProfiler.Measure("    war:judge")) judgement = WarJudgement(campaign, nation, other);
                    var appetite = AnnexationAmbition.Appetite(campaign, nation, other);
                    if (appetite > 0f) judgement += defines.AiAnnexationTargetBonus * appetite;
                    if (allied && judgement <= 0f) { Count("blocked"); continue; }
                    if (!CampaignDiplomacy.SharesLandBorder(campaign, nation.MapIndex, other.MapIndex))
                        judgement -= defines.AiOverseasCaution;
                }
                else
                {
                    if (float.IsNaN(cachedJudgement)) continue;
                    if (float.IsNegativeInfinity(cachedJudgement)) { Count("blocked"); continue; }
                    judgement = cachedJudgement;
                }
                if (judgement > 0f) Count("wanted");
                if (!TerritorialClaims.PreferTarget(campaign, nation, other, judgement, target, bestJudgement)) continue;
                bool stageable;
                using (SimProfiler.Measure("    war:stageable")) stageable = judgement <= 0f || CanStageWar(campaign, nation, other);
                if (!stageable) { Count("unreachable_or_unready"); continue; }
                bestJudgement = judgement;
                target = other;
            }
        }
        else
        {
            IReadOnlyList<int> reachableList;
            using (SimProfiler.Measure("    war:reach")) reachableList = Reachable(campaign, nation);
            var reachableCount = reachableList.Count;
            if (reachableCount > 0)
            {
                var rentedTargets = System.Buffers.ArrayPool<int>.Shared.Rent(reachableCount);
                for (var ri = 0; ri < reachableCount; ri++) rentedTargets[ri] = reachableList[ri];
                try
                {
                    for (var ri = 0; ri < reachableCount; ri++)
                    {
                        if (ri > 0 && (ri % 6 == 0 || Campaign.StageOverdue())) yield return 0;
                        var mapIndex = rentedTargets[ri];
                        var other = ByMapIndex(campaign, mapIndex);
                        if (other is null || other.Id == nation.Id) continue;
                        Count("considered");
                        if (campaign.IsHumanControlled(other) && !defines.AiMayAttackPlayer) continue;

                        var allied = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id) is { Stance: DiplomaticStance.Allied };
                        if (!allied && !CampaignDiplomacy.CanDeclareWarQuiet(campaign, nation.Id, other.Id))
                        { Count("blocked"); continue; }

                        float judgement;
                        using (SimProfiler.Measure("    war:judge")) judgement = WarJudgement(campaign, nation, other);
                        var appetite = AnnexationAmbition.Appetite(campaign, nation, other);
                        if (appetite > 0f) judgement += defines.AiAnnexationTargetBonus * appetite;
                        if (allied && judgement <= 0f) { Count("blocked"); continue; }
                        if (!CampaignDiplomacy.SharesLandBorder(campaign, nation.MapIndex, other.MapIndex))
                            judgement -= defines.AiOverseasCaution;
                        if (judgement > 0f) Count("wanted");
                        if (!TerritorialClaims.PreferTarget(campaign, nation, other, judgement, target, bestJudgement)) continue;
                        bool stageable;
                        using (SimProfiler.Measure("    war:stageable")) stageable = judgement <= 0f || CanStageWar(campaign, nation, other);
                        if (!stageable) { Count("unreachable_or_unready"); continue; }
                        bestJudgement = judgement;
                        target = other;
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<int>.Shared.Return(rentedTargets);
                }
            }
        }
        if (target is null || bestJudgement <= 0f)
        {
            // Counted, because this is where the pair-level `wanted` becomes a nation-level
            // nothing: a country can judge several neighbours worth fighting and still leave the
            // loop with no target it would rather have than its current one. Without this line
            // the per-pair and per-nation counts cannot be reconciled at all, and "the AI wants
            // 104 wars and declares 2" reads as cowardice when part of it is arithmetic.
            Count("no_target");
            ClearWarPlan(nation);
            yield break;
        }

        // A country that can swallow this neighbour outright does not ask for one region
        // first. The aim is the whole state, and the peace writer is told so by the same
        // word the declaration carries.
        if (Campaign.StageOverdue()) yield return 0;
        var annexation = AnnexationAmbition.Aim(campaign, nation, target);

        // Worth having, but not yet worth having now. A government that reaches this point
        // does not forget the idea - it keeps the target, builds, and starts prising the
        // target's friends away. That is the whole of the difference between an AI that is
        // less aggressive and one that is simply slower.
        //
        // The threshold is not a fixed number, because a government is not a thermostat. It is
        // drawn from the country and the week, so it holds steady inside a week and is identical
        // on every machine playing this seed, and it is swung by that country's volatility: a
        // staid government's threshold barely moves, an ordinary one's moves by a quarter of the
        // swing, and a mercurial one's by nearly all of it. The swing is symmetric, so it does
        // not make the world more warlike - it makes it less schedulable, which is the thing a
        // player actually notices.
        var mercurial = 1f + (Roll(campaign.Seed, nation.Id, campaign.ElapsedDays / 7) - 0.5f) * 2f *
                        defines.AiVolatilitySwing * M.Clamp01(AiTemperament.Of(campaign, nation).Volatility);
        if (bestJudgement < defines.AiWarDesireThreshold * mercurial)
        {
            if (nation.WarPlanTargetId == target.Id) yield break;
            nation.WarPlanTargetId = target.Id;
            nation.WarPlanSinceDay = campaign.ElapsedDays;
            Count("planned");
            yield break;
        }
        Count("targeted");
        if (Campaign.StageOverdue()) yield return 0;
        bool prepared;
        using (SimProfiler.Measure("    war:prepared")) prepared = PreparationComplete(campaign, nation, target);
        if (!prepared) { Count("preparing"); yield break; }
        if (Campaign.StageOverdue()) yield return 0;

        // Even a country that wants a war does not start one on a particular Tuesday.
        // The die keeps the map from lurching every time a balance of power tips.
        //
        // It is drawn from the country and the date rather than from the campaign's shared
        // stream on purpose. Sharing it meant that adding or removing one AI decision
        // reshuffled every later roll in the world - so a change to how countries build
        // roads moved the Russo-Ukrainian front by twenty provinces, and no measurement of
        // one system could be trusted after a change to any other.
        var roll = Roll(campaign.Seed, nation.Id, campaign.ElapsedDays);
        // How readily this country actually moves, as opposed to how good its case is: the
        // doctrine's aggression multiplies the chance, so a cautious state waits for a better
        // hand and a reckless one acts on a worse one. Everything else - the cause, the
        // readiness, the reach and the ultimatum - still has to be in place before this line.
        //
        // Counted, because it was the one gate in the funnel with no tally, and the gap it
        // leaves is not small: a year reports wanted=104 against declared=2, and without this
        // line the difference cannot be attributed between the die, PreparationComplete and
        // the plan book - which is precisely the question AI-05 asks first.
        var prepDays = nation.WarPlanSinceDay >= 0 ? campaign.ElapsedDays - nation.WarPlanSinceDay : 0;
        var prepBonus = Math.Min(1.0f, Math.Max(0, prepDays - defines.AiWarPreparationDays) / 60f);
        var warChance = defines.AiWarChance * bestJudgement * AiStrategyCatalog.Of(campaign, nation).Aggression * (1.25f + prepBonus);
        if (roll > warChance)
        { Count("rolled_down"); yield break; }

        // An ultimatum threatens the same war, so it needs the same considered appetite,
        // preparation and seeded commitment. Previously this ran before all three gates:
        // every fresh world handed over the same weak neighbours on their first AI day,
        // even when the government would never have committed to the threatened war.
        // A player's own papers and the recipient's assessment are unchanged.
        // Roll passed: anything past this point (ultimatum, alliance break, war declaration)
        // can alter territories, cooldowns, relations, or wars, so mark both parties dirty.
        if (annexation is null)
        {
            bool asked;
            using (SimProfiler.Measure("    war:ultimatum")) asked = TryUltimatumFirst(campaign, nation, target);
            mutatedNations?.Add(nation.Id);
            mutatedNations?.Add(target.Id);
            if (Campaign.StageOverdue()) yield return 0;
            if (asked)
            {
                Count("asked_before_war");
                yield break;
            }
        }
        else
        {
            if (Campaign.StageOverdue()) yield return 0;
        }

        // A legitimate territorial demand may supply a cause if refused, but merely
        // preparing to threaten somebody still does not permit an unjustified declaration.
        if (Campaign.StageOverdue()) yield return 0;
        var casusBelli = Statecraft.CasusBelli(campaign, nation, target);
        var rumpTarget = AnnexationAmbition.IsConqueredRump(campaign, nation, target);
        var targetRel = CampaignDiplomacy.Peek(campaign, nation.Id, target.Id);
        var unfinishedClaim = targetRel is not null && OpinionSystem.Has(targetRel, "unfinished_war_claim");
        if (annexation is null && defines.AiNeedsCauseForWar &&
            string.IsNullOrEmpty(casusBelli) && !rumpTarget && !unfinishedClaim)
        {
            Count("no_cause");
            yield break;
        }

        // A country that has been preparing for this one goes sooner; the preparation is the
        // point of having made the plan.
        if (nation.WarPlanTargetId == target.Id) ClearWarPlan(nation);

        // Turning on an ally is done openly and in two acts: the promise ends, then the war
        // begins. Folding the break into the declaration would let a country attack a friend
        // while still shown as its friend, and would rob the second act of the thing that makes
        // it mean anything. Everybody watching learns what this signature is worth, and pays
        // attention to the next one this country signs.
        if (CampaignDiplomacy.Peek(campaign, nation.Id, target.Id) is { Stance: DiplomaticStance.Allied })
        {
            // Ending a friendship is not a thing to do and then discover was pointless. Breaking
            // the alliance clears the alliance and nothing else: a shared collective-defence
            // organisation still forbids the war whatever the two countries signed, and a
            // government that breaks first and finds that out afterwards has spent everybody's
            // opinion of it on a war it cannot start. Ask that one question before the act that
            // cannot be taken back.
            if (InternationalOrganizations.HasDefencePartner(campaign, nation.Id, target.Id))
            { Count("blocked"); yield break; }
            new BreakAllianceCommand { IssuerId = nation.Id, TargetId = target.Id }.Apply(campaign);
            mutatedNations?.Add(nation.Id);
            mutatedNations?.Add(target.Id);
            nation.Notoriety += defines.AiBetrayalNotoriety;
            Count("betrayed");
            // And whatever is left - a truce nobody remembered, a subject that answers to the
            // target - is still refused here rather than declared through.
            if (!CampaignDiplomacy.CanDeclareWar(campaign, nation.Id, target.Id, out _)) { Count("blocked"); yield break; }
        }

        if (Campaign.StageOverdue()) yield return 0;
        var recovery = TerritorialClaims.RecoveryGoal(campaign, nation, target);
        var cause = !string.IsNullOrEmpty(annexation?.Cause)
            ? annexation.Value.Cause
            : (recovery is not null ? "reconquest"
                : (!string.IsNullOrEmpty(casusBelli) ? casusBelli
                    : (rumpTarget ? "hegemony" : (unfinishedClaim ? "unfinished_war_claim" : null))));
        var claimed = recovery?.MapIndex ?? annexation?.ClaimedState ?? -1;
        if (claimed <= 0 && (rumpTarget || unfinishedClaim))
            claimed = AnnexationAmbition.ClaimedState(campaign, target);
        // A union is about the whole state, so its capital is the objective even when an
        // older recovery claim names one region. A border reconquest keeps its own goal.
        if (annexation is { Reunification: true } union) claimed = union.ClaimedState;
        if (CampaignDiplomacy.DeclareWar(campaign, nation.Id, target.Id, claimed, cause) is not null)
        {
            mutatedNations?.Add(nation.Id);
            mutatedNations?.Add(target.Id);
            Count(annexation is null ? "declared" : "annexation_declared");
        }
    }


    /// <summary>
    /// The demand before the war: one region of the target's on our border - our own people's
    /// if there is one, else its most populous - once a year at most for each target, and only
    /// to a government nobody plays and when we have no reason of our own already.
    /// </summary>
    [ThreadStatic] private static HashSet<int> _ultimatumLostScratch;

    private static readonly string[] UltimatumKeys = BuildPrefixedIdKeys("ultimatum/", 384);

    internal static string UltimatumKey(int targetId) =>
        (uint)targetId < (uint)UltimatumKeys.Length ? UltimatumKeys[targetId] : "ultimatum/" + targetId;

    internal static bool TryUltimatumFirst(Campaign campaign, Nation nation, Nation target)
    {
        var defines = campaign.Defines;
        if (!defines.AiUltimatumsBeforeWar) return false;
        if (Ultimatum.PendingBetween(campaign, nation.Id, target.Id)) return true;
        var cause = Statecraft.CasusBelli(campaign, nation, target);
        // A territorial claim is still negotiable. Only an already refused demand (or
        // another non-territorial cause) skips this last chance to settle without war.
        if (!string.IsNullOrEmpty(cause) && cause is not ("irredentism" or "reconquest")) return false;
        var key = UltimatumKey(target.Id);
        if (nation.DecisionAvailableDay.Count > 0 &&
            nation.DecisionAvailableDay.GetValueOrDefault(key) > campaign.ElapsedDays) return false;
        if (campaign.ProvinceMap is null) return false;

        // Only for ground there is a case for: ours once, or our own people's. China asking
        // Mongolia for Dornod because Mongolia could not say no is not a demand anybody makes.
        var lost = _ultimatumLostScratch ??= new HashSet<int>();
        LostLands.FillOfHolder(campaign, nation, target, lost);

        ScenarioState best = null;
        var bestScore = 0f;
        foreach (var state in campaign.StatesOf(target))
        {
            if (state.Population <= 0) continue;
            if (!Ultimatum.CanDemand(campaign, target, state.MapIndex)) continue;
            // Adjacency is a region-graph question, not a walk of all thirty-five thousand
            // provinces for every region of every neighbour, once a week for every country.
            if (!TerritorialCoherence.TouchesOwnedGround(campaign, nation, state.MapIndex)) continue;
            long ours = 0;
            foreach (var group in state.PopulationGroups)
                if (DemographyCatalog.IsPrimary(nation.Tag, group.Culture)) ours += group.Size;
            var kin = ours / (float)Math.Max(1, state.Population);
            if (kin < 0.5f && !lost.Contains(state.MapIndex)) continue;
            var score = state.Population * (1f + 4f * kin) * (lost.Contains(state.MapIndex) ? 2f : 1f);
            if (score <= bestScore) continue;
            bestScore = score;
            best = state;
        }
        if (best is null)
        {
            Count("ultimatum_no_region");
            return TryAnnexUltimatum(campaign, nation, target, key);
        }

        var demand = new Ultimatum.Demand { Regions = new[] { best.MapIndex } };
        if (campaign.IsHumanControlled(target))
        {
            if (!Ultimatum.ValidateTerms(campaign, nation, target, demand, out var humanReason))
            {
                Count("ultimatum_invalid");
                if (DiagnosticLogs.Enabled)
                    DiagnosticLogs.Write("ai", $"AI_ULTIMATUM_INVALID {nation.Tag}->{target.Tag} region={best.Id} reason={humanReason}");
                return false;
            }
            nation.DecisionAvailableDay[key] = campaign.ElapsedDays + Math.Max(30, defines.AiUltimatumIntervalDays);
            var delivered = Ultimatum.Deliver(campaign, nation, target, demand);
            if (!delivered.Accepted) return false;
            Count("ultimatum_delivered");
            return true;
        }
        var assessment = Ultimatum.Assess(campaign, nation, target, demand);
        if (!assessment.Valid)
        {
            Count("ultimatum_invalid");
            if (DiagnosticLogs.Enabled)
                DiagnosticLogs.Write("ai", $"AI_ULTIMATUM_INVALID {nation.Tag}->{target.Tag} region={best.Id} reason={assessment.Reason}");
            return false;
        }
        nation.DecisionAvailableDay[key] = campaign.ElapsedDays + Math.Max(30, defines.AiUltimatumIntervalDays);
        Ultimatum.Resolve(campaign, nation, target, demand, assessment.Response);
        Count(assessment.Response == Ultimatum.Answer.Yield ? "ultimatum_yielded" : "ultimatum_refused");
        if (DiagnosticLogs.Enabled)
            DiagnosticLogs.Write("ai", $"AI_ULTIMATUM {nation.Tag}->{target.Tag} region={best.Id} answer={assessment.Response}");
        return true;
    }

    /// <summary>
    /// One annexation, and only when the same assessment that answers a region would yield.
    /// A human is never swallowed here.
    /// </summary>
    static bool TryAnnexUltimatum(Campaign campaign, Nation nation, Nation target, string key)
    {
        if (campaign.IsHumanControlled(target)) return false;
        var demand = new Ultimatum.Demand { Annex = true };
        var assessment = Ultimatum.Assess(campaign, nation, target, demand);
        if (!assessment.Valid || assessment.Response != Ultimatum.Answer.Yield) return false;
        nation.DecisionAvailableDay[key] = campaign.ElapsedDays + Math.Max(30, campaign.Defines.AiUltimatumIntervalDays);
        Ultimatum.Resolve(campaign, nation, target, demand, Ultimatum.Answer.Yield);
        Count("ultimatum_yielded");
        if (DiagnosticLogs.Enabled)
            DiagnosticLogs.Write("ai", $"AI_ULTIMATUM {nation.Tag}->{target.Tag} annex answer=Yield");
        return true;
    }

    /// <summary>
    /// How much fight the other side of this war still has, as far as its own score lets us measure.
    ///
    /// The simulation does not read an opponent's internal morale on purpose, but the score is the
    /// measure of exactly that: a side a hundred points down has nothing left to hold out with. The
    /// constant that used to stand here (0,5, "their morale is not ours to read") was above every
    /// capitulation threshold, so the indemnity branch that tested it could never fire at any score.
    /// </summary>
    internal static float OpponentMoraleEstimate(StrategicWar war, int ourId)
    {
        if (war is null) return 1f;
        var lead = MathF.Max(0f, war.IsAttacking(ourId) ? war.WarScore : -war.WarScore);
        return M.Clamp01(1f - lead / 100f);
    }

    /// <summary>
    /// Whether this country has lost the war on points, whatever its untouched home front still
    /// says. Waiting for the home front to agree is how a defender at -100 sat out nine hundred
    /// days with the fighting long decided.
    /// </summary>
    internal static bool DecisivelyBeaten(Campaign campaign, StrategicWar war, int nationId)
    {
        if (campaign?.Defines is null || war is null || war.Ended) return false;
        var score = war.IsAttacking(nationId) ? war.WarScore : -war.WarScore;
        return score <= -campaign.Defines.AiDecisiveWarScore;
    }

    /// <summary>
    /// Whether a civil war that has stood still may be offered the status quo now.
    ///
    /// Civil wars wait longer than other wars before a deadlock is called: seven hundred and
    /// thirty days on top of the ordinary stalemate, and a government does not hand its own
    /// country back because the score says so. That is what kept a moving reconquest alive.
    /// Signing those on points cost the bench eleven of fifteen recoveries. The same wait left
    /// a war on the map whose status quo the signature would already accept, and nobody asked.
    ///
    /// Once the front has not moved and that acceptance already clears, the extra wait has
    /// nothing left to protect. A front that is still moving is not this, whatever the score.
    /// </summary>
    internal static bool CivilFrozenOfferReady(Campaign campaign, StrategicWar war, int nationId)
    {
        if (campaign?.Defines is null || war is null || war.Ended || !war.CivilConflict || !war.Stalled)
            return false;
        if (!war.Involves(nationId)) return false;
        if (campaign.ElapsedDays - war.StartedDay < campaign.Defines.AiStalemateDays) return false;
        return PeaceNegotiation.BlockReason(campaign, war, nationId, PeaceTerms.StatusQuo).Length == 0;
    }

    /// <summary>
    /// A country at war asks, periodically, whether it is done. Winning it writes a peace
    /// out of the ground it actually holds - through the same conference a player uses -
    /// and losing it offers to stop.
    /// </summary>
    internal static void Negotiate(Campaign campaign, Nation nation, StrategicWar war)
    {
        foreach (var _ in NegotiateStages(campaign, nation, war)) { }
    }

    private static IEnumerable<int> NegotiateStages(Campaign campaign, Nation nation, StrategicWar war)
    {
        var defines = campaign.Defines;
        // Overrun: one side's leader has lost all its ground in this war and nobody on that side
        // has a formation left. There is nothing to fight with or bargain with, so it is decided,
        // lock or no lock, and it is settled from whichever side sits down first - the beaten
        // country may have no people left in its hands, and a country with none never
        // negotiates. Such wars used to go on for years with the winner's army standing about on
        // a front that no longer existed: India held the whole of Siachen for two years with a
        // hundred and twenty divisions assigned to it, Germany all of Liechtenstein, Italy San
        // Marino. One province was not war score enough to write a treaty with, and the white
        // peace the beaten side could offer would have handed it back.
        if (campaign.Defines is { ForcePeaceOnTotalOccupationOrFullScore: true } && war.PendingOfferFromId < 0)
        {
            var attLeader = campaign.NationById(war.AttackerId);
            var defLeader = campaign.NationById(war.DefenderId);
            var attOcc = attLeader is not null && PeaceNegotiation.IsTotallyOccupiedInWar(campaign, war, attLeader);
            if (Campaign.StageOverdue()) yield return 0;
            var defOcc = defLeader is not null && PeaceNegotiation.IsTotallyOccupiedInWar(campaign, war, defLeader);
            if (Campaign.StageOverdue()) yield return 0;
            var fullScoreAttacker = war.WarScore >= 99.5f && (!war.CivilConflict || war.Stalled);
            var fullScoreDefender = war.WarScore <= -99.5f && (!war.CivilConflict || war.Stalled);

            if (attOcc || defOcc || fullScoreAttacker || fullScoreDefender)
            {
                var victor = (fullScoreAttacker || defOcc) ? attLeader : defLeader;
                var beaten = (victor == attLeader) ? defLeader : attLeader;

                if (victor is not null && beaten is not null)
                {
                    if (campaign.IsHumanControlled(victor))
                    {
                        PeaceOffers.Put(campaign, war, beaten, victor, PeaceTerms.Capitulate);
                        yield break;
                    }

                    var victorLedger = new PeaceLedger(campaign, war, victor.Id);
                    if (Campaign.StageOverdue()) yield return 0;
                    var decisiveDemand = new PeaceDemand { WarId = war.Id, ProposerId = victor.Id, Unconditional = true };
                    var hasDecisive = WriteDecisiveSettlement(campaign, victor, war, victorLedger, decisiveDemand);
                    if (Campaign.StageOverdue()) yield return 0;

                    if (hasDecisive && PeaceConference.BlockReason(campaign, war, decisiveDemand).Length == 0)
                    {
                        PeaceOffers.Put(campaign, war, victor, beaten, decisiveDemand);
                    }
                    else
                    {
                        PeaceOffers.Put(campaign, war, beaten, victor, PeaceTerms.Capitulate);
                    }
                    yield break;
                }
            }
        }

        if (war.PendingOfferFromId < 0 &&
            campaign.ElapsedDays - war.LastPeaceProposalDay >= defines.AiPeaceProposalInterval)
            for (var si = 0; si < 2; si++)
            {
                var attackers = si == 0;
                var overrun = SideOverrun(campaign, war, attackers);
                if (overrun)
                {
                    var beaten = campaign.NationById(attackers ? war.AttackerId : war.DefenderId);
                    var victor = campaign.NationById(attackers ? war.DefenderId : war.AttackerId);
                    if (beaten is null || victor is null) break;
                    PeaceOffers.Put(campaign, war, beaten, victor, PeaceTerms.Capitulate);
                    yield break;
                }
                if (Campaign.StageOverdue()) yield return 0;
            }
        // A breakaway beaten in the field gives itself up, lock or no lock: most of its ground
        // held by the state it left, or nobody left in it who believes in the war (Reintegration).
        if (war.PendingOfferFromId < 0 && Reintegration.ShouldSurrender(campaign, war, nation) &&
            campaign.NationById(war.EnemyLeaderOf(nation.Id)) is { } government)
        {
            PeaceOffers.Put(campaign, war, nation, government, PeaceTerms.Capitulate);
            yield break;
        }
        if (Campaign.StageOverdue()) yield return 0;
        // A decided war is negotiated whatever the scenario's opening lock says: the lock keeps the
        // wars on the map from ending in their first weeks, not a beaten country from surrendering.
        if (CivilWarPersistence.NegotiationLocked(campaign, war)) yield break;
        if (campaign.ElapsedDays - war.StartedDay < defines.AiMinimumWarDays) yield break;
        if (campaign.ElapsedDays - war.LastPeaceProposalDay < defines.AiPeaceProposalInterval) yield break;

        // Only the two war leaders negotiate the peace of the whole war; junior coalition members
        // leave via SeparatePeace and must never spend the war leader's score or sign away its conquests.
        if (nation.Id != war.AttackerId && nation.Id != war.DefenderId) yield break;

        var opponentId = war.EnemyLeaderOf(nation.Id);
        var opponent = campaign.NationById(opponentId);
        if (opponent is null) yield break;

        // A settlement already on their table is a settlement being thought about. Asking
        // again over the top of it is how a foreign ministry loses an argument it was
        // winning, and it is also how a player gets three modal dialogs in a row.
        if (war.PendingOfferFromId >= 0) yield break;

        // A side with a member of the war of the weak that would keep nothing by the line and
        // still fights on has no peace to put forward: every one it would write is the line as the
        // border, which leaves that member nothing (Factions.SideKeepsNothingByTheLine). Its war is
        // ended in the field, or by a white peace somebody else offers it.
        if (Factions.SideKeepsNothingByTheLine(campaign, war, war.IsAttacking(nation.Id))) yield break;
        if (Campaign.StageOverdue()) yield return 0;

        // Asked before the reclaiming return below. That return keeps a government fighting
        // for the rest of its own country, and it is also how a stalled civil war never put
        // a paper down: the side that was ahead left, and the side that was behind was still
        // inside the extra civil delay. The status quo is the term the acceptance already
        // priced. It is not a white peace, and it is not a surrender on points.
        if (CivilFrozenOfferReady(campaign, war, nation.Id))
        {
            PeaceOffers.Put(campaign, war, nation, opponent, PeaceTerms.StatusQuo);
            yield break;
        }
        if (Campaign.StageOverdue()) yield return 0;

        var budget = PeaceConference.Budget(campaign, war, nation.Id);
        if (budget < WarAims.PeaceFloor(campaign, nation))
        {
            // A state that is ahead against its own breakaway does not offer to stop: the
            // ground is its own, and a white peace would hand back what it has retaken.
            // Unless the war has dragged into an intractable multi-year stalemate, in which case
            // both parties realistically accept freezing the line of contact (frozen conflict / status-quo).
            var ownScore = war.IsAttacking(nation.Id) ? war.WarScore : -war.WarScore;
            var reclaiming = Reintegration.Reclaiming(campaign, war, nation.Id);
            var duration = campaign.ElapsedDays - war.StartedDay;
            var staleReclaim = reclaiming && (WarWeariness.TimeToStop(campaign, war, winning: false) ||
                                              (war.Stalled && duration >= defines.AiStalemateDays) ||
                                              (duration >= defines.AiWearyDays));
            if (ownScore > 0f && reclaiming && !staleReclaim) yield break;
            // Beaten on points, not merely tired. A government that is eighty points down has lost
            // the war by every measure its own army has, whatever its untouched home front still
            // says; waiting for its morale to fall is how a defender at -100 sat out nine hundred
            // days with the fighting long decided (WarWeariness.AiDecisiveWarScore).
            //
            // Not for a civil war: a state does not concede its own ground by points, and the
            // breakaway rules (Reintegration, CivilWarPersistence) already carry that case with
            // their own resolve. Ending those here cost the bench eleven of fifteen recoveries.
            if (!war.CivilConflict && DecisivelyBeaten(campaign, war, nation.Id) &&
                duration > defines.AiMinimumWarDays)
            {
                PeaceOffers.Put(campaign, war, nation, opponent, WarWeariness.StalemateTerms(campaign, war));
                yield break;
            }
            // Not winning - which is not the same as wanting to stop. A country only walks
            // away from a war it started or is defending when the country itself has had
            // enough, and that takes years rather than a bad season. Otherwise the map
            // settles every border it has within a year of the campaign opening.
            // Or when the war has stood still long enough, or simply gone on too long: then the
            // peace of a deadlock - the line as the border where anyone holds anything, a white
            // peace where nobody does (WarWeariness).
            if (nation.WarSupport < defines.AiCapitulationWarSupport &&
                campaign.ElapsedDays - war.StartedDay > defines.AiMinimumWarDays * 3)
                // A government tired of its civil war cannot ask the side that is winning it to
                // hand the ground back: it concedes the line, and the line is the de facto border.
                PeaceOffers.Put(campaign, war, nation, opponent,
                    // Any tired government, not only a civil war's: the line is the border. A white
                    // peace handed back everything the war had changed - Russia, worn down after three
                    // years, gave Ukraine back the Donbas it had held since 2014.
                    WarWeariness.StalemateTerms(campaign, war));
            else if (WarWeariness.TimeToStop(campaign, war, winning: false) || staleReclaim ||
                (nation.Parliament is { PeaceMandateActive: true } && campaign.ElapsedDays - war.StartedDay > defines.AiMinimumWarDays))
                PeaceOffers.Put(campaign, war, nation, opponent, WarWeariness.StalemateTerms(campaign, war));
            yield break;
        }

        // Winning. Write the peace a country would actually write.
        var ledger = new PeaceLedger(campaign, war, nation.Id);
        if (Campaign.StageOverdue()) yield return 0;
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = nation.Id };
        var decisive = WriteDecisiveSettlement(campaign, nation, war, ledger, demand);
        if (Campaign.StageOverdue()) yield return 0;
        // A state winning against its own breakaway is not selling it anything - an indemnity, a
        // client government - for the ground. It writes all of it back once the war is decided,
        // and until then it fights for the rest (Reintegration).
        if (!decisive && Reintegration.Reclaiming(campaign, war, nation.Id)) yield break;
        if (!decisive)
        {
            var treatyBudget = war.Justification is "irredentism" or "reconquest" or "hegemony" or "annexation" or "reunification" or "conquest" or "imperialism"
                ? ledger.Budget : ledger.Budget * defines.AiPeaceBudgetShare;
            foreach (var _ in WriteDemandStages(campaign, nation, war, ledger, demand, treatyBudget))
                yield return 0;
            if (Campaign.StageOverdue()) yield return 0;
            ConsiderSubjugation(campaign, nation, war, ledger, demand, treatyBudget);
            if (Campaign.StageOverdue()) yield return 0;
        }

        if (demand.Provinces.Count == 0 && demand.Subjugate.Length == 0)
        {
            // Ahead on points and holding none of their ground. A country in that position
            // does not stop - it pushes, because stopping converts a war it is winning into
            // nothing at all. Only a genuinely broken opponent is worth an indemnity - and
            // "genuinely broken" is not unknowable: the score is the measure of it. The constant
            // that stood here (0,5, "their morale is not ours to read") was above every
            // capitulation threshold, so the branch could never fire: a war won on points and
            // holding no ground could not be cashed in at all, it ran to nine hundred days.
            var opponentSupport = OpponentMoraleEstimate(war, nation.Id);
            if (ledger.Budget >= defines.AiPeaceIndemnityBudget &&
                opponentSupport <= defines.AiCapitulationWarSupport)
                PeaceOffers.Put(campaign, war, nation, opponent, PeaceTerms.WarReparations);
            // Ahead on points for years and holding nothing: it is over, and it is time to say so.
            else if (WarWeariness.TimeToStop(campaign, war, winning: true))
                PeaceOffers.Put(campaign, war, nation, opponent, WarWeariness.StalemateTerms(campaign, war));
            // Nothing to take and nothing moving. A front that has stood still for a year and a
            // half is the border whatever the escalation ladder thinks about giving up: waiting
            // for the weariness date is how a war ahead on points and holding nothing stayed on
            // the map for two years with no offer ever made.
            else if (war.Stalled && campaign.ElapsedDays - war.StartedDay >= defines.AiStalemateDays)
                PeaceOffers.Put(campaign, war, nation, opponent, WarWeariness.StalemateTerms(campaign, war));
            yield break;
        }

        // Pressing a war needs a front that can move. A demand that has stood unchanged on a front
        // that has not moved for a year and a half is not a plan, it is a war nobody is fighting:
        // the line is the answer, whatever the local balance says.
        var frozenFront = war.Stalled && campaign.ElapsedDays - war.StartedDay >= defines.AiStalemateDays;
        if (!decisive && !frozenFront)
        {
            if (Campaign.StageOverdue()) yield return 0;
            if (ShouldPressWarAim(campaign, nation, war, demand)) yield break;
        }
        if (Campaign.StageOverdue()) yield return 0;
        PeaceOffers.Put(campaign, war, nation, opponent, demand);
    }

    /// <summary>
    /// Whether one side of a war has been overrun: its leader lost ground in this war and holds
    /// none of its own anywhere, and no country on that side has a formation left. A government
    /// in exile with an army, or an ally still in the field, is fighting on and is not this.
    /// </summary>
    [ThreadStatic] private static HashSet<int> _overrunSideScratch;

    internal static bool SideOverrun(Campaign campaign, StrategicWar war, bool attackers)
    {
        var control = campaign.ProvinceController;
        var leader = campaign.NationById(attackers ? war.AttackerId : war.DefenderId);
        if (control is null || leader is null) return false;
        var own = (ushort)leader.MapIndex;
        var lostHere = false;
        foreach (var (_, was) in war.OriginalController)
            if (was == own) { lostHere = true; break; }
        if (!lostHere) return false;
        var side = _overrunSideScratch ??= new HashSet<int>();
        side.Clear();
        foreach (var id in war.Side(attackers)) side.Add(id);
        var armies = campaign.Armies;
        for (var i = 0; i < armies.Count; i++)
            if (side.Contains(armies[i].NationId)) return false;
        // Nor fighters who scattered and can still take the field (Partisans): a rising with
        // no formation on the map and a detachment's worth of rifles hidden is not overrun.
        foreach (var id in side)
            if (Partisans.DetachmentsInHiding(campaign, campaign.NationById(id)) > 0) return false;
        for (var province = 1; province < control.Length; province++)
            if (control[province] == own) return false;
        return true;
    }

    /// <summary>
    /// Whether to ask for the country instead of asking for its provinces.
    ///
    /// A winner that can afford to annex a country annexes it. This is the other case, and
    /// it is the commoner one: a war won against somebody too large to swallow, where the
    /// choice used to be a strip of border provinces or nothing. What a state actually did
    /// there was leave the country standing and take its decisions - and until the AI could
    /// reach that, every dependency in the game had to be typed into the console by hand.
    ///
    /// Only for a war that is not already eating them. Half a country annexed and the other
    /// half made to answer is not a settlement any foreign ministry has written; it is a
    /// player with a budget left over.
    /// </summary>
    internal static void ConsiderSubjugation(Campaign campaign, Nation nation, StrategicWar war,
        PeaceLedger ledger, PeaceDemand demand, float budget)
    {
        var loser = campaign.NationById(war.EnemyLeaderOf(nation.Id));
        if (loser is null || loser.IsSubject) return;
        // A civil war ends with one state, not with the government's own rebels as a client.
        if (WarAims.IsRebel(nation) || AnnexationAmbition.IsCivilWarBetween(nation, loser)) return;

        var topology = campaign.ProvinceMap;
        if (topology is null) return;
        var claimable = loser.MapIndex > 0
            ? (int)campaign.Caches.OwnedProvinceBitmaps.ForOwner(campaign, loser.MapIndex).Cardinality
            : 0;
        if (claimable == 0) return;
        var takenFromSubject = 0;
        var ptn = topology.ProvinceToNation;
        var loserMap = (ushort)loser.MapIndex;
        foreach (var province in demand.Provinces)
            if (ptn[province] == loserMap) takenFromSubject++;
        if (takenFromSubject * 2 >= claimable) return;

        var left = budget - ledger.Cost(demand);
        SubjectType best = null;
        var bestCost = 0f;
        foreach (var kind in SubjectCatalog.Imposable)
        {
            var cost = PeaceConference.SubjugationCost(campaign, nation, loser, kind.Id);
            if (cost > left || cost <= bestCost) continue;
            best = kind;
            bestCost = cost;
        }
        if (best is not null) demand.Subjugate = best.Id;
    }

    /// <summary>
    /// Write a peace out of the ground a country is standing on.
    ///
    /// The rule this replaces was "cheapest first", which is what a spreadsheet would do
    /// and nothing a country has ever done. It produced the same treaty every time: a
    /// scatter of the emptiest provinces on the map, chosen because empty ground is cheap,
    /// leaving the winner with a border like a torn edge and none of what it went to war
    /// for.
    ///
    /// Four things decide instead, in the order a foreign ministry would weigh them.
    ///
    /// The war goal comes first. A country that declared for a province and then annexed
    /// three others because they were going cheap did not win its war; Paradox games make
    /// the war goal the one thing the AI will always take, and so does this.
    ///
    /// Then contiguity. Ground that touches what you already hold is worth far more than
    /// the same ground in the middle of theirs, because an enclave has to be garrisoned
    /// forever and cannot be defended at all. A province touching nothing of yours is
    /// nearly refused outright rather than merely discouraged.
    ///
    /// Then finishing what you started. Half a region is the worst of both: the loser keeps
    /// a grievance and a salient, and the winner gets neither the whole nor a straight
    /// border. Each province already taken from a region makes the rest of it cheaper to
    /// want.
    ///
    /// And only then value for money - people per point of war score - which is what the
    /// old rule used alone.
    /// </summary>
    [ThreadStatic] private static HashSet<ushort> _demandLosersScratch;

    private static bool HasOccupiedClaimable(Campaign campaign, StrategicWar war, Nation nation,
        ProvinceTopology topology, ushort[] controllers)
    {
        var losers = _demandLosersScratch ??= new HashSet<ushort>();
        losers.Clear();
        foreach (var enemyId in war.EnemiesOf(nation.Id))
            if (campaign.NationById(enemyId) is { MapIndex: > 0 } enemy)
                losers.Add((ushort)enemy.MapIndex);
        if (losers.Count == 0) return false;
        var nationMap = (ushort)nation.MapIndex;
        var ptn = topology.ProvinceToNation;
        var limit = Math.Min(topology.ProvinceCount, controllers.Length - 1);
        for (var province = 1; province <= limit; province++)
        {
            if (controllers[province] == nationMap && losers.Contains(ptn[province]))
            {
                losers.Clear();
                return true;
            }
        }
        losers.Clear();
        return false;
    }

    internal static void WriteDemand(Campaign campaign, Nation nation, StrategicWar war,
        PeaceLedger ledger, PeaceDemand demand, float budget)
    {
        foreach (var _ in WriteDemandStages(campaign, nation, war, ledger, demand, budget)) { }
    }

    private static IEnumerable<int> WriteDemandStages(Campaign campaign, Nation nation, StrategicWar war,
        PeaceLedger ledger, PeaceDemand demand, float budget)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null || budget <= 0f) yield break;

        var anyCandidate = HasOccupiedClaimable(campaign, war, nation, topology, controllers);
        if (!anyCandidate)
        {
            // There is no enemy ground to take - the winner may be standing on ten provinces
            // and every one of them its own, retaken. That is a settlement with no land in
            // it, not the absence of a settlement: an indemnity, a truce and the rest are
            // still on the table and still paid for out of the same war score.
            //
            // Returning here without writing them was how a country that won a defensive war
            // signed a piece of paper with nothing on it.
            WriteTerms(campaign, nation, war, ledger, demand, budget);
            yield break;
        }
        if (Campaign.StageOverdue()) yield return 0;

        // Region by region, from the border outwards, and settled so neither side is left in
        // pieces (PeaceShape). The province-by-province greedy that stood here wrote treaties out
        // of wherever the front had stopped, then rounded them into whole regions - no cuts
        // inside a region, and a patchwork of regions dropped anywhere in the loser's country.
        PeaceShape.Write(campaign, nation, war, ledger, demand, budget);
        if (Campaign.StageOverdue()) yield return 0;

        WriteTerms(campaign, nation, war, ledger, demand, budget);
    }

    /// <summary>
    /// Spend what the land did not on the rest of the treaty.
    ///
    /// A settlement is not only a border. A winner with war score left over and nothing
    /// worth annexing with it was, until now, simply throwing it away - it signed for the
    /// provinces and walked out, leaving an indemnity it had earned on the table. These are
    /// taken in the order a foreign ministry would take them, which is not the order of
    /// their price.
    ///
    /// Money first, because an indemnity costs the winner nothing and is spent the day it
    /// arrives. Then a decade of quiet rather than five years, because a country that has
    /// just taken land is a country that expects to be asked for it back. Then trade, then
    /// their army - and humiliation last and only against an opponent already broken, since
    /// against anyone else it buys nothing but a grievance that outlives the treaty.
    /// </summary>
    private static void WriteTerms(Campaign campaign, Nation nation, StrategicWar war,
        PeaceLedger ledger, PeaceDemand demand, float budget)
    {
        var defines = campaign.Defines;
        var spent = ledger.Cost(demand);

        bool Afford(float price) => spent + price <= budget;

        if (Afford(defines.PeaceCostReparations))
        {
            demand.Reparations = true;
            spent += defines.PeaceCostReparations;
        }
        if (demand.Provinces.Count > 0 && WantsLongTruce(campaign, nation, war, demand) && Afford(defines.PeaceCostLongTruce))
        {
            demand.LongTruce = true;
            spent += defines.PeaceCostLongTruce;
        }
        if (Afford(defines.PeaceCostTradeConcessions))
        {
            demand.TradeConcessions = true;
            spent += defines.PeaceCostTradeConcessions;
        }
        if (Afford(defines.PeaceCostDemilitarise))
        {
            demand.Demilitarise = true;
            spent += defines.PeaceCostDemilitarise;
        }

        var opponentSupport = OpponentMoraleEstimate(war, nation.Id);
        if (opponentSupport <= defines.AiCapitulationWarSupport && Afford(defines.PeaceCostHumiliate))
            demand.Humiliate = true;
    }

    // ------------------------------------------------------------------ helpers --------

    /// <summary>
    /// What a country is worth in a fight: the divisions it has, weighted by what state
    /// they are in, plus what its economy could raise if the war lasted.
    ///
    /// Not only the AI's own decisions read it - whether an ally can afford to honour a
    /// promise is the same figure, and answering that with a different yardstick is how a
    /// guarantee starts meaning two things at once.
    /// </summary>
    public static float StrengthOf(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return 0;
        // Diplomacy/UI can query before the first AI tick or between commands. Never
        // read another campaign's daily cache, or a stale pre-settlement army total.
        var strength = nation.Gdp * campaign.Defines.AiEconomicWeight;
        foreach (var army in campaign.Armies)
            if (army.NationId == nation.Id)
                strength += ArmyStrengthTerm(army);
        return strength;
    }

    /// <summary>One formation's share of a country's strength. Shared by the daily table and
    /// the cold-cache fallback on purpose: the two answers must be the same number, or a
    /// client whose tables are cold reads a different army than the host whose are warm.</summary>
    internal static float ArmyStrengthTerm(Army army) =>
        army.Divisions * army.Strength * M.Max(.15f, army.Organisation) *
        // A warehouse war is a war: the same divisions count for less when the
        // depot is empty, and the staff pricing a neighbour reads that too.
        (0.62f + army.Equipment * 0.38f);

    /// <summary>A country's strength from today's cache when it is this campaign's, and counted afresh when it is not.</summary>
    internal static float CachedStrength(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return 0f;
        var tables = TablesOf(campaign);
        if (tables.Day == campaign.ElapsedDays &&
            tables.TryGetStrength(nation.Id, out var value)) return value;
        return StrengthOf(campaign, nation);
    }

    private static float Strength(Campaign campaign, Nation nation) => CachedStrength(campaign, nation);

    /// <summary>A nation by its map index, without walking the whole list to find it.</summary>
    private static Nation ByMapIndex(Campaign campaign, int mapIndex)
    {
        // Attrition asks before today's statecraft assessment. Looking up a neighbour
        // must not also pin strength, treaties and region counts: a restored client has
        // cold daily tables where the host still has yesterday's, so warming them here
        // froze different military figures on the two peers. Identity has its own index,
        // including countries founded since the last daily assessment.
        return campaign.NationsByMapIndex().GetValueOrDefault(mapIndex);
    }

    /// <summary>
    /// A deterministic value in [0, 1) for one country on one day. Same campaign, same
    /// answer, every time - and independent of everything else that draws randomness.
    /// </summary>
    private static float Roll(int seed, int nationId, int day)
    {
        var hash = (uint)seed * 2654435761u ^ (uint)nationId * 2246822519u ^ (uint)day * 3266489917u;
        hash ^= hash >> 15;
        hash *= 2246822519u;
        hash ^= hash >> 13;
        hash *= 3266489917u;
        hash ^= hash >> 16;
        return (hash >> 8) * (1f / 16777216f);
    }

    /// <summary>Whether anybody next door is at war, which is reason enough to spend.</summary>
    /// <summary>
    /// Ask somebody large to stand behind us against the country we are afraid of.
    ///
    /// Who is worth asking is not who is heaviest. Nepal, frightened of India, could reach
    /// China, the United States and Russia; the first version of this asked Russia, because
    /// Russia is the largest thing within reach - and Russia is on good terms with India and
    /// fighting a war of its own, so Russia said no, and the country that would have said
    /// yes was never asked. Measured, in a probe, after three guesses that were not.
    ///
    /// So the choice and the answer are one question. Each candidate is scored by what it
    /// would actually answer - what it thinks of us, whether it is steady, whether it is
    /// already fighting, and above all whether it is wary of the same country - and the
    /// request goes to the best of them, if that is good enough at all.
    /// </summary>
    [ThreadStatic] private static List<int> _shelterProtectorIdsScratch;
    [ThreadStatic] private static HashSet<int> _shelterAtWarScratch;

    internal static bool AskForShelter(Campaign campaign, Nation nation, Nation rival)
    {
        var defines = campaign.Defines;
        var rivalStrength = CampaignIntel.EstimatedStrength(campaign, nation.Id, rival);

        // Somebody already stands behind us against this: stop shopping.
        //
        // Without this a country asked again every week it was frightened, and since it only
        // ever skipped the ONE country it had already asked, it collected protectors: 483
        // guarantees stood in a world of 280 countries, and a world where everybody is
        // guaranteed is a world where nothing can be attacked - wars fell from 29 to 24 and
        // annexations from 61 to 38, which is not peace, it is a mechanic overreaching.
        var shelterProtectors = _shelterProtectorIdsScratch ??= new List<int>(8);
        campaign.Caches.Protectors.FillProtectorIdsReadOnly(campaign, nation.Id, shelterProtectors);
        for (var pi = 0; pi < shelterProtectors.Count; pi++)
        {
            var patron = campaign.NationById(shelterProtectors[pi]);
            if (patron is null) continue;
            if (CampaignIntel.EstimatedStrength(campaign, nation.Id, patron) > rivalStrength) return false;
        }

        // Its own circle, built here rather than read out of the shared one: this used to
        // read whatever the weekly pass had left in the static list, which is right exactly
        // when that pass is the caller and silently wrong the moment anybody else asks.
        BuildCircle(campaign, nation, rival);

        var atWar = _shelterAtWarScratch ??= new HashSet<int>();
        atWar.Clear();
        var live = Warfare.LiveWars(campaign);
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (war.Ended) continue;
            atWar.Add(war.AttackerId);
            atWar.Add(war.DefenderId);
            foreach (var allyId in war.AttackerAllies) atWar.Add(allyId);
            foreach (var allyId in war.DefenderAllies) atWar.Add(allyId);
        }

        Nation best = null;
        var bestAnswer = float.MinValue;
        var circleList = Circle;
        for (var ci = 0; ci < circleList.Count; ci++)
        {
            var other = circleList[ci];
            // A human decides whether their country guarantees somebody, so a plea to a player
            // is a request that waits for them (Guarantee.Ask) rather than a promise made on
            // their behalf - and it is not put to a player already holding as many as they
            // should. This used to skip players altogether, which meant the countries best able
            // to shelter anybody could never be asked.
            if (other.Id == rival.Id || other.Id == nation.Id || other.IsSubject) continue;
            if (campaign.IsHumanControlled(other) && !Guarantee.CanAskQuiet(campaign, nation, other)) continue;

            var ours = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
            if (ours is not null && (ours.GuaranteedBy(other.Id) || ours.Stance == DiplomaticStance.AtWar)) continue;
            // A country you merely mistrust is a country you can still ask; one you have
            // named a rival is not.
            if (ours is { Stance: DiplomaticStance.Rival }) continue;

            var theirs = CampaignDiplomacy.Peek(campaign, other.Id, rival.Id);
            if (theirs is { Stance: DiplomaticStance.Allied }) continue;
            if (CampaignIntel.EstimatedStrength(campaign, nation.Id, other) <= rivalStrength) continue;

            // What they would say. Their opinion of us counts for half of what it would in
            // an alliance - a great power does not shelter a small country because it is
            // fond of it - and what the shelter is against counts for most of it.
            var answer = (ours?.Opinion ?? 0f) / 200f + M.Clamp(other.Stability - 0.5f, -0.3f, 0.3f);
            if (theirs is not null && theirs.Opinion <= defines.AiRivalryThreshold)
                answer += defines.AiShelterSharedRival;
            if (atWar.Contains(other.Id))
            {
                // Not a refusal: a guarantee commits nothing today, and a power already
                // fighting is if anything more interested in who else is drawn in. It
                // makes them harder to ask, not impossible.
                answer -= defines.AiShelterWarPenalty;
            }

            if (answer <= bestAnswer) continue;
            bestAnswer = answer;
            best = other;
        }

        if (best is null || bestAnswer < defines.AiShelterWillingness) return false;

        if (campaign.IsHumanControlled(best))
        {
            // The question is the move. Nothing is promised on a player's behalf; it lands on
            // their table, it is answered there or it lapses, and the country that asked has
            // still spent its week asking (Guarantee.Ask bounds the repeat).
            Guarantee.Ask(campaign, nation, best, out _);
            if (CampaignDiplomacy.Peek(campaign, nation.Id, best.Id) is not { } asked ||
                !Guarantee.Pending(campaign, asked)) return false;
            Count("shelter_asked");
            if (DiagnosticLogs.Enabled)
                DiagnosticLogs.Write("simulation",
                    $"SHELTER_ASK {nation.Tag} asks {best.Tag} against {rival.Tag} " +
                    $"answer={bestAnswer:0.00} day={campaign.ElapsedDays}");
            return true;
        }

        new GuaranteeCommand { IssuerId = best.Id, TargetId = nation.Id, Guarantee = true }
            .Apply(campaign);
        Count("shelter_granted");
        CampaignDiplomacy.Get(campaign, nation.Id, best.Id).LastActionDay = campaign.ElapsedDays;
        if (DiagnosticLogs.Enabled)
            DiagnosticLogs.Write("simulation",
                $"SHELTER {best.Tag} guarantees {nation.Tag} against {rival.Tag} " +
                $"answer={bestAnswer:0.00} day={campaign.ElapsedDays}");
        return true;
    }

    /// <summary>
    /// Buy a neighbour's peace with the treasury instead of waiting for the war.
    ///
    /// A weak country beside a strong one has one currency the stronger side values: cash.
    /// It offers a slice of its treasury for a pact, and the answer comes from the same
    /// deal table the player bargains on - the strong side does not sell a peace it could
    /// take, so the offer only ever lands where fear or fondness already did the work.
    /// Returns whether the bargain was struck.
    /// </summary>
    internal static bool BuyPeace(Campaign campaign, Nation buyer, Nation seller)
    {
        if (campaign is null || buyer is null || seller is null || buyer == seller) return false;
        if (campaign.IsHumanControlled(buyer) || !MechanicFeatures.SystemEnabled(campaign, nameof(AiStatecraft))) return false;
        if (!Ultimatum.CanNegotiateDeal(campaign, buyer, seller, out _)) return false;
        var relation = CampaignDiplomacy.Peek(campaign, buyer.Id, seller.Id);
        if (relation is null || relation.NonAggressionPact || relation.Stance == DiplomaticStance.AtWar) return false;
        if (buyer.DecisionAvailableDay.Count > 0 &&
            buyer.DecisionAvailableDay.GetValueOrDefault(BuyPeaceKey(seller.Id)) > campaign.ElapsedDays) return false;

        // The strong side does not buy what it could take for free: the offer only makes
        // sense from the weaker chair, and only while something is still worth defending.
        var mine = Strength(campaign, buyer);
        var theirs = CampaignIntel.EstimatedStrength(campaign, buyer.Id, seller);
        if (mine >= theirs) return false;

        var fiscal = AiFiscalPolicy.Assess(campaign, buyer, campaign.Defines.AiTreasuryFloor);
        var offer = DiplomacyRulesHook.BuyPeaceOffer(buyer.Treasury, fiscal.DiscretionaryCash, mine, theirs);
        if (offer <= 0f) return false;

        var command = new ProposeDealCommand
        {
            IssuerId = buyer.Id, ToId = seller.Id, FromCash = offer, FromNonAggression = true
        };
        command.Apply(campaign);
        if (command.Outcome.PendingOfferId > 0)
        {
            Count("peace_offered");
            return true; // This week's action was delivery, not a signed pact.
        }
        if (!command.Outcome.Accepted)
        {
            // A player's standing rule answered it at delivery. A waiting paper kept the buyer from
            // asking again for as long as it lay on the table; an answer at once must keep it as
            // long, or the same cheque goes out and is turned down every week.
            if (!DealNegotiations.AnsweredByRuleToday(campaign, buyer.Id, seller.Id)) return false;
            buyer.DecisionAvailableDay[BuyPeaceKey(seller.Id)] =
                campaign.ElapsedDays + Math.Clamp(campaign.Defines.DealOfferDays, 1, 365);
            Count("peace_offered");
            return true;
        }
        Count("peace_bought");
        if (DiagnosticLogs.Enabled)
            DiagnosticLogs.Write("simulation",
                $"DEAL {buyer.Tag} buys a pact from {seller.Tag} for {offer:0} day={campaign.ElapsedDays}");
        return true;
    }

    private static readonly string[] BuyPeaceKeys = BuildPrefixedIdKeys("buy_peace/", 384);

    internal static string BuyPeaceKey(int sellerId) =>
        (uint)sellerId < (uint)BuyPeaceKeys.Length ? BuyPeaceKeys[sellerId] : "buy_peace/" + sellerId;

    internal static bool Threatened(Campaign campaign, Nation nation)
    {
        // The live wars are one list for the whole question, asked once rather than once a neighbour.
        var live = Warfare.LiveWars(campaign);
        var neighbours = NeighboursOf(campaign, nation.MapIndex);
        if (neighbours is HashSet<int> neighbourSet)
        {
            foreach (var mapIndex in neighbourSet)
                if (ThreatenedBy(campaign, nation, mapIndex, live)) return true;
            return false;
        }
        foreach (var mapIndex in neighbours)
            if (ThreatenedBy(campaign, nation, mapIndex, live)) return true;
        return false;
    }

    private static bool ThreatenedBy(Campaign campaign, Nation nation, int mapIndex, IReadOnlyList<StrategicWar> live)
    {
        var other = ByMapIndex(campaign, mapIndex);
        if (other is null) return false;
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (!war.Ended && war.Involves(other.Id)) return true;
        }
        return CampaignDiplomacy.Peek(campaign, nation.Id, other.Id)?.Stance == DiplomaticStance.Rival;
    }

    // ------------------------------------------------------- coordinated partition ------------

    /// <summary>
    /// What allied great powers did in every imperial age and what the post-UN world
    /// outlawed: agree on a weak common neighbour and divide it by simultaneous demands,
    /// each taking the slice that touches its own ground.
    ///
    /// No new instrument: every slice is the existing ultimatum path (Assess, then
    /// Resolve), and "they cannot yield what they do not hold" makes the same-day demands
    /// disjoint by themselves - what one taker received, the next cannot ask for. The
    /// victim either yields to each demander in turn (a partition without a war, which is
    /// what Poland got) or refuses each and hands them all a reason (a partition by war,
    /// which is also what Poland got, a century earlier). Assess counts the demander alone
    /// against the victim and its defenders, so what makes the demands land is the
    /// coalition gate below, not a new arithmetic.
    ///
    /// Off in the shipped scenario, because a treaty partition of a sovereign is a pre-UN
    /// norm and the base world is 2024: <see cref="Defines.AiMayCoordinatePartitions"/>.
    /// A mod for an imperial age switches it on with one row of data, which is exactly the
    /// room-for-modders rule this repository runs on. The same switch lights the player's button
    /// (PartitionCoordination.ProposePartitionCommand), because a rule only the computer may use
    /// is not a rule.
    /// </summary>
    [ThreadStatic] private static List<Nation> _partitionCoalition;
    [ThreadStatic] private static HashSet<int> _partitionCoalitionIds;

    internal static bool TryCoordinatePartition(Campaign campaign, Nation nation)
    {
        var defines = campaign.Defines;
        if (!defines.AiMayCoordinatePartitions) return false;
        if (campaign.IsHumanControlled(nation) || nation.Movement is not null) return false;
        if (nation.DecisionAvailableDay.GetValueOrDefault("partition") > campaign.ElapsedDays) return false;

        var minDemanders = M.Max(2, defines.AiPartitionMinimumDemanders);

        // The coalition: ourselves and every allied government the computer plays. A human
        // ally counts towards the weight of the agreement but is not handed a slice - a human's
        // demands are their own to make (PartitionCoordination.ProposePartitionCommand), so the
        // instrument is one rule with two callers and not two rules.
        var coalition = _partitionCoalition ??= new List<Nation>();
        var coalitionIds = _partitionCoalitionIds ??= new HashSet<int>();
        coalition.Clear();
        coalitionIds.Clear();
        coalition.Add(nation);
        coalitionIds.Add(nation.Id);
        var aiMembers = 1;
        var nations = campaign.ActiveNations;
        for (var i = 0; i < nations.Count; i++)
        {
            var other = nations[i];
            if (other is null || other.Id == nation.Id || other.Movement is not null) continue;
            if (CampaignDiplomacy.Peek(campaign, nation.Id, other.Id) is { Stance: DiplomaticStance.Allied })
            {
                coalition.Add(other);
                coalitionIds.Add(other.Id);
                if (!campaign.IsHumanControlled(other)) aiMembers++;
            }
        }
        if (aiMembers < minDemanders) return false;

        var coalitionStrength = 0f;
        for (var i = 0; i < coalition.Count; i++) coalitionStrength += StrengthOf(campaign, coalition[i]);
        var strengthMultiple = M.Max(1f, defines.AiPartitionStrengthMultiple);

        Nation victim = null;
        var bestTakers = 0;
        for (var ci = 0; ci < nations.Count; ci++)
        {
            var candidate = nations[ci];
            if (candidate is null || candidate.Movement is not null) continue;
            if (coalitionIds.Contains(candidate.Id) || !campaign.HasOwnedLand(candidate)) continue;

            var takers = 0;
            for (var mi = 0; mi < coalition.Count; mi++)
            {
                var member = coalition[mi];
                if (!campaign.IsHumanControlled(member) && CampaignDiplomacy.SharesLandBorder(campaign, member.MapIndex, candidate.MapIndex))
                    takers++;
            }
            if (takers < minDemanders || takers <= bestTakers) continue;
            if (coalitionStrength < StrengthOf(campaign, candidate) * strengthMultiple) continue;

            // Nobody in the coalition may be the victim's friend or already fighting it:
            // an ally is not for dividing, and a war is conquest, not a partition.
            var disqualified = false;
            for (var mi = 0; mi < coalition.Count; mi++)
            {
                var bond = CampaignDiplomacy.Peek(campaign, coalition[mi].Id, candidate.Id);
                if (bond is { Stance: DiplomaticStance.Allied } or { Stance: DiplomaticStance.AtWar }) { disqualified = true; break; }
            }
            if (disqualified) continue;

            // And nobody outside it may stand behind the victim - an ally or a guarantor
            // turns a carve-up into a general war, which is not what was proposed.
            for (var oi = 0; oi < nations.Count; oi++)
            {
                var other = nations[oi];
                if (other is null || other.Id == candidate.Id || coalitionIds.Contains(other.Id)) continue;
                var bond = CampaignDiplomacy.Peek(campaign, other.Id, candidate.Id);
                if (bond is { Stance: DiplomaticStance.Allied } || (bond is not null && bond.GuaranteedBy(other.Id)))
                { disqualified = true; break; }
            }
            if (disqualified) continue;

            bestTakers = takers;
            victim = candidate;
        }
        if (victim is null) return false;

        // A government does not carve up a neighbour every time it could. The die is the
        // campaign's own: same seed, same day, same answer on every machine.
        nation.DecisionAvailableDay[PartitionCoordination.CadenceKey] =
            campaign.ElapsedDays + M.Max(7, defines.AiPartitionCadenceDays);
        Count("partition_attempted");
        if (!PartitionAppetiteFavours(campaign, nation)) return false;

        var slices = 0;
        for (var mi = 0; mi < coalition.Count; mi++)
        {
            var member = coalition[mi];
            if (campaign.IsHumanControlled(member)) continue;
            if (!CampaignDiplomacy.SharesLandBorder(campaign, member.MapIndex, victim.MapIndex)) continue;
            if (PartitionDemand(campaign, member, victim)) slices++;
        }
        if (slices < 2) return false;
        Count("partition_coordinated");
        if (DiagnosticLogs.Enabled)
            DiagnosticLogs.Write("ai",
                $"AI_PARTITION coalition={coalition.Count} slices={slices} victim={victim.Tag} day={campaign.ElapsedDays}");
        return true;
    }

    /// <summary>
    /// The die a government throws before it agrees to carve up a neighbour.
    ///
    /// Thrown for itself when it is the one proposing, and thrown again for every ally a player's
    /// proposal puts to - one place, so that an invitation from a human is answered on the same
    /// arithmetic the computer uses for its own plans and not on a rule written later beside it.
    /// Same campaign, same day, same answer on every machine.
    /// </summary>
    internal static bool PartitionAppetiteFavours(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return false;
        return Roll(campaign.Seed, nation.Id, campaign.ElapsedDays) <
            M.Clamp(campaign.Defines.AiPartitionAppetite, 0f, 1f);
    }

    /// <summary>
    /// One taker's slice: the most populous of the victim's regions that touches the
    /// taker's own ground and can still be demanded. No case is required - the agreement
    /// is the case, and the notoriety of a demand nobody can justify is priced by the same
    /// ultimatum path as any other. The slice is chosen by PartitionCoordination, the player's
    /// half of the same instrument, because a distinct rule for a human's slice would be a
    /// second partition.
    /// </summary>
    internal static bool PartitionDemand(Campaign campaign, Nation member, Nation victim)
    {
        if (Ultimatum.PendingBetween(campaign, member.Id, victim.Id)) return false;
        var key = UltimatumKey(victim.Id);
        if (member.DecisionAvailableDay.Count > 0 &&
            member.DecisionAvailableDay.GetValueOrDefault(key) > campaign.ElapsedDays) return false;
        var best = PartitionCoordination.Slice(campaign, member, victim);
        if (best is null) return false;

        var demand = new Ultimatum.Demand { Regions = new[] { best.MapIndex } };
        // A player is never answered for: the demand is delivered rather than assessed, so a
        // victim who plays keeps the same right to refuse that a computer government exercises
        // here. This used to be the one hole in the instrument - an AI coalition could divide
        // every neighbour except one with a human in it.
        if (campaign.IsHumanControlled(victim))
        {
            if (!Ultimatum.ValidateTerms(campaign, member, victim, demand, out _)) return false;
            member.DecisionAvailableDay[key] = campaign.ElapsedDays + M.Max(30, campaign.Defines.AiUltimatumIntervalDays);
            if (!Ultimatum.Deliver(campaign, member, victim, demand).Accepted) return false;
            Count("partition_slice_delivered");
            return true;
        }
        var assessment = Ultimatum.Assess(campaign, member, victim, demand);
        if (!assessment.Valid) return false;
        member.DecisionAvailableDay[key] = campaign.ElapsedDays + M.Max(30, campaign.Defines.AiUltimatumIntervalDays);
        Ultimatum.Resolve(campaign, member, victim, demand, assessment.Response);
        Count(assessment.Response == Ultimatum.Answer.Yield ? "partition_slice_yielded" : "partition_slice_refused");
        return true;
    }
}

