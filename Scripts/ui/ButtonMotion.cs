using Godot;

namespace DealOrForce;

/// <summary>
/// Every button answers the pointer.
///
/// The theme gave a button three still pictures - normal, hover, pressed - and swapped them in
/// a single frame, so the interface felt like a form rather than a game. Engine-default menus
/// (and Paradox's) ease into the hover and dip on the press. Kept here once for every button the
/// screens build by hand: a short brightness ease on hover, a dip on the press. A button whose
/// own tint already means something (a trade row coloured by supply) is left alone.
/// `ui.motion` 0 in the skin turns it off; one tween per hover, nothing per frame.
/// </summary>
internal static class ButtonMotion
{
    private const string Tween = "dof_hover_tween";
    private const float Lift = 1.14f;
    private const float Dip = 0.86f;
    private const float Ease = 0.09f;

    private static SceneTree _installed;

    public static void Install(SceneTree tree)
    {
        if (tree is null || ReferenceEquals(tree, _installed)) return;
        if (Ui.Metric("ui.motion", 1f) <= 0f) return;
        _installed = tree;
        tree.NodeAdded += OnAdded;
    }

    private static void OnAdded(Node node)
    {
        if (node is not BaseButton button || button is CheckBox or CheckButton) return;
        button.MouseEntered += () => To(button, Lift, Ease);
        button.MouseExited += () => To(button, 1f, Ease * 1.6f);
        button.ButtonDown += () => To(button, Dip, Ease * 0.5f);
        button.ButtonUp += () => To(button, button.IsHovered() ? Lift : 1f, Ease);
    }

    private static void To(BaseButton button, float level, float seconds)
    {
        if (!GodotObject.IsInstanceValid(button) || !button.IsInsideTree() || button.Disabled && level > 1f) return;
        var now = button.SelfModulate;
        // A tint the screen chose, not ours: never brightened, never reset to white.
        if (!button.HasMeta(Tween) && (now.R != 1f || now.G != 1f || now.B != 1f)) return;
        if (button.HasMeta(Tween) && button.GetMeta(Tween).AsGodotObject() is Tween running && running.IsValid())
            running.Kill();
        var target = new Color(level, level, level, now.A);
        if (level == 1f && now.IsEqualApprox(target)) { button.RemoveMeta(Tween); return; }
        var tween = button.CreateTween();
        tween.TweenProperty(button, "self_modulate", target, seconds)
            .SetTrans(Godot.Tween.TransitionType.Sine).SetEase(Godot.Tween.EaseType.Out);
        if (level == 1f) tween.TweenCallback(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(button)) button.RemoveMeta(Tween);
        }));
        button.SetMeta(Tween, tween);
    }
}
