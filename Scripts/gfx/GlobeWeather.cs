using Godot;

namespace DealOrForce
{

/// <summary>
/// Weather over the map: clouds from far away, and the fog of war as real fog up close.
///
/// The two belong to the two cameras the game has. From the strategic distance the world is
/// a planet, and a planet has weather - bands of cloud along the equator and the storm
/// tracks, clear skies over the subtropical deserts, all of it drifting west in the tropics
/// and east in the middle latitudes. Brought down to the tactical distance the clouds part,
/// because that is where a player reads borders and moves divisions; and there the ground
/// the player cannot see stops being merely greyed and fills with a drifting mist, thick in
/// the middle of what is unknown and thinning at its edge. The grey wash (UpdateFogTint)
/// still says which provinces are hidden; the mist is what it looks like.
///
/// Both are shells over the ground with their own materials, drawn below every marker and
/// label, and both are built for the flat map as well as the globe. Nothing here hides or
/// reveals anything: the vision model decides that, and the mist only draws what it says.
/// </summary>
public sealed partial class GlobePresentation
{
    private MeshInstance3D _cloudDeck;
    private MeshInstance3D _cloudShade;
    private MeshInstance3D _mistShell;
    private MeshInstance3D _airGlow;
    private const float AirReach = 1.045f;
    private ShaderMaterial _cloudMaterial;
    private ShaderMaterial _cloudShadeMaterial;
    private ShaderMaterial _mistMaterial;
    private DirectionalLight3D _weatherSun;
    private Node3D _weatherWorld;
    private double _weatherClock;
    private double _weatherMobileAccum;
    private float _weatherQualityTier;
    private bool _weatherEnabled = true;
    private bool _cloudsShown = !(Platform.PlatformProfile.Current?.IsTouchPrimary == true);
    private bool _mistShown = true;
    private ShaderMaterial _wCachedCloudMat;
    private ShaderMaterial _wCachedCloudShadeMat;
    private float _wCloudRealistic = float.NaN;
    private float _wCloudPresence = float.NaN;
    private float _wCloudOpacity = float.NaN;
    private float _wCloudEdgeSoftness = float.NaN;
    private float _wCloudDetailStrength = float.NaN;
    private float _wCloudWind = float.NaN;
    private Vector3 _wCloudSunDir = new(float.NaN, float.NaN, float.NaN);
    private float _wCloudFlatMap = float.NaN;
    private ShaderMaterial _wCachedMistMat;
    private float _wMistPresence = float.NaN;
    private float _wMistOpacity = float.NaN;
    private float _wMistWind = float.NaN;
    private object _wMistPackedIds;
    private object _wMistPackedLookups;
    private object _wMistVision;
    private ShaderMaterial _wMistGlobeMat;
    private bool _wMistMapApplied;
    private Simulation.Campaign _wMistCampaign;
    private GodotObject _wMistProvinceIdsObj;
    private GodotObject _wMistPoliticalMapObj;

    /// <summary>Visual clouds only; a legacy WeatherEnabled master never overwrites this preference.</summary>
    public bool CloudsShown
    {
        get => _cloudsShown;
        set { if (_cloudsShown == value) return; _cloudsShown = value; WakeGlobeRendering(); UpdateWeather(0); }
    }

    /// <summary>The decorative mist only; province vision and the hidden-ground tint are unchanged.</summary>
    public bool MistShown
    {
        get => _mistShown;
        set { if (_mistShown == value) return; _mistShown = value; WakeGlobeRendering(); UpdateWeather(0); }
    }

    /// <summary>How high the camera is, in the units the weather fades by (MapAmbience listens to it too).</summary>
    public float ViewHeight => ViewDistance;

    /// <summary>The point of the world under the middle of the view.</summary>
    public (float Latitude, float Longitude) ViewCentre => _flatMap
        ? (_flatY * 18f, Mathf.Wrap(_flatX * 18f, -180f, 180f))
        : (-Mathf.RadToDeg(_pitch), Mathf.Wrap(Mathf.RadToDeg(_yaw) - 180f, -180f, 180f));

    /// <summary>Legacy master gate for both layers, without changing their individual saved preferences.</summary>
    public bool WeatherEnabled
    {
        get => _weatherEnabled;
        set { if (_weatherEnabled == value) return; _weatherEnabled = value; UpdateWeather(0); }
    }

