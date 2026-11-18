using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;
using DealOrForce.Modding;

namespace DealOrForce;

/// <summary>
/// Which door the player came through. Set by the menu, read once by the campaign.
///
/// A static rather than an autoload or a signal because it is one enum crossing one scene
/// change, and it is read and cleared in the same frame the campaign comes up.
/// </summary>
public enum MenuIntent
{
    /// <summary>Nothing was asked for - the campaign scene was opened on its own.</summary>
    None,
    NewCampaign,
    LoadLatest,

    /// <summary>One particular save, chosen from the archive on this screen: <see cref="MainMenuScene.WantedSlot"/>.</summary>
    LoadSlot,
    Multiplayer,
    Settings,
    ModAction,

    /// <summary>The scenario library and its editors, straight from the front door.</summary>
    Scenarios
}

/// <summary>
/// The front door.
///
/// The main menu used to be a panel drawn on top of a running, paused campaign: the game
/// built the whole world - thirty-eight thousand provinces, two hundred and sixty-one
/// countries, every raster - and only then had anything to show you. Pressing "quit to menu"
/// did not put the world down, it made the menu visible again over it.
///
/// This is a scene of its own with nothing behind it, so it comes up immediately, and the
/// world is built when a player asks for one. What they came for travels across the scene
/// change as a <see cref="MenuIntent"/>, which the campaign reads once on the way up.
///
/// Behind the column of buttons stands a small live planet (<see cref="MenuGlobe"/>): one ball
/// and one picture of the world baked from the game's own map, turning slowly and answering
/// the hand. It is not the campaign's globe and loads none of its rasters. Where its pictures
/// are missing the menu falls back to a still, and where that is missing too, to a drawn
/// graticule (<see cref="AtlasBackdrop"/>) that needs no file at all.
/// </summary>
public partial class MainMenuScene : Control
{
    /// <summary>Where the memory is (MemoryReport), for tools that drive the front door.</summary>
    public string MemoryReport(int top = 25) => DealOrForce.MemoryReport.Build(top);

    /// <summary>What the player asked for, read and cleared by the campaign scene.</summary>
    public static MenuIntent Wanted = MenuIntent.None;

    /// <summary>Which save, when the intent is <see cref="MenuIntent.LoadSlot"/>.</summary>
    public static string WantedSlot = "";
    public static string WantedModAction = "";
    private readonly ModLoader _menuMods = new();
    private MenuButton _modActions;

    private const string CampaignScene = "res://Scenes/Main.tscn";
    private const string MenuScene = "res://Scenes/MainMenu.tscn";

    private Button _language;

    /// <summary>The menu itself, the credits and the options - one visible at a time.</summary>
    private PanelContainer _menu;
    private PanelContainer _creditsPanel;
    private Control _credits;
    private PanelContainer _settingsPanel;
    private Control _settings;
    private PanelContainer _archivePanel;
    private Control _archive;
    private TextureRect _logoRect;
    private VBoxContainer _archiveRows;
    private VBoxContainer _settingsRows;
    private readonly Dictionary<string, Button> _settingsTabs = new(StringComparer.OrdinalIgnoreCase);
    private string _settingsCategory = "";

