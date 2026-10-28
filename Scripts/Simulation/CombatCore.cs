using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How far one side's day of fighting may run above or below what its numbers say, as a
    /// share: the fortunes of war. 0.18 lets a lucky day hit about a fifth harder and a bad one
    /// a fifth softer, rarely both at once; zero makes every battle a sum, as it was.
    /// </summary>
    public float CombatFortune { get; set; } = 0.18f;

    /// <summary>
    /// The fortune of a whole battle for one side, 1 ± this, triangular, rolled once when it
    /// begins: the weather that holds, the commander who reads the ground, the bridge that was
    /// not blown. A day's fortune averages out over a battle of twenty days; this does not, and
    /// it is what lets an outnumbered side win a battle it should have lost.
    /// </summary>
    public float CombatBattleFortune { get; set; } = 0.22f;

    /// <summary>The share of battles that turn on one decisive moment - a brilliant stroke or a blunder.</summary>
    public float CombatDecisiveChance { get; set; } = 0.06f;

    /// <summary>How far that moment swings the side it falls to, up or down.</summary>
    public float CombatDecisiveSwing { get; set; } = 0.35f;
}

public static partial class Warfare
{
    /// <summary>
    /// How land battles have ended this session, by outcome. Not campaign state and not
    /// saved - it exists so the bench and the console can answer "is the defence holding?"
    /// with a number instead of an impression.
    /// </summary>
    public static readonly Dictionary<string, int> OutcomeTally = new(StringComparer.Ordinal);

    /// <summary>
    /// The same tally split by war. The worldwide one turned out to be no use for judging
    /// any single front: with a hundred and forty wars running, most of them between small
    /// states with no divisions at all, it says far more about how the rest of the world
    /// fights than about the front anybody is looking at.
    /// </summary>
    public static readonly Dictionary<int, Dictionary<string, int>> OutcomeTallyByWar = new();

    private static void Tally(int warId, string outcome)
    {
        OutcomeTally[outcome] = OutcomeTally.GetValueOrDefault(outcome) + 1;
        if (!OutcomeTallyByWar.TryGetValue(warId, out var perWar))
            OutcomeTallyByWar[warId] = perWar = new Dictionary<string, int>(StringComparer.Ordinal);
        perWar[outcome] = perWar.GetValueOrDefault(outcome) + 1;
    }

    // Scratch shared by the whole pass. Warfare made three megabytes of garbage a
    // campaign day, nearly all of it these indexes and the little arrays that LINQ hands
    // back from a Where or an OrderBy - one set per advancing division per day, several
    // hundred times over. They are all overwritten before they are read again, so they are
    // kept and emptied instead of rebuilt. The simulation is single-threaded by
    // construction, because it has to be to stay replayable, so one set is enough.
    private static readonly Dictionary<int, List<Army>> ByWar = new();

    // Who stands in each province, as a slot per province rather than a dictionary: the
    // index is rebuilt every step, and a dictionary rebuilt every step was a hash lookup per
    // division plus an emptying of every list any division had ever stood in. Only the slots
    // filled last time are emptied.
    private static List<Army>[] _byProvince = Array.Empty<List<Army>>();
    private static readonly List<int> ProvincesHeld = new();

    // Divisions by id, for the battles - which resolve once a day, where the index is rebuilt
    // every step. Built when a battle first asks, from the list as the index saw it, so it is
    // the same table the index would have built and no longer built twenty-three times for
    // nothing.
    private static readonly Dictionary<int, Army> ById = new();
    private static readonly List<Army> IndexedArmies = new();
    private static bool _byIdStale = true;
    private static readonly Dictionary<int, StrategicWar> WarById = new();
    private static readonly List<StrategicWar> Stale = new();
    private static readonly HashSet<int> Moved = new();
    private static readonly List<Army> Present = new();
    private static readonly List<Army> AttackingSide = new();
    private static readonly List<Army> DefendingSide = new();
    private static readonly HashSet<ushort> BelligerentMaps = new();

    /// <summary>
    /// Set whenever a province changes hands, because taking one moves the formations that
    /// took it. The province index is only rebuilt when that has actually happened, which
    /// keeps it exactly as truthful as walking every division would be and costs one pass
    /// over the army list instead of one per battle.
    /// </summary>
    private static bool _provinceIndexStale;

    /// <summary>
    /// Which campaign the scratch above was last filled from.
    ///
    /// The note on those indexes says the simulation is single-threaded, so one set is
    /// enough. That is true of one campaign and there are two: a client rebuilding from a
    /// snapshot beside the world it replaces, a bench stepping two peers to prove they
    /// agree. The staleness flag was shared by both of them, so a campaign could find the
    /// index fresh - because the OTHER campaign had just rebuilt it - and read somebody
    /// else's armies out of it.
    ///
    /// It needed a day on which nothing changed hands to leave the flag clear, and a
    /// landing to read the index before warfare's own pass rebuilt it. That is rare: two
    /// peers ran fourteen years in step before it happened once, and then they were apart
    /// by a rounding error in one division and never came back together. A determinism bug
    /// looks exactly like this, which is why the check that found it steps two worlds side
    /// by side rather than trusting that the code is deterministic because it is single
    /// -threaded.
    /// </summary>
    private static Campaign _indexedCampaign;
    private static Campaign _measuredCampaign;

    /// <summary>The index, for the campaign asking - rebuilding it if it is somebody else's.</summary>
    private static void EnsureIndexed(Campaign campaign)
    {
        if (_provinceIndexStale || !ReferenceEquals(_indexedCampaign, campaign)) IndexArmies(campaign);
    }

    // How much ground each country originally owned, and how much of it somebody else is
    // standing on. Every war used to work this out for itself by walking all thirty-eight
    // thousand provinces - with fifty wars going stale on the same day that was two million
    // province reads for a table that is the same table every time.
    private static readonly Dictionary<ushort, int> HomeProvinces = new();
    private static readonly HashSet<ushort> AttackerMaps = new();
    private static readonly HashSet<ushort> DefenderMaps = new();
    private static readonly Dictionary<long, int> OccupiedPairs = new();

