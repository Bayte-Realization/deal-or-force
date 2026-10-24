using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Resource Cartel: OPEC-style commodity export cartels, coordinated export quotas,
/// and collective pricing leverage.
/// </summary>
public static class ResourceCartelTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL resource cartel: " + text);
        Console.WriteLine("PASS resource cartel: " + text);
    }

    public static void Run()
    {
        CharterCatalogLoadsCommodityCartel();
        DoctrineCatalogLoadsCartelQuotas();
        CartelNaming();
        CartelPreferredDoctrine();
        FingerprintAndPersistence();
    }

    private static Campaign World()
    {
        var c = new Campaign(55200) { ScenarioId = "resource_cartel_test" };
        c.SetPlayer(0);
        c.Nations.Add(new Nation
        {
            Id = 0, Tag = "EXP1", Name = "Exporter One", CustomName = "Exporter One",
            MapIndex = 1, Population = 10_000_000, Gdp = 500, Treasury = 100,
            Continent = "asia", Region = "middle_east", CapitalName = "Riyadh"
        });
        c.Nations.Add(new Nation
        {
            Id = 1, Tag = "EXP2", Name = "Exporter Two", CustomName = "Exporter Two",
            MapIndex = 2, Population = 8_000_000, Gdp = 400, Treasury = 80,
            Continent = "south_america", Region = "south_america", CapitalName = "Caracas"
        });
        c.Nations.Add(new Nation
        {
            Id = 2, Tag = "EXP3", Name = "Exporter Three", CustomName = "Exporter Three",
            MapIndex = 3, Population = 12_000_000, Gdp = 600, Treasury = 120,
            Continent = "africa", Region = "north_africa", CapitalName = "Algiers"
        });
        return c;
    }

    private static void CharterCatalogLoadsCommodityCartel()
    {
        InternationalOrganizations.ResetCatalog();
        var charter = InternationalOrganizations.Charter("commodity_cartel");
        Check(charter != null, "commodity_cartel charter loaded from data");
        Check(charter.Name == "Commodity export cartel", "charter name matches catalog");
        Check(!charter.Unanimity, "commodity_cartel uses majority voting");
        Check(charter.VoteDays == 14, "commodity_cartel has 14-day voting window");
        Check(MathF.Abs(charter.WeeklyGdpDues - 0.00001f) < 1e-7f, "commodity_cartel weekly GDP dues configured");
    }

    private static void DoctrineCatalogLoadsCartelQuotas()
    {
        var doctrine = InternationalOrganizations.Doctrine("cartel_quotas");
        Check(doctrine != null, "cartel_quotas doctrine loaded from data");
        Check(doctrine.Name == "Coordinated export quotas", "doctrine name matches catalog");
        Check(MathF.Abs(doctrine.Modifiers.GetValueOrDefault("trade_reach") - 0.08f) < 1e-4f,
            "cartel_quotas grants +0.08 trade reach");
        Check(MathF.Abs(doctrine.Modifiers.GetValueOrDefault("output") - 0.03f) < 1e-4f,
            "cartel_quotas grants +0.03 output");
        Check(MathF.Abs(doctrine.Modifiers.GetValueOrDefault("tax_efficiency") - 0.03f) < 1e-4f,
            "cartel_quotas grants +0.03 tax efficiency");
    }

    private static void CartelNaming()
    {
        var c = World();
        var n0 = c.Nations[0];
        var n1 = c.Nations[1];
        var n2 = c.Nations[2];
        var members = new List<Nation> { n0, n1, n2 };

        var name1 = OrganizationDynamics.DetermineOrganizationName(c, "commodity_cartel", n0, n1, members);
        Check(name1 == "org.name.petroleum_exporting_cartel",
            $"cross-continent cartel first names petroleum exporting cartel (got {name1})");

        var org1 = InternationalOrganizations.Seed(c, name1, "commodity_cartel", new[] { n0.Id, n1.Id });
        Check(org1 != null, "first cartel seeded successfully");

        var name2 = OrganizationDynamics.DetermineOrganizationName(c, "commodity_cartel", n0, n2, members);
        Check(name2 == "org.name.critical_minerals_cartel",
            $"second cross-continent cartel names critical minerals syndicate (got {name2})");

        var org2 = InternationalOrganizations.Seed(c, name2, "commodity_cartel", new[] { n0.Id, n2.Id });
        Check(org2 != null, "second cartel seeded successfully");

        var name3 = OrganizationDynamics.DetermineOrganizationName(c, "commodity_cartel", n1, n2, members);
        Check(name3.StartsWith("org.name.cartel|"),
            $"third cartel falls back to cartel pattern with capitals: {name3}");
        Check(name3.Contains("Caracas") && name3.Contains("Algiers"),
            "cartel pattern includes member capitals");
    }

    private static void CartelPreferredDoctrine()
    {
        var c = World();
        var n0 = c.Nations[0];
        var n1 = c.Nations[1];
        var org = InternationalOrganizations.Seed(c, "Test Cartel", "commodity_cartel", new[] { n0.Id, n1.Id });

        var preferred0 = InternationalOrganizations.PreferredDoctrine(c, org, n0);
        var preferred1 = InternationalOrganizations.PreferredDoctrine(c, org, n1);

        Check(preferred0 == "cartel_quotas", $"commodity cartel preferred doctrine for n0 is cartel_quotas (got {preferred0})");
        Check(preferred1 == "cartel_quotas", $"commodity cartel preferred doctrine for n1 is cartel_quotas (got {preferred1})");
    }

    private static void FingerprintAndPersistence()
    {
        var c = World();
        var n0 = c.Nations[0];
        var n1 = c.Nations[1];

        var baselineHash = CampaignFingerprint.Of(c);

        var org = InternationalOrganizations.Seed(c, "org.name.petroleum_exporting_cartel", "commodity_cartel", new[] { n0.Id, n1.Id });
        org.DoctrineId = "cartel_quotas";

        var cartelHash = CampaignFingerprint.Of(c);
        Check(cartelHash != baselineHash, "founding a cartel and adopting doctrine alters campaign fingerprint");

        // Verify JSON round-trip of organizations
        var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        var json = System.Text.Json.JsonSerializer.Serialize(c.Organizations, options);
        var restoredOrgs = System.Text.Json.JsonSerializer.Deserialize<List<InternationalOrganization>>(json, options);

        Check(restoredOrgs != null && restoredOrgs.Count == 1, "organization deserialized from JSON");
        Check(restoredOrgs[0].CharterId == "commodity_cartel", "restored org retains commodity_cartel charter");
        Check(restoredOrgs[0].DoctrineId == "cartel_quotas", "restored org retains cartel_quotas doctrine");
    }
}
