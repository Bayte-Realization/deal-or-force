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
        parent.AddChild(DealOrForceGame.RowLabel(Loc.Get("MOD_EDITOR_HEADING"), 20, Ui.Colour("text.accent")));
        var explanation = Caption(Loc.Get("MOD_EDITOR_SEPARATE"), Ui.Colour("text.muted"));
        parent.AddChild(explanation);
        var notice = Caption(Loc.Get(campaignRunning ? "MOD_EDITOR_RESTART" : "MOD_EDITOR_NEXT_CAMPAIGN"), Ui.Colour("text.hint"));
        parent.AddChild(notice);
        if (loader.AvailableMods.Count == 0)
        {
            parent.AddChild(Caption(Loc.Get("MOD_EDITOR_EMPTY"), Ui.Colour("text.muted")));
            return;
        }
        foreach (var package in loader.AvailableMods)
        {
            var id = package.Manifest.Id;
            var requested = selection.IsEnabled(id, false, package.Manifest.Enabled);
            var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var style = new StyleBoxFlat
            {
                BgColor = new Color(.065f, .10f, .135f, .96f),
                BorderColor = Ui.Colour("text.faint"),
                ContentMarginLeft = 16, ContentMarginRight = 16,
                ContentMarginTop = 12, ContentMarginBottom = 12,
                CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
                CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3
            };
            style.SetBorderWidthAll(1);
            panel.AddThemeStyleboxOverride("panel", style);
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
            toggle.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.body", 13));
            toggle.SetPressedNoSignal(requested);
            rows.AddChild(toggle);
            var status = Caption(Status(requested, package.Manifest.Version), Ui.Colour("text.muted"));
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
