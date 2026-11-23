using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Reads the game's own data out of `game/`, laid out the way a Paradox title lays it out.
///
/// The important part is not which folders exist, it is the rule they follow: a category is
/// a *folder*, everything in it is read in file-name order, and later files win. That is
/// what makes a mod cheap. A balance mod drops `50_my_numbers.json` into
/// `common/defines/` beside the base game's `00_combat.json`, changes three values, and
/// inherits the other hundred and fifty; it never forks a file, so it does not go stale
/// when the base game edits the same file next patch.
///
/// This loader used to ask for one exact file name per category - `common/units/
/// unit_types.json` and nothing else - which meant the base game was arranged in a way no
/// mod could imitate: a mod could only replace that file wholesale. The mod loader already
/// read whole folders. Now both sides read the same way, and the base game is simply the
/// first entry in the load order rather than a special case.
///
/// Values still land in typed C# objects, and anything unreadable is skipped rather than
/// fatal: one broken file in a category cannot take the category down with it, and a
/// balancing mod cannot make the executable unbootable.
/// </summary>
public static class GameDataLoader
{
    /// <summary>
    /// Every JSON/YAML/YML document in one category folder, in file-name order. Prefixes are the
    /// convention for the same reason Paradox uses them: they make the load order visible.
    /// </summary>
    public static IReadOnlyList<string> DataFiles(params string[] relativeFolder)
    {
        // Порядок только внутри одной категории. Номер 90_... не позволяет файлу
        // раннего пакета обойти более поздний мод: порядок пакетов задаёт ModLoader.
        if (ModOverlayPolicy.IsActive && ModOverlayPolicy.IsBasePathReplaced(relativeFolder))
            return Array.Empty<string>();
        var folder = DataFolder(relativeFolder);
        if (folder is null) return Array.Empty<string>();
        try
        {
            var files = StructuredData.Files(folder, SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
            if (!ModOverlayPolicy.IsActive) return files.ToArray();
            var prefix = string.Join("/", relativeFolder).Replace('\\', '/').Trim('/');
            return files.Where(file =>
            {
                var rel = prefix.Length == 0 ? Path.GetFileName(file) : $"{prefix}/{Path.GetFileName(file)}";
                return !ModOverlayPolicy.IsBaseFileExcluded(rel);
            }).ToArray();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[dof] {Path.Combine(relativeFolder)} unreadable: {exception.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The same files, parsed. A file that will not parse is reported and skipped, so a
    /// single typo in a mod costs that file rather than every building in the game.
    /// </summary>
    public static IEnumerable<JsonDocument> DataDocuments(params string[] relativeFolder)
    {
        // Владение документом передаётся вызывающему каталогу: его нужно Dispose/using.
        // JsonElement живёт не дольше документа; для хранения вне using нужен Clone
        // или собственная типизированная запись. Это важно при добавлении каталога.
        var files = DataFiles(relativeFolder);
        if (files.Count == 0) yield break;
        var patchActive = ModJsonPatcher.IsActive;
        var patchCategory = patchActive ? string.Join("/", relativeFolder) : "";
        if (files.Count < 4)
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                JsonDocument document;
                try
                {
                    document = DataDocumentParser.ParseDataDocument(file);
                    if (patchActive)
                        document = ModJsonPatcher.ApplyToBaseDocument(patchCategory, Path.GetFileName(file), document);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"[dof] {Path.GetFileName(file)} ignored: {exception.Message}");
                    DiagnosticLogs.Write("errors", $"DATA_FILE_REJECTED file={file} error={exception.Message}");
                    continue;
                }
                yield return document;
            }
            yield break;
        }

        // Parse multi-file categories in parallel via standalone DataDocumentParser (zero
        // references to GameDataLoader inside Parallel.For, preventing any .cctor lock cycle),
        // then yield strictly in deterministic sorted file order (preserving last-file-wins).
        DataDocumentParser.ParseBatch(files, out var docs, out var errors);
        var index = 0;
        try
        {
            for (; index < files.Count; index++)
            {
                if (errors[index] is not null)
                {
                    Debug.WriteLine($"[dof] {Path.GetFileName(files[index])} ignored: {errors[index].Message}");
                    DiagnosticLogs.Write("errors", $"DATA_FILE_REJECTED file={files[index]} error={errors[index].Message}");
                    continue;
                }
                var doc = docs[index];
                docs[index] = null;
                if (doc is not null)
                {
                    if (patchActive)
                        doc = ModJsonPatcher.ApplyToBaseDocument(patchCategory, Path.GetFileName(files[index]), doc);
                    yield return doc;
                }
            }
        }
        finally
        {
            for (; index < docs.Length; index++) docs[index]?.Dispose();
        }
    }

    private static readonly object WarningLock = new();
    private static readonly List<string> PendingWarnings = new();

    /// <summary>Record a non-fatal content or legacy-schema warning for diagnostics and mod logs.</summary>
    public static void RecordWarning(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (WarningLock)
        {
            if (PendingWarnings.Count < 512) PendingWarnings.Add(message);
        }
        DiagnosticLogs.Write("errors", message);
    }

    /// <summary>Drain recorded data warnings (legacy keys, ignored nested files) since the last drain.</summary>
    public static IReadOnlyList<string> DrainWarnings()
    {
        lock (WarningLock)
        {
            if (PendingWarnings.Count == 0) return Array.Empty<string>();
            var snapshot = PendingWarnings.ToArray();
            PendingWarnings.Clear();
            return snapshot;
        }
    }

    /// <summary>
    /// Uniform entry-deletion check across dictionary catalogues. Canonical key is
    /// <c>"remove": true</c>; legacy aliases <c>"removed"</c> and <c>"disabled"</c> are still
    /// honoured and logged via <see cref="RecordWarning"/> so existing mods keep working cleanly.
    /// </summary>
    public static bool TryReadRemoveFlag(JsonElement value, out bool remove, string origin = null)
    {
        remove = false;
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (value.TryGetProperty("remove", out var canonical) &&
            canonical.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            remove = canonical.GetBoolean();
            return true;
        }
        foreach (var legacy in new[] { "removed", "disabled" })
        {
            if (value.TryGetProperty(legacy, out var alias) &&
                alias.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                remove = alias.GetBoolean();
                RecordWarning($"{(string.IsNullOrEmpty(origin) ? "data" : origin)}: legacy key '{legacy}' used for deletion — prefer '\"remove\": true'.");
                return true;
            }
        }
        return false;
    }

    /// <summary>True when an object entry declares <c>"remove": true</c> (or a logged legacy alias).</summary>
    public static bool IsRemoved(JsonElement value, string origin = null) =>
        TryReadRemoveFlag(value, out var remove, origin) && remove;

    /// <summary>
    /// Enumerate data files (<c>*.json</c>/<c>*.yaml</c>/<c>*.yml</c>) at top level of
    /// <paramref name="directory"/> (plus optionally direct children of named
    /// <paramref name="allowedSubfolders"/>, e.g. <c>2024</c> in <c>history/*</c>). Data files
    /// placed inside other or deeper subfolders are skipped with a clear warning so modders see
    /// immediately why nested folders were not loaded.
    /// </summary>
    public static string[] TopLevelDataFiles(string directory, string categoryLabel,
        ICollection<string> warnings, params string[] allowedSubfolders)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<string>();

        var results = new List<string>(StructuredData.Files(directory, SearchOption.TopDirectoryOnly));
        var allowed = allowedSubfolders is { Length: > 0 }
            ? new HashSet<string>(allowedSubfolders, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var subfolder in Directory.EnumerateDirectories(directory)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(subfolder);
            if (allowed is not null && allowed.Contains(folderName))
            {
                results.AddRange(StructuredData.Files(subfolder, SearchOption.TopDirectoryOnly));
                foreach (var deeper in Directory.EnumerateDirectories(subfolder)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    foreach (var nested in StructuredData.Files(deeper, SearchOption.AllDirectories))
                        WarnNestedDataFile(warnings, categoryLabel, directory, nested);
            }
            else
            {
                foreach (var nested in StructuredData.Files(subfolder, SearchOption.AllDirectories))
                    WarnNestedDataFile(warnings, categoryLabel, directory, nested);
            }
        }

        return results.OrderBy(path => path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Report nested non-JSON files (such as <c>*.csv</c>) found inside subfolders when top-level only is supported.</summary>
    public static void WarnNestedFilesWithExtension(string directory, string pattern, string categoryLabel,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        foreach (var subfolder in Directory.EnumerateDirectories(directory)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            foreach (var nested in Directory.EnumerateFiles(subfolder, pattern, SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                WarnNestedDataFile(warnings, categoryLabel, directory, nested);
    }

    private static void WarnNestedDataFile(ICollection<string> warnings, string categoryLabel, string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        var message = $"{categoryLabel}: nested file '{relative}' was ignored; place data files directly inside {categoryLabel}/ (not deeper subfolders).";
        warnings?.Add(message);
        RecordWarning(message);
    }


    /// <summary>
    /// Every category some catalogue has asked for, as it asked for it.
    ///
    /// The list of what is moddable used to live in a document, where it was a claim
    /// somebody had to remember to update. This is the same list as a fact: a folder nothing
    /// asks for is a folder a modder can edit all day with no effect and no error, and a
    /// category asked for with nothing shipped in it is a feature with no example to copy.
    /// The bench holds this against what is on disk.
    ///
    /// Not cleared between loads on purpose - it is a record of what this build reads, not of
    /// what the last reload happened to touch.
    /// </summary>
    public static readonly HashSet<string> Asked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shipped data documents itself with comments, so every reader allows them.</summary>
    public static readonly JsonDocumentOptions ForgivingJson = DataDocumentParser.ForgivingJson;

    private static readonly Dictionary<string, PropertyInfo> DefinesProperties =
        typeof(Defines).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanWrite)
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

    public static void ApplyDefines(Defines defines)
    {
        // Defines принадлежат конкретной кампании; каталоги обычно process-wide.
        // Новый модификатор не должен прятать состояние страны в static-поле каталога:
        // сохранение и другая кампания этого поля не получат.
        var properties = DefinesProperties;

        foreach (var document in DataDocuments("common", "defines"))
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (!properties.TryGetValue(entry.Name, out var property)) continue;
                    var value = ConvertValue(entry.Value, property.PropertyType);
                    if (value is Dictionary<string, bool> flags) MechanicFeatures.Merge(defines.Features, flags);
                    else if (value is Dictionary<string, string> rules)
                    {
                        if (property.Name == nameof(Defines.FeatureUnlocks)) MechanicFeatures.Merge(defines.FeatureUnlocks, rules);
                        else if (property.GetValue(defines) is Dictionary<string, string> bindings) MechanicFeatures.MergeBindings(bindings, rules);
                    }
                    else if (value is not null) property.SetValue(defines, value);
                }
            }
    }

    /// <summary>Merge every `name: number` table in one category folder.</summary>
    public static Dictionary<string, float> LoadNumberMap(params string[] relativeFolder)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in DataDocuments(relativeFolder))
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                // TryGetSingle throws rather than returning false when the value is not a
                // number at all, so the kind is checked first. A category folder holds
                // whatever a mod puts in it, and one stray object should not be fatal.
                foreach (var entry in document.RootElement.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.Number &&
                        entry.Value.TryGetSingle(out var number)) result[entry.Name] = number;
            }
        return result;
    }

