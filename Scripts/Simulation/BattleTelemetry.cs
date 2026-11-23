using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Share of defender entrenchment bypassed per extra attack axis beyond the first (0..1).</summary>
    public float FlankingEntrenchmentBypassPerDirection { get; set; } = 0.18f;

    /// <summary>Maximum share of defender entrenchment that multi-direction flanking can strip.</summary>
    public float FlankingEntrenchmentMaxBypass { get; set; } = 0.54f;

    /// <summary>Fire &amp; breakthrough synergy multiplier added when a line mixes soft infantry with armour or long-range support.</summary>
    public float CombinedArmsBonus { get; set; } = 0.08f;

    /// <summary>Supply level at or below which an encircled formation past SupplyCutoffDays enters pocket collapse.</summary>
    public float PocketCollapseSupplyThreshold { get; set; } = 0.12f;

    /// <summary>Organisation-damage multiplier suffered by encircled zero-supply formations in pocket collapse.</summary>
    public float PocketCollapseOrgMultiplier { get; set; } = 1.45f;

    /// <summary>Minimum reorganization cooldown days enforced on broken retreating formations to prevent 0-org ping-pong.</summary>
    public int RetreatReorgMinDays { get; set; } = 3;
}

public sealed partial class LandBattle
{
    /// <summary>Last round's distinct attacking province axes (1..MaxAttackDirections); read-only telemetry.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int LastAttackDirections = 1;

    /// <summary>Last round's tactical battle phase; read-only telemetry.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public BattlePhase LastPhase = BattlePhase.MeetingEngagement;

    /// <summary>Last round's flanking entrenchment bypass fraction (0..0.54); read-only telemetry.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float LastFlankingBypass;

    /// <summary>Last round's attacker/defender combined-arms multipliers; read-only telemetry.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float LastAttackerCombinedArms = 1f;

    [System.Text.Json.Serialization.JsonIgnore]
    public float LastDefenderCombinedArms = 1f;
}

public enum BattlePhase
{
    MeetingEngagement = 0,
    PreparedAssault = 1,
    ArmouredBreakthrough = 2,
    FlankingEnvelopment = 3,
    ArtilleryBarrage = 4,
    UrbanGrind = 5,
    ElasticDefence = 6,
    DesperateCounterattack = 7,
    PocketCollapse = 8
}

public enum BattleFormationRole
{
    Frontline = 0,
    Reserve = 1,
    Retreating = 2
}

/// <summary>One colour-coded combat modifier chip for Battle Inspection UI tooltips.</summary>
public readonly record struct BattleModifierChip(
    string Key,
    string Label,
    float Multiplier,
    bool IsPositive,
    string Category);

/// <summary>Read-only combat snapshot of one division/formation participating in a battle.</summary>
public readonly record struct BattleFormationSnapshot(
    int ArmyId,
    int NationId,
    string Name,
    string TemplateId,
    string CounterSymbol,
    float Divisions,
    int Personnel,
    float Strength,
    float Organisation,
    float Supply,
    float Entrenchment,
    float Planning,
    bool CutOff,
    bool InPocketCollapse,
    BattleFormationRole Role);

/// <summary>Aggregated read-only telemetry for one side (Attacker or Defender) of a LandBattle.</summary>
public sealed record BattleSideSnapshot(
    int LeaderNationId,
    int TotalFormations,
    int InLineCount,
    int ReserveCount,
    float TotalDivisions,
    int TotalPersonnel,
    float Organisation,
    float Strength,
    float Supply,
    float Entrenchment,
    float Equipment,
    float WidthUsed,
    float WidthLimit,
    float SupportFire,
    float CombinedArmsBonus,
    string TacticId,
    string TacticName,
    string TacticDescription,
    bool TacticCounteredEnemy,
    IReadOnlyList<BattleModifierChip> Modifiers,
    IReadOnlyList<BattleFormationSnapshot> Formations);

