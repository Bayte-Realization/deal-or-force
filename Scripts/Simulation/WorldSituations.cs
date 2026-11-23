using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public enum SituationStance { Neutral = 0, Stabilizer = 1, Exploiter = 2 }

public enum SituationOutcome
{
    Active = 0,
    StabilizedConsensus = 1,
    FracturedMultipolarity = 2,
    CrisisEscalation = 3
}

public enum SituationActionKind
{
    ConveneEmergencySummit = 0,
    FundStabilizationPackage = 1,
    ExploitStrategicLeverage = 2,
    BrokerCompromise = 3
}

/// <summary>Data-driven global crisis or geopolitical summit loaded from <c>common/situations/*.json</c>.</summary>
public sealed class WorldSituationDefinition
{
    public string Id = "";
    public string NameKey = "";
    public string SummaryKey = "";
    public string Category = "economy";
    public string TriggerKind = "chokepoint_shock";
    public float TriggerThreshold = 1f;
    public float BaseWeeklyDrift = 1.8f;
    public float StartingIntensity = 34f;
    public int MinStartDay = 30;
    public int CooldownDays = 540;
    public int DurationDays = 240;
    public float StabilizeCostGdpShare = 0.0025f;
    public float StabilizeMinCostBillions = 0.4f;
    public float StabilizeCostPp = 10f;
    public float ExploitCostPp = 8f;
    public float SummitCostPp = 14f;
    public float CompromiseCostPp = 12f;
    public ScriptedEffects.Effect ConsensusEffects = new();
    public ScriptedEffects.Effect MultipolarEffects = new();
    public ScriptedEffects.Effect EscalationEffects = new();
    public string ConsensusChronicleKey = "world_situation_resolved_consensus";
    public string MultipolarChronicleKey = "world_situation_resolved_multipolar";
    public string EscalationChronicleKey = "world_situation_resolved_escalation";
}

/// <summary>One active or archived global situation in a <see cref="Campaign"/>.</summary>
public sealed class WorldSituationInstance
{
    public int Id;
    public string DefinitionId = "";
    public int StartedDay;
    public int LastUpdateDay;
    public int ResolvedDay = -1;
    public float Intensity = 34f;
    public float StabilizerWeight;
    public float ExploiterWeight;
    public bool SummitConvened;
    public int SummitHostId = -1;
    public SituationOutcome Outcome = SituationOutcome.Active;
    public Dictionary<int, SituationStance> ParticipantStances = new();
    public Dictionary<int, int> LastActionDayByNation = new();

    public WorldSituationInstance Copy() => new()
    {
        Id = Id,
        DefinitionId = DefinitionId ?? "",
        StartedDay = StartedDay,
        LastUpdateDay = LastUpdateDay,
        ResolvedDay = ResolvedDay,
        Intensity = Intensity,
        StabilizerWeight = StabilizerWeight,
        ExploiterWeight = ExploiterWeight,
        SummitConvened = SummitConvened,
        SummitHostId = SummitHostId,
        Outcome = Outcome,
        ParticipantStances = ParticipantStances is not null
            ? new Dictionary<int, SituationStance>(ParticipantStances)
            : new Dictionary<int, SituationStance>(),
        LastActionDayByNation = LastActionDayByNation is not null
            ? new Dictionary<int, int>(LastActionDayByNation)
            : new Dictionary<int, int>()
    };
}

/// <summary>Read-only presentation brief for UI panels (e.g. Opportunities / Crisis Summit cards).</summary>
public sealed record WorldSituationBrief(
    int Id,
    string DefinitionId,
    string Title,
    string Summary,
    string Category,
    float Intensity,
    SituationOutcome Outcome,
    int StabilizerCount,
    int ExploiterCount,
    float StabilizerWeight,
    float ExploiterWeight,
    bool SummitConvened,
    string SummitHostTag,
    int DaysRemaining,
    SituationStance ViewerStance,
    bool CanIntervene,
    string RefusalReason);

public sealed partial class Defines
{
    public bool WorldSituationsEnabled { get; set; } = true;
    public bool WorldSituationsAiEnabled { get; set; } = true;
    public int WorldSituationsMaxConcurrent { get; set; } = 3;
    public int WorldSituationsActionCooldownDays { get; set; } = 45;
}

