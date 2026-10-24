using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// One sponsorship or covert operation the player is already in, as a sentence.
/// Reading it does not sponsor a side, order an operation, or write the chronicle.
/// </summary>
public sealed record HybridWarRow(
    string Id,
    string Sentence,
    string OtherName,
    string KindName,
    string Chronicle,
    bool Covert,
    bool Incoming,
    int OtherId,
    int WarId,
    int SponsorshipId,
    int RegionMapIndex);

/// <summary>
/// The player's hybrid wars, read from sponsorships and operations that already exist.
/// A row names the other country, the kind the game already uses, and a chronicle
/// sentence when one is already stored for that backing or operation.
/// </summary>
public static class HybridWarBrief
{
    public static IReadOnlyList<HybridWarRow> Read(Campaign campaign, Nation player, Nation subject)
    {
        if (campaign is null || player is null || subject is null) return Array.Empty<HybridWarRow>();
        var rows = new List<HybridWarRow>();
        if (ProxyVisible(campaign)) AppendSponsorships(campaign, player, subject, rows);
        if (CovertVisible(campaign)) AppendOperations(campaign, player, subject, rows);
        return rows;
    }

    /// <summary>The words the conflicts screen already uses for a level of support.</summary>
    public static string SupportKind(int level) => level switch
    {
        1 => Loc.Get("Advisers, money and spare parts."),
        2 => Loc.Get("Weapons and ammunition every week."),
        3 => Loc.Get("Weapons at scale and volunteers."),
        _ => Loc.Format("level {0}", level)
    };

    private static bool ProxyVisible(Campaign campaign) =>
        MechanicFeatures.SystemEnabled(campaign, nameof(ProxyWars)) && campaign.FeatureEnabled("proxy_wars");

    private static bool CovertVisible(Campaign campaign) =>
        campaign.FeatureEnabled("intelligence") && MechanicFeatures.SystemEnabled(campaign, nameof(CovertOperations));

