using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The hands both big trees are read with.
///
/// The technology graph and the doctrine graph are the same object - cards laid out on a canvas
/// inside a ScrollContainer - so they take the same controls: Alt with the wheel zooms, Ctrl with
/// the wheel moves the view (sideways, or up and down while Shift is held too), and the plain
/// wheel stays what it always was, an ordinary scroll of the page. Zooming is aimed rather than
/// guessed at: whatever the pointer is over stays under the pointer.
///
/// A tree is told its own extent - the size its cards lay out to at scale 1 - because a scaled
/// Control still occupies its unscaled rectangle inside a container, so the scroll range has to be
/// grown by hand for the far parts of a zoomed-in tree to be reachable at all.
/// </summary>
public static class TreeViewport
{
    public const float MinZoom = .55f;
    public const float MaxZoom = 1.9f;

    /// <summary>The step one wheel notch of zoom takes: a tenth, so a tree walks rather than jumps.</summary>
    private const float Notch = .12f;

    /// <summary>How far one wheel notch of Ctrl-panning moves the view, in pixels at scale 1.</summary>
    private const int PanStep = 90;

    /// <summary>
    /// One sentence naming the controls, for the captions of both screens: a player who has read
    /// it once on the technology tree does not have to be told again on the doctrine tree.
    /// </summary>
    public static string Hint => Loc.Get(
        "Alt with the wheel zooms; Ctrl with the wheel moves the view, and Shift makes it up and down.");

    /// <summary>
    /// Reads one wheel event for one tree. True means the event belonged to the tree - which is
    /// also what keeps it away from the map, the page behind the tree and the scrollbars.
    ///
    /// `viewport` is the plain Control the canvas inside the scroll lives in. Zoom has to be
    /// applied to a child of that control rather than to the scroll's own child, because a
    /// Container writes its children's transform on every layout pass (position, size, rotation,
    /// scale) - so a ScrollContainer's direct child can never be scaled: it is set back to 1 the
    /// moment anything moves. That was the bug: panning worked and zooming did nothing at all.
    /// </summary>
    public static bool Wheel(Control window, ScrollContainer scroll, Control viewport, Control canvas,
        Vector2 extent, ref float zoom, InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true } wheel ||
            wheel.ButtonIndex is not (MouseButton.WheelUp or MouseButton.WheelDown)) return false;
        if (window is null || scroll is null || viewport is null || canvas is null || !window.IsVisibleInTree())
            return false;
        if (!window.GetGlobalRect().HasPoint(wheel.Position)) return false;
        var up = wheel.ButtonIndex == MouseButton.WheelUp;
        if (wheel.AltPressed)
        {
            Zoom(scroll, viewport, canvas, extent, ref zoom, wheel.Position, up);
            return true;
        }
        if (!wheel.CtrlPressed) return false;
        // Ctrl with the wheel moves the view. Sideways is the one a tree is wide in, so that is
        // what a plain Ctrl does; Shift makes the same gesture vertical.
        var reach = up ? -PanStep : PanStep;
        var wasX = scroll.ScrollHorizontal;
        var wasY = scroll.ScrollVertical;
        if (wheel.ShiftPressed) scroll.ScrollVertical = Math.Max(0, wasY + reach);
        else scroll.ScrollHorizontal = Math.Max(0, wasX + reach);
        if (scroll.ScrollHorizontal == wasX && scroll.ScrollVertical == wasY)
        {
            // At the edge the view cannot move, and swallowing the wheel there would look like a
            // broken control: let the page have the event instead.
            return false;
        }
        return true;
    }

    /// <summary>
    /// One notch of zoom without a pointer, for the buttons beside a tree: a player on a layout
    /// where Alt with the wheel belongs to the window manager still has hands.
    /// </summary>
    public static void Nudge(ScrollContainer scroll, Control viewport, Control canvas, Vector2 extent,
        ref float zoom, bool closer)
    {
        if (scroll is null || viewport is null || canvas is null) return;
        Zoom(scroll, viewport, canvas, extent, ref zoom, scroll.GetGlobalRect().GetCenter(), closer);
    }

    /// <summary>One notch closer or further, holding whatever the pointer is over under the pointer.</summary>
    private static void Zoom(ScrollContainer scroll, Control viewport, Control canvas, Vector2 extent,
        ref float zoom, Vector2 pointer, bool closer)
    {
        var was = zoom;
        zoom = Math.Clamp(zoom * (closer ? 1f + Notch : 1f / (1f + Notch)), MinZoom, MaxZoom);
        if (Math.Abs(zoom - was) < .0001f) return;
        var factor = zoom / was;
        canvas.PivotOffset = Vector2.Zero;
        canvas.Scale = Vector2.One * zoom;
        viewport.CustomMinimumSize = extent * zoom;
        // The scroll range is computed by the container on the next layout pass, so the movement
        // that keeps the pointer's point still has to wait for it.
        var local = pointer - canvas.GlobalPosition;
        var wanted = new Vector2(
            Math.Max(0, (scroll.ScrollHorizontal + local.X) * factor - local.X),
            Math.Max(0, (scroll.ScrollVertical + local.Y) * factor - local.Y));
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(scroll)) return;
            scroll.ScrollHorizontal = (int)wanted.X;
            scroll.ScrollVertical = (int)wanted.Y;
        }).CallDeferred();
    }
}
