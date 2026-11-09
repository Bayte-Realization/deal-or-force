using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>
    /// A country to declare war on when this option is taken, or zero for the usual case.
    ///
    /// Effects are numbers written against the country the event is for, which covers almost
    /// everything an event wants to do and cannot express "and now fight THEM". A secession
    /// is the case that needs it: the choice the player is being given is precisely whether
    /// to start that war, so the war has to be reachable from the option.
    ///
    /// An id rather than a tag because tags are reused as countries come and go, and an int
    /// on this class is carried by the save without anything being taught about it.
    /// </summary>
    public int DeclareWarOn { get; init; }

    /// <summary>
    /// The region the war <see cref="DeclareWarOn"/> starts is declared over, or -1 for the
    /// target's largest - what a declaration without a goal gets. Declared over a region it is
    /// a reconquest when the region is one the country has lost (LostLands), which is a
    /// justified war rather than an unprovoked one.
    /// </summary>
    public int WarGoalStateMapIndex { get; init; } = -1;

    /// <summary>A region this option strikes off the country's homeland - a claim given up for good - or -1.</summary>
    public int RenouncesClaimOn { get; init; } = -1;

    /// <summary>
    /// The same three, as an event file writes them: a tag to declare war on, and region ids
    /// for the war goal and for a claim given up - "declare_war_on", "war_goal" and
    /// "renounce_claim" in common/events. Resolved when the option is taken, because a file
    /// cannot know the ids a campaign hands out.
    /// </summary>
    public string DeclareWarOnTag { get; init; } = "";
    public string WarGoalRegion { get; init; } = "";
    public string RenouncesClaimRegion { get; init; } = "";
    public int UnionFounderId { get; init; }
    public string UnionFormableId { get; init; } = "";
    public int ShardConsolidateFounderId { get; init; } = -1;
    /// <summary>A separate peace put to a player (SeparatePeace.Offer): the war, the member that leaves, the terms.</summary>
    public int SeparatePeaceWarId { get; init; } = -1;
    public int SeparatePeaceMemberId { get; init; } = -1;
    public int SeparatePeaceTermsId { get; init; }
    /// <summary>Saved recipient and explicit choice of the secession response. -1 is no response, so nation zero is valid.</summary>
    public int SecessionTargetId { get; init; } = -1;
    public string SecessionChoice { get; init; } = "";

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

    /// <summary>The picture its window shows, as <see cref="EventPictures"/> names one; "" defers to that table.</summary>
    public string Picture { get; init; } = "";

    /// <summary>
    /// A dated event from `common/historical_events`, or null for an ambient event. Dated
    /// events are not dealt from the ambient pool; <see cref="HistoricalTimeline"/> reads
    /// them on their date and applies them to every matching country.
    /// </summary>
    public HistoricalDate? HistoricalDate { get; init; }

    /// <summary>How many days after the date a missed event may still fire. Zero is that day only.</summary>
    public int WindowDays { get; init; } = 30;

    /// <summary>
    /// Zero means once per country. A positive value repeats the event every that many days
    /// from its anchor date, inside the same window - a yearly congress, not a one-shot.
    /// </summary>
    public int RepeatDays { get; init; }

    /// <summary>Deterministic chance in [0,1] once the date and trigger pass; 1 always fires.</summary>
    public float Chance { get; init; } = 1f;

    /// <summary>Effects applied when the event has no options. Options own their own effects.</summary>
    public ScriptedEffects.Effect Effects { get; init; } = new();

    /// <summary>Whether a successful firing writes a line to the world chronicle.</summary>
    public bool Record { get; init; } = true;

    /// <summary>The chronicle kind a firing writes; defaults to the generic historical line.</summary>
    public string ChronicleKind { get; init; } = "historical_event";

    /// <summary>Option an AI picks when a dated event has a choice; the player gets the card.</summary>
    public int AiOption { get; init; }

    /// <summary>Overrides how many days a dated event waits for the player; zero uses the common rule.</summary>
    public int DecisionDays { get; init; }

    public bool Historical => HistoricalDate.HasValue;

    /// <summary>
    /// Who it can happen to: "countries" (the default), "movements" - a movement with no
    /// ground of its own - or "all". A run on the currency means nothing to a movement that
    /// has no currency, and a split in the leadership nothing to a republic.
    /// </summary>
    public string For { get; init; } = "countries";

    public bool Suits(Nation nation) => For switch
    {
        "all" => true,
        "movements" => nation.Landless,
        _ => !nation.Landless
    };
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
        foreach (var file in GameDataLoader.DataFiles("events"))
        {
            try { Load(StructuredData.ReadJson(file), null); }
            catch { /* One unreadable file costs that file, not the whole category. */ }
        }
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
            if (!value.TryGetProperty("title", out var title) || !value.TryGetProperty("description", out var description))
            {
                errors?.Add($"Event '{entry.Name}' requires title and description.");
                continue;
            }
            var hasOptions = value.TryGetProperty("options", out var optionsElement) &&
                             optionsElement.ValueKind == JsonValueKind.Array;
            var hasEffects = value.TryGetProperty("effects", out var rootEffects);
            if (!hasOptions && !hasEffects)
            {
                errors?.Add($"Event '{entry.Name}' needs options or a root effects block.");
                continue;
            }

            HistoricalDate? historicalDate = null;
            var dateText = value.TryGetProperty("date", out var dateElement) && dateElement.ValueKind == JsonValueKind.String
                ? dateElement.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(dateText))
            {
                if (HistoricalDate.TryParse(dateText, out var parsedDate))
                    historicalDate = parsedDate;
                else
                    errors?.Add($"Event '{entry.Name}': date must be YYYY-MM-DD, or -YYYY-MM-DD for BCE, not '{dateText}'.");
            }
            else if (value.TryGetProperty("year", out var yearElement) && yearElement.TryGetInt32(out var year))
            {
                var month = value.TryGetProperty("month", out var monthElement) && monthElement.TryGetInt32(out var parsedMonth)
                    ? Math.Clamp(parsedMonth, 1, 12) : 1;
                var day = value.TryGetProperty("day", out var dayElement) && dayElement.TryGetInt32(out var parsedDay)
                    ? Math.Clamp(parsedDay, 1, 31) : 1;
                if (HistoricalDate.TryCreate(year, month, day, out var created)) historicalDate = created;
                else errors?.Add($"Event '{entry.Name}': year/month/day is not a civil date.");
            }

            var options = new List<CampaignEventOption>();
            if (hasOptions)
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
                    Modifiers = ParseModifiers(option, context, errors),
                    DeclareWarOnTag = option.TryGetProperty("declare_war_on", out var warOn) ? warOn.GetString() ?? "" : "",
                    WarGoalRegion = option.TryGetProperty("war_goal", out var goal) ? goal.GetString() ?? "" : "",
                    RenouncesClaimRegion = option.TryGetProperty("renounce_claim", out var renounce) ? renounce.GetString() ?? "" : ""
                });
            }

            if (options.Count == 0 && !hasEffects)
            {
                errors?.Add($"Event '{entry.Name}' has no usable options or effects.");
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
                    : 1f,
                Picture = value.TryGetProperty("picture", out var picture) && picture.ValueKind == JsonValueKind.String
                    ? (picture.GetString() ?? "").Trim() : "",
                For = value.TryGetProperty("for", out var audience) && audience.ValueKind == JsonValueKind.String
                    ? (audience.GetString() ?? "countries").Trim().ToLowerInvariant()
                    : "countries",
                HistoricalDate = historicalDate,
                WindowDays = value.TryGetProperty("window_days", out var windowElement) && windowElement.TryGetInt32(out var window)
                    ? Math.Max(0, window) : 30,
                RepeatDays = value.TryGetProperty("repeat_days", out var repeatElement) && repeatElement.TryGetInt32(out var repeat)
                    ? Math.Max(0, repeat) : 0,
                Chance = value.TryGetProperty("chance", out var chanceElement) && chanceElement.TryGetSingle(out var chance)
                    ? Math.Clamp(chance, 0f, 1f) : 1f,
                Effects = hasEffects ? ScriptedEffects.Parse(rootEffects, $"Event '{entry.Name}'", errors) : new ScriptedEffects.Effect(),
                Record = !value.TryGetProperty("record", out var recordElement) || recordElement.ValueKind != JsonValueKind.False,
                ChronicleKind = value.TryGetProperty("chronicle", out var chronicleElement) && chronicleElement.ValueKind == JsonValueKind.String
                    ? chronicleElement.GetString() ?? "historical_event" : "historical_event",
                AiOption = value.TryGetProperty("ai_option", out var aiElement) && aiElement.TryGetInt32(out var aiOption)
                    ? Math.Max(0, aiOption) : 0,
                DecisionDays = value.TryGetProperty("decision_days", out var decisionElement) && decisionElement.TryGetInt32(out var decision)
                    ? Math.Max(1, decision) : 0
            };
            if (definition.For is not ("countries" or "movements" or "all"))
                errors?.Add($"Event '{entry.Name}': \"for\" is \"countries\", \"movements\" or \"all\", not \"{definition.For}\".");
            if (!definition.Historical && definition.Options.Length == 0)
            {
                errors?.Add($"Event '{entry.Name}': an ambient event needs options; only a dated event may be effects-only.");
                continue;
            }
            if (definition.Historical && definition.Options.Length == 0 && definition.Effects.IsEmpty)
            {
                errors?.Add($"Event '{entry.Name}': a dated event needs options or a root effects block.");
                continue;
            }

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
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same daily pass, yielded between its three phases so a table full of
    /// questions does not resolve in one frame.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(EventSystem))) yield break;
        // Nobody is asked anything in observer mode. It is the whole of what the mode is
        // for: a timelapse is a campaign at speed five with nobody at the wheel, and one
        // modal window waiting for an answer ends it.
        if (!campaign.ObserverMode && campaign.ElapsedDays > 0)
        {
            foreach (var nationId in campaign.HumanSeats)
            {
                yield return 0;
                var nation = campaign.NationById(nationId);
                if (nation is null) continue;
                var interval = nation.Landless && campaign.Defines.MovementEventIntervalDays >= 0
                    ? campaign.Defines.MovementEventIntervalDays : campaign.Defines.EventIntervalDays;
                if (interval <= 0 || campaign.ElapsedDays % interval != 0) continue;
                if (campaign.PendingEvents.Any(item => item.NationId == nationId)) continue;
                var created = Create(campaign, nationId);
                if (created is not null) campaign.PendingEvents.Add(created);
            }
        }

        yield return 0;

        // And anything already on the table is decided rather than left waiting, so turning
        // the mode on in the middle of a campaign does not leave a window behind it.
        // A union or a merger is the one question the first answer must not settle: it is
        // "yes". Those are answered the way a government nobody plays would answer them.
        if (campaign.NobodyPlays)
            foreach (var item in campaign.PendingEvents.ToArray())
            {
                yield return 0;
                Resolve(campaign, item, ObserverAnswer(campaign, item));
            }

        foreach (var item in campaign.PendingEvents.Where(item => item.ExpiresDay <= campaign.ElapsedDays).ToArray())
        {
            yield return 0;
            Resolve(campaign, item, 1);
        }
    }

    /// <summary>The option observer mode takes: the first, except for a union or a merger, which the
    /// country takes only if it would have as a government nobody plays, and declines otherwise.</summary>
    private static int ObserverAnswer(Campaign campaign, PendingCampaignEvent item)
    {
        var nation = campaign.NationById(item.NationId);
        var decline = Math.Max(0, item.Options.Length - 1);
        foreach (var option in item.Options)
        {
            if (!string.IsNullOrEmpty(option.UnionFormableId))
            {
                var founder = campaign.NationById(option.UnionFounderId);
                return founder is not null && nation is not null &&
                       PeacefulFormation.Willingness(campaign, founder, nation) >= campaign.Defines.UnionAiAcceptance ? 0 : decline;
            }
            if (option.ShardConsolidateFounderId > 0)
            {
                var founder = campaign.NationById(option.ShardConsolidateFounderId);
                return founder is not null && nation is not null &&
                       Consolidation.ShardWillingness(campaign, founder, nation) >= campaign.Defines.ShardConsolidationAcceptance ? 0 : decline;
            }
        }
        return 0;
    }

    public static void Resolve(Campaign campaign, PendingCampaignEvent item, int optionIndex)
    {
        if (!campaign.PendingEvents.Contains(item) || item.Options.Length == 0) return;
        optionIndex = M.Clamp(optionIndex, 0, item.Options.Length - 1);
        var option = item.Options[optionIndex];
        var nation = campaign.NationById(item.NationId);
        if (option.SecessionTargetId >= 0)
        {
            var rebel = campaign.NationById(option.SecessionTargetId);
            if (rebel is null || !campaign.HasOwnedLand(rebel) || !Reintegration.IsBreakawayOf(rebel, nation))
            { campaign.PendingEvents.Remove(item); campaign.MarkStrategicChange(); return; }
            if (!SecessionResponse.Resolve(campaign, nation, option)) return;
        }
        if (!string.IsNullOrEmpty(option.UnionFormableId) && item.ExpiresDay > campaign.ElapsedDays)
            PeacefulFormation.Accept(campaign, campaign.NationById(option.UnionFounderId), nation,
                FormableNations.Find(option.UnionFormableId));
        if (option.ShardConsolidateFounderId > 0 && item.ExpiresDay > campaign.ElapsedDays && nation is not null)
        {
            var founder = campaign.NationById(option.ShardConsolidateFounderId);
            if (founder is not null)
                Consolidation.ExecuteShardConsolidation(campaign, founder, nation);
        }
        if (option.SeparatePeaceWarId >= 0 && item.ExpiresDay > campaign.ElapsedDays &&
            campaign.Wars.Find(w => w.Id == option.SeparatePeaceWarId) is { Ended: false } separateWar)
            SeparatePeace.Sign(campaign, separateWar, option.SeparatePeaceMemberId, (SeparatePeaceTerms)option.SeparatePeaceTermsId);
        option.Effect.Apply(campaign, nation);
        foreach (var (id, days) in option.Modifiers) ModifierSystem.Grant(campaign, nation, id, days);
        // By the country itself rather than by an id that means "nobody" at zero: nation 0 is
        // Indonesia, and an event file naming IDN has to be able to mean it.
        var declareWarOn = option.DeclareWarOn != 0 ? campaign.NationById(option.DeclareWarOn) : null;
        if (declareWarOn is null && !string.IsNullOrWhiteSpace(option.DeclareWarOnTag))
            declareWarOn = campaign.NationByTag(option.DeclareWarOnTag);
        var warGoal = option.WarGoalStateMapIndex;
        if (warGoal < 0 && !string.IsNullOrWhiteSpace(option.WarGoalRegion))
            warGoal = Movements.StateById(campaign, option.WarGoalRegion)?.MapIndex ?? -1;
        if (declareWarOn is not null && nation is not null)
        {
            // Refused, and unable to do anything about it - a truce, no shared border, no way
            // to reach them - is an acceptance whatever the cabinet decided. Nothing is said
            // here because the war itself is the message.
            var target = declareWarOn;
            if (target.Id != nation.Id)
                CampaignDiplomacy.DeclareWar(campaign, nation.Id, target.Id, warGoal,
                    campaign.States.Find(s => s.MapIndex == warGoal) is { } home &&
                    TerritorialClaims.TitleOf(campaign, nation, home) == TerritorialTitle.Homeland ? "reconquest"
                    // The secession question's own war, as it always was; an event file's war
                    // is justified - or not - the way any declaration is.
                    : option.DeclareWarOn != 0 && warGoal < 0 ? "secession" : null);
        }
        if (option.RenouncesClaimOn >= 0 && nation is not null) LostLands.Renounce(campaign, nation, option.RenouncesClaimOn);
        if (!string.IsNullOrWhiteSpace(option.RenouncesClaimRegion) && nation is not null &&
            Movements.StateById(campaign, option.RenouncesClaimRegion) is { } given)
            LostLands.Renounce(campaign, nation, given.MapIndex);
        campaign.PendingEvents.Remove(item);
        campaign.MarkStrategicChange();
    }

    /// <summary>
    /// Raise one now, for whoever asks. The developer console uses it, and so does anything
    /// else that needs an event to exist rather than to wait for one - a test of the event
    /// window being the obvious case.
    /// </summary>
    public static PendingCampaignEvent Raise(Campaign campaign, int nationId)
    {
        if (campaign is null) return null;
        var created = Create(campaign, nationId);
        if (created is not null) campaign.PendingEvents.Add(created);
        return created;
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
            .Where(definition => !definition.Historical && definition.Weight > 0f && definition.Suits(nation) &&
                                 definition.Trigger.Matches(campaign, nation))
            .ToArray();
        if (eligible.Length == 0) return null;

        // What this country has just seen counts against being dealt it again. Not a ban -
        // a country really can have two bad harvests - but a weight that recovers over the
        // memory's length, so a small pool still feels like a varied one and a large pool
        // is nearly a shuffle. A pool of one still deals that one, because the fallback
        // below takes whatever is left when every weight has been discounted to nothing.
        var memory = M.Max(1, campaign.Defines.EventMemoryDays);
        float WeightOf(CampaignEventDefinition definition)
        {
            if (!nation.EventMemory.TryGetValue(definition.Id, out var lastSeen)) return definition.Weight;
            var since = campaign.ElapsedDays - lastSeen;
            if (since >= memory) return definition.Weight;
            return definition.Weight * M.Clamp01(since / (float)memory) * 0.9f + definition.Weight * 0.02f;
        }

        var total = eligible.Sum(WeightOf);
        var stream = new SimRandom(campaign.Seed + nationId * 7919 + campaign.ElapsedDays);
        var roll = stream.NextFloat() * total;

        var chosen = eligible[^1];
        foreach (var definition in eligible)
        {
            roll -= WeightOf(definition);
            if (roll > 0f) continue;
            chosen = definition;
            break;
        }
        nation.EventMemory[chosen.Id] = campaign.ElapsedDays;

        return new PendingCampaignEvent
        {
            Id = campaign.ElapsedDays * 1000 + nationId,
            NationId = nationId,
            Title = chosen.Title,
            Description = chosen.Description,
            CreatedDay = campaign.ElapsedDays,
            ExpiresDay = campaign.ElapsedDays + campaign.Defines.EventExpiryDays,
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
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var item = campaign.PendingEvents.FirstOrDefault(candidate => candidate.Id == EventId && candidate.NationId == IssuerId);
        if (item is not null) EventSystem.Resolve(campaign, item, OptionIndex);
    }
}
