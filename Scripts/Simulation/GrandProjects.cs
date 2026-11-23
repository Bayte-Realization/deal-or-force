using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public enum GrandProjectStatus
{
    Active = 0,
    AwaitingStageFunding = 1,
    Paused = 2,
    Damaged = 3,
    Completed = 4
}

/// <summary>
/// One regional Strategic Megaproject in a campaign (under construction, mothballed, damaged,
/// or operational). Bound to a real <see cref="ScenarioState"/> via <see cref="StateMapIndex"/>:
/// if the region is conquered, ownership transfers to the conqueror.
/// </summary>
public sealed class GrandProjectInstance
{
    public int Id;
    public string DefinitionId = "";
    public int HostNationId;
    public int StateMapIndex;
    public int Stage = 1; // 1..3
    public float ProgressDays;
    public int StageDurationDays = 90;
    public GrandProjectStatus Status = GrandProjectStatus.Active;
    public bool PausedByPlayer;
    public int PartnerNationId = -1;
    public int PartnerJoinedDay = -1;
    public bool PartnerActive;
    public int StartedDay;
    public int CompletedDay = -1;
    public int LastSabotageDay = -9999;
    public float TotalInvestedBillions;

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool LastOperationalCached;

    public GrandProjectInstance Copy() => new()
    {
        Id = Id,
        DefinitionId = DefinitionId,
        HostNationId = HostNationId,
        StateMapIndex = StateMapIndex,
        Stage = Stage,
        ProgressDays = ProgressDays,
        StageDurationDays = StageDurationDays,
        Status = Status,
        PausedByPlayer = PausedByPlayer,
        PartnerNationId = PartnerNationId,
        PartnerJoinedDay = PartnerJoinedDay,
        PartnerActive = PartnerActive,
        StartedDay = StartedDay,
        CompletedDay = CompletedDay,
        LastSabotageDay = LastSabotageDay,
        TotalInvestedBillions = TotalInvestedBillions
    };
}

/// <summary>Cached modifier contribution attached to a nation for zero-alloc parallel reads.</summary>
public sealed record GrandProjectModifierSlice(string SourceName, IReadOnlyDictionary<string, float> Modifiers);

public sealed partial class Defines
{
    public bool GrandProjectsEnabled { get; set; } = true;
    public bool GrandProjectAiEnabled { get; set; } = true;
    public int GrandProjectMaxActivePerNation { get; set; } = 1;
    public int GrandProjectMaxCompletedPerNation { get; set; } = 3;
    public float GrandProjectConsortiumDiscount { get; set; } = 0.30f;
    public float GrandProjectConsortiumPartnerShare { get; set; } = 0.35f;
    public float GrandProjectConsortiumSpeedBonus { get; set; } = 0.25f;
    public float GrandProjectCancelSalvageShare { get; set; } = 0.25f;
    public float GrandProjectPauseDamageThreshold { get; set; } = 0.45f;
    public float GrandProjectOperationalDamageLimit { get; set; } = 0.35f;
    public float GrandProjectSabotageMinIntel { get; set; } = 0.35f;
    public float GrandProjectSabotageDelayShare { get; set; } = 0.28f;
    public int GrandProjectSabotageCooldownDays { get; set; } = 120;
    public float GrandProjectSabotageCostShare { get; set; } = 0.20f;
    public float GrandProjectSabotagePoliticalCost { get; set; } = 8.0f;
    public float GrandProjectAiTreasuryReserveMultiplier { get; set; } = 2.6f;
    public float GrandProjectAiMinStability { get; set; } = 0.55f;
}

public sealed partial class Campaign
{
    /// <summary>All active and completed regional Strategic Megaprojects in this world.</summary>
    public readonly List<GrandProjectInstance> GrandProjects = new();
    public int NextGrandProjectId = 1;
}

public sealed partial class Nation
{
    /// <summary>
    /// Cached operational megaproject modifier slices (host + consortium dividends).
    /// Rebuilt on the simulation thread whenever a project finishes, changes hands, is damaged
    /// or repaired, or a consortium fractures — read lock-free inside NationalModifiers.Fill.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal List<GrandProjectModifierSlice> GrandProjectModifierSlices;
}

/// <summary>
/// Strategic Megaprojects &amp; Joint International Consortia.
///
/// Extends Deal or Force's real loops rather than creating a disconnected minigame:
/// - Economy: GDP-scaled Treasury, Political Credit, Service XP, MoneyLedger.Construction, and
///   real MarketQuote.Satisfaction supply efficiency across the 63-good market.
/// - Diplomacy: Allied / International Organization Consortium Co-Funding (cost discount, +25%
///   build speed, supply bottleneck cushioning, OpinionSystem memory, Partner Dividend modifier,
///   and immediate suspension on diplomatic fracture/war/embargo).
/// - Warfare &amp; Intelligence: Bound to a real ScenarioState (war damage halts work / suspends
///   completed output; conquering the region transfers ownership and modifiers to the victor;
///   foreign IntelNetworks can sabotage active builds).
/// </summary>
public static class GrandProjects
{
    public const string FeatureId = "grand_projects";
    public const string SettingId = "grand_projects";

    // Stage 1: 30% | Stage 2: 45% | Stage 3: 25% of baseline stage cost multiplier.
    private static readonly float[] StageCostWeights = { 0.85f, 1.25f, 0.90f };

