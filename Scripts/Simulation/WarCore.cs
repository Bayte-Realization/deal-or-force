using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class StrategicWar
{
    public int Id;
    public string Name = "";
    public int AttackerId;
    public int DefenderId;
    public int StartedDay;
    public float AttackerWarSupport = 1f;
    public float DefenderWarSupport = 1f;
    public float AttackerLosses;
    public float DefenderLosses;

    /// <summary>
    /// What those losses were made of. The two totals above stay the totals - the war score
    /// and home support read them - and these break the same men down into the three things
    /// that happen to a soldier a war takes: killed, wounded, or taken. See WarLosses.
    /// </summary>
    public float AttackerKilled;
    public float AttackerWounded;
    public float AttackerCaptured;
    public float DefenderKilled;
    public float DefenderWounded;
    public float DefenderCaptured;

    /// <summary>
    /// Prisoners still held, by the country they were taken from. An exchange sends some
    /// home during the war; the peace sends the rest.
    /// </summary>
    public readonly Dictionary<int, float> Prisoners = new();

    /// <summary>The day of the last prisoner exchange in this war, or -1.</summary>
    public int LastPrisonerExchangeDay = -1;
    public float OccupationScore;
    public float BattleScore;
    public float ObjectiveScore;
    public float SupportScore;
    public float WarScore;
    public float Reparations;

    /// <summary>Provinces that changed owner - not merely occupier - when this war ended.</summary>
    public int AnnexedProvinces;

    /// <summary>Regions the new border cut in two, and so brought into existence.</summary>
    public int CreatedStates;
    public int ClaimedStateMapIndex = -1;

    /// <summary>
    /// What the attacking government told the world it was fighting for, as the id the
    /// simulation reasons in - see <see cref="CasusBelliNames"/>.
    ///
    /// It lives on the war rather than on the country because it belongs to the war: a
    /// government that declared twice kept only the second reason, so the first war on the map
    /// was described by an argument made about a different one. It is worked out at the
    /// declaration, priced into the attacker's standing at the same moment, and until now
    /// thrown away - which is why a player could see that a war had begun and never why.
    /// An empty string is the expensive kind: a war nobody can give a reason for.
    /// </summary>
    public string Justification = "";

    /// <summary>
    /// The day before which neither AI belligerent will sit down. A war a scenario opens
    /// with is a war both governments have already refused to end; leaving that to the
    /// ordinary appetite for peace let the third year of the Russo-Ukrainian war be settled
    /// in its first campaign month by whichever side happened to be marginally ahead.
    /// A player may always propose; this only governs what the AI is willing to offer.
    /// </summary>
    public int AiNegotiationUnlockDay;
    public bool Ended;
    public bool FrontDirty = true;
    public int LastFrontDay = -1;
    public int LastPeaceProposalDay = -1;
    public string LastNegotiation = "";

    /// <summary>
    /// A settlement the other government has put to a player and is waiting on.
    ///
    /// Two countries run by the computer settle a war between themselves in one step: the
    /// demand is written and the answer is worked out in the same breath. That cannot happen
    /// to a person, so when one side of a war is being played, what would have been a treaty
    /// waits here for an answer instead. It lives on the war because that is where its
    /// lifetime already is - the war ending takes it with it, and a save that restores the
    /// war restores what was on the table.
    /// </summary>
    public PeaceDemand PendingOffer;

    /// <summary>Whether the offer is a written settlement or one of the standing terms.</summary>
    public bool PendingOfferIsWritten;
    public PeaceTerms PendingOfferTerms = PeaceTerms.WhitePeace;

    /// <summary>Who is waiting on an answer, or -1 when nobody is.</summary>
    public int PendingOfferFromId = -1;
    public int PendingOfferDay = -1;
    public int PendingOfferExpiresDay = -1;

    /// <summary>
    /// The day a government run by the computer last put a settlement to a player in this
    /// war, or -1 when it never has.
    ///
    /// Between two computer governments an offer is answered in the same breath it is made,
    /// so how often one is made costs nobody anything. Put to a person, every offer is a
    /// letter they have to read, and a country that asks every few weeks - because its war
    /// score wobbled, or because its ally asked after it did - is nagging rather than
    /// negotiating. So offers to a player are spaced by AiPeaceOfferToPlayerIntervalDays,
    /// counted from this day and from the day the last one was refused (PeaceOffers).
    /// </summary>
    public int LastOfferToPlayerDay = -1;

    /// <summary>
    /// The last settlement a player in this war turned down or let lapse, kept so that the
    /// next one has to be a different offer rather than the same paper sent again. -1 when
    /// nothing has been refused; the other three fields mean nothing until it is set.
    /// A refused written settlement keeps its demand (the same object that was on the
    /// table); a refused standing term keeps which term it was.
    /// </summary>
    public int RefusedOfferDay = -1;
    public bool RefusedOfferWasWritten;
    public PeaceTerms RefusedOfferTerms = PeaceTerms.WhitePeace;
    public PeaceDemand RefusedOffer;
    public int EndedDay = -1;
    public int WinnerId = -1;
    public string PeaceSummary = "";
    public readonly HashSet<int> AttackerFront = new();
    public readonly HashSet<int> DefenderFront = new();
    public readonly Dictionary<int, ushort> OriginalController = new();
    public readonly Dictionary<int, float> UnopposedProgress = new();

    /// <summary>
    /// Everybody else fighting on each side, the two leaders excepted.
    ///
    /// A war used to be a pair of countries and an alliance answering one produced a fresh
    /// war per member: a NATO war was thirty wars, thirty war scores and thirty separate
    /// peace deals, which is what a player sees and what they reported. The leaders stay
    /// where they were - the country that declared and the country it declared on - so
    /// everything that already asked who started it still gets the right answer, and
    /// everybody who comes afterwards stands beside one of them.
    ///
    /// Public readonly collections, like the fronts above, because the save serialises the
    /// nation list whole with fields included and populates rather than replaces.
    /// </summary>
    public readonly HashSet<int> AttackerAllies = new();

    public readonly HashSet<int> DefenderAllies = new();

    public bool Involves(int nationId) => IsAttacking(nationId) || IsDefending(nationId);

    public bool IsAttacking(int nationId) =>
        nationId == AttackerId || (AttackerAllies.Count > 0 && AttackerAllies.Contains(nationId));

    public bool IsDefending(int nationId) =>
        nationId == DefenderId || (DefenderAllies.Count > 0 && DefenderAllies.Contains(nationId));

    /// <summary>Whether these two are on opposite sides of this war.</summary>
    public bool Opposes(int first, int second) =>
        (IsAttacking(first) && IsDefending(second)) || (IsDefending(first) && IsAttacking(second));

    /// <summary>
    /// The country on the other side that this war is about.
    ///
    /// Most of the simulation wants one enemy rather than a list - somewhere to march, a
    /// coast to blockade, a name for the war - and the leader is the right answer for all
    /// of them. Anything that has to be right about every belligerent asks for the side.
    /// </summary>
    public int EnemyLeaderOf(int nationId) =>
        IsAttacking(nationId) ? DefenderId : IsDefending(nationId) ? AttackerId : -1;

    /// <summary>Everybody on one side of it, leader first.</summary>
    public readonly struct SideEnumerable : IEnumerable<int>
    {
        private readonly int _leader;
        private readonly HashSet<int> _allies;
        internal SideEnumerable(int leader, HashSet<int> allies) { _leader = leader; _allies = allies; }
        public bool Contains(int nationId) => _leader >= 0 && (nationId == _leader || (_allies is not null && _allies.Contains(nationId)));
        public Enumerator GetEnumerator() => new(_leader, _allies);
        IEnumerator<int> IEnumerable<int>.GetEnumerator() => GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<int>
        {
            private readonly int _leader;
            private readonly HashSet<int> _allies;
            private HashSet<int>.Enumerator _alliesEnum;
            private readonly bool _hasAllies;
            private int _state;
            private int _current;
            internal Enumerator(int leader, HashSet<int> allies)
            {
                _leader = leader;
                _allies = allies;
                _hasAllies = leader >= 0 && allies is { Count: > 0 };
                _alliesEnum = _hasAllies ? allies.GetEnumerator() : default;
                _state = leader >= 0 ? 0 : 2;
                _current = 0;
            }
            public int Current => _current;
            object System.Collections.IEnumerator.Current => _current;
            public bool MoveNext()
            {
                if (_state == 0) { _state = 1; _current = _leader; return true; }
                if (_state == 1 && _hasAllies && _alliesEnum.MoveNext()) { _current = _alliesEnum.Current; return true; }
                _state = 2;
                return false;
            }
            public void Reset()
            {
                _state = _leader >= 0 ? 0 : 2;
                _current = 0;
                _alliesEnum = _hasAllies ? _allies.GetEnumerator() : default;
            }
            public void Dispose() { _alliesEnum.Dispose(); }
        }
    }

    public SideEnumerable Side(bool attacking) =>
        new(attacking ? AttackerId : DefenderId, attacking ? AttackerAllies : DefenderAllies);

    /// <summary>Everybody this country is fighting, leader first. Empty if it is not in the war.</summary>
    public SideEnumerable EnemiesOf(int nationId)
    {
        if (IsAttacking(nationId)) return Side(false);
        if (IsDefending(nationId)) return Side(true);
        return new(-1, null);
    }

    /// <summary>How many countries are in it. Two is the ordinary war.</summary>
    public int BelligerentCount => 2 + AttackerAllies.Count + DefenderAllies.Count;

    /// <summary>
    /// Put a country on one side of an existing war. A leader cannot change sides, and a
    /// member already on the other side is moved rather than counted twice.
    /// </summary>
    public void Join(int nationId, bool attacking)
    {
        if (nationId < 0 || nationId == AttackerId || nationId == DefenderId) return;
        if (attacking)
        {
            DefenderAllies.Remove(nationId);
            AttackerAllies.Add(nationId);
        }
        else
        {
            AttackerAllies.Remove(nationId);
            DefenderAllies.Add(nationId);
        }
    }

    public IReadOnlySet<int> FriendlyFront(int nationId) => FriendlyFrontSet(nationId);

    internal HashSet<int> FriendlyFrontSet(int nationId) =>
        IsAttacking(nationId) ? AttackerFront : IsDefending(nationId) ? DefenderFront : EmptyFront;

    private static readonly HashSet<int> EmptyFront = new();
}

