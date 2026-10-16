using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>Shared atlas materials, generated once per skin rather than on ticks or pointer motion.</summary>
internal static class AtlasSurfaceSkin
{
    internal enum Level { Instrument, Document, Inset, Raised }
    private static readonly Dictionary<string, StyleBoxTexture> Buttons = new();
    private static readonly Dictionary<string, Texture2D> Panels = new();
    private static Texture2D _grain;
    private static StyleBoxFlat _focus, _roundFocus;
    private static Texture2D _musicGlyph;
    static AtlasSurfaceSkin() => InterfaceSkin.Changed += () => { Buttons.Clear(); Panels.Clear(); _grain = null; _focus = null; _roundFocus = null; };

    internal static float Relief => AtlasSurfaceLighting.Relief(Ui.Metric("atlas.relief", .65f));

    internal static StyleBoxTexture Button(bool primary, int state, bool iconOnly, bool narrow, bool rounded = false)
    {
        var key = $"{primary}|{state}|{iconOnly}|{narrow}|{rounded}";
        if (Buttons.TryGetValue(key, out var cached)) return cached;
        var fill = Ui.Colour(state switch
        {
            1 => primary ? "button.primary_fill_hover" : "button.fill_hover",
            2 => primary ? "button.primary_fill_pressed" : "button.fill_pressed",
            3 => primary ? "button.primary_fill" : "button.fill_disabled",
            _ => primary ? "button.primary_fill" : "button.fill"
        });
        var edge = Ui.Colour(state switch
        {
            1 => "button.border_hover", 2 => "button.border_pressed",
            3 => primary ? "button.primary_border" : "button.border_disabled",
            _ => primary ? "button.primary_border" : "button.border"
        });
        const int width = 48, height = 40;
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        var relief = Relief * (state == 3 ? .25f : 1f);
        var shadow = Ui.Colour("surface.background");
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var light = AtlasSurfaceLighting.Light(x / (float)(width - 1), y / (float)(height - 1), state == 2) * relief;
            var colour = light >= 0 ? fill.Lightened(light) : fill.Darkened(-light);
            var distance = Math.Min(Math.Min(x, width - 1 - x), Math.Min(y, height - 1 - y));
            if (distance == 0) colour = edge.Darkened(y > height / 2 ? .28f * relief : 0);
            else if (y == 1) colour = state == 2 ? shadow.Lerp(fill, .6f) : fill.Lightened(.19f * relief);
            else if (y == height - 2) colour = state == 2 ? fill.Lightened(.08f * relief) : fill.Lerp(shadow, .55f * relief);
            // Utility shortcuts may use a softer capsule without changing every workbench button.
            var radius = rounded ? 8 : 3;
            var cornerX = Math.Max(0, radius - Math.Min(x, width - 1 - x));
            var cornerY = Math.Max(0, radius - Math.Min(y, height - 1 - y));
            colour.A *= Math.Clamp(radius + 1.3f - MathF.Sqrt(cornerX * cornerX + cornerY * cornerY), 0, 1);
            image.SetPixel(x, y, colour);
        }
        var style = new StyleBoxTexture
        {
            Texture = ImageTexture.CreateFromImage(image), TextureMarginLeft = rounded ? 10 : 5, TextureMarginRight = rounded ? 10 : 5,
            TextureMarginTop = rounded ? 10 : 5, TextureMarginBottom = rounded ? 10 : 5,
            ContentMarginLeft = narrow ? 2 : 10, ContentMarginRight = narrow ? 2 : 10,
            ContentMarginTop = iconOnly ? (state == 2 ? 9 : 7) : (state == 2 ? 7 : 6),
            ContentMarginBottom = iconOnly ? (state == 2 ? 5 : 7) : (state == 2 ? 5 : 8)
        };
        Buttons[key] = style;
        return style;
    }

    internal static StyleBoxFlat Focus(bool rounded = false)
    {
        var cached = rounded ? _roundFocus : _focus;
        if (cached is not null) return cached;
        var focus = new StyleBoxFlat { DrawCenter = false, BorderColor = Ui.Colour("text.accent", .8f) };
        focus.SetBorderWidthAll((int)Math.Clamp(Ui.Metric("button.focus_width", 2), 1, 3));
        focus.SetCornerRadiusAll(rounded ? 8 : 3);
        focus.SetContentMarginAll(0);
        if (rounded) _roundFocus = focus; else _focus = focus;
        return focus;
    }

    internal static Texture2D MusicGlyph()
    {
        if (_musicGlyph is not null) return _musicGlyph;
        var image = Image.CreateEmpty(48, 48, false, Image.Format.Rgba8);
        image.Fill(Colors.Transparent);
        for (var y = 0; y < 48; y++)
        for (var x = 0; x < 48; x++)
        {
            var u = (x + .5f) * .5f; var v = (y + .5f) * .5f;
            var headA = MathF.Pow((u - 7.5f) / 3.5f, 2) + MathF.Pow((v - 18) / 2.5f, 2) <= 1;
            var headB = MathF.Pow((u - 16.5f) / 3.5f, 2) + MathF.Pow((v - 16) / 2.5f, 2) <= 1;
            var stem = (u >= 9 && u <= 11 && v >= 5 && v <= 18) || (u >= 18 && u <= 20 && v >= 3 && v <= 16);
            var beam = u >= 9 && u <= 20 && v >= 5 - (u - 9) * (2f / 9) && v <= 7 - (u - 9) * (2f / 9);
            if (headA || headB || stem || beam) image.SetPixel(x, y, Colors.White);
        }
        return _musicGlyph = ImageTexture.CreateFromImage(image);
    }

    internal static AtlasPanelFinish Attach(PanelContainer panel, Level level, string ground = "shell.background")
    {
        var finish = new AtlasPanelFinish { Name = "AtlasMaterial", SurfaceLevel = level, Ground = ground,
            MouseFilter = Control.MouseFilterEnum.Ignore, FocusMode = Control.FocusModeEnum.None };
        panel.AddChild(finish);
        panel.MoveChild(finish, 0);
        return finish;
    }

    internal static Texture2D PanelTexture(Level level, string ground)
    {
        var key = level + "|" + ground;
        if (Panels.TryGetValue(key, out var cached)) return cached;
        var fill = Ui.Colour(ground);
        var accent = Ui.Colour("text.accent");
        const int size = 96;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var relief = Relief;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var u = x / (float)(size - 1); var v = y / (float)(size - 1);
            var illumination = AtlasSurfaceLighting.Light(u, v, level == Level.Inset) * relief;
            var colour = illumination >= 0 ? fill.Lightened(illumination * .65f) : fill.Darkened(-illumination);
            var lamp = MathF.Exp(-u * 3 - v * 5) * relief;
            if (level == Level.Document) colour = colour.Lerp(accent, lamp * .055f);
            if (level == Level.Raised) colour = colour.Lightened(.025f * relief);
            image.SetPixel(x, y, colour);
        }
        return Panels[key] = ImageTexture.CreateFromImage(image);
    }

    internal static Texture2D GrainTexture()
    {
        if (_grain is not null) return _grain;
        const int size = 64;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var amount = AtlasSurfaceLighting.GrainAmount(Ui.Metric("atlas.grain", .018f));
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var value = AtlasSurfaceLighting.Grain(x, y);
            var ink = Ui.Colour(value > 0 ? "text.body" : "surface.background");
            ink.A = Math.Abs(value) * amount;
            image.SetPixel(x, y, ink);
        }
        return _grain = ImageTexture.CreateFromImage(image);
    }

    internal static StyleBoxFlat Section(string ground = "surface.inset")
    {
        var style = new StyleBoxFlat { BgColor = Ui.Colour(ground), BorderColor = Ui.Colour("surface.line_faint"),
            ShadowColor = Ui.Colour("surface.background", .35f), ShadowSize = 4, ShadowOffset = new(0, 2) };
        style.SetBorderWidthAll(1); style.SetContentMarginAll(10); style.SetCornerRadiusAll(2);
        return style;
    }
}

