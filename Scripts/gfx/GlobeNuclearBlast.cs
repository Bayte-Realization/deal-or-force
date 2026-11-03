using System.Collections.Generic;
using Godot;

namespace DealOrForce;

/// <summary>
/// A nuclear detonation, seen from orbit.
///
/// A strike was the same pulsing "war" mark as a border skirmish. It is now the one thing on the
/// map nobody can look away from: a flash that lights the globe, a fireball that boils from white
/// through orange to soot, a shock ring racing across the ground, a mushroom cloud that climbs and
/// spreads and greys and drifts apart, and an ember scar that glows on after it. All of it is
/// procedural - spheres, a cylinder and quads with small shaders, no textures - timed in real
/// seconds so it reads the same at any game speed, sized with the camera like the event marks so
/// it reads from any distance, and gone when it is over: nothing costs a frame while no bomb falls.
/// Fed by Simulation.NuclearDetonations; "map_event_pulses" off hides it with the other marks.
/// </summary>
public sealed partial class GlobePresentation
{
    private const string BlastShaderCode = @"shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;
uniform vec4 hot : source_color = vec4(1.0, 0.95, 0.8, 1.0);
uniform vec4 cool : source_color = vec4(0.9, 0.25, 0.05, 1.0);
uniform float heat = 1.0;
uniform float fade = 1.0;
uniform float seed = 0.0;
float hash(vec3 p) { p = fract(p * 0.3183099 + seed); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float noise(vec3 x) {
    vec3 i = floor(x); vec3 f = fract(x); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash(i), hash(i + vec3(1,0,0)), f.x), mix(hash(i + vec3(0,1,0)), hash(i + vec3(1,1,0)), f.x), f.y),
               mix(mix(hash(i + vec3(0,0,1)), hash(i + vec3(1,0,1)), f.x), mix(hash(i + vec3(0,1,1)), hash(i + vec3(1,1,1)), f.x), f.y), f.z);
}
void fragment() {
    float n = noise(VERTEX * 18.0 + vec3(0.0, TIME * 1.7, 0.0)) * 0.6 + noise(VERTEX * 41.0 - TIME) * 0.4;
    float rim = pow(1.0 - abs(dot(NORMAL, VIEW)), 1.5);
    vec3 c = mix(cool.rgb, hot.rgb, clamp(heat * (0.55 + 0.6 * n) - rim * 0.3, 0.0, 1.0));
    ALBEDO = c * (1.2 + heat);
    ALPHA = fade * clamp(0.35 + 0.65 * n - rim * 0.25, 0.0, 1.0);
}";

    private const string SmokeShaderCode = @"shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_back;
uniform vec4 lit : source_color = vec4(1.0, 0.55, 0.18, 1.0);
uniform vec4 smoke : source_color = vec4(0.36, 0.33, 0.31, 1.0);
uniform float glow = 1.0;
uniform float fade = 1.0;
uniform float erode = 0.0;
uniform float seed = 0.0;
float hash(vec3 p) { p = fract(p * 0.3183099 + seed); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float noise(vec3 x) {
    vec3 i = floor(x); vec3 f = fract(x); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash(i), hash(i + vec3(1,0,0)), f.x), mix(hash(i + vec3(0,1,0)), hash(i + vec3(1,1,0)), f.x), f.y),
               mix(mix(hash(i + vec3(0,0,1)), hash(i + vec3(1,0,1)), f.x), mix(hash(i + vec3(0,1,1)), hash(i + vec3(1,1,1)), f.x), f.y), f.z);
}
void fragment() {
    vec3 p = (inverse(VIEW_MATRIX) * vec4(VERTEX, 1.0)).xyz;
    // Billows at three scales, rolling outward; the rim thins to nothing so the cloud has no edge
    // line, and erosion eats it into rags as it ages.
    float n = noise(p * 22.0 + vec3(0.0, -TIME * 0.9, 0.0)) * 0.5 + noise(p * 61.0 + TIME * 0.5) * 0.32 + noise(p * 150.0 - TIME) * 0.18;
    float rim = 1.0 - abs(dot(NORMAL, VIEW));
    float under = clamp(-NORMAL.y * 0.5 + 0.5, 0.0, 1.0);
    float shade = 0.55 + 0.75 * n;
    vec3 c = mix(smoke.rgb * shade, lit.rgb * (1.0 + 1.2 * n), clamp(glow * (0.35 + under * 0.9) * (1.2 - rim), 0.0, 1.0));
    ALBEDO = c;
    float body = smoothstep(0.32 + erode * 0.45, 0.62 + erode * 0.3, n + 0.18);
    ALPHA = fade * body * (1.0 - smoothstep(0.55, 0.95, rim));
}";

    // The cloud seen from above, which is how a map sees it: a spreading billow with a hot core
    // and ragged rim, turning as it spreads, eaten into rags as it ages. Flat on purpose - from
    // orbit a mushroom is its cap, and a sphere read as a planet.
    private const string CloudDiskShaderCode = @"shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;
