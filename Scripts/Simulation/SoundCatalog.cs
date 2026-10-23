using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// What the game plays when something happens, and how it behaves when it does.
///
/// This is the `sound/` layer, and the same argument as `gfx/`: the code says *which
/// moment* it is - a war was declared, a battle was lost - and the data says what that
/// sounds like. Everything a cue needs to behave was hard-coded before: the six names, the
/// file each one loaded, which of the three players it went to, how loud, and how often it
/// was allowed to repeat. The repeat rule in particular was a `switch` on the cue's own
/// name in the middle of the interface, which is the least reachable place a modder could
/// look for it.
///
/// A cue is a declaration now, so a mod can retime the battle report, quieten the
/// interface click, or add a moment of its own without touching the executable. Channels
/// are declared too, because "the strategic channel is seven decibels down" is a mixing
/// decision and mixing decisions belong to whoever is making the sound.
/// </summary>
public static class SoundCatalog
{
    /// <summary>One named moment: what it plays, where, and how it behaves.</summary>
    public readonly struct Cue
    {
        public Cue(string name, string file, string channel, float volumeDb, int cooldownMs, bool yielding)
        {
            Name = name ?? "";
            File = file ?? "";
            Channel = string.IsNullOrWhiteSpace(channel) ? "strategic" : channel;
            VolumeDb = volumeDb;
            CooldownMs = cooldownMs < 0 ? 0 : cooldownMs;
            Yielding = yielding;
        }

        public string Name { get; }

        /// <summary>The resolved path, ready for the engine to load.</summary>
        public string File { get; }

        /// <summary>Which mixing channel it belongs to.</summary>
        public string Channel { get; }

        /// <summary>Trim for this cue alone, on top of its channel's level.</summary>
        public float VolumeDb { get; }

        /// <summary>How long this cue must wait before it is allowed to sound again.</summary>
        public int CooldownMs { get; }

        /// <summary>
        /// Whether the cue gives way to anything already playing. Battles resolve in
        /// batches and a hundred of them must never talk over a declaration of war.
        /// </summary>
        public bool Yielding { get; }
    }

    /// <summary>One mixing channel: how loud it is, and how it behaves against the others.</summary>
    public readonly struct Channel
    {
        public Channel(string name, float volumeDb, int priority, bool independent,
            float musicDuckDb = 0, float ambienceDuckDb = 0)
        {
            Name = name ?? "";
            VolumeDb = volumeDb;
            Priority = priority;
            Independent = independent;
            MusicDuckDb = DuckLevel(musicDuckDb);
            AmbienceDuckDb = DuckLevel(ambienceDuckDb);
        }

        public string Name { get; }

        public float VolumeDb { get; }

        /// <summary>
        /// Who wins when two channels want the room. A cue silences any channel below its
        /// own: a declaration of war cuts off a battle report, never the other way round.
        /// </summary>
        public int Priority { get; }

        /// <summary>
        /// A channel that neither waits for the others nor makes them wait. The interface
        /// click is the whole reason this exists - it is feedback for something the player
        /// just did, and feedback that arrives late is worse than none.
        /// </summary>
        public bool Independent { get; }
        public float MusicDuckDb { get; }
        public float AmbienceDuckDb { get; }

        private static float DuckLevel(float value) => float.IsFinite(value) ? Math.Clamp(value, -24, 0) : 0;
    }

    private static readonly Dictionary<string, Cue> Cues = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Channel> Channels = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Beds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (string Bed, float Share)[]> TerrainMixes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The sound of the ground under the tactical camera (MapAmbience, `map_ambience` in sound/):
    /// each bed a looping recording by name, each terrain the beds it lends and their shares.
    /// Open water is <see cref="SeaBed"/>; ground whose terrain names no mix is <see cref="OpenCountryBed"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AmbienceBeds => Beds;
    public static IReadOnlyDictionary<string, (string Bed, float Share)[]> TerrainAmbience => TerrainMixes;
    public static string OpenCountryBed { get; private set; } = "";
    public static string SeaBed { get; private set; } = "";

    /// <summary>The only bed a world under the ice of `snowgrave` still has, at every height (WorldCurses).</summary>
    public static string FrozenBed { get; private set; } = "";

