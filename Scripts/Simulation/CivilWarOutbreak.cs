using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// A state that has been coming apart for long enough comes apart: below this stability,
    /// once a week, a chance (this much at zero stability, less above it) that part of the
    /// country goes over to an armed opposition. A coup or a counter-coup in the last
    /// CivilWarCoupMemoryDays doubles it.
    /// </summary>
    public bool CivilWarOutbreaks { get; set; } = true;
    public float CivilWarStabilityThreshold { get; set; } = 0.42f;
    public float CivilWarWeeklyChance { get; set; } = 0.016f;
    /// <summary>The most civil wars the whole world begins in a year. A century-old campaign
    /// whose stability has drifted down everywhere began thirteen in one year - Japan and
    /// Spain among them - without it.</summary>
    public int CivilWarWorldPerYear { get; set; } = 7;
    /// <summary>
    /// The chance a coup splits the army instead of taking it: the officers who would not go
    /// along hold part of the country, and it is a civil war from that day (Spain 1936, Sudan
    /// 2023). A plot found out turns into one at the second chance. Neither waits for the
    /// stability line; both still count against the world's year.
    /// </summary>
    public float CivilWarFromCoupChance { get; set; } = 0.3f;
    public float CivilWarFromFoiledPlotChance { get; set; } = 0.12f;
    public int CivilWarCoupMemoryDays { get; set; } = 365;
    public int CivilWarEarliestDay { get; set; } = 120;
    public int CivilWarCooldownDays { get; set; } = 1825;
    public long CivilWarMinimumPopulation { get; set; } = 2_000_000;
    public int CivilWarMinimumRegions { get; set; } = 4;
    /// <summary>How much of the country the rising takes, as a share of its people.</summary>
    public float CivilWarRebelShareMin { get; set; } = 0.15f;
    public float CivilWarRebelShareMax { get; set; } = 0.40f;
    /// <summary>Whether a country somebody plays can have one. On by default: a player's
    /// country and the computer's live by the same rules. Off is the campaign option for a
    /// player who wants its state to fall apart only by its own choices.</summary>
    public bool CivilWarStrikesPlayers { get; set; } = true;
    /// <summary>How much faster a state's minorities leave while it fights a civil war.</summary>
    public float SecessionCivilWarMultiplier { get; set; } = 3f;
    /// <summary>How much imperial overstretch amplifies secession pressure across foreign regions.</summary>
    public float SecessionOverstretchMultiplier { get; set; } = 0.50f;
}

/// <summary>
/// Civil wars that begin, rather than only the ones 2024 already had.
///
/// The world opened with Sudan, Myanmar, Somalia and the Congo at war with themselves, and
/// in a hundred years of play nothing else ever was: a country could sink to a fifth of its
/// stability, live through three coups, and stay one country with one army. States do not
/// do that. When a government has lost the country for long enough, part of the country
/// stops obeying it - the regions that are already the least governed and the most
/// radical, around the one that is worst - and an armed opposition holds them: a national
/// salvation front against a liberal government, a transitional council against a junta,
/// a revolutionary front against a nationalist one.
///
/// It is a civil war from its first day (SecessionEvents.Uprising): garrisons on its
/// ground go over, it raises its militia, and while it lasts the state's minorities leave
/// three times as readily (SecessionCivilWarMultiplier) - the partial collapse a weak centre
/// invites, which is how Yugoslavia and Somalia went.
/// </summary>
public static class CivilWarOutbreak
{
    public static readonly List<string> Recent = new();

    /// <summary>A rolling simulation limit, independent of the truncated presentation chronicle.</summary>
    public static int StartsInLastYear(Campaign campaign) => campaign is null ? 0 :
        Budget(campaign).Count(campaign.ElapsedDays);

    internal static CivilWarBudgetState Budget(Campaign campaign) => campaign.CivilWarBudget ??
        CivilWarBudgetState.FromLegacy(campaign);