    private void CreateWeather(Node3D world)
    {
        _weatherWorld = world;
        foreach (var child in world.GetChildren())
            if (child is DirectionalLight3D light) { _weatherSun = light; break; }

        var cloud = new Shader { Code = CloudShader };
        _cloudMaterial = new ShaderMaterial { Shader = cloud, RenderPriority = -20 };
        _cloudShadeMaterial = new ShaderMaterial { Shader = cloud, RenderPriority = -22 };
        _cloudShadeMaterial.SetShaderParameter("shade_pass", 1f);
        var tier = CurrentShaderQualityTier();
        _weatherQualityTier = tier;
        _cloudMaterial.SetShaderParameter("quality_tier", tier);
        _cloudShadeMaterial.SetShaderParameter("quality_tier", tier);
        var mistCode = MistShader;
        if (_packedLookups is not null)
        {
            // Reuse the same live arrays as the ground, not a second 33 MB ID raster.
            mistCode = mistCode.Replace("uniform sampler2D province_ids : filter_nearest;",
                    "uniform sampler2DArray packed_ids : filter_nearest, repeat_enable;")
                .Replace("uniform sampler2D fog_visibility : filter_nearest, repeat_disable;",
                    "uniform sampler2DArray packed_lookups : filter_nearest, repeat_disable;")
                .Replace("texture(province_ids,", "mist_province(")
                .Replace("texture(fog_visibility,", "mist_visibility(")
                .Replace("textureSize(province_ids, 0)", "textureSize(packed_ids, 0).xy")
                .Replace("uvec3 scramble(",
                    "vec4 mist_province(vec2 uv) { return texture(packed_ids, vec3(uv, 1.0)); }\n" +
                    "vec4 mist_visibility(vec2 uv) { return texture(packed_lookups, vec3(uv, 11.0)); }\nuvec3 scramble(");
        }
        _mistMaterial = new ShaderMaterial { Shader = new Shader { Code = mistCode }, RenderPriority = -21 };
        _mistMaterial.SetShaderParameter("quality_tier", tier);

        _cloudShade = Shell("CloudShadows", GlobeRadius * 1.0016f, _cloudShadeMaterial, 192, 96);
        _mistShell = Shell("FogOfWarMist", GlobeRadius * 1.0026f, _mistMaterial, 256, 128);
        _cloudDeck = Shell("CloudDeck", GlobeRadius * 1.011f, _cloudMaterial, 192, 96);
        world.AddChild(_cloudShade);
        world.AddChild(_mistShell);
        world.AddChild(_cloudDeck);

        // And the air itself: a thin blue haze round the planet's edge, brightest where the
        // line of sight grazes the ground and gone a few hundred kilometres out, which is the
        // one thing that makes a sphere on a black ground read as a world rather than a ball.
        var air = new ShaderMaterial { Shader = new Shader { Code = AirShader }, RenderPriority = -24 };
        air.SetShaderParameter("inner", GlobeRadius);
        air.SetShaderParameter("outer", GlobeRadius * AirReach);
        _airGlow = Shell("AirGlow", GlobeRadius * AirReach, air, 128, 64);
        world.AddChild(_airGlow);

        // The flat map is three copies side by side, moved round as the camera crosses the date
        // line; each carries the same three layers a hair above its ground, so they move with it.
        _flatSheets.Clear();
        if (IsInstanceValid(_flatRoot))
            foreach (var child in _flatRoot.GetChildren())
            {
                if (child is not MeshInstance3D tile) continue;
                foreach (var (name, lift, material) in new[] { ("CloudShadows", 0.004f, _cloudShadeMaterial),
                             ("FogOfWarMist", 0.008f, _mistMaterial), ("CloudDeck", 0.02f, _cloudMaterial) })
                {
                    var sheet = Sheet(name, lift, material);
                    tile.AddChild(sheet);
                    _flatSheets.Add(sheet);
                }
            }
        if (Platform.PlatformProfile.Current?.IsTouchPrimary == true && !Simulation.PlayerSettings.Has("map_clouds"))
            _cloudsShown = false;
        SyncWeatherReliefShells();
        UpdateWeather(0);
    }

