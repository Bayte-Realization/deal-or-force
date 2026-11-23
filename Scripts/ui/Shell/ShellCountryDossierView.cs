using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Wave 2 sculpted visual-first Country Dossier tabs (Overview, Society, Decisions, Staff, Diplomacy)
/// inside <see cref="ShellWorkPanelView"/>. Every tab uses padded, clipped inlaid cards (>= 14px
/// scrollbar clearance), visual composition bars, KPI tiles, and zero raw Color literals.
/// </summary>
internal static class ShellCountryDossierView
{
    private const int ScrollClearanceRight = 14;
    private static readonly string[] SwatchPalette =
    {
        "shell.brass_hi", "shell.state.info", "shell.state.positive",
        "shell.state.warning", "shell.state.crisis", "shell.paper_faint"
    };

    private static VBoxContainer CreatePaddedRoot(VBoxContainer bodyRows, string name)
    {
        var pad = new MarginContainer
        {
            Name = name,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        pad.AddThemeConstantOverride("margin_left", 2);
        pad.AddThemeConstantOverride("margin_right", ScrollClearanceRight);
        pad.AddThemeConstantOverride("margin_top", 2);
        pad.AddThemeConstantOverride("margin_bottom", 6);
        bodyRows.AddChild(pad);

        var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        root.AddThemeConstantOverride("separation", 7);
        pad.AddChild(root);
        return root;
    }

    public static void PopulateRulerCard(
        ShellContext ctx, VBoxContainer parent, Campaign world, Nation nation)
    {
        var ruler = Rulers.HeadOfState(world, nation);
        var gov = Rulers.HeadOfGovernment(world, nation);

        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        card.AddThemeStyleboxOverride("panel", ChromePlateArt.BuildCardWrapperStyle(8, 7, 8, 7) ?? ctx.BorderedBox(
            "shell.plate_bottom", "shell.engrave", 1, 3, 8, 7, 8, 7));
        parent.AddChild(card);

        var row = new HBoxContainer { ClipContents = true };
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
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            ClipContents = true
        };
        col.AddThemeConstantOverride("separation", 2);
        row.AddChild(col);

        var titleText = string.IsNullOrWhiteSpace(ruler.Title) ? Loc.Get("Head of state") : Loc.Get(ruler.Title);
        var metaTop = ruler.Age > 0 ? $"{titleText} · {Loc.Format("shell.ruler_age_fmt", ruler.Age)}" : titleText;
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
        ShellContext ctx, VBoxContainer bodyRows, Campaign world, Nation nation,
        bool isOwn, Action<Nation> openCountry, Action openStaffTab = null)
    {
        var root = CreatePaddedRoot(bodyRows, "ShellOverviewPaddedRoot");
        var observing = ctx.IsObserverMode?.Invoke() ?? world.ObserverMode;
        if (observing)
            AddInfoBanner(ctx, root, "icon_shell_eye", Loc.Get("shell.obs.readonly_banner"), "shell.state.info");

        PopulateRulerCard(ctx, root, world, nation);

        // State Identity & Research Inlaid Card
        var stateCard = MakeSectionCard(ctx, root, "ShellOverviewStateCard", "icon_research",
            Loc.Get("shell.clarity.state_pulse"), null, "shell.state.info");
        ShellCountryOutlookView.AddCapitalRow(ctx, stateCard, world, nation);
        var identityBrief = StateIdentity.Brief(world, nation);
        if (!string.IsNullOrWhiteSpace(identityBrief))
            AddKvRow(ctx, stateCard, Loc.Get("shell.clarity.state_identity"), identityBrief, "shell.paper_muted");

        if (isOwn || observing || CampaignIntel.PlayerCanInspect(world, nation.Id))
        {
            ShellCountryOutlookView.AddOverviewStabilityDragRow(ctx, stateCard, world, nation, true);
            var tech = !string.IsNullOrEmpty(nation.CurrentResearch) ? TechnologyCatalog.Of(nation.CurrentResearch) : null;
            var techName = tech is not null ? Loc.Get(tech.Name) : Loc.Get("shell.clarity.no_research");
            var progress = tech is not null ? nation.ResearchProgress.GetValueOrDefault(tech.Id) : 0f;
            var frac = tech is not null ? Math.Clamp(progress / MathF.Max(1f, tech.Cost), 0f, 1f) : 0f;
            AddKvRow(ctx, stateCard, Loc.Get("shell.clarity.active_research"),
                tech is not null ? $"{techName} ({(int)MathF.Round(frac * 100f)}%)" : techName,
                tech is not null ? "shell.state.info" : "shell.state.warning_text");
            if (tech is not null)
                AddProgressBar(stateCard, frac, "shell.state.info");
        }

        // At-a-Glance Diplomatic Pulse Strip (Wars, Allies, Subjects, Blocs)
        var warCount = Warfare.OpenWars(world).Count(w => !w.Ended && w.Involves(nation.Id));
        var allyCount = AllianceSystem.AlliesOf(world, nation).Count;
        var subjCount = SubjectSystem.SubjectsOf(world, nation).Count;
        var blocCount = world.FeatureEnabled("organizations") ? InternationalOrganizations.Memberships(world, nation.Id).Count() : 0;

        var dipStrip = MakeSectionCard(ctx, root, "ShellOverviewDipPulse", "icon_diplomacy",
            Loc.Get("shell.clarity.diplomatic_pulse"), null, warCount > 0 ? "shell.state.negative_text" : "shell.brass_hi");
        var pulseGrid = new GridContainer { Columns = 4, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        pulseGrid.AddThemeConstantOverride("h_separation", 4);
        dipStrip.AddChild(pulseGrid);
        AddMiniKpi(ctx, pulseGrid, Loc.Get("Wars"), warCount.ToString(CultureInfo.InvariantCulture), warCount > 0 ? "shell.state.negative_text" : "shell.paper_muted");
        AddMiniKpi(ctx, pulseGrid, Loc.Get("shell.dip.allies"), allyCount.ToString(CultureInfo.InvariantCulture), allyCount > 0 ? "shell.state.positive" : "shell.paper_muted");
        AddMiniKpi(ctx, pulseGrid, Loc.Get("shell.dip.subjects"), subjCount.ToString(CultureInfo.InvariantCulture), subjCount > 0 ? "shell.brass_hi" : "shell.paper_muted");
        AddMiniKpi(ctx, pulseGrid, Loc.Get("shell.dip.blocs"), blocCount.ToString(CultureInfo.InvariantCulture), blocCount > 0 ? "shell.state.info" : "shell.paper_muted");

        // Standing KPIs Card
        var kpiCard = MakeSectionCard(ctx, root, "ShellOverviewKpis", "icon_economy",
            Loc.Get("shell.standing_kpis"), null, "shell.brass_hi");
        AddStandingGrid(ctx, kpiCard, world, nation, isOwn || observing);

        if (isOwn && !observing && openStaffTab is not null)
            AddStaffQuickLink(ctx, root, nation, openStaffTab);

        // Neighbours Card
        var nbCard = MakeSectionCard(ctx, root, "ShellOverviewNeighbours", "icon_world",
            Loc.Get("Neighbours"), Loc.Get("GDP"), "shell.paper_muted");
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
            AddCountryComparisonRow(ctx, nbCard, nb, Fmt.Money(nb.Gdp), $"{rel:+#;-#;0}",
                rel < -15 ? "shell.state.negative_text" : (rel > 20 ? "shell.state.positive" : "shell.paper_muted"),
                () => openCountry(captured));
        }
    }

