namespace DealOrForce.Simulation;

/// <summary>Human-facing release label, deliberately independent of the network protocol.</summary>
public static class GameBuildInfo
{
    public const string Version = "0.7.2.3";
    /// <summary>What the main menu and the launcher show. Not the save format or the network protocol.</summary>
    public const string VersionSuffix = "-dev";
    public const string VersionLabel = "EntertAlpha " + Version + VersionSuffix;
}
