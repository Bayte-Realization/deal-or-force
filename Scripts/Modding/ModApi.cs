using System;
using System.Globalization;
using System.Reflection;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce.Modding;

/// <summary>
/// Narrow Godot-facing bridge used by GDScript mods. Methods intentionally exchange
/// primitive values and Dictionaries so a mod author needs no knowledge of C# types.
/// </summary>
public partial class ModApi : RefCounted
{
    private Campaign _campaign;
    private readonly Action<string> _logger;

    public ModApi(Action<string> logger) => _logger = logger;

    internal void Bind(Campaign campaign) => _campaign = campaign;

    public int GetDay() => _campaign?.ElapsedDays ?? 0;
    public string GetDate() => _campaign?.Date.ToString() ?? "No campaign";
    public void Log(string message) => _logger?.Invoke(message);

    // Explicit snake_case aliases form the stable GDScript ABI. Godot keeps C#
    // method spelling when the object is passed across the language boundary.
    public int get_day() => GetDay();
    public string get_date() => GetDate();
    public void log(string message) => Log(message);

    public Godot.Collections.Dictionary GetNation(string tag)
    {
        var result = new Godot.Collections.Dictionary();
        var nation = _campaign?.NationByTag(tag);
        if (nation is null) return result;

        result["id"] = nation.Id;
        result["tag"] = nation.Tag;
        result["name"] = nation.Name;
        result["population"] = nation.Population;
        result["treasury"] = nation.Treasury;
        result["gdp"] = nation.Gdp;
        result["stability"] = nation.Stability;
        result["war_support"] = nation.WarSupport;
        result["manpower"] = nation.ManpowerReserve;
        return result;
    }

    public Godot.Collections.Dictionary get_nation(string tag) => GetNation(tag);

    public bool SetNationNumber(string tag, string key, double value)
    {
        var nation = _campaign?.NationByTag(tag);
        if (nation is null || !float.IsFinite((float)value)) return false;
        var number = (float)value;

        switch (Normalize(key))
        {
            case "population": nation.Population = Math.Max(0L, (long)value); break;
            case "treasury": nation.Treasury = number; break;
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

    public bool set_nation_number(string tag, string key, double value) => SetNationNumber(tag, key, value);

    public double GetDefine(string name, double fallback = 0)
    {
        if (_campaign is null) return fallback;
        var property = FindDefine(name);
        if (property is null) return fallback;
        var value = property.GetValue(_campaign.Defines);
        return value is IConvertible convertible
            ? convertible.ToDouble(CultureInfo.InvariantCulture)
            : fallback;
    }

    public double get_define(string name, double fallback = 0) => GetDefine(name, fallback);

    public bool SetDefine(string name, double value)
    {
        if (_campaign is null || !double.IsFinite(value)) return false;
        var property = FindDefine(name);
        if (property is null || !property.CanWrite) return false;
        try
        {
            property.SetValue(_campaign.Defines, Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool set_define(string name, double value) => SetDefine(name, value);

    private PropertyInfo FindDefine(string name)
    {
        var normalized = Normalize(name);
        foreach (var property in typeof(Defines).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            if (Normalize(property.Name) == normalized)
                return property;
        return null;
    }

    private static string Normalize(string value) =>
        (value ?? "").Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
}
