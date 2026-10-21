using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
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
    }
}
