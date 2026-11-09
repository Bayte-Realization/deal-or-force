using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// Whether a state that is failing on several fronts at once can come apart in a cascade
    /// (StateFailure). Off leaves only the one-at-a-time risings and secessions.
    /// </summary>
    public bool StateCollapses { get; set; } = true;
    /// <summary>Stability below which a state can begin to collapse at all.</summary>
    public float CollapseStabilityThreshold { get; set; } = 0.28f;
    /// <summary>
    /// How many of the signs of a failing state must hold at once - the capital lost or in
    /// revolt, several risings, a war being lost, an army not being paid, a people radicalised
    /// through and through. One bad thing is a crisis; a collapse is several at once.
    /// </summary>
    public int CollapseSignsNeeded { get; set; } = 2;
    /// <summary>The weekly chance at no stability and exactly the signs needed; each further sign adds half again.</summary>
    public float CollapseWeeklyChance { get; set; } = 0.03f;
    /// <summary>The most collapses the whole world begins in a year.</summary>
    public int CollapseWorldPerYear { get; set; } = 2;
    /// <summary>How long a state that has collapsed is spared another, from the day the last one began.</summary>
    public int CollapseCooldownDays { get; set; } = 5475;
    public int CollapseEarliestDay { get; set; } = 180;
    public int CollapseMinimumRegions { get; set; } = 4;
    public long CollapseMinimumPopulation { get; set; } = 2_000_000;
    /// <summary>Days between the waves of one collapse, and the most waves it has.</summary>
    public int CollapseWaveDays { get; set; } = 45;
    public int CollapseMaxWaves { get; set; } = 6;
    /// <summary>The new sides the first wave makes at once; later waves make one, or two when the state is at the bottom.</summary>
    public int CollapseFirstWaveSides { get; set; } = 2;
    /// <summary>The share of the people it had when the collapse began that the centre is never pushed below.</summary>
    public float CollapseRumpShare { get; set; } = 0.20f;
    /// <summary>Stability at which a collapsing state has stopped the slide.</summary>
    public float CollapseRecoveryStability { get; set; } = 0.36f;
    /// <summary>A warlord's or front's ground, as a share of the people the state had when it began to collapse.</summary>
    public float CollapseSideShareMin { get; set; } = 0.06f;
    public float CollapseSideShareMax { get; set; } = 0.22f;
    /// <summary>A region of another people goes as that people's own state when its pressure or its anger is this high.</summary>
    public float CollapseNationalPressure { get; set; } = 0.20f;
    public float CollapseNationalRadicalism { get; set; } = 0.35f;

    /// <summary>
    /// Revolutions: a radicalised capital, a street that has filled, and an army that will not
    /// clear it (StateFailure.Revolution). Reviewed monthly; the chance is per review once all
    /// three hold.
    /// </summary>
    public bool Revolutions { get; set; } = true;
    public float RevolutionStabilityThreshold { get; set; } = 0.35f;
    public float RevolutionCapitalRadicalism { get; set; } = 0.40f;
    public float RevolutionStreet { get; set; } = 0.30f;
    public float RevolutionArmyDisloyalty { get; set; } = 0.55f;
    public float RevolutionMonthlyChance { get; set; } = 0.20f;
    public int RevolutionWorldPerYear { get; set; } = 3;
    public int RevolutionCooldownDays { get; set; } = 3650;
    public int RevolutionEarliestDay { get; set; } = 180;
    /// <summary>The chance the old regime's loyal regions fight on as a government of their own.</summary>
    public float RevolutionCivilWarChance { get; set; } = 0.45f;
    /// <summary>How many foreign governments of each side's creed take it up at once.</summary>
    public int RevolutionSponsorsPerSide { get; set; } = 2;

    /// <summary>
    /// Reunions: a piece of a state that came apart returning to it by agreement, once the
    /// centre has recovered and the piece is the same people (StateFailure.Reunion).
    /// </summary>
    public bool CollapseReunions { get; set; } = true;
    public float ReunionCentreStability { get; set; } = 0.45f;
    public float ReunionPieceStability { get; set; } = 0.35f;
    public int ReunionPieceMaxRegions { get; set; } = 4;
    public float ReunionMinimumOpinion { get; set; } = -30f;
    public float ReunionIdeologyDistance { get; set; } = 0.5f;
    /// <summary>The furthest apart in creed a failing piece and its old centre can be and still reunite.</summary>
    public float ReunionFailingIdeologyDistance { get; set; } = 0.6f;
    public int ReunionEarliestDays { get; set; } = 730;
    public float ReunionMonthlyChance { get; set; } = 0.12f;
}

public sealed partial class Nation
{
    /// <summary>The day this state began to come apart (StateFailure), or -1 when it is not collapsing.</summary>
    public int CollapseStartDay = -1;
    /// <summary>How many waves of the current collapse have broken; 0 when none is running.</summary>
    public int CollapseWaves;
    /// <summary>Its people on the day the collapse began: what "near-total" is measured against.</summary>
    public long CollapseStartPopulation;
    /// <summary>The day its last collapse began, kept after it ends: the cooldown reads it.</summary>
    public int LastCollapseDay = -99999;
    /// <summary>The day its government last fell to a revolution.</summary>
    public int LastRevolutionDay = -99999;
}

