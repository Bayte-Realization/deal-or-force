using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class TerritorialDealProtectionTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception("territorial deals: " + message); Console.WriteLine("PASS territorial deals: " + message); }
    public static void Run()
    {
        var c = new Campaign(20261018) { ScenarioId = "homeland_sale" };
        var buyer = new Nation { Id = 0, MapIndex = 501, Tag = "PRK", Name = "North Korea", Population = 2000000, Treasury = 10000, Gdp = 40 };
        var seller = new Nation { Id = 1, MapIndex = 502, Tag = "KOR", Name = "South Korea", Population = 2000000, Treasury = 100, Gdp = 1600 };
        c.Nations.Add(buyer); c.Nations.Add(seller);
        var states = new ushort[32]; var owners = new ushort[32];
        for (var i = 0; i < 4; i++)
        {
            var n = i < 2 ? buyer : seller;
            var s = new ScenarioState { Id = "sale_" + i, Name = i == 2 ? "Gangwon" : "Region " + i,
                MapIndex = 3001 + i, NationMapIndex = n.MapIndex, NationId = n.Tag, Population = 1000000 };
            s.PopulationGroups.Add(new PopulationGroup { Culture = "korean", Size = 1000000 });
            c.States.Add(s);
        }
        for (var y = 0; y < 4; y++) for (var x = 0; x < 8; x++)
        { states[y * 8 + x] = (ushort)(3001 + x / 2); owners[y * 8 + x] = (ushort)(x < 4 ? 501 : 502); }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 8, 4, _ => false));
        buyer.CapitalStateMapIndex = 3001; seller.CapitalStateMapIndex = 3004;
        c.Homelands[seller.Id] = new List<int> { 3003, 3004 };
        var land = c.States.Single(s => s.MapIndex == 3003);
        var deal = new Ultimatum.Deal(new Ultimatum.Side { Cash = 1000 }, new Ultimatum.Side { Regions = new[] { 3003 } });
        var before = CampaignFingerprint.Of(c);
        var result = Ultimatum.AssessDeal(c, buyer, seller, deal);
        Check(result.Valid && !result.Acceptable && result.TerritoryProtected, "AI refuses a cash-only sale of its own populated homeland even above the old price");
        Check(CampaignFingerprint.Of(c) == before, "assessment changes neither world nor diplomatic memory");
        Check(Ultimatum.CashToBalance(c, buyer, seller, deal) < 0, "cash balancer cannot buy away a non-financial refusal");
        Check(!Ultimatum.ResolveDeal(c, buyer, seller, deal).Accepted && before == CampaignFingerprint.Of(c), "refused settlement transfers no cash or land");
        Check(!AiStatecraft.TryBuyKindredLand(c, buyer), "shared Korean culture is not spare South Korean land for the buyer");
        c.Defines.DealProtectHomelandSales = false;
        Check(Ultimatum.DealAcceptable(c, buyer, seller, deal), "a mod can explicitly restore cash-only homeland sales");
        c.Defines.DealProtectHomelandSales = true;
        land.PopulationGroups.Clear(); land.PopulationGroups.Add(new PopulationGroup { Culture = "polish", Size = 1000000 });
        Check(!Ultimatum.DealAcceptable(c, buyer, seller, deal), "a multiethnic homeland is not disposable because its majority differs from the state's primary culture");
        c.Defines.DealProtectCivicHomelandSales = false;
        Check(Ultimatum.DealAcceptable(c, buyer, seller, deal), "a mod can explicitly select the older culture-only homeland protection");
        c.Defines.DealProtectCivicHomelandSales = true;
        c.Homelands[seller.Id].Remove(3003);
        Check(Ultimatum.DealAcceptable(c, buyer, seller, deal), "foreign acquisitions without a homeland title remain negotiable");
        c.Homelands[seller.Id].Add(3003);
        land.PopulationGroups.Clear(); land.PopulationGroups.Add(new PopulationGroup { Culture = "korean", Size = 1000000 });
        var swap = new Ultimatum.Deal(new Ultimatum.Side { Regions = new[] { 3002 }, Cash = 1000 }, deal.To);
        Check(!Ultimatum.AssessDeal(c, buyer, seller, swap).TerritoryProtected, "real territorial swaps keep the existing valuation rather than a blanket ban");
        seller.IsHuman = true;
        Check(!Ultimatum.AssessNegotiatedDeal(c, buyer, seller, deal).TerritoryProtected, "a human government retains explicit consent to its own terms");
    }
}

