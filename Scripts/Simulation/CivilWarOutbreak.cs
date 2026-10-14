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
    public float CivilWarStabilityThreshold { get; set; } = 0.30f;
    public float CivilWarWeeklyChance { get; set; } = 0.010f;
    /// <summary>The most civil wars the whole world begins in a year. A century-old campaign
    /// whose stability has drifted down everywhere began thirteen in one year - Japan and
    /// Spain among them - without it.</summary>
    public int CivilWarWorldPerYear { get; set; } = 3;
    public int CivilWarCoupMemoryDays { get; set; } = 365;
    public int CivilWarEarliestDay { get; set; } = 120;
    public int CivilWarCooldownDays { get; set; } = 1825;
    public long CivilWarMinimumPopulation { get; set; } = 2_000_000;
    public int CivilWarMinimumRegions { get; set; } = 4;
    /// <summary>How much of the country the rising takes, as a share of its people.</summary>
    public float CivilWarRebelShareMin { get; set; } = 0.15f;
    public float CivilWarRebelShareMax { get; set; } = 0.40f;
    /// <summary>Whether a country somebody plays can have one. Off: a player's state falls
    /// apart by its own choices, not by a roll it was never shown.</summary>
    public bool CivilWarStrikesPlayers { get; set; } = false;
    /// <summary>How much faster a state's minorities leave while it fights a civil war.</summary>
    public float SecessionCivilWarMultiplier { get; set; } = 3f;
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
        var d = campaign?.Defines;
        if (d is null) return;
        // Expired budget entries are administrative cleanup, including while outbreaks
        // are disabled. Turning the mechanic back on cannot resurrect an old year.
        campaign.CivilWarBudget ??= CivilWarBudgetState.FromLegacy(campaign);
        campaign.CivilWarBudget.Prune(campaign.ElapsedDays);
        if (!d.CivilWarOutbreaks || campaign.ProvinceMap is null) return;
        if (!MechanicFeatures.SystemEnabled(campaign, "SecessionEvents")) return;
        if (campaign.ElapsedDays < d.CivilWarEarliestDay || campaign.ElapsedDays % 7 != 3) return;
        var thisYear = StartsInLastYear(campaign);
        foreach (var nation in campaign.Nations.ToArray())
        {
            if (thisYear >= d.CivilWarWorldPerYear) return;
            if (!Eligible(campaign, nation)) continue;
            var chance = Chance(campaign, nation);
            if (!float.IsFinite(chance) || chance <= 0f) continue;
            if (Escalation.Roll(campaign.Seed, nation.Id, 0xC1C, campaign.ElapsedDays, 0x3) >= chance) continue;
            if (Break(campaign, nation) is not null) thisYear++;
        }
    }

    private static bool Eligible(Campaign c, Nation nation) => EligibilityReason(c, nation).Length == 0;

    /// <summary>The same gates for the simulation and its explanation; no hidden UI approximation.</summary>
    public static string EligibilityReason(Campaign c, Nation nation)
    {
        if (c is null || nation is null) return "No governed country selected.";
        var d = c.Defines;
        if (!d.CivilWarOutbreaks || !MechanicFeatures.SystemEnabled(c, "SecessionEvents")) return "Civil-war outbreaks are disabled.";
        if (!float.IsFinite(nation.Stability) || !float.IsFinite(d.CivilWarStabilityThreshold)) return "Invalid simulation values prevent this action. Pause and inspect the campaign.";
        if (nation.Movement is not null || nation.FactionId.Length > 0 || nation.IsSubject || c.Fallen.Contains(nation.Id))
            return "This actor is not an independent established government.";
        if (nation.IsHuman && !d.CivilWarStrikesPlayers) return "Random outbreaks in player countries are disabled in campaign settings.";
        if (c.ElapsedDays < d.CivilWarEarliestDay) return "The campaign is still in its initial protection period.";
        if (nation.LastCivilWarDay > 0 && c.ElapsedDays - nation.LastCivilWarDay < d.CivilWarCooldownDays)
            return "The country is protected from another outbreak during recovery.";
        // One civil war at a time: a state already fighting its own rebels is not split twice.
        if (c.Wars.Any(w => !w.Ended && w.CivilConflict && w.Involves(nation.Id))) return "An existing civil war must end before another outbreak.";
        if (nation.Population < d.CivilWarMinimumPopulation || c.StatesOf(nation).Count(s => s.Population > 0) < d.CivilWarMinimumRegions)
            return "Too few residents or governed regions for a territorial civil war.";
        if (nation.Stability >= d.CivilWarStabilityThreshold) return "Institutions remain above the civil-war instability threshold.";
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
        // Both coup routes set LastCoupDay. The independent military timestamp preserves
        // an earlier coup's existing multiplier even after a later institutional transition.
        var coupDay = nation.LastMilitaryCoupDay;
        var coup = coupDay >= 0 && coupDay <= c.ElapsedDays &&
            c.ElapsedDays - (long)coupDay <= d.CivilWarCoupMemoryDays;
        if (coup) chance *= 2f;
        // A country at war abroad and losing has its army away from home.
        if (c.Wars.Any(w => !w.Ended && !w.CivilConflict && w.Involves(nation.Id) &&
                            (w.IsAttacking(nation.Id) ? w.WarScore : -w.WarScore) < -10f)) chance *= 1.5f;
        return float.IsFinite(chance) ? Math.Clamp(chance, 0f, 1f) : 0f;
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
    internal static SecessionReport Break(Campaign c, Nation nation)
    {
        if (!Eligible(c, nation)) return null;
        var regions = RebelGround(c, nation);
        if (regions.Count == 0) return null;
        var ideology = OppositionTo(nation.Ideology, c.Seed ^ nation.Id);
        var template = ideology switch
        {
            "liberalism" or "social_democracy" => "transitional_council",
            "socialism" or "communism" => "revolutionary_front",
            _ => "salvation_front"
        };
        var parentTag = nation.Tag;
        var parentName = nation.Name;
        var report = SecessionEvents.Uprising(c, nation, regions, born =>
        {
            born.Name = $"rising:{template}:{parentTag}:{parentName}";
            born.Ideology = ideology;
            born.Government = "provisional_government";
            born.LastCivilWarDay = c.ElapsedDays;
        });
        if (report?.Seceded is null) return null;
        nation.LastCivilWarDay = c.ElapsedDays;
        Recent.Add($"{nation.Tag}->{report.Seceded.Tag} day {c.ElapsedDays} regions {regions.Count} people {report.Population}");
        DiagnosticLogs.Write("simulation", $"CIVIL_WAR state={nation.Tag} rebels={report.Seceded.Tag} " +
            $"regions={regions.Count} people={report.Population} stability={nation.Stability:0.00} ideology={ideology}");
        return report;
    }

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
        var owned = c.StatesOf(nation).Where(s => s.Population > 0 && s.MapIndex != nation.CapitalStateMapIndex &&
            float.IsFinite(s.Stability) && float.IsFinite(s.Radicalism)).ToList();
        if (owned.Count < 2) return new();
        float Grievance(ScenarioState s) => s.Stability - 0.6f * s.Radicalism;
        var byIndex = owned.ToDictionary(s => s.MapIndex);
        var adjacent = byIndex.Keys.ToDictionary(k => k, _ => new HashSet<int>());
        var map = c.ProvinceMap;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            var here = map.ProvinceToState[p];
            if (!adjacent.TryGetValue(here, out var set)) continue;
            foreach (var next in map.Neighbors[p])
            {
                var there = map.ProvinceToState[next];
                if (there != here && byIndex.ContainsKey(there)) set.Add(there);
            }
        }
        var share = d.CivilWarRebelShareMin + (d.CivilWarRebelShareMax - d.CivilWarRebelShareMin) *
            Escalation.Roll(c.Seed, nation.Id, 0x5A1, c.ElapsedDays, 0x9);
        var target = (long)(nation.Population * share);
        var seed = owned.OrderBy(Grievance).ThenBy(s => s.MapIndex).First();
        var taken = new List<ScenarioState> { seed };
        var inside = new HashSet<int> { seed.MapIndex };
        long people = seed.Population;
        // Never the whole country less the capital: the government keeps a real state.
        var limit = Math.Max(1, (int)(owned.Count * 0.6f));
        // Whole regions, never past the most a rising may take: a region that would carry it
        // over the line is passed over for a smaller neighbour (Yemen lost 57 % in one rising).
        var ceiling = (long)(nation.Population * d.CivilWarRebelShareMax);
        var passed = new HashSet<int>();
        while (people < target && taken.Count < limit)
        {
            var next = taken.SelectMany(s => adjacent[s.MapIndex]).Where(i => !inside.Contains(i) && !passed.Contains(i))
                .Select(i => byIndex[i]).OrderBy(Grievance).ThenBy(s => s.MapIndex).FirstOrDefault();
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

    internal void Prune(int today)
    {
        StartsByDay ??= new();
        if (StartsByDay.Count == 0) return;
        foreach (var pair in StartsByDay.Where(p => !InWindow(p.Key, today) || p.Value <= 0).ToArray())
            StartsByDay.Remove(pair.Key);
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
