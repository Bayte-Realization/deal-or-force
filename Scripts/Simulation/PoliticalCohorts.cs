using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>How far a region's opinion moves toward today's conditions, per day.</summary>
    public float CohortDriftPerDay { get; set; } = 0.0012f;
    /// <summary>Divergence at which a region of the country's own people can start to leave.</summary>
    public float CenterRegionIdeologySplit { get; set; } = 0.55f;
    /// <summary>A region more accepting than this does not take the federal road.</summary>
    public float FederalGovernmentApprovalCeiling { get; set; } = 0.42f;
    /// <summary>A centre more legitimate than this still holds its own political regions.</summary>
    public float FederalStabilityCeiling { get; set; } = 0.50f;
    /// <summary>National shortage that counts as the economic half of a federal break.</summary>
    public float FederalShortageStress { get; set; } = 0.28f;
    /// <summary>How much poorer than the country a region must be to count as that stress.</summary>
    public float FederalLivingGap { get; set; } = 0.12f;
    /// <summary>Scales weekly secession pressure on the federal road. Zero leaves the opinion, and removes the break.</summary>
    public float FederalPressureScale { get; set; } = 1f;
    /// <summary>Population-weighted acceptance below this, in an orderly state, is a political scandal.</summary>
    public float ScandalApproval { get; set; } = 0.36f;
    /// <summary>Stability above this means the laws are holding. Below it, the crisis is already a collapse.</summary>
    public float ScandalOrder { get; set; } = 0.62f;
    public int ScandalCooldownDays { get; set; } = 480;
    public float ScandalStabilityCost { get; set; } = 0.015f;
    /// <summary>How much rejected government adds to coup pressure. It does not replace low stability.</summary>
    public float CoupGovernmentDisapprovalWeight { get; set; } = 0.28f;
    /// <summary>POL-01: Daily drift speed of the 5 regional political bloc shares toward socioeconomic reality.</summary>
    public float CleavageDriftPerDay { get; set; } = 0.0018f;
    /// <summary>POL-01: Maximum early crisis pressure bonus contributed by severe political polarization/regionalism.</summary>
    public float CleavageCrisisWeight { get; set; } = 0.12f;
}

/// <summary>POL-01: Five dynamic political population cleavages whose shares always sum to 1.0.</summary>
public enum PoliticalBloc
{
    Reformist = 0,
    Security = 1,
    Industrialist = 2,
    Traditionalist = 3,
    Regionalist = 4
}

/// <summary>Population-weighted national political cleavage summary (read-only).</summary>
public readonly record struct PoliticalCleavageSummary(
    float Reformist,
    float Security,
    float Industrialist,
    float Traditionalist,
    float Regionalist,
    PoliticalBloc Dominant,
    float Polarization,
    float ReformFriction,
    bool Reviewed)
{
    public float ShareOf(PoliticalBloc bloc) => bloc switch
    {
        PoliticalBloc.Reformist => Reformist,
        PoliticalBloc.Security => Security,
        PoliticalBloc.Industrialist => Industrialist,
        PoliticalBloc.Traditionalist => Traditionalist,
        PoliticalBloc.Regionalist => Regionalist,
        _ => 0f
    };
}

public sealed partial class ScenarioState
{
    /// <summary>How far this region's politics sit from the centre, 0 to 1. Negative until the first review.</summary>
    public float PoliticalDivergence = -1f;
    /// <summary>Share that accepts the laws it lives under. Negative until the first review.</summary>
    public float LawApproval = -1f;
    /// <summary>Share that accepts the government itself. Negative until the first review.</summary>
    public float GovernmentApproval = -1f;
    /// <summary>
    /// POL-01: Normalized shares of the 5 political blocs (Reformist, Security, Industrialist,
    /// Traditionalist, Regionalist) summing to 1.0. Null until first reviewed (zero bytes in legacy saves).
    /// </summary>
    public float[] PoliticalBlocs;
}

public sealed partial class Nation
{
    /// <summary>The last day an orderly, unloved government was noticed. Negative when it has not been.</summary>
    public int LastPoliticalScandalDay = -1;
}

/// <summary>What a country thinks of its own government, counted by people rather than by regions.</summary>
public readonly record struct CohortMood(float Law, float Government, float Divergence, bool Reviewed);

/// <summary>
/// People do not only belong to a culture and a faith. They also accept or reject the laws,
/// the government, and the idea that the centre still speaks for them.
///
/// The numbers live on the region and move a little every day toward what that region is
/// living through: shortages, a poorer life than the rest of the country, a lost war, a
/// government that has just changed its mind. They do not replace radicalism, which is the
/// economic anger, and they do not invent a second country out of a tag. A settled, solvent,
/// legitimate state keeps its regions. A state that has been unloved and insolvent for years
/// can lose a region of its own people — that road is <see cref="SecessionCore"/>.
/// </summary>
public static class PoliticalCohorts
{
    public static void Review(Campaign c, Nation n, int days) => Review(c, n, days, null, -1);

