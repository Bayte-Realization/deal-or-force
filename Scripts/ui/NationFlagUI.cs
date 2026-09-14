using System.Collections.Generic;
using System.Linq;
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
    /// `gfx/flags/flag_fra.png` IS the flag of France, so a mod that wants a different one
    /// drops its own `flag_fra.png` into its own `gfx/flags/` and nothing else has to be
    /// written down anywhere. A country the base game has never heard of works the same way.
    ///
    /// They are pictures rather than vectors, which is a decision about who mods this game
    /// rather than about fidelity. Anybody can open a PNG in whatever they already have and
    /// paint over it; an SVG asks them to find and learn a vector editor first, and a
    /// modding format that half the people who want to use it cannot open is not a modding
    /// format. What that costs is bought back with resolution: every flag is stored far
    /// larger than any panel draws it, so it is downsampled rather than stretched, and
    /// downsampling is the direction that looks fine.
    /// </summary>
    private static Texture2D FlagTexture(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var key = tag.ToLowerInvariant();

        // Which flag this country flies today, which is no longer a fixed answer: a country
        // that has gone communist flies something else, a restored monarchy flies its old
        // ensign, and a country founded this morning by a secession has no file named after
        // it at all and falls back to the flag of the people who founded it. See
        // <see cref="FlagCatalog"/>, where all of that is data.
        var sprite = $"flag_{key}";
        var nation = FlagWorld?.NationByTag(tag);
        if (nation is not null)
        {
            var situation = key + "|" + string.Join("|", FlagCatalog.All.Select(q =>
                FlagCatalog.ValueOf(q.Source, FlagWorld, nation))) + $"|{FlagWorld.MapRevision}";
            if (!ResolvedFlags.TryGetValue(situation, out var resolved))
                ResolvedFlags[situation] = resolved = FlagCatalog.Resolve(FlagWorld, nation);
            if (!string.IsNullOrEmpty(resolved)) sprite = resolved;
        }

        return FlagSpriteTexture(sprite);
    }

    private static Texture2D FlagSpriteTexture(string sprite)
    {
        if (string.IsNullOrEmpty(sprite)) return null;
        if (FlagCache.TryGetValue(sprite, out var cached)) return cached;
        var path = SpriteCatalog.TextureOf(sprite, $"res://game/gfx/flags/{sprite}.png");
        var texture = !string.IsNullOrWhiteSpace(path) && ResourceLoader.Exists(path)
            ? GD.Load<Texture2D>(path)
            : null;
        FlagCache[sprite] = texture;
        return texture;
    }

    /// <summary>
    /// The world the flags are being chosen for, and the answers already worked out for it.
    ///
    /// Which flag a country flies now depends on what that country is, so the interface has
    /// to be able to ask the campaign - and asking per badge per refresh would walk the
    /// candidate list a few hundred times a second. The answer only changes when the country
    /// does, so it is kept against the things it was worked out from.
    /// </summary>
    private static Campaign FlagWorld;

    private static readonly Dictionary<string, string> ResolvedFlags = new();

    /// <summary>Point the flags at a campaign, forgetting what was worked out for the last one.</summary>
    private static void BindFlags(Campaign campaign)
    {
        FlagWorld = campaign;
        ForgetFlags();
    }

    /// <summary>Drop the cache when the sprite catalogue changes under it - a mod reload.</summary>
    private static void ForgetFlags()
    {
        FlagCache.Clear();
        ResolvedFlags.Clear();
    }

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
            TooltipText = Ui.Tip(tooltip)
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