/// <summary>
/// Unified read-only combat telemetry snapshot shared by the Battle Inspection panel
/// (<c>ui-workspaces</c>) and on-map Battle Progress Bubbles (<c>graphics-aaa</c>).
/// Building a snapshot NEVER mutates <see cref="Campaign"/> or <see cref="CampaignFingerprint"/>.
/// </summary>
public sealed record BattleTelemetrySnapshot(
    int BattleId,
    int WarId,
    int ProvinceId,
    string ProvinceName,
    string TerrainId,
    string TerrainName,
    bool Ended,
    int WinnerId,
    int DaysFought,
    BattlePhase Phase,
    string PhaseLabel,
    string PhaseDescription,
    int AttackDirections,
    float FlankingEntrenchmentBypass,
    float RiverCrossingStrength,
    float AttackerProgress,
    float Momentum,
    float EstimatedDaysToResolve,
    bool AttackerFavoured,
    bool HasEncircledDefenders,
    bool HasPocketCollapse,
    string HeadlineNote,
    BattleSideSnapshot Attacker,
    BattleSideSnapshot Defender);

/// <summary>
/// Shared warfare math &amp; zero-mutation battle telemetry builder.
/// Both <see cref="Warfare"/> combat resolution and UI/Map presentation read these exact
/// formulas so displayed chips always agree with the live simulation.
/// </summary>
public static class BattleTelemetry
{
    public static float FlankingEntrenchmentRetention(Defines defines, int attackDirections)
    {
        if (defines is null || attackDirections <= 1) return 1f;
        var extra = Math.Max(0, attackDirections - 1);
        var bypass = M.Clamp(extra * MathF.Max(0f, defines.FlankingEntrenchmentBypassPerDirection),
            0f, M.Clamp(defines.FlankingEntrenchmentMaxBypass, 0f, 0.85f));
        return 1f - bypass;
    }

    public static float CombinedArmsMultiplier(Defines defines, IReadOnlyList<Army> line, float supportFire)
    {
        if (defines is null || line is null || line.Count == 0 || defines.CombinedArmsBonus <= 0f) return 1f;
        var hasInfantry = false;
        var hasArmour = false;
        var hasMobileOrSpecial = false;
        for (var i = 0; i < line.Count; i++)
        {
            var army = line[i];
            if (army is null || army.Divisions < 0.05f) continue;
            var u = UnitTypeCatalog.Get(army.TemplateId);
            if (u.Hardness >= 0.30f || u.Armour >= 0.35f) hasArmour = true;
            else hasInfantry = true;
            if (u.Speed >= 1.25f || u.Amphibious >= 0.35f) hasMobileOrSpecial = true;
        }
        var hasSupport = supportFire > 1.03f;
        // Full synergy: infantry + armour (or armour + long-range fire); partial synergy: infantry + long-range or mobile.
        if (hasInfantry && hasArmour)
            return 1f + defines.CombinedArmsBonus * (hasSupport ? 1.25f : 1f);
        if ((hasInfantry || hasArmour) && hasSupport && (line.Count >= 2 || hasMobileOrSpecial))
            return 1f + defines.CombinedArmsBonus * 0.5f;
        return 1f;
    }

    public static bool IsInPocketCollapse(Defines defines, Army army)
    {
        if (defines is null || army is null || !army.CutOff) return false;
        return army.SupplyLevel <= M.Clamp01(defines.PocketCollapseSupplyThreshold) &&
               army.DaysCutOff >= Math.Max(1, defines.SupplyCutoffDays);
    }

    public static BattlePhase ClassifyPhase(
        string terrainId,
        int attackDirections,
        bool hasPocketCollapse,
        float attackerArmour,
        float attackerPlanning,
        float defenderEntrenchment,
        float attackerSupport,
        float defenderSupport,
        float breakthroughSignal,
        float counterattackSignal)
    {
        if (hasPocketCollapse) return BattlePhase.PocketCollapse;
        if (breakthroughSignal >= 0.40f && attackerArmour >= 0.25f) return BattlePhase.ArmouredBreakthrough;
        if (counterattackSignal >= 0.40f) return BattlePhase.DesperateCounterattack;
        if (attackDirections >= 3 || (attackDirections >= 2 && breakthroughSignal >= 0.25f))
            return BattlePhase.FlankingEnvelopment;
        if (string.Equals(terrainId, "urban", StringComparison.OrdinalIgnoreCase))
            return BattlePhase.UrbanGrind;
        if (attackerSupport >= 1.18f || defenderSupport >= 1.18f)
            return BattlePhase.ArtilleryBarrage;
        if (defenderEntrenchment >= 0.55f && counterattackSignal >= 0.15f)
            return BattlePhase.ElasticDefence;
        if (attackerPlanning >= 0.35f || breakthroughSignal >= 0.20f)
            return BattlePhase.PreparedAssault;
        return BattlePhase.MeetingEngagement;
    }

