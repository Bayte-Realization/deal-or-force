using System;
using System.IO;
using System.Text.RegularExpressions;
using DealOrForce;
using DealOrForce.Simulation;

static class GpuAtlasContractTests
{
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS GPU atlas contract: " + label); }
    public static void Run()
    {
        var source = CompatibilityMapShader.Source;
        Check(Regex.Matches(source, @"uniform\s+sampler\w+\s+").Count == CompatibilityMapShader.TextureUnits &&
            CompatibilityMapShader.TextureUnits <= 12, "compatibility atlas leaves four of the minimum sixteen units for engine internals");
        Check(!Regex.IsMatch(source, @"textureQueryLod\s*\(") && !source.Contains("sampler2DArray"),
            "fallback does not depend on high-end LOD queries or terrain arrays");
        foreach (var uniform in new[] { "province_owner_colors", "province_owner_ids", "province_control_colors", "fog_visibility", "map_mode_colors", "selected_region_members" })
            Check(source.Contains("texture(" + uniform + ","), "live gameplay texture is sampled: " + uniform);
        Check(source.Contains("texture(resource_colors, entry)"),
            "resource layer uses the province lookup, not geographic UV coordinates");
        var seam = File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(), "..", "Scripts", "gfx", "SeamSamplingShader.cs"));
        Check(!Regex.IsMatch(seam, @"textureQueryLod\s*\(") && seam.Contains("dFdx(texels)") && seam.Contains("dFdy(texels)"),
            "full atlas seam calculation also avoids the reproduced unsupported call");
        var globe = File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(), "..", "Scripts", "gfx", "GlobePresentation.cs"));
        var start = globe.IndexOf("Code = @\"shader_type spatial;", StringComparison.Ordinal) + "Code = @\"".Length;
        var end = globe.IndexOf("\n}\"", start, StringComparison.Ordinal);
        Check(start > 0 && end > start, "full shader source is located, not replaced by a test-only approximation");
        var full = globe[start..(end + 2)];
        var packed = PackedMapShader.Build(full);
        Check(Regex.Matches(packed, @"uniform\s+sampler\w+\s+").Count == PackedMapShader.TextureUnits,
            "full Compatibility material fits twelve bindings without removing visual features");
        Check(packed.Contains("terrain_tiles") && packed.Contains("terrain_coverage") && packed.Contains("relief_height") &&
            packed.Contains("river_contour") && packed.Contains("ocean_depth") && packed.Contains("water_ripple_strength"),
            "terrain, smoothed boundaries, relief, rivers, sea depth and water motion remain in the packed shader");
        Check(PackedMapShader.Lookups.Count == 12 && PackedMapShader.Ids.Count == 3 &&
            PackedMapShader.Layer(PackedMapShader.Lookups, "fog_visibility") == 11 &&
            PackedMapShader.Layer(PackedMapShader.Lookups, "unknown") == -1,
            "upload and sampling share one explicit stable layer layout");
        Check(!packed.Contains("sampler2D ids") && !packed.Contains("texture(map_texture,") &&
            !Regex.IsMatch(packed, @"textureSize\(\s*(nation_ids|province_ids|region_ids)"),
            "generic ID helpers and dimension queries address the same lossless array");
        foreach (var name in PackedMapShader.Lookups)
            Check(!Regex.IsMatch(packed, @"texture\(\s*" + name + @"\s*,"), "no obsolete standalone lookup sample: " + name);
        var rejected = false;
        try { PackedMapShader.Build(full.Replace("uniform sampler2D province_ids", "uniform sampler2D missing_ids")); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "unexpected shader contract fails loudly instead of silently freezing layers");
        Check(QualityProfiles.For("auto", "NVIDIA GeForce RTX 3060", "gl_compatibility").Id == QualityProfiles.Ladder[0].Id &&
            QualityProfiles.For("auto", "Intel HD Graphics 530", "gl_compatibility").CityLabels,
            "Compatibility is not mistaken for a mandatory low-quality preset");
    }
}
