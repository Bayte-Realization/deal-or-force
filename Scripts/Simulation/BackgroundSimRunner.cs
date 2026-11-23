using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

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

    [ThreadStatic] private static CampaignAccessGate t_primaryGate;
    [ThreadStatic] private static int t_primaryDepth;
    [ThreadStatic] private static Dictionary<CampaignAccessGate, int> t_overflowDepths;

    public bool IsWriterActive => Volatile.Read(ref _writerActive);
    public bool IsWriterHeldByCurrentThread => Volatile.Read(ref _writerThreadId) == Environment.CurrentManagedThreadId;
    public bool IsHeldByCurrentThread => IsWriterHeldByCurrentThread || GetDepth(this) > 0;
    public bool HasWriteWaiter => Volatile.Read(ref _writeWaiters) > 0;
    public bool HasReaderRequest => Volatile.Read(ref _readerRequested);

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
                IncrementDepth(this);
                return true;
            }
            if (timeoutMs <= 0)
            {
                if (requestWindowOnMiss) _readerRequested = true;
                return false;
            }
            if (requestWindowOnMiss) _readerRequested = true;
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
            IncrementDepth(this);
            return true;
        }
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
                while (_writerActive || _readers > 0 || (asBackgroundWorker && _writeWaiters > 0))
                    Monitor.Wait(_sync);
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

    public void ExitWrite(bool yieldToRequestedReader = false, int readerWindowMs = 24)
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

            if (!hadUpgradedRead && yieldToRequestedReader && _readerRequested && _writeWaiters == 0 && readerWindowMs > 0)
            {
                // Give the 60 Hz UI/render thread one frame boundary to run TryEnterRead(0) if it missed.
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
                    var rem = (int)Math.Ceiling(readerWindowMs - elapsedMs);
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
        nationRanksById: new Dictionary<int, int>(0),
        playerAvailableDecisionsCount: 0,
        decisionsWeek: -1,
        decisionsPpFloor: int.MinValue);

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
    public readonly IReadOnlyDictionary<int, int> NationRanksById;
    public readonly int PlayerAvailableDecisionsCount;

    internal readonly int RankWeek;
    internal readonly int RankMapRev;
    internal readonly int RankWarRev;
    internal readonly int RankPlayerId;
    internal readonly int DecisionsWeek;
    internal readonly int DecisionsPpFloor;

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
        IReadOnlyDictionary<int, int> nationRanksById,
        int playerAvailableDecisionsCount, int decisionsWeek, int decisionsPpFloor)
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
        NationRanksById = nationRanksById;
        PlayerAvailableDecisionsCount = playerAvailableDecisionsCount;
        DecisionsWeek = decisionsWeek;
        DecisionsPpFloor = decisionsPpFloor;
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
        else if (previous.ControllerMapRevision == campaign.MapRevision &&
                 previous.ProvinceController.Length == srcController.Length)
        {
            provinceController = previous.ProvinceController;
            controllerRev = previous.ControllerMapRevision;
        }
        else
        {
            provinceController = (ushort[])srcController.Clone();
            controllerRev = campaign.MapRevision;
        }

        ushort[] provinceOwner;
        int ownerRev;
        var srcOwner = campaign.ProvinceMap?.ProvinceToNation;
        if (srcOwner is null)
        {
            provinceOwner = Array.Empty<ushort>();
            ownerRev = -1;
        }
        else if (previous.OwnerRevision == campaign.OwnershipRevision &&
                 previous.ProvinceOwner.Length == srcOwner.Length)
        {
            provinceOwner = previous.ProvinceOwner;
            ownerRev = previous.OwnerRevision;
        }
        else
        {
            provinceOwner = (ushort[])srcOwner.Clone();
            ownerRev = campaign.OwnershipRevision;
        }

        var rawNations = campaign.Nations;
        var count = rawNations.Count;
        var maxId = 0;
        var maxMap = 0;
        for (var i = 0; i < count; i++)
        {
            var n = rawNations[i];
            if (n.Id > maxId) maxId = n.Id;
            if (n.MapIndex > maxMap) maxMap = n.MapIndex;
        }

        var atWarIds = new HashSet<int>();
        var liveWars = Warfare.LiveWars(campaign);
        for (var w = 0; w < liveWars.Count; w++)
        {
            var war = liveWars[w];
            atWarIds.Add(war.AttackerId);
            atWarIds.Add(war.DefenderId);
            foreach (var ally in war.AttackerAllies) atWarIds.Add(ally);
            foreach (var ally in war.DefenderAllies) atWarIds.Add(ally);
        }

        var summaries = new NationSnapshot[count];
        var slotById = new int[maxId + 1];
        Array.Fill(slotById, -1);
        var slotByMap = new int[maxMap + 1];
        Array.Fill(slotByMap, -1);
        var playerId = campaign.Player?.Id ?? -1;
        for (var i = 0; i < count; i++)
        {
            var n = rawNations[i];
            summaries[i] = new NationSnapshot(
                n.Id,
                n.MapIndex,
                n.Tag,
                n.DisplayName,
                n.MapName,
                n.Color,
                IsAlive: n.RetiredDay < 0,
                IsPlayer: n.Id == playerId,
                AtWar: atWarIds.Contains(n.Id),
                HasMovement: n.Movement is not null,
                n.Population,
                n.Gdp,
                n.Treasury,
                n.Stability);
            if ((uint)n.Id < (uint)slotById.Length) slotById[n.Id] = i;
            if ((uint)n.MapIndex < (uint)slotByMap.Length) slotByMap[n.MapIndex] = i;
        }

        var week = campaign.ElapsedDays / 7;
        var rankPlayerId = campaign.Player?.Id ?? -1;
        var overallRank = previous.PlayerOverallRank;
        var overallScore = previous.PlayerOverallScore;
        var ranksById = previous.NationRanksById;
        var rankWeek = previous.RankWeek;
        var rankMapRev = previous.RankMapRev;
        var rankWarRev = previous.RankWarRev;
        if (previous.Version == 0 ||
            rankWeek != week ||
            rankMapRev != campaign.MapRevision ||
            rankWarRev != campaign.WarRevision ||
            previous.RankPlayerId != rankPlayerId ||
            ranksById.Count == 0)
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
            ranksById = map;
            rankWeek = week;
            rankMapRev = campaign.MapRevision;
            rankWarRev = campaign.WarRevision;
        }

        var player = campaign.Player;
        var ppFloor = player is not null ? (int)MathF.Floor(player.PoliticalPoints) : int.MinValue;
        var decisionsCount = previous.PlayerAvailableDecisionsCount;
        var decisionsWeek = previous.DecisionsWeek;
        if (player is null)
        {
            decisionsCount = 0;
            decisionsWeek = week;
        }
        else if (previous.Version == 0 ||
                 decisionsWeek != week ||
                 previous.RankMapRev != campaign.MapRevision ||
                 previous.RankWarRev != campaign.WarRevision ||
                 previous.RankPlayerId != player.Id ||
                 previous.DecisionsPpFloor != ppFloor)
        {
            var c = 0;
            var all = DecisionCatalog.All;
            for (var i = 0; i < all.Count; i++)
                if (!DecisionCatalog.IsBlocked(campaign, player, all[i])) c++;
            decisionsCount = c;
            decisionsWeek = week;
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
            ranksById,
            decisionsCount,
            decisionsWeek,
            ppFloor);
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
    private Thread _thread;
    private volatile bool _stopRequested;
    private volatile bool _workerRunning;
    private volatile Campaign _campaign;
    private volatile bool _pausedDesired = true;
    private volatile int _speedDesired = 1;
    private long _snapshotVersion;
    private long _lastSnapshotPublishedTicks;
    private int _lastSnapshotMapRev = int.MinValue;
    private int _lastSnapshotOwnerRev = int.MinValue;
    private int _lastSnapshotWarRev = int.MinValue;
    private bool _pendingStepEvent;
    private bool _pendingStepOpensNewDay;
    private bool _pendingAutosave;
    private Exception _pendingException;
    private bool _sawPendingPlayerEvent;
    private long _workerStepStartHours = -1;

    public CountryRankings.Weights RankingWeights { get; set; } = CountryRankings.Weights.Default;
    public bool MobilePacing { get; set; }
    public double MinSpeed5DayIntervalMs { get; set; }

    public Campaign Campaign => _campaign;
    public bool IsActive => _workerRunning && _thread is { IsAlive: true };

    public void Start(Campaign campaign)
    {
        if (campaign is null) throw new ArgumentNullException(nameof(campaign));
        if (ReferenceEquals(_campaign, campaign) && IsActive) return;
        Stop();

        _campaign = campaign;
        _pausedDesired = campaign.IsPaused;
        _speedDesired = campaign.Speed;
        _stopRequested = false;
        _pendingStepEvent = false;
        _pendingStepOpensNewDay = false;
        _pendingAutosave = false;
        _pendingException = null;
        _workerStepStartHours = -1;
        _sawPendingPlayerEvent = campaign.HasPendingPlayerEvent();
        campaign.DeferMainThreadNotifications = true;

        campaign.AccessGate.EnterWrite();
        try
        {
            campaign.FinishStep();
            PublishSnapshotLocked(campaign, force: true);
        }
        finally
        {
            campaign.AccessGate.ExitWrite();
        }

        _workerRunning = true;
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "DoF-Sim"
        };
        _thread.Start();
    }

    public void Stop()
    {
        var campaign = _campaign;
        _stopRequested = true;
        lock (_wakeLock) Monitor.PulseAll(_wakeLock);
        var thread = _thread;
        if (thread is not null && thread.IsAlive)
            thread.Join(millisecondsTimeout: 2000);
        _thread = null;
        _workerRunning = false;
        if (campaign is not null)
        {
            campaign.DeferMainThreadNotifications = false;
            _campaign = null;
        }
    }

    public void SyncClockState(bool paused, int speed)
    {
        var clamped = Math.Max(1, speed);
        var changed = _pausedDesired != paused || _speedDesired != clamped;
        _pausedDesired = paused;
        _speedDesired = clamped;
        if (changed)
        {
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
        }
        finally
        {
            campaign.DeferMainThreadNotifications = prevDefer && _workerRunning;
            campaign.AccessGate.ExitWrite();
            if (!campaign.AccessGate.IsWriterHeldByCurrentThread)
            {
                _pausedDesired = campaign.IsPaused;
                _speedDesired = campaign.Speed;
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

    private void PublishSnapshotLocked(Campaign campaign, bool force)
    {
        var now = Stopwatch.GetTimestamp();
        var mapRev = campaign.MapRevision;
        var ownerRev = campaign.OwnershipRevision;
        var warRev = campaign.WarRevision;
        if (!force &&
            mapRev == _lastSnapshotMapRev &&
            ownerRev == _lastSnapshotOwnerRev &&
            warRev == _lastSnapshotWarRev &&
            !campaign.IsPaused &&
            Stopwatch.GetElapsedTime(_lastSnapshotPublishedTicks).TotalMilliseconds < 16.0)
            return;

        var nextVer = Interlocked.Increment(ref _snapshotVersion);
        var snap = CampaignRenderSnapshot.Capture(campaign, campaign.RenderSnapshot, RankingWeights, nextVer);
        campaign.PublishRenderSnapshot(snap);
        _lastSnapshotPublishedTicks = now;
        _lastSnapshotMapRev = mapRev;
        _lastSnapshotOwnerRev = ownerRev;
        _lastSnapshotWarRev = warRev;
    }

    private void WorkerLoop()
    {
        var lastTickTime = Stopwatch.GetTimestamp();
        var accumulator = 0.0;
        var lastSpeed = -1;
        long lastSpeed5DayTicks = 0;
        try
        {
            while (!_stopRequested)
            {
                var campaign = _campaign;
                if (campaign is null) break;

                if (_pausedDesired || _pendingException is not null || campaign.IsPaused || campaign.SimulationFault.Length > 0)
                {
                    accumulator = 0.0;
                    lastSpeed5DayTicks = 0;
                    lastTickTime = Stopwatch.GetTimestamp();
                    lock (_wakeLock)
                    {
                        if (!_stopRequested && (_pausedDesired || _pendingException is not null || campaign.IsPaused || campaign.SimulationFault.Length > 0))
                            Monitor.Wait(_wakeLock, 25);
                    }
                    lastTickTime = Stopwatch.GetTimestamp();
                    continue;
                }

                // Pause worker progression momentarily when a brand-new player decision modal arrives
                // so Speed 5 cannot advance past unread events before the UI pauses it.
                if (_sawPendingPlayerEvent)
                {
                    accumulator = 0.0;
                    lastSpeed5DayTicks = 0;
                    lock (_wakeLock)
                    {
                        if (!_stopRequested && !_pausedDesired) Monitor.Wait(_wakeLock, 20);
                    }
                    _sawPendingPlayerEvent = campaign.HasPendingPlayerEvent();
                    lastTickTime = Stopwatch.GetTimestamp();
                    continue;
                }

                var speed = Math.Max(1, _speedDesired);
                if (speed != lastSpeed)
                {
                    lastSpeed = speed;
                    accumulator = 0.0;
                    lastSpeed5DayTicks = 0;
                }

                var continuingStep = campaign.MidStep;
                var now = Stopwatch.GetTimestamp();
                var dt = Stopwatch.GetElapsedTime(lastTickTime).TotalSeconds;
                lastTickTime = now;
                if (dt > 0.25) dt = 0.25;

                var stepInterval = 0.0;
                if (continuingStep || speed >= 5)
                {
                    if (speed >= 5) accumulator = 0.0;
                }
                else
                {
                    stepInterval = Math.Max(0.002, campaign.Defines.SecondsForSpeed(speed) * (campaign.TickHours / 24.0));
                    accumulator = Math.Min(accumulator + dt, stepInterval * 1.25);
                    if (accumulator < stepInterval)
                    {
                        var waitMs = Math.Clamp((int)((stepInterval - accumulator) * 1000.0), 1, 16);
                        lock (_wakeLock)
                        {
                            if (!_stopRequested && !_pausedDesired && _speedDesired == speed)
                                Monitor.Wait(_wakeLock, waitMs);
                        }
                        continue;
                    }
                    accumulator -= stepInterval;
                }

                if (campaign.AccessGate.HasWriteWaiter)
                {
                    Thread.Yield();
                    continue;
                }

                var stepDone = false;
                var openedDay = false;
                var awaitingPlayer = false;
                Exception caught = null;
                var mobilePacing = MobilePacing;
                var stepWorkStarted = Stopwatch.GetTimestamp();

                campaign.AccessGate.EnterWrite(asBackgroundWorker: true);
                try
                {
                    if (_stopRequested || _pausedDesired || campaign.IsPaused || campaign.SimulationFault.Length > 0)
                    {
                        if (campaign.MidStep) campaign.FinishStep();
                        PublishSnapshotLocked(campaign, force: true);
                    }
                    else
                    {
                        campaign.Speed = speed;
                        if (!campaign.MidStep) _workerStepStartHours = campaign.ElapsedHours;
                        // Run in bounded slices so ExitWrite yields promptly whenever the
                        // render/UI thread requests a read window or command lock during heavy ticks.
                        var sliceMs = speed >= 5 ? (mobilePacing ? 4.5 : 8.0) : (mobilePacing ? 3.0 : 5.0);
                        var finished = campaign.StepFor(sliceMs);
                        if (finished && !campaign.MidStep)
                        {
                            if (_workerStepStartHours >= 0 && campaign.ElapsedHours != _workerStepStartHours)
                            {
                                stepDone = true;
                                openedDay = campaign.StepOpensNewDay;
                                awaitingPlayer = campaign.HasPendingPlayerEvent();
                            }
                            PublishSnapshotLocked(campaign, force: campaign.IsPaused || awaitingPlayer || openedDay);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not AccessViolationException)
                {
                    caught = ex;
                }
                finally
                {
                    campaign.AccessGate.ExitWrite(yieldToRequestedReader: true, readerWindowMs: mobilePacing ? 40 : 24);
                }

                if (stepDone && speed < 5 && (continuingStep || (stepInterval > 0.0 && Stopwatch.GetElapsedTime(stepWorkStarted).TotalSeconds > stepInterval)))
                    accumulator = 0.0;

                if (caught is not null || stepDone || awaitingPlayer)
                {
                    lock (_wakeLock)
                    {
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

                var minDayMs = MinSpeed5DayIntervalMs;
                if (minDayMs > 0.0 && speed >= 5 && stepDone && openedDay &&
                    caught is null && !awaitingPlayer && !_stopRequested && !_pausedDesired)
                {
                    if (lastSpeed5DayTicks != 0)
                    {
                        while (!_stopRequested && !_pausedDesired && _speedDesired >= 5)
                        {
                            var elapsedDayMs = Stopwatch.GetElapsedTime(lastSpeed5DayTicks).TotalMilliseconds;
                            var remMs = (int)Math.Ceiling(minDayMs - elapsedDayMs);
                            if (remMs <= 0) break;
                            lock (_wakeLock)
                            {
                                if (!_stopRequested && !_pausedDesired && _speedDesired >= 5)
                                    Monitor.Wait(_wakeLock, Math.Min(remMs, 16));
                            }
                        }
                    }
                    lastSpeed5DayTicks = Stopwatch.GetTimestamp();
                    lastTickTime = lastSpeed5DayTicks;
                }
            }
        }
        finally
        {
            _workerRunning = false;
        }
    }

    public void Dispose() => Stop();
}
