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
    private bool _cloudsOn = true;
    private bool _mistOn = true;

    /// <summary>How high the camera is, in the units the weather fades by (MapAmbience listens to it too).</summary>
    public float ViewHeight => ViewDistance;

    /// <summary>The point of the world under the middle of the view.</summary>
    public (float Latitude, float Longitude) ViewCentre => _flatMap
        ? (_flatY * 18f, Mathf.Wrap(_flatX * 18f, -180f, 180f))
        : (-Mathf.RadToDeg(_pitch), Mathf.Wrap(Mathf.RadToDeg(_yaw) - 180f, -180f, 180f));

    /// <summary>Clouds on or off (the "map_clouds" setting and the map bar's button).</summary>
    public bool CloudsShown
    {
        get => _cloudsOn;
        set { _cloudsOn = value; UpdateWeather(0); }
    }

    /// <summary>The fog-of-war mist on or off (the "map_mist" setting). The fog itself stays: only its look goes.</summary>
    public bool MistShown
    {
        get => _mistOn;
        set { _mistOn = value; UpdateWeather(0); }
    }

    /// <summary>Both at once, as the single option before them did.</summary>
    public bool WeatherEnabled
    {
        get => _cloudsOn || _mistOn;
        set { _cloudsOn = _mistOn = value; UpdateWeather(0); }
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
        _mistMaterial = new ShaderMaterial { Shader = new Shader { Code = MistShader }, RenderPriority = -21 };

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
        UpdateWeather(0);
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
        var defines = _campaign?.Defines;
        var distance = ViewDistance;

        // A mod's own sky (the crossover's moving clouds) replaces this one rather than doubling it.
        var replaced = IsInstanceValid(_weatherWorld) &&
                       _weatherWorld.GetNodeOrNull<Node3D>("MovingClouds") is { Visible: true };
        var cloudFull = defines?.CloudFullDistance ?? 10.5f;
        var cloudClear = defines?.CloudClearDistance ?? 7.6f;
        var clouds = !_cloudsOn || replaced || cloudFull <= cloudClear ? 0f
            : Mathf.SmoothStep(cloudClear, cloudFull, distance);

        var mistFull = defines?.MistFullDistance ?? 6.0f;
        var mistClear = defines?.MistClearDistance ?? 8.2f;
        var fogged = FogOfWarActive && _campaign?.Player is { IsHuman: true } && _visionTexture is not null;
        var mist = !_mistOn || !fogged || mistClear <= mistFull ? 0f
            : 1f - Mathf.SmoothStep(mistFull, mistClear, distance);

        // Weather keeps moving while the game is paused, only slower: a frozen sky reads as a
        // frozen program.
        var paused = _campaign is null || _campaign.IsPaused;
        _weatherClock += delta * (paused ? 0.25 : 1.0);
        var clock = (float)(_weatherClock % 100000.0);
        var readability = Simulation.WeatherReadability.Read(defines);

        _cloudMaterial.SetShaderParameter("presence", clouds);
        _cloudShadeMaterial.SetShaderParameter("presence", clouds);
        _cloudMaterial.SetShaderParameter("opacity", readability.CloudOpacity);
        _cloudShadeMaterial.SetShaderParameter("opacity", readability.CloudOpacity);
        _cloudMaterial.SetShaderParameter("edge_softness", readability.EdgeSoftness);
        _cloudShadeMaterial.SetShaderParameter("edge_softness", readability.EdgeSoftness);
        _cloudMaterial.SetShaderParameter("detail_strength", readability.DetailStrength);
        _cloudShadeMaterial.SetShaderParameter("detail_strength", readability.DetailStrength);
        _cloudMaterial.SetShaderParameter("wind", clock);
        _cloudShadeMaterial.SetShaderParameter("wind", clock);
        var sun = IsInstanceValid(_weatherSun) ? _weatherSun.GlobalBasis.Z.Normalized() : new Vector3(0.4f, 0.5f, 0.75f);
        _cloudMaterial.SetShaderParameter("sun_dir", sun);
        _cloudMaterial.SetShaderParameter("flat_map", _flatMap ? 1f : 0f);
        _cloudShadeMaterial.SetShaderParameter("flat_map", _flatMap ? 1f : 0f);

        _mistMaterial.SetShaderParameter("presence", mist);
        _mistMaterial.SetShaderParameter("opacity", readability.MistOpacity);
        _mistMaterial.SetShaderParameter("wind", clock);
        if (mist > 0.001f && _globeMaterial is not null)
        {
            // The ground's own maps, read where the ground reads them.
            _mistMaterial.SetShaderParameter("fog_visibility", _visionTexture);
            _mistMaterial.SetShaderParameter("province_ids", _globeMaterial.GetShaderParameter("province_ids"));
            _mistMaterial.SetShaderParameter("political_map", _globeMaterial.GetShaderParameter("political_map"));
        }

        var globe = !_flatMap;
        if (IsInstanceValid(_airGlow)) _airGlow.Visible = globe;
        _cloudDeck.Visible = _cloudShade.Visible = globe && clouds > 0.002f;
        _mistShell.Visible = globe && mist > 0.002f;
        foreach (var sheet in _flatSheets)
            if (IsInstanceValid(sheet))
                sheet.Visible = sheet.MaterialOverride == _mistMaterial ? mist > 0.002f : clouds > 0.002f;
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
float field(vec3 n) {
    float drift = wind * 0.0016;
    float c = cos(drift), s = sin(drift);
    vec3 p = vec3(c * n.x + s * n.z, n.y, -s * n.x + c * n.z) * 3.1;
    float evolve = wind * 0.0011;
    vec3 warp = vec3(fbm(p + vec3(0.0, 0.0, evolve), 2),
                     fbm(p + vec3(5.2, 1.3, 2.8 - evolve), 2),
                     fbm(p + vec3(1.7 + evolve, 9.2, 4.4), 2)) - 0.5;
    vec3 q = p * 1.5 + warp * 0.6;
    float body = fbm(q, 5);
    float detail = fbm(q * 2.2 + vec3(11.0, 4.0, 7.0), 2);
    return mix(body, detail, detail_strength);
}
void fragment() {
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
        ALBEDO = vec3(0.02, 0.03, 0.05);
        ALPHA = body * body * opacity * 0.22 * presence * limb;
    } else {
        float light = flat_map > 0.5 ? 0.95 : 0.80 + 0.20 * clamp(dot(sphere_at(UV), normalize(sun_dir)), 0.0, 1.0);
        // Thin cloud is a grey veil the ground shows through; thick cloud is bright on top.
        vec3 thin = vec3(0.86, 0.89, 0.93);
        vec3 thick = vec3(0.98, 0.985, 1.0);
        ALBEDO = mix(thin, thick, core) * light;
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
    float depth = clamp((outer - miss) / (outer - inner), 0.0, 1.0);
    float glow = pow(depth, 2.6) * step(inner * 0.985, miss);
    // Seen from close by, the haze is the horizon itself; keep it, but quieter.
    float height = clamp((length(centre) - inner) / (inner * 1.4), 0.0, 1.0);
    ALBEDO = vec3(0.30, 0.55, 0.95) * glow * mix(0.35, 0.85, height);
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
float fbm(vec3 p) {
    float sum = 0.0, amp = 0.5, norm = 0.0;
    for (int k = 0; k < 4; k++) {
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
    vec2 texel = 1.0 / vec2(textureSize(province_ids, 0));
    // Banks a couple of degrees across with wisps inside them, in two layers drifting
    // different ways.
    vec3 q = sphere_at(UV) * 26.0;
    float slow = fbm(q + vec3(wind * 0.020, 0.0, wind * 0.006));
    float fast = fbm(q * 2.1 - vec3(wind * 0.050, wind * 0.012, 0.0));
    // The edge of the unknown billows: the taps that find it are bent by the mist itself.
    vec2 bend = (vec2(slow, fast) - 0.5) * texel * 14.0;
    float hidden = hidden_at(UV + bend) * 2.0;
    for (int k = 0; k < 8; k++) {
        float a = float(k) * 0.785398;
        hidden += hidden_at(UV + bend + vec2(cos(a), sin(a)) * texel * 5.0);
    }
    hidden /= 10.0;
    float land = smoothstep(0.25, 0.75, texture(political_map, UV).a);
    // A thin veil that drifts, not a wall: thickest in banks, nearly clear between them, so
    // the ground under it stays readable. The intelligence mask remains unchanged.
    float billow = smoothstep(0.30, 0.80, slow * 0.75 + fast * 0.25);
    float wisps = 0.20 + 0.80 * billow;
    float density = smoothstep(0.05, 0.95, hidden) * land * wisps;
    // The tops of the banks catch the light; the hollows between them are grey.
    ALBEDO = mix(vec3(0.56, 0.61, 0.68), vec3(0.80, 0.84, 0.89), billow);
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
