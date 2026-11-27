using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private sealed class RibbonBatch
    {
        public readonly List<Vector3>[] Verts = { new(), new(), new() };
        public readonly List<Vector2>[] Uvs = { new(), new(), new() };
        public readonly List<Vector2>[] Uv2s = { new(), new(), new() };
        public readonly List<Color>[] Colors = { new(), new(), new() };
        public readonly List<int>[] Indices = { new(), new(), new() };
        public readonly Godot.Collections.Array SurfaceArrays = CreateArrays();

        private static Godot.Collections.Array CreateArrays()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            return arrays;
        }

        public void Clear()
        {
            for (var i = 0; i < Verts.Length; i++)
            {
                Verts[i].Clear();
                Uvs[i].Clear();
                Uv2s[i].Clear();
                Colors[i].Clear();
                Indices[i].Clear();
            }
        }

        public bool Commit(ArrayMesh mesh, Material m0, Material m1 = null, Material m2 = null)
        {
            mesh.ClearSurfaces();
            Material[] mats = { m0, m1, m2 };
            var surfaceIdx = 0;
            for (var i = 0; i < Verts.Length; i++)
            {
                if (Indices[i].Count == 0 || mats[i] is null) continue;
                SurfaceArrays[(int)Mesh.ArrayType.Vertex] = Verts[i].ToArray();
                SurfaceArrays[(int)Mesh.ArrayType.TexUV] = Uvs[i].ToArray();
                SurfaceArrays[(int)Mesh.ArrayType.TexUV2] = Uv2s[i].ToArray();
                SurfaceArrays[(int)Mesh.ArrayType.Color] = Colors[i].ToArray();
                SurfaceArrays[(int)Mesh.ArrayType.Index] = Indices[i].ToArray();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, SurfaceArrays);
                mesh.SurfaceSetMaterial(surfaceIdx++, mats[i]);
            }
            SurfaceArrays[(int)Mesh.ArrayType.Vertex] = default;
            SurfaceArrays[(int)Mesh.ArrayType.TexUV] = default;
            SurfaceArrays[(int)Mesh.ArrayType.TexUV2] = default;
            SurfaceArrays[(int)Mesh.ArrayType.Color] = default;
            SurfaceArrays[(int)Mesh.ArrayType.Index] = default;
            return surfaceIdx > 0;
        }
    }

    private readonly List<Vector3> _ribbonSamples = new();
    private readonly List<float> _ribbonDistances = new();
    private readonly List<System.Numerics.Vector3> _linePointNumScratch = new();
    private readonly List<DateLineSeam.Run> _lineRunScratch = new();
    private readonly RibbonBatch _routeBatch = new();
    private readonly RibbonBatch _seaLiftBatch = new();
    private RibbonBatch _activeRibbonBatch;
    private int _activeRibbonSlot;
    private int _activeRibbonPrevPair = -1;
    private static readonly Color RibbonWhite = Colors.White;

    private void CreateOrderMaterials()
    {
        if (_routeMaterials[1] is not null) return;
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
    // Flowing tactical chevrons along the shaft + crisp highlight on the arrowhead (UV2.y > 0.5).
    float is_head = step(0.5, UV2.y);
    float chevron_coord = fract(UV.x * 1.40 - abs(UV.y - 0.5) * 0.90 - TIME * 1.25);
    float chevron = smoothstep(0.18, 0.34, chevron_coord) * (1.0 - smoothstep(0.55, 0.72, chevron_coord)) * (1.0 - is_head);
    vec3 base_paint = order_color.rgb * grain * mix(0.72, 1.08, filled);
    base_paint = mix(base_paint, clamp(order_color.rgb * 1.32 + vec3(0.14, 0.11, 0.06), vec3(0.0), vec3(1.0)), chevron * 0.46 + center_spine * 0.20 + is_head * 0.22);
    ALBEDO = mix(vec3(0.03, 0.04, 0.055), base_paint, inner);
    float hatch = support_order && UV2.y < 0.5 ? mix(0.55, 1.0, step(0.28, fract(UV.x * 2.0))) : 1.0;
    ALPHA = coverage * mix(0.72, 0.98, max(filled, is_head * 0.85)) * hatch;
}
""" };
        var kinds = new[] { "move", "attack", "support" };
        // Distinct HOI4-grade directional inks: emerald-brass march vs vivid crimson assault vs amber support.
        var colors = new[] { new Color(0.32f, 0.84f, 0.54f), new Color(0.96f, 0.20f, 0.14f), new Color(1.0f, 0.58f, 0.18f) };
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

    private static System.Numerics.Vector3 ToNum(Vector3 v) => new(v.X, v.Y, v.Z);
    private static System.Numerics.Vector2 ToNum(Vector2 v) => new(v.X, v.Y);
    private static Vector3 ToGd(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    private bool TryPresentationPoint(float latitude, float longitude, float sphereRadius, out Vector3 point)
    {
        if (!GlobeLineSanity.IsValidGeo(latitude, longitude))
        {
            point = default;
            return false;
        }
        point = PresentationPoint(latitude, longitude, sphereRadius);
        return GlobeLineSanity.IsValidEmittedVertex(ToNum(point), _flatMap, _flatX);
    }

    private bool TryOnOrderSurface(Vector3 point, float sphereRadius, float flatZ, out Vector3 surfacePoint)
    {
        if (!GlobeLineSanity.IsFinite(ToNum(point)))
        {
            surfacePoint = default;
            return false;
        }
        if (_flatMap)
        {
            if (MathF.Abs(point.X - _flatX) > GlobeLineSanity.MaxFlatVertexXDelta ||
                MathF.Abs(point.Y) > GlobeLineSanity.MaxFlatVertexY)
            {
                surfacePoint = default;
                return false;
            }
            surfacePoint = LocalFlatPointFromWorld(point, flatZ);
            return GlobeLineSanity.IsValidEmittedVertex(ToNum(surfacePoint), true, _flatX);
        }

        if (!GlobeLineSanity.TrySafeNormalize(ToNum(point), out var unit))
        {
            surfacePoint = default;
            return false;
        }
        float radius;
        if (_reliefDisplacement > 0.0001f && _reliefHeightGrid is not null)
        {
            var geo = GlobeLineSanity.GeographicFromNormalSafe(unit);
            radius = LocalGlobeRadius(geo.Latitude, geo.Longitude, sphereRadius);
        }
        else
        {
            radius = sphereRadius;
        }
        if (!float.IsFinite(radius))
        {
            surfacePoint = default;
            return false;
        }
        surfacePoint = ToGd(unit * radius);
        return GlobeLineSanity.IsValidEmittedVertex(ToNum(surfacePoint), false, _flatX);
    }

    private Vector3 OnOrderSurface(Vector3 point) =>
        TryOnOrderSurface(point, SurfaceLineRadius, FlatLineZ, out var projected) ? projected : point;

    private void CollectValidLineRuns(IReadOnlyList<Vector3> points, float maxSegmentLength, List<DateLineSeam.Run> runs)
    {
        _linePointNumScratch.Clear();
        for (var i = 0; i < points.Count; i++)
            _linePointNumScratch.Add(ToNum(points[i]));
        GlobeLineSanity.CollectLineRuns(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_linePointNumScratch),
            _flatMap,
            FlatMapWidth,
            maxSegmentLength,
            runs,
            _flatX);
    }

    private void SampleRibbonTo(Vector3 point)
    {
        if (!TryOnOrderSurface(point, SurfaceLineRadius, FlatLineZ, out var projectedEnd)) return;
        if (_ribbonSamples.Count == 0)
        {
            _ribbonSamples.Add(projectedEnd);
            return;
        }
        var start = _ribbonSamples[^1];
        if (!GlobeLineSanity.IsValidEmittedVertex(ToNum(start), _flatMap, _flatX)) return;
        var dist = start.DistanceTo(projectedEnd);
        if (!float.IsFinite(dist) || dist < 1e-6f) return;
        var steps = Math.Clamp((int)MathF.Ceiling(dist / 0.012f), 1, 256);
        for (var step = 1; step <= steps; step++)
        {
            var interp = ToGd(GlobeLineSanity.InterpolateSurfacePoint(
                ToNum(start), ToNum(projectedEnd), step / (float)steps, _flatMap));
            if (TryOnOrderSurface(interp, SurfaceLineRadius, FlatLineZ, out var sample) &&
                _ribbonSamples[^1].DistanceSquaredTo(sample) >= 1e-10f)
            {
                _ribbonSamples.Add(sample);
            }
        }
    }

    private bool DrawOrderRibbon(Simulation.Army army, bool attack) =>
        DrawRibbon(_routeBatch, army.SupportOnly ? 2 : attack ? 1 : 0, OrderProgress(army));

    private bool DrawStraightRibbon(RibbonBatch batch, int slot, Vector3 start, Vector3 end,
        float progress, float widthScale = 1f)
    {
        if (batch is null) return false;
        if (!GlobeLineSanity.IsValidEmittedVertex(ToNum(start), _flatMap, _flatX) ||
            !GlobeLineSanity.IsValidEmittedVertex(ToNum(end), _flatMap, _flatX))
            return false;
        _routePoints.Clear();
        _routePoints.Add(start);
        _routePoints.Add(end);
        return DrawRibbon(batch, slot, progress, widthScale);
    }

    /// <summary>One ribbon along _routePoints into a preallocated batch slot, filled to a progress.</summary>
    private bool DrawRibbon(RibbonBatch batch, int slot, float progress, float widthScale = 1f)
    {
        if (batch is null || _routePoints.Count < 2) return false;
        progress = GlobeLineSanity.SafeClamp(progress, 0f, 1f, 0f);
        widthScale = GlobeLineSanity.SafeClamp(widthScale, 0.05f, 4f, 1f);
        var maxPointGap = _flatMap ? GlobeLineSanity.MaxFlatSegmentLength : GlobeLineSanity.MaxGlobeSegmentLength;
        CollectValidLineRuns(_routePoints, maxPointGap, _lineRunScratch);
        if (_lineRunScratch.Count == 0) return false;

        _activeRibbonBatch = batch;
        _activeRibbonSlot = slot;
        var drewAny = false;
        for (var r = 0; r < _lineRunScratch.Count; r++)
        {
            var run = _lineRunScratch[r];
            var isFinalRun = r == _lineRunScratch.Count - 1;
            _activeRibbonPrevPair = -1;
            _ribbonSamples.Clear();
            SampleRibbonTo(_routePoints[run.Start]);
            // Round only the immediate corner; the route still visits its province corridor.
            for (var i = run.Start + 1; i < run.Start + run.Count - 1; i++)
            {
                var corner = _routePoints[i];
                var enter = ToGd(GlobeLineSanity.InterpolateSurfacePoint(
                    ToNum(corner), ToNum(_routePoints[i - 1]), 0.12f, _flatMap));
                var leave = ToGd(GlobeLineSanity.InterpolateSurfacePoint(
                    ToNum(corner), ToNum(_routePoints[i + 1]), 0.12f, _flatMap));
                SampleRibbonTo(enter);
                for (var step = 1; step <= 4; step++)
                {
                    var t = step / 4f;
                    var a = ToGd(GlobeLineSanity.InterpolateSurfacePoint(ToNum(enter), ToNum(corner), t, _flatMap));
                    var b = ToGd(GlobeLineSanity.InterpolateSurfacePoint(ToNum(corner), ToNum(leave), t, _flatMap));
                    SampleRibbonTo(ToGd(GlobeLineSanity.InterpolateSurfacePoint(ToNum(a), ToNum(b), t, _flatMap)));
                }
            }
            SampleRibbonTo(_routePoints[run.Start + run.Count - 1]);
            if (_ribbonSamples.Count < 2) continue;

            _ribbonDistances.Clear();
            _ribbonDistances.Add(0f);
            for (var i = 1; i < _ribbonSamples.Count; i++)
                _ribbonDistances.Add(_ribbonDistances[i - 1] + _ribbonSamples[i - 1].DistanceTo(_ribbonSamples[i]));
            var length = _ribbonDistances[^1];
            if (!float.IsFinite(length) || length < 0.0001f) continue;

            var headLength = MathF.Min(0.032f, length * 0.28f);
            var headStart = length - headLength;
            var halfWidth = MathF.Min((_flatMap ? 0.0060f : 0.0054f) * widthScale, headLength * 0.26f);
            if (!float.IsFinite(halfWidth) || halfWidth <= 1e-6f) continue;

            var headStarted = false;
            for (var i = 0; i < _ribbonSamples.Count; i++)
            {
                var point = _ribbonSamples[i];
                var before = _ribbonSamples[Math.Max(0, i - 1)];
                var after = _ribbonSamples[Math.Min(_ribbonSamples.Count - 1, i + 1)];
                if (isFinalRun && !headStarted && _ribbonDistances[i] >= headStart && i > 0)
                {
                    var span = _ribbonDistances[i] - _ribbonDistances[i - 1];
                    var t = span > 0.00001f ? Math.Clamp((headStart - _ribbonDistances[i - 1]) / span, 0f, 1f) : 0f;
                    var shoulderRaw = ToGd(GlobeLineSanity.InterpolateSurfacePoint(ToNum(before), ToNum(point), t, _flatMap));
                    if (TryOnOrderSurface(shoulderRaw, SurfaceLineRadius, FlatLineZ, out var shoulder))
                    {
                        EmitRibbonPair(shoulder, before, point, halfWidth, headStart, length, progress, false);
                        // Same center, wider shoulder: one head, no shaft sticking out of its tip.
                        EmitRibbonPair(shoulder, before, point, halfWidth * 2.4f, headStart, length, progress, true);
                        headStarted = true;
                    }
                }
                var width = headStarted ? halfWidth * 2.4f * MathF.Max(0f, (length - _ribbonDistances[i]) / headLength) : halfWidth;
                drewAny |= EmitRibbonPair(point, before, after, width, _ribbonDistances[i], length, progress, headStarted);
            }
        }
        return drewAny;
    }

    private bool EmitRibbonPair(Vector3 point, Vector3 before, Vector3 after, float halfWidth, float distance,
        float length, float progress, bool head)
    {
        if (!float.IsFinite(halfWidth) || halfWidth < 0f || !float.IsFinite(distance) || !float.IsFinite(length) || length <= 0f)
            return false;
        if (!GlobeLineSanity.TrySafeRibbonFrame(ToNum(point), ToNum(before), ToNum(after),
                _flatMap, flatPositiveZNormal: false, out _, out _, out var sideAxis))
        {
            _activeRibbonPrevPair = -1;
            return false;
        }
        var side = ToGd(sideAxis) * halfWidth;
        if (!TryOnOrderSurface(point + side, SurfaceLineRadius, FlatLineZ, out var left) ||
            !TryOnOrderSurface(point - side, SurfaceLineRadius, FlatLineZ, out var right) ||
            !GlobeLineSanity.IsValidEmittedEdge(ToNum(left), ToNum(right), _flatMap, _flatX))
        {
            _activeRibbonPrevPair = -1;
            return false;
        }

        var batch = _activeRibbonBatch;
        var slot = _activeRibbonSlot;
        var verts = batch.Verts[slot];
        if (_activeRibbonPrevPair >= 0 &&
            (!GlobeLineSanity.IsValidEmittedEdge(ToNum(verts[_activeRibbonPrevPair]), ToNum(left), _flatMap, _flatX) ||
             !GlobeLineSanity.IsValidEmittedEdge(ToNum(verts[_activeRibbonPrevPair + 1]), ToNum(right), _flatMap, _flatX)))
        {
            _activeRibbonPrevPair = -1;
        }

        var uvs = batch.Uvs[slot];
        var uv2s = batch.Uv2s[slot];
        var colors = batch.Colors[slot];
        var indices = batch.Indices[slot];
        var pairBase = verts.Count;
        var color = RibbonWhite with { A = progress };
        var uvX = distance / 0.07f;
        var uv2 = new Vector2(Math.Clamp(distance / length, 0f, 1f), head ? 1f : 0f);
        verts.Add(left);
        uvs.Add(new Vector2(uvX, 0f));
        uv2s.Add(uv2);
        colors.Add(color);
        verts.Add(right);
        uvs.Add(new Vector2(uvX, 1f));
        uv2s.Add(uv2);
        colors.Add(color);
        if (_activeRibbonPrevPair >= 0)
        {
            indices.Add(_activeRibbonPrevPair);
            indices.Add(_activeRibbonPrevPair + 1);
            indices.Add(pairBase);
            indices.Add(_activeRibbonPrevPair + 1);
            indices.Add(pairBase + 1);
            indices.Add(pairBase);
        }
        _activeRibbonPrevPair = pairBase;
        return indices.Count > 0;
    }
}
