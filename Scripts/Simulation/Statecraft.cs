using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// A group of countries that have agreed the same country has taken enough.
///
/// Paradox calls it a coalition and it is the single most important thing a grand strategy
/// map has, because it is the only force that grows in proportion to success. Without one,
/// the country that wins its first war is stronger for the second, and stronger again for
/// the third, and the map converges on whoever started largest - which is what this world
/// did: a hundred wars in two years and thirty-eight provinces annexed by the same
/// handful of countries, with nothing anywhere pushing back.
/// </summary>
public sealed class Coalition
{
    public int TargetId = -1;
    public int FormedDay;
    public readonly List<int> MemberIds = new();
}

public sealed partial class Campaign
{
    /// <summary>Countries the world has agreed to contain, and who agreed it.</summary>
    public readonly List<Coalition> Coalitions = new();
}

public sealed partial class Nation
{
    /// <summary>
    /// How much the rest of the world minds what this country has been doing.
    ///
    /// It rises when land changes hands by force and when a war is declared with no reason
    /// anybody accepts, and it falls slowly with nothing but time and good behaviour. It is
    /// the price of conquest, and until now conquest had no price at all: every province
    /// taken made the next one easier to take.
    /// </summary>
    public float Notoriety;

    /// <summary>The reason given for the last war this country started, for the record.</summary>
    public string LastWarJustification = "";
}

/// <summary>
/// What countries do to each other short of fighting, and what fighting costs them with
/// everyone who is not fighting.
///
/// Diplomacy here was opinion, a stance, and two buttons. Alliances existed as a word: the
/// stance could be Allied, nothing anywhere read it, and a country could watch its ally be
/// dismembered without so much as a note. A guarantee could not be given. A war needed no
/// reason and cost nothing with third parties. Conquest had no price, so the strong got
/// stronger and the map converged.
///
/// Four things fix that, and they are all Paradox's, because Paradox got them right.
///
/// A reason for war, which a country either has or is seen not to have. Notoriety, which is
/// what taking things costs you with everybody who was not involved. Coalitions, the only
/// force on the map that grows in proportion to somebody else's success. And alliances and
/// guarantees that are actually honoured - or visibly broken, which is worse for the country
/// that breaks them and far more interesting than a stance nobody reads.
/// </summary>
public static class Statecraft
{
    /// <summary>Notoriety at which the neighbourhood starts organising against a country.</summary>
    public const float CoalitionThreshold = 42f;

    /// <summary>Notoriety at which a coalition disbands again.</summary>
    public const float CoalitionRelief = 24f;

    /// <summary>How much notoriety a year of quiet removes.</summary>
    public const float NotorietyYearlyDecay = 11f;

    private static readonly List<int> Candidates = new();
    private static readonly List<Coalition> Dissolved = new();

    /// <summary>
    /// Whether allies are already being called in.
    ///
    /// An ally answers by declaring its own war, which would otherwise call in that war's
    /// allies, and so on around the ring until everybody on the map is fighting everybody
    /// else. One level of escalation is what a call to arms is; the rest is a stack
    /// overflow with a flag on it.
    /// </summary>
    private static bool _answering;

    public static void Tick(Campaign campaign)
    {
        if (campaign is null) return;

        // Time is the only thing that forgets, and it forgets slowly.
        var decay = NotorietyYearlyDecay / 365f;
        foreach (var nation in campaign.Nations)
            if (nation.Notoriety > 0f) nation.Notoriety = MathF.Max(0f, nation.Notoriety - decay);

        // The rest is a weekly question. Coalitions are not formed on a Tuesday afternoon.
        if (campaign.ElapsedDays % 7 != 0) return;
        RememberNotoriety(campaign);
        ReviewCoalitions(campaign);
    }

    /// <summary>
    /// Everybody who has an opinion of a notorious country revises it.
    ///
    /// Only relations that already exist are touched, deliberately: creating one for every
    /// pair in the world would leave sixty-eight thousand relationships to settle weekly,
    /// and a country nobody has ever dealt with has no opinion to revise.
    /// </summary>
    private static void RememberNotoriety(Campaign campaign)
    {
        foreach (var relation in campaign.Relations.Values)
        {
            var a = campaign.NationById(relation.A);
            var b = campaign.NationById(relation.B);
            if (a is null || b is null) continue;
            var worst = MathF.Max(a.Notoriety, b.Notoriety);
            if (worst < 8f) OpinionSystem.Forget(campaign, relation, "notoriety");
            else OpinionSystem.Remember(campaign, relation, "notoriety", M.Clamp(worst / 60f, 0.1f, 2.0f));
        }
    }

