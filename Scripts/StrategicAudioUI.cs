using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private AudioStreamPlayer _strategicAudio = null!;
    private AudioStreamPlayer _battleAudio = null!;
    private AudioStreamPlayer _uiAudio = null!;
    private readonly Dictionary<string, AudioStream> _strategicCues = new();
    private readonly HashSet<int> _knownEndedBattles = new();
    private readonly Queue<string> _strategicAudioQueue = new();
    private readonly Dictionary<string, ulong> _lastCueAt = new();
    private ulong _lastBattleCueAt;

    private void BuildStrategicAudio()
    {
        _strategicAudio = new AudioStreamPlayer { Name = "StrategicAudio", VolumeDb = -7f };
        _battleAudio = new AudioStreamPlayer { Name = "BattleAudio", VolumeDb = -10f };
        _uiAudio = new AudioStreamPlayer { Name = "InterfaceAudio", VolumeDb = -15f };
        AddChild(_strategicAudio);
        AddChild(_battleAudio);
        AddChild(_uiAudio);
        _strategicAudio.Finished += PlayNextStrategicCue;
        LoadCue("war_declared");
        LoadCue("peace_signed");
        LoadCue("battle_won");
        LoadCue("battle_lost");
        LoadCue("event_alert");
        LoadCue("ui_select");
    }

    private void LoadCue(string name)
    {
        var modernPath = $"res://Assets/Audio/Modern/{name}.wav";
        var stream = ResourceLoader.Exists(modernPath)
            ? GD.Load<AudioStream>(modernPath)
            : GD.Load<AudioStream>($"res://Assets/Audio/{name}.wav");
        if (stream is not null) _strategicCues[name] = stream;
    }

    private void PlayStrategicCue(string name)
    {
        if (!_strategicCues.TryGetValue(name, out var stream)) return;

        var now = Time.GetTicksMsec();
        var cooldown = name switch
        {
            "ui_select" => 80UL,
            "battle_won" or "battle_lost" => 4500UL,
            _ => 900UL
        };
        if (_lastCueAt.TryGetValue(name, out var previous) && now - previous < cooldown) return;

        if (name == "ui_select")
        {
            _lastCueAt[name] = now;
            _uiAudio.Stream = stream;
            _uiAudio.Play();
            return;
        }

        if (name is "battle_won" or "battle_lost")
        {
            // Battles can resolve in batches. One concise report is enough; it must never
            // interrupt a declaration, event or peace notification.
            if (_battleAudio.Playing || _strategicAudio.Playing || now - _lastBattleCueAt < 4500UL) return;
            _lastCueAt[name] = now;
            _lastBattleCueAt = now;
            _battleAudio.Stream = stream;
            _battleAudio.Play();
            return;
        }

        if (_strategicAudio.Playing)
        {
            if (_strategicAudioQueue.Count < 3 && !_strategicAudioQueue.Contains(name))
                _strategicAudioQueue.Enqueue(name);
            return;
        }

        _lastCueAt[name] = now;
        // Important state changes take precedence over a disposable battle report.
        // Stopping the lower-priority channel avoids two unrelated stingers talking over
        // one another while keeping interface feedback independent and responsive.
        if (_battleAudio.Playing) _battleAudio.Stop();
        _strategicAudio.Stream = stream;
        _strategicAudio.Play();
    }

    private void PlayNextStrategicCue()
    {
        if (_strategicAudioQueue.Count == 0) return;
        var name = _strategicAudioQueue.Dequeue();
        if (!_strategicCues.TryGetValue(name, out var stream)) return;
        _lastCueAt[name] = Time.GetTicksMsec();
        _strategicAudio.Stream = stream;
        _strategicAudio.Play();
    }

    private void ResetStrategicAudioState()
    {
        _strategicAudio.Stop();
        _battleAudio.Stop();
        _uiAudio.Stop();
        _strategicAudioQueue.Clear();
        _lastCueAt.Clear();
        _lastBattleCueAt = 0;
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
