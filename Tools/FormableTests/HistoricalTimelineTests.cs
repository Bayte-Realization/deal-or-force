using System;
using System.Collections.Generic;
using System.IO;
using DealOrForce.Simulation;

/// <summary>
/// Dated history is content, not a tick: a file says when, a trigger says who, and the
/// ordinary event/effect vocabulary says what changes. These checks keep that boundary
/// honest before any historical pack exists.
/// </summary>
static class HistoricalTimelineTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("historical timeline: " + label); Console.WriteLine("PASS historical timeline: " + label); }

    public static void Run()
    {
        EventCatalog.ResetCatalog();
        var shipped = 0;
        foreach (var definition in EventCatalog.All)
            if (definition.Id == "french_revolution_1789" && definition.Historical) shipped++;
        Check(shipped == 1, "the shipped dated pack is loaded from events/*.json");
        Check(CountHistorical() >= 15, "the long-century pack contributes its dated events");
        var datedPath = GameDataLoader.FindDataFile("events", "60_dated_history.json");
        var diagnostics = new List<string>();
        if (datedPath is not null) EventCatalog.Load(File.ReadAllText(datedPath), diagnostics);
        Check(datedPath is not null && diagnostics.Count == 0,
            "the shipped dated pack has no unknown effects or triggers: " + string.Join("; ", diagnostics));

        var campaign = Fixture(out var nation);
        var auto = Fixture(out var autoNation);
        HistoricalTimeline.Tick(auto);
        Check(autoNation.EventMemory.ContainsKey("history:year_of_elections_2024"),
            "the daily tick fires a shipped dated event on the campaign date");

        var revolution = new CampaignEventDefinition
        {
            Id = "fixture_revolution",
            Title = "A fixture revolution",
            Description = "A dated event with no choice.",
            HistoricalDate = new HistoricalDate(1789, 7, 14),
            WindowDays = 2,
            Effects = ScriptedEffects.From(("stability", -0.1f)),
            Record = false
        };

        Check(HistoricalTimeline.Due(revolution, new HistoricalDate(1789, 7, 14)),
            "the event fires on its date");
        Check(HistoricalTimeline.Due(revolution, new HistoricalDate(1789, 7, 15)),
            "a country that missed the exact day still sees it inside the window");
        Check(!HistoricalTimeline.Due(revolution, new HistoricalDate(1789, 7, 17)),
            "the window closes rather than firing centuries late");
        Check(HistoricalDate.TryParse("-0300-03-15", out var bce) && bce.Year == -300 &&
              bce.Month == 3 && bce.Day == 15,
            "an antiquity date parses without DateOnly's year-one floor");
        Check(HistoricalDate.TryParse("0300-03-15", out var ce) && ce.Year == 300,
            "the same month and day in the common era stay a different year");
        Check(bce.DayNumber < ce.DayNumber,
            "the antiquity date sorts before the common-era date");
        var antiquity = new Campaign(4401, new Defines { StartYear = -300, StartMonth = 1, StartDay = 1 });
        Check(antiquity.Date.Year == -300 && antiquity.Date.Month == 1 && antiquity.Date.Day == 1,
            "the campaign clock can open before year one");
        Check(HistoricalTimeline.Ready(campaign, nation, revolution),
            "a country with no memory of the event is ready");

        var before = nation.Stability;
        Check(HistoricalTimeline.Fire(campaign, nation, revolution),
            "firing a dated event reaches the ordinary effect path");
        Check(Math.Abs(nation.Stability - (before - 0.1f)) < 0.0001f,
            "the effect changes the authoritative country, not a preview");
        Check(!HistoricalTimeline.Ready(campaign, nation, revolution),
            "a fired one-shot is remembered in the saved country memory");
        Check(!HistoricalTimeline.Fire(campaign, nation, revolution) ||
              nation.EventMemory.ContainsKey("history:fixture_revolution"),
            "the memory key is stable across the save boundary");

        var choice = new CampaignEventDefinition
        {
            Id = "fixture_choice",
            Title = "A fixture choice",
            Description = "A dated event with two answers.",
            HistoricalDate = new HistoricalDate(1900, 1, 1),
            WindowDays = 0,
            Record = false,
            AiOption = 1,
            Options = new[]
            {
                new CampaignEventOption { Title = "Reform", Effect = ScriptedEffects.From(("stability", 0.03f)) },
                new CampaignEventOption { Title = "Resist", Effect = ScriptedEffects.From(("stability", -0.03f)) }
            }
        };
        var ai = Fixture(out var aiNation);
        aiNation.IsHuman = false;
        HistoricalTimeline.Fire(ai, aiNation, choice);
        Check(ai.PendingEvents.Count == 0, "an AI choice is resolved immediately rather than left waiting");
        Check(Math.Abs(aiNation.Stability - (0.6f - 0.03f)) < 0.0001f,
            "the AI takes the documented option, not the first button");

        var player = Fixture(out var playerNation);
        HistoricalTimeline.Fire(player, playerNation, choice);
        Check(player.PendingEvents.Count == 1, "a human choice is a real pending event");
        Check(player.PendingEvents[0].Options.Length == 2, "the player receives both authored answers");
    }

    static int CountHistorical()
    {
        var count = 0;
        foreach (var definition in EventCatalog.All)
            if (definition.Historical) count++;
        return count;
    }

    static Campaign Fixture(out Nation nation)
    {
        var campaign = new Campaign(9127) { ScenarioId = "historical_fixture" };
        nation = campaign.AddNation(new Nation
        {
            Tag = "FRA", MapIndex = 501, Population = 10_000_000, Gdp = 100, Treasury = 50
        });
        campaign.ConfigureSinglePlayer(nation.Id);
        nation.Stability = 0.6f;
        return campaign;
    }
}
