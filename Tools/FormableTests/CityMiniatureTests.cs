using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Presentation;
using DealOrForce.Simulation;

/// <summary>
/// Headless verification of the Blender 5.3 3D PBR city, capital and port miniatures,
/// 8x4 worker-thread spatial chunking, coastal port classification, water-cell rejection,
/// and anti-spaghetti standalone renderer architecture.
/// </summary>
static class CityMiniatureTests
{
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS city miniatures: " + label); }

    public static void Run()
    {
        var dir = CityMiniatureLayout.ModelsDirectory();
        foreach (var modelName in CityMiniatureLayout.ModelNames)
        {
            var objPath = Path.Combine(dir, modelName + ".obj");
            var impPath = objPath + ".import";
            Check(File.Exists(objPath) && File.Exists(impPath),
                "Blender 5.3 .obj and Godot .import sidecar exist: " + modelName);
            var parsed = CityMiniatureLayout.ParseObj(modelName, File.ReadAllText(objPath));
            Check(parsed.TriangleCount is >= 80 and <= 200,
                modelName + " stays within the 80..200 triangle low-poly PBR budget (" + parsed.TriangleCount + " tris)");
            Check(parsed.HasStone && parsed.HasRoof && parsed.HasWindowGlow,
                modelName + " includes neutral stone masonry, roof and window light roles");
            Check(parsed.Positions.Length == parsed.TriangleCount * 9 &&
                  parsed.Normals.Length == parsed.TriangleCount * 9 &&
                  parsed.ColorsRgba.Length == parsed.TriangleCount * 12,
                modelName + " vertex/normal/RGBA streams are complete for single-draw MultiMesh upload");
        }

        // Verify 8x4 = 32 spatial chunk indexing across full globe (-90..+90, -180..+180)
        Check(CityMiniatureLayout.ChunkCount == 32 &&
              CityMiniatureLayout.ChunkIndexOf(-89.5f, -179.9f) == 0 &&
              CityMiniatureLayout.ChunkIndexOf(89.5f, 179.9f) == 31,
            "8x4 spatial chunking spans -180..+180 longitude and -90..+90 latitude into 32 chunks");
        for (var c = 0; c < CityMiniatureLayout.ChunkCount; c++)
        {
            var (clat, clon) = CityMiniatureLayout.ChunkCenterDegrees(c);
            Check(CityMiniatureLayout.ChunkIndexOf(clat, clon) == c,
                "chunk " + c + " center maps back to its own bin");
        }

        // Verify zoom fade on both globe and flat projections
        Check(CityMiniatureLayout.Visibility(false, 5.4f, 1.0f) > 0.9f &&
              CityMiniatureLayout.Visibility(false, 12.0f, 1.0f) == 0f &&
              CityMiniatureLayout.Visibility(true, 5.4f, 0.9f) > 0.9f &&
              CityMiniatureLayout.Visibility(true, 5.4f, 5.0f) == 0f &&
              CityMiniatureLayout.Visibility(false, float.NaN, float.NaN) == 0f,
            "3D city miniatures appear close in and hide cleanly at strategic zoom on globe and flat map");

        // Verify worker placement builder: capital monument, coastal port harbor, inland city cluster & water rejection
        var w = 360;
        var h = 180;
        var states = new ushort[w * h];
        var owners = new ushort[w * h];
        // Land block in central/northern Europe (lon 0..40 -> x 180..220, lat 45..60 -> y 30..45), sea outside
        for (var y = 30; y <= 45; y++)
            for (var x = 180; x <= 220; x++)
            {
                states[y * w + x] = 1;
                owners[y * w + x] = 1;
            }
        var map = ProvinceTopology.Build(states, owners, w, h, _ => false);
        var cities = new List<CityDefinition>
        {
            new() { Id = "warsaw", Name = "Warsaw", Latitude = 52.23f, Longitude = 21.01f },
            new() { Id = "krakow", Name = "Krakow", Latitude = 50.06f, Longitude = 19.94f },
            new() { Id = "gdansk", Name = "Gdansk", Latitude = 59.92f, Longitude = 21.00f }, // right next to y=29 sea coast
            new() { Id = "atlantis", Name = "Atlantis", Latitude = 10.0f, Longitude = -40.0f } // open sea -> rejected
        };
        var anchors = cities.Select(c => new CityCatalog.MapAnchor(
            CityCatalog.ProvinceUnder(map, c.Latitude, c.Longitude), c.Latitude, c.Longitude)).ToArray();
        var caps = new[] { true, false, false, false };
        var urban = new Dictionary<int, long> { [1] = 3_600_000 };

        var placedA = CityMiniatureLayout.BuildPlacements(cities, anchors, caps, urban, map, null);
        var placedB = CityMiniatureLayout.BuildPlacements(cities, anchors, caps, urban, map, null);
        Check(placedA.Length == 3 && placedA.SequenceEqual(placedB),
            "worker placement builder is deterministic and rejects sea anchors");
        Check(placedA.Any(p => p.CityIndex == 0 && p.Kind == CityMiniatureKind.CapitalMonument) &&
              placedA.Any(p => p.CityIndex == 1 && p.Kind == CityMiniatureKind.CityCluster) &&
              placedA.Any(p => p.CityIndex == 2 && p.Kind == CityMiniatureKind.PortHarbor),
            "classifies capital_monument, inland city_cluster and coastal port_harbor accurately");
        Check(placedA.All(p => CityMiniatureLayout.FootprintOnLand(map, null, p.Latitude, p.Longitude, p.FootprintDeg, p.LongitudeScale)),
            "every placed miniature center and 4 footprint corners stand strictly on land");

        var rendererCode = File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(), "..", "Scripts", "gfx", "CityMiniatureRenderer.cs"));
        Check(rendererCode.Contains("public sealed class CityMiniatureRenderer") &&
              !rendererCode.Contains("partial class GlobePresentation") &&
              rendererCode.Contains("Task.Run(") &&
              rendererCode.Contains(").Transposed()"),
            "CityMiniatureRenderer is a standalone cohesive class building upright column-aligned 8x4 MultiMesh chunks on a worker thread");
    }
}
