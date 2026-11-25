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

    /// <summary>Astronomical year, shared with HistoricalDate: 0 = 1 BCE, -299 = 300 BCE.</summary>
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
    public override string ToString() => $"{Day} {MonthNames[Month - 1]} " +
        (Year <= 0 ? $"{1L - Year} BCE" : Year.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayYear => Year <= 0 ? Loc.Format("{0} BCE", 1L - Year) :
        Year.ToString(System.Globalization.CultureInfo.InvariantCulture);

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
        ? $"{Day} {Loc.Get(MonthNames[Month - 1])} {DisplayYear}"
        : $"{Day} {Loc.Get(MonthNames[Month - 1])} {DisplayYear}, {Hour:00}:00";

    /// <summary>Format this date using an optional scenario <see cref="CalendarProfile"/>; falls back to Gregorian when null/default.</summary>
    public string Display(CalendarProfile calendar, bool withTime = false) =>
        calendar is null || calendar.IsDefault ? Display(withTime) : calendar.FormatDate(this, withTime, localized: true);
}

/// <summary>Commands are the only legal way UI and AI mutate the campaign.</summary>
public interface ICampaignCommand
{
    int IssuerId { get; }
    void Apply(Campaign campaign);
}

public sealed class CampaignCommandQueue
{
    private readonly object _queueLock = new();
    private readonly Queue<ICampaignCommand> _pending = new();
    private readonly Campaign _campaign;

    public CampaignCommandQueue(Campaign campaign = null) => _campaign = campaign;
    /// <summary>Return true to hand authority to an external transport and suppress local application.</summary>
    public Func<ICampaignCommand, bool> SubmitInterceptor;

    public int Count
    {
        get { lock (_queueLock) return _pending.Count; }
    }

    /// <summary>Throw away every order not yet carried out: a frozen world carries out none.</summary>
    internal void Discard()
    {
        lock (_queueLock) _pending.Clear();
    }

    public void Enqueue(ICampaignCommand command)
    {
        if (command is null) return;
        // Complete the world before a transport stamps/broadcasts an external order.
        // During a simulation stage or background step this is already protected by step-boundary ownership.
        if (_campaign is not null && (!_campaign.DeferMainThreadNotifications || _campaign.AccessGate.IsWriterHeldByCurrentThread))
            _campaign.FinishStep();
        if (SubmitInterceptor?.Invoke(command) == true) return;
        lock (_queueLock) _pending.Enqueue(command);
    }

    public void ApplyAll(Campaign campaign)
    {
        var needWriteLock = campaign is not null && campaign.DeferMainThreadNotifications && !campaign.AccessGate.IsWriterHeldByCurrentThread;
        if (needWriteLock) campaign.AccessGate.EnterWrite();
        var prevDefer = campaign?.DeferMainThreadNotifications ?? false;
        if (needWriteLock) campaign!.DeferMainThreadNotifications = false;
        try
        {
            campaign.FinishStep();
            while (true)
            {
                ICampaignCommand command;
                lock (_queueLock)
                {
                    if (_pending.Count == 0) break;
                    command = _pending.Dequeue();
                }
                CrashEnvelope.Record(campaign, command);
                // Несмотря на имя, TraceCommand не только пишет лог: он вызывает Apply
                // ровно один раз и при выключенной диагностике тоже. Повторный Apply здесь
                // удвоит цену/набор войск. Граница записи и трассировки должна остаться общей.
                DiagnosticLogs.TraceCommand(campaign, command);
                Controls.BatchActionUndo.OnCommandApplied(campaign, command);
            }
        }
        finally
        {
            if (needWriteLock)
            {
                campaign!.DeferMainThreadNotifications = prevDefer;
                campaign.AccessGate.ExitWrite();
            }
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
    // Political labels share the current official identity with diplomacy and the
    // country dossier, including regime/formable/custom names and localisation.
    // A separate short .map row must not silently erase "Islamic Republic of".
    public string MapName => DisplayName;

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
    /// <summary>
    /// The id the next army group is founded under, once one has been struck off (Generals.NewGroupId).
    /// 0 until then: an id is the highest standing one plus one, which is all a world that only ever
    /// adds groups needs. Kept because a group that is struck off can be the one with the highest id,
    /// and its id handed out again would give the new group the old one's first commander (the draw
    /// is seeded by the id) and pass it any order still queued for the old one.
    /// </summary>
    public int NextArmyGroupId;
    public readonly List<Fleet> Fleets = new();
    public readonly List<StrategicWar> Wars = new();

    /// <summary>Bumped whenever a war is added, ended or reshaped. <see cref="Campaign.ActiveWars"/>
    /// and <see cref="Warfare.LiveWars"/> rebuild from this stamp, not on a quiet day.</summary>
    public int WarRevision { get; private set; }

    /// <summary>Call at every war-list or war-state change a war-cache reader must see.</summary>
    public void MarkWarsChanged() => WarRevision++;

    [System.Text.Json.Serialization.JsonIgnore]
    internal object _liveWarBoard;

    [System.Text.Json.Serialization.JsonIgnore]
    internal int _repatriatedWarPrefix;

    [System.Text.Json.Serialization.JsonIgnore]
    internal int _repatriatedWarCount = -1;

    [System.Text.Json.Serialization.JsonIgnore]
    internal StrategicWar _repatriatedWarHead;

    [System.Text.Json.Serialization.JsonIgnore]
    internal StrategicWar _repatriatedWarTail;

    [System.Text.Json.Serialization.JsonIgnore]
    internal int _trimmedWarPrefix;

    [System.Text.Json.Serialization.JsonIgnore]
    internal int _trimmedWarCount = -1;

    [System.Text.Json.Serialization.JsonIgnore]
    internal StrategicWar _trimmedWarHead;

    [System.Text.Json.Serialization.JsonIgnore]
    internal StrategicWar _trimmedWarTail;

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
            var nations = ActiveNations;
            for (var i = 0; i < nations.Count; i++) if (nations[i].IsHuman) return false;
            return true;
        }
    }

