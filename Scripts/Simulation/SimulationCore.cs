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

    /// <summary>Hours into the day, 0-23. A campaign that steps a whole day never leaves 0.</summary>
    public int Hour;

    public GameDate(int year, int month, int day) => (Year, Month, Day, Hour) = (year, month, day, 0);

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

    /// <summary>Move the clock on, rolling hours into days and days into months.</summary>
    public void Advance(int hours)
    {
        if (hours <= 0) return;
        Hour += hours;
        while (Hour >= 24)
        {
            Hour -= 24;
            Advance();
        }
    }

    /// <summary>
    /// The invariant date. It is written into save files and printed by the bench, so it
    /// stays in one language whatever the interface is set to - see <see cref="Display"/>
    /// for the one a player reads.
    /// </summary>
    public override string ToString() => $"{Day} {MonthNames[Month - 1]} {Year}";

    /// <summary>
    /// The date in the player's language. Day, month and year in that order, which every
    /// language the game ships in happens to share; a language that does not would want a
    /// frame here rather than three pieces glued together.
    /// </summary>
    /// <param name="withTime">
    /// Show the clock even at midnight. A campaign stepping six hours at a time spends three
    /// quarters of its life at an hour worth printing and the other quarter at midnight, and
    /// a clock that vanished for that quarter would read as a bug rather than as a tidiness.
    /// A campaign stepping a whole day is never anywhere but midnight, so it says nothing.
    /// </param>
    public string Display(bool withTime = false) => Hour == 0 && !withTime
        ? $"{Day} {Loc.Get(MonthNames[Month - 1])} {Year}"
        : $"{Day} {Loc.Get(MonthNames[Month - 1])} {Year}, {Hour:00}:00";
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
        while (_pending.Count > 0) DiagnosticLogs.TraceCommand(campaign, _pending.Dequeue());
    }
}

public sealed partial class Nation
{
    // Read-only projection for flag selection; the live country and its collections are never changed.
    internal Nation FlagPoliticsPreview(string government, string ideology)
    {
        var preview = (Nation)MemberwiseClone();
        if (!string.IsNullOrWhiteSpace(government)) preview.Government = government;
        if (!string.IsNullOrWhiteSpace(ideology)) preview.Ideology = ideology;
        return preview;
    }

    public int Id;
    public string Tag = "";
    public string Iso = "";
    public string Name = "";
    // Additive save fields: older campaigns default to no proclamation.
    public string FormedNationId = "";
    public int FormedNationDay = -1;

    /// <summary>
    /// The country's name as a player reads it.
    ///
    /// <see cref="Name"/> is what `common/countries` calls it, and stays that in save
    /// files, logs and the bench, where a value that changed with the interface language
    /// would be a value nothing could compare. This is the same name looked up in the
    /// player's table - and because a missing row comes back unchanged, a country a mod
    /// invented reads as whatever the mod called it until the mod also translates it.
    /// </summary>
    public string DisplayName => CountryNames.Resolve(this, false);
    public string MapName => CountryNames.Resolve(this, true);

    public string Continent = "";
    public string Region = "";
    public string Color = "#888888";
    public string CapitalName = "";

    /// <summary>
    /// The region the capital stands in, or 0 while the map is not attached.
    ///
    /// Worked out once from the capital's coordinates rather than authored, because the
    /// capital is a place on the globe and the regions under it can be redrawn by a war.
    /// </summary>
    public int CapitalStateMapIndex;

    /// <summary>
    /// What this country is beyond its conditions: institutions, legitimacy, a century of
    /// habit. Set once when the scenario starts, to the difference between the stability it
    /// authored and what the formula explains, and fixed after that. See
    /// <see cref="StabilityCore"/>.
    /// </summary>
    public float StabilityBias;

    /// <summary>
    /// How this government has been doing lately. Accumulates from the cabinet, the form of
    /// government, the ideology and the budget, bounded tightly and decaying back towards
    /// nothing - so a decade of good government is worth something real and not everything,
    /// and a bad decade can be recovered from.
    /// </summary>
    public float StabilityGovernance;

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

    /// <summary>
    /// How the country is arranged, and what its government is trying to do.
    ///
    /// Two axes rather than one list: a parliamentary republic can be liberal or
    /// socialist, and a one-party state can be socialist or fascist. Ids rather than
    /// enums because both catalogues are files, and a mod inventing a fourteenth
    /// ideology should not need a value reserved for it in the code.
    /// </summary>
    public string Government = "";

    public string Ideology = "";

    /// <summary>The day the last change of either landed, for the cooling-off period.</summary>
    public int PoliticsChangedDay = -9999;

