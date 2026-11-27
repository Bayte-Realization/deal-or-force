using System;
using System.Collections.Generic;
using DealOrForce.Platform;
using Godot;

namespace DealOrForce;

/// <summary>
/// Unified PC (LMB + RMB click/drag) and Android (1-finger tap/drag with seamless 2-finger pinch zoom)
/// battle-plan drawing, painting, and plan-erasing input pipeline for <see cref="GlobePresentation"/>.
/// </summary>
public sealed partial class GlobePresentation
{
    private const float MouseBorderDragThresholdPx = 10f;
    private const float TouchPlanDragThresholdPx = 16f;
    private const float TouchPlanEraseRadiusPx = 28f;
    private const float DesktopPlanEraseRadiusPx = 14f;

    public event Action BorderStrokeCanceled;

    private bool _borderPressed;
    private MouseButton _borderButton;
    private bool _borderDragStarted;
    private Vector2 _borderPressPos;
    private GlobePick _borderPressPick;
    private bool _borderPressHasPick;
    private bool _borderPressAlt;
    private bool _borderPressShift;

    private readonly Dictionary<int, Vector2> _touchPlanFingers = new();
    private int _touchPlanFingerId = -1;
    private bool _touchPlanActive;
    private bool _touchPlanDragging;
    private Vector2 _touchPlanStartPos;
    private GlobePick _touchPlanStartPick;
    private bool _touchPlanHasStartPick;
    private TouchTapMode? _savedTouchModeForPlan;

    /// <summary>
    /// Resets transient drawing pointers and suspends/restores single-finger camera pan while
    /// a battle-plan tool (Front Line, Offensive Line, Defensive Line, Area Defence, or Eraser) is armed.
    /// </summary>
    public void SetBattlePlanToolActive(bool active)
    {
        _borderPressed = false;
        _borderPainting = false;
        _borderDragStarted = false;
        _borderPressHasPick = false;
        _borderHoverProvince = -1;
        _touchPlanActive = false;
        _touchPlanDragging = false;
        _touchPlanFingerId = -1;
        _touchPlanHasStartPick = false;
        _touchPlanFingers.Clear();
        _painting = false;
        _lastPaintedProvince = -1;
        if (active)
        {
            if (_touchAdapter is not null && _savedTouchModeForPlan is null)
                _savedTouchModeForPlan = _touchAdapter.ActiveMode;
            _touchAdapter?.Cancel();
            CancelPendingMapSelection();
        }
        else if (_savedTouchModeForPlan is { } restored)
        {
            _savedTouchModeForPlan = null;
            _touchAdapter?.SetTapMode(restored);
        }
        WakeGlobeRendering();
    }

    private bool HandleAnyTouchInput(InputEvent inputEvent) =>
        TryHandlePlanTouchInput(inputEvent) || HandleTouchInput(inputEvent);

    /// <summary>
    /// Handles raw touch events BEFORE <see cref="TouchCameraAdapter"/> whenever a battle-plan
    /// drawing, area-paint, or plan-erase tool is active. Single finger draws/taps the plan;
    /// adding a second finger cancels the stroke cleanly and hands off to 2-finger pinch zoom.
    /// </summary>
    private bool TryHandlePlanTouchInput(InputEvent inputEvent)
    {
        if (!PlanEraseMode && !BorderPaintMode && !PaintMode)
            return false;

        if (inputEvent is InputEventScreenTouch touch)
        {
            WakeGlobeRendering();
            if (touch.Canceled)
            {
                _touchPlanFingers.Remove(touch.Index);
                CancelInProgressTouchPlanStroke();
                return true;
            }
            if (touch.Pressed)
                return HandleTouchPlanDown(touch);
            return HandleTouchPlanUp(touch);
        }

        if (inputEvent is InputEventScreenDrag drag)
        {
            WakeGlobeRendering();
            _touchPlanFingers[drag.Index] = drag.Position;
            if (_touchPlanFingers.Count >= 2 || !_touchPlanActive || drag.Index != _touchPlanFingerId)
                return false;
            return HandleTouchPlanDrag(drag);
        }

        return false;
    }

