using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class RecruitmentPlacementTests
{
    private static void Check(bool condition, string caption)
    { if (!condition) throw new Exception("recruitment placement: " + caption); Console.WriteLine("PASS recruitment placement: " + caption); }

    private static (Campaign Campaign, Nation Owner, Nation Other, ScenarioState Home, ScenarioState Foreign) Fixture()
    {
        const int width = 64, height = 32;
        var states = new ushort[width * height]; var owners = new ushort[states.Length]; var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var province = x / 16 + 1; var index = y * width + x;
            states[index] = (ushort)(3000 + province); owners[index] = province < 4 ? (ushort)11 : (ushort)12;
            rgb[index * 3 + 2] = (byte)province;
        }
        var map = ProvinceTopology.FromColorRaster(states, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 4).ToDictionary(id => id, id => id));
        var campaign = new Campaign(61107) { ScenarioId = "recruitment_placement_fixture" };
        var owner = new Nation { Id = 0, Tag = "REC", MapIndex = 11, Population = 100_000_000, Gdp = 100,
            Treasury = 1_000_000, ManpowerReserve = 10_000_000, Stability = .8f };
        var other = new Nation { Id = 1, Tag = "OTHER", MapIndex = 12, Population = 10_000_000, Gdp = 20 };
        campaign.Nations.Add(owner); campaign.Nations.Add(other);
        for (var province = 1; province <= 4; province++)
            campaign.States.Add(new ScenarioState { Id = "training_" + province, Name = "Training " + province,
                MapIndex = 3000 + province, NationMapIndex = province < 4 ? 11 : 12,
                NationId = province < 4 ? owner.Tag : other.Tag, Population = 10_000_000 });
        campaign.AttachProvinceTopology(map);
        return (campaign, owner, other, campaign.States[0], campaign.States[3]);
    }

    // Recruitment is not part of the current fingerprint. Include its queue/id explicitly,
    // and include depot initialization independently of whether the fingerprint sees a store.
    private static string Snapshot(Campaign c) => CampaignFingerprint.Of(c) + ":" + c.NextRecruitmentId + ":" +
        JsonSerializer.Serialize(c.Recruitment, new JsonSerializerOptions { IncludeFields = true }) + ":" +
        string.Join(";", c.Nations.Select(n => n.Id + ":" + n.Treasury + ":" + n.ManpowerReserve + ":" + n.StockpilesSeeded + ":" +
            string.Join(",", n.Stockpiles.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key + "=" + item.Value)))) + ":" +
        c.Armies.Count + ":" + c.Commands.Count + ":" + c.Random.State + ":" + c.ElapsedHours;

    public static void Run()
    {
        foreach (var mode in Enum.GetValues<MobilisationMode>()) QuoteAndAdmission(mode);
        FirstStandardHullOrder();
        RejectedOrders();
        TransportKeepsAuthority();
        ModdableFeatureBinding();
    }

    private static void QuoteAndAdmission(MobilisationMode mode)
    {
        var (c, owner, _, home, _) = Fixture();
        var template = UnitTypeCatalog.Get("line_infantry");
        // An old/custom save with one authored empty store and the other stores uninitialized.
        owner.Stockpiles["Arms"] = 0;
        var before = Snapshot(c);
        var quote = RecruitmentSystem.TotalBatchCost(c, owner, template, 2, mode, out var surcharge);
        Check(quote > 0 && surcharge > 0 && RecruitmentSystem.Validate(c, owner.Id, home.MapIndex, template.Id, 2, mode: mode) == "",
            mode + " quote includes missing equipment and accepts an affordable owned region");
        var bill = StrategicStockpiles.DivisionMaterialBill(template, 2, mode);
        var stores = bill.ToDictionary(item => item.Key, item => StrategicStockpiles.Peek(c, owner, item.Key));
        Check(Snapshot(c) == before && !owner.StockpilesSeeded,
            mode + " cost, validation and material previews preserve the unseeded campaign and queue/id");
        StrategicStockpiles.EnsureSeeded(c, owner);
        Check(MathF.Abs(quote - RecruitmentSystem.TotalBatchCost(c, owner, template, 2, mode, out _)) < .0001f,
            mode + " read-only quote equals the initialized depot quote");
        // Test first-command initialization as well as parity, rather than letting the test's
        // seeding hide a regression in standard recruitment's material consumption.
        owner.Stockpiles.Clear(); owner.Stockpiles["Arms"] = 0; owner.StockpilesSeeded = false;
        var treasury = owner.Treasury; var manpower = owner.ManpowerReserve; var id = c.NextRecruitmentId;
        var command = new RaiseArmyCommand { IssuerId = owner.Id, StateMapIndex = home.MapIndex,
            TemplateId = template.Id, Divisions = 2, Mode = mode };
        c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
        var order = c.Recruitment.Single();
        Check(command.FailureReason == "" && c.Armies.Count == 0 && order.Id == id && c.NextRecruitmentId == id + 1 &&
            order.NationId == owner.Id && order.StateMapIndex == home.MapIndex && order.TemplateId == template.Id &&
            order.Count == 2 && order.Mode == mode && order.DurationDays == RecruitmentSystem.TrainingDaysOf(owner, template, mode),
            mode + " command queues exactly one matching training batch without instant armies");
        Check(MathF.Abs(owner.Treasury - (treasury - quote)) < .01f && manpower - owner.ManpowerReserve == template.RecruitmentPersonnel * 2 &&
            MathF.Abs(order.PaidCost - quote) < .0001f && owner.StockpilesSeeded,
            mode + " accepted command charges the displayed full bill and manpower");
        foreach (var item in bill)
            Check(MathF.Abs(owner.Stockpiles[item.Key] - MathF.Max(0, MathF.Round(stores[item.Key] - item.Value, 3))) < .0001f,
                mode + " admitted batch consumes its actual " + item.Key + " depot bill");
    }

    private static void RejectedOrders()
    {
        foreach (var refusal in new[] { "foreign", "occupied", "treasury", "manpower", "queue", "template", "fraction", "zero", "too_many", "disabled" })
        {
            var (c, owner, other, home, foreign) = Fixture();
            var state = home.MapIndex; var template = "line_infantry"; var count = 1f;
            switch (refusal)
            {
                case "foreign": state = foreign.MapIndex; break;
                case "occupied":
                    for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
                        if (c.ProvinceMap.ProvinceToState[p] == home.MapIndex) c.SetProvinceController(p, (ushort)other.MapIndex);
                    break;
                case "treasury": owner.Treasury = 0; break;
                case "manpower": owner.ManpowerReserve = 0; break;
                case "queue": c.Defines.RecruitmentBatchCap = 0; break;
                case "template": template = "missing_mod_template"; break;
                case "fraction": count = 1.5f; break;
                case "zero": count = 0; break;
                case "too_many": count = RecruitmentSystem.MaxDivisionsPerBatch(c, owner, home) + 1; break;
                case "disabled": c.SetFeature("recruitment", false); break;
            }
            var before = Snapshot(c);
            if (float.IsFinite(count) && count == MathF.Floor(count))
                Check(RecruitmentSystem.Validate(c, owner.Id, state, template, (int)count).Length > 0,
                    refusal + " has an admission explanation before a map command is issued");
            var command = new RaiseArmyCommand { IssuerId = owner.Id, StateMapIndex = state, TemplateId = template, Divisions = count };
            c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
            Check(Snapshot(c) == before && !owner.StockpilesSeeded && owner.Stockpiles.Count == 0,
                refusal + " rejection changes no campaign field, queue/id or unseeded depot");
        }
    }

    private static void FirstStandardHullOrder()
    {
        var (c, owner, _, _, _) = Fixture();
        c.Defines.SlipwaysPerEstablishedHull = 1f;
        var fleet = new Fleet { Id = 701, NationId = owner.Id, Hulls = 5, Establishment = 5, HomeProvinceId = 1 };
        c.Fleets.Add(fleet);
        var bill = StrategicStockpiles.HullMaterialBill(2, MobilisationMode.Standard);
        var stores = bill.ToDictionary(item => item.Key, item => StrategicStockpiles.Peek(c, owner, item.Key));
        var treasury = owner.Treasury; var stability = owner.Stability;
        var order = new OrderHullsCommand { IssuerId = owner.Id, FleetId = fleet.Id, Count = 2 };
        c.Commands.Enqueue(order); c.Commands.ApplyAll(c);
        Check(order.Refusal == "" && fleet.HullsOrdered == 2 && owner.StockpilesSeeded &&
            owner.Treasury == treasury && owner.Stability == stability,
            "first standard hull order initializes its depot at admission without changing daily funding or stability rules");
        foreach (var item in bill)
            Check(MathF.Abs(owner.Stockpiles[item.Key] - MathF.Max(0, MathF.Round(stores[item.Key] - item.Value, 3))) < .0001f,
                "first standard hull order consumes its " + item.Key + " bill without a prior catalogue read");
        var paidStores = string.Join(";", owner.Stockpiles.OrderBy(item => item.Key, StringComparer.Ordinal));
        c.Commands.Enqueue(new OrderHullsCommand { IssuerId = owner.Id, FleetId = fleet.Id, Count = -2 }); c.Commands.ApplyAll(c);
        Check(fleet.HullsOrdered == 0 && owner.Treasury == treasury && owner.Stability == stability &&
            string.Join(";", owner.Stockpiles.OrderBy(item => item.Key, StringComparer.Ordinal)) == paidStores,
            "negative hull orders keep their existing cancellation behavior without consuming or refunding depot materials");
    }

    private static void TransportKeepsAuthority()
    {
        var (c, owner, _, home, _) = Fixture(); ICampaignCommand sent = null;
        c.Commands.SubmitInterceptor = command => { sent = command; return true; };
        var before = Snapshot(c);
        var command = new RaiseArmyCommand { IssuerId = owner.Id, StateMapIndex = home.MapIndex, Divisions = 1 };
        c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
        Check(ReferenceEquals(sent, command) && Snapshot(c) == before && c.Commands.Count == 0,
            "transport interception submits the recruitment order without local charges or a speculative queue entry");
    }

    private static void ModdableFeatureBinding()
    {
        var (c, owner, _, home, _) = Fixture();
        c.Defines.FeatureCommands[nameof(RaiseArmyCommand)] = "scenario_recruitment";
        c.SetFeature("scenario_recruitment", false);
        var before = Snapshot(c);
        Check(!RecruitmentSystem.Enabled(c) && RecruitmentSystem.Validate(c, owner.Id, home.MapIndex, "line_infantry", 1).Length > 0 &&
            Snapshot(c) == before, "preview respects a scenario's rebound command feature without logging/applying an order");
        c.Defines.FeatureCommands[nameof(RaiseArmyCommand)] = "";
        Check(RecruitmentSystem.Enabled(c), "a mod can intentionally clear the command's feature binding");
    }
}
