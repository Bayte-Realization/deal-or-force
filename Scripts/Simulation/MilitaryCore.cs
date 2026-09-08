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
    Aggressive
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
    ArmyNotFound,
    NotControlled,
    InvalidTarget,
    TargetNotControlled,
    NoLandRoute,
    RouteInterrupted
}

/// <summary>Organisational headquarters. Its divisions remain independent map entities.</summary>
public sealed class ArmyGroup
{
    public int Id;
    public int NationId;
    public string Name = "";
    public string Commander = "Unassigned staff";
    public float StaffSkill = 0.45f;
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
        if (TargetProvinceId <= 0 || TargetProvinceId > topology.ProvinceCount)
        {
            SetResult(army, campaign, ArmyMoveResult.InvalidTarget);
            return;
        }

        var controllers = campaign.ProvinceController ?? topology.ProvinceToNation;
        var routeStart = AppendWaypoint && army.PlannedRoute.Count > 0 ? army.PlannedRoute[^1]
            : AppendWaypoint && army.Route.Count > 0 ? army.Route[^1] : army.ProvinceId;
        if (routeStart == TargetProvinceId)
        {
            SetResult(army, campaign, ArmyMoveResult.AlreadyAtDestination);
            return;
        }

        var targetController = controllers[TargetProvinceId];
        var enemy = targetController == nation.MapIndex
            ? null
            : campaign.Nations.FirstOrDefault(candidate => candidate.MapIndex == targetController);
        var war = enemy is null ? null : campaign.Wars.FirstOrDefault(candidate => !candidate.Ended &&
            ((candidate.AttackerId == nation.Id && candidate.DefenderId == enemy.Id) ||
             (candidate.DefenderId == nation.Id && candidate.AttackerId == enemy.Id)));
        if (targetController != nation.MapIndex && war is null)
        {
            SetResult(army, campaign, ArmyMoveResult.TargetNotControlled);
            return;
        }

        var continuingAttack = AppendWaypoint && army.AssignedWarId >= 0 && army.PlannedRoute.Count > 0;
        if (continuingAttack && war is null)
        {
            war = campaign.Wars.Find(candidate => candidate.Id == army.AssignedWarId && !candidate.Ended);
            if (war is not null)
                enemy = campaign.NationById(war.AttackerId == nation.Id ? war.DefenderId : war.AttackerId);
        }

        int[] route;
        int[] plannedRoute;
        if (war is null)
        {
            route = topology.FindRoute(routeStart, TargetProvinceId, (ushort)nation.MapIndex, controllers,
                allowUncontrolledStart: routeStart == army.ProvinceId);
            plannedRoute = route;
        }
        else
        {
            plannedRoute = topology.FindOperationalRoute(routeStart, TargetProvinceId,
                (ushort)nation.MapIndex, (ushort)enemy.MapIndex, controllers);
            if (plannedRoute.Length == 0)
            {
                SetResult(army, campaign, ArmyMoveResult.NoLandRoute);
                return;
            }
            // CombatCore owns every hostile step. The ordinary marcher receives only the
            // friendly prefix, while the complete corridor remains visible and guides it.
            route = plannedRoute.TakeWhile(province => controllers[province] == nation.MapIndex).ToArray();
        }
        if (route.Length == 0)
        {
            if (war is null)
            {
                SetResult(army, campaign, ArmyMoveResult.NoLandRoute);
                return;
            }
        }
        if (!AppendWaypoint)
        {
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
        army.DirectOrder = true;
        army.OrderStateMapIndex = topology.ProvinceToState[TargetProvinceId];
        army.ObjectiveStateMapIndex = war is null ? -1 : topology.ProvinceToState[TargetProvinceId];
        army.PlanActive = war is not null;
        if (!AppendWaypoint) army.MoveProgress = 0f;
        army.SupportOnly = SupportAttack && war is not null;
        army.RedeployTarget = -1;
        // Being told to go somewhere does not fill in a trench; marching out of it does,
        // and the march charges for that province by province.
        SetResult(army, campaign, army.InBattle
            ? ArmyMoveResult.QueuedAfterBattle
            : war is null ? ArmyMoveResult.Issued : ArmyMoveResult.AttackIssued);
    }

