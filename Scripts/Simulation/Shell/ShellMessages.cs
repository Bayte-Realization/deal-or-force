using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation.Shell;

public enum ShellMessagePreset
{
    Calm,
    Paradox,
    Aoh
}

public enum ShellMessageEvent
{
    WarDeclared,
    UltimatumReceived,
    DefconChanged,
    NuclearStrike,
    PeaceOffer,
    ArmyEncircled,
    BattleLost,
    WorldNews,
    ElectionSoon,
    ResearchFinished,
    CommodityShortage,
    ConstructionFinished
}

public sealed record ShellMessageDelivery(
    bool PauseClock,
    bool ShowNewspaperCard,
    bool AddAlertRow,
    bool WriteLog,
    bool Speed5SafetyPause);

/// <summary>
/// Presets and routing matrix for campaign message delivery:
/// Calm ("Спокойно", default), Paradox (auto-pause discipline), and AoH (map flow).
/// </summary>
public static class ShellMessages
{
    public const string PresetSetting = "ui_shell_message_preset";

    public static ShellMessagePreset ParsePreset(string value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "paradox" => ShellMessagePreset.Paradox,
        "aoh" => ShellMessagePreset.Aoh,
        _ => ShellMessagePreset.Calm
    };

    public static ShellMessageDelivery Resolve(
        ShellMessagePreset preset,
        ShellMessageEvent evt,
        bool involvesPlayer,
        int currentSpeed = 1)
    {
        var speed5 = currentSpeed >= 5;
        return preset switch
        {
            ShellMessagePreset.Paradox => ResolveParadox(evt, involvesPlayer, speed5),
            ShellMessagePreset.Aoh => ResolveAoh(evt, involvesPlayer, speed5),
            _ => ResolveCalm(evt, involvesPlayer, speed5)
        };
    }

    private static ShellMessageDelivery ResolveCalm(ShellMessageEvent evt, bool involvesPlayer, bool speed5) => evt switch
    {
        ShellMessageEvent.NuclearStrike => new(true, true, true, true, true),
        ShellMessageEvent.UltimatumReceived when involvesPlayer => new(true, true, true, true, true),
        ShellMessageEvent.PeaceOffer when involvesPlayer => new(true, true, true, true, true),
        ShellMessageEvent.WarDeclared when involvesPlayer => new(true, true, true, true, true),
        ShellMessageEvent.WarDeclared => new(false, true, false, true, false),
        ShellMessageEvent.DefconChanged => new(speed5 && involvesPlayer, true, involvesPlayer, true, false),
        ShellMessageEvent.ArmyEncircled when involvesPlayer => new(speed5, true, true, true, speed5),
        ShellMessageEvent.BattleLost when involvesPlayer => new(false, false, true, true, false),
        ShellMessageEvent.WorldNews => new(false, true, false, true, false),
        ShellMessageEvent.ElectionSoon when involvesPlayer => new(false, false, true, true, false),
        ShellMessageEvent.ResearchFinished when involvesPlayer => new(false, false, true, true, false),
        ShellMessageEvent.CommodityShortage when involvesPlayer => new(false, false, true, true, false),
        ShellMessageEvent.ConstructionFinished when involvesPlayer => new(false, false, false, true, false),
        _ => new(false, false, false, true, false)
    };

    private static ShellMessageDelivery ResolveParadox(ShellMessageEvent evt, bool involvesPlayer, bool speed5) => evt switch
    {
        ShellMessageEvent.NuclearStrike => new(true, true, true, true, true),
        ShellMessageEvent.UltimatumReceived when involvesPlayer => new(true, true, true, true, true),
        ShellMessageEvent.PeaceOffer when involvesPlayer => new(true, true, true, true, true),
        ShellMessageEvent.WarDeclared => new(true, true, involvesPlayer, true, true),
        ShellMessageEvent.DefconChanged => new(true, true, involvesPlayer, true, true),
        ShellMessageEvent.ArmyEncircled when involvesPlayer => new(true, true, true, true, true),
        ShellMessageEvent.BattleLost when involvesPlayer => new(speed5, true, true, true, speed5),
        ShellMessageEvent.WorldNews => new(false, true, false, true, false),
        ShellMessageEvent.ElectionSoon when involvesPlayer => new(false, true, true, true, false),
        ShellMessageEvent.ResearchFinished when involvesPlayer => new(true, false, true, true, false),
        ShellMessageEvent.CommodityShortage when involvesPlayer => new(false, false, true, true, false),
        ShellMessageEvent.ConstructionFinished when involvesPlayer => new(false, false, false, true, false),
        _ => new(false, false, false, true, false)
    };

    private static ShellMessageDelivery ResolveAoh(ShellMessageEvent evt, bool involvesPlayer, bool speed5) => evt switch
    {
        ShellMessageEvent.NuclearStrike => new(true, true, true, true, true),
        ShellMessageEvent.UltimatumReceived when involvesPlayer => new(speed5, true, true, true, speed5),
        ShellMessageEvent.PeaceOffer when involvesPlayer => new(false, true, true, true, false),
        ShellMessageEvent.WarDeclared when involvesPlayer => new(speed5, true, true, true, speed5),
        ShellMessageEvent.WarDeclared => new(false, true, false, true, false),
        ShellMessageEvent.DefconChanged => new(false, true, false, true, false),
        ShellMessageEvent.ArmyEncircled when involvesPlayer => new(false, true, true, true, false),
        ShellMessageEvent.BattleLost when involvesPlayer => new(false, false, false, true, false),
        ShellMessageEvent.WorldNews => new(false, true, false, true, false),
        ShellMessageEvent.ElectionSoon when involvesPlayer => new(false, false, true, true, false),
        ShellMessageEvent.ResearchFinished when involvesPlayer => new(false, false, false, true, false),
        ShellMessageEvent.CommodityShortage when involvesPlayer => new(false, false, false, true, false),
        ShellMessageEvent.ConstructionFinished when involvesPlayer => new(false, false, false, true, false),
        _ => new(false, false, false, true, false)
    };

    public static IReadOnlyList<ShellMessageEvent> AllEvents => (ShellMessageEvent[])Enum.GetValues(typeof(ShellMessageEvent));
}
