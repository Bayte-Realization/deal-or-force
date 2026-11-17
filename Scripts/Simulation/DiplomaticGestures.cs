using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>A state visit: what it costs, how long before the same two meet again.</summary>
    public float StateVisitPoliticalCost { get; set; } = 10f;
    public float StateVisitCash { get; set; } = 0.05f;
    public int StateVisitCooldownDays { get; set; } = 365;
    /// <summary>A show of force on their border: what it costs, how angry it makes them, how long before the next.</summary>
    public float ShowOfForceCash { get; set; } = 0.1f;
    public float ShowOfForceGrievance { get; set; } = 6f;
    public float ShowOfForceWarSupport { get; set; } = 0.03f;
    public int ShowOfForceCooldownDays { get; set; } = 180;
    /// <summary>Mediating somebody else's war: the political credit it costs, and how ready both sides must be (peace acceptance, 0-100).</summary>
    public float MediationPoliticalCost { get; set; } = 15f;
    public float MediationAcceptance { get; set; } = 45f;
    public int MediationCooldownDays { get; set; } = 180;
    /// <summary>A development loan: its size as a share of the lender's GDP, its interest, its term in weeks, and the missed weeks that make a default.</summary>
    public float LoanShareOfGdp { get; set; } = 0.004f;
    public float LoanInterest { get; set; } = 0.12f;
    public int LoanWeeks { get; set; } = 104;
    public int LoanDefaultWeeks { get; set; } = 8;
    /// <summary>Expelling their diplomats: the tension it adds, the grievance it leaves, what the hawks at home make of it, how long before the next.</summary>
    public float ExpelTension { get; set; } = 8f;
    public float ExpelGrievance { get; set; } = 4f;
    public float ExpelWarSupport { get; set; } = 0.02f;
    public int ExpelCooldownDays { get; set; } = 365;
    /// <summary>Joint exercises with a partner: what they cost, the tension they add with the partners' common rivals, how long before the next.</summary>
    public float ExercisesCash { get; set; } = 0.15f;
    public float ExercisesPoliticalCost { get; set; } = 5f;
    public float ExercisesRivalTension { get; set; } = 5f;
    public int ExercisesCooldownDays { get; set; } = 365;
    /// <summary>
    /// A formal apology with compensation: the political credit it costs, the compensation as a share
    /// of our GDP (never less than <see cref="ApologyMinimumCash"/>), how much of every grievance between
    /// us it forgives - the relation's own and the ones held as a bystander - and how long before the
    /// same government can apologise to the same country again.
    /// </summary>
    public float ApologyPoliticalCost { get; set; } = 12f;
    public float ApologyShareOfGdp { get; set; } = 0.001f;
    public float ApologyMinimumCash { get; set; } = 0.02f;
    public float ApologyForgiveness { get; set; } = 0.5f;
    public float ApologyThirdPartyForgiveness { get; set; } = 0.75f;
    public float ApologyGrievanceRelief { get; set; } = 6f;
    public float ApologyTensionRelief { get; set; } = 6f;
    /// <summary>What the grievances between us must be worth (a negative opinion) before there is anything to apologise for.</summary>
    public float ApologyMinimumGrievance { get; set; } = -5f;
    public int ApologyCooldownDays { get; set; } = 730;
    /// <summary>
    /// Humanitarian aid: its size as a share of our GDP (at most <see cref="HumanitarianAidRecipientShare"/> of
    /// theirs, never less than <see cref="HumanitarianAidMinimumCash"/>), the share of the grievances they
    /// hold as a bystander it lets go of, and how long before the same help is sent again.
    /// </summary>
    public float HumanitarianAidShareOfGdp { get; set; } = 0.0015f;
    public float HumanitarianAidRecipientShare { get; set; } = 0.02f;
    public float HumanitarianAidMinimumCash { get; set; } = 0.01f;
    public float HumanitarianAidThirdPartyForgiveness { get; set; } = 0.25f;
    public int HumanitarianAidCooldownDays { get; set; } = 365;
    /// <summary>Who is in need: stability below this, a war on their soil, or income per head below this share of ours.</summary>
    public float HumanitarianAidNeedStability { get; set; } = 0.45f;
    public float HumanitarianAidNeedIncomeShare { get; set; } = 0.5f;
}

public sealed partial class DiplomaticRelation
{
    /// <summary>A development loan between the two (DiplomaticGestures): who lent, what is still owed, the weekly instalment, the weeks missed.</summary>
    public int LoanLender = -1;
    public float LoanOutstanding;
    public float LoanWeekly;
    public int LoanMissedWeeks;
}

/// <summary>
/// The gestures between governments that are not treaties: a state visit, a show of force on
/// the border, mediation in somebody else's war, a development loan.
///
/// The game had the instruments - missions that build goodwill slowly, denunciations, treaties,
/// ultimatums - and none of the moments: the visit that turns a cold neighbour warm in a week,
/// the exercise on the border that tells a neighbour what a crisis would look like, the third
/// country that brings two others to a ceasefire, the loan that makes a poor country a friend
/// and a debtor. Each has a price, a visible result and a line in the chronicle, and each is
/// the same for every government: the AI makes them on the same terms, through the same
/// command, as a player does.
/// </summary>
public static class DiplomaticGestures
{
    public static readonly string[] All = { "state_visit", "show_of_force", "mediate", "loan", "expel_diplomats", "joint_exercises", "apology", "humanitarian_aid" };

    private static string Key(string gesture, int target) => $"gesture:{gesture}:{target}";

