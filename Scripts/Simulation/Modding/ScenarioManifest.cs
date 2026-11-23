using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>Tri-state era feature availability declared in a scenario or mod manifest.</summary>
public enum EraFeatureState
{
    /// <summary>Feature is active from day zero.</summary>
    Available = 0,
    /// <summary>Feature starts locked/disabled but may unlock later via tech, trigger or event.</summary>
    Restricted = 1,
    /// <summary>Feature is completely unavailable in this era (UI hidden, commands &amp; AI blocked, auto-unlocks cleared).</summary>
    Unavailable = 2
}

/// <summary>
/// Maps beginner-friendly era feature group aliases ("nukes", "missiles", "modern_air_force")
/// onto Deal or Force's canonical <see cref="MechanicFeatures"/> switches.
/// </summary>
public static class EraFeatureRegistry
{
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nukes"] = new[] { "defcon", "nuclear_posture", "nuclear_strikes" },
        ["nuclear"] = new[] { "defcon", "nuclear_posture", "nuclear_strikes" },
        ["nuclear_weapons"] = new[] { "defcon", "nuclear_posture", "nuclear_strikes" },
        ["missiles"] = new[] { "long_range_forces" },
        ["ballistic_missiles"] = new[] { "long_range_forces" },
        ["strategic_missiles"] = new[] { "long_range_forces" },
        ["modern_air_force"] = new[] { "aviation", "drones", "air_defence" },
        ["air_warfare"] = new[] { "aviation", "drones", "air_defence" },
        ["air_force"] = new[] { "aviation", "drones", "air_defence" },
        ["uavs"] = new[] { "drones" },
        ["modern_intelligence"] = new[] { "intelligence" },
        ["cyber"] = new[] { "intelligence" },
        ["global_organizations"] = new[] { "organizations" },
        ["proxy_warfare"] = new[] { "proxy_wars" },
        ["modern_finance"] = new[] { "public_debt" },
        ["colonial_era"] = new[] { "colonisation" }
    };

    public static IReadOnlyDictionary<string, string[]> AliasTable => Aliases;

    public static bool IsKnownFeatureOrAlias(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var trimmed = id.Trim();
        return Aliases.ContainsKey(trimmed) || MechanicFeatures.BuiltIns.Contains(trimmed);
    }

    public static bool TryExpand(string id, out IReadOnlyList<string> canonicalIds)
    {
        canonicalIds = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(id)) return false;
        var trimmed = id.Trim();
        if (Aliases.TryGetValue(trimmed, out var expanded))
        {
            canonicalIds = expanded;
            return true;
        }
        if (MechanicFeatures.ValidId(trimmed))
        {
            canonicalIds = new[] { trimmed.ToLowerInvariant() };
            return true;
        }
        return false;
    }

    public static bool TryParseState(string raw, out EraFeatureState state)
    {
        state = EraFeatureState.Available;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "available":
            case "enabled":
            case "on":
            case "true":
                state = EraFeatureState.Available;
                return true;
            case "restricted":
            case "locked":
            case "unlockable":
                state = EraFeatureState.Restricted;
                return true;
            case "unavailable":
            case "disabled":
            case "off":
            case "false":
                state = EraFeatureState.Unavailable;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Resolve <c>disabled_features</c> and <c>era_features</c> into canonical feature states.
    /// Identical alias expansions coalesce cleanly; contradictory assignments on the same underlying
    /// feature are reported as errors rather than silently letting one key win.
    /// </summary>
    public static Dictionary<string, EraFeatureState> Resolve(
        IEnumerable<string> disabledFeatures,
        IDictionary<string, string> eraFeatures,
        List<string> errors,
        bool requireKnownBuiltIn = false)
    {
        var resolved = new Dictionary<string, EraFeatureState>(StringComparer.OrdinalIgnoreCase);
        var origin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Record(string sourceKey, EraFeatureState state)
        {
            if (requireKnownBuiltIn && !IsKnownFeatureOrAlias(sourceKey))
            {
                errors?.Add($"Unknown era feature '{sourceKey}'. Use a built-in feature id (e.g. 'aviation', 'drones', 'defcon') or alias ('nukes', 'missiles', 'modern_air_force').");
                return;
            }
            if (!TryExpand(sourceKey, out var expanded))
            {
                errors?.Add($"Invalid feature identifier '{sourceKey}'.");
                return;
            }
            foreach (var canonical in expanded)
            {
                if (resolved.TryGetValue(canonical, out var existing))
                {
                    if (existing != state)
                        errors?.Add($"Conflicting era feature state for '{canonical}': '{origin[canonical]}' sets '{existing}', while '{sourceKey}' sets '{state}'.");
                    continue;
                }
                resolved[canonical] = state;
                origin[canonical] = sourceKey;
            }
        }

        if (disabledFeatures is not null)
        {
            foreach (var item in disabledFeatures)
                if (!string.IsNullOrWhiteSpace(item))
                    Record(item.Trim(), EraFeatureState.Unavailable);
        }

        if (eraFeatures is not null)
        {
            foreach (var pair in eraFeatures.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                if (!TryParseState(pair.Value, out var state))
                {
                    errors?.Add($"Invalid era_features value '{pair.Value}' for '{pair.Key}'. Expected 'available', 'restricted', or 'unavailable'.");
                    continue;
                }
                Record(pair.Key.Trim(), state);
            }
        }

        return resolved;
    }
}

