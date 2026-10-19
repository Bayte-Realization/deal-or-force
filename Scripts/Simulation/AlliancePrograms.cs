using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// A joint undertaking inside an alliance: what it is called, how long it runs, what it costs the
/// sponsor each week, and how much cohesion finishing it is worth.
///
/// <see cref="OpinionOnCompletion"/> is the reason both sides remember it afterwards: a named entry
/// in common/opinion_modifiers, which appears in the country card beside everything else two
/// countries hold against or for each other. A programme without one is a programme that was paid
/// for and forgotten, which is a legitimate thing for a mod to want and is what setting the key to
/// nothing gets.
/// </summary>
public sealed record AllianceProgramDefinition(string Id, string Name, int DurationDays = 84,
    float WeeklyGdpShare = .00002f, float TrustBudget = .12f, bool Relief = false,
    string OpinionOnCompletion = "");

public static class AllianceProgramCatalog
{
    private static readonly SortedDictionary<string, AllianceProgramDefinition> Entries = new(StringComparer.Ordinal);
    public static IEnumerable<AllianceProgramDefinition> All => Entries.Values;
    static AllianceProgramCatalog() => ResetCatalog();
    public static AllianceProgramDefinition Find(string id) => id is not null ? Entries.GetValueOrDefault(id) : null;
    public static void ResetCatalog()
    {
        Entries.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "alliance_programs"))
            using (document) Merge(document.RootElement, null);
    }
    public static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("alliance_programs: expected an object"); return; }
        foreach (var entry in root.EnumerateObject())
        {
            try
            {
                if (!MechanicFeatures.ValidId(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                    throw new FormatException("invalid programme id/object");
                var current = Find(entry.Name) ?? new(entry.Name, entry.Name);
                var s = entry.Value;
                float Number(string key, float fallback, float min, float max)
                {
                    if (!s.TryGetProperty(key, out var raw)) return fallback;
                    if (!raw.TryGetSingle(out var value) || !float.IsFinite(value) || value < min || value > max)
                        throw new FormatException(key + " is outside its supported range");
                    return value;
                }
                // The reason both sides remember a finished programme, or nothing. Checked here
                // rather than at the moment of remembering, because OpinionSystem.Remember ignores
                // an id it does not know: a typo would mean a programme that quietly leaves no
                // trace at all, and nobody ever reports a thing that does nothing.
                string Opinion()
                {
                    if (!s.TryGetProperty("opinion_on_completion", out var raw)) return current.OpinionOnCompletion;
                    if (raw.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(raw.GetString()))
                        throw new FormatException("opinion_on_completion has to be an opinion modifier id");
                    var id = raw.GetString()!;
                    if (OpinionCatalog.Find(id) is null)
                        throw new FormatException($"opinion_on_completion names '{id}', and there is no such opinion modifier");
                    return id;
                }
                var next = current with {
                    Name = s.TryGetProperty("name", out var name) ? name.GetString() : current.Name,
                    DurationDays = (int)Number("duration_days", current.DurationDays, 7, 730),
                    WeeklyGdpShare = Number("weekly_gdp_share", current.WeeklyGdpShare, .000001f, .01f),
                    TrustBudget = Number("trust_budget", current.TrustBudget, 0, .25f),
                    Relief = s.TryGetProperty("relief", out var relief) ? relief.GetBoolean() : current.Relief,
                    OpinionOnCompletion = Opinion()
                };
                Entries[entry.Name] = next;
            }
            catch (Exception e) { errors?.Add($"alliance_programs/{entry.Name}: {e.Message}"); }
        }
    }
}

