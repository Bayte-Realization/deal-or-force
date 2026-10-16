using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How far apart two governments' politics may be before an alliance between them costs
    /// rather than helps (ideological distance, 0 to 1), how much a step either side of that
    /// is worth, and the most that governments alike can add: kinship makes an alliance
    /// easier, it does not make one. A liberal democracy and a one-party state sit at about
    /// 0.75 apart.
    /// </summary>
    public float AllianceIdeologyNeutral { get; set; } = 0.25f;
    public float AllianceIdeologyWeight { get; set; } = 0.8f;
    public float AllianceIdeologyBonusCap { get; set; } = 0.15f;

    /// <summary>What it costs that the country asking is allied to somebody we count a rival or are fighting.</summary>
    public float AllianceRivalCampPenalty { get; set; } = 0.6f;

    /// <summary>What it costs that one of our own allies counts the country asking a rival.</summary>
    public float AllianceAllyObjectionPenalty { get; set; } = 0.35f;

    /// <summary>
    /// How far apart two capitals may be, in km, before two countries neither of which is a
    /// great power cannot be of much use to each other in a war - and what that costs. A
    /// great power on either side lifts it: patrons are allied to the far side of the world.
    /// </summary>
    public float AllianceReachKm { get; set; } = 2500f;
    public float AllianceDistancePenalty { get; set; } = 0.35f;

    /// <summary>
    /// How many allies a country is comfortable with, and what each further one costs a new
    /// offer, in tenths: a country already bound to thirty others is not short of friends.
    /// </summary>
    public int AllianceComfortableAllies { get; set; } = 6;
    public float AllianceCrowdingWeight { get; set; } = 0.30f;

    /// <summary>
    /// How large a bloc the country asking may bring before joining it is joining everybody's
    /// wars, and what each further dozen allies of theirs costs.
    /// </summary>
    public int AllianceEntanglementAllies { get; set; } = 12;
    public float AllianceEntanglementWeight { get; set; } = 0.25f;
}

/// <summary>
/// An alliance as a decision two countries take, rather than a number one of them crosses.
///
/// The stance already meant something: an attack on an ally is answered, a promise refused
/// is an alliance ended in public, and nobody may declare war on somebody they are allied
/// to without leaving first. What was missing was the door. Only the computer could form
/// one, and it formed them by fiat - opinion reached sixty-six and the stance changed, with
/// nobody asked and nothing to refuse. A player could watch alliances appear on the map and
/// never be in one.
///
/// So: somebody asks, somebody answers. The answer is the interesting half, because it is
/// where a country says what it thinks an alliance is for - shelter from something larger,
/// a friend against a shared rival, or a war it would be signing up to. A country that says
/// no says why, and will not be asked again for half a year.
/// </summary>
public static class AllianceSystem
{
    /// <summary>Every country this one is allied to.</summary>
    public static List<Nation> AlliesOf(Campaign campaign, Nation nation)
    {
        var found = new List<Nation>();
        if (campaign is null || nation is null) return found;
        foreach (var other in campaign.Nations)
        {
            if (other.Id == nation.Id) continue;
            var relation = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
            if (relation is { Stance: DiplomaticStance.Allied }) found.Add(other);
        }
        return found;
    }

    /// <summary>Who has an alliance on this country's table, if anybody.</summary>
    public static Nation OfferedTo(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return null;
        foreach (var other in campaign.Nations)
        {
            if (other.Id == nation.Id) continue;
            var relation = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
            if (relation is null || relation.AllianceOfferedBy != other.Id) continue;
            if (Expired(campaign, relation)) continue;
            return other;
        }
        return null;
    }

    private static bool Expired(Campaign campaign, DiplomaticRelation relation) =>
        campaign.ElapsedDays - relation.AllianceOfferedDay > campaign.Defines.AllianceOfferDays;

