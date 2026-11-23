using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    Localization = 6
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
    IReadOnlyList<ModStudioField> Fields);

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
    private static readonly JsonWriterOptions PrettyWriterOptions = new() { Indented = true };

    private static string WriteIndentedJson(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, PrettyWriterOptions))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static readonly string[] ValidModifierKeys =
    {
        "output", "research_speed", "construction_speed", "construction_cost", "tax_efficiency",
        "trade_reach", "reconstruction_speed", "credit_capacity", "borrowing_cost", "army_attack",
        "army_defense", "army_speed", "supply", "organisation_recovery", "entrenchment_speed",
        "reinforcement_rate", "recruitment_speed", "recruitment_cost", "deterrence", "naval_speed",
        "shipbuilding_speed", "stability_weekly", "war_support_weekly", "radicalism_weekly", "living_weekly"
    };

    private static readonly IReadOnlyDictionary<ModStudioCategory, ModStudioTemplate> TemplatesByCategory = BuildTemplates();

    public ModStudioCategory Category { get; }
    public ModStudioTemplate Template { get; }
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private ModStudioDocument(ModStudioCategory category)
    {
        Category = category;
        Template = TemplatesByCategory[category];
        foreach (var field in Template.Fields)
            _values[field.Key] = field.DefaultValue ?? "";
    }

    public static IReadOnlyList<ModStudioTemplate> AllTemplates => TemplatesByCategory.Values.ToArray();

    public static ModStudioTemplate TemplateOf(ModStudioCategory category) => TemplatesByCategory[category];

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
        _values[key] = value ?? "";
    }

    public string SuggestedRelativePath()
    {
        var id = GetField("id").Trim();
        var fileId = SafeIdRegex.IsMatch(id) ? id : "custom_entry";
        return Category switch
        {
            ModStudioCategory.Event => $"events/99_studio_{fileId}.json",
            ModStudioCategory.Decision => $"common/decisions/99_studio_{fileId}.json",
            ModStudioCategory.Modifier => $"common/modifiers/99_studio_{fileId}.json",
            ModStudioCategory.StrategicDoctrine => $"common/strategic_doctrines/99_studio_{fileId}.json",
            ModStudioCategory.Disease => $"common/diseases/99_studio_{fileId}.json",
            ModStudioCategory.Disaster => $"common/disasters/99_studio_{fileId}.json",
            ModStudioCategory.Localization => $"localization/english/99_studio_{fileId}.json",
            _ => $"common/modifiers/99_studio_{fileId}.json"
        };
    }

    public string RenderJson()
    {
        var id = GetField("id").Trim();
        var root = new JsonObject();
        switch (Category)
        {
            case ModStudioCategory.Event:
            {
                var opt1 = new JsonObject
                {
                    ["title"] = $"studio.ev.{id}.opt1",
                    ["consequence"] = $"studio.ev.{id}.con1",
                    ["effects"] = new JsonObject
                    {
                        ["stability"] = ParseFloat("opt1_stability", 0.02f),
                        ["political_points"] = ParseFloat("opt1_pp", -10f)
                    }
                };
                var opt2 = new JsonObject
                {
                    ["title"] = $"studio.ev.{id}.opt2",
                    ["consequence"] = $"studio.ev.{id}.con2",
                    ["effects"] = new JsonObject
                    {
                        ["treasury_percent"] = ParseFloat("opt2_treasury_pct", 0.02f),
                        ["stability"] = ParseFloat("opt2_stability", -0.01f)
                    }
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
                root[id] = new JsonObject
                {
                    ["name"] = $"studio.dec.{id}.name",
                    ["description"] = $"studio.dec.{id}.desc",
                    ["unavailable"] = $"studio.dec.{id}.unavail",
                    ["resource_costs"] = new JsonObject { ["political_points"] = ParseInt("pp_cost", 20) },
                    ["cooldown_days"] = ParseInt("cooldown_days", 365),
                    ["gdp_cost_share"] = ParseFloat("gdp_cost_share", 0.002f),
                    ["trigger"] = new JsonObject { ["min_stability"] = ParseFloat("min_stability", 0.35f) },
                    ["effects"] = new JsonObject
                    {
                        ["stability"] = ParseFloat("effect_stability", 0.03f),
                        ["war_support"] = ParseFloat("effect_war_support", 0.02f)
                    }
                };
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
            case ModStudioCategory.Modifier:
                en[$"studio.mod.{id}.name"] = titleEn;
                ru[$"studio.mod.{id}.name"] = titleRu;
                en[$"studio.mod.{id}.desc"] = descEn;
                ru[$"studio.mod.{id}.desc"] = descRu;
                break;
            case ModStudioCategory.StrategicDoctrine:
                en[$"studio.strat.{id}.name"] = titleEn;
                ru[$"studio.strat.{id}.name"] = titleRu;
                en[$"studio.strat.{id}.desc"] = descEn;
                ru[$"studio.strat.{id}.desc"] = descRu;
                break;
            case ModStudioCategory.Disease:
                en[$"studio.dis.{id}.name"] = titleEn;
                ru[$"studio.dis.{id}.name"] = titleRu;
                en[$"studio.dis.{id}.summary"] = descEn;
                ru[$"studio.dis.{id}.summary"] = descRu;
                en[$"studio.dis.{id}.cause"] = descEn;
                ru[$"studio.dis.{id}.cause"] = descRu;
                break;
            case ModStudioCategory.Disaster:
                en[$"studio.haz.{id}.name"] = titleEn;
                ru[$"studio.haz.{id}.name"] = titleRu;
                en[$"studio.haz.{id}.summary"] = descEn;
                ru[$"studio.haz.{id}.summary"] = descRu;
                en[$"studio.haz.{id}.cause"] = descEn;
                ru[$"studio.haz.{id}.cause"] = descRu;
                break;
            case ModStudioCategory.Localization:
                var locKey = string.IsNullOrWhiteSpace(GetField("loc_key")) ? $"studio.custom.{id}" : GetField("loc_key").Trim();
                en[locKey] = GetField("en_value");
                ru[locKey] = string.IsNullOrWhiteSpace(GetField("ru_value")) ? GetField("en_value") : GetField("ru_value");
                break;
        }
        enJson = WriteIndentedJson(en);
        ruJson = WriteIndentedJson(ru);
    }

    public ModStudioValidationReport Validate()
    {
        var issues = new List<ModStudioIssue>();
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
                    new("opt1_consequence", "studio.field.opt1_consequence", ModStudioFieldKind.Text, "Raises stability at a political capital cost."),
                    new("opt1_stability", "studio.field.opt1_stability", ModStudioFieldKind.Float, "0.025", -0.25f, 0.25f),
                    new("opt1_pp", "studio.field.opt1_pp", ModStudioFieldKind.Float, "-10", -100f, 100f),
                    new("opt2_label", "studio.field.opt2_label", ModStudioFieldKind.Text, "Protect the treasury"),
                    new("opt2_consequence", "studio.field.opt2_consequence", ModStudioFieldKind.Text, "Builds reserves with minor public frustration."),
                    new("opt2_treasury_pct", "studio.field.opt2_treasury_pct", ModStudioFieldKind.Float, "0.02", -0.25f, 0.25f),
                    new("opt2_stability", "studio.field.opt2_stability", ModStudioFieldKind.Float, "-0.01", -0.25f, 0.25f)
                }),
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
                    new("gdp_cost_share", "studio.field.gdp_cost_share", ModStudioFieldKind.Float, "0.002", 0f, 0.15f),
                    new("min_stability", "studio.field.min_stability", ModStudioFieldKind.Float, "0.35", 0f, 1.0f),
                    new("effect_stability", "studio.field.effect_stability", ModStudioFieldKind.Float, "0.03", -0.25f, 0.25f),
                    new("effect_war_support", "studio.field.effect_war_support", ModStudioFieldKind.Float, "0.02", -0.25f, 0.25f)
                }),
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
                }),
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
                }),
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
                }),
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
                }),
            [ModStudioCategory.Localization] = new(
                ModStudioCategory.Localization, "localization/english", "studio.cat.localization", "studio.cat.localization_desc",
                new ModStudioField[]
                {
                    new("id", "studio.field.id", ModStudioFieldKind.Id, "custom_strings", HelpKey: "studio.help.id"),
                    new("loc_key", "studio.field.loc_key", ModStudioFieldKind.Text, "my_mod.greeting"),
                    new("en_value", "studio.field.en_value", ModStudioFieldKind.Text, "Welcome to my custom scenario!"),
                    new("ru_value", "studio.field.ru_value", ModStudioFieldKind.Text, "Добро пожаловать в мой сценарий!", Required: false)
                })
        };
        return map;
    }
}