public sealed partial class Campaign
{
    /// <summary>Active and recent resolved Global Situations &amp; Emergency Summits.</summary>
    public readonly List<WorldSituationInstance> WorldSituations = new();
    public int NextSituationId = 1;
}

/// <summary>Catalog of global situations loaded from <c>game/common/situations/*.json</c> (mod-overlay safe).</summary>
public static class WorldSituationCatalog
{
    private static readonly SortedDictionary<string, WorldSituationDefinition> ById = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static IReadOnlyList<WorldSituationDefinition> All { get { Ensure(); return ById.Values.ToArray(); } }

    public static WorldSituationDefinition Find(string id)
    {
        Ensure();
        return !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id.Trim(), out var def) ? def : null;
    }

    public static void ResetCatalog()
    {
        ById.Clear();
        _loaded = true;
        foreach (var doc in GameDataLoader.DataDocuments("common", "situations"))
        using (doc)
            Merge(doc.RootElement, null);
    }

    private static void Ensure()
    {
        if (!_loaded) ResetCatalog();
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (!_loaded) { _loaded = true; }
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (GameDataLoader.IsRemoved(entry.Value, $"common/situations:{entry.Name}"))
            {
                ById.Remove(entry.Name);
                continue;
            }
            if (!ById.TryGetValue(entry.Name, out var def))
                ById[entry.Name] = def = new WorldSituationDefinition { Id = entry.Name };

            var obj = entry.Value;
            def.NameKey = ReadStr(obj, "name", def.NameKey.Length > 0 ? def.NameKey : entry.Name);
            def.SummaryKey = ReadStr(obj, "summary", def.SummaryKey);
            def.Category = ReadStr(obj, "category", def.Category);
            def.TriggerKind = ReadStr(obj, "trigger_kind", def.TriggerKind);
            def.TriggerThreshold = ReadFloat(obj, "trigger_threshold", def.TriggerThreshold);
            def.BaseWeeklyDrift = ReadFloat(obj, "base_weekly_drift", def.BaseWeeklyDrift);
            def.StartingIntensity = M.Clamp(ReadFloat(obj, "starting_intensity", def.StartingIntensity), 5f, 90f);
            def.MinStartDay = Math.Max(0, ReadInt(obj, "min_start_day", def.MinStartDay));
            def.CooldownDays = Math.Max(30, ReadInt(obj, "cooldown_days", def.CooldownDays));
            def.DurationDays = Math.Max(30, ReadInt(obj, "duration_days", def.DurationDays));
            def.StabilizeCostGdpShare = MathF.Max(0f, ReadFloat(obj, "stabilize_cost_gdp_share", def.StabilizeCostGdpShare));
            def.StabilizeMinCostBillions = MathF.Max(0f, ReadFloat(obj, "stabilize_min_cost", def.StabilizeMinCostBillions));
            def.StabilizeCostPp = MathF.Max(0f, ReadFloat(obj, "stabilize_cost_pp", def.StabilizeCostPp));
            def.ExploitCostPp = MathF.Max(0f, ReadFloat(obj, "exploit_cost_pp", def.ExploitCostPp));
            def.SummitCostPp = MathF.Max(0f, ReadFloat(obj, "summit_cost_pp", def.SummitCostPp));
            def.CompromiseCostPp = MathF.Max(0f, ReadFloat(obj, "compromise_cost_pp", def.CompromiseCostPp));
            def.ConsensusChronicleKey = ReadStr(obj, "consensus_chronicle", def.ConsensusChronicleKey);
            def.MultipolarChronicleKey = ReadStr(obj, "multipolar_chronicle", def.MultipolarChronicleKey);
            def.EscalationChronicleKey = ReadStr(obj, "escalation_chronicle", def.EscalationChronicleKey);

            if (obj.TryGetProperty("consensus_effects", out var ce) && ce.ValueKind == JsonValueKind.Object)
                def.ConsensusEffects = ScriptedEffects.Parse(ce, $"situations:{entry.Name}.consensus_effects", errors);
            if (obj.TryGetProperty("multipolar_effects", out var me) && me.ValueKind == JsonValueKind.Object)
                def.MultipolarEffects = ScriptedEffects.Parse(me, $"situations:{entry.Name}.multipolar_effects", errors);
            if (obj.TryGetProperty("escalation_effects", out var ee) && ee.ValueKind == JsonValueKind.Object)
                def.EscalationEffects = ScriptedEffects.Parse(ee, $"situations:{entry.Name}.escalation_effects", errors);
        }
    }

    private static string ReadStr(JsonElement obj, string key, string fallback) =>
        obj.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? (e.GetString() ?? fallback) : fallback;

    private static float ReadFloat(JsonElement obj, string key, float fallback) =>
        obj.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetSingle(out var v) && float.IsFinite(v) ? v : fallback;

    private static int ReadInt(JsonElement obj, string key, int fallback) =>
        obj.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : fallback;
}

