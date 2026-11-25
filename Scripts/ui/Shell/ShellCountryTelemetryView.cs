using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Rich overview &amp; society telemetry cards for <see cref="ShellCountryDossierView"/>:
/// 6-pill national standing KPI grid, Territory &amp; Industry summary card, and
/// population-weighted National Demography card (exact population, age structure,
/// employment, basic needs coverage, and vital/migration flows). Zero raw Color literals.
/// </summary>
internal static class ShellCountryTelemetryView
{
    public static void AddStandingGrid(
        ShellContext ctx, VBoxContainer parent, Campaign world, Nation nation, bool showTreasury)
    {
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 4);
        parent.AddChild(grid);

        var states = world.StatesOf(nation);
        var regSub = Loc.Format("shell.aaa.regions_count_fmt", states.Length);
        var popTip = $"{Loc.Get("Population")}: {Fmt.PeopleExact(nation.Population)}\n{regSub}";
        AddPillCard(ctx, grid, "icon_population", Loc.Get("Population"), Fmt.People(nation.Population),
            "shell.paper", regSub, "shell.paper_faint", popTip);

        var gdpPc = ShellCornerTelemetry.FormatGdpPerCapita(nation.Gdp, nation.Population);
        var gdpSub = Loc.Format("shell.aaa.per_capita_short_fmt", gdpPc);
        var gdpTip = $"{Loc.Get("GDP")}: {Fmt.MoneyExact(nation.Gdp)}\n{Loc.Get("GDP per head")}: {gdpPc}";
        AddPillCard(ctx, grid, "icon_gdp", Loc.Get("GDP"), Fmt.Money(nation.Gdp),
            "shell.brass_hi", gdpSub, "shell.paper_faint", gdpTip);

        var trVal = showTreasury ? Fmt.Money(nation.Treasury) : "—";
        var trTok = showTreasury && nation.Treasury < 0f ? "shell.state.negative_text" : "shell.paper";
        var balSub = showTreasury
            ? $"{Fmt.MoneyDelta(nation.WeeklyBalance)}/{Loc.Get("week")}"
            : Loc.Get("shell.aaa.intel_hidden");
        var balTok = showTreasury
            ? (nation.WeeklyBalance < -0.0005f ? "shell.state.negative_text" : "shell.state.positive")
            : "shell.paper_faint";
        var trTip = showTreasury
            ? $"{Loc.Get("Treasury")}: {Fmt.MoneyExact(nation.Treasury)}\n{Loc.Get("Balance")}: {Fmt.MoneyDelta(nation.WeeklyBalance)}/{Loc.Get("week")}"
            : Loc.Get("shell.aaa.intel_hidden");
        AddPillCard(ctx, grid, "icon_treasury", Loc.Get("Treasury"), trVal, trTok, balSub, balTok, trTip);

        var debt = SovereignFinance.Outstanding(nation);
        var debtPct = (debt / MathF.Max(0.01f, nation.Gdp)) * 100f;
        var inflPct = nation.Inflation * 100f;
        var debtInflVal = showTreasury ? $"{debtPct:0.#}% · {inflPct:0.0}%" : $"— · {inflPct:0.0}%";
        var debtInflTok = (showTreasury && debtPct > 90f) || nation.Inflation > 0.12f
            ? "shell.state.negative_text"
            : (showTreasury && debtPct > 55f) || nation.Inflation > 0.06f
                ? "shell.state.warning_text"
                : "shell.paper";
        var debtInflTip = showTreasury
            ? Loc.Format("shell.aaa.debt_ratio_tip_fmt", Fmt.MoneyExact(debt), $"{debtPct:0.1}%", Fmt.Percent(nation.Inflation))
            : $"{Loc.Get("Inflation")}: {Fmt.Percent(nation.Inflation)}";
        AddPillCard(ctx, grid, "icon_inflation", Loc.Get("shell.aaa.debt_and_inflation"),
            debtInflVal, debtInflTok, tooltip: debtInflTip);

        var reinfSub = nation.ReinforcementCapacity > 0
            ? Loc.Format("shell.aaa.reinf_short_fmt", Fmt.People(nation.ReinforcementCapacity))
            : "";
        var manTip = $"{Loc.Get("Manpower")}: {Fmt.PeopleExact(nation.ManpowerReserve)}" +
            (nation.ReinforcementCapacity > 0
                ? $"\n{Loc.Format("shell.clarity.reinf_rate_fmt", Fmt.People(nation.ReinforcementCapacity))}"
                : "");
        AddPillCard(ctx, grid, "icon_manpower", Loc.Get("Manpower"), Fmt.People(nation.ManpowerReserve),
            "shell.paper", reinfSub, "shell.state.positive", manTip);

