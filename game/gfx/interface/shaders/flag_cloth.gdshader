shader_type canvas_item;
render_mode unshaded;

uniform bool animate_flags = true;
uniform bool fabric_enabled = true;
uniform float wave_speed = 0.8;
uniform float wave_amplitude = 0.016;
uniform float fold_strength = 0.16;
uniform float weave_strength = 0.035;
varying vec4 flag_tint;

void vertex() {
    flag_tint = COLOR;
}

void fragment() {
    float phase = animate_flags ? TIME * wave_speed : 0.0;
    // The hoist stays still. The free end moves gently without changing the badge's
    // shape or hitbox; pennants keep their source alpha and emblems stay legible.
    float wave = sin(UV.x * 13.0 - phase * 2.0 + UV.y * 1.5);
    vec2 uv = UV;
    if (animate_flags) uv.y += wave * wave_amplitude * UV.x * UV.x;
    uv = clamp(uv, vec2(0.0), vec2(1.0));
    vec4 flag = texture(TEXTURE, uv);
    if (fabric_enabled) {
        float fold = cos(UV.x * 13.0 - phase * 2.0 + UV.y * 1.5);
        float shade = 1.0 - fold_strength * 0.45 + fold * fold_strength * (0.3 + 0.7 * UV.x);
        // Analytically fade sub-pixel threads: tiny HUD flags should show smooth
        // folds, not a moving checkerboard or GIF-like moire at fractional DPI.
        vec2 threads = UV * vec2(160.0, 100.0);
        vec2 resolved = clamp(vec2(1.0) - fwidth(threads) * 2.0, vec2(0.0), vec2(1.0));
        float weave = sin(threads.x * 6.283185) * sin(threads.y * 6.283185) * resolved.x * resolved.y;
        flag.rgb *= shade + weave * weave_strength;
    }
    COLOR = flag * flag_tint;
}
