using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

internal enum ConstructionCategory { All, Industry, Resources, Infrastructure, MilitaryScience }

internal sealed class ConstructionKpiRibbonNodes
{
    public GridContainer Root;
    public Label SitesVal, SitesSub;
    public Label WeeklyVal, WeeklySub;
    public Label CapVal, CapSub;
    public Label TreasuryVal, TreasurySub;
}

internal readonly record struct ConstructionCardActions(
    Action<BuildingKind> PlaceOnMap,
    Action<BuildingKind> BuildOne,
    Action<BuildingKind> BuildThree,
    Action<BuildingKind> DemolishOne);

/// <summary>
/// Sovereign Brass KPI ribbon, building catalogue cards, and active queue cards for
/// <see cref="ShellConstructionWorkspaceView"/>. Uses palette tokens exclusively (zero raw Color).
/// </summary>
internal static class ShellConstructionCards
{
    public static readonly (ConstructionCategory Category, string LocKey)[] Categories =
    {
        (ConstructionCategory.All, "shell.ws.build.cat_all"),
        (ConstructionCategory.Industry, "shell.ws.build.cat_industry"),
        (ConstructionCategory.Resources, "shell.ws.build.cat_resources"),
        (ConstructionCategory.Infrastructure, "shell.ws.build.cat_infra"),
        (ConstructionCategory.MilitaryScience, "shell.ws.build.cat_military_science")
    };

    public static ConstructionCategory CategoryOf(BuildingKind kind) => kind switch
    {
        BuildingKind.Agriculture or BuildingKind.ExtractionIndustry or BuildingKind.PowerPlant => ConstructionCategory.Resources,
        BuildingKind.Infrastructure or BuildingKind.Port or BuildingKind.RailNetwork or BuildingKind.Hospital => ConstructionCategory.Infrastructure,
        BuildingKind.Barracks or BuildingKind.ArmsIndustry or BuildingKind.Shipyard or BuildingKind.University or BuildingKind.ResearchLab => ConstructionCategory.MilitaryScience,
        _ => ConstructionCategory.Industry
    };

    public static Texture2D BuildingIcon(ShellContext ctx, BuildingKind kind)
    {
        var iconName = DealOrForceGame.BuildingIconName(kind);
        return ctx.Icon(iconName) ?? ctx.Icon("icon_industry");
    }

    public static ConstructionKpiRibbonNodes BuildKpiRibbon(ShellContext ctx, VBoxContainer parent)
    {
        var nodes = new ConstructionKpiRibbonNodes();
        var grid = new GridContainer
        {
            Name = "ShellConstructionKpiRibbon", Columns = 4,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true
        };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 6);
        parent.AddChild(grid);
        nodes.Root = grid;

