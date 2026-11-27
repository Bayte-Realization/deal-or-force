using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DealOrForce.Simulation;

/// <summary>
/// Writer-aware step-boundary access gate shared by the background simulation thread (<c>DoF-Sim</c>),
/// the Godot render/HUD main thread, and mutating commands/saves. Supports thread-local reentrant read
/// locks and signals a one-frame read window at step boundaries whenever a non-blocking read misses so
/// continuous Speed-5 stepping cannot starve visual updates.
/// </summary>
internal sealed class CampaignAccessGate
{
    private readonly object _sync = new();
    private int _readers;
    private bool _writerActive;
    private int _writerThreadId;
    private int _writerDepth;
    private int _upgradedReadDepth;
    private int _writeWaiters;
    private bool _readerRequested;
    private long _readerRequestedAt;
    private int _readerMissStreak;

    // Priority interactive lane: a map tap or dossier open that must not be dropped while time
    // runs. Separate from the routine HUD request flag above so an ordinary frame read can never
    // consume (clear) a tap's request. Runtime-only: nothing here is saved or fingerprinted.
    private int _priorityReaderRequests;
    private int _priorityReaderWaiters;
    private long _priorityRequestedAt;
    private const double PriorityRequestCeilingMs = 120.0;

    [ThreadStatic] private static CampaignAccessGate t_primaryGate;
    [ThreadStatic] private static int t_primaryDepth;
    [ThreadStatic] private static Dictionary<CampaignAccessGate, int> t_overflowDepths;

    public bool IsWriterActive => Volatile.Read(ref _writerActive);
    public bool IsWriterHeldByCurrentThread => Volatile.Read(ref _writerThreadId) == Environment.CurrentManagedThreadId;
    public bool IsHeldByCurrentThread => IsWriterHeldByCurrentThread || GetDepth(this) > 0;
    public bool HasWriteWaiter => Volatile.Read(ref _writeWaiters) > 0;
    public bool HasReaderRequest => Volatile.Read(ref _readerRequested);
    internal bool HasReaderRequested => Volatile.Read(ref _readerRequested);
    internal bool IsReaderStarving => Volatile.Read(ref _readerMissStreak) >= 2;

    /// <summary>
    /// An interactive reader (a tap, a dossier opened from the map) is waiting or holds a pending
    /// request younger than <see cref="PriorityRequestCeilingMs"/>. The background worker yields its
    /// write lock at the next stage boundary and does not re-acquire it while this is true.
    /// A stale request (its owner never retried) stops blocking after the ceiling by itself.
    /// </summary>
    internal bool HasPriorityReaderRequested =>
        Volatile.Read(ref _priorityReaderWaiters) > 0 ||
        (Volatile.Read(ref _priorityReaderRequests) > 0 &&
         Stopwatch.GetElapsedTime(Volatile.Read(ref _priorityRequestedAt)).TotalMilliseconds < PriorityRequestCeilingMs);

    /// <summary>Hold a priority read request open until <see cref="ClearPriorityRead"/>. Pairs one to one.</summary>
    public void RequestPriorityRead()
    {
        lock (_sync)
        {
            _priorityReaderRequests++;
            Volatile.Write(ref _priorityRequestedAt, Stopwatch.GetTimestamp());
            if (!_readerRequested) _readerRequestedAt = Stopwatch.GetTimestamp();
            _readerRequested = true;
        }
    }

    public void ClearPriorityRead()
    {
        lock (_sync)
        {
            if (_priorityReaderRequests > 0) _priorityReaderRequests--;
            if (_priorityReaderRequests == 0) Monitor.PulseAll(_sync);
        }
    }

    /// <summary>
    /// A read for an interactive action: marks itself as a priority waiter so the background
    /// worker preempts mid-stage and stays off the gate, then waits up to <paramref name="timeoutMs"/>.
    /// </summary>
    public bool TryEnterPriorityRead(int timeoutMs = 40)
    {
        if (GetDepth(this) > 0 || Volatile.Read(ref _writerThreadId) == Environment.CurrentManagedThreadId)
        {
            IncrementDepth(this);
            return true;
        }

        lock (_sync)
        {
            if (!_writerActive)
            {
                _readers++;
                _readerRequested = false;
                _readerMissStreak = 0;
                IncrementDepth(this);
                return true;
            }
            if (timeoutMs <= 0) return false;

            _priorityReaderWaiters++;
            try
            {
                Volatile.Write(ref _priorityRequestedAt, Stopwatch.GetTimestamp());
                if (!_readerRequested) _readerRequestedAt = Stopwatch.GetTimestamp();
                _readerRequested = true;
                var start = Stopwatch.GetTimestamp();
                var freqMs = Stopwatch.Frequency / 1000.0;
                while (_writerActive)
                {
                    var elapsedMs = (Stopwatch.GetTimestamp() - start) / freqMs;
                    var rem = (int)Math.Ceiling(timeoutMs - elapsedMs);
                    if (rem <= 0) return false;
                    Monitor.Wait(_sync, rem);
                }
                _readers++;
                _readerRequested = false;
                _readerMissStreak = 0;
                IncrementDepth(this);
                return true;
            }
            finally
            {
                _priorityReaderWaiters--;
                if (_priorityReaderWaiters == 0) Monitor.PulseAll(_sync);
            }
        }
    }

    private static int GetDepth(CampaignAccessGate gate)
    {
        if (ReferenceEquals(t_primaryGate, gate)) return t_primaryDepth;
        return t_overflowDepths is not null && t_overflowDepths.TryGetValue(gate, out var d) ? d : 0;
    }

    private static void IncrementDepth(CampaignAccessGate gate)
    {
        if (t_primaryGate is null || ReferenceEquals(t_primaryGate, gate))
        {
            t_primaryGate = gate;
            t_primaryDepth++;
            return;
        }
        (t_overflowDepths ??= new Dictionary<CampaignAccessGate, int>(ReferenceEqualityComparer.Instance))[gate] =
            GetDepth(gate) + 1;
    }

    private static bool DecrementDepth(CampaignAccessGate gate)
    {
        if (ReferenceEquals(t_primaryGate, gate))
        {
            if (t_primaryDepth <= 0) return false;
            t_primaryDepth--;
            if (t_primaryDepth == 0) t_primaryGate = null;
            return t_primaryDepth > 0;
        }
        if (t_overflowDepths is not null && t_overflowDepths.TryGetValue(gate, out var d) && d > 0)
        {
            if (d == 1) t_overflowDepths.Remove(gate);
            else t_overflowDepths[gate] = d - 1;
            return d > 1;
        }
        return false;
    }

    public bool TryEnterRead(int timeoutMs = 0, bool requestWindowOnMiss = true)
    {
        if (GetDepth(this) > 0 || Volatile.Read(ref _writerThreadId) == Environment.CurrentManagedThreadId)
        {
            IncrementDepth(this);
            return true;
        }

        lock (_sync)
        {
            if (!_writerActive)
            {
                _readers++;
                _readerRequested = false;
                _readerMissStreak = 0;
                IncrementDepth(this);
                return true;
            }
            if (timeoutMs <= 0)
            {
                if (requestWindowOnMiss) RequestReaderWindow();
                return false;
            }
            if (requestWindowOnMiss) RequestReaderWindow();
            var start = Stopwatch.GetTimestamp();
            var freqMs = Stopwatch.Frequency / 1000.0;
            while (_writerActive)
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - start) / freqMs;
                var rem = (int)Math.Ceiling(timeoutMs - elapsedMs);
                if (rem <= 0) return false;
                Monitor.Wait(_sync, rem);
            }
            _readers++;
            _readerRequested = false;
            _readerMissStreak = 0;
            IncrementDepth(this);
            return true;
        }
    }

    private void RequestReaderWindow()
    {
        if (!_readerRequested)
        {
            _readerRequestedAt = Stopwatch.GetTimestamp();
            _readerRequested = true;
        }
        _readerMissStreak++;
    }

    public void ExitRead()
    {
        if (GetDepth(this) <= 0) return;
        if (DecrementDepth(this)) return;
        if (Volatile.Read(ref _writerThreadId) == Environment.CurrentManagedThreadId) return;

        lock (_sync)
        {
            if (_readers > 0) _readers--;
            if (_readers == 0) Monitor.PulseAll(_sync);
        }
    }

    public void EnterWrite(bool asBackgroundWorker = false)
    {
        var tid = Environment.CurrentManagedThreadId;
        lock (_sync)
        {
            if (_writerActive && _writerThreadId == tid)
            {
                _writerDepth++;
                return;
            }

            // If this thread currently holds a read lock and upgrades to write (e.g. a UI refresh or
            // control callback invoked inside the main-thread read fence), release its reader reservation
            // across the write wait so it never deadlocks waiting on itself, then restore on ExitWrite.
            var heldReadDepth = GetDepth(this);
            if (heldReadDepth > 0)
            {
                if (_readers > 0) _readers--;
                Monitor.PulseAll(_sync);
            }

            if (!asBackgroundWorker) _writeWaiters++;
            var acquired = false;
            try
            {
                // The background worker also stays off while an interactive (priority) reader is
                // pending, so a tap is never re-locked out between frames. That request expires on
                // its own clock without a pulse, hence the short timed wait for the worker.
                while (_writerActive || _readers > 0 ||
                       (asBackgroundWorker && (_writeWaiters > 0 || HasPriorityReaderRequested)))
                {
                    if (asBackgroundWorker) Monitor.Wait(_sync, 4);
                    else Monitor.Wait(_sync);
                }
                _writerActive = true;
                _writerThreadId = tid;
                _writerDepth = 1;
                _upgradedReadDepth = heldReadDepth;
                acquired = true;
            }
            finally
            {
                if (!asBackgroundWorker) _writeWaiters--;
                if (!acquired && heldReadDepth > 0)
                {
                    _readers++;
                    Monitor.PulseAll(_sync);
                }
            }
        }
    }

    internal bool TryEnterBackgroundWrite()
    {
        lock (_sync)
        {
            if (_writerActive || _readers > 0 || _writeWaiters > 0 || HasPriorityReaderRequested) return false;
            _writerActive = true;
            _writerThreadId = Environment.CurrentManagedThreadId;
            _writerDepth = 1;
            return true;
        }
    }

    public void ExitWrite(bool yieldToRequestedReader = false, int readerWindowMs = 24, int readerWorkAheadMs = 0)
    {
        lock (_sync)
        {
            if (_writerDepth > 1)
            {
                _writerDepth--;
                return;
            }

            var hadUpgradedRead = _upgradedReadDepth > 0;
            if (hadUpgradedRead)
            {
                _readers++;
                _upgradedReadDepth = 0;
            }

            _writerDepth = 0;
            _writerActive = false;
            _writerThreadId = 0;
            Monitor.PulseAll(_sync);

            if (!hadUpgradedRead && yieldToRequestedReader && _writeWaiters == 0 && HasPriorityReaderRequested)
            {
                // An interactive tap is waiting: no work-ahead. Hold the gate open for up to two
                // 30 FPS frames until the reader has come and gone, the request is cleared, or a
                // command writer arrives. EnterWrite keeps the worker off after this anyway.
                var windowMs = Math.Max(readerWindowMs, 65);
                var pStart = Stopwatch.GetTimestamp();
                var pFreqMs = Stopwatch.Frequency / 1000.0;
                while (_writeWaiters == 0)
                {
                    if (_readers > 0)
                    {
                        while (_readers > 0) Monitor.Wait(_sync, 10);
                        if (!HasPriorityReaderRequested || _writeWaiters > 0) break;
                    }
                    if (!HasPriorityReaderRequested) break;
                    var rem = (int)Math.Ceiling(windowMs - (Stopwatch.GetTimestamp() - pStart) / pFreqMs);
                    if (rem <= 0) break;
                    Monitor.Wait(_sync, Math.Min(rem, 4));
                }
                return;
            }

            if (!hadUpgradedRead && yieldToRequestedReader && _readerRequested && _writeWaiters == 0 && readerWindowMs > 0)
            {
                // A missed read can use the last published view. On mobile, let the
                // worker do a little more before it idles for the next read window,
                // unless consecutive misses show a 30 FPS reader is starving across windows.
                var starvingReader = Volatile.Read(ref _readerMissStreak) >= 2;
                if (!starvingReader && readerWorkAheadMs > 0 && Stopwatch.GetElapsedTime(_readerRequestedAt).TotalMilliseconds < readerWorkAheadMs)
                    return;

                // A starving 30 FPS routine reader gets one 33.3 ms frame plus OS timer margin (50 ms) so
                // early stage breaks on IsReaderStarving hold the window open until the frame arrives,
                // keeping maxConsecutiveMisses <= 2 while immediately breaking once the reader enters and exits.
                var effectiveWindowMs = starvingReader ? Math.Max(readerWindowMs, 50) : readerWindowMs;
                var start = Stopwatch.GetTimestamp();
                var freqMs = Stopwatch.Frequency / 1000.0;
                while (_readerRequested && _writeWaiters == 0)
                {
                    if (_readers > 0)
                    {
                        while (_readers > 0) Monitor.Wait(_sync, 10);
                        break;
                    }
                    var elapsedMs = (Stopwatch.GetTimestamp() - start) / freqMs;
                    var rem = (int)Math.Ceiling(effectiveWindowMs - elapsedMs);
                    if (rem <= 0)
                    {
                        _readerRequested = false;
                        break;
                    }
                    Monitor.Wait(_sync, Math.Min(rem, 6));
                }
            }
        }
    }
}