    public static string PhaseKey(BattlePhase phase) => phase switch
    {
        BattlePhase.PreparedAssault => "wf.phase.prepared_assault",
        BattlePhase.ArmouredBreakthrough => "wf.phase.armoured_breakthrough",
        BattlePhase.FlankingEnvelopment => "wf.phase.flanking_envelopment",
        BattlePhase.ArtilleryBarrage => "wf.phase.artillery_barrage",
        BattlePhase.UrbanGrind => "wf.phase.urban_grind",
        BattlePhase.ElasticDefence => "wf.phase.elastic_defence",
        BattlePhase.DesperateCounterattack => "wf.phase.desperate_counterattack",
        BattlePhase.PocketCollapse => "wf.phase.pocket_collapse",
        _ => "wf.phase.meeting_engagement"
    };

    public static string PhaseLabel(BattlePhase phase) => Loc.Get(PhaseKey(phase) + ".name");
    public static string PhaseDescription(BattlePhase phase) => Loc.Get(PhaseKey(phase) + ".desc");

    /// <summary>
    /// Build a complete, read-only telemetry snapshot for any live or finished <see cref="LandBattle"/>.
    /// Zero mutation of <see cref="Campaign"/>, <see cref="LandBattle"/>, or <see cref="CampaignFingerprint"/>.
    /// </summary>
    public static BattleTelemetrySnapshot Build(Campaign campaign, LandBattle battle)
    {
        if (campaign is null || battle is null) return null;

        var defines = campaign.Defines;
        var terrain = campaign.ProvinceMap?.TerrainAt(battle.ProvinceId) ?? TerrainCatalog.Get("plains");
        var stateIdx = campaign.ProvinceMap?.ProvinceToState is { } p2s &&
                       battle.ProvinceId > 0 && battle.ProvinceId < p2s.Length
            ? p2s[battle.ProvinceId]
            : 0;
        var provinceName = stateIdx > 0 && campaign.StateByMapIndex(stateIdx) is { } st
            ? st.DisplayName
            : $"#{battle.ProvinceId}";

        var attackers = CollectArmies(campaign, battle.Attackers);
        var defenders = CollectArmies(campaign, battle.Defenders);
        var withdrawnAtk = CollectArmies(campaign, battle.WithdrawnAttackers);
        var withdrawnDef = CollectArmies(campaign, battle.WithdrawnDefenders);

        var directions = battle.LastAttackDirections > 0
            ? battle.LastAttackDirections
            : Math.Max(1, CountDistinctOriginProvinces(attackers, battle.ProvinceId));
        var baseWidth = defines.CombatWidth * terrain.CombatWidth;
        var usableWidth = baseWidth + defines.CombatWidthPerDirection * terrain.CombatWidth *
            M.Clamp(directions - 1, 0, Math.Max(0, defines.MaxAttackDirections - 1));

        var hasEncircledDef = false;
        var hasPocketCollapse = false;
        for (var i = 0; i < defenders.Count; i++)
        {
            if (defenders[i].CutOff) hasEncircledDef = true;
            if (IsInPocketCollapse(defines, defenders[i])) hasPocketCollapse = true;
        }

        var atkTactic = FindTactic(battle.AttackerTactic);
        var defTactic = FindTactic(battle.DefenderTactic);

        var attackerSide = BuildSide(
            battle.AttackerId, attackers, withdrawnAtk, battle.LastAttackerLine,
            battle.AttackerWidth, usableWidth, battle.AttackerSupport, battle.LastAttackerCombinedArms,
            atkTactic, defTactic, battle.AttackFactors, defines, isAttacker: true);

        var defenderSide = BuildSide(
            battle.DefenderId, defenders, withdrawnDef, battle.LastDefenderLine,
            battle.DefenderWidth, baseWidth, battle.DefenderSupport, battle.LastDefenderCombinedArms,
            defTactic, atkTactic, battle.DefenceFactors, defines, isAttacker: false);

        var assessment = BattleReadout.Assess(battle, defines.BattleBreakOrganisation);
        float progress;
        float estDays;
        if (battle.Ended)
        {
            progress = battle.WinnerId == battle.AttackerId ? 1f : 0f;
            estDays = 0f;
        }
        else if (assessment.Known)
        {
            progress = M.Clamp01(assessment.AttackerShare);
            estDays = MathF.Min(assessment.AttackerDays, assessment.DefenderDays);
        }
        else
        {
            // Round-0 estimate from initial org * divisions ratio so bubbles are informative on day 1.
            var aScore = MathF.Max(0.01f, attackerSide.TotalDivisions * (0.25f + attackerSide.Organisation));
            var dScore = MathF.Max(0.01f, defenderSide.TotalDivisions * (0.25f + defenderSide.Organisation));
            progress = M.Clamp01(aScore / (aScore + dScore));
            estDays = 0f;
        }

        // Momentum (-1..+1): combines endurance progress and recent round power balance.
        var momentum = M.Clamp((progress - 0.5f) * 2f, -1f, 1f);
        if (battle.Rounds.Count > 0)
        {
            var last = battle.Rounds[^1];
            var pSum = last.AttackerPower + last.DefenderPower;
            if (pSum > 0.01f)
            {
                var powerTilt = (last.AttackerPower / pSum - 0.5f) * 2f;
                momentum = M.Clamp(momentum * 0.65f + powerTilt * 0.35f, -1f, 1f);
            }
        }

        var phase = battle.Rounds.Count > 0
            ? battle.LastPhase
            : ClassifyPhase(terrain.Id, directions, hasPocketCollapse, 0f,
                attackerSide.Formations.Count > 0 ? attackerSide.Formations[0].Planning : 0f,
                defenderSide.Entrenchment, battle.AttackerSupport, battle.DefenderSupport, 0f, 0f);

        var bypass = battle.LastFlankingBypass > 0f
            ? battle.LastFlankingBypass
            : 1f - FlankingEntrenchmentRetention(defines, directions);

        var daysFought = Math.Max(0, campaign.ElapsedDays - battle.StartedDay);
        var headlineNote = battle.Rounds.Count > 0 ? battle.Rounds[^1].Note ?? "" : Loc.Get("wf.note.initial_contact");

        return new BattleTelemetrySnapshot(
            BattleId: battle.Id,
            WarId: battle.WarId,
            ProvinceId: battle.ProvinceId,
            ProvinceName: provinceName,
            TerrainId: terrain.Id,
            TerrainName: Loc.Get(terrain.Name),
            Ended: battle.Ended,
            WinnerId: battle.WinnerId,
            DaysFought: daysFought,
            Phase: phase,
            PhaseLabel: PhaseLabel(phase),
            PhaseDescription: PhaseDescription(phase),
            AttackDirections: directions,
            FlankingEntrenchmentBypass: bypass,
            RiverCrossingStrength: battle.RiverCrossingStrength,
            AttackerProgress: progress,
            Momentum: momentum,
            EstimatedDaysToResolve: estDays,
            AttackerFavoured: progress >= 0.5f,
            HasEncircledDefenders: hasEncircledDef,
            HasPocketCollapse: hasPocketCollapse,
            HeadlineNote: headlineNote,
            Attacker: attackerSide,
            Defender: defenderSide);
    }

