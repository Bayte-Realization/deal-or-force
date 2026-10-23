using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How long an AI government waits before changing its grand strategy again
    /// (common/defines/77_strategic_doctrines.json). A course, once set, is not twitched.
    /// </summary>
    public int StrategicAiChangeCooldownDays { get; set; } = 540;
}

/// <summary>
/// One strategic doctrine: how a country means to win wars, as opposed to how its divisions
/// fight them (TacticalDoctrineCatalog).
/// </summary>
public sealed record StrategicDoctrine(
    string Id,
    string Name,
    string Description,
    float PoliticalCost,
    IReadOnlyDictionary<string, float> Modifiers,
    float TrainingSlots,
    float DrillExperience,
    float BattleExperience,
    float AirExperience,
    float NavalExperience,
    string TacticalBias);

/// <summary>What a country has chosen, and when it last changed its mind.</summary>
public sealed class StrategicDoctrineMemory
{
    /// <summary>The strategy in force; empty until the government commits to one.</summary>
    public string Id = "";

    public int LastChangeDay = -9999;
}

public sealed partial class Nation
{
    public StrategicDoctrineMemory StrategicDoctrine = new();
}

/// <summary>
/// The seven roads, read from common/strategic_doctrines like every other catalogue: the base game
/// and a mod write the same thing, and a strategy a mod adds is a strategy.
/// </summary>
public static class StrategicDoctrineCatalog
{
    /// <summary>Sane bounds for a learning multiplier; outside them a mod is refused with a sentence.</summary>
    public const float MinMultiplier = 0.25f;
    public const float MaxMultiplier = 4f;

    private static readonly Dictionary<string, StrategicDoctrine> Entries = new(StringComparer.Ordinal);
    public static int Revision { get; private set; }
    public static IEnumerable<StrategicDoctrine> All => Entries.Values.OrderBy(item => item.Id, StringComparer.Ordinal);
    public static StrategicDoctrine Find(string id) => id is null ? null : Entries.GetValueOrDefault(id);
    static StrategicDoctrineCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Entries.Clear(); Revision++;
        foreach (var doc in GameDataLoader.DataDocuments("common", "strategic_doctrines")) using (doc) Merge(doc.RootElement);
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("Strategy: expected an object"); return; }
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//")) continue;
            try
            {
                if (!MechanicFeatures.ValidId(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("invalid strategy");
                var e = entry.Value;
                string Text(string key, string fallback) => e.TryGetProperty(key, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString()
                        : throw new ArgumentException(key + " must be a string")
                    : fallback;
                float Number(string key, float fallback)
                {
                    if (!e.TryGetProperty(key, out var v)) return fallback;
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetSingle(out var parsed) || !float.IsFinite(parsed))
                        throw new ArgumentException(key + " must be a number");
                    return parsed;
                }
                float Multiplier(string key, float fallback)
                {
                    var value = Number(key, fallback);
                    if (value < MinMultiplier || value > MaxMultiplier)
                        throw new ArgumentException(key + " must be between 0.25 and 4");
                    return value;
                }
                var name = Text("name", "");
                if (name.Length == 0) throw new ArgumentException("every strategy carries a name");
                var cost = Number("political_cost", 0f);
                if (cost < 0f) throw new ArgumentException("political cost cannot be negative");
                var local = new List<string>();
                var modifiers = e.TryGetProperty("modifiers", out var block)
                    ? NationalModifiers.Read(block, $"Strategy.{entry.Name}", local)
                    : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                if (local.Count > 0) throw new ArgumentException(string.Join("; ", local));
                var bias = Text("tactical_bias", "").Trim();
                // A bias naming no tree is treated as no bias where it is read, so a strategy may
                // be added before the tree it leans on; the shipped seven are checked in tests.
                Entries[entry.Name] = new StrategicDoctrine(
                    entry.Name, name, Text("description", ""), cost, modifiers,
                    Multiplier("training_slots", 1f), Multiplier("drill_experience", 1f),
                    Multiplier("battle_experience", 1f), Multiplier("air_experience", 1f),
                    Multiplier("naval_experience", 1f), bias);
            }
            catch (Exception ex)
            {
                var sentence = $"strategy '{entry.Name}': {ex.Message}";
                if (errors is null) throw new InvalidOperationException(sentence, ex);
                errors.Add(sentence);
            }
        }
    }
}

