using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Renders regional tabs (Overview, Population, Build, Recruit) with telemetry meters,
/// stacked composition bars (cultures, faiths, social classes), building/deposit grids, and
/// delegates KPI header cards, terrain cards, and flagged garrisons to <see cref="ShellRegionKpiCardsView"/>.
/// </summary>
internal static class ShellRegionVisualsView
{
    private static readonly string[] MetricSwatchTokens =
    {
        "shell.brass_hi", "shell.state.info", "shell.state.positive",
        "shell.state.warning", "shell.state.crisis", "shell.paper_faint"
    };

    public readonly record struct StackSegment(string Label, double Value, Color Swatch, string Tooltip);

    public static void PopulateRegion(
        ShellContext ctx, VBoxContainer bodyRows, Label feedbackLabel, Campaign world,
        Nation owner, ScenarioState state, int provinceId, int provCount,
        int activeTab, bool isOwn, Action onQueued)
    {
        if (!isOwn && activeTab == 0)
        {
            var hint = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
            hint.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 8, 4, 8, 4));
            hint.AddChild(MakeLabel(ctx, Loc.Get("shell.reg_read_only_hint"), "shell.sans", 11, "shell.paper_faint", clip: true));
            bodyRows.AddChild(hint);
        }

        if (activeTab == 0) PopulateOverviewTab(ctx, bodyRows, world, owner, state, provinceId, provCount);
        else if (activeTab == 1) PopulatePopulationTab(ctx, bodyRows, world, owner, state);
        else if (activeTab == 2) PopulateBuildTab(ctx, bodyRows, feedbackLabel, world, owner, state, isOwn, onQueued);
        else PopulateRecruitTab(ctx, bodyRows, world, owner, state, provinceId, provCount, isOwn);
    }

    private static void PopulateOverviewTab(
        ShellContext ctx, VBoxContainer bodyRows, Campaign world, Nation owner, ScenarioState state, int provinceId, int provCount)
    {
        ShellRegionKpiCardsView.AddRegionKpiCards(ctx, bodyRows, world, owner, state, provCount);

        AddSectionHeader(ctx, bodyRows, Loc.Get("shell.reg_terrain_ground"));
        ShellRegionKpiCardsView.AddTerrainCard(ctx, bodyRows, world, owner, state, provinceId, provCount);

        AddSectionHeader(ctx, bodyRows, Loc.Get("Region"), Fmt.PeopleExact(state.Population));
        AddMeterRow(ctx, bodyRows, "icon_economy", Loc.Get("Standard of living"), state.StandardOfLiving,
            Fmt.Percent(state.StandardOfLiving), GoodHighColor(state.StandardOfLiving), Loc.Get("Standard of living"));
        AddMeterRow(ctx, bodyRows, "icon_industry", Loc.Get("Urbanisation"), state.Urbanization,
            Fmt.Percent(state.Urbanization), Ui.Colour("shell.state.info"), Loc.Get("Urbanisation"));
        AddMeterRow(ctx, bodyRows, "icon_research", Loc.Get("Literacy"), state.Literacy,
            Fmt.Percent(state.Literacy), Ui.Colour("shell.brass_hi"), Loc.Get("Literacy"));
        AddMeterRow(ctx, bodyRows, "icon_wars", Loc.Get("Radicalism"), state.Radicalism,
            Fmt.Percent(state.Radicalism), DangerHighColor(state.Radicalism), Loc.Get("Radicalism"));

        AddSectionHeader(ctx, bodyRows, Loc.Get("shell.reg_demographics"));
        AddDemographicsBars(ctx, bodyRows, state, owner, maxChips: 4, includeItemRows: false);

        AddSectionHeader(ctx, bodyRows, Loc.Get("shell.reg_buildings_industry"));
        AddBuildingsAndQueue(ctx, bodyRows, world, state, showQueueFirst: true);
    }

    private static void PopulatePopulationTab(
        ShellContext ctx, VBoxContainer bodyRows, Campaign world, Nation owner, ScenarioState state)
    {
        AddSectionHeader(ctx, bodyRows, Loc.Get("Population"), Fmt.PeopleExact(state.Population));
        var working = DemographicCycle.WorkingShare(state);
        var ageSegments = new List<StackSegment>(3)
        {
            new(Loc.Get("shell.reg_age_young"), Math.Max(0f, state.YoungShare), Ui.Colour("shell.state.positive"),
                $"{Loc.Get("shell.reg_age_young")}: {Fmt.Percent(state.YoungShare)}"),
            new(Loc.Get("shell.reg_age_work"), Math.Max(0f, working), Ui.Colour("shell.state.info"),
                $"{Loc.Get("shell.reg_age_work")}: {Fmt.Percent(working)}"),
            new(Loc.Get("shell.reg_age_old"), Math.Max(0f, state.OldShare), Ui.Colour("shell.state.warning"),
                $"{Loc.Get("shell.reg_age_old")}: {Fmt.Percent(state.OldShare)}")
        };
        AddStackedBar(ctx, bodyRows, ageSegments, height: 11f, maxChips: 3, showLegend: true);

        if (state.LastHouseholdReviewDay >= 0 && world.Defines.HouseholdDynamicsEnabled)
        {
            var emp = HouseholdEconomy.Employment(state);
            AddMeterRow(ctx, bodyRows, "icon_industry", Loc.Get("Employment"), emp, Fmt.Percent(emp), GoodHighColor(emp), Loc.Get("Employment"));
            AddMeterRow(ctx, bodyRows, "icon_trade", Loc.Get("Basic needs covered"), state.BasicNeedsCoverage,
                Fmt.Percent(state.BasicNeedsCoverage), GoodHighColor(state.BasicNeedsCoverage), Loc.Get("Basic needs covered"));
            AddMeterRow(ctx, bodyRows, "icon_wars", Loc.Get("Livelihood stress"), state.LivelihoodStress,
                Fmt.Percent(state.LivelihoodStress), DangerHighColor(state.LivelihoodStress), Loc.Get("Livelihood stress"));
        }

        if (owner is not null)
        {
            var (birth, death, _) = HouseholdEconomy.VitalRates(world, owner, state);
            var delta = birth - death;
            AddKvRow(ctx, bodyRows, Loc.Get("Net migration"),
                state.ReviewNetMigration == 0 ? "0" : Fmt.PeopleDelta(state.ReviewNetMigration),
                state.ReviewNetMigration < 0 ? "shell.state.warning_text" : "shell.paper");
            AddKvRow(ctx, bodyRows, Loc.Get("Births / deaths, last review"),
                $"{delta:+0.00%;-0.00%;0.00%} (+{Fmt.People(state.ReviewBirths)} / −{Fmt.People(state.ReviewDeaths)})",
                death > birth ? "shell.state.warning_text" : "shell.state.positive");
        }
        if (state.BrainDrainPressure >= 0.02f)
        {
            AddMeterRow(ctx, bodyRows, "icon_research", Loc.Get("Brain drain"), state.BrainDrainPressure,
                Fmt.Percent(state.BrainDrainPressure), DangerHighColor(state.BrainDrainPressure), Loc.Get("Brain drain"));
        }

        var foreign = EconomySystem.ForeignShare(state, owner?.Tag ?? "");
        AddMeterRow(ctx, bodyRows, "icon_diplomacy", Loc.Get("Minorities"), foreign, Fmt.Percent(foreign), DangerHighColor(foreign), Loc.Get("Minorities"));

        AddDemographicsBars(ctx, bodyRows, state, owner, maxChips: 6, includeItemRows: true);
    }

    private static void PopulateBuildTab(
        ShellContext ctx, VBoxContainer bodyRows, Label feedbackLabel, Campaign world,
        Nation owner, ScenarioState state, bool isOwn, Action onQueued)
    {
        if (isOwn && owner is not null)
        {
            var running = world.Construction.Count(p => p.NationId == owner.Id);
            var slots = ConstructionCost.Limit(world, owner, world.Defines.MaxConstructionProjects);
            AddSectionHeader(ctx, bodyRows, Loc.Get("shell.tab_build"), Loc.Format("shell.ws.reg.build_queue_slots_fmt", running, slots));
            ShellRegionBuildTabView.PopulateBuildRows(ctx, bodyRows, feedbackLabel, world, owner, state, onQueued);
        }

        AddBuildingsAndQueue(ctx, bodyRows, world, state, showQueueFirst: true);
        AddDeposits(ctx, bodyRows, state);
    }

    private static void PopulateRecruitTab(
        ShellContext ctx, VBoxContainer bodyRows, Campaign world, Nation owner,
        ScenarioState state, int provinceId, int provCount, bool isOwn)
    {
        AddSectionHeader(ctx, bodyRows, Loc.Get("shell.reg_terrain_ground"));
        ShellRegionKpiCardsView.AddTerrainCard(ctx, bodyRows, world, owner, state, provinceId, provCount);

        AddSectionHeader(ctx, bodyRows, Loc.Get("shell.reg_garrison"));
        ShellRegionKpiCardsView.AddGarrisonList(ctx, bodyRows, world, state);

        if (isOwn && owner is not null)
        {
            var reserve = Math.Max(0L, owner.ManpowerReserve);
            var denom = Math.Max(1f, owner.Population * 0.06f);
            AddSectionHeader(ctx, bodyRows, Loc.Get("Manpower"), Fmt.People(reserve));
            AddMeterRow(ctx, bodyRows, "icon_military", Loc.Get("Manpower"),
                Math.Clamp(reserve / denom, 0.04f, 1f), Fmt.People(reserve), Ui.Colour("shell.brass_hi"), Loc.Get("Manpower"));
        }
    }

    public static void AddSectionHeader(ShellContext ctx, VBoxContainer parent, string title, string rightNote = "")
    {
        var h = new HBoxContainer { ClipContents = true };
        h.AddThemeConstantOverride("separation", 6);
        h.AddChild(MakeLabel(ctx, title, "shell.serif_semibold", 13, "shell.paper_muted", clip: true));
        h.AddChild(new ColorRect
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(10, 1),
            Color = Ui.Colour("shell.engrave")
        });
        if (!string.IsNullOrEmpty(rightNote))
        {
            var r = MakeLabel(ctx, rightNote, "shell.sans", 11, "shell.paper_faint", tabular: true);
            r.HorizontalAlignment = HorizontalAlignment.Right;
            h.AddChild(r);
        }
        parent.AddChild(h);
    }

    internal static void AddMeterRow(
        ShellContext ctx, VBoxContainer parent, string iconName, string label,
        float fraction01, string valueText, Color fillColor, string tooltip)
    {
        var clamped = Math.Clamp(fraction01, 0f, 1f);
        var row = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 22), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true, TooltipText = Ui.Tip(tooltip)
        };
        row.AddThemeConstantOverride("separation", 6);
        parent.AddChild(row);

        row.AddChild(new TextureRect
        {
            Texture = ctx.Icon(iconName), CustomMinimumSize = new Vector2(14, 14),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.paper_muted"), MouseFilter = Control.MouseFilterEnum.Ignore
        });

        var lbl = MakeLabel(ctx, label, "shell.sans", 12, "shell.paper_muted", clip: true);
        lbl.CustomMinimumSize = new Vector2(76, 0);
        lbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        lbl.SizeFlagsStretchRatio = 1.15f;
        row.AddChild(lbl);

        var trackWrap = new PanelContainer
        {
            CustomMinimumSize = new Vector2(56, 11), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, SizeFlagsStretchRatio = 1f,
            ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore
        };
        trackWrap.AddThemeStyleboxOverride("panel", BuildTrackStyle(ctx));
        row.AddChild(trackWrap);

        var barRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        barRow.AddThemeConstantOverride("separation", 0);
        trackWrap.AddChild(barRow);
        var fillRatio = Math.Max(0.01f, clamped);
        barRow.AddChild(new ColorRect { Color = fillColor, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = fillRatio, MouseFilter = Control.MouseFilterEnum.Ignore });
        barRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = Math.Max(0.001f, 1f - fillRatio), MouseFilter = Control.MouseFilterEnum.Ignore });

        var val = MakeLabel(ctx, valueText, "shell.sans", 11, "shell.paper", tabular: true);
        val.CustomMinimumSize = new Vector2(44, 0);
        val.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(val);
    }

    private static StyleBox BuildTrackStyle(ShellContext ctx)
    {
        var texPath = SpriteCatalog.TextureOf("shell_meter_track", "res://game/gfx/interface/shell/meter_track_engraved.png");
        if (ctx.LoadTexture(texPath) is { } tex)
        {
            return new StyleBoxTexture
            {
                Texture = tex, TextureMarginLeft = 6, TextureMarginRight = 6, TextureMarginTop = 3, TextureMarginBottom = 3,
                ContentMarginLeft = 2, ContentMarginRight = 2, ContentMarginTop = 2, ContentMarginBottom = 2
            };
        }
        return ctx.BorderedBox("shell.well", "shell.engrave", 1, 2, 2, 2, 2, 2);
    }

    private static void AddDemographicsBars(
        ShellContext ctx, VBoxContainer bodyRows, ScenarioState state, Nation owner, int maxChips, bool includeItemRows)
    {
        var cultures = EconomySystem.Cultures(state);
        if (cultures.Count > 0)
        {
            if (includeItemRows) AddSectionHeader(ctx, bodyRows, Loc.Get("PEOPLES"));
            var segs = new List<StackSegment>(cultures.Count);
            for (var i = 0; i < cultures.Count; i++)
            {
                var c = cultures[i];
                var def = DemographyCatalog.Culture(c.Culture);
                var nm = DemographyCatalog.CultureName(c.Culture);
                var sw = ParseSwatch(def?.Colour, FallbackSwatch(i));
                var share = c.Size / (double)Math.Max(1L, state.Population);
                segs.Add(new StackSegment($"{nm} {share:P0}", c.Size, sw, $"{nm}: {Fmt.PeopleExact(c.Size)} ({share:P1})"));
            }
            AddStackedBar(ctx, bodyRows, segs, height: 10f, maxChips: maxChips, showLegend: !includeItemRows);
            if (includeItemRows)
            {
                for (var i = 0; i < Math.Min(5, cultures.Count); i++)
                {
                    var c = cultures[i];
                    var sw = ParseSwatch(DemographyCatalog.Culture(c.Culture)?.Colour, FallbackSwatch(i));
                    AddSwatchKvRow(ctx, bodyRows, sw, DemographyCatalog.CultureName(c.Culture),
                        $"{c.Size / (double)Math.Max(1L, state.Population):P1} · {Fmt.People(c.Size)}");
                }
            }
        }

        var faiths = DemographyCatalog.DenominationsIn(state, state.NationId);
        if (faiths.Count > 0)
        {
            if (includeItemRows) AddSectionHeader(ctx, bodyRows, Loc.Get("FAITHS"));
            var fSegs = new List<StackSegment>(faiths.Count);
            for (var i = 0; i < faiths.Count; i++)
            {
                var f = faiths[i];
                var def = DemographyCatalog.Religion(f.Denomination);
                var nm = DemographyCatalog.ReligionName(f.Denomination);
                var sw = ParseSwatch(def?.Colour, FallbackSwatch(i + 2));
                var share = f.Size / (double)Math.Max(1L, state.Population);
                fSegs.Add(new StackSegment($"{nm} {share:P0}", f.Size, sw, $"{nm}: {Fmt.PeopleExact(f.Size)} ({share:P1})"));
            }
            AddStackedBar(ctx, bodyRows, fSegs, height: 10f, maxChips: maxChips, showLegend: !includeItemRows);
            if (includeItemRows)
            {
                for (var i = 0; i < Math.Min(5, faiths.Count); i++)
                {
                    var f = faiths[i];
                    var sw = ParseSwatch(DemographyCatalog.Religion(f.Denomination)?.Colour, FallbackSwatch(i + 2));
                    AddSwatchKvRow(ctx, bodyRows, sw, DemographyCatalog.ReligionName(f.Denomination),
                        $"{f.Size / (double)Math.Max(1L, state.Population):P1} · {Fmt.People(f.Size)}");
                }
            }
        }

        long lower = 0, middle = 0, upper = 0;
        foreach (var g in state.PopulationGroups)
        {
            if (g.Size <= 0) continue;
            if (g.Class == SocialClass.Lower) lower += g.Size;
            else if (g.Class == SocialClass.Middle) middle += g.Size;
            else upper += g.Size;
        }
        var classTotal = lower + middle + upper;
        if (classTotal > 0)
        {
            if (includeItemRows) AddSectionHeader(ctx, bodyRows, Loc.Get("shell.ws.reg.classes"));
            var cSegs = new List<StackSegment>(3)
            {
                new($"{Loc.Get("shell.ws.reg.class_lower")} {lower / (double)classTotal:P0}", lower, Ui.Colour("shell.paper_faint"),
                    $"{Loc.Get("shell.ws.reg.class_lower")}: {Fmt.PeopleExact(lower)} ({lower / (double)classTotal:P1})"),
                new($"{Loc.Get("shell.ws.reg.class_middle")} {middle / (double)classTotal:P0}", middle, Ui.Colour("shell.state.info"),
                    $"{Loc.Get("shell.ws.reg.class_middle")}: {Fmt.PeopleExact(middle)} ({middle / (double)classTotal:P1})"),
                new($"{Loc.Get("shell.ws.reg.class_upper")} {upper / (double)classTotal:P0}", upper, Ui.Colour("shell.brass_hi"),
                    $"{Loc.Get("shell.ws.reg.class_upper")}: {Fmt.PeopleExact(upper)} ({upper / (double)classTotal:P1})")
            };
            AddStackedBar(ctx, bodyRows, cSegs, height: 9f, maxChips: 3, showLegend: true);
        }
    }

    private static void AddStackedBar(
        ShellContext ctx, VBoxContainer parent, IReadOnlyList<StackSegment> segments,
        float height = 10f, int maxChips = 5, bool showLegend = true)
    {
        var total = segments.Sum(s => Math.Max(0.0, s.Value));
        if (total <= 0.0) return;

        var barPanel = new PanelContainer { CustomMinimumSize = new Vector2(0, height), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        barPanel.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well", "shell.engrave", 1, 2, 1, 1, 1, 1));
        parent.AddChild(barPanel);

        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 1);
        barPanel.AddChild(row);
        foreach (var seg in segments.Where(s => s.Value > 0.0))
        {
            row.AddChild(new ColorRect
            {
                Color = seg.Swatch, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = Math.Max(0.01f, (float)(seg.Value / total)),
                TooltipText = Ui.Tip(seg.Tooltip)
            });
        }
        if (!showLegend) return;

        var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        flow.AddThemeConstantOverride("h_separation", 8);
        flow.AddThemeConstantOverride("v_separation", 2);
        parent.AddChild(flow);
        foreach (var seg in segments.Where(s => s.Value > 0.0).Take(maxChips))
        {
            var chip = new HBoxContainer { TooltipText = Ui.Tip(seg.Tooltip) };
            chip.AddThemeConstantOverride("separation", 4);
            chip.AddChild(new ColorRect { Color = seg.Swatch, CustomMinimumSize = new Vector2(8, 8), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
            chip.AddChild(MakeLabel(ctx, seg.Label, "shell.sans", 11, "shell.paper_muted", tabular: true));
            flow.AddChild(chip);
        }
    }

    private static void AddBuildingsAndQueue(
        ShellContext ctx, VBoxContainer bodyRows, Campaign world, ScenarioState state, bool showQueueFirst)
    {
        var queue = world.Construction.Where(p => p.StateMapIndex == state.MapIndex).OrderBy(p => p.Id).ToArray();
        if (showQueueFirst && queue.Length > 0)
        {
            AddSectionHeader(ctx, bodyRows, Loc.Get("shell.reg_construction_active"), queue.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var proj in queue.Take(5))
            {
                var def = BuildingCatalog.Get(proj.Building);
                var bldKey = $"icon_building_{proj.Building.ToString().ToLowerInvariant()}";
                AddMeterRow(ctx, bodyRows, ctx.Icon(bldKey) is not null ? bldKey : "icon_industry", Loc.Get(def.Name), proj.Progress,
                    Fmt.Percent(proj.Progress), Ui.Colour("shell.state.info"), $"{Loc.Get(def.Name)} · {Fmt.Money(proj.Cost)} · {proj.DurationDays}");
            }
        }

        var built = BuildingCatalog.All
            .Select(k => (Kind: k, Count: state.Buildings.GetValueOrDefault(k)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ToArray();
        if (built.Length == 0)
        {
            bodyRows.AddChild(MakeLabel(ctx, Loc.Get("shell.reg_no_buildings"), "shell.sans", 12, "shell.paper_faint"));
            return;
        }

        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 4);
        bodyRows.AddChild(grid);

        foreach (var entry in built.Take(8))
        {
            var def = BuildingCatalog.Get(entry.Kind);
            var chip = new PanelContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true,
                TooltipText = Ui.Tip($"{Loc.Get(def.Name)} ×{entry.Count}\n{Loc.Get(def.Description)}")
            };
            chip.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.plate_bottom", "shell.engrave", 1, 2, 6, 4, 6, 4));
            grid.AddChild(chip);

            var r = new HBoxContainer { ClipContents = true };
            r.AddThemeConstantOverride("separation", 4);
            chip.AddChild(r);
            var bldIco = ctx.Icon($"icon_building_{entry.Kind.ToString().ToLowerInvariant()}") ?? ctx.Icon("icon_industry");
            if (bldIco is not null)
            {
                r.AddChild(new TextureRect
                {
                    Texture = bldIco, CustomMinimumSize = new Vector2(16, 16),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
                });
            }
            r.AddChild(MakeLabel(ctx, Loc.Get(def.Name), "shell.sans", 11, "shell.paper_muted", clip: true, expand: true));
            r.AddChild(MakeLabel(ctx, $"×{entry.Count}", "shell.sans", 11, "shell.paper", tabular: true));
        }
    }

    private static void AddDeposits(ShellContext ctx, VBoxContainer bodyRows, ScenarioState state)
    {
        var deposits = state.ResourceDeposits.Where(d => d.Value > 0.05f).OrderByDescending(d => d.Value).Take(6).ToArray();
        if (deposits.Length == 0) return;

        AddSectionHeader(ctx, bodyRows, Loc.Get("shell.reg_deposits"));
        var maxVal = Math.Max(1f, deposits[0].Value);
        foreach (var d in deposits)
        {
            var def = CommodityCatalog.Get(d.Key);
            var gKey = $"icon_good_{d.Key.ToString().ToLowerInvariant()}";
            AddMeterRow(ctx, bodyRows, ctx.Icon(gKey) is not null ? gKey : "icon_trade", Loc.Get(def.Name),
                Math.Clamp(d.Value / maxVal, 0.08f, 1f), Fmt.Number(d.Value), Ui.Colour("shell.brass_hi"), Loc.Get(def.Name));
        }
    }

    private static void AddSwatchKvRow(ShellContext ctx, VBoxContainer bodyRows, Color swatch, string key, string value, string valueTok = "shell.paper")
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 20), ClipContents = true };
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(new ColorRect { Color = swatch, CustomMinimumSize = new Vector2(9, 9), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        row.AddChild(MakeLabel(ctx, key, "shell.sans", 11, "shell.paper_muted", clip: true, expand: true));
        var v = MakeLabel(ctx, value, "shell.sans", 11, valueTok, tabular: true);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(v);
        bodyRows.AddChild(row);
    }

    private static void AddKvRow(ShellContext ctx, VBoxContainer bodyRows, string key, string value, string valueTok = "shell.paper")
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 20), ClipContents = true };
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(MakeLabel(ctx, key, "shell.sans", 11, "shell.paper_muted", clip: true, expand: true));
        var v = MakeLabel(ctx, value, "shell.sans", 11, valueTok, tabular: true);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(v);
        bodyRows.AddChild(row);
    }

    internal static Label MakeLabel(
        ShellContext ctx, string text, string fontKey, int size, string colorTok,
        bool clip = false, bool expand = false, bool tabular = false)
    {
        var lbl = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.Off, ClipText = clip };
        if (expand) lbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (clip) lbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        lbl.AddThemeFontOverride("font", ctx.Font(fontKey, tabular));
        lbl.AddThemeFontSizeOverride("font_size", size);
        lbl.AddThemeColorOverride("font_color", Ui.Colour(colorTok));
        return lbl;
    }

    internal static Color StabilityColor(float v) => v < 0.35f ? Ui.Colour("shell.state.negative") : v < 0.55f ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.positive");
    internal static Color GoodHighColor(float v) => v < 0.35f ? Ui.Colour("shell.state.negative") : v < 0.65f ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.positive");
    internal static Color DangerHighColor(float v) => v > 0.45f ? Ui.Colour("shell.state.negative") : v > 0.25f ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.positive");

    private static Color FallbackSwatch(int idx) => Ui.Colour(MetricSwatchTokens[Math.Abs(idx) % MetricSwatchTokens.Length]);

    private static Color ParseSwatch(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return Color.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