public sealed class FeaturedCountrySpec
{
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("hook")] public string Hook { get; set; } = "";
    [JsonPropertyName("difficulty")] public string Difficulty { get; set; } = "";
}

public sealed class StartingWarSpec
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("attacker")] public string Attacker { get; set; } = "";
    [JsonPropertyName("defender")] public string Defender { get; set; } = "";
    [JsonPropertyName("justification")] public string Justification { get; set; } = "scenario";
    [JsonPropertyName("limited")] public bool Limited { get; set; }
}

public sealed class StartingAllianceSpec
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("charter")] public string Charter { get; set; } = "collective_defence";
    [JsonPropertyName("members")] public List<string> Members { get; set; } = new();
}

public sealed class StartingTruceSpec
{
    [JsonPropertyName("between")] public List<string> Between { get; set; } = new();
    [JsonPropertyName("days")] public int Days { get; set; } = 365;
}

/// <summary>
/// Compact scenario/era metadata that travels with a non-default <see cref="Campaign"/>
/// and its saves. Remains null for the base 2024 campaign so legacy saves and fingerprints
/// stay bit-for-bit identical.
/// </summary>
public sealed class ScenarioMetadata
{
    [JsonPropertyName("scenario_name")] public string ScenarioName { get; set; } = "";
    [JsonPropertyName("era_name")] public string EraName { get; set; } = "";
    [JsonPropertyName("theme")] public string Theme { get; set; } = "";
    [JsonPropertyName("music_track")] public string MusicTrack { get; set; } = "";
    [JsonPropertyName("music_playlist")] public string MusicPlaylist { get; set; } = "";
    [JsonPropertyName("total_conversion")] public bool TotalConversion { get; set; }
    [JsonPropertyName("content_signature")] public string ContentSignature { get; set; } = "";
    [JsonPropertyName("featured_countries")] public List<string> FeaturedCountries { get; set; } = new();

    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ScenarioName) &&
        string.IsNullOrWhiteSpace(EraName) &&
        string.IsNullOrWhiteSpace(Theme) &&
        string.IsNullOrWhiteSpace(MusicTrack) &&
        string.IsNullOrWhiteSpace(MusicPlaylist) &&
        !TotalConversion &&
        string.IsNullOrWhiteSpace(ContentSignature) &&
        (FeaturedCountries is null || FeaturedCountries.Count == 0);

    public ScenarioMetadata Clone() => new()
    {
        ScenarioName = ScenarioName ?? "",
        EraName = EraName ?? "",
        Theme = Theme ?? "",
        MusicTrack = MusicTrack ?? "",
        MusicPlaylist = MusicPlaylist ?? "",
        TotalConversion = TotalConversion,
        ContentSignature = ContentSignature ?? "",
        FeaturedCountries = FeaturedCountries is not null ? new List<string>(FeaturedCountries) : new List<string>()
    };
}