    private void SyncWeatherReliefShells()
    {
        var tier = CurrentShaderQualityTier();
        var reliefActive = _reliefDisplacement > 0.0001f && _reliefHeightTexture is not null && tier < 1.5f;
        var flatProj = _flatMap ? 1f : 0f;
        var paperMix = _mapLookPaper ? 1f : 0f;
        var high = reliefActive && tier < 0.5f;

        if (IsInstanceValid(_atmosphereLayer) && _atmosphereLayer.Mesh is SphereMesh atmoMesh)
        {
            var r = GlobeRadius * (1f + AtmosphereThickness) + _reliefDisplacement * 1.08f;
            if (!Mathf.IsEqualApprox(atmoMesh.Radius, r)) { atmoMesh.Radius = r; atmoMesh.Height = r * 2f; }
        }
        if (IsInstanceValid(_cloudDeck) && _cloudDeck.Mesh is SphereMesh deckMesh)
        {
            var r = GlobeRadius * 1.011f + _reliefDisplacement * 1.15f;
            if (!Mathf.IsEqualApprox(deckMesh.Radius, r)) { deckMesh.Radius = r; deckMesh.Height = r * 2f; }
        }
        if (IsInstanceValid(_iceShell) && _iceShell.Mesh is SphereMesh iceMesh)
        {
            var r = GlobeRadius * 1.004f + _reliefDisplacement * 1.08f;
            if (!Mathf.IsEqualApprox(iceMesh.Radius, r)) { iceMesh.Radius = r; iceMesh.Height = r * 2f; }
        }
        if (IsInstanceValid(_cloudShade) && _cloudShade.Mesh is SphereMesh shadeMesh)
        {
            var targetSeg = reliefActive ? (high ? 512 : 256) : 192;
            var targetRings = reliefActive ? (high ? 256 : 128) : 96;
            if (shadeMesh.RadialSegments != targetSeg) shadeMesh.RadialSegments = targetSeg;
            if (shadeMesh.Rings != targetRings) shadeMesh.Rings = targetRings;
        }
        if (IsInstanceValid(_mistShell) && _mistShell.Mesh is SphereMesh mistMesh)
        {
            var targetSeg = reliefActive ? (high ? 512 : 256) : 256;
            var targetRings = reliefActive ? (high ? 256 : 128) : 128;
            if (mistMesh.RadialSegments != targetSeg) mistMesh.RadialSegments = targetSeg;
            if (mistMesh.Rings != targetRings) mistMesh.Rings = targetRings;
        }
        foreach (var sheet in _flatSheets)
        {
            if (!IsInstanceValid(sheet)) continue;
            if (sheet.MaterialOverride == _cloudMaterial)
            {
                var z = 0.02f + _reliefDisplacement * 1.95f * 1.15f;
                if (!Mathf.IsEqualApprox(sheet.Position.Z, z)) sheet.Position = sheet.Position with { Z = z };
            }
            else if (sheet.Mesh is QuadMesh quad)
            {
                var subW = reliefActive ? (high ? 256 : 128) : 0;
                var subD = reliefActive ? (high ? 128 : 64) : 0;
                if (quad.SubdivideWidth != subW) quad.SubdivideWidth = subW;
                if (quad.SubdivideDepth != subD) quad.SubdivideDepth = subD;
            }
        }
        foreach (var sheet in _iceSheets)
        {
            if (!IsInstanceValid(sheet)) continue;
            var z = 0.012f + _reliefDisplacement * 1.95f * 1.08f;
            if (!Mathf.IsEqualApprox(sheet.Position.Z, z)) sheet.Position = sheet.Position with { Z = z };
        }
        if (_cloudShadeMaterial is not null)
        {
            if (_reliefHeightTexture is not null) _cloudShadeMaterial.SetShaderParameter("relief_height", _reliefHeightTexture);
            _cloudShadeMaterial.SetShaderParameter("relief_displacement", _reliefDisplacement);
            _cloudShadeMaterial.SetShaderParameter("paper_mix", paperMix);
            _cloudShadeMaterial.SetShaderParameter("flat_projection", flatProj);
            _cloudShadeMaterial.SetShaderParameter("terrain_ready", _reliefHeightTexture is not null ? 1f : 0f);
        }
        if (_mistMaterial is not null)
        {
            if (_reliefHeightTexture is not null) _mistMaterial.SetShaderParameter("relief_height", _reliefHeightTexture);
            _mistMaterial.SetShaderParameter("relief_displacement", _reliefDisplacement);
            _mistMaterial.SetShaderParameter("paper_mix", paperMix);
            _mistMaterial.SetShaderParameter("flat_projection", flatProj);
            _mistMaterial.SetShaderParameter("terrain_ready", _reliefHeightTexture is not null ? 1f : 0f);
        }
    }

    private readonly System.Collections.Generic.List<MeshInstance3D> _flatSheets = new();

    private static MeshInstance3D Shell(string name, float radius, Material material, int segments, int rings) => new()
    {
        Name = name,
        Mesh = new SphereMesh { Radius = radius, Height = radius * 2f, RadialSegments = segments, Rings = rings },
        MaterialOverride = material,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        Visible = false
    };

    private static MeshInstance3D Sheet(string name, float lift, Material material) => new()
    {
        Name = name,
        Mesh = new QuadMesh { Size = new Vector2(FlatMapWidth, FlatMapHeight) },
        MaterialOverride = material,
        Position = new Vector3(0f, 0f, lift),
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
    };

