using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Unified dark-umber Alert & "Слежу" (Watching) ledger sheet + torn-edge paper.jpg Newspaper
/// Clipping Card (Revision 3). Draws alerts as one cohesive ruled document under the Date Plate
/// rather than floating separate boxes.
/// </summary>
internal sealed class ShellAlertColumnView
{
    private readonly ShellContext _ctx;
    private readonly ShellMessageModalView _messages;

    private PanelContainer _sheet;
    private VBoxContainer _sheetStack;
    private VBoxContainer _alertRows;
    private Control _outlinerTopRule;
    private Button _outlinerToggleBtn;
    private Label _outlinerCountLabel;
    private Label _outlinerArrowLabel;
    private PanelContainer _outlinerKbd;
    private ScrollContainer _outlinerScroll;
    private VBoxContainer _outlinerRows;
    private bool _outlinerOpen;

    private readonly HashSet<string> _viewedAlerts = new(StringComparer.Ordinal);
    private readonly CampaignAlertSnoozes _snoozes = new();
    private readonly HashSet<Commodity> _watchedGoods = new();

    // Torn-edge Newspaper Clipping Card (paper.jpg + newspaper_card_9s)
    private PanelContainer _newsCard;
    private Label _newsKickerLabel;
    private TextureRect _newsPinIcon;
    private Label _newsPinText;
    private Control _newsTimerRule;
    private Label _newsHeadlineLabel;
    private Label _newsLeadLabel;
    private NewsFeed.Line _activeNews;
    private double _newsAge;
    private bool _newsPinned;
    private long _newsSeenSeq;

    private ShellLayoutProfile _profile;
    private string _lastSnapSig = "";

    public bool IsNewsCardVisible => _newsCard is { Visible: true };
    public bool IsOutlinerOpen => _outlinerOpen;

    public ShellAlertColumnView(ShellContext ctx, ShellMessageModalView messages)
    {
        _ctx = ctx;
        _messages = messages;
    }

    public void Build()
    {
        // ONE unified dark-umber sheet holding alerts + hand-ruled ink lines + Watching outliner
        _sheet = new PanelContainer
        {
            Name = "ShellAlertColumn",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _sheet.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 14, 0, 6, 0, 6, "shell.plate_bottom", "shell.border", 3));
        _ctx.Root.AddChild(_sheet);

        _sheetStack = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _sheetStack.AddThemeConstantOverride("separation", 0);
        _sheet.AddChild(_sheetStack);

        _alertRows = new VBoxContainer { Name = "ShellAlertRows", MouseFilter = Control.MouseFilterEnum.Pass };
        _alertRows.AddThemeConstantOverride("separation", 0);
        _sheetStack.AddChild(_alertRows);

        _outlinerTopRule = ShellContext.CreateRuledLine(23, "shell.ruled_ink", 1.4f, 4f);
        _sheetStack.AddChild(_outlinerTopRule);

