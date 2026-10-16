using System;
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
    }

    private static bool Inside(ResponsiveHudLayout.Bounds b, float width, float height) =>
        float.IsFinite(b.X + b.Y + b.Width + b.Height) && b.X >= 0 && b.Y >= 0 &&
        b.Width > 0 && b.Height > 0 && b.X + b.Width <= width && b.Y + b.Height <= height;
}
