using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyRecruitmentWorkbench(DealOrForceGame game)
    {
        var c = game.VerificationWorld; var player = c.NationByTag("POL");
        c.ConfigureSinglePlayer(player.Id); c.IsPaused = true; game.VerificationSelectPlayerCountry();
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            SettingsCatalog.Store("interface_scale", 1f); game.Call("ApplySetting", "interface_scale"); game.Call("RefreshLocalisedText");
            var caption = language + size.X; var before = RecruitmentInspectionState(c);
            game.VerificationOpenRecruitmentCatalogue(-1); await Wait(.4);
            var panel = game.FindChild("RecruitmentWorkbench", true, false) as Control;
            var footer = game.FindChild("RecruitmentOrderFooter", true, false) as Control;
            var search = game.FindChild("RecruitmentTemplateSearch", true, false) as LineEdit;
            var cards = game.FindChild("RecruitmentCatalogueScroll", true, false) as Control;
            var count = game.FindChild("RecruitmentCount", true, false) as SpinBox;
            var submit = game.FindChild("RecruitmentSubmit", true, false) as Button;
            var status = game.FindChild("RecruitmentOrderStatus", true, false) as Label;
            Check(panel is not null && footer is not null && search is not null && cards is not null && count is not null && submit is not null && status is not null,
                "actual recruitment workbench and fixed order controls exist: " + caption);
            if (panel is null || footer is null || search is null || cards is null || count is null || submit is null || status is null) return;
            var postureTip = (game.FindChild("RecruitmentModeRapidCallUp", true, false) as Button).TooltipText;
            Check(language == "russian" ? postureTip.Contains("Ускоренный") : postureTip.Contains("Accelerated"),
                "posture tooltip follows a live language change: " + caption);
            Rect2 Rect(Control control) => control.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, control.Size);
            var rail = game.FindChild("CampaignNavigation", true, false) as Control;
            Check(Rect(panel).Position.X > Rect(rail).End.X + 5, "workbench clears the actual translated navigation: " + caption);
            foreach (var name in new[] { "RecruitmentWorkbench", "RecruitmentOrderFooter", "RecruitmentOnMap", "RecruitmentSubmit", "RecruitmentTemplateSearch" })
                CheckFits(game.FindChild(name, true, false) as Control, "unscrolled workbench target " + name + caption);
            Check(!Ancestors(footer).Any(node => node is ScrollContainer) && Rect(panel).Encloses(Rect(footer)),
                "order actions live outside every scroll and inside the panel: " + caption);
            Check(!(game.FindChild("RecruitmentTemplate", true, false) as Control).IsVisibleInTree(),
                "formation cards do not duplicate an on-screen dropdown: " + caption);
            var buttons = cards.FindChildren("RecruitmentCard_*", "Button", true, false).OfType<Button>().ToArray();
            var ids = buttons.Select(button => button.GetInstanceId()).ToArray();
            Check(buttons.Length >= 7, "real formation catalogue is populated: " + caption);
            foreach (var control in new Control[] { search, submit, status, game.FindChild("RecruitmentOnMap", true, false) as Control })
            {
                var pixels = (GetViewport().GetStretchTransform() * control.GetGlobalTransformWithCanvas()).Scale.Abs();
                var scale = MathF.Min(pixels.X, pixels.Y);
                Check(control.GetThemeFontSize("font_size") * scale >= 13.9f && (control is Label || control.Size.Y * scale >= 31.9f),
                    "workbench retains physical reading and pointer targets: " + control.Name + caption);
            }
            await Capture("recruitment-workbench-" + language + "-" + size.X);
            await ClickRecruitmentControl(game, "RecruitmentModeRapidCallUp", "workbench posture " + caption); count.Value = 2;
            GetViewport().PushInput(new InputEventKey { Pressed=true, Keycode=Key.F, PhysicalKeycode=Key.F, CtrlPressed=true }, true);
            GetViewport().PushInput(new InputEventKey { Pressed=false, Keycode=Key.F, PhysicalKeycode=Key.F, CtrlPressed=true }, true); await Wait(.1);
            Check(GetViewport().GuiGetFocusOwner() == search, "Ctrl+F reaches the real formation search: " + caption);
            Search(search, "motorized_corps"); await Wait(.15);
            Check(buttons.Count(button => button.Visible) == 1, "formation ID filter selects one real card: " + caption);
            await AllianceReviewKey(Key.Enter);
            Check(game.VerificationRecruitmentChoice == ("motorized_corps", 2, MobilisationMode.RapidCallUp),
                "Enter selects the filtered formation without losing posture or quantity: " + caption);
            Search(search, "__missing_formation__"); await Wait(.1);
            Check(buttons.All(button => !button.Visible) && (game.FindChild("RecruitmentTemplateSearch", true, false) as Control).IsVisibleInTree(),
                "empty formation search hides stale cards but keeps search usable: " + caption);
            Search(search, ""); for (var i = 0; i < 10; i++) game.Call("RefreshRecruitment"); await Wait(.15);
            Check(cards.FindChildren("RecruitmentCard_*", "Button", true, false).OfType<Button>().Select(button => button.GetInstanceId()).SequenceEqual(ids),
                "ten order refreshes reuse every formation card: " + caption);
            Check(RecruitmentInspectionState(c) == before, "catalogue choices spend nothing and do not seed depots: " + caption);
            await VerifyRecruitmentWorkbenchOverlay(game, cards, search, language, size.X);
            var money = player.Treasury; player.Treasury = 0; game.Call("RefreshRecruitment");
            Check(submit.Disabled && status.Text.Length > 0, "unaffordable order keeps a visible refusal beside disabled submission: " + caption);
            player.Treasury = money; game.Call("RefreshRecruitment");
            search.GrabFocus(); await AllianceReviewKey(Key.Escape);
            Check(!panel.IsVisibleInTree() && c.IsPaused && RecruitmentInspectionState(c) == before,
                "Escape from formation search closes without spending or unpausing: " + caption);

            await VerifyRecruitmentDepotLabelsAndReleaseEscape(game, language, size.X);
            var depotHeading = game.FindChild("RecruitmentDepots", true, false).GetParent().FindChild("SectionCaption", true, false) as Label;
            var headingPixels = (GetViewport().GetStretchTransform() * depotHeading.GetGlobalTransformWithCanvas()).Scale.Abs();
            Check(depotHeading.GetThemeFontSize("font_size") * MathF.Min(headingPixels.X, headingPixels.Y) >= 11.9f,
                "strategic depot section heading keeps physical caption size: " + caption);
            game.VerificationOpenRecruitmentCatalogue(-1); await Wait(.2);
            await ClickRecruitmentControl(game, "RecruitmentTabQueue", "training queue page " + caption);
            Check((game.FindChild("RecruitmentPage_queue", true, false) as Control).IsVisibleInTree(), "queue has its own reachable page: " + caption);
            await ClickRecruitmentControl(game, "RecruitmentTabNavy", "naval shipyards page " + caption);
            CheckFits(game.FindChild("RecruitmentNavyScroll", true, false) as Control, "naval workbench page " + caption);
            await Capture("recruitment-workbench-navy-" + language + "-" + size.X);
            var navy = game.FindChild("RecruitmentNavyScroll", true, false);
            var navyIds = navy.FindChildren("*", "Button", true, false).Select(node => node.GetInstanceId()).ToArray();
            await ClickRecruitmentControl(game, "RecruitmentTabArmy", "land forces page " + caption);
            for (var i = 0; i < 10; i++) game.Call("RefreshRecruitment");
            Check(navy.FindChildren("*", "Button", true, false).Select(node => node.GetInstanceId()).SequenceEqual(navyIds),
                "land order refreshes do not rebuild hidden naval controls: " + caption);
            await ClickRecruitmentControl(game, "RecruitmentTabContractors", "contractor page " + caption);
            CheckFits(game.FindChild("RecruitmentContractorScroll", true, false) as Control, "contractor workbench page " + caption);
            Check(RecruitmentInspectionState(c) == before, "inspecting all five workbench pages changes no campaign state: " + caption);
            await ClickRecruitmentControl(game, "RecruitmentTabArmy", "prepare actual land order " + caption);
            var choice = game.VerificationRecruitmentChoice;
            var quote = RecruitmentSystem.TotalBatchCost(c, player, UnitTypeCatalog.Get(choice.Template), choice.Count, choice.Mode, out _);
            var treasury = player.Treasury; var next = c.NextRecruitmentId; var orders = c.Recruitment.Count;
            await ClickRecruitmentControl(game, "RecruitmentSubmit", "fixed begin-training action " + caption);
            Check(c.Recruitment.Count == orders + 1 && c.Recruitment.Any(order => order.Id == next && order.NationId == player.Id && order.TemplateId == choice.Template && order.Count == choice.Count),
                "fixed action submits exactly one normal training batch: " + caption);
            Check(MathF.Abs(player.Treasury - (treasury - quote)) < .001f,
                "fixed action charges exactly the real displayed order quote: " + caption);
        }
        static System.Collections.Generic.IEnumerable<Node> Ancestors(Node node)
        { for (var parent = node.GetParent(); parent is not null; parent = parent.GetParent()) yield return parent; }
    }

    private async Task VerifyRecruitmentWorkbenchOverlay(DealOrForceGame game, Control catalogue, LineEdit search, string language, int width)
    {
        var before = RecruitmentInspectionState(game.VerificationWorld);
        var keys = new[] { "recruitment.card_height_pixels", "recruitment.two_card_width_pixels", "recruitment.target_pixels",
            "recruitment.minimum_font_pixels", "recruitment.minimum_caption_pixels", "recruitment.minimum_heading_pixels" };
        var saved = keys.ToDictionary(key => key, key => InterfaceSkin.Metric(key, 0));
        var unchanged = InterfaceSkin.Metric("diplomacy.minimum_font_pixels", 0);
        var errors = new System.Collections.Generic.List<string>();
        var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(GameDataLoader.DataFolder(), "..", "mods", "_templates", "recruitment_workbench", "interface"));
        Check(System.IO.Directory.Exists(directory), "shipped data-only recruitment template exists: " + language + width);
        if (!System.IO.Directory.Exists(directory)) return;
        try
        {
            // The exact directory loader used by ModLoader.ApplySkin, without enabling a user mod or running any code.
            InterfaceSkin.LoadDirectory(directory, errors); game.Call("FitRecruitmentWorkbench"); await Wait(.2);
            var scale = (GetViewport().GetStretchTransform() * search.GetGlobalTransformWithCanvas()).Scale.Abs();
            var pixels = MathF.Min(scale.X, scale.Y);
            var card = catalogue.FindChildren("RecruitmentCard_*", "Button", true, false).OfType<Button>().First();
            Check(errors.Count == 0 && MathF.Abs(card.CustomMinimumSize.Y * pixels - 78) < .2f,
                "real template overlay changes the rendered card height without C#: " + language + width);
            Check(search.Size.Y * pixels >= 37.9f && search.GetThemeFontSize("font_size") * pixels >= 15.9f,
                "data-only overrides change real pointer targets and typography: " + language + width);
            Check(game.FindChildren("RecruitmentDepotName_*", "Label", true, false).OfType<Label>()
                .All(label => label.GetThemeFontSize("font_size") * pixels >= 15.9f),
                "the same data-only reading minimum reaches strategic depot labels: " + language + width);
            Check(InterfaceSkin.Metric("diplomacy.minimum_font_pixels", 0) == unchanged && RecruitmentInspectionState(game.VerificationWorld) == before,
                "partial UI overlay preserves unrelated metrics and campaign state: " + language + width);
            CheckFits(game.FindChild("RecruitmentOrderFooter", true, false) as Control, "data-only roomier workbench footer " + language + width);
            if (language == "russian" && width == 1280) await Capture("recruitment-workbench-data-only-russian-1280");
        }
        finally
        {
            InterfaceSkin.Merge(System.Text.Json.JsonSerializer.Serialize(new { metrics = saved }), "recruitment-overlay-restore", errors);
            game.Call("FitRecruitmentWorkbench");
        }
        Check(keys.All(key => InterfaceSkin.Metric(key, 0) == saved[key]) && errors.Count == 0,
            "removing the overlay restores all six original metrics: " + language + width);
    }
}