    /// <summary>Raised whenever the tables are rebuilt, so loaded streams can be dropped.</summary>
    public static event Action Changed;

    public static int Count => Cues.Count;

    public static IEnumerable<Cue> All => Cues.Values;

    public static IEnumerable<Channel> AllChannels => Channels.Values;

    public static bool Has(string name) => !string.IsNullOrEmpty(name) && Cues.ContainsKey(name);

    public static Cue Get(string name) =>
        !string.IsNullOrEmpty(name) && Cues.TryGetValue(name, out var cue) ? cue : default;

    /// <summary>One channel, or an unattenuated default when nothing declares it.</summary>
    public static Channel GetChannel(string name) =>
        !string.IsNullOrEmpty(name) && Channels.TryGetValue(name, out var channel)
            ? channel
            : new Channel(name ?? "", 0f, 0, false);

    /// <summary>Read the base game's own `sound/` tree, replacing whatever was loaded.</summary>
    public static void Load()
    {
        Cues.Clear();
        Channels.Clear();
        Beds.Clear();
        TerrainMixes.Clear();
        OpenCountryBed = SeaBed = FrozenBed = "";
        var root = GameDataLoader.DataFolder("sound");
        if (root is not null) LoadTree(root, "res://game/sound", null);
        Changed?.Invoke();
    }

