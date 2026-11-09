using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public enum ArmyOrder
{
    Manual,
    DefendFront,
    AdvanceFront,
    ReserveFront,
    Spearhead
}

public enum ArmyStance
{
    Cautious,
    Balanced,
    Aggressive,
    // Appended, never inserted: a stance is saved as its number.
    /// <summary>
    /// Hold at any price. The formation does not give ground, entrenches faster and hits harder
    /// in defence, and pays for it in blood: the same ground is bought with more of its people.
    /// </summary>
    LastStand,
    /// <summary>
    /// Attack now rather than when ready: the heaviest attack multiplier and the fastest
    /// planning of any posture, with the worst defence and the heaviest casualties.
    /// </summary>
    ForceAttack
}

public enum ReinforcementPriority
{
    Low,
    Normal,
    High
}

/// <summary>Deterministic outcome of a manual map movement order.</summary>
public enum ArmyMoveResult
{
    None,
    Issued,
    AttackIssued,
    QueuedAfterBattle,
    AlreadyAtDestination,
    /// <summary>Told to march where it already stands, which is how a player cancels.</summary>
    OrderCancelled,
    ArmyNotFound,
    NotControlled,
    InvalidTarget,
    TargetNotControlled,
    NoLandRoute,
    RouteInterrupted,
    WithdrawnAndMoving,
    QueueFull
}

/// <summary>Organisational headquarters. Its divisions remain independent map entities.</summary>
public sealed class ArmyGroup
{
    public int Id;
    public int NationId;
    public string Name = "";
    public string Commander = "Unassigned staff";
    public float StaffSkill = 0.45f;
    /// <summary>The commander's temperament (common/generals), their record, when they took the group, and how many before them fell (Generals).</summary>
    public string CommanderTrait = "";
    public int Victories;
    public int Defeats;
    public int CommanderSinceDay = -1;
    public int CommandersLost;
    /// <summary>How many of its commanders the government has relieved (Generals.Relieve).</summary>
    public int CommandersRelieved;
}

/// <summary>
/// Engine-neutral operational formation ported from the s&box Army model.
/// Rendering and input are Godot concerns; these values are save/replay state.
/// </summary>
// Partial so that manoeuvres added later can carry their own state without this file
// growing every time: see ArmyManoeuvres.cs.
public sealed partial class Army
{
    public int Id;
    public int NationId;
    public int StateMapIndex = -1;
    public int ProvinceId = -1;
    public int OrderStateMapIndex = -1;
    public int OrderProvinceId = -1;
    /// <summary>A player/issuer specified an exact route; the front planner must not replace it.</summary>
    public bool DirectOrder;
    public int AssignedWarId = -1;
    public int ObjectiveStateMapIndex = -1;
    public ArmyOrder OrderKind;
    public ArmyStance Stance = ArmyStance.Balanced;
    public ReinforcementPriority ReinforcementPriority = ReinforcementPriority.Normal;
    public int ArmyGroupId = -1;
    public int Personnel = 15_000;
    public int MaxPersonnel = 15_000;
    public float Experience = 0.20f;
    public float Equipment = 0.88f;
    public float Cohesion = 0.78f;
    public float Recon = 0.25f;
    public float Engineers = 0.25f;
    public float Artillery = 0.25f;
    /// <summary>Whether an offensive battle plan is currently executing.</summary>
    public bool PlanActive = true;
    public float Planning;
    public float MoveProgress;
    /// <summary>Length and completed part of the current order, used by the map arrow.</summary>
    public int OrderRouteSteps;
    public int OrderStepsCompleted;

    /// <summary>
    /// The day this formation was last given somewhere to be.
    ///
    /// The front is rebuilt every time a province changes hands, and the planner re-tasks
    /// anything whose target has fallen off it - which, on a front that is moving, is most
    /// formations most days. Without a day to compare against there is no way to tell a
    /// division that needs a new objective from one that simply has not arrived at its old
    /// one yet, and the second was being re-tasked daily: three years of marching without
    /// ever getting anywhere. See FrontPlanner.RefreshAssignments.
    /// </summary>
    public int OrderIssuedDay = -9999;
    public float Divisions;
    public float Organisation = 1f;
    public float Strength = 1f;

    /// <summary>
    /// Wounded who are expected back. Casualties in this model were all permanent, so a
    /// formation could only ever get thinner and an attacker - who pays more than twice
    /// what it inflicts - ground itself to nothing in about a year whatever it achieved.
    /// Most of the people a division loses in a day are hurt rather than gone, and they
    /// come back to their own unit rather than through the recruit depot.
    /// </summary>
    public float Recovering;
    public string Name = "";
    public string TemplateId = "line_infantry";
    public float Entrenchment;
    public float SupplyLevel = 1f;
    public int SupplyDistance;
    public int SupplySourceStateMapIndex = -1;
    public float SupplyCongestion = 1f;
    public bool CutOff;
    public int DaysCutOff;
    /// <summary>
    /// Set only when the staff fills a hole from a free reserve, sends that reserve to reopen
    /// a pocket's road, or closes a threatened hole from an advance.
    /// None on a fresh formation. A later order command clears it.
    /// </summary>
    public StaffNote StaffNote;
    public int BattleId = -1;
    public int ReadyDay;

    /// <summary>The drawn line this formation was put on, or -1 for the war's own front.</summary>
    public int FrontLineId = -1;

    /// <summary>
    /// The fleet carrying this formation, or -1 on dry land. An embarked formation holds
    /// province zero - which every land system already reads as "nowhere" - so nothing on
    /// the land side has to learn that ships exist.
    /// </summary>
    public int EmbarkedFleetId = -1;
    public float LastReinforced;
    public ArmyMoveResult LastMoveResult;
    public int LastMoveResultDay = -1;
    /// <summary>
    /// Complete visual and tactical path of the current order. For an attack this includes
    /// hostile provinces; Route contains only the friendly leg that can be marched normally.
    /// </summary>
    public readonly List<int> PlannedRoute = new();
    public readonly List<int> Route = new();
    public float Latitude;
    public float Longitude;

    public bool InBattle => BattleId >= 0;
    public bool IsMoving => Route.Count > 0;
    public float Power => Divisions * Strength * Organisation * SupplyLevel *
                          (0.62f + Equipment * 0.38f) * (0.78f + Experience * 0.32f) *
                          (0.82f + Cohesion * 0.25f);

    /// <summary>
    /// What a formation is worth for denying ground rather than for winning a fight.
    ///
    /// Power falls to nothing as a division is worn down, which is right for combat and
    /// wrong for holding a line: a battered division still occupies the province it is
    /// standing in and still covers the ground beside it. Scoring cover by Power alone
    /// meant that once a side's strength collapsed its whole front became walkable, so a
    /// bad fortnight turned into a rout and the war was decided by whichever side happened
    /// to be on the down-swing - which moved with the map's province numbering rather than
    /// with anything either side did.
    /// </summary>
    public float Presence => Divisions * (0.62f + Equipment * 0.38f) *
                             (0.78f + Experience * 0.32f) * (0.82f + Cohesion * 0.25f);
}

public static class DivisionRules
{
    public static float Attack(Army division) =>
        (0.72f + division.Equipment * 0.38f) * (0.80f + division.Experience * 0.34f) *
        (0.88f + division.Artillery * 0.22f) * (0.92f + division.Recon * 0.14f);

    public static float Defense(Army division) =>
        (0.74f + division.Equipment * 0.34f) * (0.82f + division.Experience * 0.32f) *
        (0.88f + division.Engineers * 0.24f) * (0.90f + division.Cohesion * 0.18f);

    public static string PersonnelText(Army division) => division.Personnel >= 1000
        ? $"{division.Personnel / 1000f:0.#}k" : division.Personnel.ToString("N0");
}

public sealed class AssignArmyGroupCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public int ArmyGroupId { get; init; } = -1;

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var division = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (division is null || division.NationId != IssuerId) return;
        if (ArmyGroupId >= 0 && campaign.ArmyGroups.All(group =>
                group.Id != ArmyGroupId || group.NationId != IssuerId)) return;
        division.ArmyGroupId = ArmyGroupId;
    }
}

public sealed class CreateArmyGroupCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string Name { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        if (campaign.NationById(IssuerId) is null) return;
        var id = campaign.ArmyGroups.Count == 0 ? 1 : campaign.ArmyGroups.Max(group => group.Id) + 1;
        var number = campaign.ArmyGroups.Count(group => group.NationId == IssuerId) + 1;
        campaign.ArmyGroups.Add(new ArmyGroup
        {
            Id = id,
            NationId = IssuerId,
            Name = string.IsNullOrWhiteSpace(Name) ? $"Army Group {number}" : Name.Trim(),
            StaffSkill = 0.38f + (id * 17 % 24) / 100f
        });
        campaign.MarkStrategicChange();
    }
}

