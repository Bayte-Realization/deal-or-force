using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// The soundtrack, as a list somebody else can write.
///
/// The base game ships without music, which makes this the one category where the shipped
/// content is nothing at all - and exactly why it has to exist as a folder rather than as
/// a gap. A mod that wants a soundtrack has no way to add one if the game only plays what
/// its own code names; with `music/` it drops its files in, lists them, and the player
/// picks from whatever is declared.
///
/// A track says when it belongs - in the menu, in a campaign, or either - and how often it
/// should come round relative to the rest. That is the whole model: no situational scoring,
/// no stingers keyed to events, because a game with no music of its own has no business
/// inventing an elaborate way to schedule it.
/// </summary>
public static class MusicCatalog
{
    /// <summary>Where a track belongs.</summary>
    public enum Moment
    {
        Any,
        Menu,
        Campaign
    }

    /// <summary>One declared track.</summary>
    public readonly struct Track
    {
        public Track(string name, string file, Moment when, float weight)
        {
            Name = name ?? "";
            File = file ?? "";
            When = when;
            Weight = weight <= 0f ? 1f : weight;
        }

        public string Name { get; }

        /// <summary>The resolved path, ready for the engine to load.</summary>
        public string File { get; }

        public Moment When { get; }

        /// <summary>How often it comes round relative to the others.</summary>
        public float Weight { get; }
    }

    private static readonly Dictionary<string, Track> Tracks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised whenever the list is rebuilt, so a player can requeue.</summary>
    public static event Action Changed;

    public static int Count => Tracks.Count;

    public static IEnumerable<Track> All => Tracks.Values;

    /// <summary>The tracks that belong to a moment, in declaration-name order.</summary>
    public static IReadOnlyList<Track> For(Moment moment) =>
        Tracks.Values
            .Where(track => track.When == Moment.Any || track.When == moment)
            .OrderBy(track => track.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Read the base game's own `music/` tree, replacing whatever was loaded.</summary>
    public static void Load()
    {
        Tracks.Clear();
        var root = GameDataLoader.DataFolder("music");
        if (root is not null) LoadTree(root, "res://game/music", null);
        Changed?.Invoke();
    }

    /// <summary>Layer one package's `music/` tree over what is already loaded.</summary>
    public static void LoadTree(string directory, string assetRoot, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .OrderBy(path => path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            errors?.Add($"music: {exception.Message}");
            return;
        }

        foreach (var file in files)
        {
            var origin = $"music/{Path.GetFileName(file)}";
            try { Merge(File.ReadAllText(file), assetRoot, origin, errors); }
            catch (Exception exception) { errors?.Add($"{origin}: {exception.Message}"); }
        }

        Changed?.Invoke();
    }

    /// <summary>Merge one track list. A bare path is a track that plays anywhere, once as often as any other.</summary>
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
        if (root.TryGetProperty("tracks", out var listed) && listed.ValueKind == JsonValueKind.Object)
            body = listed;

        foreach (var declaration in body.EnumerateObject())
        {
            var name = declaration.Name;
            var value = declaration.Value;

            if (value.ValueKind == JsonValueKind.String)
            {
                Tracks[name] = new Track(name,
                    GameDataLoader.ResolveAssetPath(value.GetString(), assetRoot), Moment.Any, 1f);
                continue;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"{origin}: track '{name}' is neither a path nor a declaration");
                continue;
            }

            if (!value.TryGetProperty("file", out var declared) || declared.ValueKind != JsonValueKind.String)
            {
                errors?.Add($"{origin}: track '{name}' declares no file");
                continue;
            }

            var when = Moment.Any;
            if (value.TryGetProperty("when", out var moment) && moment.ValueKind == JsonValueKind.String &&
                !Enum.TryParse(moment.GetString(), true, out when))
            {
                errors?.Add($"{origin}: track '{name}' plays at an unknown moment '{moment.GetString()}'");
                when = Moment.Any;
            }

            var weight = value.TryGetProperty("weight", out var often) &&
                         often.ValueKind == JsonValueKind.Number && often.TryGetSingle(out var parsed)
                ? parsed
                : 1f;

            Tracks[name] = new Track(name,
                GameDataLoader.ResolveAssetPath(declared.GetString(), assetRoot), when, weight);
        }
    }
}
