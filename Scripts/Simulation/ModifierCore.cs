using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// A named, usually temporary condition attached to a country - Hearts of Iron calls
/// these national spirits, Victoria calls them modifiers, and both use them for the same
/// reason: an event that permanently nudges a number is forgotten the moment it resolves,
/// while one that hangs a named condition on the country for a year is something the
/// player keeps playing around.
///
/// Definitions live in `common/modifiers/*.json` and are written in the same effect
/// vocabulary as events and decisions, so nothing new has to be learned to author one:
///
///   "war_economy": {
///     "name": "War economy",
///     "description": "Industry is on a war footing.",
///     "days": 365,
///     "effects": { "productivity": 0.0004, "state.radicalism": 0.0002 }
///   }
///
/// The effects apply once per economic tick while the modifier is active, so the numbers
/// are per-tick rates rather than one-off amounts.
/// </summary>
public sealed class ModifierDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>Campaign days the modifier lasts. Zero or less means permanent.</summary>
    public int Days { get; init; }

    /// <summary>True when this is one of the good ones, for UI colouring.</summary>
    public bool IsPositive { get; init; } = true;

    public ScriptedEffects.Effect Effects { get; init; } = new();

    /// <summary>
    /// What it adds while it lasts, in the keys of <see cref="NationalModifiers"/>: shares of
    /// what the simulation works out anyway - "construction_speed": 0.15 is a sixth faster -
    /// rather than the per-tick drifts `effects` are.
    /// </summary>
    public IReadOnlyDictionary<string, float> Modifiers { get; init; } =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One modifier actually attached to one country.</summary>
public sealed class ActiveModifier
{
    public string Id = "";
    public int GrantedDay;
    public int ExpiresDay;

    /// <summary>Zero or less means it never expires on its own.</summary>
    public bool IsPermanent => ExpiresDay <= 0;

    public int DaysRemaining(int today) => IsPermanent ? int.MaxValue : Math.Max(0, ExpiresDay - today);
}

public static class ModifierCatalog
{
    private static readonly Dictionary<string, ModifierDefinition> Definitions =
        new(StringComparer.OrdinalIgnoreCase);

    static ModifierCatalog() => ResetCatalog();

    public static IReadOnlyCollection<ModifierDefinition> All => Definitions.Values;

    public static ModifierDefinition Get(string id) =>
        id is not null && Definitions.TryGetValue(id, out var definition) ? definition : null;

    public static void ResetCatalog()
    {
        Definitions.Clear();
        foreach (var definition in Defaults()) Definitions[definition.Id] = definition;

        foreach (var file in GameDataLoader.DataFiles("common", "modifiers"))
        {
            try { Load(StructuredData.ReadJson(file), null); }
            catch { /* One unreadable file costs that file, not the whole category. */ }
        }
    }

    private static IEnumerable<ModifierDefinition> Defaults() => new[]
    {
        new ModifierDefinition
        {
            Id = "war_economy",
            Name = "War economy",
            Description = "Industry has been placed on a war footing. Output climbs; the people notice what it costs.",
            Days = 365,
            IsPositive = true,
            Effects = ScriptedEffects.From(("productivity", 0.0022f), ("state.radicalism", 0.0010f))
        },
        new ModifierDefinition
        {
            Id = "national_mourning",
            Name = "National mourning",
            Description = "The country is grieving. Ordinary business waits.",
            Days = 90,
            IsPositive = false,
            Effects = ScriptedEffects.From(("stability", -0.0015f), ("productivity", -0.0012f))
        },
        new ModifierDefinition
        {
            Id = "postwar_settlement",
            Name = "Post-war settlement",
            Description = "The guns are quiet and the reconstruction budget is being spent.",
            Days = 540,
            IsPositive = true,
            Effects = ScriptedEffects.From(("stability", 0.0016f), ("state.standard_of_living", 0.0008f))
        },
        new ModifierDefinition
        {
            Id = "investment_boom",
            Name = "Investment boom",
            Description = "Capital is arriving faster than anyone planned for.",
            Days = 270,
            IsPositive = true,
            Effects = ScriptedEffects.From(("productivity", 0.0026f), ("construction_capacity", 0.004f))
        },
        new ModifierDefinition
        {
            Id = "austerity",
            Name = "Austerity programme",
            Description = "The budget balances. Very little else does.",
            Days = 365,
            IsPositive = false,
            Effects = ScriptedEffects.From(("state.standard_of_living", -0.0010f), ("state.radicalism", 0.0014f))
        }
    };

