using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation.Controls;

/// <summary>
/// Deterministic command that stops movement orders for every army belonging to the issuer
/// without pulling engaged units out of active combat (AoH #11 emergency brake, Ctrl+Alt+H).
/// </summary>
public sealed class HaltAllPlayerMovementsCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int HaltedCount { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (campaign is null || !MechanicFeatures.Allows(campaign, this)) return;
        var stopped = 0;
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != IssuerId) continue;
            var hadOrder = army.InBattle
                ? army.PostBattleWaypoints.Count > 0
                : army.Route.Count > 0 || army.PlannedRoute.Count > 0 || army.OrderProvinceId >= 0 ||
                  army.PostBattleWaypoints.Count > 0 || army.DirectOrder;
            if (!hadOrder) continue;

            if (army.InBattle)
            {
                BattleMovementOrders.Clear(army);
                stopped++;
                continue;
            }

            BattleMovementOrders.Clear(army);
            if (army.DirectOrder)
            {
                army.OrderKind = ArmyOrder.Manual;
                army.PlanActive = false;
                army.AssignedWarId = -1;
            }
            army.DirectOrder = false;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.MoveProgress = 0f;
            army.OrderProvinceId = -1;
            army.OrderStateMapIndex = -1;
            army.OrderRouteSteps = 0;
            army.OrderStepsCompleted = 0;
            stopped++;
        }
        HaltedCount = stopped;
        if (stopped > 0) campaign.MarkStrategicChange();
    }
}

public readonly record struct SplitMoveResult(
    IReadOnlyList<int> MovingArmyIds,
    IReadOnlyList<int> RemainingArmyIds,
    int MovedDivisions,
    IReadOnlyDictionary<ArmyMoveResult, int> Outcomes,
    string Refusal);

/// <summary>
/// Pure UI-selection and camera-focus state for army control groups (6..0), idle army cycling (.),
/// Alt+RMB split-move ("first where, then how many"), and gathering idle armies (AoH #8, #10, #11).
/// Selection state lives outside CampaignFingerprint.
/// </summary>
public sealed class ControlGroupTracker
{
    public static readonly int[] Slots = { 6, 7, 8, 9, 0 };

    private readonly Dictionary<int, List<int>> _groups = new();
    private int _lastTappedSlot = -1;
    private double _lastTappedMs = -100000.0;

    public void ClearAll()
    {
        _groups.Clear();
        _lastTappedSlot = -1;
        _lastTappedMs = -100000.0;
    }

    public static bool IsValidSlot(int slot) => slot is >= 6 and <= 9 or 0;

    public void Bind(int slot, IEnumerable<int> armyIds)
    {
        if (!IsValidSlot(slot)) return;
        var ids = (armyIds ?? Enumerable.Empty<int>()).Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
        if (ids.Count == 0)
        {
            _groups.Remove(slot);
            return;
        }
        _groups[slot] = ids;
    }

    public IReadOnlyList<Army> Resolve(Campaign campaign, int playerId, int slot)
    {
        if (campaign is null || !IsValidSlot(slot) || !_groups.TryGetValue(slot, out var stored))
            return Array.Empty<Army>();

        var liveById = new Dictionary<int, Army>();
        foreach (var army in campaign.Armies)
            if (army.NationId == playerId)
                liveById[army.Id] = army;

        var result = new List<Army>();
        var keptIds = new List<int>();
        foreach (var id in stored)
        {
            if (liveById.TryGetValue(id, out var army))
            {
                result.Add(army);
                keptIds.Add(id);
            }
        }
        if (keptIds.Count == 0) _groups.Remove(slot);
        else if (keptIds.Count != stored.Count) _groups[slot] = keptIds;
        return result;
    }

    public string BadgeForArmy(int armyId)
    {
        foreach (var slot in Slots)
            if (_groups.TryGetValue(slot, out var ids) && ids.Contains(armyId))
                return slot.ToString();
        return "";
    }