    private void UpdateWeather(double delta)
    {
        if (_cloudMaterial is null || _mistMaterial is null) return;
        // A game without weather (common/world_layers) or MobileLow (quality_tier >= 2) draws none of it.
        if (!Simulation.WorldLayers.On("weather") || _weatherQualityTier >= 1.5f)
        {
            if (IsInstanceValid(_cloudDeck)) _cloudDeck.Visible = false;
            if (IsInstanceValid(_cloudShade)) _cloudShade.Visible = false;
            if (IsInstanceValid(_mistShell)) _mistShell.Visible = false;
            if (IsInstanceValid(_airGlow)) _airGlow.Visible = false;
            foreach (var sheet in _flatSheets)
                if (IsInstanceValid(sheet)) sheet.Visible = false;
            return;
        }
        // On MobileMedium (quality_tier == 1), halve per-frame weather uniform updates.
        if (_weatherQualityTier >= 0.5f && delta > 0.0)
        {
            _weatherMobileAccum += delta;
            if (_weatherMobileAccum < 0.033) return;
            delta = _weatherMobileAccum;
            _weatherMobileAccum = 0.0;
        }
        var defines = _campaign?.Defines;
        var distance = ViewDistance;

        // A mod's own sky (the crossover's moving clouds) replaces this one rather than doubling it.
        var replaced = IsInstanceValid(_cloudLayer) && _cloudLayer.Visible;
        var cloudFull = defines?.CloudFullDistance ?? 10.5f;
        var cloudClear = defines?.CloudClearDistance ?? 7.6f;
        var realisticCumulus = _mapLookRealistic && _packedLookups is null && QualityWeather;
        if (realisticCumulus)
        {
            // Gone below the strategic distance (7): close in, the map is always readable.
            cloudClear = System.MathF.Min(cloudClear, 7.0f);
            cloudFull = System.MathF.Min(cloudFull, 9.6f);
        }
        var allowed = Simulation.WeatherReadability.LayersAllowed(_campaign);
        // Paper chart mode mutes decorative clouds and their shadows at every zoom (pure paper + max FPS).
        var clouds = _mapLookPaper || !allowed || !QualityWeather || !_weatherEnabled || !_cloudsShown || replaced || cloudFull <= cloudClear ? 0f
            : Mathf.SmoothStep(cloudClear, cloudFull, distance);

        var mistFull = defines?.MistFullDistance ?? 6.0f;
        var mistClear = defines?.MistClearDistance ?? 8.2f;
        var fogged = FogOfWarActive && _campaign?.Player is { IsHuman: true } && _visionImage is not null;
        // The mist is the fog of war - what the player cannot see - so no quality step takes it
        // away: it answers to the FOG switch and the campaign's rules only.
        var mist = !allowed || !_weatherEnabled || !_mistShown || !fogged || mistClear <= mistFull ? 0f
            : 1f - Mathf.SmoothStep(mistFull, mistClear, distance);

        var cloudsVisible = clouds > 0.002f;
        var mistVisible = mist > 0.002f;
        var globe = !_flatMap;
        if (cloudsVisible || mistVisible)
        {
            // Weather keeps moving while the game is paused, only slower: a frozen sky reads as a
            // frozen program.
            var paused = _campaign is null || _campaign.IsPaused;
            // Except under the ice, where the sky is as still as everything else.
            if (!Simulation.WorldCurses.Frozen(_campaign)) _weatherClock += delta * (paused ? 0.25 : 1.0);
            var clock = (float)((VerificationWeatherTime ?? _weatherClock) % 100000.0);
            var readability = Simulation.WeatherReadability.Read(defines);

            if (cloudsVisible)
                ApplyCloudUniforms(clouds, clock, realisticCumulus,
                    readability.CloudOpacity * _cloudOpacityScale * 0.38f,
                    readability.EdgeSoftness, readability.DetailStrength);

            if (mistVisible)
                ApplyMistUniforms(mist, readability.MistOpacity, clock);
        }

        if (IsInstanceValid(_airGlow))
        {
            var airVisible = globe && !_mapLookPaper;
            if (_airGlow.Visible != airVisible) _airGlow.Visible = airVisible;
        }
        var drawClouds = globe && cloudsVisible;
        if (_cloudDeck.Visible != drawClouds) _cloudDeck.Visible = drawClouds;
        if (_cloudShade.Visible != drawClouds) _cloudShade.Visible = drawClouds;
        var drawMist = globe && mistVisible;
        if (_mistShell.Visible != drawMist) _mistShell.Visible = drawMist;
        foreach (var sheet in _flatSheets)
        {
            if (!IsInstanceValid(sheet)) continue;
            var want = sheet.MaterialOverride == _mistMaterial ? mistVisible : cloudsVisible;
            if (sheet.Visible != want) sheet.Visible = want;
        }
    }

    private void SyncCloudFloat(string name, float value, ref float cached)
    {
        if (cached == value) return;
        cached = value;
        _cloudMaterial.SetShaderParameter(name, value);
        _cloudShadeMaterial.SetShaderParameter(name, value);
    }

    private void ApplyCloudUniforms(float clouds, float clock, bool realisticCumulus, float opacity, float edgeSoftness, float detailStrength)
    {
        if (!ReferenceEquals(_wCachedCloudMat, _cloudMaterial) || !ReferenceEquals(_wCachedCloudShadeMat, _cloudShadeMaterial))
        {
            _wCachedCloudMat = _cloudMaterial;
            _wCachedCloudShadeMat = _cloudShadeMaterial;
            _wCloudRealistic = _wCloudPresence = _wCloudOpacity = _wCloudEdgeSoftness = float.NaN;
            _wCloudDetailStrength = _wCloudWind = _wCloudFlatMap = float.NaN;
            _wCloudSunDir = new Vector3(float.NaN, float.NaN, float.NaN);
        }
        SyncCloudFloat("realistic_cumulus", realisticCumulus ? 1f : 0f, ref _wCloudRealistic);
        SyncCloudFloat("presence", clouds, ref _wCloudPresence);
        SyncCloudFloat("opacity", opacity, ref _wCloudOpacity);
        SyncCloudFloat("edge_softness", edgeSoftness, ref _wCloudEdgeSoftness);
        SyncCloudFloat("detail_strength", detailStrength, ref _wCloudDetailStrength);
        SyncCloudFloat("wind", clock, ref _wCloudWind);
        SyncCloudFloat("flat_map", _flatMap ? 1f : 0f, ref _wCloudFlatMap);
        var sun = IsInstanceValid(_weatherSun) ? _weatherSun.GlobalBasis.Z.Normalized() : new Vector3(0.4f, 0.5f, 0.75f);
        if (!sun.IsEqualApprox(_wCloudSunDir))
        {
            _wCloudSunDir = sun;
            _cloudMaterial.SetShaderParameter("sun_dir", sun);
        }
    }

