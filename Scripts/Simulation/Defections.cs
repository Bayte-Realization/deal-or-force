using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Monthly chance per eligible formation to cross to a rising or a civil-war enemy.</summary>
    public float WartimeDefectionChanceCivil { get; set; } = 0.12f;

    /// <summary>Monthly chance per eligible formation in an ordinary interstate war.</summary>
    public float WartimeDefectionChanceInterstate { get; set; } = 0.015f;

    /// <summary>A formation above this cohesion still has enough of a unit to hold together.</summary>
    public float DefectionCohesion { get; set; } = 0.30f;

    /// <summary>A formation above this supply is not desperate enough to leave.</summary>
    public float DefectionSupply { get; set; } = 0.35f;

    /// <summary>An ordinary war only sees desertion once the country's war support is below this.</summary>
    public float DefectionWarSupport { get; set; } = 0.25f;

    /// <summary>A formation stronger than this has not been reduced enough to consider changing sides.</summary>
    public float DefectionStrength { get; set; } = 0.60f;

    /// <summary>The share of a peaceful breakaway's garrison that goes with the new country.</summary>
    public float PeacefulSecessionDefectionShare { get; set; } = 0.35f;
}

/// <summary>
/// Troops changing sides.
///
/// A war is not only armies moving on a map: a formation that has been cut off, has lost its
/// cohesion, or is standing among its own people while its government fights them can cease to
/// be that government's formation. This is the same operation as the opening defection in a
/// secession, but it happens during the war as well.
///
/// The pass is monthly and deterministic. It never creates men or equipment: the formation
/// keeps its people and simply answers to another flag. A civil war or a rising is the normal
/// case; an interstate war only sees it where the enemy already holds the ground and the
/// country's war support has collapsed. A formation in battle or at sea is never moved.
/// </summary>
public static class Defections
{
    public static int Tick(Campaign campaign, int? dayOverride = null)
    {
        var day = dayOverride ?? campaign?.ElapsedDays ?? 0;
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null ||
            day <= 0 || day % 30 != 0) return 0;
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var moved = 0;
        var touchedWars = new HashSet<int>();
        foreach (var army in campaign.Armies.ToArray())
        {
            if (army.NationId < 0 || army.InBattle || army.EmbarkedFleetId >= 0 ||
                army.Divisions < 0.1f || army.ProvinceId <= 0 || army.ProvinceId > topology.ProvinceCount)
                continue;
            var nation = campaign.NationById(army.NationId);
            if (nation is null || nation.Population <= 0) continue;
            if (army.Cohesion > campaign.Defines.DefectionCohesion ||
                army.SupplyLevel > campaign.Defines.DefectionSupply ||
                army.Strength > campaign.Defines.DefectionStrength) continue;

            StrategicWar chosen = null;
            Nation enemy = null;
            var civil = false;
            foreach (var war in campaign.Wars)
            {
                if (war.Ended || !war.Involves(nation.Id)) continue;
                foreach (var enemyId in war.EnemiesOf(nation.Id).OrderBy(id => id))
                {
                    var candidate = campaign.NationById(enemyId);
                    if (candidate is null || candidate.Id == nation.Id) continue;
                    var rebel = war.CivilConflict || WarAims.IsRebel(candidate) ||
                                candidate.SecededFromId == nation.Id || nation.SecededFromId == candidate.Id;
                    var holdsHere = controllers[army.ProvinceId] == candidate.MapIndex;
                    if (!rebel && !holdsHere) continue;
                    chosen = war;
                    enemy = candidate;
                    civil = rebel;
                    break;
                }
                if (chosen is not null) break;
            }
            if (chosen is null || enemy is null) continue;

            var ownGround = topology.ProvinceToNation[army.ProvinceId] == nation.MapIndex;
            var enemyGround = topology.ProvinceToNation[army.ProvinceId] == enemy.MapIndex;
            if (!civil)
            {
                if (nation.WarSupport > campaign.Defines.DefectionWarSupport) continue;
                if (controllers[army.ProvinceId] != enemy.MapIndex) continue;
            }
            else if (!ownGround && !enemyGround && controllers[army.ProvinceId] != enemy.MapIndex)
                continue;

            var odds = civil ? campaign.Defines.WartimeDefectionChanceCivil
                : campaign.Defines.WartimeDefectionChanceInterstate;
            if (odds <= 0f || Roll(campaign.Seed, army.Id, enemy.Id, day / 30) >= odds)
                continue;

            army.NationId = enemy.Id;
            army.AssignedWarId = -1;
            army.ArmyGroupId = -1;
            army.FrontLineId = -1;
            army.OrderKind = ArmyOrder.Manual;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.DirectOrder = false;
            army.Cohesion = M.Clamp01(army.Cohesion - 0.12f);
            touchedWars.Add(chosen.Id);
            moved++;
            DiagnosticLogs.Write("simulation",
                $"DEFECTION army={army.Id} from={nation.Tag} to={enemy.Tag} war={chosen.Id} " +
                $"civil={civil} province={army.ProvinceId} cohesion={army.Cohesion:0.00} supply={army.SupplyLevel:0.00}");
        }
        foreach (var warId in touchedWars)
            if (campaign.Wars.Find(war => war.Id == warId) is { } war)
                FrontPlanner.ReassignAi(campaign, war);
        return moved;
    }

    /// <summary>The same stable die the opening secession uses, kept local so a test can repeat it.</summary>
    private static float Roll(int seed, int a, int b, int day)
    {
        unchecked
        {
            var hash = (uint)seed * 2654435761u ^ (uint)a * 2246822519u ^ (uint)b * 3266489917u ^
                       (uint)day * 668265263u;
            hash ^= hash >> 15; hash *= 2246822519u;
            hash ^= hash >> 13; hash *= 3266489917u;
            hash ^= hash >> 16;
            return (hash & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
