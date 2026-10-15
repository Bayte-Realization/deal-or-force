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

    private static Dictionary<string, int> _importance;
    private static int _railMinimum = 2;

    /// <summary>
    /// How much a kind of news matters, 0 to 3 (common/news): 0 is routine - aid sent, a building
    /// finished - and 3 is a war, a peace, a country born or gone. The rail beside the map shows
    /// what reaches <see cref="RailMinimum"/>, and anything that concerns the player's own country.
    /// The full feed in the reports shows everything.
    /// </summary>
    public static int Importance(string kind)
    {
        _importance ??= Load();
        return kind is not null && _importance.TryGetValue(kind, out var level) ? level : 1;
    }

    public static int RailMinimum
    {
        get
        {
            _importance ??= Load();
            return _railMinimum;
        }
    }

    /// <summary>Read the base game's table again; mods layer over it through <see cref="Merge"/>.</summary>
    public static void ResetCatalog()
    {
        _importance = Load();
    }

    private static Dictionary<string, int> Load()
    {
        _importance = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        _railMinimum = 2;
        foreach (var document in GameDataLoader.DataDocuments("common", "news"))
            using (document)
                Merge(document.RootElement, null);
        return _importance;
    }

    public static void Merge(System.Text.Json.JsonElement root, List<string> errors)
    {
        _importance ??= new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        if (root.TryGetProperty("rail_minimum", out var minimum) && minimum.TryGetInt32(out var level))
            _railMinimum = System.Math.Clamp(level, 0, 3);
        if (!root.TryGetProperty("importance", out var kinds) || kinds.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        foreach (var entry in kinds.EnumerateObject())
        {
            if (entry.Value.TryGetInt32(out var value)) _importance[entry.Name] = System.Math.Clamp(value, 0, 3);
            else errors?.Add($"common/news: '{entry.Name}' needs a whole number from 0 to 3.");
        }
    }

    /// <summary>The lines worth the rail: important enough, or about the player's own country.</summary>
    public static IEnumerable<Line> Headlines(Campaign campaign, int take)
    {
        var playerId = campaign?.Player?.Id ?? -1;
        var minimum = RailMinimum;
        var emitted = 0;
        foreach (var line in Lines(campaign))
        {
            if (Importance(line.Kind) < minimum && line.ActorId != playerId && line.TargetId != playerId) continue;
            yield return line;
            if (take > 0 && ++emitted >= take) yield break;
        }
    }

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
            // A covert operation's outcome is private, and news to the one country it names first.
            var told = !entry.Public && entry.ActorId == campaign.Player.Id && CovertOperations.TellsActor(entry.Kind);
            if (!entry.Public && !told || entry.Sequence <= since) continue;
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
