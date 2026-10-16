using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// A promise the government has made in public: a verifiable outcome with a deadline.
///
/// The glossary draws a hard line here (CONTEXT.md): a promise is a time-limited obligation
/// to reach a checkable result, and neither keeping nor breaking it rewrites the country's
/// character - that is what the national course is for, and it moves on experience, not on
/// one delivered road. So a promise is deliberately small: the government says a thing in
/// public, the date arrives, the result is measured with the same trigger vocabulary every
/// other condition in the game uses, and the outcome is a public fact with a mood attached.
///
/// What makes it a promise rather than a decision is the deadline. A decision is paid for
/// and done; a promise is cheap to make and expensive to miss, because the audience keeps
/// the date. Fulfilment is checked continuously - the day the outcome is true the promise
/// is kept, early credit included - and past the deadline it is broken, whatever the trend.
/// </summary>
public readonly record struct PromiseDefinition(
    string Id, string Name, string Description, int DurationDays)
{
    /// <summary>What has to be true of the country before the promise can be made.</summary>
    public ScriptedTrigger Trigger { get; init; }

    /// <summary>The outcome, measured against the same country values the trigger reads.</summary>
    public ScriptedTrigger Check { get; init; }

    /// <summary>What keeping it does, written in the same effects decisions use.</summary>
    public ScriptedEffects.Effect Kept { get; init; }

    /// <summary>What missing the deadline does. Nothing here ever edits the national course.</summary>
    public ScriptedEffects.Effect Broken { get; init; }

    /// <summary>Told to the player when the trigger refuses.</summary>
    public string Unavailable { get; init; }
}

public static class PromiseCatalog
{
    private static readonly List<PromiseDefinition> Definitions = new();

    static PromiseCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        // Promises name triggers and effects out of the libraries as they are read.
        ScriptLibrary.EnsureLoaded();
        Definitions.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "promises"))
            using (document)
            foreach (var entry in document.RootElement.EnumerateObject())
                Apply(entry.Name, entry.Value, null);
    }

    /// <summary>
    /// One promise, from the base game's folder or a mod's - read the same way, because
    /// they are the same thing. A promise without a checkable outcome is not a promise;
    /// it is refused rather than quietly becoming a decision with a date.
    /// </summary>
    public static bool Apply(string id, JsonElement value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(id) || value.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"promise '{id}': expected an object.");
            return false;
        }

        string Text(string key) =>
            value.TryGetProperty(key, out var found) && found.ValueKind == JsonValueKind.String
                ? found.GetString()
                : null;

        var duration = value.TryGetProperty("duration_days", out var days) && days.TryGetInt32(out var declared)
            ? declared
            : 365;
        if (duration <= 0)
        {
            errors?.Add($"promise '{id}': duration_days must be positive.");
            return false;
        }

        var trigger = value.TryGetProperty("trigger", out var condition)
            ? ScriptedTrigger.Parse(condition, $"promise '{id}'", errors)
            : null;
        var check = value.TryGetProperty("check", out var outcome)
            ? ScriptedTrigger.Parse(outcome, $"promise '{id}' check", errors)
            : null;
        if (check is null || check.IsEmpty)
        {
            errors?.Add($"promise '{id}': a promise needs a checkable outcome.");
            return false;
        }

        var kept = value.TryGetProperty("effects", out var effects)
            ? ScriptedEffects.Parse(effects, $"promise '{id}'", errors)
            : null;
        var broken = value.TryGetProperty("failure_effects", out var failure)
            ? ScriptedEffects.Parse(failure, $"promise '{id}' failure", errors)
            : null;

        Definitions.RemoveAll(definition =>
            definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        Definitions.Add(new PromiseDefinition(id, Text("name") ?? id, Text("description") ?? "", duration)
        {
            Trigger = trigger is { IsEmpty: false } ? trigger : null,
            Check = check,
            Kept = kept is { IsEmpty: false } ? kept : null,
            Broken = broken is { IsEmpty: false } ? broken : null,
            Unavailable = Text("unavailable") ?? ""
        });
        return true;
    }

    /// <summary>One mod package's promises, layered over what is loaded.</summary>
    public static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
            Apply(entry.Name, entry.Value, errors);
    }

    public static IReadOnlyList<PromiseDefinition> All => Definitions;

    public static PromiseDefinition? Find(string id)
    {
        foreach (var definition in Definitions)
            if (definition.Id.Equals(id, StringComparison.Ordinal)) return definition;
        return null;
    }
}

