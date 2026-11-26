using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DealOrForce;

/// <summary>
/// What is happening in the world, where it happens: a mark on the map for each public event of
/// weight - a war begun, shooting on a border, a coup, a strike or a march, a bloc split or
/// signed, spies caught, a state born - with a ring that pulses out of it while it is news and
/// fades as it stops being.
///
/// The news was a list on a panel and the map stood still; a player watching the world could
/// not see that the Bosporus was in crisis or that a capital had fallen without reading. Each
/// mark stands at the capital it concerns - between the two capitals, for a quarrel between
/// neighbours - lies on the ground like the borders do, keeps a few pixels' size at any distance,
/// and says what happened when the cursor rests on it. The kinds and their look are below; a kind
/// not listed is not marked. "map_event_pulses" switches them off.
/// </summary>
public sealed partial class GlobePresentation
{
    private sealed record PulseStyle(string Icon, Color Tint, int Days);

    /// <summary>What is marked, and how: the icon, the colour, how many game days it stays news.</summary>
    private static readonly Dictionary<string, PulseStyle> PulseStyles = new(StringComparer.Ordinal)
    {
        ["secession"] = new("nation", new Color(0.9f, 0.72f, 0.32f), 75),
        ["secession_contested"] = new("war", new Color(0.86f, 0.28f, 0.2f), 90),
        ["civil_war"] = new("unrest", new Color(0.78f, 0.16f, 0.22f), 90),
        ["war_started"] = new("war", new Color(0.86f, 0.24f, 0.18f), 75),
        ["world_war_began"] = new("war", new Color(0.95f, 0.15f, 0.1f), 180),
        ["border_war"] = new("war", new Color(0.88f, 0.36f, 0.2f), 60),
        ["tension_clashes"] = new("battle", new Color(0.92f, 0.5f, 0.2f), 40),
        ["tension_crisis"] = new("alert_battle", new Color(0.9f, 0.68f, 0.28f), 30),
        ["world_incident"] = new("alert_battle", new Color(0.92f, 0.45f, 0.22f), 35),
        ["coup"] = new("unrest", new Color(0.8f, 0.2f, 0.32f), 60),
        ["institutional_coup"] = new("unrest", new Color(0.8f, 0.2f, 0.32f), 60),
        ["civil_war_escalated"] = new("unrest", new Color(0.82f, 0.18f, 0.22f), 60),
        ["regime_change"] = new("government", new Color(0.82f, 0.62f, 0.3f), 45),
        ["march_toppled"] = new("unrest", new Color(0.86f, 0.3f, 0.3f), 60),
        ["march_concessions"] = new("movement_support", new Color(0.86f, 0.66f, 0.3f), 40),
        ["march_failed"] = new("movement_support", new Color(0.7f, 0.6f, 0.45f), 30),
        ["general_strike"] = new("movement_support", new Color(0.9f, 0.7f, 0.3f), 42),
        ["political_scandal"] = new("alert_event", new Color(0.82f, 0.64f, 0.35f), 30),
        ["sovereign_default"] = new("debt_default", new Color(0.8f, 0.4f, 0.3f), 45),
        ["organization_split"] = new("alliance", new Color(0.45f, 0.62f, 0.86f), 45),
        ["organization_founded"] = new("alliance", new Color(0.5f, 0.72f, 0.9f), 40),
        ["organization_expelled"] = new("alliance", new Color(0.86f, 0.42f, 0.32f), 40),
        ["organization_dissolved"] = new("alliance", new Color(0.62f, 0.62f, 0.66f), 40),
        ["organization_left"] = new("alliance", new Color(0.7f, 0.68f, 0.62f), 30),
        ["organization_expelled_vote"] = new("alliance", new Color(0.86f, 0.42f, 0.32f), 40),
        ["organization_suspended_aggression"] = new("alliance", new Color(0.86f, 0.52f, 0.32f), 35),
        ["organization_rival_bloc"] = new("alliance", new Color(0.45f, 0.62f, 0.86f), 45),
        ["alliance_ended"] = new("alliance", new Color(0.78f, 0.5f, 0.38f), 30),
        ["spy_network_exposed"] = new("search", new Color(0.62f, 0.72f, 0.82f), 25),
        ["covert_sabotage_exposed"] = new("search", new Color(0.86f, 0.5f, 0.36f), 35),
        ["covert_unrest_exposed"] = new("search", new Color(0.86f, 0.5f, 0.36f), 35),
        ["covert_opposition_exposed"] = new("search", new Color(0.86f, 0.5f, 0.36f), 35),
        ["covert_theft_exposed"] = new("search", new Color(0.86f, 0.5f, 0.36f), 35),
        // Told only to the country concerned (NewsFeed): our own operations, and fires nobody can explain.
        ["covert_sabotage_done"] = new("industry", new Color(0.62f, 0.72f, 0.82f), 25),
        ["covert_unrest_done"] = new("unrest", new Color(0.62f, 0.72f, 0.82f), 25),
        ["covert_opposition_done"] = new("movement_fundraise", new Color(0.62f, 0.72f, 0.82f), 25),
        ["covert_theft_done"] = new("research", new Color(0.62f, 0.72f, 0.82f), 25),
        ["covert_sabotage_suspected"] = new("industry", new Color(0.88f, 0.52f, 0.3f), 30),
        ["covert_unrest_suspected"] = new("unrest", new Color(0.88f, 0.52f, 0.3f), 30),
        ["covert_opposition_suspected"] = new("movement_fundraise", new Color(0.88f, 0.52f, 0.3f), 30),
        ["state_formed"] = new("nation", new Color(0.92f, 0.78f, 0.38f), 60),
        ["peaceful_independence"] = new("nation", new Color(0.7f, 0.82f, 0.5f), 60),
        ["accession_signed"] = new("treaty", new Color(0.82f, 0.76f, 0.5f), 30),
        ["peace_signed"] = new("treaty", new Color(0.62f, 0.82f, 0.58f), 45),
        // The nuclear alert (NuclearPosture): a weapon used stays news for a year.
        ["nuclear_strike"] = new("war", new Color(1f, 0.92f, 0.7f), 365),
        ["nuclear_retaliation"] = new("war", new Color(1f, 0.92f, 0.7f), 365),
        ["nuclear_devastation"] = new("war", new Color(1f, 0.85f, 0.6f), 730),
        ["nuclear_test"] = new("alert_battle", new Color(0.95f, 0.78f, 0.35f), 45),
        ["nuclear_incident"] = new("alert_battle", new Color(0.95f, 0.5f, 0.25f), 30),
        ["nuclear_alert"] = new("alert_battle", new Color(0.98f, 0.42f, 0.18f), 45),
        ["arms_control_signed"] = new("treaty", new Color(0.56f, 0.82f, 0.74f), 45),
        // Strategic megaprojects & supply-chain chokepoint shocks (Wave 2/5).
        ["grand_project_started"] = new("industry", new Color(0.92f, 0.76f, 0.34f), 40),
        ["grand_project_completed"] = new("industry", new Color(0.54f, 0.88f, 0.62f), 60),
        ["grand_project_sabotaged"] = new("alert_battle", new Color(0.92f, 0.38f, 0.22f), 45),
        ["chokepoint_weaponized"] = new("alert_event", new Color(0.94f, 0.52f, 0.20f), 45),
        ["battle_major"] = new("battle", new Color(0.94f, 0.26f, 0.18f), 30),
    };

