using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>The whole diet of a long campaign (LongCampaign). Off: nothing is retired, pruned or trimmed.</summary>
    public bool LongCampaignEnabled { get; set; } = true;

    /// <summary>How long a country must have been gone before it is retired, in days. Zero or less: never.</summary>
    public int RetireDeadAfterDays { get; set; } = 365;

    /// <summary>How often the sweep runs, in days; rounded down to whole weeks, never under one.</summary>
    public int LongCampaignSweepDays { get; set; } = 28;

    /// <summary>Whether a sleeping relation between two landless sides that carries nothing is dropped.</summary>
    public bool PruneLandlessRelations { get; set; } = true;

    /// <summary>How long after its peace a war keeps its province-by-province record. Zero or less: for ever.</summary>
    public int EndedWarDetailDays { get; set; } = 364;

    /// <summary>How often a country without ground gets a ledger sample, in days (a landed one: every week). Seven or less: weekly, as before.</summary>
    public int HistoryLandlessSampleDays { get; set; } = 28;

    /// <summary>A ledger keeps every weekly sample this many days back (CampaignHistory.Bands).</summary>
    public int HistoryWeeklyDays { get; set; } = CampaignHistory.WeeklyDays;

    /// <summary>Beyond the weekly stretch and this many days back: one sample of each four weeks.</summary>
    public int HistoryMonthlyDays { get; set; } = CampaignHistory.MonthlyDays;

    /// <summary>Beyond that and this many days back: one sample of each HistoryOldSampleDays. Older samples go.</summary>
    public int HistoryKeptDays { get; set; } = CampaignHistory.KeptDays;

    /// <summary>The spacing of the oldest stretch, in days: 91 is a quarter, 364 a year. Whole weeks, never under 28.</summary>
    public int HistoryOldSampleDays { get; set; } = CampaignHistory.OldSampleDays;
}

public sealed partial class Nation
{
    /// <summary>
    /// The day this country was retired (LongCampaign), or -1 while it is in the world. A retired
    /// country keeps who it was - id, tag, names, colour, capital, peoples, what it knew - and none
    /// of what it was doing. Saved, so a load finds it as it was left.
    /// </summary>
    public int RetiredDay = -1;

    /// <summary>The day it was first found gone with nothing left alive about it, or -1. The retirement clock.</summary>
    public int GoneSinceDay = -1;
}

public sealed partial class Campaign
{
    private volatile Nation[] _activeNations = Array.Empty<Nation>();
    private int _activeCount = -1, _activeOwnership = int.MinValue, _activeMovement = int.MinValue,
        _activeFallen = -1, _activeRetirement = -1;

    /// <summary>Bumped whenever a country is retired or comes back (LongCampaign).</summary>
    internal int RetirementRevision;

    /// <summary>Countries woken since the end of the last day, waiting for a market (LongCampaign.GiveQuotes). Written under the list's lock.</summary>
    internal readonly List<int> WokenAwaitingQuotes = new();

    private int _retiredNations;

    /// <summary>How many countries are retired now.</summary>
    public int RetiredNations { get { _ = ActiveNations; return _retiredNations; } }

    /// <summary>
    /// Every country a pass has any business visiting: all of them except the retired.
    ///
    /// <see cref="Nations"/> cannot lose a row - a country's id is its place in that list, and wars,
    /// chronicle lines and saves a century old point at it - so the dead stay there as short records
    /// and a pass that should not meet them walks this instead. The array is replaced, never
    /// rewritten: a pass that brings a country back while walking it goes on over the list it began.
    ///
    /// A retired country that has ground, people, a living movement or a fall undone is awake
    /// before this returns, whoever revived it and however: the check is the borders', the
    /// movements' and the fallen list's own revision counters, so no revival path has to
    /// remember to say so.
    /// </summary>
    public IReadOnlyList<Nation> ActiveNations
    {
        get
        {
            if (_activeCount == Nations.Count && _activeOwnership == OwnershipRevision &&
                _activeMovement == MovementRevision && _activeFallen == Fallen.Count &&
                _activeRetirement == RetirementRevision)
                return _activeNations;
            RefreshActiveNations();
            return _activeNations;
        }
    }

    /// <summary>
    /// Whether a pass should step over this country: retired, and still retired now. The second
    /// half is the same check <see cref="ActiveNations"/> makes, so a country brought back
    /// earlier in the day is met by the very next pass and not at the next sweep. Costs a living
    /// country one comparison.
    /// </summary>
    public bool IsRetired(Nation nation)
    {
        if (nation is null || nation.RetiredDay < 0) return false;
        _ = ActiveNations;
        return nation.RetiredDay >= 0;
    }