    private static BattleSideSnapshot BuildSide(
        int leaderNationId,
        List<Army> active,
        List<Army> withdrawn,
        List<int> lastLine,
        float widthUsed,
        float widthLimit,
        float supportFire,
        float combinedArms,
        CombatTactic ownTactic,
        CombatTactic enemyTactic,
        List<BattleFactor> rawFactors,
        Defines defines,
        bool isAttacker)
    {
        var summary = BattleReadout.Summarise(active);
        var totalDivs = 0f;
        var entSum = 0f;
        for (var i = 0; i < active.Count; i++)
        {
            var a = active[i];
            totalDivs += MathF.Max(0f, a.Divisions);
            entSum += M.Clamp01(a.Entrenchment) * MathF.Max(0.01f, a.Divisions);
        }
        var avgEnt = totalDivs > 0.01f ? entSum / totalDivs : 0f;

        var formations = new List<BattleFormationSnapshot>(active.Count + withdrawn.Count);
        var inLineCount = 0;
        var reserveCount = 0;
        var hasLastLine = lastLine is { Count: > 0 };
        for (var i = 0; i < active.Count; i++)
        {
            var a = active[i];
            var inLine = !hasLastLine || lastLine.Contains(a.Id);
            if (inLine) inLineCount++; else reserveCount++;
            var unit = UnitTypeCatalog.Get(a.TemplateId);
            formations.Add(new BattleFormationSnapshot(
                a.Id, a.NationId, a.Name, a.TemplateId, unit.CounterSymbol,
                a.Divisions, Math.Max(0, a.Personnel),
                M.Clamp01(a.Strength), M.Clamp01(a.Organisation),
                M.Clamp01(a.SupplyLevel), M.Clamp01(a.Entrenchment), M.Clamp01(a.Planning),
                a.CutOff, IsInPocketCollapse(defines, a),
                inLine ? BattleFormationRole.Frontline : BattleFormationRole.Reserve));
        }
        for (var i = 0; i < withdrawn.Count; i++)
        {
            var a = withdrawn[i];
            var unit = UnitTypeCatalog.Get(a.TemplateId);
            formations.Add(new BattleFormationSnapshot(
                a.Id, a.NationId, a.Name, a.TemplateId, unit.CounterSymbol,
                a.Divisions, Math.Max(0, a.Personnel),
                M.Clamp01(a.Strength), M.Clamp01(a.Organisation),
                M.Clamp01(a.SupplyLevel), M.Clamp01(a.Entrenchment), M.Clamp01(a.Planning),
                a.CutOff, IsInPocketCollapse(defines, a),
                BattleFormationRole.Retreating));
        }

        var chips = new List<BattleModifierChip>(rawFactors?.Count ?? 0);
        if (rawFactors is not null)
        {
            for (var i = 0; i < rawFactors.Count; i++)
            {
                var f = rawFactors[i];
                if (!float.IsFinite(f.Value) || MathF.Abs(f.Value - 1f) < CombatFactors.Threshold) continue;
                chips.Add(new BattleModifierChip(
                    f.Key, Loc.Get(f.Key), f.Value, f.Value > 1f, CategorizeFactor(f.Key, isAttacker)));
            }
        }

        return new BattleSideSnapshot(
            LeaderNationId: leaderNationId,
            TotalFormations: active.Count,
            InLineCount: inLineCount,
            ReserveCount: reserveCount,
            TotalDivisions: totalDivs,
            TotalPersonnel: summary.Personnel,
            Organisation: summary.Organisation,
            Strength: summary.Strength,
            Supply: summary.Supply,
            Entrenchment: avgEnt,
            Equipment: summary.Equipment,
            WidthUsed: widthUsed,
            WidthLimit: MathF.Max(1f, widthLimit),
            SupportFire: supportFire,
            CombinedArmsBonus: MathF.Max(0f, combinedArms - 1f),
            TacticId: ownTactic?.Id ?? "",
            TacticName: ownTactic is not null ? Loc.Get(ownTactic.Name) : Loc.Get("wf.phase.meeting_engagement.name"),
            TacticDescription: ownTactic is not null ? Loc.Get(ownTactic.Description) : "",
            TacticCounteredEnemy: ownTactic is not null && ownTactic.Counters(enemyTactic),
            Modifiers: chips,
            Formations: formations);
    }

