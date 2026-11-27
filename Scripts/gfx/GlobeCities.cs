using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;
using RBush;

namespace DealOrForce;

/// <summary>
/// The cities on the map (item 91): a dot and a name, the way Hearts of Iron marks its cities and
/// Age of History its provinces.
///
/// Every city is drawn the same way: one dot, one lettering. There are seven thousand of them
/// and a screen has room for a hundred and fifty names, so the map does what an atlas editor
/// does: it takes the cities in order - the ones that are some country's capital today first,
/// then the rest as the data writes them, which is from the most prominent place down - and sets
/// each name only if it has room, never over another. How many of them are candidates at all
/// follows the zoom (map.city_band*_count): from far out only the leading few hundred, closer in
/// more, and all of them only on a region's own screen.
///
/// A capital has its own mark and colour, and those belong to whichever city is a capital at
/// the moment: when a government moves, the mark moves with it and the city it left is a city
/// like any other.
///
/// Nothing is built per city. A fixed pool of pins is handed out again whenever the camera has
/// moved, at most every tenth of a second, so a map of any size costs the same.
/// </summary>
public sealed partial class GlobePresentation
{
    private Node3D _cityRoot;
    private readonly List<CityPin> _cityPins = new();
    private IReadOnlyList<CityDefinition> _cities = Array.Empty<CityDefinition>();
    private Vector3[] _cityNormals = Array.Empty<Vector3>();
    private CityCatalog.MapAnchor[] _cityAnchors = Array.Empty<CityCatalog.MapAnchor>();
    private ProvinceTopology _cityAnchorMap;
    private readonly List<Rect2> _cityTaken = new();
    private double _cityClock;
    private Transform3D _citySeenCamera;
    private float _citySeenSize = -1f;
    private float _citySeenDistance = -1f;
    private Vector2I _citySeenViewport;
    private bool _citySeenFlat;
    private Vector3[] _cityGlobePositions = Array.Empty<Vector3>();
    private Vector3[] _cityFlatBasePositions = Array.Empty<Vector3>();
    private bool[] _cityGlobePosValid = Array.Empty<bool>();
    private bool[] _cityFlatPosValid = Array.Empty<bool>();
    private Vector3[] _riverGlobePositions = Array.Empty<Vector3>();
    private Vector3[] _riverFlatBasePositions = Array.Empty<Vector3>();
    private bool[] _riverGlobePosValid = Array.Empty<bool>();
    private bool[] _riverFlatPosValid = Array.Empty<bool>();
    private float _cityPosRelief = -1f;
    private byte[] _cityPosReliefGrid;
    private bool _cityDirty = true;
    private bool _cityLabelsEnabled = true;
    private float _lastCityFlatZoom = -1f;
    private float _lastCityFlatPixelScale = -1f;
    private int _lastCityFlatViewportY = -1;
    private float _lastCityDistance = -1f;
    private Vector3 _lastCityCamPos = new(float.NaN, float.NaN, float.NaN);
    private bool _cityHasActiveFades;
    private string _citySeenLanguage = "";
    private Font _cityWidthFont;
    private int _cityWidthFontSize = -1;
    private string _cityWidthLanguage = "";
    private readonly Dictionary<string, float> _cityTextWidths = new(StringComparer.Ordinal);
    private ImageTexture _cityDotTexture;
    private ImageTexture _cityCapitalTexture;
    /// <summary>Which cities are some country's capital today, city for city. Rebuilt with the stars.</summary>
    private bool[] _cityIsCapital = Array.Empty<bool>();
    /// <summary>The same cities as a list, in the order of the catalogue: the first to be lettered.</summary>
    private readonly List<int> _cityCapitals = new();
    private IReadOnlyList<RiverLabelAnchor> _riverAnchors = Array.Empty<RiverLabelAnchor>();
    private Vector3[] _riverNormals = Array.Empty<Vector3>();
    private readonly Dictionary<int, float> _pinAlphas = new();
    private readonly HashSet<int> _placedPinKeys = new();
    private readonly List<FadePinSnapshot> _fadingPinSnapshots = new();
    private readonly HashSet<string> _placedCityNames = new(StringComparer.OrdinalIgnoreCase);
    private int _cityLayoutVersion;
    private readonly HashSet<int> _visibleStarCitiesScratch = new();
    private readonly List<Vector3> _visibleCapitalNormalsScratch = new();
    private List<int> _cachedOrderedAtlasCities;
    private int _cachedOrderedAtlasCapRev = int.MinValue;
    private int _cachedOrderedAtlasMapRev = int.MinValue;
    private int _cachedOrderedAtlasPlayer = int.MinValue;
    private int _cachedOrderedAtlasSelected = int.MinValue;
    private int _cachedOrderedAtlasCandidates = -1;
    private int _cachedOrderedAtlasCityCount = -1;
    private ProvinceTopology _cachedOrderedAtlasMap;

    private sealed class SpatialGeoItem<T> : ISpatialData
    {
        public readonly T Value;
        private readonly Envelope _envelope;

        public SpatialGeoItem(T value, float latitude, float longitude)
        {
            Value = value;
            var lon = NormalizeLongitude180(longitude);
            var lat = Math.Clamp(latitude, -90f, 90f);
            _envelope = new Envelope(lon, lat, lon, lat);
        }

        public ref readonly Envelope Envelope => ref _envelope;
    }

    private RBush<SpatialGeoItem<int>> _cityRTree;
    private int _cityRTreeVersion;
    private readonly List<int> _cityQueryScratch = new();
    private readonly List<int> _cityFrustumClassicOrder = new();
    private readonly List<int> _cityFrustumAtlasOrder = new();
    private (bool Flat, int LatBucket, int LonBucket, int SpanBucket, int Candidates, int TreeVersion, int CityCount) _cityClassicBucketKey;
    private (bool Flat, int LatBucket, int LonBucket, int SpanBucket, int Candidates, int TreeVersion, int CapRev, int MapRev, int Player, int Selected, int CityCount) _cityAtlasBucketKey;

    private RBush<SpatialGeoItem<int>> _riverRTree;
    private int _riverRTreeVersion;
    private readonly List<int> _riverFrustumOrder = new();
    private (bool Flat, int LatBucket, int LonBucket, int SpanBucket, int TreeVersion) _riverBucketKey;

    private static float NormalizeLongitude180(float longitude)
    {
        while (longitude > 180f) longitude -= 360f;
        while (longitude < -180f) longitude += 360f;
        return longitude;
    }

    private void ComputeGeoFrustumBounds(
        Vector2I viewport,
        Basis rootBasis,
        float extraPadDeg,
        out float minLat,
        out float maxLat,
        out float minLon,
        out float maxLon,
        out bool fullLongitude,
        out int latBucket,
        out int lonBucket,
        out int spanBucket)
    {
        if (_flatMap)
        {
            var halfH = _camera.Size * 0.5f;
            var halfW = halfH * viewport.X / MathF.Max(1f, viewport.Y);
            var camLat = _flatY / FlatMapHeight * 180f;
            var camLon = WrappedFlatX(_flatX) / FlatMapWidth * 360f;
            var latSpanDeg = halfH / FlatMapHeight * 180f * 1.15f + extraPadDeg;
            var lonSpanDeg = halfW / FlatMapWidth * 360f * 1.15f + extraPadDeg;
            minLat = Math.Clamp(camLat - latSpanDeg, -90f, 90f);
            maxLat = Math.Clamp(camLat + latSpanDeg, -90f, 90f);
            fullLongitude = lonSpanDeg >= 176f;
            minLon = camLon - lonSpanDeg;
            maxLon = camLon + lonSpanDeg;
            var bucketStep = Math.Clamp(MathF.Max(latSpanDeg, lonSpanDeg) * 0.10f, 1.5f, extraPadDeg * 0.5f);
            latBucket = (int)MathF.Round(camLat / bucketStep);
            lonBucket = (int)MathF.Round(camLon / bucketStep);
            spanBucket = (int)MathF.Round(MathF.Max(latSpanDeg, lonSpanDeg) * 0.25f);
            return;
        }

        if (!IsInsideTree() || !IsInstanceValid(_camera) || !_camera.IsInsideTree())
        {
            minLat = -90f;
            maxLat = 90f;
            minLon = -180f;
            maxLon = 180f;
            fullLongitude = true;
            latBucket = 0;
            lonBucket = 0;
            spanBucket = 0;
            return;
        }

        var camLocalPos = rootBasis.Inverse() * _camera.GlobalPosition;
        var dist = MathF.Max(GlobeRadius + 0.01f, camLocalPos.Length());
        var camNormal = camLocalPos / dist;
        var (gLat, gLon) = GeographicFromNormal(camNormal);
        var horizonCos = Math.Clamp(GlobeRadius / dist - 0.05f, 0f, 1f);
        var horizonAngle = MathF.Acos(horizonCos);
        var fovHalfRad = Mathf.DegToRad(_camera.Fov * 0.5f);
        var aspect = viewport.X / MathF.Max(1f, viewport.Y);
        var coneAngle = MathF.Atan(MathF.Tan(fovHalfRad) * MathF.Sqrt(1f + aspect * aspect));
        var sinCone = MathF.Sin(coneAngle);
        float maxAngle;
        if (dist * sinCone < GlobeRadius)
        {
            var theta = MathF.Asin(Math.Clamp(dist * sinCone / GlobeRadius, 0f, 1f)) - coneAngle;
            maxAngle = MathF.Min(horizonAngle, MathF.Max(theta * 1.25f + 0.06f, 0.08f));
        }
        else
        {
            maxAngle = horizonAngle;
        }

        var globeLatSpan = Mathf.RadToDeg(maxAngle) + extraPadDeg;
        minLat = Math.Clamp(gLat - globeLatSpan, -90f, 90f);
        maxLat = Math.Clamp(gLat + globeLatSpan, -90f, 90f);
        var maxAbsLat = MathF.Min(89f, MathF.Max(MathF.Abs(minLat), MathF.Abs(maxLat)));
        if (maxAbsLat >= 84f)
        {
            fullLongitude = true;
            minLon = -180f;
            maxLon = 180f;
        }
        else
        {
            var ratio = MathF.Sin(MathF.Min(maxAngle, MathF.PI * 0.49f)) / MathF.Max(0.10f, MathF.Cos(Mathf.DegToRad(maxAbsLat)));
            if (ratio >= 0.96f)
            {
                fullLongitude = true;
                minLon = -180f;
                maxLon = 180f;
            }
            else
            {
                var globeLonSpan = Mathf.RadToDeg(MathF.Asin(ratio)) + extraPadDeg * 1.25f;
                fullLongitude = globeLonSpan >= 176f;
                minLon = gLon - globeLonSpan;
                maxLon = gLon + globeLonSpan;
            }
        }
        var gBucketStep = Math.Clamp(globeLatSpan * 0.10f, 1.5f, extraPadDeg * 0.5f);
        latBucket = (int)MathF.Round(gLat / gBucketStep);
        lonBucket = (int)MathF.Round(gLon / gBucketStep);
        spanBucket = (int)MathF.Round(globeLatSpan * 0.25f);
    }

