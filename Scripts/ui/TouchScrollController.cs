using System;
using Godot;

namespace DealOrForce;

/// <summary>
/// Global touch-drag scroll controller for Godot 4 <see cref="ScrollContainer"/>s.
///
/// On Android, child <see cref="Button"/> and <see cref="PanelContainer"/> nodes (whose default
/// <see cref="Control.MouseFilterEnum"/> is <c>Stop</c>) swallow <see cref="InputEventScreenDrag"/>
/// before it can bubble to the enclosing <see cref="ScrollContainer"/>. This lightweight controller
/// intercepts single-finger drags that start inside any visible scrollable <see cref="ScrollContainer"/>,
/// scrolls vertical and/or horizontal axes with sub-pixel accumulation and gentle inertial coasting,
/// cancels any pressed child <see cref="BaseButton"/> once the drag threshold is crossed so scrolling
/// never triggers accidental clicks, and swallows synthetic touch-emulated mouse events during and
/// immediately after the drag so the globe camera underneath never moves.
/// </summary>
public sealed partial class TouchScrollController : Node
{
    internal const float DragThresholdPx = 12f;
    private const float InertiaMinVelocityPxPerSec = 40f;
    private const float InertiaDecayRate = 7.5f;
    private const ulong SyntheticMouseSuppressUsec = 200_000UL;

    private int _activeFinger = -1;
    private ScrollContainer _targetScroll;
    private BaseButton _pressedButton;
    private Vector2 _startPos;
    private Vector2 _lastPos;
    private ulong _lastDragUsec;
    private bool _isDragging;
    private bool _canScrollV;
    private bool _canScrollH;
    private float _accumV;
    private float _accumH;
    private Vector2 _velocity;

    private ScrollContainer _inertiaScroll;
    private bool _inertiaCanV;
    private bool _inertiaCanH;
    private Vector2 _inertiaVelocity;
    private float _inertiaAccumV;
    private float _inertiaAccumH;
    private ulong _suppressSyntheticMouseUntilUsec;

