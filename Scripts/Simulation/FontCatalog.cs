using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

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

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(
            new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase),
            baseLoaded: false, dirtyFromOverlay: false, loadedKey: "");

        public readonly Dictionary<string, Face> Faces;
        public readonly bool BaseLoaded;
        public readonly bool DirtyFromOverlay;
        public readonly string LoadedKey;

        public Snapshot(
            Dictionary<string, Face> faces,
            bool baseLoaded, bool dirtyFromOverlay, string loadedKey)
        {
            Faces = faces;
            BaseLoaded = baseLoaded;
            DirtyFromOverlay = dirtyFromOverlay;
            LoadedKey = loadedKey;
        }
    }

    private static readonly object SyncLock = new();
    private static Snapshot _state = Snapshot.Empty;

    /// <summary>Raised whenever the table is rebuilt, so a loaded face can be dropped.</summary>
    public static event Action Changed;

    public static int Count => Volatile.Read(ref _state).Faces.Count;

    public static IEnumerable<Face> All => Volatile.Read(ref _state).Faces.Values;

    /// <summary>The file for a role, or empty when nothing declares it and the engine's own is used.</summary>
    public static string FileOf(string role) =>
        !string.IsNullOrEmpty(role) && Volatile.Read(ref _state).Faces.TryGetValue(role, out var face) ? face.File : "";

    public static string[] FamiliesOf(string role) =>
        !string.IsNullOrEmpty(role) && Volatile.Read(ref _state).Faces.TryGetValue(role, out var face)
            ? face.Families
            : Array.Empty<string>();

    /// <summary>Read the base game's own `fonts/` tree, replacing whatever was loaded.</summary>
    public static void Load() => Load(force: false, notify: true);

    public static void Load(bool force) => Load(force, notify: true);

    public static void Load(bool force, bool notify)
    {
        var root = GameDataLoader.DataFolder("fonts");
        var cacheKey = $"{ModOverlayPolicy.IsActive}|{root ?? ""}";
        lock (SyncLock)
        {
            var current = Volatile.Read(ref _state);
            if (!force && current.BaseLoaded && !current.DirtyFromOverlay &&
                string.Equals(current.LoadedKey, cacheKey, StringComparison.Ordinal))
                return;

            var faces = new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase);
            if (root is not null)
                DataCatalogLoader.LoadTree("fonts", root, null,
                    (json, origin) => MergeInto(faces, json, "res://game/fonts", origin, null), null);
            Volatile.Write(ref _state, new Snapshot(
                faces, baseLoaded: true, dirtyFromOverlay: false, loadedKey: cacheKey));
        }
        if (notify) Changed?.Invoke();
    }

    /// <summary>Layer one package's `fonts/` tree over what is already loaded.</summary>
    public static void LoadTree(string directory, string assetRoot, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var loadedAny = false;
        lock (SyncLock)
        {
            var current = Volatile.Read(ref _state);
            var faces = new Dictionary<string, Face>(current.Faces, StringComparer.OrdinalIgnoreCase);
            DataCatalogLoader.LoadTree("fonts", directory, errors,
                (json, origin) => MergeInto(faces, json, assetRoot, origin, errors),
                () => loadedAny = true);
            Volatile.Write(ref _state, new Snapshot(
                faces, current.BaseLoaded, dirtyFromOverlay: true, current.LoadedKey));
        }
        if (loadedAny) Changed?.Invoke();
    }

    /// <summary>Merge one declaration file. A role is a path, or an object holding one.</summary>
    public static void Merge(string json, string assetRoot, string origin, List<string> errors)
    {
        lock (SyncLock)
        {
            var current = Volatile.Read(ref _state);
            var faces = new Dictionary<string, Face>(current.Faces, StringComparer.OrdinalIgnoreCase);
            MergeInto(faces, json, assetRoot, origin, errors);
            Volatile.Write(ref _state, new Snapshot(
                faces, current.BaseLoaded, dirtyFromOverlay: true, current.LoadedKey));
        }
    }

    private static void MergeInto(Dictionary<string, Face> faces, string json, string assetRoot, string origin, List<string> errors)
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

            if (value.ValueKind == JsonValueKind.Object && GameDataLoader.IsRemoved(value, $"{origin}:{role}"))
            {
                faces.Remove(role);
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                faces[role] = new Face(role, GameDataLoader.ResolveAssetPath(value.GetString(), assetRoot));
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("system_families", out var families) && families.ValueKind == JsonValueKind.Array)
            {
                var familyNames = families.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).ToArray();
                var fontFile = value.TryGetProperty("file", out var declaredFile) && declaredFile.ValueKind == JsonValueKind.String
                    ? GameDataLoader.ResolveAssetPath(declaredFile.GetString(), assetRoot) : "";
                faces[role] = new Face(role, fontFile, familyNames);
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String)
            {
                faces[role] = new Face(role, GameDataLoader.ResolveAssetPath(file.GetString(), assetRoot));
                continue;
            }

            errors?.Add($"{origin}: font role '{role}' is neither a path nor a declaration");
        }
    }
}
