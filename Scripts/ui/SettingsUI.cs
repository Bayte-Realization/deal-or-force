using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The options screen.
///
/// Built from `common/settings` rather than written out here, so the categories, the order,
/// the words and the ranges are all data: a mod reorders the screen, renames an option,
/// widens a range or adds one of its own by dropping a file in. What this file knows is the
/// other half - what to actually *do* when an id it recognises changes.
///
/// Reachable from the main menu and from the pause menu, and everything is applied the
/// moment it is changed and written to disk at the same time. There is no Apply button on
/// purpose: a setting you have to confirm is a setting you cannot hear or see the effect of
/// while you are choosing it, and the two things here that genuinely cannot be done live
/// say so on their own row.
/// </summary>
public partial class DealOrForceGame
{
    private Control _settingsLayer = null!;
    private VBoxContainer _settingsRows = null!;
    private Label _settingsRestartNotice = null!;
    private string _settingsCategory = "";
    private readonly Dictionary<string, Button> _settingsTabs = new(StringComparer.OrdinalIgnoreCase);
    private bool _settingsNeedRestart;
    private bool _settingsLanguageHooked;

    private void BuildSettingsInterface()
    {
        _settingsLayer = FullLayer("Settings");
        _settingsLayer.Visible = false;
        _settingsLayer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_settingsLayer);

