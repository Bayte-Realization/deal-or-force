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
        var openWars = Warfare.OpenWars(c);
        for (var wi = 0; wi < openWars.Count; wi++)
        {
            if (++warsScanned % 16 == 0 || Campaign.StageOverdue()) yield return 0;
            var war = openWars[wi];
            if (war.Ended || !war.CivilConflict) continue;
            for (var role = 0; role < 2; role++)
            {
                var government = role == 0 ? war.AttackerId : war.DefenderId;
                var rebel = role == 0 ? war.DefenderId : war.AttackerId;
                var side = c.NationById(rebel);
                if (side is null || !RoseAgainst(side, government)) continue;
                if (!risings.TryGetValue(government, out var list)) risings[government] = list = new List<Nation>();
                if (!list.Contains(side)) list.Add(side);
            }
        }
        var govKeys = new List<int>(risings.Count);
        foreach (var kv in risings)
            if (kv.Value.Count >= 2) govKeys.Add(kv.Key);
        if (govKeys.Count == 0) yield break;
        govKeys.Sort();
        yield return 0;

        var byIndex = c.NationsByMapIndex();
        var governments = 0;
        var indices = new HashSet<ushort>();
        var touching = new List<(int A, int B)>();
        var touchingSeen = new HashSet<long>();
        for (var gi = 0; gi < govKeys.Count; gi++)
        {
            if (++governments % 2 == 0 || Campaign.StageOverdue()) yield return 0;
            var government = govKeys[gi];
            var sides = risings[government];
            if (sides.Count < 2) continue;
            indices.Clear();
            for (var si = 0; si < sides.Count; si++) indices.Add((ushort)sides[si].MapIndex);
            touching.Clear();
            touchingSeen.Clear();
            var limit = Math.Min(map.ProvinceCount, controls.Length - 1);
            for (var p = 1; p <= limit; p++)
            {
                var here = controls[p];
                if (!indices.Contains(here)) continue;
                var neighbors = map.Neighbors[p];
                for (var ni = 0; ni < neighbors.Length; ni++)
                {
                    var q = neighbors[ni];
                    if (q >= controls.Length) continue;
                    var there = controls[q];
                    if (there == here || !indices.Contains(there)) continue;
                    int lo = here < there ? here : there;
                    int hi = here < there ? there : here;
                    var pairKey = ((long)lo << 16) | (uint)hi;
                    if (touchingSeen.Add(pairKey)) touching.Add((lo, hi));
                }
            }
            touching.Sort(static (l, r) =>
            {
                var c1 = l.A.CompareTo(r.A);
                return c1 != 0 ? c1 : l.B.CompareTo(r.B);
            });
            for (var ti = 0; ti < touching.Count; ti++)
            {
                var (a, b) = touching[ti];
                var first = byIndex[(ushort)a];
                var second = byIndex[(ushort)b];
                if (MilitaryAccess.Hostile(c, first.Id, second.Id)) continue;
                var relation = CampaignDiplomacy.Get(c, first.Id, second.Id);
                if (relation is null || relation.Stance == DiplomaticStance.Allied || relation.Opinion >= c.Defines.CivilWarThirdFrontOpinion) continue;
                if (!WantDifferentThings(first, second)) continue;
                if (MovementRules.Roll(c.Seed, first.Id * 977 + second.Id, c.ElapsedDays, 0x3F27) >= c.Defines.CivilWarThirdFrontChance * AiTemper.Aggression(c)) continue;
                // The stronger side moves first: it is the one with something to gain.
                float pFirst = 0f, pSecond = 0f;
                var armies = c.Armies;
                for (var ai = 0; ai < armies.Count; ai++)
                {
                    var army = armies[ai];
                    if (army.NationId == first.Id) pFirst += army.Divisions;
                    else if (army.NationId == second.Id) pSecond += army.Divisions;
                }
                var (attacker, defender) = pFirst >= pSecond ? (first, second) : (second, first);
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
