using Godot;

namespace DealOrForce
{

/// <summary>
/// The ice of `snowgrave` (Simulation.WorldCurses): a sheet over the whole world, on the globe
/// and on the flat map alike.
///
/// It is meant to read as thick. The sheet is broken into plates, each tilted a little so the
/// sun catches them one by one; a second net of cracks lies deeper in it and slides against the
/// first as the camera moves, which is what makes a surface look like it has depth; the ground
/// under it is seen bent, blurred and drained of colour; frost gathers in patches and on the
/// cracks, and whitens the planet's edge where the eye looks through the most ice. The armies
/// and the borders are under it with everything else.
///
/// It spreads from the player's capital when the curse falls - a front of frost running round
/// the planet - and is simply there in a save loaded under it. Drawn after every marker and
/// label, so nothing on the map stands above it.
/// </summary>
public sealed partial class GlobePresentation
{
    private MeshInstance3D _iceShell;
    private ShaderMaterial _iceMaterial, _iceFlatMaterial;
    private readonly System.Collections.Generic.List<MeshInstance3D> _iceSheets = new();
    private float _iceSpread = -1f;

    /// <summary>Start the ice from a place on the map, the front running out from it over a few seconds.</summary>
    public void BeginIce(float latitude, float longitude)
    {
        _iceSpread = 0f;
        var origin = SurfacePoint(latitude, longitude, 1f);
        EnsureIce();
        _iceMaterial?.SetShaderParameter("origin", origin);
        _iceFlatMaterial?.SetShaderParameter("origin", origin);
    }

    private void EnsureIce()
    {
        if (_iceMaterial is not null || !IsInstanceValid(_weatherWorld)) return;
        var shader = new Shader { Code = IceShader };
        _iceMaterial = new ShaderMaterial { Shader = shader, RenderPriority = 40 };
        _iceFlatMaterial = new ShaderMaterial { Shader = shader, RenderPriority = 40 };
        _iceFlatMaterial.SetShaderParameter("flat_map", 1f);
        // A flat-map unit is eighteen degrees, a globe unit a fifth of the radius: the same ice.
        _iceFlatMaterial.SetShaderParameter("scale", 0.64f);
        _iceShell = Shell("SnowgraveIce", GlobeRadius * 1.004f, _iceMaterial, 256, 128);
        _weatherWorld.AddChild(_iceShell);
        if (IsInstanceValid(_flatRoot))
            foreach (var child in _flatRoot.GetChildren())
            {
                if (child is not MeshInstance3D tile) continue;
                var sheet = Sheet("SnowgraveIce", 0.012f, _iceFlatMaterial);
                sheet.Visible = false;
                tile.AddChild(sheet);
                _iceSheets.Add(sheet);
            }
    }

    private void UpdateIce(double delta)
    {
        var frozen = _campaign?.Curse == Simulation.WorldCurses.Snowgrave;
        if (!frozen)
        {
            if (_iceShell is not null) { _iceShell.Visible = false; foreach (var sheet in _iceSheets) sheet.Visible = false; }
            _iceSpread = -1f;
            return;
        }
        EnsureIce();
        if (_iceMaterial is null) return;
        // A save loaded under the ice has always been under it.
        if (_iceSpread < 0f) _iceSpread = 1f;
        _iceSpread = Mathf.Min(1f, _iceSpread + (float)delta / 7f);
        var spread = 1f - Mathf.Pow(1f - _iceSpread, 2.2f);
        _iceMaterial.SetShaderParameter("spread", spread);
        _iceFlatMaterial.SetShaderParameter("spread", spread);
        _iceShell.Visible = !_flatMap;
        foreach (var sheet in _iceSheets)
            if (IsInstanceValid(sheet)) sheet.Visible = _flatMap;
    }

