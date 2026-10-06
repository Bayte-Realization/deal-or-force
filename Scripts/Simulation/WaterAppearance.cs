using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>Appearance only: none of these settings change province or crossing data.</summary>
public sealed record WaterAppearance(
    string LakeTexture = "", string RiverTexture = "",
    string LakeRelative = "gfx/water/lake_surface.png", string RiverRelative = "gfx/water/river_surface.png",
    float RiverInset = .18f, float RiverNearOpacity = .92f, float RiverFarOpacity = .58f,
    float ShoreWidth = .75f, float DepthReach = 5f,
    float TextureStrength = .65f, float TileRepeat = 640f,
    float RippleStrength = .035f, float AnimationSpeed = 0f, int TileResolution = 512)
{
    public RiverVisualProfile[] RiverProfiles { get; init; } = Array.Empty<RiverVisualProfile>();
}

/// <summary>Named cartographic strokes, in source-map texels, not physical crossing widths.</summary>
public sealed record RiverVisualProfile(string[] FeatureNames, float Width, float UpstreamWidth,
    float UpstreamLatitude, float FullWidthLatitude);

public static partial class TerrainCatalog
{
    public static WaterAppearance Water { get; private set; } = new();

    private static void ApplyWaterDirectory(string root, List<string> errors)
    {
        var water = Water;
        // Asset-only mods can replace a known PNG without repeating its declaration.
        if (File.Exists(LocalPath(root, water.LakeRelative))) water = water with { LakeTexture = Asset(root, water.LakeRelative) };
        if (File.Exists(LocalPath(root, water.RiverRelative))) water = water with { RiverTexture = Asset(root, water.RiverRelative) };
        Water = water;
        GameDataLoader.Asked.Add("map_data/water_settings");
        foreach (var path in Files(root, "map_data"))
        {
            if (!Path.GetFileNameWithoutExtension(path).Equals("water_settings", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var json = StructuredData.ReadDocument(path);
                var s = json.RootElement;
                var next = Water with
                {
                    LakeTexture = MapAsset(s, "lake_texture", root, Water.LakeTexture),
                    RiverTexture = MapAsset(s, "river_texture", root, Water.RiverTexture),
                    LakeRelative = s.TryGetProperty("lake_texture", out var lake) ? lake.GetString()! : Water.LakeRelative,
                    RiverRelative = s.TryGetProperty("river_texture", out var river) ? river.GetString()! : Water.RiverRelative,
                    RiverInset = Number(s, "river_inset_texels", Water.RiverInset, -.75f, .45f),
                    RiverNearOpacity = Number(s, "river_near_opacity", Water.RiverNearOpacity, 0, 1),
                    RiverFarOpacity = Number(s, "river_far_opacity", Water.RiverFarOpacity, 0, 1),
                    ShoreWidth = Number(s, "shore_width_texels", Water.ShoreWidth, .1f, 3),
                    DepthReach = Number(s, "lake_depth_texels", Water.DepthReach, 1, 7),
                    TextureStrength = Number(s, "texture_strength", Water.TextureStrength, 0, 1),
                    TileRepeat = Number(s, "tile_repeat", Water.TileRepeat, 1, 2000),
                    RippleStrength = Number(s, "ripple_strength", Water.RippleStrength, 0, .5f),
                    AnimationSpeed = Number(s, "animation_speed", Water.AnimationSpeed, 0, .05f),
                    TileResolution = (int)Number(s, "tile_resolution", Water.TileResolution, 64, 2048),
                    RiverProfiles = Profiles(s, Water.RiverProfiles)
                };
                Water = next; // Reject an invalid declaration as a whole.
            }
            catch (Exception e) { errors.Add($"{path}: {e.Message}"); }
        }
    }

    private static RiverVisualProfile[] Profiles(JsonElement settings, RiverVisualProfile[] fallback)
    {
        if (!settings.TryGetProperty("river_profiles", out var profiles)) return fallback;
        if (profiles.ValueKind != JsonValueKind.Array) throw new FormatException("river_profiles must be an array");
        var result = new List<RiverVisualProfile>();
        foreach (var entry in profiles.EnumerateArray())
        {
            var names = entry.GetProperty("feature_names").EnumerateArray()
                .Select(v => v.GetString() ?? "").Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length == 0) throw new FormatException("river profile needs feature_names");
            var width = Number(entry, "width_texels", 2.0f, .5f, 6);
            var upstream = Number(entry, "upstream_width_texels", width, .5f, 6);
            var north = Number(entry, "upstream_latitude", 90, -90, 90);
            var south = Number(entry, "full_width_latitude", -90, -90, 90);
            if (north <= south || upstream > width)
                throw new FormatException("river profile needs upstream_latitude > full_width_latitude and upstream_width <= width");
            result.Add(new(names, width, upstream, north, south));
        }
        return result.ToArray();
    }
}
