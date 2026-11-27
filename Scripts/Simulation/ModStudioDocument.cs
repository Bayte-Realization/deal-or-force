using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DealOrForce.Simulation;

/// <summary>Supported authoring categories in the guided Mod Content Studio.</summary>
public enum ModStudioCategory
{
    Event = 0,
    Decision = 1,
    Modifier = 2,
    StrategicDoctrine = 3,
    Disease = 4,
    Disaster = 5,
    Localization = 6,
    Technology = 7,
    UnitType = 8,
    Building = 9,
    Ideology = 10,
    PolicyLaw = 11,
    GrandProject = 12,
    DefinesOverride = 13,
    FormableNation = 14,
    Country = 15,
    MapPack = 16
}

/// <summary>UI navigation sections grouping the guided authoring categories.</summary>
public enum ModStudioSection
{
    EventsAndDecisions = 0,
    EconomyAndTech = 1,
    MilitaryAndProjects = 2,
    PoliticsAndBalance = 3,
    FormableAndWorld = 4
}

public enum ModStudioFieldKind
{
    Id = 0,
    Text = 1,
    Number = 2,
    Float = 3,
    Bool = 4,
    Choice = 5
}

public sealed record ModStudioField(
    string Key,
    string LabelKey,
    ModStudioFieldKind Kind,
    string DefaultValue,
    float Min = -100000f,
    float Max = 100000f,
    string[] Choices = null,
    bool Required = true,
    string HelpKey = "");

public sealed record ModStudioTemplate(
    ModStudioCategory Category,
    string Folder,
    string TitleKey,
    string DescriptionKey,
    IReadOnlyList<ModStudioField> Fields,
    ModStudioSection Section = ModStudioSection.EventsAndDecisions);

public sealed record ModStudioPreset(
    string Id,
    string TitleKey,
    IReadOnlyDictionary<string, string> Values);

public sealed record ModStudioIssue(bool IsError, string FieldKey, string Message);

public sealed record ModStudioValidationReport(
    bool Valid,
    IReadOnlyList<ModStudioIssue> Issues,
    string SuggestedPath,
    string EngineValidationMessage);

public sealed record ScenarioDraftAudit(
    bool Valid,
    int TotalLandProvinces,
    int AssignedLandProvinces,
    int UnassignedLandProvinces,
    int CountryCount,
    int RegionCount,
    IReadOnlyList<string> LandlessCountries,
    IReadOnlyList<string> EmptyRegions,
    IReadOnlyList<ModStudioIssue> Issues);

/// <summary>
/// Pure-C# schema-driven document model, validator, localization stub generator, and
/// ScenarioMapDraft live health auditor for the Mod Content Studio (simulation layer only).
/// </summary>
public sealed class ModStudioDocument
{
    private static readonly Regex SafeIdRegex = new(@"^[a-z][a-z0-9_]{1,63}$", RegexOptions.Compiled);
    private static readonly Regex HexColourRegex = new(@"^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);
    private static readonly JsonWriterOptions PrettyWriterOptions = new() { Indented = true };

    private static string WriteIndentedJson(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, PrettyWriterOptions))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static readonly string[] ValidModifierKeys = NationalModifiers.Keys.Select(k => k.Id).ToArray();
    private static readonly string[] ValidTechCategories = TechnologyCatalog.Categories.ToArray();
    private static readonly string[] ValidUnitSymbols = { "infantry", "motorized", "mechanized", "armor", "artillery" };
    private static readonly string[] ValidPolicyScopes = { "nation", "region", "foreign" };
    private static readonly string[] ValidPolicyCategories =
    {
        "Administration", "Civil rights", "Education", "Environmental regulation",
        "Fiscal mandate", "Industrial policy", "Labour regulation", "Migration policy",
        "Military service", "Public information", "Regional autonomy", "Regional recovery",
        "Security mandate", "Social guarantees", "Targeted foreign policy"
    };
    private static readonly string[] ValidGrandProjectCategories =
    {
        "infrastructure", "energy", "industry", "science", "defense",
        "agriculture", "aerospace", "logistics", "maritime", "communications", "reconstruction"
    };
    private static readonly string[] ValidGrandProjectGoods =
    {
        "IndustrialMaterials", "Machinery", "Electronics", "Energy",
        "Aluminium", "Oil", "Uranium", "RareEarths", "Arms", "Fertiliser"
    };

    private static readonly IReadOnlyDictionary<ModStudioCategory, ModStudioTemplate> TemplatesByCategory = BuildTemplates();
    private static readonly IReadOnlyDictionary<ModStudioCategory, IReadOnlyList<ModStudioPreset>> PresetsByCategory = BuildPresets();

    public ModStudioCategory Category { get; }
    public ModStudioTemplate Template { get; }
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private string _rawOverrideJson;

    public bool HasRawOverride => !string.IsNullOrWhiteSpace(_rawOverrideJson);

    private ModStudioDocument(ModStudioCategory category)
    {
        Category = category;
        Template = TemplatesByCategory[category];
        foreach (var field in Template.Fields)
            _values[field.Key] = field.DefaultValue ?? "";
    }

    public static IReadOnlyList<ModStudioTemplate> AllTemplates => TemplatesByCategory.Values.ToArray();

    public static ModStudioTemplate TemplateOf(ModStudioCategory category) => TemplatesByCategory[category];

    public static IReadOnlyList<ModStudioPreset> GetPresets(ModStudioCategory category) =>
        PresetsByCategory.TryGetValue(category, out var list) ? list : Array.Empty<ModStudioPreset>();

    public static string CategoryFolder(ModStudioCategory category) => TemplatesByCategory[category].Folder;

