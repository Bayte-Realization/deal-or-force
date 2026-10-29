using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

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
            foreach (var (w, h) in new[] { (1280f, 720f), (1920f, 1080f) })
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

        Console.WriteLine($"shell coherence: {panels.Length} panels, {navigation.Length} rail buttons, all promises kept");
    }
}
