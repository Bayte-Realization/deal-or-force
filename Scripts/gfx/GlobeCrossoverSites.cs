using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Modding;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private Node3D _crossoverSiteRoot;
    private readonly Dictionary<BuildingKind, ArrayMesh> _crossoverSiteMeshes = new();
    private readonly Dictionary<long, float> _crossoverSiteCellTops = new();
    private CrossoverWork[] _crossoverDisplayedWorks = Array.Empty<CrossoverWork>();

    private float CrossoverSiteRadius(CrossoverWork work)
    {
        float radius = GlobeRadius / 1024f * 1025.2f;
        foreach (long key in work.Blocks)
            if (_crossoverSiteCellTops.TryGetValue(key, out var top))
                radius = Math.Max(radius, (top + .8f) * GlobeRadius / 1024f);
        return radius;
    }

    private void RefreshCrossoverSiteHeights(CrossoverVoxel[] edits)
    {
        _crossoverSiteCellTops.Clear();
        foreach (var cell in edits.Where(c => c.block > 0 && !c.flora)) {
            var centre = cell.pose is { Length: 7 }
                ? new Vector3(cell.pose[0],cell.pose[1],cell.pose[2])
                : new Vector3(cell.x+.5f,cell.y+.5f,cell.z+.5f);
            _crossoverSiteCellTops[Campaign.CrossoverCellKey(cell.x,cell.y,cell.z)] = centre.Length() + .87f;
        }
        if (_crossoverSiteRoot is null) return;
        int index = 0;
        foreach (var work in _crossoverDisplayedWorks) {
            if (index >= _crossoverSiteRoot.GetChildCount()) break;
            var site = _crossoverSiteRoot.GetChild<MeshInstance3D>(index);
            var normal = site.Position.Normalized();
            var radius = CrossoverSiteRadius(work);
            site.Position = normal * radius;
            site.SetMeta("display_radius", radius);
            if (index < _crossoverWorkSigns.Count)
                _crossoverWorkSigns[index].Position = normal * (radius + 3.4f * GlobeRadius / 1024f);
            index++;
        }
    }
    public int CrossoverActiveSiteModelCount { get; private set; }

    // Strategic symbols use the same native metre scale and handedness as the block mirror.
    // Their visibility follows actual commissioned work, never a speculative button click.
    private void RebuildCrossoverSiteModels(CrossoverWork[] works)
    {
        if (_capitalRoot is null) return;
        if (_crossoverSiteRoot is not null) {
            _crossoverSiteRoot.GetParent().RemoveChild(_crossoverSiteRoot);
            _crossoverSiteRoot.QueueFree();
        }
        _crossoverSiteRoot = new Node3D { Name = "BtaCommissionedSites", Visible = !_flatMap };
        _capitalRoot.GetParent().AddChild(_crossoverSiteRoot);
        CrossoverActiveSiteModelCount = 0;
        _crossoverDisplayedWorks = works.Where(w => w.Active).Take(128).ToArray();
        foreach (var work in _crossoverDisplayedWorks) {
            static Vector3 ToGlobe(Vector3 p) => new(-p.Z, p.Y, -p.X);
            var nativeNormal = new Vector3(work.X, work.Y, work.Z).Normalized();
            if (nativeNormal.LengthSquared() < .5f) continue;
            var nativeEast = Vector3.Up.Cross(nativeNormal);
            if (nativeEast.LengthSquared() < .001f) nativeEast = Vector3.Right;
            nativeEast = nativeEast.Normalized();
            // Map all native marker axes; mirroring only the position rotates the model.
            var normal = ToGlobe(nativeNormal);
            var east = ToGlobe(nativeEast);
            var forward = ToGlobe(nativeNormal.Cross(nativeEast));
            if (!_crossoverSiteMeshes.TryGetValue(work.Building, out var mesh))
                _crossoverSiteMeshes[work.Building] = mesh = BuildCrossoverSiteMesh(work.Building);
            var site = new MeshInstance3D {
                Name = "BtaSite_" + CrossoverActiveSiteModelCount,
                Mesh = mesh,
                Transform = new Transform3D(new Basis(east, normal, forward), normal * CrossoverSiteRadius(work)),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            site.SetMeta("work_id", work.Id);
            site.SetMeta("building_kind", work.Building.ToString());
            site.SetMeta("native_position", new Vector3(work.X, work.Y, work.Z));
            site.SetMeta("display_radius", CrossoverSiteRadius(work));
            _crossoverSiteRoot.AddChild(site);
            CrossoverActiveSiteModelCount++;
        }
    }

    private ArrayMesh BuildCrossoverSiteMesh(BuildingKind kind)
    {
        using var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float scale = GlobeRadius / 1024f;
        void Box(Vector3 a, Vector3 b, Color color) {
            var vertices = new Vector3[8];
            for (int i = 0; i < 8; i++)
                vertices[i] = new Vector3((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z) * scale;
            int[][] faces = { new[] {0,2,3,1}, new[] {4,5,7,6}, new[] {0,4,6,2}, new[] {1,3,7,5}, new[] {0,1,5,4}, new[] {2,6,7,3} };
            foreach (var face in faces) {
                var normal = (vertices[face[1]] - vertices[face[0]]).Cross(vertices[face[2]] - vertices[face[0]]).Normalized();
                foreach (int index in new[] {0,1,2,0,2,3}) {
                    st.SetColor(color); st.SetNormal(normal); st.AddVertex(vertices[face[index]]);
                }
            }
        }
        var baseColor = new Color(.25f,.31f,.35f);
        var windows = new Color(.45f,.78f,.88f);
        Box(new(-1,0,-.9f),new(1,.35f,.9f),baseColor);
        if (kind == BuildingKind.Hospital) {
            Box(new(-.95f,.35f,-.7f),new(.95f,1.9f,.7f),new(.91f,.91f,.86f));
            Box(new(-1.03f,1.9f,-.78f),new(1.03f,2.08f,.78f),baseColor);
            var red = new Color(.94f,.26f,.25f);
            Box(new(-.16f,2.08f,-.12f),new(.16f,2.9f,.12f),red);
            Box(new(-.6f,2.35f,-.12f),new(.6f,2.65f,.12f),red);
            Box(new(-.65f,2.92f,-.13f),new(.65f,2.98f,.13f),red);
            Box(new(-.13f,2.92f,-.65f),new(.13f,2.98f,.65f),red);
            Box(new(-.12f,.8f,-.75f),new(.12f,1.65f,-.7f),red);
            Box(new(-.43f,1.1f,-.75f),new(.43f,1.35f,-.7f),red);
            foreach (float x in new[] {-.7f,.48f})
                Box(new(x,.75f,-.74f),new(x+.22f,1.55f,-.70f),windows);
        } else if (kind == BuildingKind.Infrastructure) {
            var crate = new Color(.69f,.46f,.25f);
            Box(new(-.87f,.35f,-.7f),new(-.06f,1.25f,.7f),crate);
            Box(new(.06f,.35f,-.7f),new(.87f,1.25f,.7f),crate);
            Box(new(-.4f,1.25f,-.5f),new(.4f,1.95f,.5f),crate);
            Box(new(-.89f,.7f,-.73f),new(.89f,.85f,.73f),baseColor);
            Box(new(.69f,1.25f,.15f),new(.79f,2.9f,.25f),windows);
            Box(new(.79f,2.2f,.15f),new(1.5f,2.8f,.25f),windows);
        } else {
            Box(new(-.8f,.35f,-.65f),new(.8f,1.5f,.65f),new(.68f,.38f,.23f));
            Box(new(-.92f,1.5f,-.78f),new(.92f,1.72f,.78f),baseColor);
            Box(new(.35f,1.72f,-.5f),new(.68f,3.05f,0),baseColor);
            Box(new(.25f,3.05f,-.6f),new(.78f,3.18f,.1f),new(.67f,.74f,.77f));
            foreach (float x in new[] {-.65f,-.35f,-.05f})
                Box(new(x,1.73f,-.5f),new(x+.13f,1.83f,.35f),new(1,.74f,.25f));
            Box(new(-.55f,.75f,-.69f),new(.15f,1.3f,-.65f),new(1,.74f,.25f));
            Box(new(-.35f,.35f,.65f),new(.35f,1.25f,.69f),windows);
        }
        var material = new StandardMaterial3D {
            VertexColorUseAsAlbedo = true,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        st.SetMaterial(material);
        return st.Commit();
    }
}
