using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>What a country accepts as a willing "yes" to answering to somebody, as for an alliance.</summary>
    public float SubmissionWillingness { get; set; } = 0.5f;
    /// <summary>How far, capital to capital, a protector can be before distance counts against it.</summary>
    public float SubmissionReachKm { get; set; } = 2500f;
    /// <summary>What distance costs an offer at twice SubmissionReachKm and beyond.</summary>
    public float SubmissionDistancePenalty { get; set; } = 0.8f;

    /// <summary>How many times stronger a country has to be before a smaller one will consider answering to it.</summary>
    public float SubmissionMinimumStrengthRatio { get; set; } = 3f;

    /// <summary>
    /// How much the gap in strength beyond that argues for it, per tenfold. A neighbour ten
    /// times larger is an argument; one a hundred times larger is the end of the argument.
    /// </summary>
    public float SubmissionStrengthWeight { get; set; } = 0.6f;

    /// <summary>
    /// How much each kind's own wish to be free (liberty_desire in common/subjects) counts
    /// against accepting it. A protectorate asks less of a country than a satellite does.
    /// </summary>
    public float SubmissionReluctanceScale { get; set; } = 2.5f;

    /// <summary>How long a country will not be asked again - by the same country - after saying no.</summary>
    public int SubmissionRefusalDays { get; set; } = 365;

    /// <summary>How much a country that feels threatened by somebody else wants a protector, at most.</summary>
    public float SubmissionThreatWeight { get; set; } = 0.6f;

    /// <summary>What the economic offer pays, as a share of the smaller country's GDP, and only if it is accepted.</summary>
    public float SubmissionEconomicPayment { get; set; } = 0.25f;

    /// <summary>How much that money argues for it.</summary>
    public float SubmissionEconomicPaymentWeight { get; set; } = 0.35f;

    /// <summary>
    /// What backing a friendly government costs, as a share of the smaller country's GDP -
    /// spent whether it works or not, because the money is gone the moment it changes hands.
    /// </summary>
    public float SubmissionPoliticalCost { get; set; } = 0.15f;

    /// <summary>And what the rest of the world thinks of it: notoriety, win or lose.</summary>
    public float SubmissionPoliticalNotoriety { get; set; } = 6f;

    /// <summary>The chance each month that a country nobody plays looks for a smaller one to bring under its wing.</summary>
    public float SubmissionAiChancePerMonth { get; set; } = 0.01f;

    /// <summary>How much better than a bare "yes" the computer wants the odds before it asks.</summary>
    public float SubmissionAiMargin { get; set; } = 0.15f;

    /// <summary>How many subjects a country nobody plays gathers this way before it stops.</summary>
    public int SubmissionAiMaxSubjects { get; set; } = 3;

    /// <summary>Which row of common/subjects each route makes: protection.</summary>
    public string SubmissionProtectionKind { get; set; } = "protectorate";

    /// <summary>Money.</summary>
    public string SubmissionEconomicKind { get; set; } = "client";

    /// <summary>And politics.</summary>
    public string SubmissionPoliticalKind { get; set; } = "satellite";
}

/// <summary>How a country is brought to answer to another without a war.</summary>
public enum SubmissionRoute
{
    /// <summary>Shelter offered to a small country that fears somebody else: a protectorate.</summary>
    Protection,

    /// <summary>Money, on condition: a client state.</summary>
    Economic,

    /// <summary>A government helped into power and kept there by the same hand: a satellite.</summary>
    Political
}

/// <summary>
/// Dependency without an invasion (item 66).
///
/// A subject used to be something only a peace treaty could make, which left out most of how
/// it has actually happened: the small state that asked a large one to keep a larger one off
/// it, the government that was paid until it could not afford to say no, and the one that
/// owed its office to a foreign embassy and knew it. Each is a route here, and each ends in a
/// row of `common/subjects` - protection in a protectorate, money in a client state, politics
/// in a satellite (which kind each route makes is a define).
///
/// The smaller country decides, and it decides the way an alliance is decided: by named terms
/// the interface can show - how it regards the asker, how much stronger the asker is, what the
/// kind of dependency would cost it, and then the route's own reason. A player is never made a
/// subject this way; that is always their decision.
/// </summary>
public static class SubmissionSystem
{
    public static string KindOf(Campaign campaign, SubmissionRoute route) => route switch
    {
        SubmissionRoute.Protection => campaign.Defines.SubmissionProtectionKind,
        SubmissionRoute.Economic => campaign.Defines.SubmissionEconomicKind,
        _ => campaign.Defines.SubmissionPoliticalKind
    };

