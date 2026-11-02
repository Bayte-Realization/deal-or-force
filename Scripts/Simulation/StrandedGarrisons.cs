using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Whether a garrison caught on ground that rises divides itself - defects, leaves or holds - instead of standing where it was.</summary>
    public bool StrandedGarrisonsEnabled { get; set; } = true;

    /// <summary>
    /// How much the people of the rising standing around a formation weigh on whether it goes
    /// over, and how much its own cohesion and its country's unpaid, beaten or plotting army
    /// do. The base share is <see cref="SecessionDefectionShare"/>; these bend it formation by
    /// formation so that it stays that share on average.
    /// </summary>
    public float GarrisonDefectionKinWeight { get; set; } = 1.3f;
    public float GarrisonDefectionCohesionWeight { get; set; } = 0.5f;
    public float GarrisonDefectionDisloyaltyWeight { get; set; } = 0.6f;

    /// <summary>
    /// Of the formations that stay loyal, the share that digs in rather than leaves, before its
    /// own makeup moves it: a cohesive, experienced formation among people who are not the
    /// rising's holds; a shaken one among the rising's own people goes home while it can.
    /// </summary>
    public float GarrisonHoldBase { get; set; } = 0.4f;

    /// <summary>What a formation taken out by sea or air leaves behind: men lost on the way, and the heavy equipment that does not fit.</summary>
    public float GarrisonEvacuationStrengthLoss { get; set; } = 0.12f;
    public float GarrisonEvacuationEquipmentLoss { get; set; } = 0.35f;

    /// <summary>Divisions one of a country's air wings can fly out of an inland garrison at the moment of the rising.</summary>
    public float GarrisonAirliftDivisionsPerWing { get; set; } = 1.5f;

    /// <summary>The entrenchment a garrison that decides to hold its ground starts with: it was already standing there.</summary>
    public float GarrisonHoldoutEntrenchment { get; set; } = 0.5f;

    /// <summary>
    /// Local stores in a pocket, in days a formation can live on them: a base, more for the
    /// region's roads (infrastructure), its barracks and its towns; scaled by how long the
    /// formation has held the ground (its entrenchment), shared between all the divisions of the
    /// country standing in the region - by the square root of how many more there are than the
    /// region's depots were laid in for, since a region that hosts a larger garrison keeps larger
    /// depots - and never outside the bounds below. A formation cut off on ground its country does
    /// not hold has only what it carries.
    /// </summary>
    public float PocketStoresBaseDays { get; set; } = 12f;
    public float PocketStoresPerInfrastructure { get; set; } = 1.0f;
    public float PocketStoresPerBarracks { get; set; } = 3f;
    public float PocketStoresUrbanDays { get; set; } = 15f;
    public float PocketStoresCarriedDays { get; set; } = 5f;
    public float PocketStoresDivisionsPerRegion { get; set; } = 4f;
    public float PocketStoresMinDays { get; set; } = 3f;
    public float PocketStoresMaxDays { get; set; } = 60f;

    /// <summary>How much faster a formation eats its stores in a day of fighting than in a quiet one.</summary>
    public float PocketStoresBattleDraw { get; set; } = 1.8f;

    /// <summary>
    /// The supply a formation living off its stores is kept near: rationed, not starving.
    /// </summary>
    public float PocketStoresSupplyLevel { get; set; } = 0.75f;

    /// <summary>
    /// What makes the stores last: divisions of a pocket one air wing of the country can keep
    /// fed from a base in range, the largest share of a pocket's needs an air bridge can carry,
    /// and what a held port and a held town give as an improvised depot. Together they never
    /// cover more than <see cref="PocketReliefMaxShare"/>, so every pocket's stores still run down.
    /// </summary>
    public float PocketAirliftDivisionsPerWing { get; set; } = 1.0f;
    public float PocketAirliftMaxShare { get; set; } = 0.6f;
    public float PocketPortDepotShare { get; set; } = 0.3f;
    public float PocketCityDepotShare { get; set; } = 0.3f;
    public float PocketReliefMaxShare { get; set; } = 0.85f;

    /// <summary>
    /// When a pocket the computer runs breaks out: once its stores are down to this many days,
    /// or at once if relief is not coming. Relief is coming when a formation of its own is
    /// fighting or marching within this many provinces of the nearest friendly ground.
    /// </summary>
    public float PocketBreakoutStoresDays { get; set; } = 5f;
    public int PocketReliefRadius { get; set; } = 2;

    /// <summary>
    /// When a pocket gives up: after this many days with the stores gone, a formation with no
    /// relief coming surrenders at this chance a day, rising each further week.
    /// </summary>
    public int PocketSurrenderHungryDays { get; set; } = 10;
    public float PocketSurrenderDailyChance { get; set; } = 0.12f;

    /// <summary>
    /// Forward depots (LogisticsSystem.ForwardDepot): the share of an enemy region's land a
    /// country must hold, with the road behind it open, before a railhead there feeds its army;
    /// and what such a depot carries against a home depot. A share above one turns them off.
    /// </summary>
    public float ForwardDepotHeldShare { get; set; } = 0.5f;
    public float ForwardDepotThroughputShare { get; set; } = 0.4f;
}

