using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    /// <summary>
    /// How much this country belonged to each of its organisations the first time it was weighed
    /// (OrganizationDynamics), by organisation id: the line from which estrangement is counted.
    /// </summary>
    public Dictionary<int, float> OrganizationBelongingAtStart = new();
}

public sealed partial class Defines
{
    /// <summary>How fast where a membership stood on the first day stops protecting it, as a half-life in years.</summary>
    public float OrganizationBaselineHalfLifeYears { get; set; } = 10f;
    /// <summary>How fast an organisation's cohesion follows what its members are to one another, each week.</summary>
    public float OrganizationCohesionPull { get; set; } = 0.08f;
    /// <summary>Below this sense of belonging (0..1) a member starts to think about leaving.</summary>
    public float OrganizationDepartureBelonging { get; set; } = 0.30f;
    /// <summary>The weekly chance a member that has stopped belonging altogether walks out; less, the less estranged it is.</summary>
    public float OrganizationDepartureChance { get; set; } = 0.035f;
    /// <summary>A member never walks out of an organisation younger than this many days.</summary>
    public int OrganizationGraceDays { get; set; } = 540;
    /// <summary>Below this cohesion an organisation can be wound up. The old 0.14 asked a
    /// council to rot almost to nothing before the world was allowed to be rid of it, which
    /// is why nothing ever ended: a body everyone had stopped believing in sat at 0.2
    /// forever. A quarter is where a charter has plainly failed and its members know it.</summary>
    public float OrganizationDissolveCohesion { get; set; } = 0.24f;
    /// <summary>The weekly chance an organisation that low is wound up.</summary>
    public float OrganizationDissolveChance { get; set; } = 0.05f;
    /// <summary>Two estranged members who think this well of each other leave together and found their own.</summary>
    public float OrganizationSchismOpinion { get; set; } = 35f;
    /// <summary>The monthly chance that countries threatened by the same great power sign a pact against it.</summary>
    public float ThreatPactChance { get; set; } = 0.25f;
    /// <summary>No pact against a great power is signed before this day: the world of January 2024 has the blocs it has.</summary>
    public int ThreatPactEarliestDay { get; set; } = 365;
    /// <summary>What a threatened country must think of the pact's founder to sign with it.</summary>
    public float ThreatPactOpinion { get; set; } = 25f;
    public float OrganizationLeaderRiftPenalty { get; set; } = .24f;
}

/// <summary>
/// Organisations that live: they hold together while their members do, and come apart when they do not.
///
/// An organisation's cohesion used to move by the dues - a little up each week paid, a little
/// down each week owed - so the Atlantic alliance and a council founded last spring were held
/// together by the same thing, and nothing ever left anything. Now it follows what the members
/// are to one another: what they think of each other, how far apart their governments stand,
/// whether they are rivals or shooting at each other, and whether they face a danger together.
///
/// Each member has its own sense of belonging - its opinion of the rest, the distance between its
/// politics and theirs, what it owes, whether the bloc is still worth anything, whether it needs
/// the bloc against somebody. A government that has stopped belonging walks out, now or in a few
/// months; one that finds another as estranged as itself, and a friend, leaves with it and founds
/// its own; a member that attacks another is thrown out; and an organisation nobody believes in
/// is wound up and its fund returned.
///
/// And blocs are made as well as unmade: the neighbours of a great power that frightens them all
/// sign a pact against it - the Warsaw–Vilnius pact, named for the capitals that signed first.
/// A player's membership is never decided for the player; a player's country can still be thrown
/// out for attacking a fellow member.
/// </summary>
public static class OrganizationDynamics
{
    public static void Tick(Campaign c)
    {
        if (c is null || !c.FeatureEnabled("organizations") || !MechanicFeatures.SystemEnabled(c, nameof(OrganizationDynamics))) return;
        foreach (var org in InternationalOrganizations.Active(c).OrderBy(o => o.Id).ToArray())
        {
            if (InternationalOrganizations.Charter(org.CharterId) is null) continue;
            // Weekly, each organisation on its own day.
            if ((c.ElapsedDays + org.Id * 3) % 7 != 0) continue;
            Review(c, org);
        }
        if (c.ElapsedDays % 30 == 11) ThreatPacts(c);
    }

