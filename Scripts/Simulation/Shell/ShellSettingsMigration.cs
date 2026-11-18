using System;

namespace DealOrForce.Simulation.Shell;

/// <summary>
/// Upgrades an existing unmigrated settings.json to the Wave 2 defaults ("new" shell, live minimap
/// enabled, and neutral message preset keys) exactly once. Subsequent manual changes in Settings are
/// preserved permanently because the migration flag stays true.
/// </summary>
public static class ShellSettingsMigration
{
    public const string MigrationKey = "ui_shell_v2_migrated";
    public const string ShellSettingKey = "ui_shell";
    public const string MinimapSettingKey = "ui_shell_minimap";

    public static bool ApplyOnce()
    {
        // Always normalize legacy preset aliases silently if present in an older settings.json
        var preset = PlayerSettings.Text(ShellMessages.PresetSetting, "");
        if (string.Equals(preset, "paradox", StringComparison.OrdinalIgnoreCase))
            PlayerSettings.Set(ShellMessages.PresetSetting, "focus");
        else if (string.Equals(preset, "aoh", StringComparison.OrdinalIgnoreCase))
            PlayerSettings.Set(ShellMessages.PresetSetting, "dispatch");

        if (PlayerSettings.Bool(MigrationKey, false))
            return false;

        PlayerSettings.Set(MigrationKey, true);
        PlayerSettings.Set(ShellSettingKey, "new");
        PlayerSettings.Set(MinimapSettingKey, true);
        return true;
    }
}
