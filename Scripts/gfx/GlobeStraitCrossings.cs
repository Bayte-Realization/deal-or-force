using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

/// <summary>
/// The straits an army can walk over, drawn as thin dashed links between the two shores.
///
/// Without a mark on the map a crossing is invisible until a division happens to use it:
/// nothing says Sicily is a march from Calabria while Sardinia is a voyage from anywhere.
/// So every crossing (Simulation.LandCrossings.Links) gets a short dashed line from the
/// centre of one shore's province to the centre of the other's - the same two points a
/// march over it goes between. A fixed link is drawn in pale stone and a ferry in pale
/// blue; one that an enemy fleet commands turns amber, and a ferry it has stopped, red.
///
/// The lines are rebuilt only when something they show has changed: who is looking, the
/// day, the projection, or the map's own crossings.
/// </summary>
public sealed partial class GlobePresentation
{
    private ImmediateMesh _straitCrossingMesh;
    private MeshInstance3D _straitCrossingPreview;
    private int _straitCrossingFingerprint = int.MinValue;
    private int _straitCrossingDay = int.MinValue;
    private int _straitCrossingViewer = int.MinValue;
    private int _straitCrossingRevision = int.MinValue;
    private readonly List<Simulation.CrossingLink> _straitCrossingLinks = new();
    private readonly List<Vector3> _straitCrossingPoints = new();

    // Still dashes, not flowing ones: a supply line moves because supply does, and a bridge
    // stands where it is.
    private const string StraitCrossingShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;
void fragment() {
    float dash = step(fract(UV.x), 0.58);
    float across = abs(UV.y * 2.0 - 1.0);
    float core = 1.0 - smoothstep(0.35, 0.95, across);
    ALBEDO = COLOR.rgb;
    ALPHA = COLOR.a * dash * core;
}";

    /// <summary>The legend line for this layer, in the player's language.</summary>
    public static string StraitCrossingLegend => Simulation.LandCrossings.Legend();

    /// <summary>
    /// What the crossings that start in a province are, for a tooltip on it: one sentence a
    /// crossing, or an empty string for a province with none.
    /// </summary>
    public string StraitCrossingTooltip(int province)
    {
        var map = _campaign?.ProvinceMap;
        if (map is null || !map.IsCrossingEnd(province)) return "";
        var text = "";
        foreach (var crossing in map.Crossings)
        {
            if (crossing.From != province && crossing.To != province) continue;
            var line = Simulation.LandCrossings.Describe(_campaign, crossing, _campaign.Player?.Id ?? -1);
            text = text.Length == 0 ? line : text + "\n" + line;
        }
        return text;
    }

    private void UpdateStraitCrossings()
    {
        var map = _campaign?.ProvinceMap;
        if (map is null || !IsInstanceValid(_routePreview)) return;
        if (!Simulation.WorldLayers.On("strait_crossings") || map.Crossings.Count == 0)
        {
            if (IsInstanceValid(_straitCrossingPreview)) _straitCrossingPreview.Visible = false;
            return;
        }
        if (_straitCrossingPreview is null || !IsInstanceValid(_straitCrossingPreview))
        {
            if (_routePreview.GetParent() is not Node3D world) return;
            _straitCrossingMesh = new ImmediateMesh();
            _straitCrossingPreview = new MeshInstance3D
            {
                Name = "StraitCrossings",
                Mesh = _straitCrossingMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new ShaderMaterial { Shader = new Shader { Code = StraitCrossingShader } },
                Visible = false
            };
            world.AddChild(_straitCrossingPreview);
            _straitCrossingFingerprint = int.MinValue;
            _straitCrossingDay = int.MinValue;
        }

        // Who commands a strait is asked of every fleet, so it is asked once a day and when
        // the viewer or the map changes - not on every frame the lines are merely looked at.
        var viewer = _campaign.Player?.Id ?? -1;
        if (_straitCrossingDay != _campaign.ElapsedDays || _straitCrossingViewer != viewer ||
            _straitCrossingRevision != map.AdjacencyRevision)
        {
            _straitCrossingDay = _campaign.ElapsedDays;
            _straitCrossingViewer = viewer;
            _straitCrossingRevision = map.AdjacencyRevision;
            Simulation.LandCrossings.Links(_campaign, viewer, _straitCrossingLinks);
        }

        var fingerprint = HashCode.Combine(viewer, _straitCrossingRevision, _straitCrossingLinks.Count,
            _flatMap, _flatMap ? (int)(_flatX * 4f) : 0);
        foreach (var link in _straitCrossingLinks)
            fingerprint = HashCode.Combine(fingerprint, link.FromProvince, link.ToProvince, link.Contested, link.Closed);
        if (fingerprint != _straitCrossingFingerprint)
        {
            _straitCrossingFingerprint = fingerprint;
            _straitCrossingMesh.ClearSurfaces();
            foreach (var link in _straitCrossingLinks) AppendStraitCrossing(link);
        }
        _straitCrossingPreview.Visible = MilitaryMarkersEnabled && _straitCrossingMesh.GetSurfaceCount() > 0;
    }

