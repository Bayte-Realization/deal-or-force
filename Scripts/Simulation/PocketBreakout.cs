using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Whether a computer-run formation that has been cut off tries to break out (PocketBreakout).</summary>
    public bool AiBreaksOutOfPockets { get; set; } = true;

    /// <summary>How many provinces from a pocket the way home is looked for.</summary>
    public int BreakoutSearchDepth { get; set; } = 12;
}

public sealed partial class Army
{
    /// <summary>
    /// The staff sent this formation home out of a pocket; it goes back under the staff's orders
    /// once it stands on friendly ground again. Saved.
    /// </summary>
    public bool BreakingOut;
}

/// <summary>
/// Formations nobody plays break out of a pocket.
///
/// A player's formation inside a pocket could already be sent home - the order is routed through
/// the captor's ground and fought for a province at a time (MoveArmyCommand) - but nothing ever gave
/// that order to a formation the computer ran. It stood in the pocket until it starved or until the
/// province it held fell and, with nowhere to go, it surrendered whole: fifteen Ukrainian
/// formations in one pocket on one day, six hundred thousand prisoners in eight months. Now every
/// day a cut-off formation of a computer-run country that is not fighting and not already marching
/// is ordered to the nearest friendly province, by the same order a player would give, and is
/// handed back to the staff when it gets there. Whether it makes it is the fighting's business.
///
/// Not every pocket runs at once any more. A pocket lives off its stores for a while
/// (StrandedGarrisons), and a column on the road is weaker than the same men dug in: a pocket
/// whose country is fighting its way toward it, or a garrison holding out behind a rising's lines,
/// stays while the stores last and breaks out when they run low. Every pocket, played or not, has
/// the distance to friendly ground measured here each morning - it is what the army card shows as
/// "relief N provinces away", and what decides whether a starving pocket still has someone coming.
/// </summary>
public static class PocketBreakout
{
    // The morning search used to keep one queue, one depth map and one neighbour list for
    // the process. The next campaign's pocket cleared them. Each campaign now owns the
    // scratch, and a second world's morning does not empty the first world's.
    sealed class Scratch
    {
        public readonly Queue<int> Frontier = new();
        public readonly Dictionary<int, int> CameFrom = new();
        public readonly List<int> Pocketed = new();
        public readonly HashSet<int> Pocket = new();
        public int[] PocketMark = Array.Empty<int>();
        public int PocketStamp;
        public int[] SearchStamp = Array.Empty<int>();
        public int[] CameFromArr = Array.Empty<int>();
        public int[] DepthArr = Array.Empty<int>();
        public int SearchTick;
        public int CameFromTick = -1;
        public int DepthTick = -1;
        public readonly List<int> Neighbours = new();
        public readonly Dictionary<int, int> Depth = new();
        public readonly Dictionary<int, Army> ById = new();
        public readonly Dictionary<(int NationId, int WarId), PassPair> PassCache = new();
        public readonly Dictionary<(int NationId, int WarId, int ProvinceId), (List<int> Path, bool TooLarge)> DayPaths = new();
        public int DayPathMapRevision = -1;
        public readonly HashSet<ushort> LocalAllowed = new();
        public readonly HashSet<ushort> LocalFriendly = new();
    }

    sealed class PassPair
    {
        public readonly HashSet<ushort> Allowed;
        public readonly HashSet<ushort> Friendly = new();
        public readonly HashSet<ushort> Hostile = new();
        public PassPair(HashSet<ushort> allowed) => Allowed = allowed;
    }

    static readonly ConditionalWeakTable<Campaign, Scratch> Scratches = new();
    sealed class SortedNeighbors
    {
        public int Revision = -1;
        public int[][] Rows;
    }

    static readonly ConditionalWeakTable<ProvinceTopology, SortedNeighbors> SortedNeighborsCache = new();
    private const int PocketLimit = 200;