    private static void SearchGeoRTree<T>(
        RBush<SpatialGeoItem<T>> tree,
        List<T> output,
        float minLat,
        float maxLat,
        float minLon,
        float maxLon,
        bool fullLongitude)
    {
        output.Clear();
        if (tree is null || tree.Count == 0) return;
        if (fullLongitude)
        {
            var hits = tree.Search(new Envelope(-180f, minLat, 180f, maxLat));
            for (var i = 0; i < hits.Count; i++) output.Add(hits[i].Value);
            return;
        }
        if (minLon < -180f)
        {
            var left = tree.Search(new Envelope(-180f, minLat, Math.Min(180f, maxLon), maxLat));
            for (var i = 0; i < left.Count; i++) output.Add(left[i].Value);
            var wrap = tree.Search(new Envelope(minLon + 360f, minLat, 180f, maxLat));
            for (var i = 0; i < wrap.Count; i++) output.Add(wrap[i].Value);
            return;
        }
        if (maxLon > 180f)
        {
            var right = tree.Search(new Envelope(minLon, minLat, 180f, maxLat));
            for (var i = 0; i < right.Count; i++) output.Add(right[i].Value);
            var wrap = tree.Search(new Envelope(-180f, minLat, maxLon - 360f, maxLat));
            for (var i = 0; i < wrap.Count; i++) output.Add(wrap[i].Value);
            return;
        }
        var box = tree.Search(new Envelope(minLon, minLat, maxLon, maxLat));
        for (var i = 0; i < box.Count; i++) output.Add(box[i].Value);
    }

    private void RebuildCitySpatialIndex()
    {
        _cityRTree = new RBush<SpatialGeoItem<int>>();
        if (_cityAnchors.Length > 0)
        {
            var items = new SpatialGeoItem<int>[_cityAnchors.Length];
            for (var i = 0; i < _cityAnchors.Length; i++)
                items[i] = new SpatialGeoItem<int>(i, _cityAnchors[i].Latitude, _cityAnchors[i].Longitude);
            _cityRTree.BulkLoad(items);
        }
        _cityRTreeVersion++;
        _cityClassicBucketKey = default;
        _cityAtlasBucketKey = default;
    }

    private void RebuildRiverSpatialIndex()
    {
        _riverRTree = new RBush<SpatialGeoItem<int>>();
        if (_riverAnchors.Count > 0)
        {
            var items = new SpatialGeoItem<int>[_riverAnchors.Count];
            for (var i = 0; i < _riverAnchors.Count; i++)
                items[i] = new SpatialGeoItem<int>(i, _riverAnchors[i].Latitude, _riverAnchors[i].Longitude);
            _riverRTree.BulkLoad(items);
        }
        _riverRTreeVersion++;
        _riverBucketKey = default;
    }

    private readonly record struct FadePinSnapshot(
        int SlotKey,
        Vector3 Position,
        Quaternion Rotation,
        Vector3 Scale,
        float FlatScale,
        string Text,
        Font Font,
        int FontSize,
        int OutlineSize,
        float PixelSize,
        Vector2 Offset,
        HorizontalAlignment Align,
        Color BaseFill,
        Color BaseOutline,
        float Alpha,
        Vector3 AnchorNormal);

    /// <summary>Whether cities are drawn at all - the player's setting.</summary>
    public bool CityLabelsEnabled
    {
        get => _cityLabelsEnabled;
        set
        {
            if (_cityLabelsEnabled == value) return;
            _cityLabelsEnabled = value;
            _cityDirty = true;
        }
    }

    private sealed class CityPin
    {
        public Node3D Node;
        public Sprite3D Dot;
        public Label3D Name;
        /// <summary>The city in the catalogue; -2 - n for the n-th star named at the star.</summary>
        public int City = -1;
        /// <summary>The capital's star a name stands beside, when it is one of those.</summary>
        public Node3D Star;
        public float FlatPixelScale = -1f;
        public int TargetKey = -1;
        public float Alpha = 1f;
        public float TargetAlpha = 1f;
        public Color BaseFill = Colors.White;
        public Color BaseOutline = Colors.Black;
        public Vector3 AnchorNormal = Vector3.Zero;
        public string CachedNodeName = "";
        public string CachedText = "";
        public Font CachedFont;
        public int CachedFontSize = -1;
        public int CachedOutlineSize = -1;
        public bool? CachedFixedSize;
        public HorizontalAlignment CachedAlign = (HorizontalAlignment)(-1);
        public Vector2 CachedOffset = new(float.NaN, float.NaN);
        public float CachedNamePixelSize = -1f;
        public float CachedDotPixelSize = -1f;
        public Vector3 CachedScale = new(float.NaN, float.NaN, float.NaN);
        public Vector3 CachedPosition = new(float.NaN, float.NaN, float.NaN);
        public Quaternion CachedRotation = new(float.NaN, float.NaN, float.NaN, float.NaN);
        public Texture2D CachedDotTexture;
        public bool CachedNameVisible = true;
        public bool CachedDotVisible = true;
        public bool CachedNodeVisible;
        public bool CachedTransformValid;
        public bool CachedDotModulateInit;
        public Color AppliedModulateFill;
        public Color AppliedModulateOutline;
        public float AppliedModulateAlpha = -1f;
    }

    private void AdoptRiverAnchors(IReadOnlyList<RiverLabelAnchor> anchors)
    {
        _riverAnchors = anchors ?? Array.Empty<RiverLabelAnchor>();
        _riverNormals = new Vector3[_riverAnchors.Count];
        for (var i = 0; i < _riverAnchors.Count; i++)
            _riverNormals[i] = SurfacePoint(_riverAnchors[i].Latitude, _riverAnchors[i].Longitude, 1f).Normalized();
        _riverGlobePositions = new Vector3[_riverAnchors.Count];
        _riverFlatBasePositions = new Vector3[_riverAnchors.Count];
        _riverGlobePosValid = new bool[_riverAnchors.Count];
        _riverFlatPosValid = new bool[_riverAnchors.Count];
        RebuildRiverSpatialIndex();
        _cityDirty = true;
    }

    /// <summary>
    /// Every capital star, with the city beside it (-1 for none within the distance a star hides
    /// a dot at) and whose star it is. Rebuilt when the stars are.
    /// </summary>
    private readonly List<(Node3D Marker, Vector3 Normal, int City, Nation Nation, string CapitalName)> _starNames = new();
    private (int Revision, int Count, ulong First, int LocRevision, string Language) _starNamesBuilt = (-1, -1, 0, -1, "");
    private bool NeedsStarNamesRefresh()
    {
        var first = _capitalMarkers.Count > 0 && IsInstanceValid(_capitalMarkers[0].Node)
            ? _capitalMarkers[0].Node.GetInstanceId() : 0ul;
        return (_capitalRevision, _capitalMarkers.Count, first, Loc.Revision, Loc.Language ?? "") != _starNamesBuilt;
    }
    /// <summary>Each star's place on the screen this placement, in <see cref="_starNames"/> order.</summary>
    private readonly List<Vector2?> _starScreens = new();
    /// <summary>The stars drawn this placement and the city each stands on, which names keep off.</summary>
    private readonly List<(Vector2 Screen, int City)> _starMarks = new();
    /// <summary>The countries' capitals' names on the screen, which the headquarters' names keep off.</summary>
    private readonly List<Rect2> _capitalNameBoxes = new();
    private readonly HashSet<int> _cityDone = new();

    private void CreateCityLayer(Node3D world)
    {
        _cityRoot = new Node3D { Name = "Cities" };
        world.AddChild(_cityRoot);
        _cityDotTexture = CityDotTexture(false);
        _cityCapitalTexture = CityDotTexture(true);
        AdoptCities();
    }

    private bool _citiesUnreadable;

    /// <summary>
    /// Take the catalogue's cities as they are now. The catalogue is read again for every
    /// campaign and a mod's cities are laid over it then (ModLoader, CityCatalog.Merge), so the
    /// list this layer was built on may no longer be the list: every index kept here - a pin's
    /// city, which cities are capitals, where each one's anchor is - is an index into the list
    /// it was taken from, and all of them are dropped with it.
    /// </summary>
    private void AdoptCities()
    {
        if (_citiesUnreadable) return;
        IReadOnlyList<CityDefinition> cities;
        try
        {
            cities = CityCatalog.All;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[dof] cities not loaded: {exception.Message}");
            _citiesUnreadable = true;
            return;
        }
        if (ReferenceEquals(cities, _cities)) return;
        _cities = cities;
        _cityNormals = new Vector3[cities.Count];
        // Until the map places them (UpdateCityLabels), each city stands at its own point.
        _cityAnchors = new CityCatalog.MapAnchor[cities.Count];
        for (var index = 0; index < cities.Count; index++)
        {
            _cityNormals[index] = SurfacePoint(cities[index].Latitude, cities[index].Longitude, 1f).Normalized();
            _cityAnchors[index] = new CityCatalog.MapAnchor(0, cities[index].Latitude, cities[index].Longitude);
        }
        _cityAnchorMap = null;
        _cityPosRelief = -1f;
        _starNamesBuilt = (-1, -1, 0, -1, "");
        _settlementCountedFor = default;
        RebuildCitySpatialIndex();
        InvalidateCities();
    }

