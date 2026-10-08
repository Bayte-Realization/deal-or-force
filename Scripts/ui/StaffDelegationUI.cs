using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Everything a player can hand to their own staff, in one place (item 44).
///
/// The budget was delegated on the state screen, the trade desk on the trade screen, the navy
/// on the fleet panel and the army's size nowhere at all - so "let the country run itself
/// while I fight this war" meant four screens and one thing that could not be done. The own
/// country's card now lists all four, each a switch that does exactly what the one on its own
/// screen does, through the same command.
/// </summary>
public partial class DealOrForceGame
{
    private static readonly (string Portfolio, string On, string Off, string Tooltip)[] StaffPortfolios =
    {
        (Delegation.Budget, "The treasury is running itself", "Let the treasury run itself",
            "Hand tax and spending to your own civil service - the same staff that governs every " +
            "other country in the world. Once a month it taxes what a thinning treasury needs, " +
            "spends on the army when there is a war or a threat, buys quiet when the country is " +
            "unhappy and can afford it, and builds with the rest. It does not take decisions and " +
            "it does not touch the central bank; those stay yours. Take it back whenever you like."),
        (Delegation.Trade, "The trade desk is running itself", "Let the trade desk run itself",
            "Hand the trade desk to your own civil service. Once a month it buys what the country " +
            "is short of first, holds back what it cannot spare, sells a real surplus, and drops " +
            "the instruction again when a good stops being a problem. Everything it does shows up " +
            "in the list above beside your own decisions, and you can take the desk back at any " +
            "time - the instructions it left stay until you change them."),
        (Delegation.Army, "The general staff keeps the army at strength", "Let the general staff keep the army at strength",
            "The general staff raises a formation when the army falls below what the military budget pays for, " +
            "and stands one down after a peace that leaves too many - at your treasury's cost, in your most " +
            "populous region. Take it into your own hands and nothing is raised or disbanded unless you do it."),
        (Delegation.Command, "The general staff commands the free army", "Let the general staff command the free army",
            "Hand the formations you have not given orders to the general staff - the one that commands every " +
            "other country's army. At peace it keeps them where a country keeps its army: the capital, the " +
            "largest regions and the borders worth watching. At war it fills the holes in your lines from the " +
            "free reserve, and moves a formation that is starving to ground its supply can reach. It never " +
            "touches a front you drew or an order you gave. Take it back whenever you like."),
        (Delegation.Diplomacy, "The foreign ministry runs our relations", "Let the foreign ministry run our relations",
            "Hand day-to-day diplomacy to the foreign ministry: once a week it does what every other " +
            "government does with its neighbours - courts friends, cools towards rivals, asks for and gives " +
            "alliances and guarantees. It never declares a war and never signs a peace; those stay yours."),
        (Delegation.Cabinet, "The cabinet takes decisions", "Let the cabinet take decisions",
            "Hand the decisions to the cabinet: once a month it takes the one decision the computer's own " +
            "governments would take in your place, when one is worth its cost. A decision cannot be undone, " +
            "so this is the switch to think about before you flip it."),
        (Delegation.Navy, "The admiralty commands the navy", "Let the admiralty command the navy",
            "Hand every fleet to the admiralty - the staff that commands every other country's navy. " +
            "At peace it keeps them in port; at war it blockades, raids or escorts by who has the " +
            "stronger fleet, brings a fleet home before its stores run out, lands troops across water, " +
            "and lays down hulls to keep the navy at what the military budget pays for. " +
            "Your own orders still go through, and it takes them over again on its next look. Take it back whenever you like.")
    };

    /// <summary>The staff block of one's own country card: one switch per portfolio.</summary>
    private void AddStaffRows(VBoxContainer rows)
    {
        var nation = _campaign?.Player;
        if (nation is null) return;
        rows.AddChild(Title("THE STAFF", 11, Ui.Colour("text.caption")));
        // Everything at once, either way: the switch most players actually want.
        var allHeld = System.Linq.Enumerable.All(StaffPortfolios, entry => Delegation.Holds(nation, entry.Portfolio));
        var everything = ActionButton(allHeld ? "Take everything back" : "Hand everything to the staff", () =>
        {
            foreach (var (portfolio, _, _, _) in StaffPortfolios) DelegatePortfolio(portfolio, !allHeld);
        }, 0, 32);
        everything.TooltipText = Ui.Tip(Tr("Every portfolio below at once. Each can still be taken back on its own."));
        rows.AddChild(everything);
        foreach (var (portfolio, on, off, tooltip) in StaffPortfolios)
        {
            var held = Delegation.Holds(nation, portfolio);
            var toggle = new CheckButton { Text = Tr(held ? on : off) };
            toggle.SetPressedNoSignal(held);
            toggle.TooltipText = Ui.Tip(Tr(tooltip));
            var captured = portfolio;
            toggle.Toggled += delegated => DelegatePortfolio(captured, delegated);
            rows.AddChild(toggle);
        }
    }

    private void DelegatePortfolio(string portfolio, bool delegated)
    {
        if (_campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetDelegationCommand
        {
            IssuerId = _campaign.Player.Id,
            Portfolio = portfolio,
            Delegated = delegated
        });
        _campaign.Commands.ApplyAll(_campaign);
        // The card is rebuilt from the country, so the switch reads back what was actually set.
        if (MapSelectionShowing) ShowMapSelectionContent();
        if (_selectedFleet is not null) ShowSelectedFleet();
    }
}