/// <summary>
/// What a war is called on screen, in the language being played.
///
/// A war's name is written once, in English, the day it starts - a history name ("Sudanese Civil
/// War") or the two countries' names ("Turkey–North and East Syria War") - and the Russian
/// interface printed it as it was written. A history name is a translation key; a pair name is
/// rebuilt from the two countries' names as they are shown now.
/// </summary>
public static class WarNames
{
    public static string Of(Campaign campaign, StrategicWar war)
    {
        if (war is null) return "";
        if (war.Name.Length > 0 && Loc.Has(war.Name)) return Loc.Get(war.Name);
        var attacker = campaign?.NationById(war.AttackerId);
        var defender = campaign?.NationById(war.DefenderId);
        if (attacker is null || defender is null) return Literal(war.Name);
        // A war against a rising of the country's own people is that country's civil war.
        var rising = attacker.Name.StartsWith("rising:", StringComparison.Ordinal) ? defender
            : defender.Name.StartsWith("rising:", StringComparison.Ordinal) ? attacker : null;
        if (rising is not null && war.CivilConflict)
            return Loc.Format("{0} Civil War", rising.DisplayName);
        // A secession is the same war seen from the other side: the parent state and the
        // province that left it. Naming it after both countries reads as a tautology -
        // "Gulf Union-Republic of East War" - when it is one country's civil war. Unless the
        // side that left was a country before it was anybody's subject: China throwing off
        // an American protectorate is not the American civil war, it is China's war of
        // independence.
        if (SeniorSide(war, attacker, defender) is { } senior)
        {
            var leaver = senior.Id == attacker.Id ? defender : attacker;
            return leaver.SecededFromId == senior.Id && string.IsNullOrEmpty(leaver.FoundedInRevolt) &&
                   leaver.Movement is null && string.IsNullOrEmpty(leaver.FactionId)
                ? Loc.Format("{0} War of Independence", leaver.DisplayName)
                : Loc.Format("{0} Civil War", senior.DisplayName);
        }
        // A pair name is rebuilt from today's names, not today's spellings of yesterday's.
        if (IsPairName(war.Name) || NationNaming.LooksLikeKey(war.Name))
            return Loc.Format(war.Name.EndsWith(" Border War", StringComparison.Ordinal)
                ? "{0}–{1} Border War" : "{0}–{1} War", attacker.DisplayName, defender.DisplayName);
        return Literal(war.Name);
    }

    /// <summary>A stored name that is only a key is not a name.</summary>
    private static string Literal(string name) =>
        name.Length > 0 && !NationNaming.LooksLikeKey(name) ? name : "";

    /// <summary>
    /// The state a civil war is being fought over, or nothing when this is a war between
    /// two separate countries. A breakaway names its parent; a parent names the breakaway.
    /// </summary>
    private static Nation SeniorSide(StrategicWar war, Nation attacker, Nation defender)
    {
        if (attacker.SecededFromId == defender.Id) return defender;
        if (defender.SecededFromId == attacker.Id) return attacker;
        if (!war.CivilConflict) return null;
        if (WarAims.IsRebel(attacker) && !WarAims.IsRebel(defender)) return defender;
        if (WarAims.IsRebel(defender) && !WarAims.IsRebel(attacker)) return attacker;
        return defender;
    }

    /// <summary>A war the game wrote from two country names, rather than a history name.</summary>
    private static bool IsPairName(string name) =>
        name.EndsWith(" War", StringComparison.Ordinal) && name.Contains("\u2013", StringComparison.Ordinal);
}

public static class WarBootstrap
{
    public static void Seed(Campaign campaign)
    {
        var russia = campaign.NationByTag("RUS");
        var ukraine = campaign.NationByTag("UKR");
        if (russia is null || ukraine is null) return;
        campaign.MarkWarsChanged();
        campaign.Wars.Add(new StrategicWar
        {
            Id = 0,
            Name = "Russo-Ukrainian War",
            AttackerId = russia.Id,
            DefenderId = ukraine.Id,
            Justification = "irredentism",
            StartedDay = campaign.ElapsedDays,
            AiNegotiationUnlockDay = campaign.ElapsedDays + campaign.Defines.ScenarioWarNegotiationLockDays,
            ClaimedStateMapIndex = campaign.States.FirstOrDefault(state =>
                state.NationMapIndex == ukraine.MapIndex &&
                (state.Name.Contains("Donetsk", StringComparison.OrdinalIgnoreCase) ||
                 state.Name.Contains("Luhansk", StringComparison.OrdinalIgnoreCase)))?.MapIndex ??
                campaign.States.FirstOrDefault(state =>
                    state.NationMapIndex == ukraine.MapIndex &&
                    state.Name.Contains("Crimea", StringComparison.OrdinalIgnoreCase))?.MapIndex ??
                campaign.States.Where(state => state.NationMapIndex == ukraine.MapIndex)
                    .OrderByDescending(state => state.Population).FirstOrDefault()?.MapIndex ?? -1
        });
        var relation = CampaignDiplomacy.Get(campaign, russia.Id, ukraine.Id);
        if (relation.Stance == DiplomaticStance.Allied) campaign.NoteAllianceChange();
        relation.Stance = DiplomaticStance.AtWar;
        // Remembered, the way a declared war is, not written into the opinion: opinion is the
        // sum of its reasons, and the minus eighty that used to be set here was summed away at
        // the next settle - Ukraine thought +11 of Russia a week into the war. The ground Russia
        // holds of Ukraine's is a reason of its own.
        OpinionSystem.Remember(campaign, relation, "declared_war_on_us");
        OpinionSystem.Remember(campaign, relation, "fought_a_war");
        OpinionSystem.Remember(campaign, relation, "took_our_land");
    }
}

