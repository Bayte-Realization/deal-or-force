using System;

namespace DealOrForce.Platform;

/// <summary>Engine-free 2D screen coordinate/vector for deterministic touch gesture math.</summary>
public readonly record struct TouchPoint2D(float X, float Y)
{
    public static TouchPoint2D Zero => default;

    public float LengthSquared => X * X + Y * Y;
    public float Length => MathF.Sqrt(LengthSquared);

    public static TouchPoint2D operator +(TouchPoint2D a, TouchPoint2D b) => new(a.X + b.X, a.Y + b.Y);
    public static TouchPoint2D operator -(TouchPoint2D a, TouchPoint2D b) => new(a.X - b.X, a.Y - b.Y);
    public static TouchPoint2D operator *(TouchPoint2D a, float s) => new(a.X * s, a.Y * s);
    public static TouchPoint2D operator /(TouchPoint2D a, float s) => s != 0f ? new(a.X / s, a.Y / s) : Zero;

    public float DistanceTo(TouchPoint2D other) => (this - other).Length;
    public static TouchPoint2D Midpoint(TouchPoint2D a, TouchPoint2D b) => new((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);

    public TouchPoint2D ClampLength(float maxLength)
    {
        if (maxLength <= 0f) return Zero;
        var lenSq = LengthSquared;
        if (lenSq <= maxLength * maxLength) return this;
        var scale = maxLength / MathF.Sqrt(lenSq);
        return new TouchPoint2D(X * scale, Y * scale);
    }
}

/// <summary>High-level state of the touch gesture recognizer.</summary>
public enum TouchGestureState
{
    Idle = 0,
    PendingTapOrPan = 1,
    Panning = 2,
    Pinching = 3,
    LongPressFired = 4,
    PostPinchLockout = 5,
    BoxSelecting = 6
}

/// <summary>Tunable thresholds for the mobile grand-strategy touch gesture recognizer.</summary>
public readonly record struct TouchGestureConfig(
    float PanDeadzonePx = 12f,
    float LongPressDurationSec = 0.42f,
    float LongPressMaxMovePx = 10f,
    float MaxTapDurationSec = 0.32f,
    float PinchMinSpanPx = 24f,
    float TwoFingerTiltDeadzonePx = 18f,
    float PostPinchGraceSec = 0.08f,
    float InertiaDamping = 8f,
    float InertiaRestCutoffSec = 0.09f,
    float InertiaMinSpeedPxPerSec = 25f,
    float InertiaMaxSpeedPxPerSec = 2400f,
    float PinchSpanStableThreshold = 0.025f,
    float TwoFingerRotateDeadzoneRad = 0.22f)
{
    public TouchGestureConfig()
        : this(12f, 0.42f, 10f, 0.32f, 24f, 18f, 0.08f, 8f, 0.09f, 25f, 2400f, 0.025f, 0.22f)
    {
    }

    public static TouchGestureConfig Default => new();

    /// <summary>Build a config scaled cleanly to the active <see cref="PlatformProfile"/>.</summary>
    public static TouchGestureConfig ForProfile(PlatformProfile profile)
    {
        if (profile is null) return Default;
        var scale = Math.Clamp(profile.UiScaleFactor, 1.0f, 1.6f);
        return Default with
        {
            PanDeadzonePx = 15f * scale,
            MaxTapDurationSec = 0.42f,
            LongPressMaxMovePx = 10f * scale,
            PinchMinSpanPx = 24f * scale
        };
    }
}

/// <summary>Per-event or per-frame output emitted by <see cref="TouchGestureMachine"/>.</summary>
public readonly record struct TouchGestureDelta(
    TouchPoint2D PanDeltaPx,
    float ZoomScaleFactor,
    TouchPoint2D ZoomAnchorPx,
    float RotateDeltaRadians,
    float TiltDeltaPx,
    bool TapTriggered,
    TouchPoint2D TapPositionPx,
    bool LongPressTriggered,
    TouchPoint2D LongPressPositionPx,
    bool BoxSelectUpdated = false,
    bool BoxSelectCompleted = false,
    TouchScreenRect BoxSelectRect = default)
{
    public static TouchGestureDelta None => new(
        PanDeltaPx: TouchPoint2D.Zero,
        ZoomScaleFactor: 1f,
        ZoomAnchorPx: TouchPoint2D.Zero,
        RotateDeltaRadians: 0f,
        TiltDeltaPx: 0f,
        TapTriggered: false,
        TapPositionPx: TouchPoint2D.Zero,
        LongPressTriggered: false,
        LongPressPositionPx: TouchPoint2D.Zero,
        BoxSelectUpdated: false,
        BoxSelectCompleted: false,
        BoxSelectRect: TouchScreenRect.Empty);

    public bool HasAnyAction =>
        PanDeltaPx.LengthSquared > 1e-6f ||
        MathF.Abs(ZoomScaleFactor - 1f) > 1e-5f ||
        MathF.Abs(RotateDeltaRadians) > 1e-5f ||
        MathF.Abs(TiltDeltaPx) > 1e-5f ||
        TapTriggered ||
        LongPressTriggered ||
        BoxSelectUpdated ||
        BoxSelectCompleted;
}

/// <summary>
/// Pure, deterministic C# multi-touch gesture state machine for Deal or Force's 3D globe and flat map.
/// Supports:
/// - 1-finger pan with deadzone + exponential velocity inertia coast
/// - 2-finger pinch-to-zoom anchored on screen midpoint
/// - 2-finger twist rotation + parallel vertical tilt for 3D globe control
/// - Tap vs drag disambiguation
/// - Long-press-as-right-click (for army orders / diplomacy context menu)
/// - Post-pinch release grace window to prevent accidental clicks or jumps when lifting fingers
/// </summary>
public sealed class TouchGestureMachine
{
    private const int NoFinger = -1;

    private readonly TouchGestureConfig _config;
    private readonly TouchTapModeController _modeController = new();

    private int _fingerA = NoFinger;
    private int _fingerB = NoFinger;
    private TouchPoint2D _posA;
    private TouchPoint2D _posB;
    private TouchPoint2D _startA;
    private double _pressTimeSec;
    private double _lastMoveTimeSec;
    private float _maxTravelFromStartPx;

    private float _prevPinchSpanPx;
    private float _pinchRefSpanPx;
    private float _prevPinchAngleRad;
    private TouchPoint2D _prevPinchMidpoint;
    private float _pinchAngleAccumulatorRad;
    private bool _pinchRotateUnlocked;
    private float _tiltAccumulatorPx;
    private bool _pinchTiltUnlocked;

    private TouchPoint2D _panVelocityPxPerSec;
    private TouchPoint2D _inertiaVelocityPxPerSec;

    public TouchGestureState State { get; private set; } = TouchGestureState.Idle;
    public TouchGestureConfig Config => _config;
    public TouchTapMode ActiveMode => _modeController.ActiveMode;
    public bool OneShotOrderMode
    {
        get => _modeController.OneShotOrderMode;
        set => _modeController.OneShotOrderMode = value;
    }
    public bool IsStickyOrder => _modeController.IsStickyOrder;
    public int ActiveFingerCount => (_fingerA != NoFinger ? 1 : 0) + (_fingerB != NoFinger ? 1 : 0);
    public TouchPoint2D InertiaVelocityPxPerSec => _inertiaVelocityPxPerSec;

    public TouchGestureMachine(TouchGestureConfig? config = null)
    {
        _config = config ?? TouchGestureConfig.Default;
    }

    public void SetTapMode(TouchTapMode mode, bool? oneShotOrder = null)
    {
        if (oneShotOrder.HasValue) _modeController.OneShotOrderMode = oneShotOrder.Value;
        _modeController.SetMode(mode);
        if (State == TouchGestureState.BoxSelecting && mode != TouchTapMode.BoxSelect)
        {
            CancelAll();
        }
    }

    public void PressOrderModeButton() => _modeController.PressOrderButton();

    public TouchGestureDelta OnTouchDown(int fingerId, float x, float y, double nowSec)
    {
        if (fingerId < 0 || !float.IsFinite(x) || !float.IsFinite(y))
            return TouchGestureDelta.None;

        var point = new TouchPoint2D(x, y);

        // Any fresh finger press halts inertial coasting immediately.
        _inertiaVelocityPxPerSec = TouchPoint2D.Zero;

        if (_fingerA == NoFinger)
        {
            _fingerA = fingerId;
            _posA = point;
            _startA = point;
            _pressTimeSec = nowSec;
            _lastMoveTimeSec = nowSec;
            _maxTravelFromStartPx = 0f;
            _panVelocityPxPerSec = TouchPoint2D.Zero;
            State = TouchGestureState.PendingTapOrPan;
            return TouchGestureDelta.None;
        }

        if (_fingerA == fingerId)
        {
            _posA = point;
            return TouchGestureDelta.None;
        }

        if (_fingerB == NoFinger)
        {
            _fingerB = fingerId;
            _posB = point;
            BeginPinchTracking(nowSec);
            return TouchGestureDelta.None;
        }

        // Ignore 3rd+ fingers cleanly without perturbing the active primary gesture.
        return TouchGestureDelta.None;
    }

    public TouchGestureDelta OnTouchMove(int fingerId, float x, float y, double nowSec)
    {
        if (fingerId < 0 || !float.IsFinite(x) || !float.IsFinite(y))
            return TouchGestureDelta.None;

        var point = new TouchPoint2D(x, y);

        if (State == TouchGestureState.Pinching)
        {
            if (fingerId != _fingerA && fingerId != _fingerB)
                return TouchGestureDelta.None;

            var prevA = _posA;
            var prevB = _posB;
            if (fingerId == _fingerA) _posA = point;
            else _posB = point;

            return ComputePinchDelta(prevA, prevB, nowSec);
        }

        if (fingerId != _fingerA)
            return TouchGestureDelta.None;

        var prev = _posA;
        _posA = point;
        var step = point - prev;
        var distFromStart = point.DistanceTo(_startA);
        if (distFromStart > _maxTravelFromStartPx)
            _maxTravelFromStartPx = distFromStart;

        if (State == TouchGestureState.LongPressFired)
        {
            _lastMoveTimeSec = nowSec;
            return TouchGestureDelta.None;
        }

        if (State == TouchGestureState.PostPinchLockout)
        {
            // Stay locked out of pan/tap/box-select until ALL fingers lift (ActiveFingerCount == 0).
            _startA = point;
            _lastMoveTimeSec = nowSec;
            return TouchGestureDelta.None;
        }

        if (State == TouchGestureState.PendingTapOrPan)
        {
            if (distFromStart < _config.PanDeadzonePx)
            {
                _lastMoveTimeSec = nowSec;
                return TouchGestureDelta.None;
            }
            if (_modeController.ActiveMode == TouchTapMode.BoxSelect)
            {
                State = TouchGestureState.BoxSelecting;
                _lastMoveTimeSec = nowSec;
                return TouchGestureDelta.None with
                {
                    BoxSelectUpdated = true,
                    BoxSelectRect = TouchScreenRect.FromCorners(_startA, point)
                };
            }
            State = TouchGestureState.Panning;
            var initialPan = point - _startA;
            UpdatePanVelocity(initialPan, nowSec);
            return TouchGestureDelta.None with { PanDeltaPx = initialPan };
        }

        if (State == TouchGestureState.BoxSelecting)
        {
            _lastMoveTimeSec = nowSec;
            return TouchGestureDelta.None with
            {
                BoxSelectUpdated = true,
                BoxSelectRect = TouchScreenRect.FromCorners(_startA, point)
            };
        }

        if (State == TouchGestureState.Panning)
        {
            UpdatePanVelocity(step, nowSec);
            return TouchGestureDelta.None with { PanDeltaPx = step };
        }

        return TouchGestureDelta.None;
    }

    public TouchGestureDelta OnTouchUp(int fingerId, float x, float y, double nowSec)
    {
        if (fingerId < 0) return TouchGestureDelta.None;

        var point = float.IsFinite(x) && float.IsFinite(y)
            ? new TouchPoint2D(x, y)
            : (fingerId == _fingerA ? _posA : _posB);

        if (fingerId != _fingerA && fingerId != _fingerB)
            return TouchGestureDelta.None;

        if (State == TouchGestureState.Pinching)
        {
            // One of the two pinch fingers lifted: promote survivor to _fingerA and lock out pan/tap
            // until ALL fingers lift (prevents residual 1-finger pan jumps after a pinch).
            if (fingerId == _fingerA)
            {
                _fingerA = _fingerB;
                _posA = _posB;
            }
            _fingerB = NoFinger;
            _startA = _posA;
            _maxTravelFromStartPx = 0f;
            _panVelocityPxPerSec = TouchPoint2D.Zero;
            _inertiaVelocityPxPerSec = TouchPoint2D.Zero;
            _lastMoveTimeSec = nowSec;
            State = _fingerA == NoFinger ? TouchGestureState.Idle : TouchGestureState.PostPinchLockout;
            return TouchGestureDelta.None;
        }

        if (fingerId != _fingerA)
            return TouchGestureDelta.None;

        var releaseState = State;
        var heldDuration = nowSec - _pressTimeSec;
        var releaseDrift = point.DistanceTo(_startA);
        var maxDrift = MathF.Max(_maxTravelFromStartPx, releaseDrift);
        var restSinceMove = nowSec - _lastMoveTimeSec;
        var velocity = _panVelocityPxPerSec;

        _fingerA = NoFinger;
        _fingerB = NoFinger;
        _panVelocityPxPerSec = TouchPoint2D.Zero;
        State = TouchGestureState.Idle;

        if (releaseState == TouchGestureState.PendingTapOrPan)
        {
            var isQuickTap = heldDuration <= _config.MaxTapDurationSec &&
                             maxDrift < _config.PanDeadzonePx;
            if (!isQuickTap)
                return TouchGestureDelta.None;

            if (_modeController.ActiveMode == TouchTapMode.Pan)
            {
                // Safe map-navigation lock: accidental taps never select or issue orders.
                return TouchGestureDelta.None;
            }

            if (_modeController.ActiveMode == TouchTapMode.Order)
            {
                // Immediate RMB/context order on single tap; auto-returns to Select if OneShotOrderMode is enabled.
                _modeController.NotifyOrderDispatched();
                return TouchGestureDelta.None with
                {
                    LongPressTriggered = true,
                    LongPressPositionPx = point
                };
            }

            return TouchGestureDelta.None with
            {
                TapTriggered = true,
                TapPositionPx = point
            };
        }

        if (releaseState == TouchGestureState.BoxSelecting)
        {
            return TouchGestureDelta.None with
            {
                BoxSelectCompleted = true,
                BoxSelectRect = TouchScreenRect.FromCorners(_startA, point)
            };
        }

        if (releaseState == TouchGestureState.Panning)
        {
            if (restSinceMove <= _config.InertiaRestCutoffSec &&
                velocity.Length >= _config.InertiaMinSpeedPxPerSec)
            {
                _inertiaVelocityPxPerSec = velocity.ClampLength(_config.InertiaMaxSpeedPxPerSec);
            }
            return TouchGestureDelta.None;
        }

        // LongPressFired and PostPinchLockout release silently without tap, box-complete, or inertia.
        return TouchGestureDelta.None;
    }

    /// <summary>
    /// Cancel a single finger reported as <c>Canceled</c> by Android/OS gesture interruption
    /// without firing tap, long-press, box-select completion, or pan inertia.
    /// </summary>
    public TouchGestureDelta OnTouchCancel(int fingerId, double nowSec)
    {
        if (fingerId < 0 || (fingerId != _fingerA && fingerId != _fingerB))
            return TouchGestureDelta.None;

        _panVelocityPxPerSec = TouchPoint2D.Zero;
        _inertiaVelocityPxPerSec = TouchPoint2D.Zero;
        _lastMoveTimeSec = nowSec;

        if (State == TouchGestureState.Pinching)
        {
            if (fingerId == _fingerA)
            {
                _fingerA = _fingerB;
                _posA = _posB;
            }
            _fingerB = NoFinger;
            _startA = _posA;
            _maxTravelFromStartPx = 0f;
            State = _fingerA == NoFinger ? TouchGestureState.Idle : TouchGestureState.PostPinchLockout;
            return TouchGestureDelta.None;
        }

        _fingerA = NoFinger;
        _fingerB = NoFinger;
        _maxTravelFromStartPx = 0f;
        State = TouchGestureState.Idle;
        return TouchGestureDelta.None;
    }

    public TouchGestureDelta OnTick(double nowSec, float dtSec)
    {
        var clampedDt = float.IsFinite(dtSec) ? Math.Clamp(dtSec, 0f, 0.1f) : 0f;

        if (State == TouchGestureState.PendingTapOrPan && _fingerA != NoFinger)
        {
            if (_modeController.ActiveMode == TouchTapMode.Pan)
                return TouchGestureDelta.None;

            var held = nowSec - _pressTimeSec;
            if (held >= _config.LongPressDurationSec &&
                _maxTravelFromStartPx <= _config.LongPressMaxMovePx)
            {
                State = TouchGestureState.LongPressFired;
                _inertiaVelocityPxPerSec = TouchPoint2D.Zero;
                return TouchGestureDelta.None with
                {
                    LongPressTriggered = true,
                    LongPressPositionPx = _posA
                };
            }
            return TouchGestureDelta.None;
        }

        if (State == TouchGestureState.Idle &&
            clampedDt > 0f &&
            _inertiaVelocityPxPerSec.Length >= _config.InertiaMinSpeedPxPerSec)
        {
            var damping = MathF.Max(0.1f, _config.InertiaDamping);
            var decay = MathF.Exp(-damping * clampedDt);
            var remaining = _inertiaVelocityPxPerSec * decay;
            var travel = (_inertiaVelocityPxPerSec - remaining) / damping;
            _inertiaVelocityPxPerSec = remaining.Length < _config.InertiaMinSpeedPxPerSec
                ? TouchPoint2D.Zero
                : remaining;
            return TouchGestureDelta.None with { PanDeltaPx = travel };
        }

        return TouchGestureDelta.None;
    }

    /// <summary>Reset all active fingers, gesture state, and inertial velocity (e.g. focus loss or modal open).</summary>
    public void CancelAll()
    {
        _fingerA = NoFinger;
        _fingerB = NoFinger;
        _posA = TouchPoint2D.Zero;
        _posB = TouchPoint2D.Zero;
        _startA = TouchPoint2D.Zero;
        _maxTravelFromStartPx = 0f;
        _prevPinchSpanPx = 0f;
        _pinchRefSpanPx = 0f;
        _prevPinchAngleRad = 0f;
        _prevPinchMidpoint = TouchPoint2D.Zero;
        _pinchAngleAccumulatorRad = 0f;
        _pinchRotateUnlocked = false;
        _tiltAccumulatorPx = 0f;
        _pinchTiltUnlocked = false;
        _panVelocityPxPerSec = TouchPoint2D.Zero;
        _inertiaVelocityPxPerSec = TouchPoint2D.Zero;
        State = TouchGestureState.Idle;
    }

    private void BeginPinchTracking(double nowSec)
    {
        State = TouchGestureState.Pinching;
        _panVelocityPxPerSec = TouchPoint2D.Zero;
        _inertiaVelocityPxPerSec = TouchPoint2D.Zero;
        _prevPinchSpanPx = _posA.DistanceTo(_posB);
        _pinchRefSpanPx = _prevPinchSpanPx;
        _prevPinchMidpoint = TouchPoint2D.Midpoint(_posA, _posB);
        var diff = _posB - _posA;
        _prevPinchAngleRad = MathF.Atan2(diff.Y, diff.X);
        _pinchAngleAccumulatorRad = 0f;
        _pinchRotateUnlocked = false;
        _tiltAccumulatorPx = 0f;
        _pinchTiltUnlocked = false;
        _lastMoveTimeSec = nowSec;
    }

    private TouchGestureDelta ComputePinchDelta(TouchPoint2D prevA, TouchPoint2D prevB, double nowSec)
    {
        var currentSpan = _posA.DistanceTo(_posB);
        var midpoint = TouchPoint2D.Midpoint(_posA, _posB);
        var diff = _posB - _posA;
        var currentAngle = MathF.Atan2(diff.Y, diff.X);

        var zoomScale = 1f;
        var rawAngleDelta = 0f;
        if (_prevPinchSpanPx >= _config.PinchMinSpanPx && currentSpan >= _config.PinchMinSpanPx)
        {
            zoomScale = Math.Clamp(currentSpan / _prevPinchSpanPx, 0.5f, 2.0f);
            rawAngleDelta = WrapAngleRadians(currentAngle - _prevPinchAngleRad);
        }

        // Strict pinch anti-drift: during an active zoom (per-frame >= 2.5% OR cumulative >= 3%
        // from the last reset span), suppress twist rotation and vertical tilt completely so smooth
        // 60-120 Hz pinches with natural thumb arc never drift the globe sideways.
        var midDelta = midpoint - _prevPinchMidpoint;
        var dA = _posA - prevA;
        var dB = _posB - prevB;
        if (_pinchRefSpanPx < _config.PinchMinSpanPx && currentSpan >= _config.PinchMinSpanPx)
            _pinchRefSpanPx = currentSpan;
        var spanRatioDelta = MathF.Abs(zoomScale - 1f);
        var cumulativeSpanMoved = _pinchRefSpanPx >= _config.PinchMinSpanPx &&
            MathF.Abs(currentSpan / _pinchRefSpanPx - 1f) >= 0.03f;
        var spanStable = spanRatioDelta < _config.PinchSpanStableThreshold && !cumulativeSpanMoved;

        var rotateDelta = 0f;
        var tiltDelta = 0f;
        if (!spanStable)
        {
            _pinchRefSpanPx = currentSpan;
            _pinchAngleAccumulatorRad = 0f;
            _pinchRotateUnlocked = false;
            _tiltAccumulatorPx = 0f;
            _pinchTiltUnlocked = false;
        }
        else
        {
            if (MathF.Abs(rawAngleDelta) > 1e-5f)
            {
                _pinchAngleAccumulatorRad += rawAngleDelta;
                if (_pinchRotateUnlocked)
                {
                    rotateDelta = rawAngleDelta;
                }
                else if (MathF.Abs(_pinchAngleAccumulatorRad) >= _config.TwoFingerRotateDeadzoneRad)
                {
                    _pinchRotateUnlocked = true;
                    rotateDelta = _pinchAngleAccumulatorRad;
                }
            }

            var parallelVertical =
                (MathF.Abs(dA.Y) > 0f || MathF.Abs(dB.Y) > 0f) &&
                (dA.Y * dB.Y >= 0f) &&
                MathF.Abs(midDelta.Y) >= MathF.Abs(midDelta.X);
            if (parallelVertical)
            {
                _tiltAccumulatorPx += midDelta.Y;
                if (_pinchTiltUnlocked)
                {
                    tiltDelta = midDelta.Y;
                }
                else if (MathF.Abs(_tiltAccumulatorPx) >= _config.TwoFingerTiltDeadzonePx)
                {
                    _pinchTiltUnlocked = true;
                    tiltDelta = _tiltAccumulatorPx;
                }
            }
        }

        _prevPinchSpanPx = currentSpan;
        _prevPinchAngleRad = currentAngle;
        _prevPinchMidpoint = midpoint;
        _lastMoveTimeSec = nowSec;

        // 2-finger pinch NEVER pans the camera in any mode (including BoxSelect).
        return TouchGestureDelta.None with
        {
            PanDeltaPx = TouchPoint2D.Zero,
            ZoomScaleFactor = zoomScale,
            ZoomAnchorPx = midpoint,
            RotateDeltaRadians = rotateDelta,
            TiltDeltaPx = tiltDelta
        };
    }

    private void UpdatePanVelocity(TouchPoint2D step, double nowSec)
    {
        var dt = (float)Math.Clamp(nowSec - _lastMoveTimeSec, 0.004, 0.1);
        _lastMoveTimeSec = nowSec;
        var instantaneous = (step / dt).ClampLength(_config.InertiaMaxSpeedPxPerSec);
        var blend = 1f - MathF.Exp(-dt * 24f);
        _panVelocityPxPerSec = new TouchPoint2D(
            _panVelocityPxPerSec.X + (instantaneous.X - _panVelocityPxPerSec.X) * blend,
            _panVelocityPxPerSec.Y + (instantaneous.Y - _panVelocityPxPerSec.Y) * blend);
    }

    /// <summary>Normalize an angle delta into (-pi, +pi].</summary>
    public static float WrapAngleRadians(float radians)
    {
        if (!float.IsFinite(radians)) return 0f;
        const float twoPi = MathF.PI * 2f;
        var wrapped = (radians + MathF.PI) % twoPi;
        if (wrapped < 0f) wrapped += twoPi;
        return wrapped - MathF.PI;
    }
}
