using Godot;

namespace DealOrForce;

/// <summary>
/// No text stands one letter to a line.
///
/// A wrapping label asks for no width of its own - it will take whatever it is given and
/// wrap to it. Put one in a row beside a flag or an icon without telling the row to give
/// it the rest, and the row gives it nothing: the title of a disputed region came out as a
/// column of single letters. Every screen builds its rows by hand, so the rule is kept here
/// once, for every one of them: a wrapping label in a row - or the column holding it in
/// a row - takes the row's spare width unless somebody has already said how wide it is.
/// </summary>
internal static class WrapGuard
{
    private const int Depth = 4;

    private static SceneTree _installed;

    /// <summary>Once per tree: the menu and the campaign both call it, and the tree outlives both.</summary>
    public static void Install(SceneTree tree)
    {
        if (tree is null || ReferenceEquals(tree, _installed)) return;
        _installed = tree;
        tree.NodeAdded += OnAdded;
    }

    private static void OnAdded(Node node)
    {
        if (!Wraps(node)) return;
        // Deferred: a screen may set the label's size flags just after adding it.
        var control = (Control)node;
        Callable.From(() => Fix(control)).CallDeferred();
    }

    private static bool Wraps(Node node) => node switch
    {
        Label label => label.AutowrapMode != TextServer.AutowrapMode.Off,
        RichTextLabel rich => rich.AutowrapMode != TextServer.AutowrapMode.Off,
        _ => false
    };

    private static void Fix(Control label)
    {
        if (!GodotObject.IsInstanceValid(label) || !label.IsInsideTree()) return;
        Control child = label;
        for (var level = 0; level < Depth; level++)
        {
            if (child.GetParent() is not Container parent || parent is ScrollContainer) return;
            // In a flow the row wraps, not the words: a wrapping label there is given no width
            // at all and stands a letter to a line ("+31 more" under a row of flags).
            if (parent is FlowContainer && ReferenceEquals(child, label))
            {
                if (label is Label plain) plain.AutowrapMode = TextServer.AutowrapMode.Off;
                else if (label is RichTextLabel rich) rich.AutowrapMode = TextServer.AutowrapMode.Off;
                return;
            }
            if (parent is HBoxContainer)
            {
                if (child.CustomMinimumSize.X <= 0f &&
                    (child.SizeFlagsHorizontal & Control.SizeFlags.Expand) == 0)
                    child.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                return;
            }
            child = parent;
        }
    }
}