/// <summary>
/// The failure of a state, which is more than one rising at a time.
///
/// Secession took a people's homeland when its pressure ripened, a civil war took a share of a
/// country when its stability had been low for long enough, a coup took the government - one
/// thing at a time, each with its own cooling-off period, so a state that lost its capital,
/// its army's pay and a war in the same year still lost one province in it. States do not fail
/// like that. Russia in 1917-18, Yugoslavia in 1991, Somalia in 1991, Libya and Syria in 2011:
/// when the centre stops being able to hold anything, everything that wanted to leave leaves
/// at once, and the officers in the provinces find they answer to nobody.
///
/// Three things live here, the same for every country:
///
/// - **The collapse.** A state below <see cref="Defines.CollapseStabilityThreshold"/> showing
///   at least <see cref="Defines.CollapseSignsNeeded"/> signs of failure (<see cref="Signs"/>)
///   may begin to come apart. It does not go in one day: every <see cref="Defines.CollapseWaveDays"/>
///   a wave breaks - its other peoples leave as states of their own by the ordinary secession
///   road, and the worst-governed provinces go over to whoever holds them, a military council or a
///   front named for what its region believes - until the centre has steadied, has nothing left
///   to lose but its heartland, or the waves are spent. Every side is a real country at war with
///   the centre (SecessionEvents), and the sides turn on each other by the rule for wars with more
///   than two sides (CivilWarFronts) - which is why their creeds are read region by region.
///
/// - **The revolution.** A government whose capital is radicalised, whose street has filled and
///   whose army will not clear it falls (<see cref="Revolution"/>): the capital's own leading
///   creed takes the state under a provisional government, and the regions most loyal to the old
///   order may fight on as a government of their own. Foreign governments of each side's creed
///   take it up (ProxyWars), as Moscow and the Comintern did, and Tehran and Riyadh do.
///
/// - **The reunion.** A piece of a state that came apart is not a permanent micro-state: once
///   the centre - or whoever now holds its capital - has recovered, a piece of the same people
///   that is failing or small returns by agreement (<see cref="Reunion"/>), and the rest can be
///   taken back by war (LostLands, Reintegration), as Russia took back most of its empire by 1922.
/// </summary>
public static class StateFailure
{
    /// <summary>What happened, for the bench. Not saved and never read by the simulation.</summary>
    public static readonly Dictionary<string, int> Tally = new();
    public static readonly List<string> Log = new();