/// <summary>
/// Data-driven Global Situations &amp; Emergency Summits engine (Wave 6A / E2).
/// Organically spawns up to 3 concurrent world crises from live economic, diplomatic,
/// military, and social telemetry; lets players and staggered AI intervene via
/// <see cref="SituationInterventionCommand"/>; and resolves into multi-outcome world shifts.
/// </summary>
public static class WorldSituations
{
    public const string FeatureId = "world_situations";
    private const int MaxArchivedResolved = 24;

    public static bool IsEnabled(Campaign c)
    {
        if (c is null || !c.Defines.WorldSituationsEnabled) return false;
        if (c.FeatureOverrides.TryGetValue(FeatureId, out var over) && !over) return false;
        if (c.Defines.Features is not null && c.Defines.Features.TryGetValue(FeatureId, out var feat) && !feat &&
            !c.FeatureOverrides.ContainsKey(FeatureId)) return false;
        return true;
    }

    /// <summary>All currently active global situations, ordered deterministically by Id.</summary>
    public static IReadOnlyList<WorldSituationInstance> ActiveSituations(Campaign c)
    {
        if (c is null || c.WorldSituations.Count == 0) return Array.Empty<WorldSituationInstance>();
        var list = new List<WorldSituationInstance>(4);
        for (var i = 0; i < c.WorldSituations.Count; i++)
            if (c.WorldSituations[i].Outcome == SituationOutcome.Active)
                list.Add(c.WorldSituations[i]);
        return list;
    }

    /// <summary>Most recently resolved global situations for history/opportunities UI.</summary>
    public static IReadOnlyList<WorldSituationInstance> RecentResolved(Campaign c, int maxCount = 5)
    {
        if (c is null || c.WorldSituations.Count == 0 || maxCount <= 0) return Array.Empty<WorldSituationInstance>();
        return c.WorldSituations
            .Where(s => s.Outcome != SituationOutcome.Active)
            .OrderByDescending(s => s.ResolvedDay)
            .ThenByDescending(s => s.Id)
            .Take(maxCount)
            .ToArray();
    }

    /// <summary>Read-only localized snapshot of one situation for UI cards (never mutates Campaign).</summary>
    public static WorldSituationBrief Describe(Campaign c, WorldSituationInstance inst, Nation viewer = null)
    {
        if (inst is null) return null;
        var def = WorldSituationCatalog.Find(inst.DefinitionId);
        var title = Loc.Get(def?.NameKey ?? inst.DefinitionId);
        var summary = Loc.Get(def?.SummaryKey ?? "");
        var category = def?.Category ?? "economy";
        var stabCount = 0;
        var expCount = 0;
        foreach (var stance in inst.ParticipantStances.Values)
        {
            if (stance == SituationStance.Stabilizer) stabCount++;
            else if (stance == SituationStance.Exploiter) expCount++;
        }
        var hostTag = inst.SummitHostId >= 0 ? (c?.NationById(inst.SummitHostId)?.Tag ?? "") : "";
        var duration = def?.DurationDays ?? 240;
        var remaining = inst.Outcome == SituationOutcome.Active && c is not null
            ? Math.Max(0, duration - (c.ElapsedDays - inst.StartedDay))
            : 0;
        var viewerStance = viewer is not null && inst.ParticipantStances.TryGetValue(viewer.Id, out var vs)
            ? vs : SituationStance.Neutral;
        var reason = "";
        var canAct = viewer is not null && c is not null &&
            CanIntervene(c, viewer, inst.Id, SituationActionKind.BrokerCompromise, out reason);

        return new WorldSituationBrief(
            inst.Id, inst.DefinitionId, title, summary, category,
            M.Clamp(inst.Intensity, 0f, 100f), inst.Outcome,
            stabCount, expCount, inst.StabilizerWeight, inst.ExploiterWeight,
            inst.SummitConvened, hostTag, remaining, viewerStance, canAct, reason);
    }