    /// <summary>
    /// Form a coalition against anybody the neighbourhood has had enough of, and dissolve
    /// one whose target has been quiet long enough to be forgiven.
    /// </summary>
    private static void ReviewCoalitions(Campaign campaign)
    {
        Dissolved.Clear();
        foreach (var coalition in campaign.Coalitions)
        {
            var target = campaign.NationById(coalition.TargetId);
            if (target is null || target.Notoriety < CoalitionRelief || target.Population <= 0)
                Dissolved.Add(coalition);
        }
        foreach (var gone in Dissolved)
        {
            foreach (var memberId in gone.MemberIds)
            foreach (var otherId in gone.MemberIds)
            {
                if (memberId >= otherId) continue;
                var relation = CampaignDiplomacy.Peek(campaign, memberId, otherId);
                if (relation is not null) OpinionSystem.Forget(campaign, relation, "coalition_partner");
            }
            campaign.Coalitions.Remove(gone);
        }

        foreach (var nation in campaign.Nations)
        {
            if (nation.Notoriety < CoalitionThreshold || nation.Population <= 0) continue;
            if (CoalitionAgainst(campaign, nation.Id) is not null) continue;

            // Who joins: everyone who can see them and has reason to worry. A country that
            // is already their ally does not, and neither does a country too far away to be
            // next - a coalition is neighbours, not a petition.
            Candidates.Clear();
            foreach (var mapIndex in CampaignDiplomacy.Neighbours(campaign, nation.MapIndex))
            {
                var neighbour = NationByMapIndex(campaign, mapIndex);
                if (neighbour is null || neighbour.Id == nation.Id || neighbour.Population <= 0) continue;
                var relation = CampaignDiplomacy.Peek(campaign, neighbour.Id, nation.Id);
                if (relation is { Stance: DiplomaticStance.Allied }) continue;
                if (relation is not null && relation.Opinion > 30f) continue;
                Candidates.Add(neighbour.Id);
            }
            if (Candidates.Count < 2) continue;

            var coalition = new Coalition { TargetId = nation.Id, FormedDay = campaign.ElapsedDays };
            Candidates.Sort();
            coalition.MemberIds.AddRange(Candidates);
            campaign.Coalitions.Add(coalition);

            // Containing the same country is the oldest reason two governments have ever
            // found to get on with each other.
            foreach (var memberId in coalition.MemberIds)
            foreach (var otherId in coalition.MemberIds)
            {
                if (memberId >= otherId) continue;
                OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, memberId, otherId),
                    "coalition_partner");
            }
            campaign.MarkStrategicChange();
        }
    }

    public static Coalition CoalitionAgainst(Campaign campaign, int nationId)
    {
        foreach (var coalition in campaign.Coalitions)
            if (coalition.TargetId == nationId) return coalition;
        return null;
    }

    /// <summary>How many countries stand ready to answer an attack on this one.</summary>
    public static int DefendersOf(Campaign campaign, Nation defender)
    {
        var count = 0;
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.A != defender.Id && relation.B != defender.Id) continue;
            var otherId = relation.Other(defender.Id);
            var other = campaign.NationById(otherId);
            if (other is null || other.Population <= 0) continue;
            if (relation.Stance == DiplomaticStance.Allied || relation.GuaranteedBy(otherId)) count++;
        }
        return count;
    }

    // ------------------------------------------------------------ a reason for war -----

    /// <summary>
    /// What a country would tell the world it was fighting for.
    ///
    /// An empty string means it has nothing to say, and a war declared with nothing to say
    /// is the expensive kind: everybody's opinion of you falls, your own people mind, and
    /// the world starts counting. Three things count as a reason here, and each of them is
    /// answered out of state the game already keeps rather than a flag somebody sets.
    /// </summary>
    public static string CasusBelli(Campaign campaign, Nation attacker, Nation defender)
    {
        if (campaign is null || attacker is null || defender is null) return "";
        var relation = CampaignDiplomacy.Peek(campaign, attacker.Id, defender.Id);

        // They took something of ours, and we have not forgotten. The opinion system has
        // been remembering exactly this since the day it was written.
        if (relation is not null)
            foreach (var reason in relation.Reasons)
                if (string.Equals(reason.Id, "took_our_land", StringComparison.OrdinalIgnoreCase))
                    return "reconquest";

        // Our people live there. Answered out of the demography rather than a claim flag,
        // so it is true of exactly the regions where it is true.
        var topology = campaign.ProvinceMap;
        if (topology is not null)
            foreach (var state in campaign.States)
            {
                if (state.NationMapIndex != defender.MapIndex) continue;
                if (!CampaignDiplomacy.SharesLandBorder(campaign, attacker.MapIndex, defender.MapIndex)) break;
                var ours = 0L;
                foreach (var group in state.PopulationGroups)
                    if (DemographyCatalog.IsPrimary(attacker.Tag, group.Culture)) ours += group.Size;
                if (ours * 2 > state.Population && state.Population > 0) return "irredentism";
            }

        // They are already fighting somebody we are bound to. Joining a war on the side of
        // an ally is the least controversial reason there is.
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(defender.Id)) continue;
            var victimId = war.AttackerId == defender.Id ? war.DefenderId : -1;
            if (victimId < 0) continue;
            var bond = CampaignDiplomacy.Peek(campaign, attacker.Id, victimId);
            if (bond is not null && (bond.Stance == DiplomaticStance.Allied || bond.GuaranteedBy(attacker.Id)))
                return "defence_of_an_ally";
        }

        return "";
    }

    /// <summary>
    /// What a war costs its instigator with everybody who is not in it.
    ///
    /// Called the moment a war is declared. A justified war is nearly free here and paid for
    /// in blood instead; an unjustified one is where notoriety comes from, and notoriety is
    /// what eventually brings a coalition.
    /// </summary>
    public static void RecordDeclaration(Campaign campaign, Nation attacker, Nation defender, string justification)
    {
        if (attacker is null || defender is null) return;
        attacker.LastWarJustification = justification ?? "";
        if (!string.IsNullOrEmpty(justification)) return;

        var defines = campaign.Defines;
        attacker.Notoriety = MathF.Min(100f, attacker.Notoriety + defines.NotorietyPerUnjustifiedWar);
        attacker.Stability = M.Clamp01(attacker.Stability - defines.UnjustifiedWarStabilityCost);
        attacker.WarSupport = M.Clamp01(attacker.WarSupport - defines.UnjustifiedWarSupportCost);
    }

    /// <summary>
    /// What taking land costs its taker with everybody who did not lose any.
    ///
    /// Scaled by how many people changed hands rather than how many provinces, because a
    /// hundred square kilometres of tundra is not what makes the neighbours nervous.
    /// </summary>
    public static void RecordAnnexation(Campaign campaign, Nation taker, long peopleTaken)
    {
        if (campaign is null || taker is null || peopleTaken <= 0) return;
        var millions = peopleTaken / 1_000_000f;
        taker.Notoriety = MathF.Min(100f,
            taker.Notoriety + campaign.Defines.NotorietyPerMillionTaken * millions);
    }

    // ------------------------------------------------------------ standing by a friend --

    /// <summary>
    /// Everybody bound to the country just attacked, asked whether they meant it.
    ///
    /// This is the piece the map most needed. Alliances existed as a stance that nothing
    /// read: a country could watch its ally dismembered without a note, so there was no such
    /// thing as a strong small country and no reason to court anyone. Now an attack on one
    /// is answered by the others - each of them separately, through the ordinary declaration
    /// path, so a war with a well-connected country is three wars and the engine needs to
    /// know nothing new about it.
    ///
    /// And a country that will not answer loses the alliance publicly, which is both the
    /// Paradox rule and the more interesting outcome: the promise that is never tested is
    /// worth nothing, and the one that is broken is worth less than nothing.
    /// </summary>
    public static int CallToArms(Campaign campaign, StrategicWar war)
    {
        if (campaign is null || war is null || war.Ended || _answering) return 0;
        var attacker = campaign.NationById(war.AttackerId);
        var defender = campaign.NationById(war.DefenderId);
        if (attacker is null || defender is null) return 0;

        Candidates.Clear();
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.A != defender.Id && relation.B != defender.Id) continue;
            var friendId = relation.Other(defender.Id);
            if (friendId == attacker.Id) continue;
            if (relation.Stance != DiplomaticStance.Allied && !relation.GuaranteedBy(friendId)) continue;
            Candidates.Add(friendId);
        }

        // And the coalition, if the attacker is what it was formed about.
        var coalition = CoalitionAgainst(campaign, attacker.Id);
        if (coalition is not null)
            foreach (var memberId in coalition.MemberIds)
                if (memberId != defender.Id && !Candidates.Contains(memberId))
                    Candidates.Add(memberId);

        Candidates.Sort();
        // The list is a shared buffer and the declarations below re-enter this method's
        // neighbours; copy it before anything else is allowed to touch it.
        var summoned = Candidates.ToArray();
        var joined = 0;
        _answering = true;
        try
        {
        foreach (var friendId in summoned)
        {
            var friend = campaign.NationById(friendId);
            if (friend is null || friend.Population <= 0) continue;
            var bond = CampaignDiplomacy.Get(campaign, friendId, defender.Id);

            if (WillAnswer(campaign, friend, defender, attacker, bond))
            {
                if (CampaignDiplomacy.DeclareWar(campaign, friendId, attacker.Id, -1, "defence_of_an_ally") is null)
                    continue;
                joined++;
                OpinionSystem.Remember(campaign, bond, "stood_by_us");
                continue;
            }

            // Refused. The alliance is over, and everybody knows why.
            if (bond.Stance == DiplomaticStance.Allied)
            {
                bond.Stance = DiplomaticStance.Neutral;
                OpinionSystem.Forget(campaign, bond, "alliance");
            }
            if (bond.GuaranteedBy(friendId))
            {
                if (friendId == bond.A) bond.GuaranteeByA = false;
                else bond.GuaranteeByB = false;
                OpinionSystem.Forget(campaign, bond, "guaranteed_us");
            }
            OpinionSystem.Remember(campaign, bond, "abandoned_us");
            friend.Stability = M.Clamp01(friend.Stability - campaign.Defines.BrokenPromiseStabilityCost);
        }
        }
        finally { _answering = false; }
        return joined;
    }

    /// <summary>
    /// Whether a country honours a promise it made in easier times.
    ///
    /// A player is never dragged in without being asked - their answer arrives as an offer
    /// they can refuse, the same way a peace does - so this is only ever the computer
    /// deciding, and it decides the way a government would: it keeps a promise it can afford
    /// to keep, to a friend it still has, against an enemy it can survive.
    /// </summary>
    private static bool WillAnswer(Campaign campaign, Nation friend, Nation defender, Nation attacker,
        DiplomaticRelation bond)
    {
        if (friend.IsHuman) return false;
        if (CampaignDiplomacy.AtWar(campaign, friend.Id, attacker.Id)) return false;
        if (!CampaignDiplomacy.CanDeclareWar(campaign, friend.Id, attacker.Id, out _)) return false;

        // Already fighting a war of its own is the commonest and most honest excuse.
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(friend.Id)) return false;

        var willingness = bond.Opinion / 100f;
        if (bond.Stance == DiplomaticStance.Allied) willingness += 0.55f;
        if (bond.GuaranteedBy(friend.Id)) willingness += 0.45f;
        willingness += M.Clamp(friend.WarSupport - 0.5f, -0.4f, 0.4f);
        willingness += M.Clamp(friend.Stability - 0.5f, -0.3f, 0.3f);

        // And the arithmetic. Nobody honours a promise into a war they cannot survive.
        var mine = AiStatecraft.StrengthOf(campaign, friend);
        var theirs = CampaignIntel.EstimatedStrength(campaign, friend.Id, attacker);
        willingness += M.Clamp(mine / MathF.Max(0.01f, theirs) - 0.75f, -0.8f, 0.8f);

        // A coalition member answers more readily; that is what it joined for.
        if (CoalitionAgainst(campaign, attacker.Id) is { } coalition && coalition.MemberIds.Contains(friend.Id))
            willingness += 0.35f;

        return willingness > 0.5f;
    }

    private static Nation NationByMapIndex(Campaign campaign, int mapIndex)
    {
        foreach (var nation in campaign.Nations)
            if (nation.MapIndex == mapIndex) return nation;
        return null;
    }
}