    /// <summary>Why this government cannot make this gesture to that one now, or empty.</summary>
    public static string Refusal(Campaign c, int issuerId, int targetId, string gesture)
    {
        var issuer = c?.NationById(issuerId);
        var target = c?.NationById(targetId);
        if (issuer is null || target is null || issuer.Id == target.Id || issuer.Movement is not null || target.Movement is not null ||
            issuer.Population <= 0 || target.Population <= 0 || c.Fallen.Contains(issuerId) || c.Fallen.Contains(targetId))
            return Loc.Get("Choose an existing foreign country.");
        if (issuer.DecisionAvailableDay.TryGetValue(Key(gesture, targetId), out var availDay) && availDay > c.ElapsedDays)
            return Loc.Format("Not again so soon: available in {0} days.", availDay - c.ElapsedDays);
        var d = c.Defines;
        var relation = CampaignDiplomacy.Peek(c, issuerId, targetId);
        var atWar = CampaignDiplomacy.AtWar(c, issuerId, targetId);
        switch (gesture)
        {
            case "state_visit":
                if (atWar) return Loc.Get("Not with a country we are at war with.");
                if (relation is not null && relation.Opinion < -30f) return Loc.Get("Relations are too poor for a visit to be received.");
                if (issuer.PoliticalPoints < d.StateVisitPoliticalCost)
                    return Loc.Format("Insufficient political credit: need {0:0.##}, available {1:0.##}.", d.StateVisitPoliticalCost, issuer.PoliticalPoints);
                return issuer.Treasury < Cash(c, issuer, "state_visit") ? Loc.Get("The treasury cannot pay for it.") : "";
            case "show_of_force":
                if (atWar) return Loc.Get("We are already at war with them.");
                if (relation is { Stance: DiplomaticStance.Allied }) return Loc.Get("Not against an ally.");
                if (!CampaignDiplomacy.SharesLandBorder(c, issuer.MapIndex, target.MapIndex))
                    return Loc.Get("Only on a border we share with them.");
                return issuer.Treasury < Cash(c, issuer, "show_of_force") ? Loc.Get("The treasury cannot pay for it.") : "";
            case "mediate":
                var war = MediableWar(c, issuer, target);
                if (war is null) return Loc.Get("They are not at war with anybody we are not fighting ourselves.");
                if (issuer.PoliticalPoints < d.MediationPoliticalCost)
                    return Loc.Format("Insufficient political credit: need {0:0.##}, available {1:0.##}.", d.MediationPoliticalCost, issuer.PoliticalPoints);
                if (CampaignDiplomacy.Peek(c, issuerId, war.AttackerId) is { Opinion: < 0f } ||
                    CampaignDiplomacy.Peek(c, issuerId, war.DefenderId) is { Opinion: < 0f })
                    return Loc.Get("Both sides must think well enough of us to listen.");
                return "";
            case "loan":
                if (atWar) return Loc.Get("Not with a country we are at war with.");
                if (relation is { LoanLender: >= 0, LoanOutstanding: > 0f }) return Loc.Get("A loan between us is still being repaid.");
                var amount = LoanSize(c, issuer, target);
                if (amount <= 0f) return Loc.Get("The treasury cannot pay for it.");
                return issuer.Treasury < amount * 2f ? Loc.Get("The treasury cannot spare it.") : "";
            case "expel_diplomats":
                if (atWar) return Loc.Get("We are already at war with them.");
                if (relation is { Stance: DiplomaticStance.Allied }) return Loc.Get("Not against an ally.");
                return "";
            case "joint_exercises":
                if (atWar) return Loc.Get("Not with a country we are at war with.");
                if (relation is null || relation.Stance is not (DiplomaticStance.Allied or DiplomaticStance.Friendly) || relation.Opinion < 20f)
                    return Loc.Get("Only with an ally or a friend who thinks well of us.");
                if (issuer.PoliticalPoints < d.ExercisesPoliticalCost)
                    return Loc.Format("Insufficient political credit: need {0:0.##}, available {1:0.##}.", d.ExercisesPoliticalCost, issuer.PoliticalPoints);
                return issuer.Treasury < Cash(c, issuer, "joint_exercises") ? Loc.Get("The treasury cannot pay for it.") : "";
            case "apology":
                // Unlike a visit, an apology is for exactly the relationship too poor to receive
                // one: no opinion is needed, only something to apologise for and the means to pay.
                if (atWar) return Loc.Get("Not with a country we are at war with.");
                if (relation is null || OpinionWitnesses.Grievances(c, relation) > d.ApologyMinimumGrievance)
                    return Loc.Get("There is nothing between us to apologise for.");
                if (issuer.PoliticalPoints < d.ApologyPoliticalCost)
                    return Loc.Format("Insufficient political credit: need {0:0.##}, available {1:0.##}.", d.ApologyPoliticalCost, issuer.PoliticalPoints);
                return issuer.Treasury < ApologyCost(c, issuer) ? Loc.Get("The treasury cannot pay for it.") : "";
            case "humanitarian_aid":
                if (atWar) return Loc.Get("Not with a country we are at war with.");
                if (!InNeed(c, issuer, target)) return Loc.Get("They are not in need of help.");
                var aid = HumanitarianAidSize(c, issuer, target);
                if (aid <= 0f) return Loc.Get("The treasury cannot pay for it.");
                return issuer.Treasury < aid * 2f ? Loc.Get("The treasury cannot spare it.") : "";
            default:
                return Loc.Get("This cannot be done.");
        }
    }