        CreateKpiCard(ctx, grid, Loc.Get("shell.ws.build.kpi_sites"), out nodes.SitesVal, out nodes.SitesSub);
        CreateKpiCard(ctx, grid, Loc.Get("shell.ws.build.kpi_weekly"), out nodes.WeeklyVal, out nodes.WeeklySub);
        CreateKpiCard(ctx, grid, Loc.Get("shell.ws.build.kpi_capacity"), out nodes.CapVal, out nodes.CapSub);
        CreateKpiCard(ctx, grid, Loc.Get("shell.ws.build.kpi_treasury"), out nodes.TreasuryVal, out nodes.TreasurySub);
        return nodes;
    }

    public static void RefreshKpiRibbon(
        ConstructionKpiRibbonNodes r, Campaign world, Nation n, CampaignHudSnapshot hud,
        int running, int maxSlots, int workingSlots, float weeklyDraw)
    {
        if (r is null || world is null || n is null) return;
        var workingNow = Math.Min(running, workingSlots);
        var queued = Math.Max(0, running - workingNow);
        r.SitesVal.Text = $"{running}/{maxSlots}";
        r.SitesVal.AddThemeColorOverride("font_color", Ui.Colour(running >= maxSlots ? "shell.state.warning_text" : running > 0 ? "shell.state.info" : "shell.paper"));
        r.SitesSub.Text = Loc.Format("shell.ws.build.kpi_sites_sub_fmt", workingNow, queued);

        var ceiling = ConstructionQueue.WeeklyCeiling(world, n);
        r.WeeklyVal.Text = Fmt.Money(weeklyDraw);
        r.WeeklyVal.AddThemeColorOverride("font_color", Ui.Colour(weeklyDraw > 0.001f ? "shell.paper" : "shell.paper_muted"));
        r.WeeklySub.Text = float.IsFinite(ceiling)
            ? Loc.Format("shell.ws.build.kpi_weekly_sub_fmt", Fmt.Money(ceiling))
            : Loc.Get("shell.ws.build.kpi_weekly_nocap");

        var pacePct = ConstructionCost.Pace(world, n) * 100f;
        r.CapVal.Text = Fmt.Number(n.ConstructionCapacity);
        r.CapVal.AddThemeColorOverride("font_color", Ui.Colour(pacePct >= 105f ? "shell.state.positive" : "shell.paper"));
        r.CapSub.Text = Loc.Format("shell.ws.build.kpi_pace_fmt", pacePct, workingSlots);

        var treasury = hud is { Version: > 0 } && hud.PlayerId == n.Id ? hud.Treasury : n.Treasury;
        var outstanding = ConstructionQueue.Outstanding(world, n);
        r.TreasuryVal.Text = Fmt.Money(treasury);
        r.TreasuryVal.AddThemeColorOverride("font_color", Ui.Colour(treasury < 0f ? "shell.state.negative_text" : "shell.paper"));
        r.TreasurySub.Text = Loc.Format("shell.ws.build.kpi_treasury_sub_fmt", Fmt.Money(outstanding));
    }

    public static PanelContainer BuildBuildingCard(
        ShellContext ctx, Campaign world, Nation nation, ScenarioState targetState, BuildingKind kind,
        int standingHere, int standingNation, (string Text, string Summary) outlook,
        bool observer, bool isTouch, ConstructionCardActions actions)
    {
        var def = BuildingCatalog.Get(kind);
        var id = BuildingCatalog.IdOf(kind);
        var card = new PanelContainer
        {
            Name = "ShellBuildCard_" + id,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.plate_bottom", "shell.engrave", 1, 3, 10, 8, 10, 8));

        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        col.AddThemeConstantOverride("separation", 5);
        card.AddChild(col);

        var top = new HBoxContainer { ClipContents = true };
        top.AddThemeConstantOverride("separation", 7);
        col.AddChild(top);

        if (BuildingIcon(ctx, kind) is { } ico)
        {
            var iconRect = new TextureRect
            {
                Texture = ico, CustomMinimumSize = new Vector2(22, 22),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            ShellContext.ApplyIconRelief(iconRect, Ui.Colour("shell.brass_hi"), 0.5f);
            top.AddChild(iconRect);
        }

        var nameLbl = new Label
        {
            Text = Loc.Get(def.Name), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center, ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        nameLbl.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
        nameLbl.AddThemeFontSizeOverride("font_size", 14);
        nameLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        top.AddChild(nameLbl);

        var badge = new Label
        {
            Text = Loc.Format("shell.ws.build.standing_badge_fmt", standingHere),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = Ui.Tip(Loc.Format("shell.ws.build.standing_tip_fmt",
                standingHere, targetState?.DisplayName ?? "—", standingNation))
        };
        badge.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        badge.AddThemeFontSizeOverride("font_size", 11);
        badge.AddThemeColorOverride("font_color", Ui.Colour(standingHere > 0 ? "shell.brass_hi" : "shell.paper_faint"));
        top.AddChild(badge);

        var cost = ConstructionCost.Of(world, nation, kind);
        var days = ConstructionCost.ExpectedDays(world, nation, kind);
        var weekly = ConstructionCost.WeeklyPayment(world, nation, kind);
        var costLbl = new Label
        {
            Text = Loc.Format("shell.ws.build.cost_line_fmt", Fmt.Money(cost), days, Fmt.Money(weekly)),
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        costLbl.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        costLbl.AddThemeFontSizeOverride("font_size", 11);
        costLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        col.AddChild(costLbl);

        var fullTip = Loc.Get(def.Description) + (string.IsNullOrEmpty(outlook.Text) ? "" : "\n\n" + outlook.Text);
        var effectLbl = new Label
        {
            Text = string.IsNullOrEmpty(outlook.Summary) ? Loc.Get(def.Description) : outlook.Summary,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = Control.MouseFilterEnum.Pass, TooltipText = Ui.Tip(fullTip)
        };
        effectLbl.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        effectLbl.AddThemeFontSizeOverride("font_size", 11);
        effectLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        col.AddChild(effectLbl);
        card.TooltipText = Ui.Tip(fullTip);

        BuildCardActionRow(ctx, col, world, nation, targetState, kind, id, cost, standingHere, observer, isTouch, actions);
        return card;
    }

    private static void BuildCardActionRow(
        ShellContext ctx, VBoxContainer col, Campaign world, Nation nation, ScenarioState targetState,
        BuildingKind kind, string id, float cost, int standingHere, bool observer, bool isTouch, ConstructionCardActions actions)
    {
        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 5);
        col.AddChild(row);

        var btnH = isTouch ? 40 : 34;
        var ruleRefusal = BuildingRules.Refusal(world, nation, kind);
        var admissionRefusal = observer
            ? Loc.Get("obs.ui.action.readonly")
            : ConstructionAdmission.Refusal(world, nation, targetState, kind);
        var canPlace = !observer && ruleRefusal.Length == 0 && world.FeatureEnabled("construction");
        var canBuild = !observer && admissionRefusal.Length == 0 && targetState is not null;

        var placeBtn = new Button
        {
            Name = "ShellPlaceOnMap_" + id,
            Text = Loc.Get("shell.ws.build.on_map"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(88, btnH),
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            Disabled = !canPlace,
            TooltipText = Ui.Tip(observer ? Loc.Get("obs.ui.action.readonly")
                : !canPlace ? ruleRefusal
                : Loc.Get(isTouch ? "shell.ws.build.on_map_tip_touch" : "shell.ws.build.on_map_tip_pc")),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
        };
        placeBtn.AddThemeStyleboxOverride("normal", ctx.NineSlice("button_brass_cta_9s", 8, 10, 4, 10, 4, "shell.plate_top", "shell.brass_hi", 3));
        placeBtn.AddThemeStyleboxOverride("hover", ctx.NineSlice("button_brass_cta_9s", 8, 10, 4, 10, 4, "shell.plate_hover", "shell.paper", 3));
        placeBtn.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
        placeBtn.AddThemeFontSizeOverride("font_size", 12);
        placeBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        placeBtn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); actions.PlaceOnMap(kind); };
        row.AddChild(placeBtn);

        var oneBtn = CreateSmallActionBtn(ctx, "ShellBuildOne_" + id, "+1", isTouch ? 44 : 38, btnH, !canBuild,
            canBuild ? Loc.Format("shell.ws.build.build_one_tip_fmt", targetState.DisplayName, Fmt.Money(cost)) : admissionRefusal,
            () => actions.BuildOne(kind));
        row.AddChild(oneBtn);

        var threeBtn = CreateSmallActionBtn(ctx, "ShellBuildThree_" + id, "×3", isTouch ? 46 : 40, btnH, !canBuild,
            canBuild ? Loc.Format("shell.ws.build.build_three_tip_fmt", targetState.DisplayName, Fmt.Money(cost)) : admissionRefusal,
            () => actions.BuildThree(kind));
        row.AddChild(threeBtn);

        var canDemolish = !observer && standingHere > 0 && targetState is not null;
        var demolishTip = observer ? Loc.Get("obs.ui.action.readonly")
            : !canDemolish ? Loc.Get("shell.ws.build.demolish_none")
            : Loc.Format("shell.ws.build.demolish_tip_fmt", targetState.DisplayName, Fmt.Money(cost * world.Defines.DemolitionRefund));
        var demBtn = CreateSmallActionBtn(ctx, "ShellDemolishOne_" + id, "−", isTouch ? 40 : 34, btnH, !canDemolish,
            demolishTip, () => actions.DemolishOne(kind));
        row.AddChild(demBtn);
    }

    public static PanelContainer BuildQueueItemCard(
        ShellContext ctx, Campaign world, ConstructionQueue.Estimate estimate, ScenarioState region,
        bool observer, bool isTouch, Action<ScenarioState> onFocusRegion, Action<int> onCancel)
    {
        var project = estimate.Project;
        var def = BuildingCatalog.Get(project.Building);
        var card = new PanelContainer
        {
            Name = "ShellQueueCard_" + project.Id,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        var unfunded = project.Waiting == ConstructionQueue.Treasury || !estimate.Funded;
        card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom",
            unfunded ? "shell.state.negative" : "shell.engrave", 1, 3, 9, 7, 9, 7));

        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        col.AddThemeConstantOverride("separation", 4);
        card.AddChild(col);

        var top = new HBoxContainer { ClipContents = true };
        top.AddThemeConstantOverride("separation", 6);
        col.AddChild(top);

        if (BuildingIcon(ctx, project.Building) is { } ico)
        {
            top.AddChild(new TextureRect
            {
                Texture = ico, CustomMinimumSize = new Vector2(18, 18),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                Modulate = Ui.Colour("shell.brass_hi"), MouseFilter = Control.MouseFilterEnum.Ignore
            });
        }

        var bName = new Label
        {
            Text = Loc.Get(def.Name), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        bName.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
        bName.AddThemeFontSizeOverride("font_size", 13);
        bName.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        top.AddChild(bName);

        var regBtn = new Button
        {
            Text = region?.DisplayName ?? "—",
            Flat = true, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            CustomMinimumSize = new Vector2(64, 24),
            Disabled = region is null,
            TooltipText = region is null ? "" : Ui.Tip(Loc.Format("shell.ws.build.queue_focus_tip_fmt", region.DisplayName)),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
        };
        regBtn.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        regBtn.AddThemeFontSizeOverride("font_size", 11);
        regBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.brass_hi"));
        regBtn.Pressed += () => { if (region is not null) { ctx.PlayCue?.Invoke("ui_select"); onFocusRegion(region); } };
        top.AddChild(regBtn);

        var mid = new HBoxContainer { ClipContents = true };
        mid.AddThemeConstantOverride("separation", 6);
        col.AddChild(mid);

        var progress = new ProgressBar
        {
            MinValue = 0, MaxValue = 100, Value = Math.Clamp(project.Progress * 100f, 0f, 100f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(80, isTouch ? 28 : 24)
        };
        mid.AddChild(progress);

        var projectId = project.Id;
        var cancelBtn = CreateSmallActionBtn(ctx, "ShellCancelConstruction_" + projectId,
            Loc.Get("shell.ws.build.queue_cancel"), isTouch ? 88 : 76, isTouch ? 34 : 26, observer,
            observer ? Loc.Get("obs.ui.action.readonly")
                : Loc.Format("shell.ws.build.queue_cancel_tip_fmt", Fmt.Money(project.Paid), Fmt.Money(MathF.Max(0f, project.Cost - project.Paid))),
            () => onCancel(projectId));
        mid.AddChild(cancelBtn);

        var paidLbl = new Label
        {
            Text = Loc.Format("shell.ws.build.queue_paid_fmt", Fmt.Money(project.Paid), Fmt.Money(project.Cost)),
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        paidLbl.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        paidLbl.AddThemeFontSizeOverride("font_size", 10);
        paidLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        col.AddChild(paidLbl);

        var statusLbl = new Label
        {
            Text = ConstructionQueue.Describe(world, project) + " · " + ConstructionQueue.When(estimate),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        statusLbl.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        statusLbl.AddThemeFontSizeOverride("font_size", 11);
        statusLbl.AddThemeColorOverride("font_color", Ui.Colour(unfunded ? "shell.state.negative_text" : "shell.paper_muted"));
        col.AddChild(statusLbl);

        return card;
    }

    public static PanelContainer BuildEmptyQueueCard(ShellContext ctx, bool isTouch)
    {
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 3, 12, 12, 12, 12));
        var col = new VBoxContainer { ClipContents = true };
        col.AddThemeConstantOverride("separation", 6);
        card.AddChild(col);

        var title = new Label { Text = Loc.Get("shell.ws.build.queue_empty_title") };
        title.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
        title.AddThemeFontSizeOverride("font_size", 13);
        title.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        col.AddChild(title);

        var hint = new Label
        {
            Text = Loc.Get(isTouch ? "shell.ws.build.queue_empty_touch" : "shell.ws.build.queue_empty_pc"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        hint.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        col.AddChild(hint);
        return card;
    }

    private static Button CreateSmallActionBtn(
        ShellContext ctx, string name, string text, int minW, int minH, bool disabled, string tip, Action onClick)
    {
        var b = new Button
        {
            Name = name, Text = text, CustomMinimumSize = new Vector2(minW, minH), Disabled = disabled,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            TooltipText = Ui.Tip(tip),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
        };
        b.AddThemeStyleboxOverride("normal", ctx.NineSlice("button_umber_9s", 8, 6, 3, 6, 3, "shell.plate_bottom", "shell.engrave", 2));
        b.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 6, 3, 6, 3));
        b.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        b.AddThemeFontSizeOverride("font_size", 12);
        b.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        b.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); onClick(); };
        return b;
    }

    private static void CreateKpiCard(ShellContext ctx, GridContainer parent, string caption, out Label valLbl, out Label subLbl)
    {
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 3, 9, 5, 9, 5));
        parent.AddChild(card);

        var col = new VBoxContainer { ClipContents = true };
        col.AddThemeConstantOverride("separation", 2);
        card.AddChild(col);

        var cap = new Label { Text = caption, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        cap.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        cap.AddThemeFontSizeOverride("font_size", 10);
        cap.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        col.AddChild(cap);

        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 6);
        col.AddChild(row);

        valLbl = new Label { Text = "—" };
        valLbl.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        valLbl.AddThemeFontSizeOverride("font_size", 16);
        valLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(valLbl);

        subLbl = new Label
        {
            Text = "", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        subLbl.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        subLbl.AddThemeFontSizeOverride("font_size", 10);
        subLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        row.AddChild(subLbl);
    }
}