    public static ModStudioDocument CreateFromTemplate(ModStudioCategory category, string id = "", string displayName = "")
    {
        var doc = new ModStudioDocument(category);
        if (!string.IsNullOrWhiteSpace(id)) doc.SetField("id", NormalizeId(id));
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            if (doc._values.ContainsKey("title")) doc.SetField("title", displayName.Trim());
            if (doc._values.ContainsKey("name")) doc.SetField("name", displayName.Trim());
            if (doc._values.ContainsKey("en_value")) doc.SetField("en_value", displayName.Trim());
        }
        return doc;
    }

    public static string NormalizeId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "custom_entry";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is '_' or '-' or ' ' or '.') sb.Append('_');
        }
        var cleaned = Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        if (cleaned.Length < 2) cleaned = "custom_" + cleaned;
        if (!char.IsAsciiLetter(cleaned[0])) cleaned = "m_" + cleaned;
        if (cleaned.Length > 64) cleaned = cleaned[..64];
        return cleaned;
    }

    public string GetField(string key) => _values.TryGetValue(key ?? "", out var v) ? v : "";

    public void SetField(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        _rawOverrideJson = null;
        _values[key] = value ?? "";
    }

    public void LoadRawOverride(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            _rawOverrideJson = null;
            return;
        }
        try
        {
            var node = JsonNode.Parse(StructuredData.ToJson(SuggestedRelativePath(), rawJson), documentOptions: GameDataLoader.ForgivingJson);
            _rawOverrideJson = node is not null ? WriteIndentedJson(node) : rawJson;
        }
        catch
        {
            _rawOverrideJson = rawJson;
        }
    }

    public void ClearRawOverride() => _rawOverrideJson = null;

    public bool ApplyPreset(string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return false;
        var presets = GetPresets(Category);
        foreach (var preset in presets)
        {
            if (!string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase)) continue;
            _rawOverrideJson = null;
            foreach (var (k, v) in preset.Values)
                if (_values.ContainsKey(k)) _values[k] = v;
            return true;
        }
        return false;
    }

    public ModStudioDocument DuplicateAs(string newId, string newDisplayName = "")
    {
        var copy = new ModStudioDocument(Category);
        foreach (var (k, v) in _values) copy._values[k] = v;
        if (!string.IsNullOrWhiteSpace(newId)) copy._values["id"] = NormalizeId(newId);
        if (!string.IsNullOrWhiteSpace(newDisplayName))
        {
            if (copy._values.ContainsKey("title")) copy._values["title"] = newDisplayName.Trim();
            if (copy._values.ContainsKey("name")) copy._values["name"] = newDisplayName.Trim();
        }
        return copy;
    }

    public static IReadOnlyList<string> ListSavedEntries(string modRoot, ModStudioCategory category)
    {
        if (string.IsNullOrWhiteSpace(modRoot)) return Array.Empty<string>();
        var dir = Path.Combine(modRoot, CategoryFolder(category).Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        if (category == ModStudioCategory.MapPack)
        {
            var packs = new List<string>();
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var subName = Path.GetFileName(sub);
                if (string.IsNullOrWhiteSpace(subName)) continue;
                var mapFile = Directory.EnumerateFiles(sub, "map.*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(StructuredData.IsDocument);
                if (!string.IsNullOrWhiteSpace(mapFile))
                    packs.Add($"{subName}/{Path.GetFileName(mapFile)}");
            }
            return packs.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => StructuredData.IsDocument(f))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    public static bool TryLoadFromMod(string modRoot, ModStudioCategory category, string fileName,
        out ModStudioDocument document, out string error)
    {
        document = null;
        error = "";
        if (string.IsNullOrWhiteSpace(modRoot) || string.IsNullOrWhiteSpace(fileName))
        {
            error = "Mod workspace or filename is empty.";
            return false;
        }
        try
        {
            string rel;
            string stem;
            if (category == ModStudioCategory.MapPack)
            {
                var clean = fileName.Replace('\\', '/').Trim().TrimStart('/');
                if (!clean.Contains('/', StringComparison.Ordinal))
                    clean += "/map.json";
                var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    error = "Map pack entry must be '<id>/map.json'.";
                    return false;
                }
                rel = $"{CategoryFolder(category)}/{parts[0]}/{parts[1]}";
                stem = parts[0];
            }
            else
            {
                rel = CategoryFolder(category) + "/" + Path.GetFileName(fileName.Trim());
                stem = Path.GetFileNameWithoutExtension(rel);
                if (stem.StartsWith("99_studio_", StringComparison.OrdinalIgnoreCase)) stem = stem["99_studio_".Length..];
            }
            var path = ScenarioLibrary.SafePath(modRoot, rel);
            if (!File.Exists(path))
            {
                error = $"File not found: {rel}";
                return false;
            }
            var raw = File.ReadAllText(path);
            if (!TryLoadJsonIntoDraft(category, StructuredData.ToJson(rel, raw), stem, out document, out error))
                return false;

            var id = document.GetField("id").Trim();
            TryOverlayLocStubs(modRoot, category, id, document);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void TryOverlayLocStubs(string modRoot, ModStudioCategory category, string id, ModStudioDocument document)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        static Dictionary<string, string> ReadLoc(string modRoot, string lang, string id)
        {
            foreach (var candidate in new[] { $"99_studio_{id}.json", $"00_{id}.json", "00_custom_map_pack.json" })
            {
                var path = Path.Combine(modRoot, "localization", lang, candidate);
                if (!File.Exists(path)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(StructuredData.ToJson(path, File.ReadAllText(path)), GameDataLoader.ForgivingJson);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString() ?? "";
                    if (map.Count > 0) return map;
                }
                catch { }
            }
            return null;
        }
        var en = ReadLoc(modRoot, "english", id);
        var ru = ReadLoc(modRoot, "russian", id);
        if (en is null && ru is null) return;
        var prefix = LocPrefix(category, id);
        void Pick(string fieldKey, params string[] suffixes)
        {
            if (!document._values.ContainsKey(fieldKey)) return;
            foreach (var s in suffixes)
            {
                if (en is not null && en.TryGetValue(prefix + s, out var vEn) && !fieldKey.EndsWith("_ru", StringComparison.Ordinal))
                    document._values[fieldKey] = vEn;
                if (ru is not null && ru.TryGetValue(prefix + s, out var vRu) && fieldKey.EndsWith("_ru", StringComparison.Ordinal))
                    document._values[fieldKey] = vRu;
            }
        }
        if (category == ModStudioCategory.Event)
        {
            Pick("title", ".title"); Pick("title_ru", ".title");
            Pick("description", ".desc"); Pick("description_ru", ".desc");
            Pick("opt1_label", ".opt1"); Pick("opt1_consequence", ".con1");
            Pick("opt2_label", ".opt2"); Pick("opt2_consequence", ".con2");
        }
        else if (category == ModStudioCategory.Localization)
        {
            var locKey = document.GetField("loc_key").Trim();
            if (en is not null && en.TryGetValue(locKey, out var eVal)) document._values["en_value"] = eVal;
            if (ru is not null && ru.TryGetValue(locKey, out var rVal)) document._values["ru_value"] = rVal;
        }
        else
        {
            Pick("title", ".name", ".title"); Pick("name", ".name", ".title");
            Pick("title_ru", ".name", ".title");
            Pick("description", ".desc", ".summary");
            Pick("description_ru", ".desc", ".summary");
        }
    }

    private static string LocPrefix(ModStudioCategory category, string id) => category switch
    {
        ModStudioCategory.Event => $"studio.ev.{id}",
        ModStudioCategory.Decision => $"studio.dec.{id}",
        ModStudioCategory.Modifier => $"studio.mod.{id}",
        ModStudioCategory.StrategicDoctrine => $"studio.strat.{id}",
        ModStudioCategory.Disease => $"studio.dis.{id}",
        ModStudioCategory.Disaster => $"studio.haz.{id}",
        ModStudioCategory.Technology => $"studio.tech.{id}",
        ModStudioCategory.UnitType => $"studio.unit.{id}",
        ModStudioCategory.Building => $"studio.bld.{id}",
        ModStudioCategory.Ideology => $"studio.ideo.{id}",
        ModStudioCategory.PolicyLaw => $"studio.law.{id}",
        ModStudioCategory.GrandProject => $"studio.gp.{id}",
        ModStudioCategory.DefinesOverride => $"studio.def.{id}",
        ModStudioCategory.FormableNation => $"studio.form.{id}",
        ModStudioCategory.Country => $"studio.ctry.{id}",
        ModStudioCategory.MapPack => $"studio.map.{id}",
        _ => $"studio.custom.{id}"
    };

    public static bool TryLoadJsonIntoDraft(ModStudioCategory category, string jsonText, string fallbackId,
        out ModStudioDocument document, out string error)
    {
        var draft = CreateFromTemplate(category, fallbackId);
        document = draft;
        error = "";
        try
        {
            using var parsed = JsonDocument.Parse(jsonText ?? "", GameDataLoader.ForgivingJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Expected a JSON object at the document root.";
                return false;
            }
            var root = parsed.RootElement;
            if (category == ModStudioCategory.Localization)
            {
                var first = root.EnumerateObject().FirstOrDefault(p => !p.Name.StartsWith("//", StringComparison.Ordinal));
                if (!string.IsNullOrEmpty(first.Name))
                {
                    document._values["loc_key"] = first.Name;
                    document._values["en_value"] = first.Value.ValueKind == JsonValueKind.String ? first.Value.GetString() ?? "" : "";
                }
                return true;
            }
            if (category == ModStudioCategory.DefinesOverride)
            {
                static string Norm(string s) => (s ?? "").Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
                var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in root.EnumerateObject())
                    if (!p.Name.StartsWith("//", StringComparison.Ordinal)) map[Norm(p.Name)] = p.Value;
                foreach (var f in document.Template.Fields)
                {
                    if (f.Key is "id" or "title" or "title_ru") continue;
                    if (!map.TryGetValue(Norm(f.Key), out var v)) continue;
                    document._values[f.Key] = v.ValueKind switch
                    {
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Number => v.GetRawText(),
                        JsonValueKind.String => v.GetString() ?? "",
                        _ => document._values[f.Key]
                    };
                }
                return true;
            }
            if (category == ModStudioCategory.MapPack)
            {
                document._values["id"] = NormalizeId(string.IsNullOrWhiteSpace(ReadStr(root, "id")) ? fallbackId : ReadStr(root, "id"));
                if (document._values["id"].Length > 48) document._values["id"] = document._values["id"][..48];
                foreach (var f in document.Template.Fields)
                {
                    if (f.Key is "id" or "title_ru" or "description_ru") continue;
                    if (root.TryGetProperty(f.Key, out var prop))
                    {
                        if (prop.ValueKind == JsonValueKind.String)
                            document._values[f.Key] = prop.GetString() ?? "";
                        else if (prop.ValueKind == JsonValueKind.Number)
                            document._values[f.Key] = prop.GetRawText();
                        else if (prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            document._values[f.Key] = prop.GetBoolean() ? "true" : "false";
                    }
                }
                if (root.TryGetProperty("inherit_base_hydrology", out var ih) && ih.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    document._values["inherits_base_hydrology"] = ih.GetBoolean() ? "true" : "false";
                return true;

                static string ReadStr(JsonElement obj, string key) =>
                    obj.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
            }
            var entries = root.EnumerateObject().Where(p => !p.Name.StartsWith("//", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.Object).ToList();
            if (entries.Count == 0)
            {
                error = "No object entries found in document.";
                return false;
            }
            var entry = entries[0];
            if (category == ModStudioCategory.Country)
            {
                document._values["tag"] = entry.Name.Trim().ToUpperInvariant();
                document._values["id"] = NormalizeId(string.IsNullOrWhiteSpace(fallbackId) ? entry.Name : fallbackId);
            }
            else
            {
                document._values["id"] = SafeIdRegex.IsMatch(entry.Name) ? entry.Name : NormalizeId(entry.Name);
            }
            var body = entry.Value;

            void CopyScalar(string fieldKey, string jsonKey)
            {
                if (!draft._values.ContainsKey(fieldKey)) return;
                if (body.TryGetProperty(jsonKey, out var el))
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var val = el.GetString() ?? "";
                        if (!val.StartsWith("studio.", StringComparison.Ordinal)) draft._values[fieldKey] = val;
                    }
                    else if (el.ValueKind == JsonValueKind.Number) draft._values[fieldKey] = el.GetRawText();
                    else if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) draft._values[fieldKey] = el.GetBoolean() ? "true" : "false";
                }
            }
            foreach (var f in document.Template.Fields)
                if (f.Key != "id" && !(category == ModStudioCategory.Country && f.Key == "tag")) CopyScalar(f.Key, f.Key);
            CopyScalar("description", "summary");
            if (category == ModStudioCategory.Country)
            {
                CopyScalar("colour", "color");
                CopyScalar("starting_treasury", "treasury");
            }
            else if (category == ModStudioCategory.FormableNation)
            {
                if (body.TryGetProperty("eligible_tags", out var et) && et.ValueKind == JsonValueKind.Array)
                    document._values["tag"] = string.Join(", ", et.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => (x.GetString() ?? "").Trim().ToUpperInvariant()).Where(x => x.Length > 0));
                if (body.TryGetProperty("required_regions", out var rr) && rr.ValueKind == JsonValueKind.Array)
                    document._values["required_regions"] = string.Join(", ", rr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => (x.GetString() ?? "").Trim().ToLowerInvariant()).Where(x => x.Length > 0));
                if (body.TryGetProperty("eligible_cultures", out var ec) && ec.ValueKind == JsonValueKind.Array)
                    document._values["allowed_cultures"] = string.Join(", ", ec.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => (x.GetString() ?? "").Trim().ToLowerInvariant()).Where(x => x.Length > 0));
            }

            if (category == ModStudioCategory.Event)
            {
                if (body.TryGetProperty("trigger", out var trig) && trig.ValueKind == JsonValueKind.Object)
                    CopyFromObj(trig, "max_stability", "max_stability");
                if (body.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    var arr = opts.EnumerateArray().ToArray();
                    if (arr.Length > 0 && arr[0].ValueKind == JsonValueKind.Object)
                    {
                        if (arr[0].TryGetProperty("effects", out var e1) && e1.ValueKind == JsonValueKind.Object)
                        {
                            CopyFromObj(e1, "opt1_income_share", "income_share");
                            CopyFromObj(e1, "opt1_stability", "stability");
                            CopyFromObj(e1, "opt1_pp", "political_points");
                            CopyFromObj(e1, "opt1_prestige", "prestige");
                        }
                    }
                    if (arr.Length > 1 && arr[1].ValueKind == JsonValueKind.Object)
                    {
                        if (arr[1].TryGetProperty("effects", out var e2) && e2.ValueKind == JsonValueKind.Object)
                        {
                            CopyFromObj(e2, "opt2_gdp_share", "gdp_share");
                            CopyFromObj(e2, "opt2_stability", "stability");
                            CopyFromObj(e2, "opt2_prestige", "prestige");
                        }
                    }
                }
            }
            else if (category == ModStudioCategory.Decision)
            {
                if (body.TryGetProperty("resource_costs", out var rc) && rc.ValueKind == JsonValueKind.Object)
                    CopyFromObj(rc, "pp_cost", "political_points");
                if (body.TryGetProperty("trigger", out var tr) && tr.ValueKind == JsonValueKind.Object)
                {
                    CopyFromObj(tr, "min_stability", "min_stability");
                    CopyFromObj(tr, "min_prestige", "min_prestige");
                }
                if (body.TryGetProperty("effects", out var ef) && ef.ValueKind == JsonValueKind.Object)
                {
                    CopyFromObj(ef, "effect_income_share", "income_share");
                    CopyFromObj(ef, "effect_prestige", "prestige");
                    CopyFromObj(ef, "effect_stability", "stability");
                    CopyFromObj(ef, "effect_war_support", "war_support");
                }
            }
            else if (category is ModStudioCategory.Modifier or ModStudioCategory.StrategicDoctrine or ModStudioCategory.Ideology or ModStudioCategory.PolicyLaw)
            {
                if (body.TryGetProperty("modifiers", out var mods) && mods.ValueKind == JsonValueKind.Object)
                {
                    var firstMod = mods.EnumerateObject().FirstOrDefault(p => !p.Name.StartsWith("//", StringComparison.Ordinal));
                    if (!string.IsNullOrEmpty(firstMod.Name) && firstMod.Value.ValueKind == JsonValueKind.Number)
                    {
                        document._values["modifier_key"] = firstMod.Name;
                        document._values["modifier_value"] = firstMod.Value.GetRawText();
                    }
                }
            }
            else if (category == ModStudioCategory.Technology)
            {
                if (body.TryGetProperty("requires", out var req) && req.ValueKind == JsonValueKind.Array)
                    document._values["requires"] = string.Join(", ", req.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
                if (body.TryGetProperty("effects", out var eff) && eff.ValueKind == JsonValueKind.Object)
                {
                    var firstMod = eff.EnumerateObject().FirstOrDefault(p => !p.Name.StartsWith("//", StringComparison.Ordinal));
                    if (!string.IsNullOrEmpty(firstMod.Name) && firstMod.Value.ValueKind == JsonValueKind.Number)
                    {
                        document._values["modifier_key"] = firstMod.Name;
                        document._values["modifier_value"] = firstMod.Value.GetRawText();
                    }
                }
            }
            else if (category == ModStudioCategory.GrandProject)
            {
                if (body.TryGetProperty("stage_days", out var sd) && sd.ValueKind == JsonValueKind.Array)
                {
                    var arr = sd.EnumerateArray().ToArray();
                    if (arr.Length >= 3)
                    {
                        document._values["stage_1_days"] = arr[0].GetRawText();
                        document._values["stage_2_days"] = arr[1].GetRawText();
                        document._values["stage_3_days"] = arr[2].GetRawText();
                    }
                }
                if (body.TryGetProperty("key_goods", out var kg) && kg.ValueKind == JsonValueKind.Array)
                {
                    var firstGood = kg.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.String);
                    if (firstGood.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(firstGood.GetString()))
                        document._values["key_good"] = firstGood.GetString()!;
                }
                if (body.TryGetProperty("host_modifiers", out var hm) && hm.ValueKind == JsonValueKind.Object)
                {
                    var firstMod = hm.EnumerateObject().FirstOrDefault(p => !p.Name.StartsWith("//", StringComparison.Ordinal));
                    if (!string.IsNullOrEmpty(firstMod.Name) && firstMod.Value.ValueKind == JsonValueKind.Number)
                    {
                        document._values["modifier_key"] = firstMod.Name;
                        document._values["modifier_value"] = firstMod.Value.GetRawText();
                    }
                }
            }

            if (category != ModStudioCategory.MapPack && entries.Count > 1)
                document._rawOverrideJson = WriteIndentedJson(JsonNode.Parse(jsonText, documentOptions: GameDataLoader.ForgivingJson)!);
            return true;

            void CopyFromObj(JsonElement src, string fieldKey, string propName)
            {
                if (draft._values.ContainsKey(fieldKey) && src.TryGetProperty(propName, out var pv))
                {
                    if (pv.ValueKind == JsonValueKind.Number) draft._values[fieldKey] = pv.GetRawText();
                    else if (pv.ValueKind == JsonValueKind.String) draft._values[fieldKey] = pv.GetString() ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public string SuggestedRelativePath()
    {
        var id = GetField("id").Trim();
        var fileId = SafeIdRegex.IsMatch(id) ? id : "custom_entry";
        if (Category == ModStudioCategory.MapPack)
            return $"maps/{fileId}/map.json";
        return $"{CategoryFolder(Category)}/99_studio_{fileId}.json";
    }

    public string RenderJson()
    {
        if (HasRawOverride) return _rawOverrideJson!;
        var id = GetField("id").Trim();
        var root = new JsonObject();
        switch (Category)
        {
            case ModStudioCategory.Event:
            {
                var eff1 = new JsonObject();
                var inc1 = ParseFloat("opt1_income_share", -0.04f);
                if (MathF.Abs(inc1) > 1e-6f) eff1["income_share"] = inc1;
                eff1["stability"] = ParseFloat("opt1_stability", 0.025f);
                eff1["political_points"] = ParseFloat("opt1_pp", -10f);
                var p1 = ParseFloat("opt1_prestige", 6f);
                if (MathF.Abs(p1) > 1e-6f) eff1["prestige"] = p1;

                var eff2 = new JsonObject();
                var gdp2 = ParseFloat("opt2_gdp_share", -0.002f);
                if (MathF.Abs(gdp2) > 1e-6f) eff2["gdp_share"] = gdp2;
                eff2["stability"] = ParseFloat("opt2_stability", -0.01f);
                var p2 = ParseFloat("opt2_prestige", -4f);
                if (MathF.Abs(p2) > 1e-6f) eff2["prestige"] = p2;

                var opt1 = new JsonObject
                {
                    ["title"] = $"studio.ev.{id}.opt1",
                    ["consequence"] = $"studio.ev.{id}.con1",
                    ["effects"] = eff1
                };
                var opt2 = new JsonObject
                {
                    ["title"] = $"studio.ev.{id}.opt2",
                    ["consequence"] = $"studio.ev.{id}.con2",
                    ["effects"] = eff2
                };
                root[id] = new JsonObject
                {
                    ["title"] = $"studio.ev.{id}.title",
                    ["description"] = $"studio.ev.{id}.desc",
                    ["trigger"] = new JsonObject { ["max_stability"] = ParseFloat("max_stability", 0.85f) },
                    ["weight"] = ParseFloat("weight", 1.0f),
                    ["options"] = new JsonArray { opt1, opt2 }
                };
                break;
            }
            case ModStudioCategory.Decision:
            {
                var trigger = new JsonObject { ["min_stability"] = ParseFloat("min_stability", 0.35f) };
                var minPrestige = ParseFloat("min_prestige", 0f);
                if (minPrestige > 0f) trigger["min_prestige"] = minPrestige;

                var effects = new JsonObject
                {
                    ["stability"] = ParseFloat("effect_stability", 0.03f),
                    ["war_support"] = ParseFloat("effect_war_support", 0.02f)
                };
                var incShare = ParseFloat("effect_income_share", -0.02f);
                if (MathF.Abs(incShare) > 1e-6f) effects["income_share"] = incShare;
                var effPrestige = ParseFloat("effect_prestige", 8f);
                if (MathF.Abs(effPrestige) > 1e-6f) effects["prestige"] = effPrestige;

                var body = new JsonObject
                {
                    ["name"] = $"studio.dec.{id}.name",
                    ["description"] = $"studio.dec.{id}.desc",
                    ["unavailable"] = $"studio.dec.{id}.unavail",
                    ["resource_costs"] = new JsonObject { ["political_points"] = ParseInt("pp_cost", 20) },
                    ["cooldown_days"] = ParseInt("cooldown_days", 365)
                };
                var gdpCost = ParseFloat("gdp_cost_share", 0.002f);
                if (gdpCost > 0f) body["gdp_cost_share"] = gdpCost;
                body["trigger"] = trigger;
                body["effects"] = effects;
                root[id] = body;
                break;
            }
            case ModStudioCategory.Modifier:
            {
                var modKey = GetField("modifier_key").Trim();
                if (string.IsNullOrEmpty(modKey)) modKey = "output";
                root[id] = new JsonObject
                {
                    ["name"] = $"studio.mod.{id}.name",
                    ["description"] = $"studio.mod.{id}.desc",
                    ["days"] = ParseInt("days", 365),
                    ["positive"] = ParseBool("positive", true),
                    ["effects"] = new JsonObject(),
                    ["modifiers"] = new JsonObject { [modKey] = ParseFloat("modifier_value", 0.08f) }
                };
                break;
            }
            case ModStudioCategory.StrategicDoctrine:
            {
                var modKey = GetField("modifier_key").Trim();
                if (string.IsNullOrEmpty(modKey)) modKey = "army_attack";
                root[id] = new JsonObject
                {
                    ["name"] = $"studio.strat.{id}.name",
                    ["description"] = $"studio.strat.{id}.desc",
                    ["political_cost"] = ParseFloat("political_cost", 40f),
                    ["training_slots"] = ParseFloat("training_slots", 1.0f),
                    ["drill_experience"] = ParseFloat("drill_experience", 1.15f),
                    ["battle_experience"] = ParseFloat("battle_experience", 1.10f),
                    ["air_experience"] = ParseFloat("air_experience", 1.0f),
                    ["naval_experience"] = ParseFloat("naval_experience", 1.0f),
                    ["tactical_bias"] = GetField("tactical_bias").Trim(),
                    ["modifiers"] = new JsonObject { [modKey] = ParseFloat("modifier_value", 0.08f) }
                };
                break;
            }
            case ModStudioCategory.Disease:
            {
                root[id] = new JsonObject
                {
                    ["name"] = $"studio.dis.{id}.name",
                    ["summary"] = $"studio.dis.{id}.summary",
                    ["cause_summary"] = $"studio.dis.{id}.cause",
                    ["category"] = GetField("category").Trim(),
                    ["min_abs_lat"] = ParseFloat("min_abs_lat", 0f),
                    ["max_abs_lat"] = ParseFloat("max_abs_lat", 90f),
                    ["preferred_seasons"] = new JsonArray { "any" },
                    ["annual_chance"] = ParseFloat("annual_chance", 0.04f),
                    ["starting_severity"] = ParseFloat("starting_severity", 30f),
                    ["weekly_severity_drift"] = ParseFloat("weekly_severity_drift", 3.5f),
                    ["base_duration_days"] = ParseInt("base_duration_days", 90),
                    ["cooldown_days"] = ParseInt("cooldown_days", 365),
                    ["spread_rate"] = ParseFloat("spread_rate", 0.30f),
                    ["mortality_annual_at_max"] = ParseFloat("mortality_annual_at_max", 0.004f),
                    ["economy_cost_gdp_share"] = ParseFloat("economy_cost_gdp_share", 0.006f),
                    ["recession_depth_max"] = 0.025f,
                    ["stability_hit"] = 0.006f,
                    ["weekly_stability_hit"] = 0.0015f,
                    ["weekly_radicalism_hit"] = 0.0012f,
                    ["weekly_living_hit"] = 0.0014f
                };
                break;
            }
            case ModStudioCategory.Disaster:
            {
                root[id] = new JsonObject
                {
                    ["name"] = $"studio.haz.{id}.name",
                    ["summary"] = $"studio.haz.{id}.summary",
                    ["cause_summary"] = $"studio.haz.{id}.cause",
                    ["category"] = GetField("category").Trim(),
                    ["min_abs_lat"] = ParseFloat("min_abs_lat", 0f),
                    ["max_abs_lat"] = ParseFloat("max_abs_lat", 90f),
                    ["preferred_seasons"] = new JsonArray { "any" },
                    ["requires_coastal"] = ParseBool("requires_coastal", false),
                    ["requires_inland_water"] = false,
                    ["annual_chance"] = ParseFloat("annual_chance", 0.03f),
                    ["starting_severity"] = ParseFloat("starting_severity", 52f),
                    ["weekly_recovery_rate"] = ParseFloat("weekly_recovery_rate", 5.5f),
                    ["base_duration_days"] = ParseInt("base_duration_days", 60),
                    ["cooldown_days"] = ParseInt("cooldown_days", 320),
                    ["instant_damage"] = ParseFloat("instant_damage", 0.14f),
                    ["instant_deaths_share"] = 0.0002f,
                    ["weekly_deaths_annual"] = 0.0012f,
                    ["economy_cost_gdp_share"] = ParseFloat("economy_cost_gdp_share", 0.008f),
                    ["stability_hit"] = 0.012f,
                    ["weekly_stability_hit"] = 0.0014f,
                    ["weekly_radicalism_hit"] = 0.0016f
                };
                break;
            }
            case ModStudioCategory.Localization:
            {
                var locKey = GetField("loc_key").Trim();
                root[string.IsNullOrEmpty(locKey) ? $"studio.custom.{id}" : locKey] = GetField("en_value");
                break;
            }
            case ModStudioCategory.Technology:
            {
                var modKey = GetField("modifier_key").Trim();
                if (string.IsNullOrEmpty(modKey)) modKey = "output";
                var body = new JsonObject
                {
                    ["name"] = $"studio.tech.{id}.name",
                    ["description"] = $"studio.tech.{id}.desc",
                    ["category"] = GetField("category").Trim(),
                    ["cost"] = ParseInt("cost", 160),
                    ["effects"] = new JsonObject { [modKey] = ParseFloat("modifier_value", 0.04f) }
                };
                var reqRaw = GetField("requires").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                if (reqRaw.Length > 0)
                {
                    var reqArr = new JsonArray();
                    foreach (var r in reqRaw) reqArr.Add(r);
                    body["requires"] = reqArr;
                }
                root[id] = body;
                break;
            }
            case ModStudioCategory.UnitType:
            {
                root[id] = new JsonObject
                {
                    ["name"] = $"studio.unit.{id}.name",
                    ["counter_symbol"] = GetField("counter_symbol").Trim(),
                    ["attack"] = ParseFloat("attack", 1.05f),
                    ["soft_attack"] = ParseFloat("soft_attack", 1.10f),
                    ["hard_attack"] = ParseFloat("hard_attack", 0.35f),
                    ["defense"] = ParseFloat("defense", 1.25f),
                    ["breakthrough"] = ParseFloat("breakthrough", 0.75f),
                    ["organisation"] = ParseFloat("organisation", 1.02f),
                    ["speed"] = ParseFloat("speed", 0.95f),
                    ["combat_width"] = ParseFloat("combat_width", 18f),
                    ["supply_use"] = ParseFloat("supply_use", 0.90f),
                    ["recruitment_cost"] = ParseFloat("recruitment_cost", 1.15f),
                    ["training_days"] = ParseInt("training_days", 55)
                };
                break;
            }
            case ModStudioCategory.Building:
            {
                root[id] = new JsonObject
                {
                    ["name"] = $"studio.bld.{id}.name",
                    ["description"] = $"studio.bld.{id}.desc",
                    ["cost"] = ParseFloat("cost", 2.20f),
                    ["days"] = ParseInt("days", 240),
                    ["capacity_gain"] = ParseFloat("capacity_gain", 0.12f),
                    ["jobs"] = ParseFloat("jobs", 0.45f),
                    ["needs_water"] = ParseBool("needs_water", false)
                };
                break;
            }
            case ModStudioCategory.Ideology:
            {
                var modKey = GetField("modifier_key").Trim();
                if (string.IsNullOrEmpty(modKey)) modKey = "research_speed";
                root[id] = new JsonObject
                {
                    ["name"] = $"studio.ideo.{id}.name",
                    ["description"] = $"studio.ideo.{id}.desc",
                    ["colour"] = GetField("colour").Trim(),
                    ["order"] = ParseInt("order", 95),
                    ["economic"] = ParseFloat("economic", 0.15f),
                    ["authority"] = ParseFloat("authority", 0.20f),
                    ["militia"] = ParseFloat("militia", 0.95f),
                    ["modifiers"] = new JsonObject { [modKey] = ParseFloat("modifier_value", 0.06f) }
                };
                break;
            }
            case ModStudioCategory.PolicyLaw:
            {
                var scope = GetField("scope").Trim().ToLowerInvariant();
                if (scope.Length == 0) scope = "nation";
                var modKey = GetField("modifier_key").Trim();
                if (string.IsNullOrEmpty(modKey)) modKey = "output";
                var body = new JsonObject
                {
                    ["name"] = $"studio.law.{id}.name",
                    ["description"] = $"studio.law.{id}.desc",
                    ["category"] = GetField("category").Trim(),
                    ["scope"] = scope,
                    ["cost_share"] = ParseFloat("cost_share", 0.002f),
                    ["cooldown_days"] = ParseInt("cooldown_days", 180),
                    ["duration_days"] = ParseInt("duration_days", 730),
                    ["requires_war"] = ParseBool("requires_war", false)
                };
                var trust = ParseFloat("foreign_trust", 0f);
                if (MathF.Abs(trust) > 1e-6f || scope == "foreign") body["foreign_trust"] = trust;
                if (scope != "foreign") body["modifiers"] = new JsonObject { [modKey] = ParseFloat("modifier_value", 0.05f) };
                root[id] = body;
                break;
            }
            case ModStudioCategory.GrandProject:
            {
                var modKey = GetField("modifier_key").Trim();
                if (string.IsNullOrEmpty(modKey)) modKey = "output";
                root[id] = new JsonObject
                {
                    ["name"] = $"studio.gp.{id}.name",
                    ["description"] = $"studio.gp.{id}.desc",
                    ["category"] = GetField("category").Trim(),
                    ["order"] = 120,
                    ["stage_days"] = new JsonArray
                    {
                        ParseInt("stage_1_days", 90),
                        ParseInt("stage_2_days", 150),
                        ParseInt("stage_3_days", 90)
                    },
                    ["base_cost_billions"] = ParseFloat("base_cost_billions", 1.5f),
                    ["gdp_cost_share"] = ParseFloat("gdp_cost_share", 0.0025f),
                    ["political_cost"] = ParseFloat("political_cost", 15f),
                    ["key_goods"] = new JsonArray { GetField("key_good").Trim() },
                    ["host_modifiers"] = new JsonObject { [modKey] = ParseFloat("modifier_value", 0.06f) }
                };
                break;
            }
            case ModStudioCategory.DefinesOverride:
            {
                root["MaxMonthlyGrowth"] = ParseFloat("max_monthly_growth", 0.006f);
                root["GdpConvergenceRate"] = ParseFloat("gdp_convergence_rate", 0.10f);
                root["ShortageAnnualGrowthPenalty"] = ParseFloat("shortage_annual_growth_penalty", 0.08f);
                root["DefaultTaxRate"] = ParseFloat("default_tax_rate", 0.21f);
                root["AiMaximumTaxRate"] = ParseFloat("ai_maximum_tax_rate", 0.46f);
                root["NotorietyPerUnjustifiedWar"] = ParseFloat("notoriety_per_unjustified_war", 18f);
                root["AiNotorietyCaution"] = ParseFloat("ai_notoriety_caution", 1.30f);
                root["PeaceWearinessAfterDays"] = ParseInt("peace_weariness_after_days", 450);
                root["PeaceWearinessPerYear"] = ParseFloat("peace_weariness_per_year", 15f);
                root["AiWarWearinessCaution"] = ParseFloat("ai_war_weariness_caution", 1.60f);
                root["AiAggression"] = ParseFloat("ai_aggression", 1.0f);
                root["AiWarChance"] = ParseFloat("ai_war_chance", 0.08f);
                root["MainlandReclamationEnabled"] = ParseBool("mainland_reclamation_enabled", true);
                root["AiMainlandReturnChance"] = ParseFloat("ai_mainland_return_chance", 0.12f);
                root["PrestigeEnabled"] = ParseBool("prestige_enabled", true);
                root["PrestigeWeeklyDriftRate"] = ParseFloat("prestige_weekly_drift_rate", 0.015f);
                break;
            }
            case ModStudioCategory.FormableNation:
            {
                static string[] SplitList(string raw, bool upper) => (raw ?? "")
                    .Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => upper ? s.Trim().ToUpperInvariant() : s.Trim().ToLowerInvariant())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var tags = SplitList(GetField("tag"), upper: true);
                var regions = SplitList(GetField("required_regions"), upper: false);
                var cultures = SplitList(GetField("allowed_cultures"), upper: false);
                var stabBonus = ParseFloat("stability_bonus", 0.04f);
                var prestigeBonus = ParseFloat("prestige_bonus", 20f);
                var unlocksEvent = GetField("unlocks_event").Trim();

                var tagArr = new JsonArray();
                foreach (var t in tags) tagArr.Add(t);
                var regionArr = new JsonArray();
                foreach (var r in regions) regionArr.Add(r);

                var body = new JsonObject
                {
                    ["name"] = $"studio.form.{id}.name",
                    ["description"] = $"studio.form.{id}.desc",
                    ["tag"] = string.Join(", ", tags),
                    ["eligible_tags"] = tagArr,
                    ["required_regions"] = regionArr,
                    ["minimum_stability"] = 0.65f,
                    ["gdp_cost_share"] = 0.02f,
                    ["governance_gain"] = Math.Clamp(stabBonus, 0f, 1f),
                    ["productivity_gain"] = 0.02f,
                    ["prestige_bonus"] = prestigeBonus,
                    ["stability_bonus"] = stabBonus
                };
                if (regions.Length == 0 && tags.Length > 0)
                {
                    var reqTags = new JsonArray();
                    foreach (var t in tags) reqTags.Add(t);
                    body["required_tags"] = reqTags;
                }
                if (cultures.Length > 0)
                {
                    var culArr = new JsonArray();
                    foreach (var c in cultures) culArr.Add(c);
                    body["eligible_cultures"] = culArr;
                }
                if (!string.IsNullOrEmpty(unlocksEvent))
                    body["unlocks_event"] = unlocksEvent;
                root[id] = body;
                break;
            }
            case ModStudioCategory.Country:
            {
                var tag = GetField("tag").Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(tag)) tag = "PRT";
                var colour = GetField("colour").Trim();
                if (string.IsNullOrWhiteSpace(colour)) colour = "#3A7CA5";
                var treasury = ParseFloat("starting_treasury", 25f);
                root[tag] = new JsonObject
                {
                    ["name"] = $"studio.ctry.{id}.name",
                    ["description"] = $"studio.ctry.{id}.desc",
                    ["color"] = colour,
                    ["colour"] = colour,
                    ["government_form"] = GetField("government_form").Trim(),
                    ["course"] = GetField("course").Trim(),
                    ["capital_region"] = GetField("capital_region").Trim(),
                    ["starting_treasury"] = treasury,
                    ["treasury"] = treasury
                };
                break;
            }
            case ModStudioCategory.MapPack:
            {
                var mapName = GetField("name").Trim();
                if (string.IsNullOrWhiteSpace(mapName)) mapName = id;
                var hydro = ParseBool("inherits_base_hydrology", true);
                root["id"] = id;
                root["name"] = mapName;
                root["description"] = GetField("description").Trim();
                root["author"] = GetField("author").Trim();
                root["cells_per_province"] = ParseInt("cells_per_province", 40);
                root["disputed_cells_per_province"] = ParseInt("disputed_cells_per_province", 36);
                root["siberia_multiplier"] = ParseFloat("siberia_multiplier", 1.35f);
                root["polar_multiplier"] = ParseFloat("polar_multiplier", 2.4f);
                root["inherit_base_hydrology"] = hydro;
                root["inherits_base_hydrology"] = hydro;
                break;
            }
        }
        return WriteIndentedJson(root);
    }

    public void RenderLocalizationStubs(out string enJson, out string ruJson)
    {
        var id = GetField("id").Trim();
        var en = new JsonObject();
        var ru = new JsonObject();
        var titleEn = GetField("title");
        if (string.IsNullOrWhiteSpace(titleEn)) titleEn = GetField("name");
        var descEn = GetField("description");
        var titleRu = string.IsNullOrWhiteSpace(GetField("title_ru")) ? titleEn : GetField("title_ru");
        var descRu = string.IsNullOrWhiteSpace(GetField("description_ru")) ? descEn : GetField("description_ru");

        switch (Category)
        {
            case ModStudioCategory.Event:
                en[$"studio.ev.{id}.title"] = titleEn;
                ru[$"studio.ev.{id}.title"] = titleRu;
                en[$"studio.ev.{id}.desc"] = descEn;
                ru[$"studio.ev.{id}.desc"] = descRu;
                en[$"studio.ev.{id}.opt1"] = GetField("opt1_label");
                ru[$"studio.ev.{id}.opt1"] = GetField("opt1_label");
                en[$"studio.ev.{id}.con1"] = GetField("opt1_consequence");
                ru[$"studio.ev.{id}.con1"] = GetField("opt1_consequence");
                en[$"studio.ev.{id}.opt2"] = GetField("opt2_label");
                ru[$"studio.ev.{id}.opt2"] = GetField("opt2_label");
                en[$"studio.ev.{id}.con2"] = GetField("opt2_consequence");
                ru[$"studio.ev.{id}.con2"] = GetField("opt2_consequence");
                break;
            case ModStudioCategory.Decision:
                en[$"studio.dec.{id}.name"] = titleEn;
                ru[$"studio.dec.{id}.name"] = titleRu;
                en[$"studio.dec.{id}.desc"] = descEn;
                ru[$"studio.dec.{id}.desc"] = descRu;
                en[$"studio.dec.{id}.unavail"] = "Requirements not currently met.";
                ru[$"studio.dec.{id}.unavail"] = "Условия сейчас не выполнены.";
                break;
            case ModStudioCategory.Disease:
            case ModStudioCategory.Disaster:
            {
                var prefix = LocPrefix(Category, id);
                en[$"{prefix}.name"] = titleEn;
                ru[$"{prefix}.name"] = titleRu;
                en[$"{prefix}.summary"] = descEn;
                ru[$"{prefix}.summary"] = descRu;
                en[$"{prefix}.cause"] = descEn;
                ru[$"{prefix}.cause"] = descRu;
                break;
            }
            case ModStudioCategory.UnitType:
            {
                var prefix = LocPrefix(Category, id);
                en[$"{prefix}.name"] = titleEn;
                ru[$"{prefix}.name"] = titleRu;
                break;
            }
            case ModStudioCategory.DefinesOverride:
            {
                var prefix = LocPrefix(Category, id);
                en[$"{prefix}.title"] = string.IsNullOrWhiteSpace(titleEn) ? id : titleEn;
                ru[$"{prefix}.title"] = string.IsNullOrWhiteSpace(titleRu) ? id : titleRu;
                break;
            }
            case ModStudioCategory.Localization:
                var locKey = string.IsNullOrWhiteSpace(GetField("loc_key")) ? $"studio.custom.{id}" : GetField("loc_key").Trim();
                en[locKey] = GetField("en_value");
                ru[locKey] = string.IsNullOrWhiteSpace(GetField("ru_value")) ? GetField("en_value") : GetField("ru_value");
                break;
            default:
            {
                var prefix = LocPrefix(Category, id);
                en[$"{prefix}.name"] = titleEn;
                ru[$"{prefix}.name"] = titleRu;
                en[$"{prefix}.desc"] = descEn;
                ru[$"{prefix}.desc"] = descRu;
                break;
            }
        }
        enJson = WriteIndentedJson(en);
        ruJson = WriteIndentedJson(ru);
    }

    public ModStudioValidationReport Validate()
    {
        var issues = new List<ModStudioIssue>();
        if (HasRawOverride)
        {
            var id = GetField("id").Trim();
            if (!SafeIdRegex.IsMatch(id))
                issues.Add(new ModStudioIssue(true, "id", "Identifier must start with a lowercase letter and contain only a-z, 0-9, or _ (2–64 chars)."));
        }
        else
        {
            foreach (var field in Template.Fields)
            {
                var raw = GetField(field.Key).Trim();
                if (field.Required && string.IsNullOrEmpty(raw))
                {
                    issues.Add(new ModStudioIssue(true, field.Key, $"Field '{field.Key}' is required."));
                    continue;
                }
                if (string.IsNullOrEmpty(raw)) continue;

                switch (field.Kind)
                {
                    case ModStudioFieldKind.Id:
                        if (!SafeIdRegex.IsMatch(raw))
                            issues.Add(new ModStudioIssue(true, field.Key, "Identifier must start with a lowercase letter and contain only a-z, 0-9, or _ (2–64 chars)."));
                        break;
                    case ModStudioFieldKind.Number:
                        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                            issues.Add(new ModStudioIssue(true, field.Key, $"'{field.Key}' must be an integer."));
                        else if (iv < field.Min || iv > field.Max)
                            issues.Add(new ModStudioIssue(true, field.Key, $"'{field.Key}' must be between {field.Min:0} and {field.Max:0}."));
                        break;
                    case ModStudioFieldKind.Float:
                        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv) || !float.IsFinite(fv))
                            issues.Add(new ModStudioIssue(true, field.Key, $"'{field.Key}' must be a finite number."));
                        else if (fv < field.Min || fv > field.Max)
                            issues.Add(new ModStudioIssue(true, field.Key, $"'{field.Key}' must be between {field.Min:0.###} and {field.Max:0.###}."));
                        break;
                    case ModStudioFieldKind.Bool:
                        if (!bool.TryParse(raw, out _))
                            issues.Add(new ModStudioIssue(true, field.Key, $"'{field.Key}' must be true or false."));
                        break;
                    case ModStudioFieldKind.Choice:
                        if (field.Choices is { Length: > 0 } && !field.Choices.Contains(raw, StringComparer.OrdinalIgnoreCase))
                            issues.Add(new ModStudioIssue(true, field.Key, $"'{field.Key}' must be one of: {string.Join(", ", field.Choices)}."));
                        break;
                }
            }

            if (Category is ModStudioCategory.Disease or ModStudioCategory.Disaster)
            {
                var minLat = ParseFloat("min_abs_lat", 0f);
                var maxLat = ParseFloat("max_abs_lat", 90f);
                if (minLat > maxLat)
                    issues.Add(new ModStudioIssue(true, "min_abs_lat", "Minimum latitude cannot exceed maximum latitude."));
            }
            if (Category is ModStudioCategory.Ideology or ModStudioCategory.Country &&
                !HexColourRegex.IsMatch(GetField("colour").Trim()))
                issues.Add(new ModStudioIssue(true, "colour", "Colour must be a 6-digit hex literal such as #4B9CD3."));
            if (Category == ModStudioCategory.Country)
            {
                var tag = GetField("tag").Trim();
                if (!Regex.IsMatch(tag, "^[A-Za-z][A-Za-z0-9]{1,5}$"))
                    issues.Add(new ModStudioIssue(true, "tag", "Country tag must be 2–6 letters or digits (typically 3 uppercase ISO letters such as PRT)."));
                var gf = GetField("government_form").Trim();
                if (gf.Length > 0 && GovernmentCatalog.All.Count > 0 && !GovernmentCatalog.Knows(gf))
                    issues.Add(new ModStudioIssue(true, "government_form", $"Unknown government form '{gf}'."));
                var course = GetField("course").Trim();
                if (course.Length > 0 && NationalCourseCatalog.All.Any() && NationalCourseCatalog.Find(course) is null)
                    issues.Add(new ModStudioIssue(true, "course", $"Unknown national course '{course}'."));
            }
            if (Category == ModStudioCategory.MapPack)
            {
                var mapId = GetField("id").Trim();
                if (!Regex.IsMatch(mapId, "^[a-z0-9_]{2,48}$"))
                    issues.Add(new ModStudioIssue(true, "id", "Map pack id must be 2–48 lowercase letters, digits, or underscores."));
            }
            if (Category == ModStudioCategory.PolicyLaw &&
                string.Equals(GetField("scope").Trim(), "region", StringComparison.OrdinalIgnoreCase) &&
                GetField("modifier_key").Trim().ToLowerInvariant() is not ("radicalism_weekly" or "living_weekly" or "stability_weekly"))
                issues.Add(new ModStudioIssue(true, "modifier_key", "Regional policies support only radicalism_weekly, living_weekly, or stability_weekly."));
            if (Category == ModStudioCategory.GrandProject && !CommodityCatalog.TryParse(GetField("key_good").Trim(), out _))
                issues.Add(new ModStudioIssue(true, "key_good", "Unknown commodity id."));
        }

        var path = SuggestedRelativePath();
        var engineMsg = "";
        if (!issues.Any(i => i.IsError))
        {
            try
            {
                var json = RenderJson();
                engineMsg = ContentWorkbench.Validate(path, json);
            }
            catch (Exception ex)
            {
                issues.Add(new ModStudioIssue(true, "document", ex.Message));
            }
        }

        return new ModStudioValidationReport(!issues.Any(i => i.IsError), issues, path, engineMsg);
    }

    public string SaveToMod(string modRoot, bool writeLocalizationStubs = true)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
            throw new InvalidDataException("Mod workspace folder is not configured.");
        var report = Validate();
        if (!report.Valid)
            throw new InvalidDataException(string.Join("\n", report.Issues.Where(i => i.IsError).Select(i => i.Message)));

        ContentWorkbench.EnsureMod(modRoot);
        var json = RenderJson();
        var written = ContentWorkbench.Write(modRoot, report.SuggestedPath, json);

        if (writeLocalizationStubs)
        {
            RenderLocalizationStubs(out var enJson, out var ruJson);
            var id = GetField("id").Trim();
            if (Category != ModStudioCategory.Localization)
                ContentWorkbench.Write(modRoot, $"localization/english/99_studio_{id}.json", enJson);
            ContentWorkbench.Write(modRoot, $"localization/russian/99_studio_{id}.json", ruJson);
        }
        return written;
    }

    /// <summary>Safe pure-C# catalog hot-reload for base + active mod packages without clobbering mid-campaign buildings.</summary>
    public static int HotReloadCatalogs(string modRoot, IEnumerable<string> activePackageRoots, Campaign campaign, out List<string> errors)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddRoot(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var full = Path.GetFullPath(raw.Trim());
            if (Directory.Exists(full) && seen.Add(full)) roots.Add(full);
        }
        if (activePackageRoots is not null)
            foreach (var r in activePackageRoots) AddRoot(r);
        AddRoot(modRoot);

        var loadErrors = PresentationReload.Reload(roots).ToList();
        errors = loadErrors;
        ScriptLibrary.ResetCatalog();
        foreach (var root in roots) ScriptLibrary.LoadDirectories(root, errors);

        EventCatalog.ResetCatalog();
        DecisionCatalog.ResetCatalog();
        ModifierCatalog.ResetCatalog();
        StrategicDoctrineCatalog.ResetCatalog();
        DiseaseCatalog.ResetCatalog(errors);
        DisasterCatalog.ResetCatalog(errors);
        TechnologyCatalog.ResetCatalog(errors);
        IdeologyCatalog.ResetCatalog(errors);
        PoliticalPolicyCatalog.ResetCatalog(errors);
        GrandProjectCatalog.ResetCatalog();
        FormableNations.ResetCatalog();
        if (campaign is null) BuildingCatalog.ResetCatalog();

        var reloaded = 0;
        foreach (var root in roots)
        {
            foreach (var file in DataFiles(root, "events"))
            {
                reloaded++;
                try { EventCatalog.Load(StructuredData.ToJson(file, File.ReadAllText(file)), errors); }
                catch (Exception ex) { errors.Add($"events/{Path.GetFileName(file)}: {ex.Message}"); }
            }
            foreach (var file in DataFiles(root, "common", "modifiers"))
            {
                reloaded++;
                try { ModifierCatalog.Load(StructuredData.ToJson(file, File.ReadAllText(file)), errors); }
                catch (Exception ex) { errors.Add($"common/modifiers/{Path.GetFileName(file)}: {ex.Message}"); }
            }
            foreach (var file in DataFiles(root, "common", "decisions"))
            {
                reloaded++;
                ParseEntries(file, "common/decisions", (id, val) => DecisionCatalog.Apply(id, val, loadErrors), errors);
            }
            foreach (var file in DataFiles(root, "common", "strategic_doctrines"))
            {
                reloaded++;
                ParseRoot(file, "common/strategic_doctrines", r => StrategicDoctrineCatalog.Merge(r, loadErrors), errors);
            }
            foreach (var file in DataFiles(root, "common", "diseases"))
            {
                reloaded++;
                ParseRoot(file, "common/diseases", r => DiseaseCatalog.Merge(r, loadErrors), errors);
            }
            foreach (var file in DataFiles(root, "common", "disasters"))
            {
                reloaded++;
                ParseRoot(file, "common/disasters", r => DisasterCatalog.Merge(r, loadErrors), errors);
            }
            foreach (var file in DataFiles(root, "common", "technologies"))
            {
                reloaded++;
                ParseRoot(file, "common/technologies", r => TechnologyCatalog.Merge(r, loadErrors), errors);
            }
            foreach (var file in DataFiles(root, "common", "units"))
            {
                reloaded++;
                var localErrs = errors;
                ParseEntries(file, "common/units", (id, val) =>
                {
                    if (!UnitTypeCatalog.Override(id, val))
                        localErrs.Add($"common/units/{Path.GetFileName(file)}: invalid unit '{id}'.");
                }, errors);
            }
            foreach (var file in DataFiles(root, "common", "buildings"))
            {
                reloaded++;
                var localErrs = errors;
                ParseEntries(file, "common/buildings", (id, val) =>
                {
                    if (GameDataLoader.IsRemoved(val, $"common/buildings:{id}")) { BuildingCatalog.Remove(id); return; }
                    string Text(string key) => val.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    float? Num(string key) => val.TryGetProperty(key, out var e) && e.TryGetSingle(out var n) ? n : null;
                    int? Int(string key) => val.TryGetProperty(key, out var e) && e.TryGetInt32(out var n) ? n : null;
                    bool? Flag(string key) => val.TryGetProperty(key, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
                    if (!BuildingCatalog.Declare(id, Text("name"), Num("cost"), Int("days"), Num("capacity_gain"), Text("description"), Flag("needs_water"), Num("jobs")))
                        localErrs.Add($"common/buildings/{Path.GetFileName(file)}: invalid building '{id}'.");
                }, errors);
            }
            foreach (var file in DataFiles(root, "common", "ideologies"))
            {
                reloaded++;
                ParseRoot(file, "common/ideologies", r => IdeologyCatalog.Merge(r, loadErrors), errors);
            }
            foreach (var file in DataFiles(root, "common", "laws"))
            {
                reloaded++;
                ParseRoot(file, "common/laws", r => PoliticalPolicyCatalog.MergeLaws(r, loadErrors), errors);
            }
            foreach (var file in DataFiles(root, "common", "agendas"))
            {
                reloaded++;
                ParseRoot(file, "common/agendas", r => PoliticalPolicyCatalog.MergeAgendas(r, loadErrors), errors);
            }
            foreach (var file in DataFiles(root, "common", "grand_projects"))
            {
                reloaded++;
                ParseRoot(file, "common/grand_projects", r => GrandProjectCatalog.Merge(r, loadErrors), errors);
            }
            foreach (var file in DataFiles(root, "common", "formable_nations"))
            {
                reloaded++;
                ParseRoot(file, "common/formable_nations", r => FormableNations.Merge(r, loadErrors), errors);
            }
            if (campaign is not null)
            {
                foreach (var file in DataFiles(root, "common", "countries"))
                {
                    reloaded++;
                    ParseRoot(file, "common/countries", r => CountryDefinitions.Apply(campaign, r), errors);
                }
            }
            if (campaign?.Defines is not null)
            {
                foreach (var file in DataFiles(root, "common", "defines"))
                {
                    reloaded++;
                    ApplyDefinesFile(file, campaign.Defines, errors);
                }
            }
        }

        MapCatalog.SetModRoots(roots);
        MapCatalog.Refresh();
        if (campaign is not null)
            foreach (var nation in campaign.Nations)
                NationalModifiers.Invalidate(nation);
        return reloaded;

        static IEnumerable<string> DataFiles(string root, params string[] parts)
        {
            var dir = Path.Combine(new[] { root }.Concat(parts).ToArray());
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return StructuredData.Files(dir, SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }
        static void ParseRoot(string file, string folder, Action<JsonElement> apply, List<string> errors)
        {
            try
            {
                using var doc = JsonDocument.Parse(StructuredData.ToJson(file, File.ReadAllText(file)), GameDataLoader.ForgivingJson);
                apply(doc.RootElement);
            }
            catch (Exception ex) { errors.Add($"{folder}/{Path.GetFileName(file)}: {ex.Message}"); }
        }
        static void ParseEntries(string file, string folder, Action<string, JsonElement> apply, List<string> errors) =>
            ParseRoot(file, folder, r =>
            {
                if (r.ValueKind != JsonValueKind.Object) return;
                foreach (var entry in r.EnumerateObject())
                    if (!entry.Name.StartsWith("//", StringComparison.Ordinal) && entry.Value.ValueKind == JsonValueKind.Object)
                        apply(entry.Name, entry.Value);
            }, errors);
        static void ApplyDefinesFile(string file, Defines defines, List<string> errors) =>
            ParseRoot(file, "common/defines", r =>
            {
                if (r.ValueKind != JsonValueKind.Object) return;
                static string Norm(string s) => (s ?? "").Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
                var props = typeof(Defines).GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanWrite).ToList();
                foreach (var entry in r.EnumerateObject())
                {
                    if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
                    var prop = props.FirstOrDefault(p => Norm(p.Name) == Norm(entry.Name));
                    if (prop is null) { errors.Add($"common/defines/{Path.GetFileName(file)}: unknown define '{entry.Name}'."); continue; }
                    try
                    {
                        var t = prop.PropertyType;
                        if (t == typeof(bool)) prop.SetValue(defines, entry.Value.GetBoolean());
                        else if (t == typeof(int)) prop.SetValue(defines, entry.Value.GetInt32());
                        else if (t == typeof(float)) prop.SetValue(defines, entry.Value.GetSingle());
                        else if (t == typeof(double)) prop.SetValue(defines, entry.Value.GetDouble());
                        else if (t == typeof(string)) prop.SetValue(defines, entry.Value.GetString());
                    }
                    catch (Exception ex) { errors.Add($"common/defines/{Path.GetFileName(file)}: `{entry.Name}` {ex.Message}"); }
                }
            }, errors);
    }

    /// <summary>Live pure-C# health check for a detached <see cref="ScenarioMapDraft"/>.</summary>
    public static ScenarioDraftAudit AuditDraft(ScenarioMapDraft draft, ProvinceTopology topology)
    {
        if (draft is null)
            return new ScenarioDraftAudit(false, 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(),
                new[] { new ModStudioIssue(true, "draft", "No active scenario draft.") });

        var totalLand = 0;
        var assignedLand = 0;
        var countryProvs = new Dictionary<int, int>();
        var regionProvs = new Dictionary<int, int>();
        for (var p = 1; p <= draft.ProvinceCount; p++)
        {
            if (topology is not null && p < topology.WaterByProvince.Length && topology.WaterByProvince[p]) continue;
            totalLand++;
            var owner = draft.OwnerAt(p);
            var state = draft.StateAt(p);
            if (owner > 0)
            {
                assignedLand++;
                countryProvs[owner] = countryProvs.GetValueOrDefault(owner) + 1;
            }
            if (state > 0)
                regionProvs[state] = regionProvs.GetValueOrDefault(state) + 1;
        }

        var landless = new List<string>();
        foreach (var c in draft.Countries)
            if (countryProvs.GetValueOrDefault(c.MapIndex) == 0)
                landless.Add($"{c.Name} [{c.Tag}]");

        var emptyRegions = new List<string>();
        foreach (var r in draft.Regions)
            if (regionProvs.GetValueOrDefault(r.MapIndex) == 0)
                emptyRegions.Add(r.Name);

        var unassigned = Math.Max(0, totalLand - assignedLand);
        var issues = new List<ModStudioIssue>();
        if (draft.Countries.Length == 0)
            issues.Add(new ModStudioIssue(true, "countries", "Scenario has no countries."));
        if (assignedLand == 0)
            issues.Add(new ModStudioIssue(true, "provinces", "No land provinces are assigned to any country."));
        if (unassigned > 0)
            issues.Add(new ModStudioIssue(false, "provinces", $"{unassigned} land provinces are unassigned."));
        if (landless.Count > 0)
            issues.Add(new ModStudioIssue(false, "countries", $"{landless.Count} countries currently hold 0 provinces."));
        if (emptyRegions.Count > 0)
            issues.Add(new ModStudioIssue(false, "regions", $"{emptyRegions.Count} regions currently hold 0 provinces."));

        return new ScenarioDraftAudit(
            !issues.Any(i => i.IsError),
            totalLand,
            assignedLand,
            unassigned,
            draft.Countries.Length,
            draft.Regions.Length,
            landless,
            emptyRegions,
            issues);
    }

    /// <summary>Deterministic FNV-1a 64-bit hash (never calls string.GetHashCode).</summary>
    public static ulong ComputeDocumentHash(string text)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        if (!string.IsNullOrEmpty(text))
        {
            for (var i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= prime;
            }
        }
        return hash;
    }

    private int ParseInt(string key, int fallback) =>
        int.TryParse(GetField(key).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private float ParseFloat(string key, float fallback) =>
        float.TryParse(GetField(key).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : fallback;

    private bool ParseBool(string key, bool fallback) =>
        bool.TryParse(GetField(key).Trim(), out var v) ? v : fallback;

    private static IReadOnlyDictionary<ModStudioCategory, ModStudioTemplate> BuildTemplates()
    {
        var map = new Dictionary<ModStudioCategory, ModStudioTemplate>
        {
            [ModStudioCategory.Event] = new(
                ModStudioCategory.Event, "events", "studio.cat.event", "studio.cat.event_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "industrial_reform_debate", Required: true, HelpKey: "studio.help.id"),
                    new("title", "studio.field.title", ModStudioFieldKind.Text, "Industrial Reform Debate"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Дебаты о промышленной реформе", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Parliament debates whether to subsidize domestic manufacturing or preserve fiscal reserves."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Парламент обсуждает субсидирование отечественной промышленности и сохранение резервов.", Required: false),
                    new("max_stability", "studio.field.max_stability", ModStudioFieldKind.Float, "0.85", 0.05f, 1.0f),
                    new("weight", "studio.field.weight", ModStudioFieldKind.Float, "1.2", 0.05f, 20f),
                    new("opt1_label", "studio.field.opt1_label", ModStudioFieldKind.Text, "Back the industrial compact"),
                    new("opt1_consequence", "studio.field.opt1_consequence", ModStudioFieldKind.Text, "Funds subsidies via annual revenue share and lifts stability & prestige."),
                    new("opt1_income_share", "studio.field.opt1_income_share", ModStudioFieldKind.Float, "-0.04", -0.5f, 0.5f, Required: false, HelpKey: "studio.help.income_share"),
                    new("opt1_stability", "studio.field.opt1_stability", ModStudioFieldKind.Float, "0.025", -0.25f, 0.25f),
                    new("opt1_pp", "studio.field.opt1_pp", ModStudioFieldKind.Float, "-10", -100f, 100f),
                    new("opt1_prestige", "studio.field.opt1_prestige", ModStudioFieldKind.Float, "6", -200f, 200f, Required: false, HelpKey: "studio.help.prestige"),
                    new("opt2_label", "studio.field.opt2_label", ModStudioFieldKind.Text, "Protect emergency liquidity"),
                    new("opt2_consequence", "studio.field.opt2_consequence", ModStudioFieldKind.Text, "Cuts outlays relative to GDP with a slight prestige & stability penalty."),
                    new("opt2_gdp_share", "studio.field.opt2_gdp_share", ModStudioFieldKind.Float, "-0.002", -0.25f, 0.25f, Required: false, HelpKey: "studio.help.gdp_share"),
                    new("opt2_stability", "studio.field.opt2_stability", ModStudioFieldKind.Float, "-0.01", -0.25f, 0.25f),
                    new("opt2_prestige", "studio.field.opt2_prestige", ModStudioFieldKind.Float, "-4", -200f, 200f, Required: false, HelpKey: "studio.help.prestige")
                }, ModStudioSection.EventsAndDecisions),
            [ModStudioCategory.Decision] = new(
                ModStudioCategory.Decision, "common/decisions", "studio.cat.decision", "studio.cat.decision_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "national_grid_modernization", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "National Grid Modernization"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Модернизация энергосети", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Invest public funds into nationwide high-voltage transmission and reliability."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Инвестировать средства бюджета в магистральные линии электропередач.", Required: false),
                    new("pp_cost", "studio.field.pp_cost", ModStudioFieldKind.Number, "20", 0f, 200f),
                    new("cooldown_days", "studio.field.cooldown_days", ModStudioFieldKind.Number, "365", 7f, 3650f),
                    new("gdp_cost_share", "studio.field.gdp_cost_share", ModStudioFieldKind.Float, "0.002", 0f, 0.15f, Required: false),
                    new("min_stability", "studio.field.min_stability", ModStudioFieldKind.Float, "0.35", 0f, 1.0f),
                    new("min_prestige", "studio.field.min_prestige", ModStudioFieldKind.Float, "0", 0f, 1000f, Required: false),
                    new("effect_income_share", "studio.field.effect_income_share", ModStudioFieldKind.Float, "-0.02", -0.5f, 0.5f, Required: false, HelpKey: "studio.help.income_share"),
                    new("effect_prestige", "studio.field.effect_prestige", ModStudioFieldKind.Float, "8", -200f, 200f, Required: false, HelpKey: "studio.help.prestige"),
                    new("effect_stability", "studio.field.effect_stability", ModStudioFieldKind.Float, "0.03", -0.25f, 0.25f),
                    new("effect_war_support", "studio.field.effect_war_support", ModStudioFieldKind.Float, "0.02", -0.25f, 0.25f)
                }, ModStudioSection.EventsAndDecisions),
            [ModStudioCategory.Disease] = new(
                ModStudioCategory.Disease, "common/diseases", "studio.cat.disease", "studio.cat.disease_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "alpine_respiratory_strain", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Alpine Respiratory Strain"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Альпийский респираторный штамм", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Seasonal outbreak stressing urban clinics across northern basins."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Сезонная вспышка, создающая нагрузку на городские больницы.", Required: false),
                    new("category", "studio.field.category", ModStudioFieldKind.Choice, "respiratory", Choices: new[] { "respiratory", "vector_borne", "waterborne", "zoonotic" }),
                    new("min_abs_lat", "studio.field.min_lat", ModStudioFieldKind.Float, "15", 0f, 90f),
                    new("max_abs_lat", "studio.field.max_lat", ModStudioFieldKind.Float, "85", 0f, 90f),
                    new("annual_chance", "studio.field.annual_chance", ModStudioFieldKind.Float, "0.04", 0.001f, 0.5f),
                    new("starting_severity", "studio.field.severity", ModStudioFieldKind.Float, "32", 5f, 95f),
                    new("weekly_severity_drift", "studio.field.severity_drift", ModStudioFieldKind.Float, "3.5", -10f, 20f),
                    new("base_duration_days", "studio.field.duration_days", ModStudioFieldKind.Number, "90", 14f, 730f),
                    new("cooldown_days", "studio.field.cooldown_days", ModStudioFieldKind.Number, "365", 30f, 2000f),
                    new("spread_rate", "studio.field.spread_rate", ModStudioFieldKind.Float, "0.30", 0f, 1.0f),
                    new("mortality_annual_at_max", "studio.field.mortality", ModStudioFieldKind.Float, "0.004", 0f, 0.05f),
                    new("economy_cost_gdp_share", "studio.field.gdp_cost_share", ModStudioFieldKind.Float, "0.006", 0f, 0.1f)
                }, ModStudioSection.EventsAndDecisions),
            [ModStudioCategory.Disaster] = new(
                ModStudioCategory.Disaster, "common/disasters", "studio.cat.disaster", "studio.cat.disaster_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "monsoon_basin_flood", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Monsoon Basin Flood"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Муссонное наводнение", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Extreme seasonal rainfall inundates river corridors and rail depots."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Экстремальные ливни затапливают речные долины и узлы снабжения.", Required: false),
                    new("category", "studio.field.category", ModStudioFieldKind.Choice, "hydrological", Choices: new[] { "geological", "meteorological", "hydrological", "climatological" }),
                    new("min_abs_lat", "studio.field.min_lat", ModStudioFieldKind.Float, "0", 0f, 90f),
                    new("max_abs_lat", "studio.field.max_lat", ModStudioFieldKind.Float, "55", 0f, 90f),
                    new("requires_coastal", "studio.field.requires_coastal", ModStudioFieldKind.Bool, "false"),
                    new("annual_chance", "studio.field.annual_chance", ModStudioFieldKind.Float, "0.035", 0.001f, 0.5f),
                    new("starting_severity", "studio.field.severity", ModStudioFieldKind.Float, "54", 10f, 95f),
                    new("weekly_recovery_rate", "studio.field.recovery_rate", ModStudioFieldKind.Float, "5.5", 0.5f, 25f),
                    new("base_duration_days", "studio.field.duration_days", ModStudioFieldKind.Number, "56", 7f, 365f),
                    new("cooldown_days", "studio.field.cooldown_days", ModStudioFieldKind.Number, "300", 30f, 2000f),
                    new("instant_damage", "studio.field.instant_damage", ModStudioFieldKind.Float, "0.14", 0f, 0.6f),
                    new("economy_cost_gdp_share", "studio.field.gdp_cost_share", ModStudioFieldKind.Float, "0.008", 0f, 0.1f)
                }, ModStudioSection.EventsAndDecisions),
            [ModStudioCategory.Technology] = new(
                ModStudioCategory.Technology, "common/technologies", "studio.cat.tech", "studio.cat.tech_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "modular_grid_storage", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Modular Grid Storage"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Модульные накопители энергии", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Utility-scale battery buffers steady industrial output during peak demand."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Промышленные накопители сглаживают пиковые нагрузки на энергосистему.", Required: false),
                    new("category", "studio.field.category", ModStudioFieldKind.Choice, "industry", Choices: ValidTechCategories),
                    new("cost", "studio.field.tech_cost", ModStudioFieldKind.Number, "160", 20f, 5000f),
                    new("requires", "studio.field.requires", ModStudioFieldKind.Text, "", Required: false, HelpKey: "studio.help.comma_list"),
                    new("modifier_key", "studio.field.modifier_key", ModStudioFieldKind.Choice, "output", Choices: ValidModifierKeys),
                    new("modifier_value", "studio.field.modifier_value", ModStudioFieldKind.Float, "0.04", -0.5f, 1.0f)
                }, ModStudioSection.EconomyAndTech),
            [ModStudioCategory.Building] = new(
                ModStudioCategory.Building, "common/buildings", "studio.cat.building", "studio.cat.building_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "synthetic_fertiliser_plant", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Synthetic Fertiliser Plant"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Завод синтетических удобрений", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Produces agricultural fertiliser and industrial chemicals to stabilise crop yields."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Производит минеральные удобрения и реагенты для стабильного урожая.", Required: false),
                    new("cost", "studio.field.building_cost", ModStudioFieldKind.Float, "2.20", 0.05f, 100f),
                    new("days", "studio.field.duration_days", ModStudioFieldKind.Number, "240", 7f, 1825f),
                    new("capacity_gain", "studio.field.capacity_gain", ModStudioFieldKind.Float, "0.12", 0f, 5f),
                    new("jobs", "studio.field.jobs", ModStudioFieldKind.Float, "0.45", 0f, 10f),
                    new("needs_water", "studio.field.needs_water", ModStudioFieldKind.Bool, "false")
                }, ModStudioSection.EconomyAndTech),
            [ModStudioCategory.Modifier] = new(
                ModStudioCategory.Modifier, "common/modifiers", "studio.cat.modifier", "studio.cat.modifier_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "strategic_industrial_surge", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Strategic Industrial Surge"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Стратегический промышленный рывок", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Coordinated state procurement lifts factory productivity."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Координированные госзаказы повышают выпуск заводов.", Required: false),
                    new("days", "studio.field.duration_days", ModStudioFieldKind.Number, "365", 7f, 3650f),
                    new("positive", "studio.field.positive", ModStudioFieldKind.Bool, "true"),
                    new("modifier_key", "studio.field.modifier_key", ModStudioFieldKind.Choice, "output", Choices: ValidModifierKeys),
                    new("modifier_value", "studio.field.modifier_value", ModStudioFieldKind.Float, "0.08", -1.0f, 2.0f)
                }, ModStudioSection.EconomyAndTech),
            [ModStudioCategory.UnitType] = new(
                ModStudioCategory.UnitType, "common/units", "studio.cat.unit", "studio.cat.unit_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "mountain_brigade", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Mountain Brigade"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Горнострелковая бригада", Required: false),
                    new("counter_symbol", "studio.field.counter_symbol", ModStudioFieldKind.Choice, "infantry", Choices: ValidUnitSymbols),
                    new("attack", "studio.field.attack", ModStudioFieldKind.Float, "1.05", 0.1f, 20f),
                    new("soft_attack", "studio.field.soft_attack", ModStudioFieldKind.Float, "1.10", 0f, 20f),
                    new("hard_attack", "studio.field.hard_attack", ModStudioFieldKind.Float, "0.35", 0f, 20f),
                    new("defense", "studio.field.defense", ModStudioFieldKind.Float, "1.25", 0.1f, 20f),
                    new("breakthrough", "studio.field.breakthrough", ModStudioFieldKind.Float, "0.75", 0.05f, 20f),
                    new("organisation", "studio.field.organisation", ModStudioFieldKind.Float, "1.02", 0.1f, 10f),
                    new("speed", "studio.field.speed", ModStudioFieldKind.Float, "0.95", 0.1f, 10f),
                    new("combat_width", "studio.field.combat_width", ModStudioFieldKind.Float, "18", 1f, 100f),
                    new("supply_use", "studio.field.supply_use", ModStudioFieldKind.Float, "0.90", 0.1f, 20f),
                    new("recruitment_cost", "studio.field.recruitment_cost", ModStudioFieldKind.Float, "1.15", 0.05f, 50f),
                    new("training_days", "studio.field.training_days", ModStudioFieldKind.Number, "55", 7f, 730f)
                }, ModStudioSection.MilitaryAndProjects),
            [ModStudioCategory.StrategicDoctrine] = new(
                ModStudioCategory.StrategicDoctrine, "common/strategic_doctrines", "studio.cat.doctrine", "studio.cat.doctrine_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "adaptive_theater_command", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Adaptive Theater Command"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Адаптивное командование ТВД", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Delegates tactical initiative to joint regional staffs."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Делегирует тактическую инициативу объединённым штабам.", Required: false),
                    new("political_cost", "studio.field.pp_cost", ModStudioFieldKind.Float, "40", 0f, 300f),
                    new("training_slots", "studio.field.training_slots", ModStudioFieldKind.Float, "1.0", 0.25f, 4.0f),
                    new("drill_experience", "studio.field.drill_xp", ModStudioFieldKind.Float, "1.15", 0.25f, 4.0f),
                    new("battle_experience", "studio.field.battle_xp", ModStudioFieldKind.Float, "1.10", 0.25f, 4.0f),
                    new("air_experience", "studio.field.air_xp", ModStudioFieldKind.Float, "1.0", 0.25f, 4.0f),
                    new("naval_experience", "studio.field.naval_xp", ModStudioFieldKind.Float, "1.0", 0.25f, 4.0f),
                    new("tactical_bias", "studio.field.tactical_bias", ModStudioFieldKind.Text, "mission_command", Required: false),
                    new("modifier_key", "studio.field.modifier_key", ModStudioFieldKind.Choice, "army_attack", Choices: ValidModifierKeys),
                    new("modifier_value", "studio.field.modifier_value", ModStudioFieldKind.Float, "0.08", -0.5f, 0.5f)
                }, ModStudioSection.MilitaryAndProjects),
            [ModStudioCategory.GrandProject] = new(
                ModStudioCategory.GrandProject, "common/grand_projects", "studio.cat.grand_project", "studio.cat.grand_project_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "transregional_energy_corridor", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Transregional Energy Corridor"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Межрегиональный энергокоридор", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Constructs HVDC interconnectors and pumped-storage reservoirs linking industrial provinces."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Строит магистральные ЛЭП и ГАЭС, связывающие промышленные провинции.", Required: false),
                    new("category", "studio.field.category", ModStudioFieldKind.Choice, "energy", Choices: ValidGrandProjectCategories),
                    new("stage_1_days", "studio.field.stage1_days", ModStudioFieldKind.Number, "90", 7f, 1825f),
                    new("stage_2_days", "studio.field.stage2_days", ModStudioFieldKind.Number, "150", 7f, 1825f),
                    new("stage_3_days", "studio.field.stage3_days", ModStudioFieldKind.Number, "90", 7f, 1825f),
                    new("base_cost_billions", "studio.field.base_cost_billions", ModStudioFieldKind.Float, "1.5", 0.05f, 500f),
                    new("gdp_cost_share", "studio.field.gdp_cost_share", ModStudioFieldKind.Float, "0.0025", 0f, 0.25f),
                    new("political_cost", "studio.field.pp_cost", ModStudioFieldKind.Float, "15", 0f, 500f),
                    new("key_good", "studio.field.key_good", ModStudioFieldKind.Choice, "IndustrialMaterials", Choices: ValidGrandProjectGoods),
                    new("modifier_key", "studio.field.modifier_key", ModStudioFieldKind.Choice, "output", Choices: ValidModifierKeys),
                    new("modifier_value", "studio.field.modifier_value", ModStudioFieldKind.Float, "0.06", -0.5f, 0.5f)
                }, ModStudioSection.MilitaryAndProjects),
            [ModStudioCategory.Ideology] = new(
                ModStudioCategory.Ideology, "common/ideologies", "studio.cat.ideology", "studio.cat.ideology_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "technocratic_pragmatism", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Technocratic Pragmatism"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Технократический прагматизм", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Emphasises engineering capacity, administrative metrics, and long-horizon public investment."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Делает ставку на инженерный потенциал, измеримую эффективность и долгосрочные инвестиции.", Required: false),
                    new("colour", "studio.field.colour", ModStudioFieldKind.Text, "#4B9CD3"),
                    new("order", "studio.field.order", ModStudioFieldKind.Number, "95", 1f, 1000f),
                    new("economic", "studio.field.economic", ModStudioFieldKind.Float, "0.15", -1f, 1f),
                    new("authority", "studio.field.authority", ModStudioFieldKind.Float, "0.20", -1f, 1f),
                    new("militia", "studio.field.militia", ModStudioFieldKind.Float, "0.95", 0.1f, 5f),
                    new("modifier_key", "studio.field.modifier_key", ModStudioFieldKind.Choice, "research_speed", Choices: ValidModifierKeys),
                    new("modifier_value", "studio.field.modifier_value", ModStudioFieldKind.Float, "0.06", -0.5f, 0.5f)
                }, ModStudioSection.PoliticsAndBalance),
            [ModStudioCategory.PolicyLaw] = new(
                ModStudioCategory.PolicyLaw, "common/laws", "studio.cat.policy", "studio.cat.policy_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "fiscal_innovation_compact", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Innovation Compact"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Инновационный пакт", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Directs state procurement toward frontier manufacturing and applied labs."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Направляет госзаказ в передовые производства и прикладные лаборатории.", Required: false),
                    new("category", "studio.field.category", ModStudioFieldKind.Choice, "Industrial policy", Choices: ValidPolicyCategories),
                    new("scope", "studio.field.scope", ModStudioFieldKind.Choice, "nation", Choices: ValidPolicyScopes),
                    new("cost_share", "studio.field.cost_share", ModStudioFieldKind.Float, "0.002", 0f, 0.01f),
                    new("cooldown_days", "studio.field.cooldown_days", ModStudioFieldKind.Number, "180", 7f, 3650f),
                    new("duration_days", "studio.field.duration_days", ModStudioFieldKind.Number, "730", 28f, 3650f),
                    new("foreign_trust", "studio.field.foreign_trust", ModStudioFieldKind.Float, "0", -0.3f, 0.3f, Required: false),
                    new("requires_war", "studio.field.requires_war", ModStudioFieldKind.Bool, "false"),
                    new("modifier_key", "studio.field.modifier_key", ModStudioFieldKind.Choice, "output", Choices: ValidModifierKeys),
                    new("modifier_value", "studio.field.modifier_value", ModStudioFieldKind.Float, "0.05", -0.5f, 0.5f)
                }, ModStudioSection.PoliticsAndBalance),
            [ModStudioCategory.DefinesOverride] = new(
                ModStudioCategory.DefinesOverride, "common/defines", "studio.cat.defines", "studio.cat.defines_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "custom_balance_rules", HelpKey: "studio.help.id"),
                    new("title", "studio.field.title", ModStudioFieldKind.Text, "Custom Balance Rules"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Пользовательские правила баланса", Required: false),
                    new("max_monthly_growth", "studio.field.max_monthly_growth", ModStudioFieldKind.Float, "0.006", 0.0005f, 0.05f),
                    new("gdp_convergence_rate", "studio.field.gdp_convergence_rate", ModStudioFieldKind.Float, "0.10", 0f, 0.5f),
                    new("shortage_annual_growth_penalty", "studio.field.shortage_growth_penalty", ModStudioFieldKind.Float, "0.08", 0f, 0.5f),
                    new("default_tax_rate", "studio.field.default_tax_rate", ModStudioFieldKind.Float, "0.21", 0.02f, 0.75f),
                    new("ai_maximum_tax_rate", "studio.field.ai_max_tax_rate", ModStudioFieldKind.Float, "0.46", 0.10f, 0.85f),
                    new("notoriety_per_unjustified_war", "studio.field.notoriety_unjustified_war", ModStudioFieldKind.Float, "18", 0f, 100f),
                    new("ai_notoriety_caution", "studio.field.ai_notoriety_caution", ModStudioFieldKind.Float, "1.30", 0.1f, 5f),
                    new("peace_weariness_after_days", "studio.field.peace_weariness_days", ModStudioFieldKind.Number, "450", 30f, 3650f),
                    new("peace_weariness_per_year", "studio.field.peace_weariness_per_year", ModStudioFieldKind.Float, "15", 0f, 100f),
                    new("ai_war_weariness_caution", "studio.field.ai_war_weariness_caution", ModStudioFieldKind.Float, "1.60", 0.1f, 5f),
                    new("ai_aggression", "studio.field.ai_aggression", ModStudioFieldKind.Float, "1.0", 0.1f, 4f),
                    new("ai_war_chance", "studio.field.ai_war_chance", ModStudioFieldKind.Float, "0.08", 0f, 1f),
                    new("mainland_reclamation_enabled", "studio.field.mainland_reclamation_enabled", ModStudioFieldKind.Bool, "true"),
                    new("ai_mainland_return_chance", "studio.field.ai_mainland_return_chance", ModStudioFieldKind.Float, "0.12", 0f, 1f),
                    new("prestige_enabled", "studio.field.prestige_enabled", ModStudioFieldKind.Bool, "true"),
                    new("prestige_weekly_drift_rate", "studio.field.prestige_weekly_drift_rate", ModStudioFieldKind.Float, "0.015", 0f, 0.25f)
                }, ModStudioSection.PoliticsAndBalance),
            [ModStudioCategory.Localization] = new(
                ModStudioCategory.Localization, "localization/english", "studio.cat.localization", "studio.cat.localization_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "custom_strings", HelpKey: "studio.help.id"),
                    new("loc_key", "studio.field.loc_key", ModStudioFieldKind.Text, "my_mod.greeting"),
                    new("en_value", "studio.field.en_value", ModStudioFieldKind.Text, "Welcome to my custom scenario!"),
                    new("ru_value", "studio.field.ru_value", ModStudioFieldKind.Text, "Добро пожаловать в мой сценарий!", Required: false)
                }, ModStudioSection.PoliticsAndBalance),
            [ModStudioCategory.FormableNation] = new(
                ModStudioCategory.FormableNation, "common/formable_nations", "studio.cat.formable", "studio.cat.formable_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "north_sea_commonwealth", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "North Sea Commonwealth"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Содружество Северного моря", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Unite the maritime littoral states of Northern Europe under one federation."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Объедините морские государства Северной Европы в единую федерацию.", Required: false),
                    new("tag", "studio.field.tag", ModStudioFieldKind.Text, "NOR, SWE", HelpKey: "studio.help.tag_list"),
                    new("required_regions", "studio.field.required_regions", ModStudioFieldKind.Text, "no_osl, se_sto", HelpKey: "studio.help.regions_list"),
                    new("allowed_cultures", "studio.field.allowed_cultures", ModStudioFieldKind.Text, "norwegian, swedish", Required: false, HelpKey: "studio.help.cultures_list"),
                    new("prestige_bonus", "studio.field.prestige_bonus", ModStudioFieldKind.Float, "25", 0f, 500f),
                    new("stability_bonus", "studio.field.stability_bonus", ModStudioFieldKind.Float, "0.04", 0f, 1f),
                    new("unlocks_event", "studio.field.unlocks_event", ModStudioFieldKind.Text, "", Required: false)
                }, ModStudioSection.FormableAndWorld),
            [ModStudioCategory.Country] = new(
                ModStudioCategory.Country, "common/countries", "studio.cat.country", "studio.cat.country_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "custom_portugal", HelpKey: "studio.help.id"),
                    new("tag", "studio.field.tag", ModStudioFieldKind.Text, "PRT", HelpKey: "studio.help.tag_list"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Portugal"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Португалия", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Atlantic maritime republic with modernized industrial corridors."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Атлантическая морская республика с обновлённой промышленностью.", Required: false),
                    new("colour", "studio.field.colour", ModStudioFieldKind.Text, "#2E7D4F"),
                    new("government_form", "studio.field.government_form", ModStudioFieldKind.Choice, "parliamentary_republic",
                        Choices: GovernmentCatalog.All.Select(g => g.Id).ToArray()),
                    new("course", "studio.field.course", ModStudioFieldKind.Choice, "institutional_continuity",
                        Choices: NationalCourseCatalog.All.Select(c => c.Id).ToArray()),
                    new("capital_region", "studio.field.capital_region", ModStudioFieldKind.Text, "pt_lis", Required: false),
                    new("starting_treasury", "studio.field.starting_treasury", ModStudioFieldKind.Float, "25", -1000f, 100000f)
                }, ModStudioSection.FormableAndWorld),
            [ModStudioCategory.MapPack] = new(
                ModStudioCategory.MapPack, "maps", "studio.cat.map_pack", "studio.cat.map_pack_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "archipelago_theater", HelpKey: "studio.help.id"),
                    new("name", "studio.field.title", ModStudioFieldKind.Text, "Archipelago Theater"),
                    new("title_ru", "studio.field.title_ru", ModStudioFieldKind.Text, "Театр архипелага", Required: false),
                    new("description", "studio.field.description", ModStudioFieldKind.Text, "Compact maritime theater tuned for naval logistics and island landings."),
                    new("description_ru", "studio.field.description_ru", ModStudioFieldKind.Text, "Компактный морской театр для флотской логистики и десантных операций.", Required: false),
                    new("cells_per_province", "studio.field.cells_per_province", ModStudioFieldKind.Number, "36", 12f, 10000f),
                    new("disputed_cells_per_province", "studio.field.disputed_cells_per_province", ModStudioFieldKind.Number, "30", 12f, 10000f),
                    new("siberia_multiplier", "studio.field.siberia_multiplier", ModStudioFieldKind.Float, "1.25", 0.25f, 16f),
                    new("polar_multiplier", "studio.field.polar_multiplier", ModStudioFieldKind.Float, "2.0", 0.25f, 16f),
                    new("inherits_base_hydrology", "studio.field.inherits_base_hydrology", ModStudioFieldKind.Bool, "true"),
                    new("author", "studio.field.author", ModStudioFieldKind.Text, "Deal or Force Mod Studio", Required: false)
                }, ModStudioSection.FormableAndWorld)
        };
        return map;
    }

    private static IReadOnlyDictionary<ModStudioCategory, IReadOnlyList<ModStudioPreset>> BuildPresets()
    {
        return new Dictionary<ModStudioCategory, IReadOnlyList<ModStudioPreset>>
        {
            [ModStudioCategory.Event] = new ModStudioPreset[]
            {
                new("ev_reform", "studio.preset.ev_reform", new Dictionary<string, string>
                {
                    ["opt1_income_share"] = "-0.04", ["opt1_stability"] = "0.025", ["opt1_pp"] = "-10", ["opt1_prestige"] = "6",
                    ["opt2_gdp_share"] = "-0.002", ["opt2_stability"] = "-0.01", ["opt2_prestige"] = "-4"
                }),
                new("ev_crisis", "studio.preset.ev_crisis", new Dictionary<string, string>
                {
                    ["opt1_income_share"] = "-0.08", ["opt1_stability"] = "0.04", ["opt1_pp"] = "-25", ["opt1_prestige"] = "12",
                    ["opt2_gdp_share"] = "-0.005", ["opt2_stability"] = "-0.03", ["opt2_prestige"] = "-10"
                })
            },
            [ModStudioCategory.Decision] = new ModStudioPreset[]
            {
                new("dec_stimulus", "studio.preset.dec_stimulus", new Dictionary<string, string>
                {
                    ["pp_cost"] = "20", ["cooldown_days"] = "365", ["gdp_cost_share"] = "0.004", ["min_prestige"] = "0",
                    ["effect_income_share"] = "0", ["effect_prestige"] = "6", ["effect_stability"] = "0.03", ["effect_war_support"] = "0.01"
                }),
                new("dec_austerity", "studio.preset.dec_austerity", new Dictionary<string, string>
                {
                    ["pp_cost"] = "15", ["cooldown_days"] = "180", ["gdp_cost_share"] = "0", ["min_prestige"] = "150",
                    ["effect_income_share"] = "0.05", ["effect_prestige"] = "-6", ["effect_stability"] = "-0.02", ["effect_war_support"] = "0"
                })
            },
            [ModStudioCategory.Building] = new ModStudioPreset[]
            {
                new("bld_factory", "studio.preset.bld_factory", new Dictionary<string, string>
                {
                    ["cost"] = "2.20", ["days"] = "240", ["capacity_gain"] = "0.12", ["jobs"] = "0.50", ["needs_water"] = "false"
                }),
                new("bld_port", "studio.preset.bld_port", new Dictionary<string, string>
                {
                    ["cost"] = "2.60", ["days"] = "260", ["capacity_gain"] = "0.20", ["jobs"] = "0.28", ["needs_water"] = "true"
                })
            },
            [ModStudioCategory.UnitType] = new ModStudioPreset[]
            {
                new("unit_mountain", "studio.preset.unit_mountain", new Dictionary<string, string>
                {
                    ["counter_symbol"] = "infantry", ["attack"] = "1.05", ["defense"] = "1.25", ["speed"] = "0.95", ["supply_use"] = "0.90"
                }),
                new("unit_armor", "studio.preset.unit_armor", new Dictionary<string, string>
                {
                    ["counter_symbol"] = "armor", ["attack"] = "1.65", ["defense"] = "1.10", ["speed"] = "1.22", ["supply_use"] = "1.65"
                })
            },
            [ModStudioCategory.DefinesOverride] = new ModStudioPreset[]
            {
                new("def_vanilla", "studio.preset.def_vanilla", new Dictionary<string, string>
                {
                    ["max_monthly_growth"] = "0.006", ["default_tax_rate"] = "0.21", ["ai_aggression"] = "1.0", ["ai_war_chance"] = "0.08",
                    ["peace_weariness_after_days"] = "450", ["mainland_reclamation_enabled"] = "true", ["prestige_enabled"] = "true", ["prestige_weekly_drift_rate"] = "0.015"
                }),
                new("def_fast", "studio.preset.def_fast", new Dictionary<string, string>
                {
                    ["max_monthly_growth"] = "0.012", ["default_tax_rate"] = "0.24", ["ai_aggression"] = "1.6", ["ai_war_chance"] = "0.16",
                    ["peace_weariness_after_days"] = "300", ["mainland_reclamation_enabled"] = "true", ["prestige_enabled"] = "true", ["prestige_weekly_drift_rate"] = "0.030"
                }),
                new("def_hardcore", "studio.preset.def_hardcore", new Dictionary<string, string>
                {
                    ["max_monthly_growth"] = "0.004", ["default_tax_rate"] = "0.18", ["ai_aggression"] = "1.3", ["ai_war_chance"] = "0.11",
                    ["peace_weariness_after_days"] = "720", ["mainland_reclamation_enabled"] = "true", ["prestige_enabled"] = "true", ["prestige_weekly_drift_rate"] = "0.010"
                })
            },
            [ModStudioCategory.FormableNation] = new ModStudioPreset[]
            {
                new("form_union", "studio.preset.form_union", new Dictionary<string, string>
                {
                    ["tag"] = "BEL, NLD, LUX", ["required_regions"] = "be_bru, nl_nh", ["allowed_cultures"] = "dutch, walloon",
                    ["prestige_bonus"] = "30", ["stability_bonus"] = "0.04"
                }),
                new("form_empire", "studio.preset.form_empire", new Dictionary<string, string>
                {
                    ["tag"] = "NOR, SWE, DNK", ["required_regions"] = "no_osl, se_sto, dk_cph", ["allowed_cultures"] = "norwegian, swedish, danish",
                    ["prestige_bonus"] = "45", ["stability_bonus"] = "0.05"
                })
            },
            [ModStudioCategory.Country] = new ModStudioPreset[]
            {
                new("ctry_republic", "studio.preset.ctry_republic", new Dictionary<string, string>
                {
                    ["tag"] = "PRT", ["colour"] = "#2E7D4F", ["government_form"] = "parliamentary_republic",
                    ["course"] = "commercial_openness", ["capital_region"] = "pt_lis", ["starting_treasury"] = "35"
                }),
                new("ctry_autocracy", "studio.preset.ctry_autocracy", new Dictionary<string, string>
                {
                    ["tag"] = "PRT", ["colour"] = "#8C3B2B", ["government_form"] = "presidential_republic",
                    ["course"] = "strategic_autonomy", ["capital_region"] = "pt_lis", ["starting_treasury"] = "45"
                })
            },
            [ModStudioCategory.MapPack] = new ModStudioPreset[]
            {
                new("map_dense", "studio.preset.map_dense", new Dictionary<string, string>
                {
                    ["cells_per_province"] = "22", ["disputed_cells_per_province"] = "18",
                    ["siberia_multiplier"] = "1.1", ["polar_multiplier"] = "1.6", ["inherits_base_hydrology"] = "true"
                }),
                new("map_archipelago", "studio.preset.map_archipelago", new Dictionary<string, string>
                {
                    ["cells_per_province"] = "42", ["disputed_cells_per_province"] = "36",
                    ["siberia_multiplier"] = "1.4", ["polar_multiplier"] = "2.5", ["inherits_base_hydrology"] = "true"
                })
            }
        };
    }
}