    private const string IceShader = @"
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, specular_schlick_ggx;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform float spread = 1.0;
uniform vec3 origin = vec3(0.0, 0.0, 1.0);
uniform float flat_map = 0.0;
uniform float scale = 1.0;

varying vec3 local_pos;
varying vec3 camera_local;

vec3 hash3(vec3 p) {
    p = vec3(dot(p, vec3(127.1, 311.7, 74.7)), dot(p, vec3(269.5, 183.3, 246.1)), dot(p, vec3(113.5, 271.9, 124.6)));
    return fract(sin(p) * 43758.5453123);
}

// Distance to the nearest plate centre and to the second nearest; the nearest plate's own random vector.
vec2 plates(vec3 p, out vec3 id) {
    vec3 base = floor(p);
    vec3 f = fract(p);
    float d1 = 8.0;
    float d2 = 8.0;
    id = vec3(0.5);
    for (int z = -1; z <= 1; z++)
    for (int y = -1; y <= 1; y++)
    for (int x = -1; x <= 1; x++) {
        vec3 g = vec3(float(x), float(y), float(z));
        vec3 h = hash3(base + g);
        vec3 r = g + h - f;
        float d = dot(r, r);
        if (d < d1) { d2 = d1; d1 = d; id = h; }
        else if (d < d2) { d2 = d; }
    }
    return vec2(sqrt(d1), sqrt(d2));
}

float noise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = mix(hash3(i).x, hash3(i + vec3(1.0, 0.0, 0.0)).x, f.x);
    float b = mix(hash3(i + vec3(0.0, 1.0, 0.0)).x, hash3(i + vec3(1.0, 1.0, 0.0)).x, f.x);
    float c = mix(hash3(i + vec3(0.0, 0.0, 1.0)).x, hash3(i + vec3(1.0, 0.0, 1.0)).x, f.x);
    float d = mix(hash3(i + vec3(0.0, 1.0, 1.0)).x, hash3(i + vec3(1.0, 1.0, 1.0)).x, f.x);
    return mix(mix(a, b, f.y), mix(c, d, f.y), f.z);
}

float fbm(vec3 p) {
    float sum = 0.0;
    float amplitude = 0.5;
    for (int i = 0; i < 4; i++) {
        sum += amplitude * noise(p);
        p = p * 2.07 + vec3(1.7, 9.2, 4.1);
        amplitude *= 0.5;
    }
    return sum;
}

void vertex() {
    local_pos = VERTEX;
    camera_local = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
}

void fragment() {
    vec3 n_local = flat_map > 0.5 ? vec3(0.0, 0.0, 1.0) : normalize(local_pos);
    vec3 dir;
    if (flat_map > 0.5) {
        float lon = UV.x * 6.2831853;
        float lat = (0.5 - UV.y) * 3.1415927;
        dir = vec3(cos(lat) * sin(lon), sin(lat), cos(lat) * cos(lon));
    } else {
        dir = n_local;
    }
    // The front of the freeze, running out from where it began.
    float reach = spread * 3.35;
    float angle = acos(clamp(dot(dir, normalize(origin)), -1.0, 1.0));
    float coverage = 1.0 - smoothstep(reach - 0.10, reach, angle);
    if (coverage <= 0.001) discard;
    float rim = smoothstep(reach - 0.22, reach - 0.04, angle) * coverage * (1.0 - step(1.0, spread));

    vec3 view_local = normalize(camera_local - local_pos);
    vec3 p = local_pos * scale;

    // The surface: plates and the cracks between them.
    vec3 plate;
    vec2 near = plates(p * 2.3, plate);
    float crack = 1.0 - smoothstep(0.0, 0.05, near.y - near.x);
    // Deeper in the ice, a second net seen through the first; it slides as the eye moves.
    vec3 deep_plate;
    vec2 deep = plates((local_pos - view_local * 0.045) * scale * 3.1 + vec3(5.3), deep_plate);
    float deep_crack = 1.0 - smoothstep(0.0, 0.08, deep.y - deep.x);
    vec3 bubble_cell;
    vec2 bubbles = plates((local_pos - view_local * 0.02) * scale * 26.0, bubble_cell);
    float bubble = (1.0 - smoothstep(0.03, 0.09, bubbles.x)) * step(0.82, bubble_cell.z);

    float frost = fbm(p * 7.5 + plate * 3.0);
    float scratches = fbm(vec3(p.x * 46.0, p.y * 9.0, p.z * 46.0));
    float fresnel = pow(1.0 - clamp(dot(n_local, view_local), 0.0, 1.0), 4.0);

    // The ground underneath, bent by each plate and drained of colour.
    vec2 bend = (plate.xy - 0.5) * 0.010 + (frost - 0.5) * 0.008;
    vec3 under = textureLod(screen_tex, SCREEN_UV + bend, 2.0).rgb;
    float lum = dot(under, vec3(0.299, 0.587, 0.114));
    vec3 cold = mix(vec3(lum), under, 0.22) * vec3(0.70, 0.86, 1.0) + vec3(0.035, 0.075, 0.11);
    cold = mix(cold, vec3(0.30, 0.56, 0.70), deep_crack * 0.40);
    cold += vec3(0.75, 0.88, 0.95) * bubble * 0.35;

    float white = clamp(smoothstep(0.46, 0.80, frost) * 0.62 + crack * 0.85 + scratches * 0.08 +
        fresnel * 0.60 + rim * 0.9, 0.0, 1.0);

    // Each plate tilted a little, so the sun finds them one at a time.
    vec3 tilt = (plate - 0.5) * 0.34;
    vec3 bent = normalize(n_local + tilt - dot(tilt, n_local) * n_local);
    NORMAL = normalize((VIEW_MATRIX * MODEL_MATRIX * vec4(bent, 0.0)).xyz);

    ALBEDO = vec3(0.86, 0.93, 0.98) * white;
    EMISSION = cold * (1.0 - white);
    ROUGHNESS = mix(0.05, 0.55, white);
    SPECULAR = 0.9;
    METALLIC = 0.0;
    ALPHA = coverage * mix(0.82, 0.96, white);
}
";
}
}