        var (divs, hulls, wings) = CountArmedForces(world, nation.Id);
        var divInt = (int)MathF.Ceiling(divs);
        var forcesMain = $"{divInt} {Loc.Get("shell.div_short")}";
        var forcesSub = Loc.Format("shell.aaa.forces_sub_fmt", hulls, wings);
        var forcesTip = Loc.Format("shell.aaa.forces_tip_fmt", divInt, hulls, wings);
        AddPillCard(ctx, grid, "icon_military", Loc.Get("Armed forces"), forcesMain,
            "shell.paper", forcesSub, "shell.paper_faint", forcesTip);

        var prestigeVal = MathF.Round(nation.Prestige);
        var eqPrestige = MathF.Round(PrestigeSystem.ComputeEquilibrium(world, nation));
        var prestigeRounded = prestigeVal.ToString("N0", Loc.Culture);
        var eqRounded = eqPrestige.ToString("N0", Loc.Culture);
        var diff = eqPrestige - prestigeVal;
        var eqSubTok = diff > 2f ? "shell.state.positive"
            : diff < -2f ? "shell.state.warning_text"
            : "shell.paper_muted";
        var prestigeSub = diff > 2f
            ? Loc.Format("aaa.ui.prestige_delta_up_fmt", diff.ToString("N0", Loc.Culture), eqRounded)
            : diff < -2f
                ? Loc.Format("aaa.ui.prestige_delta_down_fmt", diff.ToString("N0", Loc.Culture), eqRounded)
                : Loc.Format("aaa.ui.prestige_steady_fmt", eqRounded);
        var prestigeTip = Loc.Format("ui.prestige.dossier_tip_fmt", prestigeRounded, eqRounded);
        AddPillCard(ctx, grid, "icon_victory", Loc.Get("Prestige"), prestigeRounded,
            "shell.brass_hi", prestigeSub, eqSubTok, prestigeTip);

