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

    /// <summary>Attaches or updates a seamlessly tiled matte surface texture across a panel or menu.</summary>
    internal static void AttachSurfaceTexture(Control host, string surfaceKind = "slate", float alpha = -1f)
    {
        if (host is null) return;
        var (spriteId, fallback) = surfaceKind switch
        {
            "linen" or "grain" => ("shell_dossier_grain", "res://game/gfx/interface/panels/dossier_body_grain.png"),
            "blueprint" or "grid" => ("shell_surface_blueprint", "res://game/gfx/interface/panels/surface_blueprint_grid.png"),
            "laid_paper" or "paper" => ("shell_surface_laid_paper", "res://game/gfx/interface/panels/surface_laid_paper.png"),
            "leather" or "desk" => ("shell_surface_leather", "res://game/gfx/interface/panels/surface_command_leather.png"),
            "sidebar" => ("shell_menu_sidebar_art", "res://game/gfx/interface/panels/main_menu_sidebar_art.png"),
            _ => ("shell_surface_slate", "res://game/gfx/interface/panels/surface_slate_twill.png")
        };
        var metricKey = surfaceKind switch
        {
            "linen" or "grain" => "shell.dossier_grain_alpha",
            "sidebar" => "shell.menu_sidebar_art_alpha",
            _ => "shell.surface_texture_alpha"
        };
        var effectiveAlpha = alpha >= 0f
            ? Math.Clamp(alpha, 0f, 1f)
            : Math.Clamp(InterfaceSkin.Metric(metricKey, 0.32f), 0f, 1f);

        var tex = LoadSpriteTexture(spriteId, fallback);
        if (host.GetNodeOrNull<TextureRect>("PlateWeaveOverlay") is { } oldWeave) oldWeave.Visible = false;
        if (host.GetNodeOrNull<TextureRect>("DossierGrainOverlay") is { } oldGrain) oldGrain.Visible = false;

        var overlay = host.GetNodeOrNull<ChromeSurfaceOverlay>("SurfaceTextureOverlay");
        if (overlay is null)
        {
            overlay = new ChromeSurfaceOverlay { Name = "SurfaceTextureOverlay", ZIndex = 0 };
            host.AddChild(overlay);
            host.MoveChild(overlay, 0);
        }
        overlay.Configure(tex, effectiveAlpha);
    }

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
        var path = SpriteCatalog.TextureOf("shell_button_bevel", "res://game/gfx/interface/panels/button_bevel_uniform_9s.png");
        var tex = LoadSpriteTexture("shell_button_bevel", "res://game/gfx/interface/panels/button_bevel_uniform_9s.png");
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

    /// <summary>
    /// Builds a cached 9-slice card wrapper style (<c>shell_card_wrapper</c>) with clean procedural fallback.
    /// </summary>
    internal static StyleBox BuildCardWrapperStyle(
        int padLeft = 8, int padTop = 6, int padRight = 8, int padBottom = 6, float alpha = -1f)
    {
        EnsureHooks();
        if (InterfaceSkin.Metric("shell.card_wrapper_enabled", 1.0f) < 0.5f) return null;
        var effectiveAlpha = alpha >= 0f ? Math.Clamp(alpha, 0f, 1f) : 1f;
        var path = SpriteCatalog.TextureOf("shell_card_wrapper", "res://game/gfx/interface/shell/newspaper_card_9s.png");
        var tex = LoadSpriteTexture("shell_card_wrapper", "res://game/gfx/interface/shell/newspaper_card_9s.png");
        if (tex is null) return null;

        var key = $"card|{path}|{padLeft},{padTop},{padRight},{padBottom}|{effectiveAlpha:0.##}";
        if (StyleCache.TryGetValue(key, out var cached)) return cached;

        var box = new StyleBoxTexture
        {
            Texture = tex,
            ModulateColor = new Color(1f, 1f, 1f, effectiveAlpha),
            TextureMarginLeft = 14,
            TextureMarginTop = 14,
            TextureMarginRight = 14,
            TextureMarginBottom = 14,
            ContentMarginLeft = padLeft,
            ContentMarginTop = padTop,
            ContentMarginRight = padRight,
            ContentMarginBottom = padBottom
        };
        return StyleCache[key] = box;
    }

    /// <summary>
    /// Builds a cached tab strip container style (<c>shell_tab_strip</c>) with procedural fallback.
    /// </summary>
    internal static StyleBox BuildTabStripStyle(
        int padLeft = 6, int padTop = 3, int padRight = 6, int padBottom = 3, float alpha = -1f)
    {
        EnsureHooks();
        var effectiveAlpha = alpha >= 0f
            ? Math.Clamp(alpha, 0f, 1f)
            : Math.Clamp(InterfaceSkin.Metric("shell.tab_strip_alpha", 0.85f), 0f, 1f);
        var path = SpriteCatalog.TextureOf("shell_tab_strip", "res://game/gfx/interface/shell/tab_folder_9s.png");
        var tex = LoadSpriteTexture("shell_tab_strip", "res://game/gfx/interface/shell/tab_folder_9s.png");
        if (tex is null || effectiveAlpha <= 0.005f) return null;

        var key = $"tabstrip|{path}|{padLeft},{padTop},{padRight},{padBottom}|{effectiveAlpha:0.##}";
        if (StyleCache.TryGetValue(key, out var cached)) return cached;

        var box = new StyleBoxTexture
        {
            Texture = tex,
            ModulateColor = new Color(1f, 1f, 1f, effectiveAlpha),
            TextureMarginLeft = 8,
            TextureMarginTop = 6,
            TextureMarginRight = 8,
            TextureMarginBottom = 6,
            ContentMarginLeft = padLeft,
            ContentMarginTop = padTop,
            ContentMarginRight = padRight,
            ContentMarginBottom = padBottom
        };
        return StyleCache[key] = box;
    }

    /// <summary>Attaches or updates the deep atmospheric modal backdrop behind fullscreen dialogs.</summary>
    internal static void AttachModalBackdrop(Control host, float alpha = -1f)
    {
        if (host is null) return;
        var effectiveAlpha = alpha >= 0f
            ? Math.Clamp(alpha, 0f, 1f)
            : Math.Clamp(InterfaceSkin.Metric("shell.modal_backdrop_alpha", 0.88f), 0f, 1f);
        AttachOverlayTexture(
            host,
            "ModalBackdropOverlay",
            "shell_modal_backdrop",
            "res://game/gfx/interface/panels/modal_cartography_backdrop.png",
            TextureRect.StretchModeEnum.KeepAspectCovered,
            effectiveAlpha);
    }

    /// <summary>Attaches or updates the symmetrical ornate filigree band behind headers.</summary>
    internal static void AttachHeaderFiligree(Control host, float alpha = -1f)
    {
        if (host is null) return;
        var effectiveAlpha = alpha >= 0f
            ? Math.Clamp(alpha, 0f, 1f)
            : Math.Clamp(InterfaceSkin.Metric("shell.header_filigree_alpha", 0.55f), 0f, 1f);
        AttachOverlayTexture(
            host,
            "HeaderFiligreeOverlay",
            "shell_header_filigree",
            "res://game/gfx/interface/panels/header_emblem_filigree.png",
            TextureRect.StretchModeEnum.KeepAspectCentered,
            effectiveAlpha);
    }

    /// <summary>
    /// Attaches or updates the ornate cameo bezel frame over a leader or ruler portrait.
    /// </summary>
    internal static void AttachPortraitBezel(Control host, float alpha = -1f)
    {
        if (host is null) return;
        var effectiveAlpha = alpha >= 0f
            ? Math.Clamp(alpha, 0f, 1f)
            : Math.Clamp(InterfaceSkin.Metric("shell.portrait_bezel_alpha", 0.92f), 0f, 1f);
        var tex = LoadSpriteTexture("shell_portrait_bezel", "res://game/gfx/interface/shell/round_bezel.png");
        var existing = host.GetNodeOrNull<TextureRect>("PortraitBezelOverlay");
        if (tex is null || effectiveAlpha <= 0.005f)
        {
            if (existing is not null) existing.Visible = false;
            return;
        }
        if (existing is null)
        {
            existing = new TextureRect
            {
                Name = "PortraitBezelOverlay",
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ZIndex = 5
            };
            existing.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            host.AddChild(existing);
        }
        existing.Texture = tex;
        existing.Modulate = new Color(1f, 1f, 1f, effectiveAlpha);
        existing.Visible = true;
        host.MoveChild(existing, host.GetChildCount() - 1);
    }

    /// <summary>
    /// Applies an icon to an outer menu action button when <c>shell.menu_icons_enabled &gt;= 0.5</c>.
    /// </summary>
    internal static void ApplyMenuButtonIcon(Button btn, string iconId, string fallbackPath = "")
    {
        if (btn is null || string.IsNullOrWhiteSpace(iconId)) return;
        if (InterfaceSkin.Metric("shell.menu_icons_enabled", 1.0f) < 0.5f) return;
        var fallback = string.IsNullOrEmpty(fallbackPath)
            ? $"res://game/gfx/interface/icons/{iconId}.png"
            : fallbackPath;
        var tex = LoadSpriteTexture(iconId, fallback);
        if (tex is not null)
        {
            btn.Icon = tex;
            btn.ExpandIcon = true;
            btn.AddThemeConstantOverride("icon_max_width", 20);
            btn.AddThemeConstantOverride("h_separation", 10);
        }
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
