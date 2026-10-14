using System;
using System.Collections.Generic;
using DealOrForce.Simulation;

static class NetworkSnapshotDeliveryTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS snapshot fanout: " + label); }
    public static void Run()
    {
        var calls = 0;
        var sent = new List<(long Peer, byte[] Payload)>();
        var payload = new byte[] { 4, 2 };
        NetworkSnapshotDelivery.Send(new long[] { 7, 1, 3, 7, -1, 0, 9 },
            () => { calls++; return payload; }, (peer, data) => sent.Add((peer, data)));
        Check(calls == 1, "three remote peers serialize/compress the world only once");
        Check(sent.Count == 3 && sent[0].Peer == 3 && sent[1].Peer == 7 && sent[2].Peer == 9,
            "remote recipients are unique and deterministic, host and invalid IDs excluded");
        Check(sent.TrueForAll(item => ReferenceEquals(item.Payload, payload)), "all recipients receive the identical authoritative payload");
        calls = 0;
        NetworkSnapshotDelivery.Send(new long[] { 1 }, () => { calls++; return payload; }, (_, _) => throw new Exception("host was sent"));
        Check(calls == 0, "a local-only session does not prepare an unnecessary snapshot");
        sent.Clear();
        NetworkSnapshotDelivery.Send(new long[] { 3, 7 }, () => Array.Empty<byte>(), (peer, data) => sent.Add((peer, data)));
        Check(sent.Count == 0, "a rejected/empty save is not delivered");
        var fresh = new byte[] { 8 };
        NetworkSnapshotDelivery.Send(new long[] { 3 }, () => fresh, (peer, data) => sent.Add((peer, data)));
        Check(sent.Count == 1 && ReferenceEquals(sent[0].Payload, fresh), "a later resync prepares fresh state instead of retaining a stale cache");
    }
}