    /// <summary>The countries both of them are rivals with or near a crisis with: who joint exercises are a message to.</summary>
    public static List<Nation> CommonRivals(Campaign c, Nation a, Nation b)
    {
        var rivals = new List<Nation>();
        var threshold = c.Defines.TensionCrisisAt * 0.6f;
        foreach (var other in c.Nations)
        {
            if (other.Id == a.Id || other.Id == b.Id || other.Movement is not null || other.Population <= 0) continue;
            if (IsRival(c, a.Id, other.Id, threshold) && IsRival(c, b.Id, other.Id, threshold)) rivals.Add(other);
        }
        return rivals;

        static bool IsRival(Campaign c, int nationId, int otherId, float threshold)
        {
            var tie = CampaignDiplomacy.Peek(c, nationId, otherId);
            return tie is not null && (tie.Stance is DiplomaticStance.Rival or DiplomaticStance.AtWar ||
                                       tie.Tension >= threshold || tie.Opinion <= -40f);
        }
    }

    /// <summary>The war of theirs we could mediate: one they lead or fight in that we are not part of, the oldest first.</summary>
    public static StrategicWar MediableWar(Campaign c, Nation issuer, Nation target)
    {
        StrategicWar best = null;
        foreach (var w in Warfare.OpenWars(c))
        {
            if (w.Ended || !w.Involves(target.Id) || w.Involves(issuer.Id)) continue;
            if (best is null || w.StartedDay < best.StartedDay || (w.StartedDay == best.StartedDay && w.Id < best.Id))
                best = w;
        }
        return best;
    }

    /// <summary>A third party cannot bypass either government's negotiation gate or a human signature.</summary>
    public static bool CanMediateArmistice(Campaign c, StrategicWar war)
    {
        if (c is null || war is null || war.Ended || war.PendingOfferFromId >= 0 ||
            war.PendingOffer is not null || WarWeariness.PlayedByAnyone(c, war) ||
            CivilWarPersistence.NegotiationLocked(c, war) ||
            c.ElapsedDays - war.StartedDay < c.Defines.AiMinimumWarDays) return false;
        if (c.NationById(war.AttackerId) is null || c.NationById(war.DefenderId) is null) return false;
        if (PeaceNegotiation.BlockReason(c, war, war.AttackerId, PeaceTerms.Ceasefire).Length > 0 ||
            PeaceNegotiation.Acceptance(c, war, war.AttackerId, PeaceTerms.Ceasefire) < c.Defines.MediationAcceptance)
            return false;
        if (PeaceNegotiation.BlockReason(c, war, war.DefenderId, PeaceTerms.Ceasefire).Length > 0 ||
            PeaceNegotiation.Acceptance(c, war, war.DefenderId, PeaceTerms.Ceasefire) < c.Defines.MediationAcceptance)
            return false;
        return true;
    }

    /// <summary>What a formal apology costs this government in compensation, in billions.</summary>
    public static float ApologyCost(Campaign c, Nation issuer)
    {
        var amount = MathF.Max(c.Defines.ApologyMinimumCash, MathF.Max(0f, issuer.Gdp) * MathF.Max(0f, c.Defines.ApologyShareOfGdp));
        return float.IsFinite(amount) ? MathF.Round(amount, 3) : c.Defines.ApologyMinimumCash;
    }

    /// <summary>What humanitarian aid from this donor to this recipient would be, in billions.</summary>
    public static float HumanitarianAidSize(Campaign c, Nation donor, Nation recipient)
    {
        var d = c.Defines;
        var amount = MathF.Max(0f, donor.Gdp) * MathF.Max(0f, d.HumanitarianAidShareOfGdp);
        amount = MathF.Min(amount, MathF.Max(0f, recipient.Gdp) * MathF.Max(0f, d.HumanitarianAidRecipientShare));
        amount = MathF.Max(amount, d.HumanitarianAidMinimumCash);
        return float.IsFinite(amount) && amount > 0f ? MathF.Round(amount, 3) : 0f;
    }

    /// <summary>
    /// Whether a country is in need of help from this one: a government losing its grip, a war
    /// fought on its own soil, or a people much poorer than ours. Rich countries at peace are not
    /// sent food parcels, so humanitarian aid cannot be bought as goodwill from anybody at all.
    /// </summary>
    public static bool InNeed(Campaign c, Nation donor, Nation recipient)
    {
        if (c is null || donor is null || recipient is null || recipient.Population <= 0) return false;
        var d = c.Defines;
        if (recipient.Stability < d.HumanitarianAidNeedStability) return true;
        foreach (var war in Warfare.OpenWars(c))
            if (!war.Ended && war.Involves(recipient.Id) && !war.Involves(donor.Id)) return true;
        if (donor.Population <= 0) return false;
        var theirs = recipient.Gdp / recipient.Population;
        var ours = donor.Gdp / donor.Population;
        return float.IsFinite(theirs) && float.IsFinite(ours) && theirs < ours * d.HumanitarianAidNeedIncomeShare;
    }

    /// <summary>What a development loan from this lender to this borrower would be, in billions.</summary>
    /// <summary>
    /// What a gesture's cash costs this issuer: the listed StateVisitCash, ShowOfForceCash or
    /// ExercisesCash scaled to its income (DiplomacyPrice). Nought for a gesture with no cash price.
    /// </summary>
    public static float Cash(Campaign c, Nation issuer, string gesture)
    {
        var d = c?.Defines;
        if (d is null) return 0f;
        var listed = gesture switch
        {
            "state_visit" => d.StateVisitCash,
            "show_of_force" => d.ShowOfForceCash,
            "joint_exercises" => d.ExercisesCash,
            _ => 0f
        };
        return DiplomacyPrice.Of(c, issuer, listed);
    }

