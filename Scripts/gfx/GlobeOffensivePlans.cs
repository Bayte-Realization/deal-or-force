using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

/// <summary>
/// High-contrast operational offensive plan arrows and destination bars.
/// Three-pass cartographic ink (charcoal keyline, vivid operational fill, luminous spine + flared arrowhead)
/// with reusable scratch buffers for zero per-rebuild LINQ allocations.
/// </summary>
public sealed partial class GlobePresentation
{
    private readonly List<int> _objectivePreview = new();
    private readonly HashSet<int> _departurePreview = new();
    private readonly List<Vector3> _offensiveGoalScratch = new();
    private readonly List<Vector3> _offensiveStartScratch = new();
    private readonly List<Vector3> _offensiveAxisScratch = new(34);

    public void SetOffensivePlanPreview(IEnumerable<int> goals, IEnumerable<int> departure)
    {
        _defensivePreview.Clear();
        _objectivePreview.Clear(); _objectivePreview.AddRange(goals);
        _departurePreview.Clear(); _departurePreview.UnionWith(departure);
        _planPreview.Clear(); _planPreview.UnionWith(_objectivePreview);
        _planPreviewKind = 1; _planPreviewForeignId = -1;
        BuildFrontPreview();
    }

    private bool TryPlanPoint(int province, out Vector3 point)
    {
        var map = _campaign?.ProvinceMap;
        if (map is null || !GlobeLineSanity.TryValidateProvinceId(province, map.ProvinceCount, map.ProvinceCenterCell))
        {
            point = default;
            return false;
        }
        var p = map.CenterOf(province);
        return TryPresentationPoint(p.Latitude, p.Longitude, SurfaceLineRadius, out point);
    }

