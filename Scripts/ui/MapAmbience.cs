using System;
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
/// away into open country; and near a battle the player can see, the front beyond the hills.
/// Three loops, crossfaded slowly, under every cue and at the effects volume.
/// </summary>
public sealed partial class MapAmbience : Node
{
    private const string Folder = "res://game/sound/audio/ambience/";
    private readonly Func<GlobePresentation> _globe;
    private readonly Func<Campaign> _campaign;
    private AudioStreamPlayer _high, _land, _front;
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
        _land = Loop("land_calm.ogg");
        _front = Loop("front_far.ogg");
    }

    private AudioStreamPlayer Loop(string file)
    {
        var player = new AudioStreamPlayer { Name = "Ambience_" + file.Replace(".ogg", ""), VolumeDb = -80f };
        if (ResourceLoader.Exists(Folder + file) && GD.Load<AudioStream>(Folder + file) is AudioStream stream)
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
            Silence(_land, !AuditionSuppressed);
            Silence(_front, !AuditionSuppressed);
            return;
        }
        var globe = _globe();
        var campaign = _campaign();
        level *= _cueFocus.Gain;
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
        }

        var step = 1f - MathF.Exp(-(float)delta * 1.5f);
        Mix(_high, live ? high * 0.55f * level : 0f, step);
        Mix(_land, live ? land * 0.45f * level : 0f, step);
        Mix(_front, live ? _frontNear * 0.70f * level : 0f, step);
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