    public static float LoanSize(Campaign c, Nation lender, Nation borrower)
    {
        var amount = MathF.Max(0f, lender.Gdp) * MathF.Max(0f, c.Defines.LoanShareOfGdp);
        amount = MathF.Min(amount, MathF.Max(0f, borrower.Gdp) * 0.05f);
        return float.IsFinite(amount) && amount >= 0.01f ? MathF.Round(amount, 3) : 0f;
    }

    /// <summary>Make the gesture. True when it was made; a refused mediation still costs the effort and is not made.</summary>
    public static bool Perform(Campaign c, int issuerId, int targetId, string gesture)
    {
        if (Refusal(c, issuerId, targetId, gesture).Length > 0) return false;
        var d = c.Defines;
        var issuer = c.NationById(issuerId);
        var target = c.NationById(targetId);
        var relation = CampaignDiplomacy.Get(c, issuerId, targetId);
        int cooldown;
        switch (gesture)
        {
            case "state_visit":
                issuer.PoliticalPoints -= d.StateVisitPoliticalCost;
                { var ledgerBefore = issuer.Treasury; issuer.Treasury -= Cash(c, issuer, "state_visit"); MoneyLedger.Book(issuer, MoneyFlow.Diplomacy, issuer.Treasury - ledgerBefore); }
                OpinionSystem.Remember(c, relation, "state_visit");
                if (relation.Opinion >= 35f && relation.Stance is DiplomaticStance.Neutral or DiplomaticStance.Rival)
                    relation.Stance = DiplomaticStance.Friendly;
                c.Chronicle.Record(c, "state_visit", issuerId, targetId);
                cooldown = d.StateVisitCooldownDays;
                break;
            case "show_of_force":
                { var ledgerBefore = issuer.Treasury; issuer.Treasury -= Cash(c, issuer, "show_of_force"); MoneyLedger.Book(issuer, MoneyFlow.Diplomacy, issuer.Treasury - ledgerBefore); }
                OpinionSystem.Remember(c, relation, "show_of_force");
                if (relation.A == targetId) relation.GrievanceA = MathF.Min(100f, relation.GrievanceA + d.ShowOfForceGrievance);
                else relation.GrievanceB = MathF.Min(100f, relation.GrievanceB + d.ShowOfForceGrievance);
                issuer.WarSupport = M.Clamp01(issuer.WarSupport + d.ShowOfForceWarSupport);
                target.WarSupport = M.Clamp01(target.WarSupport + d.ShowOfForceWarSupport * 0.5f);
                c.Chronicle.Record(c, "show_of_force", issuerId, targetId);
                cooldown = d.ShowOfForceCooldownDays;
                break;
            case "mediate":
                issuer.PoliticalPoints -= d.MediationPoliticalCost;
                var war = MediableWar(c, issuer, target);
                var ready = CanMediateArmistice(c, war);
                issuer.DecisionAvailableDay[Key(gesture, targetId)] = c.ElapsedDays + Math.Max(1, d.MediationCooldownDays);
                if (!ready)
                {
                    c.Chronicle.Record(c, "mediation_failed", issuerId, targetId, WarNames.Of(c, war), isPublic: false);
                    return false;
                }
                var attacker = war.AttackerId;
                var defender = war.DefenderId;
                var name = WarNames.Of(c, war);
                PeaceNegotiation.Resolve(c, war, attacker, PeaceTerms.Ceasefire);
                if (!war.Ended) return false;
                OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, issuerId, attacker), "mediated_peace");
                OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, issuerId, defender), "mediated_peace");
                c.Chronicle.Record(c, "peace_mediated", issuerId, target.Id, name);
                return true;
            case "loan":
                var amount = LoanSize(c, issuer, target);
                { var ledgerBefore = issuer.Treasury; issuer.Treasury -= amount; MoneyLedger.Book(issuer, MoneyFlow.ForeignAid, issuer.Treasury - ledgerBefore); }
                { var ledgerBefore = target.Treasury; target.Treasury += amount; MoneyLedger.Book(target, MoneyFlow.ForeignAid, target.Treasury - ledgerBefore); }
                relation.LoanLender = issuerId;
                relation.LoanOutstanding = MathF.Round(amount * (1f + MathF.Max(0f, d.LoanInterest)), 4);
                relation.LoanWeekly = relation.LoanOutstanding / Math.Max(1, d.LoanWeeks);
                relation.LoanMissedWeeks = 0;
                OpinionSystem.Remember(c, relation, "development_loan");
                c.Chronicle.Record(c, "development_loan", issuerId, targetId, Fmt.Money(amount));
                cooldown = 0;
                break;
            case "expel_diplomats":
                // A public slap: they think worse of us, the two are closer to a crisis, and the
                // hawks at home applaud. Nothing is gained but the gesture - which is the point.
                OpinionSystem.Remember(c, relation, "expelled_diplomats");
                relation.Tension = MathF.Min(100f, relation.Tension + d.ExpelTension);
                if (relation.A == targetId) relation.GrievanceA = MathF.Min(100f, relation.GrievanceA + d.ExpelGrievance);
                else relation.GrievanceB = MathF.Min(100f, relation.GrievanceB + d.ExpelGrievance);
                issuer.WarSupport = M.Clamp01(issuer.WarSupport + d.ExpelWarSupport);
                c.Chronicle.Record(c, "diplomats_expelled", issuerId, targetId);
                cooldown = d.ExpelCooldownDays;
                break;
            case "joint_exercises":
                issuer.PoliticalPoints -= d.ExercisesPoliticalCost;
                { var ledgerBefore = issuer.Treasury; issuer.Treasury -= Cash(c, issuer, "joint_exercises"); MoneyLedger.Book(issuer, MoneyFlow.Diplomacy, issuer.Treasury - ledgerBefore); }
                OpinionSystem.Remember(c, relation, "joint_exercises");
                // A message to whoever both of them stand against: it is heard, and remembered.
                var rivals = CommonRivals(c, issuer, target);
                for (var ri = 0; ri < rivals.Count; ri++)
                {
                    var rival = rivals[ri];
                    var tieA = CampaignDiplomacy.Get(c, issuer.Id, rival.Id);
                    tieA.Tension = MathF.Min(100f, tieA.Tension + d.ExercisesRivalTension);
                    OpinionSystem.Remember(c, tieA, "exercises_against_us");
                    var tieB = CampaignDiplomacy.Get(c, target.Id, rival.Id);
                    tieB.Tension = MathF.Min(100f, tieB.Tension + d.ExercisesRivalTension);
                    OpinionSystem.Remember(c, tieB, "exercises_against_us");
                }
                var rivalNames = rivals.Count switch
                {
                    0 => "",
                    1 => rivals[0].DisplayName,
                    2 => $"{rivals[0].DisplayName}, {rivals[1].DisplayName}",
                    _ => $"{rivals[0].DisplayName}, {rivals[1].DisplayName}, {rivals[2].DisplayName}"
                };
                c.Chronicle.Record(c, "joint_exercises", issuerId, targetId, rivalNames);
                cooldown = d.ExercisesCooldownDays;
                break;
            case "apology":
                // Say it, pay for it, and let both of them start forgetting. Every fading grievance
                // between the two keeps only half of what it is worth today and fades from there at
                // the pace it already had; the ones held as a bystander keep a quarter. Standing
                // conditions - an embargo in force, a border, fear of the bigger country - are not
                // memories, and an apology does not touch them: they end when the condition does.
                var compensation = ApologyCost(c, issuer);
                issuer.PoliticalPoints -= d.ApologyPoliticalCost;
                { var ledgerBefore = issuer.Treasury; issuer.Treasury -= compensation; MoneyLedger.Book(issuer, MoneyFlow.ForeignAid, issuer.Treasury - ledgerBefore); }
                { var ledgerBefore = target.Treasury; target.Treasury += compensation; MoneyLedger.Book(target, MoneyFlow.ForeignAid, target.Treasury - ledgerBefore); }
                OpinionWitnesses.Forgive(c, relation, d.ApologyForgiveness, d.ApologyThirdPartyForgiveness);
                OpinionSystem.Remember(c, relation, "apology_accepted");
                if (relation.A == targetId) relation.GrievanceA = MathF.Max(0f, relation.GrievanceA - d.ApologyGrievanceRelief);
                else relation.GrievanceB = MathF.Max(0f, relation.GrievanceB - d.ApologyGrievanceRelief);
                relation.Tension = MathF.Max(0f, relation.Tension - d.ApologyTensionRelief);
                c.Chronicle.Record(c, "apology", issuerId, targetId, Fmt.Money(compensation));
                cooldown = d.ApologyCooldownDays;
                break;
            case "humanitarian_aid":
                // Help, asked for nothing. Remembered whatever they thought of us before, and the
                // grievances they hold as a bystander - our bad name, a test, a coup they disliked -
                // count for a little less once our help has arrived.
                var aid = HumanitarianAidSize(c, issuer, target);
                { var ledgerBefore = issuer.Treasury; issuer.Treasury -= aid; MoneyLedger.Book(issuer, MoneyFlow.ForeignAid, issuer.Treasury - ledgerBefore); }
                { var ledgerBefore = target.Treasury; target.Treasury += aid; MoneyLedger.Book(target, MoneyFlow.ForeignAid, target.Treasury - ledgerBefore); }
                OpinionSystem.Remember(c, relation, "humanitarian_aid");
                OpinionWitnesses.Forgive(c, relation, 0f, d.HumanitarianAidThirdPartyForgiveness);
                c.Chronicle.Record(c, "humanitarian_aid", issuerId, targetId, Fmt.Money(aid));
                cooldown = d.HumanitarianAidCooldownDays;
                break;
            default:
                return false;
        }
        if (cooldown > 0) issuer.DecisionAvailableDay[Key(gesture, targetId)] = c.ElapsedDays + cooldown;
        relation.LastActionDay = c.ElapsedDays;
        c.MarkStrategicChange();
        return true;
    }

    /// <summary>
    /// The week's instalments on every loan. A borrower whose treasury is empty misses one; a
    /// borrower that misses LoanDefaultWeeks in a row has defaulted - the rest is written off,
    /// and the lender does not forget it.
    /// </summary>
    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same weekly instalments, yielded between batches of relationships.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || c.ElapsedDays <= 0 || c.ElapsedDays % 7 != 6) yield break;
        var scanned = 0;
        foreach (var relation in c.Relations.Values)
        {
            if (++scanned % 256 == 0) yield return 0;
            if (relation.LoanLender < 0 || relation.LoanOutstanding <= 0f) continue;
            var lender = c.NationById(relation.LoanLender);
            var borrower = c.NationById(relation.LoanLender == relation.A ? relation.B : relation.A);
            if (lender is null || borrower is null || borrower.Population <= 0)
            {
                relation.LoanLender = -1; relation.LoanOutstanding = 0f; relation.LoanWeekly = 0f;
                continue;
            }
            var due = MathF.Min(relation.LoanWeekly, relation.LoanOutstanding);
            if (borrower.Treasury >= due)
            {
                { var ledgerBefore = borrower.Treasury; borrower.Treasury -= due; MoneyLedger.Book(borrower, MoneyFlow.StateLoans, borrower.Treasury - ledgerBefore); }
                { var ledgerBefore = lender.Treasury; lender.Treasury += due; MoneyLedger.Book(lender, MoneyFlow.StateLoans, lender.Treasury - ledgerBefore); }
                relation.LoanOutstanding = MathF.Max(0f, relation.LoanOutstanding - due);
                relation.LoanMissedWeeks = 0;
                if (relation.LoanOutstanding <= 0.0001f) { relation.LoanLender = -1; relation.LoanOutstanding = 0f; relation.LoanWeekly = 0f; }
                continue;
            }
            if (++relation.LoanMissedWeeks < Math.Max(1, c.Defines.LoanDefaultWeeks)) continue;
            OpinionSystem.Remember(c, relation, "defaulted_on_loan");
            c.Chronicle.Record(c, "loan_default", borrower.Id, lender.Id, Fmt.Money(relation.LoanOutstanding));
            relation.LoanLender = -1; relation.LoanOutstanding = 0f; relation.LoanWeekly = 0f; relation.LoanMissedWeeks = 0;
        }
    }

    /// <summary>
    /// Whether a relationship is worth mending for this government: a great power, a neighbour, or a
    /// country a noticeable share of our trade runs through. The AI does not apologise to the world.
    /// </summary>
    private static bool Matters(Campaign c, Nation nation, Nation other, IReadOnlySet<int> greatPowers = null) =>
        (greatPowers ?? AllianceSystem.GreatPowers(c)).Contains(other.Id) ||
        CampaignDiplomacy.SharesLandBorder(c, nation.MapIndex, other.MapIndex) ||
        nation.TradeDependenceOn(other.Id) >= 0.02f;

    /// <summary>
    /// The computer's governments make the same gestures, once a month each, on its own day:
    /// a visit to the partner it values and does not yet love, a show of force at the rival it is
    /// closest to a crisis with, mediation by a great power in a long war between two countries
    /// it gets on with, and a loan from a rich treasury to a poor friend.
    /// </summary>
    public static void AiTick(Campaign c)
    {
        foreach (var _ in AiTickStages(c)) { }
    }

    /// <summary>
    /// The same monthly gesture pass, yielded between countries.
    ///
    /// Two things were wrong with its shape on a fifty-year world. Every country that thought
    /// built its own relation list by walking the whole table - 64 000 pairs at five hundred
    /// countries - and grew a fresh list every time; and each of the eight questions below ran
    /// its own LINQ pipeline, filtering into a list and sorting it. The lists are now gathered
    /// once for the countries that act, and each question is a single walk of that list.
    /// </summary>
    public static IEnumerable<int> AiTickStages(Campaign c)
    {
        if (c is null || c.ElapsedDays <= 0) yield break;
        var pending = ActingToday(c);
        if (pending.Count == 0) yield break;
        var byNation = new Dictionary<int, List<DiplomaticRelation>>(pending.Count);
        foreach (var _ in GatherStages(c, pending, byNation)) yield return 0;
        var inserted = new List<DiplomaticRelation>();
        c.RelationInsertSink = inserted;
        try
        {
            foreach (var nation in pending)
            {
                // A gesture in this pass can open a relation the table did not hold when the lists
                // were gathered. Append any newly opened/woken pairs directly rather than rescanning
                // all tens of thousands of relations.
                if (inserted.Count > 0)
                {
                    for (var i = 0; i < inserted.Count; i++)
                    {
                        var rel = inserted[i];
                        if (byNation.TryGetValue(rel.A, out var listA)) listA.Add(rel);
                        if (byNation.TryGetValue(rel.B, out var listB)) listB.Add(rel);
                    }
                    inserted.Clear();
                }
                var relations = byNation.TryGetValue(nation.Id, out var pairs) ? pairs : NoRelations;

                if (GestureRoll(c, nation.Id, 1) < 0.12f && Warmest(nation.Id, relations) is { } partner)
                    Perform(c, nation.Id, Other(nation.Id, partner), "state_visit");

                if (GestureRoll(c, nation.Id, 2) < 0.15f && Crisis(nation.Id, relations, c.Defines.TensionCrisisAt) is { } rival)
                    Perform(c, nation.Id, Other(nation.Id, rival), "show_of_force");

                if (GestureRoll(c, nation.Id, 3) < 0.10f && AllianceSystem.GreatPowers(c).Contains(nation.Id))
                {
                    yield return 0;
                    var mediable = new List<StrategicWar>();
                    foreach (var w in Warfare.OpenWars(c))
                        if (!w.Ended && !w.Involves(nation.Id) && c.ElapsedDays - w.StartedDay > 180)
                            mediable.Add(w);
                    if (mediable.Count > 1) mediable.Sort((a, b) => a.Id.CompareTo(b.Id));
                    for (var i = 0; i < mediable.Count; i++)
                    {
                        if (i > 0) yield return 0;
                        var war = mediable[i];
                        if (CanMediateArmistice(c, war) && Refusal(c, nation.Id, war.DefenderId, "mediate").Length == 0)
                        {
                            yield return 0;
                            Perform(c, nation.Id, war.DefenderId, "mediate");
                            break;
                        }
                    }
                }

                // Diplomats expelled from a rival already deep in a crisis with us: rare, and loud.
                if (GestureRoll(c, nation.Id, 5) < 0.04f && Expel(nation.Id, relations, c.Defines.TensionCrisisAt) is { } offender)
                    Perform(c, nation.Id, Other(nation.Id, offender), "expel_diplomats");

                // Exercises with an ally, when there is somebody both of them want to impress.
                if (GestureRoll(c, nation.Id, 6) < 0.08f && ExerciseAlly(nation.Id, relations) is { } ally &&
                    c.NationById(Other(nation.Id, ally)) is { } allied)
                {
                    yield return 0;
                    if (CommonRivals(c, nation, allied).Count > 0)
                        Perform(c, nation.Id, allied.Id, "joint_exercises");
                }

                if (GestureRoll(c, nation.Id, 4) < 0.05f && nation.Gdp > 500f && LoanFriend(c, nation, relations) is { } friend)
                    Perform(c, nation.Id, Other(nation.Id, friend), "loan");

                // Mending a relationship that has gone bad with a country that matters to us - a
                // great power, a neighbour, a trading partner - and that we are not planning to fight.
                // The same apology a player can make, on the same terms; the AI reaches for it rarely,
                // and only where the grievances between the two are real.
                yield return 0;
                if (GestureRoll(c, nation.Id, 7) < 0.10f && Estranged(c, nation, relations) is { } estranged)
                    Perform(c, nation.Id, Other(nation.Id, estranged), "apology");

                // Help for a country in need that thinks poorly or only lukewarmly of us: the cheapest
                // honest way back from a cold relationship, and one that does not need them to agree.
                yield return 0;
                if (GestureRoll(c, nation.Id, 8) < 0.06f && nation.Gdp > 50f && Needy(c, nation, relations) is { } needy)
                    Perform(c, nation.Id, Other(nation.Id, needy), "humanitarian_aid");

                yield return 0;
            }
        }
        finally
        {
            c.RelationInsertSink = null;
        }
    }

    private static float GestureRoll(Campaign c, int nationId, int salt) =>
        Escalation.Roll(c.Seed, nationId, 0x6E5, c.ElapsedDays, salt);

    private static readonly List<DiplomaticRelation> NoRelations = new();

    /// <summary>The countries that act today, in the order the world lists them.</summary>
    private static List<Nation> ActingToday(Campaign c)
    {
        var pending = new List<Nation>();
        foreach (var nation in c.Nations)
        {
            if (nation.Movement is not null || nation.Population <= 0 || c.Fallen.Contains(nation.Id)) continue;
            if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Diplomacy)) continue;
            if ((c.ElapsedDays + nation.Id * 7) % 30 != 0) continue;
            pending.Add(nation);
        }
        return pending;
    }

    /// <summary>
    /// One walk of the relation table, giving each acting country its own pairs in the order the
    /// table holds them. Held for the pass and never shared: a process can be running a host's
    /// world and a client's snapshot at once, and two of them must not read each other's lists.
    /// </summary>
    private static IEnumerable<int> GatherStages(Campaign c, List<Nation> pending, Dictionary<int, List<DiplomaticRelation>> byNation)
    {
        foreach (var list in byNation.Values) list.Clear();
        foreach (var nation in pending)
        {
            if (!byNation.TryGetValue(nation.Id, out var list)) byNation[nation.Id] = list = new List<DiplomaticRelation>();
            list.Clear();
        }
        var scanned = 0;
        foreach (var relation in c.Relations.Values)
        {
            if (byNation.TryGetValue(relation.A, out var first)) first.Add(relation);
            if (byNation.TryGetValue(relation.B, out var second)) second.Add(relation);
            if (++scanned % 1024 == 0) yield return 0;
        }
    }

    private static int Other(int nationId, DiplomaticRelation relation) =>
        relation.A == nationId ? relation.B : relation.A;

    // The eight questions below were each a LINQ pipeline: filter into a list, sort it, take the
    // first. Each is now one walk that keeps the same candidate - the same key, the same id as
    // the tie-break - without the list, the sort, or the closures that made them.

    /// <summary>A friend worth visiting: friendly or allied, or merely trading, and opinion still short of warm.</summary>
    private static DiplomaticRelation Warmest(int nationId, List<DiplomaticRelation> relations)
    {
        DiplomaticRelation best = null;
        foreach (var relation in relations)
        {
            if (relation.Stance is not (DiplomaticStance.Allied or DiplomaticStance.Friendly) && !relation.TradeAgreement) continue;
            if (relation.Opinion <= 0f || relation.Opinion >= 45f) continue;
            if (best is null || relation.Opinion < best.Opinion ||
                (relation.Opinion == best.Opinion && Other(nationId, relation) < Other(nationId, best))) best = relation;
        }
        return best;
    }

    /// <summary>The rival closest to a crisis, for a show of force.</summary>
    private static DiplomaticRelation Crisis(int nationId, List<DiplomaticRelation> relations, float threshold)
    {
        DiplomaticRelation best = null;
        foreach (var relation in relations)
        {
            if (relation.Tension < threshold || relation.Stance == DiplomaticStance.AtWar) continue;
            if (best is null || relation.Tension > best.Tension ||
                (relation.Tension == best.Tension && Other(nationId, relation) < Other(nationId, best))) best = relation;
        }
        return best;
    }

    /// <summary>The rival deep enough in a crisis, and hostile enough, to have its diplomats sent home.</summary>
    private static DiplomaticRelation Expel(int nationId, List<DiplomaticRelation> relations, float threshold)
    {
        DiplomaticRelation best = null;
        foreach (var relation in relations)
        {
            if (relation.Tension < threshold || relation.Opinion > -50f || relation.Stance == DiplomaticStance.AtWar) continue;
            if (best is null || relation.Tension > best.Tension ||
                (relation.Tension == best.Tension && Other(nationId, relation) < Other(nationId, best))) best = relation;
        }
        return best;
    }

    /// <summary>An ally worth exercises with: the warmest one, before the common rival is asked about.</summary>
    private static DiplomaticRelation ExerciseAlly(int nationId, List<DiplomaticRelation> relations)
    {
        DiplomaticRelation best = null;
        foreach (var relation in relations)
        {
            if (relation.Stance != DiplomaticStance.Allied || relation.Opinion < 20f) continue;
            if (best is null || relation.Opinion > best.Opinion ||
                (relation.Opinion == best.Opinion && Other(nationId, relation) < Other(nationId, best))) best = relation;
        }
        return best;
    }

    /// <summary>A warm friend poor enough that a loan from this treasury means something.</summary>
    private static DiplomaticRelation LoanFriend(Campaign c, Nation nation, List<DiplomaticRelation> relations)
    {
        DiplomaticRelation best = null;
        foreach (var relation in relations)
        {
            if (relation.Opinion < 30f) continue;
            var other = Other(nation.Id, relation);
            if (c.NationById(other) is not { } poor || poor.Gdp >= nation.Gdp * 0.05f) continue;
            if (best is null || relation.Opinion > best.Opinion ||
                (relation.Opinion == best.Opinion && other < Other(nation.Id, best))) best = relation;
        }
        return best;
    }

    /// <summary>The coldest relationship worth mending: one that matters, and that nobody is planning to fight.</summary>
    private static DiplomaticRelation Estranged(Campaign c, Nation nation, List<DiplomaticRelation> relations)
    {
        var d = c.Defines;
        if (nation.PoliticalPoints < d.ApologyPoliticalCost || nation.Treasury < ApologyCost(c, nation))
            return null;
        var gps = AllianceSystem.GreatPowers(c);
        DiplomaticRelation best = null;
        foreach (var relation in relations)
        {
            if (relation.Opinion > -30f || relation.Stance == DiplomaticStance.AtWar) continue;
            var other = Other(nation.Id, relation);
            if (other == nation.WarPlanTargetId) continue;
            if (OpinionWitnesses.Grievances(c, relation) > d.ApologyMinimumGrievance) continue;
            if (c.NationById(other) is not { } them || them.Movement is not null || them.Population <= 0 || c.Fallen.Contains(other)) continue;
            if (nation.DecisionAvailableDay.TryGetValue(Key("apology", them.Id), out var avail) && avail > c.ElapsedDays) continue;
            if (!Matters(c, nation, them, gps)) continue;
            if (Refusal(c, nation.Id, them.Id, "apology").Length != 0) continue;
            if (best is null || relation.Opinion < best.Opinion ||
                (relation.Opinion == best.Opinion && other < Other(nation.Id, best))) best = relation;
        }
        return best;
    }

    /// <summary>A country in need that thinks poorly or only lukewarmly of us: the cheapest way back.</summary>
    private static DiplomaticRelation Needy(Campaign c, Nation nation, List<DiplomaticRelation> relations)
    {
        var d = c.Defines;
        if (nation.Treasury < d.HumanitarianAidMinimumCash * 2f) return null;
        var gps = AllianceSystem.GreatPowers(c);
        DiplomaticRelation best = null;
        foreach (var relation in relations)
        {
            if (relation.Opinion <= -70f || relation.Opinion >= 20f) continue;
            if (relation.Stance == DiplomaticStance.AtWar) continue;
            var other = Other(nation.Id, relation);
            if (other == nation.WarPlanTargetId) continue;
            if (c.NationById(other) is not { } them || them.Movement is not null || them.Population <= 0 || c.Fallen.Contains(other)) continue;
            if (nation.DecisionAvailableDay.TryGetValue(Key("humanitarian_aid", them.Id), out var avail) && avail > c.ElapsedDays) continue;
            if (!Matters(c, nation, them, gps)) continue;
            if (Refusal(c, nation.Id, them.Id, "humanitarian_aid").Length != 0) continue;
            if (best is null || relation.Opinion < best.Opinion ||
                (relation.Opinion == best.Opinion && other < Other(nation.Id, best))) best = relation;
        }
        return best;
    }
}
/// <summary>A diplomatic gesture made by a government (DiplomaticGestures): state_visit, show_of_force, mediate, loan,
/// expel_diplomats, joint_exercises, apology or humanitarian_aid.</summary>
public sealed class DiplomaticGestureCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public string Gesture { get; init; } = "";
    public bool Done { get; private set; }
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        Done = false;
        if (!MechanicFeatures.Allows(campaign, this)) return;
        Refusal = DiplomaticGestures.Refusal(campaign, IssuerId, TargetId, Gesture);
        if (Refusal.Length > 0) return;
        Done = DiplomaticGestures.Perform(campaign, IssuerId, TargetId, Gesture);
        if (!Done && Gesture == "mediate") Refusal = Loc.Get("Neither side is ready to stop yet; the effort is spent.");
    }
}
