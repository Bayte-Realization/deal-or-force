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

    /// <summary>
    /// Below this stability a government's army is hollow: in a civil war its formations on the
    /// line go over to a rising that is winning even when they are not worn out (Defections).
    /// </summary>
    public float HollowArmyStability { get; set; } = 0.35f;

    /// <summary>The rising's war score at which a hollow army's defection odds reach their full value.</summary>
    public float HollowArmyMomentumScore { get; set; } = 30f;
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
        var moved = 0;
        foreach (var chunk in TickStages(campaign, dayOverride)) moved += chunk;
        return moved;
    }

    /// <summary>The same monthly pass, yielded between batches of armies; each yield carries the
    /// defections counted so far in that stretch so the total survives the split.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign, int? dayOverride = null)
    {
        var day = dayOverride ?? campaign?.ElapsedDays ?? 0;
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null ||
            day <= 0 || day % 30 != 0) yield break;
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var moved = 0;
        var touchedWars = new HashSet<int>();
        var loggedReunion = new HashSet<(int EnemyId, int NationId)>();
        var activeBattleProvinces = new HashSet<int>();
        foreach (var battle in campaign.Battles)
            if (battle is not null && !battle.Ended && battle.ProvinceId > 0)
                activeBattleProvinces.Add(battle.ProvinceId);
        var scanned = 0;
        foreach (var army in campaign.Armies.ToArray())
        {
            if (++scanned % 32 == 0) { yield return moved; moved = 0; }
            if (army.NationId < 0 || army.InBattle || army.EmbarkedFleetId >= 0 ||
                army.Divisions < 0.1f || army.ProvinceId <= 0 || army.ProvinceId > topology.ProvinceCount)
                continue;
            var nation = campaign.NationById(army.NationId);
            if (nation is null || nation.Population <= 0) continue;
            var exhausted = army.Cohesion <= campaign.Defines.DefectionCohesion &&
                            army.SupplyLevel <= campaign.Defines.DefectionSupply &&
                            army.Strength <= campaign.Defines.DefectionStrength;
            // A hollow army: a government nobody believes in, whose soldiers on the line watch a
            // rising win. Kabul in 2021, Damascus in December 2024, Goma in January 2025 - armies
            // many times the size of what came at them, which did not fight. Only in a civil war
            // or a reunification war, only near the enemy, and only while the challenger is ahead
            // or the regime is strained.
            var hollow = !exhausted && nation.Stability < campaign.Defines.HollowArmyStability;
            var strainedForReunification = !exhausted && !hollow &&
                (nation.Stability < 0.62f || nation.WarSupport < 0.58f ||
                 ModifierSystem.Has(nation, "mainland_regime_shock"));
            if (!exhausted && !hollow && !strainedForReunification) continue;

            StrategicWar chosen = null;
            Nation enemy = null;
            var civil = false;
            var reunification = false;
            foreach (var war in Warfare.OpenWars(campaign))
            {
                if (war.Ended || !war.Involves(nation.Id)) continue;
                foreach (var enemyId in war.EnemiesOf(nation.Id).OrderBy(id => id))
                {
                    var candidate = campaign.NationById(enemyId);
                    if (candidate is null || candidate.Id == nation.Id) continue;
                    var rebel = war.CivilConflict || WarAims.IsRebel(candidate) ||
                                candidate.SecededFromId == nation.Id || nation.SecededFromId == candidate.Id;
                    var reunion = !rebel && campaign.Defines.MainlandReclamationEnabled &&
                                  MainlandReclamation.IsMainlandLiberator(campaign, war, candidate, nation);
                    var holdsHere = controllers[army.ProvinceId] == candidate.MapIndex;
                    if (!rebel && !reunion && !holdsHere) continue;
                    chosen = war;
                    enemy = candidate;
                    civil = rebel;
                    reunification = reunion;
                    break;
                }
                if (chosen is not null) break;
            }
            if (chosen is null || enemy is null) continue;
            if (strainedForReunification && !reunification) continue;

            var ownGround = topology.ProvinceToNation[army.ProvinceId] == nation.MapIndex;
            var enemyGround = topology.ProvinceToNation[army.ProvinceId] == enemy.MapIndex;
            if (!civil && !reunification)
            {
                if (nation.WarSupport > campaign.Defines.DefectionWarSupport) continue;
                if (controllers[army.ProvinceId] != enemy.MapIndex) continue;
            }
            else if (!ownGround && !enemyGround && controllers[army.ProvinceId] != enemy.MapIndex)
                continue;

            var odds = civil ? campaign.Defines.WartimeDefectionChanceCivil
                : reunification ? MathF.Max(campaign.Defines.WartimeDefectionChanceInterstate, campaign.Defines.MainlandDefectionChanceBonus)
                : campaign.Defines.WartimeDefectionChanceInterstate;
            if (!exhausted)
            {
                if (!civil && !reunification) continue;
                var rebelScore = chosen.IsAttacking(enemy.Id) ? chosen.WarScore : -chosen.WarScore;
                var nearEnemy = controllers[army.ProvinceId] == enemy.MapIndex;
                if (!nearEnemy)
                {
                    // Reunification frontline defections require true dry-land adjacency (never
                    // across a ferry edge like kinmen_ferry before an amphibious landing occurs).
                    var adjacent = reunification ? topology.LandNeighbors[army.ProvinceId] : topology.Neighbors[army.ProvinceId];
                    foreach (var next in adjacent)
                        if (next > 0 && next < controllers.Length && controllers[next] == enemy.MapIndex) { nearEnemy = true; break; }
                }
                if (!nearEnemy) continue;
                if (civil)
                {
                    if (rebelScore <= 0f) continue;
                    var weakness = 1f - nation.Stability / MathF.Max(0.01f, campaign.Defines.HollowArmyStability);
                    odds *= M.Clamp01(weakness) * M.Clamp01(rebelScore / MathF.Max(1f, campaign.Defines.HollowArmyMomentumScore));
                }
                else
                {
                    // Reunification frontline defection: requires the liberator to have actually
                    // established a mainland bridgehead in this war first.
                    var shocked = ModifierSystem.Has(nation, "mainland_regime_shock");
                    if (!MainlandReclamation.HasBridgehead(enemy, chosen.Id) && !shocked) continue;
                    if (rebelScore < -10f && !shocked) continue;
                    var underground = ModifierSystem.Has(enemy, "twn_mainland_underground_spirit") ? 1.35f : 1f;
                    var momentum = ModifierSystem.Has(enemy, "mainland_return_momentum") ? 1.25f : 1f;
                    var strain = M.Clamp(
                        (shocked ? 0.40f : 0.15f) +
                        M.Clamp01((0.65f - nation.Stability) / 0.45f) * 0.45f +
                        M.Clamp01((0.60f - nation.WarSupport) / 0.45f) * 0.35f +
                        M.Clamp01((rebelScore + 10f) / 40f) * 0.30f,
                        0.15f, 1.35f);
                    odds = MathF.Min(0.26f, odds * strain * underground * momentum);
                    if (nation.IsHuman) odds *= 0.50f;
                }
            }
            if (odds <= 0f || Roll(campaign.Seed, army.Id, enemy.Id, day / 30) >= odds)
                continue;

            army.NationId = enemy.Id;
            army.AssignedWarId = chosen.Id;
            army.ArmyGroupId = -1;
            army.FrontLineId = -1;
            army.OrderKind = ArmyOrder.Manual;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.DirectOrder = false;
            army.Cohesion = M.Clamp01(army.Cohesion - 0.12f);
            // If this garrison defected in a reunification war and no active battle or loyal
            // defenders remain in the province, it hands the province over immediately.
            if (reunification && controllers[army.ProvinceId] == nation.MapIndex &&
                !activeBattleProvinces.Contains(army.ProvinceId))
            {
                var loyalLeft = false;
                foreach (var other in campaign.Armies)
                    if (other.Id != army.Id && other.ProvinceId == army.ProvinceId &&
                        other.EmbarkedFleetId < 0 && other.Strength > 0.05f &&
                        chosen.Opposes(enemy.Id, other.NationId))
                    { loyalLeft = true; break; }
                if (!loyalLeft)
                {
                    chosen.OriginalController.TryAdd(army.ProvinceId, controllers[army.ProvinceId]);
                    campaign.SetProvinceController(army.ProvinceId, (ushort)enemy.MapIndex);
                    chosen.FrontDirty = true;
                }
            }
            touchedWars.Add(chosen.Id);
            moved++;
            if (reunification && loggedReunion.Add((enemy.Id, nation.Id)))
            {
                var regionName = campaign.StateByMapIndex(topology.ProvinceToState[army.ProvinceId])?.Name ?? "";
                campaign.Chronicle.Record(campaign, "mainland_defection", enemy.Id, nation.Id, subject: regionName);
            }
            DiagnosticLogs.Write("simulation",
                $"DEFECTION army={army.Id} from={nation.Tag} to={enemy.Tag} war={chosen.Id} " +
                $"civil={civil} province={army.ProvinceId} cohesion={army.Cohesion:0.00} supply={army.SupplyLevel:0.00}");
        }
        yield return 0;
        if (touchedWars.Count > 0) Warfare.MarkIndexStale(campaign);
        foreach (var warId in touchedWars)
            if (campaign.Wars.Find(war => war.Id == warId) is { } war)
                FrontPlanner.ReassignAi(campaign, war);
        yield return moved;
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
