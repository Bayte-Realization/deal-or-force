using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Let a ready autonomous advance use a visible, unopposed opening
    /// without waiting for the preparation needed by a defended assault.</summary>
    public bool AiStartsUnopposedAdvance { get; set; } = true;
}

// Target selection and local cover share the same indexed army view as combat.
// Scratch is reused by the single-threaded simulation; no campaign state lives here.
public static partial class Warfare
{
    private static readonly List<int> Candidates = new();
    private static readonly List<int> Neighbours = new();
    private static readonly HashSet<int> CoverSeen = new();
    private static readonly List<int> CoverRing = new();
    private static readonly List<int> CoverNext = new();
    private static readonly HashSet<ushort> OpeningEnemyMaps = new();

    internal static bool CanStartOpenAdvance(Campaign c, StrategicWar war, Army army)
    {
        if (c?.ProvinceMap is null || c.ProvinceController is null || army is null || war is null || war.Ended ||
            !c.Defines.AiStartsUnopposedAdvance || !war.Involves(army.NationId) ||
            army.OrderKind is not (ArmyOrder.AdvanceFront or ArmyOrder.Spearhead) ||
            army.DirectOrder || army.SupportOnly || army.InBattle || army.IsMoving || army.EmbarkedFleetId >= 0 ||
            army.ProvinceId <= 0 || army.ProvinceId > c.ProvinceMap.ProvinceCount) return false;
        // Assess the proposed active plan with the same value-only F# rule as a
        // real assault. This neither unpauses it nor duplicates strict/disabled
        // floors, nonfinite handling and recovery semantics in another predicate.
        var readiness = Rules.AssaultRules.Assess(true, true, true, true, false,
            army.ReadyDay, c.ElapsedDays, army.Organisation, army.SupplyLevel,
            c.Defines.AutonomousAssaultMinimumOrganisation, c.Defines.AutonomousAssaultMinimumSupply);
        if (!readiness.Ready) return false;
        EnsureIndexed(c);
        OpeningEnemyMaps.Clear();
        WarCoalition.FillControllers(c, war, !war.IsAttacking(army.NationId), OpeningEnemyMaps);
        // Ask the same selector and cover rule the advance will actually use, not
        // a second optimistic forecast. No arbitrary enemy targets or hidden armies.
        var target = ChooseAssaultTarget(c, c.ProvinceMap, war, army, c.ProvinceController, OpeningEnemyMaps);
        if (target <= 0 || !CampaignIntel.CanSeeProvince(c, army.NationId, target)) return false;
        foreach (var defender in InProvince(target))
            if (war.Opposes(army.NationId, defender.NationId) && defender.Divisions >= .1f) return false;
        return !CoverRefuses(c, c.ProvinceMap, war, army, target);
    }

    private static int ChooseAssaultTarget(Campaign campaign, ProvinceTopology topology,
        StrategicWar war, Army army, ushort[] controllers, HashSet<ushort> enemyMaps)
    {
        Candidates.Clear();
        foreach (var province in topology.Neighbors[army.ProvinceId])
            if (enemyMaps.Contains(controllers[province])) Candidates.Add(province);
        if (Candidates.Count == 0) return -1;
        Neighbours.Clear();
        Neighbours.AddRange(Candidates);
        var pinned = 0;
        if (army.PlannedRoute.Count > 0 && Candidates.Contains(army.PlannedRoute[0]))
        {
            var nextPlannedProvince = army.PlannedRoute[0];
            Candidates.Clear();
            Candidates.Add(nextPlannedProvince);
            pinned = nextPlannedProvince;
        }
        if (army.OrderProvinceId > 0 && Candidates.Contains(army.OrderProvinceId))
        {
            Candidates.Clear();
            Candidates.Add(army.OrderProvinceId);
            pinned = army.OrderProvinceId;
        }
        if (army.OrderKind == ArmyOrder.Spearhead && army.ObjectiveStateMapIndex >= 0)
        {
            // Compacted in place, by index: writing through a list's indexer counts as
            // modifying it, so a foreach over the same list would throw.
            var kept = 0;
            for (var index = 0; index < Candidates.Count; index++)
            {
                var province = Candidates[index];
                if (topology.ProvinceToState[province] == army.ObjectiveStateMapIndex)
                    Candidates[kept++] = province;
            }
            if (kept > 0) Candidates.RemoveRange(kept, Candidates.Count - kept);
        }
        // A pin that the country cannot take today must not fence the army inside itself.
        // An advance whose objective was refused for cover used to keep that objective as
        // its one candidate for good: the pin was re-chosen, refused again, and the sectors
        // beside it were never re-read - the confirmed shape of the advance deadlock
        // (COD-C01: objective 280 pinned, covered, re-picked, while 317/329 went unchecked).
        // So a pin that has stood for at least an order-settle interval is set aside for this
        // pass and every sector is scored again; the pin comes back on its own the day its
        // cover lifts, because the narrowing that made it is still here. A pin issued today -
        // by a player's RMB or a fresh plan - keeps the old strictness: the order is new, and
        // the measure that produced the cover rule was of standing fronts, not fresh aims.
        if (pinned > 0 && Candidates.Count == 1 && Candidates[0] == pinned && !army.DirectOrder &&
            campaign.ElapsedDays - army.OrderIssuedDay >= campaign.Defines.OrderSettleDays &&
            CoverRefuses(campaign, topology, war, army, pinned))
        {
            Candidates.Clear();
            foreach (var province in Neighbours)
            {
                if (province == pinned) continue;
                if (army.OrderKind == ArmyOrder.Spearhead && army.ObjectiveStateMapIndex >= 0 &&
                    topology.ProvinceToState[province] != army.ObjectiveStateMapIndex) continue;
                Candidates.Add(province);
            }
            if (Candidates.Count == 0) Candidates.Add(pinned);
        }
        // The front AI probes weak sectors and prefers dry crossings. Spearheads
        // accept more terrain risk, while regular advances strongly avoid
        // needlessly attacking across a defended river line. Picking the best of a
        // handful of neighbours by hand rather than by OrderBy().First() is the same
        // choice - lowest score, ties to the lower province id - without the sorter,
        // the key buffer and the enumerator that went with it every single time.
        var terrainWeight = army.OrderKind == ArmyOrder.Spearhead ? 0.38f : 1f;
        var target = -1;
        var bestScore = 0f;
        foreach (var province in Candidates)
        {
            // Player battle plans and AI orders use the same reconnaissance. Deep
            // hidden formations must not influence which sector the planner probes.
            var observedResistance = 0f;
            foreach (var defender in InProvince(province))
                if (war.Opposes(army.NationId, defender.NationId) && CampaignIntel.CanSeeArmy(campaign, army.NationId, defender))
                    observedResistance += defender.Power;
            // Weighted, not divided. Dividing the enemy's strength by your own makes
            // resistance dominate the score by an order of magnitude over everything
            // else, so every formation on the front picks the single weakest sector and
            // the front concentrates itself into a breakthrough. The weight keeps the
            // preference a preference: probe the weak spot, but not across a river.
            var score = topology.RiverCrossingStrength(army.ProvinceId, province) * terrainWeight +
                observedResistance * 0.018f;
            if (target >= 0 && (score > bestScore || (score == bestScore && province > target))) continue;
            bestScore = score;
            target = province;
        }
        return target;
    }

