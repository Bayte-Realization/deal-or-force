using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DealOrForce.Launcher;

// The things a launcher can do that the game cannot - read before the engine starts, or
// after it has refused to - and none of them touches WPF, so LauncherTests compiles this
// file as it is. The window that shows them is MainWindow.

/// <summary>
/// The game's own settings.json, the file its options screen keeps. The launcher writes only
/// the keys it offers (the language, the window) and keeps every other key exactly as it was;
/// a file that exists and will not parse is left alone rather than replaced.
/// </summary>
public static class GameSettings
{
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>`settings/settings.json` inside the one game folder.</summary>
    public static string FileIn(string profile) => Path.Combine(profile, "settings", "settings.json");

    /// <summary>Where the file lived before the folders were consolidated.</summary>
    public static string OldFileIn(string profile) => Path.Combine(profile, "settings.json");

    /// <summary>A string setting as the game last saved it, or null.</summary>
    public static string? Read(string profile, string key)
    {
        foreach (var path in new[] { FileIn(profile), OldFileIn(profile) })
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path), Lenient);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                return document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
            }
            catch (IOException) { return null; }
            catch (JsonException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Write one key. The game reads `settings/settings.json`; an install from before the
    /// consolidation has the file in the folder's root, so the old file is the starting point
    /// when the new one does not exist yet - and the old file itself is never touched.
    /// </summary>
    public static bool Write(string profile, string key, string value)
    {
        try
        {
            var path = FileIn(profile);
            var source = File.Exists(path) ? path : OldFileIn(profile);
            JsonObject settings;
            if (File.Exists(source))
            {
                if (JsonNode.Parse(File.ReadAllText(source), null, Lenient) is not JsonObject existing) return false;
                settings = existing;
            }
            else settings = new JsonObject();
            settings[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
            return true;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// How the next start goes, remembered between runs in `launcher_options.json` beside the
/// content selection.
///
/// The renderer and the safe map are command-line choices on purpose. The start-up guard
/// (Scripts/Boot/boot_guard.gd) treats --rendering-driver / --rendering-method as the
/// player's own choice and never steps down from it, and "Automatic" passes nothing, so the
/// game's own Settings > Graphics and the guard's fallback chain stay in charge. The window
/// mode is not a flag: the game applies `window_mode` from its settings.json at start and
/// would undo one, so the launcher writes that key there instead.
/// </summary>
public sealed class StartOptions
{
    /// <summary>The game's own renderer values (game/common/settings/00_settings.json).</summary>
    public static readonly string[] Renderers = { "auto", "d3d12", "vulkan", "opengl3", "opengl3_angle" };
    /// <summary>The game's own window_mode values, its default first.</summary>
    public static readonly string[] WindowModes = { "borderless", "fullscreen", "windowed" };

    public string Renderer { get; set; } = "auto";
    public bool SafeMap { get; set; }

    public static string FileIn(string profile) => Path.Combine(profile, "launcher_options.json");

    public static StartOptions Load(string profile)
    {
        var options = new StartOptions();
        try
        {
            var path = FileIn(profile);
            if (!File.Exists(path)) return options;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("renderer", out var renderer) && renderer.ValueKind == JsonValueKind.String &&
                Array.IndexOf(Renderers, renderer.GetString()) >= 0) options.Renderer = renderer.GetString()!;
            if (root.TryGetProperty("safe_map", out var safe) && safe.ValueKind is JsonValueKind.True or JsonValueKind.False)
                options.SafeMap = safe.GetBoolean();
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }
        return options;
    }

    public void Save(string profile)
    {
        Directory.CreateDirectory(profile);
        var path = FileIn(profile);
        File.WriteAllText(path + ".tmp", new JsonObject { ["renderer"] = Renderer, ["safe_map"] = SafeMap }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>Engine options, before any `--`.</summary>
    public IEnumerable<string> EngineArguments() => Renderer switch
    {
        "d3d12" => new[] { "--rendering-method", "forward_plus", "--rendering-driver", "d3d12" },
        "vulkan" => new[] { "--rendering-method", "forward_plus", "--rendering-driver", "vulkan" },
        "opengl3" => new[] { "--rendering-method", "gl_compatibility", "--rendering-driver", "opengl3" },
        "opengl3_angle" => new[] { "--rendering-method", "gl_compatibility", "--rendering-driver", "opengl3_angle" },
        _ => Array.Empty<string>(),
    };

    /// <summary>The game's own options, read through OS.GetCmdlineUserArgs - after `--`.</summary>
    public IEnumerable<string> GameArguments() => SafeMap ? new[] { "--safe-map" } : Array.Empty<string>();

    public void AppendTo(ProcessStartInfo info)
    {
        foreach (var argument in EngineArguments()) info.ArgumentList.Add(argument);
        var game = GameArguments().ToList();
        if (game.Count == 0) return;
        info.ArgumentList.Add("--");
        foreach (var argument in game) info.ArgumentList.Add(argument);
    }
}

/// <summary>One file of a delivered build against the checksum it was shipped with.</summary>
public sealed record FileCheck(string File, string Expected, string Actual)
{
    public bool Missing => Actual.Length == 0;
    public bool Matches => !Missing && string.Equals(Expected, Actual, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// `BUILD_INFO.json` beside a delivered build: which release it is and the SHA-256 of the
/// executable, the pack, the launcher and the two game libraries. A loose project has none.
/// </summary>
public sealed class BuildManifest
{
    public string Release { get; init; } = "";
    public string BuildId { get; init; } = "";
    public string Date { get; init; } = "";
    public string Engine { get; init; } = "";
    public string MapDataVersion { get; init; } = "";
    public Dictionary<string, string> Sha256 { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public const string FileName = "BUILD_INFO.json";

    public static BuildManifest? Load(string root)
    {
        var path = Path.Combine(root, FileName);
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true });
            var value = document.RootElement;
            string Text(string key) => value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (value.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.Object)
                foreach (var entry in sha.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.String) hashes[entry.Name] = entry.Value.GetString() ?? "";
            return new BuildManifest
            {
                Release = Text("release"), BuildId = Text("build_id"), Date = Text("date"), Engine = Text("engine"),
                MapDataVersion = Text("map_data_version"), Sha256 = hashes,
            };
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Hash every listed file. A name that would leave the build folder is reported as missing
    /// rather than read: the manifest is a file anyone can edit.
    /// </summary>
    public List<FileCheck> Verify(string root, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var results = new List<FileCheck>();
        foreach (var (name, expected) in Sha256.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(name);
            var path = Path.GetFullPath(Path.Combine(full, name.Replace('/', Path.DirectorySeparatorChar)));
            var actual = "";
            if (path.StartsWith(full, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
                    actual = Convert.ToHexString(SHA256.HashData(stream));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            results.Add(new FileCheck(name, expected, actual));
        }
        return results;
    }
}

/// <summary>Where "what's new" for this release is written.</summary>
public static class Changelog
{
    /// <summary>
    /// The project keeps one file per release and language in docs/; a delivered build carries
    /// the same text as CHANGELOG.md / CHANGELOG.ru.md beside the executable. The other
    /// language is the fallback, so the player reads something rather than nothing.
    /// </summary>
    public static string? Find(string root, bool russian, string version)
    {
        string[] Paths(bool ru) => new[]
        {
            Path.Combine(root, "docs", $"CHANGELOG_{version}_{(ru ? "RU" : "EN")}.md"),
            Path.Combine(root, ru ? "CHANGELOG.ru.md" : "CHANGELOG.md"),
        };
        return Paths(russian).Concat(Paths(!russian)).FirstOrDefault(File.Exists);
    }
}

/// <summary>Two enabled mods that both ship a data file with the same path.</summary>
public sealed record ModConflict(string File, IReadOnlyList<string> Mods);

public static class ModConflicts
{
    private static readonly string[] DataExtensions = { ".json", ".yaml", ".yml" };

    /// <summary>
    /// The game layers every mod's data files over the base game, key by key, in load order
    /// (Scripts/Modding/ModLoader.cs). Two mods with the same file are usually two edits of the
    /// same thing - where they set the same keys, the one later in the order wins and the
    /// other's change is lost without a word. Artwork and sound resolve inside each mod's own
    /// folder, so only data files count, and the manifest at the root is not data.
    /// </summary>
    public static List<ModConflict> Find(IEnumerable<Package> loadOrder)
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in loadOrder)
        {
            if (!Directory.Exists(package.Path)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(package.Path, "*", SearchOption.AllDirectories).ToList(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (Array.IndexOf(DataExtensions, Path.GetExtension(file).ToLowerInvariant()) < 0) continue;
                var relative = Path.GetRelativePath(package.Path, file).Replace('\\', '/');
                if (!relative.Contains('/')) continue;
                if (!owners.TryGetValue(relative, out var list)) owners[relative] = list = new List<string>();
                if (!list.Contains(package.Id, StringComparer.OrdinalIgnoreCase)) list.Add(package.Id);
            }
        }
        return owners.Where(o => o.Value.Count > 1).OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
            .Select(o => new ModConflict(o.Key, o.Value)).ToList();
    }
}