/// <summary>Immutable per-country summary inside <see cref="CampaignRenderSnapshot"/>.</summary>
public readonly record struct NationSnapshot(
    int Id,
    int MapIndex,
    string Tag,
    string DisplayName,
    string MapName,
    string Color,
    bool IsAlive,
    bool IsPlayer,
    bool AtWar,
    bool HasMovement,
    long Population,
    float Gdp,
    float Treasury,
    float Stability);

/// <summary>
/// Atomic immutable frame-read snapshot published by <see cref="BackgroundSimRunner"/> at step
/// boundaries so the render thread and shell HUD never allocate or race on live <see cref="Campaign"/> lists.
/// </summary>
public sealed class CampaignRenderSnapshot
{
    [ThreadStatic] private static HashSet<int> t_atWarIds;

    public static readonly CampaignRenderSnapshot Empty = new(
        version: 0,
        elapsedDays: 0,
        elapsedHours: 0,
        dayOfYear: 1,
        date: default,
        mapRevision: -1,
        warRevision: -1,
        ownershipRevision: -1,
        allianceRevision: -1,
        capitalRevision: -1,
        movementRevision: -1,
        destroyedProvinceCount: 0,
        isPaused: true,
        speed: 1,
        worldFrozen: false,
        stepOpensNewDay: false,
        playerId: -1,
        playerMapIndex: -1,
        observerMode: true,
        simulationFault: "",
        worldCurse: "",
        controllerMapRevision: -1,
        provinceController: Array.Empty<ushort>(),
        ownerRevision: -1,
        provinceOwner: Array.Empty<ushort>(),
        nations: Array.Empty<NationSnapshot>(),
        slotById: Array.Empty<int>(),
        slotByMapIndex: Array.Empty<int>(),
        playerOverallRank: 0,
        playerOverallScore: 0.0,
        rankWeek: -1,
        rankMapRev: -1,
        rankWarRev: -1,
        rankPlayerId: -1,
        nationRanksById: new Dictionary<int, int>(0), rankingRows: Array.Empty<CountryRankings.Row>());

    public readonly long Version;
    public readonly int ElapsedDays;
    public readonly long ElapsedHours;
    public readonly int DayOfYear;
    public readonly GameDate Date;
    public readonly int MapRevision;
    public readonly int WarRevision;
    public readonly int OwnershipRevision;
    public readonly int AllianceRevision;
    public readonly int CapitalRevision;
    public readonly int MovementRevision;
    public readonly int DestroyedProvinceCount;
    public readonly bool IsPaused;
    public readonly int Speed;
    public readonly bool WorldFrozen;
    public readonly bool StepOpensNewDay;
    public readonly int PlayerId;
    public readonly int PlayerMapIndex;
    public readonly bool ObserverMode;
    public readonly string SimulationFault;
    public readonly string WorldCurse;
    public readonly int ControllerMapRevision;
    public readonly ushort[] ProvinceController;
    public readonly int OwnerRevision;
    public readonly ushort[] ProvinceOwner;
    public readonly NationSnapshot[] Nations;
    private readonly int[] _slotById;
    private readonly int[] _slotByMapIndex;
    public readonly int PlayerOverallRank;
    public readonly double PlayerOverallScore;
    public readonly IReadOnlyDictionary<int, int> NationRanksById; public readonly IReadOnlyList<CountryRankings.Row> RankingRows;

    internal readonly int RankWeek;
    internal readonly int RankMapRev;
    internal readonly int RankWarRev;
    internal readonly int RankPlayerId;

    private CampaignRenderSnapshot(
        long version, int elapsedDays, long elapsedHours, int dayOfYear, GameDate date,
        int mapRevision, int warRevision, int ownershipRevision, int allianceRevision,
        int capitalRevision, int movementRevision, int destroyedProvinceCount,
        bool isPaused, int speed, bool worldFrozen, bool stepOpensNewDay,
        int playerId, int playerMapIndex, bool observerMode,
        string simulationFault, string worldCurse,
        int controllerMapRevision, ushort[] provinceController,
        int ownerRevision, ushort[] provinceOwner,
        NationSnapshot[] nations, int[] slotById, int[] slotByMapIndex,
        int playerOverallRank, double playerOverallScore,
        int rankWeek, int rankMapRev, int rankWarRev, int rankPlayerId,
        IReadOnlyDictionary<int, int> nationRanksById, IReadOnlyList<CountryRankings.Row> rankingRows = null)
    {
        Version = version;
        ElapsedDays = elapsedDays;
        ElapsedHours = elapsedHours;
        DayOfYear = dayOfYear;
        Date = date;
        MapRevision = mapRevision;
        WarRevision = warRevision;
        OwnershipRevision = ownershipRevision;
        AllianceRevision = allianceRevision;
        CapitalRevision = capitalRevision;
        MovementRevision = movementRevision;
        DestroyedProvinceCount = destroyedProvinceCount;
        IsPaused = isPaused;
        Speed = speed;
        WorldFrozen = worldFrozen;
        StepOpensNewDay = stepOpensNewDay;
        PlayerId = playerId;
        PlayerMapIndex = playerMapIndex;
        ObserverMode = observerMode;
        SimulationFault = simulationFault;
        WorldCurse = worldCurse;
        ControllerMapRevision = controllerMapRevision;
        ProvinceController = provinceController;
        OwnerRevision = ownerRevision;
        ProvinceOwner = provinceOwner;
        Nations = nations;
        _slotById = slotById;
        _slotByMapIndex = slotByMapIndex;
        PlayerOverallRank = playerOverallRank;
        PlayerOverallScore = playerOverallScore;
        RankWeek = rankWeek;
        RankMapRev = rankMapRev;
        RankWarRev = rankWarRev;
        RankPlayerId = rankPlayerId;
        NationRanksById = nationRanksById; RankingRows = rankingRows ?? Array.Empty<CountryRankings.Row>();
    }

    public bool TryGetNation(int nationId, out NationSnapshot summary)
    {
        if ((uint)nationId < (uint)_slotById.Length)
        {
            var slot = _slotById[nationId];
            if (slot >= 0) { summary = Nations[slot]; return true; }
        }
        summary = default;
        return false;
    }

    public bool TryGetNationByMapIndex(int mapIndex, out NationSnapshot summary)
    {
        if ((uint)mapIndex < (uint)_slotByMapIndex.Length)
        {
            var slot = _slotByMapIndex[mapIndex];
            if (slot >= 0) { summary = Nations[slot]; return true; }
        }
        summary = default;
        return false;
    }

    private static int ComputeDayOfYear(GameDate date)
    {
        var month = Math.Clamp(date.Month, 1, 12);
        var day = Math.Max(1, date.Day);
        var daysBefore = 0;
        for (var m = 1; m < month; m++)
            daysBefore += m switch { 1 or 3 or 5 or 7 or 8 or 10 or 12 => 31, 4 or 6 or 9 or 11 => 30, _ => HistoricalDate.IsLeapYear(date.Year) ? 29 : 28 };
        return daysBefore + day;
    }

