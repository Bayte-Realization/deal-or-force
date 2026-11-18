using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;
using SourceEarthData = DealOrForce.Simulation.WorldMapSource;

namespace DealOrForce;

/// <summary>
/// Mid-zoom regional cartographic typography and smooth zoom/fade state for country &amp; region labels.
/// Heavy geometric layout runs on worker threads (<see cref="TerritoryLabelLayout"/>); the main thread
/// reads only visible spatial buckets near the camera.
/// </summary>
public sealed partial class GlobePresentation
{
    private const int RegionTileCols = 12;
    private const int RegionTileRows = 6;
    private const int MaxActiveRegionLabels = 48;

    private readonly record struct RegionLabelCandidate(
        ScenarioState State,
        float Latitude,
        float Longitude,
        Vector3 Normal,
        Basis SurfaceBasis,
        float FlatRotation,
        float PixelSize,
        int Cells);

    private Node3D _regionLabelRoot = null!;
    private readonly List<Label3D> _regionLabelPool = new();
    private readonly List<RegionLabelCandidate>[] _regionBuckets = CreateRegionBuckets();
    private readonly Dictionary<int, float> _regionAlphas = new();
    private readonly Dictionary<int, float> _countryAlphas = new();
    private bool _regionBucketsReady;
    private float _regionLastAlpha = -1f;

    private static List<RegionLabelCandidate>[] CreateRegionBuckets()
    {
        var buckets = new List<RegionLabelCandidate>[RegionTileCols * RegionTileRows];
        for (var i = 0; i < buckets.Length; i++) buckets[i] = new List<RegionLabelCandidate>();
        return buckets;
    }

    private Color CountryFillColour(float alpha) => _mapLookAtlas
        ? new Color(Color.FromHtml(MapLook.ColourHex("map.look_label_fill")), alpha)
        : new Color(0.93f, 0.94f, 0.96f, alpha);

    private Color CountryOutlineColour(float alpha) => _mapLookAtlas
        ? new Color(Color.FromHtml(MapLook.ColourHex("map.look_label_outline")), alpha)
        : new Color(0.02f, 0.032f, 0.048f, alpha);

    private void CreateRegionLabelLayer(Node3D world)
    {
        _regionLabelRoot = new Node3D { Name = "RegionLabels", Visible = false };
        world.AddChild(_regionLabelRoot);
    }

    private void InvalidateCountryAndRegionLabelAppearance()
    {
        var fill = CountryFillColour(1f);
        var outline = CountryOutlineColour(1f);
        foreach (var label in _countryLabels)
        {
            if (!IsInstanceValid(label.Node)) continue;
            var alpha = _countryAlphas.TryGetValue(label.MapIndex, out var current) ? current : 0.88f;
            foreach (var child in label.Node.GetChildren())
            {
                if (child is not Label3D glyph) continue;
                glyph.Modulate = new Color(fill, alpha);
                glyph.OutlineModulate = new Color(outline, Math.Min(0.94f, alpha * 1.05f));
            }
        }
        _regionLastAlpha = -1f;
    }

