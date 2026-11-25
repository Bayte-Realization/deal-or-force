using System;
using System.Collections.Generic;
using System.Globalization;
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
    private StyleBoxFlat _rankNormalStyle;
    private StyleBoxFlat _rankHoverStyle;
    private PanelContainer _rankHost;

    private ShellLayoutProfile _profile;
    private bool _atWar;
    private string _lastTag = "";
    private bool? _lastCountryOpen;
    private (Campaign World, int Week, int Rev, int WarRev, int PlayerId, string Lang, int Rank, double Score) _rankCache;
    private (Campaign World, int NationId, int SampleDay, float SampleGdp, float GrowthPct) _gdpSample;
    private (int PlayerId, float Treasury, float Balance, float Manpower, int Day, int ArmiesCount, bool Observer, string Language, string Screen)? _resourceReadout;
    private (int PlayerId, int Rank, int Prestige, int Day, string Lang) _flagTipStamp;
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
        public int LastDataSig;
        public bool HasDataSig;
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

        _rankNormalStyle = Ui.ChromeBox("shell.well", 0.72f, "shell.engrave", 0.78f, cornerRadius: 2, borderWidth: 1);
        _rankHoverStyle = Ui.ChromeBox("shell.plate_hover", 0.84f, "shell.brass_hi", 0.86f, cornerRadius: 2, borderWidth: 1);
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
        _rankSubLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
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
        AddStatCell(row, "Population", "Nation", "icon_population", "shell.aaa.cap_population", () => _ctx.ToggleScreen?.Invoke("Nation"));
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
        box.MouseEntered += () =>
        {
            if (cell.LastActive) return;
            box.AddThemeStyleboxOverride("panel", _cellHoverStyle);
            ShellContext.ApplyIconRelief(cell.Icon, Ui.Colour("shell.highlight"), 0.84f);
        };
        box.MouseExited += () =>
        {
            box.AddThemeStyleboxOverride("panel", cell.LastActive ? _ctx.PressedWellStyle(underlineBottom: true) : _emptyStyle);
            ShellContext.ApplyIconRelief(cell.Icon, Ui.Colour(cell.LastActive ? "shell.highlight" : "shell.icon"), cell.LastActive ? 0.88f : 0.62f);
        };
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
        cap.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
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
        var cellCount = atWar ? 9 : 8;
        var cellW = profile.StatCellWidth;
        var bedY = oy + (compact ? 4f : 6f);
        var bedH = profile.StatCellHeight;

        var tight = cellW < 76f;
        var narrow = cellW < 92f;
        var iconSz = tight ? 12f : (narrow ? 14f : (compact ? 18f : 24f));
        var ix = tight ? 3f : (narrow ? 4f : (compact ? 6f : 9f));
        var iy = tight ? 5f : (narrow ? 4f : (compact ? 5f : 6f));
        var vx = tight ? 17f : (narrow ? 20f : (compact ? 28f : 38f));
        var vy = compact ? 2f : 4f;
        var cy = compact ? 21f : 31f;

        var visibleOrder = new[] { "Treasury", "GDP", "Population", "Debt", "Inflation", "Stability", "Manpower", "Capital", "War" };
        for (var i = 0; i < visibleOrder.Length; i++)
        {
            var c = _cells[visibleOrder[i]];
            var isWar = c.Id == "War";
            c.Box.Visible = !isWar || atWar;
            c.Box.CustomMinimumSize = new Vector2(cellW, bedH);
            c.Icon.Position = new Vector2(ix, iy);
            c.Icon.Size = new Vector2(iconSz, iconSz);

            c.TopLine.Position = new Vector2(vx, vy);
            c.TopLine.Size = new Vector2(MathF.Max(16f, cellW - vx - (narrow ? 2f : 4f)), compact ? 20f : 26f);
            c.TopLine.AddThemeConstantOverride("separation", tight ? 1 : (narrow ? 2 : 3));
            c.Figure.AddThemeFontSizeOverride("font_size", tight ? 11 : (narrow ? 13 : (compact ? 15 : 19)));
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
            c.StatusRule.Position = new Vector2(6f, bedH - 2.5f);
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
        var hs = _ctx.HudSnapshot is { Version: > 0 } snap && snap.PlayerId == player.Id ? snap : null;

        bool atWar;
        if (hs is not null && hs.OpenPlayerWars.Length == 0)
        {
            _openWarsBuf.Clear();
            atWar = false;
        }
        else
        {
            _openWarsBuf.Clear();
            var allOpenWars = Warfare.OpenWars(world);
            for (var i = 0; i < allOpenWars.Count; i++)
            {
                var w = allOpenWars[i];
                if (!w.Ended && w.Involves(player.Id)) _openWarsBuf.Add(w);
            }
            atWar = _openWarsBuf.Count > 0;
        }
        if (atWar != _atWar)
            Layout(_ctx.ResolveViewportProfile(atWar), atWar);

        if (_lastTag != player.Tag)
        {
            _lastTag = player.Tag;
            FitNaturalFlag(player.Tag);
        }

        var openScreen = _ctx.GetOpenScreen?.Invoke() ?? "";
        var countryOpen = string.Equals(openScreen, "Nation", StringComparison.Ordinal);
        if (_rankActiveUnderline.Visible != countryOpen) _rankActiveUnderline.Visible = countryOpen;
        if (_flagButton.ButtonPressed != countryOpen) _flagButton.ButtonPressed = countryOpen;
        if (_lastCountryOpen != countryOpen)
        {
            _lastCountryOpen = countryOpen;
            _flagButton.AddThemeStyleboxOverride("normal", countryOpen ? _flagPocketActive : _flagPocketNormal);
        }

        var (rank, score, rankChanged) = ResolveOverallRank(world, player);
        var rankStr = rank > 0 ? $"№ {rank}" : "№ —";
        if (_rankNumLabel.Text != rankStr) _rankNumLabel.Text = rankStr;
        var prestigeVal = hs?.Prestige ?? player.Prestige;
        var dayVal = hs?.ElapsedDays ?? world.ElapsedDays;
        var tipStamp = (player.Id, rank, (int)MathF.Round(prestigeVal), dayVal, Loc.Language);
        if (rankChanged || !_flagTipStamp.Equals(tipStamp))
        {
            _flagTipStamp = tipStamp;
            _flagButton.TooltipText = Ui.Tip(ShellCornerTelemetry.BuildFlagTooltip(world, player, rank));
            var prestigeTip = Loc.Format("aaa.ui.shell_rank_tip_prestige_fmt", MathF.Round(prestigeVal).ToString("N0", Loc.Culture));
            _rankHost.TooltipText = Ui.Tip(rank > 0
                ? $"{Loc.Format("Overall rank #{0} · index {1}/100", rank, score.ToString("0.0"))}\n{prestigeTip}\n{Loc.Get("World power rank — click to open Country Rankings.")}"
                : $"{Loc.Get("Country rankings · no visible territorial statistics")}\n{prestigeTip}\n{Loc.Get("World power rank — click to open Country Rankings.")}");
        }

        RefreshCells(world, player, hs, _openWarsBuf, openScreen);
        _xpStrip.Refresh(world, player);
        _rail.Refresh(world, player, _openWarsBuf, openScreen);
        SyncKeycapVisibility();
    }

    private bool CellDirty(string id, int sig)
    {
        if (!_cells.TryGetValue(id, out var c) || (c.HasDataSig && c.LastDataSig == sig)) return false;
        c.HasDataSig = true; c.LastDataSig = sig;
        return true;
    }

    public void RefreshResources(Nation player)
    {
        if (player is null) return;
        var world = _ctx.World;
        var hs = _ctx.HudSnapshot is { Version: > 0 } snap && snap.PlayerId == player.Id ? snap : null;
        var openScreen = _ctx.GetOpenScreen?.Invoke() ?? "";
        var observer = _ctx.IsObserverMode?.Invoke() ?? (hs?.ObserverMode ?? (world?.ObserverMode ?? false));
        var trVal = hs?.Treasury ?? player.Treasury;
        var balVal = hs?.WeeklyBalance ?? player.WeeklyBalance;
        var manVal = hs?.ManpowerReserve ?? player.ManpowerReserve;
        var dayVal = hs?.ElapsedDays ?? (world?.ElapsedDays ?? -1);
        var armyCnt = hs?.PlayerTotalDivisions ?? (world?.Armies.Count ?? 0);
        var lang = Loc.Language;
        var readout = (player.Id, trVal, balVal, manVal, dayVal, armyCnt, observer, lang, openScreen);
        if (_resourceReadout is { } previous && previous.Equals(readout)) return;
        _resourceReadout = readout;

        if (CellDirty("Treasury", HashCode.Combine(trVal, balVal, openScreen == "Economy", lang)))
        {
            var (trFig, trUnit) = ShellCornerTelemetry.SplitMoney(trVal);
            var (balDelta, _) = ShellCornerTelemetry.SplitMoney(MathF.Abs(balVal));
            var trDeltaTxt = MathF.Abs(balVal) >= 0.0005f ? (balVal >= 0 ? "▲ " : "▼ ") + balDelta : "";
            UpdateCell("Treasury", trFig, trUnit, trVal < 0.05f ? "shell.state.negative_text" : "shell.paper",
                trDeltaTxt, balVal >= 0 ? "shell.state.positive" : "shell.state.negative",
                $"{Loc.Get("Treasury")}: {Fmt.MoneyExact(trVal)}\n{Loc.Get("Balance")}: {Fmt.MoneyDelta(balVal)}/{Loc.Get("week")}", openScreen == "Economy");
        }
        if (CellDirty("Manpower", HashCode.Combine(manVal, hs?.ReinforcementCapacity ?? player.ReinforcementCapacity, armyCnt, dayVal, openScreen == "Military", lang)))
        {
            var (manFig, manUnit) = ShellCornerTelemetry.SplitPeople((long)Math.Max(0, manVal));
            UpdateCell("Manpower", manFig, manUnit, "shell.paper", "", "shell.state.positive",
                ShellCornerTelemetry.BuildManpowerTooltip(world, player), openScreen == "Military");
        }
        if (world is not null) _xpStrip.Refresh(world, player);
    }

    private void RefreshCells(Campaign world, Nation player, CampaignHudSnapshot hs, IReadOnlyList<StrategicWar> openWars, string openScreen)
    {
        var debt = hs?.PublicDebt ?? SovereignFinance.Outstanding(player);
        var gdp = hs?.Gdp ?? player.Gdp;
        var debtRatio = debt / MathF.Max(0.01f, gdp);
        var overdue = hs is not null ? (hs.DebtArrears + hs.PaymentArrears) : (player.DebtArrears + player.PaymentArrears);
        var touch = _profile?.TouchMode == true || DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var lang = Loc.Language;

        var growthPct = ShellCornerTelemetry.SampleGdpDeltaPct(ref _gdpSample, world, player);
        if (CellDirty("GDP", HashCode.Combine(gdp, player.Population, growthPct, touch, openScreen == "Ledger", lang)))
        {
            var (gdpFig, gdpUnit) = ShellCornerTelemetry.SplitMoney(gdp);
            var gdpDelta = MathF.Abs(growthPct) >= 0.05f ? $"{(growthPct >= 0 ? "▲" : "▼")} {MathF.Abs(growthPct):0.0}%" : "";
            UpdateCell("GDP", gdpFig, gdpUnit, "shell.paper", gdpDelta, growthPct >= 0 ? "shell.state.positive" : "shell.state.negative",
                touch ? "" : ShellCornerTelemetry.BuildGdpTooltip(player, growthPct), openScreen == "Ledger");
        }

        var pop = hs?.Population ?? player.Population;
        var netPop = hs?.ReviewNetPop ?? (player.ReviewBirths - player.ReviewDeaths + player.ReviewNetMigration);
        var regionCount = hs?.RegionCount ?? world.StatesOf(player).Length;
        if (CellDirty("Population", HashCode.Combine(pop, netPop >= 100 ? 1 : (netPop <= -100 ? -1 : 0), regionCount, gdp, player.Literacy, player.StandardOfLiving, touch, HashCode.Combine(openScreen == "Nation", lang))))
        {
            var (popFig, popUnit) = ShellCornerTelemetry.SplitPeople(pop);
            var popDelta = Math.Abs(netPop) >= 100 ? (netPop > 0 ? "▲" : "▼") : "";
            UpdateCell("Population", popFig, popUnit, "shell.paper", popDelta, netPop >= 0 ? "shell.state.positive" : "shell.state.warning",
                touch ? "" : ShellCornerTelemetry.BuildPopulationTooltip(world, player, regionCount), openScreen == "Nation");
        }

        if (CellDirty("Debt", HashCode.Combine((int)MathF.Round(debtRatio * 1000f), overdue > 0.0001f, (int)MathF.Round(debt * 10f), lang)))
            UpdateCell("Debt", $"{(debtRatio * 100f):0.0}%", "",
                overdue > 0.0001f ? "shell.state.negative_text" : (debtRatio > 0.85f ? "shell.state.warning_text" : "shell.paper"),
                overdue > 0.0001f ? "●" : "", "shell.state.negative",
                $"{Loc.Get("Public debt")}: {Fmt.MoneyExact(debt)} ({Fmt.Percent(debtRatio)} {Loc.Get("of GDP")})", false);

        var inf = hs?.Inflation ?? player.Inflation;
        if (CellDirty("Inflation", HashCode.Combine((int)MathF.Round(inf * 1000f), touch ? 0 : ShellCornerTelemetry.BreakdownHash(player.InflationDrivers), touch, lang)))
        {
            var infDrivers = touch ? "" : ShellCornerTelemetry.FormatTopDrivers(player.InflationDrivers, asPercent: true);
            UpdateCell("Inflation", $"{(inf * 100f):0.0}%", "",
                inf > 0.12f ? "shell.state.negative_text" : (inf > 0.06f ? "shell.state.warning_text" : "shell.paper"),
                inf > 0.05f ? "▲" : "", "shell.state.warning", touch ? "" : $"{Loc.Get("Inflation")}: {Fmt.Percent(inf)}{infDrivers}", false);
        }

        var stab = hs?.Stability ?? player.Stability;
        if (CellDirty("Stability", HashCode.Combine((int)MathF.Round(stab * 1000f), touch ? 0 : ShellCornerTelemetry.BreakdownHash(player.StabilityDrivers), touch, openScreen == "Laws", lang)))
        {
            var stabDrivers = touch ? "" : ShellCornerTelemetry.FormatTopDrivers(player.StabilityDrivers, asPercent: true);
            UpdateCell("Stability", $"{(int)MathF.Round(stab * 100f)}%", "",
                stab < 0.35f ? "shell.state.negative_text" : (stab < 0.50f ? "shell.state.warning_text" : "shell.paper"),
                stab < 0.50f ? "▼" : "", "shell.state.negative", touch ? "" : $"{Loc.Get("Stability")}: {Fmt.Percent(stab)}{stabDrivers}", openScreen == "Laws");
        }

        var pp = hs?.PoliticalPoints ?? player.PoliticalPoints;
        var ppWeekly = hs?.PpWeekly ?? (world.Defines.PoliticalPointsPerWeek + world.Defines.PoliticalPointsStabilityWeight * Math.Clamp(stab, 0f, 1f));
        if (CellDirty("Capital", HashCode.Combine((int)MathF.Round(pp * 10f), (int)MathF.Round(ppWeekly * 10f), openScreen == "State Affairs", lang)))
        {
            var ppDeltaTxt = ppWeekly >= 0.05f ? $"+{ppWeekly.ToString("0.0", CultureInfo.InvariantCulture)}" : "";
            UpdateCell("Capital", ((int)MathF.Round(pp)).ToString(CultureInfo.InvariantCulture), "", "shell.paper", ppDeltaTxt, "shell.state.positive",
                $"{Loc.Get("Political capital")}: {pp:0.0}\n{Loc.Format("shell.xp.pol_rate_fmt", ppWeekly.ToString("0.0", CultureInfo.InvariantCulture))}\n{Loc.Get("shell.xp.pol_unlocks")}",
                openScreen == "State Affairs");
        }

        if (openWars.Count > 0)
        {
            var lead = openWars[0];
            var score = (int)MathF.Round(lead.IsAttacking(player.Id) ? lead.WarScore : -lead.WarScore);
            var wsPct = (int)MathF.Round((hs?.WarSupport ?? player.WarSupport) * 100f);
            if (CellDirty("War", HashCode.Combine(openWars.Count, score, wsPct, openScreen == "Wars", lang)))
            {
                if (_cells.TryGetValue("War", out var warCell) && warCell.Caption is not null)
                {
                    var capTxt = Loc.Format("shell.cap_wars_count", openWars.Count);
                    if (warCell.Caption.Text != capTxt) warCell.Caption.Text = capTxt;
                }
                UpdateCell("War", $"{wsPct}%", "", wsPct < 40 ? "shell.state.negative_text" : "shell.state.warning_text",
                    $"{score:+#;-#;0}%", score >= 0 ? "shell.state.positive" : "shell.state.negative",
                    $"{Loc.Get("Wars")}: {openWars.Count} · {Loc.Get("War score")}: {score:+#;-#;0}% · {Loc.Get("War support")}: {wsPct}%", openScreen == "Wars");
            }
        }
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
            ShellContext.ApplyIconRelief(c.Icon, Ui.Colour(active ? "shell.highlight" : "shell.icon"), active ? 0.88f : 0.62f);
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

        if (world.RenderSnapshot is { Version: > 0 } snap &&
            snap.RankWeek == week &&
            snap.RankMapRev == world.MapRevision &&
            snap.RankWarRev == world.WarRevision &&
            snap.RankPlayerId == player.Id &&
            snap.NationRanksById.Count > 0)
        {
            _rankCache = (world, week, world.MapRevision, world.WarRevision, player.Id, lang,
                snap.PlayerOverallRank, snap.PlayerOverallScore);
            return (snap.PlayerOverallRank, snap.PlayerOverallScore, true);
        }

        if (world.MidStep)
        {
            if (world.RenderSnapshot is { Version: > 0 } fallbackSnap &&
                fallbackSnap.RankPlayerId == player.Id &&
                fallbackSnap.PlayerOverallRank > 0)
                return (fallbackSnap.PlayerOverallRank, fallbackSnap.PlayerOverallScore, false);
            if (ReferenceEquals(_rankCache.World, world) && _rankCache.PlayerId == player.Id && _rankCache.Rank >= 0)
                return (_rankCache.Rank, _rankCache.Score, false);
            return (0, 0.0, false);
        }

        var weights = new CountryRankings.Weights(
            Ui.Metric("rankings.weight.economy", 42),
            Ui.Metric("rankings.weight.population", 14),
            Ui.Metric("rankings.weight.field_power", 12),
            Ui.Metric("rankings.weight.technology", 17),
            Ui.Metric("rankings.weight.prestige", 15));
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

    private void DrawSeamlessLCorner() => ShellCornerTelemetry.DrawSeamlessLCorner(_backdrop, _profile);
}
