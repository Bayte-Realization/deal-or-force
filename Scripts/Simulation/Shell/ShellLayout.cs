using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation.Shell;

public readonly record struct ShellRect(float X, float Y, float W, float H)
{
    private const float Eps = 0.5f;
    public float Right => X + W;
    public float Bottom => Y + H;

    public bool Inside(float viewportWidth, float viewportHeight) =>
        W > 0f && H > 0f && X >= -Eps && Y >= -Eps && Right <= viewportWidth + Eps && Bottom <= viewportHeight + Eps;

    public bool Intersects(ShellRect other) =>
        X < other.Right - Eps && Right > other.X + Eps &&
        Y < other.Bottom - Eps && Bottom > other.Y + Eps;

    public static ShellRect FromOffsets(InterfaceSkin.PanelRect panel) =>
        new(panel.OffsetLeft, panel.OffsetTop,
            Math.Max(0f, panel.OffsetRight - panel.OffsetLeft),
            Math.Max(0f, panel.OffsetBottom - panel.OffsetTop));
}

public readonly record struct FlagFrame(float Width, float Height, float OffsetX, float OffsetY, bool CutoutSilhouette);

public enum ShellCampaignState { Peace, Crisis, War }

public sealed record ShellLayoutProfile(
    bool Compact,
    float ViewportWidth,
    float ViewportHeight,
    ShellRect TopPlate,
    ShellRect LeftRail,
    ShellRect CornerNode,
    ShellRect DatePlate,
    ShellRect AlertColumn,
    ShellRect MapBar,
    ShellRect Strip,
    ShellRect NewsCard,
    ShellRect LeftPanel,
    ShellRect FullWorkspace,
    float FlagWellWidth,
    float FlagWellHeight,
    float StatCellWidth,
    float StatCellHeight,
    float RailButtonSize,
    float MapButtonSize,
    bool TouchMode = false,
    ShellRect TouchModeBar = default);

public sealed record ShellRestCensus(
    int Buttons,
    int Panels,
    IReadOnlyDictionary<string, ShellRect> Rects,
    IReadOnlyList<string> Overlaps,
    IReadOnlyList<string> Offscreen);

public static class ShellLayout
{
    private const float DefWidth = 1920f, DefHeight = 1080f, MinVpW = 320f, MinVpH = 240f;
    private const float CompactBreakW = 1600f, CompactBreakH = 900f;
    private const float CompactCellX0 = 82f, FullCellX0 = 120f, CompactCellW = 102f, FullCellW = 144f;
    private const float CompactTopH = 48f, FullTopH = 70f, CompactRailW = 50f, FullRailW = 64f, CompactRailH = 504f, FullRailH = 722f;
    private const float CompactCornerW = 76f, CompactCornerH = 86f, FullCornerW = 112f, FullCornerH = 124f;
    private const float CompactDateW = 322f, CompactDateH = 58f, FullDateW = 440f, FullDateH = 84f;
    private const float CompactAlertW = 284f, FullAlertW = 376f, CompactMapBarW = 216f, CompactMapBarH = 40f, FullMapBarW = 312f, FullMapBarH = 56f;
    private const float CompactStripH = 46f, FullStripH = 68f, DockGap = 8f, WideGap = 12f;
    private const float CompactLeftRight = 390f, FullLeftRight = 544f, LeftBottomClearance = 92f;
    private const float CompactNewsX = 460f, CompactNewsY = 56f, CompactNewsW = 370f, CompactNewsH = 76f;
    private const float FullNewsX = 700f, FullNewsY = 82f, FullNewsW = 540f, FullNewsH = 114f;
    private const float DefaultButtonBudget = 41f, DefaultPanelBudget = 6f, DefaultSignBudget = 20f;

    public static int MaxRestButtons => (int)InterfaceSkin.Metric("shell.max_rest_buttons", DefaultButtonBudget);
    public static int MaxRestPanels => (int)InterfaceSkin.Metric("shell.max_rest_panels", DefaultPanelBudget);
    public static int MaxMapSigns => (int)InterfaceSkin.Metric("shell.max_map_signs", DefaultSignBudget);