    private void DrawOffensiveGeometry(IReadOnlyList<int> goals, IEnumerable<int> departure, bool preview, int phaseState = 1)
    {
        if (goals is null || goals.Count == 0 || departure is null) return;
        _offensiveGoalScratch.Clear();
        for (var i = 0; i < goals.Count; i++)
            if (TryPlanPoint(goals[i], out var goalPt)) _offensiveGoalScratch.Add(goalPt);
        if (_offensiveGoalScratch.Count == 0) return;

        var end = _offensiveGoalScratch[_offensiveGoalScratch.Count / 2];
        if (!GlobeLineSanity.IsValidEmittedVertex(ToNum(end), _flatMap, _flatX)) return;

        // A clicked province is a short destination bar, not a giant province outline.
        if (_offensiveGoalScratch.Count == 1)
        {
            var point = _offensiveGoalScratch[0];
            var side = _flatMap
                ? Vector3.Right
                : (GlobeLineSanity.TrySafeCrossAxis(ToNum(point), out var axis) ? ToGd(axis) : Vector3.Right);
            if (TryOnOrderSurface(point - side * .014f, SurfaceLineRadius, FlatLineZ, out var barA) &&
                TryOnOrderSurface(point + side * .014f, SurfaceLineRadius, FlatLineZ, out var barB) &&
                GlobeLineSanity.IsValidEmittedEdge(ToNum(barA), ToNum(barB), _flatMap, _flatX))
            {
                _offensiveGoalScratch[0] = barA;
                _offensiveGoalScratch.Add(barB);
            }
            else
            {
                _offensiveGoalScratch.Clear();
            }
        }

        var color = preview ? new Color(1f, .70f, .28f, .96f) : phaseState < 0
            ? new Color(.44f, .78f, .56f, .80f) : phaseState > 1
            ? new Color(.98f, .68f, .22f, .86f) : new Color(.98f, .26f, .18f, .96f);
        if (_offensiveGoalScratch.Count >= 2)
            PlanStroke(_offensiveGoalScratch, .0021f, color, false);

        _offensiveStartScratch.Clear();
        foreach (var dep in departure)
            if (TryPlanPoint(dep, out var depPt)) _offensiveStartScratch.Add(depPt);
        if (_offensiveStartScratch.Count == 0) return;

        var alongGoal = _offensiveGoalScratch.Count >= 2 &&
            GlobeLineSanity.TrySafeNormalize(ToNum(_offensiveGoalScratch[^1] - _offensiveGoalScratch[0]), out var alongNum)
            ? ToGd(alongNum)
            : Vector3.Right;
        // Prefer approaching across the goal line, not running along and hiding inside it.
        var start = _offensiveStartScratch[0];
        var bestScore = float.MaxValue;
        for (var i = 0; i < _offensiveStartScratch.Count; i++)
        {
            var rawCandidate = _offensiveStartScratch[i];
            var candidate = _flatMap
                ? rawCandidate with { X = DateLineSeam.NearestCopy(rawCandidate.X, end.X, FlatMapWidth) }
                : rawCandidate;
            if (!GlobeLineSanity.IsValidEmittedVertex(ToNum(candidate), _flatMap, _flatX)) continue;
            var d2 = candidate.DistanceSquaredTo(end);
            if (!float.IsFinite(d2)) continue;
            var dirDot = GlobeLineSanity.TrySafeNormalize(ToNum(end - candidate), out var dirNum)
                ? MathF.Abs(ToGd(dirNum).Dot(alongGoal))
                : 0f;
            var score = d2 * (1f + 8f * dirDot);
            if (score < bestScore) { bestScore = score; start = candidate; }
        }
        if (bestScore == float.MaxValue) return;
        var spanSq = start.DistanceSquaredTo(end);
        if (!float.IsFinite(spanSq) || spanSq < .0004f) return;

        // A single operational axis connects the source sector to the destination bar.
        _offensiveAxisScratch.Clear();
        Vector3 middle;
        if (_flatMap)
        {
            middle = (start + end) * .5f;
        }
        else if (GlobeLineSanity.TrySafeSphereQuadMidpoint(ToNum(start), ToNum(end), ToNum(start), SurfaceLineRadius, out var midNum))
        {
            middle = ToGd(midNum);
        }
        else
        {
            return;
        }
        var normal = _flatMap ? Vector3.Forward : middle.Normalized();
        var chordDist = start.DistanceTo(end);
        if (!float.IsFinite(chordDist)) return;
        var bend = GlobeLineSanity.TrySafeNormalize(ToNum(normal.Cross(end - start)), out var bendDirNum)
            ? ToGd(bendDirNum) * MathF.Min(.026f, chordDist * .11f)
            : Vector3.Zero;
        if (!TryOnOrderSurface(middle + bend, SurfaceLineRadius, FlatLineZ, out var apex) &&
            !TryOnOrderSurface(middle, SurfaceLineRadius, FlatLineZ, out apex))
            return;

        for (var i = 0; i <= 32; i++)
        {
            var t = i / 32f;
            var a = ToGd(GlobeLineSanity.InterpolateSurfacePoint(ToNum(start), ToNum(apex), t, _flatMap));
            var b = ToGd(GlobeLineSanity.InterpolateSurfacePoint(ToNum(apex), ToNum(end), t, _flatMap));
            var pt = ToGd(GlobeLineSanity.InterpolateSurfacePoint(ToNum(a), ToNum(b), t, _flatMap));
            if (TryOnOrderSurface(pt, SurfaceLineRadius, FlatLineZ, out var projected))
                _offensiveAxisScratch.Add(projected);
        }
        if (_offensiveAxisScratch.Count >= 2)
            PlanStroke(_offensiveAxisScratch, .0034f, color, true);
    }

