using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// A cut-off grouping spends the stores and the organisation it already has, on a clock of
/// days, and is still there when the clock runs out. Opening the road again clears the
/// clock. Reading the sentence a player sees does not write the campaign.
/// </summary>
public static class SupplyDaysTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL supply days: " + text);
        Console.WriteLine("PASS supply days: " + text);
    }

    /// <summary>
    /// Five provinces in a north-south column, nation 0 at index 0. The map is hung on the
    /// campaign by reflection: AttachProvinceTopology would also paint occupation, ports
    /// and the political overrides, which this clock does not need.
    /// </summary>
    private static (Campaign Campaign, ushort[] Controller) World()
    {
        const int width = 32, height = 160;
        var states = new ushort[width * height];
        var control = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = y * width + x;
                var p = y / 32 + 1;
                states[at] = (ushort)(1000 + p);
                control[at] = 100;
                rgb[at * 3 + 2] = (byte)p;
            }
        var map = ProvinceTopology.FromColorRaster(states, control, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 5).ToDictionary(p => p, p => p));
        var c = new Campaign(1203) { ScenarioId = "supply_days" };
        c.Nations.Add(new Nation
        {
            Id = 0, Tag = "SD0", Name = "Cauldron country", MapIndex = 100, Population = 4000000,
            Gdp = 400, Treasury = 50, MilitarySpending = .06f, StockpilesSeeded = true,
            CapitalProvinceId = 1, CapitalStateMapIndex = 1001, IsHuman = true
        });
        for (var p = 1; p <= 5; p++)
            c.States.Add(new ScenarioState
            {
                Id = "sd_" + p, Name = "Cauldron region " + p, MapIndex = 1000 + p,
                NationMapIndex = 100, NationId = "SD0", Population = 100000
            });
        var controller = (ushort[])map.ProvinceToNation.Clone();
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap)).GetSetMethod(true)
            .Invoke(c, new object[] { map });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController)).GetSetMethod(true)
            .Invoke(c, new object[] { controller });
        c.Armies.Clear();
        c.Armies.Add(new Army
        {
            Id = 1, NationId = 0, ProvinceId = 5, StateMapIndex = 1005, TemplateId = "line_infantry",
            Divisions = 1, Personnel = 10000, MaxPersonnel = 10000, Strength = 1f,
            SupplyLevel = 1f, Organisation = .8f, SupplyCongestion = 1f
        });
        return (c, controller);
    }

    public static void Run()
    {
        var (open, _) = World();
        Check(open.Nations.Count > 0 && open.Nations[0].Id == 0,
            "nation id 0 stands at index 0");
        for (var day = 0; day < 14; day++) LogisticsSystem.Tick(open);
        var fed = open.Armies[0];
        Check(!fed.CutOff && fed.DaysCutOff == 0 && fed.SupplyLevel > 0.7f,
            "an open road holds supply and does not start the cauldron clock " +
            $"(cutOff={fed.CutOff} days={fed.DaysCutOff} supply={fed.SupplyLevel:0.000})");
        Check(LogisticsSystem.DescribeSupply(open, fed).Contains("reaches a hub"),
            "a fed formation says the line reaches a hub");

        // Stores first (StrandedGarrisons): the ring closes, the region's stores are counted, and
        // the clock does not start until they are gone.
        var (stored, storedController) = World();
        storedController[3] = 101;
        var fedFromStores = stored.Armies[0];
        LogisticsSystem.Tick(stored);
        Check(fedFromStores.CutOff && fedFromStores.DaysCutOff == 0 && fedFromStores.PocketStores > 0f,
            "a closed road counts the pocket's stores and does not start the clock while they last " +
            $"(days={fedFromStores.DaysCutOff} stores={fedFromStores.PocketStores:0.0})");
        var guard = 0;
        while (fedFromStores.PocketStores > 0f && guard++ < 200) LogisticsSystem.Tick(stored);
        LogisticsSystem.Tick(stored);
        Check(fedFromStores.PocketStores == 0f && fedFromStores.DaysCutOff >= 1,
            $"when the stores are gone the clock starts (days={fedFromStores.DaysCutOff})");

        // The bare clock behind the stores.
        var (cut, controller) = World();
        cut.Defines.StrandedGarrisonsEnabled = false;
        controller[3] = 101;
        var army = cut.Armies[0];
        var supply = army.SupplyLevel;
        var organisation = army.Organisation;
        LogisticsSystem.Tick(cut);
        Check(army.CutOff && army.DaysCutOff == 1 && army.SupplyLevel < supply && army.Organisation < organisation,
            "a closed road advances the clock and bleeds supply and organisation " +
            $"(days={army.DaysCutOff} supply={army.SupplyLevel:0.000} org={army.Organisation:0.000})");
        Check(cut.Armies.Contains(army) && army.Divisions > 0f,
            "the first day of the cauldron does not remove the formation");
        for (var day = 1; day < 14; day++) LogisticsSystem.Tick(cut);
        Check(army.DaysCutOff == cut.Defines.SupplyCutoffDays && army.SupplyLevel <= 0.001f &&
               army.Organisation <= cut.Defines.BattleBreakOrganisation + 0.0001f,
            "after the defined days the stores are gone and organisation is on the floor " +
            $"(days={army.DaysCutOff} supply={army.SupplyLevel:0.000} org={army.Organisation:0.000} " +
            $"floor={cut.Defines.BattleBreakOrganisation:0.000})");
        Check(cut.Armies.Count == 1 && army.Divisions > 0f && army.Strength == 1f,
            "the dry grouping is still in the order of battle");

        army.OrderKind = ArmyOrder.AdvanceFront;
        army.PlanActive = true;
        army.DirectOrder = true;
        var before = CampaignFingerprint.Of(cut);
        var line = LogisticsSystem.DescribeSupply(cut, army);
        var assault = AssaultReadiness.Assess(cut, army);
        Check(CampaignFingerprint.Of(cut) == before,
            "reading the cutoff line does not change the fingerprint");
        Check(line.Contains("No path to a supply hub") && line.Contains("14"),
            "the cutoff line names the missing road and the days in the cauldron: " + line);
        Check(line.Contains("The depot is not drawing ammunition.") &&
              line.Contains("The depot is not drawing oil."),
            "a depot that is not being drawn says so on the same line: " + line);
        Check(!assault.Ready, "a spent grouping cannot keep attacking, direct order included");

        cut.Nations[0].Stockpiles["Ammunition"] = 10f;
        cut.Nations[0].DailyStockpileBurn["Ammunition"] = 2f;
        cut.Nations[0].Stockpiles["Oil"] = 4f;
        cut.Nations[0].DailyStockpileBurn["Oil"] = 2f;
        var stockedHash = CampaignFingerprint.Of(cut);
        var stocked = LogisticsSystem.DescribeSupply(cut, army);
        Check(stocked.Contains("Ammunition in the depot lasts 5 days.") &&
              stocked.Contains("Oil in the depot lasts 2 days."),
            "the cutoff line names the days of ammunition and oil still in the depot: " + stocked);
        Check(CampaignFingerprint.Of(cut) == stockedHash &&
              cut.Nations[0].Stockpiles["Ammunition"] == 10f &&
              cut.Nations[0].DailyStockpileBurn["Oil"] == 2f,
            "reading the depot days does not spend them or change the fingerprint");
        var language = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var russian = LogisticsSystem.DescribeSupply(cut, army);
            Check(russian.Contains("Боеприпасов на складе хватит на 5 дн.") &&
                  russian.Contains("Нефти на складе хватит на 2 дн.") &&
                  CampaignFingerprint.Of(cut) == stockedHash,
                "the depot days read in Russian without changing the fingerprint: " + russian);
        }
        finally { Loc.Language = language; }

        controller[3] = 100;
        LogisticsSystem.Tick(cut);
        Check(!army.CutOff && army.DaysCutOff == 0 && cut.Armies.Contains(army),
            "restoring the road clears the clock and leaves the formation standing");

        var (thin, _) = World();
        thin.Defines.BaseStateSupplyThroughput = 0.01f;
        thin.Defines.SupplyPerInfrastructure = 0f;
        thin.Defines.SupplyPerBarracks = 0f;
        LogisticsSystem.Tick(thin);
        var overloaded = thin.Armies[0];
        var reason = LogisticsSystem.DescribeSupply(thin, overloaded);
        Check(!overloaded.CutOff && overloaded.DaysCutOff == 0 && overloaded.SupplyCongestion < 0.999f &&
               reason.Contains("over capacity"),
            "a hub that cannot carry the column is thinning, not cut off " +
            $"(congestion={overloaded.SupplyCongestion:0.000} text={reason})");

        WarSupport();
    }

    private static void WarSupport()
    {
        var c = new Campaign(1204);
        c.Nations.Add(new Nation { Id = 0, Tag = "WS0", Name = "Reader", MapIndex = 1, IsHuman = true });
        Check(c.Nations[0].Id == 0, "the war-support reader is nation 0");
        var peace = EconomySystem.DescribeWarSupport(c, c.Nations[0]);
        var hash = CampaignFingerprint.Of(c);
        var reread = EconomySystem.DescribeWarSupport(c, c.Nations[0]);
        Check(peace.Contains("does not apply") && peace == reread,
            "peace says war support does not apply: " + peace);
        Check(CampaignFingerprint.Of(c) == hash, "reading the peace line does not change the fingerprint");

        c.Wars.Add(new StrategicWar { Id = 1, Name = "Test war", AttackerId = 0, DefenderId = 2 });
        c.Nations[0].WarSupportDrivers.Begin("War support, weekly", "share");
        c.Nations[0].WarSupportDrivers.Add("Government focus on national security", 0.02f);
        hash = CampaignFingerprint.Of(c);
        var war = EconomySystem.DescribeWarSupport(c, c.Nations[0]);
        Check(war.Contains("Government focus on national security") && war.Length > 0,
            "a recorded driver is named: " + war);
        Check(CampaignFingerprint.Of(c) == hash, "reading the war-support reasons does not change the fingerprint");
    }
}
