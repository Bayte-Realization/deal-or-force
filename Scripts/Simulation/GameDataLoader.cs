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
        var folder = DataFolder(relativeFolder);
        if (folder is null) return Array.Empty<string>();
        try
        {
            return StructuredData.Files(folder, SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
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
        foreach (var file in DataFiles(relativeFolder))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(StructuredData.ReadJson(file), ForgivingJson);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[dof] {Path.GetFileName(file)} ignored: {exception.Message}");
                DiagnosticLogs.Write("errors", $"DATA_FILE_REJECTED file={file} error={exception.Message}");
                continue;
            }
            yield return document;
        }
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
    public static readonly JsonDocumentOptions ForgivingJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static void ApplyDefines(Defines defines)
    {
        var properties = typeof(Defines).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanWrite)
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

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

    /// <summary>One named asset - a raster, a descriptor - rather than a category folder.</summary>
    public static string FindDataFile(params string[] relativePath)
    {
        var suffix = Path.Combine(relativePath);
        return CandidateRoots().Select(root => Path.Combine(root, "game", suffix)).FirstOrDefault(File.Exists);
    }

    public static string DataFolder(params string[] relativeFolder)
    {
        if (relativeFolder.Length > 0) Asked.Add(string.Join("/", relativeFolder));
        var suffix = Path.Combine(relativeFolder);
        return CandidateRoots().Select(root => Path.Combine(root, "game", suffix))
            .FirstOrDefault(Directory.Exists);
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
