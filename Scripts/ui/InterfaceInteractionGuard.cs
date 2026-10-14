using Godot;

namespace DealOrForce;

/// <summary>Defer destructive background rebuilds, not simulation or explicit user actions.</summary>
public static class InterfaceInteractionGuard
{
    private const string Group = "dof_live_popup_controls";
    private const string Installed = "dof_interaction_guard_installed";
    public static void Install(SceneTree tree)
    {
        if (tree.HasMeta(Installed)) return;
        tree.SetMeta(Installed, true);
        tree.NodeAdded += Register;
        Scan(tree.Root);
    }
    private static void Register(Node node)
    { if (node is OptionButton or MenuButton) node.AddToGroup(Group); }
    private static void Scan(Node node)
    { Register(node); foreach (var child in node.GetChildren()) Scan(child); }
    public static bool Busy(Control workspace)
    {
        if (workspace is null || !workspace.IsVisibleInTree()) return false;
        var focus = workspace.GetViewport().GuiGetFocusOwner();
        if (focus is not null && workspace.IsAncestorOf(focus) &&
            (focus is LineEdit or TextEdit or SpinBox ||
             Input.IsMouseButtonPressed(MouseButton.Left) && (focus is BaseButton or Range)))
            return true;
        foreach (var node in workspace.GetTree().GetNodesInGroup(Group))
        {
            if (node is not Control control || !control.IsVisibleInTree() || !workspace.IsAncestorOf(control)) continue;
            if (control is OptionButton option && option.GetPopup().Visible || control is MenuButton menu && menu.GetPopup().Visible)
                return true;
        }
        return false;
    }
}
