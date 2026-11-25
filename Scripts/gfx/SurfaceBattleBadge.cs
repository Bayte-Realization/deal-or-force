using System;
using Godot;

namespace DealOrForce;

/// <summary>
/// An owned, cached 128x128 AAA battle progress bubble on a depth-tested surface quad.
/// Draws a smooth green-to-amber-to-red progress ring, attacker/defender national rim crescents,
/// clash emblem, momentum arrow, pocket alert rim, and tabular percentage pill.
/// Only redraws the 2D SubViewport when telemetry changes; subtle combat pulse runs on GPU
/// (gated by <c>quality_tier</c> so MobileLow skips extra shader work).
/// </summary>
public partial class SurfaceBattleBadge : Node3D
{
    private const int CanvasSize = 128;

    private static Shader _pulseShader;
    private static Shader PulseShader => _pulseShader ??= new Shader
    {
        Code = """
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_back, shadows_disabled;
uniform sampler2D badge_tex : source_color, filter_linear_mipmap;
uniform float quality_tier = 0.0;
uniform float pulse_strength = 1.0;
uniform vec3 pulse_color = vec3(1.0, 0.34, 0.16);
void fragment() {
    vec4 tex = texture(badge_tex, UV);
    vec2 centered = UV * 2.0 - 1.0;
    float r = length(centered);
    float ring = smoothstep(0.76, 0.90, r) * (1.0 - smoothstep(0.90, 0.99, r));
    float beat = quality_tier < 1.5 ? (0.5 + 0.5 * sin(TIME * 3.8)) * pulse_strength : 0.0;
    vec3 col = tex.rgb + pulse_color * ring * beat * 0.45;
    float alpha = max(tex.a, ring * beat * 0.48);
    if (alpha < 0.04) discard;
    ALBEDO = col;
    ALPHA = alpha;
}
"""
    };

    public Sprite3D Plate { get; private set; }
    private SubViewport _canvas;
    private Node2D _ink;
    private ShaderMaterial _plateMaterial;
    private string _text, _tone;
    private float _share;
    private float _momentum;
    private bool _pocketAlert;
    private Color _attackerColor = new(0.88f, 0.30f, 0.22f);
    private Color _defenderColor = new(0.28f, 0.62f, 0.88f);
    private Texture2D _symbol;
    private ViewportTexture _ownedTexture;
    private float _qualityTier;

