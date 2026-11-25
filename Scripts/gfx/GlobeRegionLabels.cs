using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;
using RBush;
using SourceEarthData = DealOrForce.Simulation.WorldMapSource;

namespace DealOrForce;

/// <summary>
/// Mid-zoom regional cartographic typography and smooth zoom/fade state for country &amp; region labels.
/// Heavy geometric layout runs on worker threads (<see cref="TerritoryLabelLayout"/>); the main thread
/// queries an RBush spatial R-tree over viewport bounds with anti-meridian split and frustum-bucket caching.
/// </summary>
public sealed partial class GlobePresentation
{
    private const int MaxActiveRegionLabels = 48;

    private sealed class RegionLabelCandidate
    {
        public readonly ScenarioState State;
        public readonly float Latitude;
        public readonly float Longitude;
        public readonly Vector3 Normal;
        public readonly Basis SurfaceBasis;
        public readonly float FlatRotation;
        public readonly float PixelSize;
        public readonly float Height;
        public readonly int Cells;
        public Vector3 GlobePos;
        public Vector3 FlatPos;
        public float GlobeRelief = -1f;
        public byte[] GlobeReliefGrid;
        public float FlatRelief = -1f;
        public byte[] FlatReliefGrid;

        public RegionLabelCandidate(
            ScenarioState state,
            float latitude,
            float longitude,
            Vector3 normal,
            Basis surfaceBasis,
            float flatRotation,
            float pixelSize,
            float height,
            int cells)
        {
            State = state;
            Latitude = latitude;
            Longitude = longitude;
            Normal = normal;
            SurfaceBasis = surfaceBasis;
            FlatRotation = flatRotation;
            PixelSize = pixelSize;
            Height = height;
            Cells = cells;
        }
    }

    private sealed class RegionLabelSlotState
    {
        public string Text = "";
        public float Alpha = -1f;
        public string FillHex = "";
        public string OutlineHex = "";
        public float Scale = -1f;
        public float PixelSize = -1f;
        public int FontSize = -1;
        public int OutlineSize = -1;
        public Font Font;
        public Vector3 Normal;
        public bool Visible;
        public Vector3 Position;
        public bool HasPosition;
        public float FlatRotation;
        public bool HasFlatRotation;
        public Basis SurfaceBasis;
        public bool HasSurfaceBasis;
    }

