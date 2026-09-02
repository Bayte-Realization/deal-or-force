using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Engine-agnostic deterministic simulation foundation. This code intentionally depends
/// on neither Godot nor s&box, so a campaign can later run in headless tests or multiplayer.
/// </summary>
internal static class M
{
    public const float Pi = 3.14159265f;

    public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    public static float Clamp01(float value) => Clamp(value, 0f, 1f);
    public static float Lerp(float from, float to, float weight) => from + (to - from) * Clamp01(weight);
    public static float Max(float a, float b) => a > b ? a : b;
    public static int Max(int a, int b) => a > b ? a : b;
    public static float Min(float a, float b) => a < b ? a : b;
    public static int Min(int a, int b) => a < b ? a : b;
}

/// <summary>Stable xorshift random stream; save/replay safety must not rely on System.Random.</summary>
public sealed class SimRandom
{
    public uint State { get; private set; }

    public SimRandom(int seed)
    {
        State = (uint)seed;
        if (State == 0) State = 0x9E3779B9u;
        for (var i = 0; i < 8; i++) NextUInt();
    }

    public uint NextUInt()
    {
        State ^= State << 13;
        State ^= State >> 17;
        State ^= State << 5;
        return State;
    }

    public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);
    public int Range(int min, int max) => max <= min ? min : min + (int)(NextUInt() % (uint)(max - min));
    public bool Chance(float chance) => NextFloat() < chance;
    internal void Restore(uint state) => State = state == 0 ? 0x9E3779B9u : state;
}

/// <summary>Small Gregorian-like campaign calendar; its serialized fields are engine independent.</summary>
public struct GameDate
{
    private static readonly int[] MonthLengths = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
    private static readonly string[] MonthNames =
    {
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"
    };

    public int Year;
    public int Month;
    public int Day;

    public GameDate(int year, int month, int day) => (Year, Month, Day) = (year, month, day);

    public void Advance()
    {
        Day++;
        if (Day <= MonthLengths[Month - 1]) return;
        Day = 1;
        Month++;
        if (Month <= 12) return;
        Month = 1;
        Year++;
    }

    public override string ToString() => $"{Day} {MonthNames[Month - 1]} {Year}";
}

/// <summary>Commands are the only legal way UI and AI mutate the campaign.</summary>
public interface ICampaignCommand
{
    int IssuerId { get; }
    void Apply(Campaign campaign);
}

public sealed class CampaignCommandQueue
{
    private readonly Queue<ICampaignCommand> _pending = new();
    /// <summary>Return true to hand authority to an external transport and suppress local application.</summary>
    public Func<ICampaignCommand, bool> SubmitInterceptor;

    public int Count => _pending.Count;

    public void Enqueue(ICampaignCommand command)
    {
        if (command is null) return;
        if (SubmitInterceptor?.Invoke(command) == true) return;
        _pending.Enqueue(command);
    }

    public void ApplyAll(Campaign campaign)
    {
        while (_pending.Count > 0) _pending.Dequeue().Apply(campaign);
    }
}

public sealed class Nation
{
    public int Id;
    public string Tag = "";
    public string Iso = "";
    public string Name = "";
    public string Continent = "";
    public string Region = "";
    public string Color = "#888888";
    public string CapitalName = "";
    public float CapitalLatitude;
    public float CapitalLongitude;
    public int MapIndex;
    public long Population;
    public float Treasury;
    public float Gdp;
    public float MilitarySpending;
    public float TaxRate = 0.20f;
    public float WelfareSpending = 0.055f;
    public float EducationSpending = 0.045f;
    public GovernmentFocus GovernmentFocus = GovernmentFocus.Balanced;
    public float WeeklyTaxIncome;
    public float WeeklyExpenses;
    public float WeeklyBalance;
    public float Productivity = 1f;

    /// <summary>
    /// Market value of everything the nation's states actually produced this economic
    /// week, priced at what the market cleared at. GDP is derived from this rather than
    /// being its own free-running number - see EconomySystem.Tick.
    /// </summary>
    public float GrossOutput;