    public static void PopulateSociety(
        ShellContext ctx, VBoxContainer bodyRows, Campaign world, Nation nation)
    {
        var root = CreatePaddedRoot(bodyRows, "ShellSocietyPaddedRoot");
        PopulateRulerCard(ctx, root, world, nation);

        var states = world.StatesOf(nation);
        var totalRad = 0f;
        var stCount = 0;
        foreach (var s in states) { totalRad += s.Radicalism; stCount++; }
        var avgRadicalism = stCount > 0 ? totalRad / stCount : 0f;

        var socCard = MakeSectionCard(ctx, root, "ShellSocietyMetersCard", "icon_stability",
            Loc.Get("shell.society_profile"), null, "shell.brass_hi");
        AddSocietyMeter(ctx, socCard, "icon_stability", Loc.Get("Stability"), nation.Stability, Fmt.Percent(nation.Stability), StabilityColor(nation.Stability));
        AddSocietyMeter(ctx, socCard, "icon_living", Loc.Get("Standard of living"), nation.StandardOfLiving, Fmt.Percent(nation.StandardOfLiving), GoodHighColor(nation.StandardOfLiving));
        AddSocietyMeter(ctx, socCard, "icon_urbanisation", Loc.Get("Urbanisation"), nation.Urbanisation, Fmt.Percent(nation.Urbanisation), Ui.Colour("shell.state.info"));
        AddSocietyMeter(ctx, socCard, "icon_literacy", Loc.Get("Literacy"), nation.Literacy, Fmt.Percent(nation.Literacy), Ui.Colour("shell.brass_hi"));
        AddSocietyMeter(ctx, socCard, "icon_unrest", Loc.Get("Radicalism"), avgRadicalism, Fmt.Percent(avgRadicalism), DangerHighColor(avgRadicalism));
        AddSocietyMeter(ctx, socCard, "icon_welfare", Loc.Get("War support"), nation.WarSupport, Fmt.Percent(nation.WarSupport), GoodHighColor(nation.WarSupport));

        ShellCountryOutlookView.AddStabilityOutlookCard(ctx, root, world, nation);

        var cabCard = MakeSectionCard(ctx, root, "ShellSocietyCabinetCard", "icon_government",
            Loc.Get("shell.leader_cabinet"), null, "shell.paper_muted");
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 4);
        cabCard.AddChild(grid);

