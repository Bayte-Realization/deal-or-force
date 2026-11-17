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
        // The design's table, word for word (docs/design/graphics-2026-11-18/map-look.md).
        var table = new Dictionary<string, string>
        {
            ["map.look_paper"] = "#e6d9bb", ["map.look_shade"] = "#5b4a3c", ["map.look_lamp"] = "#fff1d2",
            ["map.look_sea_deep"] = "#18323f", ["map.look_sea_mid"] = "#234b58", ["map.look_sea_shelf"] = "#3d6b70",
            ["map.look_sea_coast"] = "#6a9690", ["map.look_sea_chart"] = "#50707a", ["map.look_foam"] = "#ece5d2",
            ["map.look_coast_ink"] = "#2e4048",
        };
        Check(MapLook.DefaultColours.Length == table.Count &&
              MapLook.DefaultColours.All(entry => table.TryGetValue(entry.Token, out var hex) &&
                  string.Equals(hex, entry.Hex, StringComparison.OrdinalIgnoreCase)),
            "the compiled colour fallbacks are the design's starting table");
        foreach (var (token, hex) in table)
            Check(InterfaceSkin.Has(token) && string.Equals(InterfaceSkin.Colour(token), hex, StringComparison.OrdinalIgnoreCase) &&
                  !InterfaceSkin.Overridden.Contains(token),
                "game/gui/78_map_look.json and the compiled skin agree on " + token);

        foreach (var (token, compiled) in new[] {
                     ("map.look_cover_near", MapLook.CoverNearDefault), ("map.look_flat_cover_near", MapLook.FlatCoverNearDefault),
                     ("map.look_cover_far", MapLook.CoverFarDefault), ("map.look_saturation_far", MapLook.SaturationFarDefault),
                     ("map.look_relief_far", MapLook.ReliefFarDefault), ("map.look_paper_from_distance", MapLook.PaperFromDistanceDefault),
                     ("map.look_paper_full_distance", MapLook.PaperFullDistanceDefault),
                     ("map.look_flat_paper_from_zoom", MapLook.FlatPaperFromZoomDefault),
                     ("map.look_flat_paper_full_zoom", MapLook.FlatPaperFullZoomDefault),
                     ("map.look_border_wobble", MapLook.BorderWobbleDefault), ("map.look_far_band", MapLook.FarBandDefault),
                     ("map.look_foam_strength", MapLook.FoamDefault), ("map.look_waterline", MapLook.WaterlineDefault),
                     ("map.look_sea_sheen", MapLook.SeaSheenDefault), ("map.look_selection_halo", MapLook.SelectionHaloDefault),
                     ("map.look_paper_grain", MapLook.PaperGrainDefault) })
            Check(Near(InterfaceSkin.Metric(token, float.NaN), compiled, 0.00001f),
                "game/gui/78_map_look.json ships " + token + " at the compiled value");

        Check(MapLook.CoverNearDefault == 0.70f && MapLook.FlatCoverNearDefault == 0.80f && MapLook.CoverFarDefault == 0.95f &&
              MapLook.SaturationFarDefault == 0.80f && MapLook.ReliefFarDefault == 0.45f &&
              MapLook.PaperFromDistanceDefault == 17f && MapLook.PaperFullDistanceDefault == 22f &&
              MapLook.FlatPaperFromZoomDefault == 9.0f && MapLook.FlatPaperFullZoomDefault == 11.5f &&
              MapLook.BorderWobbleDefault == 0.22f && MapLook.FarBandDefault == 0.30f && MapLook.FoamDefault == 0.55f &&
              MapLook.WaterlineDefault == 0.15f && MapLook.SeaSheenDefault == 0.06f &&
              MapLook.SelectionHaloDefault == 0.10f && MapLook.PaperGrainDefault == 0.035f,
            "the compiled metric fallbacks are the brief's numbers");

        var style = MapLook.Current();
        Check(Near(style.Paper, LinearColour.FromHex("#e6d9bb", default)) && Near(style.CoastInk, LinearColour.FromHex("#2e4048", default)) &&
              Near(style.SeaDeep, new LinearColour(0.0091f, 0.0319f, 0.0497f), 0.0002f),
            "colours reach the shader as linear light");
        // The brightness order of the design: land paper over coastal water over open sea; inks darkest.
        float L(LinearColour c) => MapLook.Luminance(c);
        Check(L(style.Paper) > L(style.SeaCoast) && L(style.SeaCoast) > L(style.SeaShelf) && L(style.SeaShelf) > L(style.SeaMid) &&
              L(style.SeaMid) > L(style.SeaDeep) && L(style.CoastInk) < L(style.SeaShelf) && L(style.Foam) > L(style.SeaCoast),
            "one brightness order: paper, coastal water, shelf, open sea; the coast ink below the shelf; foam above the sea");
        var shade = MapLook.ScaledTo(style.Shade, MapLook.ShadeLuminance);
        var lamp = MapLook.ScaledTo(style.Lamp, MapLook.LampLuminance);
        Check(Near(L(shade), 0.52f, 0.0005f) && Near(L(lamp), 1.42f, 0.0005f) && shade.R > shade.B && lamp.R > lamp.B,
            "the warm shade and lamp keep the old relief range (0.52 .. 1.42) and only change its colour");

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
        var globe = File.ReadAllText(Path.Combine(scripts, "gfx", "GlobePresentation.cs"));
        var start = globe.IndexOf("Code = @\"shader_type spatial;", StringComparison.Ordinal) + "Code = @\"".Length;
        var end = globe.IndexOf("\n}\"", start, StringComparison.Ordinal);
        Check(start > 20 && end > start, "the full map shader is located");
        var full = globe[start..(end + 2)];

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
                     ("look_shade_mul", MapLook.ScaledTo(style.Shade, MapLook.ShadeLuminance)),
                     ("look_lamp_mul", MapLook.ScaledTo(style.Lamp, MapLook.LampLuminance)) })
        {
            var match = Regex.Match(full, @"uniform\s+vec3\s+" + uniform + @"\s*=\s*vec3\(([^)]*)\);");
            var parts = match.Success ? match.Groups[1].Value.Split(',').Select(p => float.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray() : null;
            Check(parts is { Length: 3 } && Near(new LinearColour(parts[0], parts[1], parts[2]), colour, 0.0006f),
                "shader default of " + uniform + " is the shipped colour");
        }

        Check(full.Contains("float frontier_cells_all") && Regex.Matches(full, @"texture\(frontier_field, UV\)").Count == 1,
            "the frontier field is read once, exactly as texture(frontier_field, UV), so Compatibility can cut it");
    }
}