    /// <summary>What the members are to one another, 0 to 1: the cohesion an organisation drifts towards.</summary>
    public static float Target(Campaign c, InternationalOrganization org)
    {
        var members = org.Members.Select(c.NationById).Where(n => n is not null).ToList();
        if (members.Count < 2) return 0.3f;
        var pairs = 0;
        var opinion = 0f;
        var distance = 0f;
        var rivals = 0;
        var fighting = 0;
        for (var i = 0; i < members.Count; i++)
            for (var j = i + 1; j < members.Count; j++)
            {
                pairs++;
                var relation = CampaignDiplomacy.Peek(c, members[i].Id, members[j].Id);
                opinion += relation?.Opinion ?? 0f;
                distance += PoliticsCore.IdeologicalDistance(members[i], members[j]);
                if (relation?.Stance == DiplomaticStance.Rival) rivals++;
                if (CampaignDiplomacy.AtWar(c, members[i].Id, members[j].Id)) fighting++;
            }
        var target = 0.55f + opinion / pairs * 0.004f - distance / pairs * 0.25f - rivals / (float)pairs * 0.6f;
        // Members shooting at each other: as bad as the share of the organisation it involves.
        if (fighting > 0) target -= MathF.Min(0.35f, 0.12f + fighting * 4f / members.Count * 0.1f);
        target += Institution(c, org);
        // Trade is the glue of every economic union: Germany sells half of what it sells to the
        // rest of the European Union, and leaving would cost it that before anything else.
        target += members.Average(m => TradeWithMembers(m, members)) * 0.2f;
        // A danger in common: a member at war with an outsider, for a pact that exists to answer it.
        if (InternationalOrganizations.Charter(org.CharterId)?.Defence == true &&
            members.Any(m => c.Wars.Any(w => !w.Ended && !w.CivilConflict && w.IsDefending(m.Id) && w.EnemiesOf(m.Id).All(e => !org.Members.Contains(e)))))
            target += 0.12f;
        // Hegemonic rivalry inside the same council erodes unity when the top two members are rivals or far apart.
        if (InternationalOrganizations.Leader(c, org) is { } leader && InternationalOrganizations.Challenger(c, org) is { } challenger)
        {
            var rel = CampaignDiplomacy.Peek(c, leader.Id, challenger.Id);
            if (rel?.Stance == DiplomaticStance.Rival) target -= 0.08f;
            else if ((rel?.Opinion ?? 0f) < -15f) target -= 0.04f;
        }
        // If the dominant member or patron of the organization collapses into civil conflict, bankruptcy, or isolationism,
        // the entire organization's cohesion target suffers an organic systemic drop.
        if (InternationalOrganizations.Leader(c, org) is { } mainLeader)
        {
            var totalOrgGdp = MathF.Max(1f, members.Sum(m => MathF.Max(0f, m.Gdp)));
            var leaderShare = mainLeader.Gdp / totalOrgGdp;
            if (leaderShare >= 0.25f)
            {
                var weight = MathF.Min(1f, leaderShare / 0.5f);
                if (mainLeader.NationalCourse.Dominant is "isolationism" or "armed_neutrality") target -= 0.14f * weight;
                if (mainLeader.DebtCondition >= DebtCondition.Insolvent) target -= 0.12f * weight;
                if (mainLeader.Stability < 0.35f) target -= 0.10f * weight;
                if (c.Wars.Any(w => !w.Ended && w.CivilConflict && (w.AttackerId == mainLeader.Id || w.DefenderId == mainLeader.Id))) target -= 0.18f * weight;
            }
        }
        return M.Clamp(target, 0.05f, 0.95f);
    }

    /// <summary>How much a member still belongs, 0 to 1.</summary>
    public static float Belonging(Campaign c, InternationalOrganization org, Nation member)
        => Belonging(c,org,member,InternationalOrganizations.Leader(c,org));

