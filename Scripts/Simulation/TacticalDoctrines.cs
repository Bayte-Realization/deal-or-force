using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Political points a Doctrine rewrite costs: the government has to sell the reform.</summary>
    public float DoctrineRewritePoliticalCost { get; set; } = 12f;

    /// <summary>How long the army is retraining after its Doctrine has been rewritten.</summary>
    public int DoctrineRetrainingDays { get; set; } = 180;

    /// <summary>How long an AI government waits before rewriting its Doctrine again.</summary>
    public int DoctrineAiRewriteCooldownDays { get; set; } = 365;
}

/// <summary>
/// One node of a Doctrine's tree: what it costs in its service's experience, what it needs first,
/// and what it gives. `rush_cost` is the price of learning it now instead of over `days`.
/// </summary>
public sealed record DoctrineNode(string Id, string Name, string Description, int Days, float Cost,
    float RushCost, IReadOnlyList<string> Requires, IReadOnlyDictionary<string, float> Modifiers);

/// <summary>A branch of the war Doctrine: one philosophy of war, learned as a tree.</summary>
public sealed record DoctrineBranch(string Id, string Name, string Description, string Experience,
    IReadOnlyList<DoctrineNode> Nodes)
{
    public DoctrineNode Find(string nodeId) =>
        nodeId is null ? null : Nodes.FirstOrDefault(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal));
}

/// <summary>What the army is reading: the Doctrine in force, its learned nodes, and what is being learned now.</summary>
public sealed class TacticalDoctrineMemory
{
    /// <summary>The Doctrine in force. A node's bonuses only count while its Doctrine is the one in force.</summary>
    public string Branch = "";

    /// <summary>Nodes learned under the Doctrine in force. A rewrite starts the new Doctrine empty.</summary>
    public List<string> Known = new();

    public string LearningBranch = "";
    public string LearningNode = "";
    public int LearningDoneDay = -1;
    public int LastRewriteDay = -9999;
}

public sealed partial class Nation
{
    public TacticalDoctrineMemory TacticalDoctrine = new();

    /// <summary>Experience of the three services, earned by doing (MilitaryExperience).</summary>
    public float ArmyExperience;
    public float AirExperience;
    public float NavalExperience;

    /// <summary>What a government can push through besides money (MilitaryExperience).</summary>
    public float PoliticalPoints;
}

/// <summary>
/// The country's book of war, read from common/Doctrine like every other catalogue: the base game
/// and a mod write the same thing, and a branch a mod adds is a branch.
/// </summary>
public static class TacticalDoctrineCatalog
{
    /// <summary>What a rushed node costs when its data names no price: three times the patient one.</summary>
    public const float RushCostMultiplier = 3f;