    // A copy of the map's adjacency, so it is only as good as the adjacency it was copied
    // from: a strait crossing attached after the first search (a mod reloaded, a test taking
    // the bridges away) changes Neighbors, and the copy is made again when it does.
    static int[][] GetSortedNeighbors(ProvinceTopology map)
    {
        var kept = SortedNeighborsCache.GetOrCreateValue(map);
        if (kept.Rows is not null && kept.Revision == map.AdjacencyRevision) return kept.Rows;
        var src = map.Neighbors;
        var dst = new int[src.Length][];
        for (var i = 0; i < src.Length; i++)
        {
            var row = src[i];
            if (row is null || row.Length == 0) { dst[i] = Array.Empty<int>(); continue; }
            var copy = new int[row.Length];
            Array.Copy(row, copy, row.Length);
            Array.Sort(copy);
            dst[i] = copy;
        }
        kept.Rows = dst;
        kept.Revision = map.AdjacencyRevision;
        return dst;
    }

    static Scratch Board(Campaign campaign) => Scratches.GetOrCreateValue(campaign);

    internal static Queue<int> SearchFrontier(Campaign campaign) =>
        campaign is not null && Scratches.TryGetValue(campaign, out var scratch) ? scratch.Frontier : null;

    internal static Dictionary<int, int> SearchCameFrom(Campaign campaign)
    {
        if (campaign is null || !Scratches.TryGetValue(campaign, out var scratch)) return null;
        if (scratch.CameFromTick != scratch.SearchTick && scratch.SearchTick > 0)
        {
            scratch.CameFromTick = scratch.SearchTick;
            scratch.CameFrom.Clear();
            var stamp = scratch.SearchStamp;
            var came = scratch.CameFromArr;
            var tick = scratch.SearchTick;
            for (var p = 0; p < stamp.Length; p++)
                if (stamp[p] == tick) scratch.CameFrom[p] = came[p];
        }
        return scratch.CameFrom;
    }

    internal static Dictionary<int, int> SearchDepth(Campaign campaign)
    {
        if (campaign is null || !Scratches.TryGetValue(campaign, out var scratch)) return null;
        if (scratch.DepthTick != scratch.SearchTick && scratch.SearchTick > 0)
        {
            scratch.DepthTick = scratch.SearchTick;
            scratch.Depth.Clear();
            var stamp = scratch.SearchStamp;
            var depth = scratch.DepthArr;
            var tick = scratch.SearchTick;
            for (var p = 0; p < stamp.Length; p++)
                if (stamp[p] == tick) scratch.Depth[p] = depth[p];
        }
        return scratch.Depth;
    }

    internal static List<int> SearchNeighbours(Campaign campaign) =>
        campaign is not null && Scratches.TryGetValue(campaign, out var scratch) ? scratch.Neighbours : null;

