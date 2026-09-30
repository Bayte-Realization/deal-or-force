using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly List<int> _defensivePreview = new();

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
        var segment = new List<Vector3>();
        var previous = -1;
        var color = preview ? new Color(.75f, 1f, .64f) : new Color(.32f, .80f, .48f);
        void Flush()
        {
            if (segment.Count == 1)
            {
                var p = segment[0];
                var side = _flatMap ? Vector3.Right : p.Normalized().Cross(Vector3.Up).Normalized();
                segment[0] = OnOrderSurface(p - side * .005f);
                segment.Add(OnOrderSurface(p + side * .005f));
            }
            if (segment.Count > 1) PlanStroke(segment, .0018f, color, false);
            segment.Clear();
        }
        foreach (var p in posts)
        {
            // Never draw an intact bridge over a captured/lost segment or an old disconnected brush.
            if (!DefensivePlanning.IsPost(_campaign, nationId, p)) { Flush(); previous = -1; continue; }
            if (previous > 0 && !_campaign.ProvinceMap.Neighbors[previous].Contains(p)) Flush();
            segment.Add(PlanPoint(p)); previous = p;
        }
        Flush();
    }
}
