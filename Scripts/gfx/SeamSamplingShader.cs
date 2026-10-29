using System.Text;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    // Godot specialises sampler arguments by their filter/repeat/source_color hints.
    // A shared sampler parameter cannot accept all four maps. Generate uniform-bound
    // functions from one formula instead, preserving each map's colour space/filter.
    private static string SeamSamplingFunctions()
    {
        const string template = @"
vec4 seam_lod0_MAP(vec2 uv) {
    vec2 size = vec2(textureSize(MAP, 0));
    vec2 p = vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0)) * size - 0.5;
    vec2 f = fract(p);
    vec2 i = floor(p);
    float x0 = mod(i.x, size.x);
    float x1 = mod(i.x + 1.0, size.x);
    float y0 = clamp(i.y, 0.0, size.y - 1.0);
    float y1 = clamp(i.y + 1.0, 0.0, size.y - 1.0);
    vec2 texel = 1.0 / size;
    vec4 a = textureLod(MAP, (vec2(x0, y0) + 0.5) * texel, 0.0);
    vec4 b = textureLod(MAP, (vec2(x1, y0) + 0.5) * texel, 0.0);
    vec4 c = textureLod(MAP, (vec2(x0, y1) + 0.5) * texel, 0.0);
    vec4 d = textureLod(MAP, (vec2(x1, y1) + 0.5) * texel, 0.0);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
vec4 seam_sample_MAP(vec2 uv) {
    vec2 wrapped = vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0));
    vec4 mipped = texture(MAP, wrapped);
    float width = max(float(textureSize(MAP, 0).x), 1.0);
    float span = exp2(max(textureQueryLod(MAP, wrapped).y, 0.0)) * 1.5 / width;
    float along = min(wrapped.x, 1.0 - wrapped.x);
    float at_seam = 1.0 - smoothstep(span * 0.35, max(span, 0.000001), along);
    if (at_seam <= 0.0) return mipped;
    return mix(mipped, seam_lod0_MAP(wrapped), at_seam);
}
";
        var source = new StringBuilder(template.Length * 4);
        foreach (var map in new[] { "political_map", "ocean_depth", "relief_color", "relief_height" })
            source.Append(template.Replace("MAP", map));
        return source.ToString();
    }
}