    private void SyncMistFloat(string name, float value, ref float cached)
    {
        if (cached == value) return;
        cached = value;
        _mistMaterial.SetShaderParameter(name, value);
    }

    private void ApplyMistUniforms(float mist, float mistOpacity, float clock)
    {
        if (!ReferenceEquals(_wCachedMistMat, _mistMaterial))
        {
            _wCachedMistMat = _mistMaterial;
            _wMistPresence = _wMistOpacity = _wMistWind = float.NaN;
            _wMistPackedIds = _wMistPackedLookups = _wMistVision = null;
            _wMistGlobeMat = null;
            _wMistProvinceIdsObj = _wMistPoliticalMapObj = null;
        }
        SyncMistFloat("presence", mist, ref _wMistPresence);
        SyncMistFloat("opacity", mistOpacity, ref _wMistOpacity);
        SyncMistFloat("wind", clock, ref _wMistWind);
        if (mist <= 0.001f || _globeMaterial is null) return;
        if (_packedLookups is not null)
        {
            if (!ReferenceEquals(_wMistPackedIds, _packedIds))
            {
                _wMistPackedIds = _packedIds;
                _mistMaterial.SetShaderParameter("packed_ids", _packedIds);
            }
            if (!ReferenceEquals(_wMistPackedLookups, _packedLookups))
            {
                _wMistPackedLookups = _packedLookups;
                _mistMaterial.SetShaderParameter("packed_lookups", _packedLookups);
            }
        }
        else if (!ReferenceEquals(_wMistVision, _visionTexture))
        {
            _wMistVision = _visionTexture;
            _mistMaterial.SetShaderParameter("fog_visibility", _visionTexture);
        }
        if (ReferenceEquals(_wMistGlobeMat, _globeMaterial) && _wMistMapApplied == _mapApplied &&
            ReferenceEquals(_wMistCampaign, _campaign) && _wMistPoliticalMapObj is not null &&
            (_packedLookups is not null || _wMistProvinceIdsObj is not null))
            return;
        _wMistGlobeMat = _globeMaterial;
        _wMistMapApplied = _mapApplied;
        _wMistCampaign = _campaign;
        if (_packedLookups is null)
        {
            var provVariant = _globeMaterial.GetShaderParameter("province_ids");
            var provObj = provVariant.AsGodotObject();
            if (!ReferenceEquals(_wMistProvinceIdsObj, provObj))
            {
                _wMistProvinceIdsObj = provObj;
                _mistMaterial.SetShaderParameter("province_ids", provVariant);
            }
        }
        var polVariant = _globeMaterial.GetShaderParameter("political_map");
        var polObj = polVariant.AsGodotObject();
        if (!ReferenceEquals(_wMistPoliticalMapObj, polObj))
        {
            _wMistPoliticalMapObj = polObj;
            _mistMaterial.SetShaderParameter("political_map", polVariant);
        }
    }

    private void ApplyWeatherQualityTier(float tier)
    {
        _weatherQualityTier = tier;
        _cloudMaterial?.SetShaderParameter("quality_tier", tier);
        _cloudShadeMaterial?.SetShaderParameter("quality_tier", tier);
        _mistMaterial?.SetShaderParameter("quality_tier", tier);
        UpdateWeather(0);
    }

