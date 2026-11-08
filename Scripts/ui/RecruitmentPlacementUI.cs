using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private string _placingRecruitmentTemplate;
    private int _placingRecruitmentCount, _placingRecruitmentIssuer;
    private MobilisationMode _placingRecruitmentMode;
    private bool _recruitmentSelectionWasCountryOnly;
    private Vector2? _recruitmentPointer;
    private PanelContainer _recruitmentPlacement;
    private Label _recruitmentPlacementHeading, _recruitmentPlacementTerms, _recruitmentPlacementFeedback, _recruitmentPlacementPreview;

    private void BeginRecruitmentPlacement(string templateId, int count, MobilisationMode mode)
    {
        if (_campaign?.Player is not { IsHuman: true } player || !RecruitmentSystem.Enabled(_campaign) ||
            !UnitTypeCatalog.All.Any(template => template.Id == templateId)) return;
        CancelConstructionPlacement(); CancelRecruitmentPlacement();
        _recruitmentLayer?.Hide();
        // Recruitment may have been opened from a top-bar shortcut over a workspace.
        CloseVisibleCampaignScreen();
        if (_situationLayer is { Visible: true }) CloseSituationRoom();
        CloseSidePanel(false); HideMapSelection(true);
        _placingRecruitmentTemplate = templateId; _placingRecruitmentCount = count;
        _placingRecruitmentMode = mode; _placingRecruitmentIssuer = player.Id;
        _recruitmentPointer = null;
        _recruitmentSelectionWasCountryOnly = _globe.CountrySelectionOnly;
        _globe.CountrySelectionOnly = true;
        _selectedArmy = null; _selectedArmies.Clear(); _globe.SelectArmy(-1);
        _recruitmentPlacement = new PanelContainer { Name = "RecruitmentPlacement", MouseFilter = MouseFilterEnum.Stop };
        _recruitmentPlacement.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        _hudLayer.AddChild(_recruitmentPlacement);
        _recruitmentPlacement.Position = new Vector2(120, 148);
        _recruitmentPlacement.Size = new Vector2(620, 420);
        var rows = new VBoxContainer(); rows.AddThemeConstantOverride("separation", 8); _recruitmentPlacement.AddChild(rows);
        _recruitmentPlacementHeading = Title("", 20, Ui.Colour("text.accent")); rows.AddChild(_recruitmentPlacementHeading);
        _recruitmentPlacementTerms = WrappedUltimatumText("", 14);
        _recruitmentPlacementTerms.Name = "RecruitmentPlacementTerms"; rows.AddChild(_recruitmentPlacementTerms);
        _recruitmentPlacementFeedback = WrappedUltimatumText(
            "Hover over an owned region to inspect training. Click to queue one batch; Escape or right-click finishes placement.", 14);
        _recruitmentPlacementFeedback.Name = "RecruitmentPlacementFeedback";
        var scroll = new ScrollContainer { Name = "RecruitmentPreviewScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 90) };
        rows.AddChild(scroll);
        var previewRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        previewRows.AddThemeConstantOverride("separation", 8); scroll.AddChild(previewRows);
        previewRows.AddChild(_recruitmentPlacementFeedback);
        _recruitmentPlacementPreview = WrappedUltimatumText(
            "Move the pointer over the map. Training belongs to a region; completed divisions deploy in a controlled province of that region.", 14);
        _recruitmentPlacementPreview.Name = "RecruitmentPlacementPreview";
        _recruitmentPlacementPreview.SizeFlagsVertical = SizeFlags.ShrinkBegin; previewRows.AddChild(_recruitmentPlacementPreview);
        var actions = new HBoxContainer(); rows.AddChild(actions);
        var back = ActionButton("Recruitment catalogue", () => OpenRecruitment(preserveSelection: true), 0, 36);
        back.Name = "RecruitmentPlacementCatalogue"; back.SizeFlagsHorizontal = SizeFlags.ExpandFill; actions.AddChild(back);
        var finish = ActionButton("Finish placement", CancelRecruitmentPlacement, 0, 36);
        finish.Name = "RecruitmentPlacementFinish"; finish.SizeFlagsHorizontal = SizeFlags.ExpandFill; actions.AddChild(finish);
        _recruitmentPlacement.AddChild(new DecisionDialogLayout { Panel = _recruitmentPlacement, PreservePlacement = true,
            PlacementHeight = 420, AvoidLeft = _hudNavigationPanel, AvoidTop = _hudTopPanel });
        _recruitmentPlacement.AddChild(new RecruitmentPreviewPoll { Name = "RecruitmentPreviewPoll", Game = this });
        RefreshRecruitmentPlacementPreview(null);
        GetViewport().GuiReleaseFocus();
    }

    private void CancelRecruitmentPlacement()
    {
        if (_placingRecruitmentTemplate is not null && _globe is not null)
            _globe.CountrySelectionOnly = _recruitmentSelectionWasCountryOnly;
        _placingRecruitmentTemplate = null; _recruitmentPointer = null;
        if (IsInstanceValid(_recruitmentPlacement)) { _recruitmentPlacement.Hide(); _recruitmentPlacement.QueueFree(); }
        _recruitmentPlacement = null;
        _recruitmentPlacementHeading = null; _recruitmentPlacementTerms = null;
        _recruitmentPlacementFeedback = null; _recruitmentPlacementPreview = null;
    }

    private string RecruitmentPlacementPosture() => Tr(_placingRecruitmentMode switch
    {
        MobilisationMode.RapidCallUp => "Rapid call-up placement",
        MobilisationMode.TotalMobilisation => "Emergency mobilisation placement",
        _ => "Standard training placement"
    });

    internal void RefreshRecruitmentPlacementPreview(ScenarioState state)
    {
        if (_placingRecruitmentTemplate is null || _recruitmentPlacementPreview is null || _campaign?.MidStep == true) return;
        if (_campaign?.Player is not { IsHuman: true } player || player.Id != _placingRecruitmentIssuer)
        { CancelRecruitmentPlacement(); return; }
        var template = UnitTypeCatalog.Get(_placingRecruitmentTemplate);
        var count = _placingRecruitmentCount; var mode = _placingRecruitmentMode;
        _recruitmentPlacementHeading.Text = Loc.Format("Place recruitment: {0}", Tr(template.Name));
        var cost = RecruitmentSystem.TotalBatchCost(_campaign, player, template, count, mode, out var surcharge);
        var terms = Loc.Format("{0} × {1} · {2}\nPay now: {3} · {4:N0} recruits · {5} training days\nTraining batches {6}/{7}",
            count, Tr(template.Name), RecruitmentPlacementPosture(), Fmt.Money(cost), template.RecruitmentPersonnel * count,
            RecruitmentSystem.TrainingDaysOf(player, template, mode), _campaign.Recruitment.Count(order => order.NationId == player.Id),
            RecruitmentSystem.DynamicQueueCap(_campaign, player, mode));
        if (_recruitmentPlacementTerms.Text != terms) _recruitmentPlacementTerms.Text = terms;
        var preview = Tr("Move the pointer over the map. Training belongs to a region; completed divisions deploy in a controlled province of that region.");
        if (state is not null)
        {
            var refusal = RecruitmentSystem.Validate(_campaign, player.Id, state.MapIndex, template.Id, count, mode: mode);
            preview = state.DisplayName + "\n" + (refusal.Length == 0 ? Tr("Click to begin training here") : Tr(refusal));
            if (state.NationMapIndex == player.MapIndex)
            {
                var bill = StrategicStockpiles.DivisionMaterialBill(template, count, mode);
                var materials = string.Join("\n", bill.Select(item => Loc.Format("Depot {0}: need {1:0.#}, have {2:0.#}",
                    Tr(item.Key), item.Value, StrategicStockpiles.Peek(_campaign, player, item.Key))));
                preview += "\n\n" + Loc.Format("Regional batch limit: {0}\nMissing depot stock procurement: {1}\n{2}",
                    RecruitmentSystem.MaxDivisionsPerBatch(_campaign, player, state, mode), Fmt.Money(surcharge), materials);
                if (mode != MobilisationMode.Standard)
                {
                    var rapid = mode == MobilisationMode.RapidCallUp;
                    preview += "\n\n" + Loc.Format("Posture effects: stability −{0:P1}; regional unrest +{1:P1}; living standard −{2:P1}.",
                        (rapid ? _campaign.Defines.RapidCallUpStabilityPerDivision : _campaign.Defines.TotalMobilisationStabilityPerDivision) * count,
                        (rapid ? .002f : .005f) * count, rapid ? 0f : .002f * count);
                }
            }
        }
        if (_recruitmentPlacementPreview.Text != preview) _recruitmentPlacementPreview.Text = preview;
    }

    internal void PollRecruitmentPreview()
    {
        if (_placingRecruitmentTemplate is null || !IsInstanceValid(_recruitmentPlacement) || !_recruitmentPlacement.IsVisibleInTree() || _campaign?.MidStep == true) return;
        var hovered = GetViewport().GuiGetHoveredControl();
        if (hovered is not null && hovered != _globe && !_globe.IsAncestorOf(hovered)) return;
        RefreshRecruitmentPlacementPreview(_globe.TryConstructionPreview(_recruitmentPointer ?? GetViewport().GetMousePosition(), out var pick) ? pick.State : null);
    }

    private bool HandleRecruitmentPlacementInput(InputEvent input)
    {
        if (_placingRecruitmentTemplate is null || !IsInstanceValid(_recruitmentPlacement)) return false;
        if (input is InputEventMouseMotion motion) { _recruitmentPointer = motion.Position; return false; }
        if (input is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } &&
            input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) return false;
        CancelRecruitmentPlacement(); GetViewport().SetInputAsHandled(); return true;
    }

    private bool TryRecruitmentMapPick(GlobePick pick)
    {
        if (_placingRecruitmentTemplate is null || !_hudLayer.Visible) return false;
        if (_campaign?.Player is not { IsHuman: true } player || player.Id != _placingRecruitmentIssuer)
        { CancelRecruitmentPlacement(); return true; }
        if (_campaign.MidStep)
        { _recruitmentPlacementFeedback.Text = Tr("Wait for the current world update before choosing a training region."); return true; }
        var refusal = RecruitmentSystem.Validate(_campaign, player.Id, pick.State?.MapIndex ?? -1,
            _placingRecruitmentTemplate, _placingRecruitmentCount, mode: _placingRecruitmentMode);
        if (refusal.Length > 0)
        { _recruitmentPlacementFeedback.Text = Tr(refusal); PlayStrategicCue("ui_error"); RefreshRecruitmentPlacementPreview(pick.State); return true; }
        var nextId = _campaign.NextRecruitmentId;
        var command = new RaiseArmyCommand { IssuerId = player.Id, StateMapIndex = pick.State.MapIndex,
            TemplateId = _placingRecruitmentTemplate, Divisions = _placingRecruitmentCount, Mode = _placingRecruitmentMode };
        _campaign.Commands.Enqueue(command); _campaign.Commands.ApplyAll(_campaign);
        var queued = _campaign.Recruitment.FirstOrDefault(order => order.Id >= nextId && order.NationId == player.Id &&
            order.StateMapIndex == pick.State.MapIndex && order.TemplateId == _placingRecruitmentTemplate);
        _recruitmentPlacementFeedback.Text = queued is not null
            ? Loc.Format("Training queued in {0}: {1} × {2}, paid {3}, {4} training days. Click another region or finish placement.",
                pick.State.DisplayName, queued.Count, Tr(UnitTypeCatalog.Get(queued.TemplateId).Name), Fmt.Money(queued.PaidCost), queued.DurationDays)
            : command.FailureReason.Length > 0 ? Tr(command.FailureReason)
            : Tr("Recruitment order submitted. The host will validate this training region.");
        _recruitmentQueueState = "";
        RefreshRecruitmentPlacementPreview(pick.State);
        PlayStrategicCue(queued is not null || command.FailureReason.Length == 0 ? "ui_select" : "ui_error");
        return true;
    }
}

public partial class RecruitmentPreviewPoll : Node
{
    internal DealOrForceGame Game;
    private double _remaining;
    public override void _Process(double delta)
    {
        _remaining -= delta;
        if (_remaining > 0) return;
        _remaining = .15;
        Game.PollRecruitmentPreview();
    }
}
