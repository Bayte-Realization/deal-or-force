using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>
/// Produces a deep-isolated snapshot of a <see cref="CampaignSave"/> on the simulation frame so
/// <see cref="SaveSystem.PreparedSave.Commit"/> can serialise JSON, splice history, compress, and
/// write atomically on a worker thread without blocking the frame or racing live mutations.
/// </summary>
internal static class SaveSnapshotCloner
{
    private delegate object CloneMemberDelegate(object source, Dictionary<object, object> visited);

    private sealed class TypePlan
    {
        public bool IsImmutable;
        public bool IsLeaf;
        public bool NeedsVisited;
        public Func<object, object> ShallowClone;
        public FieldSlot[] MutableFields = Array.Empty<FieldSlot>();
        public CloneMemberDelegate CustomCloner;
    }

    private sealed class FieldSlot
    {
        public FieldInfo Field;
        public Type KnownType;
        public TypePlan KnownPlan;
        public Func<object, object> Getter;
        public Action<object, object> Setter;
    }

    private static class EmptyList<T>
    {
        public static readonly List<T> Instance = new(0);
    }

    private static class EmptyDict<TKey, TValue> where TKey : notnull
    {
        public static readonly Dictionary<TKey, TValue> Instance = new(0);
    }

    private static class EmptySet<T>
    {
        public static readonly HashSet<T> Instance = new(0);
    }