public sealed class MoveArmyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public int TargetProvinceId { get; init; }
    public bool AppendWaypoint { get; init; }
    public bool SupportAttack { get; init; }
    public ArmyMoveResult Result { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        var nation = campaign.NationById(IssuerId);
        var topology = campaign.ProvinceMap;
        Result = ArmyMoveResult.None;
        if (army is null)
        {
            Result = ArmyMoveResult.ArmyNotFound;
            return;
        }
        if (nation is null || topology is null || army.NationId != IssuerId)
        {
            // Rejected foreign commands must not even change the owner's feedback.
            Result = ArmyMoveResult.NotControlled;
            return;
        }
        var controllers = campaign.ProvinceController ?? topology.ProvinceToNation;
        if (TargetProvinceId <= 0 || TargetProvinceId > topology.ProvinceCount || TargetProvinceId >= controllers.Length)
        {
            SetResult(army, campaign, ArmyMoveResult.InvalidTarget);
            return;
        }
        if (MilitaryAccess.MustReturn(campaign, army))
        {
            SetResult(army, campaign, ArmyMoveResult.TargetNotControlled);
            return;
        }
        var wasInBattle = army.InBattle;
        if (wasInBattle)
        {
            // A map click is not consent to abandon the defence. Keep the current fight
            // intact and record a distinct, saved order to execute after combat/recovery.
            if (!AppendWaypoint && TargetProvinceId == army.ProvinceId)
            {
                var cancelled = army.PostBattleWaypoints.Count > 0;
                BattleMovementOrders.Clear(army);
                SetResult(army, campaign, cancelled ? ArmyMoveResult.OrderCancelled : ArmyMoveResult.AlreadyAtDestination);
                return;
            }
            if (!BattleDestinationAllowed(campaign, nation, army, controllers, out var refusal))
            {
                Result = refusal;
                return;
            }
            if (AppendWaypoint && army.PostBattleWaypoints.Count >= BattleMovementOrders.MaxWaypoints)
            {
                Result = ArmyMoveResult.QueueFull;
                return;
            }
            if (!AppendWaypoint) BattleMovementOrders.Clear(army);
            if (army.PostBattleWaypoints.Count == 0 || army.PostBattleWaypoints[^1] != TargetProvinceId)
                army.PostBattleWaypoints.Add(TargetProvinceId);
            army.PostBattleSupportAttack = SupportAttack;
            SetResult(army, campaign, ArmyMoveResult.QueuedAfterBattle);
            return;
        }
        var routeStart = AppendWaypoint && army.PlannedRoute.Count > 0 ? army.PlannedRoute[^1]
            : AppendWaypoint && army.Route.Count > 0 ? army.Route[^1] : army.ProvinceId;
        if (routeStart == TargetProvinceId)
        {
            // Clicking the province a formation is standing in is how a player takes an order
            // back - it is the gesture the genre has used for twenty years, and it did nothing
            // here but report that the army was already there. A marching formation stops; one
            // that was standing still anyway keeps the old answer, because cancelling nothing
            // is not a cancellation and should not read like one.
            if (!AppendWaypoint && TargetProvinceId == army.ProvinceId &&
                (army.Route.Count > 0 || army.PlannedRoute.Count > 0 || army.OrderProvinceId >= 0))
            {
                new HaltArmyCommand { IssuerId = IssuerId, ArmyId = ArmyId }.Apply(campaign);
                SetResult(army, campaign, ArmyMoveResult.OrderCancelled);
                return;
            }
            SetResult(army, campaign, wasInBattle ? ArmyMoveResult.WithdrawnAndMoving : ArmyMoveResult.AlreadyAtDestination);
            return;
        }

        var targetController = controllers[TargetProvinceId];
        var enemy = targetController == nation.MapIndex
            ? null
            : campaign.Nations.FirstOrDefault(candidate => candidate.MapIndex == targetController);
        var war = enemy is null ? null : campaign.Wars.FirstOrDefault(candidate => !candidate.Ended &&
            candidate.Opposes(nation.Id, enemy.Id));
        if (war is null && !MilitaryAccess.CanEnter(campaign, nation.Id, targetController))
        {
            SetResult(army, campaign, ArmyMoveResult.TargetNotControlled);
            return;
        }

        var continuingAttack = AppendWaypoint && army.AssignedWarId >= 0 && army.PlannedRoute.Count > 0;
        if (continuingAttack && war is null)
        {
            war = campaign.Wars.Find(candidate => candidate.Id == army.AssignedWarId && !candidate.Ended);
            if (war is not null)
                enemy = campaign.NationById(war.EnemyLeaderOf(nation.Id));
        }

        int[] route;
        int[] plannedRoute;
        if (war is null)
        {
            route = MilitaryAccess.Route(campaign, nation.Id, routeStart, TargetProvinceId, controllers);
            plannedRoute = route;

            // Breaking out of a pocket. Whether an order is a march or an attack was decided
            // by the destination alone, so a formation surrounded on enemy ground and sent
            // home was routed as a march - through friendly provinces only, of which a pocket
            // has none - and was told there was "no legal land corridor" while the corridor it
            // needed ran straight through the country it was already at war with. From inside
            // a pocket the way home IS an attack, and that is how it is routed now: through
            // the captor's ground to the nearest of its own, one contested province at a
            // time, fighting where somebody is standing in the way.
            //
            // Only when the friendly route has failed, and only from where the formation
            // actually stands: a formation that can walk home through its own lines still
            // does, and a queued waypoint is not a breakout.
            if (route.Length == 0 && routeStart == army.ProvinceId && !AppendWaypoint &&
                army.ProvinceId > 0 && army.ProvinceId < controllers.Length)
            {
                var standing = controllers[army.ProvinceId];
                var captor = standing == nation.MapIndex ? null
                    : campaign.Nations.FirstOrDefault(candidate => candidate.MapIndex == standing);
                var captorWar = captor is null ? null : campaign.Wars.FirstOrDefault(candidate =>
                    !candidate.Ended && candidate.Opposes(nation.Id, captor.Id));
                var breakout = captorWar is null ? Array.Empty<int>()
                    : WarCoalition.OperationalRoute(campaign, captorWar, nation.Id,
                        routeStart, TargetProvinceId, controllers);
                if (breakout.Length > 0)
                {
                    war = captorWar;
                    enemy = captor;
                    plannedRoute = breakout;
                    route = Array.Empty<int>();
                }
            }
        }
        else
        {
            plannedRoute = WarCoalition.OperationalRoute(campaign, war, nation.Id,
                routeStart, TargetProvinceId, controllers);
            if (plannedRoute.Length == 0)
            {
                SetResult(army, campaign, ArmyMoveResult.NoLandRoute);
                return;
            }
            // CombatCore owns every hostile step. The ordinary marcher receives only the
            // friendly prefix, while the complete corridor remains visible and guides it.
            route = plannedRoute.TakeWhile(province => province > 0 && province < controllers.Length &&
                WarCoalition.FriendlyController(campaign, war, nation.Id, controllers[province])).ToArray();
        }
        if (route.Length == 0)
        {
            if (war is null)
            {
                SetResult(army, campaign, ArmyMoveResult.NoLandRoute);
                return;
            }
        }
        // MoveProgress is progress into the NEXT province, not along the route as a whole, so
        // it stays valid exactly when that next province is unchanged. Noted before the route
        // is cleared, because after that there is nothing left to compare against.
        var previousNextProvince = army.Route.Count > 0 ? army.Route[0] : -1;
        if (!AppendWaypoint)
        {
            BattleMovementOrders.Clear(army);
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.OrderRouteSteps = 0;
            army.OrderStepsCompleted = 0;
        }
        // A queued leg beyond hostile territory cannot be marched before that territory
        // has been crossed. The operational corridor owns the ordering of those legs.
        if (!continuingAttack) army.Route.AddRange(route);
        army.PlannedRoute.AddRange(plannedRoute);
        army.OrderRouteSteps += plannedRoute.Length;
        army.OrderKind = war is null ? ArmyOrder.Manual : ArmyOrder.Spearhead;
        army.AssignedWarId = war?.Id ?? -1;
        army.OrderProvinceId = TargetProvinceId;
        army.OrderIssuedDay = campaign.ElapsedDays;
        army.DirectOrder = true;
        army.OrderStateMapIndex = topology.ProvinceToState[TargetProvinceId];
        army.ObjectiveStateMapIndex = war is null ? -1 : topology.ProvinceToState[TargetProvinceId];
        army.PlanActive = war is not null;
        // Extending the destination further along the same road - or re-issuing the same march,
        // which a player does constantly to check the order took - used to throw away up to a
        // full province of walking. It only has to be thrown away when the formation is being
        // sent somewhere that starts with a different first step.
        if (!AppendWaypoint && (army.Route.Count == 0 || army.Route[0] != previousNextProvince))
            army.MoveProgress = 0f;
        army.SupportOnly = SupportAttack && war is not null;
        army.RedeployTarget = -1;
        // Being told to go somewhere does not fill in a trench; marching out of it does,
        // and the march charges for that province by province.
        SetResult(army, campaign, army.InBattle
            ? ArmyMoveResult.QueuedAfterBattle
            : wasInBattle ? ArmyMoveResult.WithdrawnAndMoving
            : war is null ? ArmyMoveResult.Issued : ArmyMoveResult.AttackIssued);
    }

    private bool BattleDestinationAllowed(Campaign campaign, Nation nation, Army army,
        ushort[] controllers, out ArmyMoveResult refusal)
    {
        refusal = ArmyMoveResult.NoLandRoute;
        var controller = controllers[TargetProvinceId];
        var targetNation = controller == nation.MapIndex ? null
            : campaign.Nations.FirstOrDefault(candidate => candidate.MapIndex == controller);
        var targetWar = targetNation is null ? null : campaign.Wars.FirstOrDefault(candidate =>
            !candidate.Ended && candidate.Opposes(nation.Id, targetNation.Id));
        if (targetWar is null && !MilitaryAccess.CanEnter(campaign, nation.Id, controller))
        {
            refusal = ArmyMoveResult.TargetNotControlled;
            return false;
        }

        var from = AppendWaypoint && army.PostBattleWaypoints.Count > 0
            ? army.PostBattleWaypoints[^1] : army.ProvinceId;
        if (from == TargetProvinceId) return true;
        if (targetWar is not null)
            return WarCoalition.OperationalRoute(campaign, targetWar, nation.Id,
                from, TargetProvinceId, controllers).Length > 0;
        if (MilitaryAccess.Route(campaign, nation.Id, from, TargetProvinceId, controllers).Length > 0) return true;

        // A route home from hostile ground may be a legal breakout, not an ordinary march.
        // Match the execution path below without rewriting the army or promoting a neutral
        // country's province into a corridor merely because a different war exists.
        var captor = from > 0 && from < controllers.Length && controllers[from] != nation.MapIndex
            ? campaign.Nations.FirstOrDefault(candidate => candidate.MapIndex == controllers[from]) : null;
        var captorWar = captor is null ? null : campaign.Wars.FirstOrDefault(candidate =>
            !candidate.Ended && candidate.Opposes(nation.Id, captor.Id));
        return captorWar is not null && WarCoalition.OperationalRoute(campaign, captorWar, nation.Id,
            from, TargetProvinceId, controllers).Length > 0;
    }

    private void SetResult(Army army, Campaign campaign, ArmyMoveResult result)
    {
        Result = result;
        // A rejected order while fighting has no authoritative army side effects, including
        // its previous feedback. The command result still explains the refusal to its caller.
        if (army.InBattle && result is ArmyMoveResult.InvalidTarget or
                ArmyMoveResult.TargetNotControlled or ArmyMoveResult.NoLandRoute) return;
        army.LastMoveResult = result;
        army.LastMoveResultDay = campaign.ElapsedDays;
    }
}

