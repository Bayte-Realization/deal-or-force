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

    /// <summary>
    /// Whether each side refuses to trade with the other. Directional, because sanctions
    /// usually are: the country imposing them is rarely the one that needs the goods.
    ///
    /// A is always the lower id - see <see cref="CampaignDiplomacy.Get"/> - so these are
    /// read through <see cref="EmbargoBy"/> rather than by remembering which is which.
    /// </summary>
    public bool EmbargoByA;
    public bool EmbargoByB;

    /// <summary>Whether each side has promised to fight for the other if it is attacked.</summary>
    public bool GuaranteeByA;
    public bool GuaranteeByB;

    public bool EmbargoBy(int nationId) => nationId == A ? EmbargoByA : EmbargoByB;
    public bool GuaranteedBy(int nationId) => nationId == A ? GuaranteeByA : GuaranteeByB;

    /// <summary>
    /// Why this country feels the way it does - see OpinionCore. <see cref="Opinion"/> is
    /// the sum of these, not a number anybody sets directly.
    /// </summary>
    public readonly List<OpinionReason> Reasons = new();

    /// <summary>The same sum, named part by name, for the screen that shows it.</summary>
    public readonly Breakdown OpinionDrivers = new();

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

    /// <summary>
    /// The relation between two countries, or null if they have never had one.
    ///
    /// <see cref="Get"/> creates on demand, which is right for anything that is about to
    /// write to it and disastrous for anything that only wants to look: the trade pass asks
    /// about a hundred thousand pairs a week, and creating a relation object for each would
    /// leave the world holding tens of thousands of empty relationships forever - every one
    /// of them then settled, weekly, by the opinion pass.
    /// </summary>
    public static DiplomaticRelation Peek(Campaign campaign, int a, int b)
    {
        if (a == b || a < 0 || b < 0) return null;
        var low = Math.Min(a, b);
        var high = Math.Max(a, b);
        return campaign.Relations.TryGetValue(((long)low << 32) | (uint)high, out var relation)
            ? relation
            : null;
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
            // Force can be projected across water by a country that has ships to project it
            // with. This used to be a flat refusal, which made every island in the world
            // permanently safe and left a navy with nothing to do.
            var carriers = 0;
            foreach (var fleet in campaign.Fleets)
                if (fleet.NationId == attackerId) carriers += fleet.Hulls;
            if (carriers <= 0)
            {
                reason = "No shared land front, and no fleet to reach them with.";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Declare a war, and pay for it with everybody who is not in it.
    ///
    /// The justification is what the attacker tells the world. Passing null means "work it
    /// out", which is what any caller that is not deliberately staging something wants:
    /// <see cref="Statecraft.CasusBelli"/> answers it out of state the game already keeps -
    /// land taken from us and not forgotten, our own people living on the other side of the
    /// border, a friend already being attacked. An empty answer is a war nobody accepts a
    /// reason for, and that is where notoriety comes from.
    /// </summary>
    public static StrategicWar DeclareWar(Campaign campaign, int attackerId, int defenderId,
        int claimedStateMapIndex = -1, string justification = null)
    {
        if (!CanDeclareWar(campaign, attackerId, defenderId, out _)) return null;
        var attacker = campaign.NationById(attackerId);
        var defender = campaign.NationById(defenderId);
        var relation = Get(campaign, attackerId, defenderId);
        relation.Stance = DiplomaticStance.AtWar;
        OpinionSystem.Remember(campaign, relation, "declared_war_on_us");
        OpinionSystem.Remember(campaign, relation, "fought_a_war");
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
        Statecraft.RecordDeclaration(campaign, attacker, defender,
            justification ?? Statecraft.CasusBelli(campaign, attacker, defender));
        Warfare.RebuildFronts(campaign);
        FrontPlanner.ReassignAi(campaign, war);
        campaign.MarkStrategicChange();

        // And everybody who promised to stand by the country just attacked is asked whether
        // they meant it. They answer with their own declarations, through this same path, so
        // nothing downstream has to learn about wars with more than two sides.
        Statecraft.CallToArms(campaign, war);
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
    private static int _borderDay = -1;

    /// <summary>
    /// Which campaign the cached table belongs to.
    ///
    /// It was cached on the day and the map revision alone, which is correct for exactly one
    /// campaign at a time and silently wrong the moment there are two: a second campaign on
    /// a different day with a coincidentally equal revision was handed the first one's
    /// borders, and one that happened to share the day was told nobody borders anybody. That
    /// is not a theoretical case - it is a multiplayer client rebuilding its world from a
    /// snapshot while the old one is still being collected, and it is every bench check that
    /// works on a campaign of its own.
    /// </summary>
    private static Campaign _borderCampaign;

    public static bool SharesLandBorder(Campaign campaign, int mapA, int mapB) =>
        Borders(campaign).TryGetValue(mapA, out var set) && set.Contains(mapB);

    /// <summary>Every country each country can walk to.</summary>
    public static IReadOnlyCollection<int> Neighbours(Campaign campaign, int mapIndex) =>
        Borders(campaign).TryGetValue(mapIndex, out var set)
            ? set
            : (IReadOnlyCollection<int>)Array.Empty<int>();

    /// <summary>How many times the border table has been rebuilt. Diagnostic only.</summary>
    public static int Rebuilds;

    private static Dictionary<int, HashSet<int>> Borders(Campaign campaign)
    {
        // At most once a day, and only if the map actually moved.
        //
        // This was keyed on the map revision alone, and the map revision changes every time
        // a province changes hands - seven or eight times a day in a world at war. So every
        // AI heuristic that asks who it borders was paying for a walk of all thirty-eight
        // thousand provinces, several times a day each. Measured in the running game that
        // was forty-five of the fifty milliseconds a campaign day cost, and it is why
        // pressing play gave five days a second instead of the thirty asked for.
        //
        // Nothing needs it fresher. Which countries a country touches is an input to
        // decisions it takes weekly and monthly; a day out of date cannot change one.
        if (_borders is not null && ReferenceEquals(_borderCampaign, campaign))
        {
            if (_borderDay == campaign.ElapsedDays) return _borders;
            if (_borderRevision == campaign.MapRevision)
            {
                _borderDay = campaign.ElapsedDays;
                return _borders;
            }
        }

        // The table and its sets are emptied and refilled rather than thrown away and
        // rebuilt: handing the collector a dictionary of two hundred and sixty fresh hash
        // sets each time was the AI's whole allocation cost.
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        // Nothing to build from. Do not stamp the cache - a campaign whose map has not been
        // attached yet would otherwise be told for the rest of the day that it has no
        // neighbours at all, which is exactly the sort of answer nobody thinks to doubt.
        if (topology is null || controllers is null) return _borders ??= new Dictionary<int, HashSet<int>>();

        Rebuilds++;
        _borders ??= new Dictionary<int, HashSet<int>>();
        foreach (var set in _borders.Values) set.Clear();
        _borderCampaign = campaign;
        _borderRevision = campaign.MapRevision;
        _borderDay = campaign.ElapsedDays;

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
        OpinionSystem.Remember(campaign, relation, "improved_relations");
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
        OpinionSystem.Remember(campaign, relation, "denounced");
        relation.LastActionDay = campaign.ElapsedDays;
        if (relation.Opinion <= -35f && relation.Stance == DiplomaticStance.Neutral)
            relation.Stance = DiplomaticStance.Rival;
    }
}

/// <summary>
/// Refuse to trade with somebody, or stop refusing.
///
/// The first thing between a stiff note and a declaration of war, and until trade became
/// bilateral there was no way to express it: with one anonymous world market there was
/// nobody to cut off. Now an embargo removes a real supplier from a real buyer, and since
/// the world's surplus of any one good is finite - rare earths sit at barely three per cent
/// above what the world uses - a large exporter refusing to sell is felt within the month.
///
/// It costs the country that imposes it too, which is why it is a decision rather than a
/// free move: the trade it gives up was also its own, and the other side remembers.
/// </summary>
public sealed class SetEmbargoCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public bool Embargo { get; init; } = true;

    public void Apply(Campaign campaign)
    {
        var relation = CampaignDiplomacy.Get(campaign, IssuerId, TargetId);
        if (relation is null) return;
        var already = relation.EmbargoBy(IssuerId);
        if (already == Embargo) return;
        if (IssuerId == relation.A) relation.EmbargoByA = Embargo;
        else relation.EmbargoByB = Embargo;
        relation.LastActionDay = campaign.ElapsedDays;
        if (Embargo) OpinionSystem.Remember(campaign, relation, "embargoed_us");
        else OpinionSystem.Forget(campaign, relation, "embargoed_us");
        campaign.MarkStrategicChange();
    }
}

/// <summary>
/// Promise to fight for somebody who has not asked you to be their ally.
///
/// A guarantee is the cheap half of an alliance and the useful half of being a great power:
/// it commits nothing until somebody attacks the country guaranteed, and then it commits
/// everything. It is what lets a large country make a small one not worth invading without
/// tying its own foreign policy to it.
/// </summary>
public sealed class GuaranteeCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public bool Guarantee { get; init; } = true;

    public void Apply(Campaign campaign)
    {
        var relation = CampaignDiplomacy.Get(campaign, IssuerId, TargetId);
        if (relation is null || relation.Stance == DiplomaticStance.AtWar) return;
        if (relation.GuaranteedBy(IssuerId) == Guarantee) return;
        if (IssuerId == relation.A) relation.GuaranteeByA = Guarantee;
        else relation.GuaranteeByB = Guarantee;
        relation.LastActionDay = campaign.ElapsedDays;
        if (Guarantee) OpinionSystem.Remember(campaign, relation, "guaranteed_us");
        else OpinionSystem.Forget(campaign, relation, "guaranteed_us");
        campaign.MarkStrategicChange();
    }
}

public sealed class DeclareWarCampaignCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public int ClaimedStateMapIndex { get; init; } = -1;
    public void Apply(Campaign campaign) => CampaignDiplomacy.DeclareWar(campaign, IssuerId, TargetId, ClaimedStateMapIndex);
}
