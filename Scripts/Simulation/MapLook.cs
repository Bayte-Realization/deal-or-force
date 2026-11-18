using System;

namespace DealOrForce.Simulation;

/// <summary>
/// The atlas look of the map: every colour role and every number the map shader needs to draw
/// warm paper and ink, and the zoom at which the map becomes a sheet of paper.
///
/// Colours are sRGB hex tokens in game/gui/78_map_look.json (each one documented there), read
/// through the skin and handed to the shader in linear light, the same way MapBorderStyles hands
/// over the lines. The compiled values below are the design's starting table
/// (docs/design/graphics-2026-11-18/map-look.md, "Цветовая иерархия"), so a content pack without
/// the file still draws the intended look rather than a magenta one.
/// </summary>
public sealed record MapLookStyle(
    LinearColour Paper,
    LinearColour Shade,
    LinearColour Lamp,
    float ReliefTint,
    LinearColour SeaDeep,
    LinearColour SeaMid,
    LinearColour SeaShelf,
    LinearColour SeaCoast,
    LinearColour SeaChart,
    LinearColour Foam,
    LinearColour CoastInk,
    LinearColour Unowned,
    float GroundShowNear,
    float CoverFar,
    float SaturationFar,
    float ReliefFar,
    float PaperFromDistance,
    float PaperFullDistance,
    float FlatPaperFromZoom,
    float FlatPaperFullZoom,
    float FarBand,
    float FarBandPixels,
    float FoamStrength,
    float Waterline,
    float SeaSheen,
    float SelectionWidth);

public static class MapLook
{
    public const string Setting = "map_look";
    public const string AtlasValue = "atlas";
    public const string ClassicValue = "classic";

    // The relief multiplier the shader has always used runs from 0.52 in the deepest shade to
    // 1.42 on a slope facing the lamp. The atlas keeps exactly that brightness and only leans the
    // hue (ReliefShade).
    public const float HillshadeMin = 0.52f;
    public const float HillshadeMax = 1.42f;

    // The three steps of the chart sea on the paper map, as shares of map.look_sea_chart: abyss,
    // continental slope, shelf. About two and a half L* apart at this darkness.
    public const float ChartStepAbyss = 0.72f;
    public const float ChartStepSlope = 0.86f;
    public const float ChartStepShelf = 1.0f;

    public const float ReliefTintDefault = 0.60f;
    public const float GroundShowNearDefault = 0.30f;
    public const float CoverFarDefault = 0.95f;
    public const float SaturationFarDefault = 0.80f;
    public const float ReliefFarDefault = 0.45f;
    public const float PaperFromDistanceDefault = 17f;
    public const float PaperFullDistanceDefault = 22f;
    public const float FlatPaperFromZoomDefault = 9.0f;
    public const float FlatPaperFullZoomDefault = 11.5f;
    public const float FarBandDefault = 0.36f;
    public const float FarBandPixelsDefault = 4.5f;
    public const float FoamDefault = 0.55f;
    public const float WaterlineDefault = 0f;
    public const float SeaSheenDefault = 0.30f;
    public const float SelectionWidthDefault = 1.3f;

    /// <summary>The design's palette, as sRGB hex - what InterfaceSkin also ships compiled.</summary>
    public static readonly (string Token, string Hex)[] DefaultColours =
    {
        ("map.look_paper", "#e6d9bb"),
        ("map.look_shade", "#4a4f5e"),
        ("map.look_lamp", "#fff1d2"),
        ("map.look_sea_deep", "#18323f"),
        ("map.look_sea_mid", "#234b58"),
        ("map.look_sea_shelf", "#3d6b70"),
        ("map.look_sea_coast", "#6a9690"),
        ("map.look_sea_chart", "#2a4450"),
        ("map.look_foam", "#ece5d2"),
        ("map.look_coast_ink", "#2e4048"),
        ("map.look_unowned", "#d8ccb0"),
        ("map.look_label_fill", "#efe4c8"),
        ("map.look_label_outline", "#11161c"),
        ("map.look_capital_fill", "#f7eed6"),
        ("map.look_region_fill", "#e6d9bb"),
        ("map.look_river_fill", "#a8cedd"),
        ("map.look_river_outline", "#0f1e29"),
    };

    /// <summary>Returns a skin colour hex token with compiled fallback (without mutating classic globals).</summary>
    public static string ColourHex(string token)
    {
        if (InterfaceSkin.Has(token)) return InterfaceSkin.Colour(token);
        foreach (var (name, hex) in DefaultColours)
            if (name == token) return hex;
        return "#efe4c8";
    }

    /// <summary>
    /// Whether the atlas look is on. Anything but an explicit "classic" is the atlas: a player who
    /// never opened the settings, or a content pack that does not declare the option, gets the
    /// new map, and the old one is one value away.
    /// </summary>
    public static bool Atlas() => SettingsCatalog.Chosen(Setting) != ClassicValue;

