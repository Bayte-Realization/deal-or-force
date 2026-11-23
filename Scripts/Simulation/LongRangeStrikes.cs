using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Days between one formation's salvos. Each fires on its own day of the week (its id), so the load is spread.</summary>
    public int StrikeIntervalDays { get; set; } = 7;
    /// <summary>The share of what gets through that lands where it was meant to when nobody is watching the target.</summary>
    public float StrikeBlindPrecision { get; set; } = 0.45f;
    /// <summary>Air-defence points over the target at which half of an unevasive salvo would be shot down.</summary>
    public float StrikeInterceptionHalf { get; set; } = 6f;
    /// <summary>How fast a salvo larger than the screen saturates it: the share a screen stops falls with salvo / screen.</summary>
    public float StrikeSaturation { get; set; } = 0.5f;
    /// <summary>What a screen whose owner has no interceptor missiles left still stops, as a share: guns, jamming, fighters.</summary>
    public float StrikeDryInterceptors { get; set; } = 0.35f;
    /// <summary>What fighters on an air-superiority sweep add to the screen, per point of their air superiority.</summary>
    public float StrikeFighterInterception { get; set; } = 0.5f;
    /// <summary>Interceptor missiles the defender spends per strike point it shoots down, from its Missiles depot.</summary>
    public float StrikeInterceptorCost { get; set; } = 0.08f;
    /// <summary>Organisation lost by a front stack per strike point that lands, shared over its divisions.</summary>
    public float StrikeFrontOrganisation { get; set; } = 0.04f;
    /// <summary>Equipment destroyed in a front stack per strike point that lands, shared over its divisions.</summary>
    public float StrikeFrontEquipment { get; set; } = 0.012f;
    /// <summary>Ammunition, fuel and arms burnt in the target country's depots per strike point that lands on a logistics hub.</summary>
    public float StrikeDepotDrain { get; set; } = 0.5f;
    /// <summary>War damage per strike point to the region of a logistics hub, an industrial region, a power grid.</summary>
    public float StrikeLogisticsDamage { get; set; } = 0.004f;
    public float StrikeIndustryDamage { get; set; } = 0.006f;
    public float StrikeEnergyDamage { get; set; } = 0.003f;
    /// <summary>Stability the target country loses, and standard of living the region loses, per strike point on its grid.</summary>
    public float StrikeEnergyStability { get; set; } = 0.0015f;
    public float StrikeEnergyLiving { get; set; } = 0.002f;
    /// <summary>The war damage strikes alone can bring a region to. The rest of the ruin takes a ground war.</summary>
    public float StrikeDamageCeiling { get; set; } = 0.6f;
    /// <summary>How often a country nobody plays reviews its long-range procurement while at war, in days.</summary>
    public int AiLongRangeProcurementDays { get; set; } = 60;
    /// <summary>Treasury, in multiples of the formation's price, a country nobody plays keeps after buying one.</summary>
    public float AiLongRangeReserve { get; set; } = 4f;
}

public sealed partial class Campaign
{
    /// <summary>The last salvo of each formation, by id, for the long-range page. Not saved: the
    /// next salvo writes it again, and nothing in the simulation reads it.</summary>
    public readonly Dictionary<int, LongRangeStrikes.Report> SupportStrikeReports = new();
}

/// <summary>
/// The deep-strike war: what Hearts of Iron never had and every war since 2022 has been fought
/// with. Missiles, one-way drones, glide bombs and long-range rockets fired at what stands
/// behind the line - the formations in it, the depots and railheads that feed them, the plants
/// that make their shells, and the grid their cities run on - against an air defence that shoots
/// part of each salvo down and runs out of interceptors doing it.
///
/// One salvo a week per formation on the "strike" mission, on its own day of the week:
///
///   1. A TARGET. The kind is the player's (StrikeTarget) or the staff's automatic choice by
///      weapon; the region is the player's (TargetStateMapIndex) or the best one of that kind
///      inside the reach: the enemy stack with the most fighting power for "front", the region
///      that feeds the most enemy divisions for "logistics", the most arms plants for
///      "industry", the most people for "energy".
///   2. MUNITIONS. The salvo is fired from the national depot of what the weapon fires
///      (Missiles, Drones, Ammunition - StrategicStockpiles, made by the arms plants). A depot
///      short of a full salvo fires what it has; an empty one fires nothing.
///   3. INTERCEPTION. The enemy's air-defence brigades and sweeping fighters that reach the
///      target form a screen. The screen stops a share of the salvo that grows with its
///      density, falls with the size of the salvo (saturation) and with the weapon's evasion,
///      and falls to a third when the defender has no interceptor missiles left - which every
///      interception draws down.
///   4. PRECISION. What gets through lands as hard as the reconnaissance over the target
///      allows: a little under half when nobody is watching, all of it when one of the side's
///      reconnaissance flights sees the target (AirReconnaissance.SightKm).
///   5. EFFECT, by target: organisation and equipment out of the enemy stack; ammunition, fuel
///      and arms out of the enemy's depots and war damage (supply throughput) in the hub's
///      region; war damage (output) in an industrial region; war damage, living standards and
///      national stability for the grid. Strikes alone never ruin a region past
///      <see cref="Defines.StrikeDamageCeiling"/>.
///   6. LOSSES. Crewed aircraft fly into the target's sky to launch and pay for it the way a
///      close-support wing does (SupportCore.Attrition). Missiles and drones are the munition:
///      they are lost instead of the formation.
///
/// Cheap by construction: a formation fires once a week, and a salvo walks the armies and the
/// regions once, not a province loop per formation per hour. Deterministic: no dice - the
/// shares are expected values, so a seed replays its strikes exactly and two peers agree.
/// </summary>
public static class LongRangeStrikes
{
    public const string TargetFront = "front";
    public const string TargetLogistics = "logistics";
    public const string TargetIndustry = "industry";
    public const string TargetEnergy = "energy";
    public static readonly string[] Targets = { TargetFront, TargetLogistics, TargetIndustry, TargetEnergy };

