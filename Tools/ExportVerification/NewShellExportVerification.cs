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

    private async Task VerifyRegionSelectionClose(DealOrForceGame game)
    {
        SettingsCatalog.Store("ui_shell", "new");
        game.Call("ApplySetting", "ui_shell");
        game.VerificationFramePunjab();
        await Wait(1.5);
        var panel = game.FindChild("ShellWorkPanel", true, false) as Control;
        var close = panel?.FindChild("CloseShellWorkPanel", true, false) as Button;
        Check(close is not null, "region dossier close button exists");
        if (close is null) return;

        foreach (var viaEscape in new[] { false, true })
        {
            var route = viaEscape ? "Escape" : "close button";
            Check(game.VerificationSelectRegionAtCentre() && panel.Visible &&
                game.VerificationRegionSelectionMatchesMap(), $"{route}: a region and province are highlighted");
            if (!viaEscape) await Capture("region-selected");

            if (viaEscape)
            {
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = false });
            }
            else close.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.2);
            Check(!panel.Visible && game.VerificationRegionSelectionCleared(), $"{route}: dossier, region and province selection are cleared");
            if (!viaEscape) await Capture("region-cleared");
        }
    }
}

public partial class DealOrForceGame
{
    internal void VerificationFramePunjab()
    {
        _globe.SetFlatMap(true);
        _globe.FocusOn(30.1f, 71.3f, 5.5f);
    }

    internal bool VerificationSelectRegionAtCentre()
    {
        if (!_globe.VerificationPickAt(GetViewport().GetVisibleRect().Size / 2, out var pick, updateSelection: true))
            return false;
        OnCountryPicked(pick);
        return pick.Nation?.Tag == "PAK";
    }

    internal bool VerificationRegionSelectionMatchesMap() =>
        _selectedState is not null && _selectedProvinceId > 0 && _globe.VerificationShowsRegionSelection(_selectedProvinceId);

    internal bool VerificationRegionSelectionCleared() =>
        _selectedState is null && _selectedProvinceId < 0 && _globe.VerificationGroundSelectionCleared();
}

public sealed partial class GlobePresentation
{
    internal bool VerificationShowsRegionSelection(int provinceId) =>
        _globeMaterial.GetShaderParameter("selection_active").AsSingle() == 1 &&
        _globeMaterial.GetShaderParameter("selected_region_live").AsSingle() == 1 &&
        _globeMaterial.GetShaderParameter("selected_province_id").AsSingle() == provinceId;

    internal bool VerificationGroundSelectionCleared() =>
        _globeMaterial.GetShaderParameter("selection_active").AsSingle() == 0 &&
        _globeMaterial.GetShaderParameter("selected_region_live").AsSingle() == 0 &&
        _globeMaterial.GetShaderParameter("selected_region_id").AsSingle() == -1 &&
        _globeMaterial.GetShaderParameter("selected_province_id").AsSingle() == -1;
}
