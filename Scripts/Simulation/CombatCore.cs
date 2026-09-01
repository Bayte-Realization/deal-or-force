using System;
using System.Collections.Generic;
using System.Linq;

namespace ByDealOrForce.Simulation;

/// <summary>A readable daily combat record. Battles spend organisation and cost people.</summary>
public sealed class LandBattle
{
    public sealed class Round
    {
        public int Day;
        public float AttackerOrganisation;
        public float DefenderOrganisation;
        public float AttackerDamage;
        public float DefenderDamage;
        public float AttackerLosses;
        public float DefenderLosses;
        public float AttackerPower;
        public float DefenderPower;
        public float AttackerPlanning;
        public string Note = "";
    }

    public int Id;
    public int WarId;
    public int ProvinceId;
    public int AttackerId;
    public int DefenderId;
    public int StartedDay;
    public bool Ended;
    public int WinnerId = -1;
    public string Outcome = "Fighting";
    public float AttackerLosses;
    public float DefenderLosses;
    public float AttackerWidth;
    public float DefenderWidth;
    public int AttackerReinforcements;
    public int DefenderReinforcements;
    public float RiverCrossingStrength;
    public readonly List<int> Attackers = new();
    public readonly List<int> Defenders = new();
    public readonly List<Round> Rounds = new();
}

public static partial class Warfare
{
    public static void Tick(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return;

        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            if (war.FrontDirty || war.LastFrontDay < 0 || campaign.ElapsedDays - war.LastFrontDay >= 5)
            {
                var moved = war.FrontDirty;
                RebuildFronts(campaign);
                UpdateWarMetrics(campaign, war);
                if (moved)
                {
                    FrontPlanner.RefreshAssignments(campaign, war);
                    FrontPlanner.ReassignAi(campaign, war);
                }
            }
            FrontPlanner.UpdateAiExecution(campaign, war);
            ResolveAdvances(campaign, war);
        }

