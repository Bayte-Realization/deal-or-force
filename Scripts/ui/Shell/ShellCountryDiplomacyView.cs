using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Wave 2 Flag-First Diplomacy tab inside the Country Dossier (<see cref="ShellWorkPanelView"/>).
/// Owns padded scroll root (>= 14px scrollbar clearance), Bilateral Hero Card, Own Foreign Standing,
/// Active Wars cards, and delegates World Standing flag-only grids to
/// <see cref="ShellCountryDiplomacyStanding"/> and Direct Actions to
/// <see cref="ShellCountryDiplomacyActions"/>. Zero simulation mutations; zero raw Color literals.
/// </summary>
internal static class ShellCountryDiplomacyView
{
    private const int ScrollClearanceRight = 14;

    public static void Populate(
        ShellContext ctx,
        VBoxContainer bodyRows,
        Label feedbackLabel,
        Campaign world,
        Nation nation,
        bool isOwn,
        Action<Nation> openCountry,
        Action onMutated)
    {
        var pad = new MarginContainer
        {
            Name = "ShellDipPaddedRoot",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        pad.AddThemeConstantOverride("margin_left", 2);
        pad.AddThemeConstantOverride("margin_right", ScrollClearanceRight);
        pad.AddThemeConstantOverride("margin_top", 2);
        pad.AddThemeConstantOverride("margin_bottom", 6);
        bodyRows.AddChild(pad);

        var root = new VBoxContainer
        {
            Name = "ShellDipContent",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        root.AddThemeConstantOverride("separation", 7);
        pad.AddChild(root);

        var observing = ctx.IsObserverMode?.Invoke() ?? world.ObserverMode;
        if (observing)
            AddNoticeStrip(ctx, root, "icon_shell_eye", Loc.Get("shell.obs.readonly_banner"), "shell.state.info");

        if (isOwn)
            PopulateOwnSummary(ctx, root, world, nation, observing);
        else if (world.Player is { } player)
            PopulateBilateralCard(ctx, root, world, player, nation);

        PopulateWars(ctx, root, world, nation);
        ShellCountryDiplomacyStanding.PopulateStandoffs(ctx, root, world, nation, openCountry);
        ShellCountryDiplomacyStanding.PopulateWorldStanding(ctx, root, world, nation, openCountry);

        if (!isOwn && world.Player is { } actingPlayer)
            ShellCountryDiplomacyActions.Populate(ctx, root, feedbackLabel, world, actingPlayer, nation, observing, onMutated);
    }

    private static void PopulateOwnSummary(
        ShellContext ctx, VBoxContainer root, Campaign world, Nation nation, bool observing)
    {
        var allies = AllianceSystem.AlliesOf(world, nation).Count;
        var blocs = world.FeatureEnabled("organizations") ? InternationalOrganizations.Memberships(world, nation.Id).Count() : 0;
        var subjects = SubjectSystem.SubjectsOf(world, nation).Count;
        var missions = CountActiveMissions(world, nation.Id);

        var card = MakeSectionCard(ctx, root, "ShellDipOwnCard", "icon_diplomacy",
            Loc.Get("shell.dip.own_standing_header"), null, "shell.brass_hi");

        var grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        grid.AddThemeConstantOverride("h_separation", 4);
        card.AddChild(grid);
        AddMiniStatBox(ctx, grid, Loc.Get("shell.dip.allies"), allies.ToString(CultureInfo.InvariantCulture), "shell.state.positive");
        AddMiniStatBox(ctx, grid, Loc.Get("shell.dip.blocs"), blocs.ToString(CultureInfo.InvariantCulture), "shell.state.info");
        AddMiniStatBox(ctx, grid, Loc.Get("shell.dip.subjects"), subjects.ToString(CultureInfo.InvariantCulture), "shell.brass_hi");
        AddMiniStatBox(ctx, grid, Loc.Get("shell.dip.badge_envoy"), missions.ToString(CultureInfo.InvariantCulture), "shell.paper");

        if (!observing)
        {
            var inboxCount = ctx.GetIncomingDiplomaticCount?.Invoke() ?? 0;
            var inboxBtn = new Button
            {
                Name = "ShellDossierInboxButton",
                Text = inboxCount > 0 ? Loc.Format("Diplomatic inbox ({0})", inboxCount) : Loc.Get("Diplomatic inbox"),
                Icon = ctx.Icon("icon_shell_treaty"),
                ExpandIcon = true,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                CustomMinimumSize = new Vector2(0, 32),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = Ui.Tip(Loc.Get("shell.inbox_tooltip"))
            };
            var ctaStyle = ctx.NineSlice("button_brass_cta_9s", 10, 10, 4, 10, 4, "shell.plate_top", "shell.brass_hi", 3);
            inboxBtn.AddThemeStyleboxOverride("normal", ctaStyle);
            inboxBtn.AddThemeStyleboxOverride("hover", ctaStyle);
            inboxBtn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(padX: 10, padY: 4));
            inboxBtn.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
            inboxBtn.AddThemeFontSizeOverride("font_size", 13);
            inboxBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
            inboxBtn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); ctx.OpenDealInbox?.Invoke(); };
            card.AddChild(inboxBtn);
        }
    }

    private static void PopulateBilateralCard(
        ShellContext ctx, VBoxContainer root, Campaign world, Nation player, Nation nation)
    {
        var rel = CampaignDiplomacy.ReadOrNeutral(world, player.Id, nation.Id);
        var opinion = rel.Opinion;
        var opInt = (int)MathF.Round(opinion);
        var stanceText = DiplomaticVocabulary.StanceName(rel.Stance);
        var stanceTok = StanceColorToken(rel.Stance);

        var card = MakeSectionCard(ctx, root, "ShellDipBilateralCard", "icon_diplomacy",
            Loc.Get("shell.dip.bilateral_header"), opInt.ToString("+0;-0;0", CultureInfo.InvariantCulture), OpinionColorToken(opinion));

        var topRow = new HBoxContainer { ClipContents = true };
        topRow.AddThemeConstantOverride("separation", 6);
        card.AddChild(topRow);
        topRow.AddChild(MakeStatusPill(ctx, stanceText, stanceTok));
        topRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        topRow.AddChild(MakeLabel(ctx, Loc.Get("Opinion"), "shell.sans", 11, "shell.paper_faint"));
        topRow.AddChild(MakeLabel(ctx, opInt.ToString("+0;-0;0", CultureInfo.InvariantCulture),
            "shell.sans", 16, OpinionColorToken(opinion), tabular: true));

        var opBar = new Control
        {
            CustomMinimumSize = new Vector2(0, 10),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        var clampedOp = Math.Clamp(opinion / 100f, -1f, 1f);
        opBar.Draw += () =>
        {
            var sz = opBar.Size;
            if (sz.X <= 8f || sz.Y <= 2f) return;
            opBar.DrawRect(new Rect2(0, 2, sz.X, sz.Y - 4), Ui.Colour("shell.well"));
            var mid = sz.X * 0.5f;
            if (MathF.Abs(clampedOp) > 0.005f)
            {
                var w = MathF.Abs(clampedOp) * mid;
                var x = clampedOp >= 0f ? mid : mid - w;
                opBar.DrawRect(new Rect2(x, 2, w, sz.Y - 4),
                    Ui.Colour(clampedOp >= 0f ? "shell.state.positive" : "shell.state.negative"));
            }
            opBar.DrawLine(new Vector2(mid, 0), new Vector2(mid, sz.Y), Ui.Colour("shell.paper_muted"), 1.4f);
            opBar.DrawRect(new Rect2(0, 2, sz.X, sz.Y - 4), Ui.Colour("shell.engrave"), filled: false, width: 1f);
        };
        card.AddChild(opBar);

        var badges = CollectBilateralBadges(world, player, nation, rel);
        if (Deterrence.TierOf(world, nation) >= 1)
            badges.Add((Loc.Get("shell.dip.deterrent_notice"), "shell.state.warning_text"));
        if (DemographicExtinction.IsCollapsing(nation))
            badges.Add((Loc.Format("shell.dip.collapse_notice_fmt", nation.CollapseReviews), "shell.state.negative_text"));

        if (badges.Count > 0)
        {
            var flow = MakeChipFlow(card);
            foreach (var (text, tok) in badges)
                flow.AddChild(MakeStatusPill(ctx, text, tok));
        }

        // Inlaid Opinion Drivers well
        var drivers = ReadOpinionDrivers(world, player, nation, rel);
        var drvWell = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        drvWell.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.rule", 1, 2, 6, 4, 6, 4));
        card.AddChild(drvWell);

        var drvBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        drvBox.AddThemeConstantOverride("separation", 2);
        drvWell.AddChild(drvBox);

        if (drivers.Count > 0)
        {
            var fullTip = string.Join("\n", drivers.Select(d => $"{d.Value:+0;-0;0}  {d.Name}"));
            var drvHead = MakeLabel(ctx, Loc.Get("shell.dip.opinion_drivers"), "shell.sans", 10, "shell.paper_faint", clip: true);
            drvHead.MouseFilter = Control.MouseFilterEnum.Stop;
            drvHead.TooltipText = Ui.Tip(fullTip);
            drvBox.AddChild(drvHead);

            var maxAbs = MathF.Max(10f, drivers.Max(d => MathF.Abs(d.Value)));
            var shown = Math.Min(5, drivers.Count);
            for (var i = 0; i < shown; i++)
            {
                var d = drivers[i];
                var dRow = new HBoxContainer { ClipContents = true };
                dRow.AddThemeConstantOverride("separation", 5);
                dRow.AddChild(new ColorRect
                {
                    CustomMinimumSize = new Vector2(3, 12),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    Color = Ui.Colour(d.Value >= 0f ? "shell.state.positive" : "shell.state.negative"),
                    MouseFilter = Control.MouseFilterEnum.Ignore
                });
                dRow.AddChild(MakeLabel(ctx, d.Name, "shell.sans", 11, "shell.paper_muted", clip: true, expand: true));
                dRow.AddChild(MakeDriverBar(Math.Clamp(MathF.Abs(d.Value) / maxAbs, 0.1f, 1f),
                    d.Value >= 0f ? "shell.state.positive" : "shell.state.negative"));
                var v = MakeLabel(ctx, d.Value.ToString("+0;-0;0", CultureInfo.InvariantCulture),
                    "shell.sans", 11, d.Value >= 0f ? "shell.state.positive" : "shell.state.negative_text", tabular: true);
                v.CustomMinimumSize = new Vector2(30, 0);
                v.HorizontalAlignment = HorizontalAlignment.Right;
                dRow.AddChild(v);
                drvBox.AddChild(dRow);
            }
            if (drivers.Count > shown)
            {
                var more = MakeLabel(ctx, Loc.Format("shell.dip.more_drivers_fmt", drivers.Count - shown),
                    "shell.sans", 10, "shell.paper_faint", clip: true);
                more.MouseFilter = Control.MouseFilterEnum.Stop;
                more.TooltipText = Ui.Tip(fullTip);
                drvBox.AddChild(more);
            }
        }
        else
        {
            drvBox.AddChild(MakeLabel(ctx, Loc.Get("shell.dip.no_drivers"), "shell.sans", 11, "shell.paper_faint", clip: true));
        }

        var theirDivs = CountDivisions(world, nation.Id);
        var ourDivs = CountDivisions(world, player.Id);
        var cmpGrid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        cmpGrid.AddThemeConstantOverride("h_separation", 4);
        card.AddChild(cmpGrid);

        AddCompareCell(ctx, cmpGrid, Loc.Get("GDP"), Fmt.Money(nation.Gdp), nation.Gdp, player.Gdp);
        AddCompareCell(ctx, cmpGrid, Loc.Get("Army"), $"{theirDivs} {Loc.Get("shell.div_short")}", theirDivs, ourDivs);
        AddCompareCell(ctx, cmpGrid, Loc.Get("Population"), Fmt.People(nation.Population), nation.Population, player.Population);
    }

    private static void PopulateWars(ShellContext ctx, VBoxContainer root, Campaign world, Nation nation)
    {
        var wars = Warfare.OpenWars(world).Where(w => !w.Ended && w.Involves(nation.Id)).ToList();
        var warCard = MakeSectionCard(ctx, root, "ShellDipWarsCard", "icon_wars",
            Loc.Get("shell.dip.wars_header"),
            wars.Count == 0 ? Loc.Get("shell.dip.at_peace") : wars.Count.ToString(CultureInfo.InvariantCulture),
            wars.Count > 0 ? "shell.state.negative_text" : "shell.state.positive");

        if (wars.Count == 0)
        {
            var peaceRow = new HBoxContainer { ClipContents = true };
            peaceRow.AddChild(MakeStatusPill(ctx, Loc.Get("shell.dip.at_peace"), "shell.state.positive"));
            warCard.AddChild(peaceRow);
            return;
        }

        for (var i = 0; i < wars.Count; i++)
        {
            var war = wars[i];
            var warId = war.Id;
            var score = war.IsAttacking(nation.Id) ? war.WarScore : -war.WarScore;
            var scoreInt = (int)MathF.Round(score);
            var enemies = war.EnemiesOf(nation.Id).Select(world.NationById).Where(n => n is not null).ToList();

            var warBtn = new Button
            {
                Name = "ShellDipWar_" + warId,
                CustomMinimumSize = new Vector2(0, 50),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipContents = true,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = Ui.Tip($"{WarNames.Of(world, war)}\n{Loc.Get("shell.dip.open_war_tip")}")
            };
            warBtn.AddThemeStyleboxOverride("normal", ctx.BorderedBox("shell.well_bottom", "shell.state.negative", 1, 3, 6, 4, 6, 4));
            warBtn.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.state.negative_text", 1, 3, 6, 4, 6, 4));
            warBtn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(padX: 6, padY: 4));
            warBtn.Pressed += () =>
            {
                ctx.PlayCue?.Invoke("ui_select");
                ctx.OpenWarLedger?.Invoke();
                ctx.ShowWarDetail?.Invoke(warId);
            };
            warCard.AddChild(warBtn);

            var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            v.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            v.OffsetLeft = 7; v.OffsetRight = -7; v.OffsetTop = 4; v.OffsetBottom = -4;
            v.AddThemeConstantOverride("separation", 3);
            warBtn.AddChild(v);

            var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            top.AddThemeConstantOverride("separation", 4);
            v.AddChild(top);

            var maxFlags = Math.Min(4, enemies.Count);
            for (var e = 0; e < maxFlags; e++)
            {
                if (ctx.FlagTexture(enemies[e].Tag) is { } ef)
                {
                    top.AddChild(new TextureRect
                    {
                        Material = FlagClothPresentation.Material,
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                        CustomMinimumSize = new Vector2(20, 13),
                        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                        Texture = ef,
                        MouseFilter = Control.MouseFilterEnum.Ignore
                    });
                }
            }
            if (enemies.Count > maxFlags)
                top.AddChild(MakeLabel(ctx, $"+{enemies.Count - maxFlags}", "shell.sans", 10, "shell.paper_faint", tabular: true));

            var warTitle = enemies.Count == 1 ? enemies[0].DisplayName : WarNames.Of(world, war);
            top.AddChild(MakeLabel(ctx, warTitle, "shell.sans", 11, "shell.paper", clip: true, expand: true));
            top.AddChild(MakeLabel(ctx, $"{scoreInt:+#;-#;0}%", "shell.sans", 12,
                scoreInt > 0 ? "shell.state.positive" : (scoreInt < 0 ? "shell.state.negative_text" : "shell.paper_muted"),
                tabular: true));

            var bar = new Control
            {
                CustomMinimumSize = new Vector2(0, 6),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            var frac = Math.Clamp(score / 100f, -1f, 1f);
            bar.Draw += () =>
            {
                var sz = bar.Size;
                if (sz.X <= 4f) return;
                bar.DrawRect(new Rect2(0, 0, sz.X, sz.Y), Ui.Colour("shell.plate_bottom"));
                var mid = sz.X * 0.5f;
                var w = MathF.Abs(frac) * mid;
                if (w > 0.5f)
                    bar.DrawRect(new Rect2(frac >= 0f ? mid : mid - w, 0, w, sz.Y),
                        Ui.Colour(frac >= 0f ? "shell.state.positive" : "shell.state.negative"));
                bar.DrawLine(new Vector2(mid, 0), new Vector2(mid, sz.Y), Ui.Colour("shell.paper_muted"), 1.2f);
            };
            v.AddChild(bar);
        }
    }

    internal static VBoxContainer MakeSectionCard(
        ShellContext ctx, VBoxContainer root, string name, string iconName, string title, string countText, string accentTok)
    {
        var card = new PanelContainer { Name = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        card.AddThemeStyleboxOverride("panel", ChromePlateArt.BuildCardWrapperStyle(8, 6, 8, 6) ??
            ctx.BorderedBox("shell.plate_bottom", "shell.engrave", 1, 3, 8, 6, 8, 6));
        root.AddChild(card);

        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        col.AddThemeConstantOverride("separation", 5);
        card.AddChild(col);

        var head = new HBoxContainer { ClipContents = true };
        head.AddThemeConstantOverride("separation", 6);
        col.AddChild(head);

        if (ctx.Icon(iconName) is { } ico)
        {
            var tr = new TextureRect
            {
                Texture = ico, CustomMinimumSize = new Vector2(14, 14),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            ShellContext.ApplyIconRelief(tr, Ui.Colour("shell.icon"), 0.55f);
            head.AddChild(tr);
        }
        head.AddChild(MakeLabel(ctx, title, "shell.serif_semibold", 12, "shell.paper", clip: true, expand: true));
        if (!string.IsNullOrEmpty(countText))
        {
            var pill = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            pill.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well", "shell.engrave", 1, 2, 6, 1, 6, 1));
            pill.AddChild(MakeLabel(ctx, countText, "shell.sans", 10, accentTok, tabular: true));
            head.AddChild(pill);
        }
        col.AddChild(ShellContext.CreateRuledLine(title.Length * 13 + 7, "shell.rule", 1.0f, 3f));
        return col;
    }

    internal static HFlowContainer MakeChipFlow(VBoxContainer parent)
    {
        var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        flow.AddThemeConstantOverride("h_separation", 4);
        flow.AddThemeConstantOverride("v_separation", 4);
        parent.AddChild(flow);
        return flow;
    }

    internal static Control MakeStatusPill(ShellContext ctx, string text, string colorTok)
    {
        var pill = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass, ClipContents = true };
        pill.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", colorTok, 1, 2, 6, 2, 6, 2));
        pill.AddChild(MakeLabel(ctx, text, "shell.sans", 10, colorTok));
        return pill;
    }

    internal static void AddNoticeStrip(ShellContext ctx, VBoxContainer parent, string iconName, string text, string colorTok)
    {
        var box = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        box.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", colorTok, 1, 2, 8, 4, 8, 4));
        parent.AddChild(box);
        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 6);
        box.AddChild(row);
        if (ctx.Icon(iconName) is { } ico)
        {
            row.AddChild(new TextureRect
            {
                Texture = ico, CustomMinimumSize = new Vector2(14, 14),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                Modulate = Ui.Colour(colorTok)
            });
        }
        var lbl = MakeLabel(ctx, text, "shell.sans", 11, colorTok, expand: true);
        lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(lbl);
    }

    internal static Label MakeLabel(
        ShellContext ctx, string text, string fontKey, int size, string colorTok,
        bool clip = false, bool expand = false, bool tabular = false)
    {
        var lbl = new Label
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off, ClipText = clip
        };
        if (expand) lbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (clip) lbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        lbl.AddThemeFontOverride("font", ctx.Font(fontKey, tabular));
        lbl.AddThemeFontSizeOverride("font_size", size);
        lbl.AddThemeColorOverride("font_color", Ui.Colour(colorTok));
        return lbl;
    }

    private static Control MakeDriverBar(float frac01, string colorTok)
    {
        var bar = new Control
        {
            CustomMinimumSize = new Vector2(32, 6),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        bar.Draw += () =>
        {
            var sz = bar.Size;
            bar.DrawRect(new Rect2(0, 0, sz.X, sz.Y), Ui.Colour("shell.plate_bottom"));
            bar.DrawRect(new Rect2(0, 0, sz.X * frac01, sz.Y), Ui.Colour(colorTok, 0.85f));
        };
        return bar;
    }

    private static void AddMiniStatBox(ShellContext ctx, GridContainer grid, string caption, string val, string valTok)
    {
        var box = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        box.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 4, 3, 4, 3));
        grid.AddChild(box);
        var v = new VBoxContainer { ClipContents = true };
        v.AddThemeConstantOverride("separation", 1);
        box.AddChild(v);
        var lVal = MakeLabel(ctx, val, "shell.sans", 13, valTok, clip: true, tabular: true);
        lVal.HorizontalAlignment = HorizontalAlignment.Center;
        var lCap = MakeLabel(ctx, caption, "shell.sans", 9, "shell.paper_faint", clip: true);
        lCap.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(lVal);
        v.AddChild(lCap);
    }

    private static void AddCompareCell(ShellContext ctx, GridContainer grid, string caption, string val, double theirs, double ours)
    {
        var cell = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        cell.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 5, 3, 5, 3));
        grid.AddChild(cell);

        var ratioStr = ours <= 0.0001 ? "—" : (theirs / ours >= 10.0 ? $"{theirs / ours:0}×" : $"{theirs / ours:0.0}×");
        var ratioTok = ours <= 0.0001 ? "shell.paper_muted"
            : theirs / ours >= 1.35 ? "shell.state.warning_text"
            : theirs / ours <= 0.70 ? "shell.state.positive" : "shell.paper_muted";

        var v = new VBoxContainer { ClipContents = true };
        v.AddThemeConstantOverride("separation", 1);
        cell.AddChild(v);
        v.AddChild(MakeLabel(ctx, caption, "shell.sans", 10, "shell.paper_faint", clip: true));
        v.AddChild(MakeLabel(ctx, val, "shell.sans", 11, "shell.paper", clip: true, tabular: true));
        v.AddChild(MakeLabel(ctx, ratioStr, "shell.sans", 10, ratioTok, clip: true, tabular: true));
    }

    private static List<(string Text, string ColorTok)> CollectBilateralBadges(
        Campaign world, Nation player, Nation nation, DiplomaticRelation rel)
    {
        var list = new List<(string, string)>();
        if (nation.AnswersTo(player.Id)) list.Add((Loc.Get("shell.dip.badge_subject"), "shell.state.positive"));
        if (player.AnswersTo(nation.Id)) list.Add((Loc.Get("shell.dip.badge_overlord"), "shell.state.warning_text"));
        if (rel.TruceUntil > world.ElapsedDays)
            list.Add((Loc.Format("shell.dip.badge_truce_fmt", rel.TruceUntil - world.ElapsedDays), "shell.paper_muted"));
        if (rel.TradeAgreement) list.Add((Loc.Get("shell.dip.badge_trade"), "shell.state.info"));
        if (rel.NonAggressionPact) list.Add((Loc.Get("shell.dip.badge_nap"), "shell.paper_muted"));
        if (rel.GuaranteedBy(player.Id)) list.Add((Loc.Get("shell.dip.badge_guar_us"), "shell.state.positive"));
        if (rel.GuaranteedBy(nation.Id)) list.Add((Loc.Get("shell.dip.badge_guar_them"), "shell.state.info"));
        if (rel.GrantsMilitaryAccess(player.Id)) list.Add((Loc.Get("shell.dip.badge_access_us"), "shell.paper_muted"));
        if (rel.GrantsMilitaryAccess(nation.Id)) list.Add((Loc.Get("shell.dip.badge_access_them"), "shell.paper_muted"));
        if (rel.EmbargoBy(player.Id)) list.Add((Loc.Get("shell.dip.badge_embargo_us"), "shell.state.warning_text"));
        if (rel.EmbargoBy(nation.Id)) list.Add((Loc.Get("shell.dip.badge_embargo_them"), "shell.state.negative_text"));
        if (DiplomaticOutreach.Of(rel, player.Id) is not null) list.Add((Loc.Get("shell.dip.badge_envoy"), "shell.state.positive"));
        return list;
    }

    private static List<(string Name, float Value)> ReadOpinionDrivers(
        Campaign world, Nation player, Nation nation, DiplomaticRelation rel)
    {
        var list = new List<(string Name, float Value)>();
        if (rel.OpinionDrivers is { HasEntries: true })
        {
            for (var i = 0; i < rel.OpinionDrivers.Entries.Count; i++)
            {
                var e = rel.OpinionDrivers.Entries[i];
                if (MathF.Abs(e.Value) >= 0.05f) list.Add((Loc.Get(e.Source), e.Value));
            }
        }
        else
        {
            for (var i = 0; i < rel.Reasons.Count; i++)
            {
                var r = rel.Reasons[i];
                var def = OpinionCatalog.Find(r.Id);
                if (def is null) continue;
                var w = OpinionSystem.Weight(world, r);
                if (MathF.Abs(w) < 0.05f) continue;
                var nm = Loc.Get(def.Value.Name ?? r.Id);
                list.Add((r.Stacks > 1 ? $"{nm} (×{r.Stacks})" : nm, w));
            }
            var aff = PoliticsCore.Affinity(world, player, nation);
            if (MathF.Abs(aff) >= 0.05f) list.Add((Loc.Get("Political affinity"), aff));
        }
        list.Sort((a, b) => MathF.Abs(b.Value).CompareTo(MathF.Abs(a.Value)));
        return list;
    }

    private static int CountDivisions(Campaign world, int nationId)
    {
        var sum = 0f;
        for (var i = 0; i < world.Armies.Count; i++)
            if (world.Armies[i].NationId == nationId) sum += world.Armies[i].Divisions;
        return (int)MathF.Ceiling(sum);
    }

    private static int CountActiveMissions(Campaign world, int nationId)
    {
        var count = 0;
        foreach (var r in world.Relations.Values)
            if (DiplomaticOutreach.Of(r, nationId) is not null) count++;
        return count;
    }

    private static string OpinionColorToken(float op) =>
        op >= 25f ? "shell.state.positive" : op <= -25f ? "shell.state.negative_text" : "shell.paper";

    private static string StanceColorToken(DiplomaticStance stance) => stance switch
    {
        DiplomaticStance.Allied => "shell.state.positive",
        DiplomaticStance.Friendly => "shell.state.info",
        DiplomaticStance.Rival => "shell.state.warning_text",
        DiplomaticStance.AtWar => "shell.state.negative_text",
        DiplomaticStance.Truce => "shell.paper_muted",
        _ => "shell.paper_faint"
    };
}
