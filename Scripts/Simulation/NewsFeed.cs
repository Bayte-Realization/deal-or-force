using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// The world's news, as sentences.
///
/// The chronicle already records every public act the simulation performs - wars declared,
/// peace signed, embargoes imposed, ultimatums answered - and the ledger reads it as a
/// table. The feed reads the same buffer for the player who is not looking: each entry is
/// one localised line, newest first, and <see cref="Lines"/> answers only what happened
/// after a sequence the caller remembers. Append-only consumers can ask for a delta;
/// panels that rebuild their rows must request a bounded snapshot instead.
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
        var emitted = 0;
        // The buffer is append-ordered (oldest first); the feed is news, so newest first.
        for (var index = campaign.Chronicle.Entries.Count - 1; index >= 0; index--)
        {
            var entry = campaign.Chronicle.Entries[index];
            if (!entry.Public || entry.Sequence <= since) continue;
            // Chronicle snapshots retain their historical text. Translate at read time,
            // without rewriting the saved entry or renaming a historical identity.
            string Country(int id, string recorded)
            {
                var nation = campaign.NationById(id);
                return nation is not null && nation.Name == recorded ? nation.DisplayName : Loc.Get(recorded);
            }
            var subject = entry.Kind is "ultimatum_accepted" or "ultimatum_refused"
                ? campaign.States.FirstOrDefault(state => state.Name == entry.Subject)?.DisplayName ?? Loc.Get(entry.Subject)
                : Loc.Get(entry.Subject);
            var text = Loc.Format("chronicle." + entry.Kind,
                Country(entry.ActorId, entry.ActorName), Country(entry.TargetId, entry.TargetName), subject, Loc.Get(entry.Place));
            yield return new Line(entry.Sequence, entry.Day, entry.Kind, text, entry.ActorId, entry.TargetId);
            if (take > 0 && ++emitted >= take) yield break;
        }
    }
}
