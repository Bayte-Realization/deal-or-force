using System;
using Godot;

namespace DealOrForce.Platform;

/// <summary>
/// Clean Godot <see cref="InputEventScreenTouch"/> / <see cref="InputEventScreenDrag"/> adapter
/// that routes multi-touch gestures through <see cref="TouchGestureMachine"/> into camera/map
/// callbacks while having strictly zero impact on desktop mouse and keyboard input.
///
/// Integration into <c>GlobePresentation</c> requires only:
/// <code>
///   if (_touchAdapter.TryHandleInput(inputEvent)) { AcceptEvent(); return; } // in _GuiInput
///   _touchAdapter.Tick(delta);                                               // in _Process
/// </code>
/// </summary>
public sealed class TouchCameraAdapter
{
    private readonly TouchGestureMachine _machine;
    private readonly float _pinchZoomSensitivity;

    /// <summary>1-finger drag or inertial coast delta in viewport pixels.</summary>
    public event Action<Vector2> PanRequested;

    /// <summary>
    /// 2-finger pinch zoom anchored at midpoint.
    /// Parameters: <c>(anchorPx, zoomDirection, rawScaleFactor)</c> where negative <c>zoomDirection</c>
    /// zooms IN (matching <c>GlobePresentation.ZoomGlobeAt</c> / <c>MouseButton.WheelUp</c>).
    /// </summary>
    public event Action<Vector2, float, float> ZoomRequested;

    /// <summary>2-finger 3D globe tilt (pixels) and twist rotation (radians).</summary>
    public event Action<float, float> TiltRotateRequested;

    /// <summary>Short single-finger tap mapped to primary selection (LMB click equivalent).</summary>
    public event Action<Vector2> TapRequested;

    /// <summary>Long-press (or single tap in <see cref="TouchTapMode.Order"/>) mapped to RMB order/context action.</summary>
    public event Action<Vector2> LongPressContextRequested;

    /// <summary>Live marquee selection rectangle emitted during 1-finger drag in <see cref="TouchTapMode.BoxSelect"/>.</summary>
    public event Action<Rect2> BoxSelectUpdated;

    /// <summary>Final marquee selection rectangle emitted when releasing 1-finger drag in <see cref="TouchTapMode.BoxSelect"/>.</summary>
    public event Action<Rect2> BoxSelectCompleted;

    /// <summary>Emitted when an active marquee selection is cancelled (e.g. 2nd finger pinch, OS cancel, mode switch, modal block).</summary>
    public event Action BoxSelectCanceled;

    /// <summary>Emitted whenever the active tap mode changes manually or auto-returns from one-shot Order to Select.</summary>
    public event Action<TouchTapMode> ModeChanged;

    public TouchGestureState CurrentState => _machine.State;
    public TouchTapMode ActiveMode => _machine.ActiveMode;
    public bool OneShotOrderMode
    {
        get => _machine.OneShotOrderMode;
        set => _machine.OneShotOrderMode = value;
    }
    public bool IsStickyOrder => _machine.IsStickyOrder;
    public int ActiveFingerCount => _machine.ActiveFingerCount;
    public bool IsTouchActive => _machine.ActiveFingerCount > 0;

    public TouchCameraAdapter(PlatformProfile profile = null, float pinchZoomSensitivity = 4.2f)
    {
        var active = profile ?? PlatformProfile.Current;
        _machine = new TouchGestureMachine(TouchGestureConfig.ForProfile(active));
        _pinchZoomSensitivity = Math.Clamp(pinchZoomSensitivity, 0.5f, 12f);
    }

    public void SetTapMode(TouchTapMode mode, bool? oneShotOrder = null)
    {
        var prev = _machine.ActiveMode;
        var wasBox = _machine.State == TouchGestureState.BoxSelecting;
        _machine.SetTapMode(mode, oneShotOrder);
        if (wasBox && _machine.State != TouchGestureState.BoxSelecting)
            BoxSelectCanceled?.Invoke();
        if (_machine.ActiveMode != prev) ModeChanged?.Invoke(_machine.ActiveMode);
    }

    public void PressOrderModeButton()
    {
        var prev = _machine.ActiveMode;
        var prevSticky = _machine.IsStickyOrder;
        _machine.PressOrderModeButton();
        if (_machine.ActiveMode != prev || _machine.IsStickyOrder != prevSticky)
            ModeChanged?.Invoke(_machine.ActiveMode);
    }

