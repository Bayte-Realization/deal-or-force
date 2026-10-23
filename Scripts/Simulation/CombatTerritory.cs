using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

// Captures, liberation and occupation accounting. Battle resolution calls these
// only after deciding the outcome; this file does not select targets or orders.
public static partial class Warfare
{
    /// <summary>
    /// Who a province taken in battle is handed to: the winner, unless it is winning back
    /// somebody else's ground.
    ///
    /// A province retaken from the enemy used to become the property of whoever's division
    /// walked in. Brazil fighting beside Austria against an army that had overrun it ended
    /// the war holding Lower Austria, and a status-quo peace - each side keeps what it holds -
    /// then made that permanent: Brazil owned Vienna, Italy owned Bohemia, South Korea owned
    /// Mongolia, every one of them land its own ally had been fighting to keep. Ground that
    /// belongs to a country the winner is not at war with goes back to that country the day
    /// it is freed; the winner's own troops still stand on it, as guests.
    /// </summary>
    internal static ushort Liberator(Campaign campaign, int provinceId, Nation winner)
    {
        var owner = campaign.ProvinceMap.ProvinceToNation[provinceId];
        if (owner == 0 || owner == winner.MapIndex) return (ushort)winner.MapIndex;
        var owning = NavalOperations.NationAt(campaign, owner);
        if (owning is null || owning.Population <= 0 || CampaignDiplomacy.AtWar(campaign, winner.Id, owning.Id))
            return (ushort)winner.MapIndex;
        return owner;
    }

    private static bool Capture(Campaign campaign, StrategicWar war, int provinceId, int winnerId,
        IReadOnlyCollection<Army> advancing, IReadOnlyCollection<Army> retreating, string how)
    {
        // Taking a province moves whoever took it, so the province index no longer
        // describes where everyone is standing.
        _provinceIndexStale = true;
        // A battle keeps its original representative for its history, but that country
        // may have withdrawn all its units. An ally that actually occupies the ground
        // takes it (or liberates its owner); a support-only representative cannot annex it.
        Army firstOccupier = null;
        var winnerOccupies = false;
        foreach (var army in advancing)
        {
            if (army.SupportOnly) continue;
            firstOccupier ??= army;
            if (army.NationId == winnerId) winnerOccupies = true;
        }
        if (!winnerOccupies && firstOccupier is not null) winnerId = firstOccupier.NationId;
        var winner = campaign.NationById(winnerId);
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (winner is null || topology is null || controllers is null) return false;
        var occupying = firstOccupier is not null;
        if (occupying)
        {
            Tally(war.Id, "capture: " + how);
            war.OriginalController.TryAdd(provinceId, controllers[provinceId]);
            if (!campaign.SetProvinceController(provinceId, Liberator(campaign, provinceId, winner))) return false;
            war.FrontDirty = true;
        }
        war.UnopposedProgress.Remove(provinceId);
        var center = topology.CenterOf(provinceId);
        foreach (var army in advancing)
        {
            if (army.SupportOnly)
            {
                // It was ordered to help win the fight, not to take the ground. It keeps
                // its position, and with it whatever it had dug - which is the entire
                // reason a player gives this order instead of an ordinary attack.
                army.SupportOnly = false;
                army.OrderProvinceId = -1;
                army.OrderStateMapIndex = -1;
                army.DirectOrder = false;
                army.PlanActive = false;
                army.OrderKind = ArmyOrder.Manual;
                army.AssignedWarId = -1;
                army.Route.Clear();
                army.MoveProgress = 0f;
                army.PlannedRoute.Clear();
                continue;
            }
            army.ProvinceId = provinceId;
            army.StateMapIndex = topology.ProvinceToState[provinceId];
            army.Latitude = center.Latitude;
            army.Longitude = center.Longitude;
            army.Route.Clear();
            army.MoveProgress = 0f;
            if (army.PlannedRoute.Count > 0 && army.PlannedRoute[0] == provinceId)
            {
                army.PlannedRoute.RemoveAt(0);
                army.OrderStepsCompleted++;
            }
            else if (army.PlannedRoute.Count > 0)
            {
                // A battle can be resolved by another formation first. Drop the stale
                // corridor; it will be rebuilt from this new position on the next advance.
                army.PlannedRoute.Clear();
            }
            army.Entrenchment = 0f;
            army.ReadyDay = campaign.ElapsedDays + campaign.Defines.RestAfterCaptureDays;
            if (army.OrderProvinceId == provinceId)
            {
                var objectiveLine = BattlePlanning.LineFor(campaign, army);
                if (!army.DirectOrder && objectiveLine is { Kind: FrontLineKind.Offensive } && objectiveLine.Objectives.Count > 0)
                {
                    army.PlanActive = false;
                    army.PlannedRoute.Clear();
                    continue;
                }
                // A direct RMB attack ends in the selected province instead of
                // silently turning into a perpetual autonomous offensive.
                army.OrderKind = ArmyOrder.Manual;
                army.DirectOrder = false;
                army.AssignedWarId = -1;
                army.OrderProvinceId = -1;
                army.OrderStateMapIndex = -1;
                army.ObjectiveStateMapIndex = -1;
                army.PlanActive = false;
                army.PlannedRoute.Clear();
            }
        }
        foreach (var army in retreating)
        {
            var retreat = WarCoalition.SpreadRetreatProvince(campaign, war, army, provinceId, beaten: true);

            // Nowhere to go. It was left standing in the province it had just lost, inside
            // ground the enemy now holds, and the code said as much in a diagnostic marked
            // as a known defect. What has happened to a formation beaten out of its last
            // position with no friendly ground behind it is that it surrendered, and that is
            // now what happens to it - through the same path attrition uses, so it is
            // counted and reported rather than quietly left in place.
            if (retreat <= 0)
            {
                DiagnosticLogs.Write("simulation", $"SURRENDER army={army.Id} nation={army.NationId} province={provinceId} war={war.Id} reason=no_line_of_retreat");
                WarLosses.Surrender(campaign, army);
                Attrition.Destroy(campaign, army, "destroyed: cut off with no line of retreat");
                continue;
            }
            DiagnosticLogs.Write("simulation", $"RETREAT army={army.Id} nation={army.NationId} from={provinceId} to={retreat} ready_day={campaign.ElapsedDays + campaign.Defines.RestAfterRetreatDays}");
            var fallback = topology.CenterOf(retreat);
            army.ProvinceId = retreat;
            army.StateMapIndex = topology.ProvinceToState[retreat];
            army.Latitude = fallback.Latitude;
            army.Longitude = fallback.Longitude;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.MoveProgress = 0f;
            army.Entrenchment = 0f;
            army.ReadyDay = campaign.ElapsedDays + campaign.Defines.RestAfterRetreatDays;
        }
        return occupying;
    }