    /// <summary>What one salvo did, for the page and the tests.</summary>
    public sealed class Report
    {
        public int Day;
        public string Target = "";
        public int StateMapIndex = -1;
        public int Province = -1;
        public int TargetNationId = -1;
        public float DistanceKm;
        /// <summary>Strike points launched, intercepted, and landed (after precision).</summary>
        public float Launched;
        public float Intercepted;
        public float Landed;
        public float Precision;
        /// <summary>Munitions drawn from the depot for the salvo.</summary>
        public float MunitionsSpent;
        /// <summary>Why nothing was fired, or why the named target was not the one struck; empty when all went to plan.</summary>
        public string Note = "";
    }

    /// <summary>A salvo's target and the odds over it, before anything is fired. Read-only: the page previews it.</summary>
    public sealed class Plan
    {
        public string Target = "";
        public int StateMapIndex = -1;
        public int Province = -1;
        public int TargetNationId = -1;
        public float DistanceKm;
        public float InterceptShare;
        public float Precision;
        public bool Spotted;
        /// <summary>0 when the enemy owns the sky over the target, 1 when we do, -1 when nobody flies there.</summary>
        public float AirControl = -1f;
        public SupportCore.SideReach Own;
        public SupportCore.SideReach Enemy;
        public string Refusal = "";
    }

    // ------------------------------------------------------------------ bench telemetry

    /// <summary>Totals for the bench (--pulse). Telemetry only: nothing in the simulation reads them.</summary>
    public static int Salvos, DryLaunches, Procured;
    public static float LaunchedPoints, InterceptedPoints, LandedPoints;
    public static readonly Dictionary<string, int> ByTarget = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ region anchors

    private static readonly ConditionalWeakTable<ProvinceTopology, int[]> AnchorTables = new();

    /// <summary>
    /// The province a region is struck at and measured to: the one of its provinces nearest the
    /// middle of all of them. Fixed by the map, so computed once per topology.
    /// </summary>
    internal static int AnchorOf(ProvinceTopology map, int stateMapIndex)
    {
        if (map is null || stateMapIndex < 0) return -1;
        var table = AnchorTables.GetValue(map, BuildAnchors);
        return stateMapIndex < table.Length ? table[stateMapIndex] : -1;
    }

    private static int[] BuildAnchors(ProvinceTopology map)
    {
        var top = 0;
        for (var p = 1; p <= map.ProvinceCount && p < map.ProvinceToState.Length; p++)
            top = Math.Max(top, map.ProvinceToState[p]);
        var sums = new (double X, double Y, double Z)[top + 1];
        for (var p = 1; p <= map.ProvinceCount && p < map.ProvinceToState.Length; p++)
        {
            if (map.IsWater(p)) continue;
            var (x, y, z) = Direction(map.CenterOf(p));
            ref var sum = ref sums[map.ProvinceToState[p]];
            sum = (sum.X + x, sum.Y + y, sum.Z + z);
        }
        var anchors = Enumerable.Repeat(-1, top + 1).ToArray();
        var best = new double[top + 1];
        Array.Fill(best, double.NegativeInfinity);
        for (var p = 1; p <= map.ProvinceCount && p < map.ProvinceToState.Length; p++)
        {
            if (map.IsWater(p)) continue;
            var state = map.ProvinceToState[p];
            var (x, y, z) = Direction(map.CenterOf(p));
            var score = x * sums[state].X + y * sums[state].Y + z * sums[state].Z;
            if (score <= best[state]) continue;
            best[state] = score;
            anchors[state] = p;
        }
        return anchors;
    }

