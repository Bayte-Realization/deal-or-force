using System;
using System.Globalization;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Journal-style Date &amp; World plate (Sovereign Brass &amp; Obsidian chrome): play/pause glyph +
/// tabular date/hour readout + weekday + state-tinted pause/speed/observer note over a hand-ruled
/// stroke, 5 tactile brass-outlined speed chevrons, DEFCON 5-lamp strip with notched 75% tension
/// bar, and brass-hovered Esc menu button.
/// </summary>
internal sealed class ShellDatePlateView
{
    private readonly ShellContext _ctx;
    private readonly Action _onToggleWorldPopover;

    private PanelContainer _plate;
    private Button _datePauseBtn;
    private TextureRect _pauseGlyph;
    private Label _dateLabel;
    private Label _weekdayLabel;
    private Label _stateNoteLabel;
    private Control _ruledStroke;

    private StyleBoxFlat _dateRunningStyle;
    private StyleBoxFlat _datePausedStyle;
    private StyleBoxFlat _dateObserverStyle;
    private StyleBoxFlat _dateHoverStyle;

    private PanelContainer _speedWell;
    private readonly Button[] _speedChevrons = new Button[8];

    private Button _worldBtn;
    private HBoxContainer _worldRow;
    private Label _defconCap;
    private Label _defconNumLabel;
    private HBoxContainer _lampsRow;
    private readonly Control[] _defconLamps = new Control[5];
    private TextureRect _tensionGlyph;
    private Label _tensionNumLabel;
    private Control _tensionBar;

    private Control _menuDivider;
    private Button _menuBtn;
    private PanelContainer _pauseKbd;
    private PanelContainer _menuKbd;

    private ShellLayoutProfile _profile;
    private HBoxContainer _dateRow;
    private (int Year, int Month, int Day, int Hour)? _lastDate;
    private (int Year, int Month, int Day, string Language)? _lastWeekday;
    private string _lastDateLang = "";
    private bool? _lastPaused;
    private bool? _lastObserverMode;
    private string _lastStateLang = "";
    private int _lastSpeed = -1;
    private int _lastWorldDay = int.MinValue;
    private int _lastWorldWarRev = int.MinValue;
    private int _lastDefcon = -1;
    private float _lastTension = -1f;

    public ShellDatePlateView(ShellContext ctx, Action onToggleWorldPopover)
    {
        _ctx = ctx;
        _onToggleWorldPopover = onToggleWorldPopover;
    }

    public void Build()
    {
        _plate = new PanelContainer
        {
            Name = "ShellDatePlate",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _plate.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 14, 0, 0, 0, 0, "shell.plate_bottom", "shell.border", 3));
        _ctx.Root.AddChild(_plate);
        ChromePlateArt.AttachSurfaceTexture(_plate, "blueprint", 0.26f);

        var canvas = new Control { ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _plate.AddChild(canvas);

        BuildDateLine(canvas);
        BuildSpeedChevrons(canvas);
        BuildWorldStrip(canvas);
        BuildMenuButton(canvas);
        _ctx.AttachPlateFrame(_plate);
    }

    private void BuildDateLine(Control canvas)
    {
        _dateRunningStyle = Ui.ChromeBox("shell.plate_top", 0.30f, "shell.engrave", 0.65f, cornerRadius: 2, borderWidth: 1);
        _datePausedStyle = Ui.ChromeBox("shell.state.warning", 0.14f, "shell.state.warning", 0.65f, cornerRadius: 2, borderWidth: 1, leftBorderWidth: 3);
        _dateObserverStyle = Ui.ChromeBox("shell.state.info", 0.14f, "shell.state.info", 0.72f, cornerRadius: 2, borderWidth: 1, leftBorderWidth: 3);
        _dateHoverStyle = Ui.ChromeBox("shell.plate_hover", 0.84f, "shell.brass_hi", 0.82f, cornerRadius: 2, borderWidth: 1);

        _datePauseBtn = new Button
        {
            Name = "ShellDatePauseButton",
            ClipContents = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        _datePauseBtn.AddThemeStyleboxOverride("normal", _datePausedStyle);
        _datePauseBtn.AddThemeStyleboxOverride("hover", _dateHoverStyle);
        _datePauseBtn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle());
        _datePauseBtn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            _ctx.TogglePause?.Invoke();
            Refresh();
        };
        _datePauseBtn.TooltipText = Ui.Tip(Loc.Get("Pause or resume campaign time (Space)."));
        canvas.AddChild(_datePauseBtn);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        row.AddThemeConstantOverride("separation", 6);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 7;
        row.OffsetRight = -7;
        _datePauseBtn.AddChild(row);
        _dateRow = row;

