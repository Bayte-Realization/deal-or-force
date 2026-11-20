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
    float MapButtonSize);

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

    public static ShellLayoutProfile ForViewport(float width, float height, bool atWar = false)
    {
        const int warCells = 8, peaceCells = 7;
        const float compactPad = 10f, fullPad = 14f, compactMargin = 6f;
        var w = float.IsFinite(width) && width > MinVpW ? width : DefWidth;
        var h = float.IsFinite(height) && height > MinVpH ? height : DefHeight;
        var compact = w < CompactBreakW || h < CompactBreakH;
        var prefix = compact ? "shell.compact." : "shell.";
        var cellCount = atWar ? warCells : peaceCells;
        var cellX0 = compact ? CompactCellX0 : FullCellX0;
        var wantedTopW = cellX0 + cellCount * (compact ? CompactCellW : FullCellW) + (compact ? compactPad : fullPad);
        var rightMargin = compact ? compactMargin : DockGap;

        var top = Resolve(prefix + "top_plate", new ShellRect(0f, 0f, wantedTopW, compact ? CompactTopH : FullTopH)) with { W = wantedTopW };
        var rail = Resolve(prefix + "rail", compact ? new ShellRect(0f, CompactTopH, CompactRailW, CompactRailH) : new ShellRect(0f, FullTopH, FullRailW, FullRailH));
        var corner = compact ? new ShellRect(0f, 0f, CompactCornerW, CompactCornerH) : new ShellRect(0f, 0f, FullCornerW, FullCornerH);
        var date = AnchorRight(Resolve(prefix + "date_plate", compact ? new ShellRect(w - CompactDateW - compactMargin, compactMargin, CompactDateW, CompactDateH) : new ShellRect(w - FullDateW - DockGap, DockGap, FullDateW, FullDateH)), w, rightMargin);
        var alert = AnchorRight(Resolve(prefix + "alert_column", compact ? new ShellRect(w - CompactAlertW - compactMargin, CompactDateH + DockGap, CompactAlertW, Math.Max(MinVpH, h - MinVpH)) : new ShellRect(w - FullAlertW - DockGap, FullDateH + compactPad, FullAlertW, Math.Max(MinVpW, h - MinVpW))), w, rightMargin);
        var mapBar = AnchorBottomRight(Resolve(prefix + "map_bar", compact ? new ShellRect(w - CompactMapBarW - compactMargin, h - CompactMapBarH - compactMargin, CompactMapBarW, CompactMapBarH) : new ShellRect(w - FullMapBarW - DockGap, h - FullMapBarH - DockGap, FullMapBarW, FullMapBarH)), w, h, rightMargin);
        if (top.Right > date.X - DockGap) top = top with { W = Math.Max(MinVpW, date.X - DockGap - top.X) };

        var stripDef = compact ? new ShellRect(CompactRailW + DockGap, h - CompactStripH - compactMargin, Math.Max(MinVpH, mapBar.X - CompactCellX0), CompactStripH) : new ShellRect(FullRailW + DockGap, h - FullStripH - DockGap, Math.Max(MinVpW, mapBar.X - FullCellX0), FullStripH);
        var strip = AnchorBottomStrip(Resolve(prefix + "strip", stripDef), rail.Right + DockGap, mapBar.X - WideGap, h, rightMargin);
        var (leftPanel, fullWorkspace, news) = BuildInteriorZones(compact, prefix, w, h, top, rail, corner, alert, strip);
        var dynamicCellW = MathF.Floor(Math.Max(CompactAlertW, top.W - cellX0 - (compact ? DockGap : WideGap)) / cellCount);

        const float compactFlagW = 62f, fullFlagW = 92f, compactFlagH = 44f, fullFlagH = 66f;
        const float compactCellH = 40f, fullCellH = 58f, compactRailBtn = 40f, fullRailBtn = 56f, compactMapBtn = 30f, fullMapBtn = 40f;
        return new ShellLayoutProfile(
            compact, w, h, top, rail, corner, date, alert, mapBar, strip, news, leftPanel, fullWorkspace,
            FlagWellWidth: compact ? compactFlagW : fullFlagW, FlagWellHeight: compact ? compactFlagH : fullFlagH,
            StatCellWidth: dynamicCellW, StatCellHeight: compact ? compactCellH : fullCellH,
            RailButtonSize: compact ? compactRailBtn : fullRailBtn, MapButtonSize: compact ? compactMapBtn : fullMapBtn);
    }

    private static (ShellRect Left, ShellRect Full, ShellRect News) BuildInteriorZones(
        bool compact, string prefix, float w, float h, ShellRect top, ShellRect rail, ShellRect corner, ShellRect alert, ShellRect strip)
    {
        const float minPanelW = 260f, minFullW = 520f, minFullH = 360f, compactFullX = 58f, fullFullX = 80f;
        const float compactBottomPad = 60f, fullBottomPad = 88f, compactRightPad = 12f, fullRightPad = 16f, stripGap = 10f;
        var leftTop = Math.Max(top.Bottom + DockGap, corner.Bottom + DockGap);
        var leftBottom = Math.Min(h - LeftBottomClearance, strip.Y - WideGap);
        var leftX = rail.Right + DockGap;
        var leftRight = compact ? CompactLeftRight : FullLeftRight;
        var leftPanel = new ShellRect(leftX, leftTop, Math.Max(minPanelW, leftRight - leftX), Math.Max(MinVpH, leftBottom - leftTop));

        var fullX = compact ? compactFullX : fullFullX;
        var fullRight = w - (compact ? compactRightPad : fullRightPad);
        var fullBottom = Math.Min(h - (compact ? compactBottomPad : fullBottomPad), strip.Y - stripGap);
        var fullWorkspace = new ShellRect(fullX, leftTop, Math.Max(minFullW, fullRight - fullX), Math.Max(minFullH, fullBottom - leftTop));

        var newsDef = compact ? new ShellRect(CompactNewsX, CompactNewsY, CompactNewsW, CompactNewsH) : new ShellRect(FullNewsX, FullNewsY, FullNewsW, FullNewsH);
        var rawNews = Resolve(prefix + "news_card", newsDef);
        var newsX = Math.Clamp(rawNews.X, leftPanel.Right + WideGap, Math.Max(leftPanel.Right + WideGap, alert.X - newsDef.W - WideGap));
        var newsY = Math.Max(top.Bottom + DockGap, rawNews.Y);
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