    private Node3D _regionLabelRoot = null!;
    private readonly List<Label3D> _regionLabelPool = new();
    private readonly List<RegionLabelSlotState> _regionSlotStates = new();
    private int _regionLastUsed;
    private float _regionLastFlatX = float.NaN;
    private float _regionLastFlatY = float.NaN;
    private float _regionLastMeasure = -1f;
    private Vector3 _regionLastCamPos;
    private StrategicMapMode _regionLastMapMode = (StrategicMapMode)(-1);
    private bool _regionLastFlatMap;
    private Vector2I _regionLastViewport;
    private int _regionLastCityLayoutVersion = -1;
    private bool _regionVisSettled;
    private RBush<SpatialGeoItem<RegionLabelCandidate>> _regionRTree;
    private int _regionRTreeVersion;
    private readonly List<SpatialGeoItem<RegionLabelCandidate>> _regionBuildScratch = new();
    private readonly List<RegionLabelCandidate> _regionQueryScratch = new();
    private readonly List<RegionLabelCandidate> _regionFrustumCandidates = new();
    private (bool Flat, int LatBucket, int LonBucket, int SpanBucket, int TreeVersion) _regionFrustumKey;
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
        _countryAppliedAlpha.Clear();
        _countryVisAllSettled = false;
        _countryVisNeedsInitialSync = true;
        _regionLastAlpha = -1f;
        _regionVisSettled = false;
        for (var i = 0; i < _regionSlotStates.Count; i++)
        {
            _regionSlotStates[i].Alpha = -1f;
            _regionSlotStates[i].FillHex = "";
            _regionSlotStates[i].OutlineHex = "";
        }
    }

    private void AdoptRegionLabelAnchors(Dictionary<int, TerritoryLabelLayout.TerritoryLabelAnchor> anchors)
    {
        _regionBuildScratch.Clear();
        _regionAlphas.Clear();
        _regionFrustumCandidates.Clear();
        _regionBucketsReady = false;
        _regionLastUsed = 0;
        _regionLastAlpha = -1f;
        _regionVisSettled = false;
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

            var candidate = new RegionLabelCandidate(
                state, lat, lon, normal, basis, flatRot, pixelSize, height, anchor.Cells);
            _regionBuildScratch.Add(new SpatialGeoItem<RegionLabelCandidate>(candidate, lat, lon));
        }

        _regionRTree = new RBush<SpatialGeoItem<RegionLabelCandidate>>();
        if (_regionBuildScratch.Count > 0)
            _regionRTree.BulkLoad(_regionBuildScratch);
        _regionBuildScratch.Clear();
        _regionRTreeVersion++;
        _regionFrustumKey = default;
        _regionBucketsReady = _regionRTree.Count > 0;
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
            if (_regionLabelRoot.Visible) _regionLabelRoot.Visible = false;
            _regionVisSettled = false;
            return;
        }

        var viewport = ViewportSize();
        var camPos = _camera.GlobalPosition;
        var measureTol = _flatMap ? 0.004f : 0.02f;
        var camStable = _flatMap
            ? (float.IsFinite(_regionLastFlatX) && float.IsFinite(_regionLastFlatY) &&
               MathF.Abs(_flatX - _regionLastFlatX) <= 0.02f && MathF.Abs(_flatY - _regionLastFlatY) <= 0.02f)
            : camPos.DistanceSquaredTo(_regionLastCamPos) <= 0.0001f;
        if (_regionVisSettled &&
            _regionLastFlatMap == _flatMap &&
            _regionLastMapMode == _mapMode &&
            _regionLastViewport == viewport &&
            _regionLastCityLayoutVersion == _cityLayoutVersion &&
            MathF.Abs(measure - _regionLastMeasure) <= measureTol &&
            camStable)
            return;

        var cameraNormal = _flatMap ? Vector3.Zero : camPos.Normalized();
        var rootBasis = _regionLabelRoot.GlobalTransform.Basis;
        var horizon = _flatMap ? -1f : Math.Max(0.28f, CityHorizon() + 0.06f);
        var tier = CurrentShaderQualityTier();
        var mobileTier = tier >= 0.5f;
        var inMotion = _flatMap ? FlatViewInMotion : GlobeViewInMotion;
        if (mobileTier && inMotion && _regionLastUsed > 0 && _regionLastAlpha > 0.01f &&
            (!_flatMap || (float.IsFinite(_regionLastFlatX) && MathF.Abs(_flatX - _regionLastFlatX) < FlatMapWidth * 0.25f)))
        {
            // Keep existing mobile region labels steady while panning/zooming instead of
            // reshuffling pool slots and triggering Label3D Text/Modulate mesh rebuilds.
            if (!_flatMap)
            {
                var anyVisible = false;
                for (var i = 0; i < _regionLastUsed && i < _regionLabelPool.Count; i++)
                {
                    var slotState = _regionSlotStates[i];
                    if (!slotState.Visible) continue;
                    if ((rootBasis * slotState.Normal).Dot(cameraNormal) <= horizon)
                    {
                        slotState.Visible = false;
                        _regionLabelPool[i].Visible = false;
                    }
                    else
                        anyVisible = true;
                }
                if (_regionLabelRoot.Visible != anyVisible) _regionLabelRoot.Visible = anyVisible;
            }
            _regionVisSettled = false;
            return;
        }

        var font = PresentationFonts.For("map.cities");
        var fillBase = AtlasColourCached("map.look_region_fill", ref _cachedRegionFillHex, ref _cachedRegionFillColor);
        var outlineBase = CountryOutlineColour(1f);
        var fillHex = _mapLookAtlas ? _cachedRegionFillHex : "";
        var outlineHex = _mapLookAtlas ? _cachedCountryOutlineHex : "";
        var modulateThreshold = mobileTier ? 0.06f : 0.01f;
        var scale = _flatMap ? Math.Clamp(_flatZoom * 0.55f, 0.45f, 1.35f) : Math.Clamp(_distance / 9.0f, 0.72f, 1.25f);
        var stepDt = (float)Math.Max(0.016, delta);
        var rootTransform = _regionLabelRoot.GlobalTransform;
        var lang = Simulation.Loc.Language ?? "";
        if (!string.Equals(_regionUpperLanguage, lang, StringComparison.Ordinal))
        {
            _regionUpperLanguage = lang;
            _regionUpperNames.Clear();
        }

        ComputeGeoFrustumBounds(viewport, rootBasis, 6f,
            out var rMinLat, out var rMaxLat, out var rMinLon, out var rMaxLon, out var rFullLon,
            out var rLatB, out var rLonB, out var rSpanB);
        var regionKey = (_flatMap, rLatB, rLonB, rSpanB, _regionRTreeVersion);
        if (regionKey != _regionFrustumKey)
        {
            _regionFrustumKey = regionKey;
            SearchGeoRTree(_regionRTree, _regionQueryScratch, rMinLat, rMaxLat, rMinLon, rMaxLon, rFullLon);
            _regionFrustumCandidates.Clear();
            _regionFrustumCandidates.AddRange(_regionQueryScratch);
            _regionFrustumCandidates.Sort(static (a, b) => b.Cells.CompareTo(a.Cells));
        }

        var used = 0;
        var maxAlpha = 0f;
        var allSettled = true;
        _regionTaken.Clear();
        for (var i = 0; i < _regionFrustumCandidates.Count && used < MaxActiveRegionLabels; i++)
        {
            var item = _regionFrustumCandidates[i];
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
                        pos = RegionLabelPosition(item);
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
                        screenBox = RegionScreenBox(item, upperDisplay, font, scale, pos, in rootTransform);
                        if (RegionCollidesWithHigherPriority(screenBox))
                            target = 0f;
                    }

                    var next = horizonVisible ? MapLabelPriority.StepOpacity(prev, target, stepDt) : 0f;
                    _regionAlphas[item.State.MapIndex] = next;
                    if (horizonVisible && MathF.Abs(next - target) > 0.002f) allSettled = false;
                    if (next <= 0.01f) continue;

                    if (!hasPos)
                        pos = RegionLabelPosition(item);
                    if (upperDisplay is null && !_regionUpperNames.TryGetValue(item.State.MapIndex, out upperDisplay))
                    {
                        display ??= item.State.DisplayName;
                        upperDisplay = string.IsNullOrWhiteSpace(display) ? "" : display.ToUpperInvariant();
                        _regionUpperNames[item.State.MapIndex] = upperDisplay;
                    }
                    if (target > 0.01f) _regionTaken.Add(screenBox);
                    var slot = used++;
                    var label = RegionLabelAt(slot);
                    var slotState = _regionSlotStates[slot];
                    var wasVisible = slotState.Visible;
                    var textValue = upperDisplay ?? "";
                    if (!string.Equals(slotState.Text, textValue, StringComparison.Ordinal))
                    {
                        slotState.Text = textValue;
                        label.Text = textValue;
                    }
                    if (!ReferenceEquals(slotState.Font, font))
                    {
                        slotState.Font = font;
                        label.Font = font;
                    }
                    if (slotState.FontSize != 36)
                    {
                        slotState.FontSize = 36;
                        label.FontSize = 36;
                    }
                    if (slotState.OutlineSize != 6)
                    {
                        slotState.OutlineSize = 6;
                        label.OutlineSize = 6;
                    }
                    if (!Mathf.IsEqualApprox(slotState.PixelSize, item.PixelSize))
                    {
                        slotState.PixelSize = item.PixelSize;
                        label.PixelSize = item.PixelSize;
                    }
                    if (MathF.Abs(scale - slotState.Scale) > 0.005f)
                    {
                        slotState.Scale = scale;
                        label.Scale = Vector3.One * scale;
                    }
                    if (!slotState.HasPosition || slotState.Position != pos)
                    {
                        slotState.Position = pos;
                        slotState.HasPosition = true;
                        label.Position = pos;
                    }
                    if (_flatMap)
                    {
                        if (!slotState.HasFlatRotation || slotState.FlatRotation != item.FlatRotation)
                        {
                            slotState.FlatRotation = item.FlatRotation;
                            slotState.HasFlatRotation = true;
                            slotState.HasSurfaceBasis = false;
                            label.Rotation = new Vector3(0f, 0f, item.FlatRotation);
                        }
                    }
                    else
                    {
                        if (!slotState.HasSurfaceBasis || slotState.SurfaceBasis != item.SurfaceBasis)
                        {
                            slotState.SurfaceBasis = item.SurfaceBasis;
                            slotState.HasSurfaceBasis = true;
                            slotState.HasFlatRotation = false;
                            label.Basis = item.SurfaceBasis;
                        }
                    }
                    slotState.Normal = item.Normal;
                    var atEndpoint = next <= 0.015f || MathF.Abs(next - target) <= 0.005f;
                    var colorChanged = !string.Equals(slotState.FillHex, fillHex, StringComparison.Ordinal) ||
                                       !string.Equals(slotState.OutlineHex, outlineHex, StringComparison.Ordinal);
                    if (!wasVisible || colorChanged || slotState.Alpha < 0f ||
                        (atEndpoint && MathF.Abs(next - slotState.Alpha) > 0.002f) ||
                        MathF.Abs(next - slotState.Alpha) >= modulateThreshold)
                    {
                        label.Modulate = fillBase with { A = next };
                        label.OutlineModulate = outlineBase with { A = Math.Min(0.85f, next * 1.15f) };
                        slotState.Alpha = next;
                        slotState.FillHex = fillHex;
                        slotState.OutlineHex = outlineHex;
                    }
                    if (!wasVisible)
                    {
                        slotState.Visible = true;
                        label.Visible = true;
                    }
                    if (next > maxAlpha) maxAlpha = next;
        }
        for (var i = used; i < _regionLabelPool.Count; i++)
        {
            if (_regionSlotStates[i].Visible)
            {
                _regionSlotStates[i].Visible = false;
                _regionLabelPool[i].Visible = false;
            }
        }
        _regionLastUsed = used;
        _regionLastFlatX = _flatX;
        _regionLastFlatY = _flatY;
        _regionLastMeasure = measure;
        _regionLastCamPos = camPos;
        _regionLastMapMode = _mapMode;
        _regionLastFlatMap = _flatMap;
        _regionLastViewport = viewport;
        _regionLastCityLayoutVersion = _cityLayoutVersion;
        _regionVisSettled = allSettled;
        _regionLastAlpha = maxAlpha;
        var showRoot = used > 0;
        if (_regionLabelRoot.Visible != showRoot) _regionLabelRoot.Visible = showRoot;
    }

    private Vector3 RegionLabelPosition(RegionLabelCandidate item)
    {
        var reliefBias = _reliefDisplacement > 0.0001f ? 0.005f : 0f;
        var footprint = item.Height * 1.6f;
        if (_flatMap)
        {
            if (!Mathf.IsEqualApprox(item.FlatRelief, _reliefDisplacement) ||
                !ReferenceEquals(item.FlatReliefGrid, _reliefHeightGrid))
            {
                var z = FlatTextZ + reliefBias + MaxReliefLiftAround(item.Latitude, item.Longitude, footprint, flat: true);
                item.FlatPos = new Vector3(item.Longitude / 360f * FlatMapWidth, item.Latitude / 180f * FlatMapHeight, z);
                item.FlatRelief = _reliefDisplacement;
                item.FlatReliefGrid = _reliefHeightGrid;
            }
            return WrapCanonicalFlatPoint(item.FlatPos);
        }
        if (!Mathf.IsEqualApprox(item.GlobeRelief, _reliefDisplacement) ||
            !ReferenceEquals(item.GlobeReliefGrid, _reliefHeightGrid))
        {
            item.GlobePos = LocalSurfaceLabelPoint(item.Latitude, item.Longitude, SurfaceTextRadius + reliefBias, footprint);
            item.GlobeRelief = _reliefDisplacement;
            item.GlobeReliefGrid = _reliefHeightGrid;
        }
        return item.GlobePos;
    }

    private Rect2 RegionScreenBox(RegionLabelCandidate item, string upperDisplay, Font font, float scale, Vector3 localPos, in Transform3D xform)
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
            _regionSlotStates.Add(new RegionLabelSlotState());
        }
        return _regionLabelPool[slot];
    }
}