    private readonly object _activeGate = new();

    /// <summary>
    /// The list again, from the countries as they stand. Under a lock: a pass that asks
    /// <see cref="IsRetired"/> from the worker threads of one stage may be the first to notice a
    /// border has moved, and two of them waking the same country at once would each be half done.
    /// Whoever comes second finds the list already current and builds the same one - the answer
    /// is read off the campaign, so it does not depend on who asked first.
    /// </summary>
    internal void RefreshActiveNations()
    {
        lock (_activeGate) RefreshActiveNationsLocked();
    }

    /// <summary>
    /// Once a day (LongCampaign.Sweep): is any retired country a country again? The revision
    /// counters above catch a border, a movement and a change in how many have fallen - but a fall
    /// undone in the same breath as another country falling leaves that count as it was. This
    /// asks the records themselves, so nothing waits longer than a day; seven hundred records
    /// are seven hundred comparisons.
    /// </summary>
    internal void WakeTheReturned()
    {
        _ = ActiveNations;
        if (_retiredNations == 0) return;
        for (var id = 0; id < Nations.Count; id++)
        {
            var nation = Nations[id];
            if (nation.RetiredDay < 0 || !LongCampaign.Revived(this, nation)) continue;
            RefreshActiveNations();
            return;
        }
    }

    private void RefreshActiveNationsLocked()
    {
        var retired = 0;
        for (var id = 0; id < Nations.Count; id++)
        {
            var nation = Nations[id];
            if (nation.RetiredDay < 0) continue;
            if (LongCampaign.Revived(this, nation)) LongCampaign.Wake(this, nation);
            else retired++;
        }
        var active = retired == 0 && _activeNations.Length == Nations.Count ? _activeNations : new Nation[Nations.Count - retired];
        var at = 0;
        for (var id = 0; id < Nations.Count; id++)
            if (Nations[id].RetiredDay < 0) active[at++] = Nations[id];
        _activeNations = active;
        _retiredNations = retired;
        _activeCount = Nations.Count;
        _activeOwnership = OwnershipRevision;
        _activeMovement = MovementRevision;
        _activeFallen = Fallen.Count;
        _activeRetirement = RetirementRevision;
    }
}

/// <summary>
/// What a campaign of centuries may not carry: the dead.
///
/// A country that lost its last ground was never unmade. A save from 2131, 107 years in, held
/// 1,469 countries of which 360 had land; 786 had no land, no people and no living movement, and
/// every one of them still had a market, four shelves of doctrine, research half done, a ledger,
/// fleets nobody manned (60), army groups with no armies (525), training and building orders that
/// would never finish (181 and 32), seats in organisations (53) - and a relation with most of the
/// world: 142,703 of the 244,841 pairs in the file had a dead side. Their weekly ledger samples
/// were 20 MB of a 201 MB save. None of it is read by anything a living country does, and all of
/// it grows with every year played.
///
/// So a country that has been gone for RetireDeadAfterDays, with nothing left alive about it, is
/// retired: it stays in <see cref="Campaign.Nations"/> - its id is its place there, and wars,
/// chronicle lines and old saves point at it - as a short record of who it was, and everything it
/// was doing goes. "Who it was" is what bringing a country back reads: tag, id, names, colour,
/// capital, the peoples it is for, its homeland (<see cref="Campaign.Homelands"/>, untouched),
/// its form of government, its laws, what it knew (technologies). A restoration, a release or a
/// rising under the old tag finds the same country and it starts its books again the way a newly
/// founded one does; <see cref="Campaign.ActiveNations"/> has it back before the next pass reads.
///
/// This CHANGES THE WORLD, and not only for the dead (RetireDeadAfterDays, 0 = never). What the
/// living notice: an organisation loses the seats of its retired members, so its vote counts
/// and thresholds change, and one with nobody alive left is wound up; the living forget what
/// they thought of a retired country (PublicImages) and their sleeping relations with it, so a
/// grievance or an opinion read from that relation reads as a new relation's; a relation still
/// saying "at war" with a country in no war is set to peace; the fleets struck no longer sail in
/// the naval pass, which moves the order of the random draws after them. A returned country
/// remembers its name, not its grudges. None of this happens before a country has been gone
/// RetireDeadAfterDays, so a campaign younger than that is the same world to the bit; after
/// that it is deterministic and keeps people and money exactly - the record keeps its treasury
/// and its debt as they stood, and a wound-up organisation's fund goes into its members'
/// treasuries. What it moves on a real save is measured in
/// docs/PERFORMANCE_LONG_CAMPAIGN_2026_11_16_RU.md.
/// </summary>
public static class LongCampaign
{
    /// <summary>What the last sweep did, for the bench and the tests.</summary>
    public sealed class Report
    {
        public int Gone, Busy, Waiting, Retired, Woken, RelationsDropped, EmptyRelationsDropped, ImagesDropped, HistorySeriesDropped,
            FleetsStruck, HullsStruck, ArmyGroupsStruck, OrdersStruck, ProjectsStruck, SeatsVacated, WarsTrimmed,
            StaleWarsClosed;
        public override string ToString() =>
            $"gone {Gone} (busy {Busy}, not yet due {Waiting}), retired {Retired}, woken {Woken}, relations dropped {RelationsDropped} (+{EmptyRelationsDropped} empty), " +
            $"images {ImagesDropped}, history series {HistorySeriesDropped}, fleets {FleetsStruck} ({HullsStruck} hulls), " +
            $"army groups {ArmyGroupsStruck}, orders {OrdersStruck}, building sites {ProjectsStruck}, seats {SeatsVacated}, " +
            $"wars trimmed {WarsTrimmed}, stale war stances closed {StaleWarsClosed}";
    }

