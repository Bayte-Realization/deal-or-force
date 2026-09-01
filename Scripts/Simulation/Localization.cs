using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ByDealOrForce.Simulation;

/// <summary>
/// Every string the player reads, in one table per language, loaded the same layered way
/// content is: base game first, then each enabled mod in load order, later wins.
///
/// The folder is named the way Paradox titles name theirs (`game/localization/`), and a
/// mod translating the game drops `localization/&lt;language&gt;.json` into its own root -
/// no code, no rebuild. Files are flat `{ "key": "text" }` maps rather than Paradox's
/// own YAML dialect, because the rest of this project's content is JSON and one format
/// a modder already knows beats a second one they have to learn.
///
/// A missing key never throws and never blanks the UI: lookup falls back to the current
/// language, then English, then the key itself, so a half-finished translation shows the
/// untranslated string instead of an empty panel.
/// </summary>
public static class Loc
{
    public const string DefaultLanguage = "english";

    private static readonly Dictionary<string, Dictionary<string, string>> Tables =
        new(StringComparer.OrdinalIgnoreCase);

    private static string _language = DefaultLanguage;

    /// <summary>Raised when the language changes, so open panels can rebuild their text.</summary>
    public static event Action LanguageChanged;

    static Loc() => ResetCatalog();

    public static string Language
    {
        get => _language;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? DefaultLanguage : value.Trim().ToLowerInvariant();
            if (string.Equals(next, _language, StringComparison.OrdinalIgnoreCase)) return;
            _language = next;
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>Languages that actually have a table loaded, for a language selector.</summary>
    public static IReadOnlyList<string> Languages => Tables.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();

    /// <summary>Human-readable name of a language, taken from its own table.</summary>
    public static string DisplayName(string language) =>
        Tables.TryGetValue(language ?? "", out var table) && table.TryGetValue("language.name", out var name)
            ? name
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(language ?? "");

    /// <summary>Reload base-game tables. Mods layer on top afterwards via <see cref="Merge"/>.</summary>
    public static void ResetCatalog()
    {
        Tables.Clear();
        var root = FindLocalizationRoot();
        if (root is not null) LoadDirectory(root, null);

        // The base game must be readable even with no data files present at all.
        if (!Tables.ContainsKey(DefaultLanguage)) Tables[DefaultLanguage] = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Layer one directory of `&lt;language&gt;.json` files over what is already loaded.</summary>
    public static void LoadDirectory(string directory, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var language = Path.GetFileNameWithoutExtension(file);
            try { Merge(language, File.ReadAllText(file), errors); }
            catch (Exception exception) { errors?.Add($"localization/{Path.GetFileName(file)}: {exception.Message}"); }
        }
    }

    /// <summary>Merge one language table. Later calls overwrite matching keys.</summary>
    public static void Merge(string language, string json, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(language)) return;

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"localization/{language}.json must be an object of key/text pairs.");
            return;
        }

        if (!Tables.TryGetValue(language, out var table))
            Tables[language] = table = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                errors?.Add($"localization/{language}.json: '{entry.Name}' must be a string.");
                continue;
            }
            table[entry.Name] = entry.Value.GetString() ?? "";
        }
    }

    /// <summary>
    /// Look up a key. Falls through the current language, then English, then returns the
    /// key itself - so an untranslated screen reads as slightly odd rather than as empty,
    /// and a missing key is visible to whoever is testing instead of silent.
    /// </summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (Tables.TryGetValue(_language, out var table) && table.TryGetValue(key, out var text)) return text;
        if (Tables.TryGetValue(DefaultLanguage, out var fallback) && fallback.TryGetValue(key, out var english)) return english;
        return key;
    }

    /// <summary>Look up a key, or return <paramref name="literal"/> when it is untranslated.</summary>
    public static string GetOr(string key, string literal)
    {
        var text = Get(key);
        return ReferenceEquals(text, key) || text == key ? literal : text;
    }

    public static string Format(string key, params object[] arguments)
    {
        var text = Get(key);
        if (arguments is null || arguments.Length == 0) return text;
        try { return string.Format(CultureInfo.CurrentCulture, text, arguments); }
        catch (FormatException) { return text; }
    }

    public static bool Has(string key) =>
        (Tables.TryGetValue(_language, out var table) && table.ContainsKey(key)) ||
        (Tables.TryGetValue(DefaultLanguage, out var fallback) && fallback.ContainsKey(key));

    /// <summary>Every key the base game and loaded mods define, for a coverage report.</summary>
    public static IEnumerable<string> Keys(string language) =>
        Tables.TryGetValue(language ?? _language, out var table)
            ? table.Keys.OrderBy(key => key, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

    /// <summary>Keys English has that <paramref name="language"/> does not - a translator's to-do list.</summary>
    public static IReadOnlyList<string> MissingKeys(string language)
    {
        if (!Tables.TryGetValue(DefaultLanguage, out var english)) return Array.Empty<string>();
        if (!Tables.TryGetValue(language ?? "", out var table)) return english.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
        return english.Keys.Where(key => !table.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal).ToList();
    }

    private static string FindLocalizationRoot()
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "game", "localization");
            if (Directory.Exists(directory)) return directory;
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 7 && directory is not null; depth++, directory = directory.Parent)
            yield return directory.FullName;
    }
}
