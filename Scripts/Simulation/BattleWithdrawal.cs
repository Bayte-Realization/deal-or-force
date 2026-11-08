using System;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Withdraw only the issuer's formation, through the replay/network command path.
/// A Share below one pulls only that fraction of the divisions out: the rest stand fast in
/// the battle, so a country can feed one fight and hold its line at the same time instead
/// of every division it owns being pinned by a single enemy touch.</summary>
public sealed class WithdrawArmyFromBattleCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public int TargetProvinceId { get; init; } = -1;
    /// <summary>The fraction of divisions to pull out, 0.05..1. One (the default) is the
    /// whole-formation withdrawal this command has always been.</summary>
    public float Share { get; init; } = 1f;
    public bool Withdrawn { get; private set; }
    public bool Applied { get; private set; }
    public int WithdrawnArmyId { get; private set; } = -1;
    public string Result { get; private set; } = "Not in battle";

    public void Apply(Campaign campaign)
    {
        Withdrawn = false;
        Applied = true;
        WithdrawnArmyId = -1;
        Result = "Not in battle";
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var army = campaign?.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId) { Result = "Order not authorised"; return; }
        if (!float.IsFinite(Share) || Share <= 0f || Share > 1f)
        { Result = "Invalid withdrawal share"; return; }
        var share = Share;
        if (share < 0.999f)
        {
            Withdrawn = Warfare.WithdrawShare(campaign, army, share, out var partial, TargetProvinceId);
            Result = partial;
            if (Withdrawn) WithdrawnArmyId = campaign.Armies[^1].Id;
        }
        else
        {
            Withdrawn = Warfare.Withdraw(campaign, army, out var full, TargetProvinceId);
            Result = full;
            if (Withdrawn) WithdrawnArmyId = army.Id;
        }
    }
}

public static partial class Warfare
{
    /// <summary>
    /// Pull a share of a formation out of a battle while the rest of it keeps fighting.
    ///
    /// The leaving divisions become a detachment of their own: the defenders' part marches
    /// to the same friendly line a full retreat would take (and pays the same rest, because
    /// ground given up under fire is ground given up), the attackers' part simply stops
    /// pressing and stands where it was. The formations that stay keep their place in the
    /// battle, their losses and their organisation - the fight reads smaller, not reset.
    /// </summary>
    internal static bool WithdrawShare(Campaign campaign, Army army, float share, out string result,
        int targetProvinceId = -1)
    {
        result = "Not in battle";
        if (!float.IsFinite(share) || share <= 0 || share >= 1f)
        { result = "Invalid withdrawal share"; return false; }
        var quote = AssessWithdrawal(campaign, army, share, targetProvinceId);
        if (!quote.Available) { result = quote.Refusal; return false; }
        var battle = campaign.Battles.Find(candidate => candidate.Id == army.BattleId && !candidate.Ended);
        var attacking = quote.Attacking;
        var leaving = quote.Leaving;
        var remaining = quote.Remaining;
        var retreat = quote.Destination;

        var fraction = leaving / army.Divisions;
        var personnel = (int)MathF.Floor(army.Personnel * fraction);
        var establishment = (int)MathF.Floor(army.MaxPersonnel * fraction);
        var wounded = army.Recovering * fraction;
        var id = campaign.Armies.Max(candidate => candidate.Id) + 1;
        var map0 = campaign.ProvinceMap;
        var detachment = new Army
        {
            Id = id,
            NationId = army.NationId,
            StateMapIndex = army.StateMapIndex,
            ProvinceId = army.ProvinceId,
            Divisions = leaving,
            Name = $"{army.Name} Detachment",
            TemplateId = army.TemplateId,
            Personnel = personnel,
            MaxPersonnel = establishment,
            Recovering = wounded,
            Equipment = army.Equipment,
            Experience = army.Experience,
            Cohesion = army.Cohesion,
            Recon = army.Recon,
            Engineers = army.Engineers,
            Artillery = army.Artillery,
            Stance = army.Stance,
            ReinforcementPriority = army.ReinforcementPriority,
            ArmyGroupId = army.ArmyGroupId,
            ContractUntilDay = army.ContractUntilDay,
            Training = army.Training,
            ReadyDay = army.ReadyDay,
            PlanActive = false,
            DirectOrder = true,
            Organisation = army.Organisation,
            Strength = army.Strength,
            SupplyLevel = army.SupplyLevel,
            SupplyDistance = army.SupplyDistance,
            SupplySourceStateMapIndex = army.SupplySourceStateMapIndex,
            SupplyCongestion = army.SupplyCongestion,
            CutOff = army.CutOff,
            Latitude = army.Latitude,
            Longitude = army.Longitude
        };
        army.Divisions = remaining;
        army.Personnel -= personnel;
        army.MaxPersonnel -= establishment;
        army.Recovering -= wounded;
        campaign.Armies.Add(detachment);
        // The new ID must not be immediately drafted back into this very battle.
        (attacking ? battle.WithdrawnAttackers : battle.WithdrawnDefenders).Add(id);
        if (!attacking)
        {
            detachment.ProvinceId = retreat;
            detachment.StateMapIndex = map0.ProvinceToState[retreat];
            (detachment.Latitude, detachment.Longitude) = map0.CenterOf(retreat);
            detachment.Entrenchment = 0;
            // Ground was given up under fire; the detachment needs a breath before it is a
            // formation again. The part that stayed gets no rest - it never left the line.
            detachment.ReadyDay = Math.Max(army.ReadyDay, campaign.ElapsedDays + campaign.Defines.RestAfterRetreatDays);
        }
        _provinceIndexStale = true;
        EnsureIndexed(campaign);
        campaign.MarkStrategicChange();
        result = attacking
            ? $"{leaving:0} division(s) broke off the attack"
            : $"{leaving:0} division(s) fell back to friendly ground";
        return true;
    }

