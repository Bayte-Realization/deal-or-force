using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Who takes what, and from how far away.
///
/// A world left to run for a few decades used to end with Brazil owning Lower Austria, Italy
/// owning Bohemia and South Korea owning Mongolia, and none of that showed up in any other
/// report: every one of them was a peace signed properly. This watches the political map
/// change day by day and sorts each piece of land that changed hands:
///
/// - colonised: it was nobody's (Antarctica);
/// - born: the new owner had no ground at all before - a secession, a movement's uprising;
/// - joined: it touches ground the new owner already had - an ordinary border moving;
/// - strait: it does not, but the new owner's nearest ground is a narrow sea away;
/// - far: it does not, and the nearest ground the new owner had is further than that;
/// - ally land: the country it was taken from was not the new owner's enemy in any war
///   that was running or had just ended - somebody who liberated a friend's province and
///   was then allowed to keep it.
///
/// And every month it counts occupations nobody is at war over: a province held by a
/// country that is not fighting the country that owns it.
/// </summary>
internal static class LandReport
{
    private const float StraitKm = 600f;

    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        var map = c.ProvinceMap;
        var owners = (ushort[])map.ProvinceToNation.Clone();
        var seenWars = new HashSet<int>(c.Wars.Select(war => war.Id));
        var early = new List<string>();
        var examples = new List<string>();
        int colonised = 0, born = 0;
        int transfers = 0, joined = 0, strait = 0, far = 0, farPieces = 0, allyLand = 0, allyPieces = 0;
        int strayPeak = 0, strayLast = 0, straySamples = 0;
        long straySum = 0;
        var strayExamples = new List<string>();

