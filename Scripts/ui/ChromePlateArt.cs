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
    private static bool _hooked;

    private static void EnsureHooks()
    {
        if (_hooked) return;
        _hooked = true;
        SpriteCatalog.Changed += Clear;
        InterfaceSkin.Changed += Clear;
    }

    internal static void Clear() => Cache.Clear();

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