/// <summary>Raises a moddable field formation in an owned administrative region.</summary>
public sealed class RaiseArmyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; }
    public float Divisions { get; init; } = 1f;
    public string TemplateId { get; init; } = "line_infantry";
    public MobilisationMode Mode { get; init; } = MobilisationMode.Standard;

    /// <summary>
    /// Emergency mobilisation for a country whose treasury cannot pay today. The same
    /// manpower, training queue and placement rules still apply; only the cash check is
    /// waived, and the deficit remains on the national balance sheet.
    /// </summary>
    public bool AllowDeficit { get; init; }
    public string FailureReason { get; private set; } = "";
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var state = campaign.States.Find(s => s.MapIndex == StateMapIndex);
        var maxBatch = RecruitmentSystem.MaxDivisionsPerBatch(campaign, nation, state, Mode);
        if (!float.IsFinite(Divisions) || Divisions != MathF.Floor(Divisions) || Divisions < 1 || Divisions > maxBatch)
        {
            FailureReason = Loc.Format("Choose 1–{0} whole divisions.", maxBatch);
            return;
        }
        var count = (int)Divisions;
        FailureReason = RecruitmentSystem.Validate(campaign, IssuerId, StateMapIndex, TemplateId, count,
            AllowDeficit, Mode);
        if (FailureReason.Length > 0) return;
        var template = UnitTypeCatalog.Get(TemplateId);
        var cost = RecruitmentSystem.TotalBatchCost(campaign, nation, template, count, Mode, out _);
        nation.Treasury -= cost;
        nation.ManpowerReserve -= template.RecruitmentPersonnel * count;

        // Quoting is read-only. Successful admission is where every posture initializes
        // and consumes its physical depot bill, including the first standard batch.
        var bill = StrategicStockpiles.DivisionMaterialBill(template, count, Mode);
        StrategicStockpiles.ConsumeBill(campaign, nation, bill);
        if (Mode == MobilisationMode.RapidCallUp)
        {
            nation.Stability = M.Clamp(nation.Stability - campaign.Defines.RapidCallUpStabilityPerDivision * count, 0.05f, 1f);
            if (state is not null) state.Radicalism = M.Clamp01(state.Radicalism + 0.002f * count);
        }
        else if (Mode == MobilisationMode.TotalMobilisation)
        {
            nation.Stability = M.Clamp(nation.Stability - campaign.Defines.TotalMobilisationStabilityPerDivision * count, 0.05f, 1f);
            if (state is not null)
            {
                state.Radicalism = M.Clamp01(state.Radicalism + 0.005f * count);
                state.StandardOfLiving = M.Clamp01(state.StandardOfLiving - 0.002f * count);
            }
        }

        campaign.Recruitment.Add(new RecruitmentOrder
        {
            Id = campaign.NextRecruitmentId++,
            NationId = IssuerId, StateMapIndex = StateMapIndex, TemplateId = TemplateId,
            Count = count, PersonnelPerDivision = template.RecruitmentPersonnel,
            PaidCost = cost, DurationDays = RecruitmentSystem.TrainingDaysOf(nation, template, Mode),
            LastProcessedDay = campaign.ElapsedDays, Mode = Mode
        });
        campaign.MarkStrategicChange();
    }
}

/// <summary>Splits one controllable field army into two operational formations.</summary>
public sealed class SplitArmyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId || army.InBattle || army.Divisions < 2f) return;
        var detached = MathF.Floor(army.Divisions * 2f) / 4f;
        if (detached < 1f || army.Divisions - detached < 1f) return;
        army.Divisions -= detached;
        army.OrderKind = ArmyOrder.Manual;
        army.Route.Clear();
        army.PlannedRoute.Clear();
        army.AssignedWarId = -1;
        army.OrderProvinceId = -1;
        army.OrderStateMapIndex = -1;
        army.MoveProgress = 0f;
        army.OrderRouteSteps = 0;
        army.OrderStepsCompleted = 0;

        var id = campaign.Armies.Max(candidate => candidate.Id) + 1;
        campaign.Armies.Add(new Army
        {
            Id = id,
            NationId = army.NationId,
            StateMapIndex = army.StateMapIndex,
            ProvinceId = army.ProvinceId,
            Divisions = detached,
            Name = $"{army.Name} Detachment",
            TemplateId = army.TemplateId,
            ReinforcementPriority = army.ReinforcementPriority,
            Organisation = army.Organisation,
            Strength = army.Strength,
            SupplyLevel = army.SupplyLevel,
            SupplyDistance = army.SupplyDistance,
            SupplySourceStateMapIndex = army.SupplySourceStateMapIndex,
            SupplyCongestion = army.SupplyCongestion,
            CutOff = army.CutOff,
            DaysCutOff = army.DaysCutOff,
            // A detachment is still in the same pocket, on the same stores.
            PocketStores = army.PocketStores,
            PocketSinceDay = army.PocketSinceDay,
            HoldoutSinceDay = army.HoldoutSinceDay,
            ReliefDistance = army.ReliefDistance,
            ReadyDay = army.ReadyDay,
            Latitude = army.Latitude,
            Longitude = army.Longitude
        });
        campaign.MarkStrategicChange();
    }
}

public static class MilitaryOperations
{
    /// <summary>
    /// How much of a country's replacement capacity each of its formations has a claim on,
    /// rebuilt every step. Held here so the step does not allocate it; see the note at its
    /// one use.
    /// </summary>
    private static readonly Dictionary<int, float> ReinforcementWeights = new();

