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

    private static readonly Dictionary<string, Sprite> Sprites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised whenever the table is rebuilt, so cached textures can be dropped.</summary>
    public static event Action Changed;

    public static int Count => Sprites.Count;

    public static IEnumerable<string> Names => Sprites.Keys;

    public static bool Has(string name) =>
        !string.IsNullOrEmpty(name) && Sprites.ContainsKey(name);

    public static Sprite Get(string name) =>
        !string.IsNullOrEmpty(name) && Sprites.TryGetValue(name, out var sprite)
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

    /// <summary>Read the base game's own `gfx/` tree, replacing whatever was loaded.</summary>
    public static void Load()
    {
        Sprites.Clear();
        var root = GameDataLoader.DataFolder("gfx");
        if (root is not null) LoadTree(root, "res://game/gfx", null);
        Changed?.Invoke();
    }

    /// <summary>What the engine will load a picture as, from a file name.</summary>
    private static readonly string[] PictureExtensions =
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga", ".dds", ".ktx", ".svg" };

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
            var previous = Get(name);
            Sprites[name] = new Sprite(
                GameDataLoader.ResolveAssetPath(relative, virtualDirectory),
                previous.Tint, previous.Frames);
            // A replacement PNG may be white or another colour. Do not inherit the
            // base game's gold reference; the mod may declare its own source_colour.
        }

        foreach (var file in everything)
        {
            if (!StructuredData.IsDocument(file)) continue;
            var origin = $"gfx/{Path.GetFileName(file)}";
            try { Merge(StructuredData.ReadJson(file), virtualDirectory, origin, errors); }
            catch (Exception exception) { errors?.Add($"{origin}: {exception.Message}"); }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Merge one declaration file. A sprite is either a bare path or an object with a
    /// texture and whatever else it needs; both spellings of the envelope are accepted and
    /// so is none at all, because a modder retinting one icon should not have to remember
    /// which wrapper it lives in.
    /// </summary>
    public static void Merge(string json, string assetRoot, string origin, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"{origin}: expected an object of sprite declarations");
            return;
        }

        var body = root;
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
                    Sprites[name] = new Sprite(GameDataLoader.ResolveAssetPath(declaration.Value.GetString(), assetRoot), "", 1);
                    break;

                case JsonValueKind.Object:
                {
                    var value = declaration.Value;
                    var texture = "";
                    foreach (var key in new[] { "texture", "texturefile", "file", "path" })
                        if (value.TryGetProperty(key, out var found) && found.ValueKind == JsonValueKind.String)
                        {
                            texture = found.GetString();
                            break;
                        }
                    var replacesTexture = !string.IsNullOrWhiteSpace(texture);
                    if (!replacesTexture)
                    {
                        // No file named, so this line is describing a picture the scan
                        // already found - how many frames it holds, what to tint it by.
                        var scanned = Get(name);
                        if (string.IsNullOrEmpty(scanned.Texture))
                        {
                            errors?.Add($"{origin}: sprite '{name}' names no texture, and no " +
                                        "picture in the tree is called that either");
                            continue;
                        }
                        texture = scanned.Texture;
                    }

                    var known = Get(name);
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
                    Sprites[name] = new Sprite(GameDataLoader.ResolveAssetPath(texture, assetRoot), tint, frames, sourceColour);
                    break;
                }

                default:
                    errors?.Add($"{origin}: sprite '{name}' is neither a path nor a declaration");
                    break;
            }
        }
    }

}
