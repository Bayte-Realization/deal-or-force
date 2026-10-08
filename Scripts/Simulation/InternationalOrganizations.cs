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

public sealed class OrganizationMotion
{
    public int Id, ProposerId, TargetId, EndsDay;
    public string Kind = "";
    public float Amount;
    public Dictionary<int, bool> Votes = new();
}

public sealed class InternationalOrganization
{
    public int Id, FounderId, FoundedDay, LastReviewDay = -1, NextMotionId = 1;
    public string Name = "", CharterId = "", LastOutcome = "";
    public string GeneratedCountryTag = "";
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
    public float OrganizationFoundingDeposit { get; set; } = .25f;
    public int OrganizationMembershipLimit { get; set; } = 6;
    public float OrganizationArrearsCohesionLoss { get; set; } = .012f;
    public int OrganizationDepartureTruceDays { get; set; } = 180;
    public bool AiMayFoundOrganizations { get; set; } = true;
    public int AiOrganizationWorldLimit { get; set; } = 28;
}

/// <summary>Institutions are campaign entities, not manufactured bilateral alliances.
/// A common treasury and an assembly decide admission and grants. Every member can
/// propose/vote/leave; founders cannot force membership, confiscate funds or spend alone.</summary>
public static class InternationalOrganizations
{
    public static string DisplayName(Campaign c, InternationalOrganization o) => string.IsNullOrEmpty(o.GeneratedCountryTag)
        ? Loc.Get(o.Name) : Loc.Format("Council of {0}", c.NationByTag(o.GeneratedCountryTag)?.DisplayName ?? o.GeneratedCountryTag);
    private static Dictionary<string, OrganizationCharter> _charters;
    public static IReadOnlyCollection<OrganizationCharter> Charters { get { Ensure(); return _charters.Values; } }
    public static OrganizationCharter Charter(string id) { Ensure(); return _charters.GetValueOrDefault(id ?? ""); }
    public static void ResetCatalog() => _charters = null;
    private static void Ensure()
    {
        if (_charters is not null) return;
        _charters = new(StringComparer.OrdinalIgnoreCase);
        foreach (var document in GameDataLoader.DataDocuments("common", "organizations"))
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("charters", out var rows) || rows.ValueKind != JsonValueKind.Object) continue;
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
        }
    }

    public static IEnumerable<InternationalOrganization> Active(Campaign c) => c.Organizations.Where(o => !o.Dissolved);
    public static IEnumerable<InternationalOrganization> Memberships(Campaign c, int nationId) => Active(c).Where(o => o.Members.Contains(nationId));
    public static IEnumerable<int> DefencePartners(Campaign c, int nationId) => !c.FeatureEnabled("organizations")
        ? Array.Empty<int>() : Memberships(c, nationId).Where(o => Charter(o.CharterId)?.Defence == true && o.Cohesion >= .25f)
            .SelectMany(o => o.Members).Where(id => id != nationId).Distinct().OrderBy(id => id);
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
        c.Organizations.Add(org); return org;
    }

    public static string Refusal(Campaign c, OrganizationCommand cmd)
    {
        if (c is null || !c.FeatureEnabled("organizations")) return Loc.Get("International organizations are disabled.");
        var n = c.NationById(cmd.IssuerId);
        if (!Eligible(c, n)) return Loc.Get("Only a living territorial country can participate.");
        var ownCount = Memberships(c, n.Id).Count();
        if (cmd.Action == "create")
        {
            if (!float.IsFinite(n.Treasury) || !float.IsFinite(c.Defines.OrganizationFoundingDeposit)) return Loc.Get("Invalid treasury or deposit.");
            if (Charter(cmd.CharterId) is null) return Loc.Get("Choose an organization charter.");
            if (string.IsNullOrWhiteSpace(cmd.Name) || cmd.Name.Trim().Length > 80 || cmd.Name.Any(char.IsControl)) return Loc.Get("Use a name of 1 to 80 characters.");
            if (Active(c).Any(o => string.Equals(o.Name, cmd.Name.Trim(), StringComparison.OrdinalIgnoreCase))) return Loc.Get("An organization already uses that name.");
            if (ownCount >= Math.Clamp(c.Defines.OrganizationMembershipLimit, 1, 30)) return Loc.Get("This country's organization membership limit is reached.");
            if (n.Treasury < Math.Max(0, c.Defines.OrganizationFoundingDeposit)) return Loc.Get("Not enough treasury for the founding deposit.");
            return "";
        }
        var org = Active(c).FirstOrDefault(o => o.Id == cmd.OrganizationId);
        if (org is null || Charter(org.CharterId) is null) return Loc.Get("That organization no longer exists.");
        var member = org.Members.Contains(n.Id);
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
        var target = c.NationById(cmd.TargetId);
        if (!Eligible(c, target)) return Loc.Get("Choose a living country.");
        if (cmd.Kind == "admission")
        {
            if (org.Members.Contains(target.Id) || org.Invitations.Contains(target.Id)) return Loc.Get("That country is already a member or invited.");
            if (org.Members.Any(id => CampaignDiplomacy.AtWar(c, target.Id, id))) return Loc.Get("A country cannot join while fighting a member.");
            return "";
        }
        if (cmd.Kind != "grant" || !org.Members.Contains(target.Id)) return Loc.Get("A common-fund grant must name a member.");
        if (!float.IsFinite(cmd.Amount) || cmd.Amount <= 0 || cmd.Amount > org.Budget ||
            !float.IsFinite(target.Treasury + cmd.Amount)) return Loc.Get("The grant must fit the existing common fund.");
        return "";
    }

    public static void Apply(Campaign c, OrganizationCommand cmd)
    {
        if (Refusal(c, cmd).Length > 0) return;
        var n = c.NationById(cmd.IssuerId);
        if (cmd.Action == "create")
        {
            var deposit = Math.Max(0, c.Defines.OrganizationFoundingDeposit);
            n.Treasury -= deposit;
            c.Organizations.Add(new InternationalOrganization { Id = c.NextOrganizationId++, FounderId = n.Id,
                Name = cmd.Name.Trim(), CharterId = cmd.CharterId, FoundedDay = c.ElapsedDays, Members = new() { n.Id }, Budget = deposit });
        }
        else
        {
            var org = c.Organizations.First(o => o.Id == cmd.OrganizationId);
            switch (cmd.Action)
            {
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
                    TargetId = cmd.TargetId, Kind = cmd.Kind, Amount = cmd.Amount,
                    EndsDay = c.ElapsedDays + Charter(org.CharterId).VoteDays, Votes = new() { [n.Id] = true } }; break;
            }
        }
        cmd.Changed = true; c.MarkStrategicChange();
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
        var target = c.NationById(m.TargetId);
        if (!Eligible(c, target)) return false;
        if (m.Kind == "admission") return !CampaignDiplomacy.AtWar(c, voter.Id, target.Id) &&
            (CampaignDiplomacy.Peek(c, voter.Id, target.Id)?.Opinion ?? 0) >= -10;
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
                    if (!Eligible(c, n)) { org.UnpaidWeeks[id] = org.UnpaidWeeks.GetValueOrDefault(id) + 1; unpaid = true; continue; }
                    var dues = Dues(c, org, n);
                    if (!float.IsFinite(dues) || n.Treasury < dues) { org.UnpaidWeeks[id] = org.UnpaidWeeks.GetValueOrDefault(id) + 1; unpaid = true; continue; }
                    if (!float.IsFinite(n.Treasury) || !float.IsFinite(org.Budget + dues)) { unpaid = true; continue; }
                    n.Treasury -= dues; org.Budget += dues; org.UnpaidWeeks[id] = 0;
                }
                var hostile = org.Members.Any(id => org.Members.Any(other => id < other && CampaignDiplomacy.AtWar(c, id, other)));
                org.Cohesion = Math.Clamp(org.Cohesion + (unpaid ? -Math.Max(0, c.Defines.OrganizationArrearsCohesionLoss) : .002f) - (hostile ? .05f : 0), 0, 1);
            }
            if (org.Motion is not { } m) continue;
            foreach (var id in org.Members)
                if (!m.Votes.ContainsKey(id) && c.NationById(id) is { IsHuman: false } voter) m.Votes[id] = AiVote(c, org, voter, m);
            if (c.ElapsedDays < m.EndsDay) continue;
            var yes = org.Members.Count(id => m.Votes.GetValueOrDefault(id));
            var passed = Charter(org.CharterId).Unanimity ? yes == org.Members.Count : yes > org.Members.Count / 2;
            var targetNation = c.NationById(m.TargetId);
            if (passed && Eligible(c, targetNation))
            {
                if (m.Kind == "admission" && !org.Members.Contains(m.TargetId) &&
                    !org.Members.Any(id => CampaignDiplomacy.AtWar(c, id, m.TargetId))) org.Invitations.Add(m.TargetId);
                else if (m.Kind == "grant" && org.Members.Contains(m.TargetId) && m.Amount <= org.Budget && float.IsFinite(targetNation.Treasury + m.Amount))
                { org.Budget -= m.Amount; targetNation.Treasury += m.Amount; }
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
        foreach (var org in Active(c).OrderBy(o => o.Id).ToArray())
        {
            if (org.Motion is not null || org.Invitations.Count > 0 || org.Members.Count > 12) continue;
            var proposer = org.Members.Select(c.NationById).FirstOrDefault(n => n is { IsHuman: false, IsSubject: false });
            if (proposer is null || (c.ElapsedDays + proposer.Id) % 30 != 0) continue;
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
                    string.Equals(n.Region, proposer.Region, StringComparison.OrdinalIgnoreCase) &&
                    (CampaignDiplomacy.Peek(c, proposer.Id, n.Id)?.Opinion ?? 0) >= 40 && WouldJoin(c, org, n))
                .OrderBy(n => n.Id).FirstOrDefault();
            if (partner is not null) new OrganizationCommand { IssuerId = proposer.Id, OrganizationId = org.Id,
                Action = "propose", Kind = "admission", TargetId = partner.Id }.Apply(c);
        }
        if (!c.Defines.AiMayFoundOrganizations || c.ElapsedDays < 365 || Active(c).Count() >= Math.Clamp(c.Defines.AiOrganizationWorldLimit, 0, 100)) return;
        foreach (var n in c.Nations.OrderBy(n => n.Id))
        {
            if (n.IsHuman || n.IsSubject || !Eligible(c, n) || Memberships(c, n.Id).Any() || (c.ElapsedDays + n.Id) % 365 != 0 ||
                n.Stability < .55f || n.Treasury < Math.Max(1, n.Gdp * .005f)) continue;
            var friend = c.Nations.Where(other => other.Id != n.Id && Eligible(c, other) && !other.IsSubject &&
                string.Equals(n.Region, other.Region, StringComparison.OrdinalIgnoreCase) &&
                (CampaignDiplomacy.Peek(c, n.Id, other.Id)?.Opinion ?? 0) >= 45).OrderBy(other => other.Id).FirstOrDefault();
            if (friend is null) continue;
            var command = new OrganizationCommand { IssuerId = n.Id, Action = "create", Name = "Council " + n.Tag, CharterId = "cooperation" };
            command.Apply(c);
            if (!command.Changed) continue;
            var created = c.Organizations.Last();
            created.GeneratedCountryTag = n.Tag;
            new OrganizationCommand { IssuerId = n.Id, OrganizationId = created.Id, Action = "propose", Kind = "admission", TargetId = friend.Id }.Apply(c);
            break;
        }
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
    public float Amount { get; init; }
    public bool Yes { get; init; }
    public bool Changed { get; internal set; }
    public void Apply(Campaign c) { if (MechanicFeatures.Allows(c, this)) InternationalOrganizations.Apply(c, this); }
}