    /// <summary>
    /// Bench hook for the isolation harness: when set, fires once per march increment for
    /// the formation named by <see cref="MarchProbeArmyId"/> with every factor of the
    /// step's speed, so a two-campaign divergence names the multiplier that parted rather
    /// than only the progress it left behind. Null in the game; allocation only while armed.
    /// </summary>
    internal static Action<Campaign, Army, string> MarchProbe;
    internal static int MarchProbeArmyId = -1;

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same step's military work, yielded between batches of formations.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(MilitaryOperations))) yield break;
        OffensivePlanning.Tick(campaign);
        var topology = campaign.ProvinceMap;
        // A nation's daily replacement capacity is shared out by priority - but only among
        // the formations that can actually take replacements. Counting every division in
        // the army, full-strength ones included, handed a third of the capacity to units
        // that returned it unused and quietly threw it away: Russia could field six million
        // reservists and still watch its line fall to 6 % strength, because most of what it
        // could move each day was allotted to divisions that were already full.
        // Reused rather than rebuilt. At a daily step this was one dictionary of two hundred
        // and sixty entries a day and beneath noticing; at the shipped hourly one it is
        // twenty-four, and the pass that builds it is already the largest single source of
        // garbage in the simulation.
        var reinforcementWeights = ReinforcementWeights;
        reinforcementWeights.Clear();
        foreach (var formation in campaign.Armies)
        {
            if (!CanTakeReplacements(formation)) continue;
            reinforcementWeights[formation.NationId] = reinforcementWeights.GetValueOrDefault(formation.NationId) +
                                                       ReinforcementDoctrine.Weight(formation.ReinforcementPriority);
        }
        yield return 0;
        var marched = 0;
        foreach (var army in campaign.Armies)
        {
            if (++marched % 64 == 0) yield return 0;
            army.LastReinforced = 0f;
            // Interned on neutral ground with no safe home left: no offensive orders or
            // starvation. The repatriation pass will send it back once a home exists.
            if (MilitaryAccess.MustReturn(campaign, army) && !army.InBattle)
            { MilitaryAccess.Provision(army); continue; }
            ReturnWounded(campaign, army);
            if (!army.InBattle)
            {
                var recovery = campaign.Defines.OrganisationRecoveryPerDay * campaign.TickScale *
                    MilitaryDoctrine.Recovery(army.Stance) *
                    M.Clamp(0.20f + army.SupplyLevel * 0.80f, 0.15f, 1f) *
                    NationalModifiers.Factor(campaign.NationById(army.NationId), "organisation_recovery") *
                    DefconSystem.ReadinessRecoveryMultiplier(campaign, army.NationId);
                if (army.OrderKind == ArmyOrder.ReserveFront) recovery *= 1.25f;
                // A closed pocket is living on what it carried. Rest would put the day's
                // organisation straight back, and the dry-out would never arrive.
                if (army.CutOff) recovery = 0f;
                army.Organisation = M.Clamp01(army.Organisation + recovery);
                Reinforce(campaign, army, reinforcementWeights.GetValueOrDefault(army.NationId, 1f));
            }

            if (army.CutOff) LogisticsSystem.HoldCauldron(campaign, army);

            // By the hour. These were written as a step's worth when a step was a day, and the
            // pockets that surrender when they starve were built and measured once the step
            // was an hour - so an hour's worth is what the game has been playing, and scaling
            // by the step keeps that while a longer step no longer starves a pocket slower.
            if (army.CutOff && army.SupplyLevel < 0.16f)
            {
                army.Organisation = M.Clamp01(army.Organisation - 0.012f * campaign.TickHours);
                army.Strength = M.Clamp01(army.Strength - 0.00045f * campaign.TickHours);
            }

            if (army.InBattle) continue;

            // Resolve from the real post-battle position, never from an assumed retreat
            // tile. While regrouping a pending order must not start an old route either.
            if (army.PostBattleWaypoints.Count > 0)
            {
                if (army.ReadyDay > campaign.ElapsedDays) continue;
                BattleMovementOrders.Resume(campaign, army);
            }

            if (topology is null || army.Route.Count == 0)
            {
                if (!army.InBattle)
                {
                    army.Entrenchment = M.Clamp01(army.Entrenchment + campaign.Defines.EntrenchmentPerDay * campaign.TickScale *
                        MilitaryDoctrine.Entrenchment(army.Stance) *
                        NationalModifiers.Factor(campaign.NationById(army.NationId), "entrenchment_speed"));
                    if (army.AssignedWarId >= 0 &&
                        (army.OrderKind == ArmyOrder.AdvanceFront || army.OrderKind == ArmyOrder.Spearhead))
                        army.Planning = M.Clamp01(army.Planning + campaign.Defines.PlanningGainPerDay * campaign.TickScale *
                            MilitaryDoctrine.Planning(army.Stance));
                }
                continue;
            }

            var template = UnitTypeCatalog.Get(army.TemplateId);
            var nextProvince = army.Route[0];
            var nation = campaign.NationById(army.NationId);
            var controllers = campaign.ProvinceController ?? topology.ProvinceToNation;
            if (nation is null || nextProvince <= 0 || nextProvince > topology.ProvinceCount ||
                (controllers[nextProvince] != nation.MapIndex && !WarCoalition.FriendlyController(campaign,
                    WarCoalition.AssignedWar(campaign, army), army.NationId, controllers[nextProvince])))
            {
                StepBlocked(campaign, topology, controllers, army, nation, nextProvince);
                continue;
            }
            var crossingStrength = topology.RiverCrossingStrength(army.ProvinceId, nextProvince);
            var riverMovement = M.Lerp(1f, campaign.Defines.RiverMovementMultiplier, crossingStrength);
            var marchSupply = M.Clamp(army.SupplyLevel, campaign.Defines.MarchSupplyFloor, 1f);
            var cutOffSlow = army.CutOff && army.DaysCutOff > 3 ? (army.DaysCutOff >= 10 ? 0.20f : 0.35f) : 1.0f;
            var terrainSpeed = topology.TerrainAt(nextProvince).MovementSpeed;
            var redeploySpeed = StrategicRedeployment.DayOf(campaign, army);
            var nationSpeed = NationalModifiers.Factor(nation, "army_speed");
            // And on roads: the march is through friendly ground, and a
            // region with a network of them is crossed faster than one without.
            var roadSpeed = MovementPace.Roads(campaign, nextProvince);
            var doctrineSpeed = MilitaryDoctrine.Movement(army.Stance, campaign.Defines);
            army.MoveProgress += campaign.Defines.MarchProgressPerDay * campaign.TickScale * template.Speed *
                doctrineSpeed * marchSupply * riverMovement * cutOffSlow *
                terrainSpeed * redeploySpeed * nationSpeed * roadSpeed;
            if (MarchProbe is not null && army.Id == MarchProbeArmyId)
                MarchProbe(campaign, army,
                    $"spd={template.Speed:R}|doc={doctrineSpeed:R}|" +
                    $"sup={marchSupply:R}|riv={riverMovement:R}|cut={cutOffSlow:R}|ter={terrainSpeed:R}|" +
                    $"red={redeploySpeed:R}|nat={nationSpeed:R}|road={roadSpeed:R}|mv={army.MoveProgress:R}");
            if (army.MoveProgress < 1f) continue;
            army.MoveProgress -= 1f;
            army.ProvinceId = army.Route[0];
            army.Route.RemoveAt(0);
            if (army.PlannedRoute.Count > 0 && army.PlannedRoute[0] == army.ProvinceId)
                army.PlannedRoute.RemoveAt(0);
            army.OrderStepsCompleted++;
            army.StateMapIndex = topology.ProvinceToState[army.ProvinceId];
            (army.Latitude, army.Longitude) = topology.CenterOf(army.ProvinceId);
            // Own or co-belligerent ground: still a march within friendly lines, not a capture.
            army.Entrenchment *= campaign.Defines.EntrenchmentKeptMovingWithinOwnLines;
            army.Planning = M.Clamp01(army.Planning - campaign.Defines.PlanningLossPerProvince);
            if (army.Route.Count == 0)
            {
                // An offensive RMB order keeps its exact hostile objective while
                // the formation waits on the staging province for CombatCore.
                if (army.OrderKind == ArmyOrder.Manual || (army.DirectOrder && army.OrderProvinceId == army.ProvinceId))
                {
                    army.OrderProvinceId = -1;
                    army.OrderStateMapIndex = -1;
                    army.DirectOrder = false;
                    army.OrderKind = ArmyOrder.Manual;
                    army.AssignedWarId = -1;
                    army.PlanActive = false;
                    army.PlannedRoute.Clear();
                }
                army.MoveProgress = 0f;
            }
        }
    }

    /// <summary>
    /// A friendly staging step can become hostile after the order was given. Keep the
    /// player's corridor and hand that step to combat, rather than silently throwing away the
    /// whole order or walking through the enemy.
    /// </summary>
    /// <remarks>
    /// A method of its own for the sake of the two lambdas. Written inline in the march loop,
    /// they captured the loop's own locals, and the compiler allocates a closure for those at
    /// the top of every iteration - one object per division per step, whether or not anybody's
    /// route was blocked. That was 52 kB a step and nearly all the garbage the step made.
    /// </remarks>
    private static void StepBlocked(Campaign campaign, ProvinceTopology topology, ushort[] controllers,
        Army army, Nation nation, int nextProvince)
    {
        var blockingNation = nextProvince > 0 && nextProvince <= topology.ProvinceCount
            ? campaign.Nations.Find(n => n.MapIndex == controllers[nextProvince]) : null;
        var crossingWar = nation is null || blockingNation is null ? null : campaign.Wars.Find(w =>
            !w.Ended && w.Opposes(nation.Id, blockingNation.Id));
        if (army.DirectOrder && army.OrderProvinceId > 0 && crossingWar is not null)
        {
            army.Route.Clear();
            army.MoveProgress = 0f;
            army.OrderKind = ArmyOrder.Spearhead;
            army.AssignedWarId = crossingWar.Id;
            army.PlanActive = true;
            return;
        }
        new HaltArmyCommand { IssuerId = army.NationId, ArmyId = army.Id }.Apply(campaign);
        army.Route.Clear();
        army.PlannedRoute.Clear();
        army.OrderProvinceId = -1;
        army.OrderStateMapIndex = -1;
        army.MoveProgress = 0f;
        army.LastMoveResult = ArmyMoveResult.RouteInterrupted;
        army.LastMoveResultDay = campaign.ElapsedDays;
    }

    /// <summary>
    /// Walk the day's share of a formation's wounded back into its ranks. This is not the
    /// national replacement pipeline and is not rationed by it: these people are already
    /// in the division's own aid posts, so all it takes is that the division is being
    /// supplied at all. A formation cut off keeps its wounded and gets none of them back.
    /// </summary>
    private static void ReturnWounded(Campaign campaign, Army army)
    {
        if (army.Recovering <= 0f || army.Divisions <= 0f) return;
        if (army.SupplyLevel < campaign.Defines.WoundedReturnMinimumSupply)
        {
            // Untreated, they stop being wounded and start being losses - and on the war's
            // books they stop being wounded and start being dead.
            var died = army.Recovering * campaign.Defines.WoundedLostWhenUnsuppliedPerDay * campaign.TickScale;
            army.Recovering -= died;
            WarLosses.DiedOfWounds(WarLosses.WarOf(campaign, army), army.NationId, died);
            return;
        }
        var capacity = army.Divisions * MilitaryBootstrap.DivisionSize;
        var returning = MathF.Min(army.Recovering * campaign.Defines.WoundedReturnPerDay * campaign.TickScale,
            MathF.Max(0f, capacity * (1f - army.Strength)));
        if (returning <= 0f) return;
        army.Recovering -= returning;
        army.Strength = M.Clamp01(army.Strength + returning / MathF.Max(1f, capacity));
        army.Personnel = Math.Min(army.MaxPersonnel, army.Personnel + (int)MathF.Round(returning));
    }

    /// <summary>
    /// Whether a formation is in a state to receive replacements at all. The weighting and
    /// the payout have to agree on this, or capacity is allotted where it cannot be spent.
    /// A formation in a pocket takes none: it may be fed from the stores where it stands
    /// (StrandedGarrisons), which keeps its supply up, but no draft reaches it through the ring.
    /// </summary>
    private static bool CanTakeReplacements(Army army) =>
        !army.InBattle && !army.CutOff && army.Divisions > 0f && army.Strength < 0.9999f && army.SupplyLevel >= 0.22f;

    private static void Reinforce(Campaign campaign, Army army, float totalWeight)
    {
        if (!CanTakeReplacements(army)) return;
        var nation = campaign.NationById(army.NationId);
        if (nation is null || nation.ManpowerReserve < 1f || nation.ReinforcementCapacity < 1f) return;
        var need = army.Divisions * MilitaryBootstrap.DivisionSize * (1f - army.Strength);
        var dailyShare = nation.ReinforcementCapacity * ReinforcementDoctrine.Weight(army.ReinforcementPriority) /
                         MathF.Max(0.1f, totalWeight);
        // Men are not equipment. A division refills with people, rifles, shells and boots,
        // and only the people half of that comes out of the manpower reserve: the rest
        // comes out of the arms on the national market. A country that makes none and buys
        // none still sends men - at half the rate of a supplied one, which is a shortage a
        // war industry can answer and a country can survive, where a fifth was not: the
        // front it bought was one that churned its own orders, and the divisions walked
        // about it more than they held it.
        var arms = nation.Market.TryGetValue(Commodity.Arms, out var armsQuote) ? armsQuote.Satisfaction : 1f;
        var equipment = 0.50f + 0.50f * M.Clamp01(arms);
        var people = MathF.Min(need, MathF.Min(nation.ManpowerReserve,
            dailyShare * M.Clamp(army.SupplyLevel, 0.15f, 1f) * equipment));
        if (people <= 0f) return;
        army.Strength = M.Clamp01(army.Strength + people /
            MathF.Max(1f, army.Divisions * MilitaryBootstrap.DivisionSize));
        army.Personnel = Math.Min(army.MaxPersonnel, army.Personnel + (int)MathF.Round(people));
        army.Cohesion = M.Clamp01(army.Cohesion + people / MathF.Max(1f, army.MaxPersonnel) * 0.08f);
        army.LastReinforced = people;
        nation.ManpowerReserve -= people;
        nation.ReinforcedLastWeek += people;
    }
}