    private static readonly Dictionary<string, DoctrineBranch> Entries = new(StringComparer.Ordinal);
    public static int Revision { get; private set; }
    public static IEnumerable<DoctrineBranch> All => Entries.Values.OrderBy(branch => branch.Id, StringComparer.Ordinal);
    public static DoctrineBranch Find(string id) => id is null ? null : Entries.GetValueOrDefault(id);
    public static bool KnowsExperience(string kind) =>
        kind is MilitaryExperience.Army or MilitaryExperience.Air or MilitaryExperience.Navy;
    static TacticalDoctrineCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Entries.Clear(); Revision++;
        foreach (var doc in GameDataLoader.DataDocuments("common", "tactical_doctrines")) using (doc) Merge(doc.RootElement);
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("Doctrine: expected an object"); return; }
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//")) continue;
            try
            {
                if (!MechanicFeatures.ValidId(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("invalid Doctrine branch");
                var e = entry.Value;
                string Text(string key, string fallback) => e.TryGetProperty(key, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString()
                        : throw new ArgumentException(key + " must be a string")
                    : fallback;
                // Read numbers as numbers. JsonElement.TryGetSingle throws on a string, and a mod
                // writing "cost": "20" must be refused with a sentence, not a stack trace.
                float Number(JsonElement item, string key, float fallback)
                {
                    if (!item.TryGetProperty(key, out var v)) return fallback;
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetSingle(out var parsed) || !float.IsFinite(parsed))
                        throw new ArgumentException(key + " must be a number");
                    return parsed;
                }
                var experience = Text("experience", "").Trim().ToLowerInvariant();
                if (!KnowsExperience(experience))
                    throw new ArgumentException("experience must be army, air or navy");
                if (!e.TryGetProperty("nodes", out var list) || list.ValueKind != JsonValueKind.Array ||
                    list.GetArrayLength() == 0)
                    throw new ArgumentException("a Doctrine with no nodes is not a Doctrine");
                var nodes = new List<DoctrineNode>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("a node must be an object");
                    var id = item.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                        ? idValue.GetString() : "";
                    if (!MechanicFeatures.ValidId(id) || !seen.Add(id))
                        throw new ArgumentException($"node id '{id}' is invalid or repeated");
                    var days = (int)Number(item, "days", 0f);
                    var cost = Number(item, "cost", 0f);
                    if (days <= 0 || cost <= 0f)
                        throw new ArgumentException($"node '{id}' needs positive days and cost");
                    var rush = Number(item, "rush_cost", MathF.Ceiling(cost * RushCostMultiplier));
                    if (rush < cost)
                        throw new ArgumentException($"node '{id}' rushes for less than it studies");
                    var requires = new List<string>();
                    if (item.TryGetProperty("requires", out var needs) && needs.ValueKind == JsonValueKind.Array)
                        foreach (var need in needs.EnumerateArray())
                            requires.Add(need.ValueKind == JsonValueKind.String ? need.GetString() : "");
                    var local = new List<string>();
                    var modifiers = item.TryGetProperty("modifiers", out var block)
                        ? NationalModifiers.Read(block, $"Doctrine.{entry.Name}.{id}", local)
                        : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    if (local.Count != 0) throw new ArgumentException(string.Join("; ", local));
                    var name = item.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                        ? nameValue.GetString() : id;
                    var description = item.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String
                        ? text.GetString() : "";
                    nodes.Add(new DoctrineNode(id, name, description, days, cost, rush, requires, modifiers));
                }
                // Prerequisites are checked once the whole branch is read, so a mod may write its
                // tree in whatever order reads best and a missing node is refused by name.
                foreach (var node in nodes)
                    foreach (var need in node.Requires)
                        if (!seen.Contains(need))
                            throw new ArgumentException($"node '{node.Id}' requires '{need}', which this branch does not hold");
                Entries[entry.Name] = new DoctrineBranch(entry.Name, Text("name", entry.Name),
                    Text("description", ""), experience, nodes);
                Revision++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
            {
                errors?.Add($"Doctrine.{entry.Name}: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// The country's book of war: what its army is taught to be, and what changing its mind costs.
///
/// A Doctrine is a tree, studied the way technologies are: nodes open by their prerequisites, and
/// each is paid for in the experience its own service actually earned (MilitaryExperience). A
/// node can be taken patiently - the cost, over its days - or as a crash course, which is the
/// rush price and lands at once. Nothing here is decorative: every node's block is read by the
/// simulation's own army, supply and training arithmetic through NationalModifiers.
///
/// Rewriting a Doctrine is the point of the mechanic, not an exception to it: a country whose war
/// stopped being the war it prepared for can change its book, and pays twice - political points
/// for the government that has to sell the reform, and a season of retraining in which the old
/// habits are gone and the new ones are not learned (the doctrine_retraining spirit). The nodes of
/// the abandoned Doctrine are not kept. That is what makes the choice real.
/// </summary>
public static class TacticalDoctrines
{
    /// <summary>The spirit granted while an army relearns: see common/modifiers/46_retraining.json.</summary>
    public const string RetrainingModifier = "doctrine_retraining";

    /// <summary>Which Doctrine each character of government leans towards (AiStrategyCatalog).</summary>
    private static readonly Dictionary<string, string> CharacterBranches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blitzkrieg"] = "manoeuvre", ["irredentist"] = "manoeuvre",
        ["attrition"] = "fire", ["major_offensive"] = "fire", ["default"] = "fire",
        ["defensive"] = "fortress", ["fortress"] = "fortress", ["garrison_state"] = "fortress",
        ["isolated"] = "fortress",
        ["minor_power"] = "levy",
        ["expeditionary"] = "sky", ["trading_state"] = "coast"
    };

    public static float Experience(Nation nation, string kind) => kind switch
    {
        MilitaryExperience.Air => nation.AirExperience,
        MilitaryExperience.Navy => nation.NavalExperience,
        _ => nation.ArmyExperience
    };

    private static void Spend(Nation nation, string kind, float cost)
    {
        switch (kind)
        {
            case MilitaryExperience.Air: nation.AirExperience = MathF.Max(0f, nation.AirExperience - cost); break;
            case MilitaryExperience.Navy: nation.NavalExperience = MathF.Max(0f, nation.NavalExperience - cost); break;
            default: nation.ArmyExperience = MathF.Max(0f, nation.ArmyExperience - cost); break;
        }
    }

    public static string ServiceName(string kind) => kind switch
    {
        MilitaryExperience.Air => "air experience",
        MilitaryExperience.Navy => "naval experience",
        _ => "army experience"
    };

    /// <summary>Whether this node has been learned under the Doctrine in force.</summary>
    public static bool Known(Nation nation, string nodeId) =>
        nation.TacticalDoctrine.Known.Contains(nodeId);

    /// <summary>
    /// The nodes of a branch that can be started now: not learned, and every prerequisite learned.
    /// For a branch that is not the one in force this is its roots, because a rewrite starts empty.
    /// </summary>
    public static List<DoctrineNode> Available(Nation nation, string branchId)
    {
        var branch = TacticalDoctrineCatalog.Find(branchId);
        var found = new List<DoctrineNode>();
        if (branch is null) return found;
        var inForce = string.Equals(nation.TacticalDoctrine.Branch, branchId, StringComparison.Ordinal);
        foreach (var node in branch.Nodes)
        {
            if (inForce && Known(nation, node.Id)) continue;
            var ready = true;
            foreach (var need in node.Requires)
                if (!inForce || !Known(nation, need)) { ready = false; break; }
            if (ready) found.Add(node);
        }
        return found;
    }

    private static bool Rewriting(Nation nation, string branchId) =>
        nation.TacticalDoctrine.Branch.Length > 0 &&
        !string.Equals(nation.TacticalDoctrine.Branch, branchId, StringComparison.Ordinal);

    /// <summary>Why this node cannot be started now, in the words the interface shows. Empty means it can.</summary>
    public static string Refusal(Campaign campaign, Nation nation, string branchId, string nodeId, bool rush)
    {
        if (!campaign.FeatureEnabled("tactical_doctrines")) return Loc.Get("This mechanic is disabled in this scenario.");
        var branch = TacticalDoctrineCatalog.Find(branchId);
        if (branch is null) return Loc.Get("No such Doctrine is written.");
        var node = branch.Find(nodeId);
        if (node is null) return Loc.Get("No such article of the Doctrine is written.");
        if (Known(nation, node.Id)) return Loc.Get("This article has already been learned.");
        if (nation.TacticalDoctrine.LearningNode.Length > 0) return Loc.Get("The army is already learning an article.");
        foreach (var need in node.Requires)
            if (!Known(nation, need)) return Loc.Get("The earlier articles of this Doctrine come first.");
        if (Rewriting(nation, branch.Id) &&
            nation.PoliticalPoints + 0.001f < campaign.Defines.DoctrineRewritePoliticalCost)
            return Loc.Format("Rewriting the Doctrine costs {0} political points.",
                campaign.Defines.DoctrineRewritePoliticalCost);
        var price = rush ? node.RushCost : node.Cost;
        if (Experience(nation, branch.Experience) + 0.001f < price)
            return Loc.Format("This article needs {0} points of {1}.",
                price, Loc.Get(ServiceName(branch.Experience)));
        return "";
    }

    /// <summary>Start a node, patiently or as a crash course. The one door: the command, the AI and a test all use it.</summary>
    public static bool Study(Campaign campaign, Nation nation, string branchId, string nodeId, bool rush)
    {
        var branch = TacticalDoctrineCatalog.Find(branchId);
        var node = branch?.Find(nodeId);
        if (branch is null || node is null || Refusal(campaign, nation, branchId, nodeId, rush).Length > 0) return false;
        if (Rewriting(nation, branchId))
        {
            nation.PoliticalPoints = MathF.Max(0f, nation.PoliticalPoints - campaign.Defines.DoctrineRewritePoliticalCost);
            nation.TacticalDoctrine.Branch = branch.Id;
            nation.TacticalDoctrine.Known.Clear();     // the abandoned book is not kept
            nation.TacticalDoctrine.LastRewriteDay = campaign.ElapsedDays;
            ModifierSystem.Grant(campaign, nation, RetrainingModifier, campaign.Defines.DoctrineRetrainingDays);
            campaign.Chronicle.Record(campaign, "doctrine_rewritten", nation.Id, subject: branch.Name, isPublic: true);
            NationalModifiers.Invalidate(nation);
            if (Delegation.RunByAi(nation)) Count("doctrine_rewritten");
        }
        // Paid after every refusal has passed: an article the country cannot afford must not
        // leave it half-taught and out of pocket.
        Spend(nation, branch.Experience, rush ? node.RushCost : node.Cost);
        if (rush)
        {
            Learn(campaign, nation, branch, node);
        }
        else
        {
            nation.TacticalDoctrine.LearningBranch = branch.Id;
            nation.TacticalDoctrine.LearningNode = node.Id;
            nation.TacticalDoctrine.LearningDoneDay = campaign.ElapsedDays + Math.Max(1, node.Days);
        }
        campaign.MarkStrategicChange();
        return true;
    }

    private static void Learn(Campaign campaign, Nation nation, DoctrineBranch branch, DoctrineNode node)
    {
        // Whose article has just landed is whose Doctrine is in force: a crash course taken from
        // nothing must start the Doctrine, not leave the army with a known article and no book.
        nation.TacticalDoctrine.Branch = branch.Id;
        nation.TacticalDoctrine.LearningBranch = "";
        nation.TacticalDoctrine.LearningNode = "";
        nation.TacticalDoctrine.LearningDoneDay = -1;
        if (!nation.TacticalDoctrine.Known.Contains(node.Id)) nation.TacticalDoctrine.Known.Add(node.Id);
        NationalModifiers.Invalidate(nation);
        campaign.Chronicle.Record(campaign, "doctrine_article", nation.Id, subject: node.Name);
        // The bench's own record of what the AI did with its book, beside its war counters.
        if (Delegation.RunByAi(nation)) Count("doctrine_adopted");
    }

    private static void Count(string what) =>
        AiStatecraft.Tally[what] = AiStatecraft.Tally.GetValueOrDefault(what) + 1;

    /// <summary>
    /// What has come due, and what every other government is doing about its own book. Each
    /// country keeps its own day of the week, the spread the rest of the cabinet already uses.
    /// </summary>

    public static void Tick(Campaign campaign)
    {
        if (campaign is null) return;
        var day = campaign.ElapsedDays;
        var runAi = campaign.FeatureEnabled("tactical_doctrines") &&
                    campaign.LastTacticalDoctrineDay != day;
        if (runAi)
        {
            campaign.LastTacticalDoctrineDay = day;
        }
        foreach (var nation in campaign.Nations)
        {
            var memory = nation.TacticalDoctrine;
            if (memory.LearningNode.Length > 0 && day >= memory.LearningDoneDay)
            {
                var branch = TacticalDoctrineCatalog.Find(memory.LearningBranch);
                var node = branch?.Find(memory.LearningNode);
                if (branch is null || node is null)
                {
                    memory.LearningBranch = ""; memory.LearningNode = ""; memory.LearningDoneDay = -1;
                }
                else
                {
                    Learn(campaign, nation, branch, node);
                    campaign.MarkStrategicChange();
                }
            }
            if (!runAi || !Delegation.RunByAi(nation)) continue;
            if ((day + nation.Id) % 7 != 0) continue;
            AiStudy(campaign, nation);
        }
    }

    /// <summary>
    /// A government improving its own book: the Doctrine its character prefers, the cheapest
    /// article open right now, and a crash course when it is at war and can pay for one - war is
    /// when the next article is worth more than the money of waiting.
    /// </summary>
    private static void AiStudy(Campaign campaign, Nation nation)
    {
        if (nation.TacticalDoctrine.LearningNode.Length > 0) return;
        var branch = TacticalDoctrineCatalog.Find(PreferredBranch(campaign, nation));
        if (branch is null) return;
        if (Rewriting(nation, branch.Id) && nation.TacticalDoctrine.Known.Count > 0 &&
            campaign.ElapsedDays - nation.TacticalDoctrine.LastRewriteDay <
            Math.Max(30, campaign.Defines.DoctrineAiRewriteCooldownDays)) return;
        var have = Experience(nation, branch.Experience);
        var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        DoctrineNode choice = null;
        var rush = false;
        foreach (var node in Available(nation, branch.Id))
        {
            if (atWar && have + 0.001f >= node.RushCost) { choice = node; rush = true; break; }
            if (have + 0.001f < node.Cost) continue;
            if (choice is null || node.Cost < choice.Cost) choice = node;
        }
        if (choice is null) return;
        Study(campaign, nation, branch.Id, choice.Id, rush);
    }

    /// <summary>
    /// The tactical tree this country reaches for: the one its grand strategy leans on if the
    /// strategy names one, otherwise the one its character prefers (AiStrategyCatalog.Of).
    /// </summary>
    public static string PreferredBranch(Campaign campaign, Nation nation)
    {
        var bias = StrategicDoctrines.TacticalBias(nation);
        if (bias.Length > 0) return bias;
        return CharacterBranches.TryGetValue(AiStrategyCatalog.Of(campaign, nation).Id, out var branch) ? branch : "fire";
    }

    /// <summary>The articles in force, as one source of national modifiers - read by NationalModifiers.</summary>
    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation nation)
    {
        var memory = nation?.TacticalDoctrine;
        var branch = memory is null ? null : TacticalDoctrineCatalog.Find(memory.Branch);
        if (branch is null || memory.Known.Count == 0) yield break;
        // The dictionary is the country's own, kept and refilled: asked once a day for every
        // country with a doctrine, and a fresh dictionary a day was the collector's budget.
        var totals = nation.TacticalDoctrineModifiers ??= new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        totals.Clear();
        foreach (var node in branch.Nodes)
        {
            if (!memory.Known.Contains(node.Id)) continue;
            foreach (var effect in node.Modifiers)
                totals[effect.Key] = totals.GetValueOrDefault(effect.Key) + effect.Value;
        }
        if (totals.Count > 0) yield return (branch.Name, totals);
    }
}

public sealed partial class Nation
{
    /// <summary>What the army's book of war adds, kept between asks. Not saved; refilled from scratch.</summary>
    internal Dictionary<string, float> TacticalDoctrineModifiers;
}

public sealed class StudyDoctrineCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string BranchId { get; init; } = "";
    public string NodeId { get; init; } = "";
    public bool Rush { get; init; }

    /// <summary>Why the order could not be carried out, in the words the interface shows.</summary>
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        Refusal = TacticalDoctrines.Refusal(campaign, nation, BranchId, NodeId, Rush);
        if (Refusal.Length > 0) return;
        TacticalDoctrines.Study(campaign, nation, BranchId, NodeId, Rush);
    }
}