    private static (double, double, double) Direction((float Latitude, float Longitude) centre)
    {
        var lat = centre.Latitude * (Math.PI / 180.0);
        var lon = centre.Longitude * (Math.PI / 180.0);
        return (Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), Math.Cos(lat) * Math.Sin(lon));
    }

    // ------------------------------------------------------------------ sides of the war

    /// <summary>Who fights beside a country and who against it, in every war it is in.</summary>
    internal sealed class Sides
    {
        public readonly List<int> Own = new();
        public readonly HashSet<int> Enemies = new();
        public readonly HashSet<int> EnemyMaps = new();
        public List<(ScenarioState State, int Anchor)> EnemyStates;

        public void Reset()
        {
            Own.Clear();
            Enemies.Clear();
            EnemyMaps.Clear();
            EnemyStates = null;
        }
    }

    internal static Sides SidesOf(Campaign c, int nationId)
    {
        var sides = new Sides();
        FillSides(c, nationId, sides);
        return sides;
    }

    internal static void FillSides(Campaign c, int nationId, Sides sides)
    {
        sides.Reset();
        sides.Own.Add(nationId);
        var openWars = Warfare.OpenWars(c);
        for (var wi = 0; wi < openWars.Count; wi++)
        {
            var war = openWars[wi];
            if (war.Ended || !war.Involves(nationId)) continue;
            var attacking = war.IsAttacking(nationId);
            foreach (var id in war.Side(attacking)) if (!sides.Own.Contains(id)) sides.Own.Add(id);
            foreach (var id in war.Side(!attacking))
            {
                sides.Enemies.Add(id);
                if (c.NationById(id) is { } enemy) sides.EnemyMaps.Add(enemy.MapIndex);
            }
        }
    }

    /// <summary>Whether anything of the enemy's flies: the reason to sweep the sky rather than bomb.</summary>
    internal static bool EnemyFlies(Campaign c, int nationId)
    {
        foreach (var enemy in SidesOf(c, nationId).Enemies)
        {
            if (!c.SupportByNation.TryGetValue(enemy, out var formations)) continue;
            foreach (var formation in formations)
                if (SupportTypeCatalog.TryGet(formation.TypeId, out var type) &&
                    type.Kind is SupportKind.Air or SupportKind.Drone) return true;
        }
        return false;
    }

    /// <summary>Whether the depot holds this many full salvos of what the type fires (always, with no munition or no depots).</summary>
    public static bool DepotHolds(Campaign c, Nation nation, SupportTypeDefinition type, float salvos)
    {
        if (nation is null || type is null || type.Munition.Length == 0 || type.MunitionPerSalvo <= 0f ||
            !c.Defines.StrategicStockpilesEnabled) return true;
        return StrategicStockpiles.Peek(c, nation, type.Munition) >= type.MunitionPerSalvo * salvos;
    }

    /// <summary>
    /// The staff's own choice of target, by what the formation fires: rockets and glide bombs at
    /// the line, missiles at the depots, long-range drones at the grid - each falling back on the
    /// others when nothing of the kind is in reach.
    /// </summary>
    public static string AutoTarget(SupportTypeDefinition type) =>
        type.Munition == "Drones" && type.RangeKm >= 800f ? TargetEnergy
        : type.Munition == "Missiles" ? TargetLogistics
        : TargetFront;

    // ------------------------------------------------------------------ the daily pass

    /// <summary>Every formation on the strike mission whose day of the week it is fires one salvo.</summary>
    public static void Fire(Campaign c)
    {
        foreach (var _ in FireStages(c)) { }
    }

    internal static IEnumerable<int> FireStages(Campaign c)
    {
        if (c.ProvinceMap is null || c.SupportFormations.Count == 0) yield break;
        var interval = Math.Max(1, c.Defines.StrikeIntervalDays);
        var pass = c.Caches.SupportPass;
        var due = pass.FireDue;
        due.Clear();
        var formations = c.SupportFormations;
        for (var i = 0; i < formations.Count; i++)
        {
            var f = formations[i];
            if (f.Mission != SupportCore.MissionStrike) continue;
            if (((c.ElapsedDays + f.Id) % interval + interval) % interval != 0) continue;
            due.Add(f);
        }
        if (due.Count == 0) yield break;

        var sides = pass.FireSides;
        sides.Clear();
        var fired = 0;
        try
        {
            for (var i = 0; i < due.Count; i++)
            {
                var formation = due[i];
                if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type) || !SupportCore.Enabled(c, type) ||
                    type.Strike <= 0f || !type.Missions.Contains(SupportCore.MissionStrike)) continue;
                var nation = c.NationById(formation.NationId);
                if (nation is null || SupportCore.Displaced(c, formation)) continue;
                if (!sides.TryGetValue(nation.Id, out var side)) sides[nation.Id] = side = SidesOf(c, nation.Id);
                if (side.Enemies.Count == 0) continue;
                Salvo(c, formation, type, nation, side);
                fired++;
                if (i + 1 < due.Count && Campaign.StageOverdue()) yield return 0;
            }
        }
        finally
        {
            due.Clear();
            sides.Clear();
        }
    }

    private static void Salvo(Campaign c, SupportFormation formation, SupportTypeDefinition type, Nation nation,
        Sides side)
    {
        var d = c.Defines;
        var report = new Report { Day = c.ElapsedDays };
        c.SupportStrikeReports[formation.Id] = report;
        var plan = PlanFor(c, formation, type, side);
        report.Target = plan.Target;
        report.StateMapIndex = plan.StateMapIndex;
        report.Province = plan.Province;
        report.TargetNationId = plan.TargetNationId;
        report.DistanceKm = plan.DistanceKm;
        report.Precision = plan.Precision;
        report.Note = plan.Refusal;
        if (plan.Province <= 0) return;

        // The munitions: a salvo is what the depot holds of a full one.
        var weight = formation.Strength * (1f + formation.Experience * 0.35f);
        var supplied = 1f;
        if (type.Munition.Length > 0 && type.MunitionPerSalvo > 0f && d.StrategicStockpilesEnabled)
        {
            var need = type.MunitionPerSalvo * M.Clamp01(formation.Strength);
            var have = StrategicStockpiles.Get(c, nation, type.Munition);
            if (have < need * 0.05f)
            {
                report.Note = "The depot is empty: nothing was fired.";
                DryLaunches++;
                return;
            }
            var spent = MathF.Min(have, need);
            supplied = need <= 0f ? 1f : spent / need;
            nation.Stockpiles[type.Munition] = MathF.Round(have - spent, 3);
            report.MunitionsSpent = spent;
        }
        var launched = type.Strike * weight * supplied;

        // The screen over the target, recomputed for the salvo actually fired (saturation
        // depends on its size), and what the defender spends doing it.
        var ctrl = Controller(c, plan.Province);
        var target = ctrl > 0 ? c.NationByMapIndex(ctrl) : null;
        var share = InterceptShare(c, type, launched, plan.Enemy, target);
        var intercepted = launched * share;
        if (intercepted > 0f && target is not null && d.StrategicStockpilesEnabled)
        {
            var interceptors = StrategicStockpiles.Get(c, target, "Missiles");
            target.Stockpiles["Missiles"] = MathF.Round(MathF.Max(0f, interceptors - intercepted * d.StrikeInterceptorCost), 3);
        }
        var landed = (launched - intercepted) * plan.Precision;
        report.Launched = launched;
        report.Intercepted = intercepted;
        report.Landed = landed;

        // Crewed aircraft fly into that sky to launch.
        if (type.Kind == SupportKind.Air)
        {
            var threat = SupportCore.ThreatTo(type, SupportCore.MissionStrike, plan.Own, plan.Enemy);
            if (threat > 0f && (!c.SupportExposure.TryGetValue(formation.Id, out var worst) || threat > worst))
                c.SupportExposure[formation.Id] = threat;
        }

        Salvos++;
        LaunchedPoints += launched;
        InterceptedPoints += intercepted;
        LandedPoints += landed;
        ByTarget[plan.Target] = ByTarget.GetValueOrDefault(plan.Target) + 1;
        if (landed <= 0f) return;
        Land(c, side, plan, landed, target);
    }

    private static readonly string[] DepotDrainGoods = { "Ammunition", "Oil", "Arms" };

    /// <summary>What a salvo that got through does to what it was aimed at.</summary>
    private static void Land(Campaign c, Sides side, Plan plan, float landed, Nation target)
    {
        var d = c.Defines;
        var state = plan.StateMapIndex > 0
            ? c.StateByMapIndex(plan.StateMapIndex)
            : c.States.Find(s => s.MapIndex == plan.StateMapIndex);
        switch (plan.Target)
        {
            case TargetFront:
            {
                // The stack is the striker's enemies standing in the province, shared by divisions:
                // a salvo into a corps hurts each of its divisions less than one into a brigade.
                var divisions = 0f;
                var armies = c.Armies;
                for (var i = 0; i < armies.Count; i++)
                {
                    var army = armies[i];
                    if (army.ProvinceId == plan.Province && army.Divisions > 0f && side.Enemies.Contains(army.NationId))
                        divisions += army.Divisions;
                }
                if (divisions <= 0f) return;
                var organisation = MathF.Min(0.3f, d.StrikeFrontOrganisation * landed / MathF.Max(1f, divisions));
                var equipment = MathF.Min(0.1f, d.StrikeFrontEquipment * landed / MathF.Max(1f, divisions));
                for (var i = 0; i < armies.Count; i++)
                {
                    var army = armies[i];
                    if (army.ProvinceId != plan.Province || army.Divisions <= 0f || !side.Enemies.Contains(army.NationId)) continue;
                    army.Organisation = M.Clamp01(army.Organisation - organisation);
                    army.Equipment = MathF.Max(MathF.Min(army.Equipment, 0.2f), army.Equipment - equipment);
                }
                return;
            }
            case TargetLogistics:
                if (target is not null && d.StrategicStockpilesEnabled)
                    for (var gi = 0; gi < DepotDrainGoods.Length; gi++)
                    {
                        var good = DepotDrainGoods[gi];
                        var have = StrategicStockpiles.Get(c, target, good);
                        target.Stockpiles[good] = MathF.Round(MathF.Max(0f, have - d.StrikeDepotDrain * landed), 3);
                    }
                Ruin(state, d.StrikeLogisticsDamage * landed, d.StrikeDamageCeiling);
                return;
            case TargetIndustry:
                Ruin(state, d.StrikeIndustryDamage * landed, d.StrikeDamageCeiling);
                return;
            case TargetEnergy:
                Ruin(state, d.StrikeEnergyDamage * landed, d.StrikeDamageCeiling);
                if (state is not null)
                    state.StandardOfLiving = M.Clamp01(state.StandardOfLiving - d.StrikeEnergyLiving * landed);
                if (target is not null)
                    target.Stability = M.Clamp01(target.Stability - d.StrikeEnergyStability * landed);
                return;
        }
    }

    private static void Ruin(ScenarioState state, float amount, float ceiling)
    {
        if (state is null || amount <= 0f || state.WarDamage >= ceiling) return;
        state.WarDamage = MathF.Min(ceiling, state.WarDamage + amount * (1f - state.WarDamage));
    }

    private static int Controller(Campaign c, int province)
    {
        var map = c.ProvinceMap;
        if (map is null || province <= 0 || province > map.ProvinceCount) return -1;
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        return province < controllers.Length ? controllers[province] : -1;
    }

    // ------------------------------------------------------------------ the odds

    /// <summary>
    /// The share of a salvo of this size the screen over the target stops: denser screens stop
    /// more, bigger salvos saturate them, evasive weapons slip them, and a defender out of
    /// interceptor missiles stops a third of what it would.
    /// </summary>
    public static float InterceptShare(Campaign c, SupportTypeDefinition type, float salvo, SupportCore.SideReach enemy, Nation defender)
    {
        var d = c.Defines;
        if (enemy is null) return 0f;
        var screen = enemy.AirDefence + MathF.Max(0f, d.StrikeFighterInterception) * enemy.AirSuperiority;
        if (screen <= 0f) return 0f;
        if (defender is not null && d.StrategicStockpilesEnabled && StrategicStockpiles.Peek(c, defender, "Missiles") < 0.01f)
            screen *= M.Clamp01(d.StrikeDryInterceptors);
        var density = screen / (screen + MathF.Max(0.1f, d.StrikeInterceptionHalf));
        var saturation = 1f / (1f + MathF.Max(0f, d.StrikeSaturation) * MathF.Max(0f, salvo) / MathF.Max(1f, screen));
        return M.Clamp01(density * saturation * (1f - M.Clamp01(type.Evasion)));
    }

    /// <summary>
    /// Choose the target and work out the odds over it, without firing. Used by the salvo and by
    /// the page that previews it: the player sees exactly what the staff will do.
    /// </summary>
    public static Plan Preview(Campaign c, SupportFormation formation)
    {
        if (c?.ProvinceMap is null || formation is null || !SupportTypeCatalog.TryGet(formation.TypeId, out var type))
            return new Plan { Refusal = "No such formation." };
        if (type.Strike <= 0f) return new Plan { Refusal = "This formation cannot strike deep." };
        if (SupportCore.Displaced(c, formation)) return new Plan { Refusal = "Not deployed: it strikes once it has arrived and deployed." };
        var side = SidesOf(c, formation.NationId);
        if (side.Enemies.Count == 0) return new Plan { Refusal = "We are at war with nobody." };
        return PlanFor(c, formation, type, side);
    }

    /// <summary>Fast check for AI repositioning: whether any enemy target of any kind lies in reach, without computing air-screen odds.</summary>
    internal static bool HasTargetInReach(Campaign c, SupportFormation formation, SupportTypeDefinition type, Sides side, Plan scratchPlan = null)
    {
        if (c?.ProvinceMap is null || formation is null || type is null || type.Strike <= 0f || side is null || side.Enemies.Count == 0)
            return false;
        if (SupportCore.Displaced(c, formation)) return false;
        var plan = scratchPlan ?? new Plan();
        var wanted = Targets.Contains(formation.StrikeTarget) ? formation.StrikeTarget : AutoTarget(type);
        var named = formation.TargetStateMapIndex;
        if (named >= 0)
        {
            var anchor = AnchorOf(c.ProvinceMap, named);
            var enemyHeld = anchor > 0 && side.EnemyMaps.Contains(Controller(c, anchor));
            if (enemyHeld && SupportCore.DistanceKm(c, formation.BaseProvinceId, anchor) <= SupportCore.ReachKm(c, type) &&
                Choose(c, formation, type, side, wanted, named, plan))
                return true;
        }
        if (Choose(c, formation, type, side, wanted, -1, plan)) return true;
        if (wanted != TargetLogistics && Choose(c, formation, type, side, TargetLogistics, -1, plan)) return true;
        if (wanted != TargetFront && Choose(c, formation, type, side, TargetFront, -1, plan)) return true;
        if (wanted != TargetIndustry && Choose(c, formation, type, side, TargetIndustry, -1, plan)) return true;
        if (wanted != TargetEnergy && Choose(c, formation, type, side, TargetEnergy, -1, plan)) return true;
        return false;
    }

    private static List<(ScenarioState State, int Anchor)> EnemyStatesOf(Campaign c, Sides side)
    {
        if (side.EnemyStates is not null) return side.EnemyStates;
        var map = c.ProvinceMap;
        var list = new List<(ScenarioState, int)>();
        var states = c.States;
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            var anchor = AnchorOf(map, state.MapIndex);
            if (anchor > 0 && side.EnemyMaps.Contains(Controller(c, anchor)))
                list.Add((state, anchor));
        }
        side.EnemyStates = list;
        return list;
    }

    private static Plan PlanFor(Campaign c, SupportFormation formation, SupportTypeDefinition type, Sides side)
    {
        var plan = new Plan();
        var wanted = Targets.Contains(formation.StrikeTarget) ? formation.StrikeTarget : AutoTarget(type);
        var named = formation.TargetStateMapIndex;
        if (named >= 0)
        {
            var anchor = AnchorOf(c.ProvinceMap, named);
            var enemyHeld = anchor > 0 && side.EnemyMaps.Contains(Controller(c, anchor));
            if (enemyHeld && SupportCore.DistanceKm(c, formation.BaseProvinceId, anchor) <= SupportCore.ReachKm(c, type))
            {
                if (Choose(c, formation, type, side, wanted, named, plan)) { Odds(c, formation, type, side, plan); return plan; }
                plan.Refusal = wanted == TargetFront ? "No enemy forces stand in the named region." : "";
            }
            else plan.Refusal = enemyHeld ? "The named region is out of reach." : "The named region is no longer the enemy's.";
        }
        // The staff's own pick: the kind asked for first, then the others in order of use.
        if (Choose(c, formation, type, side, wanted, -1, plan) ||
            (wanted != TargetLogistics && Choose(c, formation, type, side, TargetLogistics, -1, plan)) ||
            (wanted != TargetFront && Choose(c, formation, type, side, TargetFront, -1, plan)) ||
            (wanted != TargetIndustry && Choose(c, formation, type, side, TargetIndustry, -1, plan)) ||
            (wanted != TargetEnergy && Choose(c, formation, type, side, TargetEnergy, -1, plan)))
        {
            Odds(c, formation, type, side, plan);
            return plan;
        }
        plan.Target = wanted;
        plan.Refusal = "Nothing of the enemy's is in reach.";
        return plan;
    }

    [ThreadStatic] private static Dictionary<int, float> _chooseReachableScratch;
    [ThreadStatic] private static Dictionary<int, float> _choosePowerScratch;
    [ThreadStatic] private static Dictionary<int, float> _chooseFedScratch;
    [ThreadStatic] private static List<int> _chooseKeysScratch;

    /// <summary>The best target of one kind inside the reach (or inside one named region), into the plan.</summary>
    private static bool Choose(Campaign c, SupportFormation formation, SupportTypeDefinition type, Sides side,
        string kind, int onlyState, Plan plan)
    {
        var map = c.ProvinceMap;
        var origin = map.CenterOf(formation.BaseProvinceId);
        var reach = SupportCore.ReachKm(c, type);
        var band = reach / 111f;
        var reachable = _chooseReachableScratch ??= new Dictionary<int, float>();
        reachable.Clear();
        bool InReach(int province, out float km)
        {
            km = 0f;
            if (province <= 0 || province > map.ProvinceCount) return false;
            if (reachable.TryGetValue(province, out km)) return km >= 0f;
            km = MathF.Abs(map.CenterOf(province).Latitude - origin.Latitude) > band
                ? float.MaxValue : SupportCore.DistanceKm(c, formation.BaseProvinceId, province);
            if (km > reach) { reachable[province] = -1f; return false; }
            reachable[province] = km;
            return true;
        }

        var bestScore = 0f;
        var bestProvince = -1;
        var bestState = -1;
        var bestKm = 0f;
        void Offer(float score, int province, int state, float km)
        {
            if (score <= bestScore) return;
            bestScore = score; bestProvince = province; bestState = state; bestKm = km;
        }

        switch (kind)
        {
            case TargetFront:
            {
                var power = _choosePowerScratch ??= new Dictionary<int, float>();
                power.Clear();
                var armies = c.Armies;
                for (var i = 0; i < armies.Count; i++)
                {
                    var army = armies[i];
                    if (army.Divisions <= 0f || army.ProvinceId <= 0 || army.EmbarkedFleetId >= 0 ||
                        !side.Enemies.Contains(army.NationId)) continue;
                    if (onlyState >= 0 && map.ProvinceToState[army.ProvinceId] != onlyState) continue;
                    power[army.ProvinceId] = power.GetValueOrDefault(army.ProvinceId) + army.Power * (army.InBattle ? 1.5f : 1f);
                }
                if (power.Count > 0)
                {
                    var keys = _chooseKeysScratch ??= new List<int>();
                    keys.Clear();
                    foreach (var p in power.Keys) keys.Add(p);
                    if (keys.Count > 1) keys.Sort();
                    for (var i = 0; i < keys.Count; i++)
                    {
                        var province = keys[i];
                        if (InReach(province, out var km)) Offer(power[province], province, map.ProvinceToState[province], km);
                    }
                }
                break;
            }
            case TargetLogistics:
            {
                var fed = _chooseFedScratch ??= new Dictionary<int, float>();
                fed.Clear();
                var armies = c.Armies;
                for (var i = 0; i < armies.Count; i++)
                {
                    var army = armies[i];
                    if (army.Divisions <= 0f || !side.Enemies.Contains(army.NationId)) continue;
                    // The region the formation draws its supply from; before the first supply
                    // trace has run, the region it stands in - its railhead is not far behind.
                    var source = army.SupplySourceStateMapIndex >= 0 ? army.SupplySourceStateMapIndex
                        : army.ProvinceId > 0 && army.ProvinceId <= map.ProvinceCount ? map.ProvinceToState[army.ProvinceId] : -1;
                    if (source < 0 || onlyState >= 0 && source != onlyState) continue;
                    fed[source] = fed.GetValueOrDefault(source) + army.Divisions;
                }
                // A named region that feeds nobody is still a depot: it is struck for what it holds.
                if (onlyState >= 0 && fed.Count == 0) fed[onlyState] = 0.01f;
                if (fed.Count > 0)
                {
                    var keys = _chooseKeysScratch ??= new List<int>();
                    keys.Clear();
                    foreach (var s in fed.Keys) keys.Add(s);
                    if (keys.Count > 1) keys.Sort();
                    for (var i = 0; i < keys.Count; i++)
                    {
                        var state = keys[i];
                        var anchor = AnchorOf(map, state);
                        if (anchor <= 0 || !side.EnemyMaps.Contains(Controller(c, anchor))) continue;
                        if (InReach(anchor, out var km)) Offer(fed[state], anchor, state, km);
                    }
                }
                break;
            }
            case TargetIndustry:
            case TargetEnergy:
            {
                var enemyStates = EnemyStatesOf(c, side);
                for (var si = 0; si < enemyStates.Count; si++)
                {
                    var (state, anchor) = enemyStates[si];
                    if (onlyState >= 0 && state.MapIndex != onlyState) continue;
                    if (!side.EnemyMaps.Contains(Controller(c, anchor))) continue;
                    var undamaged = 1f - M.Clamp01(state.WarDamage) / MathF.Max(0.01f, c.Defines.StrikeDamageCeiling);
                    if (undamaged <= 0f) continue;
                    var score = kind == TargetIndustry
                        ? (3f * state.Buildings.GetValueOrDefault(BuildingKind.ArmsIndustry) +
                           2f * state.Buildings.GetValueOrDefault(BuildingKind.Refinery) +
                           state.Buildings.GetValueOrDefault(BuildingKind.ElectronicsPlant) + 0.1f) * undamaged
                        : state.Population / 1_000_000f * (0.3f + state.Urbanization) * undamaged;
                    if (score <= 0f || !InReach(anchor, out var km)) continue;
                    Offer(score, anchor, state.MapIndex, km);
                }
                break;
            }
        }
        if (bestProvince <= 0) return false;
        plan.Target = kind;
        plan.Province = bestProvince;
        plan.StateMapIndex = bestState;
        plan.DistanceKm = bestKm;
        var controller = Controller(c, bestProvince);
        if (kind == TargetFront)
        {
            var targetNation = -1;
            var bestDivs = float.NegativeInfinity;
            var bestArmyId = int.MaxValue;
            var armies = c.Armies;
            for (var i = 0; i < armies.Count; i++)
            {
                var army = armies[i];
                if (army.ProvinceId != bestProvince || !side.Enemies.Contains(army.NationId)) continue;
                if (army.Divisions > bestDivs || (army.Divisions == bestDivs && army.Id < bestArmyId))
                {
                    bestDivs = army.Divisions;
                    bestArmyId = army.Id;
                    targetNation = army.NationId;
                }
            }
            plan.TargetNationId = targetNation;
        }
        else
        {
            plan.TargetNationId = controller > 0 ? c.NationByMapIndex(controller)?.Id ?? -1 : -1;
        }
        return true;
    }

    /// <summary>The sky over the chosen target: who reaches it, the share a full salvo loses, the precision.</summary>
    private static void Odds(Campaign c, SupportFormation formation, SupportTypeDefinition type, Sides side, Plan plan)
    {
        plan.Own = SupportCore.ReachOf(c, side.Own, plan.Province);
        plan.Enemy = SupportCore.ReachOf(c, side.Enemies, plan.Province);
        var ours = plan.Own.AirSuperiority + plan.Own.AirDefence;
        var theirs = plan.Enemy.AirSuperiority + plan.Enemy.AirDefence;
        plan.AirControl = ours + theirs <= 0.01f ? -1f : ours / (ours + theirs);
        var weight = formation.Strength * (1f + formation.Experience * 0.35f);
        var controller = Controller(c, plan.Province);
        var defender = controller > 0 ? c.NationByMapIndex(controller) : null;
        plan.InterceptShare = InterceptShare(c, type, type.Strike * weight, plan.Enemy, defender);

        // Precision: the formation's own sensors see a little; a reconnaissance flight of its
        // side that sees the target sees it all.
        var spotted = M.Clamp01(type.Recon / MathF.Max(0.1f, c.Defines.ReconFullSight)) * 0.5f;
        foreach (var nationId in side.Own)
        {
            if (!c.SupportByNation.TryGetValue(nationId, out var formations)) continue;
            foreach (var scout in formations)
            {
                if (scout.Mission != AirReconnaissance.Mission || scout.BaseProvinceId <= 0 ||
                    !SupportTypeCatalog.TryGet(scout.TypeId, out var scoutType) || SupportCore.Displaced(c, scout)) continue;
                if (SupportCore.DistanceKm(c, scout.BaseProvinceId, plan.Province) > AirReconnaissance.SightKm(c, scout, scoutType)) continue;
                var seen = M.Clamp01(0.5f + 0.5f * scout.Strength);
                if (seen > spotted) spotted = seen;
            }
        }
        plan.Spotted = spotted >= 0.5f;
        var blind = M.Clamp01(c.Defines.StrikeBlindPrecision);
        plan.Precision = blind + (1f - blind) * M.Clamp01(spotted);
    }

    // ------------------------------------------------------------------ the AI's procurement

    /// <summary>
    /// What a country nobody plays buys for the deep war while it is in one: air defence first
    /// when the enemy can strike and the sky over its own cities is open, then a strike arm of
    /// its own - missiles for an economy that makes them, one-way drones for the rest. One
    /// formation per review, only out of a treasury that keeps <see cref="Defines.AiLongRangeReserve"/>
    /// times the price, and never past what an economy its size can keep (one of each per few
    /// hundred billion of GDP). The same prices, posts and validation a player meets.
    /// </summary>
    public static void Procure(Campaign c)
    {
        foreach (var _ in ProcureStages(c)) { }
    }

    internal static IEnumerable<int> ProcureStages(Campaign c)
    {
        var d = c.Defines;
        var every = Math.Max(1, d.AiLongRangeProcurementDays);
        if (c.ProvinceMap is null) yield break;
        var pass = c.Caches.SupportPass;
        var atWar = pass.ProcureAtWar;
        var usedByState = pass.ProcureUsedByState;
        var regions = pass.ProcureTopRegions;
        var side = pass.ProcureSides;
        var builtAtWar = false;
        var builtUsed = false;
        var nations = c.Nations;
        try
        {
            for (var ni = 0; ni < nations.Count; ni++)
            {
                var nation = nations[ni];
                if (nation.IsHuman || nation.Movement is not null || nation.Population <= 0 || nation.Gdp < 30f) continue;
                if ((c.ElapsedDays + nation.Id) % every != 0) continue;
                if (!builtAtWar)
                {
                    builtAtWar = true;
                    atWar.Clear();
                    var openWars = Warfare.OpenWars(c);
                    for (var wi = 0; wi < openWars.Count; wi++)
                    {
                        var war = openWars[wi];
                        if (war.Ended || war.CivilConflict) continue;
                        foreach (var id in war.Side(true)) atWar.Add(id);
                        foreach (var id in war.Side(false)) atWar.Add(id);
                    }
                }
                if (!atWar.Contains(nation.Id)) continue;
                FillSides(c, nation.Id, side);
                var enemyStrikes = 0;
                foreach (var enemy in side.Enemies)
                {
                    if (!c.SupportByNation.TryGetValue(enemy, out var enemyForms)) continue;
                    for (var fi = 0; fi < enemyForms.Count; fi++)
                        if (SupportTypeCatalog.TryGet(enemyForms[fi].TypeId, out var t) && t.Strike > 0f) enemyStrikes++;
                }
                int ownDefence = 0, ownStrike = 0;
                if (c.SupportByNation.TryGetValue(nation.Id, out var ownForms))
                {
                    for (var fi = 0; fi < ownForms.Count; fi++)
                        if (SupportTypeCatalog.TryGet(ownForms[fi].TypeId, out var t))
                        {
                            if (t.Kind == SupportKind.AirDefence) ownDefence++;
                            else if (t.Strike > t.SupportFire) ownStrike++;
                        }
                }
                var orders = c.SupportOrders;
                for (var oi = 0; oi < orders.Count; oi++)
                {
                    var order = orders[oi];
                    if (order.NationId == nation.Id && SupportTypeCatalog.TryGet(order.TypeId, out var t))
                    {
                        if (t.Kind == SupportKind.AirDefence) ownDefence++;
                        else if (t.Strike > t.SupportFire) ownStrike++;
                    }
                }
                var size = 1 + (int)(nation.Gdp / 400f);
                var typeId = enemyStrikes > 0 && ownDefence < Math.Min(3, size) ? "sam_brigade"
                    : ownStrike < Math.Min(4, size) ? (nation.Gdp >= 300f ? "strike_missile_brigade" : "long_range_drone_group")
                    : null;
                if (typeId is null || !SupportTypeCatalog.TryGet(typeId, out var type) || !SupportCore.Enabled(c, type)) continue;
                if (nation.Treasury < SupportCore.CostOf(nation, type) * MathF.Max(1f, d.AiLongRangeReserve)) continue;
                // Into the most populous region with room for it, or the most populous of all when none
                // has: a full region is crowding now, not a refusal, and it is paid for in readiness.
                regions.Clear();
                foreach (var s in c.StatesOf(nation))
                {
                    if (regions.Count == 12 && !BetterProcureRegion(s, regions[11])) continue;
                    var pos = 0;
                    while (pos < regions.Count && !BetterProcureRegion(s, regions[pos])) pos++;
                    if (regions.Count == 12) regions.RemoveAt(11);
                    regions.Insert(pos, s);
                }
                if (regions.Count == 0) continue;
                if (!builtUsed)
                {
                    builtUsed = true;
                    SupportCore.FillBasingUsed(c, usedByState);
                }
                var roomyMask = 0;
                for (var ri = 0; ri < regions.Count; ri++)
                {
                    var st = regions[ri];
                    if (usedByState.GetValueOrDefault(st.MapIndex) + type.Basing <= SupportCore.CapacityOf(c, st))
                        roomyMask |= 1 << ri;
                }
                for (var passIdx = 0; passIdx < 2; passIdx++)
                {
                    var wantRoomy = passIdx == 0;
                    var tried = false;
                    for (var ri = 0; ri < regions.Count; ri++)
                    {
                        if ((((roomyMask >> ri) & 1) != 0) != wantRoomy) continue;
                        var state = regions[ri];
                        if (SupportCore.Validate(c, nation.Id, state.MapIndex, typeId).Length > 0) continue;
                        var ordersBefore = c.SupportOrders.Count;
                        var command = new RaiseSupportCommand { IssuerId = nation.Id, StateMapIndex = state.MapIndex, TypeId = typeId };
                        command.Apply(c);
                        if (command.Refusal.Length == 0) Procured++;
                        if (c.SupportOrders.Count > ordersBefore)
                            usedByState[state.MapIndex] = usedByState.GetValueOrDefault(state.MapIndex) + type.Basing;
                        tried = true;
                        break;
                    }
                    if (tried) break;
                }
                if ((ni & 15) == 15 && ni + 1 < nations.Count && Campaign.StageOverdue()) yield return 0;
            }
        }
        finally
        {
            if (builtAtWar) atWar.Clear();
            if (builtUsed) usedByState.Clear();
            regions.Clear();
            side.Reset();
        }
    }

    private static bool BetterProcureRegion(ScenarioState candidate, ScenarioState incumbent) =>
        candidate.Population > incumbent.Population ||
        (candidate.Population == incumbent.Population && candidate.MapIndex < incumbent.MapIndex);

    // ------------------------------------------------------------------ the fingerprint

    /// <summary>The rules a peer must agree on, in a fixed order; compared against the shipped defaults.</summary>
    internal static float[] Rules(Defines d) => new[]
    {
        d.StrikeIntervalDays, d.StrikeBlindPrecision, d.StrikeInterceptionHalf, d.StrikeSaturation,
        d.StrikeDryInterceptors, d.StrikeFighterInterception, d.StrikeInterceptorCost, d.StrikeFrontOrganisation,
        d.StrikeFrontEquipment, d.StrikeDepotDrain, d.StrikeLogisticsDamage, d.StrikeIndustryDamage,
        d.StrikeEnergyDamage, d.StrikeEnergyStability, d.StrikeEnergyLiving, d.StrikeDamageCeiling,
        d.AiLongRangeProcurementDays, d.AiLongRangeReserve
    };
    // The movement rules (LongRangeMovement.Rules) are a separate array so that a world which
    // changed neither keeps the fingerprint it had before formations moved.

    internal static readonly float[] DefaultRules = Rules(new Defines());
}

