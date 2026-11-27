using System;
using Godot;

namespace DealOrForce;

/// <summary>
/// Zero-SubViewport GPU-instanced AAA battle progress medallion renderer.
/// Replaces per-battle 128x128 SubViewport FBOs (which allocated ~1.5-2.25 GB VRAM and triggered
/// D3D12/GLES3 FBO flushes during large wars) with a single shared <see cref="MultiMeshInstance3D"/>,
/// one analytical SDF spatial shader, and one 18 KB procedural digit atlas (<c>0-9, %, ?</c>).
/// All colour accents come from <see cref="Ui.Colour(string, float)"/> tokens.
/// </summary>
public static class BattleBadgeGpu
{
    public const int FloatsPerInstance = 20; // 12 (Transform3D) + 4 (Color) + 4 (CustomData)
    public const int FlagPocketAlert = 1;
    public const int FlagThirdPartyInfo = 2;
    public const int FlagMomentumUp = 4;
    public const int FlagMomentumDown = 8;
    public const int FlagKnown = 16;

    private const int GlyphCount = 12; // '0'..'9', '%', '?'
    private const int GlyphWidth = 32;
    private const int GlyphHeight = 48;

    private static Shader _shader;
    private static ImageTexture _glyphAtlas;

    public static Shader Shader => _shader ??= new Shader
    {
        Code = """
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_back, shadows_disabled;

uniform sampler2D glyph_atlas : filter_linear, repeat_disable;
uniform float quality_tier = 0.0;
uniform float linear_renderer = 0.0;
uniform vec4 col_bg : source_color = vec4(0.08, 0.10, 0.14, 1.0);
uniform vec4 col_border : source_color = vec4(0.76, 0.67, 0.45, 1.0);
uniform vec4 col_pos : source_color = vec4(0.24, 0.78, 0.42, 1.0);
uniform vec4 col_warn : source_color = vec4(0.95, 0.68, 0.18, 1.0);
uniform vec4 col_neg : source_color = vec4(0.90, 0.25, 0.20, 1.0);
uniform vec4 col_info : source_color = vec4(0.32, 0.68, 0.94, 1.0);
uniform vec4 col_muted : source_color = vec4(0.55, 0.60, 0.66, 1.0);
uniform vec4 col_text : source_color = vec4(0.96, 0.97, 0.98, 1.0);

varying vec4 v_color;
varying vec4 v_custom;

void vertex() {
    v_color = COLOR;
    v_custom = INSTANCE_CUSTOM;
}

vec3 decode_srgb(vec3 c) {
    return linear_renderer > 0.5 ? pow(max(c, vec3(0.0)), vec3(2.2)) : c;
}

float sd_segment(vec2 p, vec2 a, vec2 b) {
    vec2 pa = p - a;
    vec2 ba = b - a;
    float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
    return length(pa - ba * h);
}

float sd_box(vec2 p, vec2 b) {
    vec2 d = abs(p) - b;
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
}

float sample_glyph(int glyph_idx, vec2 local_uv) {
    if (local_uv.x < 0.0 || local_uv.x > 1.0 || local_uv.y < 0.0 || local_uv.y > 1.0) {
        return 0.0;
    }
    float u = (float(glyph_idx) + clamp(local_uv.x, 0.03, 0.97)) / 12.0;
    float v = clamp(local_uv.y, 0.03, 0.97);
    float sdf = texture(glyph_atlas, vec2(u, v)).r;
    float w = max(fwidth(sdf) * 0.85, 0.055);
    return smoothstep(0.5 - w, 0.5 + w, sdf);
}

void fragment() {
    if (v_color.a < 0.02) discard;

    // UV (0,0) top-left -> centered p in [-1, 1], +Y down to match 2D badge coordinates.
    vec2 p = UV * 2.0 - 1.0;
    float r = length(p);
    float aa = max(fwidth(r) * 1.15, 0.016);

    float share = clamp(v_custom.x, 0.0, 1.0);
    float pct_val = floor(v_custom.y + 0.5);
    int flags = int(floor(v_custom.z + 0.5));
    bool pocket_alert = (flags & 1) != 0;
    bool info_tone = (flags & 2) != 0;
    bool mom_up = (flags & 4) != 0;
    bool mom_down = (flags & 8) != 0;
    bool known = (flags & 16) != 0 && v_custom.x >= 0.0;

    // Unpack 15-bit defender RGB from v_custom.w (0..32767, exact in GLES3 + Vulkan) and decode sRGB on Forward+.
    float dp = floor(v_custom.w + 0.5);
    float db = floor(dp / 1024.0);
    float rem = dp - db * 1024.0;
    float dg = floor(rem / 32.0);
    float dr = rem - dg * 32.0;
    vec3 def_col = decode_srgb(vec3(dr, dg, db) / 31.0);
    vec3 att_col = decode_srgb(v_color.rgb);

    vec3 bg_col = col_bg.rgb;
    vec3 brd_col = col_border.rgb;
    vec3 pos_col = col_pos.rgb;
    vec3 warn_col = col_warn.rgb;
    vec3 neg_col = col_neg.rgb;
    vec3 info_col = col_info.rgb;
    vec3 muted_col = col_muted.rgb;
    vec3 text_col = col_text.rgb;

    vec3 ring_ink = muted_col;
    if (known) {
        if (info_tone) {
            ring_ink = info_col;
        } else {
            ring_ink = share < 0.5
                ? mix(neg_col, warn_col, share * 2.0)
                : mix(warn_col, pos_col, (share - 0.5) * 2.0);
        }
    }

    vec3 col = vec3(0.0);
    float alpha = 0.0;

    // 1. Soft drop halo + outer pulse ring (skipped on MobileLow quality_tier >= 1.5).
    float halo_a = (1.0 - smoothstep(0.86, 0.96, r)) * 0.78;
    col = decode_srgb(vec3(0.01, 0.015, 0.02));
    alpha = halo_a;

    if (quality_tier < 1.5) {
        float pulse_ring = smoothstep(0.78, 0.90, r) * (1.0 - smoothstep(0.90, 0.99, r));
        float pulse_strength = pocket_alert ? 1.35 : 0.90;
        float beat = (0.5 + 0.5 * sin(TIME * 3.8)) * pulse_strength;
        vec3 pulse_col = pocket_alert
            ? decode_srgb(vec3(1.0, 0.18, 0.14))
            : (share >= 0.56 ? decode_srgb(vec3(0.28, 0.88, 0.46))
                : (share <= 0.44 ? decode_srgb(vec3(1.0, 0.28, 0.16)) : decode_srgb(vec3(0.98, 0.68, 0.20))));
        float pa = pulse_ring * beat * 0.48;
        col = mix(col, pulse_col, clamp(pa / max(alpha + pa, 0.001), 0.0, 1.0));
        alpha = max(alpha, pa);
    }

    // 2. Sculpted dark medallion disc (outer radius 0.875, inner 0.828).
    float disc_outer = 1.0 - smoothstep(0.875 - aa, 0.875 + aa, r);
    if (disc_outer > 0.001) {
        vec3 bg_dark = bg_col * 0.72;
        col = mix(col, bg_dark, disc_outer);
        alpha = max(alpha, disc_outer);
    }
    float disc_inner = 1.0 - smoothstep(0.828 - aa, 0.828 + aa, r);
    if (disc_inner > 0.001) {
        col = mix(col, bg_col, disc_inner);
        alpha = max(alpha, disc_inner);
    }

    // 3. Dual faction rim crescents (left = attacker, right = defender) + outer border.
    float rim_band = smoothstep(0.805 - aa, 0.805 + aa, r) * (1.0 - smoothstep(0.865 - aa, 0.865 + aa, r));
    if (rim_band > 0.001) {
        float ang = atan(p.y, p.x); // 0 = right, +-PI = left
        float left_arc = smoothstep(1.70, 1.80, abs(ang));
        float right_arc = 1.0 - smoothstep(1.34, 1.44, abs(ang));
        if (left_arc > 0.001) {
            col = mix(col, min(att_col * 1.15, 1.0), rim_band * left_arc);
        }
        if (right_arc > 0.001) {
            col = mix(col, min(def_col * 1.15, 1.0), rim_band * right_arc);
        }
    }

    float border_w = pocket_alert ? 0.044 : 0.032;
    float border_ring = smoothstep(0.875 - border_w - aa, 0.875 - border_w + aa, r)
                      * (1.0 - smoothstep(0.885 - aa, 0.885 + aa, r));
    vec3 border_col = pocket_alert ? min(neg_col * 1.20, 1.0) : brd_col;
    if (border_ring > 0.001) {
        col = mix(col, border_col, border_ring);
        alpha = max(alpha, border_ring);
    }

    // 4. Progress ring track + green-amber-red progress arc + tip pip (r = 0.74).
    float track_outer = smoothstep(0.675 - aa, 0.675 + aa, r) * (1.0 - smoothstep(0.805 - aa, 0.805 + aa, r));
    if (track_outer > 0.001) {
        col = mix(col, bg_col * 0.42, track_outer);
    }
    float track_inner = smoothstep(0.695 - aa, 0.695 + aa, r) * (1.0 - smoothstep(0.785 - aa, 0.785 + aa, r));
    if (track_inner > 0.001) {
        col = mix(col, mix(bg_col * 0.42, neg_col, 0.34), track_inner);
        if (known) {
            // Clockwise angle from 12 o'clock (0, -1): [0, TAU)
            float cw = atan(p.x, -p.y);
            if (cw < 0.0) cw += 6.2831853;
            float sweep = 6.2831853 * clamp(share, 0.02, 1.0);
            float arc_mask = 1.0 - smoothstep(sweep - 0.04, sweep + 0.04, cw);
            col = mix(col, ring_ink, track_inner * arc_mask);
        }
    }
    if (known) {
        float sweep = 6.2831853 * clamp(share, 0.02, 1.0);
        vec2 tip = vec2(sin(sweep), -cos(sweep)) * 0.74;
        float tip_mask = 1.0 - smoothstep(0.052 - aa, 0.052 + aa, length(p - tip));
        if (tip_mask > 0.001) {
            col = mix(col, min(ring_ink * 1.25, 1.0), tip_mask);
        }
    }

    // 5. Subtle 4-way dial ticks on inner rim.
    vec2 ap = abs(p);
    float tick_d = min(
        sd_segment(ap, vec2(0.67, 0.0), vec2(0.79, 0.0)),
        sd_segment(ap, vec2(0.0, 0.67), vec2(0.0, 0.79)));
    float tick_mask = 1.0 - smoothstep(0.015 - aa, 0.015 + aa, tick_d);
    if (tick_mask > 0.001) {
        col = mix(col, brd_col * 0.72, tick_mask * 0.8);
    }

    // 6. Crossed-swords clash emblem in upper center (y in [-0.60, -0.20]).
    float blade1 = sd_segment(p, vec2(-0.22, -0.58), vec2(0.22, -0.20));
    float blade2 = sd_segment(p, vec2(0.22, -0.58), vec2(-0.22, -0.20));
    float hilt1 = sd_segment(p, vec2(-0.24, -0.29), vec2(-0.09, -0.17));
    float hilt2 = sd_segment(p, vec2(0.24, -0.29), vec2(0.09, -0.17));
    float emblem_d = min(min(blade1, blade2), min(hilt1, hilt2));
    float emblem_mask = 1.0 - smoothstep(0.038 - aa, 0.038 + aa, emblem_d);
    if (emblem_mask > 0.001) {
        col = mix(col, min(ring_ink * 1.15, 1.0), emblem_mask);
    }

    // 7. Momentum chevron right above the percentage pill when pushing.
    if (known && (mom_up || mom_down)) {
        float dir = mom_up ? -1.0 : 1.0;
        vec2 tip = vec2(0.0, -0.10 + dir * 0.045);
        vec2 bl = vec2(-0.11, -0.10 - dir * 0.045);
        vec2 br = vec2(0.11, -0.10 - dir * 0.045);
        float chev_d = min(sd_segment(p, bl, tip), sd_segment(p, tip, br));
        float chev_mask = 1.0 - smoothstep(0.028 - aa, 0.028 + aa, chev_d);
        vec3 mom_col = mom_up ? min(pos_col * 1.15, 1.0) : min(neg_col * 1.15, 1.0);
        if (chev_mask > 0.001) {
            col = mix(col, mom_col, chev_mask);
        }
    }

    // 8. High-contrast tabular battle readout pill (y in [0.00, 0.56]) + SDF atlas digits (matches _battleReadouts).
    int n_chars = 1;
    int c0 = 11; // '?'
    int c1 = 0;
    int c2 = 0;
    if (known && pct_val >= 0.0) {
        int ip = clamp(int(pct_val), 0, 100);
        if (ip >= 100) {
            n_chars = 3; c0 = 1; c1 = 0; c2 = 0;
        } else if (ip >= 10) {
            n_chars = 2; c0 = ip / 10; c1 = ip - c0 * 10;
        } else {
            n_chars = 1; c0 = ip;
        }
    }

    float char_w = n_chars >= 3 ? 0.30 : 0.36;
    float total_w = float(n_chars) * char_w;
    vec2 pill_center = vec2(0.0, 0.28);
    vec2 pill_half = vec2(max(total_w * 0.5 + 0.14, 0.34), 0.27);
    float pill_d = sd_box(p - pill_center, pill_half) - 0.04;
    float pill_fill = 1.0 - smoothstep(-aa, aa, pill_d);
    if (pill_fill > 0.001) {
        col = mix(col, bg_col * 0.45, pill_fill);
        float pill_edge = smoothstep(-0.034 - aa, -0.034 + aa, pill_d) * pill_fill;
        col = mix(col, ring_ink * 0.88, pill_edge);

        float text_x0 = -total_w * 0.5;
        float text_y0 = pill_center.y - 0.22;
        float text_h = 0.44;
        float rel_x = (p.x - text_x0) / char_w;
        float rel_y = (p.y - text_y0) / text_h;
        if (rel_x >= 0.0 && rel_x < float(n_chars) && rel_y >= 0.0 && rel_y <= 1.0) {
            int slot = int(floor(rel_x));
            int glyph = slot == 0 ? c0 : (slot == 1 ? c1 : c2);
            float ink = sample_glyph(glyph, vec2(fract(rel_x), rel_y));
            col = mix(col, text_col, ink);
        }
    }

    alpha *= v_color.a;
    if (alpha < 0.04) discard;
    ALBEDO = col;
    ALPHA = alpha;
}
"""
    };