    public override void _Ready()
    {
        Name = "MainMenu";
        ApplicationExit.Ensure(this);
        WrapGuard.Install(GetTree());
        ButtonMotion.Install(GetTree());
        TooltipKeeper.Install(GetTree());
        ControlSkin.Apply();
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // No campaign here: a campaign option set from this screen is where new ones start.
        SettingsCatalog.CurrentCampaign = null;

        // The catalogues this screen needs and nothing else.
        //
        // `Loc` and `InterfaceSkin` load themselves the first time anybody asks, so the words
        // and the palette arrive on their own. Pictures and typefaces do not - the mod loader
        // is what normally calls these, and the mod loader lives in the campaign scene, which
        // is the whole point of this one. Two catalogue reads is a very different thing from
        // building a world.
        SpriteCatalog.Load();
        Input.MouseMode = Input.MouseModeEnum.Visible;
        GameCursorService.Ensure(this).Refresh();
        FontCatalog.Load();
        if (PresentationFonts.For("ui.default") is { } face) AddThemeFontOverride("font", face);

        // And the language the player chose last time. The preferences live in the one game
        // folder under Documents, and nothing has told the store where that is yet - the
        // campaign scene is what normally does it, and it is not running.
        GamePaths.Prepare();
        PlayerSettings.UseDirectory(GamePaths.Settings);
        var chosen = SettingsCatalog.Chosen("language");
        if (chosen.Length > 0) Loc.Language = chosen;

        // And the music, which this screen had none of: the player for it used to live in the
        // campaign scene, so the menu theme only began once a world had been built. It lives
        // above the scenes now and carries on across them (MusicDirector). The catalogue is read
        // once - a campaign that has loaded mods has read it with their tracks, and coming back
        // here should not forget them.
        if (MusicCatalog.Count == 0) MusicCatalog.Load();
        MusicDirector.Ensure(this).FollowScene(this, () => MusicCatalog.Moment.Menu);

        // The frame limit the player chose. It used to be applied only when the setting was
        // changed or the window lost focus, so a menu opened fresh ran uncapped - harmless over
        // a still picture, a hot graphics card over a turning planet.
        Engine.MaxFps = Math.Max(0, (int)MathF.Round(SettingsCatalog.Number("max_fps")));

        // The world itself, behind everything: a live planet wearing the game's own map as it
        // stands on the first day - no badges, no stars, no names, so it reads the same in
        // every language. The menu used to be a column of eight equal buttons on a black
        // sheet, which says nothing about what is being started; this says it before a word is
        // read. The drawn graticule is only laid when there is no planet to cover it: under an
        // opaque world it was redrawn every frame for nobody.
        if (!MenuGlobe.Available()) AddChild(new AtlasBackdrop { MouseFilter = MouseFilterEnum.Ignore });
        AddChild(new MenuWorld());
        // The illustrations over the drawn ground, dimmed so the grid and the logo still lead.
        AddChild(new MenuIllustrations());

        // One column on the left, anchored to the screen's edge and its middle rather than to
        // pixel positions, so it sits the same on a wide monitor and on a phone held sideways.
        var panel = new PanelContainer { Name = "MenuCommandColumn", ClipContents = true };
        MenuChromeTheme.StyleArchitecturalPlate(panel, brassCorners: true, padX: 24, padY: 18);
        panel.AnchorLeft = 0f;
        panel.AnchorRight = 0f;
        panel.AnchorTop = 0f;
        panel.AnchorBottom = 0f;
        AddChild(panel);
        _menu = panel;

        var rows = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        rows.AddThemeConstantOverride("separation", 8);
        panel.AddChild(rows);

        // The thumbnail's parchment plaque supplies its own edge. Do not put a second
        // rectangular paper/gold frame behind its transparent worn perimeter.
        _logoRect = new TextureRect
        {
            Name = "MenuGameLogo", Texture = BrandAssets.GamePlaque(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(0, 196),
            MouseFilter = MouseFilterEnum.Ignore
        };
        rows.AddChild(_logoRect);

        rows.AddChild(MenuCaption("Choose a world. Shape its future.", 15, MenuChromeTheme.PaperDim, serif: true));
        rows.AddChild(MenuChromeTheme.CreateEngravedRule("CommandHeaderRule", 3f));

        // What a player came for is the first, lit button - and that depends on who they are.
        // Somebody with a campaign on disk came back to it, so "Continue" leads and a new game
        // steps down to an ordinary row; somebody with no saves has exactly one thing to do.
        // The old pair was "Singleplayer" (always lit) and "Load latest campaign" (never lit),
        // which made a returning player read two labels to find the one they wanted.
        // Only the names of the files are looked at: reading their headers is the archive's job.
        //
        // Three weights, because eight equal rows made the player read all eight: the one lit
        // action, then the other ways into a game, then - smaller and two to a row - the things
        // that are not playing.
        var saveRoot = DealOrForceGame.ResolveSaveRoot();
        var hasSaves = System.IO.Directory.Exists(saveRoot)
            && System.IO.Directory.EnumerateFiles(saveRoot, "*.bdfsave*").Any();
        if (hasSaves) rows.AddChild(MenuAction("Continue", () => Enter(MenuIntent.LoadLatest), 52, true));
        rows.AddChild(MenuAction("New campaign", () => Enter(MenuIntent.NewCampaign), hasSaves ? 42 : 52, !hasSaves));
        rows.AddChild(MenuAction("Load campaign", ShowArchive, 42));
        // Scenarios and their editors are what this game is built on; "New campaign" goes
        // straight to the country picker (one built-in scenario), so they get their own door
        // here instead of hiding behind a button on the picker's bar.
        rows.AddChild(MenuAction("Scenarios / editor", () => Enter(MenuIntent.Scenarios), 42));
        rows.AddChild(MenuAction("Multiplayer · LAN / IP", () => Enter(MenuIntent.Multiplayer), 42));

        rows.AddChild(MenuChromeTheme.CreateEngravedRule("CommandUtilityRule", 3f));
        var quiet = new GridContainer { Columns = 2 };
        quiet.AddThemeConstantOverride("h_separation", 8);
        quiet.AddThemeConstantOverride("v_separation", 8);
        rows.AddChild(quiet);
        _language = MenuAction(LanguageLabel(), CycleLanguage, 36);
        foreach (var button in new[]
                 {
                     MenuAction("Settings", ShowSettings, 36),
                     _language,
                     MenuAction("Credits", ShowCredits, 36),
                     MenuAction("Quit to desktop", () => LeaveConfirmation.QuitToDesktop(this, inCampaign: false), 36)
                 })
        {
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.AddThemeFontSizeOverride("font_size", 13);
            button.ClipText = true;
            quiet.AddChild(button);
        }

        RefreshModFrontendMenu();
        _modActions = ModFrontendMenu.Create(_menuMods.FrontendActions, id =>
        { WantedModAction = id; Enter(MenuIntent.ModAction); });
        _modActions.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        _modActions.OffsetLeft = 24; _modActions.OffsetRight = 214;
        _modActions.OffsetTop = -74; _modActions.OffsetBottom = -42;
        AddChild(_modActions);

        BuildCopyright();
        BuildCredits();
        BuildSettings();
        BuildArchive();
        Resized += LayoutMainMenuChrome;
        LayoutMainMenuChrome();
        StartupIntro.AttachIfNeeded(this);
        ExportVerification.AttachIfRequested(this);
    }

    /// <summary>
    /// Every saved campaign, from the front door.
    ///
    /// Loading used to be two things from here: the newest save, or starting a campaign in
    /// order to reach the archive inside it - which built a whole world, all thirty-eight
    /// thousand provinces of it, only to throw it away for the one being loaded. The list is
    /// read straight from the save folder, which is only files and needs no world at all, and
    /// the world that gets built is the one that was asked for.
    /// </summary>
    private void BuildArchive()
    {
        var veil = new ColorRect
        {
            Color = MenuChromeTheme.VeilColor,
            MouseFilter = MouseFilterEnum.Stop,
            Visible = false
        };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(veil);
        _archive = veil;

        var panel = new PanelContainer { ClipContents = true };
        MenuChromeTheme.StyleArchitecturalPlate(panel, brassCorners: true, padX: 26, padY: 20);
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.AnchorLeft = panel.AnchorTop = 0.5f;
        panel.AnchorRight = panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -400;
        panel.OffsetRight = 400;
        panel.OffsetTop = -310;
        panel.OffsetBottom = 310;
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        veil.AddChild(panel);
        _archivePanel = panel;

        var frame = new VBoxContainer();
        frame.AddThemeConstantOverride("separation", 10);
        panel.AddChild(frame);

        var title = MenuCaption("CAMPAIGN ARCHIVE", 24, MenuChromeTheme.Paper, serif: true);
        title.HorizontalAlignment = HorizontalAlignment.Left;
        frame.AddChild(title);
        frame.AddChild(MenuChromeTheme.CreateEngravedRule("ArchiveHeaderRule", 1f));

        var well = new PanelContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true
        };
        well.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(12, 10));
        frame.AddChild(well);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        well.AddChild(scroll);
        _archiveRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _archiveRows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_archiveRows);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 10);
        frame.AddChild(actions);
        var back = MenuAction("Back", HideArchive, 40);
        back.CustomMinimumSize = new Vector2(130, 40);
        actions.AddChild(back);
        var folder = MenuAction("Open folder", () => OS.ShellOpen(DealOrForceGame.ResolveSaveRoot()), 40);
        folder.CustomMinimumSize = new Vector2(170, 40);
        actions.AddChild(folder);
    }

    private void ShowArchive()
    {
        if (_archive is null) return;
        foreach (var child in _archiveRows.GetChildren()) child.QueueFree();

        var saves = SaveSystem.List(DealOrForceGame.ResolveSaveRoot());
        if (saves.Count == 0)
            _archiveRows.AddChild(MenuCaption("No campaigns saved yet.", 14, MenuChromeTheme.PaperDim));
        for (var i = 0; i < saves.Count; i++)
        {
            var save = saves[i];
            var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var bgTop = i % 2 == 0 ? MenuChromeTheme.BodyTop : MenuChromeTheme.BodyBottom;
            var bgBot = i % 2 == 0 ? MenuChromeTheme.BodyBottom : MenuChromeTheme.WellBottom;
            card.AddThemeStyleboxOverride("panel", MenuChromeTheme.GradientPlateBox(
                bgTop, bgBot, MenuChromeTheme.RuleLight,
                innerTopLine: new Color(MenuChromeTheme.Highlight, 0.05f),
                innerBottomLine: MenuChromeTheme.RuleDark,
                brassEdge: null, borderThickness: 1, padLeft: 12, padTop: 8, padRight: 12, padBottom: 8));
            _archiveRows.AddChild(card);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            card.AddChild(row);

            var titleText = save.Slot switch
            {
                SaveSystem.AutoSlot => Loc.Get("AUTOSAVE"),
                SaveSystem.QuickSlot => Loc.Get("QUICKSAVE"),
                _ => string.IsNullOrWhiteSpace(save.DisplayName) ? save.Slot : save.DisplayName
            };
            var textCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            textCol.AddThemeConstantOverride("separation", 2);
            row.AddChild(textCol);

            var heading = new Label
            {
                Text = titleText,
                AutowrapMode = TextServer.AutowrapMode.Off,
                ClipText = true
            };
            MenuChromeTheme.StyleExistingLabel(heading, 15, MenuChromeTheme.Paper, serif: true);
            textCol.AddChild(heading);

            var details = new Label
            {
                Text = Loc.Format("{0} · {1} · {2} · {3} KiB", save.NationName, save.GameDate,
                    save.SavedUtc.ToLocalTime().ToString("g"), (save.CompressedBytes / 1024f).ToString("0")),
                AutowrapMode = TextServer.AutowrapMode.Off,
                ClipText = true
            };
            MenuChromeTheme.StyleExistingLabel(details, 12, MenuChromeTheme.PaperFaint, tabular: true);
            textCol.AddChild(details);

            var slot = save.Slot;
            var load = MenuAction("Load", () =>
            {
                WantedSlot = slot;
                Enter(MenuIntent.LoadSlot);
            }, 36, true);
            load.CustomMinimumSize = new Vector2(114, 36);
            row.AddChild(load);
        }

        if (_menu is not null) _menu.Visible = false;
        _archive.Visible = true;
    }

    private void HideArchive()
    {
        if (_archive is not null) _archive.Visible = false;
        if (_menu is not null) _menu.Visible = true;
    }

    /// <summary>
    /// The mark in the bottom corner.
    ///
    /// It sits on the screen rather than in the menu panel, and low and quiet rather than
    /// under the logo, because that is where a copyright line goes and being where it is
    /// expected is most of what it does. The year is the year this is running in, so nobody
    /// has to remember to change it in January.
    /// </summary>
    private void BuildCopyright()
    {
        var mark = new Label
        {
            Text = Loc.Format("© {0} Bayte Realization",
                DateTime.Now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        MenuChromeTheme.StyleExistingLabel(mark, 12, MenuChromeTheme.PaperFaint, tabular: true);
        mark.SetAnchorsPreset(LayoutPreset.BottomLeft);
        mark.GrowHorizontal = GrowDirection.End;
        mark.GrowVertical = GrowDirection.Begin;
        mark.OffsetLeft = 24;
        mark.OffsetRight = 360;
        mark.OffsetTop = -34;
        mark.OffsetBottom = -12;
        AddChild(mark);
        var version = MenuCaption(GameBuildInfo.VersionLabel, 12, MenuChromeTheme.PaperFaint);
        MenuChromeTheme.StyleExistingLabel(version, 12, MenuChromeTheme.PaperFaint, tabular: true);
        version.Name = "GameVersion"; version.HorizontalAlignment = HorizontalAlignment.Right;
        version.MouseFilter = MouseFilterEnum.Ignore; version.ClipText = true;
        version.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        version.OffsetLeft = 390; version.OffsetRight = -24;
        version.OffsetTop = -34; version.OffsetBottom = -12;
        AddChild(version);
    }

    /// <summary>
    /// Who made this.
    ///
    /// Four lines and a way back, which is all a credits screen for a game with one person
    /// in it can honestly be. The last line is the interesting one: this game was written
    /// with an AI and says so on its own credits screen rather than in a disclosure
    /// somebody has to go looking for. It is the truth, it is going to be asked about
    /// anyway, and answering it first is worth more than being asked.
    /// </summary>
    private void BuildCredits()
    {
        var veil = new ColorRect
        {
            Color = MenuChromeTheme.VeilColor,
            MouseFilter = MouseFilterEnum.Stop,
            Visible = false
        };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(veil);
        _credits = veil;

        var panel = new PanelContainer { ClipContents = true };
        MenuChromeTheme.StyleArchitecturalPlate(panel, brassCorners: true, padX: 28, padY: 24);
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.AnchorLeft = panel.AnchorRight = 0.5f;
        panel.AnchorTop = panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -240;
        panel.OffsetRight = 240;
        panel.OffsetTop = -215;
        panel.OffsetBottom = 215;
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        veil.AddChild(panel);
        _creditsPanel = panel;

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 10);
        panel.AddChild(rows);

        var heading = MenuCaption("brand.title", 26, MenuChromeTheme.Paper, serif: true, bold: true);
        heading.HorizontalAlignment = HorizontalAlignment.Left;
        rows.AddChild(heading);
        rows.AddChild(MenuChromeTheme.CreateEngravedRule("CreditsRule", 2f));
        rows.AddChild(LeftCaption("Made by", 12, MenuChromeTheme.PaperFaint));
        rows.AddChild(LeftCaption("Bayte Realization (Kamil Suleymanov)", 18, MenuChromeTheme.Paper, serif: true));

        // The line the summary above promises, which had gone missing: the screen said who
        // and never said how. Plain, and in the game itself rather than in a readme.
        rows.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        rows.AddChild(LeftCaption("How it was made", 12, MenuChromeTheme.PaperFaint));
        var made = LeftCaption("credits.made_with_ai", 13, MenuChromeTheme.PaperDim);
        made.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        made.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rows.AddChild(made);

        // Expanding, with a floor under it: the expansion pushes the way back to the
        // bottom of the panel, and the floor stops it sitting flush against the last line
        // if the panel is ever shorter than its contents.
        rows.AddChild(new Control
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 14)
        });
        rows.AddChild(MenuAction("Back", HideCredits, 40, primary: true));
    }

    /// <summary>
    /// The options, here rather than in the campaign.
    ///
    /// This used to change scene, and the campaign scene builds a world: two hundred and
    /// sixty-one countries and thirty-eight thousand provinces, so that somebody could turn
    /// the music down - and then it left them looking at a globe with no way back.
    ///
    /// The rows are the campaign's rows, from <see cref="SettingsRows"/>. Only the frame is
    /// local, and it is the menu's own quiet frame rather than the dossier chrome the
    /// campaign draws over the map, because there is no map here to draw it over.
    /// </summary>
    private void BuildSettings()
    {
        var veil = new ColorRect
        {
            Color = MenuChromeTheme.VeilColor,
            MouseFilter = MouseFilterEnum.Stop,
            Visible = false
        };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(veil);
        _settings = veil;

        var panel = new PanelContainer { ClipContents = true };
        MenuChromeTheme.StyleArchitecturalPlate(panel, brassCorners: true, padX: 26, padY: 20);
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.AnchorLeft = panel.AnchorTop = 0.5f;
        panel.AnchorRight = panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -470;
        panel.OffsetRight = 470;
        panel.OffsetTop = -330;
        panel.OffsetBottom = 330;
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        veil.AddChild(panel);
        _settingsPanel = panel;

        var frame = new VBoxContainer();
        frame.AddThemeConstantOverride("separation", 10);
        panel.AddChild(frame);

        var title = MenuCaption("SETTINGS", 24, MenuChromeTheme.Paper, serif: true);
        title.HorizontalAlignment = HorizontalAlignment.Left;
        frame.AddChild(title);
        frame.AddChild(MenuChromeTheme.CreateEngravedRule("SettingsHeaderRule", 1f));

        var tabs = new HFlowContainer();
        tabs.AddThemeConstantOverride("h_separation", 6);
        tabs.AddThemeConstantOverride("v_separation", 6);
        frame.AddChild(tabs);
        var categories = DealOrForce.Modding.LauncherSelection.IsEditorRun
            ? SettingsCatalog.Categories().Append("editor_mods") : SettingsCatalog.Categories();
        foreach (var category in categories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var captured = category;
            var tab = new Button
            {
                Text = SettingsRows.CategoryName(category),
                CustomMinimumSize = new Vector2(138, 32)
            };
            // Marked like every other caption on this screen, so switching language from the
            // menu button relabels the tabs too. Without it they kept whichever language the
            // screen was built in while everything around them changed.
            tab.SetMeta(SourceMeta, SettingsRows.CategorySource(category));
            tab.Pressed += () => ShowSettingsCategory(captured);
            MenuChromeTheme.StyleTabButton(tab, active: false, fontSize: 13);
            tabs.AddChild(tab);
            _settingsTabs[category] = tab;
        }

        var bodyWell = new PanelContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true
        };
        bodyWell.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(14, 10));
        frame.AddChild(bodyWell);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        bodyWell.AddChild(scroll);
        _settingsRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _settingsRows.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(_settingsRows);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 10);
        frame.AddChild(actions);
        var back = MenuAction("Back", HideSettings, 40, primary: true);
        back.CustomMinimumSize = new Vector2(140, 40);
        actions.AddChild(back);
        var reset = MenuAction("Reset this page", () =>
        {
            if (_settingsCategory == "editor_mods")
            {
                EditorModRows.Reset();
                RefreshModFrontendMenu();
            }
            else SettingsRows.ResetCategory(_settingsCategory, ApplyMenuSetting);
            ShowSettingsCategory(_settingsCategory);
        }, 40);
        reset.CustomMinimumSize = new Vector2(180, 40);
        actions.AddChild(reset);
    }

    private void ShowSettings()
    {
        SettingsRows.RefreshLanguageChoices();
        if (_settingsCategory.Length == 0)
            _settingsCategory = SettingsCatalog.Categories().FirstOrDefault() ?? "game";
        if (_menu is not null) _menu.Visible = false;
        if (_credits is not null) _credits.Visible = false;
        if (_settings is not null) _settings.Visible = true;
        ShowSettingsCategory(_settingsCategory);
    }

    private void HideSettings()
    {
        if (_settings is not null) _settings.Visible = false;
        if (_menu is not null) _menu.Visible = true;
    }

    private void ShowSettingsCategory(string category)
    {
        _settingsCategory = category;
        foreach (var (id, tab) in _settingsTabs)
            MenuChromeTheme.StyleTabButton(tab, string.Equals(id, category, StringComparison.OrdinalIgnoreCase), 13);

        foreach (var child in _settingsRows.GetChildren()) child.QueueFree();
        if (category == "editor_mods" && DealOrForce.Modding.LauncherSelection.IsEditorRun)
        {
            EditorModRows.Build(_settingsRows, false, RefreshModFrontendMenu);
            return;
        }
        foreach (var definition in SettingsCatalog.In(category))
            SettingsRows.Build(definition, _settingsRows, ApplyMenuSetting);
    }

    /// <summary>
    /// What a changed option does with no campaign behind it.
    ///
    /// Deliberately short. Most of what a setting DOES needs a world to do it to - an
    /// autosave interval, the length of a step, the country labels on a globe - and none of
    /// those exist yet. Every one of them is still WRITTEN, to the same file the campaign
    /// reads, and the campaign applies the lot the moment it starts. What is handled here is
    /// only what can be seen from this screen.
    /// </summary>
    private void ApplyMenuSetting(string id)
    {
        if (id.StartsWith(CountryNames.VariantPrefix, StringComparison.OrdinalIgnoreCase))
        {
            CountryNames.Refresh();
            return;
        }

        switch (id.ToLowerInvariant())
        {
            case "language":
                var language = SettingsCatalog.Chosen("language");
                if (language.Length > 0 &&
                    !string.Equals(language, Loc.Language, StringComparison.OrdinalIgnoreCase))
                {
                    Loc.Language = language;
                    Relabel(this);
                    if (_language is not null) _language.Text = LanguageLabel();
                    foreach (var (category, tab) in _settingsTabs)
                        tab.Text = SettingsRows.CategoryName(category);
                    ShowSettingsCategory(_settingsCategory);
                }
                break;

            case "window_mode":
            case "window_size":
                DealOrForceGame.ApplyWindowMode(SettingsCatalog.Chosen("window_mode"));
                break;

            case "vsync":
                DisplayServer.WindowSetVsyncMode(SettingsCatalog.Flag("vsync")
                    ? DisplayServer.VSyncMode.Enabled
                    : DisplayServer.VSyncMode.Disabled);
                break;

            case "max_fps":
                Engine.MaxFps = Math.Max(0, (int)MathF.Round(SettingsCatalog.Number("max_fps")));
                break;

            case "renderer":
                GraphicsRenderer.Choose(this, SettingsCatalog.Chosen("renderer"));
                break;

            case "interface_scale":
                if (GetWindow() is { } window)
                    window.ContentScaleFactor =
                        Math.Clamp(SettingsCatalog.Number("interface_scale"), 0.5f, 2.5f);
                break;

            case "tooltip_width":
                Ui.TooltipMeasure = Math.Clamp((int)MathF.Round(SettingsCatalog.Number("tooltip_width")), 32, 200);
                break;

            // The menu has music now, so its sliders are heard from here.
            case "volume_master":
            case "volume_music":
                MusicDirector.Ensure(this).RefreshVolume();
                break;
        }
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationApplicationFocusOut)
        {
            var chosen = SettingsCatalog.Chosen("background_fps");
            if (int.TryParse(chosen, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var bgFps) && bgFps > 0)
                Engine.MaxFps = bgFps;
        }
        else if (what == NotificationApplicationFocusIn)
        {
            Engine.MaxFps = Math.Max(0, (int)MathF.Round(SettingsCatalog.Number("max_fps")));
        }
    }

    private void ShowCredits()
    {
        if (_credits is null) return;
        _credits.Visible = true;
        if (_menu is not null) _menu.Visible = false;
    }

    private void HideCredits()
    {
        if (_credits is not null) _credits.Visible = false;
        if (_menu is not null) _menu.Visible = true;
    }

    /// <summary>
    /// The same caption watch the campaign runs, over the menu and the settings screen it
    /// now opens inside itself. Debug builds only - see <see cref="LayoutAudit.Watch"/>.
    /// </summary>
    public override void _Process(double delta) => LayoutAudit.Watch(this, delta);

    /// <summary>Escape closes whatever is open over the menu, the way it does everywhere.</summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (!@event.IsActionPressed("ui_cancel")) return;
        if (_settings is { Visible: true }) HideSettings();
        else if (_archive is { Visible: true }) HideArchive();
        else if (_credits is { Visible: true }) HideCredits();
        else return;
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Go and be that. The world is built on the other side of this call.</summary>
    private void Enter(MenuIntent intent)
    {
        if (intent != MenuIntent.ModAction) WantedModAction = "";
        Wanted = intent;
        GetTree().ChangeSceneToFile(CampaignScene);
    }

    /// <summary>
    /// Leave a campaign and come back here. Called by the campaign scene rather than by
    /// anything on this screen, so the two halves agree on one path between them.
    /// </summary>
    public static void ReturnTo(SceneTree tree)
    {
        Wanted = MenuIntent.None;
        WantedSlot = "";
        WantedModAction = "";
        tree?.ChangeSceneToFile(MenuScene);
    }

    private void CycleLanguage()
    {
        var languages = Loc.Languages;
        if (languages.Count == 0) return;
        var at = 0;
        for (var index = 0; index < languages.Count; index++)
            if (string.Equals(languages[index], Loc.Language, StringComparison.OrdinalIgnoreCase))
                at = index;
        var next = languages[(at + 1) % languages.Count];
        Loc.Language = next;
        PlayerSettings.Set("language", next);
        Relabel(this);
        _language.Text = LanguageLabel();
    }

    private static string LanguageLabel() =>
        Loc.Format("Language: {0}", Loc.DisplayName(Loc.Language));

    private void RefreshModFrontendMenu()
    {
        _menuMods.Discover();
        // Content captions only: no assemblies, workers or servers run on this screen.
        foreach (var package in _menuMods.Packages.Where(p => !p.IsContentPack))
            Loc.LoadDirectory(System.IO.Path.Combine(package.PhysicalRoot, "localization"), package.Errors);
        if (_modActions is not null) _modActions.Visible = _menuMods.FrontendActions().Count > 0;
    }

    /// <summary>
    /// Put every caption on this screen back into the current language.
    ///
    /// The same trick the campaign uses: each widget carries the English it was built from,
    /// so a second language switch still has something to translate from.
    /// </summary>
    private static void Relabel(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Button button && button.HasMeta(SourceMeta))
                button.Text = Loc.Get((string)button.GetMeta(SourceMeta));
            else if (child is Label label && label.HasMeta(SourceMeta))
                label.Text = Loc.Get((string)label.GetMeta(SourceMeta));
            Relabel(child);
        }
    }

    private const string SourceMeta = "loc_source";

    private static Label MenuCaption(string text, int size, Color colour, bool serif = false, bool bold = false)
    {
        var label = new Label
        {
            Text = Loc.Get(text),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off
        };
        label.SetMeta(SourceMeta, text);
        MenuChromeTheme.StyleExistingLabel(label, size, colour, serif: serif, bold: bold);
        return label;
    }

    private static Label LeftCaption(string text, int size, Color colour, bool serif = false, bool bold = false)
    {
        var label = MenuCaption(text, size, colour, serif, bold);
        label.HorizontalAlignment = HorizontalAlignment.Left;
        return label;
    }

    private Button MenuAction(string text, Action pressed, float height, bool primary = false)
    {
        var button = new Button
        {
            Text = Loc.Get(text),
            CustomMinimumSize = new Vector2(0, height)
        };
        button.SetMeta(SourceMeta, text);
        var fontSize = height >= 50 ? 18 : height >= 42 ? 14 : 13;
        if (primary)
            MenuChromeTheme.StylePrimaryCta(button, fontSize, serif: true);
        else
            MenuChromeTheme.StyleWellButton(button, selected: false, fontSize: fontSize);
        button.Pressed += pressed;
        return button;
    }

    private void LayoutMainMenuChrome()
    {
        var view = Size.X > 100 && Size.Y > 100 ? Size : GetViewportRect().Size;
        if (_menu is not null)
        {
            var left = Mathf.Clamp(view.X * 0.045f, 28f, 84f);
            var width = Mathf.Clamp(view.X * 0.30f, 360f, 428f);
            var height = Mathf.Clamp(view.Y - 116f, 480f, 624f);
            var top = Mathf.Max(22f, (view.Y - 64f - height) * 0.5f);
            _menu.OffsetLeft = left;
            _menu.OffsetRight = left + width;
            _menu.OffsetTop = top;
            _menu.OffsetBottom = top + height;
            if (_logoRect is not null)
                _logoRect.CustomMinimumSize = new Vector2(0, view.Y <= 780f ? 164f : 204f);
        }

        if (_archivePanel is not null)
        {
            var halfW = Mathf.Min(410f, (view.X - 48f) * 0.5f);
            var halfH = Mathf.Min(310f, (view.Y - 48f) * 0.5f);
            _archivePanel.OffsetLeft = -halfW;
            _archivePanel.OffsetRight = halfW;
            _archivePanel.OffsetTop = -halfH;
            _archivePanel.OffsetBottom = halfH;
        }

        if (_settingsPanel is not null)
        {
            var halfW = Mathf.Min(470f, (view.X - 48f) * 0.5f);
            var halfH = Mathf.Min(330f, (view.Y - 48f) * 0.5f);
            _settingsPanel.OffsetLeft = -halfW;
            _settingsPanel.OffsetRight = halfW;
            _settingsPanel.OffsetTop = -halfH;
            _settingsPanel.OffsetBottom = halfH;
        }

        if (_creditsPanel is not null)
        {
            var halfW = Mathf.Min(240f, (view.X - 48f) * 0.5f);
            var halfH = Mathf.Min(215f, (view.Y - 48f) * 0.5f);
            _creditsPanel.OffsetLeft = -halfW;
            _creditsPanel.OffsetRight = halfW;
            _creditsPanel.OffsetTop = -halfH;
            _creditsPanel.OffsetBottom = halfH;
        }
    }
}

