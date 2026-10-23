using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Brain drain and skilled migration. The mechanic moves people between regions, so the
/// invariants that matter are the ones migration breaks when it is wrong: every person who
/// leaves one region arrives in another, nobody is conjured out of the treasury, and a world
/// at peace and well governed does not bleed for no reason.
/// </summary>
static class BrainDrainTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("brain drain: " + name);
        Console.WriteLine("PASS brain drain: " + name);
    }

    /// <summary>A two-country world: the receiver is rich and calm, the donor starts out merely poorer.</summary>
    private static Campaign Fixture(int seed = 909)
    {
        var c = new Campaign(seed);
        var rich = new Nation
        {
            Id = 0, MapIndex = 100, Tag = "RCH", Name = "Richland", Continent = "Europe",
            Treasury = 500, Gdp = 2000, Population = 10_000_000, Stability = .85f,
            StandardOfLiving = .78f, Literacy = .95f, Productivity = 1.4f
        };
        var poor = new Nation
        {
            Id = 1, MapIndex = 200, Tag = "POR", Name = "Poorland", Continent = "Europe",
            Treasury = 5, Gdp = 200, Population = 10_000_000, Stability = .6f,
            StandardOfLiving = .55f, Literacy = .70f, Productivity = .9f
        };
        var richState = new ScenarioState
        {
            MapIndex = 401, NationMapIndex = 100, Population = 10_000_000,
            StandardOfLiving = .78f, Literacy = .95f, Stability = .85f, EmploymentTarget = .9f
        };
        var poorState = new ScenarioState
        {
            MapIndex = 501, NationMapIndex = 200, Population = 10_000_000,
            StandardOfLiving = .55f, Literacy = .70f, Stability = .6f, EmploymentTarget = .9f
        };
        rich.CapitalStateMapIndex = 401;
        poor.CapitalStateMapIndex = 501;
        richState.PopulationGroups.Add(new PopulationGroup
        {
            Size = 9_000_000, Employment = .9f, Wealth = .6f, StandardOfLiving = .78f
        });
        poorState.PopulationGroups.Add(new PopulationGroup
        {
            Size = 9_000_000, Employment = .6f, Wealth = .3f, StandardOfLiving = .40f
        });
        c.Nations.Add(rich);
        c.Nations.Add(poor);
        c.States.Add(richState);
        c.States.Add(poorState);
        c.ConfigureSinglePlayer(rich.Id);
        return c;
    }

    private static void AtWarWeek(Campaign c) => DomesticCrisesTests.Day(c, 7);

    public static void Run()
    {
        AWorldAtRestDoesNotBleed();
        WarPushesPeopleAndTheyAreSomewhereWhenTheyArrive();
        TheDonorKeepsItsSchoolsAndTheReceiverGainsThem();
        AnEmbargoIsALockedDoor();
        RetentionAnchorsPeople();
        AModCanTurnTheWholeThingOff();
        TheReportExplainsItWithoutTouchingIt();
    }

    private static void AWorldAtRestDoesNotBleed()
    {
        var c = Fixture();
        var rich = c.NationByTag("RCH");
        var poor = c.NationByTag("POR");
        AtWarWeek(c);
        var before = CampaignFingerprint.Of(c);
        BrainDrain.Tick(c);
        Check(rich.WeeklyNetMigration == 0 && poor.WeeklyNetMigration == 0 && poor.BrainDrainRate == 0f,
            "a world that is peaceful and well governed loses nobody");
        Check(before == CampaignFingerprint.Of(c),
            "an empty migration week emits no new bytes, so old saves keep their fingerprint");
    }

    private static void WarPushesPeopleAndTheyAreSomewhereWhenTheyArrive()
    {
        var c = Fixture();
        var rich = c.NationByTag("RCH");
        var poor = c.NationByTag("POR");
        var poorHome = c.StatesOf(poor).Single();
        var richHome = c.StatesOf(rich).Single();
        poor.Stability = .2f;
        poorHome.WarDamage = .7f;
        poorHome.LivelihoodStress = .6f;
        poorHome.EmploymentTarget = .5f;
        var worldBefore = c.Nations.Sum(n => n.Population);

        AtWarWeek(c);
        BrainDrain.Tick(c);

        Check(poor.WeeklyNetMigration < 0 && poor.BrainDrainDestinationId == rich.Id,
            "war and ruin push people to a country that can take them");
        Check(rich.WeeklyNetMigration == -poor.WeeklyNetMigration && rich.BrainGainSourceId == poor.Id,
            "the receiver counts exactly what the donor lost");
        Check(poorHome.ReviewNetMigration < 0 && richHome.ReviewNetMigration == -poorHome.ReviewNetMigration,
            "the region that lost them is the region that did, and the gain is the same number");
        Check(c.Nations.Sum(n => n.Population) == worldBefore && c.States.Sum(s => s.Population) == worldBefore,
            "nobody is conjured and nobody vanishes: the world's population is conserved");
        Check(poorHome.PopulationGroups.Sum(g => g.Size) == poorHome.Population &&
              richHome.PopulationGroups.Sum(g => g.Size) == richHome.Population,
            "the peoples of both regions stay conserved too");
    }

    private static void TheDonorKeepsItsSchoolsAndTheReceiverGainsThem()
    {
        var c = Fixture();
        var rich = c.NationByTag("RCH");
        var poor = c.NationByTag("POR");
        var poorHome = c.StatesOf(poor).Single();
        var richHome = c.StatesOf(rich).Single();
        poorHome.WarDamage = .8f;
        poorHome.LivelihoodStress = .7f;
        var donorLiteracy = poor.Literacy;
        var receiverLiteracy = rich.Literacy;

        AtWarWeek(c);
        BrainDrain.Tick(c);

        Check(poor.WeeklyNetMigration < 0, "this world actually drained");
        Check(poor.Literacy <= donorLiteracy && poor.Productivity < .9f + 1e-6,
            "losing specialists costs the donor literacy and long-run productivity");
        Check(rich.Literacy >= receiverLiteracy && rich.Productivity > .9f - 1e-6,
            "receiving them raises the receiver's schools and workshops");
    }

    private static void AnEmbargoIsALockedDoor()
    {
        var c = Fixture();
        var rich = c.NationByTag("RCH");
        var poor = c.NationByTag("POR");
        var poorHome = c.StatesOf(poor).Single();
        poorHome.WarDamage = .9f;
        poorHome.LivelihoodStress = .8f;
        var relation = CampaignDiplomacy.Get(c, poor.Id, rich.Id);
        relation.EmbargoByA = true;
        relation.EmbargoByB = true;
        relation.Stance = DiplomaticStance.Rival;
        AtWarWeek(c);
        BrainDrain.Tick(c);
        Check(poor.WeeklyNetMigration == 0 && poor.BrainDrainDestinationId == -1,
            "an embargo and hostility between the two is a door nobody walks through");
    }

    private static void RetentionAnchorsPeople()
    {
        var c = Fixture();
        var rich = c.NationByTag("RCH");
        var wealthy = Fixture();
        var guarded = wealthy.NationByTag("POR");
        var ruined = wealthy.StatesOf(guarded).Single();
        ruined.WarDamage = .9f;
        ruined.LivelihoodStress = .8f;
        AtWarWeek(wealthy);
        BrainDrain.Tick(wealthy);
        var unattended = guarded.WeeklyNetMigration;

        var retained = Fixture();
        var anchor = retained.NationByTag("POR");
        var held = retained.StatesOf(anchor).Single();
        held.WarDamage = .9f;
        held.LivelihoodStress = .8f;
        ModifierSystem.Grant(retained, anchor, "talent_retention", 0);
        AtWarWeek(retained);
        BrainDrain.Tick(retained);
        Check(anchor.WeeklyNetMigration > unattended,
            "a talent retention programme keeps more specialists home than doing nothing");
        Check(anchor.WeeklyNetMigration <= 0, "and it softens the outflow, it does not invert reality");
    }

    private static void AModCanTurnTheWholeThingOff()
    {
        var c = Fixture();
        var poor = c.NationByTag("POR");
        var poorHome = c.StatesOf(poor).Single();
        poorHome.WarDamage = 1f;
        poorHome.LivelihoodStress = .9f;
        AtWarWeek(c);
        var before = CampaignFingerprint.Of(c);
        c.Defines.BrainDrainEnabled = false;
        BrainDrain.Tick(c);
        Check(poor.WeeklyNetMigration == 0 && before == CampaignFingerprint.Of(c),
            "a mod that turns the mechanic off leaves the world untouched");

        var weekly = Fixture();
        weekly.Defines.BrainDrainMaxWeeklyShare = 0f;
        AtWarWeek(weekly);
        weekly.StatesOf(weekly.NationByTag("POR")).Single().WarDamage = 1f;
        BrainDrain.Tick(weekly);
        Check(weekly.NationByTag("POR").WeeklyNetMigration == 0,
            "a share of nought is a border that is closed");
    }

    private static void TheReportExplainsItWithoutTouchingIt()
    {
        var c = Fixture();
        var rich = c.NationByTag("RCH");
        var poor = c.NationByTag("POR");
        c.StatesOf(poor).Single().WarDamage = .8f;
        c.StatesOf(poor).Single().LivelihoodStress = .7f;
        AtWarWeek(c);
        BrainDrain.Tick(c);
        var before = CampaignFingerprint.Of(c);
        var random = c.Random.State;
        var draining = SituationReport.Read(c, poor).FirstOrDefault(i => i.Id == "brain_drain");
        var receiving = SituationReport.Read(c, rich).FirstOrDefault(i => i.Id == "brain_gain");
        Check(draining is not null && draining.Priority != SituationPriority.Opportunity,
            "a draining country is told so, and that it is a drain and not an accident");
        Check(draining.Facts.Any(f => f.Label.Contains("outflow")) && draining.Causes.Length > 0,
            "the card carries the number and the reason");
        Check(receiving is not null && receiving.Priority == SituationPriority.Opportunity,
            "a receiving country is told the same, as an opportunity");
        _ = SituationReport.Read(c, poor);
        _ = SituationReport.Read(c, rich);
        Check(before == CampaignFingerprint.Of(c) && c.Random.State == random,
            "reading the report changes neither the world nor the random stream");
    }
}
