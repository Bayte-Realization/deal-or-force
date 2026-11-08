using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private static string RecruitmentInspectionState(Campaign c) => CampaignFingerprint.Of(c) + ":" + c.NextRecruitmentId + ":" +
        JsonSerializer.Serialize(c.Recruitment, new JsonSerializerOptions { IncludeFields = true }) + ":" +
        string.Join(";", c.Nations.Select(n => n.Id + ":" + n.StockpilesSeeded + ":" +
            string.Join(",", n.Stockpiles.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key + "=" + item.Value)))) + ":" +
        c.Commands.Count + ":" + c.Random.State + ":" + c.ElapsedHours;

    private async Task ClickRecruitmentControl(DealOrForceGame game, string name, string caption)
    {
        var button = game.FindChild(name, true, false) as Button;
        Check(button is { Disabled: false } && button.IsVisibleInTree(), "recruitment control is available: " + caption);
        if (button is not { Disabled: false } || !button.IsVisibleInTree()) return;
        for (var parent = button.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) { scroll.EnsureControlVisible(button); break; }
        await Wait(.15);
        var point = button.GetGlobalTransformWithCanvas() * (button.Size * .5f);
        GetViewport().PushInput(new InputEventMouseMotion { Position = point }, true);
        var hovered = GetViewport().GuiGetHoveredControl();
        Check(hovered == button || hovered is not null && button.IsAncestorOf(hovered), "actual recruitment button receives the pointer: " + caption);
        if (hovered != button && (hovered is null || !button.IsAncestorOf(hovered))) return;
        GetViewport().PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left,
            ButtonMask = MouseButtonMask.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Wait(.3);
    }

    private async Task VerifyRecruitmentPlacement(DealOrForceGame game)
    {
        var c = game.VerificationWorld; c.IsPaused = true;
        var player = c.NationByTag("RUS"); c.ConfigureSinglePlayer(player.Id);
        var other = c.NationByTag("USA");
        var own = c.StatesOf(player).First(state => !LakeRegions.IsWaterState(state) && RecruitmentSystem.DeploymentProvince(c, player.Id, state.MapIndex) > 0);
        var foreign = c.StatesOf(other).First(state => !LakeRegions.IsWaterState(state));
        game.VerificationSelectPlayerCountry(); await Wait(.3);
        const string templateId = "motorized_corps"; const int batchSize = 2;
        var template = UnitTypeCatalog.Get(templateId);
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            SettingsCatalog.Store("interface_scale", 1f); game.Call("ApplySetting", "interface_scale"); game.Call("RefreshLocalisedText");
            // Synthetic isolated verification campaign: expose initialization side effects on every pass.
            player.Treasury = 1000; player.ManpowerReserve = 10_000_000;
            player.Stockpiles.Clear(); player.StockpilesSeeded = false;
            var caption = language + " " + size.X;
            var before = RecruitmentInspectionState(c);
            game.VerificationOpenRecruitmentCatalogue(own.MapIndex); await Wait(.3);
            Check(RecruitmentInspectionState(c) == before, "opening recruitment does not initialize depots or mutate the queue: " + caption);
            var choices = game.FindChild("RecruitmentTemplate", true, false) as OptionButton;
            var count = game.FindChild("RecruitmentCount", true, false) as SpinBox;
            Check(choices is not null && count is not null, "real template and batch selectors exist: " + caption);
            if (choices is null || count is null) continue;
            var index = Enumerable.Range(0, choices.ItemCount).First(i => choices.GetItemText(i) == Loc.Get(template.Name));
            choices.Select(index); choices.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
            count.Value = batchSize;
            await ClickRecruitmentControl(game, "RecruitmentModeRapidCallUp", "rapid posture " + caption);
            await ClickRecruitmentControl(game, "RecruitmentOnMap", "map recruitment " + caption);
            var panel = game.FindChild("RecruitmentPlacement", true, false) as Control;
            Check(panel is not null && panel.IsVisibleInTree() && game.VerificationRecruitmentPrioritizesRegions,
                "map tool opens and prioritizes regions over counters: " + caption);
            if (panel is null) continue;
            Check(RecruitmentInspectionState(c) == before && !player.StockpilesSeeded,
                "template, posture and map tool choices reserve no money, people or materials: " + caption);
            game.VerificationContinuityView(false, player.CapitalLatitude, player.CapitalLongitude, 5.4f);
            var centre = game.GetViewport().GetVisibleRect().Size * .5f;
            GetViewport().PushInput(new InputEventMouseMotion { Position = centre }, true); await Wait(.2);
            game.PollRecruitmentPreview();
            var pick = game.VerificationCentrePick();
            var preview = game.FindChild("RecruitmentPlacementPreview", true, false) as Label;
            Check(pick.State is not null && preview is not null && preview.Text.Contains(pick.State.DisplayName),
                "viewport hover resolves the actual region below the recruitment pointer: " + caption);
            game.RefreshRecruitmentPlacementPreview(own);
            var terms = game.FindChild("RecruitmentPlacementTerms", true, false) as Label;
            var quote = RecruitmentSystem.TotalBatchCost(c, player, template, batchSize, MobilisationMode.RapidCallUp, out _);
            Check(terms is not null && terms.Text.Contains(Fmt.Money(quote)) && preview.Text.Contains(own.DisplayName) &&
                preview.Text.Contains(Loc.Get("Click to begin training here")), "map preview shows the bill and legal region before charging: " + caption);
            game.RefreshRecruitmentPlacementPreview(foreign);
            game.VerificationRecruitmentPick(other, foreign); await Wait(.1);
            Check(RecruitmentInspectionState(c) == before && !player.StockpilesSeeded,
                "foreign preview and map dispatch refuse without seeding or mutating anything: " + caption);
            var treasury = player.Treasury; player.Treasury = 0;
            var poor = RecruitmentInspectionState(c);
            game.VerificationRecruitmentPick(player, own); await Wait(.1);
            Check(RecruitmentInspectionState(c) == poor && !player.StockpilesSeeded,
                "unaffordable map dispatch leaves the unseeded campaign and queue/id intact: " + caption);
            player.Treasury = treasury;

            // The real pointer gesture exercises the globe's counter suppression and map dispatch.
            game.VerificationContinuityView(false, player.CapitalLatitude, player.CapitalLongitude, 5.4f);
            GetViewport().PushInput(new InputEventMouseMotion { Position = centre }, true); await Wait(.15);
            pick = game.VerificationCentrePick();
            var nextId = c.NextRecruitmentId; var queueCount = c.Recruitment.Count; var armies = c.Armies.Count;
            var manpower = player.ManpowerReserve;
            GetViewport().PushInput(new InputEventMouseButton { Position = centre, ButtonIndex = MouseButton.Left,
                ButtonMask = MouseButtonMask.Left, Pressed = true }, true);
            GetViewport().PushInput(new InputEventMouseButton { Position = centre, ButtonIndex = MouseButton.Left, Pressed = false }, true);
            await Wait(.3);
            var queued = c.Recruitment.FirstOrDefault(order => order.Id == nextId);
            Check(queued is not null && c.Recruitment.Count == queueCount + 1 && c.NextRecruitmentId == nextId + 1 &&
                queued.StateMapIndex == pick.State?.MapIndex && queued.TemplateId == templateId && queued.Count == batchSize &&
                queued.Mode == MobilisationMode.RapidCallUp && c.Armies.Count == armies,
                "one real map click queues one matching training batch without instant spawning: " + caption);
            Check(MathF.Abs(player.Treasury - (treasury - quote)) < .001f &&
                manpower - player.ManpowerReserve == template.RecruitmentPersonnel * batchSize && player.StockpilesSeeded,
                "actual map command charges the quoted money, manpower and depot bill: " + caption);
            foreach (var control in new[] { panel, game.FindChild("RecruitmentPlacementFinish", true, false) as Control,
                         game.FindChild("RecruitmentPlacementCatalogue", true, false) as Control })
                CheckFits(control, "recruitment placement and fixed footer " + caption);
            var fontScale = (GetViewport().GetStretchTransform() * preview.GetGlobalTransformWithCanvas()).Scale.Abs();
            Check(preview.LabelSettings.FontSize * MathF.Min(fontScale.X, fontScale.Y) >= 13.99f,
                "recruitment placement retains physical 14px explanations: " + caption);
            // Let a map tooltip close naturally while reading the placement tool.
            var finish = game.FindChild("RecruitmentPlacementFinish", true, false) as Button;
            GetViewport().PushInput(new InputEventMouseMotion { Position = finish.GetGlobalTransformWithCanvas() * (finish.Size * .5f) }, true);
            await Wait(.25);
            await Capture("recruitment-placement-" + language + "-" + size.X);
            var admitted = RecruitmentInspectionState(c);
            await ClickRecruitmentControl(game, "RecruitmentPlacementCatalogue", "return to recruitment " + caption);
            Check(game.VerificationRecruitmentChoice == (templateId, batchSize, MobilisationMode.RapidCallUp) && RecruitmentInspectionState(c) == admitted,
                "catalogue return retains the chosen template, count and posture without cancelling training: " + caption);
            await ClickRecruitmentControl(game, "RecruitmentOnMap", "resume map recruitment " + caption);
            var interceptor = c.Commands.SubmitInterceptor; ICampaignCommand submitted = null;
            try
            {
                c.Commands.SubmitInterceptor = command => { submitted = command; return true; };
                var pending = RecruitmentInspectionState(c);
                game.VerificationRecruitmentPick(player, own);
                Check(submitted is RaiseArmyCommand && RecruitmentInspectionState(c) == pending,
                    "map placement hands authority to transport without speculative local charges: " + caption);
            }
            finally { c.Commands.SubmitInterceptor = interceptor; }
            admitted = RecruitmentInspectionState(c);
            game._Input(new InputEventKey { Pressed = true, Keycode = Key.Escape }); await Wait(.1);
            Check(game.FindChild("RecruitmentPlacement", true, false) is null && RecruitmentInspectionState(c) == admitted &&
                !game.VerificationRecruitmentPrioritizesRegions, "Escape finishes placement and preserves every paid training order: " + caption);
        }
        var last = RecruitmentInspectionState(c);
        game.VerificationBeginRecruitmentTool(templateId, batchSize, MobilisationMode.Standard);
        game.VerificationStartConstructionTool(); await Wait(.1);
        Check(game.FindChild("RecruitmentPlacement", true, false) is null, "construction replaces the recruitment gesture without cancelling queued troops");
        game.VerificationBeginRecruitmentTool(templateId, batchSize, MobilisationMode.Standard); await Wait(.1);
        Check(game.FindChild("ConstructionPlacement", true, false) is null, "recruitment replaces the construction gesture");
        game._Input(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Right }); await Wait(.1);
        Check(game.FindChild("RecruitmentPlacement", true, false) is null && RecruitmentInspectionState(c) == last,
            "right-click and switching placement tools preserve paid queues, funds, manpower and depots");
        Loc.Language = "english"; GetTree().Root.Size = new(1920, 1080); game.Call("RefreshLocalisedText");
    }
}

public partial class DealOrForceGame
{
    internal void VerificationOpenRecruitmentCatalogue(int region) => OpenRecruitment(region);
    internal void VerificationBeginRecruitmentTool(string template, int count, MobilisationMode mode) => BeginRecruitmentPlacement(template, count, mode);
    internal void VerificationRecruitmentPick(Nation nation, ScenarioState region) => OnCountryPicked(new GlobePick(nation, region, -1, 0, 0));
    internal bool VerificationRecruitmentPrioritizesRegions => _globe.CountrySelectionOnly;
    internal (string Template, int Count, MobilisationMode Mode) VerificationRecruitmentChoice =>
        (RecruitmentTemplateId(), (int)_recruitmentCount.Value, _recruitmentMode);
}