    /// <summary>
    /// The same review for the daily pass, which has had every region's alienation worked out
    /// beforehand (<see cref="ReadAlienation"/>). <paramref name="place"/> is the country's
    /// place in the campaign's list, where the table keeps its row.
    /// </summary>
    internal static void Review(Campaign c, Nation n, int days, AlienationTable known, int place)
    {
        if (c is null || n is null) return;
        var states = c.StatesOf(n);
        var validTable = known is not null && known.Holds(place, n, states);
        if (!(validTable ? known.CohortsOn : On(c, "political_cohorts"))) return;
        var span = Math.Max(0, days);
        var debt = span > 0 && (validTable ? known.IsInDebt(n) : InDebt(c, n));
        var losing = span > 0 && (validTable ? known.IsLosing(n.Id) : Losing(c, n));
        var recentPolitics = span > 0 && n.PoliticsChangedDay >= 0 && c.ElapsedDays - n.PoliticsChangedDay < 400;
        var step = MathF.Max(0f, c.Defines.CohortDriftPerDay) * span;
        var blocStep = MathF.Max(0f, c.Defines.CleavageDriftPerDay) * span;
        var atWar = span > 0 && (validTable ? known.IsAtWar(n.Id) : AtWar(c, n));
        var shortage = n.ShortageIndex;
        var stability = n.Stability;
        var nationalLiving = n.StandardOfLiving;
        var capitalMapIndex = n.CapitalStateMapIndex;
        var mil = M.Clamp01(n.MilitarySpending * 6f);
        var taxStress = M.Clamp01((n.TaxRate - 0.22f) * 2.2f);
        var marketAccess = M.Clamp01(n.MarketAccess);
        var row = validTable ? known.RowOf(place) : -1;
        double moodPeople = 0, moodLaw = 0, moodGov = 0, moodDiv = 0;
        double blocPeople = 0, b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0;

        // Batched Rust SIMD path when drifting >= 2 non-fresh regions with a valid AlienationTable.
        if (span > 0 && validTable && states.Length >= 2 &&
            Kernels.NativeKernelsLoader.EnablePoliticalCohortsDefault &&
            Kernels.NativeKernelsLoader.HasCapability(Kernels.NativeKernelsLoader.CapWartimeGraph))
        {
            known.EnsureDriftScratch(states.Length);
            var driftCount = 0;
            var baseFlags = (byte)((debt ? 1 : 0) | (losing ? 2 : 0) | (recentPolitics ? 4 : 0) | (atWar ? 16 : 0));
            for (var index = 0; index < states.Length; index++)
            {
                var state = states[index];
                if (state is null || state.Population <= 0) continue;
                var fresh = state.PoliticalDivergence < 0f || state.LawApproval < 0f || state.GovernmentApproval < 0f;
                if (fresh) Seed(n, state);
                var alienation = row >= 0
                    ? known.Values[row + index]
                    : row == -2 ? 0f : StateIdentity.RegionAlienation(c, n, state);
                if (state.PoliticalBlocs is not { Length: 5 })
                    SeedBlocs(n, state, atWar, debt, alienation, mil, taxStress, marketAccess);
                if (fresh) continue;

                var di = driftCount++;
                known.DriftStates[di] = state;
                var capital = state.MapIndex == capitalMapIndex;
                var bl = state.PoliticalBlocs;
                var hasBlocs = bl is { Length: BlocCount };
                known.DriftFlags[di] = (byte)(baseFlags | (capital ? 8 : 0) | (hasBlocs ? 32 : 0));
                known.DriftStep[di] = step;
                known.DriftBlocStep[di] = blocStep;
                known.DriftShortage[di] = shortage;
                known.DriftStability[di] = stability;
                known.DriftNationalLiving[di] = nationalLiving;
                known.DriftAlienation[di] = alienation;
                known.DriftMil[di] = mil;
                known.DriftTaxStress[di] = taxStress;
                known.DriftMarketAccess[di] = marketAccess;
                known.DriftLiteracy[di] = state.Literacy;
                known.DriftUrbanization[di] = state.Urbanization;
                known.DriftSol[di] = state.StandardOfLiving;
                known.DriftRadicalism[di] = state.Radicalism;
                known.DriftLaw[di] = state.LawApproval;
                known.DriftGov[di] = state.GovernmentApproval;
                known.DriftDiv[di] = state.PoliticalDivergence;
                if (hasBlocs)
                {
                    var bo = di * BlocCount;
                    known.DriftBlocs[bo] = bl![0];
                    known.DriftBlocs[bo + 1] = bl[1];
                    known.DriftBlocs[bo + 2] = bl[2];
                    known.DriftBlocs[bo + 3] = bl[3];
                    known.DriftBlocs[bo + 4] = bl[4];
                }
            }

            var nativeDone = driftCount == 0 || Kernels.NativePoliticalKernels.TryDriftCohortsBatch(
                driftCount,
                known.DriftFlags, known.DriftStep, known.DriftBlocStep,
                known.DriftShortage, known.DriftStability, known.DriftNationalLiving,
                known.DriftAlienation, known.DriftMil, known.DriftTaxStress, known.DriftMarketAccess,
                known.DriftLiteracy, known.DriftUrbanization, known.DriftSol, known.DriftRadicalism,
                known.DriftLaw, known.DriftGov, known.DriftDiv, known.DriftBlocs);
            if (nativeDone)
            {
                for (var di = 0; di < driftCount; di++)
                {
                    var state = known.DriftStates[di];
                    state.LawApproval = known.DriftLaw[di];
                    state.GovernmentApproval = known.DriftGov[di];
                    state.PoliticalDivergence = known.DriftDiv[di];
                    if ((known.DriftFlags[di] & 32) != 0 && state.PoliticalBlocs is { Length: BlocCount } bl)
                    {
                        var bo = di * BlocCount;
                        bl[0] = known.DriftBlocs[bo];
                        bl[1] = known.DriftBlocs[bo + 1];
                        bl[2] = known.DriftBlocs[bo + 2];
                        bl[3] = known.DriftBlocs[bo + 3];
                        bl[4] = known.DriftBlocs[bo + 4];
                    }
                    known.DriftStates[di] = null;
                }
            }
            else
            {
                for (var di = 0; di < driftCount; di++)
                {
                    var state = known.DriftStates[di];
                    var capital = (known.DriftFlags[di] & 8) != 0;
                    Drift(state, step, debt, losing, recentPolitics, shortage, stability, nationalLiving,
                        capital, known.DriftAlienation[di]);
                    DriftBlocs(state, blocStep, atWar, losing, debt, shortage, stability, nationalLiving,
                        capital, known.DriftAlienation[di], mil, taxStress, marketAccess);
                    known.DriftStates[di] = null;
                }
            }

            for (var index = 0; index < states.Length; index++)
            {
                var state = states[index];
                if (state is null || state.Population <= 0) continue;
                var pop = (double)state.Population;
                if (state.PoliticalDivergence >= 0f)
                {
                    moodPeople += pop;
                    moodLaw += pop * (double)M.Clamp01(state.LawApproval);
                    moodGov += pop * (double)M.Clamp01(state.GovernmentApproval);
                    moodDiv += pop * (double)M.Clamp01(state.PoliticalDivergence);
                }
                if (state.PoliticalBlocs is { Length: BlocCount } bl)
                {
                    blocPeople += pop;
                    b0 += pop * bl[0];
                    b1 += pop * bl[1];
                    b2 += pop * bl[2];
                    b3 += pop * bl[3];
                    b4 += pop * bl[4];
                }
            }
        }
        else
        {
            for (var index = 0; index < states.Length; index++)
            {
                var state = states[index];
                if (state is null || state.Population <= 0) continue;
                var fresh = state.PoliticalDivergence < 0f || state.LawApproval < 0f || state.GovernmentApproval < 0f;
                if (fresh) Seed(n, state);
                var alienation = row >= 0
                    ? known.Values[row + index]
                    : row == -2 ? 0f : StateIdentity.RegionAlienation(c, n, state);
                if (state.PoliticalBlocs is not { Length: 5 })
                    SeedBlocs(n, state, atWar, debt, alienation, mil, taxStress, marketAccess);
                if (span > 0 && !fresh)
                {
                    var capital = state.MapIndex == capitalMapIndex;
                    Drift(state, step, debt, losing, recentPolitics, shortage, stability, nationalLiving,
                        capital, alienation);
                    DriftBlocs(state, blocStep, atWar, losing, debt, shortage, stability, nationalLiving,
                        capital, alienation, mil, taxStress, marketAccess);
                }
                var pop = (double)state.Population;
                if (state.PoliticalDivergence >= 0f)
                {
                    moodPeople += pop;
                    moodLaw += pop * (double)M.Clamp01(state.LawApproval);
                    moodGov += pop * (double)M.Clamp01(state.GovernmentApproval);
                    moodDiv += pop * (double)M.Clamp01(state.PoliticalDivergence);
                }
                if (state.PoliticalBlocs is { Length: BlocCount } bl)
                {
                    blocPeople += pop;
                    b0 += pop * bl[0];
                    b1 += pop * bl[1];
                    b2 += pop * bl[2];
                    b3 += pop * bl[3];
                    b4 += pop * bl[4];
                }
            }
        }
        if (known is not null)
        {
            var mood = moodPeople > 0
                ? new CohortMood((float)(moodLaw / moodPeople), (float)(moodGov / moodPeople), (float)(moodDiv / moodPeople), true)
                : default;
            var cleavage = blocPeople > 0
                ? BuildCleavageSummary(n, blocPeople, b0, b1, b2, b3, b4)
                : default;
            known.CacheSummary(n, states, c.ElapsedDays, mood, cleavage);
        }
        if (span > 0) Notice(c, n);
    }