        var veil = new ColorRect { Color = MenuChromeTheme.VeilColor, MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _settingsLayer.AddChild(veil);

        var panel = TexturedPanel(_settingsLayer);
        MenuChromeTheme.StyleArchitecturalPlate(panel, brassCorners: true);
        Ui.Place(panel, "modal.settings");

        var frame = new VBoxContainer();
        frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        frame.AddThemeConstantOverride("separation", 10);
        panel.AddChild(frame);

        var touch = DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var head = new MarginContainer();
        head.AddThemeConstantOverride("margin_left", 28);
        head.AddThemeConstantOverride("margin_right", 28);
        head.AddThemeConstantOverride("margin_top", 20);
        frame.AddChild(head);
        var headRows = new VBoxContainer();
        headRows.AddThemeConstantOverride("separation", 10);
        head.AddChild(headRows);
        var closeSettings = ActionButton("×", HideSettings, touch ? 44 : 38, touch ? 42 : 32);
        closeSettings.Name = "CloseSettings";
        closeSettings.TooltipText = Ui.Tip(Tr("Close"));
        MenuChromeTheme.StyleWellButton(closeSettings, false, 15);
        ScreenHeader(headRows, "SETTINGS",
            "Interface scale, video quality, strategic audio and campaign preferences.",
            false, closeSettings);
        MenuChromeTheme.StyleScreenHeader(headRows.GetChildOrNull<PanelContainer>(0));

        var tabs = TabStrip(headRows, "SettingsTabs");
        var categories = DealOrForce.Modding.LauncherSelection.IsEditorRun
            ? SettingsCatalog.Categories().Append("editor_mods") : SettingsCatalog.Categories();
        foreach (var category in categories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var captured = category;
            // The English, not the translation: `ActionButton` translates what it is
            // given and remembers the original, and a tab that remembered the Russian
            // had nothing to translate when the language changed back.
            var button = TabButton(tabs, SettingsRows.CategorySource(category), () => ShowSettingsCategory(captured), 150);
            MenuChromeTheme.StyleTabButton(button, active: false, fontSize: 13);
            _settingsTabs[category] = button;
        }

        var bodyWrap = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        bodyWrap.AddThemeConstantOverride("margin_left", 28);
        bodyWrap.AddThemeConstantOverride("margin_right", 28);
        frame.AddChild(bodyWrap);

        var body = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true };
        body.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(0, 0));
        bodyWrap.AddChild(body);
        _settingsRows = ScrollablePanelRows(body, 20, 14, 12);

        var foot = new MarginContainer();
        foot.AddThemeConstantOverride("margin_left", 28);
        foot.AddThemeConstantOverride("margin_right", 28);
        foot.AddThemeConstantOverride("margin_bottom", 18);
        frame.AddChild(foot);
        var footRows = new VBoxContainer();
        footRows.AddThemeConstantOverride("separation", 8);
        foot.AddChild(footRows);
        _settingsRestartNotice = RowLabel("", 12, MenuChromeTheme.BrassHi);
        _settingsRestartNotice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        footRows.AddChild(_settingsRestartNotice);

        var actionH = touch ? 46 : 40;
        var utilH = touch ? 42 : 36;
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        footRows.AddChild(actions);
        var back = ActionButton("Back", HideSettings, 180, actionH, true);
        MenuChromeTheme.StylePrimaryCta(back, 14, serif: false);
        actions.AddChild(back);
        var reset = ActionButton("Reset this page", ResetSettingsCategory, 200, actionH);
        MenuChromeTheme.StyleWellButton(reset, false, 13);
        reset.TooltipText = Ui.Tip(Tr("Put every option on this page back to what the game ships with."));
        actions.AddChild(reset);
        var media = ActionButton("Sound and artwork", OpenMediaLibrary, 190, actionH);
        media.Name = "OpenMediaLibrarySettings";
        MenuChromeTheme.StyleWellButton(media, false, 13);
        actions.AddChild(media);

        // "Where does this game keep my files?" is a question the interface should answer
        // itself, in the place where a player looks for it, with a button that opens the folder
        // rather than a path they have to copy by hand.
        var folderRow = new HBoxContainer();
        folderRow.AddThemeConstantOverride("separation", 10);
        footRows.AddChild(folderRow);
        var folderButton = ActionButton("Game folder", OpenGameFolder, 160, utilH);
        folderButton.Name = "OpenGameFolderSettings";
        MenuChromeTheme.StyleWellButton(folderButton, false, 13);
        folderButton.TooltipText = Ui.Tip(Tr("Open the folder that holds saves, settings, logs and mods."));
        folderRow.AddChild(folderButton);
        // "Something went wrong" needs one button, not a forum thread: the report carries the
        // build, the machine, the last three logs, the settings and the newest save, in one zip
        // in the game folder. Console `report` does the same when the window is unusable.
        Button reportButton = null;
        reportButton = ActionButton("Report a problem", () =>
        {
            var path = CreateSupportReport();
            var made = path.Length > 0;
            reportButton.Text = Tr(made ? "Report saved" : "Report failed");
            reportButton.TooltipText = Ui.Tip(made ? path : SupportReport.LastFailure);
            if (made) OS.ShellOpen(GamePaths.Reports);
        }, 190, utilH);
        reportButton.Name = "ReportProblemSettings";
        MenuChromeTheme.StyleWellButton(reportButton, false, 13);
        reportButton.TooltipText = Ui.Tip(Tr("Write a zip with the build, the logs, the settings and the save, so a problem can be looked at."));
        folderRow.AddChild(reportButton);
        var folderLine = RowLabel(GamePaths.Root, 11, MenuChromeTheme.PaperFaint);
        MenuChromeTheme.StyleExistingLabel(folderLine, 11, MenuChromeTheme.PaperFaint, tabular: true);
        folderLine.Name = "GameFolderPath";
        folderLine.ClipText = true;
        folderLine.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        folderLine.TooltipText = Ui.Tip(GamePaths.Root);
        folderRow.AddChild(folderLine);

        if (!_settingsLanguageHooked)
        {
            _settingsLanguageHooked = true;
            Simulation.Loc.LanguageChanged += OnSettingsLanguageChanged;
        }
    }

    private void OnSettingsLanguageChanged()
    {
        if (!IsInstanceValid(_settingsLayer) || !_settingsLayer.Visible) return;
        Callable.From(() =>
        {
            if (IsInstanceValid(_settingsLayer) && _settingsLayer.Visible)
                ShowSettingsCategory(_settingsCategory);
        }).CallDeferred();
    }

    /// <summary>Open the one game folder in the system's file browser.</summary>
    private void OpenGameFolder()
    {
        GamePaths.Prepare();
        try { OS.ShellOpen(GamePaths.Root); }
        catch (Exception exception) { DiagnosticLogs.Write("errors", $"FOLDER_OPEN_FAILED {exception.Message}"); }
    }

    private void ShowSettings()
    {
        EnsureScreen("settings", BuildSettingsInterface);
        SettingsRows.RefreshLanguageChoices();
        if (_settingsCategory.Length == 0)
            _settingsCategory = SettingsCatalog.Categories().FirstOrDefault() ?? "game";
        _settingsLayer.Visible = true;
        _settingsLayer.MoveToFront();
        ShowSettingsCategory(_settingsCategory);
    }

    /// <summary>
    /// Close the settings screen - and, when there is nothing behind it, go back to the
    /// front door. Settings reached from the menu scene enter this scene with nothing else
    /// showing, so hiding the layer on its own would leave a player looking at the globe
    /// with no way out.
    /// </summary>
    private void HideSettings()
    {
        if (_settingsLayer is not null) _settingsLayer.Visible = false;
        var somethingElseIsUp = (_hudLayer is { Visible: true }) ||
                                (_pickerLayer is { Visible: true }) ||
                                (_networkLayer is { Visible: true }) ||
                                (_menuLayer is { Visible: true });
        if (!somethingElseIsUp) ShowMainMenu();
    }

    private void ShowSettingsCategory(string category)
    {
        _settingsCategory = category;
        foreach (var (id, button) in _settingsTabs)
            MenuChromeTheme.StyleTabButton(button, string.Equals(id, category, StringComparison.OrdinalIgnoreCase), 13);

        foreach (var child in _settingsRows.GetChildren())
        {
            _settingsRows.RemoveChild(child);
            child.QueueFree();
        }
        if (category == "editor_mods" && DealOrForce.Modding.LauncherSelection.IsEditorRun)
        {
            EditorModRows.Build(_settingsRows, true, () => { _settingsNeedRestart = true; RefreshRestartNotice(); });
            return;
        }
        BuildSettingsCategoryGuide(category);
        foreach (var definition in SettingsCatalog.In(category))
            SettingsRows.Build(definition, _settingsRows, ApplySetting);
        RefreshRestartNotice();
    }

    private void BuildSettingsCategoryGuide(string category)
    {
        if (string.Equals(category, "video", StringComparison.OrdinalIgnoreCase))
        {
            var card = new PanelContainer
            {
                Name = "SettingsVideoGuide",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            card.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(12, 8, highlighted: true));
            var stack = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            stack.AddThemeConstantOverride("separation", 5);
            card.AddChild(stack);

            var adapter = RenderingServer.GetVideoAdapterName();
            if (string.IsNullOrWhiteSpace(adapter)) adapter = "—";
            var method = RenderingServer.GetCurrentRenderingMethod();
            if (string.IsNullOrWhiteSpace(method))
                method = ProjectSettings.GetSetting("rendering/renderer/rendering_method").AsString();
            var methodLabel = method switch
            {
                "forward_plus" => Tr("aaa.ui.renderer_forward_plus"),
                "mobile" => Tr("aaa.ui.renderer_mobile"),
                "gl_compatibility" => Tr("aaa.ui.renderer_gl_compat"),
                _ => string.IsNullOrWhiteSpace(method) ? "—" : method
            };

            var hw = RowLabel(Simulation.Loc.Format("aaa.ui.settings_active_gpu_fmt", adapter, methodLabel),
                12, MenuChromeTheme.BrassHi);
            MenuChromeTheme.StyleExistingLabel(hw, 12, MenuChromeTheme.BrassHi, tabular: true);
            hw.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            stack.AddChild(hw);

            foreach (var (key, tone) in new[]
                     {
                         ("aaa.ui.settings_video_renderer_note", MenuChromeTheme.PaperDim),
                         ("aaa.ui.settings_video_smooth_note", MenuChromeTheme.PaperDim),
                         ("aaa.ui.settings_video_mobile_note", MenuChromeTheme.PaperFaint)
                     })
            {
                var line = RowLabel(Tr(key), 12, tone);
                line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                stack.AddChild(line);
            }
            _settingsRows.AddChild(card);
            return;
        }

        if (string.Equals(category, "interface", StringComparison.OrdinalIgnoreCase))
        {
            var card = new PanelContainer
            {
                Name = "SettingsInterfaceGuide",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            card.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(12, 8, highlighted: true));
            var line = RowLabel(Tr("aaa.ui.settings_interface_smooth_note"), 12, MenuChromeTheme.PaperDim);
            line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            card.AddChild(line);
            _settingsRows.AddChild(card);
        }
    }

    private void ResetSettingsCategory()
    {
        if (_settingsCategory == "editor_mods") EditorModRows.Reset();
        else SettingsRows.ResetCategory(_settingsCategory, ApplySetting);
        ShowSettingsCategory(_settingsCategory);
    }

    private void RefreshRestartNotice() =>
        _settingsRestartNotice.Text = _settingsNeedRestart
            ? Tr("Something you changed needs the game restarted before it takes effect.")
            : "";
}
