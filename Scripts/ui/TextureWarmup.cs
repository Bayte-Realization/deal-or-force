using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The interface's pictures, loaded before anybody asks for them.
///
/// Opening the trade screen the first time froze the game for one and a half seconds, and the
/// measurement put almost all of it on one line: GD.Load of thirty-nine commodity icons, 44 ms
/// each for a picture of 128 pixels - the cost of opening a file, not of drawing it. Every
/// screen that shows icons paid the same the first time (state affairs, science, diplomacy).
/// Once the campaign is built, every picture under the interface folders is requested from the
/// engine's loader threads, a few at a time, and kept, so the screens find them in the cache.
/// The main thread only polls; nothing here blocks a frame.
/// </summary>
public partial class TextureWarmup : Node
{
    private static readonly string[] Folders = { "res://game/gfx/interface/" };
    private const int DesktopInFlight = 6;
    private const int MobileInFlight = 2;
    private const int MobileKeptLimit = 48;

    private readonly Queue<string> _waiting = new();
    private readonly List<string> _loading = new();
    /// <summary>
    /// Held so desktop keeps warmed textures in cache; on mobile/touch bounded to a small FIFO ring
    /// (and released on low-memory compact budgets once warmup finishes) so RAM is never pinned.
    /// </summary>
    private readonly Queue<Resource> _kept = new();
    private ulong _started;
    private int _loadedCount;
    private double _delayRemaining;
    private bool _isMobile;
    private bool _compactBudget;

    public static void Start(Node owner) => StartDeferred(owner, delaySeconds: 0.0);

    /// <summary>
    /// Queues background interface texture warmup with an optional startup settle delay so
    /// boot/first-campaign frames stay free of threaded I/O contention. Safe to call repeatedly.
    /// </summary>
    public static void StartDeferred(Node owner, double delaySeconds = 0.75)
    {
        if (owner is null || owner.GetNodeOrNull("TextureWarmup") is not null) return;
        var profile = Platform.PlatformProfile.Current;
        var isMobile = profile?.IsTouchPrimary == true || OS.HasFeature("mobile") || OS.GetName() is "Android" or "iOS";
        var warmup = new TextureWarmup
        {
            Name = "TextureWarmup",
            _isMobile = isMobile,
            _compactBudget = profile?.ShouldUseCompactTextureBudget == true,
            _delayRemaining = Math.Max(0.0, isMobile ? Math.Max(delaySeconds, 1.0) : delaySeconds)
        };
        foreach (var name in SpriteCatalog.Names)
        {
            var path = SpriteCatalog.TextureOf(name);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("res://", StringComparison.Ordinal)) continue;
            foreach (var folder in Folders)
                if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) { warmup._waiting.Enqueue(path); break; }
        }
        if (warmup._waiting.Count == 0) { warmup.QueueFree(); return; }
        owner.AddChild(warmup);
    }

    public static void Stop(Node owner)
    {
        if (owner?.GetNodeOrNull<TextureWarmup>("TextureWarmup") is { } existing)
        {
            existing._waiting.Clear();
            existing._loading.Clear();
            existing._kept.Clear();
            existing.QueueFree();
        }
    }

    public override void _Ready() => _started = Time.GetTicksMsec();

    public override void _Process(double delta)
    {
        if (_delayRemaining > 0.0)
        {
            _delayRemaining -= double.IsFinite(delta) && delta > 0.0 ? delta : 0.0;
            if (_delayRemaining > 0.0) return;
            _started = Time.GetTicksMsec();
        }

        for (var index = _loading.Count - 1; index >= 0; index--)
        {
            var path = _loading[index];
            var status = ResourceLoader.LoadThreadedGetStatus(path);
            if (status == ResourceLoader.ThreadLoadStatus.InProgress) continue;
            if (status == ResourceLoader.ThreadLoadStatus.Loaded && ResourceLoader.LoadThreadedGet(path) is { } resource)
            {
                _loadedCount++;
                if (!_compactBudget)
                {
                    _kept.Enqueue(resource);
                    if (_isMobile && _kept.Count > MobileKeptLimit)
                        _kept.Dequeue();
                }
            }
            _loading.RemoveAt(index);
        }
        var maxInFlight = _isMobile ? MobileInFlight : DesktopInFlight;
        while (_loading.Count < maxInFlight && _waiting.Count > 0)
        {
            var path = _waiting.Dequeue();
            // Already loaded by a screen, or not a picture the engine knows: nothing to do.
            if (ResourceLoader.HasCached(path) || !ResourceLoader.Exists(path)) continue;
            if (ResourceLoader.LoadThreadedRequest(path, "Texture2D") == Error.Ok) _loading.Add(path);
        }
        if (_loading.Count > 0 || _waiting.Count > 0) return;
        if (_compactBudget) _kept.Clear();
        DiagnosticLogs.Write("performance", $"TEXTURE_WARMUP pictures={_loadedCount} kept={_kept.Count} mobile={_isMobile} ms={Time.GetTicksMsec() - _started}");
        SetProcess(false);
    }
}
