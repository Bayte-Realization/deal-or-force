using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Campaign
{
    /// <summary>
    /// The console's two curses (WorldCurses), or "" for an ordinary world. Saved, and never
    /// lifted: a world that has had one is that world for good.
    /// </summary>
    public string Curse = "";

    /// <summary>The day the curse fell, -1 for none.</summary>
    public int CurseDay = -1;

    /// <summary>Formations the weird route has taken into the water.</summary>
    public int CurseDrowned;
}

public sealed partial class Defines
{
    /// <summary>Hours a formation on the weird route takes to cross a province on its way to the water.</summary>
    public int WeirdRouteHoursPerProvince { get; set; } = 10;
}

/// <summary>
/// The two console curses, `snowgrave` and `weirdroute`, named for the routes of Deltarune that
/// cannot be taken back.
///
/// Snowgrave buries the world under ice. The clock goes on - days turn, the calendar runs - and
/// nothing else does: no system runs, no order is carried out, the soundtrack stops and does
/// not come back. The weird route sends every formation in the world, those standing and those
/// yet to be raised, to the nearest water to walk into it, over any border and past any war;
/// a world with no water at all (a mod's) has nowhere to send them, and the screen goes to
/// static instead. Either way the save stays as it is now, which is the joke.
///
/// Both are state, not presentation: the curse is saved, fingerprinted and replayed, and the
/// ice, the static and the silence are drawn from it.
/// </summary>
public static class WorldCurses
{
    public const string Snowgrave = "snowgrave";
    public const string WeirdRoute = "weirdroute";
    public const string Static = "static";

    public static bool Any(Campaign campaign) => campaign is not null && campaign.Curse.Length > 0;

    /// <summary>The clock runs and nothing else does: the ice, and the static.</summary>
    public static bool Frozen(Campaign campaign) =>
        campaign is not null && (campaign.Curse == Snowgrave || campaign.Curse == Static);

    /// <summary>
    /// Lay a curse on the world; the curse it has afterwards. The weird route turns to static
    /// on a map with no water to walk into, the ice can still fall on a world already marching
    /// into the sea, and nothing is cast over the ice or the static: a frozen world has no
    /// formation left that could walk anywhere, and a world of static has no world.
    /// </summary>
    public static string Cast(Campaign campaign, string curse)
    {
        if (campaign is null) return "";
        if (Frozen(campaign)) return campaign.Curse;
        if (curse == WeirdRoute && campaign.Curse == WeirdRoute) return WeirdRoute;
        if (curse == WeirdRoute && Field(campaign) is not { Shores: > 0 }) curse = Static;
        if (curse != Snowgrave && curse != WeirdRoute && curse != Static) return campaign.Curse;
        campaign.FinishStep();
        campaign.Curse = curse;
        campaign.CurseDay = campaign.ElapsedDays;
        campaign.MarkStrategicChange();
        return curse;
    }

    // ---- the weird route ----------------------------------------------------------------

    private sealed class Shoreline
    {
        public ProvinceTopology Map;
        public int Destroyed = -1;
        public int Shores;
        /// <summary>Provinces to the nearest shore: 0 on one, -1 where no land leads to water.</summary>
        public int[] Distance = Array.Empty<int>();
        /// <summary>The neighbour one province nearer the water.</summary>
        public int[] Next = Array.Empty<int>();
    }

    private static readonly Shoreline Cached = new();
    private static readonly List<Army> Doomed = new();
    private static readonly Queue<int> Frontier = new();

