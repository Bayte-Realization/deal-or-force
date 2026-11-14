using System;
using System.Globalization;

namespace DealOrForce.Simulation;

/// <summary>A colour in the linear space the map shader mixes in. Skin tokens are sRGB hex.</summary>
public readonly record struct LinearColour(float R, float G, float B)
{
    /// <summary>
    /// A skin token's `#rrggbb` as linear light, or the fallback when the token is not a colour.
    ///
    /// The shader's constants were always linear numbers; the skin, like every other colour in
    /// the game, is written the way a paint program shows it. The transfer curve is the same one
    /// the shader's own atlas_decode_srgb applies to its textures.
    /// </summary>
    public static LinearColour FromHex(string hex, LinearColour fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var text = hex.TrimStart('#');
        if (text.Length is 3 or 4)
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2]);
        if (text.Length is not (6 or 8) ||
            !uint.TryParse(text.AsSpan(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return fallback;
        return new LinearColour(Decode((rgb >> 16) & 0xFF), Decode((rgb >> 8) & 0xFF), Decode(rgb & 0xFF));
    }

    private static float Decode(uint channel)
    {
        var value = channel / 255f;
        return value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }
}

/// <summary>One rank's line: what it is inked with, how strongly, and how wide in screen pixels.</summary>
public readonly record struct MapLineStyle(LinearColour Colour, float Opacity, float Width);

/// <summary>
/// How dark the far plate paints each rank, as a share of the country's own colour. From orbit
/// a border is not a stroke but a darker run of the same paint, and these are its three steps.
/// </summary>
public readonly record struct MapPlateShades(float Country, float Region, float Inland)
{
    public float Of(MapBorderRank rank) => rank switch
    {
        MapBorderRank.Country => Country,
        MapBorderRank.Region => Region,
        _ => Inland
    };
}

/// <summary>
/// Every line the political map draws, in one place.
///
/// The numbers used to be literals inside the shader string - a navy here, a 0.86 there, the
/// same grey spelt three ways - so nobody could say what a country border looked like without
/// reading four hundred lines of GLSL, and a mod could not change it at all. They are skin
/// tokens now (game/gui/77_map_borders.json documents each), read here once and handed to every
/// material: the full map, the packed Compatibility build of it, and the safe material.
///
/// One style per rank. There is deliberately no "new border" or "moved border" entry: a rank is
/// decided from who holds the two sides now (<see cref="MapBorders"/>), and a rank has one look.
///
///   rank       colour token               opacity metric                 width metric (screen px)
///   country    map.border_country         map.country_border_opacity     map.country_border_width
///   region     map.border_region          map.region_border_opacity      map.region_border_width
///   province   map.border_province        map.province_border_opacity    map.province_border_width
///              map.border_province_halo   map.province_border_halo
///
/// The flat map reads `map.flat_...` for the same metric first: an atlas sheet seen straight on
/// carries a finer line than a lit globe. Colours are the same on both - a border does not
/// change colour because the projection did.
///
/// Zoom: a country border is drawn at full weight from as close as the camera goes out to the
/// world view, never below `map.country_border_far_weight`; province lines fade in with
/// province_detail and are absent at country scale; the region shade follows detail_mix. Past
/// the point where a screen pixel covers more map than a stroke can carry, the far plate takes
/// over, and its shades are `map.plate_country_shade`, `map.plate_region_shade` and
/// `map.plate_inland_shade`.
///
/// Occupation is not a rank. Its pale line (map.border_occupation over map.border_occupation_key)
/// marks where held ground ends inside one owner's land; where that edge is also a country
/// border, the country border is what is drawn.
/// </summary>
public readonly record struct MapBorderStyle(
    MapLineStyle Country,
    float CountryFarWeight,
    MapLineStyle Region,
    MapLineStyle Province,
    LinearColour ProvinceHalo,
    float ProvinceHaloOpacity,
    LinearColour Occupation,
    LinearColour OccupationKey,
    MapPlateShades Plate);

public static class MapBorderStyles
{
    // The compiled fallbacks. They are the shader's own former literals, so a content pack that
    // ships no gui/77_map_borders.json draws the map it always drew. Colours fall back through
    // InterfaceSkin's compiled table, which carries the same six tokens.
    public const float CountryWidth = 1.15f;
    public const float CountryOpacity = 0.96f;
    public const float CountryFarWeightDefault = 0.62f;
    public const float RegionWidth = 3.78f;
    public const float RegionOpacity = 0.0416f;
    public const float PlateCountryShade = 0.34f;
    public const float PlateRegionShade = 0.70f;
    public const float PlateInlandShade = 0.92f;

    private static readonly LinearColour CountryInk = new(0.0116f, 0.0159f, 0.0296f);
    private static readonly LinearColour RegionInk = new(0f, 0f, 0f);
    private static readonly LinearColour ProvinceInk = new(0.055f, 0.075f, 0.09f);
    private static readonly LinearColour ProvinceHaloInk = new(0.78f, 0.80f, 0.77f);
    private static readonly LinearColour OccupationInk = new(0.92f, 0.88f, 0.71f);
    private static readonly LinearColour OccupationKeyInk = new(0.045f, 0.065f, 0.08f);

    /// <summary>The style in force, for the globe or for the flat map.</summary>
    public static MapBorderStyle For(bool flatMap) => new(
        new MapLineStyle(Ink("map.border_country", CountryInk),
            Math.Clamp(InterfaceSkin.Metric("map.country_border_opacity", CountryOpacity), 0f, 1f),
            Math.Clamp(Projected(flatMap, "country_border_width", CountryWidth, CountryWidth), 0.5f, 4f)),
        Math.Clamp(InterfaceSkin.Metric("map.country_border_far_weight", CountryFarWeightDefault), 0f, 1f),
        new MapLineStyle(Ink("map.border_region", RegionInk),
            Math.Clamp(InterfaceSkin.Metric("map.region_border_opacity", RegionOpacity), 0f, 1f),
            Math.Clamp(Projected(flatMap, "region_border_width", RegionWidth, RegionWidth), 0.5f, 12f)),
        new MapLineStyle(Ink("map.border_province", ProvinceInk),
            Math.Clamp(Projected(flatMap, "province_border_opacity", 0.36f, 0.36f), 0f, 1f),
            Math.Clamp(Projected(flatMap, "province_border_width", 1.0f, 1.0f), 0.5f, 4f)),
        Ink("map.border_province_halo", ProvinceHaloInk),
        Math.Clamp(Projected(flatMap, "province_border_halo", 0f, 0f), 0f, 0.4f),
        Ink("map.border_occupation", OccupationInk),
        Ink("map.border_occupation_key", OccupationKeyInk),
        PlateShades());

    /// <summary>
    /// The far plate's three shades. Never brighter towards a border than away from it: a mod
    /// that turns the order round would draw its frontiers as pale seams.
    /// </summary>
    public static MapPlateShades PlateShades()
    {
        var inland = Math.Clamp(InterfaceSkin.Metric("map.plate_inland_shade", PlateInlandShade), 0.05f, 1f);
        var region = Math.Clamp(InterfaceSkin.Metric("map.plate_region_shade", PlateRegionShade), 0.05f, inland);
        var country = Math.Clamp(InterfaceSkin.Metric("map.plate_country_shade", PlateCountryShade), 0.05f, region);
        return new MapPlateShades(country, region, inland);
    }

    /// <summary>The flat map's own value for a metric where the skin gives one, else the shared one.</summary>
    private static float Projected(bool flatMap, string metric, float globeFallback, float flatFallback)
    {
        var shared = InterfaceSkin.Metric("map." + metric, flatMap ? flatFallback : globeFallback);
        return flatMap ? InterfaceSkin.Metric("map.flat_" + metric, shared) : shared;
    }

    private static LinearColour Ink(string token, LinearColour fallback) =>
        InterfaceSkin.Has(token) ? LinearColour.FromHex(InterfaceSkin.Colour(token), fallback) : fallback;
}
