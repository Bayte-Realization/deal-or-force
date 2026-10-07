using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>What a province of a breakaway costs the state it broke from at a peace, against an ordinary one.</summary>
    public float PeaceCostReclaimFactor { get; set; } = 0.25f;
    /// <summary>The war score, or the share of the breakaway's land held securely, at which its old state writes the whole of it back in.</summary>
    public float ReclaimDecisiveScore { get; set; } = 40f;
    public float ReclaimSecuredShare { get; set; } = 0.5f;
    /// <summary>What a state winning against its own breakaway takes off any compromise it is offered, at full strength.</summary>
    public float ReclaimCompromisePenalty { get; set; } = 80f;
    /// <summary>The share of its land a breakaway can lose before it gives up, and the war support at which it gives up sooner.</summary>
    public float BreakawaySurrenderOccupied { get; set; } = 0.6f;
    public float BreakawaySurrenderSupport { get; set; } = 0.05f;
    /// <summary>The share of the old state's formations standing in a seceding region that go over to it when the state fights.</summary>
    public float SecessionDefectionShare { get; set; } = 0.45f;
    /// <summary>A de facto faction that has lost most of its ground fights on while it keeps this many formations and this much war support.</summary>
    public int FactionFightOnFormations { get; set; } = 2;
    public float FactionFightOnSupport { get; set; } = 0.2f;
    /// <summary>How long a faction's "seasoned insurgency" lasts from the first day.</summary>
    public int FactionSeasonedDays { get; set; } = 1460;
    /// <summary>How much of a faction's patron's strength a country thinking of attacking it counts against itself.</summary>
    public float FactionPatronDeterrence { get; set; } = 0.25f;
    /// <summary>What a war with a country whose only wars are with its own rebels is worth less to the computer: they are backed, not invaded.</summary>
    public float AiCivilWarRestraint { get; set; } = 0.6f;
    /// <summary>How much of a neighbour's weakness a government counts toward a war it has no reason it could give for.</summary>
    public float AiWeaknessWithoutCause { get; set; } = 0.3f;
    /// <summary>The share of a government's ground a faction claiming the state must hold, with the capital, for the government to fall.</summary>
    public float FactionTakeOverShare { get; set; } = 0.5f;
}

public sealed partial class Nation
{
    /// <summary>
    /// The state this one broke away from, by secession or a rising, or -1. What lets that state
    /// treat the war as one for its own ground: cheap to write back in at a peace, never given
    /// away for a white peace while it is winning, and taken back whole when the breakaway gives up.
    /// </summary>
    public int SecededFromId = -1;
}

/// <summary>
/// A state taking back the part of itself that left.
///
/// A secession war used to be priced like any other war. The government fought its way back
/// over three quarters of the breakaway - China over a hundred and eighty-seven of East
/// Turkestan's two hundred and fifty-four provinces, against a war support that had reached
/// nothing - and then, the day the opening lock on talks lifted, wrote a peace that handed
/// every one of them back and made East Turkestan a puppet, because annexing that much ground
/// cost more war score than it had. A breakaway that had been beaten flat offered a white peace
/// and was given one. And the breakaway hardly fought at all: four formations of militia
/// against the army that had garrisoned it the day before.
///
/// Now the ground a breakaway took is the old state's own to take back: a fraction of the price
/// at a peace, written back whole once the war is decided, never traded for a compromise while
/// the state is winning, and taken back entire when the breakaway surrenders - which it does
/// once most of it is held or nobody in it believes in the war. And part of the garrison that
/// stood in the region on the day goes over to it, so there is a war to fight.
/// </summary>
public static class Reintegration
{
    /// <summary>Whether this country broke away from that one.</summary>
    public static bool IsBreakawayOf(Nation breakaway, Nation government) =>
        breakaway is not null && government is not null && breakaway.Id != government.Id &&
        breakaway.SecededFromId == government.Id;

    /// <summary>Whether, in this war, the proposer is a government facing its own breakaway as the other side's leader.</summary>
    public static bool Reclaiming(Campaign c, StrategicWar war, int proposerId)
    {
        if (c is null || war is null || !war.Involves(proposerId)) return false;
        return IsBreakawayOf(c.NationById(war.EnemyLeaderOf(proposerId)), c.NationById(proposerId));
    }

