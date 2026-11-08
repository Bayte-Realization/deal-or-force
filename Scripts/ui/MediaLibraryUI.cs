using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _mediaLibraryLayer, _mediaReturnFocus;
    private VBoxContainer _mediaLibraryList;
    private LineEdit _mediaLibrarySearch;
    private Label _mediaTitle, _mediaPurpose, _mediaSource, _mediaPreviewStatus;
    private TextureRect _mediaPicture;
    private CenterContainer _mediaListenHint;
    private Button _mediaPlay, _mediaStop, _mediaPause;
    private CheckBox _mediaLoop;
    private HSlider _mediaSeek;
    private Label _mediaClock, _mediaWaveCaption;
    private MediaWaveform _mediaWave;
    private Control _mediaTimeline;
    private double _mediaLength;
    private double? _mediaPausedSeek;
    private bool _mediaSeekDragging;
    private bool IsMediaPreviewActive => (_mediaAudio?.HasStreamPlayback() ?? false) || (_mediaVideo?.IsPlaying() ?? false);
    private AudioStreamPlayer _mediaAudio;
    private VideoStreamPlayer _mediaVideo;
    private LibraryMedia _mediaSelected;
    private LibraryMediaKind? _mediaKind;
    private int _mediaRevision = -1;

    private void ApplyAtlasHeading(PanelContainer panel)
    {
        if (Ui.Metric("atlas.heading_opacity", .16f) <= 0 || !SpriteCatalog.Has("atlas_header")) return;
        var file = SpriteCatalog.TextureOf("atlas_header", "");
        if (!ResourceLoader.Exists(file)) return;
        var ground = new Control { Name = "AtlasHeadingGround", MouseFilter = MouseFilterEnum.Ignore };
        panel.AddChild(ground); panel.MoveChild(ground, 0);
        var art = new TextureRect { Name = "AtlasHeadingArt", Texture = GD.Load<Texture2D>(file),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, Mathf.Clamp(Ui.Metric("atlas.heading_opacity", .16f), 0, .35f)) };
        art.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide); art.OffsetBottom = 84; ground.AddChild(art);
    }

    private readonly System.Collections.Generic.Dictionary<string, Button> _mediaFilterButtons = new();
    private void BuildMediaLibrary()
    {
        _mediaLibraryLayer = FullLayer("MediaLibrary"); _mediaLibraryLayer.Visible = false; AddChild(_mediaLibraryLayer);
        var veil = new ColorRect { Color = Ui.Colour("surface.overlay", .9f), MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _mediaLibraryLayer.AddChild(veil);
        var panel = TexturedPanel(_mediaLibraryLayer); panel.Name = "MediaLibraryPanel"; Ui.Place(panel, "workspace.media");
        var rows = PanelRows(panel, 22, 16, 10);
        var close = ActionButton("×", CloseMediaLibrary, 38, 32); close.Name = "CloseMediaLibrary";
        ScreenHeader(rows, "SOUND AND ARTWORK",
            "Choose music for the campaign or audition individual materials. Volume follows your audio settings.",
            false, close);
        var filters = new HFlowContainer(); filters.AddThemeConstantOverride("h_separation", 6); rows.AddChild(filters);
        foreach (var kind in new LibraryMediaKind?[] { null, LibraryMediaKind.Music, LibraryMediaKind.Sound, LibraryMediaKind.Image, LibraryMediaKind.Video })
        {
            var capture = kind;
            var filterKey = kind?.ToString() ?? "All";
            var button = ActionButton(MediaKindName(kind), () => { _mediaKind = capture; RebuildMediaList(); }, 0, 30);
            button.Name = "MediaFilter_" + filterKey;
            button.SetMeta("atlas_tab_button", true);
            StyleButton(button, kind == _mediaKind);
            filters.AddChild(button);
            _mediaFilterButtons[filterKey] = button;
        }
        BuildSoundtrackPlayer(rows);
        var body = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 16); rows.AddChild(body);
        var left = new VBoxContainer { CustomMinimumSize = new(280, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = .7f, SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddChild(left);
        _mediaLibrarySearch = Placeholder(new LineEdit { Name = "MediaLibrarySearch", ClearButtonEnabled = true }, "Find a recording or texture...");
        _mediaLibrarySearch.TextChanged += _ => RebuildMediaList(); left.AddChild(_mediaLibrarySearch);
        var listFrame = new PanelContainer { Name = "MediaListFrame", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        listFrame.SetMeta("atlas_section_ground", "surface.inset");
        listFrame.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(listFrame, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        left.AddChild(listFrame);
        var scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true }; listFrame.AddChild(scroll);
        _mediaLibraryList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(_mediaLibraryList);
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.4f }; right.AddThemeConstantOverride("separation", 10); body.AddChild(right);
        _mediaTitle = SituationText("", 23, "text.bright"); _mediaTitle.Name = "MediaTitle"; right.AddChild(_mediaTitle);
        _mediaPurpose = SituationText("", 14, "text.body"); right.AddChild(_mediaPurpose);
        var preview = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new(0, 100) }; preview.AddThemeStyleboxOverride("panel", WarInsetStyle(8, 8)); right.AddChild(preview);
        _mediaPicture = new TextureRect { Name = "MediaPicture", ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore }; preview.AddChild(_mediaPicture);
        _mediaVideo = new VideoStreamPlayer { Name = "MediaVideo", Expand = true, Autoplay = false,
            MouseFilter = MouseFilterEnum.Ignore }; preview.AddChild(_mediaVideo); _mediaVideo.Hide();
        _mediaListenHint = new CenterContainer { Name = "MediaListenHint", MouseFilter = MouseFilterEnum.Ignore };
        preview.AddChild(_mediaListenHint);
        var hint = new VBoxContainer { CustomMinimumSize = new(360, 0), MouseFilter = MouseFilterEnum.Ignore };
        hint.AddThemeConstantOverride("separation", 12); _mediaListenHint.AddChild(hint);
        _mediaWave = new MediaWaveform { Name = "MediaWaveform", CustomMinimumSize = new(360, 110),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        hint.AddChild(_mediaWave);
        _mediaWaveCaption = SituationText("", 11, "text.caption");
        _mediaWaveCaption.HorizontalAlignment = HorizontalAlignment.Center; hint.AddChild(_mediaWaveCaption);
        var details = ActionButton("Material details", () => _mediaSource.Visible = !_mediaSource.Visible, 0, 26);
        details.Name = "MediaDetails"; details.TooltipText = Tr("Origin and local file path. The source record remains available here."); right.AddChild(details);
        _mediaSource = SituationText("", 11, "text.caption"); _mediaSource.Name = "MediaSource"; right.AddChild(_mediaSource); _mediaSource.Hide();
        _mediaPreviewStatus = SituationText("", 12, "text.body_soft"); _mediaPreviewStatus.Name = "MediaPreviewStatus"; right.AddChild(_mediaPreviewStatus);
        _mediaTimeline = new VBoxContainer(); right.AddChild(_mediaTimeline);
        _mediaClock = SituationText("", 12, "text.caption"); _mediaClock.Name = "MediaPlaybackClock"; _mediaTimeline.AddChild(_mediaClock);
        _mediaSeek = new HSlider { Name = "MediaSeek", MinValue = 0, MaxValue = 1, Step = .05,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 24) };
        _mediaTimeline.AddChild(_mediaSeek);
        _mediaSeek.DragStarted += () => _mediaSeekDragging = true;
        _mediaSeek.DragEnded += _ => { _mediaSeekDragging = false; SeekMediaPreview(_mediaSeek.Value); };
        _mediaSeek.ValueChanged += value => { if (!_mediaSeekDragging) SeekMediaPreview(value); };
        var actions = new HFlowContainer(); actions.AddThemeConstantOverride("h_separation", 8); right.AddChild(actions);
        _mediaPlay = ActionButton("Play preview", StartMediaPreview, 0, 36, true); _mediaPlay.Name = "PlayMediaPreview";
        _mediaStop = ActionButton("Stop preview", StopMediaPreview, 0, 36); _mediaStop.Name = "StopMediaPreview";
        _mediaPause = ActionButton("Pause preview", PauseMediaPreview, 0, 36); _mediaPause.Name = "PauseMediaPreview";
        _mediaLoop = new CheckBox { Name = "LoopMediaPreview", Text = Tr("Repeat recording"), ButtonPressed = false };
        _mediaLoop.SetMeta(LocSourceMeta, "Repeat recording");
        _mediaLoop.SetMeta(LocAppliedMeta, _mediaLoop.Text);
        Tip(_mediaLoop, "Off by default. Repeat only the recording you selected, not the campaign.");
        actions.AddChild(_mediaPlay); actions.AddChild(_mediaPause); actions.AddChild(_mediaStop); actions.AddChild(_mediaLoop);
        _soundtrackPlaySelected = ActionButton("Use as soundtrack", PlaySelectedSoundtrack, 0, 36, true);
        _soundtrackPlaySelected.Name = "PlaySelectedSoundtrack"; actions.AddChild(_soundtrackPlaySelected);
        _mediaAudio = new AudioStreamPlayer { Name = "MediaAudition", ProcessMode = ProcessModeEnum.Always }; AddChild(_mediaAudio);
        _mediaAudio.Finished += FinishMediaPreview; _mediaVideo.Finished += FinishMediaPreview;
        ApplyAtlasHeading(panel);
    }
    private static string MediaKindName(LibraryMediaKind? kind) => kind switch
    { LibraryMediaKind.Music => "Music", LibraryMediaKind.Sound => "Sound effects", LibraryMediaKind.Image => "Artwork", LibraryMediaKind.Video => "Video", _ => "All materials" };
    private void OpenSoundtrackLibrary()
    {
        _mediaKind = LibraryMediaKind.Music;
        OpenMediaLibrary();
        RebuildMediaList();
    }

    private void OpenMediaLibrary()
    {
        if (_mediaLibraryLayer is { Visible: true }) return;
        if (_mediaLibraryLayer is null) BuildMediaLibrary();
        if (_globe is not null) _globe.MediaLibraryInputBlocked = true;
        _mediaReturnFocus = GetViewport().GuiGetFocusOwner(); _mediaLibraryLayer.Show(); _mediaLibraryLayer.MoveToFront();
        RebuildMediaList(); _mediaLibraryLayer.FocusMode = FocusModeEnum.All; _mediaLibraryLayer.GrabFocus();
        RefreshSoundtrackPlayer();
    }
    private void CloseMediaLibrary()
    {
        // The audition player belongs to this window. The campaign decks do not.
        StopMediaPreview(); _mediaLibraryLayer?.Hide();
        if (_globe is not null) _globe.MediaLibraryInputBlocked = false;
        // Input-modality styling hides a pointer focus ring, not the keyboard's
        // return position. Escape/Tab users retain their visible focus path.
        RestoreDecisionFocus(ref _mediaReturnFocus);
    }
    private void RebuildMediaList()
    {
        var activeKey = _mediaKind?.ToString() ?? "All";
        foreach (var (key, btn) in _mediaFilterButtons)
            if (IsInstanceValid(btn)) StyleButton(btn, key == activeKey);
        var query = _mediaLibrarySearch.Text.Trim();
        foreach (var child in _mediaLibraryList.GetChildren()) { _mediaLibraryList.RemoveChild(child); child.QueueFree(); }
        var items = MediaLibraryCatalog.All.Where(x => (!_mediaKind.HasValue || x.Kind == _mediaKind) &&
            (query.Length == 0 || (Tr(x.Name) + " " + Tr(x.Purpose)).Contains(query, StringComparison.OrdinalIgnoreCase))).Take(128).ToArray();
        foreach (var entry in items)
        {
            var item = entry;
            var button = ActionButton(entry.Name, () => SelectMedia(item), 0, 34);
            button.Name = "MediaAsset_" + entry.Id; button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill; _mediaLibraryList.AddChild(button);
            ApplyLedgerButtonState(button, _mediaSelected?.Id == entry.Id);
        }
        if (items.Length == 0) _mediaLibraryList.AddChild(SituationText("No matching material.", 13, "text.caption"));
        var selected = items.FirstOrDefault(x => x.Id == _mediaSelected?.Id) ?? items.FirstOrDefault();
        SelectMedia(selected, refreshButtons: false); _mediaRevision = MediaLibraryCatalog.Revision;
        foreach (var child in _mediaLibraryList.GetChildren())
            if (child is Button button) ApplyLedgerButtonState(button, button.Name == "MediaAsset_" + selected?.Id);
    }
    private void SelectMedia(LibraryMedia item, bool refreshButtons = true)
    {
        StopMediaPreview(); _mediaSelected = item;
        _mediaLoop.ButtonPressed = false; _mediaLength = item?.DurationSeconds ?? 0;
        _mediaTitle.Text = item is null ? Tr("No matching material.") : Tr(item.Name);
        _mediaPurpose.Text = item is null ? "" : Tr(item.Purpose);
        _mediaSource.Text = item is null ? "" : Tr(item.Source) + "\n" + item.File;
        _mediaPicture.Texture = null; _mediaPicture.Show(); _mediaVideo.Hide();
        _mediaListenHint.Visible = item is { Kind: LibraryMediaKind.Music or LibraryMediaKind.Sound };
        _mediaWave.ShowRecording(item?.Peaks);
        _mediaWaveCaption.Text = Tr(item?.Peaks is { Count: > 0 } ? "Audio waveform" : "This package supplies no waveform.");
        _mediaWaveCaption.TooltipText = Tr("Measured mono amplitude, not a live spectrum.");
        var available = item is not null && (item.Kind is LibraryMediaKind.Music or LibraryMediaKind.Sound
            ? AudioAssets.Exists(item.File) : ResourceLoader.Exists(item.File));
        if (available && item.Kind == LibraryMediaKind.Image) _mediaPicture.Texture = GD.Load<Texture2D>(item.File);
        _mediaPlay.Visible = item is { Kind: LibraryMediaKind.Music or LibraryMediaKind.Sound or LibraryMediaKind.Video };
        _mediaPlay.Disabled = !available; _mediaStop.Visible = _mediaPlay.Visible;
        _mediaPause.Visible = _mediaPlay.Visible; _mediaLoop.Visible = _mediaPlay.Visible;
        _mediaTimeline.Visible = _mediaPlay.Visible;
        _mediaSeek.Visible = item is { Kind: LibraryMediaKind.Music or LibraryMediaKind.Sound };
        UpdateMediaTimeline(0);
        _mediaPreviewStatus.Text = item is null ? "" : !available ? Tr("Local file is unavailable. Check the package path.") :
            item.Kind == LibraryMediaKind.Image ? Tr("Static artwork preview; it does not represent geographic data.") : Tr("Ready for manual preview.");
        RefreshSoundtrackPlayer();
        if (refreshButtons) foreach (var child in _mediaLibraryList.GetChildren())
            if (child is Button button) ApplyLedgerButtonState(button, button.Name == "MediaAsset_" + item?.Id);
    }
    private void StartMediaPreview()
    {
        if (_mediaSelected is not { } item || !(item.Kind is LibraryMediaKind.Music or LibraryMediaKind.Sound
            ? AudioAssets.Exists(item.File) : ResourceLoader.Exists(item.File))) return;
        if (_audioChannels.Any(pair => pair.Value.Playing && SoundCatalog.GetChannel(pair.Key).Priority >= Ui.Metric("media.preview_interrupt_priority", 2)))
        { _mediaPreviewStatus.Text = Tr("Wait for the priority alert before auditioning."); return; }
        StopMediaPreview();
        if (item.Kind == LibraryMediaKind.Video)
        {
            var stream = GD.Load<VideoStream>(item.File);
            if (stream is null) { _mediaPreviewStatus.Text = Tr("This format cannot be previewed by the engine."); return; }
            SetMediaAuditionFocus(true); UpdateMediaGain();
            _mediaVideo.Stream = stream; _mediaVideo.Paused = false; _mediaVideo.Loop = false;
            _mediaPicture.Hide(); _mediaVideo.Show(); _mediaVideo.Play();
            _mediaLength = _mediaVideo.GetStreamLength();
        }
        else if (item.Kind is LibraryMediaKind.Music or LibraryMediaKind.Sound)
        {
            var stream = AudioAssets.Load(item.File);
            if (stream is null) { _mediaPreviewStatus.Text = Tr("This format cannot be previewed by the engine."); return; }
            // The score director may loop its cached resource. A manual audition owns its own copy.
            stream = (AudioStream)stream.Duplicate();
            if (stream is AudioStreamOggVorbis ogg) ogg.Loop = false;
            else if (stream is AudioStreamMP3 mp3) mp3.Loop = false;
            else if (stream is AudioStreamWav wav) wav.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;
            SetMediaAuditionFocus(true);
            _mediaLength = stream.GetLength();
            _mediaAudio.Stream = stream; _mediaAudio.StreamPaused = false; UpdateMediaGain(); _mediaAudio.Play();
        }
        UpdateMediaTimeline(0);
    }
    private void SetMediaAuditionFocus(bool active)
    {
        MusicDirector.Ensure(this).SuppressForAudition(active);
        if (_mapAmbience is not null) _mapAmbience.AuditionSuppressed = active;
    }
    private void PauseMediaPreview()
    {
        if (_mediaAudio.HasStreamPlayback())
        {
            var resume = _mediaAudio.StreamPaused; _mediaAudio.StreamPaused = !resume;
            if (resume && _mediaPausedSeek is { } position) { _mediaPausedSeek = null; _mediaAudio.Seek((float)position); }
        }
        else if (_mediaVideo.IsPlaying()) _mediaVideo.Paused = !_mediaVideo.Paused;
        RefreshMediaPreview();
    }
    private void SeekMediaPreview(double position)
    {
        if (!_mediaAudio.HasStreamPlayback() || _mediaLength <= 0 || !double.IsFinite(position)) return;
        var target = Math.Clamp(position, 0, Math.Max(0, _mediaLength - .01));
        // Native seek requires a playing voice. Store a paused seek and apply it on resume,
        // rather than briefly unpausing the sound or letting an end-of-stream event escape.
        if (_mediaAudio.StreamPaused) _mediaPausedSeek = target;
        else { _mediaPausedSeek = null; _mediaAudio.Seek((float)target); }
        UpdateMediaTimeline(target);
    }
    private void InterruptMediaForPriority(int priority)
    {
        if (!IsMediaPreviewActive || priority < Ui.Metric("media.preview_interrupt_priority", 2)) return;
        StopMediaPreview(); _mediaPreviewStatus.Text = Tr("Preview stopped for a priority alert.");
    }
    private void FinishMediaPreview()
    {
        if (_mediaLoop.ButtonPressed && _mediaLibraryLayer is { Visible: true })
        {
            if (_mediaSelected.Kind == LibraryMediaKind.Video) { _mediaVideo.Paused = false; _mediaVideo.Play(); }
            else { _mediaAudio.StreamPaused = false; _mediaAudio.Play(); }
            UpdateMediaTimeline(0); return;
        }
        StopMediaPreview(); UpdateMediaTimeline(_mediaLength);
        _mediaPreviewStatus.Text = Tr("Recording finished.");
    }
    private void UpdateMediaTimeline(double position)
    {
        if (_mediaSeek is null) return;
        var length = double.IsFinite(_mediaLength) ? Math.Max(0, _mediaLength) : 0;
        position = double.IsFinite(position) ? Math.Clamp(position, 0, length) : 0;
        _mediaSeek.MaxValue = Math.Max(1, length); _mediaSeek.Editable = _mediaAudio.HasStreamPlayback() && length > 0;
        if (!_mediaSeekDragging) _mediaSeek.SetValueNoSignal(position);
        static string Clock(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";
        _mediaClock.Text = Clock(position) + " / " + (length > 0 ? Clock(length) : Tr("Unknown duration"));
        _mediaWave.ShowProgress(length > 0 ? position / length : 0);
        var paused = _mediaAudio.HasStreamPlayback() ? _mediaAudio.StreamPaused : _mediaVideo.Paused;
        _mediaPause.Text = Tr(paused ? "Resume preview" : "Pause preview"); _mediaPause.Disabled = !IsMediaPreviewActive;
    }
    private void StopMediaPreview()
    {
        _mediaAudio?.Stop(); _mediaVideo?.Stop();
        _mediaPausedSeek = null;
        if (_mediaAudio is not null) { _mediaAudio.StreamPaused = false; _mediaVideo.Paused = false; SetMediaAuditionFocus(false); }
        _mediaSeekDragging = false; UpdateMediaTimeline(0);
        if (_mediaStop is not null) _mediaStop.Disabled = true;
        if (_mediaPreviewStatus is not null && _mediaSelected is not null) _mediaPreviewStatus.Text = Tr("Ready for manual preview.");
    }
    private void UpdateMediaGain()
    {
        var gain = Mathf.Clamp(SettingsCatalog.Number("volume_master") * SettingsCatalog.Number(
            _mediaSelected?.Kind == LibraryMediaKind.Music ? "volume_music" : "volume_effects"), 0, 1);
        if (_mediaAudio is not null) _mediaAudio.VolumeDb = gain <= .0005f ? -80 :
            Mathf.LinearToDb(gain) - 4 + (_mediaSelected?.VolumeDb ?? 0);
        if (_mediaVideo is not null) _mediaVideo.VolumeDb = gain <= .0005f ? -80 : Mathf.LinearToDb(gain) - 10;
    }
    private void RefreshMediaPreview()
    {
        if (_mediaLibraryLayer is not { Visible: true }) return;
        if (_mediaRevision != MediaLibraryCatalog.Revision) { RebuildMediaList(); return; }
        UpdateMediaGain();
        var playing = IsMediaPreviewActive; _mediaStop.Disabled = !playing;
        if (!playing || _mediaSelected is null) return;
        var position = _mediaAudio.HasStreamPlayback() ? _mediaPausedSeek ?? _mediaAudio.GetPlaybackPosition() : _mediaVideo.StreamPosition;
        UpdateMediaTimeline(position);
        var silent = SettingsCatalog.Number("volume_master") * SettingsCatalog.Number(
            _mediaSelected.Kind == LibraryMediaKind.Music ? "volume_music" : "volume_effects") <= .0005f;
        var paused = _mediaAudio.HasStreamPlayback() ? _mediaAudio.StreamPaused : _mediaVideo.Paused;
        _mediaPreviewStatus.Text = Tr(paused ? "Preview paused" : silent ? "Muted" : "Playing");
    }
    private bool HandleMediaLibraryInput(InputEvent input)
    {
        if (_mediaLibraryLayer is not { Visible: true }) return false;
        if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            if (GetViewport().GuiGetFocusOwner() is LineEdit edit) edit.ReleaseFocus(); else CloseMediaLibrary();
            GetViewport().SetInputAsHandled(); return true;
        }
        // Let text fields and buttons receive keys, but block campaign/global hotkeys.
        if (GetViewport().GuiGetFocusOwner() is LineEdit) return false;
        if (input is InputEventKey { Keycode: Key.Space } && GetViewport().GuiGetFocusOwner() is BaseButton) return false;
        if (input is InputEventKey { Keycode: Key.Home or Key.End } && GetViewport().GuiGetFocusOwner() is Slider) return false;
        if (input is InputEventKey { Pressed: true, Keycode: Key.Space or Key.F1 or Key.F2 or Key.F3 or Key.F5 or Key.F7 or Key.F9 or Key.F10 })
        { GetViewport().SetInputAsHandled(); return true; }
        if (input is InputEventKey { Pressed: true } key && GetViewport().GuiGetFocusOwner() is not LineEdit &&
            key.Keycode is not (Key.Enter or Key.KpEnter or Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right))
        { GetViewport().SetInputAsHandled(); return true; }
        return false;
    }
}
