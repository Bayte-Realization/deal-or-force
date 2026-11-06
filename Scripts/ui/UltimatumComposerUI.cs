using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _ultimatumComposer;
    private readonly HashSet<int> _ultimatumRegions = new();
    private SpinBox _ultimatumCash;
    private CheckBox _ultimatumEmbargo, _ultimatumPact;
    // The rest of the terms. One checkbox each, because a demand is a package and a package
    // is assembled by hand: land, money, treaties - or the country itself.
    private readonly Dictionary<string, CheckBox> _ultimatumTerms = new();
    private readonly Dictionary<string, OptionButton> _ultimatumPicks = new();
    private Label _ultimatumAssessment, _ultimatumSummary;
    private Label _ultimatumStrengthFigure, _ultimatumShareFigure, _ultimatumReparationsFigure, _ultimatumRegionCount;
    private Button _ultimatumSend, _ultimatumClose;
    private int _ultimatumTarget = -1;
    private bool _ultimatumConfirm, _ultimatumCompleted;

    private void CloseUltimatumComposer()
    {
        if (IsInstanceValid(_ultimatumComposer))
        { _ultimatumComposer.GetParent()?.RemoveChild(_ultimatumComposer); _ultimatumComposer.QueueFree(); }
        _ultimatumComposer = null;
        _ultimatumRegions.Clear();
        _ultimatumTerms.Clear();
        _ultimatumPicks.Clear();
        _ultimatumConfirm = false;
        _ultimatumCompleted = false;
        RestoreDecisionFocus(ref _ultimatumReturnFocus);
    }

    private Ultimatum.Demand ComposedUltimatum() => new()
    {
        Regions = _ultimatumRegions.OrderBy(id => id).ToArray(),
        Reparations = (float)_ultimatumCash.Value,
        LiftEmbargo = _ultimatumEmbargo.ButtonPressed,
        NonAggression = _ultimatumPact.ButtonPressed,
        Annex = TermPressed("annex"),
        Vassalise = TermPressed("vassalise"),
        Disarm = TermPressed("disarm"),
        BreakAlliances = TermPressed("break_alliances"),
        ReleaseSubjects = TermPressed("release_subjects"),
        TechnologyTransfer = TermPressed("technology"),
        ChangeGovernment = TermPressed("government"),
        FleetTransfer = TermPressed("fleet"),
        SubjectKind = TermPressed("vassalise") ? PickText("subject_kind") : "",
        TradeAgreement = TermPressed("trade"),
        EmbargoThird = TermPressed("embargo_third"),
        EmbargoOnId = Pick("embargo_third"),
        BreakNamed = TermPressed("break_named"),
        BreakWithId = Pick("break_named"),
        CancelGuarantee = TermPressed("cancel_guarantee"),
        CancelGuaranteeId = Pick("cancel_guarantee"),
        JoinAlliance = TermPressed("join_alliance"),
        AcceptGuarantee = TermPressed("accept_guarantee"),
        MilitaryAccess = TermPressed("military_access"),
        WhitePeace = TermPressed("white_peace"),
        CedeOccupied = TermPressed("cede_occupied"),
        Humiliate = TermPressed("humiliate"),
        ReleaseNamed = TermPressed("release_named"),
        ReleaseSubjectId = Pick("release_named"),
        Liberate = TermPressed("liberate"),
        LiberateRegion = Pick("liberate")
    };

    private int Pick(string id) =>
        _ultimatumPicks.TryGetValue(id, out var box) && box.Selected >= 0 && box.ItemCount > 0
            ? box.GetItemMetadata(box.Selected).AsInt32() : 0;

    private string PickText(string id) =>
        _ultimatumPicks.TryGetValue(id, out var box) && box.Selected >= 0 && box.ItemCount > 0
            ? box.GetItemMetadata(box.Selected).AsString() : "";

    private bool TermPressed(string id) => _ultimatumTerms.TryGetValue(id, out var box) && box.ButtonPressed;

    /// <summary>One further term of the package: a checkbox, a sentence of help, and a redraw.</summary>
    private void AddUltimatumTerm(VBoxContainer terms, string id, string caption, string help, bool disabled = false)
    {
        var block = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        block.AddThemeConstantOverride("separation", 2);
        terms.AddChild(block);
        var box = new CheckBox
        {
            Name = "Ultimatum_" + id, Text = Tr(caption), Disabled = disabled,
            TooltipText = Ui.Tip(Tr(help)),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        box.Toggled += _ => RefreshUltimatumComposer();
        block.AddChild(box);
        if (disabled && !string.IsNullOrEmpty(help))
        {
            var why = WrappedUltimatumText(help, 12);
            why.Name = "UltimatumWhy_" + id;
            why.LabelSettings.FontColor = Ui.Colour("state.warning");
            block.AddChild(why);
        }
        _ultimatumTerms[id] = box;
    }

    private void OpenUltimatumComposer(Nation target)
    {
        if (_campaign?.Player is null || target is null || target.Id == _campaign.Player.Id) return;
        CloseUltimatumComposer();
        CloseDealComposer(); CloseWarDeclaration();
        _ultimatumReturnFocus = GetViewport().GuiGetFocusOwner();
        _ultimatumTarget = target.Id;
        _ultimatumComposer = FullLayer("UltimatumComposer");
        _hudLayer.AddChild(_ultimatumComposer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _ultimatumComposer.AddChild(shade);
        var panel = TexturedPanel(_ultimatumComposer);
        panel.Name = "UltimatumPanel";
        Ui.Place(panel, "modal.ultimatum");
        var rows = PanelRows(panel, 20, 16, 12);
        var ultHead = ScreenHeader(rows, Loc.Format("Ultimatum to {0}", target.DisplayName),
            "Compose the settlement you actually want: land, money, treaties - or the country itself. Every term makes the package harder to accept.",
            false, ActionButton("×", CloseUltimatumComposer, 38, 32));
        PrependToScreenHeader(ultHead, FlagBadge(target.Tag, 30, target.DisplayName));
        rows.AddChild(WrappedUltimatumText(Loc.Format("If accepted: {0} days of protected peace. If refused: {1} days before war can be declared.",
            Math.Max(1, _campaign.Defines.UltimatumSettlementTruceDays), Math.Max(1, _campaign.Defines.UltimatumRefusalTruceDays)), 13));
        var body = new HBoxContainer { Name = "UltimatumColumns", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 20);
        rows.AddChild(body);
        var land = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddChild(land);
        SectionRule(land, "Territorial demands");
        var search = new LineEdit { Name = "UltimatumRegionSearch", PlaceholderText = Tr("Search regions…"), ClearButtonEnabled = true };
        land.AddChild(search);
        _ultimatumRegionCount = Title("", 11, Ui.Colour("text.muted"));
        _ultimatumRegionCount.Name = "UltimatumRegionCount";
        land.AddChild(_ultimatumRegionCount);
        var regionScroll = new ScrollContainer { Name = "UltimatumRegionsScroll",
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        land.AddChild(regionScroll);
        var regionRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        regionRows.AddThemeConstantOverride("separation", 4);
        regionScroll.AddChild(regionRows);
        var offered = _campaign.States.Where(s => s.NationMapIndex == target.MapIndex).OrderBy(s => s.DisplayName).ToArray();
        foreach (var state in offered)
        {
            var id = state.MapIndex;
            var eligible = Ultimatum.CanDemand(_campaign, target, id);
            Ultimatum.ValidateTerms(_campaign, _campaign.Player, target, new Ultimatum.Demand { Regions = new[] { id } }, out var heldWhy);
            var choice = new CheckBox
            {
                Name = $"UltimatumRegion_{id}", Text = state.DisplayName, Disabled = !eligible,
                TooltipText = eligible ? Fmt.People(state.Population) : Ui.Tip(Tr(heldWhy.Length > 0 ? heldWhy : "They cannot yield what they do not hold.")),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            if (eligible && _selectedState?.MapIndex == id) { choice.ButtonPressed = true; _ultimatumRegions.Add(id); }
            choice.Toggled += selected =>
            {
                if (selected) _ultimatumRegions.Add(id); else _ultimatumRegions.Remove(id);
                RefreshUltimatumComposer();
            };
            regionRows.AddChild(choice);
        }
        if (offered.Length == 0) regionRows.AddChild(WrappedUltimatumText("No eligible regions. Financial or treaty demands remain available.", 13));
        void ShowRegionCount(string text)
        {
            var shown = 0;
            var selectable = 0;
            foreach (var choice in regionRows.GetChildren().OfType<CheckBox>())
            {
                var match = choice.Text.Contains(text, StringComparison.OrdinalIgnoreCase);
                choice.Visible = match;
                if (!match) continue;
                shown++;
                if (!choice.Disabled) selectable++;
            }
            _ultimatumRegionCount.Text = Loc.Format("{0} regions · {1} selectable", shown, selectable);
        }
        search.TextChanged += text => ShowRegionCount(text);
        ShowRegionCount("");
        var terms = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        terms.AddThemeConstantOverride("separation", 10);
        var termsColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddChild(termsColumn);
        SectionRule(termsColumn, "Additional demands");
        var termsScroll = new ScrollContainer { Name = "UltimatumTermsScroll",
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        termsColumn.AddChild(termsScroll); termsScroll.AddChild(terms);
        terms.AddChild(WrappedUltimatumText("One-time reparations · billions of dollars", 13));
        _ultimatumCash = new SpinBox { Name = "UltimatumReparations", MinValue = 0, Step = .01,
            MaxValue = Math.Max(0, target.Treasury) * Math.Clamp(_campaign.Defines.UltimatumMaxTreasuryShare, 0, 1),
            CustomMinimumSize = new(0, 36) };
        terms.AddChild(_ultimatumCash);
        terms.AddChild(Title(Loc.Format("Maximum: {0}", Fmt.Money((float)_ultimatumCash.MaxValue)), 12, Ui.Colour("text.muted")));
        var embargoed = CampaignDiplomacy.Peek(_campaign, _campaign.Player.Id, target.Id)?.EmbargoBy(target.Id) == true;
        _ultimatumEmbargo = new CheckBox
        {
            Name = "UltimatumLiftEmbargo", Text = Tr("Lift their embargo"), Disabled = !embargoed,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = Ui.Tip(Tr(embargoed ? "Lift their embargo" : "They have not imposed an embargo on us."))
        };
        terms.AddChild(_ultimatumEmbargo);
        if (!embargoed)
        {
            var embargoWhy = WrappedUltimatumText("They have not imposed an embargo on us.", 12);
            embargoWhy.LabelSettings.FontColor = Ui.Colour("state.warning");
            terms.AddChild(embargoWhy);
        }
        _ultimatumPact = new CheckBox
        {
            Name = "UltimatumNonAggression", Text = Tr("Non-aggression pact"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        terms.AddChild(_ultimatumPact);

        // Everything else a government can be made to sign. The three at the top are the
        // ordinary terms of a settlement; the rest are what an ultimatum is for.
        _ultimatumTerms.Clear();
        AddUltimatumTerm(terms, "vassalise", "The country as our subject",
            "It keeps its flag, its army and its government, and answers to us about all three.", target.IsSubject);
        AddUltimatumTerm(terms, "disarm", "Disarmament",
            "Its divisions are halved and its army is cut down for three years.");
        AddUltimatumTerm(terms, "break_alliances", "Break their alliances",
            "Every alliance it holds is torn up.", !_campaign.Relations.Values.Any(r =>
                r.Stance == DiplomaticStance.Allied && (r.A == target.Id || r.B == target.Id)));
        AddUltimatumTerm(terms, "release_subjects", "Release their subjects",
            "Every country answering to it is let go.", !_campaign.Nations.Any(other => other.AnswersTo(target.Id)));
        AddUltimatumTerm(terms, "technology", "Hand over their drawings",
            "Its current research is handed over: our laboratories work faster for two years.");
        AddUltimatumTerm(terms, "government", "A government of our colour",
            "Its government is replaced with one of our own kind, and it does not thank us for it.",
            string.Equals(target.Government, _campaign.Player.Government, StringComparison.OrdinalIgnoreCase));
        AddUltimatumTerm(terms, "fleet", "Half their fleet",
            "Half of its hulls are struck or surrendered to us.",
            !_campaign.Fleets.Any(fleet => fleet.NationId == target.Id));
        var annexWhy = TermRefusal(target, new Ultimatum.Demand { Annex = true });
        AddUltimatumTerm(terms, "annex", "The whole country",
            annexWhy.Length > 0 ? annexWhy : "Annexation: every province it owns becomes ours and it ceases to be a state. Nothing else can be asked for with it.",
            annexWhy.Length > 0);
        AddSubjectKind(terms);
        AddChoice(terms, target, "trade", "Trade agreement", "Write down the trade that already moves between you.",
            new Ultimatum.Demand { TradeAgreement = true });
        AddNationChoice(terms, target, "embargo_third", "They embargo another country", "Name another country for the embargo.",
            id => new Ultimatum.Demand { EmbargoThird = true, EmbargoOnId = id },
            _campaign.Nations.Where(n => n.Id != target.Id && n.Id != _campaign.Player.Id && _campaign.HasOwnedLand(n)));
        AddNationChoice(terms, target, "break_named", "Break one alliance", "They are not allied with that country.",
            id => new Ultimatum.Demand { BreakNamed = true, BreakWithId = id },
            AlliesOf(target));
        AddNationChoice(terms, target, "cancel_guarantee", "Cancel one guarantee", "They do not guarantee that country.",
            id => new Ultimatum.Demand { CancelGuarantee = true, CancelGuaranteeId = id },
            GuaranteesOf(target));
        AddChoice(terms, target, "join_alliance", "Join our alliance", "They become our ally.",
            new Ultimatum.Demand { JoinAlliance = true });
        AddChoice(terms, target, "accept_guarantee", "Accept our guarantee", "We promise to fight for them.",
            new Ultimatum.Demand { AcceptGuarantee = true });
        AddChoice(terms, target, "military_access", "Military access", "Their country is open to our armies.",
            new Ultimatum.Demand { MilitaryAccess = true });
        AddChoice(terms, target, "white_peace", "White peace", "End the war and restore the pre-war line.",
            new Ultimatum.Demand { WhitePeace = true });
        AddChoice(terms, target, "cede_occupied", "Keep the ground each side holds", "End the war on the line each side holds.",
            new Ultimatum.Demand { CedeOccupied = true });
        AddChoice(terms, target, "humiliate", "Public humiliation", "Their war support and standing take the blow a peace already knows.",
            new Ultimatum.Demand { Humiliate = true });
        AddNationChoice(terms, target, "release_named", "Release one subject", "They do not hold that subject.",
            id => new Ultimatum.Demand { ReleaseNamed = true, ReleaseSubjectId = id },
            _campaign.Nations.Where(n => n.AnswersTo(target.Id)));
        AddNationChoice(terms, target, "liberate", "Liberate one region", "Select at least one non-capital region to release.",
            id => new Ultimatum.Demand { Liberate = true, LiberateRegion = id },
            CountryRelease.ReleasableStates(_campaign, target).Select(s => new Nation { Id = s.MapIndex, Name = s.Name, CustomName = s.Name }));
        _ultimatumCash.ValueChanged += _ => RefreshUltimatumComposer();
        _ultimatumEmbargo.Toggled += _ => RefreshUltimatumComposer();
        _ultimatumPact.Toggled += _ => RefreshUltimatumComposer();
        var footer = new PanelContainer { Name = "UltimatumSettlementFooter" };
        footer.AddThemeStyleboxOverride("panel", WarInsetStyle(10, 10));
        AtlasSurfaceSkin.Attach(footer, AtlasSurfaceSkin.Level.Inset, "war.card");
        rows.AddChild(footer);
        var foot = new VBoxContainer();
        foot.AddThemeConstantOverride("separation", 8);
        footer.AddChild(foot);
        var figures = new HBoxContainer();
        figures.AddThemeConstantOverride("separation", 16);
        foot.AddChild(figures);
        var strength = NumberCell("Strength ratio", "—");
        var share = NumberCell("Population share", "—");
        var reparations = NumberCell("Reparations", "—");
        foreach (var cell in new[] { strength, share, reparations })
        { cell.SizeFlagsHorizontal = SizeFlags.ExpandFill; figures.AddChild(cell); }
        _ultimatumStrengthFigure = strength.GetNode<Label>("FigureValue");
        _ultimatumShareFigure = share.GetNode<Label>("FigureValue");
        _ultimatumReparationsFigure = reparations.GetNode<Label>("FigureValue");
        _ultimatumAssessment = WrappedUltimatumText("", 16);
        _ultimatumAssessment.Name = "UltimatumAssessment";
        foot.AddChild(_ultimatumAssessment);
        _ultimatumSummary = WrappedUltimatumText("", 13);
        _ultimatumSummary.Name = "UltimatumTerms";
        // A package can contain dozens of terms. It belongs in the scrolling document,
        // not in the pinned footer where it could push the only Send/Cancel buttons away.
        SectionRule(terms, "REVIEW BEFORE SIGNING");
        terms.AddChild(_ultimatumSummary);
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        foot.AddChild(actions);
        _ultimatumClose = ActionButton("Cancel", CloseUltimatumComposer, 0, 36);
        _ultimatumClose.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        actions.AddChild(_ultimatumClose);
        _ultimatumSend = ActionButton("Review demand", SendComposedUltimatum, 0, 36, true);
        _ultimatumSend.Name = "SendComposedUltimatum";
        _ultimatumSend.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        actions.AddChild(_ultimatumSend);
        ReadableDecision(panel, "modal.ultimatum");
        RefreshUltimatumComposer();
        search.GrabFocus();
    }

    private static Label WrappedUltimatumText(string text, int size) => new()
    {
        Text = Loc.Get(text), AutowrapMode = TextServer.AutowrapMode.WordSmart,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        LabelSettings = new LabelSettings { FontSize = size, FontColor = Ui.Colour("text.body") }
    };

    private void RefreshUltimatumComposer()
    {
        if (_ultimatumCompleted) return;
        _ultimatumConfirm = false;
        var target = _campaign.NationById(_ultimatumTarget);
        var demand = ComposedUltimatum();
        var assessment = Ultimatum.Assess(_campaign, _campaign.Player, target, demand);
        _ultimatumSend.Disabled = !assessment.Valid;
        _ultimatumSend.TooltipText = assessment.Valid ? "" : Tr(assessment.Reason);
        _ultimatumSend.Text = Tr("Review demand");
        _ultimatumAssessment.Text = !assessment.Valid ? Tr(assessment.Reason) : assessment.Response == Ultimatum.Answer.Yield
            ? Tr("They are expected to yield") : Tr("They are expected to refuse");
        _ultimatumAssessment.LabelSettings.FontColor = Ui.Colour(assessment.Valid ? "text.accent" : "state.warning");
        if (_ultimatumStrengthFigure is not null)
        {
            _ultimatumStrengthFigure.Text = assessment.Valid ? assessment.StrengthRatio.ToString("0.0") : "—";
            _ultimatumShareFigure.Text = assessment.Valid ? Fmt.Percent(assessment.TerritoryShare) : "—";
            _ultimatumReparationsFigure.Text = Fmt.Money(demand.Reparations);
        }
        _ultimatumSummary.Text = !assessment.Valid ? Tr("No terms have been sent. Change the package to resolve this restriction.") :
            Loc.Format("Regions: {0} · {1} of their population · strength ratio {2}:1\n{3}",
                demand.Regions.Length, Fmt.Percent(assessment.TerritoryShare), assessment.StrengthRatio.ToString("0.0"), Ultimatum.Summary(_campaign, demand));
    }

    private void SendComposedUltimatum()
    {
        if (_ultimatumCompleted) return;
        var demand = ComposedUltimatum();
        var target = _campaign.NationById(_ultimatumTarget);
        var assessment = Ultimatum.Assess(_campaign, _campaign.Player, target, demand);
        if (!assessment.Valid) { RefreshUltimatumComposer(); return; }
        if (!_ultimatumConfirm)
        { _ultimatumConfirm = true; _ultimatumSend.Text = Tr("CONFIRM AND SEND"); return; }
        var command = new IssueUltimatumCommand { IssuerId = _campaign.Player.Id, TargetId = target.Id,
            StateMapIndices = demand.Regions, Reparations = demand.Reparations, LiftEmbargo = demand.LiftEmbargo,
            NonAggression = demand.NonAggression, Annex = demand.Annex, Vassalise = demand.Vassalise,
            Disarm = demand.Disarm, BreakAlliances = demand.BreakAlliances,
            ReleaseSubjects = demand.ReleaseSubjects, TechnologyTransfer = demand.TechnologyTransfer,
            ChangeGovernment = demand.ChangeGovernment, FleetTransfer = demand.FleetTransfer,
            SubjectKind = demand.SubjectKind ?? "", TradeAgreement = demand.TradeAgreement,
            EmbargoThird = demand.EmbargoThird, EmbargoOnId = demand.EmbargoOnId,
            BreakNamed = demand.BreakNamed, BreakWithId = demand.BreakWithId,
            CancelGuarantee = demand.CancelGuarantee, CancelGuaranteeId = demand.CancelGuaranteeId,
            JoinAlliance = demand.JoinAlliance, AcceptGuarantee = demand.AcceptGuarantee,
            MilitaryAccess = demand.MilitaryAccess, WhitePeace = demand.WhitePeace,
            CedeOccupied = demand.CedeOccupied, Humiliate = demand.Humiliate,
            ReleaseNamed = demand.ReleaseNamed, ReleaseSubjectId = demand.ReleaseSubjectId,
            Liberate = demand.Liberate, LiberateRegion = demand.LiberateRegion };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _ultimatumCompleted = true;
        _ultimatumCash.Editable = false;
        foreach (var choice in _ultimatumComposer.FindChildren("*", "CheckBox", true, false).OfType<CheckBox>())
            choice.Disabled = true;
        _ultimatumSend.Disabled = true;
        _ultimatumSend.Text = Tr("Demand delivered");
        _ultimatumClose.Text = Tr("Close");
        _ultimatumAssessment.Text = !command.Result.Accepted ? Tr(command.Result.Note) :
            command.Response == Ultimatum.Answer.Yield ? Tr("Settlement accepted — protected peace begins") : Tr("Demand refused — no territory or money transferred");
        _ultimatumAssessment.LabelSettings.FontColor = Ui.Colour(command.Result.Accepted && command.Response == Ultimatum.Answer.Yield
            ? "state.positive" : "state.warning");
        var relation = CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, target.Id);
        _ultimatumSummary.Text = command.Result.Accepted ? Loc.Format("{0}\nTruce: {1} days remain.", command.Result.Note,
            Math.Max(0, relation.TruceUntil - _campaign.ElapsedDays)) : Tr("No terms have been sent.");
        ForceDiplomacyRefresh();
        RefreshCountryViews("Diplomacy");
        Refresh();
    }

    private string TermRefusal(Nation target, Ultimatum.Demand probe)
    {
        Ultimatum.ValidateTerms(_campaign, _campaign.Player, target, probe, out var why);
        return why ?? "";
    }

    private void AddChoice(VBoxContainer terms, Nation target, string id, string caption, string help, Ultimatum.Demand probe)
    {
        var why = TermRefusal(target, probe);
        AddUltimatumTerm(terms, id, caption, why.Length > 0 ? why : help, why.Length > 0);
    }

    private void AddSubjectKind(VBoxContainer terms)
    {
        var pick = new OptionButton
        {
            Name = "UltimatumPick_subject_kind", FitToLongestItem = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 32)
        };
        var index = 0;
        foreach (var kind in SubjectCatalog.Imposable.OrderBy(type => type.Id, StringComparer.Ordinal))
        {
            pick.AddItem(Tr(kind.Name));
            pick.SetItemMetadata(index++, kind.Id);
        }
        pick.ItemSelected += _ => RefreshUltimatumComposer();
        _ultimatumPicks["subject_kind"] = pick;
        if (pick.ItemCount > 0) terms.AddChild(pick);
    }

    private void AddNationChoice(VBoxContainer terms, Nation target, string id, string caption, string emptyReason,
        Func<int, Ultimatum.Demand> probe, IEnumerable<Nation> nations)
    {
        var choices = nations.Where(nation => nation is not null).GroupBy(nation => nation.Id).Select(group => group.First())
            .OrderBy(nation => nation.DisplayName, StringComparer.Ordinal).ToArray();
        var pick = new OptionButton
        {
            Name = "UltimatumPick_" + id, FitToLongestItem = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 32),
            Disabled = choices.Length == 0
        };
        for (var index = 0; index < choices.Length; index++)
        {
            pick.AddItem(choices[index].DisplayName);
            pick.SetItemMetadata(index, choices[index].Id);
        }
        pick.ItemSelected += _ => RefreshUltimatumComposer();
        _ultimatumPicks[id] = pick;
        var why = choices.Length == 0 ? emptyReason : TermRefusal(target, probe(choices[0].Id));
        AddUltimatumTerm(terms, id, caption, why.Length > 0 ? why : caption, why.Length > 0);
        if (choices.Length > 0) terms.AddChild(pick);
    }

    private IEnumerable<Nation> AlliesOf(Nation target) =>
        _campaign.Relations.Values.Where(relation => relation.Stance == DiplomaticStance.Allied &&
            (relation.A == target.Id || relation.B == target.Id))
            .Select(relation => _campaign.NationById(relation.Other(target.Id)));

    private IEnumerable<Nation> GuaranteesOf(Nation target) =>
        _campaign.Relations.Values.Where(relation => relation.GuaranteedBy(target.Id) &&
            (relation.A == target.Id || relation.B == target.Id))
            .Select(relation => _campaign.NationById(relation.Other(target.Id)));
}
