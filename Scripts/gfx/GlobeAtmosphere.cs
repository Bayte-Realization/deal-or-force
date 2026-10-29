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
    private const float GradeContrast = 1.05f;
    private const float GradeSaturation = 1.04f;
    private const float GradeBrightness = 1.0f;

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
    private static Viewport.Msaa MapAntialiasing() =>
        (Simulation.SettingsCatalog.Chosen("map_antialiasing") ?? "").Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "none" => Viewport.Msaa.Disabled,
            "2x" or "2" => Viewport.Msaa.Msaa2X,
            _ => Viewport.Msaa.Msaa4X
        };

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
            _daySun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
            _daySun.DirectionalShadowMaxDistance = 18f;
        }
    }

    private void CreateAtmosphereRim(Node3D world)
    {
        var shader = new Shader { Code = AtmosphereShader };
        var material = new ShaderMaterial { Shader = shader, RenderPriority = 12 };
        material.SetShaderParameter("limb_color", Ui.Colour("globe.atmosphere"));
        material.SetShaderParameter("strength", 0.45f);
        material.SetShaderParameter("falloff", 4.5f);
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
    }

    /// <summary>Keep the lit side of the halo on the lit side of the planet, and hide it flat.</summary>
    private void UpdateAtmosphereRim()
    {
        if (!IsInstanceValid(_atmosphereLayer)) return;
        _atmosphereLayer.Visible = !_flatMap;
        if (_flatMap || !IsInstanceValid(_daySun)) return;
        if (_atmosphereLayer.MaterialOverride is ShaderMaterial material)
            material.SetShaderParameter("sun_direction", _daySun.GlobalBasis.Z);
    }

    private const string AtmosphereShader = @"shader_type spatial;
render_mode unshaded, blend_add, cull_back, depth_draw_never, shadows_disabled, fog_disabled;
uniform vec4 limb_color : source_color = vec4(0.45, 0.68, 0.95, 1.0);
uniform float strength = 0.62;
uniform float falloff = 3.1;
uniform vec3 sun_direction = vec3(0.2, 0.6, 0.75);
void fragment() {
    vec3 n = normalize(NORMAL);
    // Zero facing the camera, one at the silhouette: the halo is the edge, not a wash.
    float rim = 1.0 - clamp(abs(dot(n, normalize(VIEW))), 0.0, 1.0);
    rim = pow(rim, falloff);
    // The night limb of a planet is dark. Without this the ring glows all the way round and
    // the globe stops looking lit by anything.
    vec3 world_n = normalize((MODEL_MATRIX * vec4(n, 0.0)).xyz);
    float lit = smoothstep(-0.28, 0.55, dot(world_n, normalize(sun_direction)));
    ALBEDO = limb_color.rgb;
    ALPHA = rim * strength * mix(0.18, 1.0, lit);
}";
}