        Console.WriteLine($"LAND day 0: {StrayCount(c, null)} province(s) held by a country not at war with the owner");
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            var nations = c.Nations.Where(n => n.MapIndex > 0).GroupBy(n => n.MapIndex)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var war in c.Wars)
            {
                if (!seenWars.Add(war.Id) || day > 730) continue;
                early.Add($"d{day} {c.NationById(war.AttackerId)?.Tag}->{c.NationById(war.DefenderId)?.Tag}");
            }

            var gained = new Dictionary<ushort, List<int>>();
            for (var province = 1; province <= map.ProvinceCount; province++)
            {
                var now = map.ProvinceToNation[province];
                if (now == owners[province] || now == 0 || map.IsWater(province)) continue;
                if (!gained.TryGetValue(now, out var list)) gained[now] = list = new List<int>();
                list.Add(province);
            }

            foreach (var (owner, provinces) in gained)
            {
                transfers += provinces.Count;
                nations.TryGetValue(owner, out var taker);
                foreach (var piece in Pieces(map, provinces))
                {
                    var from = piece.Select(p => owners[p]).Where(o => o != 0).Distinct().ToList();
                    // Nobody's land - Antarctica, colonised - is not a conquest, near or far.
                    if (from.Count == 0) { colonised += piece.Count; continue; }
                    var fromTags = string.Join("/", from.Select(o => nations.TryGetValue(o, out var n) ? n.Tag : "#" + o));
                    var region = c.States.FirstOrDefault(s => s.MapIndex == map.ProvinceToState[piece[0]])?.Name ?? "?";
                    var friends = taker is not null && from.Count > 0 && from.All(o =>
                        nations.TryGetValue(o, out var loser) && !Enemies(c, taker.Id, loser.Id, day) && Fought(c, taker.Id, day));
                    if (friends)
                    {
                        allyLand += piece.Count;
                        allyPieces++;
                        if (examples.Count < 40)
                            examples.Add($"  d{day} ALLY {taker.Tag} took {piece.Count} province(s) of {fromTags} ({region}) {Settled(c, taker.Id, day)}");
                    }

                    var touches = piece.Any(p => map.Neighbors[p].Any(q => owners[q] == owner));
                    if (touches) { joined += piece.Count; continue; }
                    var km = NearestKm(map, owners, owner, piece[0]);
                    // A country with no ground before this is a new one - a secession, a
                    // movement's uprising - standing on land it rose on, not taking it.
                    if (!float.IsFinite(km)) { born += piece.Count; continue; }
                    if (km <= StraitKm) { strait += piece.Count; continue; }
                    far += piece.Count;
                    farPieces++;
                    if (examples.Count < 40)
                        examples.Add($"  d{day} FAR {taker?.Tag ?? "#" + owner} took {piece.Count} province(s) of {fromTags} ({region}), " +
                                     $"{(float.IsFinite(km) ? km.ToString("0") + " km" : "no land")} from its own {Settled(c, taker?.Id ?? -1, day)}");
                }
            }
            Array.Copy(map.ProvinceToNation, owners, owners.Length);

            if (day % 30 == 0 || day == days)
            {
                var stray = StrayCount(c, straySamples < 3 || day == days ? strayExamples : null);
                strayPeak = Math.Max(strayPeak, stray);
                strayLast = stray;
                straySum += stray;
                straySamples++;
            }
            if (day % 365 == 0 || day == days)
                Console.WriteLine($"LAND day {day}: moved={transfers} born={born} joined={joined} strait={strait} far={far} ({farPieces} piece(s)) " +
                                  $"ally_land={allyLand} ({allyPieces} piece(s)) stray_now={strayLast}");
        }

        Console.WriteLine("EARLY WARS " + string.Join(", ", early));
        foreach (var line in examples) Console.WriteLine(line);
        foreach (var line in strayExamples.Take(24)) Console.WriteLine(line);
        Console.WriteLine($"LAND SUMMARY days={days} moved={transfers} colonised={colonised} born={born} joined={joined} strait={strait} far={far} far_pieces={farPieces} " +
                          $"ally_land={allyLand} ally_pieces={allyPieces} stray_peak={strayPeak} stray_mean={(straySamples == 0 ? 0 : straySum / (double)straySamples):0.0} stray_end={strayLast}");
        return 0;
    }

    /// <summary>Whether the two were on opposite sides of a war running now or ended in the last week.</summary>
    private static bool Enemies(Campaign c, int a, int b, int day) =>
        c.Wars.Any(war => (!war.Ended || war.EndedDay >= c.ElapsedDays - 7) && war.Opposes(a, b));

    /// <summary>Whether the taker was in any war lately - land changing hands with no war at all is a secession or a movement, not a peace.</summary>
    private static bool Fought(Campaign c, int a, int day) =>
        c.Wars.Any(war => (!war.Ended || war.EndedDay >= c.ElapsedDays - 7) && war.Involves(a));

    private static string Settled(Campaign c, int taker, int day)
    {
        var war = c.Wars.Where(w => w.Ended && w.EndedDay >= c.ElapsedDays - 1 && w.Involves(taker))
            .OrderByDescending(w => w.EndedDay).FirstOrDefault();
        if (war is null) return "(no peace today)";
        var summary = war.PeaceSummary ?? "";
        if (summary.Length > 70) summary = summary[..70];
        return $"[war {c.NationById(war.AttackerId)?.Tag}->{c.NationById(war.DefenderId)?.Tag}, {war.BelligerentCount} in it: {summary}]";
    }

    private static int StrayCount(Campaign c, List<string> examples)
    {
        var map = c.ProvinceMap;
        var nations = c.Nations.Where(n => n.MapIndex > 0).GroupBy(n => n.MapIndex).ToDictionary(g => g.Key, g => g.First());
        var enemies = new HashSet<long>();
        foreach (var war in c.Wars)
        {
            if (war.Ended) continue;
            foreach (var a in war.Side(true))
                foreach (var d in war.Side(false))
                {
                    enemies.Add(((long)a << 32) | (uint)d);
                    enemies.Add(((long)d << 32) | (uint)a);
                }
        }
        var count = 0;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            var owner = map.ProvinceToNation[province];
            var holder = c.ProvinceController[province];
            if (owner == 0 || holder == owner || map.IsWater(province)) continue;
            if (!nations.TryGetValue(owner, out var ownerNation) || !nations.TryGetValue(holder, out var holderNation)) continue;
            if (enemies.Contains(((long)holderNation.Id << 32) | (uint)ownerNation.Id)) continue;
            count++;
            if (examples is not null && examples.Count < 24)
                examples.Add($"  d{c.ElapsedDays} STRAY {holderNation.Tag} holds province {province} of {ownerNation.Tag} " +
                             $"({c.States.FirstOrDefault(s => s.MapIndex == map.ProvinceToState[province])?.Name}) with no war between them");
        }
        return count;
    }

    private static IEnumerable<List<int>> Pieces(ProvinceTopology map, List<int> provinces)
    {
        var left = provinces.ToHashSet();
        while (left.Count > 0)
        {
            var start = left.First();
            left.Remove(start);
            var piece = new List<int> { start };
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
                foreach (var next in map.Neighbors[queue.Dequeue()])
                    if (left.Remove(next))
                    {
                        piece.Add(next);
                        queue.Enqueue(next);
                    }
            yield return piece;
        }
    }

    private static float NearestKm(ProvinceTopology map, ushort[] owners, ushort owner, int province)
    {
        var (lat, lon) = map.CenterOf(province);
        var best = float.PositiveInfinity;
        for (var other = 1; other <= map.ProvinceCount; other++)
        {
            if (owners[other] != owner) continue;
            var (lat2, lon2) = map.CenterOf(other);
            best = MathF.Min(best, Km(lat, lon, lat2, lon2));
        }
        return best;
    }

    private static float Km(float lat1, float lon1, float lat2, float lon2)
    {
        const float r = 6371f;
        var p1 = lat1 * MathF.PI / 180f;
        var p2 = lat2 * MathF.PI / 180f;
        var dp = p2 - p1;
        var dl = (lon2 - lon1) * MathF.PI / 180f;
        var h = MathF.Sin(dp / 2) * MathF.Sin(dp / 2) + MathF.Cos(p1) * MathF.Cos(p2) * MathF.Sin(dl / 2) * MathF.Sin(dl / 2);
        return 2f * r * MathF.Asin(MathF.Min(1f, MathF.Sqrt(h)));
    }
}
