using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>
    /// The one card every action in the game stands on: what it does, why it is offered or
    /// refused, what it costs, and what it will actually change.
    ///
    /// Decisions, construction and diplomacy draw the same four lines in the same order, so a
    /// player learns the shape once instead of re-reading four different screens, and a mod
    /// author can see what an action has to answer before it is allowed to exist.
    ///
    /// The result section prints the effect lines the action will actually apply
    /// (<see cref="ScriptedEffects.Lines"/>) - the same text an event option shows, never a
    /// promised forecast. An action whose consequence is compiled into the game rather than
    /// written in data simply has no lines here; its description carries them instead.
    /// </summary>
    private Button ActionCard(Container parent, string title, string what, string cost,
        IReadOnlyList<string> result, string why, string actionLabel, bool enabled, Action enact, string name = null)
    {
        var card = WorkbenchCard(parent);

        var heading = Title(title, 15, Ui.Colour("text.bright"));
        heading.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        card.AddChild(heading);

        if (!string.IsNullOrWhiteSpace(what)) card.AddChild(CardLine(what, "text.body_soft"));
        if (!string.IsNullOrWhiteSpace(cost)) card.AddChild(CardLine(cost, "text.accent_dim"));

        if (result is { Count: > 0 })
        {
            card.AddChild(CardLine(Tr("Result"), "text.caption"));
            foreach (var line in result) card.AddChild(CardLine("· " + line, "text.body_soft"));
        }

        var button = ActionButton(actionLabel, enact, 0, 32, enabled);
        if (!string.IsNullOrWhiteSpace(name)) button.Name = name;
        button.Disabled = !enabled;
        card.AddChild(button);

        // Why it cannot be taken, under the button it belongs to: the reason is read after the
        // player has decided they want the action, not before it.
        if (!string.IsNullOrWhiteSpace(why)) card.AddChild(CardLine(why, "state.warning"));
        return button;
    }

    private Label CardLine(string text, string token)
    {
        var label = Title(text, 12, Ui.Colour(token));
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return label;
    }
}