public sealed partial class Army
{
    /// <summary>
    /// Days of local stores the formation has left in its pocket, or -1 when it is not in one.
    /// While any are left the hunger clock (<see cref="DaysCutOff"/>) does not run. Saved.
    /// </summary>
    public float PocketStores = -1f;

    /// <summary>The day this formation's present pocket closed, or -1. Saved.</summary>
    public int PocketSinceDay = -1;

    /// <summary>
    /// The day this formation chose to hold its ground behind a rising's lines, or -1: a
    /// garrison that dug in rather than went over or went home. Saved.
    /// </summary>
    public int HoldoutSinceDay = -1;

    /// <summary>
    /// Provinces from the pocket to the nearest ground its country holds, as the breakout
    /// staff measured it this morning; -1 when there is none within reach or no pocket, -2 when
    /// it is not a pocket but a large piece of the country cut off from the rest. Saved.
    /// </summary>
    public int ReliefDistance = -1;
}

/// <summary>
/// The garrison standing on ground that rises, and the pocket it is in afterwards.
///
/// A secession, a civil war or a collapsing state used to give the new side the ground and the
/// old side a choice it never made: a part of the garrison went over (Reintegration.Defect) and
/// the rest stood exactly where it was, on enemy ground from one morning to the next, and
/// starved there over six weeks. Nothing was holding it there; nobody had decided anything. A
/// player read it as "they just stay to die of exhaustion on enemy land", and the bench agreed:
/// three formations in four that a war cost were buried after starving in a pocket.
///
/// Now the formations on that ground decide, each by its own makeup, at the moment of the
/// rising. Some go over - more where the rising's own people stand around them, where the army
/// is unpaid or plotting, where the formation is shaken (see <see cref="DefectionChance"/>). Of
/// the loyal, some go home: on foot, fighting through whatever the rising has under arms, when a
/// road leads to the country's own ground (the breakout order every pocket is given), and by sea
/// or air when none does, with men and heavy equipment lost on the way. The rest dig in and hold
/// their province as a pocket of resistance. A pocket lives off what was stockpiled in the
/// region - weeks, more for a region with roads, barracks and towns - stretched by an air bridge
/// and by a held port or town; it can be relieved, it can break out, and when the stores are gone
/// and nobody is coming it surrenders. The same rules for every country, played or not.
/// </summary>
public static class StrandedGarrisons
{
    /// <summary>What happened to garrisons and pockets in this process: a bench counter, not campaign state.</summary>
    public static readonly Dictionary<string, int> Tally = new(StringComparer.Ordinal);

    private static void Count(string key, int by = 1) => Tally[key] = Tally.GetValueOrDefault(key) + by;

