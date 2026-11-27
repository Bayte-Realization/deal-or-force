using System;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Write-lock-safe command dispatch, lazy stroke anchoring, touch-native prompts,
/// scrollable planning-bar chrome with Cancel/Done actions, and NewShell z-order / dock
/// helpers for <see cref="DealOrForceGame"/>'s battle-plan and army-orders UI.
/// </summary>
public partial class DealOrForceGame
{
    private HBoxContainer _planningBarRow;
    private ScrollContainer _planningBarScroll;
    private Button _planCancelButton;
    private Button _planDoneButton;
    private string _planTransientHint = "";

    private static bool IsTouchPrimaryUi => Platform.PlatformProfile.Current?.IsTouchPrimary == true;

    /// <summary>
    /// Executes battle-plan mutations safely under <see cref="BackgroundSimRunner.RunWrite"/>
    /// whenever the background worker is active, publishes a fresh visual snapshot immediately,
    /// and wakes globe rendering so drawn lines appear on Frame 0 even while paused.
    /// </summary>
    private void ApplyBattlePlanCommands(Action<Campaign> enqueue)
    {
        if (_campaign is null) return;
        if (_simRunner is not null && _simRunner.IsActive &&
            ReferenceEquals(_simRunner.Campaign, _campaign) &&
            !_campaign.AccessGate.IsWriterHeldByCurrentThread)
        {
            _simRunner.RunWrite(c =>
            {
                enqueue(c);
                c.Commands.ApplyAll(c);
            }, refreshSnapshot: true);
        }
        else
        {
            enqueue(_campaign);
            _campaign.Commands.ApplyAll(_campaign);
        }
        _globe?.RefreshBattlePlans();
        _globe?.WakeGlobeRendering();
    }

    private void CancelBattlePlanDrawing()
    {
        EndDrawing();
        SetArmyFeedback(Tr("The line was abandoned."));
    }

    private void OnBorderStrokeCanceled()
    {
        _borderDraft = null;
        _editingBorderId = -1;
        _drawnProvinces.Clear();
        _offensiveStroke.Clear();
        _offensiveSource = null;
        _defensiveStroke.Clear();
        _defensiveStrokeValid = false;
        _globe?.ClearPlanPreview();
        RefreshDrawingUi();
    }

    /// <summary>
    /// Forgiving drag anchor: if a drag started slightly inland (or on friendly territory for
    /// an offensive arrow) and then crossed onto valid border / target / controlled land, start
    /// the stroke seamlessly instead of dropping the entire drag.
    /// </summary>
    private bool TryLazyAnchorBorderStroke(GlobePick pick)
    {
        var owner = _campaign?.Player?.Id ?? -1;
        if (owner < 0 || !SelectedPlayerArmies().Any()) return false;

        if (_drawingKind == FrontLineKind.Fallback)
        {
            if (_defensiveStrokeValid) return true;
            if (!DefensivePlanning.IsPost(_campaign, owner, pick.ProvinceId)) return false;
            _defensiveStrokeValid = true;
            _planTransientHint = "";
            return true;
        }

        if (_drawingKind == FrontLineKind.Offensive)
        {
            if (_offensiveSource is null)
                _offensiveSource = OffensivePlanning.Source(_campaign, SelectedPlayerArmies());
            if (_offensiveSource is null) return false;
            _planTransientHint = "";
            return true;
        }

        if (_drawingKind == FrontLineKind.Defence && _borderDraft is null)
        {
            var foreign = BorderFronts.ForeignAt(_campaign, owner, pick.ProvinceId);
            if (foreign < 0) return false;
            _planTransientHint = "";
            _editingStart = false;
            _borderDraft = new DrawnFrontLine
            {
                NationId = owner,
                Kind = FrontLineKind.Defence,
                BorderNationId = foreign,
                WholeBorder = false,
                StartLatitude = pick.Latitude,
                StartLongitude = pick.Longitude,
                EndLatitude = pick.Latitude,
                EndLongitude = pick.Longitude
            };
            return true;
        }
        return _borderDraft is not null;
    }

