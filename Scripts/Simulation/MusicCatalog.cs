using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace DealOrForce.Simulation;

/// <summary>
/// The soundtrack, as a list somebody else can write.
///
/// A mod can add or replace recordings, tag their moods, balance their gain and change
/// the adaptive director's thresholds and timing. Older untagged playlists remain valid.
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
        public Track(string name, string file, Moment when, float weight, MusicMood[] moods = null, float volumeDb = 0, string label = "", string[] regions = null, bool loop = false)
        {
            Name = name ?? "";
            File = file ?? "";
            When = when;
            Weight = weight <= 0f ? 1f : weight;
            Moods = moods ?? Array.Empty<MusicMood>(); VolumeDb = volumeDb; Label = label.Length > 0 ? label : name;
            Regions = regions ?? Array.Empty<string>();
            Loop = loop;
        }

        public string Name { get; }

        /// <summary>The resolved path, ready for the engine to load.</summary>
        public string File { get; }

        public Moment When { get; }

        /// <summary>How often it comes round relative to the others.</summary>
        public float Weight { get; }
        public MusicMood[] Moods { get; }
        public float VolumeDb { get; }
        public string Label { get; }
        public string[] Regions { get; }
        public bool Loop { get; }
    }

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(
            new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase),
            new MusicPolicy(),
            revision: 0,
            baseLoaded: false,
            dirtyFromOverlay: false,
            loadedKey: "");

        public readonly Dictionary<string, Track> Tracks;
        public readonly MusicPolicy Director;
        public readonly int Revision;
        public readonly bool BaseLoaded;
        public readonly bool DirtyFromOverlay;
        public readonly string LoadedKey;

        public Snapshot(
            Dictionary<string, Track> tracks,
            MusicPolicy director,
            int revision,
            bool baseLoaded,
            bool dirtyFromOverlay,
            string loadedKey)
        {
            Tracks = tracks;
            Director = director;
            Revision = revision;
            BaseLoaded = baseLoaded;
            DirtyFromOverlay = dirtyFromOverlay;
            LoadedKey = loadedKey;
        }

        public MutableState ToMutable() => new()
        {
            Tracks = new Dictionary<string, Track>(Tracks, StringComparer.OrdinalIgnoreCase),
            Director = Director.Clone(),
            Revision = Revision
        };
    }

    private sealed class MutableState
    {
        public Dictionary<string, Track> Tracks = new(StringComparer.OrdinalIgnoreCase);
        public MusicPolicy Director = new();
        public int Revision;

        public Snapshot Freeze(bool baseLoaded, bool dirtyFromOverlay, string loadedKey) =>
            new(Tracks, Director, Revision, baseLoaded, dirtyFromOverlay, loadedKey);
    }

    private static readonly object SyncLock = new();
    private static Snapshot _state = Snapshot.Empty;

    /// <summary>Raised whenever the list is rebuilt, so a player can requeue.</summary>
    public static event Action Changed;
    public static MusicPolicy Director => Volatile.Read(ref _state).Director;
    public static int Revision => Volatile.Read(ref _state).Revision;

    public static int Count => Volatile.Read(ref _state).Tracks.Count;

    public static IEnumerable<Track> All => Volatile.Read(ref _state).Tracks.Values;

    /// <summary>The tracks that belong to a moment, in declaration-name order.</summary>
    public static IReadOnlyList<Track> For(Moment moment) =>
        Volatile.Read(ref _state).Tracks.Values
            .Where(track => track.When == Moment.Any || track.When == moment)
            .OrderBy(track => track.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<Track> For(Moment moment, MusicMood mood, string region = "", string subRegion = "", string country = "")
    {
        region = RegionKey(region); subRegion = RegionKey(subRegion); country = RegionKey(country);
        // An explicitly incomplete playlist does not pull a contradictory mood out of the pool.
        // Legacy untagged tracks match any mood and provide a backwards-compatible fallback.
        var matched = For(moment).Where(t => t.Moods.Length == 0 || t.Moods.Contains(MusicMood.Any) || t.Moods.Contains(mood)).ToArray();
        // Untagged tracks play anywhere. Of the regional ones only the most specific tag plays -
        // a country's own tag ("irn"), then its sub-region, then its continent: Western Asia
        // hears the Middle East's tracks rather than the continent's East Asian ones, and falls
        // back to the continent's when its sub-region has none for the mood.
        int Fit(Track t) => t.Regions.Length == 0 ? 0
            : country.Length > 0 && t.Regions.Contains(country) ? 3
            : subRegion.Length > 0 && t.Regions.Contains(subRegion) ? 2
            : region.Length > 0 && t.Regions.Contains(region) ? 1 : -1;
        var best = matched.Select(Fit).DefaultIfEmpty(0).Max();
        return matched.Where(t => Fit(t) == 0 || best > 0 && Fit(t) == best).ToArray();
    }

    /// <summary>"North America", "north-america" and "north_america" are one region tag.</summary>
    public static string RegionKey(string name) => string.IsNullOrWhiteSpace(name) ? ""
        : name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    /// <summary>Read the base game's own `music/` tree, replacing whatever was loaded.</summary>
    public static void Load() => Load(force: false, notify: true);

    public static void Load(bool force) => Load(force, notify: true);

    public static void Load(bool force, bool notify)
    {
        var root = GameDataLoader.DataFolder("music");
        var cacheKey = $"{ModOverlayPolicy.IsActive}|{root ?? ""}";
        lock (SyncLock)
        {
            var current = Volatile.Read(ref _state);
            if (!force && current.BaseLoaded && !current.DirtyFromOverlay &&
                string.Equals(current.LoadedKey, cacheKey, StringComparison.Ordinal))
                return;

            var draft = new MutableState { Revision = current.Revision + 1 };
            if (root is not null)
                DataCatalogLoader.LoadTree("music", root, null,
                    (json, origin) => MergeInto(draft, json, "res://game/music", origin, null), null);
            Volatile.Write(ref _state, draft.Freeze(baseLoaded: true, dirtyFromOverlay: false, loadedKey: cacheKey));
        }
        if (notify) Changed?.Invoke();
    }

    /// <summary>Layer one package's `music/` tree over what is already loaded.</summary>
    public static void LoadTree(string directory, string assetRoot, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var loadedAny = false;
        lock (SyncLock)
        {
            var current = Volatile.Read(ref _state);
            var draft = current.ToMutable();
            DataCatalogLoader.LoadTree("music", directory, errors,
                (json, origin) => MergeInto(draft, json, assetRoot, origin, errors),
                () => loadedAny = true);
            Volatile.Write(ref _state, draft.Freeze(current.BaseLoaded, dirtyFromOverlay: true, current.LoadedKey));
        }
        if (loadedAny) Changed?.Invoke();
    }

    /// <summary>Merge one track list. A bare path is a track that plays anywhere, once as often as any other.</summary>
    public static void Merge(string json, string assetRoot, string origin, List<string> errors)
    {
        lock (SyncLock)
        {
            var current = Volatile.Read(ref _state);
            var draft = current.ToMutable();
            MergeInto(draft, json, assetRoot, origin, errors);
            Volatile.Write(ref _state, draft.Freeze(current.BaseLoaded, dirtyFromOverlay: true, current.LoadedKey));
        }
    }

    private static void MergeInto(MutableState draft, string json, string assetRoot, string origin, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"{origin}: expected an object");
            return;
        }
        if (root.TryGetProperty("director", out var director)) draft.Director.Merge(director, errors, origin);
        draft.Revision++;

        var body = root;
        if (root.TryGetProperty("tracks", out var listed) && listed.ValueKind == JsonValueKind.Object)
            body = listed;

        foreach (var declaration in body.EnumerateObject())
        {
            var name = declaration.Name;
            var value = declaration.Value;
            if (name == "director") continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var previous = draft.Tracks.GetValueOrDefault(name);
                draft.Tracks[name] = new Track(name,
                    GameDataLoader.ResolveAssetPath(value.GetString(), assetRoot), previous.When, previous.Weight,
                    previous.Moods, previous.VolumeDb, previous.Label ?? "", previous.Regions, previous.Loop);
                continue;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"{origin}: track '{name}' is neither a path nor a declaration");
                continue;
            }
            if (GameDataLoader.IsRemoved(value, $"{origin}:{name}"))
            { draft.Tracks.Remove(name); continue; }

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
            if (!float.IsFinite(weight)) { errors?.Add($"{origin}: invalid track weight '{name}'"); continue; }
            var moods = new List<MusicMood>();
            if (value.TryGetProperty("moods", out var declaredMoods))
            {
                if (declaredMoods.ValueKind != JsonValueKind.Array) { errors?.Add($"{origin}: {name}.moods must be an array"); continue; }
                foreach (var entry in declaredMoods.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.String && Enum.TryParse<MusicMood>(entry.GetString(), true, out var mood) && Enum.IsDefined(mood)) moods.Add(mood);
                    else errors?.Add($"{origin}: unknown mood in '{name}'");
                if (declaredMoods.GetArrayLength() > 0 && moods.Count == 0) continue;
            }
            var volume = value.TryGetProperty("volume_db", out var gain) && gain.ValueKind == JsonValueKind.Number && gain.TryGetSingle(out var trim) && float.IsFinite(trim)
                ? Math.Clamp(trim, -40, 6) : 0;
            var label = value.TryGetProperty("label", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : name;
            var regions = value.TryGetProperty("regions", out var areas) && areas.ValueKind == JsonValueKind.Array
                ? areas.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => RegionKey(x.GetString()))
                    .Where(x => x.Length > 0).Distinct().Take(32).ToArray() : Array.Empty<string>();
            var loop = value.TryGetProperty("loop", out var repeat) && repeat.ValueKind == JsonValueKind.True;

            draft.Tracks[name] = new Track(name,
                GameDataLoader.ResolveAssetPath(declared.GetString(), assetRoot), when, weight, moods.Distinct().ToArray(), volume, label, regions, loop);
        }
    }
}