public sealed class ActivePromise
{
    public string Id = "";
    public int MadeDay;
    public int DueDay;
}

public sealed partial class Nation
{
    /// <summary>Promises this government has made in public and not yet concluded.</summary>
    public List<ActivePromise> Promises = new();
}

public static class GovernmentPromises
{
    public static string BlockReason(Campaign campaign, Nation nation, PromiseDefinition definition)
    {
        if (!campaign.FeatureEnabled("government_promises"))
            return Loc.Get("This mechanic is disabled in this scenario.");
        if (nation.Promises.Any(promise => promise.Id.Equals(definition.Id, StringComparison.Ordinal)))
            return Loc.Get("Already promised to the public");
        if (nation.Promises.Count >= Math.Max(1, campaign.Defines.MaxConcurrentPromises))
            return Loc.Format("The government can only keep {0} promises at once",
                campaign.Defines.MaxConcurrentPromises);
        if (definition.Trigger is { IsEmpty: false } && !definition.Trigger.Matches(campaign, nation))
            return string.IsNullOrWhiteSpace(definition.Unavailable)
                ? Loc.Get("The country is not in a position to do this")
                : definition.Unavailable;
        return "";
    }

    /// <summary>Whether a promise may be made, without saying why not. See DecisionCatalog.IsBlocked.</summary>
    public static bool IsBlocked(Campaign campaign, Nation nation, PromiseDefinition definition) =>
        !campaign.FeatureEnabled("government_promises") ||
        nation.Promises.Any(promise => promise.Id.Equals(definition.Id, StringComparison.Ordinal)) ||
        nation.Promises.Count >= Math.Max(1, campaign.Defines.MaxConcurrentPromises) ||
        (definition.Trigger is { IsEmpty: false } && !definition.Trigger.Matches(campaign, nation));

    /// <summary>
    /// Conclude what has come due. A promise whose outcome is already true is kept the day
    /// it becomes true - early credit is the point of measuring rather than waiting - and
    /// one still false the day after its deadline is broken, whatever the trend looked like.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (campaign is null || !campaign.FeatureEnabled("government_promises")) return;
        for (var index = 0; index < campaign.Nations.Count; index++)
        {
            var nation = campaign.Nations[index];
            if (nation.Promises.Count == 0) continue;
            for (var slot = nation.Promises.Count - 1; slot >= 0; slot--)
            {
                var promise = nation.Promises[slot];
                var definition = PromiseCatalog.Find(promise.Id);
                if (definition is null)
                {
                    // A mod withdrew the promise mid-flight; nothing to measure, nothing to pay.
                    nation.Promises.RemoveAt(slot);
                    continue;
                }
                var check = definition.Value.Check;
                var kept = check is { IsEmpty: false } && check.Matches(campaign, nation);
                if (!kept && campaign.ElapsedDays <= promise.DueDay) continue;

                nation.Promises.RemoveAt(slot);
                if (kept) definition.Value.Kept?.Apply(campaign, nation);
                else definition.Value.Broken?.Apply(campaign, nation);
                // The AI's own record, which `--ai-wars` prints beside its war counters: how
                // many public bets the governments run by the AI actually settled - its own
                // countries, plus any cabinet the player handed over (Delegation).
                if (Delegation.RunByAi(nation))
                {
                    var tally = kept ? "promise_kept" : "promise_broken";
                    AiStatecraft.Tally[tally] = AiStatecraft.Tally.GetValueOrDefault(tally) + 1;
                }
                campaign.Chronicle.Record(campaign, kept ? "promise_kept" : "promise_broken",
                    nation.Id, subject: definition.Value.Name, isPublic: true);
                campaign.MarkStrategicChange();
            }
        }
    }
}

public sealed class MakePromiseCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string PromiseId { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var definition = PromiseCatalog.Find(PromiseId);
        if (nation is null || definition is null ||
            GovernmentPromises.BlockReason(campaign, nation, definition.Value).Length > 0) return;

        nation.Promises.Add(new ActivePromise
        {
            Id = definition.Value.Id,
            MadeDay = campaign.ElapsedDays,
            DueDay = campaign.ElapsedDays + definition.Value.DurationDays
        });
        campaign.Chronicle.Record(campaign, "promise_made", nation.Id,
            subject: definition.Value.Name, isPublic: true);
        campaign.MarkStrategicChange();
    }
}
