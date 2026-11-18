using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DealOrForce;
using DealOrForce.Simulation;

/// <summary>
/// The atlas look (Simulation.MapLook): the tokens the skin ships, the compiled fallbacks, the
/// zoom at which the map becomes paper, the switch, and the shader's half of the contract - every
/// uniform the game sets is declared, and its default is the same colour the skin ships, so a
/// material nobody configured and a configured one agree.
/// </summary>
static class MapLookTests
{
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS map look: " + label); }

    private static bool Near(float a, float b, float tolerance = 0.0005f) => MathF.Abs(a - b) <= tolerance;

    private static bool Near(LinearColour a, LinearColour b, float tolerance = 0.0005f) =>
        Near(a.R, b.R, tolerance) && Near(a.G, b.G, tolerance) && Near(a.B, b.B, tolerance);

    public static void Run()
    {
        Tokens();
        Paper();
        Switch();
        ShaderContract();
    }

    private static void Tokens()
    {
        InterfaceSkin.ResetCatalog();
        // The design's table, as revised after the first review (docs/MAP_LOOK_WAVE_1_2026_11_18_RU.md).
        var table = new Dictionary<string, string>
        {
            ["map.look_paper"] = "#e6d9bb", ["map.look_shade"] = "#4a4f5e", ["map.look_lamp"] = "#fff1d2",
            ["map.look_sea_deep"] = "#18323f", ["map.look_sea_mid"] = "#234b58", ["map.look_sea_shelf"] = "#3d6b70",
            ["map.look_sea_coast"] = "#6a9690", ["map.look_sea_chart"] = "#2a4450", ["map.look_foam"] = "#ece5d2",
            ["map.look_coast_ink"] = "#2e4048", ["map.look_unowned"] = "#d8ccb0",
            ["map.look_label_fill"] = "#efe4c8", ["map.look_label_outline"] = "#11161c",
            ["map.look_capital_fill"] = "#f7eed6", ["map.look_region_fill"] = "#e6d9bb",
            ["map.look_river_fill"] = "#a8cedd", ["map.look_river_outline"] = "#0f1e29",
        };
        Check(MapLook.DefaultColours.Length == table.Count &&
              MapLook.DefaultColours.All(entry => table.TryGetValue(entry.Token, out var hex) &&
                  string.Equals(hex, entry.Hex, StringComparison.OrdinalIgnoreCase)),
            "the compiled colour fallbacks are the design's table");
        foreach (var (token, hex) in table)
            Check(InterfaceSkin.Has(token) && string.Equals(InterfaceSkin.Colour(token), hex, StringComparison.OrdinalIgnoreCase) &&
                  !InterfaceSkin.Overridden.Contains(token),
                "game/gui/78_map_look.json and the compiled skin agree on " + token);

        foreach (var (token, compiled) in new[] {
                     ("map.look_ground_show_near", MapLook.GroundShowNearDefault),
                     ("map.look_cover_far", MapLook.CoverFarDefault), ("map.look_saturation_far", MapLook.SaturationFarDefault),
                     ("map.look_relief_tint", MapLook.ReliefTintDefault),
                     ("map.look_relief_far", MapLook.ReliefFarDefault), ("map.look_paper_from_distance", MapLook.PaperFromDistanceDefault),
                     ("map.look_paper_full_distance", MapLook.PaperFullDistanceDefault),
                     ("map.look_flat_paper_from_zoom", MapLook.FlatPaperFromZoomDefault),
                     ("map.look_flat_paper_full_zoom", MapLook.FlatPaperFullZoomDefault),
                     ("map.look_far_band", MapLook.FarBandDefault), ("map.look_far_band_pixels", MapLook.FarBandPixelsDefault),
                     ("map.look_foam_strength", MapLook.FoamDefault), ("map.look_waterline", MapLook.WaterlineDefault),
                     ("map.look_sea_sheen", MapLook.SeaSheenDefault), ("map.look_selection_width", MapLook.SelectionWidthDefault) })
            Check(Near(InterfaceSkin.Metric(token, float.NaN), compiled, 0.00001f),
                "game/gui/78_map_look.json ships " + token + " at the compiled value");

        Check(MapLook.GroundShowNearDefault == 0.30f && MapLook.CoverFarDefault == 0.95f &&
              MapLook.SaturationFarDefault == 0.80f && MapLook.ReliefFarDefault == 0.45f && MapLook.ReliefTintDefault == 0.60f &&
              MapLook.PaperFromDistanceDefault == 17f && MapLook.PaperFullDistanceDefault == 22f &&
              MapLook.FlatPaperFromZoomDefault == 9.0f && MapLook.FlatPaperFullZoomDefault == 11.5f &&
              MapLook.FarBandDefault == 0.36f && MapLook.FarBandPixelsDefault == 4.5f && MapLook.FoamDefault == 0.55f &&
              MapLook.WaterlineDefault == 0f && MapLook.SeaSheenDefault == 0.30f && MapLook.SelectionWidthDefault == 1.3f,
            "the compiled metric fallbacks are the brief's numbers, as revised");
        Check(InterfaceSkin.Metric("map.look_waterline", 1f) == 0f,
            "engraved waterlines are an era's mark: off in the base look");
        foreach (var gone in new[] { "map.look_paper_grain", "map.look_border_wobble", "map.look_selection_halo",
                     "map.look_cover_near", "map.look_flat_cover_near" })
            Check(float.IsNaN(InterfaceSkin.Metric(gone, float.NaN)), "the retired token " + gone + " is no longer shipped");

        var style = MapLook.Current();
        Check(Near(style.Paper, LinearColour.FromHex("#e6d9bb", default)) && Near(style.CoastInk, LinearColour.FromHex("#2e4048", default)) &&
              Near(style.SeaDeep, new LinearColour(0.0091f, 0.0319f, 0.0497f), 0.0002f),
            "colours reach the shader as linear light");
        // The brightness order of the design: land paper over coastal water over open sea; inks darkest.
        float L(LinearColour c) => MapLook.Luminance(c);
        Check(L(style.Paper) > L(style.SeaCoast) && L(style.SeaCoast) > L(style.SeaShelf) && L(style.SeaShelf) > L(style.SeaMid) &&
              L(style.SeaMid) > L(style.SeaDeep) && L(style.CoastInk) < L(style.SeaShelf) && L(style.Foam) > L(style.SeaCoast),
            "one brightness order: paper, coastal water, shelf, open sea; the coast ink below the shelf; foam above the sea");
        // Unclaimed land is paper, and far from the sea in value: the classic slate grey sat
        // within a few per cent of the chart sea, and Antarctica melted into the water.
        Check(L(style.Unowned) < L(style.Paper) && L(style.Unowned) > 0.85f * L(style.Paper) &&
              L(style.Unowned) > 4f * L(style.SeaChart) && style.Unowned.R > style.Unowned.B,
            "unowned land is the paper a shade darker, warm, and over four times the chart sea's luminance");
        // The far sea: three steps far enough apart to register, and land standing above water. A
        // country fill of luminance 0.15 is about the darkest the shipped palette gives after the
        // paper map's quieter saturation.
        Check(L(style.SeaChart) * MapLook.ChartStepShelf * 2.5f <= 0.15f,
            "the lightest chart step is at most two-fifths of the darkest usual country fill");
        float Lstar(float y) => y > 0.008856f ? 116f * MathF.Cbrt(y) - 16f : 903.3f * y;
        Check(Lstar(L(style.SeaChart) * MapLook.ChartStepSlope) - Lstar(L(style.SeaChart) * MapLook.ChartStepAbyss) >= 2f &&
              Lstar(L(style.SeaChart) * MapLook.ChartStepShelf) - Lstar(L(style.SeaChart) * MapLook.ChartStepSlope) >= 2f,
            "each chart step is at least two L* above the one below it");

        // The relief keeps the classic brightness for ANY surface, saturated ones included: only
        // the hue leans. Before this the tint was a coloured multiplier, matched to the classic
        // range only for grey, and a blue country came out a tenth darker in the shade while a
        // red one clipped on lit slopes.
        var surfaces = new[] {
            ("grey", new LinearColour(0.30f, 0.30f, 0.30f)), ("saturated blue", new LinearColour(0.02f, 0.10f, 0.60f)),
            ("saturated red", new LinearColour(0.70f, 0.05f, 0.03f)), ("forest green", new LinearColour(0.06f, 0.25f, 0.05f)),
            ("ochre", new LinearColour(0.55f, 0.35f, 0.08f)) };
        foreach (var (name, surface) in surfaces)
            foreach (var hillshade in new[] { MapLook.HillshadeMin, 0.75f, 1f, 1.2f, MapLook.HillshadeMax })
            {
                var shaded = MapLook.ReliefShade(surface, hillshade, style.Shade, style.Lamp, style.ReliefTint);
                Check(Near(L(shaded), L(surface) * hillshade, 0.002f),
                    "relief on " + name + " at hillshade " + hillshade.ToString(CultureInfo.InvariantCulture) +
                    " keeps the classic luminance");
            }
        var blue = surfaces[1].Item2;
        var red = surfaces[2].Item2;
        var blueShade = MapLook.ReliefShade(blue, MapLook.HillshadeMin, style.Shade, style.Lamp, style.ReliefTint);
        var redLit = MapLook.ReliefShade(red, MapLook.HillshadeMax, style.Shade, style.Lamp, style.ReliefTint);
        Check(blueShade.B >= blue.B * MapLook.HillshadeMin && redLit.R <= red.R * MapLook.HillshadeMax * 1.05f,
            "in the shade a blue country keeps at least the grey hillshade's blue; on a lit slope a red one gains under five per cent red");
        Check(style.Shade.B > style.Shade.R && style.Lamp.R > style.Lamp.B,
            "warm light, cool shade (Imhof): the shade leans blue, the lamp warm");

        var errors = new List<string>();
        InterfaceSkin.Merge("{ \"colours\": { \"map.look_paper\": \"#ffffff\" }, \"metrics\": { " +
                            "\"map.look_paper_from_distance\": 30, \"map.look_paper_full_distance\": 10, \"map.look_cover_far\": 4 } }",
            "test", errors);
        var modded = MapLook.Current();
        Check(errors.Count == 0 && Near(modded.Paper, new LinearColour(1f, 1f, 1f)) &&
              modded.PaperFullDistance >= modded.PaperFromDistance && modded.CoverFar <= 1f,
            "a mod's skin wins key by key, and cannot turn the paper regime inside out or cover more than everything");
        InterfaceSkin.ResetCatalog();
        Check(MapLook.Current() == style, "resetting the skin restores the shipped look");
    }

