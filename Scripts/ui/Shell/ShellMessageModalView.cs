using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Standalone World / Recent Dispatches & Message Preset popover opened from the World/DEFCON well
/// on the Date Plate, with tabs for Recent News, DEFCON, and Great Powers.
/// </summary>
internal sealed class ShellMessageModalView
{
    private readonly ShellContext _ctx;
    private readonly List<NewsFeed.Line> _recentNews = new();
    private readonly HashSet<long> _pinnedSeqs = new();
    private PanelContainer _panel;
    private VBoxContainer _newsRows;
    private int _activeTab;
    private readonly Button[] _tabButtons = new Button[3];
    private readonly Dictionary<ShellMessagePreset, Button> _presetButtons = new();

    public bool IsVisible => _panel is { Visible: true };

    public ShellMessageModalView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public ShellMessagePreset CurrentPreset =>
        ShellMessages.ParsePreset(SettingsCatalog.Chosen(ShellMessages.PresetSetting));

    public ShellMessageDelivery Resolve(ShellMessageEvent evt, bool involvesPlayer) =>
        ShellMessages.Resolve(CurrentPreset, evt, involvesPlayer, _ctx.GetSpeed?.Invoke() ?? 1);

    public static ShellMessageEvent ClassifyNewsKind(string kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "war" or "war_declared" or "invasion" => ShellMessageEvent.WarDeclared,
        "ultimatum" or "demand" => ShellMessageEvent.UltimatumReceived,
        "defcon" or "nuclear_alert" => ShellMessageEvent.DefconChanged,
        "nuclear" or "nuclear_strike" => ShellMessageEvent.NuclearStrike,
        "peace" or "peace_treaty" or "truce" => ShellMessageEvent.PeaceOffer,
        "encirclement" => ShellMessageEvent.ArmyEncircled,
        "battle_lost" or "defeat" => ShellMessageEvent.BattleLost,
        "election" => ShellMessageEvent.ElectionSoon,
        "research" or "technology" => ShellMessageEvent.ResearchFinished,
        "shortage" => ShellMessageEvent.CommodityShortage,
        "construction" => ShellMessageEvent.ConstructionFinished,
        _ => ShellMessageEvent.WorldNews
    };

    public void RecordLine(NewsFeed.Line line)
    {
        _recentNews.Insert(0, line);
        if (_recentNews.Count > 24)
            _recentNews.RemoveRange(24, _recentNews.Count - 24);
        if (IsVisible) Refresh();
    }

    public void Toggle()
    {
        EnsureBuilt();
        _panel.Visible = !_panel.Visible;
        if (_panel.Visible)
        {
            _panel.MoveToFront();
            Refresh();
        }
    }

    public void Close()
    {
        if (_panel is not null && GodotObject.IsInstanceValid(_panel))
            _panel.Visible = false;
    }

    public void Layout(ShellLayoutProfile profile)
    {
        if (_panel is null) return;
        var w = profile.Compact ? 322f : 440f;
        var h = profile.Compact ? 276f : 368f;
        var x = Math.Max(12f, profile.DatePlate.Right - w);
        var y = profile.DatePlate.Bottom + 4f;
        ShellContext.PlaceRect(_panel, new ShellRect(x, y, w, h));
    }

    private void EnsureBuilt()
    {
        if (_panel is not null && GodotObject.IsInstanceValid(_panel)) return;

        _panel = new PanelContainer
        {
            Name = "ShellRecentNewsModal",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 35
        };
        _panel.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_brass_9s", 14, 14, 12, 14, 12, "shell.plate_bottom", "shell.brass_hi", 3));
        _ctx.Root.AddChild(_panel);

        var vBox = new VBoxContainer();
        vBox.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(vBox);

        var tabsRow = new HBoxContainer();
        tabsRow.AddThemeConstantOverride("separation", 4);
        vBox.AddChild(tabsRow);

        string[] tabKeys = { "Recent world news", "DEFCON", "shell.great_powers" };
        for (var i = 0; i < 3; i++)
        {
            var idx = i;
            var b = new Button
            {
                Text = Loc.Get(tabKeys[i]),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 30),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None
            };
            b.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            b.AddThemeFontSizeOverride("font_size", 13);
            b.Pressed += () =>
            {
                _ctx.PlayCue?.Invoke("ui_select");
                if (idx == 1) { Close(); _ctx.OpenDefconOverview?.Invoke(); return; }
                if (idx == 2) { Close(); _ctx.OpenCountryRankings?.Invoke(_ctx.World?.Player?.Id ?? -1); return; }
                _activeTab = idx;
                Refresh();
            };
            tabsRow.AddChild(b);
            _tabButtons[i] = b;
        }

