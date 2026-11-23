using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class CampaignFingerprint
{
    private static void MixNuclearAndVolunteers(Campaign campaign, ref ulong hash)
    {
        foreach (var n in campaign.Nations.OrderBy(n => n.Id))
            if (n.NuclearPosture != 5 || n.NuclearPostureDay != -100000 || n.LastNuclearTestDay != -100000 || n.LastHotlineDay != -100000)
            {
                MixText(ref hash, "nuclear_posture"); Mix(ref hash, n.Id); Mix(ref hash, n.NuclearPosture);
                Mix(ref hash, n.NuclearPostureDay); Mix(ref hash, n.LastNuclearTestDay); Mix(ref hash, n.LastHotlineDay);
            }
        foreach (var n in campaign.Nations.OrderBy(n => n.Id))
            if (n.LastNuclearStrikeDay != -100000) { MixText(ref hash, "nuclear_strike"); Mix(ref hash, n.Id); Mix(ref hash, n.LastNuclearStrikeDay); }
        foreach (var army in campaign.Armies.OrderBy(a => a.Id))
            if (army.LentById >= 0) { MixText(ref hash, "volunteers"); Mix(ref hash, army.Id); Mix(ref hash, army.LentById); Mix(ref hash, army.LentDay); }
        foreach (var n in campaign.Nations.OrderBy(n => n.Id))
            if (n.NuclearStruckDay != -100000 || n.NuclearStruckBy != -1 || n.NuclearFirstUseDay != -100000)
            { MixText(ref hash, "nuclear_struck"); Mix(ref hash, n.Id); Mix(ref hash, n.NuclearStruckDay); Mix(ref hash, n.NuclearStruckBy); Mix(ref hash, n.NuclearFirstUseDay); }
        foreach (var n in campaign.Nations.OrderBy(n => n.Id))
            if (n.NuclearDoctrine != NuclearDoctrine.RetaliatoryTriad || n.NuclearDoctrineDay != -100000 || n.ArmsControlPacts.Count > 0)
            {
                MixText(ref hash, "nuclear_doctrine_arms"); Mix(ref hash, n.Id);
                Mix(ref hash, (int)n.NuclearDoctrine); Mix(ref hash, n.NuclearDoctrineDay);
                Mix(ref hash, n.ArmsControlPacts.Count);
                foreach (var (partnerId, expiryDay) in n.ArmsControlPacts.OrderBy(p => p.Key))
                { Mix(ref hash, partnerId); Mix(ref hash, expiryDay); }
            }
    }

    private static void MixMilitaryRules(Campaign campaign, ref ulong hash)
    {
        if (!campaign.Defines.FrontStaffDisengages || campaign.Defines.FrontDisengagementMinimumBattleDays != 2 ||
            campaign.Defines.FrontDisengagementMaximumShare != .35f || campaign.Defines.FrontDisengagementDefenceRatio != .8f)
        {
            MixText(ref hash, "front_disengagement_rules"); Mix(ref hash, campaign.Defines.FrontStaffDisengages ? 1 : 0);
            Mix(ref hash, campaign.Defines.FrontDisengagementMinimumBattleDays);
            MixFloat(ref hash, campaign.Defines.FrontDisengagementMaximumShare); MixFloat(ref hash, campaign.Defines.FrontDisengagementDefenceRatio);
        }
        var eq = campaign.Defines;
        if (!eq.EquipmentReplenishmentEnabled || eq.EquipmentReplenishmentPerDay != .02f ||
            eq.EquipmentCombatDeliveryShare != .4f || eq.EquipmentMinimumSupply != .22f)
        {
            MixText(ref hash, "equipment_delivery_rules"); Mix(ref hash, eq.EquipmentReplenishmentEnabled ? 1 : 0);
            MixFloat(ref hash, eq.EquipmentReplenishmentPerDay); MixFloat(ref hash, eq.EquipmentCombatDeliveryShare);
            MixFloat(ref hash, eq.EquipmentMinimumSupply);
        }
        if (campaign.Defines.SupplyCutoffDays != 14) { MixText(ref hash, "supply_cutoff_days"); Mix(ref hash, campaign.Defines.SupplyCutoffDays); }
        if (!campaign.Defines.FrontStaffRelievesPockets) { MixText(ref hash, "front_staff_relieves_pockets"); Mix(ref hash, 0); }
        if (MathF.Abs(campaign.Defines.SupplyAttackCoverageFloor - 0.5f) > 0.0001f)
        { MixText(ref hash, "supply_attack_cover"); MixFloat(ref hash, campaign.Defines.SupplyAttackCoverageFloor); }
        if (campaign.Defines.DoctrineBooksPerShelf != 2) { MixText(ref hash, "doctrine_books_per_shelf"); Mix(ref hash, campaign.Defines.DoctrineBooksPerShelf); }
        if (MathF.Abs(campaign.Defines.DoctrineSameShelfStack - Defines.DefaultDoctrineSameShelfStack) > 0.0001f)
        { MixText(ref hash, "doctrine_same_shelf_stack"); MixFloat(ref hash, campaign.Defines.DoctrineSameShelfStack); }
        if (MathF.Abs(campaign.Defines.ResearchUseShare - Defines.DefaultResearchUseShare) > 0.0001f ||
            MathF.Abs(campaign.Defines.ResearchUseCeiling - Defines.DefaultResearchUseCeiling) > 0.0001f)
        { MixText(ref hash, "research_field_use"); MixFloat(ref hash, campaign.Defines.ResearchUseShare); MixFloat(ref hash, campaign.Defines.ResearchUseCeiling); }
        if (!campaign.Defines.CombatTacticsEnabled || MathF.Abs(campaign.Defines.CombatCounterTacticBonus - 1.15f) > 0.0001f)
        { MixText(ref hash, "combat_tactics"); Mix(ref hash, campaign.Defines.CombatTacticsEnabled ? 1 : 0); MixFloat(ref hash, campaign.Defines.CombatCounterTacticBonus); }
    }

    private static void MixMovementRules(Campaign campaign, ref ulong hash)
    {
        if (campaign.Defines.AutonomousAssaultMinimumOrganisation != .16f ||
            campaign.Defines.AutonomousAssaultMinimumSupply != .20f)
        { MixText(ref hash, "autonomous_assault_rules"); MixFloat(ref hash, campaign.Defines.AutonomousAssaultMinimumOrganisation); MixFloat(ref hash, campaign.Defines.AutonomousAssaultMinimumSupply); }
        var mr = campaign.Defines;
        if (mr.MovementStrikeMomentum != .4f || mr.MovementMarchMomentum != .65f || mr.MovementParallelMomentum != .5f ||
            mr.MovementParallelSupport != .6f || mr.MovementParallelLevy != .0006f || mr.MovementStrikeDays != 42 ||
            mr.MovementMarchSupport != .5f)
        {
            MixText(ref hash, "movement_power_rules"); MixFloat(ref hash, mr.MovementStrikeMomentum);
            MixFloat(ref hash, mr.MovementMarchMomentum); MixFloat(ref hash, mr.MovementParallelMomentum);
            MixFloat(ref hash, mr.MovementParallelSupport); MixFloat(ref hash, mr.MovementParallelLevy);
            Mix(ref hash, mr.MovementStrikeDays); MixFloat(ref hash, mr.MovementMarchSupport);
        }
    }

    private static void MixSeaLifts(Campaign campaign, ref ulong hash)
    {
        if (campaign.SeaLifts.Count == 0) return;
        MixText(ref hash, "sea_lifts"); Mix(ref hash, campaign.SeaLifts.Count);
        foreach (var lift in campaign.SeaLifts.OrderBy(l => l.Id))
        {
            Mix(ref hash, lift.Id); Mix(ref hash, lift.NationId); Mix(ref hash, lift.FleetId);
            Mix(ref hash, lift.PortProvinceId); Mix(ref hash, lift.LandingProvinceId);
            Mix(ref hash, lift.TargetProvinceId); Mix(ref hash, lift.Stage); Mix(ref hash, lift.OrderedDay);
            foreach (var id in lift.ArmyIds) Mix(ref hash, id);
            Mix(ref hash, -106);
        }
    }

    private static void MixNationDoctrinesAndExperience(Nation nation, ref ulong hash)
    {
        var legacy = nation.TacticalDoctrine;
        var legacyCopied = legacy.Branch.Length > 0 && legacy.Tracks.Values.Any(track =>
            string.Equals(track.Branch, legacy.Branch, StringComparison.Ordinal) && track.Known.Count == legacy.Known.Count);
        if (!legacyCopied && (legacy.Branch.Length > 0 || legacy.Known.Count > 0 || legacy.LearningNode.Length > 0))
        {
            MixText(ref hash, "tactical_doctrine_legacy"); MixText(ref hash, legacy.Branch); Mix(ref hash, legacy.Known.Count);
            foreach (var node in legacy.Known.OrderBy(id => id, StringComparer.Ordinal)) MixText(ref hash, node);
            MixText(ref hash, legacy.LearningNode); Mix(ref hash, legacy.LearningDoneDay);
        }
        if (nation.TacticalDoctrine.Tracks.Count > 0)
            foreach (var slot in DoctrineSlots.All)
            {
                var track = nation.TacticalDoctrine.Tracks.GetValueOrDefault(slot);
                if (track is null || (track.Branch.Length == 0 && track.Known.Count == 0 && track.LearningNode.Length == 0)) continue;
                MixText(ref hash, "tactical_doctrines"); MixText(ref hash, slot); MixText(ref hash, track.Branch); Mix(ref hash, track.Known.Count);
                foreach (var node in track.Known.OrderBy(id => id, StringComparer.Ordinal)) MixText(ref hash, node);
                MixText(ref hash, track.LearningNode); Mix(ref hash, track.LearningDoneDay);
                var companion = track.Companion;
                if (companion is null || (companion.Branch.Length == 0 && companion.Known.Count == 0 && companion.LearningNode.Length == 0)) continue;
                MixText(ref hash, "tactical_doctrine_second"); MixText(ref hash, slot); MixText(ref hash, companion.Branch);
                Mix(ref hash, companion.Known.Count);
                foreach (var node in companion.Known.OrderBy(id => id, StringComparer.Ordinal)) MixText(ref hash, node);
                MixText(ref hash, companion.LearningNode); Mix(ref hash, companion.LearningDoneDay);
            }
        if (nation.StrategicDoctrine.Id.Length > 0 || nation.StrategicDoctrine.LastChangeDay > -9999)
        { MixText(ref hash, "strategic_doctrine"); MixText(ref hash, nation.StrategicDoctrine.Id); Mix(ref hash, nation.StrategicDoctrine.LastChangeDay); }
        if (nation.TechnologyFamiliarity.Count > 0)
        {
            var wrote = false;
            foreach (var entry in nation.TechnologyFamiliarity.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!(entry.Value > 0f) || !float.IsFinite(entry.Value)) continue;
                if (!wrote) { MixText(ref hash, "technology_familiarity"); wrote = true; }
                MixText(ref hash, entry.Key); MixFloat(ref hash, entry.Value);
            }
        }
        if (nation.ArmyExperience != 0f || nation.AirExperience != 0f || nation.NavalExperience != 0f || nation.PoliticalPoints != 0f)
        {
            MixText(ref hash, "military_experience"); MixFloat(ref hash, nation.ArmyExperience);
            MixFloat(ref hash, nation.AirExperience); MixFloat(ref hash, nation.NavalExperience); MixFloat(ref hash, nation.PoliticalPoints);
        }
        if (MathF.Abs(nation.ImperialOverstretch) > 0.0001f)
        { MixText(ref hash, "imperial_overstretch"); MixFloat(ref hash, nation.ImperialOverstretch); }
    }

    private static void MixFrontLinesAndArmyGroups(Campaign campaign, ref ulong hash)
    {
        foreach (var line in Ordered(campaign.FrontLines, line => line.Id))
        {
            if (line.AllEnemies) { MixText(ref hash, "whole_front"); Mix(ref hash, line.Id); Mix(ref hash, line.NationId); }
            if (line.Kind != FrontLineKind.Offensive || line.OffensivePhases.Count <= 1 &&
                line.ActiveOffensivePhase == 0 && line.OffensiveRegroupDay < 0) continue;
            MixText(ref hash, "offensive_phases"); Mix(ref hash, line.Id); Mix(ref hash, line.OffensivePhases.Count);
            Mix(ref hash, line.ActiveOffensivePhase); Mix(ref hash, line.OffensiveRegroupDay);
            foreach (var phase in line.OffensivePhases)
            { Mix(ref hash, phase.ObjectiveSequence.Count); foreach (var province in phase.ObjectiveSequence) Mix(ref hash, province); }
            foreach (var assignment in line.Objectives.OrderBy(pair => pair.Key)) { Mix(ref hash, assignment.Key); Mix(ref hash, assignment.Value); }
        }
        foreach (var group in Ordered(campaign.ArmyGroups, group => group.Id))
        {
            MixText(ref hash, "army_group"); Mix(ref hash, group.Id); Mix(ref hash, group.NationId); MixText(ref hash, group.Commander);
            MixFloat(ref hash, group.StaffSkill); MixText(ref hash, group.CommanderTrait); Mix(ref hash, group.Victories);
            Mix(ref hash, group.Defeats); Mix(ref hash, group.CommanderSinceDay); Mix(ref hash, group.CommandersLost);
            if (group.CommandersRelieved != 0) { MixText(ref hash, "relieved"); Mix(ref hash, group.CommandersRelieved); }
            if (group.StaffedDay >= 0) { MixText(ref hash, "staffed"); Mix(ref hash, group.StaffedDay); }
        }
        if (campaign.NextArmyGroupId > 0) { MixText(ref hash, "next_army_group"); Mix(ref hash, campaign.NextArmyGroupId); }
    }

    private static void MixArmiesAndBattleWithdrawals(Campaign campaign, ref ulong hash)
    {
        foreach (var army in Ordered(campaign.Armies, army => army.Id))
        {
            Mix(ref hash, army.Id); Mix(ref hash, army.ProvinceId); Mix(ref hash, army.Personnel);
            MixFloat(ref hash, army.Organisation); MixFloat(ref hash, army.Strength);
            if (army.Equipment != .88f) { MixText(ref hash, "formation_equipment"); MixFloat(ref hash, army.Equipment); }
            MixFloat(ref hash, army.Entrenchment); Mix(ref hash, army.Route.Count);
            if (army.PostBattleWaypoints.Count > 0)
            {
                MixText(ref hash, "post_battle_orders"); Mix(ref hash, army.PostBattleSupportAttack ? 1 : 0);
                Mix(ref hash, army.PostBattleWaypoints.Count);
                foreach (var destination in army.PostBattleWaypoints) Mix(ref hash, destination);
            }
            if (army.InBattle || army.ReadyDay > 0) { MixText(ref hash, "battle_assignment"); Mix(ref hash, army.BattleId); Mix(ref hash, army.ReadyDay); }
            if (army.ContractUntilDay >= 0) { MixText(ref hash, "contract_until"); Mix(ref hash, army.ContractUntilDay); }
            if (army.Training) { MixText(ref hash, "training"); Mix(ref hash, army.Id); }
            if (army.Stance != ArmyStance.Balanced) { MixText(ref hash, "formation_stance"); Mix(ref hash, army.Id); Mix(ref hash, (int)army.Stance); }
            if (army.StaffNote != StaffNote.None) { MixText(ref hash, "staff_note"); Mix(ref hash, army.Id); Mix(ref hash, (int)army.StaffNote); }
        }
        foreach (var battle in Ordered(campaign.Battles, b => b.Id))
        {
            if (battle.WithdrawnAttackers.Count == 0 && battle.WithdrawnDefenders.Count == 0) continue;
            MixText(ref hash, "battle_withdrawal"); Mix(ref hash, battle.Id); Mix(ref hash, battle.Ended ? 1 : 0);
            Mix(ref hash, battle.Attackers.Count); foreach (var id in battle.Attackers.OrderBy(id => id)) Mix(ref hash, id);
            Mix(ref hash, battle.Defenders.Count); foreach (var id in battle.Defenders.OrderBy(id => id)) Mix(ref hash, id);
            Mix(ref hash, battle.WithdrawnAttackers.Count); foreach (var id in battle.WithdrawnAttackers.OrderBy(id => id)) Mix(ref hash, id);
            Mix(ref hash, battle.WithdrawnDefenders.Count); foreach (var id in battle.WithdrawnDefenders.OrderBy(id => id)) Mix(ref hash, id);
        }
        if (!campaign.Battles.Any(b => b.WithdrawnAttackers.Count > 0 || b.WithdrawnDefenders.Count > 0)) return;
        foreach (var army in Ordered(campaign.Armies, a => a.Id))
        {
            MixText(ref hash, "withdrawal_formation"); Mix(ref hash, army.Id); MixFloat(ref hash, army.Divisions); Mix(ref hash, army.MaxPersonnel);
            MixFloat(ref hash, army.Recovering); MixFloat(ref hash, army.Equipment); Mix(ref hash, army.BattleId); Mix(ref hash, army.ReadyDay);
            Mix(ref hash, army.AssignedWarId); Mix(ref hash, army.FrontLineId); Mix(ref hash, (int)army.OrderKind);
            Mix(ref hash, army.DirectOrder ? 1 : 0); Mix(ref hash, army.OrderProvinceId);
            foreach (var step in army.Route) Mix(ref hash, step);
            Mix(ref hash, -35);
        }
    }

    private static void MixArmyPockets(Campaign campaign, ref ulong hash)
    {
        foreach (var army in Ordered(campaign.Armies, a => a.Id))
        {
            if (army.CutOff) { MixText(ref hash, "cut_off"); Mix(ref hash, army.Id); }
            if (army.DaysCutOff > 0) { MixText(ref hash, "days_cut_off"); Mix(ref hash, army.Id); Mix(ref hash, army.DaysCutOff); }
            if (army.BreakingOut) { MixText(ref hash, "breaking_out"); Mix(ref hash, army.Id); }
            if (army.PocketStores >= 0f) { MixText(ref hash, "pocket_stores"); Mix(ref hash, army.Id); MixFloat(ref hash, army.PocketStores); Mix(ref hash, army.PocketSinceDay); }
            if (army.HoldoutSinceDay >= 0) { MixText(ref hash, "holdout"); Mix(ref hash, army.Id); Mix(ref hash, army.HoldoutSinceDay); }
            if (army.ReliefDistance != -1) { MixText(ref hash, "relief_distance"); Mix(ref hash, army.Id); Mix(ref hash, army.ReliefDistance); }
            if (army.LivingOffLand) { MixText(ref hash, "living_off_land"); Mix(ref hash, army.Id); }
        }
    }

    private static void MixWars(Campaign campaign, ref ulong hash)
    {
        foreach (var war in Ordered(campaign.Wars, war => war.Id))
        {
            Mix(ref hash, war.Id); Mix(ref hash, war.Ended ? 1 : 0);
            MixFloat(ref hash, war.OccupationScore); MixFloat(ref hash, war.BattleScore);
            Mix(ref hash, war.CivilConflict ? 1 : 0); Mix(ref hash, war.EscalationStage); Mix(ref hash, war.AiNegotiationUnlockDay);
            if (war.WorldWarDay >= 0) { MixText(ref hash, "world_war"); Mix(ref hash, war.WorldWarDay); }
            if (war.PendingOffer is { BeneficiaryId: >= 0 } offer) { MixText(ref hash, "peace_beneficiary"); Mix(ref hash, offer.BeneficiaryId); }
            MixWarPeaceOffers(war, ref hash);
            if (war.SeparatePeaces.Count > 0)
            {
                MixText(ref hash, "separate_peaces");
                foreach (var left in war.SeparatePeaces)
                { Mix(ref hash, left.NationId); Mix(ref hash, left.WithId); Mix(ref hash, left.Day); Mix(ref hash, (int)left.Terms); Mix(ref hash, left.SwitchedSides ? 1 : 0); MixFloat(ref hash, left.Payment); Mix(ref hash, left.ProvincesCeded); }
            }
            if (war.AttackerAllies.Count > 0 || war.DefenderAllies.Count > 0)
            {
                MixText(ref hash, "war_coalition"); Mix(ref hash, war.AttackerId); Mix(ref hash, war.DefenderId);
                Mix(ref hash, war.AttackerAllies.Count); foreach (var member in war.AttackerAllies.OrderBy(id => id)) Mix(ref hash, member);
                Mix(ref hash, war.DefenderAllies.Count); foreach (var member in war.DefenderAllies.OrderBy(id => id)) Mix(ref hash, member);
            }
        }
    }

    private static void MixWarPeaceOffers(StrategicWar war, ref ulong hash)
    {
        if (war.PendingOfferFromId >= 0)
        {
            MixText(ref hash, "pending_peace_offer"); Mix(ref hash, war.PendingOfferFromId);
            Mix(ref hash, war.PendingOfferIsWritten ? 1 : 0); Mix(ref hash, (int)war.PendingOfferTerms);
            Mix(ref hash, war.PendingOfferDay); Mix(ref hash, war.PendingOfferExpiresDay);
            if (war.PendingOffer is { } pending)
            {
                Mix(ref hash, pending.Provinces.Count);
                foreach (var province in pending.Provinces.OrderBy(id => id)) Mix(ref hash, province);
                Mix(ref hash, pending.ReleaseRegions ? 1 : 0); Mix(ref hash, pending.Reparations ? 1 : 0);
                Mix(ref hash, pending.TradeConcessions ? 1 : 0); Mix(ref hash, pending.Demilitarise ? 1 : 0);
                Mix(ref hash, pending.Humiliate ? 1 : 0); Mix(ref hash, pending.LongTruce ? 1 : 0);
                Mix(ref hash, pending.Unconditional ? 1 : 0); MixText(ref hash, pending.Subjugate);
            }
        }
        if (war.LastOfferToPlayerDay < 0 && war.RefusedOfferDay < 0) return;
        MixText(ref hash, "peace_offer_to_player"); Mix(ref hash, war.LastOfferToPlayerDay); Mix(ref hash, war.RefusedOfferDay);
        Mix(ref hash, war.RefusedOfferWasWritten ? 1 : 0); Mix(ref hash, (int)war.RefusedOfferTerms);
        if (war.RefusedOffer is not { } refused) return;
        Mix(ref hash, refused.Provinces.Count);
        foreach (var province in refused.Provinces.OrderBy(id => id)) Mix(ref hash, province);
    }

    private static void MixLongRangeForces(Campaign campaign, ref ulong hash)
    {
        var strikeRules = LongRangeStrikes.Rules(campaign.Defines);
        if (!strikeRules.SequenceEqual(LongRangeStrikes.DefaultRules))
        { MixText(ref hash, "long_range_strike_rules"); foreach (var rule in strikeRules) MixFloat(ref hash, rule); }
        foreach (var formation in campaign.SupportFormations
                     .Where(f => !string.IsNullOrEmpty(f.StrikeTarget) || f.TargetStateMapIndex >= 0).OrderBy(f => f.Id))
        { MixText(ref hash, "strike_target"); Mix(ref hash, formation.Id); MixText(ref hash, formation.StrikeTarget); Mix(ref hash, formation.TargetStateMapIndex); }
        var relocationRules = LongRangeMovement.Rules(campaign.Defines);
        if (!relocationRules.SequenceEqual(LongRangeMovement.DefaultRules))
        { MixText(ref hash, "long_range_movement_rules"); foreach (var rule in relocationRules) MixFloat(ref hash, rule); }
        foreach (var formation in campaign.SupportFormations
                     .Where(f => f.Route.Count > 0 || f.DeployDaysLeft > 0 || f.CutOff || f.Overrun).OrderBy(f => f.Id))
        {
            MixText(ref hash, "support_position"); Mix(ref hash, formation.Id); Mix(ref hash, formation.ProvinceId); Mix(ref hash, formation.BaseProvinceId);
            Mix(ref hash, formation.Route.Count);
            foreach (var step in formation.Route) Mix(ref hash, step);
            foreach (var mode in formation.RouteModes) Mix(ref hash, mode);
            MixFloat(ref hash, formation.MoveProgress); Mix(ref hash, formation.DeployDaysLeft);
            Mix(ref hash, formation.CutOff ? 1 : 0); Mix(ref hash, formation.Overrun ? 1 : 0); MixFloat(ref hash, formation.Strength);
            if (formation.SeaCourseMask != 0) { MixText(ref hash, "sea_course"); Mix(ref hash, formation.SeaCourseMask); }
        }
    }
}
