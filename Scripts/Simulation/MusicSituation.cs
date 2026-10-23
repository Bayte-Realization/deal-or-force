using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public enum MusicMood { Any, Calm, Uneasy, Crisis, War, Recovery, SystemicCrisis }
public sealed record MusicReadout(MusicMood Mood, string Reason, float WorldWarExposure = 0, string Region = "", string SubRegion = "", string Country = "");

/// <summary>Presentation policy, layered through music/*.json rather than saved as gameplay rules.</summary>
public sealed class MusicPolicy
{
    public float FadeSeconds = 7, EscalateSeconds = 6, ReleaseSeconds = 35, MinimumHoldSeconds = 45, WarMinimumHoldSeconds = 12;
    public float WorldExposureWarning = .30f, WorldExposureCrisis = .60f, ShortageWarning = .18f, ShortageCrisis = .45f;
    public float StabilityWarning = .35f, RegionalRadicalismWarning = .65f;
    public int RecoveryDays = 180;
    public float SystemicWarExposure = .60f, SystemicWarGdpShare = .55f;
    public int SystemicBelligerents = 10;
    public string EmergencyFeature = "world_order_emergency";

    internal void Merge(JsonElement e, List<string> errors, string origin)
    {
        if (e.ValueKind != JsonValueKind.Object) { errors?.Add(origin + ": director must be an object"); return; }
        float Read(string id, float previous, float min, float max)
        {
            if (!e.TryGetProperty(id, out var v)) return previous;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetSingle(out var n) || !float.IsFinite(n))
            { errors?.Add(origin + ": invalid director." + id); return previous; }
            return Math.Clamp(n, min, max);
        }
        FadeSeconds = Read("fade_seconds", FadeSeconds, .1f, 30);
        EscalateSeconds = Read("escalate_seconds", EscalateSeconds, 0, 120);
        ReleaseSeconds = Read("release_seconds", ReleaseSeconds, 0, 300);
        MinimumHoldSeconds = Read("minimum_hold_seconds", MinimumHoldSeconds, 0, 300);
        WarMinimumHoldSeconds = Read("war_minimum_hold_seconds", WarMinimumHoldSeconds, 0, 120);
        WorldExposureWarning = Read("world_exposure_warning", WorldExposureWarning, 0, 1);
        WorldExposureCrisis = MathF.Max(WorldExposureWarning, Read("world_exposure_crisis", WorldExposureCrisis, 0, 1));
        ShortageWarning = Read("shortage_warning", ShortageWarning, 0, 1);
        ShortageCrisis = MathF.Max(ShortageWarning, Read("shortage_crisis", ShortageCrisis, 0, 1));
        StabilityWarning = Read("stability_warning", StabilityWarning, 0, 1);
        RegionalRadicalismWarning = Read("regional_radicalism_warning", RegionalRadicalismWarning, 0, 1);
        RecoveryDays = (int)Read("recovery_days", RecoveryDays, 0, 3650);
        SystemicWarExposure = Read("systemic_war_exposure", SystemicWarExposure, 0, 1);
        SystemicWarGdpShare = Read("systemic_war_gdp_share", SystemicWarGdpShare, 0, 1);
        SystemicBelligerents = (int)Read("systemic_belligerents", SystemicBelligerents, 2, 100);
        if (e.TryGetProperty("emergency_feature", out var feature))
        {
            if (feature.ValueKind == JsonValueKind.String && MechanicFeatures.ValidId(feature.GetString())) EmergencyFeature = feature.GetString();
            else errors?.Add(origin + ": invalid director.emergency_feature");
        }
    }
}

