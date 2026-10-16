using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>The war Doctrine: a researched tree of army articles paid for in army experience, with
/// a rewrite that costs political points, and the experience itself earned by fighting, drilling
/// and being funded.</summary>
static class TacticalDoctrineTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL Doctrine: " + label);
        Console.WriteLine("PASS Doctrine: " + label);
    }

    static void SetDay(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(campaign, (long)day * 24);

    public static void Run()
    {
        ShippedTree();
        StudyAndRewrite();
        ExperienceSources();
        AiChooses();
        ContentSpeaks();
        MenuSpeaks();
    }

    static void ShippedTree()
    {
        Check(TacticalDoctrineCatalog.All.Count() == 6, "six shipped Doctrines");
        foreach (var branch in TacticalDoctrineCatalog.All)
        {
            Check(branch.Nodes.Count == 4, $"'{branch.Id}' is a tree of four articles");
            Check(TacticalDoctrineCatalog.KnowsExperience(branch.Experience), $"'{branch.Id}' is paid for in a real service's experience");
            var ids = branch.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            Check(branch.Nodes.All(node => node.Requires.All(ids.Contains)),
                $"'{branch.Id}' opens only towards its own articles");
            Check(branch.Nodes.Any(node => node.Requires.Count == 0),
                $"'{branch.Id}' has a way in");
            Check(branch.Nodes.All(node => node.RushCost > node.Cost),
                $"'{branch.Id}' sells speed dearer than patience");
            Check(branch.Nodes.SelectMany(node => node.Modifiers.Keys)
                    .All(key => NationalModifiers.Knows(key)),
                $"'{branch.Id}' adds only keys the simulation asks for");
        }

        // A mod writes a branch in the same shape; a broken one is refused whole, by name.
        var errors = new List<string>();
        using (var doc = JsonDocument.Parse(
            "{\"mod_tree\":{\"name\":\"Mod tree\",\"experience\":\"army\",\"nodes\":[" +
            "{\"id\":\"mod_root\",\"name\":\"Root\",\"cost\":10,\"days\":30,\"modifiers\":{\"army_attack\":0.05}}," +
            "{\"id\":\"mod_leaf\",\"name\":\"Leaf\",\"requires\":[\"mod_root\"],\"cost\":20,\"days\":60,\"modifiers\":{\"army_defense\":0.05}}]}}"))
            TacticalDoctrineCatalog.Merge(doc.RootElement, errors);
        Check(errors.Count == 0 && TacticalDoctrineCatalog.Find("mod_tree")?.Nodes.Count == 2,
            "a mod's branch merges beside the shipped ones");
        Check(TacticalDoctrineCatalog.Find("mod_tree").Nodes[1].RushCost == 60f,
            "a node with no rush price rushes at three times its cost");

        var before = TacticalDoctrineCatalog.All.Count();
        using (var bad = JsonDocument.Parse(
            "{\"bad_tree\":{\"name\":\"Bad\",\"experience\":\"army\",\"nodes\":[" +
            "{\"id\":\"bad_leaf\",\"name\":\"Leaf\",\"requires\":[\"nowhere\"],\"cost\":10,\"days\":30}]}}"))
            TacticalDoctrineCatalog.Merge(bad.RootElement, errors);
        Check(errors.Count >= 1 && TacticalDoctrineCatalog.Find("bad_tree") is null &&
            TacticalDoctrineCatalog.All.Count() == before,
            "a branch requiring a node it does not hold is refused whole");
        errors.Clear();
        using (var bad = JsonDocument.Parse(
            "{\"bad_cost\":{\"name\":\"Bad\",\"experience\":\"army\",\"nodes\":[" +
            "{\"id\":\"bad_node\",\"name\":\"Node\",\"cost\":\"20\",\"days\":30}]}}"))
            TacticalDoctrineCatalog.Merge(bad.RootElement, errors);
        Check(errors.Count >= 1 && TacticalDoctrineCatalog.Find("bad_cost") is null,
            "a cost written as text is a sentence, not a crash");
        TacticalDoctrineCatalog.ResetCatalog();
    }

    static void StudyAndRewrite()
    {
        var campaign = Campaign.CreateBootstrap(1701);
        var nation = campaign.NationByTag("ROU");
        var branch = TacticalDoctrineCatalog.Find("manoeuvre");

        Check(TacticalDoctrines.Refusal(campaign, nation, branch.Id, "manoeuvre_march", false).Length > 0,
            "an article nobody can pay for is refused with a sentence");
        Check(nation.TacticalDoctrine.Known.Count == 0 && nation.ArmyExperience == 0f,
            "a refused article spends nothing");

        nation.ArmyExperience = 60f;
        Check(TacticalDoctrines.Refusal(campaign, nation, branch.Id, "manoeuvre_columns", false).Length > 0,
            "the tree opens from the root, not from the middle");
        var command = new StudyDoctrineCommand { IssuerId = nation.Id, BranchId = branch.Id, NodeId = "manoeuvre_march" };
        campaign.Commands.Enqueue(command);
        campaign.Commands.ApplyAll(campaign);
        Check(command.Refusal.Length == 0 && nation.TacticalDoctrine.LearningNode == "manoeuvre_march",
            "the root article is being learned");
        Check(MathF.Abs(nation.ArmyExperience - 40f) < 0.01f, "the article was paid for when it was started");
        Check(TacticalDoctrines.Refusal(campaign, nation, branch.Id, "manoeuvre_screen", false).Length > 0,
            "a second article waits for the first to finish");

        SetDay(campaign, 130);
        TacticalDoctrines.Tick(campaign);
        Check(nation.TacticalDoctrine.Branch == "manoeuvre" && nation.TacticalDoctrine.Known.Contains("manoeuvre_march"),
            "the article lands on its day");
        Check(MathF.Abs(NationalModifiers.Of(nation, "army_speed") - 0.06f) < 0.0001f &&
              MathF.Abs(NationalModifiers.Of(nation, "organisation_recovery") - 0.05f) < 0.0001f,
            "a learned article reaches the army through NationalModifiers");
        Check(campaign.Chronicle.Entries.Any(e => e.Kind == "doctrine_article" && e.ActorId == nation.Id),
            "learning an article is recorded");

        // A crash course: the next article of the same tree, at once, for the rush price.
        nation.ArmyExperience = 100f;
        var rush = new StudyDoctrineCommand { IssuerId = nation.Id, BranchId = branch.Id, NodeId = "manoeuvre_screen", Rush = true };
        campaign.Commands.Enqueue(rush);
        campaign.Commands.ApplyAll(campaign);
        Check(rush.Refusal.Length == 0 && nation.TacticalDoctrine.Known.Contains("manoeuvre_screen"),
            "a crash course lands the same day");
        Check(MathF.Abs(nation.ArmyExperience - 30f) < 0.01f, "the crash course cost the rush price");
        Check(NationalModifiers.Of(nation, "supply") > 0.02f, "the rushed article counts at once");

        // Rewriting: another Doctrine, sold to the government and paid for in retraining.
        var before = NationalModifiers.Of(nation, "army_speed");
        Check(TacticalDoctrines.Refusal(campaign, nation, "fire", "fire_concentrated", false).Length > 0,
            "a rewrite without political credit is refused");
        nation.ArmyExperience = 200f;
        nation.PoliticalPoints = 20f;
        var rewrite = new StudyDoctrineCommand { IssuerId = nation.Id, BranchId = "fire", NodeId = "fire_concentrated", Rush = true };
        campaign.Commands.Enqueue(rewrite);
        campaign.Commands.ApplyAll(campaign);
        Check(rewrite.Refusal.Length == 0 && nation.TacticalDoctrine.Branch == "fire",
            "the Doctrine has been rewritten");
        Check(nation.TacticalDoctrine.Known.Count == 1 && nation.TacticalDoctrine.Known.Contains("fire_concentrated"),
            "the abandoned book is not kept");
        Check(NationalModifiers.Of(nation, "army_speed") < before,
            "the old Doctrine's articles stop counting");
        Check(nation.Modifiers.Any(m => m.Id == TacticalDoctrines.RetrainingModifier),
            "rewriting puts the army through retraining");
        Check(MathF.Abs(nation.PoliticalPoints - 8f) < 0.01f, "the rewrite was paid for in political points");
        Check(campaign.Chronicle.Entries.Any(e => e.Kind == "doctrine_rewritten" && e.ActorId == nation.Id),
            "rewriting the Doctrine is public news");

        // Saved state, and the fingerprint seeing it.
        var options = new JsonSerializerOptions { IncludeFields = true };
        var memory = JsonSerializer.Deserialize<TacticalDoctrineMemory>(
            JsonSerializer.Serialize(nation.TacticalDoctrine, options), options);
        Check(memory is not null && memory.Branch == nation.TacticalDoctrine.Branch &&
              memory.Known.SequenceEqual(nation.TacticalDoctrine.Known), "a Doctrine survives a save roundtrip");
        var print = CampaignFingerprint.Of(campaign);
        nation.TacticalDoctrine.Known.RemoveAt(0);
        Check(CampaignFingerprint.Of(campaign) != print, "the network fingerprint covers the Doctrine");
    }

    static void ExperienceSources()
    {
        var campaign = Campaign.CreateBootstrap(1702);
        var nation = campaign.NationByTag("ROU");
        var army = campaign.Armies.First(candidate => candidate.NationId == nation.Id);
        var baseline = campaign.Defines.BaselineMilitarySpending;

        // Peace, and no money for the army: only the small trickle.
        nation.MilitarySpending = baseline;
        nation.ArmyExperience = 0f;
        SetDay(campaign, 10); MilitaryExperience.Tick(campaign);
        var trickle = nation.ArmyExperience;
        Check(trickle > 0f && trickle < 0.1f, "peace teaches a little, and not a lot");

        // Funding above the baseline buys training value; below it buys none.
        nation.ArmyExperience = 0f;
        nation.MilitarySpending = baseline * 2f;
        SetDay(campaign, 11); MilitaryExperience.Tick(campaign);
        Check(nation.ArmyExperience > trickle * 2f, "a funded army learns faster than an unfunded one");
        nation.MilitarySpending = baseline * 0.5f;
        nation.ArmyExperience = 0f;
        SetDay(campaign, 12); MilitaryExperience.Tick(campaign);
        Check(nation.ArmyExperience <= trickle * 1.001f, "a cut teaches nobody faster");

        // The order: drilling costs money and earns experience.
        nation.MilitarySpending = baseline;
        army.BattleId = -1;   // InBattle is BattleId >= 0; the fixture writes the field it reads
        var training = new SetTrainingCommand { IssuerId = nation.Id, ArmyId = army.Id, Training = true };
        campaign.Commands.Enqueue(training);
        campaign.Commands.ApplyAll(campaign);
        Check(training.Refusal.Length == 0 && army.Training, "the formation is drilling");
        nation.ArmyExperience = 0f;
        var treasury = nation.Treasury;
        SetDay(campaign, 13); MilitaryExperience.Tick(campaign);
        Check(nation.ArmyExperience > trickle * 2f, "a day of drill is worth more than a day of peace");
        Check(nation.Treasury < treasury, "drill costs money");

        // Nobody drills under fire: the order clears itself the day the division fights.
        army.BattleId = 777;
        SetDay(campaign, 14); MilitaryExperience.Tick(campaign);
        Check(!army.Training, "a division sent into battle stops drilling the same day");
        var refused = new SetTrainingCommand { IssuerId = nation.Id, ArmyId = army.Id, Training = true };
        campaign.Commands.Enqueue(refused);
        campaign.Commands.ApplyAll(campaign);
        Check(refused.Refusal.Length > 0 && !army.Training, "and the order that asks anyway is refused");

        // Being shot at teaches too.
        nation.ArmyExperience = 0f;
        SetDay(campaign, 15); MilitaryExperience.Tick(campaign);
        army.BattleId = -1;
        Check(nation.ArmyExperience > trickle, "a division in battle learns from it");
    }

    static void AiChooses()
    {
        var campaign = Campaign.CreateBootstrap(1703);
        var nation = campaign.NationByTag("UKR");
        Check(AiStrategyCatalog.ProfileOf("UKR") == "attrition", "the fixture keeps its written character");
        Check(TacticalDoctrines.PreferredBranch(campaign, nation) == "fire",
            "a government of attrition reaches for the Doctrine of fire");
        nation.ArmyExperience = 200f;
        nation.PoliticalPoints = 50f;
        // Long enough for the patient road: the first article of a tree takes its own days, and
        // a government at war takes the crash course instead - either way it is done inside half a year.
        for (var day = 1; day <= 150; day++)
        {
            SetDay(campaign, day);
            TacticalDoctrines.Tick(campaign);
        }
        Check(nation.TacticalDoctrine.Branch == "fire" && nation.TacticalDoctrine.Known.Count >= 1,
            "the government studies its own book without being told");
        Check(NationalModifiers.Of(nation, "army_attack") > 0f,
            "and the article it learned reaches its army");
        Check(nation.ArmyExperience < 200f, "and pays for it in its own experience");
    }

    static void ContentSpeaks()
    {
        var campaign = Campaign.CreateBootstrap(1704);
        var nation = campaign.NationByTag("ROU");
        nation.Treasury = 50f;
        nation.ArmyExperience = 0f;
        nation.PoliticalPoints = 0f;

        Check(DecisionCatalog.Find("grand_manoeuvres") is not null &&
              DecisionCatalog.Find("push_the_reform") is not null,
            "the Doctrine's decisions ship with the game");

        // The currencies are ordinary scripted vocabulary now: readable in a trigger, writable
        // in an effect, so content can speak about them without a line of code.
        Check(ScriptedValues.TryRead(campaign, nation, "army_experience", out var read) && read == 0f,
            "a decision's trigger can read the army's experience");
        Check(ScriptedValues.TryRead(campaign, nation, "political_points", out _) &&
              ScriptedValues.TryRead(campaign, nation, "air_experience", out _) &&
              ScriptedValues.TryRead(campaign, nation, "naval_experience", out _),
            "and the government's credit, and both other services");

        var manoeuvres = new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = "grand_manoeuvres" };
        campaign.Commands.Enqueue(manoeuvres);
        campaign.Commands.ApplyAll(campaign);
        Check(MathF.Abs(nation.ArmyExperience - 10f) < 0.01f,
            "a decision can teach the army: grand manoeuvres pays experience");
        Check(nation.Treasury < 50f, "and the manoeuvres cost money");

        // The reform is gated on the credit it is about to spend, so it cannot be free.
        var definition = DecisionCatalog.Find("push_the_reform");
        Check(DecisionCatalog.BlockReason(campaign, nation, definition.Value).Length > 0,
            "without the credit, the reform is refused before it is even offered");
        nation.PoliticalPoints = 10f;
        var pushed = new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = "push_the_reform" };
        campaign.Commands.Enqueue(pushed);
        campaign.Commands.ApplyAll(campaign);
        Check(MathF.Abs(nation.PoliticalPoints - 2f) < 0.01f &&
              MathF.Abs(nation.ArmyExperience - 16f) < 0.01f,
            "with the credit, the reform spends it and the staff learns");

        Check(ScriptLibrary.Score(campaign, nation, "ai_grand_manoeuvres") > 0f &&
              ScriptLibrary.Score(campaign, nation, "ai_push_the_reform") > 0f &&
              ScriptLibrary.Score(campaign, nation, "ai_flight_school") > 0f,
            "and the AI has a stated appetite for all of it");
    }

    static void MenuSpeaks()
    {
        // The simulation harness need not touch InterfaceSkin before this suite.
        // Ask the real GUI loader instead of relying on another suite's static initialization.
        InterfaceSkin.ResetCatalog();
        // Where the player finds the doctrines: the Army panel's own tab (ArmyPanelUI), where the
        // experience that pays for them is earned. It used to be a Laws tab, and a future of the
        // whole army's way of fighting sat oddly among tax rates, so the screen changed and this
        // check changed with it: what a data test can prove now is the negative.
        Check(!CampaignUiCatalog.For("Laws").Tabs.Any(tab => tab.Id == "Doctrines"),
            "the Laws workspace no longer offers a Doctrines tab");
        // And the four currencies are reserved on the top bar, where a player watches them.
        var stats = CampaignUiCatalog.TopStats;
        Check(stats.Contains("ARMY XP") && stats.Contains("AIR XP") &&
              stats.Contains("NAVY XP") && stats.Contains("POLITICAL CAPITAL"),
            "the top bar reserves the four currencies");
        var language = Loc.Language;
        try
        {
            Loc.Language = "russian";
            Check(Loc.Get("Doctrine of the coast") == "Морская доктрина" &&
                  Loc.Get("Doctrine of the levy") == "Мобилизационная доктрина",
                "readable Russian doctrine captions win the real localization merge order");
            Check(Loc.Get("Fleet speed") == "Скорость флота" &&
                  Loc.Format("At once · {0}", "35") == "Освоить сразу · 35",
                "effect and immediate-study captions have Russian translations");
        }
        finally { Loc.Language = language; }
    }
}