    /// <summary>
    /// A mark drawn once. There are two: a pale disc in a dark ring, which is every city, and a
    /// golden compass-star roundel, which is a seat of government for as long as it is one.
    /// </summary>
    private static ImageTexture CityDotTexture(bool capital)
    {
        const int size = 32;
        const int scale = 2;
        const int s = size * scale;
        var image = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        var centre = (s - 1) / 2f;
        var gold = new Color(0.99f, 0.85f, 0.38f);
        var ivory = new Color(0.95f, 0.97f, 0.99f);
        var fill = capital ? gold : ivory;
        var ink = new Color(0.03f, 0.05f, 0.08f, 0.96f);
        var outerRadius = (capital ? 14.8f : 11.2f) * scale;
        for (var y = 0; y < s; y++)
        for (var x = 0; x < s; x++)
        {
            var dx = x - centre;
            var dy = y - centre;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance > outerRadius + 1f) continue;

            Color colour;
            if (capital)
            {
                // Golden cartographic capital roundel with 4-point star flare and ivory core
                var angle = MathF.Atan2(dy, dx);
                var starFlare = MathF.Pow(MathF.Abs(MathF.Cos(angle * 2f)), 3f) * 3.2f;
                if (distance <= 4.5f * scale) colour = ivory;
                else if (distance <= (8.2f + starFlare * 0.45f) * scale) colour = gold;
                else if (distance >= 10.2f * scale && distance <= 12.8f * scale) colour = gold.Darkened(0.08f);
                else colour = ink;
            }
            else
            {
                if (distance <= 7.2f * scale) colour = fill.Lerp(Colors.White, (1f - distance / (7.2f * scale)) * 0.25f);
                else colour = ink;
            }

            var edge = outerRadius - distance;
            colour.A *= Mathf.Clamp(edge + 0.75f, 0f, 1f);
            image.SetPixel(x, y, colour);
        }
        image.Resize(size, size, Image.Interpolation.Lanczos);
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// The farthest out each zoom band begins, as a view distance: band 0 from where cities are
    /// drawn at all, band 3 the closest. (The metrics keep the names they had when the bands were
    /// a rank written on each city.)
    /// </summary>
    private static float CityTierDistance(int band) => band switch
    {
        0 => InterfaceSkin.Metric("map.city_tier0_distance", 15.0f),
        1 => InterfaceSkin.Metric("map.city_tier1_distance", 11.0f),
        2 => InterfaceSkin.Metric("map.city_tier2_distance", 8.4f),
        _ => InterfaceSkin.Metric("map.city_tier3_distance", 6.6f)
    };

    /// <summary>
    /// How many cities are lettered in a zoom band: that many from the head of the catalogue,
    /// beside the capitals, which always are. The closest band takes every city. The defaults are
    /// how many places each of the old ranks held, so the map shows what it showed.
    /// </summary>
    private int CityBandCount(int band) => band switch
    {
        0 => Math.Clamp((int)InterfaceSkin.Metric("map.city_band0_count", 330f), 0, _cities.Count),
        1 => Math.Clamp((int)InterfaceSkin.Metric("map.city_band1_count", 1142f), 0, _cities.Count),
        2 => Math.Clamp((int)InterfaceSkin.Metric("map.city_band2_count", 2448f), 0, _cities.Count),
        _ => _cities.Count
    };

    private void EnsureCityAnchorsForMap(bool allowCampaignRead = true)
    {
        var map = _provinceTopology ?? (allowCampaignRead ? _campaign?.ProvinceMap : null);
        if (map is null || (ReferenceEquals(map, _cityAnchorMap) && _cityAnchors.Length == _cities.Count)) return;
        _cityAnchorMap = map;
        _cityAnchors = new CityCatalog.MapAnchor[_cities.Count];
        for (var index = 0; index < _cities.Count; index++)
        {
            var city = _cities[index];
            var anchor = CityCatalog.AnchorOnLand(map, city.Latitude, city.Longitude);
            _cityAnchors[index] = anchor;
            _cityNormals[index] = SurfacePoint(anchor.Latitude, anchor.Longitude, 1f).Normalized();
        }
        _cityPosRelief = -1f;
        _starNamesBuilt = (-1, -1, 0, -1, "");
        RebuildCitySpatialIndex();
        InvalidateCities();
    }

    private void SyncFlatCityPinScale(Vector2I viewport)
    {
        if (!_flatMap) return;
        var pixels = _camera.Size / Math.Max(1, viewport.Y);
        if (viewport.Y == _lastCityFlatViewportY &&
            MathF.Abs(_flatZoom - _lastCityFlatZoom) <= _flatZoom * 1e-4f &&
            MathF.Abs(pixels - _lastCityFlatPixelScale) <= 1e-6f)
            return;
        _lastCityFlatZoom = _flatZoom;
        _lastCityFlatViewportY = viewport.Y;
        _lastCityFlatPixelScale = pixels;
        foreach (var pin in _cityPins)
            if (pin.CachedNodeVisible && Mathf.Abs(pin.FlatPixelScale - pixels) > 1e-6f)
            {
                pin.FlatPixelScale = pixels;
                var scale = Vector3.One * pixels;
                pin.CachedScale = scale;
                pin.Node.Scale = scale;
            }
    }

    private void UpdateCityLabels(double delta, CampaignRenderSnapshot snap = null, bool allowCampaignRead = true)
    {
        if (!IsInsideTree() || !IsInstanceValid(_cityRoot) || !_cityRoot.IsInsideTree() ||
            !IsInstanceValid(_camera) || !_camera.IsInsideTree()) return;
        var frame = Engine.GetProcessFrames();
        if (_cityLastProcessFrame == frame && !(allowCampaignRead && (_cityDirty || _cityAnchorMap is null || NeedsStarNamesRefresh())))
            return;
        _cityLastProcessFrame = frame;
        if (!Simulation.WorldLayers.On("city_labels")) { if (_cityRoot.Visible) _cityRoot.Visible = false; return; }
        AdoptCities();
        EnsureCityAnchorsForMap(allowCampaignRead);
        var wanted = QualityCityLabels && CityLabelsEnabled && _mapApplied && _cities.Count > 0 &&
                     ViewDistance <= CityTierDistance(0);
        if (_cityRoot.Visible != wanted) _cityRoot.Visible = wanted;
        if (!wanted)
        {
            // Hidden capitals must not reserve old screen rectangles after zooming out.
            if (_capitalNameBoxes.Count > 0 || _cityTaken.Count > 0 || _placedCityNames.Count > 0 || _pinAlphas.Count > 0)
            {
                _capitalNameBoxes.Clear();
                _cityTaken.Clear();
                _placedCityNames.Clear();
                _pinAlphas.Clear();
                _cityHasActiveFades = false;
            }
            return;
        }
        if (_mapLookAtlas && _cityHasActiveFades && _cityLastFadeFrame != frame)
        {
            _cityLastFadeFrame = frame;
            StepCityPinFades((float)delta);
        }

        if (_cityLastClockFrame != frame)
        {
            _cityLastClockFrame = frame;
            _cityClock += delta;
        }
        var viewport = ViewportSize();
        SyncFlatCityPinScale(viewport);
        var cameraTransform = _camera.GlobalTransform;
        var moved = _citySeenFlat != _flatMap || _citySeenViewport != viewport ||
                    !cameraTransform.IsEqualApprox(_citySeenCamera) ||
                    Mathf.Abs(_camera.Size - _citySeenSize) > 0.0001f;
        var language = Loc.Language;
        if (language != _citySeenLanguage) { _citySeenLanguage = language; InvalidateCities(); }
        var projectionSwitched = _citySeenFlat != _flatMap;
        if (projectionSwitched)
        {
            InvalidateCities();
            _pinAlphas.Clear();
            _fadingPinSnapshots.Clear();
            _cityHasActiveFades = false;
        }
        var angleRad = 0f;
        var zoomRatio = 0f;
        var effectiveAngleRad = 0f;
        if (_citySeenFlat == _flatMap && _citySeenViewport == viewport)
        {
            if (_flatMap && _citySeenSize > 0f)
            {
                var shift = new Vector2(cameraTransform.Origin.X - _citySeenCamera.Origin.X,
                    cameraTransform.Origin.Y - _citySeenCamera.Origin.Y).Length();
                zoomRatio = MathF.Abs(_camera.Size - _citySeenSize) / _citySeenSize;
                if (!_cityDirty && shift < _camera.Size * 0.02f && zoomRatio <= 0.02f) return;
                if (shift >= _camera.Size * 0.15f) effectiveAngleRad = MapOverlaySyncRules.RapidSweepAngleRad;
            }
            else if (!_flatMap && _citySeenDistance > 0f)
            {
                var prevDir = _citySeenCamera.Origin.Normalized();
                var curDir = cameraTransform.Origin.Normalized();
                var originAngle = prevDir.LengthSquared() > 0.5f && curDir.LengthSquared() > 0.5f
                    ? MathF.Acos(Math.Clamp(prevDir.Dot(curDir), -1f, 1f))
                    : float.MaxValue;
                var lookDot = Math.Clamp(_citySeenCamera.Basis.Z.Dot(cameraTransform.Basis.Z), -1f, 1f);
                angleRad = MathF.Max(originAngle, MathF.Acos(lookDot));
                effectiveAngleRad = angleRad;
                zoomRatio = MathF.Abs(_distance - _citySeenDistance) / _citySeenDistance;
                if (!_cityDirty && angleRad <= 0.02f && zoomRatio <= 0.02f)
                {
                    if (moved) HideCitiesPastHorizon();
                    return;
                }
            }
        }
        var cityTier = CurrentShaderQualityTier();
        var movedInterval = cityTier >= 1.5f ? 0.080 : cityTier >= 0.5f ? 0.055 : 0.033;
        var idleInterval = cityTier >= 1.5f ? 1.00 : cityTier >= 0.5f ? 0.80 : 0.50;
        if (!moved)
        {
            if (!_cityDirty || _cityClock < idleInterval) return;
        }
        else if (!MapOverlaySyncRules.ShouldRelayoutCitiesNow(effectiveAngleRad, zoomRatio, _cityClock, movedInterval, _cityDirty || projectionSwitched))
        {
            if (!_flatMap) HideCitiesPastHorizon();
            return;
        }
        _cityClock = 0;
        _cityDirty = false;
        _citySeenCamera = cameraTransform;
        _citySeenSize = _camera.Size;
        _citySeenDistance = _distance;
        _citySeenViewport = viewport;
        _citySeenFlat = _flatMap;
        SyncCityDestroyedCache(snap, allowCampaignRead);
        var allowFadeSnapshots = !projectionSwitched && angleRad <= 0.06f && (_flatMap || !GlobeViewInMotion);
        using (SimProfiler.Measure("f:cities")) PlaceCities(viewport, allowFadeSnapshots, snap, allowCampaignRead);
        _lastCityFlatZoom = _flatZoom;
        _lastCityFlatViewportY = viewport.Y;
        _lastCityFlatPixelScale = _camera.Size / Math.Max(1, viewport.Y);
        _lastCityCamPos = _camera.GlobalPosition;
        _lastCityDistance = _distance;
        _lastCityCamBasis = cameraTransform.Basis;
    }

