using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DealOrForce.Presentation;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Standalone renderer for the Blender 5.3 stylized-realistic 3D PBR city, capital and port
/// miniatures. Builds 8x4 (32) spatial chunks on a worker thread, uploads MultiMesh buffers
/// once per map revision, scales footprints and heights on the GPU via shader uniform, and
/// culls at O(32) chunk granularity on the main thread (zero main-thread city loops).
/// </summary>
public sealed class CityMiniatureRenderer
{
    private const float GlobeRadius = 5f;
    private const float FlatMapWidth = 20f;
    private const float FlatMapHeight = 10f;
    private const float FlatLineZ = 0.0042f;
    private const int KindCount = 3;

    public readonly record struct FrameInput(
        Node3D Parent,
        Camera3D Camera,
        Campaign Campaign,
        IReadOnlyList<CityDefinition> Cities,
        CityCatalog.MapAnchor[] Anchors,
        bool[] IsCapital,
        bool MapApplied,
        bool FlatMap,
        float Distance,
        float FlatZoom,
        float FlatX,
        bool QualityAllowed,
        bool LabelsEnabled,
        bool? VerificationOverride,
        string LookMode,
        Vector3 SunDirection);

    private sealed class PreparedBatch
    {
        public ProvinceTopology Map;
        public int Revision;
        public CityMiniatureLayout.ObjMeshData[] MeshData;
        public CityMiniatureLayout.Placement[] AllPlacements;
        public ChunkSlice[] Chunks;
    }

    private sealed class ChunkSlice
    {
        public Vector3 GlobeCenterNormal;
        public float CenterLon;
        public float CenterLat;
        public CityMiniatureLayout.Placement[] Placements;
        public Transform3D[][] GlobeTransforms;
        public Transform3D[][] FlatTransforms;
        public Color[][] Colors;
    }

    private Node3D _root;
    private Node3D[] _chunkNodes;
    private MultiMeshInstance3D[,] _batches;
    private ArrayMesh[] _meshes;
    private ShaderMaterial _material;

    private Task<PreparedBatch> _buildTask;
    private (ProvinceTopology Map, int Revision) _buildingFor;
    private (ProvinceTopology Map, int Revision) _uploadedFor;
    private PreparedBatch _activeBatch;
    private bool _uploadedFlat;
    private bool _visibilityDirty = true;

    public int VisibleInstanceCount { get; private set; }
    public bool IsHandlingCurrentLook { get; private set; }

    public void InvalidateVisibility() => _visibilityDirty = true;

    public void Update(in FrameInput input)
    {
        var enabledByMood = input.VerificationOverride.HasValue
            ? input.VerificationOverride.Value
            : input.LookMode == MapLook.RealisticValue && SettingsCatalog.Flag("map_city_models");

        IsHandlingCurrentLook = input.VerificationOverride.HasValue ||
                                input.LookMode == MapLook.RealisticValue ||
                                input.LookMode == MapLook.PaperValue;

        var visibility = CityMiniatureLayout.Visibility(input.FlatMap, input.Distance, input.FlatZoom);
        var wanted = enabledByMood &&
                     input.LookMode != MapLook.PaperValue &&
                     input.MapApplied &&
                     input.QualityAllowed &&
                     input.LabelsEnabled &&
                     visibility > 0.015f &&
                     input.Campaign?.ProvinceMap is not null &&
                     input.Cities is { Count: > 0 } &&
                     input.Anchors.Length == input.Cities.Count;

        if (!wanted)
        {
            HideAll();
            return;
        }

        EnsureSceneNodes(input.Parent);
        PollOrStartWorker(in input);

        if (_activeBatch is null)
        {
            HideAll();
            return;
        }

        if (_uploadedFlat != input.FlatMap || _visibilityDirty)
        {
            ApplyProjectionTransforms(_activeBatch, input.FlatMap);
            _uploadedFlat = input.FlatMap;
            _visibilityDirty = false;
        }

        UpdateShaderUniforms(visibility, input.FlatMap, input.SunDirection);
        CullChunks(in input);
    }

