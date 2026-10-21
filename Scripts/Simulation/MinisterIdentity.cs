using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public int PoliticianCandidateCohortYears { get; set; } = 5;
    /// <summary>Retained as a mod-facing alias: a minister and a ruler end at the same line.</summary>
    public int RulerMaximumAge
    {
        get => RulerMaxAge;
        set => RulerMaxAge = value;
    }
}

/// <summary>The person appointed to an existing portfolio, not another cabinet/effect system.</summary>
public sealed class MinisterPerson
{
    public string Name = "";
    public int BirthYear;
    public string TraitId = "";
    public int AppointedDay;
}

public sealed partial class Nation
{
    // Missing in old saves: the existing deterministic minister name remains the fallback.
    public Dictionary<string, MinisterPerson> CabinetPeople = new(StringComparer.OrdinalIgnoreCase);
}

public static class PoliticalAppointments
{
    public static bool MayPromoteMinister(Campaign c, Nation n, string portfolio, out float price, out string refusal)
    {
        price = 0;
        if (c is null || n is null) { refusal = "No country selected."; return false; }
        if (!c.Defines.RulersEnabled || !c.FeatureEnabled("rulers"))
        { refusal = "This mechanic is disabled in this scenario."; return false; }
        if (!n.Cabinet.TryGetValue(portfolio, out var option) || Rulers.ActiveMinister(n, portfolio) is null)
        { refusal = "Unknown minister candidate."; return false; }
        if (!Rulers.CanAppointMinister(c, n, portfolio, option, out refusal, allowSameOption: true)) return false;
        price = Rulers.CabinetAppointmentCost(c, n) * 1.5f;
        if (!float.IsFinite(price) || !float.IsFinite(n.Treasury) || price < 0 || n.Treasury < price)
        { refusal = "Insufficient treasury for this appointment."; return false; }
        refusal = ""; return true;
    }

    public static bool MayRecruit(Campaign c, Nation n, int slot, bool asLeader,
        out Rulers.PoliticianCandidate candidate, out float price, out string refusal)
    {
        candidate = default; price = 0;
        if (c is null || n is null) { refusal = "No country selected."; return false; }
        if (!c.Defines.RulersEnabled || !c.FeatureEnabled("rulers"))
        { refusal = "This mechanic is disabled in this scenario."; return false; }
        if (slot < 0 || slot >= 32) { refusal = "Unknown minister candidate."; return false; }
        var pool = Rulers.CandidatePool(c, n, Math.Max(4, slot + 1));
        if (slot >= pool.Count) { refusal = "Unknown minister candidate."; return false; }
        candidate = pool[slot];
        if (!Rulers.CanAppointMinister(c, n, candidate.PortfolioId, candidate.OptionId, out refusal, allowSameOption: asLeader)) return false;
        price = Rulers.CabinetAppointmentCost(c, n) * (asLeader ? 1.5f : 1f);
        if (!float.IsFinite(price) || !float.IsFinite(n.Treasury) || price < 0 || n.Treasury < price)
        { refusal = "Insufficient treasury for this appointment."; return false; }
        refusal = ""; return true;
    }

    /// <summary>Called only by the existing political tick, never while drawing an officeholder.</summary>
    public static void ReviewMinisters(Campaign c, Nation n, int reviewDays)
    {
        if (c is null || n is null || !c.Defines.RulersEnabled || !c.FeatureEnabled("rulers")) return;
        // Older saves stored only a policy choice per portfolio. Adopt its existing
        // displayed identity once, on a simulation review, never while inspecting UI.
        foreach (var portfolio in Rulers.AllPortfolios)
        {
            if (n.CabinetPeople.ContainsKey(portfolio.Id)) continue;
            var authM = Rulers.AuthoredMinister(n.Tag, portfolio.Id);
            if (!n.Cabinet.ContainsKey(portfolio.Id))
            {
                if (c.ElapsedDays == 0 && n.LastCabinetChangeDay == -9999 && authM is not null && !string.IsNullOrEmpty(authM.Name))
                {
                    n.Cabinet[portfolio.Id] = !string.IsNullOrEmpty(authM.Option) ? authM.Option
                        : (portfolio.Options.Count > 0 ? portfolio.Options[0].Id : "");
                    n.CabinetPeople[portfolio.Id] = new MinisterPerson
                    {
                        Name = authM.Name,
                        BirthYear = c.Date.Year - Math.Clamp(authM.Age, 25, 95),
                        TraitId = authM.Trait,
                        AppointedDay = 0
                    };
                    c.MarkStrategicChange();
                }
                continue;
            }
            var name = Rulers.MinisterPersonName(n, portfolio.Id);
            var age = authM is not null ? authM.Age : Rulers.GeneratedMinisterAge(n, portfolio.Id);
            n.CabinetPeople[portfolio.Id] = new MinisterPerson
            {
                Name = name,
                BirthYear = c.Date.Year - Math.Clamp(age, 25, 95),
                TraitId = authM?.Trait ?? "",
                AppointedDay = n.LastCabinetChangeDay
            };
            c.MarkStrategicChange();
        }
        foreach (var entry in n.CabinetPeople.ToArray())
        {
            if (!n.Cabinet.ContainsKey(entry.Key)) { n.CabinetPeople.Remove(entry.Key); continue; }
            var person = entry.Value;
            if (person is null) continue;
            var age = Math.Max(0, c.Date.Year - person.BirthYear);
            if (age < c.Defines.RulerMinMortalityAge) continue;
            var annual = Math.Clamp(c.Defines.RulerAnnualMortalityAtMinAge +
                (age - c.Defines.RulerMinMortalityAge) * c.Defines.RulerAnnualMortalityPerYearOver, 0, 1);
            var chance = Math.Clamp(annual * Math.Max(1, reviewDays) / 365f, 0, 1);
            var roll = Escalation.Roll(c.Seed, n.Id, Rulers.StableHash(entry.Key, person.BirthYear), c.ElapsedDays, 0x71D);
            if (age < Math.Max(1, c.Defines.RulerMaximumAge) && roll >= chance) continue;
            n.Cabinet.Remove(entry.Key); n.CabinetPeople.Remove(entry.Key);
            NationalModifiers.Invalidate(n); c.MarkStrategicChange();
            c.Chronicle.Record(c, "minister_died", n.Id, place: person.Name);
        }
    }
}