    public override void _Ready()
    {
        _canvas = new SubViewport
        {
            Name = "BattleBadgeCanvas",
            Size = new Vector2I(CanvasSize, CanvasSize),
            Disable3D = true,
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once
        };
        AddChild(_canvas);
        _ink = new Node2D();
        _canvas.AddChild(_ink);
        _ink.Draw += Paint;
        _ownedTexture = _canvas.GetTexture();

        _plateMaterial = new ShaderMaterial { Shader = PulseShader, RenderPriority = 20 };
        _plateMaterial.SetShaderParameter("badge_tex", _ownedTexture);
        _plateMaterial.SetShaderParameter("quality_tier", _qualityTier);
        _plateMaterial.SetShaderParameter("pulse_strength", 1f);

        Plate = new Sprite3D
        {
            Name = "BattleSurfacePlate",
            Texture = _ownedTexture,
            MaterialOverride = _plateMaterial,
            PixelSize = 1f / CanvasSize,
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
            NoDepthTest = false,
            AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
            RenderPriority = 20,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(Plate);
    }

    public override void _ExitTree()
    {
        // The generated C# wrapper can outlive a queued node until a later GC.
        // Release OUR viewport texture now; never dispose the borrowed icon/font.
        if (IsInstanceValid(_ink)) _ink.Draw -= Paint;
        if (IsInstanceValid(Plate))
        {
            Plate.MaterialOverride = null;
            Plate.Texture = null;
        }
        // ShaderMaterial and ViewportTexture are RefCounted; never manually Dispose() on teardown.
        _plateMaterial = null;
        _ownedTexture = null;
        _symbol = null;
    }

    public void SetQualityTier(float tier)
    {
        if (Mathf.IsEqualApprox(_qualityTier, tier)) return;
        _qualityTier = tier;
        _plateMaterial?.SetShaderParameter("quality_tier", tier);
    }

    /// <summary>Park this badge in the reuse pool without tearing down its SubViewport render target.</summary>
    public void PrepareForPool()
    {
        Visible = false;
        if (IsInstanceValid(_canvas))
            _canvas.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    /// <summary>Re-arm a pooled badge for a newly opened battle.</summary>
    public void RestoreFromPool(float qualityTier)
    {
        Visible = true;
        _text = null;
        SetQualityTier(qualityTier);
    }

    public void SetReadout(string text, float share, string tone, Texture2D symbol) =>
        SetTelemetryReadout(text, share, tone, symbol, _attackerColor, _defenderColor, 0f, false);

    public void SetTelemetryReadout(
        string text,
        float share,
        string tone,
        Texture2D symbol,
        Color attackerColor,
        Color defenderColor,
        float momentum,
        bool pocketAlert)
    {
        if (_text == text &&
            MathF.Abs(_share - share) <= 0.001f &&
            _tone == tone &&
            _symbol == symbol &&
            _attackerColor == attackerColor &&
            _defenderColor == defenderColor &&
            MathF.Abs(_momentum - momentum) <= 0.05f &&
            _pocketAlert == pocketAlert)
            return;

        _text = text;
        _share = share;
        _tone = tone;
        _symbol = symbol;
        _attackerColor = attackerColor;
        _defenderColor = defenderColor;
        _momentum = momentum;
        _pocketAlert = pocketAlert;

        if (_plateMaterial is not null)
        {
            var pulseCol = pocketAlert
                ? new Color(1.0f, 0.18f, 0.14f)
                : share >= 0.56f
                    ? new Color(0.28f, 0.88f, 0.46f)
                    : share <= 0.44f
                        ? new Color(1.0f, 0.28f, 0.16f)
                        : new Color(0.98f, 0.68f, 0.20f);
            _plateMaterial.SetShaderParameter("pulse_color", new Vector3(pulseCol.R, pulseCol.G, pulseCol.B));
            _plateMaterial.SetShaderParameter("pulse_strength", pocketAlert ? 1.35f : 0.90f);
        }

        if (_canvas is null) return;
        _canvas.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        _ink.QueueRedraw();
    }

    private static Color ProgressRingColor(float share, bool known, string fallbackTone)
    {
        if (!known) return Ui.Colour(fallbackTone ?? "text.muted");
        var t = Math.Clamp(share, 0f, 1f);
        var red = Ui.Colour("state.negative");
        var amber = Ui.Colour("state.warning");
        var green = Ui.Colour("state.positive");
        return t < 0.5f
            ? red.Lerp(amber, t * 2f)
            : amber.Lerp(green, (t - 0.5f) * 2f);
    }

    private void Paint()
    {
        var center = new Vector2(64f, 64f);
        const float outerRadius = 56f;
        const float ringRadius = 48f;
        var known = _text is not (null or "?");
        var background = Ui.Colour("combat.badge_background");
        var border = _pocketAlert ? Ui.Colour("state.negative").Lightened(0.18f) : Ui.Colour("button.primary_border");
        var ringInk = ProgressRingColor(_share, known, _tone);

        // Soft dark cartographic drop-halo + sculpted dark medallion disc.
        _ink.DrawCircle(center, outerRadius + 4f, new Color(0.01f, 0.015f, 0.02f, 0.72f));
        _ink.DrawCircle(center, outerRadius, background.Darkened(0.22f));
        _ink.DrawCircle(center, outerRadius - 3f, background);

        // Dual faction rim crescents (left = attacker colour, right = defender colour).
        _ink.DrawArc(center, outerRadius - 1.5f, Mathf.Pi * 0.56f, Mathf.Pi * 1.44f, 32, _attackerColor.Lightened(0.18f), 3.2f, true);
        _ink.DrawArc(center, outerRadius - 1.5f, -Mathf.Pi * 0.44f, Mathf.Pi * 0.44f, 32, _defenderColor.Lightened(0.18f), 3.2f, true);
        _ink.DrawArc(center, outerRadius + 0.5f, 0f, Mathf.Tau, 64, border, _pocketAlert ? 2.6f : 1.8f, true);

        // Progress ring track + smooth green-amber-red progress arc.
        _ink.DrawArc(center, ringRadius, -Mathf.Pi * 0.5f, Mathf.Pi * 1.5f, 64, background.Darkened(0.55f), 7.5f, true);
        _ink.DrawArc(center, ringRadius, -Mathf.Pi * 0.5f, Mathf.Pi * 1.5f, 64, Ui.Colour("state.negative", 0.32f), 5.5f, true);
        if (known)
        {
            var sweep = Mathf.Tau * Math.Clamp(_share, 0.02f, 1f);
            _ink.DrawArc(center, ringRadius, -Mathf.Pi * 0.5f, -Mathf.Pi * 0.5f + sweep, 64, ringInk, 6.2f, true);
            // Bright leading-edge pip at the end of the progress arc.
            var tipAngle = -Mathf.Pi * 0.5f + sweep;
            var tip = center + new Vector2(MathF.Cos(tipAngle), MathF.Sin(tipAngle)) * ringRadius;
            _ink.DrawCircle(tip, 3.6f, ringInk.Lightened(0.35f));
        }

        // Subtle 4-way dial ticks on the inner rim.
        var tickCol = border.Darkened(0.25f);
        for (var i = 0; i < 4; i++)
        {
            var a = i * Mathf.Pi * 0.5f;
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            _ink.DrawLine(center + dir * (ringRadius - 4.5f), center + dir * (ringRadius + 3.5f), tickCol, 1.5f, true);
        }

        // Prominent clash emblem in upper center.
        if (_symbol is not null)
        {
            _ink.DrawTextureRect(_symbol, new Rect2(44f, 20f, 40f, 40f), false, ringInk.Lightened(0.22f));
        }
        else
        {
            _ink.DrawLine(new Vector2(49f, 26f), new Vector2(79f, 54f), ringInk.Lightened(0.18f), 4f, true);
            _ink.DrawLine(new Vector2(79f, 26f), new Vector2(49f, 54f), ringInk.Lightened(0.18f), 4f, true);
        }

        // Momentum chevron right above the percentage pill when a side is clearly pushing.
        if (known && MathF.Abs(_momentum) >= 0.08f)
        {
            var gaining = _momentum > 0f;
            var momCol = gaining ? Ui.Colour("state.positive").Lightened(0.15f) : Ui.Colour("state.negative").Lightened(0.15f);
            var tipY = gaining ? 55f : 63f;
            var baseY = gaining ? 63f : 55f;
            _ink.DrawPolygon(
                new[] { new Vector2(64f, tipY), new Vector2(57f, baseY), new Vector2(71f, baseY) },
                new[] { momCol, momCol, momCol });
        }

        // High-contrast tabular percentage pill.
        var font = PresentationFonts.For("ui.default");
        var text = _text == "?" ? "?" : (_text ?? "?") + "%";
        const int fontSize = 29;
        var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
        var pill = new Rect2(64f - width * 0.5f - 9f, 66f, width + 18f, 34f);
        _ink.DrawRect(pill, background.Darkened(0.45f));
        _ink.DrawRect(pill, ringInk.Darkened(0.15f), false, 1.8f);
        _ink.DrawString(font, new Vector2(64f - width * 0.5f, 92f), text, HorizontalAlignment.Left, -1, fontSize, Ui.Colour("text.bright"));
    }
}