        AddPillCard(ctx, grid, "icon_government", Loc.Get("shell.government_form"), Loc.Get(PoliticsCore.GovernmentOf(nation).Name));
        AddPillCard(ctx, grid, "icon_laws", Loc.Get("shell.national_ideology"), Loc.Get(PoliticsCore.IdeologyOf(nation).Name));

        var cultureId = DemographyCatalog.PrimaryCulture(nation.Tag);
        var primaryCulture = !string.IsNullOrEmpty(cultureId) ? DemographyCatalog.CultureName(cultureId) : "—";
        AddPillCard(ctx, grid, "icon_nation", Loc.Get("shell.dominant_culture"), primaryCulture);

        var faiths = !string.IsNullOrEmpty(cultureId) ? DemographyCatalog.FaithOf(cultureId) : null;
        var faithId = faiths is { Count: > 0 } ? faiths[0].Religion : null;
        var primaryFaith = !string.IsNullOrEmpty(faithId) ? DemographyCatalog.ReligionName(faithId) : "—";
        AddPillCard(ctx, grid, "icon_faith", Loc.Get("shell.dominant_faith"), primaryFaith);

        // National demographic composition visual stacked bars (read-only)
        var cultTotals = new Dictionary<string, long>(StringComparer.Ordinal);
        var faithTotals = new Dictionary<string, long>(StringComparer.Ordinal);
        var totalPop = 0L;
        foreach (var s in states)
        {
            if (s.Population <= 0) continue;
            totalPop += s.Population;
            foreach (var c in EconomySystem.Cultures(s))
                cultTotals[c.Culture] = cultTotals.GetValueOrDefault(c.Culture) + c.Size;
            foreach (var f in DemographyCatalog.DenominationsIn(s, s.NationId))
                faithTotals[f.Denomination] = faithTotals.GetValueOrDefault(f.Denomination) + f.Size;
        }
        if (totalPop > 0 && cultTotals.Count > 0)
        {
            var cultCard = MakeSectionCard(ctx, root, "ShellSocietyCultureCard", "icon_nation",
                Loc.Get("shell.clarity.culture_breakdown"), cultTotals.Count.ToString(CultureInfo.InvariantCulture), "shell.brass_hi");
            var topCult = cultTotals.OrderByDescending(x => x.Value).Take(5)
                .Select((kv, idx) => (Name: DemographyCatalog.CultureName(kv.Key), Share: (float)(kv.Value / (double)totalPop),
                    Tok: SwatchPalette[idx % SwatchPalette.Length])).ToList();
            AddCompositionVisual(ctx, cultCard, topCult);
        }
        if (totalPop > 0 && faithTotals.Count > 0)
        {
            var faithCard = MakeSectionCard(ctx, root, "ShellSocietyFaithCard", "icon_faith",
                Loc.Get("shell.clarity.faith_breakdown"), faithTotals.Count.ToString(CultureInfo.InvariantCulture), "shell.state.info");
            var topFaith = faithTotals.OrderByDescending(x => x.Value).Take(5)
                .Select((kv, idx) => (Name: DemographyCatalog.ReligionName(kv.Key), Share: (float)(kv.Value / (double)totalPop),
                    Tok: SwatchPalette[(idx + 1) % SwatchPalette.Length])).ToList();
            AddCompositionVisual(ctx, faithCard, topFaith);
        }
    }

    public static void PopulateDecisions(
        ShellContext ctx, VBoxContainer bodyRows, Campaign world, Nation nation)
    {
        var root = CreatePaddedRoot(bodyRows, "ShellDecisionsPaddedRoot");
        var ppWeekly = world.Defines.PoliticalPointsPerWeek +
            world.Defines.PoliticalPointsStabilityWeight * Math.Clamp(nation.Stability, 0f, 1f);
        var ready = new List<DecisionDefinition>();
        var all = DecisionCatalog.All;
        for (var i = 0; i < all.Count; i++)
            if (!DecisionCatalog.IsBlocked(world, nation, all[i])) ready.Add(all[i]);

        var card = MakeSectionCard(ctx, root, "ShellDecisionsCard", "icon_decisions",
            Loc.Get("Decisions"), Loc.Format("shell.clarity.decisions_ready_fmt", ready.Count),
            ready.Count > 0 ? "shell.state.positive" : "shell.paper_muted");

        AddKvRow(ctx, card, Loc.Get("Political capital"),
            $"{nation.PoliticalPoints:0.0} (+{ppWeekly:0.0}/{Loc.Get("week")})", "shell.paper");

        var cap = Math.Min(7, ready.Count);
        for (var i = 0; i < cap; i++)
        {
            var dec = ready[i];
            var costTxt = dec.GdpCostShare > 0f
                ? Fmt.Money(nation.Gdp * dec.GdpCostShare)
                : (dec.ResourceCosts is { Count: > 0 } ? $"{dec.ResourceCosts[0].Amount:0.#}" : "—");
            var rowPanel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
            rowPanel.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 7, 4, 7, 4));
            rowPanel.TooltipText = Ui.Tip($"{Loc.Get(dec.Name)}\n{Loc.Get(dec.Description)}");
            card.AddChild(rowPanel);

            var h = new HBoxContainer { ClipContents = true };
            h.AddThemeConstantOverride("separation", 6);
            rowPanel.AddChild(h);
            if (ctx.Icon("icon_decision") is { } dIco)
            {
                h.AddChild(new TextureRect
                {
                    Texture = dIco, CustomMinimumSize = new Vector2(14, 14),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    Modulate = Ui.Colour("shell.brass_hi")
                });
            }
            h.AddChild(MakeLabel(ctx, Loc.Get(dec.Name), "shell.sans", 11, "shell.paper", clip: true, expand: true));
            h.AddChild(MakeLabel(ctx, costTxt, "shell.sans", 11, "shell.state.warning_text", tabular: true));
        }

        if (ready.Count == 0)
        {
            var note = MakeLabel(ctx, Loc.Get("shell.open_decisions_hint"), "shell.sans", 11, "shell.paper_muted");
            note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            card.AddChild(note);
        }

        if (world.Player?.Id == nation.Id)
        {
            var openBtn = new Button
            {
                Text = Loc.Get("shell.clarity.decisions_open_cta"),
                CustomMinimumSize = new Vector2(0, 32),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None
            };
            openBtn.AddThemeStyleboxOverride("normal", ctx.NineSlice("button_brass_cta_9s", 10, 8, 4, 8, 4, "shell.plate_top", "shell.brass_hi", 3));
            openBtn.AddThemeStyleboxOverride("hover", ctx.NineSlice("button_brass_cta_9s", 10, 8, 4, 8, 4, "shell.plate_hover", "shell.paper", 3));
            openBtn.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
            openBtn.AddThemeFontSizeOverride("font_size", 12);
            openBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
            openBtn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); ctx.OpenScreen?.Invoke("Decisions"); };
            root.AddChild(openBtn);
        }
    }

    public static void PopulateDiplomacy(
        ShellContext ctx, VBoxContainer bodyRows, Label feedbackLabel, Campaign world,
        Nation nation, bool isOwn, Action<Nation> openCountry, Action onMutated) =>
        ShellCountryDiplomacyView.Populate(ctx, bodyRows, feedbackLabel, world, nation, isOwn, openCountry, onMutated);

    public static void PopulateStaff(
        ShellContext ctx, VBoxContainer bodyRows, Campaign world, Nation nation, Action onChanged)
    {
        var root = CreatePaddedRoot(bodyRows, "ShellStaffPaddedRoot");
        var observing = ctx.IsObserverMode?.Invoke() ?? world.ObserverMode;
        var items = DealOrForceGame.StaffPortfolios;
        var active = items.Count(entry => Delegation.Holds(nation, entry.Portfolio));
        var allHeld = active == items.Length;

        var headCard = MakeSectionCard(ctx, root, "ShellStaffHeaderCard", "icon_government",
            Loc.Get("THE STAFF"), $"{active}/{items.Length}", active > 0 ? "shell.state.positive" : "shell.paper_muted");

        var hint = MakeLabel(ctx, Loc.Get(observing ? "shell.obs.readonly_banner" : "shell.staff_subtitle"),
            "shell.sans", 11, observing ? "shell.state.info" : "shell.paper_muted");
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        headCard.AddChild(hint);

        var master = new Button
        {
            Name = "ShellStaffToggleAll",
            Text = Loc.Get(allHeld ? "Take everything back" : "Hand everything to the staff"),
            Disabled = observing,
            CustomMinimumSize = new Vector2(0, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseDefaultCursorShape = observing ? Control.CursorShape.Arrow : Control.CursorShape.PointingHand,
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
        headCard.AddChild(master);

        foreach (var (portfolio, on, off, tooltip) in items)
        {
            var held = Delegation.Holds(nation, portfolio);
            var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
            card.AddThemeStyleboxOverride("panel", ctx.BorderedBox(
                held ? "shell.plate_top" : "shell.well_bottom",
                held ? "shell.brass_hi" : "shell.engrave",
                1, 3, 8, 3, 8, 3));
            root.AddChild(card);

            var toggle = new CheckButton
            {
                Name = "ShellStaff_" + portfolio,
                Text = Loc.Get(held ? on : off),
                Disabled = observing,
                CustomMinimumSize = new Vector2(0, 28),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseDefaultCursorShape = observing ? Control.CursorShape.Arrow : Control.CursorShape.PointingHand,
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

    private static void AddCompositionVisual(
        ShellContext ctx, VBoxContainer parent, List<(string Name, float Share, string Tok)> segments)
    {
        var bar = new Control
        {
            CustomMinimumSize = new Vector2(0, 10),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        bar.Draw += () =>
        {
            var sz = bar.Size;
            if (sz.X <= 4f) return;
            bar.DrawRect(new Rect2(0, 0, sz.X, sz.Y), Ui.Colour("shell.well"));
            var x = 0f;
            for (var i = 0; i < segments.Count; i++)
            {
                var w = MathF.Max(1.5f, sz.X * Math.Clamp(segments[i].Share, 0f, 1f));
                if (x + w > sz.X) w = sz.X - x;
                if (w > 0.5f) bar.DrawRect(new Rect2(x, 0, w, sz.Y), Ui.Colour(segments[i].Tok));
                x += w;
                if (x >= sz.X) break;
            }
            bar.DrawRect(new Rect2(0, 0, sz.X, sz.Y), Ui.Colour("shell.engrave"), filled: false, width: 1f);
        };
        parent.AddChild(bar);

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var row = new HBoxContainer { ClipContents = true };
            row.AddThemeConstantOverride("separation", 6);
            row.AddChild(new ColorRect
            {
                CustomMinimumSize = new Vector2(8, 8),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                Color = Ui.Colour(seg.Tok),
                MouseFilter = Control.MouseFilterEnum.Ignore
            });
            row.AddChild(MakeLabel(ctx, seg.Name, "shell.sans", 11, "shell.paper_muted", clip: true, expand: true));
            var pct = MakeLabel(ctx, $"{seg.Share:P1}", "shell.sans", 11, "shell.paper", tabular: true);
            pct.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(pct);
            parent.AddChild(row);
        }
    }

    private static void AddProgressBar(VBoxContainer parent, float frac01, string colorTok)
    {
        var bar = new Control
        {
            CustomMinimumSize = new Vector2(0, 6),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        bar.Draw += () =>
        {
            var sz = bar.Size;
            if (sz.X <= 4f) return;
            bar.DrawRect(new Rect2(0, 0, sz.X, sz.Y), Ui.Colour("shell.well"));
            var w = sz.X * Math.Clamp(frac01, 0f, 1f);
            if (w > 0.5f) bar.DrawRect(new Rect2(0, 0, w, sz.Y), Ui.Colour(colorTok));
            bar.DrawRect(new Rect2(0, 0, sz.X, sz.Y), Ui.Colour("shell.engrave"), filled: false, width: 1f);
        };
        parent.AddChild(bar);
    }

    internal static VBoxContainer MakeSectionCard(
        ShellContext ctx, VBoxContainer parent, string name, string iconName, string title, string badgeText, string badgeTok)
    {
        var card = new PanelContainer { Name = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        card.AddThemeStyleboxOverride("panel", ChromePlateArt.BuildCardWrapperStyle(8, 6, 8, 6) ??
            ctx.BorderedBox("shell.plate_bottom", "shell.engrave", 1, 3, 8, 6, 8, 6));
        parent.AddChild(card);

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
        if (!string.IsNullOrEmpty(badgeText))
        {
            var pill = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            pill.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well", "shell.engrave", 1, 2, 6, 1, 6, 1));
            pill.AddChild(MakeLabel(ctx, badgeText, "shell.sans", 10, badgeTok, tabular: true));
            head.AddChild(pill);
        }
        col.AddChild(ShellContext.CreateRuledLine(title.Length * 11 + 5, "shell.rule", 1.0f, 3f));
        return col;
    }

    private static void AddInfoBanner(ShellContext ctx, VBoxContainer parent, string iconName, string text, string colorTok)
    {
        var note = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        note.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", colorTok, 1, 2, 8, 4, 8, 4));
        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 6);
        note.AddChild(row);
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
        parent.AddChild(note);
    }

    private static void AddMiniKpi(ShellContext ctx, GridContainer grid, string caption, string val, string valTok)
    {
        var box = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        box.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 4, 3, 4, 3));
        grid.AddChild(box);
        var v = new VBoxContainer { ClipContents = true };
        v.AddThemeConstantOverride("separation", 1);
        box.AddChild(v);
        var lv = MakeLabel(ctx, val, "shell.sans", 13, valTok, clip: true, tabular: true);
        lv.HorizontalAlignment = HorizontalAlignment.Center;
        var lc = MakeLabel(ctx, caption, "shell.sans", 9, "shell.paper_faint", clip: true);
        lc.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(lv);
        v.AddChild(lc);
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
            ClipContents = true,
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

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
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

    private static void AddStandingGrid(ShellContext ctx, VBoxContainer parent, Campaign world, Nation nation, bool showTreasury)
    {
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 4);
        parent.AddChild(grid);

        AddPillCard(ctx, grid, "icon_gdp", Loc.Get("GDP"), Fmt.Money(nation.Gdp));
        AddPillCard(ctx, grid, "icon_treasury", Loc.Get("Treasury"), showTreasury ? Fmt.Money(nation.Treasury) : "—");
        AddPillCard(ctx, grid, "icon_inflation", Loc.Get("Inflation"), Fmt.Percent(nation.Inflation),
            nation.Inflation > 0.12f ? "shell.state.negative_text" : (nation.Inflation > 0.06f ? "shell.state.warning_text" : "shell.paper"));
        AddPillCard(ctx, grid, "icon_population", Loc.Get("Manpower"), Fmt.People(nation.ManpowerReserve));
    }

    private static void AddSocietyMeter(
        ShellContext ctx, VBoxContainer parent, string iconName, string label, float fraction01, string valueText, Color fillColor) =>
        ShellRegionVisualsView.AddMeterRow(ctx, parent, iconName, label, fraction01, valueText, fillColor, label);

    private static void AddPillCard(
        ShellContext ctx, GridContainer parent, string iconName, string label, string value, string valTok = "shell.paper")
    {
        var pill = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        pill.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 6, 4, 6, 4));
        parent.AddChild(pill);

        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 6);
        pill.AddChild(row);

        if (ctx.Icon(iconName) is { } tex)
        {
            var ico = new TextureRect
            {
                Texture = tex, CustomMinimumSize = new Vector2(16, 16),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            ShellContext.ApplyIconRelief(ico, Ui.Colour("shell.icon"), 0.60f);
            row.AddChild(ico);
        }

        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        col.AddThemeConstantOverride("separation", 1);
        row.AddChild(col);
        col.AddChild(MakeLabel(ctx, label, "shell.sans", 10, "shell.paper_faint", clip: true));
        col.AddChild(MakeLabel(ctx, value, "shell.sans", 11, valTok, clip: true, tabular: true));
    }

    private static void AddCountryComparisonRow(
        ShellContext ctx, VBoxContainer parent, Nation target, string gdpText, string relText, string relColorTok, Action onClick)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, 26),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        btn.AddThemeStyleboxOverride("normal", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 6, 2, 6, 2));
        btn.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 6, 2, 6, 2));
        btn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(underlineBottom: true));
        btn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); onClick(); };
        parent.AddChild(btn);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 6; row.OffsetRight = -6;
        row.AddThemeConstantOverride("separation", 6);
        btn.AddChild(row);

        if (ctx.FlagTexture(target.Tag) is { } flagTex)
        {
            row.AddChild(new TextureRect
            {
                Material = FlagClothPresentation.Material,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                CustomMinimumSize = new Vector2(20, 13),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                Texture = flagTex,
                MouseFilter = Control.MouseFilterEnum.Ignore
            });
        }

        row.AddChild(MakeLabel(ctx, target.DisplayName, "shell.sans", 11, "shell.paper", clip: true, expand: true));
        row.AddChild(MakeLabel(ctx, gdpText, "shell.sans", 11, "shell.paper_muted", tabular: true));
        row.AddChild(MakeLabel(ctx, relText, "shell.sans", 11, relColorTok, tabular: true));
    }

    private static void AddKvRow(ShellContext ctx, VBoxContainer bodyRows, string key, string value, string valueTok)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 20), ClipContents = true };
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(MakeLabel(ctx, key, "shell.sans", 11, "shell.paper_muted", clip: true, expand: true));
        var v = MakeLabel(ctx, value, "shell.sans", 11, valueTok, clip: true, tabular: true);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(v);
        bodyRows.AddChild(row);
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

    private static Color StabilityColor(float v) => v < 0.35f ? Ui.Colour("shell.state.negative") : v < 0.55f ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.positive");
    private static Color GoodHighColor(float v) => v < 0.35f ? Ui.Colour("shell.state.negative") : v < 0.65f ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.positive");
    private static Color DangerHighColor(float v) => v > 0.45f ? Ui.Colour("shell.state.negative") : v > 0.25f ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.positive");
}
