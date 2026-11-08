using System;
using System.Linq;
using System.Text;
using Godot;

namespace DealOrForce;

/// <summary>
/// Where the game's memory is, in one text: the process, the managed heap, the renderer's
/// counters and the largest textures by name.
///
/// The bench measures the simulation and found its world at under two hundred megabytes of
/// managed heap; a player's game holds four and a half gigabytes. Whatever holds the rest is
/// on this side of the engine - textures, meshes, nodes - and a counter that says only "video
/// memory" cannot say which of them. Reachable from the developer console (`memory`) and, for
/// tools, as a method on the scene root of both the front door and the campaign.
/// </summary>
public static class MemoryReport
{
    /// <summary>What a texture of this shape and format occupies, mipmaps included.</summary>
    public static long Size(int width, int height, int layers, Image.Format format, bool mipmaps)
    {
        var bits = format switch
        {
            Image.Format.L8 or Image.Format.R8 => 8,
            Image.Format.La8 or Image.Format.Rg8 or Image.Format.Rh or Image.Format.Rgba4444 or Image.Format.Rgb565 => 16,
            Image.Format.Rgb8 => 24,
            Image.Format.Rgba8 or Image.Format.Rf or Image.Format.Rgh or Image.Format.Rgbe9995 => 32,
            Image.Format.Rgbh => 48,
            Image.Format.Rgf or Image.Format.Rgbah => 64,
            Image.Format.Rgbf => 96,
            Image.Format.Rgbaf => 128,
            Image.Format.Dxt1 or Image.Format.RgtcR or Image.Format.Etc or Image.Format.Etc2R11 or Image.Format.Etc2Rgb8 => 4,
            _ => 8 // the block-compressed rest: DXT3/5, BPTC, RGTC RG, ASTC 4x4
        };
        var bytes = (long)Math.Max(1, width) * Math.Max(1, height) * Math.Max(1, layers) * bits / 8;
        return mipmaps ? bytes * 4 / 3 : bytes;
    }

    public static string Build(int top = 25)
    {
        var text = new StringBuilder();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        static string Mb(double bytes) => (bytes / 1048576.0).ToString("0.0") + " MB";
        text.AppendLine($"process: private {Mb(process.PrivateMemorySize64)}, working set {Mb(process.WorkingSet64)}");
        text.AppendLine($"managed heap {Mb(GC.GetTotalMemory(false))} (collections {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}); " +
                        $"engine static {Mb(OS.GetStaticMemoryUsage())}");
        text.AppendLine($"renderer: video {Mb(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed))}, " +
                        $"textures {Mb(Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed))}, " +
                        $"buffers {Mb(Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed))}");
        text.AppendLine($"objects {Performance.GetMonitor(Performance.Monitor.ObjectCount):0}, resources {Performance.GetMonitor(Performance.Monitor.ObjectResourceCount):0}, " +
                        $"nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):0}, orphan nodes {Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount):0}");
        // Every texture a material in the scene holds, once, sized by its format: the map's own
        // rasters are uniforms of its shaders, and they are where the gigabytes are.
        var seen = new System.Collections.Generic.Dictionary<ulong, (long Bytes, string Line)>();
        void Consider(Texture texture, string where)
        {
            if (texture is null) return;
            var rid = texture.GetRid().Id;
            if (seen.ContainsKey(rid)) return;
            long bytes; string shape; string format = "";
            switch (texture)
            {
                case Texture2D flat:
                    var formatOf = flat is ImageTexture imageTexture ? imageTexture.GetFormat() :
                        flat is CompressedTexture2D compressed ? compressed.GetFormat() : Image.Format.Rgba8;
                    format = formatOf.ToString();
                    bytes = Size(flat.GetWidth(), flat.GetHeight(), 1, formatOf, flat is ImageTexture { } withMips && withMips.GetImage()?.HasMipmaps() == true);
                    shape = $"{flat.GetWidth()}x{flat.GetHeight()}";
                    break;
                case TextureLayered layered:
                    format = layered.GetFormat().ToString();
                    bytes = Size(layered.GetWidth(), layered.GetHeight(), layered.GetLayers(), layered.GetFormat(), layered.HasMipmaps());
                    shape = $"{layered.GetWidth()}x{layered.GetHeight()}x{layered.GetLayers()}";
                    break;
                case Texture3D volume:
                    format = volume.GetFormat().ToString();
                    bytes = Size(volume.GetWidth(), volume.GetHeight(), volume.GetDepth(), volume.GetFormat(), volume.HasMipmaps());
                    shape = $"{volume.GetWidth()}x{volume.GetHeight()}x{volume.GetDepth()}";
                    break;
                default: return;
            }
            var name = string.IsNullOrEmpty(texture.ResourcePath) ? where : texture.ResourcePath;
            seen[rid] = (bytes, $"{Mb(bytes),10}  {shape}  {format}  {name}");
        }
        void ConsiderMaterial(Material material, string where)
        {
            if (material is ShaderMaterial shaded && shaded.Shader is { } shader)
                foreach (var uniform in shader.GetShaderUniformList())
                {
                    var name = uniform.AsGodotDictionary()["name"].AsString();
                    if (shaded.GetShaderParameter(name).Obj is Texture texture) Consider(texture, $"{where}:{name}");
                }
            else if (material is BaseMaterial3D standard) Consider(standard.AlbedoTexture, $"{where}:albedo");
        }
        void Walk(Node node)
        {
            var where = node.Name.ToString();
            if (node is CanvasItem canvas) ConsiderMaterial(canvas.Material, where);
            if (node is GeometryInstance3D geometry)
            {
                ConsiderMaterial(geometry.MaterialOverride, where);
                if (geometry is MeshInstance3D meshed && meshed.Mesh is { } mesh)
                    for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                        ConsiderMaterial(meshed.GetActiveMaterial(surface), where);
            }
            if (node is TextureRect rect) Consider(rect.Texture, where);
            if (node is Sprite2D sprite) Consider(sprite.Texture, where);
            foreach (var child in node.GetChildren()) Walk(child);
        }
        if (Engine.GetMainLoop() is SceneTree tree) Walk(tree.Root);
        var total = seen.Values.Sum(row => row.Bytes);
        text.AppendLine($"textures held by the scene's materials: {seen.Count} totalling {Mb(total)}; largest:");
        foreach (var row in seen.Values.OrderByDescending(row => row.Bytes).Take(Math.Max(1, top))) text.AppendLine(row.Line);
        return text.ToString();
    }
}