    public static ImageTexture GlyphAtlas => _glyphAtlas ??= BuildGlyphAtlas();

    public static ShaderMaterial CreateSharedMaterial(float qualityTier)
    {
        var mat = new ShaderMaterial
        {
            Shader = Shader,
            RenderPriority = 22
        };
        mat.SetShaderParameter("glyph_atlas", GlyphAtlas);
        mat.SetShaderParameter("quality_tier", qualityTier);
        var isLinear = !string.Equals(RenderingServer.GetCurrentRenderingMethod(), "gl_compatibility", StringComparison.Ordinal);
        mat.SetShaderParameter("linear_renderer", isLinear ? 1f : 0f);
        ApplyThemePalette(mat);
        return mat;
    }

    public static void ApplyThemePalette(ShaderMaterial mat)
    {
        if (mat is null) return;
        mat.SetShaderParameter("col_bg", Ui.Colour("combat.badge_background"));
        mat.SetShaderParameter("col_border", Ui.Colour("button.primary_border"));
        mat.SetShaderParameter("col_pos", Ui.Colour("state.positive"));
        mat.SetShaderParameter("col_warn", Ui.Colour("state.warning"));
        mat.SetShaderParameter("col_neg", Ui.Colour("state.negative"));
        mat.SetShaderParameter("col_info", Ui.Colour("state.info"));
        mat.SetShaderParameter("col_muted", Ui.Colour("text.muted"));
        mat.SetShaderParameter("col_text", Ui.Colour("text.bright"));
    }

