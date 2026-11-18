using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation.Shell;

public readonly record struct ShellRect(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;

    public bool Inside(float viewportWidth, float viewportHeight) =>
        W > 0f && H > 0f && X >= 0f && Y >= 0f && Right <= viewportWidth + 0.5f && Bottom <= viewportHeight + 0.5f;

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
    ShellRect DatePlate,
    ShellRect AlertColumn,
    ShellRect MapBar,
    ShellRect Strip,
    ShellRect NewsCard,
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

/// <summary>
/// Pure viewport-adaptive layout geometry, natural-aspect flag fitting, and rest-HUD
/// density census for the new campaign shell at both 1920x1080 and 1280x720.
/// </summary>
public static class ShellLayout
{
    public static int MaxRestButtons => (int)InterfaceSkin.Metric("shell.max_rest_buttons", 40f);
    public static int MaxRestPanels => (int)InterfaceSkin.Metric("shell.max_rest_panels", 6f);
    public static int MaxMapSigns => (int)InterfaceSkin.Metric("shell.max_map_signs", 20f);

    /// <summary>
    /// Preserves the flag's natural proportions (2:3, 1:1 square, 28:11 Qatar, or cutout Nepal)
    /// inside a recessed flag frame without stretching or applying a heraldic shield crop.
    /// </summary>
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

        ShellRect Resolve(string suffix, ShellRect fallback)
        {
            var key = prefix + suffix;
            if (InterfaceSkin.HasLayout(key))
            {
                var r = ShellRect.FromOffsets(InterfaceSkin.Layout(key));
                if (r.W > 0f && r.H > 0f) return r;
            }
            return fallback;
        }

        var topDefault = compact
            ? new ShellRect(12f, 12f, atWar ? 708f : 632f, 56f)
            : new ShellRect(16f, 16f, atWar ? 908f : 800f, 64f);
        var railDefault = compact
            ? new ShellRect(12f, 68f, 52f, 514f)
            : new ShellRect(16f, 80f, 64f, 570f);
        var dateDefault = compact
            ? new ShellRect(Math.Max(760f, w - 376f), 12f, 364f, 90f)
            : new ShellRect(Math.Max(980f, w - 432f), 16f, 416f, 102f);
        var alertDefault = compact
            ? new ShellRect(Math.Max(820f, w - 312f), 110f, 300f, Math.Max(220f, h - 232f))
            : new ShellRect(Math.Max(1100f, w - 356f), 126f, 340f, Math.Max(300f, h - 300f));
        var mapBarDefault = compact
            ? new ShellRect(Math.Max(760f, w - 324f), h - 68f, 312f, 56f)
            : new ShellRect(Math.Max(1100f, w - 376f), h - 76f, 360f, 60f);
        var stripDefault = compact
            ? new ShellRect(76f, h - 68f, Math.Max(240f, mapBarDefault.X - 92f), 56f)
            : new ShellRect(92f, h - 84f, Math.Max(320f, mapBarDefault.X - 116f), 68f);
        var newsDefault = compact
            ? new ShellRect(76f, h - 180f, 360f, 102f)
            : new ShellRect(92f, h - 224f, 420f, 128f);

        var top = Resolve("top_plate", topDefault);
        if (!atWar)
        {
            var trimmed = compact ? 632f : 800f;
            if (top.W > trimmed) top = top with { W = trimmed };
        }
        var rail = Resolve("rail", railDefault);
        var date = AnchorRight(Resolve("date_plate", dateDefault), w, compact ? 12f : 16f);
        var alert = AnchorRight(Resolve("alert_column", alertDefault), w, compact ? 12f : 16f);
        var mapBar = AnchorBottomRight(Resolve("map_bar", mapBarDefault), w, h, compact ? 12f : 16f);
        var strip = AnchorBottomStrip(Resolve("strip", stripDefault), rail.Right + (compact ? 12f : 12f), mapBar.X - (compact ? 16f : 24f), h, compact ? 12f : 16f);
        var news = AnchorBottomNews(Resolve("news_card", newsDefault), rail.Right + (compact ? 12f : 12f), strip.Y - (compact ? 10f : 12f));

        return new ShellLayoutProfile(
            compact, w, h, top, rail, date, alert, mapBar, strip, news,
            FlagWellWidth: compact ? 74f : 96f,
            FlagWellHeight: compact ? 42f : 48f,
            StatCellWidth: compact ? 76f : 98f,
            StatCellHeight: compact ? 44f : 50f,
            RailButtonSize: compact ? 44f : 50f,
            MapButtonSize: compact ? 44f : 50f);
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

    private static ShellRect AnchorBottomNews(ShellRect r, float minX, float maxBottom) =>
        new(Math.Max(r.X, minX), Math.Max(0f, maxBottom - r.H), r.W, r.H);

    public static ShellRestCensus AuditRestHud(
        float width,
        float height,
        ShellCampaignState state,
        bool outlinerOpen,
        bool newsCardVisible,
        bool undoCellVisible = false,
        int alertRowCount = -1,
        int outlinerButtonCount = 0,
        int stripTagCount = 2)
    {
        var atWar = state == ShellCampaignState.War;
        var profile = ForViewport(width, height, atWar);

        var maxAlerts = outlinerOpen ? 3 : 4;
        var alerts = alertRowCount >= 0 ? Math.Min(alertRowCount, maxAlerts)
            : state switch
            {
                ShellCampaignState.War => Math.Min(4, maxAlerts),
                ShellCampaignState.Crisis => Math.Min(3, maxAlerts),
                _ => Math.Min(2, maxAlerts)
            };
        var outlinerItems = outlinerOpen ? Math.Clamp(outlinerButtonCount > 0 ? outlinerButtonCount : 3, 0, 3) : 0;

        // Interactive rest buttons matching live Godot shell node tree:
        // Flag(1) + Rail(10) + DatePlate(Pause 1 + Speed 5 + World/DEFCON 1 + NewsLog 1 + Menu 1 = 9)
        // + Alerts(alerts) + OutlinerEyeToggle(1) + OutlinerItems(outlinerItems)
        // + MapBar(6) + StripTags(<=2) + NewsCard(Pin 1 + Close 1) + UndoCell(0 or 1).
        // Top resource wells are hover/click PanelContainers rather than Button nodes.
        var stripTags = Math.Clamp(stripTagCount, 0, 2);
        var totalButtons = 1 + 10 + 9 + 1 + alerts + outlinerItems + 6 + stripTags
            + (newsCardVisible ? 2 : 0) + (undoCellVisible ? 1 : 0);

        // Visual plates: LCorner(1) + DatePlate(1) + AlertOutlinerColumn(1) + MapBar(1) + Strip(0..1) + NewsCard(0..1)
        var totalPanels = 4 + (stripTags > 0 ? 1 : 0) + (newsCardVisible ? 1 : 0);

        var named = new Dictionary<string, ShellRect>(StringComparer.Ordinal)
        {
            ["top_plate"] = profile.TopPlate,
            ["left_rail"] = profile.LeftRail,
            ["date_plate"] = profile.DatePlate,
            ["alert_column"] = profile.AlertColumn,
            ["map_bar"] = profile.MapBar
        };
        if (stripTags > 0) named["strip"] = profile.Strip;
        if (newsCardVisible) named["news_card"] = profile.NewsCard;

        var overlaps = new List<string>();
        var offscreen = new List<string>();
        var keys = new List<string>(named.Keys);
        for (var i = 0; i < keys.Count; i++)
        {
            var aKey = keys[i];
            var aRect = named[aKey];
            if (!aRect.Inside(profile.ViewportWidth, profile.ViewportHeight))
                offscreen.Add(aKey);
            for (var j = i + 1; j < keys.Count; j++)
            {
                var bKey = keys[j];
                if (aRect.Intersects(named[bKey]))
                    overlaps.Add($"{aKey} vs {bKey}");
            }
        }

        return new ShellRestCensus(totalButtons, totalPanels, named, overlaps, offscreen);
    }
}
