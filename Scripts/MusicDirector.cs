using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>Persistent, two-deck soundtrack. Presentation randomness never consumes simulation randomness.</summary>
public partial class MusicDirector : Node
{
    private static MusicDirector _instance;
    private readonly AudioStreamPlayer[] _decks = new AudioStreamPlayer[2];
    private readonly MusicPlaybackTransition _transition = new();
    private readonly AudioFocusEnvelope _cueFocus = new();
    private readonly MusicCatalog.Track[] _tracks = new MusicCatalog.Track[2];
    private readonly Random _order = new();
    private readonly MusicMoodLatch _mood = new();
    private readonly HashSet<string> _unavailable = new(StringComparer.OrdinalIgnoreCase);
    private Node _asker;
    private Func<MusicCatalog.Moment> _askMoment;
    private Func<MusicReadout> _askSituation;
    private MusicCatalog.Moment _moment = MusicCatalog.Moment.Menu;
    private MusicReadout _readout = new(MusicMood.Calm, "No campaign pressure recorded.");
    private bool _started;
    private bool _lastAdaptive;
    private int _catalogRevision = -1;
    private string _playing = "";
    private double _clock;
    private string _region = "";
    private bool _auditionSuppressed;

    public void SuppressForAudition(bool suppressed)
    { _auditionSuppressed = suppressed; RefreshVolume(); }

    public void DuckForCue(double seconds, float decibels) =>
        _cueFocus.Request(seconds, Mathf.DbToLinear(decibels));

    public void ClearCueFocus() => _cueFocus.Reset();
    internal float CueFocusGain => _cueFocus.Gain;

    public MusicMood CurrentMood => _mood.Current;
    public string CurrentTrack => _playing.Length == 0 ? "" : Loc.Get(_tracks[_transition.ActiveDeck].Label);
    public string CurrentReason => Loc.Get(_readout.Reason);
    public bool Adaptive => SettingsCatalog.Flag("adaptive_music");

