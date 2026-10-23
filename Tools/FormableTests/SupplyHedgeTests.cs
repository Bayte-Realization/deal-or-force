using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

static class SupplyHedgeTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("supply hedge: " + name);
        Console.WriteLine("PASS supply hedge: " + name);
    }

    public static void Run()
    {
        DefaultsAddNoFingerprintBytes();
        TheIndexMatchesAScanAndReadingDoesNotRebuildIt();
        TheShortageNamesTheSameSupplierAsTheExposureCard();
        AnAiSignsWithTheOtherSupplier();
        APlayerKeepsTheLandUntilTheySign();
        TheRuleCanBeSwitchedOff();
        SupplierAttributionIsAuthoritativeState();
        AReloadMakesTheSameHedgeDecision();
        LegacyAndRemappedAttributionStayExplicit();
    }

    static Campaign World(out Nation buyer, out Nation monopolist, out Nation alternate)
    {
        var campaign = new Campaign(21);
        buyer = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "BUY", Name = "Buyer", Population = 2_000_000,
            Stability = 0.8f, Gdp = 100f, Treasury = 80f,
            CapitalLatitude = 52f, CapitalLongitude = 13f
        };
        monopolist = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "MON", Name = "Monopolist", Population = 2_000_000,
            Stability = 0.8f, Gdp = 100f, Treasury = 80f,
            CapitalLatitude = 52.4f, CapitalLongitude = 13.2f
        };
        alternate = new Nation
        {
            Id = 2, MapIndex = 3, Tag = "ALT", Name = "Alternate", Population = 2_000_000,
            Stability = 0.8f, Gdp = 100f, Treasury = 80f,
            CapitalLatitude = -33f, CapitalLongitude = 151f
        };
        campaign.Nations.Add(buyer);
        campaign.Nations.Add(monopolist);
        campaign.Nations.Add(alternate);
        Check(buyer.Id == 0 && campaign.Nations[0] == buyer, "nation id 0 stays at index 0");
        buyer.Market[Commodity.Food] = new MarketQuote { Commodity = Commodity.Food, Production = 0f, Demand = 10f, Price = 1f };
        monopolist.Market[Commodity.Food] = new MarketQuote { Commodity = Commodity.Food, Production = 40f, Demand = 1f, Price = 1f };
        alternate.Market[Commodity.Food] = new MarketQuote { Commodity = Commodity.Food, Production = 15f, Demand = 1f, Price = 1f };
        var bond = CampaignDiplomacy.Get(campaign, buyer.Id, monopolist.Id);
        bond.TradeAgreement = true;
        bond.Opinion = 80f;
        bond.LastActionDay = -30;
        var warm = CampaignDiplomacy.Get(campaign, buyer.Id, alternate.Id);
        warm.Opinion = 50f;
        warm.LastActionDay = -30;
        var states = new ushort[] { 9001, 9002, 9003, 9001 };
        var owners = new ushort[] { 1, 2, 3, 1 };
        var map = ProvinceTopology.Build(states, owners, 2, 2, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap)).SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController)).SetValue(campaign, (ushort[])map.ProvinceToNation.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation)).SetValue(campaign, new float[map.ProvinceCount + 1]);
        Check(campaign.HasOwnedLand(buyer) && campaign.HasOwnedLand(monopolist) && campaign.HasOwnedLand(alternate),
            "each country keeps a province of its own");
        return campaign;
    }

    static void DefaultsAddNoFingerprintBytes()
    {
        var left = new Campaign(3);
        var right = new Campaign(3);
        Check(left.Defines.AiSupplyHedges && left.Defines.AiSupplyHedgeShare == 0.75f &&
            left.Defines.AiSupplyHedgeSatisfaction == 0.875f && left.Defines.AiSupplyHedgeRetryDays == 180,
            "the hedge numbers load as the compiled defaults");
        var before = CampaignFingerprint.Of(left);
        Check(before == CampaignFingerprint.Of(right), "two untouched worlds still match");
        left.Defines.AiSupplyHedges = false;
        Check(CampaignFingerprint.Of(left) != before, "switching the hedge off is visible to peers");
        left.Defines.AiSupplyHedges = true;
        Check(CampaignFingerprint.Of(left) == before, "the default hedge rule adds no fingerprint bytes");
    }

    static void TheIndexMatchesAScanAndReadingDoesNotRebuildIt()
    {
        var campaign = World(out var buyer, out var monopolist, out _);
        var builds = TradeNetwork.ImportIndexBuilds;
        TradeNetwork.Route(campaign);
        Check(TradeNetwork.ImportIndexBuilds == builds + 1, "one finished market week files the routes once");
        Check(TradeNetwork.IndexReady(campaign), "the index is ready after the week closes");
        var filed = TradeNetwork.RoutesTouching(campaign, buyer.Id);
        var scanned = campaign.TradeRoutes.Where(route => route.SellerId == buyer.Id || route.BuyerId == buyer.Id).ToArray();
        Check(filed.Count == scanned.Length, "the filed list has the same routes a full scan would find");
        for (var index = 0; index < scanned.Length; index++)
            Check(ReferenceEquals(filed[index], scanned[index]), "filed route " + index + " is the same record as the scan");
        Check(filed.Count > 0 && filed.All(route => route.SellerId == monopolist.Id),
            "the buyer's food comes from the near partner with the trade agreement");
        var other = new Campaign(22);
        other.Nations.Add(new Nation { Id = 0, MapIndex = 1, Tag = "ISO_A", Name = "A", Population = 1_000_000, Treasury = 10f });
        other.Nations.Add(new Nation { Id = 1, MapIndex = 2, Tag = "ISO_B", Name = "B", Population = 1_000_000, Treasury = 10f });
        other.Nations[0].Market[Commodity.Coal] = new MarketQuote { Commodity = Commodity.Coal, Production = 20f, Demand = 1f, Price = 1f };
        other.Nations[1].Market[Commodity.Coal] = new MarketQuote { Commodity = Commodity.Coal, Production = 0f, Demand = 10f, Price = 1f };
        TradeNetwork.Route(other);
        var still = TradeNetwork.RoutesTouching(campaign, buyer.Id);
        Check(still.Count == filed.Count && ReferenceEquals(still[0], filed[0]) && still[0].SellerId == monopolist.Id,
            "another market does not rewrite this country's filed routes");
        var after = TradeNetwork.RouteScans;
        for (var pass = 0; pass < 8; pass++)
        {
            _ = TradeNetwork.RoutesTouching(campaign, buyer.Id);
            _ = EconomicExposure.Read(campaign, buyer);
            _ = EconomicExposure.Cause(campaign, buyer, Commodity.Food);
        }
        Check(TradeNetwork.RouteScans == after, "reading the filed routes does not walk the world list");
        Check(TradeNetwork.ImportIndexBuilds == builds + 2, "those reads do not file the week again");
    }

    static void TheShortageNamesTheSameSupplierAsTheExposureCard()
    {
        var campaign = World(out var buyer, out var monopolist, out _);
        TradeNetwork.Route(campaign);
        var quote = buyer.Market[Commodity.Food];
        quote.Satisfaction = quote.Demand <= 0.001f ? 1f : Math.Clamp((quote.Production + quote.Imports) / quote.Demand, 0f, 1f);
        var row = EconomicExposure.Read(campaign, buyer).Single(item => item.Good == Commodity.Food);
        Check(row.Suppliers[0].NationId == monopolist.Id && row.LargestImportShare + 0.0001f >= 0.75f,
            "the exposure card's largest supplier is the monopolist (" + row.LargestImportShare.ToString("0.00") + ")");
        var language = Loc.Language;
        var before = CampaignFingerprint.Of(campaign);
        try
        {
            Loc.Language = "english";
            var expected = Loc.Format("{0} supplies {1:P0} of the imports of this good.", monopolist.DisplayName, row.LargestImportShare);
            Check(EconomicExposure.Cause(campaign, buyer, Commodity.Food) == expected,
                "the cause uses the exposure card's supplier and share");
            quote.Satisfaction = 0.4f;
            var item = SituationReport.Read(campaign, buyer).Single(entry => entry.Id == "shortage:Food");
            Check(item.Causes.Length == 2 && item.Causes[1] == expected && item.Facts.Length == 4,
                "the situation room names that supplier and keeps the same facts");
            Check(!item.Causes[1].Contains("trade bottleneck", StringComparison.Ordinal),
                "the long undifferentiated shortage sentence is gone");
            Loc.Language = "russian";
            var russian = EconomicExposure.Cause(campaign, buyer, Commodity.Food);
            Check(russian.Contains(monopolist.DisplayName, StringComparison.Ordinal) &&
                russian.Contains("импорта", StringComparison.Ordinal),
                "the Russian cause names the supplier");
            var again = SituationReport.Read(campaign, buyer).Single(entry => entry.Id == "shortage:Food");
            Check(again.Causes[1] == russian, "the situation room uses that same Russian sentence");
        }
        finally { Loc.Language = language; }
        Check(CampaignFingerprint.Of(campaign) == before, "reading the shortage does not change the fingerprint");
    }

    static void AnAiSignsWithTheOtherSupplier()
    {
        var campaign = World(out var buyer, out var monopolist, out var alternate);
        TradeNetwork.Route(campaign);
        var quote = buyer.Market[Commodity.Food];
        quote.Satisfaction = 0.4f;
        var owners = (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone();
        var cash = buyer.Treasury + monopolist.Treasury + alternate.Treasury;
        Check(AiStatecraft.TryHedgeSupply(campaign, buyer), "the short buyer asks for a second supplier");
        var withAlternate = CampaignDiplomacy.Peek(campaign, buyer.Id, alternate.Id);
        var withMonopolist = CampaignDiplomacy.Peek(campaign, buyer.Id, monopolist.Id);
        Check(withAlternate?.TradeAgreement == true, "the agreement is with the other supplier");
        Check(withMonopolist?.TradeAgreement == true, "the existing agreement with the monopolist stays");
        Check(campaign.DealOffers.Count == 0, "two governments settle the agreement without a leftover paper");
        Check(campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owners), "no province changes hands");
        Check(buyer.Treasury + monopolist.Treasury + alternate.Treasury == cash, "no cash moves");
        Check(!AiStatecraft.TryHedgeSupply(campaign, buyer), "the same good is not hedged again inside the pause");
        Check(buyer.DecisionAvailableDay["supply_hedge/" + (int)Commodity.Food] > campaign.ElapsedDays,
            "the pause is on the book");
        var plain = World(out var quiet, out _, out _);
        TradeNetwork.Route(plain);
        plain.NationById(0).Market[Commodity.Food].Satisfaction = 0.4f;
        var hashed = CampaignFingerprint.Of(plain);
        quiet.DecisionAvailableDay["supply_hedge/" + (int)Commodity.Food] = 180;
        Check(CampaignFingerprint.Of(plain) != hashed, "a hedge clock is part of the fingerprint");
    }

    static void APlayerKeepsTheLandUntilTheySign()
    {
        var campaign = World(out var buyer, out _, out var alternate);
        alternate.IsHuman = true;
        TradeNetwork.Route(campaign);
        buyer.Market[Commodity.Food].Satisfaction = 0.4f;
        var owners = (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone();
        var buyerCash = buyer.Treasury;
        var alternateCash = alternate.Treasury;
        Check(AiStatecraft.TryHedgeSupply(campaign, buyer), "the paper is offered to the player who can sell");
        var paper = campaign.DealOffers.Single(offer => offer.Pending && offer.ToId == alternate.Id);
        Check(paper.From.TradeAgreement && !paper.To.TradeAgreement && paper.From.Cash == 0f && paper.To.Cash == 0f &&
            (paper.From.Regions?.Length ?? 0) == 0 && (paper.To.Regions?.Length ?? 0) == 0 &&
            paper.FromProvinces.Length == 0 && paper.ToProvinces.Length == 0,
            "the paper is a trade agreement and no land");
        Check(CampaignDiplomacy.Peek(campaign, buyer.Id, alternate.Id)?.TradeAgreement != true,
            "nothing is signed before the player agrees");
        Check(buyer.Treasury == buyerCash && alternate.Treasury == alternateCash, "treasuries wait for the signature");
        Check(campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owners), "the player's provinces stay");
        var signed = DealNegotiations.Respond(campaign, alternate.Id, paper.Id, DealReply.Accept);
        Check(signed.Accepted, "the player can sign the agreement (" + signed.Note + ")");
        Check(CampaignDiplomacy.Peek(campaign, buyer.Id, alternate.Id)?.TradeAgreement == true, "signing writes the agreement");
        Check(buyer.Treasury == buyerCash && alternate.Treasury == alternateCash, "signing the agreement moves no cash");
        Check(campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owners), "signing the agreement moves no land");
    }

    static void TheRuleCanBeSwitchedOff()
    {
        var campaign = World(out var buyer, out _, out var alternate);
        campaign.Defines.AiSupplyHedges = false;
        TradeNetwork.Route(campaign);
        buyer.Market[Commodity.Food].Satisfaction = 0.4f;
        Check(!AiStatecraft.TryHedgeSupply(campaign, buyer), "the switch leaves the week alone");
        Check(CampaignDiplomacy.Peek(campaign, buyer.Id, alternate.Id)?.TradeAgreement != true, "no agreement is written");
        Check(campaign.DealOffers.Count == 0, "no paper is sent");
    }

    static void SupplierAttributionIsAuthoritativeState()
    {
        var c = World(out var buyer, out var seller, out var alternate);
        var unknown = CampaignFingerprint.Of(c);
        c.TradeRoutesDay = 0;
        var emptyWeek = CampaignFingerprint.Of(c);
        Check(emptyWeek != unknown, "a known empty market week differs from unavailable legacy attribution");
        var route = new TradeRoute { SellerId = seller.Id, BuyerId = buyer.Id, Commodity = Commodity.Food, Volume = 3.25f, Value = 4.125f };
        c.TradeRoutes.Add(route);
        var attributed = CampaignFingerprint.Of(c);
        Check(attributed != emptyWeek, "supplier attribution contributes to the campaign fingerprint");
        void Changed(Action change, Action reset, string label)
        {
            change(); Check(CampaignFingerprint.Of(c) != attributed, label); reset();
            Check(CampaignFingerprint.Of(c) == attributed, "restoring " + label + " restores the fingerprint");
        }
        Changed(() => c.TradeRoutesDay++, () => c.TradeRoutesDay--, "the settlement day");
        Changed(() => route.SellerId = alternate.Id, () => route.SellerId = seller.Id, "the supplier");
        Changed(() => route.BuyerId = alternate.Id, () => route.BuyerId = buyer.Id, "the buyer");
        Changed(() => route.Commodity = Commodity.Coal, () => route.Commodity = Commodity.Food, "the good");
        Changed(() => route.Volume = 3.5f, () => route.Volume = 3.25f, "the exact volume");
        Changed(() => route.Value = 4.25f, () => route.Value = 4.125f, "the exact value");
        c.TradeRoutes.Add(new TradeRoute { SellerId = alternate.Id, BuyerId = buyer.Id, Commodity = Commodity.Food, Volume = 1.25f, Value = 2.5f });
        var ordered = CampaignFingerprint.Of(c);
        c.TradeRoutes.Reverse();
        Check(CampaignFingerprint.Of(c) != ordered, "the fingerprint preserves route summation order rather than sorting it");
        c.TradeRoutes.Reverse();
        var rng = c.Random.State;
        for (var pass = 0; pass < 3; pass++) _ = EconomicExposure.Read(c, buyer);
        Check(CampaignFingerprint.Of(c) == ordered && c.Random.State == rng, "reading restored-style attribution is read-only");
    }

    static void AReloadMakesTheSameHedgeDecision()
    {
        foreach (var concentrated in new[] { false, true })
        {
            var c = World(out var buyer, out var seller, out var alternate);
            c.ConfigureSinglePlayer(seller.Id);
            // This is a current campaign, not a pre-output/debt migration fixture.
            // Legacy output calibration legitimately primes a new market on load.
            foreach (var nation in c.Nations) { nation.OutputScale = 1f; nation.DebtInitialized = true; }
            // First market, day zero is significant: it is not the unavailable -1 default.
            // A diversified shortage must not turn into an unattributed-import hedge on reload.
            c.TradeRoutesDay = 0;
            c.TradeRoutes.Add(new TradeRoute { SellerId = seller.Id, BuyerId = buyer.Id, Commodity = Commodity.Food, Volume = concentrated ? 8f : 4f, Value = 9.125f });
            if (!concentrated)
                c.TradeRoutes.Add(new TradeRoute { SellerId = alternate.Id, BuyerId = buyer.Id, Commodity = Commodity.Food, Volume = 4f, Value = 4.75f });
            buyer.Market[Commodity.Food].Imports = 8f;
            buyer.Market[Commodity.Food].Satisfaction = 0.4f;
            var random = c.Random.State;
            var date = c.Date;
            var snapshot = SaveSystem.ExportNetworkSnapshot(c, "hedge-state");
            using (var wire = new MemoryStream(snapshot))
            using (var unpack = new GZipStream(wire, CompressionMode.Decompress))
            {
                var payload = JsonNode.Parse(unpack).AsObject();
                Check(payload["TradeRoutesDay"]?.GetValue<int>() == c.TradeRoutesDay &&
                    payload["TradeRoutes"]?.AsArray().Count == c.TradeRoutes.Count,
                    "snapshot JSON carries attribution day=" + payload["TradeRoutesDay"] + " routes=" + payload["TradeRoutes"]?.AsArray().Count);
            }
            var result = SaveSystem.ImportNetworkSnapshot(snapshot, "hedge-state");
            Check(result.Success, "supplier week imports from snapshot: " + result.Warning);
            var peer = result.Campaign;
            CheckAttribution(c, peer, "snapshot");
            // The tiny test map is attached directly like World(), without initial deployment.
            typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap)).SetValue(peer, c.ProvinceMap);
            typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController)).SetValue(peer, (ushort[])c.ProvinceController.Clone());
            typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation)).SetValue(peer, (float[])c.ProvinceOccupation.Clone());
            var sourceRow = EconomicExposure.Read(c, buyer).Single(row => row.Good == Commodity.Food);
            var peerRow = EconomicExposure.Read(peer, peer.NationById(buyer.Id)).Single(row => row.Good == Commodity.Food);
            Check(sourceRow.LargestImportShare == peerRow.LargestImportShare && sourceRow.Suppliers.SequenceEqual(peerRow.Suppliers),
                "the restored exposure has the same suppliers and concentration");
            var sourceDecision = AiStatecraft.TryHedgeSupply(c, buyer);
            var peerBuyer = peer.NationById(buyer.Id);
            var peerDecision = AiStatecraft.TryHedgeSupply(peer, peerBuyer);
            Check(sourceDecision == concentrated && peerDecision == sourceDecision,
                concentrated ? "both worlds hedge the actual monopoly" : "neither world hedges an already diversified shortage");
            Check(CampaignDiplomacy.Peek(c, buyer.Id, alternate.Id)?.TradeAgreement ==
                CampaignDiplomacy.Peek(peer, buyer.Id, alternate.Id)?.TradeAgreement &&
                buyer.DecisionAvailableDay.GetValueOrDefault("supply_hedge/" + (int)Commodity.Food) ==
                peerBuyer.DecisionAvailableDay.GetValueOrDefault("supply_hedge/" + (int)Commodity.Food),
                "the agreement and retry clock agree after the snapshot");
            Check(c.Random.State == random && peer.Random.State == random && c.Date.Equals(date) && peer.Date.Equals(date) &&
                c.ElapsedDays == 0 && peer.ElapsedDays == 0, "saving, importing and hedging do not advance time or consume randomness");

            var root = Path.Combine(Path.GetTempPath(), "dof-supply-week-" + Guid.NewGuid().ToString("N"));
            try
            {
                SaveSystem.Save(root, "week", c, "hedge-state");
                var disk = SaveSystem.Load(root, "week", "hedge-state");
                Check(disk.Success, "supplier week loads from disk: " + disk.Warning);
                CheckAttribution(c, disk.Campaign, "disk save");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    static void CheckAttribution(Campaign source, Campaign restored, string label)
    {
        Check(restored.TradeRoutesDay == source.TradeRoutesDay && restored.TradeRoutes.Count == source.TradeRoutes.Count,
            label + " retains the settled day and every route (expected " + source.TradeRoutesDay + "/" + source.TradeRoutes.Count +
            ", actual " + restored.TradeRoutesDay + "/" + restored.TradeRoutes.Count + ")");
        for (var index = 0; index < source.TradeRoutes.Count; index++)
        {
            var left = source.TradeRoutes[index]; var right = restored.TradeRoutes[index];
            Check(left.SellerId == right.SellerId && left.BuyerId == right.BuyerId && left.Commodity == right.Commodity &&
                BitConverter.SingleToInt32Bits(left.Volume) == BitConverter.SingleToInt32Bits(right.Volume) &&
                BitConverter.SingleToInt32Bits(left.Value) == BitConverter.SingleToInt32Bits(right.Value) && !ReferenceEquals(left, right),
                label + " retains independent route " + index + " in exact order with lossless quantities");
        }
    }

    static void LegacyAndRemappedAttributionStayExplicit()
    {
        var c = World(out var buyer, out var seller, out _);
        c.ConfigureSinglePlayer(seller.Id);
        foreach (var nation in c.Nations) { nation.OutputScale = 1f; nation.DebtInitialized = true; }
        TradeNetwork.Route(c);
        using var input = new MemoryStream(SaveSystem.ExportNetworkSnapshot(c, "hedge-state"));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var payload = JsonNode.Parse(gzip).AsObject();
        byte[] Wire(JsonObject document)
        {
            using var output = new MemoryStream();
            using (var compress = new GZipStream(output, CompressionLevel.Fastest, true))
                compress.Write(Encoding.UTF8.GetBytes(document.ToJsonString()));
            return output.ToArray();
        }
        var old = (JsonObject)payload.DeepClone();
        old.Remove("TradeRoutes"); old.Remove("TradeRoutesDay");
        var legacy = SaveSystem.ImportNetworkSnapshot(Wire(old), "hedge-state");
        Check(legacy.Success && legacy.Campaign.TradeRoutesDay == -1 && legacy.Campaign.TradeRoutes.Count == 0,
            "older files keep attribution explicitly unavailable, not a fabricated market week");
        Check(legacy.Campaign.NationById(buyer.Id).Market[Commodity.Food].Imports == buyer.Market[Commodity.Food].Imports,
            "legacy import totals are not rewritten while attribution is unavailable");
        var mappedGood = CommodityCatalog.IdOf(Commodity.Food);
        payload["GoodIds"][mappedGood] = 777;
        foreach (var route in payload["TradeRoutes"].AsArray()) route["Commodity"] = 777;
        var remapped = SaveSystem.ImportNetworkSnapshot(Wire(payload), "hedge-state");
        Check(remapped.Success && remapped.Campaign.TradeRoutes.Count == c.TradeRoutes.Count &&
            remapped.Campaign.TradeRoutes.All(route => route.Commodity == Commodity.Food),
            "saved commodity identities remap route goods before attribution can be read");
    }
}