    [ThreadStatic] private static Report _last;
    public static Report Last => _last ??= new Report();

    /// <summary>
    /// What a retired country stops carrying, by member name. A name, not a member, so the list is
    /// data a test can hold to the class (LongCampaignTests: every name exists) and a field another
    /// file adds later is simply not on it. Each is put back to what a newly constructed country
    /// has - which is what every system already copes with, because that is how a seceded or
    /// released country arrives.
    /// </summary>
    public static readonly string[] Dropped =
    {
        "Market", "TradePartners", "ImportValue", "ExportValue", "Stockpiles", "StockpilesSeeded",
        "TacticalDoctrine", "StrategicDoctrine", "NationalCourse",
        "TechnologyFamiliarity", "ResearchProgress", "CurrentResearch",
        "Budget", "StabilityDrivers", "WarSupportDrivers", "InflationDrivers",
        "LedgerWeek", "LedgerLastWeek", "LedgerBudgetWeek", "LedgerBudgetLastWeek",
        "LedgerPenaltyWeek", "LedgerPenaltyLastWeek", "LedgerTreasuryAtOpen", "LedgerOpen",
        "DomesticCrisis", "Elections", "Parliament", "Agenda", "Promises", "PolicyRates", "PolicyReadyOnDay",
        "DecisionAvailableDay", "EventMemory", "Modifiers", "MilitiaReadiness", "MilitiaQuietSince",
        "DomesticImage", "PublicImages",
        // What it remembered of its seats; an expulsion forgets the same two (OrganizationDiscipline).
        "OrganizationBelongingAtStart", "OrganizationConcessions"
    };

    private static FieldInfo[] _droppedFields;

    private static FieldInfo[] DroppedFields()
    {
        if (_droppedFields is not null) return _droppedFields;
        var found = new List<FieldInfo>(Dropped.Length);
        foreach (var name in Dropped)
            if (typeof(Nation).GetField(name, BindingFlags.Public | BindingFlags.Instance) is { } field) found.Add(field);
        return _droppedFields = found.ToArray();
    }

    /// <summary>Names on <see cref="Dropped"/> that are not public fields of a country (for the test).</summary>
    public static IEnumerable<string> UnknownDroppedNames()
    {
        foreach (var name in Dropped)
            if (typeof(Nation).GetField(name, BindingFlags.Public | BindingFlags.Instance) is null) yield return name;
    }

    /// <summary>Whether a retired country has been brought back by something.</summary>
    internal static bool Revived(Campaign c, Nation n) =>
        n.Movement is { Dissolved: false } || n.Population > 0 ||
        n.Movement is null && !c.Fallen.Contains(n.Id) || c.HasOwnedLand(n);

    /// <summary>Back into the world: the record is a country again and opens its books as a new one does.</summary>
    internal static void Wake(Campaign c, Nation n)
    {
        if (n.RetiredDay < 0) return;
        n.RetiredDay = -1;
        n.GoneSinceDay = -1;
        c.RetirementRevision++;
        // Its market is filled at the end of the day (GiveQuotes), not here: a wake can happen on
        // a worker thread of any pass, and another may be reading this country's market then.
        c.WokenAwaitingQuotes.Add(n.Id);
        Last.Woken++;
        DiagnosticLogs.Write("simulation", $"RETIRED_BACK nation={n.Tag} id={n.Id} day={c.ElapsedDays}");
    }

