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
        if (issuer.DecisionAvailableDay.GetValueOrDefault(Key(gesture, targetId)) > c.ElapsedDays)
            return Loc.Format("Not again so soon: available in {0} days.", issuer.DecisionAvailableDay[Key(gesture, targetId)] - c.ElapsedDays);
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
                return issuer.Treasury < d.StateVisitCash ? Loc.Get("The treasury cannot pay for it.") : "";
            case "show_of_force":
                if (atWar) return Loc.Get("We are already at war with them.");
                if (relation is { Stance: DiplomaticStance.Allied }) return Loc.Get("Not against an ally.");
                if (!CampaignDiplomacy.SharesLandBorder(c, issuer.MapIndex, target.MapIndex))
                    return Loc.Get("Only on a border we share with them.");
                return issuer.Treasury < d.ShowOfForceCash ? Loc.Get("The treasury cannot pay for it.") : "";
            case "mediate":
                var war = MediableWar(c, issuer, target);
                if (war is null) return Loc.Get("They are not at war with anybody we are not fighting ourselves.");
                if (issuer.PoliticalPoints < d.MediationPoliticalCost)
                    return Loc.Format("Insufficient political credit: need {0:0.##}, available {1:0.##}.", d.MediationPoliticalCost, issuer.PoliticalPoints);
                foreach (var side in new[] { war.AttackerId, war.DefenderId })
                    if (CampaignDiplomacy.Peek(c, issuerId, side) is { Opinion: < 0f })
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
                return issuer.Treasury < d.ExercisesCash ? Loc.Get("The treasury cannot pay for it.") : "";
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
        foreach (var other in c.Nations)
        {
            if (other.Id == a.Id || other.Id == b.Id || other.Movement is not null || other.Population <= 0) continue;
            bool Rival(Nation n)
            {
                var tie = CampaignDiplomacy.Peek(c, n.Id, other.Id);
                return tie is not null && (tie.Stance is DiplomaticStance.Rival or DiplomaticStance.AtWar ||
                                           tie.Tension >= c.Defines.TensionCrisisAt * 0.6f || tie.Opinion <= -40f);
            }
            if (Rival(a) && Rival(b)) rivals.Add(other);
        }
        return rivals;
    }

    /// <summary>The war of theirs we could mediate: one they lead or fight in that we are not part of, the oldest first.</summary>
    public static StrategicWar MediableWar(Campaign c, Nation issuer, Nation target) =>
        c.Wars.Where(w => !w.Ended && w.Involves(target.Id) && !w.Involves(issuer.Id))
            .OrderBy(w => w.StartedDay).ThenBy(w => w.Id).FirstOrDefault();

    /// <summary>A third party cannot bypass either government's negotiation gate or a human signature.</summary>
    public static bool CanMediateArmistice(Campaign c, StrategicWar war)
    {
        if (c is null || war is null || war.Ended || war.PendingOfferFromId >= 0 ||
            war.PendingOffer is not null || WarWeariness.PlayedByAnyone(c, war) ||
            CivilWarPersistence.NegotiationLocked(c, war) ||
            c.ElapsedDays - war.StartedDay < c.Defines.AiMinimumWarDays) return false;
        if (c.NationById(war.AttackerId) is null || c.NationById(war.DefenderId) is null) return false;
        foreach (var side in new[] { war.AttackerId, war.DefenderId })
            if (PeaceNegotiation.BlockReason(c, war, side, PeaceTerms.Ceasefire).Length > 0 ||
                PeaceNegotiation.Acceptance(c, war, side, PeaceTerms.Ceasefire) < c.Defines.MediationAcceptance)
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
        foreach (var war in c.Wars)
            if (!war.Ended && war.Involves(recipient.Id) && !war.Involves(donor.Id)) return true;
        if (donor.Population <= 0) return false;
        var theirs = recipient.Gdp / recipient.Population;
        var ours = donor.Gdp / donor.Population;
        return float.IsFinite(theirs) && float.IsFinite(ours) && theirs < ours * d.HumanitarianAidNeedIncomeShare;
    }

    /// <summary>What a development loan from this lender to this borrower would be, in billions.</summary>
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
                issuer.Treasury -= d.StateVisitCash;
                OpinionSystem.Remember(c, relation, "state_visit");
                if (relation.Opinion >= 35f && relation.Stance is DiplomaticStance.Neutral or DiplomaticStance.Rival)
                    relation.Stance = DiplomaticStance.Friendly;
                c.Chronicle.Record(c, "state_visit", issuerId, targetId);
                cooldown = d.StateVisitCooldownDays;
                break;
            case "show_of_force":
                issuer.Treasury -= d.ShowOfForceCash;
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
                foreach (var side in new[] { attacker, defender })
                    OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, issuerId, side), "mediated_peace");
                c.Chronicle.Record(c, "peace_mediated", issuerId, target.Id, name);
                return true;
            case "loan":
                var amount = LoanSize(c, issuer, target);
                issuer.Treasury -= amount;
                target.Treasury += amount;
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
                issuer.Treasury -= d.ExercisesCash;
                OpinionSystem.Remember(c, relation, "joint_exercises");
                // A message to whoever both of them stand against: it is heard, and remembered.
                var rivals = CommonRivals(c, issuer, target);
                foreach (var rival in rivals)
                    foreach (var side in new[] { issuer, target })
                    {
                        var tie = CampaignDiplomacy.Get(c, side.Id, rival.Id);
                        tie.Tension = MathF.Min(100f, tie.Tension + d.ExercisesRivalTension);
                        OpinionSystem.Remember(c, tie, "exercises_against_us");
                    }
                c.Chronicle.Record(c, "joint_exercises", issuerId, targetId,
                    rivals.Count == 0 ? "" : string.Join(", ", rivals.Take(3).Select(r => r.DisplayName)));
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
                issuer.Treasury -= compensation;
                target.Treasury += compensation;
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
                issuer.Treasury -= aid;
                target.Treasury += aid;
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
            if (++scanned % 512 == 0) yield return 0;
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
                borrower.Treasury -= due;
                lender.Treasury += due;
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
    private static bool Matters(Campaign c, Nation nation, Nation other) =>
        AllianceSystem.GreatPowers(c).Contains(other.Id) ||
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

    /// <summary>The same monthly gesture pass, yielded between batches of countries.</summary>
    public static IEnumerable<int> AiTickStages(Campaign c)
    {
        if (c is null || c.ElapsedDays <= 0) yield break;
        var ticked = 0;
        foreach (var nation in c.Nations)
        {
            if (++ticked % 8 == 0) yield return 0;
            if (nation.Movement is not null || nation.Population <= 0 || c.Fallen.Contains(nation.Id)) continue;
            if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Diplomacy)) continue;
            if ((c.ElapsedDays + nation.Id * 7) % 30 != 0) continue;
            float Roll(int salt) => Escalation.Roll(c.Seed, nation.Id, 0x6E5, c.ElapsedDays, salt);
            var relations = c.Relations.Values.Where(r => r.A == nation.Id || r.B == nation.Id).ToList();
            int Other(DiplomaticRelation r) => r.A == nation.Id ? r.B : r.A;

            if (Roll(1) < 0.12f && relations.Where(r => r.Stance is DiplomaticStance.Allied or DiplomaticStance.Friendly || r.TradeAgreement)
                    .Where(r => r.Opinion is > 0f and < 45f).OrderBy(r => r.Opinion).ThenBy(Other).FirstOrDefault() is { } partner)
                Perform(c, nation.Id, Other(partner), "state_visit");

            if (Roll(2) < 0.15f && relations.Where(r => r.Tension >= c.Defines.TensionCrisisAt && r.Stance != DiplomaticStance.AtWar)
                    .OrderByDescending(r => r.Tension).ThenBy(Other).FirstOrDefault() is { } rival)
                Perform(c, nation.Id, Other(rival), "show_of_force");

            if (Roll(3) < 0.10f && AllianceSystem.GreatPowers(c).Contains(nation.Id))
                foreach (var war in c.Wars.Where(w => !w.Ended && !w.Involves(nation.Id) && c.ElapsedDays - w.StartedDay > 180).OrderBy(w => w.Id))
                    if (CanMediateArmistice(c, war) && Refusal(c, nation.Id, war.DefenderId, "mediate").Length == 0)
                    {
                        Perform(c, nation.Id, war.DefenderId, "mediate");
                        break;
                    }

            // Diplomats expelled from a rival already deep in a crisis with us: rare, and loud.
            if (Roll(5) < 0.04f && relations.Where(r => r.Tension >= c.Defines.TensionCrisisAt && r.Opinion <= -50f &&
                    r.Stance != DiplomaticStance.AtWar).OrderByDescending(r => r.Tension).ThenBy(Other).FirstOrDefault() is { } offender)
                Perform(c, nation.Id, Other(offender), "expel_diplomats");

            // Exercises with an ally, when there is somebody both of them want to impress.
            if (Roll(6) < 0.08f && relations.Where(r => r.Stance == DiplomaticStance.Allied && r.Opinion >= 20f)
                    .OrderByDescending(r => r.Opinion).ThenBy(Other).FirstOrDefault() is { } ally &&
                c.NationById(Other(ally)) is { } allied && CommonRivals(c, nation, allied).Count > 0)
                Perform(c, nation.Id, allied.Id, "joint_exercises");

            if (Roll(4) < 0.05f && nation.Gdp > 500f &&
                relations.Where(r => r.Opinion >= 30f && c.NationById(Other(r)) is { } poor && poor.Gdp < nation.Gdp * 0.05f)
                    .OrderByDescending(r => r.Opinion).ThenBy(Other).FirstOrDefault() is { } friend)
                Perform(c, nation.Id, Other(friend), "loan");

            // Mending a relationship that has gone bad with a country that matters to us - a
            // great power, a neighbour, a trading partner - and that we are not planning to fight.
            // The same apology a player can make, on the same terms; the AI reaches for it rarely,
            // and only where the grievances between the two are real.
            if (Roll(7) < 0.10f && relations.Where(r => r.Opinion <= -30f && r.Stance != DiplomaticStance.AtWar &&
                        Other(r) != nation.WarPlanTargetId && c.NationById(Other(r)) is { } them && Matters(c, nation, them) &&
                        Refusal(c, nation.Id, them.Id, "apology").Length == 0)
                    .OrderBy(r => r.Opinion).ThenBy(Other).FirstOrDefault() is { } estranged)
                Perform(c, nation.Id, Other(estranged), "apology");

            // Help for a country in need that thinks poorly or only lukewarmly of us: the cheapest
            // honest way back from a cold relationship, and one that does not need them to agree.
            if (Roll(8) < 0.06f && nation.Gdp > 50f && relations.Where(r => r.Opinion is > -70f and < 20f &&
                        r.Stance != DiplomaticStance.AtWar && Other(r) != nation.WarPlanTargetId &&
                        c.NationById(Other(r)) is { } them && Matters(c, nation, them) &&
                        Refusal(c, nation.Id, them.Id, "humanitarian_aid").Length == 0)
                    .OrderBy(r => r.Opinion).ThenBy(Other).FirstOrDefault() is { } needy)
                Perform(c, nation.Id, Other(needy), "humanitarian_aid");
        }
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