/// <summary>
/// The country's grand strategy, and what the state does about it: one choice out of seven roads,
/// changed only at a political price and never in the middle of a war. This layer says how the
/// country means to win; the tactical layer says how its divisions fight.
/// </summary>
public static class StrategicDoctrines
{
    /// <summary>Which strategy each character of government reaches for (AiStrategyCatalog).</summary>
    private static readonly Dictionary<string, string> CharacterStrategies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blitzkrieg"] = "manoeuvre_war", ["irredentist"] = "manoeuvre_war",
        ["attrition"] = "attrition_war", ["major_offensive"] = "attrition_war",
        ["defensive"] = "homeland_defence", ["fortress"] = "homeland_defence",
        ["garrison_state"] = "homeland_defence", ["isolated"] = "homeland_defence",
        ["minor_power"] = "universal_levy",
        ["expeditionary"] = "air_power", ["trading_state"] = "sea_power",
        ["default"] = "professional_cadre"
    };

    public static StrategicDoctrine Of(Nation nation) => StrategicDoctrineCatalog.Find(nation?.StrategicDoctrine.Id);
    public static bool InForce(Nation nation) => Of(nation) is not null;

    /// <summary>The strategy's hand on learning. One when a country has chosen none.</summary>
    public static float TrainingSlots(Nation nation) => Of(nation)?.TrainingSlots ?? 1f;
    public static float DrillExperience(Nation nation) => Of(nation)?.DrillExperience ?? 1f;
    public static float BattleExperience(Nation nation) => Of(nation)?.BattleExperience ?? 1f;
    public static float AirExperience(Nation nation) => Of(nation)?.AirExperience ?? 1f;
    public static float NavalExperience(Nation nation) => Of(nation)?.NavalExperience ?? 1f;

    /// <summary>
    /// The tactical tree this strategy leans on, or empty. A bias naming no tree is no bias: a
    /// strategy may be written before the tree it leans on, and the AI falls back on its character.
    /// </summary>
    public static string TacticalBias(Nation nation)
    {
        var bias = Of(nation)?.TacticalBias ?? "";
        return TacticalDoctrineCatalog.Find(bias) is null ? "" : bias;
    }

    /// <summary>The strategy in force, as one source of national modifiers - read by NationalModifiers.</summary>
    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation nation)
    {
        var doctrine = Of(nation);
        if (doctrine is not null) yield return (doctrine.Name, doctrine.Modifiers);
    }

    /// <summary>Why this strategy cannot be taken up now, in the words the interface shows. Empty means it can.</summary>
    public static string Refusal(Campaign campaign, Nation nation, string id)
    {
        if (!campaign.FeatureEnabled("strategic_doctrines")) return Loc.Get("This mechanic is disabled in this scenario.");
        var doctrine = StrategicDoctrineCatalog.Find(id);
        if (doctrine is null) return Loc.Get("No such strategy is written.");
        if (string.Equals(nation.StrategicDoctrine.Id, id, StringComparison.Ordinal))
            return Loc.Get("This is already the country's strategy.");
        if (nation.StrategicDoctrine.Id.Length == 0) return "";      // the first choice is free
        if (nation.PoliticalPoints + 0.001f < doctrine.PoliticalCost)
            return Loc.Format("Changing the grand strategy costs {0} political points.", doctrine.PoliticalCost);
        return "";
    }

    /// <summary>The one door: the command, the AI and a test all come through here.</summary>
    public static bool Adopt(Campaign campaign, Nation nation, string id)
    {
        var doctrine = StrategicDoctrineCatalog.Find(id);
        if (doctrine is null || Refusal(campaign, nation, id).Length > 0) return false;
        var changing = nation.StrategicDoctrine.Id.Length > 0;
        // Paid only after every refusal has passed: a course the country cannot afford to change
        // must not spend the credit and keep the old course anyway.
        if (changing) nation.PoliticalPoints = MathF.Max(0f, nation.PoliticalPoints - doctrine.PoliticalCost);
        nation.StrategicDoctrine.Id = doctrine.Id;
        nation.StrategicDoctrine.LastChangeDay = campaign.ElapsedDays;
        NationalModifiers.Invalidate(nation);
        campaign.Chronicle.Record(campaign, changing ? "strategy_changed" : "strategy_adopted",
            nation.Id, subject: doctrine.Name, isPublic: true);
        if (Delegation.RunByAi(nation)) Count(changing ? "strategy_changed" : "strategy_adopted");
        campaign.MarkStrategicChange();
        return true;
    }

    private static void Count(string what) =>
        AiStatecraft.Tally[what] = AiStatecraft.Tally.GetValueOrDefault(what) + 1;

    /// <summary>What this government would choose on its own character.</summary>
    public static string Preferred(Campaign campaign, Nation nation) =>
        CharacterStrategies.TryGetValue(AiStrategyCatalog.Of(campaign, nation).Id, out var id)
            ? id : "professional_cadre";

    /// <summary>
    /// The AI's own hand: a country with no course adopts one, and a government whose character has
    /// changed changes course - at long intervals, in peace, and only if it can pay for it.
    /// </summary>

    public static void Tick(Campaign campaign)
    {
        if (campaign is null || !campaign.FeatureEnabled("strategic_doctrines")) return;
        var day = campaign.ElapsedDays;
        if (campaign.LastStrategicDoctrineDay == day) return;
        campaign.LastStrategicDoctrineDay = day;
        foreach (var nation in campaign.Nations)
        {
            if (!Delegation.RunByAi(nation)) continue;
            // Its own day of the week, on a longer cycle than the book: a course is not twitched.
            if ((day + nation.Id) % 11 != 0) continue;
            var wanted = Preferred(campaign, nation);
            if (nation.StrategicDoctrine.Id.Length == 0) { Adopt(campaign, nation, wanted); continue; }
            if (string.Equals(nation.StrategicDoctrine.Id, wanted, StringComparison.Ordinal)) continue;
            if (day - nation.StrategicDoctrine.LastChangeDay < campaign.Defines.StrategicAiChangeCooldownDays) continue;
            if (campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id))) continue;
            Adopt(campaign, nation, wanted);
        }
    }
}

public sealed class AdoptStrategicDoctrineCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string DoctrineId { get; init; } = "";

    /// <summary>Why the order could not be carried out, in the words the interface shows.</summary>
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        Refusal = StrategicDoctrines.Refusal(campaign, nation, DoctrineId);
        if (Refusal.Length > 0) return;
        StrategicDoctrines.Adopt(campaign, nation, DoctrineId);
    }
}