    private static float Belonging(Campaign c, InternationalOrganization org, Nation member, Nation leader)
    {
        var others = org.Members.Where(id => id != member.Id).Select(c.NationById).Where(n => n is not null).ToList();
        if (others.Count == 0) return 1f;
        var opinion = others.Average(o => CampaignDiplomacy.Peek(c, member.Id, o.Id)?.Opinion ?? 0f);
        var distance = others.Average(o => PoliticsCore.IdeologicalDistance(member, o));
        var trust = others.Average(o => PoliticalPolicies.ForeignTrust(c, member, o));
        var belonging = 0.55f + opinion * 0.004f - distance * 0.3f + trust + Institution(c, org) * 0.6f +
                        MathF.Min(0.25f, TradeWithMembers(member, others) * 0.4f);
        if (others.Any(o => CampaignDiplomacy.AtWar(c, member.Id, o.Id))) belonging -= 0.4f;
        belonging -= MathF.Min(0.25f, org.UnpaidWeeks.GetValueOrDefault(member.Id) * 0.02f);
        if (org.Cohesion < 0.4f) belonging -= (0.4f - org.Cohesion) * 0.5f;
        if (!string.IsNullOrEmpty(org.DoctrineId))
        {
            belonging += string.Equals(InternationalOrganizations.PreferredDoctrine(c, org, member), org.DoctrineId, StringComparison.OrdinalIgnoreCase)
                ? 0.04f : -0.04f;
        }
        // A pact is worth keeping to a country that has somebody to fear.
        if (InternationalOrganizations.Charter(org.CharterId)?.Defence == true && Threatened(c, member)) belonging += 0.15f;
        if (member.NationalCourse.Dominant is "isolationism" or "armed_neutrality") belonging -= 0.20f;
        if (member.DebtCondition >= DebtCondition.Insolvent) belonging -= 0.12f;
        belonging -= LeadershipFriction(c,org,member,leader);
        return M.Clamp01(belonging);
    }

    /// <summary>A hostile hegemon is not diluted to one-thirtieth of a small member's
    /// interests. Mere leadership is no penalty; rivalry or sustained hostility is.</summary>
    public static float LeadershipFriction(Campaign c, InternationalOrganization org, Nation member, Nation leader)
    {
        if (!c.Defines.OrganicOrganizationReachEnabled || leader is null) return 0;
        if (leader.Id == member.Id)
        {
            var leaderBurden = 0f;
            if (member.NationalCourse.Dominant is "isolationism" or "armed_neutrality") leaderBurden += 0.28f;
            if (member.DebtCondition >= DebtCondition.Insolvent) leaderBurden += 0.18f;
            if (member.Stability < 0.35f) leaderBurden += 0.15f;
            if (WarWeariness.NationalFatigue(c, member) > 0.40f) leaderBurden += 0.12f;
            return Math.Clamp(leaderBurden, 0f, 0.5f);
        }
        var relation=CampaignDiplomacy.Peek(c,member.Id,leader.Id);
        if (relation is null) return 0;
        var hostility = relation.Stance==DiplomaticStance.Rival ? 1f : Math.Clamp((-relation.Opinion-15)/65f,0,1);
        if (leader.NationalCourse.Dominant is "isolationism" or "armed_neutrality") hostility = MathF.Max(hostility, 0.40f);
        if (leader.DebtCondition >= DebtCondition.Insolvent || leader.Stability < 0.30f) hostility = MathF.Max(hostility, 0.35f);
        return hostility * Math.Clamp(c.Defines.OrganizationLeaderRiftPenalty,0,.5f);
    }

    public static bool SharedThreat(Campaign c,Nation a,Nation b)
    {
        bool Afraid(Nation n,int power) => CampaignDiplomacy.Peek(c,n.Id,power) is { } r &&
            (r.Stance is DiplomaticStance.Rival or DiplomaticStance.AtWar || r.Opinion<=-40);
        foreach(var power in AllianceSystem.GreatPowers(c))
            if(power!=a.Id && power!=b.Id && Afraid(a,power) && Afraid(b,power)) return true;
        return false;
    }

    public static bool FoundingRelevant(Campaign c,Nation founder,Nation target) =>
        string.Equals(founder.Region,target.Region,StringComparison.OrdinalIgnoreCase) ||
        string.Equals(founder.Continent,target.Continent,StringComparison.OrdinalIgnoreCase) ||
        (c.Defines.OrganicOrganizationReachEnabled && (SharedThreat(c,founder,target) ||
        ((founder.Gdp>=Math.Max(0,c.Defines.AiOrganizationGlobalGdp) || AllianceSystem.GreatPowers(c).Contains(founder.Id)) &&
         (founder.NationalCourse.Dominant=="global_integration" || founder.TradeDependenceOn(target.Id)>=.02f ||
          CampaignDiplomacy.Peek(c,founder.Id,target.Id)?.Stance==DiplomaticStance.Allied))));