    public static FlagFrame FitFlag(float texWidth, float texHeight, float maxWidth, float maxHeight, string nationTag = "")
    {
        const float defAspectW = 3f, defAspectH = 2f, roundStep = 10f, half = 0.5f;
        if (maxWidth <= 0f || maxHeight <= 0f) return new(0f, 0f, 0f, 0f, false);
        var w = texWidth > 0f ? texWidth : defAspectW;
        var h = texHeight > 0f ? texHeight : defAspectH;
        var scale = Math.Min(maxWidth / w, maxHeight / h);
        var fittedW = MathF.Max(1f, MathF.Round(w * scale * roundStep) / roundStep);
        var fittedH = MathF.Max(1f, MathF.Round(h * scale * roundStep) / roundStep);
        var cutout = string.Equals(nationTag, "NPL", StringComparison.OrdinalIgnoreCase);
        return new(fittedW, fittedH, (maxWidth - fittedW) * half, (maxHeight - fittedH) * half, cutout);
    }

    /// <summary>Corner minimap rectangle anchored cleanly above the bottom-right MapBar.</summary>
    public static ShellRect MinimapRect(ShellLayoutProfile profile)
    {
        var miniW = profile.Compact ? 196f : 260f;
        var miniH = profile.Compact ? 98f : 130f;
        return new ShellRect(profile.MapBar.Right - miniW, profile.MapBar.Y - miniH - 6f, miniW, miniH);
    }

    /// <summary>
    /// Doctrine/XP strip anchored cleanly to the right of the CornerNode (flag + rank)
    /// and below TopPlate, preserving exact desktop coordinates at SafeArea=0 while shifting
    /// with SafeArea on notched/rounded mobile screens so it never overlaps the flag, rank, or rail.
    /// </summary>
    public static ShellRect XpStripRect(ShellLayoutProfile profile)
    {
        var compact = profile.Compact;
        var gapX = compact ? 4f : 6f;
        var gapY = compact ? 2f : 4f;
        var x = Math.Max(profile.TopPlate.X + (compact ? 80f : 118f), profile.CornerNode.Right + gapX);
        var y = Math.Max(profile.TopPlate.Bottom + gapY, profile.TopPlate.Y + (compact ? 50f : 74f));
        var baseW = compact ? 276f : 348f;
        var h = compact ? 32f : 40f;
        var margin = compact ? 8f : 12f;
        var rightLimit = profile.NewsCard.X - margin;
        if (profile.TouchModeBar.W > 0f && profile.TouchModeBar.Bottom > y)
            rightLimit = Math.Min(rightLimit, profile.TouchModeBar.X - margin);
        var w = rightLimit > x + 120f ? Math.Min(baseW, rightLimit - x) : baseW;
        return new ShellRect(x, y, w, h);
    }