    // ------------------------------------------------------------------ the moment of the rising

    /// <summary>
    /// The chance that one formation standing on the rising's ground goes over to it: the base
    /// share, bent by the people around it, its own cohesion and how little its army will do for
    /// its government. Averages to about the base share across an ordinary garrison.
    /// </summary>
    public static float DefectionChance(Campaign c, Nation parent, Nation born, Army army, float share)
    {
        if (c is null || army is null || !c.Defines.StrandedGarrisonsEnabled) return share;
        var d = c.Defines;
        var kin = Kin(c, parent, born, army.ProvinceId);
        var disloyalty = parent is null ? 0f : StateFailure.ArmyDisloyalty(c, parent);
        var factor = (0.45f + d.GarrisonDefectionKinWeight * kin) *
                     (1.3f - d.GarrisonDefectionCohesionWeight * M.Clamp01(army.Cohesion)) *
                     (0.9f + d.GarrisonDefectionDisloyaltyWeight * disloyalty);
        return M.Clamp(share * factor, 0f, 0.9f);
    }

    /// <summary>
    /// How much the ground around a formation belongs to the rising, 0 to 1. A people leaving is
    /// the share of its own people in the region; a rising of the country's own people is the
    /// region's anger instead - its radicalism and how badly it is governed.
    /// </summary>
    public static float Kin(Campaign c, Nation parent, Nation born, int province)
    {
        var region = RegionOf(c, province);
        if (region is null || born is null) return 0.5f;
        var risers = WarAims.Cultures(born);
        var rulers = parent is null ? new HashSet<string>() : WarAims.Cultures(parent);
        if (risers.Count > 0 && !risers.Overlaps(rulers))
            return M.Clamp01(WarAims.CultureShare(region, risers));
        return M.Clamp01(region.Radicalism * 0.6f + (1f - region.Stability) * 0.4f);
    }

    /// <summary>
    /// The loyal garrison left on the rising's ground, once the war over it has begun: each
    /// formation goes home or holds. Called once per rising, after the defections, by every road
    /// a rising takes into a war (SecessionEvents, MovementRules, SecessionResponse).
    /// </summary>
    public static void AtRising(Campaign c, Nation parent, Nation born, IReadOnlyCollection<int> provinces)
    {
        if (c?.ProvinceMap is null || c.ProvinceController is null || parent is null || born is null ||
            provinces is null || provinces.Count == 0 || !c.Defines.StrandedGarrisonsEnabled) return;
        var war = c.Wars.FirstOrDefault(w => !w.Ended && w.Opposes(parent.Id, born.Id));
        if (war is null) return;
        var ground = provinces as HashSet<int> ?? new HashSet<int>(provinces);
        var garrison = c.Armies.Where(army => army.NationId == parent.Id && army.EmbarkedFleetId < 0 &&
                                              army.Divisions > 0f && ground.Contains(army.ProvinceId) &&
                                              !MilitaryAccess.MustReturn(c, army))
            .OrderBy(army => army.Id).ToList();
        if (garrison.Count == 0) return;

        var d = c.Defines;
        var seat = born.CapitalProvinceId;
        var disloyalty = StateFailure.ArmyDisloyalty(c, parent);
        // What the country can fly out of an inland garrison today: its air wings, each a few divisions.
        var airlift = (c.SupportFormations ?? new List<SupportFormation>())
            .Count(f => f.NationId == parent.Id && SupportTypeCatalog.TryGet(f.TypeId, out var type) &&
                        type.Kind == SupportKind.Air && f.Strength > 0.2f) * d.GarrisonAirliftDivisionsPerWing;
        var home = CapitalCore.SupplyOriginProvinceId(c, parent);
        int held = 0, marched = 0, shipped = 0;
        foreach (var army in garrison)
        {
            var province = army.ProvinceId;
            var route = PocketBreakout.WayHome(c, parent, province);
            var steps = route is null ? -1 : route.Count - 1;
            var kin = Kin(c, parent, born, province);
            var hold = d.GarrisonHoldBase + 0.6f * (M.Clamp01(army.Cohesion) - 0.7f) +
                       0.4f * (M.Clamp01(army.Experience) - 0.3f) - 0.5f * kin - 0.4f * disloyalty;
            if (steps is > 0 and <= 2) hold -= 0.1f;
            hold = M.Clamp(hold, 0.05f, 0.9f);
            var roll = Roll(c.Seed, army.Id, born.Id, c.ElapsedDays, 0x6A12);

            if (roll >= hold)
            {
                // Home on foot, through whatever the rising has under arms: the breakout order.
                if (steps > 0 && March(c, parent, army, route))
                {
                    marched++;
                    continue;
                }
                // No road: by sea from a coast the enemy's fleets do not hold, by air from inland.
                if (home > 0 && home != province && ShipOut(c, parent, army, home, ref airlift))
                {
                    shipped++;
                    continue;
                }
            }
            Hold(c, parent, born, war, army, seat);
            held++;
        }
        Count("garrisons_held", held);
        Count("garrisons_marched", marched);
        Count("garrisons_shipped", shipped);
        if (held + marched + shipped > 0)
        {
            c.MarkStrategicChange();
            DiagnosticLogs.Write("simulation",
                $"GARRISON_FATE parent={parent.Tag} born={born.Tag} held={held} marched={marched} shipped={shipped}");
        }
    }