public static partial class Warfare
{
    /// <summary>
    /// Rebuild every front in one pass over the map.
    ///
    /// It used to walk all 37 807 provinces and their neighbours once per war. With a
    /// single scripted war that was invisible; once countries started declaring their own
    /// and seventy were running at a time it became two and a half million province visits
    /// per rebuild and the largest cost in the simulation. The map is walked once, and each
    /// pair of hostile neighbours is filed against whichever wars those two countries are
    /// fighting each other in.
    /// </summary>
    public static void RebuildFronts(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController ?? topology?.ProvinceToNation;
        if (topology is null || controllers is null) return;

        // Which war, if any, two countries are fighting each other in.
        //
        // The province walk below is the single most expensive thing the simulation does -
        // thirty-eight thousand provinces and all their neighbours, every time any war's
        // front goes stale. Hashing a country index twice per neighbour was most of it, so
        // membership is a flat array indexed by map index instead, and the attacker's index
        // is worked out once per war rather than once per pair of touching provinces.
        var byPair = Pairs;
        byPair.Clear();
        var belligerents = Belligerents;
        // Map indices are sparse: breakaways start at 1000, and mods may use
        // still higher values. Country COUNT silently excluded their entire fronts.
        var largestMapIndex = 0;
        foreach (var nation in campaign.ActiveNations) largestMapIndex = Math.Max(largestMapIndex, nation.MapIndex);
        if (belligerents.Length <= largestMapIndex)
            belligerents = Belligerents = new bool[largestMapIndex + 1];
        Array.Clear(belligerents);
        var live = LiveWars(campaign);
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (war.Ended) continue;
            war.AttackerFront.Clear();
            war.DefenderFront.Clear();
            war.FrontDirty = false;
            war.LastFrontDay = campaign.ElapsedDays;
            // Every pair across the two sides, not just the two leaders. A coalition war
            // has a front wherever any of its members touches any of the other side's, and
            // filing only the leaders' pair left everybody else's border unmanned.
            foreach (var attackingId in war.Side(true))
            {
                var attacker = campaign.NationById(attackingId);
                if (attacker is null) continue;
                if (attacker.MapIndex < belligerents.Length) belligerents[attacker.MapIndex] = true;
                foreach (var defendingId in war.Side(false))
                {
                    var defender = campaign.NationById(defendingId);
                    if (defender is null) continue;
                    if (defender.MapIndex < belligerents.Length) belligerents[defender.MapIndex] = true;
                    byPair[PairKey(attacker.MapIndex, defender.MapIndex)] = (war, (ushort)attacker.MapIndex);
                }
            }
        }
        if (byPair.Count == 0) return;

        var neighborsByProv = topology.Neighbors;
        var provCount = topology.ProvinceCount;
        var bellLen = belligerents.Length;
        for (var owner = 1; owner < bellLen; owner++)
        {
            if (!belligerents[owner]) continue;
            var held = CampaignIntel.ProvincesHeldBy(campaign, controllers, owner);
            for (var hi = 0; hi < held.Length; hi++)
            {
                var province = held[hi];
                if (province <= 0 || province > provCount || topology.IsWater(province)) continue;
                var adj = neighborsByProv[province];
                for (var ni = 0; ni < adj.Length; ni++)
                {
                    var neighbour = adj[ni];
                    var other = controllers[neighbour];
                    if (other == 0 || other == owner || other >= bellLen || !belligerents[other]) continue;
                    if (topology.IsWater(neighbour)) continue;
                    if (!byPair.TryGetValue(PairKey(owner, other), out var found)) continue;

                    if (owner == found.AttackerMap)
                    {
                        found.War.AttackerFront.Add(province);
                        found.War.DefenderFront.Add(neighbour);
                    }
                    else
                    {
                        found.War.DefenderFront.Add(province);
                        found.War.AttackerFront.Add(neighbour);
                    }
                }
            }
        }
    }

    private static readonly Dictionary<long, (StrategicWar War, ushort AttackerMap)> Pairs = new();
    private static bool[] Belligerents = Array.Empty<bool>();

    /// <summary>Order-independent key for a pair of countries.</summary>
    private static long PairKey(int a, int b) =>
        a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    sealed class LiveBoard
    {
        public readonly List<StrategicWar> Wars = new();
        public readonly List<StrategicWar> Advance = new();
        public int Revision = int.MinValue;
        public int Count = -1;
        /// <summary>The last war of the record when the board was built: a record replaced with one of the same length is another object.</summary>
        public StrategicWar Tail;
        public bool TailWasEnded;
    }

    private static LiveBoard LiveBoardFor(Campaign campaign)
    {
        if (campaign._liveWarBoard is LiveBoard existing) return existing;
        var created = new LiveBoard();
        var raced = System.Threading.Interlocked.CompareExchange(ref campaign._liveWarBoard, created, null);
        return (LiveBoard)(raced ?? created);
    }

    /// <summary>How many times a campaign's live list was rebuilt from the full war record.</summary>
    internal static int LiveWarPasses { get; private set; }

    /// <summary>
    /// Wars the day's supply, fronts, scores and reviews still visit.
    ///
    /// An ended war stays on <see cref="Campaign.Wars"/>: the chronicle, the ledger and a
    /// peace paper already on the table name it from there. The live list is rebuilt when
    /// a war is declared or ends, and a day that changes neither does not walk the record.
    /// Each campaign keeps its own list.
    /// </summary>
    public static IReadOnlyList<StrategicWar> LiveWars(Campaign campaign)
    {
        if (campaign is null) return Array.Empty<StrategicWar>();
        var board = LiveBoardFor(campaign);
        if (board.Revision != campaign.WarRevision || board.Count != campaign.Wars.Count ||
            HasEndedIn(board.Wars) ||
            !ReferenceEquals(board.Tail, campaign.Wars.Count > 0 ? campaign.Wars[^1] : null) ||
            board.TailWasEnded != (board.Tail?.Ended ?? false))
            RefreshLive(campaign, board);
        return board.Wars;
    }

    private static bool HasEndedIn(List<StrategicWar> wars)
    {
        for (var index = 0; index < wars.Count; index++)
            if (wars[index] is null || wars[index].Ended) return true;
        return false;
    }

    /// <summary>
    /// A copy of <see cref="LiveWars"/> for the advance pass, which yields between wars.
    /// Refreshing the live list while that pass is suspended does not drop a war the pass
    /// has not reached.
    /// </summary>
    internal static List<StrategicWar> AdvanceWars(Campaign campaign)
    {
        var board = LiveBoardFor(campaign);
        var live = LiveWars(campaign);
        board.Advance.Clear();
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (war is { Ended: false }) board.Advance.Add(war);
        }
        return board.Advance;
    }

    static readonly List<StrategicWar> NoWars = new(0);

    /// <summary>
    /// The wars not over, in the record's own order: what "is anybody at war with X" is asked of.
    /// Delegated directly to <see cref="Campaign.ActiveWarsList"/> so hot callers never touch
    /// ended historical wars or secondary lookup tables.
    /// </summary>
    public static List<StrategicWar> OpenWars(Campaign campaign) =>
        campaign is null ? NoWars : campaign.ActiveWarsList;

    /// <summary>
    /// The war with this number, over or not; null when the record holds none.
    /// Delegated directly to <see cref="Campaign.WarById"/>.
    /// </summary>
    public static StrategicWar WarById(Campaign campaign, int id) => campaign?.WarById(id);

    static void RefreshLive(Campaign campaign, LiveBoard board)
    {
        LiveWarPasses++;
        // The front rebuild used to clear an ended war on every pass. Once, as it leaves.
        for (var index = 0; index < board.Wars.Count; index++)
        {
            var war = board.Wars[index];
            if (war is not { Ended: true }) continue;
            war.AttackerFront.Clear();
            war.DefenderFront.Clear();
            war.FrontDirty = false;
            war.LastFrontDay = campaign.ElapsedDays;
        }
        board.Wars.Clear();
        var wars = campaign.Wars;
        for (var index = 0; index < wars.Count; index++)
        {
            var war = wars[index];
            if (war is null || war.Ended) continue;
            board.Wars.Add(war);
        }
        board.Revision = campaign.WarRevision;
        board.Count = wars.Count;
        board.Tail = wars.Count > 0 ? wars[^1] : null;
        board.TailWasEnded = board.Tail?.Ended ?? false;
    }
}

