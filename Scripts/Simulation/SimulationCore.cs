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

/// <summary>Gregorian campaign calendar; its serialized fields are engine independent.</summary>
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
        var days = MonthLengths[Month - 1];
        if (Month == 2 && Year % 4 == 0 && (Year % 100 != 0 || Year % 400 == 0)) days++;
        if (Day <= days) return;
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
    private readonly Campaign _campaign;

    public CampaignCommandQueue(Campaign campaign = null) => _campaign = campaign;
    /// <summary>Return true to hand authority to an external transport and suppress local application.</summary>
    public Func<ICampaignCommand, bool> SubmitInterceptor;

    public int Count => _pending.Count;

    /// <summary>Throw away every order not yet carried out: a frozen world carries out none.</summary>
    internal void Discard() => _pending.Clear();

    public void Enqueue(ICampaignCommand command)
    {
        if (command is null) return;
        // Complete the world before a transport stamps/broadcasts an external order.
        // During a simulation stage this is already protected by FinishStep's guard.
        _campaign?.FinishStep();
        if (SubmitInterceptor?.Invoke(command) == true) return;
        _pending.Enqueue(command);
    }

    public void ApplyAll(Campaign campaign)
    {
        campaign.FinishStep();
        while (_pending.Count > 0)
        {
            var command = _pending.Dequeue();
            CrashEnvelope.Record(campaign, command);
            // Несмотря на имя, TraceCommand не только пишет лог: он вызывает Apply
            // ровно один раз и при выключенной диагностике тоже. Повторный Apply здесь
            // удвоит цену/набор войск. Граница записи и трассировки должна остаться общей.
            DiagnosticLogs.TraceCommand(campaign, command);
        }
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
    public HashSet<string> FormedNationHistory = new(StringComparer.OrdinalIgnoreCase);

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
    public float ImperialOverstretch;

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
    /// The country this one answers to, or zero. See <see cref="SubjectSystem"/>.
    ///
    /// An id and not a tag, because tags are reused as countries come and go and a
    /// dependency has to survive that. One overlord at most: a country with two masters is
    /// a country in a war, not a country in an arrangement.
    /// </summary>
    public int OverlordId;

    /// <summary>Which kind, by its id in `common/subjects`. Empty when there is no overlord.</summary>
    public string SubjectType = "";

    /// <summary>The day the arrangement began, which is what the grace period counts from.</summary>
    public int SubjectSinceDay = -9999;

    /// <summary>
    /// What this country paid its overlord last week, and what it took from its own
    /// subjects. Both are recorded rather than recomputed because a treasury carries a
    /// country's whole budget: a tribute of sixty million inside a deficit of four billion
    /// is invisible in the balance and is the thing a player wants to see.
    /// </summary>
    public float TributePaidWeekly;

    public float TributeTakenWeekly;

    /// <summary>
    /// How badly it wants out, nought to one, moving a little each week.
    ///
    /// Stored rather than computed on demand because it is a position rather than an
    /// opinion: a subject that has been drifting towards independence for six years does
    /// not forget that because its overlord had a good month.
    /// </summary>
    public float LibertyDesire;

    /// <summary>
    /// What kind of revolt founded this country, for the countries that were founded by one:
    /// `national`, `sectarian`, `discontent`, or empty for a state that was always here.
    ///
    /// Kept because it is the only thing a country born last Tuesday knows about itself that
    /// bears on what it should look like. A people that walked out has its own banner to
    /// reach for; a bloc of angry provinces that share nothing but a grievance does not, and
    /// the flag resolver needs to be told which of the two it is looking at rather than
    /// guessing from a culture that may be nobody's in particular.
    /// </summary>
    public string FoundedInRevolt = "";

    /// <summary>
    /// Who this country is for, when the scenario never said - which is every country the
    /// campaign founds rather than loads.
    ///
    /// history/countries answers this for the 261 the world ships with, and a tag it has never
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
    public Breakdown Budget = new();
    public Breakdown StabilityDrivers = new();
    public Breakdown WarSupportDrivers = new();

    /// <summary>
    /// How fast prices are rising here, as an annual rate. 0.03 is three per cent.
    ///
    /// Seeded at the comfortable rate rather than zero: a world that opens with every
    /// country in perfect price stability opens with two hundred and sixty central banks
    /// that have nothing to do, and the first deficit anybody runs looks like a catastrophe.
    /// </summary>
    public float Inflation = 0.02f;

    public Breakdown InflationDrivers = new();
    public float Productivity = 1f;

    /// <summary>
    /// Market value of everything the nation's states actually produced this economic
    /// week, priced at what the market cleared at. GDP is derived from this rather than
    /// being its own free-running number - see EconomySystem.Tick.
    /// </summary>
    public float GrossOutput;
    /// <summary>
    /// What a head of its people makes, at base prices, the first week it is measured, and how far
    /// its households' appetite has grown with it since (MarketCore, the recipes' "income"): one at
    /// the start, so the calibrated 2024 market is untouched, and rising as the country gets richer.
    /// </summary>
    public float IncomeBaseline;
    public float IncomeFactor = 1f;
    /// <summary>Jobs per worker its buildings asked for in its first market (MarketSystem.CapitalReturns).</summary>
    public float CapitalIntensityReference;

    /// <summary>
    /// Converts weekly gross output into the GDP scale the campaign was authored in.
    /// Calibrated once, at seed, so day-zero emergent GDP equals the historical 2024
    /// figure the scenario shipped; everything after that is genuinely emergent.
    ///
    /// Zero means "not calibrated yet" - a save written before output became emergent
    /// deserialises to zero and is recalibrated on load rather than rejected.
    /// </summary>
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

    /// <summary>
    /// When each event last came up here, by its id.
    ///
    /// Nine events and a memoryless weighted draw deals the same card again and again: a
    /// decade of a campaign is about fifty events out of a pool of nine, and a player who
    /// has seen the industrial bottleneck five times stops reading them. A public readonly
    /// collection, so the save writes it with everything else on the country.
    /// </summary>
    public readonly Dictionary<string, int> EventMemory = new();

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

    /// <summary>
    /// Whether this government has taken the key rate off its own central bank.
    ///
    /// False is the ordinary arrangement and the default for every country in the world: the
    /// bank holds the rate against its target and the government spends what it dares. True
    /// hands the lever to the player - and hands them the bill, because money a government
    /// has ordered to be cheap is money nobody believes will stay cheap. See
    /// <see cref="CentralBank"/>.
    ///
    /// A public field, so the save carries it without anything being written to carry it.
    /// </summary>
    public bool GovernmentSetsKeyRate;

    /// <summary>
    /// Portfolios this country has handed to its own civil service - see
    /// <see cref="Delegation"/>. Empty for everybody except a player who asked.
    /// </summary>
    public readonly HashSet<string> Delegated = new(StringComparer.Ordinal);

    /// <summary>
    /// How much money this government is having printed for it, as a share of a year's
    /// output per year. Zero for everybody until somebody decides otherwise, and forced back
    /// to zero the moment the central bank is independent again.
    /// </summary>
    public float MoneyPrinting;

    /// <summary>
    /// What the country is remembered for printing, which is not the same as what it is
    /// printing now.
    ///
    /// The press stopping does not un-print the money or restore anybody's confidence that
    /// it will stay stopped. This rises to whatever the press is running at and falls back
    /// slowly on its own, and it is what prices actually answer to - so a government that
    /// printed through a war goes on paying for it through the peace.
    /// </summary>
    public float EmissionMemory;

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

    /// <summary>Bumped whenever a war is added, ended or reshaped. Readers that keep a
    /// snapshot of the active wars (AtWar's cache) use it as their change stamp.</summary>
    public int WarRevision { get; private set; }

    /// <summary>Call at every war-list or war-state change a war-cache reader must see.</summary>
    public void MarkWarsChanged() => WarRevision++;

    /// <summary>Lines the player has brushed onto the map, and who is on them.</summary>
    public readonly List<DrawnFrontLine> FrontLines = new();
    public int NextFrontLineId;
    public readonly List<LandBattle> Battles = new();
    public readonly List<ConstructionProject> Construction = new();
    public readonly List<PendingCampaignEvent> PendingEvents = new();

    /// <summary>
    /// Watching rather than playing: no events are raised, nothing stops the clock, and
    /// the interface gets out of the way.
    ///
    /// A recorded timelapse of a world running for fifty years is the thing this game is
    /// most worth looking at, and the one thing that prevented it was that the game asks
    /// the player questions. This is the seat that is not asked.
    /// </summary>
    public bool ObserverMode;

    /// <summary>
    /// Nobody is at the wheel: the watching mode, or a campaign with no human seat and no human
    /// country at all (the top bar's "Observer mode"). The two used to be separate questions, so a
    /// world watched through Jordan's eyes was asked to sign Jordan into a Levant federation: the
    /// country being looked at is not a country being played.
    /// </summary>
    public bool NobodyPlays
    {
        get
        {
            if (ObserverMode) return true;
            if (HumanSeats.Count > 0) return false;
            foreach (var nation in Nations) if (nation.IsHuman) return false;
            return true;
        }
    }
    public readonly Dictionary<long, DiplomaticRelation> Relations = new();
    public readonly List<int> HumanSeats = new();
    public readonly CampaignCommandQueue Commands;
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
    /// Whether this step is the one that opened its campaign day.
    ///
    /// Most of what runs per step accumulates - a march, a supply draw, a round of fighting -
    /// and takes its share of the step through <see cref="TickScale"/>. A few things do not:
    /// they are decisions that belong to a DAY, and they say so by dividing the day number.
    /// "Every third day" written inside a pass that runs per step means every third step,
    /// which at a daily step is the same sentence and at an hourly step is twenty-four times
    /// on the third day and never on the other two.
    ///
    /// A pass that thinks in days asks this first. True at every step of a daily or longer
    /// step, true for the first of the twenty-four hourly ones, and true by default so that a
    /// pass called outside <see cref="Tick"/> - by the bench, by a test - behaves as it always
    /// has.
    /// </summary>
    public bool StepOpensNewDay { get; private set; } = true;

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
    public int LastTacticalDoctrineDay = -1;
    public int LastStrategicDoctrineDay = -1;
    internal readonly MovementRules.MovementPeopleCache MovementPeople = new();
    public bool InitialDeploymentCompleted { get; private set; }
    internal int NextBattleId { get; set; }
    private ushort[] _pendingProvinceController;
    private float[] _pendingProvinceOccupation;

    public event Action Changed;

    public Campaign(int seed, Defines scenarioRules = null)
    {
        Commands = new CampaignCommandQueue(this);
        Seed = seed;
        Random = new SimRandom(seed);
        if (scenarioRules is null) GameDataLoader.ApplyDefines(Defines);
        else Defines = scenarioRules;
        Date = new GameDate(Defines.StartYear, Defines.StartMonth, Defines.StartDay);
        // The crash envelope keeps this as the campaign a fatal handler reports on, so a
        // dump names the world that was playing even before the first order was issued.
        CrashEnvelope.Track(this);
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
        // The scan is a method of its own because its lambda captures the parameter, and a
        // closure over a parameter is allocated on entry - on every call, indexed or not,
        // and this is asked for every country by nearly every pass.
        if (DisableStateIndex) return StatesOfByScan(nationMapIndex);

        if (_statesByNation is null)
        {
            _statesByNation = new Dictionary<int, ScenarioState[]>();
            foreach (var group in States.GroupBy(state => state.NationMapIndex))
                _statesByNation[group.Key] = group.ToArray();
        }
        return _statesByNation.TryGetValue(nationMapIndex, out var states) ? states : NoStates;
    }

    private ScenarioState[] StatesOfByScan(int nationMapIndex) =>
        States.Where(state => state.NationMapIndex == nationMapIndex).ToArray();

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

    private Dictionary<int, Nation> _nationsByMapIndex;
    public Dictionary<int, Nation> NationsByMapIndex()
    {
        if (_nationsByMapIndex == null || _nationsByMapIndex.Count != Nations.Count)
        {
            _nationsByMapIndex = new Dictionary<int, Nation>();
            for (var i = 0; i < Nations.Count; i++) _nationsByMapIndex[Nations[i].MapIndex] = Nations[i];
        }
        return _nationsByMapIndex;
    }

    public Nation NationById(int id) => id >= 0 && id < Nations.Count ? Nations[id] : null;
    public Nation NationByMapIndex(int mapIndex)
    {
        for (var i = 0; i < Nations.Count; i++)
            if (Nations[i].MapIndex == mapIndex) return Nations[i];
        return null;
    }
    public Nation NationByTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        for (var i = 0; i < Nations.Count; i++)
        {
            var n = Nations[i];
            if (string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Iso, tag, StringComparison.OrdinalIgnoreCase))
                return n;
        }
        return null;
    }

    /// <summary>
    /// Advance one whole campaign day, whatever the step length is.
    ///
    /// <see cref="Tick"/> advances a STEP, and a step has not been a day since the step
    /// became a setting: at the shipped hour it takes twenty-four of them. Anything that
    /// means "a day" - a harness measuring a day's cost, a test that wants a year of world -
    /// has to say so, or it silently runs a twenty-fourth of what it thinks and every
    /// duration it checks becomes a different claim.
    ///
    /// Bounded, because a paused campaign never advances and a step longer than a day
    /// overshoots on purpose: a ten-day step crosses ten days and this returns after one
    /// call, which is the honest answer to "advance a day" at that setting.
    /// </summary>
    public void TickDay()
    {
        if (IsPaused || SimulationFault.Length > 0) return;
        var until = ElapsedDays + 1;
        for (var steps = 0; ElapsedDays < until && steps < 240; steps++) Tick();
    }

    /// <summary>One step, all of it now. A step begun by StepFor and not finished is finished first.</summary>
    public void Tick()
    {
        FinishStep();
        StepFor(double.PositiveInfinity);
    }

    private IEnumerator<int> _step;
    private bool _runningStages;
    private long _stepStartedHours;

    /// <summary>Whether a step has been begun by StepFor and is not finished yet.</summary>
    public bool MidStep => _step is not null;

    /// <summary>A complete step, after its iterator is cleared; saves and external orders share this boundary.</summary>
    public event Action<Campaign> StepCompleted;

    /// <summary>
    /// Begin a step, or carry on with the one begun, for at most this many milliseconds of
    /// work; true once it is finished. This is the smoothing: the day's passes and the week's
    /// economy are spread over as many frames as they need, in the order they always run, and
    /// the clock does not move on until the step is done - the same world, without the frame
    /// that used to stop for a third of a second every simulated week.
    /// </summary>
    public bool StepFor(double milliseconds)
    {
        if (_step is null)
        {
            _stepStartedHours = ElapsedHours;
            _step = TickStages().GetEnumerator();
        }
        return RunStages(_step, milliseconds);
    }

    /// <summary>Finish a step in progress, so a save, a snapshot or a command never sees half of one.</summary>
    public void FinishStep()
    {
        // TickStages applies its initial orders while MoveNext is executing. That is
        // already a stable boundary, and reentering the same iterator would discard it.
        if (_step is not null && !_runningStages) RunStages(_step, double.PositiveInfinity);
    }

    private bool RunStages(IEnumerator<int> stages, double milliseconds)
    {
        // A whole-step caller has no deadline. Bounded frames use a monotonic
        // timestamp, not a heap-allocated Stopwatch on every iterator resume.
        var bounded = !double.IsPositiveInfinity(milliseconds);
        var started = bounded ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        var finished = false;
        var startedHours = _stepStartedHours;
        _runningStages = true;
        try
        {
            while (true)
            {
                if (!stages.MoveNext()) { finished = true; break; }
                if (bounded && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= milliseconds) break;
            }
        }
        catch (Exception ex)
        {
            finished = true;
            CrashEnvelope.Dump(this, ex);
            throw;
        }
        finally
        {
            _runningStages = false;
            if (finished)
            {
                if (ReferenceEquals(stages, _step)) _step = null;
                stages.Dispose();
            }
        }
        // Notify only after clearing the iterator. A hook may issue commands or save;
        // neither should recurse into the day whose last stage just returned.
        if (finished && ElapsedHours != startedHours) StepCompleted?.Invoke(this);
        return finished;
    }

    private IEnumerable<int> TickStages()
    {
        // =====================================================================================
        // THE RHYTHM OF THE SIMULATION - the contract every system here obeys.
        //
        // A step is whatever the player or a bench asks for: six hours, one hour, three days. The
        // whole architecture is a promise that the answer does not depend on which of those it was.
        //
        //   PASSES PER STEP (this half of the method). Marching, supply, organisation, the fighting
        //   itself and the building of things accumulate at a rate per day, so they are handed the
        //   fraction of a day the step covers, and they run once per step whatever the step is. That
        //   is why a six-hour tick moves an army a quarter of the way instead of not at all.
        //
        //   PASSES PER DAY (the loop further down, marked "THE DAY, IN ORDER"). A decision happens
        //   on a day: an event either fires today or it does not, the economy clears on the week,
        //   history takes one sample. These run once for every whole day the step crossed, which is
        //   what stops them from running four times on an hourly clock and once on a six-hour one.
        //
        //   PASSES ON THEIR OWN CLOCK. Inside the day a system may gate itself: `OnItsDay` spreads
        //   the countries of the world across a cycle so that a monthly decision does not land on
        //   the same day for everybody, and `% 7` / `% 30` gates keep weekly and monthly work off
        //   the daily path. A gate is not an optimisation detail, it is part of this contract: a
        //   system that loses its gate runs thirty times more often than it was tuned for.
        //
        // Orders are applied at the top of the step, before anything reads the world, so a player's
        // command and a computer's are read at exactly the same point of the clock.
        // =====================================================================================
        if (IsPaused || SimulationFault.Length > 0) yield break;
        // A world under the ice or the static (WorldCurses): the clock goes on and nothing
        // else does - no system runs and no order is carried out, the player's included.
        if (WorldCurses.Frozen(this))
        {
            Commands.Discard();
            ElapsedHours += TickHours;
            Date.Advance(TickHours);
            StepOpensNewDay = false;
            DiagnosticLogs.SetDay(ElapsedDays);
            yield break;
        }
        DiagnosticLogs.SetDay(ElapsedDays);
        if (DiagnosticLogs.Enabled) DiagnosticLogs.Write("simulation", $"DAY_BEGIN date={Date} pending_commands={Commands.Count}");

        Commands.ApplyAll(this);
        EvaluateFeatureUnlocks();

        // The long-range reach index, rebuilt fresh from the orders the step has just
        // applied: the fighting below asks it who reaches into each battle, and a client
        // restored from a snapshot must hold the same answer a host does at the same hour.
        SupportCore.IndexReach(this);

        // What the step covers, and how many whole days it crosses. A day boundary is what
        // the day pass is for; the hours are what everything that accumulates gets.
        var hours = TickHours;
        var before = ElapsedDays;
        ElapsedHours += hours;
        Date.Advance(hours);
        var daysCrossed = ElapsedDays - before;
        // Read by the handful of per-step passes that are really once-a-day decisions. See
        // StepOpensNewDay for why they cannot simply be moved into the day loop below: they
        // have to run before the fighting, not after it.
        StepOpensNewDay = daysCrossed > 0;
        DiagnosticLogs.SetDay(ElapsedDays);
        // The weird route walks every formation towards the water before anything else moves.
        if (Curse.Length > 0) WorldCurses.March(this);

        // Each pass is measured so both the in-game console (`sim`) and the headless
        // bench can say which system a slow day was actually spent in, rather than
        // leaving "the simulation is slow" as the whole diagnosis.
        //
        // These accumulate: marching, supply, organisation, the fighting itself and the
        // building of things all happen at a rate per day, and a step that covers a
        // fraction of a day gives them that fraction. They run once per step whatever the
        // step is, which is what lets a six-hour tick move an army a quarter of the way
        // instead of not at all.
        using (SimProfiler.Measure("military access")) MilitaryAccess.Tick(this);
        using (SimProfiler.Measure("expeditions")) Expeditions.Tick(this);
        using (SimProfiler.Measure("border shape")) BorderShape.Tick(this);
        using (SimProfiler.Measure("civil war fronts")) CivilWarFronts.Tick(this);
        using (SimProfiler.Measure("movement mergers")) MovementMergers.Tick(this);
        using (SimProfiler.Measure("diplomatic acts")) DiplomaticActs.Tick(this);
        using (SimProfiler.Measure("logistics")) LogisticsSystem.Tick(this);
        // A frame may stop here and between the fleets and the fighting: the same passes in the
        // same order, but the step's heaviest pair - supply and the battles with their staff
        // work, thirty milliseconds together - no longer has to land in one frame.
        yield return 0;
        using (SimProfiler.Measure("military")) MilitaryOperations.Tick(this);
        using (SimProfiler.Measure("naval"))
        {
            using (SimProfiler.Measure("  naval:admiralty")) AiAdmiralty.Tick(this);
            using (SimProfiler.Measure("  naval:operations")) NavalOperations.Tick(this);
        }
        yield return 0;
        using (SimProfiler.Measure("warfare")) Warfare.Tick(this);
        // Right after the fighting: the step's broken formations are removed and the
        // countries that lost them start replacing them.
        using (SimProfiler.Measure("attrition")) Attrition.Tick(this);
        // A pocket the computer is in is a pocket it tries to leave.
        using (SimProfiler.Measure("pocket breakout")) PocketBreakout.Tick(this);
        // And a pocket whose stores are gone, with nobody coming, gives up (StrandedGarrisons).
        using (SimProfiler.Measure("pocket surrender")) StrandedGarrisons.Tick(this);
        // The ordinary staff is conservative by design. A war for existence is the one
        // exception: mend a hole with a reserve, take an empty enemy province, or queue an
        // emergency formation - all through the same commands and the same manpower/debt
        // rules every player uses.
        using (SimProfiler.Measure("war emergency")) WarEmergency.Tick(this);
        // What the services learned from that fighting, and what the army is reading: articles of
        // the war doctrine are studied by the day, and they are shares of the same arithmetic.
        using (SimProfiler.Measure("military experience")) MilitaryExperience.Tick(this);
        using (SimProfiler.Measure("tactical doctrines")) TacticalDoctrines.Tick(this);
        using (SimProfiler.Measure("strategic doctrines")) StrategicDoctrines.Tick(this);
        using (SimProfiler.Measure("construction")) ConstructionSystem.Tick(this);
        yield return 0;

        // And these happen on a day rather than during one. An event either fires today or
        // it does not; the economy clears on the week; history takes one sample. Running
        // them once for each whole day the step crossed is what keeps the answer the same
        // whether the campaign is stepping six hours at a time or three days.
        for (var day = 0; day < daysCrossed; day++)
        {
            // =====================================================================================
            // THE DAY, IN ORDER - read this before adding anything below.
            //
            // Sixty passes run here, every whole day the step crosses. They are grouped by what
            // they read, and the grouping is the reason for the order:
            //
            //   1. A country's own numbers ..... recruitment, contractors, stockpiles, support,
            //                                    militia, modifiers
            //   2. What people think ........... social memory, opinion
            //   3. Treaties and courts ......... alliances, guarantees and agreements, gestures,
            //                                    land arrangements, treasury dynamics
            //   4. Our hand abroad ............. parallel administration, internal migration
            //   5. Sanity ...................... numeric integrity (reports; never repairs)
            //   6. What the war costs .......... war damage, war weariness, civil-war reserves,
            //                                    crisis ladder, DEFCON
            //   7. Power inside ................ factions, defections, desertion, coups
            //   8. The world's furniture ....... proxy wars, organizations and their life, deals,
            //                                    ultimatums, foreign assistance
            //   9. Movements and our kin ....... movement methods, kin statecraft
            //  10. Long arcs ................... research, shipbuilding, statecraft, subjects,
            //                                    consolidation, culture union, corridors, world war,
            //                                    intel, covert operations, addresses
            //  11. Record and story ............ historical timeline, events, history sample
            //  12. The map changes ............. secession, union, demography, extinction, resistance,
            //                                    civil-war outbreak, names, governance, rulers,
            //                                    crises, mood, promises, goals, identity, naming,
            //                                    colonisation, submission, occupations, lost lands,
            //                                    capital
            //
            // Three sentences for why: numbers before opinions, opinions before treaties, treaties
            // before the map; a war's cost is counted before anything is decided about it; and the
            // map is redrawn last, because every pass above was worked out on the map that is still
            // standing.
            //
            // ADDING SOMETHING? Put it in the group whose state it reads, and put it here only if it
            // is a daily decision - the passes before this loop scale with the hours in the step and
            // must not. A pass that writes anything must also reach the save (SaveSystem) and, when
            // it changes what the world *is*, the fingerprint (CampaignFingerprint); otherwise a
            // loaded campaign and the bench disagree and nothing says so.
            //
            // A phase that is heavy belongs behind a day-cycle gate (`MovementRules.OnItsDay`) or
            // in stages (`DemographicCycle.Stages`), never dropped in raw: at the hourly step the
            // profiler shows a single system eating the frame, and this list is where a reader looks
            // first.
            // =====================================================================================
            using (SimProfiler.Measure("recruitment")) RecruitmentSystem.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("contractors")) Contractors.Tick(this);
            using (SimProfiler.Measure("stockpiles")) StrategicStockpiles.TickDay(this);
            yield return 0;
            SupportCore.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("militia")) MilitiaCore.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("modifiers")) ModifierSystem.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("social memory")) SocialMemory.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("opinion")) OpinionSystem.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("alliances")) AllianceMaintenance.Tick(this);
            // Offers and requests that nobody answered stop occupying the tie. Both are state
            // that travels in a save, so both have to expire somewhere.
            using (SimProfiler.Measure("diplomatic_offers")) { Guarantee.Tick(this); AgreementSystem.Tick(this); }
            // Visits, shows of force, mediation and loans (DiplomaticGestures): the instalments weekly, the computer's choices monthly.
            using (SimProfiler.Measure("diplomatic_gestures")) { DiplomaticGestures.Tick(this); DiplomaticGestures.AiTick(this); }
            // Rents, terms, the troops that stay and the owners who answer them (LandArrangements).
            using (SimProfiler.Measure("land_arrangements")) LandArrangementSystem.Tick(this);
            // Why money does not pile up: erosion, the pressure to spend, the credit cycle, the bills nobody budgets for.
            using (SimProfiler.Measure("treasury_dynamics")) TreasuryDynamics.Tick(this);
            // Somebody else's paperwork inside somebody else's region: what it takes, what it
            // costs, and whether the host has noticed (ParallelAdministration).
            using (SimProfiler.Measure("parallel administration")) ParallelAdministration.Tick(this);
            // Votes under the troops in regions held a year or more (HeldRegions).
            HeldRegions.AiMonth(this);
            // A government relieves the commander who keeps losing (Generals).
            Generals.AiMonth(this);
            yield return 0;
            foreach (var _ in EconomySystem.TickStages(this)) yield return 0;
            // In stages, like the economy before it (BrainDrain.Stages).
            using (var drain = BrainDrain.Stages(this).GetEnumerator())
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("brain drain")) more = drain.MoveNext();
                    yield return 0;
                    if (!more) break;
                }
            // And inside each country: from the regions that are worse off to the ones that are
            // better, and away from the fighting, carrying who they are.
            using (SimProfiler.Measure("internal migration")) InternalMigration.Tick(this);
            yield return 0;
            // Before political actions read thresholds: comparisons with NaN can otherwise
            // authorise every uprising. Do not let one bad economy contaminate the map.
            string invalid = null;
            if (ElapsedDays % 7 == 0)
                using (SimProfiler.Measure("integrity")) invalid = NumericIntegrity.FirstLiveIssue(this);
            if (invalid is not null)
            {
                BlockSimulation(invalid);
                throw new CampaignIntegrityException(invalid);
            }
            yield return 0;
            using (SimProfiler.Measure("war damage")) WarDamage.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("war weariness")) WarWeariness.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("civil war reserves")) CivilWarPersistence.Tick(this);
            yield return 0;
            foreach (var _ in Escalation.TickStages(this)) yield return 0;
            using (SimProfiler.Measure("crisis ladder")) CrisisLadder.Tick(this);
            // The world's nuclear alert and the holders' own postures (DefconSystem, NuclearPosture).
            using (SimProfiler.Measure("defcon system")) DefconSystem.Tick(this);
            using (SimProfiler.Measure("factions")) Factions.Tick(this);
            yield return 0;
            // A formation that has been cut off or is standing among its own people can
            // change sides. Monthly, deterministic, and only after the fighting has moved.
            using (SimProfiler.Measure("defections")) Defections.Tick(this);
            using (SimProfiler.Measure("desertion")) Desertion.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("coups")) Coups.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("proxy wars")) ProxyWars.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("organizations")) InternationalOrganizations.Tick(this);
            using (SimProfiler.Measure("organization life")) OrganizationDynamics.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("deal inbox")) DealNegotiations.Tick(this);
            using (SimProfiler.Measure("ultimatum desk")) Ultimatum.Tick(this);
            using (SimProfiler.Measure("foreign assistance")) ForeignAssistance.Tick(this);
            yield return 0;
            foreach (var _ in MovementRules.TickStages(this)) yield return 0;
            foreach (var _ in MovementAi.TickStages(this)) yield return 0;
            using (SimProfiler.Measure("movements"))
            using (SimProfiler.Measure("  mov:methods")) MovementMethods.Tick(this);
            yield return 0;
            // A country's own hand among its own people abroad (KinStatecraft): organisers and
            // volunteers across a border, monthly and quiet, where the demography gives it a
            // people to work. It happens after the movements have acted, because what it may do
            // about a rising is join one that already exists.
            using (SimProfiler.Measure("kin statecraft")) KinStatecraft.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("research")) ResearchSystem.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("shipbuilding")) Shipbuilding.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("diplomacy")) Statecraft.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("subjects")) SubjectSystem.Tick(this);
            yield return 0;
            foreach (var _ in AiStatecraft.TickStages(this)) yield return 0;
            using (SimProfiler.Measure("consolidation")) Consolidation.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("culture union")) CultureUnion.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("corridors")) Corridors.Tick(this);
            using (SimProfiler.Measure("border diplomacy")) BorderDiplomacy.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("world war")) WorldWar.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("intel networks")) IntelNetworks.Tick(this);
            using (SimProfiler.Measure("covert operations")) CovertOperations.Tick(this);
            using (SimProfiler.Measure("addresses")) AddressToNation.Tick(this);
            foreach (var _ in MovementPower.TickStages(this)) yield return 0;
            yield return 0;
            using (SimProfiler.Measure("historical events")) HistoricalTimeline.Tick(this);
            using (SimProfiler.Measure("events")) EventSystem.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("history")) History.Capture(this);
            yield return 0;

            // Whether any part of any country has finished deciding it is not part of it.
            // Last in the day, because it redraws the map the rest of the day was worked
            // out on; it keeps its own weekly schedule, because it also has to clear its
            // report list on the days it does nothing.
            using (SimProfiler.Measure("secession")) SecessionEvents.Tick(this);
        // A proclaimed state is tested once a day, not once: history does not stop at the moment a
        // country is proclaimed (UnionCohesion).
        using (SimProfiler.Measure("union")) UnionCohesion.Tick(this);
        // Age structure and the cultures that migration moves: the demographic cycle reads the births
        // and deaths the household review has just counted, and aggregates the country's shape so a
        // tooltip or a modifier source reads one number (DemographicCycle).
        // In stages, so the week's heaviest pass does not land in one frame (DemographicCycle.Stages).
        using (var demography = DemographicCycle.Stages(this).GetEnumerator())
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("demography")) more = demography.MoveNext();
                if (!more) break;
                yield return 0;
            }
        // A country that is running out of people is counted here, straight after the cycle that
        // counts them. What becomes of its empty ground is a later decision about inheritance,
        // deliberately not this line (DEMOGRAPHIC_EXTINCTION_DESIGN_RU.md).
        using (SimProfiler.Measure("extinction")) DemographicExtinction.Tick(this);
        // What holding somebody else's ground costs: once a week, an occupation nobody is
        // standing in slips (OccupationResistance, gated, off by default like extinction).
        using (SimProfiler.Measure("resistance")) OccupationResistance.Tick(this);
            yield return 0;
            // And whether any country has lost its government's grip on part of itself.
            using (SimProfiler.Measure("civil war outbreak")) CivilWarOutbreak.Tick(this);
            // And whether a state failing on several fronts at once is coming apart, its capital
            // overthrowing its government, or a piece of a state that came apart going home
            // (StateFailure). After the single outbreak, so the two read the same day's world.
            using (SimProfiler.Measure("state failure")) StateFailure.Tick(this);
            yield return 0;
            CountryNames.Remember(this);

            // And whether any country has stopped being what it was. After secession
            // because a state that has just lost a province is a state in exactly the
            // condition this reads.
            using (SimProfiler.Measure("governance")) GovernanceEvents.Tick(this);
            using (SimProfiler.Measure("rulers")) Rulers.Tick(this);
            yield return 0;
            using (SimProfiler.Measure("domestic crises")) DomesticCrises.Tick(this);
            yield return 0;
            // And whether the country still agrees with the government that holds it. Derived
            // from what the residents have and believe, never a bonus or penalty per tag.
            using (SimProfiler.Measure("political mood")) PoliticalMood.Tick(this);
            yield return 0;

            // And whether a promise made in public has come true or missed its date. Checked
            // continuously, so early fulfilment is credited the day it happens; a government
            // with no promises out costs nothing to ask.
            using (SimProfiler.Measure("government promises")) GovernmentPromises.Tick(this);
            yield return 0;
            // And whether a government the player does not run says something in public about
            // its own crisis: the same bet on the same terms, taken by everyone else.
            using (SimProfiler.Measure("ai promises")) AiPromises.Tick(this);
            yield return 0;

            // And what the player's own pinned goals say now that the day's facts are in.
            // Private bookkeeping: a goal met changes nothing in the world but the record.
            using (SimProfiler.Measure("strategic goals")) StrategicGoals.Tick(this);
            yield return 0;

            // And first sight of identities: a country that declares nothing yet declares
            // what it already is - its own plurality, silently and at nobody's cost.
            StateIdentity.EnsureDeclared(this);
            // And what a founded state has decided to call itself, once it has stood long
            // enough to have decided anything.
            NationNaming.Tick(this);
            yield return 0;

            // Settlers on land nobody owns, and whether anybody new sets out for it.
            ColonisationCore.Tick(this);

            // And whether any country nobody plays asks a smaller one to answer to it.
            SubmissionSystem.Tick(this);

            // Ground held by a country nobody is at war with goes home: whatever ended the
            // war today - a peace, a submission, a subjugation - the troops do not stay.
            // Ground held by arrangement first - a lease, troops in without a war - so the
            // occupations that go home below are only the ones nothing holds there.
            if (FeatureEnabled("land_arrangements")) using (SimProfiler.Measure("land arrangements")) LandArrangementSystem.Enforce(this);
            using (SimProfiler.Measure("occupations")) Occupations.Tick(this);
            yield return 0;

            // And whether a country played by somebody has ground it lost that it could take back.
            using (SimProfiler.Measure("lost lands")) LostLands.Tick(this);
            yield return 0;

            // And last, where each government now sits: a region ceded today takes its
            // capital with it, and the country moves to what it has left.
            CapitalCore.Tick(this);
        }
        CampaignIntel.Invalidate(this);
        if (DiagnosticLogs.Enabled)
            DiagnosticLogs.Write("simulation", $"DAY_END date={Date} player={Player?.Tag} divisions={Armies.Count} fleets={Fleets.Count} wars={Wars.Count(w => !w.Ended)} battles={Battles.Count(b => !b.Ended)} training={Recruitment.Count} construction={Construction.Count} treasury={DiagnosticLogs.N(Player?.Treasury ?? 0)} gdp={DiagnosticLogs.N(Player?.Gdp ?? 0)} map_revision={MapRevision}");

        // The interface redraws what changed when it next draws (FlushInterfaceChanges), not
        // once a step. At the hourly step a frame at top speed runs several steps, and every
        // one of them redrew the whole interface - the top bar, the side panel, two hundred
        // and sixty names on the map - to show a state the player saw for no frame at all.
        //
        // What cannot wait for the frame is whatever stops the clock: an event or an offer
        // put to a player is answered by the interface pausing, and the step after it would
        // be a step too many.
        if (SomethingAwaitsAPlayer()) Changed?.Invoke();
        else InterfaceDirty = true;
    }

    private bool SomethingAwaitsAPlayer()
    {
        if (HumanSeats.Count == 0) return false;
        foreach (var pending in PendingEvents)
            if (HumanSeats.Contains(pending.NationId)) return true;
        // A peace offer no longer stops the clock: it arrives as a notification the player
        // opens when they choose (PeaceOfferUI), so it waits for the frame like anything else.
        // Counting it here redrew the whole interface every step for the sixty days it stood.
        return false;
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
        // Sitting down in front of a country is the moment its books have to make sense.
        // Every path arrives here - a new campaign, a hot seat handed over, a save just
        // restored - and the weekly tick that writes these figures does not run on day zero
        // and does not survive a save, which is the whole of why the economy screen was
        // sometimes blank.
        EconomySystem.Explain(this);
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
        // Only for the full 2024 map: synthetic test fixtures build tiny topologies (e.g. 16x8)
        // while keeping the default ScenarioId, and applying 2024's province IDs and lat/lon
        // point entries to a 1-province test grid overwrites the test's own province owner.
        ProvinceController = (ushort[])topology.ProvinceToNation.Clone();
        var politicalOverrides = ScenarioId == "2024" && topology.ProvinceCount > 1000
            ? ProvinceOwnership.Apply(this, topology, ProvinceController) : 0;
        if (politicalOverrides > 0) PoliticalOverridesApplied = politicalOverrides;
        // Water is nobody's, which the file already says by naming none of it - but the raster
        // still painted an owner under sixty-seven sea provinces off the South Orkneys, and it
        // was Iran's.
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToNation[province] == 0 || !topology.IsWater(province)) continue;
            topology.ProvinceToNation[province] = 0;
            if (province < ProvinceController.Length) ProvinceController[province] = 0;
        }
        // And where history says nobody owns the land, nobody does - whatever the raster
        // painted there. Before the region list is reconciled with the ground below, which
        // would otherwise hand the region straight back to the raster's owner.
        UnclaimedLand.ApplyToGround(this, topology, ProvinceController);
        // And where history says somebody else's troops hold a region, they do (Gaza).
        HeldGround.ApplyToGround(this, topology, ProvinceController);
        // And ground held by arrangement - Baikonur, Guantanamo - from the first look, not the first day.
        LandArrangementSystem.LayOnGround(this, topology, ProvinceController);
        ProvinceOccupation = new float[topology.ProvinceCount + 1];

        // A fleet's home port is a province, and provinces only exist once the map is
        // attached. Until then a fleet knows where it is but not what it belongs to.
        FleetHistory.AssignHomePorts(this, keepSaved: _pendingProvinceController is not null);
        // And every state's church and people from the first look: the State Affairs screen of
        // 1 January showed nothing declared until the first day had ticked, as if each
        // country made its mind up on the morning of the 2nd. A new campaign only - a save
        // carries its own declarations, and an empty one there is what was saved.
        if (_pendingProvinceController is null) StateIdentity.EnsureDeclared(this, strain: false);
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
        foreach (var province in DestroyedProvinces)
            if (province > 0 && province <= topology.ProvinceCount)
            {
                topology.DestroyedByProvince[province] = true;
                topology.ProvinceToNation[province] = 0;
                ProvinceController[province] = 0;
            }

        // Now that the map knows who owns what, make the region list agree with it. Both
        // passes are no-ops on a world that is already consistent, which is what makes them
        // safe to run on a loaded save as well as a new campaign: a save that was written
        // before regions could be split heals here instead of staying wrong for ever.
        StatePartition.RepartitionWorld(this);
        StatePartition.Reunify(this);

        if (savedTopologyMatches && RepairDiminishedRestorationsAfterAttach)
            FormableRestorations.RepairDiminishedIdentities(this);
        RepairDiminishedRestorationsAfterAttach = false;

        // Where each capital actually stands, and what that makes of the country. Both need
        // the map: a capital is a place on the globe rather than a region a scenario names,
        // and the regions under it can be redrawn by a war.
        LocateCapitals(preserveExisting: true);
        // Only ever from the authored world. Whether that has happened is read from the
        // world rather than remembered in a field, because a multiplayer client adopts a
        // campaign it did not create: a flag in memory says "not yet" about a world that was
        // calibrated on the host an hour ago, and recalibrating a world at war writes the
        // war into every country's institutions.
        // The de facto powers of 2024 - the Houthis, the Rapid Support Forces, Idlib - cut out of
        // the states they hold ground of, before stability is calibrated so their institutions
        // are measured from the world they open in. A new world only (Factions).
        Factions.Found(this);
        // Initial homeland must exist BEFORE calibrating imperial overstretch;
        // learning it afterwards would change a country's target on day zero.
        // Never re-baseline a restored or network-adopted world's territorial memory.
        if (!InitialDeploymentCompleted) MovementFall.RememberHomelands(this);
        if (!StabilityCore.IsCalibrated(this)) StabilityCore.Calibrate(this);

        // The countries without land: founded on a new world once its regions exist, and put
        // back where their headquarters stand on a loaded one (Movements).
        Movements.Found(this);
        Movements.Place(this);
        // A save written before the permanent record existed still knows a live claim and, for a
        // faction, its parent in the data. Adopt it here so an old campaign gains the memory
        // instead of falling back to reading tag prefixes for the rest of its life.
        Reintegration.AdoptLegacyLineage(this);
        if (ElapsedDays == 0 && FeatureEnabled("national_courses"))
            foreach (var nation in Nations) NationalCourses.Initialize(this, nation);
        // Who leads: a person per chair, rolled once at the world's first morning.
        Rulers.Found(this);

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
            // The fronts first, and this order is the whole of it.
            //
            // The planner posts most of an army to its front when there is a war with a
            // front to post it to, and 94% of it for the two countries this scenario is
            // about. It asked one line before the fronts were built, so what it saw was
            // `front=0` for both of them - measured, after two years of the opening weeks
            // looking like a collapse - and both armies fell through to the peacetime
            // branch, which spreads them along every border the country has. For Russia
            // that is two hundred and thirty-seven provinces from Finland to the Pacific,
            // and for Ukraine it is the Polish border as much as the Donbas.
            //
            // Rebuilding a front reads province control and nothing else, so it is safe
            // this side of the deployment and was only ever on the wrong side of it.
            Warfare.RebuildFronts(this);
            StrategicDeploymentPlanner.Deploy(this);
            InitialDeploymentCompleted = true;
            FrontPlanner.AssignAi(this);
            // The long-range forces the world opens with: the artillery that exists, the
            // air wings that exist, before anybody has built anything. A loaded save has
            // its own - this block runs for a new world only.
            SupportCore.EnsureInitial(this);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Find the region each capital stands in. Nearest province of the country's own ground
    /// to the capital's coordinates, which is also how the deployment planner finds it -
    /// a capital that has been overrun still points at the region it is in.
    /// </summary>
    public void LocateCapitals(bool preserveExisting = false)
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
            // A loaded capital is a real saved place, even if ceded or occupied. Rebinding
            // it to the nearest remaining province kept the old city's name forever.
            // Emergency/permanent relocation belongs to CapitalCore, not map attachment
            // (a network snapshot must remain identical until its next authoritative tick).
            if (preserveExisting && nation.CapitalProvinceId > 0 && nation.CapitalProvinceId <= topology.ProvinceCount)
            {
                nation.CapitalStateMapIndex = topology.ProvinceToState[nation.CapitalProvinceId];
                continue;
            }
            nation.CapitalStateMapIndex = 0;
            nation.CapitalProvinceId = 0;
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
            nation.CapitalProvinceId = best;
            // A capital's name is a city ("Port Sudan", "Seoul"), not a region, so a name that is
            // none of the country's region names is the authored seat, not a stale one. Only a
            // country with no capital name at all borrows the name of the region it sits in.
            var state = States.Find(s => s.MapIndex == nation.CapitalStateMapIndex);
            if (state is not null && !string.IsNullOrWhiteSpace(state.Name) && string.IsNullOrWhiteSpace(nation.CapitalName))
            {
                nation.CapitalName = state.Name;
            }
            // A name inherited with a fallen state's title (a faction proclaimed as Syria keeps
            // "Damascus") while the city itself is somebody else's: the seat is where it rules.
            else if (state is not null && !string.IsNullOrWhiteSpace(state.Name) && CapitalCore.SeatNameLost(this, nation))
            {
                nation.CapitalName = state.Name;
                var centre = topology.CenterOf(best);
                nation.CapitalLatitude = centre.Latitude;
                nation.CapitalLongitude = centre.Longitude;
            }
        }
        // A landless headquarters stands at its host's capital or its region's main city
        // (Movements.Place), so it moves when they do. Left until the next load it kept the old
        // spot, and a save read back a different headquarters from the one it wrote.
        Movements.Place(this);
    }

    internal ushort[] PendingProvinceController => _pendingProvinceController;
    private ushort[] _pendingProvinceOwner;
    private ushort[] _pendingProvinceState;
    // A disk-load migration request, not campaign state. A network snapshot never sets it.
    internal bool RepairDiminishedRestorationsAfterAttach;

    internal void RestoreRuntimeState(GameDate date, int elapsedDays, bool paused, uint randomState,
        int playerId, IReadOnlyList<int> humanSeats, int activeSeat, int nextBattleId,
        int mapRevision, bool initialDeploymentCompleted, ushort[] controllers, float[] occupation, ushort[] owners, ushort[] states,
        bool noHumanAuthority = false)
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
        if (noHumanAuthority) HumanSeats.Clear();
        else if (HumanSeats.Count == 0 && Nations.Count > 0) HumanSeats.Add(Math.Clamp(playerId, 0, Nations.Count - 1));
        ActiveSeat = Math.Clamp(activeSeat, 0, Math.Max(0, HumanSeats.Count - 1));
        foreach (var nation in Nations)
        {
            nation.IsHuman = HumanSeats.Contains(nation.Id);
            nation.IsPlayer = nation.Id == playerId;
        }
        Player = NationById(playerId) ??
            (HumanSeats.Count > 0 ? NationById(HumanSeats[ActiveSeat]) : null) ?? Nations.FirstOrDefault();
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
        // The map is authoritative, including annexation by deals and peace. Waiting
        // for a movement's staggered weekly update can make a landed country look exiled.
        if (ProvinceMap is not null)
            foreach (var n in Nations)
                if (n.Movement is not null) MovementRules.SynchroniseTerritory(this, n);
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
        // Initial civil-war factions and later emergent states are added after the
        // bookmark's financial migration. They need the same books immediately.
        SovereignFinance.NormalizeAccounts(this, nation);
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

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        campaign.SetMilitarySpending(IssuerId, Value);
    }
}

public sealed class SetTaxRateCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public float Value { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        campaign.SetTaxRate(IssuerId, Value);
    }
}

public sealed class SetTradePolicyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public TradePolicy Policy { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        campaign.SetTradePolicy(IssuerId, Policy);
    }
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

    /// <summary>
    /// Forget every measurement.
    ///
    /// For a harness that wants to count one run's passes rather than a session's: how often
    /// a system ran is only an answer if something said when to start counting.
    /// </summary>
    public static void Reset() => Samples.Clear();

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