/// <summary>Immediate deterministic order change used by UI and, later, AI planning.</summary>
public sealed class SetArmyOrderCommand : ICampaignCommand
{
    /// <summary>
    /// The war this order is for. Left at -1 the order works it out: the war the division
    /// already served if its front is about as near, otherwise the war whose front is nearest.
    /// </summary>
    public int WarId { get; init; } = -1;

    /// <summary>
    /// Which of the issuer's wars an order belongs to.
    ///
    /// It was the first war in the list, whatever the order and wherever the division was. For
    /// a country in one war that is the same thing. For one in two it sent every division told
    /// to hold a line - by the player, or by the AI planning its second war - to the front of
    /// the first; the planner then found it off the second war's posts and ordered it again,
    /// every few days, for the whole war, while the second front was held by nobody.
    /// </summary>
    private static StrategicWar WarFor(Campaign campaign, int issuerId, int named, int previous, int province)
    {
        StrategicWar first = null, kept = null;
        var count = 0;
        foreach (var candidate in campaign.Wars)
        {
            if (candidate.Ended || !candidate.Involves(issuerId)) continue;
            if (candidate.Id == named) return candidate;
            if (candidate.Id == previous) kept = candidate;
            first ??= candidate;
            count++;
        }
        if (count <= 1 || campaign.ProvinceMap is null || province <= 0) return kept ?? first;

        var map = campaign.ProvinceMap;
        var (latitude, longitude) = map.CenterOf(province);
        StrategicWar nearest = null;
        var best = float.MaxValue;
        var keptDistance = float.MaxValue;
        foreach (var candidate in campaign.Wars)
        {
            if (candidate.Ended || !candidate.Involves(issuerId)) continue;
            var distance = FrontDistance(map, candidate.FriendlyFront(issuerId), latitude, longitude);
            if (candidate == kept) keptDistance = distance;
            if (distance < best) { best = distance; nearest = candidate; }
        }
        // Half as far again is still "about as near": a division between two fronts is not
        // handed back and forth as the lines move a province either way.
        return kept is not null && keptDistance <= best * 2.25f ? kept : nearest ?? kept ?? first;
    }

    /// <summary>Squared distance, in degrees at the equator, to the nearest of a sample of a front.</summary>
    internal static float FrontDistance(ProvinceTopology map, IReadOnlyCollection<int> front, float latitude, float longitude)
    {
        if (front.Count == 0) return float.MaxValue;
        var stride = Math.Max(1, front.Count / 48);
        var scale = MathF.Cos(latitude * MathF.PI / 180f);
        var best = float.MaxValue;
        var index = 0;
        foreach (var province in front)
        {
            if (index++ % stride != 0) continue;
            var (lat, lon) = map.CenterOf(province);
            var east = MathF.Abs(lon - longitude);
            if (east > 180f) east = 360f - east;
            east *= scale;
            var north = lat - latitude;
            var distance = east * east + north * north;
            if (distance < best) best = distance;
        }
        return best;
    }

    /// <summary>
    /// Whether a post already holds more than its share of the country's line.
    ///
    /// The bar is deliberately slack - half again over an even spread, and never fewer than
    /// three - because re-tasking a division costs it a march, and a planner that rebalances
    /// on every rounding error is the "my troops will not advance, they just walk about"
    /// failure this file spent a long time getting rid of. This only fires on a genuine
    /// pile.
    /// </summary>
    private static bool Overcrowded(Campaign campaign, Army army, StrategicWar war, int post, int posts)
    {
        if (posts <= 1 || war is null) return false;
        var peers = 0;
        var here = 0;
        foreach (var other in campaign.Armies)
        {
            if (other.NationId != army.NationId || other.Id == army.Id) continue;
            if (other.TemplateId == MilitiaCore.TemplateId) continue;
            if (other.AssignedWarId != war.Id) continue;
            peers++;
            if (other.OrderProvinceId == post) here++;
        }
        return here > M.Max(3f, (peers + 1) / (float)posts * 1.5f);
    }