    private static void AppendSponsorships(Campaign campaign, Nation player, Nation subject, List<HybridWarRow> rows)
    {
        var ordered = new List<Sponsorship>();
        foreach (var sponsorship in campaign.Sponsorships)
            if (sponsorship is not null && !sponsorship.Ended &&
                (sponsorship.SponsorId == player.Id || sponsorship.ClientId == player.Id))
                ordered.Add(sponsorship);
        ordered.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var sponsorship in ordered)
        {
            var otherId = sponsorship.SponsorId == player.Id ? sponsorship.ClientId : sponsorship.SponsorId;
            if (otherId == player.Id) continue;
            if (subject.Id != player.Id && subject.Id != otherId) continue;
            var other = campaign.NationById(otherId);
            if (other is null) continue;
            var incoming = sponsorship.ClientId == player.Id;
            var kind = SupportKind(sponsorship.Level);
            var chronicle = ProxyChronicle(campaign, player, sponsorship);
            var sentence = incoming
                ? Loc.Format("Armed by {0}: {1}.", other.DisplayName, Bare(kind))
                : Loc.Format("Arming {0}: {1}.", other.DisplayName, Bare(kind));
            rows.Add(new HybridWarRow(
                "proxy:" + sponsorship.Id, Join(sentence, chronicle), other.DisplayName, kind, chronicle,
                false, incoming, other.Id, sponsorship.WarId, sponsorship.Id, -1));
        }
    }

    private static void AppendOperations(Campaign campaign, Nation player, Nation subject, List<HybridWarRow> rows)
    {
        var found = new List<(Nation Owner, CovertOperation Operation)>();
        if (player.CovertOperations is not null)
            foreach (var operation in player.CovertOperations)
                if (operation is not null) found.Add((player, operation));
        var owners = new List<Nation>();
        foreach (var nation in campaign.Nations)
            if (nation is not null && nation.Id != player.Id && nation.CovertOperations is { Count: > 0 })
                owners.Add(nation);
        owners.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var owner in owners)
            foreach (var operation in owner.CovertOperations)
                if (operation is not null && operation.TargetId == player.Id)
                    found.Add((owner, operation));
        found.Sort((a, b) =>
        {
            var byOwner = a.Owner.Id.CompareTo(b.Owner.Id);
            if (byOwner != 0) return byOwner;
            var byTarget = a.Operation.TargetId.CompareTo(b.Operation.TargetId);
            if (byTarget != 0) return byTarget;
            return string.Compare(a.Operation.Kind, b.Operation.Kind, StringComparison.Ordinal);
        });
        foreach (var (owner, operation) in found)
        {
            var incoming = owner.Id != player.Id;
            var otherId = incoming ? owner.Id : operation.TargetId;
            if (otherId == player.Id) continue;
            if (subject.Id != player.Id && subject.Id != otherId) continue;
            var other = campaign.NationById(otherId);
            if (other is null) continue;
            var kind = CovertKind(operation);
            var chronicle = CovertChronicle(campaign, player, owner.Id, operation);
            var sentence = incoming
                ? Loc.Format("Operation by {0}: {1}.", other.DisplayName, Bare(kind))
                : Loc.Format("Operation against {0}: {1}.", other.DisplayName, Bare(kind));
            rows.Add(new HybridWarRow(
                "covert:" + owner.Id + ":" + operation.TargetId + ":" + operation.Kind + ":" + operation.StartedDay,
                Join(sentence, chronicle), other.DisplayName, kind, chronicle,
                true, incoming, other.Id, -1, 0, operation.RegionMapIndex));
        }
    }

    private static string CovertKind(CovertOperation operation)
    {
        var name = CovertOperations.Find(operation.Kind)?.Name;
        if (string.IsNullOrWhiteSpace(name)) name = operation.Kind ?? "";
        return Loc.Get(name);
    }

    private static string ProxyChronicle(Campaign campaign, Nation player, Sponsorship sponsorship)
    {
        var warName = "";
        if (sponsorship.WarId >= 0)
            foreach (var war in campaign.Wars)
                if (war is not null && war.Id == sponsorship.WarId) { warName = war.Name ?? ""; break; }
        return Latest(campaign, player, entry =>
            entry.Day >= sponsorship.StartedDay &&
            entry.Kind is "proxy_started" or "proxy_ended" &&
            entry.ActorId == sponsorship.SponsorId && entry.TargetId == sponsorship.ClientId &&
            (warName.Length == 0 || entry.Subject.Length == 0 || entry.Subject == warName));
    }

    private static string CovertChronicle(Campaign campaign, Nation player, int ownerId, CovertOperation operation) =>
        Latest(campaign, player, entry =>
            entry.Day >= operation.StartedDay &&
            entry.Kind is not null && entry.Kind.StartsWith("covert_", StringComparison.Ordinal) &&
            (entry.ActorId == ownerId && entry.TargetId == operation.TargetId ||
             entry.ActorId == operation.TargetId && entry.TargetId == ownerId ||
             entry.ActorId == operation.TargetId && entry.TargetId < 0));

    private static string Latest(Campaign campaign, Nation player, Func<ChronicleEntry, bool> match)
    {
        var entries = campaign.Chronicle?.Entries;
        if (entries is null) return "";
        ChronicleEntry best = null;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null || !match(entry) || !Visible(player, entry)) continue;
            if (best is null || entry.Sequence > best.Sequence) best = entry;
        }
        return best is null ? "" : NewsFeed.TextFor(campaign, best);
    }

    /// <summary>The same privacy the news rail uses: public facts, and a country's own covert notes.</summary>
    private static bool Visible(Nation player, ChronicleEntry entry)
    {
        if (entry.Public) return true;
        return player is not null && entry.ActorId == player.Id &&
            (CovertOperations.TellsActor(entry.Kind) ||
             entry.Kind is not null && entry.Kind.StartsWith("proxy_", StringComparison.Ordinal));
    }

    private static string Bare(string name)
    {
        var text = (name ?? "").Trim();
        while (text.EndsWith('.')) text = text[..^1].TrimEnd();
        return text;
    }

    private static string Join(string sentence, string chronicle) =>
        string.IsNullOrEmpty(chronicle) ? sentence : sentence + " " + chronicle;
}