    private bool HandleTouchPlanDown(InputEventScreenTouch touch)
    {
        _touchPlanFingers[touch.Index] = touch.Position;
        if (_touchPlanFingers.Count >= 2)
        {
            var firstId = -1;
            var firstPos = Vector2.Zero;
            foreach (var (id, pos) in _touchPlanFingers)
            {
                if (id != touch.Index) { firstId = id; firstPos = pos; break; }
            }
            CancelInProgressTouchPlanStroke();
            if (firstId >= 0 && _touchAdapter is not null && _touchAdapter.ActiveFingerCount == 0)
            {
                var synthetic = new InputEventScreenTouch { Index = firstId, Pressed = true, Position = firstPos };
                _touchAdapter.TryHandleInput(synthetic);
            }
            return false;
        }

        if (_touchAdapter is { IsTouchActive: true })
            return false;

        _touchAdapter?.Cancel();
        _touchPlanFingerId = touch.Index;
        _touchPlanStartPos = touch.Position;
        _touchPlanDragging = false;
        _touchPlanActive = true;
        _touchPlanHasStartPick = false;

        if (PlanEraseMode) return true;
        if (PaintMode)
        {
            _painting = true;
            _paintingClaims = true;
            _lastPaintedProvince = -1;
            Paint(touch.Position);
            return true;
        }

        var picked = TryPickForPlan(touch.Position, 150, out var pick);
        _touchPlanHasStartPick = picked;
        _touchPlanStartPick = pick;
        if (ObjectivePaintMode)
        {
            _touchPlanDragging = true;
            _borderPainting = true;
            if (picked) BorderStrokeStarted?.Invoke(pick, false, false, false);
        }
        else if (picked)
        {
            BorderHovered?.Invoke(pick);
        }
        return true;
    }

    private bool HandleTouchPlanDrag(InputEventScreenDrag drag)
    {
        if (PlanEraseMode) return true;
        if (PaintMode)
        {
            Paint(drag.Position);
            return true;
        }

        var picked = TryPickForPlan(drag.Position, 35, out var pick);
        if (!ObjectivePaintMode && !_touchPlanDragging)
        {
            if (drag.Position.DistanceTo(_touchPlanStartPos) >= TouchPlanDragThresholdPx)
            {
                _touchPlanDragging = true;
                _borderPainting = true;
                if (_touchPlanHasStartPick) BorderStrokeStarted?.Invoke(_touchPlanStartPick, false, false, false);
                else if (picked) BorderStrokeStarted?.Invoke(pick, false, false, false);
                if (picked) BorderStrokeMoved?.Invoke(pick);
            }
            else if (picked)
            {
                BorderHovered?.Invoke(pick);
            }
            return true;
        }

        if (picked)
        {
            if (!_touchPlanHasStartPick)
            {
                _touchPlanHasStartPick = true;
                _touchPlanStartPick = pick;
                BorderStrokeStarted?.Invoke(pick, false, false, false);
            }
            BorderStrokeMoved?.Invoke(pick);
        }
        return true;
    }

    private bool HandleTouchPlanUp(InputEventScreenTouch touch)
    {
        _touchPlanFingers.Remove(touch.Index);
        if (!_touchPlanActive || touch.Index != _touchPlanFingerId)
            return false;

        _touchPlanActive = false;
        _touchPlanFingerId = -1;

        if (PlanEraseMode)
        {
            if (touch.Position.DistanceTo(_touchPlanStartPos) < TouchPlanDragThresholdPx)
                TryErasePlanAt(touch.Position, TouchPlanEraseRadiusPx);
            return true;
        }

        if (PaintMode)
        {
            _painting = false;
            _lastPaintedProvince = -1;
            return true;
        }

        _borderPainting = false;
        if (!ObjectivePaintMode && !_touchPlanDragging)
        {
            var hasPick = _touchPlanHasStartPick;
            var pick = _touchPlanStartPick;
            if (!hasPick) hasPick = TryPickForPlan(touch.Position, 150, out pick);
            if (hasPick) BorderStrokeStarted?.Invoke(pick, true, false, false);
            BorderStrokeFinished?.Invoke();
        }
        else
        {
            BorderStrokeFinished?.Invoke();
        }
        _touchPlanDragging = false;
        _touchPlanHasStartPick = false;
        return true;
    }

    private void CancelInProgressTouchPlanStroke()
    {
        var hadStroke = _touchPlanActive || _borderPainting || _painting;
        _touchPlanActive = false;
        _touchPlanDragging = false;
        _touchPlanFingerId = -1;
        _touchPlanHasStartPick = false;
        _borderPainting = false;
        _painting = false;
        _lastPaintedProvince = -1;
        if (hadStroke && BorderPaintMode)
            BorderStrokeCanceled?.Invoke();
    }