    private static readonly ConcurrentDictionary<Type, TypePlan> Plans = new();
    private static readonly ConcurrentDictionary<Type, bool> ImmutableCache = new();
    private static readonly ConcurrentDictionary<Type, bool> CycleCache = new();
    private static readonly ConstructorInfo ObjectCtor = typeof(object).GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo ObjectMemberwiseCloneMethod =
        typeof(object).GetMethod(nameof(MemberwiseClone), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo FuncCloneInvoke =
        typeof(Func<object, object>).GetMethod(nameof(Func<object, object>.Invoke))!;
    private static readonly MethodInfo CloneMemberInvoke =
        typeof(CloneMemberDelegate).GetMethod(nameof(CloneMemberDelegate.Invoke))!;
    private static readonly MethodInfo CloneObjectMethod =
        typeof(SaveSnapshotCloner).GetMethod(nameof(CloneObject), BindingFlags.Static | BindingFlags.NonPublic,
            null, new[] { typeof(object), typeof(Dictionary<object, object>) }, null)!;
    private static readonly Func<object, object> ObjectMemberwiseClone = CreateMemberwiseClone();

    [ThreadStatic]
    private static Dictionary<object, object> _visitedScratch;

    internal static void IsolateListElements<T>(List<T> list, Dictionary<object, object> visited)
    {
        if (list is null || list.Count == 0 || IsImmutableType(typeof(T))) return;
        var span = CollectionsMarshal.AsSpan(list);
        if (typeof(T).IsSealed)
        {
            var plan = PlanOf(typeof(T));
            if (plan.IsImmutable) return;
            if (plan.IsLeaf)
            {
                var shallow = plan.ShallowClone;
                for (var i = 0; i < span.Length; i++)
                {
                    var item = span[i];
                    if (item is not null) span[i] = (T)shallow(item);
                }
                return;
            }
            for (var i = 0; i < span.Length; i++)
            {
                var item = span[i];
                if (item is not null) span[i] = (T)CloneWithPlan(item, plan, visited);
            }
            return;
        }
        for (var i = 0; i < span.Length; i++)
        {
            var item = span[i];
            if (item is not null) span[i] = (T)CloneObject(item, visited);
        }
    }

    internal static Dictionary<TKey, TValue> CloneDictionaryValues<TKey, TValue>(
        Dictionary<TKey, TValue> dict, Dictionary<object, object> visited) where TKey : notnull
    {
        if (dict is null || dict.Count == 0)
            return new Dictionary<TKey, TValue>(0, dict?.Comparer);
        var keyImmutable = IsImmutableType(typeof(TKey));
        var valueImmutable = IsImmutableType(typeof(TValue));
        if (keyImmutable && valueImmutable)
            return new Dictionary<TKey, TValue>(dict, dict.Comparer);

        var copy = new Dictionary<TKey, TValue>(dict.Count, dict.Comparer);
        var keyPlan = !keyImmutable && typeof(TKey).IsSealed ? PlanOf(typeof(TKey)) : null;
        var valuePlan = !valueImmutable && typeof(TValue).IsSealed ? PlanOf(typeof(TValue)) : null;

        if (keyImmutable && valuePlan is not null)
        {
            if (valuePlan.IsLeaf)
            {
                var shallow = valuePlan.ShallowClone;
                foreach (var pair in dict)
                    copy.Add(pair.Key, pair.Value is null ? default! : (TValue)shallow(pair.Value));
                return copy;
            }
            if (!valuePlan.NeedsVisited && valuePlan.CustomCloner is { } custom)
            {
                foreach (var pair in dict)
                    copy.Add(pair.Key, pair.Value is null ? default! : (TValue)custom(pair.Value, visited));
                return copy;
            }
            foreach (var pair in dict)
                copy.Add(pair.Key, pair.Value is null ? default! : (TValue)CloneWithPlan(pair.Value, valuePlan, visited));
            return copy;
        }

        foreach (var pair in dict)
        {
            var k = keyImmutable || pair.Key is null ? pair.Key : keyPlan is not null ? (TKey)CloneWithPlan(pair.Key, keyPlan, visited) : (TKey)CloneObject(pair.Key, visited);
            var v = valueImmutable || pair.Value is null ? pair.Value : valuePlan is not null ? (TValue)CloneWithPlan(pair.Value, valuePlan, visited) : (TValue)CloneObject(pair.Value, visited);
            copy.Add(k, v);
        }
        return copy;
    }

    internal static void CopyClonedDictionaryValues<TKey, TValue>(
        Dictionary<TKey, TValue> source, Dictionary<TKey, TValue> destination, Dictionary<object, object> visited)
        where TKey : notnull
    {
        if (source is null || source.Count == 0) return;
        var keyImmutable = IsImmutableType(typeof(TKey));
        var valueImmutable = IsImmutableType(typeof(TValue));
        if (keyImmutable && valueImmutable)
        {
            foreach (var pair in source) destination[pair.Key] = pair.Value;
            return;
        }
        var keyPlan = !keyImmutable && typeof(TKey).IsSealed ? PlanOf(typeof(TKey)) : null;
        var valuePlan = !valueImmutable && typeof(TValue).IsSealed ? PlanOf(typeof(TValue)) : null;
        if (keyImmutable && valuePlan is not null)
        {
            if (valuePlan.IsLeaf)
            {
                var shallow = valuePlan.ShallowClone;
                foreach (var pair in source)
                    destination[pair.Key] = pair.Value is null ? default! : (TValue)shallow(pair.Value);
                return;
            }
            if (!valuePlan.NeedsVisited && valuePlan.CustomCloner is { } custom)
            {
                foreach (var pair in source)
                    destination[pair.Key] = pair.Value is null ? default! : (TValue)custom(pair.Value, visited);
                return;
            }
            foreach (var pair in source)
                destination[pair.Key] = pair.Value is null ? default! : (TValue)CloneWithPlan(pair.Value, valuePlan, visited);
            return;
        }
        foreach (var pair in source)
        {
            var k = keyImmutable || pair.Key is null ? pair.Key : keyPlan is not null ? (TKey)CloneWithPlan(pair.Key, keyPlan, visited) : (TKey)CloneObject(pair.Key, visited);
            var v = valueImmutable || pair.Value is null ? pair.Value : valuePlan is not null ? (TValue)CloneWithPlan(pair.Value, valuePlan, visited) : (TValue)CloneObject(pair.Value, visited);
            destination[k] = v;
        }
    }

    internal static void IsolateDictionaryValues<TKey, TValue>(Dictionary<TKey, TValue> dict, Dictionary<object, object> visited)
    {
        if (dict is null || dict.Count == 0 || IsImmutableType(typeof(TValue))) return;
        var valuePlan = typeof(TValue).IsSealed ? PlanOf(typeof(TValue)) : null;
        var keys = new TKey[dict.Count];
        dict.Keys.CopyTo(keys, 0);
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var value = dict[key];
            if (value is not null)
                dict[key] = valuePlan is not null ? (TValue)CloneWithPlan(value, valuePlan, visited) : (TValue)CloneObject(value, visited);
        }
    }