public static partial class FrontPlanner
{
    /// <summary>
    /// The provinces a reserve stands on: everything of ours that touches our own front
    /// without being on it.
    ///
    /// This is the same answer for every formation of a nation in a war, and it was being
    /// worked out separately for each of them - twice, because issuing the order asked for
    /// it again - by walking the whole front and all its neighbours and building a fresh
    /// set each time. That was most of the garbage the war pass made. It is worked out once
    /// and kept until the front is redrawn or a province changes hands, which is exactly
    /// when the answer can change.
    /// </summary>
    private static readonly ConditionalWeakTable<Campaign, Dictionary<long, (int Stamp, HashSet<int> Line)>> ReserveLines = new();

    public static HashSet<int> ReserveLine(Campaign campaign, StrategicWar war, int nationId)
    {
        var topology = campaign.ProvinceMap;
        var nation = campaign.NationById(nationId);
        var controllers = campaign.ProvinceController;
        if (topology is null || nation is null || controllers is null) return new HashSet<int>();

        var key = ((long)war.Id << 32) | (uint)nationId;
        var stamp = HashCode.Combine(war.LastFrontDay, campaign.MapRevision,
            war.AttackerFront.Count, war.DefenderFront.Count);
        // A save being inspected beside a live world can use the same ids and
        // revision on a different front. Neither may own the other's cached set.
        var lines = ReserveLines.GetValue(campaign, static _ => new());
        if (lines.TryGetValue(key, out var cached) && cached.Stamp == stamp) return cached.Line;

        var result = new HashSet<int>();
        var front = war.FriendlyFrontSet(nationId);
        // The seat of government on the front itself: the reserve stands in it. A country that
        // sent every brigade forward and left its capital to the local guards lost the war in the
        // afternoon the capital fell - the whole of a small country can be one province.
        var seat = nation.CapitalProvinceId;
        if (seat > 0 && seat < controllers.Length && controllers[seat] == nation.MapIndex && front.Contains(seat))
        {
            result.Add(seat);
            lines[key] = (stamp, result);
            return result;
        }
        foreach (var province in front)
        foreach (var neighbor in topology.Neighbors[province])
            if (!front.Contains(neighbor) && controllers[neighbor] == nation.MapIndex)
                result.Add(neighbor);
        if (result.Count == 0) result = new HashSet<int>(front);
        lines[key] = (stamp, result);
        return result;
    }

    public static void RefreshAssignments(Campaign campaign, StrategicWar war)
    {
        foreach (var _ in RefreshAssignmentStages(campaign, war, sliced: false)) { }
    }

    internal static IEnumerable<int> RefreshAssignmentStages(Campaign campaign, StrategicWar war, bool sliced = true)
    {
        foreach (var army in campaign.Armies)
        {
            if (army.AssignedWarId != war.Id || army.OrderKind == ArmyOrder.Manual || army.InBattle)
                continue;
            if (army.DirectOrder && army.OrderProvinceId > 0) continue;
            if (army.EmbarkedFleetId >= 0) continue;

            // A division that was given somewhere to be, and is still on its way there, is
            // left to get there.
            //
            // The test below asks whether the target is still on the front, and the front is
            // rebuilt every time a province changes hands - so on a moving front a marching
            // division fails that test almost every day and is handed a new target it will
            // also never reach. That is measurable: three years of unbroken marching, and a
            // hundred and twelve formations re-entering the same province five times or more.
            //
            // What still excuses an immediate re-task is the objective going away: ground we
            // now hold ourselves is not somewhere to march to.
            if (army.OrderProvinceId > 0 &&
                campaign.ElapsedDays - army.OrderIssuedDay < campaign.Defines.OrderSettleDays &&
                !OwnGround(campaign, army)) continue;
            var expected = BattlePlanning.Posts(campaign, war, army);
            if (expected.Count == 0 || expected.Contains(army.ProvinceId) ||
                (army.OrderProvinceId > 0 && expected.Contains(army.OrderProvinceId)))
                continue;
            new SetArmyOrderCommand
            {
                IssuerId = army.NationId,
                ArmyId = army.Id,
                WarId = war.Id,
                Order = army.OrderKind,
                ObjectiveStateMapIndex = army.ObjectiveStateMapIndex
            }.Apply(campaign);
            if (sliced && Campaign.StageOverdue()) yield return 0;
        }
    }

    /// <summary>
    /// Whether what a formation is marching towards is already its own side's ground, which
    /// is the one thing that makes a march pointless rather than merely unfinished.
    /// </summary>
    private static bool OwnGround(Campaign campaign, Army army)
    {
        var control = campaign.ProvinceController;
        var nation = campaign.NationById(army.NationId);
        if (control is null || nation is null) return false;
        if (army.OrderProvinceId <= 0 || army.OrderProvinceId >= control.Length) return false;
        // Only for the orders that are about taking ground. A division posted to defend or
        // to sit in reserve is supposed to be marching to somewhere its own side holds.
        if (army.OrderKind != ArmyOrder.AdvanceFront && army.OrderKind != ArmyOrder.Spearhead)
            return false;
        return control[army.OrderProvinceId] == (ushort)nation.MapIndex;
    }

    public static void AssignAi(Campaign campaign)
    {
        var active = campaign.ActiveWarsList;
        for (var i = 0; i < active.Count; i++)
            ReassignAi(campaign, active[i]);
    }

    public static void ReassignAi(Campaign campaign, StrategicWar war)
    {
        foreach (var _ in ReassignAiStages(campaign, war, sliced: false)) { }
    }

    internal static IEnumerable<int> ReassignAiStages(Campaign campaign, StrategicWar war, bool sliced = true)
    {
        if (war.Ended) yield break;
        for (var side = 0; side < 2; side++)
        {
            var attacking = side == 0;
            foreach (var nationId in war.Side(attacking))
            {
                foreach (var _ in AssignSideStages(campaign, war, nationId, attacking, sliced))
                    yield return 0;
                if (sliced && Campaign.StageOverdue()) yield return 0;
            }
        }
    }

