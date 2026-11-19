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

            // Verify 100% Godot CW outward front-face winding: dot(cross(v1-v0, v2-v0), n0) <= 0
            var allCw = true;
            for (var t = 0; t < parsed.TriangleCount; t++)
            {
                var p = t * 9;
                var e1x = parsed.Positions[p + 3] - parsed.Positions[p];
                var e1y = parsed.Positions[p + 4] - parsed.Positions[p + 1];
                var e1z = parsed.Positions[p + 5] - parsed.Positions[p + 2];
                var e2x = parsed.Positions[p + 6] - parsed.Positions[p];
                var e2y = parsed.Positions[p + 7] - parsed.Positions[p + 1];
                var e2z = parsed.Positions[p + 8] - parsed.Positions[p + 2];
                var cx = e1y * e2z - e1z * e2y;
                var cy = e1z * e2x - e1x * e2z;
                var cz = e1x * e2y - e1y * e2x;
                var dot = cx * parsed.Normals[p] + cy * parsed.Normals[p + 1] + cz * parsed.Normals[p + 2];
                if (dot > 0f) { allCw = false; break; }
            }
            Check(allCw, modelName + " winds 100% of triangles clockwise outward for Godot cull_back");
        }

        // Verify upright column-basis orientation math across Northern/Southern & Eastern/Western hemispheres
        foreach (var (cityName, lat, lon) in new[] {
                     ("Warsaw", 52.23f, 21.01f),
                     ("Tokyo", 35.68f, 139.69f),
                     ("Sydney", -33.87f, 151.21f),
                     ("Buenos Aires", -34.60f, -58.38f) })
        {
            foreach (var bearing in new[] { 0f, 1.15f, 3.85f })
            {
                var (c0, c1, c2, normal) = CityMiniatureLayout.GlobeBasisColumns(lat, lon, bearing, 0.14f, 5f);
                // Column-vector basis multiplication: Basis(c0, c1, c2) * (0, 0, 1) == c2
                var c2Len = MathF.Sqrt(c2.X * c2.X + c2.Y * c2.Y + c2.Z * c2.Z);
                var upDotNormal = (c2.X * normal.X + c2.Y * normal.Y + c2.Z * normal.Z) / MathF.Max(c2Len, 1e-6f);
                var ortho0 = MathF.Abs(c0.X * normal.X + c0.Y * normal.Y + c0.Z * normal.Z);
                var ortho1 = MathF.Abs(c1.X * normal.X + c1.Y * normal.Y + c1.Z * normal.Z);
                // Positive right-handed determinant preserves CW triangle winding on the sphere
                var det = c0.X * (c1.Y * c2.Z - c1.Z * c2.Y)
                        - c1.X * (c0.Y * c2.Z - c0.Z * c2.Y)
                        + c2.X * (c0.Y * c1.Z - c0.Z * c1.Y);
                Check(upDotNormal > 0.9999f && ortho0 < 1e-4f && ortho1 < 1e-4f && det > 0f,
                    "upright right-handed column basis verified at " + cityName + " (bearing=" + bearing.ToString("F2") + ")");
            }
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

        // Verify Zipf regional rank prevents 14 small towns in one region from starving its primary city
        var denseCities = new List<CityDefinition>();
        for (var i = 0; i < 15; i++)
            denseCities.Add(new CityDefinition { Id = "town_" + i, Name = "Town " + i, Latitude = 48.0f + i * 0.55f, Longitude = 10.0f + (i % 4) * 2.5f });
        var denseAnchors = denseCities.Select(c => new CityCatalog.MapAnchor(
            CityCatalog.ProvinceUnder(map, c.Latitude, c.Longitude), c.Latitude, c.Longitude)).ToArray();
        var densePlaced = CityMiniatureLayout.BuildPlacements(
            denseCities, denseAnchors, new bool[15], new Dictionary<int, long> { [1] = 1_400_000 }, map, null);
        Check(densePlaced.Length is >= 2 and <= 5 && densePlaced.Any(p => p.CityIndex == 0),
            "Zipf regional urban distribution places primary regional cities even when 15 towns share a state (" + densePlaced.Length + " placed)");

        var rendererCode = File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(), "..", "Scripts", "gfx", "CityMiniatureRenderer.cs"));
        Check(rendererCode.Contains("public sealed class CityMiniatureRenderer") &&
              !rendererCode.Contains("partial class GlobePresentation") &&
              rendererCode.Contains("Task.Run(") &&
              !rendererCode.Contains(".Transposed()"),
            "CityMiniatureRenderer is a standalone cohesive class using direct column vectors on a worker thread");
    }
}
