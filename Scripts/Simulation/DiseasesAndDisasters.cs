using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DealOrForce.Simulation;

public enum CrisisSeverityTier
{
    Minor = 1,
    Moderate = 2,
    Severe = 3,
    Critical = 4
}

public enum HealthResponseAction
{
    None = 0,
    TargetedQuarantine = 1,
    MassVaccinationAndMedicalDrive = 2,
    EmergencyHealthSubsidies = 3,
    InternationalMedicalCooperation = 4
}

public enum DisasterReliefAction
{
    None = 0,
    EmergencyEvacuationAndRescue = 1,
    InfrastructureReconstruction = 2,
    StrategicReserveAid = 3,
    CivilDefenseMobilization = 4
}

public sealed partial class Defines
{
    public bool DiseasesAndDisastersEnabled { get; set; } = true;
    public bool DiseasesAndDisastersAiEnabled { get; set; } = true;
    public int MaxActiveDiseasesPerNation { get; set; } = 2;
    public int MaxActiveDisastersPerNation { get; set; } = 2;
    public int HealthDisasterResponseCooldownDays { get; set; } = 21;
    public float DiseaseFrequencyMultiplier { get; set; } = 1.0f;
    public float DisasterFrequencyMultiplier { get; set; } = 1.0f;
    public float DiseaseSpreadMultiplier { get; set; } = 1.0f;
}

public sealed partial class Nation
{
    public List<HealthCrisisInstance> ActiveHealthCrises = new();
    public List<NaturalDisasterInstance> ActiveDisasters = new();
    public Dictionary<string, int> HealthCooldownUntilDay = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> DisasterCooldownUntilDay = new(StringComparer.OrdinalIgnoreCase);
    public float HealthPreparedness = 0.5f;
    public float DisasterPreparedness = 0.5f;
    public int HealthCrisisSeq = 1;
    public int DisasterSeq = 1;
    public int LastHealthDisasterAiDay = -1;
}

/// <summary>Data-driven disease definition loaded from <c>game/common/diseases/*.json</c>.</summary>
public sealed class DiseaseDefinition
{
    public string Id = "";
    public string NameKey = "";
    public string SummaryKey = "";
    public string CauseSummaryKey = "";
    public string Category = "respiratory";
    public float MinAbsLat;
    public float MaxAbsLat = 90f;
    public string[] PreferredSeasons = { "any" };
    public float RequiresWarDamage;
    public float MinUrbanization;
    public Dictionary<string, float> TerrainWeights = new(StringComparer.OrdinalIgnoreCase);
    public float AnnualChance = 0.04f;
    public float StartingSeverity = 30f;
    public float WeeklySeverityDrift = 3.5f;
    public int BaseDurationDays = 98;
    public int CooldownDays = 420;
    public float SpreadRate = 0.25f;
    public float MortalityAnnualAtMax = 0.005f;
    public float EconomyCostGdpShare = 0.006f;
    public float RecessionDepthMax = 0.025f;
    public float StabilityHit = 0.006f;
    public float WeeklyStabilityHit = 0.0018f;
    public float WeeklyRadicalismHit = 0.0015f;
    public float WeeklyLivingHit = 0.0016f;
}

/// <summary>Data-driven natural disaster definition loaded from <c>game/common/disasters/*.json</c>.</summary>
public sealed class DisasterDefinition
{
    public string Id = "";
    public string NameKey = "";
    public string SummaryKey = "";
    public string CauseSummaryKey = "";
    public string Category = "geological";
    public float MinAbsLat;
    public float MaxAbsLat = 90f;
    public string[] PreferredSeasons = { "any" };
    public bool RequiresCoastal;
    public bool RequiresInlandWater;
    public Dictionary<string, float> TerrainWeights = new(StringComparer.OrdinalIgnoreCase);
    public float AnnualChance = 0.03f;
    public float StartingSeverity = 50f;
    public float WeeklyRecoveryRate = 5f;
    public int BaseDurationDays = 63;
    public int CooldownDays = 320;
    public float InstantDamage = 0.12f;
    public float InstantDeathsShare = 0.0001f;
    public float WeeklyDeathsAnnual = 0.0012f;
    public float EconomyCostGdpShare = 0.008f;
    public float StabilityHit = 0.012f;
    public float WeeklyStabilityHit = 0.0015f;
    public float WeeklyRadicalismHit = 0.0018f;
}

/// <summary>One active or recently resolved disease outbreak on a <see cref="Nation"/>.</summary>
public sealed class HealthCrisisInstance
{
    public int Id;
    public string DefinitionId = "";
    public int NationId;
    public int AffectedStateMapIndex;
    public int OriginNationId = -1;
    public int StartedDay;
    public int LastUpdateDay = -1;
    public int ResolvedDay = -1;
    public int DurationDays = 98;
    public float Severity = 30f;
    public HealthResponseAction Response = HealthResponseAction.None;
    public int ResponseDay = -1;
    public int ResponseCooldownUntilDay = -1;
    public long TotalDeaths;
    public float TotalCostBillions;

    public bool IsActive => ResolvedDay < 0;

    public HealthCrisisInstance Copy() => new()
    {
        Id = Id,
        DefinitionId = DefinitionId ?? "",
        NationId = NationId,
        AffectedStateMapIndex = AffectedStateMapIndex,
        OriginNationId = OriginNationId,
        StartedDay = StartedDay,
        LastUpdateDay = LastUpdateDay,
        ResolvedDay = ResolvedDay,
        DurationDays = DurationDays,
        Severity = Severity,
        Response = Response,
        ResponseDay = ResponseDay,
        ResponseCooldownUntilDay = ResponseCooldownUntilDay,
        TotalDeaths = TotalDeaths,
        TotalCostBillions = TotalCostBillions
    };
}

/// <summary>One active or recently resolved natural disaster on a <see cref="Nation"/>.</summary>
public sealed class NaturalDisasterInstance
{
    public int Id;
    public string DefinitionId = "";
    public int NationId;
    public int AffectedStateMapIndex;
    public int StartedDay;
    public int LastUpdateDay = -1;
    public int ResolvedDay = -1;
    public int DurationDays = 63;
    public float Severity = 50f;
    public DisasterReliefAction Response = DisasterReliefAction.None;
    public int ResponseDay = -1;
    public int ResponseCooldownUntilDay = -1;
    public float InstantDamageApplied;
    public long TotalDeaths;
    public float TotalCostBillions;

    public bool IsActive => ResolvedDay < 0;

    public NaturalDisasterInstance Copy() => new()
    {
        Id = Id,
        DefinitionId = DefinitionId ?? "",
        NationId = NationId,
        AffectedStateMapIndex = AffectedStateMapIndex,
        StartedDay = StartedDay,
        LastUpdateDay = LastUpdateDay,
        ResolvedDay = ResolvedDay,
        DurationDays = DurationDays,
        Severity = Severity,
        Response = Response,
        ResponseDay = ResponseDay,
        ResponseCooldownUntilDay = ResponseCooldownUntilDay,
        InstantDamageApplied = InstantDamageApplied,
        TotalDeaths = TotalDeaths,
        TotalCostBillions = TotalCostBillions
    };
}

/// <summary>Read-only UI brief for an active public health crisis.</summary>
public sealed record HealthCrisisBrief(
    int Id,
    string DefinitionId,
    string Name,
    string Summary,
    string Cause,
    string Category,
    int AffectedStateMapIndex,
    string AffectedStateName,
    int OriginNationId,
    string OriginNationTag,
    float Severity,
    CrisisSeverityTier SeverityTier,
    int DaysElapsed,
    int DaysRemaining,
    HealthResponseAction ActiveResponse,
    string ActiveResponseLabel,
    float SpreadRisk,
    long TotalDeaths,
    float TotalCostBillions,
    float HospitalCoverage,
    float MedicineSupply,
    float HealthPreparedness,
    bool CanRespond,
    string RefusalReason);

/// <summary>Read-only UI brief for an active natural disaster.</summary>
public sealed record DisasterBrief(
    int Id,
    string DefinitionId,
    string Name,
    string Summary,
    string Cause,
    string Category,
    int AffectedStateMapIndex,
    string AffectedStateName,
    float Severity,
    CrisisSeverityTier SeverityTier,
    int DaysElapsed,
    int DaysRemaining,
    DisasterReliefAction ActiveResponse,
    string ActiveResponseLabel,
    float InstantDamageApplied,
    long TotalDeaths,
    float TotalCostBillions,
    float DisasterPreparedness,
    bool CanRespond,
    string RefusalReason);

/// <summary>Catalog of data-driven diseases loaded from <c>game/common/diseases/*.json</c>.</summary>
public static class DiseaseCatalog
{
    private static readonly SortedDictionary<string, DiseaseDefinition> ById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object InitLock = new();
    private static volatile bool _loaded;
    private static DiseaseDefinition[] _cachedAll = Array.Empty<DiseaseDefinition>();
    private static FrozenDictionary<string, DiseaseDefinition> _frozenById = FrozenDictionary<string, DiseaseDefinition>.Empty;

    public static IReadOnlyList<DiseaseDefinition> All
    {
        get
        {
            EnsureLoaded();
            return _cachedAll;
        }
    }

    public static DiseaseDefinition Find(string id)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _frozenById.TryGetValue(id.Trim(), out var def) ? def : null;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (InitLock)
        {
            if (_loaded) return;
            ResetCatalog();
        }
    }

    public static void ResetCatalog(List<string> errors = null)
    {
        lock (InitLock)
        {
            ById.Clear();
            _loaded = true;
            _cachedAll = Array.Empty<DiseaseDefinition>();
            _frozenById = FrozenDictionary<string, DiseaseDefinition>.Empty;
            var root = GameDataLoader.DataFolder();
            if (string.IsNullOrWhiteSpace(root)) return;
            var folder = Path.Combine(root, "common", "diseases");
            if (!Directory.Exists(folder)) return;
            var files = new List<string>(StructuredData.Files(folder, SearchOption.TopDirectoryOnly));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                try
                {
                    using var doc = StructuredData.ReadDocument(file);
                    Merge(doc.RootElement, errors);
                }
                catch (Exception ex)
                {
                    errors?.Add($"common/diseases/{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        lock (InitLock)
        {
            if (!_loaded) _loaded = true;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors?.Add("common/diseases must be a JSON object keyed by snake_case id.");
                return;
            }
            foreach (var entry in root.EnumerateObject())
            {
                if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (GameDataLoader.IsRemoved(entry.Value, $"common/diseases:{entry.Name}"))
                {
                    ById.Remove(entry.Name);
                    continue;
                }
                if (!ById.TryGetValue(entry.Name, out var def))
                    ById[entry.Name] = def = new DiseaseDefinition { Id = entry.Name };
                var obj = entry.Value;
                def.NameKey = DiseasesAndDisasters.ReadStr(obj, "name", def.NameKey.Length > 0 ? def.NameKey : entry.Name);
                def.SummaryKey = DiseasesAndDisasters.ReadStr(obj, "summary", def.SummaryKey);
                def.CauseSummaryKey = DiseasesAndDisasters.ReadStr(obj, "cause_summary", def.CauseSummaryKey);
                def.Category = DiseasesAndDisasters.ReadStr(obj, "category", def.Category);
                def.MinAbsLat = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "min_abs_lat", def.MinAbsLat), 0f, 90f);
                def.MaxAbsLat = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "max_abs_lat", def.MaxAbsLat), def.MinAbsLat, 90f);
                def.PreferredSeasons = DiseasesAndDisasters.ReadSeasons(obj, "preferred_seasons", def.PreferredSeasons);
                def.RequiresWarDamage = MathF.Max(0f, DiseasesAndDisasters.ReadFloat(obj, "requires_war_damage", def.RequiresWarDamage));
                def.MinUrbanization = M.Clamp01(DiseasesAndDisasters.ReadFloat(obj, "min_urbanization", def.MinUrbanization));
                DiseasesAndDisasters.MergeWeights(obj, "terrain_weights", def.TerrainWeights);
                def.AnnualChance = MathF.Max(0f, DiseasesAndDisasters.ReadFloat(obj, "annual_chance", def.AnnualChance));
                def.StartingSeverity = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "starting_severity", def.StartingSeverity), 5f, 90f);
                def.WeeklySeverityDrift = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "weekly_severity_drift", def.WeeklySeverityDrift), -10f, 20f);
                def.BaseDurationDays = Math.Clamp(DiseasesAndDisasters.ReadInt(obj, "base_duration_days", def.BaseDurationDays), 14, 730);
                def.CooldownDays = Math.Clamp(DiseasesAndDisasters.ReadInt(obj, "cooldown_days", def.CooldownDays), 30, 3650);
                def.SpreadRate = M.Clamp01(DiseasesAndDisasters.ReadFloat(obj, "spread_rate", def.SpreadRate));
                def.MortalityAnnualAtMax = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "mortality_annual_at_max", def.MortalityAnnualAtMax), 0f, 0.08f);
                def.EconomyCostGdpShare = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "economy_cost_gdp_share", def.EconomyCostGdpShare), 0f, 0.08f);
                def.RecessionDepthMax = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "recession_depth_max", def.RecessionDepthMax), 0f, 0.15f);
                def.StabilityHit = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "stability_hit", def.StabilityHit), 0f, 0.10f);
                def.WeeklyStabilityHit = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "weekly_stability_hit", def.WeeklyStabilityHit), 0f, 0.02f);
                def.WeeklyRadicalismHit = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "weekly_radicalism_hit", def.WeeklyRadicalismHit), 0f, 0.02f);
                def.WeeklyLivingHit = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "weekly_living_hit", def.WeeklyLivingHit), 0f, 0.02f);
            }
            var arr = new DiseaseDefinition[ById.Count];
            ById.Values.CopyTo(arr, 0);
            _cachedAll = arr;
            _frozenById = ById.Count > 0 ? ById.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) : FrozenDictionary<string, DiseaseDefinition>.Empty;
        }
    }
}