    /// <summary>
    /// Inspect an incoming Godot <see cref="InputEvent"/>.
    /// Immediately returns <c>false</c> for all non-touch events (mouse, keyboard, joypad),
    /// guaranteeing zero interference with desktop input paths.
    /// </summary>
    public bool TryHandleInput(InputEvent inputEvent)
    {
        if (inputEvent is null) return false;

        var nowSec = Time.GetTicksUsec() / 1_000_000.0;
        var modeBefore = _machine.ActiveMode;
        var wasBox = _machine.State == TouchGestureState.BoxSelecting;

        if (inputEvent is InputEventScreenTouch touch)
        {
            var delta = touch.Canceled
                ? _machine.OnTouchCancel(touch.Index, nowSec)
                : touch.Pressed
                    ? _machine.OnTouchDown(touch.Index, touch.Position.X, touch.Position.Y, nowSec)
                    : _machine.OnTouchUp(touch.Index, touch.Position.X, touch.Position.Y, nowSec);
            if (wasBox && _machine.State != TouchGestureState.BoxSelecting && !delta.BoxSelectCompleted)
                BoxSelectCanceled?.Invoke();
            Dispatch(delta);
            if (_machine.ActiveMode != modeBefore) ModeChanged?.Invoke(_machine.ActiveMode);
            return true;
        }

        if (inputEvent is InputEventScreenDrag drag)
        {
            var delta = _machine.OnTouchMove(drag.Index, drag.Position.X, drag.Position.Y, nowSec);
            if (wasBox && _machine.State != TouchGestureState.BoxSelecting && !delta.BoxSelectCompleted)
                BoxSelectCanceled?.Invoke();
            Dispatch(delta);
            if (_machine.ActiveMode != modeBefore) ModeChanged?.Invoke(_machine.ActiveMode);
            return true;
        }

        return false;
    }

    /// <summary>Advance long-press timer and inertial coasting; call once per frame from <c>_Process</c>.</summary>
    public void Tick(double deltaSec)
    {
        if (_machine.State == TouchGestureState.Idle &&
            _machine.InertiaVelocityPxPerSec.LengthSquared <= 1e-4f)
        {
            return;
        }

        var nowSec = Time.GetTicksUsec() / 1_000_000.0;
        var delta = _machine.OnTick(nowSec, (float)deltaSec);
        Dispatch(delta);
    }

    /// <summary>Cancel all in-flight touch gestures and inertia (call when modals open or input is blocked).</summary>
    public void Cancel()
    {
        var wasBox = _machine.State == TouchGestureState.BoxSelecting;
        _machine.CancelAll();
        if (wasBox) BoxSelectCanceled?.Invoke();
    }

    private void Dispatch(TouchGestureDelta delta)
    {
        if (!delta.HasAnyAction) return;

        if (delta.PanDeltaPx.LengthSquared > 1e-6f)
        {
            PanRequested?.Invoke(new Vector2(delta.PanDeltaPx.X, delta.PanDeltaPx.Y));
        }

        if (MathF.Abs(delta.ZoomScaleFactor - 1f) > 1e-5f)
        {
            // Pinch-out (scale > 1) -> negative direction (zoom in), matching WheelUp in GlobePresentation.
            var direction = -MathF.Log(delta.ZoomScaleFactor) * _pinchZoomSensitivity;
            ZoomRequested?.Invoke(
                new Vector2(delta.ZoomAnchorPx.X, delta.ZoomAnchorPx.Y),
                direction,
                delta.ZoomScaleFactor);
        }

        if (MathF.Abs(delta.TiltDeltaPx) > 1e-5f || MathF.Abs(delta.RotateDeltaRadians) > 1e-5f)
        {
            TiltRotateRequested?.Invoke(delta.TiltDeltaPx, delta.RotateDeltaRadians);
        }

        if (delta.TapTriggered)
        {
            TapRequested?.Invoke(new Vector2(delta.TapPositionPx.X, delta.TapPositionPx.Y));
        }

        if (delta.LongPressTriggered)
        {
            LongPressContextRequested?.Invoke(new Vector2(delta.LongPressPositionPx.X, delta.LongPressPositionPx.Y));
        }

        if (delta.BoxSelectUpdated)
        {
            var r = delta.BoxSelectRect;
            BoxSelectUpdated?.Invoke(new Rect2(r.X, r.Y, r.Width, r.Height));
        }

        if (delta.BoxSelectCompleted)
        {
            var r = delta.BoxSelectRect;
            BoxSelectCompleted?.Invoke(new Rect2(r.X, r.Y, r.Width, r.Height));
        }
    }
}