uniform vec4 lit : source_color = vec4(1.0, 0.62, 0.22, 1.0);
uniform vec4 smoke : source_color = vec4(0.42, 0.39, 0.37, 1.0);
uniform float glow = 1.0;
uniform float fade = 1.0;
uniform float erode = 0.0;
uniform float seed = 0.0;
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7)) + seed) * 43758.5453); }
float noise(vec2 x) {
    vec2 i = floor(x); vec2 f = fract(x); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
}
float fbm(vec2 p) { float v = 0.0; float a = 0.5; for (int i = 0; i < 5; i++) { v += a * noise(p); p = p * 2.03 + 7.1; a *= 0.5; } return v; }
void fragment() {
    vec2 c = UV - vec2(0.5);
    float d = length(c) * 2.0;
    float ang = atan(c.y, c.x);
    // Billows that roll outward and turn slowly.
    vec2 q = vec2(cos(ang + d * 1.6 - TIME * 0.18), sin(ang + d * 1.6 - TIME * 0.18)) * d * 3.2;
    float n = fbm(q + vec2(seed, -TIME * 0.25));
    float rim = 0.62 + 0.30 * n - erode * 0.35;
    float body = 1.0 - smoothstep(rim - 0.22, rim, d);
    float rags = smoothstep(erode * 0.75, erode * 0.75 + 0.25, n + 0.15 - d * 0.2);
    float core = (1.0 - smoothstep(0.0, 0.45, d)) * glow;
    vec3 col = mix(smoke.rgb * (0.7 + 0.6 * n), lit.rgb * (1.2 + 0.8 * n), clamp(core * 1.4, 0.0, 1.0));
    col += vec3(1.0, 0.85, 0.6) * pow(core, 3.0) * 0.8;
    ALBEDO = col;
    ALPHA = fade * body * rags * (0.55 + 0.45 * n);
}";

    private const string RingShaderCode = @"shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;
