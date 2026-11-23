using System;
using System.Collections.Concurrent;
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
/// Non-active languages stay unparsed until selected or explicitly queried (`Language =`,
/// `TextIn`, `CultureOf`, `Keys`, `MissingKeys`, `Problems`), cutting boot parsing overhead
/// across shipped and modded translation packs.
///
/// A missing key never throws and never blanks the UI: lookup falls back to the current
/// language, then English, then the key itself, so a half-finished translation shows the
/// untranslated string instead of an empty panel.
/// </summary>
public static class Loc
{
    public const string DefaultLanguage = "english";

    private sealed record LocalizationSource(bool IsFlatFile, string FilePath);

    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> Tables =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> DiscoveredLanguages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoadedLanguages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<LocalizationSource>> PendingSourcesByLanguage =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LazyDisplayNameHints =
        new(StringComparer.OrdinalIgnoreCase);

    private static string _language = DefaultLanguage;
    internal static int Revision { get; private set; }

    private static Func<string, string, string> _textAdapter;
    private static int _adaptedRevision = -1;
    private static ConcurrentDictionary<string, string> _adaptedCache;

    /// <summary>
    /// Optional platform copy adapter (e.g. MobileTextAdapter on touchscreens).
    /// Null on desktop for a single-branch, zero-allocation passthrough.
    /// Signature: (localizationKey, resolvedText) -> adaptedText.
    /// </summary>
    public static Func<string, string, string> TextAdapter
    {
        get => _textAdapter;
        set
        {
            if (ReferenceEquals(_textAdapter, value)) return;
            _textAdapter = value;
            _adaptedRevision = -1;
            _adaptedCache = value is null
                ? null
                : new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Raised when the language changes, so open panels can rebuild their text.</summary>
    public static event Action LanguageChanged;

    private static readonly object CatalogInitLock = new();
    private static volatile bool _catalogLoaded;

    /// <summary>
    /// Lazy non-.cctor bootstrap. Keeping initial catalog loading out of <c>static Loc()</c>
    /// guarantees that <see cref="LocalizationFileParser.ParseBatch"/> never runs under the CLR
    /// type-initializer lock (which would deadlock if any worker thread touched <see cref="Loc"/>).
    /// </summary>
    private static void EnsureLoaded()
    {
        if (_catalogLoaded) return;
        lock (CatalogInitLock)
        {
            if (_catalogLoaded) return;
            _catalogLoaded = true;
            ResetCatalog();
        }
    }

    private static bool IsActiveOrDefault(string language) =>
        string.Equals(language, DefaultLanguage, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(language, _language, StringComparison.OrdinalIgnoreCase);

    private static void EnsureLanguageLoaded(string language, List<string> errors = null)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(language)) return;
        lock (CatalogInitLock)
        {
            LoadLanguageLocked(language, errors);
        }
    }

    private static void LoadLanguageLocked(string language, List<string> errors)
    {
        if (!PendingSourcesByLanguage.TryGetValue(language, out var sources) || sources.Count == 0)
        {
            LoadedLanguages.Add(language);
            return;
        }

        // Preserve exact directory/file overlay order while still batch-parsing contiguous folder runs in parallel.
        var table = Tables.TryGetValue(language, out var existing)
            ? existing
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var idx = 0;
        while (idx < sources.Count)
        {
            if (sources[idx].IsFlatFile)
            {
                var file = sources[idx].FilePath;
                try { MergeFileContentIntoLocked(language, ref table, StructuredData.ReadJson(file), errors); }
                catch (Exception exception) { errors?.Add($"localization/{Path.GetFileName(file)}: {exception.Message}"); }
                idx++;
                continue;
            }

            var runEnd = idx + 1;
            while (runEnd < sources.Count && !sources[runEnd].IsFlatFile) runEnd++;
            var work = new List<(string Language, string File)>(runEnd - idx);
            for (var i = idx; i < runEnd; i++) work.Add((language, sources[i].FilePath));

            LocalizationFileParser.ParseBatch(work, out var parsedEntries, out var parseErrors);
            var addedCount = 0;
            for (var i = 0; i < work.Count; i++)
                if (parsedEntries[i] is { } list) addedCount += list.Count;
            if (addedCount > 0) table.EnsureCapacity(table.Count + addedCount);

            for (var i = 0; i < work.Count; i++)
            {
                var localErr = parseErrors[i];
                if (localErr is not null && errors is not null) errors.AddRange(localErr);
                var entries = parsedEntries[i];
                if (entries is null) continue;
                for (var ei = 0; ei < entries.Count; ei++)
                {
                    var kv = entries[ei];
                    table[kv.Key] = kv.Value;
                }
            }
            Revision++;
            idx = runEnd;
        }

        Tables[language] = table;
        sources.Clear();
        LoadedLanguages.Add(language);
    }

    public static string Language
    {
        get => _language;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? DefaultLanguage : value.Trim().ToLowerInvariant();
            if (string.Equals(next, _language, StringComparison.OrdinalIgnoreCase)) return;
            EnsureLanguageLoaded(next);
            _language = next;
            Revision++;
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>Languages that actually have a table loaded or discovered, for a language selector.</summary>
    public static IReadOnlyList<string> Languages
    {
        get
        {
            EnsureLoaded();
            lock (CatalogInitLock)
            {
                var set = new HashSet<string>(DiscoveredLanguages, StringComparer.OrdinalIgnoreCase);
                foreach (var k in Tables.Keys) set.Add(k);
                return set.OrderBy(key => key, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>Human-readable name of a language, taken from its own table.</summary>
    public static string DisplayName(string language)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(language)) return "";
        if (Tables.TryGetValue(language, out var table) && table.TryGetValue("language.name", out var name))
            return name;
        lock (CatalogInitLock)
        {
            if (Tables.TryGetValue(language, out table) && table.TryGetValue("language.name", out name))
                return name;
            if (LazyDisplayNameHints.TryGetValue(language, out var hint))
                return hint;
            if (PendingSourcesByLanguage.TryGetValue(language, out var sources) && sources.Count > 0)
            {
                // Peek only small 00_* metadata header files before deciding whether full parse is needed.
                var headers = new List<(string Language, string File)>(2);
                for (var i = 0; i < sources.Count; i++)
                {
                    if (sources[i].IsFlatFile) continue;
                    var fname = Path.GetFileName(sources[i].FilePath);
                    if (fname.StartsWith("00_", StringComparison.OrdinalIgnoreCase))
                        headers.Add((language, sources[i].FilePath));
                }
                if (headers.Count > 0 && headers.Count <= 4)
                {
                    LocalizationFileParser.ParseBatch(headers, out var entriesByFile, out _);
                    string foundName = null;
                    for (var i = 0; i < entriesByFile.Length; i++)
                    {
                        var entries = entriesByFile[i];
                        if (entries is null) continue;
                        for (var ei = 0; ei < entries.Count; ei++)
                            if (string.Equals(entries[ei].Key, "language.name", StringComparison.Ordinal))
                                foundName = entries[ei].Value;
                    }
                    if (!string.IsNullOrWhiteSpace(foundName))
                    {
                        LazyDisplayNameHints[language] = foundName;
                        return foundName;
                    }
                }
                LoadLanguageLocked(language, null);
                if (Tables.TryGetValue(language, out table) && table.TryGetValue("language.name", out name))
                    return name;
            }
        }
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(language);
    }

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
        EnsureLanguageLoaded(language);
        if (Tables.TryGetValue(language ?? "", out var table) &&
            table.TryGetValue("language.culture", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            try { return CultureInfo.GetCultureInfo(name.Trim()); }
            catch (CultureNotFoundException) { }
        }
        return CultureInfo.InvariantCulture;
    }

    /// <summary>One language's own text for a key, or null - no fallback, for a report or a validator.</summary>
    public static string TextIn(string language, string key)
    {
        EnsureLanguageLoaded(language);
        return Tables.TryGetValue(language ?? "", out var table) && table.TryGetValue(key ?? "", out var text) ? text : null;
    }

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
        EnsureLanguageLoaded(DefaultLanguage);
        EnsureLanguageLoaded(language);
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
        lock (CatalogInitLock)
        {
            _catalogLoaded = true;
            Tables.Clear();
            DiscoveredLanguages.Clear();
            LoadedLanguages.Clear();
            PendingSourcesByLanguage.Clear();
            LazyDisplayNameHints.Clear();
            Revision++;
            var root = FindLocalizationRoot();
            if (root is not null) LoadDirectoryLocked(root, errors);

            // The base game must be readable even with no data files present at all.
            if (!Tables.ContainsKey(DefaultLanguage)) Tables[DefaultLanguage] = new Dictionary<string, string>(StringComparer.Ordinal);
            LoadedLanguages.Add(DefaultLanguage);
        }
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
        EnsureLoaded();
        lock (CatalogInitLock)
        {
            LoadDirectoryLocked(directory, errors);
        }
    }

    private static void LoadDirectoryLocked(string directory, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in StructuredData.Files(directory, SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var language = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(language)) continue;
            DiscoveredLanguages.Add(language);
            LazyDisplayNameHints.Remove(language);
            if (!PendingSourcesByLanguage.TryGetValue(language, out var sources))
                PendingSourcesByLanguage[language] = sources = new List<LocalizationSource>(1);
            sources.Add(new LocalizationSource(true, file));
            touched.Add(language);
        }

        var folders = Directory.EnumerateDirectories(directory)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var fi = 0; fi < folders.Length; fi++)
        {
            var folder = folders[fi];
            var language = Path.GetFileName(folder);
            if (string.IsNullOrWhiteSpace(language)) continue;
            var files = GameDataLoader.TopLevelDataFiles(folder, $"localization/{language}", errors);
            if (files.Length == 0) continue;
            DiscoveredLanguages.Add(language);
            LazyDisplayNameHints.Remove(language);
            if (!PendingSourcesByLanguage.TryGetValue(language, out var sources))
                PendingSourcesByLanguage[language] = sources = new List<LocalizationSource>(files.Length);
            for (var i = 0; i < files.Length; i++)
                sources.Add(new LocalizationSource(false, files[i]));
            touched.Add(language);
        }

        foreach (var language in touched)
        {
            if (IsActiveOrDefault(language) || LoadedLanguages.Contains(language))
                LoadLanguageLocked(language, errors);
        }
    }

    /// <summary>Merge one language table. Later calls overwrite matching keys.</summary>
    public static void Merge(string language, string json, List<string> errors)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(language)) return;
        lock (CatalogInitLock)
        {
            LoadLanguageLocked(language, null);
            var table = Tables.TryGetValue(language, out var existing)
                ? new Dictionary<string, string>(existing, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            if (MergeFileContentIntoLocked(language, ref table, json, errors))
            {
                Tables[language] = table;
                DiscoveredLanguages.Add(language);
                LazyDisplayNameHints.Remove(language);
                Revision++;
            }
        }
    }

