using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// What the map sounds like from where the camera is.
///
/// The game had sounds for moments - a war declared, a battle won - and silence between
/// them, which over a long campaign is most of the time. This fills the silence from the
/// camera's point of view, the same way the weather fills the sky: from the strategic height
/// a hush of high wind over the world; brought down to the tactical distance the wind falls
/// away into the sound of the ground under the camera - open country, forest, jungle, desert,
/// snow, a city, the sea; and near a battle the player can see, the front beyond the hills.
/// Loops crossfaded slowly, under every cue and at the effects volume.
/// </summary>
public sealed partial class MapAmbience : Node
{
    private const string Folder = "res://game/sound/audio/ambience/";
    private readonly Func<GlobePresentation> _globe;
    private readonly Func<Campaign> _campaign;
    private AudioStreamPlayer _high, _front;
    // Each terrain lends the tactical camera the beds of its ground, a marsh a share of rain;
    // open water is the sea, and ground with no mix of its own is open country. Which bed is
    // which recording, and which terrain sounds like what, is sound/'s `map_ambience` block.
    private readonly Dictionary<string, AudioStreamPlayer> _beds = new();
    private readonly Dictionary<string, float> _ground = new();
    private readonly AudioFocusEnvelope _cueFocus = new();
    private float _frontNear;
    private double _frontCheck;
    public bool AuditionSuppressed { get; set; }

    public void DuckForCue(double seconds, float decibels) =>
        _cueFocus.Request(seconds, Mathf.DbToLinear(decibels));
    public void ClearCueFocus() => _cueFocus.Reset();
    internal float CueFocusGain => _cueFocus.Gain;

    public MapAmbience(Func<GlobePresentation> globe, Func<Campaign> campaign)
    {
        Name = "MapAmbience";
        _globe = globe;
        _campaign = campaign;
    }

    public override void _Ready()
    {
        _high = Loop("wind_high.ogg");
        foreach (var (name, file) in SoundCatalog.AmbienceBeds) _beds[name] = Loop(file, name);
        _front = Loop("front_far.ogg");
    }

    private AudioStreamPlayer Loop(string file) => Loop(Folder + file, file.Replace(".ogg", ""));

    private AudioStreamPlayer Loop(string path, string name)
    {
        var player = new AudioStreamPlayer { Name = "Ambience_" + name, VolumeDb = -80f };
        if (ResourceLoader.Exists(path) && GD.Load<AudioStream>(path) is AudioStream stream)
        {
            // The library and the map may share an imported recording. Only this
            // private playback copy loops; browsing it must not become endless.
            stream = (AudioStream)stream.Duplicate();
            if (stream is AudioStreamOggVorbis ogg) ogg.Loop = true;
            player.Stream = stream;
        }
        AddChild(player);
        return player;
    }

    public override void _Process(double delta)
    {
        _cueFocus.Advance(delta);
        var level = SettingsCatalog.Number("volume_master") * SettingsCatalog.Number("volume_effects") *
                    (SettingsCatalog.Flag("map_ambience") ? 1f : 0f);
        if (AuditionSuppressed || level <= .0005f)
        {
            // Keep the loops' phases; audition must not restart or randomize the map's sound.
            Silence(_high, !AuditionSuppressed);
            foreach (var bed in _beds.Values) Silence(bed, !AuditionSuppressed);
            Silence(_front, !AuditionSuppressed);
            return;
        }
        var globe = _globe();
        var campaign = _campaign();
        level *= _cueFocus.Gain;
        // A cursed world (WorldCurses): under the ice only the wind over it, at every height;
        // the static has its own sound, and the ground none.
        if (campaign?.Curse is WorldCurses.Snowgrave or WorldCurses.Static)
        {
            var hush = 1f - MathF.Exp(-(float)delta * 1.5f);
            var wind = campaign.Curse == WorldCurses.Snowgrave && GodotObject.IsInstanceValid(globe) && globe.IsVisibleInTree();
            Mix(_high, 0f, hush);
            Mix(_front, 0f, hush);
            foreach (var (name, player) in _beds)
                Mix(player, wind && name == SoundCatalog.FrozenBed ? 0.6f * level : 0f, hush);
            return;
        }
        var live = campaign is not null && GodotObject.IsInstanceValid(globe) && globe.IsVisibleInTree() && level > 0.0005f;

        var height = live ? globe.ViewHeight : 12f;
        // The wind belongs to the strategic camera, the country to the tactical one.
        var high = Mathf.SmoothStep(7.0f, 11.0f, height);
        var land = 1f - Mathf.SmoothStep(6.2f, 8.6f, height);

        _frontCheck -= delta;
        if (live && _frontCheck <= 0)
        {
            _frontCheck = 0.5;
            _frontNear = FrontNear(campaign, globe, height);
            if (land > 0.001f) Survey(campaign.ProvinceMap, globe, height);
        }

        var step = 1f - MathF.Exp(-(float)delta * 1.5f);
        Mix(_high, live ? high * 0.55f * level : 0f, step);
        foreach (var (name, player) in _beds)
            Mix(player, live ? land * 0.45f * _ground.GetValueOrDefault(name) * level : 0f, step);
        Mix(_front, live ? _frontNear * 0.70f * level : 0f, step);
    }

