using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>Reports must count simulated days, never successful calls to a stopped clock.</summary>
internal static class SimulationReportProgress
{
    internal static bool TickDay(Campaign campaign, out string failure)
    {
        var before = campaign.ElapsedDays;
        campaign.TickDay();
        failure = !string.IsNullOrEmpty(campaign.SimulationFault)
            ? $"day {campaign.ElapsedDays}: simulation fault: {campaign.SimulationFault}"
            : campaign.ElapsedDays <= before
                ? $"day {before}: clock did not advance (paused={campaign.IsPaused})"
                : "";
        return failure.Length == 0;
    }
}
