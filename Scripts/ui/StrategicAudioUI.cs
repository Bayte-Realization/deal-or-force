using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private readonly Dictionary<string, AudioStreamPlayer> _audioChannels = new();
    private MapAmbience _mapAmbience;
    private readonly Dictionary<string, AudioStream> _strategicCues = new();
    private readonly HashSet<int> _knownEndedBattles = new();
    private readonly StrategicCueQueue _strategicAudioQueue = new();
    private readonly Dictionary<string, ulong> _lastCueAt = new();
    private readonly Dictionary<string, float> _channelCueTrim = new();
    private int _audioObservedNation = -1;
    private string _audioGov = "", _audioIdeology = "", _audioFormation = "";
    private readonly Dictionary<int, (int State, BuildingKind Building, int Count)> _audioConstruction = new();
    private readonly HashSet<int> _audioSupportFormations = new();
    private readonly List<int> _audioConstructionRemovals = new();
    private readonly HashSet<int> _audioSupportScratch = new();
    private StrategicCueQueue.ChannelState[] _audioChannelScratch = System.Array.Empty<StrategicCueQueue.ChannelState>();
    private int _audioKnownTechCount = -1;
    private long _audioNewsSeen = -1;
    private ulong _lastNewsStingAt;
    private Campaign _cachedAudioCampaign;
    private MusicReadout _cachedMusicReadout = new(MusicMood.Calm, "No campaign pressure recorded.");
    private bool _cachedWorldCursed;

    /// <summary>
    /// Put the player's three volumes onto the players.
    ///
    /// Each channel keeps the level the sound catalogue gave it - a battle report is meant
    /// to be quieter than an alarm and that is a design decision, not a preference - and the
    /// slider moves the whole channel from there. Multiplied rather than replaced, so a mod
    /// that rebalances its cues is not silently overridden by whatever the player last
    /// dragged.
    ///
    /// Decibels, because that is what ears and mixers both work in: a linear slider at a
    /// half sounds like a half rather than like a quarter.
    /// </summary>
    private void ApplyVolumes()
    {
        var master = SettingsCatalog.Number("volume_master");
        var effects = SettingsCatalog.Number("volume_effects");

        static float Decibels(float share) =>
            share <= 0.0005f ? -80f : Mathf.LinearToDb(Mathf.Clamp(share, 0f, 1f));

        foreach (var (name, player) in _audioChannels)
        {
            if (!GodotObject.IsInstanceValid(player)) continue;
            var channel = SoundCatalog.GetChannel(name);
            player.VolumeDb = channel.VolumeDb + _channelCueTrim.GetValueOrDefault(name) + Decibels(master * effects);
            if (master * effects <= 0.0005f) player.Stop();
        }
        // The music's own level, which the director puts on the player it keeps across scenes.
        MusicDirector.Ensure(this).RefreshVolume();
    }

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

        // The music is not this scene's: it plays over the main menu too, and carries on across
        // the change between them (MusicDirector). This scene says which moment it is - the
        // campaign while its interface is up, the menu while it is not.
        MusicDirector.Ensure(this).FollowScene(this, () => _hudLayer is { Visible: true }
            ? MusicCatalog.Moment.Campaign
            : MusicCatalog.Moment.Menu,
            () =>
            {
                if (_hudLayer is not { Visible: true })
                    return new MusicReadout(MusicMood.Calm, "No campaign pressure recorded.");
                var campaign = _campaign;
                if (!ReferenceEquals(_cachedAudioCampaign, campaign))
                {
                    _cachedAudioCampaign = campaign;
                    _cachedMusicReadout = new(MusicMood.Calm, "No campaign pressure recorded.");
                    _cachedWorldCursed = false;
                }
                if (campaign is null)
                    return _cachedMusicReadout;
                if (campaign.TryEnterRead(0, requestWindowOnMiss: false))
                {
                    try
                    {
                        _cachedMusicReadout = MusicSituation.Read(campaign, campaign.Player, _openScreen == "Region" ? _selectedState : null, _observerMode);
                    }
                    finally
                    {
                        campaign.ExitRead();
                    }
                }
                return _cachedMusicReadout;
            },
            // A cursed world has no soundtrack, ever again (WorldCurses); the menu keeps its own.
            () =>
            {
                if (_hudLayer is not { Visible: true }) return false;
                var campaign = _campaign;
                if (!ReferenceEquals(_cachedAudioCampaign, campaign))
                {
                    _cachedAudioCampaign = campaign;
                    _cachedMusicReadout = new(MusicMood.Calm, "No campaign pressure recorded.");
                    _cachedWorldCursed = false;
                }
                if (campaign is null) return false;
                if (campaign.TryEnterRead(0, requestWindowOnMiss: false))
                {
                    try
                    {
                        _cachedWorldCursed = WorldCurses.Any(campaign);
                    }
                    finally
                    {
                        campaign.ExitRead();
                    }
                }
                return _cachedWorldCursed;
            });

        // And under all of it, the sound of the map from where the camera is.
        _mapAmbience = new MapAmbience(() => _hudLayer is { Visible: true } ? _globe : null, () => _campaign);
        AddChild(_mapAmbience);

        foreach (var cue in SoundCatalog.All)
        {
            if (!AudioAssets.Exists(cue.File))
            {
                GD.PushWarning($"[dof] sound cue '{cue.Name}' points at {cue.File}, which is not there");
                continue;
            }
            var stream = AudioAssets.Load(cue.File);
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

        if (player.Playing || Occupied(channel.Priority + 1))
        {
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

    private void Speak(AudioStreamPlayer player, SoundCatalog.Cue cue, AudioStream stream)
    {
        var gain = Mathf.Clamp(SettingsCatalog.Number("volume_master") * SettingsCatalog.Number("volume_effects"), 0, 1);
        if (gain <= 0.0005f) { player.Stop(); return; }
        InterruptMediaForPriority(SoundCatalog.GetChannel(cue.Channel).Priority);
        _channelCueTrim[cue.Channel] = cue.VolumeDb;
        player.VolumeDb = SoundCatalog.GetChannel(cue.Channel).VolumeDb + cue.VolumeDb + Mathf.LinearToDb(gain);
        player.Stream = stream;
        player.Play();
        // Duck only a cue that actually started, not a queued or muted request.
        // Interface clicks opt out in data so rapid navigation cannot pump the score.
        var channel = SoundCatalog.GetChannel(cue.Channel);
        if (channel.MusicDuckDb < 0)
            MusicDirector.Ensure(this).DuckForCue(stream.GetLength(), channel.MusicDuckDb);
        if (channel.AmbienceDuckDb < 0)
            _mapAmbience?.DuckForCue(stream.GetLength(), channel.AmbienceDuckDb);
    }

    private void PlayNextStrategicCue()
    {
        if (_strategicAudioQueue.Count == 0) return;
        if (_audioChannelScratch.Length != _audioChannels.Count)
            _audioChannelScratch = new StrategicCueQueue.ChannelState[_audioChannels.Count];
        var idx = 0;
        foreach (var pair in _audioChannels)
        {
            var channel = SoundCatalog.GetChannel(pair.Key);
            _audioChannelScratch[idx++] = new StrategicCueQueue.ChannelState(pair.Key, channel.Priority, channel.Independent, pair.Value.Playing);
        }
        _strategicAudioQueue.TryDispatch(name => _strategicCues.ContainsKey(name) ? SoundCatalog.Get(name) : null, _audioChannelScratch, cue =>
        {
            foreach (var (other, speaking) in _audioChannels)
            {
                var declaration = SoundCatalog.GetChannel(other);
                if (!declaration.Independent && declaration.Priority < SoundCatalog.GetChannel(cue.Channel).Priority && speaking.Playing)
                    speaking.Stop();
            }
            _lastCueAt[cue.Name] = Time.GetTicksMsec();
            Speak(_audioChannels[cue.Channel], cue, _strategicCues[cue.Name]);
        });
    }

    private void ResetStrategicAudioState()
    {
        _cachedAudioCampaign = _campaign;
        _cachedMusicReadout = new(MusicMood.Calm, "No campaign pressure recorded.");
        _cachedWorldCursed = false;
        _situationAttention.Reset();
        _situationNation = -1;
        _dealNoticeViewer = -1; _dealNoticeIds.Clear();
        foreach (var player in _audioChannels.Values) player.Stop();
        MusicDirector.Ensure(this).ClearCueFocus();
        _mapAmbience?.ClearCueFocus();
        _strategicAudioQueue.Clear();
        _lastCueAt.Clear();
        _knownEndedBattles.Clear();
        _audioObservedNation = -1;
        _audioGov = "";
        _audioIdeology = "";
        _audioFormation = "";
        _audioConstruction.Clear();
        _audioSupportFormations.Clear();
        _audioKnownTechCount = -1;
        _audioLastBattleCount = -1;
        _audioLastBattleHours = -1;
        _audioLastBattleWarRev = -1;
        _audioLastBattlePlayerId = -1;
        // A loaded campaign's old news is not news: only what happens from here sounds.
        _audioNewsSeen = _campaign.Chronicle.Entries.Count > 0 ? _campaign.Chronicle.Entries[^1].Sequence : 0;
        for (var i = 0; i < _campaign.Battles.Count; i++)
        {
            var battle = _campaign.Battles[i];
            if (battle.Ended) _knownEndedBattles.Add(battle.Id);
        }
    }

    private int _audioLastBattleCount = -1;
    private long _audioLastBattleHours = -1;
    private int _audioLastBattleWarRev = -1;
    private int _audioLastBattlePlayerId = -1;

    private void RefreshStrategicAudio()
    {
        if (_campaign?.Player is null || !_hudLayer.Visible) return;
        RefreshDomesticAudio();
        var playerId = _campaign.Player.Id;
        var playerWon = false;
        var playerLost = false;
        if (_campaign.Battles.Count != _audioLastBattleCount ||
            _campaign.ElapsedHours != _audioLastBattleHours ||
            _campaign.WarRevision != _audioLastBattleWarRev ||
            playerId != _audioLastBattlePlayerId)
        {
            _audioLastBattleCount = _campaign.Battles.Count;
            _audioLastBattleHours = _campaign.ElapsedHours;
            _audioLastBattleWarRev = _campaign.WarRevision;
            _audioLastBattlePlayerId = playerId;
            for (var i = 0; i < _campaign.Battles.Count; i++)
            {
                var battle = _campaign.Battles[i];
                if (!battle.Ended || (battle.AttackerId != playerId && battle.DefenderId != playerId)) continue;
                if (!_knownEndedBattles.Add(battle.Id)) continue;
                if (battle.WinnerId == playerId) playerWon = true;
                else playerLost = true;
            }
        }

        if (playerLost) PlayStrategicCue("battle_lost");
        else if (playerWon) PlayStrategicCue("battle_won");
        else RefreshNewsAudio();
    }

    /// <summary>
    /// A headline the rail will show makes its sound (common/news "stings"): the most
    /// important of what arrived since the last look, at most one per gap. The player's own
    /// acts are left out - declaring a war or signing a peace already sounded from the button.
    /// </summary>
    private void RefreshNewsAudio()
    {
        var player = _campaign.Player;
        if (_audioNewsSeen < 0)
        {
            _audioNewsSeen = _campaign.Chronicle.Entries.Count > 0 ? _campaign.Chronicle.Entries[^1].Sequence : 0;
            return;
        }
        NewsFeed.Line best = null;
        var bestWeight = -1;
        var newest = _audioNewsSeen;
        foreach (var line in NewsFeed.Lines(_campaign, _audioNewsSeen, 64))
        {
            newest = System.Math.Max(newest, line.Sequence);
            if (line.ActorId == player.Id || NewsFeed.Sting(line.Kind).Length == 0) continue;
            var ours = line.TargetId == player.Id;
            var importance = NewsFeed.Importance(line.Kind);
            if (importance < NewsFeed.RailMinimum && !ours) continue;
            var weight = importance * 2 + (ours ? 1 : 0);
            if (weight > bestWeight) { best = line; bestWeight = weight; }
        }
        _audioNewsSeen = newest;
        if (best is null) return;
        var now = Time.GetTicksMsec();
        if (_lastNewsStingAt > 0 && now - _lastNewsStingAt < (ulong)NewsFeed.StingGapMs) return;
        _lastNewsStingAt = now;
        PlayStrategicCue(NewsFeed.Sting(best.Kind));
    }

    private ScenarioState FindStateByMapIndex(int mapIndex)
    {
        var states = _campaign.States;
        for (var i = 0; i < states.Count; i++)
            if (states[i].MapIndex == mapIndex) return states[i];
        return null;
    }

    private void RefreshDomesticAudio()
    {
        var nation = _campaign.Player;
        var initial = _audioObservedNation != nation.Id;
        var politicsChanged = !string.Equals(_audioGov, nation.Government, System.StringComparison.Ordinal) ||
                              !string.Equals(_audioIdeology, nation.Ideology, System.StringComparison.Ordinal);
        if (!initial)
        {
            if (_audioFormation != nation.FormedNationId) PlayStrategicCue("nation_formed");
            else if (politicsChanged) PlayStrategicCue("policy_changed");
        }
        else _audioConstruction.Clear();
        _audioObservedNation = nation.Id;
        _audioGov = nation.Government;
        _audioIdeology = nation.Ideology;
        _audioFormation = nation.FormedNationId;

        var constructions = _campaign.Construction;
        var queued = false;
        var finished = false;
        if (_audioConstruction.Count > 0)
        {
            _audioConstructionRemovals.Clear();
            foreach (var (id, previous) in _audioConstruction)
            {
                var stillPresent = false;
                for (var i = 0; i < constructions.Count; i++)
                {
                    var p = constructions[i];
                    if (p.Id == id && p.NationId == nation.Id) { stillPresent = true; break; }
                }
                if (stillPresent) continue;
                var state = FindStateByMapIndex(previous.State);
                // A cancellation or lost state is not a finished building.
                if (state?.NationMapIndex == nation.MapIndex && state.Buildings.GetValueOrDefault(previous.Building) > previous.Count)
                    finished = true;
                _audioConstructionRemovals.Add(id);
            }
            for (var i = 0; i < _audioConstructionRemovals.Count; i++)
                _audioConstruction.Remove(_audioConstructionRemovals[i]);
        }
        for (var i = 0; i < constructions.Count; i++)
        {
            var p = constructions[i];
            if (p.NationId != nation.Id || _audioConstruction.ContainsKey(p.Id)) continue;
            var state = FindStateByMapIndex(p.StateMapIndex);
            _audioConstruction[p.Id] = (p.StateMapIndex, p.Building, state?.Buildings.GetValueOrDefault(p.Building) ?? 0);
            queued = true;
        }
        if (!initial && finished) PlayStrategicCue("construction_complete");
        else if (!initial && queued) PlayStrategicCue("construction_queued");

        var supportReady = false;
        _audioSupportScratch.Clear();
        var formations = _campaign.SupportFormations;
        for (var i = 0; i < formations.Count; i++)
        {
            var f = formations[i];
            if (f.NationId != nation.Id) continue;
            _audioSupportScratch.Add(f.Id);
            if (!initial && !supportReady && !_audioSupportFormations.Contains(f.Id))
                supportReady = true;
        }
        _audioSupportFormations.Clear();
        foreach (var id in _audioSupportScratch) _audioSupportFormations.Add(id);
        if (supportReady) PlayStrategicCue("support_ready");

        var techCount = nation.Technologies.Count;
        if (!initial && _audioKnownTechCount >= 0 && techCount > _audioKnownTechCount)
            PlayStrategicCue("save_complete");
        _audioKnownTechCount = techCount;
    }
}
