using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>A lost capital evacuates one rear, not every depot inside every pocket.</summary>
public static class GovernmentSupplyTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL government supply: " + text);
        Console.WriteLine("PASS government supply: " + text);
    }

    private static Campaign World(bool depots = true)
    {
        const int width = 32, height = 256;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var at = y * width + x;
            var p = y / 32 + 1;
            states[at] = (ushort)(1000 + p);
            owners[at] = 100;
            rgb[at * 3 + 2] = (byte)p;
        }
        var map = ProvinceTopology.FromColorRaster(states, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 8).ToDictionary(p => p, p => p));
        var c = new Campaign(2410) { ScenarioId = "government_supply" };
        c.Nations.Add(new Nation { Id = 0, Tag = "GS0", Name = "Evacuated country", MapIndex = 100,
            Population = 4000000, Gdp = 400, Treasury = 50, MilitarySpending = .06f,
            StockpilesSeeded = true, ReinforcementCapacity = 440f, CapitalProvinceId = 1, CapitalStateMapIndex = 1001,
            CapitalName = "Legal city", GovernmentSeatProvinceId = 5 });
        c.Nations.Add(new Nation { Id = 1, Tag = "GS1", Name = "Enemy", MapIndex = 101,
            Population = 2000000, Gdp = 200, Treasury = 50 });
        c.SetPlayer(0);
        for (var p = 1; p <= 8; p++)
            c.States.Add(new ScenarioState { Id = "gs_" + p, Name = "Region " + p,
                MapIndex = 1000 + p, NationMapIndex = 100, NationId = "GS0",
                Population = p == 5 ? 2000000 : 100000 });
        if (depots)
        {
            c.States[1].Buildings[BuildingKind.Barracks] = 2;
            c.States[4].Buildings[BuildingKind.Barracks] = 1;
        }
        c.AttachProvinceTopology(map);
        c.ProvinceController[1] = c.ProvinceController[3] = 101;
        c.Armies.Clear();
        foreach (var p in new[] { 2, 6 })
            c.Armies.Add(new Army { Id = p, NationId = 0, ProvinceId = p, StateMapIndex = 1000 + p,
                TemplateId = "line_infantry", Divisions = 1, Personnel = 10000, MaxPersonnel = 10000,
                Strength = 1f, Equipment = .2f, SupplyLevel = 1f, Organisation = .8f });
        c.Wars.Add(new StrategicWar { Id = 710, AttackerId = 1, DefenderId = 0 });
        foreach (var good in StrategicStockpiles.TrackedGoods) c.Nations[0].Stockpiles[good] = 100f;
        return c;
    }

    private static void Morning(Campaign c, int day)
    {
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours), BindingFlags.Public | BindingFlags.Instance)
            .SetValue(c, day * 24L);
        LogisticsSystem.Tick(c);
    }

    public static void Run()
    {
        var c = World(); var n = c.Nations[0];
        Morning(c, 0);
        Check(c.Armies[0].CutOff && !c.Armies[1].CutOff,
            "only the government's connected rear supplies the army after the legal city falls");
        Check(c.Armies[0].SupplySourceStateMapIndex == 1002 && c.Armies[0].DaysCutOff == 1,
            "the other barracks remains a finite emergency store, not another national root");
        Check(EquipmentLogistics.DailyLimit(c, c.Armies[0]) == 0 &&
            EquipmentLogistics.DailyLimit(c, c.Armies[1]) > 0,
            "national replacements reach the emergency government's rear but not the separate depot");
        for (var day = 1; day < 14; day++) Morning(c, day);
        Check(c.Armies[0].DaysCutOff == 14 && c.Armies[0].SupplyLevel == 0 && !c.Armies[1].CutOff,
            "losing the capital does not keep the other pocket alive through repeated traces");
        Check(c.Armies.All(a => a.Personnel == 10000 && a.Strength == 1f && a.Equipment == .2f),
            "the route decision itself neither destroys people nor durable kit");

        c = World(); n = c.Nations[0];
        c.ProvinceMap.ProvinceToState[2] = (ushort)n.CapitalStateMapIndex;
        Morning(c, 0);
        Check(c.Armies[0].CutOff && !c.Armies[1].CutOff,
            "an occupied capital's surviving regional fragment cannot override the actual government seat");

        c = World(); n = c.Nations[0]; n.GovernmentSeatProvinceId = 0;
        string Government() => JsonSerializer.Serialize(new { n.CapitalProvinceId, n.CapitalStateMapIndex,
            n.CapitalName, n.GovernmentSeatProvinceId, n.CapitalMovedDay, n.Treasury,
            n.WarSupport, n.StabilityGovernance });
        var before = Government();
        var revision = c.CapitalRevision; var random = c.Random.State;
        Morning(c, 0);
        Check(c.Armies[0].CutOff && !c.Armies[1].CutOff,
            "before the daily capital pass, supply predicts the same safe emergency seat");
        Check(Government() == before && c.CapitalRevision == revision && c.Random.State == random,
            "route fallback does not relocate government, charge money, change war support or draw RNG");
        CapitalCore.Tick(c);
        Check(n.GovernmentSeatProvinceId == 5 && n.CapitalProvinceId == 1,
            "the authoritative capital pass chooses the predicted seat without erasing the legal capital");
        var fingerprint = CampaignFingerprint.Of(c);
        Check(CapitalCore.SupplyOriginProvinceId(c, n) == 5 && CampaignFingerprint.Of(c) == fingerprint,
            "reading the supply origin is fingerprint-pure");

        c = World(false); n = c.Nations[0]; n.GovernmentSeatProvinceId = 0;
        Morning(c, 0);
        Check(c.Armies[0].CutOff && !c.Armies[1].CutOff && c.Armies[0].SupplyLevel > 0,
            "without barracks a single safe replacement rear feeds its component, not every scrap of ground");
        c.ProvinceController[3] = 100; Morning(c, 3);
        Check(c.Armies.All(a => !a.CutOff && a.DaysCutOff == 0),
            "opening a real corridor joins both halves to that rear without needing barracks");

        c = World(); n = c.Nations[0]; c.ProvinceController[1] = 100;
        CapitalCore.Tick(c); Morning(c, 0);
        Check(n.GovernmentSeatProvinceId == 0 && !c.Armies[0].CutOff && c.Armies[1].CutOff,
            "liberating the legal city restores its rear; an unrelated closed pocket does not refill");

        c = World(); n = c.Nations[0];
        c.ProvinceMap.ProvinceToNation[1] = 101; c.ProvinceController[1] = 100;
        n.GovernmentSeatProvinceId = 0;
        Morning(c, 0);
        Check(c.Armies[0].CutOff && !c.Armies[1].CutOff,
            "controlling a ceded legal city cannot use a foreign-owned stale capital as a supply root");

        c = World(false); n = c.Nations[0]; n.CapitalProvinceId = 0; n.GovernmentSeatProvinceId = 0;
        c.ProvinceController[1] = 100;
        Morning(c, 0);
        Check(!c.Armies[0].CutOff && c.Armies[1].CutOff,
            "legacy maps with only a capital region still start from one province in that region");

        c = World(); n = c.Nations[0];
        n.GovernmentSeatProvinceId = -1;
        for (var p = 1; p <= 8; p++) c.ProvinceController[p] = 101;
        Morning(c, 0);
        Check(c.Armies.All(a => a.CutOff && a.DaysCutOff == 1 && a.SupplyLevel > 0),
            "total occupation leaves finite carried reserves, not a root on enemy ground");
        c.ProvinceController[6] = 100; Morning(c, 3);
        Check(c.Armies[0].CutOff && !c.Armies[1].CutOff,
            "recovered owned ground can restart one rear even with the saved no-seat sentinel");

        c = World(); n = c.Nations[0]; n.IsHuman = true;
        c.DestroyedProvinces.Add(5); n.GovernmentSeatProvinceId = 5;
        Check(CapitalCore.SupplyOriginProvinceId(c, n) != 5,
            "destroyed government ground is not a supply source, including for a human player");
        c.States.ForEach(s => s.Kind = "Lake");
        Check(CapitalCore.SupplyOriginProvinceId(c, n) == 0,
            "water-only administration cannot invent a usable emergency supply root");

        c = World(); n = c.Nations[0]; n.GovernmentSeatProvinceId = 0;
        c.ConfigureSinglePlayer(0);
        var snapshot = SaveSystem.ExportNetworkSnapshot(c, "government-supply");
        var restored = SaveSystem.ImportNetworkSnapshot(snapshot, "government-supply");
        Check(restored.Success, "mid-day campaign snapshot imports without format changes: " + restored.Warning);
        restored.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Morning(c, 0); Morning(restored.Campaign, 0);
        Check(c.Armies.Zip(restored.Campaign.Armies).All(pair => pair.First.CutOff == pair.Second.CutOff &&
            pair.First.DaysCutOff == pair.Second.DaysCutOff && pair.First.SupplyLevel == pair.Second.SupplyLevel),
            "restoring a snapshot before evacuation chooses the identical route and reserve clock");
    }
}
