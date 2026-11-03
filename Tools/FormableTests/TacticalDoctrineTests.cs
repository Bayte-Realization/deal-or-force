using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>The war Doctrine: researched trees of articles paid for in the experience of their own
/// service, standing on four shelves that are learned side by side, with a rewrite that costs
/// political points, and the experience itself earned by fighting, drilling and being funded.</summary>
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
        FourShelves();
        ExperienceSources();
        AiChooses();
        ContentSpeaks();
        MenuSpeaks();
    }

    static void ShippedTree()
    {
        var branches = TacticalDoctrineCatalog.All.ToArray();
        // Dozens of articles, and every shelf has more than one book to choose from.
        Check(branches.Length >= 12, "the game ships a dozen books of war");
        Check(branches.Sum(branch => branch.Nodes.Count) >= 90,
            "and dozens of articles to learn in them");
        foreach (var slot in DoctrineSlots.All)
            Check(TacticalDoctrineCatalog.OnShelf(slot).Count() >= 2, $"the {slot} shelf offers a real choice");
        foreach (var branch in branches)
        {
            Check(branch.Nodes.Count >= 8, $"'{branch.Id}' is a tree of at least eight articles");
            Check(TacticalDoctrineCatalog.KnowsExperience(branch.Experience), $"'{branch.Id}' is paid for in a real service's experience");
            Check(DoctrineSlots.ForExperience(branch.Experience) == branch.Slot,
                $"'{branch.Id}' stands on the shelf its own service pays for");
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
        // A node id belongs to one article: the save speaks in these words.
        var allNodes = branches.SelectMany(branch => branch.Nodes.Select(node => node.Id)).ToArray();
        Check(allNodes.Distinct(StringComparer.Ordinal).Count() == allNodes.Length,
            "no two books of war share an article id");

        // A mod writes a branch in the same shape; a broken one is refused whole, by name.
        var errors = new List<string>();
        using (var doc = JsonDocument.Parse(
            "{\"mod_tree\":{\"name\":\"Mod tree\",\"experience\":\"army\",\"nodes\":[" +
            "{\"id\":\"mod_root\",\"name\":\"Root\",\"cost\":10,\"days\":30,\"modifiers\":{\"army_attack\":0.05}}," +
            "{\"id\":\"mod_leaf\",\"name\":\"Leaf\",\"requires\":[\"mod_root\"],\"cost\":20,\"days\":60,\"modifiers\":{\"army_defense\":0.05}}]}}"))
            TacticalDoctrineCatalog.Merge(doc.RootElement, errors);
        Check(errors.Count == 0 && TacticalDoctrineCatalog.Find("mod_tree")?.Nodes.Count == 2,
            "a mod's branch merges beside the shipped ones");
        Check(TacticalDoctrineCatalog.Find("mod_tree").Slot == DoctrineSlots.Ground,
            "a mod's army book lands on the ground shelf without saying so");
        Check(TacticalDoctrineCatalog.Find("mod_tree").Nodes[1].RushCost == 60f,
            "a node with no rush price rushes at three times its cost");

        // A book on the wrong shelf for its purse is refused: the shelves are not decoration.
        using (var badSlot = JsonDocument.Parse(
            "{\"bad_slot\":{\"name\":\"Bad\",\"experience\":\"navy\",\"slot\":\"air\",\"nodes\":[" +
            "{\"id\":\"bad_slot_node\",\"name\":\"Node\",\"cost\":10,\"days\":30}]}}"))
            TacticalDoctrineCatalog.Merge(badSlot.RootElement, errors);
        Check(errors.Count >= 1 && TacticalDoctrineCatalog.Find("bad_slot") is null,
            "a naval book claiming the air shelf is refused");

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
        // An article id already spoken for cannot be taken over by another book either.
        errors.Clear();
        using (var stolen = JsonDocument.Parse(
            "{\"stolen_tree\":{\"name\":\"Stolen\",\"experience\":\"army\",\"nodes\":[" +
            "{\"id\":\"manoeuvre_march\",\"name\":\"Borrowed\",\"cost\":10,\"days\":30}]}}"))
            TacticalDoctrineCatalog.Merge(stolen.RootElement, errors);
        Check(errors.Count >= 1 && TacticalDoctrineCatalog.Find("stolen_tree") is null,
            "an article id another book already holds is refused");
        TacticalDoctrineCatalog.ResetCatalog();
    }

    static void StudyAndRewrite()
    {
        var campaign = Campaign.CreateBootstrap(1701);
        var nation = campaign.NationByTag("ROU");
        // A human country does not study behind the player's back: the test drives every order.
        campaign.ConfigureSinglePlayer(nation.Id);
        // The tick also offers the next article to a computer government. This test is the
        // player's own order, so the cabinet must not spend the learning slot first.
        nation.IsHuman = true;
        var branch = TacticalDoctrineCatalog.Find("manoeuvre");
        var ground = nation.TacticalDoctrine.Track(DoctrineSlots.Ground);

        Check(TacticalDoctrines.Refusal(campaign, nation, branch.Id, "manoeuvre_march", false).Length > 0,
            "an article nobody can pay for is refused with a sentence");
        Check(ground.Known.Count == 0 && nation.ArmyExperience == 0f,
            "a refused article spends nothing");

        nation.ArmyExperience = 60f;
        Check(TacticalDoctrines.Refusal(campaign, nation, branch.Id, "manoeuvre_columns", false).Length > 0,
            "the tree opens from the root, not from the middle");
        var command = new StudyDoctrineCommand { IssuerId = nation.Id, BranchId = branch.Id, NodeId = "manoeuvre_march" };
        campaign.Commands.Enqueue(command);
        campaign.Commands.ApplyAll(campaign);
        Check(command.Refusal.Length == 0 && ground.LearningNode == "manoeuvre_march",
            "the root article is being learned");
        Check(MathF.Abs(nation.ArmyExperience - 40f) < 0.01f, "the article was paid for when it was started");
        Check(TacticalDoctrines.Refusal(campaign, nation, branch.Id, "manoeuvre_screen", false).Length > 0,
            "a second article on the same shelf waits for the first to finish");

        SetDay(campaign, 130);
        TacticalDoctrines.Tick(campaign);
        Check(ground.Branch == "manoeuvre" && ground.Known.Contains("manoeuvre_march"),
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
        Check(rush.Refusal.Length == 0 && ground.Known.Contains("manoeuvre_screen"),
            "a crash course lands the same day");
        Check(MathF.Abs(nation.ArmyExperience - 30f) < 0.01f, "the crash course cost the rush price");
        Check(NationalModifiers.Of(nation, "supply") > 0.02f, "the rushed article counts at once");

        // One book on the shelf: a different Doctrine replaces it.
        campaign.Defines.DoctrineBooksPerShelf = 1;
        // Rewriting: another Doctrine on the same shelf, sold to the government and paid for in retraining.
        var before = NationalModifiers.Of(nation, "army_speed");
        Check(TacticalDoctrines.Refusal(campaign, nation, "fire", "fire_concentrated", false).Length > 0,
            "a rewrite without political credit is refused");
        nation.ArmyExperience = 200f;
        nation.PoliticalPoints = 20f;
        var rewrite = new StudyDoctrineCommand { IssuerId = nation.Id, BranchId = "fire", NodeId = "fire_concentrated", Rush = true };
        campaign.Commands.Enqueue(rewrite);
        campaign.Commands.ApplyAll(campaign);
        Check(rewrite.Refusal.Length == 0 && ground.Branch == "fire",
            "the Doctrine has been rewritten");
        Check(ground.Known.Count == 1 && ground.Known.Contains("fire_concentrated"),
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
        Check(memory is not null && memory.Track(DoctrineSlots.Ground).Branch == ground.Branch &&
              memory.Track(DoctrineSlots.Ground).Known.SequenceEqual(ground.Known),
            "a Doctrine survives a save roundtrip");
        var print = CampaignFingerprint.Of(campaign);
        ground.Known.RemoveAt(0);
        Check(CampaignFingerprint.Of(campaign) != print, "the network fingerprint covers the Doctrine");
    }

    /// <summary>The point of the shelves: a country is never made to choose a navy instead of an air force.</summary>
    static void FourShelves()
    {
        var campaign = Campaign.CreateBootstrap(1705);
        var nation = campaign.NationByTag("ROU");
        // A human country does not study behind the player's back: the test drives every order.
        campaign.ConfigureSinglePlayer(nation.Id);
        nation.IsHuman = true;
        var ground = nation.TacticalDoctrine.Track(DoctrineSlots.Ground);
        var air = nation.TacticalDoctrine.Track(DoctrineSlots.Air);
        var sea = nation.TacticalDoctrine.Track(DoctrineSlots.Sea);
        var staff = nation.TacticalDoctrine.Track(DoctrineSlots.General);

        nation.ArmyExperience = 200f;
        nation.AirExperience = 200f;
        nation.NavalExperience = 200f;
        nation.PoliticalPoints = 200f;

        // Three books at once, each paid from its own purse, each learning on its own days.
        Check(TacticalDoctrines.Study(campaign, nation, "manoeuvre", "manoeuvre_march", true),
            "the army takes its first article");
        Check(TacticalDoctrines.Study(campaign, nation, "sky", "sky_crews", false),
            "the air force starts its own book the same day");
        Check(TacticalDoctrines.Study(campaign, nation, "coast", "coast_patrol", false),
            "and the fleet starts its own");
        Check(ground.Branch == "manoeuvre" && air.LearningNode == "sky_crews" &&
              sea.LearningNode == "coast_patrol",
            "three shelves are working at once");
        Check(MathF.Abs(nation.ArmyExperience - 145f) < 0.01f &&
              MathF.Abs(nation.AirExperience - 188f) < 0.01f &&
              MathF.Abs(nation.NavalExperience - 188f) < 0.01f,
            "each shelf spent its own service's experience and nobody else's");

        SetDay(campaign, 200);
        TacticalDoctrines.Tick(campaign);
        Check(air.Branch == "sky" && air.Known.Contains("sky_crews") &&
              sea.Branch == "coast" && sea.Known.Contains("coast_patrol"),
            "the air and sea books land on their own days");
        Check(NationalModifiers.Of(nation, "support_recovery") > 0.07f &&
              NationalModifiers.Of(nation, "naval_speed") > 0.07f &&
              NationalModifiers.Of(nation, "army_speed") > 0.05f,
            "all three books reach the country at once");
        Check(TacticalDoctrines.Books(nation).Count() == 3, "the country is reading three books");

        // One book on the shelf: changing the army's mind replaces that book and leaves the others.
        campaign.Defines.DoctrineBooksPerShelf = 1;
        // Rewriting one shelf leaves the others untouched.
        Check(TacticalDoctrines.Study(campaign, nation, "fortress", "fortress_dig", true),
            "the ground shelf may change its mind");
        Check(ground.Branch == "fortress" && air.Branch == "sky" && sea.Branch == "coast",
            "rewriting the army book did not touch the air or sea book");
        Check(NationalModifiers.Of(nation, "naval_speed") > 0.07f,
            "and the fleet's article still counts");

        // The general staff's book is bought with political credit, not with a service's experience.
        var credit = nation.PoliticalPoints;
        var armyLeft = nation.ArmyExperience;
        Check(TacticalDoctrines.Study(campaign, nation, "staff", "staff_plans", true),
            "the staff takes its first article");
        Check(staff.Branch == "staff" && nation.PoliticalPoints < credit - 7f,
            "and pays for it in political credit");
        Check(MathF.Abs(nation.ArmyExperience - armyLeft) < 0.01f, "not in the army's experience");
        Check(DoctrineSlots.ForExperience(MilitaryExperience.Command) == DoctrineSlots.General,
            "command experience is the general shelf's own purse");

        // An article on one shelf does not open an article on another.
        Check(TacticalDoctrines.Available(nation, "sky").Any(node => node.Id == "sky_cover") &&
              !TacticalDoctrines.Available(nation, "coast").Any(node => node.Id == "sky_cover"),
            "the air book opens its own next articles and nobody else's");

        // A save written before the shelves existed: one book, read onto the shelf of its service.
        var legacy = Campaign.CreateBootstrap(1706);
        var old = legacy.NationByTag("ROU");
        old.TacticalDoctrine = new TacticalDoctrineMemory
        {
            Branch = "coast", Known = new List<string> { "coast_patrol" },
            LearningBranch = "coast", LearningNode = "coast_yards", LearningDoneDay = 40,
            LastRewriteDay = 5
        };
        TacticalDoctrines.EnsureShelves(old);
        var opened = old.TacticalDoctrine.Track(DoctrineSlots.Sea);
        Check(opened.Branch == "coast" && opened.Known.Contains("coast_patrol") &&
              opened.LearningNode == "coast_yards" && opened.LearningDoneDay == 40,
            "a one-book save opens on the shelf its service belongs to");
        Check(old.TacticalDoctrine.Track(DoctrineSlots.Ground).Branch.Length == 0,
            "and leaves the other shelves empty");
        NationalModifiers.Invalidate(old);
        var withArticle = NationalModifiers.Of(old, "naval_speed");
        opened.Known.Clear();
        NationalModifiers.Invalidate(old);
        var withoutArticle = NationalModifiers.Of(old, "naval_speed");
        opened.Known.Add("coast_patrol");
        NationalModifiers.Invalidate(old);
        var article = TacticalDoctrineCatalog.Find("coast").Find("coast_patrol").Modifiers["naval_speed"];
        Check(article > 0f && withArticle > withoutArticle + 0.0001f,
            "its article still reaches the country (" + withArticle + " versus " + withoutArticle + ", article " + article + ")");
        Check(TacticalDoctrines.BooksHeld(old, false) == 1, "an old save with one doctrine still counts as one slot");

        // Three tactical shelves and the general shelf. A further tactical shelf is refused.
        var full = Campaign.CreateBootstrap(1707);
        full.Defines.DoctrineTacticalSlots = 3;
        full.Defines.DoctrineGeneralSlots = 1;
        var reader = full.NationByTag("ROU");
        reader.ArmyExperience = reader.AirExperience = reader.NavalExperience = 500f;
        reader.PoliticalPoints = 80f;
        Check(TacticalDoctrines.Study(full, reader, "manoeuvre", "manoeuvre_march", true) &&
              TacticalDoctrines.Study(full, reader, "sky", "sky_crews", true) &&
              TacticalDoctrines.Study(full, reader, "coast", "coast_patrol", true) &&
              TacticalDoctrines.Study(full, reader, "staff", "staff_plans", true),
            "one land, one air, one naval and one general doctrine are held together");
        Check(TacticalDoctrines.BooksHeld(reader, false) == 3 && TacticalDoctrines.BooksHeld(reader, true) == 1,
            "that is three tactical slots and one general slot");
        full.Defines.DoctrineTacticalSlots = 3;
        var fifth = TacticalDoctrines.Refusal(full, reader, "sea_denial", "sub_boats", true);
        // The sea shelf is already the naval book. A second naval book rewrites that shelf;
        // it must not become a fourth tactical book.
        if (fifth.Length == 0)
            TacticalDoctrines.Study(full, reader, "sea_denial", "sub_boats", true);
        var seaShelf = reader.TacticalDoctrine.Track(DoctrineSlots.Sea);
        Check(seaShelf.Branch == "coast" && seaShelf.Companion?.Branch == "sea_denial",
            "the second naval book stands beside the first");
        Check(TacticalDoctrines.BooksHeld(reader, false) == 3,
            "a second book on the sea shelf does not open a fourth tactical slot");
        var another = TacticalDoctrineCatalog.OnShelf(DoctrineSlots.Sea)
            .FirstOrDefault(book => book.Id != "coast" && book.Id != "sea_denial");
        if (another is not null)
        {
            var root = another.Nodes.First(node => node.Requires.Count == 0);
            Check(TacticalDoctrines.Refusal(full, reader, another.Id, root.Id, true).Length > 0,
                "a third book on the same shelf is refused");
        }

        var tight = Campaign.CreateBootstrap(1708);
        tight.Defines.DoctrineTacticalSlots = 2;
        var limited = tight.NationByTag("ROU");
        limited.ArmyExperience = limited.AirExperience = limited.NavalExperience = 500f;
        limited.PoliticalPoints = 80f;
        Check(TacticalDoctrines.Study(tight, limited, "manoeuvre", "manoeuvre_march", true) &&
              TacticalDoctrines.Study(tight, limited, "sky", "sky_crews", true),
            "two tactical doctrines fit under a cap of two");
        var blocked = TacticalDoctrines.Refusal(tight, limited, "coast", "coast_patrol", true);
        Check(blocked.Length > 0 && limited.TacticalDoctrine.Track(DoctrineSlots.Sea).Branch.Length == 0 &&
              limited.NavalExperience == 500f,
            "a further tactical doctrine is refused, and nothing is spent");
        Check(TacticalDoctrines.Study(tight, limited, "staff", "staff_plans", true) &&
              TacticalDoctrines.BooksHeld(limited, true) == 1,
            "the general doctrine is still allowed when the tactical cap is full");
        Check(TacticalDoctrines.Refusal(tight, limited, "coast", "coast_patrol", true).Length > 0,
            "the general doctrine did not free a tactical slot");
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
        var ground = nation.TacticalDoctrine.Track(DoctrineSlots.Ground);
        Check(ground.Branch == "fire" && ground.Known.Count >= 1,
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
              ScriptedValues.TryRead(campaign, nation, "air_experience", out _),
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
        // experience that pays for them is earned - and a screen of its own on the wide layout.
        // A tab that is declared but not allowed, or a screen that is navigated to but never
        // declared, is a screen nobody can open: this is the pair the interface actually needs.
        Check(CampaignUiCatalog.NavigationItems.Any(row => row.Screen == "Doctrines"),
            "the navigation offers the book of war");
        Check(CampaignUiCatalog.Destinations.Contains("Doctrines"),
            "the book of war is an allowed destination");
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
            Check(Loc.Get("Doctrine of the general staff") == "Доктрина генерального штаба" &&
                  Loc.Get("naval doctrine") == "морская доктрина",
                "the new shelves and staff books speak Russian too");
        }
        finally { Loc.Language = language; }
    }
}
