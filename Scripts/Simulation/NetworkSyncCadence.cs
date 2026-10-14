using System;

namespace DealOrForce.Simulation;

/// <summary>Simulation-day cadence. A fresh negative sentinel is due immediately.</summary>
public static class NetworkSyncCadence
{
    public static bool Due(int today, int previous, int interval) => today >= 0 && today - (long)previous >= Math.Max(0, interval);
}
