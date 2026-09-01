using System;
using ByDealOrForce.Simulation;

namespace ByDealOrForce.Modding;

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
    public int Day => Campaign?.ElapsedDays ?? 0;
    public string Date => Campaign?.Date.ToString() ?? "No campaign";

    internal ModContext(string modId, Action<string> logger)
    {
        ModId = modId;
        _logger = logger;
    }

    public Nation Nation(string tag) => Campaign?.NationByTag(tag);
    public void Queue(ICampaignCommand command) => Campaign?.Commands.Enqueue(command);
    public void Log(string message) => _logger?.Invoke(message);
}
