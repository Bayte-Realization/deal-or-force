using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ZeroAlloc.Collections;

namespace DealOrForce.Simulation;

/// <summary>
/// The demographic cycle: how a population grows old, moves and changes, and what
/// that does to everything else.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// FOR MODDERS — what the cycle does and where to bend it:
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// Every region has three cohorts — young, working, old — advanced yearly:
///
///   YOUNG → WORKING  at MaturationAnnual (default 5.5% a year — ~18 years of childhood)
///   WORKING → OLD    at AgeingAnnual (default 1.8% — ~55 years of working life)
///
/// BIRTHS scale with how the young share compares to the reference (28%): more young
/// than that, more births; fewer, fewer. FERTILITY by the F# kernel DemographyRules.
///
/// DEATHS scale with how the old share compares to its reference (10%): more old
/// than that, more deaths — and harder, at the MortalityOldWeight.
///
/// MIGRATION moves people between regions (and countries) by living standard,
/// security and brain-drain factors. A flood is more than FloodShare of a
/// population in a year; it destabilises both ends.
///
/// ASSIMILATION: a minority adopts the culture around it at AssimilationAnnual
/// (default 0.6% a year — generations, not news cycles), weighed by kinship,
/// size, schooling, a kin state, and the country's policy (see the laws).
///
/// WHAT TO CHANGE:
///   • Faster ageing world → raise AgeingAnnual to 0.025 (retirement at ~40).
///   • Population boom → lower ReferenceYoungShare to 0.22 and raise fertility weight.
///   • Rapid assimilation → raise AssimilationAnnual to 0.02 (a generation).
///   • Age structure off entirely → `DemographyAgeStructureEnabled` to false:
///     old saves load with nobody young, nobody old, and the cycle stays quiet.
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
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

    /// <summary>
    /// Share of a minority who adopt the culture around them in a year, before kinship, size,
    /// schooling, a kin state and the country's policy are weighed (DemographicCycle.Assimilate).
    /// </summary>
    public float DemographyAssimilationAnnual { get; set; } = 0.006f;

    /// <summary>
    /// The share of a region the country's own people must be for minorities there to adopt the
    /// country's culture rather than the region's majority's.
    /// </summary>
    public float DemographyAssimilationNationalShare { get; set; } = 0.10f;

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
        state is null ? 0.62f : DemographyRulesHook.WorkingShare(state.YoungShare, state.OldShare);

    /// <summary>
    /// A country's own newborns: the further the young share has fallen below the reference, the
    /// fewer of them there are. 1 at the reference and above, never below a third.
    /// </summary>
    public static float FertilityFactor(ScenarioState state) =>
        state is null ? 1f : DemographyRulesHook.FertilityFactor(state.YoungShare);

    /// <summary>
    /// A country's own deaths: an old population dies at a higher rate even where medicine is good,
    /// because medicine does not make anybody younger. 1 at the reference and below.
    /// </summary>
    public static float MortalityFactor(ScenarioState state, Defines defines) =>
        state is null || defines is null ? 1f :
            DemographyRulesHook.MortalityFactor(state.OldShare, defines.DemographyReferenceOldShare, defines.DemographyMortalityOldWeight);

    /// <summary>
    /// Once per household review, region by region: the bins move. Piggybacking on that review is
    /// deliberate - its births and deaths are the figures this moves on, and a second clock of its
    /// own would drift away from them.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in Stages(campaign)) { }
    }

    // A bounded step checks its time budget between batches. The synchronous path can
    // use larger batches to spread the worker hand-off cost over more regions.
    private const int StageRegions = 64;
    private const int UnboundedStageRegions = 256;
    [ThreadStatic] private static List<ScenarioState> _dueStatesScratch;
    [ThreadStatic] private static CohortBatchScratch _cohortScratch;

    private sealed class CohortBatchScratch
    {
        public ScenarioState[] States = Array.Empty<ScenarioState>();
        public float[] Population = Array.Empty<float>();
        public long[] Births = Array.Empty<long>();
        public long[] Deaths = Array.Empty<long>();
        public float[] Young = Array.Empty<float>();
        public float[] Old = Array.Empty<float>();
        public float[] OutYoung = Array.Empty<float>();
        public float[] OutOld = Array.Empty<float>();
        public float[] OutWorking = Array.Empty<float>();

        public void EnsureCapacity(int count)
        {
            if (States.Length >= count) return;
            var cap = Math.Max(count, States.Length == 0 ? 64 : States.Length * 2);
            States = new ScenarioState[cap];
            Population = new float[cap];
            Births = new long[cap];
            Deaths = new long[cap];
            Young = new float[cap];
            Old = new float[cap];
            OutYoung = new float[cap];
            OutOld = new float[cap];
            OutWorking = new float[cap];
        }
    }

    /// <summary>
    /// The cycle in stages: the same regions in the same order, a pause when a batch crosses frame budget.
    /// </summary>
    public static IEnumerable<int> Stages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(DemographicCycle))) yield break;
        var defines = campaign.Defines;
        if (!defines.DemographyAgeStructureEnabled) yield break;
        var interval = Math.Clamp(defines.EconomyTickDays, 1, 365) / 365f;
        var states = campaign.States;
        var stateCount = states.Count;
        var dueStates = _dueStatesScratch ??= new List<ScenarioState>(512);
        dueStates.Clear();
        for (var index = 0; index < stateCount; index++)
        {
            var state = states[index];
            if (state is not null && !state.Unclaimed && state.Population > 0 &&
                state.LastDemographyDay != state.LastHouseholdReviewDay)
            {
                dueStates.Add(state);
            }
        }
        if (dueStates.Count == 0) yield break;

        campaign.NationsByMapIndex();
        if (campaign.Nations.Count > 0) campaign.HasOwnedLand(campaign.Nations[0]);
        var kinTable = KinTable(campaign);
        var nationsByMap = campaign.NationsByMapIndex();
        var shield = PopulationFolding.Enabled(campaign) ? PopulationFolding.Prepare(campaign) : null;
        var useParallel = DeterministicParallel.Enabled(campaign);
        var anyChanged = false;
        var dueCount = dueStates.Count;
        // A whole-step caller (a replay, the bench, a client catching up) has no frame to hand
        // back, so the day's due regions go to the workers as one loop: one wake-up of the pool
        // instead of one per batch. A bounded step keeps its batches and its budget checks.
        var batchSize = Campaign.IsUnboundedStep ? Math.Max(UnboundedStageRegions, dueCount) : StageRegions;

        for (var first = 0; first < dueCount; first += batchSize)
        {
            if (first > 0 && Campaign.StageOverdue()) yield return 0;
            var last = Math.Min(first + batchSize, dueCount);
            var batchAppliedNative = false;
            var batchLen = last - first;
            if (batchLen >= 16 &&
                !DemographyRulesHook.Installed &&
                Kernels.NativeKernelsLoader.EnableEconomyDemoDefault &&
                Kernels.NativeKernelsLoader.HasCapability(Kernels.NativeKernelsLoader.CapEconomyDemo))
            {
                var scratch = _cohortScratch ??= new CohortBatchScratch();
                scratch.EnsureCapacity(batchLen);
                var active = 0;
                for (var index = first; index < last; index++)
                {
                    var state = dueStates[index];
                    if (state.Unclaimed || state.Population <= 0 ||
                        state.LastDemographyDay == state.LastHouseholdReviewDay) continue;
                    scratch.States[active] = state;
                    scratch.Population[active] = state.Population;
                    scratch.Births[active] = state.ReviewBirths;
                    scratch.Deaths[active] = state.ReviewDeaths;
                    scratch.Young[active] = state.YoungShare;
                    scratch.Old[active] = state.OldShare;
                    active++;
                }

                try
                {
                    if (active >= 16 && Kernels.NativeEconomyDemoKernels.TryStepCohortsBatch(
                            scratch.Population.AsSpan(0, active),
                            scratch.Births.AsSpan(0, active),
                            scratch.Deaths.AsSpan(0, active),
                            scratch.Young.AsSpan(0, active),
                            scratch.Old.AsSpan(0, active),
                            defines.DemographyMaturationAnnual,
                            defines.DemographyAgeingAnnual,
                            defines.DemographyMortalityOldWeight,
                            interval,
                            scratch.OutYoung.AsSpan(0, active),
                            scratch.OutOld.AsSpan(0, active),
                            scratch.OutWorking.AsSpan(0, active)))
                    {
                        batchAppliedNative = true;
                        var bStates = scratch.States;
                        var bYoung = scratch.OutYoung;
                        var bOld = scratch.OutOld;
                        if (active >= DeterministicParallel.MinItems && useParallel)
                        {
                            DeterministicParallel.ForRange(campaign, 0, active, (from, to) =>
                            {
                                for (var k = from; k < to; k++)
                                {
                                    var state = bStates[k];
                                    state.LastDemographyDay = state.LastHouseholdReviewDay;
                                    state.YoungShare = bYoung[k];
                                    state.OldShare = bOld[k];
                                    if (MoveCultures(campaign, state, shield, kinTable, nationsByMap))
                                        MovementRules.MarkPeopleStateDirty(campaign, state);
                                }
                            });
                            anyChanged = true;
                        }
                        else
                        {
                            for (var k = 0; k < active; k++)
                            {
                                var state = bStates[k];
                                state.LastDemographyDay = state.LastHouseholdReviewDay;
                                anyChanged = true;
                                state.YoungShare = bYoung[k];
                                state.OldShare = bOld[k];
                                if (MoveCultures(campaign, state, shield, kinTable, nationsByMap))
                                    MovementRules.MarkPeopleStateDirty(campaign, state);
                            }
                        }
                    }
                }
                finally
                {
                    if (active > 0) Array.Clear(scratch.States, 0, active);
                }
            }

            if (batchAppliedNative) continue;

            if (last - first >= DeterministicParallel.MinItems && useParallel)
            {
                DeterministicParallel.ForRange(campaign, first, last, (from, to) =>
                {
                    for (var index = from; index < to; index++)
                    {
                        var state = dueStates[index];
                        if (state.Unclaimed || state.Population <= 0 ||
                            state.LastDemographyDay == state.LastHouseholdReviewDay) continue;
                        state.LastDemographyDay = state.LastHouseholdReviewDay;
                        DemographyRulesHook.StepCohorts(
                            state.Population, state.ReviewBirths, state.ReviewDeaths,
                            state.YoungShare, state.OldShare,
                            defines.DemographyMaturationAnnual, defines.DemographyAgeingAnnual,
                            defines.DemographyMortalityOldWeight, interval,
                            out var nextYoungShare, out var nextOldShare, out _);
                        state.YoungShare = nextYoungShare;
                        state.OldShare = nextOldShare;
                        if (MoveCultures(campaign, state, shield, kinTable, nationsByMap))
                            MovementRules.MarkPeopleStateDirty(campaign, state);
                    }
                });
                anyChanged = true;
            }
            else
            {
                for (var index = first; index < last; index++)
                {
                    var state = dueStates[index];
                    if (state.Unclaimed || state.Population <= 0 ||
                        state.LastDemographyDay == state.LastHouseholdReviewDay) continue;
                    state.LastDemographyDay = state.LastHouseholdReviewDay;
                    anyChanged = true;
                    DemographyRulesHook.StepCohorts(
                        state.Population, state.ReviewBirths, state.ReviewDeaths,
                        state.YoungShare, state.OldShare,
                        defines.DemographyMaturationAnnual, defines.DemographyAgeingAnnual,
                        defines.DemographyMortalityOldWeight, interval,
                        out var nextYoungShare, out var nextOldShare, out _);
                    state.YoungShare = nextYoungShare;
                    state.OldShare = nextOldShare;
                    if (MoveCultures(campaign, state, shield, kinTable, nationsByMap))
                        MovementRules.MarkPeopleStateDirty(campaign, state);
                }
            }
        }
        dueStates.Clear();
        if (anyChanged) Aggregate(campaign);
    }

    /// <summary>
    /// What happens to a people nobody adds to. Migrants no longer need sorting out here: they
    /// arrive as the people they are, culture and faith and trade (InternalMigration.Move, used
    /// across borders by BrainDrain too). They used to arrive as a copy of the region's own mix
    /// and then be relabelled as its largest minority, so the refugees of South Sudan and Congo
    /// became Ganda in Uganda and Ganda appeared all over Tanzania - five points a year.
    /// </summary>
    private sealed class CoalesceInterner
    {
        private readonly Dictionary<string, int> _byRef = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _byText = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _byText.Count;

        public void Reset()
        {
            _byRef.Clear();
            _byText.Clear();
        }

        public int GetOrAdd(string value)
        {
            if (_byRef.TryGetValue(value, out var id)) return id;
            if (!_byText.TryGetValue(value, out id))
            {
                id = _byText.Count;
                _byText[value] = id;
            }
            if (_byRef.Count < 32768) _byRef[value] = id;
            return id;
        }
    }

    [ThreadStatic] private static CoalesceInterner _coalesceCultures;
    [ThreadStatic] private static CoalesceInterner _coalesceFaiths;
    [ThreadStatic] private static CoalesceInterner _coalesceTrades;

    private static void MergeGroupWeighted(PopulationGroup host, PopulationGroup gone)
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

    private static void MoveCultures(Campaign campaign, ScenarioState state, PopulationFolding.Shield shield) =>
        MoveCultures(campaign, state, shield, KinTable(campaign), campaign.NationsByMapIndex());

    private static bool MoveCultures(Campaign campaign, ScenarioState state, PopulationFolding.Shield shield,
        KinCache kinTable, Dictionary<int, Nation> nationsByMap)
    {
        var groups = state.PopulationGroups;
        if (groups.Count == 0 || state.Population <= 0) return false;
        var changed = Assimilate(campaign, state, groups, kinTable, nationsByMap);

        // First coalesce exact-key duplicate cohorts ((Class, Sector, Culture, Religion, Profession))
        // and, when Count > 16, fold tiny (<= 250) profession/class splinters into the largest
        // group of the exact same (Culture, Religion) in O(N) while preserving exact population
        // and population-weighted metrics (Employment, Wealth, StandardOfLiving, Radicalism).
        var count = groups.Count;
        if (count > 1)
        {
            var cultureIds = _coalesceCultures ??= new CoalesceInterner();
            var faithIds = _coalesceFaiths ??= new CoalesceInterner();
            var tradeIds = _coalesceTrades ??= new CoalesceInterner();
            if (cultureIds.Count >= 16384) cultureIds.Reset();
            if (faithIds.Count >= 16384) faithIds.Reset();
            if (tradeIds.Count >= 16384) tradeIds.Reset();

            var checkMicro = false;
            if (count > 16)
            {
                for (var i = 0; i < count; i++)
                {
                    if ((uint)(groups[i].Size - 1) < 250u) { checkMicro = true; break; }
                }
            }
            changed |= checkMicro
                ? CoalesceWithMicro(groups, count, cultureIds, faithIds, tradeIds)
                : CoalesceExactOnly(groups, count, cultureIds, faithIds, tradeIds);
        }

        // And the slivers a century of migration leaves, into their nearest kin (PopulationFolding).
        if (shield is not null) PopulationFolding.FoldRegion(campaign, state, shield, PopulationFolding.PeoplesTurn(campaign, state));
        // A group everybody has left or assimilated out of is nobody. Kept, they piled up -
        // every migration and every assimilation leaves its emptied group behind - and every
        // weekly walk over the region's people walked them too.
        var write = 0;
        for (var i = 0; i < groups.Count; i++)
            if (groups[i].Size > 0) groups[write++] = groups[i];
        if (write < groups.Count)
        {
            groups.RemoveRange(write, groups.Count - write);
            changed = true;
        }
        return changed;
    }

    private static bool CoalesceExactOnly(List<PopulationGroup> groups, int count,
        CoalesceInterner cultureIds, CoalesceInterner faithIds, CoalesceInterner tradeIds)
    {
        using var exactHosts = new HeapSpanDictionary<long, int>(count);
        string lastCulture = null, lastFaith = null, lastTrade = null;
        int cultureId = -1, faithId = -1, tradeId = -1;
        var merged = false;

        for (var i = 0; i < count; i++)
        {
            var g = groups[i];
            if (g.Size <= 0) continue;

            var cName = g.Culture ?? "";
            if (cultureId < 0 || !ReferenceEquals(cName, lastCulture))
            {
                lastCulture = cName;
                cultureId = cultureIds.GetOrAdd(cName);
            }
            var rName = g.Religion ?? "";
            if (faithId < 0 || !ReferenceEquals(rName, lastFaith))
            {
                lastFaith = rName;
                faithId = faithIds.GetOrAdd(rName);
            }
            var pName = g.Profession ?? "";
            if (tradeId < 0 || !ReferenceEquals(pName, lastTrade))
            {
                lastTrade = pName;
                tradeId = tradeIds.GetOrAdd(pName);
            }

            if (cultureId < 65535 && faithId < 65535 && tradeId < 65535)
            {
                var exactKey = ((long)cultureId << 40) | ((long)faithId << 24) | ((long)tradeId << 8) |
                               (((long)(int)g.Class & 0xF) << 4) | ((long)(int)g.Sector & 0xF);
                if (exactHosts.TryGetValue(exactKey, out var hostIdx))
                {
                    MergeGroupWeighted(groups[hostIdx], g);
                    merged = true;
                    continue;
                }
                exactHosts[exactKey] = i;
            }
        }
        return merged;
    }

    private static bool CoalesceWithMicro(List<PopulationGroup> groups, int count,
        CoalesceInterner cultureIds, CoalesceInterner faithIds, CoalesceInterner tradeIds)
    {
        using var exactHosts = new HeapSpanDictionary<long, int>(count);
        using var pairBest = new HeapSpanDictionary<long, int>(Math.Min(count, 64));
        using var groupPairs = new HeapPooledList<long>(count);

        string lastCulture = null, lastFaith = null, lastTrade = null;
        int cultureId = -1, faithId = -1, tradeId = -1;
        var merged = false;

        for (var i = 0; i < count; i++)
        {
            var g = groups[i];
            if (g.Size <= 0)
            {
                groupPairs.Add(-1L);
                continue;
            }

            var cName = g.Culture ?? "";
            if (cultureId < 0 || !ReferenceEquals(cName, lastCulture))
            {
                lastCulture = cName;
                cultureId = cultureIds.GetOrAdd(cName);
            }
            var rName = g.Religion ?? "";
            if (faithId < 0 || !ReferenceEquals(rName, lastFaith))
            {
                lastFaith = rName;
                faithId = faithIds.GetOrAdd(rName);
            }
            var pName = g.Profession ?? "";
            if (tradeId < 0 || !ReferenceEquals(pName, lastTrade))
            {
                lastTrade = pName;
                tradeId = tradeIds.GetOrAdd(pName);
            }

            // Pack full 5-tuple cohort identity when indices fit cleanly (always true per state).
            if (cultureId < 65535 && faithId < 65535 && tradeId < 65535)
            {
                var exactKey = ((long)cultureId << 40) | ((long)faithId << 24) | ((long)tradeId << 8) |
                               (((long)(int)g.Class & 0xF) << 4) | ((long)(int)g.Sector & 0xF);
                if (exactHosts.TryGetValue(exactKey, out var hostIdx))
                {
                    MergeGroupWeighted(groups[hostIdx], g);
                    merged = true;
                    groupPairs.Add(-1L);
                    continue;
                }
                exactHosts[exactKey] = i;
            }

            var pairKey = ((long)cultureId << 20) | (uint)faithId;
            groupPairs.Add(pairKey);
            if (!pairBest.TryGetValue(pairKey, out var bestIdx) || g.Size > groups[bestIdx].Size)
                pairBest[pairKey] = i;
        }

        for (var i = 0; i < count; i++)
        {
            var tiny = groups[i];
            if (tiny.Size <= 0 || tiny.Size > 250) continue;
            var pairKey = groupPairs[i];
            if (pairKey < 0 || !pairBest.TryGetValue(pairKey, out var hostIdx) || hostIdx == i) continue;
            var host = groups[hostIdx];
            if (host.Size <= tiny.Size) continue;
            MergeGroupWeighted(host, tiny);
            merged = true;
        }
        return merged;
    }

    // One per thread: regions are worked side by side (Stages).
    [ThreadStatic] private static Dictionary<string, long> _peopleHere;
    private static Dictionary<string, long> PeopleHere => _peopleHere ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which countries are for which people, for one campaign's one day: every country under the
    /// first people it is for, in the order of the campaign's list.
    ///
    /// "Does this people have a living state of its own" was SecessionEvents.KinState asked once
    /// per (owner, people) a day and remembered: a walk over all 495 countries, a catalogue lookup
    /// and a comparison of names for each - 232 of the cycle's 472 ms over the 14 late days on
    /// one thread, with a closure allocated for every one of the 157,000 groups a review asks
    /// for. The walk is now over the two or three countries that are for that people; the
    /// tests that depend on the day's world (alive, not a movement, not fallen, holding land) are
    /// still made when the question is asked, in the same order, so the answer is KinState's.
    /// </summary>
    private sealed class KinCache
    {
        public int Day = -1;
        public int Countries = -1;
        public readonly Dictionary<string, List<Nation>> ByPrimary = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<List<Nation>> Spare = new();
    }
    // Per campaign, not one static stamped with the last campaign that asked: two worlds in one
    // process took turns clearing it (see KinStatecraft's scan, the same trap).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, KinCache> KinCaches = new();

    /// <summary>
    /// The campaign's kin table for today, built again when the day has moved. Called on the
    /// stage's own thread before its regions are worked, so the workers only read it. Nothing
    /// between the stages of one day's cycle founds a country or renames one: orders are carried
    /// out at the top of the step.
    /// </summary>
    private static KinCache KinTable(Campaign campaign)
    {
        var cache = KinCaches.GetValue(campaign, _ => new KinCache());
        if (cache.Day == campaign.ElapsedDays && cache.Countries == campaign.Nations.Count) return cache;
        cache.Day = campaign.ElapsedDays;
        cache.Countries = campaign.Nations.Count;
        foreach (var list in cache.ByPrimary.Values) { list.Clear(); cache.Spare.Add(list); }
        cache.ByPrimary.Clear();
        foreach (var nation in campaign.Nations)
        {
            var primary = DemographyCatalog.PrimaryCulture(nation.Tag) ?? "";
            if (!cache.ByPrimary.TryGetValue(primary, out var list))
            {
                if (cache.Spare.Count > 0) { list = cache.Spare[^1]; cache.Spare.RemoveAt(cache.Spare.Count - 1); }
                else list = new List<Nation>(2);
                cache.ByPrimary[primary] = list;
            }
            list.Add(nation);
        }
        return cache;
    }

    /// <summary>SecessionEvents.KinState(campaign, owner, culture) is not null, read off the day's table.</summary>
    private static bool HasKinState(Campaign campaign, KinCache kin, Nation owner, string culture)
    {
        if (!kin.ByPrimary.TryGetValue(culture, out var list)) return false;
        for (var i = 0; i < list.Count; i++)
        {
            var nation = list[i];
            if (nation.Id == owner.Id || nation.Population <= 0 || nation.Movement is not null ||
                campaign.Fallen.Contains(nation.Id) || !campaign.HasOwnedLand(nation)) continue;
            return true;
        }
        return false;
    }

    /// <summary>The same answer for a test, which holds it to the walk it replaced: today's table, read now.</summary>
    internal static bool HasLivingKinState(Campaign campaign, Nation owner, string culture) =>
        HasKinState(campaign, KinTable(campaign), owner, culture);

    // The groups of the region that are already counted under the people everyone is shifting
    // towards, in the region's own order. One per thread: regions are worked side by side.
    [ThreadStatic] private static List<PopulationGroup> _targetGroups;

    /// <summary>
    /// Assimilation: a minority adopts the culture around it - the country's own where the
    /// country's people are a real part of the region, the local majority's where they are not.
    /// Its people keep their profession, their class and their faith: what changes is the
    /// language they are counted under.
    ///
    /// The rate is a year's share of the minority, and it used to be applied at every weekly
    /// review - twenty-seven per cent a year, so every minority on the map was gone inside a
    /// decade and the regions that should stay contested (Russians in Latvia, Hungarians in
    /// Transylvania, Albanians in North Macedonia) became one people. It is now scaled to the
    /// review and weighed by what actually decides it:
    ///   - kinship: a people of the same family shifts twice as readily, one of another faith
    ///     at four tenths the rate;
    ///   - size: a minority that is a large share of its region keeps its schools, its press and
    ///     its marriages - half of a region shifts at half the rate, and none ever stops wholly;
    ///   - schooling: literacy is where a state's language is taught, and anger is where it is
    ///     refused (radicalism);
    ///   - a living kin state keeps a people's own media and schools going;
    ///   - and the country's policy, the modifier key "assimilation".
    /// A people the country counts among its own (Belgium's Flemish and Walloons) is not
    /// assimilated into another of them.
    /// </summary>
    private static bool Assimilate(Campaign campaign, ScenarioState state, List<PopulationGroup> groups,
        KinCache kinTable, Dictionary<int, Nation> nationsByMap)
    {
        var defines = campaign.Defines;
        var annual = defines.DemographyAssimilationAnnual;
        if (annual <= 0f || groups.Count < 2) return false;
        var owner = nationsByMap.GetValueOrDefault(state.NationMapIndex);
        if (owner is null) return false;
        var interval = Math.Clamp(defines.EconomyTickDays, 1, 365) / 365f;
        var population = Math.Max(1L, state.Population);

        // Fast-path homogeneous regions (all positive groups share the exact same Culture reference):
        // avoid touching PeopleHere at all when there is no second culture in the state.
        string run = null;
        long runPeople = 0;
        var peoplePopulated = false;
        var count = groups.Count;
        for (var i = 0; i < count; i++)
        {
            var group = groups[i];
            if (group.Size <= 0 || string.IsNullOrEmpty(group.Culture)) continue;
            if (ReferenceEquals(group.Culture, run)) { runPeople += group.Size; continue; }
            if (run is not null)
            {
                if (!peoplePopulated)
                {
                    PeopleHere.Clear();
                    peoplePopulated = true;
                }
                CollectionsMarshal.GetValueRefOrAddDefault(PeopleHere, run, out _) += runPeople;
            }
            run = group.Culture;
            runPeople = group.Size;
        }
        if (!peoplePopulated) return false;
        if (run is not null)
            CollectionsMarshal.GetValueRefOrAddDefault(PeopleHere, run, out _) += runPeople;
        if (PeopleHere.Count < 2) return false;
        string majority = null;
        long majorityPeople = 0;
        foreach (var (culture, people) in PeopleHere)
            if (majority is null || people > majorityPeople ||
                people == majorityPeople && string.CompareOrdinal(culture, majority) < 0)
            {
                majority = culture;
                majorityPeople = people;
            }
        var national = DemographyCatalog.PrimaryCulture(owner.Tag);
        var target = !string.IsNullOrEmpty(national) &&
                     PeopleHere.GetValueOrDefault(national) >= population * defines.DemographyAssimilationNationalShare
            ? national
            : majority;
        var targetFamily = DemographyCatalog.Culture(target)?.Group ?? "";
        var targetFaiths = DemographyCatalog.FaithOf(target);
        var ownerPrimary = DemographyCatalog.PrimarySet(owner.Tag);
        var schooling = Math.Clamp(0.5f + state.Literacy, 0.5f, 1.5f);
        // Angry people do not take the language of the state they are angry with.
        var calm = Math.Clamp(1f - state.Radicalism, 0.05f, 1f);
        var policy = NationalModifiers.FactorSlot(owner, NationalModifiers.SlotAssimilation);

        // Built for today by the stage, on its own thread, before the regions were handed out.
        string lastCulture = null;
        var stays = false;
        float kin = 1f, size = 1f;
        var hasKin = false;
        string lastReligion = null;
        var religionRead = false;
        var faith = 1f;
        List<PopulationGroup> targetGroups = null;
        var changed = false;

        for (var i = 0; i < count; i++)
        {
            var group = groups[i];
            if (group.Size <= 0 || string.IsNullOrEmpty(group.Culture)) continue;
            if (!ReferenceEquals(group.Culture, lastCulture))
            {
                lastCulture = group.Culture;
                stays = ReferenceEquals(lastCulture, target) ||
                        string.Equals(lastCulture, target, StringComparison.OrdinalIgnoreCase) ||
                        (ownerPrimary is not null && ownerPrimary.Contains(lastCulture));
                if (!stays)
                {
                    var family = DemographyCatalog.Culture(lastCulture)?.Group ?? "";
                    kin = family.Length > 0 && (ReferenceEquals(family, targetFamily) ||
                        string.Equals(family, targetFamily, StringComparison.OrdinalIgnoreCase)) ? 2f : 1f;
                    size = Math.Clamp(1f - PeopleHere.GetValueOrDefault(lastCulture) / (float)population, 0.2f, 1f);
                    hasKin = HasKinState(campaign, kinTable, owner, lastCulture);
                }
            }
            if (stays) continue;
            if (!religionRead || !ReferenceEquals(group.Religion, lastReligion))
            {
                religionRead = true;
                lastReligion = group.Religion;
                faith = SharesFaith(lastReligion, targetFaiths) ? 1f : 0.4f;
            }
            var rate = annual * interval * kin * faith * size * schooling * calm * (hasKin ? 0.6f : 1f) * policy;
            var moved = Math.Min(group.Size, (long)Math.Round(group.Size * (double)rate));
            if (moved <= 0 && rate > 0f && group.Size <= 10)
                moved = Math.Min(group.Size, group.Size <= 3 ? group.Size : 1L);
            if (moved <= 0) continue;
            group.Size -= moved;
            changed = true;
            if (targetGroups is null)
            {
                // Who they join is looked for among the groups already counted under that people
                // (every one of them, emptied or not, in the region's order) instead of among the
                // whole region for every group that shifts.
                targetGroups = _targetGroups ??= new List<PopulationGroup>();
                targetGroups.Clear();
                string seen = null;
                var seenIsTarget = false;
                for (var ci = 0; ci < count; ci++)
                {
                    var candidate = groups[ci];
                    if (candidate.Culture is null) continue;
                    if (!ReferenceEquals(candidate.Culture, seen))
                    {
                        seen = candidate.Culture;
                        seenIsTarget = ReferenceEquals(seen, target) ||
                                       string.Equals(seen, target, StringComparison.OrdinalIgnoreCase);
                    }
                    if (seenIsTarget) targetGroups.Add(candidate);
                }
            }
            Counterpart(groups, targetGroups, group, target).Size += moved;
        }
        return changed;
    }

    private static bool SharesFaith(string religion, IReadOnlyList<(string Religion, float Share)> faiths)
    {
        if (string.IsNullOrEmpty(religion) || faiths.Count == 0) return true;
        for (var i = 0; i < faiths.Count; i++)
        {
            var r = faiths[i].Religion;
            if (ReferenceEquals(r, religion) || string.Equals(r, religion, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// The group of the given culture doing what this one does, made if there is none.
    /// <paramref name="ofCulture"/> is the region's groups of that culture in the region's order;
    /// a group made here joins both lists.
    /// </summary>
    private static PopulationGroup Counterpart(List<PopulationGroup> groups, List<PopulationGroup> ofCulture,
        PopulationGroup like, string culture)
    {
        // The two integer tests first: they are what most candidates fail on, and asking them
        // before the two case-blind string comparisons changes nothing about which group is the
        // first to pass all four. Indexed, so the list's enumerator is not touched per call.
        var likeClass = like.Class;
        var likeSector = like.Sector;
        var likeReligion = like.Religion;
        var likeProfession = like.Profession;
        for (var i = 0; i < ofCulture.Count; i++)
        {
            var group = ofCulture[i];
            if (group.Class == likeClass && group.Sector == likeSector &&
                (ReferenceEquals(group.Religion, likeReligion) || string.Equals(group.Religion, likeReligion, StringComparison.OrdinalIgnoreCase)) &&
                (ReferenceEquals(group.Profession, likeProfession) || string.Equals(group.Profession, likeProfession, StringComparison.OrdinalIgnoreCase))) return group;
        }
        var created = new PopulationGroup
        {
            Class = like.Class, Sector = like.Sector, Culture = culture, Religion = like.Religion,
            Profession = like.Profession, Size = 0, Employment = like.Employment, Wealth = like.Wealth,
            StandardOfLiving = like.StandardOfLiving, Radicalism = like.Radicalism
        };
        groups.Add(created);
        ofCulture.Add(created);
        return created;
    }

    /// <summary>
    /// The country's age structure, aggregated once per review so that anything that needs it - a
    /// modifier source with no campaign in hand, a tooltip, a chart - reads one number instead of
    /// walking a thousand regions.
    /// </summary>
    [ThreadStatic] private static AggregateScratch _aggregateScratch;

    private sealed class AggregateScratch
    {
        public uint[] StateOffsets = Array.Empty<uint>();
        public float[] OutYoung = Array.Empty<float>();
        public float[] OutOld = Array.Empty<float>();
        public float[] OutWorking = Array.Empty<float>();
        public float[] OutOldExcess = Array.Empty<float>();
        public long[] OutBirths = Array.Empty<long>();
        public long[] OutDeaths = Array.Empty<long>();
        public long[] OutMigration = Array.Empty<long>();

        public long[] StatePop = Array.Empty<long>();
        public float[] StateYoung = Array.Empty<float>();
        public float[] StateOld = Array.Empty<float>();
        public long[] StateBirths = Array.Empty<long>();
        public long[] StateDeaths = Array.Empty<long>();
        public long[] StateMigration = Array.Empty<long>();

        public void EnsureNations(int nationCount)
        {
            if (StateOffsets.Length < nationCount + 1)
                StateOffsets = new uint[Math.Max(nationCount + 1, StateOffsets.Length == 0 ? 128 : StateOffsets.Length * 2)];
            if (OutYoung.Length >= nationCount) return;
            var cap = Math.Max(nationCount, OutYoung.Length == 0 ? 128 : OutYoung.Length * 2);
            OutYoung = new float[cap];
            OutOld = new float[cap];
            OutWorking = new float[cap];
            OutOldExcess = new float[cap];
            OutBirths = new long[cap];
            OutDeaths = new long[cap];
            OutMigration = new long[cap];
        }

        public void EnsureStates(int stateCount)
        {
            if (StatePop.Length >= stateCount) return;
            var cap = Math.Max(stateCount, StatePop.Length == 0 ? 512 : StatePop.Length * 2);
            StatePop = new long[cap];
            StateYoung = new float[cap];
            StateOld = new float[cap];
            StateBirths = new long[cap];
            StateDeaths = new long[cap];
            StateMigration = new long[cap];
        }
    }

    public static void Aggregate(Campaign campaign)
    {
        var nations = campaign.Nations;
        if (nations.Count == 0) return;
        campaign.StatesOf(0);
        var defaultYoung = campaign.Defines.DemographyYoungShareDefault;
        var defaultOld = campaign.Defines.DemographyOldShareDefault;
        var defaultWorking = WorkingShare(null);
        var refOld = campaign.Defines.DemographyReferenceOldShare;

        var nationCount = nations.Count;
        if (Kernels.NativeKernelsLoader.EnableDemoAggregateDefault &&
            Kernels.NativeKernelsLoader.HasCapability(Kernels.NativeKernelsLoader.CapEconomyDemo))
        {
            var scratch = _aggregateScratch ??= new AggregateScratch();
            scratch.EnsureNations(nationCount);
            var offsets = scratch.StateOffsets;
            var totalStates = 0;
            offsets[0] = 0;
            for (var n = 0; n < nationCount; n++)
            {
                var states = campaign.StatesOf(nations[n]);
                totalStates += states.Length;
                offsets[n + 1] = (uint)totalStates;
            }

            scratch.EnsureStates(totalStates);
            var pos = 0;
            for (var n = 0; n < nationCount; n++)
            {
                var states = campaign.StatesOf(nations[n]);
                for (var s = 0; s < states.Length; s++)
                {
                    var state = states[s];
                    scratch.StatePop[pos] = state.Population;
                    scratch.StateYoung[pos] = state.YoungShare;
                    scratch.StateOld[pos] = state.OldShare;
                    scratch.StateBirths[pos] = state.ReviewBirths;
                    scratch.StateDeaths[pos] = state.ReviewDeaths;
                    scratch.StateMigration[pos] = state.ReviewNetMigration;
                    pos++;
                }
            }

            if (Kernels.NativeEconomyDemoKernels.TryAggregateNations(
                    nationCount,
                    offsets.AsSpan(0, nationCount + 1),
                    scratch.StatePop.AsSpan(0, totalStates),
                    scratch.StateYoung.AsSpan(0, totalStates),
                    scratch.StateOld.AsSpan(0, totalStates),
                    scratch.StateBirths.AsSpan(0, totalStates),
                    scratch.StateDeaths.AsSpan(0, totalStates),
                    scratch.StateMigration.AsSpan(0, totalStates),
                    totalStates,
                    defaultYoung,
                    defaultOld,
                    defaultWorking,
                    refOld,
                    scratch.OutYoung.AsSpan(0, nationCount),
                    scratch.OutOld.AsSpan(0, nationCount),
                    scratch.OutWorking.AsSpan(0, nationCount),
                    scratch.OutOldExcess.AsSpan(0, nationCount),
                    scratch.OutBirths.AsSpan(0, nationCount),
                    scratch.OutDeaths.AsSpan(0, nationCount),
                    scratch.OutMigration.AsSpan(0, nationCount)))
            {
                for (var n = 0; n < nationCount; n++)
                {
                    var nation = nations[n];
                    var hasStates = offsets[n + 1] > offsets[n];
                    nation.YoungShare = scratch.OutYoung[n];
                    nation.OldShare = scratch.OutOld[n];
                    nation.WorkingShare = scratch.OutWorking[n];
                    if (hasStates) nation.OldShareExcess = scratch.OutOldExcess[n];
                    nation.ReviewBirths = scratch.OutBirths[n];
                    nation.ReviewDeaths = scratch.OutDeaths[n];
                    nation.ReviewNetMigration = scratch.OutMigration[n];
                }
                return;
            }
        }

        for (var n = 0; n < nationCount; n++)
        {
            var nation = nations[n];
            var states = campaign.StatesOf(nation);
            if (states.Length == 0)
            {
                nation.YoungShare = defaultYoung;
                nation.OldShare = defaultOld;
                nation.ReviewBirths = nation.ReviewDeaths = nation.ReviewNetMigration = 0;
                nation.WorkingShare = defaultWorking;
                continue;
            }
            double people = 0, young = 0, old = 0;
            long births = 0, deaths = 0, migration = 0;
            for (var s = 0; s < states.Length; s++)
            {
                var state = states[s];
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
            nation.OldShareExcess = Math.Max(0f, nation.OldShare - refOld);
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
    public static void AddTo(Dictionary<string, float> totals, Nation nation)
    {
        if (nation is null || nation.WorkingShare <= 0f) return;
        var excess = nation.OldShareExcess;
        if (excess > 0.005f)
        {
            CollectionsMarshal.GetValueRefOrAddDefault(totals, "stability_weekly", out _) -= excess * 0.05f;
            CollectionsMarshal.GetValueRefOrAddDefault(totals, "living_weekly", out _) -= excess * 0.04f;
            return;
        }
        if (nation.YoungShare > 0.34f)
        {
            CollectionsMarshal.GetValueRefOrAddDefault(totals, "living_weekly", out _) -= 0.004f;
            CollectionsMarshal.GetValueRefOrAddDefault(totals, "stability_weekly", out _) += 0.003f;
        }
    }

    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation nation)
    {
        if (nation is null || nation.WorkingShare <= 0f) yield break;
        var excess = nation.OldShareExcess;
        if (excess > 0.005f)
        {
            var modifiers = nation.DemographicCycleModifiers ??= new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            modifiers.Clear();
            modifiers["stability_weekly"] = -excess * 0.05f;
            modifiers["living_weekly"] = -excess * 0.04f;
            yield return (Loc.Get("A country growing old"), modifiers);
            yield break;
        }
        if (nation.YoungShare > 0.34f)
        {
            var modifiers = nation.DemographicCycleModifiers ??= new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            modifiers.Clear();
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

public sealed partial class Defines
{
    /// <summary>Whether regions are worked side by side on the thread pool (demography, regional economy). Off: one at a time.</summary>
    public bool ParallelRegions { get; set; } = true;
}