    /// <summary>
    /// Returns true if activating <paramref name="slot"/> should glide the camera to the group's centroid
    /// (double-tap within 450 ms OR group is already the exact active selection).
    /// </summary>
    public bool RecordActivateAndShouldFocus(int slot, double nowMs, IReadOnlyList<Army> currentSelection,
        IReadOnlyList<Army> groupArmies, double doubleTapWindowMs = 450.0)
    {
        if (groupArmies is not { Count: > 0 })
        {
            _lastTappedSlot = slot;
            _lastTappedMs = nowMs;
            return false;
        }
        var doubleTap = _lastTappedSlot == slot && nowMs - _lastTappedMs <= doubleTapWindowMs;
        var alreadySelected = currentSelection is { Count: > 0 } &&
            currentSelection.Count == groupArmies.Count &&
            groupArmies.All(g => currentSelection.Any(s => s.Id == g.Id));
        _lastTappedSlot = slot;
        _lastTappedMs = nowMs;
        return doubleTap || alreadySelected;
    }
}

public static class ArmySelectionRules
{
    public static bool ComputeCentroid(IReadOnlyList<Army> armies, ProvinceTopology map,
        out float latitude, out float longitude)
    {
        latitude = 0f;
        longitude = 0f;
        if (armies is null || armies.Count == 0) return false;

        double sumLat = 0.0, sumSin = 0.0, sumCos = 0.0;
        var count = 0;
        foreach (var army in armies)
        {
            if (army is null) continue;
            float lat = army.Latitude, lon = army.Longitude;
            if (map is not null && army.ProvinceId > 0 && army.ProvinceId <= map.ProvinceCount)
            {
                var c = map.CenterOf(army.ProvinceId);
                lat = c.Latitude;
                lon = c.Longitude;
            }
            var lonRad = lon * Math.PI / 180.0;
            sumLat += lat;
            sumSin += Math.Sin(lonRad);
            sumCos += Math.Cos(lonRad);
            count++;
        }
        if (count == 0) return false;
        latitude = (float)(sumLat / count);
        longitude = (float)(Math.Atan2(sumSin, sumCos) * 180.0 / Math.PI);
        return true;
    }

    public static bool IsIdleArmy(Campaign campaign, Army army, int playerId)
    {
        if (campaign is null || army is null || army.NationId != playerId) return false;
        if (army.InBattle || army.IsMoving || army.EmbarkedFleetId >= 0) return false;
        if (army.Route.Count > 0 || army.PlannedRoute.Count > 0 || army.OrderProvinceId >= 0) return false;
        if (army.FrontLineId >= 0 || army.OrderKind != ArmyOrder.Manual) return false;
        if (army.ProvinceId <= 0) return false;
        return SeaLift.Of(campaign, army) is null;
    }

    public static IReadOnlyList<Army> IdlePlayerArmies(Campaign campaign, int playerId)
    {
        if (campaign is null) return Array.Empty<Army>();
        return campaign.Armies
            .Where(a => IsIdleArmy(campaign, a, playerId))
            .OrderBy(a => a.Id)
            .ToArray();
    }

    public static Army NextIdleArmy(Campaign campaign, int playerId, int currentArmyId)
    {
        var idle = IdlePlayerArmies(campaign, playerId);
        if (idle.Count == 0) return null;
        if (currentArmyId <= 0) return idle[0];
        for (var i = 0; i < idle.Count; i++)
            if (idle[i].Id > currentArmyId)
                return idle[i];
        return idle[0];
    }

    /// <summary>
    /// Idle player divisions that can march toward <paramref name="targetProvinceId"/> (AoH #10).
    /// </summary>
    public static IReadOnlyList<Army> GatherableIdleArmies(Campaign campaign, Nation player, int targetProvinceId)
    {
        if (campaign is null || player is null || campaign.ProvinceMap is not { } map) return Array.Empty<Army>();
        if (targetProvinceId <= 0 || targetProvinceId > map.ProvinceCount) return Array.Empty<Army>();
        var controllers = campaign.ProvinceController ?? map.ProvinceToNation;
        var targetController = controllers[targetProvinceId];

        var canEnter = targetController == player.MapIndex || MilitaryAccess.CanEnter(campaign, player.Id, targetController);
        if (!canEnter)
        {
            var enemy = campaign.Nations.FirstOrDefault(n => n.MapIndex == targetController);
            if (enemy is null || !Warfare.OpenWars(campaign).Any(w => !w.Ended && w.Opposes(player.Id, enemy.Id)))
                return Array.Empty<Army>();
        }

        return IdlePlayerArmies(campaign, player.Id)
            .Where(a => a.ProvinceId != targetProvinceId)
            .ToArray();
    }

