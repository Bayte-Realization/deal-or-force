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
    // Each type's float and double fields, read through a compiled getter rather than
    // FieldInfo.GetValue: the weekly checkpoint read every number of every country, region,
    // army and relation by reflection, boxing each, and built a path string for every object
    // whether or not anything was wrong - twenty milliseconds of one indivisible frame.
    private static readonly ConcurrentDictionary<Type, (FieldInfo Field, Func<object, double> Read)[]> ScalarReaders = new();

    private static (FieldInfo Field, Func<object, double> Read)[] ReadersFor(Type type) =>
        ScalarReaders.GetOrAdd(type, t => ScalarFields.GetOrAdd(t, k => k.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(float) || f.FieldType == typeof(double)).ToArray())
            .Select(field =>
            {
                var boxed = System.Linq.Expressions.Expression.Parameter(typeof(object), "value");
                var read = System.Linq.Expressions.Expression.Convert(
                    System.Linq.Expressions.Expression.Field(System.Linq.Expressions.Expression.Convert(boxed, t), field), typeof(double));
                return (field, System.Linq.Expressions.Expression.Lambda<Func<object, double>>(read, boxed).Compile());
            }).ToArray());

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
            var issue = ScalarIssue(n, "country ", n.Tag) ?? ScalarIssue(n.Movement, "movement ", n.Tag);
            if (issue is not null) return issue;
            foreach (var q in n.Market)
                if (!float.IsFinite(q.Value.Price) || !float.IsFinite(q.Value.Production) ||
                    !float.IsFinite(q.Value.Demand)) return "market " + n.Tag + "." + q.Key;
            if (n.Movement is { } movement)
                foreach (var support in movement.Support)
                    if (!float.IsFinite(support.Value)) return "support " + n.Tag + "[" + support.Key + "]";
        }
        // The regions and their people are most of the walk - two thousand regions, eighty
        // thousand groups, read through reflection - and reading is all it does, so they are read
        // side by side. The answer is the first bad region in list order, exactly as one at a time.
        var regionIssue = FirstRegionIssue(c.States);
        if (regionIssue is not null) return regionIssue;
        foreach (var a in c.Armies) { var issue = ScalarIssue(a, "army ", a.Id); if (issue is not null) return issue; }
        foreach (var b in c.Battles) { var issue = ScalarIssue(b, "battle ", b.Id); if (issue is not null) return issue; }
        foreach (var w in c.Wars) { var issue = ScalarIssue(w, "war ", w.Id); if (issue is not null) return issue; }
        foreach (var f in c.Fleets) { var issue = ScalarIssue(f, "fleet ", f.Id); if (issue is not null) return issue; }
        foreach (var f in c.SupportFormations) { var issue = ScalarIssue(f, "support ", f.Id); if (issue is not null) return issue; }
        if (c.ProvinceOccupation is { } occupation)
            for (var index = 0; index < occupation.Length; index++)
                if (!float.IsFinite(occupation[index])) return "occupation[" + index + "]";
        foreach (var relation in c.Relations.Values)
        {
            var issue = ScalarIssue(relation, "relation", "");
            if (issue is not null) return issue;
        }
        return null;
    }

    private static string RegionIssue(ScenarioState s)
    {
        var issue = ScalarIssue(s, "region ", s.Id);
        if (issue is not null) return issue;
        foreach (var p in s.PopulationGroups)
            if (!float.IsFinite(p.Employment) || !float.IsFinite(p.Wealth) ||
                !float.IsFinite(p.StandardOfLiving) || !float.IsFinite(p.Radicalism)) return "population " + s.Id;
        return null;
    }

    private static string FirstRegionIssue(List<ScenarioState> states)
    {
        if (states.Count < 256)
        {
            foreach (var s in states) { var issue = RegionIssue(s); if (issue is not null) return issue; }
            return null;
        }
        var first = int.MaxValue;
        string found = null;
        var gate = new object();
        System.Threading.Tasks.Parallel.For(0, states.Count,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8)) },
            index =>
            {
                if (index > System.Threading.Volatile.Read(ref first)) return;
                var issue = RegionIssue(states[index]);
                if (issue is null) return;
                lock (gate) if (index < first) { first = index; found = issue; }
            });
        return found;
    }

    /// <summary>The first non-finite number on an object, named by what it is and which one, or null. The name is built only when there is something wrong.</summary>
    private static string ScalarIssue(object value, string kind, object id)
    {
        if (value is null) return null;
        foreach (var (field, read) in ReadersFor(value.GetType()))
        {
            var number = read(value);
            if (!double.IsFinite(number)) return kind + id + "." + field.Name + " = " + field.GetValue(value);
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
