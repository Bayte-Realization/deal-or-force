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
/// The whole `gfx/` tree is read, subfolders and all, in path order: where a declaration
/// sits carries no meaning, the same way a `.gfx` file's location carries none. Later
/// files win, so a mod loaded after the base game retints one sprite and inherits the
/// other hundred.
/// </summary>
public static class SpriteCatalog
{
    /// <summary>One named picture: where it is, and how the interface should use it.</summary>
    public readonly struct Sprite
    {
        public Sprite(string texture, string tint, int frames)
        {
            Texture = texture ?? "";
            Tint = tint ?? "";
            Frames = frames < 1 ? 1 : frames;
        }

        /// <summary>The resolved path, ready for the engine to load.</summary>
        public string Texture { get; }

        /// <summary>A colour to multiply the art by, or empty to use it as authored.</summary>
        public string Tint { get; }

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
        if (root is not null) LoadTree(root, "res://", null);
        Changed?.Invoke();
    }

    /// <summary>
    /// Layer one package's `gfx/` tree over what is already loaded. <paramref name="assetRoot"/>
    /// is where that package's own art lives - `res://` for the base game, the mod's folder
    /// for a mod - and every relative texture path in it is resolved against that.
    /// </summary>
    public static void LoadTree(string directory, string assetRoot, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .OrderBy(path => path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            errors?.Add($"gfx: {exception.Message}");
            return;
        }

        foreach (var file in files)
        {
            var origin = $"gfx/{Path.GetFileName(file)}";
            try { Merge(File.ReadAllText(file), assetRoot, origin, errors); }
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
                    if (string.IsNullOrWhiteSpace(texture))
                    {
                        errors?.Add($"{origin}: sprite '{name}' declares no texture");
                        continue;
                    }

                    var tint = value.TryGetProperty("tint", out var tinted) &&
                               tinted.ValueKind == JsonValueKind.String
                        ? tinted.GetString()
                        : "";
                    var frames = value.TryGetProperty("frames", out var counted) &&
                                 counted.ValueKind == JsonValueKind.Number &&
                                 counted.TryGetInt32(out var parsed)
                        ? parsed
                        : 1;
                    Sprites[name] = new Sprite(GameDataLoader.ResolveAssetPath(texture, assetRoot), tint, frames);
                    break;
                }

                default:
                    errors?.Add($"{origin}: sprite '{name}' is neither a path nor a declaration");
                    break;
            }
        }
    }

}
