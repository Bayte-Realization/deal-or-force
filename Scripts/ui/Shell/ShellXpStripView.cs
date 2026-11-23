using System;
using System.Globalization;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Recessed Military Experience &amp; Observer status ledge tucked cleanly beneath the Top Plate
/// (x = CornerNode.Right + 6 .. left of NewsCard, strictly above LeftPanel at both 1080p and 720p).
/// Surfaces Army XP, Air XP, and Navy XP with icons, Tabular() numerals, net daily gain, inline
/// progress toward the next doctrine article, 3-level tooltips, and click-through to Doctrines.
/// Also renders the crisp 3-state Observer Mode badge beside the strip when observing.
/// </summary>
internal sealed class ShellXpStripView
{
    private readonly ShellContext _ctx;

    private PanelContainer _strip;
    private readonly XpCellNodes[] _cells = new XpCellNodes[3];
    private Button _observerBadge;
    private TextureRect _observerIcon;
    private Label _observerLabel;

    private ShellLayoutProfile _profile;
    private bool _observerFitsInStrip = true;
    private StyleBoxEmpty _emptyStyle;
    private StyleBoxFlat _hoverStyle;
    private StyleBoxFlat _observerNormalStyle;
    private StyleBoxFlat _observerHoverStyle;
    private string _lastObsSig = "";
    private string _lastFullSig = "";

    private sealed class XpCellNodes
    {
        public string Id;
        public string Slot;
        public string FallbackScreen;
        public PanelContainer Box;
        public TextureRect Icon;
        public Label Value;
        public Label Rate;
        public Control ProgressBar;
        public Control SepGroove;
        public float FillRatio;
        public bool Ready;
        public string LastSig = "";
        public string LastTooltip = "";
    }

