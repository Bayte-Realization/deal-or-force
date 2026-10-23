using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The console curses as the player meets them (Simulation.WorldCurses).
///
/// `snowgrave` lays the ice over the map (GlobeIce) and a rime of frost round the edge of the
/// screen, over the interface too, since nothing in it will move again. `weirdroute` keeps a
/// count at the top of the screen of the formations that have walked into the water, with a
/// splash for each that the cue's cooldown lets through. A weird route with no water is static:
/// the whole picture, every frame, with its hiss - over the campaign and its interface, under
/// the pause menu and the console, so a player can still leave.
///
/// The soundtrack is told to stay silent while a cursed world is up (MusicDirector), and the
/// map's own sound becomes the wind over the ice, or nothing (MapAmbience).
/// </summary>
public partial class DealOrForceGame
{
    private Control _curseLayer;
    private ColorRect _curseFrost, _curseStatic;
    private Label _curseCaption;
    private AudioStreamPlayer _curseNoise;
    private float _curseFrostShown, _curseStaticShown;
    private int _curseDrownedHeard = -1;
    private string _curseShown = "";

    private void BuildWorldCurseLayer()
    {
        _curseLayer = FullLayer("WorldCurse");
        _curseLayer.Visible = false;
        AddChild(_curseLayer);

        _curseFrost = new ColorRect
        {
            Name = "SnowgraveFrost", MouseFilter = MouseFilterEnum.Ignore, Color = Colors.White,
            Material = new ShaderMaterial { Shader = new Shader { Code = FrostShader } }
        };
        _curseLayer.AddChild(_curseFrost);
        _curseFrost.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _curseStatic = new ColorRect
        {
            Name = "WeirdRouteStatic", MouseFilter = MouseFilterEnum.Stop, Color = Colors.White,
            Material = new ShaderMaterial { Shader = new Shader { Code = StaticShader } }
        };
        _curseLayer.AddChild(_curseStatic);
        _curseStatic.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _curseCaption = new Label
        {
            Name = "CurseCaption", MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _curseCaption.AddThemeFontSizeOverride("font_size", 17);
        _curseCaption.AddThemeColorOverride("font_color", new Color("d9e2e8"));
        _curseCaption.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, .85f));
        _curseCaption.AddThemeConstantOverride("outline_size", 6);
        _curseLayer.AddChild(_curseCaption);
        _curseCaption.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
        _curseCaption.OffsetLeft = -420; _curseCaption.OffsetRight = 420;
        _curseCaption.OffsetTop = 150; _curseCaption.OffsetBottom = 180;

