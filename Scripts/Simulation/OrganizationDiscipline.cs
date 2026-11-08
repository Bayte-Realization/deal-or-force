using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class InternationalOrganization
{
    /// <summary>
    /// Members whose seat is suspended, by country id, and the day it was. A suspended member is
    /// taken out of <see cref="Members"/> for as long as the suspension lasts, so it loses every
    /// benefit the membership carried - the vote, the customs union, the common defence, the fund -
    /// without any of those systems having to know about suspensions; the seat is kept here until
    /// it is given back (readmission) or taken away for good (expulsion, or the member walking out).
    /// </summary>
    public Dictionary<int, int> Suspended = new();
    /// <summary>Why each suspended member was suspended (OrganizationDiscipline.Cause keys).</summary>
    public Dictionary<int, string> SuspendedFor = new();
}

public sealed partial class Defines
{
    /// <summary>
    /// Suspension and expulsion of a single member (OrganizationDiscipline). Off, a member that
    /// attacks a fellow member is thrown out on the spot, as before, and nothing else is ever voted on.
    /// </summary>
    public bool OrganizationDisciplineEnabled { get; set; } = true;
    /// <summary>A military coup this recent breaks the constitutional order of an assembly of democracies.</summary>
    public int OrganizationCoupDays { get; set; } = 365;
    /// <summary>What the other members must think of one of their own, on average, before it is a pariah among them.</summary>
    public float OrganizationPariahOpinion { get; set; } = -55f;
    /// <summary>The weekly chance that a member with a cause against it is put to a suspension vote.</summary>
    public float OrganizationSuspendMotionChance { get; set; } = 0.2f;
    /// <summary>How long a suspension stands before a member whose cause persists is put to an expulsion vote.</summary>
    public int OrganizationExpelAfterDays { get; set; } = 540;
    /// <summary>How long a suspension stands at least before the seat can be given back.</summary>
    public int OrganizationReadmitAfterDays { get; set; } = 365;
    /// <summary>The weekly chance a ripe expulsion or readmission is put to the vote.</summary>
    public float OrganizationSeatMotionChance { get; set; } = 0.05f;
    /// <summary>The share of the other members that must vote for an expulsion (unanimity charters: all of them).</summary>
    public float OrganizationExpelMajority { get; set; } = 0.67f;
    /// <summary>A member this fond of the one suspended or expelled may walk out in protest.</summary>
    public float OrganizationProtestOpinion { get; set; } = 45f;
    /// <summary>The chance such a friend, already half out, walks out with it.</summary>
    public float OrganizationProtestChance { get; set; } = 0.5f;
    /// <summary>The chance a member put to an expulsion vote, and estranged, walks out before the vote.</summary>
    public float OrganizationPreemptiveExitChance { get; set; } = 0.5f;
    /// <summary>What an expulsion costs the expelled government at home, in stability.</summary>
    public float OrganizationExpulsionStability { get; set; } = 0.03f;
    /// <summary>Below this cohesion an organisation that was above it last week is in open crisis, and the world hears of it.</summary>
    public float OrganizationRiftCohesion { get; set; } = 0.3f;
}

/// <summary>
/// One member, judged by the rest: suspension, readmission and expulsion.
///
/// Organisations used to have one way to be rid of a member, and it was automatic: whoever
/// attacked a fellow member was out that week. Real assemblies do it in steps, by vote, and for
/// more than aggression. The Council of Europe suspended Russia the day after it invaded Ukraine
/// in 2022 and voted it out three weeks later - and Russia walked out the day before the vote.
/// The African Union and ECOWAS suspend a member after a coup and give the seat back after an
/// election; Mercosur suspended Venezuela for breaking its democratic clause. And the suspended
/// do not always wait: Mali, Burkina Faso and Niger left ECOWAS together in 2024 and founded the
/// Alliance of Sahel States.
///
/// So now: a member that attacks a fellow member is suspended at once and its expulsion is put to
/// the vote; a member whose army has overthrown its government (in an assembly most of whose
/// members are governed by elections), the first to use a nuclear weapon, one most of the others
/// already sanction, or one the rest have come to loathe, is put to a suspension vote. A suspended
/// member loses everything the seat carried - vote, customs union, common defence, fund. While its
/// cause stands it can be voted out; when the cause is gone it can be voted back in. Friends of the
/// accused walk out in protest, and walk out together into a rival bloc. Every vote is the ordinary
/// assembly vote (InternationalOrganizations.Tick) with the ordinary motion, so a player tables,
/// votes on and suffers exactly the same motions as everyone else.
/// </summary>
public static class OrganizationDiscipline
{
    public static bool Enabled(Campaign c) => c?.Defines.OrganizationDisciplineEnabled == true;

