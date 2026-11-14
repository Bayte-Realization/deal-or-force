using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--exiles`: the movements that have no land, counted once a year - how many there are, how
/// many of them are fallen countries carrying on abroad or underground, and what those were
/// before they fell.
///
/// Written for a player's report that the map fills up with flags of "movements without land":
/// ten years into one world 40 of 86 movements were exiles, and among them militias, pieces a
/// war had carved off a region, the same exile twice and raw "rising:..." ids. A count of
/// movements says nothing about which of those it is; this sorts them by origin, so a rule
/// about who may go into exile can be judged by what it changes.
///
/// The origin is read from what the country recorded at its birth, not from its name:
///   fallen_state - a country of the bookmark, or one that took a state's own name and seat;
///   armed_group  - a faction of the bookmark (history/factions), a rising of the discontented
///                  or the faithful, or a "rising:" front that never settled;
///   fragment     - a breakaway or a piece released or carved out of a state that still claims it;
///   other        - nothing on record answers.
/// The classifier lives here rather than in the simulation so that the same instrument
/// measures the world before and after a change to the simulation's rule.
/// </summary>
internal static class ExileReport
{
    static readonly string[] Kinds = { "fallen_state", "armed_group", "fragment", "other" };

    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        // First sight of each exile, by the kind it was when it appeared: what the rule let in.
        var seen = new Dictionary<int, string>();
        var seenUnderground = new HashSet<int>();
        var dissolvedSeen = new HashSet<int>();
        var becameByKind = Kinds.ToDictionary(k => k, _ => 0);
        var undergroundByKind = Kinds.ToDictionary(k => k, _ => 0);
        Console.WriteLine($"EXILES seed={c.Seed} days={days}");
        Console.WriteLine("EXILES_COLUMNS year day movements landless map_flags exiles(gov/underground) " +
                          "by_kind[fallen_state/armed_group/fragment/other] dup_display dup_homeland raw_name raw_display " +
                          "legit[<.05/<.10/<.25/<.50/>=.50] dissolved_exiles");
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (day % 7 == 0 || day == days)
                foreach (var nation in c.Nations)
                {
                    if (nation.Movement is not { Exile: true } state) continue;
                    if (state.Dissolved)
                    {
                        if (seen.ContainsKey(nation.Id)) dissolvedSeen.Add(nation.Id);
                        continue;
                    }
                    if (!state.Landless || seen.ContainsKey(nation.Id)) continue;
                    var kind = Kind(nation);
                    seen[nation.Id] = kind;
                    if (Underground(state)) { seenUnderground.Add(nation.Id); undergroundByKind[kind]++; }
                    else becameByKind[kind]++;
                }
            if (day % 365 != 0 && day != days) continue;
            Line(c, day, dissolvedSeen.Count);
        }

        Console.WriteLine($"EXILES_FOUNDED government_in_exile " + string.Join(" ", Kinds.Select(k => $"{k}={becameByKind[k]}")));
        Console.WriteLine($"EXILES_FOUNDED underground " + string.Join(" ", Kinds.Select(k => $"{k}={undergroundByKind[k]}")));
        foreach (var nation in LiveExiles(c).OrderBy(n => Underground(n.Movement)).ThenBy(n => Kind(n)).ThenBy(n => n.Id))
        {
            var state = nation.Movement;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "EXILE {0} {1} kind={2} type={3} name=\"{4}\" display=\"{5}\" legit={6:0.00} recognised={7} sponsors={8} cadres={9:0} following={10:0.00} homeland={11}",
                nation.Tag, nation.Id, Kind(nation), Underground(state) ? "underground" : "government",
                nation.Name, nation.DisplayName, state.Legitimacy, state.RecognisedBy.Count, state.SponsoredBy.Count,
                state.Cadres, state.Support.Count == 0 ? 0f : state.Support.Values.Max(), state.Homeland.Count));
        }
        return 0;
    }

    static IEnumerable<Nation> LiveExiles(Campaign c) =>
        c.Nations.Where(n => n.Movement is { Exile: true, Landless: true, Dissolved: false });

    static bool Underground(MovementState state) =>
        state.Id.StartsWith(MovementCatalog.UndergroundPrefix, StringComparison.Ordinal);

    static void Line(Campaign c, int day, int dissolvedExiles)
    {
        var active = Movements.Active(c).ToList();
        var landless = active.Count(n => n.Movement.Landless);
        // What GlobeMovements draws: every standing landless movement with a headquarters.
        var flags = active.Count(n => n.Movement is { Landless: true, Dissolved: false } m && m.HeadquartersStateMapIndex >= 0);
        var exiles = LiveExiles(c).ToList();
        var underground = exiles.Count(n => Underground(n.Movement));
        var byKind = Kinds.Select(k => exiles.Count(n => Kind(n) == k)).ToArray();
        var dupDisplay = exiles.GroupBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Sum(g => g.Count() - 1);
        // The same lost state twice: two live exiles that each have most of their homeland in the other's.
        var dupHomeland = 0;
        for (var i = 0; i < exiles.Count; i++)
            for (var j = i + 1; j < exiles.Count; j++)
                if (SameHomeland(exiles[i].Movement, exiles[j].Movement)) dupHomeland++;
        var rawName = exiles.Count(n => RawId(n.Name));
        var rawDisplay = exiles.Count(n => RawId(n.DisplayName));
        var legit = new int[5];
        foreach (var n in exiles)
        {
            var l = n.Movement.Legitimacy;
            legit[l < 0.05f ? 0 : l < 0.10f ? 1 : l < 0.25f ? 2 : l < 0.5f ? 3 : 4]++;
        }
        Console.WriteLine($"EXILES year={day / 365} day={day} movements={active.Count} landless={landless} map_flags={flags} " +
                          $"exiles={exiles.Count}({exiles.Count - underground}/{underground}) " +
                          $"by_kind={string.Join("/", byKind)} dup_display={dupDisplay} dup_homeland={dupHomeland} " +
                          $"raw_name={rawName} raw_display={rawDisplay} legit={string.Join("/", legit)} dissolved_exiles={dissolvedExiles}");
    }

    static bool SameHomeland(MovementState a, MovementState b)
    {
        if (a.Homeland.Count == 0 || b.Homeland.Count == 0) return false;
        var shared = a.Homeland.Intersect(b.Homeland, StringComparer.Ordinal).Count();
        return shared * 2 > a.Homeland.Count && shared * 2 > b.Homeland.Count;
    }

    static bool RawId(string name) =>
        string.IsNullOrEmpty(name) || name.StartsWith("rising:", StringComparison.Ordinal) || name.Contains('@') ||
        NationNaming.LooksLikeKey(name);

    /// <summary>What the country was before it fell, from what it recorded at its birth.</summary>
    internal static string Kind(Nation n)
    {
        if (!string.IsNullOrEmpty(n.FactionId)) return "armed_group";
        if (n.Name.StartsWith("rising:", StringComparison.Ordinal)) return "armed_group";
        // A side that took the state's own name and seat was that state's government.
        if (n.FoundNameKind == "tag") return "fallen_state";
        var revolt = n.FoundedInRevolt ?? "";
        var born = n.RevoltParentId >= 0 || n.SecededFromId >= 0 || n.ReleasedFromId >= 0 || revolt.Length > 0;
        if (!born) return "fallen_state";
        // A country the scenario shipped that walked out of an overlord: born of nobody's revolt
        // and never given a founding name.
        if (n.ReleasedFromId < 0 && string.IsNullOrEmpty(n.FoundNameKind) && revolt.Length == 0) return "fallen_state";
        if (revolt is "discontent" or "sectarian") return "armed_group";
        return "fragment";
    }
}
