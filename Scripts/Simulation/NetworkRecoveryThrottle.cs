using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Bound expensive world recovery by real session time, never by the damaged world's date.
/// A paused or stuck client must be able to retry. The host enforces the same limit per
/// registered peer; trusting only a client's limiter lets one burst serialize many worlds.
/// This is transient transport state, not part of a campaign, save or fingerprint.
/// </summary>
public sealed class NetworkRecoveryThrottle
{
    public const double RetrySeconds = 10;
    private readonly Dictionary<long, double> _accepted = new();
    public int Count => _accepted.Count;

    public bool TryAcquire(long peer, double sessionSeconds)
    {
        if (peer <= 0 || !double.IsFinite(sessionSeconds) || sessionSeconds < 0) return false;
        if (_accepted.TryGetValue(peer, out var previous) && sessionSeconds - previous < RetrySeconds) return false;
        _accepted[peer] = sessionSeconds;
        return true;
    }

    public void Forget(long peer) => _accepted.Remove(peer);
    public void Clear() => _accepted.Clear();
}
