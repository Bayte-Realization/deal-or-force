using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The console curses (2026-10-21): `snowgrave` freezes the world for good, `weirdroute` walks
/// every formation into the nearest water over any border, and a world without water gets the
/// static. All of it is state: saved, replayed and never lifted.
/// </summary>
static class WorldCurseTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("world curses: " + label);
        Console.WriteLine("PASS world curses: " + label);
    }

    public static void Run()
    {
        SnowgraveFreezesForGood();
        WeirdRouteWalksIntoTheSea();
        NoWaterMeansStatic();
    }

    /// <summary>A bootstrap world on a small map; `sea` leaves a ring of open water round the land.</summary>
    private static (Campaign Campaign, ProvinceTopology Map) World(bool sea)
    {
        var campaign = Campaign.CreateBootstrap(61045);
        var player = campaign.NationByTag("KOR");
        var neighbour = campaign.NationByTag("JPN");
        campaign.ConfigureSinglePlayer(player.Id);
        var home = campaign.StatesOf(player).First();
        var inland = campaign.StatesOf(neighbour).First();
        const int width = 16, height = 8;
        var states = new ushort[width * height];
        var owners = new ushort[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var land = !sea || (x >= 1 && x <= 14 && y >= 1 && y <= 6);
            if (!land) continue;
            // A block of the neighbour's land in the middle, with Korean ground on every side of it.
            var middle = x >= 6 && x <= 9 && y >= 3 && y <= 4;
            states[y * width + x] = (ushort)(middle ? inland.MapIndex : home.MapIndex);
            owners[y * width + x] = (ushort)(middle ? neighbour.MapIndex : player.MapIndex);
        }
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        campaign.AttachProvinceTopology(map);
        campaign.Armies.Clear();
        campaign.ObserverMode = true;
        campaign.Defines.HoursPerTick = 1;
        campaign.Defines.EventIntervalDays = 0;
        campaign.IsPaused = false;
        return (campaign, map);
    }

    private static Army Formation(Campaign campaign, int id, Nation owner, int province)
    {
        var map = campaign.ProvinceMap;
        var army = new Army
        {
            Id = id, NationId = owner.Id, ProvinceId = province, StateMapIndex = map.ProvinceToState[province],
            Name = "Formation " + id, Divisions = 1f, PlanActive = false
        };
        (army.Latitude, army.Longitude) = map.CenterOf(province);
        campaign.Armies.Add(army);
        return army;
    }

    private static void SnowgraveFreezesForGood()
    {
        var (campaign, _) = World(sea: true);
        var player = campaign.Player;
        Check(WorldCurses.Cast(campaign, WorldCurses.Snowgrave) == WorldCurses.Snowgrave &&
              WorldCurses.Frozen(campaign), "snowgrave lays the ice");
        var treasury = player.Treasury;
        var tax = player.TaxRate;
        var hours = campaign.ElapsedHours;
        campaign.Commands.Enqueue(new SetTaxRateCommand { IssuerId = player.Id, Value = tax + .05f });
        campaign.TickDay();
        Check(campaign.ElapsedHours == hours + 24, "the calendar keeps turning under the ice");
        Check(player.TaxRate == tax && player.Treasury == treasury && campaign.Commands.Count == 0,
              "nothing else moves: no order is carried out and no money changes hands");
        Check(WorldCurses.Cast(campaign, WorldCurses.WeirdRoute) == WorldCurses.Snowgrave,
              "nothing marches out of a frozen world");
        var copy = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(campaign, "curse"), "curse");
        Check(copy.Success && copy.Campaign.Curse == WorldCurses.Snowgrave && copy.Campaign.CurseDay == campaign.CurseDay &&
              WorldCurses.Frozen(copy.Campaign), "the ice is saved with the world, and a loaded world is still frozen");
    }

    private static void WeirdRouteWalksIntoTheSea()
    {
        var (campaign, map) = World(sea: true);
        var player = campaign.Player;
        var neighbour = campaign.NationByTag("JPN");
        var inland = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToNation[p] == neighbour.MapIndex);
        var shore = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToNation[p] == player.MapIndex);
        // The neighbour's formation has to cross Korean ground to reach any water.
        Formation(campaign, 9101, neighbour, inland);
        Formation(campaign, 9102, player, shore);
        var plain = CampaignFingerprint.Of(campaign);
        Check(WorldCurses.Cast(campaign, WorldCurses.WeirdRoute) == WorldCurses.WeirdRoute && !WorldCurses.Frozen(campaign),
              "the weird route begins, and the world keeps running");
        Check(CampaignFingerprint.Of(campaign) != plain, "the curse is part of the world's fingerprint");
        for (var day = 0; day < 3 && campaign.Armies.Any(a => a.Id is 9101 or 9102); day++) campaign.TickDay();
        Check(campaign.Armies.All(a => a.Id is not (9101 or 9102)) && campaign.CurseDrowned >= 2,
              "both formations walk into the water within three days, the inland one over a foreign border");
        Formation(campaign, 9103, player, shore);
        campaign.TickDay();
        Check(campaign.Armies.All(a => a.Id != 9103), "a formation raised afterwards goes the same way");
        Check(WorldCurses.Cast(campaign, WorldCurses.Snowgrave) == WorldCurses.Snowgrave,
              "the ice can still fall on a world marching into the sea");
    }

    private static void NoWaterMeansStatic()
    {
        var (campaign, _) = World(sea: false);
        Check(WorldCurses.Cast(campaign, WorldCurses.WeirdRoute) == WorldCurses.Static && WorldCurses.Frozen(campaign),
              "with no water anywhere the weird route is static, and the world stops");
        Check(WorldCurses.Cast(campaign, WorldCurses.Snowgrave) == WorldCurses.Static,
              "and nothing is cast over the static");
    }
}
