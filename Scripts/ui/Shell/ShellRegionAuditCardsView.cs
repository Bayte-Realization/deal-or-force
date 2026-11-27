using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Renders audited regional simulation cards for <see cref="ShellRegionVisualsView"/> and compact
/// province badges for <see cref="DealOrForceGame"/>: active Movements in Region (clickable to
/// inspect), Governance &amp; Territorial Claims (Homeland/Claim, Autonomy, Conquest Acceptance,
/// Secession Pressure, Parallel/Held Administration, Occupation Resistance, War Damage &amp;
/// Displaced, Active Crises), Infrastructure/Connectivity &amp; Garrison Entrenchment, and Regional
/// Political Cohorts. Strictly read-only; zero raw Color literals.
/// </summary>
internal static class ShellRegionAuditCardsView
{
    public static void AppendOverviewAuditSections(
        ShellContext ctx, VBoxContainer bodyRows, Campaign world, Nation owner, ScenarioState state, int provinceId)
    {
        var canInspectAll = ObserverUiRules.IsObserving(world, ctx.IsObserverMode?.Invoke() ?? world.ObserverMode);
        var audit = CardTelemetryAudit.ReadRegionAudit(world, owner, state, provinceId, canInspectAll);

        AddMovementsCard(ctx, bodyRows, audit.Movements);
        AddGovernanceAndStatusCard(ctx, bodyRows, owner, audit);
        AddDamageAndCrisesCard(ctx, bodyRows, audit);
        AddConnectivityStrip(ctx, bodyRows, audit);
    }

    public static void AppendPopulationCohortsAudit(
        ShellContext ctx, VBoxContainer bodyRows, Campaign world, Nation owner, ScenarioState state)
    {
        var canInspectAll = ObserverUiRules.IsObserving(world, ctx.IsObserverMode?.Invoke() ?? world.ObserverMode);
        var audit = CardTelemetryAudit.ReadRegionAudit(world, owner, state, -1, canInspectAll);
        if (!audit.CohortsReviewed && audit.CivicAwareness <= 0f) return;

        ShellRegionVisualsView.AddSectionHeader(ctx, bodyRows, Loc.Get("shell.audit.reg_public_mood"));
        if (audit.CohortsReviewed)
        {
            ShellRegionVisualsView.AddMeterRow(ctx, bodyRows, "icon_government",
                Loc.Get("shell.audit.gov_approval"), audit.GovernmentApproval,
                Fmt.Percent(audit.GovernmentApproval),
                ShellRegionVisualsView.GoodHighColor(audit.GovernmentApproval),
                Loc.Get("shell.audit.gov_approval_tip"));
            ShellRegionVisualsView.AddMeterRow(ctx, bodyRows, "icon_laws",
                Loc.Get("shell.audit.law_approval"), audit.LawApproval,
                Fmt.Percent(audit.LawApproval),
                ShellRegionVisualsView.GoodHighColor(audit.LawApproval),
                Loc.Get("shell.audit.law_approval_tip"));
            ShellRegionVisualsView.AddMeterRow(ctx, bodyRows, "icon_unrest",
                Loc.Get("shell.audit.pol_divergence"), audit.PoliticalDivergence,
                Fmt.Percent(audit.PoliticalDivergence),
                ShellRegionVisualsView.DangerHighColor(audit.PoliticalDivergence),
                Loc.Get("shell.audit.pol_divergence_tip"));
        }
        ShellRegionVisualsView.AddMeterRow(ctx, bodyRows, "icon_research",
            Loc.Get("shell.audit.civic_awareness"), audit.CivicAwareness,
            Fmt.Percent(audit.CivicAwareness), Ui.Colour("shell.state.info"),
            Loc.Get("shell.audit.civic_awareness_tip"));
    }

