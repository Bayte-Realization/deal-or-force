using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace DealOrForce;

/// <summary>
/// The world's trade, moving: ships on the sea lanes between the busiest partners, lorries on
/// the roads between neighbours.
///
/// The three dozen largest trading pairs of the world each run a few small ships or lorries -
/// more for the larger trade - along the way the trade actually goes: by sea from one country's
/// main port to the other's, along the lanes a fleet would sail, or by road where two countries
/// share a border, from capital to capital over their own provinces. Every lane is checked
/// before it is drawn - a sea lane that touches land, or a road that runs over water, is not
/// drawn at all - and rounded into a curve only where the curve stays where the lane is.
///
/// The lanes are a faint dashed line that runs slowly in the direction of the trade; the
/// vehicles are the models in game/gfx/models/trade (made in Blender; a mod adds a ship or a
/// lorry by adding a file there), lit like the ground, a few pixels long at any distance. They
/// stop when the clock does. The routes are drawn up again once a month, two a frame.
/// "map_trade_traffic" switches the whole layer off.
/// </summary>
public sealed partial class GlobePresentation
{
    private const int TradeRouteLimit = 36;
    private const int TradeMoversPerRoute = 24;
    private const float ShipDegreesPerSecond = 0.8f;
    private const float LorryDegreesPerSecond = 0.4f;
    /// <summary>How long a model is drawn on the globe at the reference distance, in globe units: a few pixels.</summary>
    private const float ShipDisplayLength = 0.03f;
    private const float LorryDisplayLength = 0.013f;
    private const float LaneHalfWidth = 0.0022f;
    private const int TradeLinesDrawn = 18;

    private sealed class TradeLane
    {
        public int A, B;
        public bool Sea;
        public int Movers;
        public readonly List<Vector2> Points = new(); // (latitude, longitude), half a degree or less apart
        public readonly List<float> Along = new();     // cumulative length in degrees
        public float Length;
    }

    private sealed class TradeModel
    {
        public string Name = "";
        public bool Ship;
        public int Weight = 1;
        public float LengthMetres = 1f;
        public MultiMeshInstance3D Layer;
        /// <summary>A glTF model brings its own materials; an inline one is painted by its vertex colours.</summary>
        public bool OwnMaterials;
    }

    private bool _tradeTrafficShown = true;
    private Node3D _tradeRoot;
    private readonly List<TradeModel> _tradeModels = new();
    private readonly List<TradeLane> _tradeLanes = new();
    private readonly List<TradeLane> _tradeLanesNext = new();
    private readonly Queue<(int A, int B, float Value)> _tradeLanePending = new();
    private readonly Dictionary<int, (int Province, float Latitude, float Longitude)> _tradePorts = new();
    private readonly Dictionary<int, int> _tradeCapitalProvinces = new();
    private readonly List<float> _tradeWaypoints = new();
    private int _tradeLanesMonth = int.MinValue;
    private float _tradeLanesTop = 1f;
    private double _tradeClock;
    private MeshInstance3D _tradeLaneLines;
    private ImmediateMesh _tradeLaneMesh;
    private ShaderMaterial _tradeLaneMaterial;
    private int _tradeLaneFingerprint = int.MinValue;

    private const string TradeLaneShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;
uniform float period = 1.5;
uniform float duty = 0.48;
uniform float phase = 0.0;
void fragment() {
    float d = fract(UV.x / period - phase);
    float dash = smoothstep(0.0, 0.07, d) * (1.0 - smoothstep(duty - 0.07, duty, d));
    float across = 1.0 - abs(UV.y * 2.0 - 1.0);
    ALBEDO = COLOR.rgb;
    ALPHA = COLOR.a * dash * smoothstep(0.0, 0.55, across);
}";

