using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// One option on an event card. Its consequence is a <see cref="ScriptedEffects.Effect"/>
/// rather than a closed enum member, so a mod can write an outcome nobody anticipated
/// instead of picking from a fixed shortlist.
/// </summary>
public sealed class CampaignEventOption
{
    public string Title { get; init; } = "Option";
    public string Consequence { get; init; } = "";
    public ScriptedEffects.Effect Effect { get; init; } = new();

    /// <summary>Modifier ids this option attaches to the country, with optional durations.</summary>
    public (string Id, int Days)[] Modifiers { get; init; } = Array.Empty<(string, int)>();

    /// <summary>Falls back to a generated summary when content shipped no prose.</summary>
    public string ConsequenceText => string.IsNullOrWhiteSpace(Consequence) ? Effect.Describe() : Consequence;
}

public sealed class CampaignEventDefinition
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public CampaignEventOption[] Options { get; init; } = Array.Empty<CampaignEventOption>();

    /// <summary>Optional gate. An empty trigger matches every nation, which is what a
    /// plain flavour event wants.</summary>
    public ScriptedTrigger Trigger { get; init; } = new();

    /// <summary>Relative selection weight once the trigger passes.</summary>
    public float Weight { get; init; } = 1f;
}

public static class EventCatalog
{
    private static readonly List<CampaignEventDefinition> Definitions = new();