    public bool VisibleFootprintsOnLand(ProvinceTopology map, HashSet<int> destroyed)
    {
        if (_activeBatch is null || map is null || VisibleInstanceCount == 0 || _chunkNodes is null)
            return true;
        for (var c = 0; c < CityMiniatureLayout.ChunkCount; c++)
        {
            if (!_chunkNodes[c].Visible) continue;
            var slice = _activeBatch.Chunks[c];
            for (var i = 0; i < slice.Placements.Length; i++)
            {
                var p = slice.Placements[i];
                if (!CityMiniatureLayout.FootprintOnLand(map, destroyed, p.Latitude, p.Longitude, p.FootprintDeg, p.LongitudeScale))
                    return false;
            }
        }
        return true;
    }

    private void HideAll()
    {
        VisibleInstanceCount = 0;
        if (_root is not null && GodotObject.IsInstanceValid(_root))
            _root.Visible = false;
    }

    private void EnsureSceneNodes(Node3D parent)
    {
        if (_root is not null && GodotObject.IsInstanceValid(_root)) return;
        _material = new ShaderMaterial { Shader = new Shader { Code = MiniatureShaderCode } };
        _root = new Node3D { Name = "CityMiniatures3D", Visible = false };
        parent.AddChild(_root);

        _chunkNodes = new Node3D[CityMiniatureLayout.ChunkCount];
        _batches = new MultiMeshInstance3D[CityMiniatureLayout.ChunkCount, KindCount];
        for (var c = 0; c < CityMiniatureLayout.ChunkCount; c++)
        {
            var chunkNode = new Node3D { Name = $"CityChunk_{c:D2}", Visible = false };
            _root.AddChild(chunkNode);
            _chunkNodes[c] = chunkNode;
            for (var k = 0; k < KindCount; k++)
            {
                var mmi = new MultiMeshInstance3D
                {
                    Name = CityMiniatureLayout.ModelNames[k],
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                    MaterialOverride = _material,
                    Multimesh = new MultiMesh
                    {
                        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                        UseColors = true,
                        InstanceCount = 0
                    }
                };
                chunkNode.AddChild(mmi);
                _batches[c, k] = mmi;
            }
        }
    }

    private void PollOrStartWorker(in FrameInput input)
    {
        var map = input.Campaign.ProvinceMap;
        var rev = input.Campaign.MapRevision;
        if (_buildTask is { IsCompleted: true })
        {
            if (_buildTask.Status == TaskStatus.RanToCompletion && _buildTask.Result is { } ready)
                UploadBatch(ready, input.FlatMap);
            _buildTask = null;
        }

        var matchesUploaded = ReferenceEquals(_uploadedFor.Map, map) && _uploadedFor.Revision == rev;
        var matchesBuilding = _buildTask is not null && ReferenceEquals(_buildingFor.Map, map) && _buildingFor.Revision == rev;
        if (matchesUploaded || matchesBuilding || _buildTask is not null) return;

        var citiesSnap = input.Cities;
        var anchorsSnap = (CityCatalog.MapAnchor[])input.Anchors.Clone();
        var capsSnap = input.IsCapital is not null ? (bool[])input.IsCapital.Clone() : Array.Empty<bool>();
        var destroyedSnap = input.Campaign.DestroyedProvinces.Count > 0
            ? new HashSet<int>(input.Campaign.DestroyedProvinces)
            : null;
        var urbanSnap = new Dictionary<int, long>(input.Campaign.States.Count);
        for (var i = 0; i < input.Campaign.States.Count; i++)
        {
            var st = input.Campaign.States[i];
            if (st is not null) urbanSnap[st.MapIndex] = CityLife.Urban(st);
        }
        var needMeshes = _meshes is null;
        string[] objTexts = needMeshes ? ReadObjSources() : null;

        _buildingFor = (map, rev);
        _buildTask = Task.Run(() => BuildWorkerBatch(
            map, rev, citiesSnap, anchorsSnap, capsSnap, urbanSnap, destroyedSnap, objTexts));
    }

