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
    public bool feature_enabled(string id) => _campaign?.FeatureEnabled(id) == true;
    // These are Godot-callable names, not the differently named worker commands.
    public string[] get_capabilities() => new[]
    {
        "get_day", "get_date", "log", "feature_enabled", "set_feature",
        "get_province_terrain", "get_nation", "set_nation_number", "get_politics",
        "enact_policy", "manage_debt", "add_course_shock", "get_define", "set_define"
    };
    public bool set_feature(string id, bool enabled) => _campaign?.SetFeature(id, enabled) == true;

    public Godot.Collections.Dictionary get_province_terrain(int provinceId)
    {
        var result = new Godot.Collections.Dictionary();
        var topology = _campaign?.ProvinceMap;
        if (topology is null || provinceId <= 0 || provinceId > topology.ProvinceCount) return result;
        var terrain = topology.TerrainAt(provinceId);
        result["id"] = terrain.Id;
        result["name"] = terrain.Name;
        result["water"] = topology.IsWater(provinceId);
        result["movement_speed"] = terrain.MovementSpeed;
        result["attack"] = terrain.Attack;
        result["defense"] = terrain.Defense;
        result["combat_width"] = terrain.CombatWidth;
        result["supply"] = terrain.Supply;
        return result;
    }

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
        result["public_debt"] = nation.PublicDebt;
        result["debt_arrears"] = nation.DebtArrears;
        result["payment_arrears"] = nation.PaymentArrears;
        result["credit_confidence"] = nation.CreditConfidence;
        result["debt_condition"] = nation.DebtCondition.ToString();
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

    public bool set_nation_number(string tag, string key, double value) => SetNationNumber(tag, key, value);

    public Godot.Collections.Dictionary get_politics(string tag)
    {
        var result = new Godot.Collections.Dictionary();
        if (_campaign?.NationByTag(tag) is not { } n) return result;
        var course = n.NationalCourse;
        var convictions = new Godot.Collections.Dictionary();
        foreach (var p in course.Convictions) convictions[p.Key] = p.Value;
        result["course"] = course.Dominant; result["course_initialized"] = course.Initialized;
        result["inertia"] = course.Entrenchment; result["shock"] = course.Shock;
        result["convictions"] = convictions;
        var laws = new Godot.Collections.Array();
        foreach (var p in n.Laws)
            laws.Add(new Godot.Collections.Dictionary { ["slot"] = p.Key, ["id"] = p.Value.Id,
                ["target_tag"] = p.Value.TargetTag, ["region"] = p.Value.Region });
        result["laws"] = laws;
        result["promise"] = n.Agenda?.Id ?? "";
        result["promise_completed"] = n.Agenda?.Completed ?? false;
        result["promise_succeeded"] = n.Agenda?.Succeeded ?? false;
        return result;
    }

    // Trusted code mods use exactly the same prices, territory checks and cooldowns as the UI.
    public bool enact_policy(string tag, string id, string targetTag = "", int region = 0, bool promise = false) =>
        ModOperations.QueuePolicy(_campaign, tag, id, targetTag, region, promise);

    public bool manage_debt(string tag, string action, double amount = 0) =>
        ModOperations.QueueDebt(_campaign, tag, action, amount);

    public bool add_course_shock(string tag, double strength, string reason)
    {
        return ModOperations.QueueCourseShock(_campaign, tag, strength, reason);
    }

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
