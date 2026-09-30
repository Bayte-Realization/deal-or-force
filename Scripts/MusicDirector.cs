using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The soundtrack, over every scene.
///
/// The music used to be a player inside the campaign scene, and the main menu became a scene of
/// its own - the front door that comes up before any world is built. So the one screen a menu
/// theme is written for had nothing to play it: the music began only once "Singleplayer" had
/// built a world, and cut off at every change of scene. This hangs off the root of the tree
/// rather than off a scene, so it outlives them. The scene on screen says which moment it is -
/// the menu is the menu, the campaign is the campaign while its interface is up - and a change
/// of moment fades one track into another rather than cutting.
///
/// What plays is still entirely `music/`: the base game's tracks and whatever a mod adds, picked
/// by weight for the moment. A moment nothing is declared for is silence.
/// </summary>
public partial class MusicDirector : Node
{
    private const float FadeSeconds = 1.6f;
    private const float SilentDb = -60f;

    private static MusicDirector _instance;

    private AudioStreamPlayer _player;
    private readonly Random _order = new();
    private MusicCatalog.Moment _moment = MusicCatalog.Moment.Menu;
    private bool _started;
    private bool _changing;
    private Node _asker;
    private Func<MusicCatalog.Moment> _askMoment;
    private string _playing = "";
    private Tween _fade;
    private double _clock;

    /// <summary>The one director, made and hung off the root the first time any scene asks.</summary>
    public static MusicDirector Ensure(Node anywhere)
    {
        if (_instance is not null && IsInstanceValid(_instance)) return _instance;
        _instance = new MusicDirector { Name = "MusicDirector", ProcessMode = ProcessModeEnum.Always };
        // Deferred: scenes ask from their _Ready, while the root is still busy adding them.
        anywhere.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _instance);
        return _instance;
    }

    /// <summary>
    /// Which moment it is, asked of the scene now showing, from now until another scene says.
    /// A scene that has gone is not asked: the music carries on until the next one speaks.
    /// </summary>
    public void FollowScene(Node scene, Func<MusicCatalog.Moment> moment)
    {
        _asker = scene;
        _askMoment = moment;
        if (IsInsideTree()) Follow();
    }

    public override void _Ready()
    {
        _player = new AudioStreamPlayer { Name = "Music", ProcessMode = ProcessModeEnum.Always };
        AddChild(_player);
        _player.Finished += () =>
        {
            if (!_changing) PlayNext(fadeIn: false);
        };
        Follow();
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (_clock < 0.5) return;
        _clock = 0;
        Follow();
    }

    /// <summary>The player's music level: the master and music sliders over the catalogue's own.</summary>
    public void RefreshVolume()
    {
        // A fade under way lands on the level it is heading for, which is this one.
        if (_player is null || _fade is not null && _fade.IsValid() && _fade.IsRunning()) return;
        _player.VolumeDb = TargetDb();
    }

    private static float TargetDb()
    {
        var share = SettingsCatalog.Number("volume_master") * SettingsCatalog.Number("volume_music");
        return share <= 0.0005f
            ? -80f
            : SoundCatalog.GetChannel("music").VolumeDb + Mathf.LinearToDb(Mathf.Clamp(share, 0f, 1f));
    }

    private void Follow()
    {
        if (_player is null) return;
        var moment = _asker is not null && IsInstanceValid(_asker) && _askMoment is not null ? _askMoment() : _moment;
        if (!_started || moment != _moment)
        {
            var first = !_started;
            _started = true;
            _moment = moment;
            ChangeTrack(first);
            return;
        }
        // Nothing playing and nothing on its way - a catalogue that was empty when asked, a
        // track that would not load - is asked again rather than left silent for good.
        if (!_player.Playing && !_changing) PlayNext(fadeIn: true);
    }

    /// <summary>Out with the old track, if there is one, and in with one for the moment.</summary>
    private void ChangeTrack(bool first)
    {
        _fade?.Kill();
        if (first || !_player.Playing)
        {
            _changing = false;
            PlayNext(fadeIn: !first);
            return;
        }
        _changing = true;
        _fade = CreateTween();
        _fade.TweenProperty(_player, "volume_db", SilentDb, FadeSeconds);
        _fade.TweenCallback(Callable.From(() =>
        {
            _changing = false;
            PlayNext(fadeIn: true);
        }));
    }

    /// <summary>The next track for the moment, by weight, and not the one that has just played.</summary>
    private void PlayNext(bool fadeIn)
    {
        var tracks = MusicCatalog.For(_moment);
        if (tracks.Count == 0)
        {
            _player.Stop();
            _playing = "";
            return;
        }
        var total = 0f;
        foreach (var track in tracks)
            if (tracks.Count == 1 || track.Name != _playing) total += track.Weight;
        var roll = (float)_order.NextDouble() * total;
        foreach (var track in tracks)
        {
            if (tracks.Count > 1 && track.Name == _playing) continue;
            roll -= track.Weight;
            if (roll > 0f) continue;
            if (!ResourceLoader.Exists(track.File))
            {
                GD.PushWarning($"[dof] music track '{track.Name}' points at {track.File}, which is not there");
                return;
            }
            _player.Stream = GD.Load<AudioStream>(track.File);
            _playing = track.Name;
            _fade?.Kill();
            if (fadeIn)
            {
                _player.VolumeDb = SilentDb;
                _player.Play();
                _fade = CreateTween();
                _fade.TweenProperty(_player, "volume_db", TargetDb(), FadeSeconds);
            }
            else
            {
                _player.VolumeDb = TargetDb();
                _player.Play();
            }
            return;
        }
    }
}