    private static bool MergeFileContentIntoLocked(string language, ref Dictionary<string, string> table, string json, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"localization/{language}.json must be an object of key/text pairs.");
            return false;
        }

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                errors?.Add($"localization/{language}.json: '{entry.Name}' must be a string.");
                continue;
            }
            table[entry.Name] = entry.Value.GetString() ?? "";
        }
        return true;
    }

    internal static bool TryGet(string key, out string text)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(key)) { text = ""; return false; }
        var has = false;
        if (Tables.TryGetValue(_language, out var table) && table.TryGetValue(key, out var local))
        {
            has = true;
            if (local.Length > 0) { text = local; return true; }
        }
        if (Tables.TryGetValue(DefaultLanguage, out var fallback) && fallback.TryGetValue(key, out var english))
        {
            has = true;
            if (english.Length > 0) { text = english; return true; }
        }
        text = key;
        return has;
    }

    internal static void CollectPoliticsStems(HashSet<string> stems)
    {
        EnsureLoaded();
        stems.Clear();
        ScanTable(Tables.GetValueOrDefault(_language), stems);
        if (!string.Equals(_language, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
            ScanTable(Tables.GetValueOrDefault(DefaultLanguage), stems);

        static void ScanTable(Dictionary<string, string> table, HashSet<string> target)
        {
            if (table is null) return;
            foreach (var key in table.Keys)
            {
                var gov = key.IndexOf(".government.", StringComparison.Ordinal);
                if (gov > 0) target.Add(key[..gov]);
                var ideo = key.IndexOf(".ideology.", StringComparison.Ordinal);
                if (ideo > 0) target.Add(key[..ideo]);
            }
        }
    }

    /// <summary>
    /// Raw lookup for <paramref name="key"/> without applying <see cref="TextAdapter"/>,
    /// returning <c>null</c> when the key is missing or empty in both the active language and
    /// <see cref="DefaultLanguage"/>. Used by adapters (e.g. mobile override lookup) to avoid
    /// recursive adaptation.
    /// </summary>
    public static string RawText(string key)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(key)) return null;
        if (Tables.TryGetValue(_language, out var table) && table.TryGetValue(key, out var text) &&
            text.Length > 0) return text;
        if (Tables.TryGetValue(DefaultLanguage, out var fallback) && fallback.TryGetValue(key, out var english) &&
            english.Length > 0) return english;
        return null;
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<string, string> GetAdaptedCache()
    {
        var rev = Revision;
        var cache = _adaptedCache;
        if (cache is not null && _adaptedRevision == rev) return cache;
        cache = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        _adaptedCache = cache;
        _adaptedRevision = rev;
        return cache;
    }

    /// <summary>
    /// Look up a key. Falls through the current language, then English, then returns the
    /// key itself - so an untranslated screen reads as slightly odd rather than as empty,
    /// and a missing key is visible to whoever is testing instead of silent. An entry that
    /// is present but empty counts as untranslated rather than as a blank: a half-finished
    /// translation shows the English sentence, never an empty panel.
    /// </summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        EnsureLoaded();
        var adapter = _textAdapter;
        if (adapter is null) return RawText(key) ?? key;
        var cache = GetAdaptedCache();
        if (cache.TryGetValue(key, out var cached)) return cached;
        var adapted = adapter(key, RawText(key) ?? key);
        if (cache.Count < 8192) cache.TryAdd(key, adapted);
        return adapted;
    }

    /// <summary>Look up a key, or return <paramref name="literal"/> when it is untranslated.</summary>
    public static string GetOr(string key, string literal)
    {
        EnsureLoaded();
        var raw = RawText(key);
        var hasTranslation = raw is not null && raw != key;
        var adapter = _textAdapter;
        if (adapter is null) return hasTranslation ? raw : literal;
        if (hasTranslation)
        {
            var cache = GetAdaptedCache();
            if (cache.TryGetValue(key, out var cached)) return cached;
            var adapted = adapter(key, raw);
            if (cache.Count < 8192) cache.TryAdd(key, adapted);
            return adapted;
        }
        return string.IsNullOrEmpty(literal) ? literal : adapter(key, literal);
    }

    public static string Format(string key, params object[] arguments)
    {
        var text = Get(key);
        if (arguments is null || arguments.Length == 0) return text;
        try { return string.Format(CultureInfo.CurrentCulture, text, arguments); }
        catch (FormatException) { return text; }
    }

    public static bool Has(string key)
    {
        EnsureLoaded();
        return (Tables.TryGetValue(_language, out var table) && table.ContainsKey(key)) ||
            (Tables.TryGetValue(DefaultLanguage, out var fallback) && fallback.ContainsKey(key));
    }

    /// <summary>Every key the base game and loaded mods define, for a coverage report.</summary>
    public static IEnumerable<string> Keys(string language)
    {
        var target = language ?? _language;
        EnsureLanguageLoaded(target);
        return Tables.TryGetValue(target, out var table)
            ? table.Keys.OrderBy(key => key, StringComparer.Ordinal)
            : Enumerable.Empty<string>();
    }

    /// <summary>Keys English has that <paramref name="language"/> does not - a translator's to-do list.</summary>
    public static IReadOnlyList<string> MissingKeys(string language)
    {
        EnsureLanguageLoaded(DefaultLanguage);
        EnsureLanguageLoaded(language);
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

/// <summary>
/// Standalone parallel UTF-8 localization parser kept strictly outside <see cref="Loc"/> so
/// <see cref="System.Threading.Tasks.Parallel.For"/> worker threads never touch <see cref="Loc"/>
/// or its type initializer.
/// </summary>
internal static class LocalizationFileParser
{
    internal static void ParseBatch(
        List<(string Language, string File)> work,
        out List<KeyValuePair<string, string>>[] parsedEntries,
        out List<string>[] parseErrors)
    {
        var count = work.Count;
        var localEntries = new List<KeyValuePair<string, string>>[count];
        var localErrors = new List<string>[count];
        System.Threading.Tasks.Parallel.For(0, count, index =>
        {
            var (language, file) = work[index];
            try
            {
                byte[] utf8 = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? File.ReadAllBytes(file)
                    : System.Text.Encoding.UTF8.GetBytes(StructuredData.ReadJson(file));
                List<string> fileErrors = null;
                localEntries[index] = ParseUtf8(utf8, language, ref fileErrors);
                localErrors[index] = fileErrors;
            }
            catch (Exception exception)
            {
                localErrors[index] = new List<string> { $"localization/{language}/{Path.GetFileName(file)}: {exception.Message}" };
            }
        });
        parsedEntries = localEntries;
        parseErrors = localErrors;
    }

    private static List<KeyValuePair<string, string>> ParseUtf8(
        ReadOnlySpan<byte> utf8, string language, ref List<string> localErrors)
    {
        // Strip optional UTF-8 BOM.
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
            utf8 = utf8.Slice(3);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            (localErrors ??= new List<string>()).Add($"localization/{language}.json must be an object of key/text pairs.");
            return null;
        }
        var entries = new List<KeyValuePair<string, string>>(64);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var key = reader.GetString() ?? "";
            if (!reader.Read()) break;
            if (reader.TokenType != JsonTokenType.String)
            {
                (localErrors ??= new List<string>()).Add($"localization/{language}.json: '{key}' must be a string.");
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();
                continue;
            }
            entries.Add(new KeyValuePair<string, string>(key, reader.GetString() ?? ""));
        }
        return entries;
    }
}