    /// <summary>Read-only preflight shared by orders and the autonomous front staff.</summary>
    internal static int WithdrawalProvince(Campaign campaign, StrategicWar war, Army army, int targetProvinceId)
    {
        var map = campaign.ProvinceMap;
        if (map is null || army.ProvinceId <= 0 || army.ProvinceId >= map.Neighbors.Length) return 0;
        if (targetProvinceId > 0 && targetProvinceId <= map.ProvinceCount)
        {
            if (map.Neighbors[army.ProvinceId].Contains(targetProvinceId) &&
                WarCoalition.SafeRetreatProvince(campaign, war, army.NationId, targetProvinceId)) return targetProvinceId;
            var route = WarCoalition.FriendlyRoute(campaign, war, army.NationId, army.ProvinceId, targetProvinceId);
            if (route.Length > 0 && WarCoalition.SafeRetreatProvince(campaign, war, army.NationId, route[0])) return route[0];
        }
        return WarCoalition.SpreadRetreatProvince(campaign, war, army, army.ProvinceId);
    }

    internal static bool Withdraw(Campaign campaign, Army army, out string result, int targetProvinceId = -1)
    {
        result = "Not in battle";
        var quote = AssessWithdrawal(campaign, army, 1, targetProvinceId);
        if (!quote.Available) { result = quote.Refusal; return false; }
        var battle = campaign.Battles.Find(candidate => candidate.Id == army.BattleId && !candidate.Ended);
        var war = campaign.Wars.Find(candidate => candidate.Id == battle.WarId && !candidate.Ended);
        var attacking = quote.Attacking;
        var retreat = quote.Destination;

        var participants = attacking ? battle.Attackers : battle.Defenders;
        participants.Remove(army.Id);
        BattleMovementOrders.Clear(army);
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
        UseBattleBuckets(campaign);
        EnsureIndexed(campaign);
        var attackers = Side(war, battle.Attackers, battle.AttackerId, AttackingSide);
        if (attackers.Count == 0) Close(campaign, battle, battle.DefenderId, "Attackers withdrew");
        else
        {
            // An uncommitted garrison still defends the province after one unit leaves.
            foreach (var remaining in InProvince(battle.ProvinceId))
                if (!remaining.InBattle && WarCoalition.SameSide(war, remaining.NationId, battle.DefenderId) && remaining.Divisions >= .1f)
                    Join(battle, remaining, false);
            var defenders = Side(war, battle.Defenders, battle.DefenderId, DefendingSide);
            if (defenders.Count == 0)
            {
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
