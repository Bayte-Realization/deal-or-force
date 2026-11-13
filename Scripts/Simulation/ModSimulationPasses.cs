using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

/// <summary>Stable core-code extension points. These are per simulation STEP, not per
/// day or rendered frame. Keep the existing TickHours-based rates inside a replacement.</summary>
public enum SimulationPass { DiplomaticActs, Logistics, Warfare, Construction }

/// <summary>Replace one existing pass, or decorate it by calling original once, synchronously.
/// Not calling original suppresses that built-in pass. Never retain the continuation.</summary>
public delegate void SimulationPassReplacement(Campaign campaign, Action original);

/// <summary>
/// Campaign-scoped code hooks, not serialized simulation state or a second simulation loop.
/// No registration means the exact built-in call. One owner per pass: conflicting mods
/// fail explicitly instead of silently replacing each other in discovery order.
/// Trusted compiled mods still own their balance, thread safety and determinism.
/// </summary>
public static class ModSimulationPasses
{
    // Weak keys prevent a static hook table from retaining an abandoned campaign.
    // Explicit lease disposal also drops delegates before collectible DLLs are unloaded.
    private static readonly ConditionalWeakTable<Campaign, Registry> Registries = new();
    private sealed class Registry
    {
        internal readonly Dictionary<SimulationPass, Lease> Entries = new();
    }

    private sealed class Lease : IDisposable
    {
        internal readonly string Owner;
        internal SimulationPassReplacement Handler;
        internal bool Running;
        private readonly Registry _registry;
        private readonly SimulationPass _pass;
        internal Lease(Registry registry, SimulationPass pass, string owner, SimulationPassReplacement handler)
        { _registry = registry; _pass = pass; Owner = owner; Handler = handler; }
        public void Dispose()
        {
            if (_registry.Entries.TryGetValue(_pass, out var current) && ReferenceEquals(current, this))
                _registry.Entries.Remove(_pass);
            Handler = null;
        }
    }

    /// <summary>Read-only diagnostic. Empty means the built-in pass owns execution.</summary>
    public static string Owner(Campaign campaign, SimulationPass pass) => campaign is not null &&
        Registries.TryGetValue(campaign, out var registry) && registry.Entries.TryGetValue(pass, out var entry)
            ? entry.Owner : "";

    internal static IDisposable Register(Campaign campaign, SimulationPass pass, string owner, SimulationPassReplacement handler)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(handler);
        if (!Enum.IsDefined(pass)) throw new ArgumentOutOfRangeException(nameof(pass));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("A replacement needs its package id.", nameof(owner));
        if (campaign.MidStep) throw new InvalidOperationException("Register simulation passes during mod loading, not halfway through a step.");
        var registry = Registries.GetOrCreateValue(campaign);
        if (registry.Entries.TryGetValue(pass, out var previous))
            throw new InvalidOperationException($"Simulation pass {pass} already belongs to '{previous.Owner}'; '{owner}' was not installed.");
        var lease = new Lease(registry, pass, owner, handler);
        registry.Entries.Add(pass, lease);
        return lease;
    }

    internal static void Run(Campaign campaign, SimulationPass pass, Action<Campaign> builtIn)
    {
        if (!Registries.TryGetValue(campaign, out var registry) || !registry.Entries.TryGetValue(pass, out var entry))
        { builtIn(campaign); return; }
        if (entry.Running) throw new InvalidOperationException($"Recursive simulation pass {pass} in mod '{entry.Owner}'.");
        var handler = entry.Handler;
        var thread = Environment.CurrentManagedThreadId;
        var active = true;
        var called = false;
        entry.Running = true;
        try
        {
            handler(campaign, () =>
            {
                if (!active) throw new InvalidOperationException("A simulation continuation cannot escape its pass.");
                if (Environment.CurrentManagedThreadId != thread)
                    throw new InvalidOperationException("A simulation continuation must stay on the simulation thread.");
                if (called) throw new InvalidOperationException("A built-in simulation pass may only run once.");
                called = true;
                builtIn(campaign);
            });
        }
        catch (Exception error)
        {
            // Do NOT fall back after a callback has already written state or called
            // original: running the built-in again could pay costs/move units twice.
            // Propagate to the campaign's normal crash envelope, naming the package.
            throw new InvalidOperationException($"Simulation pass {pass} failed in mod '{entry.Owner}'.", error);
        }
        finally { active = false; entry.Running = false; }
    }

    /// <summary>
    /// The staged twin of <see cref="Run"/>: with no mod on the pass the built-in stages
    /// yield to the caller's frame budget; with a mod installed the pass still runs as one
    /// atomic continuation exactly as before, because a mod's Action cannot suspend.
    /// </summary>
    internal static IEnumerable<int> RunStaged(Campaign campaign, SimulationPass pass,
        Func<Campaign, IEnumerable<int>> builtInStages)
    {
        if (!Registries.TryGetValue(campaign, out var registry) || !registry.Entries.TryGetValue(pass, out _))
        {
            foreach (var stage in builtInStages(campaign)) yield return stage;
            yield break;
        }
        Run(campaign, pass, c => { foreach (var _ in builtInStages(c)) { } });
        yield return 0;
    }
}
