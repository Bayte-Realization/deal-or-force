using System;
using System.Linq;
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

    private async Task VerifyMenuLayering(DealOrForceGame game)
    {
        SettingsCatalog.Store("ui_shell", "new");
        game.Call("ApplySetting", "ui_shell");
        game.Call("ShowPauseMenu");
        await Wait(.25);
        var pause = game.FindChild("PauseMenu", true, false) as Control;
        var settingsButton = pause?.FindChild("PauseOpenSettings", true, false) as Button;
        var mediaButton = pause?.FindChild("OpenMediaLibrary", true, false) as Button;
        var archiveButton = pause?.FindChild("PauseOpenSaveArchive", true, false) as Button;
        Check(pause is { Visible: true } && settingsButton is not null && mediaButton is not null && archiveButton is not null,
            "the pause menu and its Settings, Media and Save Archive actions are available");
        if (pause is not { Visible: true } || settingsButton is null || mediaButton is null || archiveButton is null) return;

        bool DrawnAbove(Control upper, Control lower) =>
            upper is { Visible: true } && lower.Visible && upper.GetParent() == lower.GetParent() &&
            upper.ZIndex == lower.ZIndex && upper.GetIndex() > lower.GetIndex();

        var ornaments = pause.FindChildren("*", "CanvasItem", true, false).OfType<CanvasItem>()
            .Where(node => node.IsVisibleInTree() && node.Name.ToString() is
                "CornerOrnamentsOverlay" or "MenuChromeBrassFrame" or "PortraitBezelOverlay").ToArray();
        var decorationKinds = ornaments.Select(node => node.Name).Distinct().Count();
        Check(decorationKinds == 3 && ornaments.All(node => node.ZIndex == 0 && !node.TopLevel),
            "the pause ornaments and portrait bezel draw in their own panel's order");
        await Capture("stack-pause");

        settingsButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.2);
        var settings = game.FindChild("Settings", true, false) as Control;
        Check(DrawnAbove(settings, pause), "Settings covers the pause menu");
        await Capture("stack-pause-settings");

        var mediaFromSettings = settings?.FindChild("OpenMediaLibrarySettings", true, false) as Button;
        Check(mediaFromSettings is not null, "Settings has the Sound and artwork action");
        if (mediaFromSettings is null) return;
        mediaFromSettings.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.25);
        var media = game.FindChild("MediaLibrary", true, false) as Control;
        Check(DrawnAbove(media, settings) && DrawnAbove(media, pause), "Sound and artwork covers Settings and the pause menu together");
        await Capture("stack-pause-settings-media");
        game.Call("CloseMediaLibrary");
        Check(media is { Visible: false } && DrawnAbove(settings, pause), "closing Sound and artwork returns to Settings");
        game.Call("HideSettings");
        Check(settings is { Visible: false } && pause.Visible, "closing Settings returns to the pause menu");

        mediaButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.2);
        Check(DrawnAbove(media, pause), "Sound and artwork also covers the pause menu directly");
        await Capture("stack-pause-media");
        game.Call("CloseMediaLibrary");

        archiveButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.2);
        var archive = game.FindChild("SaveLoad", true, false) as Control;
        Check(archive is { Visible: true } && !pause.Visible, "the save archive replaces the pause menu while open");
        await Capture("stack-pause-archive");
        game.Call("HideSaveLoadInterface");
        Check(archive is { Visible: false } && pause.Visible, "closing the save archive returns to the pause menu");
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
