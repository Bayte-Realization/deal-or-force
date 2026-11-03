using System;
using DealOrForce.Simulation;

namespace DealOrForce.Modding;

/// <summary>
/// Entry point a compiled desktop mod implements. C# and F# both use this interface.
/// The assembly is named in <c>corecode_assembly</c> (preferred) or the older
/// <c>csharp_assembly</c>, and <see cref="ModLoader"/> constructs each concrete type.
///
/// The contract stays free of Godot scene types so a gameplay mod can be tested without
/// the engine. Extend a system the campaign already ticks (economy, war, diplomacy,
/// a catalogue under <c>common/</c>). A second economy, a second battle resolver, or a
/// tax the budget screen can set back the same day will fight the game instead of
/// extending it. New state is saved and mixed into <c>CampaignFingerprint</c> only when
/// it is not the default. Reading a screen must not change that hash.
///
/// <see cref="ModContext.Nation"/> returns the live country. Its <c>Id</c> is the
/// <c>NationById</c> list slot, not a map index and not a tag. Do not keep the reference
/// across campaigns, and do not write it from a background thread. Land a human holds
/// is not taken without their click.
/// </summary>
public interface IGlobalityMod
{
    // Историческое имя — часть публичного контракта скомпилированных модов.
    // Переименование интерфейса ради косметики сломает уже собранные сборки.

    /// <summary>Once, when the assembly is accepted. The campaign may already be bound. Do not tick the world from here.</summary>
    void OnLoad(ModContext context) { }

    /// <summary>Once per campaign, after data overlays have been applied and the world exists. Queue setup here, do not invent a parallel start-of-game.</summary>
    void OnCampaignCreated(ModContext context) { }

    /// <summary>After each advanced campaign day, on the simulation thread. Queue commands; do not apply them yourself a second time.</summary>
    void OnDayAdvanced(ModContext context) { }

    /// <summary>UI-thread heartbeat for local adapters, including paused campaigns. Must not change the fingerprint: a paint is not a turn.</summary>
    void OnFrame(ModContext context, double delta) { }
}

// Coordinates are BTA planet coordinates, including global Y across storage slices.
// Block zero records a dug cell; color is RGB and metadata preserves block variants.
// Flat Earth adds the canonical top-cell (x,z) footprint. Excavated remembers a
// player removal even when it was filled before the next network snapshot.
// A crossover mod presents voxels the game already dug. It does not keep a second map of who owns the province.
public sealed record CrossoverVoxel(int x, int y, int z, int block, int metadata, int color, float[] pose = null, bool flora = false, bool town = false, bool terrain = false, int[] surface = null, bool excavated = false);

/// <summary>
/// What a compiled mod is allowed to touch for one package. <see cref="Queue"/> and the
/// <c>Enact*</c> helpers only enqueue. The command's <c>Apply</c> still checks issuer,
/// price, and target. <see cref="ModScalarAccess"/> is the immediate path and is not this.
/// </summary>
public sealed class ModContext
{
    private readonly Action<string> _logger;

    public string ModId { get; }
    public Campaign Campaign { get; internal set; }
    internal ModFrontendActionCatalog.Registration FrontendActions { get; set; }
    // Optional main-thread snapshot of the game's own rendered map (DFT1 + RGBA8).
    // Null unless the presentation layer attached it. Do not call off the main thread.
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

    // Nation(...) возвращает живой объект доверенному .NET-моду, не защитный snapshot.
    // Не сохраняйте эту ссылку между кампаниями и не меняйте её из фонового потока.
    /// <summary>Live country by tag, or null. <c>Id</c> is the <c>NationById</c> list index.</summary>
    public Nation Nation(string tag) => Campaign?.NationByTag(tag);

    /// <summary>
    /// Bind a handler the package already declared in <c>frontend_actions</c>.
    /// An id the manifest did not name is refused. The handler runs on the UI thread when the player picks that action.
    /// </summary>
    public bool RegisterFrontendAction(string id, Func<string> handler) => FrontendActions?.Register(id, handler) ?? false;

    /// <summary>Queues intent only; the game applies it at its command boundary.
    /// The command must still validate issuer, current price and target in Apply.
    /// Do not call <c>Apply</c> yourself beside this: a traced command already applies once.</summary>
    public void Queue(ICampaignCommand command) => Campaign?.Commands.Enqueue(command);

    /// <summary>Queue an existing policy or promise. True means queued, not paid. See <see cref="ModOperations.QueuePolicy"/>.</summary>
    public bool EnactPolicy(string issuerTag, string policyId, string targetTag = "", int region = 0, bool promise = false) =>
        ModOperations.QueuePolicy(Campaign, issuerTag, policyId, targetTag, region, promise);

    /// <summary>Queue borrow, repay, or restructure on the existing debt books. True means queued.</summary>
    public bool ManageDebt(string issuerTag, string action, double amount = 0) =>
        ModOperations.QueueDebt(Campaign, issuerTag, action, amount);

    /// <summary>Queue a national-course shock, strength in (0, 1]. Single-player only. Feeds the existing course, not a new ideology.</summary>
    public bool AddCourseShock(string issuerTag, double strength, string reason) =>
        ModOperations.QueueCourseShock(Campaign, issuerTag, strength, reason);

    /// <summary>Queue study of a doctrine node by catalogue id. Same price and refusal as the doctrine screen.</summary>
    public bool StudyDoctrine(string issuerTag, string branchId, string nodeId, bool rush = false) =>
        ModOperations.QueueStudyDoctrine(Campaign, issuerTag, branchId, nodeId, rush);

    /// <summary>Queue a proposal of an existing diplomatic act. A human target still answers on their own click.</summary>
    public bool ProposeDiplomaticAct(string issuerTag, string targetTag, string actName) =>
        ModOperations.QueueDiplomaticAct(Campaign, issuerTag, targetTag, actName);

    /// <summary>Mod log line. Does not change the campaign.</summary>
    public void Log(string message) => _logger?.Invoke(message);
}