        BuildOutlinerSection();
        BuildNewspaperCard();
    }

    private void BuildOutlinerSection()
    {
        var empty = new StyleBoxEmpty();
        var hover = new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.06f) };

        _outlinerToggleBtn = new Button
        {
            Name = "ShellOutlinerToggle",
            CustomMinimumSize = new Vector2(0, 30),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        _outlinerToggleBtn.AddThemeStyleboxOverride("normal", empty);
        _outlinerToggleBtn.AddThemeStyleboxOverride("hover", hover);
        _outlinerToggleBtn.AddThemeStyleboxOverride("pressed", hover);
        _outlinerToggleBtn.Pressed += ToggleOutliner;
        _outlinerToggleBtn.TooltipText = Ui.Tip(Loc.Get("Toggle Watching outliner (J)"));
        _sheetStack.AddChild(_outlinerToggleBtn);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 7);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 12;
        row.OffsetRight = -12;
        _outlinerToggleBtn.AddChild(row);

        var eyeIcon = new TextureRect
        {
            Texture = _ctx.Icon("icon_shell_eye"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(15, 15),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.paper_muted"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddChild(eyeIcon);

        _outlinerCountLabel = new Label
        {
            Text = Loc.Get("Watching"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _outlinerCountLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold", tabular: true));
        _outlinerCountLabel.AddThemeFontSizeOverride("font_size", 13);
        _outlinerCountLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        row.AddChild(_outlinerCountLabel);

        _outlinerKbd = _ctx.CreateKeycapBadge("J");
        row.AddChild(_outlinerKbd);

        _outlinerArrowLabel = new Label
        {
            Text = "▸",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _outlinerArrowLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _outlinerArrowLabel.AddThemeFontSizeOverride("font_size", 12);
        _outlinerArrowLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        row.AddChild(_outlinerArrowLabel);

        _outlinerScroll = new ScrollContainer
        {
            Name = "ShellOutlinerScroll",
            Visible = false,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            CustomMinimumSize = new Vector2(0, 0)
        };
        _sheetStack.AddChild(_outlinerScroll);

        var pad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_left", 12);
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddThemeConstantOverride("margin_top", 2);
        pad.AddThemeConstantOverride("margin_bottom", 6);
        _outlinerScroll.AddChild(pad);

        _outlinerRows = new VBoxContainer
        {
            Name = "ShellOutlinerRows",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _outlinerRows.AddThemeConstantOverride("separation", 3);
        pad.AddChild(_outlinerRows);
    }

    private void BuildNewspaperCard()
    {
        _newsCard = new PanelContainer
        {
            Name = "ShellNewspaperCard",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            ZIndex = 20
        };
        _newsCard.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("newspaper_card_9s", 16, 14, 10, 16, 12, "shell.news_paper", "shell.paper_tag_edge", 2));
        _newsCard.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                _ctx.PlayCue?.Invoke("ui_select");
                _newsPinned = !_newsPinned;
                UpdateNewsPinVisual();
                _newsCard.AcceptEvent();
            }
            else if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            {
                DismissNewsCard();
                _newsCard.AcceptEvent();
            }
        };
        _ctx.Root.AddChild(_newsCard);

        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 3);
        _newsCard.AddChild(col);

        var kickRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        kickRow.AddThemeConstantOverride("separation", 6);
        col.AddChild(kickRow);

        _newsKickerLabel = new Label
        {
            Text = Loc.Get("Dispatch"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _newsKickerLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _newsKickerLabel.AddThemeFontSizeOverride("font_size", 11);
        _newsKickerLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.ink_faint"));
        kickRow.AddChild(_newsKickerLabel);

        _newsPinIcon = new TextureRect
        {
            Texture = _ctx.Icon("icon_shell_pin"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(13, 13),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.ink"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        kickRow.AddChild(_newsPinIcon);

        _newsPinText = new Label
        {
            Text = "",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _newsPinText.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _newsPinText.AddThemeFontSizeOverride("font_size", 11);
        _newsPinText.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
        kickRow.AddChild(_newsPinText);

        // Shrinking ink rule directly under the kicker (matching style.css .clip .headline .timer)
        _newsTimerRule = new Control
        {
            CustomMinimumSize = new Vector2(0, 2),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _newsTimerRule.Draw += () =>
        {
            var w = _newsTimerRule.Size.X;
            _newsTimerRule.DrawLine(new Vector2(0, 0), new Vector2(w, 0), Ui.Colour("shell.plate_top", 0.30f), 1f);
            if (!_newsPinned)
            {
                var maxLife = InterfaceSkin.Metric("shell.news_lifetime_sec", 9f);
                var frac = Math.Clamp(1f - (float)(_newsAge / Math.Max(1f, maxLife)), 0f, 1f);
                _newsTimerRule.DrawRect(new Rect2(0, 0, w * frac, 2f), Ui.Colour("shell.ink_faint"));
            }
        };
        col.AddChild(_newsTimerRule);

        _newsHeadlineLabel = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MaxLinesVisible = 2,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _newsHeadlineLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif_bold"));
        _newsHeadlineLabel.AddThemeFontSizeOverride("font_size", 20);
        _newsHeadlineLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
        col.AddChild(_newsHeadlineLabel);

        _newsLeadLabel = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MaxLinesVisible = 2,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _newsLeadLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif"));
        _newsLeadLabel.AddThemeFontSizeOverride("font_size", 12);
        _newsLeadLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.ink_muted"));
        col.AddChild(_newsLeadLabel);
    }

    public void ToggleOutliner()
    {
        _outlinerOpen = !_outlinerOpen;
        _lastSnapSig = "";
        _ctx.PlayCue?.Invoke("ui_select");
        Refresh();
    }

    public void DismissNewsCard()
    {
        if (_newsCard is not null) _newsCard.Visible = false;
        _newsPinned = false;
    }

    public void Layout(ShellLayoutProfile profile)
    {
        _profile = profile;
        var compact = profile.Compact;
        _sheet.Position = new Vector2(profile.AlertColumn.X, profile.AlertColumn.Y);
        _sheet.CustomMinimumSize = new Vector2(profile.AlertColumn.W, 0);
        _sheet.Size = new Vector2(profile.AlertColumn.W, _sheet.GetCombinedMinimumSize().Y);

        ShellContext.PlaceRect(_newsCard, profile.NewsCard);
        _newsHeadlineLabel.AddThemeFontSizeOverride("font_size", compact ? 15 : 20);
        _newsLeadLabel.AddThemeFontSizeOverride("font_size", compact ? 11 : 12);
        _lastSnapSig = "";
    }

    public void Refresh()
    {
        var world = _ctx.World;
        if (world?.Player is not { } player) return;

        _outlinerKbd.Visible = _ctx.AltKeycapsVisible;

        var snap = ShellAlerts.BuildSnapshot(
            world,
            player,
            _outlinerOpen,
            SettingsCatalog.Flag("ui_shell_next_hints"),
            _viewedAlerts,
            _watchedGoods,
            key => _snoozes.Hidden(key, world.ElapsedDays));

        var sig = $"{_outlinerOpen}|{Loc.Language}|{_profile.Compact}|" +
                  string.Join(";", snap.Alerts.Select(a => $"{a.TopicKey}:{a.Viewed}:{a.RightDeadline}")) + "|" +
                  string.Join(";", snap.OutlinerGroups.SelectMany(g => g.Rows).Select(r => $"{r.TopicKey}:{r.RightStat}"));
        if (_lastSnapSig == sig) return;
        _lastSnapSig = sig;

        RebuildAlertRows(snap, world);
        RebuildOutlinerRows(snap);

        // Fit unified sheet height tightly to its actual contents — zero empty hanging box!
        _sheet.ResetSize();
        var minH = _sheet.GetCombinedMinimumSize().Y;
        _sheet.OffsetLeft = _profile.AlertColumn.X;
        _sheet.OffsetTop = _profile.AlertColumn.Y;
        _sheet.OffsetRight = _profile.AlertColumn.Right;
        _sheet.OffsetBottom = _profile.AlertColumn.Y + minH;
        _sheet.Size = new Vector2(_profile.AlertColumn.W, minH);
    }

    private void RebuildAlertRows(ShellAlertSnapshot snap, Campaign world)
    {
        foreach (var ch in _alertRows.GetChildren()) ch.QueueFree();

        var compact = _profile.Compact;
        var rowH = compact ? 30f : 38f;
        var emptyStyle = new StyleBoxEmpty();
        var hoverStyle = new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.06f) };

        for (var i = 0; i < snap.Alerts.Count; i++)
        {
            if (i > 0)
            {
                var padRule = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                padRule.AddThemeConstantOverride("margin_left", compact ? 12 : 16);
                padRule.AddThemeConstantOverride("margin_right", compact ? 10 : 12);
                padRule.AddChild(ShellContext.CreateRuledLine(7 + i * 3, "shell.ruled_ink", 1.35f, 4f));
                _alertRows.AddChild(padRule);
            }

            var item = snap.Alerts[i];
            var btn = new Button
            {
                CustomMinimumSize = new Vector2(0, rowH),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None
            };
            btn.AddThemeStyleboxOverride("normal", emptyStyle);
            btn.AddThemeStyleboxOverride("hover", hoverStyle);
            btn.AddThemeStyleboxOverride("pressed", hoverStyle);

            var captured = item;
            btn.Pressed += () =>
            {
                _viewedAlerts.Add(captured.TopicKey);
                _ctx.PlayCue?.Invoke("ui_select");
                RouteTopic(captured.RouteScreen, captured.TargetId);
                Refresh();
            };
            btn.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } && !captured.IsNextHint)
                {
                    _snoozes.Snooze(captured.TopicKey, world.ElapsedDays);
                    Refresh();
                    btn.AcceptEvent();
                }
            };
            btn.TooltipText = Ui.Tip($"{captured.Title}\n{captured.Subtitle}\n{Loc.Get("Right-click: hide temporarily.")}");

            var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            h.AddThemeConstantOverride("separation", compact ? 5 : 7);
            h.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            h.OffsetLeft = compact ? 4 : 6;
            h.OffsetRight = compact ? -6 : -8;
            btn.AddChild(h);

            // Left pencil-tick margin (✓ when seen, empty margin when unseen so icons stay vertically aligned)
            var tickSlot = new Control
            {
                CustomMinimumSize = new Vector2(compact ? 12 : 15, rowH),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            if (item.Viewed)
            {
                var tickTex = new TextureRect
                {
                    Texture = _ctx.Icon("icon_shell_tick"),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    Modulate = Ui.Colour("shell.tick_ink"),
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                tickTex.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                tickTex.OffsetTop = 8; tickTex.OffsetBottom = -8;
                tickSlot.AddChild(tickTex);
            }
            h.AddChild(tickSlot);

            var iconName = ResolveAlertIcon(item);
            var ico = new TextureRect
            {
                Texture = _ctx.Icon(iconName),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(compact ? 17 : 21, compact ? 17 : 21),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                Modulate = SeverityTint(item.Severity, iconTint: true),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            h.AddChild(ico);

            var titleLbl = new Label
            {
                Text = item.Title,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.Off,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            titleLbl.AddThemeFontOverride("font", _ctx.Font("shell.serif"));
            titleLbl.AddThemeFontSizeOverride("font_size", compact ? 12 : 14);
            titleLbl.AddThemeColorOverride("font_color", Ui.Colour(item.Viewed ? "shell.paper_muted" : "shell.paper"));
            h.AddChild(titleLbl);

            if (!string.IsNullOrEmpty(item.RightDeadline))
            {
                var fd = new Label
                {
                    Text = item.RightDeadline,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    AutowrapMode = TextServer.AutowrapMode.Off,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                fd.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
                fd.AddThemeFontSizeOverride("font_size", compact ? 11 : 13);
                fd.AddThemeColorOverride("font_color",
                    Ui.Colour(item.Severity == ShellAlertSeverity.Critical ? "shell.state.negative_text" : "shell.paper"));
                h.AddChild(fd);
            }

            var mark = new Label
            {
                Text = item.Severity switch
                {
                    ShellAlertSeverity.Critical => "●",
                    ShellAlertSeverity.Warning => "▲",
                    _ => "◆"
                },
                CustomMinimumSize = new Vector2(12, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            mark.AddThemeFontSizeOverride("font_size", compact ? 10 : 11);
            mark.AddThemeColorOverride("font_color", SeverityTint(item.Severity, iconTint: false));
            h.AddChild(mark);

            _alertRows.AddChild(btn);
        }
    }

    private void RebuildOutlinerRows(ShellAlertSnapshot snap)
    {
        foreach (var ch in _outlinerRows.GetChildren()) ch.QueueFree();

        var totalRows = snap.OutlinerGroups.Sum(g => g.Rows.Count);
        _outlinerTopRule.Visible = snap.Alerts.Count > 0;
        _outlinerCountLabel.Text = totalRows > 0
            ? $"{Loc.Get("Watching")} · {totalRows}"
            : Loc.Get("Watching");
        _outlinerArrowLabel.Text = _outlinerOpen ? "▾" : "▸";

        var showBody = _outlinerOpen && totalRows > 0;
        _outlinerScroll.Visible = showBody;
        if (!showBody) return;

        var compact = _profile.Compact;
        var emptyStyle = new StyleBoxEmpty();
        var hoverStyle = new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.06f) };
        var rendered = 0;

        foreach (var grp in snap.OutlinerGroups)
        {
            // Group header with horizontal rule (<h4> in style.css)
            var gHead = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            gHead.AddThemeConstantOverride("separation", 6);
            var gLbl = new Label { Text = grp.GroupTitle };
            gLbl.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            gLbl.AddThemeFontSizeOverride("font_size", compact ? 11 : 12);
            gLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
            gHead.AddChild(gLbl);
            var gLine = new ColorRect
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                CustomMinimumSize = new Vector2(12, 1),
                Color = Ui.Colour("shell.engrave"),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            gHead.AddChild(gLine);
            _outlinerRows.AddChild(gHead);

            foreach (var row in grp.Rows)
            {
                var captured = row;
                var rb = new Button
                {
                    CustomMinimumSize = new Vector2(0, compact ? 24 : 28),
                    MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                    FocusMode = Control.FocusModeEnum.None
                };
                rb.AddThemeStyleboxOverride("normal", emptyStyle);
                rb.AddThemeStyleboxOverride("hover", hoverStyle);
                rb.Pressed += () =>
                {
                    _ctx.PlayCue?.Invoke("ui_select");
                    RouteTopic(captured.RouteScreen, captured.TargetId);
                };

                var rh = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                rh.AddThemeConstantOverride("separation", 6);
                rh.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                rh.OffsetLeft = 4; rh.OffsetRight = -4;
                rb.AddChild(rh);

                var rl = new Label
                {
                    Text = row.Label,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    VerticalAlignment = VerticalAlignment.Center,
                    AutowrapMode = TextServer.AutowrapMode.Off,
                    ClipText = true,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
                };
                rl.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
                rl.AddThemeFontSizeOverride("font_size", compact ? 11 : 13);
                rl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
                rh.AddChild(rl);

                var rv = new Label
                {
                    Text = row.RightStat,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    AutowrapMode = TextServer.AutowrapMode.Off
                };
                rv.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
                rv.AddThemeFontSizeOverride("font_size", compact ? 11 : 12);
                rv.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
                rh.AddChild(rv);

                _outlinerRows.AddChild(rb);
                rendered++;
            }
        }

        // Cap visible scroll viewport height so open Outliner never collides with the bottom-right Minimap or MapBar!
        var miniTop = SettingsCatalog.Flag("ui_shell_minimap")
            ? _profile.MapBar.Y - (_profile.Compact ? 110f : 144f)
            : _profile.MapBar.Y - 12f;
        var availBelowAlerts = Math.Max(72f, miniTop - _profile.AlertColumn.Y - (snap.Alerts.Count * (_profile.Compact ? 32f : 40f)) - 44f);
        var maxVisibleH = Math.Min(compact ? 148f : 196f, availBelowAlerts);
        var estH = Math.Min(maxVisibleH, rendered * (compact ? 26f : 31f) + snap.OutlinerGroups.Count * 22f + 10f);
        _outlinerScroll.CustomMinimumSize = new Vector2(0, estH);
    }

    public void TickNews(double delta)
    {
        var world = _ctx.World;
        if (world is null) return;

        if (_ctx.IsAnyModalOpen?.Invoke() ?? false)
        {
            if (_newsCard is { Visible: true }) _newsCard.Visible = false;
            return;
        }

        var lines = NewsFeed.Read(world, 6);
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Sequence <= _newsSeenSeq) continue;
            _newsSeenSeq = line.Sequence;
            _messages.RecordLine(line);

            var involvesPlayer = world.Player is { } p && (line.ActorId == p.Id || line.SecondId == p.Id);
            var evt = ShellMessageModalView.ClassifyNewsKind(line.Kind);
            var delivery = _messages.Resolve(evt, involvesPlayer);

            if (delivery.AutoPause && !(_ctx.IsPaused?.Invoke() ?? true))
                _ctx.TogglePause?.Invoke();

            if (delivery.Channel == ShellMessageChannel.NewspaperCard && !_newsPinned)
                ShowNewsCard(line, delivery.ReasonTag);
        }

        if (_newsCard is { Visible: true } && !_newsPinned && !(_ctx.IsPaused?.Invoke() ?? true))
        {
            _newsAge += delta;
            _newsTimerRule.QueueRedraw();
            var maxLife = InterfaceSkin.Metric("shell.news_lifetime_sec", 9f);
            if (_newsAge >= maxLife)
                _newsCard.Visible = false;
        }
    }

    private void ShowNewsCard(NewsFeed.Line line, string reasonKey)
    {
        _activeNews = line;
        _newsAge = 0;
        _newsPinned = false;
        var world = _ctx.World;
        var dateTxt = world is not null ? CalendarDate.Format(world.StartYear, line.Day) : "";
        var reasonTxt = string.IsNullOrEmpty(reasonKey) ? Loc.Get("Dispatch") : Loc.Get(reasonKey);
        _newsKickerLabel.Text = string.IsNullOrEmpty(dateTxt) ? reasonTxt : $"{reasonTxt} · {dateTxt}";

        // Split sentence into crisp headline + detail lead when possible
        var text = line.Text ?? "";
        var splitAt = text.IndexOf(". ", StringComparison.Ordinal);
        if (splitAt > 12 && splitAt < text.Length - 6)
        {
            _newsHeadlineLabel.Text = text[..splitAt];
            _newsLeadLabel.Text = text[(splitAt + 2)..];
            _newsLeadLabel.Visible = true;
        }
        else
        {
            _newsHeadlineLabel.Text = text;
            _newsLeadLabel.Text = "";
            _newsLeadLabel.Visible = false;
        }

        UpdateNewsPinVisual();
        _newsCard.Visible = true;
        _newsTimerRule.QueueRedraw();
    }

    private void UpdateNewsPinVisual()
    {
        _newsPinText.Visible = _newsPinned;
        _newsPinText.Text = Loc.Get("shell.pinned");
        _newsTimerRule.QueueRedraw();
    }

    private void RouteTopic(string screen, int targetId)
    {
        if (screen == "Space") { _ctx.TogglePause?.Invoke(); return; }
        if (screen == "Wars")
        {
            _ctx.OpenWarLedger?.Invoke();
            if (targetId >= 0) _ctx.ShowWarDetail?.Invoke(targetId);
            return;
        }
        if (screen == "SovereignFinance") { _ctx.OpenSovereignFinance?.Invoke(); return; }
        _ctx.OpenScreen?.Invoke(screen);
    }

    private static string ResolveAlertIcon(ShellAlertItem item)
    {
        if (item.IsNextHint) return "icon_shell_crane";
        return item.RouteScreen switch
        {
            "Wars" => "icon_alert_battle",
            "SovereignFinance" => "icon_shell_warning",
            "Trade" => "icon_shell_nickel",
            "Production" => "icon_industry",
            "Research" => "icon_research",
            "Diplomacy" => "icon_shell_treaty",
            "Economy" => "icon_shell_crane",
            _ => "icon_alert_event"
        };
    }

    private static Color SeverityTint(ShellAlertSeverity sev, bool iconTint) => sev switch
    {
        ShellAlertSeverity.Critical => Ui.Colour("shell.state.negative"),
        ShellAlertSeverity.Warning => Ui.Colour("shell.state.warning"),
        _ => Ui.Colour(iconTint ? "shell.icon" : "shell.state.info")
    };
}
