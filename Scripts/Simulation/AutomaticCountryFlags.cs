using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class FlagCatalog
{
    /// <summary>Exile is a location/status, not a replacement for the country's identity.</summary>
    internal static bool KeepsNationalIdentity(Nation nation) =>
        nation is not null && (nation.Movement is null || nation.Movement.Exile);

    /// <summary>Use the same bespoke regional banner in diplomacy and headquarters,
    /// including after a defeated regional country becomes an exiled movement.</summary>
    public static bool NeedsRegionalBanner(Campaign campaign, Nation nation, string sprite)
    {
        if (!KeepsNationalIdentity(nation) || !string.IsNullOrWhiteSpace(nation.CustomFlag) ||
            !string.IsNullOrWhiteSpace(nation.FormedNationId)) return false;
        if (nation.MapIndex < SecessionEvents.FirstDynamicMapIndex &&
            nation.SecededFromId < 0 && nation.ReleasedFromId < 0) return false;
        if (string.IsNullOrEmpty(sprite)) return true;
        var fallback = sprite.StartsWith("flag_culture_", StringComparison.OrdinalIgnoreCase) ||
            sprite.StartsWith("flag_ideology_", StringComparison.OrdinalIgnoreCase) ||
            sprite.StartsWith("flag_government_", StringComparison.OrdinalIgnoreCase) ||
            sprite.StartsWith("flag_revolt_", StringComparison.OrdinalIgnoreCase);
        if (!fallback) return false;
        if (nation.FoundNameKind is "land" or "region" or "direction") return true;
        var parentId = nation.SecededFromId >= 0 ? nation.SecededFromId : nation.ReleasedFromId;
        if (parentId >= 0 && campaign?.NationById(parentId) is { } parent)
        {
            var primary = DemographyCatalog.PrimaryCulture(nation.Tag);
            return !string.IsNullOrEmpty(primary) && DemographyCatalog.IsPrimary(parent.Tag, primary);
        }
        return false;
    }

    /// <summary>Cheap presentation invalidation, never a network fingerprint. A static
    /// army can change its banner without changing its unit ID, position or template.</summary>
    public static int VisualStateHash(Campaign campaign)
    {
        var hash = new HashCode();
        foreach (var nation in campaign.Nations)
        {
            hash.Add(nation.Id); hash.Add(nation.Tag); hash.Add(nation.CustomFlag); hash.Add(nation.Color);
            hash.Add(nation.CustomFlagDesignJson);
            hash.Add(nation.FoundNameKind); hash.Add(nation.FoundNameId); hash.Add(nation.FoundedInRevolt);
            hash.Add(nation.SecededFromId); hash.Add(nation.ReleasedFromId);
            foreach (var qualifier in All) hash.Add(ValueOf(qualifier.Source, campaign, nation));
        }
        return hash.ToHashCode();
    }
    private static readonly Dictionary<string, bool> AutomaticVariantArt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A national identity does not freeze its artwork forever. Matching authored
    /// variants outrank the base flag; legacy workshop placeholders do not. Previews and
    /// live countries use this same list. No assets are removed from mods or saves.</summary>
    private static List<string> AutomaticCountryVariants(Campaign c, Nation n, List<string> names)
    {
        if (!KeepsNationalIdentity(n) || !string.IsNullOrEmpty(n.FormedNationId)) return names;
        var stem = "flag_" + Slug(n.Tag);
        var explicitSprites = new List<string>(); AddRegimeFlags(c, n, explicitSprites);
        var custom = string.IsNullOrWhiteSpace(n.CustomFlag) ? "" : Slug(n.CustomFlag);
        if (custom.Length > 0 && !custom.StartsWith("flag_", StringComparison.Ordinal)) custom = "flag_" + custom;
        bool OwnVariant(string name) => name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase);
        names.RemoveAll(name => OwnVariant(name) && name != custom && !explicitSprites.Contains(name) &&
            SpriteCatalog.Has(name) && !HasAutomaticVariantArt(name));
        return names;
    }

    internal static bool HasAutomaticVariantArt(string sprite)
    {
        if (!SpriteCatalog.Has(sprite)) return false;
        var path = SpriteCatalog.TextureOf(sprite);
        if (AutomaticVariantArt.TryGetValue(path, out var allowed)) return allowed;
        if (path.StartsWith(FlagDesignCatalog.TexturePrefix, StringComparison.Ordinal)) return true;
        var file = Path.GetFileNameWithoutExtension(path);
        if (file.StartsWith("flag_ideology_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("flag_government_", StringComparison.OrdinalIgnoreCase))
            return AutomaticVariantArt[path] = false;
        // These exact 192x120 base-game exports are the legacy workshop template set.
        // Explicit regime rules and packages' own paths remain free to use pixel artwork.
        const string basePrefix = "res://game/";
        if (!path.StartsWith(basePrefix, StringComparison.Ordinal) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var input = File.OpenRead(Path.Combine(GameDataLoader.DataFolder(), path[basePrefix.Length..]));
            Span<byte> header = stackalloc byte[24];
            allowed = input.Read(header) != 24 || header[0] != 137 || header[1] != 80 || header[2] != 78 || header[3] != 71 ||
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]) != 192 ||
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]) != 120;
        }
        catch (IOException) { allowed = false; }
        return AutomaticVariantArt[path] = allowed;
    }
}