    private static string[] ReadObjSources()
    {
        var texts = new string[KindCount];
        var folder = CityMiniatureLayout.ModelsDirectory();
        for (var k = 0; k < KindCount; k++)
        {
            var name = CityMiniatureLayout.ModelNames[k];
            var resPath = $"res://game/gfx/models/cities/{name}.obj";
            if (Godot.FileAccess.FileExists(resPath))
                texts[k] = Godot.FileAccess.GetFileAsString(resPath);
            else
            {
                var diskPath = Path.Combine(folder, name + ".obj");
                texts[k] = File.Exists(diskPath) ? File.ReadAllText(diskPath) : "";
            }
        }
        return texts;
    }

    private static PreparedBatch BuildWorkerBatch(
        ProvinceTopology map,
        int rev,
        IReadOnlyList<CityDefinition> cities,
        CityCatalog.MapAnchor[] anchors,
        bool[] caps,
        Dictionary<int, long> urbanByState,
        HashSet<int> destroyed,
        string[] objTexts)
    {
        CityMiniatureLayout.ObjMeshData[] meshData = null;
        if (objTexts is not null)
        {
            meshData = new CityMiniatureLayout.ObjMeshData[KindCount];
            for (var k = 0; k < KindCount; k++)
                meshData[k] = CityMiniatureLayout.ParseObj(CityMiniatureLayout.ModelNames[k], objTexts[k]);
        }

        var placements = CityMiniatureLayout.BuildPlacements(cities, anchors, caps, urbanByState, map, destroyed);
        var perChunk = new List<CityMiniatureLayout.Placement>[CityMiniatureLayout.ChunkCount];
        for (var c = 0; c < CityMiniatureLayout.ChunkCount; c++) perChunk[c] = new List<CityMiniatureLayout.Placement>();
        for (var i = 0; i < placements.Length; i++) perChunk[placements[i].ChunkIndex].Add(placements[i]);

        var chunks = new ChunkSlice[CityMiniatureLayout.ChunkCount];
        for (var c = 0; c < CityMiniatureLayout.ChunkCount; c++)
        {
            var (cLat, cLon) = CityMiniatureLayout.ChunkCenterDegrees(c);
            var list = perChunk[c];
            var byKindGlobe = new List<Transform3D>[KindCount];
            var byKindFlat = new List<Transform3D>[KindCount];
            var byKindCol = new List<Color>[KindCount];
            for (var k = 0; k < KindCount; k++)
            {
                byKindGlobe[k] = new List<Transform3D>();
                byKindFlat[k] = new List<Transform3D>();
                byKindCol[k] = new List<Color>();
            }

            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i];
                var k = (int)p.Kind;
                var cb = MathF.Cos(p.BearingRad);
                var sb = MathF.Sin(p.BearingRad);

                var (c0, c1, c2, normal) = CityMiniatureLayout.GlobeBasisColumns(
                    p.Latitude, p.Longitude, p.BearingRad, p.FootprintDeg, GlobeRadius);
                var gRot = new Basis(
                    new Vector3(c0.X, c0.Y, c0.Z),
                    new Vector3(c1.X, c1.Y, c1.Z),
                    new Vector3(c2.X, c2.Y, c2.Z));
                var gOrigin = new Vector3(normal.X, normal.Y, normal.Z) * (GlobeRadius * 1.00018f);
                byKindGlobe[k].Add(new Transform3D(gRot, gOrigin));

                var flatW = p.FootprintDeg * 1.35f * (FlatMapWidth / 360f / p.LongitudeScale);
                var flatD = p.FootprintDeg * 1.35f * (FlatMapHeight / 180f);
                var fRot = new Basis(
                    new Vector3(cb, sb, 0f) * flatW,
                    new Vector3(-sb, cb, 0f) * flatD,
                    new Vector3(0f, 0f, flatD * 1.15f));
                var fOrigin = new Vector3(p.Longitude / 360f * FlatMapWidth, p.Latitude / 180f * FlatMapHeight, FlatLineZ);
                byKindFlat[k].Add(new Transform3D(fRot, fOrigin));

                byKindCol[k].Add(new Color(p.Shade, p.Shade, p.Shade, 1f));
            }

