using System;
using System.Buffers.Binary;
using System.IO;

namespace DealOrForce.Simulation;

/// <summary>
/// Source geography is data, not executable constants. Each lossless RGB PNG stores a
/// uint16 id as R + 256*G; B must be zero. Never use rendered/filtered Godot textures
/// for political ids. The former s&box tables remain a temporary compatibility fallback.
/// </summary>
public static class SourceMapGrid
{
    public static ushort[] Load(string path, int width, int height)
    {
        // Check dimensions before the PNG decoder allocates a mod-controlled raster.
        using (var stream = File.OpenRead(path))
        {
            Span<byte> header = stackalloc byte[24];
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32BigEndian(header[16..20]) != width ||
                BinaryPrimitives.ReadUInt32BigEndian(header[20..24]) != height)
                throw new InvalidDataException($"{Path.GetFileName(path)}: source layer must be {width}x{height}.");
        }
        var png = DataMapPng.LoadRgb(path);
        var ids = new ushort[checked(width * height)];
        for (var cell = 0; cell < ids.Length; cell++)
        {
            var offset = cell * 3;
            if (png.Pixels[offset + 2] != 0)
                throw new InvalidDataException($"{Path.GetFileName(path)}: B must be zero; unexpected colour at cell {cell}.");
            ids[cell] = (ushort)(png.Pixels[offset] | png.Pixels[offset + 1] << 8);
        }
        return ids;
    }

    public static ushort[] LoadOptional(string path, int width, int height) =>
        string.IsNullOrWhiteSpace(path) ? null : Load(path, width, height);
}