    public static bool ExpansionRelevant(Campaign c,InternationalOrganization org,Nation proposer,Nation target) =>
        FoundingRelevant(c,proposer,target) || c.Defines.OrganicOrganizationReachEnabled && org.Members.Any(id=>c.NationById(id) is { } member &&
            (string.Equals(member.Region,target.Region,StringComparison.OrdinalIgnoreCase) ||
             string.Equals(member.Continent,target.Continent,StringComparison.OrdinalIgnoreCase) ||
             SharedThreat(c,member,target) || member.TradeDependenceOn(target.Id)>=.02f));

    /// <summary>The share of a member's trade that is with the others, 0 to 1.</summary>
    private static float TradeWithMembers(Nation member, IEnumerable<Nation> others)
    {
        var share = 0f;
        foreach (var other in others)
            if (other.Id != member.Id) share += member.TradeDependenceOn(other.Id);
        return M.Clamp01(share);
    }

    /// <summary>
    /// What decades of habit are worth: an organisation the world began with, or one five years
    /// old, has offices, treaties and a way of doing things that a council founded last spring
    /// does not.
    /// </summary>
    private static float Institution(Campaign c, InternationalOrganization org) =>
        org.FoundedDay <= 0 || c.ElapsedDays - org.FoundedDay >= 5 * 365 ? 0.1f : 0f;

    private static bool Threatened(Campaign c, Nation nation)
    {
        foreach (var powerId in AllianceSystem.GreatPowers(c))
        {
            if (powerId == nation.Id) continue;
            var relation = CampaignDiplomacy.Peek(c, nation.Id, powerId);
            if (relation is null || relation.Stance == DiplomaticStance.Allied) continue;
            if (relation.Stance is DiplomaticStance.Rival or DiplomaticStance.AtWar || relation.Opinion <= -40f) return true;
        }
        return false;
    }

    private static void Review(Campaign c, InternationalOrganization org)
    {
        var defines = c.Defines;
        var leader = InternationalOrganizations.Leader(c,org);
        org.Cohesion = M.Clamp01(org.Cohesion + (Target(c, org) - org.Cohesion) * M.Clamp01(defines.OrganizationCohesionPull));

        // Whoever attacks a fellow member is out.
        foreach (var war in c.Wars)
        {
            if (war.Ended || war.CivilConflict) continue;
            if (!org.Members.Contains(war.AttackerId) || !war.Side(false).Any(org.Members.Contains)) continue;
            Expel(c, org, c.NationById(war.AttackerId), war.DefenderId);
            if (org.Dissolved) return;
        }

        // A council nobody believes in is wound up; one with a single member left, a year on, too.
        var lonely = org.Members.Count < 2 && org.Invitations.Count == 0 && org.Motion is null &&
                     c.ElapsedDays - org.FoundedDay > 365 && org.Members.All(id => c.NationById(id) is not { IsHuman: true });
        if (lonely || (org.Cohesion < defines.OrganizationDissolveCohesion && org.Members.All(id => c.NationById(id) is not { IsHuman: true }) &&
                       MovementRules.Roll(c.Seed, 7000 + org.Id, c.ElapsedDays, 4603) < defines.OrganizationDissolveChance))
        {
            Dissolve(c, org);
            return;
        }

        // Where each member stood the first time it was weighed, from the first week on.
        foreach (var id in org.Members)
            if (c.NationById(id) is { } weighed && !weighed.OrganizationBelongingAtStart.ContainsKey(org.Id))
                weighed.OrganizationBelongingAtStart[org.Id] = Belonging(c, org, weighed,leader);
        if (c.ElapsedDays - org.FoundedDay < defines.OrganizationGraceDays) return;
        foreach (var id in org.Members.ToArray())
        {
            var member = c.NationById(id);
            if (member is null || member.IsHuman || !org.Members.Contains(id)) continue;
            var belonging = Belonging(c, org, member,leader);
            var floor = defines.OrganizationDepartureBelonging;
            // A member that was already half out on the first day - most of the African Union's
            // quarrels are older than 2024 - is not estranged by them: what counts is how far it
            // has drifted since, and the first day's protection fades over a decade.
            if (!member.OrganizationBelongingAtStart.TryGetValue(org.Id, out var start))
                member.OrganizationBelongingAtStart[org.Id] = start = belonging;
            var years = (c.ElapsedDays - Math.Max(0, org.FoundedDay)) / 365f;
            belonging += MathF.Max(0f, floor + 0.08f - start) *
                         MathF.Pow(0.5f, years / MathF.Max(0.5f, defines.OrganizationBaselineHalfLifeYears));
            if (belonging >= floor) continue;
            // A government only just estranged rarely goes; one that no longer belongs at all soon does.
            var estrangement = M.Clamp01((floor - belonging) / MathF.Max(0.01f, floor) * 2.5f);
            var chance = defines.OrganizationDepartureChance * estrangement * estrangement;
            if (MovementRules.Roll(c.Seed, member.Id, c.ElapsedDays, 4600 + org.Id) >= chance) continue;
            Leave(c, org, member);
            if (org.Dissolved) return;
        }
    }