    /// <summary>
    /// Where on the line this division stands, when the player has not drawn one.
    ///
    /// It used to be `orderedFront[|id| % count]`, which looks like a spread and is not
    /// one. The front list is rebuilt every time a province changes hands, so dividing by
    /// its length sends the same division to an unrelated part of the line every time the
    /// line grows or shrinks by a single province. That is the whole of "my troops will not
    /// advance, they just walk about": measured over three years of the 2024 world, 4,949
    /// divisions were sent somewhere else before reaching where they were last sent, against
    /// 2,471 that arrived, and one division was re-tasked forty-six times.
    ///
    /// A division's place in its own order of battle does not change when a province falls.
    /// Spreading by rank means a front that gains a province moves each division one post
    /// along instead of permuting the whole army, and a division that is already where it
    /// was sent usually stays there.
    /// </summary>
    private static int PostOnTheLine(Campaign campaign, Army army, ArmyOrder order, List<int> line, int warId)
    {
        if (line.Count == 0) return -1;

        // Peers on THIS line, not everywhere the country has somebody standing.
        //
        // The count decides how far apart the posts are, and it used to be every division of
        // the nation with the same duty - across every war it was in. A country fighting on
        // two fronts therefore divided one front's length by both fronts' divisions, so each
        // line's formations landed inside a slice of it a fraction of its length, and the
        // rest of the line was held by nobody.
        //
        // The war is passed in rather than read off the division, and that is the whole of
        // the pile. `Apply` clears `AssignedWarId` at the top, before it has worked out where
        // to send anybody, and restores it at the bottom - so a filter written as
        // `other.AssignedWarId != army.AssignedWarId` compares every division in the army
        // against -1, matches nobody, counts one peer, takes the branch below and sends the
        // division to the middle of the line. Every division. Sixty-nine of Russia's
        // seventy-seven in one province, and the other eight somewhere behind them.
        //
        // The division being posted is its own peer whatever its field currently says.
        var peers = 0;
        var rank = 0;
        foreach (var other in campaign.Armies)
        {
            if (other.NationId != army.NationId || other.OrderKind != order) continue;
            if (other.Id != army.Id && other.AssignedWarId != warId) continue;
            if (other.TemplateId == MilitiaCore.TemplateId) continue;
            peers++;
            if (other.Id < army.Id) rank++;
        }
        if (peers <= 1) return line[line.Count / 2];

        var slot = (int)((rank + 0.5f) * line.Count / peers);
        return line[Math.Clamp(slot, 0, line.Count - 1)];
    }

    /// <summary>
    /// A new post for a division that had one: its post has left the line, or has more on it
    /// than its share. The nearest province of the line to that post that is not already
    /// carrying its share of divisions with the same duty - a step along the line, or forward,
    /// or back.
    ///
    /// These used to go through <see cref="PostOnTheLine"/>, whose slots are ranks in a list
    /// sorted by province number: a place in the order of battle, but anywhere at all on the
    /// ground. A front that moved one province sent the division whose post it took to some
    /// other end of the line, and it was sent somewhere else again before it got there. Measured
    /// over three years of the 2024 world, that was three quarters of every re-task - reserves
    /// and defenders swapping duty were the most of it - and the reason divisions were sent
    /// somewhere new about twice for every time one arrived anywhere.
    ///
    /// A fresh division still takes a rank slot: spreading an army that has no posts yet
    /// across the whole line is what the slots are for.
    /// </summary>
    private static int PostNear(Campaign campaign, Army army, ArmyOrder order, List<int> line, int warId, int heldPost)
    {
        if (line.Count == 0) return -1;
        var map = campaign.ProvinceMap;
        var load = new Dictionary<int, int>();
        var peers = 1;
        foreach (var other in campaign.Armies)
        {
            if (other.Id == army.Id || other.NationId != army.NationId || other.OrderKind != order) continue;
            if (other.AssignedWarId != warId || other.TemplateId == MilitiaCore.TemplateId) continue;
            peers++;
            if (other.OrderProvinceId > 0) load[other.OrderProvinceId] = load.GetValueOrDefault(other.OrderProvinceId) + 1;
        }
        // An even spread's worth of divisions per province; one more than that costs as much as
        // walking one province further (a province is about half a degree across).
        var share = MathF.Max(1f, peers / (float)line.Count);
        const float CrowdCost = 0.6f;
        var from = map.CenterOf(heldPost);
        var best = -1;
        var bestScore = float.MaxValue;
        foreach (var province in line)
        {
            var there = map.CenterOf(province);
            var score = NavalOperations.Distance(from.Latitude, from.Longitude, there.Latitude, there.Longitude);
            var over = load.GetValueOrDefault(province) + 1 - share;
            if (over > 0f) score += over * CrowdCost;
            if (score < bestScore || (score == bestScore && province < best))
            {
                bestScore = score;
                best = province;
            }
        }
        return best;
    }

    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public ArmyOrder Order { get; init; }
    public int ObjectiveStateMapIndex { get; init; } = -1;
    public int PreferredProvinceId { get; init; } = -1;

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId) return;
        // Where this division was already going, before the reset below wipes it.
        //
        // Keeping it is the difference between a line that holds and an army that walks
        // about. Everything here is recomputed from scratch whenever the front is redrawn -
        // which is every time a single province changes hands - so without remembering the
        // post, a division that is perfectly well placed is handed a new one for no reason
        // at all, several times a week, for the whole war.
        var heldPost = army.OrderProvinceId;
        var servedWar = army.AssignedWarId;
        var marchingFront = !army.DirectOrder && army.OrderKind != ArmyOrder.Manual && army.Route.Count > 0;
        var previousStep = marchingFront ? army.Route[0] : -1;
        var previousProgress = army.MoveProgress;

        army.OrderKind = Order;
        army.StaffNote = StaffNote.None;
        army.DirectOrder = false;
        // Front dispatch is the only subsystem allowed to repopulate these fields.
        // Until a war/front is assigned, changing role cancels a manual march cleanly.
        army.AssignedWarId = -1;
        army.OrderStateMapIndex = -1;
        army.OrderProvinceId = -1;
        army.ObjectiveStateMapIndex = Order == ArmyOrder.Spearhead ? ObjectiveStateMapIndex : -1;
        army.MoveProgress = 0f;
        army.OrderRouteSteps = 0;
        army.OrderStepsCompleted = 0;
        army.Route.Clear();
        army.PlannedRoute.Clear();

        if (Order == ArmyOrder.Manual || campaign.ProvinceMap is null) return;
        var war = WarFor(campaign, IssuerId, WarId, servedWar, army.ProvinceId);
        var nation = campaign.NationById(IssuerId);
        var peacefulLine = BattlePlanning.LineFor(campaign, army);
        if (peacefulLine is { Kind: FrontLineKind.Fallback })
        {
            DefensivePlanning.Route(campaign, army, peacefulLine);
            return;
        }
        if (peacefulLine is { Kind: FrontLineKind.Offensive } && peacefulLine.Objectives.Count > 0 &&
            Order is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead)
        {
            OffensivePlanning.Route(campaign, army, peacefulLine);
            return;
        }
        if (war is null && nation is not null && peacefulLine is { Kind: FrontLineKind.Defence })
        {
            var posts = BattlePlanning.Sequence(campaign, peacefulLine);
            var post = posts.Count > 0 ? BattlePlanning.PostFor(peacefulLine, army, posts) : -1;
            if (post > 0)
            {
                var march = campaign.ProvinceMap.FindRoute(army.ProvinceId, post, (ushort)nation.MapIndex,
                    campaign.ProvinceController ?? campaign.ProvinceMap.ProvinceToNation);
                army.Route.AddRange(march);
                army.PlannedRoute.AddRange(march);
                army.OrderRouteSteps = march.Length;
                army.OrderProvinceId = post;
                army.OrderIssuedDay = campaign.ElapsedDays;
                army.OrderStateMapIndex = campaign.ProvinceMap.ProvinceToState[post];
            }
            return;
        }
        if (war is null || nation is null) return;
        // A line the player drew outranks the front the war worked out for itself, and
        // formations spread along its length rather than being scattered over it by a
        // hash of their id - covering the line is the whole of what drawing one means.
        var drawn = BattlePlanning.LineFor(campaign, army);
        var front = drawn?.Provinces ?? (Order == ArmyOrder.ReserveFront
            ? FrontPlanner.ReserveLine(campaign, war, IssuerId)
            : war.FriendlyFront(IssuerId));
        var controllers = campaign.ProvinceController ?? campaign.ProvinceMap.ProvinceToNation;
        var orderedFront = drawn is not null
            ? BattlePlanning.Sequence(campaign, drawn)
            : new List<int>(front);
        if (drawn is null) orderedFront.Sort();
        if (Order == ArmyOrder.Spearhead && drawn is null)
            FocusSpearhead(campaign, war, IssuerId, army, orderedFront);
        // The post it already had, if that post is still on the line and the duty has not
        // changed. A division ordered to do the same thing it was already doing, on a line
        // that still contains where it was going, has no reason to go anywhere else.
        // The post it already had, whether or not its JOB has changed.
        //
        // It used to require the order to be the same, which threw the march away every time
        // a division was moved between spearhead and advance - and that happens constantly,
        // because the roles are shares of a list sorted by a strength that moves daily. But
        // a division standing on a good post on the line is standing on a good post whatever
        // it has just been told to call itself; the two questions are separate, and only one
        // of them is a reason to start walking somewhere else.
        //
        // ...unless that post is already carrying more than its share. Keeping a post is
        // what stops an army walking about, and it is also why a post never gives anybody
        // up: a war that opens on a one-province front hands every division the same post,
        // and when the front grows to five they all keep the one they were given while the
        // other four provinces are held by nobody. Measured, that is how Turkey finished a
        // decade with twenty-nine of forty divisions in one place - every one of them
        // standing exactly where it had been told to stand.
        var keeping = heldPost > 0 && orderedFront.Contains(heldPost) &&
                      !Overcrowded(campaign, army, war, heldPost, orderedFront.Count);

        var target = PreferredProvinceId > 0 && orderedFront.Contains(PreferredProvinceId)
            ? PreferredProvinceId
            : keeping
                ? heldPost
                : drawn is not null
                    ? BattlePlanning.PostFor(drawn, army, orderedFront)
                    : heldPost > 0
                        ? PostNear(campaign, army, Order, orderedFront, war.Id, heldPost)
                        : PostOnTheLine(campaign, army, Order, orderedFront, war.Id);
        // A post on the whole front belongs to the war against whoever it faces, not to the
        // war nearest where the division happened to be standing when it was told: so the
        // division is counted, supported and - when that war alone ends - stood down with the
        // right war. Every other line keeps the war worked out above.
        if (drawn is { AllEnemies: true } && target > 0 &&
            BorderFronts.WarAt(campaign, IssuerId, target) is { } facing) war = facing;
        var targetIsHostile = target > 0 && WarCoalition.HostileController(campaign, war, army.NationId, controllers[target]);
        var plannedRoute = target < 0
            ? Array.Empty<int>()
            : targetIsHostile
                ? WarCoalition.OperationalRoute(campaign, war, army.NationId, army.ProvinceId, target, controllers)
                : campaign.ProvinceMap.FindRoute(army.ProvinceId, target, (ushort)nation.MapIndex, controllers);
        if (plannedRoute.Length == 0 && army.ProvinceId != target && !targetIsHostile)
        {
            plannedRoute = WarCoalition.OperationalRoute(campaign, war, army.NationId, army.ProvinceId, target, controllers);
            if (plannedRoute.Length == 0)
                plannedRoute = campaign.ProvinceMap.FindRouteToAny(army.ProvinceId, front,
                    (ushort)nation.MapIndex, controllers);
        }
        var route = targetIsHostile
            ? plannedRoute.TakeWhile(province => WarCoalition.FriendlyController(campaign, war, army.NationId, controllers[province])).ToArray()
            : plannedRoute;
        army.AssignedWarId = war.Id;
        army.Route.AddRange(route);
        army.PlannedRoute.AddRange(plannedRoute);
        army.OrderRouteSteps = plannedRoute.Length;
        army.OrderStepsCompleted = 0;
        // A staff role is not a physical turn-around. Recompute and validate the
        // route as usual, but don't erase distance already travelled on the same
        // first edge merely because an advance was promoted/demoted. A changed
        // direction, war, direct march or blocked route still starts cleanly.
        if (marchingFront && servedWar == war.Id && route.Length > 0 && route[0] == previousStep)
            army.MoveProgress = previousProgress;
        if (target > 0)
        {
            army.OrderProvinceId = target;
            army.OrderIssuedDay = campaign.ElapsedDays;
            army.OrderStateMapIndex = campaign.ProvinceMap.ProvinceToState[target];
            if (targetIsHostile) army.ObjectiveStateMapIndex = army.OrderStateMapIndex;
        }
    }

    private static void FocusSpearhead(Campaign campaign, StrategicWar war, int nationId, Army army, List<int> front)
    {
        var topology = campaign.ProvinceMap;
        var nation = campaign.NationById(nationId);
        var objective = campaign.States.Find(state => state.MapIndex == army.ObjectiveStateMapIndex);
        if (objective is null || objective.NationMapIndex == nation?.MapIndex)
        {
            army.ObjectiveStateMapIndex = war.ClaimedStateMapIndex;
            objective = campaign.States.Find(state => state.MapIndex == army.ObjectiveStateMapIndex);
        }
        if (topology is null || objective is null || front.Count < 2) return;
        var focused = front.Where(province => topology.Neighbors[province].Any(neighbor =>
            topology.ProvinceToState[neighbor] == objective.MapIndex)).ToList();
        if (focused.Count == 0) return;
        front.Clear();
        front.AddRange(focused.OrderBy(value => value));
    }
}

