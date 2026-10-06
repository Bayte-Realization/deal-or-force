using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private ulong _nextSurfaceDensityMs;

    private void UpdateSurfaceCounterDensity()
    {
        if (_flatMap || !IsInstanceValid(_armyRoot)) return;
        var now = Time.GetTicksMsec();
        if (!_counterDensityDirty && now < _nextSurfaceDensityMs) return;
        _nextSurfaceDensityMs = now + 100;
        _counterDensityDirty = false;
        // The province centre wins over screen-space crowding. Never combine
        // neighbouring provinces or retain a collision-avoidance surface slot.
        foreach (var marker in _armyStacks.Values)
        {
            marker.Node.Position = ArmyMarkerPosition(marker);
            OrientSurfaceArmyCard(marker);
            SizeSurfaceArmyCounter(marker);
            RefreshProvinceCounterDisplay(marker, false);
        }
    }
}