    /// <summary>
    /// Converts weekly gross output into the GDP scale the campaign was authored in.
    /// Calibrated once, at seed, so day-zero emergent GDP equals the historical 2024
    /// figure the scenario shipped; everything after that is genuinely emergent.
    /// </summary>
    /// <summary>Zero means "not calibrated yet" - a save written before output became
    /// emergent deserialises to zero and is recalibrated on load rather than rejected.</summary>
    public float OutputScale;

    /// <summary>Share of the jobs the nation's buildings demand that its pops actually fill.</summary>
    public float EmployedShare = 1f;

    public float StandardOfLiving = 0.50f;
    public float Literacy = 0.65f;
    public float Stability = 0.65f;
    public float ConstructionCapacity = 1f;
    public readonly Dictionary<string, int> DecisionAvailableDay = new(StringComparer.Ordinal);
    public float WarSupport = 1f;
    public float ManpowerReserve;
    public float ReinforcementCapacity;
    public float ReinforcedLastWeek;
    /// <summary>Named conditions currently attached to this country - see ModifierCore.</summary>
    public readonly List<ActiveModifier> Modifiers = new();

    public readonly Dictionary<Commodity, MarketQuote> Market = new();
    public float MarketAccess = 1f;
    public float ShortageIndex;
    public float WeeklyTradeBalance;
    public float WeeklyTariffIncome;
    public TradePolicy TradePolicy = TradePolicy.BalancedTrade;
    public bool IsPlayer;
    public bool IsHuman;
}

/// <summary>
/// The first portable replacement for the old GameSession/World boundary. Godot owns
/// presentation and inputs; this class owns only campaign state and tick ordering.
/// </summary>
public sealed partial class Campaign
{
    public readonly List<Nation> Nations = new();
    public readonly List<ScenarioState> States = new();
    public readonly List<Army> Armies = new();
    public readonly List<ArmyGroup> ArmyGroups = new();
    public readonly List<Fleet> Fleets = new();
    public readonly List<StrategicWar> Wars = new();
    public readonly List<LandBattle> Battles = new();
    public readonly List<ConstructionProject> Construction = new();
    public readonly List<PendingCampaignEvent> PendingEvents = new();
    public readonly Dictionary<long, DiplomaticRelation> Relations = new();
    public readonly List<int> HumanSeats = new();
    public readonly CampaignCommandQueue Commands = new();
    public readonly CampaignHistory History = new();
    public readonly SimRandom Random;
    public readonly Defines Defines = new();

    public int Seed { get; }
    public GameDate Date;
    public int ElapsedDays { get; private set; }
    public bool IsPaused { get; set; } = true;
    public Nation Player { get; private set; }
    public int ActiveSeat { get; private set; }
    public bool IsHotseat => HumanSeats.Count > 1;
    public ProvinceTopology ProvinceMap { get; private set; }
    public ushort[] ProvinceController { get; private set; }
    public float[] ProvinceOccupation { get; private set; }
    public int MapRevision { get; private set; }
    public bool InitialDeploymentCompleted { get; private set; }
    internal int NextBattleId { get; set; }
    private ushort[] _pendingProvinceController;
    private float[] _pendingProvinceOccupation;

    public event Action Changed;

    public Campaign(int seed)
    {
        Seed = seed;
        Random = new SimRandom(seed);
        GameDataLoader.ApplyDefines(Defines);
        Date = new GameDate(Defines.StartYear, Defines.StartMonth, Defines.StartDay);
    }

    /// <summary>Builds the current-world base scenario from the source catalogue.</summary>
    public static Campaign CreateBootstrap(int seed) => ScenarioCatalog.CreateBaseCampaign(seed);

    /// <summary>
    /// States indexed by the nation that owns them, built once and reused.
    ///
    /// Every system that works per-nation used to filter the whole 1,716-entry state list
    /// to find that nation's own regions - the economy did it twice per nation per weekly
    /// tick, which is roughly 900,000 comparisons and an array allocation per nation for
    /// an answer that never changes during a campaign. Conquest moves province control,
    /// not state ownership, so this index only has to be rebuilt when the state list
    /// itself is replaced (scenario setup, or restoring a save).
    /// </summary>
    private Dictionary<int, ScenarioState[]> _statesByNation;

