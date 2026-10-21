using System;
using System.Collections.Generic;
using System.Linq;

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
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(Statecraft))) return;
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
            var existingCoalition = CoalitionAgainst(campaign, nation.Id);

            // Who joins: everyone who can see them and has reason to worry. A country that
            // is already their ally does not, and neither does a country too far away to be
            // next - a coalition is neighbours, not a petition.
            Candidates.Clear();
            var landNeighbours = CampaignDiplomacy.Neighbours(campaign, nation.MapIndex).ToHashSet();
            foreach (var other in campaign.Nations)
            {
                if (other.Id == nation.Id || other.Population <= 0 || other.IsSubject) continue;
                if (!landNeighbours.Contains(other.MapIndex) && AllianceSystem.CapitalKm(other, nation) > 1500f) continue;
                var relation = CampaignDiplomacy.Peek(campaign, other.Id, nation.Id);
                if (relation is { Stance: DiplomaticStance.Allied }) continue;
                if (relation is not null && relation.Opinion > 30f) continue;
                Candidates.Add(other.Id);
            }
            if (Candidates.Count < 2) continue;

            if (existingCoalition is null)
            {
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
            else
            {
                var changed = false;
                foreach (var candidate in Candidates)
                {
                    if (!existingCoalition.MemberIds.Contains(candidate))
                    {
                        existingCoalition.MemberIds.Add(candidate);
                        changed = true;
                        foreach (var otherId in existingCoalition.MemberIds)
                        {
                            if (candidate == otherId) continue;
                            OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, candidate, otherId),
                                "coalition_partner");
                        }
                    }
                }
                if (changed)
                {
                    existingCoalition.MemberIds.Sort();
                    campaign.MarkStrategicChange();
                }
            }
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
    private static bool IsMonarchy(string government) =>
        government is "absolute_monarchy" or "constitutional_monarchy";

    private static bool InCivilWar(Campaign campaign, Nation nation)
    {
        foreach (var war in campaign.Wars)
            if (war is { Ended: false, CivilConflict: true } && war.Involves(nation.Id)) return true;
        return false;
    }

    public static string CasusBelli(Campaign campaign, Nation attacker, Nation defender)
    {
        if (campaign is null || attacker is null || defender is null) return "";
        if (TerritorialClaims.HoldsOurHomeland(campaign, attacker, defender)) return "reconquest";
        var relation = CampaignDiplomacy.Peek(campaign, attacker.Id, defender.Id);

        // They took something of ours, and we have not forgotten. The opinion system has
        // been remembering exactly this since the day it was written.
        if (relation is not null)
            foreach (var reason in relation.Reasons)
            {
                if (string.Equals(reason.Id, "took_our_land", StringComparison.OrdinalIgnoreCase))
                    return "reconquest";

                // They were told to hand a region over and said no. The demand and the
                // refusal are both remembered; this is the refusal doing its work.
                if (string.Equals(reason.Id, "refused_our_ultimatum", StringComparison.OrdinalIgnoreCase))
                    return "ultimatum_refusal";
            }

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
        if (AllyUnderAttack(campaign, attacker, defender) is not null) return "defence_of_an_ally";

        // Months of shooting across the border: the clashes are the war's first act, and the
        // border war that may come of them is the second (Escalation).
        if (relation is not null && Escalation.StageOf(relation) == TensionStage.Clashes &&
            campaign.ElapsedDays - relation.TensionStageDay >= campaign.Defines.AiClashesCauseAfterDays)
            return "border_clashes";

        // They are fighting a rising we are already arming: going in is the end of a road the
        // money started down (ProxyWars).
        foreach (var backing in campaign.Sponsorships)
        {
            if (backing.Ended || backing.SponsorId != attacker.Id) continue;
            var war = campaign.Wars.Find(w => w.Id == backing.WarId);
            if (war is { Ended: false, CivilConflict: true } && war.Opposes(backing.ClientId, defender.Id))
                return "intervention";
        }

        // A crown in crisis while a cousin of the same house sits on a neighbouring throne:
        // the oldest reason in the book, and the one the twentieth century closed. Dormant
        // in 2024 - no shared house is in crisis - and one row of data away for any older
        // age (DynastyCatalog). The peace that ends such a war restores a personal union
        // rather than annexing; see AiPeaceOutcomes.
        if (campaign.Defines.DynasticClaimsEnabled &&
            IsMonarchy(attacker.Government) && IsMonarchy(defender.Government) &&
            DynastyCatalog.Shares(attacker.Tag, defender.Tag) &&
            (defender.Stability < campaign.Defines.DynasticClaimStabilityMax || InCivilWar(campaign, defender)))
            return "dynastic_claim";

        // And then everything else the two of them remember, weighed the way the opinion panel
        // already weighs it.
        //
        // Until now only two of the things a country can hold against another were ever read here
        // - land taken and an ultimatum refused - so a government whose non-aggression pact had
        // been torn up, whose trade had been cut off, whose rebels had been armed and whose
        // waters had been fished by the same neighbour still had, in its own estimation, "nothing
        // to say", and a war it badly wanted could not be begun. Measured over five years of the
        // world: eighty-eight wars worth having, sixty-five of them dropped for want of a reason,
        // and not one declaration. The reasons were all there, in the opinion the game has been
        // keeping since the day it was written. Nothing was reading them.
        //
        // The gravest single grievance, not the sum of them: a decade of small slights is not a
        // reason for a war, and a country that could add up denunciations and moved border markers
        // into a declaration would be declaring wars nobody could explain. The weight is the
        // modifier's own, from data, so how deep a grudge has to go before it is worth a war is a
        // decision a mod can make - see <see cref="Defines.CasusBelliGrudgeFloor"/>.
        if (relation is not null)
        {
            var gravest = "";
            var gravestWeight = 0f;
            foreach (var reason in relation.Reasons)
            {
                var definition = OpinionCatalog.Find(reason.Id);
                if (definition is null || definition.Value.Opinion >= 0f) continue;
                // Doing it twice is worse than doing it once, but the fifth time is not five
                // times worse than the first: a grudge shared between clauses of the opinion
                // panel should not quietly become a war aim by repetition alone.
                var weight = definition.Value.Opinion * M.Min(2f, Math.Max(1, reason.Stacks));
                if (weight >= gravestWeight) continue;
                gravestWeight = weight;
                gravest = reason.Id;
            }
            if (!string.IsNullOrEmpty(gravest) && gravestWeight <= campaign.Defines.CasusBelliGrudgeFloor)
                return gravest;
        }

        // A state much stronger than a small neighbour can simply take it. This is not a
        // grievance and it is not free: RecordDeclaration still records the act, and the
        // notoriety and the coalition that follows are the price. It is a reason the strong
        // have always given, and without it the AI could want a conquest it was never
        // allowed to name.
        if (AnnexationAmbition.HegemonyCause(campaign, attacker, defender)) return "hegemony";

        return "";
    }

    /// <summary>
    /// The ally or ward of ours the defender is attacking, if any.
    ///
    /// Not in a civil war. A state that has gone to war with its own rebels has not attacked
    /// anybody's ally, whatever the rebels have signed: a bond China had made with one of the
    /// powers fighting the junta was read as Myanmar attacking China's ally, and China invaded
    /// Myanmar within a year in every run. A patron of rebels arms them (ProxyWars); it does not
    /// get to march in on their behalf.
    /// </summary>
    public static Nation AllyUnderAttack(Campaign campaign, Nation attacker, Nation defender)
    {
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || war.CivilConflict || !war.Involves(defender.Id)) continue;
            var victimId = war.AttackerId == defender.Id ? war.DefenderId : -1;
            if (victimId < 0) continue;
            var bond = CampaignDiplomacy.Peek(campaign, attacker.Id, victimId);
            if (bond is not null && (bond.Stance == DiplomaticStance.Allied || bond.GuaranteedBy(attacker.Id)))
                return campaign.NationById(victimId);
        }
        return null;
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
        if (!war.CivilConflict && !war.Limited)
            foreach (var partner in InternationalOrganizations.DefencePartners(campaign, defender.Id))
                if (partner != attacker.Id && !Candidates.Contains(partner)) Candidates.Add(partner);
        // A civil war is not what an alliance is for. The friends of either side back it -
        // money, weapons, volunteers (ProxyWars) - and only a state the side answers to, or
        // that answers to it, fights in it: the United States was not at war in Sudan, and a
        // Sudan that made peace by becoming an American client state was the alliance table
        // fighting somebody else's civil war for them.
        foreach (var relation in war.CivilConflict || war.Limited ? Enumerable.Empty<DiplomaticRelation>() : campaign.Relations.Values)
        {
            if (relation.A != defender.Id && relation.B != defender.Id) continue;
            var friendId = relation.Other(defender.Id);
            if (friendId == attacker.Id) continue;
            if (relation.Stance != DiplomaticStance.Allied && !relation.GuaranteedBy(friendId)) continue;
            Candidates.Add(friendId);
        }

        // Every dependency of the defender that is obliged to come. Checked here rather
        // than in SubjectSystem because this is the list of everybody who answers a war, and
        // a dependency answering one is not a special case.
        //
        // The attacker's own subjects are NOT in this list, and the reason is worth writing
        // down: everybody in it declares war on the attacker. A subject dragged along by its
        // overlord declares on the overlord's enemy instead, which is the opposite direction
        // and is done at the end of this method.
        foreach (var nation in campaign.Nations)
        {
            if (!nation.AnswersTo(defender.Id) || nation.Id == attacker.Id) continue;
            if (SubjectCatalog.Of(nation.SubjectType) is not { JoinsWars: true }) continue;
            if (!Candidates.Contains(nation.Id)) Candidates.Add(nation.Id);
        }

        // And the coalition, if the attacker is what it was formed about.
        var coalition = war.CivilConflict || war.Limited ? null : CoalitionAgainst(campaign, attacker.Id);
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
            // A human decides from the war/organization screen. Not answering an automatic
            // AI call is not a refusal, and must not instantly punish their institutions.
            if (friend.IsHuman) continue;
            var bond = CampaignDiplomacy.Get(campaign, friendId, defender.Id);

            if (WillAnswer(campaign, friend, defender, attacker, bond))
            {
                // Into the war that is already happening, on the side of the country that
                // asked. A separate declaration would be a separate war with its own score
                // and its own peace, which is exactly what a coalition is not.
                if (!CampaignDiplomacy.JoinWar(campaign, war, friendId, defending: true,
                        "declared_war_on_us"))
                    continue;
                joined++;
                OpinionSystem.Remember(campaign, bond, "stood_by_us");
                continue;
            }

            // Refused. The alliance is over, and everybody knows why.
            foreach (var org in InternationalOrganizations.Memberships(campaign, defender.Id))
                if (org.Members.Contains(friendId) && InternationalOrganizations.Charter(org.CharterId)?.Defence == true)
                    org.Cohesion = Math.Max(0, org.Cohesion - .08f);
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

        // And the attacker's own, dragged along. Not a call and not a choice: the kind they
        // answer under says they come, so they declare on the country their overlord just
        // declared on. A subject a player is steering is left out, because the rule that a
        // player is never brought into a war without being asked outranks the arrangement -
        // it arrives as an offer instead, the same way answering an ally does.
        // The other direction first, because it matters more: attacking somebody's
        // dependency is attacking them. Not run through WillAnswer - an overlord defending
        // what it holds is not keeping a promise it weighed up, and the kind of arrangement
        // is what decides whether it comes at all.
        if (defender.IsSubject && defender.OverlordId != attacker.Id &&
            SubjectCatalog.Of(defender.SubjectType) is { OverlordDefends: true } &&
            campaign.NationById(defender.OverlordId) is { } master &&
            (campaign.Player is null || campaign.Player.Id != master.Id) &&
            !CampaignDiplomacy.AtWar(campaign, master.Id, attacker.Id) &&
            CampaignDiplomacy.JoinWar(campaign, war, master.Id, defending: true, "declared_war_on_us"))
            joined++;

        foreach (var nation in campaign.Nations.ToArray())
        {
            if (!nation.AnswersTo(attacker.Id) || nation.Id == defender.Id) continue;
            if (SubjectCatalog.Of(nation.SubjectType) is not { DraggedIntoWars: true }) continue;
            if (campaign.Player is not null && campaign.Player.Id == nation.Id) continue;
            if (CampaignDiplomacy.AtWar(campaign, nation.Id, defender.Id)) continue;
            // Beside its overlord, in its overlord's war. The direction is the other one -
            // it declares on the country the overlord declared on - so it is the attacking
            // side of the same war rather than a war of its own.
            if (CampaignDiplomacy.JoinWar(campaign, war, nation.Id, defending: false,
                    "dragged_by_our_overlord"))
                joined++;
        }
        }
        finally { _answering = false; }
        return joined;
    }

    /// <summary>
    /// Everybody this country could ask to join a war it is already fighting.
    ///
    /// Allies and guarantors, and nobody who is in the war already. Whether any of them
    /// would actually come is <see cref="CallRefusal"/> and then their own judgement - this
    /// is only the list the screen offers.
    /// </summary>
    public static List<int> CallableAllies(Campaign campaign, StrategicWar war, int callerId)
    {
        var result = new List<int>();
        if (campaign is null || war is null || war.Ended || !war.Involves(callerId)) return result;
        if (!war.CivilConflict && !war.Limited && war.IsDefending(callerId))
            foreach (var partner in InternationalOrganizations.DefencePartners(campaign, callerId))
                if (!war.Involves(partner)) result.Add(partner);
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.A != callerId && relation.B != callerId) continue;
            var friendId = relation.Other(callerId);
            if (war.Involves(friendId)) continue;
            if (relation.Stance != DiplomaticStance.Allied && !relation.GuaranteedBy(friendId)) continue;
            if (campaign.NationById(friendId) is null) continue;
            if (!result.Contains(friendId)) result.Add(friendId);
        }

        // And the dependencies that come when they are told to, which is most of what
        // having one is for.
        foreach (var nation in campaign.Nations)
        {
            if (!nation.AnswersTo(callerId) || war.Involves(nation.Id)) continue;
            if (SubjectCatalog.Of(nation.SubjectType) is not { DraggedIntoWars: true }) continue;
            if (!result.Contains(nation.Id)) result.Add(nation.Id);
        }
        result.Sort();
        return result;
    }

    /// <summary>Why this country cannot be asked at all, or an empty string when it can.</summary>
    public static string CallRefusal(Campaign campaign, StrategicWar war, int callerId, int allyId)
    {
        if (campaign is null || war is null || war.Ended) return Loc.Get("That war is over.");
        if (!war.Involves(callerId)) return Loc.Get("You are not in that war.");
        var ally = campaign.NationById(allyId);
        if (ally is null) return Loc.Get("Invalid target.");
        if (war.Involves(allyId)) return Loc.Format("{0} is already in this war.", ally.Name);

        // Somebody they are not allowed to fight. The ordinary refusals - a truce, a pact,
        // an ocean and no ships - are exactly the ones that apply here.
        foreach (var enemyId in war.EnemiesOf(callerId))
        {
            if (!CampaignDiplomacy.CanDeclareWar(campaign, allyId, enemyId, out var why, onOwnInitiative: false))
            {
                var enemy = campaign.NationById(enemyId);
                return Loc.Format("{0} cannot join against {1}: {2}", ally.Name, enemy?.Name ?? "?", why);
            }
        }
        return "";
    }

    /// <summary>
    /// Put the question, and take the answer.
    ///
    /// A dependency that comes when it is told to is not asked. Everybody else weighs it the
    /// same way they weigh a defensive call, with the promise itself worth rather less -
    /// somebody else's offensive war is not what an alliance is for, and an ally that comes
    /// anyway is doing you a favour.
    /// </summary>
    public static bool AskAllyToJoin(Campaign campaign, StrategicWar war, int callerId, int allyId,
        out string answer)
    {
        answer = CallRefusal(campaign, war, callerId, allyId);
        if (answer.Length > 0) return false;

        var caller = campaign.NationById(callerId);
        var ally = campaign.NationById(allyId);
        var bond = CampaignDiplomacy.Get(campaign, callerId, allyId);
        var defending = war.IsDefending(callerId);
        var enemy = campaign.NationById(war.EnemyLeaderOf(callerId));

        // Told rather than asked.
        var obliged = ally.AnswersTo(callerId) &&
                      SubjectCatalog.Of(ally.SubjectType) is { DraggedIntoWars: true } &&
                      !ally.IsHuman;

        var willing = obliged || (!ally.IsHuman && enemy is not null &&
                                  WillAnswer(campaign, ally, caller, enemy, bond, favour: !defending));
        if (willing)
        {
            if (!CampaignDiplomacy.JoinWar(campaign, war, allyId, defending, "declared_war_on_us"))
            {
                answer = Loc.Format("{0} could not join.", ally.Name);
                return false;
            }
            OpinionSystem.Remember(campaign, bond, "stood_by_us");
            answer = Loc.Format("{0} is coming.", ally.Name);
            campaign.MarkStrategicChange();
            return true;
        }

        // Refused. An offensive call is a favour, not a promise, so it costs goodwill and
        // leaves the alliance standing; a defensive one is the promise, and that path is
        // CallToArms above, which still ends the alliance over it.
        OpinionSystem.Remember(campaign, bond, "refused_a_call");
        bond.LastActionDay = campaign.ElapsedDays;
        answer = ally.IsHuman
            ? Loc.Format("{0} is played by somebody. Ask them.", ally.Name)
            : Loc.Format("{0} stayed home.", ally.Name);
        campaign.MarkStrategicChange();
        return false;
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
        DiplomaticRelation bond, bool favour = false)
    {
        if (friend.IsHuman) return false;
        if (CampaignDiplomacy.AtWar(campaign, friend.Id, attacker.Id)) return false;
        if (!CampaignDiplomacy.CanDeclareWar(campaign, friend.Id, attacker.Id, out _)) return false;

        // Already fighting a war of its own is the commonest and most honest excuse.
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(friend.Id)) return false;
        if (campaign.FeatureEnabled("public_debt") && friend.DebtCondition >= DebtCondition.Insolvent) return false;

        var willingness = bond.Opinion / 100f;
        if (campaign.FeatureEnabled("public_debt") && friend.DebtCondition >= DebtCondition.Default) willingness -= 0.35f;
        if (bond.Stance == DiplomaticStance.Allied) willingness += favour ? 0.25f : 0.55f;
        if (!favour && InternationalOrganizations.DefencePartners(campaign, defender.Id).Contains(friend.Id)) willingness += .55f;
        if (bond.GuaranteedBy(friend.Id)) willingness += favour ? 0.15f : 0.45f;
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

