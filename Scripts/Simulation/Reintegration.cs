using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Unconditional surrender returns the winner's unrenounced homeland,
    /// even when a successor movement broke from an intervening country.</summary>
    public bool PeaceCapitulationRecoversHomeland { get; set; } = true;
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
    /// <summary>What a kindred or reunification province costs at a peace, against an ordinary foreign one.</summary>
    public float PeaceCostReunificationFactor { get; set; } = 0.30f;
    /// <summary>How many regions a defeated neighbour may have before an ordinary conqueror prefers subjugation unless it holds almost all of it or has expansionist aims.</summary>
    public int AiConquestFullAnnexationMaxRegions { get; set; } = 12;
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
/// A state taking back the part of itself that left, a civil-war contender taking the whole
/// country, a nation reuniting its people, or a decisive conqueror swallowing a beaten rival.
/// </summary>
public static class Reintegration
{
    /// <summary>Whether this country broke away from that one.</summary>
    public static bool IsBreakawayOf(Nation breakaway, Nation government)
    {
        if (breakaway is null || government is null || breakaway.Id == government.Id) return false;
        if (breakaway.SecededFromId == government.Id) return true;
        if (breakaway.Name.StartsWith("rising:", StringComparison.Ordinal))
        {
            var parts = breakaway.Name.Split(':');
            if (parts.Length >= 3 && string.Equals(parts[2], government.Tag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (!string.IsNullOrEmpty(breakaway.FoundedInRevolt) &&
            (breakaway.Tag.StartsWith(government.Tag, StringComparison.OrdinalIgnoreCase) ||
             (government.Tag == "UKR" && breakaway.Tag.StartsWith("U")) ||
             (government.Tag == "IND" && breakaway.Tag.StartsWith("1"))))
            return true;
        return false;
    }

    /// <summary>
    /// Whether a civil-war challenger (a state-claiming faction, an armed uprising, a revolutionary
    /// front, or a co-national rival regime) aims to take over the entire parent state rather than
    /// secede with a minority border province.
    /// </summary>
    public static bool ClaimsParentState(Campaign c, StrategicWar war, Nation challenger, Nation parent)
    {
        if (c is null || challenger is null || parent is null || challenger.Id == parent.Id) return false;
        var related = IsBreakawayOf(challenger, parent) ||
                      (war is { CivilConflict: true } && challenger.SecededFromId >= 0 &&
                       challenger.SecededFromId == parent.SecededFromId);
        if (!related && (war is null || !war.CivilConflict || !war.Opposes(challenger.Id, parent.Id)))
            return false;

        if (Factions.Of(challenger) is { ClaimsState: true }) return true;
        if (challenger.Name.StartsWith("rising:", StringComparison.Ordinal)) return true;
        if (war is not null && (WarAims.Of(c, war, challenger) == WarAimKind.Revolutionary ||
            war.Justification is "uprising" or "revolution" or "revolutionary" or "social_revolution" or "civil_war"))
            return true;

        // If the challenger has an explicit narrow regional homeland that does not cover the
        // parent's heartland and its people are a minority across the parent, it is a regional
        // separatist (e.g. a border secession), not a contender for the whole state.
        var parentStates = c.StatesOf(parent);
        if (parentStates.Length == 0) return false;
        var cultures = WarAims.Cultures(challenger);
        if (c.Homelands.TryGetValue(challenger.Id, out var homes) && homes is { Count: > 0 })
        {
            var coversParent = parentStates.All(s => homes.Contains(s.MapIndex) ||
                (cultures.Count > 0 && WarAims.CultureShare(s, cultures) >= 0.40f));
            if (!coversParent) return false;
        }

        return SharesMajorityCulture(c, challenger, parentStates, cultures);
    }

    /// <summary>Whether either belligerent in a civil war is fighting to annex the other whole.</summary>
    public static bool ClaimsStateInCivilWar(Campaign c, StrategicWar war, Nation winner, Nation loser) =>
        IsBreakawayOf(loser, winner) || ClaimsParentState(c, war, winner, loser);

    /// <summary>Whether, in this war, the proposer is fighting to reunify its own country against a breakaway or rival regime.</summary>
    public static bool Reclaiming(Campaign c, StrategicWar war, int proposerId)
    {
        if (c is null || war is null || !war.Involves(proposerId)) return false;
        var proposer = c.NationById(proposerId);
        var enemy = c.NationById(war.EnemyLeaderOf(proposerId));
        return ClaimsStateInCivilWar(c, war, proposer, enemy);
    }

    /// <summary>Whether a region belongs to the same people, culture group, homeland or unification project as the winner.</summary>
    public static bool IsKindredRegion(Campaign c, Nation winner, ScenarioState region)
    {
        if (c is null || winner is null || region is null || region.Population <= 0) return false;
        if (TerritorialClaims.TitleOf(c, winner, region) == TerritorialTitle.Homeland ||
            region.CoreTags.Contains(winner.Tag)) return true;
        if (AiStatecraft.ConsolidationAim(c, winner, region)) return true;
        var cultures = WarAims.Cultures(winner);
        if (cultures.Count == 0) return false;
        if (WarAims.CultureShare(region, cultures) >= 0.40f) return true;
        return CultureGroupShare(region, cultures) >= 0.55f;
    }

    /// <summary>
    /// Whether a war is a war of national reunification / unification of peoples, where the winner
    /// seeks to unite a kindred state into one country.
    /// </summary>
    public static bool IsReunificationTarget(Campaign c, StrategicWar war, Nation winner, Nation loser)
    {
        if (c is null || war is null || war.Limited || winner is null || loser is null || winner.Id == loser.Id)
            return false;
        if (ClaimsStateInCivilWar(c, war, winner, loser)) return true;
        if (WarAims.IsRebel(winner) && !ClaimsParentState(c, war, winner, loser)) return false;

        var loserStates = c.StatesOf(loser);
        if (loserStates.Length == 0) return false;

        // Authored multi-tag formable unification (e.g. Korea, Romania-Moldova, etc.)
        if (loserStates.Any(s => AiStatecraft.ConsolidationAim(c, winner, s))) return true;

        var winnerCultures = WarAims.Cultures(winner);
        var loserCultures = WarAims.Cultures(loser);
        var sharesPrimary = winnerCultures.Count > 0 && loserCultures.Count > 0 &&
                            winnerCultures.Overlaps(loserCultures);
        if (sharesPrimary) return true;

        long totalPop = 0, kindredPop = 0;
        var kindredRegions = 0;
        foreach (var state in loserStates)
        {
            var pop = Math.Max(0L, state.Population);
            totalPop += pop;
            if (IsKindredRegion(c, winner, state))
            {
                kindredPop += pop;
                kindredRegions++;
            }
        }
        if (totalPop <= 0) return false;
        var popShare = kindredPop / (float)totalPop;
        var regionShare = kindredRegions / (float)loserStates.Length;
        if (war.Justification is "irredentism" or "reconquest" or "unification" or "reunification")
            return popShare >= 0.45f || regionShare >= 0.50f;
        return popShare >= 0.65f && regionShare >= 0.65f;
    }

    /// <summary>Whether this regime or war has an ambitious conquest drive that prefers annexation over leaving a rump state or puppet.</summary>
    public static bool IsExpansionist(Campaign c, Nation winner, StrategicWar war)
    {
        if (winner is null) return false;
        if (war?.Justification is "conquest" or "imperialism" or "irredentism" or "reconquest" or "holy_war" or "total_war")
            return true;
        if (winner.Ideology is "nationalism" or "communism" or "theocratic_rule" or "fascism")
            return true;
        if (winner.Government is "military_junta" or "absolute_monarchy" or "one_party_state" or "theocracy")
            return true;
        if (c is not null && Rulers.HeadOfState(c, winner).TraitId is "hawk" or "veteran_commander" or "ideologue")
            return true;
        return false;
    }

    /// <summary>
    /// Whether a winning belligerent has both the military control and the political desire to
    /// annex the defeated opponent outright.
    /// </summary>
    public static bool WantsFullConquestAnnexation(Campaign c, StrategicWar war, Nation winner, Nation loser,
        float score, float securedShare, float heldShare)
    {
        if (c is null || war is null || winner is null || loser is null || loser.IsSubject) return false;
        if (war.Justification == "dynastic_claim") return false;

        if (ClaimsStateInCivilWar(c, war, winner, loser))
            return score >= c.Defines.ReclaimDecisiveScore ||
                   securedShare >= c.Defines.ReclaimSecuredShare ||
                   heldShare >= 0.65f ||
                   AiStatecraft.SideOverrun(c, war, war.IsAttacking(loser.Id));

        if (WarAims.IsRebel(winner)) return false;
        if (!PeaceShape.Near(c, winner, loser)) return false;

        if (IsReunificationTarget(c, war, winner, loser))
            return score >= c.Defines.ReclaimDecisiveScore ||
                   securedShare >= c.Defines.ReclaimSecuredShare ||
                   heldShare >= 0.65f ||
                   AiStatecraft.SideOverrun(c, war, war.IsAttacking(loser.Id));

        if (war.Limited) return false;

        var overrun = AiStatecraft.SideOverrun(c, war, war.IsAttacking(loser.Id));
        var militaryMastery = overrun ||
            (score >= c.Defines.AiPeaceDecisiveScore &&
             (securedShare >= c.Defines.AiPeaceDecisiveOccupationShare || heldShare >= 0.75f)) ||
            (score >= 60f && heldShare >= 0.85f);
        if (!militaryMastery) return false;

        var loserRegions = c.StatesOf(loser).Length;
        return overrun ||
               heldShare >= 0.80f ||
               securedShare >= 0.75f ||
               loserRegions <= c.Defines.AiConquestFullAnnexationMaxRegions ||
               IsExpansionist(c, winner, war);
    }

    /// <summary>
    /// Whether a written peace demand that takes all of the loser's provinces is a valid decisive
    /// annexation whose total province cost should be capped to the war's earned budget.
    /// </summary>
    public static bool CanAnnexWhole(Campaign c, StrategicWar war, Nation winner, Nation loser)
    {
        var map = c?.ProvinceMap;
        if (map is null || c.ProvinceController is null || war is null || winner is null || loser is null)
            return false;
        var score = war.IsAttacking(winner.Id) ? war.WarScore : -war.WarScore;
        var owned = 0;
        var held = 0;
        var secured = 0;
        var secureThreshold = c.Defines.OccupationSecureThreshold;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (map.ProvinceToNation[p] != loser.MapIndex || map.IsWater(p)) continue;
            owned++;
            if (c.ProvinceController[p] == winner.MapIndex)
            {
                held++;
                if (c.ProvinceOccupation is not null && c.ProvinceOccupation[p] >= secureThreshold)
                    secured++;
            }
        }
        if (owned == 0) return false;
        var heldShare = held / (float)owned;
        var securedShare = secured / (float)owned;
        return WantsFullConquestAnnexation(c, war, winner, loser, score, securedShare, heldShare);
    }

    private static bool SharesMajorityCulture(Campaign c, Nation challenger, ScenarioState[] parentStates,
        HashSet<string> challengerCultures)
    {
        if (challengerCultures.Count == 0) return false;
        var parentCultures = WarAims.Cultures(c.Nations.Find(n => n.MapIndex == parentStates[0].NationMapIndex));
        if (parentCultures.Count > 0 && challengerCultures.Overlaps(parentCultures))
            return true;
        long total = 0, shared = 0;
        foreach (var state in parentStates)
        {
            var pop = Math.Max(0L, state.Population);
            total += pop;
            shared += (long)(pop * WarAims.CultureShare(state, challengerCultures));
        }
        return total > 0 && shared * 2 >= total;
    }

    private static float CultureGroupShare(ScenarioState region, HashSet<string> cultures)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cultures)
            if (DemographyCatalog.Culture(c)?.Group is { Length: > 0 } g) groups.Add(g);
        if (groups.Count == 0) return 0f;
        long total = 0, matching = 0;
        foreach (var (culture, size) in EconomySystem.Cultures(region))
        {
            var s = Math.Max(0L, size);
            total += s;
            if (DemographyCatalog.Culture(culture)?.Group is { Length: > 0 } g && groups.Contains(g))
                matching += s;
        }
        return total <= 0 ? 0f : matching / (float)total;
    }

