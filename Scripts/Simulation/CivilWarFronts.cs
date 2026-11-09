using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// The chance a week that two sides risen against the same government, whose fronts touch and
    /// who want different things, turn on each other (CivilWarFronts). 0 keeps every civil war
    /// two-sided.
    /// </summary>
    public float CivilWarThirdFrontChance { get; set; } = 0.04f;
    /// <summary>Above this opinion of each other two rebel sides make common cause instead.</summary>
    public float CivilWarThirdFrontOpinion { get; set; } = 15f;
}

/// <summary>
/// Civil wars with more than two sides. The Russian civil war was Reds, Whites, Greens, Ukrainian
/// and Polish armies at once; Syria's was the government, the Islamists, the Kurds and the Free
/// Army, each fighting most of the others. Here: every side that has risen against the same
/// government and holds ground touching another such side's ground may turn on it, when they want
/// different things - another creed, other peoples - and do not like each other. The same rule
/// for every country and every rising; nobody is named.
/// </summary>
public static class CivilWarFronts
{
    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same weekly third-front pass, yielded between its phases.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (!c.StepOpensNewDay || c.ElapsedDays % 7 != 5 || c.Defines.CivilWarThirdFrontChance <= 0f) yield break;
        var map = c.ProvinceMap;
        var controls = c.ProvinceController;
        if (map is null || controls is null) yield break;

        // The governments with more than one rising against them at once.
        var risings = new Dictionary<int, List<Nation>>();
        var warsScanned = 0;
        foreach (var war in c.Wars)
        {
            if (++warsScanned % 16 == 0) yield return 0;
            if (war.Ended || !war.CivilConflict) continue;
            foreach (var (government, rebel) in new[] { (war.AttackerId, war.DefenderId), (war.DefenderId, war.AttackerId) })
            {
                var side = c.NationById(rebel);
                if (side is null || !RoseAgainst(side, government)) continue;
                if (!risings.TryGetValue(government, out var list)) risings[government] = list = new List<Nation>();
                if (!list.Contains(side)) list.Add(side);
            }
        }
        if (!risings.Values.Any(list => list.Count >= 2)) yield break;
        yield return 0;

        var byIndex = c.NationsByMapIndex();
        var governments = 0;
        foreach (var (government, sides) in risings.OrderBy(pair => pair.Key))
        {
            if (++governments % 2 == 0) yield return 0;
            if (sides.Count < 2) continue;
            var indices = sides.Select(s => (ushort)s.MapIndex).ToHashSet();
            var touching = new HashSet<(int, int)>();
            for (var p = 1; p <= map.ProvinceCount && p < controls.Length; p++)
            {
                var here = controls[p];
                if (!indices.Contains(here)) continue;
                foreach (var q in map.Neighbors[p])
                {
                    if (q >= controls.Length) continue;
                    var there = controls[q];
                    if (there == here || !indices.Contains(there)) continue;
                    touching.Add(here < there ? (here, there) : (there, here));
                }
            }
            foreach (var (a, b) in touching.OrderBy(t => t.Item1).ThenBy(t => t.Item2))
            {
                var first = byIndex[(ushort)a];
                var second = byIndex[(ushort)b];
                if (MilitaryAccess.Hostile(c, first.Id, second.Id)) continue;
                var relation = CampaignDiplomacy.Get(c, first.Id, second.Id);
                if (relation is null || relation.Stance == DiplomaticStance.Allied || relation.Opinion >= c.Defines.CivilWarThirdFrontOpinion) continue;
                if (!WantDifferentThings(first, second)) continue;
                if (MovementRules.Roll(c.Seed, first.Id * 977 + second.Id, c.ElapsedDays, 0x3F27) >= c.Defines.CivilWarThirdFrontChance * AiTemper.Aggression(c)) continue;
                // The stronger side moves first: it is the one with something to gain.
                var power = (Func<Nation, float>)(n => c.Armies.Where(x => x.NationId == n.Id).Sum(x => x.Divisions));
                var (attacker, defender) = power(first) >= power(second) ? (first, second) : (second, first);
                if (attacker.IsHuman) continue;   // a player chooses its own wars
                var war = CampaignDiplomacy.DeclareWar(c, attacker.Id, defender.Id, justification: "civil_war", onOwnInitiative: false);
                if (war is null) continue;
                war.CivilConflict = true;
                c.Chronicle.Record(c, "civil_war_third_front", attacker.Id, defender.Id, c.NationById(government)?.DisplayName ?? "");
                DiagnosticLogs.Write("simulation", $"THIRD_FRONT {attacker.Tag} vs {defender.Tag} inside {c.NationById(government)?.Tag}");
            }
        }
    }

    private static bool RoseAgainst(Nation side, int government) =>
        side.RevoltParentId == government || side.SecededFromId == government ||
        side.Movement is not null && !side.Movement.Exile;

    /// <summary>Another creed or other peoples: sides that would not share the country they are fighting for.</summary>
    private static bool WantDifferentThings(Nation first, Nation second)
    {
        if (!string.Equals(first.Ideology, second.Ideology, StringComparison.OrdinalIgnoreCase)) return true;
        var a = first.FoundedCultures.Count > 0 ? first.FoundedCultures[0] : DemographyCatalog.PrimaryCulture(first.Tag);
        var b = second.FoundedCultures.Count > 0 ? second.FoundedCultures[0] : DemographyCatalog.PrimaryCulture(second.Tag);
        return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