    private static void Paper()
    {
        InterfaceSkin.ResetCatalog();
        var style = MapLook.Current();
        foreach (var flat in new[] { false, true })
        {
            var (from, full, lowest, highest) = flat ? (9.0f, 11.5f, 0.1f, 11.5f) : (17f, 22f, 5.25f, 24f);
            var previous = -1f;
            var monotone = true;
            for (var x = lowest; x <= highest + 0.0001f; x += 0.01f)
            {
                var mix = MapLook.PaperMix(flat, x, style);
                if (mix < previous - 1e-6f || mix < 0f || mix > 1f) monotone = false;
                previous = mix;
            }
            var name = flat ? "flat map" : "globe";
            Check(monotone, name + ": the paper share never falls as the camera pulls back, and stays within 0..1");
            Check(MapLook.PaperMix(flat, from - 0.01f, style) == 0f && MapLook.PaperMix(flat, lowest, style) == 0f &&
                  MapLook.PaperMix(flat, from, style) == 0f,
                name + ": no paper before the far regime begins");
            Check(MapLook.PaperMix(flat, full, style) == 1f && MapLook.PaperMix(flat, full + 5f, style) == 1f,
                name + ": all paper once the far regime is complete");
            var middle = MapLook.PaperMix(flat, (from + full) * 0.5f, style);
            Check(Near(middle, 0.5f, 0.001f), name + ": a smoothstep, half way at the middle of the span");
        }
        Check(MapLook.PaperMix(true, 8.8f, style) == 0f && MapLook.PaperMix(false, 12.5f, style) == 0f,
            "the default camera on both projections is still the middle regime, not paper");
        Check(MapLook.PaperMix(false, float.NaN, style) == 0f, "a camera that is not a number draws no paper");
    }