    private static CampaignEventDefinition[] Defaults() => new[]
    {
        new CampaignEventDefinition
        {
            Id = "industrial_bottlenecks",
            Title = "Industrial bottlenecks",
            Description = "Manufacturers report that ageing plant and congested logistics are holding back output. The cabinet requests a clear priority.",
            Options = new[]
            {
                new CampaignEventOption
                {
                    Title = "Launch a modernisation programme",
                    Consequence = "Costs treasury; raises GDP, productivity and construction capacity.",
                    Effect = ScriptedEffects.From(("treasury_percent", -0.06f), ("gdp_percent", 0.0035f),
                        ("productivity", 0.008f), ("construction_capacity", 0.18f))
                },
                new CampaignEventOption
                {
                    Title = "Industry must adapt privately",
                    Consequence = "Preserves cash; modest stability loss.",
                    Effect = ScriptedEffects.From(("stability", -0.012f))
                }
            }
        },
        new CampaignEventDefinition
        {
            Id = "housing_pressure",
            Title = "Housing pressure in growing cities",
            Description = "Rapid urbanisation is raising rents and worsening living conditions in the largest regional centres.",
            Options = new[]
            {
                new CampaignEventOption
                {
                    Title = "Fund public housing",
                    Consequence = "Costs treasury; improves living standards and reduces radicalism.",
                    Effect = ScriptedEffects.From(("treasury_percent", -0.04f), ("standard_of_living", 0.012f),
                        ("state.standard_of_living", 0.010f), ("state.radicalism", -0.018f))
                },
                new CampaignEventOption
                {
                    Title = "Ease planning rules",
                    Consequence = "Cheaper and pro-growth, but less socially stabilising.",
                    Effect = ScriptedEffects.From(("gdp_percent", 0.0015f), ("construction_capacity", 0.08f))
                }
            }
        },
        new CampaignEventDefinition
        {
            Id = "labour_dispute",
            Title = "National labour dispute",
            Description = "A wage dispute is spreading across transport and heavy industry. Production is beginning to slow.",
            Options = new[]
            {
                new CampaignEventOption
                {
                    Title = "Broker a national settlement",
                    Consequence = "Costs treasury; restores stability and living standards.",
                    Effect = ScriptedEffects.From(("treasury_percent", -0.03f), ("stability", 0.018f),
                        ("standard_of_living", 0.006f))
                },
                new CampaignEventOption
                {
                    Title = "Order workers back",
                    Consequence = "Preserves cash and output, but raises radicalism.",
                    Effect = ScriptedEffects.From(("stability", -0.020f), ("state.radicalism", 0.025f))
                }
            }
        },

        // Ordinary good news. Every event above can cost a government its standing; none
        // of these can. A flavour pool with nothing but crises in it teaches a player to
        // dread the event popup, which is the opposite of what flavour is for.
        new CampaignEventDefinition
        {
            Id = "national_holiday",
            Title = "A day for celebration",
            Description = "The whole country seems to have taken the day off at once - stalls in every square, music down every street. It happens like this every so often, and nobody ever quite plans it.",
            Weight = 1.4f,
            Options = new[]
            {
                new CampaignEventOption
                {
                    Title = "Open the public purse",
                    Consequence = "A modest cost; the mood is worth it.",
                    Effect = ScriptedEffects.From(("treasury_percent", -0.01f), ("standard_of_living", 0.008f),
                        ("state.radicalism", -0.014f))
                },
                new CampaignEventOption
                {
                    Title = "Let the streets have their day",
                    Consequence = "Costs nothing at all.",
                    Effect = ScriptedEffects.From(("state.radicalism", -0.006f))
                }
            }
        },
        new CampaignEventDefinition
        {
            Id = "sporting_triumph",
            Title = "A nation watching one screen",
            Description = "For ninety minutes nobody in the country was arguing about anything else. The team has won, and tonight that is the only news that matters.",
            Options = new[]
            {
                new CampaignEventOption
                {
                    Title = "Throw them a parade",
                    Consequence = "A small cost; a real lift in the national mood.",
                    Effect = ScriptedEffects.From(("treasury_percent", -0.012f), ("stability", 0.010f),
                        ("state.radicalism", -0.020f))
                },
                new CampaignEventOption
                {
                    Title = "Let the win speak for itself",
                    Consequence = "Free, and almost as good.",
                    Effect = ScriptedEffects.From(("stability", 0.004f))
                }
            }
        },
        new CampaignEventDefinition
        {
            Id = "cultural_export",
            Title = "Everyone is watching us",
            Description = "A film made here, on a budget nobody expected to travel, is filling cinemas on three continents. Diplomats report it is unexpectedly easier to get a meeting this month.",
            Options = new[]
            {
                new CampaignEventOption
                {
                    Title = "Back the industry that made it",
                    Consequence = "Costs treasury; lasting productivity and prestige.",
                    Effect = ScriptedEffects.From(("treasury_percent", -0.018f), ("productivity", 0.004f),
                        ("literacy", 0.002f))
                },
                new CampaignEventOption
                {
                    Title = "Let the moment be its own reward",
                    Consequence = "No cost; a brief lift.",
                    Effect = ScriptedEffects.From(("stability", 0.004f))
                }
            }
        },
        new CampaignEventDefinition
        {
            Id = "scientific_prize",
            Title = "A prize comes home",
            Description = "A researcher who has spent a career in an underfunded university department has just won the field's highest honour. The interview requests have not stopped since.",
            Options = new[]
            {
                new CampaignEventOption
                {
                    Title = "Fund the department properly at last",
                    Consequence = "Costs treasury; literacy and productivity rise.",
                    Effect = ScriptedEffects.From(("treasury_percent", -0.02f), ("literacy", 0.004f),
                        ("productivity", 0.006f))
                },
                new CampaignEventOption
                {
                    Title = "A medal and a photograph",
                    Consequence = "Free; a small national lift.",
                    Effect = ScriptedEffects.From(("stability", 0.006f))
                }
            }
        }
    };