uniform vec4 tint : source_color = vec4(1.0, 0.85, 0.6, 1.0);
uniform float radius = 0.2;
uniform float width = 0.05;
uniform float fade = 1.0;
uniform float scar = 0.0;
void fragment() {
    float d = length(UV - vec2(0.5)) * 2.0;
    float ring = smoothstep(radius - width, radius, d) * (1.0 - smoothstep(radius, radius + width * 0.4, d));
    float ember = scar * (1.0 - smoothstep(0.0, 0.28, d));
    ALBEDO = mix(tint.rgb * 1.6, vec3(1.0, 0.35, 0.08) * 1.4, scar);
    ALPHA = clamp(ring * fade * 1.2 + ember, 0.0, 1.0);
}";

    private sealed class Blast
    {
        public float Latitude, Longitude;
        public ulong Start;
        public Node3D Root;
        public MeshInstance3D Fireball, Stem, Cap, Ring, Cloud;
        public ShaderMaterial FireMaterial, StemMaterial, CapMaterial, RingMaterial, CloudMaterial;
        public OmniLight3D Flash;
    }

    private readonly List<Blast> _blasts = new();
    private Shader _blastShader, _smokeShader, _ringShader, _cloudShader;

    /// <summary>Seconds the whole sequence runs: flash, fireball, cloud, scar.</summary>
    private const float BlastSeconds = 20f;

    private void UpdateNuclearBlasts()
    {
        if (_pulseRoot is null || !IsInstanceValid(_pulseRoot) || _campaign?.ProvinceMap is null) return;
        while (Simulation.NuclearDetonations.TryTake(out var detonation))
        {
            if (_campaign.ElapsedDays - detonation.Day > 7) continue;
            if (detonation.Province > _campaign.ProvinceMap.ProvinceCount) continue;
            var centre = _campaign.ProvinceMap.CenterOf(detonation.Province);
            SpawnBlast(centre.Latitude, centre.Longitude);
        }
        if (_blasts.Count == 0) return;

        var zoom = _flatMap ? Mathf.Clamp(_flatZoom / 1.6f, 0.45f, 2f) : Mathf.Clamp(_distance / 9f, 0.45f, 2f);
        var now = Time.GetTicksMsec();
        for (var index = _blasts.Count - 1; index >= 0; index--)
        {
            var blast = _blasts[index];
            var t = (now - blast.Start) / 1000f;
            if (t >= BlastSeconds || !IsInstanceValid(blast.Root))
            {
                if (IsInstanceValid(blast.Root)) blast.Root.QueueFree();
                _blasts.RemoveAt(index);
                continue;
            }
            var position = _flatMap
                ? FlatPoint(blast.Latitude, blast.Longitude, FlatMarkerZ + 0.002f)
                : SurfacePoint(blast.Latitude, blast.Longitude, GlobeRadius * 1.0026f);
            var up = _flatMap ? Vector3.Back : position.Normalized();
            var east = up.Cross(Vector3.Up);
            if (east.LengthSquared() < 1e-6f) east = Vector3.Right;
            east = east.Normalized();
            var north = east.Cross(up).Normalized();
            var unit = 0.21f * zoom;
            blast.Root.Transform = new Transform3D(new Basis(east, up, -north), position);

            // The flash: a light that whitens the hemisphere for a heartbeat.
            blast.Flash.LightEnergy = 60f * Mathf.Exp(-t * 3.5f);
            blast.Flash.OmniRange = unit * 18f;

            // The fireball: grows in the first second, cools from white to soot, sinks into the cloud.
            var ball = unit * (0.35f + 0.9f * Mathf.Clamp(t / 1.1f, 0f, 1f));
            blast.Fireball.Scale = new Vector3(ball, ball * 0.92f, ball);
            blast.Fireball.Position = new Vector3(0, ball * 0.5f + unit * 1.6f * Mathf.SmoothStep(1f, 6f, t), 0);
            blast.FireMaterial.SetShaderParameter("heat", Mathf.Clamp(1.3f - t * 0.45f, 0f, 1.3f));
            blast.FireMaterial.SetShaderParameter("fade", 1f - Mathf.SmoothStep(1.6f, 3.4f, t));

            // The cloud: a stem that climbs and a cap that spreads, glowing from below, greying, drifting apart.
            var rise = Mathf.SmoothStep(0.6f, 6.5f, t);
            var stemHeight = unit * (0.2f + 2.4f * rise);
            blast.Stem.Scale = new Vector3(unit * 0.32f, stemHeight, unit * 0.32f);
            blast.Stem.Position = new Vector3(0, stemHeight * 0.5f, 0);
            var capWidth = unit * (0.5f + 1.4f * rise + 0.35f * Mathf.SmoothStep(7f, BlastSeconds, t));
            blast.Cap.Scale = new Vector3(capWidth, capWidth * 0.48f, capWidth);
            blast.Cap.Position = new Vector3(0, stemHeight + capWidth * 0.12f, 0);
            var glow = Mathf.Clamp(1.2f - t * 0.16f, 0f, 1f);
            var smokeFade = (1f - Mathf.SmoothStep(9f, BlastSeconds, t)) * Mathf.SmoothStep(0.2f, 1.2f, t);
            foreach (var material in new[] { blast.StemMaterial, blast.CapMaterial })
            {
                material.SetShaderParameter("glow", glow);
                material.SetShaderParameter("fade", smokeFade);
                material.SetShaderParameter("erode", Mathf.SmoothStep(3f, BlastSeconds, t));
            }

            // The cap from above: a billow that spreads from the fireball's place and lingers.
            var spread = unit * (1.2f + 4.2f * Mathf.SmoothStep(0.8f, 9f, t));
            blast.Cloud.Scale = new Vector3(spread, spread, 1f);
            blast.Cloud.Position = new Vector3(0, stemHeight * 0.9f, 0);
            blast.CloudMaterial.SetShaderParameter("glow", Mathf.Clamp(1.1f - t * 0.13f, 0f, 1f));
            blast.CloudMaterial.SetShaderParameter("fade", Mathf.SmoothStep(0.9f, 2.2f, t) * (1f - Mathf.SmoothStep(14f, BlastSeconds, t)));
            blast.CloudMaterial.SetShaderParameter("erode", Mathf.SmoothStep(8f, BlastSeconds, t));
            blast.CapMaterial.SetShaderParameter("fade", smokeFade * 0.35f);

            // The shock ring races out over the first three seconds; the ember scar lingers.
            var ringSize = unit * 7f;
            blast.Ring.Scale = new Vector3(ringSize, ringSize, 1f);
            blast.RingMaterial.SetShaderParameter("radius", Mathf.Clamp(t / 3f, 0.02f, 1f));
            blast.RingMaterial.SetShaderParameter("width", 0.2f * (1f - Mathf.Clamp(t / 3f, 0f, 0.7f)));
            blast.RingMaterial.SetShaderParameter("fade", 1f - Mathf.SmoothStep(1.5f, 3.2f, t));
            blast.RingMaterial.SetShaderParameter("scar", 0.85f * Mathf.SmoothStep(0.3f, 1.5f, t) * (1f - Mathf.SmoothStep(8f, BlastSeconds, t)));
        }
    }

    private void SpawnBlast(float latitude, float longitude)
    {
        _blastShader ??= new Shader { Code = BlastShaderCode };
        _smokeShader ??= new Shader { Code = SmokeShaderCode };
        _ringShader ??= new Shader { Code = RingShaderCode };
        var seed = (float)GD.RandRange(0.0, 10.0);
        var blast = new Blast { Latitude = latitude, Longitude = longitude, Start = Time.GetTicksMsec(), Root = new Node3D { Name = "NuclearBlast" } };
        _pulseRoot.GetParent().AddChild(blast.Root);

        ShaderMaterial Material(Shader shader, int priority)
        {
            var material = new ShaderMaterial { Shader = shader, RenderPriority = priority };
            material.SetShaderParameter("seed", seed);
            return material;
        }
        MeshInstance3D Piece(string name, Mesh mesh, ShaderMaterial material)
        {
            var piece = new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            blast.Root.AddChild(piece);
            return piece;
        }

        blast.RingMaterial = Material(_ringShader, 7);
        // The ring lies on the ground: a quad faces +Z, the root's up is +Y.
        var ringNode = Piece("Shock", new QuadMesh { Size = Vector2.One }, blast.RingMaterial);
        ringNode.RotationDegrees = new Vector3(-90, 0, 0);
        blast.Ring = ringNode;
        blast.StemMaterial = Material(_smokeShader, 8);
        blast.Stem = Piece("Stem", new CylinderMesh { TopRadius = 0.45f, BottomRadius = 0.8f, Height = 1f, RadialSegments = 18, Rings = 3 }, blast.StemMaterial);
        blast.CapMaterial = Material(_smokeShader, 9);
        blast.Cap = Piece("Cap", new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 24, Rings = 12 }, blast.CapMaterial);
        _cloudShader ??= new Shader { Code = CloudDiskShaderCode };
        blast.CloudMaterial = Material(_cloudShader, 11);
        var cloud = Piece("Cloud", new QuadMesh { Size = Vector2.One }, blast.CloudMaterial);
        cloud.RotationDegrees = new Vector3(-90, 0, 0);
        blast.Cloud = cloud;
        blast.FireMaterial = Material(_blastShader, 10);
        blast.Fireball = Piece("Fireball", new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 24, Rings = 12 }, blast.FireMaterial);
        blast.Flash = new OmniLight3D { Name = "Flash", LightColor = new Color(1f, 0.92f, 0.78f), ShadowEnabled = false, Position = new Vector3(0, 0.05f, 0) };
        blast.Root.AddChild(blast.Flash);
        _blasts.Add(blast);
        NuclearFlashed?.Invoke();
    }

    /// <summary>A detonation drawn at a point without anything happening in the world: for showing
    /// the effect, trailers and checking it after a change. Changes nothing in the campaign.</summary>
    public void PreviewNuclearBlast(float latitude, float longitude)
    {
        if (_pulseRoot is null || !IsInstanceValid(_pulseRoot)) return;
        SpawnBlast(latitude, longitude);
    }

    /// <summary>Raised when a detonation is first drawn, for the interface's screen flash.</summary>
    public event System.Action NuclearFlashed;
}