    internal static void RecordStart(Campaign campaign)
    {
        campaign.CivilWarBudget ??= CivilWarBudgetState.FromLegacy(campaign);
        campaign.CivilWarBudget.Record(campaign.ElapsedDays);
    }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same weekly outbreak roll, yielded between batches of nations.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        var d = campaign?.Defines;
        if (d is null) yield break;
        // Expired budget entries are administrative cleanup, including while outbreaks
        // are disabled. Turning the mechanic back on cannot resurrect an old year.
        campaign.CivilWarBudget ??= CivilWarBudgetState.FromLegacy(campaign);
        campaign.CivilWarBudget.Prune(campaign.ElapsedDays);
        if (!d.CivilWarOutbreaks || campaign.ProvinceMap is null) yield break;
        if (!MechanicFeatures.SystemEnabled(campaign, "SecessionEvents")) yield break;
        if (campaign.ElapsedDays < d.CivilWarEarliestDay || campaign.ElapsedDays % 7 != 3) yield break;
        var thisYear = StartsInLastYear(campaign);
        var scanned = 0;
        for (var i = 0; i < campaign.Nations.Count; i++)
        {
            var nation = campaign.Nations[i];
            if (thisYear >= d.CivilWarWorldPerYear) yield break;
            if (++scanned % 8 == 0 || Campaign.StageOverdue()) yield return 0;
            if (!Eligible(campaign, nation)) continue;
            var chance = Chance(campaign, nation);
            if (campaign.ElapsedDays < 730)
                chance = Math.Clamp(chance * FlashpointIntensityMultiplier(campaign, nation), 0f, 1f);
            if (!float.IsFinite(chance) || chance <= 0f) continue;
            if (Escalation.Roll(campaign.Seed, nation.Id, 0xC1C, campaign.ElapsedDays, 0x3) >= chance) continue;
            // Start the transfer in a fresh slice. The transfer itself uses the ordinary
            // secession stages for the new state, its forces and the war.
            Campaign.HeavyNext();
            yield return 0;
            var prevCount = campaign.Nations.Count;
            SecessionReport outbreak = null;
            foreach (var _ in BreakStages(campaign, nation, r => outbreak = r)) yield return 0;
            if (outbreak is not null)
            {
                thisYear++;
                // Break appends the rebel nation at the end of campaign.Nations, which doesn't shift indices <= i.
                if (campaign.Nations.Count < prevCount) i = Math.Min(i, campaign.Nations.Count - 1);
            }
        }
    }

    private static bool Eligible(Campaign c, Nation nation) => EligibilityReason(c, nation).Length == 0;

    /// <summary>
    /// A coup that splits the army (Coups): at chance, part of the country goes over to the
    /// officers who would not follow - whatever its stability, within the world's year.
    /// </summary>
    public static SecessionReport FromCoup(Campaign c, Nation nation, float chance, int salt)
    {
        SecessionReport report = null;
        foreach (var _ in FromCoupStages(c, nation, chance, salt, r => report = r)) { }
        return report;
    }

    internal static IEnumerable<int> FromCoupStages(Campaign c, Nation nation, float chance, int salt,
        Action<SecessionReport> onDone = null)
    {
        if (c?.ProvinceMap is null || nation is null || chance <= 0f || !c.Defines.CivilWarOutbreaks) yield break;
        if (StartsInLastYear(c) >= c.Defines.CivilWarWorldPerYear) yield break;
        if (EligibilityReason(c, nation, ignoreStability: true).Length > 0) yield break;
        if (Escalation.Roll(c.Seed, nation.Id, 0xC0C, c.ElapsedDays, salt) >= chance) yield break;
        foreach (var _ in BreakStages(c, nation, report =>
                 {
                     c.Chronicle.Record(c, "coup_civil_war", nation.Id, report.Seceded.Id);
                     onDone?.Invoke(report);
                 }, ignoreStability: true))
            yield return 0;
    }

    /// <summary>The same gates for the simulation and its explanation; no hidden UI approximation.</summary>
    public static string EligibilityReason(Campaign c, Nation nation) => EligibilityReason(c, nation, false);

    private static string EligibilityReason(Campaign c, Nation nation, bool ignoreStability)
    {
        if (c is null || nation is null) return "No governed country selected.";
        var d = c.Defines;
        if (!d.CivilWarOutbreaks || !MechanicFeatures.SystemEnabled(c, "SecessionEvents")) return "Civil-war outbreaks are disabled.";
        if (!float.IsFinite(nation.Stability) || !float.IsFinite(d.CivilWarStabilityThreshold)) return "The campaign data is damaged, so this cannot be done.";
        if (nation.Movement is not null || nation.FactionId.Length > 0 || nation.IsSubject || c.Fallen.Contains(nation.Id))
            return "Only an independent state can have a civil war.";
        if (nation.IsHuman && !d.CivilWarStrikesPlayers) return "Random outbreaks in player countries are disabled in campaign settings.";
        if (c.ElapsedDays < d.CivilWarEarliestDay) return "The campaign is still in its initial protection period.";
        if (nation.LastCivilWarDay > 0 && c.ElapsedDays - nation.LastCivilWarDay < d.CivilWarCooldownDays)
            return "The country is protected from another outbreak during recovery.";
        // One civil war at a time: a state already fighting its own rebels is not split twice.
        var liveWars = Warfare.LiveWars(c);
        for (var wi = 0; wi < liveWars.Count; wi++)
        {
            var w = liveWars[wi];
            if (!w.Ended && w.CivilConflict && w.Involves(nation.Id))
                return "An existing civil war must end before another outbreak.";
        }
        var nationStates = c.StatesOf(nation);
        var popRegions = 0;
        for (var si = 0; si < nationStates.Length; si++)
            if (nationStates[si].Population > 0) popRegions++;
        if (nation.Population < d.CivilWarMinimumPopulation || popRegions < d.CivilWarMinimumRegions)
            return "Too few residents or governed regions for a territorial civil war.";
        if (!ignoreStability && nation.Stability >= d.CivilWarStabilityThreshold) return "Institutions remain above the civil-war instability threshold.";
        return "";
    }

    /// <summary>The weekly chance, from how far below the line the state has sunk.</summary>
    public static float Chance(Campaign c, Nation nation)
    {
        var d = c?.Defines;
        if (d is null || nation is null || !float.IsFinite(nation.Stability) || !float.IsFinite(d.CivilWarWeeklyChance) ||
            !float.IsFinite(d.CivilWarStabilityThreshold)) return 0f;
        var depth = M.Clamp01((d.CivilWarStabilityThreshold - nation.Stability) / MathF.Max(0.01f, d.CivilWarStabilityThreshold));
        var chance = d.CivilWarWeeklyChance * depth;
        if (RecentMilitaryCoup(c, nation)) chance *= 2f;
        if (LosingForeignWar(c, nation)) chance *= 1.5f;
        if (nation.ImperialOverstretch > 0.05f) chance *= (1f + nation.ImperialOverstretch * 0.75f);
        if (c.ElapsedDays >= 730)
        {
            var epochRamp = M.Clamp01((c.ElapsedDays - 730f) / 365f);
            var wave = GeopoliticalCycle.MacroEconomicWave(c, nation);
            var downturn = wave < -0.15f ? 1f + (-wave - 0.15f) * 0.65f : 1f;
            var sclerosis = 1f + GeopoliticalCycle.InstitutionalSclerosis(c, nation) * 0.40f;
            var epochScale = GeopoliticalCycle.SystemicFragmentationPressure(c, nation) *
                             FlashpointIntensityMultiplier(c, nation) *
                             downturn * sclerosis *
                             (1f + (AiTemperament.Of(c, nation).Volatility - 0.5f) * 0.25f);
            chance *= M.Lerp(1f, epochScale, epochRamp);
        }
        return float.IsFinite(chance) ? Math.Clamp(chance, 0f, 1f) : 0f;
    }

    private static float FlashpointIntensityMultiplier(Campaign c, Nation nation)
    {
        if (c is null || nation is null || c.ElapsedDays <= 0) return 1f;
        if (!string.Equals(nation.Tag, "SYR", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(nation.Tag, "MMR", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(nation.Tag, "COD", StringComparison.OrdinalIgnoreCase))
            return 1f;
        var decade = GeopoliticalCycle.DecadeIndex(c.ElapsedDays);
        var hash = GeopoliticalCycle.HashNationEpoch(c.Seed, nation, decade, 0xF1A5);
        return 0.78f + GeopoliticalCycle.HashToUnit(hash) * 0.54f;
    }

    /// <summary>
    /// Why <see cref="Chance"/> is the number it is, as localisation keys. Only the factors
    /// that are actually on are named, in the order the chance multiplies them.
    /// </summary>
    public static string[] Factors(Campaign c, Nation nation)
    {
        if (c is null || nation is null || Chance(c, nation) <= 0f) return Array.Empty<string>();
        var factors = new List<string> { "The weekly chance starts from how far stability sits below the line." };
        if (RecentMilitaryCoup(c, nation)) factors.Add("A recent military coup doubles that chance.");
        if (LosingForeignWar(c, nation)) factors.Add("A losing war abroad raises that chance.");
        if (nation.ImperialOverstretch > 0.05f) factors.Add("Imperial overstretch raises that chance.");
        return factors.ToArray();
    }

    /// <summary>Both coup routes set LastCoupDay. This stamp keeps a military coup's multiplier after a later institutional transition.</summary>
    private static bool RecentMilitaryCoup(Campaign c, Nation nation)
    {
        var coupDay = nation.LastMilitaryCoupDay;
        var memory = c.Defines.CivilWarCoupMemoryDays;
        return coupDay >= 0 && coupDay <= c.ElapsedDays && c.ElapsedDays - (long)coupDay <= memory;
    }

    /// <summary>A country at war abroad and losing has its army away from home.</summary>
    private static bool LosingForeignWar(Campaign c, Nation nation)
    {
        var liveWars = Warfare.LiveWars(c);
        for (var i = 0; i < liveWars.Count; i++)
        {
            var war = liveWars[i];
            if (!war.Ended && !war.CivilConflict && war.Involves(nation.Id) &&
                (war.IsAttacking(nation.Id) ? war.WarScore : -war.WarScore) < -10f)
                return true;
        }
        return false;
    }

    internal static void RestoreMilitaryCoupMemory(Campaign campaign)
    {
        var days = new Dictionary<int, int>();
        foreach (var fact in campaign.Chronicle.Entries)
            if (fact.Kind is "coup" or "coup_counter" && fact.Day >= 0 && fact.Day <= campaign.ElapsedDays)
                days[fact.ActorId] = Math.Max(days.GetValueOrDefault(fact.ActorId, -99999), fact.Day);
        foreach (var nation in campaign.Nations)
        {
            if (nation.LastMilitaryCoupDay >= 0) continue; // current saves retain their independent date
            var day = days.GetValueOrDefault(nation.Id, -99999);
            var latest = nation.LastCoupDay;
            var outcome = nation.DomesticCrisis;
            var knownInstitutional = outcome is not null && outcome.LastOutcomeDay == latest &&
                !string.IsNullOrEmpty(outcome.LastOutcomeId) && outcome.LastOutcomeId != "Military coup";
            // Old saves had only one coup date. An explicit institutional outcome is not
            // promoted to a military coup; absent outcome metadata uses the stated fallback.
            if (!knownInstitutional && latest >= 0 && latest <= campaign.ElapsedDays)
                day = Math.Max(day, latest);
            if (day >= 0) nation.LastMilitaryCoupDay = day;
        }
    }

    /// <summary>Who rises, where, under what name - and the war.</summary>
    internal static SecessionReport Break(Campaign c, Nation nation, bool ignoreStability = false)
    {
        SecessionReport report = null;
        foreach (var _ in BreakStages(c, nation, r => report = r, ignoreStability)) { }
        return report;
    }

    private static IEnumerable<int> BreakStages(Campaign c, Nation nation, Action<SecessionReport> onDone,
        bool ignoreStability = false)
    {
        if (EligibilityReason(c, nation, ignoreStability).Length > 0) yield break;
        var regions = RebelGround(c, nation);
        if (regions.Count == 0) yield break;
        var oppositionSalt = unchecked((int)GeopoliticalCycle.HashNationEpoch(
            c.Seed, nation, GeopoliticalCycle.DecadeIndex(c.ElapsedDays), 0x0902));
        var ideology = OppositionTo(nation.Ideology, oppositionSalt);
        var template = ideology switch
        {
            "liberalism" or "social_democracy" => "transitional_council",
            "socialism" or "communism" => "revolutionary_front",
            _ => "salvation_front"
        };
        var parentTag = nation.Tag;
        var parentName = nation.Name;
        var rebelFaith = BeliefDynamics.RebelFaith(c, nation, regions);
        SecessionReport report = null;
        foreach (var _ in SecessionEvents.UprisingStages(c, nation, regions, r => report = r, born =>
        {
            born.Name = $"rising:{template}:{parentTag}:{parentName}";
            born.Ideology = ideology;
            born.Government = "provisional_government";
            born.LastCivilWarDay = c.ElapsedDays;
            if (rebelFaith.Length > 0) born.DeclaredReligion = rebelFaith;
        })) yield return 0;
        if (report?.Seceded is null) yield break;
        nation.LastCivilWarDay = c.ElapsedDays;
        Recent.Add($"{nation.Tag}->{report.Seceded.Tag} day {c.ElapsedDays} regions {regions.Count} people {report.Population}");
        if (DiagnosticLogs.Enabled)
            DiagnosticLogs.Write("simulation", $"CIVIL_WAR state={nation.Tag} rebels={report.Seceded.Tag} " +
                $"regions={regions.Count} people={report.Population} stability={nation.Stability:0.00} ideology={ideology}");
        onDone?.Invoke(report);
    }

    [ThreadStatic] private static List<ScenarioState> _rebelOwnedScratch;
    [ThreadStatic] private static Dictionary<int, ScenarioState> _rebelByIndexScratch;
    [ThreadStatic] private static Dictionary<int, HashSet<int>> _rebelAdjScratch;
    [ThreadStatic] private static Stack<HashSet<int>> _rebelSetPool;
    [ThreadStatic] private static HashSet<int> _rebelInsideScratch;
    [ThreadStatic] private static HashSet<int> _rebelPassedScratch;

    /// <summary>
    /// The ground: the worst-governed region that is not the capital, and its neighbours in
    /// order of how little they are governed, until the rising holds its share of the people.
    /// Contiguous by construction - an armed opposition holds a territory, not a scatter.
    /// </summary>
    private static List<ScenarioState> RebelGround(Campaign c, Nation nation)
    {
        var d = c.Defines;
        if (!float.IsFinite(d.CivilWarRebelShareMin) || !float.IsFinite(d.CivilWarRebelShareMax) ||
            d.CivilWarRebelShareMin < 0f || d.CivilWarRebelShareMax > .6f ||
            d.CivilWarRebelShareMin > d.CivilWarRebelShareMax) return new();

        var owned = _rebelOwnedScratch ??= new List<ScenarioState>();
        owned.Clear();
        var nationStates = c.StatesOf(nation);
        for (var i = 0; i < nationStates.Length; i++)
        {
            var s = nationStates[i];
            if (s.Population > 0 && s.MapIndex != nation.CapitalStateMapIndex &&
                RevoltRecovery.DaysLeft(c, nation, s) == 0 &&
                float.IsFinite(s.Stability) && float.IsFinite(s.Radicalism))
                owned.Add(s);
        }
        if (owned.Count < 2) return new();

        static float Grievance(ScenarioState s) => s.Stability - 0.6f * s.Radicalism;

        var byIndex = _rebelByIndexScratch ??= new Dictionary<int, ScenarioState>();
        byIndex.Clear();
        var adjacent = _rebelAdjScratch ??= new Dictionary<int, HashSet<int>>();
        var setPool = _rebelSetPool ??= new Stack<HashSet<int>>();
        foreach (var existing in adjacent.Values)
        {
            existing.Clear();
            setPool.Push(existing);
        }
        adjacent.Clear();

        for (var i = 0; i < owned.Count; i++)
        {
            var s = owned[i];
            byIndex[s.MapIndex] = s;
            adjacent[s.MapIndex] = setPool.Count > 0 ? setPool.Pop() : new HashSet<int>();
        }

        var map = c.ProvinceMap;
        var pts = map.ProvinceToState;
        var landNeighbors = map.LandNeighbors;
        for (var i = 0; i < owned.Count; i++)
        {
            var here = owned[i].MapIndex;
            var set = adjacent[here];
            var provinces = map.ProvincesOfState(here);
            for (var pi = 0; pi < provinces.Length; pi++)
            {
                // Drawn borders: a side in a civil war is regions that touch on the map, not ones a
                // ferry runs between (ProvinceTopology.LandNeighbors).
                var neighbors = landNeighbors[provinces[pi]];
                for (var ni = 0; ni < neighbors.Length; ni++)
                {
                    var there = pts[neighbors[ni]];
                    if (there != here && byIndex.ContainsKey(there)) set.Add(there);
                }
            }
        }
        var share = d.CivilWarRebelShareMin + (d.CivilWarRebelShareMax - d.CivilWarRebelShareMin) *
            Escalation.Roll(c.Seed, nation.Id, 0x5A1, c.ElapsedDays, 0x9);
        var target = (long)(nation.Population * share);
        var seed = owned[0];
        var seedG = Grievance(seed);
        for (var i = 1; i < owned.Count; i++)
        {
            var s = owned[i];
            var g = Grievance(s);
            if (g < seedG || (g == seedG && s.MapIndex < seed.MapIndex))
            {
                seed = s;
                seedG = g;
            }
        }
        var taken = new List<ScenarioState> { seed };
        var inside = _rebelInsideScratch ??= new HashSet<int>();
        inside.Clear();
        inside.Add(seed.MapIndex);
        long people = seed.Population;
        // Never the whole country less the capital: the government keeps a real state.
        var limit = Math.Max(1, (int)(owned.Count * 0.6f));
        // Whole regions, never past the most a rising may take: a region that would carry it
        // over the line is passed over for a smaller neighbour (Yemen lost 57 % in one rising).
        var ceiling = (long)(nation.Population * d.CivilWarRebelShareMax);
        var passed = _rebelPassedScratch ??= new HashSet<int>();
        passed.Clear();
        while (people < target && taken.Count < limit)
        {
            ScenarioState next = null;
            var nextG = float.PositiveInfinity;
            for (var ti = 0; ti < taken.Count; ti++)
            {
                foreach (var idx in adjacent[taken[ti].MapIndex])
                {
                    if (inside.Contains(idx) || passed.Contains(idx)) continue;
                    var cand = byIndex[idx];
                    var g = Grievance(cand);
                    if (next is null || g < nextG || (g == nextG && cand.MapIndex < next.MapIndex))
                    {
                        next = cand;
                        nextG = g;
                    }
                }
            }
            if (next is null) break;
            if (people + next.Population > ceiling) { passed.Add(next.MapIndex); continue; }
            taken.Add(next); inside.Add(next.MapIndex); people += next.Population;
        }
        if (people > ceiling) return new();
        return people >= nation.Population * 0.06f ? taken : new();
    }

    /// <summary>The other camp: who takes up arms against a government of this colour.</summary>
    public static string OppositionTo(string ideology, int salt)
    {
        var pick = (salt & 1) == 0;
        return ideology switch
        {
            "liberalism" or "social_democracy" or "libertarianism" or "conservatism" => pick ? "nationalism" : "socialism",
            "nationalism" or "fascism" or "traditionalism" or "theocratic_rule" or "technocratic_rule" => pick ? "liberalism" : "social_democracy",
            "socialism" or "communism" => pick ? "nationalism" : "liberalism",
            _ => pick ? "liberalism" : "nationalism"
        };
    }
}

/// <summary>
/// At most one counter per day of the rolling 365-day window. Counts include every
/// successful uprising that emits the existing civil_war fact, not ordinary secessions
/// or the civil conflicts authored in a scenario. No randomness or presentation state.
/// </summary>
public sealed class CivilWarBudgetState
{
    public const int WindowDays = 365;
    public Dictionary<int, int> StartsByDay = new();

    internal static bool InWindow(int startedDay, int today) => startedDay >= 0 &&
        startedDay <= today && today - (long)startedDay < WindowDays;

    public int Count(int today)
    {
        if (StartsByDay is null || StartsByDay.Count == 0) return 0;
        long total = 0;
        foreach (var pair in StartsByDay)
            if (InWindow(pair.Key, today) && pair.Value > 0)
                total = Math.Min(int.MaxValue, total + pair.Value);
        return (int)total;
    }

    [ThreadStatic] private static List<int> _pruneDaysScratch;

    internal void Prune(int today)
    {
        StartsByDay ??= new();
        if (StartsByDay.Count == 0) return;
        List<int> stale = null;
        foreach (var pair in StartsByDay)
        {
            if (InWindow(pair.Key, today) && pair.Value > 0) continue;
            if (stale is null)
            {
                stale = _pruneDaysScratch ??= new List<int>(8);
                stale.Clear();
            }
            stale.Add(pair.Key);
        }
        if (stale is null) return;
        for (var i = 0; i < stale.Count; i++) StartsByDay.Remove(stale[i]);
        stale.Clear();
    }

    internal void Record(int today)
    {
        Prune(today);
        if (today < 0) return;
        var count = StartsByDay.GetValueOrDefault(today);
        StartsByDay[today] = count == int.MaxValue ? count : count + 1;
    }

    internal CivilWarBudgetState Copy(int today) => new()
    {
        StartsByDay = (StartsByDay ?? new()).Where(p => InWindow(p.Key, today) && p.Value > 0)
            .OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value)
    };

    internal static CivilWarBudgetState FromLegacy(Campaign campaign)
    {
        var starts = new HashSet<(int Day, int Born)>();
        var participants = new HashSet<(int Day, int Nation)>();
        void Remember(int day, int born, int parent)
        {
            if (!InWindow(day, campaign.ElapsedDays) || born < 0) return;
            starts.Add((day, born));
            participants.Add((day, born));
            if (parent >= 0) participants.Add((day, parent));
        }
        foreach (var entry in campaign.Chronicle.Entries)
            if (entry.Kind == "civil_war") Remember(entry.Day, entry.ActorId, entry.TargetId);
        foreach (var war in campaign.Wars)
        {
            if (!war.CivilConflict || war.StartedDay <= 0 || !InWindow(war.StartedDay, campaign.ElapsedDays)) continue;
            var parent = campaign.NationById(war.AttackerId);
            var born = campaign.NationById(war.DefenderId);
            // StrategicWar did not persist its justification. Positive matching outbreak
            // dates distinguish these from scenario conflicts and ordinary ethnic secessions.
            if (born?.LastCivilWarDay == war.StartedDay || parent?.LastCivilWarDay == war.StartedDay)
                Remember(war.StartedDay, war.DefenderId, war.AttackerId);
        }
        foreach (var born in campaign.Nations)
        {
            var day = born.LastCivilWarDay;
            if (day <= 0 || !InWindow(day, campaign.ElapsedDays) || born.SecededFromId < 0) continue;
            if (campaign.NationById(born.SecededFromId)?.LastCivilWarDay == day)
                Remember(day, born.Id, born.SecededFromId);
        }
        var counts = starts.GroupBy(s => s.Day).ToDictionary(g => g.Key, g => g.Count());
        foreach (var nation in campaign.Nations)
        {
            var day = nation.LastCivilWarDay;
            if (day <= 0 || !InWindow(day, campaign.ElapsedDays) || participants.Contains((day, nation.Id))) continue;
            // A surviving timestamp proves at least one outbreak that day, but cannot say
            // whether two countries are parent/rebel. Use the evidenced lower bound; never
            // count a parent and its rebels twice or fabricate an event from sentinel zero.
            if (!counts.ContainsKey(day)) counts[day] = 1;
        }
        return new CivilWarBudgetState { StartsByDay = counts };
    }
}

public sealed partial class Campaign
{
    public CivilWarBudgetState CivilWarBudget = new();
}

public sealed partial class Nation
{
    /// <summary>The day this country last split in a civil war (CivilWarOutbreak), or 0.</summary>
    public int LastCivilWarDay;

    /// <summary>Separate from institutional coups; negative means no military coup is known.</summary>
    public int LastMilitaryCoupDay = -99999;
}
