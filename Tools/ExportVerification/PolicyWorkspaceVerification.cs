using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyPolicyWorkspace(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        // The four-pixel outer gutter belongs to the map, so parking the cursor there
        // legitimately opens a province tooltip across the document. Use the HUD's empty
        // inner padding and send a real motion event to refresh native hover state.
        var pointer = new Vector2(12, 10);
        Input.WarpMouse(pointer);
        Input.ParseInputEvent(new InputEventMouseMotion { Position = pointer, GlobalPosition = pointer });
        var nation = world.NationByTag("POL");
        world.ConfigureSinglePlayer(nation.Id); world.IsPaused = true; nation.Treasury = 200;
        game.VerificationSelectPlayerCountry();
        await Wait(.8);
        var policy = PoliticalPolicyCatalog.AllLaws.First(p => p.Scope == "nation" && !p.Default &&
            PoliticalPolicies.CanAdopt(world, nation, p, "", 0, false, out _));
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText");
            await VerifyHudSurfaceSafety(game, language, size);
            game.Call("OpenScreen", "Laws");
            var before = CampaignFingerprint.Of(world);
            SelectPolicy(policy);
            await Wait(.4);
            CheckFits(game.FindChild("PolicyPanel", true, false) as Control, "law workspace " + language + " " + size.X);
            CheckFits(game.FindChild("PolicyEnact", true, false) as Control, "persistent reform action " + language + " " + size.X);
            var action = game.FindChild("PolicyEnact", true, false) as Button;
            Check(action is { Disabled: false } && action.IsVisibleInTree(), "reform is reachable without scrolling");
            Check(game.FindChild("PolicyEffectTable", true, false) is GridContainer { Columns: 4 }, "effects have aligned current / selected / delta columns");
            Check(CampaignFingerprint.Of(world) == before, "selection and comparison are read-only");
            await Capture("laws-workspace-" + language + "-" + size.X);
            action?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
            Check(CampaignFingerprint.Of(world) == before, "first click only requests confirmation");
            Check(action == game.FindChild("PolicyEnact", true, false), "confirmation preserves the focused action instance");
            var cancel = game.FindChild("PolicyCancelReform", true, false) as Button;
            Check(cancel is not null && cancel.IsVisibleInTree(), "confirmation has an explicit cancel action");
            CheckFits(cancel, "cancel action " + language + " " + size.X);
            await Capture("laws-confirm-" + language + "-" + size.X);
            cancel?.EmitSignal(BaseButton.SignalName.Pressed);
            Check(CampaignFingerprint.Of(world) == before, "cancelling changes no simulation state");
        }
        var search = game.FindChild("PolicySearch", true, false) as LineEdit;
        search.Text = "__no_such_law__";
        // Assigning LineEdit.Text is deliberately silent in Godot. Drive the same signal as
        // editing text rather than mistaking a test-only property assignment for typing.
        search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text); await Wait(.2);
        Check(!(game.FindChild("PolicyEnact", true, false) as Button).IsVisibleInTree(), "empty search cannot enact the old selection");
        await Capture("laws-empty-russian-1280"); search.Text = "";
        search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
        var warOnly = PoliticalPolicyCatalog.AllLaws.First(p => p.RequiresWar);
        SelectPolicy(warOnly); await Wait(.1);
        Check((game.FindChild("PolicyEnact", true, false) as Button) is { Disabled: true }, "war-only law shows a disabled action at peace");
        await Capture("laws-disabled-russian-1280");
        foreach (var tab in new[] { "Regional", "Foreign", "Agendas", "Course", "Crises", "National" })
        {
            game.Call("SelectPolicyTab", tab); await Wait(.1);
            CheckFits(game.FindChild("PolicyPanel", true, false) as Control, "alternate laws tab " + tab);
        }
        SelectPolicy(policy); await Wait(.1);
        var pay = game.FindChild("PolicyEnact", true, false) as Button;
        var cash = nation.Treasury; var hours = world.ElapsedHours;
        pay?.EmitSignal(BaseButton.SignalName.Pressed); pay?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.2);
        Check(nation.Laws.Values.Any(l => l.Id == policy.Id) && MathF.Abs(nation.Treasury - (cash - nation.Gdp * policy.CostShare)) < .001f,
            "second click pays and adopts through the real command path");
        Check(world.IsPaused && world.ElapsedHours == hours, "adoption is immediate on pause");
        Check((game.FindChild("PolicyEnact", true, false) as Button) is { Disabled: true }, "enacted policy cannot be paid for twice");
        CheckFits(game.FindChild("PolicyEnact", true, false) as Control, "adopted law footer");
        await Capture("laws-adopted-russian-1280");

        void SelectPolicy(PoliticalPolicy chosen)
        {
            game.Call("SelectPolicyTab", chosen.Scope == "region" ? "Regional" : chosen.Scope == "foreign" ? "Foreign" : "National");
            var first = PoliticalPolicyCatalog.AllLaws.First(p => p.Scope == chosen.Scope && p.Category == chosen.Category);
            (game.FindChild("PolicyCategory_" + first.Id, true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
            var button = game.FindChild("SelectPolicy_" + chosen.Id, true, false) as Button;
            Check(button is not null, "law can be reached through category and option buttons: " + chosen.Id);
            button?.GrabFocus(); button?.EmitSignal(BaseButton.SignalName.Pressed);
            Check(button is not null && GodotObject.IsInstanceValid(button) && button.HasFocus(), "selection keeps keyboard focus");
        }
    }
}

public partial class DealOrForceGame
{
    internal void VerificationSelectPlayerCountry()
    {
        _selectedNation = _campaign.Player;
        _selectedState = _campaign.StatesOf(_selectedNation).FirstOrDefault();
        _globe.SetMapModeSubject(_selectedNation);
        _globe.FocusOn(_selectedNation.CapitalLatitude, _selectedNation.CapitalLongitude, 5.5f);
    }
}