    private void PlanStroke(IReadOnlyList<Vector3> points, float width, Color color, bool arrow)
    {
        if (points is null || points.Count < 2 || !float.IsFinite(width) || width <= 0f) return;
        var maxInputGap = _flatMap ? FlatMapWidth * 0.16f : GlobeLineSanity.GlobeRadius * 0.55f;
        CollectValidLineRuns(points, maxInputGap, _lineRunScratch);
        if (_lineRunScratch.Count == 0) return;

        // Three-pass cartographic operational stroke:
        // 0 = deep charcoal outer keyline for mountain/snow contrast,
        // 1 = vivid saturated operational body,
        // 2 = crisp luminous center spine on offensive arrows.
        var zoomScale = GlobeLineSanity.SafeClamp(BattleArrowUnitsPerPixel() / 0.00065f, 0.36f, 1.08f, 0.72f);
        var passes = arrow ? 3 : 2;
        var spineColor = color.Lightened(0.36f);
        spineColor.A = color.A * 0.78f;

        for (var r = 0; r < _lineRunScratch.Count; r++)
        {
            var run = _lineRunScratch[r];
            var runArrow = arrow && r == _lineRunScratch.Count - 1;
            _ribbonSamples.Clear();
            SampleRibbonTo(points[run.Start]);
            for (var i = run.Start + 1; i < run.Start + run.Count - 1; i++)
            {
                var corner = points[i];
                var enter = ToGd(GlobeLineSanity.InterpolateSurfacePoint(
                    ToNum(corner), ToNum(points[i - 1]), .18f, _flatMap));
                var leave = ToGd(GlobeLineSanity.InterpolateSurfacePoint(
                    ToNum(corner), ToNum(points[i + 1]), .18f, _flatMap));
                SampleRibbonTo(enter);
                for (var j = 1; j <= 5; j++)
                {
                    var t = j / 5f;
                    var a = ToGd(GlobeLineSanity.InterpolateSurfacePoint(ToNum(enter), ToNum(corner), t, _flatMap));
                    var b = ToGd(GlobeLineSanity.InterpolateSurfacePoint(ToNum(corner), ToNum(leave), t, _flatMap));
                    SampleRibbonTo(ToGd(GlobeLineSanity.InterpolateSurfacePoint(ToNum(a), ToNum(b), t, _flatMap)));
                }
            }
            SampleRibbonTo(points[run.Start + run.Count - 1]);
            if (_ribbonSamples.Count < 2) continue;

            if (_drawingPlanId >= 0)
            {
                for (var i = 1; i < _ribbonSamples.Count; i++)
                {
                    if (GlobeLineSanity.IsValidEmittedEdge(ToNum(_ribbonSamples[i - 1]), ToNum(_ribbonSamples[i]), _flatMap, _flatX))
                        _planHitSegments.Add((_drawingPlanId, _drawingPlanObjective, _ribbonSamples[i - 1], _ribbonSamples[i]));
                }
            }

            for (var pass = 0; pass < passes; pass++)
            {
                var paint = pass == 0 ? new Color(.06f, .055f, .05f, .82f) : pass == 1 ? color : spineColor;
                var w = width * zoomScale * (pass == 0 ? 1.48f : pass == 1 ? 1.0f : 0.34f);
                var headSpan = 0.030f * zoomScale;
                var prevPair = -1;
                for (var i = 0; i < _ribbonSamples.Count; i++)
                {
                    var point = _ribbonSamples[i];
                    var before = _ribbonSamples[Math.Max(0, i - 1)];
                    var after = _ribbonSamples[Math.Min(_ribbonSamples.Count - 1, i + 1)];
                    if (!GlobeLineSanity.TrySafeRibbonFrame(ToNum(point), ToNum(before), ToNum(after),
                            _flatMap, flatPositiveZNormal: false, out _, out _, out var sideAxis))
                    {
                        prevPair = -1;
                        continue;
                    }
                    var remaining = point.DistanceTo(_ribbonSamples[^1]);
                    if (!float.IsFinite(remaining))
                    {
                        prevPair = -1;
                        continue;
                    }
                    var half = runArrow && remaining < headSpan && headSpan > 1e-6f
                        ? w * (pass == 2 ? 1.5f : 2.5f) * remaining / headSpan
                        : w;
                    if (!float.IsFinite(half) || half < 0f)
                    {
                        prevPair = -1;
                        continue;
                    }
                    var side = ToGd(sideAxis) * half;
                    if (!TryOnOrderSurface(point + side, SurfaceLineRadius, FlatLineZ, out var left) ||
                        !TryOnOrderSurface(point - side, SurfaceLineRadius, FlatLineZ, out var right) ||
                        !GlobeLineSanity.IsValidEmittedEdge(ToNum(left), ToNum(right), _flatMap, _flatX))
                    {
                        prevPair = -1;
                        continue;
                    }
                    if (prevPair >= 0 &&
                        (!GlobeLineSanity.IsValidEmittedEdge(ToNum(_frontVerts[prevPair]), ToNum(left), _flatMap, _flatX) ||
                         !GlobeLineSanity.IsValidEmittedEdge(ToNum(_frontVerts[prevPair + 1]), ToNum(right), _flatMap, _flatX)))
                    {
                        prevPair = -1;
                    }
                    var pairBase = _frontVerts.Count;
                    _frontVerts.Add(left);
                    _frontColors.Add(paint);
                    _frontVerts.Add(right);
                    _frontColors.Add(paint);
                    if (prevPair >= 0)
                    {
                        _frontIndices.Add(prevPair);
                        _frontIndices.Add(prevPair + 1);
                        _frontIndices.Add(pairBase);
                        _frontIndices.Add(prevPair + 1);
                        _frontIndices.Add(pairBase + 1);
                        _frontIndices.Add(pairBase);
                    }
                    prevPair = pairBase;
                }
            }
        }
    }
}
