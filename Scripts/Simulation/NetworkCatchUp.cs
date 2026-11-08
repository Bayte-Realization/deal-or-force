using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Reliable clock targets, not simulation work in an RPC. Adjacent targets with the same
/// step share one segment; changes of step remain ordered. ElapsedHours can already name
/// the new hour while StepFor is unfinished, so a segment may retire only at a stable boundary.
/// Transient client transport state: never saved and never part of the world's fingerprint.
/// </summary>
public sealed class NetworkCatchUp
{
    public readonly record struct Target(long Hours, int StepHours);
    private sealed class Segment(long hours, int stepHours)
    {
        public long Hours = hours;
        public readonly int StepHours = stepHours;
    }
    public const int MaximumSegments = 256;
    private readonly Queue<Segment> _segments = new();
    private Segment _tail;
    public long LatestHours { get; private set; } = -1;
    public int Count => _segments.Count;

    /// <summary>False means malformed/unreachable targets or excessive cadence changes: recover a whole world.</summary>
    public bool Offer(long hostHours, int stepHours, long now)
    {
        if (hostHours < 0 || now < 0 || stepHours is < 1 or > 240) return false;
        var previous = Math.Max(now, LatestHours);
        if (hostHours <= previous) return true; // duplicate/obsolete target, never rewind
        if ((hostHours - previous) % stepHours != 0) return false;
        if (_tail is not null && _tail.StepHours == stepHours) _tail.Hours = hostHours;
        else
        {
            if (_segments.Count >= MaximumSegments) return false;
            _tail = new Segment(hostHours, stepHours);
            _segments.Enqueue(_tail);
        }
        LatestHours = hostHours;
        return true;
    }

    public bool TryNext(long now, bool midStep, out Target target)
    {
        while (!midStep && _segments.TryPeek(out var done) && done.Hours <= now) _segments.Dequeue();
        if (_segments.TryPeek(out var next))
        {
            target = new Target(next.Hours, next.StepHours);
            return true;
        }
        _tail = null;
        target = default;
        return false;
    }

    public bool NeedsSnapshot(long now, int maximumGapDays)
        => now >= 0 && LatestHours > now && LatestHours - now >= Math.Max(1L, maximumGapDays) * 24L;

    public static double FrameBudget(double configured)
        => double.IsFinite(configured) && configured >= 0 ? configured : 9;

    public void Clear()
    {
        _segments.Clear();
        _segments.TrimExcess();
        _tail = null;
        LatestHours = -1;
    }
}
