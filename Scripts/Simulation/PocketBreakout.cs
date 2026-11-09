using System;
using System.Collections.Generic;

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
    private static readonly Queue<int> Frontier = new();
    private static readonly Dictionary<int, int> CameFrom = new();
    private static readonly List<int> Pocketed = new();
    private static readonly HashSet<int> Pocket = new();
    private const int PocketLimit = 200;

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

        Pocketed.Clear();
        var scanned = 0;
        foreach (var army in campaign.Armies)
        {
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
            Pocketed.Add(army.Id);
        }

        yield return 0;
        var pockets = 0;
        foreach (var id in Pocketed)
        {
            if (++pockets % 8 == 0) yield return 0;
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            if (army is null) continue;
            var nation = campaign.NationById(army.NationId);
            if (nation is null) continue;
            // Measured for every pocket, played or not: the card reads it, and so does surrender.
            var path = WayHome(campaign, nation, army.ProvinceId, WarLosses.WarOf(campaign, army));
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
    internal static List<int> WayHome(Campaign campaign, Nation nation, int start, StrategicWar war = null)
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
        war ??= WarLosses.WarOf(campaign, new Army { NationId = nation.Id, ProvinceId = start });
        if (war is not null && (war.Ended || !war.Involves(nation.Id))) war = null;
        var allowed = MilitaryAccess.Controllers(campaign, nation.Id);
        if (war is not null)
        {
            WarCoalition.FillControllers(campaign, war, true, allowed);
            WarCoalition.FillControllers(campaign, war, false, allowed);
        }
        bool Valid(int province) => province > 0 && province <= map.ProvinceCount &&
            province < map.Neighbors.Length && province < controllers.Length && !map.IsWater(province);
        bool Friendly(int province) => WarCoalition.FriendlyController(campaign, war, nation.Id, controllers[province]);
        // The pocket itself: the friendly ground it can reach without crossing the enemy's.
        // Home is friendly ground outside it. A "pocket" of hundreds of provinces is a country cut in
        // two, not a pocket, and is the staff's to deal with.
        //
        // A formation standing on ground the enemy holds - a garrison the morning its region rose -
        // has no pocket of its own ground to flood: flooding from there ran straight into the
        // homeland next door, found a "pocket" of hundreds of provinces and gave up on a road home
        // one province long. Its pocket is the province it stands on.
        Pocket.Clear();
        Frontier.Clear();
        Frontier.Enqueue(start);
        Pocket.Add(start);
        if (!Friendly(start)) Frontier.Clear();
        var neighbours = new List<int>();
        while (Frontier.Count > 0)
        {
            var here = Frontier.Dequeue();
            neighbours.Clear();
            neighbours.AddRange(map.Neighbors[here]);
            neighbours.Sort();
            foreach (var next in neighbours)
            {
                if (!Valid(next) || Pocket.Contains(next) || !Friendly(next)) continue;
                Pocket.Add(next);
                if (Pocket.Count > PocketLimit)
                {
                    LastPocketTooLarge = true;
                    return null;
                }
                Frontier.Enqueue(next);
            }
        }
        Frontier.Clear();
        CameFrom.Clear();
        Frontier.Enqueue(start);
        CameFrom[start] = -1;
        var depth = new Dictionary<int, int> { [start] = 0 };
        var limit = Math.Clamp(campaign.Defines.BreakoutSearchDepth, 1, map.ProvinceCount);
        while (Frontier.Count > 0)
        {
            var here = Frontier.Dequeue();
            if (!Pocket.Contains(here) && Friendly(here))
            {
                var path = new List<int>();
                for (var step = here; step >= 0; step = CameFrom[step]) path.Add(step);
                path.Reverse();
                return path;
            }
            if (depth[here] >= limit) continue;
            neighbours.Clear();
            neighbours.AddRange(map.Neighbors[here]);
            neighbours.Sort();
            foreach (var next in neighbours)
            {
                if (!Valid(next) || CameFrom.ContainsKey(next) || !allowed.Contains(controllers[next])) continue;
                CameFrom[next] = here;
                depth[next] = depth[here] + 1;
                Frontier.Enqueue(next);
            }
        }
        return null;
    }
}
