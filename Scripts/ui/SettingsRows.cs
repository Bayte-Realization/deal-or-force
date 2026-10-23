using System;
using System.Globalization;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// One option, drawn as a row - shared by every screen that shows options.
///
/// The settings screen exists in two places now. The main menu shows it without a world
/// behind it, because a player changing the volume should not have to wait for two hundred
/// and sixty countries to be created first; a running campaign shows it over the map. The
/// FRAME around it differs - the menu draws its own quiet panel, the campaign draws its
/// dossier chrome over the globe - but what a row IS must not.
///
/// So the frame is each screen's business and the rows are here. That split is not
/// tidiness: the row is where the decisions live. Which control a kind of option gets, that
/// a language's own name must not be put through the translation table, how a slider's
/// number is written in the player's locale, when the value is saved and when the game is
/// told about it. Two copies of that drift, and the drift shows up as a screen where one of
/// them writes the setting and the other one does not.
///
/// The host says what to DO about a change by handing in `applied`. The menu's version of
/// that is small - there is no campaign to reach into and no globe to retint - and the
/// campaign's is the full one; both write to the same file, so a volume set in the menu is
/// the volume the campaign starts with.
/// </summary>
public static class SettingsRows
{
    /// <summary>
    /// Build one option into `parent`, calling `applied` with its id whenever it changes.
    /// </summary>
    public static void Build(SettingDefinition definition, Container parent, Action<string> applied)
    {
        if (definition is null || parent is null) return;

        var block = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        block.AddThemeConstantOverride("separation", 3);
        parent.AddChild(block);

        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 10);
        block.AddChild(row);

        var name = DealOrForceGame.RowLabel(DealOrForceGame.Tr(definition.Name), 14,
            Ui.Colour("text.body"), 300);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(name);

        switch (definition.Kind)
        {
            case SettingKind.Toggle: Toggle(definition, row, applied); break;
            case SettingKind.Choice: Choice(definition, row, applied); break;
            case SettingKind.Slider: Slider(definition, row, applied); break;
        }

        if (definition.Description.Length > 0)
        {
            var note = DealOrForceGame.RowLabel(DealOrForceGame.Tr(definition.Description), 11,
                Ui.Colour("text.hint"));
            note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            block.AddChild(note);
        }
        // Say whose option this is. Inside a campaign it is that campaign's alone; from the
        // main menu it is only where new campaigns start.
        if (definition.Campaign)
        {
            block.AddChild(DealOrForceGame.RowLabel(DealOrForceGame.Tr(SettingsCatalog.CampaignBound
                    ? "This campaign only - it is kept in its save. New campaigns start from the value set in the main menu."
                    : "Where new campaigns start. A campaign keeps its own value once it has begun."), 11,
                Ui.Colour("text.accent_dim")));
        }
        if (definition.NeedsRestart)
        {
            block.AddChild(DealOrForceGame.RowLabel(
                DealOrForceGame.Tr("Takes effect the next time the game starts."), 11,
                Ui.Colour("text.accent_dim")));
        }