    static EventCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Definitions.Clear();
        Definitions.AddRange(Defaults());
        var file = GameDataLoader.FindDataFile("events", "core_events.json");
        if (file is null) return;
        try { Load(File.ReadAllText(file), null); }
        catch { /* Compiled defaults deliberately remain bootable. */ }
    }

    public static IReadOnlyList<CampaignEventDefinition> All => Definitions;

    /// <summary>
    /// Legacy bridge: the first content format named one of six hard-coded outcomes with
    /// "effect": "InvestIndustry". Those names keep working as presets so older content
    /// does not break, but they are now shorthand for an ordinary scripted effect.
    /// </summary>
    private static ScriptedEffects.Effect LegacyPreset(string name) => name?.Trim().ToLowerInvariant() switch
    {
        "investindustry" => ScriptedEffects.From(("treasury_percent", -0.06f), ("gdp_percent", 0.0035f),
            ("productivity", 0.008f), ("construction_capacity", 0.18f)),
        "preservetreasury" => ScriptedEffects.From(("stability", -0.012f)),
        "fundhousing" => ScriptedEffects.From(("treasury_percent", -0.04f), ("standard_of_living", 0.012f),
            ("state.standard_of_living", 0.010f), ("state.radicalism", -0.018f)),
        "markethousing" => ScriptedEffects.From(("gdp_percent", 0.0015f), ("construction_capacity", 0.08f)),
        "negotiatelabour" => ScriptedEffects.From(("treasury_percent", -0.03f), ("stability", 0.018f),
            ("standard_of_living", 0.006f)),
        "breakstrike" => ScriptedEffects.From(("stability", -0.020f), ("state.radicalism", 0.025f)),
        _ => null
    };

    /// <summary>
    /// Reads an option's "modifiers" list. Accepts a bare id or an object with its own
    /// duration - "modifiers": ["war_economy"] and
    /// "modifiers": [{ "id": "war_economy", "days": 180 }] both work, because the short
    /// form is what most content wants and the long one should not be the price of entry.
    /// </summary>
    private static (string Id, int Days)[] ParseModifiers(JsonElement option, string context, List<string> errors)
    {
        if (!option.TryGetProperty("modifiers", out var element) || element.ValueKind != JsonValueKind.Array)
            return Array.Empty<(string, int)>();

        var result = new List<(string, int)>();
        foreach (var entry in element.EnumerateArray())
        {
            string id = null;
            var days = 0;

            if (entry.ValueKind == JsonValueKind.String) id = entry.GetString();
            else if (entry.ValueKind == JsonValueKind.Object)
            {
                if (entry.TryGetProperty("id", out var idElement)) id = idElement.GetString();
                if (entry.TryGetProperty("days", out var daysElement) && daysElement.TryGetInt32(out var parsed)) days = parsed;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                errors?.Add($"{context}: a modifier entry has no id.");
                continue;
            }
            if (ModifierCatalog.Get(id) is null)
                errors?.Add($"{context}: unknown modifier '{id}'.");

            result.Add((id, days));
        }
        return result.ToArray();
    }

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
            if (!value.TryGetProperty("title", out var title) || !value.TryGetProperty("description", out var description) ||
                !value.TryGetProperty("options", out var optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
            {
                errors?.Add($"Event '{entry.Name}' requires title, description and options.");
                continue;
            }

            var options = new List<CampaignEventOption>();
            foreach (var option in optionsElement.EnumerateArray())
            {
                var context = $"Event '{entry.Name}'";
                ScriptedEffects.Effect effect = null;

                if (option.TryGetProperty("effects", out var effects))
                {
                    effect = ScriptedEffects.Parse(effects, context, errors);
                }
                else if (option.TryGetProperty("effect", out var legacy))
                {
                    if (legacy.ValueKind == JsonValueKind.Object)
                    {
                        effect = ScriptedEffects.Parse(legacy, context, errors);
                    }
                    else if (legacy.ValueKind == JsonValueKind.String)
                    {
                        effect = LegacyPreset(legacy.GetString());
                        if (effect is null)
                            errors?.Add($"{context}: unknown legacy effect '{legacy.GetString()}'. Prefer an \"effects\" object.");
                    }
                }

                effect ??= new ScriptedEffects.Effect();

                options.Add(new CampaignEventOption
                {
                    Title = option.TryGetProperty("title", out var optionTitle) ? optionTitle.GetString() ?? "Option" : "Option",
                    Consequence = option.TryGetProperty("consequence", out var consequence) ? consequence.GetString() ?? "" : "",
                    Effect = effect,
                    Modifiers = ParseModifiers(option, context, errors)
                });
            }

            if (options.Count == 0)
            {
                errors?.Add($"Event '{entry.Name}' has no usable options.");
                continue;
            }

            var definition = new CampaignEventDefinition
            {
                Id = entry.Name,
                Title = title.GetString() ?? entry.Name,
                Description = description.GetString() ?? "",
                Options = options.ToArray(),
                Trigger = value.TryGetProperty("trigger", out var trigger)
                    ? ScriptedTrigger.Parse(trigger, $"Event '{entry.Name}'", errors)
                    : new ScriptedTrigger(),
                Weight = value.TryGetProperty("weight", out var weight) && weight.TryGetSingle(out var parsedWeight)
                    ? MathF.Max(0f, parsedWeight)
                    : 1f
            };

            var index = Definitions.FindIndex(item => string.Equals(item.Id, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) Definitions[index] = definition;
            else Definitions.Add(definition);
        }
        return true;
    }
}

