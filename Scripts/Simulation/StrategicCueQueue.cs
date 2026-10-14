using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>Bounded, presentation-only FIFO. Its dispatch policy is shared by native audio
/// and engine-neutral regression fixtures; no campaign state or clock is consumed.</summary>
public sealed class StrategicCueQueue
{
    public const int Limit = 3;
    public readonly record struct ChannelState(string Name, int Priority, bool Independent, bool Playing);
    private readonly Queue<string> _pending = new();
    public int Count => _pending.Count;
    public string Head => _pending.Count > 0 ? _pending.Peek() : null;
    public bool Enqueue(string name)
    {
        if (string.IsNullOrEmpty(name) || _pending.Count >= Limit || _pending.Contains(name)) return false;
        _pending.Enqueue(name); return true;
    }
    public void Clear() => _pending.Clear();

    public bool TryDispatch(Func<string, SoundCatalog.Cue?> resolve, IReadOnlyList<ChannelState> channels,
        Action<SoundCatalog.Cue> play)
    {
        while (_pending.Count > 0)
        {
            var cue = resolve(_pending.Peek());
            if (cue is null) { _pending.Dequeue(); continue; }
            ChannelState? target = null;
            foreach (var channel in channels)
                if (string.Equals(channel.Name, cue.Value.Channel, StringComparison.OrdinalIgnoreCase))
                { target = channel; break; }
            if (target is null) { _pending.Dequeue(); continue; }
            if (target.Value.Playing) return false;
            foreach (var channel in channels)
                if (channel.Playing && !channel.Independent &&
                    (cue.Value.Yielding || channel.Priority > target.Value.Priority)) return false;
            _pending.Dequeue(); play(cue.Value); return true;
        }
        return false;
    }
}
