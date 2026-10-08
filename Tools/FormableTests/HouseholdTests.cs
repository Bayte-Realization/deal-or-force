using System;
using System.Linq;
using DealOrForce.Simulation;

static class HouseholdTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS household: " + label); }
    public static void Run()
    {
        var c = new Campaign(541); var n = new Nation { Id = 0, MapIndex = 100, Tag = "HH", Treasury = 8, Gdp = 100, StandardOfLiving = .65f };
        var s = new ScenarioState { MapIndex = 401, NationMapIndex = 100, Population = 1000000, StandardOfLiving = .6f, Literacy = .8f };
        s.PopulationGroups.Add(new PopulationGroup { Size = 600000, Employment = .9f, Wealth = .4f, StandardOfLiving = .6f });
        s.PopulationGroups.Add(new PopulationGroup { Size = 400000, Employment = .9f, Wealth = .6f, StandardOfLiving = .6f });
        c.Nations.Add(n); c.States.Add(s);
        var hash = CampaignFingerprint.Of(c); var employment = HouseholdEconomy.Employment(s);
        Check(employment > .89f && hash == CampaignFingerprint.Of(c) && s.NonBuildingJobs == -1, "reading employment never calibrates or mutates a save");
        EconomySystem.AdjustStatePopulation(s, -999999);
        Check(s.Population == 1 && s.PopulationGroups.Sum(g => g.Size) == 1 && s.PopulationGroups.All(g => g.Size >= 0), "large decline preserves exact group totals");
        EconomySystem.AdjustStatePopulation(s, 999999);
        Check(s.PopulationGroups.Sum(g => g.Size) == s.Population, "growth preserves exact group totals");
        DomesticCrisesTests.Day(c, 7); HouseholdEconomy.Review(c,n,s,0);
        var baseline = s.NonBuildingJobs; var treasury = n.Treasury;
        Check(s.ReviewBirths > 0 && s.ReviewDeaths > 0 && s.PopulationGroups.Sum(g => g.Size) == s.Population, "births and deaths are visible and groups stay conserved");
        hash = CampaignFingerprint.Of(c); HouseholdEconomy.Review(c,n,s,0);
        Check(hash == CampaignFingerprint.Of(c), "the same review cannot grow population twice");
        s.Buildings[BuildingKind.CivilianIndustry] = 1;
        DomesticCrisesTests.Day(c,14); HouseholdEconomy.Review(c,n,s,0);
        Check(s.EmploymentTarget == 1 && HouseholdEconomy.Employment(s) > employment && HouseholdEconomy.Employment(s) < 1,
            "new factories improve employment gradually, not in one click");
        n.Market[Commodity.Food] = new MarketQuote { Commodity = Commodity.Food, Satisfaction = .1f };
        n.Market[Commodity.Energy] = new MarketQuote { Commodity = Commodity.Energy, Satisfaction = .2f };
        n.Market[Commodity.Pharmaceuticals] = new MarketQuote { Commodity = Commodity.Pharmaceuticals, Satisfaction = .2f };
        s.WarDamage = .8f; DomesticCrisesTests.Day(c,21); HouseholdEconomy.Review(c,n,s,0);
        var deaths = s.ReviewDeaths; var stress = s.LivelihoodStress;
        Check(stress > .5f && s.BasicNeedsCoverage < .2f && n.Treasury == treasury, "real shortages and local damage stress households without conjuring or charging treasury money");
        n.Market.Clear(); s.WarDamage = 0; DomesticCrisesTests.Day(c,28); HouseholdEconomy.Review(c,n,s,0);
        Check(s.LivelihoodStress < stress && s.ReviewDeaths < deaths && s.NonBuildingJobs == baseline, "supply recovery and reconstruction improve health without a permanent punishment");
        c.Defines.HouseholdDynamicsEnabled = false; DomesticCrisesTests.Day(c,35); hash = CampaignFingerprint.Of(c);
        HouseholdEconomy.Review(c,n,s,0); Check(hash == CampaignFingerprint.Of(c), "mod toggle disables the new adjustment");
    }
}