/// <summary>
/// The world behind the menu, darkened towards the side the menu stands on.
///
/// A live planet when its pictures are there (<see cref="MenuGlobe"/>), and otherwise the
/// still it replaced: one picture of the world, which is also what the plain loading screen
/// shows (<see cref="ShippedPath"/>). Never the campaign's map - the menu exists so that
/// nobody waits for thirty-eight thousand provinces before they can press "Settings". The
/// shade over either is drawn from the skin's own ground colour, so a reskin darkens it in
/// its own hue.
/// </summary>
public partial class MenuWorld : Control
{
    public const string ShippedPath = "res://game/gfx/interface/brand/menu_world.png";

    /// <summary>How much of the picture's light survives. The menu must lead, not the map.</summary>
    private const float Brightness = 0.92f;

    private TextureRect _picture;
    private double _time;

    public override void _Ready()
    {
        Name = "MenuWorld";
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;

        if (MenuGlobe.Available())
        {
            AddChild(new MenuGlobe());
            // The planet stands clear of the column on a wide screen, so the shade is only a
            // breath there; on a narrow one the planet runs under the buttons and the shade is
            // what keeps them readable.
            AddChild(Shade(0.62f, 0.34f, 0f, 0f));
            return;
        }

        var path = SpriteCatalog.TextureOf("menu_world", ShippedPath);
        if (string.IsNullOrWhiteSpace(path) || !ResourceLoader.Exists(path)) return;
        _picture = new TextureRect
        {
            Texture = GD.Load<Texture2D>(path),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            Modulate = new Color(Brightness, Brightness, Brightness * 1.04f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        _picture.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_picture);

        AddChild(Shade(0.94f, 0.82f, 0.08f, 0.16f));
    }

    /// <summary>
    /// Dark where the menu column stands, clear where the world is: one gradient, left to
    /// right, in the skin's ground colour. The four numbers are how much of it lies at the
    /// left edge, at a quarter, past the middle and at the right edge.
    /// </summary>
    private static TextureRect Shade(float edge, float column, float world, float far)
    {
        var ground = MenuChromeTheme.BackdropColor;
        var gradient = new Gradient
        {
            Offsets = new[] { 0f, 0.26f, 0.58f, 1f },
            Colors = new[]
            {
                new Color(ground, edge), new Color(ground, column), new Color(ground, world), new Color(ground, far)
            }
        };
        var shade = new TextureRect
        {
            Name = "MenuWorldShade",
            Texture = new GradientTexture2D { Gradient = gradient, Width = 256, Height = 4 },
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return shade;
    }

    public override void _Process(double delta)
    {
        if (_picture is null) return;
        _time += delta;
        // 1.00 to 1.05 and back over ninety seconds, about the centre: slow enough not to be
        // noticed as motion, enough for the screen not to be a photograph.
        var scale = 1.025f + 0.025f * Mathf.Sin((float)(_time * Mathf.Tau / 90.0));
        _picture.PivotOffset = Size * new Vector2(0.62f, 0.5f);
        _picture.Scale = new Vector2(scale, scale);
    }
}

/// <summary>
/// The backdrop: a graticule on a dark ground, and the limb of a globe behind it.
///
/// Drawn rather than loaded. A menu background is the first thing anybody sees and the only
/// thing on screen while they decide, so it has two jobs - say what kind of game this is, and
/// then get out of the way. A slowly drifting lat/long grid does both: it is unmistakably an
/// atlas, it is four colours and no detail, and at five per cent alpha it cannot compete with
/// a logo. It also costs nothing to load, which is the entire point of taking the menu out of
/// the campaign scene.
/// </summary>
public partial class AtlasBackdrop : Control
{
    /// <summary>Degrees between grid lines. Twelve gives thirty meridians and fifteen parallels.</summary>
    private const float Spacing = 12f;

    /// <summary>Degrees of longitude the grid drifts a second. A quarter of a degree is slow.</summary>
    private const float Drift = 0.25f;

    private float _longitude;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        _longitude = (_longitude + (float)delta * Drift) % Spacing;
        _time += (float)delta;
        QueueRedraw();
    }

    /// <summary>Seconds since the menu opened. Drives the slow breathing of the backdrop.</summary>
    private float _time;

    /// <summary>
    /// Fixed star field for the corners of the frame, so the dark ground reads as a war room
    /// at night rather than as an empty rectangle. Generated once from a fixed seed - the
    /// menu is the one screen where a random twinkle would be noticed as churn.
    /// </summary>
    private static readonly Vector2[] Stars = MakeStars();

    private static Vector2[] MakeStars()
    {
        var rng = new Random(20260101);
        var stars = new Vector2[90];
        for (var i = 0; i < stars.Length; i++)
            stars[i] = new Vector2((float)rng.NextDouble(), (float)rng.NextDouble());
        return stars;
    }

    public override void _Draw()
    {
        var size = Size;
        if (size.X < 2f || size.Y < 2f) return;

        // The ground is not one colour. A flat dark rectangle makes the menu feel like
        // a dialog box on a desktop; a vertical gradient from obsidian well at the top
        // to warm dark umber towards the center puts the light source where the command column
        // stands, and the eye reads it as a chart table rather than a generic form.
        var baseColour = MenuChromeTheme.BackdropColor;
        var warmGround = MenuChromeTheme.BodyBottom;
        var bands = 24;
        for (var band = 0; band < bands; band++)
        {
            var t = band / (float)(bands - 1);
            var colour = baseColour.Lerp(warmGround, t * 0.65f);
            DrawRect(new Rect2(0, size.Y * band / bands, size.X, size.Y / bands + 1f), colour);
        }

        // A halo behind where the panel stands, so the menu sits in a pool of warm study light.
        var line = MenuChromeTheme.RuleLight;
        var glowCentre = new Vector2(size.X * 0.5f, size.Y * 0.50f);
        var glowRadius = Mathf.Min(size.X, size.Y) * 0.62f;
        var halo = MenuChromeTheme.PlateTop;
        for (var ring = 14; ring >= 1; ring--)
        {
            var t = ring / 14f;
            DrawCircle(glowCentre, glowRadius * t, new Color(halo.R, halo.G, halo.B, 0.045f * (1f - t)));
        }

        // Stars in the outer margins only. Under the panel they would be hidden anyway, and
        // crowding the centre is exactly what the gradient and the halo are there to avoid.
        var starColour = MenuChromeTheme.Paper.Lerp(MenuChromeTheme.BrassHi, 0.30f);
        foreach (var star in Stars)
        {
            var at = new Vector2(star.X * size.X, star.Y * size.Y);
            var fromCentre = at.DistanceTo(glowCentre) / glowRadius;
            if (fromCentre < 0.55f) continue;
            var twinkle = 0.5f + 0.5f * Mathf.Sin(_time * 0.7f + star.X * 40f + star.Y * 17f);
            DrawCircle(at, 1f, new Color(starColour.R, starColour.G, starColour.B, 0.09f + 0.14f * twinkle * fromCentre));
        }

        var faint = new Color(line, 0.14f);
        var marked = new Color(line, 0.32f);

        // Meridians, drifting. The prime meridian is brighter, so the drift is visible as
        // movement rather than as a shimmer.
        var step = size.X / (360f / Spacing);
        var offset = _longitude / Spacing * step;
        for (var x = -step + offset; x < size.X + step; x += step)
        {
            var prime = Mathf.Abs(Mathf.PosMod(x - offset, size.X) - size.X * 0.5f) < step * 0.5f;
            DrawLine(new Vector2(x, 0), new Vector2(x, size.Y), prime ? marked : faint, 1f);
        }

        // Parallels, which do not move, because latitude does not.
        var rows = Mathf.Max(4, (int)(180f / Spacing));
        for (var row = 0; row <= rows; row++)
        {
            var y = size.Y * row / rows;
            var equator = row == rows / 2;
            DrawLine(new Vector2(0, y), new Vector2(size.X, y), equator ? marked : faint, 1f);
        }

        // The globe, and it turns. The old backdrop drew two static arcs, which read as a
        // logo rather than as a planet; a wireframe sphere with the same meridian spacing as
        // the grid behind it reads as the thing the game is actually about. Only the near
        // hemisphere is drawn - the far side of a wireframe is clutter, and hiding it is what
        // makes the rotation legible.
        var centre = new Vector2(size.X * 0.5f, size.Y * 0.52f);
        var radius = Mathf.Min(size.X, size.Y) * 0.44f;
        var spin = _time * 0.06f;
        DrawArc(centre, radius, 0f, Mathf.Tau, 128, new Color(line, 0.26f), 1.5f, true);
        for (var meridian = 0; meridian < 12; meridian++)
        {
            var phase = spin + meridian * Mathf.Tau / 12f;
            var squash = Mathf.Cos(phase);
            if (squash < 0f) continue; // far side
            var width = radius * Mathf.Abs(squash);
            if (width < 2f) continue;
            // A meridian's projection is a full ellipse, tall as the globe and squeezed to
            // the squash - not the half-circle DrawArc alone can give, which read as arcs
            // cropped by an unseen edge the moment the menu opened. A transform squeezes a
            // drawn circle into that ellipse; reset it before anything else draws.
            DrawSetTransform(centre, 0f, new Vector2(width / radius, 1f));
            DrawArc(Vector2.Zero, radius, 0f, Mathf.Tau, 64,
                new Color(line, 0.10f + 0.12f * squash), 1f, false);
            DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        }
        for (var parallel = 1; parallel <= 4; parallel++)
        {
            var lat = parallel * 0.2f;
            var ringRadius = radius * Mathf.Cos(lat * Mathf.Pi * 0.5f);
            var ringY = centre.Y - radius * Mathf.Sin(lat * Mathf.Pi * 0.5f) * 0.85f;
            DrawArc(new Vector2(centre.X, ringY), ringRadius, 0f, Mathf.Tau, 96, new Color(line, 0.14f), 1f, true);
        }

        // Two brass hairlines, top and bottom, the same rule the dossier frames are drawn in.
        var brass = MenuChromeTheme.BrassHi;
        DrawRect(new Rect2(0, size.Y * 0.055f, size.X, 1f), new Color(brass, 0.28f));
        DrawRect(new Rect2(0, size.Y * 0.945f, size.X, 1f), new Color(brass, 0.28f));
    }
}
