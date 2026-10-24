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
    private static Dictionary<string, string> _stings = new(System.StringComparer.OrdinalIgnoreCase);
    private static int _railMinimum = 2, _stingGapMs = 9000;

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

    /// <summary>
    /// The sound cue a headline of this kind makes when it reaches the rail (common/news
    /// "stings"), or "" for none. Presentation only: nothing in the simulation reads it.
    /// </summary>
    public static string Sting(string kind)
    {
        _importance ??= Load();
        return kind is not null && _stings.TryGetValue(kind, out var cue) ? cue : "";
    }

    /// <summary>Every kind of news that has a sting, and its cue.</summary>
    public static IReadOnlyDictionary<string, string> Stings
    {
        get
        {
            _importance ??= Load();
            return _stings;
        }
    }

    /// <summary>The least time between two headline stings, so a busy day is one sound.</summary>
    public static int StingGapMs
    {
        get
        {
            _importance ??= Load();
            return _stingGapMs;
        }
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
        _stings = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        _railMinimum = 2; _stingGapMs = 9000;
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
        if (root.TryGetProperty("sting_gap_ms", out var gap) && gap.TryGetInt32(out var ms))
            _stingGapMs = System.Math.Clamp(ms, 0, 120000);
        if (root.TryGetProperty("stings", out var stings) && stings.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var entry in stings.EnumerateObject())
            {
                if (entry.Value.ValueKind == System.Text.Json.JsonValueKind.String) _stings[entry.Name] = entry.Value.GetString().Trim();
                else errors?.Add($"common/news: sting '{entry.Name}' needs a cue name, or \"\" for silence.");
            }
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
            yield return new Line(entry.Sequence, entry.Day, entry.Kind, TextFor(campaign, entry), entry.ActorId, entry.TargetId);
            if (take > 0 && ++emitted >= take) yield break;
        }
    }

    /// <summary>
    /// The sentence for one recorded entry, as the feed would print it.
    ///
    /// Public because more than the feed needs the same words: a history chart marks the day an
    /// event happened and explains the mark in a tooltip, and a second translation path would
    /// sooner or later disagree with the rail about what happened, or with history about who was
    /// called what. Chronicle snapshots retain their historical text, so translate at read time
    /// without rewriting the saved entry or renaming a historical identity.
    /// </summary>
    public static string TextFor(Campaign campaign, ChronicleEntry entry)
    {
        if (entry is null) return "";
        string Country(int id, string recorded)
        {
            var nation = campaign?.NationById(id);
            return nation is not null && nation.Name == recorded ? nation.DisplayName : CountryNames.Recorded(recorded);
        }
        var subject = entry.Kind is "ultimatum_accepted" or "ultimatum_refused"
            ? campaign?.States.FirstOrDefault(state => state.Name == entry.Subject)?.DisplayName ??
              CountryNames.Recorded(entry.Subject)
            : CountryNames.Recorded(entry.Subject);
        return Loc.Format("chronicle." + entry.Kind,
            Country(entry.ActorId, entry.ActorName), Country(entry.TargetId, entry.TargetName), subject,
            CountryNames.Recorded(entry.Place));
    }

    /// <summary>
    /// One extra line for a rail tooltip. Empty below importance 2, and empty when the books
    /// have nothing further to say. At most two facts already stored: the war ledger's largest
    /// part, the crisis stage and tension the situation room already names, or the chronicle
    /// subject and place. It does not record, spend, roll, or open a relation.
    /// </summary>
    public static string Context(Campaign campaign, Line line)
    {
        if (campaign is null || line is null || Importance(line.Kind) < 2) return "";
        string first = null, second = null;
        void Take(string fact)
        {
            if (string.IsNullOrEmpty(fact) || second is not null) return;
            if (first is null) first = fact;
            else second = fact;
        }

        if (line.ActorId >= 0 && line.TargetId >= 0 && line.ActorId != line.TargetId)
        {
            var war = LatestWar(campaign, line.ActorId, line.TargetId);
            if (war is not null) Take(WarScoring.LeadingCause(war));
            var relation = CampaignDiplomacy.Peek(campaign, line.ActorId, line.TargetId);
            var stage = Escalation.StageOf(relation);
            if (relation is not null && stage is TensionStage.Crisis or TensionStage.Clashes)
            {
                Take(Loc.Format("{0}: {1}.", Loc.Get("Stage"), Loc.Get(StageCaption(stage))));
                Take(Loc.Format("{0}: {1}.", Loc.Get("Tension"),
                    relation.Tension.ToString("0.0", Loc.Culture)));
            }
        }

        var stored = Stored(campaign, line.Sequence);
        if (stored is not null)
        {
            if (stored.Subject.Length > 0) Take(Loc.Get(stored.Subject));
            if (stored.Place.Length > 0) Take(Loc.Get(stored.Place));
        }

        if (first is null) return "";
        return second is null ? first : first + " " + second;
    }

    private static string StageCaption(TensionStage stage) => stage switch
    {
        TensionStage.Clashes => "Armed clashes",
        TensionStage.Crisis => "Crisis",
        _ => ""
    };

    private static StrategicWar LatestWar(Campaign campaign, int a, int b)
    {
        StrategicWar found = null;
        foreach (var war in campaign.Wars)
        {
            if (war is null || !war.Opposes(a, b)) continue;
            if (found is null || war.Id > found.Id) found = war;
        }
        return found;
    }

    private static ChronicleEntry Stored(Campaign campaign, long sequence)
    {
        var entries = campaign.Chronicle.Entries;
        for (var index = entries.Count - 1; index >= 0; index--)
            if (entries[index].Sequence == sequence) return entries[index];
        return null;
    }
}