public enum ModScaffoldKind
{
    Data = 0,
    MapPack = 1,
    Scenario = 2,
    Formable = 3,
    GdScript = 4,
    CSharp = 5,
    RustWorker = 6,
    FSharp = 7
}

public sealed record ModScaffoldRequest(
    string ParentModsDir,
    string ModId,
    string DisplayName,
    string Author,
    ModScaffoldKind Kind);

public sealed record ModScaffoldResult(
    string ModRoot,
    string ManifestPath,
    IReadOnlyList<string> CreatedFiles);

/// <summary>
/// Pure-C# project scaffolder producing complete, validated Deal or Force mod packages on disk.
/// </summary>
public static class ModStudioScaffolder
{
    private static readonly JsonWriterOptions IndentedJson = new() { Indented = true };

    public static bool TryParseKind(string raw, out ModScaffoldKind kind)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "data": kind = ModScaffoldKind.Data; return true;
            case "map" or "map_pack" or "mappack": kind = ModScaffoldKind.MapPack; return true;
            case "scenario": kind = ModScaffoldKind.Scenario; return true;
            case "formable" or "formable_nation": kind = ModScaffoldKind.Formable; return true;
            case "gdscript" or "gd": kind = ModScaffoldKind.GdScript; return true;
            case "csharp" or "cs": kind = ModScaffoldKind.CSharp; return true;
            case "fsharp" or "fs": kind = ModScaffoldKind.FSharp; return true;
            case "rust" or "rust_worker": kind = ModScaffoldKind.RustWorker; return true;
            default: kind = ModScaffoldKind.Data; return false;
        }
    }

    public static ModScaffoldResult Scaffold(ModScaffoldRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ParentModsDir))
            throw new InvalidDataException("Mods root folder is not configured.");

        var id = ModStudioDocument.NormalizeId(request.ModId);
        if (request.Kind == ModScaffoldKind.MapPack && id.Length > 48)
            id = id[..48].TrimEnd('_');

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? ToTitle(id) : request.DisplayName.Trim();
        var author = string.IsNullOrWhiteSpace(request.Author) ? "Deal or Force Modder" : request.Author.Trim();

        var parentFull = Path.GetFullPath(request.ParentModsDir.Trim());
        ContentWorkbench.RejectLinks(parentFull);
        Directory.CreateDirectory(parentFull);

        var modRoot = ScenarioLibrary.SafePath(parentFull, id);
        ContentWorkbench.RejectLinks(modRoot);
        if (Directory.Exists(modRoot) && Directory.EnumerateFileSystemEntries(modRoot).Any())
            throw new InvalidDataException($"Mod folder '{id}' already exists. Choose a unique mod id.");
        Directory.CreateDirectory(modRoot);

        var created = new List<string>();
        string WriteRel(string relPath, string content)
        {
            var full = ScenarioLibrary.SafePath(modRoot, relPath);
            ContentWorkbench.RejectLinks(full);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n') + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            created.Add(full);
            return full;
        }

        var manifest = new JsonObject
        {
            ["id"] = id,
            ["name"] = displayName,
            ["version"] = "1.0.0",
            ["supported_game_version"] = "0.8.*",
            ["author"] = author,
            ["enabled"] = true,
            ["load_order"] = 150,
            ["dependencies"] = new JsonArray(),
            ["gdscript_entry"] = request.Kind == ModScaffoldKind.GdScript ? "scripts/mod_main.gd" : "",
            ["csharp_assembly"] = ""
        };
        if (request.Kind == ModScaffoldKind.RustWorker)
        {
            manifest["worker_interval_days"] = 7;
            manifest["worker_timeout_ms"] = 500;
        }
        var manifestPath = WriteRel("manifest.json", FormatJson(manifest));

        var enLoc = new JsonObject
        {
            [$"mod.{id}.title"] = displayName,
            [$"mod.{id}.desc"] = $"Custom {request.Kind} mod created by {author}."
        };
        var ruLoc = new JsonObject
        {
            [$"mod.{id}.title"] = displayName,
            [$"mod.{id}.desc"] = $"Пользовательский мод ({request.Kind}) от {author}."
        };

        switch (request.Kind)
        {
            case ModScaffoldKind.Data:
            {
                var decDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Decision, $"{id}_initiative", $"{displayName} Initiative");
                decDoc.SetField("title_ru", $"Инициатива: {displayName}");
                WriteRel($"common/decisions/99_studio_{id}_initiative.json", decDoc.RenderJson());
                decDoc.RenderLocalizationStubs(out var dEn, out var dRu);
                WriteRel($"localization/english/99_studio_{id}_initiative.json", dEn);
                WriteRel($"localization/russian/99_studio_{id}_initiative.json", dRu);

                var evDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Event, $"{id}_debate", $"{displayName}: Policy Debate");
                evDoc.SetField("title_ru", $"{displayName}: Дебаты о курсе");
                WriteRel($"events/99_studio_{id}_debate.json", evDoc.RenderJson());
                evDoc.RenderLocalizationStubs(out var eEn, out var eRu);
                WriteRel($"localization/english/99_studio_{id}_debate.json", eEn);
                WriteRel($"localization/russian/99_studio_{id}_debate.json", eRu);
                break;
            }
            case ModScaffoldKind.MapPack:
            {
                var mapDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.MapPack, id, displayName);
                mapDoc.SetField("author", author);
                WriteRel($"maps/{id}/map.json", mapDoc.RenderJson());
                enLoc[$"studio.map.{id}.name"] = displayName;
                ruLoc[$"studio.map.{id}.name"] = displayName;
                enLoc[$"studio.map.{id}.desc"] = $"Custom map pack authored by {author}.";
                ruLoc[$"studio.map.{id}.desc"] = $"Пользовательский пакет карты от {author}.";
                break;
            }
            case ModScaffoldKind.Scenario:
            {
                var scen = new JsonObject
                {
                    ["id"] = id,
                    ["name"] = displayName,
                    ["description"] = $"Standalone scenario created by {author}.",
                    ["start_date"] = "2024-01-01",
                    ["player_country"] = "GBR",
                    ["base_world"] = true
                };
                WriteRel($"scenarios/{id}/scenario.json", FormatJson(scen));
                enLoc[$"scenario.{id}.name"] = displayName;
                ruLoc[$"scenario.{id}.name"] = displayName;
                enLoc[$"scenario.{id}.desc"] = $"Standalone scenario created by {author}.";
                ruLoc[$"scenario.{id}.desc"] = $"Отдельный сценарий от {author}.";
                break;
            }
            case ModScaffoldKind.Formable:
            {
                var formId = $"{id}_union";
                var formDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.FormableNation, formId, displayName);
                formDoc.SetField("title_ru", displayName);
                WriteRel($"common/formable_nations/99_studio_{formId}.json", formDoc.RenderJson());
                formDoc.RenderLocalizationStubs(out var fEn, out var fRu);
                WriteRel($"localization/english/99_studio_{formId}.json", fEn);
                WriteRel($"localization/russian/99_studio_{formId}.json", fRu);
                break;
            }
            case ModScaffoldKind.GdScript:
            {
                WriteRel("scripts/mod_main.gd",
                    "extends RefCounted\n\n" +
                    "func on_load(api) -> void:\n" +
                    $"	api.log(\"[{id}] Mod loaded; map=\" + api.get_map_id())\n\n" +
                    "func on_campaign_created(api) -> void:\n" +
                    "	var tags: PackedStringArray = api.get_all_tags()\n" +
                    $"	api.log(\"[{id}] Active tags: \" + str(tags.size()))\n\n" +
                    "func on_day_advanced(api) -> void:\n" +
                    "	if api.get_day() % 30 == 0:\n" +
                    "		var wars: Array = api.get_wars(\"\")\n" +
                    $"		api.log(\"[{id}] Monthly wars tracked: \" + str(wars.size()))\n");
                break;
            }
            case ModScaffoldKind.CSharp:
            {
                var asm = ToPascal(id);
                WriteRel($"corecode/{asm}.csproj",
                    "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                    "  <PropertyGroup>\n" +
                    "    <TargetFramework>net10.0</TargetFramework>\n" +
                    "    <ImplicitUsings>enable</ImplicitUsings>\n" +
                    "    <Nullable>disable</Nullable>\n" +
                    $"    <AssemblyName>{asm}</AssemblyName>\n" +
                    "  </PropertyGroup>\n" +
                    "  <ItemGroup Condition=\"'$(GameAssembly)' == ''\">\n" +
                    "    <ProjectReference Include=\"../../../../DealOrForce.csproj\" />\n" +
                    "  </ItemGroup>\n" +
                    "</Project>\n");
                WriteRel("corecode/ModEntry.cs",
                    "using DealOrForce.Modding;\nusing DealOrForce.Simulation;\n\n" +
                    $"namespace {asm};\n\n" +
                    "public sealed class ModEntry : IGlobalityMod\n{\n" +
                    $"    public void OnLoad(ModContext context) => context.Log(\"{displayName} C# module loaded\");\n" +
                    "    public void OnCampaignCreated(ModContext context) { }\n" +
                    "    public void OnDayAdvanced(ModContext context) { }\n}\n");
                break;
            }
            case ModScaffoldKind.FSharp:
            {
                var asm = ToPascal(id);
                WriteRel($"corecode/{asm}.fsproj",
                    "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                    "  <PropertyGroup>\n" +
                    "    <TargetFramework>net10.0</TargetFramework>\n" +
                    $"    <AssemblyName>{asm}</AssemblyName>\n" +
                    "    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>\n" +
                    "  </PropertyGroup>\n" +
                    "  <ItemGroup>\n    <Compile Include=\"ModEntry.fs\" />\n  </ItemGroup>\n" +
                    "  <ItemGroup Condition=\"'$(GameAssembly)' == ''\">\n" +
                    "    <ProjectReference Include=\"../../../../DealOrForce.csproj\" />\n" +
                    "  </ItemGroup>\n" +
                    "</Project>\n");
                WriteRel("corecode/ModEntry.fs",
                    $"namespace {asm}\n\nopen DealOrForce.Modding\n\ntype ModEntry() =\n" +
                    $"    interface IGlobalityMod with\n        member _.OnLoad(context) = context.Log(\"{displayName} F# module loaded\")\n" +
                    "        member _.OnCampaignCreated(_) = ()\n        member _.OnDayAdvanced(_) = ()\n");
                break;
            }
            case ModScaffoldKind.RustWorker:
            {
                var crate = id.Replace('_', '-');
                WriteRel("corecode/Cargo.toml",
                    $"[package]\nname = \"{crate}\"\nversion = \"0.1.0\"\nedition = \"2021\"\n\n" +
                    "[dependencies]\nserde = { version = \"1\", features = [\"derive\"] }\nserde_json = \"1\"\n");
                WriteRel("corecode/src/main.rs",
                    "use std::io::{self, Read};\nuse serde::Deserialize;\n\n" +
                    "#[derive(Deserialize)]\nstruct Request { protocol: u32, event: String, day: u32 }\n\n" +
                    "fn main() -> Result<(), Box<dyn std::error::Error>> {\n" +
                    "    let mut input = String::new();\n" +
                    "    io::stdin().take(2 * 1024 * 1024 + 1).read_to_string(&mut input)?;\n" +
                    "    let req: Request = serde_json::from_str(&input)?;\n" +
                    "    if req.protocol != 1 { return Err(\"unsupported protocol\".into()); }\n" +
                    $"    let out = serde_json::json!({{\"protocol\": 1, \"commands\": [], \"logs\": [format!(\"{id} worker tick {{}} at day {{}}\", req.event, req.day)]}});\n" +
                    "    println!(\"{}\", out);\n    Ok(())\n}\n");
                break;
            }
        }

        WriteRel($"localization/english/00_{id}.json", FormatJson(enLoc));
        WriteRel($"localization/russian/00_{id}.json", FormatJson(ruLoc));
        WriteRel("README.md",
            $"# {displayName}\n\n" +
            $"- **ID:** `{id}`\n- **Kind:** `{request.Kind}`\n- **Author:** {author}\n\n" +
            $"Validate with `Tools/validate-mod.ps1 -ModPath \"{modRoot}\" -Strict` or in the Mod Content Studio.\n");

        return new ModScaffoldResult(modRoot, manifestPath, created);

        static string FormatJson(JsonNode node)
        {
            var buf = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buf, IndentedJson)) node.WriteTo(w);
            return Encoding.UTF8.GetString(buf.WrittenSpan) + "\n";
        }
        static string ToTitle(string snake) => string.Join(' ',
            snake.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
        static string ToPascal(string snake) => string.Concat(
            snake.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
    }
}

