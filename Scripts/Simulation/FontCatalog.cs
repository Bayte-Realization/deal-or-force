using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Which typeface the interface is set in.
///
/// The base game declares none and uses the engine's own, which covers the two languages
/// it ships in. That is the whole reason this folder has to exist anyway: a mod adding a
/// language the default face has no glyphs for - Chinese, Japanese, Arabic, Hindi - can
/// translate every string in the game and still render a screen of empty boxes, and there
/// was no way to supply a font without forking the interface.
///
/// A role is a name the interface asks for, not a file. There is one today, `ui.default`,
/// and adding a second means the interface has a second thing to ask for - not that this
/// table grew a field.
/// </summary>
public static class FontCatalog
{
    /// <summary>One declared face.</summary>
    public readonly struct Face
    {
        public Face(string role, string file, string[] families = null)
        {
            Role = role ?? "";
            File = file ?? "";
            Families = families ?? Array.Empty<string>();
        }

        public string Role { get; }

        /// <summary>The resolved path, ready for the engine to load.</summary>
        public string File { get; }
        public string[] Families { get; }
    }

    private static readonly Dictionary<string, Face> Faces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised whenever the table is rebuilt, so a loaded face can be dropped.</summary>
    public static event Action Changed;

    public static int Count => Faces.Count;

    public static IEnumerable<Face> All => Faces.Values;

    /// <summary>The file for a role, or empty when nothing declares it and the engine's own is used.</summary>
    public static string FileOf(string role) =>
        !string.IsNullOrEmpty(role) && Faces.TryGetValue(role, out var face) ? face.File : "";
    public static string[] FamiliesOf(string role) => Faces.TryGetValue(role, out var face) ? face.Families : Array.Empty<string>();

    /// <summary>Read the base game's own `fonts/` tree, replacing whatever was loaded.</summary>
    public static void Load()
    {
        Faces.Clear();
        var root = GameDataLoader.DataFolder("fonts");
        if (root is not null) LoadTree(root, "res://game/fonts", null);
        Changed?.Invoke();
    }

    /// <summary>Layer one package's `fonts/` tree over what is already loaded.</summary>
    public static void LoadTree(string directory, string assetRoot, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        string[] files;
        try
        {
            files = StructuredData.Files(directory, SearchOption.AllDirectories)
                .OrderBy(path => path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            errors?.Add($"fonts: {exception.Message}");
            return;
        }

        foreach (var file in files)
        {
            var origin = $"fonts/{Path.GetFileName(file)}";
            try { Merge(StructuredData.ReadJson(file), assetRoot, origin, errors); }
            catch (Exception exception) { errors?.Add($"{origin}: {exception.Message}"); }
        }

        Changed?.Invoke();
    }

    /// <summary>Merge one declaration file. A role is a path, or an object holding one.</summary>
    public static void Merge(string json, string assetRoot, string origin, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"{origin}: expected an object");
            return;
        }

        var body = root;
        if (root.TryGetProperty("fonts", out var declared) && declared.ValueKind == JsonValueKind.Object)
            body = declared;

        foreach (var declaration in body.EnumerateObject())
        {
            var role = declaration.Name;
            var value = declaration.Value;

            if (value.ValueKind == JsonValueKind.String)
            {
                Faces[role] = new Face(role, GameDataLoader.ResolveAssetPath(value.GetString(), assetRoot));
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("system_families", out var families) && families.ValueKind == JsonValueKind.Array)
            {
                var familyNames = families.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).ToArray();
                var fontFile = value.TryGetProperty("file", out var declaredFile) && declaredFile.ValueKind == JsonValueKind.String
                    ? GameDataLoader.ResolveAssetPath(declaredFile.GetString(), assetRoot) : "";
                Faces[role] = new Face(role, fontFile, familyNames);
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String)
            {
                Faces[role] = new Face(role, GameDataLoader.ResolveAssetPath(file.GetString(), assetRoot));
                continue;
            }

            errors?.Add($"{origin}: font role '{role}' is neither a path nor a declaration");
        }
    }
}
