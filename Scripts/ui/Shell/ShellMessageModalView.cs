using System;
using System.Collections.Generic;
using System.Globalization;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Editorial Sovereign Atlas World Dispatch &amp; Message Preset dossier modal opened from the
/// World/DEFCON well on the Date Plate: framed category header banner, styled preset cards with
/// color-coded consequence pills, Esc/× focus hygiene, bounded scrolling with >= 15px scrollbar
/// clearance, and word-wrapped dispatch cards with category pills and pin toggles.
/// </summary>
internal sealed class ShellMessageModalView
{
    private const int ScrollClearanceRight = 15;

    private readonly ShellContext _ctx;
    private readonly List<NewsFeed.Line> _recentNews = new();
    private readonly HashSet<long> _pinnedSeqs = new();

    private PanelContainer _panel;
    private Label _headerTitle;
    private Label _countBadgeLabel;
    private VBoxContainer _newsRows;
    private int _activeTab;
    private readonly Button[] _tabButtons = new Button[3];
    private readonly Dictionary<ShellMessagePreset, PresetCardNodes> _presetCards = new();

    private StyleBoxFlat _presetNormal;
    private StyleBoxFlat _presetHover;
    private StyleBoxFlat _presetActive;
    private StyleBoxFlat _tabHover;
    private StyleBoxFlat _closeHover;

