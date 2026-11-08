using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private static Button WorkshopButton(DealOrForceGame game, string name) =>
        game.FindChildren(name, "Button", true, false).OfType<Button>().FirstOrDefault(b => b.IsVisibleInTree());

    private async Task VerifyWorkshopMaterials(DealOrForceGame game)
    {
        var c = game.VerificationWorld; c.IsPaused = true;
        var before = CampaignFingerprint.Of(c); var rng = c.Random.State;
        foreach (var id in new[] { "felt_map_select", "atlas_lamplight" })
        {
            var item = MediaLibraryCatalog.Find(id);
            using var stream = item is null ? null : AudioAssets.Load(item.File);
            Check(stream is not null && Math.Abs(stream.GetLength() - item.DurationSeconds) < .1,
                "new loose recording loads without editor imports: " + id);
        }
        game.VerificationFlatPan(179.8f);
        Check(game.VerificationFlatMovingCounterWrap(1), "real moving counter loop rebases the eastward sheet without crossing the map");
        Check(game.VerificationFlatMovingCounterWrap(-1), "real moving counter loop rebases the westward sheet without crossing the map");
        await Wait(.3); await Capture("flat-pan-before-seam");
        game.VerificationFlatPan(180.2f); await Wait(.3); await Capture("flat-pan-after-seam");
        game.VerificationFlatPan(-180.2f); await Wait(.3); await Capture("flat-pan-reverse-seam");
        var iran = c.NationByTag("IRN");
        Check(iran is not null && iran.MapName == iran.DisplayName,
            "map labels resolve exactly the current full dossier name");
        using (var button = new Button { Text = "Material fixture" })
        {
            DealOrForceGame.StyleButton(button, false);
            var normal = button.GetThemeStylebox("normal"); var pressed = button.GetThemeStylebox("pressed");
            var hoverPressed = button.GetThemeStylebox("hover_pressed");
            Check(normal is StyleBoxTexture && pressed is StyleBoxTexture,
                "real campaign button states use shared textured nine-slices");
            Check(normal.GetMinimumSize() == pressed.GetMinimumSize() &&
                pressed.GetMinimumSize() == hoverPressed.GetMinimumSize(),
                "pressing and hovering a pressed button never changes its layout margins");
            using var pixels = ((StyleBoxTexture)normal).Texture.GetImage();
            Check(pixels.GetPixel(32, 5).V > pixels.GetPixel(32, 26).V,
                "raised button face has actual light-to-shadow pixel relief");
            button.Free(); // Dispose alone does not delete an orphan native Node.
        }
        var brush = InstrumentMaterials.Meter("combat.strength");
        var count = InstrumentMaterials.TextureCount;
        for (var i = 0; i < 1000; i++)
            if (!ReferenceEquals(brush, InstrumentMaterials.Meter("combat.strength")))
                throw new InvalidOperationException("gauge brush was regenerated");
        Check(InstrumentMaterials.TextureCount == count,
            "one thousand gauge requests reuse the same resource without generating textures");

        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size; game.Call("RefreshLocalisedText");
            game.VerificationWorkshopHide();
            game.VerificationPickerSelect(c.NationByTag("FRA")); await Wait(.4);
            Check(game.VerificationPickerUsesNation(c.NationByTag("FRA")),
                "country selection highlights the entire current nation, not one region: " + language + " " + size.X);
            await Capture("materials-whole-country-choice-" + language + "-" + size.X);
            game.Call("BeginSession"); await Wait(.1);
            game.Call("OpenScreen", "Nation"); await Wait(.4);
            foreach (var metric in new[] { "TREASURY", "BALANCE", "GDP", "DEBT" })
            {
                var cell = game.FindChild("HudStat_" + metric, true, false) as Control;
                Check(cell is not null, "full-cell metric exists: " + metric);
                if (cell is null) continue;
                foreach (var local in new[] { new Vector2(.08f, .1f), new Vector2(.5f, .5f), new Vector2(.9f, .9f) })
                {
                    var position = cell.GetGlobalTransformWithCanvas() * (cell.Size * local);
                    GetViewport().PushInput(new InputEventMouseMotion { Position = position }, true);
                    Check(GetViewport().GuiGetHoveredControl() == cell, "whole metric cell receives mouse at " + local + ": " + metric);
                    GetViewport().PushInput(new InputEventMouseButton { Position = position, ButtonIndex = MouseButton.Left,
                        ButtonMask = MouseButtonMask.Left, Pressed = true }, true);
                    GetViewport().PushInput(new InputEventMouseButton { Position = position, ButtonIndex = MouseButton.Left, Pressed = false }, true);
                    await Wait(.1);
                    Check(metric == "DEBT"
                        ? game.FindChild("SovereignFinanceWorkspace", true, false) is Control { Visible: true }
                        : metric == "GDP"
                            ? game.FindChild("CampaignDossier", true, false) is Control { Visible: true }
                            : game.FindChild("StateAffairsPanel", true, false) is Control panel && panel.IsVisibleInTree(),
                        "clicking anywhere opens the metric's real destination: " + metric);
                    if (metric == "DEBT") game.Call("CloseSovereignFinance");
                    game.Call("OpenScreen", "Nation"); await Wait(.1);
                }
            }
            var musicButton = game.FindChild("OpenMediaLibrary", true, false) as Button;
            Check(musicButton?.TooltipText == Ui.Tip(Loc.Get("Soundtrack player: choose a recording, pause or skip. Music continues when this window closes.")) &&
                musicButton.AccessibilityName == Loc.Get("Soundtrack player"), "music shortcut tooltip and accessibility follow live language switch");
            var music = MusicDirector.Ensure(game);
            // Establish a user-owned recording/pause, rather than racing the first
            // menu-to-campaign automatic crossfade after BeginSession above.
            Check(music.PlayTrack("atlas_lamplight"), "new calm recording is selectable in the real soundtrack transport");
            music.SetPaused(true); await Wait(.1);
            var musicTrack = music.CurrentTrackId;
            var musicPaused = music.Paused;
            musicButton?.GrabFocus();
            ClickPausedDoctrine(musicButton, "open music from focused shortcut"); await Wait(.2);
            Check(game.FindChild("MediaLibrary", true, false) is Control { Visible: true }, "music shortcut opens the player");
            ClickPausedDoctrine(game.FindChild("CloseMediaLibrary", true, false) as Button, "close music player"); await Wait(.2);
            Check(musicButton is not null && !musicButton.HasFocus() && !musicButton.ButtonPressed &&
                game.FindChild("MediaLibrary", true, false) is Control { Visible: false },
                "closing music releases shortcut highlight: " + language + " " + size.X);
            musicButton?.GrabFocus(); ClickPausedDoctrine(musicButton, "reopen music for Escape"); await Wait(.2);
            game.VerificationMusicEscape(); await Wait(.2);
            Check(musicButton is not null && !musicButton.HasFocus() && !musicButton.ButtonPressed &&
                game.FindChild("MediaLibrary", true, false) is Control { Visible: false },
                "Escape releases music shortcut highlight");
            Check(music.CurrentTrackId == musicTrack && music.Paused == musicPaused && music.Manual,
                "closing the player changes neither soundtrack selection nor pause");
            await Capture("materials-music-closed-" + language + "-" + size.X);
            var formation = c.SupportFormations.FirstOrDefault(f => f.NationId == c.Player.Id && SupportCore.Enabled(c, f.TypeId));
            Check(formation is not null, "player has a declared formation for reserve presentation");
            if (formation is not null)
            {
                var oldState = formation.StateMapIndex; var oldBase = formation.BaseProvinceId; var oldMission = formation.Mission;
                try
                {
                    formation.StateMapIndex = -1; formation.BaseProvinceId = -1; formation.Mission = SupportCore.MissionStandby;
                    game.Call("OpenScreen", "Support"); await Wait(.3);
                    var reserveRow = game.FindChild("SupportRow_" + formation.Id, true, false) as Control;
                    for (var parent = reserveRow?.GetParent(); parent is not null; parent = parent.GetParent())
                        if (parent is ScrollContainer scroll) { scroll.EnsureControlVisible(reserveRow); break; }
                    await Wait(.2);
                    if (reserveRow is not null) CheckFits(reserveRow, "reachable reserve row " + language + " " + size.X);
                    var supportPanel = game.FindChild("SupportForcesPanel", true, false) as Control;
                    var railButton = game.FindChild("NavMilitary", true, false) as Control;
                    Check(supportPanel is not null && railButton is not null && supportPanel.GetGlobalTransformWithCanvas().Origin.X >
                        (railButton.GetGlobalTransformWithCanvas() * railButton.Size).X,
                        "long-range dock stays clear of the persistent navigation at " + size.X);
                    Check(game.FindChildren("*", "Label", true, false).OfType<Label>().Any(l => l.IsVisibleInTree() && l.Text == Loc.Get("IN RESERVE — NO SAFE BASE")),
                        "off-map support reserve remains visible in the real roster: " + language + " " + size.X);
                    await Capture("materials-support-reserve-" + language + "-" + size.X);
                }
                finally { formation.StateMapIndex = oldState; formation.BaseProvinceId = oldBase; formation.Mission = oldMission; }
                game.Call("CloseSupportScreen"); game.Call("OpenScreen", "Nation"); await Wait(.15);
            }
            var navigation = game.FindChild("NavMilitary", true, false) as Button;
            Check(navigation?.GetThemeStylebox("normal") is StyleBoxTexture,
                "resting HUD navigation remains visibly textured/clickable: " + language + " " + size.X);
            if (navigation is not null) CheckFits(navigation, "army navigation " + language + " " + size.X);
            VerifyCampaignDossierLayout(game, "materials " + language + " " + size.X);
            await Capture("materials-country-" + language + "-" + size.X);
            game.Call("OpenScreen", "State Affairs"); await Wait(.4);
            var budgetPanel = game.FindChild("StateAffairsPanel", true, false) as Control;
            if (budgetPanel is not null && navigation is not null)
            {
                // Test the real transformed button, not just the authored 1920px layout.
                var budgetLeft = budgetPanel.GetGlobalTransformWithCanvas().Origin.X;
                var navRight = (navigation.GetGlobalTransformWithCanvas() * navigation.Size).X;
                Check(budgetLeft > navRight, "budget workspace clears persistent navigation: " + language + " " + size.X);
                CheckFits(budgetPanel, "budget workspace " + language + " " + size.X);
            }
            else Check(false, "budget workspace and navigation exist");
            var sliders = game.FindChildren("*", "HSlider", true, false).OfType<HSlider>()
                .Where(s => s.IsVisibleInTree()).ToArray();
            Check(sliders.Length > 0 && sliders.All(s => s.GetThemeStylebox("slider") is StyleBoxTexture),
                "real budget sliders use recessed textured tracks: " + language + " " + size.X);
            Check(sliders.All(s => s.GetThemeIcon("grabber").GetWidth() == 20 &&
                s.GetThemeIcon("grabber_disabled") != s.GetThemeIcon("grabber")),
                "budget sliders share tactile grips and a separate disabled image");
            await Capture("materials-budget-" + language + "-" + size.X);
            game.VerificationWorkshopOpen("library"); await Wait(.2);
            foreach (var kind in new[] { "copy", "country", "flag", "content", "layout" })
            {
                game.VerificationWorkshopOpen(kind); await Wait(.25);
                var exit = WorkshopButton(game, "WorkshopScenarios");
                Check(exit is not null && exit.Text == Loc.Get("Back to scenarios"),
                    "consistent scenarios exit in " + kind + ": " + language + " " + size.X);
                if (exit is null) continue;
                CheckFits(exit, kind + " scenarios navigation");
                ClickPausedDoctrine(exit, kind + " scenarios exit"); await Wait(.2);
                if (kind == "layout")
                {
                    var discard = game.FindChildren("*", "ConfirmationDialog", true, false)
                        .OfType<ConfirmationDialog>().SingleOrDefault(d => d.Visible);
                    Check(discard is not null, "new layout document protects its unsaved template");
                    discard?.EmitSignal(ConfirmationDialog.SignalName.Confirmed); discard?.Hide(); await Wait(.2);
                }
                Check(WorkshopButton(game, "WorkshopMainMenu") is not null,
                    kind + " returns to the actual library, not a bare globe or the main menu");
            }
            game.VerificationWorkshopOpen("copy"); await Wait(.2);
            game.VerificationWorkshopDirty();
            var leave = WorkshopButton(game, "WorkshopScenarios");
            ClickPausedDoctrine(leave, "unsaved copy exit"); await Wait(.2);
            var confirmation = game.FindChildren("*", "ConfirmationDialog", true, false)
                .OfType<ConfirmationDialog>().SingleOrDefault(d => d.Visible);
            Check(confirmation is not null, "unsaved copy asks once before leaving");
            if (confirmation is not null)
            {
                confirmation.EmitSignal(ConfirmationDialog.SignalName.Canceled); confirmation.Hide(); await Wait(.2);
                Check(WorkshopButton(game, "WorkshopScenarios") is not null && game.VerificationWorkshopIsDirty,
                    "cancel keeps the unsaved editor and its dirty state");
                game.VerificationWorkshopEscape(); await Wait(.2);
                confirmation = game.FindChildren("*", "ConfirmationDialog", true, false)
                    .OfType<ConfirmationDialog>().SingleOrDefault(d => d.Visible);
                Check(confirmation is not null, "Escape uses the same unsaved-change gate");
                confirmation?.EmitSignal(ConfirmationDialog.SignalName.Confirmed); confirmation?.Hide(); await Wait(.2);
                Check(WorkshopButton(game, "WorkshopMainMenu") is not null, "confirmed Escape returns to scenarios");
            }
            game.VerificationWorkshopOpen("country"); await Wait(.3);
            await Capture("materials-workshop-" + language + "-" + size.X);
            game.VerificationWorkshopOpen("library"); await Wait(.2);
        }
        // Real map draft is heavier than a form. Check it once, not once per resolution.
        game.VerificationWorkshopOpen("map"); await Wait(.4);
        Check(WorkshopButton(game, "WorkshopScenarios") is not null, "map editor has the same library exit");
        game.VerificationWorkshopEscape(); await Wait(.2);
        var mapConfirm = game.FindChildren("*", "ConfirmationDialog", true, false)
            .OfType<ConfirmationDialog>().Where(d => d.Visible).ToArray();
        Check(mapConfirm.Length == 1, "a fresh unsaved map prompts once, without a duplicate RequestClose dialog");
        if (mapConfirm.Length == 1)
        {
            mapConfirm[0].EmitSignal(ConfirmationDialog.SignalName.Confirmed); mapConfirm[0].Hide(); await Wait(.3);
            Check(WorkshopButton(game, "WorkshopMainMenu") is not null, "discarding the map reaches scenarios");
        }
        Check(CampaignFingerprint.Of(c) == before && c.Random.State == rng,
            "all material redraws and editor navigation preserve the campaign and its random stream");
        Check(InstrumentMaterials.TextureBytes <= 128 * 64 * 32 * 4 + 3 * 20 * 24 * 4,
            "shared instrument textures stay under the one-MiB cache budget");
        GD.Print($"MATERIAL_CACHE textures={InstrumentMaterials.TextureCount} rgba_bytes={InstrumentMaterials.TextureBytes}");
        ClickPausedDoctrine(WorkshopButton(game, "WorkshopMainMenu"), "library main menu exit"); await Wait(.8);
        Check(GetTree().CurrentScene is MainMenuScene, "only the root library exits to the main menu");
        await Wait(.2); GC.Collect(); GC.WaitForPendingFinalizers(); await Wait(.2);
    }
}

