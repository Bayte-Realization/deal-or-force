using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// How many books of war one country may hold at once: one for the ground forces, one for
    /// the air, one for the fleet, and one general staff book. They are learned side by side -
    /// a navy does not forget how to fight because the army changed its mind.
    /// </summary>
    public int DoctrineTracks { get; set; } = 4;

    /// <summary>
    /// Tactical books held at once: ground, air and sea, learned side by side. A further tactical
    /// book, including a rewrite of a shelf once this many are already in force, is refused.
    /// </summary>
    public int DoctrineTacticalSlots { get; set; } = 3;

    /// <summary>General-staff books held at once. The staff's book does not spend a tactical slot.</summary>
    public int DoctrineGeneralSlots { get; set; } = 1;

    /// <summary>
    /// Books standing on one shelf at once. The second is learned beside the first.
    /// One keeps the older rule: a different book on that shelf replaces it.
    /// </summary>
    public int DoctrineBooksPerShelf { get; set; } = 2;

    /// <summary>
    /// How much of the smaller contribution a second book on the same shelf still adds,
    /// when both books carry the same modifier. The larger contribution is kept whole.
    /// Half means the second book matters and does not double the bonus. One adds both
    /// in full, the way a single shelf used to once the books were simply summed. Zero
    /// keeps only the larger. Books on different shelves are not touched by this.
    /// </summary>
    public const float DefaultDoctrineSameShelfStack = 0.5f;

    public float DoctrineSameShelfStack { get; set; } = DefaultDoctrineSameShelfStack;
}

/// <summary>
/// One node of a Doctrine's tree: what it costs in its service's experience, what it needs first,
/// and what it gives. `rush_cost` is the price of learning it now instead of over `days`.
/// </summary>
public sealed record DoctrineNode(string Id, string Name, string Description, int Days, float Cost,
    float RushCost, IReadOnlyList<string> Requires, IReadOnlyDictionary<string, float> Modifiers)
{
    /// <summary>Alternative articles in this book. Catalogue loading makes exclusions reciprocal.</summary>
    public IReadOnlyList<string> Excludes { get; init; } = Array.Empty<string>();
    /// <summary>Optional priorities by AI character; an absent character keeps ordinary study order.</summary>
    public IReadOnlyDictionary<string, float> AiProfiles { get; init; } = new Dictionary<string, float>();
}

/// <summary>A branch of the war Doctrine: one philosophy of war, learned as a tree.</summary>
public sealed record DoctrineBranch(string Id, string Name, string Description, string Experience,
    string Slot, IReadOnlyList<DoctrineNode> Nodes)
{
    /// <summary>Optional data-authored preference by AI character. No country tags or saved state.</summary>
    public IReadOnlyDictionary<string, float> AiProfiles { get; init; } = new Dictionary<string, float>();

    public DoctrineNode Find(string nodeId) =>
        nodeId is null ? null : Nodes.FirstOrDefault(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal));
}

/// <summary>
/// The four shelves a country's books of war stand on. A book belongs to exactly one shelf and is
/// paid for out of that shelf's own purse, so the shelves never compete: the army's experience buys
/// army books, the air force's buys air books, the fleet's buys sea books, and the general staff's
/// book is paid for in the government's political credit.
/// </summary>
public static class DoctrineSlots
{
    public const string Ground = "ground";
    public const string Air = "air";
    public const string Sea = "sea";
    public const string General = "general";

    /// <summary>The order the shelves are shown in: the army first, the staff last.</summary>
    public static readonly string[] All = { Ground, Air, Sea, General };

    public static bool Knows(string id) => id is Ground or Air or Sea or General;

    /// <summary>Which shelf a service's experience pays for.</summary>
    public static string ForExperience(string experience) => experience switch
    {
        MilitaryExperience.Air => Air,
        MilitaryExperience.Navy => Sea,
        MilitaryExperience.Command => General,
        _ => Ground
    };

    /// <summary>What the shelf is called in a sentence, in the words the interface shows.</summary>
    public static string Name(string slot) => slot switch
    {
        Air => "air doctrine",
        Sea => "naval doctrine",
        General => "general doctrine",
        _ => "ground doctrine"
    };
}

/// <summary>What one shelf holds: the book in force, the articles written in it, and what is being learned.</summary>
public sealed class DoctrineTrack
{
    /// <summary>The book in force on this shelf. Empty means the shelf is still empty.</summary>
    public string Branch = "";

    /// <summary>Articles learned under the book in force. A rewrite starts the new book empty.</summary>
    public List<string> Known = new();

    public string LearningBranch = "";
    public string LearningNode = "";
    public int LearningDoneDay = -1;
    public int LastRewriteDay = -9999;

    /// <summary>The other book on this same shelf. Null until a second book is opened. Not a third shelf.</summary>
    public DoctrineTrack Companion;

    public DoctrineTrack Clone()
    {
        var copy = new DoctrineTrack
        {
            Branch = Branch, Known = new List<string>(Known), LearningBranch = LearningBranch,
            LearningNode = LearningNode, LearningDoneDay = LearningDoneDay, LastRewriteDay = LastRewriteDay
        };
        if (Companion is not null)
            copy.Companion = new DoctrineTrack
            {
                Branch = Companion.Branch, Known = new List<string>(Companion.Known),
                LearningBranch = Companion.LearningBranch, LearningNode = Companion.LearningNode,
                LearningDoneDay = Companion.LearningDoneDay, LastRewriteDay = Companion.LastRewriteDay
            };
        return copy;
    }
}

