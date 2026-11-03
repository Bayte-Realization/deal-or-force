using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce.Modding;

/// <summary>
/// Bridge a GDScript mod actually calls. Methods exchange primitives and Godot
/// dictionaries so the script never needs a C# type. Luau receives the same methods
/// as callables from <see cref="EngineScriptMod.CreateApi"/>, not this object.
///
/// A script may read the campaign and may queue an action the game already knows
/// (a policy, a debt order, a doctrine, a diplomatic act, a feature switch). It may
/// not invent a second treasury, a second war, or a second battle. Those already
/// live in the economy, diplomacy, and <c>Warfare</c>. A tax the budget screen sets
/// is <c>TaxRate</c> and the budget line; a scripted stand-in is undone the same day
/// the player moves that slider.
///
/// <c>get_nation</c>'s <c>id</c> is <c>Campaign.NationById</c>: the slot in
/// <c>Nations</c>, assigned when the country is appended. It is not the map-table
/// row and not the tag. Keep tags. A new id space does not survive a save.
///
/// New campaign state is saved and mixed into <c>CampaignFingerprint</c> only when
/// it is not the default, so a campaign that never touched the field still matches
/// one from before the field existed. Reading a screen does not change that hash.
/// Land held by a human is not taken without their click: a deal, an ultimatum, or
/// a peace aimed at them waits (<c>They answer for themselves</c>). Writing province
/// ownership from a script skips that click.
///
/// Money on this bridge is billions of the game's currency. Shares such as
/// stability and war support are 0 to 1. Days are campaign days, not frame seconds.
/// </summary>
public partial class ModApi : RefCounted
{
    private Campaign _campaign;
    private readonly Action<string> _logger;

    public ModApi(Action<string> logger) => _logger = logger;

    internal void Bind(Campaign campaign) => _campaign = campaign;

    /// <summary>Campaign days elapsed. 0 before a campaign is bound, and while paused the day does not advance.</summary>
    public int GetDay() => _campaign?.ElapsedDays ?? 0;

    /// <summary>The campaign calendar, or "No campaign" when nothing is bound.</summary>
    public string GetDate() => _campaign?.Date.ToString() ?? "No campaign";

    /// <summary>One line in the mod log, prefixed with the package id by the loader. Does not change the campaign.</summary>
    public void Log(string message) => _logger?.Invoke(message);

    // Explicit snake_case aliases form the stable GDScript ABI. Godot keeps C#
    // method spelling when the object is passed across the language boundary.
    // A new hook is invisible to scripts until it exists here, in get_capabilities,
    // and in EngineScriptMod.CreateApi. Worker mods use a different command list.
    public int get_day() => GetDay();
    public string get_date() => GetDate();
    public void log(string message) => Log(message);

    /// <summary>
    /// Whether a mechanic switch is on for the whole campaign, not for one country.
    /// Unknown ids are off. Built-ins missing from an old save stay on.
    /// </summary>
    public bool feature_enabled(string id) => _campaign?.FeatureEnabled(id) == true;

    // These are Godot-callable names, not the differently named worker commands.
    public string[] get_capabilities() => new[]
    {
        "get_day", "get_date", "log", "feature_enabled", "set_feature",
        "get_province_terrain", "get_nation", "set_nation_number", "get_politics",
        "enact_policy", "manage_debt", "add_course_shock", "study_doctrine", "propose_diplomatic_act",
        "get_define", "set_define"
    };

    /// <summary>
    /// Turns a mechanic switch on or off for every country. This writes campaign state
    /// and the fingerprint. It does not delete armies, buildings, or laws that already exist.
    /// A custom id does nothing until some system asks <c>FeatureEnabled</c> for it.
    /// </summary>
    public bool set_feature(string id, bool enabled) => _campaign?.SetFeature(id, enabled) == true;

    /// <summary>
    /// Read-only terrain of one province. <paramref name="provinceId"/> is the topology id
    /// (1 .. ProvinceCount), not a country id. An unknown id returns an empty dictionary.
    /// </summary>
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

