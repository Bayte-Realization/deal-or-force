using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// A goal the player has pinned for themselves, with named milestones that can each be
/// measured.
///
/// The ideas backlog calls for "personal strategic goals" - what the player wants out of a
/// campaign, said in the game's own vocabulary and shown as progress rather than a wish.
/// Where a government promise is public (an audience keeps the date, trust moves), a goal
/// is private: nothing happens to the country when it is pinned, nothing happens when it is
/// dropped, and completing it is news only to the person who set it. So there is no
/// deadline, no spirit, no failure state - there is a list of milestones, each one a named
/// condition written in the same trigger vocabulary every other condition in the game uses,
/// and the interface shows which of them stand and which are met.
/// </summary>
public sealed record GoalMilestone(string Text, ScriptedTrigger Trigger);

public sealed record GoalDefinition(string Id, string Name, string Description)
{
    /// <summary>The named steps, in the order the player is meant to read them.</summary>
    public IReadOnlyList<GoalMilestone> Milestones { get; init; } = Array.Empty<GoalMilestone>();
}

public sealed partial class Defines
{
    /// <summary>
    /// How many goals a player may keep open at once. A goal is private bookkeeping, but
    /// three is the most anyone can honestly keep in mind; a mod that wants a quest log
    /// raises the number here rather than in code.
    /// </summary>
    public int MaxActiveGoals { get; set; } = 3;
}

public static class GoalCatalog
{
    private static readonly List<GoalDefinition> Definitions = new();

    static GoalCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        ScriptLibrary.EnsureLoaded();
        Definitions.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "goals"))
            using (document)
            foreach (var entry in document.RootElement.EnumerateObject())
                Apply(entry.Name, entry.Value, null);
    }

    /// <summary>
    /// One goal, from the base game's folder or a mod's - read the same way, because they
    /// are the same thing. A goal with no measurable milestone is refused rather than
    /// quietly becoming a wish, and each milestone needs its own condition: a step the
    /// interface cannot measure is a step the interface cannot show.
    /// </summary>
    public static bool Apply(string id, JsonElement value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(id) || value.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"goal '{id}': expected an object.");
            return false;
        }

        var milestones = new List<GoalMilestone>();
        if (value.TryGetProperty("milestones", out var declared) && declared.ValueKind == JsonValueKind.Array)
        {
            var index = -1;
            foreach (var milestone in declared.EnumerateArray())
            {
                index++;
                if (milestone.ValueKind != JsonValueKind.Object)
                {
                    errors?.Add($"goal '{id}' milestone {index}: expected an object.");
                    return false;
                }
                var text = milestone.TryGetProperty("text", out var step) && step.ValueKind == JsonValueKind.String
                    ? step.GetString() ?? ""
                    : "";
                var trigger = milestone.TryGetProperty("trigger", out var condition)
                    ? ScriptedTrigger.Parse(condition, $"goal '{id}' milestone {index}", errors)
                    : null;
                if (text.Length == 0 || trigger is null || trigger.IsEmpty)
                {
                    errors?.Add($"goal '{id}' milestone {index}: needs text and a checkable condition.");
                    return false;
                }
                milestones.Add(new GoalMilestone(text, trigger));
            }
        }
        if (milestones.Count == 0)
        {
            errors?.Add($"goal '{id}': a goal needs at least one measurable milestone.");
            return false;
        }

        string Text(string key) =>
            value.TryGetProperty(key, out var found) && found.ValueKind == JsonValueKind.String
                ? found.GetString()
                : null;

        Definitions.RemoveAll(definition => definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        Definitions.Add(new GoalDefinition(id, Text("name") ?? id, Text("description") ?? "")
        {
            Milestones = milestones
        });
        return true;
    }

    /// <summary>One mod package's goals, layered over what is loaded.</summary>
    public static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
            Apply(entry.Name, entry.Value, errors);
    }

    public static IReadOnlyList<GoalDefinition> All => Definitions;

    public static GoalDefinition Find(string id)
    {
        foreach (var definition in Definitions)
            if (definition.Id.Equals(id, StringComparison.Ordinal)) return definition;
        return null;
    }
}

