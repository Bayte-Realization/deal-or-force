using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed class AdoptedLaw
{
    public string Id = "";
    public string TargetTag = "";
    public int Region;
    public int AdoptedDay;
}

public sealed class PoliticalAgenda
{
    public string Id = "";
    public int DurationDays;
    public string Goal = "";
    public float GoalValue;
    public int ElapsedDays;
    public int SatisfiedDays;
    public bool Completed;
    public bool Succeeded;
}

public sealed partial class Nation
{
    public bool LawsActive = true;
    public bool AgendasActive = true;
    public Dictionary<string, AdoptedLaw> Laws = new(StringComparer.Ordinal);
    public Dictionary<string, int> PolicyReadyOnDay = new(StringComparer.Ordinal);
    public PoliticalAgenda Agenda;
    public int LastPolicyReviewDay = -1;
}

public static class PoliticalPolicies
{
    public static string Slot(PoliticalPolicy p, string targetTag = "", int region = 0) =>
        p.Scope == "foreign" ? p.SlotKey + ":" + targetTag :
        p.Scope == "region" ? p.SlotKey + ":" + region : p.SlotKey;

    public static PoliticalPolicy Effective(Nation n, PoliticalPolicy p, string target = "", int region = 0) =>
        n.Laws.TryGetValue(Slot(p, target, region), out var law) ? PoliticalPolicyCatalog.Find(law.Id) :
            PoliticalPolicyCatalog.Baseline(p);

    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation n)
    {
        if (n.LawsActive)
            foreach (var category in PoliticalPolicyCatalog.NationalCategories)
                if (Effective(n, category) is { } law && law.Modifiers.Count > 0)
                    yield return (law.Name, law.Modifiers);
        if (n.AgendasActive && n.Agenda is { Completed: false } agenda &&
            PoliticalPolicyCatalog.Find(agenda.Id, true) is { } definition)
            yield return (definition.Name, definition.Modifiers);
    }

    /// <summary>
    /// The nationalities law a government nobody plays moves to, or null to keep the one it has:
    /// fascism settles; the liberal and social-democratic families give up settling and forcing
    /// for civic integration; everybody else keeps what history or an earlier government left.
    /// </summary>
    private static PoliticalPolicy NationalitiesFor(Nation n)
    {
        var current = PoliticalPolicyCatalog.Find("nationalities_integration") is { } any ? Effective(n, any)?.Id ?? "" : "";
        var wanted = n.Ideology switch
        {
            "fascism" => "nationalities_settlement",
            "liberalism" or "social_democracy" or "libertarianism"
                when current is "nationalities_settlement" or "nationalities_assimilation" => "nationalities_integration",
            _ => null
        };
        return wanted is null ? null : PoliticalPolicyCatalog.Find(wanted);
    }

    public static bool CanAdopt(Campaign c, Nation n, PoliticalPolicy p, string target, int region, bool agenda, out string reason)
    {
        var hasNation = n is not null;
        var hasPolicy = p is not null;
        var featureEnabled = c is not null && hasPolicy && c.FeatureEnabled(agenda ? "political_agendas" : "laws");
        var scopeCode = !hasPolicy ? 0 : p.Scope == "region" ? 1 : p.Scope == "foreign" ? 2 : 0;
        var ownsRegion = scopeCode == 1 && hasNation && c is not null && c.StatesOf(n).Any(s => s.MapIndex == region);
        var validForeignTarget = scopeCode == 2 && hasNation && c is not null &&
            c.NationByTag(target) is { } other && other.Id != n.Id && !c.Fallen.Contains(other.Id);
        var atWar = hasNation && c is not null && c.Wars.Any(w => !w.Ended && w.Involves(n.Id));
        var slot = hasPolicy ? Slot(p, target, region) : "";
        var alreadyEnacted = !agenda && hasNation && hasPolicy && Effective(n, p, target, region)?.Id == p.Id;
        var hasSlotKey = hasNation && hasPolicy && n.Laws.ContainsKey(slot);
        var readyOnDay = hasNation && hasPolicy ? n.PolicyReadyOnDay.GetValueOrDefault(agenda ? "agenda" : slot) : 0;

        var code = DealOrForce.Rules.PoliticalPolicyRules.EvaluateAdoption(
            hasNation,
            hasPolicy,
            featureEnabled,
            agenda,
            hasNation && n.Agenda is { Completed: false },
            scopeCode,
            ownsRegion,
            validForeignTarget,
            hasPolicy && p.RequiresWar,
            atWar,
            alreadyEnacted,
            hasNation ? n.Laws.Count : 0,
            hasSlotKey,
            readyOnDay,
            c?.ElapsedDays ?? 0,
            hasNation ? n.Treasury : 0f,
            hasNation ? n.Gdp : 0f,
            hasPolicy ? p.CostShare : 0f);

        reason = code switch
        {
            DealOrForce.Rules.PolicyAdoptRefusal.Allowed => "",
            DealOrForce.Rules.PolicyAdoptRefusal.MechanicUnavailable => "Mechanic unavailable",
            DealOrForce.Rules.PolicyAdoptRefusal.AgendaInProgress => "An agenda is already in progress",
            DealOrForce.Rules.PolicyAdoptRefusal.InvalidRegion => "Choose a region you govern",
            DealOrForce.Rules.PolicyAdoptRefusal.InvalidForeignTarget => "Choose an existing foreign country or movement",
            DealOrForce.Rules.PolicyAdoptRefusal.RequiresActiveWar => "Requires an active war",
            DealOrForce.Rules.PolicyAdoptRefusal.AlreadyEnacted => "Already enacted",
            DealOrForce.Rules.PolicyAdoptRefusal.TooManyTargetedLaws => "Too many targeted laws",
            DealOrForce.Rules.PolicyAdoptRefusal.CoolingOff => "Reform cooling-off period",
            DealOrForce.Rules.PolicyAdoptRefusal.InsufficientTreasury => "Insufficient treasury for reform",
            _ => "Mechanic unavailable"
        };
        return reason.Length == 0;
    }

    public static bool Adopt(Campaign c, Nation n, PoliticalPolicy p, string target = "", int region = 0, bool agenda = false) =>
        Adopt(c, n, p, target, region, agenda, out _);

    public static bool Adopt(Campaign c, Nation n, PoliticalPolicy p, string target, int region, bool agenda, out string reason)
    {
        if (!CanAdopt(c, n, p, target, region, agenda, out reason)) return false;
        n.Treasury -= MathF.Max(0, n.Gdp * p.CostShare);
        n.PolicyReadyOnDay[agenda ? "agenda" : Slot(p, target, region)] = c.ElapsedDays + p.CooldownDays;
        if (agenda) n.Agenda = new PoliticalAgenda { Id = p.Id, DurationDays = p.DurationDays, Goal = p.Goal, GoalValue = p.GoalValue };
        else n.Laws[Slot(p, target, region)] = new AdoptedLaw { Id = p.Id, TargetTag = target, Region = region, AdoptedDay = c.ElapsedDays };
        NationalModifiers.Invalidate(n);
        c.Chronicle.Record(c, agenda ? "agenda_announced" : "law_enacted", n.Id, c.NationByTag(target)?.Id ?? -1, p.Name);
        ParliamentaryPolitics.OnLawAdopted(c, n, p);
        c.MarkStrategicChange(); return true;
    }

    public static float ForeignTrust(Campaign c, Nation n, Nation target)
    {
        if (!c.FeatureEnabled("laws")) return 0;
        var sum = 0f;
        foreach (var law in n.Laws.Values)
            if (law.TargetTag == target.Tag && PoliticalPolicyCatalog.Find(law.Id) is { Scope: "foreign" } d) sum += d.ForeignTrust;
        return Math.Clamp(sum, -.3f, .3f);
    }

    public static bool GoalMet(Campaign c, Nation n, PoliticalAgenda a)
    {
        var kind = DealOrForce.Rules.PoliticalPolicyRules.GoalKind(a.Goal);
        // Only peace goals inspect wars, preserving the old short-circuit cost.
        var atWar = kind == 3 && c.Wars.Any(w => !w.Ended && w.Involves(n.Id));
        return DealOrForce.Rules.PoliticalPolicyRules.GoalMet(kind,a.GoalValue,n.StandardOfLiving,
            n.PublicDebt,n.Gdp,(int)n.DebtCondition,atWar,n.Stability,(int)DebtCondition.Default);
    }

    public static void Tick(Campaign c)
    {
        NationalCourses.Tick(c);
        var lawsEnabled = c.FeatureEnabled("laws");
        var agendasEnabled = c.FeatureEnabled("political_agendas");
        var reformDay = c.ElapsedDays > 0 && c.ElapsedDays % 90 == 0;
        foreach (var n in c.Nations)
        {
            n.LawsActive = lawsEnabled; n.AgendasActive = agendasEnabled;
            if (n.LastPolicyReviewDay == c.ElapsedDays) continue;
            n.LastPolicyReviewDay = c.ElapsedDays;
            if (reformDay && !n.IsHuman && !c.Fallen.Contains(n.Id))
            {
                var desired = n.DebtCondition >= DebtCondition.Stressed ? "fiscal_discipline" :
                    n.Stability < .42f ? "security_civic_ombudsman" :
                    n.Stability < .50f ? "security_community" :
                    n.Ideology == "liberalism" ? "rights_association" :
                    n.Ideology == "social_democracy" ? "labour_corporatist" :
                    n.Ideology == "communism" ? "industry_champions" :
                    n.Ideology == "fascism" ? c.Wars.Any(w => !w.Ended && w.Involves(n.Id)) ? "service_total_levy" : "service_reserve" :
                    "education_technical";
                Adopt(c, n, PoliticalPolicyCatalog.Find(desired));
                // What a state does about its minorities changes with what it believes, and
                // only then: a fascist state settles, a liberal one stops settling and forcing.
                if (NationalitiesFor(n) is { } nationalities &&
                    !string.Equals(Effective(n, nationalities).Id, nationalities.Id, StringComparison.Ordinal))
                    Adopt(c, n, nationalities);
                if (n.AgendasActive && n.Agenda is not { Completed: false })
                {
                    var promise = n.DebtCondition >= DebtCondition.Stressed ? "fiscal_recovery" :
                        n.Stability >= .62f && !c.Wars.Any(w => !w.Ended && w.Involves(n.Id)) ? "peace_dividend" :
                        n.StandardOfLiving >= .61f ? "social_compact" : "national_reconciliation";
                    if (n.DebtCondition >= DebtCondition.Stressed || n.Stability >= .56f || n.StandardOfLiving >= .61f)
                        Adopt(c, n, PoliticalPolicyCatalog.Find(promise, true), agenda: true);
                }
            }
            if (n.LawsActive)
                foreach (var law in n.Laws.Values)
                {
                    if (PoliticalPolicyCatalog.Find(law.Id) is not { Scope: "region" } p) continue;
                    var region = c.StatesOf(n).FirstOrDefault(s => s.MapIndex == law.Region);
                    if (region is null) continue; // no effects in lost/foreign territory
                    float Value(string id) => p.Modifiers.TryGetValue(id, out var v) ? v / 7f : 0;
                    region.Radicalism = M.Clamp01(region.Radicalism + Value("radicalism_weekly"));
                    region.StandardOfLiving = M.Clamp01(region.StandardOfLiving + Value("living_weekly"));
                    region.Stability = M.Clamp01(region.Stability + Value("stability_weekly"));
                }
            if (!n.AgendasActive || n.Agenda is not { Completed: false } a) continue;
            a.ElapsedDays++;
            if (GoalMet(c, n, a)) a.SatisfiedDays++;
            if (a.ElapsedDays < a.DurationDays) continue;
            a.Completed = true;
            a.Succeeded = GoalMet(c, n, a) && a.SatisfiedDays >= Math.Max(14, a.DurationDays / 3);
            n.StabilityGovernance = Math.Clamp(n.StabilityGovernance + (a.Succeeded ? .015f : -.02f), -.20f, .20f);
            var name = PoliticalPolicyCatalog.Find(a.Id, true)?.Name ?? a.Id;
            c.Chronicle.Record(c, a.Succeeded ? "agenda_fulfilled" : "agenda_failed", n.Id, subject: name);
            NationalModifiers.Invalidate(n); c.MarkStrategicChange();
        }
    }
}

public sealed class AdoptPoliticalPolicyCommand : ICampaignCommand
{
    public int IssuerId { get; set; }
    public string PolicyId { get; set; } = "";
    public string TargetTag { get; set; } = "";
    public int Region { get; set; }
    public bool Agenda { get; set; }
    public void Apply(Campaign c) => PoliticalPolicies.Adopt(c, c.NationById(IssuerId),
        PoliticalPolicyCatalog.Find(PolicyId, Agenda), TargetTag, Region, Agenda);
}
