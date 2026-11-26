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
    private readonly List<(Node3D Node, Vector3 Normal)> _armyMarkers = new();
    private readonly Dictionary<Node3D, IReadOnlyList<Simulation.Army>> _armyByMarker = new();

    /// <summary>
    /// One counter on the map, and the handles into it.
    ///
    /// The node references are kept rather than looked up by name every frame: with a
    /// thousand counters on the map, three GetNodeOrNull calls each was three thousand
    /// string path resolutions a frame to write numbers that had usually not changed.
    /// </summary>
    private sealed class ArmyStackMarker
    {
        public Node3D Node;
        public Sprite3D Plate;
        public Texture2D FullPlateTexture;
        public ulong FlagVisualId;
        public Sprite3D Glow;
        public Sprite3D ConditionBars;
        public Label3D Count;
        public readonly List<Simulation.Army> Stack = new();
        // Display aggregation never changes simulation stacks, province positions or saves.
        public readonly List<Simulation.Army> DisplayStack = new();
        public int DisplayProvinces = 1;
        public int DisplayFingerprint;
        public int ActualFingerprint;
        public Vector3 Normal;
        public int NationId = -1;
        public string TemplateId = "";
        public string CountText = "";
        public int ShownDivisions = -1;
        public int ShownMenTenths = -1;
        public bool ShownCompact;
        public int ConditionKey = -1;
        public int LastMetaProvinceId = -1;
        public bool Seen;
        public bool Compact;
        public bool FlatLayout;
        public bool LayoutInitialized;
        /// <summary>Screen-pixel fan-out when more than one nation's army stands in one province.</summary>
        public Vector2 LayoutOffset;
        /// <summary>
        /// Where the counter belongs, and where it is drawn.
        ///
        /// These were one value, written straight into the node every time the army moved,
        /// which is why a division crossing a border teleported: a hundred kilometres in one
        /// frame. The target is geography and is written by the reconcile pass; the display
        /// position eases toward it every frame, so a counter glides along the ground it is
        /// actually on instead of jumping to the next province.
        /// </summary>
        public Vector3 TargetPosition;
        public Vector3 DisplayPosition;
        /// <summary>False until the display position has caught up, so a new counter does not fly in from the origin.</summary>
        public bool MotionSettled;
    }

    private readonly Stack<ArmyStackMarker> _armyMarkerPool = new();
    private double _controllerUploadTimer;

    private void ClearArmyMarkerPool()
    {
        while (_armyMarkerPool.Count > 0)
        {
            var marker = _armyMarkerPool.Pop();
            if (marker.Node is not null && IsInstanceValid(marker.Node))
            {
                marker.Node.GetParent()?.RemoveChild(marker.Node);
                marker.Node.QueueFree();
            }
        }
    }

    // Army marker pool cleanup called during presentation reset


    private readonly Dictionary<(int, int, int, int), ArmyStackMarker> _armyStacks = new();
    private readonly List<(int, int, int, int)> _vanishedStacks = new();
    private readonly Dictionary<string, int> _templateVotes = new();
    private readonly HashSet<int> _selectedArmyIds = new();
    private readonly Dictionary<(int Nation, string Template), Texture2D> _armyCounterTextures = new();
    private int _armyStructureFingerprint = int.MinValue;
    private Texture2D _divisionTexture = null!;
    private void ApplyNewShellArmyVisibility(IReadOnlySet<int> visibleArmies)
    {
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !IsInstanceValid(marker.Node) || !marker.Node.Visible) continue;
            var stack = marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack;
            var keep = false;
            for (var i = 0; i < stack.Count; i++)
            {
                if (visibleArmies.Contains(stack[i].Id) || _selectedArmyIds.Contains(stack[i].Id))
                {
                    keep = true;
                    break;
                }
            }
            if (!keep) marker.Node.Visible = false;
        }
    }

    private float _counterScale = 1f;
    private float _counterWorldPixel;

    /// <summary>
    /// Flat counters use screen pixels, never the globe's distance-based scale curve.
    /// This remains one so picking and presentation use the same footprint.
    /// </summary>
    private float _counterFlatFar = 1f;

    public bool MilitaryMarkersEnabled
    {
        // A game without counters (common/world_layers) has none to switch on.
        get => _militaryMarkersEnabled && Simulation.WorldLayers.On("military_counters");
        set
        {
            if (_militaryMarkersEnabled == value) return;
            _militaryMarkersEnabled = value;
            // Turning the layer back on must rebuild the counters even if no army moved
            // while it was hidden; the structure fingerprint alone would otherwise say
            // "nothing changed" and leave the map empty.
            _armyStructureFingerprint = int.MinValue;
        }
    }
    private bool _militaryMarkersEnabled = true;
    private bool CanShowMilitary(int nationId) => !FogOfWarActive || Simulation.CampaignIntel.PlayerCanInspect(_campaign, nationId);

    private void SizeArmyCounter(ArmyStackMarker marker)
    {
        if (marker.Plate is null || marker.Count is null) return;
        marker.Plate.Texture = _flatMap && _flatStrategicCounters
            ? CompactCounterTexture(marker.FullPlateTexture, new Rect2I(8, 5, 51, 70))
            : marker.FullPlateTexture;
        // PixelSize/OutlineSize rebuild Label3D meshes. Never animate those properties
        // on every orthographic zoom frame for the entire world's counters.
        if (!marker.LayoutInitialized || marker.FlatLayout != _flatMap)
        {
            marker.LayoutInitialized = true;
            marker.FlatLayout = _flatMap;
            marker.Plate.FixedSize = false;
            marker.Count.FixedSize = false;
            marker.Count.HorizontalAlignment = HorizontalAlignment.Center;
            marker.Count.VerticalAlignment = VerticalAlignment.Center;
            // Godot's Label3D defaults Width to 500 - a half-kilometre of text field the
            // label then centres the text inside of, which parks the number to the right
            // of the counter it belongs to. Zero is "as wide as the text", which is what
            // centres the number on the anchor.
            marker.Count.Width = 0f;
            if (marker.Glow is not null)
            {
                marker.Glow.FixedSize = marker.Plate.FixedSize;
                marker.Glow.PixelSize = marker.Plate.PixelSize;
            }
            marker.Compact = false;
            marker.Plate.Scale = Vector3.One;
            marker.Plate.Visible = true;
            marker.Count.Visible = true;
        }
        if (_flatMap)
        {
            ApplyArmyCounterScale(marker);
            // Density owns visibility and compactness. A zoom must not turn text back on
            // over a hidden plate before the density pass decides where it fits.
        }
        else
        {
            if (marker.Node.Position == Vector3.Zero && marker.Stack.Count > 0)
                marker.Node.Position = ArmyMarkerPosition(marker);
            SizeSurfaceArmyCounter(marker);
        }
        // Shared texture-space layout, with no separate 3D displacement or shader
        // billboard/fixed-size compensation. All four layers inherit the surface frame.
        var platePixel = _flatMap
            ? Math.Clamp(Simulation.InterfaceSkin.Metric("map.flat_counter_width", 64f), 40f, 96f) / marker.Plate.Texture.GetWidth()
            : 1f / 176f;
        marker.Plate.PixelSize = platePixel;
        if (marker.Glow is not null)
        {
            marker.Glow.PixelSize = platePixel;
            marker.Glow.Scale = _flatMap && _flatStrategicCounters
                ? new Vector3(marker.Plate.Texture.GetWidth() / 176f, marker.Plate.Texture.GetHeight() / 104f, 1f)
                : Vector3.One;
        }
        marker.Count.PixelSize = platePixel * 1.45f;
        marker.Count.FontSize = _flatMap && _flatStrategicCounters ? 30 : 21;
        marker.Count.OutlineSize = _flatMap ? 2 : Mathf.Max(1, Mathf.RoundToInt(4 * _counterScale));
        marker.Count.Position = Vector3.Zero;
        // The centred two-line block uses the same texture-space anchor in both
        // projections. A separate flat-map offset put the figures on top of the icon.
        marker.Count.Offset = new Vector2(1f, 4f);
        if (marker.ConditionBars is not null)
        {
            marker.ConditionBars.FixedSize = marker.Plate.FixedSize;
            marker.ConditionBars.PixelSize = platePixel;
            marker.ConditionBars.Offset = Vector2.Zero;
            marker.ConditionBars.Position = Vector3.Zero;
        }
    }

    /// <summary>
    /// Compensate the orthographic camera each frame using transforms only. Quantizing
    /// this compensation made cards breathe/jump during smooth zoom. Expensive Label3D
    /// layout changes remain limited to projection switches and the globe's scale steps.
    /// </summary>
    private void UpdateCounterScale(bool force = false)
    {
        UpdateCapitalMarkerScale();
        if (_flatMap)
        {
            _counterWorldPixel = _camera.Size / Math.Max(1f, _camera.GetViewport().GetVisibleRect().Size.Y);
            var referenceZoom = Math.Clamp(Ui.Metric("map.flat_counter_reference_zoom", 1.7f), .6f, 3f);
            var minimumRatio = Math.Clamp(Ui.Metric("map.flat_counter_min_ratio", .25f), .15f, .5f);
            // Follow the ground on retreat; only retain a small, clickable tactical symbol
            // at the world view. Constant-width cards dwarf countries when zoomed out.
            _counterFlatFar = Math.Clamp(referenceZoom / _flatZoom, minimumRatio, 1f);
            var strategic = _flatStrategicCounters ? _flatZoom > 2.8f : _flatZoom > 3.3f;
            var detailChanged = strategic != _flatStrategicCounters;
            _flatStrategicCounters = strategic;
            foreach (var marker in _armyStacks.Values)
            {
                if (!IsInstanceValid(marker.Node)) continue;
                if (force || detailChanged || !marker.LayoutInitialized || !marker.FlatLayout) SizeArmyCounter(marker);
                marker.Node.Scale = Vector3.One * _counterWorldPixel * _counterFlatFar;
                if (force || detailChanged) marker.Count.FontSize = strategic ? 30 : 21;
            }
            foreach (var parts in _fleetNodes.Values)
            {
                if (force || detailChanged || parts.Plate.FixedSize) SizeFleetCounter(parts);
                parts.Node.Scale = Vector3.One * _counterWorldPixel * _counterFlatFar;
                parts.Count.Visible = !strategic;
                if (force || detailChanged) parts.Count.FontSize = strategic ? 30 : 24;
            }
            foreach (var marker in _supportMapMarkers.Values)
            {
                if (force || detailChanged || marker.Plate.FixedSize) PlaceSupportMarker(marker);
                marker.Node.Scale = Vector3.One * _counterWorldPixel * _counterFlatFar;
                marker.Readiness.Visible = !strategic && _selectedSupportIds.Contains(marker.Formation.Id);
                if (force || detailChanged)
                {
                    marker.Readiness.FontSize = strategic ? 30 : 20;
                    PaintSupportMarker(marker, marker.Formation);
                }
            }
            if (force || detailChanged) InvalidateFlatCounterDensity();
            return;
        }
        var wanted = Mathf.Lerp(CounterCloseScale, 1f,
                Mathf.InverseLerp(MinimumDistance, CounterGrowthDistance, _distance));
        wanted = Mathf.Clamp(wanted, 1f, CounterCloseScale);
        // The entire surface card shares one real transform, not four independent
        // shader distance compensations. Update transforms continuously when the camera moves.
        var camNow = _camera.GlobalTransform;
        if (force || camNow != _lastScaleCamera || _armyStacks.Count != _lastScaleArmyCount || _fleetNodes.Count != _lastScaleFleetCount)
        {
            _lastScaleCamera = camNow;
            _lastScaleArmyCount = _armyStacks.Count;
            _lastScaleFleetCount = _fleetNodes.Count;
            foreach (var marker in _armyStacks.Values)
                if (IsInstanceValid(marker.Node) && marker.Node.Visible) SizeSurfaceArmyCounter(marker);
            foreach (var parts in _fleetNodes.Values)
                if (IsInstanceValid(parts.Node) && parts.Node.Visible) parts.Node.Scale = Vector3.One * SurfaceCounterWidth(parts.Node, "fleet");
        }
        if (!force && Mathf.Abs(wanted - _counterScale) < 0.03f) return;
        _counterScale = wanted;
        _counterWorldPixel = 0f;
        _counterFlatFar = 1f;
        _flatStrategicCounters = false;
        InvalidateFlatCounterDensity();
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !IsInstanceValid(marker.Node)) continue;
            SizeArmyCounter(marker);
            marker.Count.FontSize = 21;
            marker.Count.Text = marker.CountText;
        }
        foreach (var parts in _fleetNodes.Values) { SizeFleetCounter(parts); parts.Count.FontSize = 24; }
        foreach (var marker in _supportMapMarkers.Values)
            if (IsInstanceValid(marker.Node)) PlaceSupportMarker(marker);
    }

    private bool TryPickArmyStack(Vector2 pixel, out IReadOnlyList<Simulation.Army> armies, bool selectableOnly = true)
    {
        armies = null;
        if (!MilitaryMarkersEnabled || (!_flatMap && _distance > 13.5f) || !IsInstanceValid(_camera)) return false;
        // A bigger counter is a bigger target: the radius that finds it follows the size
        // it is actually drawn at, or clicking a close-up counter near its edge misses.
        var bestDistance = float.MaxValue;
        foreach (var marker in _armyStacks.Values)
        {
            var node = marker.Node;
            var candidates = marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack;
            if (!IsInstanceValid(node) || !node.Visible || _camera.IsPositionBehind(node.GlobalPosition)) continue;
            if (!node.IsVisibleInTree()) continue;
            if (!TryHitSurfaceArmyCard(marker, pixel, out var distance)) continue;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            // The counter still blocks a click on the province behind it, but foreign
            // formations never enter selection or expose their detailed army tooltip.
            armies = selectableOnly
                ? candidates.Where(army => Simulation.ArmyInteraction.CanSelect(_campaign, army)).ToArray()
                : candidates;
        }
        return armies is not null;
    }

    public void SelectArmy(int armyId)
    {
        SelectArmies(new[] { armyId });
    }

    public void SelectArmies(IEnumerable<int> armyIds)
    {
        var selected = armyIds.Where(id => id >= 0).ToHashSet();
        if (selected.Count > 0)
        {
            if (!TryBeginActionRead(out var campaign)) selected.Clear();
            else
            {
                try
                {
                    selected.IntersectWith(campaign.Armies
                        .Where(army => Simulation.ArmyInteraction.CanSelect(campaign, army)).Select(army => army.Id));
                }
                finally { campaign.ExitRead(); }
            }
        }
        _selectedArmyIds.Clear();
        _selectedArmyIds.UnionWith(selected);
        InvalidateFlatCounterDensity();
        _routeFingerprint = int.MinValue;
        foreach (var (node, armies) in _armyByMarker)
        {
            if (!IsInstanceValid(node)) continue;
            var plate = node.GetChildOrNull<Sprite3D>(0);
            if (plate is null) continue;
            var chosen = armies.Any(army => selected.Contains(army.Id));
            plate.Modulate = chosen ? new Color(1f, 0.96f, 0.86f, 1f) : Colors.White;
            if (plate.GetChildOrNull<Sprite3D>(0) is { } glow) glow.Visible = chosen;
        }
    }

    private void RefreshArmyMarkerVisibility()
    {
        if (!IsInstanceValid(_camera)) return;
        UpdateMarkerVisibility(_armyRoot, _armyMarkers, MilitaryMarkersEnabled && (_flatMap || ViewDistance <= 10.8f),
            _flatMap ? 0f : GlobeRadius / Math.Max(GlobeRadius, _camera.GlobalPosition.Length()) + .002f);
        if (NewShellVisibleArmyIds is { } visibleArmies)
            ApplyNewShellArmyVisibility(visibleArmies);
    }

    /// <summary>
    /// Bring the counters on the map in line with where the armies are.
    ///
    /// This used to be a full teardown: a fingerprint over every division in the world was
    /// compared against the last one, and if any single division had moved to another
    /// province - which, with fourteen hundred of them at speed five, is every time - every
    /// counter was freed and rebuilt. That is four thousand nodes destroyed and created
    /// eight times a second, and it is the whole of why starting time made the game stutter.
    ///
    /// Now the counters persist. A stack that is still there is updated in place, a new one
    /// is built, and one whose armies have all left is freed. In a normal second that is a
    /// handful of nodes rather than four thousand.
    /// </summary>
    private readonly HashSet<int> _shownNations = new();

    /// <summary>
    /// Whose divisions the map draws (setting "map_divisions"). By default the player's own,
    /// their allies' and dependencies', and everybody who is at war: the garrisons of two
    /// hundred countries at peace buried Europe in counters, and none of them was anything a
    /// player had to act on. A country the player has selected is drawn too, so looking at a
    /// neighbour's army is one click.
    /// </summary>
    private void RefreshShownNations(Simulation.Campaign campaign)
    {
        _shownNations.Clear();
        var mode = Simulation.SettingsCatalog.Chosen("map_divisions");
        var player = campaign.Player;
        foreach (var nation in campaign.Nations)
        {
            if (mode == "all") { _shownNations.Add(nation.Id); continue; }
            if (player is not null && nation.Id == player.Id) { _shownNations.Add(nation.Id); continue; }
            if (mode == "ours") continue;
            if (player is not null && (nation.AnswersTo(player.Id) || player.AnswersTo(nation.Id) ||
                    Simulation.CampaignDiplomacy.Peek(campaign, player.Id, nation.Id) is { Stance: Simulation.DiplomaticStance.Allied }))
                _shownNations.Add(nation.Id);
            if (nation.Id == ShownCountryId) _shownNations.Add(nation.Id);
        }
        if (mode == "all" || mode == "ours") return;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            foreach (var id in war.Side(true)) _shownNations.Add(id);
            foreach (var id in war.Side(false)) _shownNations.Add(id);
        }
    }

    /// <summary>The country the interface is looking at, whose army is drawn whatever the setting says.</summary>
    public int ShownCountryId { get; set; } = -1;

    private readonly Dictionary<int, Vector3> _previousArmyPositions = new();

    private void ReconcileArmyMarkers(Simulation.Campaign campaign)
    {
        if (!MilitaryMarkersEnabled || !IsInstanceValid(_armyRoot) || _divisionTexture is null) return;

        _previousArmyPositions.Clear();
        foreach (var m in _armyStacks.Values)
            if (m.Stack.Count > 0 && m.Node is not null && IsInstanceValid(m.Node))
            {
                for (var i = 0; i < m.Stack.Count; i++)
                    _previousArmyPositions[m.Stack[i].Id] = m.DisplayPosition;
            }

        foreach (var marker in _armyStacks.Values)
        {
            marker.Seen = false;
            marker.Stack.Clear();
        }

        RefreshShownNations(campaign);
        foreach (var army in campaign.Armies)
        {
            // Aboard a ship it is the fleet's to show (an anchor on the fleet says how many).
            if (army.EmbarkedFleetId >= 0) continue;
            var bucket = ArmyPresentationBucket(campaign, army);
            if (!CanShowArmy(army) || !_shownNations.Contains(army.NationId)) continue;
            if (!_armyStacks.TryGetValue(bucket, out var marker))
            {
                marker = _armyMarkerPool.Count > 0 ? _armyMarkerPool.Pop() : new ArmyStackMarker();
                _armyStacks[bucket] = marker;
            }
            marker.Seen = true;
            marker.Stack.Add(army);
        }

        _vanishedStacks.Clear();
        foreach (var (bucket, marker) in _armyStacks)
            if (!marker.Seen || marker.Stack.Count == 0) _vanishedStacks.Add(bucket);
        foreach (var bucket in _vanishedStacks)
        {
            var marker = _armyStacks[bucket];
            if (marker.Node is not null && IsInstanceValid(marker.Node))
            {
                if (_armyMarkerPool.Count < 512)
                {
                    marker.Node.Visible = false;
                    if (marker.Glow is not null && IsInstanceValid(marker.Glow))
                        marker.Glow.Visible = false;
                    marker.Stack.Clear();
                    marker.DisplayStack.Clear();
                    marker.Seen = false;
                    marker.CountText = "";
                    marker.ShownDivisions = -1;
                    marker.ShownMenTenths = -1;
                    marker.ConditionKey = -1;
                    marker.LastMetaProvinceId = -1;
                    marker.ActualFingerprint = 0;
                    marker.DisplayFingerprint = 0;
                    marker.LayoutInitialized = false;
                    marker.LayoutOffset = Vector2.Zero;
                    marker.MotionSettled = false;
                    _armyMarkerPool.Push(marker);
                }
                else
                {
                    // Detach now so a replacement with the same lead ID does not
                    // inherit an anonymous name while the old node awaits deletion.
                    marker.Node.GetParent()?.RemoveChild(marker.Node);
                    marker.Node.QueueFree();
                }
            }
            _armyStacks.Remove(bucket);
        }
        if (_vanishedStacks.Count > 0) InvalidateFlatCounterDensity();

        _armyMarkers.Clear();
        _armyByMarker.Clear();
        foreach (var marker in _armyStacks.Values)
        {
            var lead = marker.Stack[0];
            var nation = campaign.NationById(lead.NationId);
            if (nation is null) continue;

            // The counter shows the template most of the stack is made of.
            _templateVotes.Clear();
            foreach (var army in marker.Stack)
            {
                var id = army.TemplateId ?? "";
                _templateVotes[id] = _templateVotes.GetValueOrDefault(id) + 1;
            }
            var templateId = lead.TemplateId ?? "";
            var votes = -1;
            foreach (var (id, count) in _templateVotes)
                if (count > votes || (count == votes && string.CompareOrdinal(id, templateId) < 0))
                {
                    votes = count;
                    templateId = id;
                }

            if (marker.Node is null || !IsInstanceValid(marker.Node))
            {
                var initialLoc = ArmyPresentationLocation(marker.Stack);
                marker.Normal = SurfacePoint(initialLoc.Latitude, initialLoc.Longitude, 1f);
                CreateArmyStackMarker(marker, nation, lead, templateId);
                InvalidateFlatCounterDensity();
            }
            else
            {
                if (marker.Node.GetParent() != _armyRoot)
                {
                    marker.Node.GetParent()?.RemoveChild(marker.Node);
                    _armyRoot.AddChild(marker.Node);
                }
                marker.Node.Name = $"DivisionStack_{nation.Tag}_{lead.ProvinceId}_{lead.Id}";

                var textureChanged = marker.NationId != nation.Id ||
                    marker.FlagVisualId != NationalFlagVisualId(nation) ||
                    !string.Equals(marker.TemplateId, templateId, StringComparison.Ordinal);
                if (textureChanged)
                {
                    marker.FullPlateTexture = ArmyCounterTexture(nation, templateId);
                    marker.NationId = nation.Id;
                    marker.TemplateId = templateId;
                }
                if (textureChanged || !marker.LayoutInitialized || marker.FlatLayout != _flatMap)
                    SizeArmyCounter(marker);
            }
            marker.FlagVisualId = NationalFlagVisualId(nation);
            if (marker.Node is null) continue;

            var location = ArmyPresentationLocation(marker.Stack);
            var position = ArmyMarkerPosition(marker);
            marker.TargetPosition = position;
            if (!marker.MotionSettled)
            {
                // A new province bucket starts at the formation's last drawn position
                // when available. An unrelated node from the pool starts at the new target.
                marker.DisplayPosition = _previousArmyPositions.TryGetValue(lead.Id, out var previous)
                    && previous != Vector3.Zero ? previous : position;
                marker.Node.Position = marker.DisplayPosition;
                marker.MotionSettled = true;
                InvalidateFlatCounterDensity();
            }
            marker.Normal = SurfacePoint(location.Latitude, location.Longitude, 1f);
            OrientSurfaceArmyCard(marker);
            if (!_flatMap) SizeSurfaceArmyCounter(marker);
            // Diagnostic geography is independent of the rendered position.
            if (marker.LastMetaProvinceId != lead.ProvinceId &&
                _campaign.ProvinceMap is { } topology && lead.ProvinceId > 0 &&
                lead.ProvinceId <= topology.ProvinceCount)
            {
                marker.LastMetaProvinceId = lead.ProvinceId;
                var center = topology.CenterOf(lead.ProvinceId);
                marker.Node.SetMeta("province_center_latlon", new Vector2(center.Latitude, center.Longitude));
            }

            var membership = new HashCode();
            foreach (var army in marker.Stack) membership.Add(army.Id);
            var signature = membership.ToHashCode();
            if (signature != marker.ActualFingerprint)
            {
                marker.ActualFingerprint = signature;
                var ids = new Godot.Collections.Array<int>();
                foreach (var army in marker.Stack) ids.Add(army.Id);
                marker.Node.SetMeta("actual_army_ids", ids);
                marker.Node.SetMeta("province_id", lead.ProvinceId);
                InvalidateFlatCounterDensity();
            }

            var divisions = 0f;
            var personnel = 0f;
            foreach (var army in marker.Stack)
            {
                divisions += army.Divisions;
                personnel += DivisionPersonnel(army);
            }
            var divCount = Mathf.CeilToInt(divisions);
            var menTenths = (int)MathF.Round(personnel / 100f);
            if (marker.ShownDivisions != divCount || marker.ShownMenTenths != menTenths)
            {
                var divChanged = marker.ShownDivisions != divCount;
                marker.ShownDivisions = divCount;
                marker.ShownMenTenths = menTenths;
                var countText = $"{divCount}\n{menTenths / 10f:0.#}k";
                if (!string.Equals(marker.CountText, countText, StringComparison.Ordinal))
                {
                    if (!marker.ShownCompact && (!_flatMap || marker.DisplayProvinces <= 1)) marker.Count.Text = countText;
                    marker.CountText = countText;
                    if (divChanged) InvalidateFlatCounterDensity();
                }
            }

            _armyMarkers.Add((marker.Node, marker.Normal));
            _armyByMarker[marker.Node] = marker.Stack;
        }

        PruneArmyCounterTextureCaches();
        CoverSharedProvinces(campaign);
        // Reconciliation and the visibility tick run at different rates. Cull new and
        // reused counters before rendering, including the overlays that ignore depth.
        RefreshArmyMarkerVisibility();

        _armyStructureFingerprint = ArmyStructureFingerprint(campaign);
    }

    private readonly HashSet<Node3D> _coveredCounters = new();
    private readonly Dictionary<int, ArmyStackMarker> _provinceLeader = new();

    /// <summary>
    /// One counter per province on the globe, the way Hearts of Iron draws it.
    ///
    /// Counters are bucketed by country and province, so two countries in one province - allies
    /// side by side, a garrison and a rising - made two counters on the same point. Both are
    /// billboards drawn without a depth test, and which one came out on top changed from frame
    /// to frame: two flags flickering through each other. Now the country with the most
    /// divisions there is drawn and the others stand behind it, hidden; the province, the
    /// roster and a box selection still reach every division. The flat map fans its counters
    /// out instead and is left as it is.
    /// </summary>
    private void CoverSharedProvinces(Simulation.Campaign campaign)
    {
        _coveredCounters.Clear();
        _provinceLeader.Clear();
        var playerId = campaign.Player?.Id ?? -1;
        foreach (var (bucket, marker) in _armyStacks)
        {
            if (bucket.Item2 <= 0 || marker.Node is null || marker.Stack.Count == 0) continue;
            if (!_provinceLeader.TryGetValue(bucket.Item2, out var leader))
            {
                _provinceLeader[bucket.Item2] = marker;
                continue;
            }
            var mine = Strength(marker);
            var theirs = Strength(leader);
            var wins = mine > theirs + 0.01f ||
                       (MathF.Abs(mine - theirs) <= 0.01f &&
                        (marker.Stack[0].NationId == playerId ||
                         (leader.Stack[0].NationId != playerId && marker.Stack[0].NationId < leader.Stack[0].NationId)));
            if (wins)
            {
                _coveredCounters.Add(leader.Node);
                _provinceLeader[bucket.Item2] = marker;
            }
            else _coveredCounters.Add(marker.Node);
        }
        if (_flatMap) _coveredCounters.Clear();
        foreach (var node in _coveredCounters)
            if (IsInstanceValid(node) && node.Visible) node.Visible = false;

        static float Strength(ArmyStackMarker stack)
        {
            var divisions = 0f;
            foreach (var army in stack.Stack) divisions += army.Divisions;
            return divisions;
        }
    }

    private void CreateArmyStackMarker(ArmyStackMarker marker, Simulation.Nation nation,
        Simulation.Army lead, string templateId)
    {

        {
            var initialPos = ArmyMarkerPosition(marker);
            var node = new Node3D
            {
                Name = $"DivisionStack_{nation.Tag}_{lead.ProvinceId}_{lead.Id}",
                Position = initialPos
            };
            _armyRoot.AddChild(node);
            marker.Node = node;
            marker.TargetPosition = initialPos;
            marker.DisplayPosition = initialPos;
            marker.NationId = nation.Id;
            marker.TemplateId = templateId;

            var plate = new Sprite3D
            {
                Texture = ArmyCounterTexture(nation, templateId),
                PixelSize = CounterPlatePixelSize * _counterScale,
                FixedSize = false,
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Modulate = Colors.White
            };
            node.AddChild(plate);
            marker.Plate = plate;
            marker.FullPlateTexture = plate.Texture;
            marker.Glow = AttachSelectionGlow(plate, 176, 104, 0);
            marker.ConditionBars = new Sprite3D
            {
                Name = "OrganisationEquipmentBars", Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                RenderPriority = 10,
                NoDepthTest = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest
            };
            // A sibling of the plate: status colours must not inherit its red battle tint.
            node.AddChild(marker.ConditionBars);

            var divisions = new Label3D
            {
                Name = "DivisionCount",
                Text = "",
                FontSize = 21,
                PixelSize = CounterTextPixelSize * _counterScale,
                LineSpacing = -5f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                FixedSize = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                OutlineSize = Mathf.RoundToInt(CounterOutlineSize * _counterScale),
                Modulate = Ui.Colour("military.ready"),
                OutlineModulate = Ui.Colour("globe.label_outline"),
                Position = Vector3.Zero,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                RenderPriority = 20,
                NoDepthTest = true
            };
            node.AddChild(divisions);
            marker.Count = divisions;
            SizeArmyCounter(marker);

        }
    }

    /// <summary>
    /// Which counter a division belongs to: its country and the province it stands in.
    ///
    /// One rule for everybody. The player's own divisions were bucketed by province and
    /// everybody else's by army group or by whole state, so a foreign army spread across
    /// thirty provinces of front drew as a single counter reading x122 somewhere in the
    /// middle of the country. The map was telling the player something that was not true:
    /// those divisions were on the line, in a line, and the map said they were in a heap.
    ///
    /// A division with no province falls back to its own position rounded to a twentieth
    /// of a degree, which is fine ground - two divisions merge only if they are within a
    /// few kilometres of each other, and they have nowhere else to be drawn anyway.
    /// </summary>
    private (int NationId, int LocationId, int FallbackLatitude, int FallbackLongitude)
        ArmyPresentationBucket(Simulation.Campaign campaign, Simulation.Army division)
    {
        // Province membership never depends on zoom, projection, visibility or
        // selection. A close view must not fan one province out into dozens of
        // displaced cards. Individual divisions remain accessible in the roster.
        if (division.ProvinceId > 0) return (division.NationId, division.ProvinceId, 0, 0);
        return (division.NationId, 0, Mathf.RoundToInt(division.Latitude * 20f),
            Mathf.RoundToInt(division.Longitude * 20f));
    }

    private static (float Latitude, float Longitude) AverageArmyLocation(IReadOnlyList<Simulation.Army> stack)
    {
        if (stack.Count == 0) return (0f, 0f);
        var latitude = 0f;
        var sin = 0f;
        var cos = 0f;
        for (var index = 0; index < stack.Count; index++)
        {
            var division = stack[index];
            latitude += division.Latitude;
            var radians = Mathf.DegToRad(division.Longitude);
            sin += MathF.Sin(radians);
            cos += MathF.Cos(radians);
        }
        return (latitude / stack.Count, Mathf.RadToDeg(MathF.Atan2(sin, cos)));
    }

    private void SyncArmyMarkers()
    {
        // Dropping a selection whose division no longer exists. All() over the army list
        // per selected id was quadratic in the size of a box selection.
        if (_selectedArmyIds.Count > 0)
        {
            _liveArmyIds.Clear();
            foreach (var army in _campaign.Armies)
                if (Simulation.ArmyInteraction.CanSelect(_campaign, army)) _liveArmyIds.Add(army.Id);
            _selectedArmyIds.RemoveWhere(id => !_liveArmyIds.Contains(id));
        }
        // Rebuilding the counters is a full pass over every army: dictionaries, template
        // votes, strings, node transforms. At eight passes a second it was measurable even
        // with the world paused and nothing moving. The fingerprint answers the only
        // question that matters here - did any counter's identity, place or size change -
        // and the expensive pass is skipped when it did not.
        if (ArmyStructureFingerprint(_campaign) == _armyStructureFingerprint)
        {
            SyncArmyMarkerReadouts();
            return;
        }
        ReconcileArmyMarkers(_campaign);
    }

    private void SyncArmyMarkerReadouts()
    {
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !IsInstanceValid(marker.Node) || !marker.Node.Visible) continue;
            if (marker.Plate is not null && !marker.Plate.Visible) continue;
            var stack = marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack;
            if (stack.Count == 0) continue;
            var divisions = 0f;
            var personnel = 0f;
            for (var i = 0; i < stack.Count; i++)
            {
                divisions += stack[i].Divisions;
                personnel += DivisionPersonnel(stack[i]);
            }
            var divCount = Mathf.CeilToInt(divisions);
            var menTenths = (int)MathF.Round(personnel / 100f);
            if (marker.ShownDivisions == divCount && marker.ShownMenTenths == menTenths) continue;
            var divChanged = marker.ShownDivisions != divCount;
            marker.ShownDivisions = divCount;
            marker.ShownMenTenths = menTenths;
            var text = marker.ShownCompact
                ? divCount.ToString()
                : $"{divCount}\n{menTenths / 10f:0.#}k";
            if (!string.Equals(marker.CountText, text, StringComparison.Ordinal))
            {
                marker.CountText = text;
                if (marker.Count is not null && marker.Count.Visible &&
                    (!_flatMap || marker.DisplayProvinces <= 1 || marker.Plate?.Visible == true))
                    marker.Count.Text = text;
                if (divChanged) InvalidateFlatCounterDensity();
            }
        }
    }

    private readonly HashSet<int> _liveArmyIds = new();

    private int ArmyStructureFingerprint(Simulation.Campaign campaign)
    {
        var hash = new HashCode();
        hash.Add(FlagCatalog.VisualStateHash(campaign));
        hash.Add(SettingsCatalog.Flag("movement_flags"));
        hash.Add(SettingsCatalog.Chosen("map_divisions"));
        hash.Add(ShownCountryId);
        hash.Add(campaign.Armies.Count);
        foreach (var army in campaign.Armies)
        {
            hash.Add(army.Id);
            hash.Add(army.NationId);
            hash.Add(army.TemplateId);
            hash.Add(army.EmbarkedFleetId >= 0);
            var bucket = ArmyPresentationBucket(campaign, army);
            hash.Add(bucket.LocationId);
            hash.Add(bucket.FallbackLatitude);
            hash.Add(bucket.FallbackLongitude);
        }
        return hash.ToHashCode();
    }

    private static int DivisionPersonnel(Simulation.Army division) => division.Divisions > 1.01f
        ? (int)MathF.Round(division.Divisions * Simulation.MilitaryBootstrap.DivisionSize * division.Strength)
        : Math.Max(0, division.Personnel);

}
