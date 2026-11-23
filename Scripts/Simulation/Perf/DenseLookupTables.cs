using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

/// <summary>
/// Dense, zero-allocation integer-indexed lookup primitives for hot simulation inner loops.
///
/// Most entity identifiers in Deal or Force (<see cref="Nation.Id"/>, <see cref="Nation.MapIndex"/>,
/// <see cref="ScenarioState.MapIndex"/>, province IDs) are small contiguous non-negative integers.
/// Indexing directly into a flat array eliminates dictionary hash computation, bucket probing,
/// and pointer chasing across millions of hourly and daily evaluations, while falling back
/// cleanly to a sparse dictionary only when a mod or synthetic test supplies an unusually high key.
/// </summary>
internal sealed class DenseIntTable<T> where T : class
{
    private const int MaxDenseKey = 65536;

    private T[] _dense = Array.Empty<T>();
    private Dictionary<int, T> _overflow;

    public void Reset(int maxKnownKey)
    {
        var desired = maxKnownKey >= 0 && maxKnownKey < MaxDenseKey ? maxKnownKey + 1 : 0;
        if (desired > _dense.Length)
            _dense = new T[Math.Min(MaxDenseKey, Math.Max(desired, _dense.Length * 2 + 16))];
        else if (_dense.Length > 0)
            Array.Clear(_dense, 0, _dense.Length);
        _overflow?.Clear();
    }

    public void TryAdd(int key, T value)
    {
        if ((uint)key < (uint)_dense.Length)
        {
            _dense[key] ??= value;
            return;
        }
        if (key >= 0 && key < MaxDenseKey)
        {
            var nextSize = Math.Min(MaxDenseKey, Math.Max(key + 1, _dense.Length * 2 + 16));
            Array.Resize(ref _dense, nextSize);
            _dense[key] ??= value;
            return;
        }
        _overflow ??= new Dictionary<int, T>();
        _overflow.TryAdd(key, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T GetOrDefault(int key)
    {
        var dense = _dense;
        if ((uint)key < (uint)dense.Length) return dense[key];
        return _overflow is not null && _overflow.TryGetValue(key, out var value) ? value : null;
    }
}

/// <summary>
/// O(1)-reset membership set for dense integer keys (provinces, nations, states).
///
/// Incrementing the generation counter clears the entire set without touching memory;
/// the backing array is only zeroed once every 2^31 resets.
/// </summary>
internal sealed class GenerationalStampSet
{
    private int[] _stamps = Array.Empty<int>();
    private int _generation = 1;

    public void EnsureCapacity(int maxKeyInclusive)
    {
        if (maxKeyInclusive < 0) return;
        if (maxKeyInclusive >= _stamps.Length)
            _stamps = new int[Math.Max(maxKeyInclusive + 1, _stamps.Length * 2 + 64)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        if (_generation == int.MaxValue)
        {
            Array.Clear(_stamps, 0, _stamps.Length);
            _generation = 1;
        }
        else
        {
            _generation++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(int key) =>
        (uint)key < (uint)_stamps.Length && _stamps[key] == _generation;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(int key)
    {
        if (key < 0) return false;
        if (key >= _stamps.Length) EnsureCapacity(key);
        if (_stamps[key] == _generation) return false;
        _stamps[key] = _generation;
        return true;
    }
}

/// <summary>
/// Reusable dense float accumulator indexed by entity ID (e.g., per-nation hourly weights)
/// with generational clearing so unused slots never require scanning or hashing.
/// </summary>
internal sealed class ScratchFloatMap
{
    private float[] _values = Array.Empty<float>();
    private int[] _stamps = Array.Empty<int>();
    private int _generation = 1;

    public void BeginPass(int capacityHint)
    {
        if (capacityHint > _values.Length)
        {
            var next = Math.Max(capacityHint, _values.Length * 2 + 32);
            _values = new float[next];
            _stamps = new int[next];
            _generation = 1;
            return;
        }
        if (_generation == int.MaxValue)
        {
            Array.Clear(_stamps, 0, _stamps.Length);
            _generation = 1;
        }
        else
        {
            _generation++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int id, float delta)
    {
        if (id < 0) return;
        if (id >= _values.Length)
        {
            var next = Math.Max(id + 1, _values.Length * 2 + 32);
            Array.Resize(ref _values, next);
            Array.Resize(ref _stamps, next);
        }
        if (_stamps[id] != _generation)
        {
            _stamps[id] = _generation;
            _values[id] = delta;
        }
        else
        {
            _values[id] += delta;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float GetOrDefault(int id, float fallback = 0f) =>
        (uint)id < (uint)_stamps.Length && _stamps[id] == _generation ? _values[id] : fallback;
}