    /// <summary>
    /// An elected form of government run by a creed that keeps elections meaningful. The form
    /// alone made Russia and Belarus "presidential republics", and assemblies of autocracies
    /// suspended one another for coups as if they were the Council of Europe.
    /// </summary>
    public static bool IsDemocratic(Nation n) => n?.Government is "parliamentary_republic" or "presidential_republic" or
        "constitutional_monarchy" or "federation" or "direct_democracy" &&
        PoliticsCore.IdeologyOf(n).Authority < 0.5f;

    /// <summary>
    /// What, if anything, this member has done that its assembly could hold against it. Read from
    /// the world as it stands, every week, so a cause that is gone is gone.
    /// "aggression", "nuclear", "coup", "sanctions", "pariah", or "".
    /// </summary>
    public static string Cause(Campaign c, InternationalOrganization org, Nation member)
    {
        if (c is null || org is null || member is null) return "";
        var others = org.Members.Where(id => id != member.Id).ToList();
        if (others.Count == 0) return "";
        foreach (var war in c.Wars)
        {
            if (war.Ended || war.CivilConflict || war.AttackerId != member.Id) continue;
            if (war.Side(false).Any(others.Contains)) return "aggression";
        }
        if (member.NuclearFirstUseDay > -100000 && c.ElapsedDays - member.NuclearFirstUseDay < c.Defines.NuclearShadowDays) return "nuclear";
        // A coup is a breach of the charter only for an assembly of governments that answer to
        // elections: the Arab League never suspended Egypt for 2013, the African Union did.
        if (member.Government == "military_junta" && c.ElapsedDays - member.LastMilitaryCoupDay < Math.Max(30, c.Defines.OrganizationCoupDays))
        {
            var democracies = others.Count(id => IsDemocratic(c.NationById(id)));
            if (democracies * 10 >= others.Count * 7) return "coup";
        }
        var embargoing = others.Count(id => CampaignDiplomacy.Peek(c, id, member.Id)?.EmbargoBy(id) == true);
        if (others.Count >= 2 && embargoing * 2 > others.Count) return "sanctions";
        var opinion = 0f;
        foreach (var id in others) opinion += CampaignDiplomacy.Peek(c, id, member.Id)?.Opinion ?? 0f;
        // A pariah is loathed by the rest and has stopped belonging itself: a member merely
        // unpopular, or quarrelling with one neighbour, is not put out of the room for it.
        if (others.Count >= 2 && opinion / others.Count <= c.Defines.OrganizationPariahOpinion &&
            OrganizationDynamics.Estranged(c, org, member)) return "pariah";
        return "";
    }

    /// <summary>The same cause, for a suspended member: read against the members still seated.</summary>
    public static string StandingCause(Campaign c, InternationalOrganization org, Nation suspended)
    {
        var cause = Cause(c, org, suspended);
        // A junta's suspension lasts as long as the junta does, not as long as the memory of the coup.
        if (cause.Length == 0 && org.SuspendedFor.GetValueOrDefault(suspended.Id) == "coup" &&
            suspended.Government == "military_junta") cause = "coup";
        return cause;
    }

