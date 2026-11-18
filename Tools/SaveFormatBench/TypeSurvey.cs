using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// What an attribute-based format would cost, counted instead of guessed: every type a save
/// reaches from <c>CampaignSave</c> through the members System.Text.Json writes today (public
/// fields and properties, minus [JsonIgnore]), and for each the things that decide whether a
/// contractless or an annotated serializer can carry it unchanged.
/// </summary>
public static class TypeSurvey
{
    private sealed class Row
    {
        public Type Type;
        public int Members;
        public int GetOnlyCollections;   // filled in place by JSON (Populate); lost by a set-only reader
        public int GetOnlyOther;         // computed values JSON writes and ignores on read
        public int PrivateSetters;       // public get, non-public set
        public int InitOnly;
        public int ReadonlyFields;
        public int JsonIgnored;
        public int CustomComparers;      // dictionaries/sets a fresh instance builds with a non-default comparer
        public int ObjectOrAbstract;     // members typed object / interface / abstract: need a union or a custom formatter
        public bool JsonConverter;
        public bool NoDefaultCtor;
        public bool IsStruct;
        public bool IsRecord;
        public List<string> Notes = new();
    }

    public static int Run()
    {
        var root = typeof(SaveSystem.CampaignSave);
        var rows = new Dictionary<Type, Row>();
        var queue = new Queue<Type>();
        queue.Enqueue(root);
        var collectionShapes = new SortedSet<string>();
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (rows.ContainsKey(type)) continue;
            var row = new Row
            {
                Type = type,
                IsStruct = type.IsValueType,
                IsRecord = type.GetMethod("<Clone>$") is not null || type.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance) is not null,
                JsonConverter = type.GetCustomAttribute<JsonConverterAttribute>() is not null,
                NoDefaultCtor = !type.IsValueType && type.GetConstructor(Type.EmptyTypes) is null,
            };
            rows[type] = row;
            object fresh = null;
            try { fresh = Activator.CreateInstance(type, nonPublic: true); } catch { }

            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
            {
                Type memberType;
                if (member is FieldInfo f) memberType = f.FieldType;
                else if (member is PropertyInfo p && p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0) memberType = p.PropertyType;
                else continue;
                if (member.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
                {
                    row.JsonIgnored++;
                    continue;
                }
                row.Members++;
                if (member is FieldInfo field && field.IsInitOnly) row.ReadonlyFields++;
                if (member is PropertyInfo property)
                {
                    var set = property.SetMethod;
                    if (set is null)
                    {
                        if (IsCollection(memberType)) { row.GetOnlyCollections++; row.Notes.Add("get-only collection " + member.Name); }
                        else row.GetOnlyOther++;
                    }
                    else if (!set.IsPublic) { row.PrivateSetters++; row.Notes.Add("non-public setter " + member.Name); }
                    else if (set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit")) row.InitOnly++;
                }
                if (fresh is not null && HasCustomComparer(member, fresh)) { row.CustomComparers++; row.Notes.Add("comparer " + member.Name); }
                foreach (var reached in Reach(memberType, collectionShapes))
                {
                    if (reached == typeof(object) || reached.IsInterface || reached.IsAbstract && !reached.IsSealed)
                    {
                        row.ObjectOrAbstract++;
                        row.Notes.Add($"polymorphic {member.Name}: {Pretty(reached)}");
                        continue;
                    }
                    if (IsUserType(reached) && !reached.IsEnum && !rows.ContainsKey(reached)) queue.Enqueue(reached);
                }
            }
        }

        var user = rows.Values.Where(r => IsUserType(r.Type) && !r.Type.IsEnum).OrderBy(r => r.Type.FullName).ToList();
        var enums = new HashSet<Type>();
        foreach (var r in rows.Keys)
            foreach (var m in r.GetMembers(BindingFlags.Public | BindingFlags.Instance))
            {
                var t = m is FieldInfo fi ? fi.FieldType : m is PropertyInfo pi ? pi.PropertyType : null;
                if (t is null) continue;
                foreach (var x in Reach(t, null)) if (x.IsEnum && IsUserType(x)) enums.Add(x);
            }

        Console.WriteLine($"TYPES reached={user.Count} classes={user.Count(r => !r.IsStruct)} structs={user.Count(r => r.IsStruct)} " +
                          $"records={user.Count(r => r.IsRecord)} enums={enums.Count} members={user.Sum(r => r.Members)}");
        // A dynamic (reflection-emit) formatter can only be built for a public type; an
        // internal one needs either the AllowPrivate resolver or a source-generated formatter.
        Console.WriteLine($"  not publicly visible = {user.Count(r => !r.Type.IsVisible)}: " +
                          string.Join(", ", user.Where(r => !r.Type.IsVisible).Select(r => Pretty(r.Type))));
        Console.WriteLine($"  with [JsonConverter] (row formats today) = {user.Count(r => r.JsonConverter)}: " +
                          string.Join(", ", user.Where(r => r.JsonConverter).Select(r => Pretty(r.Type))));
        Console.WriteLine($"  without a public parameterless constructor (class) = {user.Count(r => r.NoDefaultCtor)}: " +
                          string.Join(", ", user.Where(r => r.NoDefaultCtor).Select(r => Pretty(r.Type))));
        Console.WriteLine($"  get-only collection members (JSON fills in place) = {user.Sum(r => r.GetOnlyCollections)} in {user.Count(r => r.GetOnlyCollections > 0)} types");
        Console.WriteLine($"  get-only computed members (written, never read) = {user.Sum(r => r.GetOnlyOther)}");
        Console.WriteLine($"  non-public setters = {user.Sum(r => r.PrivateSetters)}; init-only = {user.Sum(r => r.InitOnly)}; readonly fields = {user.Sum(r => r.ReadonlyFields)}");
        Console.WriteLine($"  members [JsonIgnore]d = {user.Sum(r => r.JsonIgnored)} in {user.Count(r => r.JsonIgnored > 0)} types");
        Console.WriteLine($"  collections built with a non-default comparer = {user.Sum(r => r.CustomComparers)} in {user.Count(r => r.CustomComparers > 0)} types");
        Console.WriteLine($"  members typed object/interface/abstract = {user.Sum(r => r.ObjectOrAbstract)}");
        Console.WriteLine("  collection shapes: " + string.Join(", ", collectionShapes));
        Console.WriteLine();
        Console.WriteLine("TYPE|name|kind|members|getonly_coll|getonly_other|private_set|readonly|json_ignored|comparers|polymorphic|converter|notes");
        foreach (var r in user.OrderByDescending(r => r.Members))
            Console.WriteLine($"TYPE|{Pretty(r.Type)}|{(r.IsStruct ? "struct" : "class")}{(r.IsRecord ? " record" : "")}|{r.Members}|{r.GetOnlyCollections}|{r.GetOnlyOther}|" +
                              $"{r.PrivateSetters}|{r.ReadonlyFields}|{r.JsonIgnored}|{r.CustomComparers}|{r.ObjectOrAbstract}|{(r.JsonConverter ? "yes" : "")}|{string.Join("; ", r.Notes.Take(6))}");
        return 0;
    }

