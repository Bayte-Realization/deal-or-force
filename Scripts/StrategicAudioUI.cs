using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private AudioStreamPlayer _strategicAudio = null!;
    private readonly Dictionary<string, AudioStream> _strategicCues = new();
    private readonly HashSet<int> _knownEndedBattles = new();

    private void BuildStrategicAudio()
    {
        _strategicAudio = new AudioStreamPlayer { Name = "StrategicAudio", VolumeDb = -5f };
        AddChild(_strategicAudio);
        LoadCue("war_declared");
        LoadCue("peace_signed");
        LoadCue("battle_won");
        LoadCue("battle_lost");
        LoadCue("event_alert");
        LoadCue("ui_select");
    }

    private void LoadCue(string name)
    {
        var stream = GD.Load<AudioStream>($"res://Assets/Audio/{name}.wav");
        if (stream is not null) _strategicCues[name] = stream;
    }

    private void PlayStrategicCue(string name)
    {
        if (!_strategicCues.TryGetValue(name, out var stream)) return;
        _strategicAudio.Stream = stream;
        _strategicAudio.Play();
    }

    private void ResetStrategicAudioState()
    {
        _knownEndedBattles.Clear();
        foreach (var battle in _campaign.Battles.Where(battle => battle.Ended))
            _knownEndedBattles.Add(battle.Id);
    }

    private void RefreshStrategicAudio()
    {
        if (_campaign?.Player is null || !_hudLayer.Visible) return;
        foreach (var battle in _campaign.Battles.Where(battle => battle.Ended &&
                     (battle.AttackerId == _campaign.Player.Id || battle.DefenderId == _campaign.Player.Id)))
        {
            if (!_knownEndedBattles.Add(battle.Id)) continue;
            PlayStrategicCue(battle.WinnerId == _campaign.Player.Id ? "battle_won" : "battle_lost");
        }
    }
}