    private void SetResult(Army army, Campaign campaign, ArmyMoveResult result)
    {
        Result = result;
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
    public string FailureReason { get; private set; } = "";
    public void Apply(Campaign campaign)
    {
        if (!float.IsFinite(Divisions) || Divisions != MathF.Floor(Divisions) || Divisions < 1 || Divisions > 6)
        { FailureReason = "Choose 1–6 whole divisions."; return; }
        var count = (int)Divisions;
        FailureReason = RecruitmentSystem.Validate(campaign, IssuerId, StateMapIndex, TemplateId, count);
        if (FailureReason.Length > 0) return;
        var nation = campaign.NationById(IssuerId);
        var template = UnitTypeCatalog.Get(TemplateId);
        var cost = template.RecruitmentCost * count;
        nation.Treasury -= cost;
        nation.ManpowerReserve -= template.RecruitmentPersonnel * count;
        campaign.Recruitment.Add(new RecruitmentOrder
        {
            Id = campaign.NextRecruitmentId++,
            NationId = IssuerId, StateMapIndex = StateMapIndex, TemplateId = TemplateId,
            Count = count, PersonnelPerDivision = template.RecruitmentPersonnel,
            PaidCost = cost, DurationDays = template.TrainingDays, LastProcessedDay = campaign.ElapsedDays
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
            ReadyDay = army.ReadyDay,
            Latitude = army.Latitude,
            Longitude = army.Longitude
        });
        campaign.MarkStrategicChange();
    }
}

public static class MilitaryOperations
{
    public static void Tick(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        // A nation's daily replacement capacity is shared out by priority - but only among
        // the formations that can actually take replacements. Counting every division in
        // the army, full-strength ones included, handed a third of the capacity to units
        // that returned it unused and quietly threw it away: Russia could field six million
        // reservists and still watch its line fall to 6 % strength, because most of what it
        // could move each day was allotted to divisions that were already full.
        var reinforcementWeights = new Dictionary<int, float>();
        foreach (var formation in campaign.Armies)
        {
            if (!CanTakeReplacements(formation)) continue;
            reinforcementWeights[formation.NationId] = reinforcementWeights.GetValueOrDefault(formation.NationId) +
                                                       ReinforcementDoctrine.Weight(formation.ReinforcementPriority);
        }
        foreach (var army in campaign.Armies)
        {
            army.LastReinforced = 0f;
            ReturnWounded(campaign, army);
            if (!army.InBattle)
            {
                var recovery = MilitaryBootstrap.OrganisationRecoveryPerDay * MilitaryDoctrine.Recovery(army.Stance) *
                    M.Clamp(0.20f + army.SupplyLevel * 0.80f, 0.15f, 1f);
                if (army.OrderKind == ArmyOrder.ReserveFront) recovery *= 1.25f;
                army.Organisation = M.Clamp01(army.Organisation + recovery);
                Reinforce(campaign, army, reinforcementWeights.GetValueOrDefault(army.NationId, 1f));
            }

            if (army.CutOff && army.SupplyLevel < 0.16f)
            {
                army.Organisation = M.Clamp01(army.Organisation - 0.012f);
                army.Strength = M.Clamp01(army.Strength - 0.00045f);
            }

            if (army.InBattle) continue;

            if (topology is null || army.Route.Count == 0)
            {
                if (!army.InBattle)
                {
                    army.Entrenchment = M.Clamp01(army.Entrenchment + campaign.Defines.EntrenchmentPerDay *
                        MilitaryDoctrine.Entrenchment(army.Stance));
                    if (army.AssignedWarId >= 0 &&
                        (army.OrderKind == ArmyOrder.AdvanceFront || army.OrderKind == ArmyOrder.Spearhead))
                        army.Planning = M.Clamp01(army.Planning + campaign.Defines.PlanningGainPerDay *
                            MilitaryDoctrine.Planning(army.Stance));
                }
                continue;
            }

            var template = UnitTypeCatalog.Get(army.TemplateId);
            var nextProvince = army.Route[0];
            var nation = campaign.NationById(army.NationId);
            var controllers = campaign.ProvinceController ?? topology.ProvinceToNation;
            if (nation is null || nextProvince <= 0 || nextProvince > topology.ProvinceCount ||
                controllers[nextProvince] != nation.MapIndex)
            {
                // A friendly staging step can become hostile after the order was given.
                // Keep the player's corridor and hand that step to combat, rather than
                // silently throwing away the whole order or walking through the enemy.
                var blockingNation = nextProvince > 0 && nextProvince <= topology.ProvinceCount
                    ? campaign.Nations.Find(n => n.MapIndex == controllers[nextProvince]) : null;
                var crossingWar = nation is null || blockingNation is null ? null : campaign.Wars.Find(w =>
                    !w.Ended && w.Involves(nation.Id) && w.Involves(blockingNation.Id));
                if (army.DirectOrder && army.OrderProvinceId > 0 && crossingWar is not null)
                {
                    army.Route.Clear();
                    army.MoveProgress = 0f;
                    army.OrderKind = ArmyOrder.Spearhead;
                    army.AssignedWarId = crossingWar.Id;
                    army.PlanActive = true;
                    continue;
                }
                new HaltArmyCommand { IssuerId = army.NationId, ArmyId = army.Id }.Apply(campaign);
                army.Route.Clear();
                army.PlannedRoute.Clear();
                army.OrderProvinceId = -1;
                army.OrderStateMapIndex = -1;
                army.MoveProgress = 0f;
                army.LastMoveResult = ArmyMoveResult.RouteInterrupted;
                army.LastMoveResultDay = campaign.ElapsedDays;
                continue;
            }
            var crossingStrength = topology.RiverCrossingStrength(army.ProvinceId, nextProvince);
            var riverMovement = M.Lerp(1f, campaign.Defines.RiverMovementMultiplier, crossingStrength);
            army.MoveProgress += MilitaryBootstrap.MarchProgressPerDay * template.Speed *
                MilitaryDoctrine.Movement(army.Stance) * M.Clamp(army.SupplyLevel, 0.25f, 1f) * riverMovement *
                topology.TerrainAt(nextProvince).MovementSpeed *
                StrategicRedeployment.DayOf(campaign, army);
            if (army.MoveProgress < 1f) continue;
            army.MoveProgress -= 1f;
            army.ProvinceId = army.Route[0];
            army.Route.RemoveAt(0);
            if (army.PlannedRoute.Count > 0 && army.PlannedRoute[0] == army.ProvinceId)
                army.PlannedRoute.RemoveAt(0);
            army.OrderStepsCompleted++;
            army.StateMapIndex = topology.ProvinceToState[army.ProvinceId];
            (army.Latitude, army.Longitude) = topology.CenterOf(army.ProvinceId);
            // Friendly ground by definition - the route above refuses any province the
            // country does not control - so this is a march within its own lines.
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
            // Untreated, they stop being wounded and start being losses.
            army.Recovering *= 1f - campaign.Defines.WoundedLostWhenUnsuppliedPerDay;
            return;
        }
        var capacity = army.Divisions * MilitaryBootstrap.DivisionSize;
        var returning = MathF.Min(army.Recovering * campaign.Defines.WoundedReturnPerDay,
            MathF.Max(0f, capacity * (1f - army.Strength)));
        if (returning <= 0f) return;
        army.Recovering -= returning;
        army.Strength = M.Clamp01(army.Strength + returning / MathF.Max(1f, capacity));
        army.Personnel = Math.Min(army.MaxPersonnel, army.Personnel + (int)MathF.Round(returning));
    }

