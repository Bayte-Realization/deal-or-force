using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class DiplomaticRelation
{
    /// <summary>Set aside because one side has no people left (RelationDormancy). Saved, so a load puts it back where it was.</summary>
    public bool Dormant;
}

public sealed partial class Campaign
{
    /// <summary>
    /// Relations with a country that no longer has people: kept, with all they remember, but out of
    /// the table every daily system walks (RelationDormancy). Read through CampaignDiplomacy.Peek;
    /// CampaignDiplomacy.Get wakes one.
    /// </summary>
    public readonly Dictionary<long, DiplomaticRelation> DormantRelations = new();
}

public sealed partial class Defines
{
    public bool RelationDormancyEnabled { get; set; } = true;
}

/// <summary>
/// The relations of the dead, set aside.
///
/// A relation is made the first time two countries have anything to do with each other and was
/// never unmade. Every rising, faction, movement and short-lived state of a long campaign left its
/// row behind: a four-year save held 53,018 relations for 257 countries with people, 41 % of them
/// with somebody who no longer existed, and social memory, alliances, opinion, gestures and the
/// AI's diplomacy walked every one of them every day. (Most of those rows are with live movements,
/// which stay awake; the systems' own costs were the larger part of the slowdown and were cut
/// where they grew - AllianceReviewGraph, SocialMemory, the AI's friendly diplomacy.) Such a
/// relation now sleeps: it keeps its memories (a restored country
/// remembers who betrayed it) but leaves the daily table, and wakes the day its country has people
/// again or anything asks to write to it.
/// </summary>
public static class RelationDormancy
{
    private static readonly List<long> Moving = new();
    private static bool[] _gone = Array.Empty<bool>();

    /// <summary>Whether this side of a relation is gone: no country, or no people and no living movement.</summary>
    private static bool Gone(Campaign c, int id) =>
        c.NationById(id) is not { } n || n.Population <= 0 && n.Movement is not { Dissolved: false };

    private static bool GoneNow(int id) => id < 0 || id >= _gone.Length || _gone[id];

    private static bool Keep(DiplomaticRelation r) => r.Stance == DiplomaticStance.AtWar || r.LoanOutstanding > 0f;

    /// <summary>Once a week, at the day's start: the dead go to sleep and the restored wake.</summary>
    public static void Sweep(Campaign c)
    {
        // Weekly: the flag is saved, so a load restores exactly what the running game had; and
        // anything that writes to a sleeping relation wakes it at once (CampaignDiplomacy.Get).
        if (c?.Defines is not { RelationDormancyEnabled: true } || c.ElapsedDays % 7 != 0) return;
        if (_gone.Length < c.Nations.Count) _gone = new bool[c.Nations.Count + 64];
        Array.Clear(_gone);
        for (var id = 0; id < c.Nations.Count; id++) _gone[id] = Gone(c, id);
        Moving.Clear();
        foreach (var (key, relation) in c.Relations)
            if ((GoneNow(relation.A) || GoneNow(relation.B)) && !Keep(relation)) Moving.Add(key);
        foreach (var key in Moving)
        {
            var relation = c.Relations[key];
            c.Relations.Remove(key);
            relation.Dormant = true;
            c.DormantRelations[key] = relation;
        }
        if (c.DormantRelations.Count == 0) return;
        Moving.Clear();
        foreach (var (key, relation) in c.DormantRelations)
            if (!GoneNow(relation.A) && !GoneNow(relation.B)) Moving.Add(key);
        foreach (var key in Moving) Wake(c, key);
    }

    /// <summary>Back into the daily table.</summary>
    internal static DiplomaticRelation Wake(Campaign c, long key)
    {
        if (!c.DormantRelations.Remove(key, out var relation)) return null;
        relation.Dormant = false;
        c.Relations[key] = relation;
        // Its opinion drivers are worked out by the weekly opinion pass like every other relation's.
        return relation;
    }
}
