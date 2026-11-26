using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// Re-postings a front's staff may make at one look, before the length of the front is
    /// added: a few, so a line is mended within days and never reshuffled wholesale.
    /// </summary>
    public int FrontRepostsPerLook { get; set; } = 3;

    /// <summary>Of the front's own length, the share more re-postings it may make at one look.</summary>
    public float FrontRepostsPerPost { get; set; } = 0.15f;

    /// <summary>
    /// Days a division is left alone after being posted, unless where it stands has become a
    /// pile. Without it the same division would be sent back and forth as the line flickers.
    /// </summary>
    public int FrontSettleDays { get; set; } = 3;

    /// <summary>
    /// How much more threatened an empty post must be than a quiet held one before a lone
    /// division is taken off the quiet one to fill it.
    /// </summary>
    public float FrontThinQuietRatio { get; set; } = 3f;

    /// <summary>
    /// Whether the staff moves divisions ordered to advance as well as those ordered to
    /// defend. Off by default, and measured: spreading every advance evenly along a front is
    /// an attack everywhere at once, and over two years of the 2024 world it cost Ukraine a
    /// third more ground than leaving the advances where the planner massed them. A mod that
    /// wants its offensives spread along the line turns it on.
    /// </summary>
    public bool FrontStaffMovesAdvances { get; set; }

    /// <summary>
    /// Whether the staff hands a war the divisions of a country at war that serve none of its
    /// wars - raised during it, or released when another war ended - instead of leaving them
    /// until the front next moves.
    /// </summary>
    public bool FrontStaffCallsUpIdle { get; set; } = true;

    /// <summary>
    /// How threatened a hole in the line must be - its need in divisions, one for being on the
    /// front and one more for each division's worth of enemy beside it - before the staff
    /// closes it with a division taken off an advance when no reserve is free.
    /// </summary>
    public float FrontHoleAdvanceNeed { get; set; } = 3.5f;

    /// <summary>
    /// Whether one re-posting is spent on the empty post that reconnects a cut-off formation,
    /// taken from a free reserve before any other hole. Off, a hole is still filled by how
    /// threatened it is, and this rule does not order the reserve or claim the road is opening.
    /// A mod sets this false. The 14-day bleed and the dry-out of an attack stay either way.
    /// </summary>
    public bool FrontStaffRelievesPockets { get; set; } = true;
}

/// <summary>
/// The staff that keeps a front a front (item 44).
///
/// Posts were handed out when an order was given and never looked at again. A division kept
/// its post until five or six others had joined it, and was only reconsidered when its
/// province dropped off the front altogether - so when the line moved, the provinces that
/// became front were held by nobody, and when a war opened on a short front every division
/// stood where the front had been. Measured: France with eight of eighteen divisions in one
/// province of six and two provinces held by nobody; on a drawn line, "anywhere on the line"
/// counted as being where one should be, so a line could be all in one place and pass.
///
/// So each front is looked at, once a day and as soon as it moves, as a whole - which is what
/// a player sees in Hearts of Iron and what "a front" means: every post held before any post
/// is held twice, the posts facing the enemy first, and a pile given up to the holes nearest
/// it. The staff moves few divisions at a time and leaves a division it has just posted alone
/// for a few days, so a line that is mended is not also a line that walks about.
///
/// It works for every country and every kind of front: the AI's war fronts, the posts of a
/// player's divisions ordered to defend or advance on a front, and the lines a player draws.
/// Spearheads are left where they are - concentrating is what a spearhead is for - and so are
/// direct orders and embarked formations. A pinned surplus may release a covering
/// reserve through FrontDisengagement after free reserves have been exhausted.
/// </summary>
public static class FrontManager
{
    // Posting scratch, reused across fronts of ONE campaign. These lists used to be one
    // set for the process: the next campaign's look cleared the first campaign's posts,
    // army buckets and front rows, then read whatever it had just written. The live tick
    // stays single-threaded. `Active` only names the board of the look now running.
    private sealed class Scratch
    {
        public readonly Dictionary<int, float> EnemyPower = new();
        public readonly Dictionary<int, List<Army>> Allocated = new();
        public readonly Stack<List<Army>> Spare = new();
        public readonly List<Army> Loose = new();
        public readonly List<Army> BattleCandidates = new();
        public readonly List<int> Posts = new();
        public readonly List<(int Post, float Need)> RankedPosts = new();
        public readonly HashSet<int> PostSet = new();
        public readonly HashSet<int> PostStates = new();
        public readonly Dictionary<int, float> Need = new();
        public readonly HashSet<int> Refused = new();
        public readonly Dictionary<int, int> DonorComponents = new();
        public readonly Queue<int> DonorFrontier = new();
        public readonly List<DrawnFrontLine> Lines = new();
        public readonly HashSet<int> LineEnemies = new();
        public readonly List<Army> Idle = new();
        /// <summary>Empty posts this look refused to fill by pulling an attack off the line.</summary>
        public readonly HashSet<int> ReliefNoAdvance = new();
        public readonly Dictionary<int, List<Army>> ArmiesByNation = new();
        public readonly List<StrategicWar> Wars = new();
        public readonly HashSet<int> WarNations = new();
        /// <summary>
        /// This look's possible reserves, in the nation roster's order: every formation of the
        /// country on this war ordered to stand in reserve or advance, not a militia. Nothing
        /// in one look turns another formation into one of these (a re-posting keeps or
        /// restores its duty; a battle detachment is ordered to defend), so filtering the
        /// rest once is exact. NearestReserve still asks the full question of each.
        /// </summary>
        public readonly List<Army> Reserves = new();
        public readonly HashSet<int> OwnOccupied = new();
        public bool ReservesReady;
        public bool NationIndexActive;

        // Reusable synchronous BFS scratch for Repost (never spans a yield return).
        public int[] RoutePrevious = Array.Empty<int>();
        public int[] RouteStamp = Array.Empty<int>();
        public int RouteGeneration;
        public readonly Queue<int> RouteFrontier = new();
        public readonly List<int> RouteScratch = new();
    }

    private static readonly ConditionalWeakTable<Campaign, Scratch> Boards = new();
    private static Scratch Active;

