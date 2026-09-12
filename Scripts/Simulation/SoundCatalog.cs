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
        public Channel(string name, float volumeDb, int priority, bool independent)
        {
            Name = name ?? "";
            VolumeDb = volumeDb;
            Priority = priority;
            Independent = independent;
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
    }

    private static readonly Dictionary<string, Cue> Cues = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Channel> Channels = new(StringComparer.OrdinalIgnoreCase);

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
            files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
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
            try { Merge(File.ReadAllText(file), assetRoot, origin, errors); }
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
                        Single(declaration.Value, existing.VolumeDb), existing.Priority, existing.Independent);
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
                Channels[declaration.Name] = new Channel(declaration.Name, volume, priority, independent);
            }

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

    private static float Single(JsonElement element, float fallback) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var value) ? value : fallback;
}