    /// <summary>
    /// How far every governed region's people sit outside what their state declares
    /// (StateIdentity.RegionAlienation), worked out for the whole world at the head of the
    /// daily review.
    ///
    /// The review asked for it a region at a time as it came to each country. The answer is a
    /// walk over the region's people - a hundred groups scattered through memory, 4.6
    /// microseconds a region on the late save, 95 ms of the 14 days for nothing but waiting on
    /// memory - and one region's answer has nothing to do with another's. So they are read
    /// together, side by side, before the first country is reviewed, and the review takes its
    /// numbers from here.
    ///
    /// A number is used only while it is still the number a fresh walk would give: nothing the
    /// daily review does to one country changes who lives in another's regions or what another
    /// state declares - it moves approvals, radicalism, treasuries, memories and the chronicle -
    /// with the one exception of a coup, after which the table is put aside (Spoil) and every
    /// later country asks afresh. The row is also refused if the country's list of regions or
    /// either of its declarations is not the very object that was read.
    /// </summary>
    internal sealed class AlienationTable
    {
        public float[] Values = new float[2048];
        private Nation[] _owner = new Nation[2048];
        private ScenarioState[] _region = new ScenarioState[2048];
        private int[] _row = new int[512];
        private ScenarioState[][] _regions = new ScenarioState[512][];
        private string[] _religion = new string[512];
        private string[] _culture = new string[512];
        private Nation[] _nation = new Nation[512];
        private bool[] _atWar = Array.Empty<bool>();
        private bool[] _losing = Array.Empty<bool>();
        private HashSet<int> _atWarOverflow;
        private HashSet<int> _losingOverflow;
        private int _nations;
        private bool _good;

        public bool CohortsOn { get; private set; }
        public bool DebtOn { get; private set; }

        private Nation _cachedNation;
        private ScenarioState[] _cachedStates;
        private int _cachedDay = -1;
        private bool _moodValid;
        private bool _cleavageValid;
        private CohortMood _cachedMood;
        private PoliticalCleavageSummary _cachedCleavage;

        // Reusable per-campaign scratch buffers for NativePoliticalKernels.TryDriftCohortsBatch (0 static fields).
        public ScenarioState[] DriftStates = Array.Empty<ScenarioState>();
        public byte[] DriftFlags = Array.Empty<byte>();
        public float[] DriftStep = Array.Empty<float>();
        public float[] DriftBlocStep = Array.Empty<float>();
        public float[] DriftShortage = Array.Empty<float>();
        public float[] DriftStability = Array.Empty<float>();
        public float[] DriftNationalLiving = Array.Empty<float>();
        public float[] DriftAlienation = Array.Empty<float>();
        public float[] DriftMil = Array.Empty<float>();
        public float[] DriftTaxStress = Array.Empty<float>();
        public float[] DriftMarketAccess = Array.Empty<float>();
        public float[] DriftLiteracy = Array.Empty<float>();
        public float[] DriftUrbanization = Array.Empty<float>();
        public float[] DriftSol = Array.Empty<float>();
        public float[] DriftRadicalism = Array.Empty<float>();
        public float[] DriftLaw = Array.Empty<float>();
        public float[] DriftGov = Array.Empty<float>();
        public float[] DriftDiv = Array.Empty<float>();
        public float[] DriftBlocs = Array.Empty<float>();

