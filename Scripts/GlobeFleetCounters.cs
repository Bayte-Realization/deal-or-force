namespace DealOrForce;

/// <summary>
/// How big a fleet's counter is drawn. Kept beside the army counter's own constants in
/// spirit: a fleet is read the way a division is, so it grows with the camera the way a
/// division does (UpdateCounterScale), and a selected one stands a little larger than the
/// rest as well as wearing the same glow.
/// </summary>
public sealed partial class GlobePresentation
{
    /// <summary>The fleet plate at the far zoom; multiplied by the same counter scale armies use.</summary>
    private const float FleetPlatePixelSize = 0.00011f;

    /// <summary>
    /// The hull count on it, likewise - sized to read as a number on a card rather than a
    /// headline across it, in the clear middle the ship's silhouette leaves free.
    /// </summary>
    private const float FleetTextPixelSize = 0.00012f;

    /// <summary>How much larger the selected fleet's counter stands.</summary>
    private const float SelectedFleetScale = 1.2f;
}
