using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed record NationalCourseDefinition(string Id, string Name, string Description, string Icon,
    IReadOnlyDictionary<string, float> Pressures, IReadOnlyDictionary<string, float> Modifiers);

/// <summary>Societal priorities, not player-selected government promises or immutable ethnic traits.</summary>
public static class NationalCourseCatalog
{
    private static readonly Dictionary<string, NationalCourseDefinition> Entries = new(StringComparer.Ordinal);
    public static readonly HashSet<string> PressureKeys = new(StringComparer.Ordinal)
    { "baseline", "war", "exhaustion", "hardship", "fiscal_crisis", "unrest", "institutions", "openness", "autonomy", "reform", "shock" };
    public static int Revision { get; private set; }
    public static IEnumerable<NationalCourseDefinition> All => Entries.Values.OrderBy(d => d.Id, StringComparer.Ordinal);
    public static NationalCourseDefinition Find(string id) => id is null ? null : Entries.GetValueOrDefault(id);
    static NationalCourseCatalog() => ResetCatalog();
    public static void ResetCatalog()
    {
        Entries.Clear(); Revision++;
        foreach (var doc in GameDataLoader.DataDocuments("common", "national_courses")) using (doc) Merge(doc.RootElement);
    }
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("national_courses: expected an object"); return; }
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//")) continue;
            try
            {
                if (!MechanicFeatures.ValidId(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("invalid course declaration");
                var e = entry.Value;
                var old = Find(entry.Name);
                string Text(string key, string fallback) => e.TryGetProperty(key, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString() : throw new ArgumentException(key + " must be a string") : fallback;
                IReadOnlyDictionary<string, float> pressures = old?.Pressures ?? new Dictionary<string, float>();
                if (e.TryGetProperty("pressures", out var block))
                {
                    if (block.ValueKind != JsonValueKind.Object) throw new ArgumentException("pressures must be an object");
                    var values = new Dictionary<string, float>(StringComparer.Ordinal);
                    foreach (var p in block.EnumerateObject())
                    {
                        if (!PressureKeys.Contains(p.Name) || !p.Value.TryGetSingle(out var f) || !float.IsFinite(f) || MathF.Abs(f) > 4)
                            throw new ArgumentException("unknown or invalid pressure: " + p.Name);
                        values[p.Name] = f;
                    }
                    pressures = values;
                }
                var modifiers = old?.Modifiers ?? new Dictionary<string, float>();
                if (e.TryGetProperty("modifiers", out var m))
                {
                    var local = new List<string>(); modifiers = NationalModifiers.Read(m, entry.Name, local);
                    if (local.Count != 0) throw new ArgumentException(string.Join("; ", local));
                }
                Entries[entry.Name] = new(entry.Name, Text("name", old?.Name ?? entry.Name),
                    Text("description", old?.Description ?? ""), Text("icon", old?.Icon ?? "agenda"), pressures, modifiers);
                Revision++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
            { errors?.Add($"national_courses.{entry.Name}: {ex.Message}"); }
        }
    }
}
