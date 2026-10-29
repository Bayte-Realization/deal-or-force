using System;
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
        "enact_policy", "manage_debt", "add_course_shock", "study_doctrine", "propose_diplomatic_act",
        "get_define", "set_define"
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

    public bool SetNationNumber(string tag, string key, double value) =>
        ModScalarAccess.SetNationNumber(_campaign, tag, key, value);

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

    public bool study_doctrine(string tag, string branchId, string nodeId, bool rush = false) =>
        ModOperations.QueueStudyDoctrine(_campaign, tag, branchId, nodeId, rush);

    public bool propose_diplomatic_act(string issuerTag, string targetTag, string actName) =>
        ModOperations.QueueDiplomaticAct(_campaign, issuerTag, targetTag, actName);

    public double GetDefine(string name, double fallback = 0) =>
        ModScalarAccess.GetDefine(_campaign?.Defines, name, fallback);

    public double get_define(string name, double fallback = 0) => GetDefine(name, fallback);

    public bool SetDefine(string name, double value) =>
        ModScalarAccess.SetDefine(_campaign?.Defines, name, value);

    public bool set_define(string name, double value) => SetDefine(name, value);

}
