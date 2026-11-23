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
    private readonly Dictionary<int, string> _regionUpperNames = new();
    private string _regionUpperLanguage = "";
    private readonly List<Rect2> _regionTaken = new();
    private bool _regionBucketsReady;
    private float _regionLastAlpha = -1f;
    private string _cachedCountryFillHex = "";
    private Color _cachedCountryFillColor = new(0.93f, 0.94f, 0.96f, 1f);
    private string _cachedCountryOutlineHex = "";
    private Color _cachedCountryOutlineColor = new(0.02f, 0.032f, 0.048f, 1f);
    private string _cachedRegionFillHex = "";
    private Color _cachedRegionFillColor = new(0.93f, 0.94f, 0.96f, 1f);

    private static List<RegionLabelCandidate>[] CreateRegionBuckets()
    {
        var buckets = new List<RegionLabelCandidate>[RegionTileCols * RegionTileRows];
        for (var i = 0; i < buckets.Length; i++) buckets[i] = new List<RegionLabelCandidate>();
        return buckets;
    }

    private Color AtlasColourCached(string key, ref string cachedHex, ref Color cachedColor)
    {
        var hex = MapLook.ColourHex(key);
        if (!string.Equals(hex, cachedHex, StringComparison.Ordinal))
        {
            cachedHex = hex;
            cachedColor = Color.FromHtml(hex);
        }
        return cachedColor;
    }

    private Color CountryFillColour(float alpha) => _mapLookAtlas
        ? new Color(AtlasColourCached("map.look_label_fill", ref _cachedCountryFillHex, ref _cachedCountryFillColor), alpha)
        : new Color(0.93f, 0.94f, 0.96f, alpha);

    private Color CountryOutlineColour(float alpha) => _mapLookAtlas
        ? new Color(AtlasColourCached("map.look_label_outline", ref _cachedCountryOutlineHex, ref _cachedCountryOutlineColor), alpha)
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
            if (_countryGroupGlyphs.TryGetValue(label.MapIndex, out var glyphs))
            {
                for (var i = 0; i < glyphs.Length; i++)
                {
                    var glyph = glyphs[i];
                    if (!IsInstanceValid(glyph)) continue;
                    glyph.Modulate = new Color(fill, alpha);
                    glyph.OutlineModulate = new Color(outline, Math.Min(0.94f, alpha * 1.05f));
                }
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
        var fillBase = AtlasColourCached("map.look_region_fill", ref _cachedRegionFillHex, ref _cachedRegionFillColor);
        var outlineBase = CountryOutlineColour(1f);
        var scale = _flatMap ? Math.Clamp(_flatZoom * 0.55f, 0.45f, 1.35f) : Math.Clamp(_distance / 9.0f, 0.72f, 1.25f);
        var stepDt = (float)Math.Max(0.016, delta);
        var rootTransform = _regionLabelRoot.GlobalTransform;
        var lang = Simulation.Loc.Language ?? "";
        if (!string.Equals(_regionUpperLanguage, lang, StringComparison.Ordinal))
        {
            _regionUpperLanguage = lang;
            _regionUpperNames.Clear();
        }

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
        _regionTaken.Clear();
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
                    var prev = _regionAlphas.TryGetValue(item.State.MapIndex, out var a) ? a : 0f;
                    if (!horizonVisible && prev <= 0.01f) continue;

                    var target = horizonVisible
                        ? MapLabelPriority.RegionTargetAlpha(_mapLookAtlas, _flatMap, measure, item.Cells, true)
                        : 0f;

                    string display = null;
                    if (target > 0.01f)
                    {
                        // Skip region names matching their country or an already-visible city pin (e.g. Saratov).
                        var ownerMap = item.State.NationMapIndex;
                        if (_lastLabelNames.TryGetValue(ownerMap, out var countryName) &&
                            string.Equals(countryName, item.State.Name, StringComparison.OrdinalIgnoreCase))
                            target = 0f;
                        else
                        {
                            display = item.State.DisplayName;
                            if (string.IsNullOrWhiteSpace(display) || _placedCityNames.Contains(display))
                                target = 0f;
                        }
                    }

                    if (target <= 0.01f && prev <= 0.01f)
                    {
                        if (prev > 0f) _regionAlphas[item.State.MapIndex] = 0f;
                        continue;
                    }

                    Vector3 pos = default;
                    var hasPos = false;
                    if (target > 0.01f)
                    {
                        var regionBias = _reliefDisplacement > 0.0001f ? 0.0015f : 0f;
                        pos = _flatMap
                            ? LocalFlatPoint(item.Latitude, item.Longitude, FlatTextZ - 0.002f + regionBias)
                            : LocalSurfacePoint(item.Latitude, item.Longitude, SurfaceTextRadius - 0.002f + regionBias);
                        hasPos = true;
                        var worldPos = rootTransform * pos;
                        if (_camera.IsPositionBehind(worldPos)) target = 0f;
                        else
                        {
                            var screen = _camera.UnprojectPosition(worldPos);
                            if (screen.X < -60f || screen.Y < -30f || screen.X > viewport.X + 60f || screen.Y > viewport.Y + 30f)
                                target = 0f;
                        }
                    }

                    string upperDisplay = null;
                    Rect2 screenBox = default;
                    if (target > 0.01f)
                    {
                        if (!_regionUpperNames.TryGetValue(item.State.MapIndex, out upperDisplay))
                        {
                            upperDisplay = display!.ToUpperInvariant();
                            _regionUpperNames[item.State.MapIndex] = upperDisplay;
                        }
                        screenBox = RegionScreenBox(item, upperDisplay, font, scale, pos);
                        if (RegionCollidesWithHigherPriority(screenBox))
                            target = 0f;
                    }

                    var next = horizonVisible ? MapLabelPriority.StepOpacity(prev, target, stepDt) : 0f;
                    _regionAlphas[item.State.MapIndex] = next;
                    if (next <= 0.01f) continue;

                    if (!hasPos)
                    {
                        var regionBias = _reliefDisplacement > 0.0001f ? 0.0015f : 0f;
                        pos = _flatMap
                            ? LocalFlatPoint(item.Latitude, item.Longitude, FlatTextZ - 0.002f + regionBias)
                            : LocalSurfacePoint(item.Latitude, item.Longitude, SurfaceTextRadius - 0.002f + regionBias);
                    }
                    if (upperDisplay is null && !_regionUpperNames.TryGetValue(item.State.MapIndex, out upperDisplay))
                    {
                        display ??= item.State.DisplayName;
                        upperDisplay = string.IsNullOrWhiteSpace(display) ? "" : display.ToUpperInvariant();
                        _regionUpperNames[item.State.MapIndex] = upperDisplay;
                    }
                    if (target > 0.01f) _regionTaken.Add(screenBox);
                    var label = RegionLabelAt(used++);
                    if (!string.Equals(label.Text, upperDisplay, StringComparison.Ordinal))
                        label.Text = upperDisplay ?? "";
                    if (!ReferenceEquals(label.Font, font)) label.Font = font;
                    label.FontSize = 36;
                    label.OutlineSize = 6;
                    label.PixelSize = item.PixelSize;
                    label.Scale = Vector3.One * scale;
                    label.Position = pos;
                    if (_flatMap) label.Rotation = new Vector3(0f, 0f, item.FlatRotation);
                    else label.Basis = item.SurfaceBasis;
                    label.Modulate = new Color(fillBase, next);
                    label.OutlineModulate = new Color(outlineBase, Math.Min(0.85f, next * 1.15f));
                    if (!label.Visible) label.Visible = true;
                    if (next > maxAlpha) maxAlpha = next;
                }
            }
        }
        for (var i = used; i < _regionLabelPool.Count; i++)
            if (_regionLabelPool[i].Visible) _regionLabelPool[i].Visible = false;
        _regionLastAlpha = maxAlpha;
        _regionLabelRoot.Visible = used > 0;
    }

    private Rect2 RegionScreenBox(RegionLabelCandidate item, string upperDisplay, Font font, float scale, Vector3 localPos)
    {
        var halfW = CityTextWidth(font, 36, upperDisplay) * item.PixelSize * scale * 0.5f;
        var halfH = 36f * item.PixelSize * scale * 0.62f;
        Vector3 right;
        Vector3 up;
        if (_flatMap)
        {
            var cos = Mathf.Cos(item.FlatRotation);
            var sin = Mathf.Sin(item.FlatRotation);
            right = new Vector3(cos, sin, 0f) * halfW;
            up = new Vector3(-sin, cos, 0f) * halfH;
        }
        else
        {
            right = item.SurfaceBasis.X * halfW;
            up = item.SurfaceBasis.Y * halfH;
        }
        var xform = _regionLabelRoot.GlobalTransform;
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        for (var sx = -1; sx <= 1; sx += 2)
        for (var sy = -1; sy <= 1; sy += 2)
        {
            var corner = xform * (localPos + right * sx + up * sy);
            if (_camera.IsPositionBehind(corner)) return new Rect2(-10000f, -10000f, 1f, 1f);
            var pt = _camera.UnprojectPosition(corner);
            min = min.Min(pt);
            max = max.Max(pt);
        }
        return new Rect2(min, max - min).Grow(4f);
    }

    private bool RegionCollidesWithHigherPriority(Rect2 box)
    {
        foreach (var taken in _cityTaken)
            if (taken.Intersects(box)) return true;
        foreach (var (star, _) in _starMarks)
            if (box.Intersects(new Rect2(star.X - 8f, star.Y - 8f, 16f, 16f))) return true;
        foreach (var taken in _regionTaken)
            if (taken.Intersects(box)) return true;
        return false;
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
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
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
