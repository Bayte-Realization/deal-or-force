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

        var panel = TexturedPanel(_pauseMenuLayer, PanelSurface.Modal);
        Ui.Place(panel, "modal.pause");

        var rows = PanelRows(panel, 30, 26, 12);
        rows.AddChild(Title("CAMPAIGN PAUSED", 28, Ui.Colour("text.accent")));
        rows.AddChild(Title("The simulation is stopped. Your campaign remains in memory.", 14,
            Ui.Colour("text.muted")));
        rows.AddChild(ActionButton("Resume campaign", HidePauseMenu, 330, 46, true));
        rows.AddChild(ActionButton("Quick save  [F5]", QuickSave, 330, 42));
        rows.AddChild(ActionButton("Save / load campaign", ShowSaveLoadInterface, 330, 42));
        rows.AddChild(ActionButton("Settings", ShowSettings, 330, 42));
        var watch = ActionButton("Watch the world  [F7]", () => { HidePauseMenu(); ToggleObserverMode(); }, 330, 42);
        watch.TooltipText = Ui.Tip(Tr("Observer mode: no events, nothing stops the clock, and the panels get out of the way. For watching a world run, and for recording one."));
        rows.AddChild(watch);
        // Help was reachable from a small button on the top bar and from F1, and from neither
        // of the two menus a lost player actually opens.
        rows.AddChild(ActionButton("Handbook  [F1]", () => { HidePauseMenu(); OpenHandbook(); }, 330, 42));
        // One click used to throw the campaign away. Leaving now asks a plain "Are you sure?"
        // (LeaveConfirmation), and so does quitting to the desktop, which used not to ask at all.
        _leaveButton = ActionButton("Return to main menu", () => LeaveConfirmation.ReturnToMainMenu(this, ReturnToMainMenu), 330, 42);
        _leaveButton.TooltipText = Ui.Tip(Tr("Leave the campaign and go back to the title screen. Nothing is saved on the way out - quick-save first if this campaign is worth keeping."));
        rows.AddChild(_leaveButton);
        rows.AddChild(ActionButton("Quit to desktop", () => LeaveConfirmation.QuitToDesktop(this, inCampaign: true), 330, 42));
    }

    private Button _leaveButton = null!;

    private void ShowPauseMenu()
    {
        if (!_hudLayer.Visible || _handoverLayer is { Visible: true }) return;
        _leaveButton.Text = Tr("Return to main menu");
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