    public static void ActionCost(
        Campaign c, Nation n, WorldSituationInstance inst, SituationActionKind action,
        out float treasuryCost, out float ppCost)
    {
        treasuryCost = 0f;
        ppCost = 0f;
        if (n is null || inst is null) return;
        var def = WorldSituationCatalog.Find(inst.DefinitionId);
        var gdp = MathF.Max(1f, n.Gdp);
        switch (action)
        {
            case SituationActionKind.FundStabilizationPackage:
                treasuryCost = MathF.Max(def?.StabilizeMinCostBillions ?? 0.4f, gdp * (def?.StabilizeCostGdpShare ?? 0.0025f));
                ppCost = def?.StabilizeCostPp ?? 10f;
                break;
            case SituationActionKind.ConveneEmergencySummit:
                treasuryCost = MathF.Max(0.2f, gdp * 0.001f);
                ppCost = def?.SummitCostPp ?? 14f;
                break;
            case SituationActionKind.ExploitStrategicLeverage:
                ppCost = def?.ExploitCostPp ?? 8f;
                break;
            case SituationActionKind.BrokerCompromise:
                ppCost = def?.CompromiseCostPp ?? 12f;
                break;
        }
    }

    public static bool CanIntervene(Campaign c, Nation n, int situationId, SituationActionKind action, out string reason)
    {
        reason = "";
        if (!IsEnabled(c)) { reason = "world_situation.refuse.disabled"; return false; }
        if (n is null || n.Population <= 0 || n.Movement is not null) { reason = "world_situation.refuse.invalid_nation"; return false; }
        var inst = FindById(c, situationId);
        if (inst is null || inst.Outcome != SituationOutcome.Active) { reason = "world_situation.refuse.not_active"; return false; }
        if (action == SituationActionKind.ConveneEmergencySummit && inst.SummitConvened)
        {
            reason = "world_situation.refuse.summit_already_convened";
            return false;
        }
        var cd = Math.Max(7, c.Defines.WorldSituationsActionCooldownDays);
        if (inst.LastActionDayByNation.TryGetValue(n.Id, out var lastDay) && c.ElapsedDays - lastDay < cd)
        {
            reason = "world_situation.refuse.cooldown";
            return false;
        }
        ActionCost(c, n, inst, action, out var cashCost, out var ppCost);
        if (n.PoliticalPoints + 0.001f < ppCost) { reason = "world_situation.refuse.low_pp"; return false; }
        if (cashCost > 0f && n.Treasury + 0.001f < cashCost) { reason = "world_situation.refuse.low_treasury"; return false; }
        return true;
    }