    /// <summary>The style in force, read from the skin with the design's values as fallbacks.</summary>
    public static MapLookStyle Current()
    {
        var paperFrom = Math.Clamp(InterfaceSkin.Metric("map.look_paper_from_distance", PaperFromDistanceDefault), 5f, 40f);
        var paperFull = Math.Clamp(InterfaceSkin.Metric("map.look_paper_full_distance", PaperFullDistanceDefault), paperFrom, 60f);
        var flatFrom = Math.Clamp(InterfaceSkin.Metric("map.look_flat_paper_from_zoom", FlatPaperFromZoomDefault), 0.1f, 30f);
        var flatFull = Math.Clamp(InterfaceSkin.Metric("map.look_flat_paper_full_zoom", FlatPaperFullZoomDefault), flatFrom, 40f);
        return new MapLookStyle(
            Ink("map.look_paper"),
            Ink("map.look_shade"),
            Ink("map.look_lamp"),
            Math.Clamp(InterfaceSkin.Metric("map.look_relief_tint", ReliefTintDefault), 0f, 1f),
            Ink("map.look_sea_deep"),
            Ink("map.look_sea_mid"),
            Ink("map.look_sea_shelf"),
            Ink("map.look_sea_coast"),
            Ink("map.look_sea_chart"),
            Ink("map.look_foam"),
            Ink("map.look_coast_ink"),
            Ink("map.look_unowned"),
            Math.Clamp(InterfaceSkin.Metric("map.look_ground_show_near", GroundShowNearDefault), 0f, 0.6f),
            Math.Clamp(InterfaceSkin.Metric("map.look_cover_far", CoverFarDefault), 0f, 1f),
            Math.Clamp(InterfaceSkin.Metric("map.look_saturation_far", SaturationFarDefault), 0f, 1.5f),
            Math.Clamp(InterfaceSkin.Metric("map.look_relief_far", ReliefFarDefault), 0f, 1.5f),
            paperFrom, paperFull, flatFrom, flatFull,
            Math.Clamp(InterfaceSkin.Metric("map.look_far_band", FarBandDefault), 0f, 1f),
            Math.Clamp(InterfaceSkin.Metric("map.look_far_band_pixels", FarBandPixelsDefault), 0f, 16f),
            Math.Clamp(InterfaceSkin.Metric("map.look_foam_strength", FoamDefault), 0f, 1f),
            Math.Clamp(InterfaceSkin.Metric("map.look_waterline", WaterlineDefault), 0f, 1f),
            Math.Clamp(InterfaceSkin.Metric("map.look_sea_sheen", SeaSheenDefault), 0f, 1f),
            Math.Clamp(InterfaceSkin.Metric("map.look_selection_width", SelectionWidthDefault), 1f, 2.5f));
    }

    /// <summary>
    /// How far the map has turned into paper: 0 up to the point where it starts, 1 from the point
    /// where it is complete, and a smoothstep between. On the globe the measure is the camera's
    /// distance, on the flat map its zoom; both grow as the player pulls back.
    /// </summary>
    public static float PaperMix(bool flat, float distanceOrZoom, MapLookStyle style)
    {
        var from = flat ? style.FlatPaperFromZoom : style.PaperFromDistance;
        var full = flat ? style.FlatPaperFullZoom : style.PaperFullDistance;
        if (!float.IsFinite(distanceOrZoom)) return 0f;
        if (full <= from) return distanceOrZoom >= from ? 1f : 0f;
        var t = Math.Clamp((distanceOrZoom - from) / (full - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// The atlas relief, the same sum the map shader does: the surface times the grey hillshade -
    /// so its brightness is exactly the classic one - plus a lean of hue towards the shade colour
    /// (hillshade under one) or the lamp (over one). The lean is the tint over its own luminance
    /// less one, a colour with no luminance, scaled by the shaded surface's luminance, how far the
    /// slope is from flat, and <paramref name="tint"/>. Any surface keeps its luminance, however
    /// saturated; only negative channels are clipped.
    /// </summary>
    public static LinearColour ReliefShade(LinearColour surface, float hillshade, LinearColour shade, LinearColour lamp,
        float tint)
    {
        var lean = hillshade < 1f ? shade : lamp;
        var leanLuminance = MathF.Max(Luminance(lean), 0.0001f);
        var shaded = new LinearColour(surface.R * hillshade, surface.G * hillshade, surface.B * hillshade);
        var k = Luminance(shaded) * MathF.Abs(hillshade - 1f) * tint;
        return new LinearColour(
            MathF.Max(shaded.R + (lean.R / leanLuminance - 1f) * k, 0f),
            MathF.Max(shaded.G + (lean.G / leanLuminance - 1f) * k, 0f),
            MathF.Max(shaded.B + (lean.B / leanLuminance - 1f) * k, 0f));
    }

    public static float Luminance(LinearColour colour) =>
        0.2126f * colour.R + 0.7152f * colour.G + 0.0722f * colour.B;

    /// <summary>The compiled value of a colour token, in linear light.</summary>
    public static LinearColour Fallback(string token)
    {
        foreach (var (name, hex) in DefaultColours)
            if (name == token) return LinearColour.FromHex(hex, default);
        return default;
    }

    private static LinearColour Ink(string token)
    {
        var fallback = Fallback(token);
        return InterfaceSkin.Has(token) ? LinearColour.FromHex(InterfaceSkin.Colour(token), fallback) : fallback;
    }
}