    private struct CityPassContext
    {
        public Vector2I Viewport;
        public int Limit;
        public int FontSize;
        public float TextPixels;
        public float CapitalTextPixels;
        public Font Font;
        /// <summary>How many cities from the head of the catalogue are lettered at this zoom.</summary>
        public int Candidates;
        public float DotPixels;
        public float CapitalMarkPixels;
        /// <summary>The capital star's ink ring across, on the screen: a name beside a star starts past it.</summary>
        public float CapitalRingPixels;
        public float FixedPixel;
        public float FlatPixel;
        public Vector3 CameraNormal;
        public Transform3D RootTransform;
        public Transform3D RootInverse;
        public Basis RootBasis;
        public float Horizon;
        public Color CapitalColor;
        public Color CityColor;
        public Color OutlineColor;
        public int Used;
        public bool StarsNamed;
        public bool AllowFadeSnapshots;
    }

    private float CityTextWidth(Font font, int fontSize, string name)
    {
        var language = Loc.Language;
        if (!ReferenceEquals(_cityWidthFont, font) || _cityWidthFontSize != fontSize || _cityWidthLanguage != language)
        {
            _cityWidthFont = font;
            _cityWidthFontSize = fontSize;
            _cityWidthLanguage = language;
            _cityTextWidths.Clear();
        }
        if (!_cityTextWidths.TryGetValue(name, out var width))
        {
            width = font.GetStringSize(name, HorizontalAlignment.Left, -1, fontSize).X;
            if (_cityTextWidths.Count < 8192) _cityTextWidths[name] = width;
        }
        return width;
    }

    private void PlaceCities(Vector2I viewport, bool allowFadeSnapshots, CampaignRenderSnapshot snap = null, bool allowCampaignRead = true)
    {
        if (!IsInsideTree() || !IsInstanceValid(_cityRoot) || !_cityRoot.IsInsideTree() ||
            !IsInstanceValid(_camera) || !_camera.IsInsideTree()) return;
        EnsureCityPositionCache();
        var limit = Math.Clamp((int)InterfaceSkin.Metric(_flatMap ? "map.flat_city_label_limit" : "map.city_label_limit", _flatMap ? 90f : 140f), 0, 400);
        var tier = CurrentShaderQualityTier();
        if (tier >= 1.5f) limit = Math.Min(limit, 36);
        else if (tier >= 0.5f) limit = Math.Min(limit, 64);
        var fontSize = Math.Clamp((int)InterfaceSkin.Metric("map.city_font_size", 44f), 12, 64);
        var textPixels = Math.Clamp(InterfaceSkin.Metric(_flatMap ? "map.flat_city_text_pixels" : "map.city_text_pixels", _flatMap ? 12f : 12.5f), 8f, 28f);
        var capitalTextPixels = Math.Clamp(InterfaceSkin.Metric(_flatMap ? "map.flat_city_capital_text_pixels" : "map.city_capital_text_pixels", textPixels + 1.3f), textPixels, 32f);
        var font = PresentationFonts.For("map.cities");
        var distance = ViewDistance;
        var band = 0;
        if (_flatMap)
            while (band < 3 && _flatZoom <= InterfaceSkin.Metric($"map.flat_city_tier{band + 1}_zoom",
                band switch { 0 => 3.2f, 1 => 1.5f, _ => .8f })) band++;
        else
            while (band < 3 && distance <= CityTierDistance(band + 1)) band++;
        var candidates = CityBandCount(band);
        var leading = Math.Min(candidates, CityBandCount(0));

        // World units per screen pixel: on the flat map the camera's own height over the
        // viewport; on the globe, a fixed-size label's pixel size for the field of view.
        var fixedPixel = 2f * Mathf.Tan(Mathf.DegToRad(_camera.Fov * 0.5f)) / Math.Max(1, viewport.Y);
        var flatPixel = _camera.Size / Math.Max(1, viewport.Y);
        var cameraNormal = _flatMap ? Vector3.Zero : _camera.GlobalPosition.Normalized();
        var rootTransform = _cityRoot.GlobalTransform;
        var rootBasis = rootTransform.Basis;
        var horizon = CityHorizon();

        _cityTaken.Clear();
        _movementLayer?.AddMedallions(_cityTaken);
        // Surface counters are world-sized in both views. Project their actual corners;
        // skip markers already visibility-culled out of the frustum or horizon.
        if (IsInstanceValid(_armyRoot) && _armyRoot.Visible && _armyRoot.IsVisibleInTree())
            foreach (var marker in _armyStacks.Values)
                if (IsInstanceValid(marker.Node) && marker.Node.Visible)
                    ReserveSurfaceCard(marker.Plate, viewport);
        if (IsInstanceValid(_supportMarkerRoot) && _supportMarkerRoot.Visible && _supportMarkerRoot.IsVisibleInTree())
            foreach (var marker in _supportMapMarkers.Values)
                if (IsInstanceValid(marker.Node) && marker.Node.Visible)
                    ReserveSurfaceCard(marker.Plate, viewport);
        if (IsInstanceValid(_fleetRoot) && _fleetRoot.Visible && _fleetRoot.IsVisibleInTree())
            foreach (var parts in _fleetNodes.Values)
                if (IsInstanceValid(parts.Node) && parts.Node.Visible)
                    ReserveSurfaceCard(parts.Plate, viewport);

        // Where every capital's star stands on the screen, before any name is set: a name is
        // never written across a star that is not its own. Santo Domingo, with no room to its
        // right, was set to its left straight over Port-au-Prince's.
        RefreshStarNames(allowCampaignRead);
        _starMarks.Clear();
        _starScreens.Clear();
        _visibleStarCitiesScratch.Clear();
        _visibleCapitalNormalsScratch.Clear();
        var capRootVisible = IsInstanceValid(_capitalRoot) && _capitalRoot.Visible && _capitalRoot.IsVisibleInTree();
        if (capRootVisible)
        {
            foreach (var (node, markerNormal) in _capitalMarkers)
                if (IsInstanceValid(node) && node.Visible)
                    _visibleCapitalNormalsScratch.Add(markerNormal);
        }
        var limb = SurfaceMarkerRadius / Math.Max(SurfaceMarkerRadius, _camera.GlobalPosition.Length());
        foreach (var (marker, normal, city, _, _) in _starNames)
        {
            var markerVisible = capRootVisible && IsInstanceValid(marker) && marker.Visible;
            if (markerVisible && city >= 0) _visibleStarCitiesScratch.Add(city);
            var screen = markerVisible ? StarOnScreen(marker, normal, viewport, rootBasis, cameraNormal, limb) : null;
            _starScreens.Add(screen);
            if (screen is { } point) _starMarks.Add((point, city));
        }

        // The stars no drawn city stands beside - a capital the map has no city for, the seat of
        // a country too small for the raster - are named at the star, by the name the country
        // gives it. After the capitals and the leading cities, before the rest: a territory of
        // fifty thousand people does not take the room Santo Domingo's name needs.
        var pass = new CityPassContext
        {
            Viewport = viewport,
            Limit = limit,
            FontSize = fontSize,
            TextPixels = textPixels,
            CapitalTextPixels = capitalTextPixels,
            Font = font,
            Candidates = candidates,
            DotPixels = Math.Clamp(InterfaceSkin.Metric("map.city_dot_pixels", 8f), 3f, 24f),
            CapitalMarkPixels = Math.Clamp(InterfaceSkin.Metric("map.city_capital_mark_pixels", 10f), 3f, 32f),
            CapitalRingPixels = CapitalRingAcrossPixels(),
            FixedPixel = fixedPixel,
            FlatPixel = flatPixel,
            CameraNormal = cameraNormal,
            RootTransform = rootTransform,
            RootInverse = rootTransform.AffineInverse(),
            RootBasis = rootBasis,
            Horizon = horizon,
            CapitalColor = _mapLookAtlas ? AtlasColourCached("map.look_capital_fill", ref _cachedCapitalFillHex, ref _cachedCapitalFillColor) : Ui.Colour("globe.city_capital"),
            CityColor = _mapLookAtlas ? AtlasColourCached("map.look_label_fill", ref _cachedCountryFillHex, ref _cachedCountryFillColor) : Ui.Colour("globe.city"),
            OutlineColor = _mapLookAtlas ? AtlasColourCached("map.look_label_outline", ref _cachedCountryOutlineHex, ref _cachedCountryOutlineColor) : Ui.Colour("globe.city_outline"),
            Used = 0,
            StarsNamed = false, AllowFadeSnapshots = allowFadeSnapshots
        };

        _fadingPinSnapshots.Clear();
        _placedPinKeys.Clear();
        _placedCityNames.Clear();
        if (_mapLookAtlas && allowFadeSnapshots)
        {
            for (var i = 0; i < _cityPins.Count && _fadingPinSnapshots.Count < 24; i++)
            {
                var p = _cityPins[i];
                if (!p.CachedNodeVisible || !p.CachedNameVisible || p.Alpha <= 0.03f || p.TargetKey == -1) continue;
                _fadingPinSnapshots.Add(new FadePinSnapshot(
                    p.TargetKey, p.CachedPosition, p.CachedRotation, p.CachedScale, p.FlatPixelScale,
                    p.CachedText, p.CachedFont, p.CachedFontSize, p.CachedOutlineSize, p.CachedNamePixelSize,
                    p.CachedOffset, p.CachedAlign, p.BaseFill, p.BaseOutline, p.Alpha, p.AnchorNormal));
            }
        }

        // Headquarters names have fixed anchors and never hide on collision. Reserve their
        // room first, then place capitals before lesser cities without a cyclic dependency
        // between last frame's city rectangles and this frame's headquarters visibility.
        _movementLayer?.AddNames(_cityTaken);
        _capitalNameBoxes.Clear();
        _cityDone.Clear();

        ComputeGeoFrustumBounds(pass.Viewport, pass.RootBasis, 4f,
            out var qMinLat, out var qMaxLat, out var qMinLon, out var qMaxLon, out var qFullLon,
            out var qLatB, out var qLonB, out var qSpanB);

        if (!_mapLookAtlas)
        {
            // Classic preserves the original placement order and instant visibility switch,
            // querying only cities inside the visible RBush frustum envelope.
            foreach (var index in _cityCapitals)
            {
                if (pass.Used >= limit) break;
                _cityDone.Add(index);
                if (PlaceCity(index, ref pass, out var room)) _capitalNameBoxes.Add(room);
            }
            var classicKey = (_flatMap, qLatB, qLonB, qSpanB, candidates, _cityRTreeVersion, _cities.Count);
            if (classicKey != _cityClassicBucketKey)
            {
                _cityClassicBucketKey = classicKey;
                _cityFrustumClassicOrder.Clear();
                if (_cityRTree is null || (qFullLon && qMaxLat - qMinLat >= 150f && candidates <= 600))
                {
                    for (var idx = 0; idx < candidates; idx++)
                        if (!IsCapitalNow(idx)) _cityFrustumClassicOrder.Add(idx);
                }
                else
                {
                    SearchGeoRTree(_cityRTree, _cityQueryScratch, qMinLat, qMaxLat, qMinLon, qMaxLon, qFullLon);
                    for (var qi = 0; qi < _cityQueryScratch.Count; qi++)
                    {
                        var idx = _cityQueryScratch[qi];
                        if (idx < candidates && !IsCapitalNow(idx)) _cityFrustumClassicOrder.Add(idx);
                    }
                    _cityFrustumClassicOrder.Sort();
                }
            }
            for (var oi = 0; oi < _cityFrustumClassicOrder.Count && pass.Used < limit; oi++)
            {
                var index = _cityFrustumClassicOrder[oi];
                if (index >= leading && !pass.StarsNamed) NameStars(ref pass);
                if (pass.Used >= limit) break;
                if (_cityDone.Contains(index)) continue;
                PlaceCity(index, ref pass, out _);
            }
            if (!pass.StarsNamed) NameStars(ref pass);
        }
        else
        {
            PlaceOrderedAtlasCities(ref pass, candidates, limit, leading,
                qMinLat, qMaxLat, qMinLon, qMaxLon, qFullLon, qLatB, qLonB, qSpanB, snap, allowCampaignRead);
        }

        for (var index = pass.Used; index < _cityPins.Count; index++)
        {
            var unused = _cityPins[index];
            unused.TargetKey = -1; unused.AnchorNormal = Vector3.Zero;
            SetPinNodeVisible(unused, false);
        }
        _cityLayoutVersion++;
    }