    public static TouchScrollController EnsureInstalled(Node host)
    {
        if (host is null || !GodotObject.IsInstanceValid(host)) return null;
        if (host.GetNodeOrNull<TouchScrollController>("TouchScrollController") is { } existing &&
            GodotObject.IsInstanceValid(existing))
            return existing;
        var controller = new TouchScrollController
        {
            Name = "TouchScrollController",
            ProcessMode = ProcessModeEnum.Always
        };
        host.AddChild(controller);
        return controller;
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (inputEvent is InputEventScreenTouch touch)
        {
            HandleScreenTouch(touch);
            return;
        }
        if (inputEvent is InputEventScreenDrag drag)
        {
            HandleScreenDrag(drag);
            return;
        }
        if (inputEvent is InputEventMouse mouse && mouse.Device == -1)
        {
            if (_isDragging || Time.GetTicksUsec() < _suppressSyntheticMouseUntilUsec)
                GetViewport()?.SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (_activeFinger >= 0 || _inertiaScroll is null) return;
        if (!IsInstanceValid(_inertiaScroll) || !_inertiaScroll.IsVisibleInTree())
        {
            StopInertia();
            return;
        }
        if (_inertiaVelocity.Length() < InertiaMinVelocityPxPerSec)
        {
            StopInertia();
            return;
        }
        var dt = (float)Math.Clamp(delta, 0.001, 0.05);
        var moved = ApplyScrollDelta(_inertiaScroll, _inertiaCanV, _inertiaCanH,
            _inertiaVelocity.X * dt, _inertiaVelocity.Y * dt, ref _inertiaAccumH, ref _inertiaAccumV);
        var decay = MathF.Exp(-InertiaDecayRate * dt);
        _inertiaVelocity *= decay;
        if (!moved && _inertiaVelocity.Length() < 120f)
            StopInertia();
    }

    private void HandleScreenTouch(InputEventScreenTouch touch)
    {
        if (touch.Pressed)
        {
            // Second finger down cancels active scroll so multi-touch/pinch never gets stuck.
            if (_activeFinger >= 0 && _activeFinger != touch.Index)
            {
                ResetActiveTracking();
                return;
            }
            StopInertia();
            var root = GetParent() as Control ?? GetTree()?.Root as Node;
            var hit = HitTestTopControl(root, touch.Position);
            if (hit is null || IsDirectManipulationControl(hit))
            {
                ResetActiveTracking();
                return;
            }
            var scroll = FindScrollableAncestor(hit, out var canV, out var canH);
            if (scroll is null)
            {
                ResetActiveTracking();
                return;
            }
            _activeFinger = touch.Index;
            _targetScroll = scroll;
            _pressedButton = FindButtonAncestorInside(hit, scroll);
            _canScrollV = canV;
            _canScrollH = canH;
            _startPos = touch.Position;
            _lastPos = touch.Position;
            _lastDragUsec = Time.GetTicksUsec();
            _isDragging = false;
            _accumV = 0f;
            _accumH = 0f;
            _velocity = Vector2.Zero;
            return;
        }

        if (touch.Index != _activeFinger) return;
        var wasDragging = _isDragging;
        var scrollRef = _targetScroll;
        var canVRef = _canScrollV;
        var canHRef = _canScrollH;
        var velRef = _velocity;
        ResetActiveTracking();

        if (wasDragging)
        {
            GetViewport()?.SetInputAsHandled();
            _suppressSyntheticMouseUntilUsec = Time.GetTicksUsec() + SyntheticMouseSuppressUsec;
            if (!touch.Canceled && scrollRef is not null && IsInstanceValid(scrollRef) &&
                velRef.Length() >= InertiaMinVelocityPxPerSec)
            {
                _inertiaScroll = scrollRef;
                _inertiaCanV = canVRef;
                _inertiaCanH = canHRef;
                _inertiaVelocity = velRef;
                _inertiaAccumV = 0f;
                _inertiaAccumH = 0f;
            }
        }
    }

    private void HandleScreenDrag(InputEventScreenDrag drag)
    {
        if (drag.Index != _activeFinger) return;
        if (_targetScroll is null || !IsInstanceValid(_targetScroll) || !_targetScroll.IsVisibleInTree())
        {
            ResetActiveTracking();
            return;
        }

        var nowUsec = Time.GetTicksUsec();
        var step = drag.Position - _lastPos;
        var total = drag.Position - _startPos;

        if (!_isDragging)
        {
            if (!ShouldStartDrag(total.X, total.Y, _canScrollV, _canScrollH, DragThresholdPx))
            {
                _lastPos = drag.Position;
                _lastDragUsec = nowUsec;
                return;
            }
            _isDragging = true;
            CancelPressedButton(_pressedButton);
            _pressedButton = null;
            ApplyScrollDelta(_targetScroll, _canScrollV, _canScrollH, total.X, total.Y, ref _accumH, ref _accumV);
        }
        else
        {
            ApplyScrollDelta(_targetScroll, _canScrollV, _canScrollH, step.X, step.Y, ref _accumH, ref _accumV);
        }

        var dtSec = Math.Clamp((nowUsec - _lastDragUsec) / 1_000_000f, 0.004f, 0.1f);
        var instVel = new Vector2(_canScrollH ? step.X / dtSec : 0f, _canScrollV ? step.Y / dtSec : 0f);
        _velocity = _velocity.Lerp(instVel, 0.45f).LimitLength(2400f);
        _lastPos = drag.Position;
        _lastDragUsec = nowUsec;
        GetViewport()?.SetInputAsHandled();
    }

    private void ResetActiveTracking()
    {
        _activeFinger = -1;
        _targetScroll = null;
        _pressedButton = null;
        _isDragging = false;
        _canScrollV = false;
        _canScrollH = false;
        _accumV = 0f;
        _accumH = 0f;
        _velocity = Vector2.Zero;
    }

    private void StopInertia()
    {
        _inertiaScroll = null;
        _inertiaVelocity = Vector2.Zero;
        _inertiaAccumV = 0f;
        _inertiaAccumH = 0f;
    }

    /// <summary>
    /// Pure threshold and axis check deciding when a touch motion becomes a scroll drag.
    /// </summary>
    internal static bool ShouldStartDrag(float dx, float dy, bool canScrollV, bool canScrollH, float thresholdPx = DragThresholdPx)
    {
        if (!canScrollV && !canScrollH) return false;
        var distSq = dx * dx + dy * dy;
        if (distSq < thresholdPx * thresholdPx) return false;
        var absX = MathF.Abs(dx);
        var absY = MathF.Abs(dy);
        if (canScrollV && canScrollH) return true;
        if (canScrollV) return absY >= absX * 0.45f;
        return absX >= absY * 0.45f;
    }

    internal static int ClampScrollValue(double current, double delta, double maxValue, double page)
    {
        var maxScroll = Math.Max(0.0, maxValue - page);
        var next = Math.Clamp(current + delta, 0.0, maxScroll);
        return (int)Math.Round(next);
    }

    private static bool ApplyScrollDelta(ScrollContainer scroll, bool canV, bool canH,
        float fingerDx, float fingerDy, ref float accumH, ref float accumV)
    {
        var moved = false;
        if (canV && scroll.GetVScrollBar() is { } vbar && IsInstanceValid(vbar))
        {
            accumV -= fingerDy;
            var whole = (int)MathF.Truncate(accumV);
            if (whole != 0)
            {
                accumV -= whole;
                var before = scroll.ScrollVertical;
                var next = ClampScrollValue(before, whole, vbar.MaxValue, vbar.Page);
                if (next != before)
                {
                    scroll.ScrollVertical = next;
                    moved = true;
                }
            }
        }
        if (canH && scroll.GetHScrollBar() is { } hbar && IsInstanceValid(hbar))
        {
            accumH -= fingerDx;
            var whole = (int)MathF.Truncate(accumH);
            if (whole != 0)
            {
                accumH -= whole;
                var before = scroll.ScrollHorizontal;
                var next = ClampScrollValue(before, whole, hbar.MaxValue, hbar.Page);
                if (next != before)
                {
                    scroll.ScrollHorizontal = next;
                    moved = true;
                }
            }
        }
        return moved;
    }

    private static void CancelPressedButton(BaseButton button)
    {
        if (button is null || !IsInstanceValid(button) || button.Disabled) return;
        // Toggling Disabled briefly clears Godot's internal press_attempt / pressing_inside flags
        // without firing Pressed, so lifting the finger after a scroll drag never clicks the row.
        button.Disabled = true;
        button.Disabled = false;
    }

    private static bool IsDirectManipulationControl(Control control) =>
        control is ScrollBar or Slider or SpinBox or LineEdit or TextEdit;

    private static ScrollContainer FindScrollableAncestor(Control control, out bool canV, out bool canH)
    {
        canV = false;
        canH = false;
        Node current = control;
        while (current is not null)
        {
            if (current is ScrollContainer sc && sc.IsVisibleInTree())
            {
                var v = CanScrollVertically(sc);
                var h = CanScrollHorizontally(sc);
                if (v || h)
                {
                    canV = v;
                    canH = h;
                    return sc;
                }
            }
            current = current.GetParent();
        }
        return null;
    }

    private static bool CanScrollVertically(ScrollContainer sc) =>
        sc.VerticalScrollMode != ScrollContainer.ScrollMode.Disabled &&
        sc.GetVScrollBar() is { } bar && IsInstanceValid(bar) &&
        bar.MaxValue > bar.Page + 1.0;

    private static bool CanScrollHorizontally(ScrollContainer sc) =>
        sc.HorizontalScrollMode != ScrollContainer.ScrollMode.Disabled &&
        sc.GetHScrollBar() is { } bar && IsInstanceValid(bar) &&
        bar.MaxValue > bar.Page + 1.0;

    private static BaseButton FindButtonAncestorInside(Control control, ScrollContainer stopAt)
    {
        Node current = control;
        while (current is not null && current != stopAt)
        {
            if (current is BaseButton btn) return btn;
            current = current.GetParent();
        }
        return null;
    }

    /// <summary>
    /// Walk visible UI controls in reverse draw order (topmost first) to locate the exact control
    /// under a touch point, respecting clipping and MouseFilter.
    /// </summary>
    private static Control HitTestTopControl(Node node, Vector2 point)
    {
        if (node is null || !GodotObject.IsInstanceValid(node)) return null;
        if (node is CanvasItem item && !item.Visible) return null;
        if (node is Control ctrl)
        {
            var inside = ctrl.GetGlobalRect().HasPoint(point);
            if (ctrl.ClipContents && !inside) return null;
            for (var i = ctrl.GetChildCount() - 1; i >= 0; i--)
            {
                var childHit = HitTestTopControl(ctrl.GetChild(i), point);
                if (childHit is not null) return childHit;
            }
            if (inside && ctrl.MouseFilter != Control.MouseFilterEnum.Ignore)
                return ctrl;
            return null;
        }
        for (var i = node.GetChildCount() - 1; i >= 0; i--)
        {
            var childHit = HitTestTopControl(node.GetChild(i), point);
            if (childHit is not null) return childHit;
        }
        return null;
    }
}