    // The clouds are gradient noise - smooth at every scale, no grid to show - turned by a
    // different rotation at every octave so no octave's lattice lines up with another's. The
    // field they replaced was value noise on one axis-aligned grid, and its creases came
    // through on the globe as long straight seams; a wind that blew west in the tropics and
    // east beyond them sheared the band between into threads. Now the whole sky turns slowly
    // and the clouds change shape as it goes, which is what clouds seen from orbit do.
    private const string CloudShader = @"shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_back, shadows_disabled;
uniform float presence = 1.0;
uniform float wind = 0.0;
uniform vec3 sun_dir = vec3(0.4, 0.5, 0.75);
uniform float shade_pass = 0.0;
uniform float flat_map = 0.0;
uniform float opacity = 0.50;
uniform float edge_softness = 0.20;
uniform float detail_strength = 0.08;
uniform float realistic_cumulus = 0.0;
uniform float quality_tier = 0.0;
uniform sampler2D relief_height : filter_linear, repeat_enable;
uniform float relief_displacement = 0.0;
uniform float paper_mix = 0.0;
uniform float flat_projection = 0.0;
uniform float terrain_ready = 0.0;

void vertex() {
    if (shade_pass > 0.5 && relief_displacement > 0.0001 && terrain_ready > 0.5) {
        float paper_fade = smoothstep(0.92, 0.25, paper_mix);
        float disp_scale = relief_displacement * paper_fade;
        if (disp_scale > 0.0001) {
            vec2 uv = vec2(fract(UV.x), clamp(UV.y, 0.0, 1.0));
            float pole_fade = flat_projection > 0.5 ? 1.0 : smoothstep(0.495, 0.435, abs(uv.y - 0.5));
            float sample_lod = quality_tier < 0.5 ? 0.5 : 1.0;
            float h = textureLod(relief_height, uv, sample_lod).r;
            float h_land = max(0.0, h - 0.015) / 0.985;
            float land_lift = pow(h_land, 0.85) * disp_scale;
            float proj_scale = mix(1.0, 1.95, flat_projection);
            vec3 up_dir = flat_projection > 0.5 ? vec3(0.0, 0.0, 1.0) : normalize(NORMAL);
            VERTEX += up_dir * (land_lift * pole_fade * proj_scale);
        }
    }
}

uvec3 scramble(uvec3 v) {
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v;
}
vec3 gradient(vec3 cell) {
    uvec3 h = scramble(uvec3(ivec3(cell) + 4096));
    return normalize(vec3(h & uvec3(1023u)) / 511.5 - 1.0 + vec3(0.0001));
}
float gnoise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = p - i;
    vec3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float n000 = dot(gradient(i), f);
    float n100 = dot(gradient(i + vec3(1, 0, 0)), f - vec3(1, 0, 0));
    float n010 = dot(gradient(i + vec3(0, 1, 0)), f - vec3(0, 1, 0));
    float n110 = dot(gradient(i + vec3(1, 1, 0)), f - vec3(1, 1, 0));
    float n001 = dot(gradient(i + vec3(0, 0, 1)), f - vec3(0, 0, 1));
    float n101 = dot(gradient(i + vec3(1, 0, 1)), f - vec3(1, 0, 1));
    float n011 = dot(gradient(i + vec3(0, 1, 1)), f - vec3(0, 1, 1));
    float n111 = dot(gradient(i + vec3(1, 1, 1)), f - vec3(1, 1, 1));
    return mix(mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y),
               mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y), u.z);
}
const mat3 TURN = mat3(vec3(0.00, 0.80, 0.60), vec3(-0.80, 0.36, -0.48), vec3(-0.60, -0.48, 0.64));
// Roughly 0 to 1, centred on a half.
float fbm(vec3 p, int octaves) {
    float sum = 0.0, amp = 0.5, norm = 0.0;
    for (int k = 0; k < octaves; k++) {
        sum += amp * gnoise(p);
        norm += amp;
        p = TURN * p * 2.03 + vec3(17.1, 3.7, 9.3);
        amp *= 0.5;
    }
    return clamp(0.5 + 1.3 * sum / norm, 0.0, 1.0);
}
vec3 sphere_at(vec2 uv) {
    float lon = (uv.x - 0.5) * TAU;
    float lat = (0.5 - uv.y) * PI;
    return vec3(cos(lat) * sin(lon), sin(lat), cos(lat) * cos(lon));
}
// The sky at one point of the planet: turned with the wind, warped gently, and morphing.
// Tiered by quality_tier to keep fragment ALU fast across RTX/desktop/mobile while retaining cloud form.
float field(vec3 n) {
    float drift = wind * 0.0016;
    float c = cos(drift), s = sin(drift);
    vec3 p = vec3(c * n.x + s * n.z, n.y, -s * n.x + c * n.z) * 3.1;
    if (quality_tier > 1.5) {
        return fbm(p * 1.5, 2);
    }
    float evolve = wind * 0.0011;
    if (shade_pass > 0.5) {
        vec3 warp_s = vec3(fbm(p + vec3(0.0, 0.0, evolve), 1), 0.5, 0.5) - 0.5;
        return fbm(p * 1.5 + warp_s * 0.58, 2);
    }
    if (quality_tier > 0.5) {
        vec3 warp_m = vec3(fbm(p + vec3(0.0, 0.0, evolve), 1),
                           fbm(p + vec3(5.2, 1.3, 2.8 - evolve), 1), 0.5) - 0.5;
        return fbm(p * 1.5 + warp_m * 0.55, 2);
    }
    vec3 warp = vec3(fbm(p + vec3(0.0, 0.0, evolve), 1),
                     fbm(p + vec3(5.2, 1.3, 2.8 - evolve), 1), 0.5) - 0.5;
    vec3 q = p * 1.5 + warp * 0.58;
    float body = fbm(q, 3);
    float detail = detail_strength > 0.005 ? fbm(q * 2.2 + vec3(11.0, 4.0, 7.0), 1) : body;
    return mix(body, detail, detail_strength);
}
void fragment() {
    if (presence <= 0.002 || opacity <= 0.001) discard;
    vec2 uv = UV;
    if (shade_pass > 0.5) uv += vec2(0.0030, 0.0040);
    vec3 n = sphere_at(uv);
    float lat = (0.5 - uv.y) * 180.0;
    float alat = abs(lat);
    float d = field(n);
    // Where cloud is common: the equatorial belt, the storm tracks; not the horse latitudes.
    float itcz = exp(-pow((lat - 5.0) / 10.0, 2.0));
    float subtropic = exp(-pow((alat - 25.0) / 10.0, 2.0));
    float storms = exp(-pow((alat - 56.0) / 13.0, 2.0));
    float cover = clamp(0.40 + 0.30 * itcz - 0.26 * subtropic + 0.28 * storms, 0.08, 0.90);
    float threshold = mix(0.68, 0.47, cover);
    // Soft edges, and never sharper than a pixel: far away a cloud's edge is a blur.
    float aa = fwidth(d) * 1.5;
    // The define is the edge's whole width; half of it either side of the threshold.
    float soft = edge_softness * 0.65 + aa;
    float body = smoothstep(threshold - soft, threshold + soft * 1.2, d);
    float core = smoothstep(threshold, threshold + soft * 2.0, d);
    float limb = flat_map > 0.5 ? 1.0 : smoothstep(0.0, 0.25, dot(NORMAL, VIEW));
    if (shade_pass > 0.5) {
        ALBEDO = vec3(0.015, 0.024, 0.042);
        ALPHA = body * body * opacity * mix(0.22, 0.29, realistic_cumulus) * presence * limb;
    } else {
        float sun_dot = clamp(dot(sphere_at(UV), normalize(sun_dir)), -0.2, 1.0);
        float light = flat_map > 0.5 ? 0.95 : 0.80 + 0.20 * clamp(sun_dot, 0.0, 1.0);
        // Subtle twilight silver-warmth near planetary dusk + crisp D65 cumulus crowns.
        float dusk = flat_map > 0.5 ? 0.0 : smoothstep(-0.18, 0.06, sun_dot) * (1.0 - smoothstep(0.06, 0.36, sun_dot));
        vec3 thin = mix(vec3(0.86, 0.89, 0.93), vec3(0.79, 0.84, 0.92), realistic_cumulus);
        vec3 thick = mix(vec3(0.98, 0.985, 1.0), vec3(0.985, 0.99, 1.00), realistic_cumulus);
        vec3 cloud_col = mix(thin, thick, core) * light;
        cloud_col = mix(cloud_col, cloud_col * vec3(1.06, 0.92, 0.80), dusk * 0.42);
        ALBEDO = cloud_col;
        ALPHA = body * mix(0.30, 1.0, core) * opacity * presence * limb;
    }
}";

    // Drawn on the shell's far side: a ray that misses the planet passes the far wall of the
    // shell, and how close it came to the ground is how much air it went through.
    private const string AirShader = @"shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_front, shadows_disabled;
