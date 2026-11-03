using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DealOrForce.Simulation;

namespace DealOrForce.Modding;

/// <summary>
/// Immediate numeric reads and writes shared by the script bridge and its tests.
/// This is the old trusted path. It does not queue a command, pay a price, or ask the
/// player. Prefer <see cref="ModOperations"/> for anything a screen can also do.
/// A direct write is not multiplayer-safe just because it sits behind an API, and it
/// does not retune regional cohorts when national population changes.
/// Keys ignore underscores and hyphens: <c>war_support</c> and <c>warsupport</c> are the same field.
/// Shares are clamped to 0..1. Treasury and GDP are billions and are not clamped to a share.
/// <see cref="SetDefine"/> changes the live campaign only. It does not rewrite <c>common/defines</c>.
/// </summary>
public static class ModScalarAccess
{
    // Совместимость со старыми доверенными скриптами, НЕ обычный игровой приказ.
    // Прямой setter не проводит сделку, не платит цену и не становится безопасным
    // для сетевой игры автоматически. Для игровых действий используйте ModOperations.
    private static readonly Dictionary<string, PropertyInfo> DefineProperties = IndexDefines();

    /// <summary>
    /// Write one national aggregate by tag. Returns false for an unknown tag, a non-finite value,
    /// or a key this list does not name. Population does not rebuild regional cohorts.
    /// Setting <c>taxrate</c> writes the same <c>TaxRate</c> the budget screen writes, so a second tax will not stick.
    /// </summary>
    public static bool SetNationNumber(Campaign campaign, string tag, string key, double value)
    {
        var nation = campaign?.NationByTag(tag);
        if (nation is null || !float.IsFinite((float)value)) return false;
        var number = (float)value;
        switch (Normalize(key))
        {
            case "population":
                // Меняется национальный агрегат, не перепись когорт во всех регионах.
                // Для демографического контента задавайте профили/историю регионов;
                // этот низкоуровневый setter сам их согласовывать не умеет.
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

    /// <summary>Read one numeric define already loaded into this campaign. Unknown or non-numeric names return <paramref name="fallback"/>.</summary>
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

    /// <summary>
    /// Overwrite one numeric define in memory. Bool defines accept 0 and 1 via the usual conversion.
    /// There is no new define: a name <c>Defines</c> does not already have is refused.
    /// A lasting balance change belongs in <c>common/defines</c>, which the loader reapplies next campaign.
    /// </summary>
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
