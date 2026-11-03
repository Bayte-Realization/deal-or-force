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
    private const int InFlight = 8;

    private readonly Queue<string> _waiting = new();
    private readonly List<string> _loading = new();
    /// <summary>Held so the cache keeps them: the engine forgets a resource nobody references.</summary>
    private readonly List<Resource> _kept = new();
    private ulong _started;

    public static void Start(Node owner)
    {
        if (owner is null || owner.GetNodeOrNull("TextureWarmup") is not null) return;
        var warmup = new TextureWarmup { Name = "TextureWarmup" };
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

    public override void _Ready() => _started = Time.GetTicksMsec();

    public override void _Process(double delta)
    {
        for (var index = _loading.Count - 1; index >= 0; index--)
        {
            var path = _loading[index];
            var status = ResourceLoader.LoadThreadedGetStatus(path);
            if (status == ResourceLoader.ThreadLoadStatus.InProgress) continue;
            if (status == ResourceLoader.ThreadLoadStatus.Loaded && ResourceLoader.LoadThreadedGet(path) is { } resource)
                _kept.Add(resource);
            _loading.RemoveAt(index);
        }
        while (_loading.Count < InFlight && _waiting.Count > 0)
        {
            var path = _waiting.Dequeue();
            // Already loaded by a screen, or not a picture the engine knows: nothing to do.
            if (ResourceLoader.HasCached(path) || !ResourceLoader.Exists(path)) continue;
            if (ResourceLoader.LoadThreadedRequest(path, "Texture2D") == Error.Ok) _loading.Add(path);
        }
        if (_loading.Count > 0 || _waiting.Count > 0) return;
        DiagnosticLogs.Write("performance", $"TEXTURE_WARMUP pictures={_kept.Count} ms={Time.GetTicksMsec() - _started}");
        SetProcess(false);
    }
}