    private void AppendStraitCrossing(Simulation.CrossingLink link)
    {
        var colour = link.Closed
            ? new Color(0.96f, 0.34f, 0.28f, 0.92f)
            : link.Contested
                ? new Color(0.96f, 0.73f, 0.34f, 0.88f)
                : link.Kind == Simulation.LandCrossingKind.Ferry
                    ? new Color(0.66f, 0.84f, 0.96f, 0.80f)
                    : new Color(0.93f, 0.89f, 0.76f, 0.84f);

        // Walked a quarter of a degree at a time so the line lies on the globe; a crossing is
        // a few tens of kilometres, so this is a handful of points.
        _straitCrossingPoints.Clear();
        var deltaLongitude = link.ToLongitude - link.FromLongitude;
        while (deltaLongitude > 180f) deltaLongitude -= 360f;
        while (deltaLongitude < -180f) deltaLongitude += 360f;
        var steps = Math.Max(2, (int)MathF.Ceiling(MathF.Max(MathF.Abs(link.ToLatitude - link.FromLatitude),
            MathF.Abs(deltaLongitude)) * 4f));
        for (var step = 0; step <= steps; step++)
        {
            var t = step / (float)steps;
            _straitCrossingPoints.Add(PresentationPoint(link.FromLatitude + (link.ToLatitude - link.FromLatitude) * t,
                link.FromLongitude + deltaLongitude * t, SurfaceLineRadius));
        }

        // The same thin strip a supply line is, a little narrower; broken at the date line on
        // the flat map rather than streaked across it.
        const float halfWidth = 0.0019f;
        CollectDateLineRuns(_straitCrossingPoints);
        foreach (var run in _dateLineRuns)
        {
            _straitCrossingMesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
            var cumulative = 0f;
            var endIndex = run.Start + run.Count - 1;
            for (var index = run.Start; index <= endIndex; index++)
            {
                var point = _straitCrossingPoints[index];
                if (index > run.Start) cumulative += point.DistanceTo(_straitCrossingPoints[index - 1]) * 260f;
                var before = _straitCrossingPoints[Math.Max(run.Start, index - 1)];
                var after = _straitCrossingPoints[Math.Min(endIndex, index + 1)];
                var tangent = (after - before).Normalized();
                if (tangent == Vector3.Zero) tangent = Vector3.Right;
                var normal = _flatMap ? Vector3.Forward : point.Normalized();
                var side = normal.Cross(tangent).Normalized() * halfWidth;
                _straitCrossingMesh.SurfaceSetColor(colour);
                _straitCrossingMesh.SurfaceSetUV(new Vector2(cumulative, 0f));
                _straitCrossingMesh.SurfaceAddVertex(OnOrderSurface(point + side));
                _straitCrossingMesh.SurfaceSetColor(colour);
                _straitCrossingMesh.SurfaceSetUV(new Vector2(cumulative, 1f));
                _straitCrossingMesh.SurfaceAddVertex(OnOrderSurface(point - side));
            }
            _straitCrossingMesh.SurfaceEnd();
        }
    }
}
