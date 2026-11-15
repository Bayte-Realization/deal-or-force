using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;
using SurfaceState = DealOrForce.Simulation.InterfaceMaterials.SurfaceState;

namespace DealOrForce;

/// <summary>
/// The one place a material (<see cref="InterfaceMaterials"/>) becomes a StyleBox.
///
/// A material with a <c>texture</c> is that picture as a nine-slice with the margins from data.
/// Every other material is baked once on the CPU into a small square - gradient, engraved line,
/// bevel, grain, cut corners, inner shadow, and the drop shadow around it, which the StyleBox
/// hangs outside the panel with its expand margins - and cached. One draw call per panel, no
/// shader, no per-frame work, so it costs the same on the Compatibility renderer and a phone.
///
/// The cache has its own ceiling (<see cref="InterfaceMaterials.MaxBakedTextures"/>,
/// <see cref="InterfaceMaterials.MaxBakedBytes"/>), separate from the button faces in
/// <see cref="InstrumentMaterials"/>, so flag colours filling that cache cannot push the plates
/// out. Past the ceiling a material falls back to a flat box in its own colours rather than to
/// nothing. Everything is dropped when the skin reloads.
/// </summary>
internal static class MaterialStyles
{
    /// <summary>Set on a panel drawn in a material; the code-built frames underneath stand aside.</summary>
    internal const string MaterialMeta = "skin_material";
    private const string OriginalMeta = "skin_material_original";
    // Stamped on every StyleBox this class builds. The cache below is emptied on each skin
    // reload, so asking the cache "is this ours?" forgets yesterday's plates; the stamp travels
    // with the box itself and a surface from before a reload is never mistaken for a panel's
    // own code style.
    private const string SurfaceMeta = "skin_material_surface";

    private readonly record struct Key(string Role, SurfaceState State);
    private static readonly Dictionary<Key, StyleBox> Boxes = new();
    private static readonly Dictionary<Key, Texture2D> Textures = new();
    private static readonly Dictionary<(Color Tint, float Relief), ShaderMaterial> Reliefs = new();
    private static long _bytes;

    internal static int TextureCount => Textures.Count;
    internal static long TextureBytes => _bytes;

    static MaterialStyles() => InterfaceSkin.Changed += () =>
    {
        Boxes.Clear(); Textures.Clear(); Reliefs.Clear(); _bytes = 0;
    };

    /// <summary>A role's surface in one state. Unknown roles draw as <c>plate</c>.</summary>
    internal static StyleBox Surface(string role, SurfaceState state = SurfaceState.Normal)
    {
        var material = InterfaceMaterials.Resolve(role);
        var key = new Key(material.Role, state);
        if (Boxes.TryGetValue(key, out var cached)) return cached;
        var box = Build(material, state, key);
        box.SetMeta(SurfaceMeta, true);
        return Boxes[key] = box;
    }

    private static StyleBox Build(InterfaceMaterials.Material material, SurfaceState state, Key key)
    {
        var content = material.ContentMargin;
        if (material.Texture.Length > 0)
        {
            var path = SpriteCatalog.TextureOf(material.Texture, material.Texture);
            if (ResourceLoader.Exists(path) && GD.Load<Texture2D>(path) is { } picture)
            {
                var margins = material.TextureMargin;
                return new StyleBoxTexture
                {
                    Texture = picture,
                    TextureMarginLeft = margins[0], TextureMarginTop = margins[1],
                    TextureMarginRight = margins[2], TextureMarginBottom = margins[3],
                    ContentMarginLeft = content[0], ContentMarginTop = content[1],
                    ContentMarginRight = content[2], ContentMarginBottom = content[3]
                };
            }
            GD.PushWarning($"Material '{material.Role}' names texture '{material.Texture}', which does not load; drawing it in code.");
        }

        var bytes = InterfaceMaterials.BakedBytes(material);
        if (Textures.Count >= InterfaceMaterials.MaxBakedTextures || _bytes + bytes > InterfaceMaterials.MaxBakedBytes)
            return Flat(material, content);

        var pixels = InterfaceMaterials.Bake(material, state, out var size, out var pad);
        var texture = ImageTexture.CreateFromImage(Image.CreateFromData(size, size, false, Image.Format.Rgba8, pixels));
        Textures[key] = texture;
        _bytes += bytes;
        var margin = InterfaceMaterials.BakedMargin(material);
        return new StyleBoxTexture
        {
            Texture = texture,
            TextureMarginLeft = margin, TextureMarginTop = margin,
            TextureMarginRight = margin, TextureMarginBottom = margin,
            // The shadow lives in the texture's outer ring and hangs outside the panel, so the
            // panel's own rectangle - and every hit target inside it - is exactly as laid out.
            ExpandMarginLeft = pad, ExpandMarginTop = pad, ExpandMarginRight = pad, ExpandMarginBottom = pad,
            ContentMarginLeft = content[0], ContentMarginTop = content[1],
            ContentMarginRight = content[2], ContentMarginBottom = content[3]
        };
    }

