using System;
using System.Linq;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _warDeclarationReturnFocus, _ultimatumReturnFocus;

    private static void ReadableDecision(PanelContainer panel, string layout)
    {
        panel.AddChild(new DecisionDialogLayout { Name = "ReadableDecisionLayout", Panel = panel, LayoutKey = layout });
    }

    /// <summary>Keep Tab inside the active document, including offscreen scroll rows.
    /// FollowFocus on the real scroll containers reveals the chosen row. Do not steal
    /// arrows, text entry or popup inputs, and never dispatch a campaign command here.</summary>
    private void HandleDecisionInput(Control dialog, InputEvent input, Action close, bool war = false)
    {
        if (input is not InputEventKey { Pressed: true } key) return;
        if (key.Keycode == Key.Escape && !key.Echo)
        { close(); GetViewport().SetInputAsHandled(); return; }
        if (war && key.Keycode is Key.Enter or Key.KpEnter)
        { GetViewport().SetInputAsHandled(); return; }
        if (key.Keycode is Key.F7 or Key.F10)
        { GetViewport().SetInputAsHandled(); return; }
        if (key.Keycode != Key.Tab || key.CtrlPressed || key.AltPressed || key.MetaPressed) return;
        var stops = dialog.FindChildren("*", "Control", true, false).OfType<Control>()
            .Where(c => c.FocusMode == FocusModeEnum.All && c.IsVisibleInTree() &&
                c is not BaseButton { Disabled: true } && c is not LineEdit { Editable: false }).ToArray();
        if (stops.Length > 0)
        {
            var current = Array.IndexOf(stops, GetViewport().GuiGetFocusOwner());
            var index = current < 0 ? key.ShiftPressed ? stops.Length - 1 : 0
                : (current + (key.ShiftPressed ? -1 : 1) + stops.Length) % stops.Length;
            // Godot's outer FollowFocus cannot reveal a row hidden inside another
            // ScrollContainer. Reveal from the inside out before focusing it.
            Control reveal = stops[index];
            for (Node parent = reveal.GetParent(); parent is not null && parent != dialog; parent = parent.GetParent())
                if (parent is ScrollContainer scroll) { scroll.EnsureControlVisible(reveal); reveal = scroll; }
            stops[index].GrabFocus();
        }
        GetViewport().SetInputAsHandled();
    }

    private static void RestoreDecisionFocus(ref Control previous)
    {
        if (IsInstanceValid(previous) && previous.IsVisibleInTree()) previous.GrabFocus();
        previous = null;
    }
}
