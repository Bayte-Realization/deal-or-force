using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;
using State = DealOrForce.Simulation.InterfaceMaterials.SurfaceState;

/// <summary>
/// Materials as data (step 1a) and the one light they are baked under (step 1b) -
/// docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md.
/// </summary>
static class InterfaceMaterialTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("interface materials: " + label);
        Console.WriteLine("PASS interface materials: " + label);
    }

    private static int Luma(byte[] pixels, int size, int x, int y)
    {
        var i = (y * size + x) * 4;
        return pixels[i] + pixels[i + 1] + pixels[i + 2];
    }

    private static byte Alpha(byte[] pixels, int size, int x, int y) => pixels[(y * size + x) * 4 + 3];

    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(73);
        var fingerprint = CampaignFingerprint.Of(campaign);
        InterfaceSkin.ResetCatalog();

        // ---------------------------------------------------------------- 1a: the catalogue --
        var roles = new[] { "plate", "plate.header", "plate.strip", "plate.modal", "well", "button",
            "button.primary", "chip", "round", "flag.mount", "tooltip", "token" };
        var absent = roles.Where(role => !InterfaceMaterials.HasRole(role)).ToList();
        Check(absent.Count == 0, "every planned role ships in game/interface/10_materials.json" +
            (absent.Count == 0 ? "" : ": missing " + string.Join(", ", absent)));
        var header = InterfaceMaterials.Resolve("plate.header");
        Check(header.Notches == 5 && header.EngraveInset == 4 && header.Top == "material.plate.top" && header.DropShadow == 6,
            "a role inherits every field it does not set");
        Check(InterfaceMaterials.Resolve("tooltip").DropShadow == 12 && InterfaceMaterials.Resolve("tooltip").Bevel == 1,
            "inheritance runs through more than one step (tooltip, plate.modal, plate)");
        var well = InterfaceMaterials.Resolve("well");
        Check(well.Inset && well.DropShadow == 0 && well.InnerShadow == 4 && well.Top == "material.well.fill", "a well is sunk, with an inner shadow and no drop shadow");
        Check(InterfaceMaterials.Resolve("round").Shape == "circle" && InterfaceMaterials.Resolve("flag.mount").Shape == "shield",
            "round buttons and the flag mount carry their shapes");
        Check(InterfaceMaterials.Resolve("no.such.role").Role == "plate" && InterfaceMaterials.Resolve(null).Role == "plate",
            "an unknown role draws as plate, never magenta");
        Check(InterfaceMaterials.ColourOf("material.plate.top", "#000000") == "#2a2520" &&
              InterfaceMaterials.ColourOf("no.such.token", "#123456") == "#123456",
            "material colours come from skin tokens, with the plate default for a token nobody declared");

        var errors = new List<string>();
        InterfaceSkin.Merge("{\"materials\":{\"plate\":{\"bevel\":1e999,\"drop_shadow\":-1e999,\"engrave_inset\":99," +
                            "\"grain\":50,\"corner_radius\":-3,\"content_margin\":[1e999,-5,2,3]}}}", "mod", errors);
        var clamped = InterfaceMaterials.Resolve("plate");
        Check(clamped.Bevel == 4 && clamped.DropShadow == 0 && clamped.EngraveInset == 12 && clamped.Grain == 2 &&
              clamped.CornerRadius == 0 && clamped.ContentMargin.SequenceEqual(new[] { 64, 0, 2, 3 }),
            "out-of-range and infinite numbers from a mod are clamped to the safe envelope");
        InterfaceMaterials.Override("plate", "bevel", double.NaN);
        InterfaceMaterials.Override("plate", "drop_shadow", double.PositiveInfinity);
        Check(InterfaceMaterials.Resolve("plate").Bevel == 1 && InterfaceMaterials.Resolve("plate").DropShadow == 24,
            "NaN falls back to the default and infinity to the bound: never a non-finite surface");
        InterfaceSkin.Merge("{\"lighting\":{\"grain\":1e999,\"direction\":[0,0],\"highlight\":-4}}", "mod", errors);
        var lit = InterfaceMaterials.Lighting;
        Check(lit.Grain == .05f && lit.Highlight == 0 && MathF.Abs(lit.DirectionX + .70710678f) < 1e-4f &&
              MathF.Abs(lit.DirectionY + .70710678f) < 1e-4f,
            "lighting is clamped too, and a zero direction is the shipped upper-left light");
        errors.Clear();
        InterfaceSkin.Merge("{\"materials\":{\"plate\":{\"rivets\":3,\"shape\":\"hexagon\"}},\"lighting\":{\"sun\":1}}", "mod", errors);
        Check(errors.Count == 3 && errors.Any(e => e.Contains("rivets")) && InterfaceMaterials.Resolve("plate").Shape == "rect",
            "unknown fields and shapes are reported and ignored, never fatal");

        InterfaceSkin.ResetCatalog();
        InterfaceSkin.Merge("{\"metrics\":{\"button.corner_radius\":7,\"button.border_width\":2,\"panel.texture_margin\":20}}", "mod", errors);
        Check(InterfaceMaterials.Resolve("button").CornerRadius == 7 && InterfaceMaterials.Resolve("button.primary").CornerRadius == 7 &&
              InterfaceMaterials.Resolve("button").BorderWidth == 2 && InterfaceMaterials.Resolve("chip").CornerRadius == 8 &&
              InterfaceMaterials.Resolve("plate").CornerRadius == 3,
            "button.corner_radius and button.border_width reach the button family and nothing else");
        Check(InterfaceMaterials.Resolve("plate").TextureMargin.All(m => m == 20), "panel.texture_margin is the nine-slice default");

        InterfaceSkin.ResetCatalog();
        InterfaceSkin.Merge("{\"materials\":{\"plate\":{\"bevel\":2}}}", "later", errors);
        var late = InterfaceMaterials.Resolve("plate");
        Check(late.Bevel == 2 && late.EngraveInset == 4 && late.Top == "material.plate.top" && InterfaceMaterials.Resolve("plate.header").Bevel == 2,
            "a later file changes one field of a material, keeps the rest, and the change reaches its heirs");
        InterfaceSkin.ResetCatalog();

        // ------------------------------------------------------ 1b: one light, one shader --
        SpriteCatalog.Load();
        var shaderPath = SpriteCatalog.ShaderOf("ui_relief", "");
        Check(shaderPath.EndsWith("game/gfx/interface/shaders/ui_relief.gdshader", StringComparison.Ordinal),
            "the relief shader is registered by data, so a mod can replace it");
        var source = File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(), "gfx", "interface", "shaders", "ui_relief.gdshader"));
        var code = Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        Check(code.Contains("shader_type canvas_item;") && !code.Contains("SCREEN_TEXTURE") && !code.Contains("hint_screen_texture") &&
              !Regex.IsMatch(code, @"\bTIME\b") && !Regex.IsMatch(code, @"\bfor\b") && !Regex.IsMatch(code, @"\bwhile\b"),
            "the relief shader reads no screen, no clock and has no loops");
        var defaultLight = Regex.Match(code, @"uniform\s+vec2\s+light_dir\s*=\s*vec2\(([^)]*)\)");
        Check(defaultLight.Success && defaultLight.Groups[1].Value.Split(',').All(v => float.Parse(v.Trim(), System.Globalization.CultureInfo.InvariantCulture) == 0f) &&
              !Regex.IsMatch(code, @"vec2\(\s*-?0?\.7"),
            "the shader has no light direction of its own: it is given the one from lighting.direction");

        var light = InterfaceMaterials.Lighting;
        Check(InterfaceMaterials.Directional(0, 0, light.DirectionX, light.DirectionY) > 0 &&
              InterfaceMaterials.Directional(1, 1, light.DirectionX, light.DirectionY) < 0,
            "the shipped light falls from the upper left, in the bake and in the uniform alike");
        InterfaceSkin.Merge("{\"lighting\":{\"direction\":[1,0]}}", "mod", errors);
        var moved = InterfaceMaterials.Lighting;
        Check(moved.DirectionX == 1 && moved.DirectionY == 0 &&
              InterfaceMaterials.Directional(1, .5f, moved.DirectionX, moved.DirectionY) > 0 &&
              InterfaceMaterials.Directional(0, .5f, moved.DirectionX, moved.DirectionY) < 0,
            "moving the light in data moves it in the bake: nothing is wired to the upper left");
        InterfaceSkin.ResetCatalog();

        var plate = InterfaceMaterials.Resolve("plate");
        var pixels = InterfaceMaterials.Bake(plate, State.Normal, out var size, out var pad);
        Check(size == InterfaceMaterials.BakeSize + 2 * 6 && pad == 6 && pixels.Length == size * size * 4,
            "a plate bakes into one 48-pixel face with its 6-pixel shadow around it");
        Check(Alpha(pixels, size, size / 2, size / 2) == 255 && Alpha(pixels, size, 0, 0) < 40 &&
              Alpha(pixels, size, size / 2, size - 2) > Alpha(pixels, size, size / 2, 1),
            "the face is opaque and the shadow fades out, falling below it");
        Check(Luma(pixels, size, pad + 12, pad + 12) > Luma(pixels, size, pad + 36, pad + 36),
            "the face is lit toward the light and shaded away from it");
        var line = pad + plate.BorderWidth + plate.EngraveInset;
        Check(Luma(pixels, size, size / 2, line) != Luma(pixels, size, size / 2, line + 3),
            "the engraved line sits engrave_inset pixels inside the border");
        Check(pixels.SequenceEqual(InterfaceMaterials.Bake(plate, State.Normal, out _, out _)), "baking is deterministic: no clock, no random stream");
        var notched = InterfaceMaterials.Bake(header, State.Normal, out var headerSize, out var headerPad);
        Check(Alpha(notched, headerSize, headerPad + 1, headerPad + 1) < Alpha(pixels, size, pad + 1, pad + 1),
            "a header's corners are cut");
        var sunk = InterfaceMaterials.Bake(well, State.Normal, out var wellSize, out _);
        Check(Luma(sunk, wellSize, 2, 6) < Luma(sunk, wellSize, 24, 24), "a well is darker under its rim: the inner shadow");
        Check(!InterfaceMaterials.Bake(plate, State.Pressed, out _, out _).SequenceEqual(pixels) &&
              !InterfaceMaterials.Bake(plate, State.Disabled, out _, out _).SequenceEqual(pixels),
            "pressed and disabled are distinct surfaces");
        Check(InterfaceMaterials.BakedMargin(plate) > pad && InterfaceMaterials.BakedMargin(plate) < pad + InterfaceMaterials.BakeSize / 2,
            "the nine-slice margin keeps the corner and the line, and leaves a centre to stretch");

        var count = 0;
        var bytes = 0L;
        foreach (var role in InterfaceMaterials.Roles)
        foreach (var state in Enum.GetValues<State>())
        {
            count++;
            bytes += InterfaceMaterials.BakedBytes(InterfaceMaterials.Resolve(role));
        }
        Check(count <= InterfaceMaterials.MaxBakedTextures && bytes <= InterfaceMaterials.MaxBakedBytes,
            $"every role in every state fits the budget: {count} textures, {bytes / 1024} KB");

        // A finished material has the button and nine-slice metrics baked in, so a file that only
        // changes metrics - an overlay setting square buttons - must still reach a role already drawn.
        InterfaceSkin.ResetCatalog();
        errors.Clear();
        var roundCorners = InterfaceMaterials.Resolve("button");
        InterfaceSkin.Merge("{\"metrics\":{\"button.corner_radius\":0,\"panel.texture_margin\":24}}", "overlay", errors);
        var squared = InterfaceMaterials.Resolve("button");
        Check(roundCorners.CornerRadius == 3 && squared.CornerRadius == 0 && squared.TextureMargin[0] == 24 && errors.Count == 0,
            "a metrics-only change reaches materials that were already resolved");
        InterfaceSkin.Merge("{\"button.border_width\":2}", "overlay", errors);
        Check(InterfaceMaterials.Resolve("button").BorderWidth == 2,
            "so does a bare metric number written without a section");

        Check(fingerprint == CampaignFingerprint.Of(campaign), "materials and their baking never change the world fingerprint");
        InterfaceSkin.ResetCatalog();
    }
}