/// <summary>
/// What the army is reading: one book per shelf, its learned nodes, and what is being learned on
/// each shelf now. A country can be reading up to four books at once - ground, air, sea and the
/// general staff's own - and each is studied independently.
///
/// The single-book fields below are what a save written before the shelves existed carries. They
/// are read exactly once, by <see cref="TacticalDoctrines.EnsureShelves"/>, and copied onto the
/// shelf their book belongs to; nothing writes them afterwards.
/// </summary>
public sealed class TacticalDoctrineMemory
{
    public string Branch = "";
    public List<string> Known = new();
    public string LearningBranch = "";
    public string LearningNode = "";
    public int LearningDoneDay = -1;
    public int LastRewriteDay = -9999;

    /// <summary>The four shelves, by <see cref="DoctrineSlots"/> id. Refilled on demand.</summary>
    public Dictionary<string, DoctrineTrack> Tracks = new(StringComparer.Ordinal);

    /// <summary>Whether the older single-book fields have been moved onto their shelf.</summary>
    public bool ShelvesOpened;

    /// <summary>The shelf for this id, created if the country has never used it.</summary>
    public DoctrineTrack Track(string slot)
    {
        Tracks ??= new Dictionary<string, DoctrineTrack>(StringComparer.Ordinal);
        if (!Tracks.TryGetValue(slot, out var track))
        {
            track = new DoctrineTrack();
            Tracks[slot] = track;
        }
        return track;
    }
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
/// The country's book of war, read from common/tactical_doctrines like every other catalogue: the
/// base game and a mod write the same thing, and a branch a mod adds is a branch.
/// </summary>
public static class TacticalDoctrineCatalog
{
    /// <summary>What a rushed node costs when its data names no price: three times the patient one.</summary>
    public const float RushCostMultiplier = 3f;

    private static readonly Dictionary<string, DoctrineBranch> Entries = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DoctrineBranch> ByNode = new(StringComparer.Ordinal);
    private static DoctrineBranch[] _ordered;
    public static int Revision { get; private set; }
    // Keep sorting out of each country's AI review. The index is private: callers cannot
    // reorder it, and deferred enumeration still reads the catalogue current at MoveNext.
    public static IEnumerable<DoctrineBranch> All
    {
        get
        {
            _ordered ??= Entries.Values.OrderBy(branch => branch.Id, StringComparer.Ordinal).ToArray();
            foreach (var branch in _ordered) yield return branch;
        }
    }
    public static DoctrineBranch Find(string id) => id is null ? null : Entries.GetValueOrDefault(id);
    public static bool KnowsExperience(string kind) =>
        kind is MilitaryExperience.Army or MilitaryExperience.Air or MilitaryExperience.Navy or MilitaryExperience.Command;

    /// <summary>The book an article belongs to, or null for an id no book holds.</summary>
    public static DoctrineBranch BranchOf(string nodeId) =>
        nodeId is null ? null : ByNode.GetValueOrDefault(nodeId);

    /// <summary>Every book on one shelf, in catalogue order.</summary>
    public static IEnumerable<DoctrineBranch> OnShelf(string slot) =>
        All.Where(branch => string.Equals(branch.Slot, slot, StringComparison.Ordinal));