    private static void SecureOccupation(Campaign campaign)
    {
        if (!ReferenceEquals(_measuredCampaign, campaign)) MeasureOccupation(campaign);
        var controllers = campaign.ProvinceController;
        var occupation = campaign.ProvinceOccupation;
        if (controllers is null || occupation is null) return;
        // The garrison is whoever is standing in the province. Asking that with a Where
        // over the whole army list, once per occupied province per day, was a thousand
        // occupied provinces times fourteen hundred divisions - and it made securing
        // occupations the single slowest thing the simulation did.
        EnsureIndexed(campaign);
        foreach (var war in campaign.Wars)
        foreach (var pair in war.OriginalController)
        {
            var province = pair.Key;
            if (controllers[province] == pair.Value) { occupation[province] = 0f; continue; }
            var garrison = 0f;
            foreach (var army in InProvince(province))
                if (!army.InBattle) garrison += army.Power;
            // A rate, so it takes the step's share of a day. Without this a province was
            // occupied twenty-four times faster at an hourly step than at a daily one.
            occupation[province] = M.Clamp01(occupation[province] +
                campaign.Defines.OccupationDailyProgress * campaign.TickScale *
                M.Clamp(garrison / 3f, 0.25f, 1f));
        }
    }

    private static void UpdateWarMetrics(Campaign campaign, StrategicWar war)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var attacker = campaign.NationById(war.AttackerId);
        var defender = campaign.NationById(war.DefenderId);
        if (topology is null || controllers is null || attacker is null || defender is null) return;
        // Ground taken by anybody on a side counts for that side. Scoring the two leaders
        // alone meant an ally could occupy half a country and the war score would not move,
        // which is a coalition war that does not know it is one.
        var attackerHome = 0;
        var defenderHome = 0;
        var attackerOccupied = 0;
        var defenderOccupied = 0;
        foreach (var attackingId in war.Side(true))
        {
            if (campaign.NationById(attackingId) is not { } member) continue;
            var map = (ushort)member.MapIndex;
            attackerHome += HomeProvinces.GetValueOrDefault(map);
            foreach (var defendingId in war.Side(false))
                if (campaign.NationById(defendingId) is { } foe)
                    attackerOccupied += Occupied(map, (ushort)foe.MapIndex);
        }
        foreach (var defendingId in war.Side(false))
        {
            if (campaign.NationById(defendingId) is not { } member) continue;
            var map = (ushort)member.MapIndex;
            defenderHome += HomeProvinces.GetValueOrDefault(map);
            foreach (var attackingId in war.Side(true))
                if (campaign.NationById(attackingId) is { } foe)
                    defenderOccupied += Occupied(map, (ushort)foe.MapIndex);
        }
        var attackerOccupiedShare = attackerHome == 0 ? 0f : (float)attackerOccupied / attackerHome;
        var defenderOccupiedShare = defenderHome == 0 ? 0f : (float)defenderOccupied / defenderHome;
        var attackerLossBurden = war.AttackerLosses / MathF.Max(50_000f, attacker.Population * 0.035f);
        var defenderLossBurden = war.DefenderLosses / MathF.Max(50_000f, defender.Population * 0.035f);
        war.AttackerWarSupport = M.Clamp01(attacker.WarSupport - attackerLossBurden * 0.52f - attackerOccupiedShare * 0.58f);
        war.DefenderWarSupport = M.Clamp01(defender.WarSupport - defenderLossBurden * 0.52f - defenderOccupiedShare * 0.58f);

        // The score itself: occupation by worth, the capitals, the armies, the battles, the
        // war goal, the momentum and the home front, and collapse (WarScoring).
        WarScoring.Update(campaign, war);
    }

}
