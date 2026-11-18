using System;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private PanelContainer _shellDatePlate;
    private Button _shellDatePauseBtn;
    private ColorRect _shellPauseMarginMark;
    private TextureRect _shellPauseIcon;
    private Label _shellDateLabel;
    private readonly Button[] _shellSpeedPips = new Button[5];
    private Button _shellMenuButton;
    private Button _shellWorldDefconBtn;
    private Label _shellDefconLabel;
    private ColorRect _shellDefconLamp;
    private Control _shellTensionBar;
    private Label _shellTensionLabel;
    private Button _shellChronicleBtn;

    private string _shellLastDateText = "";
    private int _shellLastDefcon = -1;
    private float _shellLastTension = -1f;

    private void BuildShellDatePlate()
    {
        var plate = new PanelContainer
        {
            Name = "ShellDatePlate",
            MouseFilter = MouseFilterEnum.Stop
        };
        plate.AddThemeStyleboxOverride("panel", ShellPlateStyle(cornerPlate: true));
        AttachBrassCornerOverlay(plate, topLeftOnly: false);
        _newShellRoot.AddChild(plate);
        _shellDatePlate = plate;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 7);
        margin.AddThemeConstantOverride("margin_bottom", 7);
        plate.AddChild(margin);

        var vStack = new VBoxContainer();
        vStack.AddThemeConstantOverride("separation", 6);
        margin.AddChild(vStack);

        // Top ruled line: pause margin mark + date + 5 speed pips + Esc menu
        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 6);
        vStack.AddChild(topRow);

        var pauseBtn = new Button
        {
            Name = "ShellDatePauseButton",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        pauseBtn.AddThemeStyleboxOverride("normal", ShellWellStyle(false));
        pauseBtn.AddThemeStyleboxOverride("hover", ShellWellStyle(true));
        pauseBtn.AddThemeStyleboxOverride("pressed", ShellWellStyle(true));
        pauseBtn.Pressed += () => { TogglePause(); RefreshShellDatePlate(); };
        Tip(pauseBtn, "Pause or resume campaign time (Space).");
        topRow.AddChild(pauseBtn);
        _shellDatePauseBtn = pauseBtn;

        var pRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        pRow.AddThemeConstantOverride("separation", 7);
        pRow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        pauseBtn.AddChild(pRow);

        var marginMark = new ColorRect
        {
            CustomMinimumSize = new Vector2(4, 0),
            Color = Ui.Colour("shell.pause_amber"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        pRow.AddChild(marginMark);
        _shellPauseMarginMark = marginMark;

        var pIcon = new TextureRect
        {
            Texture = ShellIconTexture("icon_shell_pause"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(14, 14),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.paper"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        pRow.AddChild(pIcon);
        _shellPauseIcon = pIcon;

        var dateLbl = new Label
        {
            Text = "1 Jan 2024",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        dateLbl.AddThemeFontOverride("font", ShellFont("shell.serif_semibold", tabular: true));
        dateLbl.AddThemeFontSizeOverride("font_size", 16);
        dateLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        pRow.AddChild(dateLbl);
        _shellDateLabel = dateLbl;

        var pipsRow = new HBoxContainer();
        pipsRow.AddThemeConstantOverride("separation", 3);
        pipsRow.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        topRow.AddChild(pipsRow);

        for (var i = 0; i < 5; i++)
        {
            var speedValue = i + 1;
            var pip = new Button
            {
                Name = $"ShellSpeedPip_{speedValue}",
                Text = "",
                CustomMinimumSize = new Vector2(14, 26),
                MouseDefaultCursorShape = CursorShape.PointingHand
            };
            pip.Pressed += () => { SetSpeed(speedValue, resume: false); RefreshShellDatePlate(); };
            Tip(pip, Simulation.Loc.Format("Simulation speed {0} (keys 1-5).", speedValue));
            pipsRow.AddChild(pip);
            _shellSpeedPips[i] = pip;
        }

        var menuBtn = new Button
        {
            Name = "ShellMenuButton",
            CustomMinimumSize = new Vector2(32, 32),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        menuBtn.AddThemeStyleboxOverride("normal", ShellWellStyle(false));
        menuBtn.AddThemeStyleboxOverride("hover", ShellPlateStyle(hover: true));
        menuBtn.Pressed += ShowPauseMenu;
        Tip(menuBtn, "Game menu (Esc)");
        var menuIcon = new TextureRect
        {
            Texture = ShellIconTexture("icon_shell_menu"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = Ui.Colour("shell.paper"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        menuIcon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        menuIcon.OffsetLeft = 7; menuIcon.OffsetRight = -7; menuIcon.OffsetTop = 7; menuIcon.OffsetBottom = -7;
        menuBtn.AddChild(menuIcon);
        topRow.AddChild(menuBtn);
        _shellMenuButton = menuBtn;

        // Horizontal journal hairline rule
        var rule = new ColorRect
        {
            CustomMinimumSize = new Vector2(0, 1),
            Color = Ui.Colour("shell.journal_rule"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        vStack.AddChild(rule);

        // World / DEFCON + Tension threshold strip
        var worldRow = new HBoxContainer();
        worldRow.AddThemeConstantOverride("separation", 6);
        vStack.AddChild(worldRow);

        var worldBtn = new Button
        {
            Name = "ShellWorldDefconButton",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 30),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        worldBtn.AddThemeStyleboxOverride("normal", ShellWellStyle(false));
        worldBtn.AddThemeStyleboxOverride("hover", ShellWellStyle(true));
        worldBtn.Pressed += OpenDefconOverview;
        Tip(worldBtn, "World nuclear alert (DEFCON) and great-power tension. Threshold notch at 75%.");
        worldRow.AddChild(worldBtn);
        _shellWorldDefconBtn = worldBtn;

        var wInner = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        wInner.AddThemeConstantOverride("separation", 8);
        wInner.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        worldBtn.AddChild(wInner);

        wInner.AddChild(new Control { CustomMinimumSize = new Vector2(6, 0), MouseFilter = MouseFilterEnum.Ignore });

        var lamp = new ColorRect
        {
            CustomMinimumSize = new Vector2(8, 8),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Color = Ui.Colour("shell.state.positive"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        wInner.AddChild(lamp);
        _shellDefconLamp = lamp;

        var defconLbl = new Label
        {
            Text = "DEFCON 5",
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = MouseFilterEnum.Ignore
        };
        defconLbl.AddThemeFontOverride("font", ShellFont("shell.sans", tabular: true));
        defconLbl.AddThemeFontSizeOverride("font_size", 12);
        defconLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        wInner.AddChild(defconLbl);
        _shellDefconLabel = defconLbl;

        var barHost = new Control
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(80, 14),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        barHost.Draw += DrawShellTensionMeter;
        wInner.AddChild(barHost);
        _shellTensionBar = barHost;

        var tLbl = new Label
        {
            Text = "0%",
            CustomMinimumSize = new Vector2(34, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = MouseFilterEnum.Ignore
        };
        tLbl.AddThemeFontOverride("font", ShellFont("shell.sans", tabular: true));
        tLbl.AddThemeFontSizeOverride("font_size", 12);
        tLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        wInner.AddChild(tLbl);
        _shellTensionLabel = tLbl;

        wInner.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });

        // Recent news log button
        var newsBtn = new Button
        {
            Name = "ShellRecentNewsButton",
            CustomMinimumSize = new Vector2(30, 30),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        newsBtn.AddThemeStyleboxOverride("normal", ShellWellStyle(false));
        newsBtn.AddThemeStyleboxOverride("hover", ShellPlateStyle(hover: true));
        newsBtn.Pressed += ToggleShellRecentNewsModal;
        Tip(newsBtn, "Recent world news & message presets");
        var newsIcon = new TextureRect
        {
            Texture = ShellIconTexture("icon_shell_scroll"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = Ui.Colour("shell.paper"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        newsIcon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        newsIcon.OffsetLeft = 6; newsIcon.OffsetRight = -6; newsIcon.OffsetTop = 6; newsIcon.OffsetBottom = -6;
        newsBtn.AddChild(newsIcon);
        worldRow.AddChild(newsBtn);
        _shellChronicleBtn = newsBtn;
    }

    private void DrawShellTensionMeter()
    {
        if (_shellTensionBar is null) return;
        var w = _shellTensionBar.Size.X;
        var h = _shellTensionBar.Size.Y;
        var trackY = (h - 6f) * 0.5f;
        _shellTensionBar.DrawRect(new Rect2(0f, trackY, w, 6f), Ui.Colour("shell.well_bottom"));

        var pct = Mathf.Clamp(_shellLastTension, 0f, 100f) / 100f;
        var fillColour = pct >= 0.75f
            ? Ui.Colour("shell.state.negative")
            : (pct >= 0.45f ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.info"));
        if (pct > 0.001f)
            _shellTensionBar.DrawRect(new Rect2(0f, trackY, w * pct, 6f), fillColour);

        // 75% crisis threshold notch
        var notchX = MathF.Round(w * 0.75f);
        _shellTensionBar.DrawRect(new Rect2(notchX, trackY - 2f, 2f, 10f), Ui.Colour("shell.brass_hi"));
    }

    private void LayoutShellDatePlate(ShellLayoutProfile profile)
    {
        if (_shellDatePlate is null) return;
        PlaceShellRect(_shellDatePlate, profile.DatePlate);
        _shellDatePauseBtn.CustomMinimumSize = new Vector2(0, profile.Compact ? 34 : 38);
        _shellDateLabel.AddThemeFontSizeOverride("font_size", profile.Compact ? 14 : 16);
    }

    private void RefreshShellDatePlate()
    {
        if (_shellDatePlate is null || _campaign is null) return;

        var dateStr = _campaign.Date.ToString();
        if (!string.Equals(_shellLastDateText, dateStr, StringComparison.Ordinal))
        {
            _shellLastDateText = dateStr;
            _shellDateLabel.Text = dateStr;
        }

        var paused = _campaign.IsPaused;
        _shellPauseMarginMark.Color = paused ? Ui.Colour("shell.pause_amber") : Ui.Colour("shell.state.positive");
        _shellPauseIcon.Texture = ShellIconTexture(paused ? "icon_shell_play" : "icon_shell_pause");
        _shellPauseIcon.Modulate = paused ? Ui.Colour("shell.pause_amber") : Ui.Colour("shell.paper");

        for (var i = 0; i < 5; i++)
        {
            var lit = (i + 1) <= _speed;
            var style = new StyleBoxFlat
            {
                BgColor = lit
                    ? (paused ? Ui.Colour("shell.brass") : Ui.Colour("shell.paper"))
                    : Ui.Colour("shell.well_top"),
                BorderColor = lit ? Ui.Colour("shell.brass_hi") : Ui.Colour("shell.brass_shadow"),
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1
            };
            style.SetCornerRadiusAll(2);
            _shellSpeedPips[i].AddThemeStyleboxOverride("normal", style);
            _shellSpeedPips[i].AddThemeStyleboxOverride("hover", style);
            _shellSpeedPips[i].AddThemeStyleboxOverride("pressed", style);
        }

        var defcon = DefconSystem.Enabled(_campaign) ? DefconSystem.CurrentLevel(_campaign) : 5;
        if (_shellLastDefcon != defcon)
        {
            _shellLastDefcon = defcon;
            _shellDefconLabel.Text = $"DEFCON {defcon}";
            _shellDefconLamp.Color = Ui.Colour(DefconSystem.LevelColourToken(defcon));
        }

        var tension = MathF.Round(Math.Clamp(WorldTensionToday(), 0f, 100f));
        if (MathF.Abs(_shellLastTension - tension) >= 0.5f)
        {
            _shellLastTension = tension;
            _shellTensionLabel.Text = $"{tension:0}%";
            _shellTensionBar.QueueRedraw();
        }
    }
}
