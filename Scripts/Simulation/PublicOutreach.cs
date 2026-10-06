using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public int PublicOutreachMaxActive { get; set; } = 3;
}

public sealed class OutreachDefinition
{
    public string Id = "";
    public string Name = "";
    public int DurationDays = 90;
    public int CooldownDays = 180;
    public float GdpCostShare = .00002f;
    public float NationImpulse = .18f;
    public float CultureImpulse;
}

public sealed class PublicOutreach
{
    public string ActionId = "";
    public string TargetTag = "";
    public int StartedDay;
    public int LastReviewDay;
    public int DurationDays;
    public int ProgressDays;
    public int CooldownDays;
    public int CooldownUntilDay;
    public float PaidCost;
    public float NationImpulse;
    public float CultureImpulse;
    public bool Cancelled;
    public int PausedDay = -1;
    public bool Active => !Cancelled && ProgressDays < DurationDays;
}

public sealed partial class Nation
{
    public Dictionary<string, PublicOutreach> Outreach = new(StringComparer.Ordinal);
}

public static class OutreachCatalog
{
    private static readonly Dictionary<string, OutreachDefinition> Entries = new(StringComparer.Ordinal);
    public static IEnumerable<OutreachDefinition> All => Entries.Values.OrderBy(d => d.Id, StringComparer.Ordinal);
    static OutreachCatalog() => ResetCatalog();
    public static void ResetCatalog()
    {
        Entries.Clear();
        foreach (var doc in GameDataLoader.DataDocuments("common", "public_outreach"))
            using (doc) Merge(doc.RootElement);
    }
    public static OutreachDefinition Find(string id) => id is not null ? Entries.GetValueOrDefault(id) : null;
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("public_outreach: expected an object"); return; }
        foreach (var entry in root.EnumerateObject())
        {
            if (!MechanicFeatures.ValidId(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
            { errors?.Add("public_outreach: invalid action " + entry.Name); continue; }
            var action = Find(entry.Name) ?? new OutreachDefinition { Id = entry.Name, Name = entry.Name };
            float Number(string key, float current, float min, float max)
            {
                if (!entry.Value.TryGetProperty(key, out var raw)) return current;
                if (raw.ValueKind == JsonValueKind.Number && raw.TryGetSingle(out var value) && float.IsFinite(value))
                    return M.Clamp(value, min, max);
                errors?.Add($"public_outreach: {entry.Name}.{key} must be a finite number"); return current;
            }
            action.DurationDays = (int)Number("duration_days", action.DurationDays, 7, 3650);
            action.CooldownDays = (int)Number("cooldown_days", action.CooldownDays, 1, 3650);
            action.GdpCostShare = Number("gdp_cost_share", action.GdpCostShare, .000001f, .01f);
            action.NationImpulse = Number("nation_impulse", action.NationImpulse, 0, .4f);
            action.CultureImpulse = Number("culture_impulse", action.CultureImpulse, 0, .4f);
            if (entry.Value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                action.Name = name.GetString();
            Entries[action.Id] = action;
        }
    }
}

public static class PublicOutreachActions
{
    public static void Suspend(Campaign c)
    {
        foreach (var actor in c.Nations) foreach (var programme in actor.Outreach.Values)
            if (programme.PausedDay < 0) programme.PausedDay = c.ElapsedDays;
    }
    public static void Resume(Campaign c)
    {
        foreach (var actor in c.Nations) foreach (var programme in actor.Outreach.Values)
        {
            if (programme.PausedDay < 0) continue;
            var dormant = Math.Max(0, c.ElapsedDays - programme.PausedDay);
            programme.LastReviewDay += dormant; programme.CooldownUntilDay += dormant;
            programme.PausedDay = -1;
        }
    }
    public static void Tick(Campaign c)
    {
        if (!SocialMemory.ImagesEnabled(c)) return;
        foreach (var actor in c.Nations)
        {
            if (actor.Outreach.Count == 0) continue;
            foreach (var programme in actor.Outreach.Values)
            {
                if (!programme.Active) continue;
                var target = c.NationByTag(programme.TargetTag);
                var relation = target is null ? null : CampaignDiplomacy.Peek(c, actor.Id, target.Id);
                if (target is null || target.Population <= 0 || actor.Population <= 0 ||
                    CampaignDiplomacy.AtWar(c, actor.Id, target.Id) || relation?.EmbargoBy(actor.Id) == true || relation?.EmbargoBy(target.Id) == true)
                {
                    programme.Cancelled = true;
                    programme.CooldownUntilDay = Math.Max(programme.CooldownUntilDay, c.ElapsedDays + programme.CooldownDays);
                    continue; // no refund of already committed funding; no phantom success
                }
                var elapsed = c.ElapsedDays - programme.LastReviewDay;
                if (elapsed <= 0 || elapsed < Math.Max(1, c.Defines.SocialReviewDays) &&
                    programme.ProgressDays + elapsed < programme.DurationDays) continue;
                var days = Math.Min(elapsed, programme.DurationDays - programme.ProgressDays);
                var share = days / (float)Math.Max(1, programme.DurationDays);
                var name = OutreachCatalog.Find(programme.ActionId)?.Name ?? programme.ActionId;
                SocialMemory.AddIncident(SocialMemory.Image(target, SocialMemory.NationKey(actor), true),
                    programme.NationImpulse * share, name, c.ElapsedDays);
                if (programme.CultureImpulse > 0f)
                {
                    void CulturalContact(Nation audience, Nation subject)
                    {
                        var cultures = DemographyCatalog.PrimaryCultures(subject.Tag);
                        foreach (var culture in cultures)
                            SocialMemory.AddIncident(SocialMemory.Image(audience, "culture:" + culture, true),
                                programme.CultureImpulse * share / Math.Max(1, cultures.Count), name, c.ElapsedDays);
                    }
                    CulturalContact(actor, target); CulturalContact(target, actor);
                }
                programme.ProgressDays += days; programme.LastReviewDay = c.ElapsedDays;
            }
            foreach (var key in actor.Outreach.Where(p => !p.Value.Active && c.ElapsedDays >= p.Value.CooldownUntilDay)
                         .Select(p => p.Key).ToArray()) actor.Outreach.Remove(key);
        }
    }
    // One programme per audience, across all action types: changing the button is not a cooldown bypass.
    public static PublicOutreach Existing(Nation actor, Nation target) => actor is not null && target is not null
        ? actor.Outreach.GetValueOrDefault(target.Tag) : null;
    public static float Cost(Nation actor, OutreachDefinition action) => actor is null || action is null ? 0f :
        MathF.Max(.000001f, actor.Gdp * action.GdpCostShare);
    public static string Blocked(Campaign c, Nation actor, Nation target, OutreachDefinition action)
    {
        if (!SocialMemory.ImagesEnabled(c)) return "Public reputation is disabled.";
        if (c.Defines.FeatureCommands.TryGetValue(nameof(StartPublicOutreachCommand), out var gate) &&
            !string.IsNullOrWhiteSpace(gate) && !c.FeatureEnabled(gate)) return "Public outreach is disabled.";
        if (actor is null || target is null || actor == target || action is null ||
            actor.Population <= 0 || target.Population <= 0) return "No valid audience.";
        if (CampaignDiplomacy.AtWar(c, actor.Id, target.Id)) return "War closes these public channels.";
        var relation = CampaignDiplomacy.Peek(c, actor.Id, target.Id);
        if (relation?.EmbargoBy(actor.Id) == true || relation?.EmbargoBy(target.Id) == true)
            return "An embargo closes these public channels.";
        var existing = Existing(actor, target);
        if (existing?.Active == true) return "A programme for this audience is already active.";
        if (existing is not null && c.ElapsedDays < existing.CooldownUntilDay) return "This audience needs time before another programme.";
        if (actor.Outreach.Values.Count(p => p.Active) >= Math.Max(1, c.Defines.PublicOutreachMaxActive))
            return "The public programme capacity is exhausted.";
        var cost = Cost(actor, action);
        if (!float.IsFinite(cost) || !float.IsFinite(actor.Treasury) || actor.Treasury < cost)
            return "The treasury cannot afford this programme.";
        return "";
    }
    public static bool Start(Campaign c, Nation actor, Nation target, string actionId)
    {
        var action = OutreachCatalog.Find(actionId);
        if (Blocked(c, actor, target, action).Length > 0) return false;
        var cost = Cost(actor, action);
        actor.Treasury -= cost;
        actor.Outreach[target.Tag] = new PublicOutreach
        {
            ActionId = action.Id, TargetTag = target.Tag, StartedDay = c.ElapsedDays, LastReviewDay = c.ElapsedDays,
            DurationDays = action.DurationDays, CooldownDays = action.CooldownDays,
            CooldownUntilDay = c.ElapsedDays + action.DurationDays + action.CooldownDays,
            PaidCost = cost, NationImpulse = action.NationImpulse, CultureImpulse = action.CultureImpulse
        };
        // No opinion, esteem, status or instant bonus here. The paid work must actually run.
        c.MarkStrategicChange();
        return true;
    }
}

public sealed class StartPublicOutreachCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public string ActionId { get; init; } = "";
    public bool Started { get; private set; }
    public void Apply(Campaign c)
    {
        if (MechanicFeatures.Allows(c, this))
            Started = PublicOutreachActions.Start(c, c.NationById(IssuerId), c.NationById(TargetId), ActionId);
    }
}
