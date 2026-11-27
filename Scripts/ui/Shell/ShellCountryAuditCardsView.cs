using System;
using System.Globalization;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Renders audited Country Dossier telemetry cards (Domestic Pressures &amp; Movements, Net Brain
/// Drain &amp; Destination, War Weariness &amp; Devastation, Strategic / Nuclear Posture &amp; Power Rank
/// Breakdown, Active National Crises, and Political Cohorts Mood) plus the layout-safe
/// <see cref="AddSafeKvRow"/> helper that prevents zero-width collapsed right-hand labels in Godot 4
/// HBoxContainers. Strictly read-only; zero raw Color literals.
/// </summary>
internal static class ShellCountryAuditCardsView
{
    /// <summary>
    /// Adds a key/value HBox row where neither side can collapse to 0px width (fixing the Godot 4
    /// bug where a non-expanding Label with ClipText=true beside an ExpandFill sibling gets 0px).
    /// </summary>
    public static void AddSafeKvRow(
        ShellContext ctx, VBoxContainer bodyRows, string key, string value, string valueTok, string tooltip = null)
    {
        var safeKey = key ?? "";
        var safeVal = value ?? "";
        var row = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 20),
            ClipContents = true,
            TooltipText = Ui.Tip(string.IsNullOrEmpty(tooltip) ? $"{safeKey}: {safeVal}" : tooltip)
        };
        row.AddThemeConstantOverride("separation", 6);

        var shortVal = safeVal.Length <= 18;
        var shortKey = safeKey.Length <= 20;
        Label kLbl, vLbl;
        if (shortVal)
        {
            kLbl = ShellCountryDossierView.MakeLabel(ctx, safeKey, "shell.sans", 11, "shell.paper_muted", clip: true, expand: true);
            vLbl = ShellCountryDossierView.MakeLabel(ctx, safeVal, "shell.sans", 11, valueTok, clip: false, expand: false, tabular: true);
        }
        else if (shortKey)
        {
            kLbl = ShellCountryDossierView.MakeLabel(ctx, safeKey, "shell.sans", 11, "shell.paper_muted", clip: false, expand: false);
            vLbl = ShellCountryDossierView.MakeLabel(ctx, safeVal, "shell.sans", 11, valueTok, clip: true, expand: true, tabular: true);
        }
        else
        {
            kLbl = ShellCountryDossierView.MakeLabel(ctx, safeKey, "shell.sans", 11, "shell.paper_muted", clip: true, expand: true);
            kLbl.SizeFlagsStretchRatio = 1.0f;
            vLbl = ShellCountryDossierView.MakeLabel(ctx, safeVal, "shell.sans", 11, valueTok, clip: true, expand: true, tabular: true);
            vLbl.SizeFlagsStretchRatio = 1.45f;
        }
        vLbl.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(kLbl);
        row.AddChild(vLbl);
        bodyRows.AddChild(row);
    }

    public static void AppendOverviewAuditCards(
        ShellContext ctx, VBoxContainer root, Campaign world, Nation nation, bool canInspect)
    {
        var audit = CardTelemetryAudit.ReadCountryAudit(world, nation, canInspect);
        AddStrategicPostureAndRankCard(ctx, root, nation, audit);
        AddDomesticPressuresAndMovementsCard(ctx, root, audit, canInspect);
        AddBrainDrainAndWarCard(ctx, root, audit);
    }

    public static void AppendSocietyAuditCards(
        ShellContext ctx, VBoxContainer root, Campaign world, Nation nation)
    {
        var canInspect = world.Player?.Id == nation.Id ||
                         (ctx.IsObserverMode?.Invoke() ?? world.ObserverMode) ||
                         CampaignIntel.PlayerCanInspect(world, nation.Id);
        var audit = CardTelemetryAudit.ReadCountryAudit(world, nation, canInspect);

        if (audit.CohortsReviewed || audit.BlocsReviewed)
            AddPoliticalCohortsCard(ctx, root, audit);

        if (MathF.Abs(audit.BrainDrainRate) >= 0.01f || audit.WeeklyNetMigration != 0 || audit.CumulativeNetMigration != 0)
            AddBrainDrainCard(ctx, root, audit, "ShellSocietyBrainDrainCard");

        if (audit.ActiveMovementsCount > 0 || audit.RestiveRegionsCount > 0 || audit.AutonomousRegionsCount > 0)
            AddDomesticPressuresAndMovementsCard(ctx, root, audit, canInspect, "ShellSocietyMovementsCard");
    }

    private static void AddStrategicPostureAndRankCard(
        ShellContext ctx, VBoxContainer root, Nation nation, CardTelemetryAudit.CountryAuditSnapshot a)
    {
        var hasRank = a.RankBreakdown is not null;
        var showNuc = a.NuclearEraActive && (a.DeterrentTier > 0 || a.GlobalDefconLevel <= 3);
        if (!hasRank && !showNuc && a.ActiveCrises.Count == 0) return;

        var badge = a.DeterrentTier > 0
            ? $"DEFCON {a.OwnNuclearPosture}"
            : hasRank ? $"№ {a.RankBreakdown.OverallRank}" : $"DEFCON {a.GlobalDefconLevel}";
        var badgeTok = (a.DeterrentTier > 0 && a.OwnNuclearPosture <= 2) || a.GlobalDefconLevel <= 2
            ? "shell.state.negative_text"
            : a.DeterrentTier > 0 ? "shell.state.warning_text" : "shell.brass_hi";

        var card = ShellCountryDossierView.MakeSectionCard(
            ctx, root, "ShellOverviewStrategicPostureCard", "icon_victory",
            Loc.Get("shell.audit.strategic_posture_rank"), badge, badgeTok);

        if (showNuc)
        {
            if (a.DeterrentTier > 0)
            {
                var tierLabel = a.DeterrentTier >= 2
                    ? Loc.Format("shell.audit.nuc_tier_second_fmt", a.DeterrentTier)
                    : Loc.Get("shell.audit.nuc_tier_mad");
                var docLabel = Loc.Get(a.NuclearDoctrine switch
                {
                    NuclearDoctrine.NoFirstUse => "shell.audit.nuc_doc_nfu",
                    NuclearDoctrine.EscalationToDeescalate => "shell.audit.nuc_doc_escalate",
                    _ => "shell.audit.nuc_doc_triad"
                });
                AddSafeKvRow(ctx, card, Loc.Get("shell.audit.nuclear_deterrent"),
                    $"{tierLabel} · DEFCON {a.OwnNuclearPosture}",
                    a.OwnNuclearPosture <= 2 ? "shell.state.negative_text" : "shell.state.warning_text");
                AddSafeKvRow(ctx, card, Loc.Get("shell.audit.nuclear_doctrine"),
                    a.ActiveArmsControlPacts > 0
                        ? $"{docLabel} · {Loc.Format("shell.audit.arms_pacts_fmt", a.ActiveArmsControlPacts)}"
                        : docLabel,
                    "shell.paper_muted");
            }
            else
            {
                AddSafeKvRow(ctx, card, Loc.Get("shell.audit.global_defcon"),
                    $"DEFCON {a.GlobalDefconLevel}",
                    a.GlobalDefconLevel <= 2 ? "shell.state.negative_text" : "shell.state.warning_text");
            }
        }

        if (hasRank)
        {
            var rb = a.RankBreakdown;
            var btn = new Button
            {
                Name = "ShellOverviewRankBreakdownBtn",
                CustomMinimumSize = new Vector2(0, 26),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipContents = true,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = Ui.Tip(Loc.Format("shell.audit.rank_tip_fmt",
                    rb.OverallRank, rb.TotalRanked, rb.OverallScore.ToString("0.0", Loc.Culture),
                    rb.EconomyRank, rb.PopulationRank, rb.FieldPowerRank, rb.TechnologyRank, rb.PrestigeRank))
            };
            btn.AddThemeStyleboxOverride("normal", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 6, 2, 6, 2));
            btn.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 6, 2, 6, 2));
            btn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(padX: 6, padY: 2));
            btn.AddThemeStyleboxOverride("hover_pressed", ctx.PressedWellStyle(padX: 6, padY: 2));
            btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            var nid = nation.Id;
            btn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); ctx.OpenCountryRankings?.Invoke(nid); };
            card.AddChild(btn);

            var rRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            rRow.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            rRow.OffsetLeft = 6; rRow.OffsetRight = -6;
            rRow.AddThemeConstantOverride("separation", 6);
            btn.AddChild(rRow);
            rRow.AddChild(ShellCountryDossierView.MakeLabel(ctx,
                Loc.Format("shell.audit.rank_overall_fmt", rb.OverallRank, rb.TotalRanked, rb.OverallScore.ToString("0.0", Loc.Culture)),
                "shell.sans", 11, "shell.brass_hi", clip: true, expand: true, tabular: true));
            rRow.AddChild(ShellCountryDossierView.MakeLabel(ctx,
                Loc.Format("shell.audit.rank_sub_fmt", rb.EconomyRank, rb.FieldPowerRank, rb.TechnologyRank, rb.PrestigeRank),
                "shell.sans", 10, "shell.paper_faint", clip: false, expand: false, tabular: true));
        }

        for (var i = 0; i < a.ActiveCrises.Count; i++)
        {
            var c = a.ActiveCrises[i];
            AddSafeKvRow(ctx, card,
                Loc.Get(c.IsDisease ? "shell.audit.disease_label" : "shell.audit.disaster_label"),
                $"{Loc.Get(c.NameKey)} ({(int)MathF.Round(c.Severity)}%)", "shell.state.negative_text");
        }
    }

    private static void AddDomesticPressuresAndMovementsCard(
        ShellContext ctx, VBoxContainer root, CardTelemetryAudit.CountryAuditSnapshot a,
        bool canInspect, string cardName = "ShellOverviewDomesticPressuresCard")
    {
        var hasContent = a.ActiveMovementsCount > 0 || a.RestiveRegionsCount > 0 ||
                         a.AutonomousRegionsCount > 0 || a.ImperialOverstretch >= 0.01f ||
                         (canInspect && a.CoupPressure >= 0.12f);
        if (!hasContent) return;

        var badge = a.ActiveMovementsCount > 0
            ? Loc.Format("shell.audit.movements_count_fmt", a.ActiveMovementsCount)
            : a.RestiveRegionsCount > 0
                ? Loc.Format("shell.audit.restive_count_fmt", a.RestiveRegionsCount)
                : Loc.Format("shell.audit.auto_count_fmt", a.AutonomousRegionsCount);
        var badgeTok = a.MaxSecessionPressure >= 0.55f || a.CoupPressure >= a.CoupThreshold * 0.75f
            ? "shell.state.negative_text"
            : a.ActiveMovementsCount > 0 || a.RestiveRegionsCount > 0
                ? "shell.state.warning_text" : "shell.state.info";

        var card = ShellCountryDossierView.MakeSectionCard(
            ctx, root, cardName, "icon_unrest",
            Loc.Get("shell.audit.domestic_movements_separatism"), badge, badgeTok);

        if (a.RestiveRegionsCount > 0)
        {
            AddSafeKvRow(ctx, card, Loc.Get("shell.audit.separatism_pressure"),
                Loc.Format("shell.audit.separatism_val_fmt", a.RestiveRegionsCount, a.MostRestiveRegionName, Fmt.Percent(M.Clamp01(a.MaxSecessionPressure))),
                a.MaxSecessionPressure >= 0.55f ? "shell.state.negative_text" : "shell.state.warning_text");
        }
        if (a.AutonomousRegionsCount > 0)
        {
            AddSafeKvRow(ctx, card, Loc.Get("shell.audit.autonomous_regions"),
                a.AutonomousRegionsCount.ToString(CultureInfo.InvariantCulture), "shell.state.info");
        }
        if (a.ImperialOverstretch >= 0.01f)
        {
            AddSafeKvRow(ctx, card, Loc.Get("shell.audit.overstretch"),
                Fmt.Percent(a.ImperialOverstretch),
                a.ImperialOverstretch >= 0.15f ? "shell.state.negative_text" : "shell.state.warning_text");
        }
        if (canInspect && a.CoupPressure >= 0.12f)
        {
            ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_wars",
                Loc.Get("shell.audit.coup_pressure"), a.CoupPressure,
                Fmt.Percent(a.CoupPressure), ShellRegionVisualsView.DangerHighColor(a.CoupPressure),
                Loc.Format("shell.audit.coup_tip_fmt", Fmt.Percent(a.CoupPressure), Fmt.Percent(a.CoupThreshold)));
        }

        for (var i = 0; i < a.TopMovements.Count; i++)
        {
            var m = a.TopMovements[i];
            var btn = new Button
            {
                Name = $"ShellCountryMovement_{m.NationId}",
                CustomMinimumSize = new Vector2(0, 24),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipContents = true,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = Ui.Tip(Loc.Format("shell.audit.country_mov_tip_fmt",
                    m.Name, m.TopRegionName, Fmt.Percent(m.TopResidentSupport), Fmt.Percent(m.MaxUprisingChance)))
            };
            btn.AddThemeStyleboxOverride("normal", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 6, 2, 6, 2));
            btn.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 6, 2, 6, 2));
            btn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(padX: 6, padY: 2));
            btn.AddThemeStyleboxOverride("hover_pressed", ctx.PressedWellStyle(padX: 6, padY: 2));
            btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            var movId = m.NationId;
            btn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); ctx.OpenObservedMovement?.Invoke(movId); };
            card.AddChild(btn);

            var r = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            r.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            r.OffsetLeft = 6; r.OffsetRight = -6;
            r.AddThemeConstantOverride("separation", 6);
            btn.AddChild(r);
            var labelTxt = m.HasHeadquartersInside ? $"{m.Name} · {Loc.Get("shell.audit.hq_short")}" : m.Name;
            r.AddChild(ShellCountryDossierView.MakeLabel(ctx, labelTxt, "shell.sans", 11, "shell.paper", clip: true, expand: true));
            var statTxt = string.IsNullOrEmpty(m.TopRegionName)
                ? Fmt.Percent(m.TopResidentSupport)
                : $"{m.TopRegionName} ({Fmt.Percent(m.TopResidentSupport)})";
            var valTok = m.MaxUprisingChance >= 0.22f ? "shell.state.negative_text" : "shell.state.warning_text";
            r.AddChild(ShellCountryDossierView.MakeLabel(ctx, statTxt, "shell.sans", 10, valTok, clip: false, expand: false, tabular: true));
        }
    }

    private static void AddBrainDrainAndWarCard(
        ShellContext ctx, VBoxContainer root, CardTelemetryAudit.CountryAuditSnapshot a)
    {
        var hasDrain = MathF.Abs(a.BrainDrainRate) >= 0.01f || a.WeeklyNetMigration != 0;
        var hasWar = a.OpenWarCount > 0 || a.DamagedRegionsCount > 0;
        if (!hasDrain && !hasWar) return;

        if (hasWar)
        {
            var badge = a.OpenWarCount > 0
                ? Loc.Format("shell.audit.fatigue_badge_fmt", Fmt.Percent(a.WarFatigue))
                : Loc.Format("shell.audit.damaged_reg_fmt", a.DamagedRegionsCount);
            var card = ShellCountryDossierView.MakeSectionCard(
                ctx, root, "ShellOverviewWarTollCard", "icon_wars",
                Loc.Get("shell.audit.war_weariness_toll"), badge,
                a.WarFatigue >= 0.50f ? "shell.state.negative_text" : "shell.state.warning_text");

            if (a.OpenWarCount > 0)
            {
                var fatigueTxt = a.StalledWarCount > 0
                    ? $"{Fmt.Percent(a.WarFatigue)} · {Loc.Format("shell.audit.stalled_wars_fmt", a.StalledWarCount)}"
                    : Fmt.Percent(a.WarFatigue);
                ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_wars",
                    Loc.Get("shell.audit.war_fatigue"), a.WarFatigue, fatigueTxt,
                    ShellRegionVisualsView.DangerHighColor(a.WarFatigue),
                    Loc.Get("shell.audit.war_fatigue_tip"));
            }
            if (a.DamagedRegionsCount > 0)
            {
                AddSafeKvRow(ctx, card, Loc.Get("shell.audit.war_devastation"),
                    Loc.Format("shell.audit.war_devastation_val_fmt", a.DamagedRegionsCount, Fmt.Percent(a.AverageWarDamage)),
                    a.AverageWarDamage >= 0.30f ? "shell.state.negative_text" : "shell.state.warning_text");
            }
        }

        if (hasDrain)
            AddBrainDrainCard(ctx, root, a, "ShellOverviewBrainDrainCard");
    }

    private static void AddBrainDrainCard(
        ShellContext ctx, VBoxContainer root, CardTelemetryAudit.CountryAuditSnapshot a, string cardName)
    {
        var draining = a.BrainDrainRate < -0.005f;
        var gaining = a.BrainDrainRate > 0.005f;
        var badge = draining
            ? Loc.Format("shell.audit.drain_rate_fmt", $"{(a.BrainDrainRate * 100f):+0.0;-0.0;0.0}%")
            : gaining
                ? Loc.Format("shell.audit.gain_rate_fmt", $"{(a.BrainDrainRate * 100f):+0.0;-0.0;0.0}%")
                : Fmt.PeopleDelta(a.WeeklyNetMigration);
        var tok = draining ? "shell.state.warning_text" : gaining ? "shell.state.positive" : "shell.paper_muted";

        var card = ShellCountryDossierView.MakeSectionCard(
            ctx, root, cardName, "icon_research",
            Loc.Get("shell.audit.talent_migration"), badge, tok);

        if (draining && !string.IsNullOrEmpty(a.BrainDrainDestinationName))
            AddSafeKvRow(ctx, card, Loc.Get("shell.audit.drain_destination"), a.BrainDrainDestinationName, "shell.state.warning_text");
        if (gaining && !string.IsNullOrEmpty(a.BrainGainSourceName))
            AddSafeKvRow(ctx, card, Loc.Get("shell.audit.gain_source"), a.BrainGainSourceName, "shell.state.positive");
        if (a.WeeklyNetMigration != 0 || a.CumulativeNetMigration != 0)
        {
            AddSafeKvRow(ctx, card, Loc.Get("shell.audit.migration_flow"),
                $"{Fmt.PeopleDelta(a.WeeklyNetMigration)}/{Loc.Get("week")} ({Fmt.PeopleDelta(a.CumulativeNetMigration)})",
                a.WeeklyNetMigration < 0 ? "shell.state.warning_text" : "shell.paper");
        }
    }

    private static void AddPoliticalCohortsCard(
        ShellContext ctx, VBoxContainer root, CardTelemetryAudit.CountryAuditSnapshot a)
    {
        var badge = a.BlocsReviewed
            ? $"{PoliticalCohorts.BlocName(a.DominantBloc)} {Fmt.Percent(a.DominantBlocShare)}"
            : Fmt.Percent(a.GovernmentApproval);
        var card = ShellCountryDossierView.MakeSectionCard(
            ctx, root, "ShellSocietyCohortsCard", "icon_government",
            Loc.Get("shell.audit.public_mood_blocs"), badge, "shell.state.info");

        if (a.CohortsReviewed)
        {
            ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_government",
                Loc.Get("shell.audit.gov_approval"), a.GovernmentApproval, Fmt.Percent(a.GovernmentApproval),
                ShellRegionVisualsView.GoodHighColor(a.GovernmentApproval), Loc.Get("shell.audit.gov_approval_tip"));
            ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_laws",
                Loc.Get("shell.audit.law_approval"), a.LawApproval, Fmt.Percent(a.LawApproval),
                ShellRegionVisualsView.GoodHighColor(a.LawApproval), Loc.Get("shell.audit.law_approval_tip"));
            ShellRegionVisualsView.AddMeterRow(ctx, card, "icon_unrest",
                Loc.Get("shell.audit.pol_divergence"), a.PoliticalDivergence, Fmt.Percent(a.PoliticalDivergence),
                ShellRegionVisualsView.DangerHighColor(a.PoliticalDivergence), Loc.Get("shell.audit.pol_divergence_tip"));
        }
        if (a.BlocsReviewed)
        {
            AddSafeKvRow(ctx, card, Loc.Get("shell.audit.polarization"),
                Fmt.Percent(a.Polarization),
                a.Polarization > 0.55f ? "shell.state.warning_text" : "shell.paper_muted");
        }
    }
}