    internal static CampaignRenderSnapshot Capture(
        Campaign campaign, CampaignRenderSnapshot previous, CountryRankings.Weights rankWeights, long version)
    {
        if (campaign is null) return Empty;
        previous ??= Empty;

        ushort[] provinceController;
        int controllerRev;
        var srcController = campaign.ProvinceController ?? campaign.ProvinceMap?.ProvinceToNation;
        if (srcController is null)
        {
            provinceController = Array.Empty<ushort>();
            controllerRev = -1;
        }
        else
        {
            controllerRev = campaign.MapRevision;
            provinceController = ReuseProvinceRow(previous.ProvinceController, previous.ControllerMapRevision, srcController, controllerRev);
        }

        ushort[] provinceOwner;
        int ownerRev;
        var srcOwner = campaign.ProvinceMap?.ProvinceToNation;
        if (srcOwner is null)
        {
            provinceOwner = Array.Empty<ushort>();
            ownerRev = -1;
        }
        else
        {
            ownerRev = campaign.OwnershipRevision;
            provinceOwner = ReuseProvinceRow(previous.ProvinceOwner, previous.OwnerRevision, srcOwner, ownerRev);
        }

        var rawNations = campaign.Nations;
        var count = rawNations.Count;
        var sameSlots = previous.Nations.Length == count;
        for (var i = 0; sameSlots && i < count; i++)
        {
            var n = rawNations[i];
            sameSlots = previous.Nations[i].Id == n.Id && previous.Nations[i].MapIndex == n.MapIndex;
        }

        var slotById = previous._slotById;
        var slotByMap = previous._slotByMapIndex;
        if (!sameSlots)
        {
            var maxId = 0;
            var maxMap = 0;
            for (var i = 0; i < count; i++)
            {
                var n = rawNations[i];
                if (n.Id > maxId) maxId = n.Id;
                if (n.MapIndex > maxMap) maxMap = n.MapIndex;
            }
            slotById = new int[maxId + 1];
            slotByMap = new int[maxMap + 1];
            Array.Fill(slotById, -1);
            Array.Fill(slotByMap, -1);
            for (var i = 0; i < count; i++)
            {
                var n = rawNations[i];
                if ((uint)n.Id < (uint)slotById.Length) slotById[n.Id] = i;
                if ((uint)n.MapIndex < (uint)slotByMap.Length) slotByMap[n.MapIndex] = i;
            }
        }

        var atWarIds = t_atWarIds ??= new HashSet<int>();
        atWarIds.Clear();
        var liveWars = Warfare.LiveWars(campaign);
        for (var w = 0; w < liveWars.Count; w++)
        {
            var war = liveWars[w];
            atWarIds.Add(war.AttackerId);
            atWarIds.Add(war.DefenderId);
            foreach (var ally in war.AttackerAllies) atWarIds.Add(ally);
            foreach (var ally in war.DefenderAllies) atWarIds.Add(ally);
        }

        // Readers may hold the previous snapshot across frames. Share its country rows while
        // they match, then copy the matching prefix into a fresh array on the first change.
        var reuseSummaries = sameSlots;
        var summaries = reuseSummaries ? previous.Nations : new NationSnapshot[count];
        var playerId = campaign.Player?.Id ?? -1;
        var names = campaign.Caches.SnapshotNames;
        names.Prepare(count);
        for (var i = 0; i < count; i++)
        {
            var n = rawNations[i];
            // Nation.MapName uses the same display name.
            var name = names.Get(n, i);
            var summary = new NationSnapshot(
                n.Id,
                n.MapIndex,
                n.Tag,
                name,
                name,
                n.Color,
                IsAlive: n.RetiredDay < 0,
                IsPlayer: n.Id == playerId,
                AtWar: atWarIds.Contains(n.Id),
                HasMovement: n.Movement is not null,
                n.Population,
                n.Gdp,
                n.Treasury,
                n.Stability);
            if (reuseSummaries)
            {
                if (summary.Equals(summaries[i])) continue;
                summaries = new NationSnapshot[count];
                Array.Copy(previous.Nations, summaries, i);
                reuseSummaries = false;
            }
            summaries[i] = summary;
        }

        var week = campaign.ElapsedDays / 7;
        var rankPlayerId = campaign.Player?.Id ?? -1;
        var overallRank = previous.PlayerOverallRank;
        var overallScore = previous.PlayerOverallScore;
        var ranksById = previous.NationRanksById; var rankingRows = previous.RankingRows;
        var rankWeek = previous.RankWeek;
        var rankMapRev = previous.RankMapRev;
        var rankWarRev = previous.RankWarRev;
        if (previous.Version == 0 ||
            rankWeek != week ||
            previous.RankPlayerId != rankPlayerId ||
            ranksById.Count == 0 || rankingRows.Count == 0 ||
            (campaign.IsPaused && (rankMapRev != campaign.MapRevision || rankWarRev != campaign.WarRevision)))
        {
            var rows = CountryRankings.Read(campaign, rankWeights);
            var map = new Dictionary<int, int>(rows.Count);
            overallRank = 0;
            overallScore = 0.0;
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                map[r.NationId] = r.OverallRank;
                if (r.NationId == rankPlayerId)
                {
                    overallRank = r.OverallRank;
                    overallScore = r.Overall;
                }
            }
            ranksById = map; rankingRows = rows;
            rankWeek = week;
            rankMapRev = campaign.MapRevision;
            rankWarRev = campaign.WarRevision;
        }

        return new CampaignRenderSnapshot(
            version,
            campaign.ElapsedDays,
            campaign.ElapsedHours,
            ComputeDayOfYear(campaign.Date),
            campaign.Date,
            campaign.MapRevision,
            campaign.WarRevision,
            campaign.OwnershipRevision,
            campaign.AllianceRevision,
            campaign.CapitalRevision,
            campaign.MovementRevision,
            campaign.DestroyedProvinces.Count,
            campaign.IsPaused,
            campaign.Speed,
            WorldCurses.Frozen(campaign),
            campaign.StepOpensNewDay,
            playerId,
            campaign.Player?.MapIndex ?? -1,
            campaign.ObserverMode,
            campaign.SimulationFault ?? "",
            campaign.Curse ?? "",
            controllerRev,
            provinceController,
            ownerRev,
            provinceOwner,
            summaries,
            slotById,
            slotByMap,
            overallRank,
            overallScore,
            rankWeek,
            rankMapRev,
            rankWarRev,
            rankPlayerId,
            ranksById, rankingRows);
    }

    private static ushort[] ReuseProvinceRow(ushort[] previous, int previousRevision, ushort[] source, int revision)
    {
        // Other map data can invalidate the revision without changing any flag in this row.
        if (previous.Length == source.Length && (previousRevision == revision || previous.AsSpan().SequenceEqual(source)))
            return previous;
        return (ushort[])source.Clone();
    }

    internal CampaignRenderSnapshot WithClock(long version, Campaign campaign) =>
        new(version,
            campaign.ElapsedDays,
            campaign.ElapsedHours,
            ComputeDayOfYear(campaign.Date),
            campaign.Date,
            MapRevision,
            WarRevision,
            OwnershipRevision,
            AllianceRevision,
            CapitalRevision,
            MovementRevision,
            DestroyedProvinceCount,
            campaign.IsPaused,
            campaign.Speed,
            WorldCurses.Frozen(campaign),
            campaign.StepOpensNewDay,
            PlayerId,
            PlayerMapIndex,
            ObserverMode,
            campaign.SimulationFault ?? "",
            campaign.Curse ?? "",
            ControllerMapRevision,
            ProvinceController,
            OwnerRevision,
            ProvinceOwner,
            Nations,
            _slotById,
            _slotByMapIndex,
            PlayerOverallRank,
            PlayerOverallScore,
            RankWeek,
            RankMapRev,
            RankWarRev,
            RankPlayerId,
            NationRanksById, RankingRows);
}

/// <summary>
/// Compact immutable summary of a player war for lock-free HUD rendering.
/// </summary>
public readonly record struct HudWarSummary(
    int Id,
    int EnemyId,
    string EnemyTag,
    string EnemyName,
    bool IsAttacking,
    float WarScore,
    int StartedDay);

/// <summary>
/// Atomic immutable HUD telemetry snapshot published at step boundaries alongside
/// <see cref="CampaignRenderSnapshot"/> so main-thread UI refreshes read consistent numbers
/// without acquiring <see cref="CampaignAccessGate"/> or blocking <c>DoF-Sim</c>.
/// </summary>
public sealed class CampaignHudSnapshot
{
    [ThreadStatic] private static HashSet<int> t_atWarIds;
    [ThreadStatic] private static List<HudWarSummary> t_playerWars;

    public static readonly CampaignHudSnapshot Empty = new(
        version: 0,
        elapsedDays: 0,
        elapsedHours: 0,
        speed: 1,
        isPaused: true,
        observerMode: true,
        hasSimulationFault: false,
        mapRevision: -1,
        warRevision: -1,
        ownershipRevision: -1,
        playerId: -1,
        playerTag: "",
        playerResearchEmpty: true,
        supportScreenEnabled: false,
        treasury: 0f,
        weeklyBalance: 0f,
        gdp: 0f,
        population: 0L,
        reviewNetPop: 0L,
        regionCount: 0,
        publicDebt: 0f,
        debtArrears: 0f,
        paymentArrears: 0f,
        manpowerReserve: 0f,
        reinforcementCapacity: 0f,
        inflation: 0f,
        stability: 0f,
        politicalPoints: 0f,
        ppWeekly: 0f,
        prestige: 0f,
        warSupport: 0f,
        armyExperience: 0f,
        airExperience: 0f,
        navalExperience: 0f,
        defconLevel: 5,
        worldTension: 0f,
        playerEngagedArmies: 0,
        playerDrillingArmies: 0,
        playerFlyingAir: 0,
        playerAtSeaFleets: 0,
        playerIdleArmies: 0,
        playerIdleSupport: 0,
        playerTotalDivisions: 0,
        pendingEventCount: 0,
        incomingDiplomaticCount: 0,
        chronicleLatestSeq: 0L,
        openPlayerWars: Array.Empty<HudWarSummary>(),
        atWarNationIds: Array.Empty<int>());

    public readonly long Version;
    public readonly int ElapsedDays;
    public readonly long ElapsedHours;
    public readonly int Speed;
    public readonly bool IsPaused;
    public readonly bool ObserverMode;
    public readonly bool HasSimulationFault;
    public readonly int MapRevision;
    public readonly int WarRevision;
    public readonly int OwnershipRevision;
    public readonly int PlayerId;
    public readonly string PlayerTag;
    public readonly bool PlayerResearchEmpty;
    public readonly bool SupportScreenEnabled;
    public readonly float Treasury;
    public readonly float WeeklyBalance;
    public readonly float Gdp;
    public readonly long Population;
    public readonly long ReviewNetPop;
    public readonly int RegionCount;
    public readonly float PublicDebt;
    public readonly float DebtArrears;
    public readonly float PaymentArrears;
    public readonly float ManpowerReserve;
    public readonly float ReinforcementCapacity;
    public readonly float Inflation;
    public readonly float Stability;
    public readonly float PoliticalPoints;
    public readonly float PpWeekly;
    public readonly float Prestige;
    public readonly float WarSupport;
    public readonly float ArmyExperience;
    public readonly float AirExperience;
    public readonly float NavalExperience;
    public readonly int DefconLevel;
    public readonly float WorldTension;
    public readonly int PlayerEngagedArmies;
    public readonly int PlayerDrillingArmies;
    public readonly int PlayerFlyingAir;
    public readonly int PlayerAtSeaFleets;
    public readonly int PlayerIdleArmies;
    public readonly int PlayerIdleSupport;
    public readonly int PlayerTotalDivisions;
    public readonly int PendingEventCount;
    public readonly int IncomingDiplomaticCount;
    public readonly long ChronicleLatestSeq;
    public readonly HudWarSummary[] OpenPlayerWars;
    public readonly int[] AtWarNationIds;