    public static void AppendRecruitReadinessAudit(
        ShellContext ctx, VBoxContainer bodyRows, Campaign world, Nation owner, ScenarioState state, int provinceId)
    {
        var canInspectAll = ObserverUiRules.IsObserving(world, ctx.IsObserverMode?.Invoke() ?? world.ObserverMode);
        var audit = CardTelemetryAudit.ReadRegionAudit(world, owner, state, provinceId, canInspectAll);
        if (audit.BarracksLevel <= 0 && audit.AverageGarrisonEntrenchment < 0.02f && audit.OccupiedProvinces <= 0) return;

        ShellRegionVisualsView.AddSectionHeader(ctx, bodyRows, Loc.Get("shell.audit.reg_defense_readiness"));
        if (audit.BarracksLevel > 0 || audit.InfrastructureLevel > 0)
        {
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, bodyRows, Loc.Get("shell.audit.barracks_infra"),
                Loc.Format("shell.audit.barracks_infra_fmt", audit.BarracksLevel, audit.InfrastructureLevel),
                "shell.paper");
        }
        if (audit.AverageGarrisonEntrenchment >= 0.02f)
        {
            ShellRegionVisualsView.AddMeterRow(ctx, bodyRows, "icon_military",
                Loc.Get("shell.audit.avg_entrenchment"), audit.AverageGarrisonEntrenchment,
                Fmt.Percent(audit.AverageGarrisonEntrenchment), Ui.Colour("shell.state.info"),
                Loc.Get("shell.audit.avg_entrenchment_tip"));
        }
        if (audit.OccupiedProvinces > 0 && audit.ProvinceWeeklyResistanceSlip > 0.0005f)
        {
            var slipPct = audit.ProvinceWeeklyResistanceSlip / MathF.Max(0.01f, audit.ProvinceOccupationShare);
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, bodyRows, Loc.Get("shell.audit.occ_resistance"),
                Loc.Format("shell.audit.occ_slip_fmt", Fmt.Percent(slipPct)),
                "shell.state.warning_text");
        }
    }

    /// <summary>Compact read-only movement &amp; status line for the CompactProvinceInfo map popover.</summary>
    public static void AppendCompactProvinceBadges(
        Campaign world, ScenarioState state, int provinceId, VBoxContainer rows)
    {
        if (world is null || state is null || rows is null) return;
        var canInspectAll = world.ObserverMode;
        var movements = CardTelemetryAudit.ReadRegionMovements(world, state, canInspectAll);
        if (movements.Count > 0)
        {
            var top = movements[0];
            var hqPart = top.IsHeadquarters ? $" · {Loc.Get("shell.audit.hq_short")}" : "";
            var text = movements.Count > 1
                ? Loc.Format("shell.audit.compact_movements_multi_fmt",
                    movements.Count, top.Name, Fmt.Percent(top.ResidentSupport), hqPart)
                : Loc.Format("shell.audit.compact_movements_one_fmt",
                    top.Name, Fmt.Percent(top.ResidentSupport), hqPart);
            var lbl = new Label
            {
                Name = "CompactProvinceMovementsBadge",
                Text = text,
                ClipText = true,
                AutowrapMode = TextServer.AutowrapMode.Off,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = Ui.Tip(Loc.Get("shell.audit.movements_section_tip"))
            };
            lbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            lbl.AddThemeFontSizeOverride("font_size", 11);
            lbl.AddThemeColorOverride("font_color",
                Ui.Colour(top.HighRisingRisk ? "state.negative" : top.IsHeadquarters ? "state.warning" : "shell.state.info"));
            rows.AddChild(lbl);
        }

        if (state.SecessionPressure >= 0.20f || state.WarDamage >= 0.08f || RegionalAutonomy.Autonomous(state))
        {
            var parts = new List<string>(3);
            if (RegionalAutonomy.Autonomous(state)) parts.Add(Loc.Get("shell.audit.autonomous_badge"));
            if (state.SecessionPressure >= 0.20f)
                parts.Add(Loc.Format("shell.audit.compact_secession_fmt", Fmt.Percent(M.Clamp01(state.SecessionPressure))));
            if (state.WarDamage >= 0.08f)
                parts.Add(Loc.Format("shell.ws.reg.war_damage_fmt", Fmt.Percent(M.Clamp01(state.WarDamage))));
            var statusLbl = new Label
            {
                Name = "CompactProvinceStatusBadge",
                Text = string.Join(" · ", parts),
                ClipText = true,
                AutowrapMode = TextServer.AutowrapMode.Off,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            statusLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            statusLbl.AddThemeFontSizeOverride("font_size", 11);
            statusLbl.AddThemeColorOverride("font_color",
                Ui.Colour(state.SecessionPressure >= 0.55f || state.WarDamage >= 0.35f ? "state.negative" : "state.warning"));
            rows.AddChild(statusLbl);
        }
    }

    private static void AddMovementsCard(
        ShellContext ctx, VBoxContainer bodyRows, IReadOnlyList<CardTelemetryAudit.RegionMovementEntry> movements)
    {
        var badge = movements.Count > 0
            ? movements.Count.ToString(CultureInfo.InvariantCulture)
            : Loc.Get("shell.audit.none_short");
        var badgeTok = movements.Any(m => m.HighRisingRisk)
            ? "shell.state.negative_text"
            : movements.Count > 0 ? "shell.state.warning_text" : "shell.paper_faint";
        var card = ShellCountryDossierView.MakeSectionCard(
            ctx, bodyRows, "ShellRegionMovementsCard", "icon_unrest",
            Loc.Get("shell.audit.movements_in_region"), badge, badgeTok);

        if (movements.Count == 0)
        {
            var empty = ShellRegionVisualsView.MakeLabel(
                ctx, Loc.Get("shell.audit.no_active_movements"), "shell.sans", 11, "shell.paper_faint", clip: true);
            empty.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            card.AddChild(empty);
            return;
        }

        var cap = Math.Min(6, movements.Count);
        for (var i = 0; i < cap; i++)
            AddMovementRow(ctx, card, movements[i]);
    }

    private static void AddMovementRow(ShellContext ctx, VBoxContainer parent, CardTelemetryAudit.RegionMovementEntry m)
    {
        var tipLines = new List<string>(6)
        {
            $"{m.Name} ({m.Tag})",
            Loc.Format("shell.audit.mov_tip_resident_fmt", Fmt.Percent(m.ResidentSupport)),
            Loc.Format("shell.audit.mov_tip_people_fmt", Fmt.Percent(m.PeopleSupport))
        };
        if (m.IsHeadquarters) tipLines.Add(Loc.Get("shell.audit.mov_tip_hq"));
        if (m.ParallelLevy) tipLines.Add(Loc.Get("shell.audit.mov_tip_parallel"));
        if (m.UprisingChance > 0f)
            tipLines.Add(Loc.Format("shell.audit.mov_tip_rising_fmt", Fmt.Percent(m.UprisingChance)));
        if (m.CanInspectDetails)
        {
            tipLines.Add(Loc.Format("shell.audit.mov_tip_heat_cadres_fmt", Fmt.Percent(m.Heat), Fmt.People((long)m.Cadres)));
            if (m.InfiltratedFighters >= 50f)
                tipLines.Add(Loc.Format("shell.audit.mov_tip_infiltrated_fmt", Fmt.People((long)m.InfiltratedFighters)));
        }
        tipLines.Add(Loc.Get("shell.audit.mov_tip_click"));

        var borderTok = m.HighRisingRisk ? "shell.state.negative" : m.IsHeadquarters ? "shell.brass_hi" : "shell.engrave";
        var btn = new Button
        {
            Name = $"ShellRegionMovement_{m.NationId}",
            CustomMinimumSize = new Vector2(0, 36),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(string.Join("\n", tipLines))
        };
        btn.AddThemeStyleboxOverride("normal", ctx.BorderedBox("shell.well_bottom", borderTok, 1, 2, 6, 4, 6, 4));
        btn.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 6, 4, 6, 4));
        btn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(padX: 6, padY: 4));
        btn.AddThemeStyleboxOverride("hover_pressed", ctx.PressedWellStyle(padX: 6, padY: 4));
        btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var targetId = m.NationId;
        btn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); ctx.OpenObservedMovement?.Invoke(targetId); };
        parent.AddChild(btn);

        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        col.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        col.OffsetLeft = 6; col.OffsetRight = -6; col.OffsetTop = 3; col.OffsetBottom = -3;
        col.AddThemeConstantOverride("separation", 3);
        btn.AddChild(col);

        var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        top.AddThemeConstantOverride("separation", 5);
        col.AddChild(top);

        var swatchCol = ParseHexOrFallback(m.ColourHex, Ui.Colour("shell.state.warning"));
        top.AddChild(new ColorRect
        {
            Color = swatchCol, CustomMinimumSize = new Vector2(8, 8),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        });
        top.AddChild(ShellRegionVisualsView.MakeLabel(ctx, m.Name, "shell.sans", 11, "shell.paper", clip: true, expand: true));

        if (m.IsHeadquarters)
            top.AddChild(MakeCompactBadge(ctx, Loc.Get("shell.audit.hq_short"), "shell.brass_hi"));
        if (m.ParallelLevy)
            top.AddChild(MakeCompactBadge(ctx, Loc.Get("shell.audit.parallel_short"), "shell.state.warning_text"));
        if (m.HighRisingRisk)
            top.AddChild(MakeCompactBadge(ctx, Loc.Format("shell.audit.rise_short_fmt", Fmt.Percent(m.UprisingChance)), "shell.state.negative_text"));

        var pctTok = m.HighRisingRisk ? "shell.state.negative_text"
            : m.ResidentSupport >= 0.25f ? "shell.state.warning_text" : "shell.paper";
        var pctLbl = ShellRegionVisualsView.MakeLabel(ctx, Fmt.Percent(m.ResidentSupport), "shell.sans", 11, pctTok, tabular: true);
        pctLbl.HorizontalAlignment = HorizontalAlignment.Right;
        top.AddChild(pctLbl);

        var barFill = m.HighRisingRisk ? Ui.Colour("shell.state.negative")
            : m.ResidentSupport >= 0.25f ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.info");
        var bar = ShellRegionKpiCardsView.BuildMiniProgressBar(ctx, MathF.Max(m.ResidentSupport, m.PeopleSupport * 0.25f), barFill);
        bar.CustomMinimumSize = new Vector2(0, 5);
        col.AddChild(bar);
    }

    private static void AddGovernanceAndStatusCard(
        ShellContext ctx, VBoxContainer bodyRows, Nation owner, CardTelemetryAudit.RegionAuditSnapshot a)
    {
        var titleStr = a.OwnerTitle switch
        {
            TerritorialTitle.Homeland => Loc.Get("shell.audit.title_homeland"),
            TerritorialTitle.Claim => Loc.Get("shell.audit.title_claim"),
            _ => Loc.Get("shell.audit.title_unentitled")
        };
        var titleTok = a.OwnerTitle == TerritorialTitle.Homeland ? "shell.state.positive"
            : a.OwnerTitle == TerritorialTitle.Claim ? "shell.state.info" : "shell.state.warning_text";

        var card = ShellCountryDossierView.MakeSectionCard(
            ctx, bodyRows, "ShellRegionGovernanceCard", "icon_laws",
            Loc.Get("shell.audit.governance_claims"), titleStr, titleTok);

        if (a.IsAutonomous)
        {
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, card, Loc.Get("shell.audit.autonomy_label"),
                Loc.Format("shell.audit.autonomy_active_fmt", Fmt.Money(a.AutonomyWeeklyCost)),
                "shell.state.info");
        }
        else if (a.AutonomyRecentlyRevoked)
        {
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, card, Loc.Get("shell.audit.autonomy_label"),
                Loc.Get("shell.audit.autonomy_revoked"), "shell.state.warning_text");
        }

        if (a.IsUnentitled)
        {
            var accTxt = a.ConquestStallReason.Length > 0
                ? $"{Fmt.Percent(a.ConquestAcceptanceShare)} · {Loc.Get("shell.audit.stalled_short")}"
                : $"{Fmt.Percent(a.ConquestAcceptanceShare)} (+{(a.ConquestWeeklyGain * 100f):0.00}%/{Loc.Get("week")})";
            ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_stability",
                Loc.Get("shell.audit.conquest_acceptance"), a.ConquestAcceptanceShare, accTxt,
                a.ConquestStallReason.Length > 0 ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.state.info"),
                a.ConquestStallReason.Length > 0
                    ? $"{Loc.Get("shell.audit.conquest_acceptance")}: {Fmt.Percent(a.ConquestAcceptanceShare)}\n{Loc.Get(a.ConquestStallReason)}"
                    : Loc.Get("shell.audit.conquest_acceptance_tip"));
        }

        if (a.SecessionPressure >= 0.02f)
        {
            ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_wars",
                Loc.Get("shell.audit.secession_pressure"), M.Clamp01(a.SecessionPressure),
                Fmt.Percent(M.Clamp01(a.SecessionPressure)),
                ShellRegionVisualsView.DangerHighColor(a.SecessionPressure),
                Loc.Get("shell.audit.secession_pressure_tip"));
        }

        if (a.ForeignMinorityShare >= 0.05f)
        {
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, card, Loc.Get("Minorities"),
                Fmt.Percent(a.ForeignMinorityShare),
                a.ForeignMinorityShare > 0.50f ? "shell.state.negative_text"
                    : a.ForeignMinorityShare > 0.25f ? "shell.state.warning_text" : "shell.paper_muted");
        }

        if (a.BrainDrainPressure >= 0.08f)
        {
            ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_research",
                Loc.Get("Brain drain"), a.BrainDrainPressure, Fmt.Percent(a.BrainDrainPressure),
                ShellRegionVisualsView.DangerHighColor(a.BrainDrainPressure),
                Loc.Get("Brain drain"));
        }

        if (a.ParallelAdminNationId >= 0)
        {
            var tag = a.ParallelAdminExposed ? Loc.Get("shell.audit.exposed_short") : Loc.Get("shell.audit.covert_short");
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, card, Loc.Get("shell.audit.parallel_offices"),
                $"{a.ParallelAdminName} ({Fmt.Percent(a.ParallelAdminCoverage)} · {tag})",
                "shell.state.warning_text");
        }

        if (a.HeldByNationId >= 0 && !string.IsNullOrEmpty(a.HeldByName))
        {
            var status = a.HeldAdministered ? Loc.Get("shell.audit.held_admin") : Loc.Get("shell.audit.held_settling");
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, card, Loc.Get("shell.audit.held_ground"),
                $"{a.HeldByName} · {status}", "shell.state.warning_text");
        }

        if (a.OccupiedProvinces > 0 && a.ProvinceWeeklyResistanceSlip > 0.0005f)
        {
            var slipPct = a.ProvinceWeeklyResistanceSlip / MathF.Max(0.01f, a.ProvinceOccupationShare);
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, card, Loc.Get("shell.audit.occ_resistance"),
                Loc.Format("shell.audit.occ_hold_slip_fmt", Fmt.Percent(a.ProvinceOccupationShare), Fmt.Percent(slipPct)),
                "shell.state.warning_text");
        }

        if (a.ForeignClaimants.Count > 0)
        {
            var shown = string.Join(", ", a.ForeignClaimants.Take(4).Select(c =>
                c.IsHomeland ? $"{c.Name} ★" : c.Name));
            if (a.ForeignClaimants.Count > 4) shown += $" +{a.ForeignClaimants.Count - 4}";
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, card, Loc.Get("shell.audit.foreign_claims"),
                shown, "shell.state.warning_text");
        }
    }

    private static void AddDamageAndCrisesCard(
        ShellContext ctx, VBoxContainer bodyRows, CardTelemetryAudit.RegionAuditSnapshot a)
    {
        var hasDamage = a.WarDamageShare >= 0.01f || a.DisplacedPeople > 0 || a.RebuildingDaysLeft > 0;
        if (!hasDamage && a.ActiveCrises.Count == 0) return;

        var badge = a.WarDamageShare >= 0.01f
            ? Fmt.Percent(a.WarDamageShare)
            : a.ActiveCrises.Count.ToString(CultureInfo.InvariantCulture);
        var card = ShellCountryDossierView.MakeSectionCard(
            ctx, bodyRows, "ShellRegionDamageCrisesCard", "icon_wars",
            Loc.Get("shell.audit.devastation_crises"), badge, "shell.state.negative_text");

        if (a.WarDamageShare >= 0.01f)
        {
            ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_wars",
                Loc.Get("shell.audit.war_damage_label"), a.WarDamageShare,
                $"{Fmt.Percent(a.WarDamageShare)} (−{Fmt.Percent(a.OutputPenaltyShare)})",
                ShellRegionVisualsView.DangerHighColor(a.WarDamageShare),
                Loc.Format("shell.audit.war_damage_tip_fmt", Fmt.Percent(a.WarDamageShare), Fmt.Percent(a.OutputPenaltyShare)));
        }
        if (a.RebuildingDaysLeft > 0)
        {
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, card, Loc.Get("shell.audit.reconstruction"),
                Loc.Format("shell.audit.days_left_fmt", a.RebuildingDaysLeft), "shell.state.positive");
        }
        if (a.DisplacedPeople > 0)
        {
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, card, Loc.Get("shell.audit.displaced_people"),
                Fmt.People(a.DisplacedPeople), "shell.state.warning_text");
        }
        for (var i = 0; i < a.ActiveCrises.Count; i++)
        {
            var c = a.ActiveCrises[i];
            ShellCountryAuditCardsView.AddSafeKvRow(ctx, card,
                Loc.Get(c.IsDisease ? "shell.audit.disease_label" : "shell.audit.disaster_label"),
                $"{Loc.Get(c.NameKey)} ({(int)MathF.Round(c.Severity)}%)", "shell.state.negative_text");
        }
    }

    private static void AddConnectivityStrip(
        ShellContext ctx, VBoxContainer bodyRows, CardTelemetryAudit.RegionAuditSnapshot a)
    {
        if (a.InfrastructureLevel <= 0 && a.PortLevel <= 0 && a.RailLevel <= 0 && a.BarracksLevel <= 0) return;
        var flow = new HFlowContainer { Name = "ShellRegionConnectivityStrip", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        flow.AddThemeConstantOverride("h_separation", 5);
        flow.AddThemeConstantOverride("v_separation", 4);
        bodyRows.AddChild(flow);

        if (a.InfrastructureLevel > 0) flow.AddChild(MakeStatChip(ctx, Loc.Get("shell.audit.chip_infra"), a.InfrastructureLevel.ToString(CultureInfo.InvariantCulture), "shell.paper"));
        if (a.RailLevel > 0) flow.AddChild(MakeStatChip(ctx, Loc.Get("shell.audit.chip_rail"), a.RailLevel.ToString(CultureInfo.InvariantCulture), "shell.state.info"));
        if (a.PortLevel > 0) flow.AddChild(MakeStatChip(ctx, Loc.Get("shell.audit.chip_port"), a.PortLevel.ToString(CultureInfo.InvariantCulture), "shell.state.info"));
        if (a.BarracksLevel > 0) flow.AddChild(MakeStatChip(ctx, Loc.Get("shell.audit.chip_barracks"), a.BarracksLevel.ToString(CultureInfo.InvariantCulture), "shell.brass_hi"));
    }

    private static PanelContainer MakeCompactBadge(ShellContext ctx, string text, string colorTok)
    {
        var pill = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        pill.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well", colorTok, 1, 2, 4, 0, 4, 0));
        pill.AddChild(ShellRegionVisualsView.MakeLabel(ctx, text, "shell.sans", 9, colorTok, tabular: true));
        return pill;
    }

    private static PanelContainer MakeStatChip(ShellContext ctx, string label, string val, string valTok)
    {
        var pill = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        pill.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 6, 2, 6, 2));
        var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", 4);
        pill.AddChild(h);
        h.AddChild(ShellRegionVisualsView.MakeLabel(ctx, label, "shell.sans", 10, "shell.paper_faint"));
        h.AddChild(ShellRegionVisualsView.MakeLabel(ctx, val, "shell.sans", 10, valTok, tabular: true));
        return pill;
    }

    private static Color ParseHexOrFallback(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return Color.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
