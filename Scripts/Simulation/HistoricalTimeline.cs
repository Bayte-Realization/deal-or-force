using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Dated history: the layer that lets a scenario say "in 1789, this happened" without
/// hard-coding the date in a tick. Events live in `events/*.json` and reuse the
/// ordinary event options and <see cref="ScriptedEffects"/> vocabulary, so a mod can write
/// an early-modern revolution, a medieval succession or an ancient league with the same
/// file format. The campaign owns the state; this class only reads the date and decides
/// which country has reached which event.
///
/// Firing is per country, not per world: a trigger with `tags` applies to those countries,
/// an empty trigger applies to every country that suits the event. Memory is kept in the
/// existing `Nation.EventMemory` under a `history:` prefix, which means the save needs no
/// new field and an old save simply has no event behind it.
/// </summary>
public static class HistoricalTimeline
{
    private const string MemoryPrefix = "history:";

    public static void Tick(Campaign campaign)
    {
        if (campaign is null || !campaign.FeatureEnabled("events") ||
            !MechanicFeatures.SystemEnabled(campaign, nameof(EventSystem))) return;
        if (campaign.Date.Year < -9999 || campaign.Date.Year > 9999) return;
        var today = new HistoricalDate(campaign.Date.Year, campaign.Date.Month, campaign.Date.Day);
        foreach (var definition in EventCatalog.All)
        {
            if (!definition.Historical || !Due(definition, today)) continue;
            foreach (var nation in campaign.Nations)
            {
                if (!definition.Suits(nation) || !definition.Trigger.Matches(campaign, nation)) continue;
                if (!Ready(campaign, nation, definition)) continue;
                if (definition.Chance < 1f && StableRoll(definition.Id, nation.Id, campaign.ElapsedDays) >= definition.Chance)
                    continue;
                Fire(campaign, nation, definition);
            }
        }
    }

    /// <summary>Whether a dated event is inside its date window today.</summary>
    internal static bool Due(CampaignEventDefinition definition, HistoricalDate today)
    {
        if (definition?.HistoricalDate is not { } date) return false;
        var age = today.DayNumber - date.DayNumber;
        if (age < 0) return false;
        var window = Math.Max(0, definition.WindowDays);
        if (definition.RepeatDays <= 0) return age <= window;
        return age % definition.RepeatDays <= window;
    }

    /// <summary>Whether this country has not already had the event, or may have its next occurrence.</summary>
    internal static bool Ready(Campaign campaign, Nation nation, CampaignEventDefinition definition)
    {
        if (campaign is null || nation is null || definition is null) return false;
        var key = MemoryPrefix + definition.Id;
        if (!nation.EventMemory.TryGetValue(key, out var last)) return true;
        if (definition.RepeatDays <= 0) return false;
        return campaign.ElapsedDays - last >= definition.RepeatDays;
    }

    /// <summary>
    /// Fire one dated event for one country. A choice is a real pending event for a human
    /// and the documented AI option for everybody else; an effect-only event runs directly.
    /// Exposed to tests and to the console, which must be able to prove the same path.
    /// </summary>
    internal static bool Fire(Campaign campaign, Nation nation, CampaignEventDefinition definition)
    {
        if (campaign is null || nation is null || definition is null || !definition.Historical) return false;
        var key = MemoryPrefix + definition.Id;
        nation.EventMemory[key] = campaign.ElapsedDays;
        if (definition.Options.Length > 0)
        {
            var pending = new PendingCampaignEvent
            {
                Id = campaign.PendingEvents.Count == 0 ? 1 : campaign.PendingEvents.Max(item => item.Id) + 1,
                NationId = nation.Id,
                Title = definition.Title,
                Description = definition.Description,
                CreatedDay = campaign.ElapsedDays,
                ExpiresDay = campaign.ElapsedDays + (definition.DecisionDays > 0
                    ? definition.DecisionDays : Math.Max(1, campaign.Defines.EventExpiryDays)),
                Options = definition.Options
            };
            campaign.PendingEvents.Add(pending);
            if (!nation.IsHuman)
                EventSystem.Resolve(campaign, pending,
                    Math.Clamp(definition.AiOption, 0, definition.Options.Length - 1));
        }
        else
        {
            definition.Effects.Apply(campaign, nation);
            campaign.MarkStrategicChange();
        }

        if (definition.Record)
            campaign.Chronicle.Record(campaign, definition.ChronicleKind, nation.Id, -1,
                definition.Title, "", true);
        return true;
    }

    /// <summary>A stable [0,1) roll for content probability: replay and save never change it.</summary>
    private static float StableRoll(string id, int nationId, int day)
    {
        var hash = 2166136261u;
        foreach (var character in id ?? "")
        {
            hash ^= character;
            hash *= 16777619u;
        }
        hash ^= (uint)(nationId + 1) * 2246822519u;
        hash ^= (uint)(day + 1) * 3266489917u;
        hash ^= hash >> 15; hash *= 2246822519u;
        hash ^= hash >> 13; hash *= 3266489917u;
        hash ^= hash >> 16;
        return (hash & 0xFFFFFF) / 16777216f;
    }
}
