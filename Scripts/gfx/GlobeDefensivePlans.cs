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
                var side = _flatMap ? Vector3.Right : p.Normalized().Cross(Vector3.Up).Normalized();
                _defensiveSegmentScratch[0] = OnOrderSurface(p - side * .006f);
                _defensiveSegmentScratch.Add(OnOrderSurface(p + side * .006f));
            }
            if (_defensiveSegmentScratch.Count > 1)
            {
                PlanStroke(_defensiveSegmentScratch, .0022f, color, false);
                DrawDefensiveBastionTicks(_defensiveSegmentScratch, shieldAccent);
            }
            _defensiveSegmentScratch.Clear();
        }

        for (var i = 0; i < posts.Count; i++)
        {
            var p = posts[i];
            // Never draw an intact bridge over a captured/lost segment or an old disconnected brush.
            if (!DefensivePlanning.IsPost(_campaign, nationId, p)) { Flush(); previous = -1; continue; }
            if (previous > 0 && !_campaign.ProvinceMap.Neighbors[previous].Contains(p)) Flush();
            _defensiveSegmentScratch.Add(PlanPoint(p));
            previous = p;
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
            var dir = after - before;
            if (dir.LengthSquared() < 1e-8f) continue;
            dir = dir.Normalized();
            var normal = _flatMap ? Vector3.Forward : p.Normalized();
            var side = normal.Cross(dir).Normalized();
            const float span = 0.0052f;
            const float depth = 0.0046f;
            _defensiveBastionScratch.Clear();
            _defensiveBastionScratch.Add(OnOrderSurface(p - dir * span));
            _defensiveBastionScratch.Add(OnOrderSurface(p + side * depth));
            _defensiveBastionScratch.Add(OnOrderSurface(p + dir * span));
            PlanStroke(_defensiveBastionScratch, .0013f, color, false);
        }
    }
}
