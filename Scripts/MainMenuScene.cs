using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

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
    Settings
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
/// The backdrop is drawn rather than painted: a graticule on a dark ground, drifting very
/// slowly, and the limb of a globe behind the panel. No image file, nothing to load, and it
/// is the one thing on screen while the player decides - so it is strict, quiet and does not
/// compete with the logo.
/// </summary>
public partial class MainMenuScene : Control
{
    /// <summary>What the player asked for, read and cleared by the campaign scene.</summary>
    public static MenuIntent Wanted = MenuIntent.None;

    /// <summary>Which save, when the intent is <see cref="MenuIntent.LoadSlot"/>.</summary>
    public static string WantedSlot = "";

    private const string CampaignScene = "res://Scenes/Main.tscn";
    private const string MenuScene = "res://Scenes/MainMenu.tscn";

    private Button _language;

    /// <summary>The menu itself, the credits and the options - one visible at a time.</summary>
    private Control _menu;
    private Control _credits;
    private Control _settings;
    private Control _archive;
    private VBoxContainer _archiveRows;
    private VBoxContainer _settingsRows;
    private readonly Dictionary<string, Button> _settingsTabs = new(StringComparer.OrdinalIgnoreCase);
    private string _settingsCategory = "";

    public override void _Ready()
    {
        Name = "MainMenu";
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

        // And the language the player chose last time. Their preferences live in a file
        // under user://, and nothing has told the store where that is yet - the campaign
        // scene is what normally does it, and it is not running.
        PlayerSettings.UseDirectory(ProjectSettings.GlobalizePath("user://"));
        var chosen = SettingsCatalog.Chosen("language");
        if (chosen.Length > 0) Loc.Language = chosen;

        // And the music, which this screen had none of: the player for it used to live in the
        // campaign scene, so the menu theme only began once a world had been built. It lives
        // above the scenes now and carries on across them (MusicDirector). The catalogue is read
        // once - a campaign that has loaded mods has read it with their tracks, and coming back
        // here should not forget them.
        if (MusicCatalog.Count == 0) MusicCatalog.Load();
        MusicDirector.Ensure(this).FollowScene(this, () => MusicCatalog.Moment.Menu);

        AddChild(new AtlasBackdrop { MouseFilter = MouseFilterEnum.Ignore });

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", MenuPanelStyle());
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.AnchorLeft = 0.5f;
        panel.AnchorRight = 0.5f;
        panel.AnchorTop = 0.5f;
        panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -230;
        panel.OffsetRight = 230;
        panel.OffsetTop = -300;
        panel.OffsetBottom = 300;
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        AddChild(panel);
        _menu = panel;

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right" }) margin.AddThemeConstantOverride("margin_" + side, 30);
        foreach (var side in new[] { "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 26);
        panel.AddChild(margin);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 9);
        margin.AddChild(rows);

        rows.AddChild(new TextureRect
        {
            Texture = GD.Load<Texture2D>(SpriteCatalog.TextureOf("brand_logo_banner",
                "res://game/gfx/interface/brand_logo_banner.png")),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(0, 150),
            MouseFilter = MouseFilterEnum.Ignore
        });

        rows.AddChild(MenuCaption("Choose a world. Shape its future.", 15, "text.muted"));
        rows.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        rows.AddChild(MenuAction("Singleplayer", () => Enter(MenuIntent.NewCampaign), 46, true));
        rows.AddChild(MenuAction("Load latest campaign", () => Enter(MenuIntent.LoadLatest), 42));
        rows.AddChild(MenuAction("Load campaign", ShowArchive, 42));
        rows.AddChild(MenuAction("Multiplayer · LAN / IP", () => Enter(MenuIntent.Multiplayer), 42));
        rows.AddChild(MenuAction("Settings", ShowSettings, 42));
        _language = MenuAction(LanguageLabel(), CycleLanguage, 42);
        rows.AddChild(_language);
        rows.AddChild(MenuAction("Credits", ShowCredits, 42));
        rows.AddChild(MenuAction("Quit to desktop", () => GetTree().Quit(), 42));

        BuildCopyright();
        BuildCredits();
        BuildSettings();
        BuildArchive();
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
            Color = new Color(0.01f, 0.02f, 0.04f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop,
            Visible = false
        };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(veil);
        _archive = veil;

        var panel = new PanelContainer { ClipContents = true };
        panel.AddThemeStyleboxOverride("panel", MenuPanelStyle());
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

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right" }) margin.AddThemeConstantOverride("margin_" + side, 26);
        foreach (var side in new[] { "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 20);
        panel.AddChild(margin);
        var frame = new VBoxContainer();
        frame.AddThemeConstantOverride("separation", 12);
        margin.AddChild(frame);

        frame.AddChild(MenuCaption("CAMPAIGN ARCHIVE", 24, "text.accent"));
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        frame.AddChild(scroll);
        _archiveRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _archiveRows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_archiveRows);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        frame.AddChild(actions);
        actions.AddChild(MenuAction("Back", HideArchive, 40));
        actions.AddChild(MenuAction("Open folder", () => OS.ShellOpen(DealOrForceGame.ResolveSaveRoot()), 40));
    }

    private void ShowArchive()
    {
        if (_archive is null) return;
        foreach (var child in _archiveRows.GetChildren()) child.QueueFree();

        var saves = SaveSystem.List(DealOrForceGame.ResolveSaveRoot());
        if (saves.Count == 0)
            _archiveRows.AddChild(MenuCaption("No campaigns saved yet.", 14, "text.muted"));
        foreach (var save in saves)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            _archiveRows.AddChild(row);

            var title = save.Slot switch
            {
                SaveSystem.AutoSlot => Loc.Get("AUTOSAVE"),
                SaveSystem.QuickSlot => Loc.Get("QUICKSAVE"),
                _ => string.IsNullOrWhiteSpace(save.DisplayName) ? save.Slot : save.DisplayName
            };
            var info = new Label
            {
                Text = Loc.Format("{0}\n{1} · {2} · {3} · {4} KiB", title, save.NationName, save.GameDate,
                    save.SavedUtc.ToLocalTime().ToString("g"), (save.CompressedBytes / 1024f).ToString("0")),
                AutowrapMode = TextServer.AutowrapMode.Off,
                ClipText = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center
            };
            info.AddThemeFontSizeOverride("font_size", 13);
            info.AddThemeColorOverride("font_color", Ui.Colour("text.body_soft"));
            row.AddChild(info);

            var slot = save.Slot;
            var load = MenuAction("Load", () =>
            {
                WantedSlot = slot;
                Enter(MenuIntent.LoadSlot);
            }, 38, true);
            load.CustomMinimumSize = new Vector2(110, 38);
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
        mark.AddThemeFontSizeOverride("font_size", 12);
        mark.AddThemeColorOverride("font_color", Ui.Colour("text.faint"));
        mark.SetAnchorsPreset(LayoutPreset.BottomLeft);
        mark.GrowHorizontal = GrowDirection.End;
        mark.GrowVertical = GrowDirection.Begin;
        mark.OffsetLeft = 24;
        mark.OffsetRight = 360;
        mark.OffsetTop = -40;
        mark.OffsetBottom = -18;
        AddChild(mark);
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
        var panel = new PanelContainer { Visible = false };
        panel.AddThemeStyleboxOverride("panel", MenuPanelStyle());
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.AnchorLeft = panel.AnchorRight = 0.5f;
        panel.AnchorTop = panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -230;
        panel.OffsetRight = 230;
        panel.OffsetTop = -160;
        panel.OffsetBottom = 160;
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        AddChild(panel);
        _credits = panel;

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right" }) margin.AddThemeConstantOverride("margin_" + side, 30);
        foreach (var side in new[] { "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 28);
        panel.AddChild(margin);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 10);
        margin.AddChild(rows);

        rows.AddChild(MenuCaption("brand.title", 26, "text.accent"));
        rows.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });
        rows.AddChild(MenuCaption("Made by", 13, "text.faint"));
        rows.AddChild(MenuCaption("Bayte Realization (Kamil Suleymanov)", 18, "text.body"));
        rows.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });
        rows.AddChild(MenuCaption("With the explicit use of AI", 13, "text.muted"));