public sealed class SetArmyStanceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public ArmyStance Stance { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId || !Enum.IsDefined(Stance)) return;
        army.Stance = Stance;
    }
}

public sealed class SetArmyReinforcementPriorityCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public ReinforcementPriority Priority { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId || !Enum.IsDefined(Priority)) return;
        army.ReinforcementPriority = Priority;
    }
}

/// <summary>Separates laying out an offensive plan from executing it.</summary>
public sealed class SetArmyPlanExecutionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public bool Active { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId) return;
        if (army.OrderKind != ArmyOrder.AdvanceFront && army.OrderKind != ArmyOrder.Spearhead) return;
        if (Active && StaffReasons.SupplyHoldsAttack(campaign, army)) return;
        army.PlanActive = Active;
        var line = BattlePlanning.LineFor(campaign, army);
        if (!army.InBattle && line is { Kind: FrontLineKind.Offensive } && line.Objectives.Count > 0)
        {
            if (Active) OffensivePlanning.Route(campaign, army, line);
            else { army.Route.Clear(); army.PlannedRoute.Clear(); army.MoveProgress = 0; }
        }
    }
}

public static class MilitaryDoctrine
{
    // The constants are the fallback a caller without a campaign in its hand still gets, the
    // same arrangement the rates above use. Anything that can reach the defines should take
    // the overload, because that is the copy a mod can change.
    public const float CautiousMovement = 0.86f;
    public const float AggressiveMovement = 1.14f;

    public static float Movement(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => CautiousMovement,
        ArmyStance.Aggressive => AggressiveMovement,
        ArmyStance.LastStand => 0.70f,
        ArmyStance.ForceAttack => 1.05f,
        _ => 1f
    };

    /// <summary>The same multiplier, taken from the defines so a mod can retune it.</summary>
    public static float Movement(ArmyStance stance, Defines defines) => defines is null
        ? Movement(stance)
        : stance switch
        {
            ArmyStance.Cautious => defines.MarchCautiousMultiplier,
            ArmyStance.Aggressive => defines.MarchAggressiveMultiplier,
            ArmyStance.LastStand => defines.MarchCautiousMultiplier,
            ArmyStance.ForceAttack => 1.0f,
            _ => 1f
        };

    public static float Recovery(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 1.16f,
        ArmyStance.Aggressive => 0.84f,
        // A formation ordered to hold where it stands does not fall back to rest: it digs,
        // and it is relieved by somebody else or by nobody.
        ArmyStance.LastStand => 0.55f,
        ArmyStance.ForceAttack => 0.70f,
        _ => 1f
    };

    public static float Entrenchment(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 1.24f,
        ArmyStance.Aggressive => 0.72f,
        ArmyStance.LastStand => 1.45f,
        ArmyStance.ForceAttack => 0.55f,
        _ => 1f
    };

    public static float Planning(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 0.80f,
        ArmyStance.Aggressive => 1.10f,
        // Attack now is the whole point of the order: the plan is what is already drawn.
        ArmyStance.LastStand => 0.70f,
        ArmyStance.ForceAttack => 1.25f,
        _ => 1f
    };

    public static float Attack(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 0.84f,
        ArmyStance.Aggressive => 1.16f,
        ArmyStance.LastStand => 0.92f,
        ArmyStance.ForceAttack => 1.32f,
        _ => 1f
    };

    public static float Defense(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 1.12f,
        ArmyStance.Aggressive => 0.94f,
        ArmyStance.LastStand => 1.38f,
        ArmyStance.ForceAttack => 0.70f,
        _ => 1f
    };

    public static float CasualtyExposure(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 0.84f,
        ArmyStance.Aggressive => 1.22f,
        ArmyStance.LastStand => 1.45f,
        ArmyStance.ForceAttack => 1.30f,
        _ => 1f
    };
}

