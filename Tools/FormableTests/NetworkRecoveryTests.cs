using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DealOrForce.Simulation;

internal static class NetworkRecoveryTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL network recovery: " + label);
        Console.WriteLine("PASS network recovery: " + label);
    }

    public static void Run()
    {
        var gate = new NetworkRecoveryThrottle();
        Check(gate.TryAcquire(1, 0), "the first request works at session time zero");
        Check(!gate.TryAcquire(1, 0) && !gate.TryAcquire(1, 9.999), "duplicates cannot prepare another world during cooldown");
        Check(gate.TryAcquire(1, 10), "the exact real-time boundary permits recovery");
        Check(!NetworkSyncCadence.Due(400, 400, 10) && gate.TryAcquire(1, 20),
            "a paused world can retry without waiting ten campaign days");
        Check(!gate.TryAcquire(1, 19) && !gate.TryAcquire(1, 20), "backwards and repeated clocks do not manufacture a request");
        Check(gate.TryAcquire(2, 20), "one stalled peer does not block another peer's recovery");
        Check(gate.Count == 2, "the budget contains one timestamp per admitted peer, not one per request");
        var prepared = 0;
        for (var i = 0; i < 10000; i++)
            if (gate.TryAcquire(7, 25 + i * .0001)) prepared++;
        Check(prepared == 1, "ten thousand same-peer requests cause one expensive preparation");
        Check(gate.TryAcquire(7, 35), "the host permits a later retry for that same peer");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1 })
            Check(!gate.TryAcquire(90, invalid), "invalid time is refused without poisoning the budget: " + invalid);
        Check(!gate.TryAcquire(0, 40) && !gate.TryAcquire(-1, 40), "invalid peer IDs never enter the budget");
        Check(gate.Count == 3 && gate.TryAcquire(90, 40), "refused times leave no retained peer entry");
        gate.Forget(1);
        Check(gate.TryAcquire(1, 20) && !gate.TryAcquire(2, 20), "disconnect forgets only the disconnected peer");
        gate.Clear();
        Check(gate.Count == 0 && gate.TryAcquire(2, 0), "a fresh session has no cooldown from the previous game");
        Check(!gate.TryAcquire(2, 5) && gate.TryAcquire(2, 10), "clearing a session keeps the normal cooldown policy");
        QueueStress();
    }

    private static void QueueStress()
    {
        var queue = new NetworkHeldOrders();
        const int count = 25000;
        var next = 0; var ordered = true;
        for (var i = 0; i < count; i++)
        {
            var order = i;
            queue.ApplyOrHold(100 + i / 1000, 0, () => { ordered &= next == order; next++; });
        }
        Check(queue.Count == count && queue.ApplyDue(99) == 0, "a large future backlog remains untouched before its hour");
        Check(queue.ApplyDue(111) == 12000 && next == 12000 && queue.Count == 13000,
            "catch-up drains only the due prefix, including orders at the same hour");
        Check(queue.ApplyDue(200) == 13000 && next == count && ordered && queue.Count == 0,
            "all twenty-five thousand orders execute once in authoritative arrival order");
        var seen = new List<int>();
        queue.ApplyOrHold(1, 0, () => { seen.Add(1); queue.Clear(); queue.ApplyOrHold(1, 1, () => seen.Add(3)); });
        queue.ApplyOrHold(1, 0, () => seen.Add(2));
        queue.ApplyDue(1);
        Check(seen.Count == 2 && seen[0] == 1 && seen[1] == 3 && queue.Count == 0,
            "snapshot-style clear during a callback does not replay discarded orders");
        var payload = HoldPayload(queue);
        Check(payload.IsAlive, "a held command keeps its payload until recovery");
        queue.Clear();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!payload.IsAlive, "recovery releases references to discarded command payloads");
        Check(!queue.ApplyOrHold(0, 0, null) && queue.Count == 0, "a missing action creates no pending order");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference HoldPayload(NetworkHeldOrders queue)
    {
        var bytes = new byte[128 * 1024];
        queue.ApplyOrHold(1000, 0, () => GC.KeepAlive(bytes));
        return new WeakReference(bytes);
    }
}
