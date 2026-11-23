using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Seamless top-left L-corner plate: one continuous chamfered graphite & brushed-titanium frame
/// joining the recessed natural-aspect flag pocket (Q), overall rank line, 2-row telemetry cells, and 64px rail.
/// </summary>
internal sealed class ShellCornerView
{
    private readonly ShellContext _ctx;
    private readonly ShellNavRailView _rail;
    private readonly ShellXpStripView _xpStrip;
    private Control _backdrop;
    private PanelContainer _topHitBox;
    private PanelContainer _railHitBox;
    private PanelContainer _cellsBed;
    private Button _flagButton;
    private Control _flagInner;
    private TextureRect _flagTexture;
    private Control _flagEdgeOverlay;
    private Label _rankNumLabel;
    private Label _rankSubLabel;
    private ColorRect _rankActiveUnderline;
    private PanelContainer _flagKbd;
    private StyleBox _flagPocketNormal;
    private StyleBoxFlat _flagPocketHover;
    private StyleBoxFlat _flagPocketActive;
    private StyleBoxEmpty _rankNormalStyle;
    private StyleBoxFlat _rankHoverStyle;
    private PanelContainer _rankHost;

    private ShellLayoutProfile _profile;
    private bool _atWar;
    private string _lastTag = "";
    private bool? _lastCountryOpen;
    private (Campaign World, int Week, int Rev, int WarRev, int PlayerId, string Lang, int Rank, double Score) _rankCache;
    private (Campaign World, int NationId, int SampleDay, float SampleGdp, float GrowthPct) _gdpSample;
    private (int PlayerId, float Treasury, float Balance, float Manpower, bool Observer, string Language, string Screen)? _resourceReadout;
    private StyleBoxEmpty _emptyStyle;
    private StyleBoxFlat _cellHoverStyle;
    private readonly List<StrategicWar> _openWarsBuf = new(8);
    private readonly Dictionary<string, StatCellNodes> _cells = new(StringComparer.Ordinal);

    private sealed class StatCellNodes
    {
        public string Id;
        public string ScreenMatch;
        public PanelContainer Box;
        public TextureRect Icon;
        public HBoxContainer TopLine;
        public HBoxContainer BotLine;
        public Label Figure;
        public Label Unit;
        public Label Caption;
        public Label Delta;
        public ColorRect StatusRule;
        public Control SepGroove;
        public string LastFig = "";
        public string LastUnit = "";
        public string LastFigColor = "";
        public string LastDelta = "";
        public string LastDeltaColor = "";
        public string LastTooltip = "";
        public bool LastActive;
    }

    public ShellCornerView(ShellContext ctx)
    {
        _ctx = ctx;
        _rail = new ShellNavRailView(ctx);
        _xpStrip = new ShellXpStripView(ctx);
    }

    public void Build()
    {
        _emptyStyle ??= new StyleBoxEmpty();
        _cellHoverStyle ??= Ui.ChromeBox("shell.plate_hover", 0.80f, "shell.brass_hi", 0.72f, cornerRadius: 2, borderWidth: 1);
        _backdrop = new Control { Name = "ShellLCornerBackdrop", MouseFilter = Control.MouseFilterEnum.Ignore };
        _backdrop.Draw += DrawSeamlessLCorner;
        _ctx.Root.AddChild(_backdrop);

        _topHitBox = new PanelContainer { Name = "ShellTopPlate", MouseFilter = Control.MouseFilterEnum.Stop };
        _topHitBox.AddThemeStyleboxOverride("panel", _emptyStyle);
        _ctx.Root.AddChild(_topHitBox);
        ChromePlateArt.AttachSurfaceTexture(_topHitBox, "leather", 0.28f);

        _railHitBox = new PanelContainer { Name = "ShellRailPlate", MouseFilter = Control.MouseFilterEnum.Stop };
        _railHitBox.AddThemeStyleboxOverride("panel", _emptyStyle);
        _ctx.Root.AddChild(_railHitBox);
        ChromePlateArt.AttachSurfaceTexture(_railHitBox, "leather", 0.28f);

        BuildFlagAndRank();
        BuildCellsBed();
        _xpStrip.Build();
        _rail.Build();
    }

