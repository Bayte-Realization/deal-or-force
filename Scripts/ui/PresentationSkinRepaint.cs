using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>
    /// Refreshes skin layout, surface boxes, and semantic token colours across live UI nodes
    /// without rebuilding scenes or resetting player selections and scroll positions.
    /// </summary>
    private void RepaintPresentation(Node node)
    {
        if (node.IsQueuedForDeletion()) return;
        if (node is Control control && node.HasMeta("skin_layout"))
            Ui.Place(control, node.GetMeta("skin_layout").AsString());
        if (node is PanelContainer panel) RepaintSkinPanel(panel);
        if (node is Button button) RepaintSkinButton(button);
        if (node is Label label) RepaintSkinLabel(label);
        foreach (var child in node.GetChildren()) RepaintPresentation(child);
    }

    private void RepaintSkinPanel(PanelContainer panel)
    {
        if (!panel.HasMeta("skin_panel_surface")) return;
        var surface = (PanelSurface)panel.GetMeta("skin_panel_surface").AsInt32();
        var inCampaign = panel.GetMeta("skin_campaign_surface").AsBool();
        panel.AddThemeStyleboxOverride("panel", PanelStyle(surface, inCampaign));
        if (!inCampaign && panel.GetNodeOrNull<Panel>("ShellEdge") is { } edge)
            edge.AddThemeStyleboxOverride("panel", ShellEdgeStyle());
        if (panel.HasMeta("skin_layout"))
            MaterialStyles.ApplyLayout(panel, panel.GetMeta("skin_layout").AsString());
    }

    private void RepaintSkinButton(Button button)
    {
        if (!button.HasMeta("skin_button_primary")) return;
        var colour = button.GetThemeColor("font_color");
        var custom = button.HasMeta("skin_button_applied") &&
            !colour.IsEqualApprox(button.GetMeta("skin_button_applied").AsColor());
        StyleButton(button, button.GetMeta("skin_button_primary").AsBool());
        if (custom) button.AddThemeColorOverride("font_color", colour);
    }

    private static void RepaintSkinLabel(Label label)
    {
        if (!label.HasMeta("skin_label_colour")) return;
        if (!label.GetThemeColor("font_color").IsEqualApprox(label.GetMeta("skin_label_applied").AsColor())) return;
        var colour = Ui.Colour(label.GetMeta("skin_label_colour").AsString());
        label.AddThemeColorOverride("font_color", colour);
        label.SetMeta("skin_label_applied", colour);
    }
}