/// <summary>Catalog of data-driven natural disasters loaded from <c>game/common/disasters/*.json</c>.</summary>
public static class DisasterCatalog
{
    private static readonly SortedDictionary<string, DisasterDefinition> ById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object InitLock = new();
    private static volatile bool _loaded;
    private static DisasterDefinition[] _cachedAll = Array.Empty<DisasterDefinition>();
    private static FrozenDictionary<string, DisasterDefinition> _frozenById = FrozenDictionary<string, DisasterDefinition>.Empty;

    public static IReadOnlyList<DisasterDefinition> All
    {
        get
        {
            EnsureLoaded();
            return _cachedAll;
        }
    }

    public static DisasterDefinition Find(string id)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _frozenById.TryGetValue(id.Trim(), out var def) ? def : null;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (InitLock)
        {
            if (_loaded) return;
            ResetCatalog();
        }
    }

    public static void ResetCatalog(List<string> errors = null)
    {
        lock (InitLock)
        {
            ById.Clear();
            _loaded = true;
            _cachedAll = Array.Empty<DisasterDefinition>();
            _frozenById = FrozenDictionary<string, DisasterDefinition>.Empty;
            var root = GameDataLoader.DataFolder();
            if (string.IsNullOrWhiteSpace(root)) return;
            var folder = Path.Combine(root, "common", "disasters");
            if (!Directory.Exists(folder)) return;
            var files = new List<string>(StructuredData.Files(folder, SearchOption.TopDirectoryOnly));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                try
                {
                    using var doc = StructuredData.ReadDocument(file);
                    Merge(doc.RootElement, errors);
                }
                catch (Exception ex)
                {
                    errors?.Add($"common/disasters/{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        lock (InitLock)
        {
            if (!_loaded) _loaded = true;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors?.Add("common/disasters must be a JSON object keyed by snake_case id.");
                return;
            }
            foreach (var entry in root.EnumerateObject())
            {
                if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (GameDataLoader.IsRemoved(entry.Value, $"common/disasters:{entry.Name}"))
                {
                    ById.Remove(entry.Name);
                    continue;
                }
                if (!ById.TryGetValue(entry.Name, out var def))
                    ById[entry.Name] = def = new DisasterDefinition { Id = entry.Name };
                var obj = entry.Value;
                def.NameKey = DiseasesAndDisasters.ReadStr(obj, "name", def.NameKey.Length > 0 ? def.NameKey : entry.Name);
                def.SummaryKey = DiseasesAndDisasters.ReadStr(obj, "summary", def.SummaryKey);
                def.CauseSummaryKey = DiseasesAndDisasters.ReadStr(obj, "cause_summary", def.CauseSummaryKey);
                def.Category = DiseasesAndDisasters.ReadStr(obj, "category", def.Category);
                def.MinAbsLat = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "min_abs_lat", def.MinAbsLat), 0f, 90f);
                def.MaxAbsLat = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "max_abs_lat", def.MaxAbsLat), def.MinAbsLat, 90f);
                def.PreferredSeasons = DiseasesAndDisasters.ReadSeasons(obj, "preferred_seasons", def.PreferredSeasons);
                def.RequiresCoastal = DiseasesAndDisasters.ReadBool(obj, "requires_coastal", def.RequiresCoastal);
                def.RequiresInlandWater = DiseasesAndDisasters.ReadBool(obj, "requires_inland_water", def.RequiresInlandWater);
                DiseasesAndDisasters.MergeWeights(obj, "terrain_weights", def.TerrainWeights);
                def.AnnualChance = MathF.Max(0f, DiseasesAndDisasters.ReadFloat(obj, "annual_chance", def.AnnualChance));
                def.StartingSeverity = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "starting_severity", def.StartingSeverity), 5f, 95f);
                def.WeeklyRecoveryRate = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "weekly_recovery_rate", def.WeeklyRecoveryRate), -10f, 25f);
                def.BaseDurationDays = Math.Clamp(DiseasesAndDisasters.ReadInt(obj, "base_duration_days", def.BaseDurationDays), 14, 730);
                def.CooldownDays = Math.Clamp(DiseasesAndDisasters.ReadInt(obj, "cooldown_days", def.CooldownDays), 30, 3650);
                def.InstantDamage = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "instant_damage", def.InstantDamage), 0f, 0.40f);
                def.InstantDeathsShare = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "instant_deaths_share", def.InstantDeathsShare), 0f, 0.005f);
                def.WeeklyDeathsAnnual = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "weekly_deaths_annual", def.WeeklyDeathsAnnual), 0f, 0.04f);
                def.EconomyCostGdpShare = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "economy_cost_gdp_share", def.EconomyCostGdpShare), 0f, 0.08f);
                def.StabilityHit = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "stability_hit", def.StabilityHit), 0f, 0.10f);
                def.WeeklyStabilityHit = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "weekly_stability_hit", def.WeeklyStabilityHit), 0f, 0.02f);
                def.WeeklyRadicalismHit = M.Clamp(DiseasesAndDisasters.ReadFloat(obj, "weekly_radicalism_hit", def.WeeklyRadicalismHit), 0f, 0.02f);
            }
            var arr = new DisasterDefinition[ById.Count];
            ById.Values.CopyTo(arr, 0);
            _cachedAll = arr;
            _frozenById = ById.Count > 0 ? ById.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) : FrozenDictionary<string, DisasterDefinition>.Empty;
        }
    }
}

/// <summary>
/// Data-driven Diseases (6) and Natural Disasters (8) system (Wave 6H).
/// Computes regional/climate triggers, severity tiers, economic/demographic/stability impacts,
/// cross-border contagion, player/AI agency commands, and bit-exact deterministic state.
/// </summary>
public static class DiseasesAndDisasters
{
    public const string FeatureId = "diseases_and_disasters";
    private const int MaxResolvedHistoryPerNation = 6;

    private static readonly HealthResponseAction[] AllHealthActions =
    {
        HealthResponseAction.MassVaccinationAndMedicalDrive,
        HealthResponseAction.EmergencyHealthSubsidies,
        HealthResponseAction.TargetedQuarantine,
        HealthResponseAction.InternationalMedicalCooperation
    };

    private static readonly DisasterReliefAction[] AllDisasterActions =
    {
        DisasterReliefAction.InfrastructureReconstruction,
        DisasterReliefAction.EmergencyEvacuationAndRescue,
        DisasterReliefAction.StrategicReserveAid,
        DisasterReliefAction.CivilDefenseMobilization
    };

