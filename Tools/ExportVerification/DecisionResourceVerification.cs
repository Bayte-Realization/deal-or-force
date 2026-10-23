using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyDecisionResources(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        var nation = world.NationByTag("POL");
        world.ConfigureSinglePlayer(nation.Id);
        world.IsPaused = true;
        nation.Treasury = 200;
        nation.ArmyExperience = nation.AirExperience = nation.NavalExperience = nation.PoliticalPoints = 200;
        game.Call("Refresh");
        game.Call("OpenScreen", "Decisions");
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language;
            GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText");
            game.VerificationSelectAgenda(AgendaWorkspace.Section.Decisions, "joint_air_staff_course");
            await Wait(.25);
            var d = DecisionCatalog.Find("joint_air_staff_course").Value;
            var before = CampaignFingerprint.Of(world);
            var cost = game.FindChild("AgendaCost", true, false) as Label;
            var effects = game.FindChild("AgendaEffects", true, false) as Label;
            var action = game.FindChild("Decision_joint_air_staff_course", true, false) as Button;
            Check(cost is not null && cost.Text.Contains(DecisionResources.PriceText(nation, d)),
                "actual multi-currency price and wallet are visible " + language + " " + size.X);
            Check(cost is not null && effects is not null && cost.GetGlobalRect().End.Y <= effects.GetGlobalRect().Position.Y + .5f &&
                cost.Size.Y + .5f >= cost.GetMinimumSize().Y, "resource price is not clipped or overlaid by effects " + language + " " + size.X);
            Check(action is not null && action.Disabled == DecisionCatalog.IsBlocked(world, nation, d),
                "button and authoritative resource eligibility agree " + language + " " + size.X);
            CheckFits(game.FindChild("CampaignDossier", true, false) as Control, "resource dossier " + language + " " + size.X);
            CheckFits(action, "resource action " + language + " " + size.X);
            await Capture("decision-resources-" + language + "-" + size.X);
            Check(CampaignFingerprint.Of(world) == before, "resource quotation and capture leave world state unchanged");
        }
        game.VerificationSelectAgenda(AgendaWorkspace.Section.Decisions, "civil_service_compact");
        await Wait(.15);
        var item = DecisionCatalog.Find("civil_service_compact").Value;
        var pay = game.FindChild("Decision_civil_service_compact", true, false) as Button;
        Check(pay is { Disabled: false }, "Poland can fund the civil-service compact");
        var cash = nation.Treasury;
        var points = nation.PoliticalPoints;
        pay?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.2);
        Check(nation.PoliticalPoints == points - 75 && System.MathF.Abs(nation.Treasury - (cash - DecisionCatalog.Cost(nation, item))) < .0001f,
            "real agenda click pays both the political and monetary price");
        Check(nation.DecisionAvailableDay.ContainsKey(item.Id) && ModifierSystem.Has(nation, item.Id),
            "paid compact creates the real expiring modifier and cooldown");
        var paid = CampaignFingerprint.Of(world);
        pay?.EmitSignal(BaseButton.SignalName.Pressed);
        Check(CampaignFingerprint.Of(world) == paid, "stale/double click cannot purchase the programme again");
        nation.ArmyExperience = 0;
        game.VerificationSelectAgenda(AgendaWorkspace.Section.Decisions, "joint_air_staff_course");
        await Wait(.15);
        var blocked = game.FindChild("Decision_joint_air_staff_course", true, false) as Button;
        var reason = game.FindChild("AgendaReason", true, false) as Label;
        Check(blocked is { Disabled: true } && reason.Text == DecisionResources.Refusal(nation,
            DecisionCatalog.Find("joint_air_staff_course").Value), "live wallet shortage disables the action and explains the exact currency");
        await Capture("decision-resources-russian-shortage");
        var selected = blocked;
        var day = world.ElapsedDays;
        world.IsPaused = false; world.TickDay(); world.TickDay(); world.IsPaused = true;
        var afterTicks = CampaignFingerprint.Of(world);
        game.Call("Refresh"); game.Call("ShowDecisionsScreen");
        await Wait(.15);
        Check(world.ElapsedDays == day + 2 && game.FindChild("Decision_joint_air_staff_course", true, false) == selected,
            "selected currency decision and controls survive two real campaign days");
        Check((game.FindChild("AgendaCost", true, false) as Label)?.Text.Contains(DecisionResources.PriceText(nation,
            DecisionCatalog.Find("joint_air_staff_course").Value)) == true,
            "live resource prices show the post-tick wallet rather than a stale quotation");
        Check(CampaignFingerprint.Of(world) == afterTicks, "post-tick quotation refresh changes no authoritative state");
        await Capture("decision-resources-russian-after-ticks");
    }
}
