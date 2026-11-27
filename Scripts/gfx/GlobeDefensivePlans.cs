using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// High-contrast entrenched defensive plan lines and bastion shield chevrons.
/// Reusable scratch buffers eliminate per-rebuild list allocations.
/// </summary>
public sealed partial class GlobePresentation
{
    private readonly List<int> _defensivePreview = new();
    private readonly List<Vector3> _defensiveSegmentScratch = new();
    private readonly List<Vector3> _defensiveBastionScratch = new(3);

    public void SetDefensivePlanPreview(IEnumerable<int> posts)
    {
        _objectivePreview.Clear(); _departurePreview.Clear();
        _defensivePreview.Clear(); _defensivePreview.AddRange(posts);
        _planPreview.Clear(); _planPreview.UnionWith(_defensivePreview);
        _planPreviewKind = 2; _planPreviewForeignId = -1;
        BuildFrontPreview();
    }

    private void DrawDefensiveGeometry(IReadOnlyList<int> posts, int nationId, bool preview)
    {
        _defensiveSegmentScratch.Clear();
        var previous = -1;
        var color = preview ? new Color(.72f, 1f, .64f, .96f) : new Color(.28f, .86f, .54f, .94f);
        var shieldAccent = color.Lightened(0.24f);

        void Flush()
        {
            if (_defensiveSegmentScratch.Count == 1)
            {
                var p = _defensiveSegmentScratch[0];
                var side = _flatMap
                    ? Vector3.Right
                    : (GlobeLineSanity.TrySafeCrossAxis(ToNum(p), out var axis) ? ToGd(axis) : Vector3.Right);
                if (TryOnOrderSurface(p - side * .006f, SurfaceLineRadius, FlatLineZ, out var left) &&
                    TryOnOrderSurface(p + side * .006f, SurfaceLineRadius, FlatLineZ, out var right) &&
                    GlobeLineSanity.IsValidEmittedEdge(ToNum(left), ToNum(right), _flatMap, _flatX))
                {
                    _defensiveSegmentScratch[0] = left;
                    _defensiveSegmentScratch.Add(right);
                }
                else
                {
                    _defensiveSegmentScratch.Clear();
                }
            }
            if (_defensiveSegmentScratch.Count > 1)
            {
                PlanStroke(_defensiveSegmentScratch, .0022f, color, false);
                DrawDefensiveBastionTicks(_defensiveSegmentScratch, shieldAccent);
            }
            _defensiveSegmentScratch.Clear();
        }

        var map = _campaign?.ProvinceMap;
        if (map is null) return;
        for (var i = 0; i < posts.Count; i++)
        {
            var p = posts[i];
            if (!GlobeLineSanity.TryValidateProvinceId(p, map.ProvinceCount, map.ProvinceCenterCell))
            {
                Flush(); previous = -1; continue;
            }
            // Never draw an intact bridge over a captured/lost segment or an old disconnected brush.
            if (!DefensivePlanning.IsPost(_campaign, nationId, p)) { Flush(); previous = -1; continue; }
            if (previous > 0 && previous < map.Neighbors.Length && !map.Neighbors[previous].Contains(p)) Flush();
            if (TryPlanPoint(p, out var point))
            {
                _defensiveSegmentScratch.Add(point);
                previous = p;
            }
            else
            {
                Flush();
                previous = -1;
            }
        }
        Flush();
    }

    /// <summary>Draws crisp fortified bastion chevrons along each defensive line segment.</summary>
    private void DrawDefensiveBastionTicks(IReadOnlyList<Vector3> segment, Color color)
    {
        if (segment.Count < 2) return;
        var stride = Math.Max(1, segment.Count / 6);
        for (var i = 0; i < segment.Count; i += stride)
        {
            var p = segment[i];
            var before = segment[Math.Max(0, i - 1)];
            var after = segment[Math.Min(segment.Count - 1, i + 1)];
            if (_flatMap &&
                (MathF.Abs(p.X - before.X) > FlatMapWidth * 0.5f ||
                 MathF.Abs(after.X - p.X) > FlatMapWidth * 0.5f))
                continue;
            if (!GlobeLineSanity.TrySafeRibbonFrame(ToNum(p), ToNum(before), ToNum(after),
                    _flatMap, flatPositiveZNormal: false, out _, out var dirNum, out var sideNum))
                continue;
            var dir = ToGd(dirNum);
            var side = ToGd(sideNum);
            const float span = 0.0052f;
            const float depth = 0.0046f;
            if (!TryOnOrderSurface(p - dir * span, SurfaceLineRadius, FlatLineZ, out var p0) ||
                !TryOnOrderSurface(p + side * depth, SurfaceLineRadius, FlatLineZ, out var p1) ||
                !TryOnOrderSurface(p + dir * span, SurfaceLineRadius, FlatLineZ, out var p2))
                continue;
            if (_flatMap &&
                (MathF.Abs(p1.X - p0.X) > FlatMapWidth * 0.5f ||
                 MathF.Abs(p2.X - p1.X) > FlatMapWidth * 0.5f))
                continue;
            _defensiveBastionScratch.Clear();
            _defensiveBastionScratch.Add(p0);
            _defensiveBastionScratch.Add(p1);
            _defensiveBastionScratch.Add(p2);
            PlanStroke(_defensiveBastionScratch, .0013f, color, false);
        }
    }
}
