using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The planning bar: the battle-plan tools on the map, over the army bar, whenever divisions of
/// the player's are selected.
///
/// The tools used to sit half way down the orders page under a heading and above two paragraphs
/// of instructions, so drawing a plan meant reading first and looking for the button second.
/// Here they are one row of pictures where the eye already is - beside the armies - in the order
/// a plan is made: man a front, draw where to attack or where to hold, rub out, then the switch
/// that sets it going. The one line under them says what the tool in hand does and what it has
/// drawn so far, so no instruction has to be read before it is needed.
///
/// Its place is `hud.planning_bar` and its colours the `planning_bar.*` tokens (game/gui).
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _planningBar;
    private Button _planningExecute;

    private void BuildPlanningBar()
    {
        _planningBar = new PanelContainer { Name = "PlanningBar", Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour("planning_bar.background"),
            BorderColor = Ui.Colour("planning_bar.border"),
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 5, ContentMarginBottom = 5
        };
        style.SetBorderWidthAll(1);
        _planningBar.AddThemeStyleboxOverride("panel", style);
        _hudLayer.AddChild(_planningBar);
        Ui.Place(_planningBar, "hud.planning_bar");

        var column = new VBoxContainer { ClipContents = true };
        column.AddThemeConstantOverride("separation", 3);
        _planningBar.AddChild(column);
        BuildPlanningBarControls(column);
    }

    /// <summary>Shown while divisions are selected or a tool is in hand; beside the orders panel.</summary>
    private void RefreshPlanningBar()
    {
        if (_planningBar is null) return;
        if (_campaign?.Player is null || ObserverUiRules.IsObserving(_campaign, _observerMode))
        {
            if (_planningBar.Visible) _planningBar.Visible = false;
            return;
        }
        var mine = SelectedPlayerDivisions();
        var show = _hudLayer is { Visible: true } && (mine.Length > 0 || _drawingKind is not null || _erasingPlans);
        if (_planningBar.Visible != show) _planningBar.Visible = show;
        if (!show) return;
        SyncPlanningBarDock();

        var offensive = mine.Where(IsOffensive).ToArray();
        var executing = offensive.Length > 0 && offensive.All(army => army.PlanActive);
        var caption = Tr(executing ? "Executing" : "Execute");
        if (_planningExecute.Text != caption)
        {
            _planningExecute.Text = caption;
            ArmyCommandTip(_planningExecute, executing ? "Executing" : "Execute",
                executing
                    ? "The offensive is under way. Press to hold it: the divisions stop on their front, dig in and build up the plan again."
                    : "Set the selected offensive going: the divisions advance phase by phase and stop after the last objective. Fronts and defensive lines are not affected.");
        }
        _planningExecute.Disabled = offensive.Length == 0;
        StyleButton(_planningExecute, executing);
        RefreshDrawingUi();
    }

    private void TogglePlanningExecution()
    {
        var offensive = SelectedPlayerDivisions().Where(IsOffensive).ToArray();
        if (offensive.Length == 0) return;
        IssuePlanExecution(!offensive.All(army => army.PlanActive));
        RefreshPlanningBar();
    }
}
