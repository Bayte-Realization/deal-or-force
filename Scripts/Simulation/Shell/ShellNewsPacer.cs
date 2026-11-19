using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation.Shell;

public enum ShellNewsPriority
{
    Routine = 0,
    Notable = 1,
    Crisis = 2,
    War = 3,
    Urgent = 4
}

public sealed class ShellNewsItem
{
    public NewsFeed.Line Line { get; }
    public string ReasonKey { get; }
    public ShellNewsPriority Priority { get; }
    public bool Urgent { get; }
    public double Age { get; internal set; }

    public long Sequence => Line?.Sequence ?? 0;

    public ShellNewsItem(NewsFeed.Line line, string reasonKey, ShellNewsPriority priority, bool urgent)
    {
        Line = line ?? throw new ArgumentNullException(nameof(line));
        ReasonKey = string.IsNullOrEmpty(reasonKey) ? "Dispatch" : reasonKey;
        Priority = priority;
        Urgent = urgent;
    }
}

public readonly record struct ShellNewsStep(
    ShellNewsItem Current,
    bool Changed,
    int QueuedCount,
    bool Frozen);

/// <summary>
/// Pure real-time news clipping pacer (engine-free): enforces minimum dwell before
/// routine headline swaps, bounded priority queue keeping War/Crisis/Urgent dispatches over
/// routine fluff, max lifetime, hover/pin age freeze, and audio cue cooldown throttling.
/// </summary>
public sealed class ShellNewsPacer
{
    public const float DefaultMinDwellSec = 2.8f;
    public const float DefaultLifetimeSec = 9.0f;
    public const float DefaultAudioCooldownSec = 3.5f;
    public const float DefaultCooldownSec = 1.2f;
    public const int DefaultMaxQueue = 6;

    public static float MinDwellSec =>
        Math.Clamp(InterfaceSkin.Metric("shell.news_min_dwell_sec", DefaultMinDwellSec), 0.1f, 30f);

    public static float LifetimeSec =>
        Math.Clamp(InterfaceSkin.Metric("shell.news_lifetime_sec", DefaultLifetimeSec), 1f, 60f);

    public static float AudioCooldownSec =>
        Math.Clamp(InterfaceSkin.Metric("shell.news_audio_cooldown_sec", DefaultAudioCooldownSec), 0f, 30f);

    public static float CooldownSec =>
        Math.Clamp(InterfaceSkin.Metric("shell.news_cooldown_sec", DefaultCooldownSec), 0f, 15f);

    public static int MaxQueue =>
        Math.Clamp((int)MathF.Round(InterfaceSkin.Metric("shell.news_max_queue", DefaultMaxQueue)), 1, 32);

    private readonly List<ShellNewsItem> _queue = new();
    private ShellNewsItem _active;
    private bool _pinned;
    private bool _dirtyActivated;
    private double _dismissCooldownRemaining;
    private double _lastAudioCueSec = double.NegativeInfinity;
    private bool _lastAudioCueUrgent;

    public ShellNewsItem Active => _active;
    public int QueuedCount => _queue.Count;
    public bool IsPinned => _pinned;
    public IReadOnlyList<ShellNewsItem> PendingQueue => _queue;

    public static bool IsUrgentKind(string kind)
    {
        var k = (kind ?? "").Trim().ToLowerInvariant();
        return k is "nuclear" or "nuclear_strike" or "war" or "war_declared" or "invasion"
            or "ultimatum" or "demand" or "peace" or "peace_treaty" or "truce"
            or "defcon" or "nuclear_alert" or "encirclement";
    }

    public static ShellNewsPriority ClassifyPriority(NewsFeed.Line line, bool urgent, bool involvesPlayer, int importance = -1)
    {
        if (urgent) return ShellNewsPriority.Urgent;
        var kind = (line?.Kind ?? "").Trim().ToLowerInvariant();
        if (kind is "nuclear" or "nuclear_strike") return ShellNewsPriority.Urgent;
        if (kind is "war" or "war_declared" or "invasion" or "peace" or "peace_treaty" or "truce" or "encirclement")
            return ShellNewsPriority.War;
        if (kind is "ultimatum" or "demand" or "defcon" or "nuclear_alert" or "crisis" or "coup" or "rebellion")
            return ShellNewsPriority.Crisis;
        var imp = importance >= 0 ? importance : NewsFeed.Importance(line?.Kind);
        if (imp >= 3) return ShellNewsPriority.Crisis;
        if (imp >= 2 || involvesPlayer) return ShellNewsPriority.Notable;
        return ShellNewsPriority.Routine;
    }