    public ShellXpStripView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build()
    {
        _emptyStyle ??= new StyleBoxEmpty();
        _hoverStyle ??= new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.08f) };

        _strip = new PanelContainer
        {
            Name = "ShellXpStrip",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _strip.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("well_recessed_9s", 10, 2, 0, 2, 0, "shell.well_top", "shell.border", 2));
        _ctx.Root.AddChild(_strip);
        _ctx.AttachPlateFrame(_strip, "shell.brass_dim", "shell.border_width", 1.1f, 1.0f, 3.0f);

        var row = new HBoxContainer { Name = "ShellXpRow", MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 0);
        _strip.AddChild(row);

        _cells[0] = BuildCell(row, "Army", DoctrineSlots.Ground, "Military", "icon_military", showSep: true);
        _cells[1] = BuildCell(row, "Air", DoctrineSlots.Air, "Support", "icon_air", showSep: true);
        _cells[2] = BuildCell(row, "Navy", DoctrineSlots.Sea, "Military", "icon_navy", showSep: false);

        BuildObserverBadge();
    }

    private XpCellNodes BuildCell(HBoxContainer parent, string id, string slot, string fallbackScreen, string iconName, bool showSep)
    {
        var box = new PanelContainer
        {
            Name = "ShellXp_" + id,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        box.AddThemeStyleboxOverride("panel", _emptyStyle);

        var cell = new XpCellNodes { Id = id, Slot = slot, FallbackScreen = fallbackScreen, Box = box };
        box.MouseEntered += () => box.AddThemeStyleboxOverride("panel", _hoverStyle);
        box.MouseExited += () => box.AddThemeStyleboxOverride("panel", _emptyStyle);
        box.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                _ctx.PlayCue?.Invoke("ui_select");
                OpenDoctrineOrFallback(fallbackScreen);
                box.AcceptEvent();
            }
        };

        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        box.AddChild(root);

        var line = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        line.OffsetLeft = 6f;
        line.OffsetRight = -6f;
        line.OffsetBottom = -4f;
        line.AddThemeConstantOverride("separation", 5);
        root.AddChild(line);

        var icon = new TextureRect
        {
            Texture = _ctx.Icon(iconName),
            CustomMinimumSize = new Vector2(18, 18),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        ShellContext.ApplyIconRelief(icon, Ui.Colour("shell.icon"), 0.55f);
        line.AddChild(icon);
        cell.Icon = icon;

        var val = new Label
        {
            Text = "0",
            CustomMinimumSize = new Vector2(30, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        val.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        val.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        line.AddChild(val);
        cell.Value = val;

        var rate = new Label
        {
            Text = "",
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        rate.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        rate.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        line.AddChild(rate);
        cell.Rate = rate;

        var bar = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AnchorLeft = 0f;
        bar.AnchorRight = 1f;
        bar.AnchorTop = 1f;
        bar.AnchorBottom = 1f;
        bar.OffsetLeft = 6f;
        bar.OffsetRight = -6f;
        bar.OffsetTop = -5f;
        bar.OffsetBottom = -2f;
        bar.Draw += () =>
        {
            var sz = bar.Size;
            if (sz.X <= 4f || sz.Y <= 0.5f) return;
            bar.DrawRect(new Rect2(0f, 0f, sz.X, sz.Y), Ui.Colour("shell.plate_top", 0.85f));
            var ratio = Math.Clamp(cell.FillRatio, 0f, 1f);
            if (ratio > 0.01f)
            {
                var fillCol = Ui.Colour(cell.Ready ? "shell.state.positive" : "shell.brass_hi");
                bar.DrawRect(new Rect2(0f, 0f, sz.X * ratio, sz.Y), fillCol);
            }
            if (cell.Ready)
                bar.DrawCircle(new Vector2(sz.X - 2f, -4f), 2.5f, Ui.Colour("shell.state.positive"));
        };
        root.AddChild(bar);
        cell.ProgressBar = bar;

        if (showSep)
        {
            var sep = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            sep.AnchorLeft = 1f;
            sep.AnchorRight = 1f;
            sep.AnchorTop = 0f;
            sep.AnchorBottom = 1f;
            sep.OffsetLeft = -2f;
            sep.OffsetRight = 0f;
            sep.Draw += () =>
            {
                var h = sep.Size.Y;
                if (h <= 8f) return;
                sep.DrawLine(new Vector2(0, 5), new Vector2(0, h - 5), Ui.Colour("shell.sep_dark"), 1f);
                sep.DrawLine(new Vector2(1, 5), new Vector2(1, h - 5), Ui.Colour("shell.sep_light"), 1f);
            };
            root.AddChild(sep);
            cell.SepGroove = sep;
        }

        parent.AddChild(box);
        return cell;
    }

    private void BuildObserverBadge()
    {
        _observerNormalStyle = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.plate_top"),
            BorderColor = Ui.Colour("shell.state.info"),
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 2,
            ContentMarginBottom = 2
        };
        _observerNormalStyle.SetBorderWidthAll(1);
        _observerNormalStyle.SetCornerRadiusAll(3);

        _observerHoverStyle = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.plate_hover"),
            BorderColor = Ui.Colour("shell.paper"),
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 2,
            ContentMarginBottom = 2
        };
        _observerHoverStyle.SetBorderWidthAll(1);
        _observerHoverStyle.SetCornerRadiusAll(3);

        _observerBadge = new Button
        {
            Name = "ShellObserverBadge",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        _observerBadge.AddThemeStyleboxOverride("normal", _observerNormalStyle);
        _observerBadge.AddThemeStyleboxOverride("hover", _observerHoverStyle);
        _observerBadge.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle(padX: 8, padY: 2));
        _observerBadge.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            {
                _ctx.PlayCue?.Invoke("ui_select");
                _ctx.WatchWorld?.Invoke();
                _observerBadge.AcceptEvent();
            }
        };
        _observerBadge.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            _ctx.ShowPauseMenu?.Invoke();
        };
        _ctx.Root.AddChild(_observerBadge);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 8f;
        row.OffsetRight = -8f;
        row.AddThemeConstantOverride("separation", 5);
        _observerBadge.AddChild(row);

        _observerIcon = new TextureRect
        {
            Texture = _ctx.Icon("icon_shell_eye"),
            CustomMinimumSize = new Vector2(15, 15),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.state.info"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddChild(_observerIcon);

        _observerLabel = new Label
        {
            Text = "",
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _observerLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        _observerLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.state.info"));
        row.AddChild(_observerLabel);
    }

    public void Layout(ShellLayoutProfile profile)
    {
        _profile = profile;
        if (_strip is null) return;
        var compact = profile.Compact;
        var rect = ResolveStripRect(profile);
        ShellContext.PlaceRect(_strip, rect);

        var iconSz = compact ? 14f : 18f;
        var valFont = compact ? 13 : 15;
        var rateFont = compact ? 10 : 11;
        for (var i = 0; i < _cells.Length; i++)
        {
            var c = _cells[i];
            c.Icon.CustomMinimumSize = new Vector2(iconSz, iconSz);
            c.Value.AddThemeFontSizeOverride("font_size", valFont);
            c.Rate.AddThemeFontSizeOverride("font_size", rateFont);
            c.Rate.Visible = !compact;
            c.ProgressBar.QueueRedraw();
            c.SepGroove?.QueueRedraw();
        }

        var obsX = rect.Right + (compact ? 4f : 6f);
        var obsMargin = compact ? 6f : 10f;
        var maxRight = profile.NewsCard.X - obsMargin;
        if (profile.TouchModeBar.W > 0f && profile.TouchModeBar.Bottom > rect.Y)
            maxRight = Math.Min(maxRight, profile.TouchModeBar.X - obsMargin);
        var obsAvail = maxRight - obsX;
        _observerFitsInStrip = obsAvail >= 60f;
        var obsW = _observerFitsInStrip ? MathF.Min(compact ? 94f : 212f, obsAvail) : 0f;
        ShellContext.PlaceRect(_observerBadge, new ShellRect(obsX, rect.Y, obsW, rect.H));
        if (!_observerFitsInStrip) _observerBadge.Visible = false;
        _observerIcon.CustomMinimumSize = new Vector2(compact ? 12 : 15, compact ? 12 : 15);
        _observerLabel.AddThemeFontSizeOverride("font_size", compact ? 10 : 12);
        _lastObsSig = "";
    }

    public void Refresh(Campaign world, Nation player)
    {
        if (_strip is null || world is null || player is null) return;
        var compact = _profile?.Compact ?? false;

        RefreshObserverBadge(world, player, compact);

        var fullSig = $"{player.Id}|{world.ElapsedDays}|{world.WarRevision}|{(int)MathF.Floor(player.ArmyExperience * 10f)}|" +
                      $"{(int)MathF.Floor(player.AirExperience * 10f)}|{(int)MathF.Floor(player.NavalExperience * 10f)}|{compact}|{Loc.Language}";
        if (_lastFullSig == fullSig) return;
        _lastFullSig = fullSig;

        var d = world.Defines;
        var strategy = StrategicDoctrines.Of(player);
        var peacetimeDay = d.ExperiencePeacetimePerWeek / 7f;
        var baseline = MathF.Max(0.0001f, d.BaselineMilitarySpending);
        var fundingDay = MathF.Max(0f, player.MilitarySpending / baseline - 1f) * d.ArmyExperienceFundingWeight / 7f;

        var engaged = 0;
        var drilling = 0;
        for (var i = 0; i < world.Armies.Count; i++)
        {
            var a = world.Armies[i];
            if (a.NationId != player.Id) continue;
            if (a.InBattle) engaged++;
            else if (a.Training) drilling++;
        }

        var flying = 0;
        for (var i = 0; i < world.SupportFormations.Count; i++)
        {
            var f = world.SupportFormations[i];
            if (f.NationId == player.Id && f.Strength > 0.05f &&
                !string.IsNullOrEmpty(f.Mission) && f.Mission != SupportCore.MissionStandby)
                flying++;
        }

        var atSea = 0;
        for (var i = 0; i < world.Fleets.Count; i++)
        {
            var fl = world.Fleets[i];
            if (fl.NationId == player.Id && fl.Hulls > 0 && (fl.InAction || fl.Mission != FleetMission.Anchored))
                atSea++;
        }

        var armyDaily = peacetimeDay + fundingDay +
            engaged * d.ArmyExperiencePerDivisionDay * (strategy?.BattleExperience ?? 1f) +
            drillsRate(drilling, d.TrainingExperiencePerDivisionDay, strategy?.DrillExperience ?? 1f);
        var airDaily = peacetimeDay + flying * d.AirExperiencePerMissionDay * (strategy?.AirExperience ?? 1f);
        var navyDaily = peacetimeDay + atSea * d.NavalExperiencePerHullDay * (strategy?.NavalExperience ?? 1f);

        UpdateXpCell(_cells[0], world, player, player.ArmyExperience, armyDaily,
            Loc.Get("shell.xp.army_title"),
            Loc.Format("shell.xp.army_sources_fmt", engaged, drilling),
            Loc.Get("shell.xp.army_unlocks"), compact);

        UpdateXpCell(_cells[1], world, player, player.AirExperience, airDaily,
            Loc.Get("shell.xp.air_title"),
            Loc.Format("shell.xp.air_sources_fmt", flying),
            Loc.Get("shell.xp.air_unlocks"), compact);

        UpdateXpCell(_cells[2], world, player, player.NavalExperience, navyDaily,
            Loc.Get("shell.xp.navy_title"),
            Loc.Format("shell.xp.navy_sources_fmt", atSea),
            Loc.Get("shell.xp.navy_unlocks"), compact);
    }

    private static float drillsRate(int drills, float perDay, float mult) => drills * perDay * mult;

    private void UpdateXpCell(
        XpCellNodes cell, Campaign world, Nation player, float current, float dailyGain,
        string title, string sourcesLine, string unlocksLine, bool compact)
    {
        var nextCost = NextArticlePrice(world, player, cell.Slot);
        var ready = nextCost > 0f && current >= nextCost;
        var ratio = nextCost > 0f ? Math.Clamp(current / nextCost, 0f, 1f) : (current > 0f ? 1f : 0f);

        var fig = Fmt.Number(current);
        var rateTxt = !compact && dailyGain >= 0.005f ? $"+{dailyGain.ToString("0.0#", CultureInfo.InvariantCulture)}" : "";
        var sig = $"{fig}|{rateTxt}|{ready}|{(int)(ratio * 100f)}";
        if (cell.LastSig != sig)
        {
            cell.LastSig = sig;
            cell.Value.Text = fig;
            cell.Value.AddThemeColorOverride("font_color", Ui.Colour(ready ? "shell.state.positive" : "shell.paper"));
            cell.Rate.Text = rateTxt;
            cell.Rate.Visible = rateTxt.Length > 0;
            cell.Rate.AddThemeColorOverride("font_color", Ui.Colour(dailyGain > 0.05f ? "shell.state.positive" : "shell.paper_faint"));
            cell.FillRatio = ratio;
            cell.Ready = ready;
            cell.ProgressBar.QueueRedraw();
        }

        var nextLine = nextCost <= 0f
            ? Loc.Get("shell.xp.no_doctrine")
            : ready
                ? Loc.Get("shell.xp.doctrine_ready")
                : Loc.Format("shell.xp.next_doctrine_fmt", Fmt.Number(nextCost));
        var rateFull = Loc.Format("shell.xp.daily_fmt",
            dailyGain.ToString("0.00", CultureInfo.InvariantCulture),
            (dailyGain * 7f).ToString("0.0", CultureInfo.InvariantCulture));
        var tip = $"{title}: {current.ToString("0.0", CultureInfo.InvariantCulture)}\n{rateFull}\n{sourcesLine}\n{nextLine}\n{unlocksLine}\n{Loc.Get("shell.xp.click_doctrines")}";
        if (cell.LastTooltip != tip)
        {
            cell.LastTooltip = tip;
            cell.Box.TooltipText = Ui.Tip(tip);
        }
    }

    private void RefreshObserverBadge(Campaign world, Nation player, bool compact)
    {
        if (_observerBadge is null) return;
        var observing = _ctx.IsObserverMode?.Invoke() ?? world.ObserverMode;
        if (!observing || !_observerFitsInStrip)
        {
            if (_observerBadge.Visible) _observerBadge.Visible = false;
            _lastObsSig = "off";
            return;
        }
        if (!_observerBadge.Visible) _observerBadge.Visible = true;

        var watchingWorld = _ctx.IsWatchingWorld?.Invoke() ?? false;
        var watched = _ctx.GetObservedNation?.Invoke();
        var note = _ctx.GetObserverTransientNote?.Invoke() ?? "";
        var sig = $"{watchingWorld}|{watched?.Id ?? -1}|{player.Id}|{compact}|{Loc.Language}|{note}";
        if (_lastObsSig == sig) return;
        _lastObsSig = sig;

        if (watchingWorld)
        {
            _observerLabel.Text = Loc.Get(compact ? "shell.obs.badge_world_compact" : "shell.obs.badge_world");
            var tip = Loc.Get("shell.obs.badge_tip_world");
            _observerBadge.TooltipText = Ui.Tip(string.IsNullOrEmpty(note) ? tip : $"{note}\n{tip}");
        }
        else
        {
            var subject = watched ?? player;
            var tagOrName = compact ? subject.Tag : subject.DisplayName;
            _observerLabel.Text = Loc.Format(compact ? "shell.obs.badge_country_compact_fmt" : "shell.obs.badge_country_fmt", tagOrName);
            var tip = Loc.Format("shell.obs.badge_tip_country_fmt", subject.DisplayName);
            _observerBadge.TooltipText = Ui.Tip(string.IsNullOrEmpty(note) ? tip : $"{note}\n{tip}");
        }
    }

    private void OpenDoctrineOrFallback(string fallbackScreen)
    {
        if (_ctx.World is { } w && w.FeatureEnabled("tactical_doctrines") && _ctx.OpenDoctrineScreen is not null)
        {
            _ctx.OpenDoctrineScreen();
            return;
        }
        _ctx.ToggleScreen?.Invoke(fallbackScreen);
    }

    private static float NextArticlePrice(Campaign world, Nation player, string slot)
    {
        if (!world.FeatureEnabled("tactical_doctrines")) return 0f;
        var book = TacticalDoctrines.BookOn(player, slot);
        var branch = book?.Id ?? TacticalDoctrines.PreferredBranch(world, player, slot);
        var price = 0f;
        foreach (var node in TacticalDoctrines.Available(player, branch))
            if (price == 0f || node.Cost < price) price = node.Cost;
        return price;
    }

    internal static ShellRect ResolveStripRect(ShellLayoutProfile profile)
    {
        var fallback = ShellLayout.XpStripRect(profile);
        var key = profile.Compact ? "shell.compact.xp_strip" : "shell.xp_strip";
        if (InterfaceSkin.HasLayout(key))
        {
            var r = ShellRect.FromOffsets(InterfaceSkin.Layout(key));
            if (r.W > 40f && r.H > 16f)
            {
                var compact = profile.Compact;
                var x = Math.Max(r.X + profile.TopPlate.X, profile.CornerNode.Right + (compact ? 4f : 6f));
                var y = Math.Max(r.Y + profile.TopPlate.Y, profile.TopPlate.Bottom + (compact ? 2f : 4f));
                var margin = compact ? 8f : 12f;
                var rightLimit = profile.NewsCard.X - margin;
                if (profile.TouchModeBar.W > 0f && profile.TouchModeBar.Bottom > y)
                    rightLimit = Math.Min(rightLimit, profile.TouchModeBar.X - margin);
                var w = rightLimit > x + 120f ? Math.Min(r.W, rightLimit - x) : Math.Min(r.W, fallback.W);
                return new ShellRect(x, y, w, r.H);
            }
        }
        return fallback;
    }
}
