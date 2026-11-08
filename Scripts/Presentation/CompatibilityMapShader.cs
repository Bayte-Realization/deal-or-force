namespace DealOrForce;

/// <summary>Explicit --safe-map emergency material, not the normal integrated-GPU
/// path. Uses the SAME live ownership, control, fog and map-layer tables; it is not
/// a stale screenshot. Omits detailed terrain/relief/inland water only in this mode.
/// Normal Compatibility uses PackedMapShader with the full visual material.</summary>
public static class CompatibilityMapShader
{
    public const int TextureUnits = 12;
    public const string Source = @"shader_type spatial;
render_mode unshaded, cull_back;
uniform sampler2D political_map : source_color, filter_linear_mipmap;
uniform sampler2D province_ids : filter_nearest;
uniform sampler2D nation_ids : filter_nearest;
uniform sampler2D region_ids : filter_nearest;
uniform sampler2D province_control_colors : source_color, filter_nearest, repeat_disable;
uniform sampler2D province_owner_colors : source_color, filter_nearest, repeat_disable;
uniform sampler2D province_owner_ids : filter_nearest, repeat_disable;
uniform sampler2D map_mode_colors : source_color, filter_nearest, repeat_disable;
uniform sampler2D resource_colors : source_color, filter_nearest, repeat_disable;
uniform sampler2D front_membership : filter_nearest, repeat_disable;
uniform sampler2D fog_visibility : filter_nearest, repeat_disable;
uniform sampler2D selected_region_members : filter_nearest, repeat_disable;
uniform float owners_moved = 0.0;
uniform float map_mode = 0.0;
uniform float resource_mode = 0.0;
uniform float province_detail = 0.0;
uniform float province_border_opacity = 0.72;
uniform float selected_region_id = -1.0;
uniform float selected_province_id = -1.0;
uniform float selected_region_live = 0.0;
uniform float highlighted_nation = -1.0;
uniform float fog_viewer = -1.0;
uniform float fog_tint = 1.0;
uniform float fog_darkness = 0.74;
uniform float fog_saturation = 0.65;
uniform vec3 ocean_color = vec3(0.020, 0.075, 0.110);

float code_id(vec4 sample_) {
    vec2 bytes_ = floor(sample_.rg * 255.0 + 0.5);
    return bytes_.r + bytes_.g * 256.0;
}
vec2 lookup(float id) {
    return (vec2(mod(id, 256.0), floor(id / 256.0)) + 0.5) / 256.0;
}
float owner_at(vec2 at) {
    float native_id = code_id(texture(nation_ids, at));
    if (owners_moved < 0.5) return native_id;
    float province_ = code_id(texture(province_ids, at));
    vec4 live = texture(province_owner_ids, lookup(province_));
    return mix(native_id, code_id(live), step(0.5, live.b));
}
void fragment() {
    vec2 at = vec2(fract(UV.x), clamp(UV.y, 0.0, 1.0));
    vec4 paint = texture(political_map, at);
    float province_ = code_id(texture(province_ids, at));
    vec2 entry = lookup(province_);
    vec4 control_ = texture(province_control_colors, entry);
    vec4 owner_ = texture(province_owner_colors, entry);
    vec4 front_ = texture(front_membership, entry);
    if (front_.a > 0.5) discard;
    float owner_id = owner_at(at);
    vec3 land = mix(paint.rgb, owner_.rgb * 0.82, owner_.a);
    // A treaty is solid ownership; military control remains an occupation ribbon.
    land = mix(land, control_.rgb * 0.82, step(0.95, control_.a));
    float occupied = step(0.10, control_.a) * (1.0 - step(0.75, control_.a));
    float phase = (UV.x * 2.0 + UV.y) * 260.0;
    float ribbon = smoothstep(0.40, 0.48, abs(fract(phase) - 0.5));
    land = mix(land, control_.rgb * 0.88, occupied * ribbon);
    float leased = step(0.76, control_.a) * (1.0 - step(0.92, control_.a));
    vec2 dots = fract(UV * vec2(520.0, 260.0)) - 0.5;
    land = mix(land, control_.rgb, leased * (1.0 - smoothstep(0.13, 0.20, length(dots))));
    vec4 thematic = texture(map_mode_colors, lookup(owner_id));
    land = mix(land, thematic.rgb, step(0.5, map_mode) * thematic.a);
    vec4 resource_ = texture(resource_colors, entry);
    land = mix(land, resource_.rgb, resource_mode * resource_.a);

    vec2 texel = 1.0 / vec2(textureSize(province_ids, 0));
    vec2 dx = vec2(texel.x, 0.0);
    vec2 dy = vec2(0.0, texel.y);
    float national = max(max(step(0.5, abs(owner_id - owner_at(at + dx))), step(0.5, abs(owner_id - owner_at(at - dx)))),
                         max(step(0.5, abs(owner_id - owner_at(at + dy))), step(0.5, abs(owner_id - owner_at(at - dy)))));
    float provincial = max(step(0.5, abs(province_ - code_id(texture(province_ids, at + dx)))),
                           step(0.5, abs(province_ - code_id(texture(province_ids, at + dy)))));
    land = mix(land, vec3(0.13, 0.19, 0.22), provincial * province_detail * province_border_opacity * 0.65);
    land = mix(land, vec3(0.065, 0.105, 0.13), national * 0.85);
    float planned = step(0.1, front_.b) * provincial;
    land = mix(land, vec3(0.90, 0.66, 0.22), planned * 0.75);

    float region_ = code_id(texture(region_ids, at));
    float selected_region = (1.0 - step(0.5, abs(region_ - selected_region_id))) * step(0.0, selected_region_id);
    selected_region = mix(selected_region, texture(selected_region_members, entry).r, step(0.5, selected_region_live));
    float selected = max(selected_region, (1.0 - step(0.5, abs(province_ - selected_province_id))) * step(0.0, selected_province_id));
    land = mix(land, vec3(0.95, 0.78, 0.30), selected * 0.18);
    float highlighted = (1.0 - step(0.5, abs(owner_id - highlighted_nation))) * step(0.0, highlighted_nation);
    land = mix(land, vec3(0.96, 0.82, 0.44), highlighted * national * 0.65);
    float fog = step(0.0, fog_viewer) * (1.0 - step(0.5, texture(fog_visibility, entry).r)) * fog_tint;
    vec3 grey = vec3(dot(land, vec3(0.2126, 0.7152, 0.0722)));
    land = mix(land, mix(grey, land, fog_saturation) * fog_darkness, fog);
    ALBEDO = mix(ocean_color, land, paint.a);
}";
}
