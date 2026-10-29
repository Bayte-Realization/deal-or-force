using System;
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
    /// <summary>The flag the interface shows for this country, for the map's own markers:
    /// one answer everywhere, provisional flag included.</summary>
    internal static Texture2D FlagTextureFor(string tag) => FlagTexture(tag);
    internal static string FlagSpriteFor(string tag) => string.IsNullOrWhiteSpace(tag) ? "" : FlagSprite(tag, out _);

    /// <summary>
    /// A flag beside a name in a list, on the button that carries the name.
    ///
    /// The picker's country rows were built without one while the movement rows under them had
    /// one, so half the list said what a country was called and only the other half said which
    /// country it was. One helper now, so the next list that shows a country cannot forget: the
    /// flag is read now rather than left to the background loader, because the list is built the
    /// moment the screen opens, before the loader has reached flags nobody has asked for yet.
    /// Not expanded: an expanded icon adds nothing to a button's minimum width, and a button in
    /// a flowing row is given exactly its minimum - the flag would get no room at all.
    /// </summary>
    internal static void ListFlag(Button button, Nation nation, int width = 22)
    {
        if (button is null || nation is null || string.IsNullOrWhiteSpace(nation.Tag)) return;
        var name = $"flag_{nation.Tag.ToLowerInvariant()}";
        var path = SpriteCatalog.TextureOf(name, $"res://game/gfx/flags/{name}.png");
        button.Icon = FlagTexture(nation.Tag) ??
            (ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null);
        button.ExpandIcon = false;
        button.AddThemeConstantOverride("icon_max_width", width);
    }


    private static Texture2D FlagTexture(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var sprite = FlagSprite(tag, out var nation);
        if (nation is not null && ShouldUseDynamicRegionalFlag(nation, sprite))
            return ProvisionalFlag(nation);
        return FlagSpriteTexture(sprite) ?? (nation is null ? null : ProvisionalFlag(nation));
    }

    private static bool ShouldUseDynamicRegionalFlag(Nation nation, string sprite)
    {
        if (nation.Movement is not null || !string.IsNullOrWhiteSpace(nation.CustomFlag) ||
            !string.IsNullOrWhiteSpace(nation.FormedNationId)) return false;
        if (nation.MapIndex < SecessionEvents.FirstDynamicMapIndex &&
            nation.SecededFromId < 0 && nation.ReleasedFromId < 0) return false;
        if (string.IsNullOrEmpty(sprite)) return true;
        // If the country resolved to a generic culture/ideology/government/revolt fallback rather
        // than a tag-specific flag, give regional/macro-regional/directional states (or states whose
        // culture is already the primary culture of their parent) their own bespoke dynamic banner.
        var isFallback = sprite.StartsWith("flag_culture_", System.StringComparison.OrdinalIgnoreCase) ||
            sprite.StartsWith("flag_ideology_", System.StringComparison.OrdinalIgnoreCase) ||
            sprite.StartsWith("flag_government_", System.StringComparison.OrdinalIgnoreCase) ||
            sprite.StartsWith("flag_revolt_", System.StringComparison.OrdinalIgnoreCase);
        if (!isFallback) return false;
        if (nation.FoundNameKind is "land" or "region" or "direction") return true;
        var parentId = nation.SecededFromId >= 0 ? nation.SecededFromId : nation.ReleasedFromId;
        if (parentId >= 0 && FlagWorld?.NationById(parentId) is { } parent)
        {
            var primary = DemographyCatalog.PrimaryCulture(nation.Tag);
            if (!string.IsNullOrEmpty(primary) && DemographyCatalog.IsPrimary(parent.Tag, primary))
                return true;
        }
        return false;
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
        if (nation is not null && !string.IsNullOrWhiteSpace(nation.CustomFlag))
        {
            var custom = nation.CustomFlag.Trim();
            if (SpriteCatalog.Has(custom)) return custom;
            if (SpriteCatalog.Has("flag_" + custom)) return "flag_" + custom;
        }
        // Authored insignia are shown by default. The player may choose plain alternatives
        // explicitly; availability must not silently turn every movement into a placeholder.
        if (SettingsCatalog.Flag(MovementFlagsSetting) && SpriteCatalog.Has($"flag_{key}_insignia"))
            return $"flag_{key}_insignia";
        if (nation is null) return sprite;
        var country = nation;
        // Kept against what the country is - every value a flag can depend on - and nothing
        // else. It used to carry the map revision too, which changes with every occupation:
        // a few months into a war every answer was stale at once, and the first screen with a
        // list of flags on it worked out a hundred and sixty of them again on the frame that
        // opened it - ten seconds for the conflicts screen. A mod reload clears it instead.
        var situation = key + "|" + (nation.CustomFlag ?? "") + "|" + (nation.Movement?.Landless ?? false) + "|" +
            FlagCatalog.CivilWarParentTag(nation) + "|" +
            (nation.Movement?.Exile ?? false) + "|" + string.Join("|", FlagCatalog.All.Select(q =>
            FlagCatalog.ValueOf(q.Source, FlagWorld, country)));
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
    // Invalidating a UI cache does not cancel the engine's owned load request.
    // Retired requests must still be collected, but must not repopulate that cache.
    private static readonly List<string> RetiredFlagLoads = new();
    internal static int PendingFlagWarmups => FlagsLoading.Count + RetiredFlagLoads.Count;

    internal static void PollFlagWarmups()
    {
        if (PendingFlagWarmups == 0) return;
        var budget = 16;
        foreach (var pending in FlagsLoading.ToArray())
        {
            var status = ResourceLoader.LoadThreadedGetStatus(pending.Value);
            if (status == ResourceLoader.ThreadLoadStatus.InProgress) continue;
            var result = status == ResourceLoader.ThreadLoadStatus.InvalidResource
                ? null : ResourceLoader.LoadThreadedGet(pending.Value);
            FlagsLoading.Remove(pending.Key);
            if (result is Texture2D texture) FlagCache[pending.Key] = texture;
            if (--budget == 0) return;
        }
        for (var i = RetiredFlagLoads.Count - 1; i >= 0; i--)
        {
            var path = RetiredFlagLoads[i];
            var status = ResourceLoader.LoadThreadedGetStatus(path);
            if (status == ResourceLoader.ThreadLoadStatus.InProgress) continue;
            if (status != ResourceLoader.ThreadLoadStatus.InvalidResource) ResourceLoader.LoadThreadedGet(path);
            RetiredFlagLoads.RemoveAt(i);
            if (--budget == 0) return;
        }
    }

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
        var religion = nation.DeclaredReligion?.ToLowerInvariant() ?? "";
        var ideology = nation.Ideology?.ToLowerInvariant() ?? "";
        var culture = !string.IsNullOrWhiteSpace(nation.DeclaredCulture)
            ? nation.DeclaredCulture.ToLowerInvariant()
            : DemographyCatalog.PrimaryCulture(nation.Tag).ToLowerInvariant();
        if (string.IsNullOrEmpty(religion) && !string.IsNullOrEmpty(culture))
        {
            var faiths = DemographyCatalog.FaithOf(culture);
            if (faiths.Count > 0) religion = faiths[0].Religion.ToLowerInvariant();
        }
        var landId = nation.FoundNameKind == "land" ? (nation.FoundNameId?.ToLowerInvariant() ?? "") : "";
        var key = $"provisional|{nation.Tag}|{nation.Color}|{ideology}|{religion}|{nation.Government}|{culture}|{landId}|{nation.FoundedInRevolt}";
        if (FlagCache.TryGetValue(key, out var cached)) return cached;

        const int width = 300;
        const int height = 200;
        var baseColour = Color.FromString(nation.Color ?? "#888888", new Color(0.45f, 0.48f, 0.52f));
        var light = new Color(0.96f, 0.95f, 0.92f);
        var gold = new Color(0.95f, 0.76f, 0.16f);
        var dark = baseColour.Darkened(0.52f);
        var navy = new Color(0.10f, 0.27f, 0.52f);
        var crimson = new Color(0.74f, 0.13f, 0.18f);
        var emerald = new Color(0.11f, 0.44f, 0.24f);
        var charcoal = new Color(0.11f, 0.13f, 0.16f);

        var isMuslim = religion is "sunni" or "shia" or "ibadi" or "sufi";
        var isCommunist = ideology is "communism" or "socialism";
        var isFascist = ideology is "fascism";
        var isChristian = religion is "orthodox" or "catholic" or "protestant" or "other_christian";
        var isMonarchy = nation.Government?.Contains("monarchy") == true;
        var isFederation = nation.Government == "federation" || nation.FoundedInRevolt == "federal";
        var group = DemographyCatalog.Culture(culture)?.Group?.ToLowerInvariant() ?? "";

        var hash = 2166136261u;
        foreach (var character in (nation.Tag ?? "") + "|" + landId + "|" + culture)
            hash = (hash ^ character) * 16777619u;
        var style = (int)(hash % 10);

        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);

        if (!DrawNamedLandBanner(image, width, height, landId, isCommunist, isMonarchy, isMuslim, light, gold, navy, crimson, emerald, charcoal))
        {
            if (isCommunist)
            {
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var pixel = (style % 4) switch
                    {
                        0 => y < height * 0.22f || y >= height * 0.78f ? baseColour.Darkened(0.15f) : crimson,
                        1 => y < height * 0.68f ? crimson : (y < height * 0.74f ? light : navy),
                        2 => (x * height < y * width / 2) ? gold : crimson,
                        _ => crimson
                    };
                    image.SetPixel(x, y, ShadeFabric(pixel, x, y, width, height));
                }
                var starPos = (style % 4) == 0 ? new Vector2(width * 0.50f, height * 0.50f) : new Vector2(width * 0.20f, height * 0.28f);
                DrawStar(image, width, height, starPos, 30f, gold);
                DrawStar(image, width, height, starPos, 22f, crimson);
                DrawStar(image, width, height, starPos, 14f, gold);
            }
            else if (isMuslim)
            {
                var c1 = group is "turkic" ? new Color(0.14f, 0.56f, 0.68f) : emerald;
                var c2 = light;
                var c3 = group is "arabic" ? charcoal : dark;
                RenderLayout(image, width, height, style % 6, c1, c2, c3, gold);
                var crescentCenter = (style % 6) == 4
                    ? new Vector2(width * 0.18f, height * 0.50f)
                    : new Vector2(width * 0.50f, height * 0.50f);
                var emblemColor = (style % 6) is 1 or 4 ? gold : light;
                DrawCrescent(image, width, height, crescentCenter, 32f, 25f, new Vector2(9f, 0f), emblemColor);
                DrawStar(image, width, height, crescentCenter + new Vector2(20f, 0f), 12f, emblemColor);
            }
            else if (group is "finnic" or "baltic" || (isChristian && style is 0 or 1))
            {
                DrawNordicOrCenteredCross(image, width, height, style % 2 == 0, baseColour, light, gold);
                if (isMonarchy)
                    DrawShieldEmblem(image, width, height, new Vector2(width * 0.50f, height * 0.50f), crimson, gold, light);
            }
            else
            {
                var c1 = isFascist ? charcoal : baseColour;
                var c2 = light;
                var c3 = isFascist ? crimson : (group is "east_slavic" or "west_slavic" or "south_slavic" ? crimson : dark);
                RenderLayout(image, width, height, style, c1, c2, c3, gold);
                var center = style == 4 ? new Vector2(width * 0.18f, height * 0.50f)
                    : style == 5 ? new Vector2(width * 0.22f, height * 0.27f)
                    : new Vector2(width * 0.50f, height * 0.50f);
                if (isMonarchy)
                    DrawShieldEmblem(image, width, height, center, crimson, gold, light);
                else if (isFederation)
                    DrawStarRing(image, width, height, center, 34f, 6, gold);
                else if (group is "turkic" or "mongolic" or "iranian" or "indo_aryan")
                    DrawSunDisc(image, width, height, center, 24f, gold);
                else if (style is 0 or 3 or 4 or 5 or 7)
                    DrawStar(image, width, height, center, 24f, style == 3 ? c1 : gold);
            }
        }

        image.GenerateMipmaps();
        var texture = ImageTexture.CreateFromImage(image);
        FlagCache[key] = texture;
        return texture;
    }

    private static bool DrawNamedLandBanner(Image image, int width, int height, string landId,
        bool isCommunist, bool isMonarchy, bool isMuslim,
        Color light, Color gold, Color navy, Color crimson, Color emerald, Color charcoal)
    {
        if (string.IsNullOrEmpty(landId)) return false;
        switch (landId)
        {
            case "siberia":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(y < height / 2 ? light : emerald, x, y, width, height));
                DrawEightPointStar(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 28f, isCommunist ? crimson : gold);
                return true;
            case "ural":
            case "urals":
                FillHorizontalThree(image, width, height, light, new Color(0.12f, 0.52f, 0.32f), charcoal, 0.33f, 0.67f);
                if (isMonarchy || isCommunist)
                    DrawShieldEmblem(image, width, height, new Vector2(width * 0.50f, height * 0.50f), crimson, gold, light);
                return true;
            case "far_east":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var inCanton = x < width * 0.42f && y < height * 0.52f;
                    image.SetPixel(x, y, ShadeFabric(inCanton ? navy : crimson, x, y, width, height));
                }
                DrawSunDisc(image, width, height, new Vector2(width * 0.21f, height * 0.26f), 20f, gold);
                return true;
            case "kuban":
            case "don_kuban":
                FillHorizontalThree(image, width, height, navy, new Color(0.64f, 0.11f, 0.26f), emerald, 0.25f, 0.75f);
                DrawShieldEmblem(image, width, height, new Vector2(width * 0.50f, height * 0.50f), gold, navy, light);
                return true;
            case "don":
                FillHorizontalThree(image, width, height, navy, gold, crimson, 0.33f, 0.67f);
                return true;
            case "pomorye":
                DrawNordicOrCenteredCross(image, width, height, true, navy, light, emerald);
                return true;
            case "ingria":
                DrawNordicOrCenteredCross(image, width, height, true, gold, navy, crimson);
                return true;
            case "prussia":
            case "kaliningrad":
                FillHorizontalThree(image, width, height, charcoal, light, charcoal, 0.22f, 0.78f);
                DrawShieldEmblem(image, width, height, new Vector2(width * 0.50f, height * 0.50f), charcoal, gold, light);
                return true;
            case "donbas":
                FillHorizontalThree(image, width, height, charcoal, navy, crimson, 0.33f, 0.67f);
                DrawSunDisc(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 24f, gold);
                return true;
            case "sloboda":
                FillHorizontalThree(image, width, height, new Color(0.62f, 0.12f, 0.26f), gold, navy, 0.34f, 0.67f);
                DrawEightPointStar(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 25f, light);
                return true;
            case "novorossiya":
            case "budjak":
                DrawSaltireBanner(image, width, height, crimson, navy, light);
                return true;
            case "zaporizhzhia":
                FillHorizontalThree(image, width, height, new Color(0.64f, 0.11f, 0.26f), gold, new Color(0.64f, 0.11f, 0.26f), 0.42f, 0.58f);
                DrawShieldEmblem(image, width, height, new Vector2(width * 0.28f, height * 0.50f), navy, gold, light);
                return true;
            case "galicia":
                FillHorizontalThree(image, width, height, navy, gold, crimson, 0.34f, 0.67f);
                DrawShieldEmblem(image, width, height, new Vector2(width * 0.50f, height * 0.50f), navy, gold, light);
                return true;
            case "volhynia":
                DrawNordicOrCenteredCross(image, width, height, false, crimson, light, gold);
                return true;
            case "podolia":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(y < height * 0.72f ? navy : light, x, y, width, height));
                DrawSunDisc(image, width, height, new Vector2(width * 0.50f, height * 0.38f), 30f, gold);
                return true;
            case "crimea":
                FillHorizontalThree(image, width, height, light, new Color(0.16f, 0.54f, 0.82f), crimson, 0.16f, 0.84f);
                if (isMuslim || isMonarchy)
                    DrawCrescent(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 26f, 20f, new Vector2(7f, 0f), gold);
                else
                    DrawShieldEmblem(image, width, height, new Vector2(width * 0.50f, height * 0.50f), crimson, gold, light);
                return true;
            case "texas":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var c = x < width / 3 ? navy : (y < height / 2 ? light : crimson);
                    image.SetPixel(x, y, ShadeFabric(c, x, y, width, height));
                }
                DrawStar(image, width, height, new Vector2(width / 6f, height * 0.50f), 32f, light);
                return true;
            case "california":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(y >= height * 0.82f ? crimson : light, x, y, width, height));
                DrawStar(image, width, height, new Vector2(width * 0.16f, height * 0.22f), 20f, crimson);
                DrawDiamond(image, width, height, new Vector2(width * 0.50f, height * 0.45f), 42f, 26f, new Color(0.52f, 0.34f, 0.18f));
                return true;
            case "cascadia":
                FillHorizontalThree(image, width, height, new Color(0.14f, 0.46f, 0.76f), light, emerald, 0.33f, 0.67f);
                DrawDiamond(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 24f, 52f, new Color(0.08f, 0.28f, 0.14f));
                return true;
            case "new_england":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var inCanton = x < width * 0.40f && y < height * 0.50f;
                    image.SetPixel(x, y, ShadeFabric(inCanton ? light : crimson, x, y, width, height));
                }
                DrawDiamond(image, width, height, new Vector2(width * 0.20f, height * 0.25f), 18f, 30f, emerald);
                return true;
            case "dixie":
                DrawSaltireBanner(image, width, height, crimson, light, navy);
                return true;
            case "manchuria":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var pixel = gold;
                    if (x < width * 0.42f && y < height * 0.52f)
                    {
                        var band = (int)(y / (height * 0.13f));
                        pixel = band switch { 0 => crimson, 1 => navy, 2 => light, _ => charcoal };
                    }
                    image.SetPixel(x, y, ShadeFabric(pixel, x, y, width, height));
                }
                return true;
            case "hadhramaut":
            case "south_arabia":
            case "hejaz":
            case "asir":
                RenderLayout(image, width, height, 4, charcoal, light, emerald, crimson);
                DrawCrescent(image, width, height, new Vector2(width * 0.16f, height * 0.50f), 20f, 15f, new Vector2(5f, 0f), gold);
                return true;
            case "cyrenaica":
            case "fezzan":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(charcoal, x, y, width, height));
                DrawCrescent(image, width, height, new Vector2(width * 0.48f, height * 0.50f), 32f, 25f, new Vector2(8f, 0f), light);
                DrawStar(image, width, height, new Vector2(width * 0.56f, height * 0.50f), 12f, light);
                return true;
            case "volga":
                FillHorizontalThree(image, width, height, gold, new Color(0.18f, 0.52f, 0.82f), light, 0.33f, 0.67f);
                DrawSunDisc(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 22f, gold);
                return true;
            case "chernozemye":
                FillHorizontalThree(image, width, height, charcoal, gold, emerald, 0.33f, 0.67f);
                return true;
            case "central_russia":
            case "northwest_russia":
                FillHorizontalThree(image, width, height, light, navy, crimson, 0.33f, 0.67f);
                DrawShieldEmblem(image, width, height, new Vector2(width * 0.50f, height * 0.50f), crimson, gold, light);
                return true;
            case "novgorod":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(light, x, y, width, height));
                DrawNordicOrCenteredCross(image, width, height, false, crimson, gold, navy);
                return true;
            case "north_caucasus":
                FillHorizontalThree(image, width, height, emerald, light, emerald, 0.28f, 0.72f);
                DrawCrescent(image, width, height, new Vector2(width * 0.45f, height * 0.50f), 22f, 17f, new Vector2(5f, 0f), gold);
                DrawStar(image, width, height, new Vector2(width * 0.55f, height * 0.50f), 10f, gold);
                return true;
            case "primorye":
                RenderLayout(image, width, height, 6, navy, light, emerald, gold);
                DrawStar(image, width, height, new Vector2(width * 0.22f, height * 0.28f), 16f, light);
                return true;
            case "amur":
                FillHorizontalThree(image, width, height, emerald, light, new Color(0.15f, 0.45f, 0.85f), 0.33f, 0.67f);
                return true;
            case "transbaikalia":
                FillHorizontalThree(image, width, height, gold, light, navy, 0.33f, 0.67f);
                return true;
            case "kuzbass":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(y < height * 0.50f ? charcoal : crimson, x, y, width, height));
                DrawEightPointStar(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 24f, gold);
                return true;
            case "tavrida":
                FillHorizontalThree(image, width, height, new Color(0.2f, 0.55f, 0.85f), light, gold, 0.33f, 0.67f);
                return true;
            case "midwest":
                FillHorizontalThree(image, width, height, navy, light, gold, 0.33f, 0.67f);
                DrawStarRing(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 26f, 6, navy);
                return true;
            case "rockies":
                FillHorizontalThree(image, width, height, new Color(0.15f, 0.40f, 0.80f), light, emerald, 0.35f, 0.65f);
                DrawDiamond(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 38f, 28f, light);
                return true;
            case "florida":
                DrawSaltireBanner(image, width, height, light, crimson, gold);
                DrawSunDisc(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 18f, gold);
                return true;
            case "alaska":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(navy, x, y, width, height));
                DrawStar(image, width, height, new Vector2(width * 0.72f, height * 0.30f), 16f, gold);
                DrawStar(image, width, height, new Vector2(width * 0.30f, height * 0.65f), 9f, gold);
                DrawStar(image, width, height, new Vector2(width * 0.40f, height * 0.60f), 9f, gold);
                return true;
            case "deseret":
                FillHorizontalThree(image, width, height, navy, light, gold, 0.25f, 0.75f);
                DrawStar(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 22f, gold);
                return true;
            case "scotland":
                DrawSaltireBanner(image, width, height, navy, light, light);
                return true;
            case "wales":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(y < height * 0.50f ? light : emerald, x, y, width, height));
                DrawShieldEmblem(image, width, height, new Vector2(width * 0.50f, height * 0.50f), crimson, gold, light);
                return true;
            case "kurdistan":
                FillHorizontalThree(image, width, height, crimson, light, emerald, 0.33f, 0.67f);
                DrawSunDisc(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 24f, gold);
                return true;
            case "balochistan":
                RenderLayout(image, width, height, 4, emerald, crimson, navy, light);
                DrawCrescent(image, width, height, new Vector2(width * 0.16f, height * 0.50f), 18f, 13f, new Vector2(4f, 0f), light);
                DrawStar(image, width, height, new Vector2(width * 0.22f, height * 0.50f), 8f, light);
                return true;
            case "bavaria":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(y < height * 0.50f ? light : new Color(0.2f, 0.55f, 0.88f), x, y, width, height));
                DrawShieldEmblem(image, width, height, new Vector2(width * 0.50f, height * 0.50f), charcoal, gold, light);
                return true;
            case "rhineland":
                FillHorizontalThree(image, width, height, emerald, light, crimson, 0.33f, 0.67f);
                return true;
            case "saxony":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(y < height * 0.50f ? light : emerald, x, y, width, height));
                return true;
            case "lombardy":
            case "veneto":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(crimson, x, y, width, height));
                DrawShieldEmblem(image, width, height, new Vector2(width * 0.50f, height * 0.50f), gold, crimson, light);
                return true;
            case "sicily":
                RenderLayout(image, width, height, 6, gold, light, crimson, gold);
                DrawStar(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 22f, light);
                return true;
            case "occitania":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(crimson, x, y, width, height));
                DrawEightPointStar(image, width, height, new Vector2(width * 0.50f, height * 0.50f), 28f, gold);
                return true;
            case "brittany":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var inCanton = x < width * 0.40f && y < height * 0.50f;
                    if (inCanton) image.SetPixel(x, y, ShadeFabric(light, x, y, width, height));
                    else
                    {
                        var stripe = (int)(y / (height / 9f));
                        image.SetPixel(x, y, ShadeFabric(stripe % 2 == 0 ? charcoal : light, x, y, width, height));
                    }
                }
                DrawEightPointStar(image, width, height, new Vector2(width * 0.20f, height * 0.25f), 14f, charcoal);
                return true;
            case "normandy":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(crimson, x, y, width, height));
                DrawShieldEmblem(image, width, height, new Vector2(width * 0.50f, height * 0.50f), crimson, gold, light);
                return true;
            case "rio_grande":
            case "sao_paulo":
                RenderLayout(image, width, height, 5, crimson, light, charcoal, gold);
                return true;
            case "yucatan":
                RenderLayout(image, width, height, 1, emerald, light, crimson, gold);
                DrawStar(image, width, height, new Vector2(width * 0.16f, height * 0.50f), 16f, light);
                return true;
            case "zhongyuan":
            case "xibei":
            case "yunnan":
            case "shanxi":
            case "fujian":
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    image.SetPixel(x, y, ShadeFabric(crimson, x, y, width, height));
                DrawSunDisc(image, width, height, new Vector2(width * 0.22f, height * 0.30f), 22f, gold);
                return true;
            default:
                return false;
        }
    }

    private static void RenderLayout(Image image, int width, int height, int style, Color c1, Color c2, Color c3, Color accent)
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var u = x / (float)width;
            var v = y / (float)height;
            var pixel = (style % 8) switch
            {
                0 => v < 0.33f ? c1 : (v < 0.67f ? c2 : c3),
                1 => u < 0.33f ? c1 : (u < 0.67f ? c2 : c3),
                2 => v < 0.28f || v > 0.72f ? c1 : (v < 0.32f || v > 0.68f ? c2 : c3),
                3 => v < 0.50f ? c2 : c1,
                4 => u < (0.5f - Mathf.Abs(v - 0.5f)) * 0.92f ? accent : (v < 0.33f ? c1 : (v < 0.67f ? c2 : c3)),
                5 => (u < 0.42f && v < 0.54f) ? c1 : (((int)(v * 7f) % 2 == 0) ? c3 : c2),
                6 => Mathf.Abs(v - (1f - u)) < 0.11f ? accent : (Mathf.Abs(v - (1f - u)) < 0.15f ? c2 : (v < 1f - u ? c1 : c3)),
                _ => u < 0.25f || u > 0.75f ? c1 : c2
            };
            image.SetPixel(x, y, ShadeFabric(pixel, x, y, width, height));
        }
    }

    private static void FillHorizontalThree(Image image, int width, int height, Color top, Color mid, Color bot, float split1, float split2)
    {
        var y1 = (int)(height * split1);
        var y2 = (int)(height * split2);
        for (var y = 0; y < height; y++)
        {
            var c = y < y1 ? top : (y < y2 ? mid : bot);
            for (var x = 0; x < width; x++)
                image.SetPixel(x, y, ShadeFabric(c, x, y, width, height));
        }
    }

    private static void DrawNordicOrCenteredCross(Image image, int width, int height, bool nordic, Color field, Color fimbriation, Color innerCross)
    {
        var cx = nordic ? (int)(width * 0.36f) : width / 2;
        var cy = height / 2;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dx = Mathf.Abs(x - cx);
            var dy = Mathf.Abs(y - cy);
            var c = (dx <= 10 || dy <= 10) ? innerCross
                : (dx <= 16 || dy <= 16) ? fimbriation
                : field;
            image.SetPixel(x, y, ShadeFabric(c, x, y, width, height));
        }
    }

    private static void DrawSaltireBanner(Image image, int width, int height, Color field, Color fimbriation, Color innerSaltire)
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var u = x / (float)width;
            var v = y / (float)height;
            var d = Mathf.Min(Mathf.Abs(v - u), Mathf.Abs(v - (1f - u)));
            var c = d < 0.075f ? innerSaltire : (d < 0.115f ? fimbriation : field);
            image.SetPixel(x, y, ShadeFabric(c, x, y, width, height));
        }
    }

    /// <summary>
    /// A field colour as it is painted: flat. It used to ripple the cloth and darken the edges,
    /// and the provisional flags of new countries read as embossed and muddy beside the flat flags
    /// every other country flies (the user, 2026-10-28). Kept as one door so a mod that wants the
    /// cloth back has one place to put it.
    /// </summary>
    private static Color ShadeFabric(Color color, int x, int y, int width, int height) =>
        new(Mathf.Clamp(color.R, 0f, 1f), Mathf.Clamp(color.G, 0f, 1f), Mathf.Clamp(color.B, 0f, 1f), 1f);

    private static void DrawEightPointStar(Image image, int width, int height, Vector2 centre, float radius, Color fill)
    {
        var points = new Vector2[16];
        for (var p = 0; p < 16; p++)
        {
            var angle = -Mathf.Pi / 2f + p * Mathf.Pi / 8f;
            var reach = p % 2 == 0 ? radius : radius * 0.44f;
            points[p] = centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * reach;
        }
        for (var y = Math.Max(0, (int)(centre.Y - radius)); y <= Math.Min(height - 1, (int)(centre.Y + radius)); y++)
        for (var x = Math.Max(0, (int)(centre.X - radius)); x <= Math.Min(width - 1, (int)(centre.X + radius)); x++)
            if (Geometry2D.IsPointInPolygon(new Vector2(x + 0.5f, y + 0.5f), points))
                image.SetPixel(x, y, fill);
    }

    private static void DrawSunDisc(Image image, int width, int height, Vector2 centre, float radius, Color fill)
    {
        DrawEightPointStar(image, width, height, centre, radius * 1.15f, fill);
        var rSq = (radius * 0.68f) * (radius * 0.68f);
        for (var y = Math.Max(0, (int)(centre.Y - radius)); y <= Math.Min(height - 1, (int)(centre.Y + radius)); y++)
        for (var x = Math.Max(0, (int)(centre.X - radius)); x <= Math.Min(width - 1, (int)(centre.X + radius)); x++)
            if ((x - centre.X) * (x - centre.X) + (y - centre.Y) * (y - centre.Y) <= rSq)
                image.SetPixel(x, y, fill);
    }

    private static void DrawStarRing(Image image, int width, int height, Vector2 centre, float ringRadius, int count, Color fill)
    {
        for (var i = 0; i < count; i++)
        {
            var angle = -Mathf.Pi / 2f + i * Mathf.Tau / count;
            var pos = centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * ringRadius;
            DrawStar(image, width, height, pos, 9.5f, fill);
        }
    }

    private static void DrawShieldEmblem(Image image, int width, int height, Vector2 centre, Color shieldFill, Color borderGold, Color innerCharge)
    {
        const float halfW = 24f;
        const float halfH = 28f;
        for (var y = Math.Max(0, (int)(centre.Y - halfH)); y <= Math.Min(height - 1, (int)(centre.Y + halfH)); y++)
        for (var x = Math.Max(0, (int)(centre.X - halfW)); x <= Math.Min(width - 1, (int)(centre.X + halfW)); x++)
        {
            var ny = (y - (centre.Y - halfH)) / (2f * halfH);
            var maxHalfX = ny < 0.55f ? halfW : halfW * Mathf.Max(0f, 1f - Mathf.Pow((ny - 0.55f) / 0.45f, 1.45f));
            var dx = Mathf.Abs(x - centre.X);
            if (dx > maxHalfX) continue;
            var isBorder = dx >= maxHalfX - 3.5f || ny < 0.11f;
            image.SetPixel(x, y, isBorder ? borderGold : shieldFill);
        }
        DrawStar(image, width, height, centre + new Vector2(0f, -2f), 11f, innerCharge);
    }

    private static void DrawStar(Image image, int width, int height, Vector2 centre, float radius, Color fill)
    {
        var points = new Vector2[10];
        for (var point = 0; point < 10; point++)
        {
            var angle = -Mathf.Pi / 2f + point * Mathf.Pi / 5f;
            var reach = point % 2 == 0 ? radius : radius * 0.40f;
            points[point] = centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * reach;
        }
        for (var y = Math.Max(0, (int)(centre.Y - radius)); y <= Math.Min(height - 1, (int)(centre.Y + radius)); y++)
        for (var x = Math.Max(0, (int)(centre.X - radius)); x <= Math.Min(width - 1, (int)(centre.X + radius)); x++)
            if (Geometry2D.IsPointInPolygon(new Vector2(x + 0.5f, y + 0.5f), points))
                image.SetPixel(x, y, fill);
    }

    private static void DrawCrescent(Image image, int width, int height, Vector2 centre, float outerR, float innerR, Vector2 offset, Color fill)
    {
        var innerCenter = centre + offset;
        var rOutSq = outerR * outerR;
        var rInSq = innerR * innerR;
        for (var y = Math.Max(0, (int)(centre.Y - outerR)); y <= Math.Min(height - 1, (int)(centre.Y + outerR)); y++)
        for (var x = Math.Max(0, (int)(centre.X - outerR)); x <= Math.Min(width - 1, (int)(centre.X + outerR)); x++)
        {
            var dOut = (x - centre.X) * (x - centre.X) + (y - centre.Y) * (y - centre.Y);
            if (dOut <= rOutSq)
            {
                var dIn = (x - innerCenter.X) * (x - innerCenter.X) + (y - innerCenter.Y) * (y - innerCenter.Y);
                if (dIn > rInSq) image.SetPixel(x, y, fill);
            }
        }
    }

    private static void DrawDiamond(Image image, int width, int height, Vector2 centre, float halfW, float halfH, Color fill)
    {
        for (var y = Math.Max(0, (int)(centre.Y - halfH)); y <= Math.Min(height - 1, (int)(centre.Y + halfH)); y++)
        for (var x = Math.Max(0, (int)(centre.X - halfW)); x <= Math.Min(width - 1, (int)(centre.X + halfW)); x++)
        {
            var dx = Mathf.Abs(x - centre.X) / halfW;
            var dy = Mathf.Abs(y - centre.Y) / halfH;
            if (dx + dy <= 1f) image.SetPixel(x, y, fill);
        }
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
        RetiredFlagLoads.AddRange(FlagsLoading.Values);
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
            // Its own width, never the column's: in a vertical list a badge took the whole
            // column, and the deal composer drew Poland as two bands half a screen wide.
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
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
            // And in its own proportions whatever the frame is given; filtered through its
            // mipmaps, because a flag stored 512 pixels high and drawn 24 high is otherwise a
            // field of jagged pixels.
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.LinearWithMipmaps,
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