    private void PlaceOrderedAtlasCities(
        ref CityPassContext pass, int candidates, int limit, int leading,
        float qMinLat, float qMaxLat, float qMinLon, float qMaxLon, bool qFullLon,
        int qLatB, int qLonB, int qSpanB, CampaignRenderSnapshot snap, bool allowCampaignRead)
    {
        ResolveCitySnapshotContext(snap, allowCampaignRead, out var playerMapIndex, out var provinceOwners, out var mapRev);
        var selectedMapIndex = _highlightedNation;
        var map = _provinceTopology ?? (allowCampaignRead ? _campaign?.ProvinceMap : null);
        bool IsPriorityCity(int cityIndex)
        {
            if (provinceOwners is null || cityIndex < 0 || cityIndex >= _cityAnchors.Length) return false;
            var pid = _cityAnchors[cityIndex].ProvinceId;
            if (pid <= 0 || pid >= provinceOwners.Length) return false;
            var owner = provinceOwners[pid];
            return (playerMapIndex > 0 && owner == playerMapIndex) || (selectedMapIndex > 0 && owner == selectedMapIndex);
        }

        var playerCap = -1;
        var selectedCap = -1;
        if (provinceOwners is not null)
            foreach (var capIndex in _cityCapitals)
            {
                if (capIndex < 0 || capIndex >= _cityAnchors.Length) continue;
                var pid = _cityAnchors[capIndex].ProvinceId;
                if (pid <= 0 || pid >= provinceOwners.Length) continue;
                var owner = provinceOwners[pid];
                if (playerCap < 0 && playerMapIndex > 0 && owner == playerMapIndex) playerCap = capIndex;
                if (selectedCap < 0 && selectedMapIndex > 0 && owner == selectedMapIndex) selectedCap = capIndex;
            }

        var globalWorldView = _cityRTree is null || (qFullLon && qMaxLat - qMinLat >= 150f && candidates <= 600);
        if (globalWorldView)
        {
            if (_cachedOrderedAtlasCities is null ||
                _cachedOrderedAtlasCapRev != _capitalRevision ||
                _cachedOrderedAtlasMapRev != mapRev ||
                _cachedOrderedAtlasPlayer != playerMapIndex ||
                _cachedOrderedAtlasSelected != selectedMapIndex ||
                _cachedOrderedAtlasCandidates != candidates ||
                _cachedOrderedAtlasCityCount != _cities.Count ||
                !ReferenceEquals(_cachedOrderedAtlasMap, map))
            {
                _cachedOrderedAtlasCities = MapLabelPriority.OrderCityCandidates(_cityCapitals, playerCap, selectedCap, candidates, IsPriorityCity);
                _cachedOrderedAtlasCapRev = _capitalRevision;
                _cachedOrderedAtlasMapRev = mapRev;
                _cachedOrderedAtlasPlayer = playerMapIndex;
                _cachedOrderedAtlasSelected = selectedMapIndex;
                _cachedOrderedAtlasCandidates = candidates;
                _cachedOrderedAtlasCityCount = _cities.Count;
                _cachedOrderedAtlasMap = map;
            }
        }
        else
        {
            var atlasKey = (_flatMap, qLatB, qLonB, qSpanB, candidates, _cityRTreeVersion,
                _capitalRevision, mapRev, playerMapIndex, selectedMapIndex, _cities.Count);
            if (atlasKey != _cityAtlasBucketKey)
            {
                _cityAtlasBucketKey = atlasKey;
                _cityFrustumAtlasOrder.Clear();
                SearchGeoRTree(_cityRTree, _cityQueryScratch, qMinLat, qMaxLat, qMinLon, qMaxLon, qFullLon);
                for (var qi = 0; qi < _cityQueryScratch.Count; qi++)
                {
                    var idx = _cityQueryScratch[qi];
                    if (idx < candidates || IsCapitalNow(idx)) _cityFrustumAtlasOrder.Add(idx);
                }
                int CityTier(int idx)
                {
                    if (idx == playerCap) return 0;
                    if (idx == selectedCap) return 1;
                    if (IsCapitalNow(idx)) return 2;
                    return IsPriorityCity(idx) ? 3 : 4;
                }
                _cityFrustumAtlasOrder.Sort((a, b) =>
                {
                    var cmp = CityTier(a).CompareTo(CityTier(b));
                    return cmp != 0 ? cmp : a.CompareTo(b);
                });
            }
        }

        var ordered = globalWorldView ? _cachedOrderedAtlasCities : _cityFrustumAtlasOrder;
        var placedOrdinary = 0;
        for (var i = 0; i < ordered.Count && pass.Used < limit; i++)
        {
            var index = ordered[i];
            var capital = IsCapitalNow(index);
            if (!capital && placedOrdinary >= leading && !pass.StarsNamed) NameStars(ref pass);
            if (pass.Used >= limit) break;
            if (!_cityDone.Add(index)) continue;
            if (PlaceCity(index, ref pass, out var room) && capital) _capitalNameBoxes.Add(room);
            if (!capital) placedOrdinary++;
        }
        if (!pass.StarsNamed) NameStars(ref pass);
        PlaceRivers(ref pass);
        EmitFadeOutPins(ref pass);
    }

    private string _cachedCapitalFillHex = "";
    private Color _cachedCapitalFillColor = new(0.98f, 0.92f, 0.72f, 1f);
    private string _cachedRiverFillHex = "";
    private Color _cachedRiverFillColor = new(0.45f, 0.70f, 0.92f, 1f);
    private string _cachedRiverOutlineHex = "";
    private Color _cachedRiverOutlineColor = new(0.02f, 0.032f, 0.048f, 1f);

    private void PlaceRivers(ref CityPassContext pass)
    {
        if (!_mapLookAtlas || _riverAnchors.Count == 0 || !WorldLayers.On("city_labels")) return;
        var measure = _flatMap ? _flatZoom : ViewDistance;
        var riverPixels = Math.Max(10f, pass.TextPixels * 0.92f);
        var riverFont = PresentationFonts.For("map.cities");
        var riverFill = AtlasColourCached("map.look_river_fill", ref _cachedRiverFillHex, ref _cachedRiverFillColor);
        var riverOutline = AtlasColourCached("map.look_river_outline", ref _cachedRiverOutlineHex, ref _cachedRiverOutlineColor);

        ComputeGeoFrustumBounds(pass.Viewport, pass.RootBasis, 5f,
            out var rMinLat, out var rMaxLat, out var rMinLon, out var rMaxLon, out var rFullLon,
            out var rLatB, out var rLonB, out var rSpanB);
        var riverKey = (_flatMap, rLatB, rLonB, rSpanB, _riverRTreeVersion);
        if (riverKey != _riverBucketKey && _riverRTree is not null)
        {
            _riverBucketKey = riverKey;
            SearchGeoRTree(_riverRTree, _riverFrustumOrder, rMinLat, rMaxLat, rMinLon, rMaxLon, rFullLon);
            _riverFrustumOrder.Sort((a, b) => _riverAnchors[b].Rank.CompareTo(_riverAnchors[a].Rank));
        }

        var riverCount = _riverRTree is not null ? _riverFrustumOrder.Count : _riverAnchors.Count;
        for (var ri = 0; ri < riverCount && pass.Used < pass.Limit; ri++)
        {
            var r = _riverRTree is not null ? _riverFrustumOrder[ri] : ri;
            var anchor = _riverAnchors[r];
            var targetAlpha = MapLabelPriority.RiverTargetAlpha(true, _flatMap, measure, anchor.Rank, true);
            if (targetAlpha <= 0.02f) continue;

            var normal = _riverNormals[r];
            if (!_flatMap && (pass.RootBasis * normal).Dot(pass.CameraNormal) < pass.Horizon) continue;
            var local = CachedRiverPosition(r);
            var world = pass.RootTransform * local;
            if (_camera.IsPositionBehind(world)) continue;
            var screen = _camera.UnprojectPosition(world);
            if (screen.X < -40f || screen.Y < -20f || screen.X > pass.Viewport.X + 40f || screen.Y > pass.Viewport.Y + 20f) continue;

            var name = anchor.DisplayName;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var textWidth = CityTextWidth(riverFont, pass.FontSize, name) * riverPixels / pass.FontSize;
            var key = -10000 - r;
            var side = TakeLabelRoom(screen, 0f, textWidth, riverPixels, key, out _);
            if (side == 0) continue;

            var pin = CityPinAt(pass.Used++);
            pin.City = key;
            pin.TargetKey = key;
            pin.Star = null;
            pin.AnchorNormal = normal;
            SetPinNodeName(pin, $"River_{anchor.Key}");
            ApplyPinNameStyle(pin, name, riverFont, pass.FontSize, Math.Max(4, pass.FontSize / 5), fixedSize: !_flatMap);
            SetPinAlignment(pin, HorizontalAlignment.Center, Vector2.Zero);
            var rot = _flatMap ? Quaternion.Identity
                : CountryLabelSurfaceBasis(anchor.Latitude, anchor.Longitude, 1f, 0f).GetRotationQuaternion();
            var pixel = _flatMap ? 1f : pass.FixedPixel;
            var scale = _flatMap ? Vector3.One * pass.FlatPixel : Vector3.One;
            pin.FlatPixelScale = _flatMap ? pass.FlatPixel : -1f;
            ApplyPinNodeTransform(pin, local, rot, scale);
            SetPinNamePixelSize(pin, pixel * riverPixels / pass.FontSize);
            SetPinDotVisible(pin, false);
            SetPinNameVisible(pin, true);
            SetPinNodeVisible(pin, true);
            AssignPinOpacity(pin, key, riverFill, riverOutline, targetAlpha, smooth: true);
        }
    }

