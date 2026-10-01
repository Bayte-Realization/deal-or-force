using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Who is allied to whom, and whether it is a world anybody would recognise.
///
/// A campaign played to 2060 ended with the United States allied to a hundred and thirty-six
/// countries, China and Russia and North Korea among them, and nothing else said so: every
/// one of those alliances was offered and accepted properly. This counts, once a year, how
/// many alliances there are and how they are spread, and the ones that should give pause:
///
/// - opposed: between governments whose politics are far apart (ideological distance at or
///   above 0.6 - a liberal democracy and a one-party state);
/// - far: between two countries neither of which is a great power, with capitals further
///   apart than 3,000 km - Chile and Laos;
/// - watched: any alliance between two of a short list of powers whose alliances would be
///   news (the United States and China, Russia and Japan...).
/// </summary>
internal static class AllianceReport
{
    private const float FarKm = 3000f;
    private const float OpposedDistance = 0.6f;

    private static readonly string[] Watched =
    {
        "USA", "CHN", "RUS", "GBR", "FRA", "DEU", "IND", "JPN", "PRK", "KOR", "IRN", "ISR",
        "SAU", "TUR", "PAK", "UKR"
    };

    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        var first = Snapshot(c, 0);
        Tally? last = null;
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (day % 365 == 0 || day == days) last = Snapshot(c, day);
        }
        var end = last ?? first;
        Console.WriteLine($"ALLIANCE SUMMARY pairs {first.Pairs}->{end.Pairs} mean {first.Mean:0.0}->{end.Mean:0.0} " +
                          $"biggest {end.Biggest} opposed {first.Opposed}->{end.Opposed} far {first.Far}->{end.Far} " +
                          $"watched {end.WatchedCount}: {string.Join(" ", end.WatchedPairs.Take(12))}");
        return 0;
    }

    private readonly record struct Tally(int Pairs, float Mean, string Biggest, int Opposed, int Far,
        int WatchedCount, List<string> WatchedPairs);

    private static Tally Snapshot(Campaign c, int day)
    {
        var powers = c.Nations.Where(n => n.Population > 0)
            .OrderByDescending(n => AiStatecraft.StrengthOf(c, n))
            .Take(c.Defines.AiGreatPowers)
            .Select(n => n.Id)
            .ToHashSet();
        var allies = new Dictionary<int, int>();
        int pairs = 0, opposed = 0, far = 0;
        var watched = new List<string>();
        var examples = new List<string>();
        foreach (var relation in c.Relations.Values)
        {
            if (relation.Stance != DiplomaticStance.Allied) continue;
            var a = c.NationById(relation.A);
            var b = c.NationById(relation.B);
            if (a is null || b is null || a.Population <= 0 || b.Population <= 0) continue;
            pairs++;
            allies[a.Id] = allies.GetValueOrDefault(a.Id) + 1;
            allies[b.Id] = allies.GetValueOrDefault(b.Id) + 1;
            if (PoliticsCore.IdeologicalDistance(a, b) >= OpposedDistance)
            {
                opposed++;
                if (examples.Count < 6) examples.Add($"{a.Tag}-{b.Tag}");
            }
            if (!powers.Contains(a.Id) && !powers.Contains(b.Id) && Km(a, b) > FarKm) far++;
            if (Array.IndexOf(Watched, a.Tag) >= 0 && Array.IndexOf(Watched, b.Tag) >= 0)
                watched.Add(string.CompareOrdinal(a.Tag, b.Tag) < 0 ? $"{a.Tag}-{b.Tag}" : $"{b.Tag}-{a.Tag}");
        }
        watched.Sort(StringComparer.Ordinal);
        var living = c.Nations.Count(n => n.Population > 0);
        var mean = living == 0 ? 0f : 2f * pairs / living;
        var biggest = string.Join(" ", allies.OrderByDescending(pair => pair.Value).Take(6)
            .Select(pair => $"{c.NationById(pair.Key)?.Tag}:{pair.Value}"));
        Console.WriteLine($"ALLIANCES y{day / 365f:0.#}: pairs {pairs}, {mean:0.0} each, biggest {biggest}; " +
                          $"opposed {opposed} ({string.Join(" ", examples)}), far {far}, " +
                          $"watched {watched.Count}: {string.Join(" ", watched.Take(14))}");
        return new Tally(pairs, mean, biggest, opposed, far, watched.Count, watched);
    }

    private static float Km(Nation a, Nation b)
    {
        const float radians = MathF.PI / 180f;
        var lat1 = a.CapitalLatitude * radians;
        var lat2 = b.CapitalLatitude * radians;
        var dLat = lat2 - lat1;
        var dLon = (b.CapitalLongitude - a.CapitalLongitude) * radians;
        var h = MathF.Sin(dLat / 2f) * MathF.Sin(dLat / 2f) +
                MathF.Cos(lat1) * MathF.Cos(lat2) * MathF.Sin(dLon / 2f) * MathF.Sin(dLon / 2f);
        return 2f * 6371f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(h)));
    }
}