    public static float PackDefenderColor(Color defender)
    {
        var r = Math.Clamp((int)(defender.R * 31f + 0.5f), 0, 31);
        var g = Math.Clamp((int)(defender.G * 31f + 0.5f), 0, 31);
        var b = Math.Clamp((int)(defender.B * 31f + 0.5f), 0, 31);
        return r + g * 32 + b * 1024;
    }

    public static float PackFlags(bool known, bool pocketAlert, bool thirdPartyInfo, float momentum)
    {
        var flags = 0;
        if (pocketAlert) flags |= FlagPocketAlert;
        if (thirdPartyInfo) flags |= FlagThirdPartyInfo;
        if (known && momentum >= 0.08f) flags |= FlagMomentumUp;
        else if (known && momentum <= -0.08f) flags |= FlagMomentumDown;
        if (known) flags |= FlagKnown;
        return flags;
    }

    public static void WriteInstance(
        float[] buffer,
        int instanceIndex,
        in Transform3D transform,
        in Color attackerColor,
        float alpha,
        float share,
        float displayPercent,
        float flags,
        float packedDefenderColor)
    {
        var o = instanceIndex * FloatsPerInstance;
        var b = transform.Basis;
        var p = transform.Origin;
        // Godot MultiMesh Transform3D row-major 3x4 layout
        buffer[o + 0] = b.X.X; buffer[o + 1] = b.Y.X; buffer[o + 2] = b.Z.X; buffer[o + 3] = p.X;
        buffer[o + 4] = b.X.Y; buffer[o + 5] = b.Y.Y; buffer[o + 6] = b.Z.Y; buffer[o + 7] = p.Y;
        buffer[o + 8] = b.X.Z; buffer[o + 9] = b.Y.Z; buffer[o + 10] = b.Z.Z; buffer[o + 11] = p.Z;
        // COLOR
        buffer[o + 12] = attackerColor.R;
        buffer[o + 13] = attackerColor.G;
        buffer[o + 14] = attackerColor.B;
        buffer[o + 15] = alpha;
        // INSTANCE_CUSTOM
        buffer[o + 16] = share;
        buffer[o + 17] = displayPercent;
        buffer[o + 18] = flags;
        buffer[o + 19] = packedDefenderColor;
    }

