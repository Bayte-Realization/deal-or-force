using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>How often the officers of every country are asked whether they have had enough, in days.</summary>
    public int CoupReviewDays { get; set; } = 30;
    /// <summary>Stability at and above which nobody plots; below it the risk grows with how far below.</summary>
    public float CoupStabilityThreshold { get; set; } = 0.35f;
    /// <summary>The chance of a plot in a review for a country at no stability at all, before its government and its neighbours.</summary>
    public float CoupBaseChance { get; set; } = 0.04f;
    /// <summary>How long a plot is in the making: a government that is stable again by then has seen it off.</summary>
    public int CoupPlotDays { get; set; } = 60;
    /// <summary>What a coup next door does to the risk, for this many days after it.</summary>
    public float CoupContagion { get; set; } = 1.6f;
    public int CoupContagionDays { get; set; } = 1095;
    /// <summary>What a war being lost does to the risk.</summary>
    public float CoupLosingWar { get; set; } = 2f;
    /// <summary>The order a junta brings, for a while: stability and war support the day it takes power.</summary>
    public float CoupStabilityGain { get; set; } = 0.12f;
    public float CoupWarSupportGain { get; set; } = 0.05f;
    /// <summary>How long a junta that has just taken power is left alone by its own officers.</summary>
    public int CoupGraceDays { get; set; } = 730;
}

public sealed partial class Nation
{
    /// <summary>The day officers began plotting against this government, or -1; and the day of its last coup.</summary>
    public int CoupPlotDay = -1;
    public int LastCoupDay = -99999;
}

/// <summary>
/// Military coups: the way a government ends in the 2020s far more often than by conquest -
/// Myanmar, Mali, Guinea, Sudan, Burkina Faso twice, Niger, Gabon in four years.
///
/// A country whose stability has fallen below <see cref="Defines.CoupStabilityThreshold"/>
/// risks a plot every review, more the further below it is, more for a government that rests on
/// the army anyway, more when it is losing a war, and more in the years after a coup next door.
/// A plot takes <see cref="Defines.CoupPlotDays"/> to ripen and is in the news of the country it
/// is against: a government that is stable again by then has seen it off. One that is not
/// becomes what common/coups says a coup makes it - a military junta - with the brief order a
/// junta brings, and the coldness of the governments whose creed says coups are the end of
/// something (liberal, social-democratic, conservative); Moscow welcomed the Sahel's juntas.
///
/// A player is told the day the plot begins, and has the same sixty days.
/// </summary>
public static class Coups
{
    private static Dictionary<string, float> _risk;
    private static string _installsGovernment = "military_junta";
    private static string _installsIdeology = "nationalism";
    private static HashSet<string> _disapproving;

    public static void ResetCatalog() { _risk = null; _disapproving = null; }

    private static void EnsureLoaded()
    {
        if (_risk is not null) return;
        _risk = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        _disapproving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in GameDataLoader.DataFiles("common", "coups"))
        {
            if (file is null || !File.Exists(file)) continue;
            try
            {
                using var document = JsonDocument.Parse(StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
                Merge(document.RootElement, null);
            }
            catch (Exception exception)
            {
                DiagnosticLogs.Write("simulation", $"COUPS_FAILED file={Path.GetFileName(file)} error={exception.Message}");
            }
        }
    }

