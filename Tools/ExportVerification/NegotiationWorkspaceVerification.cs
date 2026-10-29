using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyNegotiationWorkspace(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.ConfigureSinglePlayer(world.NationByTag("POL").Id); world.IsPaused = true;
        game.VerificationSelectPlayerCountry();
        var pointer = new Vector2(12, 10);
        Input.WarpMouse(pointer);
        Input.ParseInputEvent(new InputEventMouseMotion { Position = pointer, GlobalPosition = pointer });
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText"); game.VerificationOpenDiplomacy("DEU"); await Wait(.4);
            var before = CampaignFingerprint.Of(world); var random = world.Random.State;
            var search = game.FindChild("DiplomacyActionSearch", true, false) as LineEdit;
            var country = game.FindChild("DiplomacySearch", true, false) as LineEdit;
            var shortcuts = game.FindChild("DiplomacyShortcuts", true, false) as Control;
            CheckFits(game.FindChild("DiplomacyWorkspacePanel", true, false) as Control, "diplomacy workspace " + language + size.X);
            Check(shortcuts.GetChildren().OfType<Control>().All(c => shortcuts.GetGlobalRect().Grow(1).Encloses(c.GetGlobalRect())),
                "diplomacy shortcuts flow within their own row " + language + size.X);
            Check(search is not null && country is not null, "separate country and action searches exist");
            await Capture("negotiation-diplomacy-" + language + "-" + size.X);
            var target = game.FindChild("IssueUltimatumWide", true, false) as Button;
            Search(search, target.Text); await Wait(.1);
            Check(target.IsVisibleInTree(), "action search finds a real localized diplomatic action");
            game.Call("ForceDiplomacyRefresh"); await Wait(.1);
            Check(search.Text == target.Text && target.IsVisibleInTree(), "refresh preserves search and rule-controlled visibility");
            await Capture("negotiation-search-" + language + "-" + size.X);
            Search(search, "__no_such_action__"); await Wait(.1);
            Check((game.FindChild("DiplomacyActionEmpty", true, false) as Control).IsVisibleInTree() &&
                !target.IsVisibleInTree(), "empty action search is explained without a stale actionable result");
            await Capture("negotiation-empty-" + language + "-" + size.X);
            Search(search, ""); Search(country, "__no_such_country__"); await Wait(.1);
            Check((game.FindChild("DiplomacyCountryEmpty", true, false) as Control).IsVisibleInTree(), "empty country filter gives a recovery instruction");
            Search(country, ""); await Wait(.1);
            var own = game.FindChild("DiplomacyMyCountry", true, false) as Button;
            own.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
            Check(!(game.FindChild("DiplomacyActionColumn", true, false) as Control).Visible, "own-country dossier cannot target itself with foreign actions");
            game.VerificationOpenDiplomacy("DEU"); await Wait(.1);
            target.GrabFocus();
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Tab, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Tab, Pressed = false });
            await Wait(.1);
            var focus = game.GetViewport().GuiGetFocusOwner();
            Check(focus is not null && focus != target && focus.IsVisibleInTree(), "Tab advances to a visible control without executing diplomacy");
            Check(CampaignFingerprint.Of(world) == before && world.Random.State == random, "search, navigation and keyboard inspection preserve campaign authority");
            game.Call("CloseDiplomacyScreen");
        }
    }

    private static void Search(LineEdit field, string text)
    {
        field.Text = text; field.EmitSignal(LineEdit.SignalName.TextChanged, text);
    }

    private async Task VerifyDeskSelectionClearance(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.ConfigureSinglePlayer(world.NationByTag("POL").Id); world.IsPaused = true;
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText"); game.Call("SetDeskSide", false); game.Call("SetDeskOpen", true);
            game.VerificationSelectPlayerCountry();
            game.Call("OpenScreen", "Nation"); await Wait(.2);
            var desk = game.FindChild("SituationDesk", true, false) as Control;
            var fullHeight = desk.Size.Y;
            var fp = CampaignFingerprint.Of(world);
            game.VerificationSelectOwnedProvinceForDesk(); await Wait(.25);
            var card = game.VerificationRegionCard;
            Check(card.Visible && desk.Visible, "region and desk are both usable " + language + size.X);
            Check(desk.GetGlobalRect().End.Y <= card.GetGlobalRect().Position.Y - 3,
                "desk clears the entire region/province card with a visible gap " + language + size.X);
            CheckFits(desk, "desk above selection " + language + size.X);
            CheckFits(card, "region/province selection " + language + size.X);
            await Capture("desk-region-clearance-" + language + "-" + size.X);
            game.VerificationHideRegionCardForDesk(); await Wait(.2);
            Check(desk.Size.Y >= fullHeight - 1, "closing selection restores authored desk height");
            Check(CampaignFingerprint.Of(world) == fp, "selection dock layout is read-only");
        }
        game.Call("SetDeskOpen", false);
    }
}

public partial class DealOrForceGame
{
    internal Control VerificationRegionCard => _regionCard;
    internal void VerificationSelectOwnedProvinceForDesk()
    {
        _sidePanel.Visible = false;
        var map = _campaign.ProvinceMap;
        var province = Enumerable.Range(1, map.ProvinceCount).First(p => !map.IsWater(p) && map.ProvinceToNation[p] == _campaign.Player.MapIndex);
        var state = _campaign.States.First(s => s.MapIndex == map.ProvinceToState[province]);
        var point = map.CenterOf(province);
        ShowMapSelection(new GlobePick(_campaign.Player, state, province, point.Latitude, point.Longitude));
    }
    internal void VerificationHideRegionCardForDesk() { _regionCard.Visible = false; LayoutMapSelectionCards(); }
}