    private static bool IsUserType(Type t) => t.Namespace?.StartsWith("DealOrForce", StringComparison.Ordinal) == true || t.Namespace is null && !t.IsPrimitive;

    private static bool IsCollection(Type t) => t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t);

    /// <summary>The element types a member leads to: through arrays, nullable, and generic
    /// collections, down to the types that are written as objects.</summary>
    private static IEnumerable<Type> Reach(Type t, SortedSet<string> shapes)
    {
        if (t.IsArray) { shapes?.Add("T[]"); foreach (var x in Reach(t.GetElementType()!, shapes)) yield return x; yield break; }
        if (Nullable.GetUnderlyingType(t) is { } inner) { foreach (var x in Reach(inner, shapes)) yield return x; yield break; }
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            {
                shapes?.Add(def.Name);
                foreach (var a in t.GetGenericArguments()) foreach (var x in Reach(a, shapes)) yield return x;
                yield break;
            }
        }
        if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(Guid) || t == typeof(TimeSpan)) yield break;
        yield return t;
    }

    private static bool HasCustomComparer(MemberInfo member, object fresh)
    {
        object value;
        try { value = member is FieldInfo f ? f.GetValue(fresh) : ((PropertyInfo)member).GetValue(fresh); }
        catch { return false; }
        if (value is null) return false;
        var comparer = value.GetType().GetProperty("Comparer")?.GetValue(value);
        if (comparer is null) return false;
        var keyType = value.GetType().IsGenericType ? value.GetType().GetGenericArguments()[0] : null;
        if (keyType is null) return false;
        var defaultComparer = typeof(EqualityComparer<>).MakeGenericType(keyType).GetProperty("Default")!.GetValue(null);
        return !Equals(comparer, defaultComparer) && comparer.GetType() != defaultComparer!.GetType();
    }

    private static string Pretty(Type t) =>
        !t.IsGenericType ? (t.DeclaringType is null ? t.Name : Pretty(t.DeclaringType) + "." + t.Name)
            : t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(Pretty)) + ">";
}