    private void EmitFadeOutPins(ref CityPassContext pass)
    {
        if (!_mapLookAtlas || !pass.AllowFadeSnapshots || _fadingPinSnapshots.Count == 0 || (!_flatMap && GlobeViewInMotion)) return;
        var emitted = 0;
        for (var i = 0; i < _fadingPinSnapshots.Count && emitted < 16; i++)
        {
            var snap = _fadingPinSnapshots[i];
            if (_placedPinKeys.Contains(snap.SlotKey)) continue;
            if (_flatMap)
            {
                if (MathF.Abs(snap.Position.X - _flatX) > FlatMapWidth * 0.5f ||
                    _camera.IsPositionBehind(pass.RootTransform * snap.Position))
                    continue;
            }
            else if (MapOverlaySyncRules.IsBeyondGlobeHorizon(
                         ToNum(pass.RootBasis * snap.AnchorNormal), ToNum(pass.CameraNormal), pass.Horizon) ||
                     _camera.IsPositionBehind(pass.RootTransform * snap.Position))
                continue;
            var pin = CityPinAt(pass.Used++);
            emitted++;
            pin.City = snap.SlotKey;
            pin.TargetKey = snap.SlotKey;
            pin.Star = null;
            pin.AnchorNormal = snap.AnchorNormal;
            pin.FlatPixelScale = snap.FlatScale;
            ApplyPinNodeTransform(pin, snap.Position, snap.Rotation, snap.Scale);
            ApplyPinNameStyle(pin, snap.Text, snap.Font, snap.FontSize, snap.OutlineSize, fixedSize: !_flatMap);
            SetPinNamePixelSize(pin, snap.PixelSize);
            SetPinAlignment(pin, snap.Align, snap.Offset);
            SetPinDotVisible(pin, false);
            SetPinNameVisible(pin, true);
            SetPinNodeVisible(pin, true);
            pin.BaseFill = snap.BaseFill;
            pin.BaseOutline = snap.BaseOutline;
            pin.Alpha = snap.Alpha;
            pin.TargetAlpha = 0f;
            _pinAlphas[snap.SlotKey] = snap.Alpha;
            _cityHasActiveFades = true;
            ApplyPinModulate(pin);
        }
    }

    private void AssignPinOpacity(CityPin pin, int key, Color baseFill, Color baseOutline, float targetAlpha, bool smooth)
    {
        pin.BaseFill = baseFill;
        pin.BaseOutline = baseOutline;
        pin.TargetAlpha = targetAlpha;
        if (!smooth)
            pin.Alpha = targetAlpha;
        else
            pin.Alpha = _pinAlphas.TryGetValue(key, out var existing) ? existing : Math.Min(0.18f, targetAlpha);
        _pinAlphas[key] = pin.Alpha;
        _placedPinKeys.Add(key);
        if (MathF.Abs(pin.Alpha - targetAlpha) > 0.002f || targetAlpha <= 0.001f)
            _cityHasActiveFades = true;
        ApplyPinModulate(pin);
    }

    private static void ApplyPinModulate(CityPin pin)
    {
        if (pin.AppliedModulateAlpha >= 0f &&
            MathF.Abs(pin.Alpha - pin.AppliedModulateAlpha) < 0.002f &&
            pin.AppliedModulateFill == pin.BaseFill &&
            pin.AppliedModulateOutline == pin.BaseOutline)
            return;
        pin.AppliedModulateFill = pin.BaseFill;
        pin.AppliedModulateOutline = pin.BaseOutline;
        pin.AppliedModulateAlpha = pin.Alpha;
        pin.Name.Modulate = pin.BaseFill with { A = pin.BaseFill.A * pin.Alpha };
        pin.Name.OutlineModulate = pin.BaseOutline with { A = pin.BaseOutline.A * pin.Alpha };
    }

    private void StepCityPinFades(float delta)
    {
        if (delta <= 0f) return;
        var anyFading = false;
        for (var i = 0; i < _cityPins.Count; i++)
        {
            var pin = _cityPins[i];
            if (!pin.CachedNodeVisible || pin.TargetKey == -1) continue;
            if (MathF.Abs(pin.Alpha - pin.TargetAlpha) > 0.002f)
            {
                pin.Alpha = MapLabelPriority.StepOpacity(pin.Alpha, pin.TargetAlpha, delta);
                _pinAlphas[pin.TargetKey] = pin.Alpha;
                ApplyPinModulate(pin);
                anyFading = true;
            }
            if (pin.TargetAlpha <= 0.001f && pin.Alpha <= 0.01f)
            {
                SetPinNameVisible(pin, false);
                if (!pin.CachedDotVisible) SetPinNodeVisible(pin, false);
                _pinAlphas.Remove(pin.TargetKey);
                pin.TargetKey = -1;
            }
            else if (MathF.Abs(pin.Alpha - pin.TargetAlpha) > 0.002f)
            {
                anyFading = true;
            }
        }
        _cityHasActiveFades = anyFading;
    }

    private void NameStars(ref CityPassContext pass)
    {
        pass.StarsNamed = true;
        for (var star = 0; star < _starNames.Count && pass.Used < pass.Limit; star++)
        {
            var (marker, normal, city, _, capName) = _starNames[star];
            if (_starScreens[star] is not { } screen) continue;
            // The city the star stands on letters it, when that city is drawn at this zoom.
            if (city >= 0 && (city < pass.Candidates || IsCapitalNow(city))) continue;
            if (!_flatMap && MapOverlaySyncRules.IsBeyondGlobeHorizon(
                    ToNum(pass.RootBasis * normal), ToNum(pass.CameraNormal), pass.Horizon))
                continue;
            var name = capName;
            if (string.IsNullOrWhiteSpace(name) && city >= 0) name = _cities[city].DisplayName;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var tierPixels = pass.CapitalTextPixels;
            var textWidth = CityTextWidth(pass.Font, pass.FontSize, name) * tierPixels / pass.FontSize;
            // The name starts past the star's ring, not under it ("rague", "ienna").
            var starSize = pass.CapitalRingPixels;
            var side = TakeLabelRoom(screen, starSize, textWidth, tierPixels, city, out _);
            if (side == 0) continue;

            var pin = CityPinAt(pass.Used++);
            var key = -2 - star;
            if (pin.City != key || !string.Equals(pin.CachedText, name, StringComparison.Ordinal))
                SetUpStarPin(pin, star, marker, name, pass.Font, pass.FontSize);
            pin.TargetKey = key;
            pin.AnchorNormal = normal;
            var geo = GeographicFromNormal(normal);
            var pos = CityLabelPosition(geo.Latitude, geo.Longitude);
            var rot = _flatMap ? Quaternion.Identity
                : CountryLabelSurfaceBasis(geo.Latitude, geo.Longitude, 1f, 0f).GetRotationQuaternion();
            var pixel = _flatMap ? 1f : pass.FixedPixel;
            var scale = _flatMap ? Vector3.One * pass.FlatPixel : Vector3.One;
            pin.FlatPixelScale = _flatMap ? pass.FlatPixel : -1f;
            ApplyPinNodeTransform(pin, pos, rot, scale);
            SetPinNamePixelSize(pin, pixel * tierPixels / pass.FontSize);
            SetLabelSide(pin, side, starSize, pass.FontSize, tierPixels);
            SetPinDotVisible(pin, false);
            SetPinNameVisible(pin, true);
            SetPinNodeVisible(pin, true);
            AssignPinOpacity(pin, key, pass.CapitalColor, pass.OutlineColor, 1f, _mapLookAtlas);
            _placedCityNames.Add(name);
        }
    }

