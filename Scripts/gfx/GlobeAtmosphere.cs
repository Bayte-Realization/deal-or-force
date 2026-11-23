using System;
using Godot;

namespace DealOrForce;

/// <summary>
/// The air around the planet, and the grade the whole globe is seen through.
///
/// What was missing was not detail. The ground already has terrain tiles, relief normals, a
/// hillshade, sea depth, wave crests with sun glint and a surf line, and the boundaries are
/// reconstructed from the id rasters rather than sampled. What it did not have was anything
/// above the ground: a planet seen from orbit has a lit edge, and a grand-strategy map is
/// looked at for hours, so the frame around it matters as much as the fill.
///
/// Two pieces, both inside the globe's own viewport, so nothing here can touch the interface:
///
/// * A thin shell a little larger than the sphere, drawn additively and faded to nothing in
///   the middle. Its weight is `1 - dot(normal, view)`, which is zero facing the camera and
///   one at the silhouette - so it lights the limb and leaves the map alone. Brightness
///   follows the sun, because the night limb of a real planet is dark and a glowing ring all
///   the way round reads as a sticker.
/// * A gentle adjustment of contrast and saturation over the whole three-dimensional picture.
///   The map's own colours are authored and must not be repainted; on a display with a wide
///   gamut and no grading the same map reads flat next to any Paradox title, and the fix is a
///   few per cent either way, not a filter.
///
/// Deliberately no tonemapping change and no bloom. Both repaint the map - a filmic curve
/// desaturates the political colours that carry the actual information, and bloom on a map is
/// fog over the thing the player is reading.
/// </summary>
public sealed partial class GlobePresentation
{
    private MeshInstance3D _atmosphereLayer;

    /// <summary>Contrast and saturation, in per cent of the renderer's own picture.</summary>
    private const float GradeContrast = 1.06f;
    private const float GradeSaturation = 1.05f;
    private const float GradeBrightness = 0.99f;

    /// <summary>How thick the shell is, as a share of the planet's radius.</summary>
    private const float AtmosphereThickness = 0.0135f;

    /// <summary>
    /// The player's choice of edge smoothing, read once when the map is built.
    ///
    /// The planet's silhouette, the counter quads and the order ribbons are the only true
    /// geometric edges on the map, so this is the one place multisampling buys anything. Four
    /// samples is the default because it is the difference between a planet and a jagged circle;
    /// two and off exist for machines where the fill rate matters more than the outline.
    /// </summary>
    private static Viewport.Msaa MapAntialiasing(Simulation.QualityProfile profile = null)
    {
        var chosen = (Simulation.SettingsCatalog.Chosen("map_antialiasing") ?? "").Trim().ToLowerInvariant();
        if (chosen is "off" or "0" or "none") return Viewport.Msaa.Disabled;
        if (chosen is "2x" or "2")
            return profile is { EffectiveMsaaSamples: <= 0 } ? Viewport.Msaa.Disabled : Viewport.Msaa.Msaa2X;
        if (chosen is "4x" or "4")
        {
            if (profile is { EffectiveMsaaSamples: <= 0 }) return Viewport.Msaa.Disabled;
            if (profile is { EffectiveMsaaSamples: <= 2 }) return Viewport.Msaa.Msaa2X;
            return Viewport.Msaa.Msaa4X;
        }
        var samples = profile?.EffectiveMsaaSamples ?? 2;
        return samples switch
        {
            <= 0 => Viewport.Msaa.Disabled,
            <= 2 => Viewport.Msaa.Msaa2X,
            _ => Viewport.Msaa.Msaa4X
        };
    }

    private void ConfigureGlobeGrading()
    {
        if (!IsInstanceValid(_skyEnvironment)) return;
        var environment = _skyEnvironment.Environment;
        environment.AdjustmentEnabled = true;
        environment.AdjustmentBrightness = GradeBrightness;
        environment.AdjustmentContrast = GradeContrast;
        environment.AdjustmentSaturation = GradeSaturation;
        // The sun's own edge: a light with a little size gives a soft terminator on the relief,
        // where a point light gives a hard one and the mountains read as cut paper.
        if (IsInstanceValid(_daySun))
        {
            _daySun.LightAngularDistance = 0.9f;
            _daySun.ShadowBias = 0.02f;
            _daySun.DirectionalShadowMode = _quality.RenderScale >= 0.99f
                ? DirectionalLight3D.ShadowMode.Parallel4Splits
                : DirectionalLight3D.ShadowMode.Parallel2Splits;
            _daySun.DirectionalShadowMaxDistance = 18f;
        }
    }

    private void CreateAtmosphereRim(Node3D world)
    {
        var shader = new Shader { Code = AtmosphereShader };
        var material = new ShaderMaterial { Shader = shader, RenderPriority = 12 };
        material.SetShaderParameter("limb_color", Ui.Colour("globe.atmosphere"));
        _atmosphereLayer = new MeshInstance3D
        {
            Name = "AtmosphereRim",
            Mesh = new SphereMesh
            {
                Radius = GlobeRadius * (1f + AtmosphereThickness),
                Height = GlobeRadius * 2f * (1f + AtmosphereThickness),
                RadialSegments = 128,
                Rings = 64
            },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled
        };
        world.AddChild(_atmosphereLayer);
        ApplyAtmosphereLook();
        SyncWeatherReliefShells();
    }