        public void EnsureDriftScratch(int count)
        {
            if (DriftStates.Length >= count) return;
            var cap = Math.Max(count, Math.Max(32, DriftStates.Length * 2));
            DriftStates = new ScenarioState[cap];
            DriftFlags = new byte[cap];
            DriftStep = new float[cap];
            DriftBlocStep = new float[cap];
            DriftShortage = new float[cap];
            DriftStability = new float[cap];
            DriftNationalLiving = new float[cap];
            DriftAlienation = new float[cap];
            DriftMil = new float[cap];
            DriftTaxStress = new float[cap];
            DriftMarketAccess = new float[cap];
            DriftLiteracy = new float[cap];
            DriftUrbanization = new float[cap];
            DriftSol = new float[cap];
            DriftRadicalism = new float[cap];
            DriftLaw = new float[cap];
            DriftGov = new float[cap];
            DriftDiv = new float[cap];
            DriftBlocs = new float[cap * BlocCount];
        }

        public void Read(Campaign c)
        {
            _good = false;
            ClearSummary();
            CohortsOn = On(c, "political_cohorts");
            DebtOn = On(c, "public_debt");
            var stateIdentityOn = On(c, "state_identity");
            _nations = c.Nations.Count;
            if (_row.Length < _nations)
            {
                var size = Math.Max(_nations, _row.Length * 2);
                _row = new int[size];
                _regions = new ScenarioState[size][];
                _religion = new string[size];
                _culture = new string[size];
                _nation = new Nation[size];
            }
            var maxNationId = 0;
            var count = 0;
            for (var place = 0; place < _nations; place++)
            {
                var nation = c.Nations[place];
                if (nation.Id > maxNationId) maxNationId = nation.Id;
                var regions = c.StatesOf(nation);
                _nation[place] = nation;
                _regions[place] = regions;
                _religion[place] = nation.DeclaredReligion;
                _culture[place] = nation.DeclaredCulture;
                var hasIdentity = stateIdentityOn &&
                    ((!string.IsNullOrWhiteSpace(nation.DeclaredReligion) && !StateIdentity.IsSecular(nation.DeclaredReligion)) ||
                     !string.IsNullOrWhiteSpace(nation.DeclaredCulture));
                if (!hasIdentity)
                {
                    _row[place] = -2;
                    continue;
                }
                _row[place] = count;
                if (_owner.Length < count + regions.Length)
                {
                    var size = Math.Max(count + regions.Length, _owner.Length * 2);
                    Array.Resize(ref _owner, size);
                    Array.Resize(ref _region, size);
                    Values = new float[size];
                }
                for (var index = 0; index < regions.Length; index++)
                {
                    _owner[count] = nation;
                    _region[count] = regions[index];
                    count++;
                }
            }
            var warSlots = Math.Min(maxNationId + 1, 16384);
            if (_atWar.Length < warSlots)
            {
                _atWar = new bool[Math.Max(warSlots, 512)];
                _losing = new bool[Math.Max(warSlots, 512)];
            }
            else if (warSlots > 0)
            {
                Array.Clear(_atWar, 0, warSlots);
                Array.Clear(_losing, 0, warSlots);
            }
            _atWarOverflow?.Clear();
            _losingOverflow?.Clear();
            var liveWars = Warfare.LiveWars(c);
            for (var wi = 0; wi < liveWars.Count; wi++)
            {
                var war = liveWars[wi];
                if (war.Ended) continue;
                var atkLosing = war.WarScore <= -20f;
                var defLosing = -war.WarScore <= -20f;
                MarkWarMember(war.AttackerId, atkLosing, warSlots);
                MarkWarMember(war.DefenderId, defLosing, warSlots);
                foreach (var aid in war.AttackerAllies) MarkWarMember(aid, atkLosing, warSlots);
                foreach (var did in war.DefenderAllies) MarkWarMember(did, defLosing, warSlots);
            }

            if (count > 0)
            {
                var values = Values;
                var owner = _owner;
                var region = _region;
                DeterministicParallel.ForRange(c, 0, count, (from, to) =>
                {
                    for (var idx = from; idx < to; idx++)
                    {
                        var st = region[idx];
                        if (st is null || st.Population <= 0) continue;
                        values[idx] = StateIdentity.RegionAlienation(c, owner[idx], st);
                    }
                }, minItems: 128);
            }
            // The world's regions are not kept alive by a table that has been read.
            Array.Clear(_owner, 0, count);
            Array.Clear(_region, 0, count);
            _good = true;
        }

        private void MarkWarMember(int nationId, bool losing, int warSlots)
        {
            if ((uint)nationId < (uint)warSlots)
            {
                _atWar[nationId] = true;
                if (losing) _losing[nationId] = true;
                return;
            }
            (_atWarOverflow ??= new HashSet<int>()).Add(nationId);
            if (losing) (_losingOverflow ??= new HashSet<int>()).Add(nationId);
        }

        public bool IsAtWar(int nationId) =>
            (uint)nationId < (uint)_atWar.Length ? _atWar[nationId] : (_atWarOverflow?.Contains(nationId) ?? false);

        public bool IsLosing(int nationId) =>
            (uint)nationId < (uint)_losing.Length ? _losing[nationId] : (_losingOverflow?.Contains(nationId) ?? false);