    public static bool Load(string json, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var value = entry.Value;
            if (value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"Modifier '{entry.Name}' must be an object.");
                continue;
            }

            Definitions[entry.Name] = new ModifierDefinition
            {
                Id = entry.Name,
                Name = value.TryGetProperty("name", out var name) ? name.GetString() ?? entry.Name : entry.Name,
                Description = value.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
                Days = value.TryGetProperty("days", out var days) && days.TryGetInt32(out var parsedDays) ? parsedDays : 0,
                IsPositive = !value.TryGetProperty("positive", out var positive) ||
                             positive.ValueKind != JsonValueKind.False,
                Effects = value.TryGetProperty("effects", out var effects)
                    ? ScriptedEffects.Parse(effects, $"Modifier '{entry.Name}'", errors)
                    : new ScriptedEffects.Effect(),
                Modifiers = value.TryGetProperty("modifiers", out var modifiers)
                    ? NationalModifiers.Read(modifiers, $"Modifier '{entry.Name}'", errors)
                    : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            };
        }
        return true;
    }
}

public static class ModifierSystem
{
    /// <summary>Attach a modifier, or refresh its timer if the country already has it.</summary>
    public static bool Grant(Campaign campaign, Nation nation, string id, int daysOverride = 0)
    {
        var definition = ModifierCatalog.Get(id);
        if (definition is null || nation is null) return false;

        var days = daysOverride > 0 ? daysOverride : definition.Days;
        var expires = days > 0 ? campaign.ElapsedDays + days : 0;

        var existing = nation.Modifiers.FirstOrDefault(item =>
            string.Equals(item.Id, definition.Id, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // Re-granting refreshes rather than stacks: two copies of "war economy" is a
            // bookkeeping artefact, not something a player would ever mean.
            existing.GrantedDay = campaign.ElapsedDays;
            existing.ExpiresDay = expires;
            return true;
        }
        NationalModifiers.Invalidate(nation);

        nation.Modifiers.Add(new ActiveModifier
        {
            Id = definition.Id,
            GrantedDay = campaign.ElapsedDays,
            ExpiresDay = expires
        });
        return true;
    }

    public static bool Revoke(Nation nation, string id)
    {
        NationalModifiers.Invalidate(nation);
        return nation is not null && nation.Modifiers.RemoveAll(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public static bool Has(Nation nation, string id) =>
        nation is not null && nation.Modifiers.Any(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Applies every active modifier once per economic tick and drops the expired ones.
    /// Runs before the economy's own pass so a modifier's productivity change is already
    /// in place when output is priced.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(ModifierSystem))) return;
        // Every day: what the modifiers add up to, and the weekly ones paid a day at a time.
        NationalModifiers.Tick(campaign);
        if (campaign.ElapsedDays == 0 || campaign.ElapsedDays % campaign.Defines.EconomyTickDays != 0) return;

        foreach (var nation in campaign.Nations)
        {
            if (nation.Modifiers.Count == 0) continue;

            for (var index = nation.Modifiers.Count - 1; index >= 0; index--)
            {
                var active = nation.Modifiers[index];
                if (!active.IsPermanent && active.ExpiresDay <= campaign.ElapsedDays)
                {
                    nation.Modifiers.RemoveAt(index);
                    continue;
                }

                ModifierCatalog.Get(active.Id)?.Effects.Apply(campaign, nation);
            }
        }
    }
}
