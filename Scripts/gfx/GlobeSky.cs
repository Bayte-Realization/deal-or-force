using System;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private Node3D _skyWorld;
    private DirectionalLight3D _daySun;
    private WorldEnvironment _skyEnvironment;
    private MeshInstance3D _cloudLayer;
    private MeshInstance3D _interiorLayer;
    private double _cloudWind;
    private float _skyAngle;
    private bool? _cachedInteriorVisible;
    private Vector3 _cachedDaySunRotation = new(float.NaN, float.NaN, float.NaN);
    private Color _cachedDaySunColor = new(float.NaN, float.NaN, float.NaN, float.NaN);
    private float _cachedAmbientEnergy = float.NaN;
    private bool? _cachedSkyCloudVisible;
    private ShaderMaterial _cachedSkyCloudMaterial;
    private Vector3 _cachedSkyCloudSunDir = new(float.NaN, float.NaN, float.NaN);
    // Base-game lighting stays fixed. The loaded crossover mod opts into these effects.
    public bool DayNightEnabled { get; set; }
    public bool CloudsEnabled { get; set; }

    public void SetCrossoverAtmosphere(bool enabled)
    {
        DayNightEnabled = CloudsEnabled = enabled;
        WakeGlobeRendering();
        if (enabled)
        {
            if (_cloudLayer is null && IsInstanceValid(_skyWorld)) CreateCloudLayer(_skyWorld);
            _skyAngle = ((_campaign?.ElapsedHours ?? 6) % 24) / 24f * Mathf.Tau;
            UpdateSky(0);
            return;
        }
        if (IsInstanceValid(_cloudLayer)) { _cloudLayer.Visible = false; _cloudLayer.QueueFree(); }
        if (IsInstanceValid(_interiorLayer)) { _interiorLayer.Visible = false; _interiorLayer.QueueFree(); }
        _cloudLayer = _interiorLayer = null;
        _cachedInteriorVisible = null;
        _cachedDaySunRotation = new Vector3(float.NaN, float.NaN, float.NaN);
        _cachedDaySunColor = new Color(float.NaN, float.NaN, float.NaN, float.NaN);
        _cachedAmbientEnergy = float.NaN;
        _cachedSkyCloudVisible = null;
        _cachedSkyCloudMaterial = null;
        _cachedSkyCloudSunDir = new Vector3(float.NaN, float.NaN, float.NaN);
        if (IsInstanceValid(_daySun)) _daySun.RotationDegrees = new Vector3(-32f, -38f, 0f);
        if (IsInstanceValid(_skyEnvironment)) _skyEnvironment.Environment.AmbientLightEnergy = .72f;
    }

    private void CreateCloudLayer(Node3D world)
    {
        var shader = new Shader { Code = @"shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, unshaded;
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
varying vec3 sphere_normal;
vec3 gradient3(vec3 cell) {
    // Integer hashing avoids the precision discontinuities of a large sin()*constant hash.
    uint h = uint(int(cell.x)) * 1597334677u ^ uint(int(cell.y)) * 3812015801u ^ uint(int(cell.z)) * 2798796415u;
    h = (h ^ (h >> 16u)) * 2246822519u; h = h ^ (h >> 13u);
    return normalize(vec3(float(h & 1023u), float((h >> 10u) & 1023u),
        float((h >> 20u) & 1023u)) / 511.5 - 1.0);
}
float gradient_noise(vec3 p) {
    vec3 i = floor(p), f = fract(p);
    vec3 w = f*f*f*(f*(f*6.0-15.0)+10.0);
    return 0.5 + 0.8 * mix(
        mix(mix(dot(gradient3(i),f), dot(gradient3(i+vec3(1,0,0)),f-vec3(1,0,0)),w.x),
            mix(dot(gradient3(i+vec3(0,1,0)),f-vec3(0,1,0)),dot(gradient3(i+vec3(1,1,0)),f-vec3(1,1,0)),w.x),w.y),
        mix(mix(dot(gradient3(i+vec3(0,0,1)),f-vec3(0,0,1)),dot(gradient3(i+vec3(1,0,1)),f-vec3(1,0,1)),w.x),
            mix(dot(gradient3(i+vec3(0,1,1)),f-vec3(0,1,1)),dot(gradient3(i+vec3(1,1,1)),f-vec3(1,1,1)),w.x),w.y),w.z);
}
void vertex() { sphere_normal = normalize(VERTEX); }
void fragment() {
    vec3 n = normalize(sphere_normal);
    vec3 p = n * 7.0;
    float density = gradient_noise(p)*0.55 + gradient_noise(p*2.1+7.0)*0.28
        + gradient_noise(p*4.3)*0.12 + gradient_noise(p*8.7+13.0)*0.05;
    float aa = max(fwidth(density)*0.65,0.004);
    float limb = smoothstep(0.0,0.16,abs(dot(normalize(NORMAL),normalize(VIEW))));
    ALPHA = smoothstep(0.52-aa,0.68+aa,density)*0.30*limb;
    vec3 world_normal = normalize((MODEL_MATRIX * vec4(n,0.0)).xyz);
    float day = smoothstep(-0.18,0.35,dot(world_normal,sun_direction));
    ALBEDO = mix(vec3(0.15,0.19,0.28),vec3(0.94,0.96,1.0),day);
}" };
        _cloudLayer = new MeshInstance3D
        {
            Name = "MovingClouds",
            Mesh = new SphereMesh { Radius = GlobeRadius * 1.018f, Height = GlobeRadius * 2.036f,
                RadialSegments = 256, Rings = 128 },
            MaterialOverride = new ShaderMaterial { Shader = shader },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        world.AddChild(_cloudLayer);
        // A visible interior behind removed land. The BTA side supplies the walkable voxel cavern.
        var interior = new MeshInstance3D
        {
            Name = "PlanetInterior",
            Mesh = new SphereMesh { Radius = GlobeRadius * .83f, Height = GlobeRadius * 1.66f,
                RadialSegments = 64, Rings = 32 },
            MaterialOverride = new StandardMaterial3D
            {
                CullMode = BaseMaterial3D.CullModeEnum.Front,
                AlbedoColor = new Color(.12f, .10f, .085f),
                EmissionEnabled = true, Emission = new Color(.025f, .018f, .01f),
                Roughness = 1f
            }
        };
        world.AddChild(interior);
        _interiorLayer = interior;
    }

    private void UpdateSky(double delta)
    {
        UpdateAtmosphereRim();
        UpdateUrbanGlowClusters();
        if (!IsInstanceValid(_daySun) || (!DayNightEnabled && _cloudLayer is null))
        {
            SyncAtlasSun();
            return;
        }
        var snap = _campaign?.RenderSnapshot;
        var hasSnap = snap is { Version: > 0 };
        var target = ((hasSnap ? snap.ElapsedHours : _campaign?.ElapsedHours ?? 6) % 24) / 24f * Mathf.Tau;
        _skyAngle = Mathf.LerpAngle(_skyAngle, target, 1f - MathF.Exp(-(float)delta * 3f));
        var wantInterior = !_flatMap;
        if (IsInstanceValid(_interiorLayer) && _cachedInteriorVisible != wantInterior)
        {
            _cachedInteriorVisible = wantInterior;
            _interiorLayer.Visible = wantInterior;
        }
        var dayNightActive = DayNightEnabled && !_flatMap;
        var desiredRot = dayNightActive
            ? new Vector3(-.41f, _skyAngle, 0)
            : new Vector3(Mathf.DegToRad(-32f), Mathf.DegToRad(-38f), 0);
        if (!desiredRot.IsEqualApprox(_cachedDaySunRotation))
        {
            _cachedDaySunRotation = desiredRot;
            _daySun.Rotation = desiredRot;
        }
        // Subtle warm golden-hour tint when day/night sweeps across the globe, keeping night readable for orders.
        Color desiredColor;
        if (dayNightActive)
        {
            var sunElev = Math.Clamp(MathF.Abs(MathF.Cos(_skyAngle)), 0f, 1f);
            var duskWarmth = 1f - sunElev;
            desiredColor = new Color(
                Mathf.Lerp(0.99f, 1.00f, duskWarmth),
                Mathf.Lerp(0.97f, 0.90f, duskWarmth),
                Mathf.Lerp(0.94f, 0.80f, duskWarmth));
        }
        else
        {
            desiredColor = new Color(1.0f, 0.985f, 0.955f);
        }
        if (desiredColor != _cachedDaySunColor)
        {
            _cachedDaySunColor = desiredColor;
            _daySun.LightColor = desiredColor;
        }
        SyncAtlasSun();
        // Night remains readable enough to issue strategic orders.
        var desiredAmbient = dayNightActive ? .30f : .72f;
        if (desiredAmbient != _cachedAmbientEnergy)
        {
            _cachedAmbientEnergy = desiredAmbient;
            _skyEnvironment.Environment.AmbientLightEnergy = desiredAmbient;
        }
        if (_cloudLayer is null) return;
        var isPaused = hasSnap ? snap.IsPaused : _campaign?.IsPaused ?? false;
        if (_campaign is null || !isPaused) _cloudWind += delta * .007;
        _cloudLayer.Rotation = new Vector3(0, (float)_cloudWind, .12f);
        var cloudsVis = CloudsEnabled && !_flatMap && CurrentShaderQualityTier() < 1.5f && Simulation.WorldLayers.On("sky_clouds");
        if (_cachedSkyCloudVisible != cloudsVis)
        {
            _cachedSkyCloudVisible = cloudsVis;
            _cloudLayer.Visible = cloudsVis;
        }
        if (!cloudsVis) return;
        var lightDirection = _daySun.GlobalBasis.Z;
        if (_cloudLayer.MaterialOverride is ShaderMaterial cloudMat &&
            (!ReferenceEquals(_cachedSkyCloudMaterial, cloudMat) || !lightDirection.IsEqualApprox(_cachedSkyCloudSunDir)))
        {
            _cachedSkyCloudMaterial = cloudMat;
            _cachedSkyCloudSunDir = lightDirection;
            cloudMat.SetShaderParameter("sun_direction", lightDirection);
        }
    }

    private bool TerrainGone(float latitude, float longitude)
    {
        if (_campaign is null || _provinceTopology is null || _campaign.DestroyedProvinces.Count == 0) return false;
        var x = Math.Clamp((int)((longitude + 180f) / 360f * _provinceTopology.Width), 0, _provinceTopology.Width - 1);
        var y = Math.Clamp((int)((90f - latitude) / 180f * _provinceTopology.Height), 0, _provinceTopology.Height - 1);
        return _campaign.DestroyedProvinces.Contains(_provinceTopology.CellToProvince[y * _provinceTopology.Width + x]);
    }
}