        public bool IsInDebt(Nation n)
        {
            if (!DebtOn) return false;
            var condition = n.DebtCondition;
            return condition == DebtCondition.Stressed || condition == DebtCondition.Default
                || condition == DebtCondition.Insolvent || condition == DebtCondition.Restructuring;
        }

        public void CacheSummary(Nation nation, ScenarioState[] states, int day, CohortMood mood, PoliticalCleavageSummary cleavage)
        {
            _cachedNation = nation;
            _cachedStates = states;
            _cachedDay = day;
            _cachedMood = mood;
            _cachedCleavage = cleavage;
            _moodValid = true;
            _cleavageValid = true;
        }

        public void InvalidateMood() => _moodValid = false;

        public void ClearSummary()
        {
            _cachedNation = null;
            _cachedStates = null;
            _moodValid = false;
            _cleavageValid = false;
        }

        public bool TryGetMood(Nation nation, ScenarioState[] states, int day, out CohortMood mood)
        {
            if (_moodValid && _cachedDay == day && ReferenceEquals(_cachedNation, nation) && ReferenceEquals(_cachedStates, states))
            {
                mood = _cachedMood;
                return true;
            }
            mood = default;
            return false;
        }

        public bool TryGetCleavage(Nation nation, ScenarioState[] states, int day, out PoliticalCleavageSummary cleavage)
        {
            if (_cleavageValid && _cachedDay == day && ReferenceEquals(_cachedNation, nation) && ReferenceEquals(_cachedStates, states))
            {
                cleavage = _cachedCleavage;
                return true;
            }
            cleavage = default;
            return false;
        }

        /// <summary>Something happened that could have changed what a walk would find: stop answering.</summary>
        public void Spoil() { _good = false; ClearSummary(); }

        public bool Holds(int place, Nation nation, ScenarioState[] regions) =>
            _good && place >= 0 && place < _nations && ReferenceEquals(_nation[place], nation) &&
            ReferenceEquals(_regions[place], regions) &&
            ReferenceEquals(_religion[place], nation.DeclaredReligion) &&
            ReferenceEquals(_culture[place], nation.DeclaredCulture);

        public int RowOf(int place) => _row[place];
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, AlienationTable> AlienationTables = new();

    /// <summary>The campaign's table, read now; null when the mechanic is off and nobody will ask.</summary>
    internal static AlienationTable ReadAlienation(Campaign c)
    {
        if (c is null || !On(c, "political_cohorts")) return null;
        var table = AlienationTables.GetValue(c, static _ => new AlienationTable());
        table.Read(c);
        return table;
    }

    /// <summary>
    /// One sentence for the dossier. Empty when there is nothing worth saying.
    /// A country people still like gets a sentence too: comfort is not only a list of alarms.
    /// </summary>
    public static string StrainCaption(Campaign c, Nation n)
    {
        if (c is null || n is null || !On(c, "political_cohorts")) return "";
        var mood = Mood(c, n);
        if (!mood.Reviewed) return "";
        foreach (var state in c.StatesOf(n))
            if (SecessionCore.IsFederalBreak(c, n, state))
                return "A region of its own has stopped belonging to the centre.";
        if (mood.Government < 0.42f && mood.Divergence >= 0.35f)
            return "The laws still hold. The government is losing the room.";
        if (mood.Government >= 0.62f && n.Stability >= 0.55f && mood.Divergence < 0.25f)
            return "Liked enough to argue in the open.";
        return "";
    }

    /// <summary>
    /// What the unrest map paints for one region. Radicalism, the pull to leave, and —
    /// once the region has been read — how far its politics have left the government.
    /// An unread region stays on the first two, so the opening map is not a revolt.
    /// </summary>
    public static float UnrestHeat(ScenarioState state)
    {
        if (state is null) return 0f;
        var heat = MathF.Max(M.Clamp01(state.Radicalism), state.SecessionPressure < 0f ? 0f : M.Clamp01(state.SecessionPressure));
        if (state.PoliticalDivergence >= 0f && state.GovernmentApproval >= 0f)
            heat = MathF.Max(heat, M.Clamp01(state.PoliticalDivergence) * (1f - M.Clamp01(state.GovernmentApproval)));
        return heat;
    }

    /// <summary>
    /// One region, read and not changed: how hot it is, and the single pressure on the books
    /// that is pushing its opinion. The sentence is an English key. Empty when nothing
    /// reviewed is pushing.
    /// </summary>
    public sealed record RegionAccount(int MapIndex, string Name, float Heat, string Cause, string Chip);

    /// <summary>
    /// The governed region whose politics have left the government furthest.
    /// Radicalism alone does not win this: the unrest map already paints that.
    /// Null when no region has been read yet.
    /// </summary>
    public static RegionAccount Loudest(Campaign c, Nation n)
    {
        if (c is null || n is null || !On(c, "political_cohorts")) return null;
        ScenarioState best = null;
        var bestScore = -1f;
        foreach (var state in c.StatesOf(n))
        {
            if (state is null || state.Population <= 0 || state.PoliticalDivergence < 0f || state.GovernmentApproval < 0f)
                continue;
            var score = M.Clamp01(state.PoliticalDivergence) * (1f - M.Clamp01(state.GovernmentApproval));
            if (score < bestScore || (score == bestScore && best is not null && state.MapIndex > best.MapIndex)) continue;
            best = state;
            bestScore = score;
        }
        if (best is null) return null;
        var (cause, chip) = CauseOf(c, n, best);
        return new RegionAccount(best.MapIndex, best.DisplayName ?? "", UnrestHeat(best), cause, chip);
    }