    /// <summary>
    /// Whether a formation is in a state to receive replacements at all. The weighting and
    /// the payout have to agree on this, or capacity is allotted where it cannot be spent.
    /// </summary>
    private static bool CanTakeReplacements(Army army) =>
        !army.InBattle && army.Divisions > 0f && army.Strength < 0.9999f && army.SupplyLevel >= 0.22f;

    private static void Reinforce(Campaign campaign, Army army, float totalWeight)
    {
        if (!CanTakeReplacements(army)) return;
        var nation = campaign.NationById(army.NationId);
        if (nation is null || nation.ManpowerReserve < 1f || nation.ReinforcementCapacity < 1f) return;
        var need = army.Divisions * MilitaryBootstrap.DivisionSize * (1f - army.Strength);
        var dailyShare = nation.ReinforcementCapacity * ReinforcementDoctrine.Weight(army.ReinforcementPriority) /
                         MathF.Max(0.1f, totalWeight);
        var people = MathF.Min(need, MathF.Min(nation.ManpowerReserve,
            dailyShare * M.Clamp(army.SupplyLevel, 0.15f, 1f)));
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
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public ArmyOrder Order { get; init; }
    public int ObjectiveStateMapIndex { get; init; } = -1;
    public int PreferredProvinceId { get; init; } = -1;

    public void Apply(Campaign campaign)
    {
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId) return;
        army.OrderKind = Order;
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
        var war = campaign.Wars.Find(candidate => !candidate.Ended && candidate.Involves(IssuerId));
        var nation = campaign.NationById(IssuerId);
        var peacefulLine = BattlePlanning.LineFor(campaign, army);
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
        var target = PreferredProvinceId > 0 && orderedFront.Contains(PreferredProvinceId)
            ? PreferredProvinceId
            : drawn is not null
                ? BattlePlanning.PostFor(drawn, army, orderedFront)
                : orderedFront.Count == 0 ? -1 : orderedFront[Math.Abs(army.Id) % orderedFront.Count];
        var enemyId = army.NationId == war.AttackerId ? war.DefenderId : war.AttackerId;
        var enemy = campaign.NationById(enemyId);
        var targetIsHostile = target > 0 && enemy is not null && controllers[target] == enemy.MapIndex;
        var plannedRoute = target < 0
            ? Array.Empty<int>()
            : targetIsHostile
                ? campaign.ProvinceMap.FindOperationalRoute(army.ProvinceId, target,
                    (ushort)nation.MapIndex, (ushort)enemy.MapIndex, controllers)
                : campaign.ProvinceMap.FindRoute(army.ProvinceId, target, (ushort)nation.MapIndex, controllers);
        if (plannedRoute.Length == 0 && army.ProvinceId != target && !targetIsHostile)
            plannedRoute = campaign.ProvinceMap.FindRouteToAny(army.ProvinceId, front,
                (ushort)nation.MapIndex, controllers);
        var route = targetIsHostile
            ? plannedRoute.TakeWhile(province => controllers[province] == nation.MapIndex).ToArray()
            : plannedRoute;
        army.AssignedWarId = war.Id;
        army.Route.AddRange(route);
        army.PlannedRoute.AddRange(plannedRoute);
        army.OrderRouteSteps = plannedRoute.Length;
        army.OrderStepsCompleted = 0;
        if (target > 0)
        {
            army.OrderProvinceId = target;
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
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId) return;
        if (army.OrderKind != ArmyOrder.AdvanceFront && army.OrderKind != ArmyOrder.Spearhead) return;
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
    public static float Movement(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 0.86f,
        ArmyStance.Aggressive => 1.14f,
        _ => 1f
    };

    public static float Recovery(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 1.16f,
        ArmyStance.Aggressive => 0.84f,
        _ => 1f
    };

    public static float Entrenchment(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 1.24f,
        ArmyStance.Aggressive => 0.72f,
        _ => 1f
    };

    public static float Planning(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 0.80f,
        ArmyStance.Aggressive => 1.10f,
        _ => 1f
    };

    public static float Attack(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 0.84f,
        ArmyStance.Aggressive => 1.16f,
        _ => 1f
    };

    public static float Defense(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 1.12f,
        ArmyStance.Aggressive => 0.94f,
        _ => 1f
    };

    public static float CasualtyExposure(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => 0.84f,
        ArmyStance.Aggressive => 1.22f,
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
            files = System.IO.Directory
                .EnumerateFiles(directory, "*.json", System.IO.SearchOption.AllDirectories)
                .OrderBy(path => path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .ToArray();
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
                    System.IO.File.ReadAllText(file), GameDataLoader.ForgivingJson);
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