    /// <summary>
    /// The day this country last lost part of itself - or gained itself, by leaving.
    ///
    /// A state does not come apart in four pieces on the same afternoon, and a country three
    /// weeks old is not yet somewhere anybody secedes from. One number serves both cases
    /// because it is the same fact read from either side of the new border.
    /// </summary>
    public int LastSecessionDay = -9999;

    /// <summary>
    /// Who this country is for, when the scenario never said - which is every country the
    /// campaign founds rather than loads.
    ///
    /// `history/pops` answers this for the 261 the world ships with, and a tag it has never
    /// heard of is a state that is for nobody: every person inside it is a minority of it.
    /// Held here as well as in the catalogue because the catalogue is a file and this is a
    /// fact about a particular country in a particular save - and a country that forgot who
    /// it was on reload would come apart on the strength of having been reloaded.
    /// </summary>
    public readonly List<string> FoundedCultures = new();

    public float WeeklyTaxIncome;
    public float WeeklyExpenses;
    public float WeeklyBalance;

    /// <summary>
    /// Where the week's balance came from, and where stability and war support are being
    /// pushed. These are what the interface shows when a number is hovered, and they are
    /// refilled each economic week rather than rebuilt - a country is not allowed to make
    /// garbage just to explain itself.
    /// </summary>
    public readonly Breakdown Budget = new();
    public readonly Breakdown StabilityDrivers = new();
    public readonly Breakdown WarSupportDrivers = new();
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

    /// <summary>
    /// How much of the country lives in towns, and how much of it works the land.
    ///
    /// Both are left at zero until a scenario says otherwise, and zero means "not stated"
    /// rather than "none": a country that says nothing has these worked out from what it
    /// earns per head, which is a decent guess and never as good as the real number. They
    /// are here rather than derived on the spot because they decide what trades every one
    /// of the country's regions holds, and a figure that half the game derives and half
    /// looks up is a figure the two halves will eventually disagree about.
    /// </summary>
    public float Urbanisation;

    public float AgricultureShare;
    public float Stability = 0.65f;
    public float ConstructionCapacity = 1f;
    public readonly Dictionary<string, int> DecisionAvailableDay = new(StringComparer.Ordinal);
    public float WarSupport = 1f;
    public float ManpowerReserve;

    /// <summary>
    /// How far each of this country's regions is towards standing a militia formation up,
    /// and the day each of its militia formations last had an enemy near it.
    ///
    /// On the country rather than on <see cref="MilitiaCore"/>, because two campaigns can
    /// exist in one process - a multiplayer client rebuilding from a snapshot beside the
    /// one it is replacing - and a static keyed by a region index belongs to whichever
    /// world wrote to it last. That is exactly how two worlds start reading each other's
    /// notes, and the bench checks for it.
    /// </summary>
    public readonly Dictionary<int, float> MilitiaReadiness = new();
    public readonly Dictionary<int, int> MilitiaQuietSince = new();
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

    /// <summary>
    /// What this government has set every budget line to, except the four that have fields
    /// of their own above.
    ///
    /// Those four - taxation, the armed forces, welfare, education - are named directly by
    /// three sliders, the AI cabinet, four campaign commands, every scripted effect and
    /// every save ever written, and moving them in here would be a rename with no gain. A
    /// line the data adds has none of that history, so it lives here and costs a country
    /// one dictionary entry. See <see cref="BudgetCore.RateOf"/>.
    /// </summary>
    public readonly Dictionary<string, float> PolicyRates = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Lines the player has brushed onto the map, and who is on them.</summary>
    public readonly List<DrawnFrontLine> FrontLines = new();
    public int NextFrontLineId;
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
    public string ScenarioId = "2024";
    public Dictionary<string, int[]> ScenarioTerritories = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Hours since the campaign began. The authoritative clock.
    ///
    /// Days are derived from it rather than counted alongside it, because two counters of
    /// the same thing disagree the first time anything is restored, rounded or skipped.
    /// </summary>
    public long ElapsedHours { get; private set; }

    /// <summary>Whole days since the campaign began. Every timestamp in the game is one of these.</summary>
    public int ElapsedDays => (int)(ElapsedHours / 24);

    /// <summary>
    /// Hours one step of the simulation covers, as the defines ask for it. Clamped to
    /// something a campaign can actually be run in: below an hour nothing accumulates
    /// enough to see, and beyond ten days a single step hides a war.
    /// </summary>
    public int TickHours => Math.Clamp(Defines?.HoursPerTick ?? 24, 1, 240);