    /// <summary>
    /// Why this region's opinion is where it is, from the same pressures the daily drift uses.
    /// One sentence, the strongest pressure on the books. Reading it changes nothing.
    /// </summary>
    public static (string Cause, string Chip) CauseOf(Campaign c, Nation n, ScenarioState state)
    {
        if (c is null || n is null || state is null || state.PoliticalDivergence < 0f || state.GovernmentApproval < 0f)
            return ("", "");
        if (InDebt(c, n))
            return ("The centre is in debt, and this region is paying for it.", "the centre's debt");
        if (Losing(c, n))
            return ("A war is being lost, and this region can tell.", "a lost war");
        if (n.PoliticsChangedDay >= 0 && c.ElapsedDays - n.PoliticsChangedDay < 400)
            return ("The government changed its mind, and this region has not forgiven it.", "a new government");
        if (n.ShortageIndex >= c.Defines.FederalShortageStress)
            return ("The shelves are short, and the blame is sitting on the government.", "short shelves");
        if (state.StandardOfLiving + c.Defines.FederalLivingGap < n.StandardOfLiving)
            return ("This region lives worse than the country that taxes it.", "poorer than the country");
        if (StateIdentity.RegionAlienation(c, n, state) >= 0.35f)
            return ("The state's declared identity is not this region's own.", "declared identity");
        if (state.MapIndex == n.CapitalStateMapIndex)
            return ("The capital still belongs. The grievance is quieter here.", "");
        if (state.PoliticalDivergence >= 0.35f)
            return ("The opinion here is its own. Nothing else on the books is pushing it today.", "");
        return ("", "");
    }

    /// <summary>The people's verdict, or not reviewed when no governed region has been read yet.</summary>
    public static CohortMood Mood(Campaign c, Nation n)
    {
        if (c is null || n is null) return default;
        var states = c.StatesOf(n);
        if (AlienationTables.TryGetValue(c, out var table) &&
            table.TryGetMood(n, states, c.ElapsedDays, out var cached))
            return cached;
        if (!On(c, "political_cohorts")) return default;
        double people = 0, law = 0, government = 0, divergence = 0;
        for (var i = 0; i < states.Length; i++)
        {
            var state = states[i];
            if (state is null || state.Population <= 0 || state.PoliticalDivergence < 0f) continue;
            people += state.Population;
            law += state.Population * (double)M.Clamp01(state.LawApproval);
            government += state.Population * (double)M.Clamp01(state.GovernmentApproval);
            divergence += state.Population * (double)M.Clamp01(state.PoliticalDivergence);
        }
        if (people <= 0) return default;
        return new CohortMood((float)(law / people), (float)(government / people), (float)(divergence / people), true);
    }

    /// <summary>
    /// An orderly country whose people have stopped accepting it becomes news. A country
    /// already falling apart is a crisis, not a scandal, and is left to the coup and
    /// secession roads.
    /// </summary>
    public static bool Notice(Campaign c, Nation n)
    {
        if (c is null || n is null || !On(c, "political_cohorts")) return false;
        if (n.Stability < c.Defines.ScandalOrder) return false;
        if (n.LastPoliticalScandalDay >= 0 && c.ElapsedDays - n.LastPoliticalScandalDay < Math.Max(1, c.Defines.ScandalCooldownDays))
            return false;
        var mood = Mood(c, n);
        if (!mood.Reviewed || mood.Government >= c.Defines.ScandalApproval) return false;
        n.LastPoliticalScandalDay = c.ElapsedDays;
        n.Stability = M.Clamp01(n.Stability - MathF.Max(0f, c.Defines.ScandalStabilityCost));
        c.Chronicle.Record(c, "political_scandal", n.Id);
        return true;
    }

    private static void Seed(Nation n, ScenarioState s)
    {
        s.LawApproval = M.Clamp01(0.48f + s.StandardOfLiving * 0.25f);
        s.GovernmentApproval = M.Clamp01(0.40f + n.Stability * 0.35f);
        s.PoliticalDivergence = s.MapIndex == n.CapitalStateMapIndex ? 0.04f : 0.08f;
    }

    private static void Drift(ScenarioState s, float step, bool debt, bool losing, bool recentPolitics,
        float shortage, float stability, float nationalLiving, bool capital, float alienation)
    {
        var debtPenalty = debt ? 0.12f : 0f;
        var radical = M.Clamp01(s.Radicalism);
        var law = M.Clamp01(0.40f + s.StandardOfLiving * 0.40f + stability * 0.15f - shortage * 0.40f - debtPenalty * 0.5f - radical * 0.25f);
        var government = 0.42f + stability * 0.35f + s.StandardOfLiving * 0.18f - shortage * 0.35f - debtPenalty - radical * 0.22f - alienation * 0.14f;
        if (losing) government -= 0.10f;
        if (recentPolitics) government -= 0.08f;
        government = M.Clamp01(government);
        s.LawApproval = Move(M.Clamp01(s.LawApproval), law, step);
        s.GovernmentApproval = Move(M.Clamp01(s.GovernmentApproval), government, step);
        var rejection = M.Clamp01((1f - government) * 0.55f + (1f - law) * 0.35f + radical * 0.25f);
        var poorer = nationalLiving > s.StandardOfLiving ? nationalLiving - s.StandardOfLiving : 0f;
        if (poorer > 1f) poorer = 1f;
        var divergence = M.Clamp01(rejection + poorer * 0.20f + alienation * 0.16f);
        if (capital) divergence *= 0.45f;
        s.PoliticalDivergence = Move(M.Clamp01(s.PoliticalDivergence), divergence, step * 0.65f);
    }

    private static bool InDebt(Campaign c, Nation n)
    {
        if (!On(c, "public_debt")) return false;
        var condition = n.DebtCondition;
        return condition == DebtCondition.Stressed || condition == DebtCondition.Default
            || condition == DebtCondition.Insolvent || condition == DebtCondition.Restructuring;
    }

