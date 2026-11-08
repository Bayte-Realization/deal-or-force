using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    /// <summary>The day each of its organisations last bought it back with a concession (OrganizationCrises), by organisation id.</summary>
    public Dictionary<int, int> OrganizationConcessions = new();
}

public sealed partial class Defines
{
    /// <summary>
    /// A membership crisis (OrganizationCrises): how long a member that has stopped belonging
    /// bargains before the vote on leaving, and how much the organisation's cohesion bleeds each
    /// week meanwhile. 0 days: members walk out at once, as before.
    /// </summary>
    public int OrganizationCrisisDays { get; set; } = 180;
    public float OrganizationCrisisCohesionDrain { get; set; } = 0.006f;
    /// <summary>A crisis only in an organisation of at least this many members; a pair simply parts.</summary>
    public int OrganizationCrisisMinimumMembers { get; set; } = 3;
    /// <summary>
    /// What a concession costs whoever makes it, as a share of the GDP of the member bought back
    /// (paid to it), and what it is worth to that member's sense of belonging, halving every so
    /// many years - the British rebate of 1984 bought a generation, not for ever.
    /// </summary>
    public float OrganizationConcessionShare { get; set; } = 0.003f;
    public float OrganizationConcessionBelonging { get; set; } = 0.18f;
    public float OrganizationConcessionHalfLifeYears { get; set; } = 3f;
    /// <summary>How often a member may open a crisis of its own by demanding better terms.</summary>
    public int OrganizationDemandCooldownDays { get; set; } = 730;
    /// <summary>
    /// The computer's leader buys a member back when it carries at least this share of the
    /// organisation's economy, can pay three times the price, and the week's roll falls under the chance.
    /// </summary>
    public float AiConcessionValueShare { get; set; } = 0.04f;
    public float AiConcessionChance { get; set; } = 0.25f;
}

public sealed partial class InternationalOrganization
{
    /// <summary>The member in a membership crisis, and since when; -1 for none.</summary>
    public int CrisisMemberId = -1;
    public int CrisisSinceDay = -1;
}

/// <summary>
/// Membership crises: the bargaining before a country leaves.
///
/// A member that had stopped belonging to its organisation used to walk out on a weekly roll, and
/// that was the whole of it - no Brexit referendum, no Greek summer of 2015, no Turkey holding
/// Sweden's NATO membership hostage for a better deal. Now a member that would leave opens a
/// crisis instead: it demands better terms, the organisation bleeds cohesion while it lasts, and
/// for half a year any other member - its leader first - can buy it back with a concession paid
/// out of its own treasury. A concession lifts the member's belonging for years; without one, the
/// crisis ends in a vote, and the vote is on how the member feels on the day. A member may also
/// open a crisis of its own to extract a concession, as the player can. The same rules and prices
/// for every government.
/// </summary>
public static class OrganizationCrises
{
    public static bool Active(Campaign c) => c.Defines.OrganizationCrisisDays > 0;

    private static string DemandKey(int organizationId) => "organization_demand:" + organizationId;

    /// <summary>What a concession to this member costs now.</summary>
    public static float ConcessionCost(Campaign c, Nation member) =>
        member is null ? 0f : MathF.Max(0f, member.Gdp) * MathF.Max(0f, c.Defines.OrganizationConcessionShare);

    /// <summary>What a past concession still adds to a member's sense of belonging.</summary>
    public static float ConcessionBelonging(Campaign c, InternationalOrganization org, Nation member)
    {
        if (member is null || org is null || !member.OrganizationConcessions.TryGetValue(org.Id, out var day)) return 0f;
        var years = MathF.Max(0f, c.ElapsedDays - day) / 365f;
        return MathF.Max(0f, c.Defines.OrganizationConcessionBelonging) *
               MathF.Pow(0.5f, years / MathF.Max(0.25f, c.Defines.OrganizationConcessionHalfLifeYears));
    }

    /// <summary>
    /// A member that would leave opens a crisis instead, when crises are on, the organisation is
    /// large enough and none is under way. True when it did; false leaves the departure to go ahead.
    /// </summary>
    public static bool Open(Campaign c, InternationalOrganization org, Nation member)
    {
        if (!Active(c) || org is null || member is null || !org.Members.Contains(member.Id)) return false;
        if (org.CrisisMemberId == member.Id) return true;
        if (org.CrisisMemberId >= 0 || org.Members.Count < Math.Max(2, c.Defines.OrganizationCrisisMinimumMembers)) return false;
        org.CrisisMemberId = member.Id;
        org.CrisisSinceDay = c.ElapsedDays;
        c.Chronicle.Record(c, "organization_crisis", member.Id, -1, InternationalOrganizations.DisplayName(c, org));
        c.MarkStrategicChange();
        return true;
    }

