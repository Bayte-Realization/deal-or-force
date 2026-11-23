using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed class ActiveNationalAmbition
{
    public string Id = "";
    public int SelectedDay;
    public float BaselineValue;
    public float TargetValue;
    public float Progress;
}

public sealed class ActiveCabinetDirective
{
    public string Id = "";
    public int StartedDay;
    public int UntilDay;
}

public sealed partial class Nation
{
    public List<ActiveNationalAmbition> ActiveAmbitions = new();
    public List<string> CompletedAmbitions = new();
    public int GoldenEraUntilDay = -1;
    public int GoldenEraCount;
    public List<ActiveCabinetDirective> ActiveDirectives = new();
    public Dictionary<string, int> DirectiveCooldownUntilDay = new(StringComparer.OrdinalIgnoreCase);
    public int LastAmbitionAiDay = -1;
}

public sealed class AmbitionDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Metric { get; init; } = "";
    public float TargetDelta { get; init; }
    public float TargetAbsolute { get; init; }
    public int GoldenEraDays { get; init; } = 365;
    public float RewardPp { get; init; } = 25f;
    public float RewardStability { get; init; } = 0.03f;
    public float RewardProductivity { get; init; } = 0.03f;
    public float RewardXp { get; init; } = 10f;
}

public sealed class CabinetDirectiveDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public float TreasuryGdpShare { get; init; } = 0.005f;
    public float PpCost { get; init; } = 15f;
    public int DurationDays { get; init; } = 180;
    public int CooldownDays { get; init; } = 365;
    public float InstantCapacity { get; init; }
    public float InstantStability { get; init; }
    public float InstantWarSupport { get; init; }
    public float InstantCreditConfidence { get; init; }
    public float InstantLiving { get; init; }
    public float InstantXp { get; init; }
    public float NeighborOpinion { get; init; }
    public float WeeklyProductivity { get; init; }
    public float WeeklyStability { get; init; }
    public float WeeklyPp { get; init; }
    public float WeeklyRadicalism { get; init; }
    public float WeeklyXp { get; init; }
}

/// <summary>
/// Data-driven National Ambitions, Golden Era Momentum, and Executive Cabinet Directives (`E4`).
/// Loaded from <c>game/common/ambitions/*.json</c> with flat snake_case IDs and zero Godot dependencies.
/// </summary>
public static class NationalAmbitions
{
    public const int MaxActiveSlots = 2;

    private static readonly Dictionary<string, AmbitionDefinition> AmbitionById =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<AmbitionDefinition> AmbitionList = new();

    private static readonly Dictionary<string, CabinetDirectiveDefinition> DirectiveById =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<CabinetDirectiveDefinition> DirectiveList = new();

