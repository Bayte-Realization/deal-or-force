using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>Scratch for ONE atomic alliance review, rebuilt on every invocation.
/// No day cache, no stale political state, no ownership of a campaign.</summary>
internal sealed class AllianceReviewGraph
{
    private readonly Dictionary<int, List<int>> _allies = new();
    // Rivals and live-war enemies of each country, gathered once per review pass (Rebuild).
    private readonly Dictionary<int, HashSet<int>> _hostile = new();
    internal readonly List<StrategicWar> Wars = new();
    internal readonly List<(long Key, DiplomaticRelation Relation)> Due = new();

    internal void Rebuild(Campaign c, int interval = 0)
    {
        Due.Clear(); Wars.Clear();
        foreach (var allies in _allies.Values) allies.Clear();
        foreach (var hostile in _hostile.Values) hostile.Clear();
        foreach (var war in c.Wars) if (!war.Ended) Wars.Add(war);
        foreach (var pair in c.Relations)
        {
            var r = pair.Value;
            if (r.Stance == DiplomaticStance.Allied)
            {
                Add(r.A, r.B); Add(r.B, r.A);
                if (interval > 0 && (r.AllianceAssessmentDay < 0 || c.ElapsedDays - r.AllianceAssessmentDay >= interval))
                {
                    Due.Add((pair.Key, r));
                }
            }
            else if (r.Stance == DiplomaticStance.Rival)
            {
                Hostile(r.A, r.B); Hostile(r.B, r.A);
            }
        }
        foreach (var war in Wars)
            foreach (var attacker in war.Side(true))
                foreach (var defender in war.Side(false))
                {
                    if (attacker == defender) continue;
                    Hostile(attacker, defender); Hostile(defender, attacker);
                }
    }

    private void Add(int a, int b)
    {
        if (!_allies.TryGetValue(a, out var allies)) _allies[a] = allies = new();
        allies.Add(b);
    }

    private void Hostile(int a, int b)
    {
        if (!_hostile.TryGetValue(a, out var set)) _hostile[a] = set = new();
        set.Add(b);
    }

    internal void Remove(int a, int b)
    {
        if (_allies.TryGetValue(a, out var one)) one.Remove(b);
        if (_allies.TryGetValue(b, out var other)) other.Remove(a);
    }

    internal bool Conflicts(Campaign c, int a, int b) => Against(c, a, b) || Against(c, b, a);

    private bool Against(Campaign c, int a, int b)
    {
        if (!_allies.TryGetValue(a, out var allies) || !_hostile.TryGetValue(b, out var hostile) || hostile.Count == 0)
            return false;
        foreach (var third in allies)
        {
            if (third == a || third == b || !hostile.Contains(third) || c.NationById(third) is null) continue;
            // A preceding rupture changes later reviews in this SAME pass.
            if (CampaignDiplomacy.Peek(c, a, third)?.Stance != DiplomaticStance.Allied) continue;
            if (CampaignDiplomacy.Peek(c, b, third)?.Stance == DiplomaticStance.Rival) return true;
            foreach (var war in Wars)
                if (!war.Ended && war.Opposes(b, third)) return true;
        }
        return false;
    }
}
