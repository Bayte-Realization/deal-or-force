using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyPlayerFeedback(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.IsPaused = true;
        game.Call("CloseSidePanel");
        foreach (var sample in game.VerificationMapDragSamples())
            Check(Math.Abs(sample.Pixels - 36) < 3, $"ground follows a 36px drag: {sample.Name} actual={sample.Pixels:F2}");

        var country = world.NationByTag("KOR");
        var north = world.NationByTag("PRK");
        Check(country is not null && north is not null, "unification fixture has both authored Korean states");
        if (country is null || north is null) return;
        world.ConfigureSinglePlayer(country.Id);
        game.VerificationSelectPlayerCountry();
        game.VerificationFeedbackCamera(false);
        await Wait(1.5);
        await Capture("feedback-korea-before");
        var original = game.VerificationCountryLabel(country.MapIndex);
        var provinces = Enumerable.Range(1, world.ProvinceMap.ProvinceCount)
            .Where(p => world.ProvinceMap.ProvinceToNation[p] == north.MapIndex).ToArray();
        StatePartition.Annex(world, provinces, country);
        var deadline = Time.GetTicksMsec() + 30_000;
        while (Time.GetTicksMsec() < deadline && !game.VerificationCountryLabelReady) await Wait(.1);
        var united = game.VerificationCountryLabel(country.MapIndex);
        Check(game.VerificationCountryLabelReady && united.Cells > original.Cells && united.CenterY < original.CenterY,
            "live annexation moves the South Korean name north into the united footprint");
        Check(Math.Abs(united.AxisY) > .45f, "live united Korean label follows the peninsula instead of floating above it");
        foreach (var flat in new[] { false, true })
        {
            game.VerificationFeedbackCamera(flat); await Wait(1);
            await Capture(flat ? "feedback-korea-united-flat" : "feedback-korea-united-globe");
        }
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText"); game.VerificationRefreshHud();
            game.VerificationSelectPlayerCountry(); game.VerificationFeedbackCamera(true);
            game.Call("OpenScreen", "Nation"); await Wait(.4);
            Check(game.FindChild("OpenMediaLibraryNavigation", true, false) is null &&
                game.FindChild("CampaignSoundtrack", true, false) is null &&
                game.FindChildren("OpenMediaLibrary", "Button", true, false).Count == 1,
                "one permanent soundtrack entry, no navigation/outliner duplicates " + language + size.X);
            Check(game.VerificationReadableCountryCodes(), "country codes are explained in tooltips rather than unexplained headlines");
            Check(game.VerificationAllNavigationReachable(), "all destinations stay on the bordered rail without a category dropdown " + language + size.X);
            await Wait(.2);
            CheckFits(game.FindChild("CampaignNavigation", true, false) as Control, "filtered navigation " + language + size.X);
            await Capture("feedback-clean-hud-" + language + "-" + size.X);
            var note = game.FindChild("OpenMediaLibrary", true, false) as Button;
            note?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.2);
            Check(game.FindChild("SoundtrackPlayer", true, false) is Control { Visible: true }, "single note opens the working player");
            game.VerificationCloseMusicLibrary();
        }
        var research = CampaignHintArt.Of("research") as AtlasTexture;
        Check(research?.Atlas.ResourcePath.EndsWith("icon_research.png", StringComparison.Ordinal) == true,
            "alert glyph and top-bar/nav research glyph share the authored icon family");
        await VerifyDeclarationAndLobby(game);
        await VerifyRegimeArtwork(game);
        // The soundtrack probe deliberately returns to the real main menu and frees
        // this campaign scene. It must be last, not followed by a stale game reference.
        await VerifySoundtrackPlayer(game);
    }

    private async Task VerifyRegimeArtwork(DealOrForceGame game)
    {
        Check(DealOrForceGame.FlagSpriteFor("AFG") == "flag_afg_theocratic_rule", "actual UI resolver uses the Taliban banner in 2024");
        var image = DealOrForceGame.FlagTextureFor("AFG")?.GetImage();
        Check(image is not null && !image.IsEmpty() && image.GetPixel(0, 0).R > .9f,
            "actual Afghan UI texture has a white field, not the republican tricolour");
        game.VerificationShowAfghanistan(); await Wait(.7);
        await Capture("feedback-afghanistan-white-banner");

        var designs = FlagDesignCatalog.All.Where(d => d.Id.EndsWith("_defence_council", StringComparison.Ordinal) ||
            d.Id.EndsWith("_covenant", StringComparison.Ordinal)).OrderBy(d => d.Id).ToArray();
        for (var page = 0; page * 32 < designs.Length; page++)
        {
        var panel = new PanelContainer { Name = "VerificationRegimeGallery", Theme = game.Theme };
        GetTree().Root.AddChild(panel); panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var box = new VBoxContainer(); panel.AddChild(box);
        box.AddChild(new Label { Text = "Original alternate-history banners — fictional outcomes, not historical flags · " + (page + 1) });
        var grid = new GridContainer { Columns = 8 }; box.AddChild(grid);
        foreach (var design in designs.Skip(page * 32).Take(32))
        {
            var cell = new VBoxContainer { CustomMinimumSize = new Vector2(150, 145) }; grid.AddChild(cell);
            var texture = DealOrForceGame.VerificationRegimeTexture(design.Id);
            Check(texture is not null, "actual UI texture builds " + design.Id);
            cell.AddChild(new TextureRect { Texture = texture, CustomMinimumSize = new Vector2(140, 94),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
            cell.AddChild(new Label { Text = design.Id.Replace("flag_", "").Replace("_defence_council", " · council").Replace("_covenant", " · covenant"),
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis });
        }
        await Wait(.3); await Capture("feedback-alternate-regime-gallery-" + (page + 1)); panel.QueueFree(); await Wait(.1);
        }
        var world = game.VerificationWorld;
        var originalDate = world.Date;
        world.Date = new GameDate(-3499, 2, 28) { Hour = 23 };
        game.VerificationRefreshHud(); await Wait(.3);
        await Capture("feedback-bronze-age-calendar");
        world.Date = originalDate; game.VerificationRefreshHud();
        var draft = new ScenarioMapDraft(SaveSystem.ExportScenarioJson(world, "bce_ui_probe"),
            world.ProvinceMap.ProvinceCount, "bce_ui_probe");
        var editor = new ScenarioMapEditor { Name = "VerificationBceEditor", Draft = draft,
            Topology = world.ProvinceMap, ModRoot = _captureDirectory };
        GetTree().Root.AddChild(editor); editor.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        await Wait(.3);
        (editor.FindChild("MapSettings", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.2);
        var era = editor.FindChild("ScenarioEra", true, false) as OptionButton;
        var year = editor.FindChild("ScenarioYear", true, false) as SpinBox;
        Check(era is not null && year is not null, "actual scenario settings expose an era selector and civil year");
        if (era is not null && year is not null)
        {
            era.Select(1); year.Value = 3500;
            await Capture("feedback-bce-scenario-settings");
            (editor.FindChild("ApplyScenarioSettings", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.3);
            Check(draft.Settings.Year == -3499, "actual editor Apply converts 3500 BCE into the saved astronomical year");
        }
        editor.QueueFree();
    }
}

public partial class DealOrForceGame
{
    internal static Texture2D VerificationRegimeTexture(string sprite) => FlagSpriteTexture(sprite);
    internal void VerificationShowAfghanistan()
    {
        CloseSidePanel(); EnsureMapSelectionPanels();
        ShowCountryCard(_campaign.NationByTag("AFG")); _countryCard.Show(); LayoutMapSelectionCards();
        _globe.FocusOn(34, 66, 8.5f);
    }
    internal (string Name, float Pixels)[] VerificationMapDragSamples() => _globe.VerificationMapDragSamples();
    internal void VerificationFeedbackCamera(bool flat)
    { _globe.SetFlatMap(flat); _globe.FocusOn(38, 127.5f, flat ? 6 : 8.5f); }
    internal TerritoryLabelLayout.TerritoryLabelAnchor VerificationCountryLabel(int index) => _globe.VerificationCountryLabel(index);
    internal bool VerificationCountryLabelReady => _globe.VerificationCountryLabelReady;
    internal bool VerificationReadableCountryCodes()
    {
        // Map dossiers are lazy-built by a geographic pick, not by OpenScreen(Nation).
        CloseSidePanel();
        EnsureMapSelectionPanels();
        ShowCountryCard(_campaign.Player);
        _countryCard.Show();
        LayoutMapSelectionCards();
        return !_identityTag.Text.Contains(_campaign.Player.Tag, StringComparison.Ordinal) &&
            !_countryCardCaption.Text.Contains(_campaign.Player.Tag, StringComparison.Ordinal) &&
            _countryCardCaption.TooltipText.Contains(_campaign.Player.Tag, StringComparison.Ordinal);
    }
    internal bool VerificationAllNavigationReachable()
    {
        var previous = _campaignNavigationSearch.Text;
        _campaignNavigationSearch.Text = "";
        LayoutCampaignNavigation();
        var all = FindChild("NavigationCategory", true, false) is null &&
            _campaignNavigationEntries.All(entry =>
                !NavigationDestinationEnabled(_screenButtons.First(pair => pair.Value == entry.Button).Key) || entry.Button.Visible);
        _campaignNavigationSearch.Text = previous;
        return all;
    }
}

public sealed partial class GlobePresentation
{
    internal TerritoryLabelLayout.TerritoryLabelAnchor VerificationCountryLabel(int index) => _lastLabelAnchors[index];
    internal bool VerificationCountryLabelReady => _labelTask is null && _labelRevision == _campaign.MapRevision;

    internal (string Name, float Pixels)[] VerificationMapDragSamples()
    {
        var samples = new System.Collections.Generic.List<(string, float)>();
        SetFlatMap(false);
        foreach (var distance in new[] { 5.5f, 7f, 12f })
        {
            _distance = _targetDistance = distance; _yaw = _targetYaw = 0; _pitch = _targetPitch = 0;
            ApplyCamera();
            var ground = _camera.GlobalPosition.Normalized() * GlobeRadius;
            var before = _camera.UnprojectPosition(ground);
            // An unfinished previous move must not jump when the player takes hold.
            _targetYaw = 2;
            BeginCameraDrag(Vector2.Zero, MouseButton.Middle);
            DragCamera(new Vector2(36, 0)); ApplyCamera();
            samples.Add(("globe altitude=" + (distance - GlobeRadius), (_camera.UnprojectPosition(ground) - before).Length()));
        }
        SetFlatMap(true);
        foreach (var zoom in new[] { .2f, 2f, 8f })
        {
            _flatZoom = _targetFlatZoom = zoom; _flatX = _targetFlatX = _flatY = _targetFlatY = 0;
            ApplyCamera();
            var before = _camera.UnprojectPosition(Vector3.Zero);
            BeginCameraDrag(Vector2.Zero, MouseButton.Middle);
            DragCamera(new Vector2(36, 0)); ApplyCamera();
            samples.Add(("flat extent=" + zoom, (_camera.UnprojectPosition(Vector3.Zero) - before).Length()));
        }
        _dragging = false; _flatGlide = Vector2.Zero;
        return samples.ToArray();
    }
}
