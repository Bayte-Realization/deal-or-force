using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The cities on the map (item 91): a dot and a name, the way Hearts of Iron marks its cities and
/// Age of History its provinces.
///
/// There are five and a half thousand of them and a screen has room for a hundred and fifty
/// names, so the map does what an atlas editor does: it takes the cities in order of importance -
/// capitals and world cities first, then by population - and sets each name only if it has room,
/// never over another. Which cities are candidates at all follows the zoom: from far out only the
/// capitals and the great cities, closer in the regional centres, and the towns only on a region's
/// own screen.
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
    private readonly List<Rect2> _cityTaken = new();
    private double _cityClock;
    private Transform3D _citySeenCamera;
    private float _citySeenSize = -1f;
    private Vector2I _citySeenViewport;
    private bool _citySeenFlat;
    private bool _cityDirty = true;
    private bool _cityLabelsEnabled = true;
    private string _citySeenLanguage = "";
    private ImageTexture _cityDotTexture;
    private ImageTexture _cityRegionalTexture;
    private ImageTexture _cityCapitalTexture;

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
        public int City = -1;
    }

    private void CreateCityLayer(Node3D world)
    {
        _cityRoot = new Node3D { Name = "Cities" };
        world.AddChild(_cityRoot);
        _cityDotTexture = CityDotTexture(false, false);
        _cityRegionalTexture = CityDotTexture(true, false);
        _cityCapitalTexture = CityDotTexture(true, true);
        try
        {
            _cities = CityCatalog.All;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[dof] cities not loaded: {exception.Message}");
            _cities = Array.Empty<CityDefinition>();
        }
        _cityNormals = new Vector3[_cities.Count];
        for (var index = 0; index < _cities.Count; index++)
            _cityNormals[index] = SurfacePoint(_cities[index].Latitude, _cities[index].Longitude, 1f).Normalized();
    }

    /// <summary>
    /// A dot drawn once: a pale disc in a dark ring for a town, a ringed one for a regional
    /// capital, and a gold one for a seat of government.
    /// </summary>
    private static ImageTexture CityDotTexture(bool ringed, bool capital)
    {
        const int size = 32;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var centre = (size - 1) / 2f;
        var fill = capital ? new Color(0.98f, 0.84f, 0.42f) : new Color(0.93f, 0.95f, 0.96f);
        var ink = new Color(0.05f, 0.07f, 0.09f);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var distance = new Vector2(x - centre, y - centre).Length();
            Color colour;
            if (distance <= 7.5f) colour = fill;
            else if (ringed && distance > 9.5f && distance <= 12.5f) colour = fill;
            else if (distance <= (ringed ? 14.5f : 10.5f)) colour = ink;
            else continue;
            // A pixel of antialiasing on the outer edge.
            var edge = (ringed ? 14.5f : 10.5f) - distance;
            colour.A = Mathf.Clamp(edge + 0.5f, 0f, 1f);
            image.SetPixel(x, y, colour);
        }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>The farthest out each tier of city is drawn, as a view distance.</summary>
    private static float CityTierDistance(int tier) => tier switch
    {
        0 => InterfaceSkin.Metric("map.city_tier0_distance", 15.0f),
        1 => InterfaceSkin.Metric("map.city_tier1_distance", 11.0f),
        2 => InterfaceSkin.Metric("map.city_tier2_distance", 8.4f),
        _ => InterfaceSkin.Metric("map.city_tier3_distance", 6.6f)
    };

    private void UpdateCityLabels(double delta)
    {
        if (!IsInstanceValid(_cityRoot) || !IsInstanceValid(_camera)) return;
        var wanted = CityLabelsEnabled && _mapApplied && _cities.Count > 0 &&
                     ViewDistance <= CityTierDistance(0);
        _cityRoot.Visible = wanted;
        if (!wanted) return;

        _cityClock += delta;
        var viewport = ViewportSize();
        var cameraTransform = _camera.GlobalTransform;
        var moved = _citySeenFlat != _flatMap || _citySeenViewport != viewport ||
                    !cameraTransform.IsEqualApprox(_citySeenCamera) ||
                    Mathf.Abs(_camera.Size - _citySeenSize) > 0.0001f;
        var language = Loc.Language;
        if (language != _citySeenLanguage) { _citySeenLanguage = language; InvalidateCities(); }
        // Fixed-size pins on the globe, pixel-scaled ones on the flat map: a switch redoes them all.
        if (_citySeenFlat != _flatMap) InvalidateCities();
        if (!_cityDirty && (!moved || _cityClock < 0.1)) return;
        _cityClock = 0;
        _cityDirty = false;
        _citySeenCamera = cameraTransform;
        _citySeenSize = _camera.Size;
        _citySeenViewport = viewport;
        _citySeenFlat = _flatMap;
        using (SimProfiler.Measure("f:cities")) PlaceCities(viewport);
    }

    private void PlaceCities(Vector2I viewport)
    {
        var limit = Math.Clamp((int)InterfaceSkin.Metric("map.city_label_limit", 140f), 0, 400);
        var fontSize = Math.Clamp((int)InterfaceSkin.Metric("map.city_font_size", 28f), 12, 64);
        var textPixels = Math.Clamp(InterfaceSkin.Metric("map.city_text_pixels", 13f), 8f, 28f);
        var font = PresentationFonts.For("map.cities");
        var distance = ViewDistance;
        var tiers = 0;
        while (tiers < 3 && distance <= CityTierDistance(tiers + 1)) tiers++;

        // World units per screen pixel: on the flat map the camera's own height over the
        // viewport; on the globe, a fixed-size label's pixel size for the field of view.
        var fixedPixel = 2f * Mathf.Tan(Mathf.DegToRad(_camera.Fov * 0.5f)) / Math.Max(1, viewport.Y);
        var flatPixel = _camera.Size / Math.Max(1, viewport.Y);
        var cameraNormal = _flatMap ? Vector3.Zero : _camera.GlobalPosition.Normalized();
        var rootBasis = _cityRoot.GlobalTransform.Basis;
        var horizon = distance > 11f ? 0.55f : distance > 8f ? 0.42f : 0.30f;

        _cityTaken.Clear();
        _movementLayer?.AddOccupied(_cityTaken);
        var used = 0;
        var examined = 0;
        for (var index = 0; index < _cities.Count && used < limit; index++)
        {
            var city = _cities[index];
            if (city.Tier > tiers) break;
            if (++examined > 4000) break;
            Vector3 local;
            if (_flatMap)
                local = FlatPoint(city.Latitude, city.Longitude, FlatMarkerZ);
            else
            {
                if ((rootBasis * _cityNormals[index]).Dot(cameraNormal) < horizon) continue;
                local = _cityNormals[index] * SurfaceMarkerRadius;
            }
            var world = _cityRoot.GlobalTransform * local;
            if (_camera.IsPositionBehind(world)) continue;
            var screen = _camera.UnprojectPosition(world);
            if (screen.X < -40f || screen.Y < -20f || screen.X > viewport.X + 40f || screen.Y > viewport.Y + 20f) continue;

            var tierPixels = textPixels * (city.Tier switch { 0 => 1.22f, 1 => 1.08f, _ => 1f });
            var name = city.DisplayName;
            var textWidth = font.GetStringSize(name, HorizontalAlignment.Left, -1, fontSize).X * tierPixels / fontSize;
            var dot = city.Tier == 0 ? 9f : 7f;
            // The dot and the name to its right, with a little air around both.
            var box = new Rect2(screen.X - dot * 0.5f - 2f, screen.Y - tierPixels * 0.62f,
                dot + 5f + textWidth + 4f, tierPixels * 1.24f);
            var clear = true;
            foreach (var taken in _cityTaken)
                if (taken.Intersects(box)) { clear = false; break; }
            if (!clear) continue;
            _cityTaken.Add(box);

            var pin = CityPinAt(used++);
            if (pin.City != index || pin.Name.Text != name) SetUpCityPin(pin, index, name, font, fontSize);
            pin.Node.Position = local;
            var pixel = _flatMap ? 1f : fixedPixel;
            pin.Node.Scale = _flatMap ? Vector3.One * flatPixel : Vector3.One;
            pin.Name.PixelSize = pixel * tierPixels / fontSize;
            pin.Name.Offset = new Vector2((dot * 0.5f + 4f) * fontSize / tierPixels, 0f);
            pin.Dot.PixelSize = pixel * dot / 32f;
            pin.Dot.Visible = !(city.IsNationalCapital && NearCapitalMarker(index));
            pin.Node.Visible = true;
        }
        for (var index = used; index < _cityPins.Count; index++)
            if (_cityPins[index].Node.Visible) _cityPins[index].Node.Visible = false;
    }

    private CityPin CityPinAt(int slot)
    {
        while (_cityPins.Count <= slot)
        {
            var node = new Node3D { Name = $"City{_cityPins.Count}", Visible = false };
            var dot = new Sprite3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                RenderPriority = 1
            };
            var label = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                RenderPriority = 1,
                OutlineRenderPriority = 0
            };
            node.AddChild(dot);
            node.AddChild(label);
            _cityRoot.AddChild(node);
            _cityPins.Add(new CityPin { Node = node, Dot = dot, Name = label });
        }
        return _cityPins[slot];
    }

    private void SetUpCityPin(CityPin pin, int index, string name, Font font, int fontSize)
    {
        var city = _cities[index];
        pin.City = index;
        pin.Node.Name = $"City_{city.Id}";
        pin.Name.Text = name;
        pin.Name.Font = font;
        pin.Name.FontSize = fontSize;
        pin.Name.OutlineSize = Math.Max(4, fontSize / 4);
        pin.Name.FixedSize = !_flatMap;
        pin.Dot.FixedSize = !_flatMap;
        pin.Name.Modulate = city.IsNationalCapital ? Ui.Colour("globe.city_capital") : Ui.Colour("globe.city");
        pin.Name.OutlineModulate = Ui.Colour("globe.city_outline");
        pin.Dot.Texture = city.IsNationalCapital ? _cityCapitalTexture
            : city.Capital == "regional" ? _cityRegionalTexture : _cityDotTexture;
    }

    /// <summary>A capital's own star already marks this city: its dot would sit under it.</summary>
    private bool NearCapitalMarker(int index)
    {
        var normal = _cityNormals[index];
        foreach (var (node, markerNormal) in _capitalMarkers)
            if (IsInstanceValid(node) && node.Visible && markerNormal.Dot(normal) > 0.99995f) return true;
        return false;
    }

    /// <summary>Everything about the pins is redone on the next frame - projection, language, setting.</summary>
    private void InvalidateCities()
    {
        _cityDirty = true;
        foreach (var pin in _cityPins) pin.City = -1;
    }
}