    private CampaignHudSnapshot(
        long version, int elapsedDays, long elapsedHours, int speed, bool isPaused,
        bool observerMode, bool hasSimulationFault, int mapRevision, int warRevision,
        int ownershipRevision, int playerId, string playerTag, bool playerResearchEmpty,
        bool supportScreenEnabled, float treasury, float weeklyBalance, float gdp,
        long population, long reviewNetPop, int regionCount, float publicDebt,
        float debtArrears, float paymentArrears, float manpowerReserve,
        float reinforcementCapacity, float inflation, float stability,
        float politicalPoints, float ppWeekly, float prestige, float warSupport,
        float armyExperience, float airExperience, float navalExperience,
        int defconLevel, float worldTension, int playerEngagedArmies,
        int playerDrillingArmies, int playerFlyingAir, int playerAtSeaFleets,
        int playerIdleArmies, int playerIdleSupport, int playerTotalDivisions,
        int pendingEventCount, int incomingDiplomaticCount, long chronicleLatestSeq,
        HudWarSummary[] openPlayerWars, int[] atWarNationIds)
    {
        Version = version;
        ElapsedDays = elapsedDays;
        ElapsedHours = elapsedHours;
        Speed = speed;
        IsPaused = isPaused;
        ObserverMode = observerMode;
        HasSimulationFault = hasSimulationFault;
        MapRevision = mapRevision;
        WarRevision = warRevision;
        OwnershipRevision = ownershipRevision;
        PlayerId = playerId;
        PlayerTag = playerTag;
        PlayerResearchEmpty = playerResearchEmpty;
        SupportScreenEnabled = supportScreenEnabled;
        Treasury = treasury;
        WeeklyBalance = weeklyBalance;
        Gdp = gdp;
        Population = population;
        ReviewNetPop = reviewNetPop;
        RegionCount = regionCount;
        PublicDebt = publicDebt;
        DebtArrears = debtArrears;
        PaymentArrears = paymentArrears;
        ManpowerReserve = manpowerReserve;
        ReinforcementCapacity = reinforcementCapacity;
        Inflation = inflation;
        Stability = stability;
        PoliticalPoints = politicalPoints;
        PpWeekly = ppWeekly;
        Prestige = prestige;
        WarSupport = warSupport;
        ArmyExperience = armyExperience;
        AirExperience = airExperience;
        NavalExperience = navalExperience;
        DefconLevel = defconLevel;
        WorldTension = worldTension;
        PlayerEngagedArmies = playerEngagedArmies;
        PlayerDrillingArmies = playerDrillingArmies;
        PlayerFlyingAir = playerFlyingAir;
        PlayerAtSeaFleets = playerAtSeaFleets;
        PlayerIdleArmies = playerIdleArmies;
        PlayerIdleSupport = playerIdleSupport;
        PlayerTotalDivisions = playerTotalDivisions;
        PendingEventCount = pendingEventCount;
        IncomingDiplomaticCount = incomingDiplomaticCount;
        ChronicleLatestSeq = chronicleLatestSeq;
        OpenPlayerWars = openPlayerWars;
        AtWarNationIds = atWarNationIds;
    }

    public bool IsNationAtWar(int nationId) =>
        AtWarNationIds.Length > 0 && Array.BinarySearch(AtWarNationIds, nationId) >= 0;

    public static CampaignHudSnapshot Capture(Campaign campaign, long version, CampaignHudSnapshot previous = null)
    {
        if (campaign is null) return Empty;
        var prev = previous ?? Empty;
        var player = campaign.Player;
        var pid = player?.Id ?? -1;
        var obs = campaign.ObserverMode;

        var openWars = Warfare.OpenWars(campaign);
        int[] atWarIds;
        if (prev.Version > 0 && prev.WarRevision == campaign.WarRevision && prev.AtWarNationIds is not null)
        {
            atWarIds = prev.AtWarNationIds;
        }
        else if (openWars.Count == 0)
        {
            atWarIds = Array.Empty<int>();
        }
        else
        {
            var set = t_atWarIds ??= new HashSet<int>();
            set.Clear();
            for (var i = 0; i < openWars.Count; i++)
            {
                var w = openWars[i];
                if (w.Ended) continue;
                set.Add(w.AttackerId);
                set.Add(w.DefenderId);
                foreach (var a in w.AttackerAllies) set.Add(a);
                foreach (var d in w.DefenderAllies) set.Add(d);
            }
            if (set.Count == 0)
            {
                atWarIds = Array.Empty<int>();
            }
            else
            {
                atWarIds = new int[set.Count];
                set.CopyTo(atWarIds);
                Array.Sort(atWarIds);
            }
        }

        HudWarSummary[] playerWars;
        if (pid < 0 || openWars.Count == 0)
        {
            playerWars = Array.Empty<HudWarSummary>();
        }
        else
        {
            var list = t_playerWars ??= new List<HudWarSummary>(4);
            list.Clear();
            for (var i = 0; i < openWars.Count; i++)
            {
                var w = openWars[i];
                if (w.Ended || !w.Involves(pid)) continue;
                var isAtk = w.IsAttacking(pid);
                var enemyId = isAtk ? w.DefenderId : w.AttackerId;
                var enemy = campaign.NationById(enemyId);
                list.Add(new HudWarSummary(
                    w.Id,
                    enemyId,
                    enemy?.Tag ?? "",
                    enemy?.DisplayName ?? "",
                    isAtk,
                    w.WarScore,
                    w.StartedDay));
            }
            if (list.Count == 0)
            {
                playerWars = Array.Empty<HudWarSummary>();
            }
            else if (prev.OpenPlayerWars.Length == list.Count && prev.WarRevision == campaign.WarRevision && prev.PlayerId == pid)
            {
                var same = true;
                for (var i = 0; i < list.Count; i++)
                {
                    if (!list[i].Equals(prev.OpenPlayerWars[i])) { same = false; break; }
                }
                playerWars = same ? prev.OpenPlayerWars : list.ToArray();
            }
            else
            {
                playerWars = list.ToArray();
            }
        }

        var engaged = 0;
        var drilling = 0;
        var idleArmies = 0;
        var totalDivisions = 0;
        if (pid >= 0)
        {
            var armies = campaign.Armies;
            for (var i = 0; i < armies.Count; i++)
            {
                var a = armies[i];
                if (a.NationId != pid) continue;
                totalDivisions += (int)MathF.Ceiling(a.Divisions);
                if (a.InBattle) engaged++;
                else if (a.Training) drilling++;
                if (playerWars.Length > 0 && a.FrontLineId < 0 && a.AssignedWarId < 0 && !a.DirectOrder && !a.Training && !a.InBattle)
                    idleArmies++;
            }
        }

        var flying = 0;
        var idleSupport = 0;
        if (pid >= 0)
        {
            var formations = campaign.SupportFormations;
            for (var i = 0; i < formations.Count; i++)
            {
                var f = formations[i];
                if (f.NationId != pid) continue;
                if (f.Strength > 0.05f && !string.IsNullOrEmpty(f.Mission) &&
                    !string.Equals(f.Mission, SupportCore.MissionStandby, StringComparison.Ordinal))
                    flying++;
                if (playerWars.Length > 0 && !f.CutOff && f.Route.Count == 0 &&
                    string.Equals(f.Mission, SupportCore.MissionStandby, StringComparison.Ordinal))
                    idleSupport++;
            }
        }

        var atSea = 0;
        if (pid >= 0)
        {
            var fleets = campaign.Fleets;
            for (var i = 0; i < fleets.Count; i++)
            {
                var fl = fleets[i];
                if (fl.NationId == pid && fl.Hulls > 0 && (fl.InAction || fl.Mission != FleetMission.Anchored))
                    atSea++;
            }
        }

        var pendingEvents = 0;
        if (pid >= 0)
        {
            var events = campaign.PendingEvents;
            for (var i = 0; i < events.Count; i++)
                if (events[i].NationId == pid) pendingEvents++;
        }

        var incomingDeals = 0;
        if (player is not null && !obs)
        {
            incomingDeals = DealNegotiations.Incoming(campaign, pid)
                + ForeignAssistance.Incoming(campaign, pid)
                + ArmsShipmentRequests.Incoming(campaign, pid)
                + Ultimatum.Incoming(campaign, pid)
                + MilitaryAccess.Incoming(campaign, pid)
                + DiplomaticActs.Incoming(campaign, pid)
                + AllyRivalShift.Incoming(campaign, pid);
            var leases = campaign.LeaseOffers;
            for (var i = 0; i < leases.Count; i++)
            {
                var o = leases[i];
                if (o.ToId == pid && o.ExpiresDay > campaign.ElapsedDays && string.Equals(o.State, "pending", StringComparison.Ordinal))
                    incomingDeals++;
            }
            var nations = campaign.Nations;
            for (var i = 0; i < nations.Count; i++)
            {
                var other = nations[i];
                if (other.Id == pid) continue;
                var rel = CampaignDiplomacy.Peek(campaign, pid, other.Id, out var awake);
                if (!awake || rel is null) continue;
                var propId = rel.AgreementOfferedBy;
                if (propId < 0 || propId == pid) continue;
                if (!AgreementSystem.OfferPending(campaign, rel)) continue;
                if (campaign.NationById(propId) is not null) incomingDeals++;
            }
        }

        var chronicleEntries = campaign.Chronicle.Entries;
        var chronicleSeq = chronicleEntries.Count > 0 ? chronicleEntries[^1].Sequence : 0L;
        var ppWeekly = player is null ? 0f :
            campaign.Defines.PoliticalPointsPerWeek + campaign.Defines.PoliticalPointsStabilityWeight * Math.Clamp(player.Stability, 0f, 1f);

        return new CampaignHudSnapshot(
            version: version,
            elapsedDays: campaign.ElapsedDays,
            elapsedHours: campaign.ElapsedHours,
            speed: campaign.Speed,
            isPaused: campaign.IsPaused,
            observerMode: obs,
            hasSimulationFault: campaign.SimulationFault is { Length: > 0 },
            mapRevision: campaign.MapRevision,
            warRevision: campaign.WarRevision,
            ownershipRevision: campaign.OwnershipRevision,
            playerId: pid,
            playerTag: player?.Tag ?? "",
            playerResearchEmpty: player is null || string.IsNullOrEmpty(player.CurrentResearch),
            supportScreenEnabled: MechanicFeatures.ScreenEnabled(campaign, "Support"),
            treasury: player?.Treasury ?? 0f,
            weeklyBalance: player?.WeeklyBalance ?? 0f,
            gdp: player?.Gdp ?? 0f,
            population: player?.Population ?? 0L,
            reviewNetPop: player is null ? 0L : (player.ReviewBirths - player.ReviewDeaths + player.ReviewNetMigration),
            regionCount: player is null ? 0 : campaign.StatesOf(player).Length,
            publicDebt: player is null ? 0f : SovereignFinance.Outstanding(player),
            debtArrears: player?.DebtArrears ?? 0f,
            paymentArrears: player?.PaymentArrears ?? 0f,
            manpowerReserve: player?.ManpowerReserve ?? 0f,
            reinforcementCapacity: player?.ReinforcementCapacity ?? 0f,
            inflation: player?.Inflation ?? 0f,
            stability: player?.Stability ?? 0f,
            politicalPoints: player?.PoliticalPoints ?? 0f,
            ppWeekly: ppWeekly,
            prestige: player?.Prestige ?? 0f,
            warSupport: player?.WarSupport ?? 0f,
            armyExperience: player?.ArmyExperience ?? 0f,
            airExperience: player?.AirExperience ?? 0f,
            navalExperience: player?.NavalExperience ?? 0f,
            defconLevel: Math.Clamp(DefconSystem.CurrentLevel(campaign), 1, 5),
            worldTension: Math.Clamp(WorldWar.Tension(campaign) / 100f, 0f, 1f),
            playerEngagedArmies: engaged,
            playerDrillingArmies: drilling,
            playerFlyingAir: flying,
            playerAtSeaFleets: atSea,
            playerIdleArmies: idleArmies,
            playerIdleSupport: idleSupport,
            playerTotalDivisions: totalDivisions,
            pendingEventCount: pendingEvents,
            incomingDiplomaticCount: incomingDeals,
            chronicleLatestSeq: chronicleSeq,
            openPlayerWars: playerWars,
            atWarNationIds: atWarIds);
    }
}