    /// <summary>Layer one package's `sound/` tree over what is already loaded.</summary>
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
            errors?.Add($"sound: {exception.Message}");
            return;
        }

        foreach (var file in files)
        {
            var origin = $"sound/{Path.GetFileName(file)}";
            try { Merge(StructuredData.ReadJson(file), assetRoot, origin, errors); }
            catch (Exception exception) { errors?.Add($"{origin}: {exception.Message}"); }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Merge one declaration file. Both tables are optional and independent, so a mod that
    /// only wants the interface click quieter writes four lines and inherits every cue.
    /// </summary>
    public static void Merge(string json, string assetRoot, string origin, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"{origin}: expected an object");
            return;
        }

        if (root.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Object)
            foreach (var declaration in channels.EnumerateObject())
            {
                var existing = Channels.TryGetValue(declaration.Name, out var known)
                    ? known
                    : new Channel(declaration.Name, 0f, 0, false);

                if (declaration.Value.ValueKind == JsonValueKind.Number)
                {
                    Channels[declaration.Name] = new Channel(declaration.Name,
                        Single(declaration.Value, existing.VolumeDb), existing.Priority, existing.Independent,
                        existing.MusicDuckDb, existing.AmbienceDuckDb);
                    continue;
                }
                if (declaration.Value.ValueKind != JsonValueKind.Object)
                {
                    errors?.Add($"{origin}: channel '{declaration.Name}' is neither a level nor a declaration");
                    continue;
                }

                var body = declaration.Value;
                var volume = body.TryGetProperty("volume_db", out var level)
                    ? Single(level, existing.VolumeDb)
                    : existing.VolumeDb;
                var priority = body.TryGetProperty("priority", out var rank) &&
                               rank.ValueKind == JsonValueKind.Number && rank.TryGetInt32(out var ranked)
                    ? ranked
                    : existing.Priority;
                var independent = body.TryGetProperty("independent", out var alone) &&
                                  alone.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? alone.GetBoolean()
                    : existing.Independent;
                var musicDuck = body.TryGetProperty("music_duck_db", out var duckMusic)
                    ? Single(duckMusic, existing.MusicDuckDb) : existing.MusicDuckDb;
                var ambienceDuck = body.TryGetProperty("ambience_duck_db", out var duckAmbience)
                    ? Single(duckAmbience, existing.AmbienceDuckDb) : existing.AmbienceDuckDb;
                Channels[declaration.Name] = new Channel(declaration.Name, volume, priority, independent,
                    musicDuck, ambienceDuck);
            }

        if (root.TryGetProperty("map_ambience", out var ambience)) MergeAmbience(ambience, assetRoot, origin, errors);

        if (!root.TryGetProperty("cues", out var cues) || cues.ValueKind != JsonValueKind.Object) return;

        foreach (var declaration in cues.EnumerateObject())
        {
            var name = declaration.Name;
            var value = declaration.Value;

            // A bare path is the common case: a mod replacing the sound of a moment it
            // already understands should not have to restate how that moment behaves.
            if (value.ValueKind == JsonValueKind.String)
            {
                var previous = Get(name);
                Cues[name] = new Cue(name,
                    GameDataLoader.ResolveAssetPath(value.GetString(), assetRoot),
                    previous.Channel, previous.VolumeDb, previous.CooldownMs, previous.Yielding);
                continue;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"{origin}: cue '{name}' is neither a path nor a declaration");
                continue;
            }

            var existing = Get(name);
            var file = value.TryGetProperty("file", out var declared) &&
                       declared.ValueKind == JsonValueKind.String
                ? GameDataLoader.ResolveAssetPath(declared.GetString(), assetRoot)
                : existing.File;
            if (string.IsNullOrWhiteSpace(file))
            {
                errors?.Add($"{origin}: cue '{name}' declares no file");
                continue;
            }

            var channel = value.TryGetProperty("channel", out var routed) &&
                          routed.ValueKind == JsonValueKind.String
                ? routed.GetString()
                : existing.Channel;
            var volumeDb = value.TryGetProperty("volume_db", out var loudness)
                ? Single(loudness, existing.VolumeDb)
                : existing.VolumeDb;
            var cooldown = value.TryGetProperty("cooldown_ms", out var wait) &&
                           wait.ValueKind == JsonValueKind.Number && wait.TryGetInt32(out var parsed)
                ? parsed
                : existing.CooldownMs;
            var yielding = value.TryGetProperty("yields_to_anything_playing", out var polite) &&
                           polite.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? polite.GetBoolean()
                : existing.Yielding;

            Cues[name] = new Cue(name, file, channel, volumeDb, cooldown, yielding);
        }
    }

    /// <summary>
    /// The `map_ambience` block: `beds` (name: file), `terrain` (terrain id: {bed: share}, or null
    /// to drop a mix), and the `open_country` and `sea` beds. Later files override by name.
    /// </summary>
    private static void MergeAmbience(JsonElement block, string assetRoot, string origin, List<string> errors)
    {
        if (block.ValueKind != JsonValueKind.Object) { errors?.Add($"{origin}: map_ambience must be an object"); return; }
        if (block.TryGetProperty("beds", out var beds) && beds.ValueKind == JsonValueKind.Object)
            foreach (var bed in beds.EnumerateObject())
            {
                if (bed.Value.ValueKind == JsonValueKind.Null) { Beds.Remove(bed.Name); continue; }
                if (bed.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(bed.Value.GetString()))
                { errors?.Add($"{origin}: ambience bed '{bed.Name}' needs a file"); continue; }
                Beds[bed.Name] = GameDataLoader.ResolveAssetPath(bed.Value.GetString(), assetRoot);
            }
        if (block.TryGetProperty("terrain", out var terrain) && terrain.ValueKind == JsonValueKind.Object)
            foreach (var entry in terrain.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.Null) { TerrainMixes.Remove(entry.Name); continue; }
                if (entry.Value.ValueKind != JsonValueKind.Object)
                { errors?.Add($"{origin}: ambience for terrain '{entry.Name}' must be an object of bed: share"); continue; }
                var mix = new List<(string Bed, float Share)>();
                foreach (var share in entry.Value.EnumerateObject())
                    if (share.Value.ValueKind == JsonValueKind.Number && share.Value.TryGetSingle(out var value) &&
                        float.IsFinite(value) && value > 0f)
                        mix.Add((share.Name, Math.Min(value, 1f)));
                    else errors?.Add($"{origin}: ambience share '{entry.Name}.{share.Name}' must be a positive number");
                TerrainMixes[entry.Name] = mix.ToArray();
            }
        if (block.TryGetProperty("open_country", out var open) && open.ValueKind == JsonValueKind.String)
            OpenCountryBed = open.GetString() ?? "";
        if (block.TryGetProperty("sea", out var sea) && sea.ValueKind == JsonValueKind.String)
            SeaBed = sea.GetString() ?? "";
        if (block.TryGetProperty("frozen", out var frozen) && frozen.ValueKind == JsonValueKind.String)
            FrozenBed = frozen.GetString() ?? "";
    }

    private static float Single(JsonElement element, float fallback) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var value) ? value : fallback;
}