    private void ApplyAtmosphereLook()
    {
        if (!IsInstanceValid(_atmosphereLayer) || _atmosphereLayer.MaterialOverride is not ShaderMaterial material) return;
        material.SetShaderParameter("quality_tier", CurrentShaderQualityTier());
        var polish = MapLabelPriority.AtmospherePolishForMood(
            _mapLookMode, RenderingServer.GetCurrentRenderingMethod(), 0f);
        material.SetShaderParameter("rayleigh_mie", polish.RayleighMieHalo ? 1f : 0f);
        if (polish.MuteSpaceRim)
        {
            material.SetShaderParameter("tabletop_warmth", 0f);
            material.SetShaderParameter("strength", 0f);
            material.SetShaderParameter("falloff", 5.2f);
        }
        else if (polish.RayleighMieHalo)
        {
            material.SetShaderParameter("tabletop_warmth", 0f);
            material.SetShaderParameter("strength", 0.22f);
            material.SetShaderParameter("falloff", 4.4f);
        }
        else if (polish.WarmLampRim)
        {
            material.SetShaderParameter("tabletop_warmth", polish.RimWarmth);
            material.SetShaderParameter("lamp_rim_color", Color.FromHtml(Simulation.MapLook.ColourHex("map.look_lamp")));
            material.SetShaderParameter("strength", 0.14f);
            material.SetShaderParameter("falloff", 5.0f);
        }
        else
        {
            material.SetShaderParameter("tabletop_warmth", 0f);
            material.SetShaderParameter("strength", 0.16f);
            material.SetShaderParameter("falloff", 5.0f);
        }
    }

    /// <summary>Keep the lit side of the halo on the lit side of the planet, and hide it flat or on paper.</summary>
    private void UpdateAtmosphereRim()
    {
        if (!IsInstanceValid(_atmosphereLayer)) return;
        _atmosphereLayer.Visible = !_flatMap && !_mapLookPaper;
        if (!_atmosphereLayer.Visible || !IsInstanceValid(_daySun)) return;
        if (_atmosphereLayer.MaterialOverride is ShaderMaterial material)
            material.SetShaderParameter("sun_direction", _daySun.GlobalBasis.Z);
    }

    private const string AtmosphereShader = @"shader_type spatial;
render_mode unshaded, blend_add, cull_back, depth_draw_never, shadows_disabled, fog_disabled;
uniform vec4 limb_color : source_color = vec4(0.45, 0.68, 0.95, 1.0);
uniform float strength = 0.18;
uniform float falloff = 4.8;
uniform vec3 sun_direction = vec3(0.2, 0.6, 0.75);
uniform float tabletop_warmth = 0.0;
uniform float rayleigh_mie = 0.0;
uniform float quality_tier = 0.0;
uniform vec3 lamp_rim_color : source_color = vec3(0.98, 0.98, 0.97);
uniform vec3 lamp_direction = vec3(-0.62, 0.62, 0.48);
void fragment() {
    vec3 n = normalize(NORMAL);
    vec3 v = normalize(VIEW);
    // Zero facing the camera, one at the silhouette: the halo is the edge, not a wash.
    float ndotv = clamp(abs(dot(n, v)), 0.0, 1.0);
    float rim = pow(1.0 - ndotv, falloff);
    vec3 world_n = normalize((MODEL_MATRIX * vec4(n, 0.0)).xyz);
    vec3 sun_l = normalize(sun_direction);
    float sun_facing = dot(world_n, sun_l);
    float lit = smoothstep(-0.28, 0.55, sun_facing);
    if (rayleigh_mie > 0.5 && quality_tier < 1.5) {
        // Rayleigh molecular sky scatter + Henyey-Greenstein crisp D65 Mie horizon scatter +
        // subtle warm golden-peach twilight scatter right along the dusk/dawn terminator limb.
        vec3 world_v = normalize((INV_VIEW_MATRIX * vec4(-v, 0.0)).xyz);
        float cos_theta = clamp(dot(world_v, sun_l), -1.0, 1.0);
        float rayleigh = 0.75 * (1.0 + cos_theta * cos_theta);
        float g = 0.68;
        float g2 = g * g;
        float mie = (1.0 - g2) / max(4.0 * PI * pow(1.0 + g2 - 2.0 * g * cos_theta, 1.5), 0.02);
        float terminator = smoothstep(-0.24, 0.08, sun_facing) * (1.0 - smoothstep(0.08, 0.52, sun_facing));
        float sharp_edge = pow(1.0 - ndotv, falloff * 1.45);
        vec3 scatter = vec3(0.22, 0.52, 0.98) * rayleigh * 0.70
                     + vec3(0.84, 0.92, 1.00) * (mie * 0.44 + sharp_edge * 0.36)
                     + vec3(0.98, 0.58, 0.32) * terminator * 0.28;
        ALBEDO = scatter;
        ALPHA = rim * strength * mix(0.10, 1.0, smoothstep(-0.32, 0.48, sun_facing));
    } else {
        float lamp = smoothstep(-0.15, 0.75, dot(world_n, normalize(lamp_direction)));
        ALBEDO = mix(limb_color.rgb, lamp_rim_color, tabletop_warmth * lamp);
        ALPHA = rim * strength * mix(0.18, 1.0, max(lit, lamp * tabletop_warmth));
    }
}";
}