    private static List<Army> CollectArmies(Campaign campaign, List<int> ids)
    {
        var list = new List<Army>(ids?.Count ?? 0);
        if (ids is null || ids.Count == 0) return list;
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            for (var j = 0; j < campaign.Armies.Count; j++)
            {
                var a = campaign.Armies[j];
                if (a.Id == id) { list.Add(a); break; }
            }
        }
        return list;
    }

    private static int CountDistinctOriginProvinces(List<Army> attackers, int targetProvince)
    {
        if (attackers.Count <= 1) return 1;
        var seen = new HashSet<int>();
        for (var i = 0; i < attackers.Count; i++)
        {
            var p = attackers[i].ProvinceId;
            if (p > 0 && p != targetProvince) seen.Add(p);
        }
        return Math.Max(1, seen.Count);
    }

    private static CombatTactic FindTactic(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        for (var i = 0; i < CombatTactics.All.Length; i++)
            if (string.Equals(CombatTactics.All[i].Id, id, StringComparison.OrdinalIgnoreCase))
                return CombatTactics.All[i];
        return null;
    }

    private static string CategorizeFactor(string key, bool isAttacker) => key switch
    {
        "Terrain" or "River crossing" or "Strait crossing" => "terrain",
        "Entrenchment" or "Flanking bypass" or "Attack from several sides" or "Attacked from one side" => "flanking",
        "Guns and aircraft" or "Shelled" or "Combined arms" => "support",
        "Encircled" or "Pocket collapse" or "Munitions" => "supply",
        "Manoeuvre of the day" or "Commander" or "Plan prepared" => "tactics",
        _ => isAttacker ? "offence" : "defence"
    };
}
