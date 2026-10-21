using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Which house sits on which crown, and therefore which crowns are cousins.
///
/// The game has governments but never had rulers: a monarchy is a string in
/// <see cref="Nation.Government"/>, and nothing died, inherited or married. Dynasties are
/// the one piece of the old story that still does work in a world without rulers - as a
/// claim. When a monarchy of the same house falls into civil war or below the stability
/// floor, a cousin crown acquires the oldest reason for a war there is (Statecraft's
/// "dynastic_claim"), and the peace that ends it restores a personal union rather than
/// annexing (AiPeaceOutcomes). In 2024 no shared house is in crisis, so the whole axis
/// sleeps; a mod for an older age wakes it with rows.
///
/// Data only: tags to houses in `common/dynasties`. Nothing here is state, nothing is
/// saved, nothing moves a fingerprint.
/// </summary>
public static class DynastyCatalog
{
    private static Dictionary<string, string> _dynastyOfTag;

    /// <summary>Tag to house, from `common/dynasties`. Empty means an age without houses.</summary>
    public static IReadOnlyDictionary<string, string> DynastyOfTag { get { Ensure(); return _dynastyOfTag; } }

    public static void ResetCatalog() => _dynastyOfTag = null;

    private static void Ensure()
    {
        if (_dynastyOfTag is not null) return;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in GameDataLoader.DataDocuments("common", "dynasties"))
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("dynasties", out var houses) ||
                houses.ValueKind != JsonValueKind.Object) continue;
            foreach (var house in houses.EnumerateObject())
            {
                if (house.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var tag in house.Value.EnumerateArray())
                    if (tag.ValueKind == JsonValueKind.String) map[tag.GetString()] = house.Name;
            }
        }
        _dynastyOfTag = map;
    }

    /// <summary>The house a tag wears, or an empty string for a republic or an unknown crown.</summary>
    public static string DynastyOf(string tag) =>
        !string.IsNullOrEmpty(tag) && DynastyOfTag.TryGetValue(tag, out var house) ? house : "";

    /// <summary>Whether two crowns sit on the same house.</summary>
    public static bool Shares(string tagA, string tagB)
    {
        var house = DynastyOf(tagA);
        return house.Length > 0 && string.Equals(house, DynastyOf(tagB), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed partial class Defines
{
    /// <summary>
    /// Whether a cousin crown may claim a monarchy in crisis. The axis sleeps without data
    /// rows either way; this is the switch a mod of a later age turns off.
    /// </summary>
    public bool DynasticClaimsEnabled { get; set; } = true;

    /// <summary>How low a fellow monarchy's stability has to fall before a cousin's claim becomes a reason for war.</summary>
    public float DynasticClaimStabilityMax { get; set; } = 0.35f;
}
