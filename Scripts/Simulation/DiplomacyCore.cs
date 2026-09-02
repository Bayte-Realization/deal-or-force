using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public enum DiplomaticStance
{
    Neutral,
    Friendly,
    Allied,
    Rival,
    AtWar,
    Truce
}

public sealed class DiplomaticRelation
{
    public int A;
    public int B;
    public float Opinion;
    public DiplomaticStance Stance;
    public int TruceUntil;
    public int LastActionDay = -30;
    public bool TradeAgreement;
    public bool NonAggressionPact;

    public int Other(int nationId) => nationId == A ? B : A;
}

public static class CampaignDiplomacy
{
    public static DiplomaticRelation Get(Campaign campaign, int a, int b)
    {
        if (a == b || a < 0 || b < 0) return null;
        var low = Math.Min(a, b);
        var high = Math.Max(a, b);
        var key = ((long)low << 32) | (uint)high;
        if (campaign.Relations.TryGetValue(key, out var relation)) return relation;
        relation = new DiplomaticRelation { A = low, B = high };
        campaign.Relations[key] = relation;
        return relation;
    }

    public static bool AtWar(Campaign campaign, int a, int b) =>
        campaign.Wars.Any(war => !war.Ended &&
            ((war.AttackerId == a && war.DefenderId == b) || (war.AttackerId == b && war.DefenderId == a)));

    public static bool CanDeclareWar(Campaign campaign, int attackerId, int defenderId, out string reason)
    {
        reason = "";
        var attacker = campaign.NationById(attackerId);
        var defender = campaign.NationById(defenderId);
        if (attacker is null || defender is null || attacker == defender) { reason = "Invalid target."; return false; }
        if (AtWar(campaign, attackerId, defenderId)) { reason = "Already at war."; return false; }
        var relation = Get(campaign, attackerId, defenderId);
        if (relation.Stance == DiplomaticStance.Allied) { reason = "Break the alliance first."; return false; }
        if (relation.NonAggressionPact) { reason = "A non-aggression pact is active."; return false; }
        if (relation.TruceUntil > campaign.ElapsedDays) { reason = $"Truce: {relation.TruceUntil - campaign.ElapsedDays} days remain."; return false; }
        if (!SharesLandBorder(campaign, attacker.MapIndex, defender.MapIndex))
        {
            reason = "No shared land front; naval invasions are not ported yet.";
            return false;
        }
        return true;
    }

    public static StrategicWar DeclareWar(Campaign campaign, int attackerId, int defenderId, int claimedStateMapIndex = -1)
    {
        if (!CanDeclareWar(campaign, attackerId, defenderId, out _)) return null;
        var attacker = campaign.NationById(attackerId);
        var defender = campaign.NationById(defenderId);
        var relation = Get(campaign, attackerId, defenderId);
        relation.Stance = DiplomaticStance.AtWar;
        relation.Opinion = Math.Min(relation.Opinion, -60f);
        relation.LastActionDay = campaign.ElapsedDays;
        var war = new StrategicWar
        {
            Id = campaign.Wars.Count == 0 ? 0 : campaign.Wars.Max(other => other.Id) + 1,
            Name = $"{attacker.Name}–{defender.Name} War",
            AttackerId = attackerId,
            DefenderId = defenderId,
            StartedDay = campaign.ElapsedDays,
            ClaimedStateMapIndex = ResolveWarGoal(campaign, defender.MapIndex, claimedStateMapIndex)
        };
        campaign.Wars.Add(war);
        Warfare.RebuildFronts(campaign);
        FrontPlanner.ReassignAi(campaign, war);
        campaign.MarkStrategicChange();
        return war;
    }

    private static int ResolveWarGoal(Campaign campaign, int defenderMapIndex, int requestedState)
    {
        var selected = campaign.States.FirstOrDefault(state => state.MapIndex == requestedState);
        if (selected is not null && selected.NationMapIndex == defenderMapIndex) return selected.MapIndex;
        return campaign.States.Where(state => state.NationMapIndex == defenderMapIndex)
            .OrderByDescending(state => state.Population)
            .ThenBy(state => state.MapIndex)
            .FirstOrDefault()?.MapIndex ?? -1;
    }

    // Who borders whom. This used to be answered by walking all 37 807 provinces and
    // their neighbours for every question, which is fine when a player asks it once and
    // ruinous when an AI asks it twelve thousand times a year. It is built once per change
    // to the map and read from there.
    private static Dictionary<int, HashSet<int>> _borders;
    private static int _borderRevision = -1;

    public static bool SharesLandBorder(Campaign campaign, int mapA, int mapB) =>
        Borders(campaign).TryGetValue(mapA, out var set) && set.Contains(mapB);

    /// <summary>Every country each country can walk to.</summary>
    public static IReadOnlyCollection<int> Neighbours(Campaign campaign, int mapIndex) =>
        Borders(campaign).TryGetValue(mapIndex, out var set)
            ? set
            : (IReadOnlyCollection<int>)Array.Empty<int>();

    private static Dictionary<int, HashSet<int>> Borders(Campaign campaign)
    {
        if (_borders is not null && _borderRevision == campaign.MapRevision) return _borders;
        // Rebuilt whenever a province changes hands, which is most days - so the table and
        // its sets are emptied and refilled rather than thrown away and built again. The
        // scan has to happen; handing the collector a dictionary of two hundred and sixty
        // fresh hash sets every time it does was the AI's whole allocation cost.
        _borders ??= new Dictionary<int, HashSet<int>>();
        foreach (var set in _borders.Values) set.Clear();
        _borderRevision = campaign.MapRevision;

        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return _borders;

        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var owner = controllers[province];
            if (owner == 0) continue;
            foreach (var neighbour in topology.Neighbors[province])
            {
                var other = controllers[neighbour];
                if (other == 0 || other == owner) continue;
                if (!_borders.TryGetValue(owner, out var set)) _borders[owner] = set = new HashSet<int>();
                set.Add(other);
            }
        }
        return _borders;
    }
}

public sealed class ImproveRelationsCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public void Apply(Campaign campaign)
    {
        var relation = CampaignDiplomacy.Get(campaign, IssuerId, TargetId);
        var nation = campaign.NationById(IssuerId);
        if (relation is null || nation is null || campaign.ElapsedDays - relation.LastActionDay < 14 || nation.Treasury < 0.1f) return;
        nation.Treasury -= 0.1f;
        relation.Opinion = M.Clamp(relation.Opinion + 12f, -100f, 100f);
        relation.LastActionDay = campaign.ElapsedDays;
        if (relation.Opinion >= 35f && relation.Stance == DiplomaticStance.Neutral)
            relation.Stance = DiplomaticStance.Friendly;
    }
}

public sealed class DenounceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public void Apply(Campaign campaign)
    {
        var relation = CampaignDiplomacy.Get(campaign, IssuerId, TargetId);
        if (relation is null || campaign.ElapsedDays - relation.LastActionDay < 14) return;
        relation.Opinion = M.Clamp(relation.Opinion - 18f, -100f, 100f);
        relation.LastActionDay = campaign.ElapsedDays;
        if (relation.Opinion <= -35f && relation.Stance == DiplomaticStance.Neutral)
            relation.Stance = DiplomaticStance.Rival;
    }
}

public sealed class DeclareWarCampaignCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public int ClaimedStateMapIndex { get; init; } = -1;
    public void Apply(Campaign campaign) => CampaignDiplomacy.DeclareWar(campaign, IssuerId, TargetId, ClaimedStateMapIndex);
}
