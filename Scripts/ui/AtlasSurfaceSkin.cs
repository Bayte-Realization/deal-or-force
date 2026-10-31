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
    private static readonly Dictionary<string, StyleBox> SpecialButtons = new();
    private static readonly Dictionary<string, Texture2D> Panels = new();
    private static Texture2D _grain;
    private static StyleBoxFlat _focus, _roundFocus;
    private static Texture2D _musicGlyph;
    static AtlasSurfaceSkin() => InterfaceSkin.Changed += () =>
    {
        Buttons.Clear();
        SpecialButtons.Clear();
        Panels.Clear();
        _grain = null;
        _focus = null;
        _roundFocus = null;
    };

    internal static float Relief => AtlasSurfaceLighting.Relief(Ui.Metric("atlas.relief", .65f));

    internal static StyleBoxTexture Button(bool primary, int state, bool iconOnly, bool narrow, bool rounded = false)
    {
        var key = $"{primary}|{state}|{iconOnly}|{narrow}|{rounded}";
        if (Buttons.TryGetValue(key, out var cached)) return cached;
        var baseFill = Ui.Colour(state switch
        {
            1 => primary ? "button.primary_fill_hover" : "button.fill_hover",
            2 => primary ? "button.primary_fill_pressed" : "button.fill_pressed",
            3 => primary ? "button.primary_fill" : "button.fill_disabled",
            _ => primary ? "button.primary_fill" : "button.fill"
        });
        var obsidian = Ui.Colour("surface.inset");
        // Blend primary buttons with deep obsidian so they read as dark architectural slate
        // lit by warm brass rather than a flat brown rectangle.
        var fill = primary && state != 3 ? obsidian.Lerp(baseFill, state == 1 ? 0.68f : 0.56f) : baseFill;
        var edge = Ui.Colour(state switch
        {
            1 => "button.border_hover",
            2 => "button.border_pressed",
            3 => primary ? "button.primary_border" : "button.border_disabled",
            _ => primary ? "button.primary_border" : "button.border"
        });
        var accent = Ui.Colour("text.accent");
        var bright = Ui.Colour("text.bright");
        var shadow = Ui.Colour("surface.background");
        const int width = 64, height = 48;
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        var relief = Relief * (state == 3 ? .25f : 1f);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var u = x / (float)(width - 1);
            var v = y / (float)(height - 1);
            // Smooth vertical studio gradient + subtle directional relief for tactile volume.
            var verticalBias = state == 2 ? (v - 0.5f) * 0.14f : (0.45f - v) * 0.16f;
            var directional = AtlasSurfaceLighting.Light(u, v, state == 2) * 0.45f;
            var light = (verticalBias + directional) * relief;
            var colour = light >= 0 ? fill.Lightened(light) : fill.Darkened(-light);
            if (primary && state != 2)
            {
                var topWarmth = MathF.Max(0f, 1f - v * 2.2f) * (state == 1 ? 0.14f : 0.09f) * relief;
                colour = colour.Lerp(accent, topWarmth);
            }

            var distX = Math.Min(x, width - 1 - x);
            var distY = Math.Min(y, height - 1 - y);
            var distance = Math.Min(distX, distY);
            if (distance == 0)
            {
                colour = edge.Darkened(y > height / 2 ? .22f * relief : 0f);
                if (primary && y <= 1 && state != 3)
                    colour = accent.Lightened(state == 1 ? 0.12f : 0.02f);
            }
            else if (primary && y == 1 && x >= 1 && x <= width - 2 && state != 2)
            {
                // Crisp 2px warm brass top accent bar on primary/active controls.
                colour = edge.Lerp(accent, state == 1 ? 0.65f : 0.42f);
            }
            else if (y == 1)
            {
                colour = state == 2
                    ? shadow.Lerp(fill, .45f)
                    : colour.Lerp(bright, (state == 1 ? .18f : .11f) * relief);
            }
            else if (y == height - 2)
            {
                colour = state == 2
                    ? fill.Lightened(.06f * relief)
                    : colour.Lerp(shadow, .62f * relief);
            }
            else if (distX == 1)
            {
                colour = colour.Lightened((state == 1 ? .06f : .03f) * relief);
            }

            var radius = rounded ? 8 : 3;
            var cornerX = Math.Max(0, radius - distX);
            var cornerY = Math.Max(0, radius - distY);
            colour.A *= Math.Clamp(radius + 1.25f - MathF.Sqrt(cornerX * cornerX + cornerY * cornerY), 0, 1);
            image.SetPixel(x, y, colour);
        }
        var style = new StyleBoxTexture
        {
            Texture = ImageTexture.CreateFromImage(image),
            TextureMarginLeft = rounded ? 10 : 6,
            TextureMarginRight = rounded ? 10 : 6,
            TextureMarginTop = rounded ? 10 : 6,
            TextureMarginBottom = rounded ? 10 : 6,
            ContentMarginLeft = narrow ? 3 : 10,
            ContentMarginRight = narrow ? 3 : 10,
            ContentMarginTop = iconOnly ? (state == 2 ? 9 : 7) : (state == 2 ? 7 : 6),
            ContentMarginBottom = iconOnly ? (state == 2 ? 5 : 7) : (state == 2 ? 5 : 8)
        };
        Buttons[key] = style;
        return style;
    }

    /// <summary>
    /// Architectural left-rail navigation tile: dark obsidian-slate face, subtle 1px structural
    /// perimeter, and a bold 3px luminous gold-amber indicator bar on the left edge when active.
    /// </summary>
    internal static StyleBox NavTile(bool active, int state)
    {
        var key = $"nav|{active}|{state}";
        if (SpecialButtons.TryGetValue(key, out var cached)) return cached;
        var baseBg = Ui.Colour(active ? "surface.raised" : state == 1 ? "button.fill_hover" : "surface.inset");
        var accent = Ui.Colour("text.accent");
        var border = Ui.Colour(active ? "button.primary_border" : state == 1 ? "button.border_hover" : "surface.line_faint");
        if (active) baseBg = baseBg.Lerp(Ui.Colour("button.primary_fill"), state == 1 ? 0.42f : 0.30f);
        else if (state == 2) baseBg = Ui.Colour("button.fill_pressed");
        else if (state == 0) baseBg = baseBg.Lightened(0.02f);

        var style = new StyleBoxFlat
        {
            BgColor = baseBg,
            BorderColor = active ? accent : (state == 1 ? border.Lerp(accent, 0.45f) : border),
            BorderWidthLeft = active ? 3 : (state == 1 ? 2 : 1),
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ShadowColor = new Color(0, 0, 0, active ? 0.42f : 0.22f),
            ShadowSize = active ? 4 : 2,
            ShadowOffset = new Vector2(0, 1),
            ContentMarginLeft = 4,
            ContentMarginRight = 4,
            ContentMarginTop = state == 2 ? 6 : 5,
            ContentMarginBottom = state == 2 ? 4 : 5
        };
        style.SetCornerRadiusAll(4);
        SpecialButtons[key] = style;
        return style;
    }

    /// <summary>
    /// Segmented tab button for dossiers, country cards, and workspaces: quiet recessed pill when
    /// inactive, elevated slate card with a 2px gold-amber top bar when active.
    /// </summary>
    internal static StyleBox TabPill(bool active, int state)
    {
        var key = $"tab|{active}|{state}";
        if (SpecialButtons.TryGetValue(key, out var cached)) return cached;
        var accent = Ui.Colour("text.accent");
        // An active tab is filled, not only outlined: a gold hairline on the same dark ground as
        // its neighbours could not be told from them at a glance (CLOUDS on beside FOG off).
        var fill = active
            ? Ui.Colour("surface.raised").Lerp(Ui.Colour("button.primary_fill"), state == 1 ? 0.72f : 0.58f)
            : state == 1
                ? Ui.Colour("button.fill_hover")
                : Ui.Colour("surface.inset").Lightened(0.015f);
        var style = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = active ? accent : Ui.Colour(state == 1 ? "button.border_hover" : "surface.line_faint"),
            BorderWidthTop = active ? 2 : 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ShadowColor = new Color(0, 0, 0, active ? 0.35f : 0f),
            ShadowSize = active ? 3 : 0,
            ShadowOffset = new Vector2(0, 1),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 5,
            ContentMarginBottom = 5
        };
        style.SetCornerRadiusAll(3);
        SpecialButtons[key] = style;
        return style;
    }

    /// <summary>
    /// Section header bar inside the right-hand Agenda / Outliner rail: tracked uppercase header
    /// with a crisp bottom rule and subtle elevated slate background.
    /// </summary>
    internal static StyleBox OutlinerGroupBar(int state)
    {
        var key = $"outliner_group|{state}";
        if (SpecialButtons.TryGetValue(key, out var cached)) return cached;
        var fill = state == 1
            ? Ui.Colour("surface.raised").Lightened(0.04f)
            : Ui.Colour("surface.inset").Lerp(Ui.Colour("surface.raised"), 0.45f);
        var style = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = state == 1 ? Ui.Colour("text.accent_dim") : Ui.Colour("surface.line"),
            BorderWidthBottom = 1,
            BorderWidthTop = 1,
            BorderWidthLeft = 2,
            BorderWidthRight = 0,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        style.SetCornerRadiusAll(2);
        SpecialButtons[key] = style;
        return style;
    }

    /// <summary>
    /// Structured Agenda Card row inside the right-hand Outliner: recessed/elevated dark slate card
    /// with a 3px colored status bar on the left edge.
    /// </summary>
    internal static StyleBox OutlinerCardRow(string stripeToken, int state)
    {
        if (string.IsNullOrEmpty(stripeToken) || !InterfaceSkin.Has(stripeToken))
            stripeToken = "button.primary_border";
        var key = $"outliner_row|{stripeToken}|{state}";
        if (SpecialButtons.TryGetValue(key, out var cached)) return cached;
        var stripe = Ui.Colour(stripeToken);
        var fill = state switch
        {
            1 => Ui.Colour("button.fill_hover").Lerp(stripe, 0.08f),
            2 => Ui.Colour("button.fill_pressed"),
            _ => Ui.Colour("shell.row").Lerp(Ui.Colour("surface.raised"), 0.35f)
        };
        var style = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = state == 1 ? stripe.Lightened(0.15f) : stripe,
            BorderWidthLeft = 3,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ShadowColor = new Color(0, 0, 0, 0.26f),
            ShadowSize = 3,
            ShadowOffset = new Vector2(0, 1),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 5,
            ContentMarginBottom = 5
        };
        style.SetCornerRadiusAll(3);
        SpecialButtons[key] = style;
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

    private static readonly Texture2D[] _lamps = new Texture2D[2];

    /// <summary>
    /// The lamp of a switch: a lit gold dot when on, an empty ring when off. Worn by every
    /// toggle-mode tab, so on and off read the same way on the map bar, in settings and in
    /// any screen - the state is in the shape, not only in a shade of gold.
    /// </summary>
    internal static Texture2D ToggleLamp(bool on)
    {
        if (_lamps[on ? 1 : 0] is { } cached) return cached;
        const int size = 28;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        image.Fill(Colors.Transparent);
        var lit = Ui.Colour("text.accent");
        var dim = Ui.Colour("text.muted");
        var c = (size - 1) * 0.5f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
            Color pixel;
            if (on)
            {
                var core = Mathf.Clamp(7.5f - d, 0f, 1f);
                var glow = Mathf.Clamp(1f - (d - 7f) / 6f, 0f, 1f) * 0.35f;
                pixel = new Color(lit.R, lit.G, lit.B, MathF.Max(core, glow * glow));
            }
            else
            {
                var ring = Mathf.Clamp(1.2f - MathF.Abs(d - 6.5f), 0f, 1f);
                pixel = new Color(dim.R, dim.G, dim.B, ring * 0.9f);
            }
            image.SetPixel(x, y, pixel);
        }
        return _lamps[on ? 1 : 0] = ImageTexture.CreateFromImage(image);
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
        // A ground that is not in the palette would come back magenta, and a panel ground is the
        // whole screen: a misspelt surface takes the budget and the state-affairs rooms with it.
        // Say so once and keep a surface the screen can be read on.
        if (!InterfaceSkin.Has(ground))
        {
            var fallback = level == Level.Inset ? "surface.inset" : "shell.background";
            GD.PushWarning($"Unknown surface token '{ground}' on a panel ground; using '{fallback}'.");
            ground = fallback;
        }
        if (panel.GetNodeOrNull<AtlasPanelFinish>("AtlasMaterial") is { } existing)
        {
            existing.SurfaceLevel = level;
            existing.Ground = ground;
            existing.QueueRedraw();
            return existing;
        }
        var finish = new AtlasPanelFinish { Name = "AtlasMaterial", SurfaceLevel = level, Ground = ground };
        panel.AddChild(finish);
        panel.MoveChild(finish, 0);
        return finish;
    }

    internal static Texture2D PanelTexture(Level level, string ground)
    {
        var key = level + "|" + ground;
        if (Panels.TryGetValue(key, out var cached)) return cached;
        var fill = Ui.Colour(ground);
        var deep = Ui.Colour("surface.background");
        // Blend blue-tinted legacy shell grounds slightly toward deep obsidian-slate so panels
        // have a grounded, modern dark-graphite authority without losing their skin token.
        if (level is Level.Instrument or Level.Document)
            fill = fill.Lerp(deep, 0.28f);
        const int size = 96;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var relief = Relief;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var u = x / (float)(size - 1);
            var v = y / (float)(size - 1);
            // Primarily vertical studio illumination so stretching to wide strips or tall rails
            // never creates a diagonal smudge.
            var vertical = level == Level.Inset ? (v - 0.35f) * 0.08f : (0.35f - v) * 0.09f;
            var subtleCorner = AtlasSurfaceLighting.Light(u, v, level == Level.Inset) * 0.22f;
            var illumination = (vertical + subtleCorner) * relief;
            var colour = illumination >= 0 ? fill.Lightened(illumination * .55f) : fill.Darkened(-illumination);
            if (level == Level.Raised) colour = colour.Lightened(.022f * relief);
            image.SetPixel(x, y, colour);
        }
        return Panels[key] = ImageTexture.CreateFromImage(image);
    }

    internal static Texture2D GrainTexture()
    {
        if (_grain is not null) return _grain;
        const int size = 64;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var amount = AtlasSurfaceLighting.GrainAmount(Ui.Metric("atlas.grain", .018f)) * .32f;
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
        if (!InterfaceSkin.Has(ground)) ground = "surface.inset";
        var isRaised = ground is "surface.raised" or "war.card";
        var baseFill = Ui.Colour(ground);
        if (!isRaised) baseFill = baseFill.Lerp(Ui.Colour("surface.background"), 0.22f);
        var style = new StyleBoxFlat
        {
            BgColor = baseFill,
            BorderColor = Ui.Colour(isRaised ? "surface.line" : "surface.line_faint"),
            ShadowColor = Ui.Colour("surface.background", isRaised ? .48f : .34f),
            ShadowSize = isRaised ? 6 : 4,
            ShadowOffset = new(0, 2)
        };
        style.SetBorderWidthAll(1);
        style.SetContentMarginAll(10);
        style.SetCornerRadiusAll(4);
        return style;
    }
}

