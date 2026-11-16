using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--pockets`: what becomes of formations that are cut off, and of the forces of risings.
///
/// The pulse says how many formations "starved in pockets" and stops there. It cannot say
/// whether anybody was attacking them when they died, how long the pocket had lasted, or
/// whether the formation was a rising's own militia standing among its own people - and
/// those are the three questions the pocket and partisan rules are judged by. A player
/// reported that partisans "die right where they rose"; this is the count of it.
///
/// It follows every formation by the object, not by its id (an id is handed to the next
/// formation raised on the day the last one is buried), listens to Attrition.Destroy for the
/// reason each one left the world, and prints: deaths by cause for regular armies, risings
/// and the factions of 2024; every pocket from the day it closed to the day it ended and how
/// (relieved, starved untouched, starved under attack, broken in battle, gave up); the first
/// sixty days of every formation that appeared under a rebel flag; and each faction's
/// strength at fixed days. It reads the campaign and writes nothing into it.
/// </summary>
internal static class PocketReport
{
    private sealed class Life
    {
        public int NationId;
        public string Tag = "";
        public string Kind = "";      // regular, rising, faction
        public string Origin = "";    // opening, raised, militia, defected
        public int BornDay;
        public int BornProvince;
        public int BornRegion;
        public int LastProvince;
        public int LastRegion;
        public bool CutAtFirstTrace;
        public int FirstCutDay = -1;
        public bool FirstCutOnHeldGround;
        public int GoneDay = -1;
        public string Cause = "";
        public bool GoneCutOff;
        public float LastStrength, LastOrganisation;
        public Episode Open;
    }

    private sealed class Episode
    {
        public string Kind = "";
        public int Start;
        public int End = -1;
        public string Ending = "";
        public float Stores = -1f;
        public int BattleDays;
        public int HungryDays;
        public bool HeldGround;
    }

    private sealed class Side
    {
        public Nation Nation;
        public string Kind = "";
        public int BornDay;
        public int FallenDay = -1;
        public int NoArmyDay = -1;
        public int NoGroundDay = -1;
        public bool BornFighting;
        // What it still had the day before it fell: a side erased with an army in the field
        // and ground under it did not lose a war, it lost a signature.
        public int LastFormations, LastHeld;
        public readonly Dictionary<int, (int Formations, int Held)> At = new();
    }

    private static readonly int[] Marks = { 1, 30, 60, 180, 365, 730 };

    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        var map = c.ProvinceMap;
        var lives = new Dictionary<Army, Life>(ReferenceEqualityComparer.Instance);
        var all = new List<Life>();
        var episodes = new List<Episode>();
        var sides = new Dictionary<int, Side>();
        var said = new Dictionary<Army, (string Reason, bool Lost)>(ReferenceEqualityComparer.Instance);
        var held = new Dictionary<int, int>();
        var formations = new Dictionary<int, int>();
        long cutOffDays = 0, cutOffBattleDays = 0, cutOffHungryDays = 0, armyDays = 0;

        string KindOf(Nation nation) =>
            nation is null ? "regular" :
            !string.IsNullOrEmpty(nation.FactionId) ? "faction" :
            WarAims.IsRebel(nation) ? "rising" : "regular";

        Life Open(Army army, string origin)
        {
            var nation = c.NationById(army.NationId);
            var life = new Life
            {
                NationId = army.NationId, Tag = nation?.Tag ?? "?", Kind = KindOf(nation), Origin = origin,
                BornDay = c.ElapsedDays, BornProvince = army.ProvinceId, BornRegion = army.StateMapIndex,
                LastProvince = army.ProvinceId, LastRegion = army.StateMapIndex,
                LastStrength = army.Strength, LastOrganisation = army.Organisation
            };
            lives[army] = life;
            all.Add(life);
            return life;
        }

        void CloseEpisode(Life life, string ending)
        {
            if (life.Open is null) return;
            life.Open.End = c.ElapsedDays;
            life.Open.Ending = ending;
            life.Open = null;
        }

        void Close(Life life, string cause, bool cutOff)
        {
            life.GoneDay = c.ElapsedDays;
            life.Cause = cause;
            life.GoneCutOff = cutOff;
            CloseEpisode(life, cause);
        }

        foreach (var army in c.Armies) Open(army, "opening");
        Attrition.Witness = (_, army, reason, lost) => said[army] = (reason, lost);
        try
        {
            for (var day = 1; day <= days; day++)
            {
                said.Clear();
                c.TickDay();

                var controllers = c.ProvinceController;
                var seen = new HashSet<Army>(ReferenceEqualityComparer.Instance);
                formations.Clear();
                foreach (var army in c.Armies)
                {
                    seen.Add(army);
                    formations[army.NationId] = formations.GetValueOrDefault(army.NationId) + 1;
                    if (!lives.TryGetValue(army, out var life))
                        life = Open(army, army.TemplateId == MilitiaCore.TemplateId ? "militia" : "raised");
                    else if (life.NationId != army.NationId)
                    {
                        // The same men under another flag: one life ends, another begins.
                        Close(life, "changed flag", army.CutOff);
                        all[all.IndexOf(life)] = life;
                        life = Open(army, "defected");
                    }
                    life.LastProvince = army.ProvinceId;
                    life.LastRegion = army.StateMapIndex;
                    life.LastStrength = army.Strength;
                    life.LastOrganisation = army.Organisation;
                    armyDays++;

                    var nation = c.NationById(army.NationId);
                    var onHeld = nation is not null && controllers is not null && army.ProvinceId > 0 &&
                                 army.ProvinceId < controllers.Length && controllers[army.ProvinceId] == nation.MapIndex;
                    if (army.CutOff && army.EmbarkedFleetId < 0)
                    {
                        cutOffDays++;
                        if (army.InBattle) cutOffBattleDays++;
                        if (army.PocketStores == 0f) cutOffHungryDays++;
                        if (life.FirstCutDay < 0)
                        {
                            life.FirstCutDay = c.ElapsedDays;
                            life.FirstCutOnHeldGround = onHeld;
                            // The roads are traced every third day: three days is the first trace.
                            if (c.ElapsedDays - life.BornDay <= 3) life.CutAtFirstTrace = true;
                        }
                        if (life.Open is null)
                        {
                            life.Open = new Episode { Kind = life.Kind, Start = c.ElapsedDays, HeldGround = onHeld };
                            episodes.Add(life.Open);
                        }
                        if (life.Open.Stores < 0f && army.PocketStores >= 0f) life.Open.Stores = army.PocketStores;
                        if (army.InBattle) life.Open.BattleDays++;
                        if (army.PocketStores == 0f) life.Open.HungryDays++;
                    }
                    else CloseEpisode(life, "road open again");
                }

                foreach (var (army, life) in lives)
                {
                    if (life.GoneDay >= 0 || seen.Contains(army)) continue;
                    var open = life.Open;
                    var cutOff = open is not null;
                    string cause;
                    if (said.TryGetValue(army, out var word))
                    {
                        cause = word.Reason;
                        // One reason covers two deaths: broken by hunger, and broken by fire.
                        if (cause == "destroyed: broken in the field" && cutOff)
                            cause = open.BattleDays == 0 ? "starved in a pocket nobody attacked" : "starved in a pocket under attack";
                    }
                    else cause = army.TemplateId == MilitiaCore.TemplateId
                        ? "militia gone home (stood down in the quiet, or merged)"
                        : "left the list without a burial (merged into another formation)";
                    Close(life, cause, cutOff);
                }

                // Who holds how much, for the sides this report follows.
                held.Clear();
                if (controllers is not null)
                    for (var province = 1; province <= map.ProvinceCount && province < controllers.Length; province++)
                        if (controllers[province] != 0 && !map.IsWater(province))
                            held[controllers[province]] = held.GetValueOrDefault(controllers[province]) + 1;
                foreach (var nation in c.Nations)
                {
                    if (nation is null) continue;
                    var kind = KindOf(nation);
                    if (kind == "regular") continue;
                    if (!sides.TryGetValue(nation.Id, out var side))
                    {
                        // A rising that never held ground or raised a formation is a name in a list.
                        if (formations.GetValueOrDefault(nation.Id) == 0 && held.GetValueOrDefault(nation.MapIndex) == 0) continue;
                        sides[nation.Id] = side = new Side { Nation = nation, Kind = kind, BornDay = kind == "faction" && day == 1 ? 0 : c.ElapsedDays };
                        // A breakaway let go in peace is a new country; one born into a war is a rising.
                        side.BornFighting = Warfare.LiveWars(c).Any(war => !war.Ended && war.Involves(nation.Id));
                    }
                    var age = c.ElapsedDays - side.BornDay;
                    var count = formations.GetValueOrDefault(nation.Id);
                    var ground = held.GetValueOrDefault(nation.MapIndex);
                    if (Array.IndexOf(Marks, age) >= 0) side.At[age] = (count, ground);
                    if (side.FallenDay < 0 && c.Fallen.Contains(nation.Id)) side.FallenDay = age;
                    if (side.FallenDay < 0) { side.LastFormations = count; side.LastHeld = ground; }
                    if (side.NoArmyDay < 0 && count == 0) side.NoArmyDay = age;
                    if (side.NoGroundDay < 0 && ground == 0) side.NoGroundDay = age;
                }
            }
        }
        finally { Attrition.Witness = null; }

        var end = c.ElapsedDays;
        Console.WriteLine($"POCKETS run of {days} days, seed {c.Seed}: {all.Count} formation lives, {armyDays:N0} formation-days, " +
                          $"{cutOffDays:N0} of them cut off ({Share(cutOffDays, armyDays)}); of the cut-off days {Share(cutOffBattleDays, cutOffDays)} " +
                          $"were spent in a battle and {Share(cutOffHungryDays, cutOffDays)} with the stores gone");
        Console.WriteLine($"POCKETS counters: starved in pockets {Attrition.StarvedInPockets}, surrendered formations {WarLosses.SurrenderedFormations}; " +
                          string.Join(", ", StrandedGarrisons.Tally.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value}")));

        Console.WriteLine("POCKETS deaths by cause (regular / rising / faction):");
        foreach (var group in all.Where(l => l.GoneDay >= 0).GroupBy(l => l.Cause).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"   {group.Count(),5}  {group.Count(l => l.Kind == "regular"),5} / {group.Count(l => l.Kind == "rising"),4} / {group.Count(l => l.Kind == "faction"),4}  {group.Key}");

        Console.WriteLine($"POCKETS episodes: {episodes.Count} closed rings around a formation");
        foreach (var kind in new[] { "regular", "rising", "faction" })
        {
            var mine = episodes.Where(e => e.Kind == kind).ToList();
            if (mine.Count == 0) continue;
            Console.WriteLine($"  {kind}: {mine.Count}, on ground the formation's country held {Share(mine.Count(e => e.HeldGround), mine.Count)}, " +
                              $"stores counted at the start {Spread(mine.Where(e => e.Stores >= 0f).Select(e => (double)e.Stores))} days");
            foreach (var group in mine.GroupBy(e => e.End < 0 ? "still closed at the end" : e.Ending).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
                Console.WriteLine($"   {group.Count(),5}  lasted {Spread(group.Select(e => (double)((e.End < 0 ? end : e.End) - e.Start)))} days, " +
                                  $"hungry {Spread(group.Select(e => (double)e.HungryDays))}, in battle {Spread(group.Select(e => (double)e.BattleDays))}  {group.Key}");
        }

        foreach (var kind in new[] { "rising", "faction" })
        {
            var cohort = all.Where(l => l.Kind == kind && (kind == "faction" || l.BornDay > 0)).ToList();
            if (cohort.Count == 0) continue;
            Console.WriteLine($"{kind.ToUpperInvariant()} formations: {cohort.Count} " +
                              $"({string.Join(", ", cohort.GroupBy(l => l.Origin).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))})");
            var old60 = cohort.Where(l => end - l.BornDay >= 60).ToList();
            var cut = cohort.Where(l => l.FirstCutDay >= 0).ToList();
            Console.WriteLine($"   cut off at the first trace (three days) {Share(cohort.Count(l => l.CutAtFirstTrace), cohort.Count)}; " +
                              $"cut off inside 30 days {Share(cohort.Count(l => l.FirstCutDay >= 0 && l.FirstCutDay - l.BornDay <= 30), cohort.Count)}; " +
                              $"ever {Share(cut.Count, cohort.Count)}; first cut off on ground their side held {Share(cut.Count(l => l.FirstCutOnHeldGround), cut.Count)}");
            Console.WriteLine($"   of the {old60.Count} that had sixty days to live: gone by day 30 {Share(old60.Count(l => Gone(l, 30)), old60.Count)}, " +
                              $"by day 60 {Share(old60.Count(l => Gone(l, 60)), old60.Count)}");
            var early = cohort.Where(l => l.CutAtFirstTrace).GroupBy(l => l.Tag).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            if (early.Count > 0)
                Console.WriteLine("   cut off at the first trace, by side: " + string.Join(", ", early.Select(g => $"{g.Key} {g.Count()} ({g.Count(l => l.FirstCutOnHeldGround)} on its own ground)")));
            var dead = cohort.Where(l => l.GoneDay >= 0 && l.Cause != "changed flag").ToList();
            Console.WriteLine($"   {dead.Count} gone in the run, after {Spread(dead.Select(l => (double)(l.GoneDay - l.BornDay)))} days; " +
                              $"in the province they appeared in {Share(dead.Count(l => l.LastProvince == l.BornProvince), dead.Count)}, " +
                              $"in the same region {Share(dead.Count(l => l.LastRegion == l.BornRegion), dead.Count)}, cut off when they went {Share(dead.Count(l => l.GoneCutOff), dead.Count)}");
            foreach (var group in dead.GroupBy(l => l.Cause).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
                Console.WriteLine($"   {group.Count(),5}  after {Spread(group.Select(l => (double)(l.GoneDay - l.BornDay)))} days  {group.Key}");
        }

        var risings = sides.Values.Where(s => s.Kind == "rising").OrderBy(s => s.BornDay).ThenBy(s => s.Nation.Id).ToList();
        if (risings.Count > 0)
        {
            int Old(int age) => risings.Count(s => end - s.BornDay >= age);
            int Unarmed(int age) => risings.Count(s => end - s.BornDay >= age && s.NoArmyDay >= 0 && s.NoArmyDay <= age);
            int Landless(int age) => risings.Count(s => end - s.BornDay >= age && s.NoGroundDay >= 0 && s.NoGroundDay <= age);
            Console.WriteLine($"RISING sides: {risings.Count} took ground or arms in the run, {risings.Count(s => s.BornFighting)} of them born into a war");
            foreach (var age in new[] { 30, 60, 180, 365 })
                Console.WriteLine($"   by day {age,3} of its life: without a formation {Share(Unarmed(age), Old(age))}, without ground {Share(Landless(age), Old(age))} (of {Old(age)} old enough)");
            foreach (var side in risings.Take(60)) Console.WriteLine("   " + Row(c, side, end, all));
        }
        var factions = sides.Values.Where(s => s.Kind == "faction").OrderBy(s => s.Nation.Tag, StringComparer.Ordinal).ToList();
        if (factions.Count > 0)
        {
            Console.WriteLine($"FACTION sides: {factions.Count}");
            foreach (var side in factions) Console.WriteLine("   " + Row(c, side, end, all));
        }
        return 0;
    }

    private static bool Gone(Life life, int age) =>
        life.GoneDay >= 0 && life.Cause != "changed flag" && life.GoneDay - life.BornDay <= age;

    private static string Row(Campaign c, Side side, int end, List<Life> all)
    {
        var nation = side.Nation;
        // What its formations died of, the commonest first: the side's story in one line.
        var causes = string.Join("; ", all.Where(l => l.NationId == nation.Id && l.GoneDay >= 0 && l.Cause != "changed flag")
            .GroupBy(l => l.Cause).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {Short(g.Key)}"));
        var parent = c.NationById(nation.SecededFromId);
        var marks = string.Join(" ", Marks.Where(side.At.ContainsKey).Select(age => $"d{age}:{side.At[age].Formations}f/{side.At[age].Held}p"));
        var now = $"end:{c.Armies.Count(a => a.NationId == nation.Id)}f";
        var fate = side.FallenDay >= 0 ? $"FELL day {side.FallenDay} (the day before: {side.LastFormations}f/{side.LastHeld}p)" :
                   side.NoGroundDay >= 0 ? $"no ground from day {side.NoGroundDay}" :
                   side.NoArmyDay >= 0 ? $"no formation from day {side.NoArmyDay}" : "standing";
        return $"{nation.Tag,-5} born {side.BornDay,4}{(side.BornFighting ? " at war" : "")} parent={parent?.Tag ?? "-",-4} {marks} {now} {fate} (age {end - side.BornDay}) {nation.Name}" +
               (causes.Length > 0 ? $" [{causes}]" : "");
    }

    private static string Short(string cause) => cause switch
    {
        "destroyed: zero morale in pocket" => "broken with no retreat",
        "destroyed: cut off with no line of retreat" => "beaten with no retreat",
        "destroyed: its country ceased to exist" => "fell with its country",
        "starved in a pocket nobody attacked" => "starved untouched",
        "starved in a pocket under attack" => "starved under attack",
        "surrendered: a pocket out of stores" => "gave up hungry",
        "destroyed: broken in the field" => "broken in the field",
        _ => cause.StartsWith("militia gone home", StringComparison.Ordinal) ? "militia home"
            : cause.StartsWith("left the list", StringComparison.Ordinal) ? "merged" : cause
    };

    private static string Share(long part, long whole) =>
        whole <= 0 ? "-" : $"{part} of {whole} ({100.0 * part / whole:0}%)";

    /// <summary>The quarter, the middle and the three-quarter mark, and the largest.</summary>
    private static string Spread(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return "-";
        double At(double q) => sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(q * sorted.Length))];
        return $"{At(0.25):0}/{At(0.5):0}/{At(0.75):0} (max {sorted[^1]:0})";
    }
}
