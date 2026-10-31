using Godot;

namespace DealOrForce;

/// <summary>
/// A soft darkening towards the corners of the campaign view, under every panel.
///
/// The map is the picture and the panels sit on its edges; a lens-like falloff keeps the eye in
/// the middle and gives the panels a calmer ground to read on - the framing every grand strategy
/// game and most engine demo scenes use. One full-screen quad, no texture, no per-frame work.
/// `map.vignette` in the skin sets the strength; 0 turns it off.
/// </summary>
public partial class DealOrForceGame
{
    private const string VignetteShader = @"shader_type canvas_item;
uniform float strength = 0.38;
uniform vec4 tint : source_color = vec4(0.02, 0.03, 0.05, 1.0);
void fragment() {
    vec2 p = (UV - 0.5) * vec2(1.0, 0.82);
    float fall = smoothstep(0.30, 0.78, length(p) * 1.18);
    COLOR = vec4(tint.rgb, fall * strength);
}";

    private void BuildMapVignette()
    {
        var strength = Ui.Metric("map.vignette", 0.38f);
        if (strength <= 0f) return;
        var material = new ShaderMaterial { Shader = new Shader { Code = VignetteShader } };
        material.SetShaderParameter("strength", Mathf.Clamp(strength, 0f, 1f));
        material.SetShaderParameter("tint", Ui.Colour("surface.background"));
        var veil = new ColorRect
        {
            Name = "MapVignette",
            Material = material,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None
        };
        veil.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _hudLayer.AddChild(veil);
        _hudLayer.MoveChild(veil, 0);
    }
}