public enum ModSnippetTarget
{
    JsonEffect = 0,
    JsonTrigger = 1,
    GdScript = 2,
    Luau = 3
}

public sealed record ModStudioSnippet(
    string Id,
    string TitleKey,
    ModSnippetTarget Target,
    string Code);

/// <summary>
/// Curated validated JSON trigger/effect blocks and GDScript/Luau API hooks for 1-click insertion in Mod Content Studio.
/// </summary>
public static class ModStudioSnippets
{
    public static IReadOnlyList<ModStudioSnippet> All { get; } = Array.AsReadOnly(new ModStudioSnippet[]
    {
        new("trigger_war", "studio.snippet.trigger_war", ModSnippetTarget.JsonTrigger,
            "{\n  \"at_war\": true,\n  \"min_stability\": 0.45,\n  \"max_radicalism\": 0.35\n}"),
        new("trigger_treasury", "studio.snippet.trigger_treasury", ModSnippetTarget.JsonTrigger,
            "{\n  \"min_treasury\": 10.0,\n  \"min_prestige\": 100.0,\n  \"has_feature\": \"diplomatic_missions\"\n}"),
        new("trigger_course", "studio.snippet.trigger_course", ModSnippetTarget.JsonTrigger,
            "{\n  \"tags\": [\"GBR\", \"FRA\", \"DEU\"],\n  \"has_grand_project\": \"transcontinental_hsr\",\n  \"missing_feature\": \"nuclear_strikes\"\n}"),
        new("effect_modifier", "studio.snippet.effect_modifier", ModSnippetTarget.JsonEffect,
            "{\n  \"add_modifier\": { \"supply_diversification_pact\": 365 },\n  \"prestige\": 8.0\n}"),
        new("effect_fiscal_stability", "studio.snippet.effect_fiscal_stability", ModSnippetTarget.JsonEffect,
            "{\n  \"income_share\": -0.03,\n  \"stability\": 0.025,\n  \"war_support\": 0.015,\n  \"political_points\": -15\n}"),
        new("effect_prestige_shift", "studio.snippet.effect_prestige_shift", ModSnippetTarget.JsonEffect,
            "{\n  \"treasury\": 5.0,\n  \"prestige\": 12.0,\n  \"unlock_feature\": \"aviation\"\n}"),
        new("gdscript_hook", "studio.snippet.gdscript_hook", ModSnippetTarget.GdScript,
            "\tvar wars: Array = api.get_wars(\"\")\n" +
            "\tvar map_id: String = api.get_map_id()\n" +
            "\tif wars.size() > 0:\n" +
            "\t\tvar rel: float = api.get_relation(\"GBR\", \"FRA\")\n" +
            "\t\tapi.log(\"Map %s wars=%d rel=%.1f\" % [map_id, wars.size(), rel])\n" +
            "\t\tapi.add_modifier(\"GBR\", \"supply_diversification_pact\", 180)\n"),
        new("luau_hook", "studio.snippet.luau_hook", ModSnippetTarget.Luau,
            "local tags = api:Get(\"get_all_tags\"):Call()\n" +
            "local region = api:Get(\"get_region\"):Call(1)\n" +
            "api:Get(\"log\"):Call(\"Map=\" .. api:Get(\"get_map_id\"):Call() .. \" regions sample=\" .. tostring(region[\"name\"] or \"\"))\n" +
            "api:Get(\"trigger_event\"):Call(tags[1] or \"GBR\", \"industrial_bottlenecks\")\n")
    });

    public static ModStudioSnippet Find(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}