public partial class DealOrForceGame
{
    internal void VerificationWorkshopOpen(string kind)
    {
        switch (kind)
        {
            case "library": OpenScenarioLibrary(); break;
            case "copy": CreateScenarioCopy(); break;
            case "country": OpenCountryEditor(_campaign.Player.Tag); break;
            case "flag": OpenFlagEditor(_campaign.Player.Tag); break;
            case "content": OpenContentEditor(); break;
            case "layout": OpenVisualLayoutEditor(); break;
            case "map": OpenScenarioMapEditor(); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
    internal void VerificationWorkshopDirty() => _authorDirty = true;
    internal void VerificationWorkshopHide() => CloseAuthorWindow();
    internal bool VerificationWorkshopIsDirty => _authorDirty;
    internal void VerificationWorkshopEscape() => EscapeAuthorWindow();
    internal void VerificationMusicEscape()
    {
        using var key = new InputEventKey { Pressed = true, Keycode = Key.Escape };
        HandleMediaLibraryInput(key);
    }
    internal void VerificationFlatPan(float longitude) => _globe.VerificationFlatPan(longitude);
    internal bool VerificationFlatMovingCounterWrap(int direction) => _globe.VerificationMovingWrap(direction);
    internal void VerificationPickerSelect(Nation nation)
    {
        EnsureScreen("picker", BuildCountryPicker);
        _pickerLayer.Show(); _hudLayer.Hide(); _menuLayer.Hide();
        _globe.VerificationContinuityView(false, nation.CapitalLatitude, nation.CapitalLongitude, 8.4f);
        OnCountryPicked(new GlobePick(nation, _campaign.StatesOf(nation).FirstOrDefault(), -1,
            nation.CapitalLatitude, nation.CapitalLongitude));
    }
    internal bool VerificationPickerUsesNation(Nation nation) => _globe.VerificationNationHighlighted(nation.MapIndex);
}

public sealed partial class GlobePresentation
{
    internal bool VerificationNationHighlighted(int mapIndex) =>
        _globeMaterial.GetShaderParameter("highlighted_nation").AsSingle() == mapIndex &&
        _globeMaterial.GetShaderParameter("selection_active").AsSingle() == 0;
    internal void VerificationFlatPan(float longitude)
    {
        SetFlatMap(true);
        _flatX = _targetFlatX = longitude / 18f; _flatY = _targetFlatY = 0;
        _flatZoom = _targetFlatZoom = 9f;
        _flatGlide = Vector2.Zero; _flatZoomAnchorActive = false;
        ApplyFlatCamera(); UpdateFlatWorldPositions(); SnapArmyMarkerPositions();
        InvalidateFlatCounterDensity();
    }

    internal bool VerificationMovingWrap(int direction)
    {
        var node = new Node3D { Name = "OwnedFlatWrapProbe" }; _armyRoot.AddChild(node);
        var marker = new ArmyStackMarker { Node = node, MotionSettled = true };
        var army = new Army { Latitude = 0, Longitude = 179.8f };
        army.Route.Add(0); // Synthetic presentation-only route; never added to Campaign.
        marker.Stack.Add(army);
        var target = ArmyMarkerPosition(marker);
        marker.TargetPosition = target;
        marker.DisplayPosition = target + new Vector3(direction * (FlatMapWidth - .04f), 0, 0);
        node.Position = marker.DisplayPosition;
        var key = (int.MinValue, int.MinValue, int.MinValue, int.MinValue);
        _armyStacks.Add(key, marker);
        try
        {
            UpdateArmyMarkerMotion(1f / 60f);
            return MathF.Abs(marker.DisplayPosition.X - target.X) < .041f &&
                node.Position == marker.DisplayPosition && army.Route.Count == 1;
        }
        finally { _armyStacks.Remove(key); node.Free(); }
    }
}
