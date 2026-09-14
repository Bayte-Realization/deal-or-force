using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Where a country's army is standing on the first morning.
///
/// Every division in the game used to be dealt into a ring around its own capital, which is
/// a reasonable answer for a country at peace and the wrong one for a country at war: the
/// scenario opens with a front across eastern Ukraine and both armies a thousand kilometres
/// behind it, near Moscow and near Kyiv. The front is drawn, nobody is on it, and the first
/// weeks of the campaign are two armies walking towards a line that anybody could have taken
/// in the meantime.
///
/// A row names a country, what share of its divisions start forward, and the places they
/// stand. The places are coordinates rather than region names because this is read before
/// the map is attached - and because a line in a file is the honest way to say "the army was
/// here", the same way the traced borders in `map_data/state_regions` say where a border ran.
/// </summary>
public sealed class Deployment
{
    public string Tag = "";

    /// <summary>What share of the country's divisions are deployed forward, 0 to 1.</summary>
    public float Share = 0.5f;

    /// <summary>Where they stand, in degrees east and north. Divisions are dealt round-robin.</summary>
    public readonly List<(float Longitude, float Latitude)> Positions = new();
}

public static class DeploymentCatalog
{
    private static readonly Dictionary<string, Deployment> Deployments =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<Deployment> All => Deployments.Values;

    static DeploymentCatalog() => ResetCatalog();

    public static Deployment Of(string tag) =>
        tag is not null && Deployments.TryGetValue(tag, out var deployment) ? deployment : null;

    public static void ResetCatalog(List<string> errors = null)
    {
        Deployments.Clear();
        foreach (var document in GameDataLoader.DataDocuments("history", "deployments", "2024"))
            using (document)
                Merge(document.RootElement, errors);
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal) ||
                entry.Value.ValueKind != JsonValueKind.Object) continue;

            var deployment = new Deployment { Tag = entry.Name };
            if (entry.Value.TryGetProperty("share", out var share) && share.TryGetSingle(out var value))
                deployment.Share = M.Clamp01(value);
            if (entry.Value.TryGetProperty("positions", out var positions) &&
                positions.ValueKind == JsonValueKind.Array)
                foreach (var point in positions.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Array) continue;
                    var pair = point.EnumerateArray();
                    if (!pair.MoveNext() || !pair.Current.TryGetSingle(out var longitude)) continue;
                    if (!pair.MoveNext() || !pair.Current.TryGetSingle(out var latitude)) continue;
                    deployment.Positions.Add((longitude, latitude));
                }

            if (deployment.Positions.Count == 0)
            {
                errors?.Add($"deployments/{entry.Name}: no positions, so nothing is deployed.");
                continue;
            }
            Deployments[entry.Name] = deployment;
        }
    }

    /// <summary>
    /// Where the index-th division of this country stands, or null for "at the capital".
    ///
    /// Dealt round-robin along the positions, with each pass out a little further back, so
    /// forty divisions on eleven places read as a front in depth rather than eleven stacks
    /// of four sitting on the same coordinate.
    /// </summary>
    public static (float Longitude, float Latitude)? PlaceOf(string tag, int index, int divisions)
    {
        var deployment = Of(tag);
        if (deployment is null || deployment.Positions.Count == 0) return null;
        var forward = (int)MathF.Round(divisions * deployment.Share);
        if (index >= forward) return null;

        var place = deployment.Positions[index % deployment.Positions.Count];
        var echelon = index / deployment.Positions.Count;
        if (echelon == 0) return place;

        // Around it rather than behind it. Which way is "behind" depends on where the enemy
        // is, and this is read before there is a map to ask - so the later echelons ring the
        // place instead of claiming a rear that might be the front.
        var angle = (index * 2.39996f) % (MathF.PI * 2f);
        var radius = 0.10f * echelon;
        return (place.Longitude + MathF.Cos(angle) * radius, place.Latitude + MathF.Sin(angle) * radius);
    }
}