    private const int PulseLimit = 48;
    private const float PulseSize = 0.05f;
    private float _pulseQualityTier;

    private int ActivePulseCap => _pulseQualityTier > 1.5f ? 12 : _pulseQualityTier > 0.5f ? 20 : PulseLimit;

    private void ApplyEventPulseQualityTier(float tier)
    {
        _pulseQualityTier = tier;
        for (var i = 0; i < _pulses.Count; i++)
            _pulses[i].Material?.SetShaderParameter("quality_tier", tier);
    }

    private sealed class EventPulse
    {
        public long Sequence;
        public int Day;
        public string Kind = "";
        public PulseStyle Style;
        public float Latitude, Longitude;
        public MeshInstance3D Node;
        public ShaderMaterial Material;
        public int LastToday = int.MinValue;
        public float LastZoom = float.NaN;
        public bool LastFlat;
        public float LastFade = float.NaN;
        public float LastPhase = float.NaN;
    }

    private bool _eventPulsesShown = true;
    private Node3D _pulseRoot;
    private readonly List<EventPulse> _pulses = new();
    private long _pulseSeen = -1;
    private QuadMesh _pulseQuad;
    private Shader _pulseShader;
    private readonly Dictionary<string, Texture2D> _pulseIcons = new(StringComparer.Ordinal);