    /// <summary>
    /// Every land province's way to the water. A shore is a province that touches the sea on
    /// the map itself (a cell beside one that belongs to no province), a lake beside it or a
    /// water province next to it; the rest are counted outward from the shores over land,
    /// whoever holds it. Ownership plays no part, so it is rebuilt only when the ground itself
    /// changes: a new map, or a province destroyed.
    /// </summary>
    private static Shoreline Field(Campaign campaign)
    {
        var map = campaign.ProvinceMap;
        if (map is null) return null;
        var field = Cached;
        if (ReferenceEquals(field.Map, map) && field.Destroyed == campaign.DestroyedProvinces.Count) return field;
        var count = map.ProvinceCount + 1;
        var distance = new int[count];
        var next = new int[count];
        Array.Fill(distance, -1);
        Frontier.Clear();
        var shores = 0;
        void Shore(int province)
        {
            if (province <= 0 || province >= count || distance[province] == 0 || map.IsWater(province)) return;
            distance[province] = 0;
            next[province] = province;
            Frontier.Enqueue(province);
            shores++;
        }
        // The sea is the cells no province holds; the map wraps east to west, not over the poles.
        var cells = map.CellToProvince;
        var width = map.Width;
        var height = map.Height;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                int here = cells[row + x];
                int east = cells[row + (x + 1) % width];
                if ((here == 0) != (east == 0)) Shore(here == 0 ? east : here);
                if (y + 1 >= height) continue;
                int south = cells[row + width + x];
                if ((here == 0) != (south == 0)) Shore(here == 0 ? south : here);
            }
        }
        for (var province = 1; province < count; province++)
        {
            if (distance[province] == 0 || map.IsWater(province)) continue;
            if (map.HasLakeAccess(province)) { Shore(province); continue; }
            foreach (var neighbour in map.Neighbors[province])
                if (map.IsWater(neighbour)) { Shore(province); break; }
        }
        while (Frontier.Count > 0)
        {
            var from = Frontier.Dequeue();
            foreach (var neighbour in map.Neighbors[from])
            {
                if (neighbour <= 0 || neighbour >= count || distance[neighbour] >= 0 || map.IsWater(neighbour)) continue;
                distance[neighbour] = distance[from] + 1;
                next[neighbour] = from;
                Frontier.Enqueue(neighbour);
            }
        }
        field.Map = map;
        field.Destroyed = campaign.DestroyedProvinces.Count;
        field.Shores = shores;
        field.Distance = distance;
        field.Next = next;
        return field;
    }

    /// <summary>
    /// One step of the weird route, before anything else moves: every formation is taken off
    /// whatever it was doing - its order, its plan, its battle - and walked one province nearer
    /// the water each WeirdRouteHoursPerProvince, staggered by its number so a corps does not
    /// move as one; a formation on the shore walks in when its own hour comes, and one at sea
    /// goes over the side.
    /// </summary>
    public static void March(Campaign campaign)
    {
        if (campaign?.Curse != WeirdRoute) return;
        var map = campaign.ProvinceMap;
        var field = Field(campaign);
        if (map is null || field is null) return;
        var pace = Math.Max(1, campaign.Defines.WeirdRouteHoursPerProvince);
        var end = campaign.ElapsedHours;
        var start = end - campaign.TickHours;
        Doomed.Clear();
        var moved = false;
        foreach (var army in campaign.Armies)
        {
            if (army.OrderKind != ArmyOrder.Manual || army.PlanActive || army.FrontLineId >= 0)
                BattlePlanning.Forget(campaign, army);
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.OrderProvinceId = -1;
            army.OrderStateMapIndex = -1;
            army.OrderKind = ArmyOrder.Manual;
            army.DirectOrder = false;
            army.AssignedWarId = -1;
            army.PlanActive = false;
            army.Entrenchment = 0f;
            army.MoveProgress = 0f;
            if (army.BattleId >= 0)
            {
                foreach (var battle in campaign.Battles)
                {
                    if (battle.Id != army.BattleId) continue;
                    battle.Attackers.Remove(army.Id);
                    battle.Defenders.Remove(army.Id);
                }
                army.BattleId = -1;
            }
            if (army.EmbarkedFleetId >= 0) { Doomed.Add(army); continue; }
            var at = army.ProvinceId;
            if (at <= 0 || at >= field.Distance.Length || field.Distance[at] < 0) continue;
            var offset = (long)(army.Id * 7 % pace);
            var steps = (int)(FloorDiv(end + offset, pace) - FloorDiv(start + offset, pace));
            // On the shore it walks in at its own hour, not all at once with every other
            // formation standing by the sea when the curse fell.
            if (field.Distance[at] == 0) { if (steps > 0) Doomed.Add(army); continue; }
            for (var step = 0; step < steps && field.Distance[army.ProvinceId] > 0; step++)
                army.ProvinceId = field.Next[army.ProvinceId];
            if (army.ProvinceId == at) continue;
            moved = true;
            army.StateMapIndex = map.ProvinceToState[army.ProvinceId];
            (army.Latitude, army.Longitude) = map.CenterOf(army.ProvinceId);
        }
        foreach (var army in Doomed)
        {
            if (army.EmbarkedFleetId >= 0)
                foreach (var fleet in campaign.Fleets) fleet.Carried.Remove(army.Id);
            Attrition.Destroy(campaign, army, "drowned: the weird route");
            campaign.CurseDrowned++;
        }
        if (moved || Doomed.Count > 0) campaign.MarkStrategicChange();
    }

    private static long FloorDiv(long value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
}