    private static void Leave(Campaign c, InternationalOrganization org, Nation member)
    {
        var name = InternationalOrganizations.DisplayName(c, org);
        // A friend as estranged as itself leaves with it, and they found their own.
        var companion = org.Members.Where(id => id != member.Id).Select(c.NationById)
            .Where(n => n is { IsHuman: false, IsSubject: false } &&
                        Belonging(c, org, n) < c.Defines.OrganizationDepartureBelonging &&
                        (CampaignDiplomacy.Peek(c, member.Id, n.Id)?.Opinion ?? 0f) >= c.Defines.OrganizationSchismOpinion)
            .OrderByDescending(n => CampaignDiplomacy.Peek(c, member.Id, n.Id)?.Opinion ?? 0f).ThenBy(n => n.Id).FirstOrDefault();
        var leaver = new OrganizationCommand { IssuerId = member.Id, OrganizationId = org.Id, Action = "leave" };
        leaver.Apply(c);
        if (!leaver.Changed) return;
        Remember(c, org, member, "left_our_organization");
        if (companion is not null && org.Members.Contains(companion.Id))
        {
            var with = new OrganizationCommand { IssuerId = companion.Id, OrganizationId = org.Id, Action = "leave" };
            with.Apply(c);
            if (with.Changed)
            {
                Remember(c, org, companion, "left_our_organization");
                var founded = Found(c, org.CharterId, member, new[] { member, companion });
                c.Chronicle.Record(c, "organization_split", member.Id, companion.Id, name);
                if (founded is not null) return;
            }
        }
        c.Chronicle.Record(c, "organization_left", member.Id, -1, name);
        // Hegemon / dominant pillar departure shock across ALL organizations:
        // if a founder or an economic/military pillar (>= 35% of org GDP) leaves, cohesion collapses.
        var totalGdp = org.Members.Select(c.NationById).Where(n => n != null).Sum(n => n.Gdp);
        var isPillar = member.Id == org.FounderId || (totalGdp > 0 && member.Gdp >= totalGdp * 0.35f);
        if (isPillar)
        {
            org.Cohesion = MathF.Max(0.05f, org.Cohesion - 0.35f);
            var remaining = org.Members.Select(c.NationById).Where(n => n is not null && !n.IsSubject && InternationalOrganizations.Eligible(c, n)).ToList();
            if (remaining.Count >= 3 && org.Cohesion < c.Defines.OrganizationDissolveCohesion)
            {
                var newLeader = remaining.OrderByDescending(n => n.Gdp).First();
                var companions = remaining.Where(n => n.Id != newLeader.Id && (CampaignDiplomacy.Peek(c, newLeader.Id, n.Id)?.Opinion ?? 0f) >= 20f).ToList();
                if (companions.Count >= 2)
                {
                    companions.Insert(0, newLeader);
                    var newBloc = Found(c, org.CharterId, newLeader, companions);
                    if (newBloc is not null)
                    {
                        c.Chronicle.Record(c, "organization_reorganized", newLeader.Id, -1,
                            InternationalOrganizations.DisplayName(c, newBloc));
                    }
                }
            }
        }
        if (InternationalOrganizations.Charter(org.CharterId)?.Defence == true)
        {
            foreach (var otherId in org.Members)
            {
                if (otherId == member.Id) continue;
                var rel = CampaignDiplomacy.Peek(c, member.Id, otherId);
                if (rel is { Stance: DiplomaticStance.Allied })
                {
                    var shareOther = InternationalOrganizations.Active(c).Any(o => o.Id != org.Id && !o.Dissolved &&
                        InternationalOrganizations.Charter(o.CharterId)?.Defence == true &&
                        o.Members.Contains(member.Id) && o.Members.Contains(otherId));
                    if (!shareOther) rel.Stance = DiplomaticStance.Neutral;
                }
            }
        }
    }

