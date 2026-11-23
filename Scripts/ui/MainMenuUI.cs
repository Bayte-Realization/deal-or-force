using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void OpenWhateverWasAskedFor()
    {
        var wanted = MainMenuScene.Wanted;
        MainMenuScene.Wanted = MenuIntent.None;
        var modAction = MainMenuScene.WantedModAction;
        MainMenuScene.WantedModAction = "";
        _menuLayer.Visible = false;

        switch (wanted)
        {
            case MenuIntent.NewCampaign: OpenNewCampaign(); break;
            case MenuIntent.ModAction:
                _modLoader.TryRunFrontendAction(modAction, out var modNote);
                GD.Print("[mod action] " + Tr(modNote));
                OpenScenarioLibrary();
                ShowModFrontendResult(modNote);
                break;
            case MenuIntent.LoadLatest: LoadLatestSave(); break;
            case MenuIntent.LoadSlot:
                var slot = MainMenuScene.WantedSlot;
                MainMenuScene.WantedSlot = "";
                if (string.IsNullOrWhiteSpace(slot)) LoadLatestSave();
                else LoadFromSlot(slot);
                break;
            case MenuIntent.Multiplayer: OpenNetworkMultiplayer(); break;
            case MenuIntent.Scenarios: OpenScenarioLibrary(); break;
            case MenuIntent.Settings: ShowSettings(); break;
            // Opened directly - from the editor, or by a scene reload - so there is nothing
            // to act on. Only the layer is shown, not the whole hide-everything routine:
            // that one pauses the campaign, and at this point in the boot there is not one.
            default: _menuLayer.Visible = true; break;
        }
    }

    /// <summary>
    /// "New campaign" leads to choosing a country, not to a workshop.
    ///
    /// It used to open the scenario library first: a window of editor buttons, developer
    /// metadata and a list holding exactly one scenario, from which the newcomer had to find
    /// the one button that starts a game - and then sit through a second build of the world
    /// that had just been built behind the loading screen. The library earns that step only
    /// when there is something to choose between. With the built-in scenario alone, the world
    /// already standing behind the menu IS that scenario, so the picker opens on it directly;
    /// the library stays one click away on the picker's own bar, and a mod or an authored
    /// scenario that adds a second entry brings the choice back by existing.
    /// </summary>
    private void OpenNewCampaign()
    {
        var scenarios = ScenarioLibrary.Discover(ScenarioRoots, System.IO.Path.Combine(AuthorRoot, "scenarios")).ToList();
        if (scenarios.Count == 1 && scenarios[0].Id == "2024" && scenarios[0].WorldFile.Length == 0)
        {
            _pickerScenarioEntry = null;
            OpenCountryPicker();
            return;
        }
        OpenScenarioLibrary();
    }

    private void BuildMainMenu()
    {
        _menuLayer = FullLayer("MainMenu");
        AddChild(_menuLayer);

        var panel = TexturedPanel(_menuLayer);
        MenuChromeTheme.StyleArchitecturalPlate(panel, brassCorners: true, padX: 0, padY: 0);
        // Runtime-created Controls need explicit anchors and offsets. Setting
        // Position after a preset leaves them relative to (0, 0) on some
        // Godot 4.x builds, which pushed this panel off-screen.
        Ui.Place(panel, "menu.main");

        var rows = PanelRows(panel, 26, 22, 8);
        var logo = new TextureRect
        {
            Texture = Art("brand_logo_banner", "res://game/gfx/interface/brand_logo_banner.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(0, 178),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            TooltipText = Ui.Tip(Tr("Deal or Force"))
        };
        rows.AddChild(logo);
        var tagline = Title("Choose a world. Shape its future.", 15, MenuChromeTheme.PaperDim);
        MenuChromeTheme.StyleExistingLabel(tagline, 15, MenuChromeTheme.PaperDim, serif: true);
        rows.AddChild(tagline);

        // The heading that used to sit here said "Scenarios & workshop" over two status
        // lines about the world that is loaded and the mods that are not. It named a
        // section that is not on this screen - the workshop is a window reached from a
        // campaign - and a player reading the menu top to bottom was told they were
        // looking at something they were not. The two lines say what they are.
        _scenarioSummary = Title("Preparing campaign data…", 13, MenuChromeTheme.PaperDim);
        rows.AddChild(_scenarioSummary);

        _modStatus = Title(_modLoader.StatusLine, 12, MenuChromeTheme.PaperFaint);
        _modStatus.TooltipText = Ui.Tip(Tr("Development mods: res://mods · installed mods: ") +
                                        ProjectSettings.GlobalizePath("user://mods"));
        rows.AddChild(_modStatus);
        rows.AddChild(MenuChromeTheme.CreateEngravedRule("LegacyMenuDivider", 2f));

        var singleplayer = ActionButton("Singleplayer", OpenScenarioLibrary, 330, 46, true);
        MenuChromeTheme.StylePrimaryCta(singleplayer, 16, serif: true);
        rows.AddChild(singleplayer);
        var modActions = ModFrontendMenu.Create(_modLoader.FrontendActions, id =>
        {
            _modLoader.TryRunFrontendAction(id, out var note);
            GD.Print("[mod action] " + Tr(note));
            OpenScenarioLibrary();
            ShowModFrontendResult(note);
        });
        modActions.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
        modActions.OffsetLeft = 24; modActions.OffsetRight = 214;
        modActions.OffsetTop = -74; modActions.OffsetBottom = -42;
        _menuLayer.AddChild(modActions);

        var loadLatest = ActionButton("Load latest campaign", LoadLatestSave, 330, 40);
        MenuChromeTheme.StyleWellButton(loadLatest, selected: false, fontSize: 14);
        rows.AddChild(loadLatest);

        var multiplayer = ActionButton("Multiplayer · LAN / IP", OpenNetworkMultiplayer, 330, 40);
        MenuChromeTheme.StyleWellButton(multiplayer, selected: false, fontSize: 14);
        multiplayer.TooltipText = Ui.Tip(Tr("Host or join a campaign over LAN, by IPv4 address, or through a virtual LAN such as Radmin VPN."));
        rows.AddChild(multiplayer);

        _mainProjectionButton = ActionButton("Map view: 3D globe", ToggleProjection, 330, 38);
        MenuChromeTheme.StyleWellButton(_mainProjectionButton, selected: false, fontSize: 13);
        _mainProjectionButton.TooltipText = Ui.Tip(Tr("Switch between globe and classic infinitely wrapping map (F2)."));
        rows.AddChild(_mainProjectionButton);
        _smoothingButton = ActionButton(SmoothingButtonLabel(), ToggleMapSmoothing, 330, 38);
        MenuChromeTheme.StyleWellButton(_smoothingButton, selected: false, fontSize: 13);
        _smoothingButton.TooltipText = Ui.Tip(
            Tr("Smoothing rebuilds country, region and terrain boundaries so the map has no visible " +
            "texel grid. Turn it off for the raster exactly as authored - pixels and all - which " +
            "is also the cheaper picture on a slower machine (F3)."));
        rows.AddChild(_smoothingButton);
        var settingsBtn = ActionButton("Settings", ShowSettings, 330, 38);
        MenuChromeTheme.StyleWellButton(settingsBtn, selected: false, fontSize: 13);
        rows.AddChild(settingsBtn);
        _languageButton = ActionButton(LanguageButtonLabel(), CycleLanguage, 330, 38);
        MenuChromeTheme.StyleWellButton(_languageButton, selected: false, fontSize: 13);
        _languageButton.TooltipText = Ui.Tip(Tr("Cycle the interface language. Mods add their own by dropping localization/<language>.json into the mod folder."));
        rows.AddChild(_languageButton);
        var quitBtn = ActionButton("Quit to desktop", () => LeaveConfirmation.QuitToDesktop(this, inCampaign: false), 330, 38);
        MenuChromeTheme.StyleWellButton(quitBtn, selected: false, fontSize: 13);
        rows.AddChild(quitBtn);

        _navigationHint = Title(
            Platform.PlatformProfile.Current.IsTouchPrimary
                ? "mobile.override.map_controls"
                : "Right-drag to turn the globe · wheel to zoom · WASD to move.",
            12, MenuChromeTheme.PaperFaint);
        _navigationHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_navigationHint);
        var version = Title(GameBuildInfo.VersionLabel, 12, MenuChromeTheme.PaperFaint);
        MenuChromeTheme.StyleExistingLabel(version, 12, MenuChromeTheme.PaperFaint, tabular: true);
        version.Name = "GameVersion"; version.HorizontalAlignment = HorizontalAlignment.Right;
        version.MouseFilter = Control.MouseFilterEnum.Ignore; version.ClipText = true;
        version.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        version.OffsetLeft = 24; version.OffsetRight = -24;
        version.OffsetTop = -34; version.OffsetBottom = -12;
        _menuLayer.AddChild(version);
    }

    /// <summary>
    /// Shows the standalone main menu scene, rather than building a panel over the game as it
    /// used to do - the old menu was a panel over a world that carried on existing.
    /// </summary>
    private void ShowMainMenu()
    {
        MainMenuScene.ReturnTo(GetTree());
    }

    /// <summary>The panel the campaign scene shows when it is opened without an intent.</summary>
    private void ShowLegacyMenuPanel()
    {
        if (_pauseMenuLayer is not null) _pauseMenuLayer.Visible = false;
        if (_eventLayer is not null) _eventLayer.Visible = false;
        if (_campaign is not null) _campaign.IsPaused = true;
        if (_pause is not null) _pause.Text = Tr("▶");
        _hudLayer.Visible = false;
        if (IsInstanceValid(_pickerLayer)) _pickerLayer.Visible = false;
        _menuLayer.Visible = true;
        _sidePanel.Visible = false;
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        if (_hotseatHud is not null) _hotseatHud.Visible = false;
        if (_handoverLayer is not null) _handoverLayer.Visible = false;
        if (_networkLayer is not null) _networkLayer.Visible = false;
        if (_networkHud is not null) _networkHud.Visible = false;
        if (_sovereignFinanceLayer is not null) _sovereignFinanceLayer.Visible = false;
    }

    /// <summary>
    /// Both halves are localised: the word, through the ordinary lookup, and the language's
    /// own name, which every table gives in that language ("Русский", not "Russian").
    /// </summary>
    private static string LanguageButtonLabel() =>
        $"{Tr("Language")}: {Simulation.Loc.DisplayName(Simulation.Loc.Language)}";

    /// <summary>
    /// Steps through every language that actually has a table loaded, base game or mod.
    /// </summary>
    private string SmoothingButtonLabel() =>
        Tr(_globe is null || _globe.MapSmoothing ? "Map smoothing: on" : "Map smoothing: off");

    private void ToggleMapSmoothing()
    {
        if (_globe is null) return;
        _globe.MapSmoothing = !_globe.MapSmoothing;
        if (_smoothingButton is not null && GodotObject.IsInstanceValid(_smoothingButton))
        {
            // Written straight onto the button rather than through the localisation sweep,
            // for the same reason the language button is: the caption is built from a state
            // rather than from a source string, so a generic refresh would overwrite it.
            var label = Tr(SmoothingButtonLabel());
            _smoothingButton.Text = label;
            PlayerSettings.Set("map_smoothing", _globe.MapSmoothing);
            _smoothingButton.SetMeta(LocAppliedMeta, label);
        }
    }

    private void CycleLanguage()
    {
        var languages = Simulation.Loc.Languages;
        if (languages.Count <= 1) return;

        var index = languages.ToList().FindIndex(item =>
            string.Equals(item, Simulation.Loc.Language, StringComparison.OrdinalIgnoreCase));
        Simulation.Loc.Language = languages[(index + 1) % languages.Count];

        RefreshLocalisedText();

        // Written after the sweep, not before: this caption is built from the language's
        // own name rather than from a source string, so the generic refresh cannot
        // produce it and would otherwise overwrite it with the English it was created as.
        if (_languageButton is not null && GodotObject.IsInstanceValid(_languageButton))
        {
            var label = LanguageButtonLabel();
            _languageButton.Text = label;
            _languageButton.SetMeta(LocAppliedMeta, label);
        }
    }
}
