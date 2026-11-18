using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _modActionModal;

    private void ShowModFrontendResult(string note)
    {
        if (IsInstanceValid(_modActionModal)) _modActionModal.QueueFree();

        var layer = FullLayer("ModActionResult");
        layer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(layer);
        _modActionModal = layer;

        void Dismiss()
        {
            if (IsInstanceValid(layer)) layer.QueueFree();
            if (ReferenceEquals(_modActionModal, layer)) _modActionModal = null;
        }

        var veil = new ColorRect { Color = MenuChromeTheme.VeilColor, MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        veil.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Dismiss();
        };
        layer.AddChild(veil);

        var panel = TexturedPanel(layer, PanelSurface.Modal);
        MenuChromeTheme.StyleArchitecturalPlate(panel, brassCorners: true, padX: 26, padY: 20);
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.AnchorLeft = panel.AnchorRight = 0.5f;
        panel.AnchorTop = panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -280;
        panel.OffsetRight = 280;
        panel.OffsetTop = -135;
        panel.OffsetBottom = 135;
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 10);
        panel.AddChild(rows);

        var title = Title("Mod action", 22, MenuChromeTheme.Paper);
        MenuChromeTheme.StyleExistingLabel(title, 22, MenuChromeTheme.Paper, serif: true);
        rows.AddChild(title);
        rows.AddChild(MenuChromeTheme.CreateEngravedRule("ModActionRule", 1f));

        var well = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        well.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(12, 10));
        rows.AddChild(well);

        var body = new Label
        {
            Text = Tr(note ?? ""),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        MenuChromeTheme.StyleExistingLabel(body, 14, MenuChromeTheme.PaperDim);
        well.AddChild(body);

        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        rows.AddChild(footer);
        var ok = ActionButton("OK", Dismiss, 120, 38, true);
        MenuChromeTheme.StylePrimaryCta(ok, 14, serif: false);
        footer.AddChild(ok);
        layer.MoveToFront();
        ok.GrabFocus();
    }
}