    public static ShellLayoutProfile ForViewport(
        float width, float height, bool atWar = false, bool touchMode = false,
        float safeLeft = 0f, float safeTop = 0f, float safeRight = 0f, float safeBottom = 0f)
    {
        const int warCells = 9, peaceCells = 8;
        const float compactPad = 10f, fullPad = 14f, compactMargin = 6f;
        var fullW = float.IsFinite(width) && width > MinVpW ? width : DefWidth;
        var fullH = float.IsFinite(height) && height > MinVpH ? height : DefHeight;
        var sL = float.IsFinite(safeLeft) ? Math.Clamp(safeLeft, 0f, fullW * 0.20f) : 0f;
        var sT = float.IsFinite(safeTop) ? Math.Clamp(safeTop, 0f, fullH * 0.15f) : 0f;
        var sR = float.IsFinite(safeRight) ? Math.Clamp(safeRight, 0f, fullW * 0.20f) : 0f;
        var sB = float.IsFinite(safeBottom) ? Math.Clamp(safeBottom, 0f, fullH * 0.15f) : 0f;
        var w = Math.Max(MinVpW, fullW - sL - sR);
        var h = Math.Max(MinVpH, fullH - sT - sB);
        var compact = w < CompactBreakW || h < CompactBreakH;
        var prefix = compact ? "shell.compact." : "shell.";
        var cellCount = atWar ? warCells : peaceCells;
        var cellX0 = compact ? CompactCellX0 : FullCellX0;
        var wantedTopW = cellX0 + cellCount * (compact ? CompactCellW : FullCellW) + (compact ? compactPad : fullPad);
        var rightMargin = touchMode ? DockGap : (compact ? compactMargin : DockGap);

        var top = Resolve(prefix + "top_plate", new ShellRect(0f, 0f, wantedTopW, compact ? CompactTopH : FullTopH)) with { W = wantedTopW };
        var rail = Resolve(prefix + "rail", compact ? new ShellRect(0f, CompactTopH, CompactRailW, CompactRailH) : new ShellRect(0f, FullTopH, FullRailW, FullRailH));
        var corner = compact ? new ShellRect(0f, 0f, CompactCornerW, CompactCornerH) : new ShellRect(0f, 0f, FullCornerW, FullCornerH);
        var date = AnchorRight(Resolve(prefix + "date_plate", compact ? new ShellRect(w - CompactDateW - compactMargin, compactMargin, CompactDateW, CompactDateH) : new ShellRect(w - FullDateW - DockGap, DockGap, FullDateW, FullDateH)), w, rightMargin);
        var alert = AnchorRight(Resolve(prefix + "alert_column", compact ? new ShellRect(w - CompactAlertW - compactMargin, CompactDateH + DockGap, CompactAlertW, Math.Max(MinVpH, h - MinVpH)) : new ShellRect(w - FullAlertW - DockGap, FullDateH + compactPad, FullAlertW, Math.Max(MinVpW, h - MinVpW))), w, rightMargin);
        var mapBar = AnchorBottomRight(Resolve(prefix + "map_bar", compact ? new ShellRect(w - CompactMapBarW - compactMargin, h - CompactMapBarH - compactMargin, CompactMapBarW, CompactMapBarH) : new ShellRect(w - FullMapBarW - DockGap, h - FullMapBarH - DockGap, FullMapBarW, FullMapBarH)), w, h, rightMargin);
        ShellRect touchModeBar = default;

        if (touchMode)
        {
            // Balanced touch DatePlate (430..470) + 156px TouchModeBar gives TopPlate +50..63px extra
            // room at 1280w so 3-digit values + Russian units (млрд/трлн/тыс) fit even at war (8 cells).
            var touchDateW = Math.Clamp(MathF.Round(w * 0.34f), 430f, 470f);
            const float touchDateH = 102f;
            date = AnchorRight(new ShellRect(w - touchDateW - rightMargin, rightMargin, touchDateW, touchDateH), w, rightMargin);

            const float touchBarW = 156f;
            var touchBarX = Math.Max(corner.Right + DockGap, date.X - DockGap - touchBarW);
            touchModeBar = new ShellRect(touchBarX, date.Y, touchBarW, date.H);

            var touchRailW = Math.Max(rail.W, 60f);
            rail = rail with { W = touchRailW, H = Math.Max(rail.H, h - top.Bottom - DockGap) };
            const float touchMapW = 328f, touchMapH = 60f;
            mapBar = AnchorBottomRight(new ShellRect(w - touchMapW - rightMargin, h - touchMapH - rightMargin, touchMapW, touchMapH), w, h, rightMargin);

            var minimapReserve = (compact ? 98f : 130f) + 14f;
            var alertY = date.Bottom + DockGap;
            alert = alert with { Y = alertY, H = Math.Max(160f, mapBar.Y - minimapReserve - alertY) };
        }

        var topRightLimit = touchMode ? touchModeBar.X - DockGap : date.X - DockGap;
        if (top.Right > topRightLimit) top = top with { W = Math.Max(120f, topRightLimit - top.X) };

        var stripDef = compact ? new ShellRect(CompactRailW + DockGap, h - CompactStripH - compactMargin, Math.Max(MinVpH, mapBar.X - CompactCellX0), CompactStripH) : new ShellRect(FullRailW + DockGap, h - FullStripH - DockGap, Math.Max(MinVpW, mapBar.X - FullCellX0), FullStripH);
        if (touchMode) stripDef = stripDef with { H = Math.Max(stripDef.H, 54f) };
        var strip = AnchorBottomStrip(Resolve(prefix + "strip", stripDef), rail.Right + DockGap, mapBar.X - WideGap, h, rightMargin);
        var (leftPanel, fullWorkspace, news) = BuildInteriorZones(compact, prefix, w, h, top, rail, corner, date, alert, strip, touchModeBar);
        var dynamicCellW = MathF.Max(1f, MathF.Floor(Math.Max(0f, top.W - cellX0 - (compact ? DockGap : WideGap)) / cellCount));

        if (sL > 0f || sT > 0f)
        {
            top = Shift(top, sL, sT); rail = Shift(rail, sL, sT); corner = Shift(corner, sL, sT);
            date = Shift(date, sL, sT); alert = Shift(alert, sL, sT); mapBar = Shift(mapBar, sL, sT);
            strip = Shift(strip, sL, sT); news = Shift(news, sL, sT);
            leftPanel = Shift(leftPanel, sL, sT); fullWorkspace = Shift(fullWorkspace, sL, sT);
            if (touchModeBar.W > 0f) touchModeBar = Shift(touchModeBar, sL, sT);
        }

        const float compactFlagW = 62f, fullFlagW = 92f, compactFlagH = 44f, fullFlagH = 66f;
        const float compactCellH = 40f, fullCellH = 58f, compactRailBtn = 40f, fullRailBtn = 56f, compactMapBtn = 30f, fullMapBtn = 40f;
        var railBtnSz = touchMode ? 48f : (compact ? compactRailBtn : fullRailBtn);
        var mapBtnSz = touchMode ? 46f : (compact ? compactMapBtn : fullMapBtn);
        return new ShellLayoutProfile(
            compact, fullW, fullH, top, rail, corner, date, alert, mapBar, strip, news, leftPanel, fullWorkspace,
            FlagWellWidth: compact ? compactFlagW : fullFlagW, FlagWellHeight: compact ? compactFlagH : fullFlagH,
            StatCellWidth: dynamicCellW, StatCellHeight: compact ? compactCellH : fullCellH,
            RailButtonSize: railBtnSz, MapButtonSize: mapBtnSz, TouchMode: touchMode, TouchModeBar: touchModeBar);
    }