    static TacticalDoctrineCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Entries.Clear(); ByNode.Clear(); _ordered = null; Revision++;
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
                var append = e.TryGetProperty("append_nodes", out var additions);
                var previous = append ? Entries.GetValueOrDefault(entry.Name) : null;
                if (append && previous is null)
                    throw new ArgumentException("append_nodes needs an existing Doctrine branch");
                if (append && e.TryGetProperty("nodes", out _))
                    throw new ArgumentException("use nodes or append_nodes, not both");
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
                Dictionary<string, float> AiProfiles(JsonElement item)
                {
                    var weights = new Dictionary<string, float>(StringComparer.Ordinal);
                    if (!item.TryGetProperty("ai_profiles", out var block)) return weights;
                    if (block.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("ai_profiles must be an object of non-negative weights");
                    foreach (var profile in block.EnumerateObject())
                    {
                        if (!MechanicFeatures.ValidId(profile.Name) || profile.Value.ValueKind != JsonValueKind.Number ||
                            !profile.Value.TryGetSingle(out var weight) || !float.IsFinite(weight) || weight < 0f)
                            throw new ArgumentException($"ai_profiles.{profile.Name} must be a finite non-negative number");
                        weights[profile.Name] = weight;
                    }
                    return weights;
                }
                var experience = Text("experience", previous?.Experience ?? "").Trim().ToLowerInvariant();
                if (!KnowsExperience(experience))
                    throw new ArgumentException("experience must be army, air, navy or command");
                // A book's shelf follows from the purse that pays for it unless the data says
                // otherwise; a book that says otherwise has to be payable on the shelf it names.
                var slot = Text("slot", previous?.Slot ?? "").Trim().ToLowerInvariant();
                if (slot.Length == 0) slot = DoctrineSlots.ForExperience(experience);
                if (!DoctrineSlots.Knows(slot))
                    throw new ArgumentException("slot must be ground, air, sea or general");
                if (!string.Equals(slot, DoctrineSlots.ForExperience(experience), StringComparison.Ordinal))
                    throw new ArgumentException($"slot '{slot}' cannot be paid for in {experience} experience");
                if (append && (experience != previous.Experience || slot != previous.Slot))
                    throw new ArgumentException("append_nodes cannot change a Doctrine's service or currency");
                var list = append ? additions : e.TryGetProperty("nodes", out var ordinary) ? ordinary : default;
                if (list.ValueKind != JsonValueKind.Array ||
                    list.GetArrayLength() == 0)
                    throw new ArgumentException("a Doctrine with no nodes is not a Doctrine");
                // A sparse extension keeps every earlier save ID and definition. Validation
                // stays transactional: no index is changed until the complete graph is sound.
                var nodes = previous?.Nodes.ToList() ?? new List<DoctrineNode>();
                var seen = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("a node must be an object");
                    var id = item.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                        ? idValue.GetString() : "";
                    if (!MechanicFeatures.ValidId(id) || !seen.Add(id))
                        throw new ArgumentException($"node id '{id}' is invalid or repeated");
                    // Article ids are the save's own vocabulary: two books may not share one.
                    if (ByNode.ContainsKey(id) || Entries.Values.Any(other => other.Find(id) is not null))
                        throw new ArgumentException($"node id '{id}' already belongs to another Doctrine");
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
                    var excludes = new List<string>();
                    if (item.TryGetProperty("excludes", out var alternatives))
                    {
                        if (alternatives.ValueKind != JsonValueKind.Array)
                            throw new ArgumentException($"node '{id}' excludes must be an array of article ids");
                        foreach (var alternative in alternatives.EnumerateArray())
                        {
                            if (alternative.ValueKind != JsonValueKind.String)
                                throw new ArgumentException($"node '{id}' excludes must contain article ids");
                            excludes.Add(alternative.GetString());
                        }
                    }
                    var local = new List<string>();
                    var modifiers = item.TryGetProperty("modifiers", out var block)
                        ? NationalModifiers.Read(block, $"Doctrine.{entry.Name}.{id}", local)
                        : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    if (local.Count != 0) throw new ArgumentException(string.Join("; ", local));
                    var name = item.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                        ? nameValue.GetString() : id;
                    var description = item.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String
                        ? text.GetString() : "";
                    nodes.Add(new DoctrineNode(id, name, description, days, cost, rush, requires, modifiers)
                    { Excludes = excludes, AiProfiles = AiProfiles(item) });
                }
                // Prerequisites are checked once the whole branch is read, so a mod may write its
                // tree in whatever order reads best and a missing node is refused by name.
                foreach (var node in nodes)
                {
                    foreach (var need in node.Requires)
                        if (!seen.Contains(need))
                            throw new ArgumentException($"node '{node.Id}' requires '{need}', which this branch does not hold");
                    foreach (var alternative in node.Excludes)
                        if (!seen.Contains(alternative) || alternative == node.Id || node.Requires.Contains(alternative))
                            throw new ArgumentException($"node '{node.Id}' excludes invalid or required article '{alternative}'");
                }
                // One declaration is enough for a mod: neither study order may learn both sides.
                for (var i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    nodes[i] = node with { Excludes = nodes.Where(other =>
                        node.Excludes.Contains(other.Id) || other.Excludes.Contains(node.Id))
                        .Select(other => other.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray() };
                }
                foreach (var node in nodes)
                {
                    var ancestors = new HashSet<string>(StringComparer.Ordinal);
                    var pending = new Stack<string>(node.Requires);
                    while (pending.Count > 0)
                    {
                        var ancestor = pending.Pop();
                        if (ancestor == node.Id)
                            throw new ArgumentException($"node '{node.Id}' has a cyclic prerequisite");
                        if (!ancestors.Add(ancestor)) continue;
                        foreach (var earlier in nodes.First(other => other.Id == ancestor).Requires) pending.Push(earlier);
                    }
                    if (node.Excludes.Any(ancestors.Contains))
                        throw new ArgumentException($"node '{node.Id}' excludes an earlier required article");
                }
                var branch = new DoctrineBranch(entry.Name, Text("name", previous?.Name ?? entry.Name),
                    Text("description", previous?.Description ?? ""), experience, slot, nodes)
                { AiProfiles = append && !e.TryGetProperty("ai_profiles", out _) ? previous.AiProfiles : AiProfiles(e) };
                Entries[entry.Name] = branch;
                foreach (var node in nodes) ByNode[node.Id] = branch;
                _ordered = null;
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
/// The country's books of war: what each of its services is taught to be, and what changing its
/// mind costs.
///
/// A Doctrine is a tree, studied the way technologies are: nodes open by their prerequisites, and
/// each is paid for in the experience its own service actually earned (MilitaryExperience). A
/// node can be taken patiently - the cost, over its days - or as a crash course, which is the
/// rush price and lands at once. Nothing here is decorative: every node's block is read by the
/// simulation's own army, supply and training arithmetic through NationalModifiers.
///
/// The shelves are what makes a country's war whole: the ground, air, sea and general staff books
/// are all read at once, each studied on its own days, so a country is never forced to choose a
/// navy instead of an air force. Within one shelf, rewriting is the point of the mechanic: a
/// country whose war stopped being the war it prepared for can change its book, and pays twice -
/// political points for the government that has to sell the reform, and a season of retraining in
/// which the old habits are gone and the new ones are not learned (the doctrine_retraining
/// spirit). The nodes of the abandoned book are not kept. That is what makes the choice real.
/// </summary>
public static class TacticalDoctrines
{
    /// <summary>The spirit granted while an army relearns: see common/modifiers/46_retraining.json.</summary>
    public const string RetrainingModifier = "doctrine_retraining";

    /// <summary>Which Doctrine each character of government leans towards on the ground shelf.</summary>
    private static readonly Dictionary<string, string> CharacterBranches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blitzkrieg"] = "manoeuvre", ["irredentist"] = "manoeuvre",
        ["attrition"] = "fire", ["major_offensive"] = "fire", ["default"] = "fire",
        ["defensive"] = "fortress", ["fortress"] = "fortress", ["garrison_state"] = "fortress",
        ["isolated"] = "fortress",
        ["minor_power"] = "levy",
        ["expeditionary"] = "sky", ["trading_state"] = "coast"
    };

    /// <summary>Which book a government reaches for on each of the other three shelves.</summary>
    private static readonly Dictionary<string, string> CharacterAir = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blitzkrieg"] = "sky", ["major_offensive"] = "sky", ["expeditionary"] = "sky",
        ["attrition"] = "strategic_air", ["default"] = "sky"
    };

