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
    public const string RealisticValue = "realistic";
    public const string PaperValue = "paper";
    public const string AtlasValue = "atlas";
    public const string ClassicValue = "classic";
    public const string MigrationKeyV3 = "map_look_default_v3";

    public const string PoliticalFillSetting = "map_political_fill";
    public const string PoliticalFillCustomKey = "map_political_fill_custom";
    public const string FillClassicOpaque = "classic_opaque";
    public const string FillBalanced = "balanced";
    public const string FillTranslucent = "translucent";

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

    public const float ReliefTintDefault = 0.28f;
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

    /// <summary>The design's de-yellowed neutral D65 / archival cotton palette, as sRGB hex.</summary>
    public static readonly (string Token, string Hex)[] DefaultColours =
    {
        ("map.look_paper", "#eceae4"),
        ("map.look_shade", "#4d5463"),
        ("map.look_lamp", "#faf9f6"),
        ("map.look_sea_deep", "#18323f"),
        ("map.look_sea_mid", "#234b58"),
        ("map.look_sea_shelf", "#3d6b70"),
        ("map.look_sea_coast", "#6a9690"),
        ("map.look_sea_chart", "#2a4450"),
        ("map.look_foam", "#f2f1ee"),
        ("map.look_coast_ink", "#2e4048"),
        ("map.look_unowned", "#dfddd6"),
        ("map.look_label_fill", "#f5f6f8"),
        ("map.look_label_outline", "#111318"),
        ("map.look_capital_fill", "#f8f9fb"),
        ("map.look_region_fill", "#eceef2"),
        ("map.look_river_fill", "#b4d6e4"),
        ("map.look_river_outline", "#0f1c26"),
    };

    /// <summary>Returns a skin colour hex token with compiled fallback (without mutating classic globals).</summary>
    public static string ColourHex(string token)
    {
        if (InterfaceSkin.Has(token)) return InterfaceSkin.Colour(token);
        foreach (var (name, hex) in DefaultColours)
            if (name == token) return hex;
        return "#f5f6f8";
    }

    /// <summary>
    /// Active 4-way mood ("realistic", "paper", "atlas", "classic"). Unknown or missing values
    /// resolve to "realistic", the shipped AAA physical look.
    /// </summary>
    public static string Mode()
    {
        var chosen = SettingsCatalog.Chosen(Setting);
        return chosen switch
        {
            RealisticValue or PaperValue or AtlasValue or ClassicValue => chosen,
            _ => RealisticValue
        };
    }

    /// <summary>
    /// Whether the overhauled cartographic pipeline is on. Anything but an explicit "classic"
    /// uses linear-light skin inks, clean MSDF typography and distance-field borders; "classic"
    /// reverts the entire map in one click.
    /// </summary>
    public static bool Atlas() => Mode() != ClassicValue;

    public static bool IsRealistic() => Mode() == RealisticValue;

    public static bool IsPaper() => Mode() == PaperValue;

    /// <summary>
    /// Effective country fill style ("classic_opaque", "balanced", "translucent"). Defaults to
    /// "translucent" in realistic mode and "classic_opaque" in paper/atlas/classic unless the
    /// player has explicitly stored a preference, which then works across every map mood.
    /// </summary>
    public static string EffectivePoliticalFill(string mode = null)
    {
        var activeMode = mode ?? Mode();
        if (PlayerSettings.Has(PoliticalFillSetting))
        {
            var stored = SettingsCatalog.Chosen(PoliticalFillSetting);
            if (stored is FillClassicOpaque or FillBalanced or FillTranslucent)
                return stored;
        }
        return activeMode == RealisticValue ? FillTranslucent : FillClassicOpaque;
    }

    /// <summary>Shader blend weight [0..1] for Imperator-style interior political translucency.</summary>
    public static float PoliticalFillTranslucency(string fill) => fill switch
    {
        FillTranslucent => 1.0f,
        FillBalanced => 0.5f,
        _ => 0.0f
    };

    /// <summary>Keeps the UI fill choice aligned with mood changes until the player picks a custom fill.</summary>
    public static void SyncMoodDefaultFill(string mode)
    {
        if (PlayerSettings.Bool(PoliticalFillCustomKey, false)) return;
        SettingsCatalog.Store(PoliticalFillSetting, mode == RealisticValue ? FillTranslucent : FillClassicOpaque);
    }

    /// <summary>
    /// One-time boot upgrade from Wave 1/2 default ("atlas") to Wave 3 default ("realistic").
    /// Preserves an explicit "classic" choice and never runs twice once stamped.
    /// </summary>
    public static void MigrateDefaultsIfNeeded()
    {
        if (!PlayerSettings.Located || PlayerSettings.Bool(MigrationKeyV3, false)) return;
        if (PlayerSettings.Has(Setting))
        {
            var current = PlayerSettings.Text(Setting, "");
            if (string.Equals(current, AtlasValue, StringComparison.Ordinal))
                PlayerSettings.Set(Setting, RealisticValue);
        }
        if (PlayerSettings.Has(PoliticalFillSetting))
        {
            var fill = PlayerSettings.Text(PoliticalFillSetting, "");
            if (fill is not (FillClassicOpaque or FillBalanced or FillTranslucent))
                PlayerSettings.Clear(PoliticalFillSetting);
        }
        PlayerSettings.Set(MigrationKeyV3, true);
    }

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
    /// Mode-aware paper share: "paper" stays 1.0 at every zoom; "classic" stays 0.0; "atlas"
    /// uses the shipped middle-to-far transition; "realistic" keeps physical ground and ocean up
    /// to orbital distance (d &gt; 18.5 on the globe) and fades smoothly into the engraved chart.
    /// </summary>
    public static float PaperMixForMode(string mode, bool flat, float distanceOrZoom, MapLookStyle style)
    {
        if (mode == ClassicValue || style is null) return 0f;
        if (mode == PaperValue) return 1f;
        if (mode == RealisticValue)
        {
            var orbital = style with
            {
                PaperFromDistance = Math.Max(style.PaperFromDistance + 1.5f, 18.5f),
                PaperFullDistance = Math.Max(style.PaperFullDistance + 2.5f, 24.5f),
                FlatPaperFromZoom = Math.Max(style.FlatPaperFromZoom + 1.2f, 10.2f),
                FlatPaperFullZoom = Math.Max(style.FlatPaperFullZoom + 1.5f, 13.0f)
            };
            return PaperMix(flat, distanceOrZoom, orbital);
        }
        return PaperMix(flat, distanceOrZoom, style);
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
