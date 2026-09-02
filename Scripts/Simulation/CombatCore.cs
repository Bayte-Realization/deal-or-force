using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

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
    /// <summary>
    /// How land battles have ended this session, by outcome. Not campaign state and not
    /// saved - it exists so the bench and the console can answer "is the defence holding?"
    /// with a number instead of an impression.
    /// </summary>
    public static readonly Dictionary<string, int> OutcomeTally = new(StringComparer.Ordinal);

    public static void Tick(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return;

        // RebuildFronts redraws every front in the world, so asking for it from inside the
        // per-war loop walked the whole map once per stale war. With one scripted war that
        // was one pass; with seventy it was seventy, and it made warfare the most expensive
        // system in the simulation. Decide once whether the map needs redrawing, redraw it
        // once, and then let each war do its own follow-up.
        var stale = new List<StrategicWar>();
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            if (war.FrontDirty || war.LastFrontDay < 0 || campaign.ElapsedDays - war.LastFrontDay >= 5)
                stale.Add(war);
        }

        if (stale.Count > 0)
        {
            var moved = new HashSet<int>();
            foreach (var war in stale) if (war.FrontDirty) moved.Add(war.Id);
            RebuildFronts(campaign);
            foreach (var war in stale)
            {
                UpdateWarMetrics(campaign, war);
                if (!moved.Contains(war.Id)) continue;
                FrontPlanner.RefreshAssignments(campaign, war);
                FrontPlanner.ReassignAi(campaign, war);
            }
        }

        // Two indexes, built once. ResolveAdvances used to walk every division in the world
        // for every war - seventy wars against fourteen hundred divisions - and then walk
        // them again for each province it was weighing up. Both questions are lookups now.
        var byProvince = new Dictionary<int, List<Army>>();
        var byWar = new Dictionary<int, List<Army>>();
        foreach (var army in campaign.Armies)
        {
            if (army.ProvinceId > 0)
            {
                if (!byProvince.TryGetValue(army.ProvinceId, out var here))
                    byProvince[army.ProvinceId] = here = new List<Army>();
                here.Add(army);
            }
            if (army.AssignedWarId < 0) continue;
            if (!byWar.TryGetValue(army.AssignedWarId, out var fighting))
                byWar[army.AssignedWarId] = fighting = new List<Army>();
            fighting.Add(army);
        }

        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            FrontPlanner.UpdateAiExecution(campaign, war);
            ResolveAdvances(campaign, war, byProvince, byWar);
        }

        ResolveBattles(campaign);
        SecureOccupation(campaign);
        campaign.Battles.RemoveAll(battle => battle.Ended && campaign.ElapsedDays - battle.StartedDay > 180);
    }

    private static readonly List<Army> NoArmies = new();

    private static List<Army> In(Dictionary<int, List<Army>> index, int key) =>
        index.TryGetValue(key, out var list) ? list : NoArmies;

    private static void ResolveAdvances(Campaign campaign, StrategicWar war,
        Dictionary<int, List<Army>> byProvince, Dictionary<int, List<Army>> byWar)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return;

        foreach (var army in In(byWar, war.Id))
        {
            if ((army.OrderKind != ArmyOrder.AdvanceFront && army.OrderKind != ArmyOrder.Spearhead) || !army.PlanActive ||
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
                    Power(In(byProvince, province), enemyId) * 0.018f)
                .ThenBy(province => province)
                .First();
            // Everyone standing in the target province, including anyone already fighting
            // there. Excluding divisions that were in a battle meant a province being
            // actively defended looked empty to the next attacker, which walked into it
            // unopposed while the defenders were busy holding it. Ten province captures in
            // eleven came through that hole rather than through winning anything.
            var present = In(byProvince, target)
                .Where(other => other.NationId == enemyId && other.Divisions >= 0.1f).ToArray();

            if (present.Length == 0)
            {
                var riverAdvance = M.Lerp(1f, campaign.Defines.RiverMovementMultiplier,
                    topology.RiverCrossingStrength(army.ProvinceId, target));

                // An empty province is not open ground. The divisions holding the provinces
                // around it cover the gap by fire, which is what keeps a front a front
                // rather than a race: a breach has to be widened and held, not simply
                // strolled through because nobody happened to be standing in it.
                //
                // A gap can only be exploited by a force worth more than what covers it.
                // Everything else has to break the flank first - which is the difference
                // between an offensive and a walk, and the reason the opening front does
                // not move on its own.
                var coveringPower = 0f;
                var covering = 0;
                foreach (var neighbour in topology.Neighbors[target])
                foreach (var other in In(byProvince, neighbour))
                {
                    if (other.NationId != enemyId || other.Divisions < 0.1f) continue;
                    coveringPower += other.Power;
                    covering++;
                }
                if (coveringPower > army.Power * campaign.Defines.GapExploitAdvantage) continue;
                var contested = 1f / (1f + covering * campaign.Defines.ZoneOfControlResistance);

                var progress = war.UnopposedProgress.GetValueOrDefault(target) +
                    campaign.Defines.UnopposedCapturePerDay * M.Clamp(army.Power / 4f, 0.35f, 1f) *
                    riverAdvance * contested;
                war.UnopposedProgress[target] = progress;
                if (progress >= 1f) Capture(campaign, war, target, army.NationId, new[] { army }, Array.Empty<Army>(), "walked into a gap");
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
                foreach (var defending in present) Join(battle, defending, false);
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
                Capture(campaign, war, battle.ProvinceId, battle.AttackerId, attacking, defending,
                    "defenders left the province");
                Close(campaign, battle, battle.AttackerId, "Province taken unopposed");
                continue;
            }

            // ----------------------------------------------------------------------
            // Frontage. A province only has so much room, so numbers past the width
            // wait their turn - which is what lets a smaller, better force hold a line
            // against a larger one. Attacking from several provinces widens the front,
            // the way it does in Hearts of Iron; cramming more than fits is a penalty
            // rather than free strength.
            // ----------------------------------------------------------------------
            var defines = campaign.Defines;
            var directions = attacking.Select(army => army.ProvinceId).Distinct().Count();
            var usableWidth = defines.CombatWidth + defines.CombatWidthPerDirection *
                M.Clamp(directions - 1, 0, M.Max(0, defines.MaxAttackDirections - 1));

            var attackerWantedWidth = WantedWidth(attacking);
            var defenderWantedWidth = WantedWidth(defending);
            battle.AttackerWidth = MathF.Min(usableWidth, attackerWantedWidth);
            battle.DefenderWidth = MathF.Min(defines.CombatWidth, defenderWantedWidth);
            var attackerEngagement = Engagement(battle.AttackerWidth, attackerWantedWidth, usableWidth, defines);
            var defenderEngagement = Engagement(battle.DefenderWidth, defenderWantedWidth, defines.CombatWidth, defines);

            // ----------------------------------------------------------------------
            // What each side is made of. Hardness decides which of an enemy's two
            // attack values it gets to use; armour against piercing decides how much
            // of that lands at all.
            // ----------------------------------------------------------------------
            var attackerHardness = Weighted(attacking, army => UnitTypeCatalog.Get(army.TemplateId).Hardness);
            var defenderHardness = Weighted(defending, army => UnitTypeCatalog.Get(army.TemplateId).Hardness);
            var attackerArmour = Weighted(attacking, army => UnitTypeCatalog.Get(army.TemplateId).Armour);
            var defenderArmour = Weighted(defending, army => UnitTypeCatalog.Get(army.TemplateId).Armour);
            var attackerPiercing = Weighted(attacking, army => UnitTypeCatalog.Get(army.TemplateId).Piercing);
            var defenderPiercing = Weighted(defending, army => UnitTypeCatalog.Get(army.TemplateId).Piercing);
            var attackerPierces = ArmourFactor(attackerPiercing, defenderArmour, defines);
            var defenderPierces = ArmourFactor(defenderPiercing, attackerArmour, defines);

            var attackerPlanning = WeightedPlanning(attacking);
            var riverAttackMultiplier = battle.RiverCrossingStrength <= 0f ? 1f : M.Lerp(
                defines.RiverAttackMultiplier,
                defines.MajorRiverAttackMultiplier,
                M.Clamp01((battle.RiverCrossingStrength - 0.45f) / 0.45f));

            // ----------------------------------------------------------------------
            // Both sides shoot. The attacker's fire is measured against the defender's
            // defence; the defender's return fire is measured against the attacker's
            // breakthrough. That second half is the whole point: a division built to
            // attack survives a counter-attack it would not survive on defence, and a
            // dug-in division survives an assault it could never have launched.
            // ----------------------------------------------------------------------
            var attackerOffence = attacking.Sum(army => M.Max(0.01f, army.Power) *
                    Offence(army, defenderHardness) * DivisionRules.Attack(army) *
                    MilitaryDoctrine.Attack(army.Stance)) *
                attackerEngagement * attackerPierces * riverAttackMultiplier *
                (1f + attackerPlanning * defines.MaxPlanningAttackBonus);

            var defenderOffence = defending.Sum(army => M.Max(0.01f, army.Power) *
                    Offence(army, attackerHardness) * DivisionRules.Attack(army) *
                    MilitaryDoctrine.Attack(army.Stance)) *
                defenderEngagement * defenderPierces * defines.DefenderCounterFire;

            var dugIn = defending.Average(army => army.Entrenchment);
            var defenderProtection = defending.Sum(army => M.Max(0.01f, army.Power) *
                    UnitTypeCatalog.Get(army.TemplateId).Defense * DivisionRules.Defense(army) *
                    MilitaryDoctrine.Defense(army.Stance)) *
                defenderEngagement * defines.DefenderAdvantage *
                (1f + dugIn * defines.EntrenchmentBonus);

            var attackerProtection = attacking.Sum(army => M.Max(0.01f, army.Power) *
                    UnitTypeCatalog.Get(army.TemplateId).Breakthrough * DivisionRules.Defense(army) *
                    MilitaryDoctrine.Defense(army.Stance)) *
                attackerEngagement;

            var pressureOnDefender = M.Clamp(attackerOffence / M.Max(0.01f, defenderProtection), 0.05f, 14f);
            var pressureOnAttacker = M.Clamp(defenderOffence / M.Max(0.01f, attackerProtection), 0.05f, 14f);
            var ratio = pressureOnDefender;

            var defenderOrgLoss = M.Clamp(defines.BaseOrganisationDamage * MathF.Sqrt(pressureOnDefender),
                defines.MinOrganisationDamage, defines.MaxOrganisationDamage);
            var attackerOrgLoss = M.Clamp(defines.BaseOrganisationDamage * MathF.Sqrt(pressureOnAttacker),
                defines.MinOrganisationDamage, defines.MaxOrganisationDamage);
            var defenderLoss = ApplyDamage(campaign, defending, defenderOrgLoss, 1f);
            var attackerLoss = ApplyDamage(campaign, attacking, attackerOrgLoss,
                defines.AttackerCasualtyMultiplier);
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
                AttackerPower = attackerOffence,
                DefenderPower = defenderProtection,
                AttackerPlanning = attackerPlanning,
                Note = attackerPierces < 0.99f ? "Armour is shrugging off the attack" :
                    defenderPierces < 0.99f ? "Defenders cannot pierce the assault" :
                    attackerWantedWidth > usableWidth * 1.35f ? "Too many divisions for the frontage" :
                    directions > 1 ? $"Attacking from {directions} directions widens the front" :
                    attackerPlanning > 0.65f ? "Prepared offensive plan committed" :
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
                Capture(campaign, war, battle.ProvinceId, battle.AttackerId, attacking, defending,
                    "won the battle");
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
        IReadOnlyCollection<Army> advancing, IReadOnlyCollection<Army> retreating, string how)
    {
        OutcomeTally["capture: " + how] = OutcomeTally.GetValueOrDefault("capture: " + how) + 1;
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

    /// <summary>Frontage a side would take if the province had unlimited room.</summary>
    /// <summary>Combined power of one country's divisions in a list of what is standing there.</summary>
    private static float Power(List<Army> present, int nationId)
    {
        var total = 0f;
        foreach (var army in present) if (army.NationId == nationId) total += army.Power;
        return total;
    }

    private static float WantedWidth(IReadOnlyCollection<Army> side) =>
        side.Sum(army => army.Divisions * UnitTypeCatalog.Get(army.TemplateId).CombatWidth);

    /// <summary>
    /// How much of a side is actually in the fight. Divisions past the frontage wait their
    /// turn, and piling far more in than fits starts to cost the ones who are fighting -
    /// men in the way of their own guns.
    /// </summary>
    private static float Engagement(float committed, float wanted, float available, Defines defines)
    {
        var share = M.Clamp(committed / M.Max(0.01f, wanted), 0.05f, 1f);
        var over = M.Max(0f, wanted - available) / M.Max(0.01f, available);
        var crowding = M.Clamp(1f - over * defines.OverWidthPenalty, defines.MinCrowdingFactor, 1f);
        return share * crowding;
    }

    /// <summary>
    /// One division's fire against a target of the given hardness. Soft attack lands on
    /// infantry, hard attack on armour, and a mixed force gets a mixture of both.
    /// </summary>
    private static float Offence(Army army, float targetHardness)
    {
        var template = UnitTypeCatalog.Get(army.TemplateId);
        return template.Attack * M.Lerp(template.SoftAttack, template.HardAttack, M.Clamp01(targetHardness));
    }

    /// <summary>
    /// Armour against piercing. Enough piercing and the shot counts in full; not enough and
    /// half of it bounces. The floor is a define so a mod can make armour decisive or
    /// irrelevant without touching code.
    /// </summary>
    private static float ArmourFactor(float piercing, float armour, Defines defines)
    {
        if (armour <= 0.01f || piercing >= armour) return 1f;
        return M.Lerp(defines.UnpiercedDamageFloor, 1f, M.Clamp01(piercing / armour));
    }

    /// <summary>A division-weighted average of some per-division number.</summary>
    private static float Weighted(IReadOnlyCollection<Army> side, Func<Army, float> select)
    {
        var divisions = side.Sum(army => M.Max(0.01f, army.Divisions));
        return divisions <= 0f ? 0f : side.Sum(army => select(army) * M.Max(0.01f, army.Divisions)) / divisions;
    }

    /// <summary>
    /// Spend a day of fighting on one side. Organisation goes first and men go slowly:
    /// a division that still has organisation is losing its willingness to press, not its
    /// people. Once organisation is gone the same fire starts taking the formation apart,
    /// which is why a broken unit that keeps standing bleeds so much faster than one that
    /// withdraws in order.
    ///
    /// The side doing the assaulting pays more for the same day. That asymmetry is what
    /// makes an attrition war grind down the attacker rather than whoever has fewer men:
    /// crossing open ground into a prepared position has never cost what holding one does.
    /// </summary>
    private static float ApplyDamage(Campaign campaign, IReadOnlyCollection<Army> side,
        float organisationLoss, float casualtyScale)
    {
        var defines = campaign.Defines;
        var totalPower = side.Sum(army => M.Max(0.01f, army.Power));
        var lost = 0f;
        foreach (var army in side)
        {
            var template = UnitTypeCatalog.Get(army.TemplateId);
            var share = M.Max(0.01f, army.Power) / totalPower * side.Count;
            var loss = organisationLoss * share / M.Max(0.2f, template.Organisation);
            var brokenExposure = M.Lerp(defines.BrokenCasualtyMultiplier, 1f,
                M.Clamp01(army.Organisation / M.Max(0.05f, defines.BattleBreakOrganisation * 4f)));

            army.Organisation = M.Clamp01(army.Organisation - loss);

            // A division that holds the same ground goes on digging even while it is being
            // shelled - that is what a prepared line is. Shelling costs it a little more
            // than a day of work is worth only when the fighting is heavy; otherwise the
            // position slowly deepens. Advancing throws all of it away, which is the price
            // of an offensive and the reason a static front is so expensive to move.
            army.Entrenchment = M.Clamp01(army.Entrenchment
                - defines.EntrenchmentLostPerCombatDay
                + defines.EntrenchmentPerDay * defines.EntrenchmentUnderFireShare);

            var personnel = army.Divisions * MilitaryBootstrap.DivisionSize * army.Strength;
            var casualties = personnel * loss * defines.CasualtiesPerOrganisationPoint *
                             brokenExposure * casualtyScale *
                             MilitaryDoctrine.CasualtyExposure(army.Stance);
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
        OutcomeTally[outcome] = OutcomeTally.GetValueOrDefault(outcome) + 1;
        battle.Ended = true;
        battle.WinnerId = winnerId;
        battle.Outcome = outcome;
        foreach (var army in campaign.Armies)
            if (army.BattleId == battle.Id) army.BattleId = -1;
    }
}