    /// <summary>
    /// What ground is under the camera: nine points around the view centre, so a coast or a
    /// forest edge blends the beds instead of switching them. Presentation only.
    /// </summary>
    private void Survey(ProvinceTopology map, GlobePresentation globe, float height)
    {
        _ground.Clear();
        var openCountry = SoundCatalog.OpenCountryBed;
        if (map is null) { _ground[openCountry] = 1f; return; }
        var (latitude, longitude) = globe.ViewCentre;
        var radius = Mathf.Lerp(.5f, 2.5f, Mathf.InverseLerp(5.3f, 8.6f, height));
        const float point = 1f / 9f;
        for (var i = 0; i < 9; i++)
        {
            var angle = i * MathF.Tau / 8f;
            var lat = i == 8 ? latitude : Math.Clamp(latitude + radius * MathF.Sin(angle), -89f, 89f);
            var lon = i == 8 ? longitude : longitude + radius * MathF.Cos(angle) / MathF.Max(.2f, MathF.Cos(Mathf.DegToRad(latitude)));
            var province = CityCatalog.ProvinceUnder(map, lat, lon);
            if (province <= 0) { Add(SoundCatalog.SeaBed, point); continue; }
            if (SoundCatalog.TerrainAmbience.TryGetValue(map.TerrainAt(province).Id, out var mix))
                foreach (var (bed, share) in mix) Add(bed, point * share);
            else Add(openCountry, point);
        }

        void Add(string bed, float share) => _ground[bed] = _ground.GetValueOrDefault(bed) + share;
    }

    /// <summary>How close the camera is to a battle the player can see, 0 to 1; only from low down.</summary>
    private static float FrontNear(Campaign campaign, GlobePresentation globe, float height)
    {
        if (height > 9.5f || campaign.ProvinceMap is null) return 0f;
        var (latitude, longitude) = globe.ViewCentre;
        var reach = Mathf.Lerp(4f, 14f, Mathf.InverseLerp(5.3f, 9.5f, height));
        var nearest = float.MaxValue;
        foreach (var battle in campaign.Battles)
        {
            if (battle.Ended || battle.ProvinceId <= 0) continue;
            if (campaign.Player is { IsHuman: true } && !CampaignIntel.PlayerCanSeeBattle(campaign, battle)) continue;
            var (lat, lon) = campaign.ProvinceMap.CenterOf(battle.ProvinceId);
            var dLon = Mathf.Wrap(lon - longitude, -180f, 180f) * MathF.Cos(Mathf.DegToRad(latitude));
            var distance = MathF.Sqrt((lat - latitude) * (lat - latitude) + dLon * dLon);
            nearest = MathF.Min(nearest, distance);
        }
        if (nearest == float.MaxValue) return 0f;
        return (1f - Mathf.SmoothStep(reach * 0.25f, reach, nearest)) * (1f - Mathf.SmoothStep(8.0f, 9.5f, height));
    }

    private static void Mix(AudioStreamPlayer player, float share, float step)
    {
        if (!GodotObject.IsInstanceValid(player) || player.Stream is null) return;
        var current = player.VolumeDb <= -79f ? 0f : Mathf.DbToLinear(player.VolumeDb);
        var next = Mathf.Lerp(current, share, step);
        if (next < 0.002f)
        {
            player.VolumeDb = -80f;
            if (player.Playing && share <= 0f) player.Stop();
            return;
        }
        player.VolumeDb = Mathf.LinearToDb(next);
        if (!player.Playing) player.Play((float)GD.RandRange(0.0, Math.Max(0.0, player.Stream.GetLength() - 1.0)));
    }

    private static void Silence(AudioStreamPlayer player, bool stop)
    {
        if (!GodotObject.IsInstanceValid(player)) return;
        player.VolumeDb = -80;
        if (stop && player.Playing) player.Stop();
    }
}