    private static Dictionary<int, float> EnemyPower => Active.EnemyPower;
    private static Dictionary<int, List<Army>> Allocated => Active.Allocated;
    private static Stack<List<Army>> Spare => Active.Spare;
    private static List<Army> Loose => Active.Loose;
    private static List<Army> BattleCandidates => Active.BattleCandidates;
    private static List<int> Posts => Active.Posts;
    private static HashSet<int> PostSet => Active.PostSet;
    private static HashSet<int> PostStates => Active.PostStates;
    private static Dictionary<int, float> Need => Active.Need;
    private static HashSet<int> Refused => Active.Refused;
    private static Dictionary<int, int> DonorComponents => Active.DonorComponents;
    private static Queue<int> DonorFrontier => Active.DonorFrontier;
    private static List<DrawnFrontLine> Lines => Active.Lines;
    private static HashSet<int> LineEnemies => Active.LineEnemies;
    private static List<Army> Idle => Active.Idle;
    private static HashSet<int> ReliefNoAdvance => Active.ReliefNoAdvance;

    private static void BeginNationArmies(Campaign campaign)
    {
        if (Active.NationIndexActive) return;
        foreach (var list in Active.ArmiesByNation.Values) { list.Clear(); Spare.Push(list); }
        Active.ArmiesByNation.Clear();
        for (var i = 0; i < campaign.Armies.Count; i++)
        {
            var army = campaign.Armies[i];
            if (!Active.ArmiesByNation.TryGetValue(army.NationId, out var list))
                Active.ArmiesByNation[army.NationId] = list = Take();
            list.Add(army);
        }
        Active.NationIndexActive = true;
    }

    private static void EndNationArmies()
    {
        if (!Active.NationIndexActive) return;
        foreach (var list in Active.ArmiesByNation.Values) { list.Clear(); Spare.Push(list); }
        Active.ArmiesByNation.Clear();
        Active.NationIndexActive = false;
    }

    private static List<Army> NationArmies(Campaign campaign, int nationId) =>
        Active is not null && Active.NationIndexActive
            ? (Active.ArmiesByNation.TryGetValue(nationId, out var list) ? list : EmptyArmyList)
            : campaign.Armies;

    private static readonly List<Army> EmptyArmyList = new();

    /// <summary>
    /// Provinces the last staff look posted in this campaign, in the order that look left them.
    /// Empty when this world has not looked. Reading does not look and does not write the world.
    /// </summary>
    internal static IReadOnlyList<int> PostedProvinces(Campaign campaign) =>
        campaign is not null && Boards.TryGetValue(campaign, out var board) ? board.Posts : Array.Empty<int>();

    /// <summary>Armies the last look counted on one post in this campaign. Empty when it counted none.</summary>
    internal static IReadOnlyList<Army> PostedArmies(Campaign campaign, int province) =>
        campaign is not null && Boards.TryGetValue(campaign, out var board) &&
        board.Allocated.TryGetValue(province, out var armies) ? armies : Array.Empty<Army>();

    /// <summary>
    /// Drawn lines the last look walked in this campaign. Empty when this world has not looked.
    /// The next look of this same campaign reuses the list; another campaign cannot clear it.
    /// </summary>
    internal static IReadOnlyList<DrawnFrontLine> FrontRows(Campaign campaign) =>
        campaign is not null && Boards.TryGetValue(campaign, out var board) ? board.Lines : Array.Empty<DrawnFrontLine>();

    /// <summary>Re-postings made since the campaign began, for the bench and the console.</summary>
    public static int Reposts { get; private set; }

