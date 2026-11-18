using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private bool IsAnyShellModalOpen() =>
        _handbookLayer is { Visible: true }
        || _peaceLayer is { Visible: true }
        || _defconLayer is { Visible: true }
        || _pauseMenuLayer is { Visible: true }
        || _settingsLayer is { Visible: true }
        || _saveLoadLayer is { Visible: true }
        || _pickerLayer is { Visible: true }
        || _stateAffairsLayer is { Visible: true }
        || _sovereignFinanceLayer is { Visible: true }
        || _ledgerLayer is { Visible: true }
        || _warLedgerLayer is { Visible: true }
        || _diplomacyLayer is { Visible: true }
        || _conflictsLayer is { Visible: true }
        || _policyLayer is { Visible: true }
        || _tradeScreenLayer is { Visible: true }
        || _productionLayer is { Visible: true }
        || _supportLayer is { Visible: true }
        || _researchLayer is { Visible: true }
        || _doctrineLayer is { Visible: true };

    private bool HandleNewShellHotkey(InputEventKey key)
    {
        if (!IsNewShellActive || _campaign is null || _hudLayer is not { Visible: true })
            return false;

        var letter = Letter(key);
        var raw = letter switch
        {
            Key.Space => "Space",
            Key.Escape => "Escape",
            Key.Home => "Home",
            Key.Slash => "Slash",
            Key.Equal or Key.Plus or Key.KpAdd => "Equal",
            Key.Minus or Key.KpSubtract => "Minus",
            >= Key.Key0 and <= Key.Key9 => ((char)('0' + (letter - Key.Key0))).ToString(),
            >= Key.Kp0 and <= Key.Kp9 => ((char)('0' + (letter - Key.Kp0))).ToString(),
            >= Key.A and <= Key.Z => ((char)('A' + (letter - Key.A))).ToString(),
            _ => ""
        };

        var textFocused = GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;
        var modalOpen = IsAnyShellModalOpen();

        if (!ShellHotkeys.TryResolve(
                raw,
                key.CtrlPressed || key.MetaPressed,
                key.AltPressed,
                textFocused,
                _freeCameraMode,
                modalOpen,
                out var action))
            return false;

        switch (action)
        {
            case ShellHotkeyAction.Country: ToggleCampaignScreen("Nation"); break;
            case ShellHotkeyAction.StateAffairs: ToggleCampaignScreen("State Affairs"); break;
            case ShellHotkeyAction.Economy: ToggleCampaignScreen("Economy"); break;
            case ShellHotkeyAction.Trade: ToggleCampaignScreen("Trade"); break;
            case ShellHotkeyAction.Production: ToggleCampaignScreen("Production"); break;
            case ShellHotkeyAction.Research: ToggleCampaignScreen("Research"); break;
            case ShellHotkeyAction.Military: ToggleCampaignScreen("Military"); break;
            case ShellHotkeyAction.Diplomacy: ToggleCampaignScreen("Diplomacy"); break;
            case ShellHotkeyAction.Wars: ToggleCampaignScreen("Wars"); break;
            case ShellHotkeyAction.Ledger: ToggleCampaignScreen("Ledger"); break;
            case ShellHotkeyAction.Search: ToggleNavigator(); break;

            case ShellHotkeyAction.MapPolitical:
                _globe?.SetMapMode(StrategicMapMode.Political);
                RefreshShellMapBar();
                break;
            case ShellHotkeyAction.MapDiplomatic:
                _globe?.SetMapMode(StrategicMapMode.Diplomatic);
                RefreshShellMapBar();
                break;
            case ShellHotkeyAction.MapEconomy:
                _globe?.SetMapMode(StrategicMapMode.Economy);
                RefreshShellMapBar();
                break;
            case ShellHotkeyAction.MapResources:
                _globe?.SetMapMode(StrategicMapMode.Resources);
                RefreshShellMapBar();
                break;
            case ShellHotkeyAction.MapWar:
                _globe?.SetMapMode(StrategicMapMode.Wars);
                RefreshShellMapBar();
                break;
            case ShellHotkeyAction.MapMore:
                ToggleShellMapMorePopover();
                break;
            case ShellHotkeyAction.ToggleOutliner:
                ToggleShellOutliner();
                break;

            case ShellHotkeyAction.TogglePause:
                TogglePause();
                RefreshShellDatePlate();
                break;
            case ShellHotkeyAction.Speed1: SetSpeed(1, resume: false); RefreshShellDatePlate(); break;
            case ShellHotkeyAction.Speed2: SetSpeed(2, resume: false); RefreshShellDatePlate(); break;
            case ShellHotkeyAction.Speed3: SetSpeed(3, resume: false); RefreshShellDatePlate(); break;
            case ShellHotkeyAction.Speed4: SetSpeed(4, resume: false); RefreshShellDatePlate(); break;
            case ShellHotkeyAction.Speed5: SetSpeed(5, resume: false); RefreshShellDatePlate(); break;
            case ShellHotkeyAction.SpeedFaster: SetSpeed(_speed + 1, resume: false); RefreshShellDatePlate(); break;
            case ShellHotkeyAction.SpeedSlower: SetSpeed(_speed - 1, resume: false); RefreshShellDatePlate(); break;
            case ShellHotkeyAction.FocusCapital:
                FocusOwnCapital();
                break;
            case ShellHotkeyAction.MenuOrClose:
                if (_shellRecentNewsPanel is { Visible: true })
                { CloseShellRecentNewsModal(); return true; }
                if (_shellMapMorePopover is { Visible: true })
                { CloseShellMapMorePopover(); return true; }
                return false;
        }

        return true;
    }
}
