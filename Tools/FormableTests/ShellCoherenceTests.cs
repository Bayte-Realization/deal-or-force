using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

// The shell's own promises, checked forever:
//   - every named panel fits on the smallest screen the game promises (1280x720),
//   - the four always-visible HUD regions do not overlap each other,
//   - every reachable destination has a navigation button, and every button has its words,
//   - a modal is big enough to be a modal, on the smallest screen too.
static class ShellCoherenceTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("shell coherence: " + label);
        Console.WriteLine("PASS shell coherence: " + label);
    }

    static (float Left, float Top, float Right, float Bottom) Pixels(InterfaceSkin.PanelRect rect, float width, float height) =>
        (rect.AnchorLeft * width + rect.OffsetLeft,
            rect.AnchorTop * height + rect.OffsetTop,
            rect.AnchorRight * width + rect.OffsetRight,
            rect.AnchorBottom * height + rect.OffsetBottom);

    public static void Run()
    {
        InterfaceSkin.ResetCatalog();

        // 1. Everything fits. A panel that runs off the screen at 720p is a button the
        // player cannot reach, whatever the layout editor thought it drew.
        var panels = InterfaceSkin.Panels.ToArray();
        Check(panels.Length >= 20, $"the skin names its panels ({panels.Length} found)");
        var offenders = new List<string>();
        foreach (var (key, rect) in panels)
        {
            // Responsive breakpoint panels (`shell.<name>` with a dedicated `shell.compact.<name>`
            // override in 90_shell.json) only apply at >=1600x900; their `shell.compact.*`
            // counterparts serve 1280x720.
            var hasCompactOverride = key.StartsWith("shell.", StringComparison.Ordinal) &&
                !key.StartsWith("shell.compact.", StringComparison.Ordinal) &&
                InterfaceSkin.HasLayout("shell.compact." + key["shell.".Length..]);
            var viewports = hasCompactOverride
                ? new[] { (1920f, 1080f) }
                : new[] { (1280f, 720f), (1920f, 1080f) };
            foreach (var (w, h) in viewports)
            {
                var (left, top, right, bottom) = Pixels(rect, w, h);
                if (left < -0.5f || top < -0.5f || right > w + 0.5f || bottom > h + 0.5f ||
                    right <= left + 1f || bottom <= top + 1f)
                    offenders.Add($"{key}@{w:0}x{h:0}: {left:0},{top:0}..{right:0},{bottom:0}");
            }
        }
        Check(offenders.Count == 0, "every named panel fits 1280x720 and 1920x1080" +
            (offenders.Count == 0 ? "" : " — " + string.Join("; ", offenders.Take(6))));

        // 2. The co-visible HUD does not stack. The top bar, the navigation rail, the
        // outliner and the map controls are on screen together for the whole campaign;
        // anything else may share a column with a sibling by design, these four may not.
        string[] coVisible = { "hud.top", "hud.navigation", "hud.outliner", "hud.map_controls" };
        var overlaps = new List<string>();
        for (var a = 0; a < coVisible.Length; a++)
            for (var b = a + 1; b < coVisible.Length; b++)
            {
                if (!InterfaceSkin.HasLayout(coVisible[a]) || !InterfaceSkin.HasLayout(coVisible[b])) continue;
                var first = Pixels(InterfaceSkin.Layout(coVisible[a]), 1280, 720);
                var second = Pixels(InterfaceSkin.Layout(coVisible[b]), 1280, 720);
                var shared = Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left);
                var tall = Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top);
                if (shared > 1f && tall > 1f)
                    overlaps.Add($"{coVisible[a]} x {coVisible[b]} share {shared:0}x{tall:0}px");
            }
        Check(overlaps.Count == 0, "the four always-visible HUD regions leave each other room" +
            (overlaps.Count == 0 ? "" : " — " + string.Join("; ", overlaps)));

        // 3. Reachability. Every destination the interface can route to has a button on
        // the rail; a screen reachable only from another screen is a screen players
        // report as missing.
        var navigation = CampaignUiCatalog.NavigationItems.ToArray();
        var onRail = navigation.Select(item => item.Screen).ToHashSet(StringComparer.Ordinal);
        var unreachable = CampaignUiCatalog.Destinations
            .Where(screen => !onRail.Contains(screen)).ToArray();
        Check(unreachable.Length == 0, "every destination has a navigation button" +
            (unreachable.Length == 0 ? "" : " — missing: " + string.Join(", ", unreachable)));
        Check(navigation.Length >= 12, $"the rail carries the game's screens ({navigation.Length} buttons)");

        // 4. Every button has its words. A short caption, a full label, help for the
        // tooltip and a group to file under - an empty one of these is an unlabelled
        // button or a row that cannot be collapsed.
        var silent = navigation.Where(item =>
            string.IsNullOrWhiteSpace(item.Label) || string.IsNullOrWhiteSpace(item.Short) ||
            string.IsNullOrWhiteSpace(item.Help) || string.IsNullOrWhiteSpace(item.Group)).ToArray();
        Check(silent.Length == 0, "every navigation button is captioned, short-captioned, helped and grouped" +
            (silent.Length == 0 ? "" : " — " + string.Join(", ", silent.Select(item => item.Screen).Take(6))));

        // 5. A modal is a modal: no dialog smaller than a quarter of the smallest screen,
        // whatever its contents. Below that the game reads as a pop-up, not a workspace.
        var smallModals = new List<string>();
        foreach (var (key, rect) in panels)
        {
            if (!key.StartsWith("modal.", StringComparison.Ordinal)) continue;
            var (left, top, right, bottom) = Pixels(rect, 1280, 720);
            if (right - left < 300f || bottom - top < 240f)
                smallModals.Add($"{key} {right - left:0}x{bottom - top:0}");
        }
        Check(smallModals.Count == 0, "no modal is smaller than 300x240 on the smallest screen" +
            (smallModals.Count == 0 ? "" : " — " + string.Join("; ", smallModals)));

        // 6. The tab strip is one height everywhere. Screens may differ in everything
        // except the strip you click through, which is what makes them feel like one game.
        var tabHeight = InterfaceSkin.Metric("shell.tab_height", 34f);
        Check(tabHeight is >= 28f and <= 44f, $"the tab strip height is standard ({tabHeight:0}px)");
        var rowHeight = InterfaceSkin.Metric("shell.navigation_height", 50f);
        Check(rowHeight is >= 40f and <= 60f, $"the navigation row height is standard ({rowHeight:0}px)");

        // 7. New Shell layout geometry on desktop (touchMode: false) and mobile/touch (touchMode: true):
        // Desktop must keep TouchModeBar empty; touch viewports must dock TouchModeBar immediately left
        // of DatePlate, keep the bottom-right corner Minimap unobstructed, and have zero overlaps.
        var desktop1080 = ShellLayout.ForViewport(1920f, 1080f, atWar: true, touchMode: false);
        Check(desktop1080.TouchModeBar.W == 0f && desktop1080.TouchModeBar.H == 0f,
            "desktop ShellLayout keeps TouchModeBar empty");
        foreach (var (vw, vh) in new[] { (1280f, 720f), (1600f, 720f), (1920f, 1080f), (2400f, 1080f) })
        {
            foreach (var atWar in new[] { false, true })
            {
                var p = ShellLayout.ForViewport(vw, vh, atWar: atWar, touchMode: true);
                var mini = ShellLayout.MinimapRect(p);
                var tag = $"{vw:0}x{vh:0} {(atWar ? "war" : "peace")}";
                Check(p.TouchModeBar.Inside(vw, vh) &&
                      p.TouchModeBar.Right <= p.DatePlate.X &&
                      MathF.Abs(p.TouchModeBar.Y - p.DatePlate.Y) < 0.5f &&
                      MathF.Abs(p.TouchModeBar.H - p.DatePlate.H) < 0.5f,
                    $"touch mode bar docked left of DatePlate ({tag})");
                Check(p.TouchModeBar.W >= 156f && p.TouchModeBar.H >= 96f,
                    $"touch mode bar fits 2x2 >=72x44 touch buttons ({tag})");
                var hudRects = new Dictionary<string, ShellRect>(StringComparer.Ordinal)
                {
                    ["top_plate"] = p.TopPlate,
                    ["left_rail"] = p.LeftRail,
                    ["touch_mode_bar"] = p.TouchModeBar,
                    ["date_plate"] = p.DatePlate,
                    ["alert_column"] = p.AlertColumn,
                    ["map_bar"] = p.MapBar,
                    ["minimap"] = mini,
                    ["strip"] = p.Strip,
                    ["news_card"] = p.NewsCard
                };
                var (touchOverlaps, touchOffscreen) = ShellDockRules.CheckRectSet(hudRects, vw, vh);
                Check(touchOverlaps.Count == 0 && touchOffscreen.Count == 0,
                    $"touch HUD zero overlaps & on-screen ({tag})" +
                    (touchOverlaps.Count + touchOffscreen.Count == 0
                        ? ""
                        : $" — overlaps=[{string.Join(", ", touchOverlaps)}] offscreen=[{string.Join(", ", touchOffscreen)}]"));
            }
        }

        Console.WriteLine($"shell coherence: {panels.Length} panels, {navigation.Length} rail buttons, all promises kept");
    }
}