    private static readonly ScenarioState[] NoStates = Array.Empty<ScenarioState>();

    public ScenarioState[] StatesOf(Nation nation) => nation is null ? NoStates : StatesOf(nation.MapIndex);

    /// <summary>
    /// Set BDOF_NO_STATE_INDEX=1 to fall back to the linear scan this index replaced.
    /// Only the bench uses it, to keep the "this optimisation is worth having" claim
    /// measurable rather than asserted.
    /// </summary>
    private static readonly bool DisableStateIndex =
        System.Environment.GetEnvironmentVariable("BDOF_NO_STATE_INDEX") == "1";

    public ScenarioState[] StatesOf(int nationMapIndex)
    {
        if (DisableStateIndex)
            return States.Where(state => state.NationMapIndex == nationMapIndex).ToArray();

        if (_statesByNation is null)
        {
            _statesByNation = new Dictionary<int, ScenarioState[]>();
            foreach (var group in States.GroupBy(state => state.NationMapIndex))
                _statesByNation[group.Key] = group.ToArray();
        }
        return _statesByNation.TryGetValue(nationMapIndex, out var states) ? states : NoStates;
    }

    /// <summary>Drop the ownership index. Call after replacing or re-owning states.</summary>
    public void InvalidateStateIndex() => _statesByNation = null;

    public Nation NationById(int id) => id >= 0 && id < Nations.Count ? Nations[id] : null;
    public Nation NationByTag(string tag) => Nations.FirstOrDefault(n =>
        string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(n.Iso, tag, StringComparison.OrdinalIgnoreCase));

    public void Tick()
    {
        if (IsPaused) return;

        Commands.ApplyAll(this);
        Date.Advance();
        ElapsedDays++;

        // Each pass is measured so both the in-game console (`sim`) and the headless
        // bench can say which system a slow day was actually spent in, rather than
        // leaving "the simulation is slow" as the whole diagnosis.
        using (SimProfiler.Measure("logistics")) LogisticsSystem.Tick(this);
        using (SimProfiler.Measure("military")) MilitaryOperations.Tick(this);
        using (SimProfiler.Measure("naval")) NavalOperations.Tick(this);
        using (SimProfiler.Measure("warfare")) Warfare.Tick(this);
        using (SimProfiler.Measure("construction")) ConstructionSystem.Tick(this);
        using (SimProfiler.Measure("modifiers")) ModifierSystem.Tick(this);
        using (SimProfiler.Measure("economy")) EconomySystem.Tick(this);
        using (SimProfiler.Measure("statecraft")) AiStatecraft.Tick(this);
        using (SimProfiler.Measure("events")) EventSystem.Tick(this);
        using (SimProfiler.Measure("history")) History.Capture(this);

        Changed?.Invoke();
    }

    public void SetPlayer(int nationId)
    {
        foreach (var nation in Nations) nation.IsPlayer = false;
        Player = NationById(nationId);
        if (Player is null) return;
        Player.IsPlayer = true;
        if (HumanSeats.Count == 0)
        {
            foreach (var nation in Nations) nation.IsHuman = false;
            Player.IsHuman = true;
            HumanSeats.Add(Player.Id);
            ActiveSeat = 0;
        }
        Changed?.Invoke();
    }

    public bool AddHumanSeat(int nationId)
    {
        var nation = NationById(nationId);
        if (nation is null || HumanSeats.Contains(nationId)) return false;
        HumanSeats.Add(nationId);
        nation.IsHuman = true;
        foreach (var army in Armies)
        {
            if (army.NationId != nationId || army.InBattle) continue;
            army.OrderKind = ArmyOrder.Manual;
            army.AssignedWarId = -1;
            army.OrderProvinceId = -1;
            army.OrderStateMapIndex = -1;
            army.Route.Clear();
            army.MoveProgress = 0f;
        }
        Changed?.Invoke();
        return true;
    }