    /// <summary>
    /// Whether nothing about this country is alive: no ground, no people, no movement that has not
    /// dissolved, and - for a country that was a state - a fall the fall flow has ruled on. The
    /// rest of "nothing alive" is what other lists say about it (<see cref="MarkBusy"/>).
    ///
    /// A human's country is never gone, and "human" here is the seat (IsHuman, HumanSeats), the one
    /// piece of who-plays-what every machine of a network game holds alike. Not IsPlayer and not
    /// Campaign.Player: each machine points those at its own country, and the observer screen
    /// points Player at whatever was clicked - so a dead country would retire on one machine and
    /// not on the other, or on a different day depending on what the observer had selected.
    /// </summary>
    private static bool Gone(Campaign c, Nation n) =>
        n.Population <= 0 && !c.IsHumanControlled(n) &&
        (n.Movement is null ? c.Fallen.Contains(n.Id) : n.Movement.Dissolved) &&
        !n.IsSubject && !c.HasOwnedLand(n);

    /// <summary>
    /// The day a country that is gone went, for one found gone for the first time - which, in a
    /// campaign begun before this existed, is every one of its dead at once.
    ///
    /// A state stops being sampled the week it loses its last ground and its ledger keeps its
    /// last row for ever (CampaignHistory), so the end of its ledger is the day. A movement that
    /// dissolved wrote down the day it may be born again, a fixed time after (MovementFall).
    /// With neither, today.
    /// </summary>
    private static int GoneDay(Campaign c, Nation n)
    {
        if (n.Movement is { } state)
            return state.ReadyOnDay.TryGetValue("lifecycle_rebirth", out var rebirth)
                ? rebirth - Math.Max(30, c.Defines.MovementRebirthDays)
                : c.ElapsedDays;
        var last = c.History.LastDay(n.Id);
        return last >= 0 ? last : c.ElapsedDays;
    }

    [ThreadStatic] private static bool[] _busy;
    [ThreadStatic] private static bool[] _candidate;
    [ThreadStatic] private static bool[] _retiredNow;
    [ThreadStatic] private static bool[] _holdsProvince;
    [ThreadStatic] private static List<Nation> _retiring;
    [ThreadStatic] private static HashSet<string> _imageKeys;
    [ThreadStatic] private static List<KeyValuePair<long, DiplomaticRelation>> _kept;

    /// <summary>Once every LongCampaignSweepDays, after the weekly dormancy sweep: see <see cref="Run"/>.</summary>
    public static void Sweep(Campaign c)
    {
        if (c is null) return;
        // Every day, whatever the defines say: a retired country something has brought back is a
        // country again (a save may carry records from a campaign that had the diet on).
        c.WakeTheReturned();
        GiveQuotes(c);
        if (c.Defines is not { LongCampaignEnabled: true } rules) return;
        var every = Math.Max(7, rules.LongCampaignSweepDays / 7 * 7);
        if (c.ElapsedDays <= 0 || c.ElapsedDays % every != 0) return;
        Run(c);
    }

    /// <summary>
    /// A country woken today gets the quotes every country starts a campaign with (base price,
    /// nothing made or wanted yet), the same ones its first weekly balance would give it. A
    /// record has no market, and until that balance the woken country had none either - while
    /// the ledger screen reads a country's quotes by index and threw on it. Here, on the day's
    /// own thread after every pass, in id order: wherever in the day it woke, every machine does
    /// this at the same moment.
    /// </summary>
    internal static void GiveQuotes(Campaign c)
    {
        var woken = c.WokenAwaitingQuotes;
        if (woken.Count == 0) return;
        woken.Sort();
        foreach (var id in woken)
        {
            if (c.NationById(id) is not { RetiredDay: < 0 } n) continue;
            foreach (var commodity in CommodityCatalog.All)
                if (!n.Market.ContainsKey(commodity))
                    n.Market[commodity] = new MarketQuote { Commodity = commodity, Price = CommodityCatalog.Get(commodity).BasePrice };
        }
        woken.Clear();
    }

