using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Renders visual-first Country Dossier tabs (Overview, Society, Decisions, Diplomacy)
/// and sculpted ruler cameo cards inside <see cref="ShellWorkPanelView"/>.
/// </summary>
internal static class ShellCountryDossierView
{
    public static void PopulateRulerCard(
        ShellContext ctx,
        VBoxContainer parent,
        Campaign world,
        Nation nation)
    {
        var ruler = Rulers.HeadOfState(world, nation);
        var gov = Rulers.HeadOfGovernment(world, nation);

        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", ChromePlateArt.BuildCardWrapperStyle(8, 7, 8, 7) ?? ctx.BorderedBox(
            "shell.plate_bottom", "shell.engrave", 1, 3, 8, 7, 8, 7));
        parent.AddChild(card);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        card.AddChild(row);

        var portrait = ctx.BuildLeaderPortrait?.Invoke(ruler, nation.Color, 54, 66);
        if (portrait is not null)
        {
            portrait.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(portrait);
        }

        var col = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        col.AddThemeConstantOverride("separation", 2);
        row.AddChild(col);

        var titleText = string.IsNullOrWhiteSpace(ruler.Title) ? Loc.Get("Head of state") : Loc.Get(ruler.Title);
        var metaTop = ruler.Age > 0
            ? $"{titleText} · {Loc.Format("shell.ruler_age_fmt", ruler.Age)}"
            : titleText;
        col.AddChild(MakeLabel(ctx, metaTop, "shell.sans", 11, "shell.paper_faint", clip: true));

        var nameText = string.IsNullOrWhiteSpace(ruler.Name) ? "—" : ruler.Name;
        col.AddChild(MakeLabel(ctx, nameText, "shell.serif_semibold", 16, "shell.paper", clip: true));

        var ideology = Loc.Get(PoliticsCore.IdeologyOf(nation).Name);
        var trait = string.IsNullOrWhiteSpace(ruler.TraitName) ? "" : $" · {Loc.Get(ruler.TraitName)}";
        col.AddChild(MakeLabel(ctx, ideology + trait, "shell.sans", 11, "shell.paper_muted", clip: true));

        if (!string.IsNullOrWhiteSpace(gov.Name) &&
            !string.Equals(gov.Name, ruler.Name, StringComparison.OrdinalIgnoreCase))
        {
            var govLine = $"{Loc.Get("Head of government")}: {gov.Name}";
            col.AddChild(MakeLabel(ctx, govLine, "shell.sans", 11, "shell.paper_faint", clip: true));
        }
    }

    public static void PopulateOverview(
        ShellContext ctx,
        VBoxContainer bodyRows,
        Campaign world,
        Nation nation,
        bool isOwn,
        Action<Nation> openCountry,
        Action openStaffTab = null)
    {
        PopulateRulerCard(ctx, bodyRows, world, nation);

        ShellRegionVisualsView.AddSectionHeader(ctx, bodyRows, Loc.Get("shell.standing_kpis"));
        AddStandingGrid(ctx, bodyRows, world, nation, isOwn);

        if (isOwn && openStaffTab is not null)
            AddStaffQuickLink(ctx, bodyRows, nation, openStaffTab);

        ShellRegionVisualsView.AddSectionHeader(ctx, bodyRows, Loc.Get("Neighbours"), Loc.Get("GDP"));
        var neighbors = world.Nations
            .Where(n => n.Population > 0 && n.Id != nation.Id)
            .OrderByDescending(n => n.Region == nation.Region ? 1 : 0)
            .ThenByDescending(n => n.Gdp)
            .Take(6)
            .ToArray();

        foreach (var nb in neighbors)
        {
            var captured = nb;
            var rel = (int)MathF.Round(CampaignDiplomacy.ReadOrNeutral(world, nation.Id, nb.Id).Opinion);
            AddCountryComparisonRow(ctx, bodyRows, nb, Fmt.Money(nb.Gdp), $"{rel:+#;-#;0}",
                rel < -15 ? "shell.state.negative_text" : (rel > 20 ? "shell.state.positive" : "shell.paper_muted"),
                () => openCountry(captured));
        }
    }

    public static void PopulateSociety(
        ShellContext ctx,
        VBoxContainer bodyRows,
        Campaign world,
        Nation nation)
    {
        PopulateRulerCard(ctx, bodyRows, world, nation);

        var states = world.StatesOf(nation);
        var totalRad = 0f;
        var stCount = 0;
        foreach (var s in states)
        {
            totalRad += s.Radicalism;
            stCount++;
        }
        var avgRadicalism = stCount > 0 ? totalRad / stCount : 0f;

        ShellRegionVisualsView.AddSectionHeader(ctx, bodyRows, Loc.Get("shell.society_profile"));
        AddSocietyMeter(ctx, bodyRows, "icon_stability", Loc.Get("Stability"), nation.Stability, Fmt.Percent(nation.Stability),
            StabilityColor(nation.Stability));
        AddSocietyMeter(ctx, bodyRows, "icon_living", Loc.Get("Standard of living"), nation.StandardOfLiving, Fmt.Percent(nation.StandardOfLiving),
            GoodHighColor(nation.StandardOfLiving));
        AddSocietyMeter(ctx, bodyRows, "icon_urbanisation", Loc.Get("Urbanisation"), nation.Urbanisation, Fmt.Percent(nation.Urbanisation),
            Ui.Colour("shell.state.info"));
        AddSocietyMeter(ctx, bodyRows, "icon_literacy", Loc.Get("Literacy"), nation.Literacy, Fmt.Percent(nation.Literacy),
            Ui.Colour("shell.brass_hi"));
        AddSocietyMeter(ctx, bodyRows, "icon_unrest", Loc.Get("Radicalism"), avgRadicalism, Fmt.Percent(avgRadicalism),
            DangerHighColor(avgRadicalism));
        AddSocietyMeter(ctx, bodyRows, "icon_welfare", Loc.Get("War support"), nation.WarSupport, Fmt.Percent(nation.WarSupport),
            GoodHighColor(nation.WarSupport));

        ShellRegionVisualsView.AddSectionHeader(ctx, bodyRows, Loc.Get("shell.leader_cabinet"));
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 4);
        bodyRows.AddChild(grid);

        AddPillCard(ctx, grid, "icon_government", Loc.Get("shell.government_form"), Loc.Get(PoliticsCore.GovernmentOf(nation).Name));
        AddPillCard(ctx, grid, "icon_laws", Loc.Get("shell.national_ideology"), Loc.Get(PoliticsCore.IdeologyOf(nation).Name));

        var cultureId = DemographyCatalog.PrimaryCulture(nation.Tag);
        var primaryCulture = !string.IsNullOrEmpty(cultureId) ? DemographyCatalog.CultureName(cultureId) : "—";
        AddPillCard(ctx, grid, "icon_nation", Loc.Get("shell.dominant_culture"), primaryCulture);

        var faiths = !string.IsNullOrEmpty(cultureId) ? DemographyCatalog.FaithOf(cultureId) : null;
        var faithId = faiths is { Count: > 0 } ? faiths[0].Religion : null;
        var primaryFaith = !string.IsNullOrEmpty(faithId) ? DemographyCatalog.ReligionName(faithId) : "—";
        AddPillCard(ctx, grid, "icon_faith", Loc.Get("shell.dominant_faith"), primaryFaith);
    }

    public static void PopulateDecisions(
        ShellContext ctx,
        VBoxContainer bodyRows,
        Campaign world,
        Nation nation)
    {
        ShellRegionVisualsView.AddSectionHeader(ctx, bodyRows, Loc.Get("Decisions"));
        AddKvRow(ctx, bodyRows, Loc.Get("Political capital"), $"{nation.PoliticalPoints:0.0}", "shell.paper");
        var note = MakeLabel(ctx, Loc.Get("shell.open_decisions_hint"), "shell.sans", 12, "shell.paper_muted");
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        bodyRows.AddChild(note);
    }

    public static void PopulateDiplomacy(
        ShellContext ctx,
        VBoxContainer bodyRows,
        Campaign world,
        Nation nation,
        bool isOwn)
    {
        ShellRegionVisualsView.AddSectionHeader(ctx, bodyRows, Loc.Get("Diplomacy"));
        var wars = Warfare.OpenWars(world).Where(w => !w.Ended && w.Involves(nation.Id)).ToArray();
        AddKvRow(ctx, bodyRows, Loc.Get("Wars"), wars.Length.ToString(CultureInfo.InvariantCulture),
            wars.Length > 0 ? "shell.state.negative_text" : "shell.paper");
        if (isOwn)
        {
            var inboxCount = ctx.GetIncomingDiplomaticCount?.Invoke() ?? 0;
            AddKvRow(ctx, bodyRows, Loc.Get("Diplomatic inbox"), inboxCount.ToString(CultureInfo.InvariantCulture),
                inboxCount > 0 ? "shell.state.warning_text" : "shell.paper");
            var inboxBtn = new Button
            {
                Name = "ShellDossierInboxButton",
                Text = inboxCount > 0
                    ? Loc.Format("Diplomatic inbox ({0})", inboxCount)
                    : Loc.Get("Diplomatic inbox"),
                Icon = ctx.Icon("icon_shell_treaty"),
                ExpandIcon = true,
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
            bodyRows.AddChild(inboxBtn);
        }
        else if (world.Player is { } pl)
        {
            var op = (int)MathF.Round(CampaignDiplomacy.ReadOrNeutral(world, pl.Id, nation.Id).Opinion);
            AddKvRow(ctx, bodyRows, Loc.Get("Opinion"), $"{op:+#;-#;0}",
                op < 0 ? "shell.state.negative_text" : "shell.state.positive");
        }
    }

    public static void PopulateStaff(
        ShellContext ctx,
        VBoxContainer bodyRows,
        Campaign world,
        Nation nation,
        Action onChanged)
    {
        var items = DealOrForceGame.StaffPortfolios;
        var active = items.Count(entry => Delegation.Holds(nation, entry.Portfolio));
        var allHeld = active == items.Length;

        ShellRegionVisualsView.AddSectionHeader(ctx, bodyRows, Loc.Get("THE STAFF"), $"{active}/{items.Length}");

        var hint = MakeLabel(ctx, Loc.Get("shell.staff_subtitle"), "shell.sans", 11, "shell.paper_muted");
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        bodyRows.AddChild(hint);

        var master = new Button
        {
            Name = "ShellStaffToggleAll",
            Text = Loc.Get(allHeld ? "Take everything back" : "Hand everything to the staff"),
            CustomMinimumSize = new Vector2(0, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get("Every portfolio below at once. Each can still be taken back on its own."))
        };
        var masterStyle = allHeld
            ? ctx.NineSlice("button_umber_9s", 10, 10, 4, 10, 4, "shell.plate_bottom", "shell.engrave", 3)
            : ctx.NineSlice("button_brass_cta_9s", 10, 10, 4, 10, 4, "shell.plate_top", "shell.brass_hi", 3);
        master.AddThemeStyleboxOverride("normal", masterStyle);
        master.AddThemeStyleboxOverride("hover", masterStyle);
        master.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(padX: 10, padY: 4));
        master.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
        master.AddThemeFontSizeOverride("font_size", 13);
        master.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        master.Pressed += () =>
        {
            ctx.PlayCue?.Invoke("ui_select");
            foreach (var (portfolio, _, _, _) in items)
                ctx.DelegatePortfolio?.Invoke(portfolio, !allHeld);
            onChanged?.Invoke();
        };
        bodyRows.AddChild(master);

        foreach (var (portfolio, on, off, tooltip) in items)
        {
            var held = Delegation.Holds(nation, portfolio);
            var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            card.AddThemeStyleboxOverride("panel", ctx.BorderedBox(
                held ? "shell.plate_top" : "shell.well_bottom",
                held ? "shell.brass_hi" : "shell.engrave",
                1, 3, 8, 3, 8, 3));
            bodyRows.AddChild(card);

            var toggle = new CheckButton
            {
                Name = "ShellStaff_" + portfolio,
                Text = Loc.Get(held ? on : off),
                CustomMinimumSize = new Vector2(0, 28),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = Ui.Tip(Loc.Get(tooltip))
            };
            toggle.SetPressedNoSignal(held);
            toggle.AddThemeFontOverride("font", ctx.Font("shell.sans"));
            toggle.AddThemeFontSizeOverride("font_size", 12);
            toggle.AddThemeColorOverride("font_color", Ui.Colour(held ? "shell.paper" : "shell.paper_muted"));
            var captured = portfolio;
            toggle.Toggled += delegated =>
            {
                ctx.PlayCue?.Invoke("ui_select");
                ctx.DelegatePortfolio?.Invoke(captured, delegated);
                onChanged?.Invoke();
            };
            card.AddChild(toggle);
        }
    }

    private static void AddStaffQuickLink(ShellContext ctx, VBoxContainer parent, Nation nation, Action openStaffTab)
    {
        var items = DealOrForceGame.StaffPortfolios;
        var active = items.Count(entry => Delegation.Holds(nation, entry.Portfolio));

        var btn = new Button
        {
            Name = "ShellOverviewOpenStaff",
            CustomMinimumSize = new Vector2(0, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get("shell.staff_subtitle"))
        };
        var normal = ctx.BorderedBox("shell.plate_top", active > 0 ? "shell.brass_hi" : "shell.engrave", 1, 3, 8, 4, 8, 4);
        var hover = ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 3, 8, 4, 8, 4);
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(underlineLeft: true));
        btn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); openStaffTab(); };
        parent.AddChild(btn);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 8; row.OffsetRight = -8;
        row.AddThemeConstantOverride("separation", 6);
        btn.AddChild(row);

        var tex = ctx.Icon("icon_government");
        if (tex is not null)
        {
            var ico = new TextureRect
            {
                Texture = tex, CustomMinimumSize = new Vector2(15, 15),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            ShellContext.ApplyIconRelief(ico, Ui.Colour("shell.icon"), 0.55f);
            row.AddChild(ico);
        }
        row.AddChild(MakeLabel(ctx, Loc.Format("shell.staff_active_fmt", active, items.Length),
            "shell.sans", 11, active > 0 ? "shell.paper" : "shell.paper_muted", clip: true, expand: true, tabular: true));
        row.AddChild(MakeLabel(ctx, Loc.Get("shell.staff_manage_cta"),
            "shell.serif_semibold", 11, "shell.state.warning_text"));
    }

    private static void AddStandingGrid(
        ShellContext ctx,
        VBoxContainer parent,
        Campaign world,
        Nation nation,
        bool isOwn)
    {
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 4);
        parent.AddChild(grid);

        AddPillCard(ctx, grid, "icon_gdp", Loc.Get("GDP"), Fmt.Money(nation.Gdp));
        AddPillCard(ctx, grid, "icon_treasury", Loc.Get("Treasury"), isOwn ? Fmt.Money(nation.Treasury) : "—");
        AddPillCard(ctx, grid, "icon_inflation", Loc.Get("Inflation"), Fmt.Percent(nation.Inflation));
        AddPillCard(ctx, grid, "icon_population", Loc.Get("Manpower"), Fmt.People(nation.ManpowerReserve));
    }

    private static void AddSocietyMeter(
        ShellContext ctx,
        VBoxContainer parent,
        string iconName,
        string label,
        float fraction01,
        string valueText,
        Color fillColor)
    {
        ShellRegionVisualsView.AddMeterRow(ctx, parent, iconName, label, fraction01, valueText, fillColor, label);
    }

    private static void AddPillCard(
        ShellContext ctx,
        GridContainer parent,
        string iconName,
        string label,
        string value)
    {
        var pill = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        pill.AddThemeStyleboxOverride("panel", ctx.BorderedBox(
            "shell.plate_bottom", "shell.engrave", 1, 2, 6, 4, 6, 4));
        parent.AddChild(pill);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        pill.AddChild(row);

        var tex = ctx.Icon(iconName);
        if (tex is not null)
        {
            var ico = new TextureRect
            {
                Texture = tex,
                CustomMinimumSize = new Vector2(16, 16),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            ShellContext.ApplyIconRelief(ico, Ui.Colour("shell.icon"), 0.60f);
            row.AddChild(ico);
        }

        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 1);
        row.AddChild(col);

        col.AddChild(MakeLabel(ctx, label, "shell.sans", 10, "shell.paper_faint", clip: true));
        col.AddChild(MakeLabel(ctx, value, "shell.sans", 11, "shell.paper", clip: true, tabular: true));
    }

    private static void AddCountryComparisonRow(
        ShellContext ctx,
        VBoxContainer parent,
        Nation target,
        string gdpText,
        string relText,
        string relColorTok,
        Action onClick)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, 26),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        btn.AddThemeStyleboxOverride("normal", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 6, 2, 6, 2));
        btn.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.border_bright", 1, 2, 6, 2, 6, 2));
        btn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(underlineBottom: true));
        btn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); onClick(); };
        parent.AddChild(btn);

        var row = new HBoxContainer();
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 6; row.OffsetRight = -6;
        row.AddThemeConstantOverride("separation", 6);
        btn.AddChild(row);

        var flagTex = ctx.ResolveFlag?.Invoke(target.Tag);
        if (flagTex is not null)
        {
            var flag = new TextureRect
            {
                Texture = flagTex,
                CustomMinimumSize = new Vector2(18, 12),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            row.AddChild(flag);
        }

        row.AddChild(MakeLabel(ctx, target.DisplayName, "shell.sans", 11, "shell.paper", clip: true, expand: true));
        row.AddChild(MakeLabel(ctx, gdpText, "shell.sans", 11, "shell.paper_muted", tabular: true));
        row.AddChild(MakeLabel(ctx, relText, "shell.sans", 11, relColorTok, tabular: true));
    }

    private static void AddKvRow(
        ShellContext ctx,
        VBoxContainer bodyRows,
        string key,
        string value,
        string valueTok)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 22) };
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(MakeLabel(ctx, key, "shell.sans", 12, "shell.paper_muted", clip: true, expand: true));
        var v = MakeLabel(ctx, value, "shell.sans", 12, valueTok, tabular: true);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(v);
        bodyRows.AddChild(row);
    }

    private static Label MakeLabel(
        ShellContext ctx,
        string text,
        string fontKey,
        int size,
        string colorTok,
        bool clip = false,
        bool expand = false,
        bool tabular = false)
    {
        var lbl = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = clip
        };
        if (expand) lbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (clip) lbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        lbl.AddThemeFontOverride("font", ctx.Font(fontKey, tabular));
        lbl.AddThemeFontSizeOverride("font_size", size);
        lbl.AddThemeColorOverride("font_color", Ui.Colour(colorTok));
        return lbl;
    }

    private static Color StabilityColor(float v) => v < 0.35f ? Ui.Colour("shell.state.negative") : v < 0.55f ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.positive");
    private static Color GoodHighColor(float v) => v < 0.35f ? Ui.Colour("shell.state.negative") : v < 0.65f ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.positive");
    private static Color DangerHighColor(float v) => v > 0.45f ? Ui.Colour("shell.state.negative") : v > 0.25f ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.positive");
}