uniform float inner = 5.0;
uniform float outer = 5.2;
void fragment() {
    vec3 centre = (VIEW_MATRIX * MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    vec3 ray = normalize(VERTEX);
    float along = dot(centre, ray);
    float miss = length(centre - ray * along);
    if (miss < inner * 0.985 || miss >= outer) discard;
    float depth = clamp((outer - miss) / (outer - inner), 0.0, 1.0);
    float glow = pow(depth, 2.4);
    float horizon_band = pow(depth, 5.2) * step(inner * 0.992, miss);
    // Seen from close by, the haze is the horizon itself; keep it, but quieter.
    float height = clamp((length(centre) - inner) / (inner * 1.4), 0.0, 1.0);
    vec3 rayleigh = mix(vec3(0.24, 0.48, 0.92), vec3(0.50, 0.76, 1.0), horizon_band);
    ALBEDO = rayleigh * glow * mix(0.15, 0.38, height);
    ALPHA = 1.0;
}";

    private const string MistShader = @"shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_back, shadows_disabled;
uniform sampler2D province_ids : filter_nearest;
uniform sampler2D political_map : filter_linear;
uniform sampler2D fog_visibility : filter_nearest, repeat_disable;
uniform float presence = 0.0;
uniform float wind = 0.0;
uniform float opacity = 0.22;
uniform float quality_tier = 0.0;
uniform sampler2D relief_height : filter_linear, repeat_enable;
uniform float relief_displacement = 0.0;
uniform float paper_mix = 0.0;
uniform float flat_projection = 0.0;
uniform float terrain_ready = 0.0;

void vertex() {
    if (relief_displacement > 0.0001 && terrain_ready > 0.5) {
        float paper_fade = smoothstep(0.92, 0.25, paper_mix);
        float disp_scale = relief_displacement * paper_fade;
        if (disp_scale > 0.0001) {
            vec2 uv = vec2(fract(UV.x), clamp(UV.y, 0.0, 1.0));
            float pole_fade = flat_projection > 0.5 ? 1.0 : smoothstep(0.495, 0.435, abs(uv.y - 0.5));
            float sample_lod = quality_tier < 0.5 ? 0.5 : 1.0;
            float h = textureLod(relief_height, uv, sample_lod).r;
            float h_land = max(0.0, h - 0.015) / 0.985;
            float land_lift = pow(h_land, 0.85) * disp_scale;
            float proj_scale = mix(1.0, 1.95, flat_projection);
            vec3 up_dir = flat_projection > 0.5 ? vec3(0.0, 0.0, 1.0) : normalize(NORMAL);
            VERTEX += up_dir * (land_lift * pole_fade * proj_scale);
        }
    }
}

uvec3 scramble(uvec3 v) {
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v;
}
vec3 gradient(vec3 cell) {
    uvec3 h = scramble(uvec3(ivec3(cell) + 4096));
    return normalize(vec3(h & uvec3(1023u)) / 511.5 - 1.0 + vec3(0.0001));
}
// Gradient noise on the sphere itself, as the clouds use: no grid to show, and no stretching
// towards the poles the way noise laid on the map's own coordinates stretches.
float gnoise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = p - i;
    vec3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    return mix(mix(mix(dot(gradient(i), f), dot(gradient(i + vec3(1, 0, 0)), f - vec3(1, 0, 0)), u.x),
                   mix(dot(gradient(i + vec3(0, 1, 0)), f - vec3(0, 1, 0)), dot(gradient(i + vec3(1, 1, 0)), f - vec3(1, 1, 0)), u.x), u.y),
               mix(mix(dot(gradient(i + vec3(0, 0, 1)), f - vec3(0, 0, 1)), dot(gradient(i + vec3(1, 0, 1)), f - vec3(1, 0, 1)), u.x),
                   mix(dot(gradient(i + vec3(0, 1, 1)), f - vec3(0, 1, 1)), dot(gradient(i + vec3(1, 1, 1)), f - vec3(1, 1, 1)), u.x), u.y), u.z);
}
const mat3 TURN = mat3(vec3(0.00, 0.80, 0.60), vec3(-0.80, 0.36, -0.48), vec3(-0.60, -0.48, 0.64));
float fbm(vec3 p, int octaves) {
    float sum = 0.0, amp = 0.5, norm = 0.0;
    for (int k = 0; k < octaves; k++) {
        sum += amp * gnoise(p);
        norm += amp;
        p = TURN * p * 2.03 + vec3(17.1, 3.7, 9.3);
        amp *= 0.5;
    }
    return clamp(0.5 + 1.2 * sum / norm, 0.0, 1.0);
}
vec3 sphere_at(vec2 uv) {
    float lon = (uv.x - 0.5) * TAU;
    float lat = (0.5 - uv.y) * PI;
    return vec3(cos(lat) * sin(lon), sin(lat), cos(lat) * cos(lon));
}
float hidden_at(vec2 uv) {
    vec4 encoded = texture(province_ids, vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0)));
    float province = floor(encoded.r * 255.0 + 0.5) + floor(encoded.g * 255.0 + 0.5) * 256.0;
    vec2 lookup = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
    return 1.0 - step(0.5, texture(fog_visibility, lookup).r);
}
void fragment() {
    if (presence <= 0.002 || opacity <= 0.001) discard;
    vec2 texel = 1.0 / vec2(textureSize(province_ids, 0));
    // Banks a couple of degrees across with wisps inside them, in two layers drifting
    // different ways. Tiered by quality_tier to keep fragment ALU fast across desktop & mobile.
    vec3 q = sphere_at(UV) * 26.0;
    int slow_oct = quality_tier > 0.5 ? 2 : 3;
    int fast_oct = quality_tier > 0.5 ? 1 : 2;
    float slow = fbm(q + vec3(wind * 0.020, 0.0, wind * 0.006), slow_oct);
    float fast = quality_tier > 1.5 ? slow : fbm(q * 2.1 - vec3(wind * 0.050, wind * 0.012, 0.0), fast_oct);
    // The edge of the unknown billows: the taps that find it are bent by the mist itself.
    vec2 bend = (vec2(slow, fast) - 0.5) * texel * 14.0;
    float hidden = hidden_at(UV + bend) * 2.0;
    if (quality_tier > 0.5) {
        for (int k = 0; k < 4; k++) {
            float a = float(k) * 1.570796;
            hidden += hidden_at(UV + bend + vec2(cos(a), sin(a)) * texel * 5.0);
        }
        hidden /= 6.0;
    } else {
        for (int k = 0; k < 8; k++) {
            float a = float(k) * 0.785398;
            hidden += hidden_at(UV + bend + vec2(cos(a), sin(a)) * texel * 5.0);
        }
        hidden /= 10.0;
    }
    float land = smoothstep(0.25, 0.75, texture(political_map, UV).a);
    // A thin veil that drifts, not a wall: thickest in banks, nearly clear between them, so
    // the ground under it stays readable. The intelligence mask remains unchanged.
    float billow = smoothstep(0.30, 0.80, slow * 0.75 + fast * 0.25);
    float wisps = 0.20 + 0.80 * billow;
    float density = smoothstep(0.05, 0.95, hidden) * land * wisps;
    // A grey veil, not a white cloud bank: the light tops (0.80-0.89) read as weather over
    // the closest zoom, where the map has to stay readable.
    ALBEDO = mix(vec3(0.42, 0.45, 0.49), vec3(0.58, 0.61, 0.65), billow);
    ALPHA = density * opacity * presence;
}";
}
}

namespace DealOrForce.Simulation
{
    public sealed partial class Defines
    {
        /// <summary>View distance at which the clouds are fully drawn (the strategic camera).</summary>
        public float CloudFullDistance { get; set; } = 10.5f;

        /// <summary>View distance at which the clouds have parted completely (the tactical camera).</summary>
        public float CloudClearDistance { get; set; } = 7.6f;

        /// <summary>View distance at which the fog-of-war mist is fully drawn.</summary>
        public float MistFullDistance { get; set; } = 6.0f;

        /// <summary>View distance past which there is no fog-of-war mist.</summary>
        public float MistClearDistance { get; set; } = 8.2f;
    }
}