    /// <summary>
    /// How a government votes on a motion about one member's seat. Friends and allies vote no,
    /// and so does a government that has done the same thing itself; a country at war with the
    /// accused votes yes; the rest follow their opinion of it - leniently while there is a cause,
    /// hardly at all without one.
    /// </summary>
    public static bool Vote(Campaign c, InternationalOrganization org, Nation voter, OrganizationMotion m)
    {
        var target = c.NationById(m.TargetId);
        if (voter is null || target is null || voter.Id == target.Id) return false;
        var relation = CampaignDiplomacy.Peek(c, voter.Id, target.Id);
        var opinion = relation?.Opinion ?? 0f;
        var cause = org.Suspended.ContainsKey(target.Id) ? StandingCause(c, org, target) : Cause(c, org, target);
        if (m.Kind == "readmit")
            return cause.Length == 0 && opinion > -20f && !CampaignDiplomacy.AtWar(c, voter.Id, target.Id);
        if (CampaignDiplomacy.AtWar(c, voter.Id, target.Id)) return true;
        if (relation?.Stance == DiplomaticStance.Allied || opinion >= 50f || voter.AnswersTo(target.Id)) return false;
        // The pot does not vote against the kettle.
        if (cause == "coup" && voter.Government == "military_junta") return false;
        if (cause == "aggression" && c.Wars.Any(w => !w.Ended && !w.CivilConflict && w.IsAttacking(voter.Id) && w.IsAttacking(target.Id))) return false;
        if (m.Kind == "suspend") return cause.Length > 0 ? opinion < 30f : opinion < -45f;
        // Expulsion is for good: a member votes for it only against a government it has fallen out with.
        return cause.Length > 0 ? opinion < -10f : opinion < -55f;
    }

    /// <summary>Whether a finished vote on a seat carried: the accused has no vote, an expulsion needs two thirds.</summary>
    public static bool Carried(Campaign c, InternationalOrganization org, OrganizationMotion m, out int yes, out int voters)
    {
        var seated = org.Members.Where(id => id != m.TargetId).ToList();
        voters = seated.Count;
        yes = seated.Count(id => m.Votes.GetValueOrDefault(id));
        if (voters == 0) return false;
        if (InternationalOrganizations.Charter(org.CharterId)?.Unanimity == true) return yes == voters;
        if (m.Kind == "expel") return yes >= (int)MathF.Ceiling(voters * M.Clamp(c.Defines.OrganizationExpelMajority, 0.5f, 1f) - 0.0001f);
        return yes * 2 > voters;
    }

    /// <summary>A passed seat motion takes effect.</summary>
    public static bool Carry(Campaign c, InternationalOrganization org, OrganizationMotion m)
    {
        var target = c.NationById(m.TargetId);
        if (target is null) return false;
        switch (m.Kind)
        {
            case "suspend":
                if (!org.Members.Contains(target.Id)) return false;
                // The cause is read on the day the vote closes: a junta that held its election in
                // the meantime, or a war that ended, leaves the motion with nothing to carry.
                var cause = Cause(c, org, target);
                if (cause.Length == 0) return false;
                Suspend(c, org, target, cause, m.ProposerId);
                return true;
            case "expel":
                if (!org.Members.Contains(target.Id) && !org.Suspended.ContainsKey(target.Id)) return false;
                Expel(c, org, target, m);
                return true;
            case "readmit":
                if (!org.Suspended.ContainsKey(target.Id)) return false;
                Readmit(c, org, target);
                return true;
        }
        return false;
    }