    /// <summary>
    /// Generates a crisp 384x48 R8 (18 KB VRAM) Signed Distance Field (SDF) atlas of tabular numerals
    /// '0'..'9', '%', and '?' using analytical distance-to-segment/circle strokes.
    /// Zero font/SubViewport dependency; fwidth-based SDF anti-aliasing stays sharp at any zoom.
    /// </summary>
    private static ImageTexture BuildGlyphAtlas()
    {
        const int w = GlyphCount * GlyphWidth;
        const int h = GlyphHeight;
        const float strokeRadius = 0.118f;
        const float sdfSpread = 0.28f;
        var data = new byte[w * h];

        for (var g = 0; g < GlyphCount; g++)
        {
            var xOff = g * GlyphWidth;
            for (var py = 0; py < h; py++)
            {
                var ny = (py + 0.5f) / h; // [0..1], top to bottom
                for (var px = 0; px < GlyphWidth; px++)
                {
                    var nx = (px + 0.5f) / GlyphWidth; // [0..1], left to right
                    var d = EvalGlyphDistance(g, nx, ny);
                    var sdf = Math.Clamp(0.5f + (strokeRadius - d) / (2f * sdfSpread), 0f, 1f);
                    data[py * w + xOff + px] = (byte)Math.Clamp((int)(sdf * 255f + 0.5f), 0, 255);
                }
            }
        }

        using var img = Image.CreateFromData(w, h, false, Image.Format.R8, data);
        return ImageTexture.CreateFromImage(img);
    }