    private static bool March(Campaign c, Nation parent, Army army, List<int> route)
    {
        if (army.InBattle) return false;
        army.Route.Clear(); army.PlannedRoute.Clear(); army.DirectOrder = false;
        var command = new MoveArmyCommand { IssuerId = parent.Id, ArmyId = army.Id, TargetProvinceId = route[^1] };
        command.Apply(c);
        if (command.Result is not (ArmyMoveResult.Issued or ArmyMoveResult.AttackIssued or ArmyMoveResult.QueuedAfterBattle))
            return false;
        army.BreakingOut = true;
        // Standing on the rising's ground it is cut off already, whatever the last trace said:
        // the release home waits for the road to be open, not for the next morning's trace.
        army.CutOff = true;
        return true;
    }

    private static bool ShipOut(Campaign c, Nation parent, Army army, int home, ref float airlift)
    {
        if (army.InBattle) return false;
        var map = c.ProvinceMap;
        var (latitude, longitude) = map.CenterOf(army.ProvinceId);
        var bySea = SeaLanes.TryHarbourPoint(map, army.ProvinceId, out _, out _) &&
                    NavalOperations.BlockadePressure(c, parent, latitude, longitude) < 0.5f;
        if (!bySea)
        {
            if (airlift < army.Divisions) return false;
            airlift -= army.Divisions;
        }
        var (_, cost) = Expeditions.Quote(c, army, home);
        if (parent.Treasury < cost) return false;
        parent.Treasury -= cost;
        Expeditions.Evacuate(c, army, home, c.Defines.GarrisonEvacuationStrengthLoss,
            c.Defines.GarrisonEvacuationEquipmentLoss);
        return true;
    }

    /// <summary>
    /// Dig in where it stands: the province is held for the old state again - unless the rising
    /// itself stands in it or it is the rising's seat, which a garrison besieges rather than
    /// owns - and the stores of the region are counted for it now.
    /// </summary>
    private static void Hold(Campaign c, Nation parent, Nation born, StrategicWar war, Army army, int seat)
    {
        var province = army.ProvinceId;
        var controllers = c.ProvinceController;
        var risingHere = c.Armies.Any(other => other.NationId == born.Id && other.ProvinceId == province);
        if (province != seat && !risingHere && controllers[province] == born.MapIndex)
        {
            war.OriginalController.TryAdd(province, controllers[province]);
            c.SetProvinceController(province, (ushort)parent.MapIndex);
            war.FrontDirty = true;
        }
        army.Route.Clear(); army.PlannedRoute.Clear(); army.MoveProgress = 0f;
        army.OrderKind = ArmyOrder.Manual;
        army.AssignedWarId = -1;
        army.FrontLineId = -1;
        foreach (var line in c.FrontLines) line.Posts.Remove(army.Id);
        // The staff does not post a garrison that is holding out; the breakout staff decides
        // when it leaves (PocketBreakout).
        army.DirectOrder = true;
        army.BreakingOut = false;
        army.HoldoutSinceDay = c.ElapsedDays;
        army.Entrenchment = MathF.Max(army.Entrenchment, c.Defines.GarrisonHoldoutEntrenchment);
        army.CutOff = true;
        army.PocketStores = AssessStores(c, army);
        army.PocketSinceDay = c.ElapsedDays;
    }