    /// <summary>The trade moving on the map, or not.</summary>
    public bool TradeTrafficShown
    {
        get => _tradeTrafficShown;
        set
        {
            _tradeTrafficShown = value;
            if (_tradeRoot is not null && IsInstanceValid(_tradeRoot)) _tradeRoot.Visible = value;
        }
    }

    private void CreateTradeTraffic(Node3D world)
    {
        _tradeRoot = new Node3D { Name = "TradeTraffic", Visible = _tradeTrafficShown };
        world.AddChild(_tradeRoot);
        var material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.82f };
        foreach (var model in LoadTradeModels())
        {
            if (!model.OwnMaterials) model.Layer.MaterialOverride = material;
            _tradeRoot.AddChild(model.Layer);
            _tradeModels.Add(model);
        }
        _tradeLaneMesh = new ImmediateMesh();
        _tradeLaneMaterial = new ShaderMaterial { Shader = new Shader { Code = TradeLaneShader } };
        _tradeLaneLines = new MeshInstance3D
        {
            Name = "TradeLanes", Mesh = _tradeLaneMesh, MaterialOverride = _tradeLaneMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _tradeRoot.AddChild(_tradeLaneLines);
    }

    /// <summary>
    /// Every model in game/gfx/models/trade, in metres: a JSON file carrying its own positions,
    /// normals and colours (what the Blender script in models/source writes), or a JSON file whose
    /// "mesh" names a .glb or .gltf beside it - any glTF a modeller exports, with its materials.
    /// See game/gfx/models/README.md.
    /// </summary>
    private static IEnumerable<TradeModel> LoadTradeModels()
    {
        foreach (var file in Simulation.GameDataLoader.DataFiles("gfx", "models", "trade"))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(Simulation.StructuredData.ReadJson(file), Simulation.GameDataLoader.ForgivingJson); }
            catch (Exception error) { GD.PushWarning($"[dof] trade model {file} ignored: {error.Message}"); continue; }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                Mesh mesh;
                var own = false;
                if (root.TryGetProperty("mesh", out var linked) && linked.ValueKind == JsonValueKind.String)
                {
                    mesh = LoadGltfMesh(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file) ?? "", linked.GetString() ?? ""));
                    own = true;
                }
                else mesh = InlineMesh(root);
                if (mesh is null) continue;
                var role = root.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : "ship";
                var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "trade";
                var length = root.TryGetProperty("length_m", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetSingle() : 1f;
                var weight = root.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number ? Math.Clamp(w.GetInt32(), 1, 100) : 1;
                yield return new TradeModel
                {
                    Name = name, Ship = !string.Equals(role, "lorry", StringComparison.OrdinalIgnoreCase), Weight = weight,
                    LengthMetres = MathF.Max(1f, length), OwnMaterials = own,
                    Layer = new MultiMeshInstance3D
                    {
                        Name = "Trade_" + name, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                        Multimesh = new MultiMesh
                        {
                            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh,
                            InstanceCount = TradeRouteLimit * TradeMoversPerRoute, VisibleInstanceCount = 0
                        }
                    }
                };
            }
        }
    }

    /// <summary>A mesh written out inline: positions, normals and colours per vertex, and triangles.</summary>
    private static Mesh InlineMesh(JsonElement root)
    {
        float[] Floats(string key) => root.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(v => v.GetSingle()).ToArray() : Array.Empty<float>();
        var positions = Floats("positions");
        var normals = Floats("normals");
        var colours = Floats("colors");
        var indices = root.TryGetProperty("indices", out var ie) && ie.ValueKind == JsonValueKind.Array
            ? ie.EnumerateArray().Select(v => v.GetInt32()).ToArray() : Array.Empty<int>();
        var count = positions.Length / 3;
        if (count == 0 || normals.Length != positions.Length || colours.Length != positions.Length || indices.Length < 3) return null;
        var vertices = new Vector3[count];
        var normalArray = new Vector3[count];
        var colourArray = new Color[count];
        for (var i = 0; i < count; i++)
        {
            vertices[i] = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
            normalArray[i] = new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
            colourArray[i] = new Color(colours[i * 3], colours[i * 3 + 1], colours[i * 3 + 2]);
        }
        // Godot winds front faces clockwise; the exported triangles are anticlockwise.
        for (var i = 0; i + 2 < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normalArray;
        arrays[(int)Mesh.ArrayType.Color] = colourArray;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>The first mesh of a glTF file, with its materials; null when the file will not load.</summary>
    private static Mesh LoadGltfMesh(string path)
    {
        if (!System.IO.File.Exists(path)) { GD.PushWarning($"[dof] trade model mesh {path} not found"); return null; }
        var document = new GltfDocument();
        var state = new GltfState();
        if (document.AppendFromFile(path, state) != Error.Ok) { GD.PushWarning($"[dof] trade model mesh {path} will not load"); return null; }
        var scene = document.GenerateScene(state);
        try { return FirstMesh(scene); }
        finally { scene?.Free(); }
    }

    private static Mesh FirstMesh(Node node)
    {
        if (node is null) return null;
        if (node is MeshInstance3D instance && instance.Mesh is not null) return instance.Mesh;
        foreach (var child in node.GetChildren())
            if (FirstMesh(child) is { } mesh) return mesh;
        return null;
    }

    private int[] _tradeModelCounts = Array.Empty<int>();
    private double _lastTradeDrawnClock = -1.0;
    private float _lastTradeDrawnZoom = -1f;
    private bool _lastTradeDrawnFlat;

    private void UpdateTradeTraffic(double delta)
    {
        if (_tradeRoot is null || !IsInstanceValid(_tradeRoot) || _campaign?.ProvinceMap is null) return;
        if (IsInstanceValid(_tradeLaneLines) && !Simulation.WorldLayers.On("trade_lanes")) _tradeLaneLines.Visible = false;
        if (!QualityTradeTraffic || !_tradeTrafficShown || !_mapApplied || _tradeModels.Count == 0 ||
            !Simulation.WorldLayers.On("trade_traffic"))
        {
            foreach (var model in _tradeModels) model.Layer.Multimesh.VisibleInstanceCount = 0;
            return;
        }
        PlanTradeLanes();
        if (!_campaign.IsPaused) _tradeClock += delta;

        // A few pixels at any distance: the size follows the camera out, and only a little in.
        var zoom = _flatMap ? Mathf.Clamp(_flatZoom / 1.6f, 0.45f, 2f) : Mathf.Clamp(_distance / 9f, 0.45f, 2f);
        if (Math.Abs(_tradeClock - _lastTradeDrawnClock) < 0.0001 &&
            MathF.Abs(zoom - _lastTradeDrawnZoom) < 0.002f &&
            _flatMap == _lastTradeDrawnFlat)
        {
            DrawTradeLanes(zoom);
            return;
        }
        _lastTradeDrawnClock = _tradeClock;
        _lastTradeDrawnZoom = zoom;
        _lastTradeDrawnFlat = _flatMap;

        if (_tradeModelCounts.Length < _tradeModels.Count)
            _tradeModelCounts = new int[_tradeModels.Count];
        else
            Array.Clear(_tradeModelCounts, 0, _tradeModels.Count);
        var counts = _tradeModelCounts;
        foreach (var lane in _tradeLanes)
        {
            if (lane.Length <= 0.01f) continue;
            var speed = lane.Sea ? ShipDegreesPerSecond : LorryDegreesPerSecond;
            for (var mover = 0; mover < lane.Movers; mover++)
            {
                var pick = PickModel(lane, mover);
                if (pick < 0) continue;
                var model = _tradeModels[pick];
                var layer = model.Layer.Multimesh;
                if (counts[pick] >= layer.InstanceCount) continue;
                // Half go one way, half the other, spread along the lane.
                var travelled = (float)((_tradeClock * speed + lane.Length * (mover + 0.5f) / lane.Movers) % lane.Length);
                var outward = mover % 2 == 0;
                var at = outward ? travelled : lane.Length - travelled;
                var (position, heading) = OnLane(lane, at, outward);
                var up = _flatMap ? Vector3.Back : position.Normalized();
                var forward = (heading - up * heading.Dot(up)).Normalized();
                if (forward.LengthSquared() < 0.5f) forward = up.Cross(Vector3.Up).Normalized();
                var side = forward.Cross(up).Normalized();
                // One scale for every ship and one for every lorry: a tanker stays shorter than a box ship.
                var scale = (model.Ship ? ShipDisplayLength / 300f : LorryDisplayLength / 16f) * zoom;
                layer.SetInstanceTransform(counts[pick]++, new Transform3D(new Basis(forward, up, side).Scaled(Vector3.One * scale), position));
            }
        }
        for (var i = 0; i < _tradeModels.Count; i++) _tradeModels[i].Layer.Multimesh.VisibleInstanceCount = counts[i];
        _tradeLaneMaterial.SetShaderParameter("phase", (float)(_tradeClock * 0.22));
        DrawTradeLanes(zoom);
    }

    /// <summary>Which model this mover is: a ship for a sea lane, a lorry for a road, by weight, fixed per mover.</summary>
    private int PickModel(TradeLane lane, int mover)
    {
        var total = 0;
        foreach (var model in _tradeModels) if (model.Ship == lane.Sea) total += model.Weight;
        if (total == 0) return -1;
        var roll = (int)((uint)HashCode.Combine(lane.A, lane.B, mover) % (uint)total);
        for (var i = 0; i < _tradeModels.Count; i++)
        {
            if (_tradeModels[i].Ship != lane.Sea) continue;
            roll -= _tradeModels[i].Weight;
            if (roll < 0) return i;
        }
        return -1;
    }

    /// <summary>Where on its lane a mover is, and which way it is facing, in the scene.</summary>
    private (Vector3 Position, Vector3 Heading) OnLane(TradeLane lane, float at, bool outward)
    {
        var index = lane.Along.BinarySearch(at);
        if (index < 0) index = ~index;
        index = Math.Clamp(index, 1, lane.Points.Count - 1);
        var from = lane.Points[index - 1];
        var to = lane.Points[index];
        var span = MathF.Max(0.0001f, lane.Along[index] - lane.Along[index - 1]);
        var t = Math.Clamp((at - lane.Along[index - 1]) / span, 0f, 1f);
        var latitude = from.X + (to.X - from.X) * t;
        var longitude = from.Y + WrapDegrees(to.Y - from.Y) * t;
        var radius = GlobeRadius * 1.0016f;
        var here = _flatMap ? FlatPoint(latitude, longitude, FlatMarkerZ) : SurfacePoint(latitude, longitude, radius);
        var ahead = _flatMap ? FlatPoint(to.X, from.Y + WrapDegrees(to.Y - from.Y), FlatMarkerZ) : SurfacePoint(to.X, to.Y, radius);
        var behind = _flatMap ? FlatPoint(from.X, from.Y, FlatMarkerZ) : SurfacePoint(from.X, from.Y, radius);
        return (here, outward ? ahead - behind : behind - ahead);
    }

    /// <summary>The lanes, faint and dashed, the dashes running from the first partner to the second.</summary>
    private void DrawTradeLanes(float zoom)
    {
        var fingerprint = HashCode.Combine(_tradeLanes.Count, _tradeLanesMonth, _flatMap, _flatMap ? (int)(_flatX * 4f) : 0, (int)(zoom * 8f));
        if (fingerprint == _tradeLaneFingerprint) return;
        _tradeLaneFingerprint = fingerprint;
        _tradeLaneMesh.ClearSurfaces();
        var halfWidth = LaneHalfWidth * zoom;
        // The lines of the busiest lanes only: where a dozen lanes share the Mediterranean, a dozen
        // lines read as rain. The ships on the rest say where they go.
        foreach (var lane in _tradeLanes.Take(TradeLinesDrawn))
        {
            var colour = lane.Sea ? new Color(0.86f, 0.82f, 0.68f, 0.26f) : new Color(0.9f, 0.76f, 0.5f, 0.3f);
            var open = false;
            Vector3? previous = null;
            for (var i = 0; i < lane.Points.Count; i++)
            {
                var point = lane.Points[i];
                var here = _flatMap ? FlatPoint(point.X, point.Y, FlatLineZ) : SurfacePoint(point.X, point.Y, GlobeRadius * 1.0013f);
                if (previous is { } last && _flatMap && MathF.Abs(here.X - last.X) > FlatMapWidth * 0.5f)
                {
                    if (open) _tradeLaneMesh.SurfaceEnd();
                    open = false;
                }
                if (!open) { _tradeLaneMesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip); open = true; }
                var next = lane.Points[Math.Min(lane.Points.Count - 1, i + 1)];
                var ahead = _flatMap ? FlatPoint(next.X, next.Y, FlatLineZ) : SurfacePoint(next.X, next.Y, GlobeRadius * 1.0013f);
                var tangent = (ahead - (previous ?? here)).Normalized();
                if (tangent == Vector3.Zero) tangent = Vector3.Right;
                var normal = _flatMap ? Vector3.Back : here.Normalized();
                var side = normal.Cross(tangent).Normalized() * halfWidth;
                // The ends fade in and out, so a lane begins at a port rather than at a full stop.
                var fade = Math.Clamp(MathF.Min(lane.Along[i], lane.Length - lane.Along[i]) / 1.5f, 0f, 1f);
                var shade = new Color(colour.R, colour.G, colour.B, colour.A * fade);
                _tradeLaneMesh.SurfaceSetColor(shade);
                _tradeLaneMesh.SurfaceSetUV(new Vector2(lane.Along[i], 0f));
                _tradeLaneMesh.SurfaceAddVertex(here + side);
                _tradeLaneMesh.SurfaceSetColor(shade);
                _tradeLaneMesh.SurfaceSetUV(new Vector2(lane.Along[i], 1f));
                _tradeLaneMesh.SurfaceAddVertex(here - side);
                previous = here;
            }
            if (open) _tradeLaneMesh.SurfaceEnd();
        }
    }

    private static float WrapDegrees(float delta)
    {
        while (delta > 180f) delta -= 360f;
        while (delta < -180f) delta += 360f;
        return delta;
    }

    /// <summary>
    /// Once a month: the largest trading pairs of the world, then their lanes worked out two a
    /// frame into a second list that replaces the first when it is complete.
    /// </summary>
    private void PlanTradeLanes()
    {
        var month = _campaign.ElapsedDays / 30;
        if (month != _tradeLanesMonth && _tradeLanePending.Count == 0)
        {
            _tradeLanesMonth = month;
            _tradePorts.Clear();
            _tradeCapitalProvinces.Clear();
            _tradeLanesNext.Clear();
            var pairs = new Dictionary<(int, int), float>();
            foreach (var nation in _campaign.Nations)
            {
                if (nation.Movement is not null || nation.Population <= 0) continue;
                foreach (var (partnerId, value) in nation.TradePartners)
                {
                    if (value <= 0f || partnerId == nation.Id) continue;
                    var key = nation.Id < partnerId ? (nation.Id, partnerId) : (partnerId, nation.Id);
                    pairs[key] = pairs.GetValueOrDefault(key) + value;
                }
            }
            var top = pairs.Where(p => _campaign.NationById(p.Key.Item1) is { Movement: null } &&
                                       _campaign.NationById(p.Key.Item2) is { Movement: null } &&
                                       !Simulation.CampaignDiplomacy.AtWar(_campaign, p.Key.Item1, p.Key.Item2))
                .OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(TradeRouteLimit).ToList();
            _tradeLanesTop = top.Count > 0 ? top[0].Value : 1f;
            foreach (var (key, value) in top) _tradeLanePending.Enqueue((key.Item1, key.Item2, value));
        }
        for (var work = 0; work < 2 && _tradeLanePending.Count > 0; work++)
        {
            var (a, b, value) = _tradeLanePending.Dequeue();
            var lane = Lane(a, b, value);
            if (lane is not null) _tradeLanesNext.Add(lane);
            if (_tradeLanePending.Count == 0)
            {
                _tradeLanes.Clear();
                _tradeLanes.AddRange(_tradeLanesNext);
                _tradeLanesNext.Clear();
                _tradeLaneFingerprint = int.MinValue;
            }
        }
    }

    private TradeLane Lane(int aId, int bId, float value)
    {
        var a = _campaign.NationById(aId);
        var b = _campaign.NationById(bId);
        if (a is null || b is null) return null;
        var lane = new TradeLane { A = aId, B = bId };
        var share = M01(value / MathF.Max(0.0001f, _tradeLanesTop));
        var map = _campaign.ProvinceMap;
        // Neighbours trade by road, capital to capital over their own ground.
        if (Simulation.CampaignDiplomacy.SharesLandBorder(_campaign, a.MapIndex, b.MapIndex) && RoadLane(lane, a, b))
            return Crew(lane, share);
        // Everyone else by sea, port to port, along the lanes a fleet would sail.
        if (!PortOf(a, out var portA) || !PortOf(b, out var portB)) return null;
        if (Simulation.SeaLanes.HarbourBasin(map, portA.Province) != Simulation.SeaLanes.HarbourBasin(map, portB.Province)) return null;
        if (!Simulation.SeaLanes.Plan(_campaign, a.Id, portA.Latitude, portA.Longitude, portB.Latitude, portB.Longitude, _tradeWaypoints, out _))
            return null;
        var corners = new List<Vector2> { new(portA.Latitude, portA.Longitude) };
        for (var i = 0; i + 1 < _tradeWaypoints.Count; i += 2) corners.Add(new Vector2(_tradeWaypoints[i], _tradeWaypoints[i + 1]));
        lane.Sea = true;
        return Shape(lane, corners, water: true) ? Crew(lane, share) : null;
    }

    private static float M01(float value) => Math.Clamp(value, 0f, 1f);

    /// <summary>
    /// How many are on a lane at once: one every few degrees of it - closer together on the
    /// busiest trade - so a stretch of sea between two great traders is never empty for long.
    /// </summary>
    private static TradeLane Crew(TradeLane lane, float share)
    {
        var spacing = lane.Sea ? 9f - 5f * share : 5f - 2.5f * share;
        lane.Movers = Math.Clamp((int)MathF.Ceiling(lane.Length / spacing), 1, TradeMoversPerRoute);
        return lane;
    }

    /// <summary>
    /// A road between two neighbours: the shortest way over their provinces from one capital to
    /// the other, from province centre to province centre - never across the sea between them.
    /// </summary>
    private bool RoadLane(TradeLane lane, Simulation.Nation a, Simulation.Nation b)
    {
        var map = _campaign.ProvinceMap;
        var controllers = _campaign.ProvinceController ?? map.ProvinceToNation;
        var from = CapitalProvince(a);
        var to = CapitalProvince(b);
        if (from <= 0 || to <= 0) return false;
        bool Allowed(int p) => p > 0 && p <= map.ProvinceCount && p < controllers.Length && !map.WaterByProvince[p] &&
                               (controllers[p] == a.MapIndex || controllers[p] == b.MapIndex);
        var path = LandPath(map, from, to, Allowed, 60000);
        if (path is null || path.Count < 2) return false;
        var corners = path.Select(p => { var c = map.CenterOf(p); return new Vector2(c.Latitude, c.Longitude); }).ToList();
        lane.Sea = false;
        return Shape(lane, corners, water: false);
    }

    /// <summary>A* over the province graph, by distance between province centres.</summary>
    private static List<int> LandPath(Simulation.ProvinceTopology map, int from, int to, Func<int, bool> allowed, int budget)
    {
        if (!allowed(from) || !allowed(to)) return null;
        var target = map.CenterOf(to);
        var goal = new Vector2(target.Latitude, target.Longitude);
        float Distance(int p, Vector2 q)
        {
            var c = map.CenterOf(p);
            var cos = MathF.Cos((c.Latitude + q.X) * 0.5f * MathF.PI / 180f);
            var dx = WrapDegrees(c.Longitude - q.Y) * cos;
            var dy = c.Latitude - q.X;
            return MathF.Sqrt(dx * dx + dy * dy);
        }
        var cost = new Dictionary<int, float> { [from] = 0f };
        var cameFrom = new Dictionary<int, int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(from, Distance(from, goal));
        var expanded = 0;
        while (open.TryDequeue(out var p, out _))
        {
            if (p == to)
            {
                var path = new List<int> { to };
                while (cameFrom.TryGetValue(path[^1], out var back)) path.Add(back);
                path.Reverse();
                return path;
            }
            if (++expanded > budget) return null;
            var here = cost[p];
            var at = map.CenterOf(p);
            var centre = new Vector2(at.Latitude, at.Longitude);
            foreach (var next in map.Neighbors[p])
            {
                if (!allowed(next)) continue;
                var step = here + Distance(next, centre);
                if (cost.TryGetValue(next, out var known) && known <= step) continue;
                cost[next] = step;
                cameFrom[next] = p;
                open.Enqueue(next, step + Distance(next, goal));
            }
        }
        return null;
    }

    /// <summary>The province of the country's capital: the one of its capital region nearest the capital city.</summary>
    private int CapitalProvince(Simulation.Nation nation)
    {
        if (_tradeCapitalProvinces.TryGetValue(nation.Id, out var known)) return known;
        var map = _campaign.ProvinceMap;
        var controllers = _campaign.ProvinceController ?? map.ProvinceToNation;
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var pass = 0; pass < 2 && best < 0; pass++)
            for (var p = 1; p <= map.ProvinceCount && p < controllers.Length; p++)
            {
                if (controllers[p] != nation.MapIndex || map.WaterByProvince[p]) continue;
                if (pass == 0 && map.ProvinceToState[p] != nation.CapitalStateMapIndex) continue;
                var c = map.CenterOf(p);
                var d = Simulation.NavalOperations.Distance(c.Latitude, c.Longitude, nation.CapitalLatitude, nation.CapitalLongitude);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = p;
            }
        _tradeCapitalProvinces[nation.Id] = best;
        return best;
    }

    /// <summary>The country's main port: its coastal province nearest the capital.</summary>
    private bool PortOf(Simulation.Nation nation, out (int Province, float Latitude, float Longitude) port)
    {
        if (_tradePorts.TryGetValue(nation.Id, out port)) return port.Province > 0;
        var map = _campaign.ProvinceMap;
        var controllers = _campaign.ProvinceController ?? map.ProvinceToNation;
        var best = -1;
        var bestDistance = float.MaxValue;
        foreach (var province in Simulation.SeaLanes.CoastalProvinces(map))
        {
            if (province <= 0 || province >= controllers.Length || controllers[province] != nation.MapIndex) continue;
            var centre = map.CenterOf(province);
            var distance = Simulation.NavalOperations.Distance(centre.Latitude, centre.Longitude, nation.CapitalLatitude, nation.CapitalLongitude);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = province;
        }
        port = (-1, 0f, 0f);
        if (best > 0 && Simulation.SeaLanes.TryHarbourPoint(map, best, out var latitude, out var longitude))
            port = (best, latitude, longitude);
        _tradePorts[nation.Id] = port;
        return port.Province > 0;
    }

    /// <summary>
    /// Round the lane's corners into a curve where the curve stays on its own element - water for
    /// a sea lane, land for a road - and keep the corner where it would not; then refuse the lane
    /// outright if, even so, it crosses the wrong element.
    /// </summary>
    private bool Shape(TradeLane lane, List<Vector2> corners, bool water)
    {
        if (corners.Count < 2) return false;
        var map = _campaign.ProvinceMap;
        bool Right(Vector2 point) => Simulation.SeaLanes.IsWaterAt(map, point.X, point.Y) == water;
        var shape = corners;
        for (var round = 0; round < 3; round++) shape = Chaikin(shape, Right);
        Densify(lane, shape);
        if (lane.Length <= 0.3f) return false;
        // The first and last stretch is a port or a capital: the ground there is whatever it is.
        // A canal or a strait narrower than the map (Suez, the Bosporus) is a short stretch the
        // raster calls land, and a road may cross a river or a lake; a long stretch is a ship
        // crossing a peninsula, or a lorry crossing a gulf, and the lane is not drawn.
        var wrong = 0;
        var samples = 0;
        var run = 0;
        var longest = 0;
        for (var i = 0; i < lane.Points.Count; i++)
        {
            if (lane.Along[i] < 0.6f || lane.Length - lane.Along[i] < 0.6f) continue;
            samples++;
            if (Right(lane.Points[i])) { run = 0; continue; }
            wrong++;
            longest = Math.Max(longest, ++run);
        }
        return samples == 0 || (longest <= 4 && wrong <= Math.Max(2, samples / 16));
    }

    /// <summary>One round of corner cutting, keeping any corner whose cut would leave the lane's element.</summary>
    private static List<Vector2> Chaikin(List<Vector2> points, Func<Vector2, bool> right)
    {
        if (points.Count < 3) return points;
        var result = new List<Vector2> { points[0] };
        for (var i = 1; i < points.Count - 1; i++)
        {
            var before = points[i - 1];
            var corner = points[i];
            var after = points[i + 1];
            var inFrom = Lerp(before, corner, 0.75f);
            var outTo = Lerp(corner, after, 0.25f);
            if (right(inFrom) && right(outTo) && right(Lerp(inFrom, outTo, 0.5f)))
            {
                result.Add(inFrom);
                result.Add(outTo);
            }
            else result.Add(corner);
        }
        result.Add(points[^1]);
        return result;
    }

    private static Vector2 Lerp(Vector2 a, Vector2 b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + WrapDegrees(b.Y - a.Y) * t);

    /// <summary>The corners of a lane, cut half a degree at a time so it lies on the globe, with the length run along it.</summary>
    private static void Densify(TradeLane lane, IReadOnlyList<Vector2> corners)
    {
        lane.Points.Clear();
        lane.Along.Clear();
        lane.Length = 0f;
        if (corners.Count < 2) return;
        lane.Points.Add(corners[0]);
        lane.Along.Add(0f);
        var total = 0f;
        for (var c = 1; c < corners.Count; c++)
        {
            var from = corners[c - 1];
            var to = corners[c];
            var dLon = WrapDegrees(to.Y - from.Y);
            var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(to.X - from.X), MathF.Abs(dLon)) * 2f));
            var previous = from;
            for (var step = 1; step <= steps; step++)
            {
                var t = step / (float)steps;
                var point = new Vector2(from.X + (to.X - from.X) * t, from.Y + dLon * t);
                var cos = MathF.Cos((previous.X + point.X) * 0.5f * MathF.PI / 180f);
                var dy = point.X - previous.X;
                var dx = (point.Y - previous.Y) * cos;
                total += MathF.Sqrt(dx * dx + dy * dy);
                lane.Points.Add(point);
                lane.Along.Add(total);
                previous = point;
            }
        }
        lane.Length = total;
    }
}