    private static void Count(string key, int by = 1) => Tally[key] = Tally.GetValueOrDefault(key) + by;

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same failure watch, yielded between batches of nations.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c?.ProvinceMap is null) yield break;
        if (!MechanicFeatures.SystemEnabled(c, nameof(SecessionEvents))) yield break;
        var d = c.Defines;
        var scanned = 0;
        if (d.StateCollapses)
        {
            // Waves of a collapse already under way, every day: each runs on its own clock.
            foreach (var nation in c.Nations.ToArray())
            {
                if (++scanned % 8 == 0) yield return 0;
                if (nation.CollapseStartDay >= 0) Continue(c, nation);
            }
            if (c.ElapsedDays % 7 == 6 && c.ElapsedDays >= d.CollapseEarliestDay)
                foreach (var _ in BeginStages(c)) yield return 0;
        }
        if (d.Revolutions && c.ElapsedDays >= d.RevolutionEarliestDay)
            foreach (var nation in c.Nations.ToArray())
            {
                if (++scanned % 8 == 0) yield return 0;
                if ((c.ElapsedDays + (long)nation.Id) % 30 == 0) ConsiderRevolution(c, nation);
            }
        if (d.CollapseReunions)
            foreach (var nation in c.Nations.ToArray())
            {
                if (++scanned % 8 == 0) yield return 0;
                if ((c.ElapsedDays + (long)nation.Id * 3) % 30 == 17) ConsiderReunion(c, nation);
            }
    }

    // ---------------------------------------------------------------------------- signs

    /// <summary>
    /// The signs of a failing state that hold right now, as localisation keys, in a fixed order.
    /// Read the same way for the simulation and for anyone explaining it.
    /// </summary>
    public static List<string> Signs(Campaign c, Nation nation)
    {
        var signs = new List<string>(5);
        if (c is null || nation is null) return signs;
        if (CapitalLost(c, nation)) signs.Add("The government has lost its capital or fled it.");
        var risings = RisingsAgainst(c, nation);
        if (risings >= 2) signs.Add("Several armed risings hold parts of the country.");
        if (LosingWar(c, nation)) signs.Add("A war is being lost.");
        if (nation.PaymentArrears > 0f || nation.DebtCondition is DebtCondition.Default or DebtCondition.Insolvent ||
            nation.Treasury < 0f)
            signs.Add("The state cannot pay its soldiers.");
        if (Radicalism(c, nation) >= 0.45f) signs.Add("The people are radicalised through and through.");
        return signs;
    }

    /// <summary>The legal capital in enemy hands, or the government evacuated from it.</summary>
    public static bool CapitalLost(Campaign c, Nation nation)
    {
        if (nation.GovernmentSeatProvinceId != 0) return true;
        var province = nation.CapitalProvinceId;
        var control = c.ProvinceController;
        if (province <= 0 || control is null || province >= control.Length) return false;
        return control[province] != (ushort)nation.MapIndex;
    }

    /// <summary>Armed sides risen against this government that it is at war with now.</summary>
    public static int RisingsAgainst(Campaign c, Nation nation)
    {
        var count = 0;
        foreach (var war in c.Wars)
        {
            if (war.Ended || !war.CivilConflict || !war.Involves(nation.Id)) continue;
            foreach (var enemyId in war.EnemiesOf(nation.Id))
                if (c.NationById(enemyId) is { } enemy &&
                    (enemy.RevoltParentId == nation.Id || enemy.SecededFromId == nation.Id ||
                     enemy.Movement is not null && !enemy.Movement.Exile))
                    count++;
        }
        return count;
    }

    private static bool LosingWar(Campaign c, Nation nation)
    {
        foreach (var war in c.Wars)
            if (!war.Ended && war.Involves(nation.Id) &&
                (war.IsAttacking(nation.Id) ? war.WarScore : -war.WarScore) <= -25f)
                return true;
        return false;
    }

    private static float Radicalism(Campaign c, Nation nation)
    {
        double people = 0, weighted = 0;
        foreach (var state in c.StatesOf(nation))
        {
            if (state.Population <= 0 || !float.IsFinite(state.Radicalism)) continue;
            people += state.Population;
            weighted += state.Population * (double)M.Clamp01(state.Radicalism);
        }
        return people > 0 ? (float)(weighted / people) : 0f;
    }

    // ---------------------------------------------------------------------------- collapse

    /// <summary>Why this state cannot begin to collapse now, or "" when it can.</summary>
    public static string CollapseRefusal(Campaign c, Nation nation)
    {
        if (c is null || nation is null) return "No governed country selected.";
        var d = c.Defines;
        if (!d.StateCollapses) return "State collapse is disabled.";
        if (nation.Movement is not null || nation.FactionId.Length > 0 || nation.IsSubject || c.Fallen.Contains(nation.Id) ||
            nation.Population <= 0) return "Only an independent state can collapse.";
        if (nation.IsHuman && !d.CivilWarStrikesPlayers) return "Random outbreaks in player countries are disabled in campaign settings.";
        if (nation.CollapseStartDay >= 0) return "The state is already coming apart.";
        if (c.ElapsedDays - (long)nation.LastCollapseDay < d.CollapseCooldownDays) return "The state is protected from another collapse during recovery.";
        if (!float.IsFinite(nation.Stability) || nation.Stability >= d.CollapseStabilityThreshold)
            return "Institutions remain above the collapse threshold.";
        if (nation.Population < d.CollapseMinimumPopulation ||
            c.StatesOf(nation).Count(s => s.Population > 0) < d.CollapseMinimumRegions)
            return "Too few residents or governed regions to come apart.";
        if (Signs(c, nation).Count < Math.Max(1, d.CollapseSignsNeeded)) return "Too few signs of a failing state.";
        return "";
    }

    /// <summary>The weekly chance this state begins to collapse.</summary>
    public static float CollapseChance(Campaign c, Nation nation)
    {
        if (CollapseRefusal(c, nation).Length > 0) return 0f;
        var d = c.Defines;
        var depth = M.Clamp01((d.CollapseStabilityThreshold - nation.Stability) / MathF.Max(0.01f, d.CollapseStabilityThreshold));
        var extra = Signs(c, nation).Count - Math.Max(1, d.CollapseSignsNeeded);
        var chance = d.CollapseWeeklyChance * depth * (1f + 0.5f * extra);
        return float.IsFinite(chance) ? Math.Clamp(chance, 0f, 1f) : 0f;
    }

    private static int CollapsesInLastYear(Campaign c) =>
        c.Nations.Count(n => n.LastCollapseDay >= 0 && c.ElapsedDays - (long)n.LastCollapseDay < 365);

    private static IEnumerable<int> BeginStages(Campaign c)
    {
        var d = c.Defines;
        var thisYear = CollapsesInLastYear(c);
        var scanned = 0;
        foreach (var nation in c.Nations.ToArray())
        {
            if (thisYear >= d.CollapseWorldPerYear) yield break;
            if (++scanned % 8 == 0) yield return 0;
            if (nation.Stability >= d.CollapseStabilityThreshold) continue;   // the cheap test first
            var chance = CollapseChance(c, nation);
            if (chance <= 0f) { if (CollapseRefusal(c, nation) == "Too few signs of a failing state.") Count("collapse_one_sign_weeks"); continue; }
            Count("collapse_eligible_weeks");
            if (Escalation.Roll(c.Seed, nation.Id, 0xFA11, c.ElapsedDays, 0x5) >= chance) continue;
            if (StartCollapse(c, nation)) thisYear++;
        }
    }

    /// <summary>
    /// The collapse begins, and its first wave breaks the same day. A state with no ground that
    /// can go - all of it occupied, or settling from the last rising - does not collapse: nothing
    /// is announced and it is not spared the next one, because nothing happened.
    /// </summary>
    public static bool StartCollapse(Campaign c, Nation nation)
    {
        if (c?.ProvinceMap is null || nation is null) return false;
        var lastCollapse = nation.LastCollapseDay;
        nation.CollapseStartDay = c.ElapsedDays;
        nation.LastCollapseDay = c.ElapsedDays;
        nation.CollapseWaves = 0;
        nation.CollapseStartPopulation = Math.Max(1, nation.Population);
        var signs = Signs(c, nation);
        var people = nation.Population;
        if (Wave(c, nation) == 0)
        {
            nation.CollapseStartDay = -1;
            nation.LastCollapseDay = lastCollapse;
            nation.CollapseWaves = 0;
            nation.CollapseStartPopulation = 0;
            Count("collapse_nothing_to_break");
            return false;
        }
        c.Chronicle.Record(c, "state_collapse", nation.Id);
        Count("collapses");
        if (nation.Population >= 50_000_000) Count("collapses_large");
        Log.Add($"day {c.ElapsedDays} COLLAPSE {nation.Tag} people {people:N0} stability {nation.Stability:0.00} signs {signs.Count}");
        DiagnosticLogs.Write("simulation", $"STATE_COLLAPSE nation={nation.Tag} stability={nation.Stability:0.00} " +
            $"signs={signs.Count} people={people} day={c.ElapsedDays}");
        c.MarkStrategicChange();
        return true;
    }

    private static void Continue(Campaign c, Nation nation)
    {
        var d = c.Defines;
        if (Over(c, nation)) { End(c, nation); return; }
        var due = (long)nation.CollapseStartDay + (long)nation.CollapseWaves * Math.Max(1, d.CollapseWaveDays);
        if (c.ElapsedDays < due) return;
        Wave(c, nation);
        if (Over(c, nation)) End(c, nation);
    }

    /// <summary>Whether the collapse has run its course: steadied, spent, or down to its heartland.</summary>
    private static bool Over(Campaign c, Nation nation)
    {
        var d = c.Defines;
        if (c.Fallen.Contains(nation.Id) || nation.Population <= 0 || !c.HasOwnedLand(nation)) return true;
        if (nation.CollapseWaves >= Math.Max(1, d.CollapseMaxWaves)) return true;
        if (nation.CollapseWaves > 0 && nation.Stability >= d.CollapseRecoveryStability) return true;
        return nation.Population <= (long)(nation.CollapseStartPopulation * (double)M.Clamp01(d.CollapseRumpShare));
    }

    private static void End(Campaign c, Nation nation)
    {
        if (nation.CollapseStartDay < 0) return;
        Log.Add($"day {c.ElapsedDays} COLLAPSE_END {nation.Tag} waves {nation.CollapseWaves} people {nation.Population:N0} of {nation.CollapseStartPopulation:N0}");
        nation.CollapseStartDay = -1;
        nation.CollapseWaves = 0;
        nation.CollapseStartPopulation = 0;
    }

    /// <summary>One wave: several sides at once on the first, one or two after. Returns the sides made.</summary>
    private static int Wave(Campaign c, Nation nation)
    {
        var d = c.Defines;
        var first = nation.CollapseWaves == 0;
        nation.CollapseWaves++;
        var sides = first ? Math.Max(1, d.CollapseFirstWaveSides)
            : nation.Stability < d.CollapseStabilityThreshold * 0.5f ? 2 : 1;
        var made = 0;
        for (var i = 0; i < sides; i++)
        {
            if (Over(c, nation) && !(first && i == 0)) break;
            var born = BreakOne(c, nation, countsAsOutbreak: first && made == 0, salt: nation.CollapseWaves * 7 + i);
            if (born is null) break;
            made++;
        }
        if (made > 0) Count("waves");
        return made;
    }

    /// <summary>
    /// One new side: another people's homeland as a state of its own if the state has one ready
    /// to go, otherwise the worst-governed ground under whoever holds it.
    /// </summary>
    private static Nation BreakOne(Campaign c, Nation nation, bool countsAsOutbreak, int salt)
    {
        var owned = Breakable(c, nation);
        if (owned.Count < 2) return null;
        var adjacency = Adjacency(c, owned);
        var d = c.Defines;
        var basis = Math.Max(nation.Population, nation.CollapseStartPopulation);
        var ceiling = (long)(basis * (double)d.CollapseSideShareMax);
        // Never past the rump: the centre keeps its heartland whatever the waves take.
        var rumpFloor = (long)(nation.CollapseStartPopulation * (double)M.Clamp01(d.CollapseRumpShare));
        ceiling = Math.Min(ceiling, Math.Max(0, nation.Population - rumpFloor));
        if (ceiling <= 0) return null;

        var national = NationalGround(c, nation, owned, adjacency, ceiling, out var culture);
        if (national.Count > 0)
        {
            var report = SecessionEvents.Breakaway(c, nation, national, culture);
            if (report?.Seceded is { } born)
            {
                Count("sides_national");
                Log.Add($"day {c.ElapsedDays}   {nation.Tag} -> {born.Tag} national {culture} regions {national.Count} people {report.Population:N0} outcome {report.Outcome}");
                return born;
            }
        }

        var share = d.CollapseSideShareMin + (d.CollapseSideShareMax - d.CollapseSideShareMin) *
            Escalation.Roll(c.Seed, nation.Id, 0xFA12, c.ElapsedDays, salt);
        var target = Math.Min(ceiling, (long)(basis * (double)share));
        var minimum = Math.Min(target, (long)(basis * (double)d.CollapseSideShareMin * 0.5));
        float Grievance(ScenarioState s) => s.Stability - 0.6f * s.Radicalism;
        // The worst-governed region first; where it cannot carry a side (too big for what is
        // left to lose, or too small with its neighbours) the next worst - a few tries, not all.
        foreach (var start in owned.Where(s => s.Population <= ceiling).OrderBy(Grievance).ThenBy(s => s.MapIndex).Take(6))
        {
            var ground = Grow(owned, adjacency, start, Grievance, target, ceiling, Math.Max(1, (int)(owned.Count * 0.6f)));
            if (ground.Count == 0 || ground.Sum(s => s.Population) < minimum) continue;
            var side = Rise(c, nation, ground, countsAsOutbreak);
            if (side is not null) return side;
        }
        return null;
    }

    /// <summary>An armed side on this ground, named for its seat and believing what its seat believes.</summary>
    private static Nation Rise(Campaign c, Nation nation, List<ScenarioState> ground, bool countsAsOutbreak)
    {
        // The ground grew from its worst-governed region, but a side sits where its people are:
        // its seat, its capital and its name are its most populous region, not a glacier.
        var seed = ground[0];
        foreach (var region in ground)
            if (region.Population > seed.Population) seed = region;
        var (template, ideology, government) = Allegiance(c, nation, seed);
        var parentTag = nation.Tag;
        var parentName = nation.Name;
        var regionName = (seed.Name ?? "").Replace(':', ' ').Replace('@', ' ');
        var faith = BeliefDynamics.RebelFaith(c, nation, ground);
        var rising = SecessionEvents.Uprising(c, nation, ground, born =>
        {
            born.Name = $"rising:{template}@{seed.Id}@{regionName}:{parentTag}:{parentName}";
            born.Ideology = ideology;
            born.Government = government;
            born.LastCivilWarDay = c.ElapsedDays;
            if (faith.Length > 0) born.DeclaredReligion = faith;
        }, countsAsOutbreak);
        if (rising?.Seceded is not { } side) return null;
        nation.LastCivilWarDay = c.ElapsedDays;
        Count(template == "military_council" ? "sides_warlord" : "sides_front");
        Log.Add($"day {c.ElapsedDays}   {nation.Tag} -> {side.Tag} {template} {ideology} regions {ground.Count} people {rising.Population:N0}");
        return side;
    }

    /// <summary>
    /// What the ground a side holds believes: the region's own leading creed. Officers whose
    /// region thinks what the capital thinks are a military council - the same creed, no longer
    /// the same chain of command; a region that thinks otherwise rises as a front for it.
    /// </summary>
    private static (string Template, string Ideology, string Government) Allegiance(Campaign c, Nation nation, ScenarioState seat)
    {
        var shares = PoliticalMood.RegionShares(c, seat, nation);
        var leading = shares.Count > 0 ? shares[0].Ideology : nation.Ideology;
        if (string.IsNullOrEmpty(leading) || string.Equals(leading, nation.Ideology, StringComparison.OrdinalIgnoreCase))
            return ("military_council", nation.Ideology, Known("military_junta", nation.Government));
        var template = leading switch
        {
            "liberalism" or "social_democracy" or "libertarianism" => "regional_transitional_council",
            "socialism" or "communism" => "regional_revolutionary_front",
            _ => "regional_salvation_front"
        };
        return (template, leading, Known("provisional_government", nation.Government));
    }

    private static string Known(string government, string fallback) =>
        GovernmentCatalog.All.Any(g => string.Equals(g.Id, government, StringComparison.OrdinalIgnoreCase)) ? government : fallback;

    /// <summary>
    /// The regions that can leave: peopled, not the capital, not still settling from the last
    /// rising, and mostly in the government's own hands - ground a foreign army stands on is that
    /// army's to lose, not a province's to take (RevoltTerritory refuses it anyway).
    /// </summary>
    private static List<ScenarioState> Breakable(Campaign c, Nation nation)
    {
        var map = c.ProvinceMap;
        var control = c.ProvinceController;
        var total = new Dictionary<int, int>();
        var held = new Dictionary<int, int>();
        if (map is not null && control is not null)
            for (var p = 1; p <= map.ProvinceCount && p < control.Length; p++)
            {
                if (map.ProvinceToNation[p] != (ushort)nation.MapIndex || map.IsWater(p)) continue;
                var state = map.ProvinceToState[p];
                total[state] = total.GetValueOrDefault(state) + 1;
                if (control[p] == (ushort)nation.MapIndex) held[state] = held.GetValueOrDefault(state) + 1;
            }
        return c.StatesOf(nation).Where(s => s.Population > 0 && s.MapIndex != nation.CapitalStateMapIndex &&
            !LakeRegions.IsWaterState(s) && RevoltRecovery.DaysLeft(c, nation, s) == 0 &&
            float.IsFinite(s.Stability) && float.IsFinite(s.Radicalism) &&
            (!total.TryGetValue(s.MapIndex, out var all) || held.GetValueOrDefault(s.MapIndex) * 2 >= all)).ToList();
    }

    /// <summary>Which of these regions touch which, through the province graph, once.</summary>
    private static Dictionary<int, HashSet<int>> Adjacency(Campaign c, List<ScenarioState> owned)
    {
        var adjacent = owned.ToDictionary(s => s.MapIndex, _ => new HashSet<int>());
        var map = c.ProvinceMap;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            var here = map.ProvinceToState[p];
            if (!adjacent.TryGetValue(here, out var set)) continue;
            foreach (var next in map.Neighbors[p])
            {
                var there = map.ProvinceToState[next];
                if (there != here && adjacent.ContainsKey(there)) set.Add(there);
            }
        }
        return adjacent;
    }

    /// <summary>
    /// Contiguous ground from a seed, taking the neighbour that sorts first by <paramref name="order"/>
    /// until it holds the target - whole regions, never past the ceiling, never more than the limit.
    /// </summary>
    private static List<ScenarioState> Grow(List<ScenarioState> owned, Dictionary<int, HashSet<int>> adjacency,
        ScenarioState seed, Func<ScenarioState, float> order, long target, long ceiling, int limit,
        Func<ScenarioState, bool> admits = null)
    {
        if (seed is null || seed.Population > ceiling) return new();
        var byIndex = owned.ToDictionary(s => s.MapIndex);
        var taken = new List<ScenarioState> { seed };
        var inside = new HashSet<int> { seed.MapIndex };
        var passed = new HashSet<int>();
        long people = seed.Population;
        while (people < target && taken.Count < limit)
        {
            var next = taken.SelectMany(s => adjacency[s.MapIndex])
                .Where(i => !inside.Contains(i) && !passed.Contains(i) && byIndex.ContainsKey(i))
                .Select(i => byIndex[i]).Where(s => admits is null || admits(s))
                .OrderBy(order).ThenBy(s => s.MapIndex).FirstOrDefault();
            if (next is null) break;
            if (people + next.Population > ceiling) { passed.Add(next.MapIndex); continue; }
            taken.Add(next); inside.Add(next.MapIndex); people += next.Population;
        }
        return taken;
    }

    /// <summary>The largest people in a region and its share of the region.</summary>
    private static (string Culture, float Share) Majority(ScenarioState state)
    {
        if (state.PopulationGroups is null || state.PopulationGroups.Count == 0 || state.Population <= 0) return ("", 0f);
        var byCulture = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var group in state.PopulationGroups)
        {
            if (string.IsNullOrEmpty(group.Culture) || group.Size <= 0) continue;
            byCulture[group.Culture] = byCulture.GetValueOrDefault(group.Culture) + group.Size;
            total += group.Size;
        }
        if (total <= 0) return ("", 0f);
        var best = byCulture.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First();
        return (best.Key, best.Value / (float)total);
    }

    /// <summary>
    /// Another people's homeland that is ready to go: regions where that people is the majority
    /// and the region is pressing to leave or angry, grown along its own kind. The people with the
    /// most pressure behind it goes first.
    /// </summary>
    private static List<ScenarioState> NationalGround(Campaign c, Nation nation, List<ScenarioState> owned,
        Dictionary<int, HashSet<int>> adjacency, long ceiling, out string culture)
    {
        culture = "";
        var d = c.Defines;
        var ours = new HashSet<string>(DemographyCatalog.PrimaryCultures(nation.Tag), StringComparer.OrdinalIgnoreCase);
        foreach (var people in nation.FoundedCultures) ours.Add(people);
        var ready = new Dictionary<int, string>();
        foreach (var state in owned)
        {
            var (majority, share) = Majority(state);
            if (majority.Length == 0 || share < 0.5f || ours.Contains(majority)) continue;
            if (state.SecessionPressure < d.CollapseNationalPressure && state.Radicalism < d.CollapseNationalRadicalism) continue;
            ready[state.MapIndex] = majority;
        }
        if (ready.Count == 0) return new();
        var seed = owned.Where(s => ready.ContainsKey(s.MapIndex) && s.Population <= ceiling)
            .OrderByDescending(s => s.SecessionPressure + s.Radicalism).ThenBy(s => s.MapIndex).FirstOrDefault();
        if (seed is null) return new();
        var chosen = ready[seed.MapIndex];
        culture = chosen;
        return Grow(owned, adjacency, seed, s => -s.SecessionPressure, long.MaxValue, ceiling, owned.Count - 1,
            s => ready.TryGetValue(s.MapIndex, out var theirs) && string.Equals(theirs, chosen, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------- revolution

    /// <summary>How full the capital's street is, 0 to 1: the protest wave where it is measured, else the capital's own anger.</summary>
    public static float Street(Campaign c, Nation nation)
    {
        var capital = Capital(c, nation);
        if (capital is null) return 0f;
        var measured = c.Defines.ProtestsEnabled ? nation.DomesticCrisis?.ProtestIntensity ?? 0f : 0f;
        var floor = M.Clamp(c.Defines.ProtestRadicalismFloor, 0f, 0.9f);
        var anger = M.Clamp01((M.Clamp01(capital.Radicalism) - floor) / (1f - floor));
        var openness = ParliamentaryPolitics.Eligible(nation) ? 1f : c.Defines.ProtestRepressedTurnout;
        var turnout = (0.35f + 0.65f * ResidentAwareness.Of(c, capital)) * openness;
        var crowd = M.Clamp01(anger * turnout * (1f + M.Clamp01(0.45f - nation.Stability)));
        return MathF.Max(M.Clamp01(measured), crowd);
    }

    /// <summary>
    /// How little the army will do for this government, 0 to 1: the officers' strain the coup
    /// machinery already keeps (DomesticCrises), and more for soldiers not paid or being beaten.
    /// </summary>
    public static float ArmyDisloyalty(Campaign c, Nation nation)
    {
        var value = M.Clamp01(nation.DomesticCrisis?.CoupPressure ?? 0f);
        if (nation.PaymentArrears > 0f || nation.Treasury < 0f || nation.DebtCondition is DebtCondition.Default or DebtCondition.Insolvent)
            value += 0.2f;
        if (LosingWar(c, nation)) value += 0.15f;
        return M.Clamp01(value);
    }

    private static ScenarioState Capital(Campaign c, Nation nation)
    {
        foreach (var state in c.StatesOf(nation))
            if (state.MapIndex == nation.CapitalStateMapIndex) return state;
        return null;
    }

    /// <summary>Why this government cannot fall to a revolution now, or "" when it can.</summary>
    public static string RevolutionRefusal(Campaign c, Nation nation)
    {
        if (c is null || nation is null) return "No governed country selected.";
        var d = c.Defines;
        if (!d.Revolutions) return "Revolutions are disabled.";
        if (nation.Movement is not null || nation.FactionId.Length > 0 || nation.IsSubject || c.Fallen.Contains(nation.Id) ||
            nation.Population <= 0) return "Only an independent state has a government to overthrow.";
        if (nation.IsHuman && !d.CivilWarStrikesPlayers) return "Random outbreaks in player countries are disabled in campaign settings.";
        if (c.ElapsedDays - (long)nation.LastRevolutionDay < d.RevolutionCooldownDays) return "A revolution has only just happened.";
        if (nation.DomesticCrisis?.CooldownUntilDay > c.ElapsedDays) return "The new rulers still hold the army.";
        if (!float.IsFinite(nation.Stability) || nation.Stability >= d.RevolutionStabilityThreshold) return "The state is too settled for a revolution.";
        var capital = Capital(c, nation);
        if (capital is null || !float.IsFinite(capital.Radicalism)) return "The government does not hold its capital.";
        if (capital.Radicalism < d.RevolutionCapitalRadicalism) return "The capital is not radicalised enough.";
        if (Street(c, nation) < d.RevolutionStreet) return "The street has not filled.";
        if (ArmyDisloyalty(c, nation) < d.RevolutionArmyDisloyalty) return "The army still obeys the government.";
        if (RevolutionaryCreed(c, nation, capital).Length == 0) return "The capital wants nothing the government is not already.";
        return "";
    }

    /// <summary>What the capital believes that its government does not: the creed a revolution there takes power in.</summary>
    private static string RevolutionaryCreed(Campaign c, Nation nation, ScenarioState capital)
    {
        foreach (var share in PoliticalMood.RegionShares(c, capital, nation))
            if (!share.Governing && IdeologyCatalog.Knows(share.Ideology)) return share.Ideology;
        return "";
    }

    private static int RevolutionsInLastYear(Campaign c) =>
        c.Nations.Count(n => n.LastRevolutionDay >= 0 && c.ElapsedDays - (long)n.LastRevolutionDay < 365);

    private static void ConsiderRevolution(Campaign c, Nation nation)
    {
        var d = c.Defines;
        if (nation.Stability >= d.RevolutionStabilityThreshold || nation.Population <= 0) return;   // the cheap test first
        if (RevolutionRefusal(c, nation).Length > 0) return;
        Count("revolution_ripe_months");
        if (RevolutionsInLastYear(c) >= d.RevolutionWorldPerYear) return;
        if (Escalation.Roll(c.Seed, nation.Id, 0x4E70, c.ElapsedDays, 0x2) >= d.RevolutionMonthlyChance) return;
        Revolution(c, nation);
    }

    /// <summary>
    /// The government falls to its capital. The capital's creed takes the state under a
    /// provisional government; the loyal regions may fight on for the old one; each side's
    /// co-believers abroad take it up. Returns the old regime's government in arms, if any.
    /// </summary>
    public static Nation Revolution(Campaign c, Nation nation, bool forceCivilWar = false)
    {
        if (c is null || nation is null) return null;
        var capital = Capital(c, nation);
        var creed = capital is null ? "" : RevolutionaryCreed(c, nation, capital);
        if (creed.Length == 0) return null;
        var d = c.Defines;
        var oldIdeology = nation.Ideology;
        var oldGovernment = nation.Government;
        nation.Ideology = creed;
        nation.Government = Known("provisional_government", nation.Government);
        nation.PoliticsChangedDay = c.ElapsedDays;
        nation.LastRevolutionDay = c.ElapsedDays;
        // The crowd has what it wanted, for now; and a new order is briefly believed in.
        if (capital is not null) capital.Radicalism = M.Clamp01(capital.Radicalism * 0.6f);
        nation.Stability = M.Clamp01(nation.Stability + 0.05f);
        Coups.RegisterOutcome(c, nation, "Revolution");
        c.Chronicle.Record(c, "revolution", nation.Id);
        Count("revolutions");
        Log.Add($"day {c.ElapsedDays} REVOLUTION {nation.Tag} {oldGovernment}/{oldIdeology} -> {nation.Government}/{creed} stability {nation.Stability:0.00}");
        DiagnosticLogs.Write("simulation", $"REVOLUTION nation={nation.Tag} from={oldIdeology} to={creed} day={c.ElapsedDays}");
        c.MarkStrategicChange();

        var roll = Escalation.Roll(c.Seed, nation.Id, 0x4E71, c.ElapsedDays, 0x3);
        if (!forceCivilWar && roll >= d.RevolutionCivilWarChance) return null;
        if (c.Wars.Any(w => !w.Ended && w.CivilConflict && w.Involves(nation.Id)) && !forceCivilWar) return null;
        var owned = Breakable(c, nation);
        if (owned.Count < 2) return null;
        var adjacency = Adjacency(c, owned);
        var share = d.CivilWarRebelShareMin + (d.CivilWarRebelShareMax - d.CivilWarRebelShareMin) *
            Escalation.Roll(c.Seed, nation.Id, 0x4E72, c.ElapsedDays, 0x4);
        var target = (long)(nation.Population * (double)share);
        var ceiling = (long)(nation.Population * (double)d.CivilWarRebelShareMax);
        // The old order holds out where it was least resented.
        float Loyalty(ScenarioState s) => s.Radicalism - 0.3f * s.Stability;
        var ground = Grow(owned, adjacency, owned.OrderBy(Loyalty).ThenBy(s => s.MapIndex).First(), Loyalty, target, ceiling,
            Math.Max(1, (int)(owned.Count * 0.6f)));
        if (ground.Count == 0 || ground.Sum(s => s.Population) < nation.Population * 0.06f) return null;
        var parentTag = nation.Tag;
        var parentName = nation.Name;
        var report = SecessionEvents.Uprising(c, nation, ground, born =>
        {
            born.Name = $"rising:loyalist_government:{parentTag}:{parentName}";
            born.Ideology = oldIdeology;
            born.Government = oldGovernment;
            born.LastCivilWarDay = c.ElapsedDays;
            if (nation.DeclaredReligion.Length > 0) born.DeclaredReligion = nation.DeclaredReligion;
        });
        if (report?.Seceded is not { } loyalists) return null;
        nation.LastCivilWarDay = c.ElapsedDays;
        Count("revolution_civil_wars");
        Log.Add($"day {c.ElapsedDays}   {nation.Tag} old regime fights on as {loyalists.Tag} regions {ground.Count} people {report.Population:N0}");
        var war = c.Wars.LastOrDefault(w => !w.Ended && w.Involves(nation.Id) && w.Involves(loyalists.Id));
        if (war is not null)
        {
            Count("revolution_sponsors", Backers(c, war, nation));
            Count("revolution_sponsors", Backers(c, war, loyalists));
        }
        return loyalists;
    }

    /// <summary>
    /// The governments of a side's creed that take it up at once: those with a stake in the war
    /// (ProxyWars.HasStake) that can pay for it, the largest first. A player's government chooses
    /// its own wars to back, with the same command, and is only told.
    /// </summary>
    public static int Backers(Campaign c, StrategicWar war, Nation client)
    {
        if (c is null || war is null || client is null || !MechanicFeatures.SystemEnabled(c, nameof(ProxyWars))) return 0;
        var enemy = c.NationById(war.EnemyLeaderOf(client.Id));
        if (enemy is null) return 0;
        var added = 0;
        foreach (var sponsor in c.Nations.Where(n => !n.IsHuman && n.Id != client.Id && n.Id != enemy.Id &&
                     n.Movement is null && !n.IsSubject && n.Population > 0 && !c.Fallen.Contains(n.Id) &&
                     string.Equals(n.Ideology, client.Ideology, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(n.Ideology, enemy.Ideology, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(n => n.Gdp).ThenBy(n => n.Id))
        {
            if (added >= Math.Max(0, c.Defines.RevolutionSponsorsPerSide)) break;
            if (!ProxyWars.HasStake(c, sponsor, client, enemy, war)) continue;
            if (ProxyWars.Interest(c, sponsor, client, enemy, war) < 0f) continue;
            var level = Math.Min(2, ProxyWars.MaximumLevel(sponsor));
            if (level <= 0 || ProxyWars.Block(c, sponsor, war, client, level).Length > 0) continue;
            if (ProxyWars.Sponsor(c, sponsor, war, client, level)) added++;
        }
        return added;
    }

    // ---------------------------------------------------------------------------- reunion

    /// <summary>
    /// Who speaks for a state that came apart now: itself while it stands, otherwise the piece of
    /// it that took its capital and its name (Factions.TakeOver).
    /// </summary>
    public static Nation Heir(Campaign c, Nation parent)
    {
        if (c is null || parent is null) return null;
        if (!c.Fallen.Contains(parent.Id) && parent.Population > 0 && parent.Movement is null && c.HasOwnedLand(parent)) return parent;
        foreach (var nation in c.Nations)
            if (nation.Id != parent.Id && nation.RevoltParentId == parent.Id && !c.Fallen.Contains(nation.Id) &&
                nation.Population > 0 && nation.Movement is null && string.Equals(nation.Name, parent.Name, StringComparison.Ordinal))
                return nation;
        return null;
    }

    /// <summary>Why this piece will not return to the state it came from now, or "".</summary>
    public static string ReunionRefusal(Campaign c, Nation piece, Nation centre)
    {
        var d = c.Defines;
        if (piece is null || centre is null || piece.Id == centre.Id) return "Choose a country to reunite with.";
        if (c.Fallen.Contains(piece.Id) || c.Fallen.Contains(centre.Id) || piece.Population <= 0 || centre.Population <= 0)
            return "Both countries must be alive to unite.";
        if (piece.Movement is not null || centre.Movement is not null || piece.IsSubject || centre.IsSubject)
            return "Both countries must be sovereign to unite by consent.";
        if (c.Wars.Any(w => !w.Ended && (w.Involves(piece.Id) || w.Involves(centre.Id))))
            return "Union is a peace-time act; neither country may be at war.";
        var born = Math.Max(piece.LastSecessionDay, piece.LastCivilWarDay);
        if (c.ElapsedDays - (long)born < d.ReunionEarliestDays) return "The wounds of the split are too fresh.";
        if (centre.Stability < d.ReunionCentreStability) return "The old centre has not recovered.";
        var ours = DemographyCatalog.PrimaryCulture(centre.Tag);
        var theirs = DemographyCatalog.PrimaryCulture(piece.Tag);
        if (string.IsNullOrEmpty(ours) || !string.Equals(ours, theirs, StringComparison.OrdinalIgnoreCase))
            return "A union of peoples needs peoples that are the same.";
        if (!CultureUnion.SharesBorder(c, centre, piece)) return "The countries must share a border to become one.";
        var opinion = CampaignDiplomacy.Peek(c, piece.Id, centre.Id)?.Opinion ?? 0f;
        if (opinion < d.ReunionMinimumOpinion) return "Relations must not be hostile before union talks.";
        // A failing piece takes a centre it does not quite agree with; a thriving one only a
        // centre of nearly its own mind. Neither joins a centre of the opposite creed: an
        // insurgency of believers does not sign itself over to the republic it fought.
        var distance = PoliticsCore.IdeologicalDistance(piece, centre);
        var failing = piece.Stability < d.ReunionPieceStability && distance <= d.ReunionFailingIdeologyDistance;
        var close = c.StatesOf(piece).Length <= d.ReunionPieceMaxRegions && distance <= d.ReunionIdeologyDistance;
        if (!failing && !close) return "It is doing well enough on its own.";
        return "";
    }

    private static void ConsiderReunion(Campaign c, Nation piece)
    {
        if (piece.RevoltParentId < 0 || piece.Population <= 0 || c.Fallen.Contains(piece.Id)) return;
        var parent = c.NationById(piece.RevoltParentId);
        var centre = Heir(c, parent);
        if (centre is null || centre.Id == piece.Id) return;
        // A player's government decides its own: a piece it plays is never folded in, and a
        // centre it plays reunites only when it has handed diplomacy over, as for any union.
        if (piece.IsHuman && !c.ObserverMode) return;
        if (centre.IsHuman && !c.ObserverMode && !Delegation.Holds(centre, Delegation.Diplomacy)) return;
        if (ReunionRefusal(c, piece, centre).Length > 0) return;
        if (Escalation.Roll(c.Seed, piece.Id, 0x2E01, c.ElapsedDays, centre.Id) >= c.Defines.ReunionMonthlyChance) return;
        Reunite(c, centre, piece);
    }

    /// <summary>The piece returns: its ground, people, army and books become the centre's again.</summary>
    public static bool Reunite(Campaign c, Nation centre, Nation piece)
    {
        if (c is null || centre is null || piece is null || centre.Id == piece.Id) return false;
        var place = piece.DisplayName;
        PeacefulFormation.IntegratePartner(c, centre, piece, transferPlayerSeat: false);
        if (!c.Fallen.Contains(piece.Id)) return false;
        c.Chronicle.Record(c, "collapse_reunion", centre.Id, piece.Id, place: place);
        Count("reunions");
        Log.Add($"day {c.ElapsedDays} REUNION {piece.Tag} -> {centre.Tag}");
        DiagnosticLogs.Write("simulation", $"COLLAPSE_REUNION centre={centre.Tag} piece={piece.Tag} day={c.ElapsedDays}");
        c.MarkMapChanged();
        c.MarkStrategicChange();
        return true;
    }
}
