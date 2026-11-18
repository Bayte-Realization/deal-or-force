using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _shellAlertColumn;
    private VBoxContainer _shellAlertRows;
    private PanelContainer _shellOutlinerBox;
    private Button _shellOutlinerToggleBtn;
    private Label _shellOutlinerOverflowLbl;
    private VBoxContainer _shellOutlinerRows;
    private bool _shellOutlinerOpen;

    private readonly HashSet<string> _shellViewedAlerts = new(StringComparer.Ordinal);
    private readonly CampaignAlertSnoozes _shellAlertSnoozes = new();
    private readonly HashSet<Commodity> _shellWatchedGoods = new();

    // Newspaper-clipping world news card
    private PanelContainer _shellNewsCardPanel;
    private TextureRect _shellNewsFlag;
    private Label _shellNewsHeadline;
    private Label _shellNewsReason;
    private Button _shellNewsPinBtn;
    private NewsFeed.Line _shellActiveNewsCard;
    private double _shellNewsAge;
    private bool _shellNewsPinned;
    private long _shellNewsSeenSeq;

    private void BuildShellAlertColumn()
    {
        var column = new Control
        {
            Name = "ShellAlertColumn",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _newShellRoot.AddChild(column);
        _shellAlertColumn = column;

        var vStack = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        vStack.AddThemeConstantOverride("separation", 6);
        vStack.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        column.AddChild(vStack);

        var alertRows = new VBoxContainer
        {
            Name = "ShellAlertRows",
            MouseFilter = MouseFilterEnum.Ignore
        };
        alertRows.AddThemeConstantOverride("separation", 5);
        vStack.AddChild(alertRows);
        _shellAlertRows = alertRows;

        // "Watching" ("Слежу") Outliner header + expandable drawer
        var outlinerBox = new PanelContainer
        {
            Name = "ShellOutlinerBox",
            MouseFilter = MouseFilterEnum.Stop
        };
        outlinerBox.AddThemeStyleboxOverride("panel", ShellPlateStyle(cornerPlate: false));
        vStack.AddChild(outlinerBox);
        _shellOutlinerBox = outlinerBox;

        var outMargin = new MarginContainer();
        outMargin.AddThemeConstantOverride("margin_left", 8);
        outMargin.AddThemeConstantOverride("margin_right", 8);
        outMargin.AddThemeConstantOverride("margin_top", 5);
        outMargin.AddThemeConstantOverride("margin_bottom", 6);
        outlinerBox.AddChild(outMargin);

        var outStack = new VBoxContainer();
        outStack.AddThemeConstantOverride("separation", 5);
        outMargin.AddChild(outStack);

        var toggleBtn = new Button
        {
            Name = "ShellOutlinerToggle",
            CustomMinimumSize = new Vector2(0, 30),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        toggleBtn.AddThemeStyleboxOverride("normal", ShellWellStyle(false));
        toggleBtn.AddThemeStyleboxOverride("hover", ShellPlateStyle(hover: true));
        toggleBtn.Pressed += ToggleShellOutliner;
        Tip(toggleBtn, "Toggle Watching outliner (J)");
        outStack.AddChild(toggleBtn);
        _shellOutlinerToggleBtn = toggleBtn;

        var hRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        hRow.AddThemeConstantOverride("separation", 7);
        hRow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        toggleBtn.AddChild(hRow);

        hRow.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });

        var eyeIcon = new TextureRect
        {
            Texture = ShellIconTexture("icon_shell_eye"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(15, 15),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.paper"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        hRow.AddChild(eyeIcon);

        var eyeLabel = new Label
        {
            Text = Tr("Watching"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        eyeLabel.AddThemeFontOverride("font", ShellFont("shell.serif_semibold"));
        eyeLabel.AddThemeFontSizeOverride("font_size", 13);
        eyeLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        eyeLabel.SetMeta(LocSourceMeta, "Watching");
        eyeLabel.SetMeta(LocAppliedMeta, eyeLabel.Text);
        hRow.AddChild(eyeLabel);

        var overflowLbl = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = MouseFilterEnum.Ignore
        };
        overflowLbl.AddThemeFontOverride("font", ShellFont("shell.sans", tabular: true));
        overflowLbl.AddThemeFontSizeOverride("font_size", 12);
        overflowLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        hRow.AddChild(overflowLbl);
        _shellOutlinerOverflowLbl = overflowLbl;

        hRow.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });

        var outlinerRows = new VBoxContainer
        {
            Name = "ShellOutlinerRows",
            Visible = false
        };
        outlinerRows.AddThemeConstantOverride("separation", 4);
        outStack.AddChild(outlinerRows);
        _shellOutlinerRows = outlinerRows;

        BuildShellNewsCard();
    }

    private void BuildShellNewsCard()
    {
        var card = new PanelContainer
        {
            Name = "ShellNewspaperCard",
            Visible = false,
            MouseFilter = MouseFilterEnum.Stop
        };
        card.AddThemeStyleboxOverride("panel", ShellPaperTagStyle(active: false));
        _newShellRoot.AddChild(card);
        _shellNewsCardPanel = card;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        card.AddChild(margin);

        var vBox = new VBoxContainer();
        vBox.AddThemeConstantOverride("separation", 5);
        margin.AddChild(vBox);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        vBox.AddChild(head);

        var flag = new TextureRect
        {
            CustomMinimumSize = new Vector2(28, 18),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        head.AddChild(flag);
        _shellNewsFlag = flag;

        var reason = new Label
        {
            Text = Tr("Dispatch"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        reason.AddThemeFontOverride("font", ShellFont("shell.sans"));
        reason.AddThemeFontSizeOverride("font_size", 11);
        reason.AddThemeColorOverride("font_color", Ui.Colour("shell.ink_muted"));
        head.AddChild(reason);
        _shellNewsReason = reason;

        var pinBtn = new Button
        {
            Name = "ShellNewsPinButton",
            CustomMinimumSize = new Vector2(24, 24),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        pinBtn.AddThemeStyleboxOverride("normal", ShellWellStyle(false));
        pinBtn.AddThemeStyleboxOverride("hover", ShellWellStyle(true));
        pinBtn.Pressed += () =>
        {
            _shellNewsPinned = !_shellNewsPinned;
            UpdateShellNewsPinVisual();
        };
        Tip(pinBtn, "Pin or unpin headline");
        var pinIcon = new TextureRect
        {
            Texture = ShellIconTexture("icon_shell_pin"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = Ui.Colour("shell.paper"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        pinIcon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        pinIcon.OffsetLeft = 4; pinIcon.OffsetRight = -4; pinIcon.OffsetTop = 4; pinIcon.OffsetBottom = -4;
        pinBtn.AddChild(pinIcon);
        head.AddChild(pinBtn);
        _shellNewsPinBtn = pinBtn;

        var closeBtn = new Button
        {
            Name = "ShellNewsCloseButton",
            Text = "×",
            CustomMinimumSize = new Vector2(24, 24),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        closeBtn.AddThemeStyleboxOverride("normal", ShellWellStyle(false));
        closeBtn.AddThemeStyleboxOverride("hover", ShellWellStyle(true));
        closeBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        closeBtn.Pressed += DismissShellNewsCard;
        head.AddChild(closeBtn);

        var headline = new Label
        {
            Text = "",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ClipText = true,
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        headline.AddThemeFontOverride("font", ShellFont("shell.serif_semibold"));
        headline.AddThemeFontSizeOverride("font_size", 14);
        headline.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
        headline.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && _shellActiveNewsCard is { } line)
            {
                _shellNewsPinned = true;
                UpdateShellNewsPinVisual();
                FocusNews(line);
                headline.AcceptEvent();
            }
        };
        vBox.AddChild(headline);
        _shellNewsHeadline = headline;
    }

    private void ToggleShellOutliner()
    {
        _shellOutlinerOpen = !_shellOutlinerOpen;
        RefreshShellAlertColumn();
    }

    private void LayoutShellAlertColumn(ShellLayoutProfile profile)
    {
        if (_shellAlertColumn is null) return;
        PlaceShellRect(_shellAlertColumn, profile.AlertColumn);
        if (_shellNewsCardPanel is not null)
            PlaceShellRect(_shellNewsCardPanel, profile.NewsCard);
    }

    private void RefreshShellAlertColumn()
    {
        if (_shellAlertRows is null || _campaign?.Player is not { } player) return;

        foreach (var child in _shellAlertRows.GetChildren()) child.QueueFree();
        foreach (var child in _shellOutlinerRows.GetChildren()) child.QueueFree();

        var nextHints = SettingsCatalog.Flag("ui_shell_next_hints");
        var snapshot = ShellAlerts.BuildSnapshot(
            _campaign,
            player,
            _shellOutlinerOpen,
            nextHints,
            _observerMode,
            _shellViewedAlerts,
            _shellAlertSnoozes,
            _shellWatchedGoods);

        foreach (var entry in snapshot.Alerts)
        {
            var captured = entry;
            var btn = new Button
            {
                Name = "ShellAlert_" + entry.Id.Replace(':', '_'),
                CustomMinimumSize = new Vector2(0, 42),
                MouseDefaultCursorShape = CursorShape.PointingHand
            };
            btn.AddThemeStyleboxOverride("normal", ShellPlateStyle());
            btn.AddThemeStyleboxOverride("hover", ShellPlateStyle(hover: true));
            btn.AddThemeStyleboxOverride("pressed", ShellWellStyle(true));
            btn.GuiInput += input =>
            {
                if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } && captured.Snoozable)
                {
                    var snoozeDays = Math.Max(1, _campaign.Defines.CampaignAlertSnoozeDays);
                    _shellAlertSnoozes.Hide(
                        new CampaignAlert(captured.Id,
                            captured.Severity == ShellAlertSeverity.Critical ? CampaignAlertGroup.Must : CampaignAlertGroup.Should,
                            captured.Title, captured.Summary, captured.Route, captured.TargetId),
                        _campaign.ElapsedDays,
                        captured.Severity <= ShellAlertSeverity.Warning ? 7 : Math.Max(snoozeDays, 30));
                    RefreshShellAlertColumn();
                    GetViewport().SetInputAsHandled();
                }
            };
            btn.Pressed += () =>
            {
                if (captured.IsNextHint)
                {
                    if (captured.Id == "next:research") OpenResearchScreen();
                    else if (captured.Id == "next:build") OpenScreen("Economy");
                    else TogglePause();
                    return;
                }
                _shellViewedAlerts.Add(captured.Id);
                FollowCampaignAlert(captured.Id);
                RefreshShellAlertColumn();
            };
            var tipText = captured.Summary + (captured.Snoozable ? "\n" + Tr("Right-click: hide temporarily.") : "");
            btn.TooltipText = Ui.Tip(tipText);

            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("separation", 7);
            row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            btn.AddChild(row);

            row.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });

            var dot = new ColorRect
            {
                CustomMinimumSize = new Vector2(8, 8),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                Color = Ui.Colour(AlertSeverityToken(entry.Severity)),
                MouseFilter = MouseFilterEnum.Ignore
            };
            row.AddChild(dot);

            var titleLbl = new Label
            {
                Text = entry.Title,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.Off,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MouseFilter = MouseFilterEnum.Ignore
            };
            titleLbl.AddThemeFontOverride("font", ShellFont(entry.IsNextHint ? "shell.sans" : "shell.serif_semibold"));
            titleLbl.AddThemeFontSizeOverride("font_size", 13);
            titleLbl.AddThemeColorOverride("font_color", Ui.Colour(entry.IsNextHint ? "shell.paper_muted" : "shell.paper"));
            row.AddChild(titleLbl);

            if (entry.IsViewed)
            {
                var tickIcon = new TextureRect
                {
                    Texture = ShellIconTexture("icon_shell_tick"),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    CustomMinimumSize = new Vector2(13, 13),
                    SizeFlagsVertical = SizeFlags.ShrinkCenter,
                    Modulate = Ui.Colour("shell.paper_muted"),
                    MouseFilter = MouseFilterEnum.Ignore
                };
                row.AddChild(tickIcon);
            }

            // Right-aligned fixed deadline column: never truncated
            if (!string.IsNullOrEmpty(entry.DeadlineLabel))
            {
                var dl = new Label
                {
                    Text = entry.DeadlineLabel,
                    CustomMinimumSize = new Vector2(46, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    AutowrapMode = TextServer.AutowrapMode.Off,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                dl.AddThemeFontOverride("font", ShellFont("shell.sans", tabular: true));
                dl.AddThemeFontSizeOverride("font_size", 12);
                dl.AddThemeColorOverride("font_color", Ui.Colour(entry.DaysRemaining <= 5 ? "shell.state.negative" : "shell.state.warning"));
                row.AddChild(dl);
            }

            row.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });
            _shellAlertRows.AddChild(btn);
        }

        _shellOutlinerOverflowLbl.Text = snapshot.HiddenAlertCount > 0
            ? $"+{snapshot.HiddenAlertCount}"
            : (snapshot.OutlinerGroups.Count > 0 ? $"({snapshot.OutlinerGroups.Sum(g => g.Rows.Count)})" : "");

        _shellOutlinerRows.Visible = _shellOutlinerOpen && snapshot.OutlinerGroups.Count > 0;
        if (_shellOutlinerRows.Visible)
        {
            foreach (var group in snapshot.OutlinerGroups)
            {
                var gLbl = new Label
                {
                    Text = group.Title.ToUpperInvariant(),
                    AutowrapMode = TextServer.AutowrapMode.Off,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                gLbl.AddThemeFontOverride("font", ShellFont("shell.sans"));
                gLbl.AddThemeFontSizeOverride("font_size", 11);
                gLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.brass_hi"));
                _shellOutlinerRows.AddChild(gLbl);

                foreach (var oRow in group.Rows)
                {
                    var capturedRow = oRow;
                    var oBtn = new Button
                    {
                        CustomMinimumSize = new Vector2(0, 30),
                        MouseDefaultCursorShape = CursorShape.PointingHand
                    };
                    oBtn.AddThemeStyleboxOverride("normal", ShellWellStyle(false));
                    oBtn.AddThemeStyleboxOverride("hover", ShellWellStyle(true));
                    oBtn.Pressed += () => OpenShellOutlinerTarget(capturedRow);

                    var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                    h.AddThemeConstantOverride("separation", 6);
                    h.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                    oBtn.AddChild(h);

                    h.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });

                    var nameLbl = new Label
                    {
                        Text = oRow.Title,
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        VerticalAlignment = VerticalAlignment.Center,
                        AutowrapMode = TextServer.AutowrapMode.Off,
                        ClipText = true,
                        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                        MouseFilter = MouseFilterEnum.Ignore
                    };
                    nameLbl.AddThemeFontOverride("font", ShellFont("shell.sans"));
                    nameLbl.AddThemeFontSizeOverride("font_size", 12);
                    nameLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
                    h.AddChild(nameLbl);

                    var valLbl = new Label
                    {
                        Text = oRow.Value,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        AutowrapMode = TextServer.AutowrapMode.Off,
                        MouseFilter = MouseFilterEnum.Ignore
                    };
                    valLbl.AddThemeFontOverride("font", ShellFont("shell.sans", tabular: true));
                    valLbl.AddThemeFontSizeOverride("font_size", 12);
                    valLbl.AddThemeColorOverride("font_color", Ui.Colour(AlertSeverityToken(oRow.Severity)));
                    h.AddChild(valLbl);

                    h.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });
                    _shellOutlinerRows.AddChild(oBtn);
                }
            }
        }
    }

    private void OpenShellOutlinerTarget(ShellOutlinerRow row)
    {
        switch (row.RouteKind)
        {
            case "War":
                OpenWarLedger();
                ShowWarLedgerDetail(row.TargetId);
                break;
            case "Battle":
                OpenFirstPlayerBattle();
                break;
            case "Economy":
                OpenScreen("Economy");
                break;
            case "Research":
                OpenResearchScreen();
                break;
            case "Trade":
                OpenTradeScreen();
                break;
        }
    }

    private static string AlertSeverityToken(ShellAlertSeverity sev) => sev switch
    {
        ShellAlertSeverity.Critical => "shell.state.negative",
        ShellAlertSeverity.Warning => "shell.state.warning",
        ShellAlertSeverity.Important => "shell.state.info",
        ShellAlertSeverity.Opportunity => "shell.state.positive",
        _ => "shell.paper_muted"
    };

    private void TickShellNewsAndHover(double delta)
    {
        if (_campaign is null) return;

        var newest = _shellNewsSeenSeq;
        var playerId = _campaign.Player?.Id ?? -1;
        foreach (var line in NewsFeed.Lines(_campaign, _shellNewsSeenSeq, 24))
        {
            if (line.Sequence > newest) newest = line.Sequence;
            _shellRecentNews.Insert(0, line);
            if (_shellRecentNews.Count > 60) _shellRecentNews.RemoveAt(_shellRecentNews.Count - 1);

            var evt = ClassifyNewsKind(line.Kind);
            var involvesPlayer = playerId >= 0 && (line.ActorId == playerId || line.TargetId == playerId);
            var delivery = ResolveShellMessage(evt, involvesPlayer);

            if (delivery.PauseClock && !_campaign.IsPaused)
            {
                _campaign.IsPaused = true;
                RefreshShellDatePlate();
            }

            if (delivery.ShowNewspaperCard)
            {
                var importance = NewsFeed.Importance(line.Kind);
                var tie = HeadlinePopups.TieOf(_campaign, line);
                if (involvesPlayer || importance >= 2 || tie != HeadlineTie.World)
                    PresentShellNewsCard(line);
            }
        }
        _shellNewsSeenSeq = newest;

        if (_shellNewsCardPanel is { Visible: true } && !_shellNewsPinned)
        {
            var mouse = GetViewport().GetMousePosition();
            if (!_shellNewsCardPanel.GetGlobalRect().HasPoint(mouse))
                _shellNewsAge += delta;

            var life = InterfaceSkin.Metric("shell.news_lifetime_sec", 9f);
            if (_shellNewsAge >= life)
                DismissShellNewsCard();
        }
    }

    private void PresentShellNewsCard(NewsFeed.Line line)
    {
        if (_shellNewsCardPanel is null) return;
        _shellActiveNewsCard = line;
        _shellNewsAge = 0;
        _shellNewsPinned = false;
        UpdateShellNewsPinVisual();

        var judge = HeadlinePopups.Judge(_campaign, line);
        var subject = judge.Through;
        _shellNewsFlag.Texture = subject is not null ? FlagTexture(subject.Tag) : null;
        _shellNewsFlag.Visible = _shellNewsFlag.Texture is not null;
        _shellNewsReason.Text = HeadlinePopups.Reason(judge.Tie, subject?.DisplayName ?? "");
        _shellNewsHeadline.Text = line.Text;
        _shellNewsCardPanel.Visible = true;
    }

    private void UpdateShellNewsPinVisual()
    {
        if (_shellNewsPinBtn is null) return;
        _shellNewsPinBtn.AddThemeStyleboxOverride("normal", ShellWellStyle(_shellNewsPinned));
    }

    private void DismissShellNewsCard()
    {
        if (_shellNewsCardPanel is not null) _shellNewsCardPanel.Visible = false;
        _shellActiveNewsCard = null;
        _shellNewsPinned = false;
        _shellNewsAge = 0;
    }
}
