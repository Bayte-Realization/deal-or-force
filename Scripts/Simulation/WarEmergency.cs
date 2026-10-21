using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Whether a country in an existential war gets the emergency staff pass.</summary>
    public bool WarEmergencyEnabled { get; set; } = true;

    /// <summary>How much of a country's own ground must be occupied before the war is existential.</summary>
    public float WarEmergencyCriticalShare { get; set; } = 0.05f;

    /// <summary>Divisions the emergency staff may re-post into empty posts in one day.</summary>
    public int WarEmergencyReinforcementsPerDay { get; set; } = 2;

    /// <summary>Emergency formations a country may queue in one day when its line is short.</summary>
    public int WarEmergencyRaisesPerDay { get; set; } = 1;
}

/// <summary>
/// The last line between a country and a map that has stopped moving.
///
/// The ordinary front planner is deliberately conservative: it distributes a finite army,
/// leaves marches alone for a few days and does not invent troops. That is right for a
/// border quarrel and wrong for a war for existence, where an unheld province next to an
/// enemy is not a scheduling preference. This pass is the exception, and it stays inside
/// the same commands: it re-posts existing formations, takes an undefended enemy province
/// with a formation already in the theatre, and queues an emergency formation through
/// <see cref="RaiseArmyCommand"/> with the same manpower, training and debt rules.
///
/// It runs only for countries the computer commands (or whose field command was delegated)
/// when a held line is threatened. It writes no save state and creates nothing by
/// itself; every effect is a normal command, so replay and network authority see it.
/// </summary>
public static class WarEmergency
{
    public static int Reinforced { get; private set; }
    public static int Rushed { get; private set; }
    public static int Raised { get; private set; }

    private static readonly List<int> Posts = new();
    private static readonly List<Army> Candidates = new();
    private static readonly HashSet<long> Seen = new();
    private static readonly HashSet<int> MilitiaToday = new();
    private static readonly HashSet<int> RaisedToday = new();
    private static readonly HashSet<int> Held = new();
    private static readonly HashSet<int> RepostedToday = new();
    private static readonly Dictionary<int, int> ReinforcedToday = new();

