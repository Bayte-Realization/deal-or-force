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
    private Button _mediaPlay, _mediaStop;
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

    private void BuildMediaLibrary()
    {
        _mediaLibraryLayer = FullLayer("MediaLibrary"); _mediaLibraryLayer.Visible = false; AddChild(_mediaLibraryLayer);
        var veil = new ColorRect { Color = Ui.Colour("surface.overlay", .9f), MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _mediaLibraryLayer.AddChild(veil);
        var panel = TexturedPanel(_mediaLibraryLayer); panel.Name = "MediaLibraryPanel"; Ui.Place(panel, "workspace.media");
        var rows = PanelRows(panel, 22, 16, 10);
        var header = new HBoxContainer(); rows.AddChild(header);
        var title = SituationText("SOUND AND ARTWORK", 24, "text.accent"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var close = ActionButton("×", CloseMediaLibrary, 38, 32); close.Name = "CloseMediaLibrary"; header.AddChild(close);
        rows.AddChild(SituationText("Audition without changing the campaign. Volume follows your audio settings.", 12, "text.caption"));
        var filters = new HFlowContainer(); filters.AddThemeConstantOverride("h_separation", 6); rows.AddChild(filters);
        foreach (var kind in new LibraryMediaKind?[] { null, LibraryMediaKind.Music, LibraryMediaKind.Sound, LibraryMediaKind.Image, LibraryMediaKind.Video })
        {
            var capture = kind;
            var button = ActionButton(MediaKindName(kind), () => { _mediaKind = capture; RebuildMediaList(); }, 0, 28);
            button.Name = "MediaFilter_" + (kind?.ToString() ?? "All"); filters.AddChild(button);
        }
        var body = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 16); rows.AddChild(body);
        var left = new VBoxContainer { CustomMinimumSize = new(280, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = .7f, SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddChild(left);
        _mediaLibrarySearch = Placeholder(new LineEdit { Name = "MediaLibrarySearch", ClearButtonEnabled = true }, "Find a recording or texture...");
        _mediaLibrarySearch.TextChanged += _ => RebuildMediaList(); left.AddChild(_mediaLibrarySearch);
        var scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true }; left.AddChild(scroll);
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
        var hint = new VBoxContainer { CustomMinimumSize = new(220, 0), MouseFilter = MouseFilterEnum.Ignore };
        hint.AddThemeConstantOverride("separation", 12); _mediaListenHint.AddChild(hint);
        var mark = SituationText("♫", 44, "text.accent"); mark.HorizontalAlignment = HorizontalAlignment.Center; hint.AddChild(mark);
        var instruction = SituationText("This is an audio recording. Press Play preview to listen.", 13, "text.caption");
        instruction.CustomMinimumSize = new(220, 0); instruction.HorizontalAlignment = HorizontalAlignment.Center; hint.AddChild(instruction);
        _mediaSource = SituationText("", 11, "text.caption"); right.AddChild(_mediaSource);
        _mediaPreviewStatus = SituationText("", 12, "text.body_soft"); _mediaPreviewStatus.Name = "MediaPreviewStatus"; right.AddChild(_mediaPreviewStatus);
        var actions = new HBoxContainer(); right.AddChild(actions);
        _mediaPlay = ActionButton("Play preview", StartMediaPreview, 0, 36, true); _mediaPlay.Name = "PlayMediaPreview";
        _mediaStop = ActionButton("Stop preview", StopMediaPreview, 0, 36); _mediaStop.Name = "StopMediaPreview";
        actions.AddChild(_mediaPlay); actions.AddChild(_mediaStop);
        _mediaAudio = new AudioStreamPlayer { Name = "MediaAudition", ProcessMode = ProcessModeEnum.Always }; AddChild(_mediaAudio);
        _mediaAudio.Finished += StopMediaPreview; _mediaVideo.Finished += StopMediaPreview;
        ApplyAtlasHeading(panel);
    }
    private static string MediaKindName(LibraryMediaKind? kind) => kind switch
    { LibraryMediaKind.Music => "Music", LibraryMediaKind.Sound => "Sound effects", LibraryMediaKind.Image => "Artwork", LibraryMediaKind.Video => "Video", _ => "All materials" };
    private void OpenMediaLibrary()
    {
        if (_mediaLibraryLayer is { Visible: true }) return;
        if (_mediaLibraryLayer is null) BuildMediaLibrary();
        if (_globe is not null) _globe.MediaLibraryInputBlocked = true;
        _mediaReturnFocus = GetViewport().GuiGetFocusOwner(); _mediaLibraryLayer.Show(); _mediaLibraryLayer.MoveToFront();
        RebuildMediaList(); _mediaLibraryLayer.FocusMode = FocusModeEnum.All; _mediaLibraryLayer.GrabFocus();
    }
    private void CloseMediaLibrary()
    {
        StopMediaPreview(); _mediaLibraryLayer?.Hide();
        if (_globe is not null) _globe.MediaLibraryInputBlocked = false;
        if (IsInstanceValid(_mediaReturnFocus) && _mediaReturnFocus.IsVisibleInTree()) _mediaReturnFocus.GrabFocus();
        _mediaReturnFocus = null;
    }
    private void RebuildMediaList()
    {
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
        _mediaTitle.Text = item is null ? Tr("No matching material.") : Tr(item.Name);
        _mediaPurpose.Text = item is null ? "" : Tr(item.Purpose);
        _mediaSource.Text = item is null ? "" : Tr(item.Source) + "\n" + item.File;
        _mediaPicture.Texture = null; _mediaPicture.Show(); _mediaVideo.Hide();
        _mediaListenHint.Visible = item is { Kind: LibraryMediaKind.Music or LibraryMediaKind.Sound };
        var available = item is not null && ResourceLoader.Exists(item.File);
        if (available && item.Kind == LibraryMediaKind.Image) _mediaPicture.Texture = GD.Load<Texture2D>(item.File);
        _mediaPlay.Visible = item is { Kind: LibraryMediaKind.Music or LibraryMediaKind.Sound or LibraryMediaKind.Video };
        _mediaPlay.Disabled = !available; _mediaStop.Visible = _mediaPlay.Visible;
        _mediaPreviewStatus.Text = item is null ? "" : !available ? Tr("Local file is unavailable. Check the package path.") :
            item.Kind == LibraryMediaKind.Image ? Tr("Static artwork preview; it does not represent geographic data.") : Tr("Ready for manual preview.");
        if (refreshButtons) foreach (var child in _mediaLibraryList.GetChildren())
            if (child is Button button) ApplyLedgerButtonState(button, button.Name == "MediaAsset_" + item?.Id);
    }
    private void StartMediaPreview()
    {
        if (_mediaSelected is not { } item || !ResourceLoader.Exists(item.File)) return;
        StopMediaPreview();
        if (item.Kind == LibraryMediaKind.Video)
        {
            var stream = GD.Load<VideoStream>(item.File);
            if (stream is null) { _mediaPreviewStatus.Text = Tr("This format cannot be previewed by the engine."); return; }
            MusicDirector.Ensure(this).SuppressForAudition(true); UpdateMediaGain();
            _mediaVideo.Stream = stream; _mediaPicture.Hide(); _mediaVideo.Show(); _mediaVideo.Play();
        }
        else if (item.Kind is LibraryMediaKind.Music or LibraryMediaKind.Sound)
        {
            var stream = GD.Load<AudioStream>(item.File);
            if (stream is null) { _mediaPreviewStatus.Text = Tr("This format cannot be previewed by the engine."); return; }
            MusicDirector.Ensure(this).SuppressForAudition(true);
            _mediaAudio.Stream = stream; UpdateMediaGain(); _mediaAudio.Play();
        }
    }
    private void StopMediaPreview()
    {
        _mediaAudio?.Stop(); _mediaVideo?.Stop();
        if (_mediaAudio is not null) MusicDirector.Ensure(this).SuppressForAudition(false);
        if (_mediaStop is not null) _mediaStop.Disabled = true;
        if (_mediaPreviewStatus is not null && _mediaSelected is not null) _mediaPreviewStatus.Text = Tr("Ready for manual preview.");
    }
    private void UpdateMediaGain()
    {
        var gain = Mathf.Clamp(SettingsCatalog.Number("volume_master") * SettingsCatalog.Number(
            _mediaSelected?.Kind == LibraryMediaKind.Music ? "volume_music" : "volume_effects"), 0, 1);
        if (_mediaAudio is not null) _mediaAudio.VolumeDb = gain <= .0005f ? -80 : Mathf.LinearToDb(gain) - 4;
        if (_mediaVideo is not null) _mediaVideo.VolumeDb = gain <= .0005f ? -80 : Mathf.LinearToDb(gain) - 10;
    }
    private void RefreshMediaPreview()
    {
        if (_mediaLibraryLayer is not { Visible: true }) return;
        if (_mediaRevision != MediaLibraryCatalog.Revision) { RebuildMediaList(); return; }
        UpdateMediaGain();
        var playing = _mediaAudio.Playing || _mediaVideo.IsPlaying(); _mediaStop.Disabled = !playing;
        if (!playing || _mediaSelected is null) return;
        var position = _mediaAudio.Playing ? _mediaAudio.GetPlaybackPosition() : _mediaVideo.StreamPosition;
        var silent = SettingsCatalog.Number("volume_master") * SettingsCatalog.Number(
            _mediaSelected.Kind == LibraryMediaKind.Music ? "volume_music" : "volume_effects") <= .0005f;
        _mediaPreviewStatus.Text = Loc.Format("Preview {0:0.0}s · {1}", position, Tr(silent ? "Muted" : "Playing"));
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
        if (input is InputEventKey { Pressed: true, Keycode: Key.Space or Key.F1 or Key.F2 or Key.F3 or Key.F5 or Key.F7 or Key.F9 or Key.F10 })
        { GetViewport().SetInputAsHandled(); return true; }
        if (input is InputEventKey { Pressed: true } key && GetViewport().GuiGetFocusOwner() is not LineEdit &&
            key.Keycode is not (Key.Enter or Key.KpEnter or Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right))
        { GetViewport().SetInputAsHandled(); return true; }
        return false;
    }
}