    private static void Expel(Campaign c, InternationalOrganization org, Nation aggressor, int victimId)
    {
        if (aggressor is null || !org.Members.Contains(aggressor.Id)) return;
        var name = InternationalOrganizations.DisplayName(c, org);
        org.Members.Remove(aggressor.Id);
        InternationalOrganizations.InvalidateDefencePartners();
        org.UnpaidWeeks.Remove(aggressor.Id);
        org.Motion?.Votes.Remove(aggressor.Id);
        org.Cohesion = MathF.Max(0f, org.Cohesion - 0.08f);
        Remember(c, org, aggressor, "attacked_a_fellow_member");
        c.Chronicle.Record(c, "organization_expelled", aggressor.Id, victimId, name);
        if (org.Members.Count == 0) Dissolve(c, org);
        c.MarkStrategicChange();
    }

    private static void Dissolve(Campaign c, InternationalOrganization org)
    {
        var name = InternationalOrganizations.DisplayName(c, org);
        var members = org.Members.Select(c.NationById).Where(n => n is not null).ToList();
        // The common fund goes back to the members, by the size of their economies.
        var gdp = members.Sum(n => MathF.Max(0f, n.Gdp));
        if (org.Budget > 0f && members.Count > 0)
            foreach (var member in members)
                member.Treasury += org.Budget * (gdp > 0f ? MathF.Max(0f, member.Gdp) / gdp : 1f / members.Count);
        org.Budget = 0f;
        org.Motion = null;
        org.Invitations.Clear();
        org.Dissolved = true;
        InternationalOrganizations.InvalidateDefencePartners();
        var actor = members.OrderByDescending(n => n.Gdp).ThenBy(n => n.Id).FirstOrDefault()?.Id ?? org.FounderId;
        c.Chronicle.Record(c, "organization_dissolved", actor, -1, name);
        if (InternationalOrganizations.Charter(org.CharterId)?.Defence == true)
        {
            for (var i = 0; i < members.Count; i++)
            for (var j = i + 1; j < members.Count; j++)
            {
                var rel = CampaignDiplomacy.Peek(c, members[i].Id, members[j].Id);
                if (rel is { Stance: DiplomaticStance.Allied })
                {
                    var shareOther = InternationalOrganizations.Active(c).Any(o => o.Id != org.Id && !o.Dissolved &&
                        InternationalOrganizations.Charter(o.CharterId)?.Defence == true &&
                        o.Members.Contains(members[i].Id) && o.Members.Contains(members[j].Id));
                    if (!shareOther) rel.Stance = DiplomaticStance.Neutral;
                }
            }
        }
        c.MarkStrategicChange();
    }

    private static void Remember(Campaign c, InternationalOrganization org, Nation subject, string reason)
    {
        foreach (var id in org.Members)
            if (id != subject.Id && CampaignDiplomacy.Get(c, id, subject.Id) is { } relation)
                OpinionSystem.Remember(c, relation, reason);
    }

    /// <summary>
    /// A new organisation of the founders, named for the first two capitals that signed - the
    /// Warsaw–Vilnius pact - or an authentic global/regional treaty name, and seeded with them all at once.
    /// </summary>
    private static InternationalOrganization Found(Campaign c, string charterId, Nation founder, IReadOnlyList<Nation> members)
    {
        if (members.Count < 2 || InternationalOrganizations.Active(c).Count() >= Math.Clamp(c.Defines.AiOrganizationWorldLimit + 8, 0, 120)) return null;
        var limit = Math.Clamp(c.Defines.OrganizationMembershipLimit, 1, 30);
        if (members.Any(m => InternationalOrganizations.Memberships(c, m.Id).Count() >= limit)) return null;
        var second = members.Where(m => m.Id != founder.Id).OrderByDescending(m => m.Gdp).ThenBy(m => m.Id).First();
        var name = DetermineOrganizationName(c, charterId, founder, second, members);
        if (InternationalOrganizations.Active(c).Any(o => o.Name == name)) return null;
        var org = InternationalOrganizations.Seed(c, name, charterId, members.Select(m => m.Id));
        if (org is null || org.Name != name) return null;
        org.FounderId = founder.Id;
        org.Cohesion = 0.6f;
        if (charterId == "collective_defence")
        {
            for (var i = 0; i < members.Count; i++)
            for (var j = i + 1; j < members.Count; j++)
            {
                var rel = CampaignDiplomacy.Get(c, members[i].Id, members[j].Id);
                if (rel.Stance != DiplomaticStance.Allied) rel.Stance = DiplomaticStance.Allied;
            }
        }
        c.Chronicle.Record(c, "organization_founded", founder.Id, second.Id, InternationalOrganizations.DisplayName(c, org));
        c.MarkStrategicChange();
        return org;
    }