    /// <summary>What the asker pays: on acceptance for money, up front for politics, nothing for protection.</summary>
    public static float Cost(Campaign campaign, Nation target, SubmissionRoute route) => route switch
    {
        SubmissionRoute.Economic => MathF.Max(0f, target.Gdp) * campaign.Defines.SubmissionEconomicPayment,
        SubmissionRoute.Political => MathF.Max(0f, target.Gdp) * campaign.Defines.SubmissionPoliticalCost,
        _ => 0f
    };

    /// <summary>Why this cannot even be asked, or empty when it can.</summary>
    public static string Refusal(Campaign campaign, Nation asker, Nation target, SubmissionRoute route,
        bool refreshAssessment = true)
    {
        if (campaign is null || asker is null || target is null || asker.Id == target.Id)
            return Loc.Get("No such country.");
        if (!SubjectCatalog.Knows(KindOf(campaign, route)))
            return Loc.Format("No kind of dependency called '{0}' is defined.", KindOf(campaign, route));
        if (target.IsHuman) return Loc.Get("A player decides that for themselves.");
        if (asker.IsSubject) return Loc.Get("A country that answers to somebody cannot take subjects of its own.");
        if (target.IsSubject)
            return Loc.Format("{0} already answers to {1}.", target.DisplayName,
                SubjectSystem.OverlordOf(campaign, target)?.DisplayName ?? Loc.Get("Somebody"));
        if (target.Population <= 0 || campaign.StatesOf(target).Length == 0) return Loc.Get("There is nobody there to ask.");
        // A country with subjects of its own is a power, and a chain of masters is a war waiting
        // to find out who is really in charge.
        if (SubjectSystem.SubjectsOf(campaign, target).Count > 0)
            return Loc.Format("{0} has subjects of its own, and answers to nobody.", target.DisplayName);
        if (CampaignDiplomacy.AtWar(campaign, asker.Id, target.Id)) return Loc.Get("They are at war with each other.");

        var relation = refreshAssessment ? CampaignDiplomacy.Get(campaign, asker.Id, target.Id)
            : CampaignDiplomacy.ReadOrNeutral(campaign, asker.Id, target.Id);
        var since = campaign.ElapsedDays - relation.SubmissionAskedDay;
        if (since < campaign.Defines.SubmissionRefusalDays)
            return Loc.Format("They said no {0:N0} day(s) ago; they will not be asked again this year.", since);

        if (StrengthRatio(campaign, asker, target, refreshAssessment) < campaign.Defines.SubmissionMinimumStrengthRatio)
            return Loc.Format("{0} is not so much weaker that answering to us is worth discussing.", target.DisplayName);

        var cost = Cost(campaign, target, route);
        if (asker.Treasury < cost)
            return Loc.Format("This costs {0:0.00}; the treasury holds {1:0.00}.", cost, asker.Treasury);
        return "";
    }

    /// <summary>How much stronger the asker looks to the country being asked.</summary>
    public static float StrengthRatio(Campaign campaign, Nation asker, Nation target, bool refreshAssessment = true)
    {
        var mine = MathF.Max(0.01f, AiStatecraft.StrengthOf(campaign, target));
        var theirs = CampaignIntel.EstimatedStrength(campaign, target.Id, asker, refreshAssessment);
        return theirs / mine;
    }

