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

    /// <summary>Placement queues a regional project, not a province-local 3D object.
    /// Keep this distinction explicit until individual building sites are simulated.</summary>
    private void BeginConstructionPlacement(BuildingKind kind)
    {
        if (_campaign?.Player is null) return;
        CancelConstructionPlacement(); CloseSidePanel(false); HideMapSelection(true);
        _placingBuilding = kind;
        _selectedArmy = null; _selectedArmies.Clear(); _globe.SelectArmy(-1);
        _constructionPlacement = new PanelContainer { Name = "ConstructionPlacement", MouseFilter = MouseFilterEnum.Stop };
        _constructionPlacement.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        _hudLayer.AddChild(_constructionPlacement);
        // Below the top bar and alerts, outside the report's right-hand column.
        _constructionPlacement.Position = new Vector2(120, 148);
        _constructionPlacement.Size = new Vector2(620, 180);
        var rows = new VBoxContainer(); rows.AddThemeConstantOverride("separation", 8); _constructionPlacement.AddChild(rows);
        rows.AddChild(Title(Simulation.Loc.Format("Place construction: {0}", Tr(BuildingCatalog.Get(kind).Name)), 20, Ui.Colour("text.accent")));
        _constructionPlacementFeedback = WrappedUltimatumText(
            "Click a land region you administer. Each accepted click queues one building there. Escape or right-click ends placement.", 16);
        _constructionPlacementFeedback.Name = "ConstructionPlacementFeedback"; rows.AddChild(_constructionPlacementFeedback);
        var actions = new HBoxContainer(); rows.AddChild(actions);
        var back = ActionButton("Building catalogue", () => { CancelConstructionPlacement(); OpenEconomyTab("Construction"); }, 0, 36);
        back.SizeFlagsHorizontal = SizeFlags.ExpandFill; actions.AddChild(back);
        var cancel = ActionButton("Finish placement", CancelConstructionPlacement, 0, 36);
        cancel.SizeFlagsHorizontal = SizeFlags.ExpandFill; actions.AddChild(cancel);
        _constructionPlacement.AddChild(new DecisionDialogLayout { Panel = _constructionPlacement, PreservePlacement = true,
            AvoidLeft = _hudNavigationPanel, AvoidTop = _hudTopPanel });
        // Removing keyboard focus prevents Space on the old '+' from queuing again.
        GetViewport().GuiReleaseFocus();
    }

    private void CancelConstructionPlacement()
    {
        _placingBuilding = null;
        if (IsInstanceValid(_constructionPlacement))
        { _constructionPlacement.Hide(); _constructionPlacement.QueueFree(); }
        _constructionPlacement = null; _constructionPlacementFeedback = null;
    }

    private bool HandleConstructionPlacementInput(InputEvent input)
    {
        if (_placingBuilding is null || !IsInstanceValid(_constructionPlacement)) return false;
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
        _constructionPlacementFeedback.Text = accepted
            ? Simulation.Loc.Format("Queued in {0}. Contract: {1}; estimated {2} days. Click another region or finish placement.",
                pick.State.DisplayName, Fmt.Money(ConstructionCost.Of(_campaign, player, kind)), ConstructionCost.ExpectedDays(_campaign, player, kind))
            : Tr("Order submitted. The host will validate the construction site.");
        PlayStrategicCue("ui_select");
        return true;
    }
}