    private static StyleBoxFlat Flat(InterfaceMaterials.Material material, IReadOnlyList<int> content)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(InterfaceMaterials.ColourOf(material.Bottom, "#1c1916")),
            BorderColor = new Color(InterfaceMaterials.ColourOf(material.Border, "#0e0c0a")),
            ContentMarginLeft = content[0], ContentMarginTop = content[1],
            ContentMarginRight = content[2], ContentMarginBottom = content[3]
        };
        style.SetBorderWidthAll(material.BorderWidth);
        style.SetCornerRadiusAll(material.CornerRadius);
        return style;
    }

    /// <summary>
    /// Dress a placed panel in the material its layout key names, or give it back its own style
    /// when the key names none (or materials are switched off). Called from <see cref="Ui.Place"/>
    /// on every placement, so a panel styled in its constructor and placed afterwards still ends
    /// up in its material, and a skin reload that turns the switch off undoes it.
    /// </summary>
    internal static void ApplyLayout(Control control, string key)
    {
        if (control is not (PanelContainer or Panel)) return;
        var role = InterfaceSkin.LayoutMaterial(key);
        if (role is null)
        {
            if (!control.HasMeta(MaterialMeta)) return;
            control.RemoveMeta(MaterialMeta);
            if (control.HasMeta(OriginalMeta) && control.GetMeta(OriginalMeta).AsGodotObject() is StyleBox original)
                control.AddThemeStyleboxOverride("panel", original);
            else control.RemoveThemeStyleboxOverride("panel");
            if (control.HasMeta(OriginalMeta)) control.RemoveMeta(OriginalMeta);
            Redraw(control);
            return;
        }

        var surface = Surface(role);
        var current = control.HasThemeStyleboxOverride("panel") ? control.GetThemeStylebox("panel") : null;
        if (ReferenceEquals(current, surface) && control.HasMeta(MaterialMeta)) return;
        // Whatever the panel was dressed in by its own code is kept, so switching off gives it back.
        if (current is not null && !IsOurs(current)) control.SetMeta(OriginalMeta, current);
        control.AddThemeStyleboxOverride("panel", surface);
        control.SetMeta(MaterialMeta, role);
        Redraw(control);
    }

    private static bool IsOurs(StyleBox box) => box.HasMeta(SurfaceMeta);

    private static void Redraw(Control control)
    {
        control.QueueRedraw();
        if (control.GetNodeOrNull<CanvasItem>("AtlasMaterial") is { } finish) finish.QueueRedraw();
    }

    /// <summary>
    /// The relief shader (game/gfx/interface/shaders/ui_relief.gdshader, replaceable by a mod as
    /// `ui_relief`) with the shared light and one tone: an icon's family colour, a flag shield, a
    /// round button. One material per tone and depth, shared by everything wearing both: a flat
    /// button and a full-relief icon of the same colour must not hand each other their depth.
    /// </summary>
    internal static ShaderMaterial Relief(Color tint, float relief = 1f)
    {
        relief = float.IsNaN(relief) ? 1f : Math.Clamp(relief, 0f, 1f);
        if (Reliefs.TryGetValue((tint, relief), out var cached)) return cached;
        var path = SpriteCatalog.ShaderOf("ui_relief", "res://game/gfx/interface/shaders/ui_relief.gdshader");
        if (!ResourceLoader.Exists(path) || GD.Load<Shader>(path) is not { } shader) return null;
        var light = InterfaceMaterials.Lighting;
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("light_dir", new Vector2(light.DirectionX, light.DirectionY));
        material.SetShaderParameter("relief", relief);
        material.SetShaderParameter("tint", tint);
        return Reliefs[(tint, relief)] = material;
    }

    /// <summary>The family tone of an icon role (campaign.icon_categories), or white when it has none.</summary>
    internal static Color IconTone(string role) =>
        CampaignUiCatalog.IconCategoryToken(role) is { Length: > 0 } token && InterfaceSkin.Has(token)
            ? Ui.Colour(token)
            : Colors.White;
}