    private static void Switch()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dof-map-look-" + Guid.NewGuid().ToString("N"));
        var campaign = SettingsCatalog.CurrentCampaign;
        try
        {
            SettingsCatalog.CurrentCampaign = null;
            PlayerSettings.UseDirectory(folder);
            SettingsCatalog.ResetCatalog(new List<string>());
            Check(MapLook.Atlas(), "the atlas is the default look");
            SettingsCatalog.Store(MapLook.Setting, MapLook.ClassicValue);
            Check(!MapLook.Atlas(), "classic switches the look off with one value");
            SettingsCatalog.Store(MapLook.Setting, MapLook.AtlasValue);
            Check(MapLook.Atlas(), "and atlas switches it back");
            SettingsCatalog.Store(MapLook.Setting, "no-such-look");
            Check(MapLook.Atlas(), "an unknown stored value falls back to the default, the atlas");
        }
        finally
        {
            SettingsCatalog.CurrentCampaign = campaign;
            PlayerSettings.UseDirectory(null);
        }
    }

    private static void ShaderContract()
    {
        var scripts = Path.Combine(GameDataLoader.DataFolder(), "..", "Scripts");
        var full = MapShaderSource.LoadFull();
        Check(full.StartsWith("shader_type spatial;", StringComparison.Ordinal) && full.TrimEnd().EndsWith("}", StringComparison.Ordinal),
            "the full map shader is loaded from the canonical .gdshader asset");

        Check(full.Contains("uniform float atlas_look = 0.0;") && full.Contains("uniform float paper_mix = 0.0;"),
            "both switches of the look start at nought: an unconfigured material draws the classic map");
        var look = File.ReadAllText(Path.Combine(scripts, "gfx", "GlobeMapLook.cs"));
        var uniforms = Regex.Matches(look, "SetShaderParameter\\(\"(\\w+)\"").Select(match => match.Groups[1].Value).Distinct().ToList();
        Check(uniforms.Contains("atlas_look") && uniforms.Contains("paper_mix") && uniforms.Count >= 25,
            "the look sets the switch, the paper share and every role and metric");
        foreach (var uniform in uniforms)
            Check(Regex.IsMatch(full, @"uniform\s+(float|vec3|sampler2D)\s+" + uniform + @"\b"),
                "the full map declares " + uniform);

        // Shader defaults are the same colours the skin ships, so the two never disagree.
        InterfaceSkin.ResetCatalog();
        var style = MapLook.Current();
        foreach (var (uniform, colour) in new[] {
                     ("look_paper", style.Paper), ("look_lamp", style.Lamp), ("look_sea_deep", style.SeaDeep),
                     ("look_sea_mid", style.SeaMid), ("look_sea_shelf", style.SeaShelf), ("look_sea_coast", style.SeaCoast),
                     ("look_sea_chart", style.SeaChart), ("look_foam", style.Foam), ("look_coast_ink", style.CoastInk),
                     ("look_shade", style.Shade), ("look_unowned", style.Unowned) })
        {
            var match = Regex.Match(full, @"uniform\s+vec3\s+" + uniform + @"\s*=\s*vec3\(([^)]*)\);");
            var parts = match.Success ? match.Groups[1].Value.Split(',').Select(p => float.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray() : null;
            Check(parts is { Length: 3 } && Near(new LinearColour(parts[0], parts[1], parts[2]), colour, 0.0006f),
                "shader default of " + uniform + " is the shipped colour");
        }

        Check(full.Contains("float frontier_cells_all") && Regex.Matches(full, @"texture\(frontier_field, UV\)").Count == 1,
            "the frontier field is read once, exactly as texture(frontier_field, UV), so Compatibility can cut it");

        // Metric defaults in the shader are the compiled ones, so an unconfigured material and a
        // configured one agree on numbers as well as on colours.
        foreach (var (uniform, value) in new[] {
                     ("look_relief_tint", style.ReliefTint), ("look_ground_show_near", style.GroundShowNear),
                     ("look_cover_far", style.CoverFar), ("look_saturation_far", style.SaturationFar),
                     ("look_relief_far", style.ReliefFar), ("look_far_band", style.FarBand),
                     ("look_far_band_pixels", style.FarBandPixels), ("look_foam_strength", style.FoamStrength),
                     ("look_waterline", style.Waterline), ("look_sea_sheen", style.SeaSheen),
                     ("look_selection_width", style.SelectionWidth) })
        {
            var match = Regex.Match(full, @"uniform\s+float\s+" + uniform + @"\s*=\s*([0-9.]+);");
            Check(match.Success && Near(float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), value, 0.00001f),
                "shader default of " + uniform + " is the shipped number");
        }

        // The review's findings, held in place (docs/MAP_LOOK_WAVE_1_2026_11_18_RU.md, "Разбор замечаний").
        Check(full.Contains("water += surf_color * surf_edge * 0.50 * wave_fade;") &&
              full.Contains("water = mix(water, water * 0.58, shore_edge * 0.38 * (1.0 - political.a));"),
            "the atlas replaces only the one-pixel surf: the dark frame on the water side of every coast stays");
        Check(full.Contains("vec3 shaded = base * hillshade;") && !full.Contains("look_shade_mul"),
            "the relief's brightness is the classic grey hillshade's; only the hue leans");
        Check(!full.Contains("SCREEN_UV, vec2(0.28, 0.22)") && full.Contains("water *= 1.0 + sheen * look_sea_sheen"),
            "the sea sheen brightens the water's own colour, on the globe only, never fixed to the screen");
        Check(!full.Contains("selection_outer") && full.Contains("look_selection_width"),
            "no glow outside the picked country, which the id block could not place: a heavier gold stroke instead");
        Check(MapBorders.Unowned == 65535 && full.Contains("abs(near_nation - 65535.0) < 0.5"),
            "unowned land is found by the id MapBorders.Unowned, which the shader spells as 65535");
        Check(full.Contains("look_far_band_pixels * id_aa"), "the far band is a few screen pixels wide at every zoom");
        Check(full.Contains("textureLod(political_map,") && full.Contains("coast_side = mix(coast_side, coast_far, frontier_weight);"),
            "from the middle zoom out a coast is told from a frontier by the filtered land mask, not the id block");
        Check(!full.Contains("look_border_wobble") && !full.Contains("look_paper_grain") &&
              full.Contains("float atlas_noise(vec2 uv, float across, float flat_share)"),
            "no border wobble or paper fibre; the one map noise left keeps its cells square on the globe");
        var fonts = File.ReadAllText(Path.Combine(scripts, "ui", "PresentationFonts.cs"));
        Check(fonts.Contains("MultichannelSignedDistanceField = true") && fonts.Contains("MapMsdfPixelRange = 40") &&
              fonts.Contains("IsMapLabelRole(role)"),
            "dynamic map label fonts use MSDF with pixel range >= 2x max outline while leaving small UI fonts hinted");
    }
}