        _curseNoise = new AudioStreamPlayer { Name = "CurseStatic", VolumeDb = -80f };
        const string noise = "res://game/sound/audio/curses/curse_static.ogg";
        if (ResourceLoader.Exists(noise) && GD.Load<AudioStream>(noise) is AudioStream stream)
        {
            stream = (AudioStream)stream.Duplicate();
            if (stream is AudioStreamOggVorbis ogg) ogg.Loop = true;
            _curseNoise.Stream = stream;
        }
        _curseLayer.AddChild(_curseNoise);
    }

    /// <summary>Every frame, paused or not: what the curse looks and sounds like now.</summary>
    private void UpdateWorldCurse(double delta)
    {
        if (_curseLayer is null) return;
        var curse = _campaign is not null && _hudLayer is { Visible: true } ? _campaign.Curse : "";
        if (curse != _curseShown)
        {
            // A curse already in the save is there at once; one cast now arrives.
            var instant = _curseShown.Length == 0 && _curseDrownedHeard < 0;
            _curseShown = curse;
            if (instant) { _curseFrostShown = curse == WorldCurses.Snowgrave ? 1f : 0f; _curseStaticShown = curse == WorldCurses.Static ? 1f : 0f; }
            _curseDrownedHeard = _campaign?.CurseDrowned ?? 0;
        }
        if (curse.Length == 0 && _curseFrostShown <= 0f && _curseStaticShown <= 0f)
        {
            if (_curseLayer.Visible) _curseLayer.Visible = false;
            if (_curseNoise.Playing) _curseNoise.Stop();
            _curseDrownedHeard = -1;
            return;
        }
        _curseLayer.Visible = true;
        var step = (float)delta;
        _curseFrostShown = Mathf.MoveToward(_curseFrostShown, curse == WorldCurses.Snowgrave ? 1f : 0f, step / 6f);
        _curseStaticShown = Mathf.MoveToward(_curseStaticShown, curse == WorldCurses.Static ? 1f : 0f, step / 1.5f);
        ((ShaderMaterial)_curseFrost.Material).SetShaderParameter("presence", _curseFrostShown);
        ((ShaderMaterial)_curseStatic.Material).SetShaderParameter("presence", _curseStaticShown);
        _curseFrost.Visible = _curseFrostShown > 0.001f;
        _curseStatic.Visible = _curseStaticShown > 0.001f;

        var level = Mathf.Clamp(SettingsCatalog.Number("volume_master") * SettingsCatalog.Number("volume_effects"), 0f, 1f) *
                    _curseStaticShown;
        if (level > 0.0005f && _curseNoise.Stream is not null)
        {
            _curseNoise.VolumeDb = Mathf.LinearToDb(level);
            if (!_curseNoise.Playing) _curseNoise.Play();
        }
        else if (_curseNoise.Playing) _curseNoise.Stop();

        _curseCaption.Visible = curse is WorldCurses.WeirdRoute or WorldCurses.Snowgrave;
        if (curse == WorldCurses.WeirdRoute)
        {
            var drowned = _campaign.CurseDrowned;
            _curseCaption.Text = drowned == 0
                ? Loc.Get("* They are on their way to the water.")
                : Loc.Format("* {0} formations have walked into the water.", drowned.ToString("N0"));
            if (drowned > _curseDrownedHeard) PlayStrategicCue("curse_drowned");
            _curseDrownedHeard = drowned;
        }
        else if (curse == WorldCurses.Snowgrave)
            _curseCaption.Text = Loc.Get("* Nothing moves any more.");
    }

    private string ConsoleSnowgrave() => CastWorldCurse(WorldCurses.Snowgrave);
    private string ConsoleWeirdRoute() => CastWorldCurse(WorldCurses.WeirdRoute);

    /// <summary>
    /// Cast a curse from the console. Whatever happens - the ice falls, the march begins, the
    /// static comes, or the world was already past saving - the console says one word, the one
    /// Deltarune says at the start of its own point of no return.
    /// </summary>
    private string CastWorldCurse(string curse)
    {
        if (_campaign is null) return "No campaign.";
        if (IsNetworkClient) return "Only the host's world can be cursed.";
        var before = _campaign.Curse;
        var now = WorldCurses.Cast(_campaign, curse);
        if (now == before) return "PROCEED";
        _curseDrownedHeard = _campaign.CurseDrowned;
        if (now == WorldCurses.Snowgrave)
        {
            var capital = _campaign.Player;
            var (latitude, longitude) = capital is not null && (capital.CapitalLatitude != 0f || capital.CapitalLongitude != 0f)
                ? (capital.CapitalLatitude, capital.CapitalLongitude) : _globe.ViewCentre;
            _globe.BeginIce(latitude, longitude);
            PlayStrategicCue("curse_snowgrave");
        }
        else if (now == WorldCurses.WeirdRoute) PlayStrategicCue("curse_weirdroute");
        return "PROCEED";
    }

    // A rime of frost growing in from the edges of the screen, thickest in the corners.
    private const string FrostShader = @"
shader_type canvas_item;
uniform float presence = 0.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p) {
    vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}
float fbm(vec2 p) {
    float sum = 0.0; float a = 0.5;
    for (int i = 0; i < 5; i++) { sum += a * noise(p); p = p * 2.03 + vec2(3.1, 1.7); a *= 0.5; }
    return sum;
}
void fragment() {
    // SCREEN_UV, not UV: a ColorRect hands its shader no texture coordinates to speak of.
    vec2 uv = SCREEN_UV;
    vec2 size = 1.0 / SCREEN_PIXEL_SIZE;
    vec2 aspect = vec2(size.x / size.y, 1.0);
    vec2 p = uv * aspect;
    vec2 edge = min(uv, 1.0 - uv) * aspect;
    float d = min(edge.x, edge.y);
    float grain = fbm(p * 7.0);
    float fern = fbm(p * 22.0 + grain * 3.0);
    float reach = 0.03 + 0.09 * presence;
    float rime = 1.0 - smoothstep(reach * 0.25, reach, d + (grain - 0.5) * 0.05);
    float crystals = smoothstep(0.55, 0.75, fern) * (1.0 - smoothstep(reach, reach * 1.8, d));
    float a = clamp(rime * (0.45 + grain * 0.4) + crystals * 0.35, 0.0, 1.0) * presence;
    COLOR = vec4(mix(vec3(0.72, 0.84, 0.93), vec3(0.97, 0.99, 1.0), grain), a * 0.85);
}
";

    // Static: a fresh field of noise every frame, torn lines, scanlines and a bar rolling down.
    private const string StaticShader = @"
shader_type canvas_item;
uniform float presence = 0.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
void fragment() {
    float frame = floor(TIME * 24.0);
    vec2 cell = floor(FRAGCOORD.xy / 2.0);
    float n = hash(cell + vec2(frame * 13.1, frame * 7.7));
    float line = (hash(vec2(floor(FRAGCOORD.y / 3.0), frame)) - 0.5) * 0.35;
    float roll = fract(SCREEN_UV.y - TIME * 0.11);
    float bar = smoothstep(0.0, 0.05, roll) * (1.0 - smoothstep(0.05, 0.16, roll));
    float scan = 0.80 + 0.20 * sin(FRAGCOORD.y * 3.14159);
    float v = clamp(n * 0.9 + line + bar * 0.22, 0.0, 1.0) * scan;
    COLOR = vec4(vec3(v), presence);
}
";
}
