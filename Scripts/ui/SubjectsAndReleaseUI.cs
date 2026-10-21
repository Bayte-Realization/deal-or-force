using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _subjectsModalLayer;
    private VBoxContainer _subjectsModalBody;
    private Label _subjectsModalFeedback;
    private int _subjectsModalSelectedId = -1;
    private string _pendingSubjectMergeKey = "";

    private Control _releaseModalLayer;
    private VBoxContainer _releaseModalBody;
    private Label _releaseModalFeedback;
    private Label _releaseModalTitle;
    private readonly HashSet<int> _releaseSelectedStates = new();
    private int _releaseToSubjectId = -1;
    private bool _releaseAsSubject = true;
    private string _releaseSubjectType = "puppet";
    private string _releaseCulture = "";
    private string _releaseReligion = "";
    private string _releaseGovernment = "";
    private string _releaseIdeology = "";
    private string _releaseCustomName = "";

    private VBoxContainer _rulersCabinetHost;
    private Label _rulersCabinetFeedback;
    private LineEdit _customCountryNameInput;
    private OptionButton _customCountryFlagPicker;

    private void EnsureSubjectsAndReleaseModals()
    {
        if (_hudLayer is null) return;
        if (_subjectsModalLayer is null)
        {
            _subjectsModalLayer = FullLayer("SubjectsManagerModal");
            _subjectsModalLayer.Visible = false;
            _hudLayer.AddChild(_subjectsModalLayer);

            var shade = new ColorRect
            {
                Color = new Color(0.015f, 0.026f, 0.041f, 0.76f),
                MouseFilter = MouseFilterEnum.Stop
            };
            shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _subjectsModalLayer.AddChild(shade);

            var panel = TexturedPanel(_subjectsModalLayer);
            Ui.Place(panel, "modal.ledger");
            var rows = ScrollablePanelRows(panel, 22, 16, 10);

            var header = new HBoxContainer();
            header.AddThemeConstantOverride("separation", 8);
            rows.AddChild(header);
            var title = Title("MANAGE SUBJECTS & SATELLITES", 23, Ui.Colour("text.accent"));
            title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            header.AddChild(title);
            header.AddChild(ActionButton("Release new country...", () =>
            {
                CloseSubjectsModal();
                OpenReleaseCountryModal();
            }, 190, 32, true));
            header.AddChild(ActionButton("×", CloseSubjectsModal, 38, 32));

            _subjectsModalFeedback = Title("", 12, Ui.Colour("text.accent_dim"));
            _subjectsModalFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            rows.AddChild(_subjectsModalFeedback);

            _subjectsModalBody = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _subjectsModalBody.AddThemeConstantOverride("separation", 10);
            rows.AddChild(_subjectsModalBody);
        }

        if (_releaseModalLayer is null)
        {
            _releaseModalLayer = FullLayer("ReleaseCountryModal");
            _releaseModalLayer.Visible = false;
            _hudLayer.AddChild(_releaseModalLayer);

            var shade = new ColorRect
            {
                Color = new Color(0.015f, 0.026f, 0.041f, 0.76f),
                MouseFilter = MouseFilterEnum.Stop
            };
            shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _releaseModalLayer.AddChild(shade);

            var panel = TexturedPanel(_releaseModalLayer);
            Ui.Place(panel, "modal.ledger");
            var rows = ScrollablePanelRows(panel, 22, 16, 10);

            var header = new HBoxContainer();
            header.AddThemeConstantOverride("separation", 8);
            rows.AddChild(header);
            var title = Title("RELEASE A NEW COUNTRY", 23, Ui.Colour("text.accent"));
            _releaseModalTitle = title;
            title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            header.AddChild(title);
            header.AddChild(ActionButton("Manage existing subjects", () =>
            {
                CloseReleaseCountryModal();
                OpenSubjectsModal();
            }, 200, 32));
            header.AddChild(ActionButton("×", CloseReleaseCountryModal, 38, 32));

            _releaseModalFeedback = Title("", 12, Ui.Colour("text.accent_dim"));
            _releaseModalFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            rows.AddChild(_releaseModalFeedback);

            _releaseModalBody = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _releaseModalBody.AddThemeConstantOverride("separation", 10);
            rows.AddChild(_releaseModalBody);
        }
    }

    internal void OpenSubjectsModal(Nation focusSubject = null)
    {
        if (_campaign?.Player is null) return;
        EnsureSubjectsAndReleaseModals();
        if (focusSubject is not null) _subjectsModalSelectedId = focusSubject.Id;
        _subjectsModalFeedback.Text = "";
        _pendingSubjectMergeKey = "";
        RaiseWorkspaceLayer(_subjectsModalLayer);
        RefreshSubjectsModal();
    }

    internal void CloseSubjectsModal()
    {
        if (_subjectsModalLayer is not null) _subjectsModalLayer.Visible = false;
    }

    private void RefreshSubjectsModal()
    {
        if (_subjectsModalLayer is null || !_subjectsModalLayer.Visible || _campaign?.Player is null) return;
        ClearRuntimeChildren(_subjectsModalBody);
        var player = _campaign.Player;
        var subjects = SubjectSystem.SubjectsOf(_campaign, player);

        if (subjects.Count == 0)
        {
            _subjectsModalBody.AddChild(Title(Tr("Your country currently holds no satellites, protectorates or dominions."), 14, Ui.Colour("text.body_soft")));
            _subjectsModalBody.AddChild(Title(Tr("You can bring smaller states under your wing through Diplomacy (Offer protection, Buy influence, Back a government), impose a puppet at a peace conference, or release a region from your own territory."), 12, Ui.Colour("text.muted")));
        }

        foreach (var subject in subjects)
        {
            var captured = subject;
            var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            card.AddThemeStyleboxOverride("panel", WarInsetStyle(16, 12));
            _subjectsModalBody.AddChild(card);

            var stack = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            stack.AddThemeConstantOverride("separation", 8);
            card.AddChild(stack);

            var top = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            top.AddThemeConstantOverride("separation", 10);
            stack.AddChild(top);

            top.AddChild(FlagBadge(subject.Tag, 34));
            var leader = Rulers.HeadOfState(_campaign, subject);
            top.AddChild(BuildLeaderPortraitBadge(leader, subject.Color, 42, 52));

            var titleCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            titleCol.AddThemeConstantOverride("separation", 1);
            top.AddChild(titleCol);
            var typeDef = SubjectCatalog.Of(subject.SubjectType);
            titleCol.AddChild(Title($"{subject.DisplayName} ({subject.Tag}) · {Tr(typeDef?.Name ?? subject.SubjectType)}", 16, Ui.Colour("text.bright")));
            titleCol.AddChild(Title($"{Tr(leader.Title)}: {leader.Name} · {Tr(PoliticsCore.GovernmentOf(subject).Name)} · {Tr(PoliticsCore.IdeologyOf(subject).Name)}", 12, Ui.Colour("text.caption")));

            var lib = subject.LibertyDesire;
            var want = SubjectSystem.LibertyDesireOf(_campaign, subject);
            var libCol = lib >= 0.70f ? "state.negative" : lib >= 0.40f ? "state.warning" : "state.positive";

            stack.AddChild(BuildGrandStrategyMetricsGrid(
                ("Population", Fmt.People(subject.Population), "text.bright", ""),
                ("GDP", Fmt.Money(subject.Gdp), "text.bright", ""),
                ("Liberty desire", $"{lib:P0} (target {want:P0})", libCol, Tr("At 100% the subject breaks free; above 65% it may sabotage tribute or rebel.")),
                ("Weekly tribute", Fmt.Money(subject.TributePaidWeekly), "state.positive", ""),
                ("Integration", subject.SubjectIntegrating ? $"{subject.SubjectIntegrationProgress:P0} (active)" : $"{subject.SubjectIntegrationProgress:P0}",
                    subject.SubjectIntegrating ? "text.accent" : "text.body_soft",
                    Tr("Administrative integration progress toward full annexation.")),
                ("Stability", Fmt.Percent(subject.Stability), subject.Stability < 0.35f ? "state.negative" : "text.bright", "")
            ));

            if (subject.SubjectConspiring)
            {
                var peers = SubjectInteractions.ConspiringSubjectsOf(_campaign, player)
                    .Where(p => p.Id != subject.Id)
                    .Select(p => p.DisplayName)
                    .ToList();
                var peerNames = peers.Count > 0 ? string.Join(", ", peers) : Tr("fellow satellites");
                stack.AddChild(Title(Simulation.Loc.Format("Secret liberty pact (conspiracy) with: {0}", peerNames), 12, Ui.Colour("state.negative")));
            }
            if (subject.SubjectForeignBackers.Count > 0)
            {
                var backerNames = subject.SubjectForeignBackers
                    .Select(id => _campaign.NationById(id)?.DisplayName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();
                if (backerNames.Count > 0)
                    stack.AddChild(Title(Simulation.Loc.Format("Foreign independence backers: {0}", string.Join(", ", backerNames)), 12, Ui.Colour("state.warning")));
            }

            if (subject.SubjectIntegrating || subject.SubjectIntegrationProgress > 0f)
            {
                var bar = new ProgressBar
                {
                    MinValue = 0, MaxValue = 100,
                    Value = subject.SubjectIntegrationProgress * 100,
                    ShowPercentage = true,
                    CustomMinimumSize = new Vector2(0, 14)
                };
                stack.AddChild(bar);
            }

            // Friendly / Soft Pacification row
            stack.AddChild(Title(Tr("CONCILIATION & AUTONOMY (SOFT)"), 11, Ui.Colour("text.caption")));
            var softFlow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            softFlow.AddThemeConstantOverride("h_separation", 6);
            softFlow.AddThemeConstantOverride("v_separation", 6);
            stack.AddChild(softFlow);

            AddSubjectActionButton(softFlow, captured, SubjectInteractionKind.GrantSubsidies,
                Simulation.Loc.Format("Send subsidies ({0})", Fmt.Money(SubjectInteractions.SubsidyCost(_campaign, captured))),
                "Pay development funds to lower their liberty desire by 16% and warm relations.");
            AddSubjectActionButton(softFlow, captured, SubjectInteractionKind.TributeHoliday,
                "Grant tribute holiday (1 yr)",
                "Waive weekly tribute for one year to calm liberty desire.");
            AddSubjectActionButton(softFlow, captured, SubjectInteractionKind.LoosenAutonomy,
                "Loosen autonomy",
                "Step the dependency toward greater self-rule (e.g. Puppet -> Satellite -> Protectorate -> Dominion), lowering liberty desire.");

            // Coercive / Hard Pacification & Extraction row
            stack.AddChild(Title(Tr("COERCION, EXTRACTION & INTEGRATION (HARD)"), 11, Ui.Colour("text.caption")));
            var hardFlow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            hardFlow.AddThemeConstantOverride("h_separation", 6);
            hardFlow.AddThemeConstantOverride("v_separation", 6);
            stack.AddChild(hardFlow);

            AddSubjectActionButton(hardFlow, captured, SubjectInteractionKind.TightenAutonomy,
                "Tighten autonomy",
                "Step the dependency toward tighter control and +12% integration progress, at the cost of +16% liberty desire.");
            AddSubjectActionButton(hardFlow, captured, SubjectInteractionKind.MartialCrackdown,
                "Martial garrison crackdown",
                "Deploy troops to suppress immediate liberty desire (-25%), costing manpower, stability and international notoriety.");
            AddSubjectActionButton(hardFlow, captured, SubjectInteractionKind.ReplaceGovernor,
                "Install loyal cabinet",
                "Replace their ruler and align their politics with ours (+10% integration, -10% liberty desire).");
            AddSubjectActionButton(hardFlow, captured, SubjectInteractionKind.SiphonTreasury,
                Simulation.Loc.Format("Siphon treasury (+{0})", Fmt.Money(SubjectInteractions.SiphonGain(captured))),
                "Extract emergency funds from their treasury (+20% liberty desire).");
            AddSubjectActionButton(hardFlow, captured, SubjectInteractionKind.DraftLevies,
                Simulation.Loc.Format("Conscript colonial levies (+{0:N0})", (long)SubjectInteractions.LevyGain(captured)),
                "Draft a quarter of their manpower reserves into our army (+18% liberty desire).");
            if (captured.SubjectConspiring || captured.SubjectForeignBackers.Count > 0)
            {
                AddSubjectActionButton(hardFlow, captured, SubjectInteractionKind.BreakConspiracy,
                    "Break conspiracy & expel agents",
                    "Dismantle inter-subject liberty pacts and expel foreign agitators (-12% liberty desire), at the risk of provoking an immediate uprising if liberty desire is above 85%.");
            }

            // Integration / Annexation / Independence row
            var finalFlow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            finalFlow.AddThemeConstantOverride("h_separation", 6);
            finalFlow.AddThemeConstantOverride("v_separation", 6);
            stack.AddChild(finalFlow);

            var grantRegions = ActionButton(Tr("Transfer regions..."), () =>
            {
                _subjectsModalLayer.Visible = false;
                OpenReleaseCountryModal(grantTo: captured);
            }, 0, 32);
            grantRegions.Name = "GrantRegionsTo" + captured.Id;
            if (SubjectCatalog.Of(captured.SubjectType)?.ReceivesLandGrants == false)
            {
                grantRegions.Disabled = true;
                grantRegions.TooltipText = Ui.Tip(Simulation.Loc.Get(
                    "This kind of dependency cannot receive regional land grants."));
            }
            finalFlow.AddChild(grantRegions);

            var quickPackage = RegionalAdministration.QuickGrantPackage(_campaign, player, captured);
            if (quickPackage.Length > 0)
            {
                var mayQuick = RegionalAdministration.MayGrant(_campaign, player, captured, quickPackage, out var quickPrice, out var quickWhy);
                var quickBtn = ActionButton(Simulation.Loc.Format("Feed adjacent package (+{0})", quickPackage.Length), () =>
                {
                    if (RegionalAdministration.MayGrant(_campaign, player, captured, quickPackage, out _, out var why))
                    {
                        _campaign.Commands.Enqueue(new GrantSubjectRegionsCommand
                        {
                            IssuerId = player.Id,
                            SubjectId = captured.Id,
                            StateMapIndices = quickPackage
                        });
                        _campaign.Commands.ApplyAll(_campaign);
                        _subjectsModalFeedback.Text = Simulation.Loc.Format("Transferred {0} region(s) to {1}.", quickPackage.Length, captured.DisplayName);
                        RefreshSubjectsModal();
                        if (_diplomacyLayer is { Visible: true }) ForceDiplomacyRefresh();
                        Refresh();
                    }
                    else _subjectsModalFeedback.Text = why;
                }, 0, 32);
                quickBtn.Disabled = !mayQuick;
                quickBtn.TooltipText = Ui.Tip(mayQuick
                    ? Simulation.Loc.Format("Administrative cost: {0:0.#} political points", quickPrice)
                    : quickWhy);
                finalFlow.AddChild(quickBtn);
            }

            var merge = SubjectBlocs.BestFor(_campaign, player, captured);
            if (merge is { } candidate)
            {
                var pair = candidate;
                var mergeKey = SubjectBlocs.MergeKey(pair.Keep, pair.Absorb);
                var confirming = string.Equals(_pendingSubjectMergeKey, mergeKey, StringComparison.Ordinal);
                var label = confirming
                    ? Simulation.Loc.Format("Click again to merge {0} into {1}", pair.Absorb.DisplayName, pair.Keep.DisplayName)
                    : pair.Keep.Id == captured.Id
                        ? Simulation.Loc.Format("Consolidate: absorb {0}", pair.Absorb.DisplayName)
                        : Simulation.Loc.Format("Consolidate into {0}", pair.Keep.DisplayName);
                var mergeBtn = ActionButton(label, () =>
                {
                    if (!SubjectBlocs.CanMerge(_campaign, player, pair.Keep, pair.Absorb, out _, out var mergeWhy))
                    {
                        _pendingSubjectMergeKey = "";
                        _subjectsModalFeedback.Text = mergeWhy;
                        RefreshSubjectsModal();
                        return;
                    }
                    if (!string.Equals(_pendingSubjectMergeKey, mergeKey, StringComparison.Ordinal))
                    {
                        _pendingSubjectMergeKey = mergeKey;
                        RefreshSubjectsModal();
                        _subjectsModalFeedback.Text = Simulation.Loc.Format(
                            "Confirm the merger: {0} will become part of {1}. Click again.", pair.Absorb.DisplayName, pair.Keep.DisplayName);
                        return;
                    }
                    _pendingSubjectMergeKey = "";
                    _campaign.Commands.Enqueue(new ConsolidateSubjectsCommand
                    {
                        IssuerId = player.Id,
                        KeepId = pair.Keep.Id,
                        AbsorbId = pair.Absorb.Id
                    });
                    _campaign.Commands.ApplyAll(_campaign);
                    _subjectsModalFeedback.Text = Simulation.Loc.Format("Merged {0} into {1}.",
                        pair.Absorb.DisplayName, pair.Keep.DisplayName);
                    RefreshSubjectsModal();
                    if (_diplomacyLayer is { Visible: true }) ForceDiplomacyRefresh();
                    Refresh();
                }, 0, 32, confirming);
                mergeBtn.Name = "ConsolidateSubject" + captured.Id;
                mergeBtn.TooltipText = Ui.Tip(Simulation.Loc.Format(
                    "One administration instead of two micro-clients. Cost: {0:0.#} political points. {1}",
                    pair.Price, pair.Keep.Id == captured.Id
                        ? Simulation.Loc.Format("{0} keeps its subject status; {1} is absorbed.", pair.Keep.DisplayName, pair.Absorb.DisplayName)
                        : Simulation.Loc.Format("{0} is absorbed into {1}, which remains your subject.", pair.Absorb.DisplayName, pair.Keep.DisplayName)));
                finalFlow.AddChild(mergeBtn);
            }

            AddSubjectActionButton(finalFlow, captured, SubjectInteractionKind.ToggleIntegration,
                captured.SubjectIntegrating ? "Pause integration" : "Begin integration",
                "Steadily integrate their administration week by week until they can be annexed outright.",
                primary: !captured.SubjectIntegrating);
            AddSubjectActionButton(finalFlow, captured, SubjectInteractionKind.CompleteAnnexation,
                "Annex / Integrate now",
                "Absorb this dependency and all its provinces into your country.",
                primary: true);
            AddSubjectActionButton(finalFlow, captured, SubjectInteractionKind.ReleaseFree,
                "Grant full independence",
                "End the dependency arrangement and let them stand as a sovereign country.");
        }

        var foreignSubjects = _campaign.Nations
            .Where(n => n.IsSubject && n.OverlordId != player.Id && _campaign.HasOwnedLand(n))
            .OrderByDescending(n => n.LibertyDesire)
            .ThenByDescending(n => n.Population)
            .Take(8)
            .ToList();
        if (foreignSubjects.Count > 0)
        {
            _subjectsModalBody.AddChild(Title(Tr("FOREIGN SUBJECTS & INDEPENDENCE AGITATION"), 13, Ui.Colour("text.accent")));
            foreach (var foreignSub in foreignSubjects)
            {
                var capturedSub = foreignSub;
                var overlord = _campaign.NationById(foreignSub.OverlordId);
                var rowCard = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                rowCard.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 8));
                _subjectsModalBody.AddChild(rowCard);

                var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 10);
                rowCard.AddChild(row);
                row.AddChild(FlagBadge(foreignSub.Tag, 28));
                var info = Title(
                    $"{foreignSub.DisplayName} ({foreignSub.Tag}) · {Tr(SubjectCatalog.Of(foreignSub.SubjectType)?.Name ?? foreignSub.SubjectType)} → {overlord?.DisplayName ?? "?"} · {Tr("Liberty desire")}: {foreignSub.LibertyDesire:P0}",
                    13, Ui.Colour("text.bright"));
                info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(info);

                AddSubjectActionButton(row, capturedSub, SubjectInteractionKind.AgitateIndependence,
                    Simulation.Loc.Format("Sponsor independence ({0})", Fmt.Money(SubjectInteractions.AgitationCost(_campaign, player, capturedSub))),
                    "Covertly fund separatists and independence networks in this foreign dependency (+24% liberty desire).");
            }
        }
    }

    private void AddSubjectActionButton(Container parent, Nation subject, SubjectInteractionKind kind, string label, string help, bool primary = false)
    {
        var can = SubjectInteractions.CanInteract(_campaign, _campaign.Player, subject, kind, out var refusal);
        var btn = ActionButton(Tr(label), () =>
        {
            if (SubjectInteractions.CanInteract(_campaign, _campaign.Player, subject, kind, out var why))
            {
                _campaign.Commands.Enqueue(new SubjectInteractionCommand
                {
                    IssuerId = _campaign.Player.Id,
                    SubjectId = subject.Id,
                    Kind = kind
                });
                _campaign.Commands.ApplyAll(_campaign);
                _subjectsModalFeedback.Text = Simulation.Loc.Format("Order issued to {0}.", subject.DisplayName);
                RefreshSubjectsModal();
                if (_diplomacyLayer is { Visible: true }) ForceDiplomacyRefresh();
                Refresh();
            }
            else _subjectsModalFeedback.Text = why;
        }, 0, 32, primary);
        btn.Disabled = !can;
        btn.TooltipText = Ui.Tip(Tr(help) + (can || refusal.Length == 0 ? "" : "\n\n" + refusal));
        parent.AddChild(btn);
    }

    internal void OpenReleaseCountryModal(ScenarioState preselectedState = null, Nation grantTo = null)
    {
        if (_campaign?.Player is null) return;
        EnsureSubjectsAndReleaseModals();
        var player = _campaign.Player;
        _releaseSelectedStates.Clear();
        _releaseToSubjectId = grantTo?.Id ?? -1;
        if (preselectedState is not null && preselectedState.NationMapIndex == player.MapIndex &&
            preselectedState.MapIndex != player.CapitalStateMapIndex)
            _releaseSelectedStates.Add(preselectedState.MapIndex);
        else
        {
            var first = CountryRelease.ReleasableStates(_campaign, player).FirstOrDefault();
            if (first is not null) _releaseSelectedStates.Add(first.MapIndex);
        }
        _releaseAsSubject = true;
        _releaseSubjectType = "puppet";
        _releaseCulture = "";
        _releaseReligion = "";
        _releaseGovernment = player.Government;
        _releaseIdeology = player.Ideology;
        _releaseCustomName = "";
        _releaseModalFeedback.Text = "";
        RaiseWorkspaceLayer(_releaseModalLayer);
        RefreshReleaseCountryModal();
    }

    internal void CloseReleaseCountryModal()
    {
        if (_releaseModalLayer is not null) _releaseModalLayer.Visible = false;
    }

    private void RefreshReleaseCountryModal()
    {
        if (_releaseModalLayer is null || !_releaseModalLayer.Visible || _campaign?.Player is null) return;
        ClearRuntimeChildren(_releaseModalBody);
        var player = _campaign.Player;
        var releasable = CountryRelease.ReleasableStates(_campaign, player);
        var grantRecipient = _campaign.NationById(_releaseToSubjectId);
        _releaseModalFeedback.Text = "";
        if (_releaseModalTitle is not null)
            _releaseModalTitle.Text = Tr(grantRecipient is null ? "RELEASE A NEW COUNTRY" : "TRANSFER REGIONAL ADMINISTRATION");

        if (releasable.Count == 0)
        {
            _releaseModalBody.AddChild(Title(Tr("Your country has no non-capital regions available to release."), 14, Ui.Colour("text.body_soft")));
            return;
        }

        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 16);
        _releaseModalBody.AddChild(columns);

        // Left column: Region multi-selector
        var leftCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(340, 0) };
        leftCol.AddThemeConstantOverride("separation", 6);
        columns.AddChild(leftCol);
        leftCol.AddChild(Title(Tr("1. SELECT REGIONS TO HAND OVER"), 12, Ui.Colour("text.accent")));
        leftCol.AddChild(ActionButton(Tr("Select coherent regional package"), () =>
        {
            var seed = releasable.FirstOrDefault(s => _releaseSelectedStates.Contains(s.MapIndex)) ?? releasable.FirstOrDefault();
            var package = RegionalAdministration.Package(_campaign, player, seed, grantRecipient);
            if (package.Length > 0)
            {
                _releaseSelectedStates.Clear(); _releaseSelectedStates.UnionWith(package);
                if (_releaseAsSubject && package.Length >= 4 && SubjectCatalog.Knows("special_administrative_region"))
                    _releaseSubjectType = "special_administrative_region";
            }
            RefreshReleaseCountryModal();
        }, 0, 30));

        var regScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 320),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        leftCol.AddChild(regScroll);
        var regList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        regList.AddThemeConstantOverride("separation", 4);
        regScroll.AddChild(regList);

        foreach (var state in releasable)
        {
            var captured = state;
            var picked = _releaseSelectedStates.Contains(state.MapIndex);
            var cultures = EconomySystem.Cultures(state);
            var topCul = cultures.Count > 0 ? DemographyCatalog.CultureName(cultures[0].Culture) : "—";
            var caption = $"{(picked ? "☑ " : "☐ ")}{state.DisplayName} · {Fmt.People(state.Population)} · {topCul} ({Fmt.Percent( state.Radicalism )} unrest)";
            var btn = ActionButton(caption, () =>
            {
                if (!_releaseSelectedStates.Remove(captured.MapIndex))
                    _releaseSelectedStates.Add(captured.MapIndex);
                RefreshReleaseCountryModal();
            }, 0, 32, picked);
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            btn.Name = "ReleaseRegion" + state.MapIndex;
            regList.AddChild(btn);
        }

        // Right column: Fine-tuning options
        var rightCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(380, 0) };
        rightCol.AddThemeConstantOverride("separation", 8);
        columns.AddChild(rightCol);

        if (grantRecipient is not null)
        {
            rightCol.AddChild(Title(Loc.Format("Enlarge administration: {0}", grantRecipient.DisplayName), 16, Ui.Colour("text.accent")));
            var selected = releasable.Where(s => _releaseSelectedStates.Contains(s.MapIndex)).ToArray();
            var summary = Title(Loc.Format("Selected package: {0} region(s), {1} people", selected.Length,
                Fmt.People(selected.Sum(s => s.Population))) + "\n" + string.Join(", ", selected.Select(s => s.DisplayName)),
                13, Ui.Colour("text.body_soft"));
            summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            rightCol.AddChild(summary);
            rightCol.AddChild(Title(Tr("Select several connected regions. Their residents and productive capacity move; armies and bank accounts do not."), 12, Ui.Colour("text.body_soft")));
            var mayGrant = RegionalAdministration.MayGrant(_campaign, player, grantRecipient, _releaseSelectedStates,
                out var price, out var why);
            rightCol.AddChild(Title(Loc.Format("Administrative cost: {0:0.#} political points", price), 13, Ui.Colour("text.body_soft")));
            var grantButton = ActionButton(Tr("TRANSFER SELECTED REGIONS"), () =>
            {
                _campaign.Commands.Enqueue(new GrantSubjectRegionsCommand { IssuerId = player.Id,
                    SubjectId = grantRecipient.Id, StateMapIndices = _releaseSelectedStates.OrderBy(id => id).ToArray() });
                _campaign.Commands.ApplyAll(_campaign);
                CloseReleaseCountryModal(); OpenSubjectsModal(grantRecipient); Refresh();
            }, 0, 40, true);
            grantButton.Name = "ConfirmSubjectLandGrant";
            grantButton.Disabled = !mayGrant;
            grantButton.TooltipText = Ui.Tip(mayGrant ? Loc.Format("Administrative cost: {0:0.#} political points", price) : why);
            rightCol.AddChild(grantButton);
            if (!mayGrant) _releaseModalFeedback.Text = why;
            return;
        }

        rightCol.AddChild(Title(Tr("2. SOVEREIGNTY & DEPENDENCY STATUS"), 12, Ui.Colour("text.accent")));
        var statusRow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        statusRow.AddThemeConstantOverride("h_separation", 6);
        statusRow.AddThemeConstantOverride("v_separation", 6);
        rightCol.AddChild(statusRow);

        var freeBtn = ActionButton(Tr("Independent state"), () => { _releaseAsSubject = false; RefreshReleaseCountryModal(); }, 0, 32, !_releaseAsSubject);
        statusRow.AddChild(freeBtn);
        foreach (var kind in SubjectCatalog.All)
        {
            var kId = kind.Id;
            var active = _releaseAsSubject && string.Equals(_releaseSubjectType, kId, StringComparison.OrdinalIgnoreCase);
            var kBtn = ActionButton(Tr(kind.Name), () =>
            {
                _releaseAsSubject = true;
                _releaseSubjectType = kId;
                RefreshReleaseCountryModal();
            }, 0, 32, active);
            kBtn.TooltipText = Ui.Tip(Tr(kind.Description));
            statusRow.AddChild(kBtn);
        }

        // Compute dominant culture & faith of selected regions
        var selectedStates = releasable.Where(s => _releaseSelectedStates.Contains(s.MapIndex)).ToList();
        var cultureTallies = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var faithTallies = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long totalPop = 0;
        foreach (var st in selectedStates)
        {
            totalPop += st.Population;
            foreach (var g in st.PopulationGroups)
            {
                cultureTallies[g.Culture] = cultureTallies.GetValueOrDefault(g.Culture) + g.Size;
                faithTallies[g.Religion] = faithTallies.GetValueOrDefault(g.Religion) + g.Size;
            }
        }
        var topCultures = cultureTallies.OrderByDescending(p => p.Value).Select(p => p.Key).Take(6).ToList();
        if (!topCultures.Contains(DemographyCatalog.PrimaryCulture(player.Tag)))
            topCultures.Add(DemographyCatalog.PrimaryCulture(player.Tag));
        if (string.IsNullOrEmpty(_releaseCulture) && topCultures.Count > 0)
            _releaseCulture = topCultures[0];

        var topFaiths = faithTallies.OrderByDescending(p => p.Value).Select(p => p.Key).Take(5).ToList();
        if (!topFaiths.Contains(StateIdentity.SecularChoice)) topFaiths.Add(StateIdentity.SecularChoice);
        if (string.IsNullOrEmpty(_releaseReligion) && topFaiths.Count > 0)
            _releaseReligion = topFaiths[0];

        rightCol.AddChild(Title(Tr("3. STATE CULTURE & RELIGION"), 12, Ui.Colour("text.accent")));
        var culFlow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        culFlow.AddThemeConstantOverride("h_separation", 6);
        culFlow.AddThemeConstantOverride("v_separation", 4);
        rightCol.AddChild(culFlow);
        foreach (var cId in topCultures.Where(c => !string.IsNullOrEmpty(c)))
        {
            var captured = cId;
            var btn = ActionButton(DemographyCatalog.CultureName(captured), () => { _releaseCulture = captured; RefreshReleaseCountryModal(); },
                0, 28, string.Equals(_releaseCulture, captured, StringComparison.OrdinalIgnoreCase));
            culFlow.AddChild(btn);
        }

        var relFlow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        relFlow.AddThemeConstantOverride("h_separation", 6);
        relFlow.AddThemeConstantOverride("v_separation", 4);
        rightCol.AddChild(relFlow);
        foreach (var rId in topFaiths.Where(r => !string.IsNullOrEmpty(r)))
        {
            var captured = rId;
            var btn = ActionButton(StateIdentity.Name(captured, true), () => { _releaseReligion = captured; RefreshReleaseCountryModal(); },
                0, 28, string.Equals(_releaseReligion, captured, StringComparison.OrdinalIgnoreCase));
            relFlow.AddChild(btn);
        }

        rightCol.AddChild(Title(Tr("4. GOVERNMENT, IDEOLOGY & CUSTOM NAME"), 12, Ui.Colour("text.accent")));
        var polRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        polRow.AddThemeConstantOverride("separation", 8);
        rightCol.AddChild(polRow);

        var govPicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var gIdx = 0;
        foreach (var g in GovernmentCatalog.All)
        {
            govPicker.AddItem(Tr(g.Name));
            govPicker.SetItemMetadata(gIdx, g.Id);
            if (string.Equals(g.Id, _releaseGovernment, StringComparison.OrdinalIgnoreCase)) govPicker.Select(gIdx);
            gIdx++;
        }
        govPicker.ItemSelected += idx => { _releaseGovernment = govPicker.GetItemMetadata((int)idx).AsString(); RefreshReleaseCountryModal(); };
        polRow.AddChild(govPicker);

        var ideoPicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var iIdx = 0;
        foreach (var id in IdeologyCatalog.All)
        {
            ideoPicker.AddItem(Tr(id.Name));
            ideoPicker.SetItemMetadata(iIdx, id.Id);
            if (string.Equals(id.Id, _releaseIdeology, StringComparison.OrdinalIgnoreCase)) ideoPicker.Select(iIdx);
            iIdx++;
        }
        ideoPicker.ItemSelected += idx => { _releaseIdeology = ideoPicker.GetItemMetadata((int)idx).AsString(); RefreshReleaseCountryModal(); };
        polRow.AddChild(ideoPicker);

        var nameEdit = Placeholder(new LineEdit
        {
            Text = _releaseCustomName,
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        }, "Optional custom country name (leave blank for automatic national name)...");
        nameEdit.TextChanged += txt => _releaseCustomName = txt;
        rightCol.AddChild(nameEdit);

        var seat = selectedStates.OrderByDescending(s => s.Population).FirstOrDefault();
        var autoName = "—";
        Texture2D previewFlagTex = null;
        if (seat is not null)
        {
            var regionIndices = selectedStates.Select(s => s.MapIndex).ToArray();
            var (bKind, bId) = NationNaming.BaseFor(_campaign, player, regionIndices, _releaseCulture, "auto");
            var previewNation = new Nation
            {
                Tag = bKind is "tag" or "free_tag" && !string.IsNullOrWhiteSpace(bId) ? bId : "REL",
                Name = seat.Name,
                Color = DemographyCatalog.Culture(_releaseCulture)?.Colour ?? player.Color,
                Government = string.IsNullOrEmpty(_releaseGovernment) ? player.Government : _releaseGovernment,
                Ideology = string.IsNullOrEmpty(_releaseIdeology) ? player.Ideology : _releaseIdeology,
                DeclaredCulture = _releaseCulture ?? "",
                DeclaredReligion = _releaseReligion ?? "",
                FoundNameKind = bKind,
                FoundNameId = bId,
                FoundNameBase = SecessionEvents.StripAdministrativeSuffix(seat.Name),
                FoundNameForm = NationNaming.FormFor(
                    string.IsNullOrEmpty(_releaseGovernment) ? player.Government : _releaseGovernment,
                    string.IsNullOrEmpty(_releaseIdeology) ? player.Ideology : _releaseIdeology,
                    _releaseReligion ?? "")
            };
            autoName = NationNaming.Render(_campaign, previewNation, false);
            if (string.IsNullOrWhiteSpace(autoName))
                autoName = SecessionEvents.NameFor(_campaign, _releaseCulture, seat.Id, seat.Name, player);
            previewFlagTex = ProvisionalFlag(previewNation);
        }
        var previewRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        previewRow.AddThemeConstantOverride("separation", 8);
        if (previewFlagTex is not null)
        {
            var flagPreview = new TextureRect
            {
                Texture = previewFlagTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(36, 24)
            };
            previewRow.AddChild(flagPreview);
        }
        previewRow.AddChild(Title(Simulation.Loc.Format("Selected: {0} region(s) · {1} people · Default name: {2}",
            selectedStates.Count, Fmt.People(totalPop), Tr(autoName)), 12, Ui.Colour("text.body_soft")));
        rightCol.AddChild(previewRow);

        var opts = new CountryRelease.ReleaseOptions
        {
            StateMapIndices = _releaseSelectedStates.ToList(),
            AsSubject = _releaseAsSubject,
            SubjectType = _releaseSubjectType,
            Culture = _releaseCulture,
            Religion = _releaseReligion,
            Government = _releaseGovernment,
            Ideology = _releaseIdeology,
            CustomName = _releaseCustomName
        };
        var canRelease = CountryRelease.CanRelease(_campaign, player, opts, out var refusal);
        var confirmBtn = ActionButton(Tr("RELEASE COUNTRY"), () =>
        {
            _campaign.Commands.Enqueue(new ReleaseCountryCommand
            {
                IssuerId = player.Id,
                StateMapIndices = _releaseSelectedStates.ToArray(),
                AsSubject = _releaseAsSubject,
                SubjectType = _releaseSubjectType,
                Culture = _releaseCulture,
                Religion = _releaseReligion,
                Government = _releaseGovernment,
                Ideology = _releaseIdeology,
                CustomName = _releaseCustomName
            });
            _campaign.Commands.ApplyAll(_campaign);
            PlayStrategicCue("save_complete");
            CloseReleaseCountryModal();
            Refresh();
        }, 0, 40, true);
        confirmBtn.Disabled = !canRelease;
        if (!canRelease) _releaseModalFeedback.Text = refusal;
        rightCol.AddChild(confirmBtn);
    }

    // ------------------------------------------------------------------ State Affairs: Rulers, Cabinet & Cosmetics
    private void BuildRulersAndCabinetSection(VBoxContainer rows)
    {
        StateSection(rows, "HEAD OF STATE, CABINET & NATIONAL COSMETICS",
            "Who heads the state and government, the ministers holding the four key portfolios, " +
            "and your country's official name, flag and subjects.");

        var topButtons = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        topButtons.AddThemeConstantOverride("h_separation", 8);
        topButtons.AddThemeConstantOverride("v_separation", 6);
        rows.AddChild(topButtons);
        topButtons.AddChild(ActionButton("Manage subjects & satellites...", () => OpenSubjectsModal(), 230, 34, true));
        topButtons.AddChild(ActionButton("Release a new country...", () => OpenReleaseCountryModal(), 210, 34));

        _rulersCabinetHost = new VBoxContainer { Name = "RulersCabinetHost", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rulersCabinetHost.AddThemeConstantOverride("separation", 8);
        rows.AddChild(_rulersCabinetHost);

        _rulersCabinetFeedback = RowLabel("", 11, Ui.Colour("text.accent_dim"));
        _rulersCabinetFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_rulersCabinetFeedback);
    }

    private void RefreshRulersAndCabinetSection(Nation nation)
    {
        if (_rulersCabinetHost is null || nation is null) return;
        ClearRuntimeChildren(_rulersCabinetHost);

        // 1. Head of State & Head of Government cards
        var leadersRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        leadersRow.AddThemeConstantOverride("separation", 12);
        _rulersCabinetHost.AddChild(leadersRow);

        var hos = Rulers.HeadOfState(_campaign, nation);
        leadersRow.AddChild(BuildExecutiveCard(nation, hos));

        if (Rulers.HasDualExecutive(nation))
        {
            var hog = Rulers.HeadOfGovernment(_campaign, nation);
            leadersRow.AddChild(BuildExecutiveCard(nation, hog));
        }

        // 2. Cabinet Ministers
        BuildElectionSection(_rulersCabinetHost, nation);
        _rulersCabinetHost.AddChild(RowLabel("CABINET MINISTERS", 11, Ui.Colour("text.caption")));
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 8);
        _rulersCabinetHost.AddChild(grid);

        foreach (var portfolio in Rulers.AllPortfolios)
        {
            var pCard = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            pCard.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 10));
            grid.AddChild(pCard);

            var pBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            pBox.AddThemeConstantOverride("separation", 4);
            pCard.AddChild(pBox);

            var active = Rulers.ActiveMinister(nation, portfolio.Id);
            var personName = Rulers.MinisterPersonName(nation, portfolio.Id);
            var minAge = Rulers.MinisterPersonAge(_campaign, nation, portfolio.Id);
            pBox.AddChild(Title(Tr(portfolio.Name).ToUpperInvariant(), 10, Ui.Colour("text.caption")));
            pBox.AddChild(Title(active is null
                ? (!string.IsNullOrEmpty(personName)
                    ? $"{personName} ({Simulation.Loc.Format("{0} YRS", minAge)}) · {Tr("Vacant portfolio")}"
                    : Tr("Vacant portfolio"))
                : $"{personName} ({Simulation.Loc.Format("{0} YRS", minAge)}) · {Tr(active.Name)}",
                13, Ui.Colour(active is null ? "text.muted" : "text.bright")));

            if (active is not null)
            {
                var mods = string.Join(" · ", active.Modifiers.Select(kv =>
                    $"{Tr(NationalModifiers.Of(kv.Key)?.Name ?? kv.Key)} {kv.Value:+0.#%;-0.#%;0%}"));
                pBox.AddChild(Title(mods, 11, Ui.Colour("state.positive")));
            }

            var btnFlow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            btnFlow.AddThemeConstantOverride("h_separation", 4);
            btnFlow.AddThemeConstantOverride("v_separation", 4);
            pBox.AddChild(btnFlow);

            foreach (var opt in portfolio.Options)
            {
                var pId = portfolio.Id;
                var oId = opt.Id;
                var isCurrent = active is not null && string.Equals(active.Id, oId, StringComparison.OrdinalIgnoreCase);
                var can = Rulers.CanAppointMinister(_campaign, nation, pId, oId, out var refusal);
                var btn = ActionButton(Tr(opt.Name), () =>
                {
                    if (!Rulers.CanAppointMinister(_campaign, nation, pId, oId, out var why))
                    {
                        _rulersCabinetFeedback.Text = why;
                        return;
                    }
                    _campaign.Commands.Enqueue(new AppointMinisterCommand
                    {
                        IssuerId = nation.Id,
                        PortfolioId = pId,
                        OptionId = oId
                    });
                    _campaign.Commands.ApplyAll(_campaign);
                    _rulersCabinetFeedback.Text = Simulation.Loc.Format("Appointed {0} as {1}.", Tr(opt.Name), Tr(portfolio.Name));
                    RefreshStateAffairs();
                    Refresh();
                }, 0, 28, isCurrent);
                btn.Disabled = isCurrent || !can;
                var modText = string.Join("\n", opt.Modifiers.Select(kv =>
                    $"{Tr(NationalModifiers.Of(kv.Key)?.Name ?? kv.Key)}: {kv.Value:+0.#%;-0.#%;0%}"));
                btn.TooltipText = Ui.Tip(Tr(opt.Description) + "\n" + modText + (refusal.Length > 0 ? "\n\n" + refusal : ""));
                btnFlow.AddChild(btn);
            }

            if (active is not null)
            {
                var pId = portfolio.Id;
                var mayPromote = PoliticalAppointments.MayPromoteMinister(_campaign, nation, pId, out var promoteCost, out var promoteRefusal);
                var promoteBtn = ActionButton(Tr("Back as national leader"), () =>
                {
                    _campaign.Commands.Enqueue(new AppointMinisterCommand
                    {
                        IssuerId = nation.Id,
                        PortfolioId = pId,
                        PromoteToLeader = true
                    });
                    _campaign.Commands.ApplyAll(_campaign);
                    _rulersCabinetFeedback.Text = Tr("Appointment order submitted.");
                    RefreshStateAffairs();
                    Refresh();
                }, 0, 26);
                promoteBtn.Disabled = !mayPromote;
                promoteBtn.TooltipText = Ui.Tip(mayPromote ? Simulation.Loc.Format("Appointment cost: {0}", Fmt.Money(promoteCost)) : promoteRefusal);
                pBox.AddChild(promoteBtn);
            }
        }

        // 2b. Dynamically Generated Politician Candidates Pool
        var candHeader = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        candHeader.AddThemeConstantOverride("separation", 8);
        _rulersCabinetHost.AddChild(candHeader);
        var candTitle = RowLabel("RISING POLITICIANS & CANDIDATE POOL", 11, Ui.Colour("text.caption"));
        candTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        candHeader.AddChild(candTitle);
        var refreshPoolBtn = ActionButton(Tr("Scout new candidates"), () =>
        {
            _campaign.Commands.Enqueue(new RecruitPoliticianCommand
            {
                IssuerId = nation.Id,
                RefreshPool = true
            });
            _campaign.Commands.ApplyAll(_campaign);
            _rulersCabinetFeedback.Text = Tr("Scouted a new slate of rising politicians.");
            RefreshStateAffairs();
            Refresh();
        }, 168, 26);
        refreshPoolBtn.TooltipText = Ui.Tip(Tr("Generate a fresh set of politician candidates from your country's cultural name pool."));
        candHeader.AddChild(refreshPoolBtn);

        var candGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        candGrid.AddThemeConstantOverride("h_separation", 10);
        candGrid.AddThemeConstantOverride("v_separation", 8);
        _rulersCabinetHost.AddChild(candGrid);

        foreach (var cand in Rulers.CandidatePool(_campaign, nation, 4))
        {
            var captured = cand;
            var cCard = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            cCard.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 8));
            candGrid.AddChild(cCard);

            var cRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            cRow.AddThemeConstantOverride("separation", 8);
            cCard.AddChild(cRow);

            var previewProfile = new LeaderProfile(
                captured.Name, captured.PortfolioName, captured.Age, captured.BirthYear,
                captured.TraitId, captured.TraitName, captured.PortraitSeed, false);
            cRow.AddChild(BuildLeaderPortraitBadge(previewProfile, nation.Color, 40, 48));

            var cCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            cCol.AddThemeConstantOverride("separation", 2);
            cRow.AddChild(cCol);

            cCol.AddChild(Title($"{captured.Name} ({Simulation.Loc.Format("{0} YRS", captured.Age)})", 13, Ui.Colour("text.bright")));
            cCol.AddChild(Title($"{Tr(captured.TraitName)} · {Tr(captured.PortfolioName)}: {Tr(captured.OptionName)}", 11, Ui.Colour("text.accent_dim")));

            var cBtns = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            cBtns.AddThemeConstantOverride("h_separation", 4);
            cBtns.AddThemeConstantOverride("v_separation", 4);
            cCol.AddChild(cBtns);

            var canRecruit = PoliticalAppointments.MayRecruit(_campaign, nation, captured.SlotIndex, false,
                out _, out var recruitPrice, out var cWhy);
            var canInstall = PoliticalAppointments.MayRecruit(_campaign, nation, captured.SlotIndex, true,
                out _, out var leaderPrice, out var leadWhy);
            var appointCandBtn = ActionButton(Tr("Appoint to cabinet"), () =>
            {
                _campaign.Commands.Enqueue(new RecruitPoliticianCommand
                {
                    IssuerId = nation.Id,
                    CandidateSlot = captured.SlotIndex,
                    ExpectedName = captured.Name,
                    AsNationalLeader = false
                });
                _campaign.Commands.ApplyAll(_campaign);
                _rulersCabinetFeedback.Text = Tr("Appointment order submitted.");
                RefreshStateAffairs();
                Refresh();
            }, 0, 25, true);
            appointCandBtn.Disabled = !canRecruit;
            appointCandBtn.Name = "AppointPolitician" + captured.SlotIndex;
            appointCandBtn.TooltipText = Ui.Tip(canRecruit ? Loc.Format("Appointment cost: {0}", Fmt.Money(recruitPrice)) : cWhy);
            cBtns.AddChild(appointCandBtn);

            var leadCandBtn = ActionButton(Tr("Install as leader"), () =>
            {
                _campaign.Commands.Enqueue(new RecruitPoliticianCommand
                {
                    IssuerId = nation.Id,
                    CandidateSlot = captured.SlotIndex,
                    ExpectedName = captured.Name,
                    AsNationalLeader = true
                });
                _campaign.Commands.ApplyAll(_campaign);
                _rulersCabinetFeedback.Text = Tr("Appointment order submitted.");
                RefreshStateAffairs();
                Refresh();
            }, 0, 25);
            leadCandBtn.Disabled = !canInstall;
            leadCandBtn.Name = "InstallPolitician" + captured.SlotIndex;
            leadCandBtn.TooltipText = Ui.Tip(canInstall ?
                Loc.Format("Appointment cost: {0}", Fmt.Money(leaderPrice)) + "\n" +
                Tr("Elevate this politician directly to Head of Government / State.") : leadWhy);
            cBtns.AddChild(leadCandBtn);
        }

        // 3. Arbitrary Country Name & Flag Switcher
        _rulersCabinetHost.AddChild(RowLabel("NATIONAL NAME & FLAG OVERRIDE", 11, Ui.Colour("text.caption")));
        var cosPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cosPanel.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 10));
        _rulersCabinetHost.AddChild(cosPanel);

        var cosRow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cosRow.AddThemeConstantOverride("h_separation", 8);
        cosRow.AddThemeConstantOverride("v_separation", 6);
        cosPanel.AddChild(cosRow);

        _customCountryNameInput = Placeholder(new LineEdit
        {
            Text = nation.CustomName,
            CustomMinimumSize = new Vector2(240, 32),
            ClearButtonEnabled = true
        }, "Custom country name (or blank for default)...");
        cosRow.AddChild(_customCountryNameInput);

        _customCountryFlagPicker = new OptionButton { CustomMinimumSize = new Vector2(220, 32) };
        var flagChoices = AvailableFlagOverridesFor(nation);
        var fIdx = 0;
        foreach (var (sprite, label) in flagChoices)
        {
            _customCountryFlagPicker.AddItem(label);
            _customCountryFlagPicker.SetItemMetadata(fIdx, sprite);
            if (string.Equals(sprite, nation.CustomFlag ?? "", StringComparison.OrdinalIgnoreCase))
                _customCountryFlagPicker.Select(fIdx);
            fIdx++;
        }
        cosRow.AddChild(_customCountryFlagPicker);

        var applyCosBtn = ActionButton(Tr("Apply name & flag"), () =>
        {
            var chosenFlag = _customCountryFlagPicker.Selected >= 0
                ? _customCountryFlagPicker.GetItemMetadata(_customCountryFlagPicker.Selected).AsString()
                : "";
            _campaign.Commands.Enqueue(new SetNationalCosmeticsCommand
            {
                IssuerId = nation.Id,
                TargetNationId = nation.Id,
                CustomName = _customCountryNameInput.Text,
                CustomFlag = chosenFlag
            });
            _campaign.Commands.ApplyAll(_campaign);
            _rulersCabinetFeedback.Text = Tr("Updated national name and banner.");
            RefreshStateAffairs();
            Refresh();
        }, 160, 32, true);
        cosRow.AddChild(applyCosBtn);

        var resetCosBtn = ActionButton(Tr("Reset to default"), () =>
        {
            _campaign.Commands.Enqueue(new SetNationalCosmeticsCommand
            {
                IssuerId = nation.Id,
                TargetNationId = nation.Id,
                CustomName = "",
                CustomFlag = ""
            });
            _campaign.Commands.ApplyAll(_campaign);
            _rulersCabinetFeedback.Text = Tr("Restored automatic historical name and flag.");
            RefreshStateAffairs();
            Refresh();
        }, 150, 32);
        cosRow.AddChild(resetCosBtn);
    }

    private Control BuildExecutiveCard(Nation nation, LeaderProfile leader)
    {
        var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", WarInsetStyle(14, 10));
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 10);
        card.AddChild(row);

        row.AddChild(BuildLeaderPortraitBadge(leader, nation.Color, 56, 68));
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 2);
        row.AddChild(col);
        col.AddChild(Title($"{Tr(leader.Title).ToUpperInvariant()} · {Simulation.Loc.Format("{0} YRS", leader.Age)}", 10, Ui.Colour("text.caption")));
        col.AddChild(Title(leader.Name, 16, Ui.Colour("text.bright")));
        var trait = Rulers.TraitOf(leader.TraitId);
        col.AddChild(Title(Tr(leader.TraitName), 12, Ui.Colour("text.accent")));
        if (trait is not null && trait.Modifiers.Count > 0)
        {
            var mods = string.Join(" · ", trait.Modifiers.Select(kv =>
                $"{Tr(NationalModifiers.Of(kv.Key)?.Name ?? kv.Key)} {kv.Value:+0.#%;-0.#%;0%}"));
            col.AddChild(Title(mods, 11, Ui.Colour("state.positive")));
        }
        return card;
    }

    private List<(string Sprite, string Label)> AvailableFlagOverridesFor(Nation nation)
    {
        var list = new List<(string, string)> { ("", Tr("Automatic (by regime & identity)")) };
        if (nation is null) return list;
        var tag = nation.Tag.ToLowerInvariant();
        void Offer(string sprite, string label)
        {
            if (list.Any(x => string.Equals(x.Item1, sprite, StringComparison.OrdinalIgnoreCase))) return;
            var path = SpriteCatalog.TextureOf(sprite, $"res://game/gfx/flags/{sprite}.png");
            if (SpriteCatalog.Has(sprite) || ResourceLoader.Exists(path))
                list.Add((sprite, label));
        }

        Offer($"flag_{tag}", $"{nation.Tag} · {Tr("Standard flag")}");
        foreach (var ideo in IdeologyCatalog.All)
            Offer($"flag_{tag}_{ideo.Id}", $"{nation.Tag} · {Tr(ideo.Name)}");
        foreach (var gov in GovernmentCatalog.All)
            Offer($"flag_{tag}_{gov.Id}", $"{nation.Tag} · {Tr(gov.Name)}");
        foreach (var ideo in IdeologyCatalog.All)
            Offer($"flag_ideology_{ideo.Id}", $"{Tr("Ideology banner")}: {Tr(ideo.Name)}");
        var primaryCul = DemographyCatalog.PrimaryCulture(nation.Tag);
        if (!string.IsNullOrEmpty(primaryCul))
            Offer($"flag_culture_{primaryCul}", $"{Tr("Cultural banner")}: {DemographyCatalog.CultureName(primaryCul)}");
        return list;
    }
}
