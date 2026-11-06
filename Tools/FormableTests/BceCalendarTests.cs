using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

internal static class BceCalendarTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("FAIL BCE calendar: " + message); Console.WriteLine("PASS BCE calendar: " + message); }

    internal static void Run()
    {
        foreach (var year in new[] { -9999, -3499, -400, -100, -44, -4, -1, 0, 1, 4, 100, 400, 1900, 2000, 2024, 9999 })
        for (var month = 1; month <= 12; month++)
        {
            var start = new HistoricalDate(year, month, 1);
            var end = new HistoricalDate(year, month, HistoricalDate.DaysInMonth(year, month));
            Check(HistoricalDate.FromDayNumber(start.DayNumber) == start && HistoricalDate.FromDayNumber(end.DayNumber) == end,
                "day-number round trip " + start);
            if (year >= 1)
                Check((new DateTime(year, month, 1) - new DateTime(1970, 1, 1)).Days == start.DayNumber,
                    "positive-year arithmetic agrees with the existing Gregorian calendar " + start);
        }
        Check(HistoricalDate.TryParse("0300-03-15 BCE", out var named) && named.Year == -299 &&
            HistoricalDate.TryParse("-0300-03-15", out var legacy) && legacy.Year == -300,
            "civil BCE suffix and legacy astronomical signed dates are unambiguous and compatible");
        Check(!HistoricalDate.TryParse("0000-01-01 BCE", out _) && !HistoricalDate.TryParse("-0044-03-15 BCE", out _) &&
            !HistoricalDate.TryParse("0300-02-30 BCE", out _), "no civil year zero, contradictory sign or invalid February");
        var date = new GameDate(0, 12, 31) { Hour = 23 }; date.Advance(1);
        Check(date.Year == 1 && date.Month == 1 && date.Day == 1 && date.Hour == 0,
            "1 BCE rolls directly into 1 CE without displaying a year zero");
        var eraEnd = new HistoricalDate(0, 12, 31);
        Check(eraEnd.AddDays(1) == new HistoricalDate(1, 1, 1) && eraEnd.AddDays(1).AddDays(-1) == eraEnd,
            "dated events and agreements cross the era boundary in both directions");
        var previousLanguage = Loc.Language;
        try
        {
            Loc.Language = "english";
            Check(new GameDate(-3499, 1, 1).Display().Contains("3500 BCE", StringComparison.Ordinal), "English Bronze Age display");
            Loc.Language = "russian";
            Check(new GameDate(-3499, 1, 1).Display().Contains("3500 до н. э.", StringComparison.Ordinal), "Russian Bronze Age display");
        }
        finally { Loc.Language = previousLanguage; }

        var c = ExclaveSupplyTests.Rack(); var original = CampaignFingerprint.Of(c);
        var draft = new ScenarioMapDraft(SaveSystem.ExportScenarioJson(c, "bronze_fixture"), c.ProvinceMap.ProvinceCount, "bronze_fixture");
        draft.UpdateSettings(draft.Settings with { Name = "Bronze fixture", Year = -3499, Month = 2, Day = 28, Hour = 23 });
        var json = draft.Export();
        var imported = SaveSystem.ImportScenarioJson(json, "bronze_fixture", c.ProvinceMap.ProvinceCount);
        Check(imported.Date.Year == -3499 && imported.Date.Hour == 23 && CampaignFingerprint.Of(c) == original,
            "scenario editor exports/imports a Bronze Age clock without touching the live world");
        draft.Undo(); Check(draft.Settings.Year == 2024, "era/date change participates in normal editor undo");
        draft.Redo(); Check(draft.Settings.Year == -3499, "editor redo restores the BCE date");
        var folder = Path.Combine(Path.GetTempPath(), "dof-bce-" + Guid.NewGuid().ToString("N"));
        try
        {
            imported.AttachProvinceTopology(c.ProvinceMap);
            SaveSystem.Save(folder, "bronze", imported, "");
            var loaded = SaveSystem.Load(folder, "bronze", "", c.ProvinceMap.ProvinceCount).Campaign;
            Check(loaded is not null && loaded.Date.Year == -3499 && loaded.Date.Hour == 23,
                "ordinary compressed save/load preserves the ancient calendar");
            loaded.Date.Advance(24 * 365);
            Check(loaded.Date.Year == -3498, "loaded ancient calendar advances for a full year");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        var nation = imported.Nations[0]; nation.HeadOfStateName = "Calendar fixture ruler";
        nation.HeadOfStateBirthYear = -3549;
        Check(Rulers.HeadOfState(imported, nation).Age == 50, "negative ruler birth year is a real date, not a missing-year sentinel");
        Check(LandArrangementSystem.DateOfDay(imported, 365).Contains("BCE", StringComparison.Ordinal),
            "land agreement date captions no longer throw DateTime's year-one exception");
        var eventDefinition = new CampaignEventDefinition { Id = "fixture_bronze", HistoricalDate = new HistoricalDate(-3499, 2, 28), WindowDays = 0 };
        Check(HistoricalTimeline.Due(eventDefinition, new HistoricalDate(imported.Date.Year, imported.Date.Month, imported.Date.Day)),
            "historical event windows use the same ancient day numbers");
    }
}
