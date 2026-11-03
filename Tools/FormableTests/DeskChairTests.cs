using System;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The society chair names three facts the situation room already files:
/// a conciliation programme, a piece of land cut off, and a headquarters wearing out its welcome.
/// Each line is quiet when the fact is absent. Reading one changes nothing.
/// </summary>
static class DeskChairTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("desk chair: " + label);
        Console.WriteLine("PASS desk chair: " + label);
    }

    public static void Run()
    {
        var previous = Loc.Language;
        Loc.Language = "english";
        try
        {
            Check(SaveSystem.FormatVersion == 2, "save format stays 2");
            Check(SaveSystem.MapDataVersion == "2024.10.06-a", "map data stays 2024.10.06-a");
            QuietWithoutTheFacts();
            UnnamedProgrammeUsesTheSentence();
            ThreeFacts();
            RussianLines();
        }
        finally { Loc.Language = previous; }
    }

    static void QuietWithoutTheFacts()
    {
        var campaign = Bare();
        var nation = campaign.Nations[0];
        var before = CampaignFingerprint.Of(campaign);
        var society = SituationDesk.Read(campaign, nation, DeskLens.Society);
        Check(Find(society, "conciliation").Id is null or "", "no conciliation programme, no line");
        Check(Find(society, "detached_land").Id is null or "", "a country with no map has no detached line");
        Check(Find(society, "host_temper").Id is null or "", "a quiet headquarters is not on the chair");
        Check(Find(society, "political_strain").Id is null or "", "the quiet chair does not invent a loudest region");
        foreach (var item in society)
            Check(item.Title != "0" && item.Title != "—" && !item.Title.EndsWith(": —") && !item.Title.EndsWith(": 0"),
                "a quiet line is not a fake zero: " + item.Title);
        Check(CampaignFingerprint.Of(campaign) == before, "reading the quiet chair does not change the fingerprint");
        Check(nation.Treasury == 0f, "reading the quiet chair does not move money");
    }

    static void UnnamedProgrammeUsesTheSentence()
    {
        var campaign = Bare();
        var nation = campaign.Nations[0];
        nation.DomesticCrisis.ConciliationRegion = 99;
        nation.DomesticCrisis.ConciliationUntilDay = 40;
        var line = Find(SituationDesk.Read(campaign, nation, DeskLens.Society), "conciliation");
        Check(line.Title == "A region is being settled",
            "a programme with no named region keeps the report's sentence, got [" + line.Title + "]");
        Check(!line.Title.Contains('—') && line.Title != "0" && !line.Title.Contains("Settlement under way:"),
            "the unnamed programme is not a dash");
        Check(line.Screen == "Laws" && line.NationId < 0, "the programme opens Laws and does not name a country to buy");
        Check(line.Tooltip.Contains("The payment has 40 days left."), "the tooltip keeps the report's sentence");
        Check(nation.DomesticCrisis.ConciliationUntilDay == 40 && nation.Treasury == 0f,
            "reading the programme does not pay for it or end it");
    }

    static void ThreeFacts()
    {
        var campaign = World(out var home, out var movement, out var map);
        var treasury = home.Treasury;
        var until = home.DomesticCrisis.ConciliationUntilDay;
        var heat = movement.Movement.Heat;
        var headquarters = movement.Movement.HeadquartersStateMapIndex;
        var offers = campaign.DealOffers.Count;
        ushort Owner(ushort region)
        {
            for (var province = 1; province <= map.ProvinceCount; province++)
                if (map.ProvinceToState[province] == region) return map.ProvinceToNation[province];
            return 0;
        }
        var far = Owner(2);
        var way = Owner(3);
        var before = CampaignFingerprint.Of(campaign);
        Check(Corridors.TryReport(campaign, home, out var report) && report.Provinces > 0,
            "the fixture's detached piece is the corridor report");
        var society = SituationDesk.Read(campaign, home, DeskLens.Society);
        var conciliation = Find(society, "conciliation");
        var detached = Find(society, "detached_land");
        var welcome = Find(society, "host_temper");
        Check(Count(society, "conciliation") == 1 && Count(society, "detached_land") == 1 && Count(society, "host_temper") == 1,
            "each fact is one line");
        Check(Count(society, "political_strain") == 0, "these three do not add a second loudest-region line");
        Check(conciliation.Title == "Settlement under way: Deskedge",
            "the programme names the region, got [" + conciliation.Title + "]");
        Check(conciliation.Tooltip.Contains("Deskedge is being settled. 40 days of it are left."),
            "the programme tooltip is the report's sentence");
        Check(conciliation.Tooltip.Contains("The pull to leave, the anger, and the political distance cool while the money lasts. A border is not redrawn."),
            "the programme tooltip keeps the report's cause");
        Check(conciliation.Tooltip.Contains("Region:") && conciliation.Tooltip.Contains("Days left:"),
            "the programme tooltip keeps the report's labels");
        Check(conciliation.Screen == "Laws" && conciliation.Lens == DeskLens.Society && conciliation.NationId < 0 && conciliation.Detail.Length == 0,
            "the programme sits on the society chair and opens Laws");
        var expectedWay = report.CanAsk && report.Holder.Length > 0
            ? "The way: " + report.Holder
            : "A piece of the country is cut off";
        Check(detached.Title == expectedWay, "the detached line is [" + expectedWay + "], got [" + detached.Title + "]");
        Check(!detached.Title.Contains('—') && detached.Title != "0" && detached.Title != "Cut off: 0",
            "the detached line is not a fake zero");
        Check(detached.Tooltip.Contains("provinces are cut off from the rest"),
            "the detached tooltip is the report's sentence");
        Check(detached.Tooltip.Contains("The piece is not joined to the rest by land. A purchase asks for the regions the way runs through, not a war."),
            "the detached tooltip keeps the report's cause");
        Check(detached.Tooltip.Contains("Cut off:") && detached.Tooltip.Contains("The way:"),
            "the detached tooltip keeps the report's labels");
        Check(detached.Screen == "Situation" && detached.NationId < 0 && detached.Detail.Length == 0,
            "the detached line opens the situation room and does not send the ask");
        var chip = MovementRules.PatienceChip(campaign, movement);
        Check(chip == "welcome wearing thin", "the fixture is the wearing-thin welcome, got [" + chip + "]");
        Check(welcome.Title == "welcome wearing thin: " + movement.DisplayName,
            "the welcome line names the movement, got [" + welcome.Title + "]");
        Check(welcome.Tooltip.Contains(Loc.Get(MovementRules.Patience(campaign, movement))),
            "the welcome tooltip is the patience sentence");
        Check(welcome.Tooltip.Contains("A headquarters is wearing out its welcome"),
            "the welcome tooltip keeps the report's title");
        Check(welcome.Tooltip.Contains("Patience spent:") && welcome.Tooltip.Contains("Left before it goes:"),
            "the welcome tooltip keeps the report's labels");
        Check(welcome.Tooltip.Contains("Throwing it out is a decision. Leaving it spends the patience until the headquarters goes on its own."),
            "the welcome tooltip keeps the report's consequence");
        Check(welcome.Screen == "Movements" && welcome.NationId == movement.Id && welcome.Lens == DeskLens.Society && welcome.Detail.Length == 0,
            "the welcome opens that movement and does not fund it");
        foreach (var lens in new[] { DeskLens.Security, DeskLens.Economy, DeskLens.Diplomacy, DeskLens.Cabinet })
        {
            var items = SituationDesk.Read(campaign, home, lens);
            Check(Count(items, "conciliation") == 0 && Count(items, "detached_land") == 0 && Count(items, "host_temper") == 0,
                lens + " does not take the society lines");
        }
        var rail = StrategicHudBrief.Read(campaign, home);
        Check(rail.TrueForAll(item => item.Id is not ("conciliation" or "detached_land" or "host_temper")),
            "the compact rail does not gain these lines");
        Check(home.Treasury == treasury && home.DomesticCrisis.ConciliationUntilDay == until, "reading does not pay or end the programme");
        Check(movement.Movement.Heat == heat && movement.Movement.HeadquartersStateMapIndex == headquarters,
            "reading does not spend the patience or move the headquarters");
        Check(campaign.DealOffers.Count == offers && Owner(2) == far && Owner(3) == way,
            "reading does not send an ask or move land");
        Check(CampaignFingerprint.Of(campaign) == before, "reading the three lines does not change the fingerprint");
    }

    static void RussianLines()
    {
        var previous = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var quiet = Bare();
            var missing = SituationDesk.Read(quiet, quiet.Nations[0], DeskLens.Society);
            Check(Count(missing, "conciliation") == 0 && Count(missing, "detached_land") == 0 && Count(missing, "host_temper") == 0,
                "the Russian chair is quiet when the facts are absent");
            var campaign = World(out var home, out var movement, out _);
            Check(Corridors.TryReport(campaign, home, out var report), "the Russian fixture still has the detached piece");
            var society = SituationDesk.Read(campaign, home, DeskLens.Society);
            var conciliation = Find(society, "conciliation");
            var detached = Find(society, "detached_land");
            var welcome = Find(society, "host_temper");
            Check(Loc.Get("Settlement under way") == "Успокоение уже идёт"
                  && conciliation.Title == "Успокоение уже идёт: Deskedge",
                "the Russian programme line is Успокоение уже идёт: Deskedge, got [" + conciliation.Title + "]");
            Check(conciliation.Tooltip.Contains("Deskedge успокаивают. Осталось 40 дн."),
                "the Russian programme tooltip keeps the report's sentence");
            Check(conciliation.Tooltip.Contains(Loc.Get("Region") + ":") && conciliation.Tooltip.Contains(Loc.Get("Days left") + ":"),
                "the Russian programme tooltip keeps the report's labels");
            var expectedWay = report.CanAsk && report.Holder.Length > 0
                ? Loc.Get("The way") + ": " + report.Holder
                : Loc.Get("A piece of the country is cut off");
            Check(Loc.Get("The way") == "Путь" && Loc.Get("A piece of the country is cut off") == "Кусок страны отрезан"
                  && detached.Title == expectedWay,
                "the Russian detached line is [" + expectedWay + "], got [" + detached.Title + "]");
            Check(welcome.Title == Loc.Get("welcome wearing thin") + ": " + movement.DisplayName
                  && Loc.Get("welcome wearing thin") == "гостеприимство на исходе",
                "the Russian welcome line is гостеприимство на исходе: " + movement.DisplayName + ", got [" + welcome.Title + "]");
            Check(welcome.Tooltip.Contains(Loc.Get(MovementRules.Patience(campaign, movement))),
                "the Russian welcome tooltip keeps the patience sentence");
            Check(conciliation.Screen == "Laws" && detached.Screen == "Situation" && welcome.Screen == "Movements",
                "the Russian buttons open the same screens");
        }
        finally { Loc.Language = previous; }
    }

    static DeskItem Find(System.Collections.Generic.List<DeskItem> items, string id)
    {
        foreach (var item in items)
            if (item.Id == id) return item;
        return default;
    }

    static int Count(System.Collections.Generic.List<DeskItem> items, string id)
    {
        var count = 0;
        foreach (var item in items)
            if (item.Id == id) count++;
        return count;
    }

    static Campaign World(out Nation home, out Nation movement, out ProvinceTopology map)
    {
        var campaign = Bare();
        home = campaign.Nations[0];
        home.Treasury = 500f;
        home.CapitalStateMapIndex = 1;
        campaign.Defines.CorridorMinimumProvinces = 1;
        var other = new Nation { Id = 1, MapIndex = 2, Tag = "BBB", Name = "Other", Population = 100, Treasury = 40f };
        movement = new Nation
        {
            Id = 2, MapIndex = 3, Tag = "LGE", Name = "The League", Population = 10_000,
            Movement = new MovementState
            {
                Id = "league", Landless = true, HeadquartersStateMapIndex = 1,
                Heat = 0.5f, Militancy = 0.2f
            }
        };
        campaign.Nations.Add(other);
        campaign.Nations.Add(movement);
        campaign.States.Add(new ScenarioState
        {
            MapIndex = 1, NationMapIndex = 1, Name = "Deskedge", Population = 50
        });
        campaign.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 1, Id = "far", Name = "Far", Population = 20 });
        campaign.States.Add(new ScenarioState { MapIndex = 3, NationMapIndex = 2, Id = "gate", Name = "Gate", Population = 20 });
        campaign.States.Add(new ScenarioState { MapIndex = 4, NationMapIndex = 2, Id = "rest", Name = "Rest", Population = 40 });
        home.DomesticCrisis.ConciliationRegion = 1;
        home.DomesticCrisis.ConciliationUntilDay = 40;
        const int width = 5, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        void Cell(int x, int y, ushort region, ushort owner)
        {
            states[y * width + x] = region;
            owners[y * width + x] = owner;
        }
        Cell(1, 1, 1, 1);
        Cell(2, 1, 3, 2);
        Cell(3, 1, 2, 1);
        Cell(2, 2, 4, 2);
        map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        var flags = BindingFlags.Instance | BindingFlags.Public;
        var controller = new ushort[map.ProvinceCount + 1];
        var occupation = new float[map.ProvinceCount + 1];
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap), flags)!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController), flags)!.SetValue(campaign, controller);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation), flags)!.SetValue(campaign, occupation);
        Check(ReferenceEquals(campaign.ProvinceMap, map)
              && ReferenceEquals(campaign.ProvinceController, controller)
              && ReferenceEquals(campaign.ProvinceOccupation, occupation),
            "the province arrays are attached by reflection and not by AttachProvinceTopology");
        campaign.InvalidateStateIndex();
        return campaign;
    }

    static Campaign Bare()
    {
        var campaign = new Campaign(30);
        campaign.Nations.Add(new Nation
        {
            Id = 0, MapIndex = 1, Tag = "H0", Name = "Home", Population = 2_000_000,
            Stability = 0.8f, Gdp = 0f, Treasury = 0f, Inflation = 0f, ShortageIndex = 0f,
            StandardOfLiving = 0.5f, IsHuman = false
        });
        Check(campaign.Nations[0].Id == 0 && campaign.NationById(0) == campaign.Nations[0],
            "nation id 0 stands at index 0");
        var flags = BindingFlags.Instance | BindingFlags.Public;
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap), flags)!.SetValue(campaign, null);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController), flags)!.SetValue(campaign, new ushort[1]);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation), flags)!.SetValue(campaign, new float[1]);
        return campaign;
    }
}
