using System;
using System.Collections.Generic;
using DealOrForce.Simulation;

/// <summary>Orders held to the host's hour, and the handshake that keeps two builds apart (NetworkLockstep).</summary>
internal static class NetworkLockstepTests
{
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("FAIL network lockstep: " + message);
        Console.WriteLine("PASS network lockstep: " + message);
    }

    public static void Run()
    {
        var done = new List<string>();
        var held = new NetworkHeldOrders();

        Check(held.ApplyOrHold(100, 100, () => done.Add("now")) && done.Count == 1,
            "an order for the hour this world is at runs at once");
        Check(!held.ApplyOrHold(113, 100, () => done.Add("afternoon")) && held.Count == 1 && done.Count == 1,
            "an order given at hour 13 waits on a client still at hour 0 of that day");
        Check(!held.ApplyOrHold(100, 100, () => done.Add("after")) && held.Count == 2,
            "an order for now waits behind an older held one, because it came after it at the host");
        held.ApplyDue(112);
        Check(held.Count == 2 && done.Count == 1, "an hour short of it, nothing held runs");
        held.ApplyDue(113);
        Check(held.Count == 0 && done.Count == 3 && done[1] == "afternoon" && done[2] == "after",
            "at its hour it runs, and what came after it runs in arrival order");

        held.ApplyOrHold(500, 10, () => done.Add("stale"));
        held.Clear();
        held.ApplyDue(1000);
        Check(!done.Contains("stale"), "a whole world arriving throws away what was held (it is in that world)");

        var seq = new List<int>();
        for (var i = 0; i < 5; i++) { var n = i; held.ApplyOrHold(200 + i / 2, 0, () => seq.Add(n)); }
        held.ApplyDue(201);
        Check(seq.Count == 4 && seq[0] == 0 && seq[3] == 3 && held.Count == 1, "several hours caught up in one go keep their order");

        Check(NetworkProtocol.Refusal(NetworkProtocol.Version, SaveSystem.GameVersion, SaveSystem.MapDataVersion) is null,
            "the same build is welcome");
        Check(NetworkProtocol.Refusal(NetworkProtocol.Version - 1, SaveSystem.GameVersion, SaveSystem.MapDataVersion) is not null,
            "an older protocol is turned away with a reason");
        Check(NetworkProtocol.Refusal(NetworkProtocol.Version, "0.0.0.1", SaveSystem.MapDataVersion) is { Length: > 0 } version &&
              version.Contains("0.0.0.1"), "another game version is named in the refusal");
        Check(NetworkProtocol.Refusal(NetworkProtocol.Version, SaveSystem.GameVersion, "1999") is not null,
            "other map data is turned away");
    }
}
