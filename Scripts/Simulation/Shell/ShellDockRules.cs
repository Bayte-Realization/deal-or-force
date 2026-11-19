using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation.Shell;

public sealed record ShellDockGeometry(
    ShellRect LeftPanel,
    ShellRect BottomStrip,
    ShellRect PlanningBar,
    bool CompactStripTokens);

/// <summary>
/// Pure anti-overlap docking rules for Left Panels (Army, Side, Fleet, Dossier), PlanningBar,
/// BottomStrip, FullWorkspace modals, and region-icon visibility in the campaign shell.
/// </summary>
public static class ShellDockRules
{
    public static bool ShouldShowRegionIcons(
        bool isNewShell,
        bool explicitUserToggle,
        string openScreen,
        bool productionScreenVisible,
        bool resourcesOrEconomyMapMode)
    {
        if (!isNewShell) return true;
        if (explicitUserToggle || productionScreenVisible || resourcesOrEconomyMapMode) return true;
        return string.Equals(openScreen, "Economy", StringComparison.Ordinal)
            || string.Equals(openScreen, "Production", StringComparison.Ordinal);
    }

    public static ShellDockGeometry ComputeLeftAndBottomDock(
        ShellLayoutProfile profile,
        bool anyLeftPanelVisible,
        bool fullWorkspaceVisible = false,
        float planningBarDesiredWidth = 520f)
    {
        var left = profile.LeftPanel;
        var stripX = anyLeftPanelVisible ? left.Right + 10f : profile.Strip.X;
        var maxRight = profile.MapBar.X - 12f;
        var stripW = Math.Max(120f, maxRight - stripX);
        var strip = new ShellRect(stripX, profile.Strip.Y, stripW, profile.Strip.H);

        var pbarH = profile.Compact ? 42f : 52f;
        var pbarX = anyLeftPanelVisible ? left.Right + 10f : strip.X;
        var pbarMaxW = Math.Max(180f, maxRight - pbarX);
        var pbarW = Math.Min(Math.Max(220f, planningBarDesiredWidth), pbarMaxW);
        var pbarBottom = strip.Y - 6f;
        var pbar = new ShellRect(pbarX, pbarBottom - pbarH, pbarW, pbarH);

        return new ShellDockGeometry(left, strip, pbar, CompactStripTokens: anyLeftPanelVisible || fullWorkspaceVisible);
    }

    public static (IReadOnlyDictionary<string, ShellRect> Rects, IReadOnlyList<string> Overlaps, IReadOnlyList<string> Offscreen)
        AuditDockScenario(float width, float height, bool leftPanelOpen, bool planningBarOpen, bool atWar = false, bool fullWorkspaceOpen = false)
    {
        var profile = ShellLayout.ForViewport(width, height, atWar);
        var dock = ComputeLeftAndBottomDock(profile, leftPanelOpen, fullWorkspaceOpen);
        var rects = new Dictionary<string, ShellRect>(StringComparer.Ordinal)
        {
            ["top_plate"] = profile.TopPlate,
            ["left_rail"] = profile.LeftRail,
            ["corner_node"] = profile.CornerNode,
            ["date_plate"] = profile.DatePlate,
            ["map_bar"] = profile.MapBar,
            ["bottom_strip"] = dock.BottomStrip
        };
        if (!fullWorkspaceOpen) rects["alert_column"] = profile.AlertColumn;
        if (leftPanelOpen && !fullWorkspaceOpen) rects["left_panel"] = dock.LeftPanel;
        if (planningBarOpen && !fullWorkspaceOpen) rects["planning_bar"] = dock.PlanningBar;
        if (fullWorkspaceOpen) rects["full_workspace"] = profile.FullWorkspace;

        var (overlaps, offscreen) = CheckRectSet(
            rects,
            profile.ViewportWidth,
            profile.ViewportHeight,
            ignorePair: static (a, b) =>
                (a == "corner_node" && (b == "top_plate" || b == "left_rail")) ||
                (b == "corner_node" && (a == "top_plate" || a == "left_rail")));
        return (rects, overlaps, offscreen);
    }

    internal static (List<string> Overlaps, List<string> Offscreen) CheckRectSet(
        Dictionary<string, ShellRect> named,
        float viewportW,
        float viewportH,
        Func<string, string, bool> ignorePair = null)
    {
        var overlaps = new List<string>();
        var offscreen = new List<string>();
        var keys = new List<string>(named.Keys);
        for (var i = 0; i < keys.Count; i++)
        {
            var aKey = keys[i];
            var aRect = named[aKey];
            if (!aRect.Inside(viewportW, viewportH))
                offscreen.Add(aKey);
            for (var j = i + 1; j < keys.Count; j++)
            {
                var bKey = keys[j];
                if (ignorePair is not null && ignorePair(aKey, bKey)) continue;
                if (aRect.Intersects(named[bKey]))
                    overlaps.Add($"{aKey} vs {bKey}");
            }
        }
        return (overlaps, offscreen);
    }
}
