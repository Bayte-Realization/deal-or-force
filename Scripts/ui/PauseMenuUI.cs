using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _pauseMenuLayer = null!;
    private PanelContainer _pausePanel = null!;
    private PanelContainer _pauseLiveCard = null!;
    private TextureRect _pauseFlagRect = null!;
    private Control _pausePortraitHost = null!;
    private Label _pauseLeaderTitleName = null!;
    private Label _pauseNationName = null!;
    private Label _pauseDateLabel = null!;
    private Label _pauseRankBadge = null!;
    private Label _pauseWarBadge = null!;

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
        ChromePlateArt.AttachModalBackdrop(veil);

        var panel = TexturedPanel(_pauseMenuLayer, PanelSurface.Modal);
        MenuChromeTheme.StyleArchitecturalPlate(panel, brassCorners: true);
        ChromePlateArt.AttachSurfaceTexture(panel, "leather", 0.34f);
        Ui.Place(panel, "modal.pause");
        _pausePanel = panel;

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        panel.AddChild(scroll);

        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(rows);

        AddPauseHeader(rows);
        BuildPauseLiveCard(rows);

        var btnResume = PausePrimaryAction("Resume campaign", HidePauseMenu);
        ChromePlateArt.ApplyMenuButtonIcon(btnResume, "icon_menu_resume");
        rows.AddChild(btnResume);

        var btnQuickSave = PauseWellAction("Quick save  [F5]", QuickSave);
        rows.AddChild(btnQuickSave);

        var btnSaveLoad = PauseWellAction("Save / load campaign", ShowSaveLoadInterface);
        btnSaveLoad.Name = "PauseOpenSaveArchive";
        ChromePlateArt.ApplyMenuButtonIcon(btnSaveLoad, "icon_menu_load");
        rows.AddChild(btnSaveLoad);

        var settingsRow = PauseMenuPair(rows);
        var settings = PauseWellAction("Settings", ShowSettings, 0);
        settings.Name = "PauseOpenSettings";
        settings.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ChromePlateArt.ApplyMenuButtonIcon(settings, "icon_menu_settings");
        settingsRow.AddChild(settings);

        _mediaButton = MusicShortcut(0, 38);
        MenuChromeTheme.StyleWellButton(_mediaButton, false, 12);
        _mediaButton.ClipText = true;
        _mediaButton.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _mediaButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        settingsRow.AddChild(_mediaButton);

        _pauseObserverButton = PauseWellAction("shell.obs.pause_enter", () =>
        {
            HidePauseMenu();
            ToggleObserverMode();
        });
        _pauseObserverButton.Name = "PauseToggleObserver";
        _pauseObserverButton.TooltipText = Ui.Tip(Loc.Get("shell.obs.pause_world_tip"));
        rows.AddChild(_pauseObserverButton);

        var helpRow = PauseMenuPair(rows);
        var handbook = PauseWellAction("Handbook  [F1]", () => { HidePauseMenu(); OpenHandbook(); }, 0);
        handbook.Name = "PauseOpenHandbook";
        handbook.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ChromePlateArt.ApplyMenuButtonIcon(handbook, "icon_menu_handbook");
        Tip(handbook, "What every screen is for and what its figures mean (F1).");
        helpRow.AddChild(handbook);

        var find = PauseWellAction("Find  [Ctrl+K]", () => { HidePauseMenu(); OpenNavigator(); }, 0);
        find.Name = "PauseOpenNavigator";
        find.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ChromePlateArt.ApplyMenuButtonIcon(find, "icon_search");
        Tip(find, "Every screen in the campaign, searchable (Ctrl+K).");
        helpRow.AddChild(find);

        if (Platform.PlatformProfile.Current?.IsTouchPrimary == true)
        {
            var console = PauseWellAction("Developer console", () =>
            {
                HidePauseMenu();
                EnsureScreen("console", BuildDeveloperConsole);
                ToggleDeveloperConsole();
            });
            console.Name = "PauseOpenConsole";
            Tip(console, "Open the command console.");
            rows.AddChild(console);
        }

        rows.AddChild(ChromePlateArt.CreateEngravedMedallionDivider("PauseMenuRuleBottom", 1f, 10f));

        _leaveButton = PauseWellAction("Return to main menu", () => LeaveConfirmation.ReturnToMainMenu(this, ReturnToMainMenu));
        _leaveButton.TooltipText = Ui.Tip(Tr("Leave the campaign and go back to the title screen. Nothing is saved on the way out - quick-save first if this campaign is worth keeping."));
        rows.AddChild(_leaveButton);

        var btnQuit = PauseWellAction("Quit to desktop", () => LeaveConfirmation.QuitToDesktop(this, inCampaign: true));
        ChromePlateArt.ApplyMenuButtonIcon(btnQuit, "icon_menu_quit");
        rows.AddChild(btnQuit);
    }

    private void BuildPauseLiveCard(VBoxContainer rows)
    {
        _pauseLiveCard = new PanelContainer { Name = "PauseLiveCard", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _pauseLiveCard.AddThemeStyleboxOverride("panel", ChromePlateArt.BuildCardWrapperStyle(8, 6, 8, 6) ?? MenuChromeTheme.GradientPlateBox(
            MenuChromeTheme.BodyTop, MenuChromeTheme.BodyBottom, MenuChromeTheme.BrassShadow, borderThickness: 1));
        rows.AddChild(_pauseLiveCard);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _pauseLiveCard.AddChild(row);

        _pausePortraitHost = new Control { CustomMinimumSize = new Vector2(44, 54), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(_pausePortraitHost);

        var infoCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        infoCol.AddThemeConstantOverride("separation", 2);
        row.AddChild(infoCol);

        var line1 = new HBoxContainer();
        line1.AddThemeConstantOverride("separation", 6);
        infoCol.AddChild(line1);

        _pauseFlagRect = new TextureRect
        {
            CustomMinimumSize = new Vector2(24, 16),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        line1.AddChild(_pauseFlagRect);

        _pauseNationName = new Label { ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        MenuChromeTheme.StyleExistingLabel(_pauseNationName, 13, MenuChromeTheme.Paper, serif: true);
        line1.AddChild(_pauseNationName);

        _pauseLeaderTitleName = new Label { ClipText = true };
        MenuChromeTheme.StyleExistingLabel(_pauseLeaderTitleName, 11, MenuChromeTheme.PaperDim, serif: false);
        infoCol.AddChild(_pauseLeaderTitleName);

        var badgesRow = new HBoxContainer();
        badgesRow.AddThemeConstantOverride("separation", 8);
        infoCol.AddChild(badgesRow);

        _pauseDateLabel = new Label();
        MenuChromeTheme.StyleExistingLabel(_pauseDateLabel, 10, MenuChromeTheme.PaperFaint, tabular: true);
        badgesRow.AddChild(_pauseDateLabel);

        _pauseRankBadge = new Label();
        MenuChromeTheme.StyleExistingLabel(_pauseRankBadge, 10, MenuChromeTheme.BrassHi, tabular: true);
        badgesRow.AddChild(_pauseRankBadge);

        _pauseWarBadge = new Label();
        MenuChromeTheme.StyleExistingLabel(_pauseWarBadge, 10, MenuChromeTheme.PaperDim, tabular: false);
        badgesRow.AddChild(_pauseWarBadge);
    }

    private void AddPauseHeader(VBoxContainer rows)
    {
        var headBox = new PanelContainer { Name = "PauseHeaderBox" };
        headBox.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        rows.AddChild(headBox);
        ChromePlateArt.AttachHeaderFiligree(headBox, 0.36f);

        var title = Title("CAMPAIGN PAUSED", 22, MenuChromeTheme.Paper);
        MenuChromeTheme.StyleExistingLabel(title, 22, MenuChromeTheme.Paper, serif: true);
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        headBox.AddChild(title);

        var sub = Title("The simulation is stopped. Your campaign remains in memory.", 12, MenuChromeTheme.PaperDim);
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
    private Button _pauseObserverButton = null!;

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
        EnsureScreen("pause_menu", BuildPauseMenu);
        _leaveButton.Text = Tr("Return to main menu");
        _campaign.IsPaused = true;
        _pause.Text = "▶";
        UpdateSpeedButtons();
        if (_pausePanel is not null)
        {
            var view = Size.X > 100 && Size.Y > 100 ? Size : GetViewportRect().Size;
            var halfW = Mathf.Min(240f, (view.X - 48f) * 0.5f);
            var halfH = Mathf.Min(310f, (view.Y - 40f) * 0.5f);
            _pausePanel.OffsetLeft = -halfW;
            _pausePanel.OffsetRight = halfW;
            _pausePanel.OffsetTop = -halfH;
            _pausePanel.OffsetBottom = halfH;
        }

        UpdatePauseLiveCard();
        UpdatePauseObserverButton();
        _pauseMenuLayer.Visible = true;
        _pauseMenuLayer.MoveToFront();
    }

    private void UpdatePauseObserverButton()
    {
        if (_pauseObserverButton is null || _campaign is null) return;
        var observing = _observerMode || _campaign.ObserverMode;
        if (!observing)
        {
            _pauseObserverButton.Disabled = false;
            _pauseObserverButton.Text = Loc.Get("shell.obs.pause_enter");
            _pauseObserverButton.TooltipText = Ui.Tip(Loc.Get("shell.obs.pause_world_tip"));
            return;
        }
        if (_campaign.HumanSeats.Count == 0 && _observerWatched is { } watched)
        {
            _pauseObserverButton.Disabled = false;
            _pauseObserverButton.Text = Loc.Format("shell.obs.pause_take_fmt", watched.DisplayName);
            _pauseObserverButton.TooltipText = Ui.Tip(Loc.Format("shell.obs.pause_take_tip_fmt", watched.DisplayName));
            return;
        }
        if (_campaign.HumanSeats.Count == 0 && _observerWatched is null)
        {
            _pauseObserverButton.Disabled = true;
            _pauseObserverButton.Text = Loc.Get("obs.ui.pause.watching_world");
            _pauseObserverButton.TooltipText = Ui.Tip(Loc.Get("shell.obs.badge_tip_world"));
            return;
        }
        _pauseObserverButton.Disabled = false;
        _pauseObserverButton.Text = Loc.Get("shell.obs.pause_leave");
        _pauseObserverButton.TooltipText = Ui.Tip(Loc.Get("shell.obs.pause_leave_tip"));
    }

    private void UpdatePauseLiveCard()
    {
        if (_campaign is null || _pauseLiveCard is null) return;
        var observing = _observerMode || _campaign.ObserverMode;
        if (_campaign.Player is not { } player)
        {
            var worldIcon = SpriteCatalog.TextureOf("icon_world", "res://game/gfx/interface/icons/icon_world.png");
            _pauseFlagRect.Texture = ResourceLoader.Exists(worldIcon) ? GD.Load<Texture2D>(worldIcon) : null;
            _pauseNationName.Text = Loc.Get("shell.obs.badge_world");
            var summary = ObserverUiRules.ComputeWorldSummary(_campaign);
            _pauseLeaderTitleName.Text = Loc.Format("obs.ui.pause.world_sub_fmt", summary.CountryCount, summary.LandlessMovementCount);
            foreach (var ch in _pausePortraitHost.GetChildren()) ch.QueueFree();
            _pauseDateLabel.Text = _campaign.Date.Display();
            _pauseRankBadge.Text = "OBS";
            _pauseWarBadge.Text = summary.OpenWarCount > 0
                ? Loc.Format("shell.active_theatres_badge", summary.OpenWarCount)
                : Loc.Get("shell.peace_status_badge");
            _pauseWarBadge.AddThemeColorOverride("font_color",
                summary.OpenWarCount > 0 ? MenuChromeTheme.Warning : MenuChromeTheme.PaperFaint);
            return;
        }
        _pauseFlagRect.Texture = FlagTextureFor(player.Tag);
        _pauseNationName.Text = observing
            ? $"{player.DisplayName} · {Loc.Get("shell.obs.live_card_tag")}"
            : player.DisplayName;

        var ruler = Rulers.HeadOfState(_campaign, player);
        _pauseLeaderTitleName.Text = $"{Loc.Get(ruler.Title)} {ruler.Name}";

        foreach (var ch in _pausePortraitHost.GetChildren()) ch.QueueFree();
        var portrait = BuildLeaderPortraitBadge(ruler, player.Color, 44, 54);
        _pausePortraitHost.AddChild(portrait);

        _pauseDateLabel.Text = _campaign.Date.Display();

        var rankRows = CountryRankings.Read(_campaign, CountryRankings.Weights.Default);
        var rankRow = rankRows.FirstOrDefault(r => r.NationId == player.Id);
        var rank = rankRow?.OverallRank ?? 1;
        _pauseRankBadge.Text = $"№ {rank} {Loc.Get("shell.overall_rank")}";

        var openWars = Warfare.OpenWars(_campaign).Count(w => !w.Ended && w.Involves(player.Id));
        _pauseWarBadge.Text = openWars > 0
            ? Loc.Format("shell.active_theatres_badge", openWars)
            : Loc.Get("shell.peace_status_badge");
        _pauseWarBadge.AddThemeColorOverride("font_color",
            openWars > 0 ? MenuChromeTheme.Warning : MenuChromeTheme.PaperFaint);
    }

    private void HidePauseMenu()
    {
        if (_pauseMenuLayer is not null) _pauseMenuLayer.Visible = false;
        if (IsNewShellActive) SyncNewShellModalState();
    }

    private void ReturnToMainMenu()
    {
        HidePauseMenu();
        ShowMainMenu();
    }
}