    public bool RemoveHumanSeat(int nationId)
    {
        if (HumanSeats.Count <= 1 || !HumanSeats.Remove(nationId)) return false;
        var nation = NationById(nationId);
        if (nation is not null) nation.IsHuman = false;
        if (ActiveSeat >= HumanSeats.Count) ActiveSeat = 0;
        TakeSeat(ActiveSeat);
        if (ProvinceMap is not null) FrontPlanner.AssignAi(this);
        return true;
    }

    public void ConfigureSinglePlayer(int nationId)
    {
        foreach (var nation in Nations) { nation.IsHuman = false; nation.IsPlayer = false; }
        HumanSeats.Clear();
        ActiveSeat = 0;
        AddHumanSeat(nationId);
        TakeSeat(0);
    }

    public bool TakeSeat(int index)
    {
        if (HumanSeats.Count == 0) return false;
        ActiveSeat = M.Clamp(index, 0, HumanSeats.Count - 1);
        var nation = NationById(HumanSeats[ActiveSeat]);
        if (nation is null) return false;
        foreach (var other in Nations) other.IsPlayer = false;
        nation.IsPlayer = true;
        nation.IsHuman = true;
        Player = nation;
        IsPaused = true;
        Changed?.Invoke();
        return true;
    }

    public bool NextSeat() => HumanSeats.Count > 1 && TakeSeat((ActiveSeat + 1) % HumanSeats.Count);

    public void SetMilitarySpending(int nationId, float value)
    {
        var nation = NationById(nationId);
        if (nation is null) return;
        nation.MilitarySpending = M.Clamp01(value);
        Changed?.Invoke();
    }

    public void SetTaxRate(int nationId, float value)
    {
        var nation = NationById(nationId);
        if (nation is null) return;
        nation.TaxRate = M.Clamp(value, 0.05f, 0.55f);
        Changed?.Invoke();
    }

    public void SetTradePolicy(int nationId, TradePolicy policy)
    {
        var nation = NationById(nationId);
        if (nation is null || !Enum.IsDefined(policy)) return;
        nation.TradePolicy = policy;
        Changed?.Invoke();
    }

    public void AttachProvinceTopology(ProvinceTopology topology)
    {
        ProvinceMap = topology;
        ProvinceController = (ushort[])topology.ProvinceToNation.Clone();
        ProvinceOccupation = new float[topology.ProvinceCount + 1];
        var savedTopologyMatches = _pendingProvinceController is not null &&
            _pendingProvinceOccupation is not null &&
            _pendingProvinceController.Length == ProvinceController.Length &&
            _pendingProvinceOccupation.Length == ProvinceOccupation.Length;
        if (savedTopologyMatches)
        {
            ProvinceController = _pendingProvinceController;
            ProvinceOccupation = _pendingProvinceOccupation;
        }
        _pendingProvinceController = null;
        _pendingProvinceOccupation = null;

        // Borders a peace redrew are campaign state, not map data. The raster on disk still
        // says who owned what in 2024; a save that annexed provinces and split regions has
        // to put its own political layer back over it, or loading would quietly hand every
        // conquest back and leave the region list describing a map that no longer exists.
        if (_pendingProvinceOwner is not null && _pendingProvinceOwner.Length == topology.ProvinceToNation.Length)
            Array.Copy(_pendingProvinceOwner, topology.ProvinceToNation, _pendingProvinceOwner.Length);
        if (_pendingProvinceState is not null && _pendingProvinceState.Length == topology.ProvinceToState.Length)
            Array.Copy(_pendingProvinceState, topology.ProvinceToState, _pendingProvinceState.Length);
        _pendingProvinceOwner = null;
        _pendingProvinceState = null;
        MapRevision++;
        Warfare.RebuildFronts(this);
        if (!InitialDeploymentCompleted)
        {
            StrategicDeploymentPlanner.Deploy(this);
            InitialDeploymentCompleted = true;
        }
        FrontPlanner.AssignAi(this);
        Changed?.Invoke();
    }

    private ushort[] _pendingProvinceOwner;
    private ushort[] _pendingProvinceState;

