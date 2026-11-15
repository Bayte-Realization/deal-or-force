using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// One border between two countries at war, watched: who stands on it, under what orders, and
/// how much of the far side is empty and still not taken.
///
/// `--border-probe A B [days]`. Written for the owner's report of NATO divisions packed along
/// the Belarusian border for months while the Belarusian provinces behind it stood empty: the
/// numbers a staff decision is made of, per side of one border, every fifteen days, plus the
/// ground A took from B. It reads the world and never writes a save.
/// </summary>
internal static class BorderProbeReport
{
    private static readonly MethodInfo OwnGround = typeof(Warfare).GetMethod("OwnGround",
        BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly MethodInfo GroundToTakeBack = typeof(Warfare).GetMethod("GroundToTakeBack",
        BindingFlags.NonPublic | BindingFlags.Static);

    public static int Run(Campaign c, string tagA, string tagB, int days, string join = null, string warOf = null)
    {
        var a = c.NationByTag(tagA);
        var b = c.NationByTag(tagB);
        if (a is null || b is null || c.ProvinceMap is null) { Console.WriteLine($"BORDER no such pair {tagA}/{tagB}"); return 2; }
        // `--join T1,T2`: those countries answer the call against B on day 0, through the same
        // JoinWar a call to arms uses - the owner's world had NATO in B's war, a seed may not.
        if (!string.IsNullOrEmpty(join))
        {
            var bWar = c.Wars.FirstOrDefault(w => !w.Ended && w.Involves(b.Id));
            // B not at war yet (Belarus on a fresh 2024 bench): B joins the war of `--war-of X`
            // against X, as the owner's world had it, and the others join on X's side.
            if (bWar is null && warOf is not null && c.NationByTag(warOf) is { } x &&
                c.Wars.FirstOrDefault(w => !w.Ended && w.Involves(x.Id)) is { } xWar)
            {
                Console.WriteLine($"BORDER join {b.Tag} -> war {xWar.Id} " +
                    CampaignDiplomacy.JoinWar(c, xWar, b.Id, defending: xWar.IsAttacking(x.Id)));
                bWar = xWar;
            }
            if (bWar is null) { Console.WriteLine($"BORDER {b.Tag} is in no war to join"); return 2; }
            foreach (var tag in join.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (c.NationByTag(tag) is { } joiner)
                    Console.WriteLine($"BORDER join {tag} -> war {bWar.Id} " +
                        CampaignDiplomacy.JoinWar(c, bWar, joiner.Id, defending: bWar.IsAttacking(b.Id)));
        }
        var map = c.ProvinceMap;
        var startOwnedByB = new HashSet<int>();
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (c.ProvinceController[p] == b.MapIndex) startOwnedByB.Add(p);
        Report(c, a, b, startOwnedByB);
        c.IsPaused = false;
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (c.SimulationFault.Length > 0) { Console.WriteLine($"BORDER_FAULT day={c.ElapsedDays} {c.SimulationFault}"); return 1; }
            if (day % 15 == 0 || day == days) Report(c, a, b, startOwnedByB);
        }
        return 0;
    }

    private static void Report(Campaign c, Nation a, Nation b, HashSet<int> startB)
    {
        var map = c.ProvinceMap;
        var control = c.ProvinceController;
        var war = c.Wars.FirstOrDefault(w => !w.Ended && w.Opposes(a.Id, b.Id));
        var takenFromB = startB.Count(p => control[p] != b.MapIndex &&
            (control[p] == a.MapIndex || (war is not null && WarCoalition.SameSide(war, a.Id, NationAt(c, control[p])))));
        var takenByA = startB.Count(p => control[p] == a.MapIndex);
        if (war is null)
        {
            Console.WriteLine($"BORDER day={c.ElapsedDays} {a.Tag}-{b.Tag} no war; tookFromB={takenFromB} byA={takenByA}");
            return;
        }
        var attacking = war.IsAttacking(a.Id);
        var fenced = !attacking && -war.WarScore < c.Defines.AiDefenderInvadesFromScore;
        // The rule as the build being measured reads it: a build that knows lost ground asks it.
        if (fenced && GroundToTakeBack is not null)
        {
            var enemyMaps = new HashSet<ushort>();
            WarCoalition.FillControllers(c, war, !attacking, enemyMaps);
            fenced = (bool)GroundToTakeBack.Invoke(null, new object[] { c, map, war, a, control, enemyMaps });
        }
        // B's ground touching A's: how much of it is empty of B's side.
        var byProvince = new Dictionary<int, List<Army>>();
        foreach (var army in c.Armies)
        {
            if (army.ProvinceId <= 0 || army.EmbarkedFleetId >= 0) continue;
            if (!byProvince.TryGetValue(army.ProvinceId, out var list)) byProvince[army.ProvinceId] = list = new List<Army>();
            list.Add(army);
        }
        bool EnemyIn(int p) => byProvince.TryGetValue(p, out var l) && l.Any(x => war.Opposes(a.Id, x.NationId) && x.Divisions >= .1f);
        var farSide = new HashSet<int>();
        var nearSide = new HashSet<int>();
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (control[p] != a.MapIndex || map.IsWater(p)) continue;
            foreach (var n in map.Neighbors[p])
                if (n > 0 && control[n] == b.MapIndex && !map.IsWater(n)) { farSide.Add(n); nearSide.Add(p); }
        }
        var emptyFar = farSide.Count(p => !EnemyIn(p));
        var own = a.Id;
        var ownFar = farSide.Count(p => (bool)OwnGround.Invoke(null, new object[] { c, map, a, p }));
        var mine = c.Armies.Where(x => x.NationId == own && x.TemplateId != MilitiaCore.TemplateId).ToList();
        var onBorder = mine.Where(x => nearSide.Contains(x.ProvinceId)).ToList();
        string Orders(IEnumerable<Army> armies) => string.Join(",", armies.GroupBy(x => x.OrderKind).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:{g.Count()}/{g.Count(x => x.PlanActive)}act"));
        var besideEmpty = onBorder.Count(x => map.Neighbors[x.ProvinceId].Any(n => farSide.Contains(n) && !EnemyIn(n)));
        var piles = onBorder.GroupBy(x => x.ProvinceId).Select(g => g.Count()).DefaultIfEmpty(0).Max();
        Console.WriteLine($"BORDER day={c.ElapsedDays} {a.Tag}-{b.Tag} war={war.Id} {(attacking ? "attacker" : "defender")} " +
            $"score={war.WarScore:0.0} fenced={(fenced ? 1 : 0)} farSide={farSide.Count} emptyFar={emptyFar} ownGroundFar={ownFar} " +
            $"tookFromB={takenFromB} byA={takenByA} armies={mine.Count} [{Orders(mine)}] onBorder={onBorder.Count} " +
            $"[{Orders(onBorder)}] besideEmpty={besideEmpty} pile={piles} support={a.WarSupport:0.00} " +
            $"advShare={FrontPlanner.AdvanceShare(c, war, a, AiStrategyCatalog.Resolve(c, war, a), attacking, out var pressing):0.00} pressing={(pressing ? 1 : 0)}");
    }

    private static int NationAt(Campaign c, ushort mapIndex)
    {
        foreach (var n in c.Nations) if (n.MapIndex == mapIndex) return n.Id;
        return -1;
    }
}
