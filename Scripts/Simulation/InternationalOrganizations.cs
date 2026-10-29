using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed class OrganizationCharter
{
    public string Id = "", Name = "", Description = "";
    public bool Defence, Unanimity;
    public float WeeklyGdpDues = .00001f;
    public int VoteDays = 30;
}

public sealed class OrganizationDoctrine
{
    public string Id = "", Name = "", Description = "";
    public Dictionary<string, float> Modifiers = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OrganizationMotion
{
    public int Id, ProposerId, TargetId, EndsDay;
    public string Kind = "", Subject = "";
    public float Amount;
    public Dictionary<int, bool> Votes = new();
}

public sealed class InternationalOrganization
{
    public int Id, FounderId, FoundedDay, LastReviewDay = -1, NextMotionId = 1;
    public string Name = "", CharterId = "", LastOutcome = "";
    public string GeneratedCountryTag = "";
    public string DoctrineId = "";
    public int LastYes, LastVoters;
    public float Budget, Cohesion = .65f;
    public bool Dissolved;
    public List<int> Members = new(), Invitations = new();
    public Dictionary<int, int> UnpaidWeeks = new();
    public OrganizationMotion Motion;
}

public sealed partial class Campaign
{
    public readonly List<InternationalOrganization> Organizations = new();
    public int NextOrganizationId = 1;
}

public sealed partial class Defines
{
    /// <summary>
    /// A customs union: the doctrine that makes an organisation one, how much more its members
    /// buy from each other, and how much less from outside it (the common external tariff).
    /// </summary>
    public string CustomsUnionDoctrine { get; set; } = "common_market";
    public float CustomsUnionInsideTrade { get; set; } = 1.6f;
    public float CustomsUnionOutsideTrade { get; set; } = 0.88f;

