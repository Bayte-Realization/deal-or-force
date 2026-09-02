using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _pauseMenuLayer = null!;

    private void BuildPauseMenu()
    {
        _pauseMenuLayer = FullLayer("PauseMenu");
        _pauseMenuLayer.Visible = false;
        _pauseMenuLayer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_pauseMenuLayer);

        var veil = new ColorRect
        {
            Color = new Color(0.015f, 0.025f, 0.045f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop
        };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _pauseMenuLayer.AddChild(veil);

        var panel = TexturedPanel(_pauseMenuLayer);
        panel.AnchorLeft = 0.5f;
        panel.AnchorRight = 0.5f;
        panel.AnchorTop = 0.5f;
        panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -210;
        panel.OffsetRight = 210;
        panel.OffsetTop = -245;
        panel.OffsetBottom = 245;

        var rows = PanelRows(panel, 30, 26, 12);
        rows.AddChild(Title("CAMPAIGN PAUSED", 28, Ui.Colour("text.accent")));
        rows.AddChild(Title("The simulation is stopped. Your campaign remains in memory.", 14,
            Ui.Colour("text.muted")));
        rows.AddChild(ActionButton("Resume campaign", HidePauseMenu, 330, 46, true));
        rows.AddChild(ActionButton("Quick save  [F5]", QuickSave, 330, 42));
        rows.AddChild(ActionButton("Save / load campaign", ShowSaveLoadInterface, 330, 42));
        rows.AddChild(ActionButton("Return to main menu", ReturnToMainMenu, 330, 42));
        rows.AddChild(ActionButton("Quit to desktop", () => GetTree().Quit(), 330, 42));
        rows.AddChild(Title("Esc closes this menu", 12, Ui.Colour("text.hint")));
    }

    private void ShowPauseMenu()
    {
        if (!_hudLayer.Visible || _handoverLayer is { Visible: true }) return;
        _campaign.IsPaused = true;
        _pause.Text = "▶";
        _pauseMenuLayer.Visible = true;
        _pauseMenuLayer.MoveToFront();
    }

    private void HidePauseMenu() => _pauseMenuLayer.Visible = false;

    private void ReturnToMainMenu()
    {
        HidePauseMenu();
        ShowMainMenu();
    }
}