    /// <summary>
    /// What the country being asked thinks of it; above <see cref="Defines.SubmissionWillingness"/>
    /// is yes. Every term is named, so the reasons can be read before anybody asks.
    /// </summary>
    /// <summary>
    /// What distance does to an offer of submission, 0 or below: nothing for a neighbour, for a
    /// country of the same people or for a former overlord; past SubmissionReachKm, up to minus
    /// SubmissionDistancePenalty at twice that.
    /// </summary>
    public static float Reach(Campaign campaign, Nation target, Nation asker)
    {
        if (CampaignDiplomacy.SharesLandBorder(campaign, asker.MapIndex, target.MapIndex)) return 0f;
        var theirs = DemographyCatalog.PrimaryCulture(target.Tag);
        if (!string.IsNullOrWhiteSpace(theirs) && DemographyCatalog.IsPrimary(asker.Tag, theirs)) return 0f;
        if (target.ReleasedFromId == asker.Id || target.SecededFromId == asker.Id) return 0f;
        var km = AllianceSystem.CapitalKm(asker, target);
        var reach = MathF.Max(1f, campaign.Defines.SubmissionReachKm);
        if (km <= reach) return 0f;
        return -campaign.Defines.SubmissionDistancePenalty * M.Clamp01((km - reach) / reach);
    }

    public static float Willingness(Campaign campaign, Nation target, Nation asker, SubmissionRoute route,
        Breakdown why = null, bool refreshAssessment = true)
    {
        why?.Begin("Whether they would accept", "share");
        if (campaign is null || target is null || asker is null) return 0f;
        var defines = campaign.Defines;
        var relation = refreshAssessment ? CampaignDiplomacy.Get(campaign, target.Id, asker.Id)
            : CampaignDiplomacy.ReadOrNeutral(campaign, target.Id, asker.Id);

        var willingness = relation.Opinion / 100f;
        why?.Add("How they regard us", relation.Opinion / 100f);

        // Strength first, because nothing else is worth saying without it.
        var ratio = StrengthRatio(campaign, asker, target, refreshAssessment);
        var weight = M.Clamp(MathF.Log10(MathF.Max(1f, ratio)) -
                             MathF.Log10(MathF.Max(1f, defines.SubmissionMinimumStrengthRatio)), 0f, 1f) *
                     defines.SubmissionStrengthWeight;
        willingness += weight;
        why?.Add("How much stronger we are", weight);

        // And what the arrangement itself asks of them.
        var kind = SubjectCatalog.Of(KindOf(campaign, route));
        var reluctance = (kind?.LibertyDesire ?? 0.3f) * defines.SubmissionReluctanceScale;
        willingness -= reluctance;
        why?.Add(Loc.Format("What becoming a {0} costs them", Loc.Get(kind?.Name ?? "subject").ToLowerInvariant()), -reluctance);

        // And a protector has to be able to protect. A power across an ocean with no border, no
        // shared people and no old bond is not where a small country goes for safety: Jersey
        // answered to Portugal, Guam to Gabon and Monaco to Czechia, and each was then annexed.
        var reach = Reach(campaign, target, asker);
        if (reach < 0f)
        {
            willingness += reach;
            why?.Add("How far away we are", reach);
        }

        // A pariah's protection is a pariah's enemies.
        if (asker.Notoriety > 0f)
        {
            var cost = -M.Clamp(asker.Notoriety / 100f, 0f, 1f) * 0.4f;
            willingness += cost;
            why?.Add("Our reputation", cost);
        }

        switch (route)
        {
            case SubmissionRoute.Protection:
            {
                // The reason protectorates were ever asked for: somebody else is worse.
                var threat = GreatestThreat(campaign, target, asker, out var feared, refreshAssessment);
                if (threat > 0f)
                {
                    var fear = M.Clamp(threat - 1f, 0f, 2f) * 0.5f * defines.SubmissionThreatWeight +
                               (feared is not null && CampaignDiplomacy.AtWar(campaign, target.Id, feared.Id) ? 0.2f : 0f);
                    willingness += fear;
                    why?.Add(Loc.Format("They fear {0}", feared?.DisplayName ?? Loc.Get("Somebody")), fear);
                }
                if (relation.GuaranteedBy(asker.Id))
                {
                    willingness += 0.15f;
                    why?.Add("We already guarantee them", 0.15f);
                }
                break;
            }
            case SubmissionRoute.Economic:
            {
                willingness += defines.SubmissionEconomicPaymentWeight;
                why?.Add(Loc.Format("What we pay ({0:0.00})", Cost(campaign, target, route)),
                    defines.SubmissionEconomicPaymentWeight);
                if (relation.TradeAgreement)
                {
                    willingness += 0.15f;
                    why?.Add("They trade with us already", 0.15f);
                }
                // A treasury in the red listens to anybody with money.
                var debt = M.Clamp(-target.Treasury / MathF.Max(0.01f, target.Gdp), 0f, 0.5f);
                willingness += debt;
                why?.Add("Their debts", debt);
                break;
            }
            default:
            {
                var sameIdeology = !string.IsNullOrEmpty(target.Ideology) &&
                                   string.Equals(target.Ideology, asker.Ideology, StringComparison.OrdinalIgnoreCase);
                var politics = sameIdeology ? 0.35f : -0.25f;
                if (!string.IsNullOrEmpty(target.Government) &&
                    string.Equals(target.Government, asker.Government, StringComparison.OrdinalIgnoreCase))
                    politics += 0.15f;
                willingness += politics;
                why?.Add(sameIdeology ? "Their politics are ours" : "Their politics are not ours", politics);
                // A government that is sure of itself does not need a foreign one to stay in office.
                var unrest = M.Clamp(0.55f - target.Stability, -0.3f, 0.4f);
                willingness += unrest;
                why?.Add("How secure their government is", unrest);
                break;
            }
        }
        return willingness;
    }

