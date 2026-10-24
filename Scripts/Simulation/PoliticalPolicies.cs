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
        reason = "";
        if (n is null || p is null || !c.FeatureEnabled(agenda ? "political_agendas" : "laws")) reason = "Mechanic unavailable";
        else if (agenda && n.Agenda is { Completed: false }) reason = "An agenda is already in progress";
        else if (p.Scope == "region" && !c.StatesOf(n).Any(s => s.MapIndex == region)) reason = "Choose a region you govern";
        else if (p.Scope == "foreign" && (c.NationByTag(target) is not { } other || other.Id == n.Id || c.Fallen.Contains(other.Id)))
            reason = "Choose an existing foreign country or movement";
        else if (!agenda && Effective(n, p, target, region)?.Id == p.Id) reason = "Already enacted";
        else if (!agenda && n.Laws.Count >= 64 && !n.Laws.ContainsKey(Slot(p, target, region))) reason = "Too many targeted laws";
        else if (n.PolicyReadyOnDay.GetValueOrDefault(agenda ? "agenda" : Slot(p, target, region)) > c.ElapsedDays)
            reason = "Reform cooling-off period";
        else if (n.Treasury < n.Gdp * p.CostShare) reason = "Insufficient treasury for reform";
        return reason.Length == 0;
    }

    public static bool Adopt(Campaign c, Nation n, PoliticalPolicy p, string target = "", int region = 0, bool agenda = false)
    {
        if (!CanAdopt(c, n, p, target, region, agenda, out _)) return false;
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

    public static bool GoalMet(Campaign c, Nation n, PoliticalAgenda a) => a.Goal switch
    {
        "living" => n.StandardOfLiving >= a.GoalValue,
        "debt_ratio" => n.PublicDebt / MathF.Max(.01f, n.Gdp) <= a.GoalValue && n.DebtCondition < DebtCondition.Default,
        "peace" => !c.Wars.Any(w => !w.Ended && w.Involves(n.Id)),
        _ => n.Stability >= a.GoalValue
    };

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
                    n.Stability < .50f ? "security_community" : n.Ideology == "liberalism" ? "rights_association" : "education_technical";
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
