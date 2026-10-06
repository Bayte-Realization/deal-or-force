using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed record PoliticalPolicy
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Scope { get; init; } = "nation";
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "government";
    public bool Default { get; init; }
    public float CostShare { get; init; } = .00002f;
    public int CooldownDays { get; init; } = 90;
    public int DurationDays { get; init; } = 180;
    public string Goal { get; init; } = "stability";
    public float GoalValue { get; init; } = .70f;
    public float ForeignTrust { get; init; }
    public IReadOnlyDictionary<string, float> Modifiers { get; init; } = new Dictionary<string, float>();
}

/// <summary>Same safe JSON/YAML overlay contract for laws and political agendas.</summary>
public static class PoliticalPolicyCatalog
{
    private static readonly Dictionary<string, PoliticalPolicy> Laws = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PoliticalPolicy> Agendas = new(StringComparer.Ordinal);
    private static PoliticalPolicy[] NationalCategoryRows;
    private static Dictionary<string, PoliticalPolicy> Baselines;
    public static IReadOnlyList<PoliticalPolicy> NationalCategories => NationalCategoryRows ??=
        AllLaws.Where(p => p.Scope == "nation").GroupBy(p => p.Category).Select(g => g.First()).ToArray();
    public static PoliticalPolicy Baseline(PoliticalPolicy p)
    {
        Baselines ??= AllLaws.Where(d => d.Default).GroupBy(d => d.Scope + ":" + d.Category)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return Baselines.GetValueOrDefault(p.Scope + ":" + p.Category);
    }
    public static IEnumerable<PoliticalPolicy> AllLaws => Laws.Values.OrderBy(p => p.Category).ThenBy(p => p.Id, StringComparer.Ordinal);
    public static IEnumerable<PoliticalPolicy> AllAgendas => Agendas.Values.OrderBy(p => p.Id, StringComparer.Ordinal);
    public static PoliticalPolicy Find(string id, bool agenda = false) => id is null ? null : (agenda ? Agendas : Laws).GetValueOrDefault(id);
    static PoliticalPolicyCatalog() => ResetCatalog();
    public static void ResetCatalog()
    {
        Laws.Clear(); Agendas.Clear(); NationalCategoryRows = null; Baselines = null;
        foreach (var doc in GameDataLoader.DataDocuments("common", "laws")) using (doc) MergeLaws(doc.RootElement);
        foreach (var doc in GameDataLoader.DataDocuments("common", "agendas")) using (doc) MergeAgendas(doc.RootElement);
    }
    public static void MergeLaws(JsonElement root, List<string> errors = null) => Merge(root, false, errors);
    public static void MergeAgendas(JsonElement root, List<string> errors = null) => Merge(root, true, errors);
    private static void Merge(JsonElement root, bool agenda, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("policies: expected an object"); return; }
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//")) continue;
            try
            {
                if (!MechanicFeatures.ValidId(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("invalid policy id or declaration");
                var d = Find(entry.Name, agenda) ?? new PoliticalPolicy { Id = entry.Name, Name = entry.Name };
                var e = entry.Value;
                string Text(string key, string old) => e.TryGetProperty(key, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString() : throw new ArgumentException(key + " must be a string") : old;
                float Number(string key, float old, float min, float max)
                {
                    if (!e.TryGetProperty(key, out var v)) return old;
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetSingle(out var f) || !float.IsFinite(f) || f < min || f > max)
                        throw new ArgumentException(key + " outside allowed range");
                    return f;
                }
                var modifiers = d.Modifiers;
                if (e.TryGetProperty("modifiers", out var block))
                {
                    var local = new List<string>(); modifiers = NationalModifiers.Read(block, entry.Name, local);
                    if (local.Count != 0) throw new ArgumentException(string.Join("; ", local));
                }
                d = d with { Name = Text("name", d.Name), Category = Text("category", d.Category),
                    Scope = Text("scope", d.Scope), Description = Text("description", d.Description), Icon = Text("icon", d.Icon),
                    CostShare = Number("cost_share", d.CostShare, 0, .01f), CooldownDays = (int)Number("cooldown_days", d.CooldownDays, 7, 3650),
                    DurationDays = (int)Number("duration_days", d.DurationDays, 28, 3650),
                    Goal = Text("goal", d.Goal), GoalValue = Number("goal_value", d.GoalValue, 0, 4),
                    ForeignTrust = Number("foreign_trust", d.ForeignTrust, -.3f, .3f), Modifiers = modifiers };
                if (e.TryGetProperty("default", out var baseline))
                    d = d with { Default = baseline.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? baseline.GetBoolean() : throw new ArgumentException("default must be boolean") };
                if (d.Scope is not ("nation" or "region" or "foreign") || d.Category.Length == 0)
                    throw new ArgumentException("scope must be nation/region/foreign and category is required");
                if (agenda && (d.Scope != "nation" || d.Goal is not ("stability" or "living" or "debt_ratio" or "peace")))
                    throw new ArgumentException("agendas require nation scope and a supported goal");
                if (d.Scope == "region" && d.Modifiers.Keys.Any(k => k is not ("radicalism_weekly" or "living_weekly" or "stability_weekly")))
                    throw new ArgumentException("regional laws support radicalism_weekly/living_weekly/stability_weekly only");
                if (d.Scope == "foreign" && d.Modifiers.Count > 0)
                    throw new ArgumentException("foreign laws use foreign_trust, not national modifiers");
                (agenda ? Agendas : Laws)[d.Id] = d;
                NationalCategoryRows = null; Baselines = null;
            }
            catch (ArgumentException ex) { errors?.Add($"policies.{entry.Name}: {ex.Message}"); }
        }
    }
}
