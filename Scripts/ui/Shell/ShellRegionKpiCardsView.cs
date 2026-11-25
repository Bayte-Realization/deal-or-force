using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Renders inlaid Region KPI header cards (Population + growth, GDP + war damage, Stability + unrest
/// bar, Control + Owner/Controller flags &amp; garrison), Terrain &amp; Supply card, and flagged Garrison
/// rows for <see cref="ShellRegionVisualsView"/>.
/// </summary>
internal static class ShellRegionKpiCardsView
{
    public static void AddRegionKpiCards(
        ShellContext ctx,
        VBoxContainer parent,
        Campaign world,
        Nation owner,
        ScenarioState state,
        int provCount)
    {
        var grid = new GridContainer
        {
            Name = "ShellRegionKpiGrid",
            Columns = 2,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        parent.AddChild(grid);

        // 1. Population + Natural Growth
        string growthText, growthTok;
        if (owner is not null)
        {
            var (birth, death, _) = HouseholdEconomy.VitalRates(world, owner, state);
            var delta = birth - death;
            growthText = Loc.Format("shell.ws.reg.growth_fmt", $"{delta:+0.00%;-0.00%;0.00%}");
            growthTok = delta < -0.0005f ? "shell.state.warning_text" : delta > 0.0005f ? "shell.state.positive" : "shell.paper_faint";
        }
        else
        {
            growthText = Fmt.PeopleExact(state.Population);
            growthTok = "shell.paper_faint";
        }
        AddKpiCard(ctx, grid, "icon_population", Loc.Get("shell.ws.reg.kpi_pop"),
            Fmt.People(state.Population), "shell.paper", growthText, growthTok,
            $"{Loc.Get("Population")}: {Fmt.PeopleExact(state.Population)}\n{growthText}");

        // 2. Economy / Regional GDP (+ War Damage alert if present)
        var estGdp = RegionalStatistics.EstimatedGdp(world, state);
        var gdpShare = owner is not null && owner.Gdp > 0.01f ? (int)MathF.Round((estGdp / owner.Gdp) * 100f) : 0;
        var hasDamage = state.WarDamage > 0.02f;
        var ecoSub = hasDamage
            ? Loc.Format("shell.ws.reg.war_damage_fmt", Fmt.Percent(state.WarDamage))
            : Loc.Format("shell.ws.reg.gdp_share_fmt", gdpShare);
        AddKpiCard(ctx, grid, "icon_economy", Loc.Get("shell.ws.reg.kpi_gdp"),
            Fmt.Money(estGdp), "shell.brass_hi", ecoSub, hasDamage ? "shell.state.negative_text" : "shell.paper_faint",
            $"{Loc.Get("shell.ws.reg.kpi_gdp")}: {Fmt.Money(estGdp)} ({gdpShare}%)\n{Loc.Get(RegionalStatistics.GdpExplanation)}");

        // 3. Stability & Unrest Bar
        var stabCard = CreateKpiPlate(ctx, $"{Loc.Get("Stability")}: {Fmt.Percent(state.Stability)} · {Loc.Get("Radicalism")}: {Fmt.Percent(state.Radicalism)}");
        grid.AddChild(stabCard);
        var stabCol = new VBoxContainer { ClipContents = true };
        stabCol.AddThemeConstantOverride("separation", 3);
        stabCard.AddChild(stabCol);

        stabCol.AddChild(BuildKpiCardTop(ctx, "icon_laws", Loc.Get("shell.ws.reg.kpi_stability")));
        var stabRow = new HBoxContainer { ClipContents = true };
        stabRow.AddThemeConstantOverride("separation", 6);
        stabCol.AddChild(stabRow);
        var stabTok = state.Stability < 0.35f ? "shell.state.negative_text" : state.Stability < 0.55f ? "shell.state.warning_text" : "shell.state.positive";
        stabRow.AddChild(ShellRegionVisualsView.MakeLabel(ctx, Fmt.Percent(state.Stability), "shell.sans", 15, stabTok, tabular: true));
        var unrestLbl = ShellRegionVisualsView.MakeLabel(ctx, Loc.Format("shell.ws.reg.unrest_fmt", Fmt.Percent(state.Radicalism)),
            "shell.sans", 10, state.Radicalism > 0.30f ? "shell.state.warning_text" : "shell.paper_faint", clip: true, expand: true, tabular: true);
        unrestLbl.HorizontalAlignment = HorizontalAlignment.Right;
        stabRow.AddChild(unrestLbl);
        stabCol.AddChild(BuildMiniProgressBar(ctx, state.Stability, ShellRegionVisualsView.StabilityColor(state.Stability)));

        // 4. Control & Garrison + Owner/Controller Flags
        AddControlGarrisonCard(ctx, grid, world, owner, state, provCount);
    }

    private static void AddControlGarrisonCard(
        ShellContext ctx,
        GridContainer grid,
        Campaign world,
        Nation owner,
        ScenarioState state,
        int provCount)
    {
        var (controller, occupiedProvs) = InspectRegionControl(world, owner, state);
        var arrangement = controller is not null ? LandArrangementSystem.Covering(world, state.MapIndex, controller.Id) : null;
        var leased = arrangement is { Kind: LandArrangementSystem.Lease };
        var visDivs = world.Armies
            .Where(a => a.StateMapIndex == state.MapIndex && CampaignIntel.PlayerCanSeeArmy(world, a))
            .Sum(a => a.Divisions);
        var ctrlTip = owner is not null ? Loc.Format("shell.ws.reg.owner_tip_fmt", owner.DisplayName) : "";
        if (controller is not null)
        {
            ctrlTip += "\n" + Loc.Format("shell.ws.reg.controller_tip_fmt", controller.DisplayName);
            if (arrangement is not null)
                ctrlTip += "\n" + LandArrangementSystem.Describe(world, state);
        }

        var ctrlCard = CreateKpiPlate(ctx, ctrlTip);
        grid.AddChild(ctrlCard);
        var ctrlCol = new VBoxContainer { ClipContents = true };
        ctrlCol.AddThemeConstantOverride("separation", 3);
        ctrlCard.AddChild(ctrlCol);

        ctrlCol.AddChild(BuildKpiCardTop(ctx, "icon_military", Loc.Get("shell.ws.reg.kpi_control")));
        var flagsRow = new HBoxContainer { ClipContents = true };
        flagsRow.AddThemeConstantOverride("separation", 5);
        ctrlCol.AddChild(flagsRow);
        if (owner is not null)
            flagsRow.AddChild(BuildMiniFlagFrame(ctx, owner.Tag, "shell.border", Loc.Format("shell.ws.reg.owner_tip_fmt", owner.DisplayName)));
        var controllerFrameColor = arrangement is not null ? "shell.state.warning" : "shell.state.negative";
        if (controller is not null)
            flagsRow.AddChild(BuildMiniFlagFrame(ctx, controller.Tag, controllerFrameColor, Loc.Format("shell.ws.reg.controller_tip_fmt", controller.DisplayName)));

        var ctrlStatus = controller is not null
            ? Loc.Format(leased ? "shell.ws.reg.leased_by_fmt" : "shell.ws.reg.occupied_by_fmt", controller.DisplayName)
            : Loc.Get("shell.ws.reg.full_control");
        var ctrlTextColor = controller is not null
            ? (arrangement is not null ? "shell.state.warning_text" : "shell.state.negative_text")
            : "shell.state.positive";
        flagsRow.AddChild(ShellRegionVisualsView.MakeLabel(ctx, ctrlStatus, "shell.sans", 11, ctrlTextColor, clip: true, expand: true));

        var garSub = Loc.Format("shell.ws.reg.garrison_divs_fmt", $"{visDivs:0.#}", provCount.ToString(CultureInfo.InvariantCulture));
        if (occupiedProvs > 0) garSub = $"{occupiedProvs}/{provCount} · {garSub}";
        ctrlCol.AddChild(ShellRegionVisualsView.MakeLabel(ctx, garSub, "shell.sans", 10, "shell.paper_faint", clip: true, tabular: true));
    }

    public static void AddTerrainCard(
        ShellContext ctx,
        VBoxContainer parent,
        Campaign world,
        Nation owner,
        ScenarioState state,
        int provinceId,
        int provCount)
    {
        var terrain = world.ProvinceMap is not null && provinceId > 0 && provinceId <= world.ProvinceMap.ProvinceCount
            ? world.ProvinceMap.TerrainAt(provinceId)
            : TerrainCatalog.Get("plains");
        var isCap = owner is not null && state.MapIndex == owner.CapitalStateMapIndex;
        var infraLv = state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure);

        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 3, 8, 6, 8, 6));
        parent.AddChild(card);

        var col = new VBoxContainer { ClipContents = true };
        col.AddThemeConstantOverride("separation", 5);
        card.AddChild(col);

        var top = new HBoxContainer { ClipContents = true };
        top.AddThemeConstantOverride("separation", 8);
        col.AddChild(top);

        var badgeTex = ctx.Icon($"icon_terrain_{terrain.Id.ToLowerInvariant()}") ?? ctx.Icon("icon_atlas_terrain");
        if (badgeTex is not null)
        {
            top.AddChild(new TextureRect
            {
                Texture = badgeTex, CustomMinimumSize = new Vector2(28, 28),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            });
        }

        var metaCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        metaCol.AddThemeConstantOverride("separation", 1);
        top.AddChild(metaCol);

        var capTag = isCap ? $" · ★ {Loc.Get("Capital")}" : "";
        var tName = string.IsNullOrWhiteSpace(terrain.Name) ? terrain.Id : Loc.Get(terrain.Name);
        metaCol.AddChild(ShellRegionVisualsView.MakeLabel(ctx, tName + capTag, "shell.serif_semibold", 13, "shell.paper", clip: true));

        var supSub = Loc.Format("shell.ws.reg.supply_fmt", $"{terrain.Supply:0.00}", infraLv.ToString(CultureInfo.InvariantCulture));
        metaCol.AddChild(ShellRegionVisualsView.MakeLabel(ctx, $"{supSub} · {provCount} {Loc.Get("Provinces").ToLowerInvariant()}",
            "shell.sans", 11, "shell.paper_faint", clip: true, tabular: true));

        var grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        grid.AddThemeConstantOverride("h_separation", 4);
        col.AddChild(grid);

        AddTerrainPill(ctx, grid, Loc.Get("shell.ws.reg.move_short"), Loc.Get("shell.terrain_move"), terrain.MovementSpeed);
        AddTerrainPill(ctx, grid, Loc.Get("shell.ws.reg.atk_short"), Loc.Get("shell.terrain_atk"), terrain.Attack);
        AddTerrainPill(ctx, grid, Loc.Get("shell.ws.reg.def_short"), Loc.Get("shell.terrain_def"), terrain.Defense);
        AddTerrainPill(ctx, grid, Loc.Get("shell.ws.reg.sup_short"), Loc.Get("shell.terrain_sup"), terrain.Supply);
    }

    public static void AddGarrisonList(ShellContext ctx, VBoxContainer bodyRows, Campaign world, ScenarioState state)
    {
        var armies = world.Armies
            .Where(a => a.StateMapIndex == state.MapIndex && CampaignIntel.PlayerCanSeeArmy(world, a))
            .OrderByDescending(a => a.Divisions)
            .Take(6)
            .ToArray();
        if (armies.Length == 0)
        {
            bodyRows.AddChild(ShellRegionVisualsView.MakeLabel(ctx, Loc.Get("shell.reg_no_garrison"), "shell.sans", 12, "shell.paper_faint"));
            return;
        }

        foreach (var army in armies)
        {
            var armyOwner = world.NationById(army.NationId);
            var str = Math.Clamp(army.Strength, 0f, 1f);
            var tip = Loc.Format("shell.ws.reg.garrison_tip_fmt",
                army.Name, armyOwner?.DisplayName ?? "—", $"{army.Divisions:0.#}", Fmt.Percent(str));

            var card = new PanelContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipContents = true,
                TooltipText = Ui.Tip(tip)
            };
            card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.plate_bottom", "shell.engrave", 1, 2, 6, 4, 6, 4));
            bodyRows.AddChild(card);

            var row = new HBoxContainer { ClipContents = true };
            row.AddThemeConstantOverride("separation", 6);
            card.AddChild(row);

            if (armyOwner is not null)
                row.AddChild(BuildMiniFlagFrame(ctx, armyOwner.Tag, "shell.border", armyOwner.DisplayName));

            row.AddChild(ShellRegionVisualsView.MakeLabel(ctx, army.Name, "shell.sans", 11, "shell.paper", clip: true, expand: true));
            row.AddChild(ShellRegionVisualsView.MakeLabel(ctx, $"{army.Divisions:0.#} {Loc.Get("shell.div_short")}", "shell.sans", 11, "shell.paper_muted", tabular: true));

            var bar = BuildMiniProgressBar(ctx, str, ShellRegionVisualsView.GoodHighColor(str));
            bar.CustomMinimumSize = new Vector2(46, 8);
            bar.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            bar.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(bar);
        }
    }

    private static (Nation DominantOccupier, int OccupiedProvinces) InspectRegionControl(Campaign world, Nation owner, ScenarioState state)
    {
        if (world.ProvinceMap is not { } map || owner is null) return (null, 0);
        var controllers = world.ProvinceController ?? map.ProvinceToNation;
        var toState = map.ProvinceToState;
        if (controllers is null || toState is null) return (null, 0);

        var occupied = 0;
        var occupierCounts = new Dictionary<int, int>();
        var max = Math.Min(map.ProvinceCount, Math.Min(controllers.Length - 1, toState.Length - 1));
        for (var p = 1; p <= max; p++)
        {
            if (toState[p] != state.MapIndex || map.WaterByProvince[p]) continue;
            var ctrl = controllers[p];
            if (ctrl > 0 && ctrl != owner.MapIndex)
            {
                occupied++;
                occupierCounts[ctrl] = occupierCounts.GetValueOrDefault(ctrl) + 1;
            }
        }
        if (occupied == 0) return (null, 0);
        var topMapIdx = occupierCounts.OrderByDescending(kv => kv.Value).First().Key;
        return (world.Nations.FirstOrDefault(n => n.MapIndex == topMapIdx), occupied);
    }

    private static PanelContainer CreateKpiPlate(ShellContext ctx, string tooltip)
    {
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true, TooltipText = Ui.Tip(tooltip) };
        card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 3, 7, 5, 7, 5));
        return card;
    }

    private static HBoxContainer BuildKpiCardTop(ShellContext ctx, string iconName, string caption)
    {
        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 4);
        if (ctx.Icon(iconName) is { } ico)
        {
            row.AddChild(new TextureRect
            {
                Texture = ico, CustomMinimumSize = new Vector2(12, 12),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                Modulate = Ui.Colour("shell.paper_faint"), MouseFilter = Control.MouseFilterEnum.Ignore
            });
        }
        row.AddChild(ShellRegionVisualsView.MakeLabel(ctx, caption, "shell.sans", 10, "shell.paper_faint", clip: true, expand: true));
        return row;
    }

    private static void AddKpiCard(
        ShellContext ctx, GridContainer grid, string iconName, string caption,
        string mainValue, string mainColorTok, string subValue, string subColorTok, string tooltip)
    {
        var card = CreateKpiPlate(ctx, tooltip);
        grid.AddChild(card);
        var col = new VBoxContainer { ClipContents = true };
        col.AddThemeConstantOverride("separation", 2);
        card.AddChild(col);
        col.AddChild(BuildKpiCardTop(ctx, iconName, caption));
        col.AddChild(ShellRegionVisualsView.MakeLabel(ctx, mainValue, "shell.sans", 15, mainColorTok, clip: true, tabular: true));
        col.AddChild(ShellRegionVisualsView.MakeLabel(ctx, subValue, "shell.sans", 10, subColorTok, clip: true, tabular: true));
    }

    private static void AddTerrainPill(ShellContext ctx, GridContainer grid, string shortTag, string fullCaption, float mult)
    {
        var pill = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true,
            TooltipText = Ui.Tip(Loc.Format("shell.ws.reg.terrain_tip_fmt", fullCaption, $"{mult:0.00}"))
        };
        pill.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.plate_top", "shell.engrave", 1, 2, 4, 2, 4, 2));
        grid.AddChild(pill);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, ClipContents = true };
        row.AddThemeConstantOverride("separation", 3);
        pill.AddChild(row);
        row.AddChild(ShellRegionVisualsView.MakeLabel(ctx, shortTag, "shell.sans", 9, "shell.paper_faint", clip: true));
        var valCol = mult > 1.04f ? "shell.state.positive" : mult < 0.85f ? "shell.state.warning_text" : "shell.paper";
        row.AddChild(ShellRegionVisualsView.MakeLabel(ctx, $"{mult:0.00}×", "shell.sans", 11, valCol, tabular: true));
    }

    internal static PanelContainer BuildMiniProgressBar(ShellContext ctx, float fraction01, Color fillColor)
    {
        var clamped = Math.Clamp(fraction01, 0f, 1f);
        var wrap = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, 7), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore
        };
        wrap.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well", "shell.engrave", 1, 2, 1, 1, 1, 1));
        var barRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        barRow.AddThemeConstantOverride("separation", 0);
        wrap.AddChild(barRow);

        var ratio = Math.Max(0.01f, clamped);
        barRow.AddChild(new ColorRect { Color = fillColor, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = ratio, MouseFilter = Control.MouseFilterEnum.Ignore });
        barRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = Math.Max(0.001f, 1f - ratio), MouseFilter = Control.MouseFilterEnum.Ignore });
        return wrap;
    }

    internal static PanelContainer BuildMiniFlagFrame(ShellContext ctx, string tag, string borderToken, string tooltip)
    {
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(22, 15), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ClipContents = true, TooltipText = Ui.Tip(tooltip) };
        frame.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well", borderToken, 1, 1, 1, 1, 1, 1));
        frame.AddChild(new TextureRect
        {
            Material = FlagClothPresentation.Material,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
            Texture = ctx.FlagTexture(tag),
            MouseFilter = Control.MouseFilterEnum.Ignore
        });
        return frame;
    }
}
