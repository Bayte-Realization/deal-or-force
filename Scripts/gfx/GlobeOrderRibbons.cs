using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly List<Vector3> _ribbonSamples = new();

    private void CreateOrderMaterials()
    {
        var shader = new Shader { Code = """
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;
uniform sampler2D ink : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 order_color : source_color = vec4(1.0, 0.76, 0.18, 1.0);
uniform bool support_order = false;
void fragment() {
    float edge_distance = min(UV.y, 1.0 - UV.y);
    float aa = max(fwidth(UV.y), 0.012);
    float coverage = smoothstep(0.0, aa, edge_distance);
    float inner = smoothstep(0.09, 0.09 + aa, edge_distance);
    float center_spine = 1.0 - smoothstep(0.0, 0.24, abs(UV.y - 0.5));
    float filled = 1.0 - smoothstep(COLOR.a - 0.006, COLOR.a + 0.006, UV2.x);
    float grain = mix(0.72, 1.10, texture(ink, UV).r);
    // Flowing tactical chevrons along the shaft (suppressed inside the arrowhead UV2.y > 0.5)
    float chevron_coord = fract(UV.x * 1.35 - abs(UV.y - 0.5) * 0.85 - TIME * 1.15);
    float chevron = smoothstep(0.18, 0.34, chevron_coord) * (1.0 - smoothstep(0.55, 0.72, chevron_coord)) * (1.0 - step(0.5, UV2.y));
    vec3 base_paint = order_color.rgb * grain * mix(0.68, 1.04, filled);
    base_paint = mix(base_paint, clamp(order_color.rgb * 1.28 + vec3(0.12, 0.10, 0.06), vec3(0.0), vec3(1.0)), chevron * 0.42 + center_spine * 0.18);
    ALBEDO = mix(vec3(0.04, 0.05, 0.07), base_paint, inner);
    float hatch = support_order && UV2.y < 0.5 ? mix(0.55, 1.0, step(0.28, fract(UV.x * 2.0))) : 1.0;
    ALPHA = coverage * mix(0.68, 0.98, filled) * hatch;
}
""" };
        var kinds = new[] { "move", "attack", "support" };
        var colors = new[] { new Color(1f, 0.78f, 0.22f), new Color(0.94f, 0.23f, 0.15f), new Color(1f, 0.46f, 0.24f) };
        for (var i = 0; i < kinds.Length; i++)
        {
            // The source PNG if it is on disk, the imported copy if it is not.
            //
            // Loading the source directly is deliberate and worth keeping: it means somebody
            // can replace the art and relaunch without the editor having imported it first.
            // But `GlobalizePath` on a `res://` path only names a real file while the editor
            // is what is running. In an exported game `res://` is a .pck, and this asked the
            // filesystem for `game/gfx/globe/order_move_ink_v2.png` relative to wherever the
            // player happened to launch from, got nothing, and dereferenced it - which is
            // the exception the first exported build died of, on the second screen.
            var image = OrderInk(kinds[i]);
            var material = new ShaderMaterial { Shader = shader };
            // No ink is a plain ribbon rather than no game: the shader multiplies by this
            // texture's red channel, so leaving it unset costs the paper grain and nothing
            // else. A ribbon that is a little flat beats a title screen that will not open.
            if (image is not null) material.SetShaderParameter("ink", ImageTexture.CreateFromImage(image));
            material.SetShaderParameter("order_color", colors[i]);
            material.SetShaderParameter("support_order", i == 2);
            _routeMaterials[i] = material;
        }
    }

    /// <summary>
    /// The paper grain for one order ribbon, from wherever this build keeps it.
    ///
    /// Three places, in the order that respects a mod: the loose file beside the game, which
    /// is what `game/` is and what somebody replacing the art would edit; then the copy
    /// Godot imported into the pack, which is the only one an exported build is guaranteed
    /// to have; then nothing, which the caller is expected to survive.
    /// </summary>
    private static Image OrderInk(string kind)
    {
        var name = $"order_{kind}_ink_v2.png";
        var loose = Simulation.GameDataLoader.FindDataFile("gfx", "globe", name);
        var image = string.IsNullOrEmpty(loose) ? null : Image.LoadFromFile(loose);

        if (image is null)
        {
            var packed = GD.Load<Texture2D>($"res://game/gfx/globe/{name}");
            image = packed?.GetImage();
        }
        if (image is null)
        {
            GD.PushWarning($"[dof] order ribbon ink {name} is missing; ribbons will draw without grain");
            return null;
        }

        // Thin map ribbons need a small tile, not three full-resolution art sources on the GPU.
        image.Resize(512, 512, Image.Interpolation.Lanczos);
        image.GenerateMipmaps();
        return image;
    }

    private Vector3 OnOrderSurface(Vector3 point) => _flatMap ? point : point.Normalized() * SurfaceLineRadius;

    private void SampleRibbonTo(Vector3 point)
    {
        if (_ribbonSamples.Count == 0) { _ribbonSamples.Add(OnOrderSurface(point)); return; }
        var start = _ribbonSamples[^1];
        var steps = Math.Clamp((int)MathF.Ceiling(start.DistanceTo(point) / 0.012f), 1, 256);
        for (var step = 1; step <= steps; step++)
            _ribbonSamples.Add(OnOrderSurface(start.Lerp(point, step / (float)steps)));
    }

    private bool DrawOrderRibbon(Simulation.Army army, bool attack)
    {
        _ribbonSamples.Clear();
        SampleRibbonTo(_routePoints[0]);
        // Round only the immediate corner; the route still visits its province corridor.
        for (var i = 1; i < _routePoints.Count - 1; i++)
        {
            var corner = _routePoints[i];
            var enter = corner.Lerp(_routePoints[i - 1], 0.12f);
            var leave = corner.Lerp(_routePoints[i + 1], 0.12f);
            SampleRibbonTo(enter);
            for (var step = 1; step <= 4; step++)
            {
                var t = step / 4f;
                SampleRibbonTo(enter.Lerp(corner, t).Lerp(corner.Lerp(leave, t), t));
            }
        }
        SampleRibbonTo(_routePoints[^1]);
        var distances = new float[_ribbonSamples.Count];
        for (var i = 1; i < distances.Length; i++)
            distances[i] = distances[i - 1] + _ribbonSamples[i - 1].DistanceTo(_ribbonSamples[i]);
        var length = distances[^1];
        if (length < 0.0001f) return false;
        var headLength = MathF.Min(0.032f, length * 0.28f);
        var headStart = length - headLength;
        var halfWidth = MathF.Min(_flatMap ? 0.0060f : 0.0054f, headLength * 0.26f);
        var progress = OrderProgress(army);
        var material = _routeMaterials[army.SupportOnly ? 2 : attack ? 1 : 0];
        var headStarted = false;
        _routeMesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip, material);
        for (var i = 0; i < _ribbonSamples.Count; i++)
        {
            var point = _ribbonSamples[i];
            var before = _ribbonSamples[Math.Max(0, i - 1)];
            var after = _ribbonSamples[Math.Min(_ribbonSamples.Count - 1, i + 1)];
            var tangent = (after - before).Normalized();
            if (!headStarted && distances[i] >= headStart && i > 0)
            {
                var t = (headStart - distances[i - 1]) / MathF.Max(0.00001f, distances[i] - distances[i - 1]);
                var shoulder = OnOrderSurface(before.Lerp(point, t));
                EmitRibbonPair(shoulder, tangent, halfWidth, headStart, length, progress, false);
                // Same center, wider shoulder: one head, no shaft sticking out of its tip.
                EmitRibbonPair(shoulder, tangent, halfWidth * 2.4f, headStart, length, progress, true);
                headStarted = true;
            }
            var width = headStarted ? halfWidth * 2.4f * MathF.Max(0f, (length - distances[i]) / headLength) : halfWidth;
            EmitRibbonPair(point, tangent, width, distances[i], length, progress, headStarted);
        }
        _routeMesh.SurfaceEnd();
        return true;
    }

    private void EmitRibbonPair(Vector3 point, Vector3 tangent, float halfWidth, float distance,
        float length, float progress, bool head)
    {
        var normal = _flatMap ? Vector3.Forward : point.Normalized();
        var side = normal.Cross(tangent).Normalized() * halfWidth;
        for (var edge = 0; edge < 2; edge++)
        {
            _routeMesh.SurfaceSetColor(new Color(1f, 1f, 1f, progress));
            _routeMesh.SurfaceSetUV(new Vector2(distance / 0.07f, edge));
            _routeMesh.SurfaceSetUV2(new Vector2(distance / length, head ? 1f : 0f));
            _routeMesh.SurfaceAddVertex(OnOrderSurface(point + (edge == 0 ? side : -side)));
        }
    }
}