    // ------------------------------------------------------------------ stores in a pocket

    /// <summary>
    /// Days of stores a formation finds in its pocket on the day it closes: the region's roads,
    /// barracks and towns, how long it has held the ground, and how many divisions of its
    /// country share them. A formation on ground its country does not hold has only what it
    /// carries; a garrison holding out behind a rising's lines holds its own depot.
    /// </summary>
    public static float AssessStores(Campaign c, Army army)
    {
        var d = c.Defines;
        var province = army.ProvinceId;
        var nation = c.NationById(army.NationId);
        var controllers = c.ProvinceController;
        var holdsGround = nation is not null && controllers is not null && province > 0 && province < controllers.Length &&
                          controllers[province] == nation.MapIndex;
        var region = RegionOf(c, province);
        if (region is null || !holdsGround && army.HoldoutSinceDay < 0)
            return M.Clamp(d.PocketStoresCarriedDays, d.PocketStoresMinDays, d.PocketStoresMaxDays);
        var days = d.PocketStoresBaseDays +
                   region.Buildings.GetValueOrDefault(BuildingKind.Infrastructure) * d.PocketStoresPerInfrastructure +
                   region.Buildings.GetValueOrDefault(BuildingKind.Barracks) * d.PocketStoresPerBarracks +
                   M.Clamp01(region.Urbanization) * d.PocketStoresUrbanDays;
        days *= 0.6f + 0.6f * M.Clamp01(army.Entrenchment);
        var sharing = 0f;
        foreach (var other in c.Armies)
            if (other.NationId == army.NationId && other.StateMapIndex == army.StateMapIndex && other.EmbarkedFleetId < 0)
                sharing += other.Divisions;
        days /= MathF.Sqrt(MathF.Max(1f, sharing / MathF.Max(0.5f, d.PocketStoresDivisionsPerRegion)));
        return M.Clamp(days, d.PocketStoresMinDays, d.PocketStoresMaxDays);
    }

    /// <summary>
    /// Days of stores one campaign day costs this formation: more in a day of fighting, less
    /// for what an air bridge and a held port or town bring in. Never nothing.
    /// </summary>
    public static float DailyDraw(Campaign c, Army army, Dictionary<int, float> cutOffDivisions = null)
    {
        var draw = army.InBattle ? c.Defines.PocketStoresBattleDraw : 1f;
        var relief = MathF.Min(c.Defines.PocketReliefMaxShare,
            AirBridgeShare(c, army, cutOffDivisions) + DepotShare(c, army));
        return draw * (1f - relief);
    }