            var gArr = new Transform3D[KindCount][];
            var fArr = new Transform3D[KindCount][];
            var cArr = new Color[KindCount][];
            for (var k = 0; k < KindCount; k++)
            {
                gArr[k] = byKindGlobe[k].ToArray();
                fArr[k] = byKindFlat[k].ToArray();
                cArr[k] = byKindCol[k].ToArray();
            }

            chunks[c] = new ChunkSlice
            {
                CenterLat = cLat,
                CenterLon = cLon,
                GlobeCenterNormal = SpherePoint(cLat, cLon, 1f).Normalized(),
                Placements = list.ToArray(),
                GlobeTransforms = gArr,
                FlatTransforms = fArr,
                Colors = cArr
            };
        }

        return new PreparedBatch
        {
            Map = map,
            Revision = rev,
            MeshData = meshData,
            AllPlacements = placements,
            Chunks = chunks
        };
    }

    private void UploadBatch(PreparedBatch batch, bool flatMap)
    {
        if (batch.MeshData is not null && _meshes is null)
        {
            _meshes = new ArrayMesh[KindCount];
            for (var k = 0; k < KindCount; k++)
                _meshes[k] = BuildGodotMesh(batch.MeshData[k]);
            for (var c = 0; c < CityMiniatureLayout.ChunkCount; c++)
                for (var k = 0; k < KindCount; k++)
                    _batches[c, k].Multimesh.Mesh = _meshes[k];
        }

        _activeBatch = batch;
        _uploadedFor = (batch.Map, batch.Revision);
        ApplyProjectionTransforms(batch, flatMap);
        _uploadedFlat = flatMap;
        _visibilityDirty = false;
    }

    private void ApplyProjectionTransforms(PreparedBatch batch, bool flatMap)
    {
        for (var c = 0; c < CityMiniatureLayout.ChunkCount; c++)
        {
            var slice = batch.Chunks[c];
            _chunkNodes[c].Position = Vector3.Zero;
            for (var k = 0; k < KindCount; k++)
            {
                var transforms = flatMap ? slice.FlatTransforms[k] : slice.GlobeTransforms[k];
                var colors = slice.Colors[k];
                var mm = _batches[c, k].Multimesh;
                mm.InstanceCount = transforms.Length;
                mm.VisibleInstanceCount = transforms.Length;
                for (var i = 0; i < transforms.Length; i++)
                {
                    mm.SetInstanceTransform(i, transforms[i]);
                    mm.SetInstanceColor(i, colors[i]);
                }
            }
        }
    }

    private static ArrayMesh BuildGodotMesh(CityMiniatureLayout.ObjMeshData data)
    {
        var mesh = new ArrayMesh();
        if (data.TriangleCount <= 0) return mesh;
        var vertCount = data.Positions.Length / 3;
        var verts = new Vector3[vertCount];
        var norms = new Vector3[vertCount];
        var cols = new Color[vertCount];
        for (var i = 0; i < vertCount; i++)
        {
            var p3 = i * 3;
            var c4 = i * 4;
            verts[i] = new Vector3(data.Positions[p3], data.Positions[p3 + 1], data.Positions[p3 + 2]);
            norms[i] = new Vector3(data.Normals[p3], data.Normals[p3 + 1], data.Normals[p3 + 2]);
            cols[i] = new Color(data.ColorsRgba[c4], data.ColorsRgba[c4 + 1], data.ColorsRgba[c4 + 2], data.ColorsRgba[c4 + 3]);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = norms;
        arrays[(int)Mesh.ArrayType.Color] = cols;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private void UpdateShaderUniforms(float visibility, bool flatMap, Vector3 sunDir)
    {
        if (_material is null) return;
        _material.SetShaderParameter("zoom_scale", visibility);
        _material.SetShaderParameter("flat_map", flatMap ? 1f : 0f);
        _material.SetShaderParameter("sun_direction", sunDir);
    }

    private void CullChunks(in FrameInput input)
    {
        _root.Visible = true;
        var total = 0;
        var camDir = input.Camera is not null && GodotObject.IsInstanceValid(input.Camera)
            ? input.Camera.GlobalPosition.Normalized()
            : Vector3.Back;

        for (var c = 0; c < CityMiniatureLayout.ChunkCount; c++)
        {
            var slice = _activeBatch.Chunks[c];
            if (slice.Placements.Length == 0)
            {
                _chunkNodes[c].Visible = false;
                continue;
            }

            bool visible;
            if (input.FlatMap)
            {
                var baseX = slice.CenterLon / 360f * FlatMapWidth;
                var shift = 0f;
                if (baseX - input.FlatX > FlatMapWidth * 0.5f) shift = -FlatMapWidth;
                else if (baseX - input.FlatX < -FlatMapWidth * 0.5f) shift = FlatMapWidth;
                _chunkNodes[c].Position = new Vector3(shift, 0f, 0f);
                var dx = MathF.Abs(baseX + shift - input.FlatX);
                visible = dx <= input.FlatZoom * 2.6f + 3.2f;
            }
            else
            {
                _chunkNodes[c].Position = Vector3.Zero;
                // Chunk spans 45 deg lon x 45 deg lat (~0.55 rad radius); dot > -0.32 keeps whole horizon chunk.
                visible = slice.GlobeCenterNormal.Dot(camDir) > -0.32f;
            }

            _chunkNodes[c].Visible = visible;
            if (visible) total += slice.Placements.Length;
        }
        VisibleInstanceCount = total;
    }

    private static Vector3 SpherePoint(float latitude, float longitude, float radius)
    {
        var lat = Mathf.DegToRad(latitude);
        var lon = Mathf.DegToRad(longitude + 180f);
        var h = Mathf.Cos(lat);
        return new Vector3(h * Mathf.Sin(lon), Mathf.Sin(lat), h * Mathf.Cos(lon)) * radius;
    }

    private const string MiniatureShaderCode = @"shader_type spatial;
render_mode cull_back, depth_draw_opaque;
uniform float zoom_scale = 1.0;
uniform float flat_map = 0.0;
uniform vec3 sun_direction = vec3(0.35, 0.55, 0.75);
varying float window_mask;
varying vec3 world_norm;
void vertex() {
    float xy_fade = sqrt(clamp(zoom_scale, 0.0, 1.0));
    VERTEX.xy *= xy_fade;
    VERTEX.z *= clamp(zoom_scale, 0.0, 1.0);
    window_mask = COLOR.a;
    world_norm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}
void fragment() {
    float is_window = clamp(window_mask, 0.0, 1.0);
    ALBEDO = COLOR.rgb;
    ROUGHNESS = mix(0.76, 0.32, is_window);
    METALLIC = 0.0;
    SPECULAR = 0.28;
    float sun_dot = dot(normalize(world_norm), normalize(sun_direction));
    float twilight = flat_map > 0.5 ? 0.38 : clamp(1.0 - smoothstep(-0.18, 0.42, sun_dot), 0.28, 1.0);
    EMISSION = vec3(0.95, 0.93, 0.88) * is_window * twilight * 0.42;
}";
}
