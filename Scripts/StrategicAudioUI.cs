using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private readonly Dictionary<string, AudioStreamPlayer> _audioChannels = new();
    private readonly Dictionary<string, AudioStream> _strategicCues = new();
    private readonly HashSet<int> _knownEndedBattles = new();
    private readonly Queue<string> _strategicAudioQueue = new();
    private readonly Dictionary<string, ulong> _lastCueAt = new();
    private AudioStreamPlayer _musicPlayer;
    private readonly System.Random _musicOrder = new();

    /// <summary>
    /// The channels and the cues both come out of `sound/`. The code below knows that a
    /// cue has a channel, a level, a cooldown and a manner; it does not know that there
    /// are three channels, or that a battle report waits four and a half seconds. Those
    /// were constants in this file and are declarations now.
    /// </summary>
    private void BuildStrategicAudio()
    {
        foreach (var channel in SoundCatalog.AllChannels)
        {
            var player = new AudioStreamPlayer
            {
                Name = $"Audio_{channel.Name}",
                VolumeDb = channel.VolumeDb
            };
            AddChild(player);
            _audioChannels[channel.Name] = player;
            // Only a queueing channel needs to know when it has finished.
            if (!channel.Independent) player.Finished += PlayNextStrategicCue;
        }

        _musicPlayer = new AudioStreamPlayer { Name = "Music", VolumeDb = SoundCatalog.GetChannel("music").VolumeDb };
        AddChild(_musicPlayer);
        _musicPlayer.Finished += PlayNextTrack;
        PlayNextTrack();

        foreach (var cue in SoundCatalog.All)
        {
            if (!ResourceLoader.Exists(cue.File))
            {
                GD.PushWarning($"[dof] sound cue '{cue.Name}' points at {cue.File}, which is not there");
                continue;
            }
            var stream = GD.Load<AudioStream>(cue.File);
            if (stream is not null) _strategicCues[cue.Name] = stream;
        }
    }

    /// <summary>
    /// Whether a cue may be heard, and at whose expense.
    ///
    /// Three rules, all of them from the data: a cue waits out its own cooldown; a
    /// yielding cue stays quiet while anything that can be interrupted is speaking; and a
    /// cue that does sound silences every lower-priority channel and queues behind its own.
    /// </summary>
    private void PlayStrategicCue(string name)
    {
        if (!_strategicCues.TryGetValue(name, out var stream)) return;
        var cue = SoundCatalog.Get(name);
        var channel = SoundCatalog.GetChannel(cue.Channel);
        if (!_audioChannels.TryGetValue(cue.Channel, out var player)) return;

        var now = Time.GetTicksMsec();
        if (_lastCueAt.TryGetValue(name, out var previous) && now - previous < (ulong)cue.CooldownMs) return;

        if (channel.Independent)
        {
            _lastCueAt[name] = now;
            Speak(player, cue, stream);
            return;
        }

        // Anything that arrives in batches gives way rather than queueing: a hundred
        // resolved battles must not become a hundred reports waiting their turn.
        if (cue.Yielding && Occupied(0)) return;

        if (player.Playing)
        {
            if (_strategicAudioQueue.Count < 3 && !_strategicAudioQueue.Contains(name))
                _strategicAudioQueue.Enqueue(name);
            return;
        }

        foreach (var (other, speaking) in _audioChannels)
        {
            var declaration = SoundCatalog.GetChannel(other);
            if (declaration.Independent || declaration.Priority >= channel.Priority) continue;
            if (speaking.Playing) speaking.Stop();
        }

        _lastCueAt[name] = now;
        Speak(player, cue, stream);
    }

    /// <summary>Whether any interruptible channel at or above a priority is speaking.</summary>
    private bool Occupied(int fromPriority)
    {
        foreach (var (name, player) in _audioChannels)
        {
            var channel = SoundCatalog.GetChannel(name);
            if (channel.Independent || channel.Priority < fromPriority) continue;
            if (player.Playing) return true;
        }
        return false;
    }

    private static void Speak(AudioStreamPlayer player, SoundCatalog.Cue cue, AudioStream stream)
    {
        player.VolumeDb = SoundCatalog.GetChannel(cue.Channel).VolumeDb + cue.VolumeDb;
        player.Stream = stream;
        player.Play();
    }

    private void PlayNextStrategicCue()
    {
        if (_strategicAudioQueue.Count == 0) return;
        var name = _strategicAudioQueue.Dequeue();
        if (!_strategicCues.TryGetValue(name, out var stream)) return;
        var cue = SoundCatalog.Get(name);
        if (!_audioChannels.TryGetValue(cue.Channel, out var player)) return;
        _lastCueAt[name] = Time.GetTicksMsec();
        Speak(player, cue, stream);
    }

    /// <summary>
    /// The next track, weighted, from whatever is declared for where the player is.
    ///
    /// The base game declares nothing, so this does nothing - which is the point. A game
    /// that only plays music its own code names is a game no mod can score, and the whole
    /// `music/` folder exists so that the list is somebody else's to write.
    /// </summary>
    private void PlayNextTrack()
    {
        if (_musicPlayer is null) return;
        var moment = _hudLayer is not null && _hudLayer.Visible
            ? MusicCatalog.Moment.Campaign
            : MusicCatalog.Moment.Menu;
        var tracks = MusicCatalog.For(moment);
        if (tracks.Count == 0) return;

        var total = 0f;
        foreach (var track in tracks) total += track.Weight;
        var roll = (float)_musicOrder.NextDouble() * total;
        foreach (var track in tracks)
        {
            roll -= track.Weight;
            if (roll > 0f) continue;
            if (!ResourceLoader.Exists(track.File))
            {
                GD.PushWarning($"[dof] music track '{track.Name}' points at {track.File}, which is not there");
                return;
            }
            _musicPlayer.Stream = GD.Load<AudioStream>(track.File);
            _musicPlayer.Play();
            return;
        }
    }

    private void ResetStrategicAudioState()
    {
        foreach (var player in _audioChannels.Values) player.Stop();
        _strategicAudioQueue.Clear();
        _lastCueAt.Clear();
        _knownEndedBattles.Clear();
        foreach (var battle in _campaign.Battles.Where(battle => battle.Ended))
            _knownEndedBattles.Add(battle.Id);
    }

    private void RefreshStrategicAudio()
    {
        if (_campaign?.Player is null || !_hudLayer.Visible) return;
        var playerWon = false;
        var playerLost = false;
        foreach (var battle in _campaign.Battles.Where(battle => battle.Ended &&
                     (battle.AttackerId == _campaign.Player.Id || battle.DefenderId == _campaign.Player.Id)))
        {
            if (!_knownEndedBattles.Add(battle.Id)) continue;
            if (battle.WinnerId == _campaign.Player.Id) playerWon = true;
            else playerLost = true;
        }

        if (playerLost) PlayStrategicCue("battle_lost");
        else if (playerWon) PlayStrategicCue("battle_won");
    }
}