    /// <summary>
    /// The organisation's week: who is suspended at once (an attack on a fellow member), who is
    /// put to a vote, who walks out first. Runs inside OrganizationDynamics.Review. True when the
    /// organisation was dissolved on the way.
    /// </summary>
    public static bool Week(Campaign c, InternationalOrganization org)
    {
        // Seats of countries that are no more.
        foreach (var id in org.Suspended.Keys.ToArray())
            if (c.NationById(id) is not { } held || !InternationalOrganizations.Eligible(c, held))
            { org.Suspended.Remove(id); org.SuspendedFor.Remove(id); }

        // An attack on a fellow member: suspended the same week, its expulsion tabled.
        foreach (var war in c.Wars)
        {
            if (war.Ended || war.CivilConflict || !org.Members.Contains(war.AttackerId)) continue;
            if (!war.Side(false).Any(id => id != war.AttackerId && org.Members.Contains(id))) continue;
            var aggressor = c.NationById(war.AttackerId);
            if (aggressor is null || org.Members.Count < 3)
            {
                // A pair has nobody to judge between them: the old rule, out at once.
                ExpelOutright(c, org, aggressor, war.DefenderId);
                if (org.Dissolved) return true;
                continue;
            }
            Suspend(c, org, aggressor, "aggression", war.DefenderId);
            if (org.Dissolved) return true;
        }

        if (org.Motion is not null || org.Members.Count < 2) return false;
        // The seats already in question come first: expel those whose cause stands, give back the rest.
        foreach (var (id, since) in org.Suspended.OrderBy(p => p.Key).ToArray())
        {
            var held = c.NationById(id);
            if (held is null) continue;
            var cause = StandingCause(c, org, held);
            var days = c.ElapsedDays - since;
            // A junta is kept suspended, not thrown out: the African Union and ECOWAS hold the seat
            // for the election that gives it back. Expulsion is for what a member does to others.
            var kind = cause.Length > 0 ? (cause != "coup" && days >= c.Defines.OrganizationExpelAfterDays ? "expel" : "")
                                        : (days >= c.Defines.OrganizationReadmitAfterDays ? "readmit" : "");
            if (kind.Length == 0) continue;
            if (MovementRules.Roll(c.Seed, id, c.ElapsedDays, 4810 + org.Id) >= c.Defines.OrganizationSeatMotionChance) continue;
            if (Table(c, org, held, kind)) return org.Dissolved;
        }
        if (org.Members.Count < 3) return false;
        foreach (var id in org.Members.ToArray())
        {
            var member = c.NationById(id);
            if (member is null) continue;
            var cause = Cause(c, org, member);
            if (cause.Length == 0) continue;
            if (MovementRules.Roll(c.Seed, id, c.ElapsedDays, 4820 + org.Id) >= c.Defines.OrganizationSuspendMotionChance) continue;
            if (Table(c, org, member, "suspend")) return org.Dissolved;
        }
        return false;
    }

    /// <summary>
    /// A member that would vote for it tables the motion, the leading power first. Nobody willing,
    /// no motion. An estranged member put to an expulsion vote may not wait for it.
    /// </summary>
    private static bool Table(Campaign c, InternationalOrganization org, Nation target, string kind)
    {
        var leader = InternationalOrganizations.Leader(c, org);
        var candidates = org.Members.Where(id => id != target.Id).Select(c.NationById)
            .Where(n => n is { IsHuman: false, IsSubject: false })
            .OrderBy(n => n.Id == leader?.Id ? 0 : 1).ThenByDescending(n => InternationalOrganizations.Influence(c, org, n)).ThenBy(n => n.Id);
        foreach (var proposer in candidates)
        {
            var probe = new OrganizationMotion { Kind = kind, TargetId = target.Id, ProposerId = proposer.Id };
            if (!Vote(c, org, proposer, probe)) continue;
            var command = new OrganizationCommand { IssuerId = proposer.Id, OrganizationId = org.Id, Action = "propose", Kind = kind, TargetId = target.Id };
            command.Apply(c);
            if (!command.Changed) return false;
            if (kind == "expel" && !target.IsHuman &&
                OrganizationDynamics.Estranged(c, org, target) &&
                MovementRules.Roll(c.Seed, target.Id, c.ElapsedDays, 4830 + org.Id) < c.Defines.OrganizationPreemptiveExitChance)
            {
                // Russia left the Council of Europe the day before the vote that would have expelled it.
                Withdraw(c, org, target, "organization_withdrew");
                if (org.Motion?.TargetId == target.Id) org.Motion = null;
            }
            return true;
        }
        return false;
    }

