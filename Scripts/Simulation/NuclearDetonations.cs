using System.Collections.Concurrent;

namespace DealOrForce.Simulation;

/// <summary>
/// Where a nuclear weapon has just gone off, for whatever draws the world. Presentation only:
/// nothing here is saved, read by the simulation or fingerprinted - a loaded game does not
/// replay old explosions, and a bench with no globe simply never empties it (it is bounded).
/// </summary>
public static class NuclearDetonations
{
    private const int Limit = 16;
    private static readonly ConcurrentQueue<(int Province, int Day, bool Retaliation)> Pending = new();

    public static void Announce(int province, int day, bool retaliation)
    {
        if (province <= 0) return;
        while (Pending.Count >= Limit && Pending.TryDequeue(out _)) { }
        Pending.Enqueue((province, day, retaliation));
    }

    public static bool TryTake(out (int Province, int Day, bool Retaliation) detonation) =>
        Pending.TryDequeue(out detonation);

    public static void Clear()
    {
        while (Pending.TryDequeue(out _)) { }
    }
}