    /// <summary>
    /// What a state winning a civil war or reunification war knocks off a compromise. Nothing for a
    /// surrender, nothing for a written peace that hands ground back, and nothing for a state
    /// that is losing - that one is entitled to stop.
    /// </summary>
    public static float CompromisePenalty(Campaign c, StrategicWar war, int proposerId, PeaceTerms terms)
    {
        if (terms is PeaceTerms.Capitulate or PeaceTerms.EnforceDemands or PeaceTerms.EnforceWarGoal) return 0f;
        var responderId = war.EnemyLeaderOf(proposerId);
        var responder = c?.NationById(responderId);
        var proposer = c?.NationById(proposerId);
        if (!Reclaiming(c, war, responderId) && !IsReunificationTarget(c, war, responder, proposer)) return 0f;
        var responderScore = war.IsAttacking(responderId) ? war.WarScore : -war.WarScore;
        if (responderScore <= 5f) return 0f;
        var basePenalty = c.Defines.ReclaimCompromisePenalty * M.Clamp01((responderScore - 5f) / 10f);

        // When a civil war or rebellion enters prolonged stalemate, the government's refusal to accept
        // a line-of-contact ceasefire / status-quo softens over time, allowing organic frozen conflicts.
        var duration = c.ElapsedDays - war.StartedDay;
        var stalemateDiscount = 0f;
        if (duration > 365)
        {
            var years = (duration - 365f) / 365f;
            stalemateDiscount = terms == PeaceTerms.StatusQuo ? MathF.Min(0.85f, years * 0.35f) : MathF.Min(0.60f, years * 0.20f);
        }
        if (war.Stalled) stalemateDiscount = MathF.Max(stalemateDiscount, terms == PeaceTerms.StatusQuo ? 0.65f : 0.40f);

        return MathF.Max(0f, basePenalty * (1f - stalemateDiscount));
    }