    internal static Dictionary<object, object> RentVisited()
    {
        var visited = _visitedScratch ??= new Dictionary<object, object>(4096, ReferenceEqualityComparer.Instance);
        visited.Clear();
        return visited;
    }

    internal static void ReturnVisited(Dictionary<object, object> visited) => visited?.Clear();

    internal static object CloneObject(object value, Dictionary<object, object> visited = null)
    {
        if (value is null) return null;
        return CloneWithPlan(value, PlanOf(value.GetType()), visited);
    }

    private static object CloneWithPlan(object value, TypePlan plan, Dictionary<object, object> visited)
    {
        if (plan.IsImmutable) return value;
        if (plan.IsLeaf) return plan.ShallowClone(value);
        if (!plan.NeedsVisited)
        {
            if (plan.CustomCloner is not null)
                return plan.CustomCloner(value, visited);
            var copyFast = plan.ShallowClone(value);
            var fastFields = plan.MutableFields;
            for (var i = 0; i < fastFields.Length; i++)
            {
                var slot = fastFields[i];
                var fieldVal = slot.Getter(value);
                if (fieldVal is not null)
                {
                    var cloned = CloneSlotValue(slot, fieldVal, visited);
                    slot.Setter(copyFast, cloned);
                }
            }
            return copyFast;
        }

        visited ??= RentVisited();
        if (visited.TryGetValue(value, out var existing)) return existing;
        if (plan.CustomCloner is not null)
        {
            var custom = plan.CustomCloner(value, visited);
            visited[value] = custom;
            return custom;
        }

        var copy = plan.ShallowClone(value);
        visited[value] = copy;
        var fields = plan.MutableFields;
        for (var i = 0; i < fields.Length; i++)
        {
            var slot = fields[i];
            var fieldVal = slot.Getter(value);
            if (fieldVal is not null)
                slot.Setter(copy, CloneSlotValue(slot, fieldVal, visited));
        }
        return copy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object CloneSlotValue(FieldSlot slot, object fieldVal, Dictionary<object, object> visited)
    {
        if (slot.KnownType is null)
            return CloneObject(fieldVal, visited);
        var plan = slot.KnownPlan ??= PlanOf(slot.KnownType);
        return CloneWithPlan(fieldVal, plan, visited);
    }

    internal static bool IsImmutableType(Type type) =>
        ImmutableCache.GetOrAdd(type, static t => ComputeImmutable(t, null));

    private static bool ComputeImmutable(Type type, HashSet<Type> seenStructs)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
            type == typeof(Guid) || type == typeof(object))
            return true;

        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return ComputeImmutable(underlying, seenStructs);

        if (IsExcludedRuntimeType(type))
            return true;

        if (!type.IsValueType)
            return false;

