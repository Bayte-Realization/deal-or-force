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

        var inboxBtn = game.FindChild("ShellDiplomaticInboxButton", true, false) as Button;
        Check(inboxBtn is { Visible: true }, "new shell diplomatic inbox bar is visible in shipped release");
        if (inboxBtn is not null)
        {
            inboxBtn.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.25);
            var inboxPanel = game.FindChild("DiplomaticInboxPanel", true, false) as Control;
            Check(inboxPanel is { Visible: true }, "diplomatic inbox opens cleanly from the main HUD bar");
            await Capture("new-shell-inbox");
            game.VerificationCloseDealInbox();
            await Wait(.15);
            Check(game.FindChild("DiplomaticInboxPanel", true, false) is null, "diplomatic inbox closes cleanly");
        }

        var flagBtn = game.FindChild("ShellFlagButton", true, false) as Button;
        Check(flagBtn is { Visible: true }, "new shell flag button is visible in shipped release");
        if (flagBtn is not null)
        {
            flagBtn.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.25);
            var staffQuick = game.FindChild("ShellOverviewOpenStaff", true, false) as Button;
            Check(staffQuick is { Visible: true }, "own country dossier shows AI staff quick-link");
            if (staffQuick is not null)
            {
                staffQuick.EmitSignal(BaseButton.SignalName.Pressed);
                await Wait(.25);
                var toggleAll = game.FindChild("ShellStaffToggleAll", true, false) as Button;
                var staffCon = game.FindChild("ShellStaff_construction", true, false) as CheckButton;
                Check(toggleAll is { Visible: true } && staffCon is { Visible: true },
                    "own country AI staff tab exposes all branch switches");
                await Capture("new-shell-staff-tab");
                if (staffCon is not null)
                {
                    var was = Delegation.Holds(game.VerificationWorld.Player, Delegation.Construction);
                    staffCon.ButtonPressed = !was;
                    await Wait(.1);
                    Check(Delegation.Holds(game.VerificationWorld.Player, Delegation.Construction) == !was,
                        "AI staff branch toggle updates Delegation state live");
                    var restore = game.FindChild("ShellStaff_construction", true, false) as CheckButton;
                    if (restore is not null) restore.ButtonPressed = was;
                    await Wait(.1);
                }
            }
            var closeDossier = game.FindChild("CloseShellWorkPanel", true, false) as Button;
            closeDossier?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.15);
        }

        game.VerificationToggleScreen("State Affairs");
        await Wait(.25);
        Check(game.FindChild("StateAffairsOpenStaff", true, false) is Button { Visible: true } &&
              game.FindChild("StateStaff_construction", true, false) is CheckButton { Visible: true },
            "State Affairs exposes full AI staff delegation controls at the top");
        await Capture("state-affairs-staff");
        game.CloseVisibleCampaignScreen();
        await Wait(.15);
    }

    private async Task VerifyCampaignTopBar(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        var originalPlayer = world.Player.Id;
        var originalLanguage = Loc.Language;
        var originalSize = GetTree().Root.Size;
        var originalPause = world.IsPaused;
        var openWars = Warfare.OpenWars(world);
        var player = new[] { world.NationByTag("SRB"), world.Player }.Concat(world.Nations)
            .First(n => n is not null && !openWars.Any(w => w.Involves(n.Id)));
        var opponents = world.Nations.Where(n => n.Id != player.Id).Take(2).ToArray();
        var nextWarId = world.Wars.Select(w => w.Id).DefaultIfEmpty().Max() + 1;
        var first = new StrategicWar { Id = nextWarId, AttackerId = player.Id, DefenderId = opponents[0].Id, Ended = true };
        var second = new StrategicWar { Id = nextWarId + 1, AttackerId = player.Id, DefenderId = opponents[1].Id, Ended = true };
        try
        {
            world.ViewAs(player.Id);
            world.IsPaused = true;
            world.Wars.Add(first);
            world.Wars.Add(second);
            world.MarkWarsChanged();
            SettingsCatalog.Store("ui_shell", "new");
            game.Call("ApplySetting", "ui_shell");

            var frame = game.FindChild("ShellLCornerBackdrop", true, false) as Control;
            var plate = game.FindChild("ShellTopPlate", true, false) as Control;
            var bed = game.FindChild("ShellCellsBed", true, false) as PanelContainer;
            var warCell = game.FindChild("ShellStat_War", true, false) as Control;
            Check(frame is not null && plate is not null && bed is not null && warCell is not null,
                "campaign top bar frame, plate, stat row and war cell exist");
            if (frame is null || plate is null || bed is null || warCell is null) return;

            foreach (var language in new[] { "english", "russian" })
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                Loc.Language = language;
                GetTree().Root.Size = size;
                game.Call("RefreshLocalisedText");
                await Wait(.25);

                var caption = $"{language} {size.X}";
                var peaceSize = (frame.Size.X, bed.Size.X);
                Check(!warCell.IsVisibleInTree(), $"top bar starts at peace: {caption}");
                await SetWars(firstEnded: false, secondEnded: false);
                var warSize = (frame.Size.X, bed.Size.X);
                Check(warCell.IsVisibleInTree() && warSize.Item1 > peaceSize.Item1 && warSize.Item2 > peaceSize.Item2,
                    $"frame and stat row expand together when war begins: {caption}");
                await Capture("campaign-topbar-war-" + language + "-" + size.X);

                await SetWars(firstEnded: true, secondEnded: false);
                Check(warCell.IsVisibleInTree() && (frame.Size.X, bed.Size.X) == warSize,
                    $"frame and stat row stay expanded while another war remains: {caption}");

                await SetWars(firstEnded: true, secondEnded: true);
                Check(!warCell.IsVisibleInTree() && (frame.Size.X, bed.Size.X) == peaceSize &&
                    bed.GetGlobalRect().End.X < plate.GetGlobalRect().End.X,
                    $"frame and stat row return to the same peace widths after the last war: {caption}");
                await Capture("campaign-topbar-peace-" + language + "-" + size.X);
            }
        }
        finally
        {
            world.Wars.Remove(first);
            world.Wars.Remove(second);
            world.MarkWarsChanged();
            world.ViewAs(originalPlayer);
            world.IsPaused = originalPause;
            Loc.Language = originalLanguage;
            GetTree().Root.Size = originalSize;
            game.Call("RefreshLocalisedText");
        }

        async Task SetWars(bool firstEnded, bool secondEnded)
        {
            first.Ended = firstEnded;
            second.Ended = secondEnded;
            world.MarkWarsChanged();
            game.Call("Refresh");
            await Wait(.15);
        }
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

    internal void VerificationCloseDealInbox() => CloseDealComposer();
    internal void VerificationToggleScreen(string screen) => ToggleCampaignScreen(screen);
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
