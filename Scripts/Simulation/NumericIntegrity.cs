using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>Reject non-finite state, without turning a broken calculation into a believable zero.</summary>
public static class NumericIntegrity
{
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> RecordProperties = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> ScalarFields = new();

    public static List<string> Inspect(object snapshot, int limit = 24)
    {
        var issues = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(snapshot, new StringBuilder("campaign"), issues, seen, Math.Max(1, limit));
        return issues;
    }

    private static void Walk(object value, StringBuilder path, List<string> issues, HashSet<object> seen, int limit)
    {
        if (value is null || issues.Count >= limit) return;
        if (value is float f) { if (!float.IsFinite(f)) issues.Add(path + " = " + f); return; }
        if (value is double d) { if (!double.IsFinite(d)) issues.Add(path + " = " + d); return; }
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string || value is decimal) return;
        if (!type.IsValueType && !seen.Add(value)) return;
        var length = path.Length;
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                path.Append('[').Append(entry.Key).Append(']');
                Walk(entry.Value, path, issues, seen, limit);
                path.Length = length;
                if (issues.Count >= limit) return;
            }
            return;
        }
        if (value is IEnumerable list)
        {
            var index = 0;
            foreach (var item in list)
            {
                path.Append('[').Append(index++).Append(']');
                Walk(item, path, issues, seen, limit);
                path.Length = length;
                if (issues.Count >= limit) return;
            }
            return;
        }
        if (type.Namespace != typeof(Campaign).Namespace) return;
        var fields = Fields.GetOrAdd(type, t => t.GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Where(field => field.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always).ToArray());
        foreach (var field in fields)
        {
            path.Append('.').Append(field.Name);
            Walk(field.GetValue(value), path, issues, seen, limit);
            path.Length = length;
            if (issues.Count >= limit) return;
        }
        // Positional records (notably history samples and market quotes) have properties,
        // not public fields. Do not invoke computed UI properties of mutable entities.
        if (!type.IsValueType || fields.Length != 0) return;
        foreach (var property in RecordProperties.GetOrAdd(type, t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 &&
                                 p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always).ToArray()))
        {
            path.Append('.').Append(property.Name);
            Walk(property.GetValue(value), path, issues, seen, limit);
            path.Length = length;
            if (issues.Count >= limit) return;
        }
    }

    /// <summary>Cheap weekly checkpoint; a full, nested validation also runs before saving.</summary>
    public static string FirstLiveIssue(Campaign c)
    {
        foreach (var n in c.Nations)
        {
            var issue = ScalarIssue(n, "country " + n.Tag) ?? ScalarIssue(n.Movement, "movement " + n.Tag);
            if (issue is not null) return issue;
            foreach (var q in n.Market)
                if (!float.IsFinite(q.Value.Price) || !float.IsFinite(q.Value.Production) ||
                    !float.IsFinite(q.Value.Demand)) return "market " + n.Tag + "." + q.Key;
            if (n.Movement is { } movement)
                foreach (var support in movement.Support)
                    if (!float.IsFinite(support.Value)) return "support " + n.Tag + "[" + support.Key + "]";
        }
        foreach (var s in c.States)
        {
            var issue = ScalarIssue(s, "region " + s.Id);
            if (issue is not null) return issue;
            foreach (var p in s.PopulationGroups)
                if (!float.IsFinite(p.Employment) || !float.IsFinite(p.Wealth) ||
                    !float.IsFinite(p.StandardOfLiving) || !float.IsFinite(p.Radicalism)) return "population " + s.Id;
        }
        foreach (var a in c.Armies) { var issue = ScalarIssue(a, "army " + a.Id); if (issue is not null) return issue; }
        foreach (var b in c.Battles) { var issue = ScalarIssue(b, "battle " + b.Id); if (issue is not null) return issue; }
        foreach (var w in c.Wars) { var issue = ScalarIssue(w, "war " + w.Id); if (issue is not null) return issue; }
        foreach (var f in c.Fleets) { var issue = ScalarIssue(f, "fleet " + f.Id); if (issue is not null) return issue; }
        foreach (var f in c.SupportFormations) { var issue = ScalarIssue(f, "support " + f.Id); if (issue is not null) return issue; }
        if (c.ProvinceOccupation is { } occupation)
            for (var index = 0; index < occupation.Length; index++)
                if (!float.IsFinite(occupation[index])) return "occupation[" + index + "]";
        foreach (var relation in c.Relations.Values)
        {
            var issue = ScalarIssue(relation, "relation");
            if (issue is not null) return issue;
        }
        return null;
    }

    private static string ScalarIssue(object value, string path)
    {
        if (value is null) return null;
        foreach (var field in ScalarFields.GetOrAdd(value.GetType(), t => t.GetFields(BindingFlags.Public | BindingFlags.Instance)
                     .Where(f => f.FieldType == typeof(float) || f.FieldType == typeof(double)).ToArray()))
        {
            var number = field.GetValue(value);
            if (number is float f && !float.IsFinite(f) || number is double d && !double.IsFinite(d))
                return path + "." + field.Name + " = " + number;
        }
        return null;
    }
}

public sealed class CampaignIntegrityException : InvalidOperationException
{
    public string Issue { get; }
    public string DiagnosticPath { get; }
    public CampaignIntegrityException(string issue, string diagnosticPath = "") : base("Invalid campaign numbers: " + issue)
    { Issue = issue; DiagnosticPath = diagnosticPath; }
}

public sealed partial class Campaign
{
    [JsonIgnore] public string SimulationFault { get; private set; } = "";

    /// <summary>A failed, partially executed step must not be resumed as if it succeeded.</summary>
    public void BlockSimulation(string reason, Exception exception = null)
    {
        SimulationFault = reason ?? "simulation error";
        IsPaused = true;
        // The exception goes into the envelope when it exists: the diagnostic snapshot quotes
        // only the message, and a message without a stack is a bug report nobody can act on.
        CrashEnvelope.Dump(this, exception, reason);
        MarkStrategicChange();
    }
}
