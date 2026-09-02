using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed class StrategicWar
{
    public int Id;
    public string Name = "";
    public int AttackerId;
    public int DefenderId;
    public int StartedDay;
    public float AttackerWarSupport = 1f;
    public float DefenderWarSupport = 1f;
    public float AttackerLosses;
    public float DefenderLosses;
    public float OccupationScore;
    public float BattleScore;
    public float ObjectiveScore;
    public float SupportScore;
    public float WarScore;
    public float Reparations;
    public int ClaimedStateMapIndex = -1;
    public bool Ended;
    public bool FrontDirty = true;
    public int LastFrontDay = -1;
    public int LastPeaceProposalDay = -1;
    public string LastNegotiation = "";
    public int EndedDay = -1;
    public int WinnerId = -1;
    public string PeaceSummary = "";
    public readonly HashSet<int> AttackerFront = new();
    public readonly HashSet<int> DefenderFront = new();
    public readonly Dictionary<int, ushort> OriginalController = new();
    public readonly Dictionary<int, float> UnopposedProgress = new();

    public bool Involves(int nationId) => nationId == AttackerId || nationId == DefenderId;
    public IReadOnlySet<int> FriendlyFront(int nationId) =>
        nationId == AttackerId ? AttackerFront : nationId == DefenderId ? DefenderFront : EmptyFront;

    private static readonly HashSet<int> EmptyFront = new();
}

public static class WarBootstrap
{
    public static void Seed(Campaign campaign)
    {
        var russia = campaign.NationByTag("RUS");
        var ukraine = campaign.NationByTag("UKR");
        if (russia is null || ukraine is null) return;
        campaign.Wars.Add(new StrategicWar
        {
            Id = 0,
            Name = "Russo-Ukrainian War",
            AttackerId = russia.Id,
            DefenderId = ukraine.Id,
            StartedDay = campaign.ElapsedDays,
            ClaimedStateMapIndex = campaign.States.FirstOrDefault(state =>
                state.NationMapIndex == ukraine.MapIndex &&
                state.Name.Contains("Kyiv", StringComparison.OrdinalIgnoreCase))?.MapIndex ??
                campaign.States.Where(state => state.NationMapIndex == ukraine.MapIndex)
                    .OrderByDescending(state => state.Population).FirstOrDefault()?.MapIndex ?? -1
        });
        var relation = CampaignDiplomacy.Get(campaign, russia.Id, ukraine.Id);
        relation.Stance = DiplomaticStance.AtWar;
        relation.Opinion = -80f;
    }
}

public static partial class Warfare
{
    public static void RebuildFronts(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController ?? topology?.ProvinceToNation;
        if (topology is null || controllers is null) return;
        foreach (var war in campaign.Wars)
        {
            war.AttackerFront.Clear();
            war.DefenderFront.Clear();
            war.FrontDirty = false;
            war.LastFrontDay = campaign.ElapsedDays;
            if (war.Ended) continue;
            var attacker = campaign.NationById(war.AttackerId);
            var defender = campaign.NationById(war.DefenderId);
            if (attacker is null || defender is null) continue;
            var attackerMap = (ushort)attacker.MapIndex;
            var defenderMap = (ushort)defender.MapIndex;

            for (var province = 1; province <= topology.ProvinceCount; province++)
            {
                var owner = controllers[province];
                if (owner != attackerMap && owner != defenderMap) continue;
                foreach (var neighbor in topology.Neighbors[province])
                {
                    var other = controllers[neighbor];
                    if (owner == attackerMap && other == defenderMap)
                    {
                        war.AttackerFront.Add(province);
                        war.DefenderFront.Add(neighbor);
                    }
                    else if (owner == defenderMap && other == attackerMap)
                    {
                        war.DefenderFront.Add(province);
                        war.AttackerFront.Add(neighbor);
                    }
                }
            }
        }
    }
}

public static partial class FrontPlanner
{
    public static HashSet<int> ReserveLine(Campaign campaign, StrategicWar war, int nationId)
    {
        var result = new HashSet<int>();
        var topology = campaign.ProvinceMap;
        var nation = campaign.NationById(nationId);
        var controllers = campaign.ProvinceController;
        if (topology is null || nation is null || controllers is null) return result;
        var front = war.FriendlyFront(nationId);
        foreach (var province in front)
        foreach (var neighbor in topology.Neighbors[province])
            if (!front.Contains(neighbor) && controllers[neighbor] == nation.MapIndex)
                result.Add(neighbor);
        return result.Count > 0 ? result : new HashSet<int>(front);
    }

