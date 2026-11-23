using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void ShowOpportunityAtlas()
    {
        _formationConfirmation = "";
        ClearRuntimeChildren(_decisionControls);
        var nation = _campaign.Player;
        _sideBody.Text = Simulation.Loc.Format("Opportunities for {0} — an overview, not an instruction to spend.", nation.DisplayName);
        var snapshot = OpportunityAtlas.Read(_campaign, nation, InterfaceSkin.Metric("atlas.shortage_threshold", .985f));
        var limit = (int)Math.Clamp(InterfaceSkin.Metric("atlas.cards_per_section", 3), 1, 12);
        var groups = CampaignUiCatalog.For("Decisions").Tabs.FirstOrDefault(t => t.Id == "Opportunities")?.Groups ?? Array.Empty<string>();
        _decisionControls.AddChild(Title("Opportunity atlas", 19, Ui.Colour("text.accent")));
        _decisionControls.AddChild(Title("These are live conditions, not forecasts. Opening a card spends nothing and issues no orders.", 12, Ui.Colour("text.muted")));
        foreach (var group in groups.Distinct())
        {
            if (group == "formables")
            {
                AtlasHeading("New state identities", snapshot.Formations.Length, limit);
                if (snapshot.Formations.Length == 0)
                    _decisionControls.AddChild(Title("No unproclaimed state is defined for this founding country.", 13, Ui.Colour("text.muted")));
                foreach (var entry in snapshot.Formations.Take(limit))
                {
                    var definition = entry.Definition; var status = entry.Status;
                    var card = AtlasCard(definition.Name, status.CanForm ? "Ready to proclaim" : "Requirements remain", status.CanForm);
                    var flag = FlagSpriteTexture(FlagCatalog.ResolveFormationPreview(_campaign, nation, definition.Id));
                    if (flag is not null) card.AddChild(new TextureRect { Texture = flag, CustomMinimumSize = new Vector2(60, 36),
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        SizeFlagsHorizontal = SizeFlags.ShrinkBegin, TooltipText = Tr("Flag after formation") });
                    card.AddChild(Title(Simulation.Loc.Format("Territory owned and controlled: {0}/{1}", status.Owned, status.Total), 13, Ui.Colour("text.body")));
                    card.AddChild(Title(Simulation.Loc.Format("Founding cost: {0} ({1:P1} of GDP)", Fmt.Money(status.Cost), definition.CostShare), 12, Ui.Colour("text.accent_dim")));
                    foreach (var requirement in status.Requirements.Where(r => !r.Met))
                        card.AddChild(Title("• " + requirement.Label, 12, Ui.Colour("state.warning")));
                    if (AtlasHasTab("Formables")) card.AddChild(ActionButton("Review requirements", () => AtlasOpenTab("Formables", "FormNation_" + definition.Id), 0, 32));
                }
            }
            else if (group == "shortages")
            {
                AtlasHeading("Supply gaps", snapshot.Shortages.Length, limit);
                if (snapshot.Shortages.Length == 0)
                    _decisionControls.AddChild(Title("No shortage meets the atlas threshold in the latest market figures.", 13, Ui.Colour("text.muted")));
                foreach (var entry in snapshot.Shortages.Take(limit))
                {
                    var card = AtlasCard(CommodityCatalog.Get(entry.Good).Name, "Needs investigation", false);
                    card.AddChild(Title(Simulation.Loc.Format("Supply {0:P0}", entry.Satisfaction), 15, Ui.Colour("state.warning")));
                    card.AddChild(Title(Simulation.Loc.Format("Output {0:0.#} · demand {1:0.#} · imports {2:0.#} · exports {3:0.#}",
                        entry.Production, entry.Demand, entry.Imports, entry.Exports), 12, Ui.Colour("text.body_soft")));
                    card.AddChild(Title("Inspect production and trade before investing. More buildings alone may not resolve this gap.", 12, Ui.Colour("text.muted")));
                    if (CampaignUiCatalog.For("Economy").Tabs.Any(t => t.Id == "Market"))
                        card.AddChild(ActionButton("Inspect good", () =>
                        {
                            OpenEconomyTab("Market");
                            _shortagesOnly.ButtonPressed = false;
                            _marketSearch.Text = Tr(CommodityCatalog.Get(entry.Good).Name);
                            RefreshMarketTable();
                        }, 0, 32));
                }
            }
            else if (group == "decisions")
            {
                AtlasHeading("National initiatives", snapshot.Decisions.Length, limit);
                if (snapshot.Decisions.Length == 0) _decisionControls.AddChild(Title("No national decisions are defined.", 13, Ui.Colour("text.muted")));
                foreach (var entry in snapshot.Decisions.Take(limit))
                {
                    var definition = entry.Definition;
                    var card = AtlasCard(definition.Name, entry.Block.Length == 0 ? "Available now" : "Not available yet", entry.Block.Length == 0);
                    card.AddChild(Title(definition.Description, 12, Ui.Colour("text.body_soft")));
                    card.AddChild(Title(Simulation.Loc.Format("Cost {0} · cooldown {1} days", Fmt.Money(entry.Cost), definition.CooldownDays), 12, Ui.Colour("text.accent_dim")));
                    if (entry.Block.Length > 0)
                    {
                        var days = nation.DecisionAvailableDay.GetValueOrDefault(definition.Id) - _campaign.ElapsedDays;
                        card.AddChild(Title(days > 0 ? Simulation.Loc.Format("Available in {0} days", days) : Tr(entry.Block), 12, Ui.Colour("state.warning")));
                    }
                    if (AtlasHasTab("Decisions")) card.AddChild(ActionButton("Review decision", () => AtlasOpenTab("Decisions", "Decision_" + definition.Id), 0, 32));
                }
            }
        }

        BuildOpportunityWorldSituations(nation);
        BuildOpportunityDiseasesAndDisasters(nation);
        BuildOpportunityAmbitionsAndDirectives(nation);
    }

    private void BuildOpportunityDiseasesAndDisasters(Nation nation)
    {
        if (_campaign is null || nation is null || !DiseasesAndDisasters.IsEnabled(_campaign)) return;

        var healthCrises = DiseasesAndDisasters.ActiveHealthCrises(_campaign, nation);
        var disasters = DiseasesAndDisasters.ActiveDisasters(_campaign, nation);
        if (healthCrises.Count == 0 && disasters.Count == 0) return;

        // ---- Regional Public Health Crises (Wave 6H)
        if (healthCrises.Count > 0)
        {
            _decisionControls.AddChild(Title(Loc.Get("wave6.ui.health.heading"), 16, Ui.Colour("text.accent_dim")));
            var healthActions = new[]
            {
                HealthResponseAction.TargetedQuarantine,
                HealthResponseAction.MassVaccinationAndMedicalDrive,
                HealthResponseAction.EmergencyHealthSubsidies,
                HealthResponseAction.InternationalMedicalCooperation
            };

            for (var i = 0; i < healthCrises.Count; i++)
            {
                var brief = healthCrises[i];
                var tierLabel = Loc.Get(DiseasesAndDisasters.TierLocalizationKey(brief.SeverityTier));
                var stateLine = Loc.Format("wave6.ui.health.state_fmt",
                    brief.AffectedStateName, tierLabel, (int)MathF.Round(brief.Severity), brief.DaysRemaining);
                var card = AtlasCard(brief.Name, stateLine, brief.Severity < 45f);
                card.Name = "OpportunityHealthCrisis_" + brief.Id;

                if (!string.IsNullOrWhiteSpace(brief.Summary))
                {
                    var desc = Title(brief.Summary, 12, Ui.Colour("text.body_soft"));
                    desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    card.AddChild(desc);
                }

                var bar = new ProgressBar
                {
                    MinValue = 0, MaxValue = 100, Value = brief.Severity, ShowPercentage = false,
                    CustomMinimumSize = new Vector2(0, 8), SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.inset") });
                bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
                    { BgColor = Ui.Colour(brief.Severity >= 65f ? "state.negative" : "state.warning") });
                card.AddChild(bar);

                var metricsLbl = Title(Loc.Format("wave6.ui.health.metrics_fmt",
                    Fmt.People(brief.TotalDeaths), Fmt.Percent(brief.HospitalCoverage),
                    Fmt.Percent(brief.MedicineSupply), Fmt.Money(brief.TotalCostBillions)),
                    12, Ui.Colour("text.body"));
                metricsLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
                card.AddChild(metricsLbl);

                if (brief.ActiveResponse != HealthResponseAction.None && !string.IsNullOrWhiteSpace(brief.ActiveResponseLabel))
                    card.AddChild(Title(Loc.Format("wave6.ui.health.active_resp_fmt", brief.ActiveResponseLabel), 12, Ui.Colour("state.positive")));

                var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
                flow.AddThemeConstantOverride("h_separation", 6);
                flow.AddThemeConstantOverride("v_separation", 6);
                card.AddChild(flow);

                foreach (var act in healthActions)
                {
                    DiseasesAndDisasters.HealthActionCost(_campaign, nation, brief.Id, act, out var cashCost, out var ppCost);
                    var refusal = "";
                    var canAct = !_observerMode && DiseasesAndDisasters.CanRespondHealth(_campaign, nation, brief.Id, act, out refusal);
                    var actLabel = Loc.Get(DiseasesAndDisasters.ActionLocalizationKey(act));
                    var btnText = Loc.Format("wave6.ui.crisis.action_cost_fmt", actLabel, Fmt.Money(cashCost), ppCost);

                    var capturedId = brief.Id;
                    var capturedAct = act;
                    var btn = ActionButton(btnText, () =>
                    {
                        if (_observerMode || _campaign?.Player is not { } p) return;
                        _campaign.Commands.Enqueue(new HealthResponseCommand
                        {
                            IssuerId = p.Id,
                            CrisisId = capturedId,
                            Action = capturedAct
                        });
                        _campaign.Commands.ApplyAll(_campaign);
                        ShowOpportunityAtlas();
                        Refresh();
                    }, 0, 30);
                    btn.Name = $"HealthAction_{brief.Id}_{act}";
                    btn.Disabled = !canAct;
                    btn.AddThemeFontOverride("font", PresentationFonts.Tabular());
                    btn.TooltipText = Ui.Tip(_observerMode
                        ? Loc.Get("shell.obs.readonly_banner")
                        : (!canAct && refusal.Length > 0 ? refusal : btnText));
                    flow.AddChild(btn);
                }
            }
        }

        // ---- Regional Natural Disasters (Wave 6H)
        if (disasters.Count > 0)
        {
            _decisionControls.AddChild(Title(Loc.Get("wave6.ui.disasters.heading"), 16, Ui.Colour("text.accent_dim")));
            var reliefActions = new[]
            {
                DisasterReliefAction.EmergencyEvacuationAndRescue,
                DisasterReliefAction.InfrastructureReconstruction,
                DisasterReliefAction.StrategicReserveAid,
                DisasterReliefAction.CivilDefenseMobilization
            };

            for (var i = 0; i < disasters.Count; i++)
            {
                var brief = disasters[i];
                var tierLabel = Loc.Get(DiseasesAndDisasters.TierLocalizationKey(brief.SeverityTier));
                var stateLine = Loc.Format("wave6.ui.disasters.state_fmt",
                    brief.AffectedStateName, tierLabel, (int)MathF.Round(brief.Severity), brief.DaysRemaining);
                var card = AtlasCard(brief.Name, stateLine, brief.Severity < 45f);
                card.Name = "OpportunityDisaster_" + brief.Id;

                if (!string.IsNullOrWhiteSpace(brief.Summary))
                {
                    var desc = Title(brief.Summary, 12, Ui.Colour("text.body_soft"));
                    desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    card.AddChild(desc);
                }

                var bar = new ProgressBar
                {
                    MinValue = 0, MaxValue = 100, Value = brief.Severity, ShowPercentage = false,
                    CustomMinimumSize = new Vector2(0, 8), SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.inset") });
                bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
                    { BgColor = Ui.Colour(brief.Severity >= 65f ? "state.negative" : "state.warning") });
                card.AddChild(bar);

                var metricsLbl = Title(Loc.Format("wave6.ui.disasters.metrics_fmt",
                    Fmt.Percent(brief.InstantDamageApplied), Fmt.People(brief.TotalDeaths),
                    Fmt.Percent(brief.DisasterPreparedness), Fmt.Money(brief.TotalCostBillions)),
                    12, Ui.Colour("text.body"));
                metricsLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
                card.AddChild(metricsLbl);

                if (brief.ActiveResponse != DisasterReliefAction.None && !string.IsNullOrWhiteSpace(brief.ActiveResponseLabel))
                    card.AddChild(Title(Loc.Format("wave6.ui.disasters.active_resp_fmt", brief.ActiveResponseLabel), 12, Ui.Colour("state.positive")));

                var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
                flow.AddThemeConstantOverride("h_separation", 6);
                flow.AddThemeConstantOverride("v_separation", 6);
                card.AddChild(flow);

                foreach (var act in reliefActions)
                {
                    DiseasesAndDisasters.DisasterActionCost(_campaign, nation, brief.Id, act, out var cashCost, out var ppCost);
                    var refusal = "";
                    var canAct = !_observerMode && DiseasesAndDisasters.CanRespondDisaster(_campaign, nation, brief.Id, act, out refusal);
                    var actLabel = Loc.Get(DiseasesAndDisasters.ActionLocalizationKey(act));
                    var btnText = Loc.Format("wave6.ui.crisis.action_cost_fmt", actLabel, Fmt.Money(cashCost), ppCost);

                    var capturedId = brief.Id;
                    var capturedAct = act;
                    var btn = ActionButton(btnText, () =>
                    {
                        if (_observerMode || _campaign?.Player is not { } p) return;
                        _campaign.Commands.Enqueue(new DisasterReliefCommand
                        {
                            IssuerId = p.Id,
                            DisasterId = capturedId,
                            Action = capturedAct
                        });
                        _campaign.Commands.ApplyAll(_campaign);
                        ShowOpportunityAtlas();
                        Refresh();
                    }, 0, 30);
                    btn.Name = $"DisasterAction_{brief.Id}_{act}";
                    btn.Disabled = !canAct;
                    btn.AddThemeFontOverride("font", PresentationFonts.Tabular());
                    btn.TooltipText = Ui.Tip(_observerMode
                        ? Loc.Get("shell.obs.readonly_banner")
                        : (!canAct && refusal.Length > 0 ? refusal : btnText));
                    flow.AddChild(btn);
                }
            }
        }
    }

    private void BuildOpportunityWorldSituations(Nation nation)
    {
        if (_campaign is null || nation is null || !WorldSituations.IsEnabled(_campaign)) return;
        var active = WorldSituations.ActiveSituations(_campaign);
        _decisionControls.AddChild(Title(Loc.Get("wave6.ui.situations.heading"), 16, Ui.Colour("text.accent_dim")));
        if (active.Count == 0)
        {
            _decisionControls.AddChild(Title(Loc.Get("wave6.ui.situations.none"), 13, Ui.Colour("text.muted")));
            return;
        }

        var actions = new[]
        {
            (SituationActionKind.ConveneEmergencySummit, "world_situation.action.convene_summit"),
            (SituationActionKind.FundStabilizationPackage, "world_situation.action.fund_stabilization"),
            (SituationActionKind.ExploitStrategicLeverage, "world_situation.action.exploit_leverage"),
            (SituationActionKind.BrokerCompromise, "world_situation.action.broker_compromise")
        };

        for (var i = 0; i < active.Count; i++)
        {
            var inst = active[i];
            var brief = WorldSituations.Describe(_campaign, inst, nation);
            if (brief is null) continue;

            var stateLine = Loc.Format("wave6.ui.situations.state_fmt",
                Loc.Get(brief.Category), brief.Intensity, brief.DaysRemaining);
            var card = AtlasCard(brief.Title, stateLine, brief.Intensity < 65f);
            card.Name = "OpportunitySituation_" + inst.Id;

            if (!string.IsNullOrWhiteSpace(brief.Summary))
            {
                var summaryLbl = Title(brief.Summary, 12, Ui.Colour("text.body_soft"));
                summaryLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                card.AddChild(summaryLbl);
            }

            var bar = new ProgressBar
            {
                MinValue = 0, MaxValue = 100, Value = brief.Intensity, ShowPercentage = false,
                CustomMinimumSize = new Vector2(0, 8), SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.inset") });
            bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
                { BgColor = Ui.Colour(brief.Intensity >= 70f ? "state.negative" : "state.warning") });
            card.AddChild(bar);

            var coalLbl = Title(Loc.Format("wave6.ui.situations.coalitions_fmt",
                brief.StabilizerCount, brief.StabilizerWeight, brief.ExploiterCount, brief.ExploiterWeight),
                12, Ui.Colour("text.body"));
            coalLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            card.AddChild(coalLbl);

            if (brief.SummitConvened && !string.IsNullOrEmpty(brief.SummitHostTag))
                card.AddChild(Title(Loc.Format("wave6.ui.situations.summit_host_fmt", brief.SummitHostTag), 12, Ui.Colour("state.positive")));

            var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            flow.AddThemeConstantOverride("h_separation", 6);
            flow.AddThemeConstantOverride("v_separation", 6);
            card.AddChild(flow);

            foreach (var (kind, locKey) in actions)
            {
                WorldSituations.ActionCost(_campaign, nation, inst, kind, out var cashCost, out var ppCost);
                var refusal = "";
                var canAct = !_observerMode && WorldSituations.CanIntervene(_campaign, nation, inst.Id, kind, out refusal);
                var labelText = cashCost > 0f
                    ? Loc.Format("wave6.ui.situations.action_cost_cash_pp_fmt", Loc.Get(locKey), Fmt.Money(cashCost), ppCost)
                    : Loc.Format("wave6.ui.situations.action_cost_pp_fmt", Loc.Get(locKey), ppCost);

                var capturedId = inst.Id;
                var capturedKind = kind;
                var btn = ActionButton(labelText, () =>
                {
                    if (_observerMode || _campaign?.Player is not { } p) return;
                    _campaign.Commands.Enqueue(new SituationInterventionCommand
                    {
                        IssuerId = p.Id,
                        SituationId = capturedId,
                        Action = capturedKind
                    });
                    _campaign.Commands.ApplyAll(_campaign);
                    ShowOpportunityAtlas();
                    Refresh();
                }, 0, 30);
                btn.Name = $"SituationAction_{inst.Id}_{kind}";
                btn.Disabled = !canAct;
                btn.AddThemeFontOverride("font", PresentationFonts.Tabular());
                btn.TooltipText = Ui.Tip(_observerMode
                    ? Loc.Get("shell.obs.readonly_banner")
                    : (!canAct && refusal.Length > 0 ? Loc.Get(refusal) : labelText));
                flow.AddChild(btn);
            }
        }
    }

    private void BuildOpportunityAmbitionsAndDirectives(Nation nation)
    {
        if (_campaign is null || nation is null) return;

        // ---- National Ambitions & Golden Era
        _decisionControls.AddChild(Title(Loc.Get("wave6.ui.ambitions.heading"), 16, Ui.Colour("text.accent_dim")));
        if (NationalAmbitions.InGoldenEra(_campaign, nation))
        {
            var days = NationalAmbitions.GoldenEraDaysRemaining(_campaign, nation);
            var goldenBanner = WorkbenchCard(_decisionControls);
            goldenBanner.Name = "OpportunityGoldenEraBanner";
            var gLbl = Title(Loc.Format("wave6.ui.ambitions.golden_era_fmt", days), 14, Ui.Colour("text.accent"));
            gLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            goldenBanner.AddChild(gLbl);
        }

        var activeAmbitions = NationalAmbitions.ActiveFor(_campaign, nation);
        for (var i = 0; i < activeAmbitions.Count; i++)
        {
            var slot = activeAmbitions[i];
            var def = NationalAmbitions.FindAmbition(slot.Id);
            if (def is null) continue;
            var pct = (int)MathF.Round(Math.Clamp(slot.Progress, 0f, 1f) * 100f);
            var card = AtlasCard(Loc.Get(def.Name), Loc.Format("wave6.ui.ambitions.active_state_fmt", pct), pct >= 75);
            card.Name = "OpportunityActiveAmbition_" + def.Id;

            if (!string.IsNullOrWhiteSpace(def.Description))
            {
                var desc = Title(Loc.Get(def.Description), 12, Ui.Colour("text.body_soft"));
                desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                card.AddChild(desc);
            }
            var bar = new ProgressBar
            {
                MinValue = 0, MaxValue = 100, Value = pct, ShowPercentage = false,
                CustomMinimumSize = new Vector2(0, 8), SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.inset") });
            bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Colour("state.positive") });
            card.AddChild(bar);

            var rew = Title(Loc.Format("wave6.ui.ambitions.reward_fmt", def.GoldenEraDays, def.RewardPp, def.RewardXp),
                12, Ui.Colour("text.accent_dim"));
            rew.AddThemeFontOverride("font", PresentationFonts.Tabular());
            card.AddChild(rew);
        }

        var availableAmbitions = NationalAmbitions.AvailableFor(_campaign, nation);
        if (availableAmbitions.Count == 0 && activeAmbitions.Count == 0)
        {
            _decisionControls.AddChild(Title(Loc.Get("wave6.ui.ambitions.none_available"), 13, Ui.Colour("text.muted")));
        }
        else
        {
            var showCount = Math.Min(availableAmbitions.Count, 4);
            for (var i = 0; i < showCount; i++)
            {
                var def = availableAmbitions[i];
                var refusal = "";
                var canSelect = !_observerMode && NationalAmbitions.CanSelectAmbition(_campaign, nation, def.Id, out refusal);
                var card = AtlasCard(Loc.Get(def.Name), Loc.Get("wave6.ui.ambitions.available_state"), canSelect);
                card.Name = "OpportunityAvailableAmbition_" + def.Id;

                if (!string.IsNullOrWhiteSpace(def.Description))
                {
                    var desc = Title(Loc.Get(def.Description), 12, Ui.Colour("text.body_soft"));
                    desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    card.AddChild(desc);
                }
                var rew = Title(Loc.Format("wave6.ui.ambitions.reward_fmt", def.GoldenEraDays, def.RewardPp, def.RewardXp),
                    12, Ui.Colour("text.accent_dim"));
                rew.AddThemeFontOverride("font", PresentationFonts.Tabular());
                card.AddChild(rew);

                var capturedId = def.Id;
                var btn = ActionButton(Loc.Get("wave6.ui.ambitions.adopt"), () =>
                {
                    if (_observerMode || _campaign?.Player is not { } p) return;
                    _campaign.Commands.Enqueue(new SelectAmbitionCommand { IssuerId = p.Id, AmbitionId = capturedId });
                    _campaign.Commands.ApplyAll(_campaign);
                    ShowOpportunityAtlas();
                    Refresh();
                }, 0, 30);
                btn.Name = "SelectAmbition_" + def.Id;
                btn.Disabled = !canSelect;
                btn.TooltipText = Ui.Tip(_observerMode
                    ? Loc.Get("shell.obs.readonly_banner")
                    : (!canSelect && refusal.Length > 0 ? refusal : Loc.Get(def.Description)));
                card.AddChild(btn);
            }
        }

        // ---- Executive Cabinet Directives
        var directives = NationalAmbitions.Directives;
        if (directives.Count == 0) return;
        _decisionControls.AddChild(Title(Loc.Get("wave6.ui.directives.heading"), 16, Ui.Colour("text.accent_dim")));
        for (var i = 0; i < directives.Count; i++)
        {
            var dir = directives[i];
            var isActive = NationalAmbitions.IsDirectiveActive(_campaign, nation, dir.Id);
            var cooldown = NationalAmbitions.DirectiveCooldownRemaining(_campaign, nation, dir.Id);
            var refusal = "";
            var canExec = !_observerMode && NationalAmbitions.CanExecuteDirective(_campaign, nation, dir.Id, out refusal);
            var state = isActive
                ? Loc.Get("wave6.ui.directives.active_state")
                : cooldown > 0
                    ? Loc.Format("wave6.ui.directives.cooldown_fmt", cooldown)
                    : Loc.Get("wave6.ui.directives.ready_state");

            var card = AtlasCard(Loc.Get(dir.Name), state, isActive || canExec);
            card.Name = "OpportunityDirective_" + dir.Id;

            if (!string.IsNullOrWhiteSpace(dir.Description))
            {
                var desc = Title(Loc.Get(dir.Description), 12, Ui.Colour("text.body_soft"));
                desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                card.AddChild(desc);
            }

            var (cash, pp) = NationalAmbitions.DirectiveCost(nation, dir);
            var costLbl = Title(Loc.Format("wave6.ui.directives.cost_fmt", Fmt.Money(cash), pp, dir.DurationDays),
                12, Ui.Colour("text.accent_dim"));
            costLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            card.AddChild(costLbl);

            var capturedDirId = dir.Id;
            var btn = ActionButton(Loc.Get("wave6.ui.directives.enact"), () =>
            {
                if (_observerMode || _campaign?.Player is not { } p) return;
                _campaign.Commands.Enqueue(new ExecuteCabinetDirectiveCommand { IssuerId = p.Id, DirectiveId = capturedDirId });
                _campaign.Commands.ApplyAll(_campaign);
                ShowOpportunityAtlas();
                Refresh();
            }, 0, 30);
            btn.Name = "ExecuteDirective_" + dir.Id;
            btn.Disabled = !canExec;
            btn.TooltipText = Ui.Tip(_observerMode
                ? Loc.Get("shell.obs.readonly_banner")
                : (!canExec && refusal.Length > 0 ? refusal : Loc.Get(dir.Description)));
            card.AddChild(btn);
        }
    }

    private static bool AtlasHasTab(string id) => CampaignUiCatalog.For("Decisions").Tabs.Any(t => t.Id == id);
    private void AtlasHeading(string title, int count, int limit)
    {
        _decisionControls.AddChild(Title(title, 16, Ui.Colour("text.accent_dim")));
        if (count > limit) _decisionControls.AddChild(Title(Simulation.Loc.Format("Showing {0} of {1}; the full list is in the corresponding tab.", limit, count), 12, Ui.Colour("text.muted")));
    }
    private VBoxContainer AtlasCard(string title, string state, bool ready)
    {
        var card = WorkbenchCard(_decisionControls);
        card.AddChild(Title(title, 16, Ui.Colour("text.bright")));
        card.AddChild(Title(state, 12, Ui.Colour(ready ? "state.positive" : "state.warning")));
        return card;
    }
    private void AtlasOpenTab(string tab, string targetName)
    {
        SelectDossierTab(tab);
        if (tab == "Decisions" && targetName.StartsWith("Decision_", StringComparison.Ordinal))
            SelectAgendaEntry(AgendaWorkspace.Section.Decisions, targetName[9..]);
        var target = _decisionControls.FindChildren("*", "Button", true, false).OfType<Control>().FirstOrDefault(c => c.Name == targetName);
        if (target?.GetParent() is Control card)
            _ = AtlasScrollToCard(card, tab);
    }

    private async System.Threading.Tasks.Task AtlasScrollToCard(Control card, string tab)
    {
        // New cards need a completed container layout before measuring their position.
        var tree = GetTree();
        await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        if (!GodotObject.IsInstanceValid(card) || !card.IsInsideTree() || !card.IsVisibleInTree() || _dossierTab != tab) return;
        // Align the heading, not just the action button, so the player sees what they opened.
        _dossierScroll.ScrollVertical += (int)Math.Round(card.GlobalPosition.Y - _dossierScroll.GlobalPosition.Y);
    }
}
