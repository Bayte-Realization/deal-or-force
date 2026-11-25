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

    private Vector3 PlanPoint(int province)
    {
        var p = _campaign.ProvinceMap.CenterOf(province);
        return PresentationPoint(p.Latitude, p.Longitude, SurfaceLineRadius);
    }

    private void DrawOffensiveGeometry(IReadOnlyList<int> goals, IEnumerable<int> departure, bool preview, int phaseState = 1)
    {
        if (goals.Count == 0) return;
        _offensiveGoalScratch.Clear();
        for (var i = 0; i < goals.Count; i++)
            _offensiveGoalScratch.Add(PlanPoint(goals[i]));

        // A clicked province is a short destination bar, not a giant province outline.
        if (_offensiveGoalScratch.Count == 1)
        {
            var point = _offensiveGoalScratch[0];
            var side = _flatMap ? Vector3.Right : point.Normalized().Cross(Vector3.Up).Normalized();
            _offensiveGoalScratch[0] = OnOrderSurface(point - side * .014f);
            _offensiveGoalScratch.Add(OnOrderSurface(point + side * .014f));
        }

        var color = preview ? new Color(1f, .70f, .28f, .96f) : phaseState < 0
            ? new Color(.44f, .78f, .56f, .80f) : phaseState > 1
            ? new Color(.98f, .68f, .22f, .86f) : new Color(.98f, .26f, .18f, .96f);
        PlanStroke(_offensiveGoalScratch, .0021f, color, false);

        var end = PlanPoint(goals[goals.Count / 2]);
        _offensiveStartScratch.Clear();
        foreach (var dep in departure) _offensiveStartScratch.Add(PlanPoint(dep));
        if (_offensiveStartScratch.Count == 0) return;

        var alongGoal = (_offensiveGoalScratch[^1] - _offensiveGoalScratch[0]).Normalized();
        // Prefer approaching across the goal line, not running along and hiding inside it.
        var start = _offensiveStartScratch[0];
        var bestScore = float.MaxValue;
        for (var i = 0; i < _offensiveStartScratch.Count; i++)
        {
            var candidate = _offensiveStartScratch[i];
            var d2 = candidate.DistanceSquaredTo(end);
            var score = d2 * (1f + 8f * MathF.Abs((end - candidate).Normalized().Dot(alongGoal)));
            if (score < bestScore) { bestScore = score; start = candidate; }
        }
        if (start.DistanceSquaredTo(end) < .0004f) return;

        // A single operational axis connects the source sector to the destination bar.
        _offensiveAxisScratch.Clear();
        var middle = (start + end) * .5f;
        var normal = _flatMap ? Vector3.Forward : middle.Normalized();
        var bend = normal.Cross(end - start).Normalized() * MathF.Min(.026f, start.DistanceTo(end) * .11f);
        for (var i = 0; i <= 32; i++)
        {
            var t = i / 32f;
            _offensiveAxisScratch.Add(OnOrderSurface(start.Lerp(middle + bend, t).Lerp((middle + bend).Lerp(end, t), t)));
        }
        PlanStroke(_offensiveAxisScratch, .0034f, color, true);
    }

    private void PlanStroke(IReadOnlyList<Vector3> points, float width, Color color, bool arrow)
    {
        _ribbonSamples.Clear(); SampleRibbonTo(points[0]);
        for (var i = 1; i < points.Count - 1; i++)
        {
            if (_flatMap && MathF.Abs(points[i].X - points[i - 1].X) > FlatMapWidth / 2) return;
            var corner = points[i]; var enter = corner.Lerp(points[i - 1], .18f); var leave = corner.Lerp(points[i + 1], .18f);
            SampleRibbonTo(enter);
            for (var j = 1; j <= 5; j++)
            {
                var t = j / 5f;
                SampleRibbonTo(enter.Lerp(corner, t).Lerp(corner.Lerp(leave, t), t));
            }
        }
        SampleRibbonTo(points[^1]);
        if (_drawingPlanId >= 0)
            for (var i = 1; i < _ribbonSamples.Count; i++)
                _planHitSegments.Add((_drawingPlanId, _drawingPlanObjective, _ribbonSamples[i - 1], _ribbonSamples[i]));

        // Three-pass cartographic operational stroke:
        // 0 = deep charcoal outer keyline for mountain/snow contrast,
        // 1 = vivid saturated operational body,
        // 2 = crisp luminous center spine on offensive arrows.
        var zoomScale = Math.Clamp(BattleArrowUnitsPerPixel() / 0.00065f, 0.36f, 1.08f);
        var passes = arrow ? 3 : 2;
        var spineColor = color.Lightened(0.36f);
        spineColor.A = color.A * 0.78f;
        for (var pass = 0; pass < passes; pass++)
        {
            var paint = pass == 0 ? new Color(.06f, .055f, .05f, .82f) : pass == 1 ? color : spineColor;
            var w = width * zoomScale * (pass == 0 ? 1.48f : pass == 1 ? 1.0f : 0.34f);
            var headSpan = 0.030f * zoomScale;
            var prevPair = -1;
            for (var i = 0; i < _ribbonSamples.Count; i++)
            {
                var point = _ribbonSamples[i];
                var direction = (_ribbonSamples[Math.Min(i + 1, _ribbonSamples.Count - 1)] -
                    _ribbonSamples[Math.Max(i - 1, 0)]).Normalized();
                var normal = _flatMap ? Vector3.Forward : point.Normalized();
                var remaining = point.DistanceTo(_ribbonSamples[^1]);
                var half = arrow && remaining < headSpan
                    ? w * (pass == 2 ? 1.5f : 2.5f) * remaining / headSpan
                    : w;
                var side = normal.Cross(direction).Normalized() * half;
                var pairBase = _frontVerts.Count;
                _frontVerts.Add(OnOrderSurface(point + side));
                _frontColors.Add(paint);
                _frontVerts.Add(OnOrderSurface(point - side));
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