    /// <summary>
    /// The strongest country the target has reason to fear, other than the asker, as a multiple
    /// of the target's own strength - or 0 when there is none. Fear means a war, or a relation
    /// as bad as a rivalry.
    /// </summary>
    public static float GreatestThreat(Campaign campaign, Nation target, Nation asker, out Nation feared,
        bool refreshAssessment = true)
    {
        feared = null;
        var worst = 0f;
        var mine = MathF.Max(0.01f, AiStatecraft.StrengthOf(campaign, target));
        foreach (var other in campaign.Nations)
        {
            if (other.Id == target.Id || other.Id == asker.Id || other.Population <= 0) continue;
            var relation = CampaignDiplomacy.Peek(campaign, target.Id, other.Id);
            var atWar = CampaignDiplomacy.AtWar(campaign, target.Id, other.Id);
            if (!atWar && (relation is null || relation.Opinion > campaign.Defines.AiRivalryThreshold)) continue;
            var multiple = CampaignIntel.EstimatedStrength(campaign, target.Id, other, refreshAssessment) / mine;
            if (multiple <= worst) continue;
            worst = multiple;
            feared = other;
        }
        return worst;
    }

    /// <summary>
    /// Ask. The answer comes at once, because the country being asked is never a player.
    /// Returns whether it became a subject; `answer` says what happened either way.
    /// </summary>
    public static bool Offer(Campaign campaign, Nation asker, Nation target, SubmissionRoute route, out string answer)
    {
        answer = Refusal(campaign, asker, target, route);
        if (answer.Length > 0) return false;

        var defines = campaign.Defines;
        var relation = CampaignDiplomacy.Get(campaign, asker.Id, target.Id);
        relation.LastActionDay = campaign.ElapsedDays;
        var cost = Cost(campaign, target, route);

        // Backing a government is paid for up front, and noticed whatever comes of it.
        if (route == SubmissionRoute.Political)
        {
            asker.Treasury -= cost;
            asker.Notoriety += defines.SubmissionPoliticalNotoriety;
        }

        var willingness = Willingness(campaign, target, asker, route);
        var kind = KindOf(campaign, route);
        if (willingness <= defines.SubmissionWillingness)
        {
            relation.SubmissionAskedDay = campaign.ElapsedDays;
            OpinionSystem.Remember(campaign, relation,
                route == SubmissionRoute.Political ? "meddled_in_our_politics" : "sought_our_submission");
            campaign.MarkStrategicChange();
            DiagnosticLogs.Write("simulation",
                $"SUBMISSION_REFUSED by={target.Tag} to={asker.Tag} route={route} willingness={willingness:0.00} day={campaign.ElapsedDays}");
            answer = Loc.Format("{0} declines.", target.DisplayName);
            return false;
        }

        if (route == SubmissionRoute.Economic)
        {
            asker.Treasury -= cost;
            target.Treasury += cost;
        }
        else if (route == SubmissionRoute.Political)
            target.Treasury += cost;

        if (!SubjectSystem.Subjugate(campaign, asker, target, kind, out var refusal))
        {
            answer = refusal;
            return false;
        }
        OpinionSystem.Remember(campaign, relation, "chose_to_answer_to_us");
        DiagnosticLogs.Write("simulation",
            $"SUBMISSION by={target.Tag} to={asker.Tag} route={route} kind={kind} willingness={willingness:0.00} day={campaign.ElapsedDays}");
        answer = Loc.Format("{0} now answers to {1}.", target.DisplayName, asker.DisplayName);
        return true;
    }