    private static ShellRect Shift(ShellRect r, float dx, float dy) => r with { X = r.X + dx, Y = r.Y + dy };

    private static (ShellRect Left, ShellRect Full, ShellRect News) BuildInteriorZones(
        bool compact, string prefix, float w, float h, ShellRect top, ShellRect rail, ShellRect corner,
        ShellRect date, ShellRect alert, ShellRect strip, ShellRect touchModeBar)
    {
        const float minPanelW = 260f, minFullW = 520f, minFullH = 360f, compactFullX = 58f, fullFullX = 80f;
        const float compactBottomPad = 60f, fullBottomPad = 88f, compactRightPad = 12f, fullRightPad = 16f, stripGap = 10f;
        var leftTop = Math.Max(top.Bottom + DockGap, corner.Bottom + DockGap);
        var leftBottom = Math.Min(h - LeftBottomClearance, strip.Y - WideGap);
        var leftX = rail.Right + DockGap;
        var leftRight = compact ? CompactLeftRight : FullLeftRight;
        var leftPanel = new ShellRect(leftX, leftTop, Math.Max(minPanelW, leftRight - leftX), Math.Max(MinVpH, leftBottom - leftTop));

        var fullX = Math.Max(compact ? compactFullX : fullFullX, rail.Right + DockGap);
        var fullTop = Math.Max(leftTop, date.Bottom + DockGap);
        if (touchModeBar.W > 0f && touchModeBar.H > 0f)
            fullTop = Math.Max(fullTop, touchModeBar.Bottom + DockGap);
        var fullRight = w - (compact ? compactRightPad : fullRightPad);
        var fullBottom = Math.Min(h - (compact ? compactBottomPad : fullBottomPad), strip.Y - stripGap);
        var fullWorkspace = new ShellRect(fullX, fullTop, Math.Max(minFullW, fullRight - fullX), Math.Max(minFullH, fullBottom - fullTop));

        var newsDef = compact ? new ShellRect(CompactNewsX, CompactNewsY, CompactNewsW, CompactNewsH) : new ShellRect(FullNewsX, FullNewsY, FullNewsW, FullNewsH);
        var rawNews = Resolve(prefix + "news_card", newsDef);
        var newsX = Math.Clamp(rawNews.X, leftPanel.Right + WideGap, Math.Max(leftPanel.Right + WideGap, alert.X - newsDef.W - WideGap));
        var newsY = Math.Max(top.Bottom + DockGap, rawNews.Y);
        if (touchModeBar.W > 0f && touchModeBar.H > 0f)
            newsY = Math.Max(newsY, touchModeBar.Bottom + DockGap);
        var newsW = Math.Min(newsDef.W, Math.Max(MinVpH, alert.X - newsX - WideGap));
        return (leftPanel, fullWorkspace, new ShellRect(newsX, newsY, newsW, newsDef.H));
    }