    private void AdoptRegionLabelAnchors(Dictionary<int, TerritoryLabelLayout.TerritoryLabelAnchor> anchors)
    {
        foreach (var bucket in _regionBuckets) bucket.Clear();
        _regionAlphas.Clear();
        _regionBucketsReady = false;
        if (anchors is null || anchors.Count == 0 || _campaign is null) return;

        var stateByMapIndex = new Dictionary<int, ScenarioState>(_campaign.States.Count);
        foreach (var state in _campaign.States) stateByMapIndex[state.MapIndex] = state;

        var lonDegPerCell = 360f / SourceEarthData.Width;
        var latDegPerCell = 180f / SourceEarthData.Height;
        var fontSize = 36;

        foreach (var (mapIndex, anchor) in anchors)
        {
            if (anchor.Cells < 180 || !stateByMapIndex.TryGetValue(mapIndex, out var state)) continue;
            var lat = Mathf.Clamp(90f - (anchor.CenterY + 0.5f) * latDegPerCell, -84f, 84f);
            var lon = -180f + (WrapCell(anchor.CenterX, SourceEarthData.Width) + 0.5f) * lonDegPerCell;
            var normal = SurfacePoint(lat, lon, 1f).Normalized();
            var basis = CountryLabelSurfaceBasis(lat, lon, anchor.AxisX, anchor.AxisY);
            var flatRot = -Mathf.Atan2(anchor.AxisY, anchor.AxisX);

            var spanDeg = Mathf.Sqrt(
                Mathf.Pow(anchor.AxisX * anchor.HalfSpanPixels * lonDegPerCell * Mathf.Cos(Mathf.DegToRad(lat)), 2f) +
                Mathf.Pow(-anchor.AxisY * anchor.HalfSpanPixels * latDegPerCell, 2f));
            var worldSpan = GlobeRadius * Mathf.DegToRad(spanDeg) * 2f;
            var roomHeight = GlobeRadius * Mathf.DegToRad(anchor.ThicknessPixels * latDegPerCell) * 0.28f;
            var charEst = Math.Max(4, state.Name.Length);
            var height = Mathf.Clamp(Mathf.Min(roomHeight, worldSpan / charEst), 0.008f, 0.095f);
            var pixelSize = Mathf.Clamp(height / fontSize, 0.00014f, 0.0014f);

            var col = Math.Clamp((int)((lon + 180f) / 360f * RegionTileCols), 0, RegionTileCols - 1);
            var row = Math.Clamp((int)((90f - lat) / 180f * RegionTileRows), 0, RegionTileRows - 1);
            _regionBuckets[row * RegionTileCols + col].Add(new RegionLabelCandidate(
                state, lat, lon, normal, basis, flatRot, pixelSize, anchor.Cells));
        }

        // Keep top prominent regions per spatial tile so mid-zoom placement stays O(visible).
        foreach (var bucket in _regionBuckets)
        {
            if (bucket.Count <= 1) continue;
            bucket.Sort((a, b) => b.Cells.CompareTo(a.Cells));
            if (bucket.Count > 14) bucket.RemoveRange(14, bucket.Count - 14);
        }
        _regionBucketsReady = true;
    }

