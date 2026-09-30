using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>What every country researches a week whatever it is: the floor under a failed state's laboratories.</summary>
    public float ResearchBasePerWeek { get; set; } = 0.5f;

    /// <summary>
    /// What its economy adds, per square root of a billion of GDP, times its literacy. The square
    /// root because research does not scale with the size of an economy: the United States
    /// out-researches Mongolia by an order of magnitude, not by three.
    /// </summary>
    public float ResearchGdpWeight { get; set; } = 0.05f;

    /// <summary>The share of GDP spent on education at which the spending neither helps research nor holds it back.</summary>
    public float ResearchEducationBaseline { get; set; } = 0.045f;

    /// <summary>How strongly spending above or below that bends research: a power of the ratio.</summary>
    public float ResearchEducationExponent { get; set; } = 0.5f;

    /// <summary>
    /// How much cheaper a technology is once the whole world has it, weighed by GDP. The second
    /// country to build something has the first one's to look at.
    /// </summary>
    public float ResearchDiffusion { get; set; } = 0.5f;
    /// <summary>Extra discount when countries across a land border already know the technology.</summary>
    public float ResearchNeighbourDiffusion { get; set; } = 0.10f;
    /// <summary>Extra discount from allies and active trade/non-aggression partners who know it.</summary>
    public float ResearchPartnerDiffusion { get; set; } = 0.12f;
    /// <summary>Floor on effective research cost, including every diffusion source.</summary>
    public float ResearchMaximumDiscount { get; set; } = 0.70f;
    /// <summary>Bonus per university per million residents; laboratories count separately.</summary>
    public float ResearchUniversityBonus { get; set; } = 0.035f;
    public float ResearchLaboratoryBonus { get; set; } = 0.075f;
    public float ResearchInstitutionBonusCap { get; set; } = 0.35f;
    /// <summary>Fraction of research pace lost when national stability reaches zero.</summary>
    public float ResearchInstabilityPenalty { get; set; } = 0.20f;

    /// <summary>How much of an unfinished project a seceding research ministry can carry away.</summary>
    public float ResearchSecessionProgressShare { get; set; } = 0.55f;
    /// <summary>Extra difficulty of retaining advanced parent technologies after secession.</summary>
    public float ResearchSecessionComplexity { get; set; } = 0.45f;
}

