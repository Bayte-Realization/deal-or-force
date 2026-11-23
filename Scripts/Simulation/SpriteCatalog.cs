using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Which picture the game means when it asks for a named one.
///
/// This is the `gfx/` layer every Paradox title has, and the reason theirs is worth
/// copying: the interface never names a file, it names a *sprite*. A panel asks for
/// `panel_dossier` and the catalogue says where that lives. Swapping the art is then a
/// data change - one line in `gfx/interface/50_my_skin.json` - instead of a code change,
/// and a mod's own textures resolve against the mod's own folder, so it ships its art
/// beside its declaration and neither has to know where the other was installed.
///
/// Before this, twelve `res://Assets/...` paths were spelled out in the middle of the
/// interface code. Every one of them was a picture no mod could replace without forking a
/// C# file, which is exactly the thing the rest of the data layer was built to avoid.
///
/// The art itself lives in the tree, and a picture's file name *is* its sprite name.
/// `gfx/interface/icons/icon_diplomacy.png` declares `icon_diplomacy` by being called
/// that; a mod replaces it by putting a file of the same name at the same place, and
/// writes no JSON at all. That is the part a modder actually wants: not "you may point at
/// any path", but "put it here and call it this".
///
/// Declarations remain for what a file name cannot say - how many frames a strip holds,
/// what tint to multiply it by, or a sprite whose name should differ from its file. They
/// are read after the scan, so an explicit line always wins over the convention.
///
/// The whole `gfx/` tree is read, subfolders and all, in path order: the subfolders are
/// for people, not for the game. Later files win, so a mod loaded after the base game
/// replaces one sprite and inherits the other hundred.
/// </summary>
public static class SpriteCatalog
{
    /// <summary>One named picture: where it is, and how the interface should use it.</summary>
    public readonly struct Sprite
    {
        public Sprite(string texture, string tint, int frames, string sourceColour = "")
        {
            Texture = texture ?? "";
            Tint = tint ?? "";
            Frames = frames < 1 ? 1 : frames;
            SourceColour = sourceColour ?? "";
        }

        /// <summary>The resolved path, ready for the engine to load.</summary>
        public string Texture { get; }

        /// <summary>A colour to multiply the art by, or empty to use it as authored.</summary>
        public string Tint { get; }

        /// <summary>Authored RGB reference for theme-tinted icons. Empty means a white mask.</summary>
        public string SourceColour { get; }

        /// <summary>Frames across a strip, for art that holds several states in one file.</summary>
        public int Frames { get; }
    }

    public const string SpriteIndexFileName = "sprite_index_v1.json";
    public const int SpriteIndexVersion = 1;

    private static readonly Dictionary<string, Sprite> Sprites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Shaders = new(StringComparer.OrdinalIgnoreCase);
    private static bool _baseLoaded;
    private static bool _dirtyFromOverlay;
    private static string _loadedKey = "";

    /// <summary>Typed shader assets never enter the texture warm-up or picture pickers.</summary>
    public static string ShaderOf(string name, string fallback = "") =>
        name is not null && Shaders.TryGetValue(name, out var path) ? path : fallback;

    /// <summary>Raised whenever the table is rebuilt, so cached textures can be dropped.</summary>
    public static event Action Changed;

    public static int Count => Sprites.Count;

    public static IEnumerable<string> Names => Sprites.Keys;

    /// <summary>
    /// Whether a picture by this name exists - a file, a declaration, or a flag described in
    /// common/flag_designs. A design is a sprite like any other, so the flag resolver, the
    /// variant search and every list that asks "does this flag exist" find it without knowing
    /// it was never a file.
    /// </summary>
    public static bool Has(string name) =>
        !string.IsNullOrEmpty(name) && (Sprites.ContainsKey(name) || FlagDesignCatalog.Has(name));

    /// <summary>
    /// A design outranks a picture of the same name: it was written on purpose, usually by a
    /// mod or a player replacing exactly that flag. Its path is not a file but a
    /// <see cref="FlagDesignCatalog.TexturePrefix"/> address the interface draws.
    /// </summary>
    public static Sprite Get(string name) =>
        FlagDesignCatalog.Has(name) ? new Sprite(FlagDesignCatalog.TexturePrefix + name, "", 1)
        : !string.IsNullOrEmpty(name) && Sprites.TryGetValue(name, out var sprite)
            ? sprite
            : default;