    /// <summary>Execute one player or AI diplomatic/economic intervention in an active Global Situation.</summary>
    public static bool Intervene(Campaign c, Nation n, int situationId, SituationActionKind action)
    {
        if (!CanIntervene(c, n, situationId, action, out _)) return false;
        var inst = FindById(c, situationId);
        if (inst is null) return false;
        var def = WorldSituationCatalog.Find(inst.DefinitionId);

        ActionCost(c, n, inst, action, out var cashCost, out var ppCost);
        n.PoliticalPoints = MathF.Max(0f, n.PoliticalPoints - ppCost);
        if (cashCost > 0f)
        {
            n.Treasury -= cashCost;
            MoneyLedger.Book(n, MoneyFlow.EventsAndDecisions, -cashCost);
        }

        // Nation's geopolitical weight scales smoothly with log-GDP so both mid-powers and superpowers matter.
        var powerWeight = M.Clamp(4f + MathF.Log10(MathF.Max(10f, n.Gdp)) * 3.5f, 5f, 22f);
        inst.LastActionDayByNation[n.Id] = c.ElapsedDays;
        inst.LastUpdateDay = c.ElapsedDays;

        switch (action)
        {
            case SituationActionKind.ConveneEmergencySummit:
                inst.SummitConvened = true;
                inst.SummitHostId = n.Id;
                inst.ParticipantStances[n.Id] = SituationStance.Stabilizer;
                inst.StabilizerWeight += powerWeight * 1.25f;
                inst.Intensity = M.Clamp(inst.Intensity - 11f, 0f, 100f);
                n.Notoriety = MathF.Max(0f, n.Notoriety - 2f);
                c.Chronicle.Record(c, "world_situation_summit_convened", n.Id, -1, Loc.Get(def?.NameKey ?? inst.DefinitionId));
                break;

            case SituationActionKind.FundStabilizationPackage:
                inst.ParticipantStances[n.Id] = SituationStance.Stabilizer;
                inst.StabilizerWeight += powerWeight;
                inst.Intensity = M.Clamp(inst.Intensity - (inst.SummitConvened ? 12f : 9f), 0f, 100f);
                n.Stability = M.Clamp(n.Stability + 0.01f, 0.10f, 0.98f);
                break;

            case SituationActionKind.ExploitStrategicLeverage:
                inst.ParticipantStances[n.Id] = SituationStance.Exploiter;
                inst.ExploiterWeight += powerWeight;
                inst.Intensity = M.Clamp(inst.Intensity + 10f, 0f, 100f);
                var windfall = MathF.Max(0.25f, n.Gdp * 0.002f);
                n.Treasury += windfall;
                MoneyLedger.Book(n, MoneyFlow.EventsAndDecisions, windfall);
                n.Notoriety = M.Clamp(n.Notoriety + 2.5f, 0f, 100f);
                break;

            case SituationActionKind.BrokerCompromise:
                inst.ParticipantStances[n.Id] = SituationStance.Stabilizer;
                inst.StabilizerWeight += powerWeight * 0.85f;
                inst.ExploiterWeight = MathF.Max(0f, inst.ExploiterWeight - powerWeight * 0.35f);
                inst.Intensity = M.Clamp(inst.Intensity - (inst.SummitConvened ? 9f : 6f), 0f, 100f);
                n.CreditConfidence = M.Clamp(n.CreditConfidence + 0.015f, 0f, 1f);
                break;
        }

        // Immediate resolution check if intervention drove intensity to an extreme threshold.
        if (inst.Intensity <= 5f)
            Resolve(c, inst, def, SituationOutcome.StabilizedConsensus);
        else if (inst.Intensity >= 95f)
            Resolve(c, inst, def, SituationOutcome.CrisisEscalation);

        return true;
    }

    /// <summary>Weekly simulation tick called from <see cref="Campaign.TickStages"/>.</summary>
    public static void Tick(Campaign c)
    {
        if (!IsEnabled(c) || c.ElapsedDays <= 0 || c.ElapsedDays % 7 != 0) return;

        // 1. Advance active situations, run staggered AI interventions, and resolve completed crises.
        for (var i = 0; i < c.WorldSituations.Count; i++)
        {
            var inst = c.WorldSituations[i];
            if (inst.Outcome != SituationOutcome.Active) continue;
            var def = WorldSituationCatalog.Find(inst.DefinitionId);
            if (def is null) continue;

            if (c.Defines.WorldSituationsAiEnabled)
                RunStaggeredAi(c, inst, def);
            if (inst.Outcome != SituationOutcome.Active) continue;

            var coalitionDelta = (inst.ExploiterWeight - inst.StabilizerWeight) * 0.06f;
            var summitDamping = inst.SummitConvened ? -1.1f : 0f;
            inst.Intensity = M.Clamp(inst.Intensity + def.BaseWeeklyDrift + coalitionDelta + summitDamping, 0f, 100f);
            inst.LastUpdateDay = c.ElapsedDays;

            if (inst.Intensity <= 5f)
                Resolve(c, inst, def, SituationOutcome.StabilizedConsensus);
            else if (inst.Intensity >= 95f)
                Resolve(c, inst, def, SituationOutcome.CrisisEscalation);
            else if (c.ElapsedDays - inst.StartedDay >= def.DurationDays)
            {
                var outcome = inst.StabilizerWeight >= inst.ExploiterWeight + 12f
                    ? SituationOutcome.StabilizedConsensus
                    : inst.ExploiterWeight >= inst.StabilizerWeight + 12f
                        ? SituationOutcome.CrisisEscalation
                        : SituationOutcome.FracturedMultipolarity;
                Resolve(c, inst, def, outcome);
            }
        }

        // 2. Check organic world telemetry for launching a new global situation (respecting concurrency cap).
        TryTriggerOrganic(c);
    }