    /// <summary>
    /// Keeps the drawing tool armed with a visible on-bar guidance message when a click or drag
    /// missed valid territory, rather than silently exiting drawing mode.
    /// </summary>
    private bool HandleEmptyStrokeRetry(FrontLineKind kind)
    {
        if (kind == FrontLineKind.AreaDefence) return false;
        var touch = IsTouchPrimaryUi;
        var key = kind switch
        {
            FrontLineKind.Defence => touch ? "mobile.plan.need_border_retry" : "plan.pc.need_border_retry",
            FrontLineKind.Offensive => touch ? "mobile.plan.need_enemy_retry" : "plan.pc.need_enemy_retry",
            _ => touch ? "mobile.plan.need_own_retry" : "plan.pc.need_own_retry"
        };
        _planTransientHint = Tr(key);
        SetArmyFeedback(_planTransientHint);
        _borderDraft = null;
        _offensiveStroke.Clear();
        _defensiveStroke.Clear();
        _defensiveStrokeValid = false;
        _drawnProvinces.Clear();
        _globe?.ClearPlanPreview();
        RefreshDrawingUi();
        return true;
    }

    private void BuildPlanningBarControls(VBoxContainer column)
    {
        _planningBar.ClipContents = true;
        var isTouch = IsTouchPrimaryUi;
        var height = isTouch ? 40f : Ui.Metric("planning_bar.button_height", 30);

        _planningBarScroll = new ScrollContainer
        {
            Name = "PlanningBarScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true,
            CustomMinimumSize = new Vector2(0, height + 4f)
        };
        column.AddChild(_planningBarScroll);

        _planningBarRow = new HBoxContainer { Name = "PlanningBarRow" };
        _planningBarRow.AddThemeConstantOverride("separation", Ui.Gap(4));
        _planningBarScroll.AddChild(_planningBarRow);

        var wholeFront = ArmyIconButton("Whole front", AssignWholeFront, 0, height, false, "plan_front");
        wholeFront.Name = "WholeFront";
        ArmyCommandTip(wholeFront, "Whole front",
            "One front along every land border with every country at war with you. It grows and shrinks by itself as the line moves and as enemies join or leave the war; the selection spreads along all of it.");
        _planningBarRow.AddChild(wholeFront);

        AddPlanningDrawButton(_planningBarRow, "One border", "plan_front", FrontLineKind.Defence, height,
            "Front on one border", isTouch ? "mobile.plan.one_border_tip" : "plan.pc.one_border_tip");
        AddPlanningDrawButton(_planningBarRow, "Attack", "plan_attack", FrontLineKind.Offensive, height,
            "Offensive", isTouch ? "mobile.plan.attack_tip" : "plan.pc.attack_tip");
        AddPlanningDrawButton(_planningBarRow, "Defend", "plan_fallback", FrontLineKind.Fallback, height,
            "Defensive line", isTouch ? "mobile.plan.defend_tip" : "plan.pc.defend_tip");

        _planEraserButton = ArmyIconButton("", TogglePlanEraser, isTouch ? 44 : 0, height, false, "plan_erase");
        ArmyCommandTip(_planEraserButton, "Eraser",
            isTouch ? "mobile.plan.eraser_tip" : "Click a plan on the map to erase it. An offensive arrow erases only its objective and keeps the front it leaves from.");
        _planningBarRow.AddChild(_planEraserButton);

        _planningBarRow.AddChild(new VSeparator());

        _planningExecute = ArmyIconButton("Execute", TogglePlanningExecution, 0, height, true, "plan_execute");
        _planningBarRow.AddChild(_planningExecute);

        var halt = ArmyIconButton("", () => IssueArmyOrder(ArmyOrder.Manual), isTouch ? 44 : 0, height, false, "army_halt");
        ArmyCommandTip(halt, isTouch ? "Halt" : "Halt (H)", "Stop the selection where it stands, out of any plan.");
        _planningBarRow.AddChild(halt);

        var clear = ArmyIconButton("", ClearDrawnLine, isTouch ? 44 : 0, height, false, "plan_clear");
        ArmyCommandTip(clear, "Clear plans", "Delete the plans of the selected divisions and stop their planned movement. Battles already joined go on.");
        _planningBarRow.AddChild(clear);

        _planDoneButton = ActionButton(Tr("plan.bar.done"), CommitDrawnLine, isTouch ? 74 : 64, height, primary: true);
        _planDoneButton.Name = "PlanDrawDone";
        _planDoneButton.TooltipText = Ui.Tip(Tr("plan.bar.done_tip"));
        _planDoneButton.Visible = false;
        _planningBarRow.AddChild(_planDoneButton);

        _planCancelButton = ActionButton(Tr("plan.bar.cancel"), CancelBattlePlanDrawing, isTouch ? 82 : 68, height, primary: false);
        _planCancelButton.Name = "PlanDrawCancel";
        _planCancelButton.TooltipText = Ui.Tip(Tr("plan.bar.cancel_tip"));
        _planCancelButton.Visible = false;
        _planningBarRow.AddChild(_planCancelButton);

        _drawFeedback = new Label
        {
            Text = "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _drawFeedback.AddThemeFontSizeOverride("font_size", Ui.TypeStep((int)Ui.Metric("planning_bar.hint_font", 11)));
        _drawFeedback.AddThemeColorOverride("font_color", Ui.Colour("text.muted"));
        column.AddChild(_drawFeedback);
    }

    private void AddPlanningDrawButton(
        Container parent, string label, string icon, FrontLineKind kind, float height, string name, string helpKey)
    {
        var button = ArmyIconButton(label, () => ToggleDrawing(kind), 92, height, false, icon);
        ArmyCommandTip(button, name, helpKey);
        _drawButtons[kind] = button;
        parent.AddChild(button);
    }

    private void FormatDrawingFeedback()
    {
        var activeTool = _drawingKind is not null || _erasingPlans;
        if (IsInstanceValid(_planCancelButton))
        {
            _planCancelButton.Visible = activeTool;
            _planCancelButton.Text = Tr("plan.bar.cancel");
        }
        if (IsInstanceValid(_planDoneButton))
        {
            _planDoneButton.Visible = _drawingKind == FrontLineKind.AreaDefence;
            _planDoneButton.Text = Tr("plan.bar.done");
        }
        if (!IsInstanceValid(_drawFeedback)) return;

        var hasWarning = !string.IsNullOrEmpty(_planTransientHint);
        if (hasWarning && _borderDraft is null && _offensiveStroke.Count == 0 && _defensiveStroke.Count == 0)
        {
            _drawFeedback.Text = _planTransientHint;
            _drawFeedback.AddThemeColorOverride("font_color", Ui.Colour("state.warning"));
            return;
        }

        var touch = IsTouchPrimaryUi;
        _drawFeedback.Text = ResolveDrawingPromptText(touch);
        _drawFeedback.AddThemeColorOverride("font_color",
            Ui.Colour(activeTool ? "text.bright" : "text.muted"));
    }

    private string ResolveDrawingPromptText(bool touch)
    {
        if (_erasingPlans)
            return Tr(touch ? "mobile.plan.eraser_idle" : "Eraser: click one of your plans. Offensive arrows erase the objective only. Esc cancels.");
        if (_drawingKind is null)
            return OffensiveStatus();
        if (_drawingKind == FrontLineKind.Defence)
        {
            if (_borderDraft is not null)
                return Loc.Format(touch ? "mobile.plan.border_dragging" : "plan.pc.border_dragging", _drawnProvinces.Count);
            if (!touch && _borderHoverCountry >= 0 && _campaign?.NationById(_borderHoverCountry) is { } neighbour)
                return Loc.Format("plan.pc.border_hover", neighbour.DisplayName, _drawnProvinces.Count);
            return Tr(touch ? "mobile.plan.border_idle" : "plan.pc.border_idle");
        }
        if (_drawingKind == FrontLineKind.Offensive)
        {
            if (touch && _offensiveStroke.Count == 0) return Tr("mobile.plan.offensive_idle");
            return Loc.Format(touch ? "mobile.plan.offensive_dragging" : "plan.pc.offensive_dragging", _offensiveStroke.Count);
        }
        if (_drawingKind == FrontLineKind.Fallback)
        {
            if (touch && _defensiveStroke.Count == 0) return Tr("mobile.plan.fallback_idle");
            return Loc.Format(touch ? "mobile.plan.fallback_dragging" : "plan.pc.fallback_dragging", _defensiveStroke.Count);
        }
        return touch
            ? Loc.Format("mobile.plan.area_drawing", DrawName(_drawingKind.Value), _drawnProvinces.Count)
            : Loc.Format("Drawing a {0} — {1:N0} provinces.", DrawName(_drawingKind.Value), _drawnProvinces.Count) +
              " " + Tr("Click the same button again to commit.");
    }

    private (float Width, float Height) MeasurePlanningBarDesiredSize()
    {
        var isTouch = IsTouchPrimaryUi;
        var rowW = _planningBarRow is not null && IsInstanceValid(_planningBarRow)
            ? _planningBarRow.GetCombinedMinimumSize().X + 22f
            : 520f;
        var minH = _planningBar is not null && IsInstanceValid(_planningBar)
            ? _planningBar.GetCombinedMinimumSize().Y
            : 0f;
        var wantedH = MathF.Max(minH, isTouch ? 74f : 60f);
        return (MathF.Max(340f, rowW), wantedH);
    }

    private void RaiseArmyAndPlanningChromeForNewShell(bool modalOpen)
    {
        if (modalOpen || _hudLayer is null || _newShellRoot is null || !IsInstanceValid(_newShellRoot)) return;
        var targetIdx = Math.Min(_hudLayer.GetChildCount() - 1, _newShellRoot.GetIndex() + 1);
        if (_armyPanel is { Visible: true } && IsInstanceValid(_armyPanel) && _armyPanel.GetIndex() < _newShellRoot.GetIndex())
            _hudLayer.MoveChild(_armyPanel, targetIdx);
        if (_planningBar is { Visible: true } && IsInstanceValid(_planningBar) && _planningBar.GetIndex() < _newShellRoot.GetIndex())
            _hudLayer.MoveChild(_planningBar, Math.Min(_hudLayer.GetChildCount() - 1, _newShellRoot.GetIndex() + 1));
        if (_compactProvincePopover is { Visible: true } && IsInstanceValid(_compactProvincePopover))
            _compactProvincePopover.MoveToFront();
    }

    private void DockPlanningBarClassic()
    {
        if (!IsInstanceValid(_armyBar) || !IsInstanceValid(_planningBar)) return;
        var left = _armyBar.OffsetLeft;
        var vpW = _hudLayer?.Size.X > 320f ? _hudLayer.Size.X : GetViewportRect().Size.X;
        var (desiredW, desiredH) = MeasurePlanningBarDesiredSize();
        var maxRight = vpW - 12f;
        if (_mapControlsPanel is { Visible: true } && IsInstanceValid(_mapControlsPanel))
            maxRight = MathF.Min(maxRight, _mapControlsPanel.GetGlobalRect().Position.X - 10f);
        var clampedW = MathF.Max(220f, MathF.Min(desiredW, maxRight - left));
        _planningBar.OffsetLeft = left;
        _planningBar.OffsetRight = left + clampedW;
        var bottom = _armyBar.OffsetTop - 6f;
        if (bottom < -16f)
        {
            _planningBar.OffsetBottom = bottom;
            _planningBar.OffsetTop = bottom - desiredH;
        }
    }
}