    /// <summary>Look at every front in the world.</summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>Look at every front in the world, yielding between wars and drawn lines.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(FrontManager))) yield break;
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) yield break;
        var board = Boards.GetOrCreateValue(campaign);
        Active = board;
        CallUpIdle(campaign);
        yield return 0;

        Active = board;
        BeginNationArmies(campaign);
        try
        {
            // A copy: balancing one front can end a war and rebuild the live list under this look.
            var live = Warfare.LiveWars(campaign);
            var wars = board.Wars;
            wars.Clear();
            for (var index = 0; index < live.Count; index++)
            {
                var war = live[index];
                if (!war.Ended) wars.Add(war);
            }
            for (var wi = 0; wi < wars.Count; wi++)
            {
                Active = board;
                var war = wars[wi];
                if (war.Ended) continue;
                for (var side = 0; side < 2; side++)
                {
                    var attacking = side == 0;
                    FillWarEnemyPower(campaign, war, attacking);
                    foreach (var nationId in war.Side(attacking))
                    {
                        BalanceSideMember(campaign, war, nationId);
                        if (Campaign.StageOverdue())
                        {
                            yield return 0;
                            Active = board;
                        }
                    }
                }
                yield return 0;
            }
            // A full withdrawal may detach the last formation and prune empty plans.
            // Walk this look's roster, not the live list that those commands can edit.
            // The roster stays on this campaign's board until its next look reuses the list.
            // Another campaign has its own roster and cannot drop these rows.
            Active = board;
            Lines.Clear();
            Lines.AddRange(campaign.FrontLines);
            for (var li = 0; li < Lines.Count; li++)
            {
                Active = board;
                var line = Lines[li];
                if (!campaign.FrontLines.Contains(line)) continue;
                if (line.Kind is FrontLineKind.Defence or FrontLineKind.AreaDefence or FrontLineKind.Offensive) BalanceLine(campaign, line);
                else if (line.Kind == FrontLineKind.Fallback) DefensivePlanning.Maintain(campaign, line);
                yield return 0;
            }
        }
        finally
        {
            Active = board;
            board.Wars.Clear();
            EndNationArmies();
        }
    }

    /// <summary>
    /// Divisions of a country at war that serve no war at all, handed to one.
    ///
    /// The planner gives out the army when a war starts, when it is joined and when its front
    /// moves, and at no other time - so a division raised during a war, or one released when
    /// another of the country's wars ended, stood where it was until the line moved; on a front
    /// that did not move, that was for good. Measured over two years: sixty-odd divisions at
    /// any moment, in countries at war, on nobody's front.
    ///
    /// Each is told to hold the line of the war whose front is nearest, and nothing else is
    /// touched: asking the planner to hand out the whole army again for the sake of one
    /// recruit re-tasked everyone else as well, and nearly doubled the orders changed under
    /// divisions still on the march. A division marching where it was sent (to a port for the
    /// admiralty, or by a player) or aboard a ship is not idle, and a player's army is the
    /// player's.
    /// </summary>
    private static void CallUpIdle(Campaign campaign)
    {
        if (!campaign.Defines.FrontStaffCallsUpIdle) return;
        Idle.Clear();
        var allArmies = campaign.Armies;
        for (var i = 0; i < allArmies.Count; i++)
        {
            var army = allArmies[i];
            // Manual, including a division already named to a war but given no defend or advance.
            // A direct player order is left alone.
            if (army.OrderKind != ArmyOrder.Manual || army.DirectOrder) continue;
            if (army.InBattle || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 || army.Route.Count > 0) continue;
            if (army.TemplateId == MilitiaCore.TemplateId || army.FrontLineId >= 0) continue;
            Idle.Add(army);
        }
        if (Idle.Count == 0) return;
        var warNations = Active.WarNations;
        warNations.Clear();
        var live = Warfare.LiveWars(campaign);
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (war.Ended) continue;
            warNations.Add(war.AttackerId);
            warNations.Add(war.DefenderId);
            foreach (var ally in war.AttackerAllies) warNations.Add(ally);
            foreach (var ally in war.DefenderAllies) warNations.Add(ally);
        }
        for (var i = 0; i < Idle.Count; i++)
        {
            var army = Idle[i];
            var nation = campaign.NationById(army.NationId);
            // A player's divisions stay where they were put, unless the general staff in the
            // field was handed over. Then the same call-up every other country gets applies.
            if (nation is null || (campaign.IsHumanControlled(nation) && !Delegation.Holds(nation, Delegation.Command))) continue;
            if (!warNations.Contains(nation.Id)) continue;
            new SetArmyOrderCommand { IssuerId = nation.Id, ArmyId = army.Id, Order = ArmyOrder.DefendFront }.Apply(campaign);
            FrontPlanner.GiveIdleRole(campaign, army);
            CalledUp++;
            CalledUpBy[nation.Id] = CalledUpBy.GetValueOrDefault(nation.Id) + 1;
        }
    }

    /// <summary>Divisions called up from idleness since the campaign began, for the bench.</summary>
    public static int CalledUp { get; private set; }

    /// <summary>The same, by country id.</summary>
    public static readonly Dictionary<int, int> CalledUpBy = new();

    /// <summary>Both sides of one war, each country on its own sector of its side's front.</summary>
    public static void Balance(Campaign campaign, StrategicWar war)
    {
        if (war.Ended || campaign.ProvinceMap is null || campaign.ProvinceController is null) return;
        Active = Boards.GetOrCreateValue(campaign);
        var builtIndex = !Active.NationIndexActive;
        if (builtIndex) BeginNationArmies(campaign);
        try
        {
            for (var side = 0; side < 2; side++)
            {
                var attacking = side == 0;
                FillWarEnemyPower(campaign, war, attacking);
                foreach (var nationId in war.Side(attacking))
                    BalanceSideMember(campaign, war, nationId);
            }
        }
        finally
        {
            if (builtIndex) EndNationArmies();
        }
    }

    private static void BalanceSideMember(Campaign campaign, StrategicWar war, int nationId)
    {
        var nation = campaign.NationById(nationId);
        if (nation is null) return;
        // Its own sector: a coalition's front runs past every member, and a division
        // cannot march through an ally's country to stand on its border.
        Posts.Clear();
        // The set itself, not the read-only view: the same members in the same order,
        // without boxing an enumerator for every country on every side every day.
        foreach (var province in war.FriendlyFrontSet(nationId))
            if (campaign.ProvinceController[province] == (ushort)nation.MapIndex) Posts.Add(province);
        Posts.Sort();
        Arrange(campaign, nation, war, null);
    }

    private static void FillWarEnemyPower(Campaign campaign, StrategicWar war, bool attacking)
    {
        EnemyPower.Clear();
        foreach (var enemyId in war.Side(!attacking))
        {
            var enemyArmies = NationArmies(campaign, enemyId);
            for (var i = 0; i < enemyArmies.Count; i++)
            {
                var army = enemyArmies[i];
                if (army.ProvinceId <= 0 || army.EmbarkedFleetId >= 0) continue;
                EnemyPower[army.ProvinceId] = EnemyPower.GetValueOrDefault(army.ProvinceId) + army.Power;
            }
        }
    }

    /// <summary>One drawn line, held by the divisions put on it.</summary>
    private static void BalanceLine(Campaign campaign, DrawnFrontLine line)
    {
        var nation = campaign.NationById(line.NationId);
        if (nation is null) return;
        // The whole front moves with the map and with the list of enemies; bring it up to date
        // before it is read, as a division asking for its line would.
        if (line.AllEnemies) BorderFronts.Refresh(campaign, line);
        if (line.IsEmpty || line.Cohesion == PlanCohesion.Immobile) return;
        StrategicWar war = null;
        var openWars = Warfare.OpenWars(campaign);
        for (var wi = 0; wi < openWars.Count; wi++)
        {
            var candidate = openWars[wi];
            if (!candidate.Ended && candidate.Involves(nation.Id) &&
                (line.BorderNationId < 0 || candidate.Opposes(nation.Id, line.BorderNationId)))
            {
                war = candidate;
                break;
            }
        }
        // The whole front is under pressure from everybody it faces, in every war - not only
        // from the other side of whichever war happens to be first in the list.
        if (line.AllEnemies) BorderFronts.FillEnemies(campaign, nation.Id, LineEnemies);
        EnemyPower.Clear();
        if (Active.NationIndexActive)
        {
            if (line.AllEnemies)
            {
                foreach (var enemyId in LineEnemies)
                {
                    if (enemyId == nation.Id) continue;
                    var enemyArmies = NationArmies(campaign, enemyId);
                    for (var i = 0; i < enemyArmies.Count; i++)
                    {
                        var army = enemyArmies[i];
                        if (army.ProvinceId <= 0 || army.EmbarkedFleetId >= 0) continue;
                        EnemyPower[army.ProvinceId] = EnemyPower.GetValueOrDefault(army.ProvinceId) + army.Power;
                    }
                }
            }
            else if (war is not null)
            {
                foreach (var enemyId in war.Side(!war.IsAttacking(nation.Id)))
                {
                    if (enemyId == nation.Id) continue;
                    var enemyArmies = NationArmies(campaign, enemyId);
                    for (var i = 0; i < enemyArmies.Count; i++)
                    {
                        var army = enemyArmies[i];
                        if (army.ProvinceId <= 0 || army.EmbarkedFleetId >= 0) continue;
                        EnemyPower[army.ProvinceId] = EnemyPower.GetValueOrDefault(army.ProvinceId) + army.Power;
                    }
                }
            }
        }
        else
        {
            foreach (var army in campaign.Armies)
            {
                if (army.ProvinceId <= 0 || army.EmbarkedFleetId >= 0 || army.NationId == nation.Id) continue;
                if (line.AllEnemies ? !LineEnemies.Contains(army.NationId)
                    : war is null || !war.Opposes(army.NationId, nation.Id)) continue;
                EnemyPower[army.ProvinceId] = EnemyPower.GetValueOrDefault(army.ProvinceId) + army.Power;
            }
        }
        Posts.Clear();
        var controllers = campaign.ProvinceController;
        var ownController = (ushort)nation.MapIndex;
        if (line.Kind == FrontLineKind.Offensive)
        {
            foreach (var province in line.SourceProvinces)
                if (province > 0 && province < controllers.Length && controllers[province] == ownController) Posts.Add(province);
        }
        else
        {
            var sequence = BattlePlanning.Sequence(campaign, line);
            for (var i = 0; i < sequence.Count; i++)
            {
                var province = sequence[i];
                if (province > 0 && province < controllers.Length && controllers[province] == ownController) Posts.Add(province);
            }
        }
        Posts.Sort();
        Arrange(campaign, nation, war, line);
    }

    private static bool Holds(Campaign campaign, Army army, Nation nation, StrategicWar war, DrawnFrontLine line, bool advances)
    {
        if (army.NationId != nation.Id) return false;
        if (army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 || army.DirectOrder) return false;
        if (army.TemplateId == MilitiaCore.TemplateId && army.OrderKind == ArmyOrder.Manual && army.AssignedWarId < 0)
        {
            // Local guards may plug gaps inside their own region without being marked as
            // detached expeditionary militia (EmergencyFieldArmy). No player commandeering.
            if (line is not null || war is null || army.FrontLineId >= 0 ||
                campaign.IsHumanControlled(nation) && !Delegation.Holds(nation, Delegation.Command)) return false;
            var region = campaign.ProvinceMap.ProvinceToState[army.ProvinceId];
            return PostStates.Contains(region);
        }
        var duty = army.OrderKind == ArmyOrder.DefendFront ||
                   (advances && army.OrderKind == ArmyOrder.AdvanceFront);
        if (!duty) return false;
        if (line is not null) return army.FrontLineId == line.Id;
        return army.FrontLineId < 0 && war is not null && army.AssignedWarId == war.Id;
    }

    private static void Arrange(Campaign campaign, Nation nation, StrategicWar war, DrawnFrontLine line)
    {
        using var measured = SimProfiler.Measure("    staff:arrange");
        Active.Reserves.Clear();
        Active.ReservesReady = false;
        if (Posts.Count == 0) return;
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var defines = campaign.Defines;

        PostSet.Clear();
        PostStates.Clear();
        var provinceToState = topology.ProvinceToState;
        for (var i = 0; i < Posts.Count; i++)
        {
            var post = Posts[i];
            PostSet.Add(post);
            PostStates.Add(provinceToState[post]);
        }
        // The lists are kept between fronts rather than thrown away: this runs for every
        // country at war every day, and a fresh list per post would be the staff's garbage.
        foreach (var list in Allocated.Values) { list.Clear(); Spare.Push(list); }
        Allocated.Clear();
        Loose.Clear();
        BattleCandidates.Clear();
        DonorComponents.Clear();

        // Who is holding what: a division counts for the post it is going to, or the post it
        // is standing on when what it is going to is not a post - an advance's objective is
        // the enemy's province, and the ground it attacks from is the post it holds.
        var ownArmies = NationArmies(campaign, nation.Id);
        var holders = 0;
        var power = 0f;
        var reserves = Active.Reserves;
        reserves.Clear();
        var ownOccupied = Active.OwnOccupied;
        ownOccupied.Clear();
        Army pocket = null;
        var multiStackPosts = 0;
        for (var i = 0; i < ownArmies.Count; i++)
        {
            var army = ownArmies[i];
            if (army.NationId == nation.Id && army.Divisions >= 0.1f)
                ownOccupied.Add(army.ProvinceId);
            if (war is not null)
            {
                if (army.InBattle && FrontDisengagement.Eligible(campaign, nation, war, line, army))
                    BattleCandidates.Add(army);
                if (army.NationId == nation.Id && army.AssignedWarId == war.Id)
                {
                    if (army.OrderKind is (ArmyOrder.ReserveFront or ArmyOrder.AdvanceFront) &&
                        army.TemplateId != MilitiaCore.TemplateId)
                        reserves.Add(army);
                    if (army.Divisions > 0f && army.EmbarkedFleetId < 0 && army.ProvinceId > 0 &&
                        (army.DaysCutOff > 0 || army.SupplyDistance < 0) &&
                        (pocket is null || army.DaysCutOff > pocket.DaysCutOff ||
                         (army.DaysCutOff == pocket.DaysCutOff && army.SupplyDistance < pocket.SupplyDistance) ||
                         (army.DaysCutOff == pocket.DaysCutOff && army.SupplyDistance == pocket.SupplyDistance && army.Id < pocket.Id)))
                        pocket = army;
                }
            }
            if (!Holds(campaign, army, nation, war, line, defines.FrontStaffMovesAdvances)) continue;
            holders++;
            power += army.Power;
            // An assignment without a march is not cover: a failed route can leave a
            // trapped formation's target on the other side of a neutral/enemy corridor.
            // Count real positions or columns actually marching to their assigned post.
            var inbound = army.Route.Count > 0 && army.Route[^1] == army.OrderProvinceId;
            var post = inbound && PostSet.Contains(army.OrderProvinceId) ? army.OrderProvinceId
                : PostSet.Contains(army.ProvinceId) ? army.ProvinceId : -1;
            if (post < 0) { if (!army.InBattle) Loose.Add(army); continue; }
            if (!Allocated.TryGetValue(post, out var there)) Allocated[post] = there = Take();
            there.Add(army);
            if (there.Count == 2) multiStackPosts++;
        }
        Active.ReservesReady = war is not null;
        var today = campaign.ElapsedDays;
        var mayBorrow = war is not null && line is null &&
            (!campaign.IsHumanControlled(nation) || Delegation.Holds(nation, Delegation.Command));
        var referenceCount = holders;
        if (holders == 0)
        {
            if (!mayBorrow && BattleCandidates.Count == 0) return;
            // An empty line is precisely when a ready reserve is needed. There may be no
            // defender yet from which to estimate a division's power, so use the available
            // field formations, without counting them as holders until actually posted.
            for (var i = 0; i < ownArmies.Count; i++)
            {
                var army = ownArmies[i];
                if (!ReadyReserve(campaign, army, nation, war, today, advancesToo: true) &&
                    !FrontDisengagement.Eligible(campaign, nation, war, line, army)) continue;
                referenceCount++;
                power += army.Power;
            }
            if (referenceCount == 0) return;
        }

        // What each post is up against, in divisions: one for being on the front at all, and
        // one more for every division's worth of the enemy standing next to it.
        var typical = MathF.Max(0.01f, power / referenceCount);
        Need.Clear();
        var totalNeed = 0f;
        for (var pi = 0; pi < Posts.Count; pi++)
        {
            var post = Posts[pi];
            var pressure = 0f;
            foreach (var neighbour in topology.Neighbors[post])
                pressure += EnemyPower.GetValueOrDefault(neighbour);
            var homeImportance = topology.ProvinceToNation[post] == nation.MapIndex
                ? StrategicExclaves.Importance(campaign, post) : 0f;
            var need = (1f + pressure / typical) *
                (1f + homeImportance * M.Clamp(defines.ExclaveDefenceWeight, 0f, 2f));
            Need[post] = need;
            totalNeed += need;
        }

        var budget = defines.FrontRepostsPerLook + (int)(Posts.Count * defines.FrontRepostsPerPost);
        var quota = (int)MathF.Ceiling(holders / (float)Math.Max(1, Posts.Count));

        // One re-posting, before the threatened holes: a free reserve onto the empty post that
        // reconnects a cut-off formation. The pocket itself is left where it is.
        ReliefNoAdvance.Clear();
        RelieveCutOff(campaign, nation, war, today, mayBorrow, pocket, ref budget, ref holders, ref multiStackPosts);
        quota = (int)MathF.Ceiling(holders / (float)Math.Max(1, Posts.Count));

        // First the holes, the most threatened first.
        var ranked = Active.RankedPosts;
        ranked.Clear();
        for (var pi = 0; pi < Posts.Count; pi++)
        {
            var p = Posts[pi];
            ranked.Add((p, Need[p]));
        }
        ranked.Sort(static (left, right) =>
        {
            var order = right.Need.CompareTo(left.Need);
            return order != 0 ? order : left.Post.CompareTo(right.Post);
        });
        for (var pi = 0; pi < ranked.Count; pi++) Posts[pi] = ranked[pi].Post;
        FrontDisengagement.SelectionContext disengagement = null;
        try
        {
        for (var hi = 0; hi < Posts.Count; hi++)
        {
            var hole = Posts[hi];
            if (budget <= 0) break;
            if (Count(hole) > 0) continue;
            // The nearest donor may be in a pocket. After one route failure, refuse the
            // whole disconnected component for this hole: trying the same four trapped
            // divisions every day otherwise hides a reachable fifth division forever.
            Refused.Clear();
            var connectivityChecked = false;
            var filled = false;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var mover = Donor(campaign, hole, quota, today, allowThinning: holders < Posts.Count, multiStackPosts > 0);
                if (mover is null) break;
                if (Repost(campaign, nation, mover, hole, ref multiStackPosts)) { budget--; filled = true; break; }
                Refused.Add(mover.Id);
                if (!connectivityChecked)
                {
                    RefuseDisconnectedDonors(campaign, nation, hole);
                    connectivityChecked = true;
                }
            }
            // Nobody on the line to spare: the reserve is what a reserve is for. A hole the enemy
            // can walk into is filled from the nearest division held back for this war before
            // the reserve is allowed to stand as a pile behind it - Ukraine kept five divisions
            // in reserve in one province with five posts of its line held by nobody.
            // Only a staff the country has handed its army to: a player's reserve and advances
            // are the player's orders, and the staff does not rewrite them.
            if (!filled && mayBorrow)
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    // A free reserve fills the hole before anyone is taken off an attack.
                    // Spreading advances along the front stays off (FrontStaffMovesAdvances):
                    // that measurement cost ground. Only a hole the enemy is already standing
                    // next to may borrow one advance, and only after no reserve could take it.
                    var borrowedAdvance = false;
                    var reserve = NearestReserve(campaign, nation, war, hole, today, advancesToo: false);
                    // The post that would reopen a pocket already had its reserve. No attack is
                    // pulled off the line to stand there. Other holes keep the threatened-hole rule.
                    if (reserve is null && !ReliefNoAdvance.Contains(hole) &&
                        Need.GetValueOrDefault(hole, 1f) >= campaign.Defines.FrontHoleAdvanceNeed)
                    {
                        reserve = NearestReserve(campaign, nation, war, hole, today, advancesToo: true);
                        borrowedAdvance = reserve is not null && reserve.OrderKind == ArmyOrder.AdvanceFront;
                    }
                    if (reserve is null) break;
                    var duty = reserve.OrderKind;
                    reserve.OrderKind = ArmyOrder.DefendFront;
                    if (Repost(campaign, nation, reserve, hole, ref multiStackPosts))
                    {
                        reserve.StaffNote = borrowedAdvance
                            ? StaffNote.ThreatenedHole
                            : StaffNote.EmptyPostFromReserve;
                        budget--; holders++; filled = true; break;
                    }
                    reserve.OrderKind = duty;
                    Refused.Add(reserve.Id);
                    if (!connectivityChecked)
                    {
                        RefuseDisconnectedDonors(campaign, nation, hole);
                        connectivityChecked = true;
                    }
                }
            if (!filled && war is not null && BattleCandidates.Count > 0 && !ownOccupied.Contains(hole))
            {
                if (FrontDisengagement.TryCoverHole(campaign, nation, war, line, hole, out var detached, ref disengagement, BattleCandidates))
                {
                    ownOccupied.Add(detached.ProvinceId);
                    var alreadyCounted =
                        (Allocated.TryGetValue(detached.OrderProvinceId, out var atOrder) && RemoveFromAllocated(atOrder, detached, ref multiStackPosts)) ||
                        (detached.ProvinceId != detached.OrderProvinceId && Allocated.TryGetValue(detached.ProvinceId, out var atHere) && RemoveFromAllocated(atHere, detached, ref multiStackPosts));
                    if (!alreadyCounted)
                    {
                        foreach (var units in Allocated.Values)
                            if (RemoveFromAllocated(units, detached, ref multiStackPosts)) { alreadyCounted = true; break; }
                    }
                    if (!Allocated.TryGetValue(hole, out var there)) Allocated[hole] = there = Take();
                    there.Add(detached);
                    if (there.Count == 2) multiStackPosts++;
                    budget--; Reposts++; if (!alreadyCounted) holders++; filled = true;
                }
            }
        }

        }
        finally { disengagement?.Dispose(); }

        // Then the weight of the line. Each post's share of the divisions is its share of the
        // pressure - a post with an enemy corps next to it wants more than one facing an empty
        // field - and a post holding a division more than its share gives it to whichever post
        // is furthest short of its own. A pile gives first, because a pile's surplus is largest.
        var minimum = holders >= Posts.Count ? 1f : 0f;
        while (budget > 0)
        {
            var richest = -1;
            var richestSurplus = 0f;
            var poorest = -1;
            var poorestShortfall = 0f;
            for (var pi = 0; pi < Posts.Count; pi++)
            {
                var post = Posts[pi];
                var wanted = MathF.Max(minimum, holders * Need[post] / totalNeed);
                var have = Count(post);
                var surplus = have - wanted;
                if (have >= 2 && surplus >= 1f && surplus > richestSurplus && Movable(post) is not null)
                {
                    richestSurplus = surplus;
                    richest = post;
                }
                var shortfall = wanted - have;
                if (shortfall > poorestShortfall + 0.0001f)
                {
                    poorestShortfall = shortfall;
                    poorest = post;
                }
            }
            if (richest < 0 || poorest < 0 || poorestShortfall < 0.75f) break;
            // A division that has just been posted stays unless the post it is on is well over.
            Refused.Clear();
            var mover = Nearest(Allocated[richest], poorest, campaign, today, defines,
                allowSettled: richestSurplus >= 2f);
            if (mover is null || !Repost(campaign, nation, mover, poorest, ref multiStackPosts)) break;
            budget--;
        }
    }

    /// <summary>
    /// One existing re-posting onto the empty post that reconnects a cut-off formation.
    /// The formation in the pocket is not disbanded and is not the one sent. A free reserve
    /// goes first. If none can reach, the post is marked so this look does not pull an advance
    /// or a spearhead onto it; a neighbour already holding this war may still walk there by
    /// the ordinary donor pass. A human who has not handed over command is not given the move.
    /// </summary>
    private static void RelieveCutOff(Campaign campaign, Nation nation, StrategicWar war, int today,
        bool mayBorrow, Army pocket, ref int budget, ref int holders, ref int multiStackPosts)
    {
        if (!mayBorrow || budget <= 0 || war is null || !campaign.Defines.FrontStaffRelievesPockets) return;
        if (pocket is null) return;
        var post = ReconnectingPost(campaign, nation, war, pocket.ProvinceId);
        if (post < 0 || Count(post) > 0) return;
        Refused.Clear();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var reserve = NearestReserve(campaign, nation, war, post, today, advancesToo: false);
            if (reserve is null) break;
            if (reserve.Id == pocket.Id || reserve.DaysCutOff > 0 || reserve.SupplyDistance < 0)
            {
                Refused.Add(reserve.Id);
                continue;
            }
            var duty = reserve.OrderKind;
            reserve.OrderKind = ArmyOrder.DefendFront;
            if (Repost(campaign, nation, reserve, post, ref multiStackPosts))
            {
                reserve.StaffNote = StaffNote.ReopenRoad;
                budget--;
                holders++;
                return;
            }
            reserve.OrderKind = duty;
            Refused.Add(reserve.Id);
        }
        ReliefNoAdvance.Add(post);
    }

    /// <summary>
    /// The empty front post on the near side of the gap: the friendly province the existing
    /// breakout walk reaches outside the pocket, when that province is an empty post, otherwise
    /// the empty post beside it. The pocket's own province is not a post to "fill".
    /// </summary>
    private static int ReconnectingPost(Campaign campaign, Nation nation, StrategicWar war, int pocketProvince)
    {
        var path = PocketBreakout.WayHome(campaign, nation, pocketProvince, war);
        if (path is not null)
        {
            for (var i = path.Count - 1; i >= 0; i--)
            {
                var step = path[i];
                if (step == pocketProvince) continue;
                if (PostSet.Contains(step) && Count(step) == 0) return step;
            }
            if (path.Count > 0)
            {
                var beside = EmptyPostBeside(campaign, path[^1], pocketProvince);
                if (beside > 0) return beside;
            }
        }
        return EmptyPostBeside(campaign, pocketProvince, pocketProvince);
    }

    private static int EmptyPostBeside(Campaign campaign, int province, int pocketProvince)
    {
        if (province <= 0 || province >= campaign.ProvinceMap.Neighbors.Length) return -1;
        var best = -1;
        foreach (var next in campaign.ProvinceMap.Neighbors[province])
        {
            if (next == pocketProvince || !PostSet.Contains(next) || Count(next) > 0) continue;
            if (best < 0 || next < best) best = next;
        }
        return best;
    }

    private static int Count(int post) => Allocated.TryGetValue(post, out var there) ? there.Count : 0;

    /// <summary>
    /// A failed route reveals a disconnected pocket, not a reason to test every division
    /// inside it. Components are labelled lazily, visiting each friendly land province at
    /// most once in this arrangement; a successful first donor needs no extra search. The
    /// labels belong to this campaign and are cleared at its next arrangement, so a
    /// captured or reopened corridor is not treated as still open on the next look.
    /// </summary>
    private static void RefuseDisconnectedDonors(Campaign campaign, Nation nation, int hole)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        DonorFrontier.Clear();
        if (!DonorComponents.TryGetValue(hole, out var component) && !topology.IsWater(hole))
        {
            component = hole;
            DonorComponents.Add(hole, component);
            DonorFrontier.Enqueue(hole);
        }
        while (DonorFrontier.Count > 0)
        {
            var current = DonorFrontier.Dequeue();
            foreach (var next in topology.Neighbors[current])
            {
                if (controllers[next] != nation.MapIndex || topology.IsWater(next) ||
                    !DonorComponents.TryAdd(next, component)) continue;
                DonorFrontier.Enqueue(next);
            }
        }
        var own = NationArmies(campaign, nation.Id);
        for (var i = 0; i < own.Count; i++)
        {
            var army = own[i];
            if (army.NationId == nation.Id &&
                (!DonorComponents.TryGetValue(army.ProvinceId, out var source) || source != component))
                Refused.Add(army.Id);
        }
    }

    private static bool ReadyReserve(Campaign campaign, Army army, Nation nation, StrategicWar war, int today,
        bool advancesToo)
    {
        if (army.NationId != nation.Id || army.AssignedWarId != war.Id) return false;
        if (army.OrderKind != ArmyOrder.ReserveFront && !(advancesToo && army.OrderKind == ArmyOrder.AdvanceFront)) return false;
        if (army.FrontLineId >= 0 || army.InBattle || army.DirectOrder || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0) return false;
        if (army.TemplateId == MilitiaCore.TemplateId) return false;
        if (today - army.OrderIssuedDay < campaign.Defines.FrontSettleDays) return false;
        // Only formations standing by: a column already on its way is left to arrive.
        return army.Route.Count == 0;
    }

    /// <summary>The nearest division of this country held in reserve for this war that is free to go.</summary>
    private static Army NearestReserve(Campaign campaign, Nation nation, StrategicWar war, int hole, int today,
        bool advancesToo = false)
    {
        var target = campaign.ProvinceMap.CenterOf(hole);
        Army best = null;
        var bestDistance = float.MaxValue;
        var reserves = ReserveCandidates(campaign, nation, war);
        for (var i = 0; i < reserves.Count; i++)
        {
            var army = reserves[i];
            if (!ReadyReserve(campaign, army, nation, war, today, advancesToo) || Refused.Contains(army.Id)) continue;
            // A reserve before an advance at the same distance.
            var distance = NavalOperations.Distance(army.Latitude, army.Longitude, target.Latitude, target.Longitude) *
                           (army.OrderKind == ArmyOrder.AdvanceFront ? 1.5f : 1f);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = army;
        }
        return best;
    }

    private static List<Army> ReserveCandidates(Campaign campaign, Nation nation, StrategicWar war)
    {
        var reserves = Active.Reserves;
        if (Active.ReservesReady) return reserves;
        reserves.Clear();
        var own = NationArmies(campaign, nation.Id);
        for (var i = 0; i < own.Count; i++)
        {
            var army = own[i];
            if (army.NationId != nation.Id || army.AssignedWarId != war.Id ||
                army.OrderKind is not (ArmyOrder.ReserveFront or ArmyOrder.AdvanceFront) ||
                army.TemplateId == MilitiaCore.TemplateId) continue;
            reserves.Add(army);
        }
        Active.ReservesReady = true;
        return reserves;
    }

    private static List<Army> Take() => Spare.Count > 0 ? Spare.Pop() : new List<Army>();

    private static Army Movable(int post)
    {
        if (!Allocated.TryGetValue(post, out var there)) return null;
        var span = CollectionsMarshal.AsSpan(there);
        for (var i = 0; i < span.Length; i++)
            if (!span[i].InBattle) return span[i];
        return null;
    }

    /// <summary>
    /// Who fills a hole: a division that belongs to no post first, then the nearest one on a
    /// post holding more than its share, and - when there are fewer divisions than posts -
    /// the one on the quietest post, if the hole is facing a great deal more.
    /// </summary>
    private static Army Donor(Campaign campaign, int hole, int quota, int today, bool allowThinning, bool hasMultiStack)
    {
        var defines = campaign.Defines;
        var best = Nearest(Loose, hole, campaign, today, defines, allowSettled: true);
        if (best is not null) return best;

        if (hasMultiStack)
        {
            var bestDistance = float.MaxValue;
            var target = campaign.ProvinceMap.CenterOf(hole);
            foreach (var (post, there) in Allocated)
            {
                if (there.Count < 2) continue;
                // A post more than its share may give up even a division it has just been given.
                var crowded = there.Count > quota;
                var span = CollectionsMarshal.AsSpan(there);
                for (var i = 0; i < span.Length; i++)
                {
                    var army = span[i];
                    if (army.InBattle || Refused.Contains(army.Id) || !MayRepost(campaign, army, hole)) continue;
                    if (!crowded && today - army.OrderIssuedDay < defines.FrontSettleDays) continue;
                    var distance = NavalOperations.Distance(army.Latitude, army.Longitude, target.Latitude, target.Longitude) *
                                   (army.OrderKind == ArmyOrder.AdvanceFront ? 1.5f : 1f);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = army;
                }
            }
        }
        if (best is not null || !allowThinning) return best;

        // Fewer divisions than posts: a quiet post held by one is worth less than a hole the
        // enemy is standing next to.
        var holeNeed = Need.GetValueOrDefault(hole, 1f);
        var quietest = float.MaxValue;
        foreach (var (post, there) in Allocated)
        {
            if (there.Count != 1 || there[0].InBattle || Refused.Contains(there[0].Id) ||
                !MayRepost(campaign, there[0], hole)) continue;
            var need = Need.GetValueOrDefault(post, 1f);
            if (holeNeed < need * defines.FrontThinQuietRatio) continue;
            if (today - there[0].OrderIssuedDay < defines.FrontSettleDays) continue;
            if (need >= quietest) continue;
            quietest = need;
            best = there[0];
        }
        return best;
    }

    private static Army Nearest(List<Army> candidates, int post, Campaign campaign, int today, Defines defines,
        bool allowSettled)
    {
        Army best = null;
        var bestDistance = float.MaxValue;
        var target = campaign.ProvinceMap.CenterOf(post);
        var span = CollectionsMarshal.AsSpan(candidates);
        for (var i = 0; i < span.Length; i++)
        {
            var army = span[i];
            if (army.InBattle || Refused.Contains(army.Id) || !MayRepost(campaign, army, post)) continue;
            if (!allowSettled && today - army.OrderIssuedDay < defines.FrontSettleDays) continue;
            var distance = NavalOperations.Distance(army.Latitude, army.Longitude, target.Latitude, target.Longitude);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = army;
        }
        return best;
    }

    /// <summary>
    /// Whether a division may be given a new post. One standing on its post, or not going
    /// anywhere, may. One still marching to its post is diverted only to a post well nearer
    /// than the one it is walking to - a gap it is passing - and otherwise left to arrive:
    /// turning a column round halfway is the "sent somewhere new before it got anywhere" an
    /// army nobody commands is made of, and a pile it arrives at is given out from there.
    /// </summary>
    private static bool MayRepost(Campaign campaign, Army army, int post)
    {
        if (army.TemplateId == MilitiaCore.TemplateId && army.AssignedWarId < 0 &&
            campaign.ProvinceMap.ProvinceToState[army.ProvinceId] != campaign.ProvinceMap.ProvinceToState[post]) return false;
        if (army.Route.Count == 0 || army.OrderProvinceId <= 0 || army.ProvinceId == army.OrderProvinceId) return true;
        var map = campaign.ProvinceMap;
        var going = map.CenterOf(army.OrderProvinceId);
        var there = map.CenterOf(post);
        return NavalOperations.Distance(army.Latitude, army.Longitude, there.Latitude, there.Longitude) <
               0.6f * NavalOperations.Distance(army.Latitude, army.Longitude, going.Latitude, going.Longitude);
    }

    private static bool RemoveFromAllocated(List<Army> list, Army army, ref int multiStackPosts)
    {
        var hadTwo = list.Count == 2;
        if (!list.Remove(army)) return false;
        if (hadTwo) multiStackPosts--;
        return true;
    }

    private static bool TryFindRoute(ProvinceTopology topology, int startProvince, int targetProvince,
        ushort nationMapIndex, ushort[] controllers, List<int> destination)
    {
        destination.Clear();
        var neighbors = topology.Neighbors;
        if (startProvince <= 0 || targetProvince <= 0 ||
            startProvince >= neighbors.Length || targetProvince >= neighbors.Length ||
            controllers is null || controllers.Length < neighbors.Length ||
            controllers[startProvince] != nationMapIndex ||
            controllers[targetProvince] != nationMapIndex)
            return false;
        if (startProvince == targetProvince) return true;
        if (topology.IsWater(startProvince) || topology.IsWater(targetProvince)) return false;

        var board = Active;
        if (board.RoutePrevious.Length < neighbors.Length)
        {
            board.RoutePrevious = new int[neighbors.Length];
            board.RouteStamp = new int[neighbors.Length];
            board.RouteGeneration = 0;
        }
        else if (board.RouteGeneration == int.MaxValue)
        {
            Array.Clear(board.RouteStamp, 0, board.RouteStamp.Length);
            board.RouteGeneration = 0;
        }
        var generation = ++board.RouteGeneration;
        var previous = board.RoutePrevious;
        var stamp = board.RouteStamp;
        var queue = board.RouteFrontier;
        queue.Clear();
        stamp[startProvince] = generation;
        previous[startProvince] = startProvince;
        queue.Enqueue(startProvince);

        while (queue.Count > 0 && stamp[targetProvince] != generation)
        {
            var current = queue.Dequeue();
            var adj = neighbors[current];
            for (var i = 0; i < adj.Length; i++)
            {
                var next = adj[i];
                if (stamp[next] == generation || controllers[next] != nationMapIndex || topology.IsWater(next)) continue;
                stamp[next] = generation;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }

        if (stamp[targetProvince] != generation) return false;
        for (var cursor = targetProvince; cursor != startProvince; cursor = previous[cursor])
            destination.Add(cursor);
        destination.Reverse();
        return true;
    }

    /// <summary>
    /// Send one division to one post, keeping everything else about it: its duty, its war, its
    /// line, its plan. A march already under way in the same direction keeps its progress.
    /// </summary>
    private static bool Repost(Campaign campaign, Nation nation, Army army, int post, ref int multiStackPosts)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var route = Active.RouteScratch;
        route.Clear();
        // Battle withdrawals require a preflight and covering force; they use
        // FrontDisengagement, not this ordinary marching path.
        if (army.InBattle) return false;
        if (army.ProvinceId != post)
        {
            if (!TryFindRoute(topology, army.ProvinceId, post, (ushort)nation.MapIndex, controllers, route))
                return false;
            // Ground no road of ours reaches - an exclave, an island - is not a post this
            // division can take.
            if (route.Count == 0) return false;
            if (army.TemplateId == MilitiaCore.TemplateId && army.AssignedWarId < 0)
                for (var i = 0; i < route.Count; i++)
                    if (topology.ProvinceToState[route[i]] != topology.ProvinceToState[army.ProvinceId]) return false;
        }

        var sameWay = army.Route.Count > 0 && route.Count > 0 && army.Route[0] == route[0];
        if (Allocated.TryGetValue(army.OrderProvinceId, out var old) && RemoveFromAllocated(old, army, ref multiStackPosts)) { }
        else if (Allocated.TryGetValue(army.ProvinceId, out var standing)) RemoveFromAllocated(standing, army, ref multiStackPosts);
        Loose.Remove(army);
        if (!Allocated.TryGetValue(post, out var there)) Allocated[post] = there = Take();
        there.Add(army);
        if (there.Count == 2) multiStackPosts++;

        army.Route.Clear();
        army.Route.AddRange(route);
        army.PlannedRoute.Clear();
        army.PlannedRoute.AddRange(route);
        if (!sameWay) army.MoveProgress = 0f;
        army.OrderProvinceId = post;
        army.OrderStateMapIndex = topology.ProvinceToState[post];
        army.OrderIssuedDay = campaign.ElapsedDays;
        army.OrderRouteSteps = route.Count;
        army.OrderStepsCompleted = 0;
        Reposts++;
        return true;
    }
}
