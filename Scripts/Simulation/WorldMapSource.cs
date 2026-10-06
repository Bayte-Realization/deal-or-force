using System;
using System.Collections.Generic;
using System.IO;

namespace DealOrForce.Simulation;

/// <summary>Authoritative external geography. No embedded legacy raster is read by the game.</summary>
public static class WorldMapSource
{
    public const int Width = 5760;
    public const int Height = 2880;
    public const float CellDegrees = 360f / Width;
    public static int CellCount => Width * Height;
    private static ushort[] _control;
    private static ushort[] _states;
    private static Dictionary<int, ushort> _disputes;
    public static ushort[] Control => _control ??= Read(MapRasterCatalog.SourceControlPath, "source_control");
    public static ushort[] States => _states ??= Read(MapRasterCatalog.SourceStatesPath, "source_states");
    private static ushort[] Read(string path, string layer)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidDataException($"Missing external world layer '{layer}'. Restore game/map_data or correct the mod's default.map; no compiled map fallback is used.");
        return SourceMapGrid.Load(path, Width, Height);
    }
    public static ushort DisputeAt(int cell)
    {
        if (_disputes is null)
        {
            var grid = Read(MapRasterCatalog.SourceDisputesPath, "source_disputes");
            _disputes = new();
            for (var i = 0; i < grid.Length; i++) if (grid[i] != 0) _disputes[i] = grid[i];
        }
        return _disputes.GetValueOrDefault(cell);
    }
    public static void ReleaseDecodedGrids() { _control = null; _states = null; _disputes = null; }
    public static int CellIndex(int column, int row) => ((column % Width + Width) % Width) + Math.Clamp(row, 0, Height - 1) * Width;
    public static int ColumnOf(int cell) => cell % Width;
    public static int RowOf(int cell) => cell / Width;
    public static float LatitudeOf(int row) => 90f - (row + .5f) * 180f / Height;
    public static float LongitudeOf(int column) => -180f + (column + .5f) * 360f / Width;
}