        var closeBtn = new Button
        {
            Text = "×",
            CustomMinimumSize = new Vector2(30, 30),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        closeBtn.AddThemeStyleboxOverride("normal", _ctx.NineSlice("button_umber_9s", 10, 6, 2, 6, 2));
        closeBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        closeBtn.Pressed += Close;
        tabsRow.AddChild(closeBtn);

        var presetRow = new HBoxContainer();
        presetRow.AddThemeConstantOverride("separation", 4);
        vBox.AddChild(presetRow);

        var pLbl = new Label { Text = Loc.Get("Message preset:"), VerticalAlignment = VerticalAlignment.Center };
        pLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        pLbl.AddThemeFontSizeOverride("font_size", 11);
        pLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        presetRow.AddChild(pLbl);

        AddPresetChip(presetRow, ShellMessagePreset.Calm, "calm", "Calm");
        AddPresetChip(presetRow, ShellMessagePreset.Focus, "focus", "Focus (auto-pause)");
        AddPresetChip(presetRow, ShellMessagePreset.Dispatch, "dispatch", "Dispatch (map feed)");

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        vBox.AddChild(scroll);

        _newsRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _newsRows.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_newsRows);

        var world = _ctx.World;
        var atWar = world?.Player is { } pl && Warfare.OpenWars(world).Exists(w => !w.Ended && w.Involves(pl.Id));
        Layout(ShellLayout.ForViewport(_ctx.GetViewportSize().X, _ctx.GetViewportSize().Y, atWar));
    }

    private void AddPresetChip(HBoxContainer row, ShellMessagePreset preset, string val, string key)
    {
        var b = new Button
        {
            Text = Loc.Get(key),
            CustomMinimumSize = new Vector2(0, 24),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        b.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        b.AddThemeFontSizeOverride("font_size", 11);
        b.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            SettingsCatalog.Store(ShellMessages.PresetSetting, val);
            Refresh();
        };
        row.AddChild(b);
        _presetButtons[preset] = b;
    }

    public void Refresh()
    {
        if (_panel is null || !_panel.Visible) return;
        var curPreset = CurrentPreset;
        var normalChip = _ctx.NineSlice("button_umber_9s", 8, 6, 2, 6, 2);
        var activeChip = _ctx.PressedWellStyle(underlineBottom: true, padX: 6, padY: 2);

        foreach (var (p, btn) in _presetButtons)
        {
            var on = p == curPreset;
            btn.AddThemeStyleboxOverride("normal", on ? activeChip : normalChip);
            btn.AddThemeStyleboxOverride("hover", on ? activeChip : normalChip);
            btn.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_muted"));
        }

        for (var i = 0; i < _tabButtons.Length; i++)
        {
            var on = i == _activeTab;
            var tb = _tabButtons[i];
            tb.AddThemeStyleboxOverride("normal", on
                ? _ctx.NineSlice("tab_folder_9s", 10, 8, 4, 8, 4)
                : _ctx.PressedWellStyle(padX: 8, padY: 4));
            tb.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_faint"));
        }

        foreach (var ch in _newsRows.GetChildren()) ch.QueueFree();
        if (_recentNews.Count == 0)
        {
            var empty = new Label { Text = Loc.Get("No dispatches recorded yet.") };
            empty.AddThemeFontOverride("font", _ctx.Font("shell.serif"));
            empty.AddThemeFontSizeOverride("font_size", 13);
            empty.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
            _newsRows.AddChild(empty);
            return;
        }

        var startYear = _ctx.World?.StartYear ?? 2024;
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < Math.Min(20, _recentNews.Count); i++)
            {
                var line = _recentNews[i];
                var pinned = _pinnedSeqs.Contains(line.Sequence);
                if ((pass == 0 && !pinned) || (pass == 1 && pinned)) continue;
                AddDispatchRow(line, pinned, startYear);
            }
    }

    private void AddDispatchRow(NewsFeed.Line line, bool pinned, int startYear)
    {
        var rowBtn = new Button
        {
            CustomMinimumSize = new Vector2(0, 30),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        var rowStyle = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0f),
            BorderColor = Ui.Colour("shell.paper", 0.08f),
            BorderWidthBottom = 1
        };
        var hoverStyle = new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.07f) };
        rowBtn.AddThemeStyleboxOverride("normal", rowStyle);
        rowBtn.AddThemeStyleboxOverride("hover", hoverStyle);
        var seq = line.Sequence;
        rowBtn.Pressed += () => { if (!_pinnedSeqs.Remove(seq)) _pinnedSeqs.Add(seq); Refresh(); };

        var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", 8);
        h.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        h.OffsetLeft = 4; h.OffsetRight = -4;
        rowBtn.AddChild(h);

        var dt = new Label
        {
            Text = CalendarDate.Format(startYear, line.Day),
            CustomMinimumSize = new Vector2(68, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true
        };
        dt.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        dt.AddThemeFontSizeOverride("font_size", 11);
        dt.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        h.AddChild(dt);

        var txt = new Label
        {
            Text = line.Text,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        txt.AddThemeFontOverride("font", _ctx.Font("shell.serif"));
        txt.AddThemeFontSizeOverride("font_size", 13);
        txt.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        h.AddChild(txt);

        if (pinned)
        {
            var pin = new TextureRect
            {
                Texture = _ctx.Icon("icon_shell_pin"),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(13, 13),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                Modulate = Ui.Colour("shell.paper")
            };
            h.AddChild(pin);
        }
        _newsRows.AddChild(rowBtn);
    }
}