    /// <summary>
    /// The sweep itself, whatever the day. Deterministic: it reads only the campaign, walks every
    /// list in its own order, and leaves the sleeping-relation table laid out as a load of the
    /// same world would lay it - so a host that swept and a guest that loaded its save hold the
    /// same tables in the same order.
    /// </summary>
    public static Report Run(Campaign c)
    {
        var report = _last = new Report();
        if (c is null) return report;
        var rules = c.Defines;
        var count = c.Nations.Count;
        var today = c.ElapsedDays;
        // Whoever came back since the list was last read is awake before anything is decided.
        c.RefreshActiveNations();

        var candidate = Flags(ref _candidate, count);
        var busy = Flags(ref _busy, count);
        var any = false;
        for (var id = 0; id < count; id++)
        {
            var n = c.Nations[id];
            if (n.RetiredDay >= 0) continue;
            if (Gone(c, n)) { candidate[id] = true; any = true; }
            // Not gone after all: the clock starts again the next time it is.
            else n.GoneSinceDay = -1;
        }

        var retiring = _retiring ??= new List<Nation>();
        retiring.Clear();
        if (any && rules.RetireDeadAfterDays > 0)
        {
            for (var id = 0; id < count; id++)
                if (candidate[id] && c.Nations[id].GoneSinceDay < 0)
                    c.Nations[id].GoneSinceDay = Math.Clamp(GoneDay(c, c.Nations[id]), 0, today);
            MarkBusy(c, candidate, busy);
            for (var id = 0; id < count; id++)
            {
                if (!candidate[id]) continue;
                var n = c.Nations[id];
                report.Gone++;
                if (busy[id]) report.Busy++;
                else if (today - n.GoneSinceDay >= rules.RetireDeadAfterDays) retiring.Add(n);
                else report.Waiting++;
            }
        }

        var retiredNow = Flags(ref _retiredNow, count);
        if (retiring.Count > 0)
        {
            var keys = _imageKeys ??= new HashSet<string>(StringComparer.Ordinal);
            keys.Clear();
            foreach (var n in retiring)
            {
                retiredNow[n.Id] = true;
                keys.Add(SocialMemory.NationKey(n));
            }
            // The estate first: an organisation wound up hands its fund back through the members'
            // books, and those books are what Retire then closes.
            StrikeEstates(c, retiredNow, report);
            foreach (var n in retiring) Retire(c, n, report);
            // What the living think of them goes with them: nothing asks about a country nothing
            // can deal with, and one that returns is judged as it is then.
            var activeForKeys = c.ActiveNations;
            for (var ai = 0; ai < activeForKeys.Count; ai++)
            {
                var audience = activeForKeys[ai];
                if (audience.PublicImages.Count == 0) continue;
                foreach (var key in keys)
                    if (audience.PublicImages.Remove(key)) report.ImagesDropped++;
            }
            c.RetirementRevision++;
            c.RefreshActiveNations();
            c.MarkStrategicChange();
        }

        DropRelations(c, report);
        PrunePublicImages(c, report);
        TrimEndedWars(c, report);
        // The ledgers by age, here as well as at the year's turn: a campaign begun before the
        // landless were sampled monthly comes down to that the first time it is swept, not at
        // its next new year. Thinning is by fixed spans of days, so doing it more often keeps
        // the same samples (CampaignHistory.Thin).
        c.History.ThinAll(c);
        if (report.Retired > 0 || report.RelationsDropped + report.EmptyRelationsDropped > 0)
            DiagnosticLogs.Write("simulation", $"LONG_CAMPAIGN day={today} {report}");
        return report;
    }

    private static bool[] Flags(ref bool[] flags, int count)
    {
        if (flags is null || flags.Length < count) flags = new bool[count + 64];
        else Array.Clear(flags);
        return flags;
    }

