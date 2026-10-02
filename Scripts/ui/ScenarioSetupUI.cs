using System;
using System.Globalization;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class ScenarioMapEditor
{
    private (Window Window, VBoxContainer Rows) EditorDialog(string id, string title, int width, int height)
    {
        var viewport = GetViewportRect().Size;
        // Include the embedded title bar above the client size in the screen budget.
        var available = new Vector2I(Math.Max(1, (int)viewport.X - 40), Math.Max(1, (int)viewport.Y - 72));
        var window = new Window { Name = id, Title = Loc.Get(title), Exclusive = true, Theme = Theme,
            Size = new Vector2I(Math.Min((int)Math.Clamp(InterfaceSkin.Metric("map_editor." + id + ".width", width), 460, 1800), available.X),
                Math.Min((int)Math.Clamp(InterfaceSkin.Metric("map_editor." + id + ".height", height), 320, 1200), available.Y)),
            MinSize = new Vector2I(Math.Min(460, available.X), Math.Min(320, available.Y)) };
        AddChild(window); window.CloseRequested += () => window.QueueFree();
        var surface = new PanelContainer(); window.AddChild(surface); surface.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        surface.AddThemeStyleboxOverride("panel", ScenarioEditorTheme.Surface("shell.background", "shell.border", 0));
        var margin = new MarginContainer(); surface.AddChild(margin);
        foreach (var edge in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 16);
        var rows = new VBoxContainer(); margin.AddChild(rows); rows.AddThemeConstantOverride("separation", 8);
        return (window, rows);
    }

    private void EditSettings()
    {
        _map.FinishStroke();
        var settings = Draft.Settings;
        var (window, rows) = EditorDialog("ScenarioSettings", "Scenario settings", 680, 650);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill }; rows.AddChild(scroll);
        var fields = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(fields); fields.AddThemeConstantOverride("separation", 8);
        fields.AddChild(Caption("Name"));
        var name = new LineEdit { Name = "ScenarioName", Text = settings.Name, MaxLength = 160 }; fields.AddChild(name);
        fields.AddChild(Caption("Description"));
        var description = new TextEdit { Name = "ScenarioDescription", Text = settings.Description, CustomMinimumSize = new Vector2(0, 90) }; fields.AddChild(description);
        fields.AddChild(new HSeparator()); fields.AddChild(Caption("Start date & time"));
        var grid = new GridContainer { Columns = 2 }; fields.AddChild(grid); grid.AddThemeConstantOverride("h_separation", 12);
        SpinBox Number(string id, string label, int value, int min, int max)
        {
            grid.AddChild(FieldCaption(label));
            var box = new SpinBox { Name = id, MinValue = min, MaxValue = max, Step = 1, Value = value,
                SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(120, 34) }; grid.AddChild(box); return box;
        }
        var year = Number("ScenarioYear", "Year", settings.Year, 1, 9999);
        var month = Number("ScenarioMonth", "Month", settings.Month, 1, 12);
        var day = Number("ScenarioDay", "Day", settings.Day, 1, 31);
        var hour = Number("ScenarioHour", "Hour", settings.Hour, 0, 23);
        fields.AddChild(Caption("Changing the date moves the calendar only. Orders, cooldowns and event timers keep their elapsed-day origin."));
        fields.AddChild(new HSeparator()); fields.AddChild(Caption("World seed"));
        var seedRow = new HBoxContainer(); fields.AddChild(seedRow);
        var seed = new LineEdit { Name = "ScenarioSeed", Text = settings.Seed.ToString(CultureInfo.InvariantCulture), MaxLength = 10, SizeFlagsHorizontal = SizeFlags.ExpandFill }; seedRow.AddChild(seed);
        seedRow.AddChild(ActionButton("RandomScenarioSeed", "Random seed", () => seed.Text = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString(CultureInfo.InvariantCulture)));
        fields.AddChild(Caption("Changing the seed resets this draft's random stream, not its countries or borders. The campaign picker can choose a different seed."));
        fields.AddChild(Caption("Scenario ID (read-only)") ); fields.AddChild(Caption(Draft.Id));
        var error = Caption(""); rows.AddChild(error);
        var actions = new HFlowContainer(); rows.AddChild(actions);
        var apply = ActionButton("ApplyScenarioSettings", "Apply to draft", () =>
        {
            try
            {
                if (!int.TryParse(seed.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSeed) || parsedSeed <= 0)
                    throw new InvalidOperationException(Loc.Get("Seed must be a whole number from 1 to 2147483647."));
                Draft.UpdateSettings(new(name.Text, description.Text, (int)year.Value, (int)month.Value, (int)day.Value, (int)hour.Value, parsedSeed));
                RefreshStatus(); window.QueueFree();
            }
            catch (Exception e) { error.Text = Loc.Get("Could not apply changes") + ": " + Loc.Get(e.Message); }
        });
        DealOrForceGame.StyleButton(apply, true); actions.AddChild(apply);
        actions.AddChild(ActionButton("CancelScenarioSettings", "Cancel", () => window.QueueFree()));
        window.PopupCentered();
    }

    private void ShowHelp()
    {
        _map.FinishStroke();
        var dialog = new AcceptDialog { Name = "MapEditorHelp", Title = Loc.Get("Controls & scope"), Exclusive = true,
            DialogText = Loc.Get("Select mode inspects a province. Selection does not change the paint target; use the explicit target button.") + "\n\n" +
                Loc.Get("Left drag: paint whole provinces. Wheel: zoom. Middle drag: pan. Borders are visible when zoomed in.") + "\n\n" +
                Loc.Get("Ctrl+S: save to mod. Ctrl+Z / Ctrl+Y: undo / redo. Escape: cancel the current gesture.") + "\n\n" +
                Loc.Get("This brush does not split provinces or recalculate population, armies or the economy. Review those data in the content editor.") };
        AddChild(dialog); dialog.Confirmed += () => dialog.QueueFree(); dialog.Canceled += () => dialog.QueueFree();
        var screen = GetViewportRect().Size;
        dialog.PopupCentered(new Vector2I((int)Math.Min(680, screen.X - 40), (int)Math.Min(500, screen.Y - 40)));
    }
}