/// <summary>One technology: what it costs, what it needs first and what it does.</summary>
public sealed class TechnologyDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "industry";
    public float Cost { get; set; } = 100f;
    public string Icon { get; set; } = "icon_research";
    public int? TreeColumn { get; set; }
    public int? TreeRow { get; set; }
    public List<string> Requires { get; } = new();
    public Dictionary<string, float> Effects { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The technologies a country can research (item 62), loaded from `common/technologies` the
/// layered way all content is: base game, then each enabled mod, later wins, a key at a time.
/// </summary>
public static class TechnologyCatalog
{
    /// <summary>
    /// What a technology can do, by the name its `effects` give it. Each is a share added to
    /// something the simulation already computes, summed over what a country has researched.
    /// </summary>
    public static readonly IReadOnlyList<string> EffectNames = NationalModifiers.Keys.Select(key => key.Id).ToArray();

    public static readonly IReadOnlyList<string> Categories = new[] { "industry", "society", "military", "logistics" };

    private static readonly Dictionary<string, TechnologyDefinition> ById = new(StringComparer.OrdinalIgnoreCase);
    private static List<TechnologyDefinition> _ordered = new();

    static TechnologyCatalog() => ResetCatalog();

    public static IReadOnlyList<TechnologyDefinition> All => _ordered;
    public static int Revision { get; private set; }

    public static TechnologyDefinition Of(string id) =>
        id is not null && ById.TryGetValue(id, out var definition) ? definition : null;

    public static void ResetCatalog(List<string> errors = null)
    {
        ById.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "technologies"))
            using (document)
                Merge(document.RootElement, errors);
        Settle();
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal) || entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (!ById.TryGetValue(entry.Name, out var technology))
                ById[entry.Name] = technology = new TechnologyDefinition { Id = entry.Name, Name = entry.Name };
            var value = entry.Value;
            if (value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) technology.Name = name.GetString() ?? technology.Name;
            if (value.TryGetProperty("description", out var about) && about.ValueKind == JsonValueKind.String)
                technology.Description = about.GetString() ?? "";
            if (value.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String)
                technology.Category = (category.GetString() ?? technology.Category).Trim().ToLowerInvariant();
            if (value.TryGetProperty("cost", out var cost))
            {
                if (cost.ValueKind == JsonValueKind.Number && cost.TryGetSingle(out var points) && float.IsFinite(points) && points >= 1)
                    technology.Cost = points;
                else errors?.Add($"technologies/{entry.Name}: cost must be a finite number >= 1.");
            }
            if (value.TryGetProperty("icon", out var icon) && icon.ValueKind == JsonValueKind.String)
                technology.Icon = icon.GetString() ?? "icon_research";
            technology.TreeColumn = ReadCoordinate(value, "tree_column", technology.TreeColumn, entry.Name, errors);
            technology.TreeRow = ReadCoordinate(value, "tree_row", technology.TreeRow, entry.Name, errors);
            if (value.TryGetProperty("requires", out var requires) && requires.ValueKind == JsonValueKind.Array)
            {
                technology.Requires.Clear();
                foreach (var item in requires.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        technology.Requires.Add(item.GetString().Trim());
            }
            if (value.TryGetProperty("effects", out var effects) && effects.ValueKind == JsonValueKind.Object)
            {
                technology.Effects.Clear();
                foreach (var effect in effects.EnumerateObject())
                {
                    if (effect.Value.ValueKind != JsonValueKind.Number || !effect.Value.TryGetSingle(out var share) || !float.IsFinite(share))
                    {
                        errors?.Add($"technologies/{entry.Name}: effect '{effect.Name}' must be a number.");
                        continue;
                    }
                    technology.Effects[effect.Name] = share;
                }
            }
        }
        Settle();
    }

    /// <summary>In the order a screen lists them: category, then cost, then id.</summary>
    private static int? ReadCoordinate(JsonElement value, string key, int? previous, string id, List<string> errors)
    {
        if (!value.TryGetProperty(key, out var coordinate)) return previous;
        if (coordinate.ValueKind == JsonValueKind.Null) return null;
        if (coordinate.ValueKind == JsonValueKind.Number && coordinate.TryGetInt32(out var number) && number >= 0 && number <= 128) return number;
        errors?.Add($"technologies/{id}: {key} must be null or an integer from 0 to 128.");
        return previous;
    }

    private static void Settle()
    {
        Revision++;
        _ordered = ById.Values
            .OrderBy(technology => CategoryOrder(technology.Category))
            .ThenBy(technology => technology.Cost)
            .ThenBy(technology => technology.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static int CategoryOrder(string category)
    {
        for (var index = 0; index < Categories.Count; index++)
            if (string.Equals(Categories[index], category, StringComparison.OrdinalIgnoreCase)) return index;
        return Categories.Count;
    }

    /// <summary>What is wrong with the catalogue: a prerequisite that does not exist, a loop, an effect nothing reads.</summary>
    public static IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var technology in ById.Values)
        {
            foreach (var needed in technology.Requires)
                if (!ById.ContainsKey(needed))
                    problems.Add($"{technology.Id} requires '{needed}', which is not a technology");
            foreach (var effect in technology.Effects.Keys)
                if (!EffectNames.Contains(effect, StringComparer.OrdinalIgnoreCase))
                    problems.Add($"{technology.Id}: '{effect}' is not an effect ({string.Join(", ", EffectNames)})");
            if (string.IsNullOrWhiteSpace(technology.Category))
                problems.Add($"{technology.Id}: category must not be empty");
        }
        // A loop is a technology nobody can ever research.
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool Loops(string id)
        {
            if (state.TryGetValue(id, out var seen)) return seen == 1;
            state[id] = 1;
            if (ById.TryGetValue(id, out var technology))
                foreach (var needed in technology.Requires)
                    if (Loops(needed)) return true;
            state[id] = 2;
            return false;
        }
        foreach (var id in ById.Keys)
        {
            state.Clear();
            if (Loops(id)) problems.Add($"{id} needs itself, through its prerequisites");
        }
        return problems;
    }
}

public sealed partial class Nation
{
    /// <summary>The technologies this country has, by id, in the order it got them.</summary>
    public readonly List<string> Technologies = new();

    /// <summary>What it is researching now; empty for nothing chosen.</summary>
    public string CurrentResearch = "";

    /// <summary>Points put into each technology so far. Switching keeps them: nothing researched is lost.</summary>
    public readonly Dictionary<string, float> ResearchProgress = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What it researched last week, for the interface.</summary>
    [JsonIgnore] public float ResearchPerWeek;

    [JsonIgnore] internal Dictionary<string, float> TechnologyBonuses;
    [JsonIgnore] internal int TechnologyBonusesFor = -1;
    [JsonIgnore] internal int TechnologyBonusesRevision = -1;

