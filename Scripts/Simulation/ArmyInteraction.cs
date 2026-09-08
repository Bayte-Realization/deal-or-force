namespace DealOrForce.Simulation;

/// <summary>Local UI access only. Simulation commands still authorize against their issuer,
/// not the local player, so AI and other network players keep control of their own units.</summary>
public static class ArmyInteraction
{
    public static bool CanSelect(Campaign campaign, Army army) =>
        campaign?.Player is { IsHuman: true } player && army is not null && army.NationId == player.Id;
}
