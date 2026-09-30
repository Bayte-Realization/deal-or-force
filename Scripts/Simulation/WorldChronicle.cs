using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Facts emitted only by successful simulation actions; no UI-owned state or inferred causes.</summary>
public sealed record ChronicleEntry(long Sequence, int Day, GameDate Date, string Kind,
    int ActorId, int TargetId, string ActorName, string TargetName, string Subject, string Place, bool Public);

public sealed class WorldChronicle
{
    public const int Capacity = 2048;
    private readonly List<ChronicleEntry> _entries = new();
    private long _next = 1;
    public IReadOnlyList<ChronicleEntry> Entries => _entries;

    public void Record(Campaign campaign, string kind, int actorId, int targetId = -1,
        string subject = "", string place = "", bool isPublic = true)
    {
        var actor = campaign.NationById(actorId);
        if (actor is null) return;
        _entries.Add(new(_next++, campaign.ElapsedDays, campaign.Date, kind, actorId, targetId,
            actor.Name, campaign.NationById(targetId)?.Name ?? "", subject, place, isPublic));
        if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
    }

    public ChronicleEntry[] Read(int viewerId, int countryId, bool world) => _entries
        .Where(e => (e.Public || e.ActorId == viewerId) &&
            (world ? e.Public : e.ActorId == countryId || e.TargetId == countryId))
        .OrderByDescending(e => e.Sequence).ToArray();

    internal void Restore(IEnumerable<ChronicleEntry> entries)
    {
        _entries.Clear();
        if (entries is not null) _entries.AddRange(entries.Where(e => e is not null)
            .OrderBy(e => e.Sequence).TakeLast(Capacity));
        _next = _entries.Count == 0 ? 1 : _entries.Max(e => e.Sequence) + 1;
    }
}

public sealed partial class Campaign
{
    public readonly WorldChronicle Chronicle = new();
}