/// <summary>Non-interactive material underneath content. No process loop, blur, shader or world state.</summary>
public partial class AtlasPanelFinish : Node2D
{
    internal AtlasSurfaceSkin.Level SurfaceLevel;
    internal string Ground = "shell.background";
    private PanelContainer _panel;
    // A Control child is laid out INSIDE a PanelContainer's content margins. The
    // structural frame then runs straight through its labels. Node2D paints the full
    // panel without participating in content layout or intercepting GUI input.
    public override void _EnterTree()
    {
        _panel = GetParent() as PanelContainer;
        InterfaceSkin.Changed += RedrawMaterial;
        if (_panel is not null) _panel.Resized += QueueRedraw;
    }
    public override void _ExitTree()
    {
        InterfaceSkin.Changed -= RedrawMaterial;
        if (GodotObject.IsInstanceValid(_panel)) _panel.Resized -= QueueRedraw;
    }
    private void RedrawMaterial()
    {
        if (GetParent() is PanelContainer panel && panel.HasMeta("atlas_section_ground"))
            panel.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section(panel.GetMeta("atlas_section_ground").AsString()));
        QueueRedraw();
    }
    public override void _Draw()
    {
        var size = _panel?.Size ?? Vector2.Zero;
        if (size.X <= 0 || size.Y <= 0) return;
        var w = size.X;
        var rect = new Rect2(Vector2.Zero, size);
        DrawTextureRect(AtlasSurfaceSkin.PanelTexture(SurfaceLevel, Ground), rect, false);
        DrawTextureRect(AtlasSurfaceSkin.GrainTexture(), rect, true);

        var relief = AtlasSurfaceSkin.Relief;
        var isInset = SurfaceLevel == AtlasSurfaceSkin.Level.Inset;
        var isRaised = SurfaceLevel == AtlasSurfaceSkin.Level.Raised;
        var frame = new StyleBoxFlat
        {
            DrawCenter = false,
            BorderColor = Ui.Colour(isInset ? "surface.line" : "shell.border", isInset ? .58f : .82f),
            ShadowColor = Ui.Colour("surface.background", (isInset ? 0f : isRaised ? .42f : .30f) * relief),
            ShadowSize = isInset ? 0 : isRaised ? 6 : 4,
            ShadowOffset = new(0, isInset ? 0 : 2)
        };
        frame.SetBorderWidthAll(1);
        frame.SetCornerRadiusAll(4);
        frame.SetContentMarginAll(0);
        DrawStyleBox(frame, rect);

        if (w > 8f)
        {
            var topLight = isInset
                ? Ui.Colour("surface.background", .45f * relief)
                : Ui.Colour("text.body", .09f * relief);
            DrawLine(new(4f, 1.5f), new(w - 4f, 1.5f), topLight, 1f);
        }
    }
}
