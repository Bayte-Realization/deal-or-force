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
        foreach (var _ in TickStages(c)) { }
    }

    [ThreadStatic] private static List<InternationalOrganization> _activeOrgsScratch;
    [ThreadStatic] private static List<Nation> _membersScratch;
    [ThreadStatic] private static List<Nation> _othersScratch;

    /// <summary>The same reviews, yielded between organisations so one frame does not carry them all.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || !c.FeatureEnabled("organizations") || !MechanicFeatures.SystemEnabled(c, nameof(OrganizationDynamics))) yield break;
        var orgs = _activeOrgsScratch ??= new List<InternationalOrganization>();
        orgs.Clear();
        var allOrgs = c.Organizations;
        for (var i = 0; i < allOrgs.Count; i++)
        {
            var org = allOrgs[i];
            if (!org.Dissolved) orgs.Add(org);
        }
        if (orgs.Count > 1) orgs.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        for (var i = 0; i < orgs.Count; i++)
        {
            var org = orgs[i];
            if (InternationalOrganizations.Charter(org.CharterId) is null) continue;
            // Weekly, each organisation on its own day.
            if ((c.ElapsedDays + org.Id * 3) % 7 != 0) continue;
            using (SimProfiler.Measure("organization life")) Review(c, org);
            if (Campaign.StageOverdue()) yield return 0;
        }
        orgs.Clear();
        if (c.ElapsedDays % 30 == 11)
        {
            using (SimProfiler.Measure("organization life")) ThreatPacts(c);
            if (Campaign.StageOverdue()) yield return 0;
        }
    }

    /// <summary>What the members are to one another, 0 to 1: the cohesion an organisation drifts towards.</summary>
    public static float Target(Campaign c, InternationalOrganization org)
    {
        var members = _membersScratch ??= new List<Nation>();
        members.Clear();
        for (var i = 0; i < org.Members.Count; i++)
        {
            var n = c.NationById(org.Members[i]);
            if (n is not null) members.Add(n);
        }
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
        double tradeSum = 0d;
        for (var i = 0; i < members.Count; i++) tradeSum += TradeWithMembers(members[i], members);
        target += (float)(tradeSum / members.Count) * 0.2f;
        // A danger in common: a member at war with an outsider, for a pact that exists to answer it.
        var liveWars = Warfare.LiveWars(c);
        if (InternationalOrganizations.Charter(org.CharterId)?.Defence == true && liveWars.Count > 0)
        {
            var attackedByOutsider = false;
            for (var mi = 0; mi < members.Count && !attackedByOutsider; mi++)
            {
                var mId = members[mi].Id;
                for (var wi = 0; wi < liveWars.Count; wi++)
                {
                    var w = liveWars[wi];
                    if (w.Ended || w.CivilConflict || !w.IsDefending(mId)) continue;
                    var allOutside = true;
                    foreach (var e in w.EnemiesOf(mId))
                        if (org.Members.Contains(e)) { allOutside = false; break; }
                    if (allOutside) { attackedByOutsider = true; break; }
                }
            }
            if (attackedByOutsider) target += 0.12f;
        }
        // Hegemonic rivalry inside the same council erodes unity when the top two members are rivals or far apart.
        var mainLeader = InternationalOrganizations.Leader(c, org);
        if (mainLeader is not null && InternationalOrganizations.Challenger(c, org) is { } challenger)
        {
            var rel = CampaignDiplomacy.Peek(c, mainLeader.Id, challenger.Id);
            if (rel?.Stance == DiplomaticStance.Rival) target -= 0.08f;
            else if ((rel?.Opinion ?? 0f) < -15f) target -= 0.04f;
        }
        // If the dominant member or patron of the organization collapses into civil conflict, bankruptcy, or isolationism,
        // the entire organization's cohesion target suffers an organic systemic drop.
        if (mainLeader is not null)
        {
            var totalGdpSum = 0f;
            for (var i = 0; i < members.Count; i++) totalGdpSum += MathF.Max(0f, members[i].Gdp);
            var totalOrgGdp = MathF.Max(1f, totalGdpSum);
            var leaderShare = mainLeader.Gdp / totalOrgGdp;
            if (leaderShare >= 0.25f)
            {
                var weight = MathF.Min(1f, leaderShare / 0.5f);
                if (mainLeader.NationalCourse.Dominant is "isolationism" or "armed_neutrality") target -= 0.14f * weight;
                if (mainLeader.DebtCondition >= DebtCondition.Insolvent) target -= 0.12f * weight;
                if (mainLeader.Stability < 0.35f) target -= 0.10f * weight;
                for (var wi = 0; wi < liveWars.Count; wi++)
                {
                    var w = liveWars[wi];
                    if (!w.Ended && w.CivilConflict && (w.AttackerId == mainLeader.Id || w.DefenderId == mainLeader.Id))
                    { target -= 0.18f * weight; break; }
                }
            }
        }
        return M.Clamp(target, 0.05f, 0.95f);
    }

    /// <summary>How much a member still belongs, 0 to 1.</summary>
    public static float Belonging(Campaign c, InternationalOrganization org, Nation member)
        => Belonging(c,org,member,InternationalOrganizations.Leader(c,org));

    private static float Belonging(Campaign c, InternationalOrganization org, Nation member, Nation leader)
    {
        var others = _othersScratch ??= new List<Nation>();
        others.Clear();
        for (var i = 0; i < org.Members.Count; i++)
        {
            var id = org.Members[i];
            if (id == member.Id) continue;
            var n = c.NationById(id);
            if (n is not null) others.Add(n);
        }
        if (others.Count == 0) return 1f;
        double opinionSum = 0d, distanceSum = 0d, trustSum = 0d;
        var atWarWithOther = false;
        for (var i = 0; i < others.Count; i++)
        {
            var o = others[i];
            opinionSum += CampaignDiplomacy.Peek(c, member.Id, o.Id)?.Opinion ?? 0f;
            distanceSum += PoliticsCore.IdeologicalDistance(member, o);
            trustSum += PoliticalPolicies.ForeignTrust(c, member, o);
            if (!atWarWithOther && CampaignDiplomacy.AtWar(c, member.Id, o.Id)) atWarWithOther = true;
        }
        var opinion = (float)(opinionSum / others.Count);
        var distance = (float)(distanceSum / others.Count);
        var trust = (float)(trustSum / others.Count);
        var belonging = 0.55f + opinion * 0.004f - distance * 0.3f + trust + Institution(c, org) * 0.6f +
                        MathF.Min(0.25f, TradeWithMembers(member, others) * 0.4f);
        if (atWarWithOther) belonging -= 0.4f;
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
        // What a concession bought back, fading over the years (OrganizationCrises).
        belonging += OrganizationCrises.ConcessionBelonging(c, org, member);
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

    public static bool ExpansionRelevant(Campaign c, InternationalOrganization org, Nation proposer, Nation target)
    {
        if (FoundingRelevant(c, proposer, target)) return true;
        if (!c.Defines.OrganicOrganizationReachEnabled) return false;
        for (var i = 0; i < org.Members.Count; i++)
        {
            if (c.NationById(org.Members[i]) is not { } member) continue;
            if (string.Equals(member.Region, target.Region, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(member.Continent, target.Continent, StringComparison.OrdinalIgnoreCase) ||
                SharedThreat(c, member, target) || member.TradeDependenceOn(target.Id) >= .02f)
                return true;
        }
        return false;
    }

    /// <summary>The share of a member's trade that is with the others, 0 to 1.</summary>
    private static float TradeWithMembers(Nation member, List<Nation> others)
    {
        var share = 0f;
        for (var i = 0; i < others.Count; i++)
        {
            var other = others[i];
            if (other.Id != member.Id) share += member.TradeDependenceOn(other.Id);
        }
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

    [ThreadStatic] private static List<int> _reviewMembersCopyScratch;

    private static void Review(Campaign c, InternationalOrganization org)
    {
        var defines = c.Defines;
        var leader = InternationalOrganizations.Leader(c,org);
        var wasCohesion = org.Cohesion;
        org.Cohesion = M.Clamp01(org.Cohesion + (Target(c, org) - org.Cohesion) * M.Clamp01(defines.OrganizationCohesionPull));
        // An organisation sliding into open crisis is news: members at war with each other, at
        // odds over their politics, or fallen out with their leader. Read from the crossing alone,
        // so nothing has to be remembered to say it once.
        if (wasCohesion >= defines.OrganizationRiftCohesion && org.Cohesion < defines.OrganizationRiftCohesion && org.Members.Count >= 3 &&
            (leader ?? InternationalOrganizations.Leader(c, org)) is { } speaker)
            c.Chronicle.Record(c, "organization_rift", speaker.Id, -1, InternationalOrganizations.DisplayName(c, org));

        if (OrganizationDiscipline.Enabled(c))
        {
            // Suspension, readmission and expulsion of single members (OrganizationDiscipline).
            if (OrganizationDiscipline.Week(c, org)) return;
            leader = InternationalOrganizations.Leader(c, org);
        }
        else
        {
            // Whoever attacks a fellow member is out.
            var liveWars = Warfare.LiveWars(c);
            for (var wi = 0; wi < liveWars.Count; wi++)
            {
                var war = liveWars[wi];
                if (war.Ended || war.CivilConflict) continue;
                if (!org.Members.Contains(war.AttackerId)) continue;
                var hitMember = false;
                foreach (var e in war.Side(false))
                    if (org.Members.Contains(e)) { hitMember = true; break; }
                if (!hitMember) continue;
                ExpelForAggression(c, org, c.NationById(war.AttackerId), war.DefenderId);
                if (org.Dissolved) return;
            }
        }

        var noHuman = true;
        for (var mi = 0; mi < org.Members.Count; mi++)
            if (c.NationById(org.Members[mi]) is { IsHuman: true }) { noHuman = false; break; }

        // A council nobody believes in is wound up; one with a single member left, a year on, too.
        var lonely = org.Members.Count < 2 && org.Invitations.Count == 0 && org.Motion is null &&
                     c.ElapsedDays - org.FoundedDay > 365 && noHuman;
        if (lonely || (org.Cohesion < defines.OrganizationDissolveCohesion && noHuman &&
                       MovementRules.Roll(c.Seed, 7000 + org.Id, c.ElapsedDays, 4603) < defines.OrganizationDissolveChance * Durability(c, org)))
        {
            Dissolve(c, org);
            return;
        }

        // Where each member stood the first time it was weighed, from the first week on.
        for (var mi = 0; mi < org.Members.Count; mi++)
            if (c.NationById(org.Members[mi]) is { } weighed && !weighed.OrganizationBelongingAtStart.ContainsKey(org.Id))
                weighed.OrganizationBelongingAtStart[org.Id] = Belonging(c, org, weighed, leader);
        if (c.ElapsedDays - org.FoundedDay < defines.OrganizationGraceDays) return;
        // A member bargaining for better terms: bought back, still bargaining, or gone by its vote.
        if (OrganizationCrises.Week(c, org, m => Belonging(c, org, m, leader), m => Leave(c, org, m))) return;
        var memCopy = _reviewMembersCopyScratch ??= new List<int>();
        memCopy.Clear();
        for (var mi = 0; mi < org.Members.Count; mi++) memCopy.Add(org.Members[mi]);
        for (var mi = 0; mi < memCopy.Count; mi++)
        {
            var id = memCopy[mi];
            var member = c.NationById(id);
            if (member is null || member.IsHuman || !org.Members.Contains(id)) continue;
            var belonging = Belonging(c, org, member, leader);
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
            // It bargains first, when crises are on; it walks out when one is already under way.
            if (OrganizationCrises.Open(c, org, member)) continue;
            Leave(c, org, member);
            if (org.Dissolved) return;
        }
    }

    /// <summary>
    /// How readily a failing organisation is wound up rather than left to wither, as a factor on
    /// the weekly chance. A council of two founded last spring folds the year it stops working; an
    /// institution with offices, treaties and a dozen members does not - the CIS and the Arab
    /// League have spent decades below anybody's idea of cohesion without being dissolved, and
    /// what an old institution does instead is lose members. Wars, splits and walk-outs still
    /// bring it down: every member gone takes some of the weight with it.
    /// </summary>
    public static float Durability(Campaign c, InternationalOrganization org)
    {
        if (!OrganizationDiscipline.Enabled(c)) return 1f;
        // A body nobody believes in at all - every member at odds with every other - goes
        // regardless of its age: half the dissolution line is a charter that has plainly died.
        if (org.Cohesion < c.Defines.OrganizationDissolveCohesion * 0.5f) return 1f;
        var old = org.FoundedDay <= 0 || c.ElapsedDays - org.FoundedDay >= 5 * 365;
        var factor = old ? 0.12f : 1f;
        if (org.Members.Count >= 6) factor *= 0.5f;
        return factor;
    }

    /// <summary>Whether a member has stopped belonging: below the line it would leave by.</summary>
    public static bool Estranged(Campaign c, InternationalOrganization org, Nation member) =>
        member is not null && Belonging(c, org, member) < c.Defines.OrganizationDepartureBelonging;

    [ThreadStatic] private static List<Nation> _leaveRemainingScratch;
    [ThreadStatic] private static List<Nation> _leaveCompanionsScratch;
    [ThreadStatic] private static List<Nation> _dissolveMembersScratch;
    [ThreadStatic] private static List<Nation> _schismPairScratch;

    private static bool SharesOtherDefencePact(Campaign c, int excludedOrgId, int aId, int bId)
    {
        var orgs = c.Organizations;
        for (var i = 0; i < orgs.Count; i++)
        {
            var o = orgs[i];
            if (o.Id != excludedOrgId && !o.Dissolved &&
                InternationalOrganizations.Charter(o.CharterId)?.Defence == true &&
                o.Members.Contains(aId) && o.Members.Contains(bId))
                return true;
        }
        return false;
    }

    private static void Leave(Campaign c, InternationalOrganization org, Nation member)
    {
        var name = InternationalOrganizations.DisplayName(c, org);
        // A member with nothing left to govern from - its land overrun, a government in exile -
        // cannot table its own departure, but it has voted to go and goes all the same. Before
        // this, Yemen and Libya voted to leave the Arab League and OPEC every few months for a
        // decade and stayed, because the leave command refused a country without land.
        if (InternationalOrganizations.Refusal(c, new OrganizationCommand { IssuerId = member.Id, OrganizationId = org.Id, Action = "leave" }).Length > 0)
        {
            if (org.Members.Contains(member.Id)) OrganizationDiscipline.Withdraw(c, org, member, "organization_left");
            return;
        }
        // A friend as estranged as itself leaves with it, and they found their own.
        Nation companion = null;
        var bestOpinion = float.NegativeInfinity;
        var minBelonging = c.Defines.OrganizationDepartureBelonging;
        var schismOpinion = c.Defines.OrganizationSchismOpinion;
        for (var i = 0; i < org.Members.Count; i++)
        {
            var id = org.Members[i];
            if (id == member.Id) continue;
            var n = c.NationById(id);
            if (n is not { IsHuman: false, IsSubject: false }) continue;
            var op = CampaignDiplomacy.Peek(c, member.Id, n.Id)?.Opinion ?? 0f;
            if (op < schismOpinion) continue;
            if (Belonging(c, org, n) >= minBelonging) continue;
            if (companion is null || op > bestOpinion || (op == bestOpinion && n.Id < companion.Id))
            {
                companion = n;
                bestOpinion = op;
            }
        }
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
                var pair = _schismPairScratch ??= new List<Nation>(2);
                pair.Clear();
                pair.Add(member);
                pair.Add(companion);
                var founded = Found(c, org.CharterId, member, pair);
                pair.Clear();
                c.Chronicle.Record(c, "organization_split", member.Id, companion.Id, name);
                if (founded is not null) return;
            }
        }
        c.Chronicle.Record(c, "organization_left", member.Id, -1, name);
        // Hegemon / dominant pillar departure shock across ALL organizations:
        // if a founder or an economic/military pillar (>= 35% of org GDP) leaves, cohesion collapses.
        var totalGdp = 0f;
        var remaining = _leaveRemainingScratch ??= new List<Nation>();
        remaining.Clear();
        Nation newLeader = null;
        for (var i = 0; i < org.Members.Count; i++)
        {
            var n = c.NationById(org.Members[i]);
            if (n is null) continue;
            totalGdp += n.Gdp;
            if (!n.IsSubject && InternationalOrganizations.Eligible(c, n))
            {
                remaining.Add(n);
                if (newLeader is null || n.Gdp > newLeader.Gdp) newLeader = n;
            }
        }
        var isPillar = member.Id == org.FounderId || (totalGdp > 0 && member.Gdp >= totalGdp * 0.35f);
        if (isPillar)
        {
            org.Cohesion = MathF.Max(0.05f, org.Cohesion - 0.35f);
            if (remaining.Count >= 3 && org.Cohesion < c.Defines.OrganizationDissolveCohesion && newLeader is not null)
            {
                var companions = _leaveCompanionsScratch ??= new List<Nation>();
                companions.Clear();
                companions.Add(newLeader);
                for (var i = 0; i < remaining.Count; i++)
                {
                    var n = remaining[i];
                    if (n.Id != newLeader.Id && (CampaignDiplomacy.Peek(c, newLeader.Id, n.Id)?.Opinion ?? 0f) >= 20f)
                        companions.Add(n);
                }
                if (companions.Count >= 3)
                {
                    var newBloc = Found(c, org.CharterId, newLeader, companions);
                    if (newBloc is not null)
                    {
                        c.Chronicle.Record(c, "organization_reorganized", newLeader.Id, -1,
                            InternationalOrganizations.DisplayName(c, newBloc));
                    }
                }
                companions.Clear();
            }
        }
        remaining.Clear();
        if (InternationalOrganizations.Charter(org.CharterId)?.Defence == true)
        {
            for (var i = 0; i < org.Members.Count; i++)
            {
                var otherId = org.Members[i];
                if (otherId == member.Id) continue;
                var rel = CampaignDiplomacy.Peek(c, member.Id, otherId);
                if (rel is { Stance: DiplomaticStance.Allied } && !SharesOtherDefencePact(c, org.Id, member.Id, otherId))
                {
                    rel.Stance = DiplomaticStance.Neutral;
                    c.NoteAllianceChange();
                }
            }
        }
    }

    /// <summary>A defence pact's alliances with a departing member lapse, unless another pact still binds them.</summary>
    internal static void UnbindDefence(Campaign c, InternationalOrganization org, Nation member)
    {
        if (InternationalOrganizations.Charter(org.CharterId)?.Defence != true) return;
        for (var i = 0; i < org.Members.Count; i++)
        {
            var otherId = org.Members[i];
            if (otherId == member.Id) continue;
            var rel = CampaignDiplomacy.Peek(c, member.Id, otherId);
            if (rel is not { Stance: DiplomaticStance.Allied }) continue;
            if (!SharesOtherDefencePact(c, org.Id, member.Id, otherId))
            {
                rel.Stance = DiplomaticStance.Neutral;
                c.NoteAllianceChange();
            }
        }
    }

    internal static void DissolveNow(Campaign c, InternationalOrganization org) { if (!org.Dissolved) Dissolve(c, org); }

    internal static InternationalOrganization FoundBloc(Campaign c, string charterId, Nation founder, IReadOnlyList<Nation> members) =>
        Found(c, charterId, founder, members);

    internal static void ExpelForAggression(Campaign c, InternationalOrganization org, Nation aggressor, int victimId)
    {
        if (aggressor is null || !org.Members.Contains(aggressor.Id)) return;
        var name = InternationalOrganizations.DisplayName(c, org);
        org.Members.Remove(aggressor.Id);
        InternationalOrganizations.InvalidateDefencePartners();
        org.UnpaidWeeks.Remove(aggressor.Id);
        org.Motion?.Votes.Remove(aggressor.Id);
        org.Cohesion = MathF.Max(0f, org.Cohesion - 0.08f);
        Remember(c, org, aggressor, "attacked_a_fellow_member", c.NationById(victimId));
        c.Chronicle.Record(c, "organization_expelled", aggressor.Id, victimId, name);
        if (org.Members.Count == 0) Dissolve(c, org);
        c.MarkStrategicChange();
    }

    private static void Dissolve(Campaign c, InternationalOrganization org)
    {
        var name = InternationalOrganizations.DisplayName(c, org);
        var members = _dissolveMembersScratch ??= new List<Nation>(org.Members.Count);
        members.Clear();
        var gdp = 0f;
        Nation actorNation = null;
        for (var i = 0; i < org.Members.Count; i++)
        {
            var n = c.NationById(org.Members[i]);
            if (n is null) continue;
            members.Add(n);
            gdp += MathF.Max(0f, n.Gdp);
            if (actorNation is null || n.Gdp > actorNation.Gdp || (n.Gdp == actorNation.Gdp && n.Id < actorNation.Id))
                actorNation = n;
        }
        // The common fund goes back to the members, by the size of their economies.
        if (org.Budget > 0f && members.Count > 0)
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                var ledgerBefore = member.Treasury;
                member.Treasury += org.Budget * (gdp > 0f ? MathF.Max(0f, member.Gdp) / gdp : 1f / members.Count);
                MoneyLedger.Book(member, MoneyFlow.Organizations, member.Treasury - ledgerBefore);
            }
        if (org.Budget > 0f && members.Count > 0) OrganizationLedger.Book(org, OrganizationFlow.Returned, -org.Budget);
        org.Budget = 0f;
        org.Motion = null;
        org.Invitations.Clear();
        org.Suspended.Clear();
        org.SuspendedFor.Clear();
        org.Dissolved = true;
        InternationalOrganizations.InvalidateDefencePartners();
        var actor = actorNation?.Id ?? org.FounderId;
        c.Chronicle.Record(c, "organization_dissolved", actor, -1, name);
        if (InternationalOrganizations.Charter(org.CharterId)?.Defence == true)
        {
            for (var i = 0; i < members.Count; i++)
            for (var j = i + 1; j < members.Count; j++)
            {
                var rel = CampaignDiplomacy.Peek(c, members[i].Id, members[j].Id);
                if (rel is { Stance: DiplomaticStance.Allied } && !SharesOtherDefencePact(c, org.Id, members[i].Id, members[j].Id))
                {
                    rel.Stance = DiplomaticStance.Neutral;
                    c.NoteAllianceChange();
                }
            }
        }
        members.Clear();
        c.MarkStrategicChange();
    }

    /// <summary>
    /// What the rest of an organisation remembers of a member that walked out or attacked one of
    /// them. The members are witnesses, not victims: each minds as much as it concerns it
    /// (OpinionWitnesses), with the membership they shared counted as a tie of its own. A member
    /// that was the victim's rival is not sorry to see it attacked; one next door to the victim,
    /// or bound to it, minds in full; the member at the far end of a world-wide assembly notes it.
    /// </summary>
    private static void Remember(Campaign c, InternationalOrganization org, Nation subject, string reason, Nation victim = null)
    {
        foreach (var id in org.Members)
        {
            if (id == subject.Id || c.NationById(id) is not { } member) continue;
            OpinionWitnesses.Witness(c, member, subject, victim, reason, tie: c.Defines.WitnessOrganizationTie);
        }
    }

    [ThreadStatic] private static List<int> _pactPowersScratch;
    [ThreadStatic] private static List<Nation> _threatenedScratch;
    [ThreadStatic] private static List<Nation> _pactMembersScratch;
    [ThreadStatic] private static HashSet<int> _defendedScratch;
    [ThreadStatic] private static HashSet<string> _distinctContinentsScratch;
    [ThreadStatic] private static List<int> _foundMemberIdsScratch;

    private static int CountActiveOrgs(Campaign c)
    {
        var count = 0;
        var orgs = c.Organizations;
        for (var i = 0; i < orgs.Count; i++)
            if (!orgs[i].Dissolved) count++;
        return count;
    }

    private static bool AnyActiveOrgWithName(Campaign c, string name)
    {
        var orgs = c.Organizations;
        for (var i = 0; i < orgs.Count; i++)
        {
            var o = orgs[i];
            if (!o.Dissolved && o.Name == name) return true;
        }
        return false;
    }

    /// <summary>
    /// A new organisation of the founders, named for the first two capitals that signed - the
    /// Warsaw–Vilnius pact - or an authentic global/regional treaty name, and seeded with them all at once.
    /// </summary>
    private static InternationalOrganization Found(Campaign c, string charterId, Nation founder, IReadOnlyList<Nation> members)
    {
        if (members.Count < 2 || CountActiveOrgs(c) >= Math.Clamp(c.Defines.AiOrganizationWorldLimit + 8, 0, 120)) return null;
        var limit = Math.Clamp(c.Defines.OrganizationMembershipLimit, 1, 30);
        for (var i = 0; i < members.Count; i++)
        {
            var mCount = 0;
            var orgs = c.Organizations;
            var mid = members[i].Id;
            for (var oi = 0; oi < orgs.Count; oi++)
            {
                var o = orgs[oi];
                if (!o.Dissolved && o.Members.Contains(mid) && ++mCount >= limit) return null;
            }
        }
        Nation second = null;
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (m.Id == founder.Id) continue;
            if (second is null || m.Gdp > second.Gdp || (m.Gdp == second.Gdp && m.Id < second.Id))
                second = m;
        }
        var name = DetermineOrganizationName(c, charterId, founder, second, members);
        if (AnyActiveOrgWithName(c, name)) return null;
        var memberIds = _foundMemberIdsScratch ??= new List<int>(members.Count);
        memberIds.Clear();
        for (var i = 0; i < members.Count; i++) memberIds.Add(members[i].Id);
        var org = InternationalOrganizations.Seed(c, name, charterId, memberIds);
        memberIds.Clear();
        if (org is null || org.Name != name) return null;
        org.FounderId = founder.Id;
        org.Cohesion = 0.6f;
        if (charterId == "collective_defence")
        {
            for (var i = 0; i < members.Count; i++)
            for (var j = i + 1; j < members.Count; j++)
            {
                var rel = CampaignDiplomacy.Get(c, members[i].Id, members[j].Id);
                if (rel.Stance != DiplomaticStance.Allied) { rel.Stance = DiplomaticStance.Allied; c.NoteAllianceChange(); }
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
                                       founder.Gdp >= c.Defines.AiOrganizationGlobalGdp;
        if (!isGlobalOrCrossContinent)
        {
            var continents = _distinctContinentsScratch ??= new HashSet<string>();
            continents.Clear();
            for (var i = 0; i < members.Count; i++)
            {
                continents.Add(members[i].Continent);
                if (continents.Count > 1) { isGlobalOrCrossContinent = true; break; }
            }
        }

        if (charterId == "collective_defence")
        {
            if (isGlobalOrCrossContinent)
            {
                if ((founder.Continent is "north_america" && second.Continent is "europe") ||
                    (founder.Continent is "europe" && second.Continent is "north_america"))
                {
                    if (!AnyActiveOrgWithName(c, "org.name.transatlantic_treaty"))
                        return "org.name.transatlantic_treaty";
                }
                if (!AnyActiveOrgWithName(c, "org.name.global_security_coalition"))
                    return "org.name.global_security_coalition";
            }
            if (string.Equals(founder.Continent, "europe", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(second.Continent, "europe", StringComparison.OrdinalIgnoreCase) &&
                !AnyActiveOrgWithName(c, "org.name.european_defence_alliance"))
            {
                return "org.name.european_defence_alliance";
            }
            if ((string.Equals(founder.Continent, "asia", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(founder.Region, "east_asia", StringComparison.OrdinalIgnoreCase)) &&
                !AnyActiveOrgWithName(c, "org.name.indopacific_security_dialogue"))
            {
                return "org.name.indopacific_security_dialogue";
            }
            if ((founder.Continent is "asia" or "europe") && (founder.Region is "eastern_europe" or "central_asia" or "russia" || second.Region is "eastern_europe" or "central_asia") &&
                !AnyActiveOrgWithName(c, "org.name.eurasian_security_treaty"))
            {
                return "org.name.eurasian_security_treaty";
            }
            if (founder.Continent is "africa" && second.Continent is "africa" &&
                !AnyActiveOrgWithName(c, "org.name.african_defence_community"))
            {
                return "org.name.african_defence_community";
            }
            if (founder.Continent is "south_america" or "north_america" && second.Continent is "south_america" or "north_america" &&
                !AnyActiveOrgWithName(c, "org.name.panamerican_defence_pact"))
            {
                return "org.name.panamerican_defence_pact";
            }
            if (founder.Region is "middle_east" or "north_africa" && second.Region is "middle_east" or "north_africa" &&
                !AnyActiveOrgWithName(c, "org.name.middle_eastern_security_pact"))
            {
                return "org.name.middle_eastern_security_pact";
            }
        }
        else if (charterId == "development")
        {
            if (isGlobalOrCrossContinent)
            {
                if ((founder.Region is "east_asia" or "oceania" || second.Region is "east_asia" or "oceania") &&
                    !AnyActiveOrgWithName(c, "org.name.pacific_trade_partnership"))
                    return "org.name.pacific_trade_partnership";
                if (!AnyActiveOrgWithName(c, "org.name.global_trade_partnership"))
                    return "org.name.global_trade_partnership";
            }
            if (founder.Continent is "africa" && second.Continent is "africa" &&
                !AnyActiveOrgWithName(c, "org.name.african_economic_community"))
            {
                return "org.name.african_economic_community";
            }
            if (founder.Continent is "south_america" or "north_america" && second.Continent is "south_america" or "north_america" &&
                !AnyActiveOrgWithName(c, "org.name.panamerican_economic_community"))
            {
                return "org.name.panamerican_economic_community";
            }
            if ((founder.Region is "eastern_europe" or "central_asia" || second.Region is "eastern_europe" or "central_asia") &&
                !AnyActiveOrgWithName(c, "org.name.eurasian_economic_council"))
            {
                return "org.name.eurasian_economic_council";
            }
        }
        else if (charterId == "commodity_cartel")
        {
            if (isGlobalOrCrossContinent)
            {
                if (!AnyActiveOrgWithName(c, "org.name.petroleum_exporting_cartel"))
                    return "org.name.petroleum_exporting_cartel";
                if (!AnyActiveOrgWithName(c, "org.name.critical_minerals_cartel"))
                    return "org.name.critical_minerals_cartel";
            }
        }
        else // "cooperation"
        {
            if (isGlobalOrCrossContinent)
            {
                if (founder.Continent is "africa" or "asia" or "south_america" &&
                    second.Continent is "africa" or "asia" or "south_america" &&
                    !AnyActiveOrgWithName(c, "org.name.global_south_dialogue"))
                {
                    return "org.name.global_south_dialogue";
                }
                if (founder.NationalCourse.Dominant is "armed_neutrality" or "isolationism" &&
                    !AnyActiveOrgWithName(c, "org.name.non_aligned_coalition"))
                {
                    return "org.name.non_aligned_coalition";
                }
                if (!AnyActiveOrgWithName(c, "org.name.world_cooperation_council"))
                {
                    return "org.name.world_cooperation_council";
                }
            }
        }

        return $"org.name.{kind}|{CapitalOf(founder)}|{CapitalOf(second)}";
    }

    // By the capital city's key, not its raw name: the name is read back in the player's language
    // (InternationalOrganizations.DisplayName), and a city's name as its file spells it is a row
    // in neither.
    public static string CapitalOf(Nation nation) =>
        CapitalCore.NameKey(nation) is var capital && !string.IsNullOrWhiteSpace(capital) ? capital.Replace("|", " ") : nation.Tag;

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
        var powers = _pactPowersScratch ??= new List<int>(16);
        powers.Clear();
        foreach (var id in AllianceSystem.GreatPowers(c)) powers.Add(id);
        if (powers.Count > 1) powers.Sort();
        var threatened = _threatenedScratch ??= new List<Nation>(32);
        var members = _pactMembersScratch ??= new List<Nation>(5);
        var defended = _defendedScratch ??= new HashSet<int>();
        var active = c.ActiveNations;
        for (var pi = 0; pi < powers.Count; pi++)
        {
            var powerId = powers[pi];
            var power = c.NationById(powerId);
            if (power is null) continue;
            threatened.Clear();
            for (var ni = 0; ni < active.Count; ni++)
            {
                var n = active[ni];
                if (n.Id == powerId || n.IsHuman || n.IsSubject || n.AnswersTo(powerId)) continue;
                var relation = CampaignDiplomacy.Peek(c, n.Id, powerId);
                if (relation is null || relation.Stance == DiplomaticStance.Allied ||
                    (relation.Stance is not DiplomaticStance.Rival and not DiplomaticStance.AtWar && relation.Opinion > -40f)) continue;
                if (!CampaignDiplomacy.SharesLandBorder(c, n.MapIndex, power.MapIndex) &&
                    !(string.Equals(n.Region, power.Region, StringComparison.OrdinalIgnoreCase) &&
                      NavalOperations.Distance(n.CapitalLatitude, n.CapitalLongitude, power.CapitalLatitude, power.CapitalLongitude) < 3500f) &&
                    !(power.Gdp >= c.Defines.AiOrganizationGlobalGdp && (string.Equals(n.Continent, power.Continent, StringComparison.OrdinalIgnoreCase) || n.Gdp >= 300f)))
                    continue;
                if (!InternationalOrganizations.Eligible(c, n)) continue;
                var memCount = 0;
                var allOrgs = c.Organizations;
                for (var oi = 0; oi < allOrgs.Count; oi++)
                {
                    var o = allOrgs[oi];
                    if (!o.Dissolved && o.Members.Contains(n.Id) && ++memCount >= limit) break;
                }
                if (memCount >= limit) continue;
                threatened.Add(n);
            }
            if (threatened.Count < 2) continue;
            if (MovementRules.Roll(c.Seed, powerId, c.ElapsedDays, 4611) >= c.Defines.ThreatPactChance) continue;
            if (threatened.Count > 1)
                threatened.Sort(static (left, right) =>
                {
                    var cmp = right.Gdp.CompareTo(left.Gdp);
                    return cmp != 0 ? cmp : left.Id.CompareTo(right.Id);
                });
            var founder = threatened[0];
            defended.Clear();
            foreach (var dId in InternationalOrganizations.DefencePartners(c, founder.Id)) defended.Add(dId);
            members.Clear();
            members.Add(founder);
            for (var ti = 1; ti < threatened.Count; ti++)
            {
                var other = threatened[ti];
                if (defended.Contains(other.Id) || CampaignDiplomacy.AtWar(c, founder.Id, other.Id)) continue;
                if ((CampaignDiplomacy.Peek(c, founder.Id, other.Id)?.Opinion ?? 0f) < c.Defines.ThreatPactOpinion) continue;
                var warring = false;
                for (var mi = 0; mi < members.Count; mi++)
                    if (CampaignDiplomacy.AtWar(c, members[mi].Id, other.Id)) { warring = true; break; }
                if (warring) continue;
                members.Add(other);
                if (members.Count >= 5) break;
            }
            if (members.Count < 2) continue;
            if (Found(c, "collective_defence", founder, members) is not null) break;
        }
        threatened.Clear();
        members.Clear();
        defended.Clear();
    }
}
