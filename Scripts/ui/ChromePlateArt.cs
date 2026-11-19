using System;
using System.Collections.Generic;
using System.IO;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Shared loose-asset texture loader and architectural plate overlays (slate weave and header
/// frieze) for unified menu and campaign dossier chrome.
/// </summary>
internal static class ChromePlateArt
{
    private static readonly Dictionary<string, Texture2D> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, StyleBox> StyleCache = new(StringComparer.Ordinal);
    private static bool _hooked;

    private static void EnsureHooks()
    {
        if (_hooked) return;
        _hooked = true;
        SpriteCatalog.Changed += Clear;
        InterfaceSkin.Changed += Clear;
    }

    internal static void Clear()
    {
        Cache.Clear();
        StyleCache.Clear();
    }

    /// <summary>Loads an imported sprite or falls back to direct disk loading for loose res:// assets.</summary>
    internal static Texture2D LoadSpriteTexture(string sprite, string fallbackPath)
    {
        EnsureHooks();
        var path = SpriteCatalog.TextureOf(sprite, fallbackPath);
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Cache.TryGetValue(path, out var cached) && GodotObject.IsInstanceValid(cached))
            return cached;

        Texture2D loaded = null;
        if (ResourceLoader.Exists(path))
        {
            try { loaded = GD.Load<Texture2D>(path); }
            catch { loaded = null; }
        }
        if (loaded is null)
        {
            try
            {
                var disk = path.StartsWith("res://", StringComparison.Ordinal) ||
                           path.StartsWith("user://", StringComparison.Ordinal)
                    ? ProjectSettings.GlobalizePath(path)
                    : path;
                if (File.Exists(disk))
                {
                    using var img = Image.LoadFromFile(disk);
                    if (img is not null && !img.IsEmpty())
                        loaded = ImageTexture.CreateFromImage(img);
                }
            }
            catch { loaded = null; }
        }
        if (loaded is not null)
            Cache[path] = loaded;
        return loaded;
    }

    /// <summary>Attaches or updates the subtle tiled slate weave overlay behind plate contents.</summary>
    internal static void AttachPlateWeave(Control host, float alpha = 0.08f) =>
        AttachOverlayTexture(
            host,
            "PlateWeaveOverlay",
            "shell_plate_weave",
            "res://game/gfx/interface/shell/plate_slate_weave.png",
            TextureRect.StretchModeEnum.Tile,
            alpha);

    /// <summary>Attaches or updates the architectural masthead frieze behind a screen header.</summary>
    internal static void AttachHeaderFrieze(Control header, float alpha = 0.28f) =>
        AttachOverlayTexture(
            header,
            "ScreenHeaderFrieze",
            "shell_header_frieze",
            "res://game/gfx/interface/shell/header_frieze_band.png",
            TextureRect.StretchModeEnum.KeepAspectCovered,
            alpha);

    /// <summary>
    /// Attaches subtle sculpted pewter-brass L-bracket ornaments to all four corners of a plate
    /// or plate header using <c>shell.ornament_alpha</c> and <c>shell.plate_bevel_px</c>.
    /// </summary>
    internal static void AttachCornerOrnaments(Control host, float alpha = -1f, float cornerSize = 22f)
    {
        if (host is null) return;
        var effectiveAlpha = alpha >= 0f
            ? Math.Clamp(alpha, 0f, 1f)
            : Math.Clamp(InterfaceSkin.Metric("shell.ornament_alpha", 0.62f), 0f, 1f);
        var tex = LoadSpriteTexture("shell_corner_ornament", "res://game/gfx/interface/shell/corner_ornament_brass.png");
        var overlay = host.GetNodeOrNull<Control>("CornerOrnamentsOverlay");
        if (tex is null || effectiveAlpha <= 0.005f)
        {
            if (overlay is not null) overlay.Visible = false;
            return;
        }
        if (overlay is null)
        {
            overlay = new Control
            {
                Name = "CornerOrnamentsOverlay",
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ZIndex = 3
            };
            overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            host.AddChild(overlay);
        }

        var inset = Math.Clamp(InterfaceSkin.Metric("shell.plate_bevel_px", 2.0f), 0f, 6f);
        var sz = Math.Clamp(cornerSize, 12f, 36f);
        PlaceCorner(overlay, "CornerTL", tex, false, false, 0f, 0f, inset, inset, inset + sz, inset + sz);
        PlaceCorner(overlay, "CornerTR", tex, true, false, 1f, 0f, -inset - sz, inset, -inset, inset + sz);
        PlaceCorner(overlay, "CornerBL", tex, false, true, 0f, 1f, inset, -inset - sz, inset + sz, -inset);
        PlaceCorner(overlay, "CornerBR", tex, true, true, 1f, 1f, -inset - sz, -inset - sz, -inset, -inset);
        overlay.Modulate = new Color(1f, 1f, 1f, effectiveAlpha);
        overlay.Visible = true;
    }

    private static void PlaceCorner(
        Control parent, string name, Texture2D tex, bool flipH, bool flipV,
        float ax, float ay, float left, float top, float right, float bottom)
    {
        var rect = parent.GetNodeOrNull<TextureRect>(name);
        if (rect is null)
        {
            rect = new TextureRect
            {
                Name = name,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            parent.AddChild(rect);
        }
        rect.Texture = tex;
        rect.FlipH = flipH;
        rect.FlipV = flipV;
        rect.AnchorLeft = ax;
        rect.AnchorTop = ay;
        rect.AnchorRight = ax;
        rect.AnchorBottom = ay;
        rect.OffsetLeft = left;
        rect.OffsetTop = top;
        rect.OffsetRight = right;
        rect.OffsetBottom = bottom;
    }

    /// <summary>
    /// Creates a non-interactive engraved divider featuring the centered pewter medallion sprite
    /// (<c>shell_divider_medallion</c>) at <c>shell.divider_alpha</c> with seamless hairline wings.
    /// </summary>
    internal static Control CreateEngravedMedallionDivider(
        string name = "EngravedRule", float verticalMargin = 2f, float height = 16f)
    {
        var alpha = Math.Clamp(InterfaceSkin.Metric("shell.divider_alpha", 0.78f), 0f, 1f);
        var tex = LoadSpriteTexture("shell_divider_medallion", "res://game/gfx/interface/shell/divider_engraved_medallion.png");
        if (tex is null || alpha <= 0.005f)
            return MenuChromeTheme.CreateHairlineRule(name, verticalMargin);

        var divider = new Control
        {
            Name = name,
            CustomMinimumSize = new Vector2(0f, height + verticalMargin * 2f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        divider.SetMeta("rule_pad", verticalMargin);
        divider.Resized += divider.QueueRedraw;
        divider.Draw += () =>
        {
            var w = divider.Size.X;
            var h = divider.Size.Y;
            if (w < 8f) return;
            var medW = MathF.Min(w, height * (256f / 24f));
            if (w <= medW + 8f) return;
            var leftEnd = (w - medW) * 0.5f + 10f;
            var rightStart = (w + medW) * 0.5f - 10f;
            var y = MathF.Floor(h * 0.5f);
            var dark = new Color(MenuChromeTheme.RuleDark, alpha * 0.65f);
            var light = new Color(MenuChromeTheme.RuleLight, alpha * 0.62f);
            divider.DrawLine(new Vector2(0f, y - 0.5f), new Vector2(leftEnd, y - 0.5f), dark, 1f);
            divider.DrawLine(new Vector2(0f, y + 0.5f), new Vector2(leftEnd, y + 0.5f), light, 1f);
            divider.DrawLine(new Vector2(rightStart, y - 0.5f), new Vector2(w, y - 0.5f), dark, 1f);
            divider.DrawLine(new Vector2(rightStart, y + 0.5f), new Vector2(w, y + 0.5f), light, 1f);
        };

        var sprite = new TextureRect
        {
            Name = "MedallionSprite",
            Texture = tex,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
            Modulate = new Color(1f, 1f, 1f, alpha),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        sprite.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        sprite.OffsetTop = verticalMargin;
        sprite.OffsetBottom = -verticalMargin;
        divider.AddChild(sprite);
        return divider;
    }

    /// <summary>
    /// Builds a cached 9-slice <see cref="StyleBoxTexture"/> over <c>shell_button_bevel</c>
    /// when <c>shell.button_texture_enabled &gt;= 0.5</c>; returns null so callers can fall back
    /// cleanly to procedural plates when disabled or missing.
    /// </summary>
    internal static StyleBox BuildSculptedButtonStyle(
        int state, bool primary = false,
        int padLeft = 12, int padTop = 5, int padRight = 12, int padBottom = 5)
    {
        EnsureHooks();
        if (InterfaceSkin.Metric("shell.button_texture_enabled", 1.0f) < 0.5f) return null;
        var path = SpriteCatalog.TextureOf("shell_button_bevel", "res://game/gfx/interface/shell/button_bevel_sheen.png");
        var tex = LoadSpriteTexture("shell_button_bevel", "res://game/gfx/interface/shell/button_bevel_sheen.png");
        if (tex is null) return null;

        var bevelPx = Math.Clamp(InterfaceSkin.Metric("shell.plate_bevel_px", 2.0f), 1f, 6f);
        var slice = MathF.Round(6f + bevelPx);
        var key = $"btn|{path}|{state}|{primary}|{padLeft},{padTop},{padRight},{padBottom}|{slice:0.#}";
        if (StyleCache.TryGetValue(key, out var cached)) return cached;

        var tint = state switch
        {
            3 => new Color(0.56f, 0.55f, 0.53f, 0.72f),
            2 => primary ? new Color(0.76f, 0.75f, 0.72f, 1f) : new Color(0.68f, 0.67f, 0.65f, 1f),
            1 => primary ? new Color(1.26f, 1.24f, 1.20f, 1f) : new Color(1.14f, 1.12f, 1.09f, 1f),
            _ => primary ? new Color(1.12f, 1.10f, 1.06f, 1f) : new Color(0.90f, 0.89f, 0.87f, 1f)
        };
        var box = new StyleBoxTexture
        {
            Texture = tex,
            ModulateColor = tint,
            TextureMarginLeft = slice,
            TextureMarginTop = slice,
            TextureMarginRight = slice,
            TextureMarginBottom = slice,
            ContentMarginLeft = padLeft,
            ContentMarginTop = padTop,
            ContentMarginRight = padRight,
            ContentMarginBottom = padBottom
        };
        return StyleCache[key] = box;
    }

    private static void AttachOverlayTexture(
        Control host,
        string nodeName,
        string sprite,
        string fallbackPath,
        TextureRect.StretchModeEnum stretchMode,
        float alpha)
    {
        if (host is null) return;
        var tex = LoadSpriteTexture(sprite, fallbackPath);
        var existing = host.GetNodeOrNull<TextureRect>(nodeName);
        if (tex is null)
        {
            if (existing is not null) existing.Visible = false;
            return;
        }
        if (existing is null)
        {
            existing = new TextureRect
            {
                Name = nodeName,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = stretchMode,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            existing.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            host.AddChild(existing);
        }
        existing.Texture = tex;
        existing.Modulate = new Color(1f, 1f, 1f, alpha);
        existing.Visible = true;
        host.MoveChild(existing, 0);
    }
}
