using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// Pillar 3: Deep domestic politics, parliamentary caucuses, anti-war coalitions,
/// military budget restraint, peace mandates, and factional lobbying.
/// </summary>
public static class ParliamentaryPoliticsTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL parliamentary politics: " + text);
        Console.WriteLine("PASS parliamentary politics: " + text);
    }

    public static void Run()
    {
        EligibilityVerification();
        PeacefulBaselineAndApproval();
        WarWearinessAndAntiWarExpansion();
        SevereExhaustionPeaceMandate();
        FactionalMinisterLobbying();
        FactionalLawLobbying();
        FingerprintPreservationAndSerialization();
    }

    private static void SetElapsedDays(Campaign c, int days)
    {
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, days * 24L);
    }

    private static Campaign World()
    {
        var c = new Campaign(55123) { ScenarioId = "parliament_test" };
        c.SetPlayer(0);

        var n0 = new Nation
        {
            Id = 0,
            Tag = "DEM",
            Name = "Democratic Republic",
            MapIndex = 1,
            Population = 5_000_000,
            Gdp = 200,
            Treasury = 100,
            Government = "parliamentary_republic",
            Ideology = "liberalism",
            Stability = 0.70f,
            WarSupport = 0.80f
        };

        var n1 = new Nation
        {
            Id = 1,
            Tag = "AUT",
            Name = "Autocratic Empire",
            MapIndex = 2,
            Population = 8_000_000,
            Gdp = 300,
            Treasury = 150,
            Government = "absolute_monarchy",
            Ideology = "reactionary",
            Stability = 0.65f,
            WarSupport = 0.85f
        };

        var n2 = new Nation
        {
            Id = 2,
            Tag = "FED",
            Name = "Federal Republic",
            MapIndex = 3,
            Population = 4_000_000,
            Gdp = 180,
            Treasury = 80,
            Government = "federation",
            Ideology = "social_democracy",
            Stability = 0.60f,
            WarSupport = 0.75f
        };

        c.Nations.Add(n0);
        c.Nations.Add(n1);
        c.Nations.Add(n2);
        return c;
    }

    private static void EligibilityVerification()
    {
        var c = World();
        var dem = c.NationById(0);
        var aut = c.NationById(1);
        var fed = c.NationById(2);

        Check(ParliamentaryPolitics.Eligible(dem), "parliamentary republic is eligible");
        Check(ParliamentaryPolitics.Eligible(fed), "federation is eligible");
        Check(!ParliamentaryPolitics.Eligible(aut), "absolute monarchy is not eligible");

        var rebel = new Nation { Id = 3, Tag = "REB", Movement = new MovementState(), Population = 1000, Government = "parliamentary_republic" };
        Check(!ParliamentaryPolitics.Eligible(rebel), "civic movement is not eligible");
    }

    private static void PeacefulBaselineAndApproval()
    {
        var c = World();
        var dem = c.NationById(0);
        SetElapsedDays(c, 19);

        ParliamentaryPolitics.Review(c, dem);

        var parl = dem.Parliament;
        Check(parl is not null, "parliament initialized on review");
        Check(parl.RulingBlocShare > 0.50f, "high approval gives ruling majority at peace");
        Check(parl.DovishShare >= 0.05f && parl.DovishShare <= 0.30f, "dovish caucus is modest at peace");
        Check(Math.Abs(parl.RulingBlocShare + parl.OppositionShare + parl.DovishShare - 1.0f) < 0.001f,
            "caucus shares sum to exactly 1.0");
        Check(!parl.MilitaryBudgetCutActive, "no budget cut active at peace");
        Check(!parl.PeaceMandateActive, "no peace mandate active at peace");
        Check(parl.FavoredPortfolio.Length > 0, "favored portfolio identified by parliament");
        Check(parl.FavoredLawCategory.Length > 0, "favored law category identified by parliament");
    }

    private static void WarWearinessAndAntiWarExpansion()
    {
        var c = World();
        var dem = c.NationById(0);
        var aut = c.NationById(1);

        // Open an active war
        var war = new StrategicWar
        {
            Id = 1,
            StartedDay = 0,
            AttackerId = aut.Id,
            DefenderId = dem.Id,
            WarScore = -15f
        };
        c.Wars.Add(war);

        // Drop war support due to war casualties
        dem.WarSupport = 0.35f; // Fatigue = 0.65
        SetElapsedDays(c, 30);

        ParliamentaryPolitics.Review(c, dem);

        var parl = dem.Parliament;
        Check(parl.DovishShare >= c.Defines.ParliamentAntiWarThreshold,
            $"dovish caucus expands under high fatigue ({parl.DovishShare:P1} >= {c.Defines.ParliamentAntiWarThreshold:P1})");
        Check(parl.MilitaryBudgetCutActive, "anti-war caucus enacts military budget restraint");

        var sources = ParliamentaryPolitics.Sources(dem).ToList();
        Check(sources.Any(s => s.Source == "modifier.parliament_anti_war_budget"),
            "parliament_anti_war_budget source emitted");

        // Verify NationalModifiers incorporates the budget cut
        NationalModifiers.Tick(c);
        var reinf = NationalModifiers.Of(dem, "reinforcement_rate");
        Check(reinf <= -0.10f, $"reinforcement rate penalized by anti-war budget ({reinf})");
    }

    private static void SevereExhaustionPeaceMandate()
    {
        var c = World();
        var dem = c.NationById(0);
        var aut = c.NationById(1);

        var war = new StrategicWar
        {
            Id = 2,
            StartedDay = 0,
            AttackerId = aut.Id,
            DefenderId = dem.Id,
            WarScore = -35f
        };
        c.Wars.Add(war);

        // Extreme war exhaustion + debt stress
        dem.WarSupport = 0.20f; // Fatigue = 0.80
        dem.DebtCondition = DebtCondition.Stressed;
        dem.ShortageIndex = 0.30f;
        SetElapsedDays(c, 45);

        ParliamentaryPolitics.Review(c, dem);

        var parl = dem.Parliament;
        Check(parl.DovishShare >= 0.50f, $"dovish caucus achieves absolute majority ({parl.DovishShare:P1})");
        Check(parl.PeaceMandateActive, "parliament enacts peace mandate");
        Check(parl.PeaceMandateDay == 45, "peace mandate day recorded as 45");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "parliament_peace_mandate" && e.ActorId == dem.Id),
            "parliament_peace_mandate recorded in world chronicle");

        var sources = ParliamentaryPolitics.Sources(dem).ToList();
        Check(sources.Any(s => s.Source == "modifier.parliament_peace_mandate"),
            "parliament_peace_mandate source emitted");

        // Peace ends: check that mandate disengages
        war.Ended = true;
        dem.WarSupport = 0.85f;
        SetElapsedDays(c, 60);

        ParliamentaryPolitics.Review(c, dem);
        Check(!parl.PeaceMandateActive, "peace mandate disengages when peace returns");
        Check(!parl.MilitaryBudgetCutActive, "military budget restraint lifts when war ends");
    }

    private static void FactionalMinisterLobbying()
    {
        var c = World();
        var dem = c.NationById(0);

        // Induce high debt so parliament demands finance
        dem.PublicDebt = 180f; // 90% of GDP
        dem.Gdp = 200f;
        dem.DebtCondition = DebtCondition.Stressed;
        SetElapsedDays(c, 20);

        ParliamentaryPolitics.Review(c, dem);
        Check(dem.Parliament.FavoredPortfolio == "finance", "parliament demands finance portfolio under debt stress");

        dem.Parliament.FactionDiscontent = 0.50f;
        var initStab = dem.Stability;

        // Appoint finance minister
        ParliamentaryPolitics.OnMinisterAppointed(c, dem, "finance");

        Check(dem.Parliament.FactionDiscontent <= 0.35f,
            $"appointing favored finance minister calms discontent ({dem.Parliament.FactionDiscontent})");
        Check(dem.Stability >= initStab, "stability receives boost from complying with parliament");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "parliament_minister_supported" && e.ActorId == dem.Id),
            "parliament_minister_supported recorded in chronicle");
    }

    private static void FactionalLawLobbying()
    {
        var c = World();
        var dem = c.NationById(0);
        SetElapsedDays(c, 25);

        ParliamentaryPolitics.Review(c, dem);
        var favoredCat = dem.Parliament.FavoredLawCategory;
        dem.Parliament.FactionDiscontent = 0.40f;
        var initStab = dem.Stability;

        var law = new PoliticalPolicy
        {
            Id = "test_reform_law",
            Name = "Consensus Reform Law",
            Category = favoredCat
        };

        ParliamentaryPolitics.OnLawAdopted(c, dem, law);

        Check(dem.Parliament.FactionDiscontent <= 0.25f,
            $"enacting favored law category ({favoredCat}) reduces legislative friction ({dem.Parliament.FactionDiscontent})");
        Check(dem.Stability > initStab, "stability increased by cross-bench law consensus");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "parliament_law_supported" && e.ActorId == dem.Id),
            "parliament_law_supported recorded in chronicle");
    }

    private static void FingerprintPreservationAndSerialization()
    {
        var c = World();
        var dem = c.NationById(0);

        // Baseline unreviewed state: Parliament is null
        dem.Parliament = null;
        var fpBefore = CampaignFingerprint.Of(c);

        // Parliament initialized but unreviewed (LastReviewDay = -1)
        dem.Parliament = new ParliamentState { LastReviewDay = -1 };
        var fpDefault = CampaignFingerprint.Of(c);

        Check(fpBefore == fpDefault, "unreviewed parliament emits zero bytes in fingerprint");

        // Active reviewed parliament produces reproducible non-empty fingerprint
        SetElapsedDays(c, 19);
        ParliamentaryPolitics.Review(c, dem);
        var fpActive1 = CampaignFingerprint.Of(c);
        var fpActive2 = CampaignFingerprint.Of(c);

        Check(fpActive1 != fpBefore, "active parliament state is mixed into campaign fingerprint");
        Check(fpActive1 == fpActive2, "fingerprint computation is perfectly deterministic");
    }
}
