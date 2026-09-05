using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>
    /// Flags, cached, because the interface asks for the same dozen every time it refreshes
    /// and loading a texture per frame is a texture load per frame.
    /// </summary>
    private static readonly Dictionary<string, Texture2D> FlagCache = new();

    /// <summary>
    /// A country's flag.
    ///
    /// The same rule as every other picture in the game: the file is the declaration.
    /// `gfx/flags/flag_fra.svg` IS the flag of France, so a mod that wants a different one
    /// drops its own `flag_fra.svg` into its own `gfx/flags/` and nothing else has to be
    /// written down anywhere. A country the base game has never heard of works the same way.
    ///
    /// They are vector files on purpose. A flag stored as a picture is the thing that looks
    /// like porridge the moment it is drawn larger than it was saved, which is what happens
    /// to every flag in every strategy game that ships them as small bitmaps; an SVG is
    /// exact at whatever size the panel asks for.
    /// </summary>
    private static Texture2D FlagTexture(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var key = tag.ToLowerInvariant();
        if (FlagCache.TryGetValue(key, out var cached)) return cached;

        var path = SpriteCatalog.TextureOf($"flag_{key}", $"res://game/gfx/flags/flag_{key}.svg");
        var texture = !string.IsNullOrWhiteSpace(path) && ResourceLoader.Exists(path)
            ? GD.Load<Texture2D>(path)
            : null;
        FlagCache[key] = texture;
        return texture;
    }

    /// <summary>Drop the cache when the sprite catalogue changes under it - a mod reload.</summary>
    private static void ForgetFlags() => FlagCache.Clear();

    /// <summary>
    /// A flag at a given height, in its own proportions.
    ///
    /// Every flag file is stored at the same height and its own width, so a Nepalese
    /// pennant and a Swiss square keep their shape instead of being squeezed into one
    /// rectangle. The frame is there for the flags that are mostly white: without it Japan
    /// dissolves into the panel behind it.
    /// </summary>
    private Control FlagBadge(string tag, float height, string tooltip = "")
    {
        var texture = FlagTexture(tag);
        var width = texture is null || texture.GetHeight() <= 0
            ? height * 1.5f
            : height * texture.GetWidth() / texture.GetHeight();

        var frame = new PanelContainer
        {
            CustomMinimumSize = new Vector2(Mathf.Round(width) + 2, Mathf.Round(height) + 2),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            TooltipText = tooltip
        };
        var border = new StyleBoxFlat
        {
            BgColor = Ui.Colour("surface.inset"),
            BorderColor = Ui.Colour("surface.line"),
            ContentMarginLeft = 1, ContentMarginRight = 1,
            ContentMarginTop = 1, ContentMarginBottom = 1
        };
        border.SetBorderWidthAll(1);
        frame.AddThemeStyleboxOverride("panel", border);

        var rect = new TextureRect
        {
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            CustomMinimumSize = new Vector2(Mathf.Round(width), Mathf.Round(height))
        };
        frame.AddChild(rect);
        return frame;
    }

    /// <summary>The side panel's flag: whoever it is about, or nothing when it is about a screen.</summary>
    private void ShowSideFlag(string tag)
    {
        if (_sideFlag is null) return;
        _sideFlag.Visible = !string.IsNullOrWhiteSpace(tag) && FlagTexture(tag) is not null;
        if (_sideFlag.Visible) SetFlag(_sideFlag, tag, 26);
    }

    /// <summary>Point an existing badge at another country without rebuilding it.</summary>
    private static void SetFlag(Control badge, string tag, float height)
    {
        if (badge is null || badge.GetChildCount() == 0 ||
            badge.GetChild(0) is not TextureRect rect) return;
        var texture = FlagTexture(tag);
        if (rect.Texture == texture) return;
        rect.Texture = texture;
        var width = texture is null || texture.GetHeight() <= 0
            ? height * 1.5f
            : height * texture.GetWidth() / texture.GetHeight();
        rect.CustomMinimumSize = new Vector2(Mathf.Round(width), Mathf.Round(height));
        badge.CustomMinimumSize = new Vector2(Mathf.Round(width) + 2, Mathf.Round(height) + 2);
    }
}