    /// <summary>Takes the seat: out of the members, kept among the suspended, the vote and every benefit gone.</summary>
    public static void Suspend(Campaign c, InternationalOrganization org, Nation member, string cause, int otherId = -1)
    {
        if (member is null || !org.Members.Contains(member.Id)) return;
        var name = InternationalOrganizations.DisplayName(c, org);
        org.Members.Remove(member.Id);
        org.UnpaidWeeks.Remove(member.Id);
        org.Motion?.Votes.Remove(member.Id);
        if (org.Motion is { } open && open.ProposerId == member.Id && open.Kind != "admission") org.Motion = null;
        if (org.CrisisMemberId == member.Id) { org.CrisisMemberId = -1; org.CrisisSinceDay = -1; }
        org.Suspended[member.Id] = c.ElapsedDays;
        org.SuspendedFor[member.Id] = string.IsNullOrEmpty(cause) ? "pariah" : cause;
        org.Cohesion = MathF.Max(0f, org.Cohesion - 0.03f);
        InternationalOrganizations.InvalidateDefencePartners();
        foreach (var id in org.Members)
        {
            if (CampaignDiplomacy.Get(c, member.Id, id) is not { } relation) continue;
            OpinionSystem.Remember(c, relation, cause == "aggression" ? "attacked_a_fellow_member" : "suspended_us");
        }
        var target = cause == "aggression" && otherId >= 0 ? otherId : -1;
        c.Chronicle.Record(c, "organization_suspended_" + org.SuspendedFor[member.Id], member.Id, target, name);
        if (org.Members.Count == 0) { OrganizationDynamics.DissolveNow(c, org); return; }
        Protest(c, org, member);
        c.MarkStrategicChange();
    }

    private static void Readmit(Campaign c, InternationalOrganization org, Nation member)
    {
        org.Suspended.Remove(member.Id);
        org.SuspendedFor.Remove(member.Id);
        if (!org.Members.Contains(member.Id)) { org.Members.Add(member.Id); org.Members.Sort(); }
        member.OrganizationBelongingAtStart.Remove(org.Id);
        InternationalOrganizations.InvalidateDefencePartners();
        c.Chronicle.Record(c, "organization_readmitted", member.Id, -1, InternationalOrganizations.DisplayName(c, org));
        c.MarkStrategicChange();
    }

    /// <summary>
    /// Voted out: the seat is gone for good. Those who voted for it are remembered by the expelled,
    /// it is remembered by them, a defence pact's alliances with it lapse, and its government pays at home.
    /// </summary>
    private static void Expel(Campaign c, InternationalOrganization org, Nation member, OrganizationMotion m)
    {
        var name = InternationalOrganizations.DisplayName(c, org);
        var wasSeated = org.Members.Remove(member.Id);
        org.Suspended.Remove(member.Id);
        org.SuspendedFor.Remove(member.Id);
        org.UnpaidWeeks.Remove(member.Id);
        member.OrganizationBelongingAtStart.Remove(org.Id);
        member.OrganizationConcessions.Remove(org.Id);
        if (org.CrisisMemberId == member.Id) { org.CrisisMemberId = -1; org.CrisisSinceDay = -1; }
        org.Cohesion = MathF.Max(0f, org.Cohesion - (wasSeated ? 0.06f : 0.03f));
        member.Stability = M.Clamp01(member.Stability - MathF.Max(0f, c.Defines.OrganizationExpulsionStability));
        foreach (var id in org.Members)
        {
            if (CampaignDiplomacy.Get(c, member.Id, id) is not { } relation) continue;
            if (m.Votes.GetValueOrDefault(id)) OpinionSystem.Remember(c, relation, "voted_to_expel_us");
            OpinionSystem.Remember(c, relation, "expelled_from_our_organization");
        }
        OrganizationDynamics.UnbindDefence(c, org, member);
        InternationalOrganizations.InvalidateDefencePartners();
        c.Chronicle.Record(c, "organization_expelled_vote", member.Id, -1, name);
        Protest(c, org, member);
        c.MarkStrategicChange();
    }

