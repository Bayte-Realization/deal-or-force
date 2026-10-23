using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>The grand strategy: seven courses, one per country, what each does to the way an army
/// learns and to the tactical tree a government reaches for, and the price of changing course.</summary>
static class StrategicDoctrineTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL Strategy: " + label);
        Console.WriteLine("PASS Strategy: " + label);
    }

    static void SetDay(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(campaign, (long)day * 24);

    public static void Run()
    {
        ShippedSeven();
        AdoptAndChange();
        LearningHand();
        ModifiersReach();
        BiasLeadsTheBook();
        AiChooses();
    }

    static void ShippedSeven()
    {
        var all = StrategicDoctrineCatalog.All.ToList();
        Check(all.Count == 7, "seven shipped strategies");
        foreach (var doctrine in all)
        {
            Check(doctrine.Name.Length > 0 && doctrine.Description.Length > 0,
                $"'{doctrine.Id}' has a name and a reason");
            Check(doctrine.PoliticalCost > 0f, $"'{doctrine.Id}' prices a change of course");
            Check(doctrine.Modifiers.Count > 0 &&
                  doctrine.Modifiers.Keys.All(key => NationalModifiers.Knows(key)),
                $"'{doctrine.Id}' adds only keys the simulation asks for");
            Check(doctrine.TrainingSlots >= StrategicDoctrineCatalog.MinMultiplier &&
                  doctrine.DrillExperience >= StrategicDoctrineCatalog.MinMultiplier &&
                  doctrine.BattleExperience >= StrategicDoctrineCatalog.MinMultiplier &&
                  doctrine.AirExperience >= StrategicDoctrineCatalog.MinMultiplier &&
                  doctrine.NavalExperience >= StrategicDoctrineCatalog.MinMultiplier,
                $"'{doctrine.Id}' learns somewhere in the range of reason");
            if (doctrine.TacticalBias.Length > 0)
                Check(TacticalDoctrineCatalog.Find(doctrine.TacticalBias) is not null,
                    $"'{doctrine.Id}' leans on a tree that is written");
        }
        // Every tactical tree has a strategic sponsor: the two layers are one system, not two.
        foreach (var branch in TacticalDoctrineCatalog.All)
            Check(all.Any(doctrine => doctrine.TacticalBias == branch.Id),
                $"the tactical tree '{branch.Id}' has a strategy that leads to it");

        // A mod writes a strategy in the same shape; a broken one is refused whole, by name.
        var errors = new List<string>();
        using (var doc = JsonDocument.Parse(
            "{\"mod_way\":{\"name\":\"Mod way\",\"description\":\"why not\",\"political_cost\":8,\"modifiers\":{\"army_attack\":0.03}," +
            "\"training_slots\":1.2,\"tactical_bias\":\"manoeuvre\"}}"))
            StrategicDoctrineCatalog.Merge(doc.RootElement, errors);
        Check(errors.Count == 0 && StrategicDoctrineCatalog.Find("mod_way")?.TrainingSlots == 1.2f,
            "a mod's strategy merges beside the shipped ones");
        Check(StrategicDoctrineCatalog.Find("mod_way")?.TacticalBias == "manoeuvre",
            "and carries its leaning");

        var before = StrategicDoctrineCatalog.All.Count();
        using (var bad = JsonDocument.Parse(
            "{\"bad_way\":{\"name\":\"Bad\",\"political_cost\":5,\"modifiers\":{\"no_such_key\":0.1}}}"))
            StrategicDoctrineCatalog.Merge(bad.RootElement, errors);
        Check(errors.Count >= 1 && StrategicDoctrineCatalog.Find("bad_way") is null,
            "a strategy adding a key nobody reads is refused whole");
        errors.Clear();
        using (var bad = JsonDocument.Parse(
            "{\"bad_way\":{\"name\":\"Bad\",\"political_cost\":5,\"training_slots\":40}}"))
            StrategicDoctrineCatalog.Merge(bad.RootElement, errors);
        Check(errors.Count >= 1 && StrategicDoctrineCatalog.All.Count() == before,
            "a learning multiplier out of all reason is refused with a sentence");
        errors.Clear();
        using (var bad = JsonDocument.Parse("{\"bad_way\":{\"name\":\"Bad\",\"political_cost\":-3}}"))
            StrategicDoctrineCatalog.Merge(bad.RootElement, errors);
        Check(errors.Count >= 1 && StrategicDoctrineCatalog.Find("bad_way") is null,
            "and a strategy that pays the government to adopt it is refused");
    }

    static void AdoptAndChange()
    {
        var campaign = Campaign.CreateBootstrap(1704);
        var nation = campaign.NationByTag("ROU");
        nation.PoliticalPoints = 0f;

        Check(StrategicDoctrines.Refusal(campaign, nation, "sea_power") == "",
            "the first course a country takes up is free");
        var adopt = new AdoptStrategicDoctrineCommand { IssuerId = nation.Id, DoctrineId = "sea_power" };
        campaign.Commands.Enqueue(adopt);
        campaign.Commands.ApplyAll(campaign);
        Check(adopt.Refusal == "" && nation.StrategicDoctrine.Id == "sea_power",
            "a country takes up a course of its own choosing");
        Check(nation.PoliticalPoints == 0f, "and pays nothing political for the first one");
        Check(nation.StrategicDoctrine.LastChangeDay == campaign.ElapsedDays,
            "the day of the decision is kept");

        var bad = new AdoptStrategicDoctrineCommand { IssuerId = nation.Id, DoctrineId = "no_such_way" };
        campaign.Commands.Enqueue(bad);
        campaign.Commands.ApplyAll(campaign);
        Check(bad.Refusal.Length > 0, "a strategy that is not written is refused with a sentence");
        Check(StrategicDoctrines.Refusal(campaign, nation, "sea_power").Length > 0,
            "and a country is told when it is already following that course");
        Check(StrategicDoctrines.Refusal(campaign, nation, "manoeuvre_war").Length > 0,
            "changing course without the political credit is refused");

        nation.PoliticalPoints = 25f;
        var change = new AdoptStrategicDoctrineCommand { IssuerId = nation.Id, DoctrineId = "manoeuvre_war" };
        campaign.Commands.Enqueue(change);
        campaign.Commands.ApplyAll(campaign);
        Check(change.Refusal == "" && nation.StrategicDoctrine.Id == "manoeuvre_war",
            "with the credit, the government changes course");
        Check(MathF.Abs(nation.PoliticalPoints - 15f) < 0.01f,
            "and pays for the privilege out of that credit");

        campaign.SetFeature("strategic_doctrines", false);
        Check(StrategicDoctrines.Refusal(campaign, nation, "homeland_defence").Length > 0,
            "a scenario that switches the mechanic off refuses it");
        campaign.SetFeature("strategic_doctrines", true);
    }

    static void LearningHand()
    {
        var campaign = Campaign.CreateBootstrap(1704);
        campaign.Defines.AiTrainingMaxArmies = 0;      // only the division the test drills
        var nation = campaign.NationByTag("ROU");
        var army = campaign.Armies.First(item => item.NationId == nation.Id);

        float Week(int from)
        {
            for (var day = from; day < from + 7; day++) { SetDay(campaign, day); MilitaryExperience.Tick(campaign); }
            var earned = nation.ArmyExperience;
            nation.ArmyExperience = 0f;
            return earned;
        }

        army.Training = true; army.BattleId = -1;
        var plainDrill = Week(10);
        nation.StrategicDoctrine.Id = "professional_cadre"; NationalModifiers.Invalidate(nation);
        var cadreDrill = Week(30);
        Check(cadreDrill > plainDrill * 1.15f,
            "a cadre army's drill teaches more than a country with no course");

        army.Training = false; army.BattleId = 7;      // division in the line
        var plainBattle = Week(50);
        nation.StrategicDoctrine.Id = "attrition_war"; NationalModifiers.Invalidate(nation);
        var attritionBattle = Week(70);
        Check(attritionBattle > plainBattle * 1.15f,
            "a war of attrition learns more from the same fighting");
        army.BattleId = -1;

        // The bench of drilling formations follows the course as well.
        Check(StrategicDoctrines.TrainingSlots(nation) < 1f, "a war of attrition drills fewer formations");
        nation.StrategicDoctrine.Id = "professional_cadre";
        Check(StrategicDoctrines.TrainingSlots(nation) > 1f, "a cadre army drills more");
        nation.StrategicDoctrine.Id = "";
        Check(MathF.Abs(StrategicDoctrines.TrainingSlots(nation) - 1f) < 0.001f,
            "and a country with no course is left exactly as it was");
    }

    static void ModifiersReach()
    {
        var campaign = Campaign.CreateBootstrap(1704);
        var nation = campaign.NationByTag("ROU");
        // Measured as a delta: another system's own hand on the army is not this test's business.
        float Speed() => NationalModifiers.Of(nation, "army_speed");
        float Supply() => NationalModifiers.Of(nation, "supply");
        float Replacements() => NationalModifiers.Of(nation, "reinforcement_rate");
        var speed = Speed(); var supply = Supply(); var replacements = Replacements();

        nation.StrategicDoctrine.Id = "manoeuvre_war"; NationalModifiers.Invalidate(nation);
        Check(MathF.Abs(Speed() - speed - 0.06f) < 0.001f, "a course of manoeuvre reaches the army's speed");
        Check(MathF.Abs(Supply() - supply - 0.04f) < 0.001f, "and its supply");

        nation.StrategicDoctrine.Id = "attrition_war"; NationalModifiers.Invalidate(nation);
        Check(MathF.Abs(Speed() - speed) < 0.001f, "changing course takes the old course's hand off the army");
        Check(MathF.Abs(Replacements() - replacements - 0.10f) < 0.001f,
            "and the new one is felt where it says");
    }

    static void BiasLeadsTheBook()
    {
        var campaign = Campaign.CreateBootstrap(1704);
        var overridden = false;
        foreach (var nation in campaign.Nations)
        {
            nation.StrategicDoctrine.Id = "";
            var byCharacter = TacticalDoctrines.PreferredBranch(campaign, nation);
            nation.StrategicDoctrine.Id = "air_power";
            var byStrategy = TacticalDoctrines.PreferredBranch(campaign, nation);
            if (byStrategy != "sky") { overridden = false; break; }
            if (byCharacter != "sky") overridden = true;
        }
        Check(overridden, "a strategy's leaning pulls the government's book toward its own tree");

        var sample = campaign.NationByTag("ROU");
        sample.StrategicDoctrine.Id = "professional_cadre";     // the one course with no leaning
        Check(TacticalDoctrines.PreferredBranch(campaign, sample).Length > 0,
            "a course with no leaning leaves the book to the character");

        // And the leaning decides what that government studies, not only what it would like to.
        var studying = Campaign.CreateBootstrap(1704);
        var cadet = studying.Nations.First(item => TacticalDoctrines.PreferredBranch(studying, item) != "sky");
        cadet.StrategicDoctrine.Id = "air_power";
        cadet.ArmyExperience = 500f; cadet.AirExperience = 500f;
        for (var day = 1; day <= 21; day++) { SetDay(studying, day); TacticalDoctrines.Tick(studying); }
        Check(cadet.TacticalDoctrine.Branch == "sky" || cadet.TacticalDoctrine.LearningBranch == "sky",
            "and the government studies the tree its strategy leans on");
    }

    static void AiChooses()
    {
        var campaign = Campaign.CreateBootstrap(1704);
        for (var day = 1; day <= 30; day++) { SetDay(campaign, day); StrategicDoctrines.Tick(campaign); }
        var adopted = campaign.Nations.Count(nation => nation.StrategicDoctrine.Id.Length > 0);
        Check(adopted > 50, "governments take up a course of their own accord");

        var nation = campaign.NationByTag("ROU");
        Check(nation.StrategicDoctrine.Id == StrategicDoctrines.Preferred(campaign, nation),
            "each picks the course its own character suggests");

        // A course is not twitched: a change made today stands.
        nation.PoliticalPoints = 60f;
        nation.StrategicDoctrine.Id = "sea_power";
        nation.StrategicDoctrine.LastChangeDay = campaign.ElapsedDays;
        for (var day = 31; day <= 120; day++) { SetDay(campaign, day); StrategicDoctrines.Tick(campaign); }
        Check(nation.StrategicDoctrine.Id == "sea_power", "a change made today is not undone next season");

        // But a course the government no longer believes in is changed, in peace, once it can pay.
        nation.StrategicDoctrine.LastChangeDay = 0;
        for (var day = 121; day <= 700; day++) { SetDay(campaign, day); StrategicDoctrines.Tick(campaign); }
        var stillAtWar = campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        Check(stillAtWar || nation.StrategicDoctrine.Id == StrategicDoctrines.Preferred(campaign, nation),
            "and a stale course is corrected in peace");
    }
}