        ResolveBattles(campaign);
        SecureOccupation(campaign);
        campaign.Battles.RemoveAll(battle => battle.Ended && campaign.ElapsedDays - battle.StartedDay > 180);
    }

    private static void ResolveAdvances(Campaign campaign, StrategicWar war)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return;

        foreach (var army in campaign.Armies)
        {
            if ((army.OrderKind != ArmyOrder.AdvanceFront && army.OrderKind != ArmyOrder.Spearhead) || !army.PlanActive ||
                army.AssignedWarId != war.Id ||
                army.InBattle || army.IsMoving || army.ReadyDay > campaign.ElapsedDays ||
                (army.NationId != war.AttackerId && army.NationId != war.DefenderId))
                continue;

            var enemyId = army.NationId == war.AttackerId ? war.DefenderId : war.AttackerId;
            var enemy = campaign.NationById(enemyId);
            if (enemy is null || army.ProvinceId <= 0) continue;
            var candidates = topology.Neighbors[army.ProvinceId]
                .Where(province => controllers[province] == (ushort)enemy.MapIndex)
                .ToArray();
            if (candidates.Length == 0) continue;
            if (army.OrderProvinceId > 0 && candidates.Contains(army.OrderProvinceId))
                candidates = new[] { army.OrderProvinceId };
            if (army.OrderKind == ArmyOrder.Spearhead && army.ObjectiveStateMapIndex >= 0)
            {
                var focused = candidates.Where(province => topology.ProvinceToState[province] == army.ObjectiveStateMapIndex).ToArray();
                if (focused.Length > 0) candidates = focused;
            }
            // The front AI probes weak sectors and prefers dry crossings. Spearheads
            // accept more terrain risk, while regular advances strongly avoid
            // needlessly attacking across a defended river line.
            var terrainWeight = army.OrderKind == ArmyOrder.Spearhead ? 0.38f : 1f;
            var target = candidates
                .OrderBy(province => topology.RiverCrossingStrength(army.ProvinceId, province) * terrainWeight +
                    campaign.Armies
                        .Where(other => other.NationId == enemyId && other.ProvinceId == province && !other.InBattle)
                        .Sum(other => other.Power) * 0.018f)
                .ThenBy(province => province)
                .First();
            var defenders = campaign.Armies.Where(other =>
                other.NationId == enemyId && other.ProvinceId == target && !other.InBattle && other.Divisions >= 0.1f).ToArray();

            if (defenders.Length == 0)
            {
                var riverAdvance = M.Lerp(1f, campaign.Defines.RiverMovementMultiplier,
                    topology.RiverCrossingStrength(army.ProvinceId, target));
                var progress = war.UnopposedProgress.GetValueOrDefault(target) +
                    campaign.Defines.UnopposedCapturePerDay * M.Clamp(army.Power / 4f, 0.35f, 1f) * riverAdvance;
                war.UnopposedProgress[target] = progress;
                if (progress >= 1f) Capture(campaign, war, target, army.NationId, new[] { army }, Array.Empty<Army>());
                continue;
            }

            var battle = campaign.Battles.FirstOrDefault(candidate => !candidate.Ended &&
                candidate.WarId == war.Id && candidate.ProvinceId == target);
            if (battle is null)
            {
                battle = new LandBattle
                {
                    Id = campaign.NextBattleId++,
                    WarId = war.Id,
                    ProvinceId = target,
                    AttackerId = army.NationId,
                    DefenderId = enemyId,
                    StartedDay = campaign.ElapsedDays,
                    RiverCrossingStrength = topology.RiverCrossingStrength(army.ProvinceId, target)
                };
                campaign.Battles.Add(battle);
                foreach (var defending in defenders) Join(battle, defending, false);
            }
            Join(battle, army, true);
        }
    }

    private static void ResolveBattles(Campaign campaign)
    {
        foreach (var battle in campaign.Battles)
        {
            if (battle.Ended) continue;
            var war = campaign.Wars.Find(candidate => candidate.Id == battle.WarId);
            if (war is null || war.Ended) { Close(campaign, battle, -1, "War ended"); continue; }

            ReinforceBattle(campaign, battle);

            foreach (var army in campaign.Armies)
                if (!army.InBattle && army.ProvinceId == battle.ProvinceId &&
                    army.NationId == battle.DefenderId && army.Divisions >= 0.1f)
                    Join(battle, army, false);

            var attacking = Side(campaign, battle.Attackers, battle.AttackerId);
            var defending = Side(campaign, battle.Defenders, battle.DefenderId);
            if (attacking.Count == 0) { Close(campaign, battle, battle.DefenderId, "Attackers withdrew"); continue; }
            if (defending.Count == 0)
            {
                Capture(campaign, war, battle.ProvinceId, battle.AttackerId, attacking, defending);
                Close(campaign, battle, battle.AttackerId, "Province taken unopposed");
                continue;
            }

            var combatWidth = campaign.Defines.CombatWidth;
            var widthPerDivision = campaign.Defines.WidthPerDivision;
            var attackerWantedWidth = attacking.Sum(army => army.Divisions *
                UnitTypeCatalog.Get(army.TemplateId).CombatWidth * widthPerDivision / 2f);
            var defenderWantedWidth = defending.Sum(army => army.Divisions *
                UnitTypeCatalog.Get(army.TemplateId).CombatWidth * widthPerDivision / 2f);
            battle.AttackerWidth = MathF.Min(combatWidth, attackerWantedWidth);
            battle.DefenderWidth = MathF.Min(combatWidth, defenderWantedWidth);
            var attackerEngagement = M.Clamp(battle.AttackerWidth / M.Max(0.01f, attackerWantedWidth), 0.05f, 1f);
            var defenderEngagement = M.Clamp(battle.DefenderWidth / M.Max(0.01f, defenderWantedWidth), 0.05f, 1f);
            var attackerPlanning = WeightedPlanning(attacking);
            var riverAttackMultiplier = battle.RiverCrossingStrength <= 0f ? 1f : M.Lerp(
                campaign.Defines.RiverAttackMultiplier,
                campaign.Defines.MajorRiverAttackMultiplier,
                M.Clamp01((battle.RiverCrossingStrength - 0.45f) / 0.45f));
            var attackerPower = attacking.Sum(army => M.Max(0.01f, army.Power) *
                UnitTypeCatalog.Get(army.TemplateId).Attack * DivisionRules.Attack(army) *
                MilitaryDoctrine.Attack(army.Stance)) *
                attackerEngagement * (1f + attackerPlanning * campaign.Defines.MaxPlanningAttackBonus) * riverAttackMultiplier;
            var defenderPower = defending.Sum(army => M.Max(0.01f, army.Power) *
                UnitTypeCatalog.Get(army.TemplateId).Defense * DivisionRules.Defense(army) *
                MilitaryDoctrine.Defense(army.Stance)) * defenderEngagement;
            var dugIn = defending.Average(army => army.Entrenchment);
            var effectiveDefence = defenderPower * campaign.Defines.DefenderAdvantage *
                                   (1f + dugIn * MilitaryBootstrap.EntrenchmentBonus);
            var ratio = M.Clamp(attackerPower / M.Max(0.01f, effectiveDefence), 0.08f, 12f);
            var defenderOrgLoss = M.Clamp(0.050f * MathF.Sqrt(ratio), 0.012f, 0.13f);
            var attackerOrgLoss = M.Clamp(0.043f / MathF.Sqrt(ratio), 0.012f, 0.13f);
            var defenderLoss = ApplyDamage(defending, defenderOrgLoss * defenderEngagement);
            var attackerLoss = ApplyDamage(attacking, attackerOrgLoss * attackerEngagement);
            foreach (var army in attacking)
                army.Planning = M.Clamp01(army.Planning - campaign.Defines.PlanningBurnPerCombatDay);
            battle.AttackerLosses += attackerLoss;
            battle.DefenderLosses += defenderLoss;

            if (war.AttackerId == battle.AttackerId)
            {
                war.AttackerLosses += attackerLoss;
                war.DefenderLosses += defenderLoss;
            }
            else
            {
                war.AttackerLosses += defenderLoss;
                war.DefenderLosses += attackerLoss;
            }

            var attackerOrg = AverageOrganisation(attacking);
            var defenderOrg = AverageOrganisation(defending);
            battle.Rounds.Add(new LandBattle.Round
            {
                Day = campaign.ElapsedDays,
                AttackerOrganisation = attackerOrg,
                DefenderOrganisation = defenderOrg,
                AttackerDamage = defenderOrgLoss,
                DefenderDamage = attackerOrgLoss,
                AttackerLosses = attackerLoss,
                DefenderLosses = defenderLoss,
                AttackerPower = attackerPower,
                DefenderPower = effectiveDefence,
                AttackerPlanning = attackerPlanning,
                Note = attackerPlanning > 0.65f ? "Prepared offensive plan committed" :
                    battle.RiverCrossingStrength > 0.66f ? "Major river crossing favours the defenders" :
                    battle.RiverCrossingStrength > 0f ? "River crossing slows the assault" :
                    attacking.Count(army => army.Stance == ArmyStance.Aggressive) * 2 >= attacking.Count
                        ? "Aggressive assault accepts heavier losses" :
                    dugIn > 0.55f ? "Defenders are entrenched" :
                    ratio > 1.6f ? "Attacker has local superiority" :
                    ratio < 0.65f ? "Attack is understrength" : "Contested fighting"
            });
            if (battle.Rounds.Count > 90) battle.Rounds.RemoveAt(0);

            if (defenderOrg <= campaign.Defines.BattleBreakOrganisation)
            {
                Capture(campaign, war, battle.ProvinceId, battle.AttackerId, attacking, defending);
                Close(campaign, battle, battle.AttackerId, "Defenders broke");
            }
            else if (attackerOrg <= campaign.Defines.BattleBreakOrganisation)
            {
                foreach (var army in attacking) army.ReadyDay = campaign.ElapsedDays + 7;
                Close(campaign, battle, battle.DefenderId, "Attack spent");
            }
        }
    }

    private static void ReinforceBattle(Campaign campaign, LandBattle battle)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return;
        var adjacent = topology.Neighbors[battle.ProvinceId];
        var attackerAdded = 0;
        var defenderAdded = 0;
        foreach (var army in campaign.Armies.Where(army => !army.InBattle &&
                     army.AssignedWarId == battle.WarId && army.ReadyDay <= campaign.ElapsedDays &&
                     army.Organisation > 0.16f && army.SupplyLevel > 0.20f &&
                     (army.ProvinceId == battle.ProvinceId || adjacent.Contains(army.ProvinceId)))
                 .OrderByDescending(army => army.Power))
        {
            if (army.NationId == battle.AttackerId &&
                (army.OrderKind == ArmyOrder.AdvanceFront || army.OrderKind == ArmyOrder.Spearhead) && attackerAdded < 2)
            {
                Join(battle, army, true);
                attackerAdded++;
                battle.AttackerReinforcements++;
            }
            else if (army.NationId == battle.DefenderId &&
                     (army.OrderKind == ArmyOrder.DefendFront || army.OrderKind == ArmyOrder.ReserveFront) && defenderAdded < 2)
            {
                Join(battle, army, false);
                defenderAdded++;
                battle.DefenderReinforcements++;
            }
        }
    }

    private static void Capture(Campaign campaign, StrategicWar war, int provinceId, int winnerId,
        IReadOnlyCollection<Army> advancing, IReadOnlyCollection<Army> retreating)
    {
        var winner = campaign.NationById(winnerId);
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (winner is null || topology is null || controllers is null) return;
        war.OriginalController.TryAdd(provinceId, controllers[provinceId]);
        if (!campaign.SetProvinceController(provinceId, (ushort)winner.MapIndex)) return;
        war.UnopposedProgress.Remove(provinceId);
        war.FrontDirty = true;
        var center = topology.CenterOf(provinceId);
        foreach (var army in advancing)
        {
            army.ProvinceId = provinceId;
            army.StateMapIndex = topology.ProvinceToState[provinceId];
            army.Latitude = center.Latitude;
            army.Longitude = center.Longitude;
            army.Route.Clear();
            army.MoveProgress = 0f;
            army.Entrenchment = 0f;
            army.ReadyDay = campaign.ElapsedDays + 2;
            if (army.OrderProvinceId == provinceId)
            {
                // A direct RMB attack ends in the selected province instead of
                // silently turning into a perpetual autonomous offensive.
                army.OrderKind = ArmyOrder.Manual;
                army.AssignedWarId = -1;
                army.OrderProvinceId = -1;
                army.OrderStateMapIndex = -1;
                army.ObjectiveStateMapIndex = -1;
                army.PlanActive = false;
            }
        }
        foreach (var army in retreating)
        {
            var owner = campaign.NationById(army.NationId);
            var retreat = owner is null ? 0 : topology.Neighbors[provinceId]
                .FirstOrDefault(candidate => controllers[candidate] == (ushort)owner.MapIndex);
            if (retreat <= 0) continue;
            var fallback = topology.CenterOf(retreat);
            army.ProvinceId = retreat;
            army.StateMapIndex = topology.ProvinceToState[retreat];
            army.Latitude = fallback.Latitude;
            army.Longitude = fallback.Longitude;
            army.Route.Clear();
            army.MoveProgress = 0f;
            army.Entrenchment = 0f;
            army.ReadyDay = campaign.ElapsedDays + 7;
        }
    }

    private static void SecureOccupation(Campaign campaign)
    {
        var controllers = campaign.ProvinceController;
        var occupation = campaign.ProvinceOccupation;
        if (controllers is null || occupation is null) return;
        foreach (var war in campaign.Wars)
        foreach (var pair in war.OriginalController)
        {
            var province = pair.Key;
            if (controllers[province] == pair.Value) { occupation[province] = 0f; continue; }
            var garrison = campaign.Armies.Where(army => army.ProvinceId == province && !army.InBattle).Sum(army => army.Power);
            occupation[province] = M.Clamp01(occupation[province] + campaign.Defines.OccupationDailyProgress *
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
        var attackerMap = (ushort)attacker.MapIndex;
        var defenderMap = (ushort)defender.MapIndex;
        var attackerHome = 0;
        var defenderHome = 0;
        var attackerOccupied = 0;
        var defenderOccupied = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var original = topology.ProvinceToNation[province];
            if (original == attackerMap)
            {
                attackerHome++;
                if (controllers[province] == defenderMap) attackerOccupied++;
            }
            else if (original == defenderMap)
            {
                defenderHome++;
                if (controllers[province] == attackerMap) defenderOccupied++;
            }
        }
        war.OccupationScore = (defenderHome == 0 ? 0f : defenderOccupied * 65f / defenderHome) -
                              (attackerHome == 0 ? 0f : attackerOccupied * 65f / attackerHome);
        var totalLosses = war.AttackerLosses + war.DefenderLosses;
        war.BattleScore = totalLosses < 1f ? 0f :
            (war.DefenderLosses - war.AttackerLosses) / totalLosses * 25f;
        var attackerOccupiedShare = attackerHome == 0 ? 0f : (float)attackerOccupied / attackerHome;
        var defenderOccupiedShare = defenderHome == 0 ? 0f : (float)defenderOccupied / defenderHome;
        var attackerLossBurden = war.AttackerLosses / MathF.Max(50_000f, attacker.Population * 0.035f);
        var defenderLossBurden = war.DefenderLosses / MathF.Max(50_000f, defender.Population * 0.035f);
        war.AttackerWarSupport = M.Clamp01(attacker.WarSupport - attackerLossBurden * 0.52f - attackerOccupiedShare * 0.58f);
        war.DefenderWarSupport = M.Clamp01(defender.WarSupport - defenderLossBurden * 0.52f - defenderOccupiedShare * 0.58f);

        war.ObjectiveScore = 0f;
        if (war.ClaimedStateMapIndex >= 0)
        {
            var goalTotal = 0;
            var goalControlled = 0;
            for (var province = 1; province <= topology.ProvinceCount; province++)
            {
                if (topology.ProvinceToState[province] != war.ClaimedStateMapIndex) continue;
                goalTotal++;
                if (controllers[province] == attackerMap) goalControlled++;
            }
            if (goalTotal > 0)
                war.ObjectiveScore = M.Clamp(-10f + goalControlled * 35f / goalTotal, -10f, 25f);
        }
        war.SupportScore = (war.AttackerWarSupport - war.DefenderWarSupport) * 10f;
        war.WarScore = M.Clamp(war.OccupationScore + war.BattleScore + war.ObjectiveScore + war.SupportScore, -100f, 100f);
    }

    private static float ApplyDamage(IReadOnlyCollection<Army> side, float organisationLoss)
    {
        var totalPower = side.Sum(army => M.Max(0.01f, army.Power));
        var lost = 0f;
        foreach (var army in side)
        {
            var loss = organisationLoss * M.Max(0.01f, army.Power) / totalPower * side.Count;
            army.Organisation = M.Clamp01(army.Organisation - loss);
            army.Entrenchment = M.Clamp01(army.Entrenchment - 0.05f);
            var personnel = army.Divisions * MilitaryBootstrap.DivisionSize * army.Strength;
            var casualties = personnel * loss * 0.018f * MilitaryDoctrine.CasualtyExposure(army.Stance);
            if (personnel > 1f) army.Strength = M.Clamp01(army.Strength - casualties / personnel);
            army.Personnel = Math.Max(0, army.Personnel - (int)MathF.Round(casualties));
            army.Experience = M.Clamp01(army.Experience + loss * 0.004f);
            army.Cohesion = M.Clamp01(army.Cohesion - loss * 0.08f);
            lost += casualties;
        }
        return lost;
    }

    private static float AverageOrganisation(IReadOnlyCollection<Army> side)
    {
        var divisions = side.Sum(army => M.Max(0.01f, army.Divisions));
        return divisions <= 0f ? 0f : side.Sum(army => army.Organisation * M.Max(0.01f, army.Divisions)) / divisions;
    }

    private static float WeightedPlanning(IReadOnlyCollection<Army> side)
    {
        var divisions = side.Sum(army => M.Max(0.01f, army.Divisions));
        return divisions <= 0f ? 0f : side.Sum(army => army.Planning * M.Max(0.01f, army.Divisions)) / divisions;
    }

    private static List<Army> Side(Campaign campaign, IEnumerable<int> ids, int nationId) => ids
        .Select(id => campaign.Armies.Find(army => army.Id == id))
        .Where(army => army is not null && army.NationId == nationId && army.Divisions >= 0.1f)
        .ToList();

    private static void Join(LandBattle battle, Army army, bool attacker)
    {
        var list = attacker ? battle.Attackers : battle.Defenders;
        if (!list.Contains(army.Id)) list.Add(army.Id);
        army.BattleId = battle.Id;
        army.Route.Clear();
        army.MoveProgress = 0f;
    }

    private static void Close(Campaign campaign, LandBattle battle, int winnerId, string outcome)
    {
        battle.Ended = true;
        battle.WinnerId = winnerId;
        battle.Outcome = outcome;
        foreach (var army in campaign.Armies)
            if (army.BattleId == battle.Id) army.BattleId = -1;
    }
}
