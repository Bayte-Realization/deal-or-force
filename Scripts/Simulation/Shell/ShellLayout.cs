using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation.Shell;

public readonly record struct ShellRect(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;

    public bool Inside(float viewportWidth, float viewportHeight) =>
        W > 0f && H > 0f && X >= -0.5f && Y >= -0.5f && Right <= viewportWidth + 0.5f && Bottom <= viewportHeight + 0.5f;

    public bool Intersects(ShellRect other) =>
        X < other.Right - 0.5f && Right > other.X + 0.5f &&
        Y < other.Bottom - 0.5f && Bottom > other.Y + 0.5f;

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
    public static int MaxRestButtons => (int)InterfaceSkin.Metric("shell.max_rest_buttons", 40f);
    public static int MaxRestPanels => (int)InterfaceSkin.Metric("shell.max_rest_panels", 6f);
    public static int MaxMapSigns => (int)InterfaceSkin.Metric("shell.max_map_signs", 20f);

    public static FlagFrame FitFlag(float texWidth, float texHeight, float maxWidth, float maxHeight, string nationTag = "")
    {
        if (maxWidth <= 0f || maxHeight <= 0f) return new(0f, 0f, 0f, 0f, false);
        var w = texWidth > 0f ? texWidth : 3f;
        var h = texHeight > 0f ? texHeight : 2f;
        var scale = Math.Min(maxWidth / w, maxHeight / h);
        var fittedW = MathF.Max(1f, MathF.Round(w * scale * 10f) / 10f);
        var fittedH = MathF.Max(1f, MathF.Round(h * scale * 10f) / 10f);
        var cutout = string.Equals(nationTag, "NPL", StringComparison.OrdinalIgnoreCase);
        return new(fittedW, fittedH, (maxWidth - fittedW) * 0.5f, (maxHeight - fittedH) * 0.5f, cutout);
    }

    public static ShellLayoutProfile ForViewport(float width, float height, bool atWar = false)
    {
        var w = float.IsFinite(width) && width > 320f ? width : 1920f;
        var h = float.IsFinite(height) && height > 240f ? height : 1080f;
        var compact = w < 1600f || h < 900f;
        var prefix = compact ? "shell.compact." : "shell.";
        var cellCount = atWar ? 8 : 7;
        var cellX0 = compact ? 82f : 120f;
        var wantedTopW = cellX0 + cellCount * (compact ? 102f : 144f) + (compact ? 10f : 14f);
        var rightMargin = compact ? 6f : 8f;

        var top = Resolve(prefix + "top_plate", new ShellRect(0f, 0f, wantedTopW, compact ? 48f : 70f)) with { W = wantedTopW };
        var rail = Resolve(prefix + "rail", compact ? new ShellRect(0f, 48f, 50f, 504f) : new ShellRect(0f, 70f, 64f, 722f));
        var corner = compact ? new ShellRect(0f, 0f, 76f, 86f) : new ShellRect(0f, 0f, 112f, 124f);
        var date = AnchorRight(Resolve(prefix + "date_plate", compact ? new ShellRect(w - 328f, 6f, 322f, 58f) : new ShellRect(w - 448f, 8f, 440f, 84f)), w, rightMargin);
        var alert = AnchorRight(Resolve(prefix + "alert_column", compact ? new ShellRect(w - 290f, 66f, 284f, Math.Max(220f, h - 240f)) : new ShellRect(w - 384f, 94f, 376f, Math.Max(300f, h - 320f))), w, rightMargin);
        var mapBar = AnchorBottomRight(Resolve(prefix + "map_bar", compact ? new ShellRect(w - 222f, h - 46f, 216f, 40f) : new ShellRect(w - 320f, h - 64f, 312f, 56f)), w, h, rightMargin);
        if (top.Right > date.X - 8f) top = top with { W = Math.Max(320f, date.X - 8f - top.X) };

        var stripDef = compact ? new ShellRect(58f, h - 52f, Math.Max(220f, mapBar.X - 72f), 46f) : new ShellRect(72f, h - 76f, Math.Max(320f, mapBar.X - 88f), 68f);
        var strip = AnchorBottomStrip(Resolve(prefix + "strip", stripDef), rail.Right + 8f, mapBar.X - 12f, h, rightMargin);
        var (leftPanel, fullWorkspace, news) = BuildInteriorZones(compact, prefix, w, h, top, rail, corner, alert, strip);
        var dynamicCellW = MathF.Floor(Math.Max(280f, top.W - cellX0 - (compact ? 8f : 12f)) / cellCount);

        return new ShellLayoutProfile(
            compact, w, h, top, rail, corner, date, alert, mapBar, strip, news, leftPanel, fullWorkspace,
            FlagWellWidth: compact ? 62f : 92f, FlagWellHeight: compact ? 44f : 66f,
            StatCellWidth: dynamicCellW, StatCellHeight: compact ? 40f : 58f,
            RailButtonSize: compact ? 40f : 56f, MapButtonSize: compact ? 30f : 40f);
    }

    private static (ShellRect Left, ShellRect Full, ShellRect News) BuildInteriorZones(
        bool compact, string prefix, float w, float h, ShellRect top, ShellRect rail, ShellRect corner, ShellRect alert, ShellRect strip)
    {
        var leftTop = Math.Max(top.Bottom + 8f, corner.Bottom + 8f);
        var leftBottom = Math.Min(h - 92f, strip.Y - 12f);
        var leftX = rail.Right + 8f;
        var leftRight = compact ? 390f : 544f;
        var leftPanel = new ShellRect(leftX, leftTop, Math.Max(260f, leftRight - leftX), Math.Max(240f, leftBottom - leftTop));

        var fullX = compact ? 58f : 80f;
        var fullRight = w - (compact ? 12f : 16f);
        var fullBottom = Math.Min(h - (compact ? 60f : 88f), strip.Y - 10f);
        var fullWorkspace = new ShellRect(fullX, leftTop, Math.Max(520f, fullRight - fullX), Math.Max(360f, fullBottom - leftTop));

        var newsDef = compact ? new ShellRect(460f, 56f, 370f, 76f) : new ShellRect(700f, 82f, 540f, 114f);
        var rawNews = Resolve(prefix + "news_card", newsDef);
        var newsX = Math.Clamp(rawNews.X, leftPanel.Right + 12f, Math.Max(leftPanel.Right + 12f, alert.X - newsDef.W - 12f));
        var newsY = Math.Max(top.Bottom + 8f, rawNews.Y);
        var newsW = Math.Min(newsDef.W, Math.Max(220f, alert.X - newsX - 12f));
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
        var w = Math.Max(120f, Math.Min(r.W, maxRight - x));
        return new(x, Math.Max(0f, viewportH - margin - r.H), w, r.H);
    }

    public static ShellRestCensus AuditRestHud(
        float width, float height, ShellCampaignState state, bool outlinerOpen, bool newsCardVisible,
        bool undoCellVisible = false, int alertRowCount = -1, int outlinerButtonCount = 0, int stripTagCount = 2)
    {
        var profile = ForViewport(width, height, state == ShellCampaignState.War);
        var maxAlerts = outlinerOpen ? 3 : 4;
        var alerts = alertRowCount >= 0 ? Math.Min(alertRowCount, maxAlerts)
            : state switch { ShellCampaignState.War => Math.Min(4, maxAlerts), ShellCampaignState.Crisis => Math.Min(3, maxAlerts), _ => Math.Min(2, maxAlerts) };
        var outlinerItems = outlinerOpen ? Math.Clamp(outlinerButtonCount > 0 ? outlinerButtonCount : 3, 0, 3) : 0;
        var stripTags = Math.Clamp(stripTagCount, 0, 4);
        var totalButtons = 1 + 10 + 8 + 1 + alerts + outlinerItems + 6 + stripTags + (newsCardVisible ? 2 : 0) + (undoCellVisible ? 1 : 0);
        var totalPanels = 4 + (stripTags > 0 ? 1 : 0) + (newsCardVisible ? 1 : 0);
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