    /// <summary>
    /// What a state winning against its breakaway knocks off a compromise. Nothing for a
    /// surrender, nothing for a written peace that hands ground back, and nothing for a state
    /// that is losing - that one is entitled to stop.
    /// </summary>
    public static float CompromisePenalty(Campaign c, StrategicWar war, int proposerId, PeaceTerms terms)
    {
        if (terms is PeaceTerms.Capitulate or PeaceTerms.EnforceDemands or PeaceTerms.EnforceWarGoal) return 0f;
        var responderId = war.EnemyLeaderOf(proposerId);
        if (!Reclaiming(c, war, responderId)) return 0f;
        var responderScore = war.IsAttacking(responderId) ? war.WarScore : -war.WarScore;
        if (responderScore <= 5f) return 0f;
        return c.Defines.ReclaimCompromisePenalty * M.Clamp01((responderScore - 5f) / 10f);
    }

    /// <summary>The breakaway's land that its old state holds, as a share of all of it.</summary>
    public static float HeldShare(Campaign c, Nation breakaway, Nation government)
    {
        var map = c?.ProvinceMap;
        if (map is null || c.ProvinceController is null || breakaway is null || government is null) return 0f;
        var owned = 0;
        var held = 0;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (map.ProvinceToNation[province] != breakaway.MapIndex || map.IsWater(province)) continue;
            owned++;
            if (c.ProvinceController[province] == government.MapIndex) held++;
        }
        return owned == 0 ? 1f : held / (float)owned;
    }

    /// <summary>Whether a breakaway that is losing its war should stop and surrender.</summary>
    public static bool ShouldSurrender(Campaign c, StrategicWar war, Nation breakaway)
    {
        if (c is null || war is null || breakaway is null || breakaway.IsHuman) return false;
        var government = c.NationById(war.EnemyLeaderOf(breakaway.Id));
        if (!IsBreakawayOf(breakaway, government)) return false;
        if (war.EnemyLeaderOf(government.Id) != breakaway.Id) return false;
        var defines = c.Defines;
        var held = HeldShare(c, breakaway, government);
        if (held >= defines.BreakawaySurrenderOccupied * 0.5f && Factions.FightsOn(c, breakaway, held)) return false;
        return held >= defines.BreakawaySurrenderOccupied ||
               breakaway.WarSupport <= defines.BreakawaySurrenderSupport && held >= defines.BreakawaySurrenderOccupied * 0.5f;
    }

    /// <summary>Every land province the country owns.</summary>
    public static List<int> Everything(Campaign c, Nation nation)
    {
        var result = new List<int>();
        var map = c?.ProvinceMap;
        if (map is null || nation is null) return result;
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToNation[province] == nation.MapIndex && !map.IsWater(province)) result.Add(province);
        return result;
    }

    /// <summary>
    /// The share of the old state's garrison in the seceding ground that goes over. Chosen by a
    /// die per formation drawn from the formation and the day, so it is the same world on
    /// every machine and does not move anybody else's rolls.
    /// </summary>
    public static int Defect(Campaign c, Nation parent, Nation born, IReadOnlyCollection<int> provinces)
    {
        if (c is null || parent is null || born is null || provinces is null || provinces.Count == 0) return 0;
        var share = M.Clamp01(c.Defines.SecessionDefectionShare);
        if (share <= 0f) return 0;
        var ground = provinces as HashSet<int> ?? new HashSet<int>(provinces);
        var moved = 0;
        foreach (var army in c.Armies)
        {
            if (army.NationId != parent.Id || army.EmbarkedFleetId >= 0 || army.InBattle ||
                !ground.Contains(army.ProvinceId)) continue;
            if (Roll(c.Seed, army.Id, born.Id, c.ElapsedDays) >= share) continue;
            army.NationId = born.Id;
            army.AssignedWarId = -1;
            army.ArmyGroupId = -1;
            army.FrontLineId = -1;
            army.OrderKind = ArmyOrder.Manual;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.DirectOrder = false;
            army.Cohesion = MathF.Min(army.Cohesion, 0.6f);
            moved++;
        }
        return moved;
    }

    private static float Roll(int seed, int a, int b, int day)
    {
        unchecked
        {
            var hash = (uint)seed * 2654435761u ^ (uint)a * 2246822519u ^ (uint)b * 3266489917u ^ (uint)day * 668265263u;
            hash ^= hash >> 15; hash *= 2246822519u;
            hash ^= hash >> 13; hash *= 3266489917u;
            hash ^= hash >> 16;
            return (hash & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
