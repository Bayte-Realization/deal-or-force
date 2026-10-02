using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// The world's news, as sentences.
///
/// The chronicle already records every public act the simulation performs - wars declared,
/// peace signed, embargoes imposed, ultimatums answered - and the ledger reads it as a
/// table. The feed reads the same buffer for the player who is not looking: each entry is
/// one localised line, newest first, and <see cref="Lines"/> answers only what happened
/// after a sequence the caller remembers, so a panel that refreshes asks for the delta
/// rather than re-reading history.
///
/// Nothing here infers causes or holds state: a line is a chronicle entry with a voice.
/// </summary>
public static class NewsFeed
{
    public sealed record Line(long Sequence, int Day, string Kind, string Text, int ActorId, int TargetId);

    /// <summary>
    /// Public entries newer than `since`, newest first. Private entries - a country's own
    /// decisions, its construction - are somebody's ledger rows, not world news.
    /// </summary>
    public static IEnumerable<Line> Lines(Campaign campaign, long since = 0, int take = 0)
    {
        if (campaign?.Player is null) yield break;
        // The buffer is append-ordered (oldest first); the feed is news, so newest first.
        for (var index = campaign.Chronicle.Entries.Count - 1; index >= 0; index--)
        {
            var entry = campaign.Chronicle.Entries[index];
            if (!entry.Public || entry.Sequence <= since) continue;
            var text = Loc.Format("chronicle." + entry.Kind,
                entry.ActorName, entry.TargetName, entry.Subject, entry.Place);
            yield return new Line(entry.Sequence, entry.Day, entry.Kind, text, entry.ActorId, entry.TargetId);
        }
    }
}