    /// <summary>Whether the last <see cref="WayHome"/> gave up because the "pocket" was a large piece of the country, not a pocket.</summary>
    internal static bool LastPocketTooLarge { get; private set; }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same daily breakout pass, yielded between batches of pockets.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null || !campaign.StepOpensNewDay) yield break;

        var scratch = Board(campaign);
        var pocketed = scratch.Pocketed;
        var byId = scratch.ById;
        pocketed.Clear();
        byId.Clear();
        var scanned = 0;
        foreach (var army in campaign.Armies)
        {
            if (!byId.ContainsKey(army.Id)) byId[army.Id] = army;
            if (++scanned % 512 == 0) yield return 0;
            if (army.BreakingOut && !army.CutOff && !army.IsMoving && !army.InBattle)
            {
                // Home: the staff has it back.
                army.BreakingOut = false;
                army.DirectOrder = false;
                army.OrderKind = ArmyOrder.Manual;
                army.AssignedWarId = -1;
                continue;
            }
            if (!army.CutOff)
            {
                army.ReliefDistance = -1;
                continue;
            }
            if (army.Divisions < 0.1f || army.EmbarkedFleetId >= 0) continue;
            pocketed.Add(army.Id);
        }

        yield return 0;
        scratch.PassCache.Clear();
        scratch.DayPaths.Clear();
        scratch.DayPathMapRevision = campaign.MapRevision;
        var cachedChecks = 0;
        var needYield = false;
        foreach (var id in pocketed)
        {
            if (needYield || ++cachedChecks > 16)
            {
                needYield = false;
                cachedChecks = 0;
                yield return 0;
            }
            if (!byId.TryGetValue(id, out var army) || army is null) continue;
            var nation = campaign.NationById(army.NationId);
            if (nation is null) continue;
            // Measured for every pocket, played or not: the card reads it, and so does surrender.
            var war = WarLosses.WarOf(campaign, army);
            if (scratch.DayPathMapRevision != campaign.MapRevision)
            {
                scratch.DayPaths.Clear();
                scratch.DayPathMapRevision = campaign.MapRevision;
            }
            var pathKey = (nation.Id, war?.Id ?? -1, army.ProvinceId);
            List<int> path;
            if (scratch.DayPaths.TryGetValue(pathKey, out var cached))
            {
                path = cached.Path;
                LastPocketTooLarge = cached.TooLarge;
            }
            else
            {
                path = WayHomeCore(campaign, nation, army.ProvinceId, war, scratch.PassCache);
                scratch.DayPaths[pathKey] = (path, LastPocketTooLarge);
                needYield = true;
            }
            // -2: not a pocket but a whole piece of the country cut off from its capital; nobody
            // surrenders a province of a country cut in two for want of a road home.
            army.ReliefDistance = path is not null ? path.Count - 1 : LastPocketTooLarge ? -2 : -1;
            if (!campaign.Defines.AiBreaksOutOfPockets || army.InBattle || army.IsMoving ||
                army.SupportOnly || !Delegation.RunByAi(nation)) continue;
            if (army.DirectOrder && !army.BreakingOut && army.HoldoutSinceDay < 0) continue;
            if (path is null) continue;
            if (StrandedGarrisons.ShouldHold(campaign, army, path)) continue;
            if (Order(campaign, nation, army, path[^1]) || path.Count > 1 && Order(campaign, nation, army, path[1]))
                army.BreakingOut = true;
        }
    }

    private static bool Order(Campaign campaign, Nation nation, Army army, int target)
    {
        var command = new MoveArmyCommand { IssuerId = nation.Id, ArmyId = army.Id, TargetProvinceId = target };
        command.Apply(campaign);
        return command.Result is ArmyMoveResult.Issued or ArmyMoveResult.AttackIssued or ArmyMoveResult.QueuedAfterBattle;
    }

    /// <summary>
    /// The shortest legal land corridor from a pocket to friendly ground outside its component:
    /// the provinces in order, the pocket first and the friendly province last; null if there is
    /// none within BreakoutSearchDepth.
    /// </summary>
    internal static List<int> WayHome(Campaign campaign, Nation nation, int start, StrategicWar war = null) =>
        WayHomeCore(campaign, nation, start, war, passCache: null);

    private static List<int> WayHomeCore(Campaign campaign, Nation nation, int start, StrategicWar war,
        Dictionary<(int NationId, int WarId), PassPair> passCache)
    {
        var map = campaign?.ProvinceMap;
        var controllers = campaign?.ProvinceController;
        LastPocketTooLarge = false;
        if (map is null || controllers is null || nation is null || start <= 0 ||
            start > map.ProvinceCount || start >= map.Neighbors.Length || start >= controllers.Length || map.IsWater(start))
            return null;
        // Callers with a real formation pass its actual war. The three-argument read remains
        // useful to staff/tests and uses the same attribution rule, rather than admitting the
        // enemies of every unrelated war as shortcuts.
        war ??= WarLosses.WarOf(campaign, nation.Id, -1, start);
        if (war is not null && (war.Ended || !war.Involves(nation.Id))) war = null;
        var board = Board(campaign);
        HashSet<ushort> allowed;
        HashSet<ushort> friendlyYes;
        if (passCache is not null)
        {
            var key = (nation.Id, war?.Id ?? -1);
            if (!passCache.TryGetValue(key, out var pair))
            {
                var set = MilitaryAccess.Controllers(campaign, nation.Id);
                pair = new PassPair(set);
                foreach (var m in set) pair.Friendly.Add(m);
                if (war is not null)
                {
                    if (!war.Ended && war.Involves(nation.Id))
                        WarCoalition.FillControllers(campaign, war, war.IsAttacking(nation.Id), pair.Friendly);
                    WarCoalition.FillControllers(campaign, war, true, set);
                    WarCoalition.FillControllers(campaign, war, false, set);
                }
                passCache[key] = pair;
            }
            allowed = pair.Allowed;
            friendlyYes = pair.Friendly;
        }
        else
        {
            allowed = board.LocalAllowed;
            allowed.Clear();
            MilitaryAccess.FillControllers(campaign, nation.Id, allowed);
            friendlyYes = board.LocalFriendly;
            friendlyYes.Clear();
            foreach (var m in allowed) friendlyYes.Add(m);
            if (war is not null)
            {
                if (!war.Ended && war.Involves(nation.Id))
                    WarCoalition.FillControllers(campaign, war, war.IsAttacking(nation.Id), friendlyYes);
                WarCoalition.FillControllers(campaign, war, true, allowed);
                WarCoalition.FillControllers(campaign, war, false, allowed);
            }
        }
        // The pocket itself: the friendly ground it can reach without crossing the enemy's.
        // Home is friendly ground outside it. A "pocket" of hundreds of provinces is a country cut in
        // two, not a pocket, and is the staff's to deal with.
        //
        // A formation standing on ground the enemy holds - a garrison the morning its region rose -
        // has no pocket of its own ground to flood: flooding from there ran straight into the
        // homeland next door, found a "pocket" of hundreds of provinces and gave up on a road home
        // one province long. Its pocket is the province it stands on.
        var frontier = board.Frontier;
        var neighbors = map.Neighbors;
        var requiredLength = map.ProvinceCount + 1;
        if (board.PocketMark.Length < requiredLength)
            board.PocketMark = new int[requiredLength];
        var pocketMark = board.PocketMark;
        if (++board.PocketStamp == int.MaxValue)
        {
            Array.Clear(pocketMark);
            board.PocketStamp = 1;
        }
        var pocketStamp = board.PocketStamp;
        var pocketCount = 1;
        frontier.Clear();
        frontier.Enqueue(start);
        pocketMark[start] = pocketStamp;
        if (!friendlyYes.Contains(controllers[start])) frontier.Clear();
        while (frontier.Count > 0)
        {
            var here = frontier.Dequeue();
            var row = neighbors[here];
            for (var ni = 0; ni < row.Length; ni++)
            {
                var next = row[ni];
                if (!IsValidPocketProvince(map, controllers, next) || pocketMark[next] == pocketStamp ||
                    !friendlyYes.Contains(controllers[next])) continue;
                pocketMark[next] = pocketStamp;
                if (++pocketCount > PocketLimit)
                {
                    LastPocketTooLarge = true;
                    return null;
                }
                frontier.Enqueue(next);
            }
        }
        var sortedNeighbors = GetSortedNeighbors(map);
        if (board.SearchStamp.Length < requiredLength)
        {
            board.SearchStamp = new int[requiredLength];
            board.CameFromArr = new int[requiredLength];
            board.DepthArr = new int[requiredLength];
        }
        var searchStamp = board.SearchStamp;
        var cameFromArr = board.CameFromArr;
        var depthArr = board.DepthArr;
        if (++board.SearchTick == int.MaxValue)
        {
            Array.Clear(searchStamp);
            board.SearchTick = 1;
        }
        var searchTick = board.SearchTick;
        frontier.Clear();
        frontier.Enqueue(start);
        searchStamp[start] = searchTick;
        cameFromArr[start] = -1;
        depthArr[start] = 0;
        var limit = Math.Clamp(campaign.Defines.BreakoutSearchDepth, 1, map.ProvinceCount);
        while (frontier.Count > 0)
        {
            var here = frontier.Dequeue();
            if (pocketMark[here] != pocketStamp &&
                friendlyYes.Contains(controllers[here]))
            {
                var path = new List<int>();
                for (var step = here; step >= 0; step = cameFromArr[step]) path.Add(step);
                path.Reverse();
                return path;
            }
            var hereDepth = depthArr[here];
            if (hereDepth >= limit) continue;
            var row = sortedNeighbors[here];
            for (var ni = 0; ni < row.Length; ni++)
            {
                var next = row[ni];
                if (!IsValidPocketProvince(map, controllers, next) || searchStamp[next] == searchTick || !allowed.Contains(controllers[next])) continue;
                searchStamp[next] = searchTick;
                cameFromArr[next] = here;
                depthArr[next] = hereDepth + 1;
                frontier.Enqueue(next);
            }
        }
        return null;
    }

    private static bool IsValidPocketProvince(ProvinceTopology map, ushort[] controllers, int province) =>
        province > 0 && province <= map.ProvinceCount &&
        province < map.Neighbors.Length && province < controllers.Length && !map.IsWater(province);
}
