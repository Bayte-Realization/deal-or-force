using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Who governs and what they are for, on the screen where a country is governed.
///
/// Deliberately not a window of its own. A player looking for their form of government is
/// looking for a thing they can change, and everything else they can change about their own
/// country is already on State Affairs - a separate Politics screen would be a ninth button
/// in the top bar for two rows of choices, and one more place to have to know about.
///
/// Both rows are built from the catalogues, so a mod that adds a fourteenth ideology gets a
/// button for it, named and explained out of its own file, without this code being told.
/// </summary>
public partial class DealOrForceGame
{
    private HFlowContainer _governmentButtons = null!;
    private HFlowContainer _ideologyButtons = null!;
    private Label _politicsSummary = null!;
    private Label _politicsFeedback = null!;
    private readonly Dictionary<string, Button> _governmentButtonById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _ideologyButtonById = new(StringComparer.OrdinalIgnoreCase);

    private void BuildPoliticsControls(VBoxContainer rows)
    {
        StateSection(rows, "POLITICS",
            "How the state is arranged, and what the people running it are for. They are two " +
            "questions rather than one: a parliamentary republic can be liberal or socialist, and " +
            "a one-party state can be socialist or fascist. Other countries can see both.");

        _politicsSummary = RowLabel("", 12, Ui.Colour("text.body_soft"));
        _politicsSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_politicsSummary);

        rows.AddChild(RowLabel("FORM OF GOVERNMENT", 11, Ui.Colour("text.caption")));
        _governmentButtons = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _governmentButtons.AddThemeConstantOverride("h_separation", 5);
        _governmentButtons.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(_governmentButtons);
        foreach (var government in GovernmentCatalog.All)
        {
            var id = government.Id;
            var button = ActionButton(government.Name, () => ChangePolitics(id, ""), 172, 30);
            button.TooltipText = Ui.Tip(GovernmentTooltip(government));
            _governmentButtons.AddChild(button);
            _governmentButtonById[id] = button;
        }

        rows.AddChild(RowLabel("IDEOLOGY", 11, Ui.Colour("text.caption")));
        _ideologyButtons = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _ideologyButtons.AddThemeConstantOverride("h_separation", 5);
        _ideologyButtons.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(_ideologyButtons);
        foreach (var ideology in IdeologyCatalog.All)
        {
            var id = ideology.Id;
            var button = ActionButton(ideology.Name, () => ChangePolitics("", id), 172, 30);
            button.TooltipText = Ui.Tip(IdeologyTooltip(ideology));
            _ideologyButtons.AddChild(button);
            _ideologyButtonById[id] = button;
        }

        _politicsFeedback = RowLabel("", 11, Ui.Colour("text.accent_dim"));
        _politicsFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_politicsFeedback);
    }

    private void ChangePolitics(string government, string ideology)
    {
        if (_campaign?.Player is null) return;
        var refusal = PoliticsCore.Refusal(_campaign, _campaign.Player, government, ideology);
        if (refusal.Length > 0)
        {
            _politicsFeedback.Text = Tr(refusal);
            return;
        }
        _campaign.Commands.Enqueue(new ChangePoliticsCommand
        {
            IssuerId = _campaign.Player.Id,
            Government = government,
            Ideology = ideology
        });
        _campaign.Commands.ApplyAll(_campaign);
        RefreshStateAffairs();
    }

    /// <summary>
    /// Say what the country is, what it would cost to stop being it, and - when the two
    /// halves do not fit - that it is costing something every week already.
    /// </summary>
    private void RefreshPolitics(Nation nation)
    {
        if (_politicsSummary is null || nation is null) return;

        var government = PoliticsCore.GovernmentOf(nation);
        var ideology = PoliticsCore.IdeologyOf(nation);
        var settling = PoliticsCore.ChangeCooldownDays - (_campaign.ElapsedDays - nation.PoliticsChangedDay);

        var summary = Simulation.Loc.Format("{0} · {1} · the centre decides {2:P0} of it",
            Tr(government.Name), Tr(ideology.Name), government.Centralisation);
        if (PoliticsCore.IsMismatched(nation))
            summary += "\n" + Simulation.Loc.Format(
                "{0} is uncomfortable in {1}, and the arrangement costs stability every week it lasts.",
                Tr(ideology.Name), Tr(government.Name));
        _politicsSummary.Text = summary;

        foreach (var (id, button) in _governmentButtonById)
            ApplyLedgerButtonState(button, string.Equals(id, nation.Government, StringComparison.OrdinalIgnoreCase));
        foreach (var (id, button) in _ideologyButtonById)
            ApplyLedgerButtonState(button, string.Equals(id, nation.Ideology, StringComparison.OrdinalIgnoreCase));

        if (settling > 0)
        {
            _politicsFeedback.Text = Simulation.Loc.Format(
                "The country is still settling after the last change. {0} day(s) to go.", settling);
            return;
        }
        // What it would cost from here, priced against the form being left rather than the
        // one being arrived at - which is why the number moves when the government does.
        var cost = PoliticsCore.BaseChangeCost * government.ChangeCost;
        _politicsFeedback.Text = Simulation.Loc.Format(
            "Changing the form of government would cost about {0:P0} stability and stir up the regions; " +
            "changing only the ideology costs less. Either way nothing else may change for {1} days.",
            cost, PoliticsCore.ChangeCooldownDays);
    }
}
