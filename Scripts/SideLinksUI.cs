using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Where to go next from whatever is being read.
///
/// A figure on the side panel is almost always a figure somebody wants to do something
/// about, and until now doing something about it meant already knowing which of eight
/// buttons along the top of the screen owned it. A player who has come from a Paradox game
/// expects the screen that shows a problem to offer the screen that fixes it; a player who
/// has come from Age of History expects rather more of the interface to be clickable than
/// this one was.
///
/// So the same destinations, put beside the figures that lead to them, and rebuilt for
/// whichever screen is open - a row of two or three, never a menu.
/// </summary>
public partial class DealOrForceGame
{
    private void RefreshSideLinks(string screen)
    {
        if (_sideLinks is null) return;
        foreach (var child in _sideLinks.GetChildren())
        {
            _sideLinks.RemoveChild(child);
            child.QueueFree();
        }
        if (_campaign?.Player is null) return;

        var foreign = _selectedNation is not null && _selectedNation != _campaign.Player;

        switch (screen)
        {
            case "Nation":
                AddSideLink("Govern this country", OpenStateAffairs,
                    "Taxes, spending, the cabinet, trade and the form of government itself.");
                AddSideLink("Charts and history", OpenLedger,
                    "The same country as a record: how it got here, and where it stands against the rest.");
                if (foreign)
                    AddSideLink("How they see us", () => OpenScreen("Diplomacy"),
                        "What this country currently makes of yours, and why.");
                break;

            case "Economy":
                AddSideLink("Set the budget", OpenStateAffairs,
                    "Every tax and every line of spending, in one place.");
                AddSideLink("Economic history", OpenLedger,
                    "What the budget and the market have done over the campaign.");
                break;

            case "Military":
                AddSideLink("Military spending", OpenStateAffairs,
                    "What the armed forces are given, against everything else the money could do.");
                AddSideLink("Every war", OpenWarLedger,
                    "What is being fought, what it is costing, and the table where it can be ended.");
                break;

            case "Diplomacy":
                AddSideLink("Every war", OpenWarLedger,
                    "What is being fought, and who is fighting it.");
                AddSideLink("Compare them", OpenLedger,
                    "This country's figures beside your own.");
                AddSideLink("Our politics", OpenStateAffairs,
                    "What your own government is, which is half of what they make of you.");
                break;

            case "Decisions":
                AddSideLink("What it would cost", OpenStateAffairs,
                    "The treasury and the week's balance the decision would be paid out of.");
                break;
        }
    }

    private void AddSideLink(string caption, Action action, string help)
    {
        var button = ActionButton(caption, action, 168, 30);
        button.TooltipText = Tr(help);
        _sideLinks.AddChild(button);
    }
}