    private void BuildFlagAndRank()
    {
        var flagBtn = new Button
        {
            Name = "ShellFlagButton",
            ClipContents = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        _flagPocketNormal = _ctx.NineSlice("flag_pocket_9s", 10, 0, 0, 0, 0, "shell.well_top", "shell.border", 2);
        _flagPocketHover = Ui.ChromeBox("shell.plate_hover", 0.90f, "shell.brass_hi", 0.88f, cornerRadius: 3, borderWidth: 1);
        _flagPocketActive = Ui.ChromeBox("shell.well_top", 0.98f, "shell.brass_hi", 1f, cornerRadius: 3, borderWidth: 2);
        flagBtn.AddThemeStyleboxOverride("normal", _flagPocketNormal);
        flagBtn.AddThemeStyleboxOverride("hover", _flagPocketHover);
        flagBtn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle());
        flagBtn.Pressed += () => { _rail.HideHoverTag(); _ctx.PlayCue?.Invoke("shell_plate_tap"); _ctx.ToggleScreen?.Invoke("Nation"); };
        flagBtn.MouseEntered += () =>
        {
            var nationName = _ctx.World?.Player?.DisplayName ?? Loc.Get("Country");
            var tagX = _profile.LeftRail.Right > 0f ? _profile.LeftRail.Right + 8f : flagBtn.Position.X;
            var tagY = Math.Max(
                flagBtn.Position.Y + flagBtn.Size.Y + 6f,
                Math.Max(_profile.CornerNode.Bottom + 6f, _profile.TopPlate.Bottom + (_profile.Compact ? 34f : 44f)));
            _rail.ShowHoverTag(nationName, "Q", Loc.Get("shell.sub_country"), tagY, tagX);
        };
        flagBtn.MouseExited += _rail.HideHoverTag;
        _ctx.Root.AddChild(flagBtn);
        _flagButton = flagBtn;

        _flagInner = new Control { Name = "ShellFlagInner", ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        _flagInner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        flagBtn.AddChild(_flagInner);

        _flagTexture = new TextureRect
        {
            Name = "ShellFlagTexture",
            Material = FlagClothPresentation.Material,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _flagInner.AddChild(_flagTexture);

        _flagEdgeOverlay = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _flagEdgeOverlay.Draw += () =>
        {
            var sz = _flagEdgeOverlay.Size;
            if (sz.X <= 2f || sz.Y <= 2f) return;
            _flagEdgeOverlay.DrawRect(new Rect2(0, 0, sz.X, sz.Y), Ui.Colour("shell.border", 0.88f), filled: false, width: 1f);
            _flagEdgeOverlay.DrawLine(new Vector2(1, 1), new Vector2(sz.X - 1, 1), Ui.Colour("shell.brass_hi", 0.34f), 1f);
            _flagEdgeOverlay.DrawLine(new Vector2(1, sz.Y - 1), new Vector2(sz.X - 1, sz.Y - 1), Ui.Colour("shell.border", 0.40f), 1f);
        };
        _flagInner.AddChild(_flagEdgeOverlay);

        _flagKbd = _ctx.CreateKeycapBadge("Q");
        _ctx.Root.AddChild(_flagKbd);

        _rankNormalStyle = new StyleBoxEmpty();
        _rankHoverStyle = Ui.ChromeBox("shell.plate_hover", 0.72f, "shell.brass_hi", 0.70f, cornerRadius: 2, borderWidth: 1);
        _rankHost = new PanelContainer
        {
            Name = "ShellRankPlate",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        _rankHost.AddThemeStyleboxOverride("panel", _rankNormalStyle);
        _rankHost.MouseEntered += () => _rankHost.AddThemeStyleboxOverride("panel", _rankHoverStyle);
        _rankHost.MouseExited += () => _rankHost.AddThemeStyleboxOverride("panel", _rankNormalStyle);
        _rankHost.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                _ctx.PlayCue?.Invoke("ui_select");
                _ctx.OpenCountryRankings?.Invoke(_ctx.World?.Player?.Id ?? -1);
                _rankHost.AcceptEvent();
            }
        };
        _rankHost.TooltipText = Ui.Tip(Loc.Get("World power rank — click to open Country Rankings."));
        _ctx.Root.AddChild(_rankHost);

        var rankRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        rankRow.AddThemeConstantOverride("separation", 4);
        rankRow.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _rankHost.AddChild(rankRow);

        _rankNumLabel = new Label
        {
            Name = "ShellRankBadge", Text = "№ 1",
            VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _rankNumLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
        _rankNumLabel.AddThemeFontSizeOverride("font_size", 14);
        _rankNumLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        rankRow.AddChild(_rankNumLabel);

        _rankSubLabel = new Label
        {
            Text = Loc.Get("shell.overall_rank"),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _rankSubLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _rankSubLabel.AddThemeFontSizeOverride("font_size", 11);
        _rankSubLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        rankRow.AddChild(_rankSubLabel);

        _rankActiveUnderline = new ColorRect { Color = Ui.Colour("shell.brass_hi"), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _rankHost.AddChild(_rankActiveUnderline);
    }

    private void BuildCellsBed()
    {
        _cellsBed = new PanelContainer { Name = "ShellCellsBed", ClipContents = true, MouseFilter = Control.MouseFilterEnum.Pass };
        _cellsBed.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("well_recessed_9s", 12, 2, 0, 2, 0, "shell.well_top", "shell.border", 2));
        _ctx.Root.AddChild(_cellsBed);

        var row = new HBoxContainer { Name = "ShellStatRow", ClipContents = true };
        row.AddThemeConstantOverride("separation", 0);
        _cellsBed.AddChild(row);

        AddStatCell(row, "Treasury", "Economy", "icon_treasury", "Treasury", () => _ctx.ToggleScreen?.Invoke("Economy"));
        AddStatCell(row, "GDP", "Ledger", "icon_gdp", "GDP", () => _ctx.ToggleScreen?.Invoke("Ledger"));
        AddStatCell(row, "Debt", "SovereignFinance", "icon_coins", "shell.cap_debt", () => _ctx.OpenSovereignFinance?.Invoke());
        AddStatCell(row, "Inflation", "State Affairs", "icon_inflation", "Inflation", () => _ctx.ToggleScreen?.Invoke("Economy"));
        AddStatCell(row, "Stability", "Laws", "icon_stability", "Stability", () => _ctx.ToggleScreen?.Invoke("Laws"));
        AddStatCell(row, "Manpower", "Military", "icon_manpower", "shell.cap_manpower", () => _ctx.OpenRecruitment?.Invoke());
        AddStatCell(row, "Capital", "State Affairs", "icon_shell_capital", "shell.cap_polweight", () => _ctx.ToggleScreen?.Invoke("State Affairs"));
        AddStatCell(row, "War", "Wars", "icon_wars", "shell.cap_war", () => _ctx.OpenWarLedger?.Invoke());
    }

    private void AddStatCell(HBoxContainer parent, string id, string screenMatch, string iconName, string capKey, Action onClick)
    {
        var box = new PanelContainer
        {
            Name = "ShellStat_" + id,
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        _emptyStyle ??= new StyleBoxEmpty();
        _cellHoverStyle ??= Ui.ChromeBox("shell.plate_hover", 0.80f, "shell.brass_hi", 0.72f, cornerRadius: 2, borderWidth: 1);
        box.AddThemeStyleboxOverride("panel", _emptyStyle);

        var cell = new StatCellNodes { Id = id, ScreenMatch = screenMatch, Box = box };
        box.MouseEntered += () => { if (!cell.LastActive) box.AddThemeStyleboxOverride("panel", _cellHoverStyle); };
        box.MouseExited += () => { box.AddThemeStyleboxOverride("panel", cell.LastActive ? _ctx.PressedWellStyle(underlineBottom: true) : _emptyStyle); };
        box.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                _ctx.PlayCue?.Invoke("ui_select");
                onClick();
                box.AcceptEvent();
            }
        };

        var inner = new Control { ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        inner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        box.AddChild(inner);

        var icon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = _ctx.Icon(iconName),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        ShellContext.ApplyIconRelief(icon, Ui.Colour("shell.icon"), 0.55f);
        inner.AddChild(icon);
        cell.Icon = icon;

        var topLine = new HBoxContainer { ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        topLine.AddThemeConstantOverride("separation", 3);
        inner.AddChild(topLine);
        cell.TopLine = topLine;

        var fig = new Label
        {
            Text = "0",
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        fig.AddThemeFontOverride("font", PresentationFonts.Tabular());
        fig.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        topLine.AddChild(fig);
        cell.Figure = fig;

        var unit = new Label
        {
            Text = "",
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        unit.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        unit.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        topLine.AddChild(unit);
        cell.Unit = unit;

        var botLine = new HBoxContainer { ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        botLine.AddThemeConstantOverride("separation", 4);
        inner.AddChild(botLine);
        cell.BotLine = botLine;

        var cap = new Label
        {
            Text = Loc.Get(capKey),
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        cap.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        cap.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        cap.SetMeta("shell_cap_key", capKey);
        botLine.AddChild(cap);
        cell.Caption = cap;

        var delta = new Label
        {
            Text = "",
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        delta.AddThemeFontOverride("font", PresentationFonts.Tabular());
        botLine.AddChild(delta);
        cell.Delta = delta;

        var statusRule = new ColorRect { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        inner.AddChild(statusRule);
        cell.StatusRule = statusRule;

        var sep = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        sep.Draw += () =>
        {
            var h = sep.Size.Y;
            if (h <= 10f) return;
            sep.DrawLine(new Vector2(0, 6), new Vector2(0, h - 6), Ui.Colour("shell.sep_dark"), 1f);
            sep.DrawLine(new Vector2(1, 6), new Vector2(1, h - 6), Ui.Colour("shell.brass_dim", 0.50f), 1f);
        };
        inner.AddChild(sep);
        cell.SepGroove = sep;

        parent.AddChild(box);
        _cells[id] = cell;
    }

    public void Layout(ShellLayoutProfile profile, bool atWar)
    {
        _profile = profile;
        _atWar = atWar;
        var compact = profile.Compact;

        var ox = profile.TopPlate.X;
        var oy = profile.TopPlate.Y;
        ShellContext.PlaceRect(_backdrop, new ShellRect(0, 0, profile.TopPlate.Right, profile.LeftRail.Bottom));
        ShellContext.PlaceRect(_topHitBox, profile.TopPlate);
        ShellContext.PlaceRect(_railHitBox, profile.LeftRail);
        _backdrop.QueueRedraw();

        var flagRect = compact ? new ShellRect(ox + 7f, oy + 6f, 62f, 46f) : new ShellRect(ox + 10f, oy + 8f, 92f, 68f);
        var rankRect = compact ? new ShellRect(ox + 5f, oy + 56f, 66f, 22f) : new ShellRect(ox + 8f, oy + 84f, 96f, 28f);

        ShellContext.PlaceRect(_flagButton, flagRect);
        ShellContext.PlaceRect(_rankHost, rankRect);
        _rankNumLabel.AddThemeFontSizeOverride("font_size", compact ? 11 : 14);
        _rankSubLabel.Visible = !compact;
        _rankSubLabel.Text = Loc.Get("shell.overall_rank");
        _rankActiveUnderline.Position = new Vector2(rankRect.W * 0.18f, rankRect.H - 3f);
        _rankActiveUnderline.Size = new Vector2(rankRect.W * 0.64f, 2.5f);
        _flagKbd.Position = new Vector2(flagRect.Right - 18f, flagRect.Bottom - 16f);

        var cellX0 = ox + (compact ? 82f : 120f);
        var cellCount = atWar ? 8 : 7;
        var cellW = profile.StatCellWidth;
        var bedY = oy + (compact ? 4f : 6f);
        var bedH = profile.StatCellHeight;

        var tight = cellW < 76f;
        var narrow = cellW < 92f;
        var iconSz = tight ? 12f : (narrow ? 14f : (compact ? 18f : 24f));
        var ix = tight ? 3f : (narrow ? 4f : (compact ? 6f : 9f));
        var iy = tight ? 5f : (narrow ? 4f : (compact ? 5f : 6f));
        var vx = tight ? 17f : (narrow ? 20f : (compact ? 28f : 38f));
        var vy = compact ? 1f : 4f;
        var cy = compact ? 22f : 31f;

        var visibleOrder = new[] { "Treasury", "GDP", "Debt", "Inflation", "Stability", "Manpower", "Capital", "War" };
        for (var i = 0; i < visibleOrder.Length; i++)
        {
            var c = _cells[visibleOrder[i]];
            var isWar = c.Id == "War";
            c.Box.Visible = !isWar || atWar;
            c.Box.CustomMinimumSize = new Vector2(cellW, bedH);
            c.Icon.Position = new Vector2(ix, iy);
            c.Icon.Size = new Vector2(iconSz, iconSz);

            c.TopLine.Position = new Vector2(vx, vy);
            c.TopLine.Size = new Vector2(MathF.Max(16f, cellW - vx - (narrow ? 2f : 4f)), compact ? 21f : 26f);
            c.TopLine.AddThemeConstantOverride("separation", tight ? 1 : (narrow ? 2 : 3));
            c.Figure.AddThemeFontSizeOverride("font_size", tight ? 11 : (narrow ? 12 : (compact ? 14 : 19)));
            c.Unit.AddThemeFontSizeOverride("font_size", tight ? 8 : (narrow ? 9 : (compact ? 10 : 12)));

            c.BotLine.Position = new Vector2(ix, cy);
            c.BotLine.Size = new Vector2(MathF.Max(16f, cellW - ix - (narrow ? 3f : 5f)), compact ? 15f : 16f);
            c.BotLine.AddThemeConstantOverride("separation", narrow ? 2 : 4);
            c.Caption.AddThemeFontSizeOverride("font_size", tight ? 8 : (narrow ? 9 : (compact ? 10 : 11)));
            c.Caption.Text = Loc.Get(c.Caption.GetMeta("shell_cap_key").AsString());
            c.Delta.AddThemeFontSizeOverride("font_size", tight ? 8 : (narrow ? 9 : (compact ? 10 : 11)));

            var isLast = (!atWar && c.Id == "Capital") || (atWar && c.Id == "War");
            c.SepGroove.Visible = !isLast;
            c.SepGroove.Position = new Vector2(cellW - 2f, 0f);
            c.SepGroove.Size = new Vector2(2f, bedH);
            c.SepGroove.QueueRedraw();
            c.StatusRule.Position = new Vector2(6f, bedH - 3f);
            c.StatusRule.Size = new Vector2(MathF.Max(8f, cellW - 14f), 2f);
        }

        // The visible cells set the bed's minimum width. Apply the new rect after updating them.
        ShellContext.PlaceRect(_cellsBed, new ShellRect(cellX0 - 2f, bedY, cellCount * cellW + 4f, bedH));

        _xpStrip.Layout(profile);
        _rail.Layout(profile);
        if (_lastTag.Length > 0) FitNaturalFlag(_lastTag);
    }

    public void Refresh()
    {
        var world = _ctx.World;
        if (world?.Player is not { } player) return;

        _openWarsBuf.Clear();
        var allOpenWars = Warfare.OpenWars(world);
        for (var i = 0; i < allOpenWars.Count; i++)
        {
            var w = allOpenWars[i];
            if (!w.Ended && w.Involves(player.Id)) _openWarsBuf.Add(w);
        }
        var atWar = _openWarsBuf.Count > 0;
        if (atWar != _atWar)
        {
            Layout(_ctx.ResolveViewportProfile(atWar), atWar);
        }

        if (_lastTag != player.Tag)
        {
            _lastTag = player.Tag;
            FitNaturalFlag(player.Tag);
        }
        _flagButton.TooltipText = Ui.Tip(Loc.Format("shell.clarity.flag_tip_fmt",
            player.DisplayName, player.Tag, Loc.Get(PoliticsCore.GovernmentOf(player).Name)));

        var openScreen = _ctx.GetOpenScreen?.Invoke() ?? "";
        var countryOpen = string.Equals(openScreen, "Nation", StringComparison.Ordinal);
        _rankActiveUnderline.Visible = countryOpen;
        _flagButton.ButtonPressed = countryOpen;
        if (_lastCountryOpen != countryOpen)
        {
            _lastCountryOpen = countryOpen;
            _flagButton.AddThemeStyleboxOverride("normal", countryOpen ? _flagPocketActive : _flagPocketNormal);
        }

        var (rank, score, rankChanged) = ResolveOverallRank(world, player);
        var rankStr = rank > 0 ? $"№ {rank}" : "№ —";
        if (_rankNumLabel.Text != rankStr) _rankNumLabel.Text = rankStr;
        if (rankChanged)
        {
            _rankHost.TooltipText = Ui.Tip(rank > 0
                ? $"{Loc.Format("Overall rank #{0} · index {1}/100", rank, score.ToString("0.0"))}\n{Loc.Get("World power rank — click to open Country Rankings.")}"
                : $"{Loc.Get("Country rankings · no visible territorial statistics")}\n{Loc.Get("World power rank — click to open Country Rankings.")}");
        }

        RefreshCells(world, player, _openWarsBuf, openScreen);
        _xpStrip.Refresh(world, player);
        _rail.Refresh(world, player, _openWarsBuf, openScreen);
        SyncKeycapVisibility();
    }

    public void RefreshResources(Nation player)
    {
        if (player is null) return;
        var openScreen = _ctx.GetOpenScreen?.Invoke() ?? "";
        var observer = _ctx.IsObserverMode?.Invoke() ?? (_ctx.World?.ObserverMode ?? false);
        var readout = (player.Id, player.Treasury, player.WeeklyBalance, player.ManpowerReserve, observer, Loc.Language, openScreen);
        if (_resourceReadout is { } previous && previous.Equals(readout)) return;
        _resourceReadout = readout;

        var (trFig, trUnit) = SplitMoney(player.Treasury);
        var (balDelta, _) = SplitMoney(MathF.Abs(player.WeeklyBalance));
        var trDeltaTxt = MathF.Abs(player.WeeklyBalance) >= 0.0005f ? (player.WeeklyBalance >= 0 ? "▲ " : "▼ ") + balDelta : "";
        UpdateCell("Treasury", trFig, trUnit,
            player.Treasury < 0.05f ? "shell.state.negative_text" : "shell.paper",
            trDeltaTxt, player.WeeklyBalance >= 0 ? "shell.state.positive" : "shell.state.negative",
            $"{Loc.Get("Treasury")}: {Fmt.MoneyExact(player.Treasury)}\n{Loc.Get("Balance")}: {Fmt.MoneyDelta(player.WeeklyBalance)}/{Loc.Get("week")}",
            openScreen == "Economy");

        var (manFig, manUnit) = SplitPeople((long)Math.Max(0, player.ManpowerReserve));
        var reinfLine = player.ReinforcementCapacity > 0
            ? $"\n{Loc.Format("shell.clarity.reinf_rate_fmt", Fmt.People(player.ReinforcementCapacity))}"
            : "";
        UpdateCell("Manpower", manFig, manUnit, "shell.paper", "", "shell.state.positive",
            $"{Loc.Get("Manpower")}: {Fmt.PeopleExact(player.ManpowerReserve)}{reinfLine}", openScreen == "Military");
        if (_ctx.World is { } w) _xpStrip.Refresh(w, player);
    }

    private void RefreshCells(Campaign world, Nation player, IReadOnlyList<StrategicWar> openWars, string openScreen)
    {
        var debt = SovereignFinance.Outstanding(player);
        var debtRatio = debt / MathF.Max(0.01f, player.Gdp);
        var overdue = player.DebtArrears + player.PaymentArrears;

        var (gdpFig, gdpUnit) = SplitMoney(player.Gdp);
        var growthPct = SampleGdpDeltaPct(world, player);
        var gdpDelta = MathF.Abs(growthPct) >= 0.05f ? $"{(growthPct >= 0 ? "▲" : "▼")} {MathF.Abs(growthPct):0.0}%" : "";
        UpdateCell("GDP", gdpFig, gdpUnit, "shell.paper",
            gdpDelta, growthPct >= 0 ? "shell.state.positive" : "shell.state.negative",
            $"{Loc.Get("GDP")}: {Fmt.MoneyExact(player.Gdp)}", openScreen == "Ledger");

        UpdateCell("Debt", $"{(debtRatio * 100f):0.0}%", "",
            overdue > 0.0001f ? "shell.state.negative_text" : (debtRatio > 0.85f ? "shell.state.warning_text" : "shell.paper"),
            overdue > 0.0001f ? "●" : "", "shell.state.negative",
            $"{Loc.Get("Public debt")}: {Fmt.MoneyExact(debt)} ({Fmt.Percent(debtRatio)} {Loc.Get("of GDP")})", false);

        var touch = _profile?.TouchMode == true || DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var infDrivers = touch ? "" : FormatTopDrivers(player.InflationDrivers, asPercent: true);
        UpdateCell("Inflation", $"{(player.Inflation * 100f):0.0}%", "",
            player.Inflation > 0.12f ? "shell.state.negative_text" : (player.Inflation > 0.06f ? "shell.state.warning_text" : "shell.paper"),
            player.Inflation > 0.05f ? "▲" : "", "shell.state.warning",
            touch ? "" : $"{Loc.Get("Inflation")}: {Fmt.Percent(player.Inflation)}{infDrivers}", false);

        var stabDrivers = touch ? "" : FormatTopDrivers(player.StabilityDrivers, asPercent: true);
        UpdateCell("Stability", $"{(int)MathF.Round(player.Stability * 100f)}%", "",
            player.Stability < 0.35f ? "shell.state.negative_text" : (player.Stability < 0.50f ? "shell.state.warning_text" : "shell.paper"),
            player.Stability < 0.50f ? "▼" : "", "shell.state.negative",
            touch ? "" : $"{Loc.Get("Stability")}: {Fmt.Percent(player.Stability)}{stabDrivers}", openScreen == "Laws");

        var ppWeekly = world.Defines.PoliticalPointsPerWeek + world.Defines.PoliticalPointsStabilityWeight * Math.Clamp(player.Stability, 0f, 1f);
        var ppDeltaTxt = ppWeekly >= 0.05f ? $"+{ppWeekly.ToString("0.0", CultureInfo.InvariantCulture)}" : "";
        UpdateCell("Capital", ((int)MathF.Round(player.PoliticalPoints)).ToString(CultureInfo.InvariantCulture), "",
            "shell.paper", ppDeltaTxt, "shell.state.positive",
            $"{Loc.Get("Political capital")}: {player.PoliticalPoints:0.0}\n{Loc.Format("shell.xp.pol_rate_fmt", ppWeekly.ToString("0.0", CultureInfo.InvariantCulture))}\n{Loc.Get("shell.xp.pol_unlocks")}",
            openScreen == "State Affairs");

        if (openWars.Count > 0)
        {
            var lead = openWars[0];
            var score = (int)MathF.Round(lead.IsAttacking(player.Id) ? lead.WarScore : -lead.WarScore);
            var wsPct = (int)MathF.Round(player.WarSupport * 100f);
            if (_cells.TryGetValue("War", out var warCell) && warCell.Caption is not null)
                warCell.Caption.Text = Loc.Format("shell.cap_wars_count", openWars.Count);
            UpdateCell("War", $"{wsPct}%", "",
                wsPct < 40 ? "shell.state.negative_text" : "shell.state.warning_text",
                $"{score:+#;-#;0}%", score >= 0 ? "shell.state.positive" : "shell.state.negative",
                $"{Loc.Get("Wars")}: {openWars.Count} · {Loc.Get("War score")}: {score:+#;-#;0}% · {Loc.Get("War support")}: {wsPct}%", openScreen == "Wars");
        }
    }

    private static string FormatTopDrivers(Breakdown breakdown, bool asPercent)
    {
        if (breakdown is null || !breakdown.HasEntries) return "";
        var top = breakdown.Entries
            .Where(e => MathF.Abs(e.Value) >= 0.0005f)
            .OrderByDescending(e => MathF.Abs(e.Value))
            .Take(4)
            .ToArray();
        if (top.Length == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append('\n').Append(Loc.Get("shell.clarity.drivers_header"));
        for (var i = 0; i < top.Length; i++)
        {
            var e = top[i];
            var valStr = asPercent
                ? $"{(e.Value * 100f):+0.0;-0.0;0.0}%"
                : e.Value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
            sb.Append("\n  ").Append(valStr).Append("  ").Append(Loc.Get(e.Source));
        }
        return sb.ToString();
    }

    private void UpdateCell(string id, string fig, string unit, string figColorTok, string delta, string deltaColorTok, string tip, bool active)
    {
        if (!_cells.TryGetValue(id, out var c)) return;
        if (c.LastFig != fig || c.LastUnit != unit || c.LastFigColor != figColorTok || c.LastDelta != delta || c.LastDeltaColor != deltaColorTok)
        {
            c.LastFig = fig;
            c.LastUnit = unit;
            c.LastFigColor = figColorTok;
            c.LastDelta = delta;
            c.LastDeltaColor = deltaColorTok;
            c.Figure.Text = fig;
            c.Unit.Text = unit;
            c.Unit.Visible = unit.Length > 0;
            c.Figure.AddThemeColorOverride("font_color", Ui.Colour(figColorTok));
            c.Delta.Text = delta;
            c.Delta.Visible = delta.Length > 0;
            if (delta.Length > 0) c.Delta.AddThemeColorOverride("font_color", Ui.Colour(deltaColorTok));
            var warnRule = figColorTok is "shell.state.negative_text" or "shell.state.warning_text";
            c.StatusRule.Visible = warnRule;
            if (warnRule)
                c.StatusRule.Color = Ui.Colour(figColorTok == "shell.state.negative_text" ? "shell.state.negative" : "shell.state.warning", 0.85f);
        }
        if (c.LastTooltip != tip)
        {
            c.LastTooltip = tip;
            c.Box.TooltipText = Ui.Tip(tip);
        }
        if (c.LastActive != active)
        {
            c.LastActive = active;
            _emptyStyle ??= new StyleBoxEmpty();
            c.Box.AddThemeStyleboxOverride("panel", active ? _ctx.PressedWellStyle(underlineBottom: true) : _emptyStyle);
        }
    }

    public void SyncKeycapVisibility()
    {
        var vis = _ctx.AltKeycapsVisible && _profile?.TouchMode != true;
        if (_flagKbd.Visible != vis) _flagKbd.Visible = vis;
        _rail.SyncKeycapVisibility(vis);
    }

    private void FitNaturalFlag(string tag)
    {
        var tex = _ctx.FlagTexture(tag);
        _flagTexture.Texture = tex;
        var pad = _profile.Compact ? 8f : 10f;
        var fitted = ShellLayout.FitFlag(tex?.GetWidth() ?? 300, tex?.GetHeight() ?? 200, Math.Max(8f, _profile.FlagWellWidth - pad), Math.Max(8f, _profile.FlagWellHeight - pad), tag);
        var pos = new Vector2((_profile.FlagWellWidth - fitted.Width) * 0.5f, (_profile.FlagWellHeight - fitted.Height) * 0.5f);
        _flagTexture.Position = pos;
        _flagTexture.Size = new Vector2(fitted.Width, fitted.Height);
        _flagEdgeOverlay.Visible = !fitted.CutoutSilhouette;
        _flagEdgeOverlay.Position = pos;
        _flagEdgeOverlay.Size = new Vector2(fitted.Width, fitted.Height);
        _flagEdgeOverlay.QueueRedraw();
    }

    private float SampleGdpDeltaPct(Campaign world, Nation player)
    {
        if (!ReferenceEquals(_gdpSample.World, world) || _gdpSample.NationId != player.Id)
        {
            _gdpSample = (world, player.Id, world.ElapsedDays, player.Gdp, 0f);
            return 0f;
        }
        var days = world.ElapsedDays - _gdpSample.SampleDay;
        if (days >= 7 && _gdpSample.SampleGdp > 0.01f)
        {
            var annualized = ((player.Gdp - _gdpSample.SampleGdp) / _gdpSample.SampleGdp) * (365f / days) * 100f;
            _gdpSample = (world, player.Id, world.ElapsedDays, player.Gdp, annualized);
        }
        return _gdpSample.GrowthPct;
    }

    private (int Rank, double Score, bool Changed) ResolveOverallRank(Campaign world, Nation player)
    {
        var lang = Loc.Language;
        var week = world.ElapsedDays / 7;
        if (ReferenceEquals(_rankCache.World, world) && _rankCache.Week == week &&
            _rankCache.Rev == world.MapRevision && _rankCache.WarRev == world.WarRevision &&
            _rankCache.PlayerId == player.Id)
        {
            var langChanged = !string.Equals(_rankCache.Lang, lang, StringComparison.Ordinal);
            if (langChanged)
                _rankCache = (world, week, _rankCache.Rev, _rankCache.WarRev, player.Id, lang, _rankCache.Rank, _rankCache.Score);
            return (_rankCache.Rank, _rankCache.Score, langChanged);
        }

        if (world.MidStep)
        {
            if (ReferenceEquals(_rankCache.World, world) && _rankCache.PlayerId == player.Id && _rankCache.Rank >= 0)
                return (_rankCache.Rank, _rankCache.Score, false);
            return (0, 0.0, false);
        }

        var weights = new CountryRankings.Weights(
            Ui.Metric("rankings.weight.economy", 40),
            Ui.Metric("rankings.weight.population", 15),
            Ui.Metric("rankings.weight.field_power", 30),
            Ui.Metric("rankings.weight.technology", 15));
        var rows = CountryRankings.Read(world, weights);
        var rank = 0;
        var score = 0.0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].NationId == player.Id)
            {
                rank = rows[i].OverallRank;
                score = rows[i].Overall;
                break;
            }
        }
        _rankCache = (world, week, world.MapRevision, world.WarRevision, player.Id, lang, rank, score);
        return (rank, score, true);
    }

    private static (string Fig, string Unit) SplitMoney(float billions)
    {
        var abs = MathF.Abs(billions);
        var sign = billions < -0.0001f ? "−" : "";
        if (abs >= 1000f) return ($"{sign}${(abs / 1000f):0.0}", Loc.Get("shell.unit_trillion"));
        if (abs >= 1f) return ($"{sign}${abs:0.00}", Loc.Get("shell.unit_billion"));
        return ($"{sign}${(abs * 1000f):0}", Loc.Get("shell.unit_million"));
    }

    private static (string Fig, string Unit) SplitPeople(long count)
    {
        if (count >= 1_000_000) return ($"{(count / 1_000_000.0):0.0}", Loc.Get("shell.unit_million"));
        if (count >= 1_000) return ($"{(count / 1_000.0):0.#}", Loc.Get("shell.unit_thousand"));
        return (count.ToString(CultureInfo.InvariantCulture), "");
    }

    private void DrawSeamlessLCorner()
    {
        var p = _profile;
        if (p.ViewportWidth <= 0f) return;
        var compact = p.Compact;
        var ox = p.TopPlate.X;
        var oy = p.TopPlate.Y;
        var w = p.TopPlate.Right;
        var h = p.TopPlate.Bottom;
        var rw = p.LeftRail.Right;
        var b = p.LeftRail.Bottom;
        var s = p.CornerNode.Right;
        var n = p.CornerNode.Bottom;
        var ch = compact ? 8f : 12f;
        var nc = compact ? 6f : 8f;

        Vector2[] poly =
        {
            new(ox, oy), new(w, oy), new(w, h - ch), new(w - ch, h), new(s, h),
            new(s, n - nc), new(s - nc, n), new(rw, n), new(rw, b - ch), new(rw - ch, b), new(ox, b)
        };

        var shadowPoly = new Vector2[poly.Length];
        for (var i = 0; i < poly.Length; i++) shadowPoly[i] = poly[i] + new Vector2(1.5f, 4f);
        _backdrop.DrawColoredPolygon(shadowPoly, Ui.Colour("shell.border", 0.52f));

        var topCol = Ui.Colour("shell.plate_top");
        var botCol = Ui.Colour("shell.plate_bottom");
        var colors = new Color[poly.Length];
        for (var i = 0; i < poly.Length; i++)
            colors[i] = topCol.Lerp(botCol, Math.Clamp(poly[i].Y / Math.Max(1f, b), 0f, 1f));
        _backdrop.DrawPolygon(poly, colors);

        _backdrop.DrawPolyline(CloseRing(poly), Ui.Colour("shell.border"), 1.2f, antialiased: true);
        _backdrop.DrawPolyline(CloseRing(InsetPolygon(poly, compact ? 1.8f : 2.2f)), Ui.Colour("shell.brass_hi"), compact ? 2.0f : 2.6f, antialiased: true);
        _backdrop.DrawPolyline(CloseRing(InsetPolygon(poly, compact ? 3.5f : 4.2f)), Ui.Colour("shell.paper", 0.14f), 1.0f, antialiased: true);
    }

    private static Vector2[] CloseRing(Vector2[] pts)
    {
        var res = new Vector2[pts.Length + 1];
        Array.Copy(pts, res, pts.Length);
        res[^1] = pts[0];
        return res;
    }

    private static Vector2[] InsetPolygon(Vector2[] pts, float dist)
    {
        var len = pts.Length;
        var res = new Vector2[len];
        for (var i = 0; i < len; i++)
        {
            var prev = pts[(i - 1 + len) % len];
            var cur = pts[i];
            var next = pts[(i + 1) % len];
            var e1 = (cur - prev).Normalized();
            var e2 = (next - cur).Normalized();
            var n1 = new Vector2(-e1.Y, e1.X);
            var n2 = new Vector2(-e2.Y, e2.X);
            var bis = n1 + n2;
            if (bis.LengthSquared() < 1e-4f) res[i] = cur + n1 * dist;
            else
            {
                bis = bis.Normalized();
                res[i] = cur + bis * (dist / Math.Max(0.35f, bis.Dot(n1)));
            }
        }
        return res;
    }
}