    private static bool Losing(Campaign c, Nation n)
    {
        var liveWars = Warfare.LiveWars(c);
        for (var i = 0; i < liveWars.Count; i++)
        {
            var war = liveWars[i];
            if (war.Ended || !war.Involves(n.Id)) continue;
            if ((war.IsAttacking(n.Id) ? war.WarScore : -war.WarScore) <= -20f) return true;
        }
        return false;
    }

    private static bool AtWar(Campaign c, Nation n)
    {
        var liveWars = Warfare.LiveWars(c);
        for (var i = 0; i < liveWars.Count; i++)
        {
            var war = liveWars[i];
            if (!war.Ended && war.Involves(n.Id)) return true;
        }
        return false;
    }

    /// <summary>
    /// The same answer as <see cref="Campaign.FeatureEnabled"/> for a known id.
    /// The public check validates the id with a LINQ scan, and this runs for every country every day.
    /// </summary>
    private static bool On(Campaign c, string id)
    {
        if (c.FeatureOverrides.TryGetValue(id, out var changed)) return changed;
        if (c.Defines.Features is not null && c.Defines.Features.TryGetValue(id, out var initial)) return initial;
        return MechanicFeatures.BuiltIns.Contains(id);
    }

    private static float Move(float value, float target, float amount) => value < target
        ? MathF.Min(target, value + amount) : MathF.Max(target, value - amount);

    // -------------------------------------------------------------------------
    // POL-01: Dynamic Political Population Cleavages (5 normalized blocs)
    // -------------------------------------------------------------------------

    public const int BlocCount = 5;

    public static string BlocName(PoliticalBloc bloc) => Loc.Get(bloc switch
    {
        PoliticalBloc.Reformist => "pol.bloc.reformist",
        PoliticalBloc.Security => "pol.bloc.security",
        PoliticalBloc.Industrialist => "pol.bloc.industrialist",
        PoliticalBloc.Traditionalist => "pol.bloc.traditionalist",
        PoliticalBloc.Regionalist => "pol.bloc.regionalist",
        _ => "pol.bloc.reformist"
    });

    /// <summary>Strictly normalize a 5-element bloc array so all entries are positive and sum to 1.0.</summary>
    public static void NormalizeBlocs(float[] blocs)
    {
        if (blocs is not { Length: BlocCount }) return;
        var sum = 0f;
        for (var i = 0; i < BlocCount; i++)
        {
            var v = float.IsFinite(blocs[i]) ? MathF.Max(0.02f, blocs[i]) : 0.20f;
            blocs[i] = v;
            sum += v;
        }
        if (sum <= 0f)
        {
            for (var i = 0; i < BlocCount; i++) blocs[i] = 0.20f;
            return;
        }
        var inv = 1f / sum;
        var running = 0f;
        for (var i = 0; i < BlocCount - 1; i++)
        {
            blocs[i] *= inv;
            running += blocs[i];
        }
        blocs[BlocCount - 1] = MathF.Max(0.001f, 1f - running);
    }

    /// <summary>Split conservation: copy exact bloc ratios to a carved-off region shard.</summary>
    public static void CopyBlocs(ScenarioState source, ScenarioState target)
    {
        if (target is null) return;
        if (source?.PoliticalBlocs is not { Length: BlocCount } src)
        {
            target.PoliticalBlocs = null;
            return;
        }
        var copy = new float[BlocCount];
        Array.Copy(src, copy, BlocCount);
        NormalizeBlocs(copy);
        target.PoliticalBlocs = copy;
    }

    /// <summary>Merge conservation: population-weighted convex combination preserving exact bloc headcount.</summary>
    public static void MergeBlocs(ScenarioState target, ScenarioState absorbed, long targetPop, long absorbedPop)
    {
        if (target is null) return;
        var a = target.PoliticalBlocs;
        var b = absorbed?.PoliticalBlocs;
        if (a is not { Length: BlocCount } && b is not { Length: BlocCount }) return;
        if (a is not { Length: BlocCount }) { CopyBlocs(absorbed, target); return; }
        if (b is not { Length: BlocCount }) { NormalizeBlocs(a); return; }

        var wa = Math.Max(1L, targetPop);
        var wb = Math.Max(1L, absorbedPop);
        var total = (double)(wa + wb);
        for (var i = 0; i < BlocCount; i++)
            a[i] = (float)((a[i] * (double)wa + b[i] * (double)wb) / total);
        NormalizeBlocs(a);
    }

    /// <summary>Population-weighted national cleavage readout, polarization, and law/reform friction (read-only).</summary>
    public static PoliticalCleavageSummary NationalBlocs(Campaign c, Nation n)
    {
        if (c is null || n is null) return default;
        var states = c.StatesOf(n);
        if (AlienationTables.TryGetValue(c, out var table) &&
            table.TryGetCleavage(n, states, c.ElapsedDays, out var cached))
            return cached;
        if (!On(c, "political_cohorts")) return default;
        double people = 0;
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0;
        for (var i = 0; i < states.Length; i++)
        {
            var s = states[i];
            if (s is null || s.Population <= 0 || s.PoliticalBlocs is not { Length: BlocCount } bl) continue;
            var pop = (double)s.Population;
            people += pop;
            b0 += pop * bl[0];
            b1 += pop * bl[1];
            b2 += pop * bl[2];
            b3 += pop * bl[3];
            b4 += pop * bl[4];
        }
        if (people <= 0) return default;
        return BuildCleavageSummary(n, people, b0, b1, b2, b3, b4);
    }

