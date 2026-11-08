using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private void VerifyCampaignDossierLayout(DealOrForceGame game, string caption)
    {
        var (panel, body, details, scroll, navigation, header) = game.VerificationDossierLayout;
        Check(panel is { Visible: true } && body is not null && details is not null &&
            scroll is not null && navigation is not null && header is not null,
            "shared country dossier controls exist: " + caption);
        if (panel is not { Visible: true } || body is null || details is null ||
            scroll is null || navigation is null || header is null) return;

        static Rect2 CanvasRect(Control control) =>
            control.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, control.Size);
        var panelRect = CanvasRect(panel);
        var railRect = CanvasRect(navigation);
        Check(panelRect.Position.X >= railRect.End.X,
            "whole country dossier clears the transformed navigation rail: " + caption);
        Check(panelRect.Position.Y >= CanvasRect(header).End.Y,
            "whole country dossier clears the transformed header: " + caption);
        foreach (var (control, part) in new[] { (body, "body"), (details, "facts"), ((Control)scroll, "scroll area") })
        {
            var contentRect = CanvasRect(control);
            Check(contentRect.Position.X >= panelRect.Position.X && contentRect.End.X <= panelRect.End.X + .5f,
                "country dossier " + part + " stays inside the panel width: " + caption);
        }
        CheckFits(panel, "shared country dossier " + caption);
        var position = panel.Position; var size = panel.Size;
        game.Call("LayoutCampaignDossier"); game.Call("LayoutCampaignDossier");
        if (!panel.Position.IsEqualApprox(position) || !panel.Size.IsEqualApprox(size))
            GD.Print($"DOSSIER_LAYOUT_DIAGNOSTIC {caption} before={position}/{size} after={panel.Position}/{panel.Size} " +
                $"minimum={panel.GetCombinedMinimumSize()} rail={CanvasRect(navigation)} header={CanvasRect(header)} " +
                $"offsets={panel.OffsetLeft},{panel.OffsetTop},{panel.OffsetRight},{panel.OffsetBottom}");
        Check(panel.Position.IsEqualApprox(position) && panel.Size.IsEqualApprox(size),
            "repeated dossier layout does not accumulate a rail inset: " + caption);
    }

    private async Task VerifyCountryDossier(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.ConfigureSinglePlayer(world.NationByTag("KAZ").Id);
        world.IsPaused = true;
        var country = world.NationByTag("JPN");
        game.Call("OpenScreen", "Decisions"); game.Call("CloseSidePanel");
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
        {
            Loc.Language = language; game.Call("RefreshLocalisedText"); GetTree().Root.Size = size;
            var before = CampaignFingerprint.Of(world);
            game.Call("OpenScreen", "Nation"); await Wait(.25);
            VerifyCampaignDossierLayout(game, language + " " + size.X);
            await Capture("campaign-dossier-" + language + "-" + size.X);
            game.Call("CloseSidePanel");
            game.VerificationInspectPick(new GlobePick(country, null, -1, country.CapitalLatitude, country.CapitalLongitude));
            await Wait(.25);
            var card = game.FindChild("CountryCard", true, false) as Control;
            Check(card.IsVisibleInTree(), "country dossier opens on the map " + language + " " + size.X);
            foreach (var page in new[] { "Overview", "Government", "Relations", "Actions" })
            {
                var tab = game.FindChild("CountryPage" + page, true, false) as Button;
                tab.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.15);
                var facts = game.FindChild("CountryFactsScroll", true, false) as Control;
                var actions = game.FindChild("CountryActionsScroll", true, false) as Control;
                Check(facts.Visible == (page != "Actions") && actions.Visible == (page == "Actions"),
                    "country facts and commands occupy separate pages " + page);
                CheckFits(card, "country dossier " + page + " " + language + " " + size.X);
                CheckFits(tab, "country dossier tab " + language + " " + size.X);
                Check(tab.Text == Loc.Get(page), "country dossier tab translated " + page + " " + language);
                await Capture("country-dossier-" + page.ToLowerInvariant() + "-" + language + "-" + size.X);
            }
            Check(CampaignFingerprint.Of(world) == before, "all country dossier pages are read-only");
            game.Call("CloseMapSelection");
            game.VerificationOpenDiplomacy("JPN"); await Wait(.2);
            var banner = game.FindChild("DiplomacyCountryBanner", true, false);
            var tabs = game.FindChild("DiplomacyDossierTabs", true, false);
            var ids = tabs.GetChildren().Select(n => n.GetInstanceId()).ToArray();
            foreach (var page in new[] { "Overview", "Government", "Relations", "Comparison" })
            {
                var tab = game.FindChild("DiplomacyPage" + page, true, false) as Button;
                tab.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.15);
                CheckFits(banner as Control, "diplomacy masthead " + page + " " + language + " " + size.X);
                Check(tab.Text == Loc.Get(page), "diplomacy page translated " + page + " " + language);
                Check((game.FindChild("DiplomacyDossierName", true, false) as Label).Text == country.DisplayName,
                    "diplomacy masthead retains the selected country's identity");
                await Capture("diplomacy-dossier-" + page.ToLowerInvariant() + "-" + language + "-" + size.X);
            }
            var refreshBefore = CampaignFingerprint.Of(world);
            game.Call("ForceDiplomacyRefresh"); await Wait(.1);
            Check(ids.SequenceEqual(tabs.GetChildren().Select(n => n.GetInstanceId())) &&
                banner.GetInstanceId() == game.FindChild("DiplomacyCountryBanner", true, false).GetInstanceId(),
                "diplomacy refresh retains identity and navigation controls");
            Check(refreshBefore == CampaignFingerprint.Of(world), "diplomacy dossier refresh is read-only");
            game.Call("CloseDiplomacyScreen");
        }
        game.VerificationInspectPick(new GlobePick(country, null, -1, 0, 0));
        (game.FindChild("CountryPageRelations", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
        var selectedTab = game.FindChild("CountryPageRelations", true, false) as Button;
        selectedTab.GrabFocus(); var selectedId = selectedTab.GetInstanceId();
        world.IsPaused = false; world.TickDay(); world.TickDay(); world.IsPaused = true;
        game.Call("ShowMapSelectionContent"); await Wait(.2);
        Check(game.FindChild("CountryPageRelations", true, false).GetInstanceId() == selectedId && selectedTab.HasFocus(),
            "country tab and its keyboard focus survive two campaign days");
        Check(!(game.FindChild("CountryActionsScroll", true, false) as Control).Visible,
            "country tick does not switch a reader back to actions");
        game.Call("CloseMapSelection");
        Loc.Language = "english"; GetTree().Root.Size = new(1920,1080); game.Call("RefreshLocalisedText");
        await VerifyCountryCooperation(game);
    }
}

public partial class DealOrForceGame
{
    internal (Control Panel, Control Body, Control Details, ScrollContainer Scroll, Control Navigation, Control Header)
        VerificationDossierLayout => (_sidePanel, _sideBody, _sideDetail, _dossierScroll, _hudNavigationPanel, _hudTopPanel);
}