/// <summary>A national, unilateral contribution to an existing alliance. Never charges
/// the partner or signs a treaty on a human partner's behalf. Fulfilment earns trust
/// gradually; structural disagreements still determine its sustainable ceiling.</summary>
public static class AlliancePrograms
{
    public static float Cost(Nation sponsor, AllianceProgramDefinition programme) =>
        MathF.Max(.0001f, sponsor.Gdp * programme.WeeklyGdpShare);
    public static bool Active(DiplomaticRelation r) => !string.IsNullOrEmpty(r.AllianceProgram);
    public static bool CanStart(Campaign c, Nation a, Nation b, string id, out string reason)
    {
        reason = "";
        var d = AllianceProgramCatalog.Find(id);
        var r = a is null || b is null || c is null ? null : CampaignDiplomacy.Peek(c, a.Id, b.Id);
        if (d is null || r?.Stance != DiplomaticStance.Allied || a.Id == b.Id)
            reason = "An existing alliance is required.";
        else if (!c.Defines.DynamicAlliances || !MechanicFeatures.SystemEnabled(c, nameof(AllianceMaintenance)))
            reason = "Alliance maintenance is disabled.";
        else if (Active(r)) reason = "This alliance already has a funded programme.";
        else if (c.ElapsedDays - r.AllianceReaffirmedDay < Math.Max(1, c.Defines.AllianceReaffirmDays))
            reason = "The previous commitment is still on cooldown.";
        else if (r.EmbargoByA || r.EmbargoByB || CampaignDiplomacy.AtWar(c, a.Id, b.Id))
            reason = "Resolve sanctions or hostilities before funding joint work.";
        else if (a.Treasury < Cost(a, d)) reason = "The treasury cannot meet the first weekly payment.";
        return reason.Length == 0;
    }
    public static bool Start(Campaign c, Nation a, Nation b, string id)
    {
        if (!CanStart(c, a, b, id, out _)) return false;
        var d = AllianceProgramCatalog.Find(id);
        var r = CampaignDiplomacy.Get(c, a.Id, b.Id);
        r.AllianceProgram = id; r.AllianceProgramSponsor = a.Id;
        r.AllianceProgramStarted = r.AllianceProgramLastPaid = c.ElapsedDays;
        r.AllianceProgramDuration = d.DurationDays; r.AllianceProgramPaidWeeks = 1;
        r.AllianceProgramWeeklyCost = Cost(a, d); r.AllianceProgramTrustBudget = d.TrustBudget;
        r.AllianceProgramRelief = d.Relief; r.AllianceReaffirmedDay = c.ElapsedDays;
        r.AllianceProgramPausedDay = -1;
        Pay(c, r, a); // Money now, trust only after a week of keeping the promise.
        c.Chronicle.Record(c, "alliance_program_started", a.Id, b.Id, d.Name);
        c.MarkStrategicChange();
        return true;
    }
    public static void Tick(Campaign c)
    {
        // Sort only funded programmes, never the entire remembered-pair archive.
        // Preserve key order: several contributions may draw on one treasury.
        List<KeyValuePair<long, DiplomaticRelation>> active = null;
        foreach (var pair in c.Relations)
            if (Active(pair.Value)) (active ??= new()).Add(pair);
        if (active is null) return;
        active.Sort((left, right) => left.Key.CompareTo(right.Key));
        foreach (var pair in active)
        {
            var r = pair.Value;
            if (!Active(r)) continue;
            var changed = false;
            if (r.AllianceProgramPausedDay >= 0)
            {
                var dormant = Math.Max(0, c.ElapsedDays - r.AllianceProgramPausedDay);
                r.AllianceProgramStarted += dormant; r.AllianceProgramLastPaid += dormant;
                r.AllianceProgramPausedDay = -1;
                changed = true;
            }
            var sponsor = c.NationById(r.AllianceProgramSponsor);
            if (r.Stance != DiplomaticStance.Allied || sponsor is null) { Clear(r); c.MarkStrategicChange(); continue; }
            while (c.ElapsedDays - r.AllianceProgramLastPaid >= 7)
            {
                changed = true;
                // Never erase the strain clock with a payment. Buying goodwill cannot
                // sustain an alliance whose underlying target is hostile.
                var ceiling = AllianceMaintenance.Target(c, r);
                var weeks = Math.Max(1, (r.AllianceProgramDuration + 6) / 7);
                var earned = r.AllianceProgramTrustBudget / weeks;
                if (!(r.EmbargoByA || r.EmbargoByB))
                    r.AllianceCohesion = MathF.Min(1f, r.AllianceCohesion + MathF.Min(earned, MathF.Max(0, ceiling - r.AllianceCohesion)));
                r.AllianceProgramLastPaid += 7;
                if (r.AllianceProgramLastPaid - r.AllianceProgramStarted >= r.AllianceProgramDuration)
                {
                    var finished = AllianceProgramCatalog.Find(r.AllianceProgram);
                    c.Chronicle.Record(c, "alliance_program_completed", sponsor.Id, r.Other(sponsor.Id), finished?.Name ?? r.AllianceProgram);
                    // Both sides remember it, and it is one memory for the pair rather than one
                    // each, because a joint undertaking is a thing that happened to the two of them.
                    // This is the trace of a programme that finished, as opposed to the cohesion
                    // that a programme that lapsed takes away.
                    if (finished is { OpinionOnCompletion.Length: > 0 } remembered)
                        OpinionSystem.Remember(c, r, remembered.OpinionOnCompletion);
                    Clear(r); break;
                }
                if (r.EmbargoByA || r.EmbargoByB || sponsor.Treasury < r.AllianceProgramWeeklyCost)
                { Fail(c, r, sponsor); break; }
                Pay(c, r, sponsor); r.AllianceProgramPaidWeeks++;
            }
            if (changed) c.MarkStrategicChange();
        }
    }
    public static void Pause(Campaign c)
    {
        foreach (var r in c.Relations.Values)
            if (Active(r) && r.AllianceProgramPausedDay < 0) r.AllianceProgramPausedDay = c.ElapsedDays;
    }
    private static void Pay(Campaign c, DiplomaticRelation r, Nation sponsor)
    {
        sponsor.Treasury -= r.AllianceProgramWeeklyCost;
        if (r.AllianceProgramRelief && c.NationById(r.Other(sponsor.Id)) is { } recipient)
            recipient.Treasury += r.AllianceProgramWeeklyCost;
    }
    private static void Fail(Campaign c, DiplomaticRelation r, Nation sponsor)
    {
        r.AllianceCohesion = MathF.Max(0f, r.AllianceCohesion - MathF.Min(.08f, r.AllianceProgramTrustBudget * .5f));
        c.Chronicle.Record(c, "alliance_program_lapsed", sponsor.Id, r.Other(sponsor.Id), AllianceProgramCatalog.Find(r.AllianceProgram)?.Name ?? r.AllianceProgram);
        Clear(r); c.MarkStrategicChange();
    }
    public static bool Cancel(Campaign c, Nation a, Nation b)
    {
        if (c is null || a is null || b is null) return false;
        var r = CampaignDiplomacy.Peek(c, a.Id, b.Id);
        if (r is null || !Active(r) || r.AllianceProgramSponsor != a.Id) return false;
        Fail(c, r, a); return true;
    }
    public static void Clear(DiplomaticRelation r)
    {
        r.AllianceProgram = ""; r.AllianceProgramSponsor = -1;
        r.AllianceProgramPausedDay = -1;
    }
}
