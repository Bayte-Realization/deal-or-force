using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class MusicDirector
{
    private readonly MusicTransport _transport = new();
    private AudioStream _requestedStream;
    private bool _silencePending;
    public bool Manual => _transport.Manual;
    public bool Paused => _transport.Paused;
    public string CurrentTrackId => _playing;
    public string RequestedTrackId => _transport.Selection;
    public double PlaybackPosition => _decks[_transition.ActiveDeck]?.GetPlaybackPosition() ?? 0;
    public double PlaybackLength => _decks[_transition.ActiveDeck]?.Stream?.GetLength() ?? 0;

    /// <summary>Validate/load before changing the transport. A broken mod recording must
    /// leave the working soundtrack alone. Closing the library does not own these decks.</summary>
    public bool PlayTrack(string id)
    {
        if (_shuttingDown || Silenced || string.IsNullOrWhiteSpace(id)) return false;
        var track = MusicCatalog.All.FirstOrDefault(t => string.Equals(t.Name, id, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(track.Name) || !TryLoadTrack(track, out var stream)) return false;
        if (!_transport.Select(track.Name, MusicCatalog.All.Select(t => t.Name).ToArray())) return false;
        _requestedStream = stream;
        SetPaused(false); DrainSelection(); return true;
    }

    public bool StepTrack(int direction)
    {
        if (direction == 0 || _shuttingDown || Silenced) return false;
        // Bounded by catalogue size. A missing file is skipped, not retried every frame.
        var tracks = MusicCatalog.All.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Where(t => !_unavailable.Contains(t.Name)).Select(t => t.Name).ToList();
        var current = Manual ? RequestedTrackId : CurrentTrackId;
        while (tracks.Count > 0)
        {
            var next = MusicTransport.Neighbour(tracks, current, direction);
            if (PlayTrack(next)) return true;
            tracks.Remove(next); current = next;
        }
        return false;
    }

    public void SetPaused(bool paused)
    {
        if (_shuttingDown) return;
        _transport.SetPaused(paused);
        foreach (var deck in _decks) if (IsInstanceValid(deck)) deck.StreamPaused = paused;
        if (!paused) DrainSelection();
    }

    public void ResumeAutomatic()
    {
        if (_shuttingDown) return;
        _transport.Automatic(); _requestedStream = null;
        foreach (var deck in _decks) if (IsInstanceValid(deck)) deck.StreamPaused = false;
        // Compatible music need not restart. The existing director chooses the current scene/mood.
        _transition.RequestSelection(false); Follow(); DrainSelection();
    }

    private bool TryLoadTrack(MusicCatalog.Track track, out AudioStream stream)
    {
        stream = null;
        if (string.IsNullOrWhiteSpace(track.File) || !AudioAssets.Exists(track.File))
        { _unavailable.Add(track.Name); return false; }
        try { stream = AudioAssets.Load(track.File); }
        catch (Exception exception) { GD.PushWarning("[dof] cannot load music '" + track.Name + "': " + exception.Message); }
        if (stream is null) { _unavailable.Add(track.Name); return false; }
        // Never change the cached imported resource shared by audition and other scenes.
        stream = (AudioStream)stream.Duplicate();
        if (stream is AudioStreamOggVorbis ogg) ogg.Loop = track.Loop;
        else if (stream is AudioStreamMP3 mp3) mp3.Loop = track.Loop;
        else if (stream is AudioStreamWav wav) wav.LoopMode = track.Loop
            ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled;
        return true;
    }

    private void DrainManualSelection()
    {
        _transition.TryTakeSelection(true); // Discard obsolete automatic requests, not the current music.
        if (!_transport.TakeSelection(out var id)) return;
        var track = MusicCatalog.All.FirstOrDefault(t => t.Name == id);
        var stream = _requestedStream; _requestedStream = null;
        if (string.IsNullOrEmpty(track.Name) || stream is null) { ResumeAutomatic(); return; }
        Crossfade(track, stream);
    }
}
