#nullable enable
using System;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Applies first-boot mobile defaults (<c>world_compact</c> + <c>fast_mobile</c>) once per
/// device without altering desktop sessions or overwriting explicit player choices, and resolves
/// map pack switches cleanly when booting directly into an older <c>world_standard</c> save.
/// </summary>
public static class MobileFirstBootDefaults
{
    public const string MigrationKey = "mobile_first_boot_v1_applied";
    public const string DefaultMobileMapId = MapCatalog.CompactId;
    public const string DefaultMobileTempoPreset = "fast_mobile";
    public const string DefaultMobileHoursPerTick = "12";
    public const string DefaultMobileEconomyTickDays = "14";
    public const string DefaultMobileAiCalmCadence = "30";

    public static bool ApplyOnce(bool isMobilePlatform)
    {
        if (!isMobilePlatform || PlayerSettings.Bool(MigrationKey, false))
            return false;

        PlayerSettings.Set(MigrationKey, true);
        var applied = false;

        if (!PlayerSettings.Has("map_pack"))
        {
            if (!MapCatalog.IsAvailable(DefaultMobileMapId))
                MapCatalog.RefreshCatalog();
            if (MapCatalog.IsAvailable(DefaultMobileMapId))
            {
                PlayerSettings.Set("map_pack", DefaultMobileMapId);
                applied = true;
            }
        }

        if (!PlayerSettings.Has("sim_tempo_preset"))
        {
            PlayerSettings.Set("sim_tempo_preset", DefaultMobileTempoPreset);
            if (!PlayerSettings.Has("hours_per_tick"))
                PlayerSettings.Set("hours_per_tick", DefaultMobileHoursPerTick);
            if (!PlayerSettings.Has("economy_tick_days"))
                PlayerSettings.Set("economy_tick_days", DefaultMobileEconomyTickDays);
            if (!PlayerSettings.Has("ai_calm_cadence"))
                PlayerSettings.Set("ai_calm_cadence", DefaultMobileAiCalmCadence);
            applied = true;
        }

        return applied;
    }

    public static string ResolveBootMapId(
        string? configuredMapId,
        bool loadLatest,
        string? loadSlot,
        string? saveRoot)
    {
        if ((loadLatest || !string.IsNullOrWhiteSpace(loadSlot)) && !string.IsNullOrWhiteSpace(saveRoot))
        {
            try
            {
                var saves = SaveSystem.List(saveRoot);
                var target = loadLatest
                    ? saves.FirstOrDefault()
                    : saves.FirstOrDefault(s => string.Equals(s.Slot, loadSlot, StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                {
                    var saveMapId = MapCatalog.ResolveSaveMapId(target.MapId, target.ProvinceCount);
                    if (!string.IsNullOrWhiteSpace(saveMapId) && MapCatalog.IsAvailable(saveMapId))
                        return saveMapId;
                }
            }
            catch
            {
                // Keep configured map pack if save headers cannot be inspected on boot.
            }
        }

        return configuredMapId ?? "";
    }
}