    /// <summary>
    /// The path for a sprite, or the fallback when nothing declares it. Call sites pass the
    /// art they were written against as the fallback, so a deleted declaration costs the
    /// modder their override rather than costing the player a missing icon.
    /// </summary>
    public static string TextureOf(string name, string fallback = "")
    {
        var sprite = Get(name);
        return string.IsNullOrEmpty(sprite.Texture) ? fallback : sprite.Texture;
    }

    /// <summary>Read the base game's own `gfx/` tree (or pre-baked `cache/sprite_index_v1.json`), replacing whatever was loaded.</summary>
    public static void Load() => Load(force: false);

    /// <summary>
    /// Reloads base sprites/shaders. Idempotent when the same data root &amp; overlay policy are
    /// already loaded without mod/skin overlays, avoiding a second ~4,500-file crawl across
    /// MainMenuScene -&gt; DealOrForceGame. Uses pre-baked <c>cache/sprite_index_v1.json</c> when
    /// present and no total-conversion/replace_paths overlay is active (~3-5 ms, zero crawl).
    /// </summary>
    public static void Load(bool force)
    {
        var policyActive = ModOverlayPolicy.IsActive;
        var prebakedPath = policyActive ? null : GameDataLoader.FindDataFile("cache", SpriteIndexFileName);
        var gfxRoot = GameDataLoader.DataFolder("gfx");
        var cacheKey = $"{policyActive}|{prebakedPath ?? ""}|{gfxRoot ?? ""}";
        if (!force && _baseLoaded && !_dirtyFromOverlay && string.Equals(_loadedKey, cacheKey, StringComparison.Ordinal))
            return;

        Sprites.Clear();
        Shaders.Clear();
        var loadedFromPrebaked = prebakedPath is not null &&
            TryLoadPrebakedIndexInto(prebakedPath, Sprites, Shaders, out _, out _);
        if (!loadedFromPrebaked && gfxRoot is not null)
            LoadTreeInto(gfxRoot, "res://game/gfx", Sprites, Shaders, null);

        _baseLoaded = true;
        _dirtyFromOverlay = false;
        _loadedKey = cacheKey;
        Changed?.Invoke();
    }

    /// <summary>What the engine will load a picture as, from a file name.</summary>
    private static readonly string[] PictureExtensions =
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga", ".dds", ".ktx", ".svg" };

    /// <summary>
    /// Scans <paramref name="gameRoot"/>/gfx identically to runtime <see cref="Load"/> and writes a
    /// compact pre-baked JSON manifest (<c>cache/sprite_index_v1.json</c>) without mutating the live
    /// catalog. Lets Android packaging drop .pck-duplicated <c>game/gfx/</c> files from the zip while
    /// preserving the exact runtime sprite &amp; shader dictionaries. Returns sprites written.
    /// </summary>
    public static int WritePrebakedIndex(string gameRoot, string outPath = null)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
            throw new ArgumentException("gameRoot must be non-empty.", nameof(gameRoot));
        var gfxDir = Path.Combine(gameRoot, "gfx");
        var target = string.IsNullOrWhiteSpace(outPath)
            ? Path.Combine(gameRoot, "cache", SpriteIndexFileName)
            : outPath;

        var sprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        var shaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        LoadTreeInto(gfxDir, "res://game/gfx", sprites, shaders, null);

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteNumber("version", SpriteIndexVersion);

