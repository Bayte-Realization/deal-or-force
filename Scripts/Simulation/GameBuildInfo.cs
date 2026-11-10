namespace DealOrForce.Simulation;

/// <summary>Human-facing release label, deliberately independent of the network protocol.</summary>
public static class GameBuildInfo
{
    public const string Version = "0.6.3.0";
    /// <summary>What the main menu and the launcher show. Not the save format or the network protocol.</summary>
    public const string VersionLabel = "EntertAlpha " + Version;
}