    /// <summary>
    /// Evaluate real world telemetry and activate the highest-pressure eligible situation (or a specific
    /// <paramref name="forcedDefinitionId"/> when requested by tests/scripts).
    /// </summary>
    public static WorldSituationInstance TryTriggerOrganic(Campaign c, string forcedDefinitionId = null, bool bypassMinDay = false)
    {
        if (!IsEnabled(c)) return null;
        var cap = Math.Max(1, c.Defines.WorldSituationsMaxConcurrent);
        if (CountActive(c) >= cap) return null;

        var defs = WorldSituationCatalog.All;
        if (defs.Count == 0) return null;

        WorldSituationDefinition chosen = null;
        var bestScore = 0f;

        for (var i = 0; i < defs.Count; i++)
        {
            var def = defs[i];
            if (!string.IsNullOrEmpty(forcedDefinitionId) &&
                !string.Equals(def.Id, forcedDefinitionId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!bypassMinDay && c.ElapsedDays < def.MinStartDay) continue;
            if (IsActiveOrOnCooldown(c, def)) continue;

            var score = EvaluateTelemetryScore(c, def.TriggerKind);
            if (!string.IsNullOrEmpty(forcedDefinitionId) && score < def.TriggerThreshold) score = def.TriggerThreshold;
            if (score >= def.TriggerThreshold && score > bestScore)
            {
                bestScore = score;
                chosen = def;
            }
        }

        if (chosen is null) return null;
        var inst = new WorldSituationInstance
        {
            Id = c.NextSituationId++,
            DefinitionId = chosen.Id,
            StartedDay = c.ElapsedDays,
            LastUpdateDay = c.ElapsedDays,
            Intensity = chosen.StartingIntensity,
            Outcome = SituationOutcome.Active
        };
        c.WorldSituations.Add(inst);
        var leadId = ResolveLeadNationId(c);
        if (leadId >= 0) c.Chronicle.Record(c, "world_situation_started", leadId, -1, Loc.Get(chosen.NameKey));
        return inst;
    }

    private static float EvaluateTelemetryScore(Campaign c, string triggerKind)
    {
        var living = c.ActiveNations;
        if (living.Count == 0) return 0f;

        switch ((triggerKind ?? "").Trim().ToLowerInvariant())
        {
            case "chokepoint_shock":
            {
                var squeezed = 0;
                for (var i = 0; i < living.Count; i++)
                {
                    if (living[i].Movement is not null) continue;
                    var exp = StrategicChokepoints.ReadForNation(c, living[i]);
                    for (var e = 0; e < exp.Count; e++)
                        if (exp[e].Status == ChokepointStatus.Squeezed) { squeezed++; break; }
                }
                return squeezed;
            }
            case "debt_contagion":
            {
                var stressed = 0;
                for (var i = 0; i < living.Count; i++)
                {
                    var n = living[i];
                    if (n.Movement is not null || n.Gdp <= 0.1f) continue;
                    if (n.DebtArrears > 0.05f || n.PublicDebt / n.Gdp >= 1.15f || n.CreditConfidence < 0.45f)
                        stressed++;
                }
                return stressed;
            }
            case "supply_race":
            {
                var exposed = 0;
                for (var i = 0; i < living.Count; i++)
                {
                    var n = living[i];
                    if (n.Movement is not null) continue;
                    var exp = StrategicChokepoints.ReadForNation(c, n);
                    for (var e = 0; e < exp.Count; e++)
                        if (exp[e].Commodity is Commodity.Electronics or Commodity.RareEarths or Commodity.Uranium)
                        { exposed++; break; }
                }
                return exposed + (c.GrandProjects.Count(p => p.Status == GrandProjectStatus.Active) > 0 ? 2 : 0);
            }
            case "arms_spiral":
            {
                var openWars = Warfare.OpenWars(c).Count(w => !w.Ended);
                var defconBump = Math.Max(0, 5 - DefconSystem.CurrentLevel(c)) * 2;
                return openWars + defconBump;
            }
            case "autonomy_wave":
            {
                var restless = 0;
                for (var i = 0; i < c.States.Count; i++)
                    if (c.States[i].Radicalism >= 0.48f) restless++;
                return restless;
            }
            case "reconstruction_charter":
            {
                var damaged = 0;
                for (var i = 0; i < c.States.Count; i++)
                    if (c.States[i].WarDamage >= 0.25f) damaged++;
                return damaged;
            }
            default:
                return 1f;
        }
    }

    private static void RunStaggeredAi(Campaign c, WorldSituationInstance inst, WorldSituationDefinition def)
    {
        // Pick up to 2 eligible AI major powers per week using deterministic seeded hash (zero allocations).
        var living = c.ActiveNations;
        var acted = 0;
        var weekBucket = c.ElapsedDays / 7;
        var playerId = c.Player?.Id ?? -1;
        for (var i = 0; i < living.Count && acted < 2; i++)
        {
            var n = living[i];
            if (n.Id == playerId || n.Movement is not null || n.Population <= 0) continue;
            if (n.Gdp < 120f || n.PoliticalPoints < 8f) continue;

            // Deterministic stagger so each major power reviews once every ~5 weeks.
            var hash = unchecked((uint)(c.Seed * 397 ^ n.Id * 7919 ^ inst.Id * 104729 ^ weekBucket * 31));
            if (hash % 5 != 0) continue;

            SituationActionKind pick;
            if (!inst.SummitConvened && n.Stability >= 0.62f && n.Notoriety < 20f && n.Gdp >= 500f &&
                CanIntervene(c, n, inst.Id, SituationActionKind.ConveneEmergencySummit, out _))
            {
                pick = SituationActionKind.ConveneEmergencySummit;
            }
            else if (n.Notoriety >= 20f || (n.Stability < 0.50f && (hash & 8) != 0))
            {
                pick = SituationActionKind.ExploitStrategicLeverage;
            }
            else if (n.Treasury > MathF.Max(2f, n.Gdp * 0.01f) &&
                     CanIntervene(c, n, inst.Id, SituationActionKind.FundStabilizationPackage, out _))
            {
                pick = SituationActionKind.FundStabilizationPackage;
            }
            else
            {
                pick = SituationActionKind.BrokerCompromise;
            }

            if (!CanIntervene(c, n, inst.Id, pick, out _) &&
                CanIntervene(c, n, inst.Id, SituationActionKind.ExploitStrategicLeverage, out _))
                pick = SituationActionKind.ExploitStrategicLeverage;

            if (Intervene(c, n, inst.Id, pick)) acted++;
        }
    }

    private static void Resolve(Campaign c, WorldSituationInstance inst, WorldSituationDefinition def, SituationOutcome outcome)
    {
        if (inst.Outcome != SituationOutcome.Active) return;
        inst.Outcome = outcome;
        inst.ResolvedDay = c.ElapsedDays;
        inst.LastUpdateDay = c.ElapsedDays;

        if (def is not null)
        {
            var effects = outcome switch
            {
                SituationOutcome.StabilizedConsensus => def.ConsensusEffects,
                SituationOutcome.CrisisEscalation => def.EscalationEffects,
                _ => def.MultipolarEffects
            };
            if (effects is not null && !effects.IsEmpty)
            {
                var recipients = inst.ParticipantStances.Count > 0
                    ? inst.ParticipantStances.Keys.OrderBy(id => id).Select(c.NationById).Where(n => n is not null && n.Population > 0)
                    : c.ActiveNations.Where(n => n.Movement is null).OrderByDescending(n => n.Gdp).Take(10);
                foreach (var nation in recipients)
                    effects.Apply(c, nation);
            }

            var chronicleKey = outcome switch
            {
                SituationOutcome.StabilizedConsensus => def.ConsensusChronicleKey,
                SituationOutcome.CrisisEscalation => def.EscalationChronicleKey,
                _ => def.MultipolarChronicleKey
            };
            var actorId = inst.SummitHostId >= 0 ? inst.SummitHostId : ResolveLeadNationId(c);
            if (actorId >= 0) c.Chronicle.Record(c, chronicleKey, actorId, -1, Loc.Get(def.NameKey));
        }

        TrimResolvedHistory(c);
    }

    private static int ResolveLeadNationId(Campaign c)
    {
        if (c is null) return -1;
        var player = c.Player;
        if (player is not null && player.Population > 0) return player.Id;
        Nation best = null;
        var living = c.ActiveNations;
        for (var i = 0; i < living.Count; i++)
        {
            var n = living[i];
            if (n.Movement is not null || n.Population <= 0) continue;
            if (best is null || n.Gdp > best.Gdp) best = n;
        }
        return best?.Id ?? -1;
    }

    private static void TrimResolvedHistory(Campaign c)
    {
        var resolvedCount = 0;
        for (var i = 0; i < c.WorldSituations.Count; i++)
            if (c.WorldSituations[i].Outcome != SituationOutcome.Active) resolvedCount++;
        if (resolvedCount <= MaxArchivedResolved) return;

        var excess = resolvedCount - MaxArchivedResolved;
        for (var i = 0; i < c.WorldSituations.Count && excess > 0; i++)
        {
            if (c.WorldSituations[i].Outcome != SituationOutcome.Active)
            {
                c.WorldSituations.RemoveAt(i);
                i--;
                excess--;
            }
        }
    }

    private static WorldSituationInstance FindById(Campaign c, int id)
    {
        if (c is null) return null;
        for (var i = 0; i < c.WorldSituations.Count; i++)
            if (c.WorldSituations[i].Id == id) return c.WorldSituations[i];
        return null;
    }

    private static int CountActive(Campaign c)
    {
        var count = 0;
        for (var i = 0; i < c.WorldSituations.Count; i++)
            if (c.WorldSituations[i].Outcome == SituationOutcome.Active) count++;
        return count;
    }

    private static bool IsActiveOrOnCooldown(Campaign c, WorldSituationDefinition def)
    {
        for (var i = 0; i < c.WorldSituations.Count; i++)
        {
            var s = c.WorldSituations[i];
            if (!string.Equals(s.DefinitionId, def.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Outcome == SituationOutcome.Active) return true;
            if (s.ResolvedDay >= 0 && c.ElapsedDays - s.ResolvedDay < def.CooldownDays) return true;
        }
        return false;
    }

    public static List<WorldSituationInstance> CaptureSave(Campaign campaign)
    {
        if (campaign is null || campaign.WorldSituations.Count == 0) return new List<WorldSituationInstance>();
        var list = new List<WorldSituationInstance>(campaign.WorldSituations.Count);
        for (var i = 0; i < campaign.WorldSituations.Count; i++)
            list.Add(campaign.WorldSituations[i].Copy());
        return list;
    }

    public static void RestoreSave(Campaign campaign, List<WorldSituationInstance> items, int nextId)
    {
        if (campaign is null) return;
        campaign.WorldSituations.Clear();
        var maxId = 0;
        if (items is not null)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item is null || string.IsNullOrWhiteSpace(item.DefinitionId)) continue;
                campaign.WorldSituations.Add(item.Copy());
                if (item.Id > maxId) maxId = item.Id;
            }
        }
        campaign.NextSituationId = Math.Max(Math.Max(1, nextId), maxId + 1);
    }
}

/// <summary>Lockstep command for player or scripted intervention in a live <see cref="WorldSituations"/> crisis.</summary>
public sealed class SituationInterventionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int SituationId { get; init; }
    public SituationActionKind Action { get; init; } = SituationActionKind.BrokerCompromise;
    public bool Changed { get; private set; }

    public void Apply(Campaign campaign)
    {
        Changed = false;
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        Changed = WorldSituations.Intervene(campaign, nation, SituationId, Action);
        if (!Changed)
        {
            WorldSituations.CanIntervene(campaign, nation, SituationId, Action, out var reason);
            DiagnosticLogs.Write("orders", $"WORLD_SITUATION_REFUSED nation={IssuerId} situation={SituationId} action={Action} reason={reason}");
        }
    }
}