    /// <summary>
    /// The share of a pocket's needs flown in by its country's air wings whose base reaches it,
    /// against every division the country has cut off, and thinned by the enemy's fighters and
    /// missile brigades over it.
    /// </summary>
    public static float AirBridgeShare(Campaign c, Army army, Dictionary<int, float> cutOffDivisions = null)
    {
        if (c?.ProvinceMap is null || c.SupportFormations is null || army.ProvinceId <= 0) return 0f;
        var d = c.Defines;
        var wings = 0f;
        foreach (var formation in c.SupportFormations)
        {
            if (formation.NationId != army.NationId || formation.Strength <= 0f) continue;
            if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type) || type.Kind != SupportKind.Air) continue;
            if (SupportCore.Displaced(c, formation) || !SupportCore.InRange(c, formation, type, army.ProvinceId)) continue;
            wings += formation.Strength;
        }
        if (wings <= 0f) return 0f;
        var demand = cutOffDivisions is not null && cutOffDivisions.TryGetValue(army.NationId, out var all)
            ? all : army.Divisions;
        var share = wings * d.PocketAirliftDivisionsPerWing / MathF.Max(0.25f, demand);
        var enemies = Enemies(c, army.NationId);
        if (enemies.Count > 0)
        {
            var sky = SupportCore.ReachOf(c, enemies, army.ProvinceId);
            var contest = sky.AirSuperiority + sky.AirDefence;
            share *= wings / (wings + 0.5f * contest);
        }
        return M.Clamp(share, 0f, d.PocketAirliftMaxShare);
    }

    /// <summary>
    /// What an improvised depot brings in: a held port is reached by smugglers and fishing boats
    /// a blockade does not stop entirely, a held town has its warehouses and its shops.
    /// </summary>
    public static float DepotShare(Campaign c, Army army)
    {
        var map = c?.ProvinceMap;
        var nation = c?.NationById(army.NationId);
        if (map is null || nation is null || c.ProvinceController is null || army.ProvinceId <= 0 ||
            army.ProvinceId > map.ProvinceCount || c.ProvinceController[army.ProvinceId] != nation.MapIndex) return 0f;
        var share = 0f;
        var region = RegionOf(c, army.ProvinceId);
        if (region is not null) share += M.Clamp01(region.Urbanization) * c.Defines.PocketCityDepotShare;
        if (SeaLanes.TryHarbourPoint(map, army.ProvinceId, out _, out _))
        {
            var (latitude, longitude) = map.CenterOf(army.ProvinceId);
            var blockade = NavalOperations.BlockadePressure(c, nation, latitude, longitude);
            share += c.Defines.PocketPortDepotShare * (1f - M.Clamp01(blockade));
        }
        return share;
    }

    /// <summary>Divisions each country has cut off today, for the share of an air bridge each pocket gets.</summary>
    internal static Dictionary<int, float> CutOffDivisions(Campaign c)
    {
        var divisions = new Dictionary<int, float>();
        foreach (var army in c.Armies)
            if (army.CutOff && army.Divisions > 0f && army.EmbarkedFleetId < 0)
                divisions[army.NationId] = divisions.GetValueOrDefault(army.NationId) + army.Divisions;
        return divisions;
    }

    // ------------------------------------------------------------------ relief, breakout, surrender

    /// <summary>
    /// Whether a pocket the computer runs should stay where it is rather than break out: while
    /// it has stores and either it is a garrison holding out behind a rising's lines, or a
    /// formation of its own is fighting or marching near the friendly ground it would break out
    /// toward - relief is on its way, and a column on the road is weaker than one dug in.
    /// </summary>
    public static bool ShouldHold(Campaign c, Army army, List<int> wayHome)
    {
        if (army.PocketStores <= c.Defines.PocketBreakoutStoresDays) return false;
        if (army.HoldoutSinceDay >= 0) return true;
        return wayHome is { Count: > 0 } && ReliefComing(c, army, wayHome[^1]);
    }

    private static bool ReliefComing(Campaign c, Army army, int entry)
    {
        var map = c.ProvinceMap;
        var near = new HashSet<int> { entry };
        var ring = new List<int> { entry };
        for (var step = 0; step < Math.Max(0, c.Defines.PocketReliefRadius); step++)
        {
            var next = new List<int>();
            foreach (var province in ring)
                foreach (var neighbour in map.Neighbors[province])
                    if (neighbour > 0 && near.Add(neighbour)) next.Add(neighbour);
            ring = next;
        }
        foreach (var other in c.Armies)
            if (other.NationId == army.NationId && other.Id != army.Id && !other.CutOff && other.Divisions > 0f &&
                near.Contains(other.ProvinceId) && (other.InBattle || other.IsMoving))
                return true;
        return false;
    }

    /// <summary>
    /// Daily: a pocket whose stores are gone, with nobody coming and no road it is taking home,
    /// surrenders - sooner the longer it has gone hungry, the more shaken it is and while it is
    /// being attacked. Only at war; a formation stranded in peacetime has nobody to give up to.
    /// </summary>
    public static void Tick(Campaign c)
    {
        if (c?.ProvinceMap is null || !c.StepOpensNewDay || !c.Defines.StrandedGarrisonsEnabled) return;
        var d = c.Defines;
        List<Army> giving = null;
        foreach (var army in c.Armies)
        {
            if (!army.CutOff)
            {
                if (army.HoldoutSinceDay >= 0) Relieved(c, army);
                continue;
            }
            if (army.PocketStores != 0f || army.DaysCutOff < d.PocketSurrenderHungryDays || army.Divisions <= 0f ||
                army.EmbarkedFleetId >= 0 || MilitaryAccess.MustReturn(c, army)) continue;
            if (army.BreakingOut && (army.IsMoving || army.PlannedRoute.Count > 0)) continue;
            if (army.ReliefDistance >= 0 && army.ReliefDistance <= d.PocketReliefRadius + 1) continue;
            if (army.ReliefDistance == -2) continue; // a country cut in two, not a pocket
            if (WarLosses.WarOf(c, army) is null) continue;
            var weeks = (army.DaysCutOff - d.PocketSurrenderHungryDays) / 7f;
            var chance = d.PocketSurrenderDailyChance * (1f + weeks) * (1.4f - M.Clamp01(army.Cohesion)) *
                         (army.InBattle ? 1.5f : 1f);
            if (Roll(c.Seed, army.Id, army.NationId, c.ElapsedDays, 0x5A11) >= chance) continue;
            (giving ??= new List<Army>()).Add(army);
        }
        if (giving is null) return;
        foreach (var army in giving) Surrender(c, army);
        c.MarkStrategicChange();
    }

    /// <summary>The road is open again: a garrison that held out is an ordinary formation once more.</summary>
    private static void Relieved(Campaign c, Army army)
    {
        army.HoldoutSinceDay = -1;
        Count("holdouts_relieved");
        if (!Delegation.RunByAi(c.NationById(army.NationId))) return;
        army.DirectOrder = false;
        army.OrderKind = ArmyOrder.Manual;
    }

    private static void Surrender(Campaign c, Army army)
    {
        var province = army.ProvinceId;
        var nation = c.NationById(army.NationId);
        Count(army.HoldoutSinceDay >= 0 ? "holdouts_surrendered" : "pockets_surrendered");
        DiagnosticLogs.Write("simulation",
            $"POCKET_SURRENDER army={army.Id} nation={nation?.Tag} province={province} hungry={army.DaysCutOff} " +
            $"pocket_days={(army.PocketSinceDay >= 0 ? c.ElapsedDays - army.PocketSinceDay : -1)}");
        WarLosses.Surrender(c, army);
        Attrition.Destroy(c, army, "surrendered: a pocket out of stores");
        // Ground held only by the formation that gave up goes back to the country it belongs to,
        // when that country is the one it was fighting.
        var map = c.ProvinceMap;
        if (nation is null || province <= 0 || province > map.ProvinceCount ||
            c.ProvinceController[province] != nation.MapIndex) return;
        if (c.Armies.Any(other => other.NationId == nation.Id && other.ProvinceId == province)) return;
        var owner = NavalOperations.NationAt(c, map.ProvinceToNation[province]);
        if (owner is not null && owner.Id != nation.Id && CampaignDiplomacy.AtWar(c, owner.Id, nation.Id))
            c.SetProvinceController(province, (ushort)owner.MapIndex);
    }

    // ------------------------------------------------------------------ what the player reads

    /// <summary>
    /// One line about a formation in a pocket, for the army card and the map tooltip: how many
    /// days of stores are left (or how long it has gone hungry), and how far relief is. A read.
    /// </summary>
    public static string PocketLine(Campaign c, Army army)
    {
        if (army is null || !army.CutOff || c is not null && !c.Defines.StrandedGarrisonsEnabled) return "";
        string head;
        if (army.PocketStores > 0f)
            head = Loc.Format("In a pocket: {0} days of stores left", (int)MathF.Ceiling(army.PocketStores));
        else if (army.PocketStores == 0f)
            head = Loc.Format("In a pocket: the stores are gone, {0} days hungry", army.DaysCutOff);
        else
            head = Loc.Get("In a pocket: its stores are being counted");
        var relief = army.ReliefDistance >= 0
            ? Loc.Format(", relief {0} province(s) away.", army.ReliefDistance)
            : army.ReliefDistance == -2
                ? Loc.Get(", cut off together with a large part of the country.")
                : Loc.Get(", no friendly ground within reach.");
        var line = head + relief;
        if (army.HoldoutSinceDay >= 0)
            line = Loc.Get("Holding out behind the rising's lines.") + " " + line;
        if (army.BreakingOut) line += " " + Loc.Get("Breaking out toward friendly ground.");
        return line;
    }

    /// <summary>What stretches the stores today, for the supply tooltip. A read.</summary>
    public static string ReliefLine(Campaign c, Army army)
    {
        if (c is null || army is null || !army.CutOff) return "";
        var air = AirBridgeShare(c, army);
        var depot = DepotShare(c, army);
        var parts = new List<string>();
        if (air > 0.005f) parts.Add(Loc.Format("An air bridge carries {0:P0} of its needs.", air));
        if (depot > 0.005f) parts.Add(Loc.Format("A held port or town covers {0:P0}.", MathF.Min(depot, c.Defines.PocketReliefMaxShare)));
        return string.Join(" ", parts);
    }

    // ------------------------------------------------------------------ helpers

    private static ScenarioState RegionOf(Campaign c, int province)
    {
        var map = c?.ProvinceMap;
        if (map is null || province <= 0 || province > map.ProvinceCount) return null;
        var index = map.ProvinceToState[province];
        foreach (var state in c.States)
            if (state.MapIndex == index) return state;
        return null;
    }

    private static List<int> Enemies(Campaign c, int nationId)
    {
        var enemies = new List<int>();
        foreach (var war in c.Wars)
        {
            if (war.Ended || !war.Involves(nationId)) continue;
            foreach (var id in war.Side(!war.IsAttacking(nationId)))
                if (!enemies.Contains(id)) enemies.Add(id);
        }
        return enemies;
    }

    /// <summary>
    /// A deterministic draw in [0, 1). The salt keeps it apart from the defection roll
    /// (Reintegration), which hashes the same formation, rising and day: without it the formations
    /// that rolled high enough to stay loyal also rolled high enough to leave, every one of them.
    /// </summary>
    private static float Roll(int seed, int a, int b, int day, int salt)
    {
        unchecked
        {
            // Each input is folded in and mixed in turn (a murmur finaliser per step), so
            // neighbouring formation ids do not draw neighbouring numbers.
            var hash = Mix((uint)seed ^ (uint)salt * 2654435769u);
            hash = Mix(hash ^ (uint)a);
            hash = Mix(hash ^ (uint)b);
            hash = Mix(hash ^ (uint)day);
            return (hash & 0xFFFFFF) / (float)0x1000000;
        }
    }

    private static uint Mix(uint hash)
    {
        unchecked
        {
            hash ^= hash >> 16; hash *= 0x85EBCA6Bu;
            hash ^= hash >> 13; hash *= 0xC2B2AE35u;
            hash ^= hash >> 16;
            return hash;
        }
    }
}
