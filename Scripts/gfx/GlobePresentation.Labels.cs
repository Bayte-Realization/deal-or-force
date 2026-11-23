using static DealOrForce.TerritoryLabelLayout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using DealOrForce.Simulation;
using SourceEarthData = DealOrForce.Simulation.WorldMapSource;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly List<(int MapIndex, Node3D Node, Vector3 Normal, int Cells)> _countryLabels = new();
    private readonly List<(Label3D Node, float Latitude, float Longitude, Basis SurfaceBasis, float FlatRotation)> _countryGlyphs = new();
    /// <summary>The size the country letters are drawn at on the flat map, against their full size; negative until applied.</summary>
    private float _countryGlyphScale = -1f;
    private readonly Dictionary<int, Node3D> _countryLabelGroups = new();
    private Node3D _countryLabelRoot = null!;
    private Dictionary<int, TerritoryLabelAnchor> _lastLabelAnchors;
    private readonly Dictionary<int, string> _lastLabelNames = new();

    /// <summary>
    /// Who owned what when the names were last placed, and the campaign revision that scan
    /// was made at.
    ///
    /// Separate from `_paintedOwner`, which is deliberately frozen at what the raster was
    /// painted with and is how a moved border is coloured in. This one moves forward every
    /// time the names are re-laid, because its only job is to answer "has anything changed
    /// since the last time we asked".
    /// </summary>
    private readonly record struct NationLabelSnapshot(int MapIndex, string Tag, string MapName);

    private ushort[] _labelOwner;

    private int _labelRevision = -1;
    private Task<(Dictionary<int, TerritoryLabelAnchor> Anchors, NationLabelSnapshot[] Nations)> _labelTask;
    private ushort[] _labelControlBuffer;
    private ulong _nextCountryLabelPlaceMsec;
    private readonly Dictionary<int, Label3D[]> _countryGroupGlyphs = new();
    private readonly List<int> _staleCountryGroupsScratch = new();
    private readonly Dictionary<int, NationLabelSnapshot> _labelNationSnapshot = new();

    public void RefreshCountryNames()
    {
        if (_lastLabelAnchors is null) return;
        if (_campaign is not null && !TryBeginActionRead(out var campaign, 0)) return;
        try
        {
            foreach (var (id, name) in _lastLabelNames)
                if (_nationByMapIndex.TryGetValue(id, out var nation) && nation.MapName != name)
                { BuildCountryLabels(_lastLabelAnchors); return; }
        }
        finally { _campaign?.ExitRead(); }
    }

    public void RefreshCountryTagDisplay()
    {
        if (_lastLabelAnchors is null) return;
        if (_campaign is not null && !TryBeginActionRead(out _, 150)) return;
        try { BuildCountryLabels(_lastLabelAnchors); }
        finally { _campaign?.ExitRead(); }
    }

    /// <summary>
    /// Put the names back where the borders are now.
    ///
    /// Two passes, because they cost different amounts. Noticing that ownership moved is a
    /// walk over thirty-eight thousand province owners and happens only when the campaign
    /// says its map changed. Working out where a name goes is a flood fill over sixteen
    /// million cells, and that goes on a task - a peace treaty should not drop a frame, and
    /// a name arriving a moment after the colour does is not something anybody can see.
    /// </summary>
    private void RefreshCountryLabelPlacement()
    {
        var campaign = _campaign;
        if (campaign is null || !campaign.TryEnterRead(0, true)) return;
        try
        {
            if (_labelTask is not null)
            {
                if (!_labelTask.IsCompleted) return;
                if (_labelTask.IsCompletedSuccessfully)
                    BuildCountryLabels(_labelTask.Result.Anchors, _labelTask.Result.Nations);
                else if (_labelTask.IsFaulted)
                    GD.PushWarning("[dof] country names could not be re-placed: " +
                                   _labelTask.Exception?.GetBaseException().Message);
                _labelTask = null;
                return;
            }

            if (_provinceTopology is null || _labelOwner is null) return;
            var nowMsec = Time.GetTicksMsec();
            if (nowMsec < _nextCountryLabelPlaceMsec) return;
            _labelRevision = campaign.MapRevision;

            var owners = _provinceTopology.ProvinceToNation;
            var moved = false;
            for (var province = 1; province < owners.Length && province < _labelOwner.Length; province++)
                if (owners[province] != _labelOwner[province]) { moved = true; break; }
            if (!moved) return;

            // Reuse the immutable RenderSnapshot owner buffer when its revision matches MapRevision
            // (otherwise clone under the step-boundary fence), plus a lightweight nation label array
            // so the worker and post-build pass never dereference live Campaign/Nation state.
            var renderSnap = campaign.RenderSnapshot;
            var snapshot = renderSnap.Version > 0 && renderSnap.OwnerRevision == campaign.MapRevision &&
                           renderSnap.ProvinceOwner.Length == owners.Length
                ? renderSnap.ProvinceOwner
                : (ushort[])owners.Clone();
            NationLabelSnapshot[] nationSnap;
            if (renderSnap.Version > 0 && renderSnap.Nations.Length > 0)
            {
                nationSnap = new NationLabelSnapshot[renderSnap.Nations.Length];
                for (var i = 0; i < renderSnap.Nations.Length; i++)
                {
                    var n = renderSnap.Nations[i];
                    nationSnap[i] = new NationLabelSnapshot(n.MapIndex, n.Tag, n.MapName);
                }
            }
            else
            {
                nationSnap = new NationLabelSnapshot[campaign.Nations.Count];
                for (var i = 0; i < campaign.Nations.Count; i++)
                {
                    var n = campaign.Nations[i];
                    nationSnap[i] = new NationLabelSnapshot(n.MapIndex, n.Tag, n.MapName);
                }
            }
            _labelOwner = snapshot;
            _nextCountryLabelPlaceMsec = nowMsec + (Platform.PlatformProfile.Current?.IsTouchPrimary == true ? 1200UL : 500UL);
            var cells = _provinceTopology.CellToProvince;
            var width = _provinceTopology.Width;
            var height = _provinceTopology.Height;
            _labelTask = Task.Run(() => (BuildCountryLabelAnchorsWorker(snapshot, cells, width, height), nationSnap));
        }
        finally
        {
            campaign.ExitRead();
        }
    }

    private Dictionary<int, TerritoryLabelAnchor> BuildCountryLabelAnchorsWorker(
        ushort[] ownersSnapshot, ushort[] cells, int width, int height)
    {
        const int scale = 2;
        var downWidth = width / scale;
        var downHeight = height / scale;
        var downLength = downWidth * downHeight;
        if (_labelControlBuffer is null || _labelControlBuffer.Length != downLength)
            _labelControlBuffer = new ushort[downLength];

        var buffer = _labelControlBuffer;
        var ownerLen = ownersSnapshot.Length;
        for (var dy = 0; dy < downHeight; dy++)
        {
            var sRow = (dy * scale) * width;
            var dRow = dy * downWidth;
            for (var dx = 0; dx < downWidth; dx++)
            {
                var province = cells[sRow + (dx * scale)];
                buffer[dRow + dx] = province > 0 && province < ownerLen ? ownersSnapshot[province] : (ushort)0;
            }
        }
        return BuildTerritoryLabelAnchors(buffer, downWidth, downHeight, scale);
    }

    private readonly List<Label3D> _glyphBuildScratch = new();

    private void BuildCountryLabels(Dictionary<int, TerritoryLabelAnchor> anchors, NationLabelSnapshot[] nations = null)
    {
        if (!IsInstanceValid(_countryLabelRoot)) return;
        _lastLabelAnchors = anchors;
        _lastLabelNames.Clear();
        _countryLabels.Clear();
        _countryGlyphs.Clear();
        _countryGlyphScale = -1f;
        var activeGroups = new HashSet<int>();
        if (nations is not null)
        {
            _labelNationSnapshot.Clear();
            foreach (var entry in nations) _labelNationSnapshot[entry.MapIndex] = entry;
        }

        var countryFont = PresentationFonts.For("map.countries");

        foreach (var (mapIndex, anchor) in anchors)
        {
            if (anchor.Cells < 80) continue;
            string mapName;
            string tag;
            if (nations is not null)
            {
                if (!_labelNationSnapshot.TryGetValue(mapIndex, out var entry)) continue;
                mapName = entry.MapName;
                tag = entry.Tag;
            }
            else if (_nationByMapIndex.TryGetValue(mapIndex, out var nation))
            {
                mapName = nation.MapName;
                tag = nation.Tag;
            }
            else continue;

            var text = (Simulation.SettingsCatalog.Flag("show_country_tags")
                ? mapName + " (" + tag + ")" : mapName).ToUpperInvariant();
            _lastLabelNames[mapIndex] = mapName;
            if (text.Length == 0) continue;
            // Retain the glyph instances when names or borders change. Replacing every
            // Label3D at once overlaps old/new GPU resources with resized render targets,
            // which can exhaust D3D12's descriptor heap during a language/window switch.
            if (!_countryLabelGroups.TryGetValue(mapIndex, out var group) || !IsInstanceValid(group))
            {
                group = new Node3D { Name = $"CountryLabel_{tag}", Visible = false };
                _countryLabelRoot.AddChild(group);
                _countryLabelGroups[mapIndex] = group;
                _countryGroupGlyphs.Remove(mapIndex);
            }
            activeGroups.Add(mapIndex);
            if (!_countryGroupGlyphs.TryGetValue(mapIndex, out var existingGlyphs))
                existingGlyphs = Array.Empty<Label3D>();
            _glyphBuildScratch.Clear();
            var usedGlyphs = 0;

            // One letter at a time, along the chord.
            //
            // The name used to be a single label with its horizontal scale stretched to fill
            // the country, which is not what an atlas does and does not look like one: it
            // makes the glyphs themselves wide, so RUSSIA came out as a smeared word rather
            // than a spaced one. Paradox tracks the letters apart and leaves their shapes
            // alone, and a letter placed on its own also gets its own tangent to the globe -
            // so the name curves with the planet instead of lying flat across it, and a
            // slanted baseline turns each letter rather than shearing the word.
            var longitudeDegreesPerCell = 360f / SourceEarthData.Width;
            var latitudeDegreesPerCell = 180f / SourceEarthData.Height;

            var centreLatitude = 90f - (anchor.CenterY + 0.5f) * latitudeDegreesPerCell;
            var centreLongitude = -180f + (WrapCell(anchor.CenterX, SourceEarthData.Width) + 0.5f) * longitudeDegreesPerCell;
            var latitudeRadians = Mathf.DegToRad(centreLatitude);

            // The chord in map cells, turned into a length on the globe. Longitude degrees
            // shrink with the cosine of latitude, which is why Norway's name is not the
            // width its pixel count suggests.
            var spanLongitudeDegrees = anchor.AxisX * anchor.HalfSpanPixels * longitudeDegreesPerCell;
            var spanLatitudeDegrees = -anchor.AxisY * anchor.HalfSpanPixels * latitudeDegreesPerCell;
            var halfSpanRadians = Mathf.DegToRad(Mathf.Sqrt(
                Mathf.Pow(spanLongitudeDegrees * Mathf.Cos(latitudeRadians), 2f) +
                spanLatitudeDegrees * spanLatitudeDegrees));
            var span = Mathf.Max(0.02f, GlobeRadius * halfSpanRadians * 2f);

            // How tall the letters may be: what the country has room for across the
            // baseline, and never so tall that they would spill over the edge of it.
            var thicknessDegrees = anchor.ThicknessPixels * latitudeDegreesPerCell;
            var roomForHeight = GlobeRadius * Mathf.DegToRad(thicknessDegrees) * 0.42f;

            var fontSize = Math.Clamp((int)Simulation.InterfaceSkin.Metric("map.country_font_size", 64), 24, 128);
            // How far apart tracked capitals sit, as a share of their height. Atlas names are
            // set loose; this is what makes them read as cartography rather than as a caption.
            var tracking = Math.Clamp(Simulation.InterfaceSkin.Metric("map.country_tracking", 0.86f), 0.4f, 1.5f);
            var glyphCount = text.Length;

            // Size the letters first, then space them - not the other way round.
            //
            // Deriving the height from the spacing, and the spacing from "fill the whole
            // chord", is what scattered the first attempt: a thin country got tiny letters
            // flung from one end of itself to the other, because the width was fixed and the
            // height had to give. Height comes from the room across the baseline, spacing
            // follows from height, and if the word is then longer than the country the whole
            // thing shrinks together - which is what an atlas does with Liechtenstein.
            // Two ceilings, and the lower one wins: as tall as the country is thick across the
            // baseline, and as tall as filling the chord's length would allow. Using only the
            // first made continental names small - India had six times the width it was
            // using - and using only the second would write across countries too thin to
            // carry the letters.
            var fromSpan = span / Mathf.Max(1, glyphCount) / tracking;
            var height = Mathf.Clamp(Mathf.Min(roomForHeight, fromSpan), 0.010f, 0.340f);
            var advance = height * tracking;

            // Room for the glyph boxes at the ends, not only for their centres. n letters
            // at this advance occupy n advances of chord once the outer halves are counted,
            // so that is the constraint - and it is why names stopped ending in the sea.
            var widest = span / Mathf.Max(1, glyphCount);
            if (advance > widest)
            {
                advance = widest;
                height = advance / tracking;
            }
            var pixelSize = Mathf.Clamp(height / fontSize, 0.00016f, 0.00260f);

            var flatRotation = -Mathf.Atan2(anchor.AxisY, anchor.AxisX);
            var outline = Math.Max(4, (int)Mathf.Round(fontSize * 0.15f));

            // Back into map cells: what share of the chord the word actually needs, centred
            // on the chord's own middle.
            var occupied = glyphCount <= 1 ? 0f : Mathf.Min(1f, advance * (glyphCount - 1) / Mathf.Max(0.0001f, span));
            var reachPixels = anchor.HalfSpanPixels * occupied;
            var startX = anchor.CenterX - anchor.AxisX * reachPixels;
            var startY = anchor.CenterY - anchor.AxisY * reachPixels;
            var stepX = glyphCount <= 1 ? 0f : anchor.AxisX * reachPixels * 2f / (glyphCount - 1);
            var stepY = glyphCount <= 1 ? 0f : anchor.AxisY * reachPixels * 2f / (glyphCount - 1);

            // Where every letter lands, worked out before any of them is built, because a
            // letter's heading comes from its neighbours rather than from itself.
            var latitudes = new float[glyphCount];
            var longitudes = new float[glyphCount];
            var points = new Vector3[glyphCount];
            var glyphTextRadius = SurfaceTextRadius + (_reliefDisplacement > 0.0001f ? 0.002f : 0f);
            for (var index = 0; index < glyphCount; index++)
            {
                var cellX = glyphCount <= 1 ? anchor.CenterX : startX + stepX * index;
                var cellY = glyphCount <= 1 ? anchor.CenterY : startY + stepY * index;
                latitudes[index] = Mathf.Clamp(90f - (cellY + 0.5f) * latitudeDegreesPerCell, -88f, 88f);
                longitudes[index] = -180f + (WrapCell(cellX, SourceEarthData.Width) + 0.5f) * longitudeDegreesPerCell;
                points[index] = LocalSurfacePoint(latitudes[index], longitudes[index], glyphTextRadius);
            }

            // The heading of the word, taken once at its middle. Every letter then turns to
            // the direction of the line it is standing on rather than to its own local east.
            //
            // That distinction is the whole of the fix. A tangent basis built from the local
            // east and north at each letter's own position looks right at the equator and
            // falls apart towards the poles, because meridians converge: at sixty degrees
            // north the same map-space direction points somewhere measurably different two
            // letters along. Sweden and Finland came out as fans of tilted letters. Carrying
            // one heading along the path and projecting it into each letter's tangent plane
            // keeps the word straight, and lets it curve with the planet the way an atlas
            // name does instead of shearing.
            var wordHeading = CountryLabelSurfaceBasis(
                latitudes[glyphCount / 2], longitudes[glyphCount / 2], anchor.AxisX, anchor.AxisY).X;

            for (var index = 0; index < glyphCount; index++)
            {
                var character = text[index];
                if (character == ' ') continue;

                var latitude = latitudes[index];
                var longitude = longitudes[index];
                var position = points[index];

                // Prefer the direction the word is actually travelling here - the chord
                // between the neighbouring letters - and fall back to the shared heading at
                // the ends and for one-letter names.
                var heading = wordHeading;
                if (glyphCount > 1)
                {
                    var before = points[Math.Max(0, index - 1)];
                    var after = points[Math.Min(glyphCount - 1, index + 1)];
                    var along = after - before;
                    if (along.LengthSquared() > 0.0000001f) heading = along.Normalized();
                }

                var normal = position.Normalized();
                var baseline = (heading - normal * heading.Dot(normal));
                baseline = baseline.LengthSquared() > 0.0000001f ? baseline.Normalized() : wordHeading;
                var up = normal.Cross(baseline).Normalized();
                var surfaceBasis = new Basis(baseline, up, normal).Orthonormalized();

                var glyph = usedGlyphs < existingGlyphs.Length ? existingGlyphs[usedGlyphs]
                    : new Label3D { Name = $"Glyph{usedGlyphs}" };
                usedGlyphs++;
                _glyphBuildScratch.Add(glyph);
                glyph.Text = character.ToString();
                glyph.FontSize = fontSize;
                glyph.Font = countryFont;
                glyph.PixelSize = pixelSize;
                glyph.FixedSize = false;
                glyph.Billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                glyph.OutlineSize = outline;
                glyph.Modulate = CountryFillColour(0.88f);
                glyph.OutlineModulate = CountryOutlineColour(0.92f);
                glyph.NoDepthTest = false;
                glyph.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
                glyph.Position = LocalSurfacePoint(latitude, longitude, glyphTextRadius);
                glyph.Basis = surfaceBasis;
                glyph.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                if (glyph.GetParent() is null) group.AddChild(glyph);
                _countryGlyphs.Add((glyph, latitude, longitude, surfaceBasis, flatRotation));
            }
            for (var index = usedGlyphs; index < existingGlyphs.Length; index++)
            {
                group.RemoveChild(existingGlyphs[index]);
                existingGlyphs[index].QueueFree();
            }
            if (existingGlyphs.Length == usedGlyphs)
            {
                for (var g = 0; g < usedGlyphs; g++) existingGlyphs[g] = _glyphBuildScratch[g];
                _countryGroupGlyphs[mapIndex] = existingGlyphs;
            }
            else
            {
                _countryGroupGlyphs[mapIndex] = _glyphBuildScratch.ToArray();
            }

            var center = LocalSurfacePoint(centreLatitude, centreLongitude, glyphTextRadius);
            _countryLabels.Add((mapIndex, group, center.Normalized(), anchor.Cells));
        }
        _staleCountryGroupsScratch.Clear();
        foreach (var id in _countryLabelGroups.Keys)
            if (!activeGroups.Contains(id)) _staleCountryGroupsScratch.Add(id);
        foreach (var id in _staleCountryGroupsScratch)
        {
            var group = _countryLabelGroups[id];
            if (IsInstanceValid(group)) { _countryLabelRoot.RemoveChild(group); group.QueueFree(); }
            _countryLabelGroups.Remove(id);
            _countryGroupGlyphs.Remove(id);
        }
        // Refreshes may run after this frame's normal visibility/projection pass.
        // Never briefly render every country, or globe-space glyphs on the flat map.
        if (_flatMap) UpdateFlatWorldPositions();
        UpdateCountryLabelVisibility();
    }

    private bool AnyCountryAlphaVisible()
    {
        foreach (var alpha in _countryAlphas.Values)
            if (alpha > 0.01f) return true;
        return false;
    }

    /// <summary>A map column, wrapped round the date line.</summary>
    private static float WrapCell(float x, int width)
    {
        var wrapped = x % width;
        return wrapped < 0 ? wrapped + width : wrapped;
    }

    /// <summary>
    /// Whether country names are drawn across the map at all. Some players want the
    /// typography and some want to see the borders under it; it is not a decision the game
    /// should make for them.
    /// </summary>
    public bool CountryLabelsEnabled { get; set; } = true;

    private void UpdateCountryLabelVisibility(double delta = 0)
    {
        if (!IsInstanceValid(_countryLabelRoot) || !IsInstanceValid(_camera)) return;
        if (!CountryLabelsEnabled || !Simulation.WorldLayers.On("country_labels")) { _countryLabelRoot.Visible = false; return; }
        if (_mapMode is StrategicMapMode.Terrain or StrategicMapMode.Relief) { _countryLabelRoot.Visible = false; return; }

        var measure = _flatMap ? _flatZoom : _distance;
        if (!_mapLookAtlas)
        {
            _countryLabelRoot.Visible = _flatMap ? _flatZoom >= 0.85f : _distance >= 7.9f;
            if (!_countryLabelRoot.Visible) return;
        }
        else
        {
            var bandAny = MapLabelPriority.CountryTargetAlpha(true, _flatMap, measure, 100000, true, true);
            if (bandAny <= 0.005f && !AnyCountryAlphaVisible())
            {
                _countryLabelRoot.Visible = false;
                return;
            }
            _countryLabelRoot.Visible = true;
        }

        // Closer in than 1.7 the letters keep their places - spread across the country - and
        // grow no larger: at full size POLAND and UKRAINE ran into each other and out of their
        // borders. Smaller letters spaced wide are how an atlas names a country seen close.
        // Past the strategic view the globe's letters grow a little as it draws away, so the
        // names left on a whole hemisphere are still big enough to read.
        var glyphScale = _flatMap ? Math.Clamp(_flatZoom / 1.7f, 0.5f, 1f) : Math.Clamp(_distance / 16.5f, 1f, 1.3f);
        if (Math.Abs(glyphScale - _countryGlyphScale) > 0.02f)
        {
            _countryGlyphScale = glyphScale;
            foreach (var glyph in _countryGlyphs)
                if (IsInstanceValid(glyph.Node)) glyph.Node.Scale = Vector3.One * glyphScale;
        }
        var minimumCells = _flatMap
            ? _flatZoom > 8.8f ? 26000 : _flatZoom > 6.2f ? 6500 : _flatZoom > 4.0f ? 1500 : _flatZoom > 2.6f ? 150 : 40
            : _distance > 11.5f ? 26000 : _distance > 9.5f ? 6500 : _distance > 7.4f ? 1500 : 150;
        var viewNormal = _flatMap ? Vector3.Zero : _camera.GlobalPosition.Normalized();
        var horizonThreshold = _distance > 11.5f ? 0.72f : _distance > 9.5f ? 0.62f : _distance > 7.4f ? 0.50f : 0.38f;
        var rootBasis = _countryLabelRoot.GlobalTransform.Basis;
        var playerMapIndex = _campaign?.Player?.MapIndex ?? -1;
        var stepDt = (float)Math.Max(0.016, delta);
        var atlasFill = CountryFillColour(1f);
        var atlasOutline = CountryOutlineColour(1f);

        foreach (var label in _countryLabels)
            if (IsInstanceValid(label.Node))
            {
                // Stored normals are globe-local. Use the label root's inherited
                // rotation before comparing with the camera; otherwise rotating the
                // globe leaves labels from the old hemisphere visible at its rim.
                var worldNormal = (rootBasis * label.Normal).Normalized();
                var horizonOk = _flatMap || worldNormal.Dot(viewNormal) > horizonThreshold;
                if (!_mapLookAtlas)
                {
                    var visible = label.Cells >= minimumCells && horizonOk;
                    if (label.Node.Visible != visible) label.Node.Visible = visible;
                    continue;
                }
                var priority = label.MapIndex == _highlightedNation || (playerMapIndex > 0 && label.MapIndex == playerMapIndex);
                var target = MapLabelPriority.CountryTargetAlpha(true, _flatMap, measure, label.Cells, priority, horizonOk);
                var prev = _countryAlphas.TryGetValue(label.MapIndex, out var current) ? current : 0f;
                var next = horizonOk ? MapLabelPriority.StepOpacity(prev, target, stepDt) : 0f;
                _countryAlphas[label.MapIndex] = next;
                var visibleAtlas = next > 0.01f;
                if (label.Node.Visible != visibleAtlas) label.Node.Visible = visibleAtlas;
                if (visibleAtlas && Math.Abs(next - prev) > 0.005f &&
                    _countryGroupGlyphs.TryGetValue(label.MapIndex, out var glyphs))
                {
                    var fillCol = new Color(atlasFill, next);
                    var outlineCol = new Color(atlasOutline, Math.Min(0.94f, next * 1.06f));
                    for (var g = 0; g < glyphs.Length; g++)
                    {
                        var glyph = glyphs[g];
                        if (!IsInstanceValid(glyph)) continue;
                        glyph.Modulate = fillCol;
                        glyph.OutlineModulate = outlineCol;
                    }
                }
            }
    }

    private static Basis CountryLabelSurfaceBasis(
        float latitude, float longitude, float mapAxisX, float mapAxisY)
    {
        var latitudeRadians = Mathf.DegToRad(latitude);
        var longitudeRadians = Mathf.DegToRad(longitude + 180f);
        var normal = SurfacePoint(latitude, longitude, 1f).Normalized();
        var east = new Vector3(Mathf.Cos(longitudeRadians), 0f, -Mathf.Sin(longitudeRadians)).Normalized();
        var north = new Vector3(
            -Mathf.Sin(latitudeRadians) * Mathf.Sin(longitudeRadians),
            Mathf.Cos(latitudeRadians),
            -Mathf.Sin(latitudeRadians) * Mathf.Cos(longitudeRadians)).Normalized();
        var baseline = (east * mapAxisX - north * mapAxisY).Normalized();
        var up = normal.Cross(baseline).Normalized();
        return new Basis(baseline, up, normal).Orthonormalized();
    }

    private static Vector3 SurfacePoint(float latitude, float longitude, float radius)
    {
        var lat = Mathf.DegToRad(latitude);
        // The source map's seam is at -180 degrees; SphereMesh uses 0 at its own seam.
        var lon = Mathf.DegToRad(longitude + 180f);
        var horizontal = Mathf.Cos(lat);
        return new Vector3(horizontal * Mathf.Sin(lon), Mathf.Sin(lat), horizontal * Mathf.Cos(lon)) * radius;
    }

}