    /// <summary>
    /// How much of a day one step covers. Every rate written "per day" is multiplied by
    /// this; at the default of 24 hours it is 1 and nothing changes.
    /// </summary>
    public float TickScale => TickHours / 24f;
    public bool IsPaused { get; set; } = true;
    public Nation Player { get; private set; }
    public int ActiveSeat { get; private set; }
    public bool IsHotseat => HumanSeats.Count > 1;
    public ProvinceTopology ProvinceMap { get; private set; }

    /// <summary>How many provinces the political map file decided, rather than the raster.</summary>
    public int PoliticalOverridesApplied { get; private set; }
    public ushort[] ProvinceController { get; private set; }
    public float[] ProvinceOccupation { get; private set; }
    public int MapRevision { get; private set; }
    public bool InitialDeploymentCompleted { get; private set; }
    internal int NextBattleId { get; set; }
    private ushort[] _pendingProvinceController;
    private float[] _pendingProvinceOccupation;

    public event Action Changed;

    public Campaign(int seed, Defines scenarioRules = null)
    {
        Seed = seed;
        Random = new SimRandom(seed);
        if (scenarioRules is null) GameDataLoader.ApplyDefines(Defines);
        else Defines = scenarioRules;
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

    /// <summary>
    /// Put every country the campaign founded back into the demography catalogue.
    ///
    /// The catalogue is loaded from files and knows nothing about a country that did not
    /// exist when they were read. Restoring a save has to say it again, or a country that
    /// seceded in 2031 reloads as a state that is for nobody and comes apart for having
    /// been reloaded - which is the worst class of bug there is, because it only happens to
    /// people who stopped playing and came back.
    /// </summary>
    public void DeclareFoundedPeoples()
    {
        foreach (var nation in Nations)
            if (nation.FoundedCultures.Count > 0)
                DemographyCatalog.Declare(nation.Tag, nation.FoundedCultures);
    }

    public Nation NationById(int id) => id >= 0 && id < Nations.Count ? Nations[id] : null;
    public Nation NationByTag(string tag) => Nations.FirstOrDefault(n =>
        string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(n.Iso, tag, StringComparison.OrdinalIgnoreCase));

    public void Tick()
    {
        if (IsPaused) return;
        DiagnosticLogs.SetDay(ElapsedDays);
        if (DiagnosticLogs.Enabled) DiagnosticLogs.Write("simulation", $"DAY_BEGIN date={Date} pending_commands={Commands.Count}");

        Commands.ApplyAll(this);

        // What the step covers, and how many whole days it crosses. A day boundary is what
        // the day pass is for; the hours are what everything that accumulates gets.
        var hours = TickHours;
        var before = ElapsedDays;
        ElapsedHours += hours;
        Date.Advance(hours);
        var daysCrossed = ElapsedDays - before;
        DiagnosticLogs.SetDay(ElapsedDays);

        // Each pass is measured so both the in-game console (`sim`) and the headless
        // bench can say which system a slow day was actually spent in, rather than
        // leaving "the simulation is slow" as the whole diagnosis.
        //
        // These accumulate: marching, supply, organisation, the fighting itself and the
        // building of things all happen at a rate per day, and a step that covers a
        // fraction of a day gives them that fraction. They run once per step whatever the
        // step is, which is what lets a six-hour tick move an army a quarter of the way
        // instead of not at all.
        using (SimProfiler.Measure("logistics")) LogisticsSystem.Tick(this);
        using (SimProfiler.Measure("military")) MilitaryOperations.Tick(this);
        using (SimProfiler.Measure("naval"))
        {
            AiAdmiralty.Tick(this);
            NavalOperations.Tick(this);
        }
        using (SimProfiler.Measure("warfare")) Warfare.Tick(this);
        // Right after the fighting: the step's broken formations are removed and the
        // countries that lost them start replacing them.
        using (SimProfiler.Measure("attrition")) Attrition.Tick(this);
        using (SimProfiler.Measure("construction")) ConstructionSystem.Tick(this);

        // And these happen on a day rather than during one. An event either fires today or
        // it does not; the economy clears on the week; history takes one sample. Running
        // them once for each whole day the step crossed is what keeps the answer the same
        // whether the campaign is stepping six hours at a time or three days.
        for (var day = 0; day < daysCrossed; day++)
        {
            RecruitmentSystem.Tick(this);
            using (SimProfiler.Measure("militia")) MilitiaCore.Tick(this);
            using (SimProfiler.Measure("modifiers")) ModifierSystem.Tick(this);
            using (SimProfiler.Measure("opinion")) OpinionSystem.Tick(this);
            using (SimProfiler.Measure("economy")) EconomySystem.Tick(this);
            using (SimProfiler.Measure("diplomacy")) Statecraft.Tick(this);
            using (SimProfiler.Measure("statecraft")) AiStatecraft.Tick(this);
            using (SimProfiler.Measure("events")) EventSystem.Tick(this);
            using (SimProfiler.Measure("history")) History.Capture(this);

            // Whether any part of any country has finished deciding it is not part of it.
            // Last in the day, because it redraws the map the rest of the day was worked
            // out on; it keeps its own weekly schedule, because it also has to clear its
            // report list on the days it does nothing.
            using (SimProfiler.Measure("secession")) SecessionEvents.Tick(this);

            // And whether any country has stopped being what it was. After secession
            // because a state that has just lost a province is a state in exactly the
            // condition this reads.
            using (SimProfiler.Measure("governance")) GovernanceEvents.Tick(this);
        }
        CampaignIntel.Invalidate(this);
        if (DiagnosticLogs.Enabled)
            DiagnosticLogs.Write("simulation", $"DAY_END date={Date} player={Player?.Tag} divisions={Armies.Count} fleets={Fleets.Count} wars={Wars.Count(w => !w.Ended)} battles={Battles.Count(b => !b.Ended)} training={Recruitment.Count} construction={Construction.Count} treasury={DiagnosticLogs.N(Player?.Treasury ?? 0)} gdp={DiagnosticLogs.N(Player?.Gdp ?? 0)} map_revision={MapRevision}");

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
            army.PlannedRoute.Clear();
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

        // The political map is data. The raster says where the provinces are; this says who
        // owns them, and a mod that moves one border only has to name that one province.
        ProvinceController = (ushort[])topology.ProvinceToNation.Clone();
        var politicalOverrides = ScenarioId == "2024" ? ProvinceOwnership.Apply(this, topology, ProvinceController) : 0;
        if (politicalOverrides > 0) PoliticalOverridesApplied = politicalOverrides;
        ProvinceOccupation = new float[topology.ProvinceCount + 1];

        // A fleet's home port is a province, and provinces only exist once the map is
        // attached. Until then a fleet knows where it is but not what it belongs to.
        FleetHistory.AssignHomePorts(this);
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

        // Now that the map knows who owns what, make the region list agree with it. Both
        // passes are no-ops on a world that is already consistent, which is what makes them
        // safe to run on a loaded save as well as a new campaign: a save that was written
        // before regions could be split heals here instead of staying wrong for ever.
        StatePartition.RepartitionWorld(this);
        StatePartition.Reunify(this);

        // Where each capital actually stands, and what that makes of the country. Both need
        // the map: a capital is a place on the globe rather than a region a scenario names,
        // and the regions under it can be redrawn by a war.
        LocateCapitals();
        // Only ever from the authored world. Whether that has happened is read from the
        // world rather than remembered in a field, because a multiplayer client adopts a
        // campaign it did not create: a flag in memory says "not yet" about a world that was
        // calibrated on the host an hour ago, and recalibrating a world at war writes the
        // war into every country's institutions.
        if (!StabilityCore.IsCalibrated(this)) StabilityCore.Calibrate(this);

        MapRevision++;

        // Only a world that has not been played yet gets its orders handed out here.
        //
        // This used to run every time the map was attached, which is also what happens when
        // a campaign arrives from somewhere else: a multiplayer client sent the host's world
        // re-tasked every AI formation in it the moment it adopted it, and was a different
        // world before it had simulated a single step. A loaded save had the same problem
        // more quietly - the orders you saved were not the orders you got back.
        //
        // Nothing is lost by not repeating it. The AI reassigns its own fronts as the war
        // moves, and a restored world keeps the orders it was saved with, which is the whole
        // point of restoring it.
        if (!InitialDeploymentCompleted)
        {
            StrategicDeploymentPlanner.Deploy(this);
            InitialDeploymentCompleted = true;
            Warfare.RebuildFronts(this);
            FrontPlanner.AssignAi(this);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Find the region each capital stands in. Nearest province of the country's own ground
    /// to the capital's coordinates, which is also how the deployment planner finds it -
    /// a capital that has been overrun still points at the region it is in.
    /// </summary>
    public void LocateCapitals()
    {
        var topology = ProvinceMap;
        if (topology is null) return;

        var byNation = new Dictionary<ushort, List<int>>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var owner = topology.ProvinceToNation[province];
            if (owner == 0) continue;
            if (!byNation.TryGetValue(owner, out var list)) byNation[owner] = list = new List<int>();
            list.Add(province);
        }

        foreach (var nation in Nations)
        {
            nation.CapitalStateMapIndex = 0;
            if (!byNation.TryGetValue((ushort)nation.MapIndex, out var provinces) || provinces.Count == 0)
                continue;
            var best = provinces[0];
            var bestDistance = float.MaxValue;
            var longitudeScale = MathF.Max(0.12f, MathF.Cos(nation.CapitalLatitude * M.Pi / 180f));
            foreach (var province in provinces)
            {
                var centre = topology.CenterOf(province);
                var dx = MathF.Abs(centre.Longitude - nation.CapitalLongitude);
                dx = MathF.Min(dx, 360f - dx) * longitudeScale;
                var dy = centre.Latitude - nation.CapitalLatitude;
                var distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = province;
            }
            nation.CapitalStateMapIndex = topology.ProvinceToState[best];
        }
    }

    private ushort[] _pendingProvinceOwner;
    private ushort[] _pendingProvinceState;

    internal void RestoreRuntimeState(GameDate date, int elapsedDays, bool paused, uint randomState,
        int playerId, IReadOnlyList<int> humanSeats, int activeSeat, int nextBattleId,
        int mapRevision, bool initialDeploymentCompleted, ushort[] controllers, float[] occupation, ushort[] owners, ushort[] states)
    {
        Date = date;
        ElapsedHours = Math.Max(0, elapsedDays) * 24L + Math.Clamp(date.Hour, 0, 23);
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

    /// <summary>
    /// Something happened that the interface should redraw for.
    ///
    /// This used to bump the map revision and fire Changed on the spot, and it is called by
    /// nearly every action any country takes: improving relations, queuing a building,
    /// raising a division, taking a decision, resolving an event. In a world of two hundred
    /// and sixty countries that is eight or nine times a campaign day - so every one of
    /// them rebuilt the player's whole interface, and invalidated the border table that
    /// every AI heuristic reads, for something that had not touched the map at all.
    ///
    /// It sets a flag now. The game flushes it once a frame, so ten changes in one day cost
    /// one redraw instead of ten, and none of them costs a walk of the province map.
    /// </summary>
    public void MarkStrategicChange() => InterfaceDirty = true;

    /// <summary>Something moved a border. This is what invalidates map-derived caches.</summary>
    public void MarkMapChanged()
    {
        MapRevision++;
        InterfaceDirty = true;
    }

    /// <summary>Whether anything has happened that the interface has not drawn yet.</summary>
    public bool InterfaceDirty { get; private set; }

    /// <summary>Redraw once for everything that has happened since the last frame.</summary>
    public void FlushInterfaceChanges()
    {
        if (!InterfaceDirty) return;
        InterfaceDirty = false;
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

        /// <summary>
        /// Bytes this pass has asked the allocator for, in total. Wall time on a machine
        /// that is also running an editor is noise; garbage is not, and garbage is what
        /// turns into a stutter every time the collector has to catch up.
        /// </summary>
        public long AllocatedBytes;

        /// <summary>
        /// Total time, so a mean can be taken. The exponential average above is what a live
        /// readout wants; it is useless for comparing two runs, because it depends on which
        /// calls happened last.
        /// </summary>
        public double TotalMilliseconds;

        public double MeanMilliseconds => Runs == 0 ? 0 : TotalMilliseconds / Runs;
    }

    private static readonly Dictionary<string, Sample> Samples = new();

    public static IReadOnlyCollection<Sample> All => Samples.Values;

    public static Scope Measure(string name) => new(name);

    public readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _started;
        private readonly long _allocated;

        public Scope(string name)
        {
            _name = name;
            _allocated = GC.GetAllocatedBytesForCurrentThread();
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var elapsed = (Stopwatch.GetTimestamp() - _started) * 1000.0 / Stopwatch.Frequency;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocated;
            if (!Samples.TryGetValue(_name, out var sample))
            {
                sample = new Sample { Name = _name };
                Samples.Add(_name, sample);
            }

            sample.AllocatedBytes += allocated;
            sample.TotalMilliseconds += elapsed;
            sample.LastMilliseconds = elapsed;
            sample.AverageMilliseconds = sample.Runs == 0
                ? elapsed
                : sample.AverageMilliseconds + (elapsed - sample.AverageMilliseconds) * 0.2;
            sample.Runs++;
            if (DiagnosticLogs.Enabled && elapsed >= DiagnosticLogs.SlowScopeMilliseconds)
                DiagnosticLogs.Write("performance", $"SLOW_SCOPE name={_name} duration_ms={DiagnosticLogs.N(elapsed)} allocated_bytes={allocated}");
        }
    }
}
