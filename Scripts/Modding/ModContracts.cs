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
    // UI-thread heartbeat for local adapters, including paused campaigns.
    void OnFrame(ModContext context, double delta) { }
}

// Coordinates are BTA planet coordinates, including global Y across storage slices.
// Block zero records a dug cell; color is RGB and metadata preserves block variants.
// Flat Earth adds the canonical top-cell (x,z) footprint. Excavated remembers a
// player removal even when it was filled before the next network snapshot.
public sealed record CrossoverVoxel(int x, int y, int z, int block, int metadata, int color, float[] pose = null, bool flora = false, bool town = false, bool terrain = false, int[] surface = null, bool excavated = false);

public sealed class ModContext
{
    private readonly Action<string> _logger;

    public string ModId { get; }
    public Campaign Campaign { get; internal set; }
    internal ModFrontendActionCatalog.Registration FrontendActions { get; set; }
    // Optional main-thread snapshot of the game's own rendered map (DFT1 + RGBA8).
    public Func<byte[]> CaptureSurface { get; internal set; }
    public Func<byte[]> CaptureHydrology { get; internal set; }
    public Action<CrossoverVoxel[]> PresentVoxels { get; internal set; }
    public Action<CrossoverWork[]> PresentWorks { get; internal set; }
    // Optional main-thread presentation hook; mod load/disposal owns its graphics layers.
    public Action<bool> SetCrossoverPresentation { get; internal set; }
    public int Day => Campaign?.ElapsedDays ?? 0;
    public string Date => Campaign?.Date.ToString() ?? "No campaign";

    internal ModContext(string modId, Action<string> logger)
    {
        ModId = modId;
        _logger = logger;
    }

    public Nation Nation(string tag) => Campaign?.NationByTag(tag);
    /// <summary>Explicitly selected, declared, package-owned frontend actions.</summary>
    public bool RegisterFrontendAction(string id, Func<string> handler) => FrontendActions?.Register(id, handler) ?? false;
    public void Queue(ICampaignCommand command) => Campaign?.Commands.Enqueue(command);
    public bool EnactPolicy(string issuerTag, string policyId, string targetTag = "", int region = 0, bool promise = false) =>
        ModOperations.QueuePolicy(Campaign, issuerTag, policyId, targetTag, region, promise);
    public bool ManageDebt(string issuerTag, string action, double amount = 0) =>
        ModOperations.QueueDebt(Campaign, issuerTag, action, amount);
    public bool AddCourseShock(string issuerTag, double strength, string reason) =>
        ModOperations.QueueCourseShock(Campaign, issuerTag, strength, reason);
    public bool StudyDoctrine(string issuerTag, string branchId, string nodeId, bool rush = false) =>
        ModOperations.QueueStudyDoctrine(Campaign, issuerTag, branchId, nodeId, rush);
    public bool ProposeDiplomaticAct(string issuerTag, string targetTag, string actName) =>
        ModOperations.QueueDiplomaticAct(Campaign, issuerTag, targetTag, actName);
    public void Log(string message) => _logger?.Invoke(message);
}