/// <summary>
/// What a formation on the strike mission is aimed at: a kind of target (empty for the staff's
/// own choice) and, optionally, a region of an enemy country (-1 for the best one in reach).
/// Validated whole: a kind the formation does not know or a formation not the issuer's is
/// refused, and nothing changes.
/// </summary>
public sealed class SetSupportStrikeTargetCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int FormationId { get; init; }
    public string Target { get; init; } = "";
    public int StateMapIndex { get; init; } = -1;
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var formation = campaign.SupportFormations.Find(candidate =>
            candidate.Id == FormationId && candidate.NationId == IssuerId);
        if (formation is null) { Refusal = "No such formation."; return; }
        if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type) || !SupportCore.Enabled(campaign, type))
        { Refusal = "This mechanic is disabled in this scenario."; return; }
        if (type.Strike <= 0f || !type.Missions.Contains(SupportCore.MissionStrike))
        { Refusal = "This formation cannot strike deep."; return; }
        var target = Target ?? "";
        if (target.Length > 0 && !LongRangeStrikes.Targets.Contains(target)) { Refusal = "No such target."; return; }
        if (StateMapIndex >= 0 && campaign.States.All(state => state.MapIndex != StateMapIndex))
        { Refusal = "No such region."; return; }
        formation.StrikeTarget = target;
        formation.TargetStateMapIndex = Math.Max(-1, StateMapIndex);
        campaign.MarkStrategicChange();
    }
}
