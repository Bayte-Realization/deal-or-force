using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// The latest chronicle lines a country can read: its own visible entries, and entries
/// about a war it is still fighting. Opening the list does not record a line.
/// </summary>
public static class CampaignChronicle
{
    public const int Limit = 12;
    public const string Title = "Campaign chronicle";
    public const string Empty = "Nothing has been written yet.";

    public readonly record struct Line(long Sequence, string Kind, string Subject, string Text);

    /// <summary>
    /// Newest first, at most <see cref="Limit"/>. An empty book is one sentence, not a blank.
    /// <paramref name="viewer"/> decides which private lines are visible; it defaults to the
    /// country being read.
    /// </summary>
    public static IReadOnlyList<Line> Read(Campaign campaign, Nation country, Nation viewer = null)
    {
        if (campaign?.Chronicle is null || country is null)
            return new[] { Blank() };
        viewer ??= country;
        var wars = OpenWars(campaign, country.Id);
        var found = new List<Line>(Limit);
        var entries = campaign.Chronicle.Entries;
        for (var index = entries.Count - 1; index >= 0 && found.Count < Limit; index--)
        {
            var entry = entries[index];
            if (entry is null || !Visible(viewer, entry) || !Concerns(country.Id, entry, wars)) continue;
            found.Add(Describe(campaign, entry));
        }
        return found.Count == 0 ? new[] { Blank() } : found;
    }

    private static Line Blank() => new(0, "", "", Loc.Get(Empty));

    private static bool Visible(Nation viewer, ChronicleEntry entry) =>
        entry.Public || viewer is not null && entry.ActorId == viewer.Id;

    private static List<StrategicWar> OpenWars(Campaign campaign, int countryId)
    {
        var wars = new List<StrategicWar>();
        foreach (var war in campaign.Wars)
            if (war is not null && !war.Ended && war.Involves(countryId)) wars.Add(war);
        return wars;
    }

    private static bool Concerns(int countryId, ChronicleEntry entry, List<StrategicWar> wars)
    {
        if (entry.ActorId == countryId || entry.TargetId == countryId) return true;
        var subject = entry.Subject ?? "";
        foreach (var war in wars)
        {
            if (subject.Length > 0 && string.Equals(subject, war.Name, StringComparison.Ordinal))
                return true;
            if (entry.TargetId >= 0 && war.Involves(entry.ActorId) && war.Involves(entry.TargetId))
                return true;
        }
        return false;
    }

    private static Line Describe(Campaign campaign, ChronicleEntry entry)
    {
        var stored = entry.Subject ?? "";
        var kind = KindText(campaign, entry);
        var text = entry.Date.Display() + " · " + kind;
        var shown = ShownSubject(campaign, entry);
        if (shown.Length > 0 && text.IndexOf(shown, StringComparison.Ordinal) < 0)
            text += " · " + shown;
        if (stored.Length > 0 && text.IndexOf(stored, StringComparison.Ordinal) < 0)
            text += " · " + stored;
        return new Line(entry.Sequence, entry.Kind ?? "", stored, text);
    }

    /// <summary>The chronicle sentence when a translation exists; otherwise the stored kind.</summary>
    private static string KindText(Campaign campaign, ChronicleEntry entry)
    {
        var kind = entry.Kind ?? "";
        var key = "chronicle." + kind;
        return Loc.Has(key) ? NewsFeed.TextFor(campaign, entry) : kind;
    }

    private static string ShownSubject(Campaign campaign, ChronicleEntry entry)
    {
        var stored = entry.Subject ?? "";
        if (stored.Length == 0) return "";
        if (entry.Kind is "ultimatum_accepted" or "ultimatum_refused" && campaign.States is not null)
            foreach (var state in campaign.States)
                if (state is not null && state.Name == stored && !string.IsNullOrEmpty(state.DisplayName))
                    return state.DisplayName;
        var recorded = CountryNames.Recorded(stored);
        return recorded.Length > 0 ? recorded : stored;
    }
}
