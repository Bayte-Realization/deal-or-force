using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Capital marks (one thin ink ring around the capital's star) + 1-draw-call city lights on the
/// night side of the globe, scaled by real state population and GDP.
/// Strictly presentation-only: zero simulation mutation, zero texture units added to globe shader.
/// Ink, not light (23 November 2026): the brass halo with its soft glow and shimmer is now a plain
/// stroke, and the city lights no longer glow on the day side or on the flat map.
/// </summary>
public sealed partial class GlobePresentation
{
    private const int UrbanGlowMaxClusters = 240;

    private static QuadMesh _capitalHaloQuad;
    private static ShaderMaterial _capitalHaloMaterial;

    private MultiMeshInstance3D _urbanGlowNode;
    private ShaderMaterial _urbanGlowMaterial;
    private int _urbanGlowRevision = int.MinValue;
    private int _urbanGlowCityCount = -1;
    private bool _urbanGlowFlat;
    private float _urbanGlowQualityTier = -1f;
    private readonly List<(float Lat, float Lon, float Intensity, float Radius)> _urbanGlowAnchors = new(UrbanGlowMaxClusters);

    private const string CapitalHaloShader = """
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;
uniform vec3 ink_color : source_color = vec3(0.10, 0.09, 0.08);
uniform float quality_tier = 0.0;
void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float r = length(p);
    if (r > 1.0) discard;
    float aa = max(fwidth(r), 0.01);
    // One plain stroke: no soft glow inside it, no shimmer.
    float ring = 1.0 - smoothstep(0.05, 0.05 + aa, abs(r - 0.80));
    if (ring < 0.01) discard;
    ALBEDO = ink_color;
    ALPHA = ring * 0.85;
}
""";

    private const string UrbanGlowShader = """
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled;
uniform vec3 sun_direction = vec3(0.35, 0.55, 0.75);
uniform float flat_map = 0.0;
uniform float dusk_boost = 1.0;
uniform float paper_fade = 1.0;
varying vec3 world_norm;
void vertex() {
    world_norm = normalize((MODEL_MATRIX * vec4(0.0, 0.0, 1.0, 0.0)).xyz);
}
void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float r = length(p);
    if (r > 1.0) discard;
    float core = exp(-r * r * 5.6);
    float halo = pow(max(1.0 - r, 0.0), 2.4) * 0.45;
    float sun_dot = dot(normalize(world_norm), normalize(sun_direction));
    // City lights belong to the night: nothing on the day side or on the flat map (no glow).
    float twilight = flat_map > 0.5 ? 0.0 : 1.0 - smoothstep(-0.30, 0.05, sun_dot);
    float energy = (core * 0.72 + halo) * COLOR.a * twilight * dusk_boost * paper_fade;
    vec3 warm = mix(vec3(0.96, 0.62, 0.26), vec3(1.00, 0.88, 0.58), core);
    ALBEDO = warm * energy;
    ALPHA = clamp(energy, 0.0, 0.92);
}
""";

    private static ShaderMaterial EnsureCapitalHaloMaterial()
    {
        if (_capitalHaloMaterial is not null) return _capitalHaloMaterial;
        _capitalHaloQuad ??= new QuadMesh { Size = Vector2.One };
        _capitalHaloMaterial = new ShaderMaterial
        {
            Shader = new Shader { Code = CapitalHaloShader },
            RenderPriority = 6
        };
        return _capitalHaloMaterial;
    }

    private void ApplyCapitalAndUrbanQualityTier(float tier)
    {
        _capitalHaloMaterial?.SetShaderParameter("quality_tier", tier);
        if (!Mathf.IsEqualApprox(_urbanGlowQualityTier, tier))
        {
            _urbanGlowQualityTier = tier;
            _urbanGlowRevision = int.MinValue;
        }
    }

    /// <summary>
    /// A capital is a mark on the map, not a sign facing the viewer. Geographic east/north
    /// are its permanent axes; only a projection change replaces that frame with flat axes.
    /// Attaches one thin ink ring, identical on the globe and the flat atlas.
    /// </summary>
    private Vector3 CapitalMarkerPosition(float latitude, float longitude)
    {
        var reliefActive = _reliefDisplacement > 0.0001f;
        var reliefBias = reliefActive ? 0.004f : 0f;
        var footprint = reliefActive ? 0.045f : 0f;
        return _flatMap
            ? LocalFlatLabelPoint(latitude, longitude, FlatMarkerZ + reliefBias, footprint)
            : LocalSurfaceLabelPoint(latitude, longitude, SurfaceMarkerRadius + reliefBias, footprint);
    }

    private void PlaceCapitalMarker(Node3D node, Vector3 normal)
    {
        var geographic = GeographicFromNormal(normal);
        node.Position = CapitalMarkerPosition(geographic.Latitude, geographic.Longitude);
        OrientSurfaceCounter(node, normal);
        if (node is Sprite3D star)
        {
            EnsureCapitalHaloChild(star);
            SizeCapitalMarker(star);
        }
    }

    /// <summary>The halo quad's size against the star, and the ring's outer edge in the quad (0.80 + half the stroke).</summary>
    private const float CapitalHaloScale = 1.85f;
    private const float CapitalRingOuter = 0.86f;

    /// <summary>
    /// How wide the capital's ink ring is on the screen, at the star's size cap: what a name beside
    /// the star has to clear. Far out the star is drawn smaller than the cap and the name stands a
    /// few pixels wider than it needs to, never on the ring.
    /// </summary>
    private float CapitalRingAcrossPixels()
    {
        var starPixels = _flatMap
            ? Math.Clamp(Ui.Metric("map.flat_capital_pixels", 14f), 8f, 24f)
            : Math.Clamp(Ui.Metric("map.globe_capital_pixels", 18f), 8f, 30f);
        return starPixels * CapitalHaloScale * CapitalRingOuter;
    }

    private static void EnsureCapitalHaloChild(Sprite3D star)
    {
        if (star.GetNodeOrNull<MeshInstance3D>("CapitalHalo") is not null) return;
        var halo = new MeshInstance3D
        {
            Name = "CapitalHalo",
            Mesh = _capitalHaloQuad ??= new QuadMesh { Size = Vector2.One },
            MaterialOverride = EnsureCapitalHaloMaterial(),
            Position = new Vector3(0f, 0f, -0.0004f),
            Scale = Vector3.One * CapitalHaloScale,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled
        };
        star.AddChild(halo);
    }

    private void SizeCapitalMarker(Sprite3D star)
    {
        if (!IsInstanceValid(_camera) || star.Texture is null) return;
        star.Billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
        star.FixedSize = false;
        // One unit across the texture makes Scale the physical width of the whole mark.
        // Unlike FixedSize's shader compensation, this keeps the actual quad tangent.
        star.PixelSize = 1f / Math.Max(1, star.Texture.GetWidth());
        float width;
        if (_flatMap)
        {
            var unitsPerPixel = _camera.Size / Math.Max(1f, _camera.GetViewport().GetVisibleRect().Size.Y);
            width = unitsPerPixel * Math.Clamp(Ui.Metric("map.flat_capital_pixels", 14f), 8f, 24f);
        }
        else
        {
            var depth = Math.Max(.001f, -_camera.ToLocal(star.GlobalPosition).Z);
            var unitsPerPixel = _camera.ProjectPosition(Vector2.Zero, depth)
                .DistanceTo(_camera.ProjectPosition(Vector2.Down, depth));
            var pixels = Math.Clamp(Ui.Metric("map.globe_capital_pixels", 18f), 8f, 30f);
            var ground = Math.Clamp(Ui.Metric("map.globe_capital_world_width", .026f), .008f, .08f);
            width = Math.Min(ground, pixels * unitsPerPixel);
        }
        star.Scale = Vector3.One * width;
    }

    private void UpdateCapitalMarkerScale(bool force = false)
    {
        foreach (var (node, _) in _capitalMarkers)
            if (IsInstanceValid(node) && (force || node.Visible) && node is Sprite3D star) SizeCapitalMarker(star);
    }

    /// <summary>
    /// 1-draw-call city lights on the night side, scaled by real state population and GDP.
    /// Zero invented settlements, zero per-frame allocations, cleanly muted in paper mode & MobileLow.
    /// </summary>
    private ShaderMaterial _cachedUrbanGlowMaterial;
    private bool? _cachedUrbanGlowVisible;
    private Vector3 _cachedUrbanSunDir = new(float.NaN, float.NaN, float.NaN);
    private float _cachedUrbanFlatMap = float.NaN;
    private float _cachedUrbanPaperFade = float.NaN;
    private float _cachedUrbanDuskBoost = float.NaN;

    private void UpdateUrbanGlowClusters()
    {
        if (_campaign is null || !_mapApplied || !IsInstanceValid(_skyWorld)) return;
        var tier = CurrentShaderQualityTier();
        // City lights only where there is a night: on the globe with the day-night terminator on.
        var allowed = !_mapLookPaper && tier < 1.5f && Simulation.WorldLayers.On("cities") && !_flatMap && DayNightEnabled;
        if (!allowed)
        {
            if (IsInstanceValid(_urbanGlowNode) && _cachedUrbanGlowVisible != false)
            {
                _cachedUrbanGlowVisible = false;
                _urbanGlowNode.Visible = false;
            }
            return;
        }

        if (_urbanGlowNode is null || !IsInstanceValid(_urbanGlowNode))
        {
            _urbanGlowMaterial = new ShaderMaterial
            {
                Shader = new Shader { Code = UrbanGlowShader },
                RenderPriority = 4
            };
            _cachedUrbanGlowMaterial = null;
            _cachedUrbanGlowVisible = null;
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = new QuadMesh { Size = Vector2.One },
                InstanceCount = UrbanGlowMaxClusters,
                VisibleInstanceCount = 0
            };
            _urbanGlowNode = new MultiMeshInstance3D
            {
                Name = "UrbanDuskGlow",
                Multimesh = mm,
                MaterialOverride = _urbanGlowMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                GIMode = GeometryInstance3D.GIModeEnum.Disabled
            };
            _skyWorld.AddChild(_urbanGlowNode);
        }

        if (_cachedUrbanGlowVisible != true)
        {
            _cachedUrbanGlowVisible = true;
            _urbanGlowNode.Visible = true;
        }
        var matChanged = !ReferenceEquals(_cachedUrbanGlowMaterial, _urbanGlowMaterial);
        if (matChanged)
        {
            _cachedUrbanGlowMaterial = _urbanGlowMaterial;
            _cachedUrbanSunDir = new Vector3(float.NaN, float.NaN, float.NaN);
            _cachedUrbanFlatMap = float.NaN;
            _cachedUrbanPaperFade = float.NaN;
            _cachedUrbanDuskBoost = float.NaN;
        }
        if (IsInstanceValid(_daySun))
        {
            var sunDir = _daySun.GlobalBasis.Z;
            if (!sunDir.IsEqualApprox(_cachedUrbanSunDir))
            {
                _cachedUrbanSunDir = sunDir;
                _urbanGlowMaterial.SetShaderParameter("sun_direction", sunDir);
            }
        }
        var flatVal = _flatMap ? 1f : 0f;
        if (flatVal != _cachedUrbanFlatMap)
        {
            _cachedUrbanFlatMap = flatVal;
            _urbanGlowMaterial.SetShaderParameter("flat_map", flatVal);
        }
        var paperFade = 1f - Math.Clamp(_cachedPaperMix, 0f, 1f);
        if (paperFade != _cachedUrbanPaperFade && (matChanged || MathF.Abs(paperFade - _cachedUrbanPaperFade) > 0.001f || paperFade <= 0f || paperFade >= 1f))
        {
            _cachedUrbanPaperFade = paperFade;
            _urbanGlowMaterial.SetShaderParameter("paper_fade", paperFade);
        }
        const float duskBoost = 1.0f;
        if (duskBoost != _cachedUrbanDuskBoost)
        {
            _cachedUrbanDuskBoost = duskBoost;
            _urbanGlowMaterial.SetShaderParameter("dusk_boost", duskBoost);
        }

        var rev = _campaign.MapRevision;
        var cityCount = Math.Min(_cities.Count, _cityAnchors.Length);
        if (rev == _urbanGlowRevision && cityCount == _urbanGlowCityCount
            && _urbanGlowFlat == _flatMap && Mathf.IsEqualApprox(_urbanGlowQualityTier, tier))
            return;

        _urbanGlowRevision = rev;
        _urbanGlowCityCount = cityCount;
        _urbanGlowFlat = _flatMap;
        _urbanGlowQualityTier = tier;
        RebuildUrbanGlowInstances(tier);
    }

    private void RebuildUrbanGlowInstances(float tier)
    {
        if (_urbanGlowNode?.Multimesh is not { } mm || _campaign?.ProvinceMap is null) return;
        EnsureStateIndex();
        var limit = tier > 0.5f ? 110 : UrbanGlowMaxClusters;
        _urbanGlowAnchors.Clear();

        var topology = _campaign.ProvinceMap;
        var countCities = Math.Min(_cities.Count, _cityAnchors.Length);
        if (countCities > 0)
        {
            // Place warm twilight glow clusters on real metropolises scaled by live state population & urbanization.
            for (var i = 0; i < countCities; i++)
            {
                var city = _cities[i];
                var prov = _cityAnchors[i].ProvinceId;
                if (prov <= 0 || prov >= topology.ProvinceToState.Length) continue;
                var stateIdx = topology.ProvinceToState[prov];
                if (!_stateByMapIndex.TryGetValue(stateIdx, out var state)) continue;
                var urbanPop = CityLife.Urban(state);
                if (urbanPop < 400_000) continue;
                if (TerrainGone(city.Latitude, city.Longitude)) continue;

                var urbanNorm = Math.Clamp(MathF.Log10(MathF.Max(1f, urbanPop / 350_000f)) / 2.2f, 0.15f, 1.0f);
                var density = Math.Clamp(state.Urbanization, 0.15f, 1.0f);
                var isCap = i < _cityIsCapital.Length && _cityIsCapital[i];
                var intensity = Math.Clamp(0.24f + 0.44f * urbanNorm + 0.24f * density + (isCap ? 0.16f : 0f), 0.22f, 1.0f);
                var radius = Math.Clamp(0.018f + 0.038f * urbanNorm + (isCap ? 0.008f : 0f), 0.018f, 0.064f);
                _urbanGlowAnchors.Add((city.Latitude, city.Longitude, intensity, radius));
            }
        }
        else
        {
            // Fallback before cities finish loading: use real national capitals scaled by nation population.
            var nations = _campaign.Nations;
            for (var i = 0; i < nations.Count; i++)
            {
                var n = nations[i];
                if (n.Population < 600_000 || !CapitalCore.HasSeat(_campaign, n)) continue;
                var lat = CapitalCore.EffectiveLatitude(_campaign, n);
                var lon = CapitalCore.EffectiveLongitude(_campaign, n);
                if (TerrainGone(lat, lon)) continue;
                var popNorm = Math.Clamp(MathF.Log10(MathF.Max(1f, n.Population / 500_000f)) / 2.5f, 0.20f, 1.0f);
                _urbanGlowAnchors.Add((lat, lon, Math.Clamp(0.32f + 0.62f * popNorm, 0.30f, 1.0f), 0.024f + 0.034f * popNorm));
            }
        }

        _urbanGlowAnchors.Sort((a, b) => b.Intensity.CompareTo(a.Intensity));
        var count = Math.Min(limit, _urbanGlowAnchors.Count);
        for (var i = 0; i < count; i++)
        {
            var anchor = _urbanGlowAnchors[i];
            Vector3 pos, right, up, normal;
            if (_flatMap)
            {
                pos = LocalFlatPoint(anchor.Lat, anchor.Lon, FlatLineZ * 0.45f);
                right = Vector3.Right;
                up = Vector3.Up;
                normal = Vector3.Back;
            }
            else
            {
                normal = SurfacePoint(anchor.Lat, anchor.Lon, 1f).Normalized();
                pos = LocalSurfacePoint(anchor.Lat, anchor.Lon, GlobeRadius * 1.0007f);
                right = normal.Cross(Vector3.Up);
                if (right.LengthSquared() < 1e-6f) right = Vector3.Right;
                right = right.Normalized();
                up = right.Cross(normal).Normalized();
            }
            var scale = anchor.Radius * (_flatMap ? 1.25f : 1.0f);
            mm.SetInstanceTransform(i, new Transform3D(new Basis(right * scale, up * scale, normal * scale), pos));
            mm.SetInstanceColor(i, new Color(1f, 0.85f, 0.55f, anchor.Intensity));
        }
        mm.VisibleInstanceCount = count;
    }
}