    private static ShellRect Resolve(string key, ShellRect fallback)
    {
        if (!InterfaceSkin.HasLayout(key)) return fallback;
        var r = ShellRect.FromOffsets(InterfaceSkin.Layout(key));
        return r.W > 0f && r.H > 0f ? r : fallback;
    }

    private static ShellRect AnchorRight(ShellRect r, float viewportW, float margin) =>
        r with { X = Math.Max(0f, viewportW - margin - r.W) };

    private static ShellRect AnchorBottomRight(ShellRect r, float viewportW, float viewportH, float margin) =>
        new(Math.Max(0f, viewportW - margin - r.W), Math.Max(0f, viewportH - margin - r.H), r.W, r.H);

    private static ShellRect AnchorBottomStrip(ShellRect r, float minX, float maxRight, float viewportH, float margin)
    {
        var x = Math.Max(r.X, minX);
        var w = Math.Max(FullCellX0, Math.Min(r.W, maxRight - x));
        return new(x, Math.Max(0f, viewportH - margin - r.H), w, r.H);
    }

    public static ShellRestCensus AuditRestHud(
        float width, float height, ShellCampaignState state, bool outlinerOpen, bool newsCardVisible,
        bool undoCellVisible = false, int alertRowCount = -1, int outlinerButtonCount = 0, int stripTagCount = 2)
    {
        const int maxOpenAlerts = 3, maxFoldedAlerts = 4, peaceAlerts = 2, maxStripTags = 4;
        const int baseHudButtons = 28, baseHudPanels = 4, newsCardButtons = 2;
        var profile = ForViewport(width, height, state == ShellCampaignState.War);
        var maxAlerts = outlinerOpen ? maxOpenAlerts : maxFoldedAlerts;
        var alerts = alertRowCount >= 0 ? Math.Min(alertRowCount, maxAlerts)
            : state switch { ShellCampaignState.War => Math.Min(maxFoldedAlerts, maxAlerts), ShellCampaignState.Crisis => Math.Min(maxOpenAlerts, maxAlerts), _ => Math.Min(peaceAlerts, maxAlerts) };
        var outlinerItems = outlinerOpen ? Math.Clamp(outlinerButtonCount > 0 ? outlinerButtonCount : maxOpenAlerts, 0, maxOpenAlerts) : 0;
        var stripTags = Math.Clamp(stripTagCount, 0, maxStripTags);
        var totalButtons = baseHudButtons + alerts + outlinerItems + stripTags + (newsCardVisible ? newsCardButtons : 0) + (undoCellVisible ? 1 : 0);
        var totalPanels = baseHudPanels + (stripTags > 0 ? 1 : 0) + (newsCardVisible ? 1 : 0);
        var named = new Dictionary<string, ShellRect>(StringComparer.Ordinal)
        {
            ["top_plate"] = profile.TopPlate, ["left_rail"] = profile.LeftRail,
            ["date_plate"] = profile.DatePlate, ["alert_column"] = profile.AlertColumn, ["map_bar"] = profile.MapBar
        };
        if (stripTags > 0) named["strip"] = profile.Strip;
        if (newsCardVisible) named["news_card"] = profile.NewsCard;
        var (overlaps, offscreen) = ShellDockRules.CheckRectSet(named, profile.ViewportWidth, profile.ViewportHeight);
        return new ShellRestCensus(totalButtons, totalPanels, named, overlaps, offscreen);
    }
}
