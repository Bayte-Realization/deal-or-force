using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>Presentation-only transport. Never serialize it into a campaign or draw its RNG.
/// Selection is pending until a free audio deck exists; repeated requests keep only the latest.</summary>
public sealed class MusicTransport
{
    public bool Manual { get; private set; }
    public bool Paused { get; private set; }
    public string Selection { get; private set; } = "";
    private bool _pending;

    public bool Select(string id, IReadOnlyList<string> playlist)
    {
        var index = IndexOf(playlist, id);
        if (index < 0) return false;
        Manual = true; Paused = false; Selection = playlist[index]; _pending = true;
        return true;
    }

    /// <summary>Ordered browsing, independent of adaptive weights. Empty lists do nothing.</summary>
    public static string Neighbour(IReadOnlyList<string> playlist, string current, int direction)
    {
        if (playlist is null || playlist.Count == 0 || direction == 0) return "";
        var index = IndexOf(playlist, current);
        if (index < 0) return playlist[direction < 0 ? playlist.Count - 1 : 0];
        return playlist[(index + (direction < 0 ? playlist.Count - 1 : 1)) % playlist.Count];
    }

    public void SetPaused(bool paused) => Paused = paused;
    public void Automatic()
    { Manual = false; Paused = false; Selection = ""; _pending = false; }

    /// <summary>
    /// The sentence on the campaign rail. Closing the gallery is not a transport change:
    /// the window's own preview stops, and this line keeps describing the decks that stay.
    /// </summary>
    public static string Presence(bool manual, bool paused) => paused
        ? "Soundtrack paused. The campaign clock is unchanged."
        : manual
            ? "Manual playlist. Keeps playing when this window closes."
            : "Automatic soundtrack follows the campaign.";

    public bool TakeSelection(out string id)
    {
        id = "";
        if (!Manual || Paused || !_pending) return false;
        id = Selection; _pending = false; return true;
    }

    private static int IndexOf(IReadOnlyList<string> playlist, string id)
    {
        if (playlist is null || string.IsNullOrWhiteSpace(id)) return -1;
        for (var i = 0; i < playlist.Count; i++)
            if (string.Equals(playlist[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