    /// <summary>One document of common/coups, base or mod.</summary>
    public static void Merge(JsonElement root, List<string> errors)
    {
        _risk ??= new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        _disapproving ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("government_risk", out var risks) && risks.ValueKind == JsonValueKind.Object)
            foreach (var entry in risks.EnumerateObject())
                if (entry.Value.TryGetSingle(out var value)) _risk[entry.Name] = MathF.Max(0f, value);
        if (root.TryGetProperty("installs", out var installs) && installs.ValueKind == JsonValueKind.Object)
        {
            if (installs.TryGetProperty("government", out var government) && government.ValueKind == JsonValueKind.String)
                _installsGovernment = government.GetString() ?? _installsGovernment;
            if (installs.TryGetProperty("ideology", out var ideology) && ideology.ValueKind == JsonValueKind.String)
                _installsIdeology = ideology.GetString() ?? _installsIdeology;
        }
        if (root.TryGetProperty("disapproving_ideologies", out var disapproving) && disapproving.ValueKind == JsonValueKind.Array)
            foreach (var item in disapproving.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) _disapproving.Add(item.GetString() ?? "");
    }

    /// <summary>How much more or less likely this form of government is to be overthrown by its army.</summary>
    public static float GovernmentRisk(Nation nation)
    {
        EnsureLoaded();
        return _risk.TryGetValue(nation?.Government ?? "", out var value) ? value : 1f;
    }

    public static void Tick(Campaign c)
    {
        if (c is null || c.ElapsedDays <= 0 || !c.FeatureEnabled("politics") || !MechanicFeatures.SystemEnabled(c, nameof(Coups))) return;
        var defines = c.Defines;
        var every = Math.Max(1, defines.CoupReviewDays);
        if (c.ElapsedDays % every != 11 % every) return;
        EnsureLoaded();
        foreach (var nation in c.Nations.ToArray())
        {
            // A faction's coup is its own leader's business; the RSF is not overthrown by its officers.
            if (nation.Movement is not null || nation.FactionId.Length > 0 || nation.Population <= 0 || c.Fallen.Contains(nation.Id)) continue;
            // A dependency's army is its sovereign's: Pitcairn has no officers to plot with.
            if (nation.IsSubject) continue;
            if (c.StatesOf(nation).Length == 0) continue;

            if (nation.CoupPlotDay >= 0)
            {
                if (c.ElapsedDays - nation.CoupPlotDay < defines.CoupPlotDays) continue;
                if (nation.Stability >= defines.CoupStabilityThreshold)
                {
                    nation.CoupPlotDay = -1;
                    c.Chronicle.Record(c, "coup_foiled", nation.Id, isPublic: false);
                    continue;
                }
                Seize(c, nation);
                continue;
            }

            var chance = Chance(c, nation);
            if (chance <= 0f) continue;
            if (Escalation.Roll(c.Seed, nation.Id, 0x0C0, c.ElapsedDays, 0x7) >= chance) continue;
            nation.CoupPlotDay = c.ElapsedDays;
            c.Chronicle.Record(c, "coup_plot", nation.Id, isPublic: nation.IsHuman);
            DiagnosticLogs.Write("simulation", $"COUP_PLOT nation={nation.Tag} stability={nation.Stability:0.00} chance={chance:0.000}");
        }
    }

    /// <summary>The chance of a plot against this government in one review.</summary>
    public static float Chance(Campaign c, Nation nation)
    {
        if (c is null || nation is null || !MechanicFeatures.SystemEnabled(c, nameof(Coups)) ||
            !c.FeatureEnabled("politics") || nation.IsSubject || nation.Movement is not null ||
            nation.FactionId.Length > 0 || c.Fallen.Contains(nation.Id) ||
            nation.DomesticCrisis?.CooldownUntilDay > c.ElapsedDays) return 0f;
        var defines = c.Defines;
        var threshold = MathF.Max(0.01f, defines.CoupStabilityThreshold);
        if (nation.Stability >= threshold) return 0f;
        // The officers who have just taken power hold the army for a while.
        if (c.ElapsedDays - nation.LastCoupDay < defines.CoupGraceDays) return 0f;
        var below = (threshold - nation.Stability) / threshold;
        var chance = defines.CoupBaseChance * below * GovernmentRisk(nation);
        if (LosingAWar(c, nation)) chance *= defines.CoupLosingWar;
        if (CoupNextDoor(c, nation)) chance *= defines.CoupContagion;
        return M.Clamp01(chance);
    }

    /// <summary>How the plotting looks from the presidential palace: none, low, high, or under way.</summary>
    public static string Outlook(Campaign c, Nation nation)
    {
        if (c is null || nation is null || !c.FeatureEnabled("politics") || !MechanicFeatures.SystemEnabled(c, nameof(Coups))) return "none";
        if (nation.CoupPlotDay >= 0) return "plot";
        var chance = Chance(c, nation);
        return chance <= 0f ? "none" : chance < c.Defines.CoupBaseChance * 0.25f ? "low" : "high";
    }

    /// <summary>
    /// One line for the country card, from <see cref="Outlook"/>. A faint chance stays quiet,
    /// the same as the alert rail. This does not roll a plot and does not start one.
    /// </summary>
    public static (string Text, int Level) Notice(Campaign c, Nation nation)
    {
        switch (Outlook(c, nation))
        {
            case "plot": return (Loc.Get("Officers are plotting against the government."), 2);
            case "high": return (Loc.Get("A coup is possible."), 1);
            default: return ("", 0);
        }
    }

    private static bool LosingAWar(Campaign c, Nation nation)
    {
        foreach (var war in c.Wars)
        {
            if (war.Ended || !war.Involves(nation.Id)) continue;
            var score = war.IsAttacking(nation.Id) ? war.WarScore : -war.WarScore;
            if (score <= -20f) return true;
        }
        return false;
    }

    private static bool CoupNextDoor(Campaign c, Nation nation)
    {
        var since = c.ElapsedDays - Math.Max(0, c.Defines.CoupContagionDays);
        foreach (var mapIndex in CampaignDiplomacy.Neighbours(c, nation.MapIndex))
            if (NationAt(c, mapIndex) is { LastCoupDay: var day } && day >= since) return true;
        return false;
    }

    private static Campaign _mapCampaign;
    private static int _mapDay = int.MinValue;
    private static int _mapCount = -1;
    private static readonly Dictionary<int, Nation> NationByMap = new();

    /// <summary>
    /// The daily map index for a neighbour. The old loop asked every nation in the world
    /// whether its map index matched, for every neighbour of every country, on every coup
    /// review; that is a quadratic scan hiding in a fifteen-millisecond step.
    /// </summary>
    private static Nation NationAt(Campaign c, int mapIndex)
    {
        if (!ReferenceEquals(_mapCampaign, c) || _mapDay != c.ElapsedDays || _mapCount != c.Nations.Count)
        {
            _mapCampaign = c;
            _mapDay = c.ElapsedDays;
            _mapCount = c.Nations.Count;
            NationByMap.Clear();
            foreach (var nation in c.Nations)
                if (nation.MapIndex >= 0) NationByMap[nation.MapIndex] = nation;
        }
        return NationByMap.GetValueOrDefault(mapIndex);
    }

    /// <summary>The army takes power.</summary>
    public static void Seize(Campaign c, Nation nation)
    {
        if (c is null || nation is null) return;
        EnsureLoaded();
        var defines = c.Defines;
        var before = nation.Government;
        nation.CoupPlotDay = -1;
        nation.LastCoupDay = c.ElapsedDays;
        nation.Government = GovernmentCatalog.All.Any(g => string.Equals(g.Id, _installsGovernment, StringComparison.OrdinalIgnoreCase)) ? _installsGovernment : nation.Government;
        if (IdeologyCatalog.All.Any(i => string.Equals(i.Id, _installsIdeology, StringComparison.OrdinalIgnoreCase)) && before != nation.Government) nation.Ideology = _installsIdeology;
        nation.PoliticsChangedDay = c.ElapsedDays;
        nation.Stability = M.Clamp01(nation.Stability + defines.CoupStabilityGain);
        nation.WarSupport = M.Clamp01(nation.WarSupport + defines.CoupWarSupportGain);
        RegisterOutcome(c, nation, "Military coup");
        foreach (var other in c.Nations)
        {
            if (other.Id == nation.Id || other.Movement is not null || other.Population <= 0) continue;
            if (!_disapproving.Contains(other.Ideology)) continue;
            if (CampaignDiplomacy.Get(c, other.Id, nation.Id) is { } relation)
                OpinionSystem.Remember(c, relation, "seized_power_by_coup");
        }
        c.Chronicle.Record(c, before == nation.Government ? "coup_counter" : "coup", nation.Id);
        c.MarkStrategicChange();
        DiagnosticLogs.Write("simulation", $"COUP nation={nation.Tag} from={before} to={nation.Government}");
    }

    // Both plotting and accumulated institutional pressure describe the same army
    // and institutions. One successful coup cancels the other route's pending threat.
    internal static void RegisterOutcome(Campaign c, Nation nation, string outcome)
    {
        nation.CoupPlotDay = -1;
        nation.LastCoupDay = c.ElapsedDays;
        var memory = nation.DomesticCrisis ??= new();
        if (outcome == "Military coup") nation.LastMilitaryCoupDay = c.ElapsedDays;
        memory.LastOutcomeId = outcome;
        memory.LastOutcomeDay = c.ElapsedDays;
        memory.CooldownUntilDay = (int)Math.Min(int.MaxValue, (long)c.ElapsedDays +
            Math.Max(0, Math.Max(c.Defines.CoupGraceDays, c.Defines.CoupAftermathCooldownDays)));
        memory.DaysAboveThreshold = 0;
        memory.CoupPressure *= .35f;
        Rulers.ReplaceHeadOfState(c, nation, "");
        NationalModifiers.Invalidate(nation);
    }
}
