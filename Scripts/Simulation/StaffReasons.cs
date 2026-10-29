namespace DealOrForce.Simulation;

/// <summary>
/// Why the staff last closed a hole with this formation. None until that happens, and cleared
/// when a later order command replaces the role. A default adds nothing to a save or a fingerprint.
/// </summary>
public enum StaffNote : byte
{
    None = 0,
    EmptyPostFromReserve = 1,
    ThreatenedHole = 2
}

/// <summary>
/// One sentence for the order a formation already has. A read: it does not issue an order,
/// trace supply, or write the campaign.
/// </summary>
public static class StaffReasons
{
    /// <summary>The refusal a player reads when the depot record is under the attack floor.</summary>
    public const string DepotShortSentence =
        "The depot covered {0} percent of the day's ammunition or oil, under the {1} percent an attack needs, so the attack does not start.";

    /// <summary>
    /// A cut-off or stranded formation does not begin an attack on its own. A direct order
    /// is the player's, and the existing dry-out rule still applies to it.
    /// </summary>
    public static bool SupplyHoldsAttack(Army army) =>
        army is not null && !army.DirectOrder && (army.CutOff || army.SupplyDistance < 0);

    /// <summary>
    /// The road hold, or a depot whose recorded ammunition or oil cover is under the floor.
    /// A direct order is not held. A read of the depot: it does not spend it.
    /// </summary>
    public static bool SupplyHoldsAttack(Campaign campaign, Army army) =>
        SupplyHoldsAttack(army) ||
        (army is not null && !army.DirectOrder && LogisticsSystem.DepotShortOfAttack(campaign, army));

    /// <summary>Short label for a fixed, clipped row. The full sentence is <see cref="Explain"/>.</summary>
    public static string Label(Campaign campaign, Army army) => Loc.Get(LabelKey(campaign, army));

    /// <summary>The full sentence, in the player's language.</summary>
    public static string Explain(Campaign campaign, Army army)
    {
        if (army is null) return "";
        var key = SentenceKey(campaign, army);
        if (key == DepotShortSentence)
            return Loc.Format(key, LogisticsSystem.ShortCoverPercent(campaign, army),
                LogisticsSystem.AttackCoverPercent(campaign));
        return Loc.Get(key);
    }

    private static string LabelKey(Campaign campaign, Army army) => SentenceKey(campaign, army) switch
    {
        "The direct order was kept." => "Order kept",
        "No path to a supply hub, so the attack does not start." => "No route",
        DepotShortSentence => "Depot short",
        "A free reserve filled the empty post." => "Reserve fill",
        "The hole is already threatened." => "Hole threatened",
        "The spearhead stays concentrated." => "Spear stays",
        "The staff ordered an advance along the front." => "Staff advance",
        "The staff ordered this formation to hold the line." => "Staff hold",
        "The staff held this formation in reserve." => "Staff reserve",
        _ => "No staff role"
    };

    private static string SentenceKey(Campaign campaign, Army army)
    {
        if (army is null) return "";
        if (army.DirectOrder) return "The direct order was kept.";
        if (army.CutOff || army.SupplyDistance < 0)
            return "No path to a supply hub, so the attack does not start.";
        if (LogisticsSystem.DepotShortOfAttack(campaign, army))
            return DepotShortSentence;
        if (army.OrderKind == ArmyOrder.DefendFront && army.StaffNote == StaffNote.EmptyPostFromReserve)
            return "A free reserve filled the empty post.";
        if (army.OrderKind == ArmyOrder.DefendFront && army.StaffNote == StaffNote.ThreatenedHole)
            return "The hole is already threatened.";
        return army.OrderKind switch
        {
            ArmyOrder.Spearhead => "The spearhead stays concentrated.",
            ArmyOrder.AdvanceFront => "The staff ordered an advance along the front.",
            ArmyOrder.DefendFront => "The staff ordered this formation to hold the line.",
            ArmyOrder.ReserveFront => "The staff held this formation in reserve.",
            _ => "No staff order has replaced this formation."
        };
    }
}