    public void SetPinned(bool pinned)
    {
        if (_active is null)
        {
            _pinned = false;
            return;
        }
        _pinned = pinned;
    }

    public bool TogglePinned()
    {
        if (_active is null)
        {
            _pinned = false;
            return false;
        }
        _pinned = !_pinned;
        return _pinned;
    }

    public void Dismiss(bool clearQueue = false)
    {
        _active = null;
        _pinned = false;
        _dirtyActivated = false;
        if (clearQueue)
        {
            _queue.Clear();
            _dismissCooldownRemaining = 0.0;
        }
        else if (_queue.Count > 0)
        {
            _dismissCooldownRemaining = CooldownSec;
        }
    }

    public void Reset()
    {
        _active = null;
        _queue.Clear();
        _pinned = false;
        _dirtyActivated = false;
        _dismissCooldownRemaining = 0.0;
        _lastAudioCueSec = double.NegativeInfinity;
        _lastAudioCueUrgent = false;
    }

    public bool Offer(NewsFeed.Line line, string reasonKey, bool urgent = false, bool involvesPlayer = false)
    {
        var prio = ClassifyPriority(line, urgent, involvesPlayer);
        return Offer(line, reasonKey, prio, urgent);
    }

    public bool Offer(NewsFeed.Line line, string reasonKey, ShellNewsPriority priority, bool urgent)
    {
        if (line is null) return false;
        if (_active is not null && _active.Sequence == line.Sequence) return false;
        for (var i = 0; i < _queue.Count; i++)
            if (_queue[i].Sequence == line.Sequence)
                return false;

        var item = new ShellNewsItem(line, reasonKey, priority, urgent);

        if (_active is null)
        {
            if (_dismissCooldownRemaining <= 0.0)
            {
                _active = item;
                _pinned = false;
                _dirtyActivated = true;
            }
            else
            {
                EnqueueBounded(item);
            }
            return true;
        }

        // Preempt synchronously only if current card hasn't been ticked/presented yet and incoming is higher priority;
        // live urgent preemption waits for Tick() so cursor hover / pin freezes are respected cleanly.
        if (!_pinned && _dirtyActivated && _active.Age <= 0.0001 && CompareImportance(item, _active) > 0)
        {
            var displaced = _active;
            displaced.Age = 0.0;
            _active = item;
            _dirtyActivated = true;
            EnqueueBounded(displaced);
            return true;
        }

        EnqueueBounded(item);
        return true;
    }

    public ShellNewsStep Tick(double delta, bool hovered, bool paused = false)
    {
        var dt = double.IsFinite(delta) && delta > 0.0 ? delta : 0.0;
        var changed = _dirtyActivated;
        _dirtyActivated = false;

        if (_active is null)
        {
            if (_dismissCooldownRemaining > 0.0)
                _dismissCooldownRemaining = Math.Max(0.0, _dismissCooldownRemaining - dt);

            if (_dismissCooldownRemaining <= 0.0 && _queue.Count > 0)
            {
                _active = PopBestQueued();
                _active.Age = 0.0;
                _pinned = false;
                changed = true;
            }
            return new ShellNewsStep(_active, changed, _queue.Count, Frozen: false);
        }

        var readingFreeze = hovered || _pinned;
        var frozen = readingFreeze || paused;
        // Urgent dispatches preempt routine cards when not hovered/pinned (including on auto-pause frames),
        // without advancing card age while paused.
        if (!readingFreeze && !_active.Urgent && _queue.Count > 0 && PeekBestQueued() is { Urgent: true })
        {
            var displaced = _active;
            displaced.Age = 0.0;
            _active = PopBestQueued();
            _active.Age = 0.0;
            _pinned = false;
            EnqueueBounded(displaced);
            return new ShellNewsStep(_active, Changed: true, _queue.Count, Frozen: frozen);
        }

        if (!frozen)
        {
            _active.Age += dt;
            var minDwell = (double)MinDwellSec;
            var lifetime = Math.Max(minDwell, (double)LifetimeSec);

            if (_queue.Count > 0 && _active.Age >= minDwell)
            {
                var best = PeekBestQueued();
                if (!_active.Urgent || best.Urgent || best.Priority >= _active.Priority)
                {
                    _active = PopBestQueued();
                    _active.Age = 0.0;
                    _pinned = false;
                    return new ShellNewsStep(_active, Changed: true, _queue.Count, Frozen: false);
                }
            }

            if (_active.Age >= lifetime)
            {
                if (_queue.Count > 0)
                {
                    _active = PopBestQueued();
                    _active.Age = 0.0;
                    _pinned = false;
                }
                else
                {
                    _active = null;
                    _pinned = false;
                }
                changed = true;
            }
        }

        return new ShellNewsStep(_active, changed, _queue.Count, frozen);
    }

