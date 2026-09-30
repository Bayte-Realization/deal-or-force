using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly List<int> _objectivePreview = new();
    private readonly HashSet<int> _departurePreview = new();

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

    private void DrawOffensiveGeometry(IReadOnlyList<int> goals, IEnumerable<int> departure, bool preview)
    {
        if (goals.Count == 0) return;
        var points = goals.Select(PlanPoint).ToList();
        // A clicked province is a short destination bar, not a giant province outline.
        if (points.Count == 1)
        {
            var point = points[0];
            var side = _flatMap ? Vector3.Right : point.Normalized().Cross(Vector3.Up).Normalized();
            points[0] = OnOrderSurface(point - side * .012f);
            points.Add(OnOrderSurface(point + side * .012f));
        }
        var color = preview ? new Color(1f, .66f, .30f, .95f) : new Color(.95f, .29f, .20f, .95f);
        PlanStroke(points, .0017f, color, false);
        var end = PlanPoint(goals[goals.Count / 2]);
        var starts = departure.Select(PlanPoint).ToArray();
        if (starts.Length == 0) return;
        var alongGoal = (points[^1] - points[0]).Normalized();
        // Prefer approaching across the goal line, not running along and hiding inside it.
        var start = starts.OrderBy(p => p.DistanceSquaredTo(end) *
            (1f + 8f * MathF.Abs((end - p).Normalized().Dot(alongGoal)))).First();
        if (start.DistanceSquaredTo(end) < .0004f) return;
        // A single operational axis connects the source sector to the destination bar.
        // It is a plan symbol, not a substitute for the per-division province routes.
        var axis = new List<Vector3>();
        var middle = (start + end) * .5f;
        var normal = _flatMap ? Vector3.Forward : middle.Normalized();
        var bend = normal.Cross(end - start).Normalized() * MathF.Min(.025f, start.DistanceTo(end) * .1f);
        for (var i = 0; i <= 32; i++)
        {
            var t = i / 32f;
            axis.Add(OnOrderSurface(start.Lerp(middle + bend, t).Lerp((middle + bend).Lerp(end, t), t)));
        }
        PlanStroke(axis, .0030f, color, true);
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
        // Quiet charcoal keyline keeps the red ink readable on political/terrain colours.
        for (var pass = 0; pass < 2; pass++)
        {
            var paint = pass == 0 ? new Color(.10f, .09f, .07f, .65f) : color;
            var w = width * (pass == 0 ? 1.35f : 1f);
            _frontMesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
            for (var i = 0; i < _ribbonSamples.Count; i++)
            {
                var point = _ribbonSamples[i];
                var direction = (_ribbonSamples[Math.Min(i + 1, _ribbonSamples.Count - 1)] -
                    _ribbonSamples[Math.Max(i - 1, 0)]).Normalized();
                var normal = _flatMap ? Vector3.Forward : point.Normalized();
                var remaining = point.DistanceTo(_ribbonSamples[^1]);
                var half = arrow && remaining < .028f ? w * 3f * remaining / .028f : w;
                var side = normal.Cross(direction).Normalized() * half;
                _frontMesh.SurfaceSetColor(paint);
                _frontMesh.SurfaceAddVertex(OnOrderSurface(point + side));
                _frontMesh.SurfaceAddVertex(OnOrderSurface(point - side));
            }
            _frontMesh.SurfaceEnd();
        }
    }
}