    public static string DetermineOrganizationName(Campaign c, string charterId, Nation founder, Nation second, IReadOnlyList<Nation> members)
    {
        var kind = charterId switch { "collective_defence" => "pact", "development" => "community", "commodity_cartel" => "cartel", _ => "forum" };
        var isGlobalOrCrossContinent = !string.Equals(founder.Continent, second.Continent, StringComparison.OrdinalIgnoreCase) ||
                                       founder.Gdp >= c.Defines.AiOrganizationGlobalGdp ||
                                       members.Select(m => m.Continent).Distinct().Count() > 1;

        if (charterId == "collective_defence")
        {
            if (isGlobalOrCrossContinent)
            {
                if ((founder.Continent is "north_america" && second.Continent is "europe") ||
                    (founder.Continent is "europe" && second.Continent is "north_america"))
                {
                    if (!InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.transatlantic_treaty"))
                        return "org.name.transatlantic_treaty";
                }
                if (!InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.global_security_coalition"))
                    return "org.name.global_security_coalition";
            }
            if (string.Equals(founder.Continent, "europe", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(second.Continent, "europe", StringComparison.OrdinalIgnoreCase) &&
                !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.european_defence_alliance"))
            {
                return "org.name.european_defence_alliance";
            }
            if ((string.Equals(founder.Continent, "asia", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(founder.Region, "east_asia", StringComparison.OrdinalIgnoreCase)) &&
                !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.indopacific_security_dialogue"))
            {
                return "org.name.indopacific_security_dialogue";
            }
            if ((founder.Continent is "asia" or "europe") && (founder.Region is "eastern_europe" or "central_asia" or "russia" || second.Region is "eastern_europe" or "central_asia") &&
                !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.eurasian_security_treaty"))
            {
                return "org.name.eurasian_security_treaty";
            }
            if (founder.Continent is "africa" && second.Continent is "africa" &&
                !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.african_defence_community"))
            {
                return "org.name.african_defence_community";
            }
            if (founder.Continent is "south_america" or "north_america" && second.Continent is "south_america" or "north_america" &&
                !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.panamerican_defence_pact"))
            {
                return "org.name.panamerican_defence_pact";
            }
            if (founder.Region is "middle_east" or "north_africa" && second.Region is "middle_east" or "north_africa" &&
                !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.middle_eastern_security_pact"))
            {
                return "org.name.middle_eastern_security_pact";
            }
        }
        else if (charterId == "development")
        {
            if (isGlobalOrCrossContinent)
            {
                if ((founder.Region is "east_asia" or "oceania" || second.Region is "east_asia" or "oceania") &&
                    !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.pacific_trade_partnership"))
                    return "org.name.pacific_trade_partnership";
                if (!InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.global_trade_partnership"))
                    return "org.name.global_trade_partnership";
            }
            if (founder.Continent is "africa" && second.Continent is "africa" &&
                !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.african_economic_community"))
            {
                return "org.name.african_economic_community";
            }
            if (founder.Continent is "south_america" or "north_america" && second.Continent is "south_america" or "north_america" &&
                !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.panamerican_economic_community"))
            {
                return "org.name.panamerican_economic_community";
            }
            if ((founder.Region is "eastern_europe" or "central_asia" || second.Region is "eastern_europe" or "central_asia") &&
                !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.eurasian_economic_council"))
            {
                return "org.name.eurasian_economic_council";
            }
        }
        else if (charterId == "commodity_cartel")
        {
            if (isGlobalOrCrossContinent)
            {
                if (!InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.petroleum_exporting_cartel"))
                    return "org.name.petroleum_exporting_cartel";
                if (!InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.critical_minerals_cartel"))
                    return "org.name.critical_minerals_cartel";
            }
        }
        else // "cooperation"
        {
            if (isGlobalOrCrossContinent)
            {
                if (founder.Continent is "africa" or "asia" or "south_america" &&
                    second.Continent is "africa" or "asia" or "south_america" &&
                    !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.global_south_dialogue"))
                {
                    return "org.name.global_south_dialogue";
                }
                if (founder.NationalCourse.Dominant is "armed_neutrality" or "isolationism" &&
                    !InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.non_aligned_coalition"))
                {
                    return "org.name.non_aligned_coalition";
                }
                if (!InternationalOrganizations.Active(c).Any(o => o.Name == "org.name.world_cooperation_council"))
                {
                    return "org.name.world_cooperation_council";
                }
            }
        }

        return $"org.name.{kind}|{CapitalOf(founder)}|{CapitalOf(second)}";
    }

    public static string CapitalOf(Nation nation) => string.IsNullOrWhiteSpace(nation.CapitalName) ? nation.Tag : nation.CapitalName.Replace("|", " ");

    /// <summary>
    /// The neighbours of a great power that frightens them all sign a pact against it: once a
    /// month at most, somewhere in the world, if the threatened can stand one another and are
    /// not already in a pact together.
    /// </summary>
    private static void ThreatPacts(Campaign c)
    {
        if (!c.Defines.AiMayFoundOrganizations || InternationalOrganizations.Charter("collective_defence") is null ||
            c.ElapsedDays < c.Defines.ThreatPactEarliestDay) return;
        var limit = Math.Clamp(c.Defines.OrganizationMembershipLimit, 1, 30);
        foreach (var powerId in AllianceSystem.GreatPowers(c).OrderBy(id => id))
        {
            var power = c.NationById(powerId);
            if (power is null) continue;
            var threatened = c.Nations.Where(n => n.Id != powerId && !n.IsHuman && !n.IsSubject && InternationalOrganizations.Eligible(c, n) &&
                    !n.AnswersTo(powerId) && InternationalOrganizations.Memberships(c, n.Id).Count() < limit &&
                    (CampaignDiplomacy.SharesLandBorder(c, n.MapIndex, power.MapIndex) ||
                     (string.Equals(n.Region, power.Region, StringComparison.OrdinalIgnoreCase) &&
                      NavalOperations.Distance(n.CapitalLatitude, n.CapitalLongitude, power.CapitalLatitude, power.CapitalLongitude) < 3500f) ||
                     (power.Gdp >= c.Defines.AiOrganizationGlobalGdp && (string.Equals(n.Continent, power.Continent, StringComparison.OrdinalIgnoreCase) || n.Gdp >= 300f))) &&
                    CampaignDiplomacy.Peek(c, n.Id, powerId) is { } relation && relation.Stance != DiplomaticStance.Allied &&
                    (relation.Stance is DiplomaticStance.Rival or DiplomaticStance.AtWar || relation.Opinion <= -40f))
                .OrderByDescending(n => n.Gdp).ThenBy(n => n.Id).ToList();
            if (threatened.Count < 2) continue;
            if (MovementRules.Roll(c.Seed, powerId, c.ElapsedDays, 4611) >= c.Defines.ThreatPactChance) continue;
            var founder = threatened[0];
            var defended = InternationalOrganizations.DefencePartners(c, founder.Id).ToHashSet();
            var members = new List<Nation> { founder };
            foreach (var other in threatened.Skip(1))
            {
                if (defended.Contains(other.Id) || CampaignDiplomacy.AtWar(c, founder.Id, other.Id)) continue;
                if ((CampaignDiplomacy.Peek(c, founder.Id, other.Id)?.Opinion ?? 0f) < c.Defines.ThreatPactOpinion) continue;
                if (members.Any(m => CampaignDiplomacy.AtWar(c, m.Id, other.Id))) continue;
                members.Add(other);
                if (members.Count >= 5) break;
            }
            if (members.Count < 2) continue;
            if (Found(c, "collective_defence", founder, members) is not null) return;
        }
    }
}