    private sealed class PresetCardNodes
    {
        public string TitleKey;
        public string PillKey;
        public string TipKey;
        public string AccentToken;
        public Button Button;
        public Label TitleLabel;
        public Label PillLabel;
        public StyleBoxFlat PillStyle;
    }

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
        var compact = profile.Compact;
        var w = compact ? 372f : 464f;
        var y = profile.DatePlate.Bottom + 4f;
        var maxH = Math.Max(240f, profile.ViewportHeight - y - 16f);
        var h = Math.Min(compact ? 354f : 444f, maxH);
        var x = Math.Max(12f, profile.DatePlate.Right - w);
        ShellContext.PlaceRect(_panel, new ShellRect(x, y, w, h));
    }

    private void EnsureBuilt()
    {
        if (_panel is not null && GodotObject.IsInstanceValid(_panel)) return;

        _presetNormal = Ui.ChromeBox("shell.plate_top", 0.72f, "shell.engrave", 0.75f, cornerRadius: 3, borderWidth: 1, padX: 6, padY: 4);
        _presetHover = Ui.ChromeBox("shell.plate_hover", 0.90f, "shell.brass_hi", 0.85f, cornerRadius: 3, borderWidth: 1, padX: 6, padY: 4);
        _presetActive = Ui.ChromeBox("shell.well_top", 0.98f, "shell.brass_hi", 0.95f, cornerRadius: 3, borderWidth: 1, padX: 6, padY: 4, leftBorderWidth: 3);
        _tabHover = Ui.ChromeBox("shell.plate_hover", 0.86f, "shell.brass_hi", 0.80f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 4);
        _closeHover = Ui.ChromeBox("shell.state.negative", 0.28f, "shell.state.negative", 0.85f, cornerRadius: 3, borderWidth: 1, padX: 6, padY: 2);

        _panel = new PanelContainer
        {
            Name = "ShellRecentNewsModal",
            Visible = false,
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 35
        };
        _panel.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_brass_9s", 14, 12, 10, 12, 12, "shell.plate_bottom", "shell.brass_hi", 3));
        _panel.GuiInput += ev =>
        {
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            {
                Close();
                _panel.AcceptEvent();
            }
        };
        _ctx.Root.AddChild(_panel);

        var vBox = new VBoxContainer { ClipContents = true };
        vBox.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(vBox);

        BuildHeaderBanner(vBox);
        BuildTabsRow(vBox);
        BuildPresetCards(vBox);

        vBox.AddChild(ShellContext.CreateRuledLine(13, "shell.brass_dim", 1.2f, 3f));

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            ClipContents = true
        };
        vBox.AddChild(scroll);

        var pad = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        pad.AddThemeConstantOverride("margin_right", ScrollClearanceRight);
        scroll.AddChild(pad);

        _newsRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        _newsRows.AddThemeConstantOverride("separation", 6);
        pad.AddChild(_newsRows);

        var world = _ctx.World;
        var atWar = world?.Player is { } pl && Warfare.OpenWars(world).Exists(w => !w.Ended && w.Involves(pl.Id));
        Layout(_ctx.ResolveViewportProfile(atWar));
    }

    private void BuildHeaderBanner(VBoxContainer parent)
    {
        var banner = new PanelContainer
        {
            Name = "ShellNewsModalHeader",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        banner.AddThemeStyleboxOverride("panel",
            Ui.ChromeBox("shell.well_top", 0.94f, "shell.brass", 0.80f, cornerRadius: 3, borderWidth: 1, padX: 10, padY: 6, leftBorderWidth: 3));
        parent.AddChild(banner);

        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 8);
        banner.AddChild(row);

        var emblem = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = _ctx.Icon("icon_shell_tension"),
            CustomMinimumSize = new Vector2(18, 18),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.brass_hi"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddChild(emblem);

        _headerTitle = new Label
        {
            Text = Loc.Get("shell.ws.modal.title"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _headerTitle.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        _headerTitle.AddThemeFontSizeOverride("font_size", 15);
        _headerTitle.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(_headerTitle);

        var badge = new PanelContainer
        {
            Name = "ShellNewsCountBadge",
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        badge.AddThemeStyleboxOverride("panel",
            Ui.ChromeBox("shell.plate_top", 0.90f, "shell.brass_dim", 0.85f, cornerRadius: 8, borderWidth: 1, padX: 7, padY: 1));
        row.AddChild(badge);

        _countBadgeLabel = new Label { Text = "0", HorizontalAlignment = HorizontalAlignment.Center };
        _countBadgeLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
        _countBadgeLabel.AddThemeFontSizeOverride("font_size", 11);
        _countBadgeLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.brass_hi"));
        badge.AddChild(_countBadgeLabel);

        var closeBtn = new Button
        {
            Name = "CloseShellNewsModal",
            Text = "×",
            CustomMinimumSize = new Vector2(28, 28),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get("shell.ws.modal.close_tip"))
        };
        closeBtn.AddThemeStyleboxOverride("normal", _ctx.NineSlice("button_umber_9s", 8, 6, 2, 6, 2));
        closeBtn.AddThemeStyleboxOverride("hover", _closeHover);
        closeBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        closeBtn.Pressed += Close;
        row.AddChild(closeBtn);
    }

    private void BuildTabsRow(VBoxContainer parent)
    {
        var tabsRow = new HBoxContainer();
        tabsRow.AddThemeConstantOverride("separation", 4);
        parent.AddChild(tabsRow);

        string[] tabKeys = { "Recent world news", "DEFCON", "shell.great_powers" };
        for (var i = 0; i < 3; i++)
        {
            var idx = i;
            var b = new Button
            {
                Text = Loc.Get(tabKeys[i]),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 28),
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
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
    }

    private void BuildPresetCards(VBoxContainer parent)
    {
        var cap = new Label
        {
            Text = Loc.Get("Message preset:"),
            ClipText = true
        };
        cap.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        cap.AddThemeFontSizeOverride("font_size", 11);
        cap.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        parent.AddChild(cap);

        var grid = new HBoxContainer { Name = "ShellMessagePresetCards", ClipContents = true };
        grid.AddThemeConstantOverride("separation", 5);
        parent.AddChild(grid);

        AddPresetCard(grid, ShellMessagePreset.Calm, "calm", "Calm",
            "shell.ws.modal.preset_pill_calm", "shell.ws.modal.preset_tip_calm", "shell.state.positive");
        AddPresetCard(grid, ShellMessagePreset.Focus, "focus", "shell.ws.modal.preset_focus",
            "shell.ws.modal.preset_pill_focus", "shell.ws.modal.preset_tip_focus", "shell.state.warning");
        AddPresetCard(grid, ShellMessagePreset.Dispatch, "dispatch", "shell.ws.modal.preset_dispatch",
            "shell.ws.modal.preset_pill_dispatch", "shell.ws.modal.preset_tip_dispatch", "shell.state.info");
    }

    private void AddPresetCard(HBoxContainer row, ShellMessagePreset preset, string val, string titleKey, string pillKey, string tipKey, string accentTok)
    {
        var btn = new Button
        {
            Name = "ShellPreset_" + val,
            CustomMinimumSize = new Vector2(0, 44),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get(tipKey), 48)
        };
        btn.AddThemeStyleboxOverride("normal", _presetNormal);
        btn.AddThemeStyleboxOverride("hover", _presetHover);
        btn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            SettingsCatalog.Store(ShellMessages.PresetSetting, val);
            Refresh();
        };

        var col = new VBoxContainer { ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 2);
        col.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        col.OffsetLeft = 8; col.OffsetRight = -6; col.OffsetTop = 4; col.OffsetBottom = -4;
        btn.AddChild(col);

        var title = new Label
        {
            Text = Loc.Get(titleKey),
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        title.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        title.AddThemeFontSizeOverride("font_size", 12);
        title.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        col.AddChild(title);

        var pillBox = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        var pillStyle = Ui.ChromeBox(accentTok, 0.20f, accentTok, 0.72f, cornerRadius: 2, borderWidth: 1, padX: 5, padY: 0);
        pillBox.AddThemeStyleboxOverride("panel", pillStyle);
        col.AddChild(pillBox);

        var pillLbl = new Label
        {
            Text = Loc.Get(pillKey),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        pillLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        pillLbl.AddThemeFontSizeOverride("font_size", 10);
        pillLbl.AddThemeColorOverride("font_color", Ui.Colour(accentTok));
        pillBox.AddChild(pillLbl);

        row.AddChild(btn);
        _presetCards[preset] = new PresetCardNodes
        {
            TitleKey = titleKey,
            PillKey = pillKey,
            TipKey = tipKey,
            AccentToken = accentTok,
            Button = btn,
            TitleLabel = title,
            PillLabel = pillLbl,
            PillStyle = pillStyle
        };
    }

    public void Refresh()
    {
        if (_panel is null || !_panel.Visible) return;

        _headerTitle.Text = Loc.Get("shell.ws.modal.title");
        _countBadgeLabel.Text = _recentNews.Count.ToString(CultureInfo.InvariantCulture);

        var curPreset = CurrentPreset;
        foreach (var (p, card) in _presetCards)
        {
            var on = p == curPreset;
            card.TitleLabel.Text = Loc.Get(card.TitleKey);
            card.PillLabel.Text = Loc.Get(card.PillKey);
            card.Button.TooltipText = Ui.Tip(Loc.Get(card.TipKey), 48);
            card.Button.AddThemeStyleboxOverride("normal", on ? _presetActive : _presetNormal);
            card.Button.AddThemeStyleboxOverride("hover", on ? _presetActive : _presetHover);
            card.TitleLabel.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_muted"));
            card.PillStyle.BgColor = Ui.Colour(card.AccentToken, on ? 0.30f : 0.14f);
            card.PillStyle.BorderColor = Ui.Colour(card.AccentToken, on ? 0.92f : 0.55f);
        }

        string[] tabKeys = { "Recent world news", "DEFCON", "shell.great_powers" };
        for (var i = 0; i < _tabButtons.Length; i++)
        {
            var on = i == _activeTab;
            var tb = _tabButtons[i];
            tb.Text = Loc.Get(tabKeys[i]);
            var normalTab = on
                ? _ctx.NineSlice("tab_folder_9s", 10, 8, 4, 8, 4)
                : _ctx.PressedWellStyle(padX: 8, padY: 4);
            tb.AddThemeStyleboxOverride("normal", normalTab);
            tb.AddThemeStyleboxOverride("hover", on ? normalTab : _tabHover);
            tb.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_faint"));
        }

        foreach (var ch in _newsRows.GetChildren()) ch.QueueFree();
        if (_recentNews.Count == 0)
        {
            var emptyCard = new PanelContainer { ClipContents = true };
            emptyCard.AddThemeStyleboxOverride("panel",
                Ui.ChromeBox("shell.well", 0.65f, "shell.engrave", 0.65f, cornerRadius: 3, borderWidth: 1, padX: 12, padY: 14));
            var empty = new Label
            {
                Text = Loc.Get("No dispatches recorded yet."),
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            empty.AddThemeFontOverride("font", _ctx.Font("shell.serif"));
            empty.AddThemeFontSizeOverride("font_size", 13);
            empty.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
            emptyCard.AddChild(empty);
            _newsRows.AddChild(emptyCard);
            return;
        }

        var world = _ctx.World;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < Math.Min(20, _recentNews.Count); i++)
            {
                var line = _recentNews[i];
                var pinned = _pinnedSeqs.Contains(line.Sequence);
                if ((pass == 0 && !pinned) || (pass == 1 && pinned)) continue;
                AddDispatchRow(world, line, pinned);
            }
        }
    }

    private void AddDispatchRow(Campaign world, NewsFeed.Line line, bool pinned)
    {
        var (catKey, accentTok) = ResolveCategoryVisual(ClassifyNewsKind(line.Kind));
        var card = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        card.AddThemeStyleboxOverride("panel",
            Ui.ChromeBox(pinned ? "shell.plate_top" : "shell.well", pinned ? 0.84f : 0.72f,
                pinned ? "shell.brass_hi" : accentTok, pinned ? 0.85f : 0.68f,
                cornerRadius: 3, borderWidth: 1, padX: 9, padY: 6, leftBorderWidth: 3));

        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        col.AddThemeConstantOverride("separation", 4);
        card.AddChild(col);

        var metaRow = new HBoxContainer { ClipContents = true };
        metaRow.AddThemeConstantOverride("separation", 6);
        col.AddChild(metaRow);

        var catPill = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        catPill.AddThemeStyleboxOverride("panel",
            Ui.ChromeBox(accentTok, 0.20f, accentTok, 0.75f, cornerRadius: 2, borderWidth: 1, padX: 5, padY: 0));
        var catLbl = new Label { Text = Loc.Get(catKey) };
        catLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        catLbl.AddThemeFontSizeOverride("font_size", 10);
        catLbl.AddThemeColorOverride("font_color", Ui.Colour(accentTok));
        catPill.AddChild(catLbl);
        metaRow.AddChild(catPill);

        var dt = new Label
        {
            Text = world is not null ? LandArrangementSystem.DateOfDay(world, line.Day) : $"d{line.Day}",
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true
        };
        dt.AddThemeFontOverride("font", PresentationFonts.Tabular());
        dt.AddThemeFontSizeOverride("font_size", 11);
        dt.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        metaRow.AddChild(dt);

        var seq = line.Sequence;
        var pinBtn = new Button
        {
            CustomMinimumSize = new Vector2(22, 20),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get(pinned ? "shell.ws.modal.unpin_tip" : "shell.ws.modal.pin_tip"))
        };
        pinBtn.AddThemeStyleboxOverride("normal",
            Ui.ChromeBox(pinned ? "shell.brass_shadow" : "shell.well", pinned ? 0.85f : 0f,
                pinned ? "shell.brass_hi" : "shell.engrave", pinned ? 0.90f : 0.45f, cornerRadius: 2, borderWidth: 1));
        pinBtn.AddThemeStyleboxOverride("hover", _presetHover);
        pinBtn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            if (!_pinnedSeqs.Remove(seq)) _pinnedSeqs.Add(seq);
            Refresh();
        };

        var pinIcon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = _ctx.Icon("icon_shell_pin"),
            Modulate = Ui.Colour(pinned ? "shell.brass_hi" : "shell.paper_ghost"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        pinIcon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        pinIcon.OffsetLeft = 4; pinIcon.OffsetRight = -4; pinIcon.OffsetTop = 3; pinIcon.OffsetBottom = -3;
        pinBtn.AddChild(pinIcon);
        metaRow.AddChild(pinBtn);

        var txt = new Label
        {
            Text = line.Text,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        txt.AddThemeFontOverride("font", _ctx.Font("shell.serif"));
        txt.AddThemeFontSizeOverride("font_size", 13);
        txt.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        col.AddChild(txt);

        _newsRows.AddChild(card);
    }

    private static (string LabelKey, string AccentToken) ResolveCategoryVisual(ShellMessageEvent evt) => evt switch
    {
        ShellMessageEvent.WarDeclared or ShellMessageEvent.NuclearStrike or ShellMessageEvent.ArmyEncircled or ShellMessageEvent.BattleLost
            => ("shell.ws.modal.cat_war", "shell.state.negative"),
        ShellMessageEvent.UltimatumReceived or ShellMessageEvent.DefconChanged or ShellMessageEvent.CommodityShortage
            => ("shell.ws.modal.cat_crisis", "shell.state.warning"),
        ShellMessageEvent.PeaceOffer
            => ("shell.ws.modal.cat_peace", "shell.state.positive"),
        ShellMessageEvent.ElectionSoon or ShellMessageEvent.ResearchFinished or ShellMessageEvent.ConstructionFinished
            => ("shell.ws.modal.cat_state", "shell.state.info"),
        _ => ("shell.ws.modal.cat_world", "shell.brass_hi")
    };
}