    private static float Seg(float px, float py, float ax, float ay, float bx, float by)
    {
        var pax = px - ax;
        var pay = py - ay;
        var bax = bx - ax;
        var bay = by - ay;
        var denom = bax * bax + bay * bay;
        var h = denom > 1e-6f ? Math.Clamp((pax * bax + pay * bay) / denom, 0f, 1f) : 0f;
        var dx = pax - bax * h;
        var dy = pay - bay * h;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float Ring(float px, float py, float cx, float cy, float rx, float ry)
    {
        var dx = (px - cx) / rx;
        var dy = (py - cy) / ry;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        return MathF.Abs(len - 1f) * MathF.Min(rx, ry);
    }

    private static float EvalGlyphDistance(int g, float x, float y)
    {
        // Aspect-normalized coordinates inside [0..1]x[0..1] (glyph aspect is 32x48 = 2:3).
        // Scale y by 1.5 so vertical and horizontal stroke weights match cleanly!
        var px = x;
        var py = y * 1.5f;
        const float L = 0.22f, R = 0.78f, M = 0.50f;
        const float T = 0.22f, C = 0.75f, B = 1.28f;

        return g switch
        {
            0 => Ring(px, py, M, C, 0.28f, 0.52f),
            1 => MathF.Min(
                Seg(px, py, M + 0.04f, T, M + 0.04f, B),
                MathF.Min(Seg(px, py, L + 0.06f, T + 0.16f, M + 0.04f, T), Seg(px, py, L + 0.04f, B, R - 0.02f, B))),
            2 => MathF.Min(
                MathF.Min(
                    py < C - 0.12f ? Ring(px, py, M, T + 0.24f, 0.26f, 0.24f) : 9f,
                    Seg(px, py, R - 0.02f, T + 0.28f, L, B)),
                Seg(px, py, L, B, R, B)),
            3 => MathF.Min(
                py <= C
                    ? (px >= M - 0.14f || py < T + 0.22f ? Ring(px, py, M, (T + C) * 0.5f, 0.26f, 0.26f) : 9f)
                    : (px >= M - 0.14f || py > B - 0.22f ? Ring(px, py, M, (C + B) * 0.5f, 0.28f, 0.27f) : 9f),
                Seg(px, py, M - 0.08f, C, M + 0.12f, C)),
            4 => MathF.Min(
                Seg(px, py, R - 0.08f, T, R - 0.08f, B),
                MathF.Min(Seg(px, py, R - 0.08f, T, L, C + 0.14f), Seg(px, py, L, C + 0.14f, R + 0.04f, C + 0.14f))),
            5 => MathF.Min(
                MathF.Min(Seg(px, py, R - 0.02f, T, L + 0.02f, T), Seg(px, py, L + 0.02f, T, L + 0.02f, C - 0.04f)),
                MathF.Min(
                    Seg(px, py, L + 0.02f, C - 0.04f, M + 0.06f, C - 0.04f),
                    px >= M - 0.18f || py >= B - 0.26f ? Ring(px, py, M, (C + B) * 0.5f - 0.02f, 0.27f, 0.28f) : 9f)),
            6 => MathF.Min(
                Ring(px, py, M, (C + B) * 0.5f, 0.27f, 0.26f),
                MathF.Min(Seg(px, py, L + 0.01f, C + 0.18f, L + 0.08f, T + 0.18f), Seg(px, py, L + 0.08f, T + 0.18f, R - 0.06f, T))),
            7 => MathF.Min(Seg(px, py, L, T, R, T), Seg(px, py, R, T, M - 0.08f, B)),
            8 => MathF.Min(
                Ring(px, py, M, (T + C) * 0.5f, 0.24f, 0.25f),
                Ring(px, py, M, (C + B) * 0.5f, 0.27f, 0.27f)),
            9 => MathF.Min(
                Ring(px, py, M, (T + C) * 0.5f + 0.02f, 0.27f, 0.26f),
                MathF.Min(Seg(px, py, R - 0.01f, C - 0.12f, R - 0.08f, B - 0.18f), Seg(px, py, R - 0.08f, B - 0.18f, L + 0.06f, B))),
            10 => MathF.Min( // '%'
                Seg(px, py, L + 0.02f, B - 0.04f, R - 0.02f, T + 0.04f),
                MathF.Min(Ring(px, py, L + 0.12f, T + 0.20f, 0.13f, 0.16f), Ring(px, py, R - 0.12f, B - 0.20f, 0.13f, 0.16f))),
            _ => MathF.Min( // '?'
                MathF.Min(
                    py < C - 0.06f && (px >= M - 0.12f || py <= T + 0.26f)
                        ? Ring(px, py, M, T + 0.23f, 0.25f, 0.23f)
                        : 9f,
                    Seg(px, py, M, C - 0.02f, M, C + 0.24f)),
                MathF.Sqrt((px - M) * (px - M) + (py - (B - 0.04f)) * (py - (B - 0.04f))) * 0.85f)
        };
    }
}