    private static void MeasureOccupation(Campaign campaign)
    {
        HomeProvinces.Clear();
        OccupiedPairs.Clear();
        _measuredCampaign = campaign;
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var original = topology.ProvinceToNation[province];
            if (original == 0) continue;
            HomeProvinces[original] = HomeProvinces.GetValueOrDefault(original) + 1;
            var controller = controllers[province];
            if (controller == 0 || controller == original) continue;
            var key = ((long)original << 32) | controller;
            OccupiedPairs[key] = OccupiedPairs.GetValueOrDefault(key) + 1;
        }
    }

    private static int Occupied(ushort original, ushort by) =>
        OccupiedPairs.GetValueOrDefault(((long)original << 32) | by);

    private static void IndexArmies(Campaign campaign)
    {
        InvalidateCoverCache();
        foreach (var province in ProvincesHeld) _byProvince[province].Clear();
        ProvincesHeld.Clear();
        foreach (var list in ByWar.Values) list.Clear();
        IndexedArmies.Clear();
        _byIdStale = true;
        foreach (var army in campaign.Armies)
        {
            IndexedArmies.Add(army);
            var province = army.ProvinceId;
            if (province > 0)
            {
                if (province >= _byProvince.Length)
                    Array.Resize(ref _byProvince, Math.Max(province + 1, (campaign.ProvinceMap?.ProvinceCount ?? 0) + 1));
                var here = _byProvince[province] ??= new List<Army>();
                if (here.Count == 0) ProvincesHeld.Add(province);
                here.Add(army);
            }
            if (army.AssignedWarId < 0) continue;
            if (!ByWar.TryGetValue(army.AssignedWarId, out var fighting))
                ByWar[army.AssignedWarId] = fighting = new List<Army>();
            fighting.Add(army);
        }
        _indexedCampaign = campaign;
        _provinceIndexStale = false;
    }

    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(Warfare))) return;
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return;
        if (campaign.StepOpensNewDay) ReconcileBattleAssignments(campaign);

        // RebuildFronts redraws every front in the world, so asking for it from inside the
        // per-war loop walked the whole map once per stale war. With one scripted war that
        // was one pass; with seventy it was seventy, and it made warfare the most expensive
        // system in the simulation. Decide once whether the map needs redrawing, redraw it
        // once, and then let each war do its own follow-up.
        Stale.Clear();
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            if (war.FrontDirty || war.LastFrontDay < 0 || campaign.ElapsedDays - war.LastFrontDay >= 5)
                Stale.Add(war);
        }

        if (Stale.Count > 0)
        using (SimProfiler.Measure("  war:fronts"))
        {
            Moved.Clear();
            foreach (var war in Stale) if (war.FrontDirty) Moved.Add(war.Id);
            using (SimProfiler.Measure("    fr:rebuild")) RebuildFronts(campaign);
            using (SimProfiler.Measure("    fr:census")) MeasureOccupation(campaign);
            using (SimProfiler.Measure("    fr:metrics"))
                foreach (var war in Stale) UpdateWarMetrics(campaign, war);
            using (SimProfiler.Measure("    fr:refresh"))
                foreach (var war in Stale)
                    FrontPlanner.RefreshAssignments(campaign, war);
            using (SimProfiler.Measure("    fr:reassign"))
                foreach (var war in Stale)
                    if (Moved.Contains(war.Id) || FrontPlanner.NeedsEmergencyStaff(campaign, war))
                        FrontPlanner.ReassignAi(campaign, war);
        }

        // The staff keeps each front covered: every post held before any post is held twice.
        // Once a day for every front, and at once for a front that has just moved, because a
        // line that changed this hour has holes in it this hour.
        using (SimProfiler.Measure("  war:staff"))
        {
            if (campaign.StepOpensNewDay)
            {
                FrontManager.Tick(campaign);
                // And at peace, the borders it has reason to fear (ThreatPosture).
                using (SimProfiler.Measure("  war:posture")) ThreatPosture.Tick(campaign);
                using (SimProfiler.Measure("  war:disposition")) ArmyDisposition.Tick(campaign);
            }
            else
                foreach (var war in Stale)
                    if (Moved.Contains(war.Id)) FrontManager.Balance(campaign, war);
        }

        // Two indexes, built once. ResolveAdvances used to walk every division in the world
        // for every war - seventy wars against fourteen hundred divisions - and then walk
        // them again for each province it was weighing up. Both questions are lookups now.
        using (SimProfiler.Measure("  war:index"))
        {
            IndexArmies(campaign);
            WarById.Clear();
            foreach (var war in campaign.Wars) WarById[war.Id] = war;
        }

        using (SimProfiler.Measure("  war:advances"))
            foreach (var war in campaign.Wars)
            {
                if (war.Ended) continue;
                // A capture in the preceding war invalidates province/war indexes.
                // Refresh BEFORE obtaining its formation list: rebuilding that list
                // from inside the opportunity predicate would mutate an active foreach.
                EnsureIndexed(campaign);
                using (SimProfiler.Measure("    adv:execution")) FrontPlanner.UpdateAiExecution(campaign, war, In(ByWar, war.Id));
                using (SimProfiler.Measure("    adv:resolve")) ResolveAdvances(campaign, war);
            }

        // A round is a DAY of fighting - it carries a date, a note and a line in the battle
        // report - so it resolves on a day rather than on a step. At the shipped hourly step
        // this ran twenty-four times a day: twenty-four days of casualties every day, and a
        // war decided by one side running out of people instead of by the fighting.
        //
        // Gated inside the scope rather than around it, so the profiler still counts one
        // entry per step and the cadence check still reads what it is written to read.
        using (SimProfiler.Measure("  war:battles"))
            if (campaign.StepOpensNewDay) ResolveBattles(campaign);
        using (SimProfiler.Measure("  war:occupation")) SecureOccupation(campaign);
        campaign.Battles.RemoveAll(battle => battle.Ended && campaign.ElapsedDays - battle.StartedDay > 30);
    }

    private static readonly List<Army> NoArmies = new();

    private static List<Army> In(Dictionary<int, List<Army>> index, int key) =>
        index.TryGetValue(key, out var list) ? list : NoArmies;

    /// <summary>Everyone the index has standing in this province, in the order the world lists them.</summary>
    private static List<Army> InProvince(int province) =>
        province > 0 && province < _byProvince.Length && _byProvince[province] is { } here ? here : NoArmies;

    private static Dictionary<int, Army> ArmiesById()
    {
        if (!_byIdStale) return ById;
        ById.Clear();
        foreach (var army in IndexedArmies) ById[army.Id] = army;
        _byIdStale = false;
        return ById;
    }

    private static void ResolveAdvances(Campaign campaign, StrategicWar war)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return;

        // Whose ground counts as the enemy's, per side. In a war with allies in it this is
        // a set rather than one country, and it is the same set for every formation on a
        // side, so it is built once per war rather than once per formation.
        AttackerMaps.Clear();
        DefenderMaps.Clear();
        WarCoalition.FillControllers(campaign, war, true, AttackerMaps);
        WarCoalition.FillControllers(campaign, war, false, DefenderMaps);
        BelligerentMaps.Clear();
        BelligerentMaps.UnionWith(AttackerMaps);
        BelligerentMaps.UnionWith(DefenderMaps);
        _belligerentSnapshot = null;

        foreach (var army in In(ByWar, war.Id))
        {
            if ((army.OrderKind != ArmyOrder.AdvanceFront && army.OrderKind != ArmyOrder.Spearhead) || !army.PlanActive ||
                army.InBattle || army.IsMoving || army.ReadyDay > campaign.ElapsedDays ||
                !war.Involves(army.NationId) || MilitaryAccess.MustReturn(campaign, army))
                continue;

            var enemyMaps = war.IsAttacking(army.NationId) ? DefenderMaps : AttackerMaps;
            var friendlyMaps = war.IsAttacking(army.NationId) ? AttackerMaps : DefenderMaps;
            var enemyId = war.EnemyLeaderOf(army.NationId);
            var enemy = campaign.NationById(enemyId);
            if (enemy is null || army.ProvinceId <= 0) continue;
            bool routed;
            using (SimProfiler.Measure("      adv:route")) routed = PrepareOperationalRoute(campaign, war, army, controllers, friendlyMaps);
            if (routed) continue;
            var target = ChooseAssaultTarget(campaign, topology, war, army, controllers, enemyMaps);
            if (target < 0) continue;
            // Everyone standing in the target province, including anyone already fighting
            // there. Excluding divisions that were in a battle meant a province being
            // actively defended looked empty to the next attacker, which walked into it
            // unopposed while the defenders were busy holding it. Ten province captures in
            // eleven came through that hole rather than through winning anything.
            Present.Clear();
            foreach (var other in InProvince(target))
                if (war.Opposes(army.NationId, other.NationId) && other.Divisions >= 0.1f) Present.Add(other);

            if (Present.Count == 0)
            {
                // Support fire does not occupy empty ground. Wait for an actual battle;
                // an ordinary RMB order can turn this into a march explicitly.
                if (army.SupportOnly) continue;
                var riverAdvance = WaterConditions.MovementMultiplier(campaign, army.ProvinceId, target);
                // An empty province is not open ground. The divisions holding the provinces
                // around it cover the gap by fire, which is what keeps a front a front rather
                // than a race: a breach has to be widened and held, not strolled through
                // because nobody happened to be standing in it.
                //
                // Letting cover merely slow an advance instead of stopping it, and flooring
                // that slowdown at a third, is what turned this front into a walkover: over
                // two years with nobody playing, Ukraine went from losing eight provinces to
                // losing a hundred and nineteen, and gap captures on the front went from six
                // to sixty-one. Cover is a wall to a force smaller than what covers the gap,
                // and no obstacle at all to one that is larger - which is the difference
                // between an offensive and a walk.
                var coveringPower = 0f;
                var covering = 0f;
                Cover(campaign, topology, target, war, army.NationId, ref coveringPower, ref covering);
                // ...but the formation that refuses is one advancing on its own judgement.
                // A player who marched a division here by hand has already decided, with the
                // map in front of them, that this attack is worth making. The simulation gets
                // to make that expensive and slow. It does not get to quietly decline: an
                // order that produces no movement, no battle and no explanation is the single
                // worst thing a strategy game can do, and this one did it every day forever.
                //
                // The measurement that produced the rule above was of the front planner
                // exploiting gaps with nobody playing, so leaving it in force for exactly
                // that case keeps what it bought.
                var ordered = army.DirectOrder;
                var urgent = war.IsDefending(army.NationId) && (war.DefenderOccupiedValue > 0.005f || war.WarScore < -2f);
                var advantage = urgent ? campaign.Defines.GapExploitAdvantage * 2.0f : campaign.Defines.GapExploitAdvantage;
                if (!ordered && coveringPower > PushingOn(campaign, topology, war, army, target) * advantage) continue;
                var contested = 1f / (1f + covering * campaign.Defines.ZoneOfControlResistance);
                // Covered ground still costs: an ordered advance into a fully covered gap runs
                // at a fraction of the pace of one into open country, and the floor is what
                // makes it a fraction rather than a standstill. Without it the resistance term
                // alone turns a week into four months, which the player cannot tell apart from
                // the refusal this replaces.
                if (ordered) contested = M.Max(contested, campaign.Defines.OrderedAdvanceFloor);
                // Ground nobody is within reach to cover is not a gap in a line - it is the
                // country behind one. It is taken at the pace of a march, on the stores and
                // roads it is taken with, rather than at the crawl a covered gap costs: an
                // army through the line used to spend three weeks on each empty district
                // behind it, which read as the whole army stuck.
                var open = covering <= 0f;
                var pace = open
                    ? MathF.Max(campaign.Defines.UnopposedCapturePerDay, campaign.Defines.OpenCountryCapturePerDay)
                    : campaign.Defines.UnopposedCapturePerDay;
                if (ordered) pace = MathF.Max(pace, campaign.Defines.OrderedCapturePerDay);
                var supplyFloor = open
                    ? MathF.Max(campaign.Defines.MarchSupplyFloor, campaign.Defines.OpenCountrySupplyFloor)
                    : campaign.Defines.MarchSupplyFloor;
                if (ordered) supplyFloor = MathF.Max(supplyFloor, campaign.Defines.OrderedCaptureSupplyFloor);
                // Progress belongs to the division, not a pooled province counter: a
                // hundred divisions must not teleport one another across the border.
                army.MoveProgress += pace * campaign.TickScale *
                    UnitTypeCatalog.Get(army.TemplateId).Speed * Researched(campaign, army, "army_speed") *
                    MilitaryDoctrine.Movement(army.Stance, campaign.Defines) *
                    M.Clamp(army.SupplyLevel, supplyFloor, 1f) *
                    topology.TerrainAt(target).MovementSpeed * riverAdvance * contested * MovementPace.Roads(campaign, target);
                war.UnopposedProgress[target] = MathF.Max(war.UnopposedProgress.GetValueOrDefault(target), army.MoveProgress);
                if (army.MoveProgress >= 1f)
                    Capture(campaign, war, target, army.NationId, new[] { army }, Array.Empty<Army>(), "walked into a gap");
                continue;
            }

            // Reinforcements already waited for organisation and stores; the formation
            // initiating the same assault did not. Apply one live minimum to both paths.
            // This comes after empty-ground movement so a hungry formation can still
            // march out of a pocket or take undefended ground. Explicit direct orders
            // keep the player's choice to accept risk, rather than silently refusing it.
            if (!AssaultReadiness.Assess(campaign, army).Ready) continue;

            // Found by hand rather than by FirstOrDefault: the lambda captures the war and
            // the target, so it was a fresh closure and a fresh enumerator for every
            // division that advanced anywhere in the world, every day.
            LandBattle battle = null;
            foreach (var candidate in campaign.Battles)
            {
                if (candidate.Ended || candidate.WarId != war.Id || candidate.ProvinceId != target) continue;
                battle = candidate;
                break;
            }
            if (battle is null)
            {
                battle = new LandBattle
                {
                    Id = campaign.NextBattleId++,
                    WarId = war.Id,
                    ProvinceId = target,
                    AttackerId = army.NationId,
                    DefenderId = Present[0].NationId,
                    StartedDay = campaign.ElapsedDays,
                    RiverCrossingStrength = topology.RiverCrossingStrength(army.ProvinceId, target)
                };
                campaign.Battles.Add(battle);
                foreach (var defending in Present) Join(battle, defending, false);
            }
            Join(battle, army, true);
        }
    }

    /// <summary>
    /// An army has just come off a ship onto somebody else's beach. An undefended coast
    /// changes hands; a defended one is a battle, fought by the ordinary combat model with
    /// the landing party as the attacker - which is the point, because an amphibious
    /// assault should be a fight a player already knows how to read.
    /// </summary>
    public static string LandInvasion(Campaign campaign, StrategicWar war, int provinceId,
        IReadOnlyList<Army> landed)
    {
        if (landed.Count == 0) return "nothing came ashore";
        var controllers = campaign.ProvinceController;
        if (controllers is null) return "no map";

        EnsureIndexed(campaign);
        var invaderId = landed[0].NationId;

        DefendingSide.Clear();
        foreach (var army in InProvince(provinceId))
            if (war.Opposes(invaderId, army.NationId) && army.Divisions >= 0.1f) DefendingSide.Add(army);

        if (DefendingSide.Count == 0)
        {
            Capture(campaign, war, provinceId, invaderId, landed, Array.Empty<Army>(), "came ashore unopposed");
            return $"{landed.Count:N0} formation(s) landed against no opposition and took the province";
        }

        var battle = campaign.Battles.Find(candidate => !candidate.Ended &&
            candidate.WarId == war.Id && candidate.ProvinceId == provinceId);
        if (battle is null)
        {
            battle = new LandBattle
            {
                Id = campaign.NextBattleId++,
                WarId = war.Id,
                ProvinceId = provinceId,
                AttackerId = invaderId,
                DefenderId = DefendingSide[0].NationId,
                StartedDay = campaign.ElapsedDays
            };
            campaign.Battles.Add(battle);
            foreach (var defending in DefendingSide) Join(battle, defending, false);
        }
        foreach (var army in landed) Join(battle, army, true);
        _provinceIndexStale = true;
        return $"{landed.Count:N0} formation(s) landed into a contested beach";
    }

    /// <summary>One side's fortune for today's fighting: the day's, 1 ± Defines.CombatFortune, triangular, times the battle's; from the seed.</summary>
    private static float Fortune(Campaign campaign, LandBattle battle, int side)
    {
        var spread = M.Clamp(campaign.Defines.CombatFortune, 0f, 0.6f);
        if (spread <= 0f) return 1f;
        var first = Escalation.Roll(campaign.Seed, battle.Id, side, campaign.ElapsedDays, 0x6F7);
        var second = Escalation.Roll(campaign.Seed, battle.Id, side, campaign.ElapsedDays, 0x6F8);
        var today = 1f + spread * (first + second - 1f);
        // And the battle's own fortune, the same every day of it (Defines.CombatBattleFortune),
        // and now and then the one moment the whole battle turns on (CombatDecisiveChance).
        var battleSpread = M.Clamp(campaign.Defines.CombatBattleFortune, 0f, 0.6f);
        var whole = 1f + battleSpread * (Escalation.Roll(campaign.Seed, battle.Id, side, battle.StartedDay, 0x6F9) +
                                         Escalation.Roll(campaign.Seed, battle.Id, side, battle.StartedDay, 0x6FA) - 1f);
        if (Escalation.Roll(campaign.Seed, battle.Id, side, battle.StartedDay, 0x6FB) < M.Clamp01(campaign.Defines.CombatDecisiveChance))
        {
            var swing = M.Clamp(campaign.Defines.CombatDecisiveSwing, 0f, 0.9f);
            whole *= Escalation.Roll(campaign.Seed, battle.Id, side, battle.StartedDay, 0x6FC) < 0.5f ? 1f - swing : 1f + swing;
        }
        return today * whole;
    }

    private static void ResolveBattles(Campaign campaign)
    {
        foreach (var battle in campaign.Battles)
        {
            if (battle.Ended) continue;
            WarById.TryGetValue(battle.WarId, out var war);
            if (war is null || war.Ended) { Close(campaign, battle, -1, "War ended"); continue; }

            ReinforceBattle(campaign, battle);

            // Whoever is standing in the province and not already committed joins the
            // defence. This used to walk every division in the world once per battle -
            // three hundred and sixty battles against fourteen hundred divisions, half a
            // million comparisons a day - to answer a question the province index already
            // answers. The index is refreshed only after a capture has moved somebody, so
            // it says exactly what the walk said.
            EnsureIndexed(campaign);
            foreach (var army in InProvince(battle.ProvinceId))
                if (!army.InBattle && WarCoalition.SameSide(war, army.NationId, battle.DefenderId) && army.Divisions >= 0.1f)
                    Join(battle, army, false);

            var attacking = Side(war, battle.Attackers, battle.AttackerId, AttackingSide);
            var defending = Side(war, battle.Defenders, battle.DefenderId, DefendingSide);
            if (attacking.Count == 0) { Close(campaign, battle, battle.DefenderId, "Attackers withdrew"); continue; }
            if (defending.Count == 0)
            {
                var occupied = Capture(campaign, war, battle.ProvinceId, battle.AttackerId, attacking, defending,
                    "defenders left the province");
                Close(campaign, battle, battle.AttackerId, occupied ? "Province taken unopposed" : "Support ended; province not occupied");
                continue;
            }

            // ----------------------------------------------------------------------
            // Frontage. A province only has so much room, so numbers past the width
            // wait their turn - which is what lets a smaller, better force hold a line
            // against a larger one. Attacking from several provinces widens the front,
            // the way it does in Hearts of Iron.
            //
            // This is a filter now, and it used to be a multiplier. The multiplier scaled
            // a whole side's fire by the share of it that fitted, which meant nineteen
            // divisions in a province all fought, all took losses and all counted as
            // being in the battle, while the fifth one made the first four shoot worse
            // than four would have. What is past the width is simply not in the fight: it
            // stands in reserve, takes the place of whoever is spent, and the next day's
            // split decides that again.
            // ----------------------------------------------------------------------
            var defines = campaign.Defines;
            var terrain = campaign.ProvinceMap?.TerrainAt(battle.ProvinceId) ?? TerrainCatalog.Get("plains");
            var baseWidth = defines.CombatWidth * terrain.CombatWidth;
            var directions = attacking.Select(army => army.ProvinceId).Distinct().Count();
            var usableWidth = baseWidth + defines.CombatWidthPerDirection * terrain.CombatWidth *
                M.Clamp(directions - 1, 0, M.Max(0, defines.MaxAttackDirections - 1));

            var attackerWantedWidth = WantedWidth(attacking);
            FrontageReady(attacking, usableWidth, AttackerLine, AttackerReserve, defines.BattleBreakOrganisation);
            FrontageReady(defending, baseWidth, DefenderLine, DefenderReserve, defines.BattleBreakOrganisation);
            battle.LastAttackerLine.Clear(); battle.LastDefenderLine.Clear();
            foreach (var army in AttackerLine) battle.LastAttackerLine.Add(army.Id);
            foreach (var army in DefenderLine) battle.LastDefenderLine.Add(army.Id);

            // The ground each side actually holds, and how many formations hold it. The
            // report reads these; everything below is fought over the line rather than over
            // the province, so a reserve shoots at nothing and is shot at by almost nothing.
            battle.AttackerWidth = WantedWidth(AttackerLine);
            battle.DefenderWidth = WantedWidth(DefenderLine);
            battle.AttackerInLine = AttackerLine.Count;
            battle.DefenderInLine = DefenderLine.Count;
            var attackerEngagement = OverWidth(battle.AttackerWidth, usableWidth, defines);
            var defenderEngagement = OverWidth(battle.DefenderWidth, baseWidth, defines);

            // ----------------------------------------------------------------------
            // What each side is made of. Hardness decides which of an enemy's two
            // attack values it gets to use; armour against piercing decides how much
            // of that lands at all.
            // ----------------------------------------------------------------------
            var attackerHardness = Weighted(AttackerLine, army => UnitTypeCatalog.Get(army.TemplateId).Hardness);
            var defenderHardness = Weighted(DefenderLine, army => UnitTypeCatalog.Get(army.TemplateId).Hardness);
            var attackerArmour = Weighted(AttackerLine, army => UnitTypeCatalog.Get(army.TemplateId).Armour);
            var defenderArmour = Weighted(DefenderLine, army => UnitTypeCatalog.Get(army.TemplateId).Armour);
            var attackerPiercing = Weighted(AttackerLine, army => UnitTypeCatalog.Get(army.TemplateId).Piercing);
            var defenderPiercing = Weighted(DefenderLine, army => UnitTypeCatalog.Get(army.TemplateId).Piercing);
            var attackerPierces = ArmourFactor(attackerPiercing, defenderArmour, defines);
            var defenderPierces = ArmourFactor(defenderPiercing, attackerArmour, defines);

            var attackerPlanning = WeightedPlanning(AttackerLine);
            var riverAttackMultiplier = WaterConditions.AttackMultiplier(campaign, battle.RiverCrossingStrength, battle.ProvinceId);

            // ----------------------------------------------------------------------
            // Both sides shoot. The attacker's fire is measured against the defender's
            // defence; the defender's return fire is measured against the attacker's
            // breakthrough. That second half is the whole point: a division built to
            // attack survives a counter-attack it would not survive on defence, and a
            // dug-in division survives an assault it could never have launched.
            // ----------------------------------------------------------------------
            // The long-range forces over the ground: artillery, attack aircraft, drones,
            // the sweep overhead. Reaches the battle from bases within range, saturating,
            // and the enemy's sky and missile brigades take their cut of whatever the
            // other side managed to contribute - a wing over a defended front pays first.
            // Support is asked of the countries in the battle, not of the formations in the
            // line: a wing or a gun brigade is based far behind the province and reaches the
            // battle whoever happens to be standing in the front rank today.
            var attackerNations = new HashSet<int>(attacking.Select(army => army.NationId));
            var defenderNations = new HashSet<int>(defending.Select(army => army.NationId));
            var (attackerFire, defenderFire, defenderStripped, attackerStripped,
                    attackerSupportShown, defenderSupportShown) =
                SupportCore.Engagement(campaign, battle.ProvinceId, attackerNations, defenderNations);
            battle.AttackerSupport = attackerSupportShown;
            battle.DefenderSupport = defenderSupportShown;

            var attackerOffence = AttackerLine.Sum(army => M.Max(0.01f, army.Power) *
                    Offence(army, defenderHardness) * DivisionRules.Attack(army) *
                    MilitaryDoctrine.Attack(army.Stance) * Researched(campaign, army, "army_attack") *
                    StrategicStockpiles.CombatMunitionsFactor(campaign, campaign.NationById(army.NationId))) *
                attackerEngagement * attackerPierces * riverAttackMultiplier * terrain.Attack *
                (1f + attackerPlanning * defines.MaxPlanningAttackBonus) * attackerFire;

            var defenderOffence = DefenderLine.Sum(army => M.Max(0.01f, army.Power) *
                    Offence(army, attackerHardness) * DivisionRules.Attack(army) *
                    MilitaryDoctrine.Attack(army.Stance) * Researched(campaign, army, "army_attack") *
                    StrategicStockpiles.CombatMunitionsFactor(campaign, campaign.NationById(army.NationId))) *
                defenderEngagement * defenderPierces * defines.DefenderCounterFire * defenderFire;

            var dugIn = DefenderLine.Average(army => army.Entrenchment);
            var attackerSupply = Weighted(AttackerLine, army => M.Clamp01(army.SupplyLevel));
            var defenderSupply = Weighted(DefenderLine, army => M.Clamp01(army.SupplyLevel));
            var attackerOrgBefore = AverageOrganisation(AttackerLine);
            var defenderOrgBefore = AverageOrganisation(DefenderLine);

            var isHomeProvince = campaign.ProvinceMap?.ProvinceToNation != null &&
                battle.ProvinceId > 0 && battle.ProvinceId < campaign.ProvinceMap.ProvinceToNation.Length &&
                campaign.ProvinceMap.ProvinceToNation[battle.ProvinceId] == battle.DefenderId;

            var defenderNation = campaign.NationById(battle.DefenderId);
            var isCapitalState = defenderNation != null && defenderNation.CapitalStateMapIndex >= 0 &&
                campaign.ProvinceMap?.ProvinceToState != null &&
                battle.ProvinceId > 0 && battle.ProvinceId < campaign.ProvinceMap.ProvinceToState.Length &&
                campaign.ProvinceMap.ProvinceToState[battle.ProvinceId] == defenderNation.CapitalStateMapIndex;

            var homelandBonus = isHomeProvince ? defines.HomelandDefenseBonus : 1f;
            if (isCapitalState) homelandBonus *= defines.CapitalDefenseBonus;
            var frontalBonus = directions == 1 ? defines.FrontalDefenseBonus : 1f;

            var effectiveDefenderStripped = M.Lerp(defenderStripped, 1f, dugIn * defines.EntrenchmentShellingResistance);

            var defenderProtection = DefenderLine.Sum(army => M.Max(0.01f, army.Power) *
                    UnitTypeCatalog.Get(army.TemplateId).Defense * DivisionRules.Defense(army) *
                    MilitaryDoctrine.Defense(army.Stance) * Researched(campaign, army, "army_defense")) *
                defenderEngagement * defines.DefenderAdvantage * terrain.Defense *
                (1f + dugIn * defines.EntrenchmentBonus) * effectiveDefenderStripped *
                homelandBonus * frontalBonus;

            var attackerProtection = AttackerLine.Sum(army => M.Max(0.01f, army.Power) *
                    UnitTypeCatalog.Get(army.TemplateId).Breakthrough * DivisionRules.Defense(army) *
                    MilitaryDoctrine.Defense(army.Stance) * Researched(campaign, army, "army_defense")) *
                attackerEngagement * attackerStripped;

            // The fortunes of war (Defines.CombatFortune): each side's day runs a little above or
            // below what its numbers say - the weather, a commander's call, a bridge that held -
            // in its fire and in its staying power alike. Drawn from the campaign seed, the battle
            // and the day, so a seed replays its war exactly and another seed fights it otherwise;
            // centred on one, so fortune decides battles without tilting the world.
            var attackerFortune = Fortune(campaign, battle, 1);
            var defenderFortune = Fortune(campaign, battle, 2);
            attackerOffence *= attackerFortune; attackerProtection *= attackerFortune;
            defenderOffence *= defenderFortune; defenderProtection *= defenderFortune;

            var pressureOnDefender = M.Clamp(attackerOffence / M.Max(0.01f, defenderProtection), 0.05f, 14f);
            var pressureOnAttacker = M.Clamp(defenderOffence / M.Max(0.01f, attackerProtection), 0.05f, 14f);
            var ratio = pressureOnDefender;

            var terrainBreakthrough = 1f;
            if (string.Equals(terrain.Id, "urban", StringComparison.OrdinalIgnoreCase))
                terrainBreakthrough = defines.UrbanBreakthroughPenalty;
            else if (string.Equals(terrain.Id, "mountains", StringComparison.OrdinalIgnoreCase))
                terrainBreakthrough = 0.60f;
            else if (string.Equals(terrain.Id, "marsh", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(terrain.Id, "jungle", StringComparison.OrdinalIgnoreCase))
                terrainBreakthrough = 0.75f;

            var isDesperateDefense = isHomeProvince && defenderOrgBefore < 0.38f && defenderOrgBefore > defines.BattleBreakOrganisation;

            // A prepared, supplied attack can unhinge a line; a dug-in, supplied defence can
            // turn a spent one back. Both read only what the battle already knows.
            var breakthrough = CombatTempo.Breakthrough(defines, pressureOnDefender, attackerPlanning, attackerSupply,
                dugIn, directions, terrainBreakthrough);
            var counterattack = CombatTempo.Counterattack(defines, pressureOnAttacker, dugIn, defenderSupply,
                attackerOrgBefore, defenderOrgBefore, isDesperateDefense);

            var defenderOrgLoss = M.Clamp(defines.BaseOrganisationDamage * MathF.Sqrt(pressureOnDefender),
                defines.MinOrganisationDamage, defines.MaxOrganisationDamage);
            var attackerOrgLoss = M.Clamp(defines.BaseOrganisationDamage * MathF.Sqrt(pressureOnAttacker),
                defines.MinOrganisationDamage, defines.MaxOrganisationDamage);
            if (breakthrough > 0f)
            {
                defenderOrgLoss *= 1f + breakthrough * defines.BreakthroughOrganisationBonus;
                attackerOrgLoss *= 1f - breakthrough * defines.BreakthroughAttackerRelief;
            }
            if (counterattack > 0f)
            {
                attackerOrgLoss *= 1f + counterattack * defines.CounterattackOrganisationBonus;
                defenderOrgLoss *= 1f - counterattack * defines.CounterattackDefenderRelief;
            }
            // However badly a battle goes, it is still two sides shooting at each other: the
            // worse-off side's daily loss per formation is at most this many times the other's.
            // The bounds above allowed seventeen to one, and ten years of it put seven million
            // North Korean casualties against ninety thousand South Korean ones.
            var exchange = MathF.Max(1f, defines.MaxOrganisationLossRatio);
            attackerOrgLoss = MathF.Min(attackerOrgLoss, defenderOrgLoss * exchange);
            defenderOrgLoss = MathF.Min(defenderOrgLoss, attackerOrgLoss * exchange);
            // And the same for the dead. Thin templates, a broken line and an aggressive stance
            // each multiply a side's casualties after the cap above, so the share of its men a
            // side loses in a day is held to at most this many times the other side's share.
            var defenderScale = 1f;
            var attackerScale = defines.AttackerCasualtyMultiplier;
            var (defenderMen, defenderDead) = CasualtyTotals(campaign, DefenderLine, defenderOrgLoss, defenderScale);
            var (attackerMen, attackerDead) = CasualtyTotals(campaign, AttackerLine, attackerOrgLoss, attackerScale);
            var defenderRate = defenderMen > 1f ? defenderDead / defenderMen : 0f;
            var attackerRate = attackerMen > 1f ? attackerDead / attackerMen : 0f;
            var bleed = MathF.Max(1f, defines.MaxCasualtyRateRatio);
            if (attackerRate > defenderRate * bleed && attackerRate > 0f)
            {
                var cut = defenderRate * bleed / attackerRate;
                attackerScale *= cut; attackerDead *= cut;
            }
            else if (defenderRate > attackerRate * bleed && defenderRate > 0f)
            {
                var cut = attackerRate * bleed / defenderRate;
                defenderScale *= cut; defenderDead *= cut;
            }
            // And in men, not only in shares. What a side can kill is bounded by what is
            // shooting at it, however many stand in front of it: in one bench war China lost
            // twenty-seven men for each Burmese one. The frontage filter does most of that
            // work now, since a stack can only bring its line to bear, but the bound stays:
            // a line of four against one is still four times the men.
            var trade = MathF.Max(1f, defines.MaxCasualtyExchange);
            if (defenderDead > 0f && attackerDead > defenderDead * trade)
                attackerScale *= defenderDead * trade / attackerDead;
            else if (attackerDead > 0f && defenderDead > attackerDead * trade)
                defenderScale *= attackerDead * trade / defenderDead;
            var defenderLoss = ApplyDamage(campaign, DefenderLine, defenderOrgLoss, defenderScale, out var defenderKilled);
            var attackerLoss = ApplyDamage(campaign, AttackerLine, attackerOrgLoss, attackerScale, out var attackerKilled);
            // The reserve is behind the line, not out of the war: the shelling and the
            // mortars that miss the front find the queue standing in the same province. It
            // costs a share (Defines.ReserveExposure), because nothing in that queue is
            // answering back - which is why nineteen divisions waiting are worth far less
            // than nineteen divisions in the line.
            if (DefenderReserve.Count > 0)
            {
                defenderLoss += ApplyDamage(campaign, DefenderReserve,
                    defenderOrgLoss * defines.ReserveExposure, defenderScale, out var defenderReserveKilled);
                defenderKilled += defenderReserveKilled;
            }
            if (AttackerReserve.Count > 0)
            {
                attackerLoss += ApplyDamage(campaign, AttackerReserve,
                    attackerOrgLoss * defines.ReserveExposure, attackerScale, out var attackerReserveKilled);
                attackerKilled += attackerReserveKilled;
            }
            // Planning is spent by the formations that attacked, and only by them: a reserve
            // that never left its start line still has its plan.
            foreach (var army in AttackerLine)
                army.Planning = M.Clamp01(army.Planning - campaign.Defines.PlanningBurnPerCombatDay);
            battle.AttackerLosses += attackerLoss;
            battle.DefenderLosses += defenderLoss;
            // And the ground they fought over: a day of battle wrecks a share of the region.
            WarDamage.Fought(campaign, battle.ProvinceId, attackerLoss + defenderLoss);

            // By side rather than by leader. This compared the battle's attacker with the
            // war's, so an ally's assault in a coalition war put each side's dead on the other.
            WarLosses.Record(war, battle.AttackerId, attackerKilled, attackerLoss - attackerKilled, 0f);
            WarLosses.Record(war, battle.DefenderId, defenderKilled, defenderLoss - defenderKilled, 0f);

            var attackerOrg = AverageOrganisation(AttackerLine);
            var defenderOrg = AverageOrganisation(DefenderLine);
            if (DiagnosticLogs.Enabled)
                DiagnosticLogs.Write("simulation", $"BATTLE_ROUND battle={battle.Id} war={battle.WarId} province={battle.ProvinceId} attacker={battle.AttackerId} defender={battle.DefenderId} attack_power={DiagnosticLogs.N(attackerOffence)} defense_power={DiagnosticLogs.N(defenderProtection)} attack_org={DiagnosticLogs.N(attackerOrg)} defense_org={DiagnosticLogs.N(defenderOrg)} attack_losses={DiagnosticLogs.N(attackerLoss)} defense_losses={DiagnosticLogs.N(defenderLoss)} river={DiagnosticLogs.N(battle.RiverCrossingStrength)} planning={DiagnosticLogs.N(attackerPlanning)}");
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
                AttackerOrganisationSpent = M.Max(0f, attackerOrgBefore - attackerOrg),
                HasOrganisationTelemetry = true,
                DefenderOrganisationSpent = M.Max(0f, defenderOrgBefore - defenderOrg),
                Note = breakthrough > .45f ? "Breakthrough: the prepared offensive is unhinging the line" :
                    counterattack > .45f ? (isDesperateDefense ? "Desperate counter-attack: defenders thrust into overextended assault" : "Counter-attack: the defence is punishing a spent assault") :
                    attackerPierces < 0.99f ? "Armour is shrugging off the attack" :
                    defenderPierces < 0.99f ? "Defenders cannot pierce the assault" :
                    attackerWantedWidth > usableWidth * 1.35f ? "Too many divisions for the frontage" :
                    directions > 1 ? $"Attacking from {directions} directions widens the front" :
                    directions == 1 && dugIn > 0.5f ? "Entrenched defenders hold the narrow frontal approach" :
                    isCapitalState && isHomeProvince ? "Stubborn last stand for the capital" :
                    isHomeProvince && dugIn > 0.4f ? "Defenders stubbornly hold their homeland" :
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

            if (defenderOrg <= campaign.Defines.BattleBreakOrganisation &&
                !DefenderReserve.Any(army => army.Organisation > campaign.Defines.BattleBreakOrganisation))
            {
                var occupied = Capture(campaign, war, battle.ProvinceId, battle.AttackerId, attacking, defending,
                    "won the battle");
                Close(campaign, battle, battle.AttackerId, occupied ? "Defenders broke" : "Defenders broke; support did not occupy");
            }
            else if (attackerOrg <= campaign.Defines.BattleBreakOrganisation &&
                !AttackerReserve.Any(army => army.Organisation > campaign.Defines.BattleBreakOrganisation))
            {
                var extraRest = counterattack > 0.30f ? 2 : 0;
                foreach (var army in attacking)
                {
                    army.ReadyDay = campaign.ElapsedDays + campaign.Defines.RestAfterSpentAttackDays + extraRest;
                    if (counterattack > 0.30f)
                    {
                        army.Planning = 0f;
                        army.Cohesion = M.Clamp01(army.Cohesion - 0.12f);
                    }
                }
                var repelled = counterattack > 0.30f;
                var outcomeMsg = repelled ? "Attack repelled by counter-attack" : "Attack spent";
                Close(campaign, battle, battle.DefenderId, outcomeMsg);
                // A repelled assault does not loiter at the gates: the counter-attack
                // shoves the spent formations back across the line they came from, which
                // is what turns a defended province into a recovered one instead of a
                // siege that resumes the next morning. A merely exhausted attack stops
                // where it stands.
                if (repelled)
                {
                    var map = campaign.ProvinceMap;
                    var controls = campaign.ProvinceController;
                    if (map is not null && controls is not null)
                        foreach (var army in attacking)
                        {
                            if (army.ProvinceId != battle.ProvinceId) continue;
                            var back = WarCoalition.RetreatProvince(campaign, war, army, battle.ProvinceId, lowestId: true);
                            if (back <= 0 || back == battle.ProvinceId) continue;
                            army.ProvinceId = back;
                            army.StateMapIndex = map.ProvinceToState[back];
                            (army.Latitude, army.Longitude) = map.CenterOf(back);
                        }
                    _provinceIndexStale = true;
                    EnsureIndexed(campaign);
                }
            }
        }
    }

    private static readonly List<Army> Reinforcements = new();

    /// <summary>Everyone standing in one province who could still join the fight next door.</summary>
    private static void Consider(Campaign campaign, LandBattle battle, int provinceId)
    {
        foreach (var army in InProvince(provinceId))
        {
            if (army.InBattle || army.AssignedWarId != battle.WarId ||
                battle.WithdrawnAttackers.Contains(army.Id) || battle.WithdrawnDefenders.Contains(army.Id) ||
                army.ReadyDay > campaign.ElapsedDays) continue;
            Reinforcements.Add(army);
        }
    }

    private static void ReinforceBattle(Campaign campaign, LandBattle battle)
    {
        if (campaign.ProvinceMap is null) return;
        var war = campaign.Wars.Find(candidate => candidate.Id == battle.WarId && !candidate.Ended);
        if (war is null) return;
        EnsureIndexed(campaign);
        var attackerAdded = 0;
        var defenderAdded = 0;

        // Reinforcements come from the province being fought over, and from nowhere else.
        //
        // A formation standing in a province next door used to join the battle there without
        // marching - two a day, every day, for as long as the fight lasted. Nine days into an
        // assault the defender of Kharkiv was nineteen formations strong in a province that
        // had held one, and the attacker paid a day's casualties for the privilege of
        // shooting into a queue that refilled itself behind his back.
        //
        // Help still arrives: it marches into the province and joins on the day it gets
        // there, which is the road the attacker has to take as well. What is gone is the free
        // passage into somebody else's battle. It also keeps this pass cheap - one province
        // index instead of the province and each of its neighbours.
        Reinforcements.Clear();
        Consider(campaign, battle, battle.ProvinceId);

        // Strongest first, and by id where two are equally strong: a battle joined in a
        // different order on two machines is a battle that ends differently on them.
        Reinforcements.Sort(static (left, right) =>
        {
            var byPower = right.Power.CompareTo(left.Power);
            return byPower != 0 ? byPower : left.Id.CompareTo(right.Id);
        });

        foreach (var army in Reinforcements)
        {
            // A formation under a direct order joins the battle it was sent to and no
            // other - being dragged into a fight next door is exactly what a player who
            // pointed at a province did not ask for.
            //
            // A marching formation may still support the neighbouring assault, but an
            // explicitly paused offensive and a formation below the live assault minimum
            // are not free reserves for it. The same readout gates starting and joining,
            // rather than allowing a second path to override the staff's waiting decision.
            var assessment = AssaultReadiness.Assess(campaign, army);
            var attackerReady = assessment.Ready ||
                (assessment.Reason == AssaultReadiness.Reason.PlanPaused &&
                 army.FrontLineId < 0 && army.Planning > 0f &&
                 campaign.NationById(army.NationId)?.IsHuman != true &&
                 assessment.Organisation > assessment.MinimumOrganisation &&
                 assessment.Supply > assessment.MinimumSupply);
            if (WarCoalition.SameSide(war, army.NationId, battle.AttackerId) &&
                (army.OrderKind == ArmyOrder.AdvanceFront || army.OrderKind == ArmyOrder.Spearhead) &&
                attackerReady &&
                (!army.DirectOrder ||
                 (army.PlannedRoute.Count > 0 ? army.PlannedRoute[0] : army.OrderProvinceId) == battle.ProvinceId) &&
                attackerAdded < 2)
            {
                Join(battle, army, true);
                attackerAdded++;
                battle.AttackerReinforcements++;
            }
            else if (WarCoalition.SameSide(war, army.NationId, battle.DefenderId) &&
                     (army.OrderKind == ArmyOrder.DefendFront || army.OrderKind == ArmyOrder.ReserveFront) &&
                     // Defensive reserves need condition, not an active offensive plan.
                     army.Organisation > .16f && army.SupplyLevel > .20f && defenderAdded < 2)
            {
                Join(battle, army, false);
                defenderAdded++;
                battle.DefenderReinforcements++;
            }
        }
    }

    /// <summary>
    /// The formations on one side of a battle, in the order the battle listed them.
    ///
    /// This used to resolve every id with a linear search of the whole army list, once per
    /// side per battle per day: three hundred and sixty battles times fourteen hundred
    /// divisions times the ids on each side. It is a dictionary lookup now, and the result
    /// goes into a buffer the caller owns rather than a fresh list.
    /// </summary>
    private static List<Army> Side(StrategicWar war, IEnumerable<int> ids, int nationId, List<Army> into)
    {
        into.Clear();
        var byId = ArmiesById();
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var army) && WarCoalition.SameSide(war, army.NationId, nationId) && army.Divisions >= 0.1f)
                into.Add(army);
        return into;
    }

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
        DiagnosticLogs.Write("simulation", $"BATTLE_END battle={battle.Id} war={battle.WarId} province={battle.ProvinceId} winner={winnerId} outcome={outcome}");
        Tally(battle.WarId, outcome);
        battle.Ended = true;
        battle.WinnerId = winnerId;
        battle.Outcome = outcome;
        foreach (var army in campaign.Armies)
            if (army.BattleId == battle.Id) army.BattleId = -1;
    }
}