    private static readonly Dictionary<string, string> CharacterSea = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trading_state"] = "coast", ["expeditionary"] = "coast", ["default"] = "coast"
    };

    private static readonly Dictionary<string, string> CharacterGeneral = new(StringComparer.OrdinalIgnoreCase)
    {
        ["minor_power"] = "mobilisation", ["isolated"] = "mobilisation",
        ["attrition"] = "staff", ["default"] = "staff"
    };

    public static float Experience(Nation nation, string kind) => kind switch
    {
        MilitaryExperience.Air => nation.AirExperience,
        MilitaryExperience.Navy => nation.NavalExperience,
        MilitaryExperience.Command => nation.PoliticalPoints,
        _ => nation.ArmyExperience
    };

    private static void Spend(Nation nation, string kind, float cost)
    {
        switch (kind)
        {
            case MilitaryExperience.Air: nation.AirExperience = MathF.Max(0f, nation.AirExperience - cost); break;
            case MilitaryExperience.Navy: nation.NavalExperience = MathF.Max(0f, nation.NavalExperience - cost); break;
            case MilitaryExperience.Command: nation.PoliticalPoints = MathF.Max(0f, nation.PoliticalPoints - cost); break;
            default: nation.ArmyExperience = MathF.Max(0f, nation.ArmyExperience - cost); break;
        }
    }

    public static string ServiceName(string kind) => kind switch
    {
        MilitaryExperience.Air => "air experience",
        MilitaryExperience.Navy => "naval experience",
        MilitaryExperience.Command => "political credit",
        _ => "army experience"
    };

    /// <summary>
    /// Move a save written before the shelves existed onto them: the one book it carries goes on
    /// the shelf its service belongs to, with its articles and its unfinished course. Called from
    /// every read path, so a campaign opens with its book wherever it is loaded from.
    /// </summary>
    public static void EnsureShelves(Nation nation)
    {
        var memory = nation?.TacticalDoctrine;
        if (memory is null || memory.ShelvesOpened) return;
        memory.ShelvesOpened = true;
        memory.Tracks ??= new Dictionary<string, DoctrineTrack>(StringComparer.Ordinal);
        if (memory.Branch.Length == 0) return;
        var branch = TacticalDoctrineCatalog.Find(memory.Branch);
        if (branch is null) return;
        var track = memory.Track(branch.Slot);
        if (track.Branch.Length > 0) return;
        track.Branch = branch.Id;
        track.Known = new List<string>(memory.Known);
        track.LearningBranch = memory.LearningBranch;
        track.LearningNode = memory.LearningNode;
        track.LearningDoneDay = memory.LearningDoneDay;
        track.LastRewriteDay = memory.LastRewriteDay;
    }

    /// <summary>The shelf a book stands on, opened if this country has never used it.</summary>
    public static DoctrineTrack TrackFor(Nation nation, DoctrineBranch branch)
    {
        if (nation is null || branch is null) return null;
        EnsureShelves(nation);
        var primary = nation.TacticalDoctrine.Track(branch.Slot);
        if (primary.Companion is { } companion &&
            (string.Equals(companion.Branch, branch.Id, StringComparison.Ordinal) ||
             string.Equals(companion.LearningBranch, branch.Id, StringComparison.Ordinal)))
            return companion;
        return primary;
    }

    private enum ShelfHome { Primary, Companion, Open, Rewrite, Full }

    /// <summary>
    /// Where a book would be written. Does not open the second book: a refusal must not
    /// leave an empty companion behind.
    /// </summary>
    private static ShelfHome Decide(Campaign campaign, Nation nation, DoctrineBranch branch, out DoctrineTrack track)
    {
        track = null;
        if (nation is null || branch is null) return ShelfHome.Full;
        EnsureShelves(nation);
        var primary = nation.TacticalDoctrine.Track(branch.Slot);
        track = primary;
        if (primary.Branch.Length == 0 ||
            string.Equals(primary.Branch, branch.Id, StringComparison.Ordinal) ||
            string.Equals(primary.LearningBranch, branch.Id, StringComparison.Ordinal))
            return ShelfHome.Primary;
        if (primary.Companion is { } companion &&
            (companion.Branch.Length == 0 ||
             string.Equals(companion.Branch, branch.Id, StringComparison.Ordinal) ||
             string.Equals(companion.LearningBranch, branch.Id, StringComparison.Ordinal)))
        {
            track = companion;
            return ShelfHome.Companion;
        }
        var cap = Math.Max(1, campaign.Defines.DoctrineBooksPerShelf);
        var held = 1 + (primary.Companion is { Branch.Length: > 0 } ? 1 : 0);
        if (held < cap) return ShelfHome.Open;
        if (cap <= 1) return ShelfHome.Rewrite;
        return ShelfHome.Full;
    }

    /// <summary>The book in force on one shelf, or null while the shelf is empty.</summary>
    public static DoctrineBranch BookOn(Nation nation, string slot)
    {
        if (nation is null || !DoctrineSlots.Knows(slot)) return null;
        EnsureShelves(nation);
        var track = nation.TacticalDoctrine.Tracks.GetValueOrDefault(slot);
        return track is null || track.Branch.Length == 0 ? null : TacticalDoctrineCatalog.Find(track.Branch);
    }