    public float OrganizationFoundingDeposit { get; set; } = .25f;
    public int OrganizationMembershipLimit { get; set; } = 6;
    public int OrganizationApplicationCooldownDays { get; set; } = 30;
    public float OrganizationArrearsCohesionLoss { get; set; } = .012f;
    public int OrganizationDepartureTruceDays { get; set; } = 180;
    public bool AiMayFoundOrganizations { get; set; } = true;
    public int AiOrganizationWorldLimit { get; set; } = 28;
    public int AiOrganizationExpansionLimit { get; set; } = 64;
    public bool OrganicOrganizationReachEnabled { get; set; } = true;
    public float AiOrganizationGlobalGdp { get; set; } = 1000;
    public float AiOrganizationFoundingPoliticalCost { get; set; } = 30;
}

/// <summary>Institutions are campaign entities, not manufactured bilateral alliances.
/// A common treasury and an assembly decide admission and grants. Every member can
/// propose/vote/leave; founders cannot force membership, confiscate funds or spend alone.</summary>
public static class InternationalOrganizations
{
    public static string DisplayName(Campaign c, InternationalOrganization o)
    {
        if (!string.IsNullOrEmpty(o.GeneratedCountryTag))
            return Loc.Format("Council of {0}", c.NationByTag(o.GeneratedCountryTag)?.DisplayName ?? o.GeneratedCountryTag);
        // A name made in play is a template and the capitals it names: "org.name.pact|Warsaw|Vilnius"
        // (OrganizationDynamics), read in the player's language.
        if (o.Name.Contains('|'))
        {
            var parts = o.Name.Split('|');
            var places = parts.Skip(1).Select(place => (object)Loc.GetOr("capital." + place, Loc.Get(place))).ToArray();
            return Loc.Format(parts[0], places);
        }
        return Loc.Get(o.Name);
    }
    private static Dictionary<string, OrganizationCharter> _charters;
    private static Dictionary<string, OrganizationDoctrine> _doctrines;
    public static IReadOnlyCollection<OrganizationCharter> Charters { get { Ensure(); return _charters.Values; } }
    public static IReadOnlyCollection<OrganizationDoctrine> Doctrines { get { Ensure(); return _doctrines.Values; } }
    public static OrganizationCharter Charter(string id) { Ensure(); return _charters.GetValueOrDefault(id ?? ""); }
    public static OrganizationDoctrine Doctrine(string id) { Ensure(); return _doctrines.GetValueOrDefault(id ?? ""); }
    public static void ResetCatalog() { _charters = null; _doctrines = null; }
    private static void Ensure()
    {
        if (_charters is not null && _doctrines is not null) return;
        _charters = new(StringComparer.OrdinalIgnoreCase);
        _doctrines = new(StringComparer.OrdinalIgnoreCase);
        foreach (var document in GameDataLoader.DataDocuments("common", "organizations"))
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
            if (document.RootElement.TryGetProperty("charters", out var rows) && rows.ValueKind == JsonValueKind.Object)
                foreach (var row in rows.EnumerateObject())
                {
                    var d = new OrganizationCharter { Id = row.Name }; var e = row.Value;
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    string Text(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                    d.Name = Text("name"); d.Description = Text("description");
                    d.Defence = e.TryGetProperty("defence", out var defence) && defence.ValueKind == JsonValueKind.True;
                    d.Unanimity = e.TryGetProperty("unanimity", out var unanimity) && unanimity.ValueKind == JsonValueKind.True;
                    if (e.TryGetProperty("weekly_gdp_dues", out var dues) && dues.ValueKind == JsonValueKind.Number && dues.TryGetSingle(out var cost) && float.IsFinite(cost)) d.WeeklyGdpDues = Math.Clamp(cost, 0, .001f);
                    if (e.TryGetProperty("vote_days", out var days) && days.ValueKind == JsonValueKind.Number && days.TryGetInt32(out var duration)) d.VoteDays = Math.Clamp(duration, 7, 365);
                    _charters[row.Name] = d;
                }
            if (document.RootElement.TryGetProperty("doctrines", out var docs) && docs.ValueKind == JsonValueKind.Object)
                foreach (var row in docs.EnumerateObject())
                {
                    var e = row.Value;
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    string Text(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                    var mods = e.TryGetProperty("modifiers", out var m)
                        ? NationalModifiers.Read(m, "organizations.doctrines." + row.Name, null)
                        : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    _doctrines[row.Name] = new OrganizationDoctrine
                    {
                        Id = row.Name,
                        Name = Text("name"),
                        Description = Text("description"),
                        Modifiers = mods
                    };
                }
        }
    }

    public static float Influence(Campaign c, InternationalOrganization o, Nation n)
    {
        if (c is null || o is null || n is null || n.Population <= 0 || !o.Members.Contains(n.Id)) return 0f;
        var army = 0f;
        foreach (var a in c.Armies)
            if (a.NationId == n.Id) army += a.Divisions * a.Strength * Math.Max(0.15f, a.Organisation);
        var weight = Math.Max(0f, n.Gdp) * c.Defines.AiEconomicWeight + army;
        if (n.IsSubject) weight *= 0.5f;
        var unpaid = o.UnpaidWeeks.GetValueOrDefault(n.Id);
        if (unpaid > 0) weight *= MathF.Max(0.25f, 1f - unpaid * 0.08f);
        return weight;
    }

    public static Nation Leader(Campaign c, InternationalOrganization o)
    {
        if (c is null || o is null || o.Dissolved || o.Members.Count == 0) return null;
        return o.Members.Select(c.NationById).Where(n => n is not null && n.Population > 0)
            .OrderByDescending(n => Influence(c, o, n)).ThenBy(n => n.Id).FirstOrDefault();
    }

    public static Nation Challenger(Campaign c, InternationalOrganization o)
    {
        var leader = Leader(c, o);
        if (leader is null || o.Members.Count < 2) return null;
        var leadScore = Influence(c, o, leader);
        if (leadScore <= 0.001f) return null;
        var second = o.Members.Select(c.NationById).Where(n => n is not null && n.Id != leader.Id && n.Population > 0)
            .OrderByDescending(n => Influence(c, o, n)).ThenBy(n => n.Id).FirstOrDefault();
        return second is not null && Influence(c, o, second) >= leadScore * 0.55f ? second : null;
    }

    public static string PreferredDoctrine(Campaign c, InternationalOrganization o, Nation n)
    {
        if (c is null || o is null || n is null) return "sovereign_equality";
        var charter = Charter(o.CharterId);
        if (charter?.Defence == true)
        {
            if (c.Wars.Any(w => !w.Ended && w.Involves(n.Id)) || n.StrategicDoctrine.Id is "manoeuvre_warfare" or "homeland_defense")
                return Doctrine("integrated_command") is not null ? "integrated_command" : "sovereign_equality";
            return Doctrine("integrated_command") is not null ? "integrated_command" : "sovereign_equality";
        }
        if (o.CharterId == "commodity_cartel")
            return Doctrine("cartel_quotas") is not null ? "cartel_quotas" : "sovereign_equality";
        if (o.CharterId == "development" || n.NationalCourse.Dominant is "global_integration" or "market_reform")
            return Doctrine("common_market") is not null ? "common_market" : "sovereign_equality";
        if (n.NationalCourse.Dominant is "sovereign_autonomy" || n.Ideology is "nationalism" or "traditionalism")
            return Doctrine("sovereign_equality") is not null ? "sovereign_equality" : "";
        return Doctrine("sanctions_coordination") is not null ? "sanctions_coordination" : "sovereign_equality";
    }

    public static IEnumerable<InternationalOrganization> Active(Campaign c) => c.Organizations.Where(o => !o.Dissolved);
    public static IEnumerable<InternationalOrganization> Memberships(Campaign c, int nationId) => Active(c).Where(o => o.Members.Contains(nationId));

    // DefencePartners answers inside pair/third-country loops (CanDeclareWar, guarantees,
    // war aims): the LINQ chain allocated a membership enumeration, a Distinct buffer and
    // an OrderBy array per ask, per country, per day. The count stays small, so the answers
    // are kept per country and refilled only when the organization table changes.
    private static readonly Dictionary<int, List<int>> DefencePartnersByNation = new();
    private static int _defenceStamp = -1;
    private static Campaign _defenceWorld;

    private static int DefenceStamp(Campaign c)
    {
        var stamp = c.Organizations.Count * 1000003;
        foreach (var o in c.Organizations)
        {
            stamp = stamp ^ (o.Members.Count * 31 + (o.Dissolved ? 1 : 0) + o.Cohesion.GetHashCode());
        }
        return stamp;
    }

    // Membership commands invalidate immediately, even if two edits leave the same
    // count/cohesion. Do not hash every member in every hot-path threat query.
    public static void InvalidateDefencePartners() => _defenceStamp = -1;

    private static void EnsureDefencePartners(Campaign c)
    {
        var stamp = DefenceStamp(c);
        if (ReferenceEquals(_defenceWorld,c) && _defenceStamp == stamp && DefencePartnersByNation.Count > 0) return;
        DefencePartnersByNation.Clear();
        _defenceStamp = stamp; _defenceWorld = c;
        if (!c.FeatureEnabled("organizations")) return;
        foreach (var o in Active(c))
        {
            if (o.Dissolved || Charter(o.CharterId) is not { Defence: true } || o.Cohesion < .25f) continue;
            foreach (var memberId in o.Members)
            {
                if (!DefencePartnersByNation.TryGetValue(memberId, out var list))
                    DefencePartnersByNation[memberId] = list = new List<int>();
                foreach (var other in o.Members)
                    if (other != memberId && !list.Contains(other)) list.Add(other);
            }
        }
    }

    public static IReadOnlyList<int> DefencePartners(Campaign c, int nationId)
    {
        if (!c.FeatureEnabled("organizations")) return Array.Empty<int>();
        EnsureDefencePartners(c);
        return DefencePartnersByNation.TryGetValue(nationId, out var list) ? list : Array.Empty<int>();
    }
    public static float Dues(Campaign c, InternationalOrganization o, Nation n) =>
        Math.Max(0, n.Gdp) * (Charter(o.CharterId)?.WeeklyGdpDues ?? 0);
    public static bool Eligible(Campaign c, Nation n) => n is { Population: > 0, Movement: null } && !c.Fallen.Contains(n.Id) && c.HasOwnedLand(n);

    public static InternationalOrganization Seed(Campaign c, string name, string charter, IEnumerable<int> ids)
    {
        if (!c.FeatureEnabled("organizations") || Charter(charter) is null) return null;
        var members = ids.Distinct().Where(id => c.NationById(id) is not null).OrderBy(id => id).ToList();
        if (members.Count < 2) return null;
        var exists = Active(c).FirstOrDefault(o => o.Name == name);
        if (exists is not null) return exists;
        var org = new InternationalOrganization { Id = c.NextOrganizationId++, Name = name, CharterId = charter,
            FounderId = members[0], FoundedDay = c.ElapsedDays, Members = members };
        c.Organizations.Add(org); InvalidateDefencePartners(); return org;
    }

    // Keep the existing define name for saved rules/mods, but apply the same
    // price to every founder. The AI wrapper must not charge it a second time.
    public static float FoundingPoliticalCost(Campaign c) => c.Defines.OrganicOrganizationReachEnabled
        ? c.Defines.AiOrganizationFoundingPoliticalCost : 0f;

    private static string ApplicationKey(int organizationId) => "organization_application/" + organizationId;

    public static string Refusal(Campaign c, OrganizationCommand cmd)
    {
        if (c is null || !c.FeatureEnabled("organizations")) return Loc.Get("International organizations are disabled.");
        var n = c.NationById(cmd.IssuerId);
        if (!Eligible(c, n)) return Loc.Get("Only a country with land of its own can take part.");
        var ownCount = Memberships(c, n.Id).Count();
        if (cmd.Action == "create")
        {
            if (!float.IsFinite(n.Treasury) || !float.IsFinite(c.Defines.OrganizationFoundingDeposit)) return Loc.Get("Invalid treasury or deposit.");
            var politicalCost = FoundingPoliticalCost(c);
            if (!float.IsFinite(politicalCost) || politicalCost < 0 || !float.IsFinite(n.PoliticalPoints))
                return Loc.Get("Invalid political capital or founding cost.");
            if (Charter(cmd.CharterId) is null) return Loc.Get("Choose an organization charter.");
            if (string.IsNullOrWhiteSpace(cmd.Name) || cmd.Name.Trim().Length > 80 || cmd.Name.Any(char.IsControl)) return Loc.Get("Use a name of 1 to 80 characters.");
            if (Active(c).Any(o => string.Equals(o.Name, cmd.Name.Trim(), StringComparison.OrdinalIgnoreCase))) return Loc.Get("An organization already uses that name.");
            if (ownCount >= Math.Clamp(c.Defines.OrganizationMembershipLimit, 1, 30)) return Loc.Get("This country's organization membership limit is reached.");
            if (n.Treasury < Math.Max(0, c.Defines.OrganizationFoundingDeposit)) return Loc.Get("Not enough treasury for the founding deposit.");
            if (n.PoliticalPoints < politicalCost) return Loc.Get("Not enough political capital to found an organization.");
            return "";
        }
        var org = Active(c).FirstOrDefault(o => o.Id == cmd.OrganizationId);
        if (org is null || Charter(org.CharterId) is null) return Loc.Get("That organization no longer exists.");
        var member = org.Members.Contains(n.Id);
        if (cmd.Action == "request_membership")
        {
            if (member || org.Invitations.Contains(n.Id)) return Loc.Get("That country is already a member or invited.");
            if (ownCount >= Math.Clamp(c.Defines.OrganizationMembershipLimit, 1, 30)) return Loc.Get("This country's organization membership limit is reached.");
            if (org.Members.Any(id => CampaignDiplomacy.AtWar(c, n.Id, id))) return Loc.Get("A country cannot join while fighting a member.");
            if (org.Motion is not null) return Loc.Get("Finish the current assembly vote first.");
            var ready = n.DecisionAvailableDay.GetValueOrDefault(ApplicationKey(org.Id));
            if (ready > c.ElapsedDays) return Loc.Format("Another membership application can be submitted in {0} days.", ready - c.ElapsedDays);
            return "";
        }
        if (cmd.Action == "withdraw_application")
        {
            if (member || org.Motion is not { Kind: "admission" } application ||
                application.ProposerId != n.Id || application.TargetId != n.Id)
                return Loc.Get("Your country has no pending membership application here.");
            return application.EndsDay <= c.ElapsedDays ? Loc.Get("That assembly vote is closed.") : "";
        }
        if (cmd.Action is "join" or "decline")
        {
            if (member || !org.Invitations.Contains(n.Id)) return Loc.Get("There is no approved invitation to answer.");
            if (cmd.Action == "decline") return "";
            if (ownCount >= Math.Clamp(c.Defines.OrganizationMembershipLimit, 1, 30)) return Loc.Get("This country's organization membership limit is reached.");
            if (org.Members.Any(id => CampaignDiplomacy.AtWar(c, n.Id, id))) return Loc.Get("A country cannot join while fighting a member.");
            return "";
        }
        if (!member) return Loc.Get("Only members can decide the organization's affairs.");
        if (cmd.Action == "leave") return "";
        if (cmd.Action == "defend")
        {
            var war = c.Wars.Find(w => w.Id == cmd.WarId && !w.Ended);
            if (war is null || war.CivilConflict || war.Limited || war.Involves(n.Id) || Charter(org.CharterId)?.Defence != true ||
                org.Cohesion < .25f || !org.Members.Any(war.IsDefending)) return Loc.Get("There is no active collective-defence call for this country.");
            foreach (var enemy in war.EnemiesOf(war.DefenderId))
                if (!CampaignDiplomacy.CanDeclareWar(c, n.Id, enemy, out var why, onOwnInitiative: false)) return why;
            return "";
        }
        if (cmd.Action == "vote") return org.Motion is null || org.Motion.Id != cmd.MotionId || org.Motion.EndsDay <= c.ElapsedDays
            ? Loc.Get("That assembly vote is closed.") : "";
        if (cmd.Action != "propose") return Loc.Get("Unknown organization action.");
        if (org.Motion is not null) return Loc.Get("Finish the current assembly vote first.");
        if (cmd.Kind == "doctrine")
        {
            if (Doctrine(cmd.Subject) is null) return Loc.Get("Choose an organization doctrine.");
            if (string.Equals(org.DoctrineId, cmd.Subject, StringComparison.OrdinalIgnoreCase)) return Loc.Get("That doctrine is already in force.");
            return "";
        }
        var target = c.NationById(cmd.TargetId);
        if (!Eligible(c, target)) return Loc.Get("Choose a living country.");
        if (cmd.Kind == "admission")
        {
            if (org.Members.Contains(target.Id) || org.Invitations.Contains(target.Id)) return Loc.Get("That country is already a member or invited.");
            if (org.Members.Any(id => CampaignDiplomacy.AtWar(c, target.Id, id))) return Loc.Get("A country cannot join while fighting a member.");
            return "";
        }
        if (cmd.Kind == "sanction")
        {
            if (org.Members.Contains(target.Id)) return Loc.Get("An organization cannot vote collective sanctions against its own member.");
            if (org.Members.All(id => CampaignDiplomacy.Peek(c, id, target.Id)?.EmbargoBy(id) == true))
                return Loc.Get("Every member already enforces an embargo on that country.");
            return "";
        }
        if (cmd.Kind == "lift_sanction")
        {
            if (!org.Members.Any(id => CampaignDiplomacy.Peek(c, id, target.Id)?.EmbargoBy(id) == true))
                return Loc.Get("No member is enforcing an embargo on that country.");
            return "";
        }
        if (cmd.Kind != "grant" || !org.Members.Contains(target.Id)) return Loc.Get("A common-fund grant must name a member.");
        if (!float.IsFinite(cmd.Amount) || cmd.Amount <= 0 || cmd.Amount > org.Budget ||
            !float.IsFinite(target.Treasury + cmd.Amount)) return Loc.Get("The grant must fit the existing common fund.");
        return "";
    }

    public static void Apply(Campaign c, OrganizationCommand cmd)
    {
        cmd.Changed = false;
        if (Refusal(c, cmd).Length > 0) return;
        var n = c.NationById(cmd.IssuerId);
        if (cmd.Action == "create")
        {
            var deposit = Math.Max(0, c.Defines.OrganizationFoundingDeposit);
            n.Treasury -= deposit;
            n.PoliticalPoints -= FoundingPoliticalCost(c);
            c.Organizations.Add(new InternationalOrganization { Id = c.NextOrganizationId++, FounderId = n.Id,
                Name = cmd.Name.Trim(), CharterId = cmd.CharterId, FoundedDay = c.ElapsedDays, Members = new() { n.Id }, Budget = deposit });
            c.Chronicle.Record(c, "organization_founded", n.Id, -1, DisplayName(c, c.Organizations.Last()));
        }
        else
        {
            var org = c.Organizations.First(o => o.Id == cmd.OrganizationId);
            switch (cmd.Action)
            {
                case "request_membership":
                    // Applicants ask the assembly; they are not a member and receive no vote.
                    org.Motion = new OrganizationMotion { Id = org.NextMotionId++, ProposerId = n.Id,
                        TargetId = n.Id, Kind = "admission", EndsDay = c.ElapsedDays + Charter(org.CharterId).VoteDays };
                    n.DecisionAvailableDay[ApplicationKey(org.Id)] = org.Motion.EndsDay +
                        Math.Clamp(c.Defines.OrganizationApplicationCooldownDays, 1, 3650);
                    break;
                case "withdraw_application": org.Motion = null; break;
                case "join": org.Invitations.Remove(n.Id); org.Members.Add(n.Id); org.Members.Sort(); break;
                case "decline": org.Invitations.Remove(n.Id); break;
                case "leave":
                    foreach (var id in org.Members.Where(id => id != n.Id))
                    {
                        var bond = CampaignDiplomacy.Get(c, n.Id, id);
                        bond.TruceUntil = Math.Max(bond.TruceUntil, c.ElapsedDays + Math.Clamp(c.Defines.OrganizationDepartureTruceDays, 0, 3650));
                    }
                    org.Members.Remove(n.Id); org.UnpaidWeeks.Remove(n.Id); org.Motion?.Votes.Remove(n.Id);
                    org.Cohesion = Math.Max(0, org.Cohesion - .05f);
                    if (org.Members.Count == 0) { n.Treasury += org.Budget; org.Budget = 0; org.Dissolved = true; org.Motion = null; }
                    break;
                case "vote": org.Motion.Votes[n.Id] = cmd.Yes; break;
                case "defend":
                    var war = c.Wars.Find(w => w.Id == cmd.WarId);
                    if (!CampaignDiplomacy.JoinWar(c, war, n.Id, defending: true, "declared_war_on_us")) return;
                    org.Cohesion = Math.Min(1, org.Cohesion + .02f);
                    break;
                case "propose": org.Motion = new OrganizationMotion { Id = org.NextMotionId++, ProposerId = n.Id,
                    TargetId = cmd.TargetId, Kind = cmd.Kind, Subject = cmd.Subject ?? "", Amount = cmd.Amount,
                    EndsDay = c.ElapsedDays + Charter(org.CharterId).VoteDays, Votes = new() { [n.Id] = true } }; break;
            }
        }
        cmd.Changed = true; InvalidateDefencePartners(); c.MarkStrategicChange();
    }

    public static bool WouldJoin(Campaign c, InternationalOrganization org, Nation target)
    {
        if (Memberships(c, target.Id).Count() >= Math.Clamp(c.Defines.OrganizationMembershipLimit, 1, 30)) return false;
        if (org.Members.Any(id => CampaignDiplomacy.AtWar(c, id, target.Id))) return false;
        return org.Cohesion >= .35f && target.Treasury >= Dues(c, org, target) * 4 &&
            org.Members.Average(id => CampaignDiplomacy.Peek(c, id, target.Id)?.Opinion ?? 0) >= 5;
    }

    private static bool AiVote(Campaign c, InternationalOrganization org, Nation voter, OrganizationMotion m)
    {
        if (m.Kind == "doctrine")
        {
            if (Doctrine(m.Subject) is null) return false;
            if (string.Equals(PreferredDoctrine(c, org, voter), m.Subject, StringComparison.OrdinalIgnoreCase)) return true;
            var proposer = c.NationById(m.ProposerId);
            return proposer is not null && (CampaignDiplomacy.Peek(c, voter.Id, proposer.Id)?.Opinion ?? 0) >= 25;
        }
        var target = c.NationById(m.TargetId);
        if (!Eligible(c, target)) return false;
        if (m.Kind == "admission") return !CampaignDiplomacy.AtWar(c, voter.Id, target.Id) &&
            (CampaignDiplomacy.Peek(c, voter.Id, target.Id)?.Opinion ?? 0) >= -10;
        if (m.Kind == "sanction")
        {
            if (CampaignDiplomacy.AtWar(c, voter.Id, target.Id)) return true;
            var rel = CampaignDiplomacy.Peek(c, voter.Id, target.Id);
            if (rel is { Stance: DiplomaticStance.Allied }) return false;
            return (rel?.Opinion ?? 0) <= -15 || target.Notoriety >= 25f;
        }
        if (m.Kind == "lift_sanction")
        {
            if (CampaignDiplomacy.AtWar(c, voter.Id, target.Id)) return false;
            return (CampaignDiplomacy.Peek(c, voter.Id, target.Id)?.Opinion ?? 0) >= -5;
        }
        // Grants rescue an insolvent partner, but a proposer cannot routinely empty the
        // assembly treasury into its own healthy country. Target need and a reserve matter.
        return target.Treasury < Math.Max(1, target.Gdp * .005f) && m.Amount <= org.Budget * .6f;
    }

    public static void Tick(Campaign c)
    {
        if (!c.FeatureEnabled("organizations")) return;
        foreach (var org in Active(c).ToArray())
        {
            // A live mod reload can remove a charter. Freeze its institution, do not crash
            // an assembly or silently reinterpret its voting rule.
            if (Charter(org.CharterId) is null) continue;
            if (org.LastReviewDay == c.ElapsedDays) continue;
            org.LastReviewDay = c.ElapsedDays;
            foreach (var id in org.Invitations.ToArray())
                if (c.NationById(id) is { IsHuman: false } target)
                    new OrganizationCommand { IssuerId = id, OrganizationId = org.Id, Action = WouldJoin(c, org, target) ? "join" : "decline" }.Apply(c);
            if (c.ElapsedDays > org.FoundedDay && (c.ElapsedDays - org.FoundedDay) % 7 == 0)
            {
                var unpaid = false;
                foreach (var id in org.Members.ToArray())
                {
                    var n = c.NationById(id);
                    // A member with no ground of its own to tax owes nothing: arrears are a government's choice.
                    if (!Eligible(c, n)) continue;
                    var dues = Dues(c, org, n);
                    if (!float.IsFinite(dues) || n.Treasury < dues) { org.UnpaidWeeks[id] = org.UnpaidWeeks.GetValueOrDefault(id) + 1; unpaid = true; continue; }
                    if (!float.IsFinite(n.Treasury) || !float.IsFinite(org.Budget + dues)) { unpaid = true; continue; }
                    n.Treasury -= dues; org.Budget += dues; org.UnpaidWeeks[id] = 0;
                }
                // What members at war with each other do to it is part of where its cohesion is
                // heading (OrganizationDynamics.Target), not a second toll on top.
                org.Cohesion = Math.Clamp(org.Cohesion + (unpaid ? -Math.Max(0, c.Defines.OrganizationArrearsCohesionLoss) : .002f), 0, 1);
            }
            if (org.Motion is not { } m) continue;
            foreach (var id in org.Members)
                if (!m.Votes.ContainsKey(id) && c.NationById(id) is { IsHuman: false } voter) m.Votes[id] = AiVote(c, org, voter, m);
            if (c.ElapsedDays < m.EndsDay) continue;
            var yes = org.Members.Count(id => m.Votes.GetValueOrDefault(id));
            var passed = Charter(org.CharterId).Unanimity ? yes == org.Members.Count : yes > org.Members.Count / 2;
            var targetNation = c.NationById(m.TargetId);
            if (passed && m.Kind == "doctrine")
            {
                if (Doctrine(m.Subject) is { } doc)
                {
                    org.DoctrineId = doc.Id;
                    var proposer = c.NationById(m.ProposerId) ?? Leader(c, org);
                    if (proposer is not null)
                        c.Chronicle.Record(c, "organization_doctrine", proposer.Id, -1, DisplayName(c, org), doc.Name);
                }
                else passed = false;
            }
            else if (passed && Eligible(c, targetNation))
            {
                if (m.Kind == "admission" && !org.Members.Contains(m.TargetId) &&
                    !org.Members.Any(id => CampaignDiplomacy.AtWar(c, id, m.TargetId))) org.Invitations.Add(m.TargetId);
                else if (m.Kind == "grant" && org.Members.Contains(m.TargetId) && m.Amount <= org.Budget && float.IsFinite(targetNation.Treasury + m.Amount))
                { org.Budget -= m.Amount; targetNation.Treasury += m.Amount; }
                else if (m.Kind == "sanction" && !org.Members.Contains(m.TargetId))
                {
                    foreach (var id in org.Members)
                    {
                        if (id == m.TargetId) continue;
                        var rel = CampaignDiplomacy.Get(c, id, m.TargetId);
                        if (id == rel.A) rel.EmbargoByA = true; else rel.EmbargoByB = true;
                        rel.TradeAgreement = false;
                        OpinionSystem.Remember(c, rel, "embargoed_us");
                    }
                    var actor = c.NationById(m.ProposerId) ?? Leader(c, org);
                    if (actor is not null)
                        c.Chronicle.Record(c, "organization_sanction", actor.Id, targetNation.Id, DisplayName(c, org));
                }
                else if (m.Kind == "lift_sanction")
                {
                    foreach (var id in org.Members)
                    {
                        if (id == m.TargetId) continue;
                        var rel = CampaignDiplomacy.Peek(c, id, m.TargetId);
                        if (rel is null) continue;
                        if (id == rel.A) rel.EmbargoByA = false; else rel.EmbargoByB = false;
                    }
                }
                else passed = false;
            }
            else passed = false;
            org.LastOutcome = m.Kind + (passed ? "_passed" : "_rejected");
            org.LastYes = yes; org.LastVoters = org.Members.Count;
            org.Motion = null; c.MarkStrategicChange();
        }
        AiInitiatives(c);
    }

    private static void AiInitiatives(Campaign c)
    {
        var organic = c.Defines.OrganicOrganizationReachEnabled;
        foreach (var org in Active(c).OrderBy(o => o.Id).ToArray())
        {
            if (org.Motion is not null || org.Invitations.Count > 0) continue;
            var proposer = Leader(c, org) is { IsHuman: false, IsSubject: false } lead ? lead
                : org.Members.Select(c.NationById).FirstOrDefault(n => n is { IsHuman: false, IsSubject: false });
            if (proposer is null || (c.ElapsedDays + proposer.Id) % 30 != 0) continue;
            if (string.IsNullOrEmpty(org.DoctrineId))
            {
                var preferred = PreferredDoctrine(c, org, proposer);
                if (!string.IsNullOrEmpty(preferred) && Doctrine(preferred) is not null)
                {
                    new OrganizationCommand { IssuerId = proposer.Id, OrganizationId = org.Id, Action = "propose", Kind = "doctrine", Subject = preferred }.Apply(c);
                    if (org.Motion is not null) continue;
                }
            }
            var aggressor = c.Wars.Where(w => !w.Ended && !w.CivilConflict && org.Members.Any(w.IsDefending))
                .Select(w => c.NationById(w.AttackerId))
                .FirstOrDefault(a => a is not null && Eligible(c, a) && !org.Members.Contains(a.Id) &&
                    !org.Members.All(id => CampaignDiplomacy.Peek(c, id, a.Id)?.EmbargoBy(id) == true));
            if (aggressor is not null)
            {
                new OrganizationCommand { IssuerId = proposer.Id, OrganizationId = org.Id, Action = "propose", Kind = "sanction", TargetId = aggressor.Id }.Apply(c);
                if (org.Motion is not null) continue;
            }
            if (org.Members.Count >= (organic ? Math.Clamp(c.Defines.AiOrganizationExpansionLimit, 2, 160) : 13)) continue;
            var needy = org.Members.Select(c.NationById).Where(n => n is { Population: > 0 } && n.Treasury < Math.Max(1, n.Gdp * .002f))
                .OrderBy(n => n.Treasury).FirstOrDefault();
            if (needy is not null && org.Budget > .05f)
            {
                new OrganizationCommand { IssuerId = proposer.Id, OrganizationId = org.Id, Action = "propose", Kind = "grant",
                    TargetId = needy.Id, Amount = org.Budget * .3f }.Apply(c);
                continue;
            }
            if (c.ElapsedDays < org.FoundedDay + 180 || org.Cohesion < .5f) continue;
            var partner = c.Nations.Where(n => Eligible(c, n) && !n.IsSubject && !org.Members.Contains(n.Id) &&
                    OrganizationDynamics.ExpansionRelevant(c, org, proposer, n) &&
                    (CampaignDiplomacy.Peek(c, proposer.Id, n.Id)?.Opinion ?? 0) >= 40 && WouldJoin(c, org, n))
                .OrderBy(n => n.Id).FirstOrDefault();
            if (partner is not null) new OrganizationCommand { IssuerId = proposer.Id, OrganizationId = org.Id,
                Action = "propose", Kind = "admission", TargetId = partner.Id }.Apply(c);
        }
        if (!c.Defines.AiMayFoundOrganizations || c.ElapsedDays < 365 || Active(c).Count() >= Math.Clamp(c.Defines.AiOrganizationWorldLimit, 0, 100)) return;
        foreach (var n in c.Nations.OrderBy(n => n.Id))
        {
            if (n.IsHuman || n.IsSubject || !Eligible(c, n) ||
                (organic ? Memberships(c, n.Id).Count() >= Math.Clamp(c.Defines.OrganizationMembershipLimit,1,30) : Memberships(c,n.Id).Any()) ||
                ((c.ElapsedDays + n.Id) % 365 != 0 && (c.ElapsedDays + n.Id * 13) % 90 != 0) ||
                organic && (Active(c).Any(o=>o.FounderId==n.Id && o.FoundedDay>0) ||
                !float.IsFinite(n.PoliticalPoints) || !float.IsFinite(c.Defines.AiOrganizationFoundingPoliticalCost) || n.PoliticalPoints < Math.Max(0,c.Defines.AiOrganizationFoundingPoliticalCost)) ||
                n.Stability < .55f || n.Treasury < Math.Max(1, n.Gdp * .005f)) continue;
            var friend = c.Nations.Where(other => other.Id != n.Id && Eligible(c, other) && !other.IsSubject &&
                Memberships(c,other.Id).Count() < Math.Clamp(c.Defines.OrganizationMembershipLimit,1,30) &&
                !CampaignDiplomacy.AtWar(c,n.Id,other.Id) &&
                OrganizationDynamics.FoundingRelevant(c,n,other) &&
                (!organic || !Active(c).Any(o=>o.CharterId=="cooperation" && o.Cohesion>=.5f && o.Members.Contains(n.Id) && o.Members.Contains(other.Id))) &&
                (CampaignDiplomacy.Peek(c, n.Id, other.Id)?.Opinion ?? 0) >= 40).OrderBy(other => organic ? -other.Gdp : other.Id).ThenBy(other=>other.Id).FirstOrDefault();
            if (friend is null) continue;

            var charter = "cooperation";
            var mutualRel = CampaignDiplomacy.Peek(c, n.Id, friend.Id);
            var sharedThreat = OrganizationDynamics.SharedThreat(c, n, friend);
            var hasMilitaryFocus = n.NationalCourse.Dominant is "militarism" or "great_power" or "expansionism" or "forward_defense";
            var inDefencePactTogether = Active(c).Any(o => !o.Dissolved && Charter(o.CharterId)?.Defence == true && o.Members.Contains(n.Id) && o.Members.Contains(friend.Id));
            var inDevelopmentUnionTogether = Active(c).Any(o => !o.Dissolved && o.CharterId == "development" && o.Members.Contains(n.Id) && o.Members.Contains(friend.Id));

            if (!inDefencePactTogether && (sharedThreat || (hasMilitaryFocus && mutualRel?.Stance == DiplomaticStance.Allied)))
            {
                charter = "collective_defence";
            }
            else if (!inDevelopmentUnionTogether && (n.TradeDependenceOn(friend.Id) >= 0.03f || friend.TradeDependenceOn(n.Id) >= 0.03f))
            {
                charter = "development";
            }

            var chosenName = OrganizationDynamics.DetermineOrganizationName(c, charter, n, friend, new[] { n, friend });
            if (Active(c).Any(o => string.Equals(o.Name, chosenName, StringComparison.OrdinalIgnoreCase))) continue;

            var command = new FoundCooperationForumCommand { IssuerId = n.Id, TargetId = friend.Id, CharterId = charter, OrganizationName = chosenName };
            command.Apply(c);
            if (!command.Changed) continue;
            break;
        }
    }
}

/// <summary>The AI's political initiative wraps the existing institution and voting
/// commands. No direct wallet write in the planner and no forced partner membership.</summary>
public sealed class FoundCooperationForumCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public string CharterId { get; init; } = "cooperation";
    public string OrganizationName { get; init; } = "";
    public bool Changed { get; private set; }
    public void Apply(Campaign c)
    {
        Changed=false;
        if (!MechanicFeatures.Allows(c,this) || !c.FeatureEnabled("organizations")) return;
        var n=c.NationById(IssuerId); var friend=c.NationById(TargetId);
        var price=InternationalOrganizations.FoundingPoliticalCost(c);
        if (n is null || friend is null || n.Id==friend.Id || !InternationalOrganizations.Eligible(c,friend) ||
            !float.IsFinite(price) || price<0 || !float.IsFinite(n.PoliticalPoints) || n.PoliticalPoints<price ||
            CampaignDiplomacy.AtWar(c,n.Id,friend.Id) || !OrganizationDynamics.FoundingRelevant(c,n,friend)) return;
        var charter = string.IsNullOrEmpty(CharterId) ? "cooperation" : CharterId;
        var orgName = string.IsNullOrEmpty(OrganizationName)
            ? OrganizationDynamics.DetermineOrganizationName(c, charter, n, friend, new[] { n, friend })
            : OrganizationName;
        var create=new OrganizationCommand { IssuerId=n.Id,Action="create",Name=orgName,CharterId=charter };
        create.Apply(c); if (!create.Changed) return;
        var org=c.Organizations.Last();
        if (string.IsNullOrEmpty(OrganizationName) && orgName.StartsWith("Council "))
            org.GeneratedCountryTag=n.Tag;
        new OrganizationCommand { IssuerId=n.Id,OrganizationId=org.Id,Action="propose",Kind="admission",TargetId=friend.Id }.Apply(c);
        // The invitation is not membership or a bilateral alliance. The partner
        // keeps the same right to decline as when a player founds the institution.
        Changed=true; c.MarkStrategicChange();
    }
}

public sealed class OrganizationCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int OrganizationId { get; init; }
    public int TargetId { get; init; } = -1;
    public int MotionId { get; init; } = -1;
    public int WarId { get; init; } = -1;
    public string Action { get; init; } = "";
    public string Name { get; init; } = "";
    public string CharterId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Subject { get; init; } = "";
    public float Amount { get; init; }
    public bool Yes { get; init; }
    public bool Changed { get; internal set; }
    public void Apply(Campaign c) { if (MechanicFeatures.Allows(c, this)) InternationalOrganizations.Apply(c, this); }
}
