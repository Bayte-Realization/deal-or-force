using System;
using System.Globalization;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Journal-style Date & World plate (Revision 3): play/pause glyph + Source Serif 4 date + weekday +
/// right-aligned amber italic pause note over a hand-ruled stroke, 5 chevron speed marks, DEFCON 5-lamp
/// strip with notched 75% tension bar, and sunken Esc menu button.
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

    private PanelContainer _speedWell;
    private readonly Button[] _speedChevrons = new Button[5];

    private Button _worldBtn;
    private Label _defconNumLabel;
    private readonly Control[] _defconLamps = new Control[5];
    private TextureRect _tensionGlyph;
    private Label _tensionNumLabel;
    private Control _tensionBar;

    private Control _menuDivider;
    private Button _menuBtn;
    private PanelContainer _pauseKbd;
    private PanelContainer _menuKbd;

    private ShellLayoutProfile _profile;
    private string _lastDateKey = "";
    private bool? _lastPaused;
    private int _lastSpeed = -1;
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
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _plate.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 14, 0, 0, 0, 0, "shell.plate_bottom", "shell.border", 3));
        _ctx.Root.AddChild(_plate);

        var canvas = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _plate.AddChild(canvas);

        BuildDateLine(canvas);
        BuildSpeedChevrons(canvas);
        BuildWorldStrip(canvas);
        BuildMenuButton(canvas);
    }

    private void BuildDateLine(Control canvas)
    {
        var empty = new StyleBoxEmpty();
        var hover = new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.06f) };

        _datePauseBtn = new Button
        {
            Name = "ShellDatePauseButton",
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        _datePauseBtn.AddThemeStyleboxOverride("normal", empty);
        _datePauseBtn.AddThemeStyleboxOverride("hover", hover);
        _datePauseBtn.AddThemeStyleboxOverride("pressed", hover);
        _datePauseBtn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            _ctx.TogglePause?.Invoke();
            Refresh();
        };
        _datePauseBtn.TooltipText = Ui.Tip(Loc.Get("Pause or resume campaign time (Space)."));
        canvas.AddChild(_datePauseBtn);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 7);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 6;
        row.OffsetRight = -6;
        _datePauseBtn.AddChild(row);

        _pauseGlyph = new TextureRect
        {
            Texture = _ctx.Icon("icon_shell_play"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
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
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _dateLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold", tabular: true));
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

        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

        _stateNoteLabel = new Label
        {
            Text = Loc.Get("shell.paused_note"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _stateNoteLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        _stateNoteLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.pause_amber"));
        row.AddChild(_stateNoteLabel);

        _ruledStroke = ShellContext.CreateRuledLine(5, "shell.ruled_stroke", 1.6f, 5f);
        canvas.AddChild(_ruledStroke);

        _pauseKbd = _ctx.CreateKeycapBadge("Space");
        canvas.AddChild(_pauseKbd);
    }

    private void BuildSpeedChevrons(Control canvas)
    {
        _speedWell = new PanelContainer
        {
            Name = "ShellSpeedWell",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _speedWell.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("well_recessed_9s", 10, 4, 2, 4, 2, "shell.well_top", "shell.border", 2));
        canvas.AddChild(_speedWell);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 2);
        _speedWell.AddChild(row);

        for (var i = 0; i < 5; i++)
        {
            var speedVal = i + 1;
            var btn = new Button
            {
                Name = $"ShellSpeedPip_{speedVal}",
                Text = "",
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None
            };
            var empty = new StyleBoxEmpty();
            btn.AddThemeStyleboxOverride("normal", empty);
            btn.AddThemeStyleboxOverride("hover", empty);
            btn.AddThemeStyleboxOverride("pressed", empty);
            btn.Pressed += () =>
            {
                _ctx.PlayCue?.Invoke("ui_select");
                _ctx.SetSpeed?.Invoke(speedVal);
                Refresh();
            };
            btn.TooltipText = Ui.Tip(Loc.Format("Simulation speed {0} (keys 1-5).", speedVal));
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
        // Exact polygon(0 0, 58% 0, 100% 50%, 58% 100%, 0 100%, 42% 50%) from style.css .speed i
        Vector2[] pts =
        {
            new(0f, 0f),
            new(w * 0.58f, 0f),
            new(w, h * 0.5f),
            new(w * 0.58f, h),
            new(0f, h),
            new(w * 0.42f, h * 0.5f)
        };
        var curSpeed = Math.Clamp(_ctx.GetSpeed?.Invoke() ?? 1, 1, 5);
        var paused = _ctx.IsPaused?.Invoke() ?? true;
        Color col;
        if (btn.IsHovered())
            col = Ui.Colour("shell.paper_faint");
        else if (speedVal == curSpeed)
            col = Ui.Colour("shell.paper", paused ? 0.55f : 1f);
        else if (speedVal < curSpeed)
            col = Ui.Colour("shell.paper_muted", paused ? 0.45f : 1f);
        else
            col = Ui.Colour("shell.engrave");
        btn.DrawColoredPolygon(pts, col);
    }

    private void BuildWorldStrip(Control canvas)
    {
        var wellNormal = _ctx.NineSlice("well_recessed_9s", 10, 6, 2, 6, 2, "shell.well_top", "shell.border", 2);
        _worldBtn = new Button
        {
            Name = "ShellWorldDefconButton",
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        _worldBtn.AddThemeStyleboxOverride("normal", wellNormal);
        _worldBtn.AddThemeStyleboxOverride("hover", wellNormal);
        _worldBtn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle());
        _worldBtn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            _onToggleWorldPopover?.Invoke();
        };
        _worldBtn.TooltipText = Ui.Tip(Loc.Get("World nuclear alert (DEFCON) and great-power tension. Threshold notch at 75%."));
        canvas.AddChild(_worldBtn);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 5);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 7;
        row.OffsetRight = -7;
        _worldBtn.AddChild(row);

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

        _defconNumLabel = new Label
        {
            Text = "5",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _defconNumLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        _defconNumLabel.AddThemeFontSizeOverride("font_size", 16);
        _defconNumLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(_defconNumLabel);

        var lampsRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        lampsRow.AddThemeConstantOverride("separation", 3);
        lampsRow.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(lampsRow);

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
            };
            lampsRow.AddChild(lamp);
            _defconLamps[i] = lamp;
        }

        // Vertical groove (.vr) separating DEFCON lamps from World Tension
        var vr = new Control
        {
            CustomMinimumSize = new Vector2(4, 18),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        vr.Draw += () =>
        {
            vr.DrawLine(new Vector2(1, 0), new Vector2(1, vr.Size.Y), Ui.Colour("shell.sep_dark"), 1f);
            vr.DrawLine(new Vector2(2, 0), new Vector2(2, vr.Size.Y), Ui.Colour("shell.sep_light"), 1f);
        };
        row.AddChild(vr);

        _tensionGlyph = new TextureRect
        {
            Texture = _ctx.Icon("icon_shell_tension"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(15, 15),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.paper_muted"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddChild(_tensionGlyph);

        _tensionNumLabel = new Label
        {
            Text = "0",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _tensionNumLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
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
            _menuDivider.DrawLine(new Vector2(1, 0), new Vector2(1, h), Ui.Colour("shell.engrave"), 1f);
        };
        canvas.AddChild(_menuDivider);

        var well = _ctx.NineSlice("well_recessed_9s", 10, 0, 0, 0, 0, "shell.well_top", "shell.border", 2);
        _menuBtn = new Button
        {
            Name = "ShellMenuButton",
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        _menuBtn.AddThemeStyleboxOverride("normal", well);
        _menuBtn.AddThemeStyleboxOverride("hover", well);
        _menuBtn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle());
        _menuBtn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            _ctx.ShowPauseMenu?.Invoke();
        };
        _menuBtn.TooltipText = Ui.Tip(Loc.Get("Game menu (Esc)"));

        var menuIcon = new TextureRect
        {
            Texture = _ctx.Icon("icon_shell_menu"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
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
        ShellContext.PlaceRect(_plate, profile.DatePlate);

        var pw = profile.DatePlate.W;
        var ph = profile.DatePlate.H;
        var menuW = compact ? 42f : 56f;
        var divX = pw - menuW - (compact ? 8f : 12f);

        var d1W = divX - (compact ? 12f : 20f);
        var d1H = compact ? 24f : 34f;
        var d1X = compact ? 8f : 12f;
        var d1Y = compact ? 5f : 6f;

        ShellContext.PlaceRect(_datePauseBtn, new ShellRect(d1X, d1Y, d1W, d1H));
        _dateLabel.AddThemeFontSizeOverride("font_size", compact ? 13 : 17);
        _weekdayLabel.AddThemeFontSizeOverride("font_size", compact ? 10 : 12);
        _stateNoteLabel.AddThemeFontSizeOverride("font_size", compact ? 11 : 13);

        ShellContext.PlaceRect(_ruledStroke, new ShellRect(d1X + (compact ? 22f : 28f), d1Y + d1H - 2f, d1W - (compact ? 26f : 32f), 5f));
        _ruledStroke.QueueRedraw();

        var botY = compact ? 32f : 45f;
        var botH = compact ? 21f : 30f;
        var speedW = compact ? 86f : 112f;
        ShellContext.PlaceRect(_speedWell, new ShellRect(d1X, botY, speedW, botH));
        for (var i = 0; i < 5; i++)
            _speedChevrons[i].CustomMinimumSize = new Vector2(compact ? 13 : 17, compact ? 15 : 21);

        var worldX = d1X + speedW + (compact ? 6f : 8f);
        var worldW = Math.Max(100f, divX - worldX - (compact ? 6f : 8f));
        ShellContext.PlaceRect(_worldBtn, new ShellRect(worldX, botY, worldW, botH));
        _defconNumLabel.AddThemeFontSizeOverride("font_size", compact ? 13 : 16);
        _tensionNumLabel.AddThemeFontSizeOverride("font_size", compact ? 12 : 14);
        var lampSz = compact ? 6 : 8;
        foreach (var l in _defconLamps) l.CustomMinimumSize = new Vector2(lampSz, lampSz);

        ShellContext.PlaceRect(_menuDivider, new ShellRect(divX, compact ? 7f : 10f, 2f, ph - (compact ? 14f : 20f)));
        ShellContext.PlaceRect(_menuBtn, new ShellRect(divX + (compact ? 4f : 6f), compact ? 6f : 8f, menuW, ph - (compact ? 12f : 16f)));

        _pauseKbd.Position = new Vector2(d1X + d1W - 56f, d1Y + 2f);
        _menuKbd.Position = new Vector2(pw - 32f, ph - 20f);
    }

    public void Refresh()
    {
        var world = _ctx.World;
        if (world is null) return;

        var dateKey = $"{world.ElapsedHours}|{Loc.Language}";
        if (_lastDateKey != dateKey)
        {
            _lastDateKey = dateKey;
            _dateLabel.Text = world.Date.Display(withTime: true);
            _weekdayLabel.Text = FormatWeekday(world.StartYear, world.ElapsedDays);
        }

        var paused = _ctx.IsPaused?.Invoke() ?? true;
        var speed = Math.Clamp(_ctx.GetSpeed?.Invoke() ?? 1, 1, 5);
        if (_lastPaused != paused || _lastSpeed != speed)
        {
            _lastPaused = paused;
            _lastSpeed = speed;
            _pauseGlyph.Texture = _ctx.Icon(paused ? "icon_shell_play" : "icon_shell_pause");
            _stateNoteLabel.Text = Loc.Get(paused ? "shell.paused_note" : "shell.running_note");
            _stateNoteLabel.AddThemeColorOverride("font_color", Ui.Colour(paused ? "shell.pause_amber" : "shell.paper_faint"));
            foreach (var c in _speedChevrons) c.QueueRedraw();
        }

        var defcon = Math.Clamp(WorldWar.Defcon(world), 1, 5);
        var tension = Math.Clamp(WorldWar.Tension(world) / 100f, 0f, 1f);
        if (_lastDefcon != defcon || MathF.Abs(_lastTension - tension) > 0.004f)
        {
            _lastDefcon = defcon;
            _lastTension = tension;
            _defconNumLabel.Text = defcon.ToString(CultureInfo.InvariantCulture);
            _defconNumLabel.AddThemeColorOverride("font_color",
                Ui.Colour(defcon <= 2 ? "shell.state.negative_text" : defcon <= 3 ? "shell.state.warning" : "shell.paper"));
            foreach (var l in _defconLamps) l.QueueRedraw();

            var tenInt = (int)MathF.Round(tension * 100f);
            _tensionNumLabel.Text = tenInt.ToString(CultureInfo.InvariantCulture);
            _tensionNumLabel.AddThemeColorOverride("font_color",
                Ui.Colour(tenInt >= 85 ? "shell.state.negative_text" : tenInt >= 75 ? "shell.state.warning" : "shell.paper"));
            _tensionBar.QueueRedraw();
        }

        _pauseKbd.Visible = _ctx.AltKeycapsVisible;
        _menuKbd.Visible = _ctx.AltKeycapsVisible;
    }

    private static string FormatWeekday(int startYear, int elapsedDays)
    {
        try
        {
            var dt = new DateTime(Math.Clamp(startYear, 1, 9990), 1, 1).AddDays(Math.Max(0, elapsedDays));
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

        var fillW = MathF.Max(0f, w * Math.Clamp(_lastTension, 0f, 1f));
        if (fillW > 0.5f)
        {
            // Smooth multi-stop gradient matching style.css (#6f6757 -> #a39983 -> #e7b058 -> #c94f55)
            const int segs = 12;
            for (var i = 0; i < segs; i++)
            {
                var t0 = i / (float)segs;
                var t1 = (i + 1) / (float)segs;
                var x0 = t0 * w;
                if (x0 >= fillW) break;
                var x1 = MathF.Min(fillW, t1 * w);
                var col = SampleTensionGradient((t0 + t1) * 0.5f);
                _tensionBar.DrawRect(new Rect2(x0, y, x1 - x0, barH), col);
            }
        }

        // Crisp 75% white/paper threshold notch with 1px dark outline
        var notchX = MathF.Round(w * 0.75f);
        _tensionBar.DrawRect(new Rect2(notchX - 1f, y - 3f, 4f, barH + 6f), Colors.Black);
        _tensionBar.DrawRect(new Rect2(notchX, y - 2f, 2f, barH + 4f), Ui.Colour("shell.paper"));
    }

    private static Color SampleTensionGradient(float t)
    {
        var c0 = Ui.Colour("shell.paper_ghost");
        var c1 = Ui.Colour("shell.paper_faint");
        var c2 = Ui.Colour("shell.state.warning");
        var c3 = Ui.Colour("shell.state.negative");
        if (t < 0.45f) return c0.Lerp(c1, t / 0.45f);
        if (t < 0.75f) return c1.Lerp(c2, (t - 0.45f) / 0.30f);
        return c2.Lerp(c3, (t - 0.75f) / 0.25f);
    }
}