    private const string PulseShaderCode = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;
uniform sampler2D icon : source_color, filter_linear_mipmap;
uniform vec4 tint : source_color = vec4(1.0);
uniform float phase = 0.0;
uniform float fade = 1.0;
uniform float quality_tier = 0.0;
void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float r = length(p);
    if (r > 1.02) discard;
    float wave1 = fract(phase);
    float wave2 = fract(phase + 0.5);
    float ring1 = smoothstep(0.068, 0.0, abs(r - mix(0.42, 0.98, wave1))) * (1.0 - wave1);
    float ring2 = quality_tier < 1.5 ? smoothstep(0.055, 0.0, abs(r - mix(0.42, 0.90, wave2))) * (1.0 - wave2) * 0.65 : 0.0;
    float ring = max(ring1, ring2);
    // Crisp directional crisis sparks on DesktopHigh only (zero extra samplers).
    float sparks = 0.0;
    if (quality_tier < 0.5) {
        float ang = atan(p.y, p.x);
        float rays = pow(max(cos(ang * 8.0 + phase * 3.14159), 0.0), 6.0);
        sparks = rays * smoothstep(0.42, 0.56, r) * (1.0 - smoothstep(0.56, 0.86, r)) * (1.0 - wave1) * 0.42;
    }
    float disc = smoothstep(0.43, 0.39, r);
    float inner_bevel = smoothstep(0.03, 0.0, abs(r - 0.34));
    float rim = smoothstep(0.045, 0.0, abs(r - 0.41));
    vec2 iuv = clamp(p / 0.56 * 0.5 + 0.5, 0.0, 1.0);
    float glyph = texture(icon, iuv).a * step(max(abs(p.x), abs(p.y)), 0.28);
    vec3 dark_core = mix(vec3(0.022, 0.032, 0.046), vec3(0.065, 0.085, 0.115), 1.0 - r * 1.5);
    vec3 col = mix(dark_core, tint.rgb * 1.16, max(rim, glyph));
    col = mix(col, tint.rgb * 0.78, inner_bevel * 0.48);
    col = mix(col, tint.rgb * 1.24 + vec3(0.14, 0.10, 0.04), clamp(ring + sparks, 0.0, 1.0) * (1.0 - disc));
    ALBEDO = col;
    ALPHA = clamp(max(disc * 0.92, clamp(ring * 0.88 + sparks * 0.72, 0.0, 1.0)), 0.0, 1.0) * fade;
}";

    /// <summary>The events marked on the map, or not.</summary>
    public bool EventPulsesShown
    {
        get => _eventPulsesShown;
        set
        {
            _eventPulsesShown = value;
            if (_pulseRoot is not null && IsInstanceValid(_pulseRoot)) _pulseRoot.Visible = value;
        }
    }

    private void CreateEventPulses(Node3D world)
    {
        _pulseRoot = new Node3D { Name = "EventPulses", Visible = _eventPulsesShown };
        world.AddChild(_pulseRoot);
        _pulseQuad = new QuadMesh { Size = Vector2.One };
        _pulseShader = new Shader { Code = PulseShaderCode };
    }

    private Texture2D PulseIcon(string icon)
    {
        if (_pulseIcons.TryGetValue(icon, out var known)) return known;
        var path = Simulation.SpriteCatalog.TextureOf("icon_" + icon, $"res://game/gfx/interface/icons/icon_{icon}.png");
        var texture = !string.IsNullOrEmpty(path) && ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
        _pulseIcons[icon] = texture;
        return texture;
    }

    private void UpdateEventPulses(double delta)
    {
        if (_pulseRoot is null || !IsInstanceValid(_pulseRoot) || _campaign is null) return;
        if (!_mapApplied) return;
        if (!Simulation.WorldLayers.On("event_pulses")) { _pulseRoot.Visible = false; return; }
        TakeNewEvents();
        var today = _campaign.ElapsedDays;
        var zoom = _flatMap ? Mathf.Clamp(_flatZoom / 1.6f, 0.45f, 2f) : Mathf.Clamp(_distance / 9f, 0.45f, 2f);
        var wideLodFade = 1f - Mathf.SmoothStep(15.5f, 22.0f, ViewDistance) * 0.55f;
        var time = (float)Time.GetTicksMsec() / 1000f;
        var phaseStep = _pulseQualityTier >= 0.5f ? 0.02f : 0.001f;
        for (var i = _pulses.Count - 1; i >= 0; i--)
        {
            var pulse = _pulses[i];
            var age = (today - pulse.Day) / (float)Math.Max(1, pulse.Style.Days);
            if (age >= 1f || !IsInstanceValid(pulse.Node))
            {
                if (IsInstanceValid(pulse.Node)) pulse.Node.QueueFree();
                _pulses.RemoveAt(i);
                continue;
            }
            var highPriority = pulse.Kind is "world_war_began" or "nuclear_strike" or "nuclear_retaliation" or "nuclear_devastation";
            var lodAlpha = highPriority ? 1f : wideLodFade;
            if (pulse.LastToday != today || pulse.LastFlat != _flatMap ||
                !float.IsFinite(pulse.LastZoom) || MathF.Abs(zoom - pulse.LastZoom) > 1e-4f)
            {
                pulse.LastToday = today;
                pulse.LastFlat = _flatMap;
                pulse.LastZoom = zoom;
                var position = _flatMap
                    ? LocalFlatPoint(pulse.Latitude, pulse.Longitude, FlatMarkerZ + 0.001f)
                    : LocalSurfacePoint(pulse.Latitude, pulse.Longitude, SurfacePulseRadius());
                var up = _flatMap ? Vector3.Back : position.Normalized();
                var east = up.Cross(Vector3.Up);
                if (east.LengthSquared() < 1e-6f) east = Vector3.Right;
                east = east.Normalized();
                var north = east.Cross(up).Normalized();
                // A quad faces +Z: lay it on the ground, its face along the local up.
                var size = PulseSize * zoom * (1f + 0.25f * (1f - age)) * (highPriority ? 1.18f : 1f);
                pulse.Node.Transform = new Transform3D(new Basis(east * size, north * size, up * size), position);
            }
            var phase = time * 0.6f + pulse.Sequence * 0.37f;
            if (!float.IsFinite(pulse.LastPhase) || MathF.Abs(phase - pulse.LastPhase) >= phaseStep)
            {
                pulse.LastPhase = phase;
                pulse.Material.SetShaderParameter("phase", phase);
            }
            var fade = (1f - Mathf.SmoothStep(0.65f, 1f, age)) * lodAlpha;
            if (!float.IsFinite(pulse.LastFade) || MathF.Abs(fade - pulse.LastFade) > 0.001f || fade <= 0f || fade >= 1f)
            {
                pulse.LastFade = fade;
                pulse.Material.SetShaderParameter("fade", fade);
            }
        }
    }

    private float SurfacePulseRadius() => GlobeRadius * 1.0024f;

    /// <summary>Every public event of weight since the last look becomes a mark, the oldest giving way past the limit.</summary>
    private void TakeNewEvents()
    {
        var entries = _campaign.Chronicle.Entries;
        if (entries.Count == 0) return;
        if (_pulseSeen < 0)
        {
            // A campaign just opened or loaded: what happened in the last weeks is still news.
            _pulseSeen = entries[0].Sequence - 1;
        }
        var maxSeq = Math.Max(entries[0].Sequence, entries[^1].Sequence);
        if (maxSeq <= _pulseSeen) return;
        var cap = ActivePulseCap;
        for (var e = 0; e < entries.Count; e++)
        {
            var entry = entries[e];
            if (entry.Sequence <= _pulseSeen) continue;
            _pulseSeen = entry.Sequence;
            // The covert outcomes only their own country is told of are marked for it too, as the
            // news rail shows them (NewsFeed): nobody else's private entries.
            var told = !entry.Public && entry.ActorId == _campaign.Player?.Id && Simulation.CovertOperations.TellsActor(entry.Kind);
            if (!entry.Public && !told || !PulseStyles.TryGetValue(entry.Kind, out var style)) continue;
            if (_campaign.ElapsedDays - entry.Day >= style.Days) continue;
            if (!PulsePlace(entry, out var latitude, out var longitude)) continue;
            // One mark per place and kind: a second clash on the same border renews the first (zero LINQ alloc).
            EventPulse same = null;
            for (var p = 0; p < _pulses.Count; p++)
            {
                var existing = _pulses[p];
                if (existing.Style == style && MathF.Abs(existing.Latitude - latitude) < 0.5f && MathF.Abs(existing.Longitude - longitude) < 0.5f)
                {
                    same = existing;
                    break;
                }
            }
            if (same is not null) { same.Day = entry.Day; same.Sequence = entry.Sequence; same.Kind = entry.Kind; continue; }
            while (_pulses.Count >= cap && _pulses.Count > 0)
            {
                var oldestIndex = 0;
                for (var p = 1; p < _pulses.Count; p++)
                    if (_pulses[p].Day < _pulses[oldestIndex].Day) oldestIndex = p;
                var oldest = _pulses[oldestIndex];
                if (IsInstanceValid(oldest.Node)) oldest.Node.QueueFree();
                _pulses.RemoveAt(oldestIndex);
            }
            var material = new ShaderMaterial { Shader = _pulseShader, RenderPriority = 6 };
            material.SetShaderParameter("tint", style.Tint);
            material.SetShaderParameter("quality_tier", _pulseQualityTier);
            var icon = PulseIcon(style.Icon);
            if (icon is not null) material.SetShaderParameter("icon", icon);
            var node = new MeshInstance3D { Name = "Pulse_" + entry.Kind, Mesh = _pulseQuad, MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _pulseRoot.AddChild(node);
            _pulses.Add(new EventPulse { Sequence = entry.Sequence, Day = entry.Day, Kind = entry.Kind, Style = style, Latitude = latitude, Longitude = longitude, Node = node, Material = material });
        }
    }

    private bool CovertPlace(Simulation.ChronicleEntry entry, out float latitude, out float longitude)
    {
        latitude = longitude = 0f;
        var place = entry.Place ?? "";
        if (place.Length > 0)
        {
            var id = place.StartsWith("region.", StringComparison.Ordinal) && place.EndsWith(".name", StringComparison.Ordinal)
                ? place.Substring(7, place.Length - 12) : null;
            var region = _campaign.States.Find(s => id is not null ? s.Id == id : s.Name == place);
            if (region is not null)
            {
                (latitude, longitude) = Simulation.Movements.CentreOf(_campaign, region);
                if (latitude != 0f || longitude != 0f) return true;
            }
        }
        // Done by us abroad: where it was done; suspected at home: at home.
        var at = entry.Kind.EndsWith("_done", StringComparison.Ordinal) || entry.Kind == "covert_operation_failed"
            ? _campaign.NationById(entry.TargetId) : _campaign.NationById(entry.ActorId);
        if (at is null || at.CapitalLatitude == 0f && at.CapitalLongitude == 0f) return false;
        latitude = at.CapitalLatitude;
        longitude = at.CapitalLongitude;
        return true;
    }

    /// <summary>Where an event stands: between two neighbouring capitals, else at the capital it concerns.</summary>
    private bool PulsePlace(Simulation.ChronicleEntry entry, out float latitude, out float longitude)
    {
        latitude = longitude = 0f;
        // A covert operation is marked where it was aimed: the region, else the country it was run in.
        if (entry.Kind.StartsWith("covert_", StringComparison.Ordinal) && CovertPlace(entry, out latitude, out longitude)) return true;
        var actor = _campaign.NationById(entry.ActorId);
        var target = _campaign.NationById(entry.TargetId);
        // A movement has no capital of its own worth marking: its target country's is the place.
        if (actor?.Movement is not null && target is not null) (actor, target) = (target, null);
        if (actor is null && target is null) return false;
        if (actor is not null && target is not null && target.Movement is null)
        {
            var dLon = target.CapitalLongitude - actor.CapitalLongitude;
            while (dLon > 180f) dLon -= 360f;
            while (dLon < -180f) dLon += 360f;
            if (MathF.Abs(target.CapitalLatitude - actor.CapitalLatitude) + MathF.Abs(dLon) < 18f)
            {
                latitude = (actor.CapitalLatitude + target.CapitalLatitude) * 0.5f;
                longitude = actor.CapitalLongitude + dLon * 0.5f;
                return true;
            }
            // Far apart: the mark stands where the blow fell, at the target.
            if (entry.Kind is "war_started" or "border_war" or "world_war_began") actor = target;
        }
        var at = actor ?? target;
        if (at.CapitalLatitude == 0f && at.CapitalLongitude == 0f) return false;
        latitude = at.CapitalLatitude;
        longitude = at.CapitalLongitude;
        return true;
    }

    /// <summary>The news a mark stands for, when the cursor rests on it.</summary>
    private string EventPulseTooltip(Vector2 atPosition)
    {
        if (!_eventPulsesShown || _pulses.Count == 0 || _camera is null) return "";
        EventPulse best = null;
        var bestDistance = 18f;
        foreach (var pulse in _pulses)
        {
            if (!IsInstanceValid(pulse.Node) || _camera.IsPositionBehind(pulse.Node.GlobalPosition)) continue;
            if (!_flatMap && pulse.Node.GlobalPosition.Dot(_camera.GlobalPosition - pulse.Node.GlobalPosition) < 0f) continue;
            var distance = _camera.UnprojectPosition(pulse.Node.GlobalPosition).DistanceTo(atPosition);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = pulse;
        }
        if (best is null) return "";
        var line = Simulation.NewsFeed.Lines(_campaign).FirstOrDefault(l => l.Sequence == best.Sequence);
        if (line is null) return "";
        return Simulation.Loc.Format("{0} days ago", _campaign.ElapsedDays - best.Day) + "\n" + line.Text;
    }
}