    /// <summary>Every book in force, in shelf order - what the country is reading right now.</summary>
    public static IEnumerable<(string Slot, DoctrineBranch Branch)> Books(Nation nation)
    {
        if (nation is null) yield break;
        foreach (var slot in DoctrineSlots.All)
        {
            if (BookOn(nation, slot) is { } branch) yield return (slot, branch);
            var extra = nation.TacticalDoctrine.Tracks.GetValueOrDefault(slot)?.Companion;
            if (extra is { Branch.Length: > 0 } && TacticalDoctrineCatalog.Find(extra.Branch) is { } second)
                yield return (slot, second);
        }
    }

    /// <summary>Whether this node has been learned under the book in force on its shelf.</summary>
    public static bool Known(Nation nation, string nodeId)
    {
        if (nation is null || string.IsNullOrEmpty(nodeId)) return false;
        var branch = TacticalDoctrineCatalog.BranchOf(nodeId);
        return branch is not null && Known(nation, branch, nodeId);
    }

    public static bool Known(Nation nation, DoctrineBranch branch, string nodeId)
    {
        var track = TrackFor(nation, branch);
        return track is not null &&
               string.Equals(track.Branch, branch.Id, StringComparison.Ordinal) &&
               track.Known.Contains(nodeId);
    }

    /// <summary>
    /// The nodes of a branch that can be started now: not learned, and every prerequisite learned.
    /// For a branch that is not the one in force on its shelf this is its roots, because a rewrite
    /// starts empty.
    /// </summary>
    public static List<DoctrineNode> Available(Nation nation, string branchId)
    {
        var branch = TacticalDoctrineCatalog.Find(branchId);
        var found = new List<DoctrineNode>();
        if (branch is null) return found;
        var track = TrackFor(nation, branch);
        var inForce = track is not null && string.Equals(track.Branch, branchId, StringComparison.Ordinal);
        foreach (var node in branch.Nodes)
        {
            if (inForce && track.Known.Contains(node.Id)) continue;
            if (inForce && node.Excludes.Any(track.Known.Contains)) continue;
            var ready = true;
            foreach (var need in node.Requires)
                if (!inForce || !track.Known.Contains(need)) { ready = false; break; }
            if (ready) found.Add(node);
        }
        return found;
    }

    private static bool Rewriting(Nation nation, DoctrineBranch branch)
    {
        var track = TrackFor(nation, branch);
        return track is not null && track.Branch.Length > 0 &&
               !string.Equals(track.Branch, branch.Id, StringComparison.Ordinal);
    }

    /// <summary>Books already in force on the tactical shelves, or on the general shelf.</summary>
    public static int BooksHeld(Nation nation, bool general)
    {
        if (nation is null) return 0;
        EnsureShelves(nation);
        var count = 0;
        foreach (var slot in DoctrineSlots.All)
        {
            if ((slot == DoctrineSlots.General) != general) continue;
            if (BookOn(nation, slot) is not null) count++;
        }
        return count;
    }

    /// <summary>Why this node cannot be started now, in the words the interface shows. Empty means it can.</summary>
    public static string Refusal(Campaign campaign, Nation nation, string branchId, string nodeId, bool rush)
    {
        if (!campaign.FeatureEnabled("tactical_doctrines")) return Loc.Get("This mechanic is disabled in this scenario.");
        var branch = TacticalDoctrineCatalog.Find(branchId);
        if (branch is null) return Loc.Get("No such Doctrine is written.");
        var node = branch.Find(nodeId);
        if (node is null) return Loc.Get("No such article of the Doctrine is written.");
        var home = Decide(campaign, nation, branch, out var track);
        if (track is null) return Loc.Get("No such Doctrine is written.");
        if (home == ShelfHome.Full)
            return Loc.Format("This shelf already holds {0} doctrines.",
                Math.Max(1, campaign.Defines.DoctrineBooksPerShelf));
        var writingThis = home is ShelfHome.Primary or ShelfHome.Companion &&
            string.Equals(track.Branch, branch.Id, StringComparison.Ordinal);
        if (writingThis && track.Known.Contains(node.Id))
            return Loc.Get("This article has already been learned.");
        if (writingThis && node.Excludes.FirstOrDefault(track.Known.Contains) is { } excluded)
            return Loc.Format("This subdoctrine excludes {0}.", Loc.Get(branch.Find(excluded).Name));
        if (home != ShelfHome.Open && track.LearningNode.Length > 0)
            return Loc.Get("This service is already learning an article.");
        if (writingThis)
        {
            foreach (var need in node.Requires)
                if (!track.Known.Contains(need)) return Loc.Get("The earlier articles of this Doctrine come first.");
        }
        // A shelf with no book, a second book, or a replacement starts at the roots.
        else if (node.Requires.Count > 0)
            return Loc.Get("The earlier articles of this Doctrine come first.");
        var rewriting = home == ShelfHome.Rewrite;
        if (home == ShelfHome.Primary && track.Branch.Length == 0)
        {
            var generalShelf = branch.Slot == DoctrineSlots.General;
            var held = BooksHeld(nation, generalShelf);
            var tacticalCap = Math.Max(0, campaign.Defines.DoctrineTacticalSlots);
            var generalCap = Math.Max(0, campaign.Defines.DoctrineGeneralSlots);
            var cap = generalShelf ? generalCap : tacticalCap;
            // Full tactical shelves refuse another tactical book, including a swap on a shelf
            // already in use: that book would be one past the cap. Under the cap, changing
            // the book on one shelf is still a rewrite.
            // Opening an empty shelf past the cap is the refused extra book. Changing the
            // book already on a shelf does not add a slot, so that rewrite stays available.
            if (track.Branch.Length == 0 && held >= cap)
                return Loc.Format(
                    "A country may hold {0} tactical doctrines and {1} general doctrine. Another tactical doctrine is refused.",
                    tacticalCap, generalCap);
        }
        var price = rush ? node.RushCost : node.Cost;
        var rewriteCost = rewriting ? campaign.Defines.DoctrineRewritePoliticalCost : 0f;
        if (branch.Experience == MilitaryExperience.Command)
        {
            if (nation.PoliticalPoints + 0.001f < rewriteCost + price)
                return Loc.Format("This article needs {0} points of {1}.",
                    rewriteCost + price, Loc.Get(ServiceName(branch.Experience)));
        }
        else if (rewriteCost > 0f && nation.PoliticalPoints + 0.001f < rewriteCost)
            return Loc.Format("Rewriting the Doctrine costs {0} political points.", rewriteCost);
        else if (Experience(nation, branch.Experience) + 0.001f < price)
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
        var home = Decide(campaign, nation, branch, out var track);
        if (home == ShelfHome.Open)
        {
            var primary = nation.TacticalDoctrine.Track(branch.Slot);
            primary.Companion ??= new DoctrineTrack();
            track = primary.Companion;
        }
        if (home == ShelfHome.Rewrite)
        {
            nation.PoliticalPoints = MathF.Max(0f, nation.PoliticalPoints - campaign.Defines.DoctrineRewritePoliticalCost);
            track.Branch = branch.Id;
            track.Known.Clear();     // the abandoned book is not kept
            track.LastRewriteDay = campaign.ElapsedDays;
            track.LearningBranch = "";
            track.LearningNode = "";
            track.LearningDoneDay = -1;
            SyncLegacyFields(nation, track);
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
            Learn(campaign, nation, branch, node, track);
        }
        else
        {
            track.LearningBranch = branch.Id;
            track.LearningNode = node.Id;
            track.LearningDoneDay = campaign.ElapsedDays + Math.Max(1, node.Days);
            SyncLegacyFields(nation, track);
        }
        campaign.MarkStrategicChange();
        return true;
    }

