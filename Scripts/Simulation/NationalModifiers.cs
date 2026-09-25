using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// What a country's technologies, its national spirits, its form of government, its ideology
/// and its cabinet's focus add up to, key by key - the numbers a Paradox game calls modifiers:
/// "Construction speed: +15 %", "Division attack: +5 %".
///
/// There was one source of them, research, read in six places. A national spirit could only
/// nudge a quantity a little every week (`effects`, the drifts), a government only drift its
/// stability, so nothing in the game could say "while the war economy lasts, factories are
/// built faster" - and a mod had nowhere to say it either. Now every one of those sources may
/// carry a `modifiers` block written in the keys below, and the simulation reads the sum
/// wherever the thing it modifies is worked out.
///
/// A key is a share: 0.10 is ten per cent more of whatever the key names, -0.10 ten per cent
/// less. The weekly keys are the exception - they are points a week, the way the governments'
/// own drifts already are. The keys themselves are fixed, because each is a question the
/// simulation has to be written to ask; what is open is who carries them and how much.
/// </summary>
public static class NationalModifiers
{
    /// <summary>One key: its id, what a tooltip calls it, what it does, and which way is good.</summary>
    public sealed record Key(string Id, string Name, string Help, bool HigherIsBetter = true, bool Weekly = false);

    public static readonly IReadOnlyList<Key> Keys = new Key[]
    {
        // The economy.
        new("output", "Output", "What the country's farms, mines and factories produce."),
        new("research_speed", "Research speed", "Research points a week."),
        new("construction_speed", "Construction speed", "How fast a building goes up: its build time is divided by one plus this."),
        new("construction_cost", "Construction cost", "What a building costs to put up.", HigherIsBetter: false),
        new("tax_efficiency", "Tax efficiency", "How much of what the taxes ask for actually arrives."),
        new("trade_reach", "Trade reach", "How far the country's goods travel, in the same shares as ports and railways add."),
        new("reconstruction_speed", "Reconstruction speed", "How fast regions the war has wrecked are rebuilt."),
        // The army.
        new("army_attack", "Division attack", "The fire a division puts down in an attack."),
        new("army_defense", "Division defence", "The fire a division puts down holding ground."),
        new("army_speed", "Division speed", "How fast a division marches."),
        new("supply", "Supply", "Supply throughput along the country's lines."),
        new("organisation_recovery", "Organisation recovery", "How fast a division out of battle gets its organisation back."),
        new("entrenchment_speed", "Entrenchment speed", "How fast a division standing still digs in."),
        new("reinforcement_rate", "Reinforcement rate", "Replacements a day: men and equipment sent to divisions under strength."),
        new("recruitment_speed", "Training speed", "How fast new divisions are trained: the training time is divided by one plus this."),
        new("recruitment_cost", "Training cost", "What a new division costs to raise.", HigherIsBetter: false),
        // The navy.
        new("naval_speed", "Fleet speed", "How fast a fleet sails."),
        new("shipbuilding_speed", "Shipbuilding speed", "How fast the yards lay down and finish hulls."),
        // The country, a little every week.
        new("stability_weekly", "Stability a week", "Added to stability every week, in points.", Weekly: true),
        new("war_support_weekly", "War support a week", "Added to war support every week, in points.", Weekly: true),
        new("radicalism_weekly", "Radicalism a week", "Added to radicalism in every region every week, in points.", HigherIsBetter: false, Weekly: true),
        new("living_weekly", "Standard of living a week", "Added to the standard of living in every region every week, in points.", Weekly: true),
    };

    private static readonly Dictionary<string, Key> ById = Keys.ToDictionary(key => key.Id, StringComparer.OrdinalIgnoreCase);

    public static bool Knows(string id) => id is not null && ById.ContainsKey(id);

    public static Key Of(string id) => id is not null && ById.TryGetValue(id, out var key) ? key : null;

    /// <summary>What every source adds to this key for this country.</summary>
    public static float Of(Nation nation, string key)
    {
        if (nation is null) return 0f;
        var totals = nation.ModifierTotals ??= Build(nation);
        return totals.TryGetValue(key, out var value) ? value : 0f;
    }

    /// <summary>One plus <see cref="Of(Nation,string)"/>, never below a tenth: a multiplier.</summary>
    public static float Factor(Nation nation, string key) => MathF.Max(0.1f, 1f + Of(nation, key));

    /// <summary>Work the sums out again the next time they are asked for: a source changed.</summary>
    public static void Invalidate(Nation nation)
    {
        if (nation is not null) nation.ModifierTotals = null;
    }