    /// <summary>
    /// True only when a human player is actively controlling <paramref name="nation"/>.
    /// Returns <c>false</c> while <see cref="ObserverMode"/> is active so former human seats
    /// are piloted normally by AI instead of remaining paralyzed with idle armies and deadlocked diplomacy.
    /// </summary>
    public bool IsHumanControlled(Nation nation) => !ObserverMode && nation is { IsHuman: true };
    public readonly Dictionary<long, DiplomaticRelation> Relations = new(PairKeyComparer.Instance);
    public readonly List<int> HumanSeats = new();
    public readonly CampaignCommandQueue Commands;
    public readonly CampaignHistory History = new();
    public readonly SimRandom Random;
    public readonly Defines Defines = new();

    public int Seed { get; }
    public GameDate Date;
    /// <summary>Optional scenario display calendar (era name, custom months, year offset). Null for default 2024 Gregorian.</summary>
    public CalendarProfile Calendar;
    /// <summary>Optional declarative scenario metadata (era/theme/music/featured countries). Null for default 2024.</summary>
    public ScenarioMetadata ScenarioMeta;
    /// <summary>Era features started in the 'restricted' state (disabled until unlocked by tech/event). Null when none.</summary>
    public HashSet<string> RestrictedFeatures;
    public string ScenarioId = "2024";
    /// <summary>The era overlay the scenario chose (SkinOverlays); empty for the base look. Saved, never simulated.</summary>
    public string SkinOverlay = "";
    public Dictionary<string, int[]> ScenarioTerritories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Display date formatted through the active scenario's <see cref="Calendar"/> (or default Gregorian).</summary>
    public string DisplayDate(bool withTime = false) => Date.Display(Calendar, withTime);
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
    public bool StepOpensNewDay { get; internal set; } = true;

    /// <summary>
    /// How much of a day one step covers. Every rate written "per day" is multiplied by
    /// this; at the default of 24 hours it is 1 and nothing changes.
    /// </summary>
    public float TickScale => TickHours / 24f;
    public bool IsPaused { get; set; } = true;
    public int Speed { get; set; } = 1;
    public Nation Player { get; private set; }
    public int ActiveSeat { get; private set; }
    public bool IsHotseat => HumanSeats.Count > 1;
    public ProvinceTopology ProvinceMap { get; private set; }

