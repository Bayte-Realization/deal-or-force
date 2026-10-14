using System;
using DealOrForce.Simulation;

namespace DealOrForce.Modding;

/// <summary>
/// Stable, deliberately small entry point for compiled desktop mods. A mod assembly
/// references the game's managed assembly and implements this interface. Keeping the
/// contract free of Godot types also makes gameplay mods straightforward to test.
/// </summary>
public interface IGlobalityMod
{
    void OnLoad(ModContext context) { }
    void OnCampaignCreated(ModContext context) { }
    void OnDayAdvanced(ModContext context) { }
}

public sealed class ModContext
{
    private readonly Action<string> _logger;

    public string ModId { get; }
    public Campaign Campaign { get; internal set; }
    internal ModFrontendActionCatalog.Registration FrontendActions { get; set; }
    public int Day => Campaign?.ElapsedDays ?? 0;
    public string Date => Campaign?.Date.ToString() ?? "No campaign";

    internal ModContext(string modId, Action<string> logger)
    {
        ModId = modId;
        _logger = logger;
    }

    public Nation Nation(string tag) => Campaign?.NationByTag(tag);
    /// <summary>Register a declared local action for this loaded package; never executed by discovery.</summary>
    public bool RegisterFrontendAction(string id, Func<string> handler) => FrontendActions?.Register(id, handler) ?? false;
    public void Queue(ICampaignCommand command) => Campaign?.Commands.Enqueue(command);
    public bool EnactPolicy(string issuerTag, string policyId, string targetTag = "", int region = 0, bool promise = false) =>
        ModOperations.QueuePolicy(Campaign, issuerTag, policyId, targetTag, region, promise);
    public bool ManageDebt(string issuerTag, string action, double amount = 0) =>
        ModOperations.QueueDebt(Campaign, issuerTag, action, amount);
    public bool AddCourseShock(string issuerTag, double strength, string reason) =>
        ModOperations.QueueCourseShock(Campaign, issuerTag, strength, reason);
    public void Log(string message) => _logger?.Invoke(message);
}
