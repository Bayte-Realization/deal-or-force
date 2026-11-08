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
        c.Nations.Add(new Nation { Id = 0, Tag = "MAN", Name = "Manual command" });
        c.Nations.Add(new Nation { Id = 1, Tag = "AUT", Name = "Automatic command" });
        c.ConfigureSinglePlayer(0);
        army.NationId = c.Player.Id;
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
            Check(ArmyAssaultPresentation.Description(c, army).Contains("Waiting for morale"),
                "morale is a different actionable reason, with the legacy simulation field unchanged");
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

            var marching = new Army { NationId = c.Player.Id, OrderKind = ArmyOrder.AdvanceFront,
                PlanActive = true, Organisation = .8f, SupplyLevel = .8f };
            marching.Route.AddRange(new[] { 2, 3 });
            var support = new Army { NationId = c.Player.Id, OrderKind = ArmyOrder.Spearhead,
                PlanActive = true, SupportOnly = true, Organisation = .8f, SupplyLevel = .8f };
            var afloat = new Army { NationId = c.Player.Id, OrderKind = ArmyOrder.AdvanceFront,
                PlanActive = true, EmbarkedFleetId = 7, Organisation = .8f, SupplyLevel = .8f };
            Check(ArmyAssaultPresentation.Description(c, marching).Contains("2 route step(s)") &&
                ArmyAssaultPresentation.Description(c, support).StartsWith("Support fire only") &&
                ArmyAssaultPresentation.Description(c, afloat).StartsWith("Embarked"),
                "movement, support fire and transport are not misreported as ready but idle");
            var activity = ArmyAssaultPresentation.Summary(c, new[] { marching, support, afloat, ready });
            Check(activity.Contains("1 of 4") && activity.Contains("Marching: 1") &&
                activity.Contains("Embarked: 1") && activity.Contains("Support fire only: 1"),
                "grouped readiness counts only actual assault candidates, once per formation");
            var computer = new Army { NationId = c.Nations.First(n => !n.IsHuman).Id,
                OrderKind = ArmyOrder.AdvanceFront, Planning = .42f, PlanActive = false };
            Check(ArmyAssaultPresentation.Description(c, computer).Contains("AI has not started") &&
                ArmyAssaultPresentation.Description(c, computer).Contains("42"),
                "an AI preparing a plan reports actual planning instead of telling the player to execute it");
            computer.NationId = c.Player.Id;
            Check(ArmyAssaultPresentation.Description(c, computer).Contains("paused"),
                "an undelegated human plan keeps the manual explanation");
            Delegation.Set(c.Player, Delegation.Command, true);
            Check(ArmyAssaultPresentation.Description(c, computer).Contains("AI has not started"),
                "a delegated human command uses the actual automatic authority");
            Delegation.Set(c.Player, Delegation.Command, false);
            c.Armies.AddRange(new[] { marching, support, afloat, computer });
            hash = CampaignFingerprint.Of(c); random = c.Random.State;
            for (var read = 0; read < 30; read++)
            {
                ArmyAssaultPresentation.Summary(c, c.Armies);
                foreach (var formation in c.Armies) ArmyAssaultPresentation.Description(c, formation);
            }
            Check(CampaignFingerprint.Of(c) == hash && c.Random.State == random,
                "activity explanations alter neither orders, authority, routes nor RNG");
            Loc.Language = "russian";
            Check(ArmyAssaultPresentation.Description(c, waiting).StartsWith("Ожидание снабжения") &&
                ArmyAssaultPresentation.Summary(c, new[] { army, waiting, ready }).Contains("из 3"),
                "the card and grouped causes resolve in Russian");
            Check(ArmyAssaultPresentation.Description(c, marching).StartsWith("Приказ на марш") &&
                ArmyAssaultPresentation.Summary(c, new[] { marching, support, afloat, ready }).Contains("Только огневая поддержка: 1"),
                "new activity reasons and group counts resolve in Russian");
            foreach (var file in new[] { "180_assault_readiness.json", "182_army_activity.json" })
            {
                using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(),
                    "localization", "russian", file)));
                foreach (var entry in source.RootElement.EnumerateObject())
                {
                    Check(Loc.Get(entry.Name) != entry.Name && !string.IsNullOrWhiteSpace(Loc.TextIn("english", entry.Name)) &&
                        Loc.Placeholders(entry.Name).SetEquals(Loc.Placeholders(entry.Value.GetString())),
                        "localized key + placeholders: " + entry.Name);
                }
            }
        }
        finally { Loc.Language = language; }
    }
}
