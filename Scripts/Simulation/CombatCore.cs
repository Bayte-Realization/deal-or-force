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
        // Actual weighted loss in this round, after template organisation resistance.
        // Zero in older saves; their damage telemetry remains a conservative fallback.
        public float AttackerOrganisationSpent;
        public float DefenderOrganisationSpent;
        public bool HasOrganisationTelemetry;
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
    /// <summary>What each side's long-range forces - guns, aircraft, drones - contributed
    /// to this battle, in raw reach points. Written each round; shown in the report.</summary>
    public float AttackerSupport;
    public float DefenderSupport;
    public readonly List<int> Attackers = new();
    public readonly List<int> Defenders = new();
    public readonly List<int> WithdrawnAttackers = new();
    public readonly List<int> WithdrawnDefenders = new();
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
    private static readonly List<int> Candidates = new();
    private static readonly List<Army> Present = new();
    private static readonly List<Army> AttackingSide = new();
    private static readonly List<Army> DefendingSide = new();
    private static readonly HashSet<int> CoverSeen = new();
    private static readonly List<int> CoverRing = new();
    private static readonly List<int> CoverNext = new();

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
                    if (Moved.Contains(war.Id)) FrontPlanner.RefreshAssignments(campaign, war);
            using (SimProfiler.Measure("    fr:reassign"))
                foreach (var war in Stale)
                    if (Moved.Contains(war.Id)) FrontPlanner.ReassignAi(campaign, war);
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
        campaign.Battles.RemoveAll(battle => battle.Ended && campaign.ElapsedDays - battle.StartedDay > 180);
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
        foreach (var nationId in war.Side(true))
            if (campaign.NationById(nationId) is { } member) AttackerMaps.Add((ushort)member.MapIndex);
        foreach (var nationId in war.Side(false))
            if (campaign.NationById(nationId) is { } member) DefenderMaps.Add((ushort)member.MapIndex);

        foreach (var army in In(ByWar, war.Id))
        {
            if ((army.OrderKind != ArmyOrder.AdvanceFront && army.OrderKind != ArmyOrder.Spearhead) || !army.PlanActive ||
                army.InBattle || army.IsMoving || army.ReadyDay > campaign.ElapsedDays ||
                !war.Involves(army.NationId))
                continue;

            var enemyMaps = war.IsAttacking(army.NationId) ? DefenderMaps : AttackerMaps;
            var enemyId = war.EnemyLeaderOf(army.NationId);
            var enemy = campaign.NationById(enemyId);
            if (enemy is null || army.ProvinceId <= 0) continue;
            bool routed;
            using (SimProfiler.Measure("      adv:route")) routed = PrepareOperationalRoute(campaign, war, army, controllers);
            if (routed) continue;
            Candidates.Clear();
            foreach (var province in topology.Neighbors[army.ProvinceId])
                if (enemyMaps.Contains(controllers[province])) Candidates.Add(province);
            if (Candidates.Count == 0) continue;
            if (army.PlannedRoute.Count > 0 && Candidates.Contains(army.PlannedRoute[0]))
            {
                var nextPlannedProvince = army.PlannedRoute[0];
                Candidates.Clear();
                Candidates.Add(nextPlannedProvince);
            }
            if (army.OrderProvinceId > 0 && Candidates.Contains(army.OrderProvinceId))
            {
                Candidates.Clear();
                Candidates.Add(army.OrderProvinceId);
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
                    if (defender.NationId == enemyId && CampaignIntel.CanSeeArmy(campaign, army.NationId, defender))
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
            // Everyone standing in the target province, including anyone already fighting
            // there. Excluding divisions that were in a battle meant a province being
            // actively defended looked empty to the next attacker, which walked into it
            // unopposed while the defenders were busy holding it. Ten province captures in
            // eleven came through that hole rather than through winning anything.
            Present.Clear();
            foreach (var other in InProvince(target))
                if (other.NationId == enemyId && other.Divisions >= 0.1f) Present.Add(other);

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
                Cover(campaign, topology, target, enemyId, ref coveringPower, ref covering);
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
                if (!ordered && coveringPower > PushingOn(campaign, topology, army, target) * campaign.Defines.GapExploitAdvantage) continue;
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
                    DefenderId = enemyId,
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
    /// The force pushing on a gap: every ready advancing division of the country standing beside
    /// it, not the one division asking. Cover is a wall to a force smaller than what covers the
    /// gap - and the force was taken to be each division alone, so a whole front of them stood
    /// in front of a thinly covered line indefinitely, every one of them outmatched on its own:
    /// thirty-two Colombian divisions spent two years at their posts on the Panamanian border
    /// against one. Counting them together halved the advancing divisions that stood at their
    /// posts all war (343 to 159 over sixteen seeds) and did not move the Ukrainian front
    /// (24 seeds, z -0.2): a real line is covered by more than the divisions facing each gap.
    /// </summary>
    private static float PushingOn(Campaign campaign, ProvinceTopology topology, Army army, int target)
    {
        var power = 0f;
        foreach (var province in topology.Neighbors[target])
            foreach (var other in InProvince(province))
            {
                if (other.NationId != army.NationId || other.InBattle || other.IsMoving || !other.PlanActive) continue;
                if (other.OrderKind is not (ArmyOrder.AdvanceFront or ArmyOrder.Spearhead)) continue;
                if (other.ReadyDay > campaign.ElapsedDays) continue;
                power += other.Power;
            }
        return MathF.Max(power, army.Power);
    }

    /// <summary>
    /// Keeps the attack corridor executable as control changes. Returns true when the army
    /// has first been assigned a friendly staging leg and therefore should not attack today.
    /// </summary>
    private static readonly Dictionary<int, (int From, int To, ushort Friendly, ushort Hostile, int Revision)> NoRoute = new();
    private static Campaign _noRouteCampaign;

    private static bool PrepareOperationalRoute(Campaign campaign, StrategicWar war, Army army,
        ushort[] controllers)
    {
        var topology = campaign.ProvinceMap;
        var nation = campaign.NationById(army.NationId);
        var enemyId = war.EnemyLeaderOf(army.NationId);
        var enemy = campaign.NationById(enemyId);
        if (topology is null || nation is null || enemy is null) return false;

        var plan = BattlePlanning.LineFor(campaign, army);
        var boundedPlan = !army.DirectOrder && plan is { Kind: FrontLineKind.Offensive } && plan.Objectives.Count > 0;
        if (army.OrderProvinceId <= 0) return boundedPlan;
        if (boundedPlan && army.ProvinceId == army.OrderProvinceId)
        {
            army.PlanActive = false;
            army.Route.Clear(); army.PlannedRoute.Clear();
            return true;
        }

        if (army.SupportOnly && controllers[army.OrderProvinceId] == nation.MapIndex)
        {
            new HaltArmyCommand { IssuerId = army.NationId, ArmyId = army.Id }.Apply(campaign);
            army.SupportOnly = false;
            army.PlanActive = false;
            army.OrderKind = ArmyOrder.Manual;
            army.AssignedWarId = -1;
            return true;
        }

        while (army.PlannedRoute.Count > 0 && army.PlannedRoute[0] == army.ProvinceId)
            army.PlannedRoute.RemoveAt(0);
        if (army.PlannedRoute.Count == 0 || !topology.Neighbors[army.ProvinceId].Contains(army.PlannedRoute[0]) ||
            (controllers[army.PlannedRoute[0]] != nation.MapIndex && controllers[army.PlannedRoute[0]] != enemy.MapIndex))
        {
            army.PlannedRoute.Clear();
            var search = (army.ProvinceId, army.OrderProvinceId, (ushort)nation.MapIndex, (ushort)enemy.MapIndex, campaign.MapRevision);
            if (!ReferenceEquals(_noRouteCampaign, campaign)) { NoRoute.Clear(); _noRouteCampaign = campaign; }
            // The same search on the same map finds the same nothing. A division whose objective
            // it cannot reach - or is already standing on - asked again every hour, flooding its
            // side's whole territory each time: a hundred and twenty such searches a step, half
            // of a day's simulation thirty-six years in. A failed search is remembered until
            // the division moves, its objective changes, or any province changes hands.
            if (!NoRoute.TryGetValue(army.Id, out var failed) || failed != search)
            {
                army.PlannedRoute.AddRange(topology.FindOperationalRoute(army.ProvinceId, army.OrderProvinceId,
                    (ushort)nation.MapIndex, (ushort)enemy.MapIndex, controllers));
                if (army.PlannedRoute.Count == 0) NoRoute[army.Id] = search;
                else NoRoute.Remove(army.Id);
            }
            army.OrderRouteSteps = Math.Max(army.OrderStepsCompleted + army.PlannedRoute.Count,
                army.OrderRouteSteps);
        }
        if ((army.DirectOrder || boundedPlan) && army.PlannedRoute.Count == 0)
        {
            if (boundedPlan) { army.PlanActive = false; army.Route.Clear(); return true; }
            var arrived = army.ProvinceId == army.OrderProvinceId;
            new HaltArmyCommand { IssuerId = army.NationId, ArmyId = army.Id }.Apply(campaign);
            army.LastMoveResult = arrived ? ArmyMoveResult.AlreadyAtDestination : ArmyMoveResult.RouteInterrupted;
            army.LastMoveResultDay = campaign.ElapsedDays;
            return true;
        }
        if (army.PlannedRoute.Count == 0 || controllers[army.PlannedRoute[0]] != nation.MapIndex) return false;

        // By hand. TakeWhile's predicate captured `controllers` and `nation`, and a closure
        // over a parameter is allocated when the method is entered - so every advancing
        // division paid for one every step, whether or not it ever got this far.
        army.Route.Clear();
        foreach (var province in army.PlannedRoute)
        {
            if (controllers[province] != nation.MapIndex) break;
            army.Route.Add(province);
        }
        return army.Route.Count > 0;
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
        var defenderId = war.EnemyLeaderOf(invaderId);

        DefendingSide.Clear();
        foreach (var army in InProvince(provinceId))
            if (army.NationId == defenderId && army.Divisions >= 0.1f) DefendingSide.Add(army);

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
                DefenderId = defenderId,
                StartedDay = campaign.ElapsedDays
            };
            campaign.Battles.Add(battle);
            foreach (var defending in DefendingSide) Join(battle, defending, false);
        }
        foreach (var army in landed) Join(battle, army, true);
        _provinceIndexStale = true;
        return $"{landed.Count:N0} formation(s) landed into a contested beach";
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
                if (!army.InBattle && army.NationId == battle.DefenderId && army.Divisions >= 0.1f)
                    Join(battle, army, false);

            var attacking = Side(battle.Attackers, battle.AttackerId, AttackingSide);
            var defending = Side(battle.Defenders, battle.DefenderId, DefendingSide);
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
            // the way it does in Hearts of Iron; cramming more than fits is a penalty
            // rather than free strength.
            // ----------------------------------------------------------------------
            var defines = campaign.Defines;
            var terrain = campaign.ProvinceMap?.TerrainAt(battle.ProvinceId) ?? TerrainCatalog.Get("plains");
            var baseWidth = defines.CombatWidth * terrain.CombatWidth;
            var directions = attacking.Select(army => army.ProvinceId).Distinct().Count();
            var usableWidth = baseWidth + defines.CombatWidthPerDirection * terrain.CombatWidth *
                M.Clamp(directions - 1, 0, M.Max(0, defines.MaxAttackDirections - 1));

            var attackerWantedWidth = WantedWidth(attacking);
            var defenderWantedWidth = WantedWidth(defending);
            battle.AttackerWidth = MathF.Min(usableWidth, attackerWantedWidth);
            battle.DefenderWidth = MathF.Min(baseWidth, defenderWantedWidth);
            var attackerEngagement = Engagement(battle.AttackerWidth, attackerWantedWidth, usableWidth, defines);
            var defenderEngagement = Engagement(battle.DefenderWidth, defenderWantedWidth, baseWidth, defines);

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
            var attackerNations = new HashSet<int>(attacking.Select(army => army.NationId));
            var defenderNations = new HashSet<int>(defending.Select(army => army.NationId));
            var (attackerFire, defenderFire, defenderStripped, attackerStripped,
                    attackerSupportShown, defenderSupportShown) =
                SupportCore.Engagement(campaign, battle.ProvinceId, attackerNations, defenderNations);
            battle.AttackerSupport = attackerSupportShown;
            battle.DefenderSupport = defenderSupportShown;

            var attackerOffence = attacking.Sum(army => M.Max(0.01f, army.Power) *
                    Offence(army, defenderHardness) * DivisionRules.Attack(army) *
                    MilitaryDoctrine.Attack(army.Stance) * Researched(campaign, army, "army_attack")) *
                attackerEngagement * attackerPierces * riverAttackMultiplier * terrain.Attack *
                (1f + attackerPlanning * defines.MaxPlanningAttackBonus) * attackerFire;

            var defenderOffence = defending.Sum(army => M.Max(0.01f, army.Power) *
                    Offence(army, attackerHardness) * DivisionRules.Attack(army) *
                    MilitaryDoctrine.Attack(army.Stance) * Researched(campaign, army, "army_attack")) *
                defenderEngagement * defenderPierces * defines.DefenderCounterFire * defenderFire;

            var dugIn = defending.Average(army => army.Entrenchment);
            var defenderProtection = defending.Sum(army => M.Max(0.01f, army.Power) *
                    UnitTypeCatalog.Get(army.TemplateId).Defense * DivisionRules.Defense(army) *
                    MilitaryDoctrine.Defense(army.Stance) * Researched(campaign, army, "army_defense")) *
                defenderEngagement * defines.DefenderAdvantage * terrain.Defense *
                (1f + dugIn * defines.EntrenchmentBonus) * defenderStripped;

            var attackerProtection = attacking.Sum(army => M.Max(0.01f, army.Power) *
                    UnitTypeCatalog.Get(army.TemplateId).Breakthrough * DivisionRules.Defense(army) *
                    MilitaryDoctrine.Defense(army.Stance) * Researched(campaign, army, "army_defense")) *
                attackerEngagement * attackerStripped;

            var pressureOnDefender = M.Clamp(attackerOffence / M.Max(0.01f, defenderProtection), 0.05f, 14f);
            var pressureOnAttacker = M.Clamp(defenderOffence / M.Max(0.01f, attackerProtection), 0.05f, 14f);
            var ratio = pressureOnDefender;

            var defenderOrgLoss = M.Clamp(defines.BaseOrganisationDamage * MathF.Sqrt(pressureOnDefender),
                defines.MinOrganisationDamage, defines.MaxOrganisationDamage);
            var attackerOrgLoss = M.Clamp(defines.BaseOrganisationDamage * MathF.Sqrt(pressureOnAttacker),
                defines.MinOrganisationDamage, defines.MaxOrganisationDamage);
            var attackerOrgBefore = AverageOrganisation(attacking);
            var defenderOrgBefore = AverageOrganisation(defending);
            var defenderLoss = ApplyDamage(campaign, defending, defenderOrgLoss, 1f, out var defenderKilled);
            var attackerLoss = ApplyDamage(campaign, attacking, attackerOrgLoss,
                defines.AttackerCasualtyMultiplier, out var attackerKilled);
            foreach (var army in attacking)
                army.Planning = M.Clamp01(army.Planning - campaign.Defines.PlanningBurnPerCombatDay);
            battle.AttackerLosses += attackerLoss;
            battle.DefenderLosses += defenderLoss;
            // And the ground they fought over: a day of battle wrecks a share of the region.
            WarDamage.Fought(campaign, battle.ProvinceId, attackerLoss + defenderLoss);

            // By side rather than by leader. This compared the battle's attacker with the
            // war's, so an ally's assault in a coalition war put each side's dead on the other.
            WarLosses.Record(war, battle.AttackerId, attackerKilled, attackerLoss - attackerKilled, 0f);
            WarLosses.Record(war, battle.DefenderId, defenderKilled, defenderLoss - defenderKilled, 0f);

            var attackerOrg = AverageOrganisation(attacking);
            var defenderOrg = AverageOrganisation(defending);
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
                var occupied = Capture(campaign, war, battle.ProvinceId, battle.AttackerId, attacking, defending,
                    "won the battle");
                Close(campaign, battle, battle.AttackerId, occupied ? "Defenders broke" : "Defenders broke; support did not occupy");
            }
            else if (attackerOrg <= campaign.Defines.BattleBreakOrganisation)
            {
                foreach (var army in attacking)
                    army.ReadyDay = campaign.ElapsedDays + campaign.Defines.RestAfterSpentAttackDays;
                Close(campaign, battle, battle.DefenderId, "Attack spent");
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
                army.ReadyDay > campaign.ElapsedDays ||
                army.Organisation <= 0.16f || army.SupplyLevel <= 0.20f) continue;
            Reinforcements.Add(army);
        }
    }

    private static void ReinforceBattle(Campaign campaign, LandBattle battle)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return;
        EnsureIndexed(campaign);
        var adjacent = topology.Neighbors[battle.ProvinceId];
        var attackerAdded = 0;
        var defenderAdded = 0;

        // Reinforcements come from the province being fought over and the ones touching it,
        // so they can be found by looking there rather than by reading the whole world's
        // order of battle. The old form filtered all fourteen hundred divisions for every
        // live battle every day - a hundred thousand tests to find at most a dozen - and
        // then allocated a sorter, a key array and a buffer to put them in order.
        Reinforcements.Clear();
        Consider(campaign, battle, battle.ProvinceId);
        foreach (var neighbour in adjacent) Consider(campaign, battle, neighbour);

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
            // Requiring an active plan and a standing formation on top of that excluded
            // almost every attacker there was, and an attacker that never joins a battle
            // never spends anything: the Russian army finished two years at 96 % average
            // organisation with one formation spent, walked forward against a Ukrainian
            // army worn to 30 %, and took a hundred and nineteen provinces. Attacking has
            // to cost the attacker, so whoever is there and able attacks.
            if (army.NationId == battle.AttackerId &&
                (army.OrderKind == ArmyOrder.AdvanceFront || army.OrderKind == ArmyOrder.Spearhead) &&
                (!army.DirectOrder ||
                 (army.PlannedRoute.Count > 0 ? army.PlannedRoute[0] : army.OrderProvinceId) == battle.ProvinceId) &&
                attackerAdded < 2)
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

    /// <summary>
    /// Who a province taken in battle is handed to: the winner, unless it is winning back
    /// somebody else's ground.
    ///
    /// A province retaken from the enemy used to become the property of whoever's division
    /// walked in. Brazil fighting beside Austria against an army that had overrun it ended
    /// the war holding Lower Austria, and a status-quo peace - each side keeps what it holds -
    /// then made that permanent: Brazil owned Vienna, Italy owned Bohemia, South Korea owned
    /// Mongolia, every one of them land its own ally had been fighting to keep. Ground that
    /// belongs to a country the winner is not at war with goes back to that country the day
    /// it is freed; the winner's own troops still stand on it, as guests.
    /// </summary>
    internal static ushort Liberator(Campaign campaign, int provinceId, Nation winner)
    {
        var owner = campaign.ProvinceMap.ProvinceToNation[provinceId];
        if (owner == 0 || owner == winner.MapIndex) return (ushort)winner.MapIndex;
        var owning = NavalOperations.NationAt(campaign, owner);
        if (owning is null || owning.Population <= 0 || CampaignDiplomacy.AtWar(campaign, winner.Id, owning.Id))
            return (ushort)winner.MapIndex;
        return owner;
    }

    private static bool Capture(Campaign campaign, StrategicWar war, int provinceId, int winnerId,
        IReadOnlyCollection<Army> advancing, IReadOnlyCollection<Army> retreating, string how)
    {
        // Taking a province moves whoever took it, so the province index no longer
        // describes where everyone is standing.
        _provinceIndexStale = true;
        var winner = campaign.NationById(winnerId);
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (winner is null || topology is null || controllers is null) return false;
        var occupying = advancing.Any(army => !army.SupportOnly);
        if (occupying)
        {
            Tally(war.Id, "capture: " + how);
            war.OriginalController.TryAdd(provinceId, controllers[provinceId]);
            if (!campaign.SetProvinceController(provinceId, Liberator(campaign, provinceId, winner))) return false;
            war.FrontDirty = true;
        }
        war.UnopposedProgress.Remove(provinceId);
        var center = topology.CenterOf(provinceId);
        foreach (var army in advancing)
        {
            if (army.SupportOnly)
            {
                // It was ordered to help win the fight, not to take the ground. It keeps
                // its position, and with it whatever it had dug - which is the entire
                // reason a player gives this order instead of an ordinary attack.
                army.SupportOnly = false;
                army.OrderProvinceId = -1;
                army.OrderStateMapIndex = -1;
                army.DirectOrder = false;
                army.PlanActive = false;
                army.OrderKind = ArmyOrder.Manual;
                army.AssignedWarId = -1;
                army.Route.Clear();
                army.MoveProgress = 0f;
                army.PlannedRoute.Clear();
                continue;
            }
            army.ProvinceId = provinceId;
            army.StateMapIndex = topology.ProvinceToState[provinceId];
            army.Latitude = center.Latitude;
            army.Longitude = center.Longitude;
            army.Route.Clear();
            army.MoveProgress = 0f;
            if (army.PlannedRoute.Count > 0 && army.PlannedRoute[0] == provinceId)
            {
                army.PlannedRoute.RemoveAt(0);
                army.OrderStepsCompleted++;
            }
            else if (army.PlannedRoute.Count > 0)
            {
                // A battle can be resolved by another formation first. Drop the stale
                // corridor; it will be rebuilt from this new position on the next advance.
                army.PlannedRoute.Clear();
            }
            army.Entrenchment = 0f;
            army.ReadyDay = campaign.ElapsedDays + campaign.Defines.RestAfterCaptureDays;
            if (army.OrderProvinceId == provinceId)
            {
                var objectiveLine = BattlePlanning.LineFor(campaign, army);
                if (!army.DirectOrder && objectiveLine is { Kind: FrontLineKind.Offensive } && objectiveLine.Objectives.Count > 0)
                {
                    army.PlanActive = false;
                    army.PlannedRoute.Clear();
                    continue;
                }
                // A direct RMB attack ends in the selected province instead of
                // silently turning into a perpetual autonomous offensive.
                army.OrderKind = ArmyOrder.Manual;
                army.DirectOrder = false;
                army.AssignedWarId = -1;
                army.OrderProvinceId = -1;
                army.OrderStateMapIndex = -1;
                army.ObjectiveStateMapIndex = -1;
                army.PlanActive = false;
                army.PlannedRoute.Clear();
            }
        }
        foreach (var army in retreating)
        {
            var owner = campaign.NationById(army.NationId);

            // By hand: FirstOrDefault's predicate closes over `owner`, so working out where
            // to fall back to allocated a closure and a delegate for every formation beaten
            // out of every province.
            var retreat = 0;
            if (owner is not null)
                foreach (var candidate in topology.Neighbors[provinceId])
                    if (controllers[candidate] == (ushort)owner.MapIndex) { retreat = candidate; break; }

            // Nowhere to go. It was left standing in the province it had just lost, inside
            // ground the enemy now holds, and the code said as much in a diagnostic marked
            // as a known defect. What has happened to a formation beaten out of its last
            // position with no friendly ground behind it is that it surrendered, and that is
            // now what happens to it - through the same path attrition uses, so it is
            // counted and reported rather than quietly left in place.
            if (retreat <= 0)
            {
                DiagnosticLogs.Write("simulation", $"SURRENDER army={army.Id} nation={army.NationId} province={provinceId} war={war.Id} reason=no_line_of_retreat");
                WarLosses.Surrender(campaign, army);
                Attrition.Destroy(campaign, army, "destroyed: cut off with no line of retreat");
                continue;
            }
            DiagnosticLogs.Write("simulation", $"RETREAT army={army.Id} nation={army.NationId} from={provinceId} to={retreat} ready_day={campaign.ElapsedDays + campaign.Defines.RestAfterRetreatDays}");
            var fallback = topology.CenterOf(retreat);
            army.ProvinceId = retreat;
            army.StateMapIndex = topology.ProvinceToState[retreat];
            army.Latitude = fallback.Latitude;
            army.Longitude = fallback.Longitude;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.MoveProgress = 0f;
            army.Entrenchment = 0f;
            army.ReadyDay = campaign.ElapsedDays + campaign.Defines.RestAfterRetreatDays;
        }
        return occupying;
    }

    private static void SecureOccupation(Campaign campaign)
    {
        if (!ReferenceEquals(_measuredCampaign, campaign)) MeasureOccupation(campaign);
        var controllers = campaign.ProvinceController;
        var occupation = campaign.ProvinceOccupation;
        if (controllers is null || occupation is null) return;
        // The garrison is whoever is standing in the province. Asking that with a Where
        // over the whole army list, once per occupied province per day, was a thousand
        // occupied provinces times fourteen hundred divisions - and it made securing
        // occupations the single slowest thing the simulation did.
        EnsureIndexed(campaign);
        foreach (var war in campaign.Wars)
        foreach (var pair in war.OriginalController)
        {
            var province = pair.Key;
            if (controllers[province] == pair.Value) { occupation[province] = 0f; continue; }
            var garrison = 0f;
            foreach (var army in InProvince(province))
                if (!army.InBattle) garrison += army.Power;
            // A rate, so it takes the step's share of a day. Without this a province was
            // occupied twenty-four times faster at an hourly step than at a daily one.
            occupation[province] = M.Clamp01(occupation[province] +
                campaign.Defines.OccupationDailyProgress * campaign.TickScale *
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
        // Ground taken by anybody on a side counts for that side. Scoring the two leaders
        // alone meant an ally could occupy half a country and the war score would not move,
        // which is a coalition war that does not know it is one.
        var attackerHome = 0;
        var defenderHome = 0;
        var attackerOccupied = 0;
        var defenderOccupied = 0;
        foreach (var attackingId in war.Side(true))
        {
            if (campaign.NationById(attackingId) is not { } member) continue;
            var map = (ushort)member.MapIndex;
            attackerHome += HomeProvinces.GetValueOrDefault(map);
            foreach (var defendingId in war.Side(false))
                if (campaign.NationById(defendingId) is { } foe)
                    attackerOccupied += Occupied(map, (ushort)foe.MapIndex);
        }
        foreach (var defendingId in war.Side(false))
        {
            if (campaign.NationById(defendingId) is not { } member) continue;
            var map = (ushort)member.MapIndex;
            defenderHome += HomeProvinces.GetValueOrDefault(map);
            foreach (var attackingId in war.Side(true))
                if (campaign.NationById(attackingId) is { } foe)
                    defenderOccupied += Occupied(map, (ushort)foe.MapIndex);
        }
        var attackerOccupiedShare = attackerHome == 0 ? 0f : (float)attackerOccupied / attackerHome;
        var defenderOccupiedShare = defenderHome == 0 ? 0f : (float)defenderOccupied / defenderHome;
        var attackerLossBurden = war.AttackerLosses / MathF.Max(50_000f, attacker.Population * 0.035f);
        var defenderLossBurden = war.DefenderLosses / MathF.Max(50_000f, defender.Population * 0.035f);
        war.AttackerWarSupport = M.Clamp01(attacker.WarSupport - attackerLossBurden * 0.52f - attackerOccupiedShare * 0.58f);
        war.DefenderWarSupport = M.Clamp01(defender.WarSupport - defenderLossBurden * 0.52f - defenderOccupiedShare * 0.58f);

        // The score itself: occupation by worth, the capitals, the armies, the battles, the
        // war goal, the momentum and the home front, and collapse (WarScoring).
        WarScoring.Update(campaign, war);
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
    /// <summary>One plus what a formation's country adds to this: research, spirits, government (NationalModifiers).</summary>
    private static float Researched(Campaign campaign, Army army, string effect) =>
        NationalModifiers.Factor(campaign.NationById(army.NationId), effect);

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
        float organisationLoss, float casualtyScale, out float killed)
    {
        var defines = campaign.Defines;
        var totalPower = side.Sum(army => M.Max(0.01f, army.Power));
        var lost = 0f;
        killed = 0f;
        var woundedShare = M.Clamp01(defines.WoundedShareOfCasualties);
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
                             MilitaryDoctrine.CasualtyExposure(army.Stance) *
                             (army.CutOff ? defines.EncircledCasualtyMultiplier : 1f);
            if (personnel > 1f) army.Strength = M.Clamp01(army.Strength - casualties / personnel);
            army.Personnel = Math.Max(0, army.Personnel - (int)MathF.Round(casualties));
            army.Recovering += casualties * defines.WoundedReturningShare;
            army.Experience = M.Clamp01(army.Experience + loss * 0.004f);
            army.Cohesion = M.Clamp01(army.Cohesion - loss * 0.08f);
            lost += casualties;
            killed += casualties * (1f - woundedShare);
        }
        return lost;
    }

    /// <summary>
    /// Weigh everything the enemy has within reach of one province. Formations touching it
    /// count in full; each further ring counts for less, out to the radius a division is
    /// taken to hold. The tally is fractional because a division two provinces away is a
    /// fraction of a division's worth of resistance, not none and not all of it.
    /// </summary>
    private static void Cover(Campaign campaign, ProvinceTopology topology,
        int target, int enemyId, ref float coveringPower, ref float covering)
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
                    if (other.NationId != enemyId || other.Divisions < 0.1f) continue;
                    coveringPower += M.Max(other.Power,
                        other.Presence * campaign.Defines.CoveringPresenceFloor) * weight;
                    covering += weight;
                }
            }
            (ring, next) = (next, ring);
            weight *= falloff;
        }
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

    /// <summary>
    /// The formations on one side of a battle, in the order the battle listed them.
    ///
    /// This used to resolve every id with a linear search of the whole army list, once per
    /// side per battle per day: three hundred and sixty battles times fourteen hundred
    /// divisions times the ids on each side. It is a dictionary lookup now, and the result
    /// goes into a buffer the caller owns rather than a fresh list.
    /// </summary>
    private static List<Army> Side(IEnumerable<int> ids, int nationId, List<Army> into)
    {
        into.Clear();
        var byId = ArmiesById();
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var army) && army.NationId == nationId && army.Divisions >= 0.1f)
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
