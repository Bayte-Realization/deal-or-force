using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// National modifiers: a spirit, a government, an ideology and a cabinet add shares to what the
/// simulation works out, the country's total is their sum, and each key reaches the place it
/// names - construction, training, taxes, research, the weekly drifts - and a decision or an
/// event can hang a spirit on a country.
/// </summary>
static class ModifierTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(20261001);
        c.AttachProvinceTopology(map);
        var germany = c.NationByTag("DEU");
        germany.Modifiers.Clear();
        NationalModifiers.Invalidate(germany);

        // --- the sum ------------------------------------------------------------------------
        var before = NationalModifiers.Of(germany, "construction_speed");
        Check(ModifierSystem.Grant(c, germany, "infrastructure_drive"), "a spirit can be granted");
        var after = NationalModifiers.Of(germany, "construction_speed");
        Check(MathF.Abs(after - before - 0.20f) < 0.0001f,
            $"the infrastructure drive adds a fifth to construction speed ({before:+0.00;-0.00} -> {after:+0.00;-0.00})");
        var sources = NationalModifiers.Sources(germany).Select(source => source.Source).ToList();
        Check(sources.Contains("Infrastructure drive"), "and is listed among the country's sources: " + string.Join(", ", sources));

        // --- construction ------------------------------------------------------------------
        var state = c.StatesOf(germany).OrderByDescending(region => region.Population).First();
        c.Construction.RemoveAll(project => project.NationId == germany.Id);
        germany.Treasury = 10_000f;
        new QueueConstructionCommand { IssuerId = germany.Id, StateMapIndex = state.MapIndex, Building = BuildingKind.CivilianIndustry }.Apply(c);
        var fast = c.Construction.Last(project => project.NationId == germany.Id);
        ModifierSystem.Revoke(germany, "infrastructure_drive");
        new QueueConstructionCommand { IssuerId = germany.Id, StateMapIndex = state.MapIndex, Building = BuildingKind.CivilianIndustry }.Apply(c);
        var slow = c.Construction.Last(project => project.NationId == germany.Id);
        Check(fast.DurationDays < slow.DurationDays,
            $"a factory ordered during the drive takes {fast.DurationDays} days against {slow.DurationDays} without it");

        // --- training --------------------------------------------------------------------
        var template = UnitTypeCatalog.Get("line_infantry");
        var usual = RecruitmentSystem.TrainingDaysOf(germany, template);
        ModifierSystem.Grant(c, germany, "general_mobilisation");
        var mobilised = RecruitmentSystem.TrainingDaysOf(germany, template);
        Check(mobilised < usual, $"general mobilisation trains a division in {mobilised} days against {usual}");
        ModifierSystem.Revoke(germany, "general_mobilisation");

        // --- taxes ---------------------------------------------------------------------------
        var taxes = EconomySystem.WeeklyTaxAt(germany, germany.TaxRate);
        ModifierSystem.Grant(c, germany, "tax_reform");
        var reformed = EconomySystem.WeeklyTaxAt(germany, germany.TaxRate);
        Check(reformed > taxes * 1.05f, $"a tax reform collects {reformed:0.000} against {taxes:0.000} a week");
        ModifierSystem.Revoke(germany, "tax_reform");

        // --- research ---------------------------------------------------------------------
        var research = ResearchSystem.WeeklyPoints(c, germany);
        ModifierSystem.Grant(c, germany, "research_initiative");
        var funded = ResearchSystem.WeeklyPoints(c, germany);
        Check(funded > research * 1.1f, $"a research initiative gives {funded:0.0} points a week against {research:0.0}");
        ModifierSystem.Revoke(germany, "research_initiative");

        // --- a week of martial law ------------------------------------------------------------
        germany.Stability = 0.40f;
        var stable = germany.Stability;
        ModifierSystem.Grant(c, germany, "martial_law");
        for (var day = 0; day < 7; day++) NationalModifiers.Tick(c);
        Check(germany.Stability > stable + 0.002f,
            $"a week of martial law adds {(germany.Stability - stable) * 100f:0.00} points of stability");
        ModifierSystem.Revoke(germany, "martial_law");

        // --- the government's own -----------------------------------------------------------
        var government = GovernmentCatalog.Of(germany.Government);
        Check(government is not null && government.Modifiers.Count > 0,
            $"Germany's government ({germany.Government}) carries modifiers of its own: " +
            string.Join(", ", government?.Modifiers.Select(pair => $"{pair.Key} {pair.Value:+0.00;-0.00}") ?? Array.Empty<string>()));

        // --- a decision that hangs a spirit on the country -----------------------------------
        using (var document = JsonDocument.Parse("{ \"add_modifier\": { \"naval_programme\": 30 }, \"stability\": 0.001 }"))
        {
            var errors = new List<string>();
            var effect = ScriptedEffects.Parse(document.RootElement, "test", errors);
            Check(errors.Count == 0 && effect.Grants.Count == 1, "add_modifier parses: " + string.Join("; ", errors));
            effect.Apply(c, germany);
            var active = germany.Modifiers.FirstOrDefault(item => item.Id == "naval_programme");
            Check(active is not null && active.ExpiresDay == c.ElapsedDays + 30, "and applying it hangs the spirit on the country for the days it says");
            var lines = ScriptedEffects.Lines(effect);
            Check(lines.Any(line => line.Contains("Shipbuilding speed")), "and its tooltip says what the spirit does: " + string.Join(" | ", lines));
        }

        // --- an effect on the whole army, the whole navy and the laboratory -------------------
        var divisions = c.Armies.Where(army => army.NationId == germany.Id).ToList();
        foreach (var army in divisions) army.Organisation = 0.5f;
        germany.CurrentResearch = TechnologyCatalog.All.First(technology => !germany.HasTechnology(technology.Id)).Id;
        germany.ResearchProgress[germany.CurrentResearch] = 0f;
        using (var document = JsonDocument.Parse("{ \"army.organisation\": 0.2, \"research\": 12 }"))
        {
            var errors = new List<string>();
            var effect = ScriptedEffects.Parse(document.RootElement, "test", errors);
            effect.Apply(c, germany);
            Check(errors.Count == 0 && divisions.Count > 0 && divisions.All(army => MathF.Abs(army.Organisation - 0.7f) < 0.001f),
                $"army.organisation lifts every one of Germany's {divisions.Count} divisions to 70 %");
            Check(MathF.Abs(germany.ResearchProgress[germany.CurrentResearch] - 12f) < 0.001f,
                "and research puts points on what the country is researching");
        }

        // --- a typo is an error, not a silent nothing --------------------------------------
        using (var document = JsonDocument.Parse("{ \"constrution_speed\": 0.1 }"))
        {
            var errors = new List<string>();
            var read = NationalModifiers.Read(document.RootElement, "test", errors);
            Check(read.Count == 0 && errors.Count == 1 && errors[0].Contains("construction_speed"),
                "a misspelt modifier is reported with the list of real ones");
        }

        // --- the data the game ships reads cleanly ----------------------------------------
        var shipped = DecisionCatalog.All.Where(decision => decision.Effect is { Grants.Count: > 0 }).Select(decision => decision.Id).ToList();
        Check(shipped.Count >= 8, $"{shipped.Count} decisions hang a spirit on the country: {string.Join(", ", shipped)}");
        Check(ModifierCatalog.All.Count(spirit => spirit.Modifiers.Count > 0) >= 20,
            $"{ModifierCatalog.All.Count(spirit => spirit.Modifiers.Count > 0)} of {ModifierCatalog.All.Count} spirits carry modifiers");
    }
}
