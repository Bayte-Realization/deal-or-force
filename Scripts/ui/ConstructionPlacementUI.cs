using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private BuildingKind? _placingBuilding;
    private PanelContainer _constructionPlacement;
    private Label _constructionPlacementFeedback;
    private Label _constructionPlacementTerms, _constructionPlacementPreview;
    private ScenarioState _constructionPreviewState;
    private long _constructionPreviewHour = -1;
    private bool _constructionSelectionWasCountryOnly;
    private Vector2? _constructionPointer;

    /// <summary>Placement queues a regional project, not a province-local 3D object.
    /// Keep this distinction explicit until individual building sites are simulated.</summary>
    private void BeginConstructionPlacement(BuildingKind kind)
    {
        if (_campaign?.Player is null) return;
        CancelRecruitmentPlacement();
        CancelConstructionPlacement(); CloseSidePanel(false); HideMapSelection(true);
        _placingBuilding = kind;
        _constructionPointer = null;
        _constructionSelectionWasCountryOnly = _globe.CountrySelectionOnly;
        // Province clicks must not be intercepted by an army counter while placing a site.
        _globe.CountrySelectionOnly = true;
        _selectedArmy = null; _selectedArmies.Clear(); _globe.SelectArmy(-1);
        _constructionPlacement = new PanelContainer { Name = "ConstructionPlacement", MouseFilter = MouseFilterEnum.Stop };
        _constructionPlacement.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        _hudLayer.AddChild(_constructionPlacement);
        // Below the top bar and alerts, outside the report's right-hand column.
        _constructionPlacement.Position = new Vector2(120, 148);
        _constructionPlacement.Size = new Vector2(620, 420);
        var rows = new VBoxContainer(); rows.AddThemeConstantOverride("separation", 8); _constructionPlacement.AddChild(rows);
        rows.AddChild(Title(Simulation.Loc.Format("Place construction: {0}", Tr(BuildingCatalog.Get(kind).Name)), 20, Ui.Colour("text.accent")));
        _constructionPlacementTerms = WrappedUltimatumText("", 14);
        _constructionPlacementTerms.Name = "ConstructionPlacementTerms"; rows.AddChild(_constructionPlacementTerms);
        _constructionPlacementFeedback = WrappedUltimatumText(
            "Hover over a region to inspect it before building. Click to queue one project; Escape or right-click finishes placement.", 14);
        _constructionPlacementFeedback.Name = "ConstructionPlacementFeedback"; rows.AddChild(_constructionPlacementFeedback);
        var scroll = new ScrollContainer { Name = "ConstructionPreviewScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 90) };
        rows.AddChild(scroll);
        _constructionPlacementPreview = WrappedUltimatumText("Move the pointer over the map to compare sites. This is a regional project, not an exact building footprint.", 14);
        _constructionPlacementPreview.Name = "ConstructionPlacementPreview";
        _constructionPlacementPreview.SizeFlagsVertical = SizeFlags.ShrinkBegin; scroll.AddChild(_constructionPlacementPreview);
        var actions = new HBoxContainer(); rows.AddChild(actions);
        var back = ActionButton("Building catalogue", () => { CancelConstructionPlacement(); OpenEconomyTab("Construction"); }, 0, 36);
        back.Name = "ConstructionPlacementCatalogue";
        back.SizeFlagsHorizontal = SizeFlags.ExpandFill; actions.AddChild(back);
        var cancel = ActionButton("Finish placement", CancelConstructionPlacement, 0, 36);
        cancel.Name = "ConstructionPlacementFinish";
        cancel.SizeFlagsHorizontal = SizeFlags.ExpandFill; actions.AddChild(cancel);
        _constructionPlacement.AddChild(new DecisionDialogLayout { Panel = _constructionPlacement, PreservePlacement = true,
            PlacementHeight = 420, AvoidLeft = _hudNavigationPanel, AvoidTop = _hudTopPanel });
        _constructionPlacement.AddChild(new ConstructionPreviewPoll { Name = "ConstructionPreviewPoll", Game = this });
        RefreshConstructionTerms();
        // Removing keyboard focus prevents Space on the old '+' from queuing again.
        GetViewport().GuiReleaseFocus();
    }

    private void CancelConstructionPlacement()
    {
        if (_placingBuilding is not null && _globe is not null) _globe.CountrySelectionOnly = _constructionSelectionWasCountryOnly;
        _placingBuilding = null;
        if (IsInstanceValid(_constructionPlacement))
        { _constructionPlacement.Hide(); _constructionPlacement.QueueFree(); }
        _constructionPlacement = null; _constructionPlacementFeedback = null;
        _constructionPlacementTerms = null; _constructionPlacementPreview = null;
        _constructionPreviewState = null; _constructionPreviewHour = -1;
    }

    private void RefreshConstructionTerms()
    {
        if (_placingBuilding is not { } kind || _campaign?.Player is not { } player) return;
        var running = _campaign.Construction.Count(p => p.NationId == player.Id);
        _constructionPlacementTerms.Text = Loc.Format("Contract {0} · {1} working days · sites {2}/{3}",
            Fmt.Money(ConstructionCost.Of(_campaign, player, kind)), ConstructionCost.ExpectedDays(_campaign, player, kind),
            running, ConstructionCost.Limit(_campaign, player, _campaign.Defines.MaxConstructionProjects));
        Tip(_constructionPlacementTerms, "The contract is paid over time. Working days exclude waiting for a free site or funding; materials and future conditions can change the result.");
    }

    internal void RefreshConstructionPreview(ScenarioState state)
    {
        if (_placingBuilding is not { } kind || _campaign?.Player is not { } player || _constructionPlacementPreview is null) return;
        if (ReferenceEquals(state, _constructionPreviewState) && _constructionPreviewHour == _campaign.ElapsedHours) return;
        _constructionPreviewState = state; _constructionPreviewHour = _campaign.ElapsedHours;
        RefreshConstructionTerms();
        if (state is null)
        {
            _constructionPlacementPreview.Text = Tr("Move the pointer over the map to compare sites. This is a regional project, not an exact building footprint.");
            return;
        }
        var refusal = ConstructionAdmission.Refusal(_campaign, player, state, kind);
        var heading = state.DisplayName + "\n" + (refusal.Length == 0 ? Tr("Click to build here") : refusal);
        // Do not expose speculative economic data for a foreign country using the player's prices.
        var administered = state.NationMapIndex == player.MapIndex || state.HeldById == player.Id && HeldRegions.Administered(_campaign, state);
        _constructionPlacementPreview.Text = heading + (administered && !LakeRegions.IsWaterState(state)
            ? "\n\n" + BuildingEffects(state, kind).Text + "\n\n" + Tr("Estimate at current prices, staffing and supply, not guaranteed GDP or profit.") : "");
    }

    internal void PollConstructionPreview()
    {
        if (_placingBuilding is null || !IsInstanceValid(_constructionPlacement) || !_constructionPlacement.IsVisibleInTree()) return;
        if (_campaign?.MidStep == true) return; // Never read a half-completed market update.
        var hovered = GetViewport().GuiGetHoveredControl();
        if (hovered is not null && hovered != _globe && !_globe.IsAncestorOf(hovered)) return;
        RefreshConstructionPreview(_globe.TryConstructionPreview(_constructionPointer ?? GetViewport().GetMousePosition(), out var pick) ? pick.State : null);
    }

    private bool HandleConstructionPlacementInput(InputEvent input)
    {
        if (_placingBuilding is null || !IsInstanceValid(_constructionPlacement)) return false;
        // _Input supplies viewport coordinates even when the window/UI is scaled.
        // Keep the event position instead of repeatedly querying desktop mouse state.
        if (input is InputEventMouseMotion motion) { _constructionPointer = motion.Position; return false; }
        if (input is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } &&
            input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) return false;
        CancelConstructionPlacement(); GetViewport().SetInputAsHandled(); return true;
    }

    private bool TryConstructionMapPick(GlobePick pick)
    {
        if (_placingBuilding is not { } kind || _campaign?.Player is not { } player || !_hudLayer.Visible) return false;
        var refusal = ConstructionAdmission.Refusal(_campaign, player, pick.State, kind);
        if (refusal.Length > 0)
        { _constructionPlacementFeedback.Text = refusal; PlayStrategicCue("ui_error"); return true; }
        var previous = _campaign.Construction.Count(p => p.NationId == player.Id);
        _campaign.Commands.Enqueue(new QueueConstructionCommand { IssuerId = player.Id, StateMapIndex = pick.State.MapIndex, Building = kind });
        _campaign.Commands.ApplyAll(_campaign);
        var accepted = _campaign.Construction.Count(p => p.NationId == player.Id) > previous;
        _selectedState = pick.State; _selectedProvinceId = pick.ProvinceId;
        _constructionPreviewHour = -1;
        RefreshConstructionTerms(); RefreshConstructionPreview(pick.State);
        _constructionPlacementFeedback.Text = accepted
            ? Simulation.Loc.Format("Queued in {0}. Contract: {1}; estimated {2} days. Click another region or finish placement.",
                pick.State.DisplayName, Fmt.Money(ConstructionCost.Of(_campaign, player, kind)), ConstructionCost.ExpectedDays(_campaign, player, kind))
            : Tr("Order submitted. The host will validate the construction site.");
        PlayStrategicCue("ui_select");
        return true;
    }
}

/// <summary>Only an active placement tool polls; changing the hovered region triggers the expensive forecast.</summary>
public partial class ConstructionPreviewPoll : Node
{
    internal DealOrForceGame Game;
    private double _remaining;
    public override void _Process(double delta)
    {
        _remaining -= delta;
        if (_remaining > 0) return;
        _remaining = .15;
        Game.PollConstructionPreview();
    }
}