    public static int TotalWholeDivisions(IReadOnlyList<Army> selected)
    {
        if (selected is null || selected.Count == 0) return 0;
        var sum = 0;
        foreach (var a in selected)
            if (a is not null)
                sum += Math.Max(1, (int)MathF.Floor(a.Divisions));
        return sum;
    }

    /// <summary>
    /// Detaches/chooses <paramref name="wantedDivisions"/> from <paramref name="selected"/> and orders them to march
    /// to <paramref name="targetProvinceId"/> ("first where, then how many", AoH #8).
    /// </summary>
    public static SplitMoveResult ExecuteSplitMove(Campaign campaign, int issuerId, IReadOnlyList<Army> selected,
        int targetProvinceId, int wantedDivisions, bool appendWaypoint = false, bool supportAttack = false)
    {
        var outcomes = new Dictionary<ArmyMoveResult, int>();
        var movingIds = new List<int>();
        var remainingIds = new List<int>();
        if (campaign is null || selected is null || selected.Count == 0 || targetProvinceId <= 0)
            return new SplitMoveResult(movingIds, remainingIds, 0, outcomes, Loc.Get("No formation selected."));

        var eligible = selected
            .Where(a => a is not null && a.NationId == issuerId && campaign.Armies.Contains(a))
            .Distinct()
            .ToList();
        if (eligible.Count == 0)
            return new SplitMoveResult(movingIds, remainingIds, 0, outcomes, Loc.Get("No controllable formation selected."));

        var totalDivs = TotalWholeDivisions(eligible);
        var wanted = Math.Clamp(wantedDivisions, 1, Math.Max(1, totalDivs));

        // 100% -> move everything as-is without splitting.
        if (wanted >= totalDivs)
        {
            foreach (var army in eligible)
            {
                var move = new MoveArmyCommand
                {
                    IssuerId = issuerId,
                    ArmyId = army.Id,
                    TargetProvinceId = targetProvinceId,
                    AppendWaypoint = appendWaypoint,
                    SupportAttack = supportAttack
                };
                campaign.Commands.Enqueue(move);
                campaign.Commands.ApplyAll(campaign);
                outcomes[move.Result] = outcomes.GetValueOrDefault(move.Result) + 1;
                movingIds.Add(army.Id);
            }
            return new SplitMoveResult(movingIds, remainingIds, totalDivs, outcomes, "");
        }

        // Choose whole stacks first (smallest first), then split one stack if needed.
        var ordered = eligible.OrderBy(a => a.Divisions).ThenBy(a => a.Id).ToList();
        var needed = wanted;
        var movedDivs = 0;

        foreach (var army in ordered)
        {
            var stackDivs = Math.Max(1, (int)MathF.Floor(army.Divisions));
            if (needed <= 0)
            {
                remainingIds.Add(army.Id);
                continue;
            }
            if (stackDivs <= needed)
            {
                movingIds.Add(army.Id);
                needed -= stackDivs;
                movedDivs += stackDivs;
                continue;
            }
            // Need strictly fewer divisions than this stack holds: split off 'needed' divisions.
            if (!army.InBattle && army.Divisions >= 2f && needed >= 1 && army.Divisions - needed >= 1f)
            {
                var split = new SplitArmyCommand
                {
                    IssuerId = issuerId,
                    ArmyId = army.Id,
                    DetachDivisions = needed
                };
                campaign.Commands.Enqueue(split);
                campaign.Commands.ApplyAll(campaign);
                if (split.CreatedArmyId > 0)
                {
                    movingIds.Add(split.CreatedArmyId);
                    remainingIds.Add(army.Id);
                    movedDivs += needed;
                    needed = 0;
                    continue;
                }
            }
            remainingIds.Add(army.Id);
        }

        if (movingIds.Count == 0)
            return new SplitMoveResult(movingIds, remainingIds, 0, outcomes,
                Loc.Get("Cannot split: requires at least 2 divisions out of combat."));

        foreach (var armyId in movingIds)
        {
            var move = new MoveArmyCommand
            {
                IssuerId = issuerId,
                ArmyId = armyId,
                TargetProvinceId = targetProvinceId,
                AppendWaypoint = appendWaypoint,
                SupportAttack = supportAttack
            };
            campaign.Commands.Enqueue(move);
            campaign.Commands.ApplyAll(campaign);
            outcomes[move.Result] = outcomes.GetValueOrDefault(move.Result) + 1;
        }

        return new SplitMoveResult(movingIds, remainingIds, movedDivs, outcomes, "");
    }
}