public sealed class ActiveGoal
{
    public string Id = "";
    public int AdoptedDay;
    public int CompletedDay = -1;
}

public sealed partial class Nation
{
    /// <summary>Goals this player has pinned, completed ones kept as a record.</summary>
    public List<ActiveGoal> Goals = new();
}

public static class StrategicGoals
{
    public static string BlockReason(Campaign campaign, Nation nation, GoalDefinition definition)
    {
        if (!campaign.FeatureEnabled("strategic_goals"))
            return Loc.Get("This mechanic is disabled in this scenario.");
        if (nation.Goals.Any(goal => goal.Id.Equals(definition.Id, StringComparison.Ordinal)))
            return Loc.Get("Already pinned");
        if (nation.Goals.Count(goal => goal.CompletedDay < 0) >= Math.Max(1, campaign.Defines.MaxActiveGoals))
            return Loc.Format("Only {0} goals may be pinned at once", campaign.Defines.MaxActiveGoals);
        return "";
    }

    /// <summary>
    /// Measure every open goal. A goal whose milestones all stand is completed the day the
    /// last of them became true; completed goals stay on the list as a record until there
    /// are too many to be a record, and nothing in the world changes when one lands.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same daily check, yielded between countries.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null || !campaign.FeatureEnabled("strategic_goals")) yield break;
        for (var index = 0; index < campaign.Nations.Count; index++)
        {
            if (index % 16 == 0) yield return 0;
            var nation = campaign.Nations[index];
            if (nation.Goals.Count == 0) continue;
            for (var slot = nation.Goals.Count - 1; slot >= 0; slot--)
            {
                var goal = nation.Goals[slot];
                if (goal.CompletedDay >= 0) continue;
                var definition = GoalCatalog.Find(goal.Id);
                if (definition is null) continue;
                var met = 0;
                foreach (var milestone in definition.Milestones)
                    if (milestone.Trigger.Matches(campaign, nation)) met++;
                if (met < definition.Milestones.Count) continue;
                goal.CompletedDay = campaign.ElapsedDays;
                // Private news: no public chronicle entry, no mood, no modifier. The record
                // is the point, and the interface is who reads it.
                campaign.MarkStrategicChange();
            }
            var completed = nation.Goals.Where(goal => goal.CompletedDay >= 0)
                .OrderByDescending(goal => goal.CompletedDay).ToList();
            if (completed.Count <= 8) continue;
            foreach (var old in completed.Skip(8).ToList()) nation.Goals.Remove(old);
        }
    }

    /// <summary>Which milestones of this goal stand right now, in the goal's own order.</summary>
    public static List<bool> Progress(Campaign campaign, Nation nation, GoalDefinition definition)
    {
        var progress = new List<bool>();
        foreach (var milestone in definition.Milestones)
            progress.Add(milestone.Trigger.Matches(campaign, nation));
        return progress;
    }
}

public sealed class AdoptGoalCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string GoalId { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var definition = GoalCatalog.Find(GoalId);
        if (nation is null || definition is null ||
            StrategicGoals.BlockReason(campaign, nation, definition).Length > 0) return;
        nation.Goals.Add(new ActiveGoal { Id = definition.Id, AdoptedDay = campaign.ElapsedDays });
        campaign.MarkStrategicChange();
    }
}

public sealed class AbandonGoalCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string GoalId { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        var before = nation.Goals.Count;
        // Dropping a goal costs nothing, because a goal was never owed to anybody.
        nation.Goals.RemoveAll(goal => goal.Id.Equals(GoalId, StringComparison.Ordinal) && goal.CompletedDay < 0);
        if (nation.Goals.Count != before) campaign.MarkStrategicChange();
    }
}
