using System;
using System.Linq;
using DealOrForce.Modding;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

// Uses the same descriptors, dependency resolution and atomic profile writer as the launcher.
public static class EditorModRows
{
    public static void Build(VBoxContainer parent, bool campaignRunning, Action saved = null)
    {
        var loader = new ModLoader();
        loader.Discover();
        var selection = LauncherSelection.Read();
        var heading = DealOrForceGame.RowLabel(Loc.Get("MOD_EDITOR_HEADING"), 20, MenuChromeTheme.Paper);
        MenuChromeTheme.StyleExistingLabel(heading, 20, MenuChromeTheme.Paper, serif: true);
        parent.AddChild(heading);
        var explanation = Caption(Loc.Get("MOD_EDITOR_SEPARATE"), MenuChromeTheme.PaperDim);
        parent.AddChild(explanation);
        var notice = Caption(Loc.Get(campaignRunning ? "MOD_EDITOR_RESTART" : "MOD_EDITOR_NEXT_CAMPAIGN"), MenuChromeTheme.BrassHi);
        parent.AddChild(notice);
        if (loader.AvailableMods.Count == 0)
        {
            parent.AddChild(Caption(Loc.Get("MOD_EDITOR_EMPTY"), MenuChromeTheme.PaperDim));
            return;
        }
        foreach (var package in loader.AvailableMods)
        {
            var id = package.Manifest.Id;
            var requested = selection.IsEnabled(id, false, package.Manifest.Enabled);
            var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            panel.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(16, 12));
            parent.AddChild(panel);
            var rows = new VBoxContainer();
            rows.AddThemeConstantOverride("separation", 6);
            panel.AddChild(rows);
            var toggle = new CheckButton
            {
                Name = "EditorModToggle",
                Text = package.Manifest.Name,
                Alignment = HorizontalAlignment.Left,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            toggle.SetMeta("mod_id", id);
            toggle.AddThemeFontOverride("font", MenuChromeTheme.SerifBody());
            toggle.AddThemeFontSizeOverride("font_size", 15);
            toggle.AddThemeColorOverride("font_color", MenuChromeTheme.Paper);
            toggle.AddThemeColorOverride("font_hover_color", MenuChromeTheme.Highlight);
            toggle.AddThemeColorOverride("font_pressed_color", MenuChromeTheme.Paper);
            toggle.AddThemeColorOverride("font_focus_color", MenuChromeTheme.Highlight);
            toggle.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            toggle.AddThemeStyleboxOverride("hover", MenuChromeTheme.GradientPlateBox(
                MenuChromeTheme.PlateTop, MenuChromeTheme.PlateBottom, MenuChromeTheme.RuleLight,
                innerTopLine: null, innerBottomLine: null, brassEdge: null, borderThickness: 1,
                padLeft: 6, padTop: 4, padRight: 6, padBottom: 4));
            toggle.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty());
            toggle.AddThemeStyleboxOverride("focus", MenuChromeTheme.FocusBox());
            toggle.SetPressedNoSignal(requested);
            rows.AddChild(toggle);
            var status = Caption(Status(requested, package.Manifest.Version), MenuChromeTheme.PaperFaint);
            rows.AddChild(status);
            toggle.Toggled += enabled =>
            {
                try
                {
                    // Re-read so another checkbox or a launcher update is not overwritten.
                    var current = LauncherSelection.Read();
                    current.Mods[id] = enabled;
                    current.Save(LauncherSelection.WritePath);
                    status.Text = Status(enabled, package.Manifest.Version);
                    notice.Text = Loc.Get(campaignRunning ? "MOD_EDITOR_RESTART" : "MOD_EDITOR_SAVED");
                    saved?.Invoke();
                    var resolved = new ModLoader();
                    resolved.Discover();
                    if (enabled && !resolved.Packages.Any(p => !p.IsContentPack &&
                        string.Equals(p.Manifest.Id, id, StringComparison.OrdinalIgnoreCase)))
                        status.Text = Loc.Get("MOD_EDITOR_DEPENDENCY");
                }
                catch (Exception error)
                {
                    toggle.SetPressedNoSignal(!enabled);
                    notice.Text = Loc.Get("MOD_EDITOR_SAVE_FAILED") + " " + error.Message;
                }
            };
        }
    }

    public static void Reset()
    {
        var loader = new ModLoader();
        loader.Discover();
        var selection = LauncherSelection.Read();
        foreach (var package in loader.AvailableMods) selection.Mods.Remove(package.Manifest.Id);
        selection.Save(LauncherSelection.WritePath);
    }

    private static string Status(bool enabled, string version) =>
        Loc.Get(enabled ? "MOD_EDITOR_ENABLED" : "MOD_EDITOR_DISABLED") + " · v" + version;

    private static Label Caption(string text, Color colour)
    {
        var label = DealOrForceGame.RowLabel(text, 13, colour);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return label;
    }
}