    public bool HasTechnology(string id)
    {
        foreach (var known in Technologies)
            if (string.Equals(known, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>
/// Research (item 62): points a week from a country's economy, literacy and schools, spent on
/// one technology at a time, and technologies that each add a share to something the
/// simulation already works out - output, research itself, attack, defence, marching and supply.
///
/// A country nobody plays chooses for itself; so does a player's ministry when the player has
/// not, so a week's research is never thrown away. Diffusion keeps the world connected:
/// global adoption, neighbours and formal partners each lower a discovery's remaining cost.
/// </summary>
public static class ResearchSystem
{
    public readonly record struct DiffusionBreakdown(float World, float Neighbours, float Partners, float Total, float EffectiveCost);

    /// <summary>What a country's technologies add to one effect, summed. Cached until it learns another.</summary>
    public static float Bonus(Nation nation, string effect)
    {
        if (nation is null || nation.Technologies.Count == 0) return 0f;
        if (nation.TechnologyBonuses is null || nation.TechnologyBonusesFor != nation.Technologies.Count || nation.TechnologyBonusesRevision != TechnologyCatalog.Revision)
        {
            var bonuses = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in nation.Technologies)
                if (TechnologyCatalog.Of(id) is { } technology)
                    foreach (var (name, share) in technology.Effects)
                        bonuses[name] = bonuses.GetValueOrDefault(name) + share;
            nation.TechnologyBonuses = bonuses;
            nation.TechnologyBonusesFor = nation.Technologies.Count;
            nation.TechnologyBonusesRevision = TechnologyCatalog.Revision;
        }
        return nation.TechnologyBonuses.GetValueOrDefault(effect);
    }

    /// <summary>Whether a country may start this one: it exists, it is not had yet, and what it needs is.</summary>
    public static bool Available(Nation nation, TechnologyDefinition technology) =>
        nation is not null && technology is not null && !nation.HasTechnology(technology.Id) &&
        technology.Requires.All(nation.HasTechnology);

    public static IEnumerable<TechnologyDefinition> AvailableTo(Nation nation) =>
        TechnologyCatalog.All.Where(technology => Available(nation, technology));

    /// <summary>A week of research for this country, in points.</summary>
    public static float WeeklyPoints(Campaign campaign, Nation nation)
    {
        var defines = campaign.Defines;
        var economy = defines.ResearchGdpWeight * MathF.Sqrt(MathF.Max(0f, nation.Gdp)) * M.Clamp01(nation.HumanCapital);
        var schools = MathF.Pow(M.Clamp(nation.EducationSpending / MathF.Max(0.0001f, defines.ResearchEducationBaseline),
            0.1f, 4f), defines.ResearchEducationExponent);
        var residents = MathF.Max(0.1f, nation.Population / 1_000_000f);
        var institutions = 0f;
        foreach (var state in campaign.StatesOf(nation))
            institutions += state.Buildings.GetValueOrDefault(BuildingKind.University) * MathF.Max(0f, defines.ResearchUniversityBonus)
                + state.Buildings.GetValueOrDefault(BuildingKind.ResearchLab) * MathF.Max(0f, defines.ResearchLaboratoryBonus);
        var institutionFactor = 1f + MathF.Min(MathF.Max(0f, defines.ResearchInstitutionBonusCap), institutions / residents);
        var stabilityFactor = 1f - M.Clamp01(defines.ResearchInstabilityPenalty) * (1f - M.Clamp01(nation.Stability));
        return (defines.ResearchBasePerWeek + economy * schools) * institutionFactor * stabilityFactor *
            NationalModifiers.Factor(nation, "research_speed");
    }

    /// <summary>Knowledge arrives through the world, across borders, and through formal partners.</summary>
    public static DiffusionBreakdown DiffusionFor(Campaign campaign, Nation nation, TechnologyDefinition technology)
    {
        if (campaign is null || nation is null || technology is null) return default;
        var world = 0f;
        var having = 0f;
        var neighbours = CampaignDiplomacy.Neighbours(campaign, nation.MapIndex);
        var borderCount = 0;
        var borderHaving = 0;
        var partnerWeight = 0f;
        var partnerHaving = 0f;
        foreach (var other in campaign.Nations)
        {
            var knows = other != nation && other.HasTechnology(technology.Id);
            if (other.Gdp > 0f)
            {
                world += other.Gdp;
                if (knows) having += other.Gdp;
            }
            if (other == nation || other.Population <= 0 || other.Landless) continue;
            var relation = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
            var isolated = relation is { Stance: DiplomaticStance.AtWar or DiplomaticStance.Rival } ||
                relation?.EmbargoBy(nation.Id) == true || relation?.EmbargoBy(other.Id) == true;
            if (isolated) continue;
            if (neighbours.Contains(other.MapIndex))
            {
                borderCount++;
                if (knows) borderHaving++;
            }
            var weight = relation is { Stance: DiplomaticStance.Allied } ? 2f
                : relation?.TradeAgreement == true ? 1f
                : relation?.NonAggressionPact == true ? 0.5f : 0f;
            partnerWeight += weight;
            if (knows) partnerHaving += weight;
        }
        var defines = campaign.Defines;
        var global = M.Clamp01(defines.ResearchDiffusion) * (world <= 0f ? 0f : having / world);
        var border = M.Clamp01(defines.ResearchNeighbourDiffusion) * (borderCount == 0 ? 0f : borderHaving / (float)borderCount);
        var partners = M.Clamp01(defines.ResearchPartnerDiffusion) * (partnerWeight <= 0f ? 0f : partnerHaving / partnerWeight);
        var discount = MathF.Min(M.Clamp01(defines.ResearchMaximumDiscount), global + border + partners);
        return new DiffusionBreakdown(global, border, partners, discount, technology.Cost * (1f - discount));
    }

    /// <summary>What this country pays for it after knowledge diffusion.</summary>
    public static float CostFor(Campaign campaign, Nation nation, TechnologyDefinition technology) =>
        DiffusionFor(campaign, nation, technology).EffectiveCost;

    /// <summary>Weeks left at this week's pace, or -1 for never.</summary>
    public static int WeeksLeft(Campaign campaign, Nation nation, TechnologyDefinition technology)
    {
        // Facilities, stability and spending can change between weekly ticks. Forecast
        // from today's conditions rather than last week's cached display number.
        var pace = WeeklyPoints(campaign, nation);
        if (pace <= 0f || technology is null) return -1;
        var left = CostFor(campaign, nation, technology) - nation.ResearchProgress.GetValueOrDefault(technology.Id);
        return (int)MathF.Ceiling(MathF.Max(0f, left) / pace);
    }

    /// <summary>
    /// What a country chooses when it chooses for itself: the cheapest thing it can start, and
    /// between two that cost the same, its army's shelf while it is at war and its economy's
    /// otherwise.
    ///
    /// Cheapest first, not army first. Military first at war was the obvious rule and it was a
    /// thumb on the scale of every war already running: the larger side of a war researches
    /// faster, so it took the attack bonuses first and the smaller side never caught up - over two
    /// years the Russo-Ukrainian front went from Ukraine's divisions at 44 % strength to 24 %.
    /// </summary>
    public static TechnologyDefinition Choose(Campaign campaign, Nation nation)
    {
        var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        return AvailableTo(nation)
            .OrderBy(technology => CostFor(campaign, nation, technology))
            .ThenBy(technology => (technology.Category is "military" or "logistics") == atWar ? 0 : 1)
            .ThenBy(technology => technology.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Once a week, with the economy: every country puts its week into what it is researching.</summary>
    public static void Tick(Campaign campaign)
    {
        if (campaign.ElapsedDays == 0 || campaign.ElapsedDays % 7 != 0 || TechnologyCatalog.All.Count == 0) return;
        foreach (var nation in campaign.Nations)
        {
            if (nation.Population <= 0) continue;
            var current = TechnologyCatalog.Of(nation.CurrentResearch);
            if (current is null || !Available(nation, current))
            {
                current = Choose(campaign, nation);
                nation.CurrentResearch = current?.Id ?? "";
            }
            nation.ResearchPerWeek = WeeklyPoints(campaign, nation);
            if (current is null) continue;
            var progress = nation.ResearchProgress.GetValueOrDefault(current.Id) + nation.ResearchPerWeek;
            nation.ResearchProgress[current.Id] = progress;
            if (progress < CostFor(campaign, nation, current)) continue;
            nation.Technologies.Add(current.Id);
            nation.ResearchProgress.Remove(current.Id);
            nation.CurrentResearch = "";
            if (nation.IsHuman) campaign.MarkStrategicChange();
        }
    }
}

/// <summary>Choose what to research next. What was put into the last choice stays with it.</summary>
public sealed class SetResearchCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string TechnologyId { get; init; } = "";

    public bool Set { get; private set; }

    public void Apply(Campaign campaign)
    {
        var nation = campaign.NationById(IssuerId);
        var technology = TechnologyCatalog.Of(TechnologyId);
        if (nation is null || !ResearchSystem.Available(nation, technology)) return;
        nation.CurrentResearch = technology.Id;
        Set = true;
        campaign.MarkStrategicChange();
    }
}
