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
    private Label _ultimatumAssessment, _ultimatumTerms;
    private Button _ultimatumSend, _ultimatumClose;
    private int _ultimatumTarget = -1;
    private bool _ultimatumConfirm, _ultimatumCompleted;

    private void CloseUltimatumComposer()
    {
        if (IsInstanceValid(_ultimatumComposer))
        { _ultimatumComposer.GetParent()?.RemoveChild(_ultimatumComposer); _ultimatumComposer.QueueFree(); }
        _ultimatumComposer = null;
        _ultimatumRegions.Clear();
        _ultimatumConfirm = false;
        _ultimatumCompleted = false;
    }

    private Ultimatum.Demand ComposedUltimatum() => new()
    {
        Regions = _ultimatumRegions.OrderBy(id => id).ToArray(),
        Reparations = (float)_ultimatumCash.Value,
        LiftEmbargo = _ultimatumEmbargo.ButtonPressed,
        NonAggression = _ultimatumPact.ButtonPressed
    };

    private void OpenUltimatumComposer(Nation target)
    {
        if (_campaign?.Player is null || target is null || target.Id == _campaign.Player.Id) return;
        CloseUltimatumComposer();
        _ultimatumTarget = target.Id;
        _ultimatumComposer = FullLayer("UltimatumComposer");
        _hudLayer.AddChild(_ultimatumComposer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _ultimatumComposer.AddChild(shade);
        var panel = TexturedPanel(_ultimatumComposer);
        Ui.Place(panel, "modal.ultimatum");
        var rows = PanelRows(panel, 20, 16, 12);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        rows.AddChild(header);
        header.AddChild(FlagBadge(target.Tag, 30, target.DisplayName));
        var heading = Title(Loc.Format("Ultimatum to {0}", target.DisplayName), 24, Ui.Colour("text.accent"));
        heading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        header.AddChild(heading);
        header.AddChild(ActionButton("×", CloseUltimatumComposer, 38, 32));
        rows.AddChild(WrappedUltimatumText("Compose a settlement, not an annexation. Larger demands are harder to accept.", 13));
        rows.AddChild(WrappedUltimatumText(Loc.Format("If accepted: {0} days of protected peace. If refused: {1} days before war can be declared.",
            Math.Max(1, _campaign.Defines.UltimatumSettlementTruceDays), Math.Max(1, _campaign.Defines.UltimatumRefusalTruceDays)), 13));
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 20);
        rows.AddChild(body);
        var land = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddChild(land);
        land.AddChild(Title(Tr("Territorial demands"), 17, Ui.Colour("text.accent")));
        var search = new LineEdit { Name = "UltimatumRegionSearch", PlaceholderText = Tr("Search regions…"), ClearButtonEnabled = true };
        land.AddChild(search);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        land.AddChild(scroll);
        var regionRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        regionRows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(regionRows);
        var offered = _campaign.States.Where(s => s.NationMapIndex == target.MapIndex &&
            Ultimatum.CanDemand(_campaign, target, s.MapIndex)).OrderBy(s => s.Name).ToArray();
        foreach (var state in offered)
        {
            var id = state.MapIndex;
            var choice = new CheckBox { Name = $"UltimatumRegion_{id}", Text = state.Name,
                TooltipText = Fmt.People(state.Population), ClipText = true, CustomMinimumSize = new(260, 32) };
            if (_selectedState?.MapIndex == id) { choice.ButtonPressed = true; _ultimatumRegions.Add(id); }
            choice.Toggled += selected =>
            {
                if (selected) _ultimatumRegions.Add(id); else _ultimatumRegions.Remove(id);
                RefreshUltimatumComposer();
            };
            regionRows.AddChild(choice);
        }
        if (offered.Length == 0) regionRows.AddChild(WrappedUltimatumText("No eligible regions. Financial or treaty demands remain available.", 13));
        search.TextChanged += text =>
        { foreach (var choice in regionRows.GetChildren().OfType<CheckBox>()) choice.Visible = choice.Text.Contains(text, StringComparison.OrdinalIgnoreCase); };
        var termsScroll = new ScrollContainer { CustomMinimumSize = new(280, 0),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.AddChild(termsScroll);
        var terms = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        terms.AddThemeConstantOverride("separation", 10);
        termsScroll.AddChild(terms);
        terms.AddChild(Title(Tr("Additional demands"), 17, Ui.Colour("text.accent")));
        terms.AddChild(WrappedUltimatumText("One-time reparations · billions of dollars", 13));
        _ultimatumCash = new SpinBox { Name = "UltimatumReparations", MinValue = 0, Step = .01,
            MaxValue = Math.Max(0, target.Treasury) * Math.Clamp(_campaign.Defines.UltimatumMaxTreasuryShare, 0, 1),
            CustomMinimumSize = new(0, 36) };
        terms.AddChild(_ultimatumCash);
        terms.AddChild(Title(Loc.Format("Maximum: {0}", Fmt.Money((float)_ultimatumCash.MaxValue)), 12, Ui.Colour("text.muted")));
        _ultimatumEmbargo = new CheckBox { Name = "UltimatumLiftEmbargo", Text = Tr("Lift their embargo"),
            Disabled = !CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, target.Id).EmbargoBy(target.Id) };
        terms.AddChild(_ultimatumEmbargo);
        _ultimatumPact = new CheckBox { Name = "UltimatumNonAggression", Text = Tr("Non-aggression pact") };
        terms.AddChild(_ultimatumPact);
        _ultimatumCash.ValueChanged += _ => RefreshUltimatumComposer();
        _ultimatumEmbargo.Toggled += _ => RefreshUltimatumComposer();
        _ultimatumPact.Toggled += _ => RefreshUltimatumComposer();
        rows.AddChild(new HSeparator());
        _ultimatumAssessment = WrappedUltimatumText("", 16);
        _ultimatumAssessment.Name = "UltimatumAssessment";
        rows.AddChild(_ultimatumAssessment);
        _ultimatumTerms = WrappedUltimatumText("", 13);
        _ultimatumTerms.Name = "UltimatumTerms";
        var summaryScroll = new ScrollContainer { CustomMinimumSize = new(0, 64),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        rows.AddChild(summaryScroll);
        summaryScroll.AddChild(_ultimatumTerms);
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        rows.AddChild(actions);
        _ultimatumClose = ActionButton("Cancel", CloseUltimatumComposer, 130, 36);
        actions.AddChild(_ultimatumClose);
        _ultimatumSend = ActionButton("Review demand", SendComposedUltimatum, 240, 36, true);
        _ultimatumSend.Name = "SendComposedUltimatum";
        actions.AddChild(_ultimatumSend);
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
        _ultimatumSend.Text = Tr("Review demand");
        _ultimatumAssessment.Text = !assessment.Valid ? Tr(assessment.Reason) : assessment.Response == Ultimatum.Answer.Yield
            ? Tr("They are expected to yield") : Tr("They are expected to refuse");
        _ultimatumAssessment.LabelSettings.FontColor = Ui.Colour(assessment.Valid ? "text.accent" : "state.warning");
        _ultimatumTerms.Text = !assessment.Valid ? Tr("No terms have been sent. Change the package to resolve this restriction.") :
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
            NonAggression = demand.NonAggression };
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
        _ultimatumTerms.Text = command.Result.Accepted ? Loc.Format("{0}\nTruce: {1} days remain.", command.Result.Note,
            Math.Max(0, relation.TruceUntil - _campaign.ElapsedDays)) : Tr("No terms have been sent.");
        ForceDiplomacyRefresh();
        RefreshCountryViews("Diplomacy");
        Refresh();
    }
}