    public static void UpdateAiExecution(Campaign campaign, StrategicWar war) =>
        UpdateAiExecution(campaign, war, campaign.Armies);

    /// <param name="assigned">
    /// The divisions assigned to this war, in the order the world lists them - the advance
    /// pass has them indexed already. Walking every division in the world for every war, every
    /// hour, to find the same few hundred was a third of what the advance pass cost.
    /// </param>
    [ThreadStatic] private static Dictionary<int, AiStrategyDefinition> _executionProfiles;
    [ThreadStatic] private static HashSet<ushort> _executionAttackerEnemyMaps;
    [ThreadStatic] private static HashSet<ushort> _executionDefenderEnemyMaps;

    public static void UpdateAiExecution(Campaign campaign, StrategicWar war, List<Army> assigned)
    {
        // Filtered by hand: the lambda captured the war, so this was a fresh closure and a
        // fresh enumerator for every one of the world's wars, every day.
        //
        // The doctrine is resolved once per country on a side, not once per division. Every
        // division of a country is answered by the same profile, and this ran hourly for
        // every war in the world - a quarter of a thousand lookups a day to arrive at a
        // dozen answers. The profile is still read fresh for each country, so an army that
        // has just lost its supply still switches to the defensive posture this hour.
        var profiles = _executionProfiles ??= new Dictionary<int, AiStrategyDefinition>();
        profiles.Clear();
        HashSet<ushort> attackerEnemyMaps = null;
        HashSet<ushort> defenderEnemyMaps = null;
        var allowUnopposed = campaign.Defines.AiStartsUnopposedAdvance;
        var today = campaign.ElapsedDays;
        var map = campaign.ProvinceMap;
        var provCount = map?.ProvinceCount ?? 0;
        var assignedCount = assigned.Count;
        for (var i = 0; i < assignedCount; i++)
        {
            var army = assigned[i];
            if (army.AssignedWarId != war.Id || army.PlanActive) continue;
            if (army.OrderKind != ArmyOrder.AdvanceFront && army.OrderKind != ArmyOrder.Spearhead) continue;
            var nation = campaign.NationById(army.NationId);
            // A player's divisions are the player's - unless the general staff in the field
            // was handed over, in which case they march by the same rule as everybody else's.
            if (nation is null || (nation.IsHuman && !Delegation.Holds(nation, Delegation.Command))) continue;
            if (!profiles.TryGetValue(nation.Id, out var profile))
                profiles[nation.Id] = profile = AiStrategyCatalog.Resolve(campaign, war, nation);
            if (StaffReasons.SupplyHoldsAttack(campaign, army)) continue;
            if (army.Planning < profile.ExecutePlanningThreshold)
            {
                // Fast reject before constructing enemy controller sets or entering F# assessment:
                // all of these conditions unconditionally return -1 inside OpenAdvanceTarget.
                if (!allowUnopposed || map is null || campaign.ProvinceController is null ||
                    army.DirectOrder || army.SupportOnly || army.InBattle || army.IsMoving ||
                    army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 || army.ProvinceId > provCount ||
                    army.ReadyDay > today)
                    continue;
                var attacking = war.IsAttacking(army.NationId);
                HashSet<ushort> enemyMaps;
                if (attacking)
                {
                    if (attackerEnemyMaps is null)
                    {
                        attackerEnemyMaps = _executionAttackerEnemyMaps ??= new HashSet<ushort>();
                        attackerEnemyMaps.Clear();
                        WarCoalition.FillControllers(campaign, war, false, attackerEnemyMaps);
                    }
                    enemyMaps = attackerEnemyMaps;
                }
                else
                {
                    if (defenderEnemyMaps is null)
                    {
                        defenderEnemyMaps = _executionDefenderEnemyMaps ??= new HashSet<ushort>();
                        defenderEnemyMaps.Clear();
                        WarCoalition.FillControllers(campaign, war, true, defenderEnemyMaps);
                    }
                    enemyMaps = defenderEnemyMaps;
                }
                if (!Warfare.CanStartOpenAdvance(campaign, war, army, enemyMaps)) continue;
            }
            SetArmyPlanExecutionCommand.ApplyTo(campaign, army, nation.Id, active: true);
        }
    }

    /// <summary>What the shares ask of the division standing at this place in the line.</summary>
    private static ArmyOrder RoleAt(int index, int spearhead, int advance, int defend) =>
        index < spearhead ? ArmyOrder.Spearhead :
        index < advance ? ArmyOrder.AdvanceFront :
        index < advance + defend ? ArmyOrder.DefendFront : ArmyOrder.ReserveFront;

    /// <summary>
    /// How much of the line attacks - and whether the attack is called off. An offensive that
    /// is losing, costing three of ours for each of theirs and more than AiBleedingLossShare
    /// of the country's people, goes over to holding what it has: North Korea threw seven
    /// million men at a line that cost the South ninety thousand, for ten years.
    /// </summary>
    internal static float AdvanceShare(Campaign c, StrategicWar war, Nation nation, AiStrategyDefinition profile,
        bool attacking, out bool pressing)
    {
        var share = attacking ? profile.AdvanceShareAttacker : profile.AdvanceShareDefender;
        pressing = attacking;
        if (nation is null) return share;
        var score = attacking ? war.WarScore : -war.WarScore;
        var ours = attacking ? war.AttackerLosses : war.DefenderLosses;
        var theirs = attacking ? war.DefenderLosses : war.AttackerLosses;
        // Wars for existence are the exception to bleeding conservatively. A side whose own
        // ground is under the enemy's boot does not get to sit the war out no matter what
        // the losses say; an attacker who is actually taking ground is not "bleeding for
        // nothing" either - the occupation is the point. The cut below is for futile
        // bleeding, and futile is measured by the map, not by the casualty list.
        var ownGroundGone = (attacking ? war.AttackerOccupiedValue : war.DefenderOccupiedValue) >= 0.05f;
        var gainingGround = war.DefenderOccupiedValue >= 0.02f || war.AttackerOccupiedValue >= 0.02f;
        var bleeding = score < -10f && ours > theirs * 3f &&
            ours / MathF.Max(1f, nation.Population) > c.Defines.AiBleedingLossShare;
        if (bleeding && !(ownGroundGone || gainingGround))
        {
            pressing = false;
            share *= 0.2f;
        }
        else if (!attacking)
        {
            // A defender whose sovereignty or core provinces are threatened must not remain purely passive:
            // counter-offensives actively contest lost ground when the army is intact.
            var territoryThreatened = war.DefenderOccupiedValue > 0.005f || score < -2f;
            var canPress = ours <= theirs * 2.5f && nation.WarSupport >= 0.20f;
            // Lost ground is a reason to counter-attack, not an order to. A tenth of the country
            // occupied used to make the counter-offensive the majority posture whatever the
            // casualty list said: Ukraine threw more than half its army forward from the fourth
            // month, lost five men for every Russian, and was gone by the end of the year on every
            // seed. A defender presses while it can afford to and holds when it cannot - which is
            // what defending a country is.
            if (territoryThreatened && canPress)
            {
                pressing = true;
                share = MathF.Max(share, 0.45f);
            }
            else if (score > 4f && canPress)
            {
                // Exploiting clear battlefield momentum to end the war decisively.
                pressing = true;
                share = MathF.Max(share, 0.38f);
            }
        }
        else if (ownGroundGone)
        {
            // An attacker whose own land is being taken while they attack abroad presses
            // regardless of the ledger: the war at home is the one that matters.
            pressing = true;
            share = MathF.Max(share, 0.5f);
        }
        // And an army has to be able to attack. Whatever the situation asks, an offensive by
        // divisions at a quarter of their organisation is not an offensive: Ukraine kept half its
        // army advancing into Russia at 24-43 % until one counter-blow broke the whole front in a
        // month. Below the readiness line the share falls away - an operational pause, for either
        // side - and comes back as the divisions recover.
        share *= Readiness(c, war, nation);
        share = MathF.Min(0.92f, share * OffensivePulse(c, war, nation));
        return share;
    }

