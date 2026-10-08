using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    // Preserve the existing Economy/Finance tab and mod navigation entry point.
    // Financial orders now have one workspace and one confirmation path.
    private void ShowSovereignFinance(Nation nation) => OpenSovereignFinance();
}
