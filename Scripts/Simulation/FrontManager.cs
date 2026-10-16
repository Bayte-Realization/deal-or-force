using System;
using System.Collections.Generic;

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
/// divisions in a battle, on a player's own direct order, or at sea.
/// </summary>
public static class FrontManager
{
    private static readonly Dictionary<int, float> EnemyPower = new();
    private static readonly Dictionary<int, List<Army>> Allocated = new();
    private static readonly Stack<List<Army>> Spare = new();
    private static readonly List<Army> Loose = new();
    private static readonly List<int> Posts = new();
    private static readonly HashSet<int> PostSet = new();
    private static readonly Dictionary<int, float> Need = new();
    private static readonly HashSet<int> Refused = new();
    private static readonly Dictionary<int, int> DonorComponents = new();
    private static readonly Queue<int> DonorFrontier = new();

    /// <summary>Re-postings made since the campaign began, for the bench and the console.</summary>
    public static int Reposts { get; private set; }

    /// <summary>Look at every front in the world.</summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(FrontManager))) return;
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) return;
        CallUpIdle(campaign);
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            Balance(campaign, war);
        }
        foreach (var line in campaign.FrontLines)
        {
            if (line.Kind is FrontLineKind.Defence or FrontLineKind.AreaDefence) BalanceLine(campaign, line);
            else if (line.Kind == FrontLineKind.Fallback) DefensivePlanning.Maintain(campaign, line);
        }
    }

    private static readonly List<Army> Idle = new();

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
        foreach (var army in campaign.Armies)
        {
            if (army.OrderKind != ArmyOrder.Manual || army.AssignedWarId >= 0 || army.DirectOrder) continue;
            if (army.InBattle || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 || army.Route.Count > 0) continue;
            if (army.TemplateId == MilitiaCore.TemplateId || army.FrontLineId >= 0) continue;
            Idle.Add(army);
        }
        foreach (var army in Idle)
        {
            var nation = campaign.NationById(army.NationId);
            // A player's divisions stay where they were put, unless the general staff in the
            // field was handed over. Then the same call-up every other country gets applies.
            if (nation is null || (nation.IsHuman && !Delegation.Holds(nation, Delegation.Command))) continue;
            var atWar = false;
            foreach (var war in campaign.Wars)
                if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
            if (!atWar) continue;
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
        for (var side = 0; side < 2; side++)
        {
            var attacking = side == 0;
            // Who is on the other side, and where they stand: the pressure each post is under.
            EnemyPower.Clear();
            foreach (var army in campaign.Armies)
            {
                if (army.ProvinceId <= 0 || army.EmbarkedFleetId >= 0) continue;
                if (attacking ? !war.IsDefending(army.NationId) : !war.IsAttacking(army.NationId)) continue;
                EnemyPower[army.ProvinceId] = EnemyPower.GetValueOrDefault(army.ProvinceId) + army.Power;
            }
            foreach (var nationId in war.Side(attacking))
            {
                var nation = campaign.NationById(nationId);
                if (nation is null) continue;
                // Its own sector: a coalition's front runs past every member, and a division
                // cannot march through an ally's country to stand on its border.
                Posts.Clear();
                foreach (var province in war.FriendlyFront(nationId))
                    if (campaign.ProvinceController[province] == (ushort)nation.MapIndex) Posts.Add(province);
                Posts.Sort();
                Arrange(campaign, nation, war, null);
            }
        }
    }

    /// <summary>One drawn line, held by the divisions put on it.</summary>
    private static void BalanceLine(Campaign campaign, DrawnFrontLine line)
    {
        var nation = campaign.NationById(line.NationId);
        if (nation is null || line.IsEmpty) return;
        var war = campaign.Wars.Find(candidate => !candidate.Ended && candidate.Involves(nation.Id));
        EnemyPower.Clear();
        foreach (var army in campaign.Armies)
        {
            if (army.ProvinceId <= 0 || army.EmbarkedFleetId >= 0 || army.NationId == nation.Id) continue;
            if (war is null || !war.Opposes(army.NationId, nation.Id)) continue;
            EnemyPower[army.ProvinceId] = EnemyPower.GetValueOrDefault(army.ProvinceId) + army.Power;
        }
        Posts.Clear();
        foreach (var province in BattlePlanning.Sequence(campaign, line))
            if (province > 0 && province < campaign.ProvinceController.Length &&
                campaign.ProvinceController[province] == (ushort)nation.MapIndex) Posts.Add(province);
        Arrange(campaign, nation, war, line);
    }

    private static bool Holds(Army army, Nation nation, StrategicWar war, DrawnFrontLine line, bool advances)
    {
        if (army.NationId != nation.Id || army.TemplateId == MilitiaCore.TemplateId) return false;
        if (army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 || army.DirectOrder) return false;
        var duty = army.OrderKind == ArmyOrder.DefendFront || (advances && army.OrderKind == ArmyOrder.AdvanceFront);
        if (!duty) return false;
        if (line is not null) return army.FrontLineId == line.Id;
        return army.FrontLineId < 0 && war is not null && army.AssignedWarId == war.Id;
    }

    private static void Arrange(Campaign campaign, Nation nation, StrategicWar war, DrawnFrontLine line)
    {
        if (Posts.Count == 0) return;
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var defines = campaign.Defines;

        PostSet.Clear();
        foreach (var post in Posts) PostSet.Add(post);
        // The lists are kept between fronts rather than thrown away: this runs for every
        // country at war every day, and a fresh list per post would be the staff's garbage.
        foreach (var list in Allocated.Values) { list.Clear(); Spare.Push(list); }
        Allocated.Clear();
        Loose.Clear();
        DonorComponents.Clear();

        // Who is holding what: a division counts for the post it is going to, or the post it
        // is standing on when what it is going to is not a post - an advance's objective is
        // the enemy's province, and the ground it attacks from is the post it holds.
        var holders = 0;
        var power = 0f;
        foreach (var army in campaign.Armies)
        {
            if (!Holds(army, nation, war, line, defines.FrontStaffMovesAdvances)) continue;
            holders++;
            power += army.Power;
            var post = PostSet.Contains(army.OrderProvinceId) ? army.OrderProvinceId
                : PostSet.Contains(army.ProvinceId) ? army.ProvinceId : -1;
            if (post < 0) { if (!army.InBattle) Loose.Add(army); continue; }
            if (!Allocated.TryGetValue(post, out var there)) Allocated[post] = there = Take();
            there.Add(army);
        }
        var today = campaign.ElapsedDays;
        var mayBorrow = war is not null && line is null &&
            (!nation.IsHuman || Delegation.Holds(nation, Delegation.Command));
        var referenceCount = holders;
        if (holders == 0)
        {
            if (!mayBorrow) return;
            // An empty line is precisely when a ready reserve is needed. There may be no
            // defender yet from which to estimate a division's power, so use the available
            // field formations, without counting them as holders until actually posted.
            foreach (var army in campaign.Armies)
            {
                if (!ReadyReserve(campaign, army, nation, war, today, advancesToo: true)) continue;
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
        foreach (var post in Posts)
        {
            var pressure = 0f;
            foreach (var neighbour in topology.Neighbors[post])
                pressure += EnemyPower.GetValueOrDefault(neighbour);
            var need = 1f + pressure / typical;
            Need[post] = need;
            totalNeed += need;
        }

        var budget = defines.FrontRepostsPerLook + (int)(Posts.Count * defines.FrontRepostsPerPost);
        var quota = (int)MathF.Ceiling(holders / (float)Posts.Count);

        // First the holes, the most threatened first.
        Posts.Sort((left, right) =>
        {
            var order = Need[right].CompareTo(Need[left]);
            return order != 0 ? order : left.CompareTo(right);
        });
        foreach (var hole in Posts)
        {
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
                var mover = Donor(campaign, hole, quota, today, allowThinning: holders < Posts.Count);
                if (mover is null) break;
                if (Repost(campaign, nation, mover, hole)) { budget--; filled = true; break; }
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
                    // A hole with the enemy standing next to it may be closed by an advance as
                    // well, one division of it - an attack that leaves the door open behind it
                    // is not worth the ground it takes. Only for holes: the advances as a whole
                    // stay where the planner massed them (FrontStaffMovesAdvances).
                    var threatened = Need.GetValueOrDefault(hole, 1f) >= campaign.Defines.FrontHoleAdvanceNeed;
                    var reserve = NearestReserve(campaign, nation, war, hole, today, threatened);
                    if (reserve is null) break;
                    var duty = reserve.OrderKind;
                    reserve.OrderKind = ArmyOrder.DefendFront;
                    if (Repost(campaign, nation, reserve, hole)) { budget--; holders++; break; }
                    reserve.OrderKind = duty;
                    Refused.Add(reserve.Id);
                    if (!connectivityChecked)
                    {
                        RefuseDisconnectedDonors(campaign, nation, hole);
                        connectivityChecked = true;
                    }
                }
        }

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
            foreach (var post in Posts)
            {
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
            if (mover is null || !Repost(campaign, nation, mover, poorest)) break;
            budget--;
        }
    }

    private static int Count(int post) => Allocated.TryGetValue(post, out var there) ? there.Count : 0;

    /// <summary>
    /// A failed route reveals a disconnected pocket, not a reason to test every division
    /// inside it. Components are labelled lazily, visiting each friendly land province at
    /// most once in this arrangement; a successful first donor needs no extra search. The
    /// scratch is cleared at the next arrangement, so captured or reopened corridors and
    /// other campaigns cannot reuse stale controller information.
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
        foreach (var army in campaign.Armies)
            if (army.NationId == nation.Id &&
                (!DonorComponents.TryGetValue(army.ProvinceId, out var source) || source != component))
                Refused.Add(army.Id);
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
        foreach (var army in campaign.Armies)
        {
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

    private static List<Army> Take() => Spare.Count > 0 ? Spare.Pop() : new List<Army>();

    private static Army Movable(int post)
    {
        if (!Allocated.TryGetValue(post, out var there)) return null;
        foreach (var army in there)
            if (!army.InBattle) return army;
        return null;
    }

    /// <summary>
    /// Who fills a hole: a division that belongs to no post first, then the nearest one on a
    /// post holding more than its share, and - when there are fewer divisions than posts -
    /// the one on the quietest post, if the hole is facing a great deal more.
    /// </summary>
    private static Army Donor(Campaign campaign, int hole, int quota, int today, bool allowThinning)
    {
        var defines = campaign.Defines;
        var best = Nearest(Loose, hole, campaign, today, defines, allowSettled: true);
        if (best is not null) return best;

        var bestDistance = float.MaxValue;
        var target = campaign.ProvinceMap.CenterOf(hole);
        foreach (var (post, there) in Allocated)
        {
            if (there.Count < 2) continue;
            // A post more than its share may give up even a division it has just been given.
            var crowded = there.Count > quota;
            foreach (var army in there)
            {
                if (army.InBattle || Refused.Contains(army.Id) || !MayRepost(campaign, army, hole)) continue;
                if (!crowded && today - army.OrderIssuedDay < defines.FrontSettleDays) continue;
                var distance = NavalOperations.Distance(army.Latitude, army.Longitude, target.Latitude, target.Longitude) *
                               (army.OrderKind == ArmyOrder.AdvanceFront ? 1.5f : 1f);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = army;
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
        foreach (var army in candidates)
        {
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
        if (army.Route.Count == 0 || army.OrderProvinceId <= 0 || army.ProvinceId == army.OrderProvinceId) return true;
        var map = campaign.ProvinceMap;
        var going = map.CenterOf(army.OrderProvinceId);
        var there = map.CenterOf(post);
        return NavalOperations.Distance(army.Latitude, army.Longitude, there.Latitude, there.Longitude) <
               0.6f * NavalOperations.Distance(army.Latitude, army.Longitude, going.Latitude, going.Longitude);
    }

    /// <summary>
    /// Send one division to one post, keeping everything else about it: its duty, its war, its
    /// line, its plan. A march already under way in the same direction keeps its progress.
    /// </summary>
    private static bool Repost(Campaign campaign, Nation nation, Army army, int post)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        int[] route = Array.Empty<int>();
        if (army.ProvinceId != post)
        {
            route = topology.FindRoute(army.ProvinceId, post, (ushort)nation.MapIndex, controllers);
            // Ground no road of ours reaches - an exclave, an island - is not a post this
            // division can take.
            if (route.Length == 0) return false;
        }

        var sameWay = army.Route.Count > 0 && route.Length > 0 && army.Route[0] == route[0];
        if (Allocated.TryGetValue(army.OrderProvinceId, out var old) && old.Remove(army)) { }
        else if (Allocated.TryGetValue(army.ProvinceId, out var standing)) standing.Remove(army);
        Loose.Remove(army);
        if (!Allocated.TryGetValue(post, out var there)) Allocated[post] = there = Take();
        there.Add(army);

        army.Route.Clear();
        army.Route.AddRange(route);
        army.PlannedRoute.Clear();
        army.PlannedRoute.AddRange(route);
        if (!sameWay) army.MoveProgress = 0f;
        army.OrderProvinceId = post;
        army.OrderStateMapIndex = topology.ProvinceToState[post];
        army.OrderIssuedDay = campaign.ElapsedDays;
        army.OrderRouteSteps = route.Length;
        army.OrderStepsCompleted = 0;
        Reposts++;
        return true;
    }
}