    private static readonly object GameRootCacheLock = new();
    private static string _cachedCwd;
    private static string _cachedBaseDir;
    private static string _cachedGameRoot;

    private static string PrimaryGameRoot()
    {
        var cwd = Directory.GetCurrentDirectory();
        var baseDir = AppContext.BaseDirectory;
        lock (GameRootCacheLock)
        {
            if (string.Equals(_cachedCwd, cwd, StringComparison.Ordinal) &&
                string.Equals(_cachedBaseDir, baseDir, StringComparison.Ordinal) &&
                _cachedGameRoot is not null &&
                Directory.Exists(_cachedGameRoot))
                return _cachedGameRoot;
        }

        string found = null;
        foreach (var root in CandidateRoots())
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "game");
            if (Directory.Exists(candidate)) { found = candidate; break; }
        }

        lock (GameRootCacheLock)
        {
            _cachedCwd = cwd;
            _cachedBaseDir = baseDir;
            _cachedGameRoot = found;
        }
        return found;
    }

    /// <summary>One named asset - a raster, a descriptor - rather than a category folder.</summary>
    public static string FindDataFile(params string[] relativePath)
    {
        if (ModOverlayPolicy.IsActive && ModOverlayPolicy.IsBaseFileExcluded(string.Join("/", relativePath)))
            return null;
        var suffix = Path.Combine(relativePath);
        var primary = PrimaryGameRoot();
        if (primary is not null)
        {
            var direct = Path.Combine(primary, suffix);
            if (File.Exists(direct)) return direct;
        }
        foreach (var root in CandidateRoots())
        {
            var candidate = Path.Combine(root, "game", suffix);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string DataFolder(params string[] relativeFolder)
    {
        if (relativeFolder.Length > 0) Asked.Add(string.Join("/", relativeFolder));
        var suffix = Path.Combine(relativeFolder);
        var primary = PrimaryGameRoot();
        if (primary is not null)
        {
            var direct = Path.Combine(primary, suffix);
            if (Directory.Exists(direct)) return direct;
        }
        foreach (var root in CandidateRoots())
        {
            var candidate = Path.Combine(root, "game", suffix);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Every place a top-level installation folder - `mods`, `dlc` - actually is, nearest
    /// the data first.
    ///
    /// This exists because `res://` is a lie outside the editor. In the editor
    /// `GlobalizePath("res://mods")` names a real directory and everything works; in an
    /// exported game `res://` is a `.pck`, and a mod folder is the one thing that cannot
    /// live in a `.pck` - it is a folder a player drops files into after the game shipped.
    /// So these are found the way `game/` is found: by walking out from the assembly until
    /// the installation is recognised.
    ///
    /// It is recognised by `game/` being in it, which is stricter than it looks. `mods` is
    /// a common enough folder name that a bare upward search would sooner or later find
    /// somebody else's. The exported layout puts `game/` one level below the executable, so
    /// the folder holding `game/` and its parent both count: the first is where shipped
    /// content travels, the second is the folder the player can actually see.
    ///
    /// Every match is returned rather than the first, because they are not alternatives. A
    /// pack that ships with the game and a mod the player installed are both real, and it
    /// is the caller that decides which one wins when they name the same thing.
    /// </summary>
    public static IEnumerable<string> InstallFolders(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in InstallRoots())
        {
            string folder;
            try { folder = Path.GetFullPath(Path.Combine(root, name)); }
            catch (Exception) { continue; }
            if (Directory.Exists(folder) && seen.Add(folder)) yield return folder;
        }
    }

    /// <summary>One named file inside such a folder - the nearest copy of it, or null.</summary>
    public static string InstallFile(string name, params string[] relativePath)
    {
        var suffix = Path.Combine(relativePath);
        return InstallFolders(name).Select(folder => Path.Combine(folder, suffix))
            .FirstOrDefault(File.Exists);
    }

    private static object ConvertValue(JsonElement value, Type type)
    {
        if (type == typeof(string[]))
            return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
                ? value.EnumerateArray().Select(x => x.GetString()).ToArray() : null;
        if (type == typeof(Dictionary<string, bool>) || type == typeof(Dictionary<string, string>))
        {
            try { return value.ValueKind == JsonValueKind.Object ? value.Deserialize(type) : null; }
            catch (JsonException) { return null; }
        }
        if (type == typeof(float) && value.TryGetSingle(out var single)) return single;
        if (type == typeof(double) && value.TryGetDouble(out var real)) return real;
        if (type == typeof(int) && value.TryGetInt32(out var integer)) return integer;
        // `long` is not a wider `int` here: the civil-war minimum population is a count of
        // people, and a mod that wanted a hundred-million threshold would silently get the
        // built-in two million because the number did not fit an int and nothing said so.
        if (type == typeof(long) && value.TryGetInt64(out var wide)) return wide;
        if (type == typeof(bool) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
            return value.GetBoolean();
        if (type == typeof(string) && value.ValueKind == JsonValueKind.String) return value.GetString();
        if (type.IsEnum && value.ValueKind == JsonValueKind.String &&
            Enum.TryParse(type, value.GetString(), true, out var enumeration)) return enumeration;
        return null;
    }

    /// <summary>
    /// One asset path against the package that declared it.
    ///
    /// A mod names its own files relative to itself, because it cannot know where the
    /// player installed it; the base game names its own with an absolute engine path,
    /// because all of its art and audio already lives outside the data folders. Both are
    /// accepted, and every catalogue that points at a file resolves it the same way.
    /// </summary>
    public static string ResolveAssetPath(string path, string assetRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Trim().Replace('\\', '/');
        if (path.Contains("://")) return path;
        if (string.IsNullOrWhiteSpace(assetRoot)) return path;

        var root = assetRoot.Replace('\\', '/');
        if (!root.EndsWith("/", StringComparison.Ordinal)) root += "/";
        return root + path.TrimStart('/');
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 7 && directory is not null; depth++, directory = directory.Parent)
            yield return directory.FullName;
    }

    /// <summary>The candidate roots that hold a `game/`, each followed by its parent.</summary>
    private static IEnumerable<string> InstallRoots()
    {
        foreach (var root in CandidateRoots())
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (!Directory.Exists(Path.Combine(root, "game"))) continue;
            yield return root;
            // Through DirectoryInfo rather than Directory.GetParent, which is not the same
            // thing when the path ends in a separator - and AppContext.BaseDirectory always
            // does. `GetParent(@"C:\a\b\")` hands back `C:\a\b`: the folder itself, one
            // level short of where it was going. That is the whole exported layout, so the
            // folder beside the executable would never have been looked in.
            DirectoryInfo parent;
            try { parent = new DirectoryInfo(root).Parent; }
            catch (Exception) { continue; }
            if (parent is not null) yield return parent.FullName;
        }
    }
}

/// <summary>
/// Standalone parallel document parser kept strictly outside <see cref="GameDataLoader"/> so
/// worker threads inside <see cref="System.Threading.Tasks.Parallel.For"/> never touch
/// <see cref="GameDataLoader"/>'s type initializer.
/// </summary>
internal static class DataDocumentParser
{
    internal static readonly JsonDocumentOptions ForgivingJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    internal static JsonDocument ParseDataDocument(string file)
    {
        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(file);
            var payload = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? bytes.AsMemory(3)
                : bytes.AsMemory();
            return JsonDocument.Parse(payload, ForgivingJson);
        }
        return JsonDocument.Parse(StructuredData.ReadJson(file), ForgivingJson);
    }

    internal static void ParseBatch(IReadOnlyList<string> files, out JsonDocument[] docs, out Exception[] errors)
    {
        var count = files.Count;
        var localDocs = new JsonDocument[count];
        var localErrors = new Exception[count];
        System.Threading.Tasks.Parallel.For(0, count, i =>
        {
            try { localDocs[i] = ParseDataDocument(files[i]); }
            catch (Exception ex) { localErrors[i] = ex; }
        });
        docs = localDocs;
        errors = localErrors;
    }
}