    /// <summary>
    /// One country by tag (or ISO). Empty when the tag is unknown. <c>id</c> is the
    /// <c>NationById</c> slot. Treasury and GDP are billions. Population and manpower are people.
    /// </summary>
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

    /// <summary>
    /// Immediate write of one national number. Not a purchase: no price, no cooldown,
    /// no deal. Prefer <c>enact_policy</c> and the other queued calls for anything the
    /// player could also do from a screen. See <see cref="ModScalarAccess"/>.
    /// </summary>
    public bool SetNationNumber(string tag, string key, double value) =>
        ModScalarAccess.SetNationNumber(_campaign, tag, key, value);

    public bool set_nation_number(string tag, string key, double value) => SetNationNumber(tag, key, value);

    /// <summary>
    /// Read-only politics: course, convictions, enacted laws, and the current promise.
    /// Law and course ids come from <c>common/</c>, not from translated button captions.
    /// </summary>
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
    /// <summary>
    /// Queue an existing political policy or promise. True means the request was accepted
    /// into the command queue, not that the treasury has already paid. <paramref name="id"/>
    /// is the catalogue id. <paramref name="promise"/> selects an agenda rather than a law.
    /// </summary>
    public bool enact_policy(string tag, string id, string targetTag = "", int region = 0, bool promise = false) =>
        ModOperations.QueuePolicy(_campaign, tag, id, targetTag, region, promise);

    /// <summary>
    /// Queue borrow, repay, or restructure. Requires the <c>public_debt</c> switch.
    /// Amount is billions. True means queued. The command still rechecks the books in Apply.
    /// </summary>
    public bool manage_debt(string tag, string action, double amount = 0) =>
        ModOperations.QueueDebt(_campaign, tag, action, amount);

    /// <summary>
    /// Queue a shock to the national course, strength in (0, 1]. Single-player only.
    /// This is not a second ideology system; it feeds <c>NationalCourses</c>.
    /// </summary>
    public bool add_course_shock(string tag, double strength, string reason)
    {
        return ModOperations.QueueCourseShock(_campaign, tag, strength, reason);
    }

    /// <summary>
    /// Queue study of one doctrine node. Branch and node ids come from <c>common/</c>.
    /// The same refusal the doctrine screen would show rejects the queue.
    /// </summary>
    public bool study_doctrine(string tag, string branchId, string nodeId, bool rush = false) =>
        ModOperations.QueueStudyDoctrine(_campaign, tag, branchId, nodeId, rush);

    /// <summary>
    /// Queue a proposal of an existing diplomatic act. <paramref name="actName"/> is the
    /// enum name with optional underscores (<c>state_visit</c>), not a translated label.
    /// A human target still answers for themselves when the act reaches them.
    /// </summary>
    public bool propose_diplomatic_act(string issuerTag, string targetTag, string actName) =>
        ModOperations.QueueDiplomaticAct(_campaign, issuerTag, targetTag, actName);

    /// <summary>
    /// Read one numeric define on the live campaign. A name the game does not have returns
    /// <paramref name="fallback"/>. This does not read the JSON file; overlays have already been applied.
    /// </summary>
    public double GetDefine(string name, double fallback = 0) =>
        ModScalarAccess.GetDefine(_campaign?.Defines, name, fallback);

    public double get_define(string name, double fallback = 0) => GetDefine(name, fallback);

    /// <summary>
    /// Overwrite one numeric define for this running campaign only. It does not edit
    /// <c>common/defines</c> and it does not carry into the next campaign. A balance change
    /// that should ship belongs in a data overlay, which is reapplied when the campaign loads.
    /// </summary>
    public bool SetDefine(string name, double value) =>
        ModScalarAccess.SetDefine(_campaign?.Defines, name, value);

    public bool set_define(string name, double value) => SetDefine(name, value);

}