        // Expanding, with a floor under it: the expansion pushes the way back to the
        // bottom of the panel, and the floor stops it sitting flush against the last line
        // if the panel is ever shorter than its contents.
        rows.AddChild(new Control
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 18)
        });
        rows.AddChild(MenuAction("Back", HideCredits, 40));
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
            Color = new Color(0.01f, 0.02f, 0.04f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop,
            Visible = false
        };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(veil);
        _settings = veil;

        var panel = new PanelContainer { ClipContents = true };
        panel.AddThemeStyleboxOverride("panel", MenuPanelStyle());
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

        var frame = new VBoxContainer();
        frame.AddThemeConstantOverride("separation", 10);
        panel.AddChild(frame);

        var head = new MarginContainer();
        foreach (var side in new[] { "left", "right" }) head.AddThemeConstantOverride("margin_" + side, 26);
        head.AddThemeConstantOverride("margin_top", 20);
        frame.AddChild(head);
        var headRows = new VBoxContainer();
        headRows.AddThemeConstantOverride("separation", 10);
        head.AddChild(headRows);
        headRows.AddChild(MenuCaption("SETTINGS", 24, "text.accent"));

        var tabs = new HFlowContainer();
        tabs.AddThemeConstantOverride("h_separation", 6);
        tabs.AddThemeConstantOverride("v_separation", 5);
        headRows.AddChild(tabs);
        foreach (var category in SettingsCatalog.Categories())
        {
            var captured = category;
            var tab = new Button
            {
                Text = SettingsRows.CategoryName(category),
                CustomMinimumSize = new Vector2(150, 32)
            };
            // Marked like every other caption on this screen, so switching language from the
            // menu button relabels the tabs too. Without it they kept whichever language the
            // screen was built in while everything around them changed.
            tab.SetMeta(SourceMeta, SettingsRows.CategorySource(category));
            tab.Pressed += () => ShowSettingsCategory(captured);
            DealOrForceGame.StyleButton(tab, false);
            tabs.AddChild(tab);
            _settingsTabs[category] = tab;
        }

        var body = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var side in new[] { "left", "right" }) body.AddThemeConstantOverride("margin_" + side, 26);
        frame.AddChild(body);
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        body.AddChild(scroll);
        _settingsRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _settingsRows.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(_settingsRows);

        var foot = new MarginContainer();
        foreach (var side in new[] { "left", "right" }) foot.AddThemeConstantOverride("margin_" + side, 26);
        foot.AddThemeConstantOverride("margin_bottom", 18);
        frame.AddChild(foot);
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        foot.AddChild(actions);
        actions.AddChild(MenuAction("Back", HideSettings, 40));
        var reset = MenuAction("Reset this page", () =>
        {
            SettingsRows.ResetCategory(_settingsCategory, ApplyMenuSetting);
            ShowSettingsCategory(_settingsCategory);
        }, 40);
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
            DealOrForceGame.StyleButton(tab, string.Equals(id, category, StringComparison.OrdinalIgnoreCase));

        foreach (var child in _settingsRows.GetChildren()) child.QueueFree();
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

    private static Label MenuCaption(string text, int size, string colourToken)
    {
        var label = new Label
        {
            Text = Loc.Get(text),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off
        };
        label.SetMeta(SourceMeta, text);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", Ui.Colour(colourToken));
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
        DealOrForceGame.StyleButton(button, primary);
        button.AddThemeFontSizeOverride("font_size", height >= 46 ? 16 : 14);
        button.Pressed += pressed;
        return button;
    }

    /// <summary>The panel the menu sits in: the shell's own ground, ruled in its own brass.</summary>
    private static StyleBoxFlat MenuPanelStyle()
    {
        var box = new StyleBoxFlat
        {
            BgColor = Ui.Colour("surface.overlay"),
            BorderColor = new Color(Ui.Colour("button.primary_border"), 0.45f)
        };
        box.SetBorderWidthAll(1);
        return box;
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

        // The ground is not one colour. A flat #060b13 rectangle is what made the old menu
        // feel like a dialog box on a desktop; a vertical gradient from near-black at the top
        // to a warmer, faintly lit navy at the bottom puts the light source where the panel
        // is, and the eye reads it as a room rather than as a form.
        var baseColour = Ui.Colour("surface.background");
        var bands = 24;
        for (var band = 0; band < bands; band++)
        {
            var t = band / (float)(bands - 1);
            var colour = baseColour.Lerp(new Color(0.045f, 0.070f, 0.105f), t * 0.85f);
            DrawRect(new Rect2(0, size.Y * band / bands, size.X, size.Y / bands + 1f), colour);
        }

        // A halo behind where the panel stands, so the menu sits in a pool of light.
        var line = Ui.Colour("surface.line");
        var glowCentre = new Vector2(size.X * 0.5f, size.Y * 0.50f);
        var glowRadius = Mathf.Min(size.X, size.Y) * 0.62f;
        for (var ring = 14; ring >= 1; ring--)
        {
            var t = ring / 14f;
            DrawCircle(glowCentre, glowRadius * t, new Color(0.10f, 0.14f, 0.20f, 0.030f * (1f - t)));
        }

        // Stars in the outer margins only. Under the panel they would be hidden anyway, and
        // crowding the centre is exactly what the gradient and the halo are there to avoid.
        foreach (var star in Stars)
        {
            var at = new Vector2(star.X * size.X, star.Y * size.Y);
            var fromCentre = at.DistanceTo(glowCentre) / glowRadius;
            if (fromCentre < 0.55f) continue;
            var twinkle = 0.5f + 0.5f * Mathf.Sin(_time * 0.7f + star.X * 40f + star.Y * 17f);
            DrawCircle(at, 1f, new Color(0.75f, 0.82f, 0.90f, 0.10f + 0.16f * twinkle * fromCentre));
        }

        var faint = new Color(line, 0.055f);
        var marked = new Color(line, 0.13f);

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
        DrawArc(centre, radius, 0f, Mathf.Tau, 128, new Color(line, 0.16f), 1.5f, true);
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
                new Color(line, 0.05f + 0.07f * squash), 1f, false);
            DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        }
        for (var parallel = 1; parallel <= 4; parallel++)
        {
            var lat = parallel * 0.2f;
            var ringRadius = radius * Mathf.Cos(lat * Mathf.Pi * 0.5f);
            var ringY = centre.Y - radius * Mathf.Sin(lat * Mathf.Pi * 0.5f) * 0.85f;
            DrawArc(new Vector2(centre.X, ringY), ringRadius, 0f, Mathf.Tau, 96, new Color(line, 0.07f), 1f, true);
        }

        // Two brass hairlines, top and bottom, the same rule the dossier frames are drawn in.
        var brass = Ui.Colour("button.primary_border");
        DrawRect(new Rect2(0, size.Y * 0.055f, size.X, 1f), new Color(brass, 0.22f));
        DrawRect(new Rect2(0, size.Y * 0.945f, size.X, 1f), new Color(brass, 0.22f));
    }
}
