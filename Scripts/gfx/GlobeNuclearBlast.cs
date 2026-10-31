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
uniform float seed = 0.0;
float hash(vec3 p) { p = fract(p * 0.3183099 + seed); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float noise(vec3 x) {
    vec3 i = floor(x); vec3 f = fract(x); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash(i), hash(i + vec3(1,0,0)), f.x), mix(hash(i + vec3(0,1,0)), hash(i + vec3(1,1,0)), f.x), f.y),
               mix(mix(hash(i + vec3(0,0,1)), hash(i + vec3(1,0,1)), f.x), mix(hash(i + vec3(0,1,1)), hash(i + vec3(1,1,1)), f.x), f.y), f.z);
}
void fragment() {
    vec3 p = (inverse(VIEW_MATRIX) * vec4(VERTEX, 1.0)).xyz;
    float n = noise(p * 30.0 + vec3(0.0, -TIME * 0.8, 0.0)) * 0.55 + noise(p * 75.0 + TIME * 0.4) * 0.45;
    float under = clamp(-NORMAL.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 c = mix(smoke.rgb * (0.7 + 0.5 * n), lit.rgb * (0.8 + 0.6 * n), clamp(glow * (0.4 + under * 0.8), 0.0, 1.0));
    ALBEDO = c;
    ALPHA = fade * smoothstep(0.18, 0.55, n + 0.25);
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
    ALBEDO = mix(tint.rgb, vec3(1.0, 0.35, 0.08), scar);
    ALPHA = clamp(ring * fade + ember, 0.0, 1.0);
}";

    private sealed class Blast
    {
        public float Latitude, Longitude;
        public ulong Start;
        public Node3D Root;
        public MeshInstance3D Fireball, Stem, Cap, Ring;
        public ShaderMaterial FireMaterial, StemMaterial, CapMaterial, RingMaterial;
        public OmniLight3D Flash;
    }

    private readonly List<Blast> _blasts = new();
    private Shader _blastShader, _smokeShader, _ringShader;

    /// <summary>Seconds the whole sequence runs: flash, fireball, cloud, scar.</summary>
    private const float BlastSeconds = 14f;

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
            var unit = 0.07f * zoom;
            blast.Root.Transform = new Transform3D(new Basis(east, up, -north), position);

            // The flash: a light that whitens the hemisphere for a heartbeat.
            blast.Flash.LightEnergy = 40f * Mathf.Exp(-t * 4.5f);
            blast.Flash.OmniRange = unit * 30f;

            // The fireball: grows in the first second, cools from white to soot, sinks into the cloud.
            var ball = unit * (0.35f + 0.9f * Mathf.Clamp(t / 1.1f, 0f, 1f));
            blast.Fireball.Scale = new Vector3(ball, ball * 0.92f, ball);
            blast.Fireball.Position = new Vector3(0, ball * 0.5f + unit * 1.6f * Mathf.SmoothStep(1f, 6f, t), 0);
            blast.FireMaterial.SetShaderParameter("heat", Mathf.Clamp(1.3f - t * 0.32f, 0f, 1.3f));
            blast.FireMaterial.SetShaderParameter("fade", 1f - Mathf.SmoothStep(4f, 8f, t));

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
            }

            // The shock ring races out over the first three seconds; the ember scar lingers.
            var ringSize = unit * 9f;
            blast.Ring.Scale = new Vector3(ringSize, ringSize, 1f);
            blast.RingMaterial.SetShaderParameter("radius", Mathf.Clamp(t / 3f, 0.02f, 1f));
            blast.RingMaterial.SetShaderParameter("width", 0.12f * (1f - Mathf.Clamp(t / 3f, 0f, 0.8f)));
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
