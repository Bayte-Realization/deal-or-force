using Godot;
using System;
using System.IO;
using System.Text;

namespace DealOrForce;

/// <summary>Bakes the actual map material, rather than a Minecraft wool approximation.</summary>
public partial class GlobePresentation
{
    private SubViewport _crossoverBake;
    private MeshInstance3D _crossoverQuad;
    private ShaderMaterial _crossoverBakeMaterial;
    private byte[] _crossoverPixels;
    private ulong _crossoverBakeFrame;
    private ulong _crossoverNextBake;
    private bool _crossoverBakePending;
    private byte[] _crossoverGroundPixels, _crossoverTilePixels;
    private int _crossoverGroundWidth, _crossoverGroundHeight, _crossoverTileLayers;
    private const int CrossoverWidth = 5760, CrossoverHeight = 2880, CrossoverTileSide = 256;

    private void CaptureCrossoverGround()
    {
        if (_crossoverGroundPixels is not null) return;
        if (_globeMaterial.GetShaderParameter("terrain_surface").AsGodotObject() is not Texture2D ground ||
            _globeMaterial.GetShaderParameter("terrain_tiles").AsGodotObject() is not Texture2DArray tiles) return;
        using var map = ground.GetImage();
        if (map is null || map.IsEmpty()) return;
        map.ClearMipmaps(); map.Convert(Image.Format.R8);
        _crossoverGroundWidth = map.GetWidth(); _crossoverGroundHeight = map.GetHeight();
        _crossoverTileLayers = tiles.GetLayers();
        _crossoverTilePixels = new byte[CrossoverTileSide * CrossoverTileSide * 4 * _crossoverTileLayers];
        for (var layer = 0; layer < _crossoverTileLayers; layer++)
        {
            using var image = tiles.GetLayerData(layer);
            image.ClearMipmaps(); image.Convert(Image.Format.Rgba8);
            image.Resize(CrossoverTileSide, CrossoverTileSide, Image.Interpolation.Lanczos);
            var bytes = image.GetData();
            Buffer.BlockCopy(bytes, 0, _crossoverTilePixels, layer * bytes.Length, bytes.Length);
        }
        _crossoverGroundPixels = map.GetData();
    }

    // Main thread only; the HTTP adapter receives an immutable byte array.
    public byte[] CaptureCrossoverSurface()
    {
        if (!_crossoverPresentationEnabled || _globeMaterial is null || _campaign?.ProvinceMap is null) return null;
        const int width = CrossoverWidth, height = CrossoverHeight;
        CaptureCrossoverGround();
        if (_crossoverBake is null)
        {
            // Same fragment program, textures and live owner/front data as the strategy.
            // Only lighting is baked flat; the guest lights this surface around its planet.
            var code = _globeMaterial.Shader.Code.Replace(
                "render_mode diffuse_burley, cull_back;", "render_mode unshaded, cull_disabled;");
            _crossoverBakeMaterial = (ShaderMaterial)_globeMaterial.Duplicate();
            _crossoverBakeMaterial.Shader = new Shader { Code = code };
            _crossoverBake = new SubViewport {
                Name = "CrossoverSurfaceBake", Size = new Vector2I(width, height),
                OwnWorld3D = true, TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled
            };
            // SubViewportContainer displays direct SubViewport children. A plain host
            // keeps the export viewport entirely off screen, like GlobeViewportHost.
            var bakeHost = new Node { Name = "CrossoverBakeHost" };
            AddChild(bakeHost);
            bakeHost.AddChild(_crossoverBake);
            _crossoverQuad = new MeshInstance3D {
                Name = "MapAlbedo", Mesh = new QuadMesh { Size = new Vector2(2, 1) },
                MaterialOverride = _crossoverBakeMaterial
            };
            _crossoverBake.AddChild(_crossoverQuad);
            _crossoverBake.AddChild(new Camera3D {
                Name = "BakeCamera", Position = new Vector3(0, 0, 2),
                Projection = Camera3D.ProjectionType.Orthogonal, Size = 1,
                Current = true
            });
        }
        var frame = Engine.GetProcessFrames();
        if (_crossoverBakePending && frame >= _crossoverBakeFrame + 2)
        {
            using var image = _crossoverBake.GetTexture().GetImage();
            if (image is null || image.IsEmpty()) { _crossoverBakePending = false; return _crossoverPixels; }
            image.Convert(Image.Format.Rgba8);
            var pixels = image.GetData();
            var detailed = _crossoverGroundPixels is not null;
            var header = detailed ? 24 : 8;
            // One immutable packet allocation; static source tiles/maps are read only once.
            var packet = new byte[header + pixels.Length +
                (detailed ? _crossoverGroundPixels.Length + _crossoverTilePixels.Length : 0)];
            using var stream = new MemoryStream(packet, true);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
            writer.Write(Encoding.ASCII.GetBytes(detailed ? "DFT2" : "DFT1"));
            writer.Write((ushort)width); writer.Write((ushort)height);
            if (detailed)
            {
                writer.Write((ushort)_crossoverGroundWidth); writer.Write((ushort)_crossoverGroundHeight);
                writer.Write((ushort)CrossoverTileSide); writer.Write((ushort)_crossoverTileLayers);
                writer.Write(_globeMaterial.GetShaderParameter("terrain_detail").AsSingle());
                writer.Write(_globeMaterial.GetShaderParameter("terrain_repeat").AsSingle());
            }
            writer.Write(pixels);
            if (detailed) { writer.Write(_crossoverGroundPixels); writer.Write(_crossoverTilePixels); }
            _crossoverPixels = packet;
            _crossoverBakePending = false;
        }
        if (!_crossoverBakePending && frame >= _crossoverNextBake)
        {
            foreach (var info in _globeMaterial.Shader.GetShaderUniformList())
            {
                var name = info.AsGodotDictionary()["name"].AsString();
                _crossoverBakeMaterial.SetShaderParameter(name, _globeMaterial.GetShaderParameter(name));
            }
            // Selection/FoW belong to the strategy camera, not the visitor's position.
            _crossoverBakeMaterial.SetShaderParameter("fog_viewer", -1f);
            _crossoverBakeMaterial.SetShaderParameter("selection_active", 0f);
            _crossoverBakeMaterial.SetShaderParameter("highlighted_nation", -1f);
            // A visitor always needs crisp country/coast detail, independent of strategy zoom.
            // Fine repeating ground is rendered in the guest, without baking it into huge texels.
            _crossoverBakeMaterial.SetShaderParameter("detail_mix", 1f);
            // The guest cuts its smooth surface against exact edited native cells.
            _crossoverBakeMaterial.SetShaderParameter("crossover_excavations_active", 0f);
            _crossoverBakeMaterial.SetShaderParameter("province_detail", 0f);
            _crossoverBake.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            _crossoverBakeFrame = frame; _crossoverNextBake = frame + 300;
            _crossoverBakePending = true;
        }
        return _crossoverPixels;
    }
}