    private static readonly object InitLock = new();
    private static volatile bool _loaded;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (InitLock)
        {
            if (_loaded) return;
            ResetCatalog();
        }
    }

    public static IReadOnlyList<AmbitionDefinition> All { get { EnsureLoaded(); return AmbitionList; } }
    public static IReadOnlyList<CabinetDirectiveDefinition> Directives { get { EnsureLoaded(); return DirectiveList; } }

    public static AmbitionDefinition FindAmbition(string id)
    {
        EnsureLoaded();
        return !string.IsNullOrWhiteSpace(id) && AmbitionById.TryGetValue(id.Trim(), out var def) ? def : null;
    }

    public static CabinetDirectiveDefinition FindDirective(string id)
    {
        EnsureLoaded();
        return !string.IsNullOrWhiteSpace(id) && DirectiveById.TryGetValue(id.Trim(), out var def) ? def : null;
    }

    public static void ResetCatalog(List<string> errors = null)
    {
        lock (InitLock)
        {
            _loaded = true;
            AmbitionById.Clear();
            AmbitionList.Clear();
            DirectiveById.Clear();
            DirectiveList.Clear();

            var gameRoot = GameDataLoader.DataFolder();
            if (string.IsNullOrWhiteSpace(gameRoot)) return;
            var folder = Path.Combine(gameRoot, "common", "ambitions");
            if (!Directory.Exists(folder)) return;

            var files = StructuredData.Files(folder, SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                try
                {
                    using var doc = StructuredData.ReadDocument(file);
                    Merge(doc.RootElement, errors);
                }
                catch (Exception ex)
                {
                    errors?.Add($"common/ambitions/{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors?.Add("common/ambitions must be a JSON object keyed by snake_case id.");
            return;
        }
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var id = entry.Name;
            var obj = entry.Value;
            if (GameDataLoader.IsRemoved(obj, $"common/ambitions:{id}"))
            {
                if (AmbitionById.Remove(id))
                    AmbitionList.RemoveAll(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (DirectiveById.Remove(id))
                    DirectiveList.RemoveAll(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                continue;
            }

            var kind = ReadString(obj, "kind", "ambition");
            if (string.Equals(kind, "cabinet_directive", StringComparison.OrdinalIgnoreCase))
            {
                var dir = new CabinetDirectiveDefinition
                {
                    Id = id,
                    Name = ReadString(obj, "name", id),
                    Description = ReadString(obj, "description", ""),
                    TreasuryGdpShare = MathF.Max(0f, ReadFloat(obj, "treasury_gdp_share", 0.005f)),
                    PpCost = MathF.Max(0f, ReadFloat(obj, "pp_cost", 15f)),
                    DurationDays = Math.Clamp(ReadInt(obj, "duration_days", 180), 30, 1460),
                    CooldownDays = Math.Clamp(ReadInt(obj, "cooldown_days", 365), 30, 3650),
                    InstantCapacity = ReadFloat(obj, "instant_capacity", 0f),
                    InstantStability = ReadFloat(obj, "instant_stability", 0f),
                    InstantWarSupport = ReadFloat(obj, "instant_war_support", 0f),
                    InstantCreditConfidence = ReadFloat(obj, "instant_credit_confidence", 0f),
                    InstantLiving = ReadFloat(obj, "instant_living", 0f),
                    InstantXp = ReadFloat(obj, "instant_xp", 0f),
                    NeighborOpinion = ReadFloat(obj, "neighbor_opinion", 0f),
                    WeeklyProductivity = ReadFloat(obj, "weekly_productivity", 0f),
                    WeeklyStability = ReadFloat(obj, "weekly_stability", 0f),
                    WeeklyPp = ReadFloat(obj, "weekly_pp", 0f),
                    WeeklyRadicalism = ReadFloat(obj, "weekly_radicalism", 0f),
                    WeeklyXp = ReadFloat(obj, "weekly_xp", 0f)
                };
                DirectiveList.RemoveAll(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                DirectiveById[id] = dir;
                DirectiveList.Add(dir);
            }
            else
            {
                var amb = new AmbitionDefinition
                {
                    Id = id,
                    Name = ReadString(obj, "name", id),
                    Description = ReadString(obj, "description", ""),
                    Metric = ReadString(obj, "metric", "gdp_growth"),
                    TargetDelta = ReadFloat(obj, "target_delta", 0.15f),
                    TargetAbsolute = ReadFloat(obj, "target_absolute", 0f),
                    GoldenEraDays = Math.Clamp(ReadInt(obj, "golden_era_days", 365), 60, 1825),
                    RewardPp = MathF.Max(0f, ReadFloat(obj, "reward_pp", 25f)),
                    RewardStability = ReadFloat(obj, "reward_stability", 0.03f),
                    RewardProductivity = ReadFloat(obj, "reward_productivity", 0.03f),
                    RewardXp = MathF.Max(0f, ReadFloat(obj, "reward_xp", 10f))
                };
                AmbitionList.RemoveAll(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                AmbitionById[id] = amb;
                AmbitionList.Add(amb);
            }
        }
    }

    public static IReadOnlyList<ActiveNationalAmbition> ActiveFor(Campaign campaign, Nation nation)
    {
        EnsureLoaded();
        if (nation?.ActiveAmbitions is null || nation.ActiveAmbitions.Count == 0)
            return Array.Empty<ActiveNationalAmbition>();
        return nation.ActiveAmbitions;
    }

    public static IReadOnlyList<AmbitionDefinition> AvailableFor(Campaign campaign, Nation nation)
    {
        EnsureLoaded();
        if (nation is null) return Array.Empty<AmbitionDefinition>();
        var list = new List<AmbitionDefinition>(AmbitionList.Count);
        for (var i = 0; i < AmbitionList.Count; i++)
        {
            var def = AmbitionList[i];
            if (HasCompleted(nation, def.Id) || HasActive(nation, def.Id)) continue;
            list.Add(def);
        }
        return list;
    }

    public static bool HasCompleted(Nation nation, string ambitionId)
    {
        if (nation?.CompletedAmbitions is null || string.IsNullOrEmpty(ambitionId)) return false;
        for (var i = 0; i < nation.CompletedAmbitions.Count; i++)
            if (string.Equals(nation.CompletedAmbitions[i], ambitionId, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static bool HasActive(Nation nation, string ambitionId)
    {
        if (nation?.ActiveAmbitions is null || string.IsNullOrEmpty(ambitionId)) return false;
        for (var i = 0; i < nation.ActiveAmbitions.Count; i++)
            if (string.Equals(nation.ActiveAmbitions[i].Id, ambitionId, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static bool InGoldenEra(Campaign campaign, Nation nation) =>
        campaign is not null && nation is not null && nation.GoldenEraUntilDay > campaign.ElapsedDays;

    public static int GoldenEraDaysRemaining(Campaign campaign, Nation nation) =>
        campaign is null || nation is null ? 0 : Math.Max(0, nation.GoldenEraUntilDay - campaign.ElapsedDays);

    public static bool CanSelectAmbition(Campaign campaign, Nation nation, string ambitionId, out string refusal,
        string replaceAmbitionId = "")
    {
        EnsureLoaded();
        refusal = "";
        if (campaign is null || nation is null || nation.Landless || nation.Population <= 0)
        { refusal = Loc.Get("ambitions.err.unavailable"); return false; }
        var def = FindAmbition(ambitionId);
        if (def is null) { refusal = Loc.Get("ambitions.err.unknown"); return false; }
        if (HasCompleted(nation, def.Id)) { refusal = Loc.Get("ambitions.err.completed"); return false; }
        if (HasActive(nation, def.Id)) { refusal = Loc.Get("ambitions.err.active"); return false; }
        var activeCount = nation.ActiveAmbitions?.Count ?? 0;
        if (activeCount >= MaxActiveSlots &&
            (string.IsNullOrWhiteSpace(replaceAmbitionId) || !HasActive(nation, replaceAmbitionId)))
        { refusal = Loc.Format("ambitions.err.slots_full", MaxActiveSlots); return false; }
        return true;
    }

    public static (float Treasury, float PoliticalPoints) DirectiveCost(Nation nation, CabinetDirectiveDefinition directive)
    {
        if (nation is null || directive is null) return (0f, 0f);
        return (MathF.Max(0.05f, MathF.Max(0.1f, nation.Gdp) * directive.TreasuryGdpShare), directive.PpCost);
    }

    public static int DirectiveCooldownRemaining(Campaign campaign, Nation nation, string directiveId) =>
        campaign is null || nation?.DirectiveCooldownUntilDay is null || string.IsNullOrWhiteSpace(directiveId)
            ? 0
            : nation.DirectiveCooldownUntilDay.TryGetValue(directiveId, out var until)
                ? Math.Max(0, until - campaign.ElapsedDays) : 0;

    public static bool IsDirectiveActive(Campaign campaign, Nation nation, string directiveId)
    {
        if (campaign is null || nation?.ActiveDirectives is null || string.IsNullOrWhiteSpace(directiveId)) return false;
        for (var i = 0; i < nation.ActiveDirectives.Count; i++)
            if (string.Equals(nation.ActiveDirectives[i].Id, directiveId, StringComparison.OrdinalIgnoreCase) &&
                nation.ActiveDirectives[i].UntilDay > campaign.ElapsedDays) return true;
        return false;
    }

    public static bool CanExecuteDirective(Campaign campaign, Nation nation, string directiveId, out string refusal)
    {
        EnsureLoaded();
        refusal = "";
        if (campaign is null || nation is null || nation.Landless || nation.Population <= 0)
        { refusal = Loc.Get("ambitions.err.unavailable"); return false; }
        var def = FindDirective(directiveId);
        if (def is null) { refusal = Loc.Get("ambitions.err.unknown_directive"); return false; }
        if (IsDirectiveActive(campaign, nation, def.Id)) { refusal = Loc.Get("ambitions.err.directive_active"); return false; }
        var cd = DirectiveCooldownRemaining(campaign, nation, def.Id);
        if (cd > 0) { refusal = Loc.Format("ambitions.err.directive_cooldown", cd); return false; }
        var (cash, pp) = DirectiveCost(nation, def);
        if (nation.Treasury + 1e-4f < cash) { refusal = Loc.Format("ambitions.err.need_treasury", $"{cash:0.##}"); return false; }
        if (nation.PoliticalPoints + 1e-4f < pp) { refusal = Loc.Format("ambitions.err.need_pp", $"{pp:0}"); return false; }
        return true;
    }

    /// <summary>
    /// Weekly deterministic update called from <see cref="EconomySystem.TickStages"/> for each living nation.
    /// Recomputes live ambition progress, triggers Golden Era rewards on completion, ticks Golden Era
    /// & active Cabinet Directives, and runs staggered AI selection.
    /// </summary>
    public static void TickWeekly(Campaign campaign, Nation nation)
    {
        EnsureLoaded();
        if (campaign is null || nation is null || nation.Landless || nation.Population <= 0)
            return;

        // 1. Tick active Golden Era Momentum.
        if (InGoldenEra(campaign, nation))
        {
            nation.Stability = M.Clamp01(nation.Stability + 0.0018f);
            nation.Productivity = M.Clamp(nation.Productivity + 0.0012f, 0.15f, 4f);
            nation.PoliticalPoints = MathF.Min(500f, nation.PoliticalPoints + 0.60f);
        }

        // 2. Tick & expire active Executive Cabinet Directives.
        if (nation.ActiveDirectives is { Count: > 0 } directives)
        {
            var states = campaign.StatesOf(nation);
            for (var i = directives.Count - 1; i >= 0; i--)
            {
                var active = directives[i];
                if (active.UntilDay <= campaign.ElapsedDays)
                {
                    directives.RemoveAt(i);
                    continue;
                }
                var def = FindDirective(active.Id);
                if (def is null) { directives.RemoveAt(i); continue; }
                if (def.WeeklyProductivity != 0f)
                    nation.Productivity = M.Clamp(nation.Productivity + def.WeeklyProductivity, 0.15f, 4f);
                if (def.WeeklyStability != 0f)
                    nation.Stability = M.Clamp01(nation.Stability + def.WeeklyStability);
                if (def.WeeklyPp != 0f)
                    nation.PoliticalPoints = MathF.Min(500f, MathF.Max(0f, nation.PoliticalPoints + def.WeeklyPp));
                if (def.WeeklyXp != 0f)
                {
                    nation.ArmyExperience = MathF.Min(500f, nation.ArmyExperience + def.WeeklyXp);
                    nation.AirExperience = MathF.Min(500f, nation.AirExperience + def.WeeklyXp * 0.5f);
                    nation.NavalExperience = MathF.Min(500f, nation.NavalExperience + def.WeeklyXp * 0.5f);
                }
                if (def.WeeklyRadicalism != 0f && states.Length > 0)
                {
                    for (var s = 0; s < states.Length; s++)
                        states[s].Radicalism = M.Clamp01(states[s].Radicalism + def.WeeklyRadicalism);
                }
            }
        }

        // 3. Recompute live ambition progress and trigger Golden Era on completion.
        if (nation.ActiveAmbitions is { Count: > 0 } activeAmbitions)
        {
            for (var i = activeAmbitions.Count - 1; i >= 0; i--)
            {
                var slot = activeAmbitions[i];
                var def = FindAmbition(slot.Id);
                if (def is null) { activeAmbitions.RemoveAt(i); continue; }
                slot.Progress = EvaluateProgress(campaign, nation, def, slot);
                if (slot.Progress >= 1f)
                {
                    activeAmbitions.RemoveAt(i);
                    CompleteAmbition(campaign, nation, def);
                }
            }
        }

        // 4. Deterministic staggered AI selection (never touches human nations).
        if (!nation.IsHuman)
            EvaluateAi(campaign, nation);
    }

    internal static (float Baseline, float Target) ComputeBaselineAndTarget(
        Campaign campaign, Nation nation, AmbitionDefinition def)
    {
        var current = MeasureMetric(campaign, nation, def.Metric);
        switch (def.Metric)
        {
            case "gdp_growth":
            case "export_output":
                return (MathF.Max(0.1f, current), MathF.Max(0.1f, current) * (1f + MathF.Max(0.05f, def.TargetDelta)));
            case "debt_discipline":
                return (current, MathF.Min(def.TargetAbsolute > 0f ? def.TargetAbsolute : 0.25f, MathF.Max(0f, current - 0.08f)));
            case "tech_count":
            case "diplomatic_ties":
            case "maritime_reach":
            case "modern_methods":
                return (current, MathF.Max(def.TargetAbsolute, current + MathF.Max(1f, def.TargetDelta)));
            case "living_renewal":
            case "construction_power":
                return (current, MathF.Max(def.TargetAbsolute, current + def.TargetDelta));
            default:
                return (current, def.TargetAbsolute > 0f ? def.TargetAbsolute : 1f);
        }
    }

    internal static float EvaluateProgress(Campaign campaign, Nation nation, AmbitionDefinition def, ActiveNationalAmbition slot)
    {
        var current = MeasureMetric(campaign, nation, def.Metric);
        switch (def.Metric)
        {
            case "gdp_growth":
            {
                var span = MathF.Max(0.01f, slot.TargetValue - slot.BaselineValue);
                return M.Clamp01((current - slot.BaselineValue) / span);
            }
            case "export_output":
            {
                var span = MathF.Max(0.01f, slot.TargetValue - slot.BaselineValue);
                var outputProg = M.Clamp01((current - slot.BaselineValue) / span);
                var surplusBonus = nation.WeeklyTradeBalance > 0f ? 1f : 0.5f;
                return M.Clamp01(outputProg * surplusBonus);
            }
            case "debt_discipline":
            {
                var debtRatio = nation.Gdp > 0.01f ? nation.PublicDebt / nation.Gdp : 1f;
                var debtScore = debtRatio <= slot.TargetValue
                    ? 1f
                    : M.Clamp01(1f - (debtRatio - slot.TargetValue) / MathF.Max(0.15f, slot.BaselineValue - slot.TargetValue + 0.15f));
                var cashScore = nation.Gdp > 0.01f ? M.Clamp01((nation.Treasury / nation.Gdp) / 0.12f) : 0f;
                var credScore = M.Clamp01(nation.CreditConfidence / 0.78f);
                return M.Clamp01(debtScore * 0.50f + cashScore * 0.25f + credScore * 0.25f);
            }
            case "chokepoint_sovereign":
            {
                var exposures = StrategicChokepoints.ReadForNation(campaign, nation);
                var squeezed = 0;
                for (var i = 0; i < exposures.Count; i++)
                    if (exposures[i].Status == ChokepointStatus.Squeezed) squeezed++;
                var openScore = exposures.Count == 0 ? 1f : 1f - squeezed / (float)exposures.Count;
                var accessScore = M.Clamp01(nation.MarketAccess / 0.92f);
                var supplyScore = M.Clamp01((1f - nation.ShortageIndex) / 0.94f);
                return M.Clamp01(openScore * 0.45f + accessScore * 0.30f + supplyScore * 0.25f);
            }
            case "tech_count":
            case "diplomatic_ties":
            case "maritime_reach":
            case "construction_power":
            {
                var span = MathF.Max(0.1f, slot.TargetValue - slot.BaselineValue);
                return M.Clamp01((current - slot.BaselineValue) / span);
            }
            case "post_war_phoenix":
            {
                var states = campaign.StatesOf(nation);
                var avgDamage = 0f;
                for (var i = 0; i < states.Length; i++) avgDamage += states[i].WarDamage;
                if (states.Length > 0) avgDamage /= states.Length;
                var healScore = M.Clamp01(1f - avgDamage / 0.15f);
                var solTarget = MathF.Min(0.88f, MathF.Max(0.60f, slot.BaselineValue + 0.03f));
                var solScore = M.Clamp01(nation.StandardOfLiving / MathF.Max(0.1f, solTarget));
                return M.Clamp01(healScore * 0.55f + solScore * 0.45f);
            }
            case "nuclear_peacemaker":
            {
                if (NuclearPosture.HasActiveArmsControl(campaign, nation)) return 1f;
                var pacts = CountPeacefulPacts(campaign, nation);
                var defconOk = NuclearPosture.Effective(campaign, nation) >= 4 ? 0.50f : 0.15f;
                return M.Clamp01(defconOk + M.Clamp01(pacts / 3f) * 0.50f);
            }
            case "clean_grid_pioneer":
            {
                var cleanMethod = string.Equals(
                    ProductionMethods.ActiveFor(nation, BuildingKind.PowerPlant)?.Id,
                    "pm_power_clean_grid", StringComparison.OrdinalIgnoreCase);
                var cleanPlants = 0;
                if (BuildingCatalog.TryParse("CleanEnergyPlant", out var cleanKind))
                {
                    var states = campaign.StatesOf(nation);
                    for (var i = 0; i < states.Length; i++)
                        cleanPlants += states[i].Buildings.GetValueOrDefault(cleanKind);
                }
                var gridScore = cleanMethod || cleanPlants >= 2 ? 0.70f : M.Clamp01(cleanPlants / 2f) * 0.60f;
                var energySat = nation.Market.TryGetValue(Commodity.Energy, out var q) ? M.Clamp01(q.Satisfaction) : 1f;
                return M.Clamp01(gridScore + M.Clamp01(energySat / 0.95f) * 0.30f);
            }
            case "living_renewal":
            {
                var states = campaign.StatesOf(nation);
                var avgRad = 0f;
                for (var i = 0; i < states.Length; i++) avgRad += states[i].Radicalism;
                if (states.Length > 0) avgRad /= states.Length;
                var span = MathF.Max(0.02f, slot.TargetValue - slot.BaselineValue);
                var solScore = M.Clamp01((nation.StandardOfLiving - slot.BaselineValue) / span);
                var calmScore = avgRad <= 0.16f ? 1f : M.Clamp01(1f - (avgRad - 0.16f) / 0.30f);
                var stabScore = M.Clamp01(nation.Stability / 0.76f);
                return M.Clamp01(solScore * 0.45f + calmScore * 0.25f + stabScore * 0.30f);
            }
            case "modern_methods":
            {
                var methods = nation.ActiveProductionMethods?.Count ?? 0;
                var capScore = M.Clamp01(nation.ConstructionCapacity / MathF.Max(1.5f, slot.BaselineValue + 0.5f));
                return M.Clamp01(M.Clamp01(methods / MathF.Max(2f, def.TargetAbsolute)) * 0.65f + capScore * 0.35f);
            }
            default:
                return M.Clamp01(current / MathF.Max(0.01f, slot.TargetValue));
        }
    }

    private static float MeasureMetric(Campaign campaign, Nation nation, string metric)
    {
        switch (metric)
        {
            case "gdp_growth": return nation.Gdp;
            case "export_output": return nation.GrossOutput;
            case "debt_discipline": return nation.Gdp > 0.01f ? nation.PublicDebt / nation.Gdp : 1f;
            case "tech_count": return nation.Technologies.Count;
            case "diplomatic_ties": return CountPeacefulPacts(campaign, nation);
            case "living_renewal": return nation.StandardOfLiving;
            case "post_war_phoenix": return nation.StandardOfLiving;
            case "construction_power": return nation.ConstructionCapacity;
            case "modern_methods": return nation.ConstructionCapacity;
            case "maritime_reach":
            {
                var states = campaign.StatesOf(nation);
                var total = 0;
                for (var i = 0; i < states.Length; i++)
                    total += states[i].Buildings.GetValueOrDefault(BuildingKind.Port) +
                             states[i].Buildings.GetValueOrDefault(BuildingKind.Shipyard);
                return total;
            }
            default: return nation.Stability;
        }
    }

    private static int CountPeacefulPacts(Campaign campaign, Nation nation)
    {
        var count = 0;
        foreach (var rel in campaign.Relations.Values)
        {
            if (rel.A != nation.Id && rel.B != nation.Id) continue;
            if (rel.Stance == DiplomaticStance.Allied || rel.TradeAgreement || rel.NonAggressionPact)
                count++;
        }
        return count;
    }

    private static void CompleteAmbition(Campaign campaign, Nation nation, AmbitionDefinition def)
    {
        nation.CompletedAmbitions ??= new List<string>();
        if (!HasCompleted(nation, def.Id))
            nation.CompletedAmbitions.Add(def.Id);

        nation.GoldenEraUntilDay = Math.Max(nation.GoldenEraUntilDay, campaign.ElapsedDays) + def.GoldenEraDays;
        nation.GoldenEraCount++;
        nation.PoliticalPoints = MathF.Min(500f, nation.PoliticalPoints + def.RewardPp);
        nation.Stability = M.Clamp01(nation.Stability + def.RewardStability);
        nation.Productivity = M.Clamp(nation.Productivity + def.RewardProductivity, 0.15f, 4f);
        if (def.RewardXp > 0f)
        {
            nation.ArmyExperience = MathF.Min(500f, nation.ArmyExperience + def.RewardXp);
            nation.AirExperience = MathF.Min(500f, nation.AirExperience + def.RewardXp * 0.5f);
            nation.NavalExperience = MathF.Min(500f, nation.NavalExperience + def.RewardXp * 0.5f);
        }
        campaign.Chronicle.Record(campaign, "golden_era_started", nation.Id, subject: def.Name, isPublic: true);
    }

    private static void EvaluateAi(Campaign campaign, Nation nation)
    {
        if (campaign.ElapsedDays < 21) return;
        var week = campaign.ElapsedDays / 7;
        if ((week + nation.Id) % 4 != 0) return;
        if (nation.LastAmbitionAiDay >= 0 && campaign.ElapsedDays - nation.LastAmbitionAiDay < 84) return;

        // 1. Fill empty ambition slots deterministically based on national situation.
        var activeCount = nation.ActiveAmbitions?.Count ?? 0;
        if (activeCount < MaxActiveSlots)
        {
            var available = AvailableFor(campaign, nation);
            AmbitionDefinition pick = null;
            var bestScore = float.NegativeInfinity;
            for (var i = 0; i < available.Count; i++)
            {
                var candidate = available[i];
                var score = ScoreAmbitionForAi(campaign, nation, candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                    pick = candidate;
                }
            }
            if (pick is not null)
            {
                new SelectAmbitionCommand { IssuerId = nation.Id, AmbitionId = pick.Id }.Apply(campaign);
                nation.LastAmbitionAiDay = campaign.ElapsedDays;
                return;
            }
        }

        // 2. Occasionally execute a high-value cabinet directive when flush with PP and cash.
        if (nation.PoliticalPoints >= 35f && nation.Treasury > nation.Gdp * 0.08f &&
            (nation.ActiveDirectives?.Count ?? 0) == 0)
        {
            string preferred = nation.Stability < 0.62f
                ? "directive_national_cohesion"
                : nation.ShortageIndex > 0.08f
                    ? "directive_strategic_reserve"
                    : "directive_industrial_drive";
            if (CanExecuteDirective(campaign, nation, preferred, out _))
            {
                new ExecuteCabinetDirectiveCommand { IssuerId = nation.Id, DirectiveId = preferred }.Apply(campaign);
                nation.LastAmbitionAiDay = campaign.ElapsedDays;
            }
        }
    }

    private static float ScoreAmbitionForAi(Campaign campaign, Nation nation, AmbitionDefinition def)
    {
        var score = 1f;
        switch (def.Metric)
        {
            case "gdp_growth": score += nation.StandardOfLiving < 0.65f ? 0.6f : 0.35f; break;
            case "debt_discipline": score += nation.Gdp > 0.01f && nation.PublicDebt / nation.Gdp > 0.45f ? 0.8f : 0.2f; break;
            case "tech_count": score += nation.Literacy >= 0.88f ? 0.65f : 0.1f; break;
            case "clean_grid_pioneer": score += nation.Literacy >= 0.82f ? 0.55f : 0.05f; break;
            case "living_renewal": score += nation.Stability < 0.72f ? 0.7f : 0.25f; break;
            case "export_output": score += nation.WeeklyTradeBalance >= 0f ? 0.5f : 0.2f; break;
            case "nuclear_peacemaker": score += NuclearPosture.IsHolder(campaign, nation) ? 0.75f : 0.15f; break;
            default: score += 0.3f; break;
        }
        // Small deterministic tie-breaker by nation id + stable cross-process ambition id hash.
        var tie = unchecked((uint)nation.Id * 31u + StableStringHash(def.Id)) % 100u;
        return score + tie * 0.001f;
    }

    private static uint StableStringHash(string value)
    {
        var h = 2166136261u;
        if (!string.IsNullOrEmpty(value))
            for (var i = 0; i < value.Length; i++)
                h = unchecked((h ^ value[i]) * 16777619u);
        return h;
    }

    private static string ReadString(JsonElement obj, string prop, string fallback) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? fallback
            : fallback;

    private static float ReadFloat(JsonElement obj, string prop, float fallback) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number &&
        el.TryGetSingle(out var val) && float.IsFinite(val)
            ? val
            : fallback;

    private static int ReadInt(JsonElement obj, string prop, int fallback) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number &&
        el.TryGetInt32(out var val)
            ? val
            : fallback;
}

/// <summary>Selects a National Ambition into an open slot (or replaces an active one).</summary>
public sealed class SelectAmbitionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string AmbitionId { get; init; } = "";
    public string ReplaceAmbitionId { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        if (campaign is null || !MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (!NationalAmbitions.CanSelectAmbition(campaign, nation, AmbitionId, out _, ReplaceAmbitionId))
            return;

        var def = NationalAmbitions.FindAmbition(AmbitionId);
        if (def is null) return;

        nation!.ActiveAmbitions ??= new List<ActiveNationalAmbition>();
        if (!string.IsNullOrWhiteSpace(ReplaceAmbitionId))
            nation.ActiveAmbitions.RemoveAll(a => string.Equals(a.Id, ReplaceAmbitionId, StringComparison.OrdinalIgnoreCase));

        var (baseline, target) = NationalAmbitions.ComputeBaselineAndTarget(campaign, nation, def);
        var slot = new ActiveNationalAmbition
        {
            Id = def.Id,
            SelectedDay = campaign.ElapsedDays,
            BaselineValue = baseline,
            TargetValue = target,
            Progress = 0f
        };
        slot.Progress = MathF.Min(0.95f, NationalAmbitions.EvaluateProgress(campaign, nation, def, slot));
        nation.ActiveAmbitions.Add(slot);
        campaign.Chronicle.Record(campaign, "national_ambition_selected", nation.Id, subject: def.Name, isPublic: false);
    }
}

/// <summary>Executes a timed Executive Cabinet Directive with real treasury & political-point costs.</summary>
public sealed class ExecuteCabinetDirectiveCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string DirectiveId { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        if (campaign is null || !MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (!NationalAmbitions.CanExecuteDirective(campaign, nation, DirectiveId, out _)) return;

        var def = NationalAmbitions.FindDirective(DirectiveId);
        if (def is null) return;

        var (cash, pp) = NationalAmbitions.DirectiveCost(nation!, def);
        nation!.Treasury = MathF.Max(0f, nation.Treasury - cash);
        nation.PoliticalPoints = MathF.Max(0f, nation.PoliticalPoints - pp);

        if (def.InstantCapacity != 0f)
            nation.ConstructionCapacity = MathF.Max(0.25f, nation.ConstructionCapacity + def.InstantCapacity);
        if (def.InstantStability != 0f)
            nation.Stability = M.Clamp01(nation.Stability + def.InstantStability);
        if (def.InstantWarSupport != 0f)
            nation.WarSupport = M.Clamp01(nation.WarSupport + def.InstantWarSupport);
        if (def.InstantCreditConfidence != 0f)
            nation.CreditConfidence = M.Clamp01(nation.CreditConfidence + def.InstantCreditConfidence);
        if (def.InstantLiving != 0f)
            nation.StandardOfLiving = M.Clamp01(nation.StandardOfLiving + def.InstantLiving);
        if (def.InstantXp != 0f)
        {
            nation.ArmyExperience = MathF.Min(500f, nation.ArmyExperience + def.InstantXp);
            nation.AirExperience = MathF.Min(500f, nation.AirExperience + def.InstantXp * 0.5f);
            nation.NavalExperience = MathF.Min(500f, nation.NavalExperience + def.InstantXp * 0.5f);
        }
        if (def.NeighborOpinion != 0f)
        {
            foreach (var rel in campaign.Relations.Values)
            {
                if (rel.A != nation.Id && rel.B != nation.Id) continue;
                if (rel.Stance == DiplomaticStance.AtWar) continue;
                rel.Opinion = M.Clamp(rel.Opinion + def.NeighborOpinion, -100f, 100f);
            }
        }

        nation.ActiveDirectives ??= new List<ActiveCabinetDirective>();
        nation.ActiveDirectives.Add(new ActiveCabinetDirective
        {
            Id = def.Id,
            StartedDay = campaign.ElapsedDays,
            UntilDay = campaign.ElapsedDays + def.DurationDays
        });
        nation.DirectiveCooldownUntilDay ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        nation.DirectiveCooldownUntilDay[def.Id] = campaign.ElapsedDays + def.CooldownDays;

        campaign.Chronicle.Record(campaign, "cabinet_directive_executed", nation.Id, subject: def.Name, isPublic: false);
    }
}
