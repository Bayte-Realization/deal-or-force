using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class ResponsiveHudLayoutTests
{
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception("responsive HUD: " + label); Console.WriteLine("PASS responsive HUD: " + label); }

    public static void Run()
    {
        var full = ResponsiveHudLayout.Navigation(866, 15, 4, 5);
        Check(!full.CategoryMode && full.ButtonHeight >= 42, "1080p keeps all fifteen destinations readable in the existing narrow footprint");
        var compact = ResponsiveHudLayout.Navigation(454, 15, 4, 5);
        Check(compact.CategoryMode && compact.ButtonHeight == 50, "720p discloses a category instead of a permanent scroll thumb or miniature icons");
        Check(ResponsiveHudLayout.Navigation(454, 15, 15, 1).CategoryMode, "many modded group captions cannot force the rail beyond its budget");
        Check(ResponsiveHudLayout.Navigation(160, 15, 1, 15).ButtonHeight == 28, "very large categories retain minimum-size buttons for wheel and focus scrolling");
        Check(!ResponsiveHudLayout.Navigation(100, 0, 0, 0).CategoryMode, "an empty modded navigation list has a safe layout");
        Check(ResponsiveHudLayout.Navigation(float.NaN, 15, 4, 5, float.NaN).ButtonHeight >= 28, "invalid measurements cannot create NaN sizes");
        Check(ResponsiveHudLayout.DrawerColumns(452) == 2 && ResponsiveHudLayout.DrawerColumns(172) == 1,
            "layer drawer adapts between labelled columns without depending on a fixed mode count");
        Check(ResponsiveHudLayout.DrawerColumns(3000) == 3, "wide modded drawers keep a bounded column count");
        Check(ResponsiveHudLayout.DrawerColumns(float.NaN, float.NaN, 0) == 1, "invalid drawer metrics remain usable");

        foreach (var (width, height) in new[] { (1920f, 1080f), (1280f, 720f), (800f, 600f), (320f, 240f), (1f, 1f) })
        {
            var toolbar = ResponsiveHudLayout.Fit(new(width - 720, height - 52, 712, 44), width, height);
            var drawer = ResponsiveHudLayout.Fit(new(toolbar.X + toolbar.Width - 480, toolbar.Y - 310, 480, 304), width, height);
            Check(Inside(toolbar, width, height) && Inside(drawer, width, height), $"toolbar and upward drawer fit {width}x{height}");
        }
        foreach (var (width, height) in new[] { (1920f, 1080f), (1280f, 720f), (1366f, 768f) })
        foreach (var toolbarHeight in new[] { 44f, 88f })
        {
            var toolbar = new ResponsiveHudLayout.Bounds(width - 720, height - toolbarHeight - 8, 712, toolbarHeight);
            var card = ResponsiveHudLayout.AboveToolbar(new(552, height - 262, width - 560, 254), toolbar, width, height, 142);
            Check(Inside(card, width, height) && card.Y + card.Height <= toolbar.Y - 8,
                $"region dossier clears the {toolbarHeight}px map toolbar at {width}x{height}");
        }
        var separate = new ResponsiveHudLayout.Bounds(8, 400, 300, 250);
        Check(ResponsiveHudLayout.AboveToolbar(separate, new(600, 620, 650, 44), 1280, 720, 90) == separate,
            "a non-overlapping dock does not jump upwards");
        var tall = ResponsiveHudLayout.AboveToolbar(new(552, 0, 700, 5000), new(560, 620, 712, 92), 1280, 720, 142);
        Check(tall.Y >= 142 && tall.Y + tall.Height <= 612, "very tall modded facts use the available scrolling region, not the toolbar");
        var extreme = ResponsiveHudLayout.Fit(new(float.PositiveInfinity, -10000, 10000, float.NaN), 1280, 720);
        Check(Inside(extreme, 1280, 720), "out-of-range mod rectangles are clamped, not clipped off-screen");
        SpacingGrid();
        FontTokens();
        MapControls();
        Charts();
        WorkspacesClearTheRail();
        SeverityRamp();
    }

    /// <summary>
    /// A chart a reader can read: the axis rounds to a step a person would choose, every label
    /// then carries the same number of decimals, and a single reading is still a chart.
    /// </summary>
    private static void Charts()
    {
        var (low, high, decimals) = ChartScale.Axis(0f, 100f);
        Check(low == 0f && high == 100f && decimals == 0, "a round range keeps round labels");
        var (moneyLow, moneyHigh, moneyDecimals) = ChartScale.Axis(-0.34f, 1.87f);
        Check(moneyLow <= -0.34f && moneyHigh >= 1.87f && moneyDecimals <= 2,
            "an awkward range is rounded out rather than labelled with whatever it was");
        var (flatLow, flatHigh, _) = ChartScale.Axis(2f, 2f);
        Check(flatHigh > flatLow && flatHigh - flatLow >= 0.1f,
            "a flat series still gets an axis with room on both sides of its line");
        Check(ChartScale.Axis(0f, 0.01f).Decimals >= 2 &&
              ChartScale.Axis(0f, 1000f).Decimals == 0,
            "the decimals follow the size of the step, so the five labels share one shape");
    }

    /// <summary>
    /// Every workspace opens clear of the navigation rail. The rail is a FIXED number of pixels
    /// (shell.navigation_size) while the workspaces were placed by share of the window, which is
    /// exactly how the production panel came to sit under the menu on a 1280-wide screen: 4.4 %
    /// of 1920 is 84 pixels, and 4.4 % of 1280 is 56.
    /// </summary>
    /// <summary>Every workspace that opens beside the navigation rail and has to leave it clear.</summary>
    private static readonly string[] Workspaces =
    {
        "modal.ledger", "modal.war_ledger", "modal.diplomacy", "modal.trade", "modal.production",
        "modal.policies", "modal.deal", "modal.conflicts", "modal.research", "modal.doctrines", "modal.support"
    };

    private static void WorkspacesClearTheRail()
    {
        var gui = Path.Combine(GameDataLoader.DataFolder(), "gui");
        float rail;
        using (var structure = JsonDocument.Parse(File.ReadAllText(Path.Combine(gui, "20_campaign_structure.json")), GameDataLoader.ForgivingJson))
            rail = structure.RootElement.GetProperty("metrics").GetProperty("shell.navigation_size").GetSingle();
        // Read in name order and let later files win, exactly as the game does - a workspace is
        // corrected by a file that comes after the one that first declared it.
        var effective = new Dictionary<string, (float Left, float Top, float Right, float Bottom)>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(gui, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson); }
            catch { continue; }
            using (document)
            {
                if (!document.RootElement.TryGetProperty("layout", out var layout)) continue;
                foreach (var panel in layout.EnumerateObject())
                {
                    if (!panel.Value.TryGetProperty("anchors", out var anchors) || anchors.GetArrayLength() < 4) continue;
                    if (!panel.Value.TryGetProperty("offsets", out var offsets) || offsets.GetArrayLength() < 4) continue;
                    effective[panel.Name] = (anchors[0].GetSingle(), offsets[0].GetSingle(),
                        anchors[2].GetSingle(), offsets[2].GetSingle());
                }
            }
        }
        foreach (var name in Workspaces)
        {
            if (!effective.TryGetValue(name, out var placed))
            {
                Check(false, $"{name} is placed somewhere");
                continue;
            }
            foreach (var width in new[] { 800f, 1280f, 1920f })
            {
                var left = placed.Left * width + placed.Top;
                // A layer that starts at the very edge is a full-screen surface, not a workspace
                // beside the rail, and is left alone.
                if (left <= 0f) continue;
                Check(left >= rail, $"{name} opens clear of the {rail:0}-pixel rail at {width:0} ({left:0} px)");
            }
            Check(placed.Right * 800f + placed.Bottom > rail + 200f, $"{name} still has room to work in beside the rail");
        }
    }

    /// <summary>
    /// The five steps of the tension ramp have to be five colours. Green, blue, amber and red
    /// are the first four; the fifth used to be a soft salmon that sat next to the red, so a
    /// ten-pixel bar of "armed clashes" and one of "at war" read as the same bar.
    /// </summary>
    private static void SeverityRamp()
    {
        var skin = Path.Combine(GameDataLoader.DataFolder(), "interface", "00_skin.json");
        using var document = JsonDocument.Parse(File.ReadAllText(skin), GameDataLoader.ForgivingJson);
        var colors = document.RootElement.GetProperty("colors");
        string Token(string name) => colors.GetProperty(name).GetString() ?? "";
        var ramp = new[] { "state.positive", "state.info", "state.warning", "state.critical", "state.negative" }
            .Select(Token).ToArray();
        Check(ramp.All(value => value.StartsWith('#') && value.Length == 7), "every ramp step is a colour");
        Check(ramp.Distinct().Count() == ramp.Length, "no two steps of the severity ramp share a colour");
        static (int R, int G, int B) Rgb(string hex) =>
            (Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));
        for (var first = 0; first < ramp.Length; first++)
        for (var second = first + 1; second < ramp.Length; second++)
        {
            var a = Rgb(ramp[first]);
            var b = Rgb(ramp[second]);
            var distance = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            Check(distance >= 60, $"{ramp[first]} and {ramp[second]} are told apart at bar height (distance {distance})");
        }
    }

    // The §2 interface pass moved magic numbers out of the layout code and into named metrics.
    // These live in the shipped game/gui files, so this reads them the way the game does and
    // fails if a future edit quietly deletes a token or breaks the 4px rhythm.
    private static void SpacingGrid()
    {
        var gui = Path.Combine(GameDataLoader.DataFolder(), "gui");
        using (var grid = JsonDocument.Parse(File.ReadAllText(Path.Combine(gui, "63_spacing_grid.json")), GameDataLoader.ForgivingJson))
        {
            var metrics = grid.RootElement.GetProperty("metrics");
            float Token(string name) => metrics.GetProperty(name).GetSingle();
            var steps = new[] { "spacing.xs", "spacing.sm", "spacing.md", "spacing.lg", "spacing.xl" };
            var values = steps.Select(Token).ToArray();
            Check(values.SequenceEqual(new[] { 4f, 8f, 12f, 16f, 24f }) &&
                  values.All(v => MathF.Abs(v % 4f) < 0.001f) &&
                  values.SequenceEqual(values.OrderBy(v => v)),
                "the spacing grid is one ascending 4px rhythm, so unrelated panels share a unit");
            Check(Token("panel.gutter") > 0 && Token("panel.pad") > 0 && Token("hud.density") > 0,
                "panel gutter, padding and HUD density are declared once, not improvised per screen");
        }
        using (var structure = JsonDocument.Parse(File.ReadAllText(Path.Combine(gui, "20_campaign_structure.json")), GameDataLoader.ForgivingJson))
        {
            var shell = structure.RootElement.GetProperty("metrics");
            float Shell(string name) => shell.GetProperty(name).GetSingle();
            Check(Shell("shell.navigation_row_inset") == 64 && Shell("shell.navigation_row_gap") == 4 &&
                  Shell("shell.navigation_category_height") == 32 && Shell("shell.navigation_media_height") == 42,
                "the campaign navigation rail reads its geometry from named metrics, not literals");
            Check(Shell("shell.outliner_group_gap") == 10 && Shell("shell.outliner_stack_gap") == 6 &&
                  Shell("shell.outliner_row_gap") == 4 && Shell("shell.outliner_section_gap") == 3,
                "the outliner rail reads its gaps from named metrics, not literals");
        }
        using (var readability = JsonDocument.Parse(File.ReadAllText(Path.Combine(gui, "29_hud_readability.json")), GameDataLoader.ForgivingJson))
        {
            var hud = readability.RootElement.GetProperty("metrics");
            float Hud(string name) => hud.GetProperty(name).GetSingle();
            Check(Hud("hud.tabular_numbers") == 1 && Hud("hud.alert_row_gap") == 6,
                "the HUD declares tabular figures and the alert row gap once, not per screen");
        }
    }

    /// <summary>
    /// The interface's type sizes, declared once in data the same way its spacing is.
    ///
    /// A token a future edit quietly deletes is a screen that quietly falls back to whatever
    /// number its caller happened to pass as the fallback, and the two screens that shared the
    /// token stop matching each other - the same failure the spacing grid is guarded against,
    /// which until now had no guard of its own.
    /// </summary>
    private static void FontTokens()
    {
        var gui = Path.Combine(GameDataLoader.DataFolder(), "gui");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(gui, "28_ui_font.json")), GameDataLoader.ForgivingJson);
        var metrics = document.RootElement.GetProperty("metrics");
        float Token(string name) => metrics.GetProperty(name).GetSingle();

        // Every size the interface names has to be declared, and be a size a person would set:
        // legible at the small end, and not so large at the big end that a heading wraps.
        var named = new[]
        {
            "ui.font.button", "ui.font.button_small", "ui.font.button_large", "ui.font.caption",
            "ui.font.body", "ui.font.tooltip", "ui.font.heading_small", "ui.font.heading",
            "ui.font.heading_large", "ui.font.navigation", "ui.font.tab", "ui.font.console"
        };
        foreach (var name in named)
            Check(metrics.TryGetProperty(name, out var declared) &&
                  declared.GetSingle() >= 9f && declared.GetSingle() <= 32f,
                $"{name} is declared once, and is a readable size");

        // And ordered the way the names claim. If these tokens were decoration rather than a
        // scale, every comparison here would be a coin toss.
        Check(Token("ui.font.button_small") <= Token("ui.font.button") &&
              Token("ui.font.button") <= Token("ui.font.button_large"),
            "the button sizes run small, ordinary, large in that order");
        Check(Token("ui.font.caption") <= Token("ui.font.body") &&
              Token("ui.font.heading_small") <= Token("ui.font.heading") &&
              Token("ui.font.heading") < Token("ui.font.heading_large"),
            "a caption sits below body text, and the headings ascend");
        Check(Token("ui.font.caption") >= 10f && Token("ui.font.navigation") >= 10f,
            "the smallest sizes in the interface are still legible");
    }

    /// <summary>
    /// The map's own controls, declared once in data and held to what the code reading them will
    /// actually accept.
    ///
    /// Every one of these numbers is clamped where it is used, so a value outside the range is
    /// not a crash and not a warning - it is a number in a file that quietly does nothing, and
    /// the person who edited it concludes the interface ignores data. This is the guard that says
    /// so instead. The data existed with nothing holding it to anything.
    /// </summary>
    private static void MapControls()
    {
        var gui = Path.Combine(GameDataLoader.DataFolder(), "gui");
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(gui, "62_responsive_map_controls.json")), GameDataLoader.ForgivingJson);
        var metrics = document.RootElement.GetProperty("metrics");
        float Token(string name) => metrics.GetProperty(name).GetSingle();

        foreach (var name in new[]
                 {
                     "map_controls.drawer_width", "map_controls.drawer_height",
                     "map_controls.drawer_cell_width", "map.controls_flow_h_gap",
                     "map.controls_flow_v_gap", "map.controls_button_height",
                     "picker.actions_bottom_inset"
                 })
            Check(metrics.TryGetProperty(name, out var declared) && declared.GetSingle() > 0f,
                $"{name} is declared and is a real size");

        // Inside the range the map clamps to, or the number in the file is decoration.
        Check(Token("map_controls.drawer_width") is >= 200f and <= 720f,
            "the drawer width is inside the range the map clamps it to");
        // Half of the smallest supported window height, which is the other half of that clamp.
        Check(Token("map_controls.drawer_height") <= 360f,
            "the drawer height fits half of a 720-high window, which is all the map gives it");

        // The columns the drawer will actually draw for its own width: at least one, and never
        // more than the three the layout is built for.
        var columns = ResponsiveHudLayout.DrawerColumns(Token("map_controls.drawer_width") - 28f,
            Token("map_controls.drawer_cell_width"));
        Check(columns is >= 1 and <= 3,
            $"the drawer's own width lays out {columns} column(s), between one and three");

        // A button a person can hit. The code's own fallback is 34, so anything under 32 is a
        // regression wearing a data change as a disguise.
        Check(Token("map.controls_button_height") >= 32f,
            "the map control buttons are a comfortable click target");
    }

    private static bool Inside(ResponsiveHudLayout.Bounds b, float width, float height) =>
        float.IsFinite(b.X + b.Y + b.Width + b.Height) && b.X >= 0 && b.Y >= 0 &&
        b.Width > 0 && b.Height > 0 && b.X + b.Width <= width && b.Y + b.Height <= height;
}
