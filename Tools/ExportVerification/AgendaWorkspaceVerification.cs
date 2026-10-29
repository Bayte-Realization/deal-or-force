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
        var catalogue = game.FindChild("AgendaEntries", true, false) as ItemList;
        Check(host is not null && search is not null && section is not null && catalogue is not null, "agenda retained scrolling catalogue loads");
        Check(catalogue.ItemCount == DecisionCatalog.All.Count && catalogue.ItemCount > 4,
            "agenda contains every decision at once, not four per page");
        Check(game.FindChild("AgendaNext", true, false) is null && game.FindChild("AgendaPrevious", true, false) is null,
            "agenda has no page navigation");
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
        await Wait(.15);
        var last = catalogue.ItemCount - 1;
        catalogue.Select(last); catalogue.EmitSignal(ItemList.SignalName.ItemSelected, last);
        catalogue.EnsureCurrentIsVisible(); await Wait(.15);
        var scroll = catalogue.GetVScrollBar();
        var scrollBefore = scroll.Value;
        var selectedBefore = catalogue.GetItemMetadata(last).AsString();
        world.IsPaused = false; world.TickDay(); world.TickDay(); world.IsPaused = true;
        before = CampaignFingerprint.Of(world);
        catalogue.GrabFocus();
        game.Call("ShowDecisionsScreen"); await Wait(.15);
        Check(catalogue.GetSelectedItems().SequenceEqual(new[] { last }) && catalogue.HasFocus(),
            "agenda retains a late-list selection and keyboard focus through two days");
        Check(scrollBefore > 0 && Math.Abs(scroll.Value - scrollBefore) < 1,
            "agenda retains its scroll offset through two days");
        Check((game.FindChild("Decision_" + selectedBefore, true, false) as Button) is not null,
            "agenda late-list selection opens its exact command detail");
        Check(before == CampaignFingerprint.Of(world), "agenda scrolling and selection issue no orders");

        foreach (var language in new[] { "english", "russian" })
        {
            Loc.Language = language; game.Call("RefreshLocalisedText");
            foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
            {
                GetTree().Root.Size = size; await Wait(.25);
                CheckFits(game.FindChild("CampaignDossier", true, false) as Control, "agenda " + language + " " + size.X);
                Check(catalogue.ItemCount == DecisionCatalog.All.Count && catalogue.Size.Y <= (size.Y < 800 ? 180 : 280),
                    "agenda scroll viewport is bounded but contains the whole catalogue at " + size.X);
                CheckFits(catalogue, "agenda list " + language + " " + size.X);
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
                    Check((game.FindChild("AgendaTitle", true, false) as Label)?.Text == Loc.Get("The peace dividend"),
                        "agenda Russian goal title has no encoding corruption");
                await Capture("agenda-" + (index == 1 ? "promises-" : "goals-") + language);
            }
            section.Select(0); section.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        }
        game.VerificationSelectAgenda(AgendaWorkspace.Section.Decisions, target);
        Check(game.FindChild("Decision_" + target, true, false) is Button, "agenda deep link selects a decision near the end of the list");
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
        // A mod can add hundreds of initiatives without hundreds of Godot controls.
        game.VerificationSelectAgenda(AgendaWorkspace.Section.Decisions, target);
        host = game.FindChild("AgendaWorkspace", true, false) as Control;
        catalogue = game.FindChild("AgendaEntries", true, false) as ItemList;
        search = game.FindChild("AgendaSearch", true, false) as LineEdit;
        var controlCount = host.FindChildren("*", "", true, false).Count;
        try
        {
            for (var i = 0; i < 500; i++)
                DecisionCatalog.Override("agenda_ui_mod_" + i, "Mod initiative " + i, "Mod description", 7, 0, DecisionKind.Scripted);
            game.Call("ShowDecisionsScreen"); await Wait(.1);
            Check(catalogue.ItemCount == DecisionCatalog.All.Count && catalogue.ItemCount > 500,
                "agenda includes all 500 mod initiatives without pagination");
            Check(host.FindChildren("*", "", true, false).Count == controlCount,
                "agenda 500-entry mod adds no control nodes");
            search.Text = "__no_match__"; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
            Check(catalogue.ItemCount == 0 && !(game.FindChild("AgendaSelectedDetail", true, false) as Control)
                .FindChildren("*", "Button", true, false).Cast<Button>().Any(b => b.Visible),
                "agenda empty search exposes no fallback command");
        }
        finally
        {
            DecisionCatalog.ResetCatalog();
            search.Text = ""; search.EmitSignal(LineEdit.SignalName.TextChanged, "");
        }
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
