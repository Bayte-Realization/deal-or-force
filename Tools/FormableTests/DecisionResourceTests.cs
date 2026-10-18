using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class DecisionResourceTests
{
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("decision resources: " + text); Console.WriteLine("PASS decision resources: " + text); }

    static bool Overlay(string id, string json, List<string> errors = null)
    {
        using var body = JsonDocument.Parse(json);
        return DecisionCatalog.Apply(id, body.RootElement, errors);
    }

    public static void Run()
    {
        var language = Loc.Language;
        try
        {
            var c = new Campaign(20261019);
            var n = c.AddNation(new Nation { Tag = "AAA", Name = "A", MapIndex = 501, Gdp = 100, Treasury = 100,
                Population = 1_000_000, Stability = .8f, OutputScale = 1 });
            c.AddNation(new Nation { Tag = "BBB", Name = "B", MapIndex = 502, Gdp = 100, Population = 1_000_000, OutputScale = 1 });
            c.States.Add(new ScenarioState { Id = "resource_a", MapIndex = 1001, NationId = "AAA", NationMapIndex = 501, Population = 1_000_000 });
            c.States.Add(new ScenarioState { Id = "resource_b", MapIndex = 1002, NationId = "BBB", NationMapIndex = 502, Population = 1_000_000 });
            c.InvalidateStateIndex();
            c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1001, 1002, 1001, 1002 },
                new ushort[] { 501, 502, 501, 502 }, 2, 2, _ => false));
            c.SetPlayer(n.Id);
            // A current campaign has opened books. Empty constructor ledgers are a
            // legacy-save sentinel and intentionally rebuilt by the loader.
            SovereignFinance.NormalizeAccounts(c);
            EconomySystem.Explain(c);
            Check(Overlay("currency_fixture", """
                {"name":"Currency fixture","cooldown_days":10,"gdp_cost_share":0.001,
                 "trigger":{"min_stability":0},"resource_costs":{"army_experience":10,"air_experience":20,"naval_experience":30,"political_points":40},
                 "effects":{"stability":0.01}}
                """), "a mod declares all four resource prices without a compiled decision kind");
            var d = DecisionCatalog.Find("currency_fixture").Value;
            Check(d.ResourceCosts.Count == 4 && d.ResourceCosts[0].Currency == "army_experience" &&
                d.ResourceCosts[3].Currency == "political_points", "resource display and execution have a stable canonical order");
            n.ArmyExperience = 9; n.AirExperience = 20; n.NavalExperience = 30; n.PoliticalPoints = 40;
            var fingerprint = CampaignFingerprint.Of(c);
            var reason = DecisionCatalog.BlockReason(c, n, d);
            Check(reason.Contains("10") && reason.Contains("9") && DecisionCatalog.IsBlocked(c, n, d),
                "refusal states the missing currency, required amount and actual balance");
            new TakeDecisionCommand { IssuerId = n.Id, DecisionId = d.Id }.Apply(c);
            Check(CampaignFingerprint.Of(c) == fingerprint, "a missing resource spends no cash, other currency, cooldown or effect");
            n.ArmyExperience = 10;
            fingerprint = CampaignFingerprint.Of(c);
            var price = DecisionResources.PriceText(n, d);
            Check(price.Contains("10") && price.Contains("20") && price.Contains("30") && price.Contains("40") &&
                CampaignFingerprint.Of(c) == fingerprint, "price preview shows every declared resource and never changes campaign state");
            var cash = n.Treasury;
            new TakeDecisionCommand { IssuerId = n.Id, DecisionId = d.Id }.Apply(c);
            Check(n.ArmyExperience == 0 && n.AirExperience == 0 && n.NavalExperience == 0 && n.PoliticalPoints == 0 &&
                MathF.Abs(n.Treasury - (cash - DecisionCatalog.Cost(n, d))) < .0001f,
                "one authoritative decision charges money and all four balances exactly once");
            Check(n.Stability > .8f && n.DecisionAvailableDay[d.Id] == 10,
                "the paid decision applies its effect and ordinary cooldown");
            fingerprint = CampaignFingerprint.Of(c);
            new TakeDecisionCommand { IssuerId = n.Id, DecisionId = d.Id }.Apply(c);
            Check(CampaignFingerprint.Of(c) == fingerprint, "replaying a cooling-down command cannot charge twice");
            n.ArmyExperience = 12.5f; n.AirExperience = 23; n.NavalExperience = 34; n.PoliticalPoints = 45;
            fingerprint = CampaignFingerprint.Of(c);
            var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "resources"), "resources");
            Check(saved.Success, "resource expenditure and cooldown survive an existing snapshot: " + saved.Warning);
            saved.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
            if (CampaignFingerprint.Of(saved.Campaign) != fingerprint)
                foreach (var original in c.Nations)
                foreach (var field in typeof(Nation).GetFields().Where(f => f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string)))
                {
                    var copy = saved.Campaign.NationById(original.Id);
                    if (!Equals(field.GetValue(original), field.GetValue(copy)))
                        Console.WriteLine("RESOURCE_SNAPSHOT_DIFF " + original.Tag + "." + field.Name + " " + field.GetValue(original) + " -> " + field.GetValue(copy));
                }
            Check(CampaignFingerprint.Of(saved.Campaign) == fingerprint,
                "no new save fields or peer divergence are needed for explicit resource prices");

            Check(Overlay(d.Id, """{"name":"Renamed fixture"}""") &&
                DecisionCatalog.Find(d.Id).Value.ResourceCosts.Count == 4, "caption-only mod overlay preserves the complete price");
            var old = DecisionCatalog.Find(d.Id).Value;
            foreach (var bad in new[] { """{"army_experience":-1}""", """{"unknown":1}""",
                """{"air_experience":"NaN"}""", """{"naval_experience":1e309}""",
                """{"army_experience":5,"army_experience":7}""", "[]", """{"political_points":100001}""" })
            {
                var errors = new List<string>();
                var applied = Overlay(d.Id, "{\"name\":\"Corrupt\",\"resource_costs\":" + bad + "}", errors);
                Check(!applied && errors.Count == 1 && DecisionCatalog.Find(d.Id).Value.Name == old.Name &&
                    ReferenceEquals(DecisionCatalog.Find(d.Id).Value.ResourceCosts, old.ResourceCosts),
                    "bad price is rejected atomically: " + bad);
            }
            Check(Overlay(d.Id, """{"resource_costs":{"army_experience":5}}""") &&
                DecisionCatalog.Find(d.Id).Value.ResourceCosts.Count == 1, "resource object replaces the vector rather than retaining a hidden old tax");
            Check(Overlay(d.Id, """{"resource_costs":{}}""") &&
                DecisionResources.Of(DecisionCatalog.Find(d.Id).Value).Count == 0, "an empty resource object explicitly removes the price");
            var anti = DecisionCatalog.Find("anti_corruption").Value;
            Check(Overlay(anti.Id, """{"resource_costs":{"political_points":12}}""") &&
                DecisionCatalog.Find(anti.Id).Value.Kind == DecisionKind.AntiCorruptionDrive,
                "price-only overlays retain a legacy compiled action instead of silently removing its effect");

            // Isolate the real cabinet selector, not a mock with a different wallet.
            foreach (var item in DecisionCatalog.All.ToArray()) Overlay(item.Id, """{"ai_will_do":""}""");
            Overlay(d.Id, """
                {"resource_costs":{"army_experience":10},"ai_resource_reserve":{"army_experience":5,"naval_experience":8},
                 "ai_will_do":"ai_civil_service_compact"}
                """);
            d = DecisionCatalog.Find(d.Id).Value;
            n.DecisionAvailableDay.Clear(); n.ArmyExperience = 10; n.NavalExperience = 0; n.PoliticalPoints = 120;
            c.Defines.AiDecisionThreshold = .01f;
            Check(DecisionResources.CanAfford(n, d) && !DecisionResources.CanAfford(n, d, true),
                "player price is not inflated by the AI's reserve, including a reserve-only currency");
            fingerprint = CampaignFingerprint.Of(c);
            AiStatecraft.RunTheCabinet(c, n);
            Check(CampaignFingerprint.Of(c) == fingerprint, "the actual cabinet retains its reserve instead of spending the last doctrinal point");
            n.ArmyExperience = 15; n.NavalExperience = 8;
            AiStatecraft.RunTheCabinet(c, n);
            Check(n.ArmyExperience == 5 && n.NavalExperience == 8 && n.DecisionAvailableDay.ContainsKey(d.Id),
                "the actual cabinet pays the ordinary cost and leaves, but does not spend, its reserve");
            Loc.Language = "russian";
            n.ArmyExperience = 0;
            var translated = DecisionResources.Refusal(n, d);
            Check(translated.StartsWith("Недостаточно") && DecisionResources.PriceText(n, d).Contains("доступно"),
                "resource prices and shortages are readable in Russian");
            DecisionCatalog.ResetCatalog();
            Check(DecisionCatalog.Find("push_the_reform").Value.ResourceCosts.Single().Amount == 8 &&
                DecisionCatalog.Find("intensive_field_course").Value.ResourceCosts.Single().Amount == 25,
                "existing political and military courses declare their real payments as prices");
            Check(new[] { "logistics_staff_programme", "joint_air_staff_course", "naval_rotation_programme", "civil_service_compact" }
                .All(id => DecisionCatalog.Find(id) is { } item && item.ResourceCosts.Count > 0 && ModifierCatalog.Get(id) is not null),
                "four new programmes have prices, declared effects and real expiring modifiers");
            var naval = DecisionCatalog.Find("naval_rotation_programme").Value;
            foreach (var item in DecisionCatalog.All.ToArray())
                if (item.Id != naval.Id) Overlay(item.Id, """{"ai_will_do":""}""");
            n.Treasury = 100; n.NavalExperience = 55; n.PoliticalPoints = 25;
            n.DecisionAvailableDay.Clear(); c.Fleets.Clear();
            fingerprint = CampaignFingerprint.Of(c);
            AiStatecraft.RunTheCabinet(c, n);
            Check(CampaignFingerprint.Of(c) == fingerprint,
                "the real naval programme is not bought without serviceable ships even with a full wallet");
            c.Fleets.Add(new Fleet { Id = 91, NationId = n.Id, Hulls = 3 });
            AiStatecraft.RunTheCabinet(c, n);
            Check(n.NavalExperience == 20 && n.PoliticalPoints == 10 && n.Treasury < 100 &&
                n.DecisionAvailableDay.ContainsKey(naval.Id) && ModifierSystem.Has(n, naval.Id),
                "the real cabinet buys the shipped naval programme, pays both currencies and retains the reserve");
            fingerprint = CampaignFingerprint.Of(c);
            AiStatecraft.RunTheCabinet(c, n);
            Check(CampaignFingerprint.Of(c) == fingerprint,
                "a repeated cabinet sitting cannot bypass the real naval programme cooldown or reserve");
        }
        finally { Loc.Language = language; ScriptLibrary.ResetCatalog(); DecisionCatalog.ResetCatalog(); }
    }
}
