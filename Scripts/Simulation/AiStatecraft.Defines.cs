namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// An ally or guarantor may offer cash when a client's institutions are failing.
    /// The money moves through the ordinary deal table. A player still has to sign.
    /// Off: that week is spent on courtship instead.
    /// </summary>
    public bool AiCrisisGrants { get; set; } = true;

    /// <summary>Days before the same patron offers that client another stabilization grant.</summary>
    public int AiCrisisGrantRetryDays { get; set; } = 360;

    /// <summary>
    /// Whether allied powers may agree to divide a weak common neighbour by simultaneous
    /// demands. The practice of every imperial age and outlawed by the post-UN world: off
    /// in the shipped 2024 scenario, and one row of data for a mod of an older one.
    /// </summary>
    public bool AiMayCoordinatePartitions { get; set; } = false;

    /// <summary>How many takers must be able to demand a slice before it is a partition and not acession.</summary>
    public int AiPartitionMinimumDemanders { get; set; } = 2;

    /// <summary>How overwhelmingly stronger the coalition must be than the victim before anybody proposes to divide it.</summary>
    public float AiPartitionStrengthMultiple { get; set; } = 3f;

    /// <summary>Days between one government's thoughts about partitioning anybody.</summary>
    public int AiPartitionCadenceDays { get; set; } = 90;

    /// <summary>The share of such thoughts that become a proposal. A government does not carve up a neighbour every time it could.</summary>
    public float AiPartitionAppetite { get; set; } = 0.5f;
}
