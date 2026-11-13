namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    /// <summary>
    /// The developer console's "instabuild": every construction project of this country is finished,
    /// free, on the next day's construction pass (EconomyCore). Saved, and part of the fingerprint,
    /// because it changes the world.
    /// </summary>
    public bool InstantConstruction;
}