    /// <summary>A pair with no third member to judge: whoever attacks the other is simply out, as before.</summary>
    internal static void ExpelOutright(Campaign c, InternationalOrganization org, Nation aggressor, int victimId)
        => OrganizationDynamics.ExpelForAggression(c, org, aggressor, victimId);

    /// <summary>A suspended member gives up its seat itself, or a seated one walks out before the vote.</summary>
    public static void Withdraw(Campaign c, InternationalOrganization org, Nation member, string kind, int targetId = -1)
    {
        var name = InternationalOrganizations.DisplayName(c, org);
        org.Members.Remove(member.Id);
        org.Suspended.Remove(member.Id);
        org.SuspendedFor.Remove(member.Id);
        org.UnpaidWeeks.Remove(member.Id);
        org.Motion?.Votes.Remove(member.Id);
        member.OrganizationBelongingAtStart.Remove(org.Id);
        if (org.CrisisMemberId == member.Id) { org.CrisisMemberId = -1; org.CrisisSinceDay = -1; }
        foreach (var id in org.Members)
            if (CampaignDiplomacy.Get(c, member.Id, id) is { } relation) OpinionSystem.Remember(c, relation, "left_our_organization");
        OrganizationDynamics.UnbindDefence(c, org, member);
        InternationalOrganizations.InvalidateDefencePartners();
        c.Chronicle.Record(c, kind, member.Id, targetId, name);
        if (org.Members.Count == 0) OrganizationDynamics.DissolveNow(c, org);
        c.MarkStrategicChange();
    }

    /// <summary>
    /// The accused's friends - fond of it, or juntas themselves after a coup is punished, and already
    /// half out of the organisation - walk out in protest, and if any do, they and the accused found
    /// a rival bloc of the same kind: the Alliance of Sahel States out of ECOWAS.
    /// </summary>
    private static void Protest(Campaign c, InternationalOrganization org, Nation accused)
    {
        if (org.Dissolved) return;
        var cause = org.SuspendedFor.GetValueOrDefault(accused.Id, "");
        var floor = c.Defines.OrganizationDepartureBelonging + 0.15f;
        var protesters = new List<Nation>();
        foreach (var id in org.Members.ToArray())
        {
            var friend = c.NationById(id);
            if (friend is null || friend.IsHuman || friend.IsSubject) continue;
            var fond = (CampaignDiplomacy.Peek(c, friend.Id, accused.Id)?.Opinion ?? 0f) >= c.Defines.OrganizationProtestOpinion ||
                       CampaignDiplomacy.Peek(c, friend.Id, accused.Id)?.Stance == DiplomaticStance.Allied ||
                       (cause == "coup" && friend.Government == "military_junta");
            if (!fond || OrganizationDynamics.Belonging(c, org, friend) >= floor) continue;
            if (MovementRules.Roll(c.Seed, friend.Id, c.ElapsedDays, 4840 + accused.Id) >= c.Defines.OrganizationProtestChance) continue;
            protesters.Add(friend);
        }
        foreach (var friend in protesters)
        {
            if (!org.Members.Contains(friend.Id)) continue;
            Withdraw(c, org, friend, "organization_protest_exit", accused.Id);
            if (org.Dissolved) break;
        }
        if (protesters.Count == 0 || accused.IsHuman) return;
        var founders = new List<Nation> { accused };
        founders.AddRange(protesters.Where(p => !org.Members.Contains(p.Id)));
        if (founders.Count < 2) return;
        // The accused leaves what is left of its seat behind it.
        if (org.Suspended.ContainsKey(accused.Id)) { org.Suspended.Remove(accused.Id); org.SuspendedFor.Remove(accused.Id); }
        var rival = OrganizationDynamics.FoundBloc(c, org.CharterId, accused, founders);
        if (rival is not null)
            c.Chronicle.Record(c, "organization_rival_bloc", accused.Id, founders[1].Id, InternationalOrganizations.DisplayName(c, rival));
    }
}