    private static void Learn(Campaign campaign, Nation nation, DoctrineBranch branch, DoctrineNode node, DoctrineTrack track)
    {
        // Whose article has just landed is whose book is in force: a crash course taken from
        // nothing must start the Doctrine, not leave the service with a known article and no book.
        track.Branch = branch.Id;
        track.LearningBranch = "";
        track.LearningNode = "";
        track.LearningDoneDay = -1;
        if (!track.Known.Contains(node.Id)) track.Known.Add(node.Id);
        SyncLegacyFields(nation, track);
        NationalModifiers.Invalidate(nation);
        campaign.Chronicle.Record(campaign, "doctrine_article", nation.Id, subject: node.Name);
        // The bench's own record of what the AI did with its book, beside its war counters.
        if (Delegation.RunByAi(nation)) Count("doctrine_adopted");
    }

    private static void FinishCourse(Campaign campaign, Nation nation, DoctrineTrack track, int day)
    {
        if (track.LearningNode.Length == 0 || day < track.LearningDoneDay) return;
        var branch = TacticalDoctrineCatalog.Find(track.LearningBranch);
        var node = branch?.Find(track.LearningNode);
        if (branch is null || node is null)
        {
            track.LearningBranch = ""; track.LearningNode = ""; track.LearningDoneDay = -1;
            return;
        }
        Learn(campaign, nation, branch, node, track);
        campaign.MarkStrategicChange();
    }

    private static void SyncLegacyFields(Nation nation, DoctrineTrack activeTrack)
    {
        var memory = nation?.TacticalDoctrine;
        if (memory is null || activeTrack is null) return;
        var primary = false;
        foreach (var slot in DoctrineSlots.All)
            if (ReferenceEquals(memory.Tracks.GetValueOrDefault(slot), activeTrack)) { primary = true; break; }
        if (!primary) return;
        memory.Branch = activeTrack.Branch;
        memory.Known = new List<string>(activeTrack.Known);
        memory.LearningBranch = activeTrack.LearningBranch;
        memory.LearningNode = activeTrack.LearningNode;
        memory.LearningDoneDay = activeTrack.LearningDoneDay;
        memory.LastRewriteDay = activeTrack.LastRewriteDay;
    }

    private static void Count(string what) =>
        AiStatecraft.Tally[what] = AiStatecraft.Tally.GetValueOrDefault(what) + 1;

