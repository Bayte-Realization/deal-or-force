using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Batched active-battle map VFX: directional crimson assault arrows from attacking provinces
/// into active battle boundaries, plus subtle GPU-animated combat clash pulse/spark rings
/// directly underneath active battle bubbles.
/// Zero per-frame CPU allocations, zero extra textures on <c>globe_map.gdshader</c>, and cleanly
/// gated by <c>quality_tier</c> for Mobile/GLES3 performance.
/// </summary>
public sealed partial class GlobePresentation
{
    private ImmediateMesh _battleArrowMesh;
    private MeshInstance3D _battleArrowNode;
    private ShaderMaterial _battleArrowMaterial;

    private ImmediateMesh _battleClashMesh;
    private MeshInstance3D _battleClashNode;
    private ShaderMaterial _battleClashMaterial;

    private readonly List<(Vector3 Origin, Vector3 Target, float Intensity)> _battleArrowSpecs = new();
    private readonly List<(Vector3 Point, Vector3 Normal, float Intensity, bool Pocket)> _battleClashSpecs = new();

    private void CreateBattleVfx(Node3D world)
    {
        _battleArrowMesh = new ImmediateMesh();
        _battleArrowMaterial = new ShaderMaterial
        {
            Shader = new Shader { Code = BattleAssaultArrowShader },
            RenderPriority = 16
        };
        _battleArrowMaterial.SetShaderParameter("quality_tier", CurrentShaderQualityTier());
        _battleArrowNode = new MeshInstance3D
        {
            Name = "ActiveBattleAssaultArrows",
            Mesh = _battleArrowMesh,
            MaterialOverride = _battleArrowMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Visible = false
        };
        world.AddChild(_battleArrowNode);

        _battleClashMesh = new ImmediateMesh();
        _battleClashMaterial = new ShaderMaterial
        {
            Shader = new Shader { Code = BattleClashPulseShader },
            RenderPriority = 15
        };
        _battleClashMaterial.SetShaderParameter("quality_tier", CurrentShaderQualityTier());
        _battleClashNode = new MeshInstance3D
        {
            Name = "ActiveBattleClashVfx",
            Mesh = _battleClashMesh,
            MaterialOverride = _battleClashMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Visible = false
        };
        world.AddChild(_battleClashNode);
    }

    private void ApplyBattleVfxQualityTier(float tier)
    {
        _battleArrowMaterial?.SetShaderParameter("quality_tier", tier);
        _battleClashMaterial?.SetShaderParameter("quality_tier", tier);
        foreach (var marker in _battleMarkers.Values)
            if (IsInstanceValid(marker.Node))
                marker.Node.GetNodeOrNull<SurfaceBattleBadge>("SurfaceBadge")?.SetQualityTier(tier);
    }

    private void BeginBattleVfxFrame()
    {
        _battleArrowSpecs.Clear();
        _battleClashSpecs.Clear();
    }

    private void RecordBattleVfx(
        (float Latitude, float Longitude) battleLocation,
        int attackingProvinceId,
        float intensity,
        bool pocketAlert)
    {
        var battleNormal = SurfacePoint(battleLocation.Latitude, battleLocation.Longitude, 1f).Normalized();
        var battlePoint = _flatMap
            ? LocalFlatPoint(battleLocation.Latitude, battleLocation.Longitude, FlatLineZ + 0.002f)
            : LocalSurfacePoint(battleLocation.Latitude, battleLocation.Longitude, SurfaceLineRadius + 0.0008f);
        _battleClashSpecs.Add((battlePoint, battleNormal, Math.Clamp(intensity, 0.35f, 1.4f), pocketAlert));

        if (attackingProvinceId > 0 && _campaign?.ProvinceMap is { } map)
        {
            var originGeo = map.CenterOf(attackingProvinceId);
            var originPoint = _flatMap
                ? LocalFlatPoint(originGeo.Latitude, originGeo.Longitude, FlatLineZ + 0.0025f)
                : LocalSurfacePoint(originGeo.Latitude, originGeo.Longitude, SurfaceLineRadius + 0.001f);
            // Skip degenerate or date-line wrapping arrows on flat projection.
            var dist = originPoint.DistanceTo(battlePoint);
            if (dist >= 0.006f && (!_flatMap || dist < FlatMapWidth * 0.25f))
                _battleArrowSpecs.Add((originPoint, battlePoint, Math.Clamp(intensity, 0.5f, 1.35f)));
        }
    }