/// <summary>
/// Dedicated background simulation thread (<c>DoF-Sim</c>) that steps <see cref="Campaign"/> off the Godot
/// main thread so VSync rendering and camera/HUD responsiveness remain jitter-free while Speed 5 runs at
/// 100% CPU speed. Falls back cleanly to synchronous main-thread stepping whenever mods or network lockstep
/// are active.
/// </summary>
public sealed class BackgroundSimRunner : IDisposable
{
    private readonly object _wakeLock = new();
    private volatile Thread _thread;
    private volatile bool _stopRequested;
    private volatile bool _workerRunning;
    private volatile Campaign _campaign;
    private volatile bool _pausedDesired = true;
    private volatile int _speedDesired = 1;
    private int _clockDesiredVersion;
    private int _clockAppliedVersion;
    private long _snapshotVersion;
    private sealed class PublishedClock
    {
        public readonly Campaign Campaign;
        private long _calendar;

        public PublishedClock(Campaign campaign)
        {
            Campaign = campaign;
            Publish(campaign.Date);
        }

        // One atomic value retains the full signed year and the three bounded calendar fields.
        public void Publish(GameDate date) => Volatile.Write(ref _calendar,
            ((long)date.Year << 32) | ((long)(ushort)date.Month << 16) | ((long)(byte)date.Day << 8) | (byte)date.Hour);

        public GameDate Read()
        {
            var value = Volatile.Read(ref _calendar);
            return new GameDate((int)(value >> 32), (int)((value >> 16) & 0xffff), (int)((value >> 8) & 0xff))
            {
                Hour = (int)(value & 0xff)
            };
        }
    }

    private PublishedClock _publishedClock;
    private CampaignHudSnapshot _hudSnapshot = CampaignHudSnapshot.Empty;
    private readonly LockFreeTripleBuffer<WorldVisualState> _visualBuffer = new(() => new WorldVisualState());
    private readonly SmoothTimePacer _pacer = new();
    private long _presentedVisualVersion;
    private long _lastPresentedTicks;
    private long _lastSnapshotPublishedTicks;
    private long _lastHudSnapshotTicks;
    private int _lastSnapshotMapRev = int.MinValue;
    private int _lastSnapshotOwnerRev = int.MinValue;
    private int _lastSnapshotWarRev = int.MinValue;
    private int _smoothMode = (int)SmoothTimeMode.Adaptive;
    private bool _pendingStepEvent;
    private bool _pendingStepOpensNewDay;
    private bool _pendingAutosave;
    private Exception _pendingException;
    private volatile Exception _workerFault;
    private bool _sawPendingPlayerEvent;
    private long _workerStepStartHours = -1;
    private long _workerAcquireTicks;
    private long _workerWorkTicks;
    private long _workerReleaseTicks;
    private long _snapshotTicks;
    private long _snapshotBytes;
    private long _snapshotCount;
    private sealed record SaveRequest(string Root, string Slot, string ModFingerprint, string DisplayName, bool Autosave)
    {
        public readonly TaskCompletionSource<SaveSystem.PreparedSave> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private volatile SaveRequest _saveRequest;
    private bool _capturingSave;

    public CountryRankings.Weights RankingWeights { get; set; } = CountryRankings.Weights.Default;
    public bool MobilePacing { get; set; }
    /// <summary>Smooth Time 2.0 real-time pacing profile; affects wall-clock cadence only, never simulation state.</summary>
    public SmoothTimeMode SmoothMode
    {
        get => (SmoothTimeMode)Volatile.Read(ref _smoothMode);
        set => Volatile.Write(ref _smoothMode, (int)value);
    }
    public SmoothTimePacer Pacer => _pacer;
    /// <summary>
    /// Adaptive step length on every platform (1h/2h/4h/8h/24h for Speed 1..5 when the defined
    /// step is one hour). Runtime-only: <see cref="Campaign.RuntimeStepHoursOverride"/> is not
    /// saved or fingerprinted, and a defined step other than one hour is left alone.
    /// </summary>
    public bool AdaptiveStepPacing { get; set; } = true;
    public double MinSpeed5DayIntervalMs { get; set; }
    internal bool MeasurePacing { get; set; }
    public bool DesiredPaused => _pausedDesired;
    public int DesiredSpeed => _speedDesired;

    internal static int AdaptiveStepHours(int speed) => MobileAdaptiveStepHours(speed);

    /// <summary>
    /// Wall seconds per campaign day under adaptive steps across speeds 1..8.
    /// Speed 8 runs uncapped (0.0s), matching MaxSpeed turbo semantics.
    /// </summary>
    internal static double AdaptiveStepSecondsPerDay(Defines defines, int speed, bool mobilePacing)
    {
        if (mobilePacing) return MobileStepSecondsPerDay(defines, speed);
        var baseSecondsPerDay = (double)(defines?.SecondsPerDay ?? 5f);
        return speed switch
        {
            <= 1 => baseSecondsPerDay * 2.40,
            2 => baseSecondsPerDay * 1.20,
            3 => baseSecondsPerDay * 0.60,
            4 => baseSecondsPerDay * 0.28,
            5 => baseSecondsPerDay * 0.13,
            6 => baseSecondsPerDay * 0.055,
            7 => baseSecondsPerDay * 0.020,
            _ => 0.0
        };
    }

    internal static int MobileAdaptiveStepHours(int speed) => speed switch
    {
        <= 1 => 1,
        2 => 1,
        3 => 1,
        4 => 2,
        5 => 4,
        6 => 6,
        7 => 12,
        _ => 24
    };

    internal static double MobileStepSecondsPerDay(Defines defines, int speed)
    {
        var baseSecondsPerDay = (double)(defines?.SecondsPerDay ?? 5f);
        return speed switch
        {
            <= 1 => baseSecondsPerDay * 1.80,
            2 => baseSecondsPerDay * 0.90,
            3 => baseSecondsPerDay * 0.45,
            4 => baseSecondsPerDay * 0.20,
            5 => baseSecondsPerDay * 0.095,
            6 => baseSecondsPerDay * 0.042,
            7 => baseSecondsPerDay * 0.016,
            _ => 0.0
        };
    }

    internal Task<SaveSystem.PreparedSave> QueueSavePreparation(Campaign campaign, string root, string slot,
        string modFingerprint, string displayName, bool autosave)
    {
        lock (_wakeLock)
        {
            if (_stopRequested || !_workerRunning || !ReferenceEquals(campaign, _campaign))
                return Task.FromCanceled<SaveSystem.PreparedSave>(new CancellationToken(canceled: true));
            if (_saveRequest is not null || _capturingSave)
                return Task.FromException<SaveSystem.PreparedSave>(new InvalidOperationException("A save is already being prepared."));
            var request = new SaveRequest(root, slot, modFingerprint, displayName, autosave);
            _saveRequest = request;
            Monitor.PulseAll(_wakeLock);
            return request.Completion.Task;
        }
    }

