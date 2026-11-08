using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Label _soundtrackTitle, _soundtrackStatus, _soundtrackClock;
    private ProgressBar _soundtrackProgress;
    private OptionButton _soundtrackTracks;
    private int _soundtrackCatalogRevision = -1;
    private string _soundtrackLanguage = "";
    private Button _soundtrackPause, _soundtrackPlaySelected, _soundtrackAuto;
    private double _soundtrackUiClock;

    private void BuildSoundtrackPlayer(VBoxContainer parent)
    {
        var card = new PanelContainer { Name = "SoundtrackPlayer", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", WarInsetStyle(10, 7)); parent.AddChild(card);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 4); card.AddChild(rows);
        _soundtrackTitle = SituationText("Soundtrack", 15, "text.bright");
        var heading = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddChild(heading); _soundtrackTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _soundtrackTitle.Name = "SoundtrackTitle"; heading.AddChild(_soundtrackTitle);
        _soundtrackClock = SituationText("", 12, "text.caption"); _soundtrackClock.Name = "SoundtrackClock";
        _soundtrackClock.AutowrapMode = TextServer.AutowrapMode.Off; heading.AddChild(_soundtrackClock);
        _soundtrackProgress = BattleGauge(0, "text.accent", "SoundtrackProgress");
        _soundtrackProgress.CustomMinimumSize = new(0, 3); rows.AddChild(_soundtrackProgress);
        var controls = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        controls.AddThemeConstantOverride("h_separation", 6); controls.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(controls);
        _soundtrackTracks = new OptionButton { Name = "SoundtrackTrackSelector", CustomMinimumSize = new(230, 28),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false };
        _soundtrackTracks.TooltipText = Tr("Choose any declared soundtrack recording, including mod tracks.");
        _soundtrackTracks.AccessibilityName = Tr("Choose soundtrack recording");
        _soundtrackTracks.ItemSelected += index =>
        {
            StopMediaPreview();
            if (!MusicDirector.Ensure(this).PlayTrack(_soundtrackTracks.GetItemMetadata((int)index).AsString()))
                _mediaPreviewStatus.Text = Tr("This recording cannot be used as the soundtrack right now.");
            RefreshSoundtrackPlayer();
        };
        controls.AddChild(_soundtrackTracks);
        void Act(string text, string name, Action action)
        { var b = ActionButton(text, action, 0, 28); b.Name = name; controls.AddChild(b); }
        Act("Previous track", "SoundtrackPrevious", () => StepSoundtrack(-1));
        _soundtrackPause = ActionButton("Pause soundtrack", () =>
        { var music = MusicDirector.Ensure(this); music.SetPaused(!music.Paused); RefreshSoundtrackPlayer(); }, 0, 28);
        _soundtrackPause.Name = "SoundtrackPause"; controls.AddChild(_soundtrackPause);
        Act("Next track", "SoundtrackNext", () => StepSoundtrack(1));
        _soundtrackAuto = ActionButton("Automatic soundtrack", () =>
        { StopMediaPreview(); MusicDirector.Ensure(this).ResumeAutomatic(); RefreshSoundtrackPlayer(); }, 0, 28);
        _soundtrackAuto.Name = "SoundtrackAutomatic"; controls.AddChild(_soundtrackAuto);
        _soundtrackStatus = SituationText("", 12, "text.caption");
        _soundtrackStatus.Name = "SoundtrackStatus"; rows.AddChild(_soundtrackStatus);
    }

    private MusicCatalog.Track SelectedSoundtrack()
    {
        if (_mediaSelected?.Kind != LibraryMediaKind.Music) return default;
        var sameFile = MusicCatalog.All.Where(t => string.Equals(t.File, _mediaSelected.File, StringComparison.OrdinalIgnoreCase));
        return sameFile.FirstOrDefault(t => t.Name == _mediaSelected.Id, sameFile.FirstOrDefault());
    }

    private void PlaySelectedSoundtrack()
    {
        var track = SelectedSoundtrack();
        if (string.IsNullOrEmpty(track.Name)) return;
        StopMediaPreview();
        if (!MusicDirector.Ensure(this).PlayTrack(track.Name))
            _mediaPreviewStatus.Text = Tr("This recording cannot be used as the soundtrack right now.");
        RefreshSoundtrackPlayer();
    }

    private void StepSoundtrack(int direction)
    {
        StopMediaPreview();
        MusicDirector.Ensure(this).StepTrack(direction); RefreshSoundtrackPlayer();
    }


    private void RefreshSoundtrackPlayer(double delta = 1)
    {
        var libraryOpen = _mediaLibraryLayer is { Visible: true } && _soundtrackTitle is not null;
        if (!libraryOpen) return;
        _soundtrackUiClock += delta;
        if (_soundtrackUiClock < .25) return;
        _soundtrackUiClock = 0;
        var music = MusicDirector.Ensure(this);
        if (_soundtrackCatalogRevision != MusicCatalog.Revision || _soundtrackLanguage != Loc.Language)
        {
            _soundtrackTracks.Clear();
            foreach (var track in MusicCatalog.All.OrderBy(t => Loc.Get(t.Label), StringComparer.CurrentCultureIgnoreCase))
            {
                var at = _soundtrackTracks.ItemCount;
                _soundtrackTracks.AddItem(Loc.Get(track.Label)); _soundtrackTracks.SetItemMetadata(at, track.Name);
                _soundtrackTracks.SetItemDisabled(at, !ResourceLoader.Exists(track.File));
            }
            _soundtrackCatalogRevision = MusicCatalog.Revision; _soundtrackLanguage = Loc.Language;
        }
        var chosen = music.Manual ? music.RequestedTrackId : music.CurrentTrackId;
        for (var i = 0; i < _soundtrackTracks.ItemCount; i++)
            if (_soundtrackTracks.GetItemMetadata(i).AsString() == chosen) { _soundtrackTracks.Select(i); break; }
        _soundtrackTitle.Text = Loc.Format("Now playing: {0}", string.IsNullOrEmpty(music.CurrentTrack) ? Tr("No recording") : music.CurrentTrack);
        static string Clock(double value) => !double.IsFinite(value) || value < 0 ? "0:00" : $"{(int)value / 60}:{(int)value % 60:00}";
        _soundtrackClock.Text = Clock(music.PlaybackPosition) + " / " + Clock(music.PlaybackLength);
        _soundtrackProgress.Value = music.PlaybackLength > 0 ? Math.Clamp(music.PlaybackPosition / music.PlaybackLength, 0, 1) : 0;
        var pause = music.Paused ? "Resume soundtrack" : "Pause soundtrack";
        _soundtrackPause.Text = Tr(pause);
        _soundtrackPause.SetMeta(LocSourceMeta, pause); _soundtrackPause.SetMeta(LocAppliedMeta, _soundtrackPause.Text);
        StyleButton(_soundtrackAuto, !music.Manual);
        var presence = MusicTransport.Presence(music.Manual, music.Paused);
        _soundtrackStatus.Text = Tr(presence);
        if (_soundtrackPlaySelected is not null)
        {
            var selected = SelectedSoundtrack();
            _soundtrackPlaySelected.Visible = _mediaSelected?.Kind == LibraryMediaKind.Music;
            _soundtrackPlaySelected.Disabled = string.IsNullOrEmpty(selected.Name) || !ResourceLoader.Exists(selected.File);
            _soundtrackPlaySelected.TooltipText = Tr(string.IsNullOrEmpty(selected.Name)
                ? "Declare this recording in music/tracks to make it available in the soundtrack."
                : "Play during the campaign. Unlike preview, this continues after closing the library.");
        }
    }
}