    private bool PlaceCity(int index, ref CityPassContext pass, out Rect2 room)
    {
        room = default;
        var city = _cities[index];
        var anchorAt = _cityAnchors[index];
        if (CityPinDestroyed(anchorAt.Latitude, anchorAt.Longitude)) return false;
        if (!_flatMap && MapOverlaySyncRules.IsBeyondGlobeHorizon(
                ToNum(pass.RootBasis * _cityNormals[index]), ToNum(pass.CameraNormal), pass.Horizon))
            return false;
        var local = CachedCityPosition(index);
        var world = pass.RootTransform * local;
        if (_camera.IsPositionBehind(world)) return false;
        var screen = _camera.UnprojectPosition(world);
        if (screen.X < -40f || screen.Y < -20f || screen.X > pass.Viewport.X + 40f || screen.Y > pass.Viewport.Y + 20f) return false;

        // One lettering and one dot for every city. A capital differs by its mark and its colour,
        // and only for as long as it is one.
        var capital = IsCapitalNow(index);
        var tierPixels = capital ? pass.CapitalTextPixels : pass.TextPixels;
        var name = city.DisplayName;
        var textWidth = CityTextWidth(pass.Font, pass.FontSize, name) * tierPixels / pass.FontSize;
        var dot = capital ? pass.CapitalMarkPixels : pass.DotPixels;
        // A capital's star and its ink ring stand on the city: the name is set beyond the ring.
        // Offset by the small capital dot only, the ring covered the first letters of Prague
        // and Vienna at close zoom (23 November 2026, round 2).
        var ringed = capital && (StarStandsOn(index) || NearCapitalMarker(index));
        var labelMark = ringed ? Math.Max(dot, pass.CapitalRingPixels) : dot;
        var side = TakeLabelRoom(screen, labelMark, textWidth, tierPixels, index, out room);
        var pin = CityPinAt(pass.Used++);
        var samePinCity = pin.City == index && pin.CachedTransformValid && pin.CachedFixedSize == !_flatMap;
        if (pin.City != index || !string.Equals(pin.CachedText, name, StringComparison.Ordinal))
            SetUpCityPin(pin, index, name, pass.Font, pass.FontSize);
        pin.TargetKey = index;
        pin.AnchorNormal = _cityNormals[index];
        var rot = _flatMap ? Quaternion.Identity
            : (samePinCity
                ? pin.CachedRotation
                : CountryLabelSurfaceBasis(_cityAnchors[index].Latitude, _cityAnchors[index].Longitude, 1f, 0f).GetRotationQuaternion());
        var pixel = _flatMap ? 1f : pass.FixedPixel;
        var scale = _flatMap ? Vector3.One * pass.FlatPixel : Vector3.One;
        pin.FlatPixelScale = _flatMap ? pass.FlatPixel : -1f;
        ApplyPinNodeTransform(pin, local, rot, scale);
        SetPinNamePixelSize(pin, pixel * tierPixels / pass.FontSize);
        var fill = capital ? pass.CapitalColor : pass.CityColor;
        var fadeTail = _mapLookAtlas && pass.AllowFadeSnapshots && side == 0 && _pinAlphas.TryGetValue(index, out var prevAlpha) && prevAlpha > 0.03f;
        SetPinNameVisible(pin, side != 0 || fadeTail);
        if (side != 0) SetLabelSide(pin, side, labelMark, pass.FontSize, tierPixels);
        var dotPixelSize = pixel * dot / 32f;
        if (MathF.Abs(dotPixelSize - pin.CachedDotPixelSize) > 1e-6f)
        {
            pin.CachedDotPixelSize = dotPixelSize;
            pin.Dot.PixelSize = dotPixelSize;
        }
        var mark = capital ? _cityCapitalTexture : _cityDotTexture;
        if (!ReferenceEquals(pin.CachedDotTexture, mark))
        {
            pin.CachedDotTexture = mark;
            pin.Dot.Texture = mark;
        }
        var dotVisible = !(capital && NearCapitalMarker(index)) && !StarStandsOn(index);
        SetPinDotVisible(pin, dotVisible);
        if (side != 0)
        {
            AssignPinOpacity(pin, index, fill, pass.OutlineColor, _mapLookAtlas ? 0.94f : 1f, _mapLookAtlas);
            _placedCityNames.Add(name);
        }
        else
            AssignPinOpacity(pin, index, fill, pass.OutlineColor, 0f, _mapLookAtlas);
        if (!pin.CachedDotModulateInit)
        {
            pin.CachedDotModulateInit = true;
            pin.Dot.Modulate = Colors.White;
        }
        SetPinNodeVisible(pin, true);
        // A crowded name may be omitted, but its geographical point must remain.
        return side != 0;
    }

    private void ReserveSurfaceCard(Sprite3D plate, Vector2I viewport)
    {
        if (!IsInstanceValid(plate) || !plate.IsInsideTree() || !plate.Visible || plate.Texture is null || !plate.IsVisibleInTree() ||
            !IsInstanceValid(_camera) || !_camera.IsInsideTree() || _camera.IsPositionBehind(plate.GlobalPosition)) return;
        var bounds = plate.GetAabb();
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        for (var corner = 0; corner < 4; corner++)
        {
            var local = new Vector3(corner % 2 == 0 ? bounds.Position.X : bounds.End.X,
                corner < 2 ? bounds.Position.Y : bounds.End.Y, 0f);
            var world = plate.GlobalTransform * local;
            if (_camera.IsPositionBehind(world)) return;
            var screen = _camera.UnprojectPosition(world);
            min = min.Min(screen); max = max.Max(screen);
        }
        var box = new Rect2(min, max - min).Grow(4f);
        if (box.Intersects(new Rect2(Vector2.Zero, new Vector2(viewport.X, viewport.Y)))) _cityTaken.Add(box);
    }

    /// <summary>Where a capital's star is drawn on the screen, or nothing when it is not.</summary>
    private Vector2? StarOnScreen(Node3D marker, Vector3 normal, Vector2I viewport, Basis rootBasis,
        Vector3 cameraNormal, float limb)
    {
        if (!IsInstanceValid(marker) || !marker.IsInsideTree() || !marker.IsVisibleInTree() ||
            !IsInstanceValid(_camera) || !_camera.IsInsideTree()) return null;
        if (!_flatMap && (rootBasis * normal).Dot(cameraNormal) < limb) return null;
        var world = marker.GlobalPosition;
        if (_camera.IsPositionBehind(world)) return null;
        var screen = _camera.UnprojectPosition(world);
        if (screen.X < -40f || screen.Y < -20f || screen.X > viewport.X + 40f || screen.Y > viewport.Y + 20f) return null;
        return screen;
    }

    /// <summary>
    /// Room for a mark and its name: to the right of the mark, as a name is set, or - where the
    /// right is taken - to its left, then centred above it, then below, the way an atlas sets a
    /// name with no room on its usual side. Returns the side taken (1 right, -1 left, 2 above,
    /// -2 below) with the room marked as taken, or 0 when none is free. A capital in the Leeward
    /// Islands had its star and nothing else, because the one side a name was ever tried on was
    /// its neighbour's.
    /// </summary>
    private int TakeLabelRoom(Vector2 screen, float mark, float textWidth, float textHeight, int city, out Rect2 room)
    {
        room = default;
        // The mark and the name beside it, with a little air around both.
        var width = mark + 5f + textWidth + 4f;
        var top = screen.Y - textHeight * 0.62f;
        var right = new Rect2(screen.X - mark * 0.5f - 2f, top, width, textHeight * 1.24f);
        var left = new Rect2(screen.X + mark * 0.5f + 2f - width, top, width, textHeight * 1.24f);
        if (Free(right)) { _cityTaken.Add(room = right); return 1; }
        if (Free(left)) { _cityTaken.Add(room = left); return -1; }
        // Above and below: the name centred over the mark, the box reaching down (or up) to
        // take the mark in too.
        var half = Math.Max(textWidth * 0.5f + 4f, mark * 0.5f + 2f);
        var tall = textHeight * 1.24f + mark * 0.5f + 2f;
        var above = new Rect2(screen.X - half, screen.Y + mark * 0.5f + 2f - tall - textHeight * 0.1f, half * 2f, tall + textHeight * 0.1f);
        if (Free(above)) { _cityTaken.Add(room = above); return 2; }
        var below = new Rect2(screen.X - half, screen.Y - mark * 0.5f - 2f, half * 2f, tall + textHeight * 0.1f);
        if (Free(below)) { _cityTaken.Add(room = below); return -2; }
        return 0;

        bool Free(Rect2 box)
        {
            foreach (var taken in _cityTaken)
                if (taken.Intersects(box)) return false;
            // A star is a mark as well; its own name, or its city's, may of course stand by it.
            foreach (var (star, starCity) in _starMarks)
            {
                if ((city >= 0 && starCity == city) || star.DistanceSquaredTo(screen) < 4f) continue;
                if (box.Intersects(new Rect2(star.X - 6f, star.Y - 6f, 12f, 12f))) return false;
            }
            return true;
        }
    }

    private static void SetPinNodeName(CityPin pin, string name)
    {
        if (!string.Equals(pin.CachedNodeName, name, StringComparison.Ordinal))
        {
            pin.CachedNodeName = name;
            pin.Node.Name = name;
        }
    }

    private static void SetPinNodeVisible(CityPin pin, bool visible)
    {
        if (pin.CachedNodeVisible != visible)
        {
            pin.CachedNodeVisible = visible;
            pin.Node.Visible = visible;
        }
    }

    private static void SetPinNameVisible(CityPin pin, bool visible)
    {
        if (pin.CachedNameVisible != visible)
        {
            pin.CachedNameVisible = visible;
            pin.Name.Visible = visible;
        }
    }

    private static void SetPinDotVisible(CityPin pin, bool visible)
    {
        if (pin.CachedDotVisible != visible)
        {
            pin.CachedDotVisible = visible;
            pin.Dot.Visible = visible;
        }
    }

    private static void SetPinAlignment(CityPin pin, HorizontalAlignment align, Vector2 offset)
    {
        if (pin.CachedAlign != align)
        {
            pin.CachedAlign = align;
            pin.Name.HorizontalAlignment = align;
        }
        if (pin.CachedOffset != offset)
        {
            pin.CachedOffset = offset;
            pin.Name.Offset = offset;
        }
    }

    private static void SetPinNamePixelSize(CityPin pin, float pixelSize)
    {
        if (MathF.Abs(pixelSize - pin.CachedNamePixelSize) > 1e-6f)
        {
            pin.CachedNamePixelSize = pixelSize;
            pin.Name.PixelSize = pixelSize;
        }
    }

    private static void ApplyPinNodeTransform(CityPin pin, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        if (!pin.CachedTransformValid || pin.CachedPosition != position)
        {
            pin.CachedPosition = position;
            pin.Node.Position = position;
        }
        if (!pin.CachedTransformValid || pin.CachedRotation != rotation)
        {
            pin.CachedRotation = rotation;
            pin.Node.Quaternion = rotation;
        }
        if (!pin.CachedTransformValid || pin.CachedScale != scale)
        {
            pin.CachedScale = scale;
            pin.Node.Scale = scale;
        }
        pin.CachedTransformValid = true;
    }

    private static void ApplyPinNameStyle(CityPin pin, string text, Font font, int fontSize, int outlineSize, bool fixedSize)
    {
        if (!string.Equals(pin.CachedText, text, StringComparison.Ordinal))
        {
            pin.CachedText = text;
            pin.Name.Text = text;
        }
        if (!ReferenceEquals(pin.CachedFont, font))
        {
            pin.CachedFont = font;
            pin.Name.Font = font;
        }
        if (pin.CachedFontSize != fontSize)
        {
            pin.CachedFontSize = fontSize;
            pin.Name.FontSize = fontSize;
        }
        if (pin.CachedOutlineSize != outlineSize)
        {
            pin.CachedOutlineSize = outlineSize;
            pin.Name.OutlineSize = outlineSize;
        }
        if (pin.CachedFixedSize != fixedSize)
        {
            pin.CachedFixedSize = fixedSize;
            pin.Name.FixedSize = fixedSize;
            pin.Dot.FixedSize = fixedSize;
        }
    }

    /// <summary>The name set on the side <see cref="TakeLabelRoom"/> found for it.</summary>
    private static void SetLabelSide(CityPin pin, int side, float mark, int fontSize, float textPixels)
    {
        var toPixels = fontSize / textPixels;
        var align = side switch
        {
            1 => HorizontalAlignment.Left,
            -1 => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center
        };
        var offset = Math.Abs(side) == 1
            ? new Vector2(side * (mark * 0.5f + 4f) * toPixels, 0f)
            : new Vector2(0f, Math.Sign(side) * (mark * 0.5f + 2f + textPixels * 0.62f) * toPixels);
        SetPinAlignment(pin, align, offset);
    }