    private void UpdateRegionLabels(double delta)
    {
        if (!IsInstanceValid(_regionLabelRoot) || !IsInstanceValid(_camera)) return;
        var wanted = _mapLookAtlas && _regionBucketsReady && CountryLabelsEnabled &&
            Simulation.WorldLayers.On("country_labels") &&
            _mapMode is not (StrategicMapMode.Terrain or StrategicMapMode.Relief);

        var measure = _flatMap ? _flatZoom : _distance;
        var bandPeak = MapLabelPriority.RegionTargetAlpha(_mapLookAtlas, _flatMap, measure, 10000, true);
        if (!wanted || (bandPeak <= 0.005f && _regionLastAlpha <= 0.01f))
        {
            _regionLabelRoot.Visible = false;
            return;
        }

        var viewport = ViewportSize();
        var cameraNormal = _flatMap ? Vector3.Zero : _camera.GlobalPosition.Normalized();
        var rootBasis = _regionLabelRoot.GlobalTransform.Basis;
        var horizon = _flatMap ? -1f : Math.Max(0.28f, CityHorizon() + 0.06f);
        var font = PresentationFonts.For("map.cities");
        var fillBase = Color.FromHtml(MapLook.ColourHex("map.look_region_fill"));
        var outlineBase = Color.FromHtml(MapLook.ColourHex("map.look_label_outline"));
        var scale = _flatMap ? Math.Clamp(_flatZoom * 0.55f, 0.45f, 1.35f) : Math.Clamp(_distance / 9.0f, 0.72f, 1.25f);
        var stepDt = (float)Math.Max(0.016, delta);

        // Determine focus lat/lon so we scan only nearby spatial buckets.
        float focusLat = 0f, focusLon = 0f;
        if (_flatMap)
        {
            var camPos = _camera.GlobalPosition;
            focusLon = Mathf.Clamp(camPos.X / (GlobeRadius * 2f) * 360f, -180f, 180f);
            focusLat = Mathf.Clamp(camPos.Y / GlobeRadius * 90f, -89f, 89f);
        }
        else
        {
            var localCam = rootBasis.Inverse() * cameraNormal;
            var geo = GeographicFromNormal(localCam.Normalized());
            focusLat = geo.Latitude;
            focusLon = geo.Longitude;
        }

        var focusCol = Math.Clamp((int)((focusLon + 180f) / 360f * RegionTileCols), 0, RegionTileCols - 1);
        var focusRow = Math.Clamp((int)((90f - focusLat) / 180f * RegionTileRows), 0, RegionTileRows - 1);
        var colRadius = _flatMap && _flatZoom > 1.8f ? 3 : 2;
        var rowRadius = 2;

        var used = 0;
        var maxAlpha = 0f;
        for (var dr = -rowRadius; dr <= rowRadius && used < MaxActiveRegionLabels; dr++)
        {
            var r = focusRow + dr;
            if (r < 0 || r >= RegionTileRows) continue;
            for (var dc = -colRadius; dc <= colRadius && used < MaxActiveRegionLabels; dc++)
            {
                var c = ((focusCol + dc) % RegionTileCols + RegionTileCols) % RegionTileCols;
                var bucket = _regionBuckets[r * RegionTileCols + c];
                for (var i = 0; i < bucket.Count && used < MaxActiveRegionLabels; i++)
                {
                    var item = bucket[i];
                    var horizonVisible = _flatMap || (rootBasis * item.Normal).Dot(cameraNormal) > horizon;
                    var target = MapLabelPriority.RegionTargetAlpha(_mapLookAtlas, _flatMap, measure, item.Cells, horizonVisible);
                    var prev = _regionAlphas.TryGetValue(item.State.MapIndex, out var a) ? a : 0f;
                    var next = MapLabelPriority.StepOpacity(prev, target, stepDt);
                    _regionAlphas[item.State.MapIndex] = next;
                    if (next <= 0.01f) continue;

                    // Skip region names that match the sovereign state name (avoid double text in microstates).
                    var ownerMap = item.State.NationMapIndex;
                    if (_lastLabelNames.TryGetValue(ownerMap, out var countryName) &&
                        string.Equals(countryName, item.State.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var display = item.State.DisplayName;
                    if (string.IsNullOrWhiteSpace(display)) continue;

                    var pos = _flatMap
                        ? FlatPoint(item.Latitude, item.Longitude, FlatTextZ - 0.002f)
                        : SurfacePoint(item.Latitude, item.Longitude, SurfaceTextRadius - 0.002f);
                    var worldPos = _regionLabelRoot.GlobalTransform * pos;
                    if (_camera.IsPositionBehind(worldPos)) continue;
                    var screen = _camera.UnprojectPosition(worldPos);
                    if (screen.X < -60f || screen.Y < -30f || screen.X > viewport.X + 60f || screen.Y > viewport.Y + 30f) continue;

                    var label = RegionLabelAt(used++);
                    label.Text = display.ToUpperInvariant();
                    label.Font = font;
                    label.FontSize = 36;
                    label.OutlineSize = 6;
                    label.PixelSize = item.PixelSize;
                    label.Scale = Vector3.One * scale;
                    label.Position = pos;
                    if (_flatMap) label.Rotation = new Vector3(0f, 0f, item.FlatRotation);
                    else label.Basis = item.SurfaceBasis;
                    label.Modulate = new Color(fillBase, next);
                    label.OutlineModulate = new Color(outlineBase, Math.Min(0.85f, next * 1.15f));
                    label.Visible = true;
                    if (next > maxAlpha) maxAlpha = next;
                }
            }
        }
        for (var i = used; i < _regionLabelPool.Count; i++)
            if (_regionLabelPool[i].Visible) _regionLabelPool[i].Visible = false;
        _regionLastAlpha = maxAlpha;
        _regionLabelRoot.Visible = used > 0;
    }

    private Label3D RegionLabelAt(int slot)
    {
        while (_regionLabelPool.Count <= slot)
        {
            var label = new Label3D
            {
                Name = $"RegionLabel{_regionLabelPool.Count}",
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                FixedSize = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                NoDepthTest = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                RenderPriority = 0,
                OutlineRenderPriority = -1,
                Visible = false
            };
            _regionLabelRoot.AddChild(label);
            _regionLabelPool.Add(label);
        }
        return _regionLabelPool[slot];
    }
}