        seenStructs ??= new HashSet<Type>();
        if (!seenStructs.Add(type)) return true;
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (field.IsStatic || field.IsLiteral || IsAlwaysIgnored(field) || field.IsDefined(typeof(NonSerializedAttribute), inherit: false))
                continue;
            if (!ComputeImmutable(field.FieldType, seenStructs))
                return false;
        }
        return true;
    }

    private static bool NeedsVisitedFor(Type type) =>
        CycleCache.GetOrAdd(type, static t => ComputeNeedsVisited(t, new HashSet<Type>()));

    private static bool ComputeNeedsVisited(Type type, HashSet<Type> stack)
    {
        if (IsImmutableType(type) || type == typeof(Breakdown) || type == typeof(DoctrineTrack))
            return false;
        if (!stack.Add(type))
            return true;
        try
        {
            if (type.IsArray)
                return ComputeNeedsVisited(type.GetElementType()!, stack);

            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();
                var args = type.GetGenericArguments();
                if (def == typeof(List<>) || def == typeof(HashSet<>))
                    return ComputeNeedsVisited(args[0], stack);
                if (def == typeof(Dictionary<,>))
                    return ComputeNeedsVisited(args[0], stack) || ComputeNeedsVisited(args[1], stack);
            }

            if (!type.IsValueType && !type.IsSealed)
                return true;

            var customConverter = type.IsDefined(typeof(JsonConverterAttribute), inherit: false);
            for (var current = type; current is not null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
            {
                var ignoredProps = new HashSet<string>(StringComparer.Ordinal);
                var serializedProps = new HashSet<string>(StringComparer.Ordinal);
                foreach (var prop in current.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (IsAlwaysIgnored(prop)) { ignoredProps.Add(prop.Name); continue; }
                    var getterPublic = prop.GetMethod?.IsPublic == true;
                    var hasInclude = prop.IsDefined(typeof(JsonIncludeAttribute), inherit: false);
                    if (getterPublic || hasInclude) serializedProps.Add(prop.Name);
                }
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || field.IsLiteral || IsAlwaysIgnored(field) || field.IsDefined(typeof(NonSerializedAttribute), inherit: false))
                        continue;
                    if (IsExcludedRuntimeType(field.FieldType)) continue;
                    var include = field.IsPublic || field.IsDefined(typeof(JsonIncludeAttribute), inherit: false);
                    if (!include && TryAutoPropertyName(field.Name, out var propName))
                    {
                        if (ignoredProps.Contains(propName)) continue;
                        include = serializedProps.Contains(propName);
                    }
                    else if (!include && customConverter)
                    {
                        include = !field.FieldType.IsPrimitive && field.FieldType != typeof(string);
                    }
                    if (!include || IsImmutableType(field.FieldType)) continue;
                    if (ComputeNeedsVisited(field.FieldType, stack))
                        return true;
                }
            }
            return false;
        }
        finally
        {
            stack.Remove(type);
        }
    }

    private static List<string> CloneStringList(List<string> list) =>
        list is null ? null : list.Count == 0 ? EmptyList<string>.Instance : new List<string>(list);

    private static DoctrineTrack CopyDoctrineTrackFields(DoctrineTrack src) => new()
    {
        Branch = src.Branch,
        Known = CloneStringList(src.Known),
        LearningBranch = src.LearningBranch,
        LearningNode = src.LearningNode,
        LearningDoneDay = src.LearningDoneDay,
        LastRewriteDay = src.LastRewriteDay
    };

    private static object CloneDoctrineTrack(object source, Dictionary<object, object> _)
    {
        var src = (DoctrineTrack)source;
        var copy = CopyDoctrineTrackFields(src);
        var companion = src.Companion;
        if (companion is null) return copy;
        if (ReferenceEquals(companion, src)) { copy.Companion = copy; return copy; }
        if (companion.Companion is null)
        {
            copy.Companion = CopyDoctrineTrackFields(companion);
            return copy;
        }
        var seen = new Dictionary<DoctrineTrack, DoctrineTrack>(ReferenceEqualityComparer.Instance) { [src] = copy };
        var curSrc = src;
        var curDst = copy;
        while (curSrc.Companion is { } next)
        {
            if (seen.TryGetValue(next, out var existing)) { curDst.Companion = existing; break; }
            var nextCopy = CopyDoctrineTrackFields(next);
            seen[next] = nextCopy;
            curDst.Companion = nextCopy;
            curSrc = next;
            curDst = nextCopy;
        }
        return copy;
    }

    private static TypePlan PlanOf(Type type) => Plans.GetOrAdd(type, BuildPlan);

    private static TypePlan BuildPlan(Type type)
    {
        if (IsImmutableType(type))
            return new TypePlan { IsImmutable = true };

        if (type == typeof(Breakdown))
        {
            return new TypePlan
            {
                NeedsVisited = false,
                CustomCloner = static (src, _) =>
                {
                    var b = (Breakdown)src;
                    var copy = new Breakdown();
                    copy.Restore(b.Title, b.Unit, b.Total, b.Entries);
                    return copy;
                }
            };
        }

        if (type == typeof(DoctrineTrack))
        {
            return new TypePlan
            {
                NeedsVisited = false,
                CustomCloner = CloneDoctrineTrack
            };
        }

        var needsVisited = NeedsVisitedFor(type);

        if (type.IsArray)
        {
            var elemType = type.GetElementType();
            var builder = typeof(SaveSnapshotCloner)
                .GetMethod(nameof(CreateArrayCloner), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(elemType);
            return new TypePlan
            {
                NeedsVisited = needsVisited,
                CustomCloner = (CloneMemberDelegate)builder.Invoke(null, null)!
            };
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (def == typeof(List<>))
            {
                var builder = typeof(SaveSnapshotCloner)
                    .GetMethod(nameof(CreateListCloner), BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(args[0]);
                return new TypePlan
                {
                    NeedsVisited = needsVisited,
                    CustomCloner = (CloneMemberDelegate)builder.Invoke(null, null)!
                };
            }
            if (def == typeof(Dictionary<,>))
            {
                var builder = typeof(SaveSnapshotCloner)
                    .GetMethod(nameof(CreateDictionaryCloner), BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(args[0], args[1]);
                return new TypePlan
                {
                    NeedsVisited = needsVisited,
                    CustomCloner = (CloneMemberDelegate)builder.Invoke(null, null)!
                };
            }
            if (def == typeof(HashSet<>))
            {
                var builder = typeof(SaveSnapshotCloner)
                    .GetMethod(nameof(CreateHashSetCloner), BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(args[0]);
                return new TypePlan
                {
                    NeedsVisited = needsVisited,
                    CustomCloner = (CloneMemberDelegate)builder.Invoke(null, null)!
                };
            }
        }

        var fields = CollectMutableFields(type);
        if (fields.Length == 0)
            return new TypePlan
            {
                IsLeaf = true,
                NeedsVisited = false,
                ShallowClone = TryCreateTypedLeafCloner(type) ?? ObjectMemberwiseClone
            };

        if (!needsVisited && !type.IsValueType && type.IsSealed)
        {
            var compiled = TryCreateCompiledDeepCloner(type, fields);
            if (compiled is not null)
                return new TypePlan
                {
                    NeedsVisited = false,
                    CustomCloner = compiled
                };
        }

        return new TypePlan
        {
            NeedsVisited = needsVisited,
            ShallowClone = ObjectMemberwiseClone,
            MutableFields = fields
        };
    }

    private static FieldSlot[] CollectMutableFields(Type type)
    {
        var slots = new List<FieldSlot>();
        var customConverter = type.IsDefined(typeof(JsonConverterAttribute), inherit: false);
        for (var current = type; current is not null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
        {
            var ignoredProps = new HashSet<string>(StringComparer.Ordinal);
            var serializedProps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in current.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (IsAlwaysIgnored(prop))
                {
                    ignoredProps.Add(prop.Name);
                    continue;
                }
                var getterPublic = prop.GetMethod?.IsPublic == true;
                var hasInclude = prop.IsDefined(typeof(JsonIncludeAttribute), inherit: false);
                if (getterPublic || hasInclude) serializedProps.Add(prop.Name);
            }

            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic || field.IsLiteral || IsAlwaysIgnored(field) || field.IsDefined(typeof(NonSerializedAttribute), inherit: false))
                    continue;
                if (IsExcludedRuntimeType(field.FieldType)) continue;

                var include = field.IsPublic || field.IsDefined(typeof(JsonIncludeAttribute), inherit: false);
                if (!include && TryAutoPropertyName(field.Name, out var propName))
                {
                    if (ignoredProps.Contains(propName)) continue;
                    include = serializedProps.Contains(propName);
                }
                else if (!include && customConverter)
                {
                    include = !field.FieldType.IsPrimitive && field.FieldType != typeof(string);
                }

                if (!include) continue;
                if (IsImmutableType(field.FieldType)) continue;

                slots.Add(new FieldSlot
                {
                    Field = field,
                    KnownType = field.FieldType.IsSealed ? field.FieldType : null,
                    Getter = CreateFieldGetter(field),
                    Setter = CreateFieldSetter(field)
                });
            }
        }
        return slots.ToArray();
    }

    private static bool TryAutoPropertyName(string fieldName, out string propName)
    {
        if (fieldName.Length > 17 && fieldName[0] == '<' && fieldName.EndsWith(">k__BackingField", StringComparison.Ordinal))
        {
            propName = fieldName.Substring(1, fieldName.Length - 17);
            return true;
        }
        propName = "";
        return false;
    }

    private static bool IsAlwaysIgnored(MemberInfo member)
    {
        var attr = member.GetCustomAttribute<JsonIgnoreAttribute>(inherit: false);
        return attr is not null && attr.Condition == JsonIgnoreCondition.Always;
    }

    private static bool IsExcludedRuntimeType(Type type) =>
        typeof(MemberInfo).IsAssignableFrom(type) ||
        typeof(Delegate).IsAssignableFrom(type) ||
        typeof(Campaign).IsAssignableFrom(type) ||
        typeof(ProvinceTopology).IsAssignableFrom(type) ||
        typeof(Stream).IsAssignableFrom(type);

    private static CloneMemberDelegate CreateListCloner<T>()
    {
        var immutable = IsImmutableType(typeof(T));
        var isSealed = typeof(T).IsSealed;
        TypePlan cachedPlan = null;
        return (src, visited) =>
        {
            var list = (List<T>)src;
            var count = list.Count;
            if (count == 0) return EmptyList<T>.Instance;
            if (immutable) return new List<T>(list);
            var copy = new List<T>(count);
            CollectionsMarshal.SetCount(copy, count);
            var srcSpan = CollectionsMarshal.AsSpan(list);
            var dstSpan = CollectionsMarshal.AsSpan(copy);
            if (isSealed)
            {
                var plan = cachedPlan ??= PlanOf(typeof(T));
                if (plan.IsLeaf)
                {
                    var shallow = plan.ShallowClone;
                    for (var i = 0; i < count; i++)
                    {
                        var item = srcSpan[i];
                        dstSpan[i] = item is null ? default! : (T)shallow(item);
                    }
                    return copy;
                }
                for (var i = 0; i < count; i++)
                {
                    var item = srcSpan[i];
                    dstSpan[i] = item is null ? default! : (T)CloneWithPlan(item, plan, visited);
                }
                return copy;
            }
            for (var i = 0; i < count; i++)
            {
                var item = srcSpan[i];
                dstSpan[i] = item is null ? default! : (T)CloneObject(item, visited);
            }
            return copy;
        };
    }

    private static CloneMemberDelegate CreateDictionaryCloner<TKey, TValue>() where TKey : notnull
    {
        var keyImmutable = IsImmutableType(typeof(TKey));
        var valueImmutable = IsImmutableType(typeof(TValue));
        var valueSealed = typeof(TValue).IsSealed;
        TypePlan cachedValuePlan = null;
        return (src, visited) =>
        {
            var dict = (Dictionary<TKey, TValue>)src;
            if (dict.Count == 0) return EmptyDict<TKey, TValue>.Instance;
            if (keyImmutable && valueImmutable) return new Dictionary<TKey, TValue>(dict, dict.Comparer);
            var copy = new Dictionary<TKey, TValue>(dict.Count, dict.Comparer);
            if (keyImmutable && valueSealed)
            {
                var valuePlan = cachedValuePlan ??= PlanOf(typeof(TValue));
                if (valuePlan.IsLeaf)
                {
                    var shallow = valuePlan.ShallowClone;
                    foreach (var pair in dict)
                        copy.Add(pair.Key, pair.Value is null ? default! : (TValue)shallow(pair.Value));
                    return copy;
                }
                foreach (var pair in dict)
                    copy.Add(pair.Key, pair.Value is null ? default! : (TValue)CloneWithPlan(pair.Value, valuePlan, visited));
                return copy;
            }
            foreach (var pair in dict)
            {
                var k = keyImmutable || pair.Key is null ? pair.Key : (TKey)CloneObject(pair.Key, visited);
                var v = valueImmutable || pair.Value is null ? pair.Value : (TValue)CloneObject(pair.Value, visited);
                copy.Add(k, v);
            }
            return copy;
        };
    }

    private static CloneMemberDelegate CreateHashSetCloner<T>()
    {
        var immutable = IsImmutableType(typeof(T));
        var isSealed = typeof(T).IsSealed;
        TypePlan cachedPlan = null;
        return (src, visited) =>
        {
            var set = (HashSet<T>)src;
            if (set.Count == 0) return EmptySet<T>.Instance;
            if (immutable) return new HashSet<T>(set, set.Comparer);
            var copy = new HashSet<T>(set.Count, set.Comparer);
            var plan = isSealed ? (cachedPlan ??= PlanOf(typeof(T))) : null;
            foreach (var item in set)
                copy.Add(item is null ? default! : plan is not null ? (T)CloneWithPlan(item, plan, visited) : (T)CloneObject(item, visited));
            return copy;
        };
    }

    private static CloneMemberDelegate CreateArrayCloner<T>()
    {
        var immutable = IsImmutableType(typeof(T));
        var isSealed = typeof(T).IsSealed;
        TypePlan cachedPlan = null;
        return (src, visited) =>
        {
            var arr = (T[])src;
            if (arr.Length == 0) return Array.Empty<T>();
            if (immutable) return (T[])arr.Clone();
            var copy = new T[arr.Length];
            var plan = isSealed ? (cachedPlan ??= PlanOf(typeof(T))) : null;
            if (plan is { IsLeaf: true })
            {
                var shallow = plan.ShallowClone;
                for (var i = 0; i < arr.Length; i++)
                {
                    var item = arr[i];
                    copy[i] = item is null ? default! : (T)shallow(item);
                }
                return copy;
            }
            for (var i = 0; i < arr.Length; i++)
            {
                var item = arr[i];
                copy[i] = item is null ? default! : plan is not null ? (T)CloneWithPlan(item, plan, visited) : (T)CloneObject(item, visited);
            }
            return copy;
        };
    }

    private static bool IsPureFieldInitCtor(Type type, out ConstructorInfo ctor)
    {
        ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);
        if (ctor is null || ctor.DeclaringType != type) return false;
        var body = ctor.GetMethodBody();
        if (body is null || body.LocalVariables.Count > 0 || body.ExceptionHandlingClauses.Count > 0) return false;
        var il = body.GetILAsByteArray();
        if (il is null || il.Length == 0) return false;
        var i = 0;
        while (i < il.Length)
        {
            var op = il[i++];
            switch (op)
            {
                case 0x00: // nop
                case 0x02: // ldarg.0
                case 0x14: // ldnull
                case 0x15: case 0x16: case 0x17: case 0x18: case 0x19:
                case 0x1A: case 0x1B: case 0x1C: case 0x1D: case 0x1E: // ldc.i4.m1..8
                case 0x25: // dup
                case 0x2A: // ret
                    break;
                case 0x1F: // ldc.i4.s
                    if (i + 1 > il.Length) return false;
                    i += 1;
                    break;
                case 0x20: // ldc.i4
                case 0x22: // ldc.r4
                case 0x72: // ldstr
                case 0x7D: // stfld
                    if (i + 4 > il.Length) return false;
                    i += 4;
                    break;
                case 0x21: // ldc.i8
                case 0x23: // ldc.r8
                    if (i + 8 > il.Length) return false;
                    i += 8;
                    break;
                case 0x28: // call
                    if (i + 4 > il.Length) return false;
                    var token = il[i] | (il[i + 1] << 8) | (il[i + 2] << 16) | (il[i + 3] << 24);
                    i += 4;
                    MethodBase called;
                    try { called = ctor.Module.ResolveMethod(token, type.IsGenericType ? type.GetGenericArguments() : null, null); }
                    catch { return false; }
                    if (called != ObjectCtor) return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static Func<object, object> TryCreateTypedLeafCloner(Type type)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported || type.IsValueType || !type.IsSealed) return null;
        if (!IsPureFieldInitCtor(type, out var ctor)) return null;
        var fields = new List<FieldInfo>();
        for (var cur = type; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var field in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!field.IsStatic && !field.IsLiteral) fields.Add(field);
        }
        var dm = new DynamicMethod("SaveClone_Leaf_" + type.Name, typeof(object),
            new[] { typeof(object) }, typeof(SaveSnapshotCloner).Module, skipVisibility: true);
        var il = dm.GetILGenerator();
        var srcLocal = il.DeclareLocal(type);
        var copyLocal = il.DeclareLocal(type);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, type);
        il.Emit(OpCodes.Stloc, srcLocal);
        il.Emit(OpCodes.Newobj, ctor);
        il.Emit(OpCodes.Stloc, copyLocal);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            il.Emit(OpCodes.Ldloc, copyLocal);
            il.Emit(OpCodes.Ldloc, srcLocal);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Stfld, field);
        }
        il.Emit(OpCodes.Ldloc, copyLocal);
        il.Emit(OpCodes.Ret);
        return (Func<object, object>)dm.CreateDelegate(typeof(Func<object, object>));
    }

    private static CloneMemberDelegate TryCreateCompiledDeepCloner(Type type, FieldSlot[] slots)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported || type.IsValueType || !type.IsSealed || slots.Length == 0)
            return null;
        for (var i = 0; i < slots.Length; i++)
            if (slots[i].Field is null || slots[i].Field.FieldType.IsValueType) return null;

        var targets = new object[slots.Length];
        var kinds = new byte[slots.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i].KnownType is { } kt)
            {
                var fp = PlanOf(kt);
                if (fp.IsLeaf) { kinds[i] = 1; targets[i] = fp.ShallowClone; }
                else if (fp.CustomCloner is not null) { kinds[i] = 2; targets[i] = fp.CustomCloner; }
            }
        }

        var dm = new DynamicMethod("SaveClone_Deep_" + type.Name, typeof(object),
            new[] { typeof(object[]), typeof(object), typeof(Dictionary<object, object>) },
            typeof(SaveSnapshotCloner).Module, skipVisibility: true);
        var il = dm.GetILGenerator();
        var srcLocal = il.DeclareLocal(type);
        var copyLocal = il.DeclareLocal(type);
        var tmpLocal = il.DeclareLocal(typeof(object));

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, type);
        il.Emit(OpCodes.Stloc, srcLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, ObjectMemberwiseCloneMethod);
        il.Emit(OpCodes.Castclass, type);
        il.Emit(OpCodes.Stloc, copyLocal);

        for (var i = 0; i < slots.Length; i++)
        {
            var field = slots[i].Field;
            var skip = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, srcLocal);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Stloc, tmpLocal);
            il.Emit(OpCodes.Ldloc, tmpLocal);
            il.Emit(OpCodes.Brfalse, skip);
            il.Emit(OpCodes.Ldloc, copyLocal);
            if (kinds[i] == 1)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Castclass, typeof(Func<object, object>));
                il.Emit(OpCodes.Ldloc, tmpLocal);
                il.Emit(OpCodes.Callvirt, FuncCloneInvoke);
            }
            else if (kinds[i] == 2)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Castclass, typeof(CloneMemberDelegate));
                il.Emit(OpCodes.Ldloc, tmpLocal);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, CloneMemberInvoke);
            }
            else
            {
                il.Emit(OpCodes.Ldloc, tmpLocal);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, CloneObjectMethod);
            }
            il.Emit(OpCodes.Castclass, field.FieldType);
            il.Emit(OpCodes.Stfld, field);
            il.MarkLabel(skip);
        }

        il.Emit(OpCodes.Ldloc, copyLocal);
        il.Emit(OpCodes.Ret);
        return (CloneMemberDelegate)dm.CreateDelegate(typeof(CloneMemberDelegate), targets);
    }

    private static Func<object, object> CreateMemberwiseClone()
    {
        var method = ObjectMemberwiseCloneMethod;
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            var dm = new DynamicMethod("SaveClone_MemberwiseClone", typeof(object), new[] { typeof(object) }, typeof(SaveSnapshotCloner).Module, skipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, method);
            il.Emit(OpCodes.Ret);
            return (Func<object, object>)dm.CreateDelegate(typeof(Func<object, object>));
        }
        return obj => method.Invoke(obj, null)!;
    }

    private static Func<object, object> CreateFieldGetter(FieldInfo field)
    {
        if (RuntimeFeature.IsDynamicCodeSupported && !field.DeclaringType!.IsValueType)
        {
            var dm = new DynamicMethod("SaveClone_Get_" + field.Name, typeof(object), new[] { typeof(object) }, typeof(SaveSnapshotCloner).Module, skipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            if (field.FieldType.IsValueType) il.Emit(OpCodes.Box, field.FieldType);
            il.Emit(OpCodes.Ret);
            return (Func<object, object>)dm.CreateDelegate(typeof(Func<object, object>));
        }
        return field.GetValue;
    }

    private static Action<object, object> CreateFieldSetter(FieldInfo field)
    {
        if (RuntimeFeature.IsDynamicCodeSupported && !field.DeclaringType!.IsValueType)
        {
            var dm = new DynamicMethod("SaveClone_Set_" + field.Name, typeof(void), new[] { typeof(object), typeof(object) }, typeof(SaveSnapshotCloner).Module, skipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldarg_1);
            if (field.FieldType.IsValueType) il.Emit(OpCodes.Unbox_Any, field.FieldType);
            else il.Emit(OpCodes.Castclass, field.FieldType);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<object, object>)dm.CreateDelegate(typeof(Action<object, object>));
        }
        return field.SetValue;
    }
}