public static class ReinforcementDoctrine
{
    public static float Weight(ReinforcementPriority priority) => priority switch
    {
        ReinforcementPriority.Low => 0.45f,
        ReinforcementPriority.High => 1.90f,
        _ => 1f
    };

    public static string Name(ReinforcementPriority priority) => priority switch
    {
        ReinforcementPriority.Low => "Low",
        ReinforcementPriority.High => "High",
        _ => "Normal"
    };
}

/// <summary>Exact 2024 personnel calibration and deployment constants from s&box.</summary>
public static class MilitaryBootstrap
{
    public const float DivisionSize = 15_000f;
    public const float DivisionsPerArmy = 12f;
    public const int MaxPerCountry = 14;
    // Kept as the fallback the defines start from, and as what any caller without a
    // campaign in its hand still gets. The live values are on Defines, where a mod can
    // reach them.
    public const float OrganisationRecoveryPerDay = 0.06f;
    public const float StrengthRecoveryPerDay = 0.012f;
    public const float OrganisationBurnPerDay = 0.16f;
    public const float MarchProgressPerDay = 0.22f;
    public const float DefaultTroopShare = 0.0011f;

    private static readonly string[] TroopRows2024 =
    {
        "CHN 2035000 IND 1455000 USA 1328000 PRK 1280000 RUS 1150000",
        "PAK 654000 KOR 500000 IRN 610000 VNM 470000 EGY 440000",
        "MMR 406000 UKR 800000 IDN 400000 BRA 366000 THA 361000",
        "COL 293000 MEX 277000 JPN 247000 TUR 355000 TWN 169000",
        "SAU 257000 FRA 205000 DEU 184000 ITA 165000 GBR 148000",
        "POL 202000 ESP 133000 ISR 170000 IRQ 193000 ETH 162000",
        "DZA 130000 SDN 104000 MAR 195000 SYR 169000 GRC 142000",
        "AFG 0 CAN 68000 AUS 58000 ROU 69000 VEN 123000",
        "MYS 113000 PHL 143000 BGD 163000 LKA 255000 NGA 223000",
        "CUB 49000 PER 81000 CHL 77000 ARG 76000 ZAF 66000",
        "KAZ 39000 UZB 48000 AZE 65000 ARM 45000 BLR 45000",
        "SRB 28000 FIN 24000 SWE 24000 NOR 23000 DNK 16000",
        "NLD 41000 BEL 25000 AUT 22000 CHE 21000 CZE 27000",
        "HUN 22000 BGR 36000 PRT 27000 IRL 8000 NZL 9000",
        "SGP 51000 ARE 63000 QAT 17000 KWT 17000 JOR 100000",
        "LBN 60000 YEM 40000 OMN 42000 LBY 32000 TUN 36000",
        "KEN 24000 TZA 27000 UGA 47000 COD 134000 AGO 107000",
        "ZWE 29000 ZMB 16000 GHA 16000 CIV 24000 SEN 19000",
        "MLI 21000 TCD 34000 NER 11000 CMR 25000 MOZ 11000",
        "MNG 9000 NPL 96000 KHM 124000 LAO 29000 GEO 21000",
        "MDA 6000 LVA 7000 LTU 23000 EST 7000 SVK 15000",
        "SVN 7000 HRV 15000 BIH 9000 MKD 8000 ALB 8000",
        "CYP 12000 MLT 2000 LUX 1000 ISL 0 MNE 2000"
    };

    private static Dictionary<string, float> _personnel;

    public static void Seed(Campaign campaign)
    {
        campaign.Armies.Clear();
        campaign.ArmyGroups.Clear();
        var nextId = 0;
        var nextGroupId = 1;

        foreach (var nation in campaign.Nations)
        {
            var personnel = Personnel(nation.Tag, nation.Population);
            nation.ManpowerReserve = MathF.Max(personnel * campaign.Defines.ManpowerReserveFloorShare,
                nation.Population * campaign.Defines.MobilisationBaseShare - personnel);
            nation.ReinforcementCapacity =
                MathF.Max(80f, personnel * campaign.Defines.ReinforcementCapacityShare);
            if (personnel < 1_000f || string.IsNullOrWhiteSpace(nation.CapitalName)) continue;
            var divisionCount = Math.Max(1, (int)Math.Ceiling(personnel / DivisionSize));
            var remainingPersonnel = (int)MathF.Round(personnel);
            var nationGroups = new List<ArmyGroup>();
            for (var groupIndex = 0; groupIndex < (int)Math.Ceiling(divisionCount / DivisionsPerArmy); groupIndex++)
            {
                var group = new ArmyGroup
                {
                    Id = nextGroupId++,
                    NationId = nation.Id,
                    Name = $"{nation.Tag} Army {groupIndex + 1}",
                    StaffSkill = 0.38f + ((nation.MapIndex * 13 + groupIndex * 17) % 28) / 100f
                };
                campaign.ArmyGroups.Add(group);
                Generals.Appoint(campaign, group);
                nationGroups.Add(group);
            }

            for (var index = 0; index < divisionCount; index++)
            {
                var assignedPersonnel = Math.Min((int)DivisionSize, remainingPersonnel);
                remainingPersonnel -= assignedPersonnel;
                var angle = index * (MathF.PI * 2f / Math.Max(1, divisionCount));
                var ring = divisionCount <= 1 ? 0f : 0.18f + 0.025f * (index % 4);
                var templateId = UnitTypeCatalog.InitialTemplate(nation, index, personnel);
                var variation = (nation.MapIndex * 97 + index * 37) & 255;
                campaign.Armies.Add(new Army
                {
                    Id = nextId++,
                    NationId = nation.Id,
                    Divisions = 1f,
                    Name = $"{index + 1}. {nation.Tag} Division",
                    TemplateId = templateId,
                    ArmyGroupId = nationGroups[Math.Min(nationGroups.Count - 1, index / (int)DivisionsPerArmy)].Id,
                    Personnel = assignedPersonnel,
                    MaxPersonnel = (int)DivisionSize,
                    Strength = assignedPersonnel / DivisionSize,
                    Experience = 0.12f + variation % 31 / 100f,
                    Equipment = 0.72f + variation % 25 / 100f,
                    Cohesion = 0.68f + variation % 23 / 100f,
                    Recon = templateId == "motorized_corps" ? 0.68f : 0.28f + variation % 16 / 100f,
                    Engineers = templateId == "line_infantry" ? 0.52f : 0.34f,
                    Artillery = templateId == "armored_corps" ? 0.70f : 0.38f + variation % 18 / 100f,
                    Latitude = nation.CapitalLatitude + MathF.Sin(angle) * ring,
                    Longitude = nation.CapitalLongitude + MathF.Cos(angle) * ring
                });
            }
        }
    }

    /// <summary>
    /// Drop the cached order of battle so the next reader builds it again.
    ///
    /// The numbers are read once and kept, because they are asked for every day by the
    /// replacement pipeline. A mod that changes how many soldiers a country has must
    /// therefore be able to make the game forget - otherwise its file is read and the
    /// answer comes from a table built before it was loaded.
    /// </summary>
    public static void ResetPersonnel() => _personnel = null;

    /// <summary>
    /// Layer one package's own standing-army numbers over what is loaded. Later packages
    /// win per country, so a mod that doubles one army inherits the other two hundred and
    /// sixty rather than having to restate them.
    /// </summary>
    public static void MergePersonnel(string directory, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory)) return;
        _personnel ??= ParsePersonnel();

        string[] files;
        try
        {
            files = StructuredData.Files(directory, System.IO.SearchOption.AllDirectories);
        }
        catch (Exception exception)
        {
            errors?.Add($"history/units: {exception.Message}");
            return;
        }

        foreach (var file in files)
        {
            var origin = $"history/units/{System.IO.Path.GetFileName(file)}";
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(
                    StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                foreach (var entry in document.RootElement.EnumerateObject())
                    if (entry.Value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                        entry.Value.TryGetSingle(out var number)) _personnel[entry.Name] = number;
            }
            catch (Exception exception) { errors?.Add($"{origin}: {exception.Message}"); }
        }
    }

    public static float Personnel(string tag, long population)
    {
        _personnel ??= ParsePersonnel();
        return _personnel.TryGetValue(tag, out var value) ? value : population * DefaultTroopShare;
    }

    private static Dictionary<string, float> ParsePersonnel()
    {
        var data = GameDataLoader.LoadNumberMap("history", "units", "2024");
        if (data.Count > 0) return data;

        // Compiled fallback keeps headless tests and incomplete mod workspaces bootable.
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in TroopRows2024)
        {
            var parts = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index + 1 < parts.Length; index += 2)
                if (float.TryParse(parts[index + 1], out var people))
                    result[parts[index]] = people;
        }
        return result;
    }
}
