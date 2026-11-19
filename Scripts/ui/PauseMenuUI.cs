using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _pauseMenuLayer = null!;
    private PanelContainer _pausePanel = null!;

    private void BuildPauseMenu()
    {
        _pauseMenuLayer = FullLayer("PauseMenu");
        _pauseMenuLayer.Visible = false;
        _pauseMenuLayer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_pauseMenuLayer);

        var veil = new ColorRect
        {
            Color = MenuChromeTheme.VeilColor,
            MouseFilter = MouseFilterEnum.Stop
        };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _pauseMenuLayer.AddChild(veil);

        var panel = TexturedPanel(_pauseMenuLayer, PanelSurface.Modal);
        MenuChromeTheme.StyleArchitecturalPlate(panel, brassCorners: true);
        Ui.Place(panel, "modal.pause");
        _pausePanel = panel;

        var rows = PanelRows(panel, 28, 22, 10);
        AddPauseHeader(rows);
        rows.AddChild(PausePrimaryAction("Resume campaign", HidePauseMenu));
        rows.AddChild(PauseWellAction("Quick save  [F5]", QuickSave));
        rows.AddChild(PauseWellAction("Save / load campaign", ShowSaveLoadInterface));
        // Settings and the music side by side: both are about how the game sounds and looks, and the
        // panel has to stay within 720 pixels with the two entries the top bar gave up (step 3 of
        // the interface plan). The music button is the game's one soundtrack entry; it opens the
        // player over this menu and hands focus back here when it closes.
        var settingsRow = PauseMenuPair(rows);
        var settings = PauseWellAction("Settings", ShowSettings, 0);
        settings.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        settingsRow.AddChild(settings);
        _mediaButton = MusicShortcut(0, 40);
        MenuChromeTheme.StyleWellButton(_mediaButton, false, 13);
        _mediaButton.ClipText = true;
        _mediaButton.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _mediaButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        settingsRow.AddChild(_mediaButton);
        var watch = PauseWellAction("Watch the world  [F7]", () => { HidePauseMenu(); ToggleObserverMode(); });
        watch.TooltipText = Ui.Tip(Tr("Observer mode: no events, nothing stops the clock, and the panels get out of the way. For watching a world run, and for recording one."));
        rows.AddChild(watch);
        // Help was reachable from a small button on the top bar and from F1, and from neither
        // of the two menus a lost player actually opens. The top bar's Handbook and Find have
        // moved here (and onto F1 and Ctrl+K, which never changed), side by side.
        var helpRow = PauseMenuPair(rows);
        var handbook = PauseWellAction("Handbook  [F1]", () => { HidePauseMenu(); OpenHandbook(); }, 0);
        handbook.Name = "PauseOpenHandbook";
        handbook.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Tip(handbook, "What every screen is for and what its figures mean (F1).");
        helpRow.AddChild(handbook);
        var find = PauseWellAction("Find  [Ctrl+K]", () => { HidePauseMenu(); OpenNavigator(); }, 0);
        find.Name = "PauseOpenNavigator";
        find.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Tip(find, "Every screen in the campaign, searchable (Ctrl+K).");
        helpRow.AddChild(find);
        // One click used to throw the campaign away. Leaving now asks a plain "Are you sure?"
        // (LeaveConfirmation), and so does quitting to the desktop, which used not to ask at all.
        _leaveButton = PauseWellAction("Return to main menu", () => LeaveConfirmation.ReturnToMainMenu(this, ReturnToMainMenu));
        _leaveButton.TooltipText = Ui.Tip(Tr("Leave the campaign and go back to the title screen. Nothing is saved on the way out - quick-save first if this campaign is worth keeping."));
        rows.AddChild(_leaveButton);
        rows.AddChild(PauseWellAction("Quit to desktop", () => LeaveConfirmation.QuitToDesktop(this, inCampaign: true)));
    }

    private void AddPauseHeader(VBoxContainer rows)
    {
        var title = Title("CAMPAIGN PAUSED", 25, MenuChromeTheme.Paper);
        MenuChromeTheme.StyleExistingLabel(title, 25, MenuChromeTheme.Paper, serif: true);
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        rows.AddChild(title);

        var sub = Title("The simulation is stopped. Your campaign remains in memory.", 13, MenuChromeTheme.PaperDim);
        sub.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(sub);
        rows.AddChild(MenuChromeTheme.CreateEngravedRule("PauseMenuRule", 2f));
    }

    private Button PausePrimaryAction(string text, System.Action onPressed)
    {
        var button = ActionButton(text, onPressed, 330, 44, true);
        button.ClipText = true;
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        MenuChromeTheme.StylePrimaryCta(button, 16, serif: true);
        return button;
    }

    private Button PauseWellAction(string text, System.Action onPressed, float width = 330)
    {
        var button = ActionButton(text, onPressed, width, 40);
        button.ClipText = true;
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        MenuChromeTheme.StyleWellButton(button, false, 13);
        return button;
    }

    private Button _leaveButton = null!;

    /// <summary>A row of two equal buttons, as wide as the menu's single ones.</summary>
    private static HBoxContainer PauseMenuPair(VBoxContainer rows)
    {
        var pair = new HBoxContainer { CustomMinimumSize = new Vector2(330, 0) };
        pair.AddThemeConstantOverride("separation", 10);
        rows.AddChild(pair);
        return pair;
    }

    private void ShowPauseMenu()
    {
        if (!_hudLayer.Visible ||
            _handoverLayer is { Visible: true } ||
            _saveLoadLayer is { Visible: true } ||
            _settingsLayer is { Visible: true } ||
            _networkLayer is { Visible: true } ||
            _pickerLayer is { Visible: true } ||
            _mediaLibraryLayer is { Visible: true })
            return;
        _leaveButton.Text = Tr("Return to main menu");
        _campaign.IsPaused = true;
        _pause.Text = "▶";
        UpdateSpeedButtons();
        if (_pausePanel is not null)
        {
            var view = Size.X > 100 && Size.Y > 100 ? Size : GetViewportRect().Size;
            var halfW = Mathf.Min(230f, (view.X - 48f) * 0.5f);
            var halfH = Mathf.Min(280f, (view.Y - 40f) * 0.5f);
            _pausePanel.OffsetLeft = -halfW;
            _pausePanel.OffsetRight = halfW;
            _pausePanel.OffsetTop = -halfH;
            _pausePanel.OffsetBottom = halfH;
        }
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
