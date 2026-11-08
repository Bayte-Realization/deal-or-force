using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// What two machines must agree on before they may play one campaign together.
///
/// The lobby used to find out the hard way: a client one build older connected, received a
/// world its save code could not read or - worse - could, and then called methods whose
/// arguments had changed shape. Godot drops such a call with a line in the log, and the
/// player sees a campaign that simply never moves. The handshake asks first and says why.
/// </summary>
public static class NetworkProtocol
{
    /// <summary>
    /// Bumped whenever a message between host and client changes its meaning or its shape.
    /// 2: the lockstep clock counts hours, not days (an order given at hour 13 of a day was
    /// applied at hour 0 by a client that had reached that day), and seat changes travel.
    /// </summary>
    public const int Version = 2;

    /// <summary>How long a connected machine has to introduce itself before it is turned away, in seconds.</summary>
    public const double HelloSeconds = 15.0;

    /// <summary>Null when the two builds can play together; otherwise what to tell the player.</summary>
    public static string Refusal(int protocol, string gameVersion, string mapDataVersion)
    {
        if (protocol != Version)
            return Loc.Format("Different network protocol: the host speaks {0}, this game {1}. Install the same version as the host.",
                Version, protocol);
        if (!string.Equals(gameVersion ?? "", SaveSystem.GameVersion, StringComparison.Ordinal))
            return Loc.Format("Different game version: the host runs {0}, this game {1}. Install the same version as the host.",
                SaveSystem.GameVersion, gameVersion ?? "?");
        if (!string.Equals(mapDataVersion ?? "", SaveSystem.MapDataVersion, StringComparison.Ordinal))
            return Loc.Format("Different map data: the host has {0}, this game {1}. Install the same version as the host.",
                SaveSystem.MapDataVersion, mapDataVersion ?? "?");
        return null;
    }
}

/// <summary>
/// Things the host did at an hour this client has not reached yet, held until it gets there.
///
/// Lockstep is the promise that every machine does the same thing at the same moment of the
/// same world. The moment is an hour: a step is an hour long, and the host carries out an
/// order between two steps. Holding orders by day kept that promise only while a step was a
/// day - at the shipped hour a client that had reached the day applied an order given in its
/// afternoon at midnight, thirteen steps early, and the checksum found the two worlds apart
/// ten days later. Held in the order they arrived, which is the order the host did them in.
/// </summary>
public sealed class NetworkHeldOrders
{
    private readonly List<(long Hour, Action Apply)> _held = new();

    public int Count => _held.Count;

    /// <summary>Carry it out now if this world has reached the hour, otherwise hold it. True when it ran now.</summary>
    public bool ApplyOrHold(long hour, long now, Action apply)
    {
        if (apply is null) return false;
        // Something already held is older and must run first, even if it is for the same hour.
        if (hour <= now && _held.Count == 0)
        {
            apply();
            return true;
        }
        _held.Add((hour, apply));
        if (hour <= now) ApplyDue(now);
        return false;
    }

    /// <summary>
    /// Everything held for this hour or before, in arrival order. Stops at the first thing
    /// still in the future: what came after it on the wire came after it at the host.
    /// </summary>
    public int ApplyDue(long now)
    {
        var applied = 0;
        while (_held.Count > 0 && _held[0].Hour <= now)
        {
            var next = _held[0];
            _held.RemoveAt(0);
            next.Apply();
            applied++;
        }
        return applied;
    }

    /// <summary>A whole world arrived: everything held was sent before it, so it is already in it.</summary>
    public void Clear() => _held.Clear();
}
