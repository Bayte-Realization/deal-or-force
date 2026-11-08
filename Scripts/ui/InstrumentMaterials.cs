using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Small shared nine-slices, not a shader or a viewport per widget. All colours come
/// from the skin; generated pixels and brushes are discarded when that skin changes.
/// Content margins stay exactly as authored: adding depth must not move hit targets.
/// </summary>
internal static class InstrumentMaterials
{
    private readonly record struct FaceKey(Color Fill, Color Edge, bool Inset, int Radius, bool Disabled);
    private readonly record struct BrushKey(FaceKey Face, Vector4 Margins);
    private static readonly Dictionary<FaceKey, Texture2D> Faces = new();
    private static readonly Dictionary<BrushKey, StyleBoxTexture> Brushes = new();
    private static readonly Dictionary<int, Texture2D> Handles = new();
    private static readonly Dictionary<(Color, bool), StyleBox> Meters = new();
    internal static int TextureCount => Faces.Count + Handles.Count;
    internal static int TextureBytes => Faces.Count * 64 * 32 * 4 + Handles.Count * 20 * 24 * 4;

    static InstrumentMaterials() => InterfaceSkin.Changed += () =>
    {
        Faces.Clear(); Brushes.Clear(); Handles.Clear(); Meters.Clear();
    };

    internal static StyleBox Face(StyleBoxFlat source, bool inset = false, bool disabled = false)
    {
        var face = new FaceKey(source.BgColor, source.BorderColor, inset,
            Math.Clamp(source.CornerRadiusTopLeft, 0, 8), disabled);
        var margins = new Vector4(source.ContentMarginLeft, source.ContentMarginTop,
            source.ContentMarginRight, source.ContentMarginBottom);
        var key = new BrushKey(face, margins);
        if (Brushes.TryGetValue(key, out var cached)) return cached;
        if (!Faces.TryGetValue(face, out var texture))
        {
            // Modded per-country graph colours must not create an unbounded GPU cache.
            if (Faces.Count >= 128) return source;
            using var pixels = Image.CreateEmpty(64, 32, false, Image.Format.Rgba8);
            var relief = AtlasSurfaceSkin.Relief * (disabled ? .22f : 1f);
            var grain = AtlasSurfaceLighting.GrainAmount(Ui.Metric("atlas.grain", .018f));
            var light = Ui.Colour("text.bright"); var dark = Ui.Colour("surface.background");
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 64; x++)
            {
                var v = y / 31f;
                var illumination = AtlasSurfaceLighting.MaterialShade(x / 63f, v, inset, relief);
                // Additive grain stays visible on dark slate without bleaching text contrast.
                illumination += AtlasSurfaceLighting.Grain(x, y) * grain * .14f * relief;
                var colour = illumination >= 0 ? face.Fill.Lerp(light, illumination)
                    : face.Fill.Lerp(dark, -illumination);
                var dx = Math.Min(x, 63 - x); var dy = Math.Min(y, 31 - y);
                if (dx == 0 || dy == 0) colour = face.Edge;
                else if (y == 1) colour = colour.Lerp(inset ? dark : light, (inset ? .56f : .22f) * relief);
                else if (y == 30) colour = colour.Lerp(inset ? light : dark, (inset ? .09f : .64f) * relief);
                else if (x == 1) colour = colour.Lerp(inset ? dark : light, .08f * relief);
                var cx = Math.Max(0, face.Radius - dx); var cy = Math.Max(0, face.Radius - dy);
                colour.A *= Math.Clamp(face.Radius + 1f - MathF.Sqrt(cx * cx + cy * cy), 0, 1);
                pixels.SetPixel(x, y, colour);
            }
            Faces[face] = texture = ImageTexture.CreateFromImage(pixels);
        }
        return Brushes[key] = new StyleBoxTexture
        {
            Texture = texture,
            TextureMarginLeft = Math.Max(2, face.Radius), TextureMarginRight = Math.Max(2, face.Radius),
            TextureMarginTop = Math.Max(2, face.Radius), TextureMarginBottom = Math.Max(2, face.Radius),
            ContentMarginLeft = margins.X, ContentMarginTop = margins.Y,
            ContentMarginRight = margins.Z, ContentMarginBottom = margins.W
        };
    }

    internal static StyleBox Meter(string token, bool inset = false) => Meter(Ui.Colour(token), inset);
    internal static StyleBox Meter(Color fill, bool inset = false)
    {
        if (Meters.TryGetValue((fill, inset), out var cached)) return cached;
        var frame = new StyleBoxFlat { BgColor = fill,
            BorderColor = inset ? Ui.Colour("surface.line") : fill.Darkened(.38f) };
        frame.SetCornerRadiusAll(2); frame.SetContentMarginAll(0);
        var brush = Face(frame, inset);
        if (Meters.Count < 256) Meters[(fill, inset)] = brush;
        return brush;
    }

    /// <summary>A knurled grip, with an explicit separate disabled state; the hit area is unchanged.</summary>
    internal static Texture2D SliderHandle(int state)
    {
        if (Handles.TryGetValue(state, out var cached)) return cached;
        using var pixels = Image.CreateEmpty(20, 24, false, Image.Format.Rgba8);
        var disabled = state == 3;
        var fill = Ui.Colour(disabled ? "button.fill_disabled" : state == 1 ? "text.accent" : "text.accent_dim");
        var edge = Ui.Colour(disabled ? "button.border_disabled" : "button.primary_border");
        var dark = Ui.Colour("surface.background"); var light = Ui.Colour("text.bright");
        for (var y = 3; y < 21; y++)
        for (var x = 3; x < 17; x++)
        {
            var border = x == 3 || x == 16 || y == 3 || y == 20;
            var colour = border ? edge : fill.Lerp(light, .16f * (20 - y) / 17f);
            if (y == 4 && !border) colour = colour.Lerp(light, disabled ? .04f : .3f);
            if (y == 19 && !border) colour = colour.Lerp(dark, .55f);
            if ((x == 7 || x == 10 || x == 13) && y >= 8 && y <= 15) colour = colour.Lerp(dark, .52f);
            pixels.SetPixel(x, y, colour);
        }
        return Handles[state] = ImageTexture.CreateFromImage(pixels);
    }
}

