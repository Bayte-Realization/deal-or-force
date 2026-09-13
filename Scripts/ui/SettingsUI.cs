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
            var button = ActionButton(CategoryName(category), () => ShowSettingsCategory(captured), 150, 34);
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

    /// <summary>The tab's own word. A category a mod invents is shown under its own id.</summary>
    private static string CategoryName(string category) => Tr(category.ToLowerInvariant() switch
    {
        "game" => "Game",
        "interface" => "Interface",
        "video" => "Display",
        "audio" => "Sound",
        _ => category
    });

    private void ShowSettings()
    {
        RefreshLanguageChoices();
        if (_settingsCategory.Length == 0)
            _settingsCategory = SettingsCatalog.Categories().FirstOrDefault() ?? "game";
        _settingsLayer.Visible = true;
        _settingsLayer.MoveToFront();
        ShowSettingsCategory(_settingsCategory);
    }

    private void HideSettings() => _settingsLayer.Visible = false;

    /// <summary>
    /// The languages are not in the file, because which of them exist depends on what mods
    /// are loaded. The option is declared with an empty list and filled in here.
    /// </summary>
    private static void RefreshLanguageChoices()
    {
        var definition = SettingsCatalog.Of("language");
        if (definition is null) return;
        definition.Values.Clear();
        definition.Labels.Clear();
        foreach (var language in Simulation.Loc.Languages)
        {
            definition.Values.Add(language);
            definition.Labels.Add(Simulation.Loc.DisplayName(language));
        }
        if (definition.Values.Count > 0 && !definition.Values.Contains(definition.DefaultValue))
            definition.DefaultValue = definition.Values[0];
    }

    private void ShowSettingsCategory(string category)
    {
        _settingsCategory = category;
        foreach (var (id, button) in _settingsTabs)
            ApplyLedgerButtonState(button, string.Equals(id, category, StringComparison.OrdinalIgnoreCase));

        foreach (var child in _settingsRows.GetChildren()) child.QueueFree();
        foreach (var definition in SettingsCatalog.In(category)) BuildSettingRow(definition);
        RefreshRestartNotice();
    }

    private void BuildSettingRow(SettingDefinition definition)
    {
        var block = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        block.AddThemeConstantOverride("separation", 3);
        _settingsRows.AddChild(block);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 10);
        block.AddChild(row);

        var name = RowLabel(Tr(definition.Name), 14, Ui.Colour("text.body"), 300);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(name);

        switch (definition.Kind)
        {
            case SettingKind.Toggle: BuildToggle(definition, row); break;
            case SettingKind.Choice: BuildChoice(definition, row); break;
            case SettingKind.Slider: BuildSlider(definition, row); break;
        }

        if (definition.Description.Length > 0)
        {
            var note = RowLabel(Tr(definition.Description), 11, Ui.Colour("text.hint"));
            note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            block.AddChild(note);
        }
        if (definition.NeedsRestart)
        {
            var warning = RowLabel(Tr("Takes effect the next time the game starts."), 11,
                Ui.Colour("text.accent_dim"));
            block.AddChild(warning);
        }
        _settingsRows.AddChild(new ColorRect
        {
            Color = Ui.Colour("surface.line_faint"),
            CustomMinimumSize = new Vector2(0, 1)
        });
    }

    private void BuildToggle(SettingDefinition definition, Container row)
    {
        var id = definition.Id;
        Button button = null;
        button = ActionButton(SettingsCatalog.Flag(id) ? "On" : "Off", () =>
        {
            var value = !SettingsCatalog.Flag(id);
            PlayerSettings.Set(id, value);
            button!.Text = Tr(value ? "On" : "Off");
            ApplyLedgerButtonState(button, value);
            ApplySetting(id);
        }, 150, 32, SettingsCatalog.Flag(id));
        ApplyLedgerButtonState(button, SettingsCatalog.Flag(id));
        row.AddChild(button);
    }

    private void BuildChoice(SettingDefinition definition, Container row)
    {
        var id = definition.Id;
        var option = new OptionButton { CustomMinimumSize = new Vector2(220, 32), FitToLongestItem = false };
        for (var index = 0; index < definition.Values.Count; index++)
            option.AddItem(Tr(index < definition.Labels.Count && definition.Labels[index].Length > 0
                ? definition.Labels[index]
                : definition.Values[index]), index);
        var chosen = definition.Values.IndexOf(SettingsCatalog.Chosen(id));
        option.Selected = chosen >= 0 ? chosen : 0;
        option.ItemSelected += index =>
        {
            var at = (int)index;
            if (at < 0 || at >= definition.Values.Count) return;
            PlayerSettings.Set(id, definition.Values[at]);
            ApplySetting(id);
        };
        row.AddChild(option);
    }

    private void BuildSlider(SettingDefinition definition, Container row)
    {
        var id = definition.Id;
        var reading = RowLabel("", 13, Ui.Colour("text.body"), 88);
        reading.HorizontalAlignment = HorizontalAlignment.Right;

        var slider = new HSlider
        {
            MinValue = definition.Minimum,
            MaxValue = definition.Maximum,
            Step = definition.Step,
            Value = SettingsCatalog.Number(id),
            CustomMinimumSize = new Vector2(220, 26),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            // The wheel scrolls the page. The same reason it does on the budget: a player
            // reading down a list of options should not change one on the way past.
            Scrollable = false
        };
        reading.Text = SettingText(definition, (float)slider.Value);
        slider.ValueChanged += value =>
        {
            PlayerSettings.Set(id, (float)value);
            reading.Text = SettingText(definition, (float)value);
            ApplySetting(id);
        };
        row.AddChild(slider);
        row.AddChild(reading);
    }

    private static string SettingText(SettingDefinition definition, float value) =>
        definition.Unit.ToLowerInvariant() switch
        {
            "number" => value.ToString("0.##", CultureInfo.CurrentCulture),
            "days" => Simulation.Loc.Format("{0} day(s)", value.ToString("0", CultureInfo.CurrentCulture)),
            "pixels" => Simulation.Loc.Format("{0} px", value.ToString("0", CultureInfo.CurrentCulture)),
            _ => value.ToString("P0", CultureInfo.CurrentCulture)
        };

    private void ResetSettingsCategory()
    {
        foreach (var definition in SettingsCatalog.In(_settingsCategory))
        {
            PlayerSettings.Clear(definition.Id);
            ApplySetting(definition.Id);
        }
        ShowSettingsCategory(_settingsCategory);
    }

    private void RefreshRestartNotice() =>
        _settingsRestartNotice.Text = _settingsNeedRestart
            ? Tr("Something you changed needs the game restarted before it takes effect.")
            : "";
}