    /// <summary>Whether Strategic Megaprojects are active in this campaign.</summary>
    public static bool IsEnabled(Campaign c)
    {
        if (c is null || !c.Defines.GrandProjectsEnabled) return false;
        if (c.FeatureOverrides.TryGetValue(FeatureId, out var over) && !over) return false;
        if (c.Defines.Features is not null && c.Defines.Features.TryGetValue(FeatureId, out var feat) && !feat &&
            !c.FeatureOverrides.ContainsKey(FeatureId)) return false;
        if (c.Settings.TryGetValue(SettingId, out var setting) &&
            string.Equals(setting, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static float RawStageTreasuryCost(Nation host, GrandProjectDefinition def, int stage)
    {
        if (host is null || def is null) return 0f;
        var weight = StageCostWeights[M.Clamp(stage - 1, 0, StageCostWeights.Length - 1)];
        var baseStage = MathF.Max(def.BaseCostBillions, host.Gdp * def.GdpCostShare);
        return MathF.Max(0.05f, baseStage * weight);
    }

    public static float StageTreasuryCost(Campaign c, Nation host, GrandProjectDefinition def, int stage, bool hasActivePartner)
    {
        var raw = RawStageTreasuryCost(host, def, stage);
        if (!hasActivePartner || c is null) return raw;
        var discount = M.Clamp(c.Defines.GrandProjectConsortiumDiscount, 0f, 0.65f);
        return MathF.Max(0.05f, raw * (1f - discount));
    }

    public static float StagePoliticalCost(GrandProjectDefinition def, int stage)
    {
        if (def is null) return 0f;
        // Stage 1 takes the full political launch weight; stages 2 & 3 take half.
        return stage <= 1 ? def.PoliticalCost : MathF.Ceiling(def.PoliticalCost * 0.5f);
    }

    public static float StageExperienceCost(GrandProjectDefinition def, int stage)
    {
        if (def is null || string.IsNullOrEmpty(def.ExperienceCurrency) || def.ExperienceCost <= 0f) return 0f;
        // Service XP is paid on Stage 1 (blueprint/staffing) and Stage 2 (core systems), not Stage 3.
        return stage == 1 ? def.ExperienceCost : stage == 2 ? MathF.Ceiling(def.ExperienceCost * 0.5f) : 0f;
    }

    public static float PartnerContributionCost(Campaign c, Nation host, GrandProjectDefinition def, int stage)
    {
        if (c is null || host is null || def is null) return 0f;
        var share = M.Clamp(c.Defines.GrandProjectConsortiumPartnerShare, 0.05f, 0.80f);
        return MathF.Max(0.05f, RawStageTreasuryCost(host, def, stage) * share);
    }

    public static float PartnerPoliticalCost(GrandProjectDefinition def) =>
        def is null ? 0f : MathF.Max(4f, MathF.Ceiling(def.PoliticalCost * 0.5f));

    public static float SabotageCost(Campaign c, Nation host, GrandProjectDefinition def, int stage)
    {
        if (c is null || host is null || def is null) return 0f;
        var share = M.Clamp(c.Defines.GrandProjectSabotageCostShare, 0.05f, 0.80f);
        return MathF.Max(0.05f, RawStageTreasuryCost(host, def, stage) * share);
    }

    public static int CountActive(Campaign c, int nationId)
    {
        if (c is null) return 0;
        var count = 0;
        for (var i = 0; i < c.GrandProjects.Count; i++)
        {
            var p = c.GrandProjects[i];
            if (p.HostNationId == nationId && p.Status != GrandProjectStatus.Completed) count++;
        }
        return count;
    }

    public static int CountCompleted(Campaign c, int nationId)
    {
        if (c is null) return 0;
        var count = 0;
        for (var i = 0; i < c.GrandProjects.Count; i++)
        {
            var p = c.GrandProjects[i];
            if (p.HostNationId == nationId && p.Status == GrandProjectStatus.Completed) count++;
        }
        return count;
    }

    public static GrandProjectInstance FindById(Campaign c, int instanceId)
    {
        if (c is null) return null;
        for (var i = 0; i < c.GrandProjects.Count; i++)
            if (c.GrandProjects[i].Id == instanceId) return c.GrandProjects[i];
        return null;
    }

    public static GrandProjectInstance FindInState(Campaign c, int stateMapIndex)
    {
        if (c is null) return null;
        for (var i = 0; i < c.GrandProjects.Count; i++)
            if (c.GrandProjects[i].StateMapIndex == stateMapIndex) return c.GrandProjects[i];
        return null;
    }

    /// <summary>
    /// Real market supply efficiency (0.50..1.00) based on the host's (and active consortium partner's)
    /// <see cref="MarketQuote.Satisfaction"/> for the project's declared <c>key_goods</c>.
    /// Also reports the worst bottleneck commodity (if any) for clear UI explanation.
    /// </summary>
    public static float SupplyEfficiency(Campaign c, Nation host, Nation partner, GrandProjectDefinition def,
        out string bottleneckGood, out float bottleneckSatisfaction)
    {
        bottleneckGood = "";
        bottleneckSatisfaction = 1f;
        if (host is null || def?.KeyGoods is null || def.KeyGoods.Length == 0 || host.Market.Count == 0)
            return 1f;

        var worst = 1f;
        for (var i = 0; i < def.KeyGoods.Length; i++)
        {
            var goodId = def.KeyGoods[i];
            if (!CommodityCatalog.TryParse(goodId, out var commodity)) continue;
            if (!host.Market.TryGetValue(commodity, out var hostQuote)) continue;
            var sat = M.Clamp01(hostQuote.Satisfaction);
            if (partner is not null && partner.Market.TryGetValue(commodity, out var partnerQuote))
            {
                // An active consortium partner with stronger supply cushions host bottlenecks.
                var blended = (sat + M.Clamp01(partnerQuote.Satisfaction)) * 0.5f;
                if (blended > sat) sat = blended;
            }
            if (sat < worst)
            {
                worst = sat;
                bottleneckGood = Loc.Get(CommodityCatalog.Get(commodity).Name);
                bottleneckSatisfaction = sat;
            }
        }
        return worst >= 0.90f ? 1f : M.Clamp(0.50f + worst * 0.55f, 0.50f, 1f);
    }

    public static float DailyBuildPace(Campaign c, GrandProjectInstance inst, out string bottleneckGood, out float bottleneckSat)
    {
        bottleneckGood = "";
        bottleneckSat = 1f;
        if (c is null || inst is null) return 0f;
        var host = c.NationById(inst.HostNationId);
        var def = GrandProjectCatalog.Find(inst.DefinitionId);
        if (host is null || def is null) return 0f;
        var partner = inst.PartnerActive && inst.PartnerNationId >= 0 ? c.NationById(inst.PartnerNationId) : null;
        var speedMod = M.Clamp(NationalModifiers.Factor(host, "construction_speed"), 0.5f, 2.5f);
        var consortium = partner is not null ? 1f + M.Clamp(c.Defines.GrandProjectConsortiumSpeedBonus, 0f, 1f) : 1f;
        var supply = SupplyEfficiency(c, host, partner, def, out bottleneckGood, out bottleneckSat);
        var chokepoint = StrategicChokepoints.MegaprojectChokepointFactor(c, host, partner, def, out var sqGood, out _, out _);
        if (chokepoint < 0.99f && string.IsNullOrEmpty(bottleneckGood)) bottleneckGood = sqGood;
        return MathF.Max(0.20f, speedMod * consortium * supply * chokepoint);
    }

    public static bool ShareDiplomaticAlignment(Campaign c, int nationA, int nationB)
    {
        if (c is null || nationA < 0 || nationB < 0 || nationA == nationB) return false;
        if (CampaignDiplomacy.AtWar(c, nationA, nationB)) return false;
        var rel = CampaignDiplomacy.Peek(c, nationA, nationB);
        if (rel is not null && (rel.EmbargoByA || rel.EmbargoByB)) return false;
        if (rel?.Stance == DiplomaticStance.Allied) return true;
        for (var i = 0; i < c.Organizations.Count; i++)
        {
            var org = c.Organizations[i];
            if (org.Dissolved) continue;
            if (org.Members.Contains(nationA) && org.Members.Contains(nationB)) return true;
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // Eligibility & Exact Refusal Reasons (Single Source of Truth for UI & Sim)
    // -------------------------------------------------------------------------

    public static bool CanStart(Campaign c, Nation nation, ScenarioState state, string projectId, out string reason)
    {
        reason = "";
        if (!IsEnabled(c)) { reason = Loc.Get("gp.refuse.disabled"); return false; }
        if (nation is null || nation.Movement is not null || nation.Population <= 0)
        { reason = Loc.Get("gp.refuse.invalid_nation"); return false; }
        var def = GrandProjectCatalog.Find(projectId);
        if (def is null) { reason = Loc.Get("gp.refuse.invalid_project"); return false; }
        if (state is null) { reason = Loc.Get("gp.refuse.invalid_region"); return false; }
        if (state.NationMapIndex != nation.MapIndex) { reason = Loc.Get("gp.refuse.not_owner"); return false; }
        if (state.WarDamage >= c.Defines.GrandProjectPauseDamageThreshold)
        { reason = Loc.Get("gp.refuse.region_occupied"); return false; }
        if (FindInState(c, state.MapIndex) is not null)
        { reason = Loc.Get("gp.refuse.region_taken"); return false; }

        var maxActive = Math.Max(1, c.Defines.GrandProjectMaxActivePerNation);
        var active = CountActive(c, nation.Id);
        if (active >= maxActive)
        { reason = Loc.Format("gp.refuse.active_limit_fmt", active, maxActive); return false; }

        var maxDone = Math.Max(1, c.Defines.GrandProjectMaxCompletedPerNation);
        var done = CountCompleted(c, nation.Id);
        if (done >= maxDone)
        { reason = Loc.Format("gp.refuse.completed_limit_fmt", done, maxDone); return false; }

        for (var i = 0; i < c.GrandProjects.Count; i++)
        {
            var p = c.GrandProjects[i];
            if (p.DefinitionId.Equals(def.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (p.HostNationId == nation.Id) { reason = Loc.Get("gp.refuse.duplicate_nation"); return false; }
                if (def.UniqueGlobal) { reason = Loc.Get("gp.refuse.unique_global"); return false; }
            }
            if (p.HostNationId == nation.Id && GrandProjectCatalog.Find(p.DefinitionId) is { } existingDef &&
                string.Equals(existingDef.Category, def.Category, StringComparison.OrdinalIgnoreCase))
            {
                reason = Loc.Format("gp.refuse.category_taken_fmt", def.CategoryName, existingDef.DisplayName);
                return false;
            }
        }

        if (def.MinPopulation > 0 && state.Population < def.MinPopulation)
        { reason = Loc.Format("gp.refuse.population_fmt", Fmt.People(def.MinPopulation), Fmt.People(state.Population)); return false; }
        if (def.MinLiteracy > 0f && state.Literacy < def.MinLiteracy)
        { reason = Loc.Format("gp.refuse.literacy_fmt", MathF.Round(def.MinLiteracy * 100f), MathF.Round(state.Literacy * 100f)); return false; }
        if (def.RequiresDistressedRegion && state.WarDamage < 0.08f && state.StandardOfLiving > 0.58f)
        { reason = Loc.Get("gp.refuse.not_distressed"); return false; }

        return CanAffordStage(c, nation, def, 1, hasActivePartner: false, out reason);
    }

    public static bool CanAdvanceStage(Campaign c, Nation nation, GrandProjectInstance inst, out string reason)
    {
        reason = "";
        if (!IsEnabled(c)) { reason = Loc.Get("gp.refuse.disabled"); return false; }
        if (nation is null || inst is null) { reason = Loc.Get("gp.refuse.invalid_project"); return false; }
        if (inst.HostNationId != nation.Id) { reason = Loc.Get("gp.refuse.not_host"); return false; }
        if (inst.Status == GrandProjectStatus.Completed) { reason = Loc.Get("gp.refuse.already_completed"); return false; }
        if (inst.Status != GrandProjectStatus.AwaitingStageFunding || inst.Stage >= 3)
        { reason = Loc.Get("gp.refuse.not_awaiting"); return false; }
        var state = c.StateByMapIndex(inst.StateMapIndex);
        if (state is null || state.NationMapIndex != nation.MapIndex)
        { reason = Loc.Get("gp.refuse.not_owner"); return false; }
        if (state.WarDamage >= c.Defines.GrandProjectPauseDamageThreshold)
        { reason = Loc.Get("gp.refuse.region_occupied"); return false; }
        var def = GrandProjectCatalog.Find(inst.DefinitionId);
        if (def is null) { reason = Loc.Get("gp.refuse.invalid_project"); return false; }

        return CanAffordStage(c, nation, def, inst.Stage + 1, inst.PartnerActive, out reason);
    }

    public static bool CanTogglePause(Campaign c, Nation nation, GrandProjectInstance inst, bool pause, out string reason)
    {
        reason = "";
        if (!IsEnabled(c)) { reason = Loc.Get("gp.refuse.disabled"); return false; }
        if (nation is null || inst is null) { reason = Loc.Get("gp.refuse.invalid_project"); return false; }
        if (inst.HostNationId != nation.Id) { reason = Loc.Get("gp.refuse.not_host"); return false; }
        if (inst.Status == GrandProjectStatus.Completed) { reason = Loc.Get("gp.refuse.already_completed"); return false; }
        if (inst.PausedByPlayer == pause) { reason = Loc.Get("gp.refuse.same_pause_state"); return false; }
        return true;
    }

    public static bool CanCancel(Campaign c, Nation nation, GrandProjectInstance inst, out string reason)
    {
        reason = "";
        if (c is null || nation is null || inst is null) { reason = Loc.Get("gp.refuse.invalid_project"); return false; }
        if (inst.HostNationId != nation.Id) { reason = Loc.Get("gp.refuse.not_host"); return false; }
        if (inst.Status == GrandProjectStatus.Completed) { reason = Loc.Get("gp.refuse.already_completed"); return false; }
        return true;
    }

    public static bool CanJoinConsortium(Campaign c, Nation partner, GrandProjectInstance inst, out string reason)
    {
        reason = "";
        if (!IsEnabled(c)) { reason = Loc.Get("gp.refuse.disabled"); return false; }
        if (partner is null || partner.Movement is not null || partner.Population <= 0)
        { reason = Loc.Get("gp.refuse.invalid_nation"); return false; }
        if (inst is null) { reason = Loc.Get("gp.refuse.invalid_project"); return false; }
        if (inst.Status == GrandProjectStatus.Completed) { reason = Loc.Get("gp.refuse.already_completed"); return false; }
        if (inst.HostNationId == partner.Id) { reason = Loc.Get("gp.refuse.self_consortium"); return false; }
        if (inst.PartnerNationId >= 0 && inst.PartnerActive) { reason = Loc.Get("gp.refuse.has_partner"); return false; }
        var host = c.NationById(inst.HostNationId);
        var def = GrandProjectCatalog.Find(inst.DefinitionId);
        if (host is null || def is null) { reason = Loc.Get("gp.refuse.invalid_project"); return false; }

        if (CampaignDiplomacy.AtWar(c, partner.Id, host.Id))
        { reason = Loc.Get("gp.refuse.hostile_relation"); return false; }
        var rel = CampaignDiplomacy.Peek(c, partner.Id, host.Id);
        if (rel is not null && (rel.EmbargoByA || rel.EmbargoByB))
        { reason = Loc.Get("gp.refuse.hostile_relation"); return false; }
        if (!ShareDiplomaticAlignment(c, partner.Id, host.Id))
        { reason = Loc.Get("gp.refuse.no_alignment"); return false; }

        var cost = PartnerContributionCost(c, host, def, inst.Stage);
        if (partner.Treasury < cost)
        { reason = Loc.Format("gp.refuse.treasury_fmt", Fmt.Money(cost), Fmt.Money(partner.Treasury)); return false; }
        var pp = PartnerPoliticalCost(def);
        if (partner.PoliticalPoints < pp)
        { reason = Loc.Format("gp.refuse.political_fmt", MathF.Round(pp), MathF.Floor(partner.PoliticalPoints)); return false; }
        return true;
    }

    public static bool CanSabotage(Campaign c, Nation attacker, GrandProjectInstance inst, out string reason)
    {
        reason = "";
        if (!IsEnabled(c)) { reason = Loc.Get("gp.refuse.disabled"); return false; }
        if (attacker is null || attacker.Movement is not null || attacker.Population <= 0)
        { reason = Loc.Get("gp.refuse.invalid_nation"); return false; }
        if (inst is null) { reason = Loc.Get("gp.refuse.invalid_project"); return false; }
        if (inst.HostNationId == attacker.Id) { reason = Loc.Get("gp.refuse.sabotage_self"); return false; }
        if (inst.Status != GrandProjectStatus.Active) { reason = Loc.Get("gp.refuse.sabotage_inactive"); return false; }
        var host = c.NationById(inst.HostNationId);
        var def = GrandProjectCatalog.Find(inst.DefinitionId);
        if (host is null || def is null) { reason = Loc.Get("gp.refuse.invalid_project"); return false; }
        if (inst.PartnerActive && inst.PartnerNationId == attacker.Id)
        { reason = Loc.Get("gp.refuse.sabotage_allied"); return false; }
        var rel = CampaignDiplomacy.Peek(c, attacker.Id, host.Id);
        if (rel?.Stance == DiplomaticStance.Allied)
        { reason = Loc.Get("gp.refuse.sabotage_allied"); return false; }

        var cooldown = Math.Max(1, c.Defines.GrandProjectSabotageCooldownDays);
        var since = c.ElapsedDays - inst.LastSabotageDay;
        if (since < cooldown)
        { reason = Loc.Format("gp.refuse.sabotage_cooldown_fmt", cooldown - since); return false; }

        var intel = IntelNetworks.Strength(c, attacker.Id, host.Id);
        var minIntel = M.Clamp01(c.Defines.GrandProjectSabotageMinIntel);
        if (intel < minIntel)
        { reason = Loc.Format("gp.refuse.sabotage_intel_fmt", MathF.Round(minIntel * 100f), MathF.Round(intel * 100f)); return false; }

        var cost = SabotageCost(c, host, def, inst.Stage);
        if (attacker.Treasury < cost)
        { reason = Loc.Format("gp.refuse.treasury_fmt", Fmt.Money(cost), Fmt.Money(attacker.Treasury)); return false; }
        var pp = MathF.Max(0f, c.Defines.GrandProjectSabotagePoliticalCost);
        if (attacker.PoliticalPoints < pp)
        { reason = Loc.Format("gp.refuse.political_fmt", MathF.Round(pp), MathF.Floor(attacker.PoliticalPoints)); return false; }
        return true;
    }

    private static bool CanAffordStage(Campaign c, Nation nation, GrandProjectDefinition def, int stage,
        bool hasActivePartner, out string reason)
    {
        reason = "";
        var money = StageTreasuryCost(c, nation, def, stage, hasActivePartner);
        if (nation.Treasury < money)
        { reason = Loc.Format("gp.refuse.treasury_fmt", Fmt.Money(money), Fmt.Money(nation.Treasury)); return false; }

        var pp = StagePoliticalCost(def, stage);
        if (nation.PoliticalPoints < pp)
        { reason = Loc.Format("gp.refuse.political_fmt", MathF.Round(pp), MathF.Floor(nation.PoliticalPoints)); return false; }

        var xp = StageExperienceCost(def, stage);
        if (xp > 0f && !string.IsNullOrEmpty(def.ExperienceCurrency))
        {
            var have = DecisionResources.Balance(nation, def.ExperienceCurrency);
            if (!float.IsFinite(have) || have < xp)
            {
                reason = Loc.Format("gp.refuse.xp_fmt", DecisionResources.Name(def.ExperienceCurrency),
                    MathF.Round(xp), MathF.Floor(MathF.Max(0f, have)));
                return false;
            }
        }
        return true;
    }

    private static void SpendExperience(Nation nation, string currency, float amount)
    {
        if (nation is null || amount <= 0f || string.IsNullOrEmpty(currency)) return;
        switch (currency)
        {
            case "army_experience": nation.ArmyExperience = MathF.Max(0f, nation.ArmyExperience - amount); break;
            case "air_experience": nation.AirExperience = MathF.Max(0f, nation.AirExperience - amount); break;
            case "naval_experience": nation.NavalExperience = MathF.Max(0f, nation.NavalExperience - amount); break;
        }
    }

    // -------------------------------------------------------------------------
    // Execution (Invoked ONLY by ICampaignCommand.Apply)
    // -------------------------------------------------------------------------

    internal static GrandProjectInstance Start(Campaign c, Nation nation, ScenarioState state, string projectId)
    {
        if (!CanStart(c, nation, state, projectId, out _)) return null;
        var def = GrandProjectCatalog.Find(projectId);
        var money = StageTreasuryCost(c, nation, def, 1, hasActivePartner: false);
        var pp = StagePoliticalCost(def, 1);
        var xp = StageExperienceCost(def, 1);

        nation.Treasury -= money;
        MoneyLedger.Book(nation, MoneyFlow.Construction, -money);
        nation.PoliticalPoints = MathF.Max(0f, nation.PoliticalPoints - pp);
        SpendExperience(nation, def.ExperienceCurrency, xp);

        var inst = new GrandProjectInstance
        {
            Id = c.NextGrandProjectId++,
            DefinitionId = def.Id,
            HostNationId = nation.Id,
            StateMapIndex = state.MapIndex,
            Stage = 1,
            ProgressDays = 0f,
            StageDurationDays = def.DurationForStage(1),
            Status = GrandProjectStatus.Active,
            StartedDay = c.ElapsedDays,
            TotalInvestedBillions = money
        };
        c.GrandProjects.Add(inst);
        c.Chronicle.Record(c, "grand_project_started", nation.Id, -1, def.NameKey, state.Name);
        c.MarkStrategicChange();
        return inst;
    }

    internal static bool AdvanceStage(Campaign c, Nation nation, int instanceId)
    {
        var inst = FindById(c, instanceId);
        if (!CanAdvanceStage(c, nation, inst, out _)) return false;
        var def = GrandProjectCatalog.Find(inst.DefinitionId);
        var nextStage = inst.Stage + 1;
        var money = StageTreasuryCost(c, nation, def, nextStage, inst.PartnerActive);
        var pp = StagePoliticalCost(def, nextStage);
        var xp = StageExperienceCost(def, nextStage);

        nation.Treasury -= money;
        MoneyLedger.Book(nation, MoneyFlow.Construction, -money);
        nation.PoliticalPoints = MathF.Max(0f, nation.PoliticalPoints - pp);
        SpendExperience(nation, def.ExperienceCurrency, xp);

        inst.Stage = nextStage;
        inst.ProgressDays = 0f;
        inst.StageDurationDays = def.DurationForStage(nextStage);
        inst.Status = inst.PausedByPlayer ? GrandProjectStatus.Paused : GrandProjectStatus.Active;
        inst.TotalInvestedBillions += money;
        c.MarkStrategicChange();
        return true;
    }

    internal static bool SetPaused(Campaign c, Nation nation, int instanceId, bool paused)
    {
        var inst = FindById(c, instanceId);
        if (!CanTogglePause(c, nation, inst, paused, out _)) return false;
        inst.PausedByPlayer = paused;
        if (inst.Status != GrandProjectStatus.AwaitingStageFunding)
            inst.Status = paused ? GrandProjectStatus.Paused : GrandProjectStatus.Active;
        c.MarkStrategicChange();
        return true;
    }

    internal static bool Cancel(Campaign c, Nation nation, int instanceId)
    {
        var inst = FindById(c, instanceId);
        if (!CanCancel(c, nation, inst, out _)) return false;
        var salvage = MathF.Max(0f, inst.TotalInvestedBillions * M.Clamp01(c.Defines.GrandProjectCancelSalvageShare));
        if (salvage > 0f)
        {
            nation.Treasury += salvage;
            MoneyLedger.Book(nation, MoneyFlow.Construction, salvage);
        }
        var partnerId = inst.PartnerNationId;
        c.GrandProjects.Remove(inst);
        RebuildModifiersFor(c, nation.Id);
        if (partnerId >= 0) RebuildModifiersFor(c, partnerId);
        c.MarkStrategicChange();
        return true;
    }

    internal static bool JoinConsortium(Campaign c, Nation partner, int instanceId)
    {
        var inst = FindById(c, instanceId);
        if (!CanJoinConsortium(c, partner, inst, out _)) return false;
        var host = c.NationById(inst.HostNationId);
        var def = GrandProjectCatalog.Find(inst.DefinitionId);
        var cost = PartnerContributionCost(c, host, def, inst.Stage);
        var pp = PartnerPoliticalCost(def);

        partner.Treasury -= cost;
        MoneyLedger.Book(partner, MoneyFlow.ForeignAid, -cost);
        partner.PoliticalPoints = MathF.Max(0f, partner.PoliticalPoints - pp);

        // Partner contribution directly cushions the host's construction financing too (25% rebate).
        var hostGrant = cost * 0.25f;
        host.Treasury += hostGrant;
        MoneyLedger.Book(host, MoneyFlow.ForeignAid, hostGrant);

        inst.PartnerNationId = partner.Id;
        inst.PartnerJoinedDay = c.ElapsedDays;
        inst.PartnerActive = true;
        inst.TotalInvestedBillions += cost;

        var rel = CampaignDiplomacy.Get(c, partner.Id, host.Id);
        OpinionSystem.Remember(c, rel, "grand_project_consortium_partner");
        c.Chronicle.Record(c, "grand_project_consortium_joined", partner.Id, host.Id, def.NameKey);
        c.MarkStrategicChange();
        return true;
    }

    internal static bool Sabotage(Campaign c, Nation attacker, int instanceId)
    {
        var inst = FindById(c, instanceId);
        if (!CanSabotage(c, attacker, inst, out _)) return false;
        var host = c.NationById(inst.HostNationId);
        var def = GrandProjectCatalog.Find(inst.DefinitionId);
        var state = c.StateByMapIndex(inst.StateMapIndex);

        var cost = SabotageCost(c, host, def, inst.Stage);
        var pp = MathF.Max(0f, c.Defines.GrandProjectSabotagePoliticalCost);
        attacker.Treasury -= cost;
        MoneyLedger.Book(attacker, MoneyFlow.Covert, -cost);
        attacker.PoliticalPoints = MathF.Max(0f, attacker.PoliticalPoints - pp);

        inst.LastSabotageDay = c.ElapsedDays;
        var intel = IntelNetworks.Strength(c, attacker.Id, host.Id);
        var counter = IntelNetworks.CounterIntelligence(c, host);
        var successChance = M.Clamp(0.28f + 0.65f * intel - 0.35f * counter, 0.10f, 0.90f);
        var succeeded = c.Random.Chance(successChance);
        if (succeeded)
        {
            var delayDays = inst.StageDurationDays * M.Clamp(c.Defines.GrandProjectSabotageDelayShare, 0.05f, 0.75f);
            inst.ProgressDays = MathF.Max(0f, inst.ProgressDays - delayDays);
            c.Chronicle.Record(c, "grand_project_sabotaged", attacker.Id, host.Id, def.NameKey, state?.Name ?? "", isPublic: true);
        }

        var exposureChance = succeeded
            ? M.Clamp(0.12f + 0.25f * counter - 0.10f * intel, 0.05f, 0.75f)
            : M.Clamp(0.35f + 0.40f * counter - 0.15f * intel, 0.15f, 0.90f);
        if (c.Random.Chance(exposureChance))
        {
            var rel = CampaignDiplomacy.Get(c, attacker.Id, host.Id);
            OpinionSystem.Remember(c, rel, "grand_project_sabotaged_us");
            if (attacker.IntelNetworks.TryGetValue(host.Id, out var cur))
                attacker.IntelNetworks[host.Id] = cur * 0.5f;
        }
        c.MarkStrategicChange();
        return succeeded;
    }

    // -------------------------------------------------------------------------
    // Daily Simulation Tick (O(ActiveProjects) + Staggered Monthly AI)
    // -------------------------------------------------------------------------

    public static void Tick(Campaign c)
    {
        if (c is null) return;
        if (!IsEnabled(c))
        {
            ClearAllModifierCachesIfPresent(c);
            return;
        }

        var modifiersDirty = false;
        for (var i = 0; i < c.GrandProjects.Count; i++)
        {
            var inst = c.GrandProjects[i];
            var def = GrandProjectCatalog.Find(inst.DefinitionId);
            var state = c.StateByMapIndex(inst.StateMapIndex);
            if (def is null || state is null) continue;

            // 1. Conquest Inheritance: if the region changed sovereign hands, transfer the megaproject.
            var owner = c.NationByMapIndex(state.NationMapIndex);
            if (owner is not null && owner.Id != inst.HostNationId)
            {
                var previousHostId = inst.HostNationId;
                inst.HostNationId = owner.Id;
                if (inst.PartnerActive && inst.PartnerNationId >= 0 &&
                    (inst.PartnerNationId == owner.Id || !ShareDiplomaticAlignment(c, inst.PartnerNationId, owner.Id)))
                {
                    var oldPartner = inst.PartnerNationId;
                    inst.PartnerActive = false;
                    RebuildModifiersFor(c, oldPartner);
                }
                c.Chronicle.Record(c, "grand_project_captured", owner.Id, previousHostId, def.NameKey, state.Name);
                RebuildModifiersFor(c, previousHostId);
                RebuildModifiersFor(c, owner.Id);
                c.MarkStrategicChange();
                modifiersDirty = true;
            }

            var host = c.NationById(inst.HostNationId);
            if (host is null || host.Population <= 0) continue;

            // 2. Consortium Diplomatic Integrity: war, embargo, or broken alignment suspends Partner Dividend.
            if (inst.PartnerActive && inst.PartnerNationId >= 0)
            {
                var partner = c.NationById(inst.PartnerNationId);
                if (partner is null || partner.Population <= 0 || !ShareDiplomaticAlignment(c, partner.Id, host.Id))
                {
                    inst.PartnerActive = false;
                    if (partner is not null && partner.Population > 0)
                    {
                        var rel = CampaignDiplomacy.Get(c, partner.Id, host.Id);
                        OpinionSystem.Remember(c, rel, "grand_project_consortium_fractured");
                        c.Chronicle.Record(c, "grand_project_consortium_fractured", host.Id, partner.Id, def.NameKey);
                    }
                    RebuildModifiersFor(c, inst.PartnerNationId);
                    c.MarkStrategicChange();
                    modifiersDirty = true;
                }
            }

            // 3. Completed projects: monitor war-damage operational gate.
            if (inst.Status == GrandProjectStatus.Completed)
            {
                // Rebuild modifiers only when operational threshold crosses or slices are missing.
                var opNow = state.WarDamage <= c.Defines.GrandProjectOperationalDamageLimit;
                if (opNow != inst.LastOperationalCached || host.GrandProjectModifierSlices is null)
                {
                    inst.LastOperationalCached = opNow;
                    modifiersDirty = true;
                }
                continue;
            }

            // 4. Construction gating: war damage vs player pause vs stage funding.
            if (state.WarDamage >= c.Defines.GrandProjectPauseDamageThreshold)
            {
                if (inst.Status != GrandProjectStatus.Damaged)
                {
                    inst.Status = GrandProjectStatus.Damaged;
                    c.MarkStrategicChange();
                }
                continue;
            }
            if (inst.Status == GrandProjectStatus.Damaged)
            {
                inst.Status = inst.PausedByPlayer ? GrandProjectStatus.Paused : GrandProjectStatus.Active;
                c.MarkStrategicChange();
            }
            if (inst.PausedByPlayer || inst.Status == GrandProjectStatus.Paused ||
                inst.Status == GrandProjectStatus.AwaitingStageFunding)
                continue;

            // 5. Daily progress advance.
            var pace = DailyBuildPace(c, inst, out _, out _);
            inst.ProgressDays += pace;
            if (inst.ProgressDays >= inst.StageDurationDays)
            {
                inst.ProgressDays = inst.StageDurationDays;
                if (inst.Stage < 3)
                {
                    inst.Status = GrandProjectStatus.AwaitingStageFunding;
                    c.Chronicle.Record(c, "grand_project_stage", host.Id, -1, def.NameKey, state.Name);
                    c.MarkStrategicChange();
                }
                else
                {
                    inst.Status = GrandProjectStatus.Completed;
                    inst.CompletedDay = c.ElapsedDays;
                    c.Chronicle.Record(c, "grand_project_completed", host.Id, -1, def.NameKey, state.Name);
                    if (inst.PartnerActive && inst.PartnerNationId >= 0)
                    {
                        var rel = CampaignDiplomacy.Get(c, inst.PartnerNationId, host.Id);
                        OpinionSystem.Remember(c, rel, "grand_project_consortium_completed");
                    }
                    RebuildModifiersFor(c, host.Id);
                    if (inst.PartnerActive && inst.PartnerNationId >= 0)
                        RebuildModifiersFor(c, inst.PartnerNationId);
                    c.MarkStrategicChange();
                    modifiersDirty = true;
                }
            }
        }

        if (modifiersDirty) RebuildAllModifiers(c);

        // 6. Weekly Emergent Supply-Chain & Chokepoint Leverage pass (single O(R) flat cache)
        StrategicChokepoints.TickWeekly(c);

        // 7. Staggered Monthly AI Participation (nation.Id % 30 == ElapsedDays % 30 -> zero spike!)
        if (c.Defines.GrandProjectAiEnabled) TickAi(c);
    }

    private static void TickAi(Campaign c)
    {
        var bucket = c.ElapsedDays % 30;
        var living = c.ActiveNations;
        for (var i = 0; i < living.Count; i++)
        {
            var nation = living[i];
            if (nation.IsHuman || nation.Movement is not null) continue;
            if (nation.Id % 30 != bucket) continue;
            EvaluateAiNation(c, nation);
        }
    }

    private static void EvaluateAiNation(Campaign c, Nation nation)
    {
        if (nation.Stability < c.Defines.GrandProjectAiMinStability) return;
        if (nation.DebtCondition >= DebtCondition.Default) return;
        var reserveMult = MathF.Max(1.2f, c.Defines.GrandProjectAiTreasuryReserveMultiplier);

        // 1. Advance own project if awaiting stage funding and reserves are comfortable.
        for (var i = 0; i < c.GrandProjects.Count; i++)
        {
            var inst = c.GrandProjects[i];
            if (inst.HostNationId != nation.Id) continue;
            if (inst.Status != GrandProjectStatus.AwaitingStageFunding) continue;
            var def = GrandProjectCatalog.Find(inst.DefinitionId);
            if (def is null) continue;
            var cost = StageTreasuryCost(c, nation, def, inst.Stage + 1, inst.PartnerActive);
            if (nation.Treasury >= cost * reserveMult && CanAdvanceStage(c, nation, inst, out _))
            {
                new AdvanceGrandProjectStageCommand(nation.Id, inst.Id).Apply(c);
                return;
            }
        }

        // 2. Consider joining an allied/IO megaproject as Consortium Partner if flush with cash.
        for (var i = 0; i < c.GrandProjects.Count; i++)
        {
            var inst = c.GrandProjects[i];
            if (inst.HostNationId == nation.Id || inst.Status == GrandProjectStatus.Completed) continue;
            if (inst.PartnerNationId >= 0 && inst.PartnerActive) continue;
            var host = c.NationById(inst.HostNationId);
            var def = GrandProjectCatalog.Find(inst.DefinitionId);
            if (host is null || def is null) continue;
            var joinCost = PartnerContributionCost(c, host, def, inst.Stage);
            if (nation.Treasury >= joinCost * (reserveMult + 0.5f) && CanJoinConsortium(c, nation, inst, out _))
            {
                new JoinGrandProjectConsortiumCommand(nation.Id, inst.Id).Apply(c);
                return;
            }
        }

        // 3. Commission a new megaproject if at peace, below caps, and holding strong reserves.
        if (CountActive(c, nation.Id) >= Math.Max(1, c.Defines.GrandProjectMaxActivePerNation)) return;
        if (CountCompleted(c, nation.Id) >= Math.Max(1, c.Defines.GrandProjectMaxCompletedPerNation)) return;
        if (Contractors.AtWar(c, nation.Id)) return;

        var states = c.StatesOf(nation);
        if (states.Length == 0) return;

        // Pick best eligible region (highest population with low war damage).
        ScenarioState bestState = null;
        for (var s = 0; s < states.Length; s++)
        {
            var st = states[s];
            if (st.WarDamage >= c.Defines.GrandProjectPauseDamageThreshold) continue;
            if (FindInState(c, st.MapIndex) is not null) continue;
            if (bestState is null || st.Population > bestState.Population) bestState = st;
        }
        if (bestState is null) return;

        var catalog = GrandProjectCatalog.All;
        if (catalog.Count == 0) return;
        // Deterministic rotation by nation id + completed count so AI nations pick diverse wonders.
        var offset = (nation.Id + CountCompleted(c, nation.Id) * 3) % catalog.Count;
        for (var k = 0; k < catalog.Count; k++)
        {
            var def = catalog[(offset + k) % catalog.Count];
            var cost = StageTreasuryCost(c, nation, def, 1, hasActivePartner: false);
            if (nation.Treasury < cost * reserveMult) continue;
            if (CanStart(c, nation, bestState, def.Id, out _))
            {
                new StartGrandProjectCommand(nation.Id, def.Id, bestState.MapIndex).Apply(c);
                return;
            }
        }
    }

    // -------------------------------------------------------------------------
    // NationalModifiers Integration (Zero-Alloc Parallel Safe)
    // -------------------------------------------------------------------------

    public static void AddTo(Dictionary<string, float> totals, Nation nation)
    {
        var slices = nation?.GrandProjectModifierSlices;
        if (slices is null || slices.Count == 0) return;
        for (var i = 0; i < slices.Count; i++)
        {
            var mods = slices[i].Modifiers;
            if (mods is Dictionary<string, float> concrete)
            {
                foreach (var (key, value) in concrete)
                    totals[key] = totals.GetValueOrDefault(key) + value;
            }
            else
            {
                foreach (var (key, value) in mods)
                    totals[key] = totals.GetValueOrDefault(key) + value;
            }
        }
    }

    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation nation)
    {
        var slices = nation?.GrandProjectModifierSlices;
        if (slices is null || slices.Count == 0) yield break;
        for (var i = 0; i < slices.Count; i++)
            yield return (slices[i].SourceName, slices[i].Modifiers);
    }

    public static bool IsOperationallyActive(Campaign c, GrandProjectInstance inst)
    {
        if (c is null || inst is null || !IsEnabled(c)) return false;
        if (inst.Status != GrandProjectStatus.Completed) return false;
        var state = c.StateByMapIndex(inst.StateMapIndex);
        if (state is null) return false;
        var host = c.NationById(inst.HostNationId);
        if (host is null || state.NationMapIndex != host.MapIndex) return false;
        return state.WarDamage <= c.Defines.GrandProjectOperationalDamageLimit;
    }

    public static void RebuildAllModifiers(Campaign c)
    {
        if (c is null) return;
        var enabled = IsEnabled(c);
        var active = c.ActiveNations;
        for (var i = 0; i < active.Count; i++)
        {
            var n = active[i];
            var had = n.GrandProjectModifierSlices is { Count: > 0 };
            n.GrandProjectModifierSlices = null;
            if (had) NationalModifiers.Invalidate(n);
        }
        if (!enabled || c.GrandProjects.Count == 0) return;

        for (var i = 0; i < c.GrandProjects.Count; i++)
        {
            var inst = c.GrandProjects[i];
            if (!IsOperationallyActive(c, inst)) continue;
            var def = GrandProjectCatalog.Find(inst.DefinitionId);
            if (def is null) continue;

            var host = c.NationById(inst.HostNationId);
            if (host is not null && def.HostModifiers.Count > 0)
            {
                host.GrandProjectModifierSlices ??= new List<GrandProjectModifierSlice>(2);
                host.GrandProjectModifierSlices.Add(new GrandProjectModifierSlice(def.NameKey, def.HostModifiers));
                NationalModifiers.Invalidate(host);
            }
            if (inst.PartnerActive && inst.PartnerNationId >= 0 && def.PartnerModifiers.Count > 0)
            {
                var partner = c.NationById(inst.PartnerNationId);
                if (partner is not null && ShareDiplomaticAlignment(c, partner.Id, inst.HostNationId))
                {
                    partner.GrandProjectModifierSlices ??= new List<GrandProjectModifierSlice>(2);
                    partner.GrandProjectModifierSlices.Add(new GrandProjectModifierSlice(def.NameKey, def.PartnerModifiers));
                    NationalModifiers.Invalidate(partner);
                }
            }
        }
    }

    public static void RebuildModifiersFor(Campaign c, int nationId)
    {
        var nation = c?.NationById(nationId);
        if (nation is null) return;
        if (!IsEnabled(c))
        {
            if (nation.GrandProjectModifierSlices is { Count: > 0 })
            {
                nation.GrandProjectModifierSlices = null;
                NationalModifiers.Invalidate(nation);
            }
            return;
        }

        List<GrandProjectModifierSlice> next = null;
        for (var i = 0; i < c.GrandProjects.Count; i++)
        {
            var inst = c.GrandProjects[i];
            if (!IsOperationallyActive(c, inst)) continue;
            var def = GrandProjectCatalog.Find(inst.DefinitionId);
            if (def is null) continue;
            if (inst.HostNationId == nationId && def.HostModifiers.Count > 0)
            {
                next ??= new List<GrandProjectModifierSlice>(2);
                next.Add(new GrandProjectModifierSlice(def.NameKey, def.HostModifiers));
            }
            else if (inst.PartnerActive && inst.PartnerNationId == nationId &&
                     def.PartnerModifiers.Count > 0 && ShareDiplomaticAlignment(c, nationId, inst.HostNationId))
            {
                next ??= new List<GrandProjectModifierSlice>(2);
                next.Add(new GrandProjectModifierSlice(def.NameKey, def.PartnerModifiers));
            }
        }
        nation.GrandProjectModifierSlices = next;
        NationalModifiers.Invalidate(nation);
    }

    private static void ClearAllModifierCachesIfPresent(Campaign c)
    {
        for (var i = 0; i < c.Nations.Count; i++)
        {
            var n = c.Nations[i];
            if (n.GrandProjectModifierSlices is { Count: > 0 })
            {
                n.GrandProjectModifierSlices = null;
                NationalModifiers.Invalidate(n);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Save / Load Helpers
    // -------------------------------------------------------------------------

    public static List<GrandProjectInstance> CaptureSave(Campaign campaign)
    {
        if (campaign is null || campaign.GrandProjects.Count == 0) return new List<GrandProjectInstance>();
        var list = new List<GrandProjectInstance>(campaign.GrandProjects.Count);
        for (var i = 0; i < campaign.GrandProjects.Count; i++)
            list.Add(campaign.GrandProjects[i].Copy());
        return list;
    }

    public static void RestoreSave(Campaign campaign, List<GrandProjectInstance> projects, int nextId)
    {
        if (campaign is null) return;
        campaign.GrandProjects.Clear();
        var maxId = 0;
        if (projects is not null)
        {
            for (var i = 0; i < projects.Count; i++)
            {
                var item = projects[i];
                if (item is null || string.IsNullOrWhiteSpace(item.DefinitionId)) continue;
                campaign.GrandProjects.Add(item.Copy());
                if (item.Id > maxId) maxId = item.Id;
            }
        }
        campaign.NextGrandProjectId = Math.Max(Math.Max(1, nextId), maxId + 1);
        RebuildAllModifiers(campaign);
    }
}

public static partial class CampaignFingerprint
{
    /// <summary>
    /// Deterministic fingerprint contribution for Strategic Megaprojects.
    /// Adds zero bytes when no project exists and the toggle is at its default, keeping
    /// untouched legacy campaigns byte-for-byte identical.
    /// </summary>
    internal static void MixGrandProjects(Campaign campaign, ref ulong hash)
    {
        var enabled = GrandProjects.IsEnabled(campaign);
        if (!enabled)
        {
            MixText(ref hash, "grand_projects_disabled");
            Mix(ref hash, 0);
        }
        if (campaign.GrandProjects.Count == 0 && campaign.NextGrandProjectId <= 1) return;

        MixText(ref hash, "grand_projects");
        Mix(ref hash, campaign.NextGrandProjectId);
        Mix(ref hash, campaign.GrandProjects.Count);
        foreach (var p in Ordered(campaign.GrandProjects, item => item.Id))
        {
            Mix(ref hash, p.Id);
            MixText(ref hash, p.DefinitionId);
            Mix(ref hash, p.HostNationId);
            Mix(ref hash, p.StateMapIndex);
            Mix(ref hash, p.Stage);
            MixFloat(ref hash, p.ProgressDays);
            Mix(ref hash, p.StageDurationDays);
            Mix(ref hash, (int)p.Status);
            Mix(ref hash, p.PausedByPlayer ? 1 : 0);
            Mix(ref hash, p.PartnerNationId);
            Mix(ref hash, p.PartnerJoinedDay);
            Mix(ref hash, p.PartnerActive ? 1 : 0);
            Mix(ref hash, p.StartedDay);
            Mix(ref hash, p.CompletedDay);
            Mix(ref hash, p.LastSabotageDay);
            MixFloat(ref hash, p.TotalInvestedBillions);
        }
    }
}
