using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>Events buy programmes, not changes the player can undo with a budget slider.</summary>
static class PolicyProgrammeTests
{
    private static readonly string[] Programmes = {
        "revenue_compliance_audit", "procurement_rescheduling", "investment_concessions",
        "research_retention_programme", "classified_research_programme", "frontline_relief_programme",
        "minority_government_compact", "civic_mediation_programme", "border_reserve_programme",
        "market_access_commitments", "defence_arsenal_consolidation", "staff_academy_programme", "field_refit_programme"
    };
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("FAIL policy programmes: " + text); Console.WriteLine("PASS policy programmes: " + text); }

    static string Sliders(Nation n) => string.Join("|", new[] { n.TaxRate, n.MilitarySpending,
        n.WelfareSpending, n.EducationSpending }.Select(value => BitConverter.SingleToInt32Bits(value)));

    static bool IsSlider(string key) => new[] { "tax_rate", "military_spending", "welfare_spending", "education_spending", "budget." }
        .Any(slider => key.Equals(slider, StringComparison.OrdinalIgnoreCase) || key.StartsWith(slider + "_", StringComparison.OrdinalIgnoreCase) ||
            slider == "budget." && key.StartsWith(slider, StringComparison.OrdinalIgnoreCase));

    static void NoSliders(ScriptedEffects.Effect effect, string origin, HashSet<string> visited = null)
    {
        if (effect is null) return;
        Check(effect.Entries.All(entry => !IsSlider(entry.Key)), origin + " preserves budget settings");
        visited ??= new(StringComparer.Ordinal);
        foreach (var (id, _) in effect.Grants)
        {
            var modifier = ModifierCatalog.Get(id);
            Check(modifier is not null, origin + " names a real programme: " + id);
            if (modifier is not null && visited.Add(id)) NoSliders(modifier.Effects, origin + "/" + id, visited);
        }
    }

    static (Campaign Campaign, Nation Nation) World()
    {
        var c = new Campaign(20261026);
        var n = c.AddNation(new Nation { Tag = "AAA", Name = "Programme fixture", MapIndex = 501,
            Gdp = 100, Treasury = 100, Population = 1_000_000, Stability = .6f, OutputScale = 1,
            TaxRate = .23f, MilitarySpending = .031f, WelfareSpending = .043f, EducationSpending = .017f,
            ArmyExperience = 100, PoliticalPoints = 100 });
        var other = c.AddNation(new Nation { Tag = "BBB", Name = "Other", MapIndex = 502,
            Gdp = 100, Population = 1_000_000, OutputScale = 1 });
        c.States.Add(new ScenarioState { Id = "programme_a", MapIndex = 1001, NationId = "AAA",
            NationMapIndex = 501, Population = 1_000_000 });
        c.States.Add(new ScenarioState { Id = "programme_b", MapIndex = 1002, NationId = "BBB",
            NationMapIndex = 502, Population = 1_000_000 });
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1001, 1002, 1001, 1002 },
            new ushort[] { 501, 502, 501, 502 }, 2, 2, _ => false));
        c.Wars.Add(new StrategicWar { AttackerId = n.Id, DefenderId = other.Id });
        c.SetPlayer(n.Id);
        SovereignFinance.NormalizeAccounts(c);
        EconomySystem.Explain(c);
        return (c, n);
    }

    public static void Run()
    {
        foreach (var d in DecisionCatalog.All) NoSliders(d.Effect, "decision " + d.Id);
        foreach (var e in EventCatalog.All)
        {
            NoSliders(e.Effects, "event " + e.Id);
            foreach (var option in e.Options)
            {
                NoSliders(option.Effect, "option " + e.Id + "/" + option.Title);
                foreach (var (id, _) in option.Modifiers)
                    NoSliders(ModifierCatalog.Get(id)?.Effects, "option spirit " + e.Id + "/" + id);
            }
        }
        foreach (var id in Programmes)
        {
            var d = ModifierCatalog.Get(id);
            Check(d is { Days: > 0 and <= 365, Modifiers.Count: > 0 }, id + " has a bounded duration and real modifiers");
            Check(d.Modifiers.All(pair => NationalModifiers.Knows(pair.Key) && float.IsFinite(pair.Value)),
                id + " uses implemented finite simulation effects");
            foreach (var language in new[] { "english", "russian" })
                Check(Loc.Keys(language).Contains(d.Name) && Loc.Keys(language).Contains(d.Description),
                    id + " has name and explanation in " + language);
        }
        CommandsAndSaves();
        ExactExpiry();
    }

    static void CommandsAndSaves()
    {
        var (c, n) = World();
        var sliders = Sliders(n);
        var time = c.ElapsedHours;
        foreach (var (decision, programme) in new[] {
            ("anti_corruption", "revenue_compliance_audit"),
            ("defence_industry_consolidation", "defence_arsenal_consolidation"),
            ("general_staff_academy_expansion", "staff_academy_programme"),
            ("armament_modernization_field_refit", "field_refit_programme") })
        {
            var definition = DecisionCatalog.Find(decision);
            Check(definition is not null, decision + " exists");
            var cash = n.Treasury;
            var reason = DecisionCatalog.BlockReason(c, n, definition.Value);
            Check(reason.Length == 0, decision + " has a payable authoritative command: " + reason);
            c.Commands.Enqueue(new TakeDecisionCommand { IssuerId = n.Id, DecisionId = decision });
            c.Commands.ApplyAll(c);
            Check(n.Treasury < cash && ModifierSystem.Has(n, programme), decision + " pays and installs its programme on pause");
            Check(Sliders(n) == sliders && c.ElapsedHours == time && c.IsPaused, decision + " does not move sliders or time");
            var fingerprint = CampaignFingerprint.Of(c);
            new TakeDecisionCommand { IssuerId = n.Id, DecisionId = decision }.Apply(c);
            Check(CampaignFingerprint.Of(c) == fingerprint, decision + " cannot be purchased again during cooldown");
            _ = ScriptedEffects.PreviewForNation(c, n, definition.Value.Effect);
            _ = ScriptedEffects.Lines(definition.Value.Effect);
            Check(CampaignFingerprint.Of(c) == fingerprint, decision + " preview is read-only");
        }
        var archive = SaveSystem.ExportNetworkSnapshot(c, "programmes");
        var copy = SaveSystem.ImportNetworkSnapshot(archive, "programmes");
        Check(copy.Success, "programme state loads through the existing save/network format: " + copy.Warning);
        copy.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(CampaignFingerprint.Of(copy.Campaign) == CampaignFingerprint.Of(c), "save roundtrip preserves programme dates, payments and cooldowns");

        var eventDefinition = EventCatalog.All.Single(e => e.Id == "budget_crisis");
        foreach (var option in eventDefinition.Options)
        {
            var pending = new PendingCampaignEvent { Id = 900 + c.PendingEvents.Count, NationId = n.Id,
                ExpiresDay = 30, Options = new[] { option } };
            c.PendingEvents.Add(pending);
            c.Commands.Enqueue(new ResolveCampaignEventCommand { IssuerId = n.Id, EventId = pending.Id, OptionIndex = 0 });
            c.Commands.ApplyAll(c);
            Check(!c.PendingEvents.Contains(pending) && Sliders(n) == sliders, option.Title + " resolves without overwriting budgets");
        }
        var audit = NationalModifiers.Of(n, "tax_efficiency");
        Check(audit > 0, "collection can improve without raising the headline tax rate");
        ModifierSystem.Grant(c, n, "revenue_compliance_audit");
        Check(n.Modifiers.Count(m => m.Id == "revenue_compliance_audit") == 1 &&
            NationalModifiers.Of(n, "tax_efficiency") == audit, "programmes refresh, never stack duplicate bonuses");
    }

    static void ExactExpiry()
    {
        var (c, n) = World();
        var sliders = Sliders(n);
        var original = NationalModifiers.Of(n, "reinforcement_rate");
        ModifierSystem.Grant(c, n, "field_refit_programme", 2);
        Check(NationalModifiers.Of(n, "reinforcement_rate") > original, "refit affects the existing replacement calculation");
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 24L);
        ModifierSystem.Tick(c);
        Check(ModifierSystem.Has(n, "field_refit_programme"), "programme remains on the day before expiry");
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 48L);
        ModifierSystem.Tick(c);
        Check(!ModifierSystem.Has(n, "field_refit_programme") && NationalModifiers.Of(n, "reinforcement_rate") == original,
            "programme and cached bonus end on their exact day, even between economic settlements");
        Check(Sliders(n) == sliders, "programme expiry never resets the player's budgets");
    }
}
