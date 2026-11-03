using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How much an act by somebody else against somebody else concerns a country with no tie to
    /// either - the floor every bystander starts from (OpinionWitnesses.Concern). One is "as if it
    /// had been done to us"; this is a fifth of that - a raised eyebrow, not a grudge. At a tenth
    /// (with far fear at 0.15) a 3-year bench had a third fewer wars and crises; at these values
    /// it keeps the world as tense as before while the world at large no longer hates a stranger.
    /// </summary>
    public float WitnessBaseConcern { get; set; } = 0.2f;
    /// <summary>What an alliance, a guarantee or a shared defence organisation with the victim adds.</summary>
    public float WitnessAllyOfVictim { get; set; } = 0.75f;
    /// <summary>What a friendship, a trade agreement or a warm opinion of the victim adds.</summary>
    public float WitnessFriendOfVictim { get; set; } = 0.35f;
    /// <summary>Concern per unit of the bystander's trade that runs through the victim, up to <see cref="WitnessTradeCap"/>.</summary>
    public float WitnessTradeWeight { get; set; } = 3f;
    public float WitnessTradeCap { get; set; } = 0.3f;
    /// <summary>Sharing a land border with the actor or the victim: the next one could be us.</summary>
    public float WitnessNeighbour { get; set; } = 0.4f;
    /// <summary>A capital within <see cref="WitnessNearKm"/> of the actor's or the victim's.</summary>
    public float WitnessNear { get; set; } = 0.2f;
    public float WitnessNearKm { get; set; } = 1500f;
    /// <summary>A capital within <see cref="WitnessRegionKm"/>: the same part of the world, at a distance.</summary>
    public float WitnessRegion { get; set; } = 0.08f;
    public float WitnessRegionKm { get; set; } = 3000f;
    /// <summary>
    /// A bystander that is the victim's rival or enemy, or thinks as badly of it as
    /// <see cref="WitnessHostileOpinion"/>: the act is welcome, and this is how welcome.
    /// </summary>
    public float WitnessHostileToVictim { get; set; } = -0.6f;
    public float WitnessHostileOpinion { get; set; } = -40f;
    /// <summary>A bystander merely cool towards the victim (<see cref="WitnessCoolOpinion"/>) minds less.</summary>
    public float WitnessCoolToVictim { get; set; } = -0.25f;
    public float WitnessCoolOpinion { get; set; } = -15f;
    /// <summary>The actor's own allies and friends forgive it a good deal.</summary>
    public float WitnessAllyOfActor { get; set; } = -0.4f;
    public float WitnessFriendOfActor { get; set; } = -0.2f;
    /// <summary>What sitting in the same organisation as the one that was wronged, or walked out on, adds.</summary>
    public float WitnessOrganizationTie { get; set; } = 0.2f;
    /// <summary>For an act with no victim - a weapon tested, forces put on alert - the actor's rivals mind most.</summary>
    public float WitnessRivalOfActor { get; set; } = 0.25f;
    /// <summary>How far apart in politics the bystander and the actor are: distant politics, more suspicion.</summary>
    public float WitnessIdeologyWeight { get; set; } = 0.25f;
    /// <summary>Concern below this is no reaction at all - nothing is remembered and no relation is created.
    /// Above the base concern of a far country with no ties, so that one simply does not care.</summary>
    public float WitnessThreshold { get; set; } = 0.12f;
    /// <summary>Concern at or below minus this, for an act with a victim, is remembered as a favour (acted_against_our_rival).</summary>
    public float WitnessWelcomeThreshold { get; set; } = 0.2f;
    /// <summary>
    /// The most that everything bystanders hold against a country can add up to in one relation,
    /// and the most that their approval can - the opinion modifiers declared `third_party`. A
    /// world that has watched somebody misbehave for a decade minds; it does not hate it the
    /// way the countries that were wronged do.
    /// </summary>
    public float ThirdPartyOpinionFloor { get; set; } = -25f;
    public float ThirdPartyOpinionCeiling { get; set; } = 15f;
    /// <summary>How fear of a stronger country fades with distance: full next door, this share beyond the region.</summary>
    public float FearOfStrengthFarShare { get; set; } = 0.3f;
}