    /// <summary>
    /// A gone country that something still points at is left for a later sweep: a formation in
    /// the field, a live war, a province it still controls, an awake relation (a loan or a war is
    /// why one stays awake), a subject or an overlord, an open paper of any kind.
    /// </summary>
    private static void MarkBusy(Campaign c, bool[] candidate, bool[] busy)
    {
        void Mark(int id) { if ((uint)id < (uint)c.Nations.Count && candidate[id]) busy[id] = true; }

        foreach (var army in c.Armies) Mark(army.NationId);
        var wars = Warfare.LiveWars(c);
        for (var index = 0; index < wars.Count; index++)
        {
            var war = wars[index];
            if (war is null || war.Ended) continue;
            foreach (var id in war.Side(true)) Mark(id);
            foreach (var id in war.Side(false)) Mark(id);
        }
        if (c.ProvinceController is { } control)
        {
            var holds = _holdsProvince;
            if (holds is null || holds.Length < ushort.MaxValue + 1) holds = _holdsProvince = new bool[ushort.MaxValue + 1];
            else Array.Clear(holds);
            for (var p = 1; p < control.Length; p++) holds[control[p]] = true;
            for (var id = 0; id < c.Nations.Count; id++)
                if (candidate[id] && c.Nations[id].MapIndex is > 0 and <= ushort.MaxValue && holds[c.Nations[id].MapIndex]) busy[id] = true;
        }
        foreach (var relation in c.Relations.Values)
        {
            if (!(candidate[relation.A] || candidate[relation.B])) continue;
            // A relation still reading "at war" with a country that has been gone for years and is
            // in no war: the war ended with the country and nobody told the relation. That one
            // word kept the relation awake for ever (RelationDormancy.Keep) and with it the
            // country - 64 such relations held 62 of the dead in a save 107 years long. Put right
            // here; the weekly sweep then lets it sleep and the next of these retires the country.
            if (relation.Stance == DiplomaticStance.AtWar && relation.LoanOutstanding <= 0f &&
                Due(c, relation.A, candidate) && Due(c, relation.B, candidate) &&
                !CampaignDiplomacy.AtWar(c, relation.A, relation.B))
            {
                relation.Stance = DiplomaticStance.Neutral;
                Last.StaleWarsClosed++;
            }
            Mark(relation.A); Mark(relation.B);
        }
        var activeSubjects = c.ActiveNations;
        for (var ni = 0; ni < activeSubjects.Count; ni++)
        {
            var n = activeSubjects[ni];
            if (n.IsSubject) { Mark(n.Id); Mark(n.OverlordId); }
        }
        foreach (var offer in c.DealOffers) if (offer.ClosedDay < 0) { Mark(offer.FromId); Mark(offer.ToId); }
        foreach (var paper in c.UltimatumPapers) if (paper.ClosedDay < 0) { Mark(paper.FromId); Mark(paper.ToId); }
        foreach (var request in c.AssistanceRequests) if (request.ClosedDay < 0) { Mark(request.RequesterId); Mark(request.DonorId); }
        foreach (var request in c.ArmsShipmentRequests) { Mark(request.RequesterId); Mark(request.DonorId); }
        foreach (var backing in c.Sponsorships) if (!backing.Ended) { Mark(backing.SponsorId); Mark(backing.ClientId); }
        foreach (var arrangement in c.LandArrangements) { Mark(arrangement.OwnerId); Mark(arrangement.HolderId); }
        foreach (var lease in c.LeaseOffers) { Mark(lease.FromId); Mark(lease.ToId); }
        foreach (var group in c.Coalitions)
        {
            Mark(group.TargetId);
            foreach (var member in group.MemberIds) Mark(member);
        }
        foreach (var pending in c.PendingEvents) Mark(pending.NationId);
        foreach (var lift in c.SeaLifts) Mark(lift.NationId);
        foreach (var seat in c.HumanSeats) Mark(seat);
    }

    /// <summary>Not a candidate (so nothing to wait for), or a candidate gone long enough to be retired.</summary>
    private static bool Due(Campaign c, int id, bool[] candidate) =>
        !candidate[id] || c.Nations[id].GoneSinceDay >= 0 &&
        c.ElapsedDays - c.Nations[id].GoneSinceDay >= c.Defines.RetireDeadAfterDays;

    /// <summary>One country to a record: everything on <see cref="Dropped"/> back to a new country's value.</summary>
    private static void Retire(Campaign c, Nation n, Report report)
    {
        var fresh = new Nation();
        foreach (var field in DroppedFields())
        {
            if (field.IsInitOnly)
            {
                // A collection the country owns for life: emptied, since it cannot be replaced.
                switch (field.GetValue(n))
                {
                    case IDictionary table: table.Clear(); break;
                    case IList list: list.Clear(); break;
                    case { } other: other.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(other, null); break;
                }
            }
            else field.SetValue(n, field.GetValue(fresh));
        }
        if (c.History.Forget(n.Id)) report.HistorySeriesDropped++;
        n.RetiredDay = c.ElapsedDays;
        report.Retired++;
    }