    /// <summary>
    /// The rhythm of a war, for one side: offensives and pauses rather than the same pressure
    /// every day. Every side of every war has a cycle of its own, ninety to a hundred and eighty
    /// days long with a phase drawn from the seed; for about a third of it the general staff
    /// throws its weight forward (AiOffensivePulseStrength more of the army on the advance), for
    /// a quarter it rests and refits. Two sides seldom peak together, so a front moves in waves -
    /// one side's offensive, the other's counter-blow - and no two campaigns move alike. Readiness
    /// still applies under it: a broken army does not attack because it is the season to.
    /// </summary>
    internal static float OffensivePulse(Campaign c, StrategicWar war, Nation nation)
    {
        if (war is null || nation is null) return 1f;
        return WarRhythm.Pulse(c.Defines.AiOffensivePulseStrength,
            Escalation.Roll(c.Seed, war.Id, nation.Id, 0, 0x0F1), Escalation.Roll(c.Seed, war.Id, nation.Id, 0, 0x0F2),
            c.ElapsedDays - war.StartedDay);
    }

    /// <summary>
    /// How ready a country's force in a war is to attack, 0 to 1: full at AiOffensiveReadyOrganisation
    /// of average organisation, nothing at AiOffensiveExhaustedOrganisation.
    /// </summary>
    internal static float Readiness(Campaign c, StrategicWar war, Nation nation)
    {
        float organisation = 0f;
        var count = 0;
        foreach (var army in c.Armies)
        {
            if (army.NationId != nation.Id || army.AssignedWarId != war.Id || army.TemplateId == MilitiaCore.TemplateId) continue;
            organisation += army.Organisation;
            count++;
        }
        if (count == 0) return 1f;
        var ready = c.Defines.AiOffensiveReadyOrganisation;
        var spent = MathF.Min(ready - 0.01f, c.Defines.AiOffensiveExhaustedOrganisation);
        return M.Clamp01((organisation / count - spent) / MathF.Max(0.01f, ready - spent));
    }

    private static int AdvanceCount(int count, float share, bool pressingOrAttacking) =>
        Math.Min(count, Math.Max(share > 0 && count > 0 && (pressingOrAttacking || count <= 3) ? 1 : 0, (int)MathF.Round(count * share)));