/// <summary>
/// What a country makes of something done by somebody else to somebody else.
///
/// Until this existed a nuclear test, a coup, a government's growing appetite for land or an
/// assault on an organisation's member was remembered by every country on earth at the same
/// weight. A player who did two dishonest things as China found Germany, Brazil and Australia at
/// minus a hundred, with lines on the opinion panel about acts that had nothing to do with any of
/// them. That is not how a polarised world reads the news: a rival of the victim is quietly
/// pleased, the victim's allies and neighbours are alarmed, and a country on the far side of the
/// planet with no trade, no treaty and no border at stake notes it and moves on.
///
/// <see cref="Concern"/> is that reading, as a number every bystander works out for itself from
/// what the game already keeps - alliances and guarantees, defence organisations, friendships,
/// trade, borders, the distance between capitals, rivalries and politics. <see cref="Witness"/>
/// remembers the act at that weight, or remembers it as a favour when the bystander is the
/// victim's enemy, or does nothing at all. The rules are the same for every country, played or
/// not; the numbers are defines, so a mod can make its world more or less of a busybody.
///
/// The countries actually wronged are not witnesses. Their reasons are remembered the way they
/// always were, at full strength, by the code that wrongs them.
/// </summary>
public static class OpinionWitnesses
{
    /// <summary>
    /// How much an act by <paramref name="actor"/> against <paramref name="victim"/> concerns
    /// <paramref name="observer"/>, from about minus one (welcome) to one (as if done to us).
    /// A null victim is an act against nobody in particular - a test, an alert, a bad name.
    /// <paramref name="tie"/> is any bond the caller knows of that this function cannot see,
    /// such as membership of the organisation the act was against.
    /// </summary>
    public static float Concern(Campaign c, Nation observer, Nation actor, Nation victim, float tie = 0f)
    {
        if (c is null || observer is null || actor is null || observer.Id == actor.Id) return 0f;
        if (victim is not null && victim.Id == observer.Id) return 1f;
        var d = c.Defines;
        var concern = d.WitnessBaseConcern + tie;

        if (victim is not null && victim.Id != actor.Id)
        {
            var bond = CampaignDiplomacy.Peek(c, observer.Id, victim.Id);
            var hostile = bond is not null && (bond.Stance is DiplomaticStance.Rival or DiplomaticStance.AtWar ||
                                               bond.Opinion <= d.WitnessHostileOpinion);
            if (hostile) concern += d.WitnessHostileToVictim;
            else
            {
                var allied = bond is not null && (bond.Stance == DiplomaticStance.Allied ||
                                                  bond.GuaranteedBy(observer.Id) || bond.GuaranteedBy(victim.Id)) ||
                             Contains(InternationalOrganizations.DefencePartners(c, observer.Id), victim.Id) ||
                             observer.AnswersTo(victim.Id) || victim.AnswersTo(observer.Id);
                if (allied) concern += d.WitnessAllyOfVictim;
                else if (bond is not null && (bond.Stance == DiplomaticStance.Friendly || bond.TradeAgreement || bond.Opinion >= 40f))
                    concern += d.WitnessFriendOfVictim;
                else if (bond is not null && bond.Opinion <= d.WitnessCoolOpinion)
                    concern += d.WitnessCoolToVictim;
                concern += MathF.Min(d.WitnessTradeCap, observer.TradeDependenceOn(victim.Id) * d.WitnessTradeWeight);
            }
        }

        concern += Proximity(c, observer, actor, victim);

        var withActor = CampaignDiplomacy.Peek(c, observer.Id, actor.Id);
        if (withActor is not null)
        {
            if (withActor.Stance == DiplomaticStance.Allied || observer.AnswersTo(actor.Id) || actor.AnswersTo(observer.Id) ||
                Contains(InternationalOrganizations.DefencePartners(c, observer.Id), actor.Id))
                concern += d.WitnessAllyOfActor;
            else if (withActor.Stance == DiplomaticStance.Friendly) concern += d.WitnessFriendOfActor;
            else if (victim is null && withActor.Stance is DiplomaticStance.Rival or DiplomaticStance.AtWar)
                concern += d.WitnessRivalOfActor;
        }

        concern += (PoliticsCore.IdeologicalDistance(observer, actor) - 0.4f) * d.WitnessIdeologyWeight;
        return Math.Clamp(concern, -1f, 1f);
    }

    /// <summary>
    /// Geography alone: next door to either party is the most it can say, the same region less,
    /// the other side of the world almost nothing. Used both for acts and for the standing fear
    /// of a stronger country, which is the same question asked without an act.
    /// </summary>
    public static float Proximity(Campaign c, Nation observer, Nation actor, Nation victim)
    {
        var d = c.Defines;
        if (CampaignDiplomacy.SharesLandBorder(c, observer.MapIndex, actor.MapIndex) ||
            victim is not null && CampaignDiplomacy.SharesLandBorder(c, observer.MapIndex, victim.MapIndex))
            return d.WitnessNeighbour;
        var km = AllianceSystem.CapitalKm(observer, actor);
        if (victim is not null) km = MathF.Min(km, AllianceSystem.CapitalKm(observer, victim));
        if (!float.IsFinite(km)) return 0f;
        if (km <= d.WitnessNearKm) return d.WitnessNear;
        return km <= d.WitnessRegionKm ? d.WitnessRegion : 0f;
    }

