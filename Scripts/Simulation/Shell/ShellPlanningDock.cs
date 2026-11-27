using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation.Shell;

/// <summary>
/// Pure anti-overlap layout helper for the Army Panel and Battle-Plan Planning Bar across
/// desktop and touch-primary viewports (e.g. Android 2340x1080, 1733x800, 1280x720 with notches).
/// Zero Godot references.
/// </summary>
public static class ShellPlanningDock
{
    private const float DockGap = 10f;
    private const float RightGap = 12f;
    private const float BarStripGap = 10f;
    private const float TopClearance = 8f;
    private const float AlertClearance = 4f;
    private const float MinStripW = 120f;
    private const float MinBarAvailW = 180f;
    private const float MinBarW = 220f;
    private const float CompactBarH = 42f;
    private const float FullBarH = 52f;
    private const float TouchDefaultBarH = 74f;
    private const float TouchCompactWideThreshold = 1400f;
    private const float TouchCompactLeftW = 364f;
    private const float MinLeftW = 240f;
    private const float MinLeftH = 220f;

    public static ShellDockGeometry ComputeDock(
        ShellLayoutProfile profile,
        bool anyLeftPanelVisible,
        bool fullWorkspaceVisible = false,
        float planningBarDesiredWidth = 520f,
        float planningBarDesiredHeight = 0f)
    {
        var left = ResolveLeftPanel(profile);
        var stripX = anyLeftPanelVisible ? left.Right + DockGap : profile.Strip.X;
        var maxRight = profile.MapBar.X - RightGap;
        var stripW = MathF.Max(MinStripW, maxRight - stripX);
        var strip = new ShellRect(stripX, profile.Strip.Y, stripW, profile.Strip.H);

        var baseBarH = profile.TouchMode
            ? TouchDefaultBarH
            : (profile.Compact ? CompactBarH : FullBarH);
        var wantedBarH = planningBarDesiredHeight > 0f
            ? MathF.Max(baseBarH, planningBarDesiredHeight)
            : baseBarH;

        var pbarBottom = strip.Y - BarStripGap;
        var minTop = MathF.Max(profile.TopPlate.Bottom + TopClearance, profile.NewsCard.Bottom + TopClearance);
        if (profile.TouchModeBar.W > 0f && profile.TouchModeBar.H > 0f)
            minTop = MathF.Max(minTop, profile.TouchModeBar.Bottom + TopClearance);
        var maxBarH = MathF.Max(baseBarH, pbarBottom - minTop);
        var pbarH = MathF.Min(wantedBarH, maxBarH);
        var pbarY = pbarBottom - pbarH;

        var minimap = ShellLayout.MinimapRect(profile);
        var pbarMaxRight = MathF.Min(maxRight, minimap.X - RightGap);
        if (pbarY < profile.AlertColumn.Bottom + AlertClearance)
            pbarMaxRight = MathF.Min(pbarMaxRight, profile.AlertColumn.X - RightGap);

        var pbarX = anyLeftPanelVisible ? left.Right + DockGap : strip.X;
        var pbarMaxW = MathF.Max(MinBarAvailW, pbarMaxRight - pbarX);
        var pbarW = MathF.Min(MathF.Max(MinBarW, planningBarDesiredWidth), pbarMaxW);
        var pbar = new ShellRect(pbarX, pbarY, pbarW, pbarH);

        return new ShellDockGeometry(left, strip, pbar, CompactStripTokens: anyLeftPanelVisible || fullWorkspaceVisible);
    }

    public static ShellRect ResolveLeftPanel(ShellLayoutProfile profile)
    {
        var left = profile.LeftPanel;
        if (!profile.TouchMode) return left;

        var maxBottom = profile.Strip.Y - TopClearance;
        if (left.Bottom > maxBottom)
            left = left with { H = MathF.Max(MinLeftH, maxBottom - left.Y) };

        if (profile.Compact)
        {
            var targetW = profile.ViewportWidth >= TouchCompactWideThreshold
                ? MathF.Max(left.W, TouchCompactLeftW)
                : left.W;
            var rightCap = MathF.Min(profile.NewsCard.X - RightGap, profile.AlertColumn.X - RightGap);
            var cappedW = MathF.Max(MinLeftW, MathF.Min(targetW, rightCap - left.X));
            left = left with { W = cappedW };
        }
        return left;
    }

    public static (IReadOnlyDictionary<string, ShellRect> Rects, IReadOnlyList<string> Overlaps, IReadOnlyList<string> Offscreen)
        AuditTouchDockScenario(
            float width,
            float height,
            bool atWar = true,
            float safeLeft = 0f,
            float safeTop = 0f,
            float safeRight = 0f,
            float safeBottom = 0f,
            float planningBarWidth = 640f,
            float planningBarHeight = 76f)
    {
        var profile = ShellLayout.ForViewport(width, height, atWar, touchMode: true, safeLeft, safeTop, safeRight, safeBottom);
        var dock = ComputeDock(profile, anyLeftPanelVisible: true, fullWorkspaceVisible: false, planningBarWidth, planningBarHeight);
        var rects = new Dictionary<string, ShellRect>(StringComparer.Ordinal)
        {
            ["top_plate"] = profile.TopPlate,
            ["left_rail"] = profile.LeftRail,
            ["corner_node"] = profile.CornerNode,
            ["date_plate"] = profile.DatePlate,
            ["touch_mode_bar"] = profile.TouchModeBar,
            ["alert_column"] = profile.AlertColumn,
            ["map_bar"] = profile.MapBar,
            ["minimap"] = ShellLayout.MinimapRect(profile),
            ["xp_strip"] = ShellLayout.XpStripRect(profile),
            ["bottom_strip"] = dock.BottomStrip,
            ["left_panel"] = dock.LeftPanel,
            ["planning_bar"] = dock.PlanningBar
        };
        var (overlaps, offscreen) = ShellDockRules.CheckRectSet(
            rects,
            profile.ViewportWidth,
            profile.ViewportHeight,
            ignorePair: static (a, b) =>
                (a == "corner_node" && (b == "top_plate" || b == "left_rail")) ||
                (b == "corner_node" && (a == "top_plate" || a == "left_rail")));
        return (rects, overlaps, offscreen);
    }
}
