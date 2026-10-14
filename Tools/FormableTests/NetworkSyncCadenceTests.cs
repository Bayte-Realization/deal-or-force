using System;
using DealOrForce.Simulation;

static class NetworkSyncCadenceTests
{
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS network cadence: " + message); }
    public static void Run()
    {
        Check(NetworkSyncCadence.Due(0, int.MinValue, 10), "the first checksum/resync request is not suppressed by signed overflow");
        Check(NetworkSyncCadence.Due(int.MaxValue, int.MinValue, 10), "a late first request handles the whole integer range");
        Check(!NetworkSyncCadence.Due(109, 100, 10), "cadence still suppresses an early repeat");
        Check(NetworkSyncCadence.Due(110, 100, 10), "the exact cadence boundary permits a repeat");
        Check(!NetworkSyncCadence.Due(100, 110, 10), "a backwards clock does not manufacture a due request");
        Check(!NetworkSyncCadence.Due(-1, int.MinValue, 10), "invalid negative campaign dates do not send checksums");
    }
}