/// <summary>
/// Declarative scenario manifest read from <c>scenarios/&lt;id&gt;/scenario.json</c> (or YAML).
/// Extends the base 2024 schema cleanly while remaining 100% backwards-compatible with minimal manifests.
/// </summary>
public sealed class ScenarioManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string World { get; set; } = "";
    public bool BaseWorld { get; set; }
    public string PlayerCountry { get; set; } = "";
    public string Skin { get; set; } = "";
    public string Theme { get; set; } = "";
    public string MusicTrack { get; set; } = "";
    public string MusicPlaylist { get; set; } = "";

    public string StartDateRaw { get; set; } = "";
    public int? StartYear { get; set; }
    public int? StartMonth { get; set; }
    public int? StartDay { get; set; }

    public CalendarProfile Calendar { get; set; }
    public bool TotalConversion { get; set; }
    public bool ClearBaseWars { get; set; }
    public bool ClearBaseDiplomacy { get; set; }
    public List<string> ReplacePaths { get; set; } = new();
    public List<string> ExcludePaths { get; set; } = new();

    public List<FeaturedCountrySpec> FeaturedCountries { get; set; } = new();
    public List<StartingWarSpec> StartingWars { get; set; } = new();
    public List<StartingAllianceSpec> StartingAlliances { get; set; } = new();
    public List<StartingTruceSpec> StartingTruces { get; set; } = new();
    public List<string> DisabledFeatures { get; set; } = new();
    public Dictionary<string, string> EraFeatures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string SourceFile { get; set; } = "";

    public bool HasExplicitStartDate =>
        !string.IsNullOrWhiteSpace(StartDateRaw) || StartYear.HasValue || StartMonth.HasValue || StartDay.HasValue;

    public bool HasDeclarativeRules =>
        HasExplicitStartDate ||
        (Calendar is not null && !Calendar.IsDefault) ||
        TotalConversion || ClearBaseWars || ClearBaseDiplomacy ||
        !string.IsNullOrWhiteSpace(Theme) ||
        !string.IsNullOrWhiteSpace(MusicTrack) ||
        !string.IsNullOrWhiteSpace(MusicPlaylist) ||
        FeaturedCountries.Count > 0 ||
        StartingWars.Count > 0 ||
        StartingAlliances.Count > 0 ||
        StartingTruces.Count > 0 ||
        DisabledFeatures.Count > 0 ||
        EraFeatures.Count > 0 ||
        ReplacePaths.Count > 0 ||
        ExcludePaths.Count > 0;

    /// <summary>
    /// Strictly resolves the scenario start date. Rejects invalid calendar dates (e.g. Feb 30,
    /// month 13, civil year 0) instead of silently rolling forward.
    /// </summary>
    public bool TryResolveStartDate(out HistoricalDate date, out string error)
    {
        date = default;
        error = "";
        if (!HasExplicitStartDate) return false;

        if (!string.IsNullOrWhiteSpace(StartDateRaw))
        {
            if (HistoricalDate.TryParse(StartDateRaw, out date)) return true;
            error = $"Invalid start_date '{StartDateRaw}'. Use 'YYYY-MM-DD' (e.g. '1914-07-28') or 'YYYY-MM-DD BCE'.";
            return false;
        }

        if (!StartYear.HasValue)
        {
            error = "start_month or start_day was specified without start_year.";
            return false;
        }

        var year = StartYear.Value;
        var month = StartMonth ?? 1;
        var day = StartDay ?? 1;
        if (!HistoricalDate.TryCreate(year, month, day, out date))
        {
            error = $"Invalid start date ({year:0000}-{month:00}-{day:00}): month must be 1..12, day must fit that month, and year must be in [-9999, 9999].";
            return false;
        }
        return true;
    }

    public string EffectiveSkin => !string.IsNullOrWhiteSpace(Skin) ? Skin : Theme;
}

