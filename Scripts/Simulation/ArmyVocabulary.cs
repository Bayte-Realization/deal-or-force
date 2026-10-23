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

    /// <summary>
    /// Why a formation is where it is, in the words a player needs to tell "waiting" from
    /// "stuck".
    ///
    /// A division standing still with no explanation reads as a broken order. Most of the
    /// time it is doing exactly what it was told - holding a line, preparing an assault,
    /// waiting for a plan to ripen - and a few of those states are the difference between a
    /// front that is quiet and a front that is not working. The exact number is included
    /// wherever one exists: planning, provinces left on the march.
    /// </summary>
    public static string Situation(Campaign campaign, Army army)
    {
        if (army is null) return "";
        if (army.InBattle) return Loc.Format("Engaged in battle #{0}", army.BattleId);
        if (army.EmbarkedFleetId >= 0) return Loc.Get("At sea under the fleet's orders.");
        if (campaign is not null && MilitaryAccess.MustReturn(campaign, army))
            return Loc.Get("Interned on neutral ground; awaiting a safe return home.");
        if (campaign is not null && army.ReadyDay > campaign.ElapsedDays)
            return Loc.Format("Regrouping: {0} day(s) before combat readiness.", army.ReadyDay - campaign.ElapsedDays);
        if (army.CutOff) return Loc.Get("Cut off from supply; waiting for the pocket to be relieved.");
        if (army.DirectOrder)
            return army.Route.Count > 0
                ? Loc.Format("Following a direct march: {0:N0} province(s) to go.", army.Route.Count)
                : Loc.Get("Holding the destination of a direct order.");
        if (army.FrontLineId >= 0) return Loc.Get("Holding a line drawn on the map.");
        if (army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead)
        {
            if (!army.PlanActive)
                return Loc.Format(
                    "Preparing an offensive: plan {0:P0} ready; it attacks when the staff activates it.",
                    army.Planning);
            if (army.Route.Count > 0)
                return Loc.Format("Advancing to the front: {0:N0} province(s) to go.", army.Route.Count);
            if (army.OrderProvinceId > 0 && army.OrderProvinceId == army.ProvinceId)
                return Loc.Get("In position; waiting for the assault conditions.");
            return Loc.Get("Advancing: no route to an enemy province yet.");
        }
        if (army.OrderKind == ArmyOrder.DefendFront) return Loc.Get("Holding the line; it attacks only when ordered.");
        if (army.OrderKind == ArmyOrder.ReserveFront) return Loc.Get("In reserve behind the line.");
        if (army.AssignedWarId >= 0) return Loc.Get("Attached to a war but not yet given a role.");
        if (campaign is not null)
            foreach (var war in campaign.Wars)
                if (!war.Ended && war.Involves(army.NationId))
                    return Loc.Get("Idle: no front has claimed this formation.");
        return Loc.Get("At rest: the country is not at war.");
    }
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