        _pauseGlyph = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = _ctx.Icon("icon_shell_play"),
            CustomMinimumSize = new Vector2(16, 16),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.paper_muted"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddChild(_pauseGlyph);

        _dateLabel = new Label
        {
            Text = "1 Jan 2024",
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _dateLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
        _dateLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(_dateLabel);

        _weekdayLabel = new Label
        {
            Text = "Mon",
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _weekdayLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif"));
        _weekdayLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        row.AddChild(_weekdayLabel);

        _stateNoteLabel = new Label
        {
            Text = Loc.Get("shell.paused_note"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _stateNoteLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
        _stateNoteLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.pause_amber"));
        row.AddChild(_stateNoteLabel);

        _ruledStroke = ShellContext.CreateRuledLine(5, "shell.brass_dim", 1.4f, 5f);
        canvas.AddChild(_ruledStroke);

        _pauseKbd = _ctx.CreateKeycapBadge("Space");
        canvas.AddChild(_pauseKbd);
    }

    private void BuildSpeedChevrons(Control canvas)
    {
        _speedWell = new PanelContainer
        {
            Name = "ShellSpeedWell",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _speedWell.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("well_recessed_9s", 10, 4, 2, 4, 2, "shell.well_top", "shell.border", 2));
        canvas.AddChild(_speedWell);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 2);
        _speedWell.AddChild(row);

        var empty = new StyleBoxEmpty();
        for (var i = 0; i < _speedChevrons.Length; i++)
        {
            var speedVal = i + 1;
            var btn = new Button
            {
                Name = $"ShellSpeedPip_{speedVal}",
                Text = "",
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None
            };
            btn.AddThemeStyleboxOverride("normal", empty);
            btn.AddThemeStyleboxOverride("hover", empty);
            btn.AddThemeStyleboxOverride("pressed", empty);
            btn.Pressed += () =>
            {
                _ctx.PlayCue?.Invoke("ui_select");
                _ctx.SetSpeed?.Invoke(speedVal);
                Refresh();
            };
            btn.TooltipText = Ui.Tip(Loc.Format("Simulation speed {0} (keys 1-8).", speedVal));
            btn.Draw += () => DrawChevronMark(btn, speedVal);
            row.AddChild(btn);
            _speedChevrons[i] = btn;
        }
    }

    private void DrawChevronMark(Button btn, int speedVal)
    {
        var w = btn.Size.X;
        var h = btn.Size.Y;
        if (w <= 2f || h <= 2f) return;
        Vector2[] pts =
        {
            new(0.5f, 1f),
            new(w * 0.58f, 1f),
            new(w - 0.5f, h * 0.5f),
            new(w * 0.58f, h - 1f),
            new(0.5f, h - 1f),
            new(w * 0.42f, h * 0.5f)
        };
        var curSpeed = Math.Clamp(_ctx.GetSpeed?.Invoke() ?? 1, 1, _speedChevrons.Length);
        var paused = _ctx.IsPaused?.Invoke() ?? true;
        Color col;
        Color rim;
        if (btn.IsHovered())
        {
            col = Ui.Colour("shell.highlight");
            rim = Ui.Colour("shell.brass_hi", 0.90f);
        }
        else if (speedVal == curSpeed)
        {
            col = paused ? Ui.Colour("shell.state.warning", 0.72f) : Ui.Colour("shell.brass_hi");
            rim = Ui.Colour("shell.paper", paused ? 0.45f : 0.75f);
        }
        else if (speedVal < curSpeed)
        {
            col = paused ? Ui.Colour("shell.paper_muted", 0.48f) : Ui.Colour("shell.brass", 0.90f);
            rim = Ui.Colour("shell.border", 0.75f);
        }
        else
        {
            col = Ui.Colour("shell.engrave", 0.78f);
            rim = Ui.Colour("shell.border", 0.65f);
        }
        btn.DrawColoredPolygon(pts, col);
        Vector2[] closed = { pts[0], pts[1], pts[2], pts[3], pts[4], pts[5], pts[0] };
        btn.DrawPolyline(closed, rim, 1f, antialiased: true);
    }

    private void BuildWorldStrip(Control canvas)
    {
        var wellNormal = _ctx.NineSlice("well_recessed_9s", 10, 6, 2, 6, 2, "shell.well_top", "shell.border", 2);
        var wellHover = Ui.ChromeBox("shell.plate_hover", 0.90f, "shell.brass_hi", 0.80f, cornerRadius: 2, borderWidth: 1, padX: 6, padY: 2);
        _worldBtn = new Button
        {
            Name = "ShellWorldDefconButton",
            ClipContents = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        _worldBtn.AddThemeStyleboxOverride("normal", wellNormal);
        _worldBtn.AddThemeStyleboxOverride("hover", wellHover);
        _worldBtn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle());
        _worldBtn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            _onToggleWorldPopover?.Invoke();
        };
        _worldBtn.TooltipText = Ui.Tip(Loc.Get("World nuclear alert (DEFCON) and great-power tension. Threshold notch at 75%."));
        canvas.AddChild(_worldBtn);

        var row = new HBoxContainer { ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 5);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 7;
        row.OffsetRight = -7;
        _worldBtn.AddChild(row);
        _worldRow = row;

        var defconCap = new Label
        {
            Text = "DEFCON",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        defconCap.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        defconCap.AddThemeFontSizeOverride("font_size", 11);
        defconCap.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        row.AddChild(defconCap);
        _defconCap = defconCap;

        _defconNumLabel = new Label
        {
            Text = "5",
            CustomMinimumSize = new Vector2(14, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _defconNumLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
        _defconNumLabel.AddThemeFontSizeOverride("font_size", 16);
        _defconNumLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(_defconNumLabel);

        var lampsRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        lampsRow.AddThemeConstantOverride("separation", 3);
        lampsRow.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(lampsRow);
        _lampsRow = lampsRow;

        for (var i = 0; i < 5; i++)
        {
            var lampLevel = 5 - i;
            var lamp = new Control
            {
                CustomMinimumSize = new Vector2(8, 8),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            lamp.Draw += () =>
            {
                var r = lamp.Size.X * 0.5f;
                var center = new Vector2(r, r);
                var active = _lastDefcon == lampLevel;
                var col = !active
                    ? Ui.Colour("shell.plate_bottom")
                    : lampLevel <= 2
                        ? Ui.Colour("shell.state.negative")
                        : lampLevel <= 3
                            ? Ui.Colour("shell.state.warning")
                            : Ui.Colour("shell.paper_muted");
                lamp.DrawCircle(center, r, col);
                lamp.DrawArc(center, r - 0.4f, 0f, MathF.Tau, 12, Ui.Colour(active ? "shell.paper" : "shell.border", active ? 0.55f : 0.70f), 1f, true);
            };
            lampsRow.AddChild(lamp);
            _defconLamps[i] = lamp;
        }

        var vr = new Control
        {
            CustomMinimumSize = new Vector2(4, 18),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        vr.Draw += () =>
        {
            vr.DrawLine(new Vector2(1, 0), new Vector2(1, vr.Size.Y), Ui.Colour("shell.sep_dark"), 1f);
            vr.DrawLine(new Vector2(2, 0), new Vector2(2, vr.Size.Y), Ui.Colour("shell.brass_dim", 0.55f), 1f);
        };
        row.AddChild(vr);

        _tensionGlyph = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = _ctx.Icon("icon_shell_tension"),
            CustomMinimumSize = new Vector2(15, 15),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.paper_muted"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddChild(_tensionGlyph);

        _tensionNumLabel = new Label
        {
            Text = "0",
            CustomMinimumSize = new Vector2(24, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _tensionNumLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
        _tensionNumLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(_tensionNumLabel);

        _tensionBar = new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(40, 12),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _tensionBar.Draw += DrawTensionMeter;
        row.AddChild(_tensionBar);
    }

    private void BuildMenuButton(Control canvas)
    {
        _menuDivider = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _menuDivider.Draw += () =>
        {
            var h = _menuDivider.Size.Y;
            _menuDivider.DrawLine(new Vector2(0, 0), new Vector2(0, h), Ui.Colour("shell.sep_dark"), 1f);
            _menuDivider.DrawLine(new Vector2(1, 0), new Vector2(1, h), Ui.Colour("shell.brass_dim", 0.65f), 1f);
        };
        canvas.AddChild(_menuDivider);

        var well = _ctx.NineSlice("well_recessed_9s", 10, 0, 0, 0, 0, "shell.well_top", "shell.border", 2);
        var wellHover = Ui.ChromeBox("shell.plate_hover", 0.90f, "shell.brass_hi", 0.80f, cornerRadius: 2, borderWidth: 1);
        _menuBtn = new Button
        {
            Name = "ShellMenuButton",
            ClipContents = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        _menuBtn.AddThemeStyleboxOverride("normal", well);
        _menuBtn.AddThemeStyleboxOverride("hover", wellHover);
        _menuBtn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle());
        _menuBtn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            _ctx.ShowPauseMenu?.Invoke();
        };
        _menuBtn.TooltipText = Ui.Tip(Loc.Get("Game menu (Esc)"));

        var menuIcon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = _ctx.Icon("icon_shell_menu"),
            Modulate = Ui.Colour("shell.icon"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        menuIcon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        menuIcon.OffsetLeft = 10; menuIcon.OffsetRight = -10; menuIcon.OffsetTop = 10; menuIcon.OffsetBottom = -10;
        _menuBtn.AddChild(menuIcon);
        canvas.AddChild(_menuBtn);

        _menuKbd = _ctx.CreateKeycapBadge("Esc");
        canvas.AddChild(_menuKbd);
    }

    public void Layout(ShellLayoutProfile profile)
    {
        _profile = profile;
        var compact = profile.Compact;
        var touch = profile.TouchMode;
        ShellContext.PlaceRect(_plate, profile.DatePlate);

        var pw = profile.DatePlate.W;
        var ph = profile.DatePlate.H;
        var menuW = touch ? 58f : (compact ? 42f : 56f);
        var divX = pw - menuW - (touch ? 10f : (compact ? 8f : 12f));

        var d1W = divX - (touch ? 18f : (compact ? 12f : 20f));
        var d1H = touch ? 44f : (compact ? 24f : 34f);
        var d1X = touch ? 10f : (compact ? 8f : 12f);
        var d1Y = touch ? 6f : (compact ? 5f : 6f);

        ShellContext.PlaceRect(_datePauseBtn, new ShellRect(d1X, d1Y, d1W, d1H));
        if (_dateRow is not null)
        {
            _dateRow.OffsetLeft = touch ? 10 : 7;
            _dateRow.OffsetRight = touch ? -10 : -7;
        }
        _dateLabel.AddThemeFontSizeOverride("font_size", touch ? 16 : (compact ? 12 : 16));
        _weekdayLabel.AddThemeFontSizeOverride("font_size", touch ? 11 : (compact ? 10 : 12));
        _weekdayLabel.Visible = !compact && !touch;
        _stateNoteLabel.AddThemeFontSizeOverride("font_size", touch ? 12 : (compact ? 10 : 12));
        _stateNoteLabel.CustomMinimumSize = new Vector2(touch ? 78f : (compact ? 54f : 86f), 0f);

        ShellContext.PlaceRect(_ruledStroke, new ShellRect(d1X + (compact && !touch ? 22f : 28f), d1Y + d1H - 1f, d1W - (compact && !touch ? 26f : 32f), 5f));
        _ruledStroke.QueueRedraw();

        var botY = touch ? 54f : (compact ? 32f : 45f);
        var botH = touch ? 42f : (compact ? 21f : 30f);
        var speedW = touch ? Math.Clamp(MathF.Round((divX - 24f) * 0.50f), 194f, 224f) : (compact ? 114f : 146f);
        ShellContext.PlaceRect(_speedWell, new ShellRect(d1X, botY, speedW, botH));
        var touchPipW = Math.Clamp(MathF.Floor((speedW - 22f) / 8f), 21f, 25f);
        var touchPipH = Math.Max(24f, botH - 6f);
        for (var i = 0; i < _speedChevrons.Length; i++)
            _speedChevrons[i].CustomMinimumSize = touch
                ? new Vector2(touchPipW, touchPipH)
                : new Vector2(compact ? 11 : 14, compact ? 15 : 21);

        var worldX = d1X + speedW + (touch ? 6f : (compact ? 6f : 8f));
        var worldW = Math.Max(touch ? 136f : 100f, divX - worldX - (touch ? 8f : (compact ? 6f : 8f)));
        ShellContext.PlaceRect(_worldBtn, new ShellRect(worldX, botY, worldW, botH));
        if (_worldRow is not null)
        {
            _worldRow.OffsetLeft = touch ? 5 : 7;
            _worldRow.OffsetRight = touch ? -5 : -7;
            _worldRow.AddThemeConstantOverride("separation", touch ? 4 : 5);
        }
        if (_defconCap is not null) _defconCap.Text = touch ? "DEF" : "DEFCON";
        if (_lampsRow is not null) _lampsRow.Visible = !touch || worldW >= 230f;
        _tensionBar.CustomMinimumSize = new Vector2(touch ? 34f : 40f, 12f);
        _defconNumLabel.AddThemeFontSizeOverride("font_size", touch ? 15 : (compact ? 13 : 16));
        _tensionNumLabel.AddThemeFontSizeOverride("font_size", touch ? 13 : (compact ? 12 : 14));
        var lampSz = touch ? 8 : (compact ? 6 : 8);
        foreach (var l in _defconLamps) l.CustomMinimumSize = new Vector2(lampSz, lampSz);

        var padV = touch ? 6f : (compact ? 6f : 8f);
        ShellContext.PlaceRect(_menuDivider, new ShellRect(divX, padV + 2f, 2f, ph - (padV + 2f) * 2f));
        ShellContext.PlaceRect(_menuBtn, new ShellRect(divX + (touch ? 5f : (compact ? 4f : 6f)), padV, menuW, Math.Max(touch ? 88f : 36f, ph - padV * 2f)));

        _pauseKbd.Position = new Vector2(d1X + d1W - 56f, d1Y + 2f);
        _menuKbd.Position = new Vector2(pw - 32f, ph - 20f);
        if (touch) { _pauseKbd.Visible = false; _menuKbd.Visible = false; }
    }

    public void RefreshClock()
    {
        var world = _ctx.World;
        if (world is null) return;
        var hud = _ctx.HudSnapshot is { Version: > 0 } hs ? hs : null;

        ApplyDateAndClockState(
            _ctx.GetPresentationDate?.Invoke() ?? world.Date,
            hud?.IsPaused ?? (_ctx.IsPaused?.Invoke() ?? true),
            Math.Clamp(_ctx.GetSpeed?.Invoke() ?? (hud?.Speed ?? 1), 1, _speedChevrons.Length),
            _ctx.IsObserverMode?.Invoke() ?? (hud?.ObserverMode ?? world.ObserverMode));
    }

    public void Refresh()
    {
        var world = _ctx.World;
        if (world is null) return;

        RefreshClock();

        var hud = _ctx.HudSnapshot is { Version: > 0 } hs ? hs : null;
        var day = hud?.ElapsedDays ?? world.ElapsedDays;
        var warRev = hud?.WarRevision ?? world.WarRevision;
        if (_lastWorldDay != day || _lastWorldWarRev != warRev || _lastDefcon < 0)
        {
            _lastWorldDay = day;
            _lastWorldWarRev = warRev;
            var defcon = hud is not null ? hud.DefconLevel : Math.Clamp(DefconSystem.CurrentLevel(world), 1, 5);
            var tension = hud is not null ? hud.WorldTension : Math.Clamp(WorldWar.Tension(world) / 100f, 0f, 1f);
            if (_lastDefcon != defcon || MathF.Abs(_lastTension - tension) > 0.004f)
            {
                var defconTierChanged = _lastDefcon < 0 ||
                    (_lastDefcon <= 2 ? 0 : _lastDefcon <= 3 ? 1 : 2) != (defcon <= 2 ? 0 : defcon <= 3 ? 1 : 2);
                var prevTenInt = (int)MathF.Round(_lastTension * 100f);
                var tenInt = (int)MathF.Round(tension * 100f);
                var tenTierChanged = _lastDefcon < 0 ||
                    (prevTenInt >= 85 ? 0 : prevTenInt >= 75 ? 1 : 2) != (tenInt >= 85 ? 0 : tenInt >= 75 ? 1 : 2);
                var defconChanged = _lastDefcon != defcon;
                _lastDefcon = defcon;
                _lastTension = tension;

                if (defconChanged)
                {
                    var dStr = defcon.ToString(CultureInfo.InvariantCulture);
                    if (_defconNumLabel.Text != dStr) _defconNumLabel.Text = dStr;
                    if (defconTierChanged)
                        _defconNumLabel.AddThemeColorOverride("font_color",
                            Ui.Colour(defcon <= 2 ? "shell.state.negative_text" : defcon <= 3 ? "shell.state.warning" : "shell.paper"));
                    foreach (var l in _defconLamps) l.QueueRedraw();
                }

                var tStr = tenInt.ToString(CultureInfo.InvariantCulture);
                if (_tensionNumLabel.Text != tStr) _tensionNumLabel.Text = tStr;
                if (tenTierChanged)
                    _tensionNumLabel.AddThemeColorOverride("font_color",
                        Ui.Colour(tenInt >= 85 ? "shell.state.negative_text" : tenInt >= 75 ? "shell.state.warning" : "shell.paper"));
                _tensionBar.QueueRedraw();
            }
        }

        var showKbd = _ctx.AltKeycapsVisible && _profile?.TouchMode != true;
        if (_pauseKbd.Visible != showKbd) _pauseKbd.Visible = showKbd;
        if (_menuKbd.Visible != showKbd) _menuKbd.Visible = showKbd;
    }

    private void ApplyDateAndClockState(
        GameDate date, bool paused, int speed, bool observing)
    {
        var lang = Loc.Language;
        var dateKey = (date.Year, date.Month, date.Day, date.Hour);
        if (_lastDate != dateKey || _lastDateLang != lang)
        {
            _lastDate = dateKey;
            _lastDateLang = lang;
            _dateLabel.Text = date.Display(withTime: true);
        }
        var weekdayKey = (date.Year, date.Month, date.Day, lang);
        if (_weekdayLabel.Visible && _lastWeekday != weekdayKey)
        {
            _lastWeekday = weekdayKey;
            _weekdayLabel.Text = FormatWeekday(date);
        }

        if (_lastPaused != paused || _lastSpeed != speed || _lastObserverMode != observing || _lastStateLang != lang)
        {
            _lastPaused = paused;
            _lastSpeed = speed;
            _lastObserverMode = observing;
            _lastStateLang = lang;
            _pauseGlyph.Texture = _ctx.Icon(paused ? "icon_shell_play" : "icon_shell_pause");
            _pauseGlyph.Modulate = Ui.Colour(observing ? "shell.state.info" : (paused ? "shell.state.warning" : "shell.brass_hi"));
            _datePauseBtn.AddThemeStyleboxOverride("normal",
                observing ? _dateObserverStyle : (paused ? _datePausedStyle : _dateRunningStyle));
            _stateNoteLabel.Text = observing
                ? (paused ? Loc.Get("shell.obs.date_paused") : Loc.Format("shell.obs.date_running_fmt", speed))
                : (paused ? Loc.Get("shell.paused_note") : Loc.Format("shell.running_note", speed));
            _stateNoteLabel.AddThemeColorOverride("font_color", Ui.Colour(
                observing ? "shell.state.info" : (paused ? "shell.pause_amber" : "shell.brass_hi")));
            foreach (var c in _speedChevrons) c.QueueRedraw();
        }
    }

    private static string FormatWeekday(GameDate date)
    {
        try
        {
            var yr = Math.Clamp(date.Year, 1, 9990);
            var mo = Math.Clamp(date.Month, 1, 12);
            var dy = Math.Clamp(date.Day, 1, DateTime.DaysInMonth(yr, mo));
            var dt = new DateTime(yr, mo, dy);
            var ru = string.Equals(Loc.Language, "russian", StringComparison.OrdinalIgnoreCase);
            return dt.DayOfWeek switch
            {
                DayOfWeek.Monday => ru ? "пн" : "Mon",
                DayOfWeek.Tuesday => ru ? "вт" : "Tue",
                DayOfWeek.Wednesday => ru ? "ср" : "Wed",
                DayOfWeek.Thursday => ru ? "чт" : "Thu",
                DayOfWeek.Friday => ru ? "пт" : "Fri",
                DayOfWeek.Saturday => ru ? "сб" : "Sat",
                _ => ru ? "вс" : "Sun"
            };
        }
        catch
        {
            return "";
        }
    }

    private void DrawTensionMeter()
    {
        var w = _tensionBar.Size.X;
        var h = _tensionBar.Size.Y;
        if (w <= 4f || h <= 4f) return;

        var barH = 7f;
        var y = (h - barH) * 0.5f;
        _tensionBar.DrawRect(new Rect2(0, y, w, barH), Ui.Colour("shell.well"));
        _tensionBar.DrawRect(new Rect2(0, y, w, barH), Ui.Colour("shell.border", 0.70f), filled: false, width: 1f);

        var fillW = MathF.Max(0f, w * Math.Clamp(_lastTension, 0f, 1f));
        if (fillW > 0.5f)
        {
            var c0 = Ui.Colour("shell.paper_ghost");
            var c1 = Ui.Colour("shell.paper_faint");
            var c2 = Ui.Colour("shell.state.warning");
            var c3 = Ui.Colour("shell.state.negative");
            const int segs = 12;
            for (var i = 0; i < segs; i++)
            {
                var t0 = i / (float)segs;
                var t1 = (i + 1) / (float)segs;
                var x0 = t0 * w;
                if (x0 >= fillW) break;
                var x1 = MathF.Min(fillW, t1 * w);
                var tm = (t0 + t1) * 0.5f;
                var col = tm < 0.45f ? c0.Lerp(c1, tm / 0.45f)
                    : tm < 0.75f ? c1.Lerp(c2, (tm - 0.45f) / 0.30f)
                    : c2.Lerp(c3, (tm - 0.75f) / 0.25f);
                _tensionBar.DrawRect(new Rect2(x0, y, x1 - x0, barH), col);
            }
        }

        // Crisp 75% threshold notch with dark obsidian rim
        var notchX = MathF.Round(w * 0.75f);
        _tensionBar.DrawRect(new Rect2(notchX - 1f, y - 3f, 4f, barH + 6f), Ui.Colour("shell.border"));
        _tensionBar.DrawRect(new Rect2(notchX, y - 2f, 2f, barH + 4f), Ui.Colour("shell.paper"));
    }
}