    /// <summary>How many provinces the political map file decided, rather than the raster.</summary>
    public int PoliticalOverridesApplied { get; private set; }
    public ushort[] ProvinceController { get; private set; }
    public float[] ProvinceOccupation { get; private set; }
    public int MapRevision { get; private set; }
    /// <summary>
    /// Bumped when province-to-state or province-to-nation content changes (annexation,
    /// colonisation, repartition, extinction, scorched earth) - never when a controller
    /// merely takes a province in battle. MapRevision fires on both; caches that read
    /// only the ownership arrays key on this instead and survive a war's daily captures.
    /// </summary>
    internal int OwnershipRevision { get; private set; }
    public int LastTacticalDoctrineDay = -1;
    internal int LastTacticalCourseDay = -1;
    public int LastStrategicDoctrineDay = -1;
    internal readonly MovementRules.MovementPeopleCache MovementPeople = new();
    /// <summary>
    /// Road-speed table <see cref="MovementPace"/> rebuilds once per campaign day. Derived
    /// state, never saved: it is a function of the region building counts, and holding it on
    /// the campaign is what keeps two worlds ticking in one process from reading each
    /// other's roads - a shared static table refreshed on every switch re-read buildings
    /// mid-day and marched the same army at two different paces.
    /// </summary>
    internal float[] PaceRoads;
    internal int PaceRoadsDay = -1;
    /// <summary>
    /// Who borders whom, rebuilt at most once a day by <see cref="CampaignDiplomacy"/>.
    /// Per-campaign for the same reason as <see cref="PaceRoads"/>: a shared table stamped
    /// with the last asker let a second world read borders rebuilt at a different hour of
    /// its own day than the solo run would have used.
    /// </summary>
    internal Dictionary<int, HashSet<int>> BorderSets;
    internal int BorderDay = -1;
    internal int BorderRevision = -1;
    internal long BorderForgetStamp = -1;
    internal int LastReunifyMapRevision = -1;
    internal int LastReunifyStateCount = -1;
    // Derived, per-world: papers/offices can close without moving a border. Remember
    // only same-owner pairs refused last time so their eligibility can wake the cache.
    internal readonly List<(ScenarioState Survivor, ScenarioState Piece)> ReunifyBlockedPairs = new();
    /// <summary>Per-world occupation tally for <see cref="ControlTally"/>; derived, never saved.</summary>
    internal ControlTally.TallyState ControlTallyTables;
    /// <summary>Per-world daily AI tables for <see cref="AiStatecraft"/>; derived, never saved.</summary>
    internal AiStatecraft.DailyTables AiStatecraftTables;
    public bool InitialDeploymentCompleted { get; private set; }
    internal int NextBattleId { get; set; }
    private ushort[] _pendingProvinceController;
    private float[] _pendingProvinceOccupation;

    [System.Text.Json.Serialization.JsonIgnore]
    internal readonly CampaignAccessGate AccessGate = new();
    [System.Text.Json.Serialization.JsonIgnore]
    private CampaignRenderSnapshot _renderSnapshot;
    [System.Text.Json.Serialization.JsonIgnore]
    internal volatile bool DeferMainThreadNotifications;
    [System.Text.Json.Serialization.JsonIgnore]
    internal int SimulationTickClockSeq;

    public CampaignRenderSnapshot RenderSnapshot =>
        System.Threading.Volatile.Read(ref _renderSnapshot) ?? CampaignRenderSnapshot.Empty;

    public bool TryEnterRead(int timeoutMs = 0, bool requestWindowOnMiss = true) =>
        AccessGate.TryEnterRead(timeoutMs, requestWindowOnMiss);


    public void ExitRead() => AccessGate.ExitRead();

    public bool IsSimBusy => AccessGate.IsWriterActive;

    internal void PublishRenderSnapshot(CampaignRenderSnapshot snapshot) =>
        System.Threading.Volatile.Write(ref _renderSnapshot, snapshot);

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

    /// <summary>
    /// Builds the current-world base scenario from the source catalogue. The default new game is
    /// the 2024 scenario started without its picker, so it wears the look that scenario's manifest
    /// names - a mod's override of the manifest included, found in <paramref name="scenarioRoots"/>
    /// the way the scenario picker finds it - and not the base look by omission.
    /// </summary>
    public static Campaign CreateBootstrap(int seed, IEnumerable<string> scenarioRoots = null)
    {
        var campaign = ScenarioCatalog.CreateBaseCampaign(seed);
        campaign.SkinOverlay = ScenarioLibrary.SkinOf(campaign.ScenarioId, scenarioRoots);
        return campaign;
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