    /// <summary>
    /// What has come due on every shelf, and what every other government is doing about its own
    /// books. Each country keeps its own day of the week, the spread the rest of the cabinet
    /// already uses.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same doctrine pass, yielded between batches of countries.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null) yield break;
        var day = campaign.ElapsedDays;
        var runAi = campaign.FeatureEnabled("tactical_doctrines") &&
                    campaign.LastTacticalDoctrineDay != day;
        if (runAi)
        {
            campaign.LastTacticalDoctrineDay = day;
        }
        var ticked = 0;
        foreach (var nation in campaign.Nations)
        {
            if (++ticked % 8 == 0) yield return 0;
            EnsureShelves(nation);
            var memory = nation.TacticalDoctrine;
            // FinishCourse edits track fields, legacy mirrors and modifier caches, never
            // the shelf dictionary. AiStudy may add shelves, but runs AFTER this loop.
            // Preserve insertion order, including legacy/modded slots, without an hourly
            // snapshot allocation for every country. No shared scratch buffer is needed.
            foreach (var track in memory.Tracks.Values)
            {
                FinishCourse(campaign, nation, track, day);
                if (track.Companion is not null) FinishCourse(campaign, nation, track.Companion, day);
            }
            if (!runAi || !Delegation.RunByAi(nation)) continue;
            if ((day + nation.Id) % 7 != 0) continue;
            AiStudy(campaign, nation);
        }
    }

    /// <summary>
    /// A government improving its own books: for every shelf, the Doctrine its character prefers,
    /// the cheapest article open right now, and a crash course when it is at war and can pay for
    /// one - war is when the next article is worth more than the money of waiting.
    /// </summary>
    private static void AiStudy(Campaign campaign, Nation nation)
    {
        EnsureShelves(nation);
        var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        foreach (var slot in DoctrineSlots.All)
        {
            var track = nation.TacticalDoctrine.Track(slot);
            if (track.LearningNode.Length > 0) continue;
            var branch = TacticalDoctrineCatalog.Find(
                track.Branch.Length > 0 ? track.Branch : PreferredBranch(campaign, nation, slot));
            if (branch is null || !string.Equals(branch.Slot, slot, StringComparison.Ordinal)) continue;
            if (Rewriting(nation, branch) && track.Known.Count > 0 &&
                campaign.ElapsedDays - track.LastRewriteDay <
                Math.Max(30, campaign.Defines.DoctrineAiRewriteCooldownDays)) continue;
            var available = Available(nation, branch.Id);
            var character = AiStrategyCatalog.Of(campaign, nation).Id;
            // A strategic course still chooses the first book. Once its usable articles are
            // learned, an authored character preference may fill the existing companion slot.
            // This gives new/modded specialisms a real AI route without deleting a learned book.
            if (available.Count == 0 && track.Known.Count > 0 && campaign.Defines.DoctrineBooksPerShelf > 1)
            {
                var companion = track.Companion;
                if (companion is { LearningNode.Length: > 0 }) continue;
                var additional = companion is { Branch.Length: > 0 }
                    ? TacticalDoctrineCatalog.Find(companion.Branch)
                    : TacticalDoctrineCatalog.OnShelf(slot)
                        .Where(book => book.Id != branch.Id && book.AiProfiles.GetValueOrDefault(character) > 0f)
                        .OrderByDescending(book => book.AiProfiles.GetValueOrDefault(character))
                        .ThenBy(book => book.Id, StringComparer.Ordinal).FirstOrDefault();
                if (additional is null) continue;
                branch = additional;
                available = Available(nation, branch.Id);
            }
            var have = Experience(nation, branch.Experience);
            DoctrineNode choice = null;
            var rush = false;
            if (available.Any(node => node.AiProfiles.ContainsKey(character)))
            {
                choice = available.Where(node => node.AiProfiles.GetValueOrDefault(character, 1f) > 0f &&
                    have + 0.001f >= node.Cost)
                    .OrderByDescending(node => node.AiProfiles.GetValueOrDefault(character, 1f))
                    .ThenBy(node => node.Cost).ThenBy(node => node.Id, StringComparer.Ordinal).FirstOrDefault();
                rush = choice is not null && atWar && have + 0.001f >= choice.RushCost;
            }
            else foreach (var node in available)
            {
                if (atWar && have + 0.001f >= node.RushCost) { choice = node; rush = true; break; }
                if (have + 0.001f < node.Cost) continue;
                if (choice is null || node.Cost < choice.Cost) choice = node;
            }
            if (choice is null) continue;
            Study(campaign, nation, branch.Id, choice.Id, rush);
        }
    }

    /// <summary>
    /// The book this country reaches for on the ground shelf: the one its grand strategy leans on
    /// if the strategy names one, otherwise the one its character prefers (AiStrategyCatalog.Of).
    /// </summary>
    public static string PreferredBranch(Campaign campaign, Nation nation) => PreferredBranch(campaign, nation, DoctrineSlots.Ground);

    /// <summary>The book this country reaches for on one shelf.</summary>
    public static string PreferredBranch(Campaign campaign, Nation nation, string slot)
    {
        var bias = StrategicDoctrines.TacticalBias(nation);
        if (bias.Length > 0 && TacticalDoctrineCatalog.Find(bias) is { } leaning &&
            string.Equals(leaning.Slot, slot, StringComparison.Ordinal)) return bias;
        var character = AiStrategyCatalog.Of(campaign, nation).Id;
        // New/modded books opt into character-driven selection; old data keeps its exact fallback.
        var authored = TacticalDoctrineCatalog.OnShelf(slot)
            .Where(book => book.AiProfiles.GetValueOrDefault(character) > 0f)
            .OrderByDescending(book => book.AiProfiles.GetValueOrDefault(character))
            .ThenBy(book => book.Id, StringComparer.Ordinal).FirstOrDefault();
        if (authored is not null) return authored.Id;
        var table = slot switch
        {
            DoctrineSlots.Air => CharacterAir,
            DoctrineSlots.Sea => CharacterSea,
            DoctrineSlots.General => CharacterGeneral,
            _ => CharacterBranches
        };
        if (table.TryGetValue(character, out var wanted) &&
            TacticalDoctrineCatalog.Find(wanted) is { } found &&
            string.Equals(found.Slot, slot, StringComparison.Ordinal)) return wanted;
        return TacticalDoctrineCatalog.OnShelf(slot).FirstOrDefault()?.Id ?? "";
    }

    /// <summary>The sentence on the doctrine page: a fixed label, not the live fraction.</summary>
    public const string SameShelfStackSentence =
        "A second book on the same shelf adds half of a repeated bonus.";

    public static string SameShelfStackLine() => Loc.Get(SameShelfStackSentence);

    // Which campaign a country belongs to, so the shelf ceiling reads that world's define.
    // Not saved: a load binds again. A country with no home uses the shipped half.
    private static readonly ConditionalWeakTable<Nation, Campaign> Homes = new();

    internal static void BindHome(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return;
        Homes.Remove(nation);
        Homes.Add(nation, campaign);
    }

    internal static void BindHomes(Campaign campaign)
    {
        if (campaign is null) return;
        foreach (var nation in campaign.Nations) BindHome(campaign, nation);
    }

    private static float SameShelfFraction(Nation nation)
    {
        if (nation is null || !Homes.TryGetValue(nation, out var campaign) || campaign?.Defines is null)
            return Defines.DefaultDoctrineSameShelfStack;
        var fraction = campaign.Defines.DoctrineSameShelfStack;
        return float.IsFinite(fraction) ? fraction : Defines.DefaultDoctrineSameShelfStack;
    }

    /// <summary>
    /// One book's articles, summed the way they always were. Empty when that side of the
    /// shelf has nothing learned. The dictionary is the country's own and is refilled in place.
    /// </summary>
    private static Dictionary<string, float> BookModifiers(Nation nation, string slot, bool second)
    {
        if (nation is null) return null;
        EnsureShelves(nation);
        var track = nation.TacticalDoctrine.Tracks.GetValueOrDefault(slot);
        if (track is null) return null;
        DoctrineTrack home;
        DoctrineBranch book;
        if (!second)
        {
            if (track.Branch.Length == 0 || track.Known.Count == 0) return null;
            book = TacticalDoctrineCatalog.Find(track.Branch);
            home = track;
        }
        else
        {
            home = track.Companion;
            if (home is not { Branch.Length: > 0 } || home.Known.Count == 0) return null;
            book = TacticalDoctrineCatalog.Find(home.Branch);
        }
        if (book is null) return null;
        var cache = nation.DoctrineModifierCache ??=
            new Dictionary<string, Dictionary<string, float>>(StringComparer.Ordinal);
        var key = second ? slot + "#2" : slot;
        if (!cache.TryGetValue(key, out var totals))
        {
            totals = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            cache[key] = totals;
        }
        totals.Clear();
        foreach (var node in book.Nodes)
        {
            if (!home.Known.Contains(node.Id)) continue;
            foreach (var effect in node.Modifiers)
                totals[effect.Key] = totals.GetValueOrDefault(effect.Key) + effect.Value;
        }
        return totals.Count == 0 ? null : totals;
    }

    static void AddRaw(Dictionary<string, float> totals, Dictionary<string, float> modifiers)
    {
        if (modifiers is null || modifiers.Count == 0) return;
        foreach (var (key, value) in modifiers)
            totals[key] = totals.GetValueOrDefault(key) + value;
    }

    /// <summary>
    /// The articles in force, poured into the national sum. A single book is added as its
    /// articles already stand. Two books on one shelf that share a key stop at the larger
    /// contribution plus <see cref="Defines.DoctrineSameShelfStack"/> of the smaller one.
    /// A key only one of them carries is added whole. Another shelf adds in full, even
    /// when the key is the same word.
    /// </summary>
    internal static void AddTo(Dictionary<string, float> totals, Nation nation)
    {
        if (nation is null || totals is null) return;
        var fraction = SameShelfFraction(nation);
        foreach (var slot in DoctrineSlots.All)
        {
            var primary = BookModifiers(nation, slot, false);
            var companion = BookModifiers(nation, slot, true);
            if (companion is null)
            {
                AddRaw(totals, primary);
                continue;
            }
            if (primary is null)
            {
                AddRaw(totals, companion);
                continue;
            }
            foreach (var (key, left) in primary)
            {
                if (!companion.TryGetValue(key, out var right))
                {
                    totals[key] = totals.GetValueOrDefault(key) + left;
                    continue;
                }
                if (fraction >= 1f)
                {
                    totals[key] = totals.GetValueOrDefault(key) + left;
                    totals[key] += right;
                }
                else if (fraction <= 0f)
                    totals[key] = totals.GetValueOrDefault(key) + Math.Max(left, right);
                else
                    totals[key] = totals.GetValueOrDefault(key) + Math.Max(left, right) + fraction * Math.Min(left, right);
            }
            foreach (var (key, right) in companion)
                if (!primary.ContainsKey(key))
                    totals[key] = totals.GetValueOrDefault(key) + right;
        }
    }

    /// <summary>
    /// The articles in force, as sources of national modifiers. Each book is named whole,
    /// so a screen can say which book gave what. The ceiling is applied when those books
    /// are summed, not by shrinking the book a tooltip is reading.
    /// </summary>
    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation nation)
    {
        if (nation is null) yield break;
        foreach (var slot in DoctrineSlots.All)
        {
            var primary = BookModifiers(nation, slot, false);
            if (primary is not null &&
                TacticalDoctrineCatalog.Find(nation.TacticalDoctrine.Tracks[slot].Branch) is { } book)
                yield return (book.Name, primary);
            var extra = BookModifiers(nation, slot, true);
            if (extra is not null &&
                TacticalDoctrineCatalog.Find(nation.TacticalDoctrine.Tracks[slot].Companion.Branch) is { } other)
                yield return (other.Name, extra);
        }
    }
}

public sealed partial class Nation
{
    /// <summary>What the books of war add, kept between asks. Not saved; refilled from scratch.</summary>
    internal Dictionary<string, Dictionary<string, float>> DoctrineModifierCache;
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