    private void CommitBattleVfxMeshes()
    {
        if (_battleArrowMesh is null || _battleClashMesh is null) return;
        _battleArrowMesh.ClearSurfaces();
        _battleClashMesh.ClearSurfaces();

        if (_battleArrowSpecs.Count > 0)
        {
            _battleArrowMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, _battleArrowMaterial);
            for (var i = 0; i < _battleArrowSpecs.Count; i++)
                EmitBattleAssaultArrow(_battleArrowSpecs[i].Origin, _battleArrowSpecs[i].Target, _battleArrowSpecs[i].Intensity);
            _battleArrowMesh.SurfaceEnd();
        }

        if (_battleClashSpecs.Count > 0)
        {
            _battleClashMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, _battleClashMaterial);
            for (var i = 0; i < _battleClashSpecs.Count; i++)
            {
                var spec = _battleClashSpecs[i];
                EmitBattleClashQuad(spec.Point, spec.Normal, spec.Intensity, spec.Pocket);
            }
            _battleClashMesh.SurfaceEnd();
        }
    }

    private void FinishBattleVfxVisibility()
    {
        var show = MilitaryMarkersEnabled && ViewDistance <= 14f;
        var tier = CurrentShaderQualityTier();
        if (IsInstanceValid(_battleArrowNode))
            _battleArrowNode.Visible = show && _battleArrowSpecs.Count > 0;
        if (IsInstanceValid(_battleClashNode))
            _battleClashNode.Visible = show && _battleClashSpecs.Count > 0 && tier < 1.5f;
    }

    private void EmitBattleAssaultArrow(Vector3 origin, Vector3 target, float intensity)
    {
        var dir = target - origin;
        var len = dir.Length();
        if (len < 0.004f) return;
        // Trim arrow start/end slightly so it bridges neatly from attacker formation to battle bubble edge.
        var trimStart = MathF.Min(len * 0.22f, 0.025f);
        var trimEnd = MathF.Min(len * 0.14f, 0.016f);
        if (len - trimStart - trimEnd < 0.005f) { trimStart = 0f; trimEnd = 0f; }

        var p0 = OnOrderSurface(origin.Lerp(target, trimStart / len));
        var p1 = OnOrderSurface(origin.Lerp(target, 1f - trimEnd / len));
        var seg = p1 - p0;
        var segLen = seg.Length();
        if (segLen < 0.003f) return;

        var tangent = seg / segLen;
        var midNormal = _flatMap ? Vector3.Forward : ((p0 + p1) * 0.5f).Normalized();
        var sideDir = midNormal.Cross(tangent).Normalized();
        var halfW = MathF.Min(_flatMap ? 0.0085f : 0.0072f, segLen * 0.24f);
        var headFrac = Math.Clamp(0.36f, 0.22f, 0.45f);
        var shoulder = OnOrderSurface(p0.Lerp(p1, 1f - headFrac));
        var col = new Color(1f, 0.24f, 0.14f, intensity);

        // Shaft quad (2 triangles)
        var s0L = OnOrderSurface(p0 + sideDir * halfW);
        var s0R = OnOrderSurface(p0 - sideDir * halfW);
        var s1L = OnOrderSurface(shoulder + sideDir * halfW);
        var s1R = OnOrderSurface(shoulder - sideDir * halfW);
        EmitArrowTri(s0L, s0R, s1L, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0f), col);
        EmitArrowTri(s0R, s1R, s1L, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), col);

        // Bold crimson arrowhead triangle
        var headHalfW = halfW * 2.15f;
        var hL = OnOrderSurface(shoulder + sideDir * headHalfW);
        var hR = OnOrderSurface(shoulder - sideDir * headHalfW);
        EmitArrowTri(hL, hR, p1, new Vector2(1.0f, 0f), new Vector2(1.0f, 1f), new Vector2(1.5f, 0.5f), col);
    }

    private void EmitArrowTri(Vector3 a, Vector3 b, Vector3 c, Vector2 uvA, Vector2 uvB, Vector2 uvC, Color col)
    {
        _battleArrowMesh.SurfaceSetColor(col);
        _battleArrowMesh.SurfaceSetUV(uvA);
        _battleArrowMesh.SurfaceAddVertex(a);
        _battleArrowMesh.SurfaceSetColor(col);
        _battleArrowMesh.SurfaceSetUV(uvB);
        _battleArrowMesh.SurfaceAddVertex(b);
        _battleArrowMesh.SurfaceSetColor(col);
        _battleArrowMesh.SurfaceSetUV(uvC);
        _battleArrowMesh.SurfaceAddVertex(c);
    }

    private void EmitBattleClashQuad(Vector3 center, Vector3 normal, float intensity, bool pocket)
    {
        Vector3 right, up;
        if (_flatMap)
        {
            right = Vector3.Right;
            up = Vector3.Up;
        }
        else
        {
            var refUp = MathF.Abs(normal.Y) > 0.95f ? Vector3.Right : Vector3.Up;
            right = refUp.Cross(normal).Normalized();
            up = normal.Cross(right).Normalized();
        }
        var radius = _flatMap ? 0.055f : 0.044f;
        var p00 = center - right * radius - up * radius;
        var p10 = center + right * radius - up * radius;
        var p01 = center - right * radius + up * radius;
        var p11 = center + right * radius + up * radius;
        var col = pocket ? new Color(1.0f, 0.16f, 0.12f, intensity) : new Color(1.0f, 0.42f, 0.16f, intensity);

        _battleClashMesh.SurfaceSetColor(col); _battleClashMesh.SurfaceSetUV(new Vector2(0f, 0f)); _battleClashMesh.SurfaceAddVertex(p00);
        _battleClashMesh.SurfaceSetColor(col); _battleClashMesh.SurfaceSetUV(new Vector2(1f, 0f)); _battleClashMesh.SurfaceAddVertex(p10);
        _battleClashMesh.SurfaceSetColor(col); _battleClashMesh.SurfaceSetUV(new Vector2(0f, 1f)); _battleClashMesh.SurfaceAddVertex(p01);

        _battleClashMesh.SurfaceSetColor(col); _battleClashMesh.SurfaceSetUV(new Vector2(1f, 0f)); _battleClashMesh.SurfaceAddVertex(p10);
        _battleClashMesh.SurfaceSetColor(col); _battleClashMesh.SurfaceSetUV(new Vector2(1f, 1f)); _battleClashMesh.SurfaceAddVertex(p11);
        _battleClashMesh.SurfaceSetColor(col); _battleClashMesh.SurfaceSetUV(new Vector2(0f, 1f)); _battleClashMesh.SurfaceAddVertex(p01);
    }

    private const string BattleAssaultArrowShader = """
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
uniform float quality_tier = 0.0;
void fragment() {
    float edge = min(UV.y, 1.0 - UV.y);
    float aa = max(fwidth(UV.y), 0.02);
    float mask = smoothstep(0.0, aa, edge);
    float border = smoothstep(0.11, 0.11 + aa, edge);
    float is_head = step(0.98, UV.x);
    float march = quality_tier < 1.5
        ? smoothstep(0.15, 0.45, fract(UV.x * 2.4 - abs(UV.y - 0.5) * 0.9 - TIME * 1.45)) * (1.0 - is_head)
        : 0.0;
    vec3 fill = mix(COLOR.rgb, vec3(1.0, 0.74, 0.32), march * 0.52 + is_head * 0.24);
    ALBEDO = mix(vec3(0.08, 0.02, 0.02), fill, border);
    ALPHA = mask * clamp(0.76 + is_head * 0.20, 0.0, 0.96);
}
""";

    private const string BattleClashPulseShader = """
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;
uniform float quality_tier = 0.0;
void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float r = length(p);
    if (r > 1.0) discard;
    float angle = atan(p.y, p.x);
    float phase = fract(TIME * 0.65);
    float wave_ring = 1.0 - smoothstep(0.0, 0.16, abs(r - mix(0.32, 0.92, phase)));
    wave_ring *= (1.0 - phase) * 0.55;
    float core_glow = pow(1.0 - r, 2.4) * (0.36 + 0.16 * sin(TIME * 5.2));
    // Subtle directional clash spark rays on DesktopHigh only (zero textures, mobile-safe).
    float sparks = 0.0;
    if (quality_tier < 0.5) {
        float ray = pow(max(sin(angle * 7.0 + TIME * 2.6) * sin(angle * 5.0 - TIME * 3.9), 0.0), 3.0);
        sparks = ray * smoothstep(0.25, 0.58, r) * (1.0 - smoothstep(0.58, 0.94, r)) * 0.48;
    }
    float energy = (core_glow + wave_ring + sparks) * clamp(COLOR.a, 0.35, 1.35);
    vec3 col = mix(COLOR.rgb, vec3(1.0, 0.88, 0.54), clamp(sparks * 1.4 + core_glow * 0.6, 0.0, 1.0));
    ALBEDO = col * energy;
    ALPHA = clamp(energy, 0.0, 0.85);
}
""";
}
