using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace DealOrForce;

/// <summary>
/// Keep atlas arithmetic linear on every backend. Forward+/Mobile decode source_color
/// textures automatically; Compatibility gives the shader sRGB and converts ALBEDO
/// back to linear only AFTER fragment(). Without this boundary, dark sea constants
/// become almost black and relief/paint mixing changes when switching drivers.
/// IDs, masks, alpha and height fields must never pass through a colour transfer curve.
/// This is a construction-time rewrite of our own material, not a per-frame parser.
/// </summary>
public static class MapColorSpaceShader
{
    public const string Helpers = @"
vec3 atlas_decode_srgb(vec3 value) {
    return mix(value / 12.92, pow((max(value, vec3(0.0)) + 0.055) / 1.055, vec3(2.4)),
        step(vec3(0.04045), value));
}
vec3 atlas_encode_srgb(vec3 value) {
    value = max(value, vec3(0.0));
    return mix(value * 12.92, 1.055 * pow(value, vec3(1.0 / 2.4)) - 0.055,
        step(vec3(0.0031308), value));
}
vec4 atlas_color_sample(vec4 value) {
    if (OUTPUT_IS_SRGB) value.rgb = atlas_decode_srgb(value.rgb);
    return value;
}
vec3 atlas_color_output(vec3 value) {
    return OUTPUT_IS_SRGB ? atlas_encode_srgb(value) : value;
}
";

    public static string Build(string source)
    {
        if (source.Contains("vec4 atlas_color_sample(", StringComparison.Ordinal))
            throw new InvalidOperationException("Atlas colour-space boundary applied twice.");
        var colors = Regex.Matches(source, @"uniform\s+sampler\w+\s+(\w+)\s*:[^;]*\bsource_color\b[^;]*;")
            .Select(m => m.Groups[1].Value).ToArray();
        if (colors.Length == 0 || !source.Contains("float map_id(", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected atlas colour sampler contract.");
        var calls = Regex.Matches(source, @"\b(?:texture|textureLod|textureGrad|texelFetch)\s*\(\s*(?:" +
            string.Join("|", colors.Select(Regex.Escape)) + @")\s*,");
        // Work backwards so inserted wrappers never invalidate the remaining offsets.
        // Balanced parentheses preserve vec3 coordinates and nested seam/water expressions.
        foreach (var match in calls.Cast<Match>().Reverse())
        {
            var opening = source.IndexOf('(', match.Index);
            var depth = 1;
            var end = opening + 1;
            for (; end < source.Length && depth > 0; end++)
            { if (source[end] == '(') depth++; else if (source[end] == ')') depth--; }
            if (depth != 0) throw new InvalidOperationException("Unbalanced atlas colour sample.");
            source = source.Insert(end, ")").Insert(match.Index, "atlas_color_sample(");
        }
        // Encoding at the very end lets literals, terrain, ocean and ribbons all share
        // the original Forward+ maths. Do not encode masks/normals or clamp HDR emission.
        source = Regex.Replace(source, @"\b(ALBEDO|EMISSION)\s*=\s*([^;]+);",
            "$1 = atlas_color_output($2);");
        return source.Replace("float map_id(", Helpers + "\nfloat map_id(", StringComparison.Ordinal);
    }
}
