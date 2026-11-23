using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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

public sealed partial class InternationalOrganization
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
            Merge(document.RootElement, null);
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        Ensure();
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("charters", out var rows) && rows.ValueKind == JsonValueKind.Object)
            foreach (var row in rows.EnumerateObject())
            {
                if (row.Value.ValueKind != JsonValueKind.Object) continue;
                if (GameDataLoader.IsRemoved(row.Value, $"common/organizations:charters.{row.Name}"))
                {
                    _charters.Remove(row.Name);
                    continue;
                }
                var d = new OrganizationCharter { Id = row.Name }; var e = row.Value;
                string Text(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                d.Name = Text("name"); d.Description = Text("description");
                d.Defence = e.TryGetProperty("defence", out var defence) && defence.ValueKind == JsonValueKind.True;
                d.Unanimity = e.TryGetProperty("unanimity", out var unanimity) && unanimity.ValueKind == JsonValueKind.True;
                if (e.TryGetProperty("weekly_gdp_dues", out var dues) && dues.ValueKind == JsonValueKind.Number && dues.TryGetSingle(out var cost) && float.IsFinite(cost)) d.WeeklyGdpDues = Math.Clamp(cost, 0, .001f);
                if (e.TryGetProperty("vote_days", out var days) && days.ValueKind == JsonValueKind.Number && days.TryGetInt32(out var duration)) d.VoteDays = Math.Clamp(duration, 7, 365);
                _charters[row.Name] = d;
            }
        if (root.TryGetProperty("doctrines", out var docs) && docs.ValueKind == JsonValueKind.Object)
            foreach (var row in docs.EnumerateObject())
            {
                var e = row.Value;
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (GameDataLoader.IsRemoved(e, $"common/organizations:doctrines.{row.Name}"))
                {
                    _doctrines.Remove(row.Name);
                    continue;
                }
                string Text(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                var mods = e.TryGetProperty("modifiers", out var m)
                    ? NationalModifiers.Read(m, "organizations.doctrines." + row.Name, errors)
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

    [ThreadStatic] private static Dictionary<int, float> _memberArmyWeightsScratch;

    private static Dictionary<int, float> FillMemberArmyWeights(Campaign c, InternationalOrganization o)
    {
        var weights = _memberArmyWeightsScratch ??= new Dictionary<int, float>();
        weights.Clear();
        var members = o.Members;
        for (var i = 0; i < members.Count; i++) weights[members[i]] = 0f;
        var armies = c.Armies;
        for (var i = 0; i < armies.Count; i++)
        {
            var a = armies[i];
            if (weights.TryGetValue(a.NationId, out var sum))
                weights[a.NationId] = sum + a.Divisions * a.Strength * Math.Max(0.15f, a.Organisation);
        }
        return weights;
    }

    private static float InfluenceWithArmy(Campaign c, InternationalOrganization o, Nation n, float army)
    {
        var weight = Math.Max(0f, n.Gdp) * c.Defines.AiEconomicWeight + army;
        if (n.IsSubject) weight *= 0.5f;
        var unpaid = o.UnpaidWeeks.GetValueOrDefault(n.Id);
        if (unpaid > 0) weight *= MathF.Max(0.25f, 1f - unpaid * 0.08f);
        return weight;
    }

    public static Nation Leader(Campaign c, InternationalOrganization o)
    {
        if (c is null || o is null || o.Dissolved || o.Members.Count == 0) return null;
        var weights = FillMemberArmyWeights(c, o);
        Nation best = null;
        var bestInfluence = float.NegativeInfinity;
        var members = o.Members;
        for (var i = 0; i < members.Count; i++)
        {
            var n = c.NationById(members[i]);
            if (n is null || n.Population <= 0) continue;
            var infl = InfluenceWithArmy(c, o, n, weights.GetValueOrDefault(n.Id));
            if (best is null || infl > bestInfluence || (infl == bestInfluence && n.Id < best.Id))
            {
                best = n;
                bestInfluence = infl;
            }
        }
        return best;
    }

    public static Nation Challenger(Campaign c, InternationalOrganization o)
    {
        if (c is null || o is null || o.Dissolved || o.Members.Count < 2) return null;
        var weights = FillMemberArmyWeights(c, o);
        Nation leader = null, second = null;
        var leadScore = float.NegativeInfinity;
        var secondScore = float.NegativeInfinity;
        var members = o.Members;
        for (var i = 0; i < members.Count; i++)
        {
            var n = c.NationById(members[i]);
            if (n is null || n.Population <= 0) continue;
            var infl = InfluenceWithArmy(c, o, n, weights.GetValueOrDefault(n.Id));
            if (leader is null || infl > leadScore || (infl == leadScore && n.Id < leader.Id))
            {
                second = leader;
                secondScore = leadScore;
                leader = n;
                leadScore = infl;
            }
            else if (second is null || infl > secondScore || (infl == secondScore && n.Id < second.Id))
            {
                second = n;
                secondScore = infl;
            }
        }
        if (leader is null || leadScore <= 0.001f || second is null) return null;
        return secondScore >= leadScore * 0.55f ? second : null;
    }

    public static string PreferredDoctrine(Campaign c, InternationalOrganization o, Nation n)
    {
        if (c is null || o is null || n is null) return "sovereign_equality";
        var charter = Charter(o.CharterId);
        if (charter?.Defence == true)
        {
            var wars = Warfare.LiveWars(c);
            for (var i = 0; i < wars.Count; i++)
                if (!wars[i].Ended && wars[i].Involves(n.Id))
                    return Doctrine("integrated_command") is not null ? "integrated_command" : "sovereign_equality";
            if (n.StrategicDoctrine.Id is "manoeuvre_warfare" or "homeland_defense")
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

    internal static void FillMemberships(Campaign c, int nationId, List<InternationalOrganization> into)
    {
        into.Clear();
        var orgs = c?.Organizations;
        if (orgs is null) return;
        for (var i = 0; i < orgs.Count; i++)
        {
            var o = orgs[i];
            if (!o.Dissolved && o.Members.Contains(nationId)) into.Add(o);
        }
    }

    // DefencePartners answers inside pair/third-country loops (CanDeclareWar, guarantees,
    // war aims): the LINQ chain allocated a membership enumeration, a Distinct buffer and
    // an OrderBy array per ask, per country, per day. The count stays small, so the answers
    // are kept per country and refilled only when the organization table changes.
    private sealed class DefenceCache
    {
        public readonly Dictionary<int, List<int>> ByNation = new();
        public int Stamp = -1;
        public int Epoch = -1;
    }

    private static readonly ConditionalWeakTable<Campaign, DefenceCache> DefenceCaches = new();
    private static int _defenceEpoch;

    private static int DefenceStamp(Campaign c)
    {
        var orgs = c.Organizations;
        var stamp = orgs.Count * 1000003;
        for (var i = 0; i < orgs.Count; i++)
        {
            var o = orgs[i];
            stamp = stamp ^ (o.Members.Count * 31 + (o.Dissolved ? 1 : 0) + o.Cohesion.GetHashCode());
        }
        return stamp;
    }

    // Membership commands invalidate immediately, even if two edits leave the same
    // count/cohesion. Do not hash every member in every hot-path threat query.
    public static void InvalidateDefencePartners() => _defenceEpoch++;

    private static DefenceCache EnsureDefencePartners(Campaign c)
    {
        var cache = DefenceCaches.GetOrCreateValue(c);
        var stamp = DefenceStamp(c);
        if (cache.Epoch == _defenceEpoch && cache.Stamp == stamp) return cache;
        cache.ByNation.Clear();
        cache.Stamp = stamp;
        cache.Epoch = _defenceEpoch;
        if (!c.FeatureEnabled("organizations")) return cache;
        var orgs = c.Organizations;
        for (var oi = 0; oi < orgs.Count; oi++)
        {
            var o = orgs[oi];
            if (o.Dissolved || Charter(o.CharterId) is not { Defence: true } || o.Cohesion < .25f) continue;
            var members = o.Members;
            for (var i = 0; i < members.Count; i++)
            {
                var memberId = members[i];
                if (!cache.ByNation.TryGetValue(memberId, out var list))
                    cache.ByNation[memberId] = list = new List<int>();
                for (var j = 0; j < members.Count; j++)
                {
                    var other = members[j];
                    if (other != memberId && !list.Contains(other)) list.Add(other);
                }
            }
        }
        return cache;
    }

    internal static void WarmDefencePartners(Campaign c)
    {
        if (c is not null && c.FeatureEnabled("organizations")) EnsureDefencePartners(c);
    }

    public static IReadOnlyList<int> DefencePartners(Campaign c, int nationId)
    {
        if (!c.FeatureEnabled("organizations")) return Array.Empty<int>();
        var cache = EnsureDefencePartners(c);
        return cache.ByNation.TryGetValue(nationId, out var list) ? list : Array.Empty<int>();
    }

    internal static bool HasDefencePartner(Campaign c, int nationId, int otherId)
    {
        if (!c.FeatureEnabled("organizations")) return false;
        var cache = EnsureDefencePartners(c);
        return cache.ByNation.TryGetValue(nationId, out var list) && list.Contains(otherId);
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
            if (org.Suspended.ContainsKey(n.Id)) return Loc.Get("Your country's seat is suspended; only the assembly can give it back.");
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
        // A suspended member keeps one right: to give up its seat (OrganizationDiscipline).
        if (!member && cmd.Action == "leave" && org.Suspended.ContainsKey(n.Id)) return "";
        if (!member) return Loc.Get("Only members can decide the organization's affairs.");
        if (cmd.Action == "leave") return "";
        // A membership crisis: demanding better terms, or buying a member back (OrganizationCrises).
        if (cmd.Action == "demand_terms") return OrganizationCrises.DemandRefusal(c, org, n);
        if (cmd.Action == "concede") return OrganizationCrises.ConcedeRefusal(c, org, n);
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
        if (cmd.Kind is "suspend" or "expel" or "readmit")
        {
            if (target.Id == n.Id) return Loc.Get("A member cannot table a motion on its own seat.");
            if (cmd.Kind == "suspend" && !org.Members.Contains(target.Id)) return Loc.Get("Only a seated member can be suspended.");
            if (cmd.Kind == "suspend" && org.Members.Count < 3) return Loc.Get("Too few members to sit in judgement of one of them.");
            if (cmd.Kind == "expel" && !org.Members.Contains(target.Id) && !org.Suspended.ContainsKey(target.Id))
                return Loc.Get("Only a member can be expelled.");
            if (cmd.Kind == "readmit" && !org.Suspended.ContainsKey(target.Id)) return Loc.Get("That country's seat is not suspended.");
            return "";
        }
        if (cmd.Kind == "admission")
        {
            if (org.Members.Contains(target.Id) || org.Invitations.Contains(target.Id)) return Loc.Get("That country is already a member or invited.");
            if (org.Suspended.ContainsKey(target.Id)) return Loc.Get("That country's seat is suspended; vote to give it back instead.");
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
            { var ledgerBefore = n.Treasury; n.Treasury -= deposit; MoneyLedger.Book(n, MoneyFlow.Organizations, n.Treasury - ledgerBefore); }
            n.PoliticalPoints -= FoundingPoliticalCost(c);
            c.Organizations.Add(new InternationalOrganization { Id = c.NextOrganizationId++, FounderId = n.Id,
                Name = cmd.Name.Trim(), CharterId = cmd.CharterId, FoundedDay = c.ElapsedDays, Members = new() { n.Id }, Budget = deposit });
            OrganizationLedger.Book(c.Organizations[^1], OrganizationFlow.FoundingDeposit, deposit);
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
                case "leave" when org.Suspended.ContainsKey(n.Id) && !org.Members.Contains(n.Id):
                    OrganizationDiscipline.Withdraw(c, org, n, "organization_left");
                    break;
                case "leave":
                    foreach (var id in org.Members.Where(id => id != n.Id))
                    {
                        var bond = CampaignDiplomacy.Get(c, n.Id, id);
                        bond.TruceUntil = Math.Max(bond.TruceUntil, c.ElapsedDays + Math.Clamp(c.Defines.OrganizationDepartureTruceDays, 0, 3650));
                    }
                    org.Members.Remove(n.Id); org.UnpaidWeeks.Remove(n.Id); org.Motion?.Votes.Remove(n.Id);
                    org.Cohesion = Math.Max(0, org.Cohesion - .05f);
                    if (org.Members.Count == 0) { n.Treasury += org.Budget; MoneyLedger.Book(n, MoneyFlow.Organizations, org.Budget); OrganizationLedger.Book(org, OrganizationFlow.Returned, -org.Budget); org.Budget = 0; org.Dissolved = true; org.Motion = null; }
                    break;
                case "vote": org.Motion.Votes[n.Id] = cmd.Yes; break;
                case "demand_terms": OrganizationCrises.Demand(c, org, n); break;
                case "concede": OrganizationCrises.Concede(c, org, n); break;
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
        if (m.Kind is "suspend" or "expel" or "readmit") return OrganizationDiscipline.Vote(c, org, voter, m);
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

    [ThreadStatic] private static List<InternationalOrganization> _activeOrgsScratch;
    [ThreadStatic] private static List<int> _invitationsCopyScratch;
    [ThreadStatic] private static List<int> _membersCopyScratch;
    [ThreadStatic] private static List<Nation> _sortedActiveNationsScratch;
    [ThreadStatic] private static Dictionary<int, int> _membershipCountScratch;

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same organization pass, yielded between assemblies.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (!c.FeatureEnabled("organizations")) yield break;
        if (c.ElapsedDays > 0 && c.ElapsedDays % 365 == 0 && c.Organizations.Count > 0)
        {
            var orgList = c.Organizations;
            for (var i = orgList.Count - 1; i >= 0; i--)
            {
                var o = orgList[i];
                if (o.Dissolved && o.Members.Count == 0 && MathF.Abs(o.Budget) <= 0.001f && o.Motion is null &&
                    c.ElapsedDays - Math.Max(o.LastReviewDay, o.FoundedDay) > 3650)
                    orgList.RemoveAt(i);
            }
        }
        var activeOrgs = _activeOrgsScratch ??= new List<InternationalOrganization>();
        activeOrgs.Clear();
        var allOrgs = c.Organizations;
        for (var i = 0; i < allOrgs.Count; i++)
        {
            var org = allOrgs[i];
            if (!org.Dissolved) activeOrgs.Add(org);
        }
        var ticked = 0;
        for (var oi = 0; oi < activeOrgs.Count; oi++)
        {
            var org = activeOrgs[oi];
            if (++ticked % 4 == 0 && Campaign.StageOverdue()) yield return 0;
            // A live mod reload can remove a charter. Freeze its institution, do not crash
            // an assembly or silently reinterpret its voting rule.
            if (Charter(org.CharterId) is null) continue;
            if (org.LastReviewDay == c.ElapsedDays) continue;
            org.LastReviewDay = c.ElapsedDays;
            if (org.Invitations.Count > 0)
            {
                var invCopy = _invitationsCopyScratch ??= new List<int>();
                invCopy.Clear();
                for (var ii = 0; ii < org.Invitations.Count; ii++) invCopy.Add(org.Invitations[ii]);
                for (var ii = 0; ii < invCopy.Count; ii++)
                {
                    var id = invCopy[ii];
                    if (c.NationById(id) is { IsHuman: false } target)
                        new OrganizationCommand { IssuerId = id, OrganizationId = org.Id, Action = WouldJoin(c, org, target) ? "join" : "decline" }.Apply(c);
                }
            }
            if (c.ElapsedDays > org.FoundedDay && (c.ElapsedDays - org.FoundedDay) % 7 == 0)
            {
                // The fund's week closes where its dues are collected, before this week's dues.
                OrganizationLedger.Close(c, org);
                var unpaid = false;
                var memCopy = _membersCopyScratch ??= new List<int>();
                memCopy.Clear();
                for (var mi = 0; mi < org.Members.Count; mi++) memCopy.Add(org.Members[mi]);
                for (var mi = 0; mi < memCopy.Count; mi++)
                {
                    var id = memCopy[mi];
                    var n = c.NationById(id);
                    // A member with no ground of its own to tax owes nothing: arrears are a government's choice.
                    if (!Eligible(c, n)) continue;
                    var dues = Dues(c, org, n);
                    if (!float.IsFinite(dues) || n.Treasury < dues) { org.UnpaidWeeks[id] = org.UnpaidWeeks.GetValueOrDefault(id) + 1; unpaid = true; continue; }
                    if (!float.IsFinite(n.Treasury) || !float.IsFinite(org.Budget + dues)) { unpaid = true; continue; }
                    { var ledgerBefore = n.Treasury; n.Treasury -= dues; org.Budget += dues; OrganizationLedger.Book(org, OrganizationFlow.Dues, dues); org.UnpaidWeeks[id] = 0; MoneyLedger.Book(n, MoneyFlow.Organizations, n.Treasury - ledgerBefore); }
                }
                // What members at war with each other do to it is part of where its cohesion is
                // heading (OrganizationDynamics.Target), not a second toll on top.
                org.Cohesion = Math.Clamp(org.Cohesion + (unpaid ? -Math.Max(0, c.Defines.OrganizationArrearsCohesionLoss) : .002f), 0, 1);
            }
            if (org.Motion is not { } m) continue;
            for (var mi = 0; mi < org.Members.Count; mi++)
            {
                var id = org.Members[mi];
                if (!m.Votes.ContainsKey(id) && c.NationById(id) is { IsHuman: false } voter) m.Votes[id] = AiVote(c, org, voter, m);
            }
            if (c.ElapsedDays < m.EndsDay) continue;
            var yes = 0;
            for (var mi = 0; mi < org.Members.Count; mi++)
                if (m.Votes.GetValueOrDefault(org.Members[mi])) yes++;
            var passed = Charter(org.CharterId).Unanimity ? yes == org.Members.Count : yes > org.Members.Count / 2;
            var targetNation = c.NationById(m.TargetId);
            var voters = org.Members.Count;
            if (m.Kind is "suspend" or "expel" or "readmit")
            {
                // A seat motion: the accused has no vote, an expulsion needs two thirds.
                passed = OrganizationDiscipline.Carried(c, org, m, out yes, out voters) && OrganizationDiscipline.Carry(c, org, m);
            }
            else if (passed && m.Kind == "doctrine")
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
                if (m.Kind == "admission" && !org.Members.Contains(m.TargetId))
                {
                    var atWarWithMember = false;
                    for (var mi = 0; mi < org.Members.Count; mi++)
                        if (CampaignDiplomacy.AtWar(c, org.Members[mi], m.TargetId)) { atWarWithMember = true; break; }
                    if (!atWarWithMember) org.Invitations.Add(m.TargetId);
                }
                else if (m.Kind == "grant" && org.Members.Contains(m.TargetId) && m.Amount <= org.Budget && float.IsFinite(targetNation.Treasury + m.Amount))
                { org.Budget -= m.Amount; targetNation.Treasury += m.Amount; OrganizationLedger.Book(org, OrganizationFlow.Grants, -m.Amount); MoneyLedger.Book(targetNation, MoneyFlow.Organizations, m.Amount); }
                else if (m.Kind == "sanction" && !org.Members.Contains(m.TargetId))
                {
                    for (var mi = 0; mi < org.Members.Count; mi++)
                    {
                        var id = org.Members[mi];
                        if (id == m.TargetId) continue;
                        var rel = CampaignDiplomacy.Get(c, id, m.TargetId);
                        if (id == rel.A) rel.EmbargoByA = true; else rel.EmbargoByB = true;
                        // The organisation's act, not the member's: it costs the member nothing.
                        AgreementSystem.End(c, rel, AgreementSystem.TradeEnd.Sanctioned);
                        OpinionSystem.Remember(c, rel, "embargoed_us");
                    }
                    var actor = c.NationById(m.ProposerId) ?? Leader(c, org);
                    if (actor is not null)
                        c.Chronicle.Record(c, "organization_sanction", actor.Id, targetNation.Id, DisplayName(c, org));
                }
                else if (m.Kind == "lift_sanction")
                {
                    for (var mi = 0; mi < org.Members.Count; mi++)
                    {
                        var id = org.Members[mi];
                        if (id == m.TargetId) continue;
                        var rel = CampaignDiplomacy.Peek(c, id, m.TargetId);
                        if (rel is null) continue;
                        if (id == rel.A) rel.EmbargoByA = false; else rel.EmbargoByB = false;
                        // The grievance goes with the sanction. Lifting one used to leave
                        // "Refuses to trade with us" on the relation for good - a standing
                        // condition nobody was holding any more and nothing would ever forget.
                        if (!rel.EmbargoByA && !rel.EmbargoByB) OpinionSystem.Forget(c, rel, "embargoed_us");
                    }
                }
                else passed = false;
            }
            else passed = false;
            org.LastOutcome = m.Kind + (passed ? "_passed" : "_rejected");
            org.LastYes = yes; org.LastVoters = voters;
            org.Motion = null; c.MarkStrategicChange();
        }
        AiInitiatives(c);
        activeOrgs.Clear();
    }

    /// <summary>
    /// Whether most of the assembly does not yet hold an embargo on <paramref name="target"/>. Not
    /// "every member": a member friendly to the target lifts its own embargo the week after a vote,
    /// and an assembly that re-voted until it had none was voting the same sanctions every month.
    /// </summary>
    private static bool Unsanctioned(Campaign c, InternationalOrganization org, Nation target)
    {
        var members = 0;
        var holding = 0;
        foreach (var id in org.Members)
        {
            if (id == target.Id) continue;
            members++;
            if (CampaignDiplomacy.Peek(c, id, target.Id)?.EmbargoBy(id) == true) holding++;
        }
        return members > 0 && holding * 2 < members;
    }

    private static List<Nation> SortedActiveNations(Campaign c)
    {
        var list = _sortedActiveNationsScratch ??= new List<Nation>();
        list.Clear();
        var active = c.ActiveNations;
        for (var i = 0; i < active.Count; i++)
            list.Add(active[i]);
        if (list.Count > 1) list.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    private static void FillAllMemberArmyWeights(Campaign c, List<InternationalOrganization> activeOrgs, Dictionary<int, float> weights)
    {
        weights.Clear();
        var hasEligible = false;
        for (var oi = 0; oi < activeOrgs.Count; oi++)
        {
            var org = activeOrgs[oi];
            if (org.Motion is not null || org.Invitations.Count > 0 || org.Members.Count <= 1) continue;
            var members = org.Members;
            for (var mi = 0; mi < members.Count; mi++)
            {
                weights[members[mi]] = 0f;
                hasEligible = true;
            }
        }
        if (!hasEligible) return;
        var armies = c.Armies;
        for (var i = 0; i < armies.Count; i++)
        {
            var a = armies[i];
            if (weights.TryGetValue(a.NationId, out var sum))
                weights[a.NationId] = sum + a.Divisions * a.Strength * Math.Max(0.15f, a.Organisation);
        }
    }

    private static Nation LeaderFromWeights(Campaign c, InternationalOrganization o, Dictionary<int, float> weights)
    {
        if (c is null || o is null || o.Dissolved || o.Members.Count == 0) return null;
        if (o.Members.Count == 1)
            return c.NationById(o.Members[0]) is { Population: > 0 } single ? single : null;
        Nation best = null;
        var bestInfluence = float.NegativeInfinity;
        var members = o.Members;
        for (var i = 0; i < members.Count; i++)
        {
            var n = c.NationById(members[i]);
            if (n is null || n.Population <= 0) continue;
            var infl = InfluenceWithArmy(c, o, n, weights.GetValueOrDefault(n.Id));
            if (best is null || infl > bestInfluence || (infl == bestInfluence && n.Id < best.Id))
            {
                best = n;
                bestInfluence = infl;
            }
        }
        return best;
    }

    private static void AiInitiatives(Campaign c)
    {
        var organic = c.Defines.OrganicOrganizationReachEnabled;
        var activeOrgs = _activeOrgsScratch ??= new List<InternationalOrganization>();
        activeOrgs.Clear();
        var allOrgs = c.Organizations;
        for (var i = 0; i < allOrgs.Count; i++)
        {
            var org = allOrgs[i];
            if (!org.Dissolved) activeOrgs.Add(org);
        }
        if (activeOrgs.Count > 1) activeOrgs.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        var weights = _memberArmyWeightsScratch ??= new Dictionary<int, float>();
        FillAllMemberArmyWeights(c, activeOrgs, weights);

        List<Nation> sortedNations = null;
        var liveWars = Warfare.LiveWars(c);
        var shadowDays = c.Defines.NuclearShadowDays;
        var maxMembership = Math.Clamp(c.Defines.OrganizationMembershipLimit, 1, 30);

        for (var oi = 0; oi < activeOrgs.Count; oi++)
        {
            var org = activeOrgs[oi];
            if (org.Motion is not null || org.Invitations.Count > 0) continue;
            Nation proposer = LeaderFromWeights(c, org, weights) is { IsHuman: false, IsSubject: false } lead ? lead : null;
            if (proposer is null)
            {
                for (var mi = 0; mi < org.Members.Count; mi++)
                {
                    if (c.NationById(org.Members[mi]) is { IsHuman: false, IsSubject: false } cand)
                    { proposer = cand; break; }
                }
            }
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
            // The first government to use a nuclear weapon is everybody's business, not only its
            // enemies': every assembly it does not sit in moves to sanction it while the shadow lasts.
            sortedNations ??= SortedActiveNations(c);
            Nation aggressor = null;
            for (var ni = 0; ni < sortedNations.Count; ni++)
            {
                var a = sortedNations[ni];
                if (c.ElapsedDays - a.NuclearFirstUseDay < shadowDays &&
                    Eligible(c, a) && !org.Members.Contains(a.Id) && Unsanctioned(c, org, a))
                { aggressor = a; break; }
            }
            if (aggressor is null)
            {
                for (var wi = 0; wi < liveWars.Count; wi++)
                {
                    var w = liveWars[wi];
                    if (w.Ended || w.CivilConflict) continue;
                    var defenderMember = false;
                    for (var mi = 0; mi < org.Members.Count; mi++)
                        if (w.IsDefending(org.Members[mi])) { defenderMember = true; break; }
                    if (!defenderMember) continue;
                    var a = c.NationById(w.AttackerId);
                    if (a is not null && Eligible(c, a) && !org.Members.Contains(a.Id) && Unsanctioned(c, org, a))
                    { aggressor = a; break; }
                }
            }
            if (aggressor is not null)
            {
                new OrganizationCommand { IssuerId = proposer.Id, OrganizationId = org.Id, Action = "propose", Kind = "sanction", TargetId = aggressor.Id }.Apply(c);
                if (org.Motion is not null) continue;
            }
            if (org.Members.Count >= (organic ? Math.Clamp(c.Defines.AiOrganizationExpansionLimit, 2, 160) : 13)) continue;
            Nation needy = null;
            var bestNeedTreasury = float.PositiveInfinity;
            for (var mi = 0; mi < org.Members.Count; mi++)
            {
                var mn = c.NationById(org.Members[mi]);
                if (mn is { Population: > 0 } && mn.Treasury < Math.Max(1, mn.Gdp * .002f) && mn.Treasury < bestNeedTreasury)
                { needy = mn; bestNeedTreasury = mn.Treasury; }
            }
            if (needy is not null && org.Budget > .05f)
            {
                new OrganizationCommand { IssuerId = proposer.Id, OrganizationId = org.Id, Action = "propose", Kind = "grant",
                    TargetId = needy.Id, Amount = org.Budget * .3f }.Apply(c);
                continue;
            }
            if (c.ElapsedDays < org.FoundedDay + 180 || org.Cohesion < .5f) continue;
            Nation partner = null;
            for (var ni = 0; ni < sortedNations.Count; ni++)
            {
                var n = sortedNations[ni];
                if (n.IsSubject || !Eligible(c, n) || org.Members.Contains(n.Id) || org.Suspended.ContainsKey(n.Id)) continue;
                if ((CampaignDiplomacy.Peek(c, proposer.Id, n.Id)?.Opinion ?? 0) < 40) continue;
                if (OrganizationDynamics.ExpansionRelevant(c, org, proposer, n) && WouldJoin(c, org, n))
                { partner = n; break; }
            }
            if (partner is not null) new OrganizationCommand { IssuerId = proposer.Id, OrganizationId = org.Id,
                Action = "propose", Kind = "admission", TargetId = partner.Id }.Apply(c);
        }
        var worldLimit = Math.Clamp(c.Defines.AiOrganizationWorldLimit, 0, 100);
        if (!c.Defines.AiMayFoundOrganizations || c.ElapsedDays < 365 || activeOrgs.Count >= worldLimit) return;

        sortedNations ??= SortedActiveNations(c);
        var memCounts = _membershipCountScratch ??= new Dictionary<int, int>();
        memCounts.Clear();
        for (var oi = 0; oi < activeOrgs.Count; oi++)
        {
            var mem = activeOrgs[oi].Members;
            for (var mi = 0; mi < mem.Count; mi++)
            {
                var id = mem[mi];
                memCounts[id] = memCounts.GetValueOrDefault(id) + 1;
            }
        }
        var foundingCost = Math.Max(0, c.Defines.AiOrganizationFoundingPoliticalCost);
        for (var ni = 0; ni < sortedNations.Count; ni++)
        {
            var n = sortedNations[ni];
            if ((c.ElapsedDays + n.Id) % 365 != 0 && (c.ElapsedDays + n.Id * 13) % 90 != 0) continue;
            if (n.IsHuman || n.IsSubject || n.Stability < .55f || n.Treasury < Math.Max(1, n.Gdp * .005f)) continue;
            var nMemberships = memCounts.GetValueOrDefault(n.Id);
            if (organic ? nMemberships >= maxMembership : nMemberships > 0) continue;
            if (!Eligible(c, n)) continue;
            if (organic)
            {
                if (!float.IsFinite(n.PoliticalPoints) || !float.IsFinite(c.Defines.AiOrganizationFoundingPoliticalCost) ||
                    n.PoliticalPoints < foundingCost) continue;
                var alreadyFounded = false;
                for (var oi = 0; oi < activeOrgs.Count; oi++)
                    if (activeOrgs[oi].FounderId == n.Id && activeOrgs[oi].FoundedDay > 0) { alreadyFounded = true; break; }
                if (alreadyFounded) continue;
            }

            Nation friend = null;
            for (var oi = 0; oi < sortedNations.Count; oi++)
            {
                var other = sortedNations[oi];
                if (other.Id == n.Id || other.IsSubject) continue;
                if (memCounts.GetValueOrDefault(other.Id) >= maxMembership) continue;
                if ((CampaignDiplomacy.Peek(c, n.Id, other.Id)?.Opinion ?? 0) < 40) continue;
                if (!Eligible(c, other) || CampaignDiplomacy.AtWar(c, n.Id, other.Id)) continue;
                if (!OrganizationDynamics.FoundingRelevant(c, n, other)) continue;
                if (organic)
                {
                    var alreadyCoop = false;
                    for (var ai = 0; ai < activeOrgs.Count; ai++)
                    {
                        var ao = activeOrgs[ai];
                        if (ao.CharterId == "cooperation" && ao.Cohesion >= .5f && ao.Members.Contains(n.Id) && ao.Members.Contains(other.Id))
                        { alreadyCoop = true; break; }
                    }
                    if (alreadyCoop) continue;
                }
                if (friend is null) friend = other;
                else if (organic ? other.Gdp > friend.Gdp : other.Id < friend.Id) friend = other;
            }
            if (friend is null) continue;

            var charter = "cooperation";
            var mutualRel = CampaignDiplomacy.Peek(c, n.Id, friend.Id);
            var sharedThreat = OrganizationDynamics.SharedThreat(c, n, friend);
            var hasMilitaryFocus = n.NationalCourse.Dominant is "militarism" or "great_power" or "expansionism" or "forward_defense";
            var inDefencePactTogether = false;
            var inDevelopmentUnionTogether = false;
            for (var oi = 0; oi < activeOrgs.Count; oi++)
            {
                var ao = activeOrgs[oi];
                if (ao.Dissolved || !ao.Members.Contains(n.Id) || !ao.Members.Contains(friend.Id)) continue;
                if (!inDefencePactTogether && Charter(ao.CharterId)?.Defence == true) inDefencePactTogether = true;
                if (!inDevelopmentUnionTogether && ao.CharterId == "development") inDevelopmentUnionTogether = true;
            }

            if (!inDefencePactTogether && (sharedThreat || (hasMilitaryFocus && mutualRel?.Stance == DiplomaticStance.Allied)))
            {
                charter = "collective_defence";
            }
            else if (!inDevelopmentUnionTogether && (n.TradeDependenceOn(friend.Id) >= 0.03f || friend.TradeDependenceOn(n.Id) >= 0.03f))
            {
                charter = "development";
            }

            var chosenName = OrganizationDynamics.DetermineOrganizationName(c, charter, n, friend, new[] { n, friend });
            var nameTaken = false;
            for (var oi = 0; oi < activeOrgs.Count; oi++)
                if (string.Equals(activeOrgs[oi].Name, chosenName, StringComparison.OrdinalIgnoreCase)) { nameTaken = true; break; }
            if (nameTaken) continue;

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
