using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DealOrForce.Simulation;

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

    /// <summary>
    /// The culture numbers are written in for a language: `language.culture` in its own table
    /// (ru-RU, de-DE, ja-JP), or the invariant culture for a table that names none.
    ///
    /// Which language a player reads decides how a number looks - a decimal comma, a space
    /// between thousands - and not which language Windows happens to run in. The game spoke
    /// English and wrote "136,0" and "1 433" on a Russian machine, beside "$1.43T" on the same
    /// panel. The compact headline figures ($1.43T, 1.4B) are written one way in every language
    /// on purpose (Fmt); this is for everything else, and the game applies it as the language
    /// changes (SettingsApply).
    /// </summary>
    public static CultureInfo Culture => CultureOf(_language);

    public static CultureInfo CultureOf(string language)
    {
        if (Tables.TryGetValue(language ?? "", out var table) &&
            table.TryGetValue("language.culture", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            try { return CultureInfo.GetCultureInfo(name.Trim()); }
            catch (CultureNotFoundException) { }
        }
        return CultureInfo.InvariantCulture;
    }

    /// <summary>One language's own text for a key, or null - no fallback, for a report or a validator.</summary>
    public static string TextIn(string language, string key) =>
        Tables.TryGetValue(language ?? "", out var table) && table.TryGetValue(key ?? "", out var text) ? text : null;

    /// <summary>The plural rules <see cref="Plural"/> knows, by the name `language.plural` gives them.</summary>
    public static readonly IReadOnlyList<string> PluralRules = new[] { "one-other", "slavic", "single" };

    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)[^{}]*\}", RegexOptions.Compiled);

    /// <summary>Which arguments a text passes to string.Format, by index.</summary>
    public static IReadOnlySet<int> Placeholders(string text)
    {
        var found = new HashSet<int>();
        if (string.IsNullOrEmpty(text)) return found;
        foreach (Match match in Placeholder.Matches(text))
            if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                found.Add(index);
        return found;
    }

    /// <summary>
    /// What is wrong with a translation that nothing would say until a player met it.
    ///
    /// A text that asks for an argument the English never passes, or whose braces do not close,
    /// is not a crash - <see cref="Format"/> catches it - but it is worse than one: the player
    /// reads "{2}" or the raw pattern, and nobody finds out. The same goes for a language that
    /// does not say what it is called, or names a plural rule or a culture that does not exist.
    /// Checked for every language on every bench run, so a translation that ships is one that
    /// cannot do this.
    /// </summary>
    public static IReadOnlyList<string> Problems(string language)
    {
        var problems = new List<string>();
        if (!Tables.TryGetValue(language ?? "", out var table)) return new[] { $"{language}: nothing is loaded" };
        if (!table.ContainsKey("language.name"))
            problems.Add($"{language}: no language.name, so the selector calls it '{DisplayName(language)}'");
        if (table.TryGetValue("language.plural", out var rule) &&
            !PluralRules.Contains(rule.Trim().ToLowerInvariant()))
            problems.Add($"{language}: language.plural '{rule}' is not one of {string.Join(", ", PluralRules)}");
        if (table.TryGetValue("language.culture", out var culture) && !IsCultureName(culture))
            problems.Add($"{language}: language.culture '{culture}' is not a culture");

        Tables.TryGetValue(DefaultLanguage, out var english);
        foreach (var (key, text) in table)
        {
            if (key.StartsWith("language.", StringComparison.Ordinal)) continue;
            var source = english is not null && english.TryGetValue(key, out var englishText) ? englishText : key;
            var wanted = Placeholders(source);
            // A text that is never formatted may hold any brace it likes.
            if (wanted.Count == 0) continue;
            var extra = Placeholders(text).Where(index => !wanted.Contains(index)).OrderBy(index => index).ToList();
            if (extra.Count > 0)
                problems.Add($"{language}: \"{Shorten(key)}\" asks for {{{string.Join("}, {", extra)}}}, which the English never passes");
            try
            {
                // As many arguments as the highest index asks for, so a text is reported for
                // its extra placeholder once rather than a second time as unreadable.
                var arguments = Enumerable.Repeat((object)1, Math.Max(wanted.Max(), extra.Count > 0 ? extra.Max() : 0) + 1).ToArray();
                _ = string.Format(CultureInfo.InvariantCulture, text, arguments);
            }
            catch (FormatException)
            {
                problems.Add($"{language}: \"{Shorten(key)}\" is not a pattern string.Format can read (a brace that does not close?)");
            }
        }
        return problems;
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "...";

    private static readonly Regex CultureName = new(@"^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$", RegexOptions.Compiled);

    /// <summary>
    /// Whether a culture name names one. A build without culture data - the bench runs with
    /// invariant globalization, and every culture but the invariant one is missing there - can
    /// only check that the name has the shape of one; the game checks that it exists.
    /// </summary>
    private static bool IsCultureName(string name)
    {
        name = (name ?? "").Trim();
        if (!CultureName.IsMatch(name)) return false;
        if (!CulturesAvailable.Value) return true;
        try { CultureInfo.GetCultureInfo(name); return true; }
        catch (CultureNotFoundException) { return false; }
    }

    private static readonly Lazy<bool> CulturesAvailable = new(() =>
    {
        try { return CultureInfo.GetCultureInfo("en-US").Name == "en-US"; }
        catch (CultureNotFoundException) { return false; }
    });

    /// <summary>Reload base-game tables. Mods layer on top afterwards via <see cref="Merge"/>.</summary>
    public static void ResetCatalog(List<string> errors = null)
    {
        Tables.Clear();
        var root = FindLocalizationRoot();
        if (root is not null) LoadDirectory(root, errors);

        // The base game must be readable even with no data files present at all.
        if (!Tables.ContainsKey(DefaultLanguage)) Tables[DefaultLanguage] = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Layer one `localization` directory over what is already loaded.
    ///
    /// The shape is Paradox's: a folder per language, holding as many files as the
    /// translation wants to split itself into, read in file-name order. A translation that
    /// only fixes twenty strings ships one small file rather than a copy of the whole
    /// table, and it does not go stale when the base game adds a line to its own.
    ///
    /// A flat `&lt;language&gt;.json` beside those folders still works, because that is what
    /// the game shipped before and existing translations use it.
    /// </summary>
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

        foreach (var folder in Directory.EnumerateDirectories(directory)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var language = Path.GetFileName(folder);
            foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try { Merge(language, File.ReadAllText(file), errors); }
                catch (Exception exception)
                {
                    errors?.Add($"localization/{language}/{Path.GetFileName(file)}: {exception.Message}");
                }
            }
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
