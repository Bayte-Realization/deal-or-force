using System;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private sealed class UiShellScope : IDisposable
    {
        private readonly string _previous;

        public UiShellScope(string previous) => _previous = previous;

        public void Dispose()
        {
            if (!string.IsNullOrEmpty(_previous) &&
                !string.Equals(SettingsCatalog.Chosen("ui_shell"), _previous, StringComparison.OrdinalIgnoreCase))
            {
                SettingsCatalog.Store("ui_shell", _previous);
            }
        }
    }

    private static IDisposable EnterClassicUiShellAndBeginSession(DealOrForceGame game)
    {
        var previous = SettingsCatalog.Chosen("ui_shell");
        if (!string.Equals(previous, "classic", StringComparison.OrdinalIgnoreCase))
        {
            SettingsCatalog.Store("ui_shell", "classic");
            game.Call("ApplySetting", "ui_shell");
        }
        game.Call("BeginSession");
        return new UiShellScope(previous);
    }

    private async Task VerifyLedgerAndNewShell(DealOrForceGame game)
    {
        await VerifyLedgerCharts(game);
        SettingsCatalog.Store("ui_shell", "new");
        game.Call("ApplySetting", "ui_shell");
        await Wait(.2);
        Check((game.FindChild("NewCampaignShell", true, false) as Control)?.Visible == true,
            "new campaign shell activates cleanly in shipped release");
        Check((game.FindChild("ShellMinimap", true, false) as Control)?.Visible == true,
            "new shell live minimap is visible in shipped release");
        await Capture("new-shell-rest");
    }
}
