using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly List<(ArmyStackMarker Marker, Vector2 Screen, bool Selected)> _densityCandidates = new();
    private readonly HashSet<Vector2I> _occupiedCounterCells = new();
    private ulong _nextCounterDensityMs;

    // Only presentation is simplified: no province buckets or simulation divisions
    // are merged. The small marks remain at the real positions and remain pickable.
    private void UpdateFlatCounterDensity()
    {
        var now = Time.GetTicksMsec();
        if (now < _nextCounterDensityMs) return;
        _nextCounterDensityMs = now + 100;
        _densityCandidates.Clear();
        _occupiedCounterCells.Clear();
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !marker.Node.Visible || marker.Stack.Count == 0) continue;
            var selected = false;
            foreach (var army in marker.Stack)
                if (_selectedArmyIds.Contains(army.Id)) { selected = true; break; }
            _densityCandidates.Add((marker, _camera.UnprojectPosition(marker.Node.GlobalPosition), selected));
        }
        _densityCandidates.Sort((a, b) => a.Selected != b.Selected
            ? (a.Selected ? -1 : 1) : a.Marker.Stack[0].Id.CompareTo(b.Marker.Stack[0].Id));
        foreach (var (marker, at, selected) in _densityCandidates)
        {
            // Conservative grid occupancy leaves room for both rows and the frame.
            var first = new Vector2I(Mathf.FloorToInt((at.X - 28) / 8), Mathf.FloorToInt((at.Y - 18) / 8));
            var last = new Vector2I(Mathf.FloorToInt((at.X + 28) / 8), Mathf.FloorToInt((at.Y + 18) / 8));
            var compact = false;
            for (var y = first.Y; y <= last.Y && !compact; y++)
                for (var x = first.X; x <= last.X; x++)
                    if (_occupiedCounterCells.Contains(new Vector2I(x, y))) { compact = true; break; }
            if (!compact)
                for (var y = first.Y; y <= last.Y; y++)
                    for (var x = first.X; x <= last.X; x++)
                        _occupiedCounterCells.Add(new Vector2I(x, y));
            if (marker.Compact == compact) continue;
            marker.Compact = compact;
            marker.Plate.Scale = Vector3.One * (compact ? .16f : 1f);
            marker.Count.Visible = !compact;
        }
    }
}