    internal void RestoreRuntimeState(GameDate date, int elapsedDays, bool paused, uint randomState,
        int playerId, IReadOnlyList<int> humanSeats, int activeSeat, int nextBattleId,
        int mapRevision, bool initialDeploymentCompleted, ushort[] controllers, float[] occupation, ushort[] owners, ushort[] states)
    {
        Date = date;
        ElapsedDays = Math.Max(0, elapsedDays);
        IsPaused = paused;
        Random.Restore(randomState);
        NextBattleId = Math.Max(0, nextBattleId);
        MapRevision = Math.Max(0, mapRevision);
        InitialDeploymentCompleted = initialDeploymentCompleted;
        HumanSeats.Clear();
        if (humanSeats is not null)
            foreach (var id in humanSeats.Where(id => id >= 0 && id < Nations.Count).Distinct())
                HumanSeats.Add(id);
        if (HumanSeats.Count == 0 && Nations.Count > 0) HumanSeats.Add(Math.Clamp(playerId, 0, Nations.Count - 1));
        ActiveSeat = Math.Clamp(activeSeat, 0, Math.Max(0, HumanSeats.Count - 1));
        foreach (var nation in Nations)
        {
            nation.IsHuman = HumanSeats.Contains(nation.Id);
            nation.IsPlayer = nation.Id == playerId;
        }
        Player = NationById(playerId) ?? NationById(HumanSeats[ActiveSeat]) ?? Nations.FirstOrDefault();
        if (Player is not null) Player.IsPlayer = true;
        _pendingProvinceController = controllers;
        _pendingProvinceOccupation = occupation;
        _pendingProvinceOwner = owners;
        _pendingProvinceState = states;
    }

    public bool SetProvinceController(int provinceId, ushort controller)
    {
        if (ProvinceController is null || provinceId <= 0 || provinceId >= ProvinceController.Length ||
            ProvinceController[provinceId] == controller)
            return false;
        ProvinceController[provinceId] = controller;
        ProvinceOccupation[provinceId] = 0f;
        MapRevision++;
        return true;
    }

    public void MarkStrategicChange()
    {
        MapRevision++;
        Changed?.Invoke();
    }

    internal Nation AddNation(Nation nation, bool player = false)
    {
        if (nation is null) throw new ArgumentNullException(nameof(nation));

        nation.Id = Nations.Count;
        if (nation.MilitarySpending <= 0f)
            nation.MilitarySpending = Defines.BaselineMilitarySpending;

        nation.IsPlayer = player;
        nation.IsHuman = player;
        Nations.Add(nation);
        if (player)
        {
            Player = nation;
            HumanSeats.Add(nation.Id);
        }
        return nation;
    }
}

public sealed class SetMilitarySpendingCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public float Value { get; init; }

    public void Apply(Campaign campaign) => campaign.SetMilitarySpending(IssuerId, Value);
}

public sealed class SetTaxRateCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public float Value { get; init; }

    public void Apply(Campaign campaign) => campaign.SetTaxRate(IssuerId, Value);
}

public sealed class SetTradePolicyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public TradePolicy Policy { get; init; }

    public void Apply(Campaign campaign) => campaign.SetTradePolicy(IssuerId, Policy);
}

/// <summary>Engine-independent sample profiler. Godot's monitors complement, not replace it.</summary>
public static class SimProfiler
{
    public sealed class Sample
    {
        public string Name = "";
        public double LastMilliseconds;
        public double AverageMilliseconds;
        public int Runs;
    }

    private static readonly Dictionary<string, Sample> Samples = new();

    public static IReadOnlyCollection<Sample> All => Samples.Values;

    public static Scope Measure(string name) => new(name);

    public readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _started;

        public Scope(string name)
        {
            _name = name;
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var elapsed = (Stopwatch.GetTimestamp() - _started) * 1000.0 / Stopwatch.Frequency;
            if (!Samples.TryGetValue(_name, out var sample))
            {
                sample = new Sample { Name = _name };
                Samples.Add(_name, sample);
            }

            sample.LastMilliseconds = elapsed;
            sample.AverageMilliseconds = sample.Runs == 0
                ? elapsed
                : sample.AverageMilliseconds + (elapsed - sample.AverageMilliseconds) * 0.2;
            sample.Runs++;
        }
    }
}