    /// <summary>Optional save-load normalization hook ensuring non-null per-nation collections on legacy saves.</summary>
    public static void NormalizeAfterLoad(Campaign campaign)
    {
        _ = DiseaseCatalog.All;
        _ = DisasterCatalog.All;
        if (campaign?.Nations is null) return;
        for (var i = 0; i < campaign.Nations.Count; i++)
        {
            var n = campaign.Nations[i];
            if (n is null) continue;
            n.ActiveHealthCrises ??= new List<HealthCrisisInstance>();
            n.ActiveDisasters ??= new List<NaturalDisasterInstance>();
            n.HealthCooldownUntilDay ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            n.DisasterCooldownUntilDay ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!float.IsFinite(n.HealthPreparedness)) n.HealthPreparedness = 0.5f;
            if (!float.IsFinite(n.DisasterPreparedness)) n.DisasterPreparedness = 0.5f;
            if (n.HealthCrisisSeq < 1) n.HealthCrisisSeq = 1;
            if (n.DisasterSeq < 1) n.DisasterSeq = 1;
        }
    }

    public static bool IsEnabled(Campaign c)
    {
        if (c is null || !c.Defines.DiseasesAndDisastersEnabled) return false;
        if (c.FeatureOverrides.TryGetValue(FeatureId, out var over) && !over) return false;
        if (c.Defines.Features is not null && c.Defines.Features.TryGetValue(FeatureId, out var feat) && !feat &&
            !c.FeatureOverrides.ContainsKey(FeatureId)) return false;
        return true;
    }

    public static CrisisSeverityTier TierOf(float severity)
    {
        if (severity >= 80f) return CrisisSeverityTier.Critical;
        if (severity >= 55f) return CrisisSeverityTier.Severe;
        if (severity >= 30f) return CrisisSeverityTier.Moderate;
        return CrisisSeverityTier.Minor;
    }

    public static string TierLocalizationKey(CrisisSeverityTier tier) => tier switch
    {
        CrisisSeverityTier.Critical => "health_disaster.tier.critical",
        CrisisSeverityTier.Severe => "health_disaster.tier.severe",
        CrisisSeverityTier.Moderate => "health_disaster.tier.moderate",
        _ => "health_disaster.tier.minor"
    };

    public static string ActionLocalizationKey(HealthResponseAction action) => action switch
    {
        HealthResponseAction.TargetedQuarantine => "health_disaster.health_action.targeted_quarantine",
        HealthResponseAction.MassVaccinationAndMedicalDrive => "health_disaster.health_action.mass_vaccination",
        HealthResponseAction.EmergencyHealthSubsidies => "health_disaster.health_action.emergency_subsidies",
        HealthResponseAction.InternationalMedicalCooperation => "health_disaster.health_action.international_cooperation",
        _ => "health_disaster.health_action.none"
    };

    public static string ActionLocalizationKey(DisasterReliefAction action) => action switch
    {
        DisasterReliefAction.EmergencyEvacuationAndRescue => "health_disaster.disaster_action.emergency_evacuation",
        DisasterReliefAction.InfrastructureReconstruction => "health_disaster.disaster_action.infrastructure_reconstruction",
        DisasterReliefAction.StrategicReserveAid => "health_disaster.disaster_action.strategic_reserve_aid",
        DisasterReliefAction.CivilDefenseMobilization => "health_disaster.disaster_action.civil_defense",
        _ => "health_disaster.disaster_action.none"
    };

    /// <summary>Read-only UI briefs for all active diseases on <paramref name="nation"/> (never mutates Campaign).</summary>
    public static IReadOnlyList<HealthCrisisBrief> ActiveHealthCrises(Campaign c, Nation nation)
    {
        if (c is null || nation is null || nation.ActiveHealthCrises is null || nation.ActiveHealthCrises.Count == 0)
            return Array.Empty<HealthCrisisBrief>();

        var list = new List<HealthCrisisBrief>(nation.ActiveHealthCrises.Count);
        for (var i = 0; i < nation.ActiveHealthCrises.Count; i++)
        {
            var inst = nation.ActiveHealthCrises[i];
            if (inst is null || !inst.IsActive) continue;
            list.Add(DescribeHealthCrisis(c, nation, inst));
        }
        return list;
    }

    /// <summary>Read-only UI briefs for all active natural disasters on <paramref name="nation"/> (never mutates Campaign).</summary>
    public static IReadOnlyList<DisasterBrief> ActiveDisasters(Campaign c, Nation nation)
    {
        if (c is null || nation is null || nation.ActiveDisasters is null || nation.ActiveDisasters.Count == 0)
            return Array.Empty<DisasterBrief>();

        var list = new List<DisasterBrief>(nation.ActiveDisasters.Count);
        for (var i = 0; i < nation.ActiveDisasters.Count; i++)
        {
            var inst = nation.ActiveDisasters[i];
            if (inst is null || !inst.IsActive) continue;
            list.Add(DescribeDisaster(c, nation, inst));
        }
        return list;
    }

    public static HealthCrisisBrief DescribeHealthCrisis(Campaign c, Nation n, HealthCrisisInstance inst)
    {
        var def = DiseaseCatalog.Find(inst.DefinitionId);
        var state = c?.StateByMapIndex(inst.AffectedStateMapIndex);
        var stateName = state is not null ? state.DisplayName : (n?.CapitalName ?? "");
        var originTag = inst.OriginNationId >= 0 ? (c?.NationById(inst.OriginNationId)?.Tag ?? "") : "";
        var elapsed = c is not null ? Math.Max(0, c.ElapsedDays - inst.StartedDay) : 0;
        var remaining = Math.Max(0, inst.DurationDays - elapsed);
        var medicine = n is not null ? HouseholdEconomy.MedicineSupply(n) : 1f;
        var hospitalCov = state is not null
            ? HouseholdEconomy.HospitalCover(state, state.Buildings.GetValueOrDefault(BuildingKind.Hospital), medicine)
            : 0.5f;
        var spreadRisk = ComputeSpreadRisk(def, inst, n);
        var canRespond = false;
        var refusal = "";
        if (c is not null && n is not null)
        {
            for (var i = 0; i < AllHealthActions.Length; i++)
            {
                if (CanRespondHealth(c, n, inst.Id, AllHealthActions[i], out var r))
                {
                    canRespond = true;
                    refusal = "";
                    break;
                }
                if (refusal.Length == 0) refusal = r;
            }
        }

        return new HealthCrisisBrief(
            inst.Id,
            inst.DefinitionId,
            Loc.Get(def?.NameKey ?? inst.DefinitionId),
            Loc.Get(def?.SummaryKey ?? ""),
            Loc.Get(def?.CauseSummaryKey ?? ""),
            def?.Category ?? "respiratory",
            inst.AffectedStateMapIndex,
            stateName,
            inst.OriginNationId,
            originTag,
            M.Clamp(inst.Severity, 0f, 100f),
            TierOf(inst.Severity),
            elapsed,
            remaining,
            inst.Response,
            Loc.Get(ActionLocalizationKey(inst.Response)),
            spreadRisk,
            inst.TotalDeaths,
            inst.TotalCostBillions,
            hospitalCov,
            medicine,
            n?.HealthPreparedness ?? 0.5f,
            canRespond,
            refusal);
    }

    public static DisasterBrief DescribeDisaster(Campaign c, Nation n, NaturalDisasterInstance inst)
    {
        var def = DisasterCatalog.Find(inst.DefinitionId);
        var state = c?.StateByMapIndex(inst.AffectedStateMapIndex);
        var stateName = state is not null ? state.DisplayName : (n?.CapitalName ?? "");
        var elapsed = c is not null ? Math.Max(0, c.ElapsedDays - inst.StartedDay) : 0;
        var remaining = Math.Max(0, inst.DurationDays - elapsed);
        var canRespond = false;
        var refusal = "";
        if (c is not null && n is not null)
        {
            for (var i = 0; i < AllDisasterActions.Length; i++)
            {
                if (CanRespondDisaster(c, n, inst.Id, AllDisasterActions[i], out var r))
                {
                    canRespond = true;
                    refusal = "";
                    break;
                }
                if (refusal.Length == 0) refusal = r;
            }
        }

        return new DisasterBrief(
            inst.Id,
            inst.DefinitionId,
            Loc.Get(def?.NameKey ?? inst.DefinitionId),
            Loc.Get(def?.SummaryKey ?? ""),
            Loc.Get(def?.CauseSummaryKey ?? ""),
            def?.Category ?? "geological",
            inst.AffectedStateMapIndex,
            stateName,
            M.Clamp(inst.Severity, 0f, 100f),
            TierOf(inst.Severity),
            elapsed,
            remaining,
            inst.Response,
            Loc.Get(ActionLocalizationKey(inst.Response)),
            inst.InstantDamageApplied,
            inst.TotalDeaths,
            inst.TotalCostBillions,
            n?.DisasterPreparedness ?? 0.5f,
            canRespond,
            refusal);
    }

    public static void HealthActionCost(
        Campaign c, Nation n, int crisisId, HealthResponseAction action,
        out float treasuryCost, out float ppCost)
    {
        treasuryCost = 0f;
        ppCost = 0f;
        if (n is null || action == HealthResponseAction.None) return;
        var gdp = MathF.Max(1f, n.Gdp);
        var inst = FindHealthCrisis(n, crisisId);
        var sevScale = inst is not null ? 0.75f + 0.5f * M.Clamp01(inst.Severity / 100f) : 1f;
        var medicine = HouseholdEconomy.MedicineSupply(n);
        switch (action)
        {
            case HealthResponseAction.TargetedQuarantine:
                treasuryCost = MathF.Max(0.05f, gdp * 0.0008f * sevScale);
                ppCost = 12f;
                break;
            case HealthResponseAction.MassVaccinationAndMedicalDrive:
                var medDiscount = medicine >= 0.85f ? 0.75f : 1.0f;
                treasuryCost = MathF.Max(0.25f, gdp * 0.0030f * sevScale * medDiscount);
                ppCost = 10f;
                break;
            case HealthResponseAction.EmergencyHealthSubsidies:
                treasuryCost = MathF.Max(0.12f, gdp * 0.0018f * sevScale);
                ppCost = 6f;
                break;
            case HealthResponseAction.InternationalMedicalCooperation:
                treasuryCost = MathF.Max(0.04f, gdp * 0.0006f);
                ppCost = 14f;
                break;
        }
    }

    public static void DisasterActionCost(
        Campaign c, Nation n, int disasterId, DisasterReliefAction action,
        out float treasuryCost, out float ppCost)
    {
        treasuryCost = 0f;
        ppCost = 0f;
        if (n is null || action == DisasterReliefAction.None) return;
        var gdp = MathF.Max(1f, n.Gdp);
        var inst = FindDisaster(n, disasterId);
        var sevScale = inst is not null ? 0.75f + 0.5f * M.Clamp01(inst.Severity / 100f) : 1f;
        switch (action)
        {
            case DisasterReliefAction.EmergencyEvacuationAndRescue:
                treasuryCost = MathF.Max(0.10f, gdp * 0.0015f * sevScale);
                ppCost = 8f;
                break;
            case DisasterReliefAction.InfrastructureReconstruction:
                treasuryCost = MathF.Max(0.25f, gdp * 0.0032f * sevScale);
                ppCost = 10f;
                break;
            case DisasterReliefAction.StrategicReserveAid:
                treasuryCost = MathF.Max(0.12f, gdp * 0.0020f * sevScale);
                ppCost = 6f;
                break;
            case DisasterReliefAction.CivilDefenseMobilization:
                treasuryCost = MathF.Max(0.03f, gdp * 0.0005f);
                ppCost = 15f;
                break;
        }
    }

    public static bool CanRespondHealth(Campaign c, Nation n, int crisisId, HealthResponseAction action, out string reason)
    {
        reason = "";
        if (!IsEnabled(c)) { reason = Loc.Get("health_disaster.refuse.disabled"); return false; }
        if (n is null || n.Population <= 0 || n.Movement is not null)
        {
            reason = Loc.Get("health_disaster.refuse.invalid_nation");
            return false;
        }
        var inst = FindHealthCrisis(n, crisisId);
        if (inst is null) { reason = Loc.Get("health_disaster.refuse.not_found"); return false; }
        if (!inst.IsActive) { reason = Loc.Get("health_disaster.refuse.already_resolved"); return false; }
        if (action == HealthResponseAction.None || inst.Response == action)
        {
            reason = Loc.Get("health_disaster.refuse.same_response");
            return false;
        }
        if (inst.ResponseCooldownUntilDay > c.ElapsedDays)
        {
            reason = Loc.Format("health_disaster.refuse.cooldown", inst.ResponseCooldownUntilDay - c.ElapsedDays);
            return false;
        }
        HealthActionCost(c, n, crisisId, action, out var cashCost, out var ppCost);
        if (n.Treasury + 0.0005f < cashCost)
        {
            reason = Loc.Format("health_disaster.refuse.low_treasury", FmtMoney(cashCost));
            return false;
        }
        if (n.PoliticalPoints + 0.001f < ppCost)
        {
            reason = Loc.Format("health_disaster.refuse.low_pp", ppCost);
            return false;
        }
        return true;
    }

    public static bool CanRespondDisaster(Campaign c, Nation n, int disasterId, DisasterReliefAction action, out string reason)
    {
        reason = "";
        if (!IsEnabled(c)) { reason = Loc.Get("health_disaster.refuse.disabled"); return false; }
        if (n is null || n.Population <= 0 || n.Movement is not null)
        {
            reason = Loc.Get("health_disaster.refuse.invalid_nation");
            return false;
        }
        var inst = FindDisaster(n, disasterId);
        if (inst is null) { reason = Loc.Get("health_disaster.refuse.not_found"); return false; }
        if (!inst.IsActive) { reason = Loc.Get("health_disaster.refuse.already_resolved"); return false; }
        if (action == DisasterReliefAction.None || inst.Response == action)
        {
            reason = Loc.Get("health_disaster.refuse.same_response");
            return false;
        }
        if (inst.ResponseCooldownUntilDay > c.ElapsedDays)
        {
            reason = Loc.Format("health_disaster.refuse.cooldown", inst.ResponseCooldownUntilDay - c.ElapsedDays);
            return false;
        }
        DisasterActionCost(c, n, disasterId, action, out var cashCost, out var ppCost);
        if (n.Treasury + 0.0005f < cashCost)
        {
            reason = Loc.Format("health_disaster.refuse.low_treasury", FmtMoney(cashCost));
            return false;
        }
        if (n.PoliticalPoints + 0.001f < ppCost)
        {
            reason = Loc.Format("health_disaster.refuse.low_pp", ppCost);
            return false;
        }
        return true;
    }

    public static bool RespondHealth(Campaign c, Nation n, int crisisId, HealthResponseAction action)
    {
        if (!CanRespondHealth(c, n, crisisId, action, out _)) return false;
        var inst = FindHealthCrisis(n, crisisId);
        if (inst is null) return false;
        var def = DiseaseCatalog.Find(inst.DefinitionId);
        var state = c.StateByMapIndex(inst.AffectedStateMapIndex);

        HealthActionCost(c, n, crisisId, action, out var cashCost, out var ppCost);
        n.Treasury -= cashCost;
        MoneyLedger.Book(n, MoneyFlow.Disasters, -cashCost);
        n.PoliticalPoints = MathF.Max(0f, n.PoliticalPoints - ppCost);
        inst.TotalCostBillions += cashCost;
        inst.Response = action;
        inst.ResponseDay = c.ElapsedDays;
        inst.ResponseCooldownUntilDay = c.ElapsedDays + Math.Max(7, c.Defines.HealthDisasterResponseCooldownDays);
        inst.LastUpdateDay = c.ElapsedDays;

        var medicine = HouseholdEconomy.MedicineSupply(n);
        switch (action)
        {
            case HealthResponseAction.TargetedQuarantine:
                inst.Severity = M.Clamp(inst.Severity - 10f, 0f, 100f);
                inst.DurationDays = Math.Max(14, inst.DurationDays - 14);
                // Trade-off: mobility curbs reduce near-term commercial receipts and slightly chafe stability.
                IncomePenalties.Add(c, n, 0.008f, 28, "Quarantine");
                n.Stability = M.Clamp(n.Stability - 0.005f, 0.05f, 0.99f);
                break;

            case HealthResponseAction.MassVaccinationAndMedicalDrive:
                var drop = 16f + 8f * medicine;
                inst.Severity = M.Clamp(inst.Severity - drop, 0f, 100f);
                inst.DurationDays = Math.Max(14, (int)(inst.DurationDays * 0.72f));
                n.HealthPreparedness = M.Clamp(n.HealthPreparedness + 0.04f, 0.1f, 0.98f);
                n.Stability = M.Clamp(n.Stability + 0.010f, 0.05f, 0.99f);
                break;

            case HealthResponseAction.EmergencyHealthSubsidies:
                inst.Severity = M.Clamp(inst.Severity - 9f, 0f, 100f);
                n.Stability = M.Clamp(n.Stability + 0.015f, 0.05f, 0.99f);
                if (state is not null)
                {
                    state.StandardOfLiving = M.Clamp01(state.StandardOfLiving + 0.012f);
                    state.Radicalism = M.Clamp01(state.Radicalism - 0.025f);
                }
                break;

            case HealthResponseAction.InternationalMedicalCooperation:
                inst.Severity = M.Clamp(inst.Severity - 12f, 0f, 100f);
                inst.DurationDays = Math.Max(14, inst.DurationDays - 14);
                n.HealthPreparedness = M.Clamp(n.HealthPreparedness + 0.025f, 0.1f, 0.98f);
                n.Notoriety = MathF.Max(0f, n.Notoriety - 0.5f);
                break;
        }

        if (inst.Severity <= 5f)
            ResolveDisease(c, n, inst, def);

        return true;
    }

    public static bool RespondDisaster(Campaign c, Nation n, int disasterId, DisasterReliefAction action)
    {
        if (!CanRespondDisaster(c, n, disasterId, action, out _)) return false;
        var inst = FindDisaster(n, disasterId);
        if (inst is null) return false;
        var def = DisasterCatalog.Find(inst.DefinitionId);
        var state = c.StateByMapIndex(inst.AffectedStateMapIndex);

        DisasterActionCost(c, n, disasterId, action, out var cashCost, out var ppCost);
        n.Treasury -= cashCost;
        MoneyLedger.Book(n, MoneyFlow.Disasters, -cashCost);
        n.PoliticalPoints = MathF.Max(0f, n.PoliticalPoints - ppCost);
        inst.TotalCostBillions += cashCost;
        inst.Response = action;
        inst.ResponseDay = c.ElapsedDays;
        inst.ResponseCooldownUntilDay = c.ElapsedDays + Math.Max(7, c.Defines.HealthDisasterResponseCooldownDays);
        inst.LastUpdateDay = c.ElapsedDays;

        switch (action)
        {
            case DisasterReliefAction.EmergencyEvacuationAndRescue:
                inst.Severity = M.Clamp(inst.Severity - 16f, 0f, 100f);
                n.Stability = M.Clamp(n.Stability + 0.012f, 0.05f, 0.99f);
                break;

            case DisasterReliefAction.InfrastructureReconstruction:
                inst.Severity = M.Clamp(inst.Severity - 14f, 0f, 100f);
                inst.DurationDays = Math.Max(14, (int)(inst.DurationDays * 0.75f));
                n.DisasterPreparedness = M.Clamp(n.DisasterPreparedness + 0.05f, 0.1f, 0.98f);
                if (state is not null && state.WarDamage > 0f)
                {
                    var repair = MathF.Max(0.06f, inst.InstantDamageApplied * 0.55f);
                    state.WarDamage = MathF.Max(0f, state.WarDamage - repair);
                }
                break;

            case DisasterReliefAction.StrategicReserveAid:
                inst.Severity = M.Clamp(inst.Severity - 10f, 0f, 100f);
                n.Stability = M.Clamp(n.Stability + 0.018f, 0.05f, 0.99f);
                if (state is not null)
                {
                    state.StandardOfLiving = M.Clamp01(state.StandardOfLiving + 0.015f);
                    state.Radicalism = M.Clamp01(state.Radicalism - 0.03f);
                }
                break;

            case DisasterReliefAction.CivilDefenseMobilization:
                inst.Severity = M.Clamp(inst.Severity - 14f, 0f, 100f);
                inst.DurationDays = Math.Max(14, inst.DurationDays - 10);
                n.DisasterPreparedness = M.Clamp(n.DisasterPreparedness + 0.02f, 0.1f, 0.98f);
                break;
        }

        if (inst.Severity <= 5f)
            ResolveDisaster(c, n, inst, def);

        return true;
    }

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    private sealed class TickWeeklyScratch
    {
        public readonly List<Nation> EligibleNations = new();
        public int[] DiseasePick = Array.Empty<int>();
        public int[] DiseaseState = Array.Empty<int>();
        public int[] DisasterPick = Array.Empty<int>();
        public int[] DisasterState = Array.Empty<int>();
        public readonly List<(Nation Target, DiseaseDefinition Def, int OriginId)> SpreadQueue = new();

        public void EnsureCapacity(int count)
        {
            if (DiseasePick.Length >= count) return;
            var cap = Math.Max(count, 64);
            DiseasePick = new int[cap];
            DiseaseState = new int[cap];
            DisasterPick = new int[cap];
            DisasterState = new int[cap];
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, TickWeeklyScratch> _weeklyScratches = new();

    /// <summary>
    /// Weekly staged pass (every 7th campaign day when <see cref="Campaign.StepOpensNewDay"/> is true),
    /// yielding cleanly to keep frames responsive while remaining 100% deterministic across threads.
    /// </summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (!IsEnabled(c) || !c.StepOpensNewDay || c.ElapsedDays <= 0 || c.ElapsedDays % 7 != 0)
            yield break;

        var diseaseDefs = DiseaseCatalog.All;
        var disasterDefs = DisasterCatalog.All;
        var active = c.ActiveNations;
        if (active.Count == 0) yield break;

        var scratch = _weeklyScratches.GetOrCreateValue(c);
        var ordered = scratch.EligibleNations;
        ordered.Clear();
        for (var i = 0; i < active.Count; i++)
        {
            var n = active[i];
            if (n is not null && n.Movement is null && n.Population > 0 && n.Gdp > 0f)
                ordered.Add(n);
        }
        var count = ordered.Count;
        if (count == 0) yield break;
        if (count > 1) ordered.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        scratch.EnsureCapacity(count);
        var organicDiseasePick = scratch.DiseasePick;
        var organicDiseaseState = scratch.DiseaseState;
        var organicDisasterPick = scratch.DisasterPick;
        var organicDisasterState = scratch.DisasterState;
        Array.Fill(organicDiseasePick, -1, 0, count);
        Array.Fill(organicDisasterPick, -1, 0, count);

        var weekBucket = c.ElapsedDays / 7;
        var seed = c.Seed;
        var freqDis = MathF.Max(0f, c.Defines.DiseaseFrequencyMultiplier);
        var freqNat = MathF.Max(0f, c.Defines.DisasterFrequencyMultiplier);
        var gateProbDis = M.Clamp(0.08f * freqDis, 0.01f, 1.0f);
        var gateProbNat = M.Clamp(0.08f * freqNat, 0.01f, 1.0f);
        var maxDis = Math.Max(1, c.Defines.MaxActiveDiseasesPerNation);
        var maxNat = Math.Max(1, c.Defines.MaxActiveDisastersPerNation);

        // Warm lazy campaign/topology lookup caches sequentially before worker threads run.
        c.StatesOf(0);
        if (c.ProvinceMap is { } warmMap && c.States.Count > 0 && c.States[0] is { } firstState)
        {
            var warmState = firstState.MapIndex;
            LandAtlas.CentreOf(c, warmState);
            Waterfront.OfRegion(c, warmState);
            warmMap.ProvincesOfState(warmState);
        }

        DeterministicParallel.For(c, 0, count, i =>
        {
            var n = ordered[i];
            var doDis = diseaseDefs.Count > 0 && freqDis > 0f && CountActiveDiseases(n) < maxDis &&
                StableUnit(seed, weekBucket, n.Id, 0x101) < gateProbDis;
            var doNat = disasterDefs.Count > 0 && freqNat > 0f && CountActiveDisasters(n) < maxNat &&
                StableUnit(seed, weekBucket, n.Id, 0x202) < gateProbNat;
            if (!doDis && !doNat) return;

            PickOrganicForNation(
                c, n,
                doDis ? diseaseDefs : null, freqDis, gateProbDis, out organicDiseasePick[i], out organicDiseaseState[i],
                doNat ? disasterDefs : null, freqNat, gateProbNat, out organicDisasterPick[i], out organicDisasterState[i]);
        });

        if (Campaign.StageOverdue()) yield return 0;

        // Serial deterministic commit in ascending Nation.Id order (reproduces ST == MT bit-for-bit).
        var spreadQueue = scratch.SpreadQueue;
        spreadQueue.Clear();
        var aiEnabled = c.Defines.DiseasesAndDisastersAiEnabled;

        for (var i = 0; i < count; i++)
        {
            if ((i & 15) == 15 && Campaign.StageOverdue()) yield return 0;
            var nation = ordered[i];

            ProgressNationDiseases(c, nation, weekBucket, spreadQueue);
            ProgressNationDisasters(c, nation);

            if (aiEnabled && !c.IsHumanControlled(nation))
                RunAiResponses(c, nation);

            var dIdx = organicDiseasePick[i];
            if (dIdx >= 0 && dIdx < diseaseDefs.Count)
            {
                var targetState = c.StateByMapIndex(organicDiseaseState[i]);
                TryTriggerDisease(c, nation, diseaseDefs[dIdx].Id, targetState, bypassConditions: false);
            }
            var natIdx = organicDisasterPick[i];
            if (natIdx >= 0 && natIdx < disasterDefs.Count)
            {
                var targetState = c.StateByMapIndex(organicDisasterState[i]);
                TryTriggerDisaster(c, nation, disasterDefs[natIdx].Id, targetState, bypassConditions: false);
            }
        }
        ordered.Clear();

        // Apply cross-border / trade contagion deterministically after local progression.
        if (spreadQueue.Count > 0)
        {
            if (spreadQueue.Count > 1)
            {
                spreadQueue.Sort(static (a, b) =>
                {
                    var cmp = a.Target.Id.CompareTo(b.Target.Id);
                    return cmp != 0 ? cmp : string.Compare(a.Def.Id, b.Def.Id, StringComparison.Ordinal);
                });
            }
            for (var i = 0; i < spreadQueue.Count; i++)
            {
                var entry = spreadQueue[i];
                TryTriggerDisease(c, entry.Target, entry.Def.Id, targetState: null, bypassConditions: true, originNationId: entry.OriginId);
            }
            spreadQueue.Clear();
        }
    }

    public static HealthCrisisInstance TryTriggerDisease(
        Campaign c,
        Nation nation,
        string definitionId,
        ScenarioState targetState = null,
        bool bypassConditions = false,
        int originNationId = -1)
    {
        if (!IsEnabled(c) || nation is null || nation.Movement is not null || nation.Population <= 0 || nation.Gdp <= 0f)
            return null;
        var def = DiseaseCatalog.Find(definitionId);
        if (def is null) return null;

        nation.ActiveHealthCrises ??= new List<HealthCrisisInstance>();
        nation.HealthCooldownUntilDay ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (CountActiveDiseases(nation) >= Math.Max(1, c.Defines.MaxActiveDiseasesPerNation))
            return null;
        if (HasActiveDisease(nation, def.Id))
            return null;
        if (nation.HealthCooldownUntilDay.TryGetValue(def.Id, out var cdUntil) && c.ElapsedDays < cdUntil)
            return null;

        if (targetState is null || targetState.NationMapIndex != nation.MapIndex)
            targetState = BestEligibleStateForDisease(c, nation, def, bypassConditions);
        if (targetState is null) return null;

        if (!bypassConditions && !IsRegionEligibleForDisease(c, nation, targetState, def, out _))
            return null;

        var prepMitigation = 1f - 0.30f * M.Clamp01(nation.HealthPreparedness - 0.3f);
        var startSev = M.Clamp(def.StartingSeverity * prepMitigation, 10f, 90f);

        var inst = new HealthCrisisInstance
        {
            Id = Math.Max(1, nation.HealthCrisisSeq++),
            DefinitionId = def.Id,
            NationId = nation.Id,
            AffectedStateMapIndex = targetState.MapIndex,
            OriginNationId = originNationId,
            StartedDay = c.ElapsedDays,
            LastUpdateDay = c.ElapsedDays,
            ResolvedDay = -1,
            DurationDays = def.BaseDurationDays,
            Severity = startSev,
            Response = HealthResponseAction.None,
            ResponseDay = -1,
            ResponseCooldownUntilDay = -1
        };
        nation.ActiveHealthCrises.Add(inst);

        // Initial outbreak stability & fiscal signal.
        nation.Stability = M.Clamp(nation.Stability - def.StabilityHit, 0.05f, 0.99f);
        var initialPenaltyCost = nation.Gdp * def.EconomyCostGdpShare * 0.5f * (startSev / 50f);
        if (initialPenaltyCost > 0.001f)
        {
            IncomePenalties.FromLoss(c, nation, initialPenaltyCost, Math.Clamp(def.BaseDurationDays, 28, 365), def.Id);
            inst.TotalCostBillions += initialPenaltyCost;
        }
        ApplyRecessionShock(c, nation, def.RecessionDepthMax * (startSev / 100f), def.BaseDurationDays);

        if (originNationId >= 0)
            c.Chronicle.Record(c, "health_spread", nation.Id, originNationId, subject: def.NameKey, place: targetState.DisplayName);
        else
            c.Chronicle.Record(c, "health_outbreak", nation.Id, -1, subject: def.NameKey, place: targetState.DisplayName);

        return inst;
    }

    public static NaturalDisasterInstance TryTriggerDisaster(
        Campaign c,
        Nation nation,
        string definitionId,
        ScenarioState targetState = null,
        bool bypassConditions = false)
    {
        if (!IsEnabled(c) || nation is null || nation.Movement is not null || nation.Population <= 0 || nation.Gdp <= 0f)
            return null;
        var def = DisasterCatalog.Find(definitionId);
        if (def is null) return null;

        nation.ActiveDisasters ??= new List<NaturalDisasterInstance>();
        nation.DisasterCooldownUntilDay ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (CountActiveDisasters(nation) >= Math.Max(1, c.Defines.MaxActiveDisastersPerNation))
            return null;
        if (HasActiveDisaster(nation, def.Id))
            return null;
        if (nation.DisasterCooldownUntilDay.TryGetValue(def.Id, out var cdUntil) && c.ElapsedDays < cdUntil)
            return null;

        if (targetState is null || targetState.NationMapIndex != nation.MapIndex)
            targetState = BestEligibleStateForDisaster(c, nation, def, bypassConditions);
        if (targetState is null) return null;

        if (!bypassConditions && !IsRegionEligibleForDisaster(c, nation, targetState, def, out _))
            return null;

        var prepFactor = 1f - 0.35f * M.Clamp01(nation.DisasterPreparedness - 0.25f);
        var severity = M.Clamp(def.StartingSeverity * prepFactor, 12f, 95f);
        var sevNorm = severity / 100f;

        var damage = M.Clamp(def.InstantDamage * (0.5f + 0.6f * sevNorm) * prepFactor, 0f, 0.35f);
        if (damage > 0f)
            targetState.WarDamage = MathF.Min(0.85f, targetState.WarDamage + damage);

        long deaths = 0;
        if (def.InstantDeathsShare > 0f && targetState.Population > 100)
        {
            deaths = (long)Math.Round(targetState.Population * def.InstantDeathsShare * (0.5f + sevNorm) * prepFactor);
            deaths = Math.Clamp(deaths, 0L, Math.Max(0L, targetState.Population / 20));
            deaths = ApplyStateDeaths(c, nation, targetState, deaths);
        }

        var econLoss = nation.Gdp * def.EconomyCostGdpShare * (0.5f + 0.7f * sevNorm);
        if (econLoss > 0.001f)
            IncomePenalties.FromLoss(c, nation, econLoss, Math.Clamp(def.BaseDurationDays, 28, 365), def.Id);

        nation.Stability = M.Clamp(nation.Stability - def.StabilityHit * (0.6f + 0.5f * sevNorm), 0.05f, 0.99f);

        var inst = new NaturalDisasterInstance
        {
            Id = Math.Max(1, nation.DisasterSeq++),
            DefinitionId = def.Id,
            NationId = nation.Id,
            AffectedStateMapIndex = targetState.MapIndex,
            StartedDay = c.ElapsedDays,
            LastUpdateDay = c.ElapsedDays,
            ResolvedDay = -1,
            DurationDays = def.BaseDurationDays,
            Severity = severity,
            Response = DisasterReliefAction.None,
            ResponseDay = -1,
            ResponseCooldownUntilDay = -1,
            InstantDamageApplied = damage,
            TotalDeaths = deaths,
            TotalCostBillions = econLoss
        };
        nation.ActiveDisasters.Add(inst);
        c.Chronicle.Record(c, "natural_disaster", nation.Id, -1, subject: def.NameKey, place: targetState.DisplayName);
        return inst;
    }

    public static bool IsRegionEligibleForDisease(
        Campaign c, Nation n, ScenarioState state, DiseaseDefinition def, out float score)
    {
        score = 0f;
        if (c is null || n is null || state is null || def is null || state.Population <= 0 || state.Unclaimed)
            return false;

        var lat = RegionLatitude(c, n, state);
        var absLat = MathF.Abs(lat);
        if (absLat < def.MinAbsLat || absLat > def.MaxAbsLat) return false;
        if (state.Urbanization < def.MinUrbanization) return false;
        if (def.RequiresWarDamage > 0f && state.WarDamage < def.RequiresWarDamage &&
            HouseholdEconomy.MedicineSupply(n) > 0.55f)
            return false;

        var season = CurrentSeason(c.Date, lat);
        var seasonWeight = SeasonFactor(def.PreferredSeasons, season, absLat);
        if (seasonWeight <= 0f) return false;

        var terrainWeight = RegionTerrainWeight(c, state, def.TerrainWeights);
        var urbanWeight = 0.7f + 0.6f * M.Clamp01(state.Urbanization);
        var med = HouseholdEconomy.MedicineSupply(n);
        var hosp = HouseholdEconomy.HospitalCover(state, state.Buildings.GetValueOrDefault(BuildingKind.Hospital), med);
        var vulnWeight = 1.25f - 0.50f * hosp;
        score = MathF.Max(0.05f, seasonWeight * terrainWeight * urbanWeight * vulnWeight);
        return true;
    }

    public static bool IsRegionEligibleForDisaster(
        Campaign c, Nation n, ScenarioState state, DisasterDefinition def, out float score)
    {
        score = 0f;
        if (c is null || n is null || state is null || def is null || state.Population <= 0 || state.Unclaimed)
            return false;

        var lat = RegionLatitude(c, n, state);
        var absLat = MathF.Abs(lat);
        if (absLat < def.MinAbsLat || absLat > def.MaxAbsLat) return false;

        var access = RegionWaterAccess(c, state);
        if (def.RequiresCoastal && access != WaterAccess.Sea) return false;
        if (def.RequiresInlandWater && access == WaterAccess.None) return false;

        var season = CurrentSeason(c.Date, lat);
        var seasonWeight = SeasonFactor(def.PreferredSeasons, season, absLat);
        if (seasonWeight <= 0f) return false;

        var terrainWeight = RegionTerrainWeight(c, state, def.TerrainWeights);
        score = MathF.Max(0.05f, seasonWeight * terrainWeight);
        return true;
    }

    /// <summary>
    /// Mixes all non-default disease/disaster state into <paramref name="hash"/>.
    /// Emits ZERO extra bytes when no nation has active/cooldown/custom preparedness state
    /// and the feature is enabled, preserving baseline historical campaign fingerprints.
    /// </summary>
    public static void WriteFingerprint(Campaign campaign, ref ulong hash)
    {
        if (campaign is null) return;
        if (!IsEnabled(campaign))
        {
            MixText(ref hash, "diseases_disasters_disabled");
            Mix(ref hash, 0);
        }

        var anyState = false;
        var nations = campaign.Nations;
        for (var i = 0; i < nations.Count; i++)
        {
            var n = nations[i];
            if (n is null) continue;
            if ((n.ActiveHealthCrises?.Count ?? 0) > 0 ||
                (n.ActiveDisasters?.Count ?? 0) > 0 ||
                (n.HealthCooldownUntilDay?.Count ?? 0) > 0 ||
                (n.DisasterCooldownUntilDay?.Count ?? 0) > 0 ||
                n.HealthPreparedness != 0.5f ||
                n.DisasterPreparedness != 0.5f ||
                n.HealthCrisisSeq != 1 ||
                n.DisasterSeq != 1 ||
                n.LastHealthDisasterAiDay >= 0)
            {
                anyState = true;
                break;
            }
        }
        if (!anyState) return;

        MixText(ref hash, "diseases_disasters_v1");
        var fpNations = _fpNationsScratch ??= new List<Nation>();
        fpNations.Clear();
        for (var i = 0; i < nations.Count; i++)
        {
            var x = nations[i];
            if (x is not null) fpNations.Add(x);
        }
        if (fpNations.Count > 1) fpNations.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        for (var ni = 0; ni < fpNations.Count; ni++)
        {
            var n = fpNations[ni];
            if ((n.ActiveHealthCrises?.Count ?? 0) == 0 &&
                (n.ActiveDisasters?.Count ?? 0) == 0 &&
                (n.HealthCooldownUntilDay?.Count ?? 0) == 0 &&
                (n.DisasterCooldownUntilDay?.Count ?? 0) == 0 &&
                n.HealthPreparedness == 0.5f &&
                n.DisasterPreparedness == 0.5f &&
                n.HealthCrisisSeq == 1 &&
                n.DisasterSeq == 1 &&
                n.LastHealthDisasterAiDay < 0)
                continue;

            Mix(ref hash, n.Id);
            MixFloat(ref hash, n.HealthPreparedness);
            MixFloat(ref hash, n.DisasterPreparedness);
            Mix(ref hash, n.HealthCrisisSeq);
            Mix(ref hash, n.DisasterSeq);
            Mix(ref hash, n.LastHealthDisasterAiDay);

            if (n.ActiveHealthCrises is { Count: > 0 })
            {
                Mix(ref hash, n.ActiveHealthCrises.Count);
                var fpCrises = _fpCrisesScratch ??= new List<HealthCrisisInstance>();
                fpCrises.Clear();
                fpCrises.AddRange(n.ActiveHealthCrises);
                if (fpCrises.Count > 1) fpCrises.Sort(static (a, b) => a.Id.CompareTo(b.Id));
                for (var hi = 0; hi < fpCrises.Count; hi++)
                {
                    var hc = fpCrises[hi];
                    Mix(ref hash, hc.Id);
                    MixText(ref hash, hc.DefinitionId);
                    Mix(ref hash, hc.AffectedStateMapIndex);
                    Mix(ref hash, hc.OriginNationId);
                    Mix(ref hash, hc.StartedDay);
                    Mix(ref hash, hc.LastUpdateDay);
                    Mix(ref hash, hc.ResolvedDay);
                    Mix(ref hash, hc.DurationDays);
                    MixFloat(ref hash, hc.Severity);
                    Mix(ref hash, (int)hc.Response);
                    Mix(ref hash, hc.ResponseDay);
                    Mix(ref hash, hc.ResponseCooldownUntilDay);
                    Mix(ref hash, hc.TotalDeaths);
                    MixFloat(ref hash, hc.TotalCostBillions);
                }
                fpCrises.Clear();
            }
            else Mix(ref hash, 0);

            if (n.ActiveDisasters is { Count: > 0 })
            {
                Mix(ref hash, n.ActiveDisasters.Count);
                var fpDisasters = _fpDisastersScratch ??= new List<NaturalDisasterInstance>();
                fpDisasters.Clear();
                fpDisasters.AddRange(n.ActiveDisasters);
                if (fpDisasters.Count > 1) fpDisasters.Sort(static (a, b) => a.Id.CompareTo(b.Id));
                for (var di = 0; di < fpDisasters.Count; di++)
                {
                    var nd = fpDisasters[di];
                    Mix(ref hash, nd.Id);
                    MixText(ref hash, nd.DefinitionId);
                    Mix(ref hash, nd.AffectedStateMapIndex);
                    Mix(ref hash, nd.StartedDay);
                    Mix(ref hash, nd.LastUpdateDay);
                    Mix(ref hash, nd.ResolvedDay);
                    Mix(ref hash, nd.DurationDays);
                    MixFloat(ref hash, nd.Severity);
                    Mix(ref hash, (int)nd.Response);
                    Mix(ref hash, nd.ResponseDay);
                    Mix(ref hash, nd.ResponseCooldownUntilDay);
                    MixFloat(ref hash, nd.InstantDamageApplied);
                    Mix(ref hash, nd.TotalDeaths);
                    MixFloat(ref hash, nd.TotalCostBillions);
                }
                fpDisasters.Clear();
            }
            else Mix(ref hash, 0);

            if (n.HealthCooldownUntilDay is { Count: > 0 })
            {
                Mix(ref hash, n.HealthCooldownUntilDay.Count);
                var fpCd = _fpCooldownsScratch ??= new List<KeyValuePair<string, int>>();
                fpCd.Clear();
                foreach (var kv in n.HealthCooldownUntilDay) fpCd.Add(kv);
                if (fpCd.Count > 1) fpCd.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key));
                for (var ci = 0; ci < fpCd.Count; ci++)
                { MixText(ref hash, fpCd[ci].Key.ToLowerInvariant()); Mix(ref hash, fpCd[ci].Value); }
                fpCd.Clear();
            }
            else Mix(ref hash, 0);

            if (n.DisasterCooldownUntilDay is { Count: > 0 })
            {
                Mix(ref hash, n.DisasterCooldownUntilDay.Count);
                var fpCd = _fpCooldownsScratch ??= new List<KeyValuePair<string, int>>();
                fpCd.Clear();
                foreach (var kv in n.DisasterCooldownUntilDay) fpCd.Add(kv);
                if (fpCd.Count > 1) fpCd.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key));
                for (var ci = 0; ci < fpCd.Count; ci++)
                { MixText(ref hash, fpCd[ci].Key.ToLowerInvariant()); Mix(ref hash, fpCd[ci].Value); }
                fpCd.Clear();
            }
            else Mix(ref hash, 0);
        }
        fpNations.Clear();
    }

    [ThreadStatic] private static List<Nation> _fpNationsScratch;
    [ThreadStatic] private static List<HealthCrisisInstance> _fpCrisesScratch;
    [ThreadStatic] private static List<NaturalDisasterInstance> _fpDisastersScratch;
    [ThreadStatic] private static List<KeyValuePair<string, int>> _fpCooldownsScratch;

    private static ScenarioState FirstStateOf(Campaign c, Nation n)
    {
        var states = c.StatesOf(n);
        return states.Length > 0 ? states[0] : null;
    }

    public static ulong ComputeFingerprint(Campaign campaign)
    {
        var hash = 14695981039346656037UL;
        WriteFingerprint(campaign, ref hash);
        return hash;
    }

    // ---- Internal simulation logic ---------------------------------------------------------------

    private static void ProgressNationDiseases(
        Campaign c,
        Nation n,
        int weekBucket,
        List<(Nation Target, DiseaseDefinition Def, int OriginId)> spreadQueue)
    {
        if (n.ActiveHealthCrises is null || n.ActiveHealthCrises.Count == 0) return;
        var medicine = HouseholdEconomy.MedicineSupply(n);
        var spreadMult = MathF.Max(0f, c.Defines.DiseaseSpreadMultiplier);

        for (var i = 0; i < n.ActiveHealthCrises.Count; i++)
        {
            var inst = n.ActiveHealthCrises[i];
            if (inst is null || !inst.IsActive || inst.LastUpdateDay == c.ElapsedDays) continue;

            var def = DiseaseCatalog.Find(inst.DefinitionId);
            if (def is null)
            {
                inst.ResolvedDay = c.ElapsedDays;
                continue;
            }

            var state = c.StateByMapIndex(inst.AffectedStateMapIndex) ?? FirstStateOf(c, n);
            if (state is not null && state.NationMapIndex != n.MapIndex)
                state = FirstStateOf(c, n);
            if (state is not null)
                inst.AffectedStateMapIndex = state.MapIndex;

            var hospitalCov = state is not null
                ? HouseholdEconomy.HospitalCover(state, state.Buildings.GetValueOrDefault(BuildingKind.Hospital), medicine)
                : 0.4f;

            // Severity evolution: rises early without intervention, recedes past half-duration or under response.
            var elapsed = Math.Max(0, c.ElapsedDays - inst.StartedDay);
            var progress = inst.DurationDays > 0 ? (float)elapsed / inst.DurationDays : 1f;
            var drift = def.WeeklySeverityDrift * (progress < 0.45f ? 1f : -1.15f);
            drift -= hospitalCov * 2.4f + (n.HealthPreparedness - 0.5f) * 2.0f;
            switch (inst.Response)
            {
                case HealthResponseAction.TargetedQuarantine:
                    drift -= 3.2f;
                    break;
                case HealthResponseAction.MassVaccinationAndMedicalDrive:
                    drift -= 6.5f + 2.5f * medicine;
                    break;
                case HealthResponseAction.EmergencyHealthSubsidies:
                    drift -= 2.8f;
                    break;
                case HealthResponseAction.InternationalMedicalCooperation:
                    drift -= 4.2f;
                    break;
            }
            inst.Severity = M.Clamp(inst.Severity + drift, 0f, 100f);
            inst.LastUpdateDay = c.ElapsedDays;

            var sevNorm = inst.Severity / 100f;
            // Demographic mortality (strictly bounded and scaled by hospitals + medicine + active response).
            if (state is not null && state.Population > 500 && def.MortalityAnnualAtMax > 0f)
            {
                var responseDeathMult = inst.Response switch
                {
                    HealthResponseAction.MassVaccinationAndMedicalDrive => 0.35f,
                    HealthResponseAction.EmergencyHealthSubsidies => 0.65f,
                    HealthResponseAction.TargetedQuarantine => 0.72f,
                    HealthResponseAction.InternationalMedicalCooperation => 0.60f,
                    _ => 1.0f
                };
                var hospRelief = 1f - 0.45f * hospitalCov;
                var weeklyMortality = def.MortalityAnnualAtMax * sevNorm * (1f / 52f) * hospRelief * responseDeathMult;
                var deaths = (long)Math.Round(state.Population * (double)weeklyMortality);
                deaths = Math.Clamp(deaths, 0L, Math.Max(0L, state.Population / 100));
                deaths = ApplyStateDeaths(c, n, state, deaths);
                if (deaths > 0)
                    inst.TotalDeaths += deaths;
            }

            // Stability, living standards, and radicalism impacts.
            var stabScale = inst.Response == HealthResponseAction.EmergencyHealthSubsidies ? 0.45f : 1.0f;
            n.Stability = M.Clamp(n.Stability - def.WeeklyStabilityHit * sevNorm * stabScale, 0.05f, 0.99f);
            if (state is not null)
            {
                state.Radicalism = M.Clamp01(state.Radicalism + def.WeeklyRadicalismHit * sevNorm * stabScale);
                state.StandardOfLiving = M.Clamp01(state.StandardOfLiving - def.WeeklyLivingHit * sevNorm * stabScale);
            }

            // Cross-border / trade contagion check while outbreak is significant.
            var spreadRisk = ComputeSpreadRisk(def, inst, n) * spreadMult;
            if (spreadRisk > 0.02f && inst.Severity >= 30f)
            {
                var neighborRoll = StableUnit(c.Seed, weekBucket, n.Id, inst.Id * 37 + 11);
                if (neighborRoll < spreadRisk)
                {
                    var neighbor = PickContagionTarget(c, n, weekBucket, inst.Id);
                    if (neighbor is not null)
                        spreadQueue.Add((neighbor, def, n.Id));
                }
            }

            if (inst.Severity <= 5f || elapsed >= inst.DurationDays)
                ResolveDisease(c, n, inst, def);
        }

        TrimResolvedCrises(n);
    }

    private static void ProgressNationDisasters(Campaign c, Nation n)
    {
        if (n.ActiveDisasters is null || n.ActiveDisasters.Count == 0) return;

        for (var i = 0; i < n.ActiveDisasters.Count; i++)
        {
            var inst = n.ActiveDisasters[i];
            if (inst is null || !inst.IsActive || inst.LastUpdateDay == c.ElapsedDays) continue;

            var def = DisasterCatalog.Find(inst.DefinitionId);
            if (def is null)
            {
                inst.ResolvedDay = c.ElapsedDays;
                continue;
            }

            var state = c.StateByMapIndex(inst.AffectedStateMapIndex) ?? FirstStateOf(c, n);
            if (state is not null && state.NationMapIndex != n.MapIndex)
                state = FirstStateOf(c, n);
            if (state is not null)
                inst.AffectedStateMapIndex = state.MapIndex;

            var recovery = def.WeeklyRecoveryRate + (n.DisasterPreparedness - 0.5f) * 2.5f;
            switch (inst.Response)
            {
                case DisasterReliefAction.EmergencyEvacuationAndRescue:
                    recovery += 7.5f;
                    break;
                case DisasterReliefAction.InfrastructureReconstruction:
                    recovery += 8.0f;
                    break;
                case DisasterReliefAction.StrategicReserveAid:
                    recovery += 5.5f;
                    break;
                case DisasterReliefAction.CivilDefenseMobilization:
                    recovery += 6.5f;
                    break;
            }
            inst.Severity = M.Clamp(inst.Severity - recovery, 0f, 100f);
            inst.LastUpdateDay = c.ElapsedDays;

            var sevNorm = inst.Severity / 100f;
            if (state is not null && state.Population > 500 && def.WeeklyDeathsAnnual > 0f)
            {
                var deathMult = inst.Response switch
                {
                    DisasterReliefAction.EmergencyEvacuationAndRescue => 0.35f,
                    DisasterReliefAction.StrategicReserveAid => 0.55f,
                    DisasterReliefAction.CivilDefenseMobilization => 0.60f,
                    DisasterReliefAction.InfrastructureReconstruction => 0.70f,
                    _ => 1.0f
                };
                var deaths = (long)Math.Round(state.Population * (double)(def.WeeklyDeathsAnnual * sevNorm * (1f / 52f) * deathMult));
                deaths = Math.Clamp(deaths, 0L, Math.Max(0L, state.Population / 120));
                deaths = ApplyStateDeaths(c, n, state, deaths);
                if (deaths > 0)
                    inst.TotalDeaths += deaths;
            }

            var stabMult = inst.Response == DisasterReliefAction.StrategicReserveAid ? 0.45f : 1.0f;
            n.Stability = M.Clamp(n.Stability - def.WeeklyStabilityHit * sevNorm * stabMult, 0.05f, 0.99f);
            if (state is not null)
                state.Radicalism = M.Clamp01(state.Radicalism + def.WeeklyRadicalismHit * sevNorm * stabMult);

            var elapsed = Math.Max(0, c.ElapsedDays - inst.StartedDay);
            if (inst.Severity <= 5f || elapsed >= inst.DurationDays)
                ResolveDisaster(c, n, inst, def);
        }

        TrimResolvedDisasters(n);
    }

    private static void RunAiResponses(Campaign c, Nation n)
    {
        if (n.LastHealthDisasterAiDay >= 0 && c.ElapsedDays - n.LastHealthDisasterAiDay < 14)
            return;

        var acted = false;
        if (n.ActiveHealthCrises is not null)
        {
            for (var i = 0; i < n.ActiveHealthCrises.Count; i++)
            {
                var hc = n.ActiveHealthCrises[i];
                if (hc is null || !hc.IsActive || hc.Response != HealthResponseAction.None) continue;
                var action = ScoreBestHealthAction(c, n, hc);
                if (action != HealthResponseAction.None && RespondHealth(c, n, hc.Id, action))
                    acted = true;
            }
        }

        if (n.ActiveDisasters is not null)
        {
            for (var i = 0; i < n.ActiveDisasters.Count; i++)
            {
                var nd = n.ActiveDisasters[i];
                if (nd is null || !nd.IsActive || nd.Response != DisasterReliefAction.None) continue;
                var action = ScoreBestDisasterAction(c, n, nd);
                if (action != DisasterReliefAction.None && RespondDisaster(c, n, nd.Id, action))
                    acted = true;
            }
        }

        if (acted) n.LastHealthDisasterAiDay = c.ElapsedDays;
    }

    private static HealthResponseAction ScoreBestHealthAction(Campaign c, Nation n, HealthCrisisInstance hc)
    {
        var def = DiseaseCatalog.Find(hc.DefinitionId);
        var best = HealthResponseAction.None;
        var bestScore = float.NegativeInfinity;
        for (var i = 0; i < AllHealthActions.Length; i++)
        {
            var act = AllHealthActions[i];
            if (!CanRespondHealth(c, n, hc.Id, act, out _)) continue;
            HealthActionCost(c, n, hc.Id, act, out var cash, out _);
            var cashRatio = cash / MathF.Max(0.1f, n.Treasury);
            var score = act switch
            {
                HealthResponseAction.MassVaccinationAndMedicalDrive => 40f + hc.Severity * 0.45f + HouseholdEconomy.MedicineSupply(n) * 18f,
                HealthResponseAction.EmergencyHealthSubsidies => 32f + (n.Stability < 0.55f ? 32f : 8f),
                HealthResponseAction.TargetedQuarantine => 26f + (def?.SpreadRate ?? 0.25f) * 35f + (cashRatio > 0.4f ? 18f : 0f),
                HealthResponseAction.InternationalMedicalCooperation => 24f + (cashRatio > 0.35f ? 22f : 4f) + n.Notoriety * 0.2f,
                _ => 0f
            };
            score -= cashRatio * 24f;
            if (score > bestScore)
            {
                bestScore = score;
                best = act;
            }
        }
        return best;
    }

    private static DisasterReliefAction ScoreBestDisasterAction(Campaign c, Nation n, NaturalDisasterInstance nd)
    {
        var state = c.StateByMapIndex(nd.AffectedStateMapIndex);
        var best = DisasterReliefAction.None;
        var bestScore = float.NegativeInfinity;
        for (var i = 0; i < AllDisasterActions.Length; i++)
        {
            var act = AllDisasterActions[i];
            if (!CanRespondDisaster(c, n, nd.Id, act, out _)) continue;
            DisasterActionCost(c, n, nd.Id, act, out var cash, out _);
            var cashRatio = cash / MathF.Max(0.1f, n.Treasury);
            var score = act switch
            {
                DisasterReliefAction.EmergencyEvacuationAndRescue => 38f + nd.Severity * 0.4f,
                DisasterReliefAction.InfrastructureReconstruction => 34f + (state?.WarDamage ?? 0f) * 80f,
                DisasterReliefAction.StrategicReserveAid => 30f + (n.Stability < 0.55f ? 30f : 6f) + (state?.Radicalism ?? 0f) * 25f,
                DisasterReliefAction.CivilDefenseMobilization => 25f + (cashRatio > 0.35f ? 26f : 4f),
                _ => 0f
            };
            score -= cashRatio * 22f;
            if (score > bestScore)
            {
                bestScore = score;
                best = act;
            }
        }
        return best;
    }

    private struct StateGeoCache
    {
        public bool Initialized;
        public bool Valid;
        public float Lat;
        public float AbsLat;
        public string Season;
        public float UrbanWeight;
        public bool HasHosp;
        public float Hosp;
        public bool HasWater;
        public WaterAccess Water;
        public bool HasTerrain;
        public int TerrainStart;
        public int TerrainCount;
    }

    [ThreadStatic] private static StateGeoCache[] _stateGeoScratch;
    [ThreadStatic] private static List<string> _terrainSamplesScratch;
    [ThreadStatic] private static List<Nation> _contagionCandidatesScratch;
    [ThreadStatic] private static HashSet<int> _contagionSeenScratch;

    private static StateGeoCache[] EnsureGeoScratch(int len)
    {
        var arr = _stateGeoScratch;
        if (arr is null || arr.Length < len)
            arr = _stateGeoScratch = new StateGeoCache[Math.Max(len, 32)];
        Array.Clear(arr, 0, len);
        return arr;
    }

    private static ref StateGeoCache PrepareStateGeo(Campaign c, Nation n, ScenarioState st, StateGeoCache[] cache, int idx)
    {
        ref var geo = ref cache[idx];
        if (geo.Initialized) return ref geo;
        geo.Initialized = true;
        if (st is null || st.Population <= 0 || st.Unclaimed)
        {
            geo.Valid = false;
            return ref geo;
        }
        geo.Valid = true;
        geo.Lat = RegionLatitude(c, n, st);
        geo.AbsLat = MathF.Abs(geo.Lat);
        geo.Season = CurrentSeason(c.Date, geo.Lat);
        geo.UrbanWeight = 0.7f + 0.6f * M.Clamp01(st.Urbanization);
        return ref geo;
    }

    private static float CachedTerrainWeight(
        Campaign c,
        ScenarioState st,
        Dictionary<string, float> weights,
        ref StateGeoCache geo,
        List<string> samples)
    {
        if (weights is null || weights.Count == 0) return 1f;
        if (!geo.HasTerrain)
        {
            geo.HasTerrain = true;
            var map = c?.ProvinceMap;
            if (map is not null && map.TerrainByProvince is { Length: > 0 })
            {
                var provs = map.ProvincesOfState(st.MapIndex);
                if (provs.Length > 0)
                {
                    geo.TerrainStart = samples.Count;
                    var step = provs.Length <= 16 ? 1 : Math.Max(1, provs.Length / 16);
                    for (var i = 0; i < provs.Length; i += step)
                        samples.Add(map.TerrainAt(provs[i]).Id);
                    geo.TerrainCount = samples.Count - geo.TerrainStart;
                }
            }
        }
        var count = geo.TerrainCount;
        if (count <= 0) return 1f;
        var sum = 0f;
        var start = geo.TerrainStart;
        for (var i = 0; i < count; i++)
            sum += weights.TryGetValue(samples[start + i], out var w) ? w : 0.65f;
        return sum / count;
    }

    private static void PickOrganicForNation(
        Campaign c,
        Nation n,
        IReadOnlyList<DiseaseDefinition> diseaseDefs,
        float freqDis,
        float gateProbDis,
        out int pickedDisDef,
        out int pickedDisState,
        IReadOnlyList<DisasterDefinition> disasterDefs,
        float freqNat,
        float gateProbNat,
        out int pickedNatDef,
        out int pickedNatState)
    {
        pickedDisDef = -1;
        pickedDisState = 0;
        pickedNatDef = -1;
        pickedNatState = 0;

        var states = c.StatesOf(n);
        if (states.Length == 0) return;

        var disAny = false;
        if (diseaseDefs is not null)
        {
            for (var d = 0; d < diseaseDefs.Count; d++)
            {
                var def = diseaseDefs[d];
                if (def.AnnualChance <= 0f || HasActiveDisease(n, def.Id)) continue;
                if (n.HealthCooldownUntilDay is not null &&
                    n.HealthCooldownUntilDay.TryGetValue(def.Id, out var cd) && c.ElapsedDays < cd) continue;
                disAny = true;
                break;
            }
        }

        var natAny = false;
        if (disasterDefs is not null)
        {
            for (var d = 0; d < disasterDefs.Count; d++)
            {
                var def = disasterDefs[d];
                if (def.AnnualChance <= 0f || HasActiveDisaster(n, def.Id)) continue;
                if (n.DisasterCooldownUntilDay is not null &&
                    n.DisasterCooldownUntilDay.TryGetValue(def.Id, out var cd) && c.ElapsedDays < cd) continue;
                natAny = true;
                break;
            }
        }

        if (!disAny && !natAny) return;

        var geoCache = EnsureGeoScratch(states.Length);
        var terrainSamples = _terrainSamplesScratch ??= new List<string>(64);
        terrainSamples.Clear();
        var weekBucket = c.ElapsedDays / 7;
        var med = -1f;

        if (disAny)
        {
            var roll = StableUnit(c.Seed, weekBucket, n.Id, 0x331);
            var denom = MathF.Max(0.01f, gateProbDis);
            for (var d = 0; d < diseaseDefs.Count; d++)
            {
                var def = diseaseDefs[d];
                if (def.AnnualChance <= 0f || HasActiveDisease(n, def.Id)) continue;
                if (n.HealthCooldownUntilDay is not null &&
                    n.HealthCooldownUntilDay.TryGetValue(def.Id, out var cd) && c.ElapsedDays < cd) continue;

                ScenarioState bestState = null;
                var bestScore = 0f;
                for (var s = 0; s < states.Length; s++)
                {
                    var st = states[s];
                    ref var geo = ref PrepareStateGeo(c, n, st, geoCache, s);
                    if (!geo.Valid) continue;
                    if (geo.AbsLat < def.MinAbsLat || geo.AbsLat > def.MaxAbsLat) continue;
                    if (st.Urbanization < def.MinUrbanization) continue;
                    if (def.RequiresWarDamage > 0f && st.WarDamage < def.RequiresWarDamage)
                    {
                        if (med < 0f) med = HouseholdEconomy.MedicineSupply(n);
                        if (med > 0.55f) continue;
                    }
                    var seasonWeight = SeasonFactor(def.PreferredSeasons, geo.Season, geo.AbsLat);
                    if (seasonWeight <= 0f) continue;

                    var terrainWeight = CachedTerrainWeight(c, st, def.TerrainWeights, ref geo, terrainSamples);
                    if (!geo.HasHosp)
                    {
                        if (med < 0f) med = HouseholdEconomy.MedicineSupply(n);
                        geo.Hosp = HouseholdEconomy.HospitalCover(st, st.Buildings.GetValueOrDefault(BuildingKind.Hospital), med);
                        geo.HasHosp = true;
                    }
                    var vulnWeight = 1.25f - 0.50f * geo.Hosp;
                    var score = MathF.Max(0.05f, seasonWeight * terrainWeight * geo.UrbanWeight * vulnWeight);
                    var popWeight = 0.7f + 0.3f * MathF.Min(1f, st.Population / 15_000_000f);
                    var combined = score * popWeight;
                    if (combined > bestScore)
                    {
                        bestScore = combined;
                        bestState = st;
                    }
                }
                if (bestState is null) continue;
                var weeklyProb = M.Clamp01((def.AnnualChance * freqDis * bestScore / 52f) / denom);
                if (roll < weeklyProb)
                {
                    pickedDisDef = d;
                    pickedDisState = bestState.MapIndex;
                    break;
                }
                roll -= weeklyProb;
                if (roll <= 0f) break;
            }
        }

        if (natAny)
        {
            var roll = StableUnit(c.Seed, weekBucket, n.Id, 0x779);
            var denom = MathF.Max(0.01f, gateProbNat);
            for (var d = 0; d < disasterDefs.Count; d++)
            {
                var def = disasterDefs[d];
                if (def.AnnualChance <= 0f || HasActiveDisaster(n, def.Id)) continue;
                if (n.DisasterCooldownUntilDay is not null &&
                    n.DisasterCooldownUntilDay.TryGetValue(def.Id, out var cd) && c.ElapsedDays < cd) continue;

                ScenarioState bestState = null;
                var bestScore = 0f;
                for (var s = 0; s < states.Length; s++)
                {
                    var st = states[s];
                    ref var geo = ref PrepareStateGeo(c, n, st, geoCache, s);
                    if (!geo.Valid) continue;
                    if (geo.AbsLat < def.MinAbsLat || geo.AbsLat > def.MaxAbsLat) continue;
                    if (def.RequiresCoastal || def.RequiresInlandWater)
                    {
                        if (!geo.HasWater)
                        {
                            geo.Water = RegionWaterAccess(c, st);
                            geo.HasWater = true;
                        }
                        if (def.RequiresCoastal && geo.Water != WaterAccess.Sea) continue;
                        if (def.RequiresInlandWater && geo.Water == WaterAccess.None) continue;
                    }
                    var seasonWeight = SeasonFactor(def.PreferredSeasons, geo.Season, geo.AbsLat);
                    if (seasonWeight <= 0f) continue;

                    var terrainWeight = CachedTerrainWeight(c, st, def.TerrainWeights, ref geo, terrainSamples);
                    var score = MathF.Max(0.05f, seasonWeight * terrainWeight);
                    var tieBreaker = StableUnit(c.Seed, weekBucket, st.MapIndex, d + 17) * 0.25f;
                    var combined = score + tieBreaker;
                    if (combined > bestScore)
                    {
                        bestScore = combined;
                        bestState = st;
                    }
                }
                if (bestState is null) continue;
                var weeklyProb = M.Clamp01((def.AnnualChance * freqNat * bestScore / 52f) / denom);
                if (roll < weeklyProb)
                {
                    pickedNatDef = d;
                    pickedNatState = bestState.MapIndex;
                    break;
                }
                roll -= weeklyProb;
                if (roll <= 0f) break;
            }
        }
    }

    private static ScenarioState BestEligibleStateForDisease(Campaign c, Nation n, DiseaseDefinition def, bool bypass)
    {
        var states = c.StatesOf(n);
        ScenarioState best = null;
        var bestScore = -1f;
        for (var i = 0; i < states.Length; i++)
        {
            var st = states[i];
            if (st is null || st.Population <= 0 || st.Unclaimed) continue;
            if (IsRegionEligibleForDisease(c, n, st, def, out var s))
            {
                if (s > bestScore) { bestScore = s; best = st; }
            }
            else if (bypass && best is null)
            {
                best = st;
            }
        }
        return best;
    }

    private static ScenarioState BestEligibleStateForDisaster(Campaign c, Nation n, DisasterDefinition def, bool bypass)
    {
        var states = c.StatesOf(n);
        ScenarioState best = null;
        var bestScore = -1f;
        for (var i = 0; i < states.Length; i++)
        {
            var st = states[i];
            if (st is null || st.Population <= 0 || st.Unclaimed) continue;
            if (IsRegionEligibleForDisaster(c, n, st, def, out var s))
            {
                if (s > bestScore) { bestScore = s; best = st; }
            }
            else if (bypass && best is null)
            {
                best = st;
            }
        }
        return best;
    }

    private static Nation PickContagionTarget(Campaign c, Nation source, int weekBucket, int crisisId)
    {
        var candidates = _contagionCandidatesScratch ??= new List<Nation>(16);
        candidates.Clear();
        var seen = _contagionSeenScratch ??= new HashSet<int>();
        seen.Clear();
        // 1. Land neighbors
        var neighborMaps = CampaignDiplomacy.Neighbours(c, source.MapIndex);
        if (neighborMaps.Count > 0)
        {
            foreach (var mapIdx in neighborMaps)
            {
                var other = c.NationByMapIndex(mapIdx);
                if (other is null || other.Id == source.Id || other.Movement is not null || other.Population <= 0 || other.Gdp <= 0f)
                    continue;
                var rel = CampaignDiplomacy.Peek(c, source.Id, other.Id);
                if (rel is not null && (rel.EmbargoByA || rel.EmbargoByB)) continue;
                if (seen.Add(other.Id)) candidates.Add(other);
            }
        }
        // 2. Active trade partners
        var routes = c.TradeRoutes;
        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            if (route.Value <= 0f) continue;
            var otherId = route.SellerId == source.Id ? route.BuyerId : route.BuyerId == source.Id ? route.SellerId : -1;
            if (otherId < 0 || seen.Contains(otherId)) continue;
            var other = c.NationById(otherId);
            if (other is not null && other.Movement is null && other.Population > 0 && other.Gdp > 0f && seen.Add(otherId))
                candidates.Add(other);
        }
        if (candidates.Count == 0) return null;
        if (candidates.Count > 1) candidates.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        var idx = (int)(StableHash(c.Seed, weekBucket, source.Id, crisisId) % (uint)candidates.Count);
        var chosen = candidates[idx];
        candidates.Clear();
        seen.Clear();
        return chosen;
    }

    private static float ComputeSpreadRisk(DiseaseDefinition def, HealthCrisisInstance inst, Nation n)
    {
        if (def is null || inst is null || !inst.IsActive) return 0f;
        var baseRisk = def.SpreadRate * M.Clamp01(inst.Severity / 100f);
        var mult = inst.Response switch
        {
            HealthResponseAction.TargetedQuarantine => 0.35f,
            HealthResponseAction.InternationalMedicalCooperation => 0.45f,
            HealthResponseAction.MassVaccinationAndMedicalDrive => 0.55f,
            HealthResponseAction.EmergencyHealthSubsidies => 0.85f,
            _ => 1.0f
        };
        if (n is not null) mult *= 1.15f - 0.30f * M.Clamp01(n.HealthPreparedness);
        return M.Clamp01(baseRisk * mult * 0.35f);
    }

    private static long ApplyStateDeaths(Campaign c, Nation n, ScenarioState state, long deaths)
    {
        if (state is null || deaths <= 0) return 0;
        var before = state.Population;
        EconomySystem.AdjustStatePopulation(state, -deaths);
        var actual = Math.Max(0L, before - state.Population);
        if (n is not null && actual > 0)
        {
            var states = c.StatesOf(n);
            if (states.Length > 0)
            {
                var total = 0L;
                for (var i = 0; i < states.Length; i++)
                    total += Math.Max(0L, states[i].Population);
                n.Population = total;
            }
            else
            {
                n.Population = Math.Max(0L, n.Population - actual);
            }
        }
        return actual;
    }

    private static void ResolveDisease(Campaign c, Nation n, HealthCrisisInstance inst, DiseaseDefinition def)
    {
        if (!inst.IsActive) return;
        inst.ResolvedDay = c.ElapsedDays;
        inst.LastUpdateDay = c.ElapsedDays;
        inst.Severity = M.Clamp(inst.Severity, 0f, 100f);
        n.HealthCooldownUntilDay ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (def is not null)
            n.HealthCooldownUntilDay[def.Id] = c.ElapsedDays + def.CooldownDays;
        var state = c.StateByMapIndex(inst.AffectedStateMapIndex);
        c.Chronicle.Record(c, "health_contained", n.Id, -1,
            subject: def?.NameKey ?? inst.DefinitionId, place: state?.DisplayName ?? n.CapitalName);
    }

    private static void ResolveDisaster(Campaign c, Nation n, NaturalDisasterInstance inst, DisasterDefinition def)
    {
        if (!inst.IsActive) return;
        inst.ResolvedDay = c.ElapsedDays;
        inst.LastUpdateDay = c.ElapsedDays;
        inst.Severity = M.Clamp(inst.Severity, 0f, 100f);
        n.DisasterCooldownUntilDay ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (def is not null)
            n.DisasterCooldownUntilDay[def.Id] = c.ElapsedDays + def.CooldownDays;
        var state = c.StateByMapIndex(inst.AffectedStateMapIndex);
        c.Chronicle.Record(c, "disaster_recovered", n.Id, -1,
            subject: def?.NameKey ?? inst.DefinitionId, place: state?.DisplayName ?? n.CapitalName);
    }

    private static void ApplyRecessionShock(Campaign c, Nation n, float depth, int days)
    {
        depth = M.Clamp(depth, 0f, 0.25f);
        if (depth <= 0f) return;
        if (n.RecessionDepth <= 0f || c.ElapsedDays >= n.RecessionUntilDay)
        {
            n.RecessionDepth = depth;
            n.RecessionStartDay = c.ElapsedDays;
            n.RecessionUntilDay = c.ElapsedDays + Math.Max(28, days);
        }
        else
        {
            n.RecessionDepth = M.Clamp(MathF.Max(n.RecessionDepth, depth) + MathF.Min(n.RecessionDepth, depth) * 0.35f, 0f, 0.35f);
            n.RecessionUntilDay = Math.Max(n.RecessionUntilDay, c.ElapsedDays + Math.Max(28, days / 2));
        }
    }

    private static void TrimResolvedCrises(Nation n)
    {
        if (n.ActiveHealthCrises is null) return;
        var resolved = 0;
        for (var i = 0; i < n.ActiveHealthCrises.Count; i++)
            if (!n.ActiveHealthCrises[i].IsActive) resolved++;
        if (resolved <= MaxResolvedHistoryPerNation) return;
        var drop = resolved - MaxResolvedHistoryPerNation;
        for (var i = 0; i < n.ActiveHealthCrises.Count && drop > 0; i++)
        {
            if (!n.ActiveHealthCrises[i].IsActive)
            {
                n.ActiveHealthCrises.RemoveAt(i);
                i--;
                drop--;
            }
        }
    }

    private static void TrimResolvedDisasters(Nation n)
    {
        if (n.ActiveDisasters is null) return;
        var resolved = 0;
        for (var i = 0; i < n.ActiveDisasters.Count; i++)
            if (!n.ActiveDisasters[i].IsActive) resolved++;
        if (resolved <= MaxResolvedHistoryPerNation) return;
        var drop = resolved - MaxResolvedHistoryPerNation;
        for (var i = 0; i < n.ActiveDisasters.Count && drop > 0; i++)
        {
            if (!n.ActiveDisasters[i].IsActive)
            {
                n.ActiveDisasters.RemoveAt(i);
                i--;
                drop--;
            }
        }
    }

    private static int CountActiveDiseases(Nation n)
    {
        var list = n.ActiveHealthCrises;
        if (list is null || list.Count == 0) return 0;
        var count = 0;
        for (var i = 0; i < list.Count; i++)
            if (list[i] is { IsActive: true }) count++;
        return count;
    }

    private static int CountActiveDisasters(Nation n)
    {
        var list = n.ActiveDisasters;
        if (list is null || list.Count == 0) return 0;
        var count = 0;
        for (var i = 0; i < list.Count; i++)
            if (list[i] is { IsActive: true }) count++;
        return count;
    }

    private static bool HasActiveDisease(Nation n, string defId)
    {
        var list = n.ActiveHealthCrises;
        if (list is null) return false;
        for (var i = 0; i < list.Count; i++)
            if (list[i] is { IsActive: true } hc && string.Equals(hc.DefinitionId, defId, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool HasActiveDisaster(Nation n, string defId)
    {
        var list = n.ActiveDisasters;
        if (list is null) return false;
        for (var i = 0; i < list.Count; i++)
            if (list[i] is { IsActive: true } nd && string.Equals(nd.DefinitionId, defId, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static HealthCrisisInstance FindHealthCrisis(Nation n, int id)
    {
        var list = n?.ActiveHealthCrises;
        if (list is null) return null;
        for (var i = 0; i < list.Count; i++)
            if (list[i] is { } item && item.Id == id) return item;
        return null;
    }

    private static NaturalDisasterInstance FindDisaster(Nation n, int id)
    {
        var list = n?.ActiveDisasters;
        if (list is null) return null;
        for (var i = 0; i < list.Count; i++)
            if (list[i] is { } item && item.Id == id) return item;
        return null;
    }

    public static float RegionLatitude(Campaign c, Nation n, ScenarioState state)
    {
        if (c?.ProvinceMap is not null && state is not null)
        {
            var (lat, lon) = LandAtlas.CentreOf(c, state.MapIndex);
            if (lat != 0f || lon != 0f) return lat;
        }
        return n?.CapitalLatitude ?? 0f;
    }

    public static string CurrentSeason(GameDate date, float latitude)
    {
        var m = Math.Clamp(date.Month, 1, 12);
        if (MathF.Abs(latitude) < 20f)
        {
            var northernWet = m >= 5 && m <= 10;
            return (latitude >= 0f ? northernWet : !northernWet) ? "wet" : "dry";
        }
        var shifted = latitude >= 0f ? m : ((m + 5) % 12) + 1;
        return shifted switch
        {
            12 or 1 or 2 => "winter",
            3 or 4 or 5 => "spring",
            6 or 7 or 8 => "summer",
            _ => "autumn"
        };
    }

    private static float SeasonFactor(string[] preferred, string season, float absLat)
    {
        if (preferred is null || preferred.Length == 0) return 1.0f;
        for (var i = 0; i < preferred.Length; i++)
            if (string.Equals(preferred[i], "any", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(preferred[i], season, StringComparison.OrdinalIgnoreCase))
                return 1.3f;

        if (absLat < 38f)
        {
            for (var i = 0; i < preferred.Length; i++)
            {
                var p = preferred[i];
                if (string.Equals(p, "wet", StringComparison.OrdinalIgnoreCase) && season is "spring" or "autumn")
                    return 1.05f;
                if (string.Equals(p, "dry", StringComparison.OrdinalIgnoreCase) && season is "summer" or "autumn")
                    return 1.1f;
            }
        }
        return 0f;
    }

    private static float RegionTerrainWeight(Campaign c, ScenarioState state, Dictionary<string, float> weights)
    {
        if (weights is null || weights.Count == 0) return 1f;
        var map = c?.ProvinceMap;
        if (map is null || map.TerrainByProvince is null || map.TerrainByProvince.Length == 0)
            return 1f;

        var provs = map.ProvincesOfState(state.MapIndex);
        if (provs.Length == 0) return 1f;

        var step = provs.Length <= 16 ? 1 : Math.Max(1, provs.Length / 16);
        var sum = 0f;
        var samples = 0;
        for (var i = 0; i < provs.Length; i += step)
        {
            var tid = map.TerrainAt(provs[i]).Id;
            sum += weights.TryGetValue(tid, out var w) ? w : 0.65f;
            samples++;
        }
        return samples > 0 ? sum / samples : 1f;
    }

    private static WaterAccess RegionWaterAccess(Campaign c, ScenarioState state)
    {
        if (state is null) return WaterAccess.None;
        if (state.Buildings.GetValueOrDefault(BuildingKind.Port) > 0 ||
            state.Buildings.GetValueOrDefault(BuildingKind.Shipyard) > 0)
            return WaterAccess.Sea;

        if (c?.ProvinceMap is not null)
        {
            var access = Waterfront.OfRegion(c, state);
            if (access != WaterAccess.None) return access;
        }
        return WaterAccess.None;
    }

    private static uint StableHash(int seed, int a, int b, int c)
    {
        unchecked
        {
            var h = (uint)seed ^ 2166136261u;
            h = (h ^ (uint)a) * 16777619u;
            h = (h ^ (uint)b) * 16777619u;
            h = (h ^ (uint)c) * 16777619u;
            h ^= h >> 16;
            h *= 2246822507u;
            h ^= h >> 13;
            return h;
        }
    }

    private static float StableUnit(int seed, int a, int b, int c) =>
        (StableHash(seed, a, b, c) >> 8) * (1f / 16777216f);

    private static string FmtMoney(float billions) =>
        billions >= 1f ? $"{billions:0.00}B" : $"{billions * 1000f:0}M";

    private static void Mix(ref ulong hash, long value)
    {
        for (var shift = 0; shift < 64; shift += 8)
            hash = (hash ^ (byte)(value >> shift)) * 1099511628211UL;
    }

    private static void MixFloat(ref ulong hash, float value) =>
        Mix(ref hash, BitConverter.SingleToInt32Bits(value));

    private static void MixText(ref ulong hash, string value)
    {
        foreach (var ch in value ?? "") Mix(ref hash, ch);
        Mix(ref hash, -8);
    }

    internal static string ReadStr(JsonElement obj, string key, string fallback) =>
        obj.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? (e.GetString() ?? fallback) : fallback;

    internal static float ReadFloat(JsonElement obj, string key, float fallback) =>
        obj.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetSingle(out var v) && float.IsFinite(v) ? v : fallback;

    internal static int ReadInt(JsonElement obj, string key, int fallback) =>
        obj.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : fallback;

    internal static bool ReadBool(JsonElement obj, string key, bool fallback) =>
        obj.TryGetProperty(key, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : fallback;

    internal static string[] ReadSeasons(JsonElement obj, string key, string[] fallback)
    {
        if (!obj.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Array) return fallback;
        var list = new List<string>(4);
        foreach (var item in e.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                list.Add(item.GetString()!.Trim().ToLowerInvariant());
        return list.Count > 0 ? list.ToArray() : fallback;
    }

    internal static void MergeWeights(JsonElement obj, string key, Dictionary<string, float> target)
    {
        if (!obj.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Object) return;
        foreach (var kv in e.EnumerateObject())
            if (kv.Value.ValueKind == JsonValueKind.Number && kv.Value.TryGetSingle(out var v) && float.IsFinite(v))
                target[kv.Name] = MathF.Max(0f, v);
    }
}

/// <summary>Lockstep command for enacting a public health crisis response.</summary>
public sealed class HealthResponseCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int CrisisId { get; init; }
    public HealthResponseAction Action { get; init; } = HealthResponseAction.TargetedQuarantine;
    public bool Changed { get; private set; }

    public void Apply(Campaign campaign)
    {
        Changed = false;
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        Changed = DiseasesAndDisasters.RespondHealth(campaign, nation, CrisisId, Action);
        if (!Changed)
        {
            DiseasesAndDisasters.CanRespondHealth(campaign, nation, CrisisId, Action, out var reason);
            DiagnosticLogs.Write("orders", $"HEALTH_RESPONSE_REFUSED nation={IssuerId} crisis={CrisisId} action={Action} reason={reason}");
        }
    }
}

/// <summary>Lockstep command for enacting a natural disaster relief response.</summary>
public sealed class DisasterReliefCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int DisasterId { get; init; }
    public DisasterReliefAction Action { get; init; } = DisasterReliefAction.EmergencyEvacuationAndRescue;
    public bool Changed { get; private set; }

    public void Apply(Campaign campaign)
    {
        Changed = false;
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        Changed = DiseasesAndDisasters.RespondDisaster(campaign, nation, DisasterId, Action);
        if (!Changed)
        {
            DiseasesAndDisasters.CanRespondDisaster(campaign, nation, DisasterId, Action, out var reason);
            DiagnosticLogs.Write("orders", $"DISASTER_RELIEF_REFUSED nation={IssuerId} disaster={DisasterId} action={Action} reason={reason}");
        }
    }
}
