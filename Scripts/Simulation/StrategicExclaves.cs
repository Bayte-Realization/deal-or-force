using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Extra defensive weight of detached home ground; 0 disables this preference.</summary>
    public float ExclaveDefenceWeight { get; set; } = .65f;
    /// <summary>Bounded target preference, not permission to ignore readiness or declare war.</summary>
    public float ExclaveAssaultWeight { get; set; } = .32f;
    /// <summary>Existing scenario reserves posted to border exclaves, keeping a capital reserve.</summary>
    public int ExclaveInitialGarrisons { get; set; } = 3;
}

/// <summary>
/// Public geography, not hidden enemy strength. Reuses the ownership-component index that
/// corridor diplomacy already maintains; never walks the world separately for each army.
/// A foreign land border distinguishes an exposed exclave from an ordinary offshore island.
/// Control affects whether a post can be held, not whether the territory is an exclave.
/// This is disposable derived data: no save fields, random draws or extra formations.
/// </summary>
public static class StrategicExclaves
{
    internal sealed record Survey(Dictionary<int, float> Importance, int[] Posts);

    [ThreadStatic] private static Dictionary<int, (int Province, int Size, int Score)> _postsScratch;
    [ThreadStatic] private static List<(int Province, int Size, int Score)> _orderedScratch;

    private static Survey Of(Campaign c, Nation n)
    {
        var map = c.ProvinceMap;
        var cache = c.Caches.Exclaves;
        // Ownership and destruction (not battlefront occupation) define national exclaves.
        var surveyRev = c.OwnershipRevision ^ (c.DestroyedProvinces.Count * 397);
        if (cache.TryGet(map, surveyRev, c.CapitalRevision, n.MapIndex, out var known)) return known;
        var values = new Dictionary<int, float>();
        var posts = _postsScratch ??= new Dictionary<int, (int Province, int Size, int Score)>();
        posts.Clear();
        var pieces = Corridors.Pieces(c, n);
        var main = pieces.Count > 0 ? pieces[0] : null;
        // Exact capital province wins over a split region whose two fragments share its id.
        foreach (var piece in pieces)
            if (piece.Contains(n.CapitalProvinceId)) { main = piece; break; }
        foreach (var piece in pieces)
        {
            if (ReferenceEquals(piece, main)) continue;
            var foreignBorder = false;
            foreach (var p in piece)
                foreach (var q in map.Neighbors[p])
                    if (!map.IsWater(q) && map.ProvinceToNation[q] > 0 &&
                        map.ProvinceToNation[q] != n.MapIndex) foreignBorder = true;
            if (!foreignBorder) continue;
            foreach (var p in piece)
            {
                var coastal = SeaLanes.TryHarbourPoint(map, p, out _, out _);
                values[p] = coastal ? 1f : .6f;
                var region = map.ProvinceToState[p];
                var score = coastal ? 100 : 0;
                foreach (var q in map.Neighbors[p]) if (piece.Contains(q)) score++;
                // One post per region: prefer its substantial landmass to a detached spit.
                if (!posts.TryGetValue(region, out var old) || piece.Count > old.Size ||
                    piece.Count == old.Size && (score > old.Score || score == old.Score && p < old.Province))
                    posts[region] = (p, piece.Count, score);
            }
        }
        var ordered = _orderedScratch ??= new List<(int Province, int Size, int Score)>();
        ordered.Clear();
        foreach (var row in posts.Values) ordered.Add(row);
        ordered.Sort(static (a, b) => a.Size != b.Size ? b.Size.CompareTo(a.Size) : a.Province.CompareTo(b.Province));
        var postProvinces = ordered.Count == 0 ? Array.Empty<int>() : new int[ordered.Count];
        for (var i = 0; i < ordered.Count; i++) postProvinces[i] = ordered[i].Province;
        var survey = new Survey(values, postProvinces);
        cache.Store(map, surveyRev, c.CapitalRevision, n.MapIndex, survey);
        return survey;
    }

    public static float Importance(Campaign c, int province)
    {
        if (c?.ProvinceMap is not { } map || province <= 0 || province > map.ProvinceCount || map.IsWater(province)) return 0;
        return c.NationsByMapIndex().TryGetValue(map.ProvinceToNation[province], out var n)
            ? Of(c, n).Importance.GetValueOrDefault(province) : 0;
    }

    public static IReadOnlyList<int> EntryPosts(Campaign c, Nation n) =>
        c?.ProvinceMap is null || n is null ? Array.Empty<int>() : Of(c, n).Posts;
}
