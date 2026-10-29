using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyPolicyProgrammes(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        var nation = world.NationByTag("POL");
        world.ConfigureSinglePlayer(nation.Id);
        world.IsPaused = true;
        nation.Treasury = 200;
        nation.PoliticalPoints = 200;
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language;
            GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText");
            game.Call("OpenScreen", "Decisions");
            game.VerificationSelectAgenda(AgendaWorkspace.Section.Decisions, "anti_corruption");
            await Wait(.3);
            var before = CampaignFingerprint.Of(world);
            var effects = game.FindChild("AgendaEffects", true, false) as Label;
            var pay = game.FindChild("Decision_anti_corruption", true, false) as Button;
            Check(effects is not null && effects.Text.Contains(Loc.Get("Revenue compliance audit")) &&
                effects.Text.Contains("270"), "agenda explains the real programme and its duration " + language + " " + size.X);
            CheckFits(pay, "programme purchase " + language + " " + size.X);
            CheckFits(game.FindChild("CampaignDossier", true, false) as Control, "programme dossier " + language + " " + size.X);
            await Capture("policy-programme-" + language + "-" + size.X);
            Check(CampaignFingerprint.Of(world) == before, "viewing the programme changes no simulation state");

            var definition = EventCatalog.All.Single(e => e.Id == "budget_crisis");
            world.PendingEvents.Clear();
            var pending = new PendingCampaignEvent { Id = 206, NationId = nation.Id, Title = definition.Title,
                Description = definition.Description, ExpiresDay = world.ElapsedDays + 30, Options = definition.Options };
            world.PendingEvents.Add(pending);
            game.Call("RefreshEventInterface");
            await Wait(.3);
            before = CampaignFingerprint.Of(world);
            for (var index = 0; index < definition.Options.Length; index++)
            {
                var button = game.FindChild("EventChoice" + index, true, false) as Button;
                Check(button is not null && button.Text == Loc.Get(definition.Options[index].Title),
                    "event choice translated " + language + " " + index);
                CheckFits(button, "event choice " + language + " " + size.X + " " + index);
                if (index == 0)
                    Check(button is not null && button.TooltipText.Contains(Loc.Get("Revenue compliance audit")) &&
                        button.TooltipText.Contains("270"), "event tooltip names the actual programme and term");
            }
            await Capture("policy-budget-event-" + language + "-" + size.X);
            Check(CampaignFingerprint.Of(world) == before, "event preview and capture are read-only");
            world.PendingEvents.Clear();
            game.Call("RefreshEventInterface");
        }
        game.Call("OpenScreen", "Decisions");
        game.VerificationSelectAgenda(AgendaWorkspace.Section.Decisions, "anti_corruption");
        await Wait(.2);
        var action = game.FindChild("Decision_anti_corruption", true, false) as Button;
        var sliders = (nation.TaxRate, nation.MilitarySpending, nation.WelfareSpending, nation.EducationSpending);
        var cash = nation.Treasury;
        var points = nation.PoliticalPoints;
        var hours = world.ElapsedHours;
        Check(action is { Disabled: false }, "actual Polish programme button is usable");
        action?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.2);
        Check(ModifierSystem.Has(nation, "revenue_compliance_audit") && nation.Treasury < cash && nation.PoliticalPoints < points,
            "actual click pays the cash and political price and grants the programme");
        Check(sliders == (nation.TaxRate, nation.MilitarySpending, nation.WelfareSpending, nation.EducationSpending) &&
            world.ElapsedHours == hours && world.IsPaused, "programme click preserves budgets and starts immediately on pause");
    }
}