        var totalTechs = TechnologyCatalog.All.Count;
        var activeTech = !string.IsNullOrEmpty(nation.CurrentResearch) ? TechnologyCatalog.Of(nation.CurrentResearch) : null;
        var techSub = activeTech is not null ? Loc.Get(activeTech.Name) : Loc.Get("shell.clarity.no_research");
        AddPillCard(ctx, grid, "icon_research", Loc.Get("Technologies"),
            $"{nation.Technologies.Count}/{totalTechs}",
            "shell.paper", techSub, "shell.paper_faint");
    }

    public static void AddTerritoryIndustryCard(
        ShellContext ctx, VBoxContainer root, Campaign world, Nation nation)
    {
        var states = world.StatesOf(nation);
        var totalBuildings = 0;
        var industryBuildings = 0;
        for (var i = 0; i < states.Length; i++)
        {
            foreach (var (kind, count) in states[i].Buildings)
            {
                if (count <= 0) continue;
                totalBuildings += count;
                if (IsIndustrialBuilding(kind)) industryBuildings += count;
            }
        }

        var running = 0;
        for (var i = 0; i < world.Construction.Count; i++)
            if (world.Construction[i].NationId == nation.Id) running++;
        var slots = ConstructionCost.Limit(world, nation, world.Defines.MaxConstructionProjects);
        var regionBadge = Loc.Format("shell.aaa.regions_count_fmt", states.Length);

        var card = ShellCountryDossierView.MakeSectionCard(
            ctx, root, "ShellOverviewTerritoryIndustryCard", "icon_industry",
            Loc.Get("shell.aaa.territory_industry"), regionBadge, "shell.brass_hi");

        if (card.GetParent<Control>() is { } panel)
        {
            panel.TooltipText = Ui.Tip(Loc.Format("shell.aaa.territory_tip_fmt",
                states.Length, totalBuildings, industryBuildings, running, slots,
                Fmt.Percent(nation.Literacy), Fmt.Percent(nation.StandardOfLiving)));
        }

        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 4);
        card.AddChild(grid);

        AddPillCard(ctx, grid, "icon_region", Loc.Get("Regions"),
            states.Length.ToString("N0", Loc.Culture));
        AddPillCard(ctx, grid, "icon_industry", Loc.Get("Buildings"),
            totalBuildings.ToString("N0", Loc.Culture), "shell.paper",
            Loc.Format("shell.aaa.industry_count_fmt", industryBuildings));
        AddPillCard(ctx, grid, "icon_shell_crane", Loc.Get("Construction"),
            Loc.Format("shell.aaa.construction_slots_fmt", running, slots),
            running > 0 ? "shell.state.info" : "shell.paper_muted",
            running == 0 ? Loc.Get("shell.aaa.idle_slots") : "");
        AddPillCard(ctx, grid, "icon_urbanisation", Loc.Get("Urbanisation"),
            Fmt.Percent(nation.Urbanisation), "shell.paper");

        ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_literacy", Loc.Get("Literacy"),
            nation.Literacy, Fmt.Percent(nation.Literacy), Ui.Colour("shell.brass_hi"), Loc.Get("Literacy"));
        ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_living", Loc.Get("Standard of living"),
            nation.StandardOfLiving, Fmt.Percent(nation.StandardOfLiving),
            ShellRegionVisualsView.GoodHighColor(nation.StandardOfLiving), Loc.Get("Standard of living"));
    }

    public static void AddSocietyDemographyCard(
        ShellContext ctx, VBoxContainer root, Campaign world, Nation nation)
    {
        var states = world.StatesOf(nation);
        double popSum = 0, youngSum = 0, workSum = 0, oldSum = 0, empSum = 0, needsSum = 0;
        long births = 0, deaths = 0, migration = 0;
        for (var i = 0; i < states.Length; i++)
        {
            var s = states[i];
            if (s is null || s.Population <= 0) continue;
            var pop = (double)s.Population;
            popSum += pop;
            youngSum += pop * Math.Max(0f, s.YoungShare);
            workSum += pop * Math.Max(0f, DemographicCycle.WorkingShare(s));
            oldSum += pop * Math.Max(0f, s.OldShare);
            empSum += pop * Math.Clamp(HouseholdEconomy.Employment(s), 0f, 1f);
            needsSum += pop * Math.Clamp(s.BasicNeedsCoverage, 0f, 1f);
            births += s.ReviewBirths;
            deaths += s.ReviewDeaths;
            migration += s.ReviewNetMigration;
        }

        float young, working, old, employment, basicNeeds;
        if (popSum > 0.0)
        {
            young = (float)(youngSum / popSum);
            working = (float)(workSum / popSum);
            old = (float)(oldSum / popSum);
            employment = (float)(empSum / popSum);
            basicNeeds = (float)(needsSum / popSum);
        }
        else
        {
            young = Math.Max(0f, nation.YoungShare);
            old = Math.Max(0f, nation.OldShare);
            working = Math.Max(0f, nation.WorkingShare > 0f ? nation.WorkingShare : 1f - young - old);
            employment = 0.9f;
            basicNeeds = 1f;
            births = nation.ReviewBirths;
            deaths = nation.ReviewDeaths;
            migration = nation.ReviewNetMigration;
        }

        var ageSum = young + working + old;
        if (ageSum > 0.001f) { young /= ageSum; working /= ageSum; old /= ageSum; }
        else { young = 0.30f; working = 0.61f; old = 0.09f; }

        var card = ShellCountryDossierView.MakeSectionCard(
            ctx, root, "ShellSocietyDemographyCard", "icon_population",
            Loc.Get("shell.aaa.society_demography"),
            Loc.Format("shell.aaa.regions_count_fmt", states.Length), "shell.brass_hi");

        if (card.GetParent<Control>() is { } panel)
        {
            panel.TooltipText = Ui.Tip(Loc.Format("shell.aaa.demography_tip_fmt",
                Fmt.PeopleExact(nation.Population), states.Length,
                Fmt.Percent(young), Fmt.Percent(working), Fmt.Percent(old),
                Fmt.Percent(employment), Fmt.Percent(basicNeeds)));
        }

        ShellCountryDossierView.AddKvRow(ctx, card, Loc.Get("Population"),
            Fmt.PeopleExact(nation.Population), "shell.paper");
        ShellCountryDossierView.AddKvRow(ctx, card, Loc.Get("Regions"),
            states.Length.ToString("N0", Loc.Culture), "shell.paper_muted");

        card.AddChild(ShellCountryDossierView.MakeLabel(ctx, Loc.Get("shell.aaa.age_structure"),
            "shell.sans", 11, "shell.paper_muted", clip: true));

        var ageSegments = new (string Name, float Share, string Tok)[]
        {
            (Loc.Get("shell.reg_age_young"), young, "shell.state.positive"),
            (Loc.Get("shell.reg_age_work"), working, "shell.state.info"),
            (Loc.Get("shell.reg_age_old"), old, "shell.state.warning")
        };
        AddAgeStackedBar(ctx, card, ageSegments);

        ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_industry", Loc.Get("Employment"),
            employment, Fmt.Percent(employment), ShellRegionVisualsView.GoodHighColor(employment), Loc.Get("Employment"));
        ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_trade", Loc.Get("Basic needs covered"),
            basicNeeds, Fmt.Percent(basicNeeds), ShellRegionVisualsView.GoodHighColor(basicNeeds), Loc.Get("Basic needs covered"));

        var netVital = births - deaths;
        var netVitalStr = netVital == 0 ? "0" : Fmt.PeopleDelta(netVital);
        ShellCountryDossierView.AddKvRow(ctx, card, Loc.Get("shell.aaa.net_vital_label"),
            Loc.Format("shell.aaa.vital_balance_fmt", netVitalStr, Fmt.People(births), Fmt.People(deaths)),
            netVital < 0 ? "shell.state.warning_text" : "shell.state.positive");
        ShellCountryDossierView.AddKvRow(ctx, card, Loc.Get("Net migration"),
            migration == 0 ? "0" : Fmt.PeopleDelta(migration),
            migration < 0 ? "shell.state.warning_text" : "shell.paper");
    }

    internal static void AddPillCard(
        ShellContext ctx, GridContainer parent, string iconName, string label, string value,
        string valTok = "shell.paper", string subValue = "", string subTok = "shell.paper_faint", string tooltip = "")
    {
        var pill = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true,
            TooltipText = string.IsNullOrEmpty(tooltip) ? "" : Ui.Tip(tooltip)
        };
        pill.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 7, 5, 7, 5));
        parent.AddChild(pill);

        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 6);
        pill.AddChild(row);

        if (ctx.Icon(iconName) is { } tex)
        {
            var ico = new TextureRect
            {
                Texture = tex,
                CustomMinimumSize = new Vector2(16, 16),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            ShellContext.ApplyIconRelief(ico, Ui.Colour("shell.icon"), 0.65f);
            row.AddChild(ico);
        }

        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        col.AddThemeConstantOverride("separation", 1);
        row.AddChild(col);
        col.AddChild(ShellCountryDossierView.MakeLabel(ctx, label, "shell.sans", 10, "shell.paper_muted", clip: true));
        col.AddChild(ShellCountryDossierView.MakeLabel(ctx, value, "shell.sans", 13, valTok, clip: true, tabular: true));
        if (!string.IsNullOrEmpty(subValue))
            col.AddChild(ShellCountryDossierView.MakeLabel(ctx, subValue, "shell.sans", 10, subTok, clip: true, tabular: true));
    }

    private static void AddAgeStackedBar(
        ShellContext ctx, VBoxContainer parent, (string Name, float Share, string Tok)[] segments)
    {
        var barPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, 10),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        barPanel.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well", "shell.engrave", 1, 2, 1, 1, 1, 1));
        parent.AddChild(barPanel);

        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 1);
        barPanel.AddChild(row);
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg.Share <= 0f) continue;
            row.AddChild(new ColorRect
            {
                Color = Ui.Colour(seg.Tok),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = Math.Max(0.01f, seg.Share),
                TooltipText = Ui.Tip(Loc.Format("shell.aaa.age_segment_tip_fmt", seg.Name, Fmt.Percent(seg.Share)))
            });
        }

        var legend = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        legend.AddThemeConstantOverride("h_separation", 8);
        legend.AddThemeConstantOverride("v_separation", 2);
        parent.AddChild(legend);
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            var chip = new HBoxContainer { ClipContents = true };
            chip.AddThemeConstantOverride("separation", 4);
            chip.AddChild(new ColorRect
            {
                Color = Ui.Colour(seg.Tok),
                CustomMinimumSize = new Vector2(8, 8),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            });
            chip.AddChild(ShellCountryDossierView.MakeLabel(ctx,
                $"{seg.Name} {Fmt.Percent(seg.Share)}", "shell.sans", 10, "shell.paper_muted", tabular: true));
            legend.AddChild(chip);
        }
    }

    private static (float Divisions, int Hulls, int AirWings) CountArmedForces(Campaign world, int nationId)
    {
        var divs = 0f;
        for (var i = 0; i < world.Armies.Count; i++)
            if (world.Armies[i].NationId == nationId) divs += world.Armies[i].Divisions;

        var hulls = 0;
        for (var i = 0; i < world.Fleets.Count; i++)
            if (world.Fleets[i].NationId == nationId && world.Fleets[i].Hulls > 0) hulls += world.Fleets[i].Hulls;

        var wings = 0;
        for (var i = 0; i < world.SupportFormations.Count; i++)
        {
            var f = world.SupportFormations[i];
            if (f.NationId != nationId || f.Strength <= 0.05f) continue;
            if (SupportTypeCatalog.TryGet(f.TypeId, out var type) && type.Kind == SupportKind.Air)
                wings++;
        }
        return (divs, hulls, wings);
    }

    private static bool IsIndustrialBuilding(BuildingKind kind) => kind switch
    {
        BuildingKind.CivilianIndustry or BuildingKind.ArmsIndustry or BuildingKind.ExtractionIndustry or
        BuildingKind.PowerPlant or BuildingKind.Refinery or BuildingKind.ChemicalPlant or
        BuildingKind.Smelter or BuildingKind.ElectronicsPlant or BuildingKind.Shipyard => true,
        _ => false
    };
}
