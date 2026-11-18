using System.Collections.Generic;
using System.Diagnostics;

namespace DealOrForce.Simulation;

/// <summary>
/// What one indivisible piece of a staged pass cost the last time it ran - one country's new
/// name, one country's war council - so the pass can say so before it runs it again
/// (<see cref="Campaign.HeavyNext"/>) and the piece starts a fresh frame instead of landing on a
/// frame that is already full.
///
/// The pieces that make a frame wait are few and the same ones each time: the same large
/// country renames itself every month, the same war is argued over every eleven days. A cost
/// remembered from last time is a good guess, and a wrong guess costs one frame of time, never
/// a different world: this decides only where a frame ends. Main thread only; not saved.
/// </summary>
public static class StageCosts
{
    /// <summary>A piece that took this long last time is announced as heavy.</summary>
    public const double HeavyMilliseconds = 6.0;

    private static readonly Dictionary<(string Site, int Id), float> Last = new();

    /// <summary>Whether this piece took long enough last time to be sent to a fresh frame.</summary>
    public static bool Heavy(string site, int id) =>
        Last.TryGetValue((site, id), out var ms) && ms >= HeavyMilliseconds;

    /// <summary>A timestamp to hand to <see cref="Stop"/>.</summary>
    public static long Start() => Stopwatch.GetTimestamp();

    /// <summary>What the piece cost this time, for the next.</summary>
    public static void Stop(string site, int id, long started)
    {
        var ms = (float)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (ms >= HeavyMilliseconds) Last[(site, id)] = ms;
        // Forgotten once it is cheap, so the table holds only the few that matter.
        else Last.Remove((site, id));
    }
}