    public bool ShouldPlayNewsCue(double elapsedSec, bool urgent)
    {
        if (!double.IsFinite(elapsedSec)) return false;
        if (elapsedSec < _lastAudioCueSec)
        {
            _lastAudioCueSec = double.NegativeInfinity;
            _lastAudioCueUrgent = false;
        }

        var cooldown = (double)AudioCooldownSec;
        if (!urgent)
        {
            if (elapsedSec - _lastAudioCueSec < cooldown) return false;
        }
        else if (_lastAudioCueUrgent)
        {
            var burstGuard = Math.Min(cooldown, 1.0);
            if (elapsedSec - _lastAudioCueSec < burstGuard) return false;
        }

        _lastAudioCueSec = elapsedSec;
        _lastAudioCueUrgent = urgent;
        return true;
    }

    private void EnqueueBounded(ShellNewsItem item)
    {
        var cap = MaxQueue;
        if (_queue.Count < cap)
        {
            _queue.Add(item);
            return;
        }

        var victimIdx = 0;
        for (var i = 1; i < _queue.Count; i++)
        {
            if (CompareImportance(_queue[i], _queue[victimIdx]) < 0)
                victimIdx = i;
        }

        var victim = _queue[victimIdx];
        if (item.Priority > victim.Priority ||
            (item.Priority == victim.Priority && item.Urgent && !victim.Urgent) ||
            (item.Priority == victim.Priority && item.Urgent == victim.Urgent && item.Sequence > victim.Sequence))
        {
            _queue[victimIdx] = item;
        }
    }

    private ShellNewsItem PeekBestQueued()
    {
        if (_queue.Count == 0) return null;
        var best = _queue[0];
        for (var i = 1; i < _queue.Count; i++)
        {
            if (CompareDispatchOrder(_queue[i], best) > 0)
                best = _queue[i];
        }
        return best;
    }

    private ShellNewsItem PopBestQueued()
    {
        var bestIdx = 0;
        for (var i = 1; i < _queue.Count; i++)
        {
            if (CompareDispatchOrder(_queue[i], _queue[bestIdx]) > 0)
                bestIdx = i;
        }
        var item = _queue[bestIdx];
        _queue.RemoveAt(bestIdx);
        return item;
    }

    private static int CompareImportance(ShellNewsItem a, ShellNewsItem b)
    {
        var prio = a.Priority.CompareTo(b.Priority);
        if (prio != 0) return prio;
        var urg = a.Urgent.CompareTo(b.Urgent);
        if (urg != 0) return urg;
        return a.Sequence.CompareTo(b.Sequence);
    }

    // Dequeue highest priority first; within same priority tier read chronologically (oldest sequence first).
    private static int CompareDispatchOrder(ShellNewsItem a, ShellNewsItem b)
    {
        var prio = a.Priority.CompareTo(b.Priority);
        if (prio != 0) return prio;
        var urg = a.Urgent.CompareTo(b.Urgent);
        if (urg != 0) return urg;
        return b.Sequence.CompareTo(a.Sequence);
    }
}