    /// <summary>
    /// What the retired left standing: fleets in port with nobody aboard, army groups without an
    /// army, orders that no arsenal or builder will ever finish, seats nobody sits in. Each was
    /// walked by its own pass every day or week. Nothing here belonged to a country with people.
    /// </summary>
    private static void StrikeEstates(Campaign c, bool[] retired, Report report)
    {
        bool Is(int id) => (uint)id < (uint)c.Nations.Count && retired[id];

        var fleets = 0; var hulls = 0;
        foreach (var fleet in c.Fleets)
            if (Is(fleet.NationId)) { fleets++; hulls += fleet.Hulls; }
        if (fleets > 0)
        {
            c.SeaLifts.RemoveAll(lift => Is(lift.NationId));
            c.Fleets.RemoveAll(fleet => Is(fleet.NationId));
            report.FleetsStruck += fleets;
            report.HullsStruck += hulls;
        }
        report.ArmyGroupsStruck += c.ArmyGroups.RemoveAll(group => Is(group.NationId));
        report.OrdersStruck += c.Recruitment.RemoveAll(order => Is(order.NationId));
        report.OrdersStruck += c.SupportOrders.RemoveAll(order => Is(order.NationId));
        report.OrdersStruck += c.SupportFormations.RemoveAll(formation => Is(formation.NationId));
        report.ProjectsStruck += c.Construction.RemoveAll(project => Is(project.NationId));
        var seats = report.SeatsVacated;
        foreach (var org in c.Organizations)
        {
            if (org.Dissolved) continue;
            var living = 0;
            foreach (var member in org.Members) if (!Is(member)) living++;
            // Nobody alive is left in it: it is wound up the way any organisation is - the fund
            // back to the members by the size of their economies (the records keep it in their
            // treasuries, as they keep the rest of their money), the motion, the invitations and
            // the suspended gone. Struck seat by seat instead, it stood with no members and its
            // fund: an open motion then passed on nobody's votes (none against none), and the
            // first living country invited in took the dead members' money with it.
            if (living == 0 && org.Members.Count > 0) OrganizationDynamics.DissolveNow(c, org);
            for (var index = org.Members.Count - 1; index >= 0; index--)
            {
                var member = org.Members[index];
                if (!Is(member)) continue;
                org.Members.RemoveAt(index);
                org.UnpaidWeeks.Remove(member);
                org.Motion?.Votes.Remove(member);
                report.SeatsVacated++;
            }
            org.Invitations.RemoveAll(Is);
            // The rest of what a seat leaves when a member goes by any other road (leaving,
            // expulsion): a suspension, a crisis it was bargaining in, and a vote about it - a
            // motion to admit or to aid a country nobody can deal with would invite a record in.
            foreach (var id in org.Suspended.Keys)
                if (Is(id)) { _retiredSuspended ??= new List<int>(); _retiredSuspended.Add(id); }
            if (_retiredSuspended is { Count: > 0 })
            {
                foreach (var id in _retiredSuspended) { org.Suspended.Remove(id); org.SuspendedFor.Remove(id); report.SeatsVacated++; }
                _retiredSuspended.Clear();
            }
            if (Is(org.CrisisMemberId)) { org.CrisisMemberId = -1; org.CrisisSinceDay = -1; }
            if (org.Motion is { } motion && Is(motion.TargetId)) org.Motion = null;
        }
        // Who stands by whom is cached (InternationalOrganizations.DefencePartners) against a
        // stamp of member counts that two organisations losing members at once can leave as it
        // was; every other road out of an organisation says so, and so does this one.
        if (report.SeatsVacated != seats) InternationalOrganizations.InvalidateDefencePartners();
    }

    [ThreadStatic] private static List<int> _retiredSuspended;

    /// <summary>
    /// Sleeping relations that have nobody at one end, and - PruneLandlessRelations - those between
    /// two landless sides that say nothing a new relation would not.
    ///
    /// Only the sleeping table: a relation in the daily table is somebody's business, and its
    /// owner is not retired while it is. The table is emptied and refilled in its own order
    /// rather than picked at, because a dictionary hands a removed row's place to the next row
    /// added: picked at, the host's table and the table a guest loads would list the same rows in
    /// different orders, and the weekly wake walks that order.
    /// </summary>
    private static void DropRelations(Campaign c, Report report)
    {
        if (c.DormantRelations.Count == 0) return;
        var prune = c.Defines.PruneLandlessRelations;
        var kept = _kept ??= new List<KeyValuePair<long, DiplomaticRelation>>();
        kept.Clear();
        var allied = false;
        foreach (var entry in c.DormantRelations)
        {
            var r = entry.Value;
            var a = c.NationById(r.A); var b = c.NationById(r.B);
            if (a is null || b is null || a.RetiredDay >= 0 || b.RetiredDay >= 0)
            {
                report.RelationsDropped++;
                allied |= r.Stance == DiplomaticStance.Allied;
                continue;
            }
            if (prune && !c.HasOwnedLand(a) && !c.HasOwnedLand(b) && CarriesNothing(r))
            {
                report.EmptyRelationsDropped++;
                continue;
            }
            kept.Add(entry);
        }
        if (kept.Count == c.DormantRelations.Count) { kept.Clear(); return; }
        c.DormantRelations.Clear();
        foreach (var entry in kept) c.DormantRelations.Add(entry.Key, entry.Value);
        kept.Clear();
        // Whoever keeps relations by country keeps none of the dropped ones.
        MovementAi.ForgetRelationIndex(c);
        if (allied) c.NoteAllianceChange();
    }

