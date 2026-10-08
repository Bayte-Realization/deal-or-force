using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;

namespace DealOrForce.Simulation;

/// <summary>
/// A save writes what is not already true of a fresh object.
///
/// Nine tenths of a century-long save was the same nothing written forty thousand times: a
/// relation between Tuvalu and Paraguay carries fifty fields, and between most pairs of
/// countries every one of them is still what a new relation starts with - no pact, no
/// embargo, no grievance, no truce. Written out, that was 55 MB of zeros and empty lists in a
/// 247 MB file.
///
/// So a member is left out when its value is exactly what a newly constructed object of the
/// type holds - not what the CLR default is: a field that starts at -1 is written when it is
/// 0, because nation 0 is Indonesia. Loading populates a fresh object, so a member left out
/// comes back as that same value, and the campaign is the one that was saved, field for
/// field. A member whose fresh value is not fixed (two fresh objects disagree - an id handed
/// out by a counter) is always written, as is anything that is not a plain value or an empty
/// collection.
/// </summary>
internal static class SaveDefaults
{
    private static readonly ConcurrentDictionary<Type, bool> Constructible = new();

    /// <summary>
    /// Members never left out. The history, which the save splices into its own empty field at
    /// the end of the file; and anything that says what an object IS - an id, a tag, a name -
    /// because a hand-edited save is read by a person looking for Indonesia, whose id is 0,
    /// and a row that does not say which country it is cannot be found or edited.
    /// </summary>
    private static bool Pinned(JsonTypeInfo info, JsonPropertyInfo property) =>
        info.Type.Name == "CampaignSave" && property.Name == "History" ||
        property.Name.EndsWith("Id", StringComparison.Ordinal) || property.Name.EndsWith("Tag", StringComparison.Ordinal) ||
        property.Name is "Name" or "A" or "B" or "Kind" or "Day";

    /// <summary>How many members of how many types are left out when unchanged (for the bench).</summary>
    public static int Types, Members;

    /// <summary>DOF_SAVE_KEEP_DEFAULTS=1 writes every member, for comparing the two.</summary>
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("DOF_SAVE_KEEP_DEFAULTS") != "1";

    public static void Modify(JsonTypeInfo info)
    {
        if (!Enabled) return;
        if (info.Kind != JsonTypeInfoKind.Object || info.Type.IsValueType || info.Type.IsAbstract) return;
        if (!TryCreate(info, out var first) || !TryCreate(info, out var second)) return;
        Types++;
        foreach (var property in info.Properties)
        {
            if (property.Get is null || property.Set is null && !property.PropertyType.IsAssignableTo(typeof(ICollection))) continue;
            if (Pinned(info, property)) continue;
            object a, b;
            try { a = property.Get(first); b = property.Get(second); }
            catch { continue; }
            if (!Plain(a) || !Same(a, b)) continue;
            var fresh = a;
            Members++;
            var previous = property.ShouldSerialize;
            property.ShouldSerialize = (owner, value) =>
                !Same(value, fresh) && (previous is null || previous(owner, value));
        }
    }

    private static bool TryCreate(JsonTypeInfo info, out object instance)
    {
        instance = null;
        if (Constructible.TryGetValue(info.Type, out var known) && !known) return false;
        try
        {
            instance = info.CreateObject?.Invoke() ?? Activator.CreateInstance(info.Type, nonPublic: true);
            Constructible[info.Type] = instance is not null;
            return instance is not null;
        }
        catch
        {
            Constructible[info.Type] = false;
            return false;
        }
    }

    /// <summary>The values that can be compared for "unchanged": numbers, flags, text, enums,
    /// nothing at all, and empty collections.</summary>
    private static bool Plain(object value) => value switch
    {
        null => true,
        string => true,
        Enum => true,
        ICollection collection => collection.Count == 0,
        _ => value.GetType().IsPrimitive || value is decimal
    };

    private static bool Same(object value, object fresh)
    {
        if (value is null || fresh is null) return value is null && fresh is null;
        if (value is ICollection left && fresh is ICollection right)
            return left.Count == 0 && right.Count == 0 && value.GetType() == fresh.GetType();
        return value.GetType() == fresh.GetType() && value.Equals(fresh);
    }
}