    /// <summary>
    /// PC mouse input for border fronts, offensive objectives, and fallback lines:
    /// accepts BOTH LMB and RMB (click for whole border on Front Line; drag for border stretch,
    /// offensive objective, or defensive fallback line), while keeping MMB pan and wheel zoom.
    /// </summary>
    private bool HandleBattlePlanMouseInput(InputEvent input)
    {
        if (input is InputEventMouseButton button && button.ButtonIndex is MouseButton.Left or MouseButton.Right)
            return HandleBattlePlanMouseButton(button);

        if (input is InputEventMouseMotion motion && _borderPressed && _borderPainting)
            return HandleBattlePlanMouseMotion(motion);

        if (input is InputEventMouseMotion hover && !_dragging && !_borderPressed && BorderHovered is not null &&
            _campaign is { } hoverCampaign && hoverCampaign.TryEnterRead(0, true))
        {
            GlobePick under = default;
            var picked = false;
            try { picked = TryPick(hover.Position, out under, updateSelection: false); }
            finally { hoverCampaign.ExitRead(); }
            if (picked && under.ProvinceId != _borderHoverProvince)
            {
                _borderHoverProvince = under.ProvinceId;
                BorderHovered.Invoke(under);
                WakeGlobeRendering();
            }
        }

        if (input is InputEventMouseButton middle && middle.ButtonIndex == MouseButton.Middle)
        {
            if (middle.Pressed) BeginCameraDrag(middle.Position, middle.ButtonIndex);
            else _dragging = false;
            AcceptEvent();
            return true;
        }
        return false;
    }

    private bool HandleBattlePlanMouseButton(InputEventMouseButton button)
    {
        if (button.Pressed)
        {
            var picked = TryPickForPlan(button.Position, 150, out var pick);
            _borderPressed = true;
            _borderButton = button.ButtonIndex;
            _borderPressPos = button.Position;
            _borderPressPick = pick;
            _borderPressHasPick = picked;
            _borderPressAlt = button.AltPressed;
            _borderPressShift = button.ShiftPressed;
            _borderPainting = true;
            _borderDragStarted = ObjectivePaintMode;
            if (ObjectivePaintMode && picked)
                BorderStrokeStarted?.Invoke(pick, false, button.AltPressed, button.ShiftPressed);
            WakeGlobeRendering();
        }
        else if (_borderPressed && button.ButtonIndex == _borderButton)
        {
            _borderPressed = false;
            _borderPainting = false;
            if (!ObjectivePaintMode && !_borderDragStarted)
            {
                var hasPick = _borderPressHasPick;
                var pick = _borderPressPick;
                if (!hasPick) hasPick = TryPickForPlan(button.Position, 150, out pick);
                if (hasPick) BorderStrokeStarted?.Invoke(pick, true, _borderPressAlt, _borderPressShift);
                BorderStrokeFinished?.Invoke();
            }
            else
            {
                BorderStrokeFinished?.Invoke();
            }
            _borderDragStarted = false;
            _borderPressHasPick = false;
            WakeGlobeRendering();
        }
        AcceptEvent();
        return true;
    }

    private bool HandleBattlePlanMouseMotion(InputEventMouseMotion motion)
    {
        var picked = TryPickForPlan(motion.Position, 25, out var pick);
        if (!ObjectivePaintMode && !_borderDragStarted)
        {
            if (motion.Position.DistanceTo(_borderPressPos) >= MouseBorderDragThresholdPx)
            {
                _borderDragStarted = true;
                if (_borderPressHasPick)
                    BorderStrokeStarted?.Invoke(_borderPressPick, false, _borderPressAlt, _borderPressShift);
                else if (picked)
                    BorderStrokeStarted?.Invoke(pick, false, _borderPressAlt, _borderPressShift);
                if (picked) BorderStrokeMoved?.Invoke(pick);
                WakeGlobeRendering();
            }
            AcceptEvent();
            return true;
        }

        if (picked)
        {
            if (!_borderPressHasPick)
            {
                _borderPressHasPick = true;
                _borderPressPick = pick;
                BorderStrokeStarted?.Invoke(pick, false, _borderPressAlt, _borderPressShift);
            }
            BorderStrokeMoved?.Invoke(pick);
            WakeGlobeRendering();
        }
        AcceptEvent();
        return true;
    }

    private bool TryPickForPlan(Vector2 pixel, int timeoutMs, out GlobePick pick)
    {
        pick = default;
        if (!TryBeginPriorityActionRead(out var c, timeoutMs))
            return false;
        try { return TryPick(pixel, out pick, updateSelection: false); }
        finally { c.ExitRead(); }
    }

    private void TryErasePlanAt(Vector2 pixel, float hitRadiusPx)
    {
        if (!TryBeginPriorityActionRead(out var eraseCampaign, 150)) return;
        int lineId = -1;
        bool objectiveOnly = false;
        bool hit;
        try { hit = TryPickPlan(pixel, out lineId, out objectiveOnly, hitRadiusPx); }
        finally { eraseCampaign.ExitRead(); }
        if (hit)
        {
            PlanEraseRequested?.Invoke(lineId, objectiveOnly);
            WakeGlobeRendering();
        }
    }
}