    private static Func<DiplomaticRelation, bool> _carriesNothing;

    /// <summary>
    /// Whether a relation is what CampaignDiplomacy.Get would make today: every saved member at a
    /// new relation's value, except the two ids, the sleeping flag and an opinion too small to
    /// show (the weekly pass works opinion out again for any relation that is awake). Built from
    /// the class by reflection, once, so a member another file adds tomorrow is compared without
    /// anybody remembering this list.
    /// </summary>
    public static bool CarriesNothing(DiplomaticRelation relation) =>
        relation is not null && (_carriesNothing ??= BuildCarriesNothing())(relation);

    private static Func<DiplomaticRelation, bool> BuildCarriesNothing()
    {
        var fresh = new DiplomaticRelation();
        var checks = new List<(FieldInfo Field, object Fresh)>();
        foreach (var field in typeof(DiplomaticRelation).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.Name is "A" or "B" or "Dormant" or "Opinion") continue;
            if (field.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null) continue;
            checks.Add((field, field.GetValue(fresh)));
        }
        var plan = checks.ToArray();
        return relation =>
        {
            if (MathF.Abs(relation.Opinion) >= 0.01f) return false;
            foreach (var (field, was) in plan)
            {
                var now = field.GetValue(relation);
                if (now is ICollection collection) { if (collection.Count != 0) return false; }
                else if (!Equals(now, was)) return false;
            }
            return true;
        };
    }

    [ThreadStatic] private static HashSet<string> _activeCoalitionKeysScratch;
    [ThreadStatic] private static List<string> _prunedImageKeysScratch;

    /// <summary>
    /// Prunes disbanded coalition reputation entries and inert near-zero reputation records across
    /// all three prefixes (<c>nation:</c>, <c>culture:</c>, <c>coalition:</c>) on living nations.
    /// </summary>
    private static void PrunePublicImages(Campaign c, Report report)
    {
        var activeCoalitions = _activeCoalitionKeysScratch ??= new HashSet<string>(StringComparer.Ordinal);
        activeCoalitions.Clear();
        for (var i = 0; i < c.Coalitions.Count; i++)
            activeCoalitions.Add(SocialMemory.CoalitionKey(c.Coalitions[i]));
        var toRemove = _prunedImageKeysScratch ??= new List<string>(32);
        var today = c.ElapsedDays;
        var active = c.ActiveNations;
        for (var ai = 0; ai < active.Count; ai++)
        {
            var audience = active[ai];
            if (audience.PublicImages.Count == 0) continue;
            toRemove.Clear();
            foreach (var (key, image) in audience.PublicImages)
            {
                if (key.StartsWith("coalition:", StringComparison.Ordinal) && !activeCoalitions.Contains(key))
                {
                    toRemove.Add(key);
                    continue;
                }
                if (image is null ||
                    (image.Status == 0 && MathF.Abs(image.Esteem) < 0.03f && MathF.Abs(image.Incident) < 0.02f &&
                     (image.LastIncidentDay < 0 || today - image.LastIncidentDay > 365) &&
                     (image.LastReviewDay < 0 || today - image.LastReviewDay > 180)))
                {
                    toRemove.Add(key);
                }
            }
            for (var ki = 0; ki < toRemove.Count; ki++)
            {
                if (audience.PublicImages.Remove(toRemove[ki])) report.ImagesDropped++;
            }
            if (audience.PublicImages.Count == 0) audience.PublicImages.TrimExcess();
        }
        toRemove.Clear();
        activeCoalitions.Clear();
    }

    /// <summary>
    /// A war a year over keeps what its ledger and the chronicle show - who, when, the losses,
    /// the score, how it ended - and lets go of who held each province before it: the peace has
    /// read that (PeaceCore) and nothing reads it of an ended war again.
    /// </summary>
    private static void TrimEndedWars(Campaign c, Report report)
    {
        var after = c.Defines.EndedWarDetailDays;
        if (after <= 0) return;
        foreach (var war in c.Wars)
        {
            if (!war.Ended || war.OriginalController.Count == 0 || c.ElapsedDays - war.EndedDay < after) continue;
            war.OriginalController.Clear();
            war.OriginalController.TrimExcess();
            report.WarsTrimmed++;
        }
    }
}