        parent.AddChild(new ColorRect
        {
            Color = Ui.Colour("surface.line_faint"),
            CustomMinimumSize = new Vector2(0, 1)
        });
    }

    private static void Toggle(SettingDefinition definition, Container row, Action<string> applied)
    {
        var id = definition.Id;
        var button = new Button
        {
            Text = DealOrForceGame.Tr(SettingsCatalog.Flag(id) ? "On" : "Off"),
            CustomMinimumSize = new Vector2(150, 32)
        };
        button.Pressed += () =>
        {
            var value = !SettingsCatalog.Flag(id);
            SettingsCatalog.Store(id, value);
            button.Text = DealOrForceGame.Tr(value ? "On" : "Off");
            DealOrForceGame.ApplyLedgerButtonState(button, value);
            applied?.Invoke(id);
        };
        DealOrForceGame.ApplyLedgerButtonState(button, SettingsCatalog.Flag(id));
        row.AddChild(button);
    }

    private static void Choice(SettingDefinition definition, Container row, Action<string> applied)
    {
        var id = definition.Id;
        var option = new OptionButton { CustomMinimumSize = new Vector2(220, 32), FitToLongestItem = false };
        for (var index = 0; index < definition.Values.Count; index++)
        {
            var label = index < definition.Labels.Count && definition.Labels[index].Length > 0
                ? definition.Labels[index]
                : definition.Values[index];
            // A language's own name is already in that language. Everything else on this
            // screen is an English phrase and wants the table.
            option.AddItem(definition.LabelsAreLiteral ? label : DealOrForceGame.Tr(label), index);
        }
        var chosen = definition.Values.IndexOf(SettingsCatalog.Chosen(id));
        option.Selected = chosen >= 0 ? chosen : 0;
        option.ItemSelected += index =>
        {
            var at = (int)index;
            if (at < 0 || at >= definition.Values.Count) return;
            SettingsCatalog.Store(id, definition.Values[at]);
            applied?.Invoke(id);
        };
        row.AddChild(option);
    }

    private static void Slider(SettingDefinition definition, Container row, Action<string> applied)
    {
        var id = definition.Id;
        var reading = DealOrForceGame.RowLabel("", 13, Ui.Colour("text.body"), 88);
        reading.HorizontalAlignment = HorizontalAlignment.Right;

        var slider = new HSlider
        {
            MinValue = definition.Minimum,
            MaxValue = definition.Maximum,
            Step = definition.Step,
            Value = SettingsCatalog.Number(id),
            CustomMinimumSize = new Vector2(220, 26),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            // The wheel scrolls the page. The same reason it does on the budget: a player
            // reading down a list of options should not change one on the way past.
            Scrollable = false
        };
        reading.Text = Reading(definition, (float)slider.Value);
        slider.ValueChanged += value =>
        {
            SettingsCatalog.Store(id, (float)value);
            reading.Text = Reading(definition, (float)value);
            applied?.Invoke(id);
        };
        row.AddChild(slider);
        row.AddChild(reading);
    }

    /// <summary>How a slider's number is written, in the player's own locale.</summary>
    public static string Reading(SettingDefinition definition, float value) =>
        definition.Unit.ToLowerInvariant() switch
        {
            "number" => value.ToString("0.##", CultureInfo.CurrentCulture),
            "days" => Loc.Format("{0} day(s)", value.ToString("0", CultureInfo.CurrentCulture)),
            "pixels" => Loc.Format("{0} px", value.ToString("0", CultureInfo.CurrentCulture)),
            _ => value.ToString("P0", CultureInfo.CurrentCulture)
        };

    /// <summary>
    /// The languages are not in the file, because which of them exist depends on what mods
    /// are loaded. The option is declared with an empty list and filled in here.
    /// </summary>
    public static void RefreshLanguageChoices()
    {
        var definition = SettingsCatalog.Of("language");
        if (definition is null) return;
        definition.Values.Clear();
        definition.Labels.Clear();
        definition.LabelsAreLiteral = true;
        foreach (var language in Loc.Languages)
        {
            definition.Values.Add(language);
            definition.Labels.Add(Loc.DisplayName(language));
        }
        if (definition.Values.Count > 0 && !definition.Values.Contains(definition.DefaultValue))
            definition.DefaultValue = definition.Values[0];
    }

    /// <summary>
    /// The tab's own word before translation - which is also its localisation key, and which
    /// both settings screens keep on the button so that a later language change has the
    /// English to translate again. A category a mod invents is shown under its own id.
    /// </summary>
    public static string CategorySource(string category) => category.ToLowerInvariant() switch
    {
        "game" => "Game",
        "interface" => "Interface",
        "video" => "Display",
        "audio" => "Sound",
        "editor_mods" => "Editor mods",
        _ => category
    };

    /// <summary>The tab's own word, in the language being played.</summary>
    public static string CategoryName(string category) =>
        DealOrForceGame.Tr(CategorySource(category));

    /// <summary>Put every option in a category back to what the game ships with.</summary>
    public static void ResetCategory(string category, Action<string> applied)
    {
        foreach (var definition in SettingsCatalog.In(category))
        {
            SettingsCatalog.Reset(definition.Id);
            applied?.Invoke(definition.Id);
        }
    }
}