    public static void Tick(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null ||
            !campaign.Defines.WarEmergencyEnabled || !campaign.StepOpensNewDay) return;
        Seen.Clear();
        MilitiaToday.Clear();
        RaisedToday.Clear();
        RepostedToday.Clear();
        ReinforcedToday.Clear();
        foreach (var war in campaign.Wars)
        {
            // A limited border war does not need a general mobilisation, but the emergency
            // triggers below are still about holes and occupied ground; a limited war with
            // its line held never reaches the branch that acts.
            if (war.Ended) continue;
            for (var side = 0; side < 2; side++)
            foreach (var nationId in war.Side(side == 0))
            {
                var key = ((long)war.Id << 32) | (uint)nationId;
                if (!Seen.Add(key)) continue;
                var nation = campaign.NationById(nationId);
                if (nation is null || nation.Population <= 0 || nation.Landless) continue;
                if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Command)) continue;
                Review(campaign, nation, war);
            }
        }
    }

    private static void Review(Campaign campaign, Nation nation, StrategicWar war)
    {
        Posts.Clear();
        foreach (var province in war.FriendlyFront(nation.Id))
            if (province > 0 && province < campaign.ProvinceController.Length &&
                campaign.ProvinceController[province] == (ushort)nation.MapIndex)
                Posts.Add(province);
        if (Posts.Count == 0) return;
        Held.Clear();
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id || army.Divisions < .1f || army.EmbarkedFleetId >= 0) continue;
            if (army.ProvinceId > 0) Held.Add(army.ProvinceId);
            if (army.OrderProvinceId > 0 && army.IsMoving && army.Route[^1] == army.OrderProvinceId)
                Held.Add(army.OrderProvinceId);
        }
        var occupied = war.IsAttacking(nation.Id) ? war.AttackerOccupiedValue : war.DefenderOccupiedValue;
        var unheld = Posts.Count(province => !Held.Contains(province));
        var capitalLost = nation.CapitalProvinceId > 0 &&
            nation.CapitalProvinceId < campaign.ProvinceController.Length &&
            campaign.ProvinceController[nation.CapitalProvinceId] != (ushort)nation.MapIndex;
        if (!capitalLost && occupied < campaign.Defines.WarEmergencyCriticalShare &&
            unheld == 0) return;

        if (unheld > 0)
            Reinforce(campaign, nation, war, Math.Max(0, campaign.Defines.WarEmergencyReinforcementsPerDay -
                ReinforcedToday.GetValueOrDefault(nation.Id)));
        unheld = Posts.Count(province => !Held.Contains(province));
        // A hole can be closed by taking the empty ground on the other side of it. This is
        // deliberately after the line is mended: an army that leaves a hole to chase a farm
        // has not helped. Only formations with no other front duty are used.
        if (unheld == 0)
            Rushed += RushGap(campaign, nation, war);
        if (unheld > 0 || Posts.Count > CountField(campaign, nation))
            Raised += RaiseEmergency(campaign, nation);
        // A country whose regular reserve and recruiting queue are both empty can still
        // put the people who live on the threatened ground under arms. This is the same
        // local militia the daily pass raises; it costs no manpower and is not a field
        // division, but it is a formation already standing where the hole is.
        if (unheld > 0)
            Raised += EmergencyMilitia(campaign, nation, war);
    }

    private static int Reinforce(Campaign campaign, Nation nation, StrategicWar war, int budget)
    {
        var used = 0;
        var posts = Posts.Where(province => !Held.Contains(province))
            .OrderBy(province => province).ToArray();
        foreach (var post in posts)
        {
            if (used >= budget) break;
            Candidates.Clear();
            foreach (var army in campaign.Armies)
            {
                if (army.NationId != nation.Id || army.InBattle || army.IsMoving || army.SupportOnly ||
                    army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 || army.DirectOrder ||
                    RepostedToday.Contains(army.Id) || army.FrontLineId >= 0 ||
                    army.OrderKind is not (ArmyOrder.Manual or ArmyOrder.ReserveFront) ||
                    (army.TemplateId == MilitiaCore.TemplateId &&
                     (army.AssignedWarId >= 0 || army.FrontLineId >= 0 || army.OrderKind != ArmyOrder.Manual)) ||
                    army.Organisation < 0.15f || army.SupplyLevel < 0.15f) continue;
                // A division already attacking another war is not a reserve; a formation
                // standing in reserve or belonging to no war is exactly what is needed.
                if (army.AssignedWarId >= 0 && army.AssignedWarId != war.Id) continue;
                Candidates.Add(army);
            }
            if (Candidates.Count == 0) break;
            var target = Candidates
                .OrderBy(army => army.TemplateId == MilitiaCore.TemplateId ? 1 : 0)
                .ThenBy(army => army.AssignedWarId == war.Id && army.OrderKind == ArmyOrder.ReserveFront ? 0 :
                    army.AssignedWarId < 0 ? 1 : 2)
                .ThenBy(army => Distance(campaign, army.ProvinceId, post))
                .ThenBy(army => army.Id).First();
            new SetArmyOrderCommand
            {
                IssuerId = nation.Id, ArmyId = target.Id, WarId = war.Id,
                Order = ArmyOrder.DefendFront, PreferredProvinceId = post
            }.Apply(campaign);
            if (target.AssignedWarId != war.Id) continue;
            RepostedToday.Add(target.Id);
            if (target.ProvinceId != target.OrderProvinceId &&
                (!target.IsMoving || target.Route[^1] != target.OrderProvinceId)) continue;
            Held.Add(target.OrderProvinceId);
            used++;
            Reinforced++;
        }
        ReinforcedToday[nation.Id] = ReinforcedToday.GetValueOrDefault(nation.Id) + used;
        return used;
    }

    /// <summary>Take one undefended enemy province beside this front with a reserve already there.</summary>
    private static int RushGap(Campaign campaign, Nation nation, StrategicWar war)
    {
        foreach (var army in campaign.Armies.OrderBy(candidate => candidate.Id))
        {
            if (army.NationId != nation.Id || army.TemplateId == MilitiaCore.TemplateId ||
                RepostedToday.Contains(army.Id)) continue;
            var next = Warfare.ReserveOpening(campaign, war, army);
            if (next <= 0) continue;
            var command = new MoveArmyCommand
            {
                IssuerId = nation.Id, ArmyId = army.Id, TargetProvinceId = next
            };
            command.Apply(campaign);
            if (command.Result is ArmyMoveResult.AttackIssued or ArmyMoveResult.Issued or
                ArmyMoveResult.QueuedAfterBattle) return 1;
        }
        return 0;
    }

    private static int EmergencyMilitia(Campaign campaign, Nation nation, StrategicWar war)
    {
        if (!campaign.FeatureEnabled("militia") || !MilitiaToday.Add(nation.Id)) return 0;
        var weights = MilitiaCore.Weights;
        var post = Posts.FirstOrDefault(province => !Held.Contains(province));
        if (post <= 0) return 0;
        // Use a local guard that already exists before inventing another one. Its region
        // keeps its remaining guards; this is the same detachment the staff makes when a
        // country has no regular formation left.
        var detached = campaign.Armies
            .Where(army => army.NationId == nation.Id && army.TemplateId == MilitiaCore.TemplateId &&
                army.ProvinceId > 0 && army.AssignedWarId < 0 && army.FrontLineId < 0 &&
                !army.DirectOrder && !army.InBattle && army.OrderKind == ArmyOrder.Manual)
            .OrderBy(army => Distance(campaign, army.ProvinceId, post))
            .ThenBy(army => army.Id).FirstOrDefault();
        if (detached is not null)
        {
            new SetArmyOrderCommand
            {
                IssuerId = nation.Id, ArmyId = detached.Id, WarId = war.Id,
                Order = ArmyOrder.DefendFront, PreferredProvinceId = post
            }.Apply(campaign);
            return 1;
        }
        var standing = campaign.Armies.Count(army => army.NationId == nation.Id &&
            army.TemplateId == MilitiaCore.TemplateId);
        if (standing >= Math.Max(1, weights.PerCountry)) return 0;
        foreach (var state in campaign.StatesOf(nation))
        {
            if (state.Population < Math.Max(1f, weights.PopulationPerFormation)) continue;
            if (RecruitmentSystem.DeploymentProvince(campaign, nation.Id, state.MapIndex) <= 0) continue;
            if (!StateTouchesEnemy(campaign, nation, state.MapIndex)) continue;
            if (!MilitiaCore.Found(campaign, nation, state)) continue;
            var local = campaign.Armies.LastOrDefault(army => army.NationId == nation.Id &&
                army.TemplateId == MilitiaCore.TemplateId && army.ProvinceId > 0);
            if (local is null) return 0;
            new SetArmyOrderCommand
            {
                IssuerId = nation.Id, ArmyId = local.Id, WarId = war.Id,
                Order = ArmyOrder.DefendFront, PreferredProvinceId = post
            }.Apply(campaign);
            return 1;
        }
        return 0;
    }

    private static bool StateTouchesEnemy(Campaign campaign, Nation nation, int state)
    {
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (map.ProvinceToState[province] != state ||
                controllers[province] != (ushort)nation.MapIndex) continue;
            foreach (var next in map.Neighbors[province])
            {
                if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                var other = campaign.Nations.FirstOrDefault(candidate =>
                    candidate.MapIndex == controllers[next]);
                if (other is not null && campaign.Wars.Any(war => !war.Ended &&
                        war.Opposes(nation.Id, other.Id))) return true;
            }
        }
        return false;
    }

    private static int RaiseEmergency(Campaign campaign, Nation nation)
    {
        if (RaisedToday.Contains(nation.Id)) return 0;
        if (nation.ManpowerReserve < MilitaryBootstrap.DivisionSize) return 0;
        var home = EmergencyHome(campaign, nation);
        if (home < 0) return 0;
        var budget = Math.Max(0, campaign.Defines.WarEmergencyRaisesPerDay);
        var raised = 0;
        for (var index = 0; index < budget; index++)
        {
            if (nation.ManpowerReserve < MilitaryBootstrap.DivisionSize) break;
            var command = new RaiseArmyCommand
            {
                IssuerId = nation.Id, StateMapIndex = home, Divisions = 1f,
                // A country defending its own ground does not stop because today's cash
                // account is empty; the cost stays on the debt, as it does in Attrition.
                AllowDeficit = true
            };
            command.Apply(campaign);
            if (command.FailureReason.Length > 0) break;
            raised++;
        }
        if (raised > 0)
        {
            RaisedToday.Add(nation.Id);
            // Deficit spending is borrowing, not a negative cash balance: the same
            // migration every scripted charge and every weekly review performs.
            SovereignFinance.NormalizeAccounts(campaign, nation);
        }
        return raised;
    }

    private static int EmergencyHome(Campaign campaign, Nation nation)
    {
        var best = -1;
        var people = 0L;
        foreach (var state in campaign.States)
        {
            if (state.NationMapIndex != nation.MapIndex || state.Population <= people) continue;
            if (RecruitmentSystem.DeploymentProvince(campaign, nation.Id, state.MapIndex) <= 0) continue;
            people = state.Population;
            best = state.MapIndex;
        }
        return best;
    }

    private static int CountField(Campaign campaign, Nation nation) =>
        campaign.Armies.Count(army => army.NationId == nation.Id && army.TemplateId != MilitiaCore.TemplateId &&
            army.ProvinceId > 0 && !army.InBattle);

    private static float Distance(Campaign campaign, int from, int to)
    {
        var a = campaign.ProvinceMap.CenterOf(from);
        var b = campaign.ProvinceMap.CenterOf(to);
        return NavalOperations.Distance(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
    }
}

