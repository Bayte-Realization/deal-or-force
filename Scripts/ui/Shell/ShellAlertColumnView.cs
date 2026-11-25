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
    private readonly ShellInboxBarView _inboxBar;

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
    private Label _newsQueueLabel;
    private TextureRect _newsPinIcon;
    private Label _newsPinText;
    private Button _newsCloseBtn;
    private Control _newsTimerRule;
    private Label _newsHeadlineLabel;
    private Label _newsLeadLabel;
    private readonly ShellNewsPacer _newsPacer = new();
    private readonly List<NewsFeed.Line> _newsBatchBuffer = new(12);
    private Campaign _newsWorld;
    private double _newsClock;
    private double _newsRuleClock;
    private long _newsSeenSeq;
    private long _newsDisplayedSeq = -1;
    private int _newsDisplayedQueued = -1;
    private bool _newsDisplayedPinned;
    private bool _modalObscured;
    private StyleBoxEmpty _rowEmptyStyle;
    private StyleBoxFlat _rowHoverStyle;

    private ShellLayoutProfile _profile;
    private int _lastSnapHash;
    private int _lastPreSnapHash;
    private bool _hasPreSnapHash;
    private int _localMutationRev;
    private bool _hasSnapHash;

    public bool NeedsNewsRead(long chronicleLatestSeq, Campaign world) =>
        !ReferenceEquals(_newsWorld, world) || chronicleLatestSeq > _newsSeenSeq;

    public void TickNewsCardOnly(double delta)
    {
        _newsClock += double.IsFinite(delta) && delta > 0.0 ? delta : 0.0;
        StepNewsCardVisuals(delta);
    }

    public ShellAlertColumnView(ShellContext ctx, ShellMessageModalView messages)
    {
        _ctx = ctx;
        _messages = messages;
        _inboxBar = new ShellInboxBarView(ctx);
    }

    public void Build()
    {
        // ONE unified dark-umber sheet holding alerts + hand-ruled ink lines + Watching outliner
        _sheet = new PanelContainer
        {
            Name = "ShellAlertColumn",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _sheet.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 14, 0, 6, 0, 6, "shell.plate_bottom", "shell.border", 3));
        _ctx.Root.AddChild(_sheet);
        ChromePlateArt.AttachSurfaceTexture(_sheet, "laid_paper", 0.26f);

        _sheetStack = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _sheetStack.AddThemeConstantOverride("separation", 0);
        _sheet.AddChild(_sheetStack);

        _inboxBar.Build(_sheetStack);

        _alertRows = new VBoxContainer { Name = "ShellAlertRows", MouseFilter = Control.MouseFilterEnum.Pass };
        _alertRows.AddThemeConstantOverride("separation", 0);
        _sheetStack.AddChild(_alertRows);

        _outlinerTopRule = ShellContext.CreateRuledLine(23, "shell.ruled_ink", 1.4f, 4f);
        _sheetStack.AddChild(_outlinerTopRule);

        BuildOutlinerSection();
        BuildNewspaperCard();
        _ctx.AttachPlateFrame(_sheet);
    }

    private void EnsureRowStyles()
    {
        _rowEmptyStyle ??= new StyleBoxEmpty();
        _rowHoverStyle ??= new StyleBoxFlat();
        _rowHoverStyle.BgColor = Ui.Colour("shell.paper", 0.06f);
    }

    private Label MakeLabel(string text, string fontKey, int size, string colorKey, bool tabular = false,
        HorizontalAlignment align = HorizontalAlignment.Left, bool clip = false, bool expand = false)
    {
        var lbl = new Label
        {
            Text = text,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = clip,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        if (expand) lbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (clip) lbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        lbl.AddThemeFontOverride("font", _ctx.Font(fontKey, tabular));
        lbl.AddThemeFontSizeOverride("font_size", size);
        lbl.AddThemeColorOverride("font_color", Ui.Colour(colorKey));
        return lbl;
    }

    private Button MakeRowButton(string name, float height)
    {
        EnsureRowStyles();
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, height),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        if (!string.IsNullOrEmpty(name)) btn.Name = name;
        btn.AddThemeStyleboxOverride("normal", _rowEmptyStyle);
        btn.AddThemeStyleboxOverride("hover", _rowHoverStyle);
        btn.AddThemeStyleboxOverride("pressed", _rowHoverStyle);
        return btn;
    }

    private void BuildOutlinerSection()
    {
        _outlinerToggleBtn = MakeRowButton("ShellOutlinerToggle", 30);
        _outlinerToggleBtn.Pressed += ToggleOutliner;
        _outlinerToggleBtn.TooltipText = Ui.Tip(Loc.Get("Toggle Watching outliner (J)"));
        _sheetStack.AddChild(_outlinerToggleBtn);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 7);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 12; row.OffsetRight = -12;
        _outlinerToggleBtn.AddChild(row);

        var eyeIcon = new TextureRect
        {
            Texture = _ctx.Icon("icon_shell_eye"), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(15, 15),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, Modulate = Ui.Colour("shell.paper_muted"), MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddChild(eyeIcon);

        _outlinerCountLabel = MakeLabel(Loc.Get("Watching"), "shell.serif_semibold", 13, "shell.paper_muted", tabular: true, expand: true);
        row.AddChild(_outlinerCountLabel);
        _outlinerKbd = _ctx.CreateKeycapBadge("J");
        row.AddChild(_outlinerKbd);
        _outlinerArrowLabel = MakeLabel("▸", "shell.sans", 12, "shell.paper_faint");
        row.AddChild(_outlinerArrowLabel);

        _outlinerScroll = new ScrollContainer { Name = "ShellOutlinerScroll", Visible = false, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        _sheetStack.AddChild(_outlinerScroll);

        var pad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_left", 12); pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddThemeConstantOverride("margin_top", 2); pad.AddThemeConstantOverride("margin_bottom", 6);
        _outlinerScroll.AddChild(pad);

        _outlinerRows = new VBoxContainer { Name = "ShellOutlinerRows", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _outlinerRows.AddThemeConstantOverride("separation", 3);
        pad.AddChild(_outlinerRows);
    }

    private void BuildNewspaperCard()
    {
        _newsCard = new PanelContainer
        {
            Name = "ShellNewspaperCard", Visible = false, ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = Ui.Tip(Loc.Get("shell.news_card_tip")), ZIndex = 20
        };
        _newsCard.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("newspaper_card_9s", 16, 14, 10, 16, 12, "shell.news_paper", "shell.paper_tag_edge", 2));
        _newsCard.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                _ctx.PlayCue?.Invoke("ui_select"); _newsPacer.TogglePinned(); UpdateNewsPinVisual(); _newsCard.AcceptEvent();
            }
            else if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            {
                DismissNewsCard(clearQueue: false); _newsCard.AcceptEvent();
            }
        };
        _ctx.Root.AddChild(_newsCard);
        if (_ctx.PaperTexture() is { } paperGrain)
        {
            var grain = new TextureRect
            {
                Texture = paperGrain, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Tile,
                Modulate = Ui.Colour("shell.paper", 0.16f), MouseFilter = Control.MouseFilterEnum.Ignore
            };
            grain.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _newsCard.AddChild(grain);
        }

        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        col.AddThemeConstantOverride("separation", 3);
        _newsCard.AddChild(col);

        var kickRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        kickRow.AddThemeConstantOverride("separation", 6);
        col.AddChild(kickRow);

        _newsKickerLabel = MakeLabel(Loc.Get("Dispatch"), "shell.sans", 11, "shell.ink_faint", clip: true, expand: true);
        kickRow.AddChild(_newsKickerLabel);

        _newsQueueLabel = MakeLabel("", "shell.sans", 11, "shell.ink_faint", tabular: true);
        _newsQueueLabel.Name = "ShellNewsQueueCount";
        _newsQueueLabel.Visible = false;
        kickRow.AddChild(_newsQueueLabel);

        _newsPinIcon = new TextureRect
        {
            Texture = _ctx.Icon("icon_shell_pin"), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(13, 13),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, Modulate = Ui.Colour("shell.ink"), MouseFilter = Control.MouseFilterEnum.Ignore
        };
        kickRow.AddChild(_newsPinIcon);

        _newsPinText = MakeLabel("", "shell.sans", 11, "shell.ink");
        _newsPinText.Visible = false;
        kickRow.AddChild(_newsPinText);

        var touchInit = _ctx.IsTouchMode?.Invoke() ?? (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true);
        var closeBtn = new Button
        {
            Name = "ShellNewsCloseButton", CustomMinimumSize = touchInit ? new Vector2(34, 34) : new Vector2(26, 26), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get("shell.news_close_tip"))
        };
        var closeHover = new StyleBoxFlat { BgColor = Ui.Colour("shell.ink", 0.12f) };
        closeHover.SetCornerRadiusAll(3);
        closeBtn.AddThemeStyleboxOverride("normal", _rowEmptyStyle ??= new StyleBoxEmpty());
        closeBtn.AddThemeStyleboxOverride("hover", closeHover);
        closeBtn.AddThemeStyleboxOverride("pressed", closeHover);
        closeBtn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); DismissNewsCard(clearQueue: Input.IsKeyPressed(Key.Shift)); };
        var closeIco = new TextureRect
        {
            Texture = _ctx.Icon("icon_shell_close"), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Modulate = Ui.Colour("shell.ink"), MouseFilter = Control.MouseFilterEnum.Ignore
        };
        closeIco.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var inset = touchInit ? 7f : 5f;
        closeIco.OffsetLeft = inset; closeIco.OffsetRight = -inset; closeIco.OffsetTop = inset; closeIco.OffsetBottom = -inset;
        closeBtn.AddChild(closeIco);
        kickRow.AddChild(closeBtn);
        _newsCloseBtn = closeBtn;

        _newsTimerRule = new Control { CustomMinimumSize = new Vector2(0, 2), MouseFilter = Control.MouseFilterEnum.Ignore };
        _newsTimerRule.Draw += () =>
        {
            var w = _newsTimerRule.Size.X;
            _newsTimerRule.DrawLine(new Vector2(0, 0), new Vector2(w, 0), Ui.Colour("shell.plate_top", 0.30f), 1f);
            if (!_newsPacer.IsPinned && _newsPacer.Active is { } cur)
            {
                var maxLife = Math.Max(ShellNewsPacer.MinDwellSec, ShellNewsPacer.LifetimeSec);
                var frac = Math.Clamp(1f - (float)(cur.Age / Math.Max(1f, maxLife)), 0f, 1f);
                _newsTimerRule.DrawRect(new Rect2(0, 0, w * frac, 2f), Ui.Colour("shell.ink_faint"));
            }
        };
        col.AddChild(_newsTimerRule);

        _newsHeadlineLabel = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MaxLinesVisible = 3,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _newsHeadlineLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif_bold"));
        _newsHeadlineLabel.AddThemeFontSizeOverride("font_size", 20);
        _newsHeadlineLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
        col.AddChild(_newsHeadlineLabel);

        _newsLeadLabel = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
        _newsLeadLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif"));
        _newsLeadLabel.AddThemeFontSizeOverride("font_size", 12);
        _newsLeadLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.ink_muted"));
        col.AddChild(_newsLeadLabel);
    }

    public void ToggleOutliner()
    {
        _outlinerOpen = !_outlinerOpen;
        _hasSnapHash = false;
        _ctx.PlayCue?.Invoke("shell_drawer_slide");
        Refresh();
    }

    public void DismissNewsCard() => DismissNewsCard(clearQueue: true);
    public void DismissNewsCard(bool clearQueue)
    {
        if (_newsCard is not null) _newsCard.Visible = false;
        _newsPacer.Dismiss(clearQueue);
        _newsDisplayedSeq = -1;
        _newsDisplayedQueued = -1;
    }

    public void SetModalObscured(bool obscured)
    {
        _modalObscured = obscured;
        if (obscured)
        {
            if (_sheet is not null) _sheet.Visible = false;
            if (_newsCard is not null) _newsCard.Visible = false;
            return;
        }
        Refresh();
        if (_newsPacer.Active is { } cur && _newsCard is not null)
            PresentActiveNews(cur, playAudioCue: false);
    }

    public void Layout(ShellLayoutProfile profile)
    {
        var compactChanged = _profile is null || _profile.Compact != profile.Compact || _profile.TouchMode != profile.TouchMode;
        _profile = profile;
        _sheet.Position = new Vector2(profile.AlertColumn.X, profile.AlertColumn.Y);
        _sheet.CustomMinimumSize = new Vector2(profile.AlertColumn.W, 0);
        _inboxBar.SetCompact(profile.Compact);
        FitSheetBounds();
        PlaceNewsCard();
        if (compactChanged) _hasSnapHash = false;
    }

    private void PlaceNewsCard()
    {
        if (_newsCard is null) return;
        var profile = _profile;
        if (profile is null)
        {
            var world = _ctx.World;
            var atWar = world?.Player is { } player && Warfare.OpenWars(world).Any(w => !w.Ended && w.Involves(player.Id));
            profile = _ctx.ResolveViewportProfile(atWar);
            _profile = profile;
        }
        _newsCard.CustomMinimumSize = new Vector2(profile.NewsCard.W, 0f);
        ShellContext.PlaceRect(_newsCard, profile.NewsCard);
        var compact = profile.Compact;
        var touch = profile.TouchMode;
        _newsHeadlineLabel.AddThemeFontSizeOverride("font_size", compact ? 14 : 16);
        _newsLeadLabel.AddThemeFontSizeOverride("font_size", compact ? 11 : 12);
        if (_newsCloseBtn is not null)
        {
            _newsCloseBtn.CustomMinimumSize = touch ? new Vector2(34, 34) : new Vector2(26, 26);
            if (_newsCloseBtn.GetChildCount() > 0 && _newsCloseBtn.GetChild(0) is TextureRect ico)
            {
                var inset = touch ? 7f : 5f;
                ico.OffsetLeft = inset; ico.OffsetRight = -inset; ico.OffsetTop = inset; ico.OffsetBottom = -inset;
            }
        }
        if (_newsQueueLabel is not null) _newsQueueLabel.AddThemeFontSizeOverride("font_size", touch ? 13 : 11);
        if (_newsPinIcon is not null) _newsPinIcon.CustomMinimumSize = touch ? new Vector2(16, 16) : new Vector2(13, 13);
        _newsCard.ResetSize();
        var cardHeight = Math.Clamp(_newsCard.GetCombinedMinimumSize().Y, compact ? 84f : 100f, compact ? 124f : 148f);
        _newsCard.Size = new Vector2(profile.NewsCard.W, cardHeight);
    }

    private void FitSheetBounds()
    {
        if (_sheet is null || _profile is null) return;
        _sheet.CustomMinimumSize = new Vector2(_profile.AlertColumn.W, 0f);
        _sheet.ResetSize();
        var maxH = Math.Max(72f, _profile.AlertColumn.H);
        var targetH = Math.Clamp(_sheet.GetCombinedMinimumSize().Y, 40f, maxH);
        _sheet.OffsetLeft = _profile.AlertColumn.X;
        _sheet.OffsetTop = _profile.AlertColumn.Y;
        _sheet.OffsetRight = _profile.AlertColumn.Right;
        _sheet.OffsetBottom = _profile.AlertColumn.Y + targetH;
        _sheet.Size = new Vector2(_profile.AlertColumn.W, targetH);
    }

    public void Refresh()
    {
        var obscured = _modalObscured || (_ctx.IsAnyModalOpen?.Invoke() ?? false);
        if (_sheet is not null && _sheet.Visible == obscured) _sheet.Visible = !obscured;
        if (obscured) return;

        var world = _ctx.World;
        if (world?.Player is not { } player) return;

        var kbdVis = _ctx.AltKeycapsVisible && _profile?.TouchMode != true;
        if (_outlinerKbd.Visible != kbdVis) _outlinerKbd.Visible = kbdVis;
        var inboxChanged = _inboxBar.Refresh();

        var isObs = _ctx.IsObserverMode?.Invoke() ?? world.ObserverMode;
        var nextHints = SettingsCatalog.Flag("ui_shell_next_hints");
        var preHash = HashCode.Combine(
            HashCode.Combine(player.Id, isObs, _outlinerOpen, nextHints, Loc.Language, _profile?.Compact ?? false, _profile?.TouchMode ?? false, _inboxBar.LastCount),
            HashCode.Combine(world.ElapsedDays, world.MapRevision, world.WarRevision, world.PendingEvents.Count, world.Armies.Count, world.Construction.Count, _viewedAlerts.Count, _localMutationRev));
        if (!inboxChanged && _hasPreSnapHash && _lastPreSnapHash == preHash) return;
        _hasPreSnapHash = true;
        _lastPreSnapHash = preHash;

        var snap = ShellAlerts.BuildSnapshot(
            world, player, _outlinerOpen, nextHints,
            isObs, _viewedAlerts, _snoozes, _watchedGoods, _profile?.TouchMode == true);

        var hc = new HashCode();
        hc.Add(player.Id); hc.Add(isObs); hc.Add(_outlinerOpen); hc.Add(Loc.Language);
        hc.Add(_profile?.Compact ?? false); hc.Add(_profile?.TouchMode ?? false); hc.Add(_inboxBar.LastCount);
        hc.Add(snap.Alerts.Count);
        for (var i = 0; i < snap.Alerts.Count; i++)
        { var a = snap.Alerts[i]; hc.Add(a.TopicKey); hc.Add(a.IsViewed); hc.Add(a.DeadlineLabel); }
        hc.Add(snap.OutlinerGroups.Count);
        for (var gi = 0; gi < snap.OutlinerGroups.Count; gi++)
        {
            var g = snap.OutlinerGroups[gi]; hc.Add(g.Rows.Count);
            for (var ri = 0; ri < g.Rows.Count; ri++)
            { var r = g.Rows[ri]; hc.Add(r.TopicKey); hc.Add(r.Value); }
        }
        var snapHash = hc.ToHashCode();
        if (_hasSnapHash && _lastSnapHash == snapHash)
        {
            if (inboxChanged) FitSheetBounds();
            return;
        }
        _hasSnapHash = true;
        _lastSnapHash = snapHash;

        RebuildAlertRows(snap, world);
        RebuildOutlinerRows(snap);

        // Fit unified sheet height tightly to its actual contents — zero empty hanging box!
        FitSheetBounds();
    }

    private void RebuildAlertRows(ShellAlertSnapshot snap, Campaign world)
    {
        foreach (var ch in _alertRows.GetChildren()) { _alertRows.RemoveChild(ch); ch.QueueFree(); }

        var compact = _profile?.Compact ?? false;
        var rowH = compact ? 30f : 38f;
        EnsureRowStyles();

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
            var btn = MakeRowButton("", rowH);

            var captured = item;
            btn.Pressed += () =>
            {
                _viewedAlerts.Add(captured.TopicKey);
                _localMutationRev++;
                _ctx.PlayCue?.Invoke("ui_select");
                RouteTopic(RouteOfEntry(captured.Route), captured.TargetId);
                Refresh();
            };
            btn.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } && !captured.IsNextHint)
                {
                    _snoozes.Hide(new CampaignAlert(captured.Id, CampaignAlertGroup.Should, captured.Title, captured.Summary, captured.Route, captured.TargetId), world.ElapsedDays, 7);
                    _localMutationRev++;
                    Refresh();
                    btn.AcceptEvent();
                }
            };
            btn.TooltipText = Ui.Tip($"{captured.Title}\n{captured.Summary}\n{Loc.Get("Right-click: hide temporarily.")}");

            var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            h.AddThemeConstantOverride("separation", compact ? 5 : 7);
            h.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            h.OffsetLeft = compact ? 4 : 6;
            h.OffsetRight = compact ? -6 : -8;
            btn.AddChild(h);

            // Crisp 3px severity accent bar along left edge of alert row
            h.AddChild(new ColorRect
            {
                CustomMinimumSize = new Vector2(3f, rowH - 10f), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                Color = SeverityTint(item.Severity, iconTint: false), MouseFilter = Control.MouseFilterEnum.Ignore
            });

            // Left pencil-tick margin (✓ when seen, empty margin when unseen so icons stay vertically aligned)
            var tickSlot = new Control { CustomMinimumSize = new Vector2(compact ? 12 : 15, rowH), MouseFilter = Control.MouseFilterEnum.Ignore };
            if (item.IsViewed)
            {
                var tickTex = new TextureRect
                {
                    Texture = _ctx.Icon("icon_shell_tick"), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Modulate = Ui.Colour("shell.tick_ink"), MouseFilter = Control.MouseFilterEnum.Ignore
                };
                tickTex.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                tickTex.OffsetTop = 8; tickTex.OffsetBottom = -8;
                tickSlot.AddChild(tickTex);
            }
            h.AddChild(tickSlot);

            h.AddChild(new TextureRect
            {
                Texture = _ctx.Icon(ResolveAlertIcon(item)), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(compact ? 17 : 21, compact ? 17 : 21),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, Modulate = SeverityTint(item.Severity, iconTint: true), MouseFilter = Control.MouseFilterEnum.Ignore
            });

            h.AddChild(MakeLabel(item.Title, "shell.serif", compact ? 12 : 14,
                item.IsViewed ? "shell.paper_muted" : "shell.paper", clip: true, expand: true));

            if (!string.IsNullOrEmpty(item.DeadlineLabel))
                h.AddChild(MakeLabel(item.DeadlineLabel, "shell.sans", compact ? 11 : 13,
                    item.Severity == ShellAlertSeverity.Critical ? "shell.state.negative_text" : "shell.paper",
                    tabular: true, align: HorizontalAlignment.Right));

            var mark = new Label
            {
                Text = item.Severity switch { ShellAlertSeverity.Critical => "●", ShellAlertSeverity.Warning => "▲", _ => "◆" },
                CustomMinimumSize = new Vector2(12, 0), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore
            };
            mark.AddThemeFontSizeOverride("font_size", compact ? 10 : 11);
            mark.AddThemeColorOverride("font_color", SeverityTint(item.Severity, iconTint: false));
            h.AddChild(mark);
            _alertRows.AddChild(btn);
        }
    }

    private void RebuildOutlinerRows(ShellAlertSnapshot snap)
    {
        foreach (var ch in _outlinerRows.GetChildren()) { _outlinerRows.RemoveChild(ch); ch.QueueFree(); }

        var totalRows = 0;
        for (var i = 0; i < snap.OutlinerGroups.Count; i++) totalRows += snap.OutlinerGroups[i].Rows.Count;
        _outlinerTopRule.Visible = snap.Alerts.Count > 0;
        _outlinerCountLabel.Text = totalRows > 0
            ? $"{Loc.Get("Watching")} · {totalRows}"
            : Loc.Get("Watching");
        _outlinerArrowLabel.Text = _outlinerOpen ? "▾" : "▸";

        var showBody = _outlinerOpen && totalRows > 0;
        _outlinerScroll.Visible = showBody;
        if (!showBody) { _outlinerScroll.CustomMinimumSize = Vector2.Zero; return; }

        var compact = _profile?.Compact ?? false;
        EnsureRowStyles();
        var rendered = 0;

        foreach (var grp in snap.OutlinerGroups)
        {
            // Group header with horizontal rule (<h4> in style.css)
            var gHead = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            gHead.AddThemeConstantOverride("separation", 6);
            var gLbl = new Label { Text = grp.Title };
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
                var rb = MakeRowButton("", compact ? 24 : 28);
                rb.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); RouteTopic(captured.RouteKind, captured.TargetId); };

                var rh = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                rh.AddThemeConstantOverride("separation", 6);
                rh.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                rh.OffsetLeft = 4; rh.OffsetRight = -4;
                rb.AddChild(rh);

                rh.AddChild(MakeLabel(row.Title, "shell.sans", compact ? 11 : 13, "shell.paper", clip: true, expand: true));
                rh.AddChild(MakeLabel(row.Value, "shell.sans", compact ? 11 : 12, "shell.paper_muted", tabular: true, align: HorizontalAlignment.Right));
                _outlinerRows.AddChild(rb);
                rendered++;
            }
        }

        // Cap visible scroll viewport height so open Outliner never collides with the bottom-right Minimap or MapBar!
        var prof = _profile ?? _ctx.ResolveViewportProfile(atWar: false);
        var miniTop = SettingsCatalog.Flag("ui_shell_minimap") ? prof.MapBar.Y - (prof.Compact ? 110f : 144f) : prof.MapBar.Y - 12f;
        var inboxH = _inboxBar.IsVisible ? ShellInboxBarView.HeightForLayout(compact) : 0f;
        var availBelowAlerts = Math.Max(72f, miniTop - prof.AlertColumn.Y - inboxH - (snap.Alerts.Count * (prof.Compact ? 32f : 40f)) - 44f);
        var maxVisibleH = Math.Min(prof.TouchMode ? 136f : (compact ? 148f : 196f), availBelowAlerts);
        var estH = Math.Min(maxVisibleH, rendered * (compact ? 26f : 31f) + snap.OutlinerGroups.Count * 22f + 10f);
        _outlinerScroll.CustomMinimumSize = new Vector2(0, estH);
    }

    public void TickNews(double delta)
    {
        var world = _ctx.World;
        if (world is null) return;
        _newsClock += double.IsFinite(delta) && delta > 0.0 ? delta : 0.0;

        if (!ReferenceEquals(_newsWorld, world))
        {
            _newsWorld = world;
            _newsPacer.Reset();
            _newsSeenSeq = world.Chronicle.Entries.Count > 0 ? world.Chronicle.Entries[^1].Sequence : 0;
            _newsDisplayedSeq = -1;
            _newsDisplayedQueued = -1;
            if (_newsCard is not null) _newsCard.Visible = false;
        }

        // NewsFeed.Lines yields newest-first; ingest oldest->newest so chronological order stays intact.
        var entries = world.Chronicle.Entries;
        if (entries.Count > 0 && entries[^1].Sequence > _newsSeenSeq)
        {
            _newsBatchBuffer.Clear();
            foreach (var line in NewsFeed.Lines(world, _newsSeenSeq, 12)) _newsBatchBuffer.Add(line);
            for (var i = _newsBatchBuffer.Count - 1; i >= 0; i--)
            {
                var line = _newsBatchBuffer[i];
                if (line.Sequence > _newsSeenSeq) _newsSeenSeq = line.Sequence;
                _messages.RecordLine(line, refresh: i == 0);
                var involvesPlayer = HeadlinePopups.TieOf(world, line) != HeadlineTie.World;
                var delivery = _messages.Resolve(ShellMessageModalView.ClassifyNewsKind(line.Kind), involvesPlayer);
                if (delivery.PauseClock && !(_ctx.IsPaused?.Invoke() ?? true)) _ctx.TogglePause?.Invoke();
                if (delivery.ShowNewspaperCard)
                {
                    var urgent = delivery.PauseClock || delivery.Speed5SafetyPause || ShellNewsPacer.IsUrgentKind(line.Kind);
                    _newsPacer.Offer(line, delivery.PauseClock ? "Urgent dispatch" : "Dispatch",
                        ShellNewsPacer.ClassifyPriority(line, urgent, involvesPlayer, NewsFeed.Importance(line.Kind)), urgent);
                }
            }
        }
        StepNewsCardVisuals(delta);
    }

    private void StepNewsCardVisuals(double delta)
    {
        var obscured = _modalObscured || (_ctx.IsAnyModalOpen?.Invoke() ?? false);
        if (obscured) { if (_newsCard is { Visible: true }) _newsCard.Visible = false; return; }
        var hovered = _profile?.TouchMode != true && _newsCard is { Visible: true } &&
                      _newsCard.GetViewport() is { } vp && _newsCard.GetGlobalRect().HasPoint(vp.GetMousePosition());
        var step = _newsPacer.Tick(delta, hovered, _ctx.IsPaused?.Invoke() ?? true);
        if (step.Current is null) { if (_newsCard is { Visible: true }) _newsCard.Visible = false; _newsDisplayedSeq = -1; return; }
        if (step.Changed || !_newsCard.Visible || _newsDisplayedSeq != step.Current.Sequence)
            PresentActiveNews(step.Current, playAudioCue: step.Changed);
        else
            SyncQueueLabel(step.QueuedCount);
        if (!step.Frozen)
        {
            _newsRuleClock += double.IsFinite(delta) && delta > 0.0 ? delta : 0.0;
            if (_newsRuleClock >= 0.10) { _newsRuleClock = 0.0; _newsTimerRule.QueueRedraw(); }
        }
    }

    private void PresentActiveNews(ShellNewsItem item, bool playAudioCue)
    {
        PlaceNewsCard();
        var world = _ctx.World;
        var line = item.Line;
        _newsDisplayedSeq = item.Sequence;

        string dateTxt = "";
        if (world is not null)
        {
            var start = new HistoricalDate(world.Date.Year, world.Date.Month, world.Date.Day).AddDays(-world.ElapsedDays);
            var date = start.AddDays(Math.Clamp(line.Day, -36500, 365000));
            dateTxt = new GameDate(date.Year, date.Month, date.Day).Display();
        }
        var reasonTxt = string.IsNullOrEmpty(item.ReasonKey) ? Loc.Get("Dispatch") : Loc.Get(item.ReasonKey);
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

        SyncQueueLabel(_newsPacer.QueuedCount);
        UpdateNewsPinVisual(force: true);
        if (playAudioCue && _newsPacer.ShouldPlayNewsCue(_newsClock, item.Urgent))
            _ctx.PlayCue?.Invoke("shell_dispatch_seal");
        _newsCard.Visible = true;
        _newsTimerRule.QueueRedraw();
    }

    private void SyncQueueLabel(int queued)
    {
        if (_newsQueueLabel is null || _newsDisplayedQueued == queued) return;
        _newsDisplayedQueued = queued;
        _newsQueueLabel.Visible = queued > 0;
        if (queued > 0) _newsQueueLabel.Text = $"+{queued}";
        _newsCard.TooltipText = Ui.Tip(queued > 0
            ? $"{Loc.Format("shell.news_queued_fmt", queued)}\n{Loc.Get("shell.news_card_tip")}"
            : Loc.Get("shell.news_card_tip"));
    }

    private void UpdateNewsPinVisual(bool force = false)
    {
        var pinned = _newsPacer.IsPinned;
        if (!force && _newsDisplayedPinned == pinned) return;
        _newsDisplayedPinned = pinned;
        _newsPinText.Visible = pinned;
        _newsPinText.Text = Loc.Get("shell.pinned");
        _newsTimerRule.QueueRedraw();
    }

    private void RouteTopic(string screen, int targetId)
    {
        if (screen == "Space") { _ctx.TogglePause?.Invoke(); return; }
        if (screen is "Wars" or "War") { _ctx.OpenWarLedger?.Invoke(); if (targetId >= 0) _ctx.ShowWarDetail?.Invoke(targetId); return; }
        if (screen == "Defcon") { if (!_messages.IsVisible) _messages.Toggle(); return; }
        if (screen == "SovereignFinance") { _ctx.OpenSovereignFinance?.Invoke(); return; }
        _ctx.OpenScreen?.Invoke(screen);
    }

    private static string RouteOfEntry(CampaignAlertRoute route) => route switch
    {
        CampaignAlertRoute.War or CampaignAlertRoute.Battle => "Wars",
        CampaignAlertRoute.Research => "Research",
        CampaignAlertRoute.Doctrine or CampaignAlertRoute.Army => "Military",
        CampaignAlertRoute.Defcon => "Defcon",
        CampaignAlertRoute.Decisions => "Decisions",
        _ => "State Affairs"
    };

    private static string ResolveAlertIcon(ShellAlertEntry item) => item.IsNextHint ? "icon_shell_crane" : RouteOfEntry(item.Route) switch
    {
        "Wars" => "icon_alert_battle", "SovereignFinance" => "icon_shell_warning", "Trade" => "icon_shell_nickel",
        "Production" => "icon_industry", "Research" => "icon_research", "Diplomacy" => "icon_shell_treaty",
        "Economy" => "icon_shell_crane", _ => "icon_alert_event"
    };

    private static Color SeverityTint(ShellAlertSeverity sev, bool iconTint) => sev switch
    {
        ShellAlertSeverity.Critical => Ui.Colour("shell.state.negative"),
        ShellAlertSeverity.Warning => Ui.Colour("shell.state.warning"),
        _ => Ui.Colour(iconTint ? "shell.icon" : "shell.state.info")
    };
}