    public static MusicDirector Ensure(Node anywhere)
    {
        if (_instance is not null && IsInstanceValid(_instance)) return _instance;
        _instance = new MusicDirector { Name = "MusicDirector", ProcessMode = ProcessModeEnum.Always };
        anywhere.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _instance);
        return _instance;
    }

    public void FollowScene(Node scene, Func<MusicCatalog.Moment> moment, Func<MusicReadout> situation = null)
    { _cueFocus.Reset(); _auditionSuppressed = false; _asker = scene; _askMoment = moment; _askSituation = situation; if (IsInsideTree()) Follow(); }

    public override void _Ready()
    {
        for (var i = 0; i < _decks.Length; i++)
        {
            var index = i;
            _decks[i] = new AudioStreamPlayer { Name = "MusicDeck" + i, ProcessMode = ProcessModeEnum.Always };
            AddChild(_decks[i]);
            _decks[i].Finished += () =>
            { if (index == _transition.ActiveDeck && !_transition.IsChanging) RequestNext(advance: true); };
        }
        Follow();
    }

    public override void _Process(double delta)
    {
        _cueFocus.Advance(delta);
        var completed = _transition.Advance(delta);
        if (completed == MusicTransitionCompletion.Track) _decks[1 - _transition.ActiveDeck].Stop();
        else if (completed == MusicTransitionCompletion.Silence)
            foreach (var deck in _decks) deck.Stop();
        DrainSelection();
        // Recompute gains even during a crossfade: muting never waits for it to finish.
        RefreshVolume();
        _clock += delta; if (_clock < 2) return; _clock = 0; Follow();
    }

    public override void _ExitTree()
    {
        // There is no deferred tween callback left to revive an obsolete scene's selection.
        _transition.Reset(); _playing = ""; _asker = null; _askMoment = null; _askSituation = null;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    public void RefreshVolume()
    {
        for (var i = 0; i < _decks.Length; i++)
        {
            if (_decks[i] is null) continue;
            var share = _transition.Gain(i, SettingsCatalog.Number("volume_master"),
                SettingsCatalog.Number("volume_music"), _auditionSuppressed) * _cueFocus.Gain;
            _decks[i].VolumeDb = share <= .0005f ? -80f : SoundCatalog.GetChannel("music").VolumeDb + _tracks[i].VolumeDb + Mathf.LinearToDb(share);
        }
    }

    private void Follow()
    {
        if (_decks[0] is null) return;
        var live = _asker is not null && IsInstanceValid(_asker);
        var moment = live && _askMoment is not null ? _askMoment() : _moment;
        _readout = live && _askSituation is not null ? _askSituation() : new(MusicMood.Calm, "No campaign pressure recorded.");
        var adaptive = Adaptive;
        var requested = adaptive ? _readout.Mood : MusicMood.Any;
        var catalogChanged = _catalogRevision != MusicCatalog.Revision;
        if (catalogChanged) { _catalogRevision = MusicCatalog.Revision; _unavailable.Clear(); }
        var reset = !_started || moment != _moment || adaptive != _lastAdaptive;
        _lastAdaptive = adaptive;
        var regionChanged = _region != _readout.Region;
        _region = _readout.Region;
        var moodChanged = _mood.Observe(requested, Time.GetTicksMsec() / 1000d, MusicCatalog.Director, reset);
        _moment = moment; _started = true;
        if (reset || catalogChanged || moodChanged || regionChanged)
        {
            RequestNext(advance: reset || catalogChanged);
        }
        else if (!_decks[_transition.ActiveDeck].Playing && !_transition.IsChanging) RequestNext(advance: true);
        else if (!_transition.IsChanging && !_tracks[_transition.ActiveDeck].Loop && _decks[_transition.ActiveDeck].Stream is { } stream && stream.GetLength() > 0 &&
                 stream.GetLength() - _decks[_transition.ActiveDeck].GetPlaybackPosition() <= MusicCatalog.Director.FadeSeconds) RequestNext(advance: true);
    }

    private IReadOnlyList<MusicCatalog.Track> Pool() => Adaptive
        ? MusicCatalog.For(_moment, _mood.Current, _region) : MusicCatalog.For(_moment);

    private void RequestNext(bool advance)
    {
        _transition.RequestSelection(advance);
        DrainSelection();
    }

    private void DrainSelection()
    {
        if (_transition.IsChanging || !_transition.SelectionPending) return;
        // Coalesce changes during a fade. The latest scene, region and mood decide;
        // returning to a compatible context does not restart a perfectly good track.
        var compatible = _decks[_transition.ActiveDeck].Playing &&
            Pool().Any(track => string.Equals(track.Name, _playing, StringComparison.OrdinalIgnoreCase));
        if (_transition.TryTakeSelection(compatible)) PlayNext();
    }

    private void PlayNext()
    {
        var available = Pool().Where(t => !_unavailable.Contains(t.Name)).ToList();
        if (available.Count > 1) available.RemoveAll(t => t.Name == _playing);
        while (available.Count > 0)
        {
            var total = available.Sum(t => (double)t.Weight);
            var roll = _order.NextDouble() * total;
            var track = available[^1];
            foreach (var option in available) { roll -= option.Weight; if (roll <= 0) { track = option; break; } }
            AudioStream stream = null;
            if (ResourceLoader.Exists(track.File)) stream = GD.Load<AudioStream>(track.File);
            if (stream is null)
            {
                if (_unavailable.Add(track.Name)) GD.PushWarning($"[dof] music unavailable: '{track.Name}' at {track.File}");
                available.RemoveAll(t => t.Name == track.Name); continue;
            }
            if (track.Loop)
            {
                stream = (AudioStream)stream.Duplicate();
                if (stream is AudioStreamOggVorbis ogg) ogg.Loop = true;
                else if (stream is AudioStreamMP3 mp3) mp3.Loop = true;
                else if (stream is AudioStreamWav wav) wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                else GD.PushWarning($"[dof] music loop unsupported: '{track.Name}'");
            }
            Crossfade(track, stream); return;
        }
        FadeToSilence();
    }

    private void Crossfade(MusicCatalog.Track track, AudioStream stream)
    {
        var next = _transition.BeginTrack(MusicCatalog.Director.FadeSeconds,
            _decks[_transition.ActiveDeck].Playing);
        _decks[next].Stop(); _decks[next].Stream = stream; _tracks[next] = track;
        _playing = track.Name;
        RefreshVolume(); _decks[next].Play();
    }

    private void FadeToSilence()
    {
        if (_playing.Length == 0) return;
        _playing = "";
        _transition.BeginSilence(MusicCatalog.Director.FadeSeconds);
    }
}
