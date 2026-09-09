using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly List<(ArmyStackMarker Marker, Vector2 Screen, bool Selected)> _densityCandidates = new();
    private readonly HashSet<Vector2I> _occupiedCounterCells = new();
    private ulong _nextCounterDensityMs;
    private Vector3 _densityView = new(float.NaN, float.NaN, float.NaN);

    /// <summary>
    /// Which counters are readable and which are reduced to a pin, when they crowd.
    ///
    /// Only presentation is simplified: no province buckets or simulation divisions are
    /// merged. The small marks stay at the real positions and stay pickable.
    ///
    /// Two things were wrong with it, both measured on a live map before being touched.
    ///
    /// It flickered. The pass ran ten times a second and decided from screen positions, and
    /// screen positions keep moving for a good fraction of a second after every input while
    /// the camera lerps towards where it was told to go - so borderline counters crossed the
    /// occupancy threshold back and forth. The log said `compact=19->19 flipped=2` on tick
    /// after tick with nobody touching the mouse. So the pass now does nothing at all unless
    /// the view has actually moved, and the decision has hysteresis: a readable counter keeps
    /// its place unless something lands well inside its footprint, and a pin becomes readable
    /// as soon as its own footprint is clear. Both tests lean the same way - towards
    /// readable - so there is no state a counter can oscillate between.
    ///
    /// And too much of the map went quiet. Nineteen or twenty counters out of twenty-seven
    /// were pins, at 0.16 of full size with their strength hidden, which is a speck: what the
    /// player saw was a front with a handful of markers on it where the globe had shown all
    /// twenty-seven. A pin is meant to say "a formation is here, look closer", not to
    /// disappear, so it is a little under half size now and still reads as a counter.
    /// </summary>
    private void UpdateFlatCounterDensity()
    {
        // Nothing moved far enough to crowd differently, so nothing is re-decided. This is
        // what stops the flicker: the pass used to re-run ten times a second off a camera
        // that was still settling, and a few pixels of drift is all a borderline counter
        // needs to change its mind. The threshold is a few screen pixels, which is less than
        // half a cell of the grid below - smaller than that and no decision can differ.
        var now = Time.GetTicksMsec();
        var unitsPerPixel = _flatZoom / Math.Max(1f, Size.Y);
        var moved = Mathf.Abs(_flatX - _densityView.X) + Mathf.Abs(_flatY - _densityView.Y);
        var zoomed = Mathf.Abs(_flatZoom - _densityView.Z);
        if (moved < unitsPerPixel * 3f && zoomed < _flatZoom * 0.0015f) return;

        // And while it is moving, twenty times a second rather than every frame: fourteen
        // hundred markers unprojected and gridded is not free, and a decision the eye cannot
        // catch up with is not worth making faster.
        if (now < _nextCounterDensityMs) return;
        _nextCounterDensityMs = now + 50;
        _densityView = new Vector3(_flatX, _flatY, _flatZoom);
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
            // A counter's own footprint, and the smaller box a counter that is already
            // readable is judged against. Leaning the test towards whichever state the
            // counter is in would make it oscillate; leaning both tests towards readable
            // gives a counter that has to be genuinely covered before it gives up its place.
            var margin = marker.Compact ? new Vector2(28, 18) : new Vector2(17, 11);
            var first = new Vector2I(Mathf.FloorToInt((at.X - margin.X) / 8), Mathf.FloorToInt((at.Y - margin.Y) / 8));
            var last = new Vector2I(Mathf.FloorToInt((at.X + margin.X) / 8), Mathf.FloorToInt((at.Y + margin.Y) / 8));
            var compact = false;
            for (var y = first.Y; y <= last.Y && !compact; y++)
                for (var x = first.X; x <= last.X; x++)
                    if (_occupiedCounterCells.Contains(new Vector2I(x, y))) { compact = true; break; }

            // A pin has to fit too, or it is not shown.
            //
            // Pins used not to claim any room at all, on the reasoning that they mark real
            // positions and should stay where the formations are. At close range that is
            // right. Zoomed out to a continent it produced a solid unreadable clump over
            // Korea - twenty pins on top of one another, which says exactly as much as one
            // pin and hides the counter underneath. So a pin claims its own small footprint,
            // and one that cannot have even that is not drawn. Nothing is un-clicked by
            // this: the marker is still there and still picks, which is what matters when
            // the player goes looking.
            var hidden = false;
            if (compact)
            {
                var pinFirst = new Vector2I(Mathf.FloorToInt((at.X - 12) / 8), Mathf.FloorToInt((at.Y - 8) / 8));
                var pinLast = new Vector2I(Mathf.FloorToInt((at.X + 12) / 8), Mathf.FloorToInt((at.Y + 8) / 8));
                for (var y = pinFirst.Y; y <= pinLast.Y && !hidden; y++)
                    for (var x = pinFirst.X; x <= pinLast.X; x++)
                        if (_occupiedCounterCells.Contains(new Vector2I(x, y))) { hidden = true; break; }
                if (!hidden)
                    for (var y = pinFirst.Y; y <= pinLast.Y; y++)
                        for (var x = pinFirst.X; x <= pinLast.X; x++)
                            _occupiedCounterCells.Add(new Vector2I(x, y));
            }

            // What a readable counter claims is the whole counter, so nothing lands on it.
            if (!compact)
                for (var y = Mathf.FloorToInt((at.Y - 18) / 8); y <= Mathf.FloorToInt((at.Y + 18) / 8); y++)
                    for (var x = Mathf.FloorToInt((at.X - 28) / 8); x <= Mathf.FloorToInt((at.X + 28) / 8); x++)
                        _occupiedCounterCells.Add(new Vector2I(x, y));
            if (marker.Compact == compact && marker.Plate.Visible == !hidden) continue;
            marker.Compact = compact;
            marker.Plate.Visible = !hidden;
            marker.Plate.Scale = Vector3.One * (compact ? .44f : 1f);
            marker.Count.Visible = !compact && !hidden;
        }
    }
}
