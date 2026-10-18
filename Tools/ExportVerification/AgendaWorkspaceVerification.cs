using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyAgendaWorkspace(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.ConfigureSinglePlayer(world.NationByTag("POL").Id);
        world.IsPaused = true;
        game.Call("OpenScreen", "Decisions");
        await Wait(.2);
        var host = game.FindChild("AgendaWorkspace", true, false) as Control;
        var search = game.FindChild("AgendaSearch", true, false) as LineEdit;
        var section = game.FindChild("AgendaSection", true, false) as OptionButton;
        Check(host is not null && search is not null && section is not null, "agenda retained catalogue loads");
        ulong[] Ids() => host.FindChildren("*", "", true, false).Select(n => n.GetInstanceId()).OrderBy(id => id).ToArray();
        var ids = Ids();
        var before = CampaignFingerprint.Of(world);
        var target = DecisionCatalog.All.Last().Id;
        search.Text = target; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
        search.GrabFocus(); search.CaretColumn = Math.Min(2, target.Length);
        game.Call("ShowDecisionsScreen");
        Check(search.Text == target && search.CaretColumn == Math.Min(2, target.Length), "agenda refresh retains query and caret");
        Check(before == CampaignFingerprint.Of(world), "agenda search and inspection leave the authoritative world unchanged");
        var day = world.ElapsedDays;
        world.IsPaused = false; world.TickDay(); world.TickDay(); world.IsPaused = true;
        before = CampaignFingerprint.Of(world);
        game.Call("Refresh"); game.Call("ShowDecisionsScreen");
        await Wait(.2);
        Check(world.ElapsedDays == day + 2 && search.Text == target && search.HasFocus(), "agenda search survives two real campaign days");
        Check(ids.SequenceEqual(Ids()), "agenda background refresh replaces no control nodes");
        Check(before == CampaignFingerprint.Of(world), "agenda redraw after the tick consumes no commands or RNG");
        search.ReleaseFocus();
        search.Text = ""; search.EmitSignal(LineEdit.SignalName.TextChanged, "");
        section.GetPopup().Popup();
        game.Call("ShowDecisionsScreen"); await Wait(.1);
        Check(section.GetPopup().Visible, "agenda dropdown remains open through a refresh");
        section.GetPopup().Hide();

        foreach (var language in new[] { "english", "russian" })
        {
            Loc.Language = language; game.Call("RefreshLocalisedText");
            foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
            {
                GetTree().Root.Size = size; await Wait(.25);
                CheckFits(game.FindChild("CampaignDossier", true, false) as Control, "agenda " + language + " " + size.X);
                Check(host.FindChildren("AgendaEntry*", "Button", true, false).Cast<Button>().Count(b => b.Visible) <= (size.Y < 800 ? 2 : 4),
                    "agenda visible rows stay bounded at " + size.X);
                await Capture("agenda-" + language + "-" + size.X);
                Check(search.PlaceholderText == Loc.Get("Find an initiative or ID..."), "agenda search is localised in " + language);
            }
            for (var index = 1; index <= 2; index++)
            {
                section.Select(index); section.EmitSignal(OptionButton.SignalName.ItemSelected, index);
                await Wait(.1);
                if (index == 1)
                {
                    var reason = (game.FindChild("AgendaReason", true, false) as Label)?.Text;
                    var expected = Loc.Get(GovernmentPromises.BlockReason(world, world.Player, PromiseCatalog.All.First()));
                    Check(reason == expected, "agenda refusal is localised in " + language);
                }
                else if (language == "russian")
                    Check((game.FindChild("AgendaTitle", true, false) as Label)?.Text == "Дивиденд мира",
                        "agenda Russian goal title has no encoding corruption");
                await Capture("agenda-" + (index == 1 ? "promises-" : "goals-") + language);
            }
            section.Select(0); section.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        }
        game.VerificationSelectAgenda(AgendaWorkspace.Section.Decisions, target);
        Check(game.FindChild("Decision_" + target, true, false) is Button, "agenda deep link can select a decision beyond the first page");
        game.Call("SelectDossierTab", "Opportunities"); await Wait(.1);
        game.Call("AtlasOpenTab", "Decisions", "Decision_" + target); await Wait(.1);
        Check(game.FindChild("Decision_" + target, true, false) is Button, "opportunity atlas still opens its exact decision");

        game.VerificationSelectAgenda(AgendaWorkspace.Section.Goals, GoalCatalog.All.First().Id);
        var goalId = GoalCatalog.All.First().Id;
        var count = world.Player.Goals.Count;
        var action = game.FindChild("Goal_" + goalId, true, false) as Button;
        Check(action is { Disabled: false }, "agenda offers a real goal action");
        action?.EmitSignal(BaseButton.SignalName.Pressed);
        Check(world.Player.Goals.Count == count + 1 && world.Player.Goals.Any(g => g.Id == goalId), "agenda goal button goes through the real command executor");
        action?.EmitSignal(BaseButton.SignalName.Pressed);
        Check(world.Player.Goals.Count == count, "agenda active goal button abandons through the same authority");
        game.Call("OpenScreen", "Nation"); await Wait(.1);
        Check(!(game.FindChild("AgendaWorkspace", true, false) as Control).IsVisibleInTree(), "agenda actions do not leak into another dossier");
        GetTree().Root.Size = new(1920,1080);
        Loc.Language = "english"; game.Call("RefreshLocalisedText");
    }
}

public partial class DealOrForceGame
{
    internal void VerificationSelectAgenda(AgendaWorkspace.Section section, string id) => SelectAgendaEntry(section, id);
}
