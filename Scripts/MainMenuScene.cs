using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Platform;
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
    [ToolHook]
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
    private GameDiagnosticLogger _bootLogger;
    private SubViewport _compatPrewarmViewport;
    private TextureRect _compatPrewarmSink;
    private readonly List<IDisposable> _compatPrewarmResources = new();
    private int _compatPrewarmFrames;
    private static bool _compatShaderPrewarmed;

    private static bool IsMobileOrTouchPlatform =>
        Platform.PlatformProfile.Current?.IsTouchPrimary == true ||
        Platform.PlatformProfile.Current?.IsMobile == true ||
        OS.HasFeature("mobile") || OS.HasFeature("android") || OS.HasFeature("ios") ||
        OS.GetName() is "Android" or "iOS";

    private static void LogBootMilestone(string stage, string detail = "")
    {
        BootCrashDiagnostics.Step(stage, detail);
    }

    private static void LogMenuError(string stage, Exception exception)
    {
        var detail = $"{stage} failed: {exception.GetType().Name}: {exception.Message}";
        GD.PrintErr($"[menu] {detail}\n{exception}");
        BootCrashDiagnostics.RecordException($"MainMenuScene.{stage}", exception, stage, fatal: false);
    }

    private void ApplyDesktopCustomCursors()
    {
        if (IsMobileOrTouchPlatform) return;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        GameCursorService.Ensure(this).Refresh();
    }

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

        // Initialize platform profile + mobile text adapter before cursors, fonts, paths, or intro chrome.
        var initialViewHeight = Size.Y > 1f ? Size.Y : GetViewportRect().Size.Y;
        Platform.GodotPlatformBridge.EnsureInitialized(initialViewHeight > 1f ? initialViewHeight : 1080f);
        BootCrashDiagnostics.BeginSession("MainMenu",
            $"renderer={RenderingServer.GetCurrentRenderingMethod()} adapter={RenderingServer.GetVideoAdapterName()}");
        try
        {
            _bootLogger = new GameDiagnosticLogger();
            OS.AddLogger(_bootLogger);
        }
        catch
        {
            _bootLogger = null;
        }
        LogBootMilestone("main_menu_ready_start");

        // The catalogues this screen needs and nothing else.
        //
        // `Loc` and `InterfaceSkin` load themselves the first time anybody asks, so the words
        // and the palette arrive on their own. Pictures and typefaces do not - the mod loader
        // is what normally calls these, and the mod loader lives in the campaign scene, which
        // is the whole point of this one. Two catalogue reads is a very different thing from
        // building a world.
        try { SpriteCatalog.Load(); }
        catch (Exception ex) { LogMenuError("SpriteCatalog.Load", ex); }
        ApplyDesktopCustomCursors();
        try
        {
            FontCatalog.Load();
            if (PresentationFonts.For("ui.default") is { } face) AddThemeFontOverride("font", face);
        }
        catch (Exception ex) { LogMenuError("FontCatalog.Load", ex); }

        // Load preferences from the persistent game folder and apply one-time mobile defaults before reading settings.
        try
        {
            GamePaths.Prepare();
            PlayerSettings.UseDirectory(GamePaths.Settings);
            MobileFirstBootDefaults.ApplyOnce(DealOrForceGame.IsMobilePlatform);
            var chosen = SettingsCatalog.Chosen("language");
            if (chosen.Length > 0) Loc.Language = chosen;
        }
        catch (Exception ex) { LogMenuError("SettingsInit", ex); }

        // And the music, which this screen had none of: the player for it used to live in the
        // campaign scene, so the menu theme only began once a world had been built. It lives
        // above the scenes now and carries on across them (MusicDirector). The catalogue is read
        // once - a campaign that has loaded mods has read it with their tracks, and coming back
        // here should not forget them.
        try
        {
            if (MusicCatalog.Count == 0) MusicCatalog.Load();
            MusicDirector.Ensure(this).FollowScene(this, () => MusicCatalog.Moment.Menu);
        }
        catch (Exception ex) { LogMenuError("MusicInit", ex); }

        // The frame limit the player chose. It used to be applied only when the setting was
        // changed or the window lost focus, so a menu opened fresh ran uncapped - harmless over
        // a still picture, a hot graphics card over a turning planet.
        ApplyMenuMaxFps();

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
        ChromePlateArt.AttachSurfaceTexture(panel, "leather", 0.36f);

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

        var headerBox = new PanelContainer { Name = "MenuHeaderBox" };
        headerBox.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        rows.AddChild(headerBox);
        ChromePlateArt.AttachHeaderFiligree(headerBox, 0.40f);
        headerBox.AddChild(MenuCaption("Choose a world. Shape its future.", 15, MenuChromeTheme.PaperDim, serif: true));
        rows.AddChild(MenuChromeTheme.CreateEngravedRule("CommandHeaderRule", 3f));

        var hasSaves = false;
        try
        {
            var saveRoot = DealOrForceGame.ResolveSaveRoot();
            var saves = System.IO.Directory.Exists(saveRoot)
                ? SaveSystem.List(saveRoot)
                : new List<SaveMetadata>();
            hasSaves = saves.Count > 0;
            var latestSave = hasSaves ? saves[0] : null;

            if (latestSave is not null)
            {
                var heroCard = new PanelContainer { Name = "HeroContinueCard", SizeFlagsHorizontal = SizeFlags.ExpandFill };
                heroCard.AddThemeStyleboxOverride("panel", ChromePlateArt.BuildCardWrapperStyle(14, 10, 14, 10) ?? MenuChromeTheme.GradientPlateBox(
                    MenuChromeTheme.PlateHover, MenuChromeTheme.PlateBottom, MenuChromeTheme.Brass,
                    innerTopLine: new Color(MenuChromeTheme.Highlight, 0.08f), borderThickness: 1));

                var heroCol = new VBoxContainer();
                heroCol.AddThemeConstantOverride("separation", 4);
                heroCard.AddChild(heroCol);

                var heroTop = new HBoxContainer();
                heroTop.AddThemeConstantOverride("separation", 8);
                heroCol.AddChild(heroTop);

                var flagTex = !string.IsNullOrEmpty(latestSave.NationTag) ? DealOrForceGame.FlagTextureFor(latestSave.NationTag) : null;
                if (flagTex is not null)
                {
                    var flagRect = new TextureRect
                    {
                        Texture = flagTex,
                        CustomMinimumSize = new Vector2(28, 18),
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                        SizeFlagsVertical = SizeFlags.ShrinkCenter
                    };
                    heroTop.AddChild(flagRect);
                }

                var heroTitle = new Label
                {
                    Text = Loc.Get("shell.hero_continue_title"),
                    ClipText = true,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                heroTitle.SetMeta(SourceMeta, "shell.hero_continue_title");
                MenuChromeTheme.StyleExistingLabel(heroTitle, 12, MenuChromeTheme.BrassHi, serif: false, bold: true);
                heroTop.AddChild(heroTitle);

                var heroCampName = string.IsNullOrWhiteSpace(latestSave.DisplayName)
                    ? (string.IsNullOrWhiteSpace(latestSave.Slot) ? latestSave.NationName : latestSave.Slot)
                    : latestSave.DisplayName;
                var heroParts = new List<string>(3);
                if (!string.IsNullOrWhiteSpace(heroCampName)) heroParts.Add(heroCampName);
                if (!string.IsNullOrWhiteSpace(latestSave.NationName) &&
                    !heroCampName.Contains(latestSave.NationName, StringComparison.OrdinalIgnoreCase))
                    heroParts.Add(latestSave.NationName);
                if (!string.IsNullOrWhiteSpace(latestSave.GameDate) &&
                    !heroCampName.Contains(latestSave.GameDate, StringComparison.OrdinalIgnoreCase))
                    heroParts.Add(latestSave.GameDate);
                var heroSub = string.Join(" · ", heroParts);
                var heroDesc = new Label
                {
                    Text = heroSub,
                    ClipText = true
                };
                MenuChromeTheme.StyleExistingLabel(heroDesc, 13, MenuChromeTheme.Paper, serif: true);
                heroCol.AddChild(heroDesc);

                var continueBtn = MenuAction("Continue", () => Enter(MenuIntent.LoadLatest), 44, true);
                ChromePlateArt.ApplyMenuButtonIcon(continueBtn, "icon_menu_continue");
                heroCol.AddChild(continueBtn);
                rows.AddChild(heroCard);
                rows.AddChild(ChromePlateArt.CreateEngravedMedallionDivider("HeroMedallionDivider", 1f, 12f));
            }
        }
        catch (Exception ex)
        {
            hasSaves = false;
            GD.PushWarning($"[menu] Save scan failed: {ex.Message}");
        }

        var btnNew = MenuAction("New campaign", () => Enter(MenuIntent.NewCampaign), hasSaves ? 38 : 48, !hasSaves);
        ChromePlateArt.ApplyMenuButtonIcon(btnNew, "icon_menu_new");
        rows.AddChild(btnNew);

        var btnLoad = MenuAction("Load campaign", ShowArchive, 38);
        ChromePlateArt.ApplyMenuButtonIcon(btnLoad, "icon_menu_load");
        rows.AddChild(btnLoad);

        var btnScen = MenuAction("Scenarios / editor", () => Enter(MenuIntent.Scenarios), 38);
        ChromePlateArt.ApplyMenuButtonIcon(btnScen, "icon_menu_scenarios");
        rows.AddChild(btnScen);

        var btnMulti = MenuAction("Multiplayer · LAN / IP", () => Enter(MenuIntent.Multiplayer), 38);
        ChromePlateArt.ApplyMenuButtonIcon(btnMulti, "icon_menu_multiplayer");
        rows.AddChild(btnMulti);

        rows.AddChild(ChromePlateArt.CreateEngravedMedallionDivider("CommandUtilityRule", 2f, 14f));
        var quiet = new GridContainer { Columns = 2 };
        quiet.AddThemeConstantOverride("h_separation", 8);
        quiet.AddThemeConstantOverride("v_separation", 8);
        rows.AddChild(quiet);
        _language = MenuAction(LanguageLabel(), CycleLanguage, 34);
        ChromePlateArt.ApplyMenuButtonIcon(_language, "icon_menu_language");

        var btnSettings = MenuAction("Settings", ShowSettings, 34);
        ChromePlateArt.ApplyMenuButtonIcon(btnSettings, "icon_menu_settings");

        var btnCredits = MenuAction("Credits", ShowCredits, 34);
        ChromePlateArt.ApplyMenuButtonIcon(btnCredits, "icon_menu_credits");

        var btnQuit = MenuAction("Quit to desktop", () => LeaveConfirmation.QuitToDesktop(this, inCampaign: false), 34);
        ChromePlateArt.ApplyMenuButtonIcon(btnQuit, "icon_menu_quit");

        foreach (var button in new[] { btnSettings, _language, btnCredits, btnQuit })
        {
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.AddThemeFontSizeOverride("font_size", 12);
            button.ClipText = true;
            quiet.AddChild(button);
        }

        try
        {
            RefreshModFrontendMenu();
        }
        catch (Exception ex)
        {
            LogMenuError("RefreshModFrontendMenu", ex);
        }
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
        TryStartCompatShaderPrewarm();
        LogBootMilestone("main_menu_ready_done");
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
        if (Platform.PlatformProfile.Current.IsDesktopFileDialogSupported)
        {
            var folder = MenuAction("Open folder",
                () => Platform.GodotPlatformBridge.TryOpenLocalFolderSafely(DealOrForceGame.ResolveSaveRoot()), 40);
            folder.CustomMinimumSize = new Vector2(170, 40);
            actions.AddChild(folder);
        }
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
        MenuChromeTheme.StyleArchitecturalPlate(panel, brassCorners: true, padX: 28, padY: 22);
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.AnchorLeft = panel.AnchorRight = 0.5f;
        panel.AnchorTop = panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -340;
        panel.OffsetRight = 340;
        panel.OffsetTop = -250;
        panel.OffsetBottom = 250;
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        veil.AddChild(panel);
        _creditsPanel = panel;

        var rows = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        rows.AddThemeConstantOverride("separation", 10);
        panel.AddChild(rows);

        var heading = MenuCaption("brand.title", 26, MenuChromeTheme.Paper, serif: true, bold: true);
        heading.HorizontalAlignment = HorizontalAlignment.Left;
        rows.AddChild(heading);
        rows.AddChild(MenuChromeTheme.CreateEngravedRule("CreditsRule", 2f));

        var bodyWell = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true
        };
        bodyWell.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(14, 12));
        rows.AddChild(bodyWell);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        bodyWell.AddChild(scroll);

        var body = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        body.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(body);

        body.AddChild(LeftCaption("Made by", 12, MenuChromeTheme.PaperFaint));
        body.AddChild(LeftCaption("Bayte Realization (Kamil Suleymanov)", 18, MenuChromeTheme.Paper, serif: true));

        // The line the summary above promises, which had gone missing: the screen said who
        // and never said how. Plain, and in the game itself rather than in a readme.
        body.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        body.AddChild(LeftCaption("How it was made", 12, MenuChromeTheme.PaperFaint));
        var made = LeftCaption("credits.made_with_ai", 13, MenuChromeTheme.PaperDim);
        made.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        made.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        body.AddChild(made);

        body.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        body.AddChild(MenuChromeTheme.CreateEngravedRule("CreditsThirdPartyRule", 1f));
        body.AddChild(new Control { CustomMinimumSize = new Vector2(0, 2) });
        body.AddChild(LeftCaptionWithFallback(
            "credits.third_party_heading",
            "Third-party frameworks, libraries & open data",
            12,
            MenuChromeTheme.PaperFaint));
        var thirdParty = LeftCaptionWithFallback(
            "credits.third_party_body",
            ThirdPartyCreditsFallback,
            13,
            MenuChromeTheme.PaperDim);
        thirdParty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        thirdParty.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        body.AddChild(thirdParty);

        rows.AddChild(MenuAction("Back", HideCredits, 40, primary: true));
    }

    private const string ThirdPartyCreditsFallback =
        "Engine & Runtimes: Godot Engine 4.8 (MIT), Jolt Physics / Godot Jolt (Jorrit Rouwe, Mikael Hermansson & contributors, MIT), .NET 10 / WPF / System.IO.Hashing / System.Numerics.Tensors / RecyclableMemoryStream (Microsoft & .NET Foundation, MIT), F# & FSharp.Core (Don Syme & contributors, MIT).\n\n" +
        "Libraries & Compression: ZLinq & SimdLinq (Yoshifumi Kawai / Cysharp, MIT), CommunityToolkit.HighPerformance (Sergio Pedri & .NET Foundation, MIT), DotNext (Roman Sakno, MIT), BitFaster.Caching (Alex Peck, MIT), ZeroAlloc.Collections (Marcel Roozekrans, MIT), Equativ.RoaringBitmaps & RoaringBitmap format (Equativ Contributors & Daniel Lemire, Apache-2.0), RBush (Daniel Newman & Vladimir Agafonkin, MIT), K4os.Compression.LZ4 (Milosz Krajewski & Yann Collet, MIT/BSD), YamlDotNet (Antoine Aubry & contributors, MIT), Ben.Demystifier (Ben Adams & contributors, Apache-2.0).\n\n" +
        "Profiling, Addons & Language Kit: Tracy Profiler (Bartosz Taudul, BSD-3-Clause), Tracy-CSharp (clibequilibrium, MIT); Godot addons (Fennara, LimboAI, GodotSteam, Dialogue Manager, Ezcha Network, Lit); optional LanguageKit integrations (Rust, Luau, Nim, Odin, Kotlin, lua-gdextension, godot-sandbox, gdzig, graphics.gd, py4godot).\n\n" +
        "Typography & Open World Data: Source Serif 4 (Frank Griesshammer / Adobe, OFL-1.1), ESA GlobCover 2009 (ESA & UCLouvain), NASA Earth Observatory Blue Marble, GEBCO (BODC), Natural Earth, and Wikimedia Commons flag illustrators.";

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
                if (!IsMobileOrTouchPlatform) DealOrForceGame.ApplyWindowMode(SettingsCatalog.Chosen("window_mode"));
                break;

            case "vsync":
                if (!IsMobileOrTouchPlatform)
                    DisplayServer.WindowSetVsyncMode(SettingsCatalog.Flag("vsync")
                        ? DisplayServer.VSyncMode.Enabled
                        : DisplayServer.VSyncMode.Disabled);
                break;

            case "max_fps":
                ApplyMenuMaxFps();
                break;

            case "renderer":
                if (!IsMobileOrTouchPlatform) GraphicsRenderer.Choose(this, SettingsCatalog.Chosen("renderer"));
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

    private static void ApplyMenuMaxFps()
    {
        var chosen = SettingsCatalog.Chosen("max_fps");
        var configuredMaxFps = int.TryParse(chosen, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (int)MathF.Round(SettingsCatalog.Number("max_fps"));
        Engine.MaxFps = DealOrForceGame.ResolveEffectiveMaxFps(configuredMaxFps);
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationApplicationFocusOut)
        {
            var chosen = SettingsCatalog.Chosen("background_fps");
            if (int.TryParse(chosen, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var bgFps) && bgFps > 0)
                Engine.MaxFps = DealOrForceGame.ResolveEffectiveMaxFps(bgFps);
        }
        else if (what == NotificationApplicationFocusIn)
        {
            ApplyMenuMaxFps();
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
    public override void _Process(double delta)
    {
        LayoutAudit.Watch(this, delta);
        TickCompatShaderPrewarm();
    }

    public override void _ExitTree()
    {
        CleanupCompatShaderPrewarm();
        StartupIntro.ReleaseWarmResources();
        if (_bootLogger is not null)
        {
            try { OS.RemoveLogger(_bootLogger); } catch { }
            // GameDiagnosticLogger inherits Godot.Logger (RefCounted); never manually Dispose().
            _bootLogger = null;
        }
    }

    /// <summary>
    /// GLES3 (<c>gl_compatibility</c>) has no Vulkan ubershader pipeline, so compiling and drawing
    /// the packed campaign globe shader for 3 tiny hidden frames during main-menu idle removes
    /// the first-pan stall when entering the Country Picker or Campaign on ARM Mali / mobile GPUs.
    /// </summary>
    private void TryStartCompatShaderPrewarm()
    {
        if (_compatShaderPrewarmed) return;
        // Skip OwnWorld3D SubViewport shader prewarm on mobile/Android to avoid GLES3 + RefCounted teardown crashes.
        if (IsMobileOrTouchPlatform)
        {
            _compatShaderPrewarmed = true;
            return;
        }
        var args = OS.GetCmdlineUserArgs();
        if (Array.IndexOf(args, "--verify-build") >= 0 || Array.IndexOf(args, "--safe-map") >= 0)
            return;
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.Equals(RenderingServer.GetCurrentRenderingMethod(), "gl_compatibility", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            BootCrashDiagnostics.Step("compat_shader_prewarm_start");
            string rawSource = null;
            if (Godot.FileAccess.FileExists(MapShaderSource.ResourcePath))
                rawSource = Godot.FileAccess.GetFileAsString(MapShaderSource.ResourcePath).Replace("\r\n", "\n");
            else
            {
                var disk = GameDataLoader.FindDataFile("shaders", "globe_map.gdshader");
                if (!string.IsNullOrEmpty(disk) && File.Exists(disk))
                    rawSource = File.ReadAllText(disk).Replace("\r\n", "\n");
            }
            if (string.IsNullOrWhiteSpace(rawSource))
            {
                _compatShaderPrewarmed = true;
                return;
            }

            var builtSource = PackedMapShader.Build(MapColorSpaceShader.Build(rawSource));
            var shader = new Shader { Code = builtSource };
            var material = new ShaderMaterial { Shader = shader };

            var rgbaImg = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
            rgbaImg.Fill(Colors.White);
            var rgImg = Image.CreateEmpty(2, 2, false, Image.Format.Rg8);
            var rImg = Image.CreateEmpty(2, 2, false, Image.Format.R8);
            rImg.Fill(Colors.White);

            var lookups = BuildDummyTextureArray(rgbaImg, PackedMapShader.Lookups.Count);
            var ids = BuildDummyTextureArray(rgImg, PackedMapShader.Ids.Count);
            var waterTiles = BuildDummyTextureArray(rgbaImg, 2);
            var politicalTex = ImageTexture.CreateFromImage(rgbaImg);
            var waterMaskTex = ImageTexture.CreateFromImage(rImg);

            _compatPrewarmResources.Add(shader);
            _compatPrewarmResources.Add(material);
            _compatPrewarmResources.Add(lookups);
            _compatPrewarmResources.Add(ids);
            _compatPrewarmResources.Add(waterTiles);
            _compatPrewarmResources.Add(politicalTex);
            _compatPrewarmResources.Add(waterMaskTex);

            material.SetShaderParameter("packed_lookups", lookups);
            material.SetShaderParameter("packed_ids", ids);
            material.SetShaderParameter("packed_water_tiles", waterTiles);
            material.SetShaderParameter("political_map", politicalTex);
            material.SetShaderParameter("water_mask", waterMaskTex);
            material.SetShaderParameter("quality_tier",
                (float)(int)(PlatformProfile.Current?.GraphicsQualityTier ?? PlatformGraphicsTier.MobileLow));

            var mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 8, Rings = 4 };
            _compatPrewarmResources.Add(mesh);

            var vp = new SubViewport
            {
                Name = "CompatShaderPrewarmView",
                Size = new Vector2I(16, 16),
                OwnWorld3D = true,
                TransparentBg = false,
                PhysicsObjectPicking = false,
                HandleInputLocally = false,
                Msaa3D = Viewport.Msaa.Disabled,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            vp.AddChild(new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color,
                    BackgroundColor = MenuChromeTheme.BackdropColor,
                    AmbientLightSource = Godot.Environment.AmbientSource.Disabled
                }
            });
            vp.AddChild(new DirectionalLight3D { ShadowEnabled = false });
            vp.AddChild(new Camera3D { Position = new Vector3(0f, 0f, 3f), Current = true });
            vp.AddChild(new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            });
            AddChild(vp);

            var sink = new TextureRect
            {
                Name = "CompatShaderPrewarmSink",
                Texture = vp.GetTexture(),
                CustomMinimumSize = new Vector2(16, 16),
                Size = new Vector2(16, 16),
                Position = Vector2.Zero,
                ZIndex = -100,
                Modulate = new Color(1f, 1f, 1f, 0.01f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            AddChild(sink);

            _compatPrewarmViewport = vp;
            _compatPrewarmSink = sink;
            _compatPrewarmFrames = 3;
        }
        catch (Exception ex)
        {
            BootCrashDiagnostics.RecordException("MainMenuScene.CompatShaderPrewarm", ex, "non-fatal prewarm skip", fatal: false);
            CleanupCompatShaderPrewarm();
            _compatShaderPrewarmed = true;
        }
    }

    private static Texture2DArray BuildDummyTextureArray(Image layer, int count)
    {
        var layers = new Godot.Collections.Array<Image>();
        for (var i = 0; i < count; i++) layers.Add(layer);
        var arr = new Texture2DArray();
        var err = arr.CreateFromImages(layers);
        if (err != Error.Ok)
            throw new InvalidDataException("Shader prewarm texture array failed: " + err);
        return arr;
    }

    private void TickCompatShaderPrewarm()
    {
        if (_compatPrewarmViewport is null) return;
        if (--_compatPrewarmFrames > 0) return;
        CleanupCompatShaderPrewarm();
        _compatShaderPrewarmed = true;
        BootCrashDiagnostics.Step("compat_shader_prewarm_done");
    }

    private void CleanupCompatShaderPrewarm()
    {
        if (_compatPrewarmSink is not null && IsInstanceValid(_compatPrewarmSink))
            _compatPrewarmSink.QueueFree();
        _compatPrewarmSink = null;

        if (_compatPrewarmViewport is not null && IsInstanceValid(_compatPrewarmViewport))
            _compatPrewarmViewport.QueueFree();
        _compatPrewarmViewport = null;

        // Clear C# references only; Godot RefCounted teardown releases native resources safely.
        _compatPrewarmResources.Clear();
    }

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
        BootCrashDiagnostics.Step("menu_enter_campaign", intent.ToString());
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
            {
                var key = (string)label.GetMeta(SourceMeta);
                label.Text = label.HasMeta(FallbackMeta)
                    ? Loc.GetOr(key, (string)label.GetMeta(FallbackMeta))
                    : Loc.Get(key);
            }
            Relabel(child);
        }
    }

    private const string SourceMeta = "loc_source";
    private const string FallbackMeta = "loc_fallback";

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

    private static Label LeftCaptionWithFallback(string key, string fallback, int size, Color colour, bool serif = false, bool bold = false)
    {
        var label = new Label
        {
            Text = Loc.GetOr(key, fallback),
            HorizontalAlignment = HorizontalAlignment.Left,
            AutowrapMode = TextServer.AutowrapMode.Off
        };
        label.SetMeta(SourceMeta, key);
        label.SetMeta(FallbackMeta, fallback);
        MenuChromeTheme.StyleExistingLabel(label, size, colour, serif: serif, bold: bold);
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
            var halfW = Mathf.Min(340f, (view.X - 48f) * 0.5f);
            var halfH = Mathf.Min(250f, (view.Y - 40f) * 0.5f);
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
