using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class ArmyAssaultPresentationTests
{
    private static void Check(bool ok, string text)
    { if (!ok) throw new Exception(text); Console.WriteLine("PASS assault presentation: " + text); }

    public static void Run()
    {
        var c = new Campaign(83491);
        var army = new Army { Id = 4, NationId = 0, OrderKind = ArmyOrder.AdvanceFront,
            PlanActive = true, Organisation = .7f, SupplyLevel = .05f };
        c.Armies.Add(army);
        var language = Loc.Language;
        Loc.ResetCatalog();
        try
        {
            Loc.Language = "english";
            var hash = CampaignFingerprint.Of(c);
            var random = c.Random.State;
            var initial = ArmyAssaultPresentation.Description(c, army);
            Check(initial.Contains("Waiting for supply") && initial.Contains("more than"),
                "the actual blocked minimum and strict threshold are explained");
            for (var read = 0; read < 30; read++)
                ArmyAssaultPresentation.Summary(c, new[] { army });
            Check(CampaignFingerprint.Of(c) == hash && c.Random.State == random,
                "reading explanations changes neither authoritative state nor RNG");
            army.SupplyLevel = .8f;
            Check(ArmyAssaultPresentation.Description(c, army).StartsWith("Minimum assault"),
                "a same-day recovery is visible without stale readiness cache");
            army.PlanActive = false;
            Check(ArmyAssaultPresentation.Description(c, army).Contains("paused"),
                "Prepare explains a paused plan rather than predicting a battle");
            army.PlanActive = true; army.Organisation = .1f;
            Check(ArmyAssaultPresentation.Description(c, army).Contains("Waiting for organisation"),
                "organisation is a different actionable reason");
            army.DirectOrder = true;
            Check(ArmyAssaultPresentation.Description(c, army).Contains("explicit order"),
                "direct risky orders are distinguished from autonomous readiness");
            army.ReadyDay = c.ElapsedDays + 3;
            Check(ArmyAssaultPresentation.Description(c, army).Contains("3 day(s)"),
                "a direct order does not hide the actual recovery interval");
            army.BattleId = 8;
            Check(ArmyAssaultPresentation.Description(c, army).StartsWith("Already in battle"),
                "ongoing combat is not called ready for a new assault");
            var waiting = new Army { OrderKind = ArmyOrder.Spearhead, PlanActive = true,
                Organisation = .8f, SupplyLevel = .05f };
            var ready = new Army { OrderKind = ArmyOrder.AdvanceFront, PlanActive = true,
                Organisation = .8f, SupplyLevel = .8f };
            var summary = ArmyAssaultPresentation.Summary(c, new[] { army, waiting, ready });
            Check(summary.Contains("1 of 3") && summary.Contains("Already fighting: 1") &&
                summary.Contains("Waiting for supply: 1"), "mixed selections expose counted live causes");
            Check(ArmyAssaultPresentation.Summary(c, Array.Empty<Army>()).StartsWith("No current formation"),
                "an empty selection has no invented readiness");
            Loc.Language = "russian";
            Check(ArmyAssaultPresentation.Description(c, waiting).StartsWith("Ожидание снабжения") &&
                ArmyAssaultPresentation.Summary(c, new[] { army, waiting, ready }).Contains("из 3"),
                "the card and grouped causes resolve in Russian");
            using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(),
                "localization", "russian", "180_assault_readiness.json")));
            foreach (var entry in source.RootElement.EnumerateObject())
            {
                Check(Loc.Get(entry.Name) != entry.Name && !string.IsNullOrWhiteSpace(Loc.TextIn("english", entry.Name)) &&
                    Loc.Placeholders(entry.Name).SetEquals(Loc.Placeholders(entry.Value.GetString())),
                    "localized key + placeholders: " + entry.Name);
            }
        }
        finally { Loc.Language = language; }
    }
}