    /// <summary>
    /// Whether these two could sign anything at all, and if not, which of the several
    /// reasons it is. Every one of them is a fact a player can act on rather than a refusal.
    /// </summary>
    public static bool CanPropose(Campaign campaign, Nation proposer, Nation target, out string refusal, bool preview = false)
    {
        refusal = "";
        if (campaign is null || proposer is null || target is null || proposer.Id == target.Id)
        {
            refusal = "No such country.";
            return false;
        }

        var relation = preview ? CampaignDiplomacy.ReadOrNeutral(campaign, proposer.Id, target.Id)
            : CampaignDiplomacy.Get(campaign, proposer.Id, target.Id);
        if (relation.Stance == DiplomaticStance.Allied)
        {
            refusal = $"{proposer.Tag} and {target.Tag} are already allied.";
            return false;
        }
        if (CampaignDiplomacy.AtWar(campaign, proposer.Id, target.Id))
        {
            refusal = "They are at war with each other.";
            return false;
        }

        // A country that answers to another one does not have a foreign policy to pledge,
        // which is the whole of what answering to somebody means - see SubjectSystem.
        if (proposer.IsSubject)
        {
            var overlord = campaign.NationById(proposer.OverlordId);
            refusal = $"{proposer.Tag} answers to {overlord?.Tag ?? "somebody"}; its alliances are theirs to make.";
            return false;
        }
        if (target.IsSubject)
        {
            var overlord = campaign.NationById(target.OverlordId);
            refusal = $"{target.Tag} answers to {overlord?.Tag ?? "somebody"}; ask them instead.";
            return false;
        }

        // Whether an offer is pending is the date, not who made it: an offer from country 0
        // (Indonesia) was stored as the same 0 that meant "no offer".
        if (!Expired(campaign, relation))
        {
            refusal = relation.AllianceOfferedBy == proposer.Id
                ? $"{target.Tag} has not answered the last offer yet."
                : $"{target.Tag} has an offer on your table; answer that one first.";
            return false;
        }

        var since = campaign.ElapsedDays - relation.AllianceRefusedDay;
        if (since < campaign.Defines.AllianceRefusalDays)
        {
            refusal = $"They refused {since:N0} day(s) ago; asking again this soon is not diplomacy.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// What the asked country thinks of being asked, where anything above the define is yes.
    ///
    /// The terms are the ones a foreign ministry would actually weigh, and they are named so
    /// the interface can show them: how they feel about you, whether signing means joining a
    /// war you are already in, whether you are worth sheltering behind or a liability to
    /// shelter, and whether you both fear the same country - which is what most alliances in
    /// history were actually about.
    /// </summary>
    public static float Willingness(Campaign campaign, Nation asked, Nation asking, Breakdown why = null,
        bool refreshAssessment = true)
    {
        why?.Begin("Whether they would sign", "share");
        if (campaign is null || asked is null || asking is null) return 0f;
        var relation = refreshAssessment ? CampaignDiplomacy.Get(campaign, asked.Id, asking.Id)
            : CampaignDiplomacy.ReadOrNeutral(campaign, asked.Id, asking.Id);
        var defines = campaign.Defines;

        var willingness = relation.Opinion / 100f;
        why?.Add("How we regard them", relation.Opinion / 100f);
        var image = SocialMemory.Esteem(campaign, asked, asking) * defines.SocialAllianceWeight;
        willingness += image;
        if (MathF.Abs(image) >= 0.001f) why?.Add(Loc.Get("Their standing in our society"), image);

        // An alliance with a country already at war is a war, and everybody knows it.
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(asking.Id))
            {
                willingness -= defines.AllianceWarPenalty;
                why?.Add("They are already fighting", -defines.AllianceWarPenalty);
                break;
            }

        // What they are worth as a friend, and what we are worth to them. A small country
        // wants somebody large to stand behind; a large one gains little from somebody it
        // would have to carry.
        var mine = MathF.Max(0.01f, AiStatecraft.StrengthOf(campaign, asked));
        var theirs = CampaignIntel.EstimatedStrength(campaign, asked.Id, asking, refreshAssessment);
        var shelter = M.Clamp(theirs / mine - 1f, -0.6f, 1.2f) * defines.AllianceShelterWeight;
        willingness += shelter;
        why?.Add("What they bring", shelter);

        // A pariah is a liability: their enemies become yours and their reputation with them.
        if (asking.Notoriety > 0f)
        {
            var cost = -M.Clamp(asking.Notoriety / 100f, 0f, 1f) * defines.AllianceNotorietyWeight;
            willingness += cost;
            why?.Add("Their reputation", cost);
        }

        // And the reason most alliances were ever signed: the same country frightens both. A
        // country that frightens is a great power or one at the door - with two hundred and
        // sixty countries in the world, any two of them dislike some third, and a shared
        // dislike of the Falkland Islands is not a reason to go to war for each other.
        var powers = GreatPowers(campaign);
        var asksBorders = CampaignDiplomacy.Neighbours(campaign, asked.MapIndex);
        var askingBorders = CampaignDiplomacy.Neighbours(campaign, asking.MapIndex);
        var shared = 0;
        foreach (var third in campaign.Nations)
        {
            if (third.Id == asked.Id || third.Id == asking.Id) continue;
            if (!powers.Contains(third.Id) && !asksBorders.Contains(third.MapIndex) &&
                !askingBorders.Contains(third.MapIndex)) continue;
            var ours = CampaignDiplomacy.Peek(campaign, asked.Id, third.Id);
            var theirsToo = CampaignDiplomacy.Peek(campaign, asking.Id, third.Id);
            if (ours is null || theirsToo is null) continue;
            if (ours.Opinion > defines.AiRivalryThreshold || theirsToo.Opinion > defines.AiRivalryThreshold) continue;
            shared++;
        }
        if (shared > 0)
        {
            var common = MathF.Min(shared, 3) * defines.AllianceSharedRivalWeight;
            willingness += common;
            why?.Add($"We fear the same {shared:N0} country(ies)", common);
        }

        // A country in no state to promise anything does not promise anything.
        var steady = M.Clamp(asked.Stability - 0.5f, -0.3f, 0.3f);
        willingness += steady;
        why?.Add("What we can afford to promise", steady);

        // What kind of government is asking. Opinion already counts it a little; an alliance
        // counts it a great deal more, because an alliance is a promise to fight for the
        // other government's survival, and few governments promise that to their opposite.
        var politics = MathF.Min(defines.AllianceIdeologyBonusCap,
            (defines.AllianceIdeologyNeutral - PoliticsCore.IdeologicalDistance(asked, asking)) * defines.AllianceIdeologyWeight);
        if (MathF.Abs(politics) >= 0.01f)
        {
            willingness += politics;
            why?.Add(politics >= 0f ? "Governments alike" : "Governments opposed", politics);
        }

        // Which camp they are in. An alliance with a friend of our rival is an alliance with
        // their wars against us, and an ally of ours who cannot stand them will say so.
        var askedAllies = AlliesOf(campaign, asked);
        var askingAllies = AlliesOf(campaign, asking);
        foreach (var friend in askingAllies)
        {
            if (friend.Id == asked.Id || askedAllies.Contains(friend)) continue;
            var view = CampaignDiplomacy.Peek(campaign, asked.Id, friend.Id);
            if (view is null) continue;
            if (view.Opinion > defines.AiRivalryThreshold && !CampaignDiplomacy.AtWar(campaign, asked.Id, friend.Id)) continue;
            willingness -= defines.AllianceRivalCampPenalty;
            why?.Add(Loc.Format("They are allied to {0}", friend.DisplayName), -defines.AllianceRivalCampPenalty);
            break;
        }
        foreach (var friend in askedAllies)
        {
            if (friend.Id == asking.Id) continue;
            var view = CampaignDiplomacy.Peek(campaign, friend.Id, asking.Id);
            if (view is null || view.Opinion > defines.AiRivalryThreshold) continue;
            willingness -= defines.AllianceAllyObjectionPenalty;
            why?.Add(Loc.Format("Our ally {0} distrusts them", friend.DisplayName), -defines.AllianceAllyObjectionPenalty);
            break;
        }

        // How far apart they are. Two small countries on opposite sides of the world can
        // promise each other nothing an army could deliver; a great power on either side can.
        if (!powers.Contains(asked.Id) && !powers.Contains(asking.Id) &&
            CapitalKm(asked, asking) > defines.AllianceReachKm)
        {
            willingness -= defines.AllianceDistancePenalty;
            why?.Add("Too far away to defend each other", -defines.AllianceDistancePenalty);
        }

        // How bound they are already. A country with thirty allies is not short of friends,
        // and joining a bloc of thirty is joining thirty countries' wars - counted as the allies
        // the other does not share, because two members of one bloc signing with each other
        // take on nobody new.
        var newToUs = 0;
        foreach (var friend in askedAllies)
            if (friend.Id != asking.Id && !askingAllies.Contains(friend)) newToUs++;
        var newToThem = 0;
        foreach (var friend in askingAllies)
            if (friend.Id != asked.Id && !askedAllies.Contains(friend)) newToThem++;
        var crowded = MathF.Max(0, newToUs - defines.AllianceComfortableAllies) / 10f * defines.AllianceCrowdingWeight;
        if (crowded > 0.001f)
        {
            willingness -= crowded;
            why?.Add("We have allies enough", -crowded);
        }
        var entangled = MathF.Max(0, newToThem - defines.AllianceEntanglementAllies) / 12f *
                        defines.AllianceEntanglementWeight;
        if (entangled > 0.001f)
        {
            willingness -= entangled;
            why?.Add("Their bloc's wars would be ours", -entangled);
        }

        return willingness;
    }

    private static readonly HashSet<int> Powers = new();
    private static int _powersDay = int.MinValue;
    private static Campaign _powersFor;

    /// <summary>
    /// The countries large enough that a quarrel with them is everybody's quarrel - the same
    /// number the AI watches (AiGreatPowers), ranked by strength, worked out once a day.
    /// </summary>
    internal static HashSet<int> GreatPowers(Campaign campaign)
    {
        if (_powersFor == campaign && _powersDay == campaign.ElapsedDays) return Powers;
        _powersFor = campaign;
        _powersDay = campaign.ElapsedDays;
        Powers.Clear();
        var ranked = new List<(float Strength, int Id)>();
        foreach (var nation in campaign.Nations)
            if (nation.Population > 0) ranked.Add((AiStatecraft.StrengthOf(campaign, nation), nation.Id));
        ranked.Sort((left, right) => right.Strength != left.Strength
            ? right.Strength.CompareTo(left.Strength)
            : left.Id.CompareTo(right.Id));
        for (var index = 0; index < ranked.Count && index < campaign.Defines.AiGreatPowers; index++)
            Powers.Add(ranked[index].Id);
        return Powers;
    }

    /// <summary>Between two capitals, over the globe, in km.</summary>
    internal static float CapitalKm(Nation one, Nation other)
    {
        const float radians = MathF.PI / 180f;
        var lat1 = one.CapitalLatitude * radians;
        var lat2 = other.CapitalLatitude * radians;
        var dLat = lat2 - lat1;
        var dLon = (other.CapitalLongitude - one.CapitalLongitude) * radians;
        var h = MathF.Sin(dLat / 2f) * MathF.Sin(dLat / 2f) +
                MathF.Cos(lat1) * MathF.Cos(lat2) * MathF.Sin(dLon / 2f) * MathF.Sin(dLon / 2f);
        return 2f * 6371f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(h)));
    }

    /// <summary>
    /// Ask. If the country asked is one the computer speaks for, it answers now; if it is a
    /// player's, the offer goes on their table and waits, the way a peace offer does - the
    /// rule that a player is never committed to a war without being asked would mean very
    /// little if their alliances could be signed for them.
    /// </summary>
    public static bool Propose(Campaign campaign, Nation proposer, Nation target, out string answer)
    {
        if (!CanPropose(campaign, proposer, target, out answer)) return false;

        var relation = CampaignDiplomacy.Get(campaign, proposer.Id, target.Id);
        relation.LastActionDay = campaign.ElapsedDays;

        if (target.IsHuman)
        {
            relation.AllianceOfferedBy = proposer.Id;
            relation.AllianceOfferedDay = campaign.ElapsedDays;
            campaign.MarkStrategicChange();
            answer = $"{target.Name} will consider it.";
            DiagnosticLogs.Write("simulation",
                $"ALLIANCE_OFFER from={proposer.Tag} to={target.Tag} day={campaign.ElapsedDays}");
            return false;
        }

        var willingness = Willingness(campaign, target, proposer);
        if (willingness <= campaign.Defines.AllianceWillingness)
        {
            relation.AllianceRefusedDay = campaign.ElapsedDays;
            campaign.MarkStrategicChange();
            answer = $"{target.Name} declines.";
            DiagnosticLogs.Write("simulation",
                $"ALLIANCE_REFUSED by={target.Tag} to={proposer.Tag} willingness={willingness:0.00}");
            return false;
        }

        Sign(campaign, relation, proposer, target);
        answer = $"{target.Name} accepts.";
        return true;
    }

    /// <summary>The other side of an offer somebody was given time to think about.</summary>
    public static bool Answer(Campaign campaign, Nation answering, bool accept, out string outcome)
    {
        outcome = "";
        var proposer = OfferedTo(campaign, answering);
        if (proposer is null)
        {
            outcome = "Nobody has offered an alliance.";
            return false;
        }
        var relation = CampaignDiplomacy.Get(campaign, answering.Id, proposer.Id);
        relation.AllianceOfferedBy = 0;
        relation.AllianceOfferedDay = -9999;
        relation.LastActionDay = campaign.ElapsedDays;

        if (!accept)
        {
            relation.AllianceRefusedDay = campaign.ElapsedDays;
            campaign.MarkStrategicChange();
            outcome = $"{proposer.Name} was turned down.";
            return false;
        }

        Sign(campaign, relation, answering, proposer);
        outcome = $"{answering.Name} and {proposer.Name} are allied.";
        return true;
    }

    private static void Sign(Campaign campaign, DiplomaticRelation relation, Nation one, Nation other)
    {
        relation.Stance = DiplomaticStance.Allied;
        relation.AllianceOfferedBy = 0;
        relation.AllianceOfferedDay = -9999;
        relation.AllianceSinceDay = campaign.ElapsedDays;
        relation.AllianceCohesion = 0.75f;
        relation.AllianceStrainedDays = 0;
        relation.AllianceAssessmentDay = campaign.ElapsedDays;
        relation.LastActionDay = campaign.ElapsedDays;
        OpinionSystem.Remember(campaign, relation, "alliance");
        campaign.MarkStrategicChange();
        DiagnosticLogs.Write("simulation",
            $"ALLIANCE {one.Tag}+{other.Tag} day={campaign.ElapsedDays}");
    }

    /// <summary>
    /// Walk away from one.
    ///
    /// Cheaper than refusing a call to arms, which is the same promise broken at the moment
    /// it was worth something, and dearer than never having signed: they remember it, and
    /// everybody who was watching remembers that this is a country whose signature has a
    /// shelf life.
    /// </summary>
    public static bool Break(Campaign campaign, Nation nation, Nation other, out string refusal)
    {
        refusal = "";
        if (campaign is null || nation is null || other is null) { refusal = "No such country."; return false; }
        var relation = CampaignDiplomacy.Get(campaign, nation.Id, other.Id);
        if (relation.Stance != DiplomaticStance.Allied)
        {
            refusal = $"{nation.Tag} and {other.Tag} are not allied.";
            return false;
        }

        relation.Stance = DiplomaticStance.Neutral;
        AlliancePrograms.Clear(relation);
        relation.AllianceSinceDay = -9999;
        relation.AllianceRefusedDay = campaign.ElapsedDays;
        relation.AllianceStrainedDays = 0;
        relation.LastActionDay = campaign.ElapsedDays;
        OpinionSystem.Forget(campaign, relation, "alliance");
        OpinionSystem.Remember(campaign, relation, "broke_our_alliance");
        campaign.MarkStrategicChange();
        DiagnosticLogs.Write("simulation",
            $"ALLIANCE_END {nation.Tag}-{other.Tag} day={campaign.ElapsedDays}");
        return true;
    }
}

/// <summary>Ask another country to be an ally.</summary>
public sealed class ProposeAllianceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var proposer = campaign.NationById(IssuerId);
        var target = campaign.NationById(TargetId);
        if (proposer is null || target is null) return;
        AllianceSystem.Propose(campaign, proposer, target, out _);
    }
}

/// <summary>Answer one that is on the table.</summary>
public sealed class AnswerAllianceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public bool Accept { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var answering = campaign.NationById(IssuerId);
        if (answering is null) return;
        AllianceSystem.Answer(campaign, answering, Accept, out _);
    }
}

/// <summary>Leave one.</summary>
public sealed class BreakAllianceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var other = campaign.NationById(TargetId);
        if (nation is null || other is null) return;
        AllianceSystem.Break(campaign, nation, other, out _);
    }
}
