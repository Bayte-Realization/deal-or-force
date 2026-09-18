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

    private void BuildSettingsInterface()
    {
        _settingsLayer = FullLayer("Settings");
        _settingsLayer.Visible = false;
        _settingsLayer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_settingsLayer);

        var veil = new ColorRect { Color = new Color(0.01f, 0.02f, 0.04f, 0.72f), MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _settingsLayer.AddChild(veil);

        var panel = TexturedPanel(_settingsLayer);
        Ui.Place(panel, "modal.settings");

        var frame = new VBoxContainer();
        frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        frame.AddThemeConstantOverride("separation", 10);
        panel.AddChild(frame);

        var head = new MarginContainer();
        head.AddThemeConstantOverride("margin_left", 28);
        head.AddThemeConstantOverride("margin_right", 28);
        head.AddThemeConstantOverride("margin_top", 22);
        frame.AddChild(head);
        var headRows = new VBoxContainer();
        headRows.AddThemeConstantOverride("separation", 10);
        head.AddChild(headRows);
        headRows.AddChild(Title("SETTINGS", 26, Ui.Colour("text.accent")));

        var tabs = new HFlowContainer();
        tabs.AddThemeConstantOverride("h_separation", 6);
        tabs.AddThemeConstantOverride("v_separation", 5);
        headRows.AddChild(tabs);
        foreach (var category in SettingsCatalog.Categories())
        {
            var captured = category;
            // The English, not the translation: `ActionButton` translates what it is
            // given and remembers the original, and a tab that remembered the Russian
            // had nothing to translate when the language changed back.
            var button = ActionButton(SettingsRows.CategorySource(category), () => ShowSettingsCategory(captured), 150, 34);
            tabs.AddChild(button);
            _settingsTabs[category] = button;
        }

        var body = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        frame.AddChild(body);
        _settingsRows = ScrollablePanelRows(body, 28, 14, 12);

        var foot = new MarginContainer();
        foot.AddThemeConstantOverride("margin_left", 28);
        foot.AddThemeConstantOverride("margin_right", 28);
        foot.AddThemeConstantOverride("margin_bottom", 20);
        frame.AddChild(foot);
        var footRows = new VBoxContainer();
        footRows.AddThemeConstantOverride("separation", 8);
        foot.AddChild(footRows);
        _settingsRestartNotice = RowLabel("", 12, Ui.Colour("text.accent_dim"));
        _settingsRestartNotice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        footRows.AddChild(_settingsRestartNotice);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        footRows.AddChild(actions);
        var back = ActionButton("Back", HideSettings, 180, 40, true);
        actions.AddChild(back);
        var reset = ActionButton("Reset this page", ResetSettingsCategory, 200, 40);
        reset.TooltipText = Ui.Tip(Tr("Put every option on this page back to what the game ships with."));
        actions.AddChild(reset);
    }

    private void ShowSettings()
    {
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
        _settingsLayer.Visible = false;
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
            ApplyLedgerButtonState(button, string.Equals(id, category, StringComparison.OrdinalIgnoreCase));

        foreach (var child in _settingsRows.GetChildren()) child.QueueFree();
        foreach (var definition in SettingsCatalog.In(category))
            SettingsRows.Build(definition, _settingsRows, ApplySetting);
        RefreshRestartNotice();
    }

    private void ResetSettingsCategory()
    {
        SettingsRows.ResetCategory(_settingsCategory, ApplySetting);
        ShowSettingsCategory(_settingsCategory);
    }

    private void RefreshRestartNotice() =>
        _settingsRestartNotice.Text = _settingsNeedRestart
            ? Tr("Something you changed needs the game restarted before it takes effect.")
            : "";
}