    internal (double AcquireMs, double WorkMs, double ReleaseMs) ReadPacingTimings() => (
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _workerAcquireTicks)).TotalMilliseconds,
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _workerWorkTicks)).TotalMilliseconds,
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _workerReleaseTicks)).TotalMilliseconds);

    internal (double Milliseconds, long Bytes, long Count) ReadSnapshotTimings() => (
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _snapshotTicks)).TotalMilliseconds,
        Interlocked.Read(ref _snapshotBytes), Interlocked.Read(ref _snapshotCount));

    public Campaign Campaign => _campaign;
    public bool IsActive => _workerRunning && _thread is { IsAlive: true };

    internal GameDate ReadPresentationDate(Campaign campaign)
    {
        var published = Volatile.Read(ref _publishedClock);
        return campaign is not null && ReferenceEquals(_campaign, campaign) &&
               published is not null && ReferenceEquals(published.Campaign, campaign)
            ? published.Read()
            : campaign?.Date ?? default;
    }

    internal CampaignHudSnapshot ReadHudSnapshot(Campaign campaign)
    {
        if (campaign is null || !ReferenceEquals(_campaign, campaign)) return null;
        return Volatile.Read(ref _hudSnapshot) ?? CampaignHudSnapshot.Empty;
    }

    /// <summary>Wait-free read of the latest triple-buffered visual world state.</summary>
    public bool TryAcquireLatestVisualSnapshot(out WorldVisualState snapshot) =>
        _visualBuffer.TryAcquireLatest(out snapshot);

    /// <summary>The consumer's currently pinned visual snapshot slot (lock-free).</summary>
    public WorldVisualState CurrentVisualSnapshot => _visualBuffer.CurrentReadBuffer;

    /// <summary>
    /// Called by the main/render thread once it has applied and presented <paramref name="snapshotVersion"/>.
    /// Keeps soft backpressure bounded when GPU/UI frames lag so the map never drifts far behind the clock.
    /// Headless callers and tests that never call this incur zero backpressure.
    /// </summary>
    public void NotifyVisualFramePresented(long snapshotVersion)
    {
        if (snapshotVersion > Volatile.Read(ref _presentedVisualVersion))
            Volatile.Write(ref _presentedVisualVersion, snapshotVersion);
        Volatile.Write(ref _lastPresentedTicks, Stopwatch.GetTimestamp());
        if (Volatile.Read(ref _snapshotVersion) - snapshotVersion <= 2)
            lock (_wakeLock) Monitor.PulseAll(_wakeLock);
    }

    public void Start(Campaign campaign)
    {
        if (campaign is null) throw new ArgumentNullException(nameof(campaign));
        if (ReferenceEquals(_campaign, campaign) && IsActive) return;
        Stop();

        _campaign = campaign;
        _pausedDesired = campaign.IsPaused;
        _speedDesired = campaign.Speed;
        _clockAppliedVersion = Volatile.Read(ref _clockDesiredVersion);
        _stopRequested = _pendingStepEvent = _pendingStepOpensNewDay = _pendingAutosave = false;
        _pendingException = _workerFault = null;
        _workerStepStartHours = -1;
        _workerAcquireTicks = _workerWorkTicks = _workerReleaseTicks = 0;
        _snapshotTicks = _snapshotBytes = _snapshotCount = _lastHudSnapshotTicks = 0;
        _pacer.Reset();
        Volatile.Write(ref _presentedVisualVersion, 0);
        Volatile.Write(ref _lastPresentedTicks, 0);
        _sawPendingPlayerEvent = campaign.HasPendingPlayerEvent();
        campaign.DeferMainThreadNotifications = true;

        campaign.AccessGate.EnterWrite();
        try
        {
            campaign.FinishStep();
            campaign.RuntimeStepHoursOverride = 0;
            if (campaign.ProvinceMap is not null) { CampaignIntel.InvalidateActors(campaign); if (campaign.Player is { IsHuman: true } p) CampaignIntel.Vision(campaign, p.Id); }
            Volatile.Write(ref _publishedClock, new PublishedClock(campaign));
            PublishSnapshotLocked(campaign, force: true);
        }
        finally { campaign.AccessGate.ExitWrite(); }

        _workerRunning = true;
        _thread = new Thread(WorkerLoop) { IsBackground = true, Name = "DoF-Sim" };
        _thread.Start();
    }

    public void Stop()
    {
        var campaign = _campaign;
        bool finishCapture;
        lock (_wakeLock)
        {
            _stopRequested = true;
            finishCapture = _capturingSave;
            _saveRequest?.Completion.TrySetCanceled();
            _saveRequest = null;
            Monitor.PulseAll(_wakeLock);
        }
        var thread = _thread;
        if (thread is not null && thread.IsAlive && !ReferenceEquals(thread, Thread.CurrentThread))
        {
            if (finishCapture) thread.Join();
            else thread.Join(millisecondsTimeout: 2000);
        }
        _thread = null;
        _workerRunning = false;
        _pacer.SyncGcLatencyMode(wantLowLatency: false, MobilePacing);
        if (campaign is not null)
        {
            campaign.DeferMainThreadNotifications = false;
            campaign.RuntimeStepHoursOverride = 0;
            _campaign = null;
        }
        Volatile.Write(ref _publishedClock, null);
        Volatile.Write(ref _hudSnapshot, CampaignHudSnapshot.Empty);
        Volatile.Write(ref _presentedVisualVersion, 0);
        Volatile.Write(ref _lastPresentedTicks, 0);
    }

    public void SetDesiredClockState(bool paused, int speed)
    {
        var maxSpeed = Math.Max(1, _campaign?.Defines?.MaxSpeed ?? 8);
        var clamped = Math.Clamp(speed, 1, maxSpeed);
        _pausedDesired = paused;
        _speedDesired = clamped;
        Interlocked.Increment(ref _clockDesiredVersion);
        lock (_wakeLock) Monitor.PulseAll(_wakeLock);
    }

    public void SyncClockState(bool paused, int speed)
    {
        var maxSpeed = Math.Max(1, _campaign?.Defines?.MaxSpeed ?? 8);
        var clamped = Math.Clamp(speed, 1, maxSpeed);
        var changed = _pausedDesired != paused || _speedDesired != clamped;
        _pausedDesired = paused;
        _speedDesired = clamped;
        if (changed)
        {
            Interlocked.Increment(ref _clockDesiredVersion);
            lock (_wakeLock) Monitor.PulseAll(_wakeLock);
        }
    }

    public void RunWrite(Action<Campaign> action, bool refreshSnapshot = true)
    {
        var campaign = _campaign;
        if (campaign is null || action is null) return;
        var prevDefer = campaign.DeferMainThreadNotifications;
        campaign.AccessGate.EnterWrite();
        try
        {
            campaign.DeferMainThreadNotifications = false;
            if (campaign.MidStep) campaign.FinishStep();
            action(campaign);
            if (refreshSnapshot) PublishSnapshotLocked(campaign, force: true);
            else if (!campaign.MidStep) PublishCompletedDate(campaign);
        }
        finally
        {
            campaign.DeferMainThreadNotifications = prevDefer && _workerRunning;
            campaign.AccessGate.ExitWrite();
            if (!campaign.AccessGate.IsWriterHeldByCurrentThread)
            {
                _pausedDesired = campaign.IsPaused;
                _speedDesired = campaign.Speed;
                _clockAppliedVersion = Volatile.Read(ref _clockDesiredVersion);
                if (!campaign.IsPaused) lock (_wakeLock) Monitor.PulseAll(_wakeLock);
            }
        }
    }

    public TResult RunWrite<TResult>(Func<Campaign, TResult> action, bool refreshSnapshot = true)
    {
        var campaign = _campaign;
        if (campaign is null || action is null) return default;
        var prevDefer = campaign.DeferMainThreadNotifications;
        campaign.AccessGate.EnterWrite();
        try
        {
            campaign.DeferMainThreadNotifications = false;
            if (campaign.MidStep) campaign.FinishStep();
            var result = action(campaign);
            if (refreshSnapshot) PublishSnapshotLocked(campaign, force: true);
            else if (!campaign.MidStep) PublishCompletedDate(campaign);
            return result;
        }
        finally
        {
            campaign.DeferMainThreadNotifications = prevDefer && _workerRunning;
            campaign.AccessGate.ExitWrite();
            if (!campaign.AccessGate.IsWriterHeldByCurrentThread)
            {
                _pausedDesired = campaign.IsPaused;
                _speedDesired = campaign.Speed;
                _clockAppliedVersion = Volatile.Read(ref _clockDesiredVersion);
                if (!campaign.IsPaused) lock (_wakeLock) Monitor.PulseAll(_wakeLock);
            }
        }
    }

    public void RefreshSnapshotNow()
    {
        var campaign = _campaign;
        if (campaign is null) return;
        campaign.AccessGate.EnterWrite();
        try
        {
            if (campaign.MidStep) campaign.FinishStep();
            _pausedDesired = campaign.IsPaused; _speedDesired = campaign.Speed; _clockAppliedVersion = Volatile.Read(ref _clockDesiredVersion);
            if (campaign.ProvinceMap is not null) { CampaignIntel.InvalidateActors(campaign); if (campaign.Player is { IsHuman: true } p) CampaignIntel.Vision(campaign, p.Id); }
            PublishSnapshotLocked(campaign, force: true);
        }
        finally
        {
            campaign.AccessGate.ExitWrite();
        }
    }

    public bool DrainMainThreadUpdates(
        out bool stepCompleted, out bool stepOpensNewDay, out bool pendingAutosave, out Exception fault)
    {
        lock (_wakeLock)
        {
            stepCompleted = _pendingStepEvent;
            stepOpensNewDay = _pendingStepOpensNewDay;
            pendingAutosave = _pendingAutosave;
            fault = _pendingException;
            _pendingStepEvent = false;
            _pendingStepOpensNewDay = false;
            _pendingAutosave = false;
            _pendingException = null;
        }
        return stepCompleted || pendingAutosave || fault is not null;
    }

    private void PublishCompletedDate(Campaign campaign)
    {
        // The calendar advances inside the staged step. Publish it only at the
        // completed boundary, even when the larger render snapshot is throttled.
        var clock = Volatile.Read(ref _publishedClock);
        if (clock is not null && ReferenceEquals(clock.Campaign, campaign)) clock.Publish(campaign.Date);
    }

    private void PublishSnapshotLocked(
        Campaign campaign, bool force, double minIntervalMs = 16.0,
        double mapCoalesceMs = 0.0, double hudMinIntervalMs = 0.0)
    {
        var now = Stopwatch.GetTimestamp();
        var mapRev = campaign.MapRevision;
        var ownerRev = campaign.OwnershipRevision;
        var warRev = campaign.WarRevision;
        var prevRender = campaign.RenderSnapshot;
        var elapsedMs = Stopwatch.GetElapsedTime(_lastSnapshotPublishedTicks).TotalMilliseconds;
        var revsChanged = mapRev != _lastSnapshotMapRev || ownerRev != _lastSnapshotOwnerRev || warRev != _lastSnapshotWarRev;
        if (!force && !campaign.IsPaused && prevRender is { Version: > 0 } && prevRender.NationRanksById.Count > 0 &&
            ((!revsChanged && elapsedMs < minIntervalMs) || (revsChanged && mapCoalesceMs > 0.0 && elapsedMs < mapCoalesceMs)))
        {
            if (prevRender.ElapsedDays != campaign.ElapsedDays || prevRender.ElapsedHours != campaign.ElapsedHours || elapsedMs >= 24.0)
            {
                var clockSnap = prevRender.WithClock(Interlocked.Increment(ref _snapshotVersion), campaign);
                if (mapCoalesceMs <= 0.0 || elapsedMs >= 16.0) { _visualBuffer.WriteBuffer.CaptureFrom(campaign, clockSnap); _visualBuffer.PublishWrite(); }
                campaign.PublishRenderSnapshot(clockSnap);
                PublishCompletedDate(campaign);
            }
            return;
        }
        var nextVer = Interlocked.Increment(ref _snapshotVersion);
        var measurePacing = MeasurePacing;
        var captureStarted = measurePacing ? Stopwatch.GetTimestamp() : 0;
        var allocated = measurePacing ? GC.GetAllocatedBytesForCurrentThread() : 0;
        var prevHud = Volatile.Read(ref _hudSnapshot);
        var snap = CampaignRenderSnapshot.Capture(campaign, prevRender, RankingWeights, nextVer);
        var hudElapsedMs = _lastHudSnapshotTicks == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(_lastHudSnapshotTicks).TotalMilliseconds;
        var reuseHud = !force && hudMinIntervalMs > 0.0 && hudElapsedMs < hudMinIntervalMs && !revsChanged && prevHud is { Version: > 0 } &&
            prevHud.PlayerId == (campaign.Player?.Id ?? -1) && prevHud.Speed == campaign.Speed && prevHud.IsPaused == campaign.IsPaused &&
            campaign.PendingEvents.Count == 0 && prevHud.PendingEventCount == 0;
        var hudSnap = reuseHud ? prevHud : CampaignHudSnapshot.Capture(campaign, nextVer, prevHud);
        if (!reuseHud) _lastHudSnapshotTicks = now;
        _visualBuffer.WriteBuffer.CaptureFrom(campaign, snap);
        _visualBuffer.PublishWrite();
        if (measurePacing)
        {
            Interlocked.Add(ref _snapshotTicks, Stopwatch.GetTimestamp() - captureStarted);
            Interlocked.Add(ref _snapshotBytes, GC.GetAllocatedBytesForCurrentThread() - allocated);
            Interlocked.Increment(ref _snapshotCount);
        }
        if (!reuseHud) Volatile.Write(ref _hudSnapshot, hudSnap);
        campaign.PublishRenderSnapshot(snap);
        PublishCompletedDate(campaign);
        _lastSnapshotPublishedTicks = now;
        _lastSnapshotMapRev = mapRev;
        _lastSnapshotOwnerRev = ownerRev;
        _lastSnapshotWarRev = warRev;
    }

    private void PrepareQueuedSave(Campaign campaign)
    {
        if (!campaign.AccessGate.TryEnterBackgroundWrite())
        {
            Thread.Sleep(1);
            return;
        }

        SaveRequest request = null;
        SaveSystem.PreparedSave prepared = null;
        Exception failure = null;
        var finishingStep = false;
        try
        {
            lock (_wakeLock)
            {
                if (_stopRequested || _saveRequest is null || !ReferenceEquals(_thread, Thread.CurrentThread)) return;
                request = _saveRequest;
                _saveRequest = null;
                _capturingSave = true;
                if (_workerFault is { } fault)
                    throw new InvalidOperationException("The simulation stopped before the save could be prepared.", fault);
            }

            if (campaign.MidStep)
            {
                finishingStep = true;
                campaign.FinishStep();
                PublishSnapshotLocked(campaign, force: true);
                lock (_wakeLock)
                {
                    if (_workerStepStartHours >= 0 && _workerStepStartHours != campaign.ElapsedHours)
                    {
                        _pendingStepEvent = true;
                        _pendingStepOpensNewDay |= campaign.StepOpensNewDay;
                        _pendingAutosave = true;
                    }
                    _sawPendingPlayerEvent = campaign.HasPendingPlayerEvent();
                }
                finishingStep = false;
            }

            var displayName = string.IsNullOrWhiteSpace(request.DisplayName) && campaign.Player is { } player
                ? $"{player.DisplayName} · {campaign.Date.Display()}"
                : request.DisplayName;
            prepared = SaveSystem.Prepare(request.Root, request.Slot, campaign, request.ModFingerprint, displayName, request.Autosave);
        }
        catch (Exception ex)
        {
            failure = ex;
            if (ex is OutOfMemoryException or AccessViolationException) throw;
            if (finishingStep)
            {
                _workerFault = ex;
                lock (_wakeLock) _pendingException ??= ex;
            }
        }
        finally
        {
            campaign.AccessGate.ExitWrite();
            if (request is not null)
            {
                lock (_wakeLock) _capturingSave = false;
                if (failure is null) request.Completion.TrySetResult(prepared);
                else request.Completion.TrySetException(failure);
            }
        }
    }

    private void WaitForVisualBackpressure(int speed, WorkerSlicePolicy policy, ref long lastTickTime)
    {
        var presentedTicks = Volatile.Read(ref _lastPresentedTicks);
        if (presentedTicks == 0 || Stopwatch.GetElapsedTime(presentedTicks).TotalMilliseconds >= 250.0) return;
        if (Volatile.Read(ref _snapshotVersion) - Volatile.Read(ref _presentedVisualVersion) < policy.MaxVisualLeadSnapshots) return;
        var bpStart = Stopwatch.GetTimestamp();
        while (!_stopRequested && !_pausedDesired && _speedDesired == speed &&
               Volatile.Read(ref _snapshotVersion) - Volatile.Read(ref _presentedVisualVersion) >= policy.MaxVisualLeadSnapshots &&
               Stopwatch.GetElapsedTime(bpStart).TotalMilliseconds < policy.MaxBackpressureWaitMs)
        {
            lock (_wakeLock)
            {
                if (!_stopRequested && !_pausedDesired &&
                    Volatile.Read(ref _snapshotVersion) - Volatile.Read(ref _presentedVisualVersion) >= policy.MaxVisualLeadSnapshots)
                    Monitor.Wait(_wakeLock, 3);
            }
        }
        lastTickTime = Stopwatch.GetTimestamp();
    }

    private void PaceCompletedMaxSpeedDay(Campaign campaign, SmoothTimeMode smoothMode, int speed, bool mobilePacing, ref long lastDayTicks, ref long lastTickTime)
    {
        var nowDayTicks = Stopwatch.GetTimestamp();
        if (lastDayTicks != 0)
        {
            var elapsedDayMs = Stopwatch.GetElapsedTime(lastDayTicks, nowDayTicks).TotalMilliseconds;
            var waitMs = _pacer.ComputePostDayWaitMs(smoothMode, speed, mobilePacing, MinSpeed5DayIntervalMs, elapsedDayMs);
            if (waitMs > 0.5)
            {
                var waitTarget = lastDayTicks + (long)((elapsedDayMs + waitMs) * (Stopwatch.Frequency / 1000.0));
                while (!_stopRequested && !_pausedDesired && _speedDesired >= 8 && _saveRequest is null && !campaign.AccessGate.HasWriteWaiter)
                {
                    var remMs = (int)Math.Ceiling(Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), waitTarget).TotalMilliseconds);
                    if (remMs <= 0) break;
                    lock (_wakeLock)
                    {
                        if (!_stopRequested && !_pausedDesired && _speedDesired >= 8 && _saveRequest is null)
                            Monitor.Wait(_wakeLock, Math.Min(remMs, 10));
                    }
                }
            }
        }
        lastDayTicks = Stopwatch.GetTimestamp();
        lastTickTime = lastDayTicks;
    }

    private void WorkerLoop()
    {
        var lastTickTime = Stopwatch.GetTimestamp();
        var accumulator = 0.0;
        var dayWorkMs = 0.0;
        var lastSpeed = -1;
        var lastSmoothMode = SmoothMode;
        var priorityLowered = false;
        long lastSpeed5DayTicks = 0;
        try
        {
            while (!_stopRequested && ReferenceEquals(_thread, Thread.CurrentThread))
            {
                var campaign = _campaign;
                if (campaign is null) break;

                var mobilePacing = MobilePacing;
                var smoothMode = SmoothMode;
                var adaptiveSteps = AdaptiveStepPacing || mobilePacing;
                var desiredVer = Volatile.Read(ref _clockDesiredVersion);
                if (desiredVer != _clockAppliedVersion)
                {
                    if (campaign.AccessGate.HasWriteWaiter) { Thread.Yield(); continue; }
                    campaign.AccessGate.EnterWrite(asBackgroundWorker: true);
                    try
                    {
                        if (!ReferenceEquals(_thread, Thread.CurrentThread)) break;
                        desiredVer = Volatile.Read(ref _clockDesiredVersion);
                        _clockAppliedVersion = desiredVer;
                        var targetPaused = _pausedDesired;
                        var maxSpd = Math.Max(1, campaign.Defines?.MaxSpeed ?? 8);
                        var targetSpeed = Math.Clamp(_speedDesired, 1, maxSpd);
                        var clockChanged = campaign.IsPaused != targetPaused || campaign.Speed != targetSpeed;
                        campaign.IsPaused = targetPaused;
                        campaign.Speed = targetSpeed;
                        if (!campaign.MidStep)
                            campaign.RuntimeStepHoursOverride = adaptiveSteps && !targetPaused ? AdaptiveStepHours(targetSpeed) : 0;
                        if (clockChanged) PublishSnapshotLocked(campaign, force: true);
                    }
                    finally
                    {
                        campaign.AccessGate.ExitWrite(yieldToRequestedReader: true,
                            readerWindowMs: mobilePacing ? 10 : 18,
                            readerWorkAheadMs: mobilePacing ? 20 : 0);
                    }
                    _sawPendingPlayerEvent = campaign.HasPendingPlayerEvent();
                    lastTickTime = Stopwatch.GetTimestamp();
                    continue;
                }

                if (_saveRequest is not null && (!campaign.MidStep || _pausedDesired || campaign.IsPaused ||
                    _workerFault is not null || campaign.SimulationFault.Length > 0))
                {
                    _pacer.SyncGcLatencyMode(wantLowLatency: false, mobilePacing);
                    PrepareQueuedSave(campaign);
                    lastTickTime = Stopwatch.GetTimestamp();
                    continue;
                }

                if (_pausedDesired || _workerFault is not null || campaign.IsPaused || campaign.SimulationFault.Length > 0)
                {
                    if (!adaptiveSteps && !campaign.MidStep) campaign.RuntimeStepHoursOverride = 0;
                    accumulator = dayWorkMs = 0.0; lastSpeed5DayTicks = 0; _pacer.Reset();
                    _pacer.SyncGcLatencyMode(wantLowLatency: false, mobilePacing);
                    lock (_wakeLock)
                    {
                        if (!_stopRequested && _saveRequest is null &&
                            Volatile.Read(ref _clockDesiredVersion) == _clockAppliedVersion &&
                            (_pausedDesired || _workerFault is not null || campaign.IsPaused || campaign.SimulationFault.Length > 0))
                            Monitor.Wait(_wakeLock, 25);
                    }
                    lastTickTime = Stopwatch.GetTimestamp();
                    continue;
                }

                if (_sawPendingPlayerEvent)
                {
                    accumulator = dayWorkMs = 0.0; lastSpeed5DayTicks = 0; _pacer.Reset();
                    _pacer.SyncGcLatencyMode(wantLowLatency: false, mobilePacing);
                    lock (_wakeLock)
                    {
                        if (!_stopRequested && !_pausedDesired &&
                            Volatile.Read(ref _clockDesiredVersion) == _clockAppliedVersion)
                            Monitor.Wait(_wakeLock, 20);
                    }
                    _sawPendingPlayerEvent = campaign.HasPendingPlayerEvent();
                    lastTickTime = Stopwatch.GetTimestamp();
                    continue;
                }

                var speed = Math.Max(1, _speedDesired);
                if (speed != lastSpeed || smoothMode != lastSmoothMode)
                {
                    lastSpeed = speed;
                    lastSmoothMode = smoothMode;
                    accumulator = 0.0;
                    dayWorkMs = 0.0;
                    lastSpeed5DayTicks = 0;
                    _pacer.Reset();
                }

                var policy = SmoothTimePacer.WorkerPolicy(smoothMode, speed, mobilePacing);
                if (policy.LowerWorkerPriority != priorityLowered)
                {
                    priorityLowered = policy.LowerWorkerPriority;
                    try { Thread.CurrentThread.Priority = priorityLowered ? ThreadPriority.BelowNormal : ThreadPriority.Normal; }
                    catch { /* Sandboxed OS/runtime priority restriction. */ }
                }
                _pacer.SyncGcLatencyMode(
                    wantLowLatency: speed >= 6 && smoothMode is SmoothTimeMode.Adaptive or SmoothTimeMode.Cinema60Fps && _saveRequest is null,
                    mobilePacing);

                var continuingStep = campaign.MidStep;
                if (!continuingStep)
                    campaign.RuntimeStepHoursOverride = adaptiveSteps ? AdaptiveStepHours(speed) : 0;
                var now = Stopwatch.GetTimestamp();
                var dt = Stopwatch.GetElapsedTime(lastTickTime).TotalSeconds;
                lastTickTime = now;
                if (dt > 0.08) dt = 0.08;

                // Soft render-thread backpressure when a live visual consumer reported recently;
                // headless tests never call NotifyVisualFramePresented and incur zero wait.
                if (!continuingStep && policy.MaxVisualLeadSnapshots > 0)
                    WaitForVisualBackpressure(speed, policy, ref lastTickTime);

                var stepInterval = 0.0;
                if (speed >= 8)
                {
                    accumulator = 0.0;
                }
                else
                {
                    var effectiveTickHours = adaptiveSteps && campaign.Defines.HoursPerTick == 1
                        ? AdaptiveStepHours(speed)
                        : campaign.TickHours;
                    var secPerDay = adaptiveSteps
                        ? AdaptiveStepSecondsPerDay(campaign.Defines, speed, mobilePacing)
                        : campaign.Defines.SecondsForSpeed(speed);
                    stepInterval = Math.Max(0.002, secPerDay * (effectiveTickHours / 24.0));
                    accumulator = SmoothTimePacer.AccumulateWorkerStepDebt(accumulator, dt, stepInterval, smoothMode, speed);
                    if (!continuingStep)
                    {
                        if (accumulator < stepInterval)
                        {
                            var waitMs = Math.Clamp((int)((stepInterval - accumulator) * 1000.0), 1, 12);
                            lock (_wakeLock)
                            {
                                if (!_stopRequested && !_pausedDesired && _speedDesired == speed &&
                                    Volatile.Read(ref _clockDesiredVersion) == _clockAppliedVersion)
                                    Monitor.Wait(_wakeLock, waitMs);
                            }
                            continue;
                        }
                        accumulator -= stepInterval;
                    }
                }

                if (campaign.AccessGate.HasWriteWaiter) { Thread.Yield(); continue; }
                var stepDone = false;
                var openedDay = false;
                var awaitingPlayer = false;
                Exception caught = null;
                var measurePacing = MeasurePacing;
                var stepWorkStarted = Stopwatch.GetTimestamp();
                var workElapsedMs = 0.0;

                campaign.AccessGate.EnterWrite(asBackgroundWorker: true);
                var acquiredAt = Stopwatch.GetTimestamp();
                if (measurePacing) Interlocked.Add(ref _workerAcquireTicks, acquiredAt - stepWorkStarted);
                try
                {
                    if (!ReferenceEquals(_thread, Thread.CurrentThread)) break;
                    var stepStartVer = Volatile.Read(ref _clockDesiredVersion);
                    if (stepStartVer != _clockAppliedVersion)
                    {
                        _clockAppliedVersion = stepStartVer;
                        var maxSpd = Math.Max(1, campaign.Defines?.MaxSpeed ?? 8);
                        campaign.IsPaused = _pausedDesired;
                        campaign.Speed = Math.Clamp(_speedDesired, 1, maxSpd);
                    }
                    if (_stopRequested || _pausedDesired || campaign.IsPaused || campaign.SimulationFault.Length > 0)
                    {
                        if (campaign.MidStep) campaign.FinishStep();
                        if (!adaptiveSteps) campaign.RuntimeStepHoursOverride = 0;
                        PublishSnapshotLocked(campaign, force: true);
                    }
                    else
                    {
                        var wasPausedBeforeStep = campaign.IsPaused;
                        campaign.Speed = speed;
                        if (!campaign.MidStep)
                            campaign.RuntimeStepHoursOverride = adaptiveSteps ? AdaptiveStepHours(speed) : 0;
                        var sliceMs = policy.WriteSliceMs;
                        var minSnapMs = policy.SnapshotMinIntervalMs;
                        var batchStarted = Stopwatch.GetTimestamp();
                        var batchSteps = 0;
                        var anyStepDone = false;
                        var anyOpenedDay = false;
                        var anyAwaitingPlayer = false;
                        do
                        {
                            if (!campaign.MidStep)
                            {
                                campaign.RuntimeStepHoursOverride = adaptiveSteps ? AdaptiveStepHours(speed) : 0;
                                _workerStepStartHours = campaign.ElapsedHours;
                            }
                            var remainingMs = Math.Max(0.0, sliceMs - Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds);
                            var finished = campaign.StepFor(remainingMs);
                            if (!finished || campaign.MidStep) break;

                            if (_workerStepStartHours >= 0 && campaign.ElapsedHours != _workerStepStartHours)
                            {
                                anyStepDone = true;
                                batchSteps++;
                                if (campaign.StepOpensNewDay) anyOpenedDay = true;
                                if (campaign.HasPendingPlayerEvent()) anyAwaitingPlayer = true;
                            }

                            if (speed < 7 || anyAwaitingPlayer || batchSteps >= policy.MaxBatchSteps) break;
                            if (_stopRequested || _pausedDesired || campaign.IsPaused || campaign.SimulationFault.Length > 0) break;
                            if (Volatile.Read(ref _clockDesiredVersion) != stepStartVer) break;
                            if (campaign.AccessGate.HasPriorityReaderRequested || campaign.AccessGate.HasWriteWaiter || _saveRequest is not null) break;
                            var batchElapsedMs = Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds;
                            if (batchElapsedMs >= sliceMs) break;
                            if (anyStepDone && Stopwatch.GetElapsedTime(_lastSnapshotPublishedTicks).TotalMilliseconds >= minSnapMs) break;
                            if (campaign.AccessGate.HasReaderRequested &&
                                (batchElapsedMs >= policy.ReaderYieldAfterMs || campaign.AccessGate.IsReaderStarving))
                                break;
                            if (speed == 7)
                            {
                                if (stepInterval <= 0.0 || accumulator < stepInterval) break;
                                accumulator -= stepInterval;
                            }
                        }
                        while (true);

                        if (!wasPausedBeforeStep && campaign.IsPaused && Volatile.Read(ref _clockDesiredVersion) == stepStartVer)
                        {
                            _pausedDesired = true;
                            _clockAppliedVersion = stepStartVer;
                        }

                        stepDone = anyStepDone;
                        openedDay = anyOpenedDay;
                        awaitingPlayer = anyAwaitingPlayer;
                        if (!campaign.MidStep)
                            PublishSnapshotLocked(campaign,
                                force: campaign.IsPaused || anyAwaitingPlayer || (speed < 8 && anyOpenedDay && policy.MapChangeCoalesceMs <= 0.0),
                                minIntervalMs: minSnapMs,
                                mapCoalesceMs: policy.MapChangeCoalesceMs,
                                hudMinIntervalMs: policy.HudSnapshotMinIntervalMs);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not AccessViolationException)
                {
                    caught = ex;
                    if (ReferenceEquals(_thread, Thread.CurrentThread)) _workerFault = ex;
                }
                finally
                {
                    var releasingAt = Stopwatch.GetTimestamp();
                    workElapsedMs = Stopwatch.GetElapsedTime(acquiredAt, releasingAt).TotalMilliseconds;
                    if (measurePacing) Interlocked.Add(ref _workerWorkTicks, releasingAt - acquiredAt);
                    campaign.AccessGate.ExitWrite(yieldToRequestedReader: true,
                        readerWindowMs: policy.ReaderWindowMs,
                        readerWorkAheadMs: policy.ReaderWorkAheadMs);
                    if (measurePacing) Interlocked.Add(ref _workerReleaseTicks, Stopwatch.GetTimestamp() - releasingAt);
                }

                dayWorkMs += workElapsedMs;
                if (stepDone)
                {
                    _pacer.ObserveStepWork(dayWorkMs, openedDay);
                    dayWorkMs = 0.0;
                }

                if (stepDone && speed < 8)
                    accumulator = SmoothTimePacer.DampWorkerAccumulator(
                        accumulator, stepInterval, Stopwatch.GetElapsedTime(stepWorkStarted).TotalSeconds, continuingStep, smoothMode, speed);

                if (caught is not null || stepDone || awaitingPlayer)
                {
                    lock (_wakeLock)
                    {
                        if (!ReferenceEquals(_thread, Thread.CurrentThread)) break;
                        if (caught is not null) _pendingException ??= caught;
                        if (stepDone)
                        {
                            _pendingStepEvent = true;
                            _pendingStepOpensNewDay |= openedDay;
                            _pendingAutosave = true;
                        }
                        _sawPendingPlayerEvent = awaitingPlayer;
                    }
                }

                if (speed >= 8 && stepDone && openedDay && caught is null && !awaitingPlayer && !_stopRequested && !_pausedDesired)
                    PaceCompletedMaxSpeedDay(campaign, smoothMode, speed, mobilePacing, ref lastSpeed5DayTicks, ref lastTickTime);
            }
        }
        finally
        {
            _pacer.SyncGcLatencyMode(wantLowLatency: false, MobilePacing);
            lock (_wakeLock)
            {
                if (ReferenceEquals(_thread, Thread.CurrentThread))
                {
                    _workerRunning = false;
                    _saveRequest?.Completion.TrySetCanceled();
                    _saveRequest = null;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