    /// <summary>
    /// The countries nobody plays, once a month each, on a day of their own: now and then one
    /// of them looks for a smaller country that would say yes, and asks it the way it would
    /// most readily agree to. Whether it looks at all this month is a hash of the country and
    /// the month rather than a draw from the campaign's random stream, so that considering it
    /// moves no other roll in the world.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(SubmissionSystem))) return;
        if (campaign is null) return;
        var defines = campaign.Defines;
        if (defines.SubmissionAiChancePerMonth <= 0f) return;
        var month = campaign.ElapsedDays / 30;
        foreach (var asker in campaign.Nations)
        {
            if (asker.IsHuman || asker.IsSubject || asker.Population <= 0) continue;
            if (campaign.ElapsedDays % 30 != (Math.Abs(asker.Id) + 11) % 30) continue;
            if (Chance(asker.Id, month) >= defines.SubmissionAiChancePerMonth) continue;
            if (campaign.Wars.Any(war => !war.Ended && war.Involves(asker.Id))) continue;
            if (SubjectSystem.SubjectsOf(campaign, asker).Count >= defines.SubmissionAiMaxSubjects) continue;

            Nation best = null;
            var bestRoute = SubmissionRoute.Protection;
            var bestMargin = defines.SubmissionAiMargin;
            foreach (var target in campaign.Nations)
            {
                if (target.Id == asker.Id || target.IsHuman) continue;
                // Only countries that already like the asker are worth the walk across the room.
                var relation = CampaignDiplomacy.Peek(campaign, asker.Id, target.Id);
                if (relation is null || relation.Opinion < 25f) continue;
                // Nor across the world: the computer offers its protection where it can protect.
                if (Reach(campaign, target, asker) <= -defines.SubmissionDistancePenalty * 0.99f) continue;
                foreach (var route in Enum.GetValues<SubmissionRoute>())
                {
                    if (Refusal(campaign, asker, target, route).Length > 0) continue;
                    // The computer does not spend more than a quarter of its treasury on one.
                    if (Cost(campaign, target, route) > asker.Treasury * 0.25f) continue;
                    var margin = Willingness(campaign, target, asker, route) - defines.SubmissionWillingness;
                    if (margin <= bestMargin) continue;
                    bestMargin = margin;
                    best = target;
                    bestRoute = route;
                }
            }
            if (best is not null) Offer(campaign, asker, best, bestRoute, out _);
        }
    }

    private static float Chance(int nation, int month)
    {
        unchecked
        {
            var hash = (uint)(nation * 83492791) ^ (uint)(month * 2654435761u) ^ 0x5BD1E995u;
            hash ^= hash >> 16; hash *= 0x7FEB352Du; hash ^= hash >> 15; hash *= 0x846CA68Bu; hash ^= hash >> 16;
            return (hash >> 8) * (1f / 16777216f);
        }
    }
}

/// <summary>Ask another country to answer to this one, by one of the routes. See SubmissionSystem.</summary>
public sealed class OfferSubmissionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public SubmissionRoute Route { get; init; }
    public bool Accepted { get; private set; }
    public string Answer { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        Accepted = SubmissionSystem.Offer(campaign, campaign.NationById(IssuerId), campaign.NationById(TargetId),
            Route, out var answer);
        Answer = answer;
    }
}
