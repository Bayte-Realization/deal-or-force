using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public static partial class FrontPlanner
{
    [ThreadStatic] private static List<Army> _emergencyMilitiaScratch;
    [ThreadStatic] private static HashSet<int> _emergencyDetachedIdsScratch;
    [ThreadStatic] private static HashSet<int> _emergencyOpenWarsScratch;
    [ThreadStatic] private static Dictionary<int, (int Count, Army Candidate)> _emergencyByStateScratch;
    [ThreadStatic] private static List<int> _emergencyStateKeysScratch;
    [ThreadStatic] private static HashSet<int> _needsCandidatesScratch;
    [ThreadStatic] private static HashSet<int> _needsHasRegularsScratch;
    [ThreadStatic] private static HashSet<int> _needsHasMilitiaScratch;

    private static readonly Comparison<Army> CompareArmyById = static (a, b) => a.Id.CompareTo(b.Id);

    /// <summary>
    /// A state whose last regular formation was lost can still detach a small field reserve
    /// from its existing local militia. Keep at least one uncommitted guard in each donor
    /// region and at most one field formation per owned region; no new men or equipment.
    /// Existing detached units keep their place as they march, rather than drawing another
    /// village's militia on every reassignment. Ordinary armies never consume local guards.
    /// </summary>
    public static List<Army> FieldArmy(Campaign c, int nationId)
    {
        var regulars = new List<Army>();
        FieldArmyInto(c, nationId, regulars);
        return regulars;
    }

    internal static void FieldArmyInto(Campaign c, int nationId, List<Army> regulars)
    {
        regulars.Clear();
        var militia = _emergencyMilitiaScratch ??= new List<Army>();
        militia.Clear();

        var allArmies = c.Armies;
        for (var i = 0; i < allArmies.Count; i++)
        {
            var army = allArmies[i];
            if (army.NationId != nationId) continue;
            if (army.TemplateId != MilitiaCore.TemplateId)
                regulars.Add(army);
            else if (army.EmbarkedFleetId < 0 && !army.DirectOrder && army.FrontLineId < 0)
                militia.Add(army);
        }

        var hasRegulars = regulars.Count > 0;
        var nation = c.NationById(nationId);
        if (nation is null || nation.Landless || militia.Count == 0) return;
        var limit = c.StatesOf(nation).Length;
        if (limit == 0) return;

        militia.Sort(CompareArmyById);

        HashSet<int> openWars = null;
        var detachedIds = _emergencyDetachedIdsScratch ??= new HashSet<int>();
        detachedIds.Clear();

        for (var i = 0; i < militia.Count; i++)
        {
            var army = militia[i];
            if (army.AssignedWarId < 0 || (!hasRegulars && regulars.Count >= limit)) continue;
            if (openWars is null)
            {
                openWars = _emergencyOpenWarsScratch ??= new HashSet<int>();
                openWars.Clear();
                var wars = Warfare.OpenWars(c);
                for (var wi = 0; wi < wars.Count; wi++)
                {
                    var w = wars[wi];
                    if (!w.Ended) openWars.Add(w.Id);
                }
            }
            if (openWars.Contains(army.AssignedWarId))
            {
                regulars.Add(army);
                detachedIds.Add(army.Id);
            }
        }

        if (hasRegulars) return;

        var byState = _emergencyByStateScratch ??= new Dictionary<int, (int Count, Army Candidate)>();
        byState.Clear();
        for (var i = 0; i < militia.Count; i++)
        {
            var army = militia[i];
            if (detachedIds.Contains(army.Id)) continue;
            var key = army.StateMapIndex;
            byState.TryGetValue(key, out var entry);
            entry.Count++;
            if (entry.Candidate is null && !army.InBattle && army.Route.Count == 0 && army.PlannedRoute.Count == 0)
                entry.Candidate = army;
            byState[key] = entry;
        }

        if (byState.Count == 0) return;

        var stateKeys = _emergencyStateKeysScratch ??= new List<int>();
        stateKeys.Clear();
        foreach (var kv in byState)
            if (kv.Key >= 0 && kv.Value.Count >= 2 && kv.Value.Candidate is not null)
                stateKeys.Add(kv.Key);
        stateKeys.Sort();

        for (var i = 0; i < stateKeys.Count; i++)
        {
            if (regulars.Count >= limit) break;
            regulars.Add(byState[stateKeys[i]].Candidate);
        }
    }

    internal static bool NeedsEmergencyStaff(Campaign c, StrategicWar war)
    {
        var candidates = _needsCandidatesScratch ??= new HashSet<int>();
        candidates.Clear();
        CollectAiParticipants(c, war, true, candidates);
        CollectAiParticipants(c, war, false, candidates);
        if (candidates.Count == 0) return false;

        var hasRegulars = _needsHasRegularsScratch ??= new HashSet<int>();
        var hasMilitia = _needsHasMilitiaScratch ??= new HashSet<int>();
        hasRegulars.Clear();
        hasMilitia.Clear();

        var armies = c.Armies;
        for (var i = 0; i < armies.Count; i++)
        {
            var army = armies[i];
            if (!candidates.Contains(army.NationId)) continue;
            if (army.TemplateId != MilitiaCore.TemplateId)
                hasRegulars.Add(army.NationId);
            else
                hasMilitia.Add(army.NationId);
        }

        foreach (var id in candidates)
            if (!hasRegulars.Contains(id) && hasMilitia.Contains(id))
                return true;
        return false;
    }

    private static void CollectAiParticipants(Campaign c, StrategicWar war, bool attacking, HashSet<int> candidates)
    {
        var leaderId = attacking ? war.AttackerId : war.DefenderId;
        if (leaderId >= 0 && c.NationById(leaderId) is { IsHuman: false }) candidates.Add(leaderId);
        var allies = attacking ? war.AttackerAllies : war.DefenderAllies;
        foreach (var id in allies)
        {
            if (c.NationById(id) is { IsHuman: false }) candidates.Add(id);
        }
    }
}