    /// <summary>
    /// The one refusal an autonomous advance meets: a province empty of defenders but so
    /// outmatched by the enemy within reach that walking in is dying in a gap, not probing
    /// one. Selection asks it of a pinned target before letting the pin fence the army in;
    /// <see cref="CombatCore"/> asks it again of whichever target was chosen, and stays the
    /// authority - this is the same rule read twice, not a second one. A direct order is
    /// refused by neither.
    /// </summary>
    private static bool CoverRefuses(Campaign campaign, ProvinceTopology topology,
        StrategicWar war, Army army, int target)
    {
        if (army.DirectOrder) return false;
        foreach (var other in InProvince(target))
            if (war.Opposes(army.NationId, other.NationId) && other.Divisions >= 0.1f) return false;
        var coveringPower = 0f;
        var covering = 0f;
        Cover(campaign, topology, target, war, army.NationId, ref coveringPower, ref covering);
        return coveringPower > PushingOn(campaign, topology, war, army, target) * campaign.Defines.GapExploitAdvantage;
    }

    /// <summary>
    /// The force pushing on a gap: every ready advancing division of the country standing beside
    /// it, not the one division asking. Cover is a wall to a force smaller than what covers the
    /// gap - and the force was taken to be each division alone, so a whole front of them stood
    /// in front of a thinly covered line indefinitely, every one of them outmatched on its own:
    /// thirty-two Colombian divisions spent two years at their posts on the Panamanian border
    /// against one. Counting them together halved the advancing divisions that stood at their
    /// posts all war (343 to 159 over sixteen seeds) and did not move the Ukrainian front
    /// (24 seeds, z -0.2): a real line is covered by more than the divisions facing each gap.
    /// </summary>
    private static float PushingOn(Campaign campaign, ProvinceTopology topology, StrategicWar war, Army army, int target)
    {
        var power = 0f;
        foreach (var province in topology.Neighbors[target])
            foreach (var other in InProvince(province))
            {
                if (!WarCoalition.SameSide(war, other.NationId, army.NationId) || other.InBattle || other.IsMoving) continue;
                if (other.ReadyDay > campaign.ElapsedDays) continue;
                if (other.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead && other.PlanActive)
                    power += other.Power;
                else if (other.OrderKind is ArmyOrder.DefendFront)
                    power += other.Power * 0.5f;
            }
        return MathF.Max(power, army.Power);
    }

    /// <summary>
    /// Weigh everything the enemy has within reach of one province. Formations touching it
    /// count in full; each further ring counts for less, out to the radius a division is
    /// taken to hold. The tally is fractional because a division two provinces away is a
    /// fraction of a division's worth of resistance, not none and not all of it.
    /// </summary>
    private static void Cover(Campaign campaign, ProvinceTopology topology,
        int target, StrategicWar war, int advancingNationId, ref float coveringPower, ref float covering)
    {
        var radius = Math.Max(1, campaign.Defines.FrontCoverageRadius);
        var falloff = M.Clamp01(campaign.Defines.FrontCoverageFalloff);
        var seen = CoverSeen;
        var ring = CoverRing;
        var next = CoverNext;
        seen.Clear();
        seen.Add(target);
        ring.Clear();
        ring.Add(target);
        next.Clear();
        var weight = 1f;
        for (var hop = 0; hop < radius; hop++)
        {
            next.Clear();
            foreach (var province in ring)
            foreach (var neighbour in topology.Neighbors[province])
            {
                if (!seen.Add(neighbour)) continue;
                next.Add(neighbour);
                foreach (var other in InProvince(neighbour))
                {
                    if (!war.Opposes(advancingNationId, other.NationId) || other.Divisions < 0.1f) continue;
                    coveringPower += M.Max(other.Power,
                        other.Presence * campaign.Defines.CoveringPresenceFloor) * weight;
                    covering += weight;
                }
            }
            (ring, next) = (next, ring);
            weight *= falloff;
        }
    }

}