    /// <summary>Fill missing roles with a replacement without reordering formations already marching.</summary>
    internal static void GiveIdleRole(Campaign c, Army army)
    {
        StrategicWar war = null;
        if (army.AssignedWarId >= 0)
        {
            var byId = Warfare.WarById(c, army.AssignedWarId);
            if (byId is { Ended: false })
            {
                war = byId;
            }
            else if (byId is not null)
            {
                var active = c.ActiveWarsList;
                for (var wi = 0; wi < active.Count; wi++)
                    if (active[wi].Id == army.AssignedWarId) { war = active[wi]; break; }
            }
        }
        var nation = c.NationById(army.NationId);
        if (war is null || nation is null) return;
        if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Command)) return;
        var profile = AiStrategyCatalog.Resolve(c, war, nation);
        var attacking = war.IsAttacking(nation.Id);
        var share = AdvanceShare(c, war, nation, profile, attacking, out var pressing);
        // One pass, no array: this runs for every idle formation a front rebalances, and
        // materialising the peer list each time was the warfare pass's largest allocation.
        var peers = 0;
        var advancing = 0;
        foreach (var peer in c.Armies)
        {
            if (peer.NationId != nation.Id || peer.AssignedWarId != war.Id ||
                peer.TemplateId == MilitiaCore.TemplateId) continue;
            peers++;
            if (peer != army && peer.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead) advancing++;
        }
        var wanted = AdvanceCount(peers, share, pressing);
        if (advancing >= wanted) return;
        new SetArmyOrderCommand { IssuerId = nation.Id, ArmyId = army.Id, WarId = war.Id,
            Order = ArmyOrder.AdvanceFront }.Apply(c);
        new SetArmyStanceCommand { IssuerId = nation.Id, ArmyId = army.Id, Stance = profile.OffensiveStance }.Apply(c);
        new SetArmyPlanExecutionCommand { IssuerId = nation.Id, ArmyId = army.Id,
            Active = army.Planning >= profile.ExecutePlanningThreshold }.Apply(c);
    }

    /// <summary>
    /// Which of a country's divisions serve which of its wars.
    ///
    /// Every war took the whole army. Each war's pass sorted all of the country's divisions and
    /// handed them roles, and the order carrying them out sent every one to the first war in the
    /// list - so a country fighting two wars fought one. Over two years, on four worlds, seven in
    /// ten of the second fronts had none of the country's own army on them: South Korea's
    /// thirty-seven divisions sat on an ally's front while the North Korean one was held by
    /// nobody, and Nepal fought China with everything and India with nothing.
    ///
    /// Each war now gets a share of the army in proportion to what it needs - the country's own
    /// stretch of that front and the enemy divisions put into it; a war where it holds no ground
    /// of its own, an ally's, asks for little. A division stays with the war it serves while that
    /// war is not over its share, so divisions change fronts when the need moves and not when the
    /// list is rebuilt, and the war that is short takes the divisions nearest to its front.
    /// </summary>
    private static List<Army> ArmiesFor(Campaign campaign, StrategicWar war, int nationId) =>
        ArmiesFor(campaign, war, nationId, out _);

    private static List<Army> ArmiesFor(Campaign campaign, StrategicWar war, int nationId, out SplitScratch rentedScratch)
    {
        var scratch = SplitBoards.GetOrCreateValue(campaign);
        var splitWars = scratch.SplitWars;
        splitWars.Clear();
        var openWars = Warfare.OpenWars(campaign);
        for (var wi = 0; wi < openWars.Count; wi++)
        {
            var candidate = openWars[wi];
            if (!candidate.Ended && candidate.Involves(nationId))
                splitWars.Add(candidate);
        }
        var armies = scratch.RentArmyList();
        FieldArmyInto(campaign, nationId, armies);
        if (splitWars.Count < 2)
        {
            rentedScratch = scratch;
            return armies;
        }

        // Once per simulation step: all wars read the same split within that step.
        // A day-long derived cache survives hourly movement and cannot be reproduced
        // by a cold client loading a snapshot halfway through that day.
        var stamp = armies.Count;
        for (var wi = 0; wi < splitWars.Count; wi++)
            stamp = HashCode.Combine(stamp, splitWars[wi].Id);
        if (!scratch.Splits.TryGetValue(nationId, out var split)) scratch.Splits[nationId] = split = new ArmySplit();
        if (split.Hour != campaign.ElapsedHours || split.Stamp != stamp)
        {
            split.Hour = campaign.ElapsedHours;
            split.Stamp = stamp;
            Split(campaign, nationId, splitWars, armies, split.ByWar, scratch);
        }
        scratch.ReturnArmyList(armies);
        rentedScratch = null;
        return split.ByWar.TryGetValue(war.Id, out var share) ? share : scratch.EmptyArmyList;
    }

    private sealed class ArmySplit
    {
        public long Hour = -1;
        public int Stamp;
        public readonly Dictionary<int, List<Army>> ByWar = new();
    }

    private sealed class SplitScratch
    {
        public readonly Dictionary<int, ArmySplit> Splits = new();
        public readonly List<StrategicWar> SplitWars = new();
        public readonly List<Army> EmptyArmyList = new();
        public readonly List<List<Army>> SplitArmyPool = new();
        public readonly List<List<int>> SplitFrontPool = new();
        public float[] Weight = Array.Empty<float>();
        public int[] Quota = Array.Empty<int>();
        public int[] Taken = Array.Empty<int>();
        public float[] Distance = Array.Empty<float>();
        public readonly Dictionary<int, int> FirstDistanceRow = new();
        public int[] Serving = Array.Empty<int>();
        public float[] SortKeys = Array.Empty<float>();
        public readonly List<int> Order = new();
        public readonly List<int> Free = new();
        public readonly IComparer<int> OrderComparer;

        public SplitScratch()
        {
            OrderComparer = Comparer<int>.Create((a, b) => SortKeys[a].CompareTo(SortKeys[b]));
        }

        public List<Army> RentArmyList()
        {
            var n = SplitArmyPool.Count;
            if (n == 0) return new List<Army>();
            var list = SplitArmyPool[n - 1];
            SplitArmyPool.RemoveAt(n - 1);
            list.Clear();
            return list;
        }

        public void ReturnArmyList(List<Army> list)
        {
            list.Clear();
            if (SplitArmyPool.Count < 256) SplitArmyPool.Add(list);
        }
    }

    // A bounded front review can resume on a different thread when paused or saved.
    private static readonly ConditionalWeakTable<Campaign, SplitScratch> SplitBoards = new();

    private static void Split(Campaign campaign, int nationId, List<StrategicWar> wars, List<Army> armies,
        Dictionary<int, List<Army>> byWar, SplitScratch scratch)
    {
        foreach (var existing in byWar.Values)
        {
            existing.Clear();
            scratch.SplitArmyPool.Add(existing);
        }
        byWar.Clear();
        var nation = campaign.NationById(nationId);
        var controllers = campaign.ProvinceController ?? campaign.ProvinceMap?.ProvinceToNation;
        var map = campaign.ProvinceMap;
        var count = wars.Count;
        if (scratch.Weight.Length < count)
        {
            scratch.Weight = new float[count];
            scratch.Quota = new int[count];
            scratch.Taken = new int[count];
        }
        var weight = scratch.Weight;
        var threatByWar = scratch.Quota;
        Array.Clear(threatByWar, 0, count);
        var allArmies = campaign.Armies;
        for (var i = 0; i < allArmies.Count; i++)
        {
            var enemy = allArmies[i];
            if (enemy.AssignedWarId < 0 || enemy.TemplateId == MilitiaCore.TemplateId) continue;
            for (var w = 0; w < count; w++)
            {
                var war = wars[w];
                if (enemy.AssignedWarId == war.Id)
                {
                    if (war.Opposes(nationId, enemy.NationId)) threatByWar[w]++;
                    break;
                }
            }
        }
        while (scratch.SplitFrontPool.Count < count) scratch.SplitFrontPool.Add(new List<int>());
        for (var w = 0; w < count; w++)
        {
            var war = wars[w];
            byWar[war.Id] = scratch.RentArmyList();
            var front = war.FriendlyFrontSet(nationId);
            var frontList = scratch.SplitFrontPool[w];
            frontList.Clear();
            if (controllers is not null && nation is not null)
                foreach (var province in front)
                    if (controllers[province] == (ushort)nation.MapIndex) frontList.Add(province);
            var threat = threatByWar[w];
            if (frontList.Count > 0)
            {
                weight[w] = frontList.Count + 0.5f * threat;
            }
            else
            {
                weight[w] = 0.1f * Math.Min(front.Count, threat);
                foreach (var province in front) frontList.Add(province);
            }
        }

        var total = 0f;
        for (var w = 0; w < count; w++) total += weight[w];
        if (total <= 0f || map is null || armies.Count == 0)
        {
            byWar[wars[0].Id].AddRange(armies);
            return;
        }

        // Whole divisions, largest remainders first, adding up to the army.
        var quota = scratch.Quota;
        var given = 0;
        for (var w = 0; w < count; w++) given += quota[w] = (int)(armies.Count * weight[w] / total);
        while (given < armies.Count)
        {
            var most = 0;
            var remainder = float.MinValue;
            for (var w = 0; w < count; w++)
            {
                var left = armies.Count * weight[w] / total - quota[w];
                if (left > remainder) { remainder = left; most = w; }
            }
            quota[most]++;
            given++;
        }
        // A front of its own is never left to nobody while the army has a division to spare.
        for (var w = 0; w < count; w++)
        {
            if (quota[w] > 0 || scratch.SplitFrontPool[w].Count == 0 || weight[w] < 1f) continue;
            var largest = 0;
            for (var v = 1; v < count; v++) if (quota[v] > quota[largest]) largest = v;
            if (quota[largest] <= 1) break;
            quota[largest]--;
            quota[w]++;
        }

        var distLen = armies.Count * count;
        if (scratch.Distance.Length < distLen) scratch.Distance = new float[distLen];
        var distance = scratch.Distance;
        var firstDistanceRow = scratch.FirstDistanceRow;
        firstDistanceRow.Clear();
        for (var i = 0; i < armies.Count; i++)
        {
            var origin = armies[i].ProvinceId;
            var row = i * count;
            if (firstDistanceRow.TryGetValue(origin, out var existingRow))
            {
                Array.Copy(distance, existingRow, distance, row, count);
                continue;
            }
            firstDistanceRow.Add(origin, row);
            var (latitude, longitude) = map.CenterOf(origin);
            for (var w = 0; w < count; w++)
                distance[row + w] = SampleFrontDistance(map, scratch.SplitFrontPool[w], latitude, longitude);
        }

        if (scratch.Serving.Length < armies.Count)
        {
            scratch.Serving = new int[armies.Count];
            scratch.SortKeys = new float[armies.Count];
        }
        var serving = scratch.Serving;
        var taken = scratch.Taken;
        Array.Clear(taken, 0, count);
        var order = scratch.Order;
        order.Clear();
        for (var i = 0; i < armies.Count; i++)
        {
            var targetWarId = armies[i].AssignedWarId;
            var found = -1;
            for (var w = 0; w < count; w++)
                if (wars[w].Id == targetWarId) { found = w; break; }
            serving[i] = found;
            scratch.SortKeys[i] = found < 0 ? float.MaxValue : distance[i * count + found];
            order.Add(i);
        }
        // Those already serving a war stay with it, nearest to its front first, up to one over
        // its share - one either way is not worth a march across the country.
        order.Sort(scratch.OrderComparer);
        var free = scratch.Free;
        free.Clear();
        for (var o = 0; o < order.Count; o++)
        {
            var i = order[o];
            var w = serving[i];
            if (w >= 0 && quota[w] > 0 && taken[w] < quota[w] + 1) taken[w]++;
            else
            {
                serving[i] = -1;
                free.Add(i);
            }
        }
        // The rest go where divisions are short, the nearest first.
        while (free.Count > 0)
        {
            var shortest = -1;
            var need = 0;
            for (var w = 0; w < count; w++)
                if (quota[w] - taken[w] > need) { need = quota[w] - taken[w]; shortest = w; }
            var pick = 0;
            if (shortest < 0)
            {
                // Nobody is short: the nearest front.
                var i = free[0];
                var nearest = 0;
                for (var w = 1; w < count; w++)
                    if (distance[i * count + w] < distance[i * count + nearest]) nearest = w;
                serving[i] = nearest;
                taken[nearest]++;
                free.RemoveAt(0);
                continue;
            }
            for (var f = 1; f < free.Count; f++)
                if (distance[free[f] * count + shortest] < distance[free[pick] * count + shortest]) pick = f;
            serving[free[pick]] = shortest;
            taken[shortest]++;
            free.RemoveAt(pick);
        }

        for (var i = 0; i < armies.Count; i++) byWar[wars[serving[i]].Id].Add(armies[i]);
    }

    private static float SampleFrontDistance(ProvinceTopology map, List<int> front, float latitude, float longitude)
    {
        var count = front.Count;
        if (count == 0) return float.MaxValue;
        var scale = MathF.Cos(latitude * MathF.PI / 180f);
        var best = float.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var (lat, lon) = map.CenterOf(front[i]);
            var east = MathF.Abs(lon - longitude);
            if (east > 180f) east = 360f - east;
            east *= scale;
            var north = lat - latitude;
            var distance = east * east + north * north;
            if (distance < best) best = distance;
        }
        return best;
    }

    private static IEnumerable<int> AssignSideStages(Campaign campaign, StrategicWar war, int nationId, bool attacking, bool sliced)
    {
        var nation = campaign.NationById(nationId);
        // A country the computer runs takes its orders here. A player's does not - unless
        // the general staff in the field was handed over (Delegation.Command), in which case
        // its divisions are assigned and executed by exactly the same planner. Before this
        // check the delegation moved a player's free reserve into posts but never gave it a
        // role in a war, so an army handed to the staff stood on the front and did nothing.
        if (nation is null || (nation.IsHuman && !Delegation.Holds(nation, Delegation.Command))) yield break;
        var profile = AiStrategyCatalog.Resolve(campaign, war, nation);
        // Territorial defence is not part of the field army's order of battle. Militia holds
        // the region it stood up in; sweeping it into the front shares distorted them badly -
        // the counts here are shares of how many formations there are, so twenty-seven
        // militia pushed real divisions out of defending and into attacking, and Ukraine lost
        // two thirds of its ground to a mechanic written to help it hold.
        var armies = ArmiesFor(campaign, war, nationId, out var rentedScratch);
        if (armies.Count == 0)
        {
            rentedScratch?.ReturnArmyList(armies);
            yield break;
        }
        try
        {
            var participantObjective = ObjectiveFor(campaign, war, nation);
            // Strongest first.
            //
            // Worth recording what does NOT work here, because it looks obviously right: Power
            // moves every day with losses and recovery, so similar divisions trade places
            // constantly, and roles are handed out by POSITION in this list - so a swap across
            // the spearhead/advance boundary flips two divisions' orders and restarts both their
            // marches. Bucketing the power to damp that was tried at two coarsenesses and made
            // the war worse both times: the ties it creates are broken by id, which is unrelated
            // to strength, so weak divisions lead attacks. Ukraine lost thirty provinces at 0.1
            // and thirty at 0.02 where it gained eighteen without it.
            //
            // The churn is real - 8,000-odd orders in three years against 2,900 arrivals - but
            // the cure is not here. It is that a division should be re-posted when its post has
            // stopped being useful, not when the list it came from was rebuilt.
            armies.Sort(static (a, b) => b.Power != a.Power ? b.Power.CompareTo(a.Power) : a.Id.CompareTo(b.Id));

            var advanceShare = AdvanceShare(campaign, war, nation, profile, attacking, out var pressing);
            var advance = AdvanceCount(armies.Count, advanceShare, pressing);
            var spearhead = armies.Count == 0 || profile.SpearheadShare <= 0 ? 0 : Math.Min(advance,
                Math.Max(1, (int)MathF.Round(armies.Count * profile.SpearheadShare)));
            var defend = (int)MathF.Round(armies.Count * profile.DefendShare);
            var capitalProvince = CapitalCore.EffectiveProvinceId(campaign, nation);
            for (var index = 0; index < armies.Count; index++)
            {
                if (sliced && Campaign.StageOverdue()) yield return 0;
                var army = armies[index];
                // A division on a march it was sent on directly - railed somewhere, called to a port
                // for the admiralty's landing - finishes it first. Handing it a role mid-journey
                // threw the journey away and left it riding the rails to a post it was never sent
                // to; when it arrives it is free again and gets its role then.
                if (army.DirectOrder && (army.Route.Count > 0 || army.PlannedRoute.Count > 0)) continue;
                var order = RoleAt(index, spearhead, advance, defend);

                // Damping this is the wrong cure, and both attempts at it proved so. Bucketing
                // the power made weak divisions lead attacks; holding a division in its job
                // until it had moved two places in the order of battle cut the re-tasking by a
                // tenth and cost Ukraine seventy-eight provinces, because an AI that answers a
                // moving front slowly is an AI that loses.
                //
                // The churn is load-bearing. What is wrong is not that a division's job changes
                // - it is that changing the job used to throw away the march, which is handled
                // where the post is chosen.

                var stance = order == ArmyOrder.Spearhead || order == ArmyOrder.AdvanceFront
                    ? profile.OffensiveStance
                    : order == ArmyOrder.DefendFront ? profile.DefensiveStance : ArmyStance.Balanced;
                // The capital is not a province like the others. A government still sitting in its
                // own capital orders the divisions there to hold it, whatever the doctrine says:
                // that is the one order a computer army was not giving itself, and losing the
                // capital without a fight was the cheapest way to lose a war.
                if (order == ArmyOrder.DefendFront && army.ProvinceId > 0 &&
                    army.ProvinceId == capitalProvince)
                    stance = ArmyStance.LastStand;

                // Re-issuing an order a division already has is not free: it clears the route,
                // picks a fresh target from a front list that shifts every time a province
                // changes hands, and sends the division marching sideways - which costs
                // planning. Doing that to the whole army every time the line moved meant an AI
                // that spent its war walking up and down its own front and never attacked.
                // A division is only re-tasked when what is being asked of it actually changed.
                //
                // Or when it is being handed to this war from another one the country is fighting.
                if (army.OrderKind != order || (order == ArmyOrder.Spearhead &&
                        army.ObjectiveStateMapIndex != participantObjective) ||
                    army.AssignedWarId != war.Id)
                    new SetArmyOrderCommand
                    {
                        IssuerId = nationId,
                        ArmyId = army.Id,
                        WarId = war.Id,
                        ObjectiveStateMapIndex = participantObjective,
                        Order = order
                    }.Apply(campaign);

                if (army.Stance != stance)
                    new SetArmyStanceCommand { IssuerId = nationId, ArmyId = army.Id, Stance = stance }.Apply(campaign);

                bool execute;
                if (StaffReasons.SupplyHoldsAttack(campaign, army))
                    execute = false;
                else if (order is not (ArmyOrder.AdvanceFront or ArmyOrder.Spearhead))
                {
                    execute = false;
                }
                else if (army.PlanActive)
                {
                    // Active offensive plan continues unless the division is exhausted in organisation or supply.
                    var assessment = AssaultReadiness.Assess(campaign, army);
                    execute = assessment.Reason is not (AssaultReadiness.Reason.LowOrganisation or AssaultReadiness.Reason.LowSupply);
                }
                else
                {
                    execute = army.Planning >= profile.ExecutePlanningThreshold;
                }

                if (army.PlanActive != execute)
                    SetArmyPlanExecutionCommand.ApplyTo(campaign, army, nationId, execute);
            }
        }
        finally
        {
            rentedScratch?.ReturnArmyList(armies);
        }
    }
}

public sealed partial class Defines
{
    /// <summary>The share of its people a losing offensive may cost a country before its staff calls it off.</summary>
    public float AiBleedingLossShare { get; set; } = 0.01f;
}
