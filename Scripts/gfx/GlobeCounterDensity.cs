using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private ulong _nextSurfaceDensityMs;

    private void UpdateSurfaceCounterDensity()
    {
        if (_flatMap || !IsInstanceValid(_armyRoot)) return;
        if (!_counterDensityDirty) return;
        var now = Time.GetTicksMsec();
        if (now < _nextSurfaceDensityMs) return;
        _nextSurfaceDensityMs = now + 100;
        _counterDensityDirty = false;
        // The province centre wins over screen-space crowding. Never combine
        // neighbouring provinces or retain a collision-avoidance surface slot.
        foreach (var marker in _armyStacks.Values)
        {
            var pos = ArmyMarkerPosition(marker);
            if (marker.TargetPosition != pos)
            {
                marker.TargetPosition = pos;
                OrientSurfaceArmyCard(marker);
            }
            if (marker.Node.Visible) SizeSurfaceArmyCounter(marker);
            RefreshProvinceCounterDisplay(marker, false);
        }
    }
}