    /// <summary>The opponent's land that this belligerent holds, as a share of all of it.</summary>
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

    /// <summary>Whether a civil-war belligerent that is losing its war should stop and surrender.</summary>
    public static bool ShouldSurrender(Campaign c, StrategicWar war, Nation loser)
    {
        if (c is null || war is null || loser is null || loser.IsHuman) return false;
        var winner = c.NationById(war.EnemyLeaderOf(loser.Id));
        if (!ClaimsStateInCivilWar(c, war, winner, loser)) return false;
        if (war.EnemyLeaderOf(winner.Id) != loser.Id) return false;
        var defines = c.Defines;
        var held = HeldShare(c, loser, winner);
        if (held >= defines.BreakawaySurrenderOccupied * 0.5f && Factions.FightsOn(c, loser, held)) return false;
        return held >= defines.BreakawaySurrenderOccupied ||
               loser.WarSupport <= defines.BreakawaySurrenderSupport && held >= defines.BreakawaySurrenderOccupied * 0.5f;
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

    internal static List<int> CapitulationGround(Campaign c, StrategicWar war, Nation beaten, Nation winner)
    {
        var result = new List<int>();
        var map = c?.ProvinceMap;
        if (map is null || war is null || beaten is null || winner is null) return result;
        var directBreakaway = IsBreakawayOf(beaten, winner);
        var civilStateWin = ClaimsStateInCivilWar(c, war, winner, beaten);
        var heldShare = HeldShare(c, beaten, winner);
        var fullAnnex = civilStateWin ||
                        (c.Defines.PeaceCapitulationRecoversHomeland &&
                         ((IsReunificationTarget(c, war, winner, beaten) && heldShare >= c.Defines.ReclaimSecuredShare) ||
                          (heldShare >= c.Defines.AiPeaceDecisiveOccupationShare && CanAnnexWhole(c, war, winner, beaten))));
        if (!directBreakaway && !fullAnnex && !c.Defines.PeaceCapitulationRecoversHomeland) return result;
        var homes = new HashSet<int>();
        foreach (var region in c.StatesOf(beaten))
            if (directBreakaway || fullAnnex || TerritorialClaims.TitleOf(c, winner, region) == TerritorialTitle.Homeland)
                homes.Add(region.MapIndex);
        var belligerents = war.Side(true).Concat(war.Side(false)).Select(c.NationById)
            .Where(n => n is not null).Select(n => n.MapIndex).ToHashSet();
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (map.IsWater(p) || map.ProvinceToNation[p] != beaten.MapIndex || !homes.Contains(map.ProvinceToState[p])) continue;
            var controller = (c.ProvinceController ?? map.ProvinceToNation)[p];
            // A surrender in this war cannot sign away a third party's occupation
            // in another one. Leave its military settlement to the actual belligerents.
            if (controller != beaten.MapIndex && controller != winner.MapIndex && !belligerents.Contains(controller)) continue;
            result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// The share of the old state's garrison in the seceding ground that goes over. Chosen by a
    /// die per formation drawn from the formation and the day, so it is the same world on
    /// every machine and does not move anybody else's rolls.
    /// </summary>
    public static int Defect(Campaign c, Nation parent, Nation born, IReadOnlyCollection<int> provinces,
        float? shareOverride = null)
    {
        if (c is null || parent is null || born is null || provinces is null || provinces.Count == 0) return 0;
        var share = M.Clamp01(shareOverride ?? c.Defines.SecessionDefectionShare);
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
        // A new state is not always born with a garrison standing on its ground: the old
        // state's formations may be at the front. A small cross-section of the parent's
        // army still goes over, so independence is not a flag with no soldiers behind it.
        if (moved == 0)
        {
            var reserve = c.Armies.Where(candidate => candidate.NationId == parent.Id &&
                    candidate.EmbarkedFleetId < 0 && !candidate.InBattle)
                .OrderBy(candidate => candidate.Id).ToList();
            var wanted = Math.Min(reserve.Count,
                Math.Max(1, (int)MathF.Round(reserve.Count * M.Clamp(share * 0.25f, 0f, 0.5f))));
            var step = wanted <= 0 ? 1f : reserve.Count / (float)wanted;
            for (var index = 0; index < wanted; index++)
            {
                var army = reserve[Math.Min(reserve.Count - 1, (int)(index * step))];
                if (army.NationId == born.Id) continue;
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