    /// <summary>Every source of one country's modifiers, with what each adds - for a tooltip.</summary>
    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation nation)
    {
        if (nation is null) yield break;
        foreach (var id in nation.Technologies)
            if (TechnologyCatalog.Of(id) is { } technology && technology.Effects.Count > 0)
                yield return (technology.Name, technology.Effects);
        foreach (var active in nation.Modifiers)
            if (ModifierCatalog.Get(active.Id) is { Modifiers.Count: > 0 } spirit)
                yield return (spirit.Name, spirit.Modifiers);
        if (GovernmentCatalog.Of(nation.Government) is { Modifiers.Count: > 0 } government)
            yield return (government.Name, government.Modifiers);
        if (IdeologyCatalog.Of(nation.Ideology) is { Modifiers.Count: > 0 } ideology)
            yield return (ideology.Name, ideology.Modifiers);
        if (GovernmentFocusCatalog.Of(nation.GovernmentFocus) is { Modifiers.Count: > 0 } focus)
            yield return (focus.Name, focus.Modifiers);
    }

    private static Dictionary<string, float> Build(Nation nation)
    {
        var totals = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, modifiers) in Sources(nation))
            foreach (var (key, value) in modifiers)
                totals[key] = totals.GetValueOrDefault(key) + value;
        return totals;
    }

    /// <summary>
    /// Once a day: the sums are worked out afresh - research finishes, a spirit runs out, a
    /// government falls - and the weekly keys are paid, a seventh of a week at a time.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var nation in campaign.Nations)
        {
            nation.ModifierTotals = null;
            var stability = Of(nation, "stability_weekly");
            var support = Of(nation, "war_support_weekly");
            var radicalism = Of(nation, "radicalism_weekly");
            var living = Of(nation, "living_weekly");
            if (stability != 0f) nation.Stability = M.Clamp(nation.Stability + stability / 7f, 0.10f, 0.98f);
            if (support != 0f) nation.WarSupport = M.Clamp01(nation.WarSupport + support / 7f);
            if (radicalism == 0f && living == 0f) continue;
            foreach (var state in campaign.StatesOf(nation))
            {
                if (radicalism != 0f) state.Radicalism = M.Clamp01(state.Radicalism + radicalism / 7f);
                if (living != 0f) state.StandardOfLiving = M.Clamp01(state.StandardOfLiving + living / 7f);
            }
        }
    }

    /// <summary>
    /// A `modifiers` block from a file: every key checked against the list, so a typo is an
    /// error with the list beside it rather than a bonus that silently never applies.
    /// </summary>
    public static Dictionary<string, float> Read(JsonElement element, string context, List<string> errors)
    {
        var read = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"{context}: `modifiers` must be an object of key: share.");
            return read;
        }
        foreach (var entry in element.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            if (!Knows(entry.Name))
            {
                errors?.Add($"{context}: `{entry.Name}` is not a modifier. The keys are: {string.Join(", ", Keys.Select(key => key.Id))}.");
                continue;
            }
            if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetSingle(out var value) || !float.IsFinite(value))
            {
                errors?.Add($"{context}: `{entry.Name}` must be a number.");
                continue;
            }
            read[Of(entry.Name).Id] = value;
        }
        return read;
    }

    /// <summary>
    /// The lines a tooltip shows for a block of modifiers, in the shape events and decisions use:
    /// "Construction speed: +15%", "Stability a week: +0.1". Good ones and bad ones read the
    /// same; which is which is the caller's colour to choose, and <see cref="IsGood"/> says.
    /// </summary>
    public static IReadOnlyList<string> Lines(IReadOnlyDictionary<string, float> modifiers)
    {
        var lines = new List<string>();
        if (modifiers is null) return lines;
        var culture = CultureInfo.CurrentCulture;
        foreach (var key in Keys)
        {
            if (!modifiers.TryGetValue(key.Id, out var value) || value == 0f) continue;
            var text = key.Weekly
                ? (value * 100f).ToString("+0.##;-0.##", culture)
                : (value * 100f).ToString("+0.#;-0.#", culture) + "%";
            lines.Add(Loc.Format("{0}: {1}", Loc.Get(key.Name), text));
        }
        return lines;
    }

    /// <summary>The same lines under a heading, ready to go under a tooltip's prose. Empty when there are none.</summary>
    public static string Block(IReadOnlyDictionary<string, float> modifiers)
    {
        var lines = Lines(modifiers);
        return lines.Count == 0 ? "" : Loc.Get("Modifiers:") + "\n" + string.Join("\n", lines.Select(line => "  " + line));
    }

    /// <summary>Whether a value of this key is good for the country that has it.</summary>
    public static bool IsGood(string key, float value) =>
        Of(key) is { } known && (known.HigherIsBetter ? value > 0f : value < 0f);
}

public sealed partial class Nation
{
    /// <summary>What <see cref="NationalModifiers"/> added up for this country, until a source changes. Not saved.</summary>
    internal Dictionary<string, float> ModifierTotals;
}