public sealed class PendingCampaignEvent
{
    public int Id;
    public int NationId;
    public string Title = "";
    public string Description = "";
    public int CreatedDay;
    public int ExpiresDay;
    public CampaignEventOption[] Options = Array.Empty<CampaignEventOption>();
}

public static class EventSystem
{
    public static void Tick(Campaign campaign)
    {
        if (campaign.ElapsedDays > 0 && campaign.ElapsedDays % 75 == 0)
        {
            foreach (var nationId in campaign.HumanSeats)
            {
                if (campaign.PendingEvents.Any(item => item.NationId == nationId)) continue;
                var created = Create(campaign, nationId);
                if (created is not null) campaign.PendingEvents.Add(created);
            }
        }

        foreach (var item in campaign.PendingEvents.Where(item => item.ExpiresDay <= campaign.ElapsedDays).ToArray())
            Resolve(campaign, item, 1);
    }

    public static void Resolve(Campaign campaign, PendingCampaignEvent item, int optionIndex)
    {
        if (!campaign.PendingEvents.Contains(item) || item.Options.Length == 0) return;
        optionIndex = M.Clamp(optionIndex, 0, item.Options.Length - 1);
        var option = item.Options[optionIndex];
        var nation = campaign.NationById(item.NationId);
        option.Effect.Apply(campaign, nation);
        foreach (var (id, days) in option.Modifiers) ModifierSystem.Grant(campaign, nation, id, days);
        campaign.PendingEvents.Remove(item);
        campaign.MarkStrategicChange();
    }

    /// <summary>
    /// Weighted pick among the events whose trigger the nation actually satisfies. The
    /// draw is seeded from campaign state rather than a live RNG call, so replaying the
    /// same seed deals the same card on the same day.
    /// </summary>
    private static PendingCampaignEvent Create(Campaign campaign, int nationId)
    {
        var nation = campaign.NationById(nationId);
        if (nation is null) return null;

        var eligible = EventCatalog.All
            .Where(definition => definition.Weight > 0f && definition.Trigger.Matches(campaign, nation))
            .ToArray();
        if (eligible.Length == 0) return null;

        var total = eligible.Sum(definition => definition.Weight);
        var stream = new SimRandom(campaign.Seed + nationId * 7919 + campaign.ElapsedDays);
        var roll = stream.NextFloat() * total;

        var chosen = eligible[^1];
        foreach (var definition in eligible)
        {
            roll -= definition.Weight;
            if (roll > 0f) continue;
            chosen = definition;
            break;
        }

        return new PendingCampaignEvent
        {
            Id = campaign.ElapsedDays * 1000 + nationId,
            NationId = nationId,
            Title = chosen.Title,
            Description = chosen.Description,
            CreatedDay = campaign.ElapsedDays,
            ExpiresDay = campaign.ElapsedDays + 182,
            Options = chosen.Options
        };
    }
}

public sealed class ResolveCampaignEventCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int EventId { get; init; }
    public int OptionIndex { get; init; }

    public void Apply(Campaign campaign)
    {
        var item = campaign.PendingEvents.FirstOrDefault(candidate => candidate.Id == EventId && candidate.NationId == IssuerId);
        if (item is not null) EventSystem.Resolve(campaign, item, OptionIndex);
    }
}
