using Godot;

namespace DealOrForce;

/// <summary>
/// Keep logical keyboard focus (and its return position) without making a mouse-
/// activated shortcut look latched open. Only the focus overlay follows input
/// modality; a selected tab, pressed toggle, hover and primary action keep their
/// own styles. Moving the mouse alone must not hide a keyboard user's focus.
/// </summary>
internal static class ButtonFocusPresentation
{
    private const string KeyboardMeta = "button_keyboard_focus_visible";
    private const string WiredMeta = "button_focus_presentation_wired";
    private const string KeyboardStyleMeta = "button_keyboard_focus_style";
    private const string AppliedStyleMeta = "button_applied_focus_style";
    private static StyleBoxEmpty _pointerFocus;

    internal static void Observe(Viewport viewport, InputEvent input)
    {
        bool? keyboard = input switch
        {
            InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle } => false,
            InputEventScreenTouch { Pressed: true } => false,
            InputEventKey { Pressed: true, Keycode: Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right
                or Key.Enter or Key.KpEnter or Key.Space or Key.Escape } => true,
            InputEventJoypadButton { Pressed: true } => true,
            InputEventJoypadMotion motion when Mathf.Abs(motion.AxisValue) >= .5f => true,
            _ => null
        };
        if (!keyboard.HasValue || !GodotObject.IsInstanceValid(viewport)) return;
        if (KeyboardVisible(viewport) == keyboard.Value && viewport.HasMeta(KeyboardMeta)) return;
        viewport.SetMeta(KeyboardMeta, keyboard.Value);
        if (viewport.GuiGetFocusOwner() is Button button && button.HasMeta(WiredMeta)) Apply(button);
    }

    internal static void Style(Button button, bool rounded)
    {
        var focus = AtlasSurfaceSkin.Focus(rounded);
        button.SetMeta(KeyboardStyleMeta, focus);
        button.SetMeta(AppliedStyleMeta, focus);
        button.AddThemeStyleboxOverride("focus", focus);
        if (!button.HasMeta(WiredMeta))
        {
            button.SetMeta(WiredMeta, true);
            button.FocusEntered += () => Apply(button);
        }
        Apply(button);
    }

    private static bool KeyboardVisible(Viewport viewport) => !GodotObject.IsInstanceValid(viewport) ||
        !viewport.HasMeta(KeyboardMeta) || viewport.GetMeta(KeyboardMeta).AsBool();

    private static void Apply(Button button)
    {
        if (!GodotObject.IsInstanceValid(button)) return;
        var keyboard = !button.IsInsideTree() || KeyboardVisible(button.GetViewport());
        var current = button.GetThemeStylebox("focus");
        var applied = button.GetMeta(AppliedStyleMeta).AsGodotObject() as StyleBox;
        // Ledger/card rows may deliberately replace the default overlay after
        // StyleButton. Preserve their keyboard design when modality changes.
        if (current != applied) button.SetMeta(KeyboardStyleMeta, current);
        var style = keyboard ? button.GetMeta(KeyboardStyleMeta).AsGodotObject() as StyleBox
            : (_pointerFocus ??= new StyleBoxEmpty());
        button.AddThemeStyleboxOverride("focus", style);
        button.SetMeta(AppliedStyleMeta, style);
    }
}

