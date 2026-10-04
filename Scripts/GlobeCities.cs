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
        /// <summary>The city in the catalogue; -2 - n for the n-th star named at the star.</summary>
        public int City = -1;
        /// <summary>The capital's star a name stands beside, when it is one of those.</summary>
        public Node3D Star;
    }

    /// <summary>
    /// Every capital star, with the city beside it (-1 for none within the distance a star hides
    /// a dot at) and whose star it is. Rebuilt when the stars are.
    /// </summary>
    private readonly List<(Node3D Marker, Vector3 Normal, int City, Nation Nation)> _starNames = new();
    private (int Revision, int Count, ulong First) _starNamesBuilt = (-1, -1, 0);
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
        if (!wanted)
        {
            // Hidden capitals must not reserve old screen rectangles after zooming out.
            _capitalNameBoxes.Clear();
            return;
        }

        _cityClock += delta;
        var viewport = ViewportSize();
        if (_flatMap)
        {
            // Placement is throttled, scaling is not. Otherwise town labels grow for
            // several frames then snap back whenever the 10 Hz placement pass runs.
            var pixels = _camera.Size / Math.Max(1, viewport.Y);
            foreach (var pin in _cityPins)
                if (pin.Node.Visible) pin.Node.Scale = Vector3.One * pixels;
        }
        var cameraTransform = _camera.GlobalTransform;
        var moved = _citySeenFlat != _flatMap || _citySeenViewport != viewport ||
                    !cameraTransform.IsEqualApprox(_citySeenCamera) ||
                    Mathf.Abs(_camera.Size - _citySeenSize) > 0.0001f;
        var language = Loc.Language;
        if (language != _citySeenLanguage) { _citySeenLanguage = language; InvalidateCities(); }
        // Fixed-size pins on the globe, pixel-scaled ones on the flat map: a switch redoes them all.
        if (_citySeenFlat != _flatMap) InvalidateCities();
        if (_flatMap && !_cityDirty && _citySeenSize > 0 && FlatViewInMotion)
        {
            // Keep names on the same side during a gesture. Reflow only after a
            // significant move/zoom, or once the camera has settled.
            var shift = new Vector2(cameraTransform.Origin.X - _citySeenCamera.Origin.X,
                cameraTransform.Origin.Y - _citySeenCamera.Origin.Y).Length();
            var ratio = _camera.Size / _citySeenSize;
            if (shift < _camera.Size * .22f && ratio > .75f && ratio < 1.33f) return;
        }
        if (!_cityDirty && (!moved || _cityClock < 0.1))
        {
            // Between placements the pins turn with the view, and a name carried over the limb
            // would go on being drawn - in front of space or of the globe, since names are drawn
            // above everything. Nothing but the horizon is redone here; that is cheap.
            if (moved && !_flatMap) HideCitiesPastHorizon();
            return;
        }
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
        var limit = Math.Clamp((int)InterfaceSkin.Metric(_flatMap ? "map.flat_city_label_limit" : "map.city_label_limit", _flatMap ? 90f : 140f), 0, 400);
        var fontSize = Math.Clamp((int)InterfaceSkin.Metric("map.city_font_size", 28f), 12, 64);
        var textPixels = Math.Clamp(InterfaceSkin.Metric(_flatMap ? "map.flat_city_text_pixels" : "map.city_text_pixels", _flatMap ? 12f : 13f), 8f, 28f);
        var font = PresentationFonts.For("map.cities");
        var distance = ViewDistance;
        var tiers = 0;
        if (_flatMap)
            while (tiers < 3 && _flatZoom <= InterfaceSkin.Metric($"map.flat_city_tier{tiers + 1}_zoom",
                tiers switch { 0 => 3.2f, 1 => 1.5f, _ => .8f })) tiers++;
        else
            while (tiers < 3 && distance <= CityTierDistance(tiers + 1)) tiers++;

        // World units per screen pixel: on the flat map the camera's own height over the
        // viewport; on the globe, a fixed-size label's pixel size for the field of view.
        var fixedPixel = 2f * Mathf.Tan(Mathf.DegToRad(_camera.Fov * 0.5f)) / Math.Max(1, viewport.Y);
        var flatPixel = _camera.Size / Math.Max(1, viewport.Y);
        var cameraNormal = _flatMap ? Vector3.Zero : _camera.GlobalPosition.Normalized();
        var rootBasis = _cityRoot.GlobalTransform.Basis;
        var horizon = CityHorizon();

        _cityTaken.Clear();
        _movementLayer?.AddMedallions(_cityTaken);
        // An army card is a mark too: a city's name must not be written across it (Goyang sat
        // under a counter at Seoul). The card's screen size follows the same two paths the
        // counter itself does - world-sized on the flat map, fixed-size on the globe.
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Plate is not { Visible: true } plate || !plate.IsVisibleInTree()) continue;
            var world = plate.GlobalPosition;
            if (_camera.IsPositionBehind(world)) continue;
            var screen = _camera.UnprojectPosition(world);
            if (screen.X < -60f || screen.Y < -40f || screen.X > viewport.X + 60f || screen.Y > viewport.Y + 40f) continue;
            var pixels = _flatMap
                ? plate.Texture.GetWidth() * plate.PixelSize * marker.Node.Scale.X * plate.Scale.X / flatPixel
                : 176f * plate.PixelSize * plate.Scale.X / fixedPixel;
            var cardWidth = pixels + 8f;
            var cardHeight = pixels * plate.Texture.GetHeight() / plate.Texture.GetWidth();
            _cityTaken.Add(new Rect2(screen.X - cardWidth * 0.5f, screen.Y - cardHeight * 0.5f - 4f,
                cardWidth, cardHeight + 8f));
        }
        // Wings and artillery are map marks too. Their readiness labels must not be
        // crossed by town names, especially on the flat map where both are screen-sized.
        foreach (var marker in _supportMapMarkers.Values)
        {
            var plate = marker.Plate;
            if (!IsInstanceValid(plate) || !plate.IsVisibleInTree() || _camera.IsPositionBehind(plate.GlobalPosition)) continue;
            var screen = _camera.UnprojectPosition(plate.GlobalPosition);
            var pixels = _flatMap
                ? plate.Texture.GetWidth() * plate.PixelSize * marker.Node.Scale.X * plate.Scale.X / flatPixel
                : 96f * plate.PixelSize * plate.Scale.X / fixedPixel;
            var cardHeight = pixels * plate.Texture.GetHeight() / plate.Texture.GetWidth();
            _cityTaken.Add(new Rect2(screen - new Vector2(pixels * .5f + 4f, cardHeight * .5f + 4f),
                new Vector2(pixels + 8f, cardHeight + 8f)));
        }
        foreach (var parts in _fleetNodes.Values)
        {
            var plate = parts.Plate;
            if (!IsInstanceValid(plate) || !plate.IsVisibleInTree() || _camera.IsPositionBehind(plate.GlobalPosition)) continue;
            var screen = _camera.UnprojectPosition(plate.GlobalPosition);
            var pixels = new Vector2(plate.Texture.GetWidth(), plate.Texture.GetHeight()) * plate.PixelSize * plate.Scale.X *
                (_flatMap ? parts.Node.Scale.X / flatPixel : 1f / fixedPixel);
            _cityTaken.Add(new Rect2(screen - pixels * .5f - Vector2.One * 4f, pixels + Vector2.One * 8f));
        }
        var used = 0;

        // Where every capital's star stands on the screen, before any name is set: a name is
        // never written across a star that is not its own. Santo Domingo, with no room to its
        // right, was set to its left straight over Port-au-Prince's.
        RefreshStarNames();
        _starMarks.Clear();
        _starScreens.Clear();
        var limb = SurfaceMarkerRadius / Math.Max(SurfaceMarkerRadius, _camera.GlobalPosition.Length());
        foreach (var (marker, normal, city, _) in _starNames)
        {
            var screen = StarOnScreen(marker, normal, viewport, rootBasis, cameraNormal, limb);
            _starScreens.Add(screen);
            if (screen is { } point) _starMarks.Add((point, city));
        }

        // The stars no drawn city stands beside - Yaren, which the catalogue has no town for;
        // Guam's, whose town is drawn only close in; the seat of a country born this year - are
        // named at the star, by the name the country gives it. After the great capitals and
        // world cities, before the rest: a territory of fifty thousand people does not take the
        // room Santo Domingo's name needs.
        var starsNamed = false;
        void NameStars()
        {
            starsNamed = true;
            for (var star = 0; star < _starNames.Count && used < limit; star++)
            {
                var (marker, normal, city, nation) = _starNames[star];
                if (_starScreens[star] is not { } screen) continue;
                if (city >= 0 && _cities[city].Tier <= tiers) continue;
                if (!_flatMap && (rootBasis * normal).Dot(cameraNormal) < horizon) continue;
                var name = StarCapitalName(nation);
                if (string.IsNullOrWhiteSpace(name) && city >= 0) name = _cities[city].DisplayName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var tierPixels = textPixels * 1.22f;
                var textWidth = font.GetStringSize(name, HorizontalAlignment.Left, -1, fontSize).X * tierPixels / fontSize;
                const float starSize = 12f;
                var side = TakeLabelRoom(screen, starSize, textWidth, tierPixels, city, out _);
                if (side == 0) continue;

                var pin = CityPinAt(used++);
                if (pin.City != -2 - star || pin.Name.Text != name) SetUpStarPin(pin, star, marker, name, font, fontSize);
                pin.Node.Position = _cityRoot.ToLocal(marker.GlobalPosition);
                var pixel = _flatMap ? 1f : fixedPixel;
                pin.Node.Scale = _flatMap ? Vector3.One * flatPixel : Vector3.One;
                pin.Name.PixelSize = pixel * tierPixels / fontSize;
                SetLabelSide(pin, side, starSize, fontSize, tierPixels);
                pin.Dot.Visible = false;
                pin.Node.Visible = true;
            }
        }

        // Headquarters names have fixed anchors and never hide on collision. Reserve their
        // room first, then place capitals before lesser cities without a cyclic dependency
        // between last frame's city rectangles and this frame's headquarters visibility.
        _movementLayer?.AddNames(_cityTaken);
        _capitalNameBoxes.Clear();
        _cityDone.Clear();
        for (var index = 0; index < _cities.Count && used < limit; index++)
        {
            if (_cities[index].Tier > 0) break;
            if (!_cities[index].IsNationalCapital) continue;
            _cityDone.Add(index);
            if (PlaceCity(index, out var room)) _capitalNameBoxes.Add(room);
        }
        var examined = 0;
        for (var index = 0; index < _cities.Count && used < limit; index++)
        {
            if (_cities[index].Tier > tiers) break;
            if (_cities[index].Tier > 0 && !starsNamed) NameStars();
            if (used >= limit) break;
            if (_cityDone.Contains(index)) continue;
            if (++examined > 4000) break;
            PlaceCity(index, out _);
        }

        bool PlaceCity(int index, out Rect2 room)
        {
            room = default;
            var city = _cities[index];
            Vector3 local;
            if (_flatMap)
                local = FlatPoint(city.Latitude, city.Longitude, FlatMarkerZ);
            else
            {
                if ((rootBasis * _cityNormals[index]).Dot(cameraNormal) < horizon) return false;
                local = _cityNormals[index] * SurfaceMarkerRadius;
            }
            var world = _cityRoot.GlobalTransform * local;
            if (_camera.IsPositionBehind(world)) return false;
            var screen = _camera.UnprojectPosition(world);
            if (screen.X < -40f || screen.Y < -20f || screen.X > viewport.X + 40f || screen.Y > viewport.Y + 20f) return false;

            var tierPixels = textPixels * (city.Tier switch { 0 => 1.22f, 1 => 1.08f, _ => 1f });
            var name = city.DisplayName;
            var textWidth = font.GetStringSize(name, HorizontalAlignment.Left, -1, fontSize).X * tierPixels / fontSize;
            var dot = city.Tier == 0 ? 9f : 7f;
            var side = TakeLabelRoom(screen, dot, textWidth, tierPixels, index, out room);
            if (side == 0) return false;

            var pin = CityPinAt(used++);
            if (pin.City != index || pin.Name.Text != name) SetUpCityPin(pin, index, name, font, fontSize);
            pin.Node.Position = local;
            var pixel = _flatMap ? 1f : fixedPixel;
            pin.Node.Scale = _flatMap ? Vector3.One * flatPixel : Vector3.One;
            pin.Name.PixelSize = pixel * tierPixels / fontSize;
            SetLabelSide(pin, side, dot, fontSize, tierPixels);
            pin.Dot.PixelSize = pixel * dot / 32f;
            pin.Dot.Visible = !(city.IsNationalCapital && NearCapitalMarker(index)) && !StarStandsOn(index);
            pin.Node.Visible = true;
            return true;
        }
        if (!starsNamed) NameStars();
        for (var index = used; index < _cityPins.Count; index++)
            if (_cityPins[index].Node.Visible) _cityPins[index].Node.Visible = false;
    }

    /// <summary>Where a capital's star is drawn on the screen, or nothing when it is not.</summary>
    private Vector2? StarOnScreen(Node3D marker, Vector3 normal, Vector2I viewport, Basis rootBasis,
        Vector3 cameraNormal, float limb)
    {
        if (!IsInstanceValid(marker) || !marker.IsVisibleInTree()) return null;
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

    /// <summary>The name set on the side <see cref="TakeLabelRoom"/> found for it.</summary>
    private static void SetLabelSide(CityPin pin, int side, float mark, int fontSize, float textPixels)
    {
        var toPixels = fontSize / textPixels;
        pin.Name.HorizontalAlignment = side switch
        {
            1 => HorizontalAlignment.Left,
            -1 => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center
        };
        pin.Name.Offset = Math.Abs(side) == 1
            ? new Vector2(side * (mark * 0.5f + 4f) * toPixels, 0f)
            : new Vector2(0f, Math.Sign(side) * (mark * 0.5f + 2f + textPixels * 0.62f) * toPixels);
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
        pin.Star = null;
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

    /// <summary>
    /// How far round the globe from the camera a city may be and still be named, as the cosine
    /// of the angle. The limb itself is where that cosine is the globe's radius over the camera's
    /// distance; a fixed number stood in for it and, closer than about eleven units - or with a
    /// free camera's wider lens - let through cities already behind the globe. A little short of
    /// the limb, too, because the ground there is seen edge-on and its names pile up.
    /// </summary>
    private float CityHorizon()
    {
        var distance = ViewDistance;
        var chosen = distance > 11f ? 0.55f : distance > 8f ? 0.42f : 0.30f;
        var limb = SurfaceMarkerRadius / Math.Max(SurfaceMarkerRadius, _camera.GlobalPosition.Length());
        return Math.Max(chosen, limb + 0.06f);
    }

    private void HideCitiesPastHorizon()
    {
        var cameraNormal = _camera.GlobalPosition.Normalized();
        var rootBasis = _cityRoot.GlobalTransform.Basis;
        var horizon = CityHorizon();
        foreach (var pin in _cityPins)
        {
            if (!pin.Node.Visible) continue;
            Vector3 normal;
            if (pin.City >= 0 && pin.City < _cityNormals.Length) normal = _cityNormals[pin.City];
            else if (pin.Star is not null && -2 - pin.City < _starNames.Count && IsInstanceValid(pin.Star) &&
                     pin.Star.IsVisibleInTree()) normal = _starNames[-2 - pin.City].Normal;
            else { pin.Node.Visible = false; continue; }
            if ((rootBasis * normal).Dot(cameraNormal) < horizon) pin.Node.Visible = false;
        }
    }

    /// <summary>
    /// Which city each capital's star stands on: a capital city within the distance a star hides
    /// a dot at if there is one, else the nearest city there at all. Only when the stars have been
    /// rebuilt - a new campaign, a capital moved or a country born - and then once over the list.
    /// </summary>
    private void RefreshStarNames()
    {
        var first = _capitalMarkers.Count > 0 && IsInstanceValid(_capitalMarkers[0].Node)
            ? _capitalMarkers[0].Node.GetInstanceId() : 0ul;
        var key = (_capitalRevision, _capitalMarkers.Count, first);
        if (key == _starNamesBuilt) return;
        _starNamesBuilt = key;
        _starNames.Clear();
        const float beside = 0.99995f;
        var capitals = new List<(Nation Nation, Vector3 Normal)>();
        if (_campaign is not null)
            foreach (var nation in _campaign.Nations)
                if (!string.IsNullOrWhiteSpace(nation.CapitalName))
                    capitals.Add((nation, SurfacePoint(nation.CapitalLatitude, nation.CapitalLongitude, 1f).Normalized()));
        foreach (var (marker, normal) in _capitalMarkers)
        {
            if (!IsInstanceValid(marker)) continue;
            int capital = -1, nearest = -1;
            float capitalCloseness = beside, nearestCloseness = beside;
            for (var index = 0; index < _cityNormals.Length; index++)
            {
                var closeness = _cityNormals[index].Dot(normal);
                if (closeness <= beside) continue;
                if (closeness > nearestCloseness) { nearestCloseness = closeness; nearest = index; }
                if (_cities[index].Capital is "national" or "seat" && closeness > capitalCloseness)
                {
                    capitalCloseness = closeness;
                    capital = index;
                }
            }
            // Whose star: the one country whose capital is at that point. Not by the node's name,
            // which the rebuild cannot keep - the old stars are still there, only queued to go.
            Nation owner = null;
            var ownerCloseness = 0.9999f;
            foreach (var (nation, capitalNormal) in capitals)
            {
                var closeness = capitalNormal.Dot(normal);
                if (closeness > ownerCloseness) { ownerCloseness = closeness; owner = nation; }
            }
            _starNames.Add((marker, normal, capital >= 0 ? capital : nearest, owner));
        }
    }

    /// <summary>What the country calls its capital, as the country picker says it.</summary>
    private static string StarCapitalName(Nation nation)
    {
        if (nation is null || string.IsNullOrWhiteSpace(nation.CapitalName)) return "";
        return Loc.GetOr("capital." + nation.CapitalName, Loc.Get(nation.CapitalName));
    }

    /// <summary>Whether a drawn capital star stands on this city, which then needs no dot of its own.</summary>
    private bool StarStandsOn(int index)
    {
        foreach (var (marker, _, city, _) in _starNames)
            if (city == index && IsInstanceValid(marker) && marker.IsVisibleInTree()) return true;
        return false;
    }

    private void SetUpStarPin(CityPin pin, int star, Node3D marker, string name, Font font, int fontSize)
    {
        pin.City = -2 - star;
        pin.Star = marker;
        pin.Node.Name = $"Star_{star}";
        pin.Name.Text = name;
        pin.Name.Font = font;
        pin.Name.FontSize = fontSize;
        pin.Name.OutlineSize = Math.Max(4, fontSize / 4);
        pin.Name.FixedSize = !_flatMap;
        pin.Dot.FixedSize = !_flatMap;
        pin.Name.Modulate = Ui.Colour("globe.city_capital");
        pin.Name.OutlineModulate = Ui.Colour("globe.city_outline");
    }

    /// <summary>A capital's own star already marks this city: its dot would sit under it.</summary>
    private bool NearCapitalMarker(int index)
    {
        var normal = _cityNormals[index];
        // Drawn, not merely switched on: the stars go as a group farther out than the capitals'
        // names do, and a dot hidden under a star nobody can see left the capital unmarked.
        foreach (var (node, markerNormal) in _capitalMarkers)
            if (IsInstanceValid(node) && node.IsVisibleInTree() && markerNormal.Dot(normal) > 0.99995f) return true;
        return false;
    }

    /// <summary>Everything about the pins is redone on the next frame - projection, language, setting.</summary>
    private void InvalidateCities()
    {
        _cityDirty = true;
        foreach (var pin in _cityPins) pin.City = -1;
    }
}
