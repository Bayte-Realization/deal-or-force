using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>One authoritative, freshly serialized snapshot per fan-out, not per peer.</summary>
public static class NetworkSnapshotDelivery
{
    public static void Send(IEnumerable<long> peers, Func<byte[]> prepare, Action<long, byte[]> deliver)
    {
        var recipients = peers.Where(id => id > 1).Distinct().OrderBy(id => id).ToArray();
        if (recipients.Length == 0) return;
        var snapshot = prepare();
        if (snapshot.Length == 0) return;
        foreach (var peer in recipients) deliver(peer, snapshot);
    }
}
