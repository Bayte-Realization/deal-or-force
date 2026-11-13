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

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 3);
        _planningBar.AddChild(column);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", Ui.Gap(4));
        column.AddChild(row);

        var height = Ui.Metric("planning_bar.button_height", 30);
        // Where the line is: the whole border, or one drawn by hand.
        var wholeFront = ArmyIconButton("Whole front", AssignWholeFront, 0, height, false, "plan_front");
        wholeFront.Name = "WholeFront";
        ArmyCommandTip(wholeFront, "Whole front",
            "One front along every land border with every country at war with you. It grows and shrinks by itself as the line moves and as enemies join or leave the war; the selection spreads along all of it.");
        row.AddChild(wholeFront);
        AddDrawButton(row, "Front", "plan_front", FrontLineKind.Defence, "Front line",
            "Drag with the right button along a border: the selection spreads along that stretch and digs in.");
        AddDrawButton(row, "Attack", "plan_attack", FrontLineKind.Offensive, "Offensive",
            "With the selection on one front: drag with the right button where it should get to, inside the enemy's country. Shift+drag adds a later phase, Alt+drag redraws the current one.");
        AddDrawButton(row, "Defend", "plan_fallback", FrontLineKind.Fallback, "Defensive line",
            "Drag with the right button across your own ground: the selection spreads along the line and holds it at once, in peace or war.");
        _planEraserButton = ArmyIconButton("", TogglePlanEraser, 0, height, false, "plan_erase");
        ArmyCommandTip(_planEraserButton, "Eraser",
            "Click a plan on the map to erase it. An offensive arrow erases only its objective and keeps the front it leaves from.");
        row.AddChild(_planEraserButton);

        row.AddChild(new VSeparator());
        // The switch: an offensive waits on its front until it is told to go, and can be held again.
        _planningExecute = ArmyIconButton("Execute", TogglePlanningExecution, 0, height, true, "plan_execute");
        row.AddChild(_planningExecute);
        var halt = ArmyIconButton("", () => IssueArmyOrder(ArmyOrder.Manual), 0, height, false, "army_halt");
        ArmyCommandTip(halt, "Halt (H)", "Stop the selection where it stands, out of any plan.");
        row.AddChild(halt);
        var clear = ArmyIconButton("", ClearDrawnLine, 0, height, false, "plan_clear");
        ArmyCommandTip(clear, "Clear plans", "Delete the plans of the selected divisions and stop their planned movement. Battles already joined go on.");
        row.AddChild(clear);

        // What the tool in hand does, and what it has drawn: the only instructions there are.
        _drawFeedback = new Label
        {
            Text = "", AutowrapMode = TextServer.AutowrapMode.Off, ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _drawFeedback.AddThemeFontSizeOverride("font_size", Ui.TypeStep((int)Ui.Metric("planning_bar.hint_font", 11)));
        _drawFeedback.AddThemeColorOverride("font_color", Ui.Colour("text.muted"));
        column.AddChild(_drawFeedback);
    }

    /// <summary>Shown while divisions are selected or a tool is in hand; beside the orders panel.</summary>
    private void RefreshPlanningBar()
    {
        if (_planningBar is null || _campaign?.Player is null) return;
        var mine = SelectedPlayerDivisions();
        var show = _hudLayer is { Visible: true } && (mine.Length > 0 || _drawingKind is not null || _erasingPlans);
        if (_planningBar.Visible != show) _planningBar.Visible = show;
        if (!show) return;
        if (IsInstanceValid(_armyBar))
        {
            var left = _armyBar.OffsetLeft;
            if (System.Math.Abs(_planningBar.OffsetLeft - left) > 0.5f)
            {
                _planningBar.OffsetLeft = left;
                _planningBar.OffsetRight = left + _planningBar.GetCombinedMinimumSize().X;
            }
        }

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
