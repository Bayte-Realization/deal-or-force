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
        var sprite = FlagSprite(tag, out var nation);
        return FlagSpriteTexture(sprite) ?? (nation is null ? null : ProvisionalFlag(nation));
    }

    /// <summary>
    /// Which flag this country flies today, which is no longer a fixed answer: a country
    /// that has gone communist flies something else, a restored monarchy flies its old
    /// ensign, and a country founded this morning by a secession has no file named after it
    /// at all and falls back to the flag of the people who founded it. See
    /// <see cref="FlagCatalog"/>, where all of that is data.
    /// </summary>
    private static string FlagSprite(string tag, out Nation nation)
    {
        var key = tag.ToLowerInvariant();
        var sprite = $"flag_{key}";
        nation = FlagWorld?.NationByTag(tag);
        // A movement's own banner - `flag_<tag>_insignia` - only when the player has asked for
        // them (`movement_flags`). It is off by default because a video showing an armed
        // group's flag can be restricted for it; the file beside it is a banner in its colours.
        if (SettingsCatalog.Flag(MovementFlagsSetting) && SpriteCatalog.Has($"flag_{key}_insignia"))
            return $"flag_{key}_insignia";
        if (nation is null) return sprite;
        var country = nation;
        var situation = key + "|" + string.Join("|", FlagCatalog.All.Select(q =>
            FlagCatalog.ValueOf(q.Source, FlagWorld, country))) + $"|{FlagWorld.MapRevision}";
        if (!ResolvedFlags.TryGetValue(situation, out var resolved))
            ResolvedFlags[situation] = resolved = FlagCatalog.Resolve(FlagWorld, nation);
        return string.IsNullOrEmpty(resolved) ? sprite : resolved;
    }

    /// <summary>
    /// Flags being read from disk in the background, by sprite, with the path each was asked for.
    ///
    /// The first diplomacy screen of a session drew two hundred and sixty flags and read every
    /// one of them from disk on the frame that opened it: thirteen seconds in the editor, the
    /// freeze a right click on a country cost. Every flag the world flies is asked for the
    /// moment a campaign starts, on the loader's own threads, so the screen finds them read.
    /// </summary>
    private static readonly Dictionary<string, string> FlagsLoading = new();

    /// <summary>Ask for every flag the world flies now, without waiting for any of them.</summary>
    private static void WarmFlags(Campaign campaign)
    {
        if (campaign is null) return;
        foreach (var nation in campaign.Nations)
        {
            if (string.IsNullOrWhiteSpace(nation.Tag)) continue;
            var sprite = FlagSprite(nation.Tag, out _);
            if (FlagCache.ContainsKey(sprite) || FlagsLoading.ContainsKey(sprite)) continue;
            var path = SpriteCatalog.TextureOf(sprite, $"res://game/gfx/flags/{sprite}.png");
            if (string.IsNullOrWhiteSpace(path) || !ResourceLoader.Exists(path)) continue;
            if (ResourceLoader.LoadThreadedRequest(path) == Error.Ok) FlagsLoading[sprite] = path;
        }
    }

    /// <summary>
    /// A flag for a country nobody drew one for (item 42 on the list).
    ///
    /// The search above ends with the people's flag and the ideology's banner, and a state
    /// founded this morning often has neither: there is no flag for "Korean" that is not the
    /// communist one, and no banner for nationalism at all - so the new country's badge was an
    /// empty frame. It gets a provisional flag instead, drawn from its own map colour, the way
    /// a new state's first flag usually is: a bicolour or a tricolour, sometimes a star. The
    /// pattern comes from the tag, so a country keeps the same flag from one session to the
    /// next, and any file a mod names after it still wins.
    /// </summary>
    private static Texture2D ProvisionalFlag(Nation nation)
    {
        var key = $"provisional|{nation.Tag}|{nation.Color}";
        if (FlagCache.TryGetValue(key, out var cached)) return cached;

        const int width = 300;
        const int height = 200;
        var colour = Color.FromString(nation.Color ?? "#888888", new Color(0.45f, 0.48f, 0.52f));
        var light = new Color(0.94f, 0.93f, 0.90f);
        var dark = colour.Darkened(0.45f);
        var hash = 2166136261u;
        foreach (var character in nation.Tag ?? "") hash = (hash ^ character) * 16777619u;
        var pattern = (int)(hash % 6);

        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var band = pattern switch
            {
                0 => y < height / 2 ? colour : light,
                1 => y < height / 3 ? colour : y < height * 2 / 3 ? light : dark,
                2 => x < width / 3 ? colour : x < width * 2 / 3 ? light : dark,
                3 => y < height / 2 ? colour : light,
                4 => y >= height * 3 / 10 && y < height * 7 / 10 ? light : colour,
                _ => Mathf.Abs(y - height / 2) < 16 || Mathf.Abs(x - width * 3 / 8) < 16 ? light : colour
            };
            image.SetPixel(x, y, band);
        }

        // A star where the pattern leaves room for one: in the upper band of the bicolour,
        // in the middle of the band across the centre.
        if (pattern is 3 or 4)
        {
            var centre = pattern == 3 ? new Vector2(width * 0.22f, height * 0.25f) : new Vector2(width * 0.5f, height * 0.5f);
            var radius = pattern == 3 ? 34f : 30f;
            var fill = pattern == 3 ? light : colour;
            var points = new Vector2[10];
            for (var point = 0; point < 10; point++)
            {
                var angle = -Mathf.Pi / 2f + point * Mathf.Pi / 5f;
                var reach = point % 2 == 0 ? radius : radius * 0.40f;
                points[point] = centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * reach;
            }
            for (var y = (int)(centre.Y - radius); y <= (int)(centre.Y + radius); y++)
            for (var x = (int)(centre.X - radius); x <= (int)(centre.X + radius); x++)
                if (x >= 0 && y >= 0 && x < width && y < height &&
                    Geometry2D.IsPointInPolygon(new Vector2(x + 0.5f, y + 0.5f), points))
                    image.SetPixel(x, y, fill);
        }

        var texture = ImageTexture.CreateFromImage(image);
        FlagCache[key] = texture;
        return texture;
    }

    internal static Texture2D FlagSpriteTexture(string sprite)
    {
        if (string.IsNullOrEmpty(sprite)) return null;
        if (FlagCache.TryGetValue(sprite, out var cached)) return cached;
        // Asked for in the background already: take it, waiting only for what is left of it.
        if (FlagsLoading.Remove(sprite, out var loading) &&
            ResourceLoader.LoadThreadedGet(loading) is Texture2D warmed)
            return FlagCache[sprite] = warmed;
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

    /// <summary>The settings id that lets movements fly their own flags.</summary>
    private const string MovementFlagsSetting = "movement_flags";

    /// <summary>Point the flags at a campaign, forgetting what was worked out for the last one.</summary>
    private static void BindFlags(Campaign campaign)
    {
        FlagWorld = campaign;
        ForgetFlags();
        WarmFlags(campaign);
    }

    /// <summary>Drop the cache when the sprite catalogue changes under it - a mod reload.</summary>
    private static void ForgetFlags()
    {
        FlagCache.Clear();
        ResolvedFlags.Clear();
        FlagsLoading.Clear();
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
