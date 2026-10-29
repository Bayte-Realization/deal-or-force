using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// A war can ask for one depot shipment. The paper has a price and a refusal.
/// Accepting is not a proxy subsidy, a treasury grant, a land transfer, or an army order.
/// </summary>
static class ArmsShipmentTests
{
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("arms shipment: " + name);
        Console.WriteLine("PASS arms shipment: " + name);
    }

    public static void Run()
    {
        Check(SaveSystem.FormatVersion == 2, "save format stays 2");
        ReadingDoesNotChangeTheWorld();
        NoPretextFilesNothing();
        ClosedRoads();
        HumanPaperDoesNotSpendUntilAccepted();
        DeclineLeavesTheDepots();
        AcceptShipsOnceAndDoesNotEnterTheWar();
        AnAiDonorAnswersWithoutASecondSystem();
        ReviewAsksOnlyTheHuman();
        DisablingClosesThePaperWithoutShipping();
        MissingSaveFieldsStayEmpty();
    }

    static Campaign War(out Nation donor, out Nation requester, out Nation enemy, out Army army, out ScenarioState home, bool allied = true)
    {
        var c = new Campaign(29);
        donor = Nation(0, 1, "POL", "Poland", true);
        requester = Nation(1, 2, "UKR", "Ukraine", false);
        enemy = Nation(2, 3, "RUS", "Russia", false);
        c.Nations.Add(donor);
        c.Nations.Add(requester);
        c.Nations.Add(enemy);
        if (allied)
            CampaignDiplomacy.Get(c, donor.Id, requester.Id).Stance = DiplomaticStance.Allied;
        c.Wars.Add(new StrategicWar { Id = 4, AttackerId = enemy.Id, DefenderId = requester.Id, Name = "Border war" });
        Open(donor);
        Open(requester);
        army = new Army { Id = 8, NationId = donor.Id, DirectOrder = true, OrderKind = ArmyOrder.Manual, OrderProvinceId = 15 };
        c.Armies.Add(army);
        home = new ScenarioState { MapIndex = 40, NationMapIndex = donor.MapIndex, Id = "mazovia", Name = "Mazovia", Population = 1000 };
        c.States.Add(home);
        return c;
    }

    static Nation Nation(int id, int map, string tag, string name, bool human) => new()
    {
        Id = id, MapIndex = map, Tag = tag, Name = name, Population = 1_000_000, Gdp = 400, Treasury = 12, IsHuman = human
    };

    static void Open(Nation nation)
    {
        nation.StockpilesSeeded = true;
        foreach (var good in new[] { "Ammunition", "Arms", "Artillery", "Drones" })
        {
            nation.Stockpiles[good] = 20f;
            nation.StockpileCoverage[good] = 1f;
        }
        nation.DailyStockpileBurn["Ammunition"] = 2f;
    }

    static void Day(Campaign c, int day) =>
        typeof(Campaign).GetProperty("ElapsedHours", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SetValue(c, day * 24L);

    static void ReadingDoesNotChangeTheWorld()
    {
        var c = War(out var donor, out var requester, out _, out _, out _);
        var before = CampaignFingerprint.Of(c);
        var pretext = ArmsShipmentRequests.Pretext(c, donor, requester, c.NationById(2), c.Wars[0]);
        var live = ArmsShipmentRequests.Assess(c, requester.Id, donor.Id, 4, c.Defines.ArmsShipmentShare, c.Defines.ArmsShipmentDays);
        var days = StrategicStockpiles.DaysOnHand(donor, "Ammunition");
        Check(pretext == "An ally is on the defensive, and you already promised to stand with them." && live.Allowed && days == 10
            && CampaignFingerprint.Of(c) == before && donor.Stockpiles["Ammunition"] == 20f,
            "reading the pretext, the price and the depot days does not move the world");
    }

    static void NoPretextFilesNothing()
    {
        var c = War(out var donor, out var requester, out _, out _, out _, allied: false);
        var before = CampaignFingerprint.Of(c);
        var outcome = ArmsShipmentRequests.Submit(c, requester.Id, donor.Id, 4);
        Check(outcome.RequestId == 0 && outcome.Reason == "There is no pretext for sending arms into that war."
            && c.ArmsShipmentRequests.Count == 0 && c.Sponsorships.Count == 0 && CampaignFingerprint.Of(c) == before,
            "without a pretext the paper is not filed and nothing is spent");
    }

    static void ClosedRoads()
    {
        var c = War(out var donor, out var requester, out _, out _, out _);
        c.Defines.ArmsShipmentShare = 0f;
        Check(ArmsShipmentRequests.Submit(c, requester.Id, donor.Id).Reason == "Arms shipments are not available."
            && c.ArmsShipmentRequests.Count == 0, "a zero share leaves the request unfiled");
        c.Defines.ArmsShipmentShare = 0.25f;
        donor.StockpilesSeeded = false;
        requester.StockpilesSeeded = false;
        Check(ArmsShipmentRequests.CanSubmit(c, requester.Id, donor.Id, 4).Reason
            == "The depots have not been opened, so there is nothing to send.",
            "unopened depots are a refusal, not a silent seeding");
        Open(donor);
        Open(requester);
        foreach (var good in new[] { "Ammunition", "Arms", "Artillery", "Drones" })
            donor.StockpileCoverage[good] = 0.2f;
        var dry = ArmsShipmentRequests.CanSubmit(c, requester.Id, donor.Id, 4);
        Check(!dry.Allowed && dry.Reason == "The depots could not spare a shipment of ammunition, arms, artillery or drones."
            && donor.Stockpiles["Ammunition"] == 20f, "dry depots refuse and stay dry");
    }

    static void HumanPaperDoesNotSpendUntilAccepted()
    {
        var c = War(out var donor, out var requester, out _, out var army, out var home);
        var before = CampaignFingerprint.Of(c);
        var filed = ArmsShipmentRequests.Submit(c, requester.Id, donor.Id, -1);
        Check(filed.State == ArmsShipmentState.Pending && filed.Shipped == 0f && c.Sponsorships.Count == 0
            && donor.Treasury == 12f && donor.Stockpiles["Ammunition"] == 20f
            && army.DirectOrder && army.OrderKind == ArmyOrder.Manual && army.OrderProvinceId == 15
            && home.NationMapIndex == donor.MapIndex && c.ProvinceMap is null
            && CampaignFingerprint.Of(c) != before,
            "a human paper waits, and waiting is not a subsidy, a payment, an order or a border");
        c.ArmsShipmentRequests.Clear();
        c.ArmsShipmentCooldowns.Clear();
        c.NextArmsShipmentRequestId = 1;
        Check(CampaignFingerprint.Of(c) == before, "arms state is mixed only while it is not the default");
        var again = ArmsShipmentRequests.Submit(c, requester.Id, donor.Id, 4);
        var duplicate = ArmsShipmentRequests.Submit(c, requester.Id, donor.Id, 4);
        Check(again.RequestId > 0 && duplicate.RequestId == 0
            && duplicate.Reason == "A shipment between these countries already awaits a reply."
            && c.ArmsShipmentRequests.Count == 1, "the same pair cannot open a second paper");
    }

    static void DeclineLeavesTheDepots()
    {
        var c = War(out var donor, out _, out _, out var army, out var home);
        var filed = ArmsShipmentRequests.Submit(c, 1, donor.Id, 4);
        var declined = ArmsShipmentRequests.Respond(c, donor.Id, filed.RequestId, ArmsShipmentReply.Decline);
        Check(declined.State == ArmsShipmentState.Declined && declined.Shipped == 0f
            && donor.Stockpiles["Ammunition"] == 20f && c.Nations[1].Stockpiles["Ammunition"] == 20f
            && c.Sponsorships.Count == 0 && donor.Treasury == 12f
            && army.DirectOrder && home.NationMapIndex == donor.MapIndex
            && !c.Chronicle.Entries.Any(entry => entry.Kind == "arms_shipment"),
            "declining sends nothing and writes no shipment into the chronicle");
    }

    static void AcceptShipsOnceAndDoesNotEnterTheWar()
    {
        var c = War(out var donor, out var requester, out _, out var army, out var home);
        var filed = ArmsShipmentRequests.Submit(c, requester.Id, donor.Id, 4);
        var command = new RespondArmsShipmentCommand { IssuerId = donor.Id, RequestId = filed.RequestId, Reply = ArmsShipmentReply.Accept };
        c.Commands.Enqueue(command);
        c.Commands.ApplyAll(c);
        Check(command.Outcome.State == ArmsShipmentState.Granted && command.Outcome.Shipped > 0f
            && donor.Stockpiles["Ammunition"] < 20f && requester.Stockpiles["Ammunition"] > 20f
            && c.Sponsorships.Count == 0 && donor.Treasury == 12f && requester.Treasury == 12f
            && army.DirectOrder && army.OrderKind == ArmyOrder.Manual && army.OrderProvinceId == 15
            && home.NationMapIndex == donor.MapIndex && c.ProvinceMap is null
            && !c.Wars[0].Involves(donor.Id)
            && c.Chronicle.Entries.Any(entry => entry.Kind == "arms_shipment"),
            "accepting moves depot goods once and does not open a subsidy, a war, or a new border");
        var ammo = donor.Stockpiles["Ammunition"];
        var second = ArmsShipmentRequests.Respond(c, donor.Id, filed.RequestId, ArmsShipmentReply.Accept);
        Check(second.State == ArmsShipmentState.Invalidated && donor.Stockpiles["Ammunition"] == ammo,
            "a second answer does not ship again");
    }

    static void AnAiDonorAnswersWithoutASecondSystem()
    {
        var c = new Campaign(30);
        var player = Nation(0, 1, "POL", "Poland", true);
        var patron = Nation(1, 2, "FRA", "France", false);
        var enemy = Nation(2, 3, "RUS", "Russia", false);
        c.Nations.Add(player);
        c.Nations.Add(patron);
        c.Nations.Add(enemy);
        CampaignDiplomacy.Get(c, patron.Id, player.Id).Stance = DiplomaticStance.Allied;
        c.Wars.Add(new StrategicWar { Id = 2, AttackerId = enemy.Id, DefenderId = player.Id, Name = "Border war" });
        Open(player);
        Open(patron);
        var army = new Army { Id = 3, NationId = player.Id, DirectOrder = true, OrderKind = ArmyOrder.Manual, OrderProvinceId = 9 };
        c.Armies.Add(army);
        var outcome = ArmsShipmentRequests.Submit(c, player.Id, patron.Id);
        Check(outcome.State == ArmsShipmentState.Granted && outcome.Shipped > 0f && c.ArmsShipmentRequests[0].HumanConsent == false
            && c.Sponsorships.Count == 0 && player.Treasury == 12f && patron.Treasury == 12f
            && army.DirectOrder && army.OrderProvinceId == 9 && !c.Wars[0].Involves(patron.Id),
            "an AI donor with a pretext sends one shipment and does not join the war");
    }

    static void ReviewAsksOnlyTheHuman()
    {
        var c = War(out var donor, out _, out _, out _, out _);
        var other = Nation(3, 4, "GER", "Germany", false);
        var foe = Nation(4, 5, "AUS", "Austria", false);
        c.Nations.Add(other);
        c.Nations.Add(foe);
        CampaignDiplomacy.Get(c, other.Id, foe.Id).Stance = DiplomaticStance.Allied;
        c.Wars.Add(new StrategicWar { Id = 9, AttackerId = foe.Id, DefenderId = other.Id, Name = "Other war" });
        Open(other);
        Open(foe);
        Day(c, 14);
        ProxyWars.Tick(c);
        Check(c.ArmsShipmentRequests.Count == 1 && c.ArmsShipmentRequests[0].DonorId == donor.Id
            && c.ArmsShipmentRequests[0].State == ArmsShipmentState.Pending
            && c.ArmsShipmentRequests.All(r => r.DonorId == donor.Id)
            && c.Sponsorships.Count == 0 && donor.Stockpiles["Ammunition"] == 20f,
            "the review puts one paper on the human desk and does not ship between AI countries");
    }

    static void DisablingClosesThePaperWithoutShipping()
    {
        var c = War(out var donor, out _, out _, out _, out _);
        var filed = ArmsShipmentRequests.Submit(c, 1, donor.Id, 4);
        c.SetFeature("proxy_wars", false);
        Day(c, 15);
        var command = new RequestArmsShipmentCommand { IssuerId = 1, DonorId = donor.Id, WarId = 4 };
        c.Commands.Enqueue(command);
        c.Commands.ApplyAll(c);
        ProxyWars.Tick(c);
        Check(command.Outcome.RequestId == 0 && command.Outcome.Reason == "Arms shipments are not available."
            && c.ArmsShipmentRequests.Single(r => r.Id == filed.RequestId).State == ArmsShipmentState.Invalidated
            && donor.Stockpiles["Ammunition"] == 20f && c.Sponsorships.Count == 0,
            "switching proxy wars off refuses a new paper and closes the old one without a shipment");
    }

    static void MissingSaveFieldsStayEmpty()
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        var saved = new ArmsShipmentRequest
        {
            Id = 3, RequesterId = 1, DonorId = 0, WarId = 4, State = ArmsShipmentState.Granted,
            Shipped = 1.25f, Pretext = "The war is on your border.", Share = 0.25f, Days = 14, HumanConsent = true
        };
        var json = JsonSerializer.Serialize(new List<ArmsShipmentRequest> { saved }, options);
        var loaded = JsonSerializer.Deserialize<List<ArmsShipmentRequest>>(json, options);
        var round = new Campaign(1);
        ArmsShipmentRequests.Restore(round, loaded, null, 4);
        Check(round.ArmsShipmentRequests.Count == 1 && round.ArmsShipmentRequests[0].Shipped == 1.25f
            && round.ArmsShipmentRequests[0].Pretext == "The war is on your border." && round.NextArmsShipmentRequestId == 4,
            "a granted shipment round-trips with its pretext and amount");
        var empty = new Campaign(1);
        ArmsShipmentRequests.Restore(empty, null, null, 0);
        Check(empty.ArmsShipmentRequests.Count == 0 && empty.ArmsShipmentCooldowns.Count == 0 && empty.NextArmsShipmentRequestId == 1,
            "a missing shipment list loads as no requests");
        var saveType = typeof(SaveSystem).GetNestedType("CampaignSave", BindingFlags.NonPublic);
        var payload = JsonSerializer.Deserialize("{}", saveType, options);
        var restored = (Campaign)saveType.GetMethod("Restore").Invoke(payload, null);
        Check(restored.ArmsShipmentRequests.Count == 0 && restored.NextArmsShipmentRequestId == 1,
            "an old save payload with no shipment fields does not invent any");
    }
}
