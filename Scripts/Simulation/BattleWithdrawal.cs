using System;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Withdraw only the issuer's formation, through the replay/network command path.</summary>
public sealed class WithdrawArmyFromBattleCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public bool Withdrawn { get; private set; }
    public string Result { get; private set; } = "Not in battle";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        Withdrawn = false;
        var army = campaign?.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId) { Result = "Order not authorised"; return; }
        Withdrawn = Warfare.Withdraw(campaign, army, out var result);
        Result = result;
    }
}

public static partial class Warfare
{
    internal static bool Withdraw(Campaign campaign, Army army, out string result)
    {
        result = "Not in battle";
        var battle = campaign.Battles.Find(candidate => candidate.Id == army.BattleId && !candidate.Ended);
        if (battle is null || army.Divisions < .1f) return false;
        var attacking = battle.Attackers.Contains(army.Id);
        if (!attacking && !battle.Defenders.Contains(army.Id)) return false;

        var retreat = 0;
        if (!attacking)
        {
            var nation = campaign.NationById(army.NationId);
            var map = campaign.ProvinceMap;
            var control = campaign.ProvinceController;
            if (nation is not null && map is not null && control is not null &&
                army.ProvinceId > 0 && army.ProvinceId < map.Neighbors.Length)
                foreach (var neighbor in map.Neighbors[army.ProvinceId])
                    if (control[neighbor] == nation.MapIndex && !map.IsWater(neighbor) &&
                        (retreat == 0 || neighbor < retreat)) retreat = neighbor;
            // A trapped defender cannot cancel enemy fire by pressing a button. No
            // state changes precede this check, and no surrender is hidden in the order.
            if (retreat == 0) { result = "No friendly line of retreat"; return false; }
        }

        var participants = attacking ? battle.Attackers : battle.Defenders;
        participants.Remove(army.Id);
        (attacking ? battle.WithdrawnAttackers : battle.WithdrawnDefenders).Add(army.Id);
        army.BattleId = -1;
        army.Route.Clear();
        army.PlannedRoute.Clear();
        army.MoveProgress = 0;
        army.PlanActive = false;
        army.DirectOrder = false;
        army.SupportOnly = false;
        army.RedeployTarget = -1;
        army.OrderKind = ArmyOrder.Manual;
        DrawFrontLineCommand.Detach(campaign, army);
        army.AssignedWarId = -1;
        army.OrderProvinceId = army.OrderStateMapIndex = army.ObjectiveStateMapIndex = -1;
        army.OrderRouteSteps = army.OrderStepsCompleted = 0;
        if (!attacking)
        {
            var map = campaign.ProvinceMap;
            army.ProvinceId = retreat;
            army.StateMapIndex = map.ProvinceToState[retreat];
            (army.Latitude, army.Longitude) = map.CenterOf(retreat);
            army.Entrenchment = 0;
            army.ReadyDay = Math.Max(army.ReadyDay, campaign.ElapsedDays + campaign.Defines.RestAfterRetreatDays);
        }
        // Do not heal strength, organisation, equipment or reset the battle's losses.
        _provinceIndexStale = true;
        EnsureIndexed(campaign);
        var attackers = Side(battle.Attackers, battle.AttackerId, AttackingSide);
        if (attackers.Count == 0) Close(campaign, battle, battle.DefenderId, "Attackers withdrew");
        else
        {
            // An uncommitted garrison still defends the province after one unit leaves.
            foreach (var remaining in InProvince(battle.ProvinceId))
                if (!remaining.InBattle && remaining.NationId == battle.DefenderId && remaining.Divisions >= .1f)
                    Join(battle, remaining, false);
            var defenders = Side(battle.Defenders, battle.DefenderId, DefendingSide);
            if (defenders.Count == 0)
            {
                var war = campaign.Wars.Find(candidate => candidate.Id == battle.WarId && !candidate.Ended);
                var occupied = war is not null && Capture(campaign, war, battle.ProvinceId,
                    battle.AttackerId, attackers, defenders, "defenders withdrew");
                Close(campaign, battle, battle.AttackerId,
                    occupied ? "Defenders withdrew" : "Support ended; province not occupied");
            }
        }
        campaign.MarkStrategicChange();
        result = attacking ? "Attack stopped" : "Retreated to friendly ground";
        return true;
    }
}
