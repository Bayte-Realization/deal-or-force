using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DealOrForce;

/// <summary>
/// The same map shader with fewer texture bindings, not fewer visual features.
/// GLES 3 guarantees sixteen fragment texture units; the renderer needs some too.
/// Twelve small, nearest-filtered tables share one array, the three geographic ID
/// rasters another, and the two equally sized water photographs a third. Layers do
/// not interpolate into each other. No province, artwork or terrain resolution is lost.
/// </summary>
public static class PackedMapShader
{
    public const int TextureUnits = 12;
    public static IReadOnlyList<string> Lookups { get; } = Array.AsReadOnly(new[] {
        "province_control_colors", "province_owner_colors", "province_owner_ids",
        "nation_palette", "front_membership", "map_mode_colors", "resource_colors",
        "province_terrain", "border_plan_targets", "border_controller_ids",
        "selected_region_members", "fog_visibility"
    });
    public static IReadOnlyList<string> Ids { get; } = Array.AsReadOnly(new[] {
        "region_ids", "province_ids", "nation_ids"
    });

    public static int Layer(IReadOnlyList<string> names, string uniform)
    {
        for (var i = 0; i < names.Count; i++) if (names[i] == uniform) return i;
        return -1;
    }

    // These transformations operate only on our owned shader, once at construction.
    // Refuse an unexpected contract instead of silently shipping a half-packed map.
    public static string Build(string full)
    {
        var source = full;
        foreach (var name in Lookups) source = RemoveUniform(source, name);
        foreach (var name in Ids) source = RemoveUniform(source, name);
        source = RemoveUniform(RemoveUniform(source, "lake_surface"), "river_surface");

        source = source.Replace("sampler2D map_texture", "float map_texture", StringComparison.Ordinal)
            .Replace("sampler2D ids", "float ids", StringComparison.Ordinal)
            .Replace("texture(map_texture,", "packed_id(map_texture,", StringComparison.Ordinal)
            .Replace("textureSize(ids, 0)", "textureSize(packed_ids, 0).xy", StringComparison.Ordinal);
        for (var i = 0; i < Ids.Count; i++)
        {
            source = Regex.Replace(source, @"textureSize\(\s*" + Ids[i] + @"\s*,\s*0\s*\)", "textureSize(packed_ids, 0).xy");
            source = Regex.Replace(source, @"\b" + Ids[i] + @"\b", i + ".0");
        }
        for (var i = 0; i < Lookups.Count; i++)
            source = Regex.Replace(source, @"texture\(\s*" + Lookups[i] + @"\s*,", "packed_lookup(" + i + ".0,");
        source = source.Replace("texture(lake_surface,", "packed_water(0.0,", StringComparison.Ordinal)
            .Replace("texture(river_surface,", "packed_water(1.0,", StringComparison.Ordinal);

        const string helpers = @"
uniform sampler2DArray packed_ids : filter_nearest, repeat_enable;
// Compatibility shades in sRGB. Mixing color and data in this unhinted array
// preserves both raw color bytes and two-channel identifiers (no sRGB decode).
uniform sampler2DArray packed_lookups : filter_nearest, repeat_disable;
uniform sampler2DArray packed_water_tiles : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
vec4 packed_id(float layer, vec2 uv) { return texture(packed_ids, vec3(uv, layer)); }
vec4 packed_lookup(float layer, vec2 uv) { return texture(packed_lookups, vec3(uv, layer)); }
vec4 packed_water(float layer, vec2 uv) { return texture(packed_water_tiles, vec3(uv, layer)); }
";
        source = source.Replace("float map_id(", helpers + "\nfloat map_id(", StringComparison.Ordinal);
        if (Regex.Matches(source, @"uniform\s+sampler\w+\s+").Count != TextureUnits)
            throw new InvalidOperationException("Packed map shader sampler contract changed; review the layout before exporting.");
        return source;
    }

    private static string RemoveUniform(string source, string name)
    {
        var pattern = @"uniform\s+sampler2D\s+" + name + @"\s*:[^;]+;";
        if (Regex.Matches(source, pattern).Count != 1)
            throw new InvalidOperationException("Missing or duplicate map sampler: " + name);
        return Regex.Replace(source, pattern, "");
    }
}
