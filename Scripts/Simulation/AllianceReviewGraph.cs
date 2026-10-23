using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>Scratch for ONE atomic alliance review, rebuilt on every invocation.
/// No day cache, no stale political state, no ownership of a campaign.</summary>
internal sealed class AllianceReviewGraph
{
    private readonly Dictionary<int, List<int>> _allies = new();
    internal readonly List<StrategicWar> Wars = new();
    internal readonly List<(long Key, DiplomaticRelation Relation)> Due = new();

    internal void Rebuild(Campaign c)
    {
        Due.Clear(); Wars.Clear();
        foreach (var allies in _allies.Values) allies.Clear();
        foreach (var war in c.Wars) if (!war.Ended) Wars.Add(war);
        foreach (var r in c.Relations.Values)
            if (r.Stance == DiplomaticStance.Allied)
            {
                Add(r.A, r.B); Add(r.B, r.A);
            }
    }

    private void Add(int a, int b)
    {
        if (!_allies.TryGetValue(a, out var allies)) _allies[a] = allies = new();
        allies.Add(b);
    }

    internal void Remove(int a, int b)
    {
        if (_allies.TryGetValue(a, out var one)) one.Remove(b);
        if (_allies.TryGetValue(b, out var other)) other.Remove(a);
    }

    internal bool Conflicts(Campaign c, int a, int b) => Against(c, a, b) || Against(c, b, a);

    private bool Against(Campaign c, int a, int b)
    {
        if (!_allies.TryGetValue(a, out var allies)) return false;
        foreach (var third in allies)
        {
            if (third == a || third == b || c.NationById(third) is null) continue;
            // A preceding rupture changes later reviews in this SAME pass.
            if (CampaignDiplomacy.Peek(c, a, third)?.Stance != DiplomaticStance.Allied) continue;
            if (CampaignDiplomacy.Peek(c, b, third)?.Stance == DiplomaticStance.Rival) return true;
            foreach (var war in Wars)
                if (!war.Ended && war.Opposes(b, third)) return true;
        }
        return false;
    }
}
