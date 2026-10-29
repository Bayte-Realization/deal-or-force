using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DealOrForce.Simulation;

namespace DealOrForce.Modding;

/// <summary>Primitive numeric access shared by the Godot script bridge and its tests.
/// These legacy trusted-mod setters remain immediate, not queued commands.</summary>
public static class ModScalarAccess
{
    private static readonly Dictionary<string, PropertyInfo> DefineProperties = IndexDefines();

    public static bool SetNationNumber(Campaign campaign, string tag, string key, double value)
    {
        var nation = campaign?.NationByTag(tag);
        if (nation is null || !float.IsFinite((float)value)) return false;
        var number = (float)value;
        switch (Normalize(key))
        {
            case "population":
                // (double)long.MaxValue rounds up to 2^63, which is already outside
                // Int64. Check that exact exclusive bound before the truncating cast.
                if (value >= 9223372036854775808d) return false;
                nation.Population = value <= 0 ? 0 : (long)value;
                break;
            case "treasury": nation.Treasury = number; break;
            case "publicdebt": nation.PublicDebt = Math.Max(0, number); break;
            case "debtarrears": nation.DebtArrears = Math.Max(0, number); break;
            case "paymentarrears": nation.PaymentArrears = Math.Max(0, number); break;
            case "creditconfidence": nation.CreditConfidence = Math.Clamp(number, 0, 1); break;
            case "gdp": nation.Gdp = Math.Max(0f, number); break;
            case "militaryspending": nation.MilitarySpending = Math.Clamp(number, 0f, 1f); break;
            case "taxrate": nation.TaxRate = Math.Clamp(number, 0f, 1f); break;
            case "stability": nation.Stability = Math.Clamp(number, 0f, 1f); break;
            case "warsupport": nation.WarSupport = Math.Clamp(number, 0f, 1f); break;
            case "manpower": nation.ManpowerReserve = Math.Max(0f, number); break;
            default: return false;
        }
        return true;
    }

    public static double GetDefine(Defines defines, string name, double fallback = 0)
    {
        if (defines is null) return fallback;
        var property = FindDefine(name);
        if (property is null || !NumericType(property.PropertyType)) return fallback;
        var value = property.GetValue(defines);
        if (value is not IConvertible convertible) return fallback;
        var number = convertible.ToDouble(CultureInfo.InvariantCulture);
        return double.IsFinite(number) ? number : fallback;
    }

    public static bool SetDefine(Defines defines, string name, double value)
    {
        if (defines is null) return false;
        var property = FindDefine(name);
        if (property is null || !property.CanWrite ||
            !TryConvertScalar(value, property.PropertyType, out var scalar)) return false;
        property.SetValue(defines, scalar);
        return true;
    }

    /// <summary>Preserves Convert.ChangeType integer rounding and bool semantics,
    /// but rejects unsupported types and float overflow before any state is written.</summary>
    public static bool TryConvertScalar(double value, Type type, out object converted)
    {
        converted = null;
        if (!double.IsFinite(value) || !NumericType(type)) return false;
        try
        {
            var result = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            if (result is float single && !float.IsFinite(single)) return false;
            converted = result;
            return true;
        }
        catch (OverflowException) { return false; }
    }

    private static bool NumericType(Type type) => type is not null && !type.IsEnum && Type.GetTypeCode(type) is
        TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or
        TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or
        TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    private static PropertyInfo FindDefine(string name) => DefineProperties.GetValueOrDefault(Normalize(name));

    private static Dictionary<string, PropertyInfo> IndexDefines()
    {
        var result = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var property in typeof(Defines).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            result.TryAdd(Normalize(property.Name), property);
        return result;
    }

    private static string Normalize(string value) =>
        (value ?? "").Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
}