    public static string DemandRefusal(Campaign c, InternationalOrganization org, Nation member)
    {
        if (!Active(c)) return Loc.Get("Membership crises are disabled.");
        if (org.CrisisMemberId == member.Id) return Loc.Get("Your country is already bargaining with this organization.");
        if (org.CrisisMemberId >= 0) return Loc.Get("Another member's crisis is under way; one at a time.");
        if (org.Members.Count < Math.Max(2, c.Defines.OrganizationCrisisMinimumMembers))
            return Loc.Get("Too few members to bargain with; leaving is the only lever.");
        var ready = member.DecisionAvailableDay.GetValueOrDefault(DemandKey(org.Id));
        if (ready > c.ElapsedDays) return Loc.Format("Better terms can be demanded again in {0} days.", ready - c.ElapsedDays);
        return "";
    }

    /// <summary>A member opens a crisis of its own: better terms, or it may go.</summary>
    public static void Demand(Campaign c, InternationalOrganization org, Nation member)
    {
        if (DemandRefusal(c, org, member).Length > 0) return;
        member.DecisionAvailableDay[DemandKey(org.Id)] = c.ElapsedDays + Math.Max(1, c.Defines.OrganizationDemandCooldownDays);
        Open(c, org, member);
    }

    public static string ConcedeRefusal(Campaign c, InternationalOrganization org, Nation giver)
    {
        if (org.CrisisMemberId < 0) return Loc.Get("No member is bargaining for better terms.");
        if (org.CrisisMemberId == giver.Id) return Loc.Get("A country cannot buy itself back.");
        var member = c.NationById(org.CrisisMemberId);
        var cost = ConcessionCost(c, member);
        if (!float.IsFinite(cost) || giver.Treasury < cost)
            return Loc.Format("A concession costs {0}; the treasury holds {1}.", Fmt.Money(cost), Fmt.Money(giver.Treasury));
        return "";
    }

    /// <summary>Buys the member back: the price goes to it, its belonging rises for years, the crisis ends.</summary>
    public static void Concede(Campaign c, InternationalOrganization org, Nation giver)
    {
        if (ConcedeRefusal(c, org, giver).Length > 0) return;
        var member = c.NationById(org.CrisisMemberId);
        var cost = ConcessionCost(c, member);
        giver.Treasury -= cost;
        member.Treasury += cost;
        member.OrganizationConcessions[org.Id] = c.ElapsedDays;
        if (CampaignDiplomacy.Get(c, member.Id, giver.Id) is { } relation) OpinionSystem.Remember(c, relation, "bought_us_back");
        org.CrisisMemberId = -1;
        org.CrisisSinceDay = -1;
        c.Chronicle.Record(c, "organization_concession", giver.Id, member.Id, InternationalOrganizations.DisplayName(c, org));
        c.MarkStrategicChange();
    }

    /// <summary>
    /// The week of a crisis: cohesion bleeds, the computer's leader may buy the member back, and
    /// when the time is up the member votes - and goes, if it still does not belong. True when the
    /// organisation was dissolved by a departure.
    /// </summary>
    public static bool Week(Campaign c, InternationalOrganization org, Func<Nation, float> belonging, Action<Nation> leave)
    {
        if (org.CrisisMemberId < 0) return false;
        var member = c.NationById(org.CrisisMemberId);
        if (member is null || !org.Members.Contains(member.Id))
        {
            org.CrisisMemberId = -1;
            org.CrisisSinceDay = -1;
            return false;
        }
        var d = c.Defines;
        org.Cohesion = MathF.Max(0f, org.Cohesion - MathF.Max(0f, d.OrganizationCrisisCohesionDrain));

        var leader = InternationalOrganizations.Leader(c, org);
        var economy = org.Members.Select(c.NationById).Where(n => n is not null).Sum(n => MathF.Max(0f, n.Gdp));
        var share = economy > 0f ? MathF.Max(0f, member.Gdp) / economy : 0f;
        if (leader is { IsHuman: false } && leader.Id != member.Id && share >= d.AiConcessionValueShare &&
            leader.Treasury >= ConcessionCost(c, member) * 3f &&
            MovementRules.Roll(c.Seed, org.Id, c.ElapsedDays, 4700 + member.Id) < d.AiConcessionChance)
        {
            Concede(c, org, leader);
            return false;
        }

        if (c.ElapsedDays - org.CrisisSinceDay < Math.Max(1, d.OrganizationCrisisDays)) return false;
        org.CrisisMemberId = -1;
        org.CrisisSinceDay = -1;
        var name = InternationalOrganizations.DisplayName(c, org);
        // The player's own country decides for itself whether to go; only the computer's vote here.
        if (!member.IsHuman && belonging(member) < d.OrganizationDepartureBelonging)
        {
            c.Chronicle.Record(c, "organization_exit_vote", member.Id, -1, name);
            leave(member);
            return org.Dissolved;
        }
        c.Chronicle.Record(c, "organization_crisis_passed", member.Id, -1, name);
        return false;
    }
}