    private CityPin CityPinAt(int slot)
    {
        while (_cityPins.Count <= slot)
        {
            var node = new Node3D { Name = $"City{_cityPins.Count}", Visible = false };
            var dot = new Sprite3D
            {
                // The dot lies on the ground like the capital's star; only the name turns to be read.
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                NoDepthTest = true,
                AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                RenderPriority = 1,
                Modulate = Colors.White
            };
            var label = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                RenderPriority = 1,
                OutlineRenderPriority = 0
            };
            node.AddChild(dot);
            node.AddChild(label);
            _cityRoot.AddChild(node);
            _cityPins.Add(new CityPin
            {
                Node = node,
                Dot = dot,
                Name = label,
                CachedNodeName = $"City{_cityPins.Count}",
                CachedAlign = HorizontalAlignment.Left,
                CachedOffset = Vector2.Zero,
                CachedDotModulateInit = true
            });
        }
        return _cityPins[slot];
    }

    private void SetUpCityPin(CityPin pin, int index, string name, Font font, int fontSize)
    {
        var city = _cities[index];
        var wasCity = pin.City;
        pin.City = index;
        pin.Star = null;
        SetPinNodeName(pin, $"City_{city.Id}");
        if (wasCity != index)
        {
            var anchor = _cityAnchors[index];
            pin.Node.SetMeta("province_id", anchor.ProvinceId);
            pin.Node.SetMeta("map_latlon", new Vector2(anchor.Latitude, anchor.Longitude));
        }
        var outlineRatio = Math.Clamp(InterfaceSkin.Metric("map.city_outline_ratio", 0.14f), 0.06f, 0.30f);
        var outlineSize = Math.Max(2, (int)MathF.Round(fontSize * outlineRatio));
        ApplyPinNameStyle(pin, name, font, fontSize, outlineSize, fixedSize: !_flatMap);
    }

    /// <summary>
    /// How far round the globe from the camera a city may be and still be named, as the cosine
    /// of the angle. The limb itself is where that cosine is the globe's radius over the camera's
    /// distance; a fixed number stood in for it and, closer than about eleven units - or with a
    /// free camera's wider lens - let through cities already behind the globe. A little short of
    /// the limb, too, because the ground there is seen edge-on and its names pile up.
    /// </summary>
    private float CityHorizon()
    {
        var dist = IsInstanceValid(_camera) && _camera.IsInsideTree() ? _camera.GlobalPosition.Length() : _distance;
        return MapOverlaySyncRules.GlobeHorizonCosine(GlobeRadius, dist, 0.014f);
    }

    private void HideCitiesPastHorizon() => CullCitiesPastHorizonImmediate();

    /// <summary>
    /// Which cities are capitals today, and which city each capital's star stands on: the city
    /// the country's government sits in (CapitalCore.SeatCity) - and, for a capital the map has
    /// no city for, the nearest city within the distance a star hides a dot at, if any. Only when
    /// the stars have been rebuilt - a new campaign, a capital moved or a country born - and then
    /// once over the list.
    /// </summary>
    private void RefreshStarNames(bool allowCampaignRead = true)
    {
        var first = _capitalMarkers.Count > 0 && IsInstanceValid(_capitalMarkers[0].Node)
            ? _capitalMarkers[0].Node.GetInstanceId() : 0ul;
        var key = (_capitalRevision, _capitalMarkers.Count, first, Loc.Revision, Loc.Language ?? "");
        if (key == _starNamesBuilt) return;
        if (!allowCampaignRead && _campaign is not null)
        {
            if (!_campaign.TryEnterRead(0, false)) return;
            try
            {
                _starNamesBuilt = key;
                RebuildStarNamesCore();
            }
            finally
            {
                _campaign.ExitRead();
            }
            return;
        }
        _starNamesBuilt = key;
        RebuildStarNamesCore();
    }

    /// <summary>Whether a city is some country's seat of government today.</summary>
    private bool IsCapitalNow(int city) => city >= 0 && city < _cityIsCapital.Length && _cityIsCapital[city];

    /// <summary>What the country calls its capital, as the country picker says it.</summary>
    private string StarCapitalName(Nation nation)
    {
        return nation is null ? "" : CountryNames.Capital(nation, _campaign);
    }

    /// <summary>Whether a drawn capital star stands on this city, which then needs no dot of its own.</summary>
    private bool StarStandsOn(int index) => _visibleStarCitiesScratch.Contains(index);

    private void SetUpStarPin(CityPin pin, int star, Node3D marker, string name, Font font, int fontSize)
    {
        pin.City = -2 - star;
        pin.Star = marker;
        SetPinNodeName(pin, $"Star_{star}");
        var outlineRatio = Math.Clamp(InterfaceSkin.Metric("map.city_outline_ratio", 0.14f), 0.06f, 0.30f);
        var outlineSize = Math.Max(2, (int)MathF.Round(fontSize * outlineRatio));
        ApplyPinNameStyle(pin, name, font, fontSize, outlineSize, fixedSize: !_flatMap);
    }

    /// <summary>A capital's own star already marks this city: its dot would sit under it.</summary>
    private bool NearCapitalMarker(int index)
    {
        var normal = _cityNormals[index];
        // Drawn, not merely switched on: the stars go as a group farther out than the capitals'
        // names do, and a dot hidden under a star nobody can see left the capital unmarked.
        for (var i = 0; i < _visibleCapitalNormalsScratch.Count; i++)
            if (_visibleCapitalNormalsScratch[i].Dot(normal) > 0.99995f) return true;
        return false;
    }

    /// <summary>Everything about the pins is redone on the next frame - projection, language, setting.</summary>
    private void InvalidateCities()
    {
        _cityDirty = true;
        _cachedOrderedAtlasCandidates = -1;
        _lastCityFlatZoom = -1f;
        _lastCityFlatPixelScale = -1f;
        _lastCityCamPos = new Vector3(float.NaN, float.NaN, float.NaN);
        _lastCityCamBasis = Basis.Identity;
        _cityHasActiveFades = true;
        foreach (var pin in _cityPins)
        {
            pin.City = -1;
            pin.TargetKey = -1;
            pin.AnchorNormal = Vector3.Zero;
            pin.CachedNamePixelSize = -1f;
            pin.CachedDotPixelSize = -1f;
            pin.CachedFixedSize = null;
            pin.CachedTransformValid = false;
            pin.AppliedModulateAlpha = -1f;
            SetPinNodeVisible(pin, false);
        }
    }

    private void EnsureCityPositionCache()
    {
        var cityCount = _cities.Count;
        var riverCount = _riverAnchors.Count;
        var reliefChanged = !Mathf.IsEqualApprox(_cityPosRelief, _reliefDisplacement) ||
                            !ReferenceEquals(_cityPosReliefGrid, _reliefHeightGrid);
        var cityResized = _cityGlobePositions.Length != cityCount;
        var riverResized = _riverGlobePositions.Length != riverCount;
        if (!reliefChanged && !cityResized && !riverResized) return;

        if (cityResized)
        {
            _cityGlobePositions = new Vector3[cityCount];
            _cityFlatBasePositions = new Vector3[cityCount];
            _cityGlobePosValid = new bool[cityCount];
            _cityFlatPosValid = new bool[cityCount];
        }
        else if (reliefChanged)
        {
            Array.Clear(_cityGlobePosValid);
            Array.Clear(_cityFlatPosValid);
        }

        if (riverResized)
        {
            _riverGlobePositions = new Vector3[riverCount];
            _riverFlatBasePositions = new Vector3[riverCount];
            _riverGlobePosValid = new bool[riverCount];
            _riverFlatPosValid = new bool[riverCount];
        }
        else if (reliefChanged)
        {
            Array.Clear(_riverGlobePosValid);
            Array.Clear(_riverFlatPosValid);
        }

        _cityPosRelief = _reliefDisplacement;
        _cityPosReliefGrid = _reliefHeightGrid;
    }

    private Vector3 WrapCanonicalFlatPoint(Vector3 canonical)
    {
        var x = canonical.X;
        while (x - _flatX > FlatMapWidth * 0.5f) x -= FlatMapWidth;
        while (x - _flatX < -FlatMapWidth * 0.5f) x += FlatMapWidth;
        return new Vector3(x, canonical.Y, canonical.Z);
    }

    private Vector3 CachedCityPosition(int index)
    {
        EnsureCityPositionCache();
        var anchor = _cityAnchors[index];
        return CachedGeoLabelPosition(
            anchor.Latitude, anchor.Longitude, index,
            _cityFlatPosValid, _cityFlatBasePositions, _cityGlobePosValid, _cityGlobePositions, 0.055f);
    }

    private Vector3 CachedRiverPosition(int index)
    {
        EnsureCityPositionCache();
        var anchor = _riverAnchors[index];
        return CachedGeoLabelPosition(
            anchor.Latitude, anchor.Longitude, index,
            _riverFlatPosValid, _riverFlatBasePositions, _riverGlobePosValid, _riverGlobePositions, 0.055f);
    }

    private Vector3 CachedGeoLabelPosition(
        float latitude, float longitude, int index,
        bool[] flatValid, Vector3[] flatBase, bool[] globeValid, Vector3[] globePos, float activeFootprintRadius)
    {
        var reliefActive = _reliefDisplacement > 0.0001f;
        var reliefBias = reliefActive ? 0.005f : 0f;
        var footprintRadius = reliefActive ? activeFootprintRadius : 0f;
        if (_flatMap)
        {
            if (!flatValid[index])
            {
                var z = FlatTextZ + reliefBias + MaxReliefLiftAround(latitude, longitude, footprintRadius, flat: true);
                flatBase[index] = new Vector3(longitude / 360f * FlatMapWidth, latitude / 180f * FlatMapHeight, z);
                flatValid[index] = true;
            }
            return WrapCanonicalFlatPoint(flatBase[index]);
        }
        if (!globeValid[index])
        {
            globePos[index] = LocalSurfaceLabelPoint(latitude, longitude, SurfaceTextRadius + reliefBias, footprintRadius);
            globeValid[index] = true;
        }
        return globePos[index];
    }

    private Vector3 CityLabelPosition(float latitude, float longitude)
    {
        var reliefActive = _reliefDisplacement > 0.0001f;
        var reliefBias = reliefActive ? 0.005f : 0f;
        var footprintRadius = reliefActive ? 0.055f : 0f;
        return _flatMap
            ? LocalFlatLabelPoint(latitude, longitude, FlatTextZ + reliefBias, footprintRadius)
            : LocalSurfaceLabelPoint(latitude, longitude, SurfaceTextRadius + reliefBias, footprintRadius);
    }
}
