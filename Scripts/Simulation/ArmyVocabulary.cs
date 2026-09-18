namespace DealOrForce.Simulation;

/// <summary>
/// What an order and a posture are called.
///
/// The words lived in the operations panel, as a switch returning English literals, and two
/// screens away the map tooltip printed the enum's own name instead - so the same division
/// was "Advance front" in one place and "AdvanceFront" in the other, and neither was
/// translated. Words the player reads about a thing belong with the thing, not with whichever
/// screen happened to need them first.
///
/// Here, in the simulation layer, because the orders are declared here: anything that wants
/// to name one - a panel, a tooltip, an alert, a save-game summary, a mod - asks the same
/// question and gets the same answer in the same language.
/// </summary>
public static class ArmyVocabulary
{
    /// <summary>What an order is called, in the player's language.</summary>
    public static string OrderName(ArmyOrder order) => Loc.Get(order switch
    {
        ArmyOrder.DefendFront => "Defend front",
        ArmyOrder.AdvanceFront => "Advance front",
        ArmyOrder.ReserveFront => "Front reserve",
        ArmyOrder.Spearhead => "Spearhead",
        _ => "Manual"
    });

    /// <summary>And a posture.</summary>
    public static string StanceName(ArmyStance stance) => Loc.Get(stance switch
    {
        ArmyStance.Cautious => "Cautious",
        ArmyStance.Aggressive => "Aggressive",
        _ => "Balanced"
    });
}

/// <summary>
/// What one country's standing with another is called.
///
/// The screen printed the enum: a player looking at their relations with Ukraine read
/// "AtWar", which is a C# identifier and not a word in any language. It went through the
/// localisation table already - `Tr(relation.Stance.ToString())` - but the table was being
/// asked for "AtWar", and nothing had ever put "AtWar" in it, so the lookup fell through to
/// the key and the key was the identifier.
///
/// Here, beside the orders and the postures, for the same reason: the words for a thing
/// belong with the thing, and a screen that wants to name one asks rather than spells.
/// </summary>
public static class DiplomaticVocabulary
{
    public static string StanceName(DiplomaticStance stance) => Loc.Get(stance switch
    {
        DiplomaticStance.Friendly => "Friendly",
        DiplomaticStance.Allied => "Allied",
        DiplomaticStance.Rival => "Rival",
        DiplomaticStance.AtWar => "At war",
        DiplomaticStance.Truce => "Truce",
        _ => "Neutral"
    });
}