        writer.WriteStartObject("sprites");
        var spriteKeys = sprites.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var i = 0; i < spriteKeys.Length; i++)
        {
            var key = spriteKeys[i];
            var s = sprites[key];
            if (s.Tint.Length == 0 && s.Frames == 1 && s.SourceColour.Length == 0)
            {
                writer.WriteString(key, s.Texture);
            }
            else
            {
                writer.WriteStartObject(key);
                writer.WriteString("t", s.Texture);
                if (s.Tint.Length > 0) writer.WriteString("c", s.Tint);
                if (s.Frames > 1) writer.WriteNumber("f", s.Frames);
                if (s.SourceColour.Length > 0) writer.WriteString("s", s.SourceColour);
                writer.WriteEndObject();
            }
        }
        writer.WriteEndObject();

        writer.WriteStartObject("shaders");
        var shaderKeys = shaders.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var i = 0; i < shaderKeys.Length; i++)
        {
            var key = shaderKeys[i];
            writer.WriteString(key, shaders[key]);
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.Flush();
        return sprites.Count;
    }

    /// <summary>Validates a pre-baked <c>sprite_index_v1.json</c> file without mutating the live catalog.</summary>
    public static bool TryReadPrebakedIndex(string indexPath, out int spriteCount, out int shaderCount)
    {
        var sprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        var shaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return TryLoadPrebakedIndexInto(indexPath, sprites, shaders, out spriteCount, out shaderCount);
    }

    private static bool TryLoadPrebakedIndexInto(
        string indexPath,
        Dictionary<string, Sprite> sprites,
        Dictionary<string, string> shaders,
        out int spriteCount,
        out int shaderCount)
    {
        spriteCount = 0;
        shaderCount = 0;
        if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath)) return false;
        try
        {
            using var stream = File.OpenRead(indexPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number ||
                !ver.TryGetInt32(out var version) || version != SpriteIndexVersion)
                return false;
            if (!root.TryGetProperty("sprites", out var spritesEl) || spritesEl.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var entry in spritesEl.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String)
                {
                    var tex = entry.Value.GetString();
                    if (!string.IsNullOrEmpty(tex))
                        sprites[entry.Name] = new Sprite(tex, "", 1);
                }
                else if (entry.Value.ValueKind == JsonValueKind.Object)
                {
                    var obj = entry.Value;
                    var tex = obj.TryGetProperty("t", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : "";
                    if (string.IsNullOrEmpty(tex)) continue;
                    var tint = obj.TryGetProperty("c", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : "";
                    var frames = obj.TryGetProperty("f", out var fEl) && fEl.ValueKind == JsonValueKind.Number && fEl.TryGetInt32(out var fv) ? fv : 1;
                    var srcCol = obj.TryGetProperty("s", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : "";
                    sprites[entry.Name] = new Sprite(tex, tint, frames, srcCol);
                }
            }

            if (root.TryGetProperty("shaders", out var shadersEl) && shadersEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in shadersEl.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.String)
                        shaders[entry.Name] = entry.Value.GetString() ?? "";
            }

            spriteCount = sprites.Count;
            shaderCount = shaders.Count;
            return spriteCount > 0;
        }
        catch
        {
            sprites.Clear();
            shaders.Clear();
            return false;
        }
    }

    /// <summary>
    /// Layer one package's `gfx/` tree over what is already loaded.
    ///
    /// <paramref name="virtualDirectory"/> is the same folder as the engine names it -
    /// `res://game/gfx` for the base game, `<c>&lt;mod&gt;/gfx</c>` for a mod. Everything
    /// inside is addressed relative to it, so the same declaration means the same thing
    /// wherever it was installed.
    ///
    /// Pictures first, declarations after: the file names set the table and the JSON
    /// corrects it.
    /// </summary>
    public static void LoadTree(string directory, string virtualDirectory, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        _dirtyFromOverlay = true;
        LoadTreeInto(directory, virtualDirectory, Sprites, Shaders, errors);
        Changed?.Invoke();
    }

    private static void LoadTreeInto(
        string directory, string virtualDirectory,
        Dictionary<string, Sprite> sprites, Dictionary<string, string> shaders, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        string[] everything;
        try
        {
            everything = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .OrderBy(path => path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            errors?.Add($"gfx: {exception.Message}");
            return;
        }

        var root = directory.Replace('\\', '/').TrimEnd('/');
        foreach (var file in everything)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (Array.IndexOf(PictureExtensions, extension) < 0) continue;

            // The name on the box is the name of the thing. A picture called
            // `icon_diplomacy.png` is the sprite `icon_diplomacy`, wherever in the tree
            // somebody chose to file it.
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var relative = file.Replace('\\', '/');
            relative = relative.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? relative[root.Length..].TrimStart('/')
                : Path.GetFileName(file);
            sprites.TryGetValue(name, out var previous);
            sprites[name] = new Sprite(
                GameDataLoader.ResolveAssetPath(relative, virtualDirectory),
                previous.Tint, previous.Frames);
            // A replacement PNG may be white or another colour. Do not inherit the
            // base game's gold reference; the mod may declare its own source_colour.
        }

        foreach (var file in everything)
        {
            // `*.source.json` sidecars record where a generated picture came from; they are
            // provenance, not a sprite table, and their keys are not sprite names.
            if (!StructuredData.IsDocument(file) ||
                file.EndsWith(".source.json", StringComparison.OrdinalIgnoreCase)) continue;
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/models/", StringComparison.OrdinalIgnoreCase)) continue;
            var origin = $"gfx/{Path.GetFileName(file)}";
            try { MergeInto(StructuredData.ReadJson(file), virtualDirectory, origin, sprites, shaders, errors); }
            catch (Exception exception) { errors?.Add($"{origin}: {exception.Message}"); }
        }
    }

    /// <summary>
    /// Merge one declaration file. A sprite is either a bare path or an object with a
    /// texture and whatever else it needs; both spellings of the envelope are accepted and
    /// so is none at all, because a modder retinting one icon should not have to remember
    /// which wrapper it lives in.
    /// </summary>
    public static void Merge(string json, string assetRoot, string origin, List<string> errors)
    {
        _dirtyFromOverlay = true;
        MergeInto(json, assetRoot, origin, Sprites, Shaders, errors);
    }

    private static void MergeInto(
        string json, string assetRoot, string origin,
        Dictionary<string, Sprite> sprites, Dictionary<string, string> shaders, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"{origin}: expected an object of sprite declarations");
            return;
        }

        var body = root;
        if (root.TryGetProperty("shaders", out var shaderObj) && shaderObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in shaderObj.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.Null) shaders.Remove(entry.Name);
                else if (entry.Value.ValueKind == JsonValueKind.String)
                    shaders[entry.Name] = GameDataLoader.ResolveAssetPath(entry.Value.GetString(), assetRoot);
                else errors?.Add(origin + ": shader paths must be strings or null");
            }
            if (!root.TryGetProperty("sprites", out _) && !root.TryGetProperty("sprite_types", out _) &&
                !root.TryGetProperty("spriteTypes", out _)) return;
        }
        foreach (var envelope in new[] { "sprites", "sprite_types", "spriteTypes" })
            if (root.TryGetProperty(envelope, out var inner) && inner.ValueKind == JsonValueKind.Object)
            {
                body = inner;
                break;
            }

        foreach (var declaration in body.EnumerateObject())
        {
            var name = declaration.Name;
            switch (declaration.Value.ValueKind)
            {
                case JsonValueKind.String:
                    sprites[name] = new Sprite(GameDataLoader.ResolveAssetPath(declaration.Value.GetString(), assetRoot), "", 1);
                    break;

                case JsonValueKind.Object:
                {
                    var value = declaration.Value;
                    if (GameDataLoader.IsRemoved(value, $"{origin}:{name}"))
                    {
                        sprites.Remove(name);
                        break;
                    }
                    var texture = "";
                    foreach (var key in new[] { "texture", "texturefile", "file", "path" })
                        if (value.TryGetProperty(key, out var found) && found.ValueKind == JsonValueKind.String)
                        {
                            texture = found.GetString();
                            break;
                        }
                    var replacesTexture = !string.IsNullOrWhiteSpace(texture);
                    sprites.TryGetValue(name, out var known);
                    if (!replacesTexture)
                    {
                        // No file named, so this line is describing a picture the scan
                        // already found - how many frames it holds, what to tint it by.
                        if (string.IsNullOrEmpty(known.Texture))
                        {
                            errors?.Add($"{origin}: sprite '{name}' names no texture, and no " +
                                        "picture in the tree is called that either");
                            continue;
                        }
                        texture = known.Texture;
                    }

                    var tint = value.TryGetProperty("tint", out var tinted) &&
                               tinted.ValueKind == JsonValueKind.String
                        ? tinted.GetString()
                        : known.Tint;
                    var frames = value.TryGetProperty("frames", out var counted) &&
                                 counted.ValueKind == JsonValueKind.Number &&
                                 counted.TryGetInt32(out var parsed)
                        ? parsed
                        : known.Frames;
                    var sourceColour = replacesTexture ? "" : known.SourceColour;
                    if (value.TryGetProperty("source_colour", out var source))
                    {
                        var colour = source.ValueKind == JsonValueKind.String ? source.GetString() : null;
                        if (colour == "" || (colour is { Length: 7 } && colour[0] == '#' &&
                            colour.Skip(1).All(Uri.IsHexDigit) &&
                            Convert.ToInt32(colour.Substring(1, 2), 16) > 0 &&
                            Convert.ToInt32(colour.Substring(3, 2), 16) > 0 &&
                            Convert.ToInt32(colour.Substring(5, 2), 16) > 0)) sourceColour = colour;
                        else errors?.Add($"{origin}: sprite '{name}' source_colour must be empty or #RRGGBB with nonzero channels");
                    }
                    sprites[name] = new Sprite(GameDataLoader.ResolveAssetPath(texture, assetRoot), tint, frames, sourceColour);
                    break;
                }

                default:
                    errors?.Add($"{origin}: sprite '{name}' is neither a path nor a declaration");
                    break;
            }
        }
    }

}