    /// <summary>
    /// How much of the fear of a stronger country survives the distance to it: all of it next
    /// door, most of it within reach of a short war, a little across a region, and only
    /// <see cref="Defines.FearOfStrengthFarShare"/> from the far side of the world - France does
    /// not lie awake over China's army the way Vietnam does.
    /// </summary>
    public static float FearReach(Campaign c, Nation observer, Nation stronger)
    {
        if (c is null || observer is null || stronger is null) return 1f;
        var d = c.Defines;
        if (CampaignDiplomacy.SharesLandBorder(c, observer.MapIndex, stronger.MapIndex)) return 1f;
        var km = AllianceSystem.CapitalKm(observer, stronger);
        if (!float.IsFinite(km)) return 1f;
        if (km <= d.WitnessNearKm) return 0.75f;
        if (km <= d.WitnessRegionKm) return 0.4f;
        return Math.Clamp(d.FearOfStrengthFarShare, 0f, 1f);
    }

    /// <summary>
    /// Let a bystander remember an act at the weight it concerns it: scaled by <see cref="Concern"/>
    /// times <paramref name="scale"/>, remembered as a favour when it is welcome and there was a
    /// victim, and not at all when it does not matter - in which case no relation is created
    /// either, so a test on one side of the world does not leave a hundred and ninety empty
    /// relationships on the other. <paramref name="floor"/> is the least concern an act this grave
    /// can carry (a nuclear strike is everybody's business). Returns the concern used.
    /// </summary>
    public static float Witness(Campaign c, Nation observer, Nation actor, Nation victim, string id,
        float scale = 1f, float tie = 0f, float floor = 0f)
    {
        if (c is null || observer is null || actor is null || observer.Id == actor.Id) return 0f;
        if (observer.Movement is not null || observer.Population <= 0) return 0f;
        var concern = Concern(c, observer, actor, victim, tie);
        if (floor > 0f) concern = MathF.Max(floor, concern);
        var d = c.Defines;
        if (concern >= d.WitnessThreshold)
        {
            if (CampaignDiplomacy.Get(c, observer.Id, actor.Id) is { } relation)
                OpinionSystem.Remember(c, relation, id, MathF.Min(1f, concern) * scale);
        }
        else if (victim is not null && concern <= -d.WitnessWelcomeThreshold)
        {
            if (CampaignDiplomacy.Get(c, observer.Id, actor.Id) is { } relation)
                OpinionSystem.Remember(c, relation, "acted_against_our_rival", MathF.Min(1f, -concern));
        }
        return concern;
    }

    /// <summary>
    /// Shorten what one relation still holds against a country: every fading grievance keeps
    /// <c>1 - share</c> of what it is worth today and fades from there at the rate it already
    /// had, so it is gone in that share of the time it had left. Grievances bystanders hold are
    /// forgiven by <paramref name="thirdPartyShare"/>, the relation's own by <paramref name="share"/>.
    /// Standing conditions - an embargo in force, a border, fear - are not memories and are not
    /// touched; they end when the condition does. Returns how much opinion was given back.
    /// </summary>
    public static float Forgive(Campaign c, DiplomaticRelation relation, float share, float thirdPartyShare)
    {
        if (c is null || relation is null) return 0f;
        var restored = 0f;
        foreach (var reason in relation.Reasons)
        {
            if (OpinionCatalog.Find(reason.Id) is not { } definition) continue;
            if (definition.Opinion >= 0f || !definition.Decays || definition.Days <= 0 || reason.ExpiresDay <= 0) continue;
            var cut = Math.Clamp(definition.ThirdParty ? thirdPartyShare : share, 0f, 1f);
            if (cut <= 0f) continue;
            var life = Math.Max(1, reason.ExpiresDay - reason.GrantedDay);
            var left = reason.ExpiresDay - c.ElapsedDays;
            if (left <= 0) continue;
            var before = OpinionSystem.Weight(c, reason);
            var kept = (int)MathF.Floor(left * (1f - cut));
            reason.ExpiresDay = c.ElapsedDays + Math.Max(0, kept);
            reason.GrantedDay = reason.ExpiresDay - life;
            restored += OpinionSystem.Weight(c, reason) - before;
        }
        if (restored != 0f) OpinionSystem.Settle(c, relation);
        return restored;
    }

    /// <summary>What the fading grievances of one relation are worth today (a negative number, or zero).</summary>
    public static float Grievances(Campaign c, DiplomaticRelation relation)
    {
        if (c is null || relation is null) return 0f;
        var total = 0f;
        foreach (var reason in relation.Reasons)
        {
            if (OpinionCatalog.Find(reason.Id) is not { } definition) continue;
            if (definition.Opinion >= 0f || !definition.Decays || definition.Days <= 0 || reason.ExpiresDay <= 0) continue;
            total += OpinionSystem.Weight(c, reason);
        }
        return total;
    }

    private static bool Contains(System.Collections.Generic.IReadOnlyList<int> list, int id)
    {
        for (var index = 0; index < list.Count; index++)
            if (list[index] == id) return true;
        return false;
    }
}
