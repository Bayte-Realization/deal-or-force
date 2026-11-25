using System;
using System.IO;
using Godot;

namespace DealOrForce;

/// <summary>Declared package recordings work both loose and inside an imported PCK.
/// A modder does not need the Godot editor just to add an MP3, Ogg or WAV file.</summary>
internal static class AudioAssets
{
    private static string Loose(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (!path.StartsWith("res://", StringComparison.Ordinal) && !path.StartsWith("user://", StringComparison.Ordinal))
            return path;
        var globalized = ProjectSettings.GlobalizePath(path);
        if (File.Exists(globalized)) return globalized;
        // Outside the editor, `res://` resolves inside the pack while loose package audio sits under `game/`.
        if (path.StartsWith("res://game/", StringComparison.Ordinal))
        {
            var dataFile = Simulation.GameDataLoader.FindDataFile(path["res://game/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries));
            if (!string.IsNullOrEmpty(dataFile)) return dataFile;
        }
        return globalized;
    }

    internal static bool Exists(string path) => !string.IsNullOrWhiteSpace(path) &&
        (File.Exists(Loose(path)) || ResourceLoader.Exists(path));

    // Callers already own catalogue path validation and stream lifetime. Prefer the
    // physical package file, so a loose replacement isn't masked by an old import.
    internal static AudioStream Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var loose = Loose(path);
        if (File.Exists(loose)) return Path.GetExtension(loose).ToLowerInvariant() switch
        {
            ".mp3" => AudioStreamMP3.LoadFromFile(loose),
            ".ogg" => AudioStreamOggVorbis.LoadFromFile(loose),
            ".wav" => AudioStreamWav.LoadFromFile(loose),
            _ => ResourceLoader.Exists(path) ? GD.Load<AudioStream>(path) : null
        };
        return ResourceLoader.Exists(path) ? GD.Load<AudioStream>(path) : null;
    }
}