/// <summary>Non-interactive material underneath content. No process loop, blur, shader or world state.</summary>
public partial class AtlasPanelFinish : Control
{
    internal AtlasSurfaceSkin.Level SurfaceLevel;
    internal string Ground = "shell.background";
    public override void _EnterTree() { InterfaceSkin.Changed += RedrawMaterial; Resized += QueueRedraw; }
    public override void _ExitTree() { InterfaceSkin.Changed -= RedrawMaterial; Resized -= QueueRedraw; }
    private void RedrawMaterial()
    {
        if (GetParent() is PanelContainer panel && panel.HasMeta("atlas_section_ground"))
            panel.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section(panel.GetMeta("atlas_section_ground").AsString()));
        QueueRedraw();
    }
    public override void _Draw()
    {
        if (Size.X <= 0 || Size.Y <= 0) return;
        var rect = new Rect2(Vector2.Zero, Size);
        DrawTextureRect(AtlasSurfaceSkin.PanelTexture(SurfaceLevel, Ground), rect, false);
        DrawTextureRect(AtlasSurfaceSkin.GrainTexture(), rect, true);
        var light = Ui.Colour("text.body", .09f * AtlasSurfaceSkin.Relief);
        var shadow = Ui.Colour("surface.background", .75f * AtlasSurfaceSkin.Relief);
        DrawLine(new(0, .5f), new(Size.X, .5f), SurfaceLevel == AtlasSurfaceSkin.Level.Inset ? shadow : light);
        DrawLine(new(.5f, 0), new(.5f, Size.Y), SurfaceLevel == AtlasSurfaceSkin.Level.Inset ? shadow : light);
        DrawLine(new(0, Size.Y - .5f), new(Size.X, Size.Y - .5f), SurfaceLevel == AtlasSurfaceSkin.Level.Inset ? light : shadow);
    }
}