public sealed partial class Defines
{
    /// <summary>How long two countries must have been exchanging fire before it is a reason for a
    /// war rather than only for a border war.</summary>
    public int AiClashesCauseAfterDays { get; set; } = 180;
}

/// <summary>
/// A casus belli, said out loud.
///
/// The simulation reasons in ids - "reconquest", "broke_non_aggression_pact" - because that is
/// what can be compared, stored and modded. A player reasons in sentences. This is the one place
/// the two are joined, so a war on the map can be explained in the same words whichever screen
/// is doing the explaining.
///
/// The six the simulation names for itself are listed here. Everything else is a remembered
/// grievance, and a grievance is already named in the data the opinion panel reads - so a mod
/// that renames "Broke our non-aggression pact" renames it in the war ledger too, in the
/// player's own language, without this file being touched at all.
/// </summary>
public static class CasusBelliNames
{
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reconquest"] = "Took our land",
        ["ultimatum_refusal"] = "Refused our ultimatum",
        ["irredentism"] = "Our people live there",
        ["defence_of_an_ally"] = "Attacked a country we are bound to",
        ["border_clashes"] = "Months of shooting across the border",
        ["intervention"] = "Fighting people we armed",
        ["dynastic_claim"] = "A cousin crown in crisis, and our house the senior one",
        ["hegemony"] = "The strong do what they can",
        ["reunification"] = "One people, one state",
        ["conquest"] = "A war to take the country",
        ["imperialism"] = "The strong do what they can"
    };

    /// <summary>The sentence a reason is said in, or an empty string when there is no reason.</summary>
    public static string Of(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (Reasons.TryGetValue(id, out var named)) return named;
        return OpinionCatalog.Find(id)?.Name ?? "";
    }
}