    private static PoliticalCleavageSummary BuildCleavageSummary(Nation n, double people,
        double b0, double b1, double b2, double b3, double b4)
    {
        var r = (float)(b0 / people);
        var sec = (float)(b1 / people);
        var ind = (float)(b2 / people);
        var trad = (float)(b3 / people);
        var reg = (float)(b4 / people);

        Span<float> shares = stackalloc float[] { r, sec, ind, trad, reg };
        var dom = 0;
        var maxShare = shares[0];
        var secondShare = 0f;
        for (var i = 1; i < BlocCount; i++)
        {
            if (shares[i] > maxShare) { secondShare = maxShare; maxShare = shares[i]; dom = i; }
            else if (shares[i] > secondShare) { secondShare = shares[i]; }
        }
        // Polarization peaks when two opposing blocs are both large or regionalism surges.
        var rivalry = M.Clamp01((maxShare + secondShare - 0.45f) * 1.4f) * M.Clamp01(1f - MathF.Abs(maxShare - secondShare) * 2.5f);
        var polarization = M.Clamp01(rivalry * 0.65f + reg * 1.1f);
        var friction = ComputeReformFriction(n, r, sec, ind, trad, reg);
        return new PoliticalCleavageSummary(r, sec, ind, trad, reg, (PoliticalBloc)dom, polarization, friction, true);
    }

    /// <summary>Early crisis pressure bonus from acute political polarization and peripheral regionalism (0..CleavageCrisisWeight).</summary>
    public static float CrisisPressureContribution(Campaign c, Nation n)
    {
        if (c is null || n is null || !On(c, "political_cohorts")) return 0f;
        var summary = NationalBlocs(c, n);
        if (!summary.Reviewed) return 0f;
        // Prosperous, stable states absorb ordinary pluralism without crisis pressure.
        if (n.Stability >= 0.72f && summary.Regionalist < 0.34f) return 0f;
        var stress = MathF.Max(0f, summary.Regionalist - 0.24f) * 1.6f +
                     MathF.Max(0f, summary.ReformFriction - 0.12f) * 1.2f;
        return M.Clamp(stress * MathF.Max(0f, c.Defines.CleavageCrisisWeight), 0f, MathF.Max(0f, c.Defines.CleavageCrisisWeight));
    }

    private static float ComputeReformFriction(Nation n, float reformist, float security, float industrialist, float traditionalist, float regionalist)
    {
        var ideology = IdeologyCatalog.Of(n.Ideology);
        var auth = ideology?.Authority ?? 0f;
        var econ = ideology?.Economic ?? 0f;
        // Friction rises when large blocs pull against the government's ideological posture.
        var antiAuth = auth > 0.15f ? reformist * auth : security * (-auth);
        var antiEcon = econ > 0.15f ? traditionalist * econ * 0.5f : industrialist * (-econ) * 0.6f;
        var raw = MathF.Max(0f, antiAuth) + MathF.Max(0f, antiEcon) + MathF.Max(0f, regionalist - 0.18f) * 0.85f;
        return M.Clamp(raw * 0.55f, 0f, 0.35f);
    }

    private static void ComputeBlocTargets(ScenarioState s, bool atWar, bool losing, bool debt,
        float shortage, float stability, float nationalLiving, bool capital, float alienation,
        float mil, float taxStress, float marketAccess, Span<float> target)
    {
        var lit = M.Clamp01(s.Literacy);
        var urb = M.Clamp01(s.Urbanization);
        var sol = M.Clamp01(s.StandardOfLiving);
        var rad = M.Clamp01(s.Radicalism);
        var solGap = MathF.Max(0f, nationalLiving - sol);

        var tReform = 0.14f + lit * 0.24f + urb * 0.14f + sol * 0.12f - (atWar ? 0.08f : 0f);
        var tSecurity = 0.12f + mil * 0.22f + (atWar ? 0.20f : 0f) + (losing ? 0.10f : 0f) + rad * 0.08f;
        var tIndust = 0.14f + urb * 0.22f + marketAccess * 0.14f - taxStress * 0.10f - (debt ? 0.06f : 0f);
        var tTrad = 0.14f + (1f - urb) * 0.22f + (1f - lit) * 0.14f + stability * 0.10f;
        var tReg = 0.06f + alienation * 0.34f + solGap * 0.45f + rad * 0.20f + shortage * 0.18f + (debt ? 0.08f : 0f) + taxStress * 0.08f;
        if (capital) tReg *= 0.40f;

        target[0] = MathF.Max(0.03f, tReform);
        target[1] = MathF.Max(0.03f, tSecurity);
        target[2] = MathF.Max(0.03f, tIndust);
        target[3] = MathF.Max(0.03f, tTrad);
        target[4] = MathF.Max(0.02f, tReg);
        var sum = target[0] + target[1] + target[2] + target[3] + target[4];
        var inv = sum > 0f ? 1f / sum : 0.20f;
        for (var i = 0; i < BlocCount; i++) target[i] *= inv;
    }

    private static void SeedBlocs(Nation n, ScenarioState s, bool atWar, bool debt, float alienation,
        float mil, float taxStress, float marketAccess)
    {
        Span<float> target = stackalloc float[BlocCount];
        ComputeBlocTargets(s, atWar, false, debt, n.ShortageIndex, n.Stability, n.StandardOfLiving,
            s.MapIndex == n.CapitalStateMapIndex, alienation, mil, taxStress, marketAccess, target);
        var arr = new float[BlocCount];
        for (var i = 0; i < BlocCount; i++) arr[i] = target[i];
        NormalizeBlocs(arr);
        s.PoliticalBlocs = arr;
    }

    private static void DriftBlocs(ScenarioState s, float step, bool atWar, bool losing, bool debt,
        float shortage, float stability, float nationalLiving, bool capital, float alienation,
        float mil, float taxStress, float marketAccess)
    {
        if (step <= 0f || s.PoliticalBlocs is not { Length: BlocCount } blocs) return;
        Span<float> target = stackalloc float[BlocCount];
        ComputeBlocTargets(s, atWar, losing, debt, shortage, stability, nationalLiving, capital, alienation,
            mil, taxStress, marketAccess, target);
        for (var i = 0; i < BlocCount; i++)
            blocs[i] = Move(blocs[i], target[i], step);
        NormalizeBlocs(blocs);
    }
}