/// <summary>Parses <c>scenario.json</c> (and YAML equivalents) into <see cref="ScenarioManifest"/>.</summary>
public static class ScenarioManifestLoader
{
    public static bool TryParseFile(string filePath, out ScenarioManifest manifest, out List<string> errors)
    {
        errors = new List<string>();
        manifest = null;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            errors.Add($"Scenario manifest file not found: '{filePath}'.");
            return false;
        }
        try
        {
            var json = StructuredData.ReadJson(filePath);
            return TryParseJson(json, filePath, out manifest, out errors);
        }
        catch (Exception ex)
        {
            errors.Add($"{Path.GetFileName(filePath)}: {ex.Message}");
            return false;
        }
    }

    public static bool TryParseJson(string json, string sourceFile, out ScenarioManifest manifest, out List<string> errors)
    {
        errors = new List<string>();
        manifest = null;
        try
        {
            using var doc = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Scenario manifest root must be a JSON object.");
                return false;
            }

            var result = new ScenarioManifest
            {
                SourceFile = sourceFile ?? "",
                Id = ReadString(root, "id"),
                Name = ReadString(root, "name"),
                Description = ReadString(root, "description"),
                World = ReadString(root, "world"),
                BaseWorld = ReadBool(root, "base_world", false),
                PlayerCountry = ReadString(root, "player_country"),
                Skin = ReadString(root, "skin"),
                Theme = ReadString(root, "theme"),
                MusicTrack = ReadString(root, "music_track"),
                MusicPlaylist = ReadString(root, "music_playlist"),
                StartDateRaw = ReadString(root, "start_date"),
                StartYear = ReadNullableInt(root, "start_year", errors),
                StartMonth = ReadNullableInt(root, "start_month", errors),
                StartDay = ReadNullableInt(root, "start_day", errors),
                TotalConversion = ReadBool(root, "total_conversion", false),
                ClearBaseWars = ReadBool(root, "clear_base_wars", false),
                ClearBaseDiplomacy = ReadBool(root, "clear_base_diplomacy", false)
            };

            result.ReplacePaths = ReadStringList(root, "replace_paths", errors);
            result.ExcludePaths = ReadStringList(root, "exclude_paths", errors);
            result.DisabledFeatures = ReadStringList(root, "disabled_features", errors);

            if (root.TryGetProperty("era_features", out var eraFeatEl))
            {
                if (eraFeatEl.ValueKind != JsonValueKind.Object)
                    errors.Add("era_features must be an object mapping feature ids to 'available'|'restricted'|'unavailable' (or boolean).");
                else
                {
                    foreach (var prop in eraFeatEl.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            result.EraFeatures[prop.Name] = prop.Value.GetString() ?? "";
                        else if (prop.Value.ValueKind == JsonValueKind.True)
                            result.EraFeatures[prop.Name] = "available";
                        else if (prop.Value.ValueKind == JsonValueKind.False)
                            result.EraFeatures[prop.Name] = "unavailable";
                        else
                            errors.Add($"era_features.{prop.Name} must be a string or boolean.");
                    }
                }
            }

            // Support both nested "calendar": { ... } and shorthand top-level "era_name" / "era_short".
            if (root.TryGetProperty("calendar", out var calEl))
            {
                if (calEl.ValueKind != JsonValueKind.Object)
                    errors.Add("calendar must be an object.");
                else
                    result.Calendar = ParseCalendar(calEl, errors);
            }
            var topEraName = ReadString(root, "era_name");
            var topEraShort = ReadString(root, "era_short");
            if (!string.IsNullOrWhiteSpace(topEraName) || !string.IsNullOrWhiteSpace(topEraShort))
            {
                result.Calendar ??= new CalendarProfile();
                if (string.IsNullOrWhiteSpace(result.Calendar.EraName) && !string.IsNullOrWhiteSpace(topEraName))
                    result.Calendar.EraName = topEraName;
                if (string.IsNullOrWhiteSpace(result.Calendar.EraShort) && !string.IsNullOrWhiteSpace(topEraShort))
                    result.Calendar.EraShort = topEraShort;
            }

            if (root.TryGetProperty("featured_countries", out var featuredEl))
                ParseFeaturedCountries(featuredEl, result.FeaturedCountries, errors);
            if (root.TryGetProperty("starting_wars", out var warsEl))
                ParseStartingWars(warsEl, result.StartingWars, errors);
            if (root.TryGetProperty("starting_alliances", out var alliancesEl))
                ParseStartingAlliances(alliancesEl, result.StartingAlliances, errors);
            if (root.TryGetProperty("starting_truces", out var trucesEl))
                ParseStartingTruces(trucesEl, result.StartingTruces, errors);

            manifest = result;
            return !string.IsNullOrWhiteSpace(result.Id) && errors.Count == 0;
        }
        catch (Exception ex)
        {
            errors.Add($"Malformed JSON/YAML: {ex.Message}");
            return false;
        }
    }

    public static CalendarProfile ParseCalendar(JsonElement el, List<string> errors)
    {
        var profile = new CalendarProfile
        {
            CalendarId = ReadString(el, "id"),
            EraName = ReadString(el, "era_name"),
            EraShort = ReadString(el, "era_short"),
            YearOffset = ReadNullableInt(el, "year_offset", errors) ?? 0,
            DateFormat = ReadString(el, "format"),
            YearFormat = ReadString(el, "year_format"),
            ShowEraInDate = ReadBool(el, "show_era_in_date", true)
        };
        if (el.TryGetProperty("month_names", out var monthsEl))
        {
            if (monthsEl.ValueKind != JsonValueKind.Array)
                errors?.Add("calendar.month_names must be an array of 12 strings.");
            else
            {
                var list = new List<string>();
                foreach (var m in monthsEl.EnumerateArray())
                    list.Add(m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "");
                profile.MonthNames = list.ToArray();
            }
        }
        foreach (var issue in profile.Validate())
            errors?.Add(issue);
        return profile;
    }

    private static void ParseFeaturedCountries(JsonElement el, List<FeaturedCountrySpec> target, List<string> errors)
    {
        if (el.ValueKind != JsonValueKind.Array)
        {
            errors.Add("featured_countries must be an array of tags or {tag, hook, difficulty} objects.");
            return;
        }
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var tag = item.GetString()?.Trim() ?? "";
                if (tag.Length > 0) target.Add(new FeaturedCountrySpec { Tag = tag });
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var tag = ReadString(item, "tag");
                if (string.IsNullOrWhiteSpace(tag))
                    errors.Add("featured_countries entry is missing a 'tag' string.");
                else
                    target.Add(new FeaturedCountrySpec
                    {
                        Tag = tag,
                        Hook = ReadString(item, "hook"),
                        Difficulty = ReadString(item, "difficulty")
                    });
            }
            else
                errors.Add("featured_countries entries must be strings or objects.");
        }
    }

    private static void ParseStartingWars(JsonElement el, List<StartingWarSpec> target, List<string> errors)
    {
        if (el.ValueKind != JsonValueKind.Array)
        {
            errors.Add("starting_wars must be an array of {attacker, defender, name?, justification?} objects.");
            return;
        }
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { errors.Add("starting_wars entry must be an object."); continue; }
            var attacker = ReadString(item, "attacker");
            var defender = ReadString(item, "defender");
            if (string.IsNullOrWhiteSpace(attacker) || string.IsNullOrWhiteSpace(defender))
            {
                errors.Add("starting_wars entry requires both 'attacker' and 'defender' country tags.");
                continue;
            }
            target.Add(new StartingWarSpec
            {
                Name = ReadString(item, "name"),
                Attacker = attacker,
                Defender = defender,
                Justification = string.IsNullOrWhiteSpace(ReadString(item, "justification")) ? "scenario" : ReadString(item, "justification"),
                Limited = ReadBool(item, "limited", false)
            });
        }
    }

    private static void ParseStartingAlliances(JsonElement el, List<StartingAllianceSpec> target, List<string> errors)
    {
        if (el.ValueKind != JsonValueKind.Array)
        {
            errors.Add("starting_alliances must be an array of {name?, members: [...]} objects.");
            return;
        }
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { errors.Add("starting_alliances entry must be an object."); continue; }
            var members = ReadStringList(item, "members", errors);
            if (members.Count < 2)
            {
                errors.Add("starting_alliances entry requires at least 2 member tags.");
                continue;
            }
            var charter = ReadString(item, "charter");
            target.Add(new StartingAllianceSpec
            {
                Name = ReadString(item, "name"),
                Charter = string.IsNullOrWhiteSpace(charter) ? "collective_defence" : charter,
                Members = members
            });
        }
    }

    private static void ParseStartingTruces(JsonElement el, List<StartingTruceSpec> target, List<string> errors)
    {
        if (el.ValueKind != JsonValueKind.Array)
        {
            errors.Add("starting_truces must be an array of {between: [tagA, tagB], days} objects.");
            return;
        }
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { errors.Add("starting_truces entry must be an object."); continue; }
            var between = ReadStringList(item, "between", errors);
            if (between.Count != 2)
            {
                errors.Add("starting_truces entry requires 'between' with exactly 2 country tags.");
                continue;
            }
            var days = ReadNullableInt(item, "days", errors) ?? 365;
            target.Add(new StartingTruceSpec { Between = between, Days = days });
        }
    }

    private static string ReadString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String ? (prop.GetString()?.Trim() ?? "") : "";

    private static bool ReadBool(JsonElement el, string key, bool fallback) =>
        el.TryGetProperty(key, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : fallback;

    private static int? ReadNullableInt(JsonElement el, string key, List<string> errors)
    {
        if (!el.TryGetProperty(key, out var prop) || prop.ValueKind == JsonValueKind.Null) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)) return val;
        errors?.Add($"Field '{key}' must be an integer.");
        return null;
    }

    private static List<string> ReadStringList(JsonElement el, string key, List<string> errors)
    {
        var list = new List<string>();
        if (!el.TryGetProperty(key, out var prop) || prop.ValueKind == JsonValueKind.Null) return list;
        if (prop.ValueKind != JsonValueKind.Array)
        {
            errors?.Add($"Field '{key}' must be an array of strings.");
            return list;
        }
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString()?.Trim() ?? "";
                if (text.Length > 0) list.Add(text);
            }
            else
                errors?.Add($"Field '{key}' contains a non-string entry.");
        }
        return list;
    }
}