    public static void RefreshAssignments(Campaign campaign, StrategicWar war)
    {
        foreach (var army in campaign.Armies.Where(army => army.AssignedWarId == war.Id &&
                     army.OrderKind != ArmyOrder.Manual && !army.InBattle).ToArray())
        {
            var expected = army.OrderKind == ArmyOrder.ReserveFront
                ? ReserveLine(campaign, war, army.NationId)
                : war.FriendlyFront(army.NationId);
            if (expected.Count == 0 || expected.Contains(army.ProvinceId) ||
                (army.OrderProvinceId > 0 && expected.Contains(army.OrderProvinceId)))
                continue;
            new SetArmyOrderCommand
            {
                IssuerId = army.NationId,
                ArmyId = army.Id,
                Order = army.OrderKind,
                ObjectiveStateMapIndex = army.ObjectiveStateMapIndex
            }.Apply(campaign);
        }
    }

    public static void AssignAi(Campaign campaign)
    {
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            AssignSide(campaign, war, war.AttackerId, true);
            AssignSide(campaign, war, war.DefenderId, false);
        }
    }

    public static void ReassignAi(Campaign campaign, StrategicWar war)
    {
        if (war.Ended) return;
        AssignSide(campaign, war, war.AttackerId, true);
        AssignSide(campaign, war, war.DefenderId, false);
    }

    public static void UpdateAiExecution(Campaign campaign, StrategicWar war)
    {
        foreach (var army in campaign.Armies.Where(army => army.AssignedWarId == war.Id && !army.PlanActive &&
                     (army.OrderKind == ArmyOrder.AdvanceFront || army.OrderKind == ArmyOrder.Spearhead)))
        {
            var nation = campaign.NationById(army.NationId);
            if (nation is null || nation.IsHuman) continue;
            var profile = AiStrategyCatalog.Resolve(campaign, war, nation);
            if (army.Planning < profile.ExecutePlanningThreshold) continue;
            new SetArmyPlanExecutionCommand { IssuerId = nation.Id, ArmyId = army.Id, Active = true }.Apply(campaign);
        }
    }

    private static void AssignSide(Campaign campaign, StrategicWar war, int nationId, bool attacking)
    {
        var nation = campaign.NationById(nationId);
        if (nation is null || nation.IsHuman) return;
        var profile = AiStrategyCatalog.Resolve(campaign, war, nation);
        var armies = campaign.Armies.FindAll(army => army.NationId == nationId);
        armies.Sort((a, b) => b.Power.CompareTo(a.Power));
        var advanceShare = attacking ? profile.AdvanceShareAttacker : profile.AdvanceShareDefender;
        var spearhead = armies.Count == 0 ? 0 : Math.Min((int)MathF.Round(armies.Count * advanceShare),
            Math.Max(1, (int)MathF.Round(armies.Count * profile.SpearheadShare)));
        var advance = (int)MathF.Round(armies.Count * advanceShare);
        var defend = (int)MathF.Round(armies.Count * profile.DefendShare);
        for (var index = 0; index < armies.Count; index++)
        {
            var army = armies[index];
            var order = index < spearhead ? ArmyOrder.Spearhead :
                index < advance ? ArmyOrder.AdvanceFront :
                index < advance + defend ? ArmyOrder.DefendFront : ArmyOrder.ReserveFront;
            var stance = order == ArmyOrder.Spearhead || order == ArmyOrder.AdvanceFront
                ? profile.OffensiveStance
                : order == ArmyOrder.DefendFront ? profile.DefensiveStance : ArmyStance.Balanced;

            // Re-issuing an order a division already has is not free: it clears the route,
            // picks a fresh target from a front list that shifts every time a province
            // changes hands, and sends the division marching sideways - which costs
            // planning. Doing that to the whole army every time the line moved meant an AI
            // that spent its war walking up and down its own front and never attacked.
            // A division is only re-tasked when what is being asked of it actually changed.
            if (army.OrderKind != order || (order == ArmyOrder.Spearhead &&
                    army.ObjectiveStateMapIndex != war.ClaimedStateMapIndex))
                new SetArmyOrderCommand
                {
                    IssuerId = nationId,
                    ArmyId = army.Id,
                    ObjectiveStateMapIndex = war.ClaimedStateMapIndex,
                    Order = order
                }.Apply(campaign);

            if (army.Stance != stance)
                new SetArmyStanceCommand { IssuerId = nationId, ArmyId = army.Id, Stance = stance }.Apply(campaign);

            var execute = army.Planning >= profile.ExecutePlanningThreshold;
            if (army.PlanActive != execute)
                new SetArmyPlanExecutionCommand
                {
                    IssuerId = nationId,
                    ArmyId = army.Id,
                    Active = execute
                }.Apply(campaign);
        }
    }
}
