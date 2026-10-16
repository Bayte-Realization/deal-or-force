using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using DealOrForce.Modding;
using DealOrForce.Simulation;
using System.Linq;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private byte[] _crossoverHydrology;
    private MultiMeshInstance3D _crossoverBuildings;
    private readonly List<Label3D> _crossoverConstructionSigns = new();
    private ImageTexture _crossoverExcavations;
    private readonly List<Label3D> _crossoverWorkSigns = new();
    private string _crossoverWorkSignature = "";
    public int CrossoverBlockCount { get; private set; }
    private bool _crossoverPresentationEnabled;

    public void SetCrossoverPresentation(bool enabled)
    {
        _crossoverPresentationEnabled = enabled;
        SetCrossoverAtmosphere(enabled);
        if (enabled) return;
        static void Remove(Node node)
        {
            if (!IsInstanceValid(node)) return;
            node.GetParent()?.RemoveChild(node);
            node.QueueFree();
        }
        Remove(_crossoverBuildings); _crossoverBuildings = null;
        Remove(_crossoverSiteRoot); _crossoverSiteRoot = null;
        foreach (var sign in _crossoverConstructionSigns.Concat(_crossoverWorkSigns)) Remove(sign);
        _crossoverConstructionSigns.Clear(); _crossoverWorkSigns.Clear();
        _crossoverDisplayedWorks = Array.Empty<CrossoverWork>();
        _crossoverSiteCellTops.Clear(); _crossoverSiteMeshes.Clear();
        _crossoverWorkSignature = "";
        CrossoverBlockCount = CrossoverActiveSiteModelCount = 0;
        if (IsInstanceValid(_globeMaterial)) {
            _globeMaterial.SetShaderParameter("crossover_excavations_active", 0f);
            _globeMaterial.SetShaderParameter("crossover_excavations", default(Variant));
        }
        _crossoverExcavations?.Dispose(); _crossoverExcavations = null;
        if (IsInstanceValid(_crossoverBake)) Remove(_crossoverBake.GetParent());
        _crossoverBake = null; _crossoverQuad = null; _crossoverBakeMaterial = null;
        _crossoverPixels = _crossoverGroundPixels = _crossoverTilePixels = _crossoverHydrology = null;
        _crossoverBakePending = false; _crossoverNextBake = _crossoverBakeFrame = 0;
    }

    private void UpdateCrossoverProjection()
    {
        if (!_crossoverPresentationEnabled) return;
        if (_crossoverBuildings is not null) _crossoverBuildings.Visible = !_flatMap;
        if (_crossoverSiteRoot is not null) _crossoverSiteRoot.Visible = !_flatMap;
        foreach (var sign in _crossoverConstructionSigns.Concat(_crossoverWorkSigns)) if (IsInstanceValid(sign)) {
            sign.Visible = !_flatMap;
            sign.PixelSize = .001f * Math.Clamp((ViewDistance-GlobeRadius)/3f,.18f,3f);
        }
    }

    public void PresentCrossoverWorks(CrossoverWork[] works)
    {
        if (!_crossoverPresentationEnabled || _capitalRoot is null) return;
        var signature=string.Join(";",works.Select(w => $"{w.Id}:{w.Active}:{w.Building}:{w.X:R}:{w.Y:R}:{w.Z:R}"));
        if(signature==_crossoverWorkSignature) return;
        _crossoverWorkSignature=signature;
        RebuildCrossoverSiteModels(works);
        foreach(var sign in _crossoverWorkSigns) sign.QueueFree();
        _crossoverWorkSigns.Clear();
        foreach(var work in works.Where(w => w.Active).Take(128)) {
            var normal=new Vector3(-work.Z,work.Y,-work.X).Normalized();
            var color=work.Building==BuildingKind.Hospital ? new Color(.95f,.35f,.35f) :
                work.Building==BuildingKind.Infrastructure ? new Color(.35f,.75f,1f) : new Color(1,.78f,.3f);
            var sign=new Label3D { Name="BtaStrategicSite", Text="BTA · "+BuildingCatalog.Get(work.Building).Name,
                FontSize=24, PixelSize=.0007f, Offset=new Vector2(0,44), Modulate=color, Billboard=BaseMaterial3D.BillboardModeEnum.Enabled,
                Position=normal*(CrossoverSiteRadius(work)+3.4f*GlobeRadius/1024f), NoDepthTest=false, Visible=!_flatMap };
            _capitalRoot.GetParent().AddChild(sign); _crossoverWorkSigns.Add(sign);
        }
    }

    // The same authored lake/river distance fields used by the map shader.
    public byte[] CaptureCrossoverHydrology()
    {
        if (!_crossoverPresentationEnabled) return null;
        if (_crossoverHydrology is not null) return _crossoverHydrology;
        if (_globeMaterial?.GetShaderParameter("hydrology_map").AsGodotObject() is not Texture2D texture) return null;
        using var image = texture.GetImage();
        if (image is null || image.IsEmpty() || image.GetWidth() < 512) return null;
        image.ClearMipmaps(); image.Convert(Image.Format.Rgb8);
        var pixels = image.GetData();
        var packet = new byte[8 + image.GetWidth() * image.GetHeight()];
        packet[0] = (byte)'D'; packet[1] = (byte)'F'; packet[2] = (byte)'H'; packet[3] = (byte)'1';
        BitConverter.TryWriteBytes(packet.AsSpan(4,2), (ushort)image.GetWidth());
        BitConverter.TryWriteBytes(packet.AsSpan(6,2), (ushort)image.GetHeight());
        var inset = _globeMaterial.GetShaderParameter("river_inset").AsSingle() * 16;
        for (var i = 0; i < packet.Length - 8; i++)
            packet[i+8] = pixels[i*3] < 128 ? (byte)2 : pixels[i*3+1] + inset < 128 ? (byte)3 : (byte)0;
        return _crossoverHydrology = packet;
    }

    // Rasterize the actual native cell footprint; removing an above-ground building
    // must never punch a hole through the globe below it.
    private static void StampCrossoverExcavation(byte[] damage, CrossoverVoxel edit)
    {
        if (edit.surface is not null) {
            CrossoverSurface.Stamp(damage,CrossoverWidth,CrossoverHeight,edit);
            return;
        }
        if (!Campaign.CrossoverSurfaceCell(edit)) return; // Buildings, towns and plants never remove ground.
        const float radius = 1023.4f;
        var minSquared = 0f; var maxSquared = 0f;
        foreach (var value in new[] { edit.x,edit.y,edit.z }) {
            var a = MathF.Abs(value); var b = MathF.Abs(value+1f);
            minSquared += MathF.Min(a*a,b*b); maxSquared += MathF.Max(a*a,b*b);
        }
        if (minSquared > radius*radius || maxSquared < radius*radius) return;
        var centre = new Vector3(edit.x+.5f,edit.y+.5f,edit.z+.5f);
        var seam = (MathF.Atan2(centre.Z,centre.X)+MathF.PI)/(MathF.PI*2);
        var minU = float.MaxValue; var maxU = float.MinValue;
        var minV = float.MaxValue; var maxV = float.MinValue;
        for (var corner = 0; corner < 8; corner++) {
            var p = new Vector3(edit.x+(corner&1),edit.y+((corner>>1)&1),edit.z+((corner>>2)&1));
            var u = (MathF.Atan2(p.Z,p.X)+MathF.PI)/(MathF.PI*2);
            u -= MathF.Round(u-seam);
            var v = .5f-MathF.Asin(Math.Clamp(p.Y/p.Length(),-1f,1f))/MathF.PI;
            minU = MathF.Min(minU,u); maxU = MathF.Max(maxU,u);
            minV = MathF.Min(minV,v); maxV = MathF.Max(maxV,v);
        }
        var lowY = Math.Clamp((int)(minV*CrossoverHeight)-1,0,CrossoverHeight-1);
        var highY = Math.Clamp((int)(maxV*CrossoverHeight)+1,0,CrossoverHeight-1);
        for (var y = lowY; y <= highY; y++) {
            var latitude = (.5f-(y+.5f)/CrossoverHeight)*MathF.PI;
            var horizontal = radius*MathF.Cos(latitude); var gy = radius*MathF.Sin(latitude);
            if ((int)MathF.Floor(gy) != edit.y) continue;
            for (var x = (int)(minU*CrossoverWidth)-1; x <= (int)(maxU*CrossoverWidth)+1; x++) {
                var longitude = ((x+.5f)/CrossoverWidth-.5f)*(MathF.PI*2);
                if ((int)MathF.Floor(horizontal*MathF.Cos(longitude)) != edit.x ||
                    (int)MathF.Floor(horizontal*MathF.Sin(longitude)) != edit.z) continue;
                damage[y*CrossoverWidth+(x%CrossoverWidth+CrossoverWidth)%CrossoverWidth] = 255;
            }
        }
    }

    // A bounded snapshot from the adapter; no networking thread touches Godot.
    public void PresentCrossoverVoxels(CrossoverVoxel[] edits)
    {
        if (!_crossoverPresentationEnabled || _capitalRoot is null || _globeMaterial is null) return;
        RefreshCrossoverSiteHeights(edits);
        if (_crossoverBuildings is null)
        {
            var material = new StandardMaterial3D {
                VertexColorUseAsAlbedo = true, Roughness = .9f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
            _crossoverBuildings = new MultiMeshInstance3D {
                Name = "BtaPlayerBuildings", Multimesh = new MultiMesh {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true,
                    Mesh = new BoxMesh { Size = Vector3.One * (GlobeRadius / 1024f), Material = material }
                }
            };
            _capitalRoot.GetParent().AddChild(_crossoverBuildings);
        }
        var visible = new List<CrossoverVoxel>();
        var clusters = new Dictionary<(int,int,int), CrossoverVoxel>();
        var damage = new byte[CrossoverWidth * CrossoverHeight];
        foreach (var edit in edits)
        {
            var native = new Vector3(edit.x+.5f, edit.y+.5f, edit.z+.5f);
            var radius = native.Length();
            if (edit.block > 0) {
                // Repaired ground is covered by the map again, just as in the guest.
                // A coplanar cube top would flicker against the globe's own surface.
                if (edit.surface is not null) continue;
                visible.Add(edit);
                if (radius >= 1023 && !edit.flora) clusters.TryAdd((edit.x >> 4,edit.y >> 4,edit.z >> 4),edit);
            }
            else StampCrossoverExcavation(damage,edit);
        }
        CrossoverBlockCount = visible.Count;
        var mesh = _crossoverBuildings.Multimesh;
        mesh.InstanceCount = visible.Count;
        for (var i = 0; i < visible.Count; i++)
        {
            var edit = visible[i];
            // DoF's map seam and handedness: native (x,y,z) maps to (-z,y,-x).
            var native = edit.pose is { Length: 7 } pose
                ? new Vector3(pose[0],pose[1],pose[2]) : new Vector3(edit.x+.5f,edit.y+.5f,edit.z+.5f);
            var p = new Vector3(-native.Z,native.Y,-native.X) * (GlobeRadius/1024f);
            var orientation = Basis.Identity;
            if (edit.pose is { Length: 7 } rotation) {
                var basis = new Basis(new Quaternion(rotation[3],rotation[4],rotation[5],rotation[6]).Normalized());
                Vector3 MapAxis(Vector3 axis) => new(-axis.Z,axis.Y,-axis.X);
                orientation = new Basis(MapAxis(basis.X),MapAxis(basis.Y),MapAxis(basis.Z));
            }
            mesh.SetInstanceTransform(i,new Transform3D(orientation,p));
            mesh.SetInstanceColor(i,new Color(((edit.color>>16)&255)/255f,((edit.color>>8)&255)/255f,(edit.color&255)/255f));
        }
        foreach (var sign in _crossoverConstructionSigns) sign.QueueFree();
        _crossoverConstructionSigns.Clear();
        foreach (var edit in clusters.Values)
        {
            if (_crossoverConstructionSigns.Count >= 128) break;
            var normal = new Vector3(-edit.z-.5f,edit.y+.5f,-edit.x-.5f).Normalized();
            var sign = new Label3D { Name = "BtaConstructionSite", Text = "▣", FontSize = 28,
                PixelSize = .001f, Modulate = new Color(1,.78f,.3f), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Position = normal*(GlobeRadius+.022f), NoDepthTest = false };
            _capitalRoot.GetParent().AddChild(sign); _crossoverConstructionSigns.Add(sign);
        }
        using var image = Image.CreateFromData(CrossoverWidth,CrossoverHeight,false,Image.Format.R8,damage);
        if (_crossoverExcavations is null) _crossoverExcavations = ImageTexture.CreateFromImage(image);
        else _crossoverExcavations.Update(image);
        _globeMaterial.SetShaderParameter("crossover_excavations",_crossoverExcavations);
        _globeMaterial.SetShaderParameter("crossover_excavations_active",1f);
    }
}