/// <summary>No soundtrack random roll ever enters the campaign. Music only reads existing world state.</summary>
public static class MusicSituation
{
    public static MusicReadout Read(Campaign c, Nation n, ScenarioState region = null, bool observer = false)
    {
        if (c is null || n is null) return new(MusicMood.Calm, "No campaign pressure recorded.");
        var policy = MusicCatalog.Director;
        var active = c.Wars.Where(w => !w.Ended).ToArray();
        var involved = new HashSet<int>();
        foreach (var nation in c.Nations)
            if (nation.Movement is not { Landless: true } && active.Any(w => w.Involves(nation.Id))) involved.Add(nation.Id);
        double total = 0, fighting = 0;
        foreach (var nation in c.Nations)
        {
            if (!double.IsFinite(nation.Population) || nation.Population <= 0 || nation.Movement is { Landless: true }) continue;
            total += nation.Population; if (involved.Contains(nation.Id)) fighting += nation.Population;
        }
        var exposure = total > 0 ? (float)Math.Clamp(fighting / total, 0, 1) : 0;
        var paletteNation = observer && region is not null ? c.Nations.FirstOrDefault(x => x.MapIndex == region.NationMapIndex) ?? n : n;
        MusicReadout Result(MusicMood mood, string reason) => new(mood, reason, exposure,
            MusicCatalog.RegionKey(paletteNation.Continent), MusicCatalog.RegionKey(paletteNation.Region),
            MusicCatalog.RegionKey(paletteNation.Tag));
        // Unknown feature IDs default to enabled elsewhere; this deliberate extension signal
        // must be explicitly present, otherwise an old campaign would sound an emergency.
        var declaredEmergency = c.FeatureOverrides.TryGetValue(policy.EmergencyFeature, out var emergency)
            ? emergency : c.Defines.Features.TryGetValue(policy.EmergencyFeature, out var initialEmergency) && initialEmergency;
        var gdp = c.Nations.Where(x => float.IsFinite(x.Gdp) && x.Gdp > 0 && x.Movement is not { Landless: true }).Sum(x => (double)x.Gdp);
        var warGdp = c.Nations.Where(x => involved.Contains(x.Id) && float.IsFinite(x.Gdp) && x.Gdp > 0).Sum(x => (double)x.Gdp);
        if (declaredEmergency || involved.Count >= policy.SystemicBelligerents && exposure >= policy.SystemicWarExposure &&
            gdp > 0 && warGdp / gdp >= policy.SystemicWarGdpShare)
            return Result(MusicMood.SystemicCrisis, declaredEmergency ? "The scenario explicitly marks a world-order emergency." :
                "Systemic war involves much of the world's population and economy.");
        if (!observer && active.Any(w => w.Involves(n.Id))) return Result(MusicMood.War, "Your country is fighting an active war.");
        if (!observer)
        {
            var institutional = c.FeatureEnabled("politics") && (
                c.FeatureEnabled("coups") && n.CoupPlotDay >= 0 ||
                c.FeatureEnabled("domestic_crises") && n.DomesticCrisis is { CooldownUntilDay: var cooldown } crisis &&
                cooldown <= c.ElapsedDays && crisis.CoupPressure >= c.Defines.CoupPressureThreshold);
            var defaulted = c.FeatureEnabled("public_debt") && n.DebtCondition >= DebtCondition.Default;
            if (institutional || defaulted || n.ShortageIndex >= policy.ShortageCrisis)
                return Result(MusicMood.Crisis, institutional ? "The government is under acute institutional pressure." :
                    defaulted ? "Sovereign payments are in default." : "Essential shortages are severe.");
            if (c.FeatureEnabled("escalation") && Escalation.Hottest(c, c.Defines.TensionCrisisAt)
                    .Any(r => r.A == n.Id || r.B == n.Id))
                return Result(MusicMood.Uneasy, "Your country is in a dangerous international standoff.");
            if (n.Stability < policy.StabilityWarning || n.ShortageIndex >= policy.ShortageWarning)
                return Result(MusicMood.Uneasy, "Domestic conditions are strained, even without a war.");
            if (region is not null && region.NationMapIndex == n.MapIndex && region.Radicalism >= policy.RegionalRadicalismWarning)
                return Result(MusicMood.Uneasy, "The selected governed region has high radicalism.");
        }
        if (exposure >= policy.WorldExposureCrisis) return Result(MusicMood.Crisis, "A large share of the world's population lives in countries at war.");
        if (exposure >= policy.WorldExposureWarning) return Result(MusicMood.Uneasy, "War abroad is putting the wider world under pressure.");
        if (!observer && policy.RecoveryDays > 0 && c.Wars.Any(w => w.Ended && w.Involves(n.Id) &&
                w.EndedDay >= 0 && w.EndedDay <= c.ElapsedDays && c.ElapsedDays - w.EndedDay <= policy.RecoveryDays))
            return Result(MusicMood.Recovery, "Your country recently left a war and is recovering.");
        return Result(MusicMood.Calm, "No acute national or worldwide pressure is recorded.");
    }
    public static int Urgency(MusicMood mood) => mood switch
    { MusicMood.SystemicCrisis => 4, MusicMood.War => 3, MusicMood.Crisis => 2, MusicMood.Uneasy => 1, _ => 0 };
    public static string Name(MusicMood mood) => mood switch
    { MusicMood.SystemicCrisis => "World order at risk", MusicMood.War => "Wartime pressure", MusicMood.Crisis => "Acute crisis", MusicMood.Uneasy => "Uneasy calm",
        MusicMood.Recovery => "After the storm", _ => "Quiet world" };
}

/// <summary>Real-time hysteresis keeps a fluctuating gauge from changing tracks every second.</summary>
public sealed class MusicMoodLatch
{
    public MusicMood Current { get; private set; } = MusicMood.Calm;
    private MusicMood _candidate = MusicMood.Calm;
    private double _since, _lastChange;
    private bool _initialized;
    public bool Observe(MusicMood mood, double seconds, MusicPolicy policy, bool reset = false)
    {
        if (!_initialized || reset)
        { _initialized = true; _candidate = Current = mood; _since = _lastChange = seconds; return true; }
        if (mood == Current) { _candidate = Current; _since = seconds; return false; }
        if (_candidate != mood) { _candidate = mood; _since = seconds; }
        var escalation = MusicSituation.Urgency(mood) > MusicSituation.Urgency(Current);
        var delay = escalation ? policy.EscalateSeconds : policy.ReleaseSeconds;
        var hold = mood is MusicMood.War or MusicMood.SystemicCrisis && escalation ? policy.WarMinimumHoldSeconds : policy.MinimumHoldSeconds;
        if (seconds - _since < delay || seconds - _lastChange < hold) return false;
        Current = mood; _lastChange = seconds; return true;
    }
}
