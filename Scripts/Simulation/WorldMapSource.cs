using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CommunityToolkit.HighPerformance;

namespace DealOrForce.Simulation;

/// <summary>Authoritative external geography. No embedded legacy raster is read by the game.</summary>
public static class WorldMapSource
{
    public const int Width = 5760;
    public const int Height = 2880;
    public const float CellDegrees = 360f / Width;
    public static int CellCount => Width * Height;

    private static readonly object SyncLock = new();
    private static ushort[] _control;
    private static ushort[] _states;
    private static Dictionary<int, ushort> _disputes;

    public static ushort[] Control
    {
        get
        {
            var cached = Volatile.Read(ref _control);
            if (cached is not null) return cached;
            lock (SyncLock)
            {
                return GetOrLoadControlLocked();
            }
        }
    }

    public static ushort[] States
    {
        get
        {
            var cached = Volatile.Read(ref _states);
            if (cached is not null) return cached;
            lock (SyncLock)
            {
                return GetOrLoadStatesLocked();
            }
        }
    }

    /// <summary>2D row-major view over <see cref="Control"/> (<c>[row, col]</c>).</summary>
    public static ReadOnlySpan2D<ushort> ControlGrid => new(Control, Height, Width);

    /// <summary>2D row-major view over <see cref="States"/> (<c>[row, col]</c>).</summary>
    public static ReadOnlySpan2D<ushort> StatesGrid => new(States, Height, Width);

    internal static ushort[] PeekControlIfLoaded() => Volatile.Read(ref _control);
    internal static ushort[] PeekStatesIfLoaded() => Volatile.Read(ref _states);

    private static ushort[] GetOrLoadStatesLocked()
    {
        if (_states is not null) return _states;
        if (TerritorialTopology.TryReadBootRasterFromCache(Width, Height, loadControl: false, out var cachedStates, out var lakes))
        {
            TerritorialTopology.MarkPreRepaired(cachedStates);
            TerritorialTopology.SyncBootLakes(lakes);
            Volatile.Write(ref _states, cachedStates);
            return cachedStates;
        }
        if (TryLoadAndRepairBootPairLocked(loadControl: false, out var repaired)) return repaired;
        var raw = Read(MapRasterCatalog.SourceStatesPath, "source_states");
        Volatile.Write(ref _states, raw);
        return raw;
    }

    private static ushort[] GetOrLoadControlLocked()
    {
        if (_control is not null) return _control;
        if (TerritorialTopology.TryReadBootRasterFromCache(Width, Height, loadControl: true, out var cachedControl, out var lakes))
        {
            TerritorialTopology.MarkPreRepaired(cachedControl);
            TerritorialTopology.SyncBootLakes(lakes);
            Volatile.Write(ref _control, cachedControl);
            return cachedControl;
        }
        if (TryLoadAndRepairBootPairLocked(loadControl: true, out var repaired)) return repaired;
        var raw = Read(MapRasterCatalog.SourceControlPath, "source_control");
        Volatile.Write(ref _control, raw);
        return raw;
    }

    private static bool TryLoadAndRepairBootPairLocked(bool loadControl, out ushort[] requested)
    {
        requested = null;
        if (ModOverlayPolicy.IsActive ||
            string.IsNullOrWhiteSpace(MapRasterCatalog.SourceControlPath) || !File.Exists(MapRasterCatalog.SourceControlPath) ||
            string.IsNullOrWhiteSpace(MapRasterCatalog.SourceStatesPath) || !File.Exists(MapRasterCatalog.SourceStatesPath))
            return false;

        var control = _control ?? Read(MapRasterCatalog.SourceControlPath, "source_control");
        var states = _states ?? Read(MapRasterCatalog.SourceStatesPath, "source_states");
        if (!TerritorialTopology.IsPreRepaired(control) || !TerritorialTopology.IsPreRepaired(states))
            TerritorialTopology.RepairRawGridsForBoot(control, states, Width, Height);

        Volatile.Write(ref _control, control);
        Volatile.Write(ref _states, states);
        requested = loadControl ? control : states;
        return true;
    }

    private static ushort[] Read(string path, string layer)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidDataException($"Missing external world layer '{layer}'. Restore game/map_data or correct the mod's default.map; no compiled map fallback is used.");
        return SourceMapGrid.Load(path, Width, Height);
    }

    public static ushort DisputeAt(int cell)
    {
        var disputes = Volatile.Read(ref _disputes);
        if (disputes is null)
        {
            lock (SyncLock)
            {
                disputes = _disputes;
                if (disputes is null)
                {
                    var grid = Read(MapRasterCatalog.SourceDisputesPath, "source_disputes");
                    disputes = new Dictionary<int, ushort>();
                    for (var i = 0; i < grid.Length; i++)
                        if (grid[i] != 0) disputes[i] = grid[i];
                    Volatile.Write(ref _disputes, disputes);
                }
            }
        }
        return disputes.GetValueOrDefault(cell);
    }

    public static void ReleaseDecodedGrids()
    {
        lock (SyncLock)
        {
            Volatile.Write(ref _control, null);
            Volatile.Write(ref _states, null);
            Volatile.Write(ref _disputes, null);
        }
    }

    public static int CellIndex(int column, int row) =>
        ((column % Width + Width) % Width) + Math.Clamp(row, 0, Height - 1) * Width;

    public static int ColumnOf(int cell) => cell % Width;
    public static int RowOf(int cell) => cell / Width;
    public static float LatitudeOf(int row) => 90f - (row + .5f) * 180f / Height;
    public static float LongitudeOf(int column) => -180f + (column + .5f) * 360f / Width;
}
